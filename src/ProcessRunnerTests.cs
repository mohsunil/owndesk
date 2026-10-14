using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace OwnDesk
{
    public static class ProcessRunnerTests
    {
        public static int Fixture(string[] args)
        {
            if (args[1] == "hold") { if (args.Length == 3) File.WriteAllText(args[2], Process.GetCurrentProcess().Id.ToString()); Thread.Sleep(6000); return 0; }
            if (args[1] == "spawn")
            {
                Console.WriteLine("launcher-output"); Console.Error.WriteLine("launcher-error");
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--process-fixture hold " + ProcessTools.Quote(args[2])) { UseShellExecute = false, CreateNoWindow = true }); return 0;
            }
            if (args[1] == "flood") { for (int i = 0; i < 3000; i++) { Console.WriteLine("out-" + i); Console.Error.WriteLine("err-" + i); } return 0; }
            return 2;
        }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        public static void Register(Action<string, Action> test, string root)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            test("An interrupted own installation recovers pairing after its process exits without claiming unrelated services", () =>
            {
                string jobs = Path.Combine(root, "recovery-jobs"), job = Path.Combine(jobs, "old-job"), install = Path.Combine(root, "own-install"), engine = Path.Combine(root, "fixture-service.bin");
                Directory.CreateDirectory(job); File.WriteAllText(engine, "fixture");
                var request = new MachineSetupRequest { InstallRoot = install, UserSid = "fixture-own-user", EnableHost = true, ResultPath = Path.Combine(job, "result.json") };
                string record = Path.Combine(job, "request.json"); File.WriteAllText(record, AppInfo.Json().Serialize(request));
                DateTime began = DateTime.UtcNow.AddMinutes(-5); File.SetLastWriteTimeUtc(record, began); File.SetCreationTimeUtc(engine, began.AddSeconds(15));
                Require(SetupRecovery.OwnsUnfinishedInstall(jobs, request.UserSid, install, engine), "Exited interrupted setup was not recovered");
                Require(!SetupRecovery.OwnsUnfinishedInstall(jobs, "different-user", install, engine), "Other user's service was claimed");
                File.SetCreationTimeUtc(engine, began.AddHours(-1)); Require(!SetupRecovery.OwnsUnfinishedInstall(jobs, request.UserSid, install, engine), "Pre-existing service was claimed");
                File.SetCreationTimeUtc(engine, began.AddSeconds(15)); File.WriteAllText(request.ResultPath, "{}"); Require(!SetupRecovery.OwnsUnfinishedInstall(jobs, request.UserSid, install, engine), "Finished unrelated job was claimed");
            });
            test("A launcher exiting while its daemon inherits stdout cannot stall installation", () =>
            {
                string childFile = Path.Combine(root, "pipe-child.pid"); var watch = Stopwatch.StartNew();
                try
                {
                    var result = ProcessTools.Run(exe, "--process-fixture spawn " + ProcessTools.Quote(childFile), 1800).GetAwaiter().GetResult();
                    Require(watch.ElapsedMilliseconds < 3000 && result.Code == 0, "Launcher deadline was exceeded");
                    Require(result.Output.Contains("launcher-output") && result.Error.Contains("launcher-error"), "Launcher output was lost");
                    Require(File.Exists(childFile), "Persistent child was not launched");
                    using (var child = Process.GetProcessById(int.Parse(File.ReadAllText(childFile)))) Require(!child.HasExited, "Fixture child must still be alive when launcher returns");
                }
                finally
                {
                    if (File.Exists(childFile)) try { using (var child = Process.GetProcessById(int.Parse(File.ReadAllText(childFile)))) if (child.MainModule.FileName == exe) { child.Kill(); child.WaitForExit(2000); } } catch (ArgumentException) { }
                }
            });
            test("Component timeouts terminate the waited process within a bounded interval", () =>
            {
                string pid = Path.Combine(root, "timeout-child.pid"); var watch = Stopwatch.StartNew(); bool timedOut = false;
                try { ProcessTools.Run(exe, "--process-fixture hold " + ProcessTools.Quote(pid), 600).GetAwaiter().GetResult(); } catch (TimeoutException) { timedOut = true; }
                Require(timedOut && watch.ElapsedMilliseconds < 3000, "Component did not time out");
                if (File.Exists(pid)) try { using (var child = Process.GetProcessById(int.Parse(File.ReadAllText(pid)))) Require(child.HasExited, "Timed out process remained running"); } catch (ArgumentException) { }
            });
            test("Concurrent component stdout and stderr are drained without pipe buffer deadlock", () =>
            {
                var result = ProcessTools.Run(exe, "--process-fixture flood", 8000).GetAwaiter().GetResult();
                Require(result.Code == 0 && result.Output.Contains("out-2999") && result.Error.Contains("err-2999"), "Component output was truncated or blocked");
            });
        }
    }
}
