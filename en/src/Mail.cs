// Email reminders: which sessions to watch and at how many minutes of cache left to send you an email.
//
// Settings are saved in %APPDATA%\Stilla\mail.txt (text):
//   enabled=1  host=  port=  ssl=1  user=  from=  to=  pass= (encrypted with Windows DPAPI: only your Windows account on this PC can decrypt it)
//   w=<session ID>|<list of minutes, e.g. 30,10>|<session title (for humans)>      one line per ticked session
// Mail is sent over SMTP (System.Net.Mail). Gmail / Outlook / QQ Mail need an "app password / authorization code", not the login password.
// An email is sent only when the cache time left of a ticked session drops to the minutes you set, once per minute value per cache cycle.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Stilla
{
    internal sealed class MailConfig
    {
        public string Host = "smtp.gmail.com";
        public int Port = 587;
        public bool Ssl = true;
        public string User = "", Pass = "", From = "", To = "";
    }

    internal sealed class MailWatch
    {
        public string Session = "";
        public string Title = "";
        public List<int> Mins = new List<int>();        // Largest first
    }

    // One row in the dialog: one session
    internal sealed class MailSessionRow
    {
        public string Session = "";
        public string Title = "";
        public string State = "";       // "23 min left", "Running", "Expired", ...
        public int TtlMin;              // Cache lifetime in minutes, 0 = not known yet
        public bool Live = true;        // False for sessions that are not in the current list (ticked earlier)
    }

    internal static class MailStore
    {
        public static bool Enabled;
        public static string Host = "smtp.gmail.com";
        public static int Port = 587;
        public static bool Ssl = true;
        public static string User = "", From = "", To = "";
        private static string passEnc = "";
        private static bool passChecked, passUsable;        // Whether the stored password can be decrypted under this Windows account (a file copied to another computer / account cannot be decrypted)
        public static readonly List<MailWatch> Watches = new List<MailWatch>();
        public static int Attempts;                     // How many emails have been sent (attempted)
        public static string LastStatus = "";           // Result of the most recent send (shown in the dialog)
        // The DPAPI extra entropy binds the password to server + user name: if host / user in mail.txt are changed (typo, edited in a synced folder, file copied elsewhere) the password can no longer be decrypted,
        // so it is never quietly sent to a different server. The password is encrypted after Host / User have been assigned (see MailForm.OnOk)
        private static byte[] Entropy
        {
            get { return Encoding.UTF8.GetBytes("Stilla.mail.v2|" + Host.Trim().ToLowerInvariant() + "|" + User.Trim().ToLowerInvariant()); }
        }

        private static string PathOf()
        {
            return Path.Combine(AppPaths.Home, "mail.txt");
        }

        public static string Password
        {
            get
            {
                if (passEnc.Length == 0)
                {
                    return "";
                }
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(passEnc), Entropy, DataProtectionScope.CurrentUser));
                }
                catch (Exception)
                {
                    return "";      // Windows account changed / file copied to another PC: it cannot be decrypted, so ask the user to enter it again
                }
            }
            set
            {
                passEnc = string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
                passChecked = false;
            }
        }

        // There is a usable password: the stored one can really be decrypted under this Windows account (cannot be decrypted = the file was copied from another computer / account or is damaged; treat it as none, so an empty password is not used to log in over and over)
        public static bool HasPassword
        {
            get
            {
                if (passEnc.Length == 0)
                {
                    return false;
                }
                if (!passChecked)
                {
                    passUsable = Password.Length > 0;
                    passChecked = true;
                }
                return passUsable;
            }
        }

        public static bool Configured
        {
            get { return Host.Length > 0 && User.Length > 0 && To.Length > 0 && HasPassword; }
        }

        public static MailConfig Snapshot()
        {
            MailConfig c = new MailConfig();
            c.Host = Host;
            c.Port = Port;
            c.Ssl = Ssl;
            c.User = User;
            c.Pass = Password;
            c.From = From.Length > 0 ? From : User;
            c.To = To;
            return c;
        }

        public static MailWatch Find(string session)
        {
            lock (Watches)
            {
                foreach (MailWatch w in Watches)
                {
                    if (w.Session == session)
                    {
                        return w;
                    }
                }
            }
            return null;
        }

        public static int WatchCount
        {
            get
            {
                lock (Watches)
                {
                    return Watches.Count;
                }
            }
        }

        // "30, 10; 5" -> [30, 10, 5] (1-600 minutes, de-duplicated, largest first); returns empty if none is valid
        public static List<int> ParseMins(string text)
        {
            List<int> r = new List<int>();
            if (text == null)
            {
                return r;
            }
            foreach (string part in text.Split(new char[] { ',', '，', ';', '；', ' ', '、', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 1 && v <= 600 && !r.Contains(v))
                {
                    r.Add(v);
                }
            }
            r.Sort(delegate(int a, int b) { return b.CompareTo(a); });
            return r;
        }

        public static string MinsText(List<int> mins)
        {
            List<string> s = new List<string>();
            foreach (int m in mins)
            {
                s.Add(m.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(",", s.ToArray());
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(PathOf()))
                {
                    return;
                }
                lock (Watches)
                {
                    Watches.Clear();
                    foreach (string line in File.ReadAllLines(PathOf(), Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                        {
                            continue;
                        }
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        int n;
                        if (k == "enabled") Enabled = v == "1";
                        else if (k == "host") Host = v;
                        else if (k == "port" && int.TryParse(v, out n) && n > 0 && n < 65536) Port = n;
                        else if (k == "ssl") Ssl = v != "0";
                        else if (k == "user") User = v;
                        else if (k == "from") From = v;
                        else if (k == "to") To = v;
                        else if (k == "pass")
                        {
                            passEnc = v;
                            passChecked = false;
                        }
                        else if (k == "w")
                        {
                            string[] p = v.Split('|');
                            if (p.Length >= 2 && p[0].Length > 0)
                            {
                                MailWatch w = new MailWatch();
                                w.Session = p[0];
                                w.Mins = ParseMins(p[1]);
                                w.Title = p.Length > 2 ? p[2] : "";
                                if (w.Mins.Count > 0)
                                {
                                    Watches.Add(w);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // mail.txt has one item per line: a line break in free text would split an item into several lines (even creating enabled= / w= lines out of nothing), so they are replaced by spaces before writing
        private static string OneLine(string s)
        {
            return (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
        }

        public static void Save()
        {
            try
            {
                string path = PathOf();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                lines.Add("enabled=" + (Enabled ? 1 : 0));
                lines.Add("host=" + OneLine(Host));
                lines.Add("port=" + Port);
                lines.Add("ssl=" + (Ssl ? 1 : 0));
                lines.Add("user=" + OneLine(User));
                lines.Add("from=" + OneLine(From));
                lines.Add("to=" + OneLine(To));
                lines.Add("pass=" + passEnc);
                lock (Watches)
                {
                    foreach (MailWatch w in Watches)
                    {
                        lines.Add("w=" + OneLine(w.Session).Replace('|', '/') + "|" + MinsText(w.Mins) + "|" + OneLine(w.Title).Replace('|', '/'));
                    }
                }
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }
    }

    internal static class MailSender
    {
        // Fuse: at most this many mails per hour (test mails included), after that nothing is sent. Normal use never gets near it; it only guards against a loop somewhere flooding the mailbox or getting the account banned as spam
        private const int MaxPerHour = 30;
        private static readonly Queue<DateTime> sentTimes = new Queue<DateTime>();

        private static bool Reserve()
        {
            lock (sentTimes)
            {
                DateTime now = DateTime.UtcNow;
                while (sentTimes.Count > 0 && now - sentTimes.Peek() > TimeSpan.FromHours(1))
                {
                    sentTimes.Dequeue();
                }
                if (sentTimes.Count >= MaxPerHour)
                {
                    return false;
                }
                sentTimes.Enqueue(now);
                return true;
            }
        }

        // A mail header must not contain line breaks / control characters (a session title comes from conversation content and may contain a line break): replace them with spaces
        private static string CleanHeader(string s)
        {
            if (s == null)
            {
                return "";
            }
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                sb.Append(ch < ' ' || ch == (char)0x7f || ch == (char)0x2028 || ch == (char)0x2029 ? ' ' : ch);
            }
            string r = sb.ToString().Trim();
            return r.Length > 200 ? r.Substring(0, 200) : r;
        }

        // Sends on a background thread; done(null) = success, otherwise an error description (called on the sending thread, so switch back to the UI thread yourself)
        public static void SendAsync(MailConfig c, string subject, string body, Action<string> done)
        {
            subject = CleanHeader(subject);
            bool allowed = Reserve();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try
                {
                    if (!allowed)
                    {
                        throw new InvalidOperationException("Already sent " + MaxPerHour + " mails in the last hour; pausing for now (to prevent flooding).");
                    }
                    using (SmtpClient smtp = new SmtpClient(c.Host, c.Port))
                    {
                        smtp.EnableSsl = c.Ssl;
                        smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                        smtp.UseDefaultCredentials = false;
                        smtp.Credentials = new NetworkCredential(c.User, c.Pass);
                        smtp.Timeout = 25000;
                        using (MailMessage m = new MailMessage())
                        {
                            m.From = new MailAddress(c.From.Length > 0 ? c.From : c.User);
                            foreach (string to in c.To.Split(new char[] { ',', ';', '，', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                m.To.Add(to.Trim());
                            }
                            m.Subject = subject;
                            m.SubjectEncoding = Encoding.UTF8;
                            m.Body = body;
                            m.BodyEncoding = Encoding.UTF8;
                            smtp.Send(m);
                        }
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    if (ex.InnerException != null)
                    {
                        err += " (" + ex.InnerException.Message + ")";
                    }
                }
                try
                {
                    if (done != null)
                    {
                        done(err);
                    }
                }
                catch (Exception)
                {
                }
            });
        }
    }

    // Settings dialog: mailbox + tick the sessions and how many minutes ahead to remind
    internal sealed class MailForm : Form
    {
        private readonly CheckBox enable = new CheckBox();
        private readonly ComboBox preset = new ComboBox();
        private readonly TextBox host = new TextBox(), port = new TextBox(), user = new TextBox(), pass = new TextBox(), from = new TextBox(), to = new TextBox();
        private readonly CheckBox ssl = new CheckBox();
        private readonly Label testState = new Label();
        private readonly DataGridView grid = new DataGridView();
        private readonly NumericUpDown defMin = new NumericUpDown();
        private readonly List<MailSessionRow> rows;
        private static readonly string[] PresetNames = { "Custom", "Gmail", "Outlook / Hotmail", "QQ Mail" };
        private static readonly string[] PresetHosts = { "", "smtp.gmail.com", "smtp-mail.outlook.com", "smtp.qq.com" };

        public bool ResultEnabled;
        public List<MailWatch> ResultWatches = new List<MailWatch>();

        private Label Lbl(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            Controls.Add(l);
            return l;
        }

        private void Place(Control c, int x, int y, int w)
        {
            c.Location = new Point(x, y);
            c.Width = w;
            Controls.Add(c);
        }

        public MailForm(List<MailSessionRow> sessionRows)
        {
            rows = sessionRows;
            Text = "Email reminders";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            DialogDpi.Apply(this);
            // the dialog is 700 px high at 100%; on a small screen / at 150% scaling it must still fit (the list in the middle and the buttons below follow H)
            int H = Math.Max(440, Math.Min(700, (int)(Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea.Height / DialogDpi.Factor(this)) - 80));
            ClientSize = new Size(760, H);

            enable.Text = "Enable email reminders (sends you an email when a ticked session's cache time left drops to the set minutes)";
            enable.AutoSize = true;
            enable.Location = new Point(14, 12);
            enable.Checked = MailStore.Enabled;
            Controls.Add(enable);

            Lbl("Email settings", 14, 42).Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            Lbl("Preset", 14, 70);
            preset.DropDownStyle = ComboBoxStyle.DropDownList;
            preset.Items.AddRange(PresetNames);
            preset.SelectedIndex = 0;
            Place(preset, 60, 66, 150);
            Lbl("Server", 230, 70);
            Place(host, 285, 66, 190);
            Lbl("Port", 490, 70);
            Place(port, 530, 66, 60);
            ssl.Text = "STARTTLS / SSL";
            ssl.AutoSize = true;
            ssl.Location = new Point(606, 68);
            Controls.Add(ssl);
            Lbl("User", 14, 102);
            Place(user, 76, 98, 224);
            Lbl("Password", 312, 102);
            pass.UseSystemPasswordChar = true;
            Place(pass, 376, 98, 214);
            Lbl("From", 14, 134);
            Place(from, 76, 130, 224);
            Lbl("To", 312, 134);
            Place(to, 376, 130, 214);

            host.Text = MailStore.Host;
            port.Text = MailStore.Port.ToString(CultureInfo.InvariantCulture);
            ssl.Checked = MailStore.Ssl;
            user.Text = MailStore.User;
            from.Text = MailStore.From;
            to.Text = MailStore.To;
            if (MailStore.HasPassword)
            {
                pass.Text = "";
            }
            for (int i = 1; i < PresetHosts.Length; i++)
            {
                if (string.Equals(MailStore.Host, PresetHosts[i], StringComparison.OrdinalIgnoreCase))
                {
                    preset.SelectedIndex = i;
                }
            }
            preset.SelectedIndexChanged += delegate
            {
                int i = preset.SelectedIndex;
                if (i > 0)
                {
                    host.Text = PresetHosts[i];
                    port.Text = "587";
                    ssl.Checked = true;
                }
            };

            Button test = new Button();
            test.Text = "Send test email";
            test.Size = new Size(110, 26);
            test.Location = new Point(606, 96);
            test.Click += delegate { SendTest(); };
            Controls.Add(test);
            testState.AutoSize = false;
            testState.Size = new Size(730, 34);
            testState.Location = new Point(14, 160);
            testState.ForeColor = Color.FromArgb(110, 110, 110);
            testState.Text = (MailStore.HasPassword ? "Password saved (leave blank to keep it). " : "No password saved yet. ") + " Gmail / Outlook / QQ need an app-specific password / authorization code, not your login password. The password stays on this PC, encrypted with your Windows account (DPAPI)."
                + (MailStore.LastStatus.Length > 0 ? " Last sent: " + MailStore.LastStatus : "");
            Controls.Add(testState);

            Lbl("Choose sessions to remind and when (minutes of cache left, comma-separated, e.g. 30,10)", 14, 202).Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            Lbl("Default", 14, 232);
            defMin.Minimum = 1;
            defMin.Maximum = 600;
            defMin.Value = 10;
            Place(defMin, 74, 228, 60);
            Lbl("min before expiry (filled in when you tick a session)", 142, 232);
            Button all = new Button();
            all.Text = "All";
            all.Size = new Size(60, 26);
            all.Location = new Point(560, 226);
            all.Click += delegate { SetAll(true); };
            Button none = new Button();
            none.Text = "None";
            none.Size = new Size(60, 26);
            none.Location = new Point(626, 226);
            none.Click += delegate { SetAll(false); };
            Controls.Add(all);
            Controls.Add(none);

            grid.Location = new Point(14, 262);
            grid.Size = new Size(732, H - 320);
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = SystemColors.Window;
            DataGridViewCheckBoxColumn c0 = new DataGridViewCheckBoxColumn();
            c0.HeaderText = "Alert";
            c0.Width = 48;
            DataGridViewTextBoxColumn c1 = new DataGridViewTextBoxColumn();
            c1.HeaderText = "Session";
            c1.ReadOnly = true;
            c1.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            DataGridViewTextBoxColumn c2 = new DataGridViewTextBoxColumn();
            c2.HeaderText = "Now";
            c2.ReadOnly = true;
            c2.Width = 120;
            DataGridViewTextBoxColumn c3 = new DataGridViewTextBoxColumn();
            c3.HeaderText = "Cache TTL";
            c3.ReadOnly = true;
            c3.Width = 80;
            DataGridViewTextBoxColumn c4 = new DataGridViewTextBoxColumn();
            c4.HeaderText = "Remind at (min)";
            c4.Width = 120;
            grid.Columns.AddRange(new DataGridViewColumn[] { c0, c1, c2, c3, c4 });
            foreach (MailSessionRow r in rows)
            {
                MailWatch w = MailStore.Find(r.Session);
                int idx = grid.Rows.Add(w != null, r.Title.Length > 0 ? r.Title : r.Session.Substring(0, Math.Min(8, r.Session.Length)), r.State,
                    r.TtlMin > 0 ? r.TtlMin + " min" : "—", w != null ? MailStore.MinsText(w.Mins) : "");
                grid.Rows[idx].Tag = r;
                if (!r.Live)
                {
                    grid.Rows[idx].DefaultCellStyle.ForeColor = Color.Gray;
                }
            }
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                {
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            grid.CellValueChanged += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                {
                    DataGridViewRow row = grid.Rows[e.RowIndex];
                    bool on = row.Cells[0].Value is bool && (bool)row.Cells[0].Value;
                    if (on && Convert.ToString(row.Cells[4].Value).Trim().Length == 0)
                    {
                        row.Cells[4].Value = ((int)defMin.Value).ToString(CultureInfo.InvariantCulture);
                    }
                }
            };
            Controls.Add(grid);

            Button ok = new Button();
            ok.Text = "OK";
            ok.Size = new Size(80, 28);
            ok.Location = new Point(586, H - 42);
            ok.Click += delegate { OnOk(); };
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Size = new Size(80, 28);
            cancel.Location = new Point(672, H - 42);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(ok);
            Controls.Add(cancel);
            Label hint = Lbl("One email per minute value per cache cycle, none while a session is running (its cache renews itself). With a 5-minute cache, a value above 5 never fires.", 14, H - 54);
            hint.ForeColor = Color.FromArgb(110, 110, 110);
            hint.AutoSize = false;
            hint.Size = new Size(560, 38);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void SetAll(bool on)
        {
            foreach (DataGridViewRow r in grid.Rows)
            {
                r.Cells[0].Value = on;
            }
        }

        private static bool TargetChanged(string hostText, string userText)
        {
            return !string.Equals(hostText, MailStore.Host, StringComparison.OrdinalIgnoreCase) || !string.Equals(userText, MailStore.User, StringComparison.OrdinalIgnoreCase);
        }

        // Two cases that ask before sending: port 465 is used ("direct SSL", which the mail component used here does not support and will only time out); encryption is not ticked and the target is not this machine (the password would travel in clear text)
        private bool ConfirmSend(MailConfig c)
        {
            if (c.Ssl && c.Port == 465)
            {
                if (MessageBox.Show(this, "Port 465 uses 'direct SSL', which the mail component used here does not support; it usually just times out. For Gmail / Outlook / QQ Mail use port 587 (STARTTLS). Continue anyway?",
                    "Email reminders", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return false;
                }
            }
            IPAddress ip;
            if (c.Ssl || string.Equals(c.Host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(c.Host, out ip) && IPAddress.IsLoopback(ip)))
            {
                return true;
            }
            return MessageBox.Show(this, "'STARTTLS / SSL encryption' is not ticked: the mail password and the mail text would travel over the network in clear text, and anyone on the same network may see them. Continue anyway?",
                "Email reminders", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private MailConfig CurrentConfig(out string error)
        {
            error = null;
            MailConfig c = new MailConfig();
            c.Host = host.Text.Trim();
            int p;
            if (!int.TryParse(port.Text.Trim(), out p) || p <= 0 || p > 65535)
            {
                error = "Invalid port.";
                return c;
            }
            c.Port = p;
            c.Ssl = ssl.Checked;
            c.User = user.Text.Trim();
            if (pass.Text.Length == 0 && MailStore.HasPassword && TargetChanged(c.Host, c.User))
            {
                // The stored password was meant for the original server + user name: if either was changed it must not be quietly sent to the new server
                error = "The server or user name was changed, please enter the password again.";
                return c;
            }
            c.Pass = pass.Text.Length > 0 ? pass.Text : MailStore.Password;
            c.From = from.Text.Trim().Length > 0 ? from.Text.Trim() : c.User;
            c.To = to.Text.Trim();
            if (c.Host.Length == 0 || c.User.Length == 0 || c.Pass.Length == 0 || c.To.Length == 0)
            {
                error = "Server, user name, password and recipient are all required.";
            }
            return c;
        }

        private void SendTest()
        {
            string err;
            MailConfig c = CurrentConfig(out err);
            if (err != null)
            {
                MessageBox.Show(this, err, "Email reminders", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ConfirmSend(c))
            {
                return;
            }
            testState.ForeColor = Color.FromArgb(110, 110, 110);
            testState.Text = "Sending a test email to " + c.To + " …";
            MailSender.SendAsync(c, "Stilla: test email", "If you received this email, the SMTP settings for email reminders are correct.\r\n\r\n-- Stilla", delegate(string e)
            {
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        testState.ForeColor = e == null ? Color.FromArgb(30, 130, 60) : Color.FromArgb(190, 50, 40);
                        testState.Text = e == null ? "Test email sent. Check your inbox (and the spam folder)." : "Send failed: " + e;
                    }));
                }
                catch (Exception)
                {
                }
            });
        }

        private void OnOk()
        {
            string err;
            MailConfig c = CurrentConfig(out err);
            bool anyChecked = false;
            List<MailWatch> res = new List<MailWatch>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (!(r.Cells[0].Value is bool) || !(bool)r.Cells[0].Value)
                {
                    continue;
                }
                anyChecked = true;
                List<int> mins = MailStore.ParseMins(Convert.ToString(r.Cells[4].Value));
                MailSessionRow info = (MailSessionRow)r.Tag;
                if (mins.Count == 0)
                {
                    MessageBox.Show(this, "\"" + Convert.ToString(r.Cells[1].Value) + "\" has no valid minutes value (1-600, separate several with commas).", "Email reminders", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                MailWatch w = new MailWatch();
                w.Session = info.Session;
                w.Title = info.Title;
                w.Mins = mins;
                res.Add(w);
            }
            if (enable.Checked && err != null)
            {
                MessageBox.Show(this, "To turn on email reminders, complete the mail settings first: " + err, "Email reminders", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (enable.Checked && !ConfirmSend(c))
            {
                return;
            }
            if (enable.Checked && !anyChecked)
            {
                if (MessageBox.Show(this, "No session is ticked, so enabling this will not send any email. Save anyway?", "Email reminders", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
            }
            // Server or user name changed and no new password entered: do not keep the old password (it was for the original server)
            if (pass.Text.Length == 0 && MailStore.HasPassword && TargetChanged(host.Text.Trim(), user.Text.Trim()))
            {
                MailStore.Password = "";
            }
            // Save the mail settings (if they are incomplete, saving is still allowed as long as reminders are off, so you can continue next time)
            MailStore.Host = host.Text.Trim();
            int pv;
            MailStore.Port = int.TryParse(port.Text.Trim(), out pv) && pv > 0 && pv < 65536 ? pv : 587;
            MailStore.Ssl = ssl.Checked;
            MailStore.User = user.Text.Trim();
            MailStore.From = from.Text.Trim();
            MailStore.To = to.Text.Trim();
            if (pass.Text.Length > 0)
            {
                MailStore.Password = pass.Text;
            }
            ResultEnabled = enable.Checked;
            ResultWatches = res;
            DialogResult = DialogResult.OK;
        }
    }
}
