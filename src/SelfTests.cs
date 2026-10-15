using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace OwnDesk
{
    public static class SelfTests
    {
        static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
        static void Reject(Action action)
        {
            bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Expected rejection");
        }
        public static int Run(string output)
        {
            var checks = new List<object>(); int failed = 0;
            string root = Path.Combine(Path.GetDirectoryName(output), "self-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Action<string, Action> test = (name, action) =>
            {
                try { action(); checks.Add(new { name = name, passed = true, error = "" }); }
                catch (Exception ex) { failed++; checks.Add(new { name = name, passed = false, error = ex.GetType().Name + ": " + ex.Message }); }
            };
            test("Private IPv4 validation, CIDR boundaries and custom ports", () =>
            {
                Assert(Endpoint.Parse("100.064.010.020:21118").Address == "100.64.10.20", "Canonical IPv4");
                Assert(Endpoint.Parse("100.127.255.254:22222").Port == 22222, "Custom port");
                Assert(!Endpoint.Parse("192.168.1.20").IsTailnet, "LAN classified");
                foreach (string value in new[] { "100.63.1.1", "100.128.1.1", "8.8.8.8", "127.0.0.1", "100.70.1.1:0", "100.70.1.1:65536", "100.999.1.1", "--password x", "100.70.1.1 & calc.exe", "100.70.1.1\n--password", "::1" }) Reject(() => Endpoint.Parse(value));
            });
            test("Device persistence, editing and duplicate rejection", () =>
            {
                var store = new DeviceStore(Path.Combine(root, "devices"));
                var device = new Device { Name = "测试电脑", Address = "100.70.10.20" }; store.Upsert(device);
                device.Name = "测试电脑 2"; store.Upsert(device);
                Assert(new DeviceStore(store.Root).Data.Devices.Single().Name == "测试电脑 2", "Saved edit");
                Reject(() => store.Upsert(new Device { Name = "Duplicate", Address = device.Address }));
                Assert(store.Data.Devices.Count == 1, "Duplicate unchanged");
                store.Remove(device.Id); Assert(new DeviceStore(store.Root).Data.Devices.Count == 0, "Removal saved");
            });
            test("Local names persist and travel in pairing codes without changing device identity", () =>
            {
                string dir = Path.Combine(root, "local-name");
                Assert(DeviceNames.Local(dir) == Environment.MachineName, "Old installations retain default name");
                DeviceNames.SaveLocal(dir, "  老婆笔记本  ");
                Assert(DeviceNames.Local(dir) == "老婆笔记本", "Chinese name saved and trimmed");
                var paired = PairCode.Read(PairCode.Create(DeviceNames.Local(dir), "100.70.1.2", "FixtureSecret_123456789"));
                Assert(paired.Name == "老婆笔记本" && paired.Address == "100.70.1.2", "Pairing carries friendly name and original address");
                foreach (string bad in new[] { "", "  ", new string('x', 49), "name\nline" }) Reject(() => DeviceNames.SaveLocal(dir, bad));
                Assert(DeviceNames.Local(dir) == "老婆笔记本", "Rejected rename preserves saved name");
            });
            test("Renaming a saved computer preserves pairing credentials, address and history", () =>
            {
                var store = new DeviceStore(Path.Combine(root, "rename"));
                var original = new Device { Name = "Old name", Address = "100.70.1.3", ProtectedSecret = SecretStore.Protect("FixtureSecret_123456789"), LastOpenedUtc = "2026-01-01T00:00:00Z" };
                store.Upsert(original); DeviceNames.Rename(store, original, "家里台式机");
                var renamed = new DeviceStore(store.Root).Data.Devices.Single();
                Assert(renamed.Name == "家里台式机" && renamed.Id == original.Id && renamed.Address == original.Address && renamed.ProtectedSecret == original.ProtectedSecret && renamed.LastOpenedUtc == original.LastOpenedUtc, "Only display name changes");
            });
            test("Automatic discovery merges online Windows peers and preserves names, pairing and exclusions", () =>
            {
                string dir = Path.Combine(root, "automatic-discovery"); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "devices.json"), "{\"Schema\":1,\"Profile\":\"office\",\"Devices\":[]}");
                var store = new DeviceStore(dir); Assert(store.Data.AutoDiscover, "Old device files enable discovery by default");
                var saved = new Device { Name = "家里台式机", Address = "100.70.1.2", ProtectedSecret = SecretStore.Protect("FixtureSecret_123456789") }; store.Upsert(saved);
                var state = new TailState { Backend = "Running", IPv4 = "100.70.1.1", Peers = new List<TailPeer> {
                    new TailPeer { Name = "Self", Address = "100.70.1.1", OS = "windows", Online = true },
                    new TailPeer { Name = "System name", Address = saved.Address, OS = "windows", Online = true },
                    new TailPeer { Name = "老婆笔记本", Address = "100.70.1.3", OS = "windows", Online = true },
                    new TailPeer { Name = "Old offline PC", Address = "100.70.1.4", OS = "windows", Online = false },
                    new TailPeer { Name = "Phone", Address = "100.70.1.5", OS = "android", Online = true },
                    new TailPeer { Name = "External", Address = "8.8.8.8", OS = "windows", Online = true } } };
                Assert(store.DiscoverOnline(state) == 1, "Only new online Windows peer added");
                var known = store.Data.Devices.Single(x => x.Id == saved.Id);
                Assert(known.Name == saved.Name && known.ProtectedSecret == saved.ProtectedSecret, "Nickname and credentials preserved");
                Assert(string.IsNullOrEmpty(store.Data.Devices.Single(x => x.Address == "100.70.1.3").ProtectedSecret), "Discovery grants no control credentials");
                string file = Path.Combine(store.Root, "devices.json"); string hash = AppInfo.FileHash(file);
                Assert(store.DiscoverOnline(state) == 0 && AppInfo.FileHash(file) == hash, "Repeated refresh does not rewrite data");
                var discovered = store.Data.Devices.Single(x => x.Address == "100.70.1.3"); store.Remove(discovered.Id);
                Assert(new DeviceStore(store.Root).DiscoverOnline(state) == 0, "Removed peer stays hidden after restart");
                store.Upsert(discovered); Assert(!store.Data.HiddenAutoAddresses.Contains(discovered.Address), "Explicit add unhides peer");
                var next = store.Clone(); next.AutoDiscover = false; store.Commit(next);
                state.Peers.Add(new TailPeer { Name = "Extra PC", Address = "100.70.1.6", OS = "windows", Online = true });
                Assert(new DeviceStore(store.Root).DiscoverOnline(state) == 0, "Discovery preference persists");
            });
            test("Import validates all records before modifying local devices", () =>
            {
                var store = new DeviceStore(Path.Combine(root, "imports")); store.Upsert(new Device { Name = "Existing", Address = "100.70.20.20" });
                string file = Path.Combine(root, "invalid-import.json"); var data = new DeviceFile();
                data.Devices.Add(new Device { Name = "Valid", Address = "100.70.20.21" }); data.Devices.Add(new Device { Name = "Invalid", Address = "8.8.8.8" });
                AppInfo.AtomicWrite(file, AppInfo.Json().Serialize(data)); Reject(() => store.Import(file));
                Assert(new DeviceStore(store.Root).Data.Devices.Count == 1, "Invalid import atomic");
                data.Devices.RemoveAt(1); data.Devices.Add(new Device { Name = "Already known", Address = "100.70.20.20" }); AppInfo.AtomicWrite(file, AppInfo.Json().Serialize(data));
                Assert(store.Import(file) == 1 && store.Data.Devices.Count == 2, "Merge skips duplicates");
            });
            test("Corrupt device file remains intact for recovery", () =>
            {
                string dir = Path.Combine(root, "corrupt"); Directory.CreateDirectory(dir); string path = Path.Combine(dir, "devices.json"); File.WriteAllText(path, "{broken");
                var store = new DeviceStore(dir); Assert(store.LoadNotice != null && File.ReadAllText(path) == "{broken", "Corruption not replaced on load");
                store.Upsert(new Device { Name = "Fresh", Address = "100.80.20.20" }); Assert(File.ReadAllText(path + ".bak") == "{broken", "Old data backed up before save");
            });
            test("Export contains device metadata and no connection credentials", () =>
            {
                var store = new DeviceStore(Path.Combine(root, "exports")); store.Upsert(new Device { Name = "Export", Address = "100.80.20.30", LastOpenedUtc = DateTime.UtcNow.ToString("o") });
                string path = Path.Combine(root, "export.json"); store.Export(path); string json = File.ReadAllText(path);
                Assert(!json.ToLowerInvariant().Contains("password") && !json.ToLowerInvariant().Contains("token"), "No credentials");
                Assert(DeviceStore.Read(path).Devices.Single().LastOpenedUtc == "", "No connection history exported");
            });
            test("Tailscale peer parsing handles Windows, offline, null and IPv6 peers", () =>
            {
                string json = "{\"BackendState\":\"Running\",\"TailscaleIPs\":[\"100.70.1.1\",\"fd7a:115c:a1e0::1\"],\"Peer\":{\"a\":{\"HostName\":\"PC\",\"OS\":\"windows\",\"Online\":true,\"TailscaleIPs\":[\"100.70.1.2\"]},\"b\":{\"HostName\":\"Offline\",\"OS\":\"windows\",\"Online\":false,\"TailscaleIPs\":[\"100.70.1.3\"]},\"c\":null}}";
                var state = TailscaleClient.Parse(json); Assert(state.Ready && state.Peers.Count == 2 && state.Peers[0].Online, "Real CLI shape");
                Assert(!TailscaleClient.Parse("{\"BackendState\":\"NeedsLogin\",\"Peer\":null}").Ready, "Login required");
                Assert(TailscaleClient.Parse("{\"BackendState\":\"Running\",\"TailscaleIPs\":[\"100.70.1.1\"],\"Peer\":null}").Peers.Count == 0, "No peers");
            });
            test("TOML patches preserve unrelated security fields and tables", () =>
            {
                string before = "rendezvous_server = 'unchanged'\n[options]\nwhitelist = '100.70.0.0/16'\ncustom-key = 'keep-me' # comment\n[socks]\nusername = 'local-test'\n";
                string after = TomlOptions.Patch(before, new Dictionary<string, string> { { "direct-server", "Y" }, { "whitelist", "100.64.0.0/10" } });
                Assert(after.Contains("rendezvous_server = 'unchanged'") && after.Contains("custom-key = 'keep-me' # comment") && after.Contains("[socks]\r\nusername = 'local-test'"), "Unrelated content preserved");
                Assert(after.IndexOf("direct-server") < after.IndexOf("[socks]"), "Key stays in options table");
                AppInfo.AtomicWrite(Path.Combine(root, "patched.toml"), after);
                Reject(() => TomlOptions.Patch("[options]\na='b'\n[options]\nc='d'", new Dictionary<string, string>()));
                Reject(() => TomlOptions.Patch("[options]\na='b'\na='d'", new Dictionary<string, string>()));
                Reject(() => TomlOptions.Patch("a='''multiline'''", new Dictionary<string, string>()));
            });
            test("HD profile uses exact engine source keys, host whitelist and reversible backup", () =>
            {
                string cfg = Path.Combine(root, "config"), data = Path.Combine(root, "config-data"); Directory.CreateDirectory(cfg);
                string original = "rendezvous_server = 'keep'\n[options]\ncustom-key = 'unchanged'\n"; File.WriteAllText(Path.Combine(cfg, "RustDesk2.toml"), original, new UTF8Encoding(false));
                var manager = new ConfigurationManager(cfg, data); manager.Apply(QualityProfile.Get("office"), true);
                string host = File.ReadAllText(Path.Combine(cfg, "RustDesk2.toml")); string defaults = File.ReadAllText(Path.Combine(cfg, "RustDesk_default.toml"));
                Assert(host.Contains("direct-server = 'Y'") && host.Contains("whitelist = '100.64.0.0/10'"), "IP access scoped");
                Assert(defaults.Contains("image_quality = 'custom'") && defaults.Contains("custom_image_quality = '90'") && defaults.Contains("custom-fps = '30'") && defaults.Contains("view_style = 'original'"), "HD keys are actual RustDesk_default keys");
                File.Copy(Path.Combine(cfg, "RustDesk_default.toml"), Path.Combine(root, "defaults.toml"));
                manager.Restore(); Assert(File.ReadAllText(Path.Combine(cfg, "RustDesk2.toml")) == original, "Original config restored");
                Assert(!File.Exists(Path.Combine(cfg, "RustDesk_default.toml")), "New file removed during restore");
            });
            test("Restore refuses to overwrite configuration changed later", () =>
            {
                string cfg = Path.Combine(root, "restore-guard"), data = Path.Combine(root, "restore-data"); var manager = new ConfigurationManager(cfg, data);
                manager.Apply(QualityProfile.Get("smooth"), true); string path = Path.Combine(cfg, "RustDesk2.toml"); File.AppendAllText(path, "# manual edit\n"); string current = File.ReadAllText(path);
                Reject(() => manager.Restore()); Assert(File.ReadAllText(path) == current, "Later changes preserved");
            });
            test("Backup journal cannot select arbitrary paths", () =>
            {
                string cfg = Path.Combine(root, "journal-guard"), data = Path.Combine(root, "journal-data"); Directory.CreateDirectory(data);
                var journal = new ConfigChange { BackupId = "..", Entries = new List<ConfigEntry>() }; AppInfo.AtomicWrite(Path.Combine(data, "last-config-change.json"), AppInfo.Json().Serialize(journal));
                Reject(() => new ConfigurationManager(cfg, data).Restore());
                journal.BackupId = "valid"; journal.Entries.Add(new ConfigEntry { Name = "..\\outside.txt" }); AppInfo.AtomicWrite(Path.Combine(data, "last-config-change.json"), AppInfo.Json().Serialize(journal));
                Reject(() => new ConfigurationManager(cfg, data).Restore());
            });
            test("TCP detection distinguishes listening and closed local test ports", () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                try { Assert(Connectivity.Probe("127.0.0.1", port, 1500).GetAwaiter().GetResult(), "Listener reachable"); }
                finally { listener.Stop(); }
                Assert(!Connectivity.Probe("127.0.0.1", port, 1500).GetAwaiter().GetResult(), "Closed port reported");
            });
            test("Connection launch bypasses command shell and passes no password", () =>
            {
                var engine = new EngineManager(Path.Combine(root, "path with spaces")); var info = engine.ConnectionInfo(Endpoint.Parse("100.70.1.2:21119"), false);
                Assert(!info.UseShellExecute && info.Arguments == "--connect \"100.70.1.2:21119\"", "Direct CLI argument");
                Assert(!info.Arguments.Contains("password"), "Password never in process arguments");
                Assert(engine.ConnectionInfo(Endpoint.Parse("100.70.1.2"), true).Arguments.StartsWith("--file-transfer "), "File transfer launch");
                Assert(ProcessTools.Quote("C:\\folder with spaces\\") == "\"C:\\folder with spaces\\\\\"", "Windows trailing slash quoting");
            });
            test("Bundled official engine digest matches pinned release", () => new EngineManager(AppDomain.CurrentDomain.BaseDirectory).Verify());
            UpdateTests.Register(test, root);
            ProcessRunnerTests.Register(test, root);
            object live;
            try { var client = new TailscaleClient(); var state = client.Status().GetAwaiter().GetResult(); live = new { installed = client.Executable != null, backend = state.Backend, ipv4Available = state.IPv4.Length > 0, peers = state.Peers.Count, windowsPeers = state.Peers.Count(x => string.Equals(x.OS, "windows", StringComparison.OrdinalIgnoreCase)) }; }
            catch (Exception ex) { live = new { error = ex.Message }; }
            var result = new { product = "OwnDesk", version = AppInfo.Version, utc = DateTime.UtcNow.ToString("o"), passed = checks.Count - failed, failed = failed, checks = checks, liveTailscaleReadOnly = live, fixtureDirectory = root, remoteSessionVerified = false, hdPerformanceVerified = false };
            AppInfo.AtomicWrite(output, AppInfo.Json().Serialize(result)); return failed == 0 ? 0 : 1;
        }
    }
}
