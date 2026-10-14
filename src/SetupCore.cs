using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace OwnDesk
{
    public static class SecretStore
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OwnDesk paired-device secret v1");
        public static string Protect(string secret) { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser)); }
        public static string Reveal(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText)) return "";
            byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); } finally { Array.Clear(clear, 0, clear.Length); }
        }
        public static string EnsureHostSecret()
        {
            string path = Path.Combine(AppInfo.DataRoot, "host-secret.dpapi");
            if (File.Exists(path)) return Reveal(File.ReadAllText(path));
            byte[] random = new byte[24]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            string secret = Convert.ToBase64String(random).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            AppInfo.AtomicWrite(path, Protect(secret)); return secret;
        }
        public static string ConfiguredHostSecret()
        {
            string marker = Path.Combine(AppInfo.DataRoot, "host-ready.json"), secret = Path.Combine(AppInfo.DataRoot, "host-secret.dpapi");
            if (!File.Exists(marker) || !File.Exists(secret)) return "";
            try { return Reveal(File.ReadAllText(secret)); } catch { return ""; }
        }
    }
    public sealed class PairCode
    {
        public int Protocol { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string Secret { get; set; }
        const string Prefix = "OWNDESK1:";
        public static string Create(string name, string address, string secret)
        {
            var code = new PairCode { Protocol = 1, Name = name, Address = Endpoint.Parse(address).Address, Secret = secret };
            return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(AppInfo.Json().Serialize(code)));
        }
        public static Device Read(string value)
        {
            if (value == null || value.Length > 4096 || !value.Trim().StartsWith(Prefix, StringComparison.Ordinal)) throw new ArgumentException("这不是有效的连接码，请从另一台电脑复制本机连接码。");
            var code = AppInfo.Json().Deserialize<PairCode>(Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim().Substring(Prefix.Length))));
            if (code == null || code.Protocol != 1 || !Regex.IsMatch(code.Secret ?? "", "^[A-Za-z0-9_-]{16,64}$")) throw new ArgumentException("连接码格式或版本不受支持。");
            var device = new Device { Name = code.Name, Address = code.Address, ProtectedSecret = SecretStore.Protect(code.Secret) }; device.Validate(); return device;
        }
    }
    public sealed class MachineSetupRequest
    {
        public string InstallRoot { get; set; }
        public string UserSid { get; set; }
        public string ResultPath { get; set; }
        public bool EnableHost { get; set; }
    }
    public sealed class MachineSetupResult
    {
        public bool Success { get; set; }
        public bool TailscaleInstalled { get; set; }
        public bool RustDeskInstalled { get; set; }
        public bool HostPaired { get; set; }
        public bool RestartSuggested { get; set; }
        public string Message { get; set; }
    }
    public static class MachineSetup
    {
        public sealed class HostBeforeState
        {
            public bool OwnedService { get; set; }
            public Dictionary<string, string> Options { get; set; }
        }
        static string ReadOption(string engine, string key)
        {
            var output = ProcessTools.Run(engine, "--option " + ProcessTools.Quote(key), 15000).GetAwaiter().GetResult();
            var lines = output.Output.Replace("\r\n", "\n").Split('\n');
            return lines.Length > 1 ? lines[lines.Length - 2].Trim() : output.Output.Trim();
        }
        static void RunCommand(string exe, string args, int timeout = 15000)
        {
            var result = ProcessTools.Run(exe, args, timeout).GetAwaiter().GetResult();
            if (result.Code != 0) throw new InvalidOperationException("组件配置未完成，请重新运行安装程序。");
        }
        static string InstalledEngine()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
            return File.Exists(path) ? path : null;
        }
        public static int Run(string requestFile)
        {
            MachineSetupRequest request = null; var result = new MachineSetupResult();
            try
            {
                request = AppInfo.Json().Deserialize<MachineSetupRequest>(File.ReadAllText(requestFile));
                if (request == null) throw new InvalidDataException("安装任务不正确。");
                var identity = WindowsIdentity.GetCurrent();
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("需要 Windows 管理员许可才能安装远控和专网组件。");
                if (identity.User.Value != request.UserSid) throw new InvalidOperationException("请使用当前 Windows 用户的管理员许可运行安装；切换到其他管理员账号会改变配对密钥的归属。");
                string root = Path.GetFullPath(request.InstallRoot);
                string expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OwnDesk");
                if (!root.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装目录不正确。");
                request.InstallRoot = root;
                SetupRecovery.Report(requestFile, "正在恢复并检查上次安装…");
                bool recoveredOwner = SetupRecovery.RetireLegacySetup(request);
                SetupRecovery.Report(requestFile, "正在安装程序文件…");
                SetupRecovery.CopyStagedPayload(requestFile, root);
                var engine = new EngineManager(root); engine.Verify();
                SetupRecovery.Report(requestFile, "正在检查 Tailscale 专网组件…");
                if (new TailscaleClient().Executable == null)
                {
                    string msi = UpdatePolicy.Within(root, "dependencies/tailscale.msi");
                    if (!File.Exists(msi))
                    {
                        SetupRecovery.Report(requestFile, "正在下载 Tailscale 专网组件，首次安装需要联网…");
                        using (var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
                        {
                            var bytes = new UpdateTransport().Fetch("https://pkgs.tailscale.com/stable/tailscale-setup-1.102.2-amd64.msi", 80L * 1024 * 1024, cancel.Token).GetAwaiter().GetResult();
                            if (AppInfo.Hash(bytes) != UpdateTrust.TailscaleInstallerHash) throw new InvalidDataException("下载的专网组件校验失败。");
                            Directory.CreateDirectory(Path.GetDirectoryName(msi)); File.WriteAllBytes(msi, bytes);
                        }
                    }
                    if (AppInfo.FileHash(msi) != UpdateTrust.TailscaleInstallerHash) throw new InvalidDataException("专网组件校验失败。");
                    SetupRecovery.Report(requestFile, "正在安装 Tailscale 专网组件…");
                    var msiResult = ProcessTools.Run(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/i " + ProcessTools.Quote(msi) + " /qn /norestart", 180000).GetAwaiter().GetResult();
                    if (msiResult.Code != 0 && msiResult.Code != 3010) throw new InvalidOperationException("专网组件安装未完成。");
                    result.RestartSuggested = msiResult.Code == 3010;
                }
                result.TailscaleInstalled = new TailscaleClient().Executable != null;
                if (!result.TailscaleInstalled) throw new InvalidOperationException("未找到已安装的专网组件。");
                if (request.EnableHost)
                {
                    bool existed = InstalledEngine() != null;
                    bool ownService = !existed || recoveredOwner;
                    string ownershipFile = Path.Combine(AppInfo.DataRoot, "host-install-started.txt");
                    if (existed && File.Exists(ownershipFile))
                    {
                        DateTime started;
                        if (DateTime.TryParse(File.ReadAllText(ownershipFile), out started)) { DateTime created = File.GetCreationTimeUtc(InstalledEngine()); ownService |= created >= started.ToUniversalTime().AddSeconds(-3) && created <= started.ToUniversalTime().AddMinutes(2); }
                    }
                    if (!existed)
                    {
                        AppInfo.AtomicWrite(ownershipFile, DateTime.UtcNow.ToString("o"));
                        SetupRecovery.Report(requestFile, "正在安装 RustDesk 远控服务…");
                        RunCommand(engine.Executable, "--silent-install", 60000);
                        var watch = Stopwatch.StartNew();
                        while (InstalledEngine() == null && watch.Elapsed < TimeSpan.FromSeconds(45)) Thread.Sleep(250);
                    }
                    string installed = InstalledEngine(); if (installed == null) throw new InvalidOperationException("远控服务安装未完成。");
                    result.RustDeskInstalled = true;
                    string priorPath = Path.Combine(AppInfo.DataRoot, "host-options-before.json");
                    if (File.Exists(priorPath))
                    {
                        var prior = AppInfo.Json().Deserialize<HostBeforeState>(File.ReadAllText(priorPath));
                        if (prior != null && ownService && !prior.OwnedService) { prior.OwnedService = true; AppInfo.AtomicWrite(priorPath, AppInfo.Json().Serialize(prior)); }
                        if (prior != null) ownService |= prior.OwnedService;
                        if (prior != null && prior.OwnedService)
                        {
                            RunCommand(Path.Combine(Environment.SystemDirectory, "sc.exe"), "config RustDesk start= auto", 15000);
                            ProcessTools.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start RustDesk", 15000).GetAwaiter().GetResult();
                        }
                    }
                    // Wait for the official service IPC endpoint instead of assuming launcher exit means ready.
                    Thread.Sleep(1500);
                    SetupRecovery.Report(requestFile, "正在备份远控服务原设置…");
                    var settings = new[] {
                        new[] { "direct-server", "Y" }, new[] { "direct-access-port", "21118" }, new[] { "whitelist", "100.64.0.0/10" },
                        new[] { "enable-clipboard", "Y" }, new[] { "enable-file-transfer", "Y" }, new[] { "enable-hwcodec", "Y" }, new[] { "enable-abr", "Y" }, new[] { "allow-auto-update", "Y" }
                    };
                    string beforePath = Path.Combine(AppInfo.DataRoot, "host-options-before.json");
                    if (!File.Exists(beforePath))
                    {
                        var before = new HostBeforeState { OwnedService = ownService, Options = new Dictionary<string, string>() };
                        int backupStep = 0;
                        foreach (var setting in settings) { SetupRecovery.Report(requestFile, "正在备份远控设置（" + (++backupStep) + "/" + settings.Length + "）…"); before.Options[setting[0]] = ReadOption(installed, setting[0]); }
                        AppInfo.AtomicWrite(beforePath, AppInfo.Json().Serialize(before));
                    }
                    int configureStep = 0;
                    foreach (var setting in settings) { SetupRecovery.Report(requestFile, "正在配置远控接入（" + (++configureStep) + "/" + settings.Length + "）…"); RunCommand(installed, "--option " + ProcessTools.Quote(setting[0]) + " " + ProcessTools.Quote(setting[1])); }
                    if (ownService || SecretStore.ConfiguredHostSecret().Length > 0)
                    {
                        SetupRecovery.Report(requestFile, "正在准备本机连接码…");
                        string secret = SecretStore.EnsureHostSecret();
                        var setPassword = ProcessTools.Run(installed, "--password " + ProcessTools.Quote(secret), 15000).GetAwaiter().GetResult();
                        if (!setPassword.Output.Contains("Done!")) throw new InvalidOperationException("本机配对口令未确认，请重试安装。");
                        RunCommand(installed, "--option approve-mode password");
                        RunCommand(installed, "--option verification-method use-permanent-password");
                        AppInfo.AtomicWrite(Path.Combine(AppInfo.DataRoot, "host-ready.json"), AppInfo.Json().Serialize(new { configuredUtc = DateTime.UtcNow.ToString("o"), engine = AppInfo.EngineVersion }));
                        result.HostPaired = true;
                    }
                    string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
                    SetupRecovery.Report(requestFile, "正在配置专网接入并验证设置…");
                    ProcessTools.Run(netsh, "advfirewall firewall delete rule name=\"OwnDesk Private Remote\"", 15000).GetAwaiter().GetResult();
                    RunCommand(netsh, "advfirewall firewall add rule name=\"OwnDesk Private Remote\" dir=in action=allow program=" + ProcessTools.Quote(installed) + " protocol=TCP localport=21118 remoteip=100.64.0.0/10 profile=any enable=yes");
                    if (ReadOption(installed, "direct-server") != "Y" || ReadOption(installed, "whitelist") != "100.64.0.0/10") throw new InvalidOperationException("远控服务接入设置还未确认，请重试安装。");
                }
                result.Success = true; result.Message = result.HostPaired ? "安装完成，首次登录后复制连接码即可配对。" : request.EnableHost ? "已沿用现有远控服务和密码，首次连接按引擎提示验证。" : "安装完成，可以连接其他已配对电脑。";
                SetupRecovery.Report(requestFile, result.Message);
            }
            catch (Exception ex) { result.Success = false; result.Message = ex.Message; }
            if (request != null) { try { AppInfo.AtomicWrite(request.ResultPath, AppInfo.Json().Serialize(result)); } catch { } }
            return result.Success ? 0 : 1;
        }
        public static void RemoveHostConfiguration()
        {
            string installed = InstalledEngine(), path = Path.Combine(AppInfo.DataRoot, "host-options-before.json");
            if (installed == null || !File.Exists(path)) return;
            var before = AppInfo.Json().Deserialize<HostBeforeState>(File.ReadAllText(path));
            if (before == null || before.Options == null) throw new InvalidDataException("原接入设置备份不可读，未更改其他远控服务。");
            foreach (var setting in before.Options) RunCommand(installed, "--option " + ProcessTools.Quote(setting.Key) + " " + ProcessTools.Quote(setting.Value));
            if (before.OwnedService)
            {
                ProcessTools.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop RustDesk", 30000).GetAwaiter().GetResult();
                RunCommand(Path.Combine(Environment.SystemDirectory, "sc.exe"), "config RustDesk start= disabled", 15000);
            }
            ProcessTools.Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "advfirewall firewall delete rule name=\"OwnDesk Private Remote\"", 15000).GetAwaiter().GetResult();
            string marker = Path.Combine(AppInfo.DataRoot, "host-ready.json"); if (File.Exists(marker)) File.Delete(marker);
        }
    }
}
