using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace OwnDesk
{
    public static class UpdaterProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--setup-machine") return MachineSetup.Run(args[1]);
            if (args.Length == 2 && args[0] == "--uninstall") return Uninstall(args[1]);
            if (args.Length != 2 || args[0] != "--apply") return 2;
            UpdatePlan plan = null; UpdateRelease release = null; bool started = false; Process child = null;
            try
            {
                plan = AppInfo.Json().Deserialize<UpdatePlan>(File.ReadAllText(args[1]));
                if (plan == null || !Regex.IsMatch(plan.TransactionId ?? "", "^[a-f0-9]{32}$") || plan.ParentId <= 0) throw new InvalidDataException("更新任务不正确。");
                string target = Path.GetFullPath(plan.InstallRoot), manager = UpdatePolicy.Within(target, "OwnDesk.exe");
                if (AppInfo.FileHash(manager) != plan.ExpectedManagerHash || FileVersionInfo.GetVersionInfo(manager).ProductName != "OwnDesk") throw new InvalidDataException("待更新程序已改变。");
                release = UpdatePolicy.Verify(File.ReadAllBytes(Path.Combine(plan.WorkRoot, "latest.json")), UpdateTrust.PublicKeyXml);
                var current = FileVersionInfo.GetVersionInfo(manager);
                if (new Version(current.FileMajorPart, current.FileMinorPart, current.FileBuildPart, 0) >= UpdatePolicy.VersionOf(release.Version)) throw new InvalidDataException("已拒绝重复或降级更新。");
                string stage = Path.Combine(plan.WorkRoot, "verified-" + Guid.NewGuid().ToString("N"));
                UpdatePackage.Extract(Path.Combine(plan.WorkRoot, "package.zip"), stage, release);
                try
                {
                    using (var parent = Process.GetProcessById(plan.ParentId))
                    {
                        if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedTicks) throw new InvalidDataException("更新进程不匹配。");
                        if (!parent.WaitForExit(30000)) throw new TimeoutException("程序尚未退出，更新已取消。");
                    }
                }
                catch (ArgumentException) { }
                string transaction = UpdatePolicy.Within(target, ".owndesk-update/" + plan.TransactionId + "/journal.json");
                transaction = Path.GetDirectoryName(transaction); string health = Path.Combine(transaction, "health.json");
                started = true;
                UpdateTransaction.Apply(target, stage, transaction, release, () =>
                {
                    child = Process.Start(new ProcessStartInfo(manager, "--updated " + ProcessTools.Quote(plan.TransactionId)) { UseShellExecute = false, WorkingDirectory = target });
                    bool confirmed = false;
                    try
                    {
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed < TimeSpan.FromSeconds(25))
                    {
                        if (File.Exists(health))
                        {
                            string text = File.ReadAllText(health);
                            if (text.Contains(plan.TransactionId) && text.Contains(release.Version)) { confirmed = true; return true; }
                        }
                        if (child.HasExited) return false; Thread.Sleep(200);
                    }
                    return false;
                    }
                    finally { if (!confirmed && child != null && !child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
                });
                if (File.Exists(Path.Combine(target, "installed.marker"))) using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OwnDesk", true)) if (key != null) key.SetValue("DisplayVersion", release.Version);
                return 0;
            }
            catch (Exception ex)
            {
                if (child != null) { try { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } } catch { } }
                try
                {
                    var settings = UpdateSettings.Load(AppInfo.DataRoot); settings.FailedVersion = release == null ? "" : release.Version; settings.LastResult = "更新未完成，已保留或恢复旧版：" + ex.Message; settings.Save(AppInfo.DataRoot);
                    if (started && plan != null) Process.Start(new ProcessStartInfo(Path.Combine(plan.InstallRoot, "OwnDesk.exe"), "--update-rolled-back") { UseShellExecute = false, WorkingDirectory = plan.InstallRoot });
                }
                catch { }
                return 1;
            }
        }
        static void CheckRemovalTree(string root)
        {
            var pending = new System.Collections.Generic.Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("程序目录含有链接，已停止自动移除目录。");
                foreach (string entry in Directory.GetFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("程序目录含有链接，已停止自动移除目录。");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }
        static int Uninstall(string inputRoot)
        {
            try
            {
                string root = Path.GetFullPath(inputRoot).TrimEnd(Path.DirectorySeparatorChar);
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OwnDesk");
                if (!root.Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(root, "installed.marker")) || FileVersionInfo.GetVersionInfo(Path.Combine(root, "OwnDesk.exe")).ProductName != "OwnDesk") throw new InvalidDataException("卸载目录不正确。");
                CheckRemovalTree(root);
                try { using (var close = EventWaitHandle.OpenExisting(AppInfo.SetupShutdownEvent)) close.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                foreach (var process in Process.GetProcessesByName("OwnDesk")) using (process) if (process.MainModule.FileName.Equals(Path.Combine(root, "OwnDesk.exe"), StringComparison.OrdinalIgnoreCase) && !process.WaitForExit(10000)) throw new IOException("请先在托盘退出巫妖远控。");
                MachineSetup.RemoveHostConfiguration();
                using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (run != null) run.DeleteValue("OwnDesk", false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OwnDesk", false);
                foreach (string shortcut in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "巫妖远控.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "巫妖远控.lnk") }) if (File.Exists(shortcut)) File.Delete(shortcut);
                CheckRemovalTree(root);
                // Exact fixed per-user install root was checked above. User data is a separate directory.
                Directory.Delete(root, true); return 0;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "卸载未完成"); return 1; }
        }
    }
}
