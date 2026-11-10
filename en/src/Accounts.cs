// Accounts and subscriptions: account profiles (A / B / C...), switch timeline, login detection, history backfill, management dialogs.
//
// Data (all in the "logs" folder, plain text, so the Python analysis scripts can use it too):
//   accounts.csv          one account per line: ID, name, plan, subscription start date, monthly fee (USD), bound login organization, login hint, note
//   account_timeline.csv  switch timeline: from this time (UTC) on, usage belongs to which account and plan. A line is added whenever the account or plan changes
//
// How it is detected (read-only, the token is never touched):
//   - organizationUuid, subscriptionType, rateLimitTier in ~/.claude/.credentials.json
//   - oauthAccount in ~/.claude.json (email, subscription creation time, organization type)
//   - History: every session in the transcripts begins with a credential_org entry recording the organization logged in at the time; it is used to reconstruct past account switches

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
    // Subscription plans: ID, name, official nominal multiplier relative to Pro (only used to give a new account without data an initial estimate), default monthly fee (USD, for reference only, editable)
    internal static class PlanInfo
    {
        public static readonly string[] Ids = { "free", "pro", "max5", "max20", "team", "enterprise", "api", "other" };
        public static readonly string[] Names = { "Free", "Pro", "Max 5x", "Max 20x", "Team", "Enterprise", "API", "Other / not set" };
        public static readonly string[] Shorts = { "Free", "Pro", "Max5x", "Max20x", "Team", "Ent", "API", "Not set" };
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

        // The "family" a plan belongs to: max5 and max20 are both max
        public static string Family(string id)
        {
            return id == "max5" || id == "max20" ? "max" : (id ?? "");
        }

        // Determine the plan from the login info. exact = the tier (5x / 20x) was read explicitly; when it cannot be read a Max plan is assumed to be 5x
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
        public DateTime Start = DateTime.MinValue;      // Subscription start date (local date); MinValue = unknown
        public double Fee;                              // Monthly fee (USD)
        public string BindOrg = "";                     // ID of the bound login organization; empty = not bound (chosen manually)
        public string Hint = "";                        // Hint of the bound login account (masked email), only used to recognise it
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

    // Snapshot of the timeline (immutable): the background thread looks up "which account does usage at this moment belong to" by time, without clashing with the UI thread editing the timeline
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

        // Times before the first entry belong to the first entry's account (the earliest usage has to belong somewhere)
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

    // Information about the currently logged-in account (read-only). The token is neither read nor kept
    internal sealed class LoginInfo
    {
        public string OrgUuid = "", AccountUuid = "", Email = "", Display = "";
        public string Plan = "";                // The detected plan (PlanInfo id); empty if it cannot be determined
        public bool PlanExact;                  // The tier was read explicitly
        public DateTime? SubStartLocal;         // Subscription creation time (local)

        public bool Ok
        {
            get { return OrgUuid.Length > 0; }
        }

        // Email masking: a***9@example.com
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
        private const string AccountsHeader = "ID,Name,Plan,Start date,Monthly fee,Bound org,Login hint,Note";
        private const string TimelineHeader = "Time UTC,Account,Plan,Source";

        public static void Init(string logDir)
        {
            accountsPath = Path.Combine(logDir, "accounts.csv");
            timelinePath = Path.Combine(logDir, "account_timeline.csv");
            Load();
        }

        // ------------------------------------------------------------ Reading and writing
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
                            if (line.Length == 0 || line.IndexOf("ID,", StringComparison.Ordinal) >= 0 && line.IndexOf("Plan", StringComparison.Ordinal) >= 0)
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
                    // The file is being written: use an empty one this time and read again at the next start
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

        // ------------------------------------------------------------ Queries
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

        // The current "basis": account + plan. The quota conversion is fitted per basis and the UI reads the matching coefficients
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

        // From now on usage belongs to an account / plan. Nothing is recorded if it is the same as the last entry. Returns whether something was actually recorded
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

        // ------------------------------------------------------------ Subscription cycle
        // The current billing cycle: counted from the "day of month" of the subscription start date, renewing on the same day every month (months without that day use the last day). If the start date is unknown it falls back to calendar months (calendar = true)
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

        // ------------------------------------------------------------ History backfill
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        // Scans the credential_org entries in the main session transcripts (one at the start of each session and one on resume: {time, organization ID logged in at the time}) and sorts them by time.
        // Read-only; the thread goes into background mode (lowest CPU and disk priority); only main session files modified within the last 90 days are looked at, and byte matching skips almost all of the content
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
            const int carry = 4096;     // The end of each block is left unscanned and carried over to the start of the next one (the whole line after a hit still has to be seen)
            const int back = 1024;      // Enough is also kept before a hit so the start of that line can be found
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

        // Create profiles from the history: one account per login organization that ever appeared (A, B... in order of first appearance), and one timeline entry for every organization change.
        // The currently logged-in organization gets the detected plan and subscription date, the others get an empty plan for now (you fill it in). Without history nothing is created (left to "auto-create on new login")
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
                    e.Source = "History backfill";
                    tl.Add(e);
                    lastOrg = h.Value;
                }
            }
            SaveAccounts();
            ReplaceTimeline(tl);
        }

        // Fill in what is still empty in this account from the login info: plan, subscription start date, monthly fee, login hint
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

    // ---------------------------------------------------------------- Account management dialog
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

        // Only non-null once the switch records have been edited in this dialog
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
            Text = "Accounts and subscriptions";
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

            Button add = MakeButton("Add account", 12, 320, 100);
            add.Click += delegate { AddItem(); };
            Button del = MakeButton("Delete", 122, 320, 100);
            del.Click += delegate { DeleteItem(); };
            Button tl = MakeButton("Switch records...", 12, 380, 100);
            tl.Click += delegate { EditTimeline(); };

            int x = 240, cx = 350;
            AddLabel("ID", x, 16);
            lblId.Location = new Point(cx, 16);
            lblId.AutoSize = true;
            Controls.Add(lblId);

            AddLabel("Name", x, 48);
            txtName.Location = new Point(cx, 44);
            txtName.Size = new Size(280, 24);
            txtName.TextChanged += delegate { SaveFields(); };
            Controls.Add(txtName);

            AddLabel("Plan", x, 82);
            cboPlan.Location = new Point(cx, 78);
            cboPlan.Size = new Size(160, 24);
            cboPlan.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (string n in PlanInfo.Names)
            {
                cboPlan.Items.Add(n);
            }
            cboPlan.SelectedIndexChanged += delegate { SaveFields(); };
            Controls.Add(cboPlan);

            AddLabel("Subscription start", x, 116);
            chkStart.Text = "Known";
            chkStart.Location = new Point(cx, 114);
            chkStart.AutoSize = true;
            chkStart.CheckedChanged += delegate { dtStart.Enabled = chkStart.Checked; SaveFields(); };
            Controls.Add(chkStart);
            dtStart.Location = new Point(cx + 64, 112);
            dtStart.Size = new Size(150, 24);
            dtStart.Format = DateTimePickerFormat.Short;
            dtStart.ValueChanged += delegate { SaveFields(); };
            Controls.Add(dtStart);

            AddLabel("Monthly fee (USD)", x, 150);
            numFee.Location = new Point(cx, 146);
            numFee.Size = new Size(100, 24);
            numFee.Minimum = 0;
            numFee.Maximum = 100000;
            numFee.DecimalPlaces = 0;
            numFee.ValueChanged += delegate { SaveFields(); };
            Controls.Add(numFee);
            Label feeTip = new Label();
            feeTip.Text = "The default is only a reference to the official list price; change it to what you actually pay";
            feeTip.ForeColor = SystemColors.GrayText;
            feeTip.Location = new Point(cx + 108, 150);
            feeTip.AutoSize = true;
            Controls.Add(feeTip);

            AddLabel("Note", x, 184);
            txtNote.Location = new Point(cx, 180);
            txtNote.Size = new Size(280, 56);
            txtNote.Multiline = true;
            txtNote.TextChanged += delegate { SaveFields(); };
            Controls.Add(txtNote);

            AddLabel("Login binding", x, 252);
            lblBind.Location = new Point(cx, 252);
            lblBind.Size = new Size(290, 36);
            Controls.Add(lblBind);
            btnBind.Text = "Bind current login";
            btnBind.Location = new Point(cx, 292);
            btnBind.Size = new Size(110, 28);
            btnBind.Click += delegate { BindCurrent(); };
            Controls.Add(btnBind);
            btnUnbind.Text = "Unbind";
            btnUnbind.Location = new Point(cx + 118, 292);
            btnUnbind.Size = new Size(90, 28);
            btnUnbind.Click += delegate { UnbindCurrent(); };
            Controls.Add(btnUnbind);

            Label tip = new Label();
            tip.Text = "Once bound, log in to this account in Claude Code and the widget switches to it automatically;\r\naccounts that are not bound can be chosen manually.";
            tip.ForeColor = SystemColors.GrayText;
            tip.Location = new Point(cx, 330);
            tip.Size = new Size(300, 36);
            Controls.Add(tip);

            Button ok = MakeButton("OK", 470, 380, 80);
            ok.DialogResult = DialogResult.OK;
            Button cancel = MakeButton("Cancel", 560, 380, 80);
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
            return a.Label + " · " + PlanInfo.ShortOf(a.Plan) + (a.BindOrg.Length == 0 ? " · not bound" : "");
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
                lblBind.Text = a.BindOrg.Length == 0 ? "Not bound (choose manually)" : "Bound login: " + (a.Hint.Length > 0 ? a.Hint : a.BindOrg.Substring(0, 8) + "…");
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
                MessageBox.Show(this, "This account already has usage records and cannot be deleted (the usage would no longer belong to anything).\r\nYou can rename it or change its plan, or first reassign those records to another account in \"Switch records\".", "Accounts and subscriptions", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    if (MessageBox.Show(this, "The current login is already bound to \"" + o.Label + "\". Rebind it to \"" + a.Label + "\"? (" + o.Label + " will become unbound)", "Accounts and subscriptions",
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

    // ---------------------------------------------------------------- Switch records dialog
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
            Text = "Switch records";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 400);

            Label tip = new Label();
            tip.Text = "Each row: from this time on, usage belongs to which account and plan. A row is added automatically when the widget switches login account or plan;\r\nthe history is reconstructed from the session transcripts, so if it is wrong you can add or delete rows here.";
            tip.Location = new Point(12, 8);
            tip.Size = new Size(540, 36);
            Controls.Add(tip);

            lv.Location = new Point(12, 48);
            lv.Size = new Size(536, 250);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.Columns.Add("Time (local)", 150);
            lv.Columns.Add("Account", 150);
            lv.Columns.Add("Plan", 100);
            lv.Columns.Add("Source", 110);
            Controls.Add(lv);

            Label l1 = new Label();
            l1.Text = "Add entry: from";
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
            add.Text = "Add";
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
            del.Text = "Delete selected";
            del.Location = new Point(12, 360);
            del.Size = new Size(90, 28);
            del.Click += delegate { DeleteEntry(); };
            Controls.Add(del);
            Button ok = new Button();
            ok.Text = "OK";
            ok.Location = new Point(380, 360);
            ok.Size = new Size(80, 28);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "Cancel";
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
            e.Source = "Added manually";
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
                MessageBox.Show(this, "At least one record must remain.", "Switch records", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            items.Remove((TimelineEntry)lv.SelectedItems[0].Tag);
            Rebuild();
        }
    }
}
