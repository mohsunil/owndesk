using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
namespace OwnDesk
{
    public static class SetupRecovery
    {
        public sealed class ProgressState { public string Message { get; set; } public string UpdatedUtc { get; set; } }
        public static void Report(string requestFile, string message)
        {
            string job = Path.GetDirectoryName(Path.GetFullPath(requestFile));
            if (File.Exists(Path.Combine(job, "cancel.request"))) throw new OperationCanceledException("安装已停止，请重新运行安装包重试。");
            AppInfo.AtomicWrite(Path.Combine(job, "progress.json"), AppInfo.Json().Serialize(new ProgressState { Message = message, UpdatedUtc = DateTime.UtcNow.ToString("o") }));
        }
        public static bool RetireLegacySetup(MachineSetupRequest current)
        {
            string enginePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
            bool recoveredOwner = OwnsUnfinishedInstall(Path.Combine(AppInfo.DataRoot, "setup"), current.UserSid, current.InstallRoot, enginePath);
            string expected = Path.Combine(current.InstallRoot, "OwnDesk.Update.exe");
            foreach (var process in Process.GetProcessesByName("OwnDesk.Update")) using (process)
            {
                if (process.Id == Process.GetCurrentProcess().Id) continue;
                try
                {
                    if (!process.MainModule.FileName.Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;
                    var info = FileVersionInfo.GetVersionInfo(expected);
                    if (info.ProductName != "OwnDesk" || new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) >= new Version(0, 2, 1)) continue;
                    using (var details = new ManagementObject("Win32_Process.Handle='" + process.Id + "'"))
                    {
                        details.Get(); string command = Convert.ToString(details["CommandLine"]);
                        var match = Regex.Match(command, "--setup-machine\\s+(?:\"([^\"]+)\"|(\\S+))\\s*$"); if (!match.Success) continue;
                        string previousFile = Path.GetFullPath(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
                        string jobs = Path.Combine(AppInfo.DataRoot, "setup") + Path.DirectorySeparatorChar;
                        if (!previousFile.StartsWith(jobs, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(previousFile) != "request.json") continue;
                        var previous = AppInfo.Json().Deserialize<MachineSetupRequest>(File.ReadAllText(previousFile));
                        if (previous == null || previous.UserSid != current.UserSid || !Path.GetFullPath(previous.InstallRoot).Equals(current.InstallRoot, StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(Path.Combine(Path.GetDirectoryName(previousFile), "result.json"))) continue;
                        string engine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
                        if (current.EnableHost && previous.EnableHost && File.Exists(engine))
                        {
                            DateTime created = File.GetCreationTimeUtc(engine), began = File.GetLastWriteTimeUtc(previousFile);
                            recoveredOwner |= created >= began.AddSeconds(-3) && created <= process.StartTime.ToUniversalTime().AddMinutes(2);
                        }
                        int parentId = Convert.ToInt32(details["ParentProcessId"]);
                        process.Kill(); if (!process.WaitForExit(5000)) throw new IOException("上次安装进程还未退出。");
                        try
                        {
                            using (var parent = Process.GetProcessById(parentId))
                            {
                                // Close only the exact v0.2.0 installer we published; never stop remote engines.
                                if (AppInfo.FileHash(parent.MainModule.FileName) == "fb82a4463de33d839772f4692802ba79f9ae52a8cb812c19ef8361e761c55be9") { parent.Kill(); parent.WaitForExit(3000); }
                            }
                        }
                        catch (ArgumentException) { }
                    }
                }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("无法结束上次未完成的安装，请关闭旧安装窗口后重试。"); }
            }
            return recoveredOwner;
        }
        public static bool OwnsUnfinishedInstall(string jobsRoot, string sid, string installRoot, string engine)
        {
            if (!Directory.Exists(jobsRoot) || !File.Exists(engine)) return false;
            DateTime created = File.GetCreationTimeUtc(engine);
            foreach (string job in Directory.GetDirectories(jobsRoot))
            {
                string file = Path.Combine(job, "request.json");
                if (!File.Exists(file) || File.Exists(Path.Combine(job, "result.json"))) continue;
                try
                {
                    var previous = AppInfo.Json().Deserialize<MachineSetupRequest>(File.ReadAllText(file));
                    if (previous == null || !previous.EnableHost || previous.UserSid != sid || !Path.GetFullPath(previous.InstallRoot).Equals(Path.GetFullPath(installRoot), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Path.GetFullPath(previous.ResultPath).Equals(Path.Combine(job, "result.json"), StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime began = File.GetLastWriteTimeUtc(file);
                    if (created >= began.AddSeconds(-3) && created <= began.AddMinutes(2)) return true;
                }
                catch (ArgumentException) { } catch (InvalidOperationException) { } catch (IOException) { }
            }
            return false;
        }
        public static void CopyStagedPayload(string requestFile, string target)
        {
            string stage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestFile)), "payload");
            if (!Directory.Exists(stage)) throw new IOException("安装任务缺少已校验的程序文件，请运行新版安装包。");
            new EngineManager(stage).Verify();
            var manager = FileVersionInfo.GetVersionInfo(Path.Combine(stage, "OwnDesk.exe"));
            if (manager.ProductName != "OwnDesk" || manager.FileVersion != AppInfo.Version + ".0") throw new IOException("安装文件版本不一致。");
            var directories = new System.Collections.Generic.Stack<string>(); directories.Push(stage); int count = 0;
            while (directories.Count > 0)
            {
                string directory = directories.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装来源含有链接。");
                foreach (string child in Directory.GetDirectories(directory)) directories.Push(child);
                foreach (string source in Directory.GetFiles(directory))
                {
                    if (++count > 200 || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装文件列表不正确。");
                    string relative = source.Substring(stage.Length + 1).Replace('\\', '/');
                    string destination = UpdatePolicy.Within(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".installing";
                    try { File.Copy(source, temporary); if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
        }
    }
}
