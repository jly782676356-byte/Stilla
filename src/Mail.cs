// 邮件提醒：勾选哪些会话、缓存剩多少分钟的时候发一封邮件给你。
//
// 设置保存在 %APPDATA%\Stilla\mail.txt（文本）：
//   enabled=1  host=  port=  ssl=1  user=  from=  to=  pass=（Windows DPAPI 加密：只有这台电脑上你这个 Windows 账户能解开）
//   w=<会话ID>|<分钟列表，例如 30,10>|<会话标题（给人看的）>      每个勾选的会话一行
// 发信用 SMTP（System.Net.Mail）。Gmail / Outlook / QQ 邮箱要用「应用专用密码 / 授权码」，不是登录密码。
// 邮件只在你勾选的会话缓存剩余时间掉到你设的分钟数时发，每个缓存周期每个分钟数只发一次。

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
        public List<int> Mins = new List<int>();        // 从大到小
    }

    // 对话框里的一行：一个会话
    internal sealed class MailSessionRow
    {
        public string Session = "";
        public string Title = "";
        public string State = "";       // 「剩 23 分」「运行中」「已过期」…
        public int TtlMin;              // 缓存有效期（分钟），0 = 还不知道
        public bool Live = true;        // 不在当前列表里的（以前勾选过的）是 false
    }

    internal static class MailStore
    {
        public static bool Enabled;
        public static string Host = "smtp.gmail.com";
        public static int Port = 587;
        public static bool Ssl = true;
        public static string User = "", From = "", To = "";
        private static string passEnc = "";
        private static bool passChecked, passUsable;        // 存着的密码能不能在这个 Windows 账户下解开（文件拷到别的电脑 / 别的账户就解不开）
        public static readonly List<MailWatch> Watches = new List<MailWatch>();
        public static int Attempts;                     // 发过（尝试发）多少封
        public static string LastStatus = "";           // 最近一次发送的结果（给对话框看）
        // DPAPI 的附加熵绑定「服务器 + 用户名」：mail.txt 里的 host / user 被改掉（手误、同步盘里被人动过、文件拷到别处）时密码就解不开，
        // 不会被悄悄发给另一个服务器。密码是在 Host / User 都赋好值之后才加密的（见 MailForm.OnOk）
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
                    return "";      // 换了 Windows 账户 / 文件被拷到别的电脑：解不开，让用户重新输
                }
            }
            set
            {
                passEnc = string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
                passChecked = false;
            }
        }

        // 有一个能用的密码：存着的那份在这个 Windows 账户下真的解得开（解不开 = 文件是从别的电脑 / 账户拷来的或者坏了，当作没有，免得拿空密码一直去登录）
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

        // "30, 10；5" → [30, 10, 5]（1~600 分钟，去重，从大到小）；全不合法返回空
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

        // mail.txt 一行一项：自由文本里的换行会把一项拆成几行（甚至凭空多出 enabled= / w= 行），写之前统一换成空格
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
        // 保险丝：1 小时内最多发这么多封（含测试邮件），再多就不发了。正常用法远到不了，只防「哪里出了循环」把邮箱刷爆 / 被邮件服务商当成垃圾邮件封号
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

        // 邮件头里不能有换行 / 控制字符（会话标题来自对话内容，可能带换行）：统一换成空格
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

        // 在后台线程发；done(null) = 成功，否则是错误说明（在发送线程里调用，要切回界面线程自己处理）
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
                        throw new InvalidOperationException("1 小时内已经发了 " + MaxPerHour + " 封，先停一停（防止刷屏）。");
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
                        err += "（" + ex.InnerException.Message + "）";
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

    // 设置对话框：邮箱 + 勾选会话和提前多少分钟提醒
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
        private static readonly string[] PresetNames = { "自定义", "Gmail", "Outlook / Hotmail", "QQ 邮箱" };
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
            Text = "邮件提醒";
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

            enable.Text = "启用邮件提醒（勾选的会话缓存剩余时间掉到设定的分钟数时，发一封邮件给你）";
            enable.AutoSize = true;
            enable.Location = new Point(14, 12);
            enable.Checked = MailStore.Enabled;
            Controls.Add(enable);

            Lbl("邮箱设置", 14, 42).Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            Lbl("预设", 14, 70);
            preset.DropDownStyle = ComboBoxStyle.DropDownList;
            preset.Items.AddRange(PresetNames);
            preset.SelectedIndex = 0;
            Place(preset, 60, 66, 150);
            Lbl("服务器", 230, 70);
            Place(host, 285, 66, 190);
            Lbl("端口", 490, 70);
            Place(port, 530, 66, 60);
            ssl.Text = "STARTTLS / SSL 加密";
            ssl.AutoSize = true;
            ssl.Location = new Point(606, 68);
            Controls.Add(ssl);
            Lbl("用户名", 14, 102);
            Place(user, 76, 98, 224);
            Lbl("密码", 312, 102);
            pass.UseSystemPasswordChar = true;
            Place(pass, 376, 98, 214);
            Lbl("发件人", 14, 134);
            Place(from, 76, 130, 224);
            Lbl("收件人", 312, 134);
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
            test.Text = "发送测试邮件";
            test.Size = new Size(110, 26);
            test.Location = new Point(606, 96);
            test.Click += delegate { SendTest(); };
            Controls.Add(test);
            testState.AutoSize = false;
            testState.Size = new Size(730, 34);
            testState.Location = new Point(14, 160);
            testState.ForeColor = Color.FromArgb(110, 110, 110);
            testState.Text = (MailStore.HasPassword ? "密码已保存（留空 = 不修改）。" : "还没有保存密码。") + "Gmail / Outlook / QQ 要用「应用专用密码 / 授权码」，不是登录密码；密码只保存在本机，用 Windows 账户加密（DPAPI）。"
                + (MailStore.LastStatus.Length > 0 ? " 上次发送：" + MailStore.LastStatus : "");
            Controls.Add(testState);

            Lbl("勾选哪些会话要提醒，以及缓存剩多少分钟时提醒（可以填多个，用逗号隔开，例如 30,10）", 14, 202).Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            Lbl("默认提前", 14, 232);
            defMin.Minimum = 1;
            defMin.Maximum = 600;
            defMin.Value = 10;
            Place(defMin, 74, 228, 60);
            Lbl("分钟（勾选一个会话时自动填上）", 142, 232);
            Button all = new Button();
            all.Text = "全选";
            all.Size = new Size(60, 26);
            all.Location = new Point(560, 226);
            all.Click += delegate { SetAll(true); };
            Button none = new Button();
            none.Text = "清空";
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
            c0.HeaderText = "提醒";
            c0.Width = 48;
            DataGridViewTextBoxColumn c1 = new DataGridViewTextBoxColumn();
            c1.HeaderText = "会话";
            c1.ReadOnly = true;
            c1.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            DataGridViewTextBoxColumn c2 = new DataGridViewTextBoxColumn();
            c2.HeaderText = "现在";
            c2.ReadOnly = true;
            c2.Width = 120;
            DataGridViewTextBoxColumn c3 = new DataGridViewTextBoxColumn();
            c3.HeaderText = "缓存有效期";
            c3.ReadOnly = true;
            c3.Width = 80;
            DataGridViewTextBoxColumn c4 = new DataGridViewTextBoxColumn();
            c4.HeaderText = "剩几分钟时提醒";
            c4.Width = 120;
            grid.Columns.AddRange(new DataGridViewColumn[] { c0, c1, c2, c3, c4 });
            foreach (MailSessionRow r in rows)
            {
                MailWatch w = MailStore.Find(r.Session);
                int idx = grid.Rows.Add(w != null, r.Title.Length > 0 ? r.Title : r.Session.Substring(0, Math.Min(8, r.Session.Length)), r.State,
                    r.TtlMin > 0 ? r.TtlMin + " 分钟" : "—", w != null ? MailStore.MinsText(w.Mins) : "");
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
            ok.Text = "确定";
            ok.Size = new Size(80, 28);
            ok.Location = new Point(586, H - 42);
            ok.Click += delegate { OnOk(); };
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(80, 28);
            cancel.Location = new Point(672, H - 42);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(ok);
            Controls.Add(cancel);
            Label hint = Lbl("每个会话的缓存周期里，每个分钟数只发一封；会话在运行中（缓存在自动续期）不发。缓存有效期是 5 分钟的会话，填大于 5 的分钟数永远不会触发。", 14, H - 54);
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

        // 发信前先问一声的两种情况：用了 465 端口（「直接 SSL」，这里的发信组件不支持，只会一直超时）；没勾选加密、又不是发给本机（密码会明文走网络）
        private bool ConfirmSend(MailConfig c)
        {
            if (c.Ssl && c.Port == 465)
            {
                if (MessageBox.Show(this, "465 端口用的是「直接 SSL」方式，这里使用的发信组件不支持，通常只会一直超时。Gmail / Outlook / QQ 邮箱请用 587 端口（STARTTLS）。仍然继续吗？",
                    "邮件提醒", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return false;
                }
            }
            IPAddress ip;
            if (c.Ssl || string.Equals(c.Host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(c.Host, out ip) && IPAddress.IsLoopback(ip)))
            {
                return true;
            }
            return MessageBox.Show(this, "没有勾选「STARTTLS / SSL 加密」：邮箱密码和邮件内容会以明文在网络上传输，同一个网络里的人都有可能看到。仍然继续吗？",
                "邮件提醒", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private MailConfig CurrentConfig(out string error)
        {
            error = null;
            MailConfig c = new MailConfig();
            c.Host = host.Text.Trim();
            int p;
            if (!int.TryParse(port.Text.Trim(), out p) || p <= 0 || p > 65535)
            {
                error = "端口不对。";
                return c;
            }
            c.Port = p;
            c.Ssl = ssl.Checked;
            c.User = user.Text.Trim();
            if (pass.Text.Length == 0 && MailStore.HasPassword && TargetChanged(c.Host, c.User))
            {
                // 存着的密码是给原来那个服务器 + 用户名用的：改了其中一个，不能把它悄悄发给新的服务器
                error = "改了服务器或用户名，请重新输入密码。";
                return c;
            }
            c.Pass = pass.Text.Length > 0 ? pass.Text : MailStore.Password;
            c.From = from.Text.Trim().Length > 0 ? from.Text.Trim() : c.User;
            c.To = to.Text.Trim();
            if (c.Host.Length == 0 || c.User.Length == 0 || c.Pass.Length == 0 || c.To.Length == 0)
            {
                error = "服务器、用户名、密码、收件人都要填。";
            }
            return c;
        }

        private void SendTest()
        {
            string err;
            MailConfig c = CurrentConfig(out err);
            if (err != null)
            {
                MessageBox.Show(this, err, "邮件提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ConfirmSend(c))
            {
                return;
            }
            testState.ForeColor = Color.FromArgb(110, 110, 110);
            testState.Text = "正在发送测试邮件到 " + c.To + " …";
            MailSender.SendAsync(c, "Stilla：测试邮件", "收到这封邮件，说明邮件提醒的 SMTP 设置是对的。\r\n\r\n——Stilla", delegate(string e)
            {
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        testState.ForeColor = e == null ? Color.FromArgb(30, 130, 60) : Color.FromArgb(190, 50, 40);
                        testState.Text = e == null ? "测试邮件已发出，去收件箱（和垃圾邮件）看看。" : "发送失败：" + e;
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
                    MessageBox.Show(this, "「" + Convert.ToString(r.Cells[1].Value) + "」没有填有效的分钟数（1~600，多个用逗号隔开）。", "邮件提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBox.Show(this, "要启用邮件提醒，先把邮箱设置填完整：" + err, "邮件提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (enable.Checked && !ConfirmSend(c))
            {
                return;
            }
            if (enable.Checked && !anyChecked)
            {
                if (MessageBox.Show(this, "还没有勾选任何会话，启用了也不会发邮件。仍然保存吗？", "邮件提醒", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
            }
            // 服务器或用户名改了、又没重新输密码：旧密码不留（它是给原来那个服务器用的）
            if (pass.Text.Length == 0 && MailStore.HasPassword && TargetChanged(host.Text.Trim(), user.Text.Trim()))
            {
                MailStore.Password = "";
            }
            // 保存邮箱设置（没填完整时只要没启用也允许保存，下次接着填）
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
