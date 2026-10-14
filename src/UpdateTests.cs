using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OwnDesk
{
    public static class UpdateTests
    {
        static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
        static void Reject(Action action)
        {
            bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } catch (CryptographicException) { rejected = true; } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Expected invalid update to be rejected");
        }
        static byte[] Sign(UpdateRelease release, RSACryptoServiceProvider key)
        {
            byte[] payload = Encoding.UTF8.GetBytes(AppInfo.Json().Serialize(release));
            return Encoding.UTF8.GetBytes(AppInfo.Json().Serialize(new UpdateEnvelope { Schema = 1, Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256"))) }));
        }
        public static void Register(Action<string, Action> test, string root)
        {
            string fixture = Path.Combine(root, "updates"); Directory.CreateDirectory(fixture);
            string archive = Path.Combine(fixture, "release.zip");
            var files = new List<UpdateFile>();
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (string name in new[] { "OwnDesk.exe", "OwnDesk.Update.exe" })
                {
                    string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name); byte[] bytes = File.ReadAllBytes(source);
                    var entry = zip.CreateEntry(name); using (var output = entry.Open()) output.Write(bytes, 0, bytes.Length);
                    files.Add(new UpdateFile { Path = name, Size = bytes.Length, Sha256 = AppInfo.Hash(bytes) });
                }
            var release = new UpdateRelease { Schema = 1, Product = "OwnDesk", Version = AppInfo.Version, Architecture = "x64", Channel = "stable", PackageUrl = "https://example.invalid/update.zip", Size = new FileInfo(archive).Length, Sha256 = AppInfo.FileHash(archive), Files = files, Notes = "Local fixtures only" };
            string stage = Path.Combine(fixture, "stage");
            using (var key = new RSACryptoServiceProvider(2048))
            {
                key.PersistKeyInCsp = false; string publicKey = key.ToXmlString(false);
                test("Signed update verifies publisher key and detects altered payload/signature", () =>
                {
                    byte[] signed = Sign(release, key); var verified = UpdatePolicy.Verify(signed, publicKey); Assert(verified.Version == AppInfo.Version, "Signed release accepted");
                    var envelope = AppInfo.Json().Deserialize<UpdateEnvelope>(Encoding.UTF8.GetString(signed)); envelope.Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
                    Reject(() => UpdatePolicy.Verify(Encoding.UTF8.GetBytes(AppInfo.Json().Serialize(envelope)), publicKey));
                    using (var other = new RSACryptoServiceProvider(2048)) { other.PersistKeyInCsp = false; Reject(() => UpdatePolicy.Verify(signed, other.ToXmlString(false))); }
                });
                test("Authenticated manifest still enforces product, version and file allowlist", () =>
                {
                    release.Product = "OtherApp"; Reject(() => UpdatePolicy.Verify(Sign(release, key), publicKey)); release.Product = "OwnDesk";
                    release.Version = "0.2.0;calc"; Reject(() => UpdatePolicy.Verify(Sign(release, key), publicKey)); release.Version = AppInfo.Version;
                    release.Files.Add(new UpdateFile { Path = "engine/rustdesk.exe", Size = 1, Sha256 = new string('a', 64) }); Reject(() => UpdatePolicy.Verify(Sign(release, key), publicKey)); release.Files.RemoveAt(release.Files.Count - 1);
                    Assert(UpdatePolicy.VersionOf("0.2.0") > UpdatePolicy.VersionOf("0.1.9"), "Numeric version comparison");
                });
            }
            test("Downloaded ZIP verifies digest and every extracted executable", () =>
            {
                UpdatePackage.Extract(archive, stage, release);
                foreach (var file in files) Assert(AppInfo.FileHash(Path.Combine(stage, file.Path)) == file.Sha256, "Extracted digest");
                string damaged = Path.Combine(fixture, "damaged.zip"); byte[] bytes = File.ReadAllBytes(archive); bytes[bytes.Length / 2] ^= 1; File.WriteAllBytes(damaged, bytes);
                Reject(() => UpdatePackage.Extract(damaged, Path.Combine(fixture, "bad-stage"), release));
            });
            test("ZIP traversal and undeclared files are rejected before escaping staging", () =>
            {
                string bad = Path.Combine(fixture, "traversal.zip"); using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create)) { var entry = zip.CreateEntry("../escaped.txt"); using (var output = new StreamWriter(entry.Open())) output.Write("bad"); }
                var manifest = new UpdateRelease { Size = new FileInfo(bad).Length, Sha256 = AppInfo.FileHash(bad), Files = files };
                Reject(() => UpdatePackage.Extract(bad, Path.Combine(fixture, "path-stage"), manifest)); Assert(!File.Exists(Path.Combine(fixture, "escaped.txt")), "No escaped write");
                foreach (var path in new[] { "../OwnDesk.exe", "C:/OwnDesk.exe", "/OwnDesk.exe", "source/../../OwnDesk.exe", "source\\src\\x.cs", "devices.json", "host-secret.dpapi" }) Assert(!UpdatePolicy.AllowedFile(path), "Blocked: " + path);
            });
            test("Successful update preserves engine, device data and rollback backup", () =>
            {
                string target = Path.Combine(fixture, "commit"), transaction = Path.Combine(target, ".owndesk-update", "commit"); Directory.CreateDirectory(Path.Combine(target, "engine"));
                File.WriteAllText(Path.Combine(target, "OwnDesk.exe"), "old manager"); File.WriteAllText(Path.Combine(target, "OwnDesk.Update.exe"), "old updater"); File.WriteAllText(Path.Combine(target, "engine", "rustdesk.exe"), "unchanged engine");
                File.WriteAllText(Path.Combine(target, "devices.json"), "unchanged local sentinel");
                UpdateTransaction.Apply(target, stage, transaction, release, () => true);
                Assert(AppInfo.FileHash(Path.Combine(target, "OwnDesk.exe")) == files[0].Sha256, "New manager installed");
                Assert(File.ReadAllText(Path.Combine(target, "engine", "rustdesk.exe")) == "unchanged engine" && File.ReadAllText(Path.Combine(target, "devices.json")) == "unchanged local sentinel", "Independent data preserved");
                Assert(File.ReadAllText(Path.Combine(transaction, "backup", "OwnDesk.exe")) == "old manager", "Rollback backup retained");
            });
            test("Interrupted file replacement restores all old files", () =>
            {
                string target = Path.Combine(fixture, "copy-failure"), transaction = Path.Combine(target, ".owndesk-update", "failure"); Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "OwnDesk.exe"), "old manager"); File.WriteAllText(Path.Combine(target, "OwnDesk.Update.exe"), "old updater");
                Reject(() => UpdateTransaction.Apply(target, stage, transaction, release, () => true, count => { if (count == 1) throw new InvalidOperationException("Injected copy failure"); }));
                Assert(File.ReadAllText(Path.Combine(target, "OwnDesk.exe")) == "old manager" && File.ReadAllText(Path.Combine(target, "OwnDesk.Update.exe")) == "old updater", "Old files restored");
            });
            test("Failed startup health check rolls back new files and additions", () =>
            {
                string target = Path.Combine(fixture, "health-failure"), transaction = Path.Combine(target, ".owndesk-update", "failure"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "OwnDesk.exe"), "old manager");
                Reject(() => UpdateTransaction.Apply(target, stage, transaction, release, () => false));
                Assert(File.ReadAllText(Path.Combine(target, "OwnDesk.exe")) == "old manager" && !File.Exists(Path.Combine(target, "OwnDesk.Update.exe")), "Original presence restored");
            });
            test("Update URLs reject insecure schemes, credentials and downgrade redirects", () =>
            {
                UpdateTransport.Url("https://github.com/mohsunil/owndesk/releases/latest/download/latest.json", false);
                foreach (var url in new[] { "http://example.com/a", "file:///C:/update.zip", "https://user:pass@example.com/a", "javascript:alert(1)", "http://127.0.0.1/a" }) Reject(() => UpdateTransport.Url(url, false));
            });
            test("Pairing secrets are encrypted per Windows user and excluded from export", () =>
            {
                const string secret = "fixture_secret_not_a_real_key_12345";
                string code = PairCode.Create("Fixture PC", "100.70.1.2", secret); var device = PairCode.Read(code);
                Assert(device.ProtectedSecret != secret && SecretStore.Reveal(device.ProtectedSecret) == secret, "DPAPI round-trip");
                var store = new DeviceStore(Path.Combine(fixture, "paired-store")); store.Upsert(device); string path = Path.Combine(fixture, "export.json"); store.Export(path);
                string export = File.ReadAllText(path); Assert(!export.Contains(device.ProtectedSecret) && !export.Contains(secret), "Export excludes credentials");
                Reject(() => PairCode.Read("OWNDESK2:unsupported"));
            });
        }
    }
}
