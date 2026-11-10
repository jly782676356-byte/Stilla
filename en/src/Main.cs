// Stilla - a quota widget for Claude Code (an independent community project, not made by Anthropic)
// What it does:
//   1) Puts a small always-on-top widget on screen (draggable, the position is remembered; background and text opacity can be adjusted separately with sliders)
//   2) Shows the percentage used of the "5-hour quota" and the "weekly quota", plus the next reset time and countdown for each
//   3) The quota data comes primarily from Claude Code's official status line (statusLine) feature, see Source.cs: it reads no login token, uses no network and costs no tokens; when the status line has no data, the default "Auto" source only falls back to the online query after the user agrees.
//      Only the optional "online query" mode (experimental, off by default) reads the local login token and calls an undocumented endpoint, see UsageClient below.
//   4) Optional: right-click -> "show usage statistics" lists the tokens consumed by each session / subtask in each time range;
//      it only reads the usage numbers and timestamps in the local session records, never the conversation content
//
// Notes for online query mode: it only reads the access token in ~/.claude/.credentials.json and never refreshes or rewrites it,
//       so Claude Code's login is not broken. When the token expires Claude Code renews it itself; this code only re-reads it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace Stilla
{
    internal sealed class Quota
    {
        public double Percent;        // Percent used, 0-100
        public DateTime? ResetsAt;    // Next reset time (local time), may be empty
    }

    internal enum Problem { None, NoLogin, Expired, Rejected, Network, RateLimited, Server, NoBridge }

    internal sealed class FetchResult
    {
        public Quota FiveHour;
        public Quota SevenDay;
        public DateTime FetchedAt;
        public string Via = "";     // Where the data came from: bridge = status line, api = online query, demo = demo
        public Problem Problem;
        public string Detail;
        public int RetrySeconds = 60;
    }

    // Reads the local Claude Code login token and calls the read-only quota query endpoint
    internal static class UsageClient
    {
        private const string Url = "https://api.anthropic.com/api/oauth/usage";

        public static FetchResult Query()
        {
            FetchResult r = new FetchResult();

            string token;
            long expiresMs;
            if (!TryReadToken(out token, out expiresMs))
            {
                r.Problem = Problem.NoLogin;
                r.RetrySeconds = 20;
                return r;
            }
            if (expiresMs > 0 && expiresMs <= NowMs())
            {
                // Token expired: do not refresh it ourselves, re-read the file next time after Claude Code has renewed it
                r.Problem = Problem.Expired;
                r.RetrySeconds = 20;
                return r;
            }

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "Stilla/0.1";
                req.Accept = "application/json";
                req.Headers["Authorization"] = "Bearer " + token;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return Parse(sr.ReadToEnd(), r);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp == null)
                {
                    r.Problem = Problem.Network;
                    r.RetrySeconds = 45;
                    return r;
                }
                int code = (int)resp.StatusCode;
                r.Detail = "HTTP " + code;
                if (code == 401 || code == 403)
                {
                    r.Problem = Problem.Rejected;
                    r.RetrySeconds = 30;
                }
                else if (code == 429)
                {
                    int ra;
                    int.TryParse(resp.Headers["Retry-After"], out ra);
                    r.Problem = Problem.RateLimited;
                    r.RetrySeconds = Math.Max(180, ra);
                }
                else
                {
                    r.Problem = Problem.Server;
                    r.RetrySeconds = 60;
                }
                return r;
            }
            catch (Exception)
            {
                r.Problem = Problem.Network;
                r.RetrySeconds = 45;
                return r;
            }
        }

        private static FetchResult Parse(string json, FetchResult r)
        {
            Dictionary<string, object> root = null;
            try
            {
                root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception)
            {
            }
            if (root == null)
            {
                r.Problem = Problem.Server;
                r.Detail = "Unexpected response format";
                return r;
            }

            r.FiveHour = ReadQuota(root, "five_hour");
            r.SevenDay = ReadQuota(root, "seven_day");
            if (r.FiveHour == null && r.SevenDay == null)
            {
                r.Problem = Problem.Server;
                r.Detail = "No quota data";
                return r;
            }
            r.FetchedAt = DateTime.Now;
            r.Via = "api";
            r.Problem = Problem.None;
            r.RetrySeconds = 60;
            return r;
        }

        private static Quota ReadQuota(Dictionary<string, object> root, string key)
        {
            object o;
            if (!root.TryGetValue(key, out o))
            {
                return null;
            }
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null)
            {
                return null;
            }

            Quota q = new Quota();
            object u;
            if (d.TryGetValue("utilization", out u) && u != null)
            {
                q.Percent = Convert.ToDouble(u, CultureInfo.InvariantCulture);
            }
            object t;
            string ts;
            if (d.TryGetValue("resets_at", out t) && (ts = t as string) != null)
            {
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
                {
                    // The endpoint's time jitters by a fraction of a second around the full minute (10:49:59.9 / 10:50:00.3), so round to the minute
                    long m = TimeSpan.TicksPerMinute;
                    DateTime local = dto.LocalDateTime;
                    q.ResetsAt = new DateTime((local.Ticks + m / 2) / m * m, local.Kind);
                }
            }
            return q;
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        internal static string ConfigDir()
        {
            return ClaudeRoots.Primary();
        }

        // Claude Code may be writing this file right now, so open it with sharing and retry a few times
        private static bool TryReadToken(out string token, out long expiresMs)
        {
            token = null;
            expiresMs = 0;
            string path = ClaudeRoots.FindCredentials();

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string text;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        text = sr.ReadToEnd();
                    }
                    Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    object oauth;
                    Dictionary<string, object> o;
                    if (root == null || !root.TryGetValue("claudeAiOauth", out oauth) || (o = oauth as Dictionary<string, object>) == null)
                    {
                        return false;
                    }
                    object at;
                    if (!o.TryGetValue("accessToken", out at) || !(at is string) || ((string)at).Length == 0)
                    {
                        return false;
                    }
                    token = (string)at;
                    object ex;
                    if (o.TryGetValue("expiresAt", out ex) && ex != null)
                    {
                        expiresMs = Convert.ToInt64(ex, CultureInfo.InvariantCulture);
                    }
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch (DirectoryNotFoundException)
                {
                    return false;
                }
                catch (Exception)
                {
                    Thread.Sleep(200);
                }
            }
            return false;
        }
    }

    // Session titles shown by the Claude extension in VS Code (exported by scripts\sync_vscode_titles.py to logs\vscode_titles.csv). Highest priority
    internal static class VsCodeTitles
    {
        private static Dictionary<string, string> titles = new Dictionary<string, string>();
        private static DateTime loadedUtc = DateTime.MinValue;

        public static string Get(string sessionId)
        {
            Refresh();
            string t;
            return sessionId != null && titles.TryGetValue(sessionId, out t) ? t : null;
        }

        private static void Refresh()
        {
            if (DateTime.UtcNow - loadedUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }
            loadedUtc = DateTime.UtcNow;
            string path = Path.Combine(AppPaths.DataDir, "vscode_titles.csv");
            Dictionary<string, string> fresh = new Dictionary<string, string>();
            if (File.Exists(path))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int comma = line.IndexOf(',');
                        if (comma <= 0)
                        {
                            continue;
                        }
                        string id = line.Substring(0, comma);
                        string title = line.Substring(comma + 1).Trim();
                        if (title.Length >= 2 && title[0] == '"' && title[title.Length - 1] == '"')
                        {
                            title = title.Substring(1, title.Length - 2).Replace("\"\"", "\"");
                        }
                        if (id != "Session ID" && title.Length > 0)
                        {
                            fresh[id] = title;
                        }
                    }
                }
                catch (IOException)
                {
                    loadedUtc = DateTime.MinValue;   // The file is being written, read it again next round
                }
            }
            titles = fresh;
        }
    }

    // Claude Code session names (same as shown in the app): the name in ~/.claude/sessions/<pid>.json.
    // nameSource = user is a name you changed, derived is an automatically generated name. Re-read every 30 seconds.
    internal static class SessionNames
    {
        private static Dictionary<string, string> names = new Dictionary<string, string>();
        private static DateTime loadedUtc = DateTime.MinValue;

        public static string Get(string sessionId)
        {
            Refresh();
            string name;
            return sessionId != null && names.TryGetValue(sessionId, out name) ? name : null;
        }

        private static void Refresh()
        {
            if (DateTime.UtcNow - loadedUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }
            loadedUtc = DateTime.UtcNow;
            Dictionary<string, string> fresh = new Dictionary<string, string>();
            Dictionary<string, DateTime> when = new Dictionary<string, DateTime>();
            foreach (string dir in ClaudeRoots.SessionDirs())
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                foreach (string path in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        Dictionary<string, object> o = js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                        if (o == null)
                        {
                            continue;
                        }
                        string sid = o.ContainsKey("sessionId") ? o["sessionId"] as string : null;
                        string nm = o.ContainsKey("name") ? o["name"] as string : null;
                        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(nm))
                        {
                            continue;
                        }
                        // nameSource: user = a name you set, auto = a title written by the AI, derived = a placeholder made of the project name plus random characters
                        // (like project-77, 0-35). A placeholder is not a title: skip it so the AI title / VS Code title takes over
                        string nameSource = o.ContainsKey("nameSource") ? o["nameSource"] as string : null;
                        if (string.Equals(nameSource, "derived", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        DateTime t = File.GetLastWriteTimeUtc(path);
                        DateTime old;
                        if (!when.TryGetValue(sid, out old) || t > old)
                        {
                            fresh[sid] = nm.Trim();
                            when[sid] = t;
                        }
                    }
                    catch (Exception)
                    {
                        // The file is being written or malformed, skip it
                    }
                }
            }
            names = fresh;
        }
    }

    // Counts the token usage in Claude Code's local session records and summarizes these time ranges in real time (see Snapshot for the Snapshot.Periods indices):
    //   - the last 5 minutes, 15 minutes, 30 minutes, 1 hour, 3 hours
    //   - the current 5-hour cycle: ends at the 5-hour reset time given by the quota source and goes back 5 hours; if that is unavailable, the last 5 hours on a rolling basis
    //   - the previous 5-hour cycle
    // Reads only usage numbers and timestamps, never conversation content; calls no endpoint and costs no tokens.
    // Files that have not changed are not re-read; when something was appended only the appended part is read.
    internal sealed class UsageTracker
    {
        private const int ShortMinutes = 15;
        private const int CycleHours = 5;
        private const int TrackHours = 10;                  // To cover the "previous 5 hours" we track 10 hours back
        private const int ScanSeconds = 10;                 // Scan the directory every 10 seconds to find new files
        private const long TailBytes = 8L * 1024 * 1024;    // When a file is read for the first time, only read the last 8 MB
        private const int ActiveSeconds = 60;               // A session written to within the last 60 seconds is marked "active"

        public sealed class Rec
        {
            public DateTime Utc;
            public string Session;
            public string Agent;    // File path of the subtask; null for a main session
            public long In, Out, CacheWrite, CacheRead;
            public long CacheWrite1h;   // The part of CacheWrite that belongs to the 1-hour cache (the rest is the 5-minute cache)
            public string Id;           // Message ID (used to look up the tier tags)
            public string Effort = "";  // Thinking effort: low / medium / high / xhigh / max (empty if the record has none)
            public bool Fast;           // Fast mode
            public long Think;          // Thinking tokens inside the output
            public int TtlMinutes;
            public string Model;    // Model name (as is, may carry a date suffix)  // validity of the cache this message wrote (5 minutes or 1 hour); 0 if it wrote no cache
        }

        public sealed class SessionTotal
        {
            public string Session;
            public string Name;
            public string Title;    // Session title; null if none
            public long Total;      // Sum of the four
            public long Input, Output, CacheWrite, CacheRead;
            public int SubCount;
            public bool Active;
            public DateTime? CacheExpiryUtc;    // Expiry time of the main session's cache (UTC); null if there is no cache record
            public DateTime LastUtc;            // Last activity within this time range (UTC), used for sorting

            public string ShortId
            {
                get { return Session.Length > 6 ? Session.Substring(0, 6) : Session; }
            }
        }

        // Token totals per session x model (used by the model view)
        public sealed class ModelTotal
        {
            public string Session;
            public string Model;
            public string Tier = "";                            // Tier: thinking effort low / medium / high / xhigh / max; with Ultracode on it is ultra; fast mode adds +fast; entries without a tag are "Unlabeled"
            public long Msgs;                                   // Number of API messages
            public DateTime FirstUtc = DateTime.MaxValue;       // First and last time this session x model x tier appeared (UTC): used for ordering to see when it was switched midway
            public DateTime LastUtc = DateTime.MinValue;
            public long Total;
            public long CacheRead;
            // For quota conversion: [0..4] = input, output, 5-minute cache write, 1-hour cache write, cache read for normal context; [5..9] = the same five items, but for requests whose context exceeded LongContextTokens
            public readonly long[] Tok = new long[10];
        }

        public const long LongContextTokens = 100000;   // If one request's context (input + cache write + cache read) exceeds this, some models (haiku) bill the whole request at the surcharge tier

        public static void AddTokens(long[] tok, long inp, long outp, long cw5, long cw1, long cr)
        {
            int o = (inp + cw5 + cw1 + cr) > LongContextTokens ? 5 : 0;
            tok[o] += inp;
            tok[o + 1] += outp;
            tok[o + 2] += cw5;
            tok[o + 3] += cw1;
            tok[o + 4] += cr;
        }

        // Which "model x tier" this record belongs to
        private static void TierAdd(Dictionary<string, TierTotal> byTier, Rec r)
        {
            TagStore.Tag tg = TagStore.Get(r.Id) ?? TagStore.FromRec(r);
            string tier = TagStore.TierKey(tg);
            string key = r.Model + "\n" + tier;
            TierTotal tt;
            if (!byTier.TryGetValue(key, out tt))
            {
                tt = new TierTotal();
                tt.Model = r.Model;
                tt.Tier = tier;
                byTier[key] = tt;
            }
            tt.Msgs++;
            tt.Total += r.In + r.Out + r.CacheWrite + r.CacheRead;
            tt.Out += r.Out;
            tt.Think += tg.Think;
            AddTokens(tt.Tok, r.In, r.Out, r.CacheWrite - r.CacheWrite1h, r.CacheWrite1h, r.CacheRead);
        }

        // A session's current state (independent of time range): last activity, whether it is being written, when the cache expires. Used for table sorting
        public sealed class LiveInfo
        {
            public DateTime LastUtc;
            public bool Active;
            public DateTime? ExpiryUtc;
        }

        // Totals per model x tier (used by the tier view). Tier = thinking effort (low / medium / high / xhigh / max); with Ultracode on it is ultra; fast mode appends +fast
        public sealed class TierTotal
        {
            public string Model;
            public string Tier;
            public long Msgs;       // Count
            public long Total;      // Sum of the four token types
            public long Out;        // Output (including thinking)
            public long Think;      // Of which thinking tokens
            public readonly long[] Tok = new long[10];
        }

        public sealed class PeriodSummary
        {
            public string Label = "";
            public string Range = "";
            public long Total;
            public long Input, Output, CacheWrite, CacheRead;
            public readonly List<SessionTotal> Rows = new List<SessionTotal>();
            public readonly List<ModelTotal> ModelRows = new List<ModelTotal>();
            public readonly List<TierTotal> TierRows = new List<TierTotal>();
        }

        // Periods[0] = 15 minutes, [1] = current 5-hour cycle, [2] = previous 5-hour cycle, [3] = 1 hour, [4] = 5 minutes, [5] = 30 minutes, [6] = 3 hours
        public sealed class Snapshot
        {
            public readonly PeriodSummary[] Periods = new PeriodSummary[7];
            public string Signature = "";
            public readonly Dictionary<string, LiveInfo> Live = new Dictionary<string, LiveInfo>();   // Session ID -> live state

            public Snapshot()
            {
                for (int i = 0; i < Periods.Length; i++)
                {
                    Periods[i] = new PeriodSummary();
                }
            }
        }

        private sealed class FileState
        {
            public string Path;
            public string Session;
            public bool Sub;
            public string Cwd;
            public long Offset = -1;    // -1 = not read yet
            public bool Align;          // When reading from the middle of a file, first drop the truncated first line
            public string Title;        // The session title Claude Code generates automatically (ai-title), the last one is used
            public string CustomTitle;  // The session name you changed in the app (custom-title), the last one is used
            public DateTime Mtime;
        }

        private readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Rec> recs = new Dictionary<string, Rec>();   // Keyed by message ID: a message is counted only once
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private DateTime nextScanUtc = DateTime.MinValue;

        public UsageTracker()
        {
        }

        public static string FormatTokens(long n)
        {
            if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
            if (n < 100000) return string.Format(CultureInfo.InvariantCulture, "{0:0.0}k", n / 1000.0);
            if (n < 1000000) return string.Format(CultureInfo.InvariantCulture, "{0:0}k", n / 1000.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00}M", n / 1000000.0);
        }

        // Called on every refresh (on a background thread, only one call at a time).
        // anchorUtc is the end of the current cycle (the 5-hour reset time from the quota source, UTC); pass null when unavailable
        public Snapshot Take(DateTime? anchorUtc)
        {
            DateTime now = DateTime.UtcNow;
            DateTime start = now.AddHours(-TrackHours);

            if (now >= nextScanUtc)
            {
                nextScanUtc = now.AddSeconds(ScanSeconds);
                Scan(start);
            }

            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, FileState> kv in files)
            {
                if (!ReadNew(kv.Value, start))
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (string k in dead)
            {
                files.Remove(k);
            }

            List<string> old = new List<string>();
            foreach (KeyValuePair<string, Rec> kv in recs)
            {
                if (kv.Value.Utc < start)
                {
                    old.Add(kv.Key);
                }
            }
            foreach (string k in old)
            {
                recs.Remove(k);
            }

            return Summarize(now, anchorUtc);
        }

        // Find the .jsonl files written within the last 10 hours: main sessions and the subtasks under subagents
        private void Scan(DateTime start)
        {
            List<LongPath.Entry> list = LongPath.ListAll();
            HashSet<string> present = new HashSet<string>();
            foreach (LongPath.Entry e in list)
            {
                if (e.MtimeUtc < start)
                {
                    continue;
                }
                present.Add(e.Path);
                FileState fs;
                if (!files.TryGetValue(e.Path, out fs))
                {
                    fs = new FileState();
                    fs.Path = e.Path;
                    fs.Session = e.Session;
                    fs.Sub = e.Sub;
                    files[e.Path] = fs;
                }
                fs.Mtime = e.MtimeUtc;
            }
            // Files outside the window are no longer tracked
            List<string> stale = new List<string>();
            foreach (string k in files.Keys)
            {
                if (!present.Contains(k))
                {
                    stale.Add(k);
                }
            }
            foreach (string k in stale)
            {
                files.Remove(k);
            }
        }

        // Read the newly appended part of a file. Returns false when the file is no longer tracked
        private bool ReadNew(FileState fs, DateTime start)
        {
            long len;
            try
            {
                using (FileStream probe = LongPath.Open(fs.Path))
                {
                    len = probe.Length;
                }
            }
            catch (IOException)
            {
                return true;    // Cannot be opened right now, try again next time
            }
            if (fs.Offset < 0)
            {
                // First read: only look at the tail; lines outside the window are filtered out by time
                fs.Offset = Math.Max(0, len - TailBytes);
                fs.Align = fs.Offset > 0;
            }
            if (len < fs.Offset)
            {
                // The file got smaller (replaced or truncated), start over
                fs.Offset = 0;
                fs.Align = false;
            }
            if (len == fs.Offset)
            {
                return true;
            }

            long want = len - fs.Offset;
            if (want > TailBytes)
            {
                fs.Offset = len - TailBytes;
                fs.Align = true;
                want = TailBytes;
            }

            byte[] buf = new byte[want];
            int got = 0;
            try
            {
                using (FileStream s = LongPath.Open(fs.Path))
                {
                    s.Seek(fs.Offset, SeekOrigin.Begin);
                    while (got < buf.Length)
                    {
                        int n = s.Read(buf, got, buf.Length - got);
                        if (n <= 0)
                        {
                            break;
                        }
                        got += n;
                    }
                }
            }
            catch (IOException)
            {
                return true;    // The file is being written or cannot be opened right now, try again next time
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            // Only process up to the last newline; an incomplete trailing line is left for next time
            int last = -1;
            for (int i = got - 1; i >= 0; i--)
            {
                if (buf[i] == 10)
                {
                    last = i;
                    break;
                }
            }
            if (last < 0)
            {
                return true;
            }

            int begin = 0;
            if (fs.Align)
            {
                for (int i = 0; i <= last; i++)
                {
                    if (buf[i] == 10)
                    {
                        begin = i + 1;
                        break;
                    }
                }
                fs.Align = false;
            }

            fs.Offset += last + 1;
            string text = Encoding.UTF8.GetString(buf, begin, last + 1 - begin);
            foreach (string line in text.Split('\n'))
            {
                if (line.Length > 0)
                {
                    ParseLine(fs, line, start);
                }
            }
            return true;
        }

        private void ParseLine(FileState fs, string line, DateTime start)
        {
            bool hasTitle = line.IndexOf("\"ai-title\"", StringComparison.Ordinal) >= 0;
            bool hasCustom = line.IndexOf("\"custom-title\"", StringComparison.Ordinal) >= 0;
            bool hasUsage = line.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0;
            if (!hasTitle && !hasCustom && !hasUsage)
            {
                return;
            }
            Dictionary<string, object> o;
            try
            {
                o = json.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return;
            }
            if (o == null)
            {
                return;
            }

            if (hasTitle && string.Equals(AsString(o, "type"), "ai-title"))
            {
                string title = AsString(o, "aiTitle");
                if (!string.IsNullOrEmpty(title))
                {
                    fs.Title = title.Trim();
                }
            }
            if (hasCustom && string.Equals(AsString(o, "type"), "custom-title"))
            {
                string custom = AsString(o, "customTitle");
                if (!string.IsNullOrEmpty(custom))
                {
                    fs.CustomTitle = custom.Trim();
                }
            }
            if (!hasUsage)
            {
                return;
            }

            if (fs.Cwd == null)
            {
                fs.Cwd = AsString(o, "cwd");
            }
            string ts = AsString(o, "timestamp");
            DateTimeOffset dto;
            if (ts == null || !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
            {
                return;
            }
            DateTime utc = dto.UtcDateTime;
            if (utc < start)
            {
                return;
            }

            Dictionary<string, object> m = AsDict(o, "message");
            Dictionary<string, object> u = m == null ? null : AsDict(m, "usage");
            string mid = m == null ? null : AsString(m, "id");
            if (u == null || string.IsNullOrEmpty(mid))
            {
                return;
            }

            Rec r = new Rec();
            r.Utc = utc;
            r.Session = fs.Session;
            r.Model = AsString(m, "model") ?? "";
            r.Id = mid;
            r.Effort = AsString(o, "perTurnEffort") ?? AsString(o, "effort") ?? "";
            r.Fast = string.Equals(AsString(u, "speed"), "fast", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> otd = AsDict(u, "output_tokens_details");
            r.Think = otd == null ? 0 : AsLong(otd, "thinking_tokens");
            r.Agent = fs.Sub ? fs.Path : null;
            r.In = AsLong(u, "input_tokens");
            r.Out = AsLong(u, "output_tokens");
            r.CacheRead = AsLong(u, "cache_read_input_tokens");
            r.CacheWrite = AsLong(u, "cache_creation_input_tokens");
            Dictionary<string, object> cc = AsDict(u, "cache_creation");
            if (cc != null)
            {
                r.CacheWrite1h = Math.Min(AsLong(cc, "ephemeral_1h_input_tokens"), r.CacheWrite);
            }
            if (cc != null && AsLong(cc, "ephemeral_1h_input_tokens") > 0)
            {
                r.TtlMinutes = 60;
            }
            else if (cc != null && AsLong(cc, "ephemeral_5m_input_tokens") > 0)
            {
                r.TtlMinutes = 5;
            }
            recs[mid] = r;
        }

        private Snapshot Summarize(DateTime now, DateTime? anchorUtc)
        {
            TagStore.Refresh();
            // End of the current cycle: use the reset time if it is valid, otherwise roll back 5 hours from now
            bool rolling = !anchorUtc.HasValue || anchorUtc.Value <= now;
            DateTime end = rolling ? now : anchorUtc.Value;
            DateTime curStart = end.AddHours(-CycleHours);
            if (curStart > now)
            {
                curStart = now;
            }
            DateTime prevStart = curStart.AddHours(-CycleHours);
            DateTime shortStart = now.AddMinutes(-ShortMinutes);

            Dictionary<string, string> cwdBy = new Dictionary<string, string>();
            Dictionary<string, string> titleBy = new Dictionary<string, string>();
            HashSet<string> active = new HashSet<string>();
            foreach (FileState fs in files.Values)
            {
                if (fs.Title != null && !fs.Sub)
                {
                    titleBy[fs.Session] = fs.Title;
                }
                // For the project name prefer the main session's working directory
                if (fs.Cwd != null && (!fs.Sub || !cwdBy.ContainsKey(fs.Session)))
                {
                    cwdBy[fs.Session] = fs.Cwd;
                }
                if (now - fs.Mtime < TimeSpan.FromSeconds(ActiveSeconds))
                {
                    active.Add(fs.Session);
                }
            }

            // Session name priority: a name you changed > the name in the Claude Code session file > the automatic summary (ai-title)
            foreach (FileState fs in files.Values)
            {
                string named = SessionNames.Get(fs.Session);
                if (named != null && !fs.Sub)
                {
                    titleBy[fs.Session] = named;
                }
            }
            foreach (FileState fs in files.Values)
            {
                if (fs.CustomTitle != null && !fs.Sub)
                {
                    titleBy[fs.Session] = fs.CustomTitle;
                }
            }
            // The title shown in VS Code has the highest priority (same as what you see in VS Code)
            foreach (FileState fs in files.Values)
            {
                string vs = VsCodeTitles.Get(fs.Session);
                if (vs != null && !fs.Sub)
                {
                    titleBy[fs.Session] = vs;
                }
            }

            Snapshot snap = new Snapshot();
            PeriodSummary shortP = snap.Periods[0];
            shortP.Label = "15 min";
            shortP.Range = Clock(shortStart) + "–" + Clock(now);
            PeriodSummary cur = snap.Periods[1];
            cur.Label = rolling ? "Last 5 h" : "This 5 h";
            cur.Range = Clock(curStart) + "–" + Clock(end);
            PeriodSummary prev = snap.Periods[2];
            prev.Label = rolling ? "Prior 5 h" : "Prev 5 h";
            prev.Range = Clock(prevStart) + "–" + Clock(curStart);

            FillPeriod(shortP, shortStart, DateTime.MaxValue, cwdBy, titleBy, active);
            FillPeriod(cur, curStart, DateTime.MaxValue, cwdBy, titleBy, active);
            FillPeriod(prev, prevStart, curStart, cwdBy, titleBy, active);

            // Cache expiry per session: time of the last cache read/write + the validity of that write (main sessions only; 5 minutes if no validity was recorded)
            Dictionary<string, DateTime> lastCache = new Dictionary<string, DateTime>();
            Dictionary<string, DateTime> ttlAt = new Dictionary<string, DateTime>();
            Dictionary<string, int> ttlBy = new Dictionary<string, int>();
            foreach (Rec r in recs.Values)
            {
                if (r.Agent != null)
                {
                    continue;
                }
                if (r.CacheRead > 0 || r.CacheWrite > 0)
                {
                    DateTime seen;
                    if (!lastCache.TryGetValue(r.Session, out seen) || r.Utc > seen)
                    {
                        lastCache[r.Session] = r.Utc;
                    }
                }
                if (r.TtlMinutes > 0)
                {
                    DateTime ttlSeen;
                    if (!ttlAt.TryGetValue(r.Session, out ttlSeen) || r.Utc > ttlSeen)
                    {
                        ttlAt[r.Session] = r.Utc;
                        ttlBy[r.Session] = r.TtlMinutes;
                    }
                }
            }
            Dictionary<string, DateTime> expiryBy = new Dictionary<string, DateTime>();
            foreach (KeyValuePair<string, DateTime> kv in lastCache)
            {
                int ttl;
                if (!ttlBy.TryGetValue(kv.Key, out ttl))
                {
                    ttl = 5;
                }
                expiryBy[kv.Key] = kv.Value.AddMinutes(ttl);
            }
            SetExpiry(shortP, expiryBy);
            SetExpiry(cur, expiryBy);
            SetExpiry(prev, expiryBy);

            // 1 hour (real-time, only computed from the records of the last 10 hours)
            PeriodSummary hourP = snap.Periods[3];
            hourP.Label = "1 h";
            hourP.Range = Clock(now.AddHours(-1)) + "–" + Clock(now);
            FillPeriod(hourP, now.AddHours(-1), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(hourP, expiryBy);

            // 5 minutes, 30 minutes, 3 hours (real-time like the 1 hour)
            PeriodSummary fiveP = snap.Periods[4];
            fiveP.Label = "5 min";
            fiveP.Range = Clock(now.AddMinutes(-5)) + "–" + Clock(now);
            FillPeriod(fiveP, now.AddMinutes(-5), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(fiveP, expiryBy);

            PeriodSummary halfP = snap.Periods[5];
            halfP.Label = "30 min";
            halfP.Range = Clock(now.AddMinutes(-30)) + "–" + Clock(now);
            FillPeriod(halfP, now.AddMinutes(-30), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(halfP, expiryBy);

            PeriodSummary threeP = snap.Periods[6];
            threeP.Label = "3 h";
            threeP.Range = Clock(now.AddHours(-3)) + "–" + Clock(now);
            FillPeriod(threeP, now.AddHours(-3), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(threeP, expiryBy);

            // Per session: last activity (any message, including subtasks), whether it is being written, cache expiry: used for panel sorting
            Dictionary<string, DateTime> lastBy = new Dictionary<string, DateTime>();
            foreach (Rec r in recs.Values)
            {
                DateTime lastSeen;
                if (!lastBy.TryGetValue(r.Session, out lastSeen) || r.Utc > lastSeen)
                {
                    lastBy[r.Session] = r.Utc;
                }
            }
            foreach (KeyValuePair<string, DateTime> kv in lastBy)
            {
                LiveInfo liveInfo = new LiveInfo();
                liveInfo.LastUtc = kv.Value;
                liveInfo.Active = active.Contains(kv.Key);
                DateTime expUtc;
                if (expiryBy.TryGetValue(kv.Key, out expUtc))
                {
                    liveInfo.ExpiryUtc = expUtc;
                }
                snap.Live[kv.Key] = liveInfo;
            }

            // Signature: do not redraw if neither the numbers nor the time ranges changed
            StringBuilder sb = new StringBuilder();
            foreach (PeriodSummary p in snap.Periods)
            {
                sb.Append(p.Range).Append('|').Append(p.Total).Append('|').Append(p.CacheRead);
                foreach (SessionTotal t in p.Rows)
                {
                    sb.Append('|').Append(t.Session).Append(':').Append(t.Total).Append(':').Append(t.SubCount).Append(':').Append(t.Active ? 1 : 0).Append(':').Append(t.Title);
                }
                sb.Append('#');
            }
            snap.Signature = sb.ToString();
            return snap;
        }

        // Summarize the records in the [from, to) time range, grouped by session
        private void FillPeriod(PeriodSummary p, DateTime from, DateTime to, Dictionary<string, string> cwdBy, Dictionary<string, string> titleBy, HashSet<string> active)
        {
            Dictionary<string, SessionTotal> map = new Dictionary<string, SessionTotal>();
            Dictionary<string, HashSet<string>> agents = new Dictionary<string, HashSet<string>>();
            Dictionary<string, ModelTotal> byModel = new Dictionary<string, ModelTotal>();
            Dictionary<string, TierTotal> byTier = new Dictionary<string, TierTotal>();
            foreach (Rec r in recs.Values)
            {
                if (r.Utc < from || r.Utc >= to)
                {
                    continue;
                }
                long all = r.In + r.Out + r.CacheWrite + r.CacheRead;
                string tierK = TagStore.TierKey(TagStore.Get(r.Id) ?? TagStore.FromRec(r));
                string mkey = r.Session + "\n" + r.Model + "\n" + tierK;
                ModelTotal mt;
                if (!byModel.TryGetValue(mkey, out mt))
                {
                    mt = new ModelTotal();
                    mt.Session = r.Session;
                    mt.Model = r.Model;
                    mt.Tier = tierK;
                    byModel[mkey] = mt;
                }
                mt.Msgs++;
                if (r.Utc < mt.FirstUtc)
                {
                    mt.FirstUtc = r.Utc;
                }
                if (r.Utc > mt.LastUtc)
                {
                    mt.LastUtc = r.Utc;
                }
                mt.Total += all;
                mt.CacheRead += r.CacheRead;
                AddTokens(mt.Tok, r.In, r.Out, r.CacheWrite - r.CacheWrite1h, r.CacheWrite1h, r.CacheRead);
                TierAdd(byTier, r);
                SessionTotal t = GetOrAdd(map, r.Session);
                if (r.Utc > t.LastUtc)
                {
                    t.LastUtc = r.Utc;
                }
                t.Total += all;
                t.Input += r.In;
                t.Output += r.Out;
                t.CacheWrite += r.CacheWrite;
                t.CacheRead += r.CacheRead;
                p.Total += all;
                p.Input += r.In;
                p.Output += r.Out;
                p.CacheWrite += r.CacheWrite;
                p.CacheRead += r.CacheRead;
                if (r.Agent != null)
                {
                    HashSet<string> set;
                    if (!agents.TryGetValue(r.Session, out set))
                    {
                        set = new HashSet<string>();
                        agents[r.Session] = set;
                    }
                    set.Add(r.Agent);
                }
            }

            foreach (SessionTotal t in map.Values)
            {
                if (t.Total == 0)
                {
                    continue;
                }
                HashSet<string> set;
                if (agents.TryGetValue(t.Session, out set))
                {
                    t.SubCount = set.Count;
                }
                string cwd;
                cwdBy.TryGetValue(t.Session, out cwd);
                t.Name = NameOf(cwd);
                string title;
                if (titleBy.TryGetValue(t.Session, out title))
                {
                    t.Title = title;
                }
                t.Active = active.Contains(t.Session);
                p.Rows.Add(t);
            }
            p.Rows.Sort(delegate(SessionTotal a, SessionTotal b) { return b.Total.CompareTo(a.Total); });
            foreach (ModelTotal mt in byModel.Values)
            {
                if (mt.Total > 0)
                {
                    p.ModelRows.Add(mt);
                }
            }
            p.ModelRows.Sort(delegate(ModelTotal a, ModelTotal b) { return b.Total.CompareTo(a.Total); });
            p.TierRows.AddRange(byTier.Values);
            p.TierRows.Sort(delegate(TierTotal a, TierTotal b) { return b.Total.CompareTo(a.Total); });
        }

        private static void SetExpiry(PeriodSummary p, Dictionary<string, DateTime> expiryBy)
        {
            foreach (SessionTotal t in p.Rows)
            {
                DateTime until;
                if (expiryBy.TryGetValue(t.Session, out until))
                {
                    t.CacheExpiryUtc = until;
                }
            }
        }

        private static SessionTotal GetOrAdd(Dictionary<string, SessionTotal> map, string session)
        {
            SessionTotal t;
            if (!map.TryGetValue(session, out t))
            {
                t = new SessionTotal();
                t.Session = session;
                map[session] = t;
            }
            return t;
        }

        private static string NameOf(string cwd)
        {
            if (string.IsNullOrEmpty(cwd))
            {
                return "Session";
            }
            string n = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(n) ? cwd : n;
        }

        // Convert a UTC time to local HH:mm
        private static string Clock(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private static string AsString(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        private static Dictionary<string, object> AsDict(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        private static long AsLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
            {
                return 0;
            }
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
    }

    // Read long paths over 260 characters. .NET Framework's DirectoryInfo.GetFiles silently skips those files,
    // so this enumerates with the Win32 API directly (FindFirstFileW) and opens files with the \\?\ prefix.
    internal static class LongPath
    {
        internal sealed class Entry
        {
            public string Path;         // Full path without the \\?\ prefix
            public long Size;
            public DateTime MtimeUtc;
            public string Session;      // Main session: the file name; subtask: the session ID inside <session ID>\subagents\...
            public bool Sub;
        }

        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x7;          // READ | WRITE | DELETE
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileAttributeDirectory = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh;
            public uint AccessLow, AccessHigh;
            public uint WriteLow, WriteHigh;
            public uint SizeHigh, SizeLow;
            public uint Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string name, out FindData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr handle, out FindData data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        // Session records under all Claude data folders (command line / VS Code / desktop app / added manually)
        public static List<Entry> ListAll()
        {
            List<Entry> res = new List<Entry>();
            foreach (string dir in ClaudeRoots.ProjectDirs())
            {
                res.AddRange(ListJsonl(dir));
            }
            return res;
        }

        // List all .jsonl files under root (recursively)
        public static List<Entry> ListJsonl(string root)
        {
            List<Entry> result = new List<Entry>();
            if (!Directory.Exists(root))
            {
                return result;
            }
            Walk(Prefixed(root), root, result);
            return result;
        }

        // Open a file (long paths allowed); the caller closes it
        public static FileStream Open(string path)
        {
            SafeFileHandle h = CreateFileW(Prefixed(path), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (h.IsInvalid)
            {
                throw new IOException("Cannot open file: " + path);
            }
            return new FileStream(h, FileAccess.Read, 1 << 20);
        }

        private static void Walk(string dirAccess, string dirPlain, List<Entry> result)
        {
            FindData d;
            IntPtr h = FindFirstFileW(dirAccess + "\\*", out d);
            if (h == new IntPtr(-1))
            {
                return;
            }
            try
            {
                do
                {
                    string name = d.FileName;
                    if (name == "." || name == "..")
                    {
                        continue;
                    }
                    string plain = dirPlain + "\\" + name;
                    if ((d.Attributes & FileAttributeDirectory) != 0)
                    {
                        Walk(dirAccess + "\\" + name, plain, result);
                    }
                    else if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(MakeEntry(plain, d));
                    }
                } while (FindNextFileW(h, out d));
            }
            finally
            {
                FindClose(h);
            }
        }

        private static Entry MakeEntry(string plain, FindData d)
        {
            Entry e = new Entry();
            e.Path = plain;
            e.Size = ((long)d.SizeHigh << 32) | d.SizeLow;
            e.MtimeUtc = DateTime.FromFileTimeUtc(((long)d.WriteHigh << 32) | d.WriteLow);
            string[] parts = plain.Split('\\');
            int sub = -1;
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], "subagents", StringComparison.OrdinalIgnoreCase))
                {
                    sub = i;
                    break;
                }
            }
            if (sub > 0)
            {
                e.Sub = true;
                e.Session = parts[sub - 1];
            }
            else
            {
                string file = parts[parts.Length - 1];
                e.Session = file.Substring(0, file.Length - ".jsonl".Length);
            }
            return e;
        }

        private static string Prefixed(string p)
        {
            return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p : @"\\?\" + p;
        }
    }

    // Usage log (v2):
    //   usage_detail_v2.csv -- one row per API message (de-duplicated by message ID), with the task ID (the promptId of this question), tool call count and tool names
    //   session_index.csv   -- one row per session: title, start/last time, message count, token totals by type, main session file path, to make it easy to find the records later
    //   quota_snapshots.csv -- snapshots of the 5-hour and weekly percent used
    // Only reads Claude Code's session records (opened in shared mode, never blocks it from writing, never modifies the original files).
    // The old "usage_detail.csv" and "read_progress.txt" are left as they are and never deleted or overwritten.
    // Starts about 30 seconds after launch, then every 60 seconds reads only what was appended since last time.
    internal sealed class UsageLogger
    {
        private const int FirstDelaySeconds = 30;
        private const int ScanSeconds = 60;
        private const string DetailHeader = "Time,Session ID,Project,Model,Input,Output,Cache write 5m,Cache write 1h,Cache read,Subagent,Message ID,Task ID,Tool calls,Tool names";
        private const string IndexHeader = "Session ID,Project,Title,Start time,Last time,Messages,Subagent messages,Input,Output,Cache write,Cache read,Session file";
        private const string QuotaHeader = "Time,5h used %,5h resets at,Weekly used %,Weekly resets at";
        private const string TagHeader = "Message ID,Tier,Ultra,Speed,Thinking tokens,Entrypoint";
        private const int TagBudgetSeconds = 20;    // When backfilling the history tags, read for at most this long per round and leave the rest to the next round (do not read all session records at once)
        private static readonly byte[] UltraTag = Encoding.ASCII.GetBytes("\"ultra_effort");
        private static readonly byte[] UsageTag = Encoding.ASCII.GetBytes("\"usage\"");
        private static readonly byte[] PromptTag = Encoding.ASCII.GetBytes("\"promptId\":\"");
        private static readonly byte[] UserTag = Encoding.ASCII.GetBytes("\"type\":\"user\"");
        private static readonly byte[] TitleTag = Encoding.ASCII.GetBytes("\"ai-title\"");
        private static readonly byte[] CustomTag = Encoding.ASCII.GetBytes("\"custom-title\"");

        private readonly string logDir;
        private readonly string detailPath;
        private readonly string indexPath;
        private readonly string quotaPath;
        private readonly string progressPath;
        private readonly string tagPath;
        private readonly string tagProgressPath;
        private readonly string ultraPath;
        private readonly HashSet<string> tagged = new HashSet<string>();
        private readonly Dictionary<string, long> tagOffsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Session -> periods in which Ultracode was on [start, end] (UTC; end = MaxValue means still on). A subtask is counted by the main session it belongs to
        private readonly Dictionary<string, List<DateTime[]>> ultra = new Dictionary<string, List<DateTime[]>>();
        private bool ultraDirty;
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly Dictionary<string, long> offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> prompts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SessionInfo> index = new Dictionary<string, SessionInfo>();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly System.Threading.Timer timer;
        private bool loaded;
        private bool indexDirty;
        private int busy;
        private string lastQuotaKey = "";
        private DateTime lastQuotaUtc = DateTime.MinValue;

        public bool Enabled = true;

        public string DetailPath
        {
            get { return detailPath; }
        }

        public string IndexPath
        {
            get { return indexPath; }
        }

        public UsageLogger(string logDir)
        {
            this.logDir = logDir;
            detailPath = Path.Combine(logDir, "usage_detail_v2.csv");
            indexPath = Path.Combine(logDir, "session_index.csv");
            quotaPath = Path.Combine(logDir, "quota_snapshots.csv");
            progressPath = Path.Combine(logDir, "read_progress_v2.txt");
            tagPath = Path.Combine(logDir, "usage_tags.csv");
            tagProgressPath = Path.Combine(logDir, "read_progress_tags.txt");
            ultraPath = Path.Combine(logDir, "ultra_periods.csv");
            timer = new System.Threading.Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public string TagPath
        {
            get { return tagPath; }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        private const int ThreadModeBackgroundBegin = 0x00010000;   // The thread enters background mode: CPU, disk I/O and memory priority are all lowered to the minimum
        private const int ThreadModeBackgroundEnd = 0x00020000;

        private sealed class SessionInfo
        {
            public string Id;
            public string Project = "";
            public string Title = "";
            public string CustomTitle;
            public string Path = "";
            public DateTime First = DateTime.MaxValue;
            public DateTime Last = DateTime.MinValue;
            public long Msgs, SubMsgs, Inp, Outp, Cw, Cr;
        }

        public void Start()
        {
            timer.Change(FirstDelaySeconds * 1000, ScanSeconds * 1000);
        }

        private void OnTimer(object state)
        {
            if (!Enabled || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            {
                return;
            }
            ThreadPriority old = Thread.CurrentThread.Priority;
            bool background = false;
            try
            {
                // Backfilling history reads many files, so lower the priority and try not to take CPU and disk from other programs (background mode also lowers disk priority to the minimum)
                background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
                if (!background)
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                }
                ScanOnce();
            }
            catch (Exception)
            {
                // If this round fails wait for the next round (the part whose state was not saved is processed again next round)
            }
            finally
            {
                if (background)
                {
                    SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
                }
                else
                {
                    Thread.CurrentThread.Priority = old;
                }
                Interlocked.Exchange(ref busy, 0);
            }
        }

        // One scan round: find the session files with new content, read only the new part, append it to the detail file and update the session index
        internal void ScanOnce()
        {
            if (!loaded)
            {
                LoadState();
                loaded = true;
            }
            List<LongPath.Entry> list = LongPath.ListAll();
            if (list.Count == 0)
            {
                return;
            }
            Directory.CreateDirectory(logDir);
            // Main sessions first: a subtask's Ultracode state depends on the switch times of the main session it belongs to
            list.Sort(delegate(LongPath.Entry a, LongPath.Entry b) { return a.Sub == b.Sub ? 0 : (a.Sub ? 1 : -1); });
            System.Diagnostics.Stopwatch budget = System.Diagnostics.Stopwatch.StartNew();
            using (StreamWriter w = new StreamWriter(detailPath, true, new UTF8Encoding(true)))
            using (StreamWriter tw = new StreamWriter(tagPath, true, new UTF8Encoding(true)))
            {
                if (w.BaseStream.Length == 0)
                {
                    w.WriteLine(DetailHeader);
                }
                if (tw.BaseStream.Length == 0)
                {
                    tw.WriteLine(TagHeader);
                }
                foreach (LongPath.Entry e in list)
                {
                    long off;
                    if (!offsets.TryGetValue(e.Path, out off))
                    {
                        off = 0;
                    }
                    long toff;
                    if (!tagOffsets.TryGetValue(e.Path, out toff))
                    {
                        toff = 0;
                    }
                    if (e.Size < off)
                    {
                        off = 0;    // The file was replaced or truncated: read from the start; messages already recorded are de-duplicated by message ID
                    }
                    if (e.Size < toff)
                    {
                        toff = 0;
                    }
                    if (e.Size == off && e.Size == toff)
                    {
                        continue;
                    }
                    if (toff < off && budget.Elapsed.TotalSeconds > TagBudgetSeconds)
                    {
                        // Backfilling the tags has read long enough: this file only processes the newly appended detail as usual and leaves the tags for the next round
                        if (e.Size > off)
                        {
                            offsets[e.Path] = ReadLines(e, off, off, w, null);
                        }
                        continue;
                    }
                    long done = ReadLines(e, Math.Min(off, toff), off, w, tw);
                    offsets[e.Path] = done;
                    tagOffsets[e.Path] = done;
                }
            }
            if (indexDirty)
            {
                WriteIndex();
                indexDirty = false;
            }
            SaveState();
        }

        // Read line by line from off, only processing up to the last newline; returns the position reached
        // fromPos: where to start reading (when backfilling tags it is earlier than the detail read position mainOff, and that stretch of detail is de-duplicated by message ID); tw = null means no tags are written this round
        private long ReadLines(LongPath.Entry e, long fromPos, long mainOff, StreamWriter w, StreamWriter tw)
        {
            long off = fromPos;
            string prompt;
            if (fromPos < mainOff || !prompts.TryGetValue(e.Path, out prompt))
            {
                prompt = "";    // Replay from an earlier position: the current prompt is rebuilt from the start
            }
            long done = off;
            long pos = off;
            byte[] buf = new byte[1 << 20];
            MemoryStream partial = new MemoryStream();
            using (FileStream s = LongPath.Open(e.Path))
            {
                s.Seek(off, SeekOrigin.Begin);
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    int start = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] != 10)
                        {
                            continue;
                        }
                        partial.Write(buf, start, i - start);
                        prompt = ProcessLine(partial.GetBuffer(), (int)partial.Length, e, prompt, w, tw);
                        partial.SetLength(0);
                        start = i + 1;
                        done = pos + i + 1;
                    }
                    partial.Write(buf, start, n - start);
                    pos += n;
                }
            }
            prompts[e.Path] = prompt;
            return done;
        }

        // One line: a user message updates the "current prompt"; ai-title updates the session title; a reply carrying usage is written to the detail
        private string ProcessLine(byte[] b, int len, LongPath.Entry e, string prompt, StreamWriter w, StreamWriter tw)
        {
            if (len == 0)
            {
                return prompt;
            }
            if (tw != null && IndexOf(b, len, UltraTag) >= 0)
            {
                HandleUltra(b, len, e);
            }
            if (IndexOf(b, len, PromptTag) >= 0 && IndexOf(b, len, UserTag) >= 0)
            {
                return ExtractPrompt(b, len) ?? prompt;
            }
            if (IndexOf(b, len, TitleTag) >= 0 || IndexOf(b, len, CustomTag) >= 0)
            {
                HandleTitle(b, len, e);
            }
            if (IndexOf(b, len, UsageTag) >= 0)
            {
                HandleUsage(b, len, e, prompt, w, tw);
            }
            return prompt;
        }

        private void HandleTitle(byte[] b, int len, LongPath.Entry e)
        {
            if (e.Sub)
            {
                return;
            }
            Dictionary<string, object> o = ParseJson(b, len);
            if (o == null)
            {
                return;
            }
            string type = AsString(o, "type");
            if (type == "custom-title")
            {
                string custom = AsString(o, "customTitle");
                if (!string.IsNullOrEmpty(custom))
                {
                    SessionInfo ci = GetInfo(e.Session);
                    if (ci.CustomTitle != custom.Trim())
                    {
                        ci.CustomTitle = custom.Trim();
                        indexDirty = true;
                    }
                }
                return;
            }
            if (type != "ai-title")
            {
                return;
            }
            string title = AsString(o, "aiTitle");
            if (string.IsNullOrEmpty(title))
            {
                return;
            }
            SessionInfo info = GetInfo(e.Session);
            if (info.Title != title.Trim() || info.Path != e.Path)
            {
                info.Title = title.Trim();
                info.Path = e.Path;
                indexDirty = true;
            }
        }

        private void HandleUsage(byte[] b, int len, LongPath.Entry e, string prompt, StreamWriter w, StreamWriter tw)
        {
            Dictionary<string, object> o = ParseJson(b, len);
            if (o == null)
            {
                return;
            }
            Dictionary<string, object> m = AsDict(o, "message");
            Dictionary<string, object> u = m == null ? null : AsDict(m, "usage");
            string id = m == null ? null : AsString(m, "id");
            if (u == null || string.IsNullOrEmpty(id))
            {
                return;
            }
            bool isNew = !seen.Contains(id);
            bool needTag = tw != null && !tagged.Contains(id);
            if (!isNew && !needTag)
            {
                return;
            }
            DateTimeOffset dto;
            string ts = AsString(o, "timestamp");
            if (ts == null || !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
            {
                return;
            }
            if (needTag)
            {
                // Tier tags: the thinking effort (actually used in this turn), whether Ultracode was on, fast mode, thinking tokens, entrypoint
                tagged.Add(id);
                string effort = AsString(o, "perTurnEffort");
                if (string.IsNullOrEmpty(effort))
                {
                    effort = AsString(o, "effort") ?? "";
                }
                Dictionary<string, object> otd = AsDict(u, "output_tokens_details");
                tw.WriteLine(id + "," + effort.Replace(",", "") + "," + (IsUltra(e.Session, dto.UtcDateTime) ? 1 : 0) + ","
                    + (AsString(u, "speed") ?? "").Replace(",", "") + "," + (otd == null ? 0 : AsLong(otd, "thinking_tokens")) + ","
                    + (AsString(o, "entrypoint") ?? "").Replace(",", ""));
            }
            if (!isNew)
            {
                return;
            }
            seen.Add(id);

            string cwd = AsString(o, "cwd");
            string project = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
            long input = AsLong(u, "input_tokens");
            long output = AsLong(u, "output_tokens");
            long cacheRead = AsLong(u, "cache_read_input_tokens");
            long cacheWriteAll = AsLong(u, "cache_creation_input_tokens");
            Dictionary<string, object> cc = AsDict(u, "cache_creation");
            long cw1 = cc == null ? 0 : AsLong(cc, "ephemeral_1h_input_tokens");
            long cw5 = Math.Max(0, cacheWriteAll - cw1);
            int toolCount;
            string toolNames = ToolInfo(m, out toolCount);

            w.WriteLine(dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ","
                + Csv(e.Session) + "," + Csv(project) + "," + Csv(AsString(m, "model") ?? "") + ","
                + input + "," + output + "," + cw5 + "," + cw1 + "," + cacheRead + ","
                + (e.Sub ? 1 : 0) + "," + id + "," + Csv(prompt ?? "") + "," + toolCount + "," + Csv(toolNames));

            SessionInfo info = GetInfo(e.Session);
            DateTime when = dto.LocalDateTime;
            if (when < info.First)
            {
                info.First = when;
            }
            if (when > info.Last)
            {
                info.Last = when;
            }
            info.Msgs++;
            if (e.Sub)
            {
                info.SubMsgs++;
            }
            info.Inp += input;
            info.Outp += output;
            info.Cw += cw5 + cw1;
            info.Cr += cacheRead;
            if (info.Project == "" && project != "")
            {
                info.Project = project;
            }
            if (!e.Sub && info.Path == "")
            {
                info.Path = e.Path;
            }
            indexDirty = true;
        }

        // The Ultracode switch: in the records it is an attachment with attachment.type = ultra_effort_enter (turned on);
        // the type name used when turning it off has not been seen yet; anything containing exit / leave / off / end / disable is treated as off
        private void HandleUltra(byte[] b, int len, LongPath.Entry e)
        {
            Dictionary<string, object> o = ParseJson(b, len);
            if (o == null || AsString(o, "type") != "attachment")
            {
                return;
            }
            Dictionary<string, object> a = AsDict(o, "attachment");
            string t = a == null ? null : AsString(a, "type");
            if (t == null || !t.StartsWith("ultra_effort", StringComparison.Ordinal))
            {
                return;
            }
            DateTimeOffset dto;
            string ts = AsString(o, "timestamp");
            if (ts == null || !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
            {
                return;
            }
            bool off = t.IndexOf("exit", StringComparison.Ordinal) >= 0 || t.IndexOf("leave", StringComparison.Ordinal) >= 0
                || t.IndexOf("off", StringComparison.Ordinal) >= 0 || t.IndexOf("end", StringComparison.Ordinal) >= 0
                || t.IndexOf("disable", StringComparison.Ordinal) >= 0;
            SetUltra(e.Session, dto.UtcDateTime, !off);
        }

        private void SetUltra(string session, DateTime utc, bool on)
        {
            List<DateTime[]> spans;
            if (!ultra.TryGetValue(session, out spans))
            {
                spans = new List<DateTime[]>();
                ultra[session] = spans;
            }
            DateTime[] last = spans.Count > 0 ? spans[spans.Count - 1] : null;
            if (on)
            {
                if (last != null && last[1] >= utc)
                {
                    return;     // Already on, or this "on" event was recorded long ago (replay)
                }
                spans.Add(new DateTime[] { utc, DateTime.MaxValue });
                ultraDirty = true;
            }
            else if (last != null && last[1] == DateTime.MaxValue && utc > last[0])
            {
                last[1] = utc;
                ultraDirty = true;
            }
        }

        private bool IsUltra(string session, DateTime utc)
        {
            List<DateTime[]> spans;
            if (!ultra.TryGetValue(session, out spans))
            {
                return false;
            }
            foreach (DateTime[] sp in spans)
            {
                if (utc >= sp[0] && utc < sp[1])
                {
                    return true;
                }
            }
            return false;
        }

        // Tool calls in this reply: the count and the tool names used (in order of first appearance, de-duplicated)
        private static string ToolInfo(Dictionary<string, object> m, out int count)
        {
            count = 0;
            object cv;
            if (m == null || !m.TryGetValue("content", out cv) || cv is string)
            {
                return "";
            }
            System.Collections.IEnumerable items = cv as System.Collections.IEnumerable;
            if (items == null)
            {
                return "";
            }
            List<string> names = new List<string>();
            foreach (object item in items)
            {
                Dictionary<string, object> bd = item as Dictionary<string, object>;
                if (bd == null || AsString(bd, "type") != "tool_use")
                {
                    continue;
                }
                count++;
                string nm = AsString(bd, "name");
                if (!string.IsNullOrEmpty(nm) && names.IndexOf(nm) < 0)
                {
                    names.Add(nm);
                }
            }
            return string.Join(";", names.ToArray());
        }

        private SessionInfo GetInfo(string session)
        {
            SessionInfo info;
            if (!index.TryGetValue(session, out info))
            {
                info = new SessionInfo();
                info.Id = session;
                index[session] = info;
            }
            return info;
        }

        // Session index: sorted from newest to oldest by last time; the whole file is rewritten (only a few hundred lines)
        private void WriteIndex()
        {
            List<SessionInfo> list = new List<SessionInfo>(index.Values);
            list.Sort(delegate(SessionInfo a, SessionInfo b) { return b.Last.CompareTo(a.Last); });
            using (StreamWriter w = new StreamWriter(indexPath, false, new UTF8Encoding(true)))
            {
                w.WriteLine(IndexHeader);
                foreach (SessionInfo s in list)
                {
                    w.WriteLine(Csv(s.Id) + "," + Csv(s.Project) + "," + Csv(VsCodeTitles.Get(s.Id) ?? s.CustomTitle ?? SessionNames.Get(s.Id) ?? s.Title) + "," + TimeText(s.First) + "," + TimeText(s.Last) + ","
                        + s.Msgs + "," + s.SubMsgs + "," + s.Inp + "," + s.Outp + "," + s.Cw + "," + s.Cr + "," + Csv(s.Path));
                }
            }
        }

        // Quota snapshots: one row when the value or the reset time changes; when nothing changed one row every 15 minutes
        public void LogQuota(FetchResult r)
        {
            if (!Enabled || r.Problem != Problem.None)
            {
                return;
            }
            string key = Pct(r.FiveHour) + "|" + Reset(r.FiveHour) + "|" + Pct(r.SevenDay) + "|" + Reset(r.SevenDay);
            if (key == lastQuotaKey && DateTime.UtcNow - lastQuotaUtc < TimeSpan.FromMinutes(15))
            {
                return;
            }
            lastQuotaKey = key;
            lastQuotaUtc = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(logDir);
                using (StreamWriter w = new StreamWriter(quotaPath, true, new UTF8Encoding(true)))
                {
                    if (w.BaseStream.Length == 0)
                    {
                        w.WriteLine(QuotaHeader);
                    }
                    w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ","
                        + Pct(r.FiveHour) + "," + Reset(r.FiveHour) + "," + Pct(r.SevenDay) + "," + Reset(r.SevenDay));
                }
            }
            catch (IOException)
            {
                // The file is in use, record it at the next change
            }
        }

        private static string Pct(Quota q)
        {
            return q == null ? "" : q.Percent.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Reset(Quota q)
        {
            return q == null || !q.ResetsAt.HasValue ? "" : q.ResetsAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // Read back at startup: message IDs already recorded, the read position and current prompt of each file, the session index
        private void LoadState()
        {
            if (File.Exists(detailPath))
            {
                foreach (string line in File.ReadAllLines(detailPath, Encoding.UTF8))
                {
                    if (line.Length == 0 || line.StartsWith("Time,", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string[] f = SplitCsv(line);
                    if (f.Length > 10)
                    {
                        seen.Add(f[10]);
                    }
                }
            }
            if (File.Exists(tagPath))
            {
                foreach (string line in File.ReadAllLines(tagPath, Encoding.UTF8))
                {
                    int c = line.IndexOf(',');
                    if (c > 0 && !line.StartsWith("Message ID,", StringComparison.Ordinal) && !line.StartsWith("\uFEFFMessage ID,", StringComparison.Ordinal))
                    {
                        tagged.Add(line.Substring(0, c));
                    }
                }
            }
            if (File.Exists(tagProgressPath))
            {
                foreach (string line in File.ReadAllLines(tagProgressPath, Encoding.UTF8))
                {
                    int t1 = line.IndexOf('\t');
                    long off;
                    if (t1 > 0 && long.TryParse(line.Substring(0, t1), NumberStyles.Integer, CultureInfo.InvariantCulture, out off))
                    {
                        tagOffsets[line.Substring(t1 + 1)] = off;
                    }
                }
            }
            if (File.Exists(ultraPath))
            {
                foreach (string line in File.ReadAllLines(ultraPath, Encoding.UTF8))
                {
                    string[] f = SplitCsv(line);
                    DateTime st;
                    if (f.Length >= 2 && f[0] != "Session ID"
                        && DateTime.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out st))
                    {
                        DateTime en = DateTime.MaxValue;
                        DateTime parsedEnd;
                        if (f.Length >= 3 && f[2].Length > 0
                            && DateTime.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsedEnd))
                        {
                            en = parsedEnd;
                        }
                        List<DateTime[]> spans;
                        if (!ultra.TryGetValue(f[0], out spans))
                        {
                            spans = new List<DateTime[]>();
                            ultra[f[0]] = spans;
                        }
                        spans.Add(new DateTime[] { st, en });
                    }
                }
            }
            if (File.Exists(progressPath))
            {
                foreach (string line in File.ReadAllLines(progressPath, Encoding.UTF8))
                {
                    int t1 = line.IndexOf('\t');
                    int t2 = t1 < 0 ? -1 : line.IndexOf('\t', t1 + 1);
                    long off;
                    if (t2 < 0 || !long.TryParse(line.Substring(0, t1), NumberStyles.Integer, CultureInfo.InvariantCulture, out off))
                    {
                        continue;
                    }
                    string path = line.Substring(t2 + 1);
                    offsets[path] = off;
                    prompts[path] = line.Substring(t1 + 1, t2 - t1 - 1);
                }
            }
            if (File.Exists(indexPath))
            {
                foreach (string line in File.ReadAllLines(indexPath, Encoding.UTF8))
                {
                    if (line.Length == 0 || line.StartsWith("Session ID,", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string[] f = SplitCsv(line);
                    if (f.Length < 12)
                    {
                        continue;
                    }
                    SessionInfo info = new SessionInfo();
                    info.Id = f[0];
                    info.Project = f[1];
                    info.Title = f[2];
                    info.First = ParseTime(f[3], DateTime.MaxValue);
                    info.Last = ParseTime(f[4], DateTime.MinValue);
                    info.Msgs = ParseLong(f[5]);
                    info.SubMsgs = ParseLong(f[6]);
                    info.Inp = ParseLong(f[7]);
                    info.Outp = ParseLong(f[8]);
                    info.Cw = ParseLong(f[9]);
                    info.Cr = ParseLong(f[10]);
                    info.Path = f[11];
                    index[info.Id] = info;
                }
            }
        }

        private void SaveState()
        {
            using (StreamWriter tp = new StreamWriter(tagProgressPath, false, new UTF8Encoding(false)))
            {
                foreach (KeyValuePair<string, long> kv in tagOffsets)
                {
                    tp.WriteLine(kv.Value.ToString(CultureInfo.InvariantCulture) + "\t" + kv.Key);
                }
            }
            if (ultraDirty)
            {
                using (StreamWriter uw = new StreamWriter(ultraPath, false, new UTF8Encoding(true)))
                {
                    uw.WriteLine("Session ID,Start UTC,End UTC");
                    foreach (KeyValuePair<string, List<DateTime[]>> kv in ultra)
                    {
                        foreach (DateTime[] sp in kv.Value)
                        {
                            uw.WriteLine(kv.Key + "," + sp[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ","
                                + (sp[1] == DateTime.MaxValue ? "" : sp[1].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                        }
                    }
                }
                ultraDirty = false;
            }
            using (StreamWriter w = new StreamWriter(progressPath, false, new UTF8Encoding(false)))
            {
                foreach (KeyValuePair<string, long> kv in offsets)
                {
                    string prompt;
                    if (!prompts.TryGetValue(kv.Key, out prompt))
                    {
                        prompt = "";
                    }
                    w.WriteLine(kv.Value.ToString(CultureInfo.InvariantCulture) + "\t" + prompt + "\t" + kv.Key);
                }
            }
        }

        private static string TimeText(DateTime t)
        {
            return t == DateTime.MaxValue || t == DateTime.MinValue ? "" : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseTime(string s, DateTime fallback)
        {
            DateTime t;
            return DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : fallback;
        }

        private static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Parse one CSV line (fields may be double-quoted; a "" inside quotes means one ")
        private static string[] SplitCsv(string line)
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

        private static string Csv(string s)
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // The value of "promptId":"…" in a line (the whole JSON line is not parsed, to save time)
        private static string ExtractPrompt(byte[] b, int len)
        {
            int i = IndexOf(b, len, PromptTag);
            if (i < 0)
            {
                return null;
            }
            int start = i + PromptTag.Length;
            int end = start;
            while (end < len && b[end] != (byte)'"')
            {
                end++;
            }
            return end > start ? Encoding.ASCII.GetString(b, start, end - start) : null;
        }

        private Dictionary<string, object> ParseJson(byte[] b, int len)
        {
            try
            {
                return json.DeserializeObject(Encoding.UTF8.GetString(b, 0, len)) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int IndexOf(byte[] b, int len, byte[] pat)
        {
            int last = len - pat.Length;
            for (int i = 0; i <= last; i++)
            {
                int k = 0;
                while (k < pat.Length && b[i + k] == pat[k])
                {
                    k++;
                }
                if (k == pat.Length)
                {
                    return i;
                }
            }
            return -1;
        }

        private static string AsString(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        private static Dictionary<string, object> AsDict(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        private static long AsLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
            {
                return 0;
            }
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
    }

    // Per-message tier tags (logs\usage_tags.csv): thinking effort (low / medium / high / xhigh / max), whether Ultracode is on, whether fast mode is on, number of thinking tokens.
    // Written together with the details by UsageLogger; read back incrementally here and looked up by message ID when computing statistics
    internal static class TagStore
    {
        public sealed class Tag
        {
            public string Effort = "";
            public bool Ultra, Fast;
            public long Think;
        }

        private static readonly Dictionary<string, Tag> map = new Dictionary<string, Tag>();
        private static readonly object gate = new object();
        private static string path;
        private static long offset;
        private static DateTime checkedUtc = DateTime.MinValue;

        public static void Configure(string tagPath)
        {
            lock (gate)
            {
                if (path != tagPath)
                {
                    path = tagPath;
                    offset = 0;
                    map.Clear();
                    checkedUtc = DateTime.MinValue;
                }
            }
        }

        public static Tag Get(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            lock (gate)
            {
                Tag t;
                return map.TryGetValue(id, out t) ? t : null;
            }
        }

        // New messages whose tier tags are not in the file yet: use the thinking effort, fast mode and thinking tokens read directly from the record (Ultracode is only known from the tag file)
        public static Tag FromRec(UsageTracker.Rec r)
        {
            Tag t = new Tag();
            t.Effort = r.Effort ?? "";
            t.Fast = r.Fast;
            t.Think = r.Think;
            return t;
        }

        // Tier name: ultra (Ultracode on) > thinking effort; fast mode appends +fast; records without any are called "Unlabeled"
        public static string TierKey(Tag t)
        {
            if (t == null)
            {
                return "Unlabeled";
            }
            string k = t.Ultra ? "ultra" : (t.Effort.Length > 0 ? t.Effort : "Unlabeled");
            return t.Fast ? k + "+fast" : k;
        }

        // Read the newly appended part of the file; look at most once every 20 seconds
        public static void Refresh()
        {
            lock (gate)
            {
                if (path == null || DateTime.UtcNow - checkedUtc < TimeSpan.FromSeconds(20))
                {
                    return;
                }
                checkedUtc = DateTime.UtcNow;
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length < offset)
                        {
                            offset = 0;
                            map.Clear();
                        }
                        if (fs.Length == offset)
                        {
                            return;
                        }
                        fs.Seek(offset, SeekOrigin.Begin);
                        byte[] buf = new byte[fs.Length - offset];
                        int got = 0;
                        while (got < buf.Length)
                        {
                            int n = fs.Read(buf, got, buf.Length - got);
                            if (n <= 0)
                            {
                                break;
                            }
                            got += n;
                        }
                        int last = -1;
                        for (int i = got - 1; i >= 0; i--)
                        {
                            if (buf[i] == 10)
                            {
                                last = i;
                                break;
                            }
                        }
                        if (last < 0)
                        {
                            return;
                        }
                        offset += last + 1;
                        foreach (string line in Encoding.UTF8.GetString(buf, 0, last + 1).Split('\n'))
                        {
                            string[] f = line.TrimEnd('\r').Split(',');
                            if (f.Length < 5 || f[0].EndsWith("Message ID", StringComparison.Ordinal) || f[0].Length == 0)
                            {
                                continue;
                            }
                            Tag t = new Tag();
                            t.Effort = f[1];
                            t.Ultra = f[2] == "1";
                            t.Fast = string.Equals(f[3], "fast", StringComparison.OrdinalIgnoreCase);
                            long think;
                            long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out think);
                            t.Think = think;
                            map[f[0]] = t;
                        }
                    }
                }
                catch (IOException)
                {
                    checkedUtc = DateTime.MinValue;   // The file is being written, read again next time
                }
            }
        }
    }

    // Quota conversion table: logs\quota_conversion.csv, derived from the quota snapshots by logs\quota_fit.py.
    // Quota consumed ~ dollars at official prices x quota percentage points per dollar; for a model never measured, quota per dollar is the overall average (estimated from official prices)
    internal sealed class QuotaTable
    {
        public sealed class Entry
        {
            public readonly double[] Price = new double[5];   // Input, output, 5-minute cache write, 1-hour cache write, cache read (USD per million tokens)
            public double TierMult = 1;                        // Price multiplier applied to the whole request when the context exceeds LongContextTokens
            public double K5, KW;                              // 5-hour / weekly quota percentage points consumed per dollar; 0 = no result
            public bool Measured5, MeasuredW;                  // Measured (otherwise estimated from price)
        }

        public readonly Dictionary<string, Entry> Models = new Dictionary<string, Entry>();
        public readonly Dictionary<string, double[]> Tiers = new Dictionary<string, double[]>();   // Tier -> { 5-hour correction factor, weekly correction factor }, 1 if absent
        public double ReadScale = 1;                           // Scale of the cache-read unit price (fitted; 1 = official price)
        public double CalibErr = -1;                           // "Ex-ante prediction" error of the quota conversion: each block of data is fitted on the data before it and then predicted; mean relative error; -1 = none
        public int CalibBlocks;                                // Number of blocks taken into account
        public bool Stale;                                     // The file has no result for the current account / plan yet: keep only the price table (used by the subscription view to convert amounts), never use coefficients (show "-"), and do not apply another account's coefficients

        // key = the current "basis" (account:plan, e.g. B:pro). quota_fit.py writes one group of rows per basis (last column), with the current basis written last;
        // if the wanted basis does not exist yet use the last group in the file and mark it Stale
        public static QuotaTable Load(string path, string key)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            string use = key ?? "";
            string lastKey = "";
            bool found = false;
            foreach (string line in lines)
            {
                string[] f = line.Split(',');
                if (f.Length < 13 || f[0] == "Type")
                {
                    continue;
                }
                lastKey = f.Length > 22 ? f[22] : "";
                if (lastKey == use)
                {
                    found = true;
                }
            }
            if (!found)
            {
                use = lastKey;
            }
            QuotaTable t = new QuotaTable();
            t.Stale = !found && (key ?? "").Length > 0;
            foreach (string line in lines)
            {
                string[] f = line.Split(',');
                if (f.Length < 13 || f[0] == "Type")
                {
                    continue;
                }
                if ((f.Length > 22 ? f[22] : "") != use)
                {
                    continue;
                }
                if (t.Stale && f[0] != "Model")
                {
                    continue;
                }
                if (f[0] == "Global")
                {
                    double scale, err;
                    int blocks;
                    if (f.Length > 16 && double.TryParse(f[16], NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 0)
                    {
                        t.ReadScale = scale;
                    }
                    if (f.Length > 18 && double.TryParse(f[17], NumberStyles.Float, CultureInfo.InvariantCulture, out err)
                        && int.TryParse(f[18], NumberStyles.Integer, CultureInfo.InvariantCulture, out blocks) && blocks > 0)
                    {
                        t.CalibErr = err;
                        t.CalibBlocks = blocks;
                    }
                    continue;
                }
                if (f[0] == "Tier")
                {
                    double d5, dw;
                    if (double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out d5) && d5 > 0)
                    {
                        if (!double.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out dw) || dw <= 0)
                        {
                            dw = d5;
                        }
                        t.Tiers[f[1]] = new double[] { d5, dw };
                    }
                    continue;
                }
                Entry e = new Entry();
                bool ok = true;
                for (int i = 0; i < 5; i++)
                {
                    ok = ok && double.TryParse(f[2 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out e.Price[i]);
                }
                if (!ok)
                {
                    continue;
                }
                double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out e.K5);
                double.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out e.KW);
                if (t.Stale)
                {
                    e.K5 = 0;
                    e.KW = 0;
                }
                e.Measured5 = f[11] == "Measured";
                e.MeasuredW = f[12] == "Measured";
                double mult;
                if (f.Length > 15 && double.TryParse(f[15], NumberStyles.Float, CultureInfo.InvariantCulture, out mult) && mult > 0)
                {
                    e.TierMult = mult;
                }
                t.Models[f[1]] = e;
            }
            return t.Models.Count > 0 ? t : null;
        }

        // Tier correction factors (1 for tiers that have not been measured)
        public double TierFactor(string tier, bool week)
        {
            double[] d;
            return tier != null && Tiers.TryGetValue(tier, out d) ? (week ? d[1] : d[0]) : 1.0;
        }

        // tok: the 10 items of ModelTotal.Tok. Returns the converted dollar amount (cache reads use the fitted scale, for estimating quota)
        public double CostUsd(Entry e, long[] tok)
        {
            return CostUsdAt(e, tok, ReadScale);
        }

        // readScale = 1 means pure official prices (used for the "API-equivalent amount" in the subscription view: the real official list price, not a fitted quota weight)
        public double CostUsdAt(Entry e, long[] tok, double readScale)
        {
            double sum = 0;
            for (int b = 0; b < 2; b++)
            {
                int o = b * 5;
                double c = tok[o] * e.Price[0] + tok[o + 1] * e.Price[1] + tok[o + 2] * e.Price[2] + tok[o + 3] * e.Price[3]
                    + tok[o + 4] * e.Price[4] * readScale;
                sum += (b == 0 ? 1.0 : e.TierMult) * c;
            }
            return sum / 1000000.0;
        }
    }

    // Usage statistics by time range from the usage log (usage_detail_v2.csv): one read computes several ranges at once. The result has the same format as the live ranges
    internal static class LogSummary
    {
        public static UsageTracker.PeriodSummary[] ComputeMany(string detailPath, string indexPath, DateTime[] froms, DateTime[] tos, string[] labels)
        {
            return ComputeMany(detailPath, indexPath, froms, tos, labels, null, null);
        }

        // accts: parallel to froms; for entries that are not null only records that "belong to this account at that moment" are counted (judged by the switch timeline idx)
        public static UsageTracker.PeriodSummary[] ComputeMany(string detailPath, string indexPath, DateTime[] froms, DateTime[] tos, string[] labels,
            string[] accts, AccountIndex idx)
        {
            int n = froms.Length;
            UsageTracker.PeriodSummary[] res = new UsageTracker.PeriodSummary[n];
            Dictionary<string, UsageTracker.SessionTotal>[] sessions = new Dictionary<string, UsageTracker.SessionTotal>[n];
            Dictionary<string, UsageTracker.ModelTotal>[] models = new Dictionary<string, UsageTracker.ModelTotal>[n];
            Dictionary<string, UsageTracker.TierTotal>[] tiers = new Dictionary<string, UsageTracker.TierTotal>[n];
            TagStore.Refresh();
            DateTime lo = DateTime.MaxValue;
            DateTime hi = DateTime.MinValue;
            for (int k = 0; k < n; k++)
            {
                res[k] = new UsageTracker.PeriodSummary();
                res[k].Label = labels[k];
                res[k].Range = froms[k].ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " ~ " + tos[k].ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                sessions[k] = new Dictionary<string, UsageTracker.SessionTotal>();
                models[k] = new Dictionary<string, UsageTracker.ModelTotal>();
                tiers[k] = new Dictionary<string, UsageTracker.TierTotal>();
                if (froms[k] < lo)
                {
                    lo = froms[k];
                }
                if (tos[k] > hi)
                {
                    hi = tos[k];
                }
            }
            if (!File.Exists(detailPath))
            {
                return res;
            }
            Dictionary<string, string> indexTitles = ReadIndexTitles(indexPath);
            using (FileStream fs = new FileStream(detailPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    // Parse only the time first (the first field has no quotes); rows outside the range are skipped right away to save time
                    int comma = line.IndexOf(',');
                    DateTime t;
                    if (comma <= 0 || !DateTime.TryParseExact(line.Substring(0, comma), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        continue;
                    }
                    if (t < lo || t >= hi)
                    {
                        continue;
                    }
                    string[] f = ParseCsv(line);
                    if (f.Length < 11)
                    {
                        continue;
                    }
                    long inp = Num(f[4]), outp = Num(f[5]), cw = Num(f[6]) + Num(f[7]), cr = Num(f[8]);
                    TagStore.Tag tag = TagStore.Get(f[10]);
                    DateTime tUtc = accts != null && idx != null ? t.ToUniversalTime() : t;
                    for (int k = 0; k < n; k++)
                    {
                        if (t < froms[k] || t >= tos[k])
                        {
                            continue;
                        }
                        if (accts != null && accts[k] != null && idx != null && idx.AccountAt(tUtc) != accts[k])
                        {
                            continue;
                        }
                        Add(res[k], sessions[k], models[k], tiers[k], tag, f, inp, outp, cw, cr, indexTitles, t);
                    }
                }
            }
            for (int k = 0; k < n; k++)
            {
                Finish(res[k], sessions[k], models[k], tiers[k]);
            }
            return res;
        }

        private static void Add(UsageTracker.PeriodSummary p, Dictionary<string, UsageTracker.SessionTotal> sessions,
            Dictionary<string, UsageTracker.ModelTotal> models, Dictionary<string, UsageTracker.TierTotal> tiers, TagStore.Tag tag,
            string[] f, long inp, long outp, long cw, long cr, Dictionary<string, string> indexTitles, DateTime when)
        {
            long all = inp + outp + cw + cr;
            string session = f[1];
            UsageTracker.SessionTotal st;
            if (!sessions.TryGetValue(session, out st))
            {
                st = new UsageTracker.SessionTotal();
                st.Session = session;
                st.Name = string.IsNullOrEmpty(f[2]) ? "Session" : f[2];
                string idxTitle;
                bool hasIdx = indexTitles.TryGetValue(session, out idxTitle) && idxTitle.Length > 0;
                st.Title = VsCodeTitles.Get(session) ?? (hasIdx ? idxTitle : null) ?? SessionNames.Get(session);
                sessions[session] = st;
            }
            st.Total += all;
            st.Input += inp;
            st.Output += outp;
            st.CacheWrite += cw;
            st.CacheRead += cr;
            DateTime whenUtc = when.ToUniversalTime();
            if (whenUtc > st.LastUtc)
            {
                st.LastUtc = whenUtc;
            }
            string tierK = TagStore.TierKey(tag);
            string mkey = session + "\n" + f[3] + "\n" + tierK;
            UsageTracker.ModelTotal mt;
            if (!models.TryGetValue(mkey, out mt))
            {
                mt = new UsageTracker.ModelTotal();
                mt.Session = session;
                mt.Model = f[3];
                mt.Tier = tierK;
                models[mkey] = mt;
            }
            mt.Msgs++;
            if (whenUtc < mt.FirstUtc)
            {
                mt.FirstUtc = whenUtc;
            }
            if (whenUtc > mt.LastUtc)
            {
                mt.LastUtc = whenUtc;
            }
            mt.Total += all;
            mt.CacheRead += cr;
            long cw1 = Num(f[7]);
            UsageTracker.AddTokens(mt.Tok, inp, outp, cw - cw1, cw1, cr);
            string tier = TagStore.TierKey(tag);
            string tkey = f[3] + "\n" + tier;
            UsageTracker.TierTotal tt;
            if (!tiers.TryGetValue(tkey, out tt))
            {
                tt = new UsageTracker.TierTotal();
                tt.Model = f[3];
                tt.Tier = tier;
                tiers[tkey] = tt;
            }
            tt.Msgs++;
            tt.Total += all;
            tt.Out += outp;
            tt.Think += tag == null ? 0 : tag.Think;
            UsageTracker.AddTokens(tt.Tok, inp, outp, cw - cw1, cw1, cr);
            p.Total += all;
            p.Input += inp;
            p.Output += outp;
            p.CacheWrite += cw;
            p.CacheRead += cr;
        }

        private static void Finish(UsageTracker.PeriodSummary p, Dictionary<string, UsageTracker.SessionTotal> sessions,
            Dictionary<string, UsageTracker.ModelTotal> models, Dictionary<string, UsageTracker.TierTotal> tiers)
        {
            p.TierRows.AddRange(tiers.Values);
            p.TierRows.Sort(delegate(UsageTracker.TierTotal a, UsageTracker.TierTotal b) { return b.Total.CompareTo(a.Total); });
            foreach (UsageTracker.SessionTotal st in sessions.Values)
            {
                if (st.Total > 0)
                {
                    p.Rows.Add(st);
                }
            }
            p.Rows.Sort(delegate(UsageTracker.SessionTotal a, UsageTracker.SessionTotal b) { return b.Total.CompareTo(a.Total); });
            foreach (UsageTracker.ModelTotal mt in models.Values)
            {
                if (mt.Total > 0)
                {
                    p.ModelRows.Add(mt);
                }
            }
            p.ModelRows.Sort(delegate(UsageTracker.ModelTotal a, UsageTracker.ModelTotal b) { return b.Total.CompareTo(a.Total); });
        }

        private static Dictionary<string, string> ReadIndexTitles(string indexPath)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (!File.Exists(indexPath))
            {
                return d;
            }
            try
            {
                foreach (string line in File.ReadAllLines(indexPath, Encoding.UTF8))
                {
                    string[] f = ParseCsv(line);
                    if (f.Length >= 3 && f[0] != "Session ID")
                    {
                        d[f[0]] = f[2];
                    }
                }
            }
            catch (IOException)
            {
                // The index is being written, use an empty one this round
            }
            return d;
        }

        private static long Num(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Parse one CSV line (fields may be double-quoted; a "" inside quotes means one ")
        private static string[] ParseCsv(string line)
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
    }

    // Display settings: tick the time tabs to show in the panel
    internal sealed class TabSettingsForm : Form
    {
        private readonly CheckBox[] boxes;

        public int Mask
        {
            get
            {
                int m = 0;
                for (int i = 0; i < boxes.Length; i++)
                {
                    if (boxes[i].Checked)
                    {
                        m |= 1 << i;
                    }
                }
                return m;
            }
        }

        public TabSettingsForm(int mask, string[] labels)
        {
            Text = "Display settings - time tabs";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(360, 250);

            Label tip = new Label();
            tip.Text = "Tick the time tabs to show in the panel (at least one):";
            tip.Location = new Point(12, 12);
            tip.AutoSize = true;
            Controls.Add(tip);

            boxes = new CheckBox[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                CheckBox c = new CheckBox();
                c.Text = labels[i];
                c.Checked = (mask & (1 << i)) != 0;
                c.AutoSize = true;
                c.Location = new Point(16 + (i % 2) * 170, 40 + (i / 2) * 24);
                boxes[i] = c;
                Controls.Add(c);
            }

            Button ok = new Button();
            ok.Text = "OK";
            ok.Size = new Size(70, 28);
            ok.Location = new Point(186, 206);
            ok.Click += delegate
            {
                if (Mask == 0)
                {
                    MessageBox.Show(this, "At least one time tab must be shown.", "Display settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Size = new Size(70, 28);
            cancel.Location = new Point(266, 206);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // Custom range: pick a start and end time (to the minute); the shortcut buttons work too
    internal sealed class CustomRangeForm : Form
    {
        private readonly DateTimePicker fromPicker;
        private readonly DateTimePicker toPicker;

        public DateTime From
        {
            get { return fromPicker.Value; }
        }

        public DateTime To
        {
            get { return toPicker.Value; }
        }

        public CustomRangeForm(DateTime from, DateTime to)
        {
            Text = "Custom range";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(360, 200);

            Label lf = new Label();
            lf.Text = "Start";
            lf.Location = new Point(16, 22);
            lf.AutoSize = true;
            fromPicker = MakePicker(from, 60, 18);
            Label lt = new Label();
            lt.Text = "End";
            lt.Location = new Point(16, 56);
            lt.AutoSize = true;
            toPicker = MakePicker(to, 60, 52);

            string[] presets = { "Last 1 h", "Last 6 h", "Today", "Yesterday", "This week" };
            Controls.Add(lf);
            Controls.Add(fromPicker);
            Controls.Add(lt);
            Controls.Add(toPicker);
            for (int i = 0; i < presets.Length; i++)
            {
                Button b = new Button();
                b.Text = presets[i];
                b.Size = new Size(64, 26);
                b.Location = new Point(16 + i * 68, 96);
                int which = i;
                b.Click += delegate { ApplyPreset(which); };
                Controls.Add(b);
            }

            Button ok = new Button();
            ok.Text = "OK";
            ok.Size = new Size(70, 28);
            ok.Location = new Point(186, 150);
            ok.Click += delegate
            {
                if (From >= To)
                {
                    MessageBox.Show(this, "The end time must be later than the start time.", "Custom range", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Size = new Size(70, 28);
            cancel.Location = new Point(266, 150);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private DateTimePicker MakePicker(DateTime value, int x, int y)
        {
            DateTimePicker p = new DateTimePicker();
            p.Format = DateTimePickerFormat.Custom;
            p.CustomFormat = "yyyy-MM-dd HH:mm";
            p.ShowUpDown = true;
            p.Location = new Point(x, y);
            p.Size = new Size(280, 24);
            p.Value = value;
            return p;
        }

        private void ApplyPreset(int which)
        {
            DateTime now = DateTime.Now;
            DateTime from;
            DateTime to = now;
            switch (which)
            {
                case 0:
                    from = now.AddHours(-1);
                    break;
                case 1:
                    from = now.AddHours(-6);
                    break;
                case 2:
                    from = now.Date;
                    break;
                case 3:
                    from = now.Date.AddDays(-1);
                    to = now.Date;
                    break;
                default:
                    from = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));   // Monday 00:00
                    break;
            }
            fromPicker.Value = from;
            toPicker.Value = to;
        }
    }

    // Session drill-down: lists a session's totals, cache hit rate and recent API messages from logs\usage_detail.csv
    internal sealed class SessionDrillForm : Form
    {
        private const int MaxRows = 300;
        private readonly string session;
        private readonly string detailPath;
        private readonly ListView list;
        private readonly Label summary;

        public SessionDrillForm(string session, string title, string detailPath)
        {
            this.session = session;
            this.detailPath = detailPath;
            Text = "Session details - " + title;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(680, 440);

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.Columns.Add("Time", 130);
            list.Columns.Add("Model", 160);
            list.Columns.Add("Input", 70, HorizontalAlignment.Right);
            list.Columns.Add("Output", 70, HorizontalAlignment.Right);
            list.Columns.Add("Cache W", 80, HorizontalAlignment.Right);
            list.Columns.Add("Cache R", 90, HorizontalAlignment.Right);
            list.Columns.Add("Subagent", 60, HorizontalAlignment.Center);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 42;
            Button copy = new Button();
            copy.Text = "Copy session ID";
            copy.SetBounds(8, 7, 110, 28);
            copy.Click += delegate { Clipboard.SetText(session); };
            Button open = new Button();
            open.Text = "Open log folder";
            open.SetBounds(126, 7, 130, 28);
            open.Click += delegate
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(detailPath) + "\"");
            };
            bottom.Controls.Add(copy);
            bottom.Controls.Add(open);
            Button record = new Button();
            record.Text = "Open session record";
            record.SetBounds(262, 7, 120, 28);
            record.Click += delegate { OpenTranscript(); };
            bottom.Controls.Add(record);

            summary = new Label();
            summary.Dock = DockStyle.Top;
            summary.Height = 58;
            summary.Padding = new Padding(10, 8, 10, 4);

            Controls.Add(list);
            Controls.Add(bottom);
            Controls.Add(summary);
            Load += delegate { LoadRows(); };
        }

        // Select this session's original record file in Explorer (the path comes from the session index)
        private void OpenTranscript()
        {
            string path;
            try
            {
                path = TranscriptPath();
            }
            catch (IOException)
            {
                path = null;   // The index is being written, click again in a moment
            }
            if (path == null)
            {
                MessageBox.Show(this, "Could not find this session's record file (Claude Code may have cleaned it up, or it is not in the session index yet).",
                    "Session record", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        private string TranscriptPath()
        {
            string indexPath = Path.Combine(Path.GetDirectoryName(detailPath), "session_index.csv");
            if (!File.Exists(indexPath))
            {
                return null;
            }
            foreach (string line in File.ReadAllLines(indexPath, Encoding.UTF8))
            {
                string[] f = SplitCsv(line);
                if (f.Length >= 12 && f[0] == session && f[11].Length > 0)
                {
                    try
                    {
                        using (LongPath.Open(f[11]))
                        {
                        }
                        return f[11];
                    }
                    catch (IOException)
                    {
                        return null;   // The original file is gone
                    }
                }
            }
            return null;
        }

        private void LoadRows()
        {
            list.BeginUpdate();
            list.Items.Clear();
            if (!File.Exists(detailPath))
            {
                summary.Text = "No log yet. Turn on \"Log usage to file\" in the widget's right-click menu first.";
                list.EndUpdate();
                return;
            }

            List<string[]> rows = new List<string[]>();
            foreach (string line in File.ReadAllLines(detailPath, Encoding.UTF8))
            {
                string[] f = SplitCsv(line);
                if (f.Length >= 11 && f[1] == session)
                {
                    rows.Add(f);
                }
            }

            long inp = 0, outp = 0, cw = 0, cr = 0;
            foreach (string[] f in rows)
            {
                inp += ParseLong(f[4]);
                outp += ParseLong(f[5]);
                cw += ParseLong(f[6]) + ParseLong(f[7]);
                cr += ParseLong(f[8]);
            }
            double denom = inp + cw + cr;
            string hit = denom > 0 ? (cr / denom * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
            summary.Text = "Messages: " + rows.Count.ToString(CultureInfo.InvariantCulture) + " - Input " + UsageTracker.FormatTokens(inp)
                + " - Output " + UsageTracker.FormatTokens(outp) + " - Cache write " + UsageTracker.FormatTokens(cw)
                + " - Cache read " + UsageTracker.FormatTokens(cr) + "\r\nCache hit rate " + hit
                + " (showing the latest " + Math.Min(MaxRows, rows.Count).ToString(CultureInfo.InvariantCulture) + ", newest first)";

            for (int i = rows.Count - 1, shown = 0; i >= 0 && shown < MaxRows; i--, shown++)
            {
                string[] f = rows[i];
                ListViewItem item = new ListViewItem(new string[]
                {
                    f[0], f[3], Fmt(f[4]), Fmt(f[5]), UsageTracker.FormatTokens(ParseLong(f[6]) + ParseLong(f[7])),
                    Fmt(f[8]), f[9] == "1" ? "Yes" : ""
                });
                list.Items.Add(item);
            }
            list.EndUpdate();
        }

        private static string Fmt(string n)
        {
            return UsageTracker.FormatTokens(ParseLong(n));
        }

        private static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Parse one CSV line (fields may be double-quoted; a "" inside quotes means one ")
        private static string[] SplitCsv(string line)
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
    }

    // The widget itself. It composes its own picture with a "per-pixel-alpha layered window":
    // the background (rounded panel + border) and the foreground (text + progress bars) each have their own opacity and do not affect each other.
    internal sealed class Widget : Form
    {
        private const int BaseW = 264;
        private const int PollSeconds = 60;

        // Colors: decided by the current skin (a paper set and an ink set); the drawing code uses only the variables below
        private static Color Bg, BorderColor, TextMain, TextDim, Track, Accent, Green, Amber, Red;
        private static SkinPalette curPal;

        // Skins: Combo (default), Mix 1-5, A-E of scheme 2, F-J of scheme 3
        private static readonly Skin[] Skins =
        {
            new SkinCurrent(), new SkinMix1(), new SkinMix2(), new SkinMix3(), new SkinMix4(), new SkinMix5(),
            new SkinA(), new SkinB(), new SkinC(), new SkinD(), new SkinE(),
            new SkinF(), new SkinG(), new SkinH(), new SkinI(), new SkinJ()
        };

        private static readonly string[] RomanNames = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI" };

        static Widget()
        {
            ApplyPalette(Skins[0], false);
        }

        private static void ApplyPalette(Skin sk, bool dark)
        {
            SkinPalette p = sk.Palette(dark);
            curPal = p;
            Bg = p.Bg;
            BorderColor = p.Border;
            TextMain = p.Main;
            TextDim = p.Dim;
            Track = p.Track;
            Accent = p.Accent;
            Green = p.Ok;
            Amber = p.Warn;
            Red = p.Bad;
        }

        private Skin skin = Skins[0];                   // The current skin
        private int skinIndex;                          // Index of the current skin (saved as skin= in settings.txt)
        private ToolStripMenuItem[] skinItems = new ToolStripMenuItem[0];
        private const int HotkeyNextSkin = 2;           // Hotkey id for switching to the next skin
        private const uint VkN = 0x4E;                  // The N key
        private bool hotkeyNextOn;

        // Height of the compact card inside the window = the skin's own compact-card height + the toggle bar below it (the usage panel is drawn from here downwards)
        private int CompactH()
        {
            return skin.CompactH + ToggleBarH;
        }

        private Fonts FontsNow()
        {
            return new Fonts { Title = fTitle, Label = fLabel, Small = fSmall, Display = fInk, Big = fBig };
        }

        // Switch skin: remember the choice; the window height goes back to the new skin's default
        private void SetSkin(int index)
        {
            skinIndex = index;
            skin = Skins[index];
            winHu = 0;
            rowsExpanded = false;
            rowScroll = 0;
            autoWinHu = 0;
            ApplyPalette(skin, inverted);
            ApplyScale();
            KeepOnScreen();
            UpdateSkinMenu();
            SaveSettings();
        }

        private void UpdateSkinMenu()
        {
            for (int i = 0; i < skinItems.Length; i++)
            {
                skinItems[i].Checked = i == skinIndex;
            }
        }

        private static readonly string[] Weekday = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };

        private FetchResult data;       // The most recent successful data
        private FetchResult problem;    // The most recent failure (cleared after a success)
        private bool fetching;
        private DateTime nextFetchUtc = DateTime.MinValue;
        private string lastSignature = "";

        private float scale = 1f;
        private Font fTitle, fLabel, fSmall, fInk, fBig;   // fInk: the ink-drop font (titles); fBig: the big numbers
        private RectangleF refreshRect, closeRect;

        // Two independent opacities, values are "opacity" percentages: higher = more solid, lower = more transparent
        private int bgPercent = 85;     // Background: the panel and border
        private int fgPercent = 100;    // Foreground: text and progress bars
        private const int FgMin = 20;   // Text keeps at least 20% so you cannot dial it out of existence

        // Pixel buffer of the layered window (DIB section)
        private IntPtr memDc, dib, oldObj;
        private Bitmap surface;
        private int surfaceW, surfaceH;

        // Usage panel (detailed mode): on by default, so that anyone who starts Stilla sees all the core features at once. The "simple / detailed" toggle bar or the right-click menu turns it off; while it is off no session file is read
        private bool showUsage = true;
        private const int ToggleBarH = 22;      // Height of the "simple / detailed" toggle bar between the compact card and the usage panel (base units, before scaling)
        private RectangleF toggleRect;          // Position of the toggle button (a click switches between simple and detailed mode)
        private ToolStripMenuItem usageMenuItem;   // The "Show usage statistics" item of the right-click menu; its check mark has to follow when the toggle bar changes the state
        private bool showDeepSeek;              // The "DeepSeek off-peak price estimate" row at the bottom of the panel: hidden by default (a very personal feature, enable it with ds=1 or from the right-click menu)
        private UsageTracker usage;
        private UsageTracker.Snapshot usageSnap = new UsageTracker.Snapshot();
        private const int UsageW = 420;         // Window width when usage statistics are open (the session table has to fit the cache-left time and four number columns)
        private const int UsagePanelH = 288;    // Panel height (base size, before scaling): header and liquid cups 146 + 6 rows x 16 + totals and cost 36
        private const int DiagramH = 130;       // Height of the ink-drop double circle (base size, before scaling)
        private bool rowsExpanded;              // "N more" was clicked: expand all sessions (the window grows as needed)
        private int rowScroll;                  // When the expanded window does not fit, which row the mouse wheel has scrolled to
        private int savedWinHu, autoWinHu;      // Window height saved before expanding; the height set automatically on expanding
        private RectangleF moreRect;            // Position of the "N more / collapse" row (click to expand or collapse)
        // Time tabs; the index is the tab id: 0 = 5 min, 1 = 15 min, 2 = 30 min, 3 = 1 h, 4 = 3 h, 5 = this 5 h, 6 = previous 5 h,
        // 7 = today, 8 = yesterday, 9 = this week, 10 = custom
        private const int TabCount = 11;
        private const int TabToday = 7;
        private const int TabCustom = 10;
        private static readonly string[] TabLabels = { "5 min", "15 min", "30 min", "1 h", "3 h", "This 5h", "Prev 5h", "Today", "Yesterday", "This week", "Custom" };
        private const int TabDefaultMask = 1258;    // Shown by default: 15 min, 1 h, this 5 h, previous 5 h, today, custom (bit i = tab i)
        private int tabMask = TabDefaultMask;       // Which tabs the panel shows; ticked in "Display settings"
        private int usagePeriod = 1;                // The current tab (tab id), 15 min by default
        private readonly RectangleF[] usageTabs = new RectangleF[TabCount];   // Position of each tab (tabs that are not shown are empty rectangles)
        private static readonly DateTime Epoch = new DateTime(2020, 1, 1);
        private int customFromMin = int.MinValue, customToMin = int.MinValue;   // Custom range (minutes since Epoch)
        private readonly UsageTracker.PeriodSummary[] logBy = new UsageTracker.PeriodSummary[TabCount];   // Results computed from the log; the index is the tab id (7 today, 8 yesterday, 9 this week, 10 custom), computed in the background and shown back on the UI thread
        private bool logBusy;
        private DateTime logRefreshUtc = DateTime.MinValue;
        private int usageBusy;                  // Whether the background statistics are running (0 or 1)
        private UsageLogger logger;             // Usage log: the detail and quota snapshots are written into the "logs" folder
        private NotifyIcon tray;                // Tray icon: closing the window shrinks it to the tray; only "Exit" in the tray menu really ends the program
        private bool exitRequested;
        private int usageMode;                  // Panel view: 0 = sessions, 1 = models, 2 = compare, 3 = tiers, 4 = subscription
        private readonly RectangleF[] segRects = new RectangleF[5];   // Positions of the five view buttons in the header (session / model / compare / tier / subscription)
        private QuotaTable quota;                                  // Quota conversion table (logs\quota_conversion.csv); null when there is no file
        private DateTime quotaReadUtc = DateTime.MinValue;
        private DateTime quotaFitUtc = DateTime.MinValue;          // The last time the quota conversion was recomputed in the background
        private LoginInfo login;                                   // The most recently read login info (org, plan, subscription creation time)
        private DateTime loginCheckUtc = DateTime.MinValue;
        private bool bootBusy, bootDone;                           // First run: backfill past accounts from the session records
        private RectangleF accountRect = RectangleF.Empty;         // The account chip on the compact card (click to switch account)
        private readonly List<KeyValuePair<RectangleF, string>> accountHits = new List<KeyValuePair<RectangleF, string>>();   // Position of each account row in the subscription view
        private readonly Dictionary<string, UsageTracker.PeriodSummary> subBy = new Dictionary<string, UsageTracker.PeriodSummary>();   // Account -> usage in the current subscription cycle
        private DateTime subRefreshUtc = DateTime.MinValue;
        private bool subBusy;
        private DateTime renewCheckUtc = DateTime.MinValue;
        private string renewNotified = "";
        private readonly List<WeekPoint> weekHist = new List<WeekPoint>();   // Recent samples of the weekly quota (used to predict when it runs out)
        private bool weekHistLoaded;

        private sealed class WeekPoint
        {
            public DateTime T;
            public double Pct;
            public long Reset;
        }
        private DateTime vsyncUtc = DateTime.MinValue;   // The last time the VS Code titles were exported
        private readonly double[] colFr = { 0.31, 0.125, 0.115, 0.12, 0.165, 0.165 };   // Column width ratios: session name, cache left, input, output, cache write, cache read (sum is 1, can be dragged)
        private readonly float[] colWidth = new float[6];   // Width of each column in this draw (physical pixels)
        private readonly float[] colEdgeX = new float[5];   // Position of each column divider
        private float colInner = 1f;
        private float colTop, colBottom;
        private int colDrag = -1;                           // Which divider is being dragged (-1 = none)
        private int colDragX0;
        private readonly double[] colDragStart = new double[6];
        private readonly HashSet<string> expandedGroups = new HashSet<string>();     // Sessions expanded in the model view (session IDs)
        private readonly List<KeyValuePair<RectangleF, string>> chevHits = new List<KeyValuePair<RectangleF, string>>();   // Positions of the arrow in the model view (click to expand / collapse)
        private readonly List<KeyValuePair<RectangleF, string>> sessionHits = new List<KeyValuePair<RectangleF, string>>();   // Position of each session row in the usage panel (click to drill down)
        private int winWu, winHu;               // Window size after the user dragged it (base units); 0 = use the default size
        private bool resizing;                  // Dragging an edge to resize
        private int resizeEdge;
        private Point resizeCursorStart;
        private Rectangle resizeBoundsStart;

        private int savedX = int.MinValue, savedY = int.MinValue;
        private ToolStripMenuItem topItem;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int CX, CY; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyInvert = 1;             // Hotkey id for the light/dark switch
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint VkB = 0x42;                  // The B key
        private bool inverted;                          // Currently in the ink (inverted) set
        private bool hotkeyOn;                          // Whether the hotkey was registered (it fails when another program holds it; the menu still works)

        public Widget()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Bg;
            Text = "Stilla";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
            }

            Directory.CreateDirectory(AppPaths.DataDir);
            logger = new UsageLogger(AppPaths.DataDir);
            TagStore.Configure(logger.TagPath);
            AccountStore.Init(AppPaths.DataDir);
            UpdateState.Load();
            LoadSettings();
            usage = new UsageTracker();

            timer.Interval = 3000;
            timer.Tick += OnTick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000;  // WS_EX_LAYERED: per-pixel alpha
                cp.ExStyle |= 0x80;     // WS_EX_TOOLWINDOW: does not show up in Alt+Tab
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        // The picture is provided entirely by UpdateLayeredWindow; the system's paint messages are all ignored
        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyScale();
            BuildMenu();
            BuildTray();
            PlaceInitial();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RegisterInvertHotkey();
            RefreshUsage();
            logger.Start();
            Redraw();
            timer.Start();
            StartFetch();
            StartUpdateCheck(false);
        }

        // Hotkey Ctrl+Alt+B: switches without clicking the window, from anywhere (even when the window is in the tray)
        private void RegisterInvertHotkey()
        {
            if (!hotkeyOn)
            {
                hotkeyOn = RegisterHotKey(Handle, HotkeyInvert, ModControl | ModAlt, VkB);
            }
            if (!hotkeyNextOn)
            {
                hotkeyNextOn = RegisterHotKey(Handle, HotkeyNextSkin, ModControl | ModAlt, VkN);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyInvert)
            {
                ToggleInvert();
                return;
            }
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyNextSkin)
            {
                SetSkin((skinIndex + 1) % Skins.Length);
                return;
            }
            base.WndProc(ref m);
        }

        // Light/dark switch: paper background with ink text <-> ink background with paper text; redraw immediately and remember the choice
        private void ToggleInvert()
        {
            inverted = !inverted;
            ApplyPalette(skin, inverted);
            Redraw();
            SaveSettings();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                // Clicking "x" only shrinks it to the tray; only "Exit" in the tray or right-click menu really ends the program
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (hotkeyOn)
            {
                UnregisterHotKey(Handle, HotkeyInvert);
                hotkeyOn = false;
            }
            if (hotkeyNextOn)
            {
                UnregisterHotKey(Handle, HotkeyNextSkin);
                hotkeyNextOn = false;
            }
            timer.Stop();
            timer.Dispose();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            FreeSurface();
            DisposeFonts();
            base.OnFormClosed(e);
        }

        // ---------------------------------------------------------------- Fetching data

        private void OnTick(object sender, EventArgs e)
        {
            // When the window is dragged onto a screen with a different DPI, lay out again at the new scale
            if (Math.Abs(GetScale() - scale) > 0.01f)
            {
                ApplyScale();
            }

            if (!fetching)
            {
                // It is time for the scheduled fetch; or the window has just reset, so fetch new data right away (during errors do not jump the queue, follow the back-off time)
                if (DateTime.UtcNow >= nextFetchUtc || (problem == null && ResetJustPassed()))
                {
                    StartFetch();
                }
            }

            // The countdown is exact to the minute: if the text on screen did not change there is no redraw, so idle CPU use is close to nothing
            if (DateTime.UtcNow - vsyncUtc > TimeSpan.FromMinutes(10))
            {
                vsyncUtc = DateTime.UtcNow;
                StartVsCodeSync();
            }
            if (DateTime.UtcNow - quotaFitUtc > TimeSpan.FromHours(1))
            {
                quotaFitUtc = DateTime.UtcNow;
                StartQuotaFit();
            }
            try
            {
                CheckLogin();
                CheckRenew();
            }
            catch (Exception)
            {
                // No problem in account detection may affect the quota display
            }
            RefreshUsage();
            if (showUsage)
            {
                StartLogRefresh();
                if (usageMode == 4)
                {
                    StartSubRefresh();
                }
            }
            CheckRedraw();
        }

        private string Signature(DateTime now)
        {
            string l5, r5, l7, r7;
            DescribeReset(data == null ? null : data.FiveHour, now, false, out l5, out r5);
            DescribeReset(data == null ? null : data.SevenDay, now, true, out l7, out r7);
            string sig = l5 + "|" + r5 + "|" + l7 + "|" + r7;
            return showUsage ? sig + "|" + usageSnap.Signature : sig;
        }

        private bool ResetJustPassed()
        {
            if (data == null)
            {
                return false;
            }
            return PassedSinceFetch(data.FiveHour) || PassedSinceFetch(data.SevenDay);
        }

        private bool PassedSinceFetch(Quota q)
        {
            return q != null && q.ResetsAt.HasValue
                && DateTime.Now > q.ResetsAt.Value.AddSeconds(2)
                && data.FetchedAt < q.ResetsAt.Value;
        }

        private void StartFetch()
        {
            if (fetching)
            {
                return;
            }
            fetching = true;
            nextFetchUtc = DateTime.UtcNow.AddSeconds(PollSeconds);
            ThreadPool.QueueUserWorkItem(delegate
            {
                FetchResult r;
                try
                {
                    r = QuotaSource.Query();
                }
                catch (Exception)
                {
                    r = new FetchResult();
                    r.Problem = Problem.Network;
                    r.RetrySeconds = 60;
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate { Apply(r); }));
                }
                catch (Exception)
                {
                    // The window has already been closed
                }
            });
        }

        private void Apply(FetchResult r)
        {
            fetching = false;
            if (r.Problem == Problem.None)
            {
                data = r;
                problem = null;
                logger.LogQuota(r);
                NoteWeekly(r);
            }
            else
            {
                problem = r;
            }
            nextFetchUtc = DateTime.UtcNow.AddSeconds(r.RetrySeconds);
            UpdateTrayText();
            Redraw();
            if (QuotaSource.WantsOnline)
            {
                AskOnlineConsent();
            }
        }

        private bool askingConsent;

        // Default "Auto" source: when the status line has no data, the user is asked once before falling back to the online query (which reads the login token and calls an undocumented endpoint).
        // The answer is stored as onl= in settings.txt: yes = fall back automatically from now on; no = status line only, never ask again (it can be changed at any time under right-click → Quota data source)
        private void AskOnlineConsent()
        {
            if (askingConsent || QuotaSource.Mode != QuotaSource.Auto || QuotaSource.OnlineConsent != 0)
            {
                return;
            }
            askingConsent = true;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    try
                    {
                        DialogResult yes = MessageBox.Show(this,
                            "Stilla has not received quota data from the Claude Code status line yet (this is normal right after installing, before the status line is set up, or while Claude Code is not running).\r\n\r\n"
                            + "The default \"Auto\" mode can then fall back to the \"Online query\": it reads the Claude login token in ~/.claude/.credentials.json on this machine and uses it to call an undocumented endpoint (the same one Claude Code's /usage uses).\r\n\r\n"
                            + "- The token is only used on this machine; it is not sent to anyone else and is not saved.\r\n"
                            + "· This endpoint can change at any time; Anthropic's terms restrict using subscription login credentials in tools other than Claude Code, so whether this is appropriate is your decision.\r\n"
                            + "· Choosing \"No\" keeps Stilla on the status line only: no token is read and no network is used (you can change this at any time under right-click → Quota data source).\r\n\r\n"
                            + "Allow the online query when the status line has no data?",
                            "Stilla · Quota data source", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                        QuotaSource.OnlineConsent = yes == DialogResult.Yes ? 1 : 2;
                        QuotaSource.WantsOnline = false;
                        SaveSettings();
                        if (yes == DialogResult.Yes)
                        {
                            StartFetch();
                        }
                    }
                    finally
                    {
                        askingConsent = false;
                    }
                }));
            }
            catch (Exception)
            {
                askingConsent = false;
            }
        }

        // ---------------------------------------------------------------- Layered window composition

        private float GetScale()
        {
            try
            {
                if (IsHandleCreated)
                {
                    uint dpi = GetDpiForWindow(Handle);
                    if (dpi > 0)
                    {
                        return dpi / 96f;
                    }
                }
            }
            catch (Exception)
            {
            }
            using (Graphics g = CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        private void ApplyScale()
        {
            scale = GetScale();
            CreateFonts();
            ClientSize = new Size((int)Math.Round(CurW() * scale), (int)Math.Round(CurH() * scale));
            Redraw();
        }

        // Fonts are recreated for the current scale (Redraw uses fTitle to tell whether the fonts are ready)
        private void CreateFonts()
        {
            DisposeFonts();
            fTitle = new Font("Microsoft YaHei UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            fLabel = new Font(skin.UiFamily, 13f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            fSmall = new Font(skin.UiFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            fInk = new Font(skin.DisplayFamily, 13f * scale, skin.DisplayStyle, GraphicsUnit.Pixel);
            fBig = new Font(skin.DisplayFamily, 22f * scale, skin.DisplayStyle, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            if (fTitle != null) { fTitle.Dispose(); fTitle = null; }
            if (fLabel != null) { fLabel.Dispose(); fLabel = null; }
            if (fSmall != null) { fSmall.Dispose(); fSmall = null; }
            if (fInk != null) { fInk.Dispose(); fInk = null; }
            if (fBig != null) { fBig.Dispose(); fBig = null; }
        }

        private void EnsureSurface(int w, int h)
        {
            if (surface != null && surfaceW == w && surfaceH == h)
            {
                return;
            }
            FreeSurface();

            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h;   // Negative = top-down
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0;

            IntPtr screen = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screen);
            IntPtr bits;
            dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            ReleaseDC(IntPtr.Zero, screen);
            if (dib == IntPtr.Zero)
            {
                FreeSurface();
                return;
            }
            oldObj = SelectObject(memDc, dib);
            surface = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            surfaceW = w;
            surfaceH = h;
        }

        private void FreeSurface()
        {
            if (surface != null)
            {
                surface.Dispose();
                surface = null;
            }
            if (memDc != IntPtr.Zero)
            {
                if (oldObj != IntPtr.Zero)
                {
                    SelectObject(memDc, oldObj);
                }
                DeleteDC(memDc);
                memDc = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
                dib = IntPtr.Zero;
            }
            oldObj = IntPtr.Zero;
            surfaceW = 0;
            surfaceH = 0;
        }

        // Compose a frame: lay the background first (with the background opacity), then overlay the whole foreground with the text opacity
        private void Redraw()
        {
            if (!IsHandleCreated || fTitle == null)
            {
                return;
            }
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            EnsureSurface(w, h);
            if (surface == null)
            {
                return;
            }

            using (Bitmap layer = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics lg = Graphics.FromImage(layer))
                {
                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                    lg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    lg.Clear(Color.Transparent);
                    PaintContent(lg, w, h);
                }

                using (Graphics g = Graphics.FromImage(surface))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    PaintBackground(g, w, h);

                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = fgPercent / 100f;
                    ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                    g.DrawImage(layer, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                }
            }
            Present(w, h);
        }

        private void Present(int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                SIZE size = new SIZE();
                size.CX = w;
                size.CY = h;
                POINT src = new POINT();
                BLENDFUNCTION bf = new BLENDFUNCTION();
                bf.BlendOp = 0;                 // AC_SRC_OVER
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = 1;             // AC_SRC_ALPHA: use each pixel's own alpha
                // pptDst is null: do not change the window position, just update the picture
                UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memDc, ref src, 0, ref bf, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private void PaintBackground(Graphics g, int w, int h)
        {
            // Even at 0% keep at least 1/255 alpha for the background: fully transparent pixels let the mouse pass straight through, and the window could not be dragged or clicked
            int a = Math.Max(1, (int)Math.Round(255.0 * bgPercent / 100.0));
            using (GraphicsPath path = RoundRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), 10f * scale))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Bg)))
            using (Pen p = new Pen(Color.FromArgb(a, BorderColor)))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }

        // Foreground: text + progress bars, drawn in opaque colors on a transparent layer and finally overlaid with the text opacity
        private void PaintContent(Graphics g, int w, int h)
        {
            float s = scale;
            float pad = 14f * s;
            DateTime now = DateTime.Now;
            Quota five = data == null ? null : data.FiveHour;
            Quota week = data == null ? null : data.SevenDay;

            closeRect = new RectangleF(w - pad - 18f * s, 6f * s, 24f * s, 22f * s);
            refreshRect = new RectangleF(closeRect.X - 52f * s, 6f * s, 50f * s, 22f * s);

            CompactData d = new CompactData();
            d.Has5 = five != null;
            d.P5 = five == null ? 0 : five.Percent;
            d.HasW = week != null;
            d.PW = week == null ? 0 : week.Percent;
            ResetParts(five, now, false, out d.Clock5, out d.Left5);
            ResetParts(week, now, true, out d.ClockW, out d.LeftW);
            d.Updated = data != null ? data.FetchedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";

            // Bottom status
            string foot;
            Color footColor = TextDim;
            if (problem != null)
            {
                foot = (data != null ? "Stale data - " : "") + Describe(problem);
                footColor = Amber;
            }
            else if (data == null)
            {
                foot = "Reading...";
            }
            else if (data.Via == "bridge")
            {
                // Status-line data is only updated while Claude Code is running: warn when it has been too long
                int age = (int)(DateTime.Now - data.FetchedAt).TotalMinutes;
                if (age >= 10)
                {
                    foot = "No update for " + age + " min";
                    footColor = Amber;
                }
                else
                {
                    foot = "Status line - " + d.Updated;
                }
            }
            else
            {
                foot = "Updated " + d.Updated + " - every 60 s";
            }
            d.Foot = foot;
            d.FootColor = footColor;
            d.Alert = problem != null || data == null || footColor.ToArgb() == Amber.ToArgb();
            if (data != null && data.Via == "bridge")
            {
                d.Source = "via status line";
                d.SourceEn = "STATUSLINE";
            }

            SkinDraw.Shadow = (int)Math.Round(255.0 * 0.8 * (1.0 - bgPercent / 100.0));
            skin.DrawCompact(g, d, FontsNow(), curPal, closeRect, refreshRect, s, w);
            DrawAccountChip(g, s, pad);
            DrawUpdateBadge(g, s, w);
            DrawModeToggle(g, w, s);
            if (showUsage)
            {
                DrawUsagePanel(g, w, h);
            }
        }

        // "Simple / detailed" toggle button: drawn in the toggle bar right under the compact card and above the usage panel, left-aligned. The window width differs between the two modes, so left alignment is what keeps the position unchanged (the mouse is still on the button after a click); works for every skin
        private void DrawModeToggle(Graphics g, int w, float s)
        {
            string text = showUsage ? "▴  Quota only" : "▾  Show details";
            float tw = MeasureW(g, text) + 24f * s;
            toggleRect = new RectangleF(14f * s, skin.CompactH * s + 3f * s, tw, 16f * s);
            SkinDraw.RrLine(g, TextDim, toggleRect, 8f * s, 1f * s);
            DrawTextRect(g, text, fSmall, TextDim, toggleRect, StringAlignment.Center);
        }

        // Reset time and time left are given to the UI separately: "19:47" and "1:11"; "-" when there is no data
        private static void ResetParts(Quota q, DateTime now, bool weekly, out string clock, out string left)
        {
            clock = "—";
            left = "";
            if (q == null)
            {
                return;
            }
            if (!q.ResetsAt.HasValue)
            {
                clock = weekly ? "Reset time unknown" : "Not started";
                return;
            }
            DateTime t = q.ResetsAt.Value;
            if (t <= now)
            {
                clock = "Refreshing...";
                return;
            }
            clock = weekly || t.Date != now.Date
                ? t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : t.ToString("HH:mm", CultureInfo.InvariantCulture);
            left = weekly ? CountdownLong(t - now) : CountdownHm(t - now);
        }

        // DeepSeek off-peak price (CNY per million tokens), model deepseek-flash (DeepSeek-V4.1-Flash-1017). Source: the pricing page of api-docs.deepseek.com (the page gives no effective date and says prices may change)
        private const double DsHitPerM = 0.02;      // Input (cache hit)
        private const double DsMissPerM = 1.0;      // Input (cache miss); cache writes are priced at this rate
        private const double DsOutPerM = 4.0;       // Output

        private static double EstimateCostCny(long input, long output, long cacheWrite, long cacheRead)
        {
            return (input + cacheWrite) * DsMissPerM / 1000000.0
                + cacheRead * DsHitPerM / 1000000.0
                + output * DsOutPerM / 1000000.0;
        }

        // Share: this item's fraction of the whole (0-1), 0 when there is no total
        private static double ShareOf(long part, long all)
        {
            return all > 0 ? Math.Min(1.0, (double)part / all) : 0;
        }

        // Usage panel (ink-drop edition): at the top "USAGE" and three view buttons session / model / compare; then the time tabs (liquid cups, the ink height in a cup = the amount of usage in that period);
        // then the big usage number and time range; below is the table: one row per session (cache left, input, output, cache write, cache read in separate columns), and finally the total and the estimated DeepSeek off-peak cost.
        private void DrawUsagePanel(Graphics g, int w, int h)
        {
            float s = scale;
            float pad = 14f * s;
            float inner = w - 2f * pad;
            float top = CompactH() * s;
            List<int> shownTabs = VisibleTabs();
            if ((tabMask & (1 << usagePeriod)) == 0)
            {
                usagePeriod = shownTabs[0];     // The current tab is hidden in the settings: switch to the first visible tab
            }

            skin.DrawFrame(g, new RectangleF(0f, top, w, UsagePanelH * s), curPal, s);

            // Header: ink-drop icon + USAGE, with the view buttons on the right
            skin.DrawTitle(g, pad, top + 6f * s, "USAGE", FontsNow(), curPal, s);
            DrawModeSwitch(g, w, pad, top + 7f * s, s);

            // Time tabs (liquid cups), then the big number and time range
            DrawTabStrip(g, shownTabs, pad, top + 34f * s, inner, s);
            UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
            DrawTextRect(g, UsageTracker.FormatTokens(cur.Total), fBig, TextMain, new RectangleF(pad, top + 98f * s, inner * 0.4f, 26f * s), StringAlignment.Near);
            DrawTextRect(g, "TOKENS · " + cur.Range, fSmall, TextDim, new RectangleF(pad + inner * 0.4f, top + 104f * s, inner * 0.6f, 16f * s), StringAlignment.Far);

            // Columns: session name (left) | cache left (middle) | input output cache W cache R (right)
            sessionHits.Clear();
            // Column widths are shared out by ratio (the dividers can be dragged on the card): session name | cache left | input | output | cache write | cache read
            // Every column first gets its minimum width (so numbers are never squeezed out when the window is narrowed); the remaining width is shared out by ratio
            double frSum = 0;
            for (int i = 0; i < 6; i++)
            {
                frSum += colFr[i];
            }
            float[] mins = { 120f * s, 56f * s, 44f * s, 44f * s, 44f * s, 44f * s };
            float minSum = 0;
            for (int i = 0; i < 6; i++)
            {
                minSum += mins[i];
            }
            float shrink = minSum > inner ? inner / minSum : 1f;
            float remain = inner - minSum * shrink;
            for (int i = 0; i < 6; i++)
            {
                colWidth[i] = mins[i] * shrink + remain * (float)(colFr[i] / frSum);
            }
            colInner = inner;
            float edgeX = pad;
            for (int i = 0; i < 5; i++)
            {
                edgeX += colWidth[i];
                colEdgeX[i] = edgeX;
            }
            colTop = top + 128f * s;
            colBottom = top + UsagePanelH * s;
            using (Pen divPen = new Pen(Track))
            {
                for (int i = usageMode == 0 ? 0 : 1; i < (usageMode == 4 ? 0 : 5); i++)
                {
                    g.DrawLine(divPen, colEdgeX[i], colTop, colEdgeX[i], colBottom);
                }
            }
            float nameW = colWidth[0];
            float cacheW = colWidth[1];
            float colW = colWidth[2];
            float headY = top + 128f * s;
            float rowH = 16f * s;
            string[] heads = usageMode == 0 ? new string[] { "Input", "Output", "Cache W", "Cache R" }
                : usageMode == 1 ? new string[] { "token", "5h est.", "Wk est.", "Cache R" }
                : usageMode == 3 ? new string[] { "Msgs", "Output", "Think", "5h est." }
                : usageMode == 4 ? new string[] { "Period use", "Fee", "Payback", "Renews" }
                : new string[] { "token", "Share", "5h est.", "Mult." };
            DrawTextRect(g, usageMode == 0 ? "Session" : usageMode == 1 ? "Model" : usageMode == 3 ? "Tier" : usageMode == 4 ? "Account" : "Compare", fSmall, TextDim, new RectangleF(pad, headY, nameW, 14f * s), StringAlignment.Near);
            if (usageMode == 0)
            {
                DrawTextRect(g, "Expires", fSmall, TextDim, new RectangleF(pad + nameW, headY, cacheW, 14f * s), StringAlignment.Far);
            }
            DrawRowNumbers(g, pad, inner, colW, headY, 14f * s, heads, TextDim);

            // How many rows fit at the current window height (two rows are kept below for total and cost); a taller window shows more
            float panelH = h / s - CompactH();
            int capacity = (int)Math.Floor((panelH - 182f) / 16f);
            int cap = Math.Max(1, capacity - (usageMode == 4 ? 1 : 0));

            moreRect = RectangleF.Empty;
            List<UsageTracker.SessionTotal> rows = SortedSessions(cur);
            float y = top + 146f * s;
            int drawn = 0;
            if (usageMode == 1)
            {
                drawn = DrawModelRows(g, cur, pad, inner, s, colW, nameW + cacheW, y, rowH, cap);
            }
            else if (usageMode == 2)
            {
                drawn = DrawCompareRows(g, cur, pad, inner, s, colW, nameW, y, rowH, cap);
            }
            else if (usageMode == 3)
            {
                drawn = DrawTierRows(g, cur, pad, inner, s, colW, nameW + cacheW, y, rowH, cap);
            }
            else if (usageMode == 4)
            {
                drawn = DrawSubscriptionRows(g, pad, inner, s, colW, nameW + cacheW, y, rowH, cap);
                DrawSubscriptionInfo(g, pad, inner, s, y + drawn * rowH + 4f * s, rowH);
                return;
            }
            else if (rows.Count == 0)
            {
                DrawTextRect(g, "No usage in this period", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                drawn = 1;
            }
            else
            {
                // When they do not fit: the first cap-1 rows are shown normally and the last row is "N more"; click it to expand everything
                int rowN = rows.Count;
                int rowStart, rowShown;
                bool rowMore, rowCollapse;
                RowWindow(rowN, cap, out rowStart, out rowShown, out rowMore, out rowCollapse);
                for (int i = rowStart; i < rowStart + rowShown; i++)
                {
                    DrawSessionRow(g, rows[i], pad, inner, s, colW, cacheW, nameW, y + drawn * rowH, rowH);
                    sessionHits.Add(new KeyValuePair<RectangleF, string>(new RectangleF(pad, y + drawn * rowH, inner, rowH), rows[i].Session));
                    drawn++;
                }
                if (rowMore)
                {
                    long rIn = 0, rOut = 0, rCw = 0, rCr = 0;
                    for (int i = rowShown; i < rowN; i++)
                    {
                        rIn += rows[i].Input;
                        rOut += rows[i].Output;
                        rCw += rows[i].CacheWrite;
                        rCr += rows[i].CacheRead;
                    }
                    DrawMoreRow(g, "▾ " + (rowN - rowShown) + " more", Numbers(rIn, rOut, rCw, rCr), pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                    drawn++;
                }
                else if (rowCollapse)
                {
                    DrawMoreRow(g, rowN > rowShown ? "▴ Collapse · scroll to page" : "▴ Collapse", null, pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                    drawn++;
                }
            }

            // Total row: the total of this time range (matches the header)
            float yTotal = y + drawn * rowH + 3f * s;
            using (Pen p = new Pen(Track))
            {
                g.DrawLine(p, pad, yTotal - 2f * s, w - pad, yTotal - 2f * s);
            }
            DrawTextRect(g, "Total", fSmall, TextMain, new RectangleF(pad, yTotal, nameW, rowH), StringAlignment.Near);
            if (usageMode >= 1 && quota != null && quota.CalibErr >= 0)
            {
                // Self-check of the quota conversion: every block of data is fitted on the data before it and then predicted; this is the mean relative error of the latest blocks
                DrawTextRect(g, "Est. error ~" + (quota.CalibErr * 100).ToString("0", CultureInfo.InvariantCulture) + "%", fSmall, TextDim,
                    new RectangleF(pad + 34f * s, yTotal, nameW - 34f * s, rowH), StringAlignment.Near);
            }
            string[] totalNums = usageMode == 0 ? Numbers(cur.Input, cur.Output, cur.CacheWrite, cur.CacheRead)
                : usageMode == 1 ? new string[] { UsageTracker.FormatTokens(cur.Total), TotalEstimateText(cur, false), TotalEstimateText(cur, true), UsageTracker.FormatTokens(cur.CacheRead) }
                : usageMode == 3 ? TierTotalNums(cur)
                : new string[] { UsageTracker.FormatTokens(cur.Total), cur.Total > 0 ? "100%" : "", TotalEstimateText(cur, false), "" };
            DrawRowNumbers(g, pad, inner, colW, yTotal, rowH, totalNums, TextMain);

            // Last row: the cost estimated at DeepSeek off-peak prices ("what these tokens would cost on DeepSeek"), hidden by default, can be enabled from the right-click menu
            if (showDeepSeek)
            {
                double cost = EstimateCostCny(cur.Input, cur.Output, cur.CacheWrite, cur.CacheRead);
                float yCost = yTotal + rowH + 1f * s;
                DrawTextRect(g, "DeepSeek off-peak price estimate (Flash)", fSmall, TextDim, new RectangleF(pad, yCost, inner * 0.7f, rowH), StringAlignment.Near);
                DrawTextRect(g, "¥" + cost.ToString("0.00", CultureInfo.InvariantCulture), fLabel, TextMain, new RectangleF(pad + inner * 0.6f, yCost - 2f * s, inner * 0.4f, rowH + 4f * s), StringAlignment.Far);
            }
        }

        // How active a session is: 0 = being written (activity in the last 60 seconds), 1 = cache not expired yet, 2 = expired or no cache.
        // The live state follows Live in the statistics (so today / yesterday / this week from the log can use it too)
        private int SessionRank(string session, bool active, DateTime? expiry, DateTime last, DateTime nowUtc, out DateTime lastUtc)
        {
            lastUtc = last;
            UsageTracker.LiveInfo li;
            if (usageSnap.Live.TryGetValue(session, out li))
            {
                active = active || li.Active;
                if (!expiry.HasValue)
                {
                    expiry = li.ExpiryUtc;
                }
                if (li.LastUtc > lastUtc)
                {
                    lastUtc = li.LastUtc;
                }
            }
            if (active)
            {
                return 0;
            }
            return expiry.HasValue && expiry.Value > nowUtc ? 1 : 2;
        }

        private sealed class RowKey
        {
            public UsageTracker.SessionTotal Session;
            public ModelGroup Group;
            public int Rank;
            public DateTime Last;
            public long Total;
            public string Id;
        }

        private static int CompareKeys(RowKey a, RowKey b)
        {
            if (a.Rank != b.Rank)
            {
                return a.Rank.CompareTo(b.Rank);
            }
            int c = b.Last.CompareTo(a.Last);
            if (c != 0)
            {
                return c;
            }
            c = b.Total.CompareTo(a.Total);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        }

        // Display order of sessions: the ones in use on top, then those whose cache has not expired, expired ones further down; within the same group the most recently used first, then by token count
        private List<UsageTracker.SessionTotal> SortedSessions(UsageTracker.PeriodSummary cur)
        {
            DateTime now = DateTime.UtcNow;
            List<RowKey> keys = new List<RowKey>(cur.Rows.Count);
            foreach (UsageTracker.SessionTotal t in cur.Rows)
            {
                RowKey k = new RowKey();
                k.Session = t;
                k.Rank = SessionRank(t.Session, t.Active, t.CacheExpiryUtc, t.LastUtc, now, out k.Last);
                k.Total = t.Total;
                k.Id = t.Session;
                keys.Add(k);
            }
            keys.Sort(CompareKeys);
            List<UsageTracker.SessionTotal> res = new List<UsageTracker.SessionTotal>(keys.Count);
            foreach (RowKey k in keys)
            {
                res.Add(k.Session);
            }
            return res;
        }

        // ---------------------------------------------------------------- Model view: grouped by session
        // One row per session; each "model x tier" the session used in this time range is its sub-row. With only one combination it is a single row;
        // ones switched midway (changed model, or raised the thinking effort from xhigh to max) are folded into one row; click the arrow at the start of the row to expand, sub-rows are ordered by first use and carry their time span.
        private sealed class ModelGroup
        {
            public string Session;
            public readonly List<UsageTracker.ModelTotal> Parts = new List<UsageTracker.ModelTotal>();
            public long Total, CacheRead;
        }

        private sealed class ModelLine
        {
            public ModelGroup Group;
            public UsageTracker.ModelTotal Part;     // null = a session row (group row), otherwise an expanded sub-row
            public bool Expanded;
        }

        // Flatten into the rows to show: group rows + (expanded) sub-rows. Session order is the same as in the session view: those in use on top, those with an unexpired cache next, expired ones below
        private List<ModelLine> BuildModelLines(UsageTracker.PeriodSummary cur)
        {
            Dictionary<string, ModelGroup> map = new Dictionary<string, ModelGroup>();
            List<ModelGroup> groups = new List<ModelGroup>();
            foreach (UsageTracker.ModelTotal m in cur.ModelRows)
            {
                ModelGroup grp;
                if (!map.TryGetValue(m.Session, out grp))
                {
                    grp = new ModelGroup();
                    grp.Session = m.Session;
                    map[m.Session] = grp;
                    groups.Add(grp);
                }
                grp.Parts.Add(m);
                grp.Total += m.Total;
                grp.CacheRead += m.CacheRead;
            }
            DateTime now = DateTime.UtcNow;
            List<RowKey> keys = new List<RowKey>(groups.Count);
            foreach (ModelGroup grp in groups)
            {
                grp.Parts.Sort(delegate(UsageTracker.ModelTotal a, UsageTracker.ModelTotal b)
                {
                    int c = a.FirstUtc.CompareTo(b.FirstUtc);
                    return c != 0 ? c : b.Total.CompareTo(a.Total);
                });
                RowKey k = new RowKey();
                k.Group = grp;
                k.Rank = SessionRank(grp.Session, false, null, DateTime.MinValue, now, out k.Last);
                k.Total = grp.Total;
                k.Id = grp.Session;
                keys.Add(k);
            }
            keys.Sort(CompareKeys);
            List<ModelLine> lines = new List<ModelLine>();
            foreach (RowKey k in keys)
            {
                ModelLine head = new ModelLine();
                head.Group = k.Group;
                head.Expanded = k.Group.Parts.Count > 1 && expandedGroups.Contains(k.Group.Session);
                lines.Add(head);
                if (head.Expanded)
                {
                    foreach (UsageTracker.ModelTotal p in k.Group.Parts)
                    {
                        ModelLine child = new ModelLine();
                        child.Group = k.Group;
                        child.Part = p;
                        lines.Add(child);
                    }
                }
            }
            return lines;
        }

        // How to show too many rows: not expanded -> first cap-1 rows + "k more"; expanded -> as many rows as fit (one row is kept for "collapse"), the rest are scrolled with the wheel
        private void RowWindow(int n, int cap, out int start, out int shown, out bool more, out bool collapse)
        {
            start = 0;
            more = false;
            collapse = false;
            if (!rowsExpanded)
            {
                if (n <= cap)
                {
                    shown = n;
                }
                else
                {
                    shown = Math.Max(1, cap - 1);
                    more = true;
                }
                return;
            }
            collapse = true;
            shown = Math.Min(n, Math.Max(1, cap - 1));
            int maxScroll = Math.Max(0, n - shown);
            if (rowScroll > maxScroll)
            {
                rowScroll = maxScroll;
            }
            if (rowScroll < 0)
            {
                rowScroll = 0;
            }
            start = rowScroll;
        }

        // The "N more" / "collapse" row: click it to expand or collapse; when nums is not empty the totals of the merged rows are shown on the right
        private void DrawMoreRow(Graphics g, string text, string[] nums, float pad, float inner, float s, float colW, float nameW, float y, float rowH)
        {
            moreRect = new RectangleF(pad, y, inner, rowH);
            DrawTextRect(g, text, fSmall, Accent, new RectangleF(pad + 4f * s, y, nameW - 4f * s, rowH), StringAlignment.Near);
            if (nums != null)
            {
                DrawRowNumbers(g, pad, inner, colW, y, rowH, nums, TextDim);
            }
        }

        // How many rows this view has in total (the session count in the session view, the number of "session x model" rows in the model view)
        private int RowCountNow()
        {
            UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
            return usageMode == 1 ? BuildModelLines(cur).Count : usageMode == 4 ? AccountStore.Accounts.Count : cur.Rows.Count;
        }

        // Expand: the window grows exactly tall enough for all rows (up to the screen's usable height, the rest is scrolled with the wheel); click again to collapse and the window returns to its previous height
        private void ToggleRows()
        {
            if (rowsExpanded)
            {
                CollapseRows();
                return;
            }
            rowsExpanded = true;
            rowScroll = 0;
            savedWinHu = winHu;
            int need = CompactH() + 146 + (RowCountNow() + 1) * 16 + 40;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int target = Math.Min(need, (int)Math.Floor((wa.Height - 16) / scale));
            if (target > CurH())
            {
                winHu = target;
                autoWinHu = target;
            }
            else
            {
                autoWinHu = -1;
            }
            ApplyScale();
            KeepOnScreen();
        }

        private void CollapseRows()
        {
            if (!rowsExpanded)
            {
                return;
            }
            rowsExpanded = false;
            rowScroll = 0;
            if (autoWinHu > 0 && winHu == autoWinHu)
            {
                winHu = savedWinHu;     // The window height is still the automatically grown value: restore it; if you dragged it yourself leave it alone
            }
            autoWinHu = 0;
            ApplyScale();
            KeepOnScreen();
        }

        // Ids of the tabs currently shown (ascending)
        private List<int> VisibleTabs()
        {
            List<int> res = new List<int>();
            for (int i = 0; i < TabCount; i++)
            {
                if ((tabMask & (1 << i)) != 0)
                {
                    res.Add(i);
                }
            }
            if (res.Count == 0)
            {
                res.Add(1);     // Even if the settings were corrupted there is at least one tab
            }
            return res;
        }

        // Time tabs: drawn by the current skin; only the checked tabs are drawn, with equal width; the font shrinks automatically when there are too many to fit
        private void DrawTabStrip(Graphics g, List<int> shown, float pad, float y0, float inner, float s)
        {
            float cell = inner / shown.Count;
            float widest = 0;
            foreach (int i in shown)
            {
                widest = Math.Max(widest, g.MeasureString(TabLabels[i], fSmall).Width);
            }
            float k = 1f;
            if (widest + 4f * s > cell)
            {
                k = Math.Max(0.6f, (cell - 4f * s) / widest);
            }
            Fonts f = FontsNow();
            Array.Clear(usageTabs, 0, usageTabs.Length);
            for (int c = 0; c < shown.Count; c++)
            {
                int i = shown[c];
                UsageTracker.PeriodSummary ps = PeriodFor(i);
                RectangleF rect = new RectangleF(pad + cell * c, y0 - 3f * s, cell, 66f * s);
                usageTabs[i] = rect;
                TabData t = new TabData
                {
                    Index = i,
                    Label = TabLabels[i],
                    Value = UsageTracker.FormatTokens(ps.Total),
                    Roman = RomanNames[i],
                    Fill = InkPainter.LogFill(ps.Total),
                    Selected = i == usagePeriod,
                    LabelScale = k
                };
                skin.DrawTab(g, rect, t, f, curPal, s);
            }
        }

        // The four view buttons on the right of the header: session / model / compare / tier (a click switches directly)
        private void DrawModeSwitch(Graphics g, int w, float pad, float y, float s)
        {
            string[] names = { "Session", "Model", "Compare", "Tier", "Plan" };
            float segW = 58f * s, segH = 18f * s;
            float x0 = w - pad - segW * names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                segRects[i] = new RectangleF(x0 + segW * i, y, segW, segH);
            }
            skin.DrawModes(g, segRects, names, usageMode, FontsNow(), curPal, s);
        }

        // One row per session: a green dot if it is active; name; remaining cache time; the four usage numbers
        private void DrawSessionRow(Graphics g, UsageTracker.SessionTotal t, float pad, float inner, float s, float colW, float cacheW, float nameW, float y, float rowH)
        {
            Color c = t.Active ? TextMain : TextDim;
            if (t.Active)
            {
                using (SolidBrush gb = new SolidBrush(Green))
                {
                    g.FillEllipse(gb, pad, y + 5f * s, 6f * s, 6f * s);
                }
            }

            // Show the session title if there is one (it tells sessions apart best); otherwise "project · session ID"
            string name = t.Title != null ? t.Title : t.Name + " · #" + t.ShortId;
            if (t.SubCount > 0)
            {
                name += " +" + t.SubCount + " sub";
            }
            DrawTextRect(g, name, fSmall, c, new RectangleF(pad + 12f * s, y, nameW - 12f * s, rowH), StringAlignment.Near);

            // Cache remaining: time of the main session's last cache read/write + cache lifetime; a countdown while it has not expired
            string cacheText;
            Color cacheColor;
            if (t.CacheExpiryUtc.HasValue)
            {
                TimeSpan rem = t.CacheExpiryUtc.Value - DateTime.UtcNow;
                if (rem > TimeSpan.Zero)
                {
                    cacheText = FormatRemain(rem);
                    cacheColor = Green;
                }
                else
                {
                    cacheText = "Expired";
                    cacheColor = TextDim;
                }
            }
            else
            {
                cacheText = "No cache";
                cacheColor = TextDim;
            }
            DrawTextRect(g, cacheText, fSmall, cacheColor, new RectangleF(pad + nameW, y, cacheW, rowH), StringAlignment.Far);

            DrawRowNumbers(g, pad, inner, colW, y, rowH, Numbers(t.Input, t.Output, t.CacheWrite, t.CacheRead), c);
        }

        // Remaining time: m:ss when under 1 hour, h:mm:ss otherwise
        private static string FormatRemain(TimeSpan rem)
        {
            if (rem.TotalHours >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)rem.TotalHours, rem.Minutes, rem.Seconds);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", rem.Minutes, rem.Seconds);
        }

        private static string[] Numbers(long input, long output, long cacheWrite, long cacheRead)
        {
            return new string[]
            {
                UsageTracker.FormatTokens(input),
                UsageTracker.FormatTokens(output),
                UsageTracker.FormatTokens(cacheWrite),
                UsageTracker.FormatTokens(cacheRead)
            };
        }

        // The four columns on the right are right-aligned
        private void DrawRowNumbers(Graphics g, float pad, float inner, float colW, float y, float h, string[] texts, Color color)
        {
            float x = pad + colWidth[0] + colWidth[1];
            for (int k = 0; k < 4; k++)
            {
                DrawTextFit(g, texts[k], fSmall, color, new RectangleF(x, y, colWidth[2 + k], h), StringAlignment.Far);
                x += colWidth[2 + k];
            }
        }

        // Run scripts\sync_vscode_titles.py to export the session titles from VS Code. Runs on a background thread, waits at most 60 seconds, never blocks the UI
        private void StartVsCodeSync()
        {
            string dir = AppPaths.DataDir;
            string script = AppPaths.Script("sync_vscode_titles.py");
            if (!File.Exists(script))
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("python", "-I \"" + script + "\" \"" + dir + "\"");
                    AppPaths.ApplyScriptEnv(psi);
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                    using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                    {
                        if (!p.WaitForExit(60000))
                        {
                            p.Kill();
                        }
                    }
                }
                catch (Exception)
                {
                    // No Python or the script failed: keep the titles exported last time
                }
            });
        }

        // Run scripts\quota_fit.py --quick: from the quota snapshots and usage details, work out how much quota each model uses per dollar (writes logs\quota_conversion.csv).
        // Runs for a few seconds at low priority in the background, once an hour (and once at startup): the more data piles up, the more accurate the coefficients become
        private void StartQuotaFit()
        {
            string dir = AppPaths.DataDir;
            string script = AppPaths.Script("quota_fit.py");
            if (!File.Exists(script))
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("python", "-I \"" + script + "\" --quick");
                    AppPaths.ApplyScriptEnv(psi);
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                    using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                    {
                        try
                        {
                            p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                        }
                        catch (Exception)
                        {
                        }
                        if (!p.WaitForExit(120000))
                        {
                            p.Kill();
                        }
                    }
                    quotaReadUtc = DateTime.MinValue;   // Re-read the new result right away
                }
                catch (Exception)
                {
                    // No Python or the script failed: keep using the previous result
                }
            });
        }

        // Read quota_conversion.csv, re-read every 5 minutes; without the file no estimate is shown
        private void LoadQuota()
        {
            if (DateTime.UtcNow - quotaReadUtc < TimeSpan.FromMinutes(5))
            {
                return;
            }
            quotaReadUtc = DateTime.UtcNow;
            try
            {
                QuotaTable t = QuotaTable.Load(Path.Combine(AppPaths.DataDir, "quota_conversion.csv"), AccountStore.CurrentKey);
                if (t != null)
                {
                    quota = t;
                }
            }
            catch (IOException)
            {
                quotaReadUtc = DateTime.MinValue;   // The file is being written by the script, read again next round
            }
        }

        // ---------------------------------------------------------------- Accounts and subscriptions

        // Small tag in the top right of the compact card: "B · Pro". Click it to open the account menu. When the current login has no account profile yet it says "Unbound"
        private string AccountChipText()
        {
            Account cur = AccountStore.Current;
            if (cur != null)
            {
                return cur.Id + " · " + PlanInfo.ShortOf(cur.Plan);
            }
            return login != null && login.Ok ? "Unbound" : "";
        }

        private void DrawAccountChip(Graphics g, float s, float pad)
        {
            accountRect = RectangleF.Empty;
            string chip = AccountChipText();
            if (chip.Length == 0 || !skin.ShowAccountChip)
            {
                return;
            }
            float cw = MeasureW(g, chip) + 14f * s;
            if (refreshRect.X - cw - 2f * s < pad + 100f * s && AccountStore.Current != null)
            {
                chip = AccountStore.Current.Id;     // Too narrow: keep only the ID
                cw = MeasureW(g, chip) + 14f * s;
            }
            RectangleF r = new RectangleF(refreshRect.X - cw - 2f * s, 8f * s, cw, 18f * s);
            Color c = AccountStore.Current == null ? Amber : TextDim;
            SkinDraw.RrLine(g, c, r, 9f * s, 1f * s);
            DrawTextRect(g, chip, fSmall, c, r, StringAlignment.Center);
            accountRect = r;
        }

        private void Balloon(string title, string text)
        {
            if (tray != null)
            {
                tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);
            }
        }

        // Every 30 seconds read the login information on a background thread (only org / plan / subscription creation time, no token is touched)
        private void CheckLogin()
        {
            if (DateTime.UtcNow - loginCheckUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }
            loginCheckUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                LoginInfo li = null;
                try
                {
                    li = LoginInfo.Read();
                }
                catch (Exception)
                {
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        try
                        {
                            ApplyLogin(li);
                        }
                        catch (Exception)
                        {
                        }
                    }));
                }
                catch (Exception)
                {
                    // The window has already been closed
                }
            });
        }

        private void ApplyLogin(LoginInfo li)
        {
            if (li == null)
            {
                return;
            }
            login = li;
            if (!li.Ok)
            {
                Redraw();
                return;
            }
            if (AccountStore.Accounts.Count == 0 && !bootDone)
            {
                StartHistoryBootstrap(li);
                return;
            }
            Account a = AccountStore.ByOrg(li.OrgUuid);
            bool changed = false;
            if (a == null)
            {
                // A login account not seen before: create a profile automatically, with the detected plan and subscription start date
                a = new Account();
                a.Id = AccountStore.NextId(AccountStore.Accounts);
                a.BindOrg = li.OrgUuid;
                AccountStore.FillFromLogin(a, li);
                AccountStore.Accounts.Add(a);
                AccountStore.SaveAccounts();
                Balloon("New login detected", "Profile created: \"" + a.Label + "\" (" + PlanInfo.NameOf(a.Plan) + "). Right-click → Accounts and subscriptions to rename it or set the monthly fee.");
                changed = true;
            }
            else
            {
                bool dirty = false;
                if (li.Plan.Length > 0 && li.Plan != a.Plan
                    && (PlanInfo.Family(li.Plan) != PlanInfo.Family(a.Plan) || (li.PlanExact && PlanInfo.Family(a.Plan) == "max")))
                {
                    string old = a.Plan;
                    a.Plan = li.Plan;
                    if (a.Fee <= 0 || Math.Abs(a.Fee - PlanInfo.FeeOf(old)) < 0.01)
                    {
                        a.Fee = PlanInfo.FeeOf(a.Plan);
                    }
                    Balloon("Subscription plan changed", a.Label + ": " + PlanInfo.NameOf(old) + " → " + PlanInfo.NameOf(a.Plan) + ". From now on, quota conversion is calculated separately for the new plan.");
                    dirty = true;
                }
                if (a.Start == DateTime.MinValue && li.SubStartLocal.HasValue)
                {
                    a.Start = li.SubStartLocal.Value.Date;
                    dirty = true;
                }
                if (a.Hint.Length == 0 && li.Hint.Length > 0)
                {
                    a.Hint = li.Hint;
                    dirty = true;
                }
                if (dirty)
                {
                    AccountStore.SaveAccounts();
                    changed = true;
                }
            }
            Account before = AccountStore.Current;
            if (AccountStore.SetCurrent(a.Id, a.Plan, "auto-detected"))
            {
                if (before != null && before.Id != a.Id)
                {
                    Balloon("Switched to account " + a.Label, "The account logged in to Claude Code changed (" + PlanInfo.NameOf(a.Plan) + "). Quota and usage are counted under \"" + a.Label + "\".");
                }
                changed = true;
            }
            if (changed)
            {
                AfterAccountChange();
            }
            else
            {
                Redraw();
            }
        }

        // First run (no account profile yet): scan the session records for credential_org on a background thread, to restore which login accounts were used in the past and when they were switched
        private void StartHistoryBootstrap(LoginInfo li)
        {
            if (bootBusy)
            {
                return;
            }
            bootBusy = true;
            List<string> projs = ClaudeRoots.ProjectDirs();
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<KeyValuePair<DateTime, string>> hits = null;
                try
                {
                    hits = new List<KeyValuePair<DateTime, string>>();
                    foreach (string proj in projs)
                    {
                        hits.AddRange(AccountStore.ScanHistory(proj));
                    }
                    hits.Sort(delegate(KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b) { return a.Key.CompareTo(b.Key); });
                }
                catch (Exception)
                {
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        bootBusy = false;
                        bootDone = true;
                        try
                        {
                            AccountStore.BuildFromHistory(hits, li);
                        }
                        catch (Exception)
                        {
                        }
                        loginCheckUtc = DateTime.MinValue;     // Match the current login again right away
                        AfterAccountChange();
                    }));
                }
                catch (Exception)
                {
                    bootBusy = false;
                }
            });
        }

        // Account / plan changed: recompute the quota conversion on the new basis and re-read the subscription-period usage
        private void AfterAccountChange()
        {
            quotaReadUtc = DateTime.MinValue;
            quotaFitUtc = DateTime.MinValue;
            subRefreshUtc = DateTime.MinValue;
            logRefreshUtc = DateTime.MinValue;
            UpdateTrayText();
            Redraw();
        }

        // Manual account choice. The widget reads the quota of the "currently logged-in account", so statistics can only be assigned to the account that matches the current login:
        // account not bound → bind the current login to it; bound to another login → explain why, and ask to /login in Claude Code first
        private void TrySwitchAccount(string id)
        {
            Account a = AccountStore.Get(id);
            if (a == null)
            {
                return;
            }
            Account cur = AccountStore.Current;
            if (cur != null && cur.Id == id)
            {
                return;
            }
            if (login != null && login.Ok)
            {
                if (a.BindOrg.Length > 0 && a.BindOrg != login.OrgUuid)
                {
                    MessageBox.Show(this, "Account \"" + a.Label + "\" is bound to a different Claude login" + (a.Hint.Length > 0 ? " (" + a.Hint + ")" : "") + ". \r\n\r\n"
                        + "The widget reads the quota of the currently logged-in account (" + (cur != null ? cur.Label : "Unbound") + "), so usage cannot be assigned to \"" + a.Label + "\".\r\n"
                        + "Log in to that account with /login in Claude Code first; the widget will switch to it automatically.", "Accounts and subscriptions", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Account owner = AccountStore.ByOrg(login.OrgUuid);
                if (owner != null && owner != a)
                {
                    if (MessageBox.Show(this, "The current login is already bound to \"" + owner.Label + "\". Rebind it to \"" + a.Label + "\"?\r\n(" + owner.Label + " will become unbound)", "Accounts and subscriptions",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        return;
                    }
                    owner.BindOrg = "";
                    owner.Hint = "";
                }
                if (a.BindOrg.Length == 0)
                {
                    a.BindOrg = login.OrgUuid;
                    a.Hint = login.Hint;
                    AccountStore.FillFromLogin(a, login);
                }
                AccountStore.SaveAccounts();
            }
            AccountStore.SetCurrent(a.Id, a.Plan, "manual");
            AfterAccountChange();
        }

        private void FillAccountItems(ToolStripItemCollection items)
        {
            Account cur = AccountStore.Current;
            foreach (Account a in AccountStore.Accounts)
            {
                Account acc = a;
                ToolStripMenuItem it = new ToolStripMenuItem(a.Label + " · " + PlanInfo.NameOf(a.Plan) + (a.BindOrg.Length == 0 ? " (unbound)" : ""));
                it.Checked = a == cur;
                it.Click += delegate { TrySwitchAccount(acc.Id); };
                items.Add(it);
            }
            if (AccountStore.Accounts.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("(detecting the current login...)");
                none.Enabled = false;
                items.Add(none);
            }
            items.Add(new ToolStripSeparator());
            items.Add("Subscription view", null, delegate
            {
                if (!showUsage)
                {
                    SetUsageVisible(true);
                }
                CollapseRows();
                usageMode = 4;
                Redraw();
                SaveSettings();
            });
            items.Add("Manage accounts and subscriptions...", null, delegate { OpenAccountManager(); });
        }

        private void ShowAccountMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            FillAccountItems(m.Items);
            m.Closed += delegate { BeginInvoke(new MethodInvoker(delegate { m.Dispose(); })); };
            m.Show(this, new Point((int)accountRect.X, (int)accountRect.Bottom + 2));
        }

        private void OpenAccountManager()
        {
            Account cur = AccountStore.Current;
            Dictionary<string, string> oldPlans = new Dictionary<string, string>();
            foreach (Account a in AccountStore.Accounts)
            {
                oldPlans[a.Id] = a.Plan;
            }
            using (AccountForm f = new AccountForm(login, cur == null ? null : cur.Id))
            {
                if (f.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                AccountStore.Accounts.Clear();
                AccountStore.Accounts.AddRange(f.Result);
                AccountStore.SaveAccounts();
                if (f.TimelineResult != null)
                {
                    AccountStore.ReplaceTimeline(f.TimelineResult);
                }
                // Plan changed: was it a real upgrade or downgrade (counted from now on), or was it recorded wrongly before (change this account's history too)?
                List<TimelineEntry> tl = AccountStore.TimelineCopy();
                bool tlChanged = false;
                foreach (Account a in AccountStore.Accounts)
                {
                    string old;
                    if (!oldPlans.TryGetValue(a.Id, out old) || old == a.Plan || AccountStore.TimelineCount(a.Id) == 0)
                    {
                        continue;
                    }
                    DialogResult dr = MessageBox.Show(this, "\"" + a.Label + "\": plan changed from " + PlanInfo.NameOf(old) + " to " + PlanInfo.NameOf(a.Plan) + ". \r\n\r\n"
                        + "Yes — I recorded it wrong before; all history of this account is counted as " + PlanInfo.NameOf(a.Plan) + "\r\n"
                        + "No — it really was an upgrade or downgrade; counted as " + PlanInfo.NameOf(a.Plan) + " from now on (history unchanged)", "Accounts and subscriptions",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr == DialogResult.Yes)
                    {
                        foreach (TimelineEntry e in tl)
                        {
                            if (e.AccountId == a.Id)
                            {
                                e.Plan = a.Plan;
                            }
                        }
                        tlChanged = true;
                    }
                }
                if (tlChanged)
                {
                    AccountStore.ReplaceTimeline(tl);
                }
                Account c2 = AccountStore.Current;
                if (c2 != null)
                {
                    AccountStore.SetCurrent(c2.Id, c2.Plan, "manual");
                }
                AfterAccountChange();
            }
        }

        // API-equivalent amount (USD) in the subscription period: all usage under this account, converted at official prices. null when not computed yet / no price table
        private double? SubCost(string accountId)
        {
            UsageTracker.PeriodSummary p;
            if (quota == null || !subBy.TryGetValue(accountId, out p))
            {
                return null;
            }
            double sum = 0;
            foreach (UsageTracker.ModelTotal mt in p.ModelRows)
            {
                QuotaTable.Entry e;
                if (quota.Models.TryGetValue(BaseModel(mt.Model), out e))
                {
                    sum += quota.CostUsdAt(e, mt.Tok, 1.0);
                }
            }
            return sum;
        }

        private static string Money(double v)
        {
            return v >= 1000 ? "$" + (v / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k" : "$" + v.ToString("0", CultureInfo.InvariantCulture);
        }

        // Data needed by the subscription view: usage of each account in its "current subscription period". Reads the log once on a background thread (attributing to accounts by the switch timeline), at most once every 5 minutes, and only while the subscription view is open
        private void StartSubRefresh()
        {
            if (subBusy || DateTime.UtcNow - subRefreshUtc < TimeSpan.FromMinutes(5) || AccountStore.Accounts.Count == 0)
            {
                return;
            }
            DateTime now = DateTime.Now;
            List<DateTime> froms = new List<DateTime>();
            List<DateTime> tos = new List<DateTime>();
            List<string> labels = new List<string>();
            List<string> ids = new List<string>();
            foreach (Account a in AccountStore.Accounts)
            {
                DateTime cs, ce;
                bool cal;
                AccountStore.BillingCycle(a, now, out cs, out ce, out cal);
                if (cs > now)
                {
                    continue;
                }
                froms.Add(cs);
                tos.Add(now);
                labels.Add(a.Label);
                ids.Add(a.Id);
            }
            if (froms.Count == 0)
            {
                return;
            }
            subBusy = true;
            subRefreshUtc = DateTime.UtcNow;
            AccountIndex idx = AccountStore.Index();
            string detail = logger.DetailPath;
            string index = logger.IndexPath;
            DateTime[] fa = froms.ToArray(), ta = tos.ToArray();
            string[] la = labels.ToArray(), ia = ids.ToArray();
            ThreadPool.QueueUserWorkItem(delegate
            {
                UsageTracker.PeriodSummary[] res = null;
                try
                {
                    res = LogSummary.ComputeMany(detail, index, fa, ta, la, ia, idx);
                }
                catch (Exception)
                {
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (res != null)
                        {
                            for (int k = 0; k < res.Length; k++)
                            {
                                subBy[ia[k]] = res[k];
                            }
                        }
                        subBusy = false;
                        Redraw();
                    }));
                }
                catch (Exception)
                {
                    subBusy = false;
                }
            });
        }

        private List<Account> SortedAccounts()
        {
            List<Account> list = new List<Account>(AccountStore.Accounts);
            Account cur = AccountStore.Current;
            list.Sort(delegate(Account a, Account b)
            {
                if (a == cur)
                {
                    return b == cur ? 0 : -1;
                }
                if (b == cur)
                {
                    return 1;
                }
                return string.CompareOrdinal(a.Id, b.Id);
            });
            return list;
        }

        // Subscription view: one row per account. Usage this period = API-equivalent amount in the current subscription period at official prices; payback = that ÷ monthly fee; renewal = date of the next renewal
        private int DrawSubscriptionRows(Graphics g, float pad, float inner, float s, float colW, float nameW, float y, float rowH, int cap)
        {
            LoadQuota();
            accountHits.Clear();
            List<Account> items = SortedAccounts();
            if (items.Count == 0)
            {
                DrawTextRect(g, "No account profile yet · detecting the current login...", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                return 1;
            }
            Account cur = AccountStore.Current;
            DateTime now = DateTime.Now;
            double total = 0;
            foreach (Account a in items)
            {
                double? c = SubCost(a.Id);
                total += c.HasValue ? c.Value : 0;
            }
            int shown = Math.Min(items.Count, cap);
            for (int i = 0; i < shown; i++)
            {
                Account a = items[i];
                float ry = y + i * rowH;
                double? cost = SubCost(a.Id);
                skin.DrawShare(g, pad + 10f * s, ry + 2.5f * s, total > 0 && cost.HasValue ? Math.Min(1.0, cost.Value / total) : 0, curPal, s);
                float nameX = pad + (skin.ShareWidth + 17f) * s;
                float right = pad + nameW - 6f * s;
                float x = a == cur ? DrawTierBadge(g, "Current", 4, right, ry, rowH, s) : right;
                DrawTextRect(g, a.Label + " · " + PlanInfo.ShortOf(a.Plan) + (a.BindOrg.Length == 0 ? " · not bound" : ""), fSmall, a == cur ? TextMain : TextDim,
                    new RectangleF(nameX, ry, Math.Max(24f * s, x - 6f * s - nameX), rowH), StringAlignment.Near);
                DateTime cs, ce;
                bool cal;
                AccountStore.BillingCycle(a, now, out cs, out ce, out cal);
                string used = cost.HasValue ? Money(cost.Value) : (subBy.ContainsKey(a.Id) || quota == null ? "—" : "…");
                string ratio = "—";
                if (cost.HasValue && a.Fee > 0)
                {
                    double r = cost.Value / a.Fee;
                    ratio = "×" + r.ToString(r < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
                }
                DrawRowNumbers(g, pad, inner, colW, ry, rowH, new string[]
                {
                    used, a.Fee > 0 ? Money(a.Fee) : "—", ratio, cal ? "—" : ce.ToString("MM-dd", CultureInfo.InvariantCulture)
                }, a == cur ? TextMain : TextDim);
                accountHits.Add(new KeyValuePair<RectangleF, string>(new RectangleF(pad, ry, inner, rowH), a.Id));
            }
            return shown;
        }

        // Bottom of the subscription view: the subscription period of the current account, this period's usage converted, weekly quota forecast
        private void DrawSubscriptionInfo(Graphics g, float pad, float inner, float s, float y, float rowH)
        {
            Account a = AccountStore.Current;
            if (a == null)
            {
                DrawTextRect(g, "Right-click → Accounts and subscriptions → Manage... to edit name, plan, fee, start date", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                return;
            }
            DateTime now = DateTime.Now, cs, ce;
            bool cal;
            AccountStore.BillingCycle(a, now, out cs, out ce, out cal);
            int totalDays = Math.Max(1, (int)Math.Round((ce - cs).TotalDays));
            int day = Math.Max(1, Math.Min(totalDays, (int)Math.Floor((now.Date - cs.Date).TotalDays) + 1));
            string l1 = a.Label + " · " + PlanInfo.NameOf(a.Plan) + " · "
                + (a.Start == DateTime.MinValue ? "Start date unknown" : "since " + a.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "")
                + " · cycle " + day + "/" + totalDays + " d" + (cal ? " (calendar month)" : ", " + ce.ToString("MM-dd", CultureInfo.InvariantCulture) + " renewal");
            DrawTextRect(g, l1, fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);

            double? cost = SubCost(a.Id);
            string l2;
            if (!cost.HasValue)
            {
                l2 = quota == null ? "API value this cycle: no price table (quota_conversion.csv)" : "API value this cycle: calculating...";
            }
            else
            {
                double c = cost.Value;
                l2 = "API value " + Money(c) + (a.Fee > 0 ? " (fee " + Money(a.Fee) + ", ×" + (c / a.Fee).ToString(c / a.Fee < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + ")" : "")
                    + " · daily " + Money(c / day) + " · cycle ≈ " + Money(c / day * totalDays);
            }
            DrawTextRect(g, l2, fSmall, TextMain, new RectangleF(pad, y + rowH, inner, rowH), StringAlignment.Near);

            bool risk;
            string l3 = WeeklyForecast(out risk);
            DrawTextRect(g, l3, fSmall, risk ? Amber : TextDim, new RectangleF(pad, y + 2f * rowH, inner, rowH), StringAlignment.Near);
        }

        // Renewal reminder: when the renewal is within 3 days, show one tray notification (once per period). No reminder when the subscription start date is unknown
        private void CheckRenew()
        {
            if (DateTime.UtcNow - renewCheckUtc < TimeSpan.FromMinutes(10))
            {
                return;
            }
            renewCheckUtc = DateTime.UtcNow;
            Account a = AccountStore.Current;
            if (a == null || a.Start == DateTime.MinValue)
            {
                return;
            }
            DateTime cs, ce;
            bool cal;
            AccountStore.BillingCycle(a, DateTime.Now, out cs, out ce, out cal);
            double days = (ce - DateTime.Now).TotalDays;
            string id = a.Id + ce.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (days > 0 && days <= 3 && renewNotified != id)
            {
                renewNotified = id;
                double? cost = SubCost(a.Id);
                Balloon("Subscription renewing soon", a.Label + " (" + PlanInfo.NameOf(a.Plan) + ") renews in " + (int)Math.Ceiling(days) + " days (" + ce.ToString("MM-dd", CultureInfo.InvariantCulture) + ")"
                    + (a.Fee > 0 ? ", monthly fee " + Money(a.Fee) : "") + (cost.HasValue && a.Fee > 0 ? ". Usage this period is worth " + Money(cost.Value) + " (×" + (cost.Value / a.Fee).ToString("0.0", CultureInfo.InvariantCulture) + ")" : "") + ". ");
            }
        }

        // ---------------------------------------------------------------- Weekly quota forecast
        // First read the latest samples back from the end of quota_snapshots.csv; after that, append whenever data arrives
        private void EnsureWeekHist()
        {
            if (weekHistLoaded)
            {
                return;
            }
            weekHistLoaded = true;
            try
            {
                string path = Path.Combine(AppPaths.DataDir, "quota_snapshots.csv");
                if (!File.Exists(path))
                {
                    return;
                }
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long take = Math.Min(fs.Length, 96 * 1024);
                    fs.Seek(fs.Length - take, SeekOrigin.Begin);
                    byte[] buf = new byte[take];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0)
                        {
                            break;
                        }
                        got += n;
                    }
                    foreach (string line in Encoding.UTF8.GetString(buf, 0, got).Split('\n'))
                    {
                        string[] f = line.TrimEnd('\r').Split(',');
                        DateTime t, rs;
                        double p;
                        if (f.Length >= 5 && DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)
                            && DateTime.TryParseExact(f[4], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out rs)
                            && double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out p))
                        {
                            WeekPoint w = new WeekPoint();
                            w.T = t;
                            w.Pct = p;
                            w.Reset = rs.Ticks;
                            weekHist.Add(w);
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        private void NoteWeekly(FetchResult r)
        {
            if (r.SevenDay == null || !r.SevenDay.ResetsAt.HasValue)
            {
                return;
            }
            EnsureWeekHist();
            long reset = r.SevenDay.ResetsAt.Value.Ticks;
            WeekPoint last = weekHist.Count > 0 ? weekHist[weekHist.Count - 1] : null;
            if (last != null && last.Reset == reset && Math.Abs(last.Pct - r.SevenDay.Percent) < 0.001 && (r.FetchedAt - last.T).TotalMinutes < 10)
            {
                return;
            }
            WeekPoint w = new WeekPoint();
            w.T = r.FetchedAt;
            w.Pct = r.SevenDay.Percent;
            w.Reset = reset;
            weekHist.Add(w);
            while (weekHist.Count > 0 && (r.FetchedAt - weekHist[0].T).TotalHours > 48)
            {
                weekHist.RemoveAt(0);
            }
        }

        private static string HoursText(double h)
        {
            if (h >= 48)
            {
                return (h / 24).ToString("0.0", CultureInfo.InvariantCulture) + " d";
            }
            if (h >= 10)
            {
                return Math.Round(h).ToString("0", CultureInfo.InvariantCulture) + " h";
            }
            if (h >= 1)
            {
                return h.ToString("0.0", CultureInfo.InvariantCulture) + " h";
            }
            return Math.Max(1, (int)Math.Round(h * 60)) + " min";
        }

        // One sentence: at the recent pace, will the weekly quota run out before the reset? Whole percentages; with little data it only says "not enough yet" instead of guessing
        private string WeeklyForecast(out bool risk)
        {
            risk = false;
            if (data == null || data.SevenDay == null || !data.SevenDay.ResetsAt.HasValue)
            {
                return "";
            }
            EnsureWeekHist();
            DateTime now = DateTime.Now;
            double pct = data.SevenDay.Percent;
            DateTime reset = data.SevenDay.ResetsAt.Value;
            double hoursLeft = (reset - now).TotalHours;
            if (hoursLeft <= 0)
            {
                return "";
            }
            WeekPoint p0 = null;
            foreach (WeekPoint p in weekHist)
            {
                if (p.Reset == reset.Ticks && (now - p.T).TotalHours <= 12 && (p0 == null || p.T < p0.T))
                {
                    p0 = p;
                }
            }
            string head = "Week " + pct.ToString("0", CultureInfo.InvariantCulture) + "% (" + HoursText(hoursLeft) + " left)";
            if (p0 == null || (now - p0.T).TotalHours < 1.5)
            {
                return head + " · forecast: not enough data yet";
            }
            double dt = (now - p0.T).TotalHours;
            double dp = pct - p0.Pct;
            if (dp < 1.5)
            {
                return head + " · last " + HoursText(dt) + ": flat, safe until reset";
            }
            double rate = dp / dt;
            double toFull = (100 - pct) / rate;
            if (toFull < hoursLeft)
            {
                risk = true;
                return head + " · " + HoursText(dt) + " pace: full in " + HoursText(toFull) + ", early by " + HoursText(hoursLeft - toFull);
            }
            return head + " · " + HoursText(dt) + " pace: reaches ~" + Math.Min(100, pct + rate * hoursLeft).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // Strip the date suffix: claude-haiku-4-5-20251001 → claude-haiku-4-5
        private static string BaseModel(string model)
        {
            return System.Text.RegularExpressions.Regex.Replace(model ?? "", @"-\d{8}$", "");
        }

        private string SessionTitle(string session)
        {
            foreach (UsageTracker.SessionTotal t in PeriodFor(usagePeriod).Rows)
            {
                if (t.Session == session)
                {
                    return t.Title ?? (t.Name + " · #" + t.ShortId);
                }
            }
            return session.Substring(0, Math.Min(6, session.Length));
        }

        // How many quota percentage points these tokens convert to (week = true uses the weekly quota). approx = this model was never measured, estimated from official prices
        private bool TryQuotaPct(string model, long[] tok, bool week, out double pct, out bool approx)
        {
            pct = 0;
            approx = false;
            QuotaTable.Entry e;
            if (quota == null || !quota.Models.TryGetValue(BaseModel(model), out e))
            {
                return false;
            }
            double k = week ? e.KW : e.K5;
            if (k <= 0)
            {
                return false;
            }
            pct = quota.CostUsd(e, tok) * k;
            approx = !(week ? e.MeasuredW : e.Measured5) || quota.Stale;
            return true;
        }

        // How the quota estimate is shown: keep more digits when the number is small; "≈" = this model was never measured, estimated from official prices; no conversion (no prices / no file) shows "—"
        private string EstimateText(string model, long[] tok, bool week)
        {
            double pct;
            bool approx;
            if (!TryQuotaPct(model, tok, week, out pct, out approx))
            {
                return "—";
            }
            return FormatPct(pct, approx);
        }

        private static string FormatPct(double pct, bool approx)
        {
            // The "≈" prefix takes room, so keep one decimal less
            string num = pct < 10 ? pct.ToString(approx ? "0.0" : "0.00", CultureInfo.InvariantCulture) : pct.ToString("0", CultureInfo.InvariantCulture);
            return (approx ? "≈" : "") + num + "%";
        }

        // One row for the tier view / total / compare view: the total of one base model (date suffix removed) within one tier
        private sealed class TierItem
        {
            public string Model, Tier;
            public long Msgs, Out, Think, Total;
            public double Pct, PctWeek;
            public bool HasPct, Approx;
        }

        // Merge TierRows into "base model × tier" and convert to quota (model coefficient × tier correction). Sorted by quota, largest first; rows without a conversion go last
        private List<TierItem> BuildTierItems(UsageTracker.PeriodSummary cur)
        {
            Dictionary<string, TierItem> map = new Dictionary<string, TierItem>();
            Dictionary<string, long[]> toks = new Dictionary<string, long[]>();
            foreach (UsageTracker.TierTotal tt in cur.TierRows)
            {
                if (tt.Total == 0)
                {
                    continue;   // Skip rows without tokens (for example the placeholder messages of <synthetic>)
                }
                string model = BaseModel(tt.Model);
                string key = model + "\n" + tt.Tier;
                TierItem it;
                long[] tk;
                if (!map.TryGetValue(key, out it))
                {
                    it = new TierItem();
                    it.Model = model;
                    it.Tier = tt.Tier;
                    map[key] = it;
                    tk = new long[10];
                    toks[key] = tk;
                }
                else
                {
                    tk = toks[key];
                }
                it.Msgs += tt.Msgs;
                it.Out += tt.Out;
                it.Think += tt.Think;
                it.Total += tt.Total;
                for (int i = 0; i < 10; i++)
                {
                    tk[i] += tt.Tok[i];
                }
            }
            List<TierItem> list = new List<TierItem>(map.Values);
            foreach (KeyValuePair<string, TierItem> kv in map)
            {
                double pct, pctWeek;
                bool approx, approxWeek;
                if (TryQuotaPct(kv.Value.Model, toks[kv.Key], false, out pct, out approx))
                {
                    kv.Value.Pct = pct * quota.TierFactor(kv.Value.Tier, false);
                    kv.Value.HasPct = true;
                    kv.Value.Approx = approx;
                }
                if (TryQuotaPct(kv.Value.Model, toks[kv.Key], true, out pctWeek, out approxWeek))
                {
                    kv.Value.PctWeek = pctWeek * quota.TierFactor(kv.Value.Tier, true);
                }
            }
            list.Sort(delegate(TierItem a, TierItem b)
            {
                if (a.HasPct != b.HasPct)
                {
                    return a.HasPct ? -1 : 1;
                }
                int c = b.Pct.CompareTo(a.Pct);
                return c != 0 ? c : b.Total.CompareTo(a.Total);
            });
            return list;
        }

        // Total quota of all usage in this period (5 hours / weekly); with "≈" if any model was estimated from prices; empty if there is none
        private string TotalEstimateText(UsageTracker.PeriodSummary cur, bool week)
        {
            double sum = 0;
            bool any = false, approxAny = false;
            if (cur.TierRows.Count > 0)
            {
                foreach (TierItem it in BuildTierItems(cur))
                {
                    if (it.HasPct)
                    {
                        sum += week ? it.PctWeek : it.Pct;
                        any = true;
                        approxAny = approxAny || it.Approx;
                    }
                }
            }
            else
            {
                foreach (UsageTracker.ModelTotal mt in cur.ModelRows)
                {
                    double pct;
                    bool approx;
                    if (TryQuotaPct(mt.Model, mt.Tok, week, out pct, out approx))
                    {
                        sum += pct;
                        any = true;
                        approxAny = approxAny || approx;
                    }
                }
            }
            return any && sum > 0 ? FormatPct(sum, approxAny) : "";
        }

        // Quota estimate of each base model in this period (all tiers added up)
        private void PerModelPct(UsageTracker.PeriodSummary cur, Dictionary<string, double> pct, Dictionary<string, bool> approx)
        {
            if (cur.TierRows.Count > 0)
            {
                foreach (TierItem it in BuildTierItems(cur))
                {
                    if (!it.HasPct)
                    {
                        continue;
                    }
                    double v;
                    pct.TryGetValue(it.Model, out v);
                    pct[it.Model] = v + it.Pct;
                    approx[it.Model] = it.Approx;
                }
                return;
            }
            Dictionary<string, long[]> toks = new Dictionary<string, long[]>();
            foreach (UsageTracker.ModelTotal mt in cur.ModelRows)
            {
                string key = BaseModel(mt.Model);
                long[] tk;
                if (!toks.TryGetValue(key, out tk))
                {
                    tk = new long[10];
                    toks[key] = tk;
                }
                for (int i = 0; i < 10; i++)
                {
                    tk[i] += mt.Tok[i];
                }
            }
            foreach (KeyValuePair<string, long[]> kv in toks)
            {
                double p;
                bool a;
                if (TryQuotaPct(kv.Key, kv.Value, false, out p, out a))
                {
                    pct[kv.Key] = p;
                    approx[kv.Key] = a;
                }
            }
        }

        private string[] TierTotalNums(UsageTracker.PeriodSummary cur)
        {
            long msgs = 0, outp = 0, think = 0;
            foreach (UsageTracker.TierTotal tt in cur.TierRows)
            {
                msgs += tt.Msgs;
                outp += tt.Out;
                think += tt.Think;
            }
            return new string[] { UsageTracker.FormatTokens(msgs), UsageTracker.FormatTokens(outp), UsageTracker.FormatTokens(think), TotalEstimateText(cur, false) };
        }

        // Tier view: one row per "model × tier". Tier = thinking effort (low / medium / high / xhigh / max); messages with Ultracode on count as ultra on their own;
        // fast mode appends +fast; records without a tag are called "Unlabeled". Count = number of API messages; output includes thinking; 5h estimate = converted quota
        private int DrawTierRows(Graphics g, UsageTracker.PeriodSummary cur, float pad, float inner, float s,
            float colW, float nameW, float y, float rowH, int cap)
        {
            LoadQuota();
            List<TierItem> items = BuildTierItems(cur);
            if (items.Count == 0)
            {
                DrawTextRect(g, "No usage in this period", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                return 1;
            }
            double sumPct = 0;
            long sumTotal = 0;
            foreach (TierItem it in items)
            {
                sumPct += it.Pct;
                sumTotal += it.Total;
            }
            int shown = items.Count <= cap ? items.Count : cap - 1;
            int drawn = 0;
            for (int i = 0; i < shown; i++)
            {
                TierItem it = items[i];
                float ry = y + drawn * rowH;
                double share = sumPct > 0 ? it.Pct / sumPct : ShareOf(it.Total, sumTotal);
                skin.DrawShare(g, pad + 6f * s, ry + 2.5f * s, share, curPal, s);
                float nameX = pad + (skin.ShareWidth + 13f) * s;
                DrawTextRect(g, it.Model.Replace("claude-", "") + " " + it.Tier, fSmall, TextMain,
                    new RectangleF(nameX, ry, nameW - (nameX - pad), rowH), StringAlignment.Near);
                DrawRowNumbers(g, pad, inner, colW, ry, rowH, new string[]
                {
                    UsageTracker.FormatTokens(it.Msgs), UsageTracker.FormatTokens(it.Out), UsageTracker.FormatTokens(it.Think),
                    it.HasPct ? FormatPct(it.Pct, it.Approx) : "—"
                }, TextMain);
                drawn++;
            }
            if (shown < items.Count)
            {
                long rm = 0, ro = 0, rt = 0;
                for (int i = shown; i < items.Count; i++)
                {
                    rm += items[i].Msgs;
                    ro += items[i].Out;
                    rt += items[i].Think;
                }
                DrawTextRect(g, "And " + (items.Count - shown) + " more", fSmall, TextDim,
                    new RectangleF(pad + 4f * s, y + drawn * rowH, nameW - 4f * s, rowH), StringAlignment.Near);
                DrawRowNumbers(g, pad, inner, colW, y + drawn * rowH, rowH,
                    new string[] { UsageTracker.FormatTokens(rm), UsageTracker.FormatTokens(ro), UsageTracker.FormatTokens(rt), "" }, TextDim);
                drawn++;
            }
            return drawn;
        }

        // Rank of a tier (used for the tag style): low < medium < high < xhigh < max < ultra
        private static int TierRank(string tier)
        {
            string b = tier ?? "";
            int plus = b.IndexOf('+');
            if (plus > 0)
            {
                b = b.Substring(0, plus);
            }
            switch (b)
            {
                case "low": return 1;
                case "medium": return 2;
                case "high": return 3;
                case "xhigh": return 4;
                case "max": return 5;
                case "ultra": return 6;
            }
            return 0;
        }

        private static string ShortModel(string model)
        {
            return BaseModel(model).Replace("claude-", "");
        }

        private float MeasureW(Graphics g, string text)
        {
            return g.MeasureString(text, fSmall, 4096, StringFormat.GenericTypographic).Width;
        }

        // Small tier tag (right edge aligned to rightX), returns the left edge of the tag. low~high: dashed frame, pale text; xhigh: solid frame; max: filled; ultra: filled orange. Empty text = draw nothing
        private float DrawTierBadge(Graphics g, string text, int rank, float rightX, float y, float rowH, float s)
        {
            if (string.IsNullOrEmpty(text))
            {
                return rightX;
            }
            float bw = MeasureW(g, text) + 9f * s;
            float bh = rowH - 4f * s;
            RectangleF r = new RectangleF(rightX - bw, y + (rowH - bh) / 2f, bw, bh);
            Color txt;
            if (rank >= 6)
            {
                SkinDraw.Rr(g, Amber, r, 4f * s);
                txt = Bg;
            }
            else if (rank == 5)
            {
                SkinDraw.Rr(g, TextMain, r, 4f * s);
                txt = Bg;
            }
            else if (rank == 4)
            {
                SkinDraw.RrLine(g, TextMain, r, 4f * s, 1.2f * s);
                txt = TextMain;
            }
            else
            {
                SkinDraw.RrLine(g, TextDim, r, 4f * s, 1f * s);
                txt = TextDim;
            }
            DrawTextRect(g, text, fSmall, txt, r, StringAlignment.Center);
            return r.X;
        }

        // "Model + tier" summary of a session: one model shows its name, several show "first+n"; tiers are chained in the order first used (xhigh→max), the first and last when there are too many
        private static void GroupSummary(ModelGroup grp, out string modelText, out string tierText, out int rank)
        {
            List<string> models = new List<string>();
            List<string> tiers = new List<string>();
            rank = 0;
            foreach (UsageTracker.ModelTotal p in grp.Parts)
            {
                string m = ShortModel(p.Model);
                if (!models.Contains(m))
                {
                    models.Add(m);
                }
                if (p.Tier.Length > 0 && p.Tier != "Unlabeled" && !tiers.Contains(p.Tier))
                {
                    tiers.Add(p.Tier);
                }
                rank = Math.Max(rank, TierRank(p.Tier));
            }
            modelText = models.Count == 0 ? "" : (models.Count == 1 ? models[0] : models[0] + "+" + (models.Count - 1));
            tierText = tiers.Count == 0 ? "" : (tiers.Count <= 3 ? string.Join("→", tiers.ToArray()) : tiers[0] + "→…→" + tiers[tiers.Count - 1]);
        }

        // Quota converted for one session × model × tier (including the tier correction); week = weekly quota. Shows "—" when there is no conversion, "≈" = not measured
        private string PartEstimate(UsageTracker.ModelTotal mt, bool week)
        {
            double pct;
            bool approx;
            if (!TryQuotaPct(mt.Model, mt.Tok, week, out pct, out approx))
            {
                return "—";
            }
            return FormatPct(pct * quota.TierFactor(mt.Tier, week), approx);
        }

        private string GroupEstimate(ModelGroup grp, bool week)
        {
            double sum = 0;
            bool any = false, approxAny = false;
            foreach (UsageTracker.ModelTotal mt in grp.Parts)
            {
                double pct;
                bool approx;
                if (TryQuotaPct(mt.Model, mt.Tok, week, out pct, out approx))
                {
                    sum += pct * quota.TierFactor(mt.Tier, week);
                    any = true;
                    approxAny = approxAny || approx;
                }
            }
            return any ? FormatPct(sum, approxAny) : "—";
        }

        // A time range: HH:mm on the same day, with the date across days
        private static string SpanText(DateTime firstUtc, DateTime lastUtc)
        {
            DateTime a = firstUtc.ToLocalTime(), b = lastUtc.ToLocalTime();
            string f = a.Date == b.Date && a.Date == DateTime.Today ? "HH:mm" : "MM-dd HH:mm";
            return a.ToString(f, CultureInfo.InvariantCulture) + "–" + b.ToString(f, CultureInfo.InvariantCulture);
        }

        // Model view: one row per session (▸ opens each model × tier it used). Tokens are real data; the "estimate" is converted quota, for reference only
        private int DrawModelRows(Graphics g, UsageTracker.PeriodSummary cur, float pad, float inner, float s,
            float colW, float nameW, float y, float rowH, int cap)
        {
            LoadQuota();
            chevHits.Clear();
            List<ModelLine> items = BuildModelLines(cur);
            if (items.Count == 0)
            {
                DrawTextRect(g, "No model usage in this period", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                return 1;
            }
            int rowStart, rowShown;
            bool rowMore, rowCollapse;
            RowWindow(items.Count, cap, out rowStart, out rowShown, out rowMore, out rowCollapse);
            int drawn = 0;
            for (int i = rowStart; i < rowStart + rowShown; i++)
            {
                ModelLine ln = items[i];
                float ry = y + drawn * rowH;
                if (ln.Part == null)
                {
                    DrawModelGroupRow(g, ln, cur, pad, inner, s, colW, nameW, ry, rowH);
                }
                else
                {
                    DrawModelPartRow(g, ln.Part, pad, inner, s, colW, nameW, ry, rowH);
                }
                sessionHits.Add(new KeyValuePair<RectangleF, string>(new RectangleF(pad, ry, inner, rowH), ln.Group.Session));
                drawn++;
            }
            if (rowMore)
            {
                long rest = 0;
                int groupsLeft = 0;
                for (int i = rowShown; i < items.Count; i++)
                {
                    if (items[i].Part == null)
                    {
                        rest += items[i].Group.Total;
                        groupsLeft++;
                    }
                }
                DrawMoreRow(g, "▾ " + (groupsLeft > 0 ? groupsLeft + " more" : (items.Count - rowShown) + " more rows"),
                    new string[] { UsageTracker.FormatTokens(rest), "", "", "" }, pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                drawn++;
            }
            else if (rowCollapse)
            {
                DrawMoreRow(g, items.Count > rowShown ? "▴ Collapse · scroll to page" : "▴ Collapse", null, pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                drawn++;
            }
            return drawn;
        }

        // Session row: [▸] small circle title ……… model [tier]. When narrow, drop the model name first, then shorten "xhigh→max" to "…→max"; the title keeps at least 70 base units
        private void DrawModelGroupRow(Graphics g, ModelLine ln, UsageTracker.PeriodSummary cur, float pad, float inner, float s,
            float colW, float nameW, float ry, float rowH)
        {
            ModelGroup grp = ln.Group;
            if (grp.Parts.Count > 1)
            {
                DrawTextRect(g, ln.Expanded ? "▾" : "▸", fSmall, Accent, new RectangleF(pad - 2f * s, ry, 13f * s, rowH), StringAlignment.Center);
                chevHits.Add(new KeyValuePair<RectangleF, string>(new RectangleF(pad - 4f * s, ry, 26f * s, rowH), grp.Session));
            }
            skin.DrawShare(g, pad + 10f * s, ry + 2.5f * s, ShareOf(grp.Total, cur.Total), curPal, s);
            float nameX = pad + (skin.ShareWidth + 17f) * s;
            string modelText, tierText;
            int rank;
            GroupSummary(grp, out modelText, out tierText, out rank);
            float right = pad + nameW - 6f * s;
            float minTitle = 70f * s;
            int arrow = tierText.LastIndexOf('→');
            if (arrow >= 0 && MeasureW(g, tierText) + 9f * s > right - nameX - minTitle)
            {
                tierText = "…→" + tierText.Substring(arrow + 1);
            }
            float x = DrawTierBadge(g, tierText, rank, right, ry, rowH, s);
            float titleRight = x - 6f * s;
            float mw = MeasureW(g, modelText) + 12f * s;
            if (modelText.Length > 0 && titleRight - mw - nameX >= minTitle)
            {
                DrawTextRect(g, modelText, fSmall, TextDim, new RectangleF(titleRight - mw, ry, mw, rowH), StringAlignment.Far);
                titleRight -= mw + 4f * s;
            }
            DrawTextRect(g, SessionTitle(grp.Session), fSmall, TextMain, new RectangleF(nameX, ry, Math.Max(24f * s, titleRight - nameX), rowH), StringAlignment.Near);
            DrawRowNumbers(g, pad, inner, colW, ry, rowH, new string[]
            {
                UsageTracker.FormatTokens(grp.Total), GroupEstimate(grp, false), GroupEstimate(grp, true), UsageTracker.FormatTokens(grp.CacheRead)
            }, TextMain);
        }

        // Expanded sub-row: indented to the small circle, └ model period ……… [tier]; when it does not fit, drop the period first
        private void DrawModelPartRow(Graphics g, UsageTracker.ModelTotal mt, float pad, float inner, float s,
            float colW, float nameW, float ry, float rowH)
        {
            float textX = pad + 24f * s;
            DrawTextRect(g, "└", fSmall, TextDim, new RectangleF(pad + 10f * s, ry, 12f * s, rowH), StringAlignment.Center);
            float right = pad + nameW - 6f * s;
            float x = DrawTierBadge(g, mt.Tier == "Unlabeled" ? "" : mt.Tier, TierRank(mt.Tier), right, ry, rowH, s);
            float avail = x - 6f * s - textX;
            string model = ShortModel(mt.Model);
            string full = model + "  " + SpanText(mt.FirstUtc, mt.LastUtc);
            DrawTextRect(g, MeasureW(g, full) + 4f * s <= avail ? full : model, fSmall, TextDim,
                new RectangleF(textX, ry, Math.Max(24f * s, avail), rowH), StringAlignment.Near);
            DrawRowNumbers(g, pad, inner, colW, ry, rowH, new string[]
            {
                UsageTracker.FormatTokens(mt.Total), PartEstimate(mt, false), PartEstimate(mt, true), UsageTracker.FormatTokens(mt.CacheRead)
            }, TextDim);
        }

        // Compare view: merge all sessions by model, to see how many tokens each model used and its share; "5h estimate" is the 5-hour quota percentage points it consumed in this period
        // (converted to dollars at official prices, then multiplied by the quota percentage points per dollar; the coefficient is fitted from the quota snapshots by quota_fit.py);
        // "Multiple" = its quota % per million tokens ÷ that of the cheapest of these models (how many times more quota it burns than the cheapest for the same number of tokens).
        // "≈" = this model was never measured, estimated from official prices; "—" = no price for this model
        private int DrawCompareRows(Graphics g, UsageTracker.PeriodSummary cur, float pad, float inner, float s,
            float colW, float nameW, float y, float rowH, int cap)
        {
            LoadQuota();
            Dictionary<string, long> byModel = new Dictionary<string, long>();
            Dictionary<string, long[]> byTok = new Dictionary<string, long[]>();
            long all = 0;
            foreach (UsageTracker.ModelTotal mt in cur.ModelRows)
            {
                string key = BaseModel(mt.Model);
                long sum;
                byModel.TryGetValue(key, out sum);
                byModel[key] = sum + mt.Total;
                long[] tok;
                if (!byTok.TryGetValue(key, out tok))
                {
                    tok = new long[10];
                    byTok[key] = tok;
                }
                for (int i = 0; i < 10; i++)
                {
                    tok[i] += mt.Tok[i];
                }
                all += mt.Total;
            }
            if (byModel.Count == 0)
            {
                DrawTextRect(g, "No model usage in this period", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                return 1;
            }
            List<KeyValuePair<string, long>> items = new List<KeyValuePair<string, long>>(byModel);
            items.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });

            // Quota % of 5 hours per million tokens; the cheapest counts as 1x
            double cheapest = 0;
            bool cheapestApprox = false;
            Dictionary<string, double> pctBy = new Dictionary<string, double>();
            Dictionary<string, bool> apprBy = new Dictionary<string, bool>();
            PerModelPct(cur, pctBy, apprBy);
            Dictionary<string, double> rate = new Dictionary<string, double>();
            Dictionary<string, bool> rateApprox = new Dictionary<string, bool>();
            foreach (KeyValuePair<string, long> kv in items)
            {
                double pct;
                if (kv.Value > 0 && pctBy.TryGetValue(kv.Key, out pct) && pct > 0)
                {
                    bool approx = apprBy[kv.Key];
                    double r = pct / (kv.Value / 1000000.0);
                    rate[kv.Key] = r;
                    rateApprox[kv.Key] = approx;
                    if (cheapest == 0 || r < cheapest)
                    {
                        cheapest = r;
                        cheapestApprox = approx;
                    }
                }
            }

            // Share = the model's quota estimate as a fraction of all models' quota estimates (falls back to the token share only when there is no conversion)
            double sumPct = 0;
            foreach (KeyValuePair<string, double> kv in pctBy)
            {
                sumPct += kv.Value;
            }
            int shown = items.Count <= cap ? items.Count : cap - 1;
            int drawn = 0;
            for (int i = 0; i < shown; i++)
            {
                string model = items[i].Key;
                long tokens = items[i].Value;
                float ry = y + drawn * rowH;
                double myPct;
                bool byQuota = sumPct > 0 && pctBy.TryGetValue(model, out myPct);
                myPct = byQuota ? pctBy[model] : 0;
                skin.DrawShare(g, pad + 6f * s, ry + 2.5f * s, byQuota ? myPct / sumPct : ShareOf(items[i].Value, all), curPal, s);
                float nameX = pad + (skin.ShareWidth + 13f) * s;
                DrawTextRect(g, model.Replace("claude-", ""), fSmall, TextMain, new RectangleF(nameX, ry, nameW - (nameX - pad), rowH), StringAlignment.Near);
                string multiple = "—";
                double rt;
                if (rate.TryGetValue(model, out rt) && cheapest > 0)
                {
                    double m = rt / cheapest;
                    multiple = ((rateApprox[model] || cheapestApprox) && m > 1.0001 ? "≈×" : "×")
                        + (m < 10 ? m.ToString("0.0", CultureInfo.InvariantCulture) : m.ToString("0", CultureInfo.InvariantCulture));
                }
                DrawRowNumbers(g, pad, inner, colW, ry, rowH, new string[]
                {
                    UsageTracker.FormatTokens(tokens), byQuota ? (myPct * 100.0 / sumPct).ToString("0.0", CultureInfo.InvariantCulture) + "%" : Share(tokens, all),
                    pctBy.ContainsKey(model) ? FormatPct(pctBy[model], apprBy[model]) : "—", multiple
                }, TextMain);
                drawn++;
            }
            if (shown < items.Count)
            {
                long rest = 0;
                for (int i = shown; i < items.Count; i++)
                {
                    rest += items[i].Value;
                }
                DrawTextRect(g, "And " + (items.Count - shown) + " more", fSmall, TextDim,
                    new RectangleF(pad + 4f * s, y + drawn * rowH, nameW - 4f * s, rowH), StringAlignment.Near);
                DrawRowNumbers(g, pad, inner, colW, y + drawn * rowH, rowH,
                    new string[] { UsageTracker.FormatTokens(rest), sumPct > 0 ? "" : Share(rest, all), "", "" }, TextDim);
                drawn++;
            }
            return drawn;
        }

        private static string Share(long part, long all)
        {
            return all > 0 ? (part * 100.0 / all).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";
        }

        private static void DescribeReset(Quota q, DateTime now, bool weekly, out string left, out string right)
        {
            right = "";
            if (q == null)
            {
                left = "";
                return;
            }
            if (!q.ResetsAt.HasValue)
            {
                left = weekly ? "Reset time unknown" : "Not started";
                return;
            }
            DateTime t = q.ResetsAt.Value;
            if (t <= now)
            {
                left = "Reset time reached";
                right = "Refreshing...";
                return;
            }
            left = "Resets " + FormatReset(t, now, weekly);
            right = "in " + (weekly ? CountdownLong(t - now) : CountdownHm(t - now));
        }

        private static string FormatReset(DateTime t, DateTime now, bool weekly)
        {
            string hm = t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (weekly)
            {
                return t.ToString("MM-dd", CultureInfo.InvariantCulture) + " " + Weekday[(int)t.DayOfWeek] + " " + hm;
            }
            int days = (int)(t.Date - now.Date).TotalDays;
            if (days == 0)
            {
                return hm;
            }
            if (days == 1)
            {
                return "Tomorrow " + hm;
            }
            return t.ToString("MM-dd", CultureInfo.InvariantCulture) + " " + hm;
        }

        private static string CountdownHm(TimeSpan t)
        {
            if (t.TotalMinutes < 1)
            {
                return "<1 min";
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)t.TotalHours, t.Minutes);
        }

        private static string CountdownLong(TimeSpan t)
        {
            int days = (int)t.TotalDays;
            if (days > 0)
            {
                return days + "d " + t.Hours + "h";
            }
            if (t.TotalHours >= 1)
            {
                return (int)t.TotalHours + "h " + t.Minutes + "m";
            }
            return t.Minutes + "m";
        }

        private static string Describe(FetchResult p)
        {
            switch (p.Problem)
            {
                case Problem.NoBridge: return string.IsNullOrEmpty(p.Detail) ? "No status line data · right-click" : "Waiting for Claude Code (" + p.Detail + ")";
                case Problem.NoLogin: return "No Claude login found";
                case Problem.Expired: return "Token expired · open Claude Code";
                case Problem.Rejected: return "Token rejected · open Claude Code";
                case Problem.Network: return "Network down · will retry";
                case Problem.RateLimited: return "Too many queries · will retry";
                default: return "Service unavailable" + (string.IsNullOrEmpty(p.Detail) ? "" : " (" + p.Detail + ")");
            }
        }

        private static Color LevelColor(double percent)
        {
            if (percent >= 85.0) return Red;
            if (percent >= 60.0) return Amber;
            return Green;
        }

        // Like DrawTextRect, but shrinks the font (down to 70%) before it falls back to an ellipsis
        private void DrawTextFit(Graphics g, string text, Font font, Color color, RectangleF rect, StringAlignment align)
        {
            float need;
            using (StringFormat sf = new StringFormat())
            {
                sf.FormatFlags = StringFormatFlags.NoWrap;
                need = g.MeasureString(text, font, 100000, sf).Width;
            }
            if (need <= rect.Width || rect.Width <= 0f)
            {
                DrawTextRect(g, text, font, color, rect, align);
                return;
            }
            using (Font small = new Font(font.FontFamily, font.Size * Math.Max(0.7f, rect.Width / need * 0.97f), font.Style, font.Unit))
            {
                DrawTextRect(g, text, small, color, rect, align);
            }
        }

        private void DrawTextRect(Graphics g, string text, Font font, Color color, RectangleF rect, StringAlignment align)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = align;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;

                // The more transparent the background, the more a faint black drop shadow is needed so the text does not blur into the desktop; not drawn when the background is nearly solid
                int shadow = (int)Math.Round(255.0 * 0.8 * (1.0 - bgPercent / 100.0));
                if (shadow >= 12)
                {
                    RectangleF r2 = rect;
                    r2.Offset(scale, scale);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(shadow, 0, 0, 0)))
                    {
                        g.DrawString(text, font, sb, r2, sf);
                    }
                }
                using (SolidBrush b = new SolidBrush(color))
                {
                    g.DrawString(text, font, b, rect, sf);
                }
            }
        }

        private static void FillRound(Graphics g, Color color, RectangleF r)
        {
            float d = Math.Min(r.Height, r.Width);
            using (GraphicsPath path = new GraphicsPath())
            using (SolidBrush b = new SolidBrush(color))
            {
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------------------------------------------------------- Interaction

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (resizing)
            {
                DoResize();
                return;
            }
            if (colDrag >= 0)
            {
                DoColumnDrag(e.X);
                return;
            }
            if (showUsage && DividerAt(e.Location) >= 0)
            {
                Cursor = Cursors.SizeWE;
                return;
            }
            int edge = EdgeAt(e.Location);
            if (edge != 0)
            {
                Cursor = EdgeCursor(edge);
            }
            else
            {
                Cursor = (closeRect.Contains(e.Location) || refreshRect.Contains(e.Location) || TabAt(e.Location) >= 0 || SegAt(e.Location) >= 0 || (showUsage && moreRect.Contains(e.Location)) || SessionAt(e.Location) != null || accountRect.Contains(e.Location) || toggleRect.Contains(e.Location) || (!badgeRect.IsEmpty && badgeRect.Contains(e.Location))) ? Cursors.Hand : Cursors.Default;
            }
        }

        // When all sessions are expanded and the window still does not fit, scroll up and down with the wheel
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (showUsage && rowsExpanded)
            {
                rowScroll -= Math.Sign(e.Delta) * 3;
                if (rowScroll < 0)
                {
                    rowScroll = 0;
                }
                Redraw();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            if (closeRect.Contains(e.Location))
            {
                Close();
                return;
            }
            if (refreshRect.Contains(e.Location))
            {
                StartFetch();
                return;
            }
            if (accountRect.Contains(e.Location))
            {
                ShowAccountMenu();
                return;
            }
            if (!badgeRect.IsEmpty && badgeRect.Contains(e.Location))
            {
                OpenSettings();
                return;
            }
            // Click on the toggle bar: switch between simple mode (quota only) and detailed mode (with the usage panel)
            if (toggleRect.Contains(e.Location))
            {
                SetUsageVisible(!showUsage);
                return;
            }
            // Click a time tab at the top of the usage panel to switch the display
            if (showUsage)
            {
                for (int i = 0; i < usageTabs.Length; i++)
                {
                    if (usageTabs[i].Contains(e.Location))
                    {
                        CollapseRows();
                        usagePeriod = i;
                        if (i == TabCustom)
                        {
                            OpenCustomRange();   // Clicking the "Custom" tab: set the range first
                        }
                        Redraw();
                        return;
                    }
                }
            }
            // Dragging a column divider on the card: adjust the column widths (takes priority over clicks on the title and session rows)
            int divider = DividerAt(e.Location);
            if (divider >= 0)
            {
                colDrag = divider;
                colDragX0 = e.X;
                Array.Copy(colFr, colDragStart, 6);
                Capture = true;
                return;
            }
            // Clicking the title in the panel header: cycles through session -> model -> compare
            if (showUsage && SegAt(e.Location) >= 0)
            {
                CollapseRows();
                usageMode = SegAt(e.Location);
                Redraw();
                SaveSettings();
                return;
            }
            // In the model view, clicking the arrow at the start of a session row: expand or collapse the model x tier rows it used (takes priority over "click a session row to open details")
            if (showUsage && usageMode == 1)
            {
                string chev = ChevronAt(e.Location);
                if (chev != null)
                {
                    if (!expandedGroups.Remove(chev))
                    {
                        expandedGroups.Add(chev);
                    }
                    Redraw();
                    return;
                }
            }
            // In the subscription view, clicking an account row: switch the statistics to this account (explains why when the login is bound to a different account)
            if (showUsage && usageMode == 4)
            {
                foreach (KeyValuePair<RectangleF, string> h in accountHits)
                {
                    if (h.Key.Contains(e.Location))
                    {
                        TrySwitchAccount(h.Value);
                        return;
                    }
                }
            }
            // Clicking "N more" / "collapse": expand all sessions, or collapse them
            if (showUsage && moreRect.Contains(e.Location))
            {
                ToggleRows();
                return;
            }
            // Clicking a session row in the usage panel: open its details
            string hitSession = SessionAt(e.Location);
            if (hitSession != null)
            {
                OpenDrill(hitSession);
                return;
            }
            // Pressing on a window edge: drag to resize (the four sides and the four corners)
            int edge = EdgeAt(e.Location);
            if (edge != 0)
            {
                resizing = true;
                resizeEdge = edge;
                resizeCursorStart = System.Windows.Forms.Cursor.Position;
                resizeBoundsStart = Bounds;
                Capture = true;
                return;
            }
            // Press and hold the left button anywhere in the window to drag it
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
            SaveSettings();
        }

        // The usage statistics run on a background thread so that the first read of large history files does not freeze the UI; only one runs at a time
        private void RefreshUsage()
        {
            if (!showUsage || Interlocked.CompareExchange(ref usageBusy, 1, 0) != 0)
            {
                return;
            }
            DateTime? anchor = CycleAnchorUtc();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    UsageTracker.Snapshot snap = usage.Take(anchor);
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        usageSnap = snap;
                        CheckRedraw();
                    }));
                }
                catch (Exception)
                {
                    // The window has been closed, or this round of statistics failed, try again next time
                }
                finally
                {
                    Interlocked.Exchange(ref usageBusy, 0);
                }
            });
        }

        // Redraw only when the text or the usage numbers changed
        private void CheckRedraw()
        {
            string sig = Signature(DateTime.Now);
            // The cache-left time changes every second, so redraw every time while statistics are open
            if (sig != lastSignature || showUsage)
            {
                lastSignature = sig;
                Redraw();
            }
        }

        // End of the current 5-hour cycle: the 5-hour reset time from the quota source (converted to UTC); returns null when unavailable, and the rolling 5 hours are used
        private DateTime? CycleAnchorUtc()
        {
            if (data == null || data.FiveHour == null || !data.FiveHour.ResetsAt.HasValue)
            {
                return null;
            }
            return data.FiveHour.ResetsAt.Value.ToUniversalTime();
        }

        // Window size: follows the content by default; remembered once the user drags an edge (base units). The minimum size guarantees the content fits
        private int MinW() { return showUsage ? 420 : 240; }
        private int MinH() { return showUsage ? CompactH() + 196 : CompactH(); }
        private int CurW() { return Math.Max(MinW(), winWu > 0 ? winWu : ContentW()); }
        private int CurH() { return Math.Max(MinH(), winHu > 0 ? winHu : ContentH()); }

        // Which side of the window the mouse is on: 1 left, 2 right, 4 top, 8 bottom (combinable into the four corners); 0 when not on an edge
        private int EdgeAt(Point p)
        {
            int z = Math.Max(5, (int)Math.Round(6 * scale));
            int e = 0;
            if (p.X <= z) e |= 1;
            if (p.X >= ClientSize.Width - z) e |= 2;
            if (p.Y <= z) e |= 4;
            if (p.Y >= ClientSize.Height - z) e |= 8;
            return e;
        }

        private static System.Windows.Forms.Cursor EdgeCursor(int e)
        {
            if (e == 1 || e == 2) return System.Windows.Forms.Cursors.SizeWE;
            if (e == 4 || e == 8) return System.Windows.Forms.Cursors.SizeNS;
            if (e == 5 || e == 10) return System.Windows.Forms.Cursors.SizeNWSE;   // top-left, bottom-right
            return System.Windows.Forms.Cursors.SizeNESW;                          // top-right, bottom-left
        }

        private int TabAt(Point p)
        {
            if (!showUsage)
            {
                return -1;
            }
            for (int i = 0; i < usageTabs.Length; i++)
            {
                if (usageTabs[i].Contains(p))
                {
                    return i;
                }
            }
            return -1;
        }

        // While dragging an edge, change the window size by the distance the mouse moved, but not below the minimum size
        private void DoResize()
        {
            Point cur = System.Windows.Forms.Cursor.Position;
            int dx = cur.X - resizeCursorStart.X;
            int dy = cur.Y - resizeCursorStart.Y;
            int left = resizeBoundsStart.Left, right = resizeBoundsStart.Right;
            int top = resizeBoundsStart.Top, bottom = resizeBoundsStart.Bottom;
            if ((resizeEdge & 1) != 0) left += dx;
            if ((resizeEdge & 2) != 0) right += dx;
            if ((resizeEdge & 4) != 0) top += dy;
            if ((resizeEdge & 8) != 0) bottom += dy;
            int minW = (int)Math.Round(MinW() * scale);
            int minH = (int)Math.Round(MinH() * scale);
            if (right - left < minW)
            {
                if ((resizeEdge & 1) != 0) left = right - minW;
                else right = left + minW;
            }
            if (bottom - top < minH)
            {
                if ((resizeEdge & 4) != 0) top = bottom - minH;
                else bottom = top + minH;
            }
            Bounds = new Rectangle(left, top, right - left, bottom - top);
            winWu = (int)Math.Round((right - left) / scale);
            winHu = (int)Math.Round((bottom - top) / scale);
            Redraw();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (colDrag >= 0)
            {
                colDrag = -1;
                Capture = false;
                SaveSettings();
            }
            if (resizing)
            {
                resizing = false;
                Capture = false;
                SaveSettings();
            }
        }

        // Tray icon: double-click to show or hide the window; the menu can refresh or exit
        private void BuildTray()
        {
            ContextMenuStrip tm = new ContextMenuStrip();
            tm.Items.Add("Show / hide", null, delegate { ToggleVisible(); });
            tm.Items.Add("Light / dark", null, delegate { ToggleInvert(); });
            tm.Items.Add("Refresh now", null, delegate { StartFetch(); });
            tm.Items.Add("Settings...", null, delegate { OpenSettings(); });
            tm.Items.Add(new ToolStripSeparator());
            tm.Items.Add("Exit", null, delegate { exitRequested = true; Close(); });

            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.ContextMenuStrip = tm;
            tray.DoubleClick += delegate { ToggleVisible(); };
            UpdateTrayText();
            tray.Visible = true;
        }

        private void ToggleVisible()
        {
            if (Visible)
            {
                Hide();
            }
            else
            {
                Show();
                Redraw();
            }
        }

        // Tray tooltip: the percent used of the 5-hour and weekly quotas (a tray tooltip holds at most 63 characters)
        private void UpdateTrayText()
        {
            if (tray == null)
            {
                return;
            }
            string five = data != null && data.FiveHour != null ? Math.Round(data.FiveHour.Percent).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
            string week = data != null && data.SevenDay != null ? Math.Round(data.SevenDay.Percent).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
            Account acct = AccountStore.Current;
            string text = "Stilla " + (acct != null ? acct.Id + "·" + PlanInfo.ShortOf(acct.Plan) + " · " : "") + "5h " + five + " · Weekly " + week;
            if (UpdateState.Available)
            {
                text = "● " + text;
            }
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // The session row under the mouse (in the usage panel); null if none
        private string SessionAt(Point p)
        {
            if (!showUsage)
            {
                return null;
            }
            foreach (KeyValuePair<RectangleF, string> h in sessionHits)
            {
                if (h.Key.Contains(p))
                {
                    return h.Value;
                }
            }
            return null;
        }

        private string ChevronAt(Point p)
        {
            foreach (KeyValuePair<RectangleF, string> h in chevHits)
            {
                if (h.Key.Contains(p))
                {
                    return h.Value;
                }
            }
            return null;
        }

        // Open a session's detail window (the title is the one shown in the panel)
        private void OpenDrill(string session)
        {
            string title = session.Substring(0, Math.Min(6, session.Length));
            foreach (UsageTracker.SessionTotal t in PeriodFor(usagePeriod).Rows)
            {
                if (t.Session == session)
                {
                    title = t.Title ?? (t.Name + " · #" + t.ShortId);
                    break;
                }
            }
            new SessionDrillForm(session, title, logger.DetailPath).Show();
        }

        // The three view buttons in the header: returns the index of the one that was hit (0 session / 1 model / 2 compare), -1 if none
        private int SegAt(Point p)
        {
            if (!showUsage)
            {
                return -1;
            }
            for (int i = 0; i < segRects.Length; i++)
            {
                if (segRects[i].Contains(p))
                {
                    return i;
                }
            }
            return -1;
        }

        // Which column divider the mouse is on (only valid inside the card's column area); -1 if none
        private int DividerAt(Point p)
        {
            if (!showUsage || usageMode == 4 || p.Y < colTop || p.Y > colBottom)
            {
                return -1;
            }
            for (int i = usageMode == 0 ? 0 : 1; i < 5; i++)
            {
                if (Math.Abs(p.X - colEdgeX[i]) <= Math.Max(4f, 4f * scale))
                {
                    return i;
                }
            }
            return -1;
        }

        // Dragging a divider: the two columns beside it give width to each other, neither going below the minimum width
        private void DoColumnDrag(int x)
        {
            int i = colDrag;
            double total = colDragStart[i] + colDragStart[i + 1];
            double a = colDragStart[i] + (x - colDragX0) / (double)colInner;
            if (a < MinColFrac(i))
            {
                a = MinColFrac(i);
            }
            if (total - a < MinColFrac(i + 1))
            {
                a = total - MinColFrac(i + 1);
            }
            colFr[i] = a;
            colFr[i + 1] = total - a;
            Redraw();
        }

        private double MinColFrac(int col)
        {
            float px = col == 0 ? 120f * scale : col == 1 ? 56f * scale : 44f * scale;
            return colInner > 0 ? px / colInner : 0;
        }

        // Tab data of the panel: 5 min, 15 min, 30 min, 1 h, 3 h, this 5 h, previous 5 h come from the live statistics (Snapshot.Periods);
        // today, yesterday, this week, custom come from the log files (logBy)
        private UsageTracker.PeriodSummary PeriodFor(int tab)
        {
            switch (tab)
            {
                case 0:
                    return usageSnap.Periods[4];
                case 1:
                    return usageSnap.Periods[0];
                case 2:
                    return usageSnap.Periods[5];
                case 3:
                    return usageSnap.Periods[3];
                case 4:
                    return usageSnap.Periods[6];
                case 5:
                    return usageSnap.Periods[1];
                case 6:
                    return usageSnap.Periods[2];
                default:
                    return logBy[tab] ?? EmptyPeriod(TabLabels[tab]);
            }
        }

        private static UsageTracker.PeriodSummary EmptyPeriod(string label)
        {
            UsageTracker.PeriodSummary p = new UsageTracker.PeriodSummary();
            p.Label = label;
            p.Range = "Reading...";
            return p;
        }

        // Start and end of the custom range (the last 24 hours if it was never set)
        private void CustomRange(out DateTime from, out DateTime to)
        {
            DateTime now = DateTime.Now;
            if (customFromMin == int.MinValue || customToMin == int.MinValue || customToMin <= customFromMin)
            {
                from = now.AddHours(-24);
                to = now;
                return;
            }
            from = Epoch.AddMinutes(customFromMin);
            to = Epoch.AddMinutes(customToMin);
        }

        // "Time tab settings": tick which time tabs the panel shows (at least one). Takes effect and is saved as soon as you confirm
        private void OpenTabSettings()
        {
            using (TabSettingsForm f = new TabSettingsForm(tabMask, TabLabels))
            {
                if (f.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                tabMask = f.Mask;
                if ((tabMask & (1 << usagePeriod)) == 0)
                {
                    usagePeriod = VisibleTabs()[0];
                }
                logRefreshUtc = DateTime.MinValue;   // Newly shown log-based tabs are recomputed right away
                SaveSettings();
                StartLogRefresh();
                Redraw();
            }
        }

        // Open the "Custom range" dialog; after confirming, recompute with the new range
        private void OpenCustomRange()
        {
            DateTime from, to;
            CustomRange(out from, out to);
            using (CustomRangeForm f = new CustomRangeForm(from, to))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    customFromMin = (int)Math.Round((f.From - Epoch).TotalMinutes);
                    customToMin = (int)Math.Round((f.To - Epoch).TotalMinutes);
                    logRefreshUtc = DateTime.MinValue;
                    SaveSettings();
                    StartLogRefresh();
                }
            }
        }

        // "Today, yesterday, this week, custom" need the log: read it once and compute all four together; computed on a background thread once every 60 seconds, then shown back on the UI thread
        private void StartLogRefresh()
        {
            if (logBusy || DateTime.UtcNow - logRefreshUtc < TimeSpan.FromSeconds(60))
            {
                return;
            }
            logBusy = true;
            logRefreshUtc = DateTime.UtcNow;
            DateTime now = DateTime.Now;
            DateTime today = now.Date;
            DateTime monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // The week starts at 00:00 on Monday
            DateTime cFrom, cTo;
            CustomRange(out cFrom, out cTo);
            DateTime[] froms = { today, today.AddDays(-1), monday, cFrom };
            DateTime[] tos = { now, today, now, cTo };
            string[] labels = { TabLabels[TabToday], TabLabels[TabToday + 1], TabLabels[TabToday + 2], TabLabels[TabCustom] };
            string detail = logger.DetailPath;
            string index = logger.IndexPath;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UsageTracker.PeriodSummary[] result = null;
                try
                {
                    result = LogSummary.ComputeMany(detail, index, froms, tos, labels);
                }
                catch (Exception)
                {
                    // The log does not exist yet or is being written, compute next round
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (result != null)
                        {
                            for (int k = 0; k < result.Length; k++)
                            {
                                logBy[TabToday + k] = result[k];
                            }
                        }
                        logBusy = false;
                        Redraw();
                    }));
                }
                catch (Exception)
                {
                    logBusy = false;
                }
            });
        }

        private int ContentW()
        {
            return showUsage ? UsageW : BaseW;
        }

        private int ContentH()
        {
            return CompactH() + (showUsage ? UsagePanelH : 0);
        }

        // Open or close the usage panel: the window height follows; once closed no session file is read any more
        private void SetUsageVisible(bool on)
        {
            showUsage = on;
            if (usageMenuItem != null)
            {
                usageMenuItem.Checked = on;     // Changed from the toggle bar: the check mark in the right-click menu has to follow
            }
            if (on)
            {
                // When statistics are opened the window is at least the size of the full panel; when closed it goes back to the default size
                winWu = Math.Max(CurW(), UsageW);
                winHu = Math.Max(CurH(), CompactH() + UsagePanelH);
                RefreshUsage();
            }
            else
            {
                winWu = 0;
                winHu = 0;
                rowsExpanded = false;
                rowScroll = 0;
                autoWinHu = 0;
                usageSnap = new UsageTracker.Snapshot();
            }
            ApplyScale();
            KeepOnScreen();
            SaveSettings();
        }

        // If the panel gets taller and goes past the bottom of the screen, nudge it up
        private void KeepOnScreen()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (Bottom > wa.Bottom)
            {
                Top = Math.Max(wa.Top, wa.Bottom - Height);
            }
            if (Right > wa.Right)
            {
                Left = Math.Max(wa.Left, wa.Right - Width);
            }
        }

        private void BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Refresh now", null, delegate { StartFetch(); });
            ToolStripMenuItem srcMenu = new ToolStripMenuItem("Quota data source");
            srcBridgeItem = new ToolStripMenuItem("Status line only (reads no login token, no network)");
            srcBridgeItem.Click += delegate { SetSource(QuotaSource.StatusLine); };
            srcApiItem = new ToolStripMenuItem("Online query (experimental, reads the login token)");
            srcApiItem.Click += delegate { SetSource(QuotaSource.Api); };
            srcAutoItem = new ToolStripMenuItem("Auto (default): status line first, online query when it has no data");
            srcAutoItem.Click += delegate { SetSource(QuotaSource.Auto); };
            srcMenu.DropDownItems.Add(srcBridgeItem);
            srcMenu.DropDownItems.Add(srcApiItem);
            srcMenu.DropDownItems.Add(srcAutoItem);
            srcMenu.DropDownItems.Add(new ToolStripSeparator());
            srcMenu.DropDownItems.Add("Set up the Claude Code status line...", null, delegate { ShowBridgeSetup(); });
            srcMenu.DropDownItems.Add("Undo the status line setup...", null, delegate { RemoveBridgeSetup(); });
            menu.Items.Add(srcMenu);
            UpdateSourceMenu();

            ToolStripMenuItem rootsMenu = new ToolStripMenuItem("Claude data folders");
            rootsMenu.DropDownItems.Add("…");
            rootsMenu.DropDownOpening += delegate
            {
                rootsMenu.DropDownItems.Clear();
                FillRootsMenu(rootsMenu.DropDownItems);
            };
            menu.Items.Add(rootsMenu);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(MakeSlider("Background opacity", 0, bgPercent, delegate(int v) { bgPercent = v; Redraw(); }));
            menu.Items.Add(MakeSlider("Text opacity", FgMin, fgPercent, delegate(int v) { fgPercent = v; Redraw(); }));
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem logItem = new ToolStripMenuItem("Log usage to file");
            logItem.CheckOnClick = true;
            logItem.Checked = logger.Enabled;
            logItem.Click += delegate { logger.Enabled = logItem.Checked; SaveSettings(); };
            menu.Items.Add(logItem);

            ToolStripMenuItem usageItem = usageMenuItem = new ToolStripMenuItem("Show usage statistics");
            usageItem.CheckOnClick = true;
            usageItem.Checked = showUsage;
            usageItem.Click += delegate { SetUsageVisible(usageItem.Checked); };
            menu.Items.Add(usageItem);
            menu.Items.Add("Time tab settings...", null, delegate { OpenTabSettings(); });
            ToolStripMenuItem dsItem = new ToolStripMenuItem("Show DeepSeek estimate row");
            dsItem.CheckOnClick = true;
            dsItem.Checked = showDeepSeek;
            dsItem.Click += delegate { showDeepSeek = dsItem.Checked; SaveSettings(); Redraw(); };
            menu.Items.Add(dsItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem acctMenu = new ToolStripMenuItem("Accounts and subscriptions");
            acctMenu.DropDownItems.Add("…");
            acctMenu.DropDownOpening += delegate
            {
                acctMenu.DropDownItems.Clear();
                FillAccountItems(acctMenu.DropDownItems);
            };
            menu.Items.Add(acctMenu);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem skinMenu = new ToolStripMenuItem("Skin (Ctrl+Alt+N for the next one)");
            skinItems = new ToolStripMenuItem[Skins.Length];
            for (int i = 0; i < Skins.Length; i++)
            {
                int idx = i;
                skinItems[i] = new ToolStripMenuItem(Skins[i].Name);
                skinItems[i].Checked = i == skinIndex;
                skinItems[i].Click += delegate { SetSkin(idx); };
                skinMenu.DropDownItems.Add(skinItems[i]);
            }
            menu.Items.Add(skinMenu);
            menu.Items.Add("Light / dark (Ctrl+Alt+B)", null, delegate { ToggleInvert(); });
            topItem = new ToolStripMenuItem("Always on top");
            topItem.CheckOnClick = true;
            topItem.Checked = TopMost;
            topItem.Click += delegate { TopMost = topItem.Checked; };
            menu.Items.Add(topItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings...", null, delegate { OpenSettings(); });
            menu.Items.Add("Exit", null, delegate { exitRequested = true; Close(); });

            menu.Closed += delegate { SaveSettings(); };
            ContextMenuStrip = menu;
        }

        // ---------------------------------------------------------------- Settings and update notice

        private RectangleF badgeRect = RectangleF.Empty;     // Clickable area of the red dot (empty when there is no new version)
        private SettingsForm settingsForm;

        // The small red dot to the right of the Stilla mark: only drawn when there is a new version
        private void DrawUpdateBadge(Graphics g, float s, float w)
        {
            badgeRect = RectangleF.Empty;
            if (!UpdateState.Available)
            {
                return;
            }
            PointF c = skin.BadgeCenter(g, FontsNow(), s, w, closeRect, refreshRect);
            float r = 4f * s;
            using (SolidBrush ring = new SolidBrush(Bg))
            {
                g.FillEllipse(ring, c.X - r - 1.5f * s, c.Y - r - 1.5f * s, 2f * (r + 1.5f * s), 2f * (r + 1.5f * s));
            }
            using (SolidBrush dot = new SolidBrush(Color.FromArgb(229, 72, 77)))
            {
                g.FillEllipse(dot, c.X - r, c.Y - r, 2f * r, 2f * r);
            }
            badgeRect = new RectangleF(c.X - 9f * s, c.Y - 9f * s, 18f * s, 18f * s);
        }

        private void OpenSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.Activate();
                return;
            }
            settingsForm = new SettingsForm(delegate
            {
                SaveSettings();
                UpdateState.Save();
                UpdateTrayText();
                Redraw();
            });
            settingsForm.FormClosed += delegate { settingsForm = null; };
            settingsForm.Show();
        }

        // Check once at startup (can be turned off in Settings). Wait a few seconds first so it does not compete with startup
        private void StartUpdateCheck(bool manual)
        {
            if (!UpdateState.Auto && !manual)
            {
                return;
            }
            if (!UpdateChecker.Enabled())
            {
                return;
            }
            UpdateChecker.CheckAsync(manual ? 0 : 4000, delegate
            {
                try
                {
                    BeginInvoke(new MethodInvoker(AfterUpdateCheck));
                }
                catch (Exception)
                {
                    // The window has already been closed
                }
            });
        }

        private void AfterUpdateCheck()
        {
            UpdateTrayText();
            Redraw();
            if (UpdateState.Available && UpdateState.Notified != UpdateState.Latest)
            {
                // Show the tray balloon only once per new version
                UpdateState.Notified = UpdateState.Latest;
                UpdateState.Save();
                Balloon("Stilla has a new version " + UpdateState.Latest, "Current " + AppInfo.Version + ". Right-click -> Settings... -> Go to update page; or click the red dot next to the Stilla mark.");
            }
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.RefreshStatus();
            }
        }

        // ---------------------------------------------------------------- Quota data source

        private ToolStripMenuItem srcBridgeItem, srcApiItem, srcAutoItem;

        private void UpdateSourceMenu()
        {
            srcBridgeItem.Checked = QuotaSource.Mode == QuotaSource.StatusLine;
            srcApiItem.Checked = QuotaSource.Mode == QuotaSource.Api;
            srcAutoItem.Checked = QuotaSource.Mode == QuotaSource.Auto;
        }

        // Right-click -> Claude data folders: lists which folders are read now; you can add more by hand (WSL, another drive, someone's unusual location)
        private void FillRootsMenu(ToolStripItemCollection items)
        {
            int n = 0;
            foreach (string r in ClaudeRoots.All())
            {
                string tag = Directory.Exists(Path.Combine(r, "projects")) ? "" : " (no session records yet)";
                ToolStripMenuItem it = new ToolStripMenuItem((n == 0 ? "Main folder  " : "") + r + tag);
                it.Enabled = false;
                items.Add(it);
                n++;
            }
            items.Add(new ToolStripSeparator());
            items.Add("Add folder...", null, delegate { AddRootDialog(); });
            items.Add("Detect again", null, delegate { ClaudeRoots.Refresh(); });
            if (ClaudeRoots.Custom().Count > 0)
            {
                items.Add("Clear folders added by hand", null, delegate { ClaudeRoots.ClearCustom(); });
            }
            items.Add("Open the Stilla data folder", null, delegate { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.Home + "\""); } catch (Exception) { } });
        }

        private void AddRootDialog()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Pick Claude Code's data folder (it contains a projects folder and is usually called .claude; inside WSL it is \\\\wsl$\\<distro>\\home\\<user>\\.claude)";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                string dir = dlg.SelectedPath;
                if (!ClaudeRoots.Looks(dir))
                {
                    if (MessageBox.Show(this, "This folder has no projects, sessions or .credentials.json, so it does not look like Claude Code's data folder. Add it anyway?",
                        "Stilla", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        return;
                    }
                }
                ClaudeRoots.AddCustom(dir);
            }
        }

        private void SetSource(int mode)
        {
            // Already on "Auto" but the online query has not been agreed to: clicking "Auto" again shows the risk notice again
            if (mode == QuotaSource.Mode && !(mode == QuotaSource.Auto && QuotaSource.OnlineConsent != 1))
            {
                return;
            }
            if (mode == QuotaSource.Api || mode == QuotaSource.Auto)
            {
                DialogResult yes = MessageBox.Show(this,
                    "\"Online query\" reads the Claude login token in ~/.claude/.credentials.json on this machine and uses it to call an endpoint that has no public documentation (the same one Claude Code's /usage uses).\r\n\r\n"
                    + "- The token is only used on this machine; it is not sent to anyone else and is not saved.\r\n"
                    + "- The endpoint may change or stop working at any time; Anthropic's terms of service restrict \"using subscription login credentials in tools other than Claude Code\", so please judge for yourself whether this is appropriate.\r\n"
                    + "· The safer way is to use the \"status line\" only (right-click → Quota data source → Set up the Claude Code status line). Note that the VS Code extension and the desktop app may not trigger the status line, in which case the status-line mode may never have data.\r\n\r\n"
                    + "Switch to online query?",
                    "Stilla - online query (experimental)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (yes != DialogResult.Yes)
                {
                    UpdateSourceMenu();
                    return;
                }
            }
            if (mode == QuotaSource.Api || mode == QuotaSource.Auto)
            {
                QuotaSource.OnlineConsent = 1;      // The risk notice above was answered with "Yes"
            }
            QuotaSource.Mode = mode;
            UpdateSourceMenu();
            SaveSettings();
            data = null;
            problem = null;
            StartFetch();
            Redraw();
        }

        private void ShowBridgeSetup()
        {
            if (!File.Exists(StatusLineBridge.CliPath))
            {
                MessageBox.Show(this, "Could not find the status line helper:\r\n" + StatusLineBridge.CliPath + "\r\n\r\nPlease put Stilla.Statusline.exe in the same folder as Stilla.exe.",
                    "Stilla - set up the status line", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string root = ClaudeRoots.Primary();
            string snippet = StatusLineBridge.SettingsSnippet();
            DialogResult r = MessageBox.Show(this,
                "Let Claude Code hand the quota to Stilla (the official statusLine feature, local only, reads no login token).\r\n\r\n"
                + "[Yes] write it automatically into " + ClaudeSettings.PathIn(root) + "\r\n      A backup is made first; a status line you already have is not overwritten and keeps being shown.\r\n"
                + "[No] only copy what has to be added to the clipboard so you can paste it into settings.json yourself.\r\n"
                + "[Cancel] do nothing.\r\n\r\n"
                + "After that restart Claude Code and send a message, and there will be data.\r\n"
                + "Note: only the command-line Claude Code is sure to run the status line; the VS Code extension and the desktop app may not (there is no official promise). "
                + "In that case choose \"Auto\" under \"Quota data source\".",
                "Stilla - set up the status line", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                bool ok;
                string msg = ClaudeSettings.Install(root, StatusLineBridge.CliPath, out ok);
                MessageBox.Show(this, msg + (ok ? "\r\n\r\nTakes effect after Claude Code is restarted." : ""), "Stilla - set up the status line", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            else if (r == DialogResult.No)
            {
                try
                {
                    Clipboard.SetText(snippet);
                    MessageBox.Show(this, "Copied to the clipboard:\r\n\r\n" + snippet + "\r\n\r\nPaste it into " + ClaudeSettings.PathIn(root) + " (if you already have a statusLine, see \"Chaining an existing status line\" in the README).",
                        "Stilla - set up the status line", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception)
                {
                    MessageBox.Show(this, snippet, "Stilla - set up the status line", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void RemoveBridgeSetup()
        {
            bool changed;
            string msg = ClaudeSettings.Uninstall(ClaudeRoots.Primary(), out changed);
            MessageBox.Show(this, msg + (changed ? "\r\n\r\nTakes effect after Claude Code is restarted." : ""), "Stilla - undo the status line setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // A slider with a value label embedded in the menu; the window changes live while dragging and everything is saved when the menu closes
        private ToolStripControlHost MakeSlider(string caption, int min, int value, Action<int> onChange)
        {
            int panelW = (int)Math.Round(250 * scale);
            int panelH = (int)Math.Round(52 * scale);

            Panel panel = new Panel();
            panel.Size = new Size(panelW, panelH);
            panel.BackColor = Color.Transparent;

            Label label = new Label();
            label.AutoSize = false;
            label.Font = new Font("Microsoft YaHei UI", 9f);
            label.Location = new Point((int)Math.Round(8 * scale), (int)Math.Round(4 * scale));
            label.Size = new Size(panelW - (int)Math.Round(16 * scale), (int)Math.Round(20 * scale));
            label.BackColor = Color.Transparent;

            TrackBar bar = new TrackBar();
            bar.Minimum = min;
            bar.Maximum = 100;
            bar.TickStyle = TickStyle.None;
            bar.SmallChange = 1;
            bar.LargeChange = 10;
            bar.Value = Math.Max(min, Math.Min(100, value));
            bar.Location = new Point((int)Math.Round(4 * scale), (int)Math.Round(24 * scale));
            bar.Width = panelW - (int)Math.Round(8 * scale);
            bar.Height = (int)Math.Round(26 * scale);

            label.Text = caption + "  " + bar.Value + "% (further right = more opaque)";
            bar.ValueChanged += delegate
            {
                label.Text = caption + "  " + bar.Value + "% (further right = more opaque)";
                onChange(bar.Value);
            };

            panel.Controls.Add(label);
            panel.Controls.Add(bar);

            ToolStripControlHost host = new ToolStripControlHost(panel);
            host.AutoSize = false;
            host.Size = panel.Size;
            return host;
        }

        // ---------------------------------------------------------------- Remembering position / settings

        private static string SettingsPath()
        {
            return AppPaths.SettingsFile;
        }

        private void LoadSettings()
        {
            try
            {
                foreach (string line in File.ReadAllLines(SettingsPath()))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string k = line.Substring(0, eq).Trim();
                    int v;
                    if (!int.TryParse(line.Substring(eq + 1).Trim(), out v))
                    {
                        continue;
                    }
                    if (k == "x") savedX = v;
                    else if (k == "y") savedY = v;
                    else if (k == "bg" && v >= 0 && v <= 100) bgPercent = v;
                    else if (k == "fg" && v >= FgMin && v <= 100) fgPercent = v;
                    else if (k == "top") TopMost = v != 0;
                    else if (k == "usage") showUsage = v != 0;
                    else if (k == "w" && v > 0) winWu = v;
                    else if (k == "h2" && v > 0) winHu = v;     // The layout changed in v16; the h saved by older versions is no longer used (otherwise the table would be squeezed into one row)
                    else if (k == "log") logger.Enabled = v != 0;
                    else if (k == "src" && v >= 0 && v <= 2) QuotaSource.Mode = v;
                    else if (k == "onl" && v >= 0 && v <= 2) QuotaSource.OnlineConsent = v;
                    else if (k == "ds") showDeepSeek = v != 0;
                    else if (k == "upd") UpdateState.Auto = v != 0;
                    else if (k == "tabs" && v > 0 && v < (1 << TabCount)) tabMask = v;
                    else if (k == "mode" && v >= 0 && v <= 4) usageMode = v;
                    else if (k == "skin" && v >= 0 && v < Skins.Length)
                    {
                        skinIndex = v;
                        skin = Skins[v];
                        ApplyPalette(skin, inverted);
                    }
                    else if (k == "dark")
                    {
                        inverted = v != 0;
                        ApplyPalette(skin, inverted);
                    }
                    else if (k == "modelview") usageMode = v != 0 ? 1 : 0;     // A setting from old versions: there were only session and model
                    else if (k == "cfrom") customFromMin = v;
                    else if (k == "cto") customToMin = v;
                    else if (k.Length == 2 && k[0] == 'c' && k[1] >= '0' && k[1] <= '5' && v > 0) colFr[k[1] - '0'] = v / 1000.0;
                }
            }
            catch (Exception)
            {
            }
        }

        private void SaveSettings()
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new string[]
                {
                    "x=" + Left,
                    "y=" + Top,
                    "bg=" + bgPercent,
                    "fg=" + fgPercent,
                    "top=" + (TopMost ? 1 : 0),
                    "usage=" + (showUsage ? 1 : 0),
                    "w=" + winWu,
                    "h2=" + (rowsExpanded && autoWinHu > 0 && winHu == autoWinHu ? savedWinHu : winHu),
                    "log=" + (logger.Enabled ? 1 : 0),
                    "src=" + QuotaSource.Mode,
                    "onl=" + QuotaSource.OnlineConsent,
                    "ds=" + (showDeepSeek ? 1 : 0),
                    "upd=" + (UpdateState.Auto ? 1 : 0),
                    "mode=" + usageMode,
                    "dark=" + (inverted ? 1 : 0),
                    "skin=" + skinIndex,
                    "tabs=" + tabMask,
                    "c0=" + (int)Math.Round(colFr[0] * 1000),
                    "c1=" + (int)Math.Round(colFr[1] * 1000),
                    "c2=" + (int)Math.Round(colFr[2] * 1000),
                    "c3=" + (int)Math.Round(colFr[3] * 1000),
                    "c4=" + (int)Math.Round(colFr[4] * 1000),
                    "c5=" + (int)Math.Round(colFr[5] * 1000),
                    "cfrom=" + customFromMin,
                    "cto=" + customToMin
                });
            }
            catch (Exception)
            {
            }
        }

        private void PlaceInitial()
        {
            if (savedX != int.MinValue && savedY != int.MinValue)
            {
                Rectangle want = new Rectangle(savedX, savedY, Width, Height);
                foreach (Screen sc in Screen.AllScreens)
                {
                    if (sc.WorkingArea.IntersectsWith(want))
                    {
                        Location = want.Location;
                        return;
                    }
                }
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int margin = (int)Math.Round(20 * scale);
            Location = new Point(wa.Right - Width - margin, wa.Top + margin);
        }
    }

    // Drawing of the ink-drop style: ink-drop icon, liquid cups, ink-drop double circle. All drawn directly with GDI+, no image files are read
    internal static class InkPainter
    {
        private static Bitmap diagramBmp;       // Cache of the ink-drop double circle: pixels are not recomputed when the data did not change
        private static string diagramKey = "";

        // Convert usage to the height of ink in a cup (logarithmic): about 10 million tokens is almost an empty cup, about 2 billion tokens is a full cup
        public static double LogFill(long tokens)
        {
            if (tokens <= 0)
            {
                return 0.06;
            }
            double f = (Math.Log10(tokens) - 7.4) / 1.9;
            return Math.Max(0.06, Math.Min(1.0, f));
        }

        private static double Pct01(double p)
        {
            return Math.Max(0.0, Math.Min(100.0, p)) / 100.0;
        }

        // Ink-drop icon: one large and one small ellipse
        public static void DrawLogo(Graphics g, float x, float y, float s, Color ink)
        {
            using (SolidBrush b = new SolidBrush(ink))
            {
                g.FillEllipse(b, x + 0.5f * s, y + 1f * s, 13f * s, 12f * s);
                g.FillEllipse(b, x + 12f * s, y + 2f * s, 9f * s, 8f * s);
            }
        }

        // Liquid cup: a paper-colored circle with part of the bottom in ink, the ink height = fill (0-1); a thin ring is added around it when selected
        public static void DrawCup(Graphics g, RectangleF r, double fill, bool selected, float s, Color ink, Color paper)
        {
            if (selected)
            {
                RectangleF o = RectangleF.Inflate(r, 3f * s, 3f * s);
                using (Pen p = new Pen(ink, 1f * s))
                {
                    g.DrawEllipse(p, o);
                }
            }
            using (SolidBrush pb = new SolidBrush(paper))
            {
                g.FillEllipse(pb, r);
            }
            float f = (float)Math.Max(0.0, Math.Min(1.0, fill));
            if (f > 0f)
            {
                GraphicsState st = g.Save();
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddEllipse(r);
                    g.SetClip(path);
                }
                float top = r.Bottom - r.Height * f;
                using (SolidBrush ib = new SolidBrush(ink))
                {
                    g.FillRectangle(ib, r.X, top, r.Width, r.Bottom - top);
                }
                g.Restore(st);
            }
            using (Pen p = new Pen(ink, 1f * s))
            {
                g.DrawEllipse(p, r);
            }
        }

        // Ink-drop double circle: two rings stand for 5 hours and weekly (ring color follows usage); the ink area inside a circle = the used fraction, and the two drops stick together when they get close.
        // The edge of a drop is decided by a "field": the closer to the center the darker, with a transition of about 1 pixel at the boundary (anti-aliasing).
        public static void DrawDiagram(Graphics g, RectangleF box, double p5, double pw, float s, Color ink, Color paper, Color soft,
            Color ring5, Color ringW, Font numFont, Font tagFont, string t5, string tw)
        {
            int W = Math.Max(2, (int)Math.Ceiling(box.Width));
            int H = Math.Max(2, (int)Math.Ceiling(box.Height));
            float cy = box.Height / 2f;                              // Local coordinates (relative to the top-left of box)
            float R = Math.Min(box.Height * 0.40f, box.Width * 0.20f);   // Ring radius
            float ax = box.Width * 0.33f, bx = box.Width * 0.67f;
            float ra = R * (float)Math.Sqrt(Pct01(p5));
            float rb = R * (float)Math.Sqrt(Pct01(pw));

            using (SolidBrush sb = new SolidBrush(soft))
            {
                g.FillRectangle(sb, box);
            }

            // Axes and ticks
            using (Pen axis = new Pen(Color.FromArgb(70, ink), 1f * s))
            {
                g.DrawLine(axis, box.X, box.Y + cy, box.Right, box.Y + cy);
                g.DrawLine(axis, box.X + box.Width / 2f, box.Y, box.X + box.Width / 2f, box.Bottom);
                for (int k = 0; k <= 8; k++)
                {
                    float x = box.X + box.Width * k / 8f;
                    g.DrawLine(axis, x, box.Y + cy - 3f * s, x, box.Y + cy + 3f * s);
                }
            }

            // Outer ring: reference circle of the full quota
            using (Pen pa = new Pen(ring5, 1.2f * s))
            {
                g.DrawEllipse(pa, box.X + ax - R, box.Y + cy - R, 2f * R, 2f * R);
            }
            using (Pen pb = new Pen(ringW, 1.2f * s))
            {
                g.DrawEllipse(pb, box.X + bx - R, box.Y + cy - R, 2f * R, 2f * R);
            }

            // Ink drop: use the cached bitmap when the data did not change
            string key = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F2},{3:F2},{4:F2},{5:F2},{6:F2},{7}",
                W, H, ra, rb, ax, bx, cy, ink.ToArgb());
            if (diagramBmp == null || key != diagramKey)
            {
                if (diagramBmp != null)
                {
                    diagramBmp.Dispose();
                }
                diagramKey = key;
                diagramBmp = BuildInk(W, H, ax, cy, ra, bx, cy, rb, ink);
            }
            g.DrawImage(diagramBmp, box.X, box.Y, W, H);

            // The number is written in the middle of the drop: paper-colored text when the drop is big enough, otherwise ink-colored
            DrawCenter(g, t5, numFont, ra >= 13f * s ? paper : ink, box.X + ax, box.Y + cy);
            DrawCenter(g, tw, numFont, rb >= 13f * s ? paper : ink, box.X + bx, box.Y + cy);

            // Label boxes: 5 hours at the top left, weekly at the bottom right; leader lines point to the matching circle
            RectangleF tag5 = DrawTag(g, "5 h", tagFont, ink, paper, box.X + 3f * s, box.Y + 3f * s, s);
            float wWidth = g.MeasureString("Weekly", tagFont).Width + 8f * s;
            RectangleF tagW = DrawTag(g, "Weekly", tagFont, ink, paper, box.Right - 3f * s - wWidth, box.Bottom - 3f * s - tag5.Height, s);
            using (Pen lp = new Pen(Color.FromArgb(150, ink), 1f * s))
            {
                float d = R * 0.7071f;
                g.DrawLine(lp, tag5.Right, tag5.Bottom, box.X + ax - d, box.Y + cy - d);
                g.DrawLine(lp, tagW.Left, tagW.Top, box.X + bx + d, box.Y + cy + d);
            }
        }

        // Compute the drop field into a bitmap with alpha (ARGB), then copy it in one go
        private static Bitmap BuildInk(int W, int H, float ax, float ay, float ra, float bx, float by, float rb, Color ink)
        {
            int rgb = (ink.R << 16) | (ink.G << 8) | ink.B;
            int[] px = new int[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float sx = x + 0.5f, sy = y + 0.5f;
                    float f = 0f;
                    if (ra > 0f)
                    {
                        float dx = sx - ax, dy = sy - ay;
                        f += ra * ra / (dx * dx + dy * dy + 0.01f);
                    }
                    if (rb > 0f)
                    {
                        float dx = sx - bx, dy = sy - by;
                        f += rb * rb / (dx * dx + dy * dy + 0.01f);
                    }
                    float a = (f - 0.98f) / 0.04f;
                    if (a <= 0f)
                    {
                        continue;
                    }
                    if (a > 1f)
                    {
                        a = 1f;
                    }
                    px[y * W + x] = ((int)(a * 255f) << 24) | rgb;
                }
            }
            Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            bmp.UnlockBits(bd);
            return bmp;
        }

        private static void DrawCenter(Graphics g, string text, Font font, Color color, float cx, float cy)
        {
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(color))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, b, new RectangleF(cx - 40f, cy - 16f, 80f, 32f), sf);
            }
        }

        // Label box: paper-colored fill, thin ink-colored border, ink-colored text
        private static RectangleF DrawTag(Graphics g, string text, Font font, Color ink, Color paper, float x, float y, float s)
        {
            SizeF sz = g.MeasureString(text, font);
            RectangleF r = new RectangleF(x, y, sz.Width + 8f * s, sz.Height + 2f * s);
            using (SolidBrush pb = new SolidBrush(paper))
            {
                g.FillRectangle(pb, r);
            }
            using (Pen p = new Pen(ink, 1f * s))
            {
                g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }
            using (SolidBrush tb = new SolidBrush(ink))
            {
                g.DrawString(text, font, tb, r.X + 4f * s, r.Y + 1f * s);
            }
            return r;
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            foreach (string a in args)
            {
                if (a == "--demo")
                {
                    // Demo mode: fake quota, settings go to a temp folder, the real settings are not touched (for screenshots / tests)
                    Environment.SetEnvironmentVariable("STILLA_DEMO", "1");
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STILLA_HOME")))
                    {
                        Environment.SetEnvironmentVariable("STILLA_HOME", Path.Combine(Path.GetTempPath(), "Stilla-demo"));
                    }
                }
            }
            bool created;
            // The mutex name includes the data folder: different data folders (portable / demo) can run at the same time
            string mutexName = "Local\\Stilla_" + (AppPaths.Home.ToLowerInvariant().GetHashCode() & 0x7fffffff).ToString("x");
            using (Mutex single = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    return; // One is already running
                }

                EnableDpiAwareness();
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Widget());
            }
        }

        private static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) // PER_MONITOR_AWARE_V2
                {
                    return;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                SetProcessDPIAware();
            }
            catch (Exception)
            {
            }
        }
    }
}
