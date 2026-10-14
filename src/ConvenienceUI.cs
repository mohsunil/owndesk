using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace OwnDesk
{
    public sealed class ActivityTracker : IMessageFilter
    {
        readonly Form owner; readonly Action tick;
        public ActivityTracker(Form owner, Action tick) { this.owner = owner; this.tick = tick; }
        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg == 0x100 || message.Msg == 0x201 || message.Msg == 0x204 || message.Msg == 0x20A)
            {
                var control = Control.FromHandle(message.HWnd);
                if (control != null && control.FindForm() == owner) tick();
            }
            return false;
        }
    }
    public sealed class PairDialog : Form
    {
        public Device Result { get; private set; }
        public PairDialog()
        {
            Text = "配对另一台电脑"; ClientSize = new Size(520, 340); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; BackColor = Color.White; Font = Style.Font(10, false); AutoScaleMode = AutoScaleMode.Dpi;
            Controls.Add(Style.Text("从另一台电脑复制连接码", 16, true, Style.Ink, 26, 23, 466, 38));
            Controls.Add(Style.Text("另一台电脑安装后，点「复制连接码」，再粘贴到这里。", 10, false, Style.Muted, 27, 75, 465, 50));
            var code = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(28, 135), Size = new Size(464, 105), MaxLength = 4096 }; Controls.Add(code);
            var paste = Style.Button("粘贴连接码", false, 28, 270, 140, 39); paste.Click += delegate { if (Clipboard.ContainsText()) code.Text = Clipboard.GetText(); }; Controls.Add(paste);
            var save = Style.Button("完成配对", true, 352, 270, 140, 39); save.Click += delegate
            {
                try { Result = PairCode.Read(code.Text); DialogResult = DialogResult.OK; }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "检查连接码", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            Controls.Add(save); AcceptButton = save;
        }
    }
    public sealed class ScreenshotShelf : Form
    {
        readonly PictureBox picture;
        readonly Label message;
        string path;
        public ScreenshotShelf(Bitmap fixture = null)
        {
            Text = "QQ 截图助手 · 本机图片"; ClientSize = new Size(670, 510); StartPosition = FormStartPosition.CenterParent; Font = Style.Font(10, false); BackColor = Style.Paper; AutoScaleMode = AutoScaleMode.Dpi;
            Controls.Add(Style.Text("先用 QQ 截图，再点读取", 17, true, Style.Ink, 24, 20, 620, 36));
            message = Style.Text("当前支持读取图片、保存 PNG 和本机拖放；跨电脑网页直接拖放尚未完成。", 9, false, Style.Muted, 25, 66, 620, 45); Controls.Add(message);
            picture = new PictureBox { Location = new Point(26, 127), Size = new Size(618, 285), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle }; Controls.Add(picture);
            picture.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && path != null) { var data = new DataObject(DataFormats.FileDrop, new[] { path }); picture.DoDragDrop(data, DragDropEffects.Copy); }
            };
            var read = Style.Button("读取 QQ 截图", true, 26, 447, 160, 39); read.Click += delegate
            {
                try { if (!Clipboard.ContainsImage()) { message.Text = "剪贴板没有图片，请先用 QQ 完成一次截图。"; return; } using (var image = Clipboard.GetImage()) SetImage(new Bitmap(image)); }
                catch (Exception ex) { message.Text = ex.Message; }
            };
            var save = Style.Button("保存图片", false, 201, 447, 135, 39); save.Click += delegate
            {
                if (path == null) return;
                using (var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "QQ截图.png" }) if (dialog.ShowDialog(this) == DialogResult.OK) File.Copy(path, dialog.FileName, true);
            };
            Controls.Add(read); Controls.Add(save); if (fixture != null) SetImage(new Bitmap(fixture));
            FormClosed += delegate { if (picture.Image != null) picture.Image.Dispose(); };
        }
        void SetImage(Bitmap bitmap)
        {
            if (picture.Image != null) picture.Image.Dispose(); picture.Image = bitmap;
            string root = Path.Combine(AppInfo.DataRoot, "images"); Directory.CreateDirectory(root); path = Path.Combine(root, "capture-" + Guid.NewGuid().ToString("N") + ".png"); bitmap.Save(path, ImageFormat.Png);
            message.Text = "已读取图片，可保存或在本机拖入网页。远端支持图片粘贴的网页可使用 Ctrl+V；跨机直接拖放待实现。";
        }
    }
}
