using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace OwnDesk
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--process-fixture") return ProcessRunnerTests.Fixture(args);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 3 && args[0] == "--verify-update")
            {
                try { var release = UpdatePolicy.Verify(File.ReadAllBytes(args[1]), UpdateTrust.PublicKeyXml); AppInfo.AtomicWrite(Path.GetFullPath(args[2]), AppInfo.Json().Serialize(new { valid = true, version = release.Version, files = release.Files.Count, packageSha256 = release.Sha256 })); return 0; }
                catch (Exception ex) { AppInfo.AtomicWrite(Path.GetFullPath(args[2]), AppInfo.Json().Serialize(new { valid = false, error = ex.Message })); return 1; }
            }
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(Path.GetFullPath(args[1]));
            if (args.Length >= 2 && args[0] == "--render")
            {
                string root = Path.Combine(Path.GetTempPath(), "OwnDesk-preview-" + Guid.NewGuid().ToString("N"));
                var store = new DeviceStore(root);
                if (args.Contains("--devices"))
                {
                    store.Data.Devices.Add(new Device { Name = "家中电脑（示例）", Address = "100.64.10.21" });
                    store.Data.Devices.Add(new Device { Name = "书房电脑（示例）", Address = "100.64.10.22" });
                }
                int page = args.Contains("--host") ? 1 : args.Contains("--help") ? 2 : args.Contains("--updates") ? 3 : 0;
                using (var form = new MainForm(store, true)) form.Render(Path.GetFullPath(args[1]), page);
                return 0;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\OwnDesk-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, out created))
            {
                if (!created) { MessageBox.Show("巫妖远控已在运行，请从任务栏或托盘打开它。", AppInfo.ProductTitle); return 0; }
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, AppInfo.ProductTitle); };
                try
                {
                    var form = new MainForm(new DeviceStore(AppInfo.DataRoot), false);
                    if (args.Contains("--minimized")) form.StartMinimized();
                    if (args.Length == 2 && args[0] == "--updated")
                    {
                        string id = args[1]; if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$")) return 2;
                        form.Shown += delegate { new EngineManager(AppDomain.CurrentDomain.BaseDirectory).Verify(); string path = UpdatePolicy.Within(AppDomain.CurrentDomain.BaseDirectory, ".owndesk-update/" + id + "/health.json"); AppInfo.AtomicWrite(path, AppInfo.Json().Serialize(new { transaction = id, version = AppInfo.Version, healthy = true })); };
                    }
                    if (args.Contains("--first-run")) form.Shown += async delegate
                    {
                        var state = await new TailscaleClient().Status();
                        if (!state.Ready)
                        {
                            string login = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale-ipn.exe");
                            if (File.Exists(login)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(login) { UseShellExecute = false });
                        }
                    };
                    using (var setupClose = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.SetupShutdownEvent))
                    {
                        var registration = ThreadPool.RegisterWaitForSingleObject(setupClose, delegate { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(new Action(form.CloseForSetup)); }, null, -1, false);
                        try { Application.Run(form); } finally { registration.Unregister(null); }
                    }
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "巫妖远控启动失败"); return 1; }
            }
            return 0;
        }
    }
}
