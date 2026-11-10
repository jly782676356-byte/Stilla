// Settings window: updates (automatic check, check now, go to the update page), donate, about.
// Opened from the right-click menu / tray menu -> "Settings...", or by clicking the small red dot to the right of the Stilla mark.

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
            Text = "Stilla Settings";
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

            // ---- Updates
            GroupBox gu = Group("Updates", 12, 10, 516, 208);
            auto = new CheckBox();
            auto.Text = "Check for updates automatically at every launch";
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
            check.Text = "Check now";
            check.Location = new Point(16, 102);
            check.Size = new Size(120, 30);
            check.Click += delegate { CheckNow(); };
            gu.Controls.Add(check);

            go = new Button();
            go.Text = "Go to update page";
            go.Location = new Point(146, 102);
            go.Size = new Size(140, 30);
            go.Click += delegate { OpenUpdatePage(); };
            gu.Controls.Add(go);

            note = new Label();
            note.Text = "Checking only asks GitHub for the latest version number: no data is sent and nothing is downloaded or installed. When a new version exists, a red dot appears beside the Stilla mark and the button opens its download page.";
            note.ForeColor = Color.DimGray;
            note.Location = new Point(16, 140);
            note.Size = new Size(484, 58);
            gu.Controls.Add(note);
            Controls.Add(gu);

            // ---- Donate
            GroupBox gd = Group("Support this project", 12, 226, 516, 92);
            Label ld = new Label();
            ld.Text = "Stilla is free and open source. If it helps you, you are welcome to support it.";
            ld.Location = new Point(16, 24);
            ld.Size = new Size(484, 34);
            gd.Controls.Add(ld);
            donate = new Button();
            donate.Text = "Donate...";
            donate.Location = new Point(16, 54);
            donate.Size = new Size(120, 28);
            donate.Click += delegate { OpenDonate(); };
            gd.Controls.Add(donate);
            Controls.Add(gd);

            // ---- About
            GroupBox ga = Group("About", 12, 326, 516, 90);
            Label lv = new Label();
            lv.Text = "Stilla " + AppInfo.Version + " - quota widget for Claude Code";
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
            folder.Text = "Open data folder";
            folder.Location = new Point(12, 436);
            folder.Size = new Size(140, 28);
            folder.Click += delegate { OpenFolder(); };
            Controls.Add(folder);

            close = new Button();
            close.Text = "Close";
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

        // Called on the UI thread: redraw the status line and buttons from UpdateState
        public void RefreshStatus()
        {
            check.Enabled = !UpdateState.Checking;
            status.ForeColor = SystemColors.ControlText;
            if (UpdateState.Checking)
            {
                status.Text = "Checking for updates...";
            }
            else if (UpdateState.Available)
            {
                status.Text = "New version " + UpdateState.Latest + " (current " + AppInfo.Version + ")";
                status.ForeColor = Color.FromArgb(200, 40, 50);
            }
            else if (!UpdateChecker.Enabled() && !AppInfo.Configured)
            {
                status.Text = "Current version " + AppInfo.Version + ". This is a development build and no update address is configured yet (run tools\\set-repo.ps1).";
            }
            else if (UpdateState.Last == UpdateResult.Failed)
            {
                status.Text = "Check failed: " + FailText(UpdateState.Detail) + " (current version " + AppInfo.Version + ")";
            }
            else if (UpdateState.Last == UpdateResult.UpToDate || UpdateState.CheckedUtc != DateTime.MinValue)
            {
                string when = UpdateState.CheckedUtc == DateTime.MinValue ? "" : " (checked " + UpdateState.CheckedUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + ")";
                status.Text = "Up to date: version " + AppInfo.Version + when;
            }
            else
            {
                status.Text = "Current version " + AppInfo.Version + ". ";
            }
        }

        private static string FailText(string detail)
        {
            if (detail == "network") return "cannot reach GitHub";
            if (detail == "rate-limit") return "GitHub is rate-limiting requests, try again later";
            if (detail == "bad-response") return "the response could not be understood";
            return "the server returned an error (" + detail + ")";
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
            status.Text = "Checking for updates...";
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
                    // The window has already been closed
                }
            });
        }

        private void OpenUpdatePage()
        {
            if (!AppInfo.Configured && Environment.GetEnvironmentVariable("STILLA_UPDATE_URL") == null)
            {
                MessageBox.Show(this, "This is a development build and the project's GitHub address is not configured yet, so it does not know which page to open.\r\nRun tools\\set-repo.ps1 -Owner your-username and rebuild.", "Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Open(UpdateState.PageUrl);
        }

        private void OpenDonate()
        {
            if (!AppInfo.Configured)
            {
                MessageBox.Show(this, "This is a development build and the project's GitHub address is not configured yet, so the donate entry is not available for now.", "Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    MessageBox.Show(this, "Could not open the browser; the address has been copied to the clipboard:\r\n" + url, "Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
