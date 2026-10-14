using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OwnDesk
{
    public sealed class UpdateEnvelope
    {
        public int Schema { get; set; }
        public string Payload { get; set; }
        public string Signature { get; set; }
    }
    public sealed class UpdateFile
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
    }
    public sealed class UpdateRelease
    {
        public int Schema { get; set; }
        public string Product { get; set; }
        public string Version { get; set; }
        public string Architecture { get; set; }
        public string Channel { get; set; }
        public string PackageUrl { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Notes { get; set; }
        public List<UpdateFile> Files { get; set; }
    }
    public static class UpdatePolicy
    {
        public const long MaxPackage = 128L * 1024 * 1024;
        public static Version VersionOf(string text)
        {
            if (!Regex.IsMatch(text ?? "", @"^\d{1,4}\.\d{1,4}\.\d{1,4}$")) throw new InvalidDataException("更新版本格式不正确。");
            var v = new Version(text); return new Version(v.Major, v.Minor, v.Build, 0);
        }
        public static bool HashValid(string hash) { return Regex.IsMatch(hash ?? "", "^[a-f0-9]{64}$"); }
        public static bool AllowedFile(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Contains("\\") || name.StartsWith("/") || name.Contains(":") || name.Split('/').Any(x => x == ".." || x == "." || x.Length == 0)) return false;
            if (new[] { "OwnDesk.exe", "OwnDesk.Update.exe", "使用说明.html", "开始使用.txt", "THIRD-PARTY-NOTICES.txt", "测试记录.json", "文件校验.sha256", "licenses/LICENSE-OwnDesk.txt", "licenses/LICENSE-RustDesk-AGPL-3.0.txt", "source/build.ps1", "source/build-setup.ps1", "source/app.manifest", "source/SOURCE-README.txt", "source/assets/OwnDesk.ico", "source/assets/OwnDesk.png", "source/tools/Publish-Update.ps1", "source/installer/InstallerMain.cs" }.Contains(name)) return true;
            return Regex.IsMatch(name, @"^source/src/[A-Za-z0-9_-]+\.cs$");
        }
        public static string Within(string root, string name)
        {
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(basePath, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径越界。");
            string current = Path.GetDirectoryName(path);
            while (current != null && current.Length >= basePath.TrimEnd(Path.DirectorySeparatorChar).Length)
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新目录含有链接，请改用普通本地安装目录。");
                current = Path.GetDirectoryName(current);
            }
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新文件含有链接。");
            return path;
        }
        public static UpdateRelease Verify(byte[] envelopeBytes, string publicKey)
        {
            if (envelopeBytes.Length > 64 * 1024) throw new InvalidDataException("更新描述过大。");
            var envelope = AppInfo.Json().Deserialize<UpdateEnvelope>(Encoding.UTF8.GetString(envelopeBytes));
            if (envelope == null || envelope.Schema != 1) throw new InvalidDataException("更新描述版本不受支持。");
            byte[] payload = Convert.FromBase64String(envelope.Payload ?? ""), signature = Convert.FromBase64String(envelope.Signature ?? "");
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature)) throw new CryptographicException("更新签名不正确，已拒绝安装。");
            }
            var release = AppInfo.Json().Deserialize<UpdateRelease>(Encoding.UTF8.GetString(payload));
            if (release == null || release.Schema != 1 || release.Product != "OwnDesk" || release.Architecture != "x64" || release.Channel != "stable") throw new InvalidDataException("更新包不适用于本软件。");
            VersionOf(release.Version);
            if (!HashValid(release.Sha256) || release.Size <= 0 || release.Size > MaxPackage || release.Files == null || release.Files.Count < 2 || release.Files.Count > 64) throw new InvalidDataException("更新包描述不完整。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in release.Files)
            {
                if (file == null || !AllowedFile(file.Path) || !HashValid(file.Sha256) || file.Size < 0 || file.Size > MaxPackage || !names.Add(file.Path)) throw new InvalidDataException("更新文件列表不正确。");
                total += file.Size;
            }
            if (total > MaxPackage || !names.Contains("OwnDesk.exe") || !names.Contains("OwnDesk.Update.exe")) throw new InvalidDataException("更新包缺少程序或体积超限。");
            if ((release.Notes ?? "").Length > 4000) throw new InvalidDataException("更新说明过长。");
            return release;
        }
    }
    public sealed class UpdateTransport
    {
        readonly bool testHttp;
        public UpdateTransport(bool testHttp = false) { this.testHttp = testHttp; }
        public static Uri Url(string text, bool allowTest)
        {
            Uri url;
            if (!Uri.TryCreate(text, UriKind.Absolute, out url) || url.UserInfo.Length > 0 || !(url.Scheme == "https" || (allowTest && url.Scheme == "http" && url.IsLoopback))) throw new InvalidDataException("更新地址必须使用 HTTPS。");
            return url;
        }
        public async Task<byte[]> Fetch(string address, long limit, CancellationToken token)
        {
            Uri current = Url(address, testHttp); ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("OwnDesk/" + AppInfo.Version);
                for (int redirect = 0; redirect < 8; redirect++)
                {
                    using (var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        int code = (int)response.StatusCode;
                        if (code >= 300 && code < 400 && response.Headers.Location != null)
                        { current = Url(new Uri(current, response.Headers.Location).AbsoluteUri, testHttp); continue; }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > limit) throw new InvalidDataException("下载文件体积超限。");
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var output = new MemoryStream())
                        {
                            var buffer = new byte[64 * 1024]; int count;
                            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                            { if (output.Length + count > limit) throw new InvalidDataException("下载文件体积超限。"); output.Write(buffer, 0, count); }
                            return output.ToArray();
                        }
                    }
                }
            }
            throw new InvalidDataException("更新地址重定向过多。");
        }
    }
    public static class UpdatePackage
    {
        public static void Extract(string archive, string stage, UpdateRelease release)
        {
            if (new FileInfo(archive).Length != release.Size || AppInfo.FileHash(archive) != release.Sha256) throw new InvalidDataException("更新包校验失败。");
            Directory.CreateDirectory(stage); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 64) throw new InvalidDataException("压缩包文件数量超限。");
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName;
                    if (!UpdatePolicy.AllowedFile(name) || !seen.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("压缩包含有不允许的路径。");
                    var file = release.Files.FirstOrDefault(x => x.Path == name);
                    if (file == null || entry.Length != file.Size) throw new InvalidDataException("压缩包与签名文件列表不一致。");
                    string target = UpdatePolicy.Within(stage, name); Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[64 * 1024]; int count; long written = 0;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { written += count; if (written > file.Size) throw new InvalidDataException("解压体积超限。"); output.Write(buffer, 0, count); }
                        if (written != file.Size) throw new InvalidDataException("解压文件不完整。");
                    }
                    if (AppInfo.FileHash(target) != file.Sha256) throw new InvalidDataException("解压文件校验失败。");
                }
            }
            if (seen.Count != release.Files.Count) throw new InvalidDataException("压缩包缺少签名描述中的文件。");
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(stage, "OwnDesk.exe"));
            if (info.ProductName != "OwnDesk" || new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, 0) != UpdatePolicy.VersionOf(release.Version)) throw new InvalidDataException("更新程序版本不一致。");
        }
    }
    public sealed class UpdateSettings
    {
        public bool AutoCheck { get; set; }
        public bool AutoInstall { get; set; }
        public string LastCheckedUtc { get; set; }
        public string FailedVersion { get; set; }
        public string LastResult { get; set; }
        public UpdateSettings() { AutoCheck = true; AutoInstall = true; LastCheckedUtc = ""; FailedVersion = ""; LastResult = "等待首次检查"; }
        public static UpdateSettings Load(string dataRoot)
        {
            string path = Path.Combine(dataRoot, "updates", "settings.json");
            try { return File.Exists(path) ? AppInfo.Json().Deserialize<UpdateSettings>(File.ReadAllText(path)) ?? new UpdateSettings() : new UpdateSettings(); }
            catch { return new UpdateSettings(); }
        }
        public void Save(string dataRoot) { AppInfo.AtomicWrite(Path.Combine(dataRoot, "updates", "settings.json"), AppInfo.Json().Serialize(this)); }
    }
    public sealed class UpdatePlan
    {
        public string InstallRoot { get; set; }
        public string WorkRoot { get; set; }
        public string ExpectedManagerHash { get; set; }
        public int ParentId { get; set; }
        public long ParentStartedTicks { get; set; }
        public string TransactionId { get; set; }
    }
    public sealed class BackupItem
    {
        public string Path { get; set; }
        public bool Existed { get; set; }
    }
    public static class UpdateTransaction
    {
        static void CopyAtomic(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)); string temporary = target + "." + Guid.NewGuid().ToString("N") + ".new";
            try { File.Copy(source, temporary); if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static void Apply(string targetRoot, string stage, string transaction, UpdateRelease release, Func<bool> health, Action<int> afterCopy = null)
        {
            string root = Path.GetFullPath(targetRoot); var items = new List<BackupItem>();
            foreach (var file in release.Files)
            {
                if (!UpdatePolicy.AllowedFile(file.Path)) throw new InvalidDataException("更新文件不受支持。");
                string source = UpdatePolicy.Within(stage, file.Path), target = UpdatePolicy.Within(root, file.Path);
                if (!File.Exists(source) || AppInfo.FileHash(source) != file.Sha256) throw new InvalidDataException("暂存文件已改变。");
                bool existed = File.Exists(target); if (existed) { string backup = UpdatePolicy.Within(Path.Combine(transaction, "backup"), file.Path); Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Copy(target, backup); }
                items.Add(new BackupItem { Path = file.Path, Existed = existed });
            }
            AppInfo.AtomicWrite(Path.Combine(transaction, "journal.json"), AppInfo.Json().Serialize(new { state = "prepared", files = items, version = release.Version }));
            try
            {
                // The monolithic manager goes last, so interrupted preparation retains the old entry point.
                int copied = 0;
                foreach (var file in release.Files.OrderBy(x => x.Path == "OwnDesk.exe" ? 1 : 0))
                { CopyAtomic(UpdatePolicy.Within(stage, file.Path), UpdatePolicy.Within(root, file.Path)); copied++; if (afterCopy != null) afterCopy(copied); }
                if (!health()) throw new InvalidOperationException("新版启动检查未通过。");
                AppInfo.AtomicWrite(Path.Combine(transaction, "result.json"), AppInfo.Json().Serialize(new { success = true, version = release.Version }));
            }
            catch
            {
                foreach (var item in items.AsEnumerable().Reverse())
                {
                    string target = UpdatePolicy.Within(root, item.Path);
                    if (item.Existed) CopyAtomic(UpdatePolicy.Within(Path.Combine(transaction, "backup"), item.Path), target);
                    else if (File.Exists(target)) File.Delete(target);
                }
                AppInfo.AtomicWrite(Path.Combine(transaction, "result.json"), AppInfo.Json().Serialize(new { success = false, restored = true, version = release.Version }));
                throw;
            }
        }
    }
    public sealed class UpdateClient
    {
        readonly string dataRoot;
        readonly UpdateTransport transport = new UpdateTransport();
        public UpdateSettings Settings { get; private set; }
        public UpdateRelease Pending { get; private set; }
        public string PendingRoot { get; private set; }
        public bool Busy { get; private set; }
        public UpdateClient(string dataRoot) { this.dataRoot = dataRoot; Settings = UpdateSettings.Load(dataRoot); }
        public bool Due()
        {
            DateTime time; return Settings.AutoCheck && (!DateTime.TryParse(Settings.LastCheckedUtc, out time) || DateTime.UtcNow - time.ToUniversalTime() >= TimeSpan.FromHours(6));
        }
        public async Task Check(bool manual, Action<string> status)
        {
            if (Busy) return; Busy = true;
            try
            {
                status("正在检查更新…");
                using (var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    var envelope = await transport.Fetch(UpdateTrust.DefaultFeed, 64 * 1024, cancel.Token);
                    var release = UpdatePolicy.Verify(envelope, UpdateTrust.PublicKeyXml); UpdateTransport.Url(release.PackageUrl, false);
                    if (UpdatePolicy.VersionOf(release.Version) <= UpdatePolicy.VersionOf(AppInfo.Version)) { Settings.LastResult = "已是最新版本 v" + AppInfo.Version; status(Settings.LastResult); return; }
                    if (!manual && Settings.FailedVersion == release.Version) { Settings.LastResult = "上次更新已回退，等待下一个版本"; status(Settings.LastResult); return; }
                    status("发现 v" + release.Version + "，正在下载…");
                    string work = Path.Combine(dataRoot, "updates", "pending", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
                    File.WriteAllBytes(Path.Combine(work, "latest.json"), envelope);
                    var bytes = await transport.Fetch(release.PackageUrl, release.Size, cancel.Token);
                    string archive = Path.Combine(work, "package.zip"); File.WriteAllBytes(archive, bytes);
                    await Task.Run(() => UpdatePackage.Extract(archive, Path.Combine(work, "stage"), release));
                    Pending = release; PendingRoot = work; Settings.LastResult = "v" + release.Version + " 已校验，等待空闲安装"; status(Settings.LastResult);
                }
            }
            catch (Exception ex)
            {
                Settings.LastResult = ex is HttpRequestException ? "更新源暂时不可用，稍后自动重试" : "更新检查未完成：" + ex.Message;
                status(Settings.LastResult);
            }
            finally { Settings.LastCheckedUtc = DateTime.UtcNow.ToString("o"); try { Settings.Save(dataRoot); } catch { } Busy = false; }
        }
        public void StartInstall(string installRoot)
        {
            if (Pending == null || PendingRoot == null) throw new InvalidOperationException("还没有已下载的新版本。");
            string helper = Path.Combine(installRoot, "OwnDesk.Update.exe"); if (!File.Exists(helper)) throw new FileNotFoundException("缺少更新组件，请重新运行安装包。");
            string runnerDir = Path.Combine(dataRoot, "updates", "runner", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(runnerDir);
            string runner = Path.Combine(runnerDir, "OwnDesk.Update.exe"); File.Copy(helper, runner);
            var parent = Process.GetCurrentProcess();
            var plan = new UpdatePlan { InstallRoot = Path.GetFullPath(installRoot), WorkRoot = PendingRoot, ParentId = parent.Id, ParentStartedTicks = parent.StartTime.ToUniversalTime().Ticks, ExpectedManagerHash = AppInfo.FileHash(Path.Combine(installRoot, "OwnDesk.exe")), TransactionId = Guid.NewGuid().ToString("N") };
            string path = Path.Combine(runnerDir, "plan.json"); AppInfo.AtomicWrite(path, AppInfo.Json().Serialize(plan));
            Process.Start(new ProcessStartInfo(runner, "--apply " + ProcessTools.Quote(path)) { UseShellExecute = false, CreateNoWindow = true });
        }
    }
}
