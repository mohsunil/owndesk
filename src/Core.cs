using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OwnDesk
{
    public static class AppInfo
    {
        public const string Version = "0.2.0";
        public const string ProductTitle = "巫妖的远程魔导控制系统";
        public const string EngineVersion = "1.5.0";
        public const string EngineHash = "8555777215510d83d2d61c9dc984e4fcc838bd7e79f9d18a42585431f5e8bb47";
        public static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnDesk"); } }
        public static string ConfigRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config"); } }
        public static string SetupShutdownEvent { get { return "Local\\OwnDesk-SetupClose-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } }
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 * 8 }; }
        public static string Hash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        public static string FileHash(string path) { using (var sha = SHA256.Create()) using (var f = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }
        public static void AtomicWrite(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class Endpoint
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public bool IsTailnet { get; private set; }
        public string Address { get { return Host + (Port == 21118 ? "" : ":" + Port); } }
        public static Endpoint Parse(string input)
        {
            if (input == null) throw new ArgumentException("请输入电脑的 Tailscale IPv4 地址。");
            var parts = input.Trim().Split(':');
            if (parts.Length > 2 || parts[0].Length == 0) throw new ArgumentException("请输入 IPv4 地址，例如 100.80.10.20；可附加 :21118 端口。");
            var segments = parts[0].Split('.');
            if (segments.Length != 4 || segments.Any(x => !Regex.IsMatch(x, "^[0-9]{1,3}$"))) throw new ArgumentException("本版使用 IPv4 直连，请填写 100.x.x.x 专网地址或局域网 IP。");
            byte[] bytes = new byte[4];
            for (int i = 0; i < 4; i++) if (!byte.TryParse(segments[i], out bytes[i])) throw new ArgumentException("IPv4 地址不正确。");
            bool tail = bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
            bool local = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
            if (!tail && !local) throw new ArgumentException("请使用 Tailscale 专网或局域网地址，无需把远控端口开放到公网。");
            int port = 21118;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port < 1 || port > 65535)) throw new ArgumentException("端口应为 1 到 65535。");
            return new Endpoint { Host = string.Join(".", bytes.Select(x => x.ToString()).ToArray()), Port = port, IsTailnet = tail };
        }
    }

    public sealed class Device
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string LastOpenedUtc { get; set; }
        public string ProtectedSecret { get; set; }
        public Device() { Id = Guid.NewGuid().ToString("N"); LastOpenedUtc = ""; }
        public Device Copy() { return new Device { Id = Id, Name = Name, Address = Address, LastOpenedUtc = LastOpenedUtc, ProtectedSecret = ProtectedSecret }; }
        public void Validate()
        {
            Name = (Name ?? "").Trim();
            if (Name.Length == 0 || Name.Length > 48 || Name.Any(char.IsControl)) throw new ArgumentException("电脑名称应为 1 到 48 个普通字符。");
            Address = Endpoint.Parse(Address).Address;
            if ((ProtectedSecret ?? "").Length > 8192) throw new ArgumentException("配对信息过长。");
            Guid parsed;
            if (!Guid.TryParse(Id, out parsed)) Id = Guid.NewGuid().ToString("N");
            DateTime time;
            if (!string.IsNullOrEmpty(LastOpenedUtc) && !DateTime.TryParse(LastOpenedUtc, out time)) LastOpenedUtc = "";
        }
    }
    public sealed class DeviceFile
    {
        public int Schema { get; set; }
        public string Profile { get; set; }
        public List<Device> Devices { get; set; }
        public DeviceFile() { Schema = 1; Profile = "office"; Devices = new List<Device>(); }
    }
    public sealed class DeviceStore
    {
        public string Root { get; private set; }
        public string LoadNotice { get; private set; }
        public DeviceFile Data { get; private set; }
        public DeviceStore(string root)
        {
            Root = root; Data = new DeviceFile();
            string path = Path.Combine(root, "devices.json");
            if (!File.Exists(path)) return;
            try { Data = Read(path); }
            catch (Exception ex) { LoadNotice = "设备列表无法读取，原文件已保留。" + ex.Message; }
        }
        public static DeviceFile Read(string path)
        {
            if (new FileInfo(path).Length > 256 * 1024) throw new ArgumentException("设备列表超过 256 KB。");
            DeviceFile data = AppInfo.Json().Deserialize<DeviceFile>(File.ReadAllText(path, Encoding.UTF8));
            if (data == null || data.Schema != 1 || data.Devices == null || data.Devices.Count > 128) throw new ArgumentException("设备列表格式不正确或版本不受支持。");
            var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in data.Devices)
            {
                if (d == null) throw new ArgumentException("设备列表含有空记录。");
                d.Validate();
                if (!addresses.Add(d.Address) || !ids.Add(d.Id)) throw new ArgumentException("设备列表含有重复记录。");
            }
            if (!QualityProfile.All.Any(x => x.Id == data.Profile)) data.Profile = "office";
            return data;
        }
        public void Commit(DeviceFile next)
        {
            if (next.Devices.Count > 128) throw new ArgumentException("本版最多保存 128 台电脑。");
            var seen = new HashSet<string>();
            foreach (var d in next.Devices) { d.Validate(); if (!seen.Add(d.Address)) throw new ArgumentException("这台电脑已经在设备列表里。"); }
            string target = Path.Combine(Root, "devices.json");
            Directory.CreateDirectory(Root);
            if (File.Exists(target)) File.Copy(target, target + ".bak", true);
            AppInfo.AtomicWrite(target, AppInfo.Json().Serialize(next));
            Data = next;
        }
        public DeviceFile Clone() { return new DeviceFile { Profile = Data.Profile, Devices = Data.Devices.Select(x => x.Copy()).ToList() }; }
        public void Upsert(Device device)
        {
            device.Validate(); var next = Clone();
            int index = next.Devices.FindIndex(x => x.Id == device.Id);
            if (index >= 0) next.Devices[index] = device.Copy(); else next.Devices.Add(device.Copy());
            Commit(next);
        }
        public void Remove(string id) { var next = Clone(); next.Devices.RemoveAll(x => x.Id == id); Commit(next); }
        public int Import(string path)
        {
            var incoming = Read(path); var next = Clone(); int added = 0;
            foreach (var device in incoming.Devices)
            {
                if (next.Devices.Any(x => x.Address == device.Address)) continue;
                var clean = new Device { Name = device.Name, Address = device.Address };
                next.Devices.Add(clean); added++;
            }
            Commit(next); return added;
        }
        public void Export(string path)
        {
            var data = new DeviceFile { Profile = Data.Profile, Devices = Data.Devices.Select(x => new Device { Name = x.Name, Address = x.Address }).ToList() };
            AppInfo.AtomicWrite(path, AppInfo.Json().Serialize(data));
        }
    }

    public sealed class ProcessResult
    {
        public int Code;
        public string Output;
        public string Error;
    }
    public static class ProcessTools
    {
        public static string Quote(string value)
        {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') b.Append('\\', slashes * 2 + 1).Append('"');
                else b.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }
        public static Task<ProcessResult> Run(string executable, string arguments, int timeout)
        {
            return Task.Run(() =>
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                    p.Start();
                    var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeout)) { try { p.Kill(); } catch { } throw new TimeoutException("操作超时，请检查 Tailscale 是否正在运行。"); }
                    Task.WaitAll(output, error);
                    return new ProcessResult { Code = p.ExitCode, Output = output.Result, Error = error.Result };
                }
            });
        }
    }

    public sealed class TailPeer
    {
        public string Name;
        public string Address;
        public string OS;
        public bool Online;
    }
    public sealed class TailState
    {
        public string Backend = "Unknown";
        public string IPv4 = "";
        public string Error = "";
        public List<TailPeer> Peers = new List<TailPeer>();
        public bool Ready { get { return Backend == "Running" && !string.IsNullOrEmpty(IPv4); } }
    }
    public sealed class TailscaleClient
    {
        public string Executable { get; private set; }
        public TailscaleClient()
        {
            var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe") };
            Executable = candidates.FirstOrDefault(File.Exists);
        }
        static string Value(Dictionary<string, object> dict, string key)
        {
            object value; return dict.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }
        static string Address(Dictionary<string, object> dict)
        {
            object value;
            if (!dict.TryGetValue("TailscaleIPs", out value) || !(value is System.Collections.IEnumerable)) return "";
            foreach (object item in (System.Collections.IEnumerable)value)
            {
                try { var ip = Endpoint.Parse(Convert.ToString(item)); if (ip.IsTailnet) return ip.Host; } catch (ArgumentException) { }
            }
            return "";
        }
        public static TailState Parse(string json)
        {
            var root = AppInfo.Json().Deserialize<Dictionary<string, object>>(json);
            if (root == null) throw new ArgumentException("Tailscale 没有返回状态。");
            var state = new TailState { Backend = Value(root, "BackendState"), IPv4 = Address(root) };
            object peers;
            if (root.TryGetValue("Peer", out peers) && peers is Dictionary<string, object>)
                foreach (var entry in (Dictionary<string, object>)peers)
                {
                    var peer = entry.Value as Dictionary<string, object>; if (peer == null) continue;
                    string ip = Address(peer); if (ip.Length == 0 || ip == state.IPv4) continue;
                    object online;
                    state.Peers.Add(new TailPeer { Name = Value(peer, "HostName"), Address = ip, OS = Value(peer, "OS"), Online = peer.TryGetValue("Online", out online) && online is bool && (bool)online });
                }
            state.Peers = state.Peers.OrderByDescending(x => x.Online).ThenBy(x => x.Name).ToList();
            return state;
        }
        public async Task<TailState> Status()
        {
            if (Executable == null) return new TailState { Backend = "NotInstalled", Error = "尚未安装 Tailscale" };
            try
            {
                var result = await ProcessTools.Run(Executable, "status --json", 7000);
                if (result.Code != 0) return new TailState { Error = "Tailscale 服务未就绪，请在托盘中登录并连接。" };
                return Parse(result.Output);
            }
            catch (Exception ex) { return new TailState { Error = ex.Message }; }
        }
        public async Task<string> Route(Endpoint endpoint)
        {
            if (!endpoint.IsTailnet) return "局域网地址";
            if (Executable == null) return "未安装 Tailscale";
            try
            {
                var result = await ProcessTools.Run(Executable, "ping --c 3 --timeout 2s " + ProcessTools.Quote(endpoint.Host), 11000);
                var lines = result.Output.Split('\n').Where(x => x.StartsWith("pong from", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (lines.Length == 0) return "专网未应答";
                string last = lines.Last();
                var ms = Regex.Match(last, @"in (\d+(?:\.\d+)?m?s)");
                string latency = ms.Success ? " · " + ms.Groups[1].Value : "";
                if (last.IndexOf("DERP", StringComparison.OrdinalIgnoreCase) >= 0 || last.IndexOf("peer-relay", StringComparison.OrdinalIgnoreCase) >= 0) return "专网中继" + latency;
                if (last.IndexOf("via ", StringComparison.OrdinalIgnoreCase) >= 0) return "专网直连" + latency;
                return "专网已应答" + latency;
            }
            catch { return "路径未确认"; }
        }
    }
    public static class Connectivity
    {
        public static async Task<bool> Probe(string host, int port, int timeout)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var connection = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(connection, Task.Delay(timeout)) != connection)
                    {
                        client.Close();
                        try { await connection; } catch { }
                        return false;
                    }
                    await connection; return client.Connected;
                }
                catch (SocketException) { return false; }
                catch (ObjectDisposedException) { return false; }
            }
        }
    }

    public sealed class QualityProfile
    {
        public string Id;
        public string Name;
        public int Quality;
        public int Fps;
        public string Description;
        public override string ToString() { return Name; }
        public static readonly QualityProfile[] All = {
            new QualityProfile { Id = "office", Name = "高清办公", Quality = 90, Fps = 30, Description = "原始画面 · 画质值 90 · 目标 30 fps\n适合文字、表格、代码和日常办公。" },
            new QualityProfile { Id = "smooth", Name = "流畅操作", Quality = 75, Fps = 60, Description = "原始画面 · 画质值 75 · 目标 60 fps\n适合动态画面，需要更好的上传带宽与编码能力。" },
            new QualityProfile { Id = "lite", Name = "低带宽", Quality = 55, Fps = 20, Description = "原始画面 · 画质值 55 · 目标 20 fps\n网络拥堵时降低画质与帧率。" }
        };
        public static QualityProfile Get(string id) { return All.FirstOrDefault(x => x.Id == id) ?? All[0]; }
    }

    // Only edits scalar string keys in RustDesk-generated [options] tables.
    // Unknown TOML constructs are rejected instead of being rewritten.
    public static class TomlOptions
    {
        static readonly Regex Section = new Regex(@"^\s*\[([^\[\]]+)\]\s*(?:#.*)?$");
        static readonly Regex KeyValue = new Regex("^\\s*(?:\"([A-Za-z0-9_-]+)\"|'([A-Za-z0-9_-]+)'|([A-Za-z0-9_-]+))\\s*=");
        public static string Patch(string input, IDictionary<string, string> updates)
        {
            if (input.Contains("\"\"\"") || input.Contains("'''")) throw new InvalidOperationException("配置含有多行 TOML 字符串，请在 RustDesk 设置中调整。");
            var lines = input.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
            int start = -1, end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                var m = Section.Match(lines[i]);
                if (m.Success && m.Groups[1].Value.Trim() == "options")
                {
                    if (start >= 0) throw new InvalidOperationException("配置中有重复 options 段，已停止修改。");
                    start = i;
                }
                else if (start >= 0 && end == lines.Count && lines[i].TrimStart().StartsWith("[")) end = i;
            }
            if (start < 0) { if (lines.Count > 0 && lines.Last().Length > 0) lines.Add(""); start = lines.Count; lines.Add("[options]"); end = lines.Count; }
            var used = new HashSet<string>();
            for (int i = start + 1; i < end; i++)
            {
                var m = KeyValue.Match(lines[i]); if (!m.Success) continue;
                string key = m.Groups[1].Success ? m.Groups[1].Value : (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
                if (!used.Add(key)) throw new InvalidOperationException("配置中有重复设置，已停止修改。");
                string value;
                if (updates.TryGetValue(key, out value)) lines[i] = key + " = '" + Safe(value) + "'";
            }
            foreach (var kv in updates) if (!used.Contains(kv.Key)) lines.Insert(end++, kv.Key + " = '" + Safe(kv.Value) + "'");
            return string.Join("\r\n", lines.ToArray()).TrimEnd('\r', '\n') + "\r\n";
        }
        static string Safe(string value)
        {
            if (value.Any(x => char.IsControl(x) || x == '\'')) throw new ArgumentException("配置值不受支持。");
            return value;
        }
    }
    public sealed class ConfigEntry
    {
        public string Name { get; set; }
        public bool Existed { get; set; }
        public string AfterHash { get; set; }
    }
    public sealed class ConfigChange
    {
        public string BackupId { get; set; }
        public List<ConfigEntry> Entries { get; set; }
    }
    public sealed class ConfigurationManager
    {
        readonly string configRoot, dataRoot;
        static readonly string[] Allowed = { "RustDesk2.toml", "RustDesk_default.toml" };
        public ConfigurationManager(string configRoot, string dataRoot) { this.configRoot = configRoot; this.dataRoot = dataRoot; }
        public string Apply(QualityProfile quality, bool host)
        {
            var changes = new Dictionary<string, Dictionary<string, string>>();
            changes["RustDesk_default.toml"] = new Dictionary<string, string> {
                { "image_quality", "custom" }, { "custom_image_quality", quality.Quality.ToString() }, { "custom-fps", quality.Fps.ToString() }, { "view_style", "original" }, { "codec-preference", "auto" }, { "enable-file-copy-paste", "Y" }, { "disable-clipboard", "N" }
            };
            var options = new Dictionary<string, string> { { "enable-hwcodec", "Y" }, { "enable-abr", "Y" } };
            if (host)
            {
                options["direct-server"] = "Y"; options["direct-access-port"] = "21118";
                options["whitelist"] = "100.64.0.0/10";
            }
            changes["RustDesk2.toml"] = options;
            var outputs = new Dictionary<string, string>();
            foreach (var item in changes)
            {
                string path = Path.Combine(configRoot, item.Key);
                outputs[item.Key] = TomlOptions.Patch(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "", item.Value);
            }
            string id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string backup = Path.Combine(dataRoot, "config-backups", id); Directory.CreateDirectory(backup);
            var journal = new ConfigChange { BackupId = id, Entries = new List<ConfigEntry>() };
            foreach (var output in outputs)
            {
                string path = Path.Combine(configRoot, output.Key); bool existed = File.Exists(path);
                if (existed) File.Copy(path, Path.Combine(backup, output.Key));
                journal.Entries.Add(new ConfigEntry { Name = output.Key, Existed = existed, AfterHash = AppInfo.Hash(new UTF8Encoding(false).GetBytes(output.Value)) });
            }
            AppInfo.AtomicWrite(Path.Combine(backup, "journal.json"), AppInfo.Json().Serialize(journal));
            var applied = new List<ConfigEntry>();
            try
            {
                foreach (var entry in journal.Entries) { AppInfo.AtomicWrite(Path.Combine(configRoot, entry.Name), outputs[entry.Name]); applied.Add(entry); }
                AppInfo.AtomicWrite(Path.Combine(dataRoot, "last-config-change.json"), AppInfo.Json().Serialize(journal));
            }
            catch
            {
                foreach (var entry in applied) RestoreEntry(entry, backup);
                throw;
            }
            return backup;
        }
        void RestoreEntry(ConfigEntry entry, string backup)
        {
            string target = Path.Combine(configRoot, entry.Name);
            if (entry.Existed) AppInfo.AtomicWrite(target, File.ReadAllText(Path.Combine(backup, entry.Name), Encoding.UTF8));
            else if (File.Exists(target)) File.Delete(target);
        }
        public void Restore()
        {
            string journalPath = Path.Combine(dataRoot, "last-config-change.json");
            if (!File.Exists(journalPath)) throw new InvalidOperationException("还没有本软件创建的配置备份。");
            var journal = AppInfo.Json().Deserialize<ConfigChange>(File.ReadAllText(journalPath, Encoding.UTF8));
            if (journal == null || journal.Entries == null || !Regex.IsMatch(journal.BackupId ?? "", "^[A-Za-z0-9-]+$")) throw new InvalidOperationException("备份记录不正确。");
            string backup = Path.Combine(dataRoot, "config-backups", journal.BackupId);
            foreach (var entry in journal.Entries)
            {
                if (entry == null || !Allowed.Contains(entry.Name)) throw new InvalidOperationException("备份含有不受支持的配置文件。");
                string target = Path.Combine(configRoot, entry.Name);
                if (!File.Exists(target) || AppInfo.FileHash(target) != entry.AfterHash) throw new InvalidOperationException("配置在应用之后又有改动，已停止自动恢复。可在本地数据目录查看原备份。");
                if (entry.Existed && !File.Exists(Path.Combine(backup, entry.Name))) throw new InvalidOperationException("原备份文件缺失。");
            }
            foreach (var entry in journal.Entries) RestoreEntry(entry, backup);
            File.Delete(journalPath);
        }
    }

    public sealed class EngineManager
    {
        public string Executable { get; private set; }
        public EngineManager(string baseDirectory) { Executable = Path.Combine(baseDirectory, "engine", "rustdesk.exe"); }
        public bool Available { get { return File.Exists(Executable); } }
        public void Verify()
        {
            if (!Available) throw new FileNotFoundException("缺少远控引擎，请先完整解压软件包，再运行 OwnDesk.exe。");
            if (AppInfo.FileHash(Executable) != AppInfo.EngineHash) throw new InvalidOperationException("远控引擎与此版本的校验值不一致，请重新解压原始软件包。");
        }
        public static bool Running()
        {
            foreach (var p in Process.GetProcessesByName("rustdesk")) { p.Dispose(); return true; }
            return false;
        }
        public static bool Installed()
        {
            return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe")) || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RustDesk", "rustdesk.exe"));
        }
        public ProcessStartInfo ConnectionInfo(Endpoint endpoint, bool files)
        {
            return new ProcessStartInfo(Executable, (files ? "--file-transfer " : "--connect ") + ProcessTools.Quote(endpoint.Address)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Executable) };
        }
        public void Open() { Verify(); Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Executable) }); }
        public void Connect(Endpoint endpoint, bool files) { Verify(); Process.Start(ConnectionInfo(endpoint, files)); }
        public void ConnectPaired(Endpoint endpoint, bool files, string secret)
        {
            Verify(); var info = ConnectionInfo(endpoint, files);
            if (!string.IsNullOrEmpty(secret)) info.Arguments += " --password " + ProcessTools.Quote(secret);
            Process.Start(info);
        }
    }
}
