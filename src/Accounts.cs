// 账号与订阅：账号档案（A / B / C…）、切换时间线、登录信息识别、历史回溯、管理对话框。
//
// 数据（都在「日志」文件夹里，纯文本，方便 Python 分析脚本一起用）：
//   账号.csv      每个账号一行：编号、名称、计划、订阅开始日期、月费（美元）、绑定的登录组织、登录提示、备注
//   账号时段.csv  切换时间线：从这个时间（UTC）起，用量归到哪个账号、什么计划。换账号、换计划都会加一行
//
// 识别方式（只读，不碰令牌）：
//   · ~/.claude/.credentials.json 里的 organizationUuid、subscriptionType、rateLimitTier
//   · ~/.claude.json 里的 oauthAccount（邮箱、订阅创建时间、组织类型）
//   · 历史：会话记录里每个会话开头都有一条 credential_org，记着当时登录的组织，用它还原过去的账号切换

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Stilla
{
    // 订阅计划：编号、名称、相对 Pro 的官方标称倍率（只用来给没有数据的新账号一个初始估计）、默认月费（美元，仅供参考，可改）
    internal static class PlanInfo
    {
        public static readonly string[] Ids = { "free", "pro", "max5", "max20", "team", "enterprise", "api", "other" };
        public static readonly string[] Names = { "Free", "Pro", "Max 5x", "Max 20x", "Team", "Enterprise", "API", "其他 / 未设置" };
        public static readonly string[] Shorts = { "Free", "Pro", "Max5x", "Max20x", "Team", "Ent", "API", "未设置" };
        public static readonly double[] Mult = { 0, 1, 5, 20, 0, 0, 0, 0 };
        public static readonly double[] DefaultFee = { 0, 20, 100, 200, 0, 0, 0, 0 };

        public static int Index(string id)
        {
            for (int i = 0; i < Ids.Length; i++)
            {
                if (Ids[i] == id)
                {
                    return i;
                }
            }
            return Ids.Length - 1;
        }

        public static string NameOf(string id)
        {
            return Names[Index(id)];
        }

        public static string ShortOf(string id)
        {
            return Shorts[Index(id)];
        }

        public static double FeeOf(string id)
        {
            return DefaultFee[Index(id)];
        }

        // 计划所属的「大类」：max5 和 max20 都是 max
        public static string Family(string id)
        {
            return id == "max5" || id == "max20" ? "max" : (id ?? "");
        }

        // 从登录信息判断计划。exact = 档位（5x / 20x）是明确读到的；Max 读不到档位时先按 5x 算
        public static string Detect(string subscriptionType, string tier, string orgType, out bool exact)
        {
            exact = false;
            string t = ((subscriptionType ?? "") + "|" + (orgType ?? "")).ToLowerInvariant();
            string r = (tier ?? "").ToLowerInvariant();
            if (t.Contains("max"))
            {
                if (r.Contains("20x"))
                {
                    exact = true;
                    return "max20";
                }
                if (r.Contains("5x"))
                {
                    exact = true;
                    return "max5";
                }
                return "max5";
            }
            exact = true;
            if (t.Contains("enterprise"))
            {
                return "enterprise";
            }
            if (t.Contains("team"))
            {
                return "team";
            }
            if (t.Contains("pro"))
            {
                return "pro";
            }
            if (t.Contains("free"))
            {
                return "free";
            }
            exact = false;
            return "";
        }
    }

    internal sealed class Account
    {
        public string Id = "";
        public string Name = "";
        public string Plan = "other";
        public DateTime Start = DateTime.MinValue;      // 订阅开始日期（本地日期）；MinValue = 未知
        public double Fee;                              // 月费（美元）
        public string BindOrg = "";                     // 绑定的登录组织 ID；空 = 没绑定（手动选择）
        public string Hint = "";                        // 绑定的登录账号提示（邮箱打码），只用来认人
        public string Note = "";

        public string Label
        {
            get { return string.IsNullOrEmpty(Name) || Name == Id ? Id : Id + " " + Name; }
        }

        public Account Copy()
        {
            return (Account)MemberwiseClone();
        }
    }

    internal sealed class TimelineEntry
    {
        public DateTime Utc;
        public string AccountId = "";
        public string Plan = "other";
        public string Source = "";
    }

    // 时间线的快照（不可变）：后台线程按时间查「这个时刻用量归哪个账号」，不会和界面线程改时间线打架
    internal sealed class AccountIndex
    {
        private readonly TimelineEntry[] e;

        public AccountIndex(TimelineEntry[] entries)
        {
            e = entries;
        }

        public bool Empty
        {
            get { return e.Length == 0; }
        }

        // 第一条记录之前的时间，归第一条记录的账号（最早的用量总得有个归属）
        private int Find(DateTime utc)
        {
            int lo = 0, hi = e.Length - 1, r = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (e[mid].Utc <= utc)
                {
                    r = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return r;
        }

        public string AccountAt(DateTime utc)
        {
            return e.Length == 0 ? null : e[Find(utc)].AccountId;
        }

        public string PlanAt(DateTime utc)
        {
            return e.Length == 0 ? null : e[Find(utc)].Plan;
        }
    }

    // 当前登录账号的信息（只读）。令牌不读、不留
    internal sealed class LoginInfo
    {
        public string OrgUuid = "", AccountUuid = "", Email = "", Display = "";
        public string Plan = "";                // 识别出的计划（PlanInfo 编号），识别不了是空
        public bool PlanExact;                  // 档位是明确读到的
        public DateTime? SubStartLocal;         // 订阅创建时间（本地）

        public bool Ok
        {
            get { return OrgUuid.Length > 0; }
        }

        // 邮箱打码：a***9@example.com
        public string Hint
        {
            get
            {
                int at = Email.IndexOf('@');
                if (at <= 0)
                {
                    return Display;
                }
                string local = Email.Substring(0, at);
                string head = local.Length <= 2 ? local.Substring(0, 1) : local.Substring(0, 1) + "***" + local.Substring(local.Length - 1);
                return (local.Length <= 2 ? head + "***" : head) + Email.Substring(at);
            }
        }

        private static Dictionary<string, object> ReadJson(string path)
        {
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    string text;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        text = sr.ReadToEnd();
                    }
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }
                catch (Exception)
                {
                    Thread.Sleep(150);
                }
            }
            return null;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        private static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static LoginInfo Read()
        {
            LoginInfo li = new LoginInfo();
            string envDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            string cfgJson = string.IsNullOrEmpty(envDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
                : Path.Combine(envDir, ".claude.json");

            Dictionary<string, object> acct = Sub(ReadJson(cfgJson), "oauthAccount");
            Dictionary<string, object> cred = ReadJson(ClaudeRoots.FindCredentials());
            Dictionary<string, object> oauth = Sub(cred, "claudeAiOauth");

            li.OrgUuid = Str(cred, "organizationUuid");
            if (li.OrgUuid.Length == 0)
            {
                li.OrgUuid = Str(acct, "organizationUuid");
            }
            li.AccountUuid = Str(acct, "accountUuid");
            li.Email = Str(acct, "emailAddress");
            li.Display = Str(acct, "displayName");
            string tier = Str(oauth, "rateLimitTier");
            if (tier.Length == 0)
            {
                tier = Str(acct, "organizationRateLimitTier");
            }
            bool exact;
            li.Plan = PlanInfo.Detect(Str(oauth, "subscriptionType"), tier, Str(acct, "organizationType"), out exact);
            li.PlanExact = exact;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(Str(acct, "subscriptionCreatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
            {
                li.SubStartLocal = dto.LocalDateTime;
            }
            return li;
        }
    }

    internal static class AccountStore
    {
        public static readonly List<Account> Accounts = new List<Account>();
        private static readonly List<TimelineEntry> timeline = new List<TimelineEntry>();
        private static readonly object gate = new object();
        private static string accountsPath, timelinePath;
        private const string AccountsHeader = "编号,名称,计划,开始日期,月费,绑定组织,登录提示,备注";
        private const string TimelineHeader = "时间UTC,账号,计划,来源";

        public static void Init(string logDir)
        {
            accountsPath = Path.Combine(logDir, "账号.csv");
            timelinePath = Path.Combine(logDir, "账号时段.csv");
            Load();
        }

        // ------------------------------------------------------------ 读写
        private static string Q(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static string[] Split(string line)
        {
            List<string> res = new List<string>();
            StringBuilder sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    res.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                {
                    sb.Append(c);
                }
            }
            res.Add(sb.ToString());
            return res.ToArray();
        }

        private static void WriteAtomic(string path, IEnumerable<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines, new UTF8Encoding(true));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        private static string UtcText(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static void Load()
        {
            lock (gate)
            {
                Accounts.Clear();
                timeline.Clear();
                try
                {
                    if (File.Exists(accountsPath))
                    {
                        foreach (string line in File.ReadAllLines(accountsPath, Encoding.UTF8))
                        {
                            if (line.Length == 0 || line.IndexOf("编号,", StringComparison.Ordinal) >= 0 && line.IndexOf("计划", StringComparison.Ordinal) >= 0)
                            {
                                continue;
                            }
                            string[] f = Split(line);
                            if (f.Length < 3 || f[0].Length == 0)
                            {
                                continue;
                            }
                            Account a = new Account();
                            a.Id = f[0];
                            a.Name = f[1];
                            a.Plan = f[2];
                            DateTime st;
                            if (f.Length > 3 && DateTime.TryParseExact(f[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out st))
                            {
                                a.Start = st;
                            }
                            double fee;
                            if (f.Length > 4 && double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out fee))
                            {
                                a.Fee = fee;
                            }
                            a.BindOrg = f.Length > 5 ? f[5] : "";
                            a.Hint = f.Length > 6 ? f[6] : "";
                            a.Note = f.Length > 7 ? f[7] : "";
                            Accounts.Add(a);
                        }
                    }
                    if (File.Exists(timelinePath))
                    {
                        foreach (string line in File.ReadAllLines(timelinePath, Encoding.UTF8))
                        {
                            string[] f = Split(line);
                            DateTime t;
                            if (f.Length < 3 || !DateTime.TryParseExact(f[0].TrimStart('﻿'), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                            {
                                continue;
                            }
                            TimelineEntry e = new TimelineEntry();
                            e.Utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
                            e.AccountId = f[1];
                            e.Plan = f[2];
                            e.Source = f.Length > 3 ? f[3] : "";
                            timeline.Add(e);
                        }
                        timeline.Sort(delegate(TimelineEntry a, TimelineEntry b) { return a.Utc.CompareTo(b.Utc); });
                    }
                }
                catch (IOException)
                {
                    // 文件正被写入：这次先用空的，下一次启动再读
                }
            }
        }

        public static void SaveAccounts()
        {
            List<string> lines = new List<string>();
            lines.Add(AccountsHeader);
            foreach (Account a in Accounts)
            {
                lines.Add(Q(a.Id) + "," + Q(a.Name) + "," + Q(a.Plan) + "," + (a.Start == DateTime.MinValue ? "" : a.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    + "," + a.Fee.ToString("0.##", CultureInfo.InvariantCulture) + "," + Q(a.BindOrg) + "," + Q(a.Hint) + "," + Q(a.Note));
            }
            try
            {
                WriteAtomic(accountsPath, lines);
            }
            catch (IOException)
            {
            }
        }

        public static void SaveTimeline()
        {
            List<string> lines = new List<string>();
            lines.Add(TimelineHeader);
            lock (gate)
            {
                foreach (TimelineEntry e in timeline)
                {
                    lines.Add(UtcText(e.Utc) + "," + Q(e.AccountId) + "," + Q(e.Plan) + "," + Q(e.Source));
                }
            }
            try
            {
                WriteAtomic(timelinePath, lines);
            }
            catch (IOException)
            {
            }
        }

        // ------------------------------------------------------------ 查询
        public static Account Get(string id)
        {
            foreach (Account a in Accounts)
            {
                if (a.Id == id)
                {
                    return a;
                }
            }
            return null;
        }

        public static Account ByOrg(string org)
        {
            if (string.IsNullOrEmpty(org))
            {
                return null;
            }
            foreach (Account a in Accounts)
            {
                if (a.BindOrg == org)
                {
                    return a;
                }
            }
            return null;
        }

        public static string NextId(IEnumerable<Account> existing)
        {
            HashSet<string> used = new HashSet<string>();
            foreach (Account a in existing)
            {
                used.Add(a.Id);
            }
            for (char c = 'A'; c <= 'Z'; c++)
            {
                if (!used.Contains(c.ToString()))
                {
                    return c.ToString();
                }
            }
            for (int i = 2; ; i++)
            {
                if (!used.Contains("A" + i))
                {
                    return "A" + i;
                }
            }
        }

        private static TimelineEntry Last()
        {
            lock (gate)
            {
                return timeline.Count == 0 ? null : timeline[timeline.Count - 1];
            }
        }

        public static Account Current
        {
            get
            {
                TimelineEntry e = Last();
                return e == null ? null : Get(e.AccountId);
            }
        }

        // 现在的「口径」：账号 + 计划。额度换算按它分开拟合，界面按它读对应的系数
        public static string CurrentKey
        {
            get
            {
                TimelineEntry e = Last();
                return e == null ? "" : e.AccountId + ":" + e.Plan;
            }
        }

        public static bool HasTimeline
        {
            get { return Last() != null; }
        }

        public static AccountIndex Index()
        {
            lock (gate)
            {
                return new AccountIndex(timeline.ToArray());
            }
        }

        public static List<TimelineEntry> TimelineCopy()
        {
            lock (gate)
            {
                List<TimelineEntry> c = new List<TimelineEntry>();
                foreach (TimelineEntry e in timeline)
                {
                    TimelineEntry n = new TimelineEntry();
                    n.Utc = e.Utc;
                    n.AccountId = e.AccountId;
                    n.Plan = e.Plan;
                    n.Source = e.Source;
                    c.Add(n);
                }
                return c;
            }
        }

        public static int TimelineCount(string accountId)
        {
            int n = 0;
            lock (gate)
            {
                foreach (TimelineEntry e in timeline)
                {
                    if (e.AccountId == accountId)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        // 从现在起归到某个账号 / 计划。和最后一条一样就不记。返回是否真的记了
        public static bool SetCurrent(string accountId, string plan, string source)
        {
            lock (gate)
            {
                TimelineEntry last = timeline.Count == 0 ? null : timeline[timeline.Count - 1];
                if (last != null && last.AccountId == accountId && last.Plan == plan)
                {
                    return false;
                }
                TimelineEntry e = new TimelineEntry();
                e.Utc = DateTime.UtcNow;
                e.Utc = new DateTime(e.Utc.Ticks - e.Utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                e.AccountId = accountId;
                e.Plan = plan;
                e.Source = source;
                timeline.Add(e);
            }
            SaveTimeline();
            return true;
        }

        public static void ReplaceTimeline(List<TimelineEntry> list)
        {
            lock (gate)
            {
                timeline.Clear();
                timeline.AddRange(list);
                timeline.Sort(delegate(TimelineEntry a, TimelineEntry b) { return a.Utc.CompareTo(b.Utc); });
            }
            SaveTimeline();
        }

        // ------------------------------------------------------------ 订阅周期
        // 当前这个计费周期：从订阅开始日期的「几号」起算，每月同一天续费（没有这一天的月份取月底）。开始日期未知就按自然月（calendar = true）
        public static void BillingCycle(Account a, DateTime now, out DateTime start, out DateTime end, out bool calendar)
        {
            calendar = a.Start == DateTime.MinValue;
            int day = calendar ? 1 : a.Start.Day;
            DateTime first = new DateTime(now.Year, now.Month, 1);
            DateTime anchor = Anchor(first, day);
            if (anchor > now)
            {
                first = first.AddMonths(-1);
                anchor = Anchor(first, day);
            }
            start = anchor;
            end = Anchor(first.AddMonths(1), day);
            if (!calendar && start < a.Start.Date)
            {
                start = a.Start.Date;
            }
        }

        private static DateTime Anchor(DateTime firstOfMonth, int day)
        {
            return new DateTime(firstOfMonth.Year, firstOfMonth.Month, Math.Min(day, DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month)));
        }

        // ------------------------------------------------------------ 历史回溯
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        // 扫一遍主会话记录里的 credential_org（每个会话开头、恢复时各有一条：{时间, 当时登录的组织 ID}），按时间排好。
        // 只读；线程进入后台模式（CPU 和磁盘优先级最低）；只看最近 90 天内改过的主会话文件，用字节匹配跳过绝大部分内容
        public static List<KeyValuePair<DateTime, string>> ScanHistory(string projectsDir)
        {
            List<KeyValuePair<DateTime, string>> hits = new List<KeyValuePair<DateTime, string>>();
            bool bg = SetThreadPriority(GetCurrentThread(), 0x00010000);
            try
            {
                byte[] pat = Encoding.ASCII.GetBytes("\"credential_org\"");
                byte[] buf = new byte[(4 << 20) + 8192];
                DateTime cut = DateTime.UtcNow.AddDays(-90);
                foreach (LongPath.Entry e in LongPath.ListJsonl(projectsDir))
                {
                    if (e.Sub || e.MtimeUtc < cut)
                    {
                        continue;
                    }
                    try
                    {
                        using (FileStream fs = LongPath.Open(e.Path))
                        {
                            ScanFile(fs, pat, buf, hits);
                        }
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            finally
            {
                if (bg)
                {
                    SetThreadPriority(GetCurrentThread(), 0x00020000);
                }
            }
            hits.Sort(delegate(KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b) { return a.Key.CompareTo(b.Key); });
            return hits;
        }

        private static void ScanFile(Stream fs, byte[] pat, byte[] buf, List<KeyValuePair<DateTime, string>> hits)
        {
            const int carry = 4096;     // 每块末尾留着不扫，带到下一块开头（命中点后面还要看完整一行）
            const int back = 1024;      // 命中点前面也要留够，找得到这一行的开头
            int have = 0;
            int scanFrom = 0;
            while (true)
            {
                int n = fs.Read(buf, have, buf.Length - have);
                bool eof = n <= 0;
                if (!eof)
                {
                    have += n;
                }
                int limit = eof ? have - pat.Length + 1 : have - carry;
                for (int i = scanFrom; i < limit; i++)
                {
                    if (buf[i] != pat[0])
                    {
                        continue;
                    }
                    int k = 1;
                    while (k < pat.Length && buf[i + k] == pat[k])
                    {
                        k++;
                    }
                    if (k == pat.Length)
                    {
                        Extract(buf, have, i, hits);
                    }
                }
                if (eof)
                {
                    break;
                }
                if (have <= carry)
                {
                    continue;
                }
                int from = Math.Max(0, limit - back);
                int len = have - from;
                Buffer.BlockCopy(buf, from, buf, 0, len);
                scanFrom = limit - from;
                have = len;
            }
        }

        private static void Extract(byte[] buf, int have, int pos, List<KeyValuePair<DateTime, string>> hits)
        {
            int a = pos;
            while (a > 0 && buf[a - 1] != 10)
            {
                a--;
            }
            int b = pos;
            while (b < have && buf[b] != 10)
            {
                b++;
            }
            if (b - a > 4000 || buf[a] != (byte)'{')
            {
                return;
            }
            string line = Encoding.UTF8.GetString(buf, a, b - a);
            if (line.IndexOf("\"type\":\"attachment\"", StringComparison.Ordinal) < 0)
            {
                return;
            }
            string org = Between(line, "\"organizationUuid\":\"");
            string ts = Between(line, "\"timestamp\":\"");
            DateTime t;
            if (org.Length == 36 && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
            {
                hits.Add(new KeyValuePair<DateTime, string>(DateTime.SpecifyKind(t, DateTimeKind.Utc), org));
            }
        }

        private static string Between(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
            {
                return "";
            }
            i += key.Length;
            int j = s.IndexOf('"', i);
            return j < 0 ? "" : s.Substring(i, j - i);
        }

        // 用历史建档：每个出现过的登录组织一个账号（A、B…，按第一次出现的先后），时间线里每次换组织记一条。
        // 当前登录的组织用识别出的计划和订阅日期，其他的计划先空着（你来填）。没有历史就什么都不建（交给「新登录自动建档」）
        public static void BuildFromHistory(List<KeyValuePair<DateTime, string>> hits, LoginInfo li)
        {
            if (hits == null || hits.Count == 0 || Accounts.Count > 0)
            {
                return;
            }
            Dictionary<string, Account> byOrg = new Dictionary<string, Account>();
            List<TimelineEntry> tl = new List<TimelineEntry>();
            string lastOrg = null;
            foreach (KeyValuePair<DateTime, string> h in hits)
            {
                Account a;
                if (!byOrg.TryGetValue(h.Value, out a))
                {
                    a = new Account();
                    a.Id = NextId(Accounts);
                    a.BindOrg = h.Value;
                    if (li != null && li.Ok && li.OrgUuid == h.Value)
                    {
                        FillFromLogin(a, li);
                    }
                    byOrg[h.Value] = a;
                    Accounts.Add(a);
                }
                if (h.Value != lastOrg)
                {
                    TimelineEntry e = new TimelineEntry();
                    e.Utc = h.Key;
                    e.AccountId = a.Id;
                    e.Plan = a.Plan;
                    e.Source = "历史回溯";
                    tl.Add(e);
                    lastOrg = h.Value;
                }
            }
            SaveAccounts();
            ReplaceTimeline(tl);
        }

        // 用登录信息填这个账号里还没填的：计划、订阅开始日期、月费、登录提示
        public static void FillFromLogin(Account a, LoginInfo li)
        {
            if (li.Plan.Length > 0 && (a.Plan == "other" || a.Plan.Length == 0))
            {
                a.Plan = li.Plan;
            }
            if (a.Start == DateTime.MinValue && li.SubStartLocal.HasValue)
            {
                a.Start = li.SubStartLocal.Value.Date;
            }
            if (a.Fee <= 0)
            {
                a.Fee = PlanInfo.FeeOf(a.Plan);
            }
            if (a.Hint.Length == 0)
            {
                a.Hint = li.Hint;
            }
        }
    }

    // ---------------------------------------------------------------- 账号管理对话框
    internal sealed class AccountForm : Form
    {
        private readonly List<Account> items = new List<Account>();
        private readonly ListBox list = new ListBox();
        private readonly Label lblId = new Label();
        private readonly Label lblBind = new Label();
        private readonly TextBox txtName = new TextBox();
        private readonly TextBox txtNote = new TextBox();
        private readonly ComboBox cboPlan = new ComboBox();
        private readonly CheckBox chkStart = new CheckBox();
        private readonly DateTimePicker dtStart = new DateTimePicker();
        private readonly NumericUpDown numFee = new NumericUpDown();
        private readonly Button btnBind = new Button();
        private readonly Button btnUnbind = new Button();
        private readonly LoginInfo login;
        private bool loading;
        private List<TimelineEntry> timelineEdited;

        public List<Account> Result
        {
            get { return items; }
        }

        // 在这个对话框里改过切换记录才不为 null
        public List<TimelineEntry> TimelineResult
        {
            get { return timelineEdited; }
        }

        public AccountForm(LoginInfo login, string selectId)
        {
            this.login = login;
            foreach (Account a in AccountStore.Accounts)
            {
                items.Add(a.Copy());
            }
            Text = "账号与订阅";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(660, 420);

            list.Location = new Point(12, 12);
            list.Size = new Size(210, 300);
            list.SelectedIndexChanged += delegate { LoadFields(); };
            Controls.Add(list);

            Button add = MakeButton("新增账号", 12, 320, 100);
            add.Click += delegate { AddItem(); };
            Button del = MakeButton("删除", 122, 320, 100);
            del.Click += delegate { DeleteItem(); };
            Button tl = MakeButton("切换记录…", 12, 380, 100);
            tl.Click += delegate { EditTimeline(); };

            int x = 240, cx = 350;
            AddLabel("编号", x, 16);
            lblId.Location = new Point(cx, 16);
            lblId.AutoSize = true;
            Controls.Add(lblId);

            AddLabel("名称", x, 48);
            txtName.Location = new Point(cx, 44);
            txtName.Size = new Size(280, 24);
            txtName.TextChanged += delegate { SaveFields(); };
            Controls.Add(txtName);

            AddLabel("计划", x, 82);
            cboPlan.Location = new Point(cx, 78);
            cboPlan.Size = new Size(160, 24);
            cboPlan.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (string n in PlanInfo.Names)
            {
                cboPlan.Items.Add(n);
            }
            cboPlan.SelectedIndexChanged += delegate { SaveFields(); };
            Controls.Add(cboPlan);

            AddLabel("订阅开始", x, 116);
            chkStart.Text = "已知";
            chkStart.Location = new Point(cx, 114);
            chkStart.AutoSize = true;
            chkStart.CheckedChanged += delegate { dtStart.Enabled = chkStart.Checked; SaveFields(); };
            Controls.Add(chkStart);
            dtStart.Location = new Point(cx + 64, 112);
            dtStart.Size = new Size(150, 24);
            dtStart.Format = DateTimePickerFormat.Short;
            dtStart.ValueChanged += delegate { SaveFields(); };
            Controls.Add(dtStart);

            AddLabel("月费（美元）", x, 150);
            numFee.Location = new Point(cx, 146);
            numFee.Size = new Size(100, 24);
            numFee.Minimum = 0;
            numFee.Maximum = 100000;
            numFee.DecimalPlaces = 0;
            numFee.ValueChanged += delegate { SaveFields(); };
            Controls.Add(numFee);
            Label feeTip = new Label();
            feeTip.Text = "默认值只是官方标价的参考，按你实际付的改";
            feeTip.ForeColor = SystemColors.GrayText;
            feeTip.Location = new Point(cx + 108, 150);
            feeTip.AutoSize = true;
            Controls.Add(feeTip);

            AddLabel("备注", x, 184);
            txtNote.Location = new Point(cx, 180);
            txtNote.Size = new Size(280, 56);
            txtNote.Multiline = true;
            txtNote.TextChanged += delegate { SaveFields(); };
            Controls.Add(txtNote);

            AddLabel("登录绑定", x, 252);
            lblBind.Location = new Point(cx, 252);
            lblBind.Size = new Size(290, 36);
            Controls.Add(lblBind);
            btnBind.Text = "绑定当前登录";
            btnBind.Location = new Point(cx, 292);
            btnBind.Size = new Size(110, 28);
            btnBind.Click += delegate { BindCurrent(); };
            Controls.Add(btnBind);
            btnUnbind.Text = "解除绑定";
            btnUnbind.Location = new Point(cx + 118, 292);
            btnUnbind.Size = new Size(90, 28);
            btnUnbind.Click += delegate { UnbindCurrent(); };
            Controls.Add(btnUnbind);

            Label tip = new Label();
            tip.Text = "绑定后，在 Claude Code 里登录这个账号，悬浮窗会自动切到它；\r\n没绑定的账号可以手动选择。";
            tip.ForeColor = SystemColors.GrayText;
            tip.Location = new Point(cx, 330);
            tip.Size = new Size(300, 36);
            Controls.Add(tip);

            Button ok = MakeButton("确定", 470, 380, 80);
            ok.DialogResult = DialogResult.OK;
            Button cancel = MakeButton("取消", 560, 380, 80);
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;

            RefreshList(selectId);
        }

        private Button MakeButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 28);
            Controls.Add(b);
            return b;
        }

        private void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            Controls.Add(l);
        }

        private static string ItemText(Account a)
        {
            return a.Label + " · " + PlanInfo.ShortOf(a.Plan) + (a.BindOrg.Length == 0 ? " · 未绑定" : "");
        }

        private Account Sel
        {
            get { return list.SelectedIndex >= 0 && list.SelectedIndex < items.Count ? items[list.SelectedIndex] : null; }
        }

        private void RefreshList(string selectId)
        {
            loading = true;
            int keep = list.SelectedIndex;
            list.Items.Clear();
            foreach (Account a in items)
            {
                list.Items.Add(ItemText(a));
            }
            int idx = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Id == selectId)
                {
                    idx = i;
                }
            }
            if (idx < 0)
            {
                idx = Math.Min(Math.Max(keep, 0), items.Count - 1);
            }
            loading = false;
            if (idx >= 0)
            {
                list.SelectedIndex = idx;
            }
            LoadFields();
        }

        private void LoadFields()
        {
            Account a = Sel;
            loading = true;
            bool on = a != null;
            txtName.Enabled = txtNote.Enabled = cboPlan.Enabled = chkStart.Enabled = numFee.Enabled = on;
            if (on)
            {
                lblId.Text = a.Id;
                txtName.Text = a.Name;
                cboPlan.SelectedIndex = PlanInfo.Index(a.Plan);
                chkStart.Checked = a.Start != DateTime.MinValue;
                dtStart.Enabled = chkStart.Checked;
                dtStart.Value = a.Start == DateTime.MinValue ? DateTime.Today : a.Start;
                numFee.Value = (decimal)Math.Max(0, Math.Min(100000, a.Fee));
                txtNote.Text = a.Note;
                lblBind.Text = a.BindOrg.Length == 0 ? "未绑定（手动选择）" : "已绑定登录：" + (a.Hint.Length > 0 ? a.Hint : a.BindOrg.Substring(0, 8) + "…");
            }
            else
            {
                lblId.Text = "";
                lblBind.Text = "";
            }
            btnBind.Enabled = on && login != null && login.Ok;
            btnUnbind.Enabled = on && a.BindOrg.Length > 0;
            loading = false;
        }

        private void SaveFields()
        {
            Account a = Sel;
            if (loading || a == null)
            {
                return;
            }
            a.Name = txtName.Text.Trim();
            a.Plan = PlanInfo.Ids[Math.Max(0, cboPlan.SelectedIndex)];
            a.Start = chkStart.Checked ? dtStart.Value.Date : DateTime.MinValue;
            a.Fee = (double)numFee.Value;
            a.Note = txtNote.Text.Trim();
            loading = true;
            list.Items[list.SelectedIndex] = ItemText(a);
            loading = false;
        }

        private void AddItem()
        {
            Account a = new Account();
            a.Id = AccountStore.NextId(items);
            items.Add(a);
            RefreshList(a.Id);
        }

        private void DeleteItem()
        {
            Account a = Sel;
            if (a == null)
            {
                return;
            }
            if (AccountStore.TimelineCount(a.Id) > 0)
            {
                MessageBox.Show(this, "这个账号已经有使用记录，不能删除（删了这些用量就没有归属了）。\r\n可以改名字或计划，或者到「切换记录」里先把这些记录改归到别的账号。", "账号与订阅", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            items.Remove(a);
            RefreshList(null);
        }

        private void BindCurrent()
        {
            Account a = Sel;
            if (a == null || login == null || !login.Ok)
            {
                return;
            }
            foreach (Account o in items)
            {
                if (o != a && o.BindOrg == login.OrgUuid)
                {
                    if (MessageBox.Show(this, "当前登录已经绑定在「" + o.Label + "」上。改绑到「" + a.Label + "」吗？（" + o.Label + " 会变成未绑定）", "账号与订阅",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        return;
                    }
                    o.BindOrg = "";
                    o.Hint = "";
                }
            }
            a.BindOrg = login.OrgUuid;
            a.Hint = login.Hint;
            AccountStore.FillFromLogin(a, login);
            RefreshList(a.Id);
        }

        private void UnbindCurrent()
        {
            Account a = Sel;
            if (a == null)
            {
                return;
            }
            a.BindOrg = "";
            a.Hint = "";
            RefreshList(a.Id);
        }

        private void EditTimeline()
        {
            using (TimelineForm f = new TimelineForm(timelineEdited ?? AccountStore.TimelineCopy(), items))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    timelineEdited = f.Result;
                }
            }
        }
    }

    // ---------------------------------------------------------------- 切换记录对话框
    internal sealed class TimelineForm : Form
    {
        private readonly List<TimelineEntry> items;
        private readonly List<Account> accounts;
        private readonly ListView lv = new ListView();
        private readonly DateTimePicker dt = new DateTimePicker();
        private readonly ComboBox cboAcc = new ComboBox();
        private readonly ComboBox cboPlan = new ComboBox();

        public List<TimelineEntry> Result
        {
            get { return items; }
        }

        public TimelineForm(List<TimelineEntry> entries, List<Account> accounts)
        {
            items = entries;
            this.accounts = accounts;
            Text = "切换记录";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 400);

            Label tip = new Label();
            tip.Text = "每一行：从这个时间起，用量归到哪个账号、什么计划。悬浮窗换登录账号、换计划时会自动加一行；\r\n历史是从会话记录里回溯的，不对的话可以在这里补录或删除。";
            tip.Location = new Point(12, 8);
            tip.Size = new Size(540, 36);
            Controls.Add(tip);

            lv.Location = new Point(12, 48);
            lv.Size = new Size(536, 250);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.Columns.Add("时间（本地）", 150);
            lv.Columns.Add("账号", 150);
            lv.Columns.Add("计划", 100);
            lv.Columns.Add("来源", 110);
            Controls.Add(lv);

            Label l1 = new Label();
            l1.Text = "补录：从";
            l1.Location = new Point(12, 312);
            l1.AutoSize = true;
            Controls.Add(l1);
            dt.Location = new Point(70, 308);
            dt.Size = new Size(150, 24);
            dt.Format = DateTimePickerFormat.Custom;
            dt.CustomFormat = "yyyy-MM-dd HH:mm";
            dt.Value = DateTime.Now;
            Controls.Add(dt);
            cboAcc.Location = new Point(230, 308);
            cboAcc.Size = new Size(130, 24);
            cboAcc.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (Account a in accounts)
            {
                cboAcc.Items.Add(a.Label);
            }
            if (cboAcc.Items.Count > 0)
            {
                cboAcc.SelectedIndex = 0;
            }
            Controls.Add(cboAcc);
            cboPlan.Location = new Point(368, 308);
            cboPlan.Size = new Size(100, 24);
            cboPlan.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (string n in PlanInfo.Names)
            {
                cboPlan.Items.Add(n);
            }
            cboPlan.SelectedIndex = 1;
            Controls.Add(cboPlan);
            Button add = new Button();
            add.Text = "添加";
            add.Location = new Point(476, 306);
            add.Size = new Size(72, 28);
            add.Click += delegate { AddEntry(); };
            Controls.Add(add);
            cboAcc.SelectedIndexChanged += delegate
            {
                if (cboAcc.SelectedIndex >= 0)
                {
                    cboPlan.SelectedIndex = PlanInfo.Index(accounts[cboAcc.SelectedIndex].Plan);
                }
            };
            if (cboAcc.SelectedIndex >= 0)
            {
                cboPlan.SelectedIndex = PlanInfo.Index(accounts[cboAcc.SelectedIndex].Plan);
            }

            Button del = new Button();
            del.Text = "删除选中";
            del.Location = new Point(12, 360);
            del.Size = new Size(90, 28);
            del.Click += delegate { DeleteEntry(); };
            Controls.Add(del);
            Button ok = new Button();
            ok.Text = "确定";
            ok.Location = new Point(380, 360);
            ok.Size = new Size(80, 28);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(468, 360);
            cancel.Size = new Size(80, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Rebuild();
        }

        private void Rebuild()
        {
            items.Sort(delegate(TimelineEntry a, TimelineEntry b) { return a.Utc.CompareTo(b.Utc); });
            lv.Items.Clear();
            foreach (TimelineEntry e in items)
            {
                Account a = null;
                foreach (Account x in accounts)
                {
                    if (x.Id == e.AccountId)
                    {
                        a = x;
                    }
                }
                ListViewItem it = new ListViewItem(e.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                it.SubItems.Add(a == null ? e.AccountId : a.Label);
                it.SubItems.Add(PlanInfo.NameOf(e.Plan));
                it.SubItems.Add(e.Source);
                it.Tag = e;
                lv.Items.Add(it);
            }
        }

        private void AddEntry()
        {
            if (cboAcc.SelectedIndex < 0)
            {
                return;
            }
            TimelineEntry e = new TimelineEntry();
            e.Utc = DateTime.SpecifyKind(dt.Value, DateTimeKind.Local).ToUniversalTime();
            e.Utc = new DateTime(e.Utc.Ticks - e.Utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            e.AccountId = accounts[cboAcc.SelectedIndex].Id;
            e.Plan = PlanInfo.Ids[Math.Max(0, cboPlan.SelectedIndex)];
            e.Source = "手动补录";
            items.Add(e);
            Rebuild();
        }

        private void DeleteEntry()
        {
            if (lv.SelectedItems.Count == 0)
            {
                return;
            }
            if (items.Count <= 1)
            {
                MessageBox.Show(this, "至少要留一条记录。", "切换记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            items.Remove((TimelineEntry)lv.SelectedItems[0].Tag);
            Rebuild();
        }
    }
}
