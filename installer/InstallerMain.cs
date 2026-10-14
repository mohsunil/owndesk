using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OwnDesk
{
    public static class InstallPaths
    {
        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OwnDesk"); } }
        public static string ShutdownEvent { get { return "Local\\OwnDesk-SetupClose-" + WindowsIdentity.GetCurrent().User.Value; } }
        public static void Shortcut(string path, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Type shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target) });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { AppInfo.ProductTitle });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
    public static class InstallerProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--verify-payload")
            {
                try { using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OwnDesk.Payload.zip")) using (var memory = new MemoryStream()) { stream.CopyTo(memory); var bytes = memory.ToArray(); if (AppInfo.Hash(bytes) != SetupPayloadTrust.Sha256) return 1; using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)) AppInfo.AtomicWrite(Path.GetFullPath(args[1]), AppInfo.Json().Serialize(new { valid = true, hash = SetupPayloadTrust.Sha256, files = zip.Entries.Count, version = AppInfo.Version })); } return 0; } catch { return 1; }
            }
            if (args.Length == 2 && args[0] == "--render-setup")
            {
                using (var form = new InstallForm(true)) { form.Show(); Application.DoEvents(); using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.GetFullPath(args[1])); } form.Close(); } return 0;
            }
            if (args.Length == 1 && args[0] == "--uninstall") { Application.Run(new UninstallForm()); return 0; }
            var installer = new InstallForm(false);
            if (args.Length == 1 && args[0] == "--repair-now") installer.StartRepair();
            Application.Run(installer); return 0;
        }
        public static async Task ExtractPayload(string root, Action<string> status)
        {
            status("正在准备程序文件…");
            await Task.Run(() =>
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OwnDesk.Payload.zip"))
                using (var memory = new MemoryStream())
                {
                    if (stream == null) throw new IOException("安装包缺少程序文件，请重新下载完整安装包。");
                    stream.CopyTo(memory); byte[] payload = memory.ToArray();
                    if (AppInfo.Hash(payload) != SetupPayloadTrust.Sha256) throw new IOException("安装包内容校验失败。");
                    using (var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read))
                    {
                        if (zip.Entries.Count > 200) throw new IOException("安装包文件数量不正确。");
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                        foreach (var entry in zip.Entries)
                        {
                            string name = entry.FullName;
                            if (!names.Add(name) || name.Contains("\\") || name.Contains(":") || name.Split('/').Any(x => x == ".." || x == "." || x.Length == 0)) throw new IOException("安装包路径不正确。");
                            total += entry.Length; if (total > 160L * 1024 * 1024) throw new IOException("安装包体积超限。");
                            string target = UpdatePolicy.Within(root, name); Directory.CreateDirectory(Path.GetDirectoryName(target));
                            string temp = target + ".installing";
                            using (var source = entry.Open()) using (var output = File.Create(temp)) source.CopyTo(output);
                            if (new FileInfo(temp).Length != entry.Length) throw new IOException("程序文件不完整。");
                            if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                        }
                    }
                }
            });
        }
    }
    public sealed class InstallForm : Form
    {
        readonly bool preview;
        readonly CheckBox host, autoStart;
        readonly Label state;
        readonly ProgressBar progress;
        readonly Button install;
        bool working, done, stopRequested;
        string activeJob;
        public InstallForm(bool preview)
        {
            this.preview = preview; Text = AppInfo.ProductTitle + " · 安装"; ClientSize = new Size(680, 530); FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; Font = Style.Font(10, false); BackColor = Color.White; AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var top = new Panel { Location = new Point(0, 0), Size = new Size(680, 158), BackColor = Color.FromArgb(23, 32, 51) };
            top.Controls.Add(Style.Text("巫妖的远程魔导控制系统", 22, true, Color.White, 27, 31, 620, 50));
            top.Controls.Add(Style.Text("安装一次，以后点电脑连接  ·  v" + AppInfo.Version, 10.5f, false, Color.FromArgb(177, 197, 229), 30, 100, 620, 30)); Controls.Add(top);
            Controls.Add(Style.Text("为你和家人的 Windows 电脑准备远程控制", 14, true, Style.Ink, 29, 184, 620, 36));
            Controls.Add(Style.Text("程序会准备所需组件、快捷方式和自动更新。\n首次使用仍需按提示登录个人专网，并用连接码配对。", 10, false, Style.Muted, 30, 233, 620, 60));
            host = new CheckBox { Text = "允许这台电脑在配对后被远程控制", Location = new Point(31, 314), Size = new Size(612, 30), Checked = true, Font = Style.Font(10, false) };
            autoStart = new CheckBox { Text = "开机时在托盘运行，保持自动更新", Location = new Point(31, 357), Size = new Size(612, 30), Checked = true, Font = Style.Font(10, false) };
            Controls.Add(host); Controls.Add(autoStart);
            state = Style.Text(preview ? "安装界面预览 · 此图未执行系统安装" : "安装时可能需要一次 Windows 管理员许可。", 9, false, Style.Muted, 31, 411, 612, 28); Controls.Add(state);
            progress = new ProgressBar { Location = new Point(31, 449), Size = new Size(425, 27), Style = ProgressBarStyle.Continuous }; Controls.Add(progress);
            install = Style.Button("安装", true, 476, 441, 172, 42); install.Click += async delegate { if (!preview) { if (working) RequestStop(); else if (done) { OpenManager(); Close(); } else await Install(); } }; Controls.Add(install);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) { e.Cancel = true; RequestStop(); } };
        }
        public void StartRepair() { Shown += async delegate { await Install(); }; }
        void RequestStop()
        {
            if (activeJob == null || stopRequested) return;
            stopRequested = true; AppInfo.AtomicWrite(Path.Combine(activeJob, "cancel.request"), DateTime.UtcNow.ToString("o"));
            state.Text = "正在停止安装，请等待当前系统步骤结束…"; install.Enabled = false;
        }
        void Status(string message) { if (InvokeRequired) BeginInvoke(new Action(() => state.Text = message)); else state.Text = message; }
        async Task Install()
        {
            working = true; stopRequested = false; install.Enabled = true; install.Text = "停止安装"; host.Enabled = false; autoStart.Enabled = false; progress.Style = ProgressBarStyle.Marquee;
            try
            {
                string root = InstallPaths.Root; Directory.CreateDirectory(root);
                try { using (var close = EventWaitHandle.OpenExisting(InstallPaths.ShutdownEvent)) close.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                foreach (var process in Process.GetProcessesByName("OwnDesk"))
                {
                    using (process)
                    {
                        try
                        {
                            string file = process.MainModule.FileName;
                            if (FileVersionInfo.GetVersionInfo(file).ProductName != "OwnDesk") continue;
                            process.CloseMainWindow(); bool exited = await Task.Run(() => process.WaitForExit(8000));
                            if (!exited) throw new IOException("请先完成远控管理器中的操作，然后在托盘退出管理器，再运行安装包。远控连接可以保持打开。");
                        }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                }
                string job = Path.Combine(AppInfo.DataRoot, "setup", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
                activeJob = job;
                await InstallerProgram.ExtractPayload(Path.Combine(job, "payload"), Status);
                if (stopRequested) throw new OperationCanceledException("安装已停止，可以重新点击安装。");
                Status("正在等待 Windows 管理员许可，请确认弹出的窗口…");
                string requestFile = Path.Combine(job, "request.json"), resultFile = Path.Combine(job, "result.json");
                AppInfo.AtomicWrite(requestFile, AppInfo.Json().Serialize(new MachineSetupRequest { InstallRoot = root, UserSid = WindowsIdentity.GetCurrent().User.Value, ResultPath = resultFile, EnableHost = host.Checked }));
                var helper = Process.Start(new ProcessStartInfo(Path.Combine(job, "payload", "OwnDesk.Update.exe"), "--setup-machine " + ProcessTools.Quote(requestFile)) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                var elapsed = Stopwatch.StartNew();
                using (helper)
                {
                    while (!await Task.Run(() => helper.WaitForExit(300)))
                    {
                        if (!stopRequested) try { string file = Path.Combine(job, "progress.json"); if (File.Exists(file)) { var update = AppInfo.Json().Deserialize<SetupRecovery.ProgressState>(File.ReadAllText(file)); if (update != null) Status(update.Message); } } catch (IOException) { }
                        if (elapsed.Elapsed > TimeSpan.FromMinutes(10) && !stopRequested) RequestStop();
                        if (elapsed.Elapsed > TimeSpan.FromMinutes(15)) throw new TimeoutException("安装超时，已请求停止。请保留此窗口信息以便修复。");
                    }
                }
                if (!File.Exists(resultFile)) throw new IOException("接入组件配置未完成，请重新运行安装包并允许 Windows 管理员许可。");
                var result = AppInfo.Json().Deserialize<MachineSetupResult>(File.ReadAllText(resultFile));
                if (result == null || !result.Success) throw new IOException(result == null ? "安装结果无法确认。" : result.Message);
                // New-session display defaults are user-owned; remote service credentials remain separate.
                new ConfigurationManager(AppInfo.ConfigRoot, AppInfo.DataRoot).Apply(QualityProfile.Get("office"), false);
                string manager = Path.Combine(root, "OwnDesk.exe");
                InstallPaths.Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "巫妖远控.lnk"), manager);
                InstallPaths.Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "巫妖远控.lnk"), manager);
                using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                { if (autoStart.Checked) run.SetValue("OwnDesk", ProcessTools.Quote(manager) + " --minimized"); else run.DeleteValue("OwnDesk", false); }
                string uninstaller = Path.Combine(root, "OwnDesk.Setup.exe");
                if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(Application.ExecutablePath, uninstaller, true);
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OwnDesk"))
                {
                    key.SetValue("DisplayName", AppInfo.ProductTitle); key.SetValue("DisplayVersion", AppInfo.Version); key.SetValue("InstallLocation", root); key.SetValue("Publisher", "OwnDesk contributors"); key.SetValue("DisplayIcon", manager); key.SetValue("UninstallString", ProcessTools.Quote(uninstaller) + " --uninstall"); key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                AppInfo.AtomicWrite(Path.Combine(root, "installed.marker"), AppInfo.Json().Serialize(new { product = "OwnDesk", userSid = WindowsIdentity.GetCurrent().User.Value, installedUtc = DateTime.UtcNow.ToString("o") }));
                done = true; progress.Style = ProgressBarStyle.Continuous; progress.Value = 100; install.Text = "打开巫妖远控"; state.Text = result.RestartSuggested ? "安装完成；专网组件建议稍后重启电脑。" : result.Message;
            }
            catch (Exception ex) { state.Text = ex is System.ComponentModel.Win32Exception ? "Windows 管理员许可未完成，请允许后重试安装。" : ex.Message; install.Text = "重试安装"; progress.Style = ProgressBarStyle.Continuous; host.Enabled = true; autoStart.Enabled = true; }
            finally { working = false; activeJob = null; install.Enabled = true; }
        }
        void OpenManager() { Process.Start(new ProcessStartInfo(Path.Combine(InstallPaths.Root, "OwnDesk.exe"), "--first-run") { UseShellExecute = false, WorkingDirectory = InstallPaths.Root }); }
    }
    public sealed class UninstallForm : Form
    {
        public UninstallForm()
        {
            Text = "卸载巫妖远控"; ClientSize = new Size(560, 270); StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White; Font = Style.Font(10, false);
            Controls.Add(Style.Text("移除巫妖远控管理器", 17, true, Style.Ink, 25, 24, 510, 40));
            Controls.Add(Style.Text("会移除程序、快捷方式和开机运行项，保留个人设备列表与配对备份。\n为停止本机远控接入，需要一次管理员许可。\n已安装的 Tailscale 和 RustDesk 组件可在 Windows 应用列表单独卸载。", 10, false, Style.Muted, 27, 80, 506, 100));
            var uninstall = Style.Button("卸载并停止本机接入", true, 279, 207, 255, 38); Controls.Add(uninstall);
            uninstall.Click += delegate
            {
                try
                {
                    string runnerRoot = Path.Combine(AppInfo.DataRoot, "uninstall", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(runnerRoot);
                    string runner = Path.Combine(runnerRoot, "OwnDesk.Update.exe"); File.Copy(Path.Combine(InstallPaths.Root, "OwnDesk.Update.exe"), runner);
                    Process.Start(new ProcessStartInfo(runner, "--uninstall " + ProcessTools.Quote(InstallPaths.Root)) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }); Close();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "卸载未完成"); }
            };
        }
    }
}
