// 设置窗口：更新（自动检查、立即检查、前往更新页面）、捐赠、关于。
// 右键菜单 / 托盘菜单 →「设置…」，或点 Stilla 标志右边的小红点打开。

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Stilla
{
    internal sealed class SettingsForm : Form
    {
        private readonly Action changed;
        private CheckBox auto;
        private Label status, note;
        private Button check, go, donate, folder, close;

        public SettingsForm(Action onChanged)
        {
            changed = onChanged;
            Text = "Stilla 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(540, 478);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
            }

            // ---- 更新
            GroupBox gu = Group("更新", 12, 10, 516, 208);
            auto = new CheckBox();
            auto.Text = "每次启动时自动检查更新";
            auto.Checked = UpdateState.Auto;
            auto.Location = new Point(16, 26);
            auto.Size = new Size(480, 22);
            auto.CheckedChanged += delegate
            {
                UpdateState.Auto = auto.Checked;
                changed();
            };
            gu.Controls.Add(auto);

            status = new Label();
            status.Location = new Point(16, 56);
            status.Size = new Size(484, 42);
            status.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            gu.Controls.Add(status);

            check = new Button();
            check.Text = "检查更新";
            check.Location = new Point(16, 102);
            check.Size = new Size(120, 30);
            check.Click += delegate { CheckNow(); };
            gu.Controls.Add(check);

            go = new Button();
            go.Text = "前往更新页面";
            go.Location = new Point(146, 102);
            go.Size = new Size(140, 30);
            go.Click += delegate { OpenUpdatePage(); };
            gu.Controls.Add(go);

            note = new Label();
            note.Text = "检查更新只会向 GitHub 请求一次「最新版本号」，不会发送你的任何数据，也不会自动下载或安装：有新版本时 Stilla 标志右边会出现一个小红点，点「前往更新页面」在浏览器里打开下载页。";
            note.ForeColor = Color.DimGray;
            note.Location = new Point(16, 140);
            note.Size = new Size(484, 58);
            gu.Controls.Add(note);
            Controls.Add(gu);

            // ---- 捐赠
            GroupBox gd = Group("支持这个项目", 12, 226, 516, 92);
            Label ld = new Label();
            ld.Text = "Stilla 是免费开源的个人项目。如果它对你有帮助，欢迎支持作者继续维护。";
            ld.Location = new Point(16, 24);
            ld.Size = new Size(484, 34);
            gd.Controls.Add(ld);
            donate = new Button();
            donate.Text = "捐赠…";
            donate.Location = new Point(16, 54);
            donate.Size = new Size(120, 28);
            donate.Click += delegate { OpenDonate(); };
            gd.Controls.Add(donate);
            Controls.Add(gd);

            // ---- 关于
            GroupBox ga = Group("关于", 12, 326, 516, 90);
            Label lv = new Label();
            lv.Text = "Stilla " + AppInfo.Version + " · 给 Claude Code 用的额度悬浮窗";
            lv.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            lv.Location = new Point(16, 22);
            lv.Size = new Size(484, 20);
            ga.Controls.Add(lv);
            Label lc = new Label();
            lc.Text = AppInfo.Disclaimer;
            lc.ForeColor = Color.DimGray;
            lc.Location = new Point(16, 44);
            lc.Size = new Size(484, 40);
            ga.Controls.Add(lc);
            Controls.Add(ga);

            folder = new Button();
            folder.Text = "打开数据文件夹";
            folder.Location = new Point(12, 436);
            folder.Size = new Size(140, 28);
            folder.Click += delegate { OpenFolder(); };
            Controls.Add(folder);

            close = new Button();
            close.Text = "关闭";
            close.Location = new Point(448, 436);
            close.Size = new Size(80, 28);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;

            RefreshStatus();
        }

        private GroupBox Group(string title, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Location = new Point(x, y);
            g.Size = new Size(w, h);
            return g;
        }

        // 界面线程里调用：按 UpdateState 重画状态行和按钮
        public void RefreshStatus()
        {
            check.Enabled = !UpdateState.Checking;
            status.ForeColor = SystemColors.ControlText;
            if (UpdateState.Checking)
            {
                status.Text = "正在检查更新…";
            }
            else if (UpdateState.Available)
            {
                status.Text = "有新版本 " + UpdateState.Latest + "（当前 " + AppInfo.Version + "）";
                status.ForeColor = Color.FromArgb(200, 40, 50);
            }
            else if (!UpdateChecker.Enabled() && !AppInfo.Configured)
            {
                status.Text = "当前版本 " + AppInfo.Version + "。这是开发版，还没有配置更新地址（运行 tools\\set-repo.ps1）。";
            }
            else if (UpdateState.Last == UpdateResult.Failed)
            {
                status.Text = "检查失败：" + FailText(UpdateState.Detail) + "（当前版本 " + AppInfo.Version + "）";
            }
            else if (UpdateState.Last == UpdateResult.UpToDate || UpdateState.CheckedUtc != DateTime.MinValue)
            {
                string when = UpdateState.CheckedUtc == DateTime.MinValue ? "" : "，" + UpdateState.CheckedUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 检查";
                status.Text = "已是最新版本 " + AppInfo.Version + when;
            }
            else
            {
                status.Text = "当前版本 " + AppInfo.Version + "。";
            }
        }

        private static string FailText(string detail)
        {
            if (detail == "network") return "连不上 GitHub";
            if (detail == "rate-limit") return "GitHub 请求太频繁，稍后再试";
            if (detail == "bad-response") return "返回的内容无法识别";
            return "服务器返回错误（" + detail + "）";
        }

        private void CheckNow()
        {
            if (UpdateState.Checking)
            {
                return;
            }
            if (!UpdateChecker.Enabled())
            {
                RefreshStatus();
                return;
            }
            check.Enabled = false;
            status.Text = "正在检查更新…";
            status.ForeColor = SystemColors.ControlText;
            UpdateChecker.CheckAsync(0, delegate
            {
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        changed();
                        RefreshStatus();
                    }));
                }
                catch (Exception)
                {
                    // 窗口已经关了
                }
            });
        }

        private void OpenUpdatePage()
        {
            if (!AppInfo.Configured && Environment.GetEnvironmentVariable("STILLA_UPDATE_URL") == null)
            {
                MessageBox.Show(this, "这是开发版，还没有配置项目的 GitHub 地址，所以不知道该打开哪个页面。\r\n运行 tools\\set-repo.ps1 -Owner 你的用户名 之后重新构建。", "Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Open(UpdateState.PageUrl);
        }

        private void OpenDonate()
        {
            if (!AppInfo.Configured)
            {
                MessageBox.Show(this, "这是开发版，还没有配置项目的 GitHub 地址，捐赠入口暂时不可用。", "Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Open(AppInfo.DonateUrl);
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Home);
                Process.Start("explorer.exe", "\"" + AppPaths.Home + "\"");
            }
            catch (Exception)
            {
            }
        }

        private void Open(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch (Exception)
            {
                try
                {
                    Clipboard.SetText(url);
                    MessageBox.Show(this, "没能打开浏览器，网址已复制到剪贴板：\r\n" + url, "Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
