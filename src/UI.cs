using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OwnDesk
{
    public static class Style
    {
        public static readonly Color Ink = Color.FromArgb(26, 39, 61);
        public static readonly Color Muted = Color.FromArgb(105, 119, 142);
        public static readonly Color Blue = Color.FromArgb(48, 104, 244);
        public static readonly Color Paper = Color.FromArgb(244, 247, 251);
        public static readonly Color Line = Color.FromArgb(225, 232, 242);
        public static readonly Color Green = Color.FromArgb(27, 139, 103);
        public static Font Font(float size, bool bold) { return new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
        public static Label Text(string text, float size, bool bold, Color color, int x, int y, int w, int h)
        {
            return new Label { Text = text, Font = Font(size, bold), ForeColor = color, Location = new Point(x, y), Size = new Size(w, h), AutoEllipsis = true, BackColor = Color.Transparent };
        }
        public static Button Button(string text, bool primary, int x, int y, int w, int h)
        {
            var button = new Button { Text = text, Font = Font(9.5f, primary), BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = primary ? Blue : Line;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(36, 87, 220) : Color.FromArgb(237, 242, 250);
            return button;
        }
        public static void DisposeChildren(Control control)
        {
            var children = control.Controls.Cast<Control>().ToArray(); control.Controls.Clear();
            foreach (var child in children) child.Dispose();
        }
    }
    public sealed class Surface : Panel
    {
        public Color BorderColor = Style.Line;
        public Surface() { BackColor = Color.White; DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); using (var p = new Pen(BorderColor)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }
    public sealed class ScreenMark : Control
    {
        public Color Accent = Style.Blue;
        public ScreenMark() { Size = new Size(50, 48); SetStyle(ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Accent, 2.3f))
            using (var b = new SolidBrush(Color.FromArgb(18, Accent)))
            {
                g.FillRectangle(b, 5, 5, 38, 27); g.DrawRectangle(p, 5, 5, 38, 27); g.DrawLine(p, 24, 33, 24, 40); g.DrawLine(p, 15, 41, 33, 41);
            }
        }
    }

    public sealed class DeviceDialog : Form
    {
        readonly TextBox nameBox, addressBox;
        readonly Device original;
        public Device Result { get; private set; }
        public DeviceDialog(Device device)
        {
            original = device; Text = device == null ? "添加自己的电脑" : "编辑电脑";
            ClientSize = new Size(460, 310); FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false; BackColor = Color.White; Font = Style.Font(10, false); AutoScaleMode = AutoScaleMode.Dpi;
            Controls.Add(Style.Text("给电脑起个名字", 10, true, Style.Ink, 26, 24, 390, 24));
            nameBox = new TextBox { Location = new Point(28, 55), Size = new Size(402, 28), MaxLength = 48, Text = device == null ? "" : device.Name };
            Controls.Add(nameBox);
            Controls.Add(Style.Text("专网 IPv4 地址", 10, true, Style.Ink, 26, 101, 390, 24));
            addressBox = new TextBox { Location = new Point(28, 132), Size = new Size(402, 28), MaxLength = 24, Text = device == null ? "" : device.Address };
            Controls.Add(addressBox);
            Controls.Add(Style.Text("从另一台电脑复制 100.x.x.x 地址。默认端口为 21118。\n连接口令会在 RustDesk 窗口中输入。", 9, false, Style.Muted, 27, 177, 404, 48));
            var save = Style.Button("保存电脑", true, 305, 252, 125, 36);
            var cancel = Style.Button("取消", false, 205, 252, 88, 36);
            save.Click += delegate
            {
                try
                {
                    Result = new Device { Id = original == null ? Guid.NewGuid().ToString("N") : original.Id, Name = nameBox.Text, Address = addressBox.Text, LastOpenedUtc = original == null ? "" : original.LastOpenedUtc, ProtectedSecret = original != null && original.Address == addressBox.Text.Trim() ? original.ProtectedSecret : "" };
                    Result.Validate(); DialogResult = DialogResult.OK;
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "检查填写内容", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            cancel.DialogResult = DialogResult.Cancel; Controls.Add(save); Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;
        }
    }
    public sealed class DiscoveryDialog : Form
    {
        readonly CheckedListBox list;
        readonly List<TailPeer> peers;
        public List<TailPeer> Selected { get; private set; }
        public DiscoveryDialog(TailState state, IEnumerable<Device> existing)
        {
            Text = "发现专网里的 Windows 电脑"; ClientSize = new Size(590, 395); StartPosition = FormStartPosition.CenterParent; Font = Style.Font(10, false); BackColor = Color.White; MinimizeBox = false; MaximizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog; AutoScaleMode = AutoScaleMode.Dpi;
            Controls.Add(Style.Text("选择要加入的电脑", 15, true, Style.Ink, 24, 22, 530, 34));
            Controls.Add(Style.Text("来自当前 Tailscale 专网；加入列表后仍需远端开启接入并验证口令。", 9, false, Style.Muted, 24, 64, 542, 28));
            peers = state.Peers.Where(x => string.Equals(x.OS, "windows", StringComparison.OrdinalIgnoreCase) && !existing.Any(d => Endpoint.Parse(d.Address).Host == x.Address)).ToList();
            list = new CheckedListBox { Location = new Point(26, 106), Size = new Size(538, 215), CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
            foreach (var p in peers) list.Items.Add((p.Online ? "[专网在线]  " : "[专网离线]  ") + p.Name + "    " + p.Address);
            Controls.Add(list);
            if (peers.Count == 0) Controls.Add(Style.Text("暂无可加入的新 Windows 电脑。\n在另一台电脑登录同一 Tailscale 账号后刷新。", 10, false, Style.Muted, 42, 145, 490, 65));
            var add = Style.Button("加入选中电脑", true, 414, 342, 150, 36); add.Enabled = peers.Count > 0;
            add.Click += delegate { Selected = list.CheckedIndices.Cast<int>().Select(i => peers[i]).ToList(); DialogResult = DialogResult.OK; };
            var cancel = Style.Button("取消", false, 307, 342, 95, 36); cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(add); Controls.Add(cancel); CancelButton = cancel;
        }
    }

    public sealed class MainForm : Form
    {
        readonly DeviceStore store;
        readonly TailscaleClient tailscale = new TailscaleClient();
        readonly EngineManager engine;
        readonly bool preview;
        TailState state = new TailState();
        readonly Panel body;
        readonly Label title, subtitle, badge, footer;
        readonly Button[] nav = new Button[4];
        readonly UpdateClient updates;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        DateTime lastInteraction = DateTime.UtcNow;
        bool allowExit, maintenanceBusy;
        Label updateStatus;
        ActivityTracker activityTracker;
        Label localIp, localDescription, deviceCount;
        FlowLayoutPanel cards;
        Panel empty;
        int page;
        Task networkRefresh;
        readonly Dictionary<string, string> statuses = new Dictionary<string, string>();
        readonly HashSet<string> testing = new HashSet<string>();
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        public MainForm(DeviceStore store, bool preview)
        {
            this.store = store; this.preview = preview; engine = new EngineManager(AppDomain.CurrentDomain.BaseDirectory); updates = new UpdateClient(store.Root);
            Text = AppInfo.ProductTitle + "  " + AppInfo.Version + (preview ? " · 界面预览" : "");
            ClientSize = new Size(1220, 820); MinimumSize = new Size(1100, 760); StartPosition = FormStartPosition.CenterScreen; Font = Style.Font(10, false); AutoScaleMode = AutoScaleMode.Dpi; BackColor = Style.Paper;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 194, BackColor = Color.FromArgb(23, 32, 51) };
            var mark = new ScreenMark { Location = new Point(24, 27), Accent = Color.FromArgb(124, 167, 255) }; sidebar.Controls.Add(mark);
            sidebar.Controls.Add(Style.Text("巫妖远控", 18, true, Color.White, 25, 84, 155, 38));
            sidebar.Controls.Add(Style.Text("远程魔导控制系统", 9, false, Color.FromArgb(152, 168, 192), 27, 126, 160, 24));
            string[] navNames = { "我的设备", "本机接入", "画质与帮助", "软件更新" };
            for (int i = 0; i < nav.Length; i++)
            {
                int index = i; nav[i] = Style.Button(navNames[i], false, 16, 197 + i * 53, 162, 43);
                nav[i].TextAlign = ContentAlignment.MiddleLeft; nav[i].Padding = new Padding(15, 0, 0, 0); nav[i].FlatAppearance.BorderSize = 0;
                nav[i].Click += delegate { SelectPage(index); }; sidebar.Controls.Add(nav[i]);
            }
            var bottom = Style.Text("自己的电脑，随时连接\nv" + AppInfo.Version + "  ·  试用版", 9, false, Color.FromArgb(152, 168, 192), 27, 729, 152, 57); bottom.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; sidebar.Controls.Add(bottom);
            var main = new Panel { Dock = DockStyle.Fill, Size = new Size(1026, 820), Padding = new Padding(30, 0, 30, 0) };
            var header = new Panel { Dock = DockStyle.Top, Size = new Size(966, 118) };
            title = Style.Text("我的电脑", 23, true, Style.Ink, 0, 27, 580, 44);
            subtitle = Style.Text("异地连接 · Windows 电脑 · 原始画面", 9.5f, false, Style.Muted, 2, 79, 610, 25);
            badge = Style.Text(preview ? "界面预览 · 示例数据" : "正在读取专网状态", 9, false, Style.Muted, 714, 39, 244, 29); badge.TextAlign = ContentAlignment.MiddleRight; badge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(badge);
            footer = Style.Text("准备就绪", 8.8f, false, Style.Muted, 0, 0, 800, 25); footer.Dock = DockStyle.Bottom; footer.Height = 40; footer.Padding = new Padding(0, 10, 0, 0);
            body = new Panel { Dock = DockStyle.Fill, Size = new Size(966, 662) };
            main.Controls.Add(body); main.Controls.Add(footer); main.Controls.Add(header); Controls.Add(main); Controls.Add(sidebar);
            SelectPage(0);
            if (preview)
            {
                state = new TailState { Backend = "Running", IPv4 = "100.64.10.10", Peers = new List<TailPeer> { new TailPeer { Name = "家中电脑（示例）", Address = "100.64.10.21", OS = "windows", Online = true }, new TailPeer { Name = "书房电脑（示例）", Address = "100.64.10.22", OS = "windows", Online = false } } };
                footer.Text = "界面预览使用示例数据；此图不代表已完成远控连接。"; UpdateNetwork();
            }
            else
            {
                Shown += async delegate { if (!string.IsNullOrEmpty(store.LoadNotice)) Info(store.LoadNotice); await RefreshNetwork(); };
                refreshTimer.Interval = 30000; refreshTimer.Tick += async delegate { await RefreshNetwork(); }; refreshTimer.Start();
                updateTimer.Interval = 15000; updateTimer.Tick += async delegate { updateTimer.Interval = 60000; await UpdateTick(); }; updateTimer.Start();
                var menu = new ContextMenuStrip(); menu.Items.Add("打开巫妖远控", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
                menu.Items.Add("退出管理器", null, delegate { allowExit = true; Close(); }); tray.Icon = Icon; tray.Text = "巫妖远控 · 自动更新已启用"; tray.ContextMenuStrip = menu; tray.Visible = true;
                tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
                Activated += delegate { lastInteraction = DateTime.UtcNow; };
                activityTracker = new ActivityTracker(this, () => lastInteraction = DateTime.UtcNow); Application.AddMessageFilter(activityTracker);
            }
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!preview && !allowExit && e.CloseReason == CloseReason.UserClosing && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installed.marker"))) { e.Cancel = true; Hide(); } };
            FormClosed += delegate { refreshTimer.Stop(); refreshTimer.Dispose(); updateTimer.Stop(); updateTimer.Dispose(); tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); if (activityTracker != null) Application.RemoveMessageFilter(activityTracker); };
        }
        void Info(string text) { MessageBox.Show(this, text, "巫妖远控", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        void Guard(Action action)
        {
            if (preview) return;
            try { action(); } catch (Exception ex) { Info(ex.Message); }
        }
        void Say(string text) { if (!IsDisposed) footer.Text = text; }
        void SelectPage(int index)
        {
            page = index; localIp = null; localDescription = null; cards = null; empty = null; deviceCount = null;
            updateStatus = null; lastInteraction = DateTime.UtcNow;
            Style.DisposeChildren(body);
            for (int i = 0; i < nav.Length; i++)
            {
                nav[i].BackColor = i == index ? Color.FromArgb(45, 65, 101) : Color.FromArgb(23, 32, 51);
                nav[i].ForeColor = i == index ? Color.White : Color.FromArgb(166, 182, 205);
                nav[i].FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 65, 101);
            }
            if (index == 0) BuildDevices(); else if (index == 1) BuildHost(); else if (index == 2) BuildHelp(); else BuildUpdates();
            UpdateNetwork();
        }
        void BuildDevices()
        {
            title.Text = "我的电脑"; subtitle.Text = "把几台自己的 Windows 电脑，放在一个地方管理";
            var local = new Surface { Dock = DockStyle.Top, Size = new Size(966, 128), BackColor = Color.FromArgb(235, 241, 255), BorderColor = Color.FromArgb(211, 224, 252) };
            local.Controls.Add(Style.Text("这台电脑", 9.5f, true, Style.Ink, 22, 18, 420, 24));
            localIp = Style.Text("读取中…", 21, true, Style.Blue, 20, 47, 580, 38); local.Controls.Add(localIp);
            localDescription = Style.Text("另一台电脑准备好后，可用这个地址连接本机。", 8.8f, false, Style.Muted, 22, 92, 620, 23); local.Controls.Add(localDescription);
            var copy = Style.Button("复制连接码", false, 685, 31, 120, 38); copy.Anchor = AnchorStyles.Top | AnchorStyles.Right; copy.Click += delegate { CopyPair(); };
            var prepare = Style.Button("设置本机接入", true, 820, 31, 124, 38); prepare.Anchor = AnchorStyles.Top | AnchorStyles.Right; prepare.Click += delegate { SelectPage(1); };
            local.Controls.Add(copy); local.Controls.Add(prepare);
            var name = Style.Button("给本机起名", false, 820, 80, 124, 32); name.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            name.Click += delegate { RenameComputer(null); }; local.Controls.Add(name);
            var toolbar = new Panel { Dock = DockStyle.Top, Size = new Size(966, 105) };
            deviceCount = Style.Text("已保存 0 台电脑", 13, true, Style.Ink, 0, 31, 270, 30); toolbar.Controls.Add(deviceCount);
            var add = Style.Button("+ 配对电脑", true, 806, 23, 137, 37); add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            add.Click += delegate { PairDevice(); };
            var discover = Style.Button("发现专网设备", false, 655, 23, 137, 37); discover.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            discover.Click += async delegate { await Discover(); };
            var refresh = Style.Button("刷新", false, 565, 23, 77, 37); refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            refresh.Click += async delegate { await RefreshNetwork(); };
            toolbar.Controls.Add(add); toolbar.Controls.Add(discover); toolbar.Controls.Add(refresh);
            var automatic = new CheckBox { Text = "自动发现同一专网的在线电脑", Location = new Point(2, 73), Size = new Size(390, 24), Checked = store.Data.AutoDiscover, Font = Style.Font(9, false), ForeColor = Style.Muted };
            automatic.CheckedChanged += delegate { Guard(() => { var next = store.Clone(); next.AutoDiscover = automatic.Checked; store.Commit(next); if (automatic.Checked) store.DiscoverOnline(state); RenderDevices(); }); };
            toolbar.Controls.Add(automatic);
            var grid = new Panel { Dock = DockStyle.Fill };
            cards = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(0), BackColor = Style.Paper };
            cards.SizeChanged += delegate { ResizeCards(); };
            empty = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var content = new Panel { Size = new Size(620, 260) };
            var screen = new ScreenMark { Location = new Point(280, 9), Size = new Size(70, 60) }; content.Controls.Add(screen);
            var emptyTitle = Style.Text("还没有加入电脑", 17, true, Style.Ink, 50, 84, 520, 36); emptyTitle.TextAlign = ContentAlignment.MiddleCenter; content.Controls.Add(emptyTitle);
            var emptyBody = Style.Text("在另一台电脑双击安装，首次登录后点「复制连接码」。\n把连接码粘贴到这里，配对一次，以后点电脑即可连接。", 10, false, Style.Muted, 30, 138, 560, 58); emptyBody.TextAlign = ContentAlignment.MiddleCenter; content.Controls.Add(emptyBody);
            var emptyAdd = Style.Button("粘贴连接码配对", true, 215, 211, 190, 40); emptyAdd.Click += delegate { PairDevice(); }; content.Controls.Add(emptyAdd);
            empty.Controls.Add(content); empty.Resize += delegate { content.Location = new Point(Math.Max(0, (empty.ClientSize.Width - content.Width) / 2), Math.Max(15, (empty.ClientSize.Height - content.Height) / 2 - 10)); };
            grid.Controls.Add(cards); grid.Controls.Add(empty); body.Controls.Add(grid); body.Controls.Add(toolbar); body.Controls.Add(local);
            RenderDevices();
        }
        void ResizeCards()
        {
            if (cards == null || cards.IsDisposed) return;
            int width = cards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10;
            int columns = width >= 720 ? 2 : 1;
            foreach (Control card in cards.Controls) card.Width = Math.Max(320, width / columns - 12);
        }
        void RenderDevices()
        {
            if (cards == null || cards.IsDisposed) return;
            cards.SuspendLayout(); Style.DisposeChildren(cards);
            var visible = store.Data.Devices.Where(x => !state.Ready || Endpoint.Parse(x.Address).Host != state.IPv4).ToList();
            foreach (var device in visible) cards.Controls.Add(DeviceCard(device));
            deviceCount.Text = "可查看 " + visible.Count + " 台电脑";
            empty.Visible = visible.Count == 0; cards.Visible = !empty.Visible;
            ResizeCards(); cards.ResumeLayout();
        }
        Control DeviceCard(Device device)
        {
            var card = new Surface { Size = new Size(450, 208), Margin = new Padding(0, 0, 12, 15) };
            var icon = new ScreenMark { Location = new Point(15, 18) }; card.Controls.Add(icon);
            var nameLabel = Style.Text(device.Name, 13, true, Style.Ink, 76, 21, card.Width - 170, 29); nameLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right; card.Controls.Add(nameLabel);
            var rename = Style.Button("改名", false, card.Width - 85, 20, 65, 31); rename.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            rename.Click += delegate { RenameComputer(device); }; card.Controls.Add(rename);
            card.Controls.Add(Style.Text(device.Address, 9.5f, false, Style.Muted, 78, 55, 290, 23));
            string status;
            if (!statuses.TryGetValue(device.Id, out status))
            {
                var peer = state.Peers.FirstOrDefault(x => x.Address == Endpoint.Parse(device.Address).Host);
                status = peer == null ? "远控端口尚未检测" : peer.Online ? "专网在线 · 远控尚未检测" : "专网离线 · 可稍后检测";
            }
            if (string.IsNullOrEmpty(device.ProtectedSecret) && !status.Contains("\n")) status += "\n首次控制需连接码配对或远端验证";
            card.Controls.Add(Style.Text(status, 8.8f, false, status.Contains("端口可达") ? Style.Green : Style.Muted, 22, 99, 405, 44));
            var connect = Style.Button("连接桌面", true, 22, 155, 136, 35); connect.Click += async delegate { await Connect(device, false); };
            var test = Style.Button(testing.Contains(device.Id) ? "检测中…" : "检测连接", false, 171, 155, 100, 35); test.Enabled = !testing.Contains(device.Id); test.Click += async delegate { await Test(device); };
            var more = Style.Button("更多", false, 285, 155, 70, 35);
            var menu = new ContextMenuStrip { Font = Style.Font(9, false) };
            menu.Items.Add("传输文件", null, async delegate { await Connect(device, true); });
            menu.Items.Add("复制地址", null, delegate { Guard(() => Clipboard.SetText(device.Address)); });
            menu.Items.Add("编辑电脑", null, delegate { AddDevice(device); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("从列表移除", null, delegate { Guard(() => { store.Remove(device.Id); RenderDevices(); Say("已从本地列表移除，远端电脑不受影响。"); }); });
            more.Click += delegate { menu.Show(more, new Point(0, more.Height)); };
            card.Disposed += delegate { menu.Dispose(); }; card.Controls.Add(connect); card.Controls.Add(test); card.Controls.Add(more);
            return card;
        }
        void RenameComputer(Device device)
        {
            if (preview) return;
            using (var dialog = new NameDialog(device == null ? DeviceNames.Local(store.Root) : device.Name, device == null))
                if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() =>
                {
                    if (device == null) { DeviceNames.SaveLocal(store.Root, dialog.Result); UpdateNetwork(); Say("本机名称已保存，新复制的连接码会使用这个名字。已有设备列表可点「改名」修改。"); }
                    else { DeviceNames.Rename(store, device, dialog.Result); RenderDevices(); Say("名称已保存。"); }
                });
        }
        void AddDevice(Device existing)
        {
            if (preview) return;
            using (var dialog = new DeviceDialog(existing)) if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() =>
            {
                var endpoint = Endpoint.Parse(dialog.Result.Address);
                if (endpoint.Host == state.IPv4) throw new ArgumentException("这是本机地址，请添加另一台电脑的地址。");
                store.Upsert(dialog.Result); RenderDevices(); Say("电脑已保存，连接时会由远端验证口令。");
            });
        }
        void PairDevice()
        {
            if (preview) return;
            using (var dialog = new PairDialog()) if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() => { if (Endpoint.Parse(dialog.Result.Address).Host == state.IPv4) throw new ArgumentException("请使用另一台电脑的连接码。"); var known = store.Data.Devices.FirstOrDefault(x => x.Address == dialog.Result.Address); if (known != null) { dialog.Result.Id = known.Id; dialog.Result.Name = known.Name; } store.Upsert(dialog.Result); RenderDevices(); Say("配对已保存，以后点「连接桌面」即可。"); });
        }
        void CopyPair()
        {
            Guard(() => { if (!state.Ready) throw new InvalidOperationException("先登录个人专网，再复制本机连接码。"); string secret = SecretStore.ConfiguredHostSecret(); if (secret.Length == 0) throw new InvalidOperationException("本机还没有安装版创建的配对设置。请运行安装包完成配置；已有 RustDesk 的电脑会沿用原密码。"); Clipboard.SetText(PairCode.Create(DeviceNames.Local(store.Root), state.IPv4, secret)); Say("连接码已复制。它允许控制本机，请只粘贴到你们自己的电脑。"); });
        }
        async Task Discover()
        {
            if (preview) return;
            await RefreshNetwork(); if (IsDisposed) return;
            if (!state.Ready) { Info("先让本机 Tailscale 登录并连接，再发现专网里的电脑。\n可在「本机接入」中打开官方下载页。"); return; }
            using (var dialog = new DiscoveryDialog(state, store.Data.Devices)) if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() =>
            {
                var next = store.Clone();
                foreach (var p in dialog.Selected) { next.Devices.Add(new Device { Name = string.IsNullOrWhiteSpace(p.Name) ? p.Address : p.Name.Substring(0, Math.Min(p.Name.Length, 48)), Address = p.Address }); next.HiddenAutoAddresses.RemoveAll(x => x == p.Address); }
                store.Commit(next); RenderDevices(); Say("已加入 " + dialog.Selected.Count + " 台电脑。");
            });
        }
        Task RefreshNetwork()
        {
            if (preview || IsDisposed) return Task.FromResult(0);
            if (networkRefresh != null && !networkRefresh.IsCompleted) return networkRefresh;
            networkRefresh = ReadNetwork(); return networkRefresh;
        }
        async Task ReadNetwork()
        {
            var current = await tailscale.Status(); if (IsDisposed) return; state = current;
            try { int added = store.DiscoverOnline(state); if (added > 0) Say("自动发现 " + added + " 台电脑；首次控制请用对方连接码配对。"); }
            catch (Exception ex) { Say("自动发现未保存：" + ex.Message); }
            UpdateNetwork();
        }
        void UpdateNetwork()
        {
            if (!preview) { badge.Text = state.Ready ? "●  专网已连接" : "●  专网尚未就绪"; badge.ForeColor = state.Ready ? Style.Green : Style.Muted; }
            if (localIp != null)
            {
                localIp.Text = preview ? "我的电脑（示例）" : DeviceNames.Local(store.Root);
                localDescription.Text = state.Ready ? "已连接个人专网 · 发现 " + state.Peers.Count(x => string.Equals(x.OS, "windows", StringComparison.OrdinalIgnoreCase)) + " 台 Windows 电脑 · 复制连接码可配对本机" : "在「本机接入」点登录，按提示完成首次登录。";
            }
            if (page == 0) RenderDevices();
        }
        void CopyIp() { Guard(() => { if (!state.Ready) throw new InvalidOperationException("专网还没连接，请先登录 Tailscale。"); Clipboard.SetText(state.IPv4); Say("本机专网地址已复制。"); }); }
        async Task<string> TestCore(Device device)
        {
            var endpoint = Endpoint.Parse(device.Address);
            if (endpoint.Host == state.IPv4) return "这是本机地址，请添加另一台电脑";
            if (endpoint.IsTailnet && !state.Ready) return "本机专网未连接";
            var tcp = Connectivity.Probe(endpoint.Host, endpoint.Port, 2300);
            var route = tailscale.Route(endpoint);
            await Task.WhenAll(tcp, route);
            return tcp.Result ? "端口可达 · " + route.Result + "\n连接后仍需验证口令" : "端口不可达 · " + route.Result + "\n检查远端接入、睡眠状态与防火墙";
        }
        async Task Test(Device device)
        {
            if (preview || !testing.Add(device.Id)) return;
            RenderDevices(); Say("正在检查远控端口和专网路径…");
            try { string result = await TestCore(device); if (!IsDisposed) statuses[device.Id] = "上次检测：" + result; }
            catch (Exception ex) { if (!IsDisposed) statuses[device.Id] = "检测未完成：" + ex.Message; }
            finally { testing.Remove(device.Id); if (!IsDisposed) { RenderDevices(); Say("检测只验证网络与端口；画面和口令需实际连接确认。"); } }
        }
        async Task Connect(Device device, bool files)
        {
            if (preview) return;
            try
            {
                var endpoint = Endpoint.Parse(device.Address);
                if (endpoint.Host == state.IPv4) throw new InvalidOperationException("这是本机地址，请连接另一台电脑。");
                if (endpoint.IsTailnet)
                {
                    await RefreshNetwork(); if (IsDisposed) return;
                    if (!state.Ready) throw new InvalidOperationException("本机 Tailscale 尚未连接，请先在托盘中登录并连接。");
                }
                engine.ConnectPaired(endpoint, files, SecretStore.Reveal(device.ProtectedSecret));
                var next = store.Clone(); var record = next.Devices.FirstOrDefault(x => x.Id == device.Id); if (record != null) { record.LastOpenedUtc = DateTime.UtcNow.ToString("o"); store.Commit(next); }
                Say("已交给 RustDesk 打开" + (files ? "文件传输" : "远程桌面") + "，请在连接窗口中验证口令。");
            }
            catch (Exception ex) { Info(ex.Message); }
        }
        Panel ScrollPage() { var panel = new Panel { Dock = DockStyle.Fill, Size = new Size(966, 662), AutoScroll = true }; body.Controls.Add(panel); return panel; }
        void BuildHost()
        {
            title.Text = "本机接入"; subtitle.Text = "安装版自动准备组件，首次登录后配对一次即可";
            var scroll = ScrollPage(); var surface = new Surface { Location = new Point(0, 0), Size = new Size(944, 568), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            scroll.Controls.Add(surface);
            surface.Controls.Add(Style.Text("让这台电脑可以被连接", 18, true, Style.Ink, 25, 23, 860, 36));
            surface.Controls.Add(Style.Text("你和家人各自连接自己的电脑；两个连接可以分别使用。", 10, false, Style.Muted, 27, 71, 870, 29));
            string[] heads = { "每台电脑双击安装一次", "首次按提示登录", "复制连接码，配对自己的电脑" };
            string[] texts = {
                "安装器准备远控和专网组件，以及家庭专网接入。Windows 可能要求一次管理员许可。",
                "打开下方登录入口，按提示加入你们的个人专网。已有账号和 VPN 的设置会保留。",
                "在被控制电脑首页复制连接码，在控制电脑点「配对电脑」粘贴。以后直接点电脑连接。\n已有 RustDesk 的电脑沿用现有密码；电脑睡眠或关机时仍无法正常连接。"
            };
            for (int i = 0; i < 3; i++)
            {
                int y = 128 + i * 99;
                var number = Style.Text((i + 1).ToString("00"), 17, true, Style.Blue, 28, y, 54, 38); surface.Controls.Add(number);
                surface.Controls.Add(Style.Text(heads[i], 11, true, Style.Ink, 92, y + 2, 810, 27));
                surface.Controls.Add(Style.Text(texts[i], 9, false, Style.Muted, 92, y + 34, 810, 54));
            }
            var download = Style.Button("登录个人专网", false, 28, 452, 190, 40); download.Click += delegate { LoginNetwork(); };
            var apply = Style.Button("修复本机接入", true, 233, 452, 212, 40); apply.Click += async delegate { await RepairHost(); };
            var open = Style.Button("打开远控引擎", false, 460, 452, 154, 40); open.Click += delegate { Guard(() => engine.Open()); };
            surface.Controls.Add(download); surface.Controls.Add(apply); surface.Controls.Add(open);
            surface.Controls.Add(Style.Text("不同 VPN 的兼容性需分别检测。此软件不自动关闭 VPN 或调整它的规则。", 8.5f, false, Style.Muted, 28, 518, 875, 28));
        }
        async Task ApplyConfiguration(bool host)
        {
            if (preview) return;
            try
            {
                if (host) { await RefreshNetwork(); if (IsDisposed) return; if (!state.Ready) throw new InvalidOperationException("先让本机 Tailscale 登录并连接，再准备接入。"); }
                engine.Verify();
                if (EngineManager.Running()) throw new InvalidOperationException("RustDesk 正在运行。请先在它的托盘菜单选择「退出」，再应用设置。");
                if (host && EngineManager.Installed()) throw new InvalidOperationException("检测到已安装的 RustDesk。请直接在其设置中开启 IP 直连、端口 21118，并将 IP 白名单设为 100.64.0.0/10。\n画质可在「设置 → 显示」中调整。");
                string backup = new ConfigurationManager(AppInfo.ConfigRoot, store.Root).Apply(QualityProfile.Get(store.Data.Profile), host);
                Say(host ? "本机接入设置已写入；请在引擎中设置密码并检查防火墙。" : "新连接的画质默认值已应用；已有连接可在会话工具栏中调整。");
                engine.Open();
                if (host) Info("接入配置已应用，RustDesk 已打开。\n\n接下来在引擎「设置 → 安全」中设置固定密码。\n如需开机后也能连接，可在引擎界面安装服务。\n\n在另一台电脑添加本机专网地址：" + state.IPv4 + "\n口令只在 RustDesk 中设置和输入。");
            }
            catch (Exception ex) { Info(ex.Message); }
        }
        void BuildHelp()
        {
            title.Text = "画质与帮助"; subtitle.Text = "原始画面清晰度，配合适合当前网络的帧率";
            var scroll = ScrollPage();
            var quality = new Surface { Location = new Point(0, 0), Size = new Size(944, 284), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right }; scroll.Controls.Add(quality);
            quality.Controls.Add(Style.Text("新连接的默认画质", 16, true, Style.Ink, 25, 22, 860, 33));
            var description = Style.Text(QualityProfile.Get(store.Data.Profile).Description, 10, false, Style.Muted, 28, 128, 860, 57); quality.Controls.Add(description);
            for (int index = 0; index < QualityProfile.All.Length; index++)
            {
                var profile = QualityProfile.All[index];
                var choice = new RadioButton { Text = profile.Name, Location = new Point(28 + index * 185, 78), Size = new Size(170, 30), Font = Style.Font(10, false), Checked = store.Data.Profile == profile.Id, ForeColor = Style.Ink };
                choice.CheckedChanged += delegate
                {
                    if (!choice.Checked) return; description.Text = profile.Description;
                    Guard(() => { var next = store.Clone(); next.Profile = profile.Id; store.Commit(next); Say("预设已选中；点击应用后写入引擎默认设置。"); });
                };
                quality.Controls.Add(choice);
            }
            var apply = Style.Button("应用画质并打开引擎", true, 28, 217, 212, 39); apply.Click += async delegate { await ApplyConfiguration(false); }; quality.Controls.Add(apply);
            quality.Controls.Add(Style.Text("需先退出引擎；已有会话的单独设置可能覆盖默认值。", 9, false, Style.Muted, 260, 226, 650, 26));
            var help = new Surface { Location = new Point(0, 301), Size = new Size(944, 275), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right }; scroll.Controls.Add(help);
            help.Controls.Add(Style.Text("连接质量与维护", 15, true, Style.Ink, 25, 20, 860, 32));
            help.Controls.Add(Style.Text("2K / 4K 会随远端显示器使用原始分辨率；实际帧率取决于上传带宽和编码能力。\n「检测连接」可区分专网直连与中继；会话中的画质、分辨率和统计信息由引擎显示。", 9.5f, false, Style.Muted, 28, 65, 875, 57));
            var guide = Style.Button("查看使用说明", false, 28, 141, 150, 37); guide.Click += delegate { Guard(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "使用说明.html")) { UseShellExecute = true })); };
            var export = Style.Button("导出电脑列表", false, 191, 141, 150, 37); export.Click += delegate { ExportDevices(); };
            var import = Style.Button("导入电脑列表", false, 354, 141, 150, 37); import.Click += delegate { ImportDevices(); };
            var restore = Style.Button("恢复上次配置", false, 517, 141, 150, 37); restore.Click += delegate { Guard(() => { if (EngineManager.Running()) throw new InvalidOperationException("请先从 RustDesk 托盘退出引擎。"); new ConfigurationManager(AppInfo.ConfigRoot, store.Root).Restore(); Say("已恢复最近一次配置修改前的状态。"); }); };
            help.Controls.Add(guide); help.Controls.Add(export); help.Controls.Add(import); help.Controls.Add(restore);
            var data = Style.Button("打开本地数据", false, 28, 201, 150, 37); data.Click += delegate { Guard(() => { Directory.CreateDirectory(store.Root); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(store.Root) { UseShellExecute = true }); }); }; help.Controls.Add(data);
            help.Controls.Add(Style.Text("远控引擎：RustDesk " + AppInfo.EngineVersion + "（官方原版）\n本版提供设备管理与接入配置，连接窗口保留引擎界面。", 8.8f, false, Style.Muted, 195, 201, 690, 49));
            var screenshot = Style.Button("QQ 截图助手", false, 695, 141, 175, 37); screenshot.Click += delegate { if (!preview) new ScreenshotShelf().Show(this); }; help.Controls.Add(screenshot);
        }
        void LoginNetwork()
        {
            Guard(() => { string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale-ipn.exe"); if (File.Exists(exe)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }); else OpenUrl("https://tailscale.com/download/windows"); Say("请在专网窗口按提示登录，完成后本页面会自动刷新。"); });
        }
        async Task RepairHost()
        {
            if (preview || maintenanceBusy) return; maintenanceBusy = true;
            try
            {
                string root = AppDomain.CurrentDomain.BaseDirectory, work = Path.Combine(store.Root, "setup", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
                string resultPath = Path.Combine(work, "result.json"), requestPath = Path.Combine(work, "request.json");
                AppInfo.AtomicWrite(requestPath, AppInfo.Json().Serialize(new MachineSetupRequest { InstallRoot = root, UserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, ResultPath = resultPath, EnableHost = true }));
                Say("正在修复本机接入，Windows 将请求管理员许可…");
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "OwnDesk.Update.exe"), "--setup-machine " + ProcessTools.Quote(requestPath)) { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
                await Task.Run(() => process.WaitForExit());
                if (!File.Exists(resultPath)) throw new IOException("接入修复未完成，请重新运行安装包。");
                var result = AppInfo.Json().Deserialize<MachineSetupResult>(File.ReadAllText(resultPath)); if (!result.Success) throw new IOException(result.Message);
                Say(result.Message); await RefreshNetwork();
            }
            catch (Exception ex) { Info(ex.Message); }
            finally { maintenanceBusy = false; }
        }
        void BuildUpdates()
        {
            title.Text = "软件更新"; subtitle.Text = "后台检查，空闲时安装；更新管理器时保留正在运行的远控连接";
            var scroll = ScrollPage(); var surface = new Surface { Location = new Point(0, 0), Size = new Size(944, 560), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right }; scroll.Controls.Add(surface);
            surface.Controls.Add(Style.Text("当前版本  v" + AppInfo.Version, 19, true, Style.Ink, 27, 27, 870, 42));
            surface.Controls.Add(Style.Text("项目稳定更新源 · 更新签名与文件完整性校验", 10, false, Style.Muted, 29, 86, 870, 27));
            var check = new CheckBox { Text = "自动检查新版本（启动后及每 6 小时）", Location = new Point(29, 145), Size = new Size(860, 32), Checked = updates.Settings.AutoCheck, Font = Style.Font(10, false) };
            var install = new CheckBox { Text = "下载后在空闲时自动安装并重新打开", Location = new Point(29, 193), Size = new Size(860, 32), Checked = updates.Settings.AutoInstall, Font = Style.Font(10, false) };
            check.CheckedChanged += delegate { Guard(() => { updates.Settings.AutoCheck = check.Checked; updates.Settings.Save(store.Root); }); };
            install.CheckedChanged += delegate { Guard(() => { updates.Settings.AutoInstall = install.Checked; updates.Settings.Save(store.Root); }); };
            surface.Controls.Add(check); surface.Controls.Add(install);
            updateStatus = Style.Text(updates.Settings.LastResult, 11, true, Style.Blue, 29, 264, 870, 70); surface.Controls.Add(updateStatus);
            var runCheck = Style.Button("立即检查更新", true, 29, 367, 175, 40); runCheck.Click += async delegate { if (!preview) await updates.Check(true, UpdateMessage); }; surface.Controls.Add(runCheck);
            var apply = Style.Button("安装已下载新版", false, 219, 367, 175, 40); apply.Click += delegate { InstallUpdate(); }; surface.Controls.Add(apply);
            var site = Style.Button("查看更新说明", false, 409, 367, 175, 40); site.Click += delegate { OpenUrl(UpdateTrust.ReleasesPage); }; surface.Controls.Add(site);
            surface.Controls.Add(Style.Text("安装失败会尝试恢复旧版。设备列表、配对口令和远控设置保存在独立位置。\n更新源暂不可用时保留现有版本；软件未运行期间不会检查更新。\n此自动更新模块更新管理器；远控引擎由其官方更新机制维护。", 9.5f, false, Style.Muted, 29, 449, 870, 85));
        }
        void UpdateMessage(string text) { if (IsDisposed) return; Say(text); if (updateStatus != null && !updateStatus.IsDisposed) updateStatus.Text = text; }
        async Task UpdateTick()
        {
            if (preview || maintenanceBusy || IsDisposed) return;
            if (updates.Pending != null && updates.Settings.AutoInstall && OwnedForms.Length == 0 && testing.Count == 0 && (!Visible || WindowState == FormWindowState.Minimized || DateTime.UtcNow - lastInteraction > TimeSpan.FromMinutes(2))) { InstallUpdate(); return; }
            if (updates.Pending == null && updates.Due()) await updates.Check(false, UpdateMessage);
        }
        void InstallUpdate()
        {
            if (preview || maintenanceBusy || updates.Busy) return;
            Guard(() => { if (OwnedForms.Length > 0) throw new InvalidOperationException("请先完成当前窗口中的操作。"); updates.StartInstall(AppDomain.CurrentDomain.BaseDirectory); allowExit = true; Close(); });
        }
        protected override void OnMouseDown(MouseEventArgs e) { lastInteraction = DateTime.UtcNow; base.OnMouseDown(e); }
        protected override void OnKeyDown(KeyEventArgs e) { lastInteraction = DateTime.UtcNow; base.OnKeyDown(e); }
        public void StartMinimized() { Shown += delegate { Hide(); }; }
        public void CloseForSetup() { if (OwnedForms.Length > 0 || maintenanceBusy || updates.Busy) return; allowExit = true; Close(); }
        void ExportDevices()
        {
            if (preview) return;
            using (var dialog = new SaveFileDialog { Filter = "设备列表 (*.json)|*.json", FileName = "我的电脑.json", OverwritePrompt = true }) if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() => { store.Export(dialog.FileName); Say("电脑列表已导出，内容包含名称与地址。"); });
        }
        void ImportDevices()
        {
            if (preview) return;
            using (var dialog = new OpenFileDialog { Filter = "设备列表 (*.json)|*.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() => { int added = store.Import(dialog.FileName); Say("已导入 " + added + " 台电脑，重复地址自动跳过。"); });
        }
        void OpenUrl(string url) { Guard(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })); }
        public void Render(string path, int selectedPage)
        {
            SelectPage(selectedPage); Show(); Application.DoEvents(); PerformLayout(); Application.DoEvents();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
            Close();
        }
    }
}
