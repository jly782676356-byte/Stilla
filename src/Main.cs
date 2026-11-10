// Stilla —— 一个给 Claude Code 用的额度悬浮窗（独立的社区项目，不是 Anthropic 出品）
// 行为：
//   1) 在屏幕上放一个小的、始终置顶的悬浮窗（可拖动，位置会记住；背景透明度和文字透明度可分别用滑块调）
//   2) 显示「5 小时额度」「每周额度」的已用百分比，以及各自的下次重置时间和倒计时
//   3) 额度数据优先来自 Claude Code 官方的状态栏（statusLine）功能，见 Source.cs：不读登录令牌、不联网、不消耗任何 token；状态栏没有数据时，默认的「自动」来源要经用户同意才会改用在线查询。
//      可选的「在线查询」模式（实验性，默认关闭）才会读本机登录令牌并请求一个未公开的接口，见下面的 UsageClient。
//   4) 可选：右键开启「显示用量统计」，列出各时间段内各会话/子任务消耗的 token；
//      只读本机会话记录里的用量数字和时间戳，不读对话内容
//
// 在线查询模式的注意事项：只读取 ~/.claude/.credentials.json 里的访问令牌，从不刷新、从不改写它，
//       避免把 Claude Code 的登录搞掉线。令牌过期时由 Claude Code 自己续期，这里只负责重新读取。

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
        public double Percent;        // 已用百分比 0~100
        public DateTime? ResetsAt;    // 下次重置时间（本地时间），可能为空
    }

    internal enum Problem { None, NoLogin, Expired, Rejected, Network, RateLimited, Server, NoBridge }

    internal sealed class FetchResult
    {
        public Quota FiveHour;
        public Quota SevenDay;
        public DateTime FetchedAt;
        public string Via = "";     // 数据从哪来：bridge = 状态栏，api = 在线查询，demo = 演示
        public Problem Problem;
        public string Detail;
        public int RetrySeconds = 60;
    }

    // 读取本机 Claude Code 的登录令牌，并调用只读的额度查询接口
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
                // 令牌已过期：不自己刷新，等 Claude Code 续期后下次重新读文件
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
                r.Detail = "返回格式异常";
                return r;
            }

            r.FiveHour = ReadQuota(root, "five_hour");
            r.SevenDay = ReadQuota(root, "seven_day");
            if (r.FiveHour == null && r.SevenDay == null)
            {
                r.Problem = Problem.Server;
                r.Detail = "没有额度数据";
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
                    // 接口给的时间会在整分前后抖动零点几秒（10:49:59.9 / 10:50:00.3），四舍五入到分钟
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

        // Claude Code 可能正好在写这个文件，所以用共享方式打开并重试几次
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

    // VS Code 里 Claude 扩展显示的会话标题（由 scripts\sync_vscode_titles.py 导出到 logs\vscode标题.csv）。优先级最高
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
            string path = Path.Combine(AppPaths.DataDir, "vscode标题.csv");
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
                        if (id != "会话ID" && title.Length > 0)
                        {
                            fresh[id] = title;
                        }
                    }
                }
                catch (IOException)
                {
                    loadedUtc = DateTime.MinValue;   // 文件正在被写入，下一轮再读
                }
            }
            titles = fresh;
        }
    }

    // Claude Code 会话名称（和应用里显示的一致）：~/.claude/sessions/<pid>.json 里的 name。
    // nameSource = user 是你改过的名字，derived 是自动生成的名字。每 30 秒重读一次。
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
                        // nameSource：user = 你改的名字，auto = AI 起的标题，derived = 用项目名加随机字符凑的占位名
                        // （项目名-77、0-35 这种）。占位名不是标题，跳过，让 AI 标题 / VS Code 标题顶上
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
                        // 文件正在被写入或格式不对，跳过
                    }
                }
            }
            names = fresh;
        }
    }

    // 统计 Claude Code 本地会话记录里的 token 用量，实时汇总这几个时间段（Snapshot.Periods 的下标见 Snapshot）：
    //   · 最近 5 分钟、15 分钟、30 分钟、1 小时、3 小时
    //   · 本 5 小时周期：以额度接口给出的 5 小时重置时间为终点往前推 5 小时；拿不到时按最近 5 小时滚动
    //   · 上一个 5 小时周期
    // 只读用量数字和时间戳，不读对话内容；不调用任何接口，不消耗 token。
    // 文件没有变化就不重读，有新增时只读新增的那一小段。
    internal sealed class UsageTracker
    {
        private const int ShortMinutes = 15;
        private const int CycleHours = 5;
        private const int TrackHours = 10;                  // 要覆盖「上一个 5 小时」，所以往回跟踪 10 小时
        private const int ScanSeconds = 10;                 // 每 10 秒扫描一次目录，找新出现的文件
        private const long TailBytes = 8L * 1024 * 1024;    // 第一次读某个文件时，只从末尾往前读 8MB
        private const int ActiveSeconds = 60;               // 最近 60 秒内有写入的会话标为「活跃」

        public sealed class Rec
        {
            public DateTime Utc;
            public string Session;
            public string Agent;    // 子任务的文件路径；主会话为 null
            public long In, Out, CacheWrite, CacheRead;
            public long CacheWrite1h;   // CacheWrite 里属于 1 小时缓存的部分（其余是 5 分钟缓存）
            public string Id;           // 消息 ID（查档位标注用）
            public string Effort = "";  // 思考强度：low / medium / high / xhigh / max（记录里没有就是空）
            public bool Fast;           // 快速模式
            public long Think;          // 输出里的思考 token
            public int TtlMinutes;
            public string Model;    // 模型名（原样，可能带日期后缀）  // 这条消息写入缓存的有效期（5 分钟或 1 小时）；没有写入缓存时为 0
        }

        public sealed class SessionTotal
        {
            public string Session;
            public string Name;
            public string Title;    // 会话标题；没有就为 null
            public long Total;      // 四项之和
            public long Input, Output, CacheWrite, CacheRead;
            public int SubCount;
            public bool Active;
            public DateTime? CacheExpiryUtc;    // 主会话缓存的到期时间（UTC）；没有缓存记录就是 null
            public DateTime LastUtc;            // 这个时间段里最后一次活动（UTC），排序用

            public string ShortId
            {
                get { return Session.Length > 6 ? Session.Substring(0, 6) : Session; }
            }
        }

        // 会话 × 模型 的 token 合计（模型视图用）
        public sealed class ModelTotal
        {
            public string Session;
            public string Model;
            public string Tier = "";                            // 档位：思考强度 low / medium / high / xhigh / max，开着 Ultracode 的是 ultra，快速模式加 +fast，没标注的是「未标注」
            public long Msgs;                                   // API 消息条数
            public DateTime FirstUtc = DateTime.MaxValue;       // 这个 会话 × 模型 × 档位 第一次和最后一次出现（UTC）：按它排先后，看中途什么时候切换的
            public DateTime LastUtc = DateTime.MinValue;
            public long Total;
            public long CacheRead;
            // 折算额度用：[0..4] = 普通上下文的 输入、输出、5 分钟缓存写入、1 小时缓存写入、缓存读取；[5..9] = 同样五项，但那一次请求的上下文超过 LongContextTokens
            public readonly long[] Tok = new long[10];
        }

        public const long LongContextTokens = 100000;   // 一次请求的上下文（输入 + 缓存写入 + 缓存读取）超过它，有的模型（haiku）整条按加价档计费

        public static void AddTokens(long[] tok, long inp, long outp, long cw5, long cw1, long cr)
        {
            int o = (inp + cw5 + cw1 + cr) > LongContextTokens ? 5 : 0;
            tok[o] += inp;
            tok[o + 1] += outp;
            tok[o + 2] += cw5;
            tok[o + 3] += cw1;
            tok[o + 4] += cr;
        }

        // 这一条记录归到哪个「模型 × 档位」
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

        // 一个会话现在的状态（不分时间段）：最近一次活动、是不是正在写入、缓存什么时候过期。表格排序用
        public sealed class LiveInfo
        {
            public DateTime LastUtc;
            public bool Active;
            public DateTime? ExpiryUtc;
        }

        // 模型 × 档位 的合计（档位视图用）。Tier = 思考强度（low / medium / high / xhigh / max），开着 Ultracode 的是 ultra，快速模式后面加 +fast
        public sealed class TierTotal
        {
            public string Model;
            public string Tier;
            public long Msgs;       // 条数
            public long Total;      // 四项 token 之和
            public long Out;        // 输出（含思考）
            public long Think;      // 其中的思考 token
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

        // Periods[0] = 15 分钟，[1] = 本 5 小时周期，[2] = 上一个 5 小时周期，[3] = 1 小时，[4] = 5 分钟，[5] = 30 分钟，[6] = 3 小时
        public sealed class Snapshot
        {
            public readonly PeriodSummary[] Periods = new PeriodSummary[7];
            public string Signature = "";
            public readonly Dictionary<string, LiveInfo> Live = new Dictionary<string, LiveInfo>();   // 会话ID → 实时状态

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
            public long Offset = -1;    // -1 = 还没读过
            public bool Align;          // 从文件中间开始读时，先丢掉残缺的第一行
            public string Title;        // Claude Code 自动生成的会话标题（ai-title），取最后一条
            public string CustomTitle;  // 你在应用里改过的会话名（custom-title），取最后一条
            public DateTime Mtime;
        }

        private readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Rec> recs = new Dictionary<string, Rec>();   // 以消息 ID 为键：同一条消息只计一次
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

        // 每次刷新时调用（在后台线程里，同一时间只会有一个调用）。
        // anchorUtc 是本周期的结束时间（额度接口的 5 小时重置时间，UTC），拿不到时传 null
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

        // 找出最近 10 小时内有写入的 .jsonl：主会话，以及 subagents 下的子任务
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
            // 窗口外的文件不再跟踪
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

        // 读取文件新增的那一部分。返回 false 表示文件已经不在跟踪范围内
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
                return true;    // 暂时打不开，下次再试
            }
            if (fs.Offset < 0)
            {
                // 第一次读：只看末尾一段，窗口外的行会被时间过滤掉
                fs.Offset = Math.Max(0, len - TailBytes);
                fs.Align = fs.Offset > 0;
            }
            if (len < fs.Offset)
            {
                // 文件变小了（被替换或截断），从头来
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
                return true;    // 文件正在被写入或暂时打不开，下次再试
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            // 只处理到最后一个换行为止，末尾不完整的行留到下次
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
            // 本周期的结束：有效的重置时间就用它，否则按现在往前滚动 5 小时
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
                // 项目名优先用主会话的工作目录
                if (fs.Cwd != null && (!fs.Sub || !cwdBy.ContainsKey(fs.Session)))
                {
                    cwdBy[fs.Session] = fs.Cwd;
                }
                if (now - fs.Mtime < TimeSpan.FromSeconds(ActiveSeconds))
                {
                    active.Add(fs.Session);
                }
            }

            // 会话名优先级：你改过的名字 > Claude Code 会话文件里的名字 > 自动摘要（ai-title）
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
            // VS Code 里显示的标题优先级最高（和你在 VS Code 里看到的一致）
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
            shortP.Label = "15 分钟";
            shortP.Range = Clock(shortStart) + "–" + Clock(now);
            PeriodSummary cur = snap.Periods[1];
            cur.Label = rolling ? "近 5 小时" : "本 5 小时";
            cur.Range = Clock(curStart) + "–" + Clock(end);
            PeriodSummary prev = snap.Periods[2];
            prev.Label = rolling ? "前 5 小时" : "上 5 小时";
            prev.Range = Clock(prevStart) + "–" + Clock(curStart);

            FillPeriod(shortP, shortStart, DateTime.MaxValue, cwdBy, titleBy, active);
            FillPeriod(cur, curStart, DateTime.MaxValue, cwdBy, titleBy, active);
            FillPeriod(prev, prevStart, curStart, cwdBy, titleBy, active);

            // 每个会话的缓存到期时间：最近一次读写缓存的时间 + 那次写入的有效期（只看主会话；没有有效期记录时按 5 分钟）
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

            // 1 小时（实时统计，只在最近 10 小时的记录里算）
            PeriodSummary hourP = snap.Periods[3];
            hourP.Label = "1小时";
            hourP.Range = Clock(now.AddHours(-1)) + "–" + Clock(now);
            FillPeriod(hourP, now.AddHours(-1), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(hourP, expiryBy);

            // 5 分钟、30 分钟、3 小时（和 1 小时一样是实时统计）
            PeriodSummary fiveP = snap.Periods[4];
            fiveP.Label = "5分钟";
            fiveP.Range = Clock(now.AddMinutes(-5)) + "–" + Clock(now);
            FillPeriod(fiveP, now.AddMinutes(-5), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(fiveP, expiryBy);

            PeriodSummary halfP = snap.Periods[5];
            halfP.Label = "30分钟";
            halfP.Range = Clock(now.AddMinutes(-30)) + "–" + Clock(now);
            FillPeriod(halfP, now.AddMinutes(-30), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(halfP, expiryBy);

            PeriodSummary threeP = snap.Periods[6];
            threeP.Label = "3小时";
            threeP.Range = Clock(now.AddHours(-3)) + "–" + Clock(now);
            FillPeriod(threeP, now.AddHours(-3), DateTime.MaxValue, cwdBy, titleBy, active);
            SetExpiry(threeP, expiryBy);

            // 每个会话最近一次活动（任何消息，包括子任务）、是否正在写入、缓存到期时间：面板排序用
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

            // 签名：数字和时间范围都没变就不重绘
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

        // 汇总 [from, to) 时间段内的记录，按会话分组
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
                return "会话";
            }
            string n = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(n) ? cwd : n;
        }

        // UTC 时间转成本地的 HH:mm
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

    // 读取超过 260 个字符的长路径。.NET Framework 的 DirectoryInfo.GetFiles 会静默跳过这些文件，
    // 所以这里直接用 Win32 接口枚举（FindFirstFileW），并用 \\?\ 前缀打开文件。
    internal static class LongPath
    {
        internal sealed class Entry
        {
            public string Path;         // 不带 \\?\ 前缀的完整路径
            public long Size;
            public DateTime MtimeUtc;
            public string Session;      // 主会话：文件名；子任务：<会话ID>\subagents\… 里的会话ID
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

        // 所有 Claude 数据目录（命令行 / VS Code / 桌面端 / 手动加的）下的会话记录
        public static List<Entry> ListAll()
        {
            List<Entry> res = new List<Entry>();
            foreach (string dir in ClaudeRoots.ProjectDirs())
            {
                res.AddRange(ListJsonl(dir));
            }
            return res;
        }

        // 列出 root 下所有 .jsonl 文件（递归）
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

        // 打开文件（可以是长路径）；调用方负责关闭
        public static FileStream Open(string path)
        {
            SafeFileHandle h = CreateFileW(Prefixed(path), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
            if (h.IsInvalid)
            {
                throw new IOException("无法打开文件：" + path);
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

    // 用量日志（v2）：
    //   用量明细_v2.csv —— 每条 API 消息一行（按消息 ID 去重），含任务ID（这次提问的 promptId）、工具次数、工具名
    //   会话索引.csv    —— 每个会话一行：标题、开始/最后时间、消息数、各类 token、主会话文件路径，方便之后找回记录
    //   额度快照.csv    —— 5 小时和每周的已用百分比快照
    // 只读取 Claude Code 的会话记录（共享方式打开，不阻止它继续写入，也不改动原文件）。
    // 旧版的「用量明细.csv」「读取进度.txt」保持原样，不会被删除或覆盖。
    // 启动约 30 秒后开始，之后每 60 秒只读上次之后新增的内容。
    internal sealed class UsageLogger
    {
        private const int FirstDelaySeconds = 30;
        private const int ScanSeconds = 60;
        private const string DetailHeader = "时间,会话ID,项目,模型,输入,输出,缓存写入5分钟,缓存写入1小时,缓存读取,子任务,消息ID,任务ID,工具次数,工具名";
        private const string IndexHeader = "会话ID,项目,标题,开始时间,最后时间,消息数,子任务消息数,输入,输出,缓存写入,缓存读取,主会话文件";
        private const string QuotaHeader = "时间,5小时已用%,5小时重置时间,每周已用%,每周重置时间";
        private const string TagHeader = "消息ID,档位,Ultra,速度,思考token,入口";
        private const int TagBudgetSeconds = 20;    // 补历史标注时，一轮最多读这么久，剩下的留给下一轮（不一次性读完所有会话记录）
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
        // 会话 → Ultracode 开着的时间段 [开始, 结束]（UTC；结束 = MaxValue 表示现在还开着）。子任务按它所属的主会话算
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
            detailPath = Path.Combine(logDir, "用量明细_v2.csv");
            indexPath = Path.Combine(logDir, "会话索引.csv");
            quotaPath = Path.Combine(logDir, "额度快照.csv");
            progressPath = Path.Combine(logDir, "读取进度_v2.txt");
            tagPath = Path.Combine(logDir, "用量标注.csv");
            tagProgressPath = Path.Combine(logDir, "读取进度_标注.txt");
            ultraPath = Path.Combine(logDir, "ultra时段.csv");
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

        private const int ThreadModeBackgroundBegin = 0x00010000;   // 线程进入后台模式：CPU、磁盘读写、内存优先级都降到最低
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
                // 补全历史时读的文件很多，压低优先级，尽量不抢其他程序的 CPU 和磁盘（后台模式把磁盘优先级也降到最低）
                background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
                if (!background)
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                }
                ScanOnce();
            }
            catch (Exception)
            {
                // 这一轮出错就等下一轮（状态没有保存的部分会在下一轮重新处理）
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

        // 一轮扫描：找出有新内容的会话文件，只读新增的部分，追加到明细，并更新会话索引
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
            // 主会话先读：子任务的 Ultracode 状态要看它所属主会话的开关时间
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
                        off = 0;    // 文件被替换或截断：从头读，已记录的消息靠消息 ID 去重
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
                        // 补历史标注读得够久了：这个文件只照常处理新增的明细，标注留给下一轮
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

        // 从 off 开始逐行读，只处理到最后一个换行为止；返回处理到的位置
        // fromPos：开始读的位置（补标注时比明细的读取位置 mainOff 靠前，这一段明细靠消息 ID 去重）；tw = null 表示这一轮不写标注
        private long ReadLines(LongPath.Entry e, long fromPos, long mainOff, StreamWriter w, StreamWriter tw)
        {
            long off = fromPos;
            string prompt;
            if (fromPos < mainOff || !prompts.TryGetValue(e.Path, out prompt))
            {
                prompt = "";    // 从更早的位置重放：当前提问从头重新推出来
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

        // 一行：用户消息更新「当前提问」；ai-title 更新会话标题；带 usage 的回复写入明细
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
                // 档位标注：思考强度（这一轮实际用的）、Ultracode 开着没有、快速模式、思考 token、入口
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

        // Ultracode 的开关：记录里是一条 attachment，attachment.type = ultra_effort_enter（开启）；
        // 关闭时的类型名还没见过，凡是带 exit / leave / off / end / disable 的都当作关闭
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
                    return;     // 已经开着，或者这个开启事件早就记过了（重放）
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

        // 这条回复里的工具调用：次数，以及用到的工具名（按第一次出现的顺序，去重）
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

        // 会话索引：按最后时间从新到旧排序，整个文件重写（只有几百行）
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

        // 额度快照：数值或重置时间变了就记一行；没变化时每 15 分钟记一行
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
                // 文件正被占用，下一次变化时再记
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

        // 启动时读回：已记过的消息 ID、每个文件的读取位置和当前提问、会话索引
        private void LoadState()
        {
            if (File.Exists(detailPath))
            {
                foreach (string line in File.ReadAllLines(detailPath, Encoding.UTF8))
                {
                    if (line.Length == 0 || line.StartsWith("时间,", StringComparison.Ordinal))
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
                    if (c > 0 && !line.StartsWith("消息ID,", StringComparison.Ordinal) && !line.StartsWith("\uFEFF消息ID,", StringComparison.Ordinal))
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
                    if (f.Length >= 2 && f[0] != "会话ID"
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
                    if (line.Length == 0 || line.StartsWith("会话ID,", StringComparison.Ordinal))
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
                    uw.WriteLine("会话ID,开始UTC,结束UTC");
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

        // 解析一行 CSV（字段可能带双引号，双引号内的 "" 表示一个 "）
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

        // 行里 "promptId":"…" 的值（不解析整行 JSON，省时间）
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

    // 每条消息的档位标注（日志\用量标注.csv）：思考强度（low / medium / high / xhigh / max）、是否开着 Ultracode、是否快速模式、思考 token 数。
    // 由 UsageLogger 记明细时一起写入；这里增量读回，统计时按消息 ID 查
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

        // 档位标注还没写到文件里的新消息：用记录里直接读到的思考强度、快速模式、思考 token（Ultracode 只有标注文件里才有）
        public static Tag FromRec(UsageTracker.Rec r)
        {
            Tag t = new Tag();
            t.Effort = r.Effort ?? "";
            t.Fast = r.Fast;
            t.Think = r.Think;
            return t;
        }

        // 档位名：ultra（开着 Ultracode）> 思考强度；快速模式后面加 +fast；记录里没有的叫「未标注」
        public static string TierKey(Tag t)
        {
            if (t == null)
            {
                return "未标注";
            }
            string k = t.Ultra ? "ultra" : (t.Effort.Length > 0 ? t.Effort : "未标注");
            return t.Fast ? k + "+fast" : k;
        }

        // 把文件里新追加的部分读进来；最多每 20 秒看一次
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
                            if (f.Length < 5 || f[0].EndsWith("消息ID", StringComparison.Ordinal) || f[0].Length == 0)
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
                    checkedUtc = DateTime.MinValue;   // 文件正被写入，下一次再读
                }
            }
        }
    }

    // 额度换算表：日志\额度换算.csv，由「日志\额度换算.py」用额度快照反推。
    // 额度消耗 ≈ 官方价格折算的美元数 × 每美元消耗的额度百分点；没有实测过的模型，每美元的额度取全体平均（按官方价格推算）
    internal sealed class QuotaTable
    {
        public sealed class Entry
        {
            public readonly double[] Price = new double[5];   // 输入、输出、5 分钟缓存写入、1 小时缓存写入、缓存读取（美元 / 百万 token）
            public double TierMult = 1;                        // 上下文超过 LongContextTokens 时整条的价格倍率
            public double K5, KW;                              // 每美元折算消耗的 5 小时 / 每周额度百分点；0 = 没有结果
            public bool Measured5, MeasuredW;                  // 实测（否则是按价格推算）
        }

        public readonly Dictionary<string, Entry> Models = new Dictionary<string, Entry>();
        public readonly Dictionary<string, double[]> Tiers = new Dictionary<string, double[]>();   // 档位 → { 5 小时修正系数, 每周修正系数 }，没有就是 1
        public double ReadScale = 1;                           // 缓存读取单价的缩放（拟合出来的，1 = 官方价格）
        public double CalibErr = -1;                           // 额度换算的「事前预测」误差：每一块数据用它之前的数据拟合再预测，平均相对误差；-1 = 没有
        public int CalibBlocks;                                // 参与的块数
        public bool Stale;                                     // 文件里还没有当前账号 / 计划的结果：只留价格表（订阅视图折算金额用），系数一律不用（显示「—」），别拿另一个账号的系数硬套

        // key = 当前的「口径」（账号:计划，例如 B:pro）。额度换算.py 给每个口径各写一组行（最后一列），当前口径写在最后；
        // 要的口径还没有就用文件里最后一组，并标记 Stale
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
                if (f.Length < 13 || f[0] == "类型")
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
                if (f.Length < 13 || f[0] == "类型")
                {
                    continue;
                }
                if ((f.Length > 22 ? f[22] : "") != use)
                {
                    continue;
                }
                if (t.Stale && f[0] != "模型")
                {
                    continue;
                }
                if (f[0] == "全局")
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
                if (f[0] == "档位")
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
                e.Measured5 = f[11] == "实测";
                e.MeasuredW = f[12] == "实测";
                double mult;
                if (f.Length > 15 && double.TryParse(f[15], NumberStyles.Float, CultureInfo.InvariantCulture, out mult) && mult > 0)
                {
                    e.TierMult = mult;
                }
                t.Models[f[1]] = e;
            }
            return t.Models.Count > 0 ? t : null;
        }

        // 档位修正系数（没有测过的档位就是 1）
        public double TierFactor(string tier, bool week)
        {
            double[] d;
            return tier != null && Tiers.TryGetValue(tier, out d) ? (week ? d[1] : d[0]) : 1.0;
        }

        // tok：ModelTotal.Tok 的 10 项。返回折算的美元数（缓存读取按拟合出来的缩放，估额度用）
        public double CostUsd(Entry e, long[] tok)
        {
            return CostUsdAt(e, tok, ReadScale);
        }

        // readScale = 1 就是纯官方价格（订阅视图的「API 等价金额」用它：这是真实的官方标价，不是拟合出来的额度权重）
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

    // 按时间段统计用量日志（用量明细_v2.csv）：一次读取，同时算出多个时段。结果和实时统计的时段格式一样
    internal static class LogSummary
    {
        public static UsageTracker.PeriodSummary[] ComputeMany(string detailPath, string indexPath, DateTime[] froms, DateTime[] tos, string[] labels)
        {
            return ComputeMany(detailPath, indexPath, froms, tos, labels, null, null);
        }

        // accts：和 froms 一一对应，不为 null 的那项只统计「这个时刻归这个账号」的记录（按切换时间线 idx 判断）
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
                    // 先只解析时间（第一个字段没有引号），不在范围内的行直接跳过，省时间
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
                st.Name = string.IsNullOrEmpty(f[2]) ? "会话" : f[2];
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
                    if (f.Length >= 3 && f[0] != "会话ID")
                    {
                        d[f[0]] = f[2];
                    }
                }
            }
            catch (IOException)
            {
                // 索引正在被写入，这一轮用空的
            }
            return d;
        }

        private static long Num(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // 解析一行 CSV（字段可能带双引号，双引号内的 "" 表示一个 "）
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

    // 显示设置：勾选要在面板里显示的时间标签
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
            Text = "显示设置 · 时间标签";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(360, 250);

            Label tip = new Label();
            tip.Text = "勾选要在面板里显示的时间标签（至少选一个）：";
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
            ok.Text = "确定";
            ok.Size = new Size(70, 28);
            ok.Location = new Point(186, 206);
            ok.Click += delegate
            {
                if (Mask == 0)
                {
                    MessageBox.Show(this, "至少要显示一个时间标签。", "显示设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(70, 28);
            cancel.Location = new Point(266, 206);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // 自定义时段：选择开始和结束时间（精确到分钟），也可以用快捷按钮
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
            Text = "自定义时段";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(360, 200);

            Label lf = new Label();
            lf.Text = "开始";
            lf.Location = new Point(16, 22);
            lf.AutoSize = true;
            fromPicker = MakePicker(from, 60, 18);
            Label lt = new Label();
            lt.Text = "结束";
            lt.Location = new Point(16, 56);
            lt.AutoSize = true;
            toPicker = MakePicker(to, 60, 52);

            string[] presets = { "最近1小时", "最近6小时", "今天", "昨天", "本周" };
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
            ok.Text = "确定";
            ok.Size = new Size(70, 28);
            ok.Location = new Point(186, 150);
            ok.Click += delegate
            {
                if (From >= To)
                {
                    MessageBox.Show(this, "结束时间要晚于开始时间。", "自定义时段", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button();
            cancel.Text = "取消";
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
                    from = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));   // 周一 00:00
                    break;
            }
            fromPicker.Value = from;
            toPicker.Value = to;
        }
    }

    // 会话钻取：列出一个会话在「日志\用量明细.csv」里的合计、缓存命中率和最近的 API 消息
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
            Text = "会话明细 · " + title;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(680, 440);

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.Columns.Add("时间", 130);
            list.Columns.Add("模型", 160);
            list.Columns.Add("输入", 70, HorizontalAlignment.Right);
            list.Columns.Add("输出", 70, HorizontalAlignment.Right);
            list.Columns.Add("缓存写", 80, HorizontalAlignment.Right);
            list.Columns.Add("缓存读", 90, HorizontalAlignment.Right);
            list.Columns.Add("子任务", 60, HorizontalAlignment.Center);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 42;
            Button copy = new Button();
            copy.Text = "复制会话ID";
            copy.SetBounds(8, 7, 110, 28);
            copy.Click += delegate { Clipboard.SetText(session); };
            Button open = new Button();
            open.Text = "打开日志文件夹";
            open.SetBounds(126, 7, 130, 28);
            open.Click += delegate
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(detailPath) + "\"");
            };
            bottom.Controls.Add(copy);
            bottom.Controls.Add(open);
            Button record = new Button();
            record.Text = "打开会话记录";
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

        // 在资源管理器里选中这个会话的原始记录文件（路径来自会话索引）
        private void OpenTranscript()
        {
            string path;
            try
            {
                path = TranscriptPath();
            }
            catch (IOException)
            {
                path = null;   // 索引正在被写入，稍后再点
            }
            if (path == null)
            {
                MessageBox.Show(this, "没有找到这个会话的记录文件（可能已被 Claude Code 清理，或者还没有写入会话索引）。",
                    "会话记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        private string TranscriptPath()
        {
            string indexPath = Path.Combine(Path.GetDirectoryName(detailPath), "会话索引.csv");
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
                        return null;   // 原文件已经不在了
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
                summary.Text = "还没有日志。请先在悬浮窗右键菜单里开启「记录用量到日志」。";
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
            summary.Text = "消息 " + rows.Count.ToString(CultureInfo.InvariantCulture) + " 条 · 输入 " + UsageTracker.FormatTokens(inp)
                + " · 输出 " + UsageTracker.FormatTokens(outp) + " · 缓存写入 " + UsageTracker.FormatTokens(cw)
                + " · 缓存读取 " + UsageTracker.FormatTokens(cr) + "\r\n缓存命中率 " + hit
                + "（以下列出最近 " + Math.Min(MaxRows, rows.Count).ToString(CultureInfo.InvariantCulture) + " 条，最新在前）";

            for (int i = rows.Count - 1, shown = 0; i >= 0 && shown < MaxRows; i--, shown++)
            {
                string[] f = rows[i];
                ListViewItem item = new ListViewItem(new string[]
                {
                    f[0], f[3], Fmt(f[4]), Fmt(f[5]), UsageTracker.FormatTokens(ParseLong(f[6]) + ParseLong(f[7])),
                    Fmt(f[8]), f[9] == "1" ? "是" : ""
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

        // 解析一行 CSV（字段可能带双引号，双引号内的 "" 表示一个 "）
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

    // 悬浮窗本体。用「逐像素 alpha 的分层窗口」自己合成画面：
    // 背景（圆角底板+边框）和前景（文字+进度条）各自带各自的透明度，互不影响。
    internal sealed class Widget : Form
    {
        private const int BaseW = 264;
        private const int PollSeconds = 60;

        // 配色：由当前界面风格决定（纸色 / 墨底两套），绘制代码只用下面这些变量
        private static Color Bg, BorderColor, TextMain, TextDim, Track, Accent, Green, Amber, Red;
        private static SkinPalette curPal;

        // 界面风格：组合（当前）、混合1–5、方案 2 的 A–E、方案 3 的 F–J
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

        private Skin skin = Skins[0];                   // 当前界面风格
        private int skinIndex;                          // 当前风格的序号（保存到 settings.txt 的 skin=）
        private ToolStripMenuItem[] skinItems = new ToolStripMenuItem[0];
        private const int HotkeyNextSkin = 2;           // 切换下一个风格的快捷键编号
        private const uint VkN = 0x4E;                  // N 键
        private bool hotkeyNextOn;

        // 窗口里紧凑卡占的高度 = 风格自己的紧凑卡高度 + 底下那条切换栏（用量面板从这里往下画）
        private int CompactH()
        {
            return skin.CompactH + ToggleBarH;
        }

        private Fonts FontsNow()
        {
            return new Fonts { Title = fTitle, Label = fLabel, Small = fSmall, Display = fInk, Big = fBig };
        }

        // 切换界面风格：记住选择；窗口高度按新风格恢复默认
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

        private static readonly string[] Weekday = { "日", "一", "二", "三", "四", "五", "六" };

        private FetchResult data;       // 最近一次成功的数据
        private FetchResult problem;    // 最近一次失败（成功后清空）
        private bool fetching;
        private DateTime nextFetchUtc = DateTime.MinValue;
        private string lastSignature = "";

        private float scale = 1f;
        private Font fTitle, fLabel, fSmall, fInk, fBig;   // fInk：墨滴字体（标题）；fBig：大数字
        private RectangleF refreshRect, closeRect;

        // 两个独立的透明度，数值是「不透明度」百分比：越大越实，越小越透
        private int bgPercent = 85;     // 背景：底板和边框
        private int fgPercent = 100;    // 前景：文字和进度条
        private const int FgMin = 20;   // 文字最低保留 20%，免得把自己调没了

        // 分层窗口的像素缓冲（DIB section）
        private IntPtr memDc, dib, oldObj;
        private Bitmap surface;
        private int surfaceW, surfaceH;

        // 用量面板（详细模式）：默认打开，别人一启动就能看到全部核心功能。「简洁 / 详细」切换栏或右键菜单可以关；关闭时不读任何会话文件
        private bool showUsage = true;
        private const int ToggleBarH = 22;      // 紧凑卡和用量面板之间那条「简洁 / 详细」切换栏的高度（基础尺寸，未乘缩放）
        private RectangleF toggleRect;          // 切换按钮的位置（点击在简洁模式和详细模式之间切换）
        private ToolStripMenuItem usageMenuItem;   // 右键菜单里的「显示用量统计」，用切换栏改了状态后要同步勾选
        private bool showDeepSeek;              // 面板最下面的「DeepSeek 闲时价估算」行：默认不显示（这是个很个人的功能，设置 ds=1 或右键菜单里打开）
        private UsageTracker usage;
        private UsageTracker.Snapshot usageSnap = new UsageTracker.Snapshot();
        private const int UsageW = 420;         // 打开用量统计时的窗口宽度（会话表格要放下缓存剩余时间和四列数字）
        private const int UsagePanelH = 288;    // 面板高度（基础尺寸，未乘缩放）：表头和液面杯 146 + 6 行 × 16 + 合计和费用 36
        private const int DiagramH = 130;       // 墨滴双圆的高度（基础尺寸，未乘缩放）
        private bool rowsExpanded;              // 点了「其他 N 个」：展开全部会话（窗口会按需要长高）
        private int rowScroll;                  // 展开后窗口放不下时，滚轮翻到第几行
        private int savedWinHu, autoWinHu;      // 展开前保存的窗口高度；展开时自动设置的高度
        private RectangleF moreRect;            // 「其他 N 个 / 收起」那一行的位置（点击展开或收起）
        // 时间标签，下标就是标签 id：0 = 5分钟，1 = 15分钟，2 = 30分钟，3 = 1小时，4 = 3小时，5 = 本5小时，6 = 上5小时，
        // 7 = 今天，8 = 昨天，9 = 本周，10 = 自定义
        private const int TabCount = 11;
        private const int TabToday = 7;
        private const int TabCustom = 10;
        private static readonly string[] TabLabels = { "5分钟", "15分钟", "30分钟", "1小时", "3小时", "本5小时", "上5小时", "今天", "昨天", "本周", "自定义" };
        private const int TabDefaultMask = 1258;    // 默认显示：15分钟、1小时、本5小时、上5小时、今天、自定义（第 i 位 = 标签 i）
        private int tabMask = TabDefaultMask;       // 面板里显示哪些标签，在「显示设置」里勾选
        private int usagePeriod = 1;                // 当前标签（标签 id），默认 15分钟
        private readonly RectangleF[] usageTabs = new RectangleF[TabCount];   // 每个标签的位置（没有显示的标签是空矩形）
        private static readonly DateTime Epoch = new DateTime(2020, 1, 1);
        private int customFromMin = int.MinValue, customToMin = int.MinValue;   // 自定义时段（距 Epoch 的分钟数）
        private readonly UsageTracker.PeriodSummary[] logBy = new UsageTracker.PeriodSummary[TabCount];   // 日志统计结果，下标是标签 id（7 今天、8 昨天、9 本周、10 自定义），后台算好再回到界面线程显示
        private bool logBusy;
        private DateTime logRefreshUtc = DateTime.MinValue;
        private int usageBusy;                  // 后台统计是否正在进行（0 或 1）
        private UsageLogger logger;             // 用量日志：明细和额度快照写进「日志」文件夹
        private NotifyIcon tray;                // 托盘图标：关窗口时缩到托盘，托盘菜单里的「退出」才真正结束
        private bool exitRequested;
        private int usageMode;                  // 面板视图：0 = 会话，1 = 模型，2 = 对比，3 = 档位，4 = 订阅
        private readonly RectangleF[] segRects = new RectangleF[5];   // 表头五个视图按钮（会话 / 模型 / 对比 / 档位 / 订阅）的位置
        private QuotaTable quota;                                  // 额度换算表（日志\额度换算.csv），没有文件时为 null
        private DateTime quotaReadUtc = DateTime.MinValue;
        private DateTime quotaFitUtc = DateTime.MinValue;          // 上一次在后台重算额度换算的时间
        private LoginInfo login;                                   // 最近一次读到的登录信息（org、计划、订阅创建时间）
        private DateTime loginCheckUtc = DateTime.MinValue;
        private bool bootBusy, bootDone;                           // 第一次运行：从会话记录回溯历史账号
        private RectangleF accountRect = RectangleF.Empty;         // 紧凑卡上的账号小标签（点击切换账号）
        private readonly List<KeyValuePair<RectangleF, string>> accountHits = new List<KeyValuePair<RectangleF, string>>();   // 订阅视图里每个账号行的位置
        private readonly Dictionary<string, UsageTracker.PeriodSummary> subBy = new Dictionary<string, UsageTracker.PeriodSummary>();   // 账号 → 当前订阅周期的用量
        private DateTime subRefreshUtc = DateTime.MinValue;
        private bool subBusy;
        private DateTime renewCheckUtc = DateTime.MinValue;
        private string renewNotified = "";
        private readonly List<WeekPoint> weekHist = new List<WeekPoint>();   // 每周额度的近期采样（周额度用完预测用）
        private bool weekHistLoaded;

        private sealed class WeekPoint
        {
            public DateTime T;
            public double Pct;
            public long Reset;
        }
        private DateTime vsyncUtc = DateTime.MinValue;   // 上一次导出 VS Code 标题的时间
        private readonly double[] colFr = { 0.40, 0.12, 0.12, 0.12, 0.12, 0.12 };   // 列宽比例：会话名、缓存剩余、输入、输出、缓存写入、缓存读取（和为 1，可以拖动调整）
        private readonly float[] colWidth = new float[6];   // 本次绘制时各列的宽度（物理像素）
        private readonly float[] colEdgeX = new float[5];   // 各列分隔线的位置
        private float colInner = 1f;
        private float colTop, colBottom;
        private int colDrag = -1;                           // 正在拖动第几条分隔线（-1 = 没有）
        private int colDragX0;
        private readonly double[] colDragStart = new double[6];
        private readonly HashSet<string> expandedGroups = new HashSet<string>();     // 模型视图里展开了的会话（会话 ID）
        private readonly List<KeyValuePair<RectangleF, string>> chevHits = new List<KeyValuePair<RectangleF, string>>();   // 模型视图里 ▸ 的位置（点击展开 / 折叠）
        private readonly List<KeyValuePair<RectangleF, string>> sessionHits = new List<KeyValuePair<RectangleF, string>>();   // 用量面板里每个会话行的位置（点击钻取）
        private int winWu, winHu;               // 用户拖动调整后的窗口大小（基础单位）；0 = 用默认大小
        private bool resizing;                  // 正在拖动边缘调整大小
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
        private const int HotkeyInvert = 1;             // 黑白切换的快捷键编号
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint VkB = 0x42;                  // B 键
        private bool inverted;                          // 当前是墨底（黑白反向）
        private bool hotkeyOn;                          // 快捷键是否注册成功（被别的程序占用时会失败，菜单照常可用）

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
                cp.ExStyle |= 0x80000;  // WS_EX_LAYERED：逐像素 alpha
                cp.ExStyle |= 0x80;     // WS_EX_TOOLWINDOW：不出现在 Alt+Tab 里
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        // 画面完全由 UpdateLayeredWindow 提供，系统的绘制消息一律忽略
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

        // 快捷键 Ctrl+Alt+B：不用点中窗口，在任何界面里都能切换（窗口缩到托盘时也能）
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

        // 黑白切换：纸色底墨色字 ↔ 墨底纸色字；马上重画，并记住这次的选择
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
                // 点「×」只是缩到托盘；托盘或右键菜单里的「退出」才真正结束程序
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

        // ---------------------------------------------------------------- 取数

        private void OnTick(object sender, EventArgs e)
        {
            // 窗口被拖到另一块 DPI 不同的屏幕时，重新按新比例排版
            if (Math.Abs(GetScale() - scale) > 0.01f)
            {
                ApplyScale();
            }

            if (!fetching)
            {
                // 到了计划的时间；或者窗口刚好重置，马上去拿新数据（出错期间不抢拍，按退避时间走）
                if (DateTime.UtcNow >= nextFetchUtc || (problem == null && ResetJustPassed()))
                {
                    StartFetch();
                }
            }

            // 倒计时精确到分钟：界面上的文字没变就不重绘，空闲时几乎不占 CPU
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
                // 账号识别出任何问题都不能影响额度显示
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
                    // 窗口已经关了
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

        // 默认的「自动」来源：状态栏没有数据时，改用在线查询要先问用户一次（在线查询会读登录令牌、请求没有公开文档的接口）。
        // 答案记在 settings.txt 的 onl=：同意 = 以后自动回退；拒绝 = 只用状态栏，不再问（右键 → 额度数据来源 里随时可以改）
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
                            "Stilla 现在没有从 Claude Code 状态栏收到额度数据（刚装好、还没配置状态栏、或者 Claude Code 没在运行时都会这样）。\r\n\r\n"
                            + "默认的「自动」模式可以在这种时候改用「在线查询」来补上：读取本机 ~/.claude/.credentials.json 里的 Claude 登录令牌，并用它请求一个没有公开文档的接口（和 Claude Code 的 /usage 用的是同一个）。\r\n\r\n"
                            + "· 令牌只在本机使用，不会发给别人，也不会保存。\r\n"
                            + "· 这个接口随时可能改动；Anthropic 的服务条款对「订阅登录凭据用在 Claude Code 以外的工具里」有限制，是否合适请你自己判断。\r\n"
                            + "· 选「否」就只用状态栏，不读令牌、不联网（右键 → 额度数据来源 里随时可以改）。\r\n\r\n"
                            + "状态栏没有数据时，允许使用在线查询吗？",
                            "Stilla · 额度数据来源", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
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

        // ---------------------------------------------------------------- 分层窗口合成

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

        // 字体按当前缩放重新创建（Redraw 用 fTitle 判断字体是否已经准备好）
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
            bi.biHeight = -h;   // 负数 = 自上而下
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

        // 合成一帧：先铺背景（带背景透明度），再把前景整体按文字透明度叠上去
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
                bf.AlphaFormat = 1;             // AC_SRC_ALPHA：用每个像素自己的 alpha
                // pptDst 传空：不改窗口位置，只更新画面
                UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memDc, ref src, 0, ref bf, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private void PaintBackground(Graphics g, int w, int h)
        {
            // 背景拉到 0% 也至少留 1/255 的 alpha：完全透明的像素会让鼠标直接穿过去，窗口就拖不动、点不到了
            int a = Math.Max(1, (int)Math.Round(255.0 * bgPercent / 100.0));
            using (GraphicsPath path = RoundRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), 10f * scale))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Bg)))
            using (Pen p = new Pen(Color.FromArgb(a, BorderColor)))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }

        // 前景：文字 + 进度条，用不透明色画在一张透明图层上，最后整体按文字透明度叠加
        private void PaintContent(Graphics g, int w, int h)
        {
            float s = scale;
            float pad = 14f * s;
            DateTime now = DateTime.Now;
            Quota five = data == null ? null : data.FiveHour;
            Quota week = data == null ? null : data.SevenDay;

            closeRect = new RectangleF(w - pad - 18f * s, 6f * s, 24f * s, 22f * s);
            refreshRect = new RectangleF(closeRect.X - 40f * s, 6f * s, 38f * s, 22f * s);

            CompactData d = new CompactData();
            d.Has5 = five != null;
            d.P5 = five == null ? 0 : five.Percent;
            d.HasW = week != null;
            d.PW = week == null ? 0 : week.Percent;
            ResetParts(five, now, false, out d.Clock5, out d.Left5);
            ResetParts(week, now, true, out d.ClockW, out d.LeftW);
            d.Updated = data != null ? data.FetchedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";

            // 底部状态
            string foot;
            Color footColor = TextDim;
            if (problem != null)
            {
                foot = (data != null ? "旧数据 · " : "") + Describe(problem);
                footColor = Amber;
            }
            else if (data == null)
            {
                foot = "正在读取…";
            }
            else if (data.Via == "bridge")
            {
                // 状态栏数据只在 Claude Code 运行时更新：太久没更新就提醒一下
                int age = (int)(DateTime.Now - data.FetchedAt).TotalMinutes;
                if (age >= 10)
                {
                    foot = "已 " + age + " 分钟没更新";
                    footColor = Amber;
                }
                else
                {
                    foot = "状态栏 · " + d.Updated;
                }
            }
            else
            {
                foot = "更新于 " + d.Updated + " · 每 60 秒";
            }
            d.Foot = foot;
            d.FootColor = footColor;
            d.Alert = problem != null || data == null || footColor.ToArgb() == Amber.ToArgb();
            if (data != null && data.Via == "bridge")
            {
                d.Source = "来自状态栏";
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

        // 「简洁 / 详细」切换按钮：画在紧凑卡正下方、用量面板上方的切换栏里，靠左。两种模式下窗口宽度不同，靠左才能保证位置不变（点一下后鼠标还在按钮上），所有风格通用
        private void DrawModeToggle(Graphics g, int w, float s)
        {
            string text = showUsage ? "▴  收起，只看额度" : "▾  展开详细统计";
            float tw = MeasureW(g, text) + 24f * s;
            toggleRect = new RectangleF(14f * s, skin.CompactH * s + 3f * s, tw, 16f * s);
            SkinDraw.RrLine(g, TextDim, toggleRect, 8f * s, 1f * s);
            DrawTextRect(g, text, fSmall, TextDim, toggleRect, StringAlignment.Center);
        }

        // 重置时刻和剩余时间分开给界面用：「19:47」和「1:11」；没有数据时是「—」
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
                clock = weekly ? "重置时间未知" : "尚未开始计时";
                return;
            }
            DateTime t = q.ResetsAt.Value;
            if (t <= now)
            {
                clock = "刷新中…";
                return;
            }
            clock = weekly || t.Date != now.Date
                ? t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : t.ToString("HH:mm", CultureInfo.InvariantCulture);
            left = weekly ? CountdownLong(t - now) : CountdownHm(t - now);
        }

        // DeepSeek 闲时价（人民币，每百万 tokens），模型 deepseek-flash（DeepSeek-V4.1-Flash-1017）。来源：api-docs.deepseek.com 定价页（页面未写生效日期，页面称价格可能调整）
        private const double DsHitPerM = 0.02;      // 输入（缓存命中）
        private const double DsMissPerM = 1.0;      // 输入（缓存未命中）；缓存写入按这个价算
        private const double DsOutPerM = 4.0;       // 输出

        private static double EstimateCostCny(long input, long output, long cacheWrite, long cacheRead)
        {
            return (input + cacheWrite) * DsMissPerM / 1000000.0
                + cacheRead * DsHitPerM / 1000000.0
                + output * DsOutPerM / 1000000.0;
        }

        // 占比：这一项占全部的比例（0~1），没有总量时是 0
        private static double ShareOf(long part, long all)
        {
            return all > 0 ? Math.Min(1.0, (double)part / all) : 0;
        }

        // 用量面板（墨滴版）：顶部是「USAGE」和 会话 / 模型 / 对比 三个视图按钮；然后是时间标签（液面杯，杯里墨的高度 = 这段时间的用量大小）；
        // 再是大号的用量数字和时间范围；下面是表格：每个会话一行（缓存剩余、输入、输出、缓存写入、缓存读取分开列），最后是合计和 DeepSeek 闲时价估算的费用。
        private void DrawUsagePanel(Graphics g, int w, int h)
        {
            float s = scale;
            float pad = 14f * s;
            float inner = w - 2f * pad;
            float top = CompactH() * s;
            List<int> shownTabs = VisibleTabs();
            if ((tabMask & (1 << usagePeriod)) == 0)
            {
                usagePeriod = shownTabs[0];     // 当前标签在设置里被隐藏了，就切到第一个显示的标签
            }

            skin.DrawFrame(g, new RectangleF(0f, top, w, UsagePanelH * s), curPal, s);

            // 表头：墨滴图标 + USAGE，右边是三个视图按钮
            skin.DrawTitle(g, pad, top + 6f * s, "USAGE", FontsNow(), curPal, s);
            DrawModeSwitch(g, w, pad, top + 7f * s, s);

            // 时间标签（液面杯），再是大号数字和时间范围
            DrawTabStrip(g, shownTabs, pad, top + 34f * s, inner, s);
            UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
            DrawTextRect(g, UsageTracker.FormatTokens(cur.Total), fBig, TextMain, new RectangleF(pad, top + 98f * s, inner * 0.4f, 26f * s), StringAlignment.Near);
            DrawTextRect(g, "TOKENS · " + cur.Range, fSmall, TextDim, new RectangleF(pad + inner * 0.4f, top + 104f * s, inner * 0.6f, 16f * s), StringAlignment.Far);

            // 列：会话名（左）| 缓存剩余（中）| 输入 输出 缓存写 缓存读（右）
            sessionHits.Clear();
            // 列宽按比例分配（可以在卡片里拖动分隔线调整）：会话名 | 缓存剩余 | 输入 | 输出 | 缓存写入 | 缓存读取
            // 每列先保证最小宽度（窗口缩窄后也不会把数字挤没），剩下的宽度再按比例分配
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
            string[] heads = usageMode == 0 ? new string[] { "输入", "输出", "缓存写", "缓存读" }
                : usageMode == 1 ? new string[] { "token", "5h估算", "周估算", "缓存读" }
                : usageMode == 3 ? new string[] { "条数", "输出", "思考", "5h估算" }
                : usageMode == 4 ? new string[] { "本期用量", "月费", "回本", "续费" }
                : new string[] { "token", "占比", "5h估算", "倍数" };
            DrawTextRect(g, usageMode == 0 ? "会话" : usageMode == 1 ? "模型" : usageMode == 3 ? "档位" : usageMode == 4 ? "账号" : "对比", fSmall, TextDim, new RectangleF(pad, headY, nameW, 14f * s), StringAlignment.Near);
            if (usageMode == 0)
            {
                DrawTextRect(g, "缓存剩余", fSmall, TextDim, new RectangleF(pad + nameW, headY, cacheW, 14f * s), StringAlignment.Far);
            }
            DrawRowNumbers(g, pad, inner, colW, headY, 14f * s, heads, TextDim);

            // 当前窗口高度能放下几行（底下还要留合计和费用两行）；窗口拉高了就多显示
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
                DrawTextRect(g, "这段时间内没有用量", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                drawn = 1;
            }
            else
            {
                // 放不下时：前 cap-1 行照常显示，最后一行是「其他 N 个」，点它展开全部
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
                    DrawMoreRow(g, "▾ 其他 " + (rowN - rowShown) + " 个", Numbers(rIn, rOut, rCw, rCr), pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                    drawn++;
                }
                else if (rowCollapse)
                {
                    DrawMoreRow(g, rowN > rowShown ? "▴ 收起 · 滚轮翻页" : "▴ 收起", null, pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                    drawn++;
                }
            }

            // 合计行：这个时间段的合计（和表头对应）
            float yTotal = y + drawn * rowH + 3f * s;
            using (Pen p = new Pen(Track))
            {
                g.DrawLine(p, pad, yTotal - 2f * s, w - pad, yTotal - 2f * s);
            }
            DrawTextRect(g, "合计", fSmall, TextMain, new RectangleF(pad, yTotal, nameW, rowH), StringAlignment.Near);
            if (usageMode >= 1 && quota != null && quota.CalibErr >= 0)
            {
                // 额度换算的自我检验：每一块数据都先用它之前的数据拟合、再去预测它，这是最近这些块的平均相对误差
                DrawTextRect(g, "估算误差≈" + (quota.CalibErr * 100).ToString("0", CultureInfo.InvariantCulture) + "%", fSmall, TextDim,
                    new RectangleF(pad + 34f * s, yTotal, nameW - 34f * s, rowH), StringAlignment.Near);
            }
            string[] totalNums = usageMode == 0 ? Numbers(cur.Input, cur.Output, cur.CacheWrite, cur.CacheRead)
                : usageMode == 1 ? new string[] { UsageTracker.FormatTokens(cur.Total), TotalEstimateText(cur, false), TotalEstimateText(cur, true), UsageTracker.FormatTokens(cur.CacheRead) }
                : usageMode == 3 ? TierTotalNums(cur)
                : new string[] { UsageTracker.FormatTokens(cur.Total), cur.Total > 0 ? "100%" : "", TotalEstimateText(cur, false), "" };
            DrawRowNumbers(g, pad, inner, colW, yTotal, rowH, totalNums, TextMain);

            // 最后一行：按 DeepSeek 闲时价估算的费用（「如果这些 token 用 DeepSeek 要花多少」，默认不显示，右键菜单里可以打开）
            if (showDeepSeek)
            {
                double cost = EstimateCostCny(cur.Input, cur.Output, cur.CacheWrite, cur.CacheRead);
                float yCost = yTotal + rowH + 1f * s;
                DrawTextRect(g, "DeepSeek 闲时价估算（Flash）", fSmall, TextDim, new RectangleF(pad, yCost, inner * 0.7f, rowH), StringAlignment.Near);
                DrawTextRect(g, "¥" + cost.ToString("0.00", CultureInfo.InvariantCulture), fLabel, TextMain, new RectangleF(pad + inner * 0.6f, yCost - 2f * s, inner * 0.4f, rowH + 4f * s), StringAlignment.Far);
            }
        }

        // 一个会话的活跃程度：0 = 正在写入（最近 60 秒有动静），1 = 缓存还没过期，2 = 已过期或没有缓存。
        // 实时状态以统计里的 Live 为准（日志统计的今天 / 昨天 / 本周也能用上）
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

        // 会话的显示顺序：正在用的在最上面，缓存没过期的其次，过期的往下排；同一档里最近用过的在前，再按 token 多少
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

        // ---------------------------------------------------------------- 模型视图：按会话分组
        // 一个会话一行；这个会话在这段时间里用过的每个「模型 × 档位」是它的子行。只有一种组合时就是一行；
        // 中途切换过（换了模型、或者把思考强度从 xhigh 调到 max）的折叠在同一行里，点行首的 ▸ 展开，子行按首次使用的先后排，带时间段。
        private sealed class ModelGroup
        {
            public string Session;
            public readonly List<UsageTracker.ModelTotal> Parts = new List<UsageTracker.ModelTotal>();
            public long Total, CacheRead;
        }

        private sealed class ModelLine
        {
            public ModelGroup Group;
            public UsageTracker.ModelTotal Part;     // null = 会话行（组行），否则是展开后的子行
            public bool Expanded;
        }

        // 平铺成要显示的行：组行 + （展开了的）子行。会话的顺序和会话视图一样：正在用的在上面，缓存没过期的其次，过期的往下
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

        // 行太多时怎么显示：没展开 → 前 cap-1 行 +「其他 k 个」；展开 → 能放下几行放几行（留一行给「收起」），多出来的用滚轮翻
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

        // 「其他 N 个」「收起」这一行：点它展开或收起；nums 不为空时右边显示被合并的这些行的合计
        private void DrawMoreRow(Graphics g, string text, string[] nums, float pad, float inner, float s, float colW, float nameW, float y, float rowH)
        {
            moreRect = new RectangleF(pad, y, inner, rowH);
            DrawTextRect(g, text, fSmall, Accent, new RectangleF(pad + 4f * s, y, nameW - 4f * s, rowH), StringAlignment.Near);
            if (nums != null)
            {
                DrawRowNumbers(g, pad, inner, colW, y, rowH, nums, TextDim);
            }
        }

        // 现在这个视图一共有多少行（会话视图是会话数，模型视图是「会话 × 模型」的行数）
        private int RowCountNow()
        {
            UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
            return usageMode == 1 ? BuildModelLines(cur).Count : usageMode == 4 ? AccountStore.Accounts.Count : cur.Rows.Count;
        }

        // 展开：窗口长高到刚好放下全部行（最高到屏幕可用高度，放不下的用滚轮翻）；再点一次收起，窗口恢复原来的高度
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
                winHu = savedWinHu;     // 窗口高度还是自动长高的那个值：恢复；你自己拖过就不动
            }
            autoWinHu = 0;
            ApplyScale();
            KeepOnScreen();
        }

        // 当前显示的标签 id（从小到大）
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
                res.Add(1);     // 设置被改坏了也至少有一个标签
            }
            return res;
        }

        // 时间标签：交给当前风格去画；只画勾选显示的标签，平均分宽；标签太多放不下时字号自动缩小
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

        // 表头右边的四个视图按钮：会话 / 模型 / 对比 / 档位（点按钮直接切换）
        private void DrawModeSwitch(Graphics g, int w, float pad, float y, float s)
        {
            string[] names = { "会话", "模型", "对比", "档位", "订阅" };
            float segW = 34f * s, segH = 18f * s;
            float x0 = w - pad - segW * names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                segRects[i] = new RectangleF(x0 + segW * i, y, segW, segH);
            }
            skin.DrawModes(g, segRects, names, usageMode, FontsNow(), curPal, s);
        }

        // 一个会话一行：有活跃标记的画绿点；名字；缓存剩余时间；四项用量
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

            // 有会话标题就显示标题（最能区分是哪个会话），没有时用「项目名 · 会话ID」
            string name = t.Title != null ? t.Title : t.Name + " · 未命名 " + t.ShortId;
            if (t.SubCount > 0)
            {
                name += " +" + t.SubCount + "子";
            }
            DrawTextRect(g, name, fSmall, c, new RectangleF(pad + 12f * s, y, nameW - 12f * s, rowH), StringAlignment.Near);

            // 缓存剩余：主会话最近一次读写缓存的时间 + 有效期，还没到期就显示倒计时
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
                    cacheText = "已过期";
                    cacheColor = TextDim;
                }
            }
            else
            {
                cacheText = "无缓存";
                cacheColor = TextDim;
            }
            DrawTextRect(g, cacheText, fSmall, cacheColor, new RectangleF(pad + nameW, y, cacheW, rowH), StringAlignment.Far);

            DrawRowNumbers(g, pad, inner, colW, y, rowH, Numbers(t.Input, t.Output, t.CacheWrite, t.CacheRead), c);
        }

        // 剩余时间：不到 1 小时写 m:ss，超过写 h:mm:ss
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

        // 右边四列靠右对齐
        private void DrawRowNumbers(Graphics g, float pad, float inner, float colW, float y, float h, string[] texts, Color color)
        {
            float x = pad + colWidth[0] + colWidth[1];
            for (int k = 0; k < 4; k++)
            {
                DrawTextRect(g, texts[k], fSmall, color, new RectangleF(x, y, colWidth[2 + k], h), StringAlignment.Far);
                x += colWidth[2 + k];
            }
        }

        // 调用 scripts\sync_vscode_titles.py 导出 VS Code 里的会话标题。在后台线程里跑，最多等 60 秒，不卡界面
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
                    // 没有 Python 或脚本出错：保留上一次导出的标题
                }
            });
        }

        // 调用 scripts\quota_fit.py --quick：用额度快照和用量明细反推每个模型每美元消耗多少额度（写 logs\额度换算.csv）。
        // 后台低优先级跑几秒钟，每小时一次（启动时也跑一次）：数据越积越多，系数就越来越准
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
                    quotaReadUtc = DateTime.MinValue;   // 马上重读新结果
                }
                catch (Exception)
                {
                    // 没有 Python 或脚本出错：继续用上一次的结果
                }
            });
        }

        // 读取 额度换算.csv，每 5 分钟重读一次；没有文件时不显示估算
        private void LoadQuota()
        {
            if (DateTime.UtcNow - quotaReadUtc < TimeSpan.FromMinutes(5))
            {
                return;
            }
            quotaReadUtc = DateTime.UtcNow;
            try
            {
                QuotaTable t = QuotaTable.Load(Path.Combine(AppPaths.DataDir, "额度换算.csv"), AccountStore.CurrentKey);
                if (t != null)
                {
                    quota = t;
                }
            }
            catch (IOException)
            {
                quotaReadUtc = DateTime.MinValue;   // 文件正被脚本写入，下一轮再读
            }
        }

        // ---------------------------------------------------------------- 账号与订阅

        // 紧凑卡右上角的小标签：「B · Pro」。点它弹出账号菜单。当前登录还没有对应的账号档案时写「未绑定」
        private string AccountChipText()
        {
            Account cur = AccountStore.Current;
            if (cur != null)
            {
                return cur.Id + " · " + PlanInfo.ShortOf(cur.Plan);
            }
            return login != null && login.Ok ? "未绑定" : "";
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
                chip = AccountStore.Current.Id;     // 窄了：只留编号
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

        // 每 30 秒在后台读一次登录信息（只读 org / 计划 / 订阅创建时间，不碰令牌）
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
                    // 窗口已经关了
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
                // 没见过的登录账号：自动建档，计划和订阅开始日期用识别到的
                a = new Account();
                a.Id = AccountStore.NextId(AccountStore.Accounts);
                a.BindOrg = li.OrgUuid;
                AccountStore.FillFromLogin(a, li);
                AccountStore.Accounts.Add(a);
                AccountStore.SaveAccounts();
                Balloon("检测到新的登录账号", "已建档为「" + a.Label + "」（" + PlanInfo.NameOf(a.Plan) + "）。右键 → 账号与订阅 里可以改名字、月费。");
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
                    Balloon("订阅计划变了", a.Label + "：" + PlanInfo.NameOf(old) + " → " + PlanInfo.NameOf(a.Plan) + "。额度换算会从现在起按新计划分开统计。");
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
            if (AccountStore.SetCurrent(a.Id, a.Plan, "自动识别"))
            {
                if (before != null && before.Id != a.Id)
                {
                    Balloon("已切换到账号 " + a.Label, "检测到 Claude Code 登录的账号变了（" + PlanInfo.NameOf(a.Plan) + "）。额度和用量从现在起算到「" + a.Label + "」名下。");
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

        // 第一次运行（还没有任何账号档案）：在后台扫一遍会话记录里的 credential_org，还原过去用过哪些登录账号、什么时候换的
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
                        loginCheckUtc = DateTime.MinValue;     // 马上再对一次当前登录
                        AfterAccountChange();
                    }));
                }
                catch (Exception)
                {
                    bootBusy = false;
                }
            });
        }

        // 账号 / 计划变了：额度换算按新的口径重算，订阅周期用量重读
        private void AfterAccountChange()
        {
            quotaReadUtc = DateTime.MinValue;
            quotaFitUtc = DateTime.MinValue;
            subRefreshUtc = DateTime.MinValue;
            logRefreshUtc = DateTime.MinValue;
            UpdateTrayText();
            Redraw();
        }

        // 手动选账号。悬浮窗读的是「当前登录账号」的额度，所以只能把统计归到和当前登录一致的账号：
        // 账号没绑定 → 把当前登录绑定到它；绑定的是另一个登录 → 说明原因，请先在 Claude Code 里 /login
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
                    MessageBox.Show(this, "账号「" + a.Label + "」绑定的是另一个 Claude 登录" + (a.Hint.Length > 0 ? "（" + a.Hint + "）" : "") + "。\r\n\r\n"
                        + "悬浮窗读的是当前登录账号（" + (cur != null ? cur.Label : "未绑定") + "）的额度，所以不能把统计归到「" + a.Label + "」名下。\r\n"
                        + "请先在 Claude Code 里 /login 登录到那个账号，悬浮窗会自动切过去。", "账号与订阅", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Account owner = AccountStore.ByOrg(login.OrgUuid);
                if (owner != null && owner != a)
                {
                    if (MessageBox.Show(this, "当前登录已经绑定在「" + owner.Label + "」上。改绑到「" + a.Label + "」吗？\r\n（" + owner.Label + " 会变成未绑定）", "账号与订阅",
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
            AccountStore.SetCurrent(a.Id, a.Plan, "手动");
            AfterAccountChange();
        }

        private void FillAccountItems(ToolStripItemCollection items)
        {
            Account cur = AccountStore.Current;
            foreach (Account a in AccountStore.Accounts)
            {
                Account acc = a;
                ToolStripMenuItem it = new ToolStripMenuItem(a.Label + " · " + PlanInfo.NameOf(a.Plan) + (a.BindOrg.Length == 0 ? "（未绑定）" : ""));
                it.Checked = a == cur;
                it.Click += delegate { TrySwitchAccount(acc.Id); };
                items.Add(it);
            }
            if (AccountStore.Accounts.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("（正在识别当前登录…）");
                none.Enabled = false;
                items.Add(none);
            }
            items.Add(new ToolStripSeparator());
            items.Add("订阅视图", null, delegate
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
            items.Add("管理账号与订阅…", null, delegate { OpenAccountManager(); });
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
                // 计划改了：是真的升降级（从现在起算），还是之前记错了（把这个账号的历史记录一起改）？
                List<TimelineEntry> tl = AccountStore.TimelineCopy();
                bool tlChanged = false;
                foreach (Account a in AccountStore.Accounts)
                {
                    string old;
                    if (!oldPlans.TryGetValue(a.Id, out old) || old == a.Plan || AccountStore.TimelineCount(a.Id) == 0)
                    {
                        continue;
                    }
                    DialogResult dr = MessageBox.Show(this, "「" + a.Label + "」的计划从 " + PlanInfo.NameOf(old) + " 改成了 " + PlanInfo.NameOf(a.Plan) + "。\r\n\r\n"
                        + "是 —— 之前记错了，这个账号的全部历史记录都按 " + PlanInfo.NameOf(a.Plan) + " 算\r\n"
                        + "否 —— 真的升降级了，从现在起按 " + PlanInfo.NameOf(a.Plan) + " 算（历史不变）", "账号与订阅",
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
                    AccountStore.SetCurrent(c2.Id, c2.Plan, "手动");
                }
                AfterAccountChange();
            }
        }

        // 订阅周期里的 API 等价金额（美元）：这个账号名下的全部用量，按官方价格折算。还没算出来 / 没有价格表是 null
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

        // 订阅视图要的数据：每个账号「当前订阅周期」里的用量。在后台读一遍日志（按切换时间线归账号），最多 5 分钟一次，只在订阅视图开着时算
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

        // 订阅视图：每个账号一行。本期用量 = 当前订阅周期里按官方价格折算的 API 等价金额；回本 = 它 ÷ 月费；续费 = 下次续费的日期
        private int DrawSubscriptionRows(Graphics g, float pad, float inner, float s, float colW, float nameW, float y, float rowH, int cap)
        {
            LoadQuota();
            accountHits.Clear();
            List<Account> items = SortedAccounts();
            if (items.Count == 0)
            {
                DrawTextRect(g, "还没有账号档案 · 正在识别当前登录…", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
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
                float x = a == cur ? DrawTierBadge(g, "当前", 4, right, ry, rowH, s) : right;
                DrawTextRect(g, a.Label + " · " + PlanInfo.ShortOf(a.Plan) + (a.BindOrg.Length == 0 ? " · 未绑定" : ""), fSmall, a == cur ? TextMain : TextDim,
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

        // 订阅视图底部：当前账号的订阅周期、本期用量折算、周额度用完预测
        private void DrawSubscriptionInfo(Graphics g, float pad, float inner, float s, float y, float rowH)
        {
            Account a = AccountStore.Current;
            if (a == null)
            {
                DrawTextRect(g, "右键 → 账号与订阅 → 管理账号与订阅…，可以改名字、计划、月费、开始日期", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                return;
            }
            DateTime now = DateTime.Now, cs, ce;
            bool cal;
            AccountStore.BillingCycle(a, now, out cs, out ce, out cal);
            int totalDays = Math.Max(1, (int)Math.Round((ce - cs).TotalDays));
            int day = Math.Max(1, Math.Min(totalDays, (int)Math.Floor((now.Date - cs.Date).TotalDays) + 1));
            string l1 = a.Label + " · " + PlanInfo.NameOf(a.Plan) + " · "
                + (a.Start == DateTime.MinValue ? "开始日期未知" : "订阅 " + a.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 起")
                + " · 本期第 " + day + "/" + totalDays + " 天" + (cal ? "（按自然月）" : "，" + ce.ToString("MM-dd", CultureInfo.InvariantCulture) + " 续费");
            DrawTextRect(g, l1, fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);

            double? cost = SubCost(a.Id);
            string l2;
            if (!cost.HasValue)
            {
                l2 = quota == null ? "本期 API 等价金额：没有价格表（额度换算.csv）" : "本期 API 等价金额：计算中…";
            }
            else
            {
                double c = cost.Value;
                l2 = "本期 API 等价 " + Money(c) + (a.Fee > 0 ? "（月费 " + Money(a.Fee) + " 的 ×" + (c / a.Fee).ToString(c / a.Fee < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "）" : "")
                    + " · 日均 " + Money(c / day) + " · 整期约 " + Money(c / day * totalDays);
            }
            DrawTextRect(g, l2, fSmall, TextMain, new RectangleF(pad, y + rowH, inner, rowH), StringAlignment.Near);

            bool risk;
            string l3 = WeeklyForecast(out risk);
            DrawTextRect(g, l3, fSmall, risk ? Amber : TextDim, new RectangleF(pad, y + 2f * rowH, inner, rowH), StringAlignment.Near);
        }

        // 续费提醒：还有 3 天以内续费，托盘弹一次提示（每个周期一次）。订阅开始日期未知时不提醒
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
                Balloon("订阅即将续费", a.Label + "（" + PlanInfo.NameOf(a.Plan) + "）将在 " + (int)Math.Ceiling(days) + " 天后（" + ce.ToString("MM-dd", CultureInfo.InvariantCulture) + "）续费"
                    + (a.Fee > 0 ? "，月费 " + Money(a.Fee) : "") + (cost.HasValue && a.Fee > 0 ? "。本期用量折合 " + Money(cost.Value) + "（×" + (cost.Value / a.Fee).ToString("0.0", CultureInfo.InvariantCulture) + "）" : "") + "。");
            }
        }

        // ---------------------------------------------------------------- 周额度用完预测
        // 先从额度快照.csv 的末尾读回最近的采样；之后每次取到数据就追加
        private void EnsureWeekHist()
        {
            if (weekHistLoaded)
            {
                return;
            }
            weekHistLoaded = true;
            try
            {
                string path = Path.Combine(AppPaths.DataDir, "额度快照.csv");
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
                return (h / 24).ToString("0.0", CultureInfo.InvariantCulture) + " 天";
            }
            if (h >= 10)
            {
                return Math.Round(h).ToString("0", CultureInfo.InvariantCulture) + " 小时";
            }
            if (h >= 1)
            {
                return h.ToString("0.0", CultureInfo.InvariantCulture) + " 小时";
            }
            return Math.Max(1, (int)Math.Round(h * 60)) + " 分钟";
        }

        // 一句话：按最近的速度，每周额度在重置前会不会用完。整数百分比、数据少时只说「还不够」，不硬猜
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
            string head = "周额度 " + pct.ToString("0", CultureInfo.InvariantCulture) + "%（" + HoursText(hoursLeft) + "后重置）";
            if (p0 == null || (now - p0.T).TotalHours < 1.5)
            {
                return head + " · 预测：数据还不够（需要至少 1.5 小时的采样）";
            }
            double dt = (now - p0.T).TotalHours;
            double dp = pct - p0.Pct;
            if (dp < 1.5)
            {
                return head + " · 近 " + HoursText(dt) + "几乎没动，重置前不会用完";
            }
            double rate = dp / dt;
            double toFull = (100 - pct) / rate;
            if (toFull < hoursLeft)
            {
                risk = true;
                return head + " · 按近 " + HoursText(dt) + "速度约 " + HoursText(toFull) + "后用完，早于重置 " + HoursText(hoursLeft - toFull);
            }
            return head + " · 按近 " + HoursText(dt) + "速度，重置前约到 " + Math.Min(100, pct + rate * hoursLeft).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // 去掉日期后缀：claude-haiku-4-5-20251001 → claude-haiku-4-5
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
                    return t.Title ?? (t.Name + " · 未命名 " + t.ShortId);
                }
            }
            return session.Substring(0, Math.Min(6, session.Length));
        }

        // 这些 token 折算成多少额度百分点（week = true 用每周额度）。approx = 这个模型没有实测过，是按官方价格推算的
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

        // 额度估算的显示：数字小就多留几位；「≈」= 这个模型没有实测过，按官方价格推算；没有换算（没价格 / 没文件）显示「—」
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
            // 带「≈」的前缀占地方，小数少留一位
            string num = pct < 10 ? pct.ToString(approx ? "0.0" : "0.00", CultureInfo.InvariantCulture) : pct.ToString("0", CultureInfo.InvariantCulture);
            return (approx ? "≈" : "") + num + "%";
        }

        // 档位视图 / 合计 / 对比视图用的一行：同一个基础模型（去掉日期后缀）在同一个档位里的合计
        private sealed class TierItem
        {
            public string Model, Tier;
            public long Msgs, Out, Think, Total;
            public double Pct, PctWeek;
            public bool HasPct, Approx;
        }

        // 把 TierRows 合并成「基础模型 × 档位」，并折算额度（模型系数 × 档位修正系数）。按额度从大到小排；没有换算的排在后面
        private List<TierItem> BuildTierItems(UsageTracker.PeriodSummary cur)
        {
            Dictionary<string, TierItem> map = new Dictionary<string, TierItem>();
            Dictionary<string, long[]> toks = new Dictionary<string, long[]>();
            foreach (UsageTracker.TierTotal tt in cur.TierRows)
            {
                if (tt.Total == 0)
                {
                    continue;   // 没有 token 的（例如 <synthetic> 的占位消息）不列
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

        // 这个时段所有用量折算的额度合计（5 小时 / 每周）；任何一个模型是按价格推算的就带「≈」；一个都没有就空着
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

        // 每个基础模型在这个时段的额度估算（各档位加起来）
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

        // 档位视图：每个「模型 × 档位」一行。档位 = 思考强度（low / medium / high / xhigh / max）；开着 Ultracode 的消息单独算 ultra；
        // 快速模式后面加 +fast；记录里没有标注的叫「未标注」。条数 = API 消息数；输出含思考；5h估算 = 折算的额度
        private int DrawTierRows(Graphics g, UsageTracker.PeriodSummary cur, float pad, float inner, float s,
            float colW, float nameW, float y, float rowH, int cap)
        {
            LoadQuota();
            List<TierItem> items = BuildTierItems(cur);
            if (items.Count == 0)
            {
                DrawTextRect(g, "这段时间内没有用量", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
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
                DrawTextRect(g, "其他 " + (items.Count - shown) + " 个", fSmall, TextDim,
                    new RectangleF(pad + 4f * s, y + drawn * rowH, nameW - 4f * s, rowH), StringAlignment.Near);
                DrawRowNumbers(g, pad, inner, colW, y + drawn * rowH, rowH,
                    new string[] { UsageTracker.FormatTokens(rm), UsageTracker.FormatTokens(ro), UsageTracker.FormatTokens(rt), "" }, TextDim);
                drawn++;
            }
            return drawn;
        }

        // 档位的高低（标签样式用）：low < medium < high < xhigh < max < ultra
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

        // 档位小标签（右边缘对齐到 rightX），返回标签的左边缘。low~high：虚线框淡色字；xhigh：实线框；max：实心；ultra：实心橙色。文字空 = 不画
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

        // 一个会话的「模型 + 档位」概要：模型一个就写名字，多个写「首个+n」；档位按首次使用的先后连起来（xhigh→max），太多了写首尾
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
                if (p.Tier.Length > 0 && p.Tier != "未标注" && !tiers.Contains(p.Tier))
                {
                    tiers.Add(p.Tier);
                }
                rank = Math.Max(rank, TierRank(p.Tier));
            }
            modelText = models.Count == 0 ? "" : (models.Count == 1 ? models[0] : models[0] + "+" + (models.Count - 1));
            tierText = tiers.Count == 0 ? "" : (tiers.Count <= 3 ? string.Join("→", tiers.ToArray()) : tiers[0] + "→…→" + tiers[tiers.Count - 1]);
        }

        // 一个 会话 × 模型 × 档位 折算的额度（含档位修正系数）；week = 每周额度。没有换算时是「—」，「≈」= 没实测过
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

        // 一个时间范围：同一天写 HH:mm，跨天带日期
        private static string SpanText(DateTime firstUtc, DateTime lastUtc)
        {
            DateTime a = firstUtc.ToLocalTime(), b = lastUtc.ToLocalTime();
            string f = a.Date == b.Date && a.Date == DateTime.Today ? "HH:mm" : "MM-dd HH:mm";
            return a.ToString(f, CultureInfo.InvariantCulture) + "–" + b.ToString(f, CultureInfo.InvariantCulture);
        }

        // 模型视图：每个会话一行（▸ 点开是它用过的每个 模型 × 档位）。token 是实际数据；「估算」是折算出的额度，只供参考
        private int DrawModelRows(Graphics g, UsageTracker.PeriodSummary cur, float pad, float inner, float s,
            float colW, float nameW, float y, float rowH, int cap)
        {
            LoadQuota();
            chevHits.Clear();
            List<ModelLine> items = BuildModelLines(cur);
            if (items.Count == 0)
            {
                DrawTextRect(g, "这段时间内没有模型用量", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
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
                DrawMoreRow(g, "▾ 其他 " + (groupsLeft > 0 ? groupsLeft + " 个" : (items.Count - rowShown) + " 行"),
                    new string[] { UsageTracker.FormatTokens(rest), "", "", "" }, pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                drawn++;
            }
            else if (rowCollapse)
            {
                DrawMoreRow(g, items.Count > rowShown ? "▴ 收起 · 滚轮翻页" : "▴ 收起", null, pad, inner, s, colW, nameW, y + drawn * rowH, rowH);
                drawn++;
            }
            return drawn;
        }

        // 会话行：[▸] 小圆 标题 ……… 模型 [档位]。窄的时候先丢模型名，再把「xhigh→max」缩成「…→max」，标题最少留 70 个基础单位
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

        // 展开后的子行：缩进到小圆的位置，└ 模型 时间段 ……… [档位]；放不下时先丢时间段
        private void DrawModelPartRow(Graphics g, UsageTracker.ModelTotal mt, float pad, float inner, float s,
            float colW, float nameW, float ry, float rowH)
        {
            float textX = pad + 24f * s;
            DrawTextRect(g, "└", fSmall, TextDim, new RectangleF(pad + 10f * s, ry, 12f * s, rowH), StringAlignment.Center);
            float right = pad + nameW - 6f * s;
            float x = DrawTierBadge(g, mt.Tier == "未标注" ? "" : mt.Tier, TierRank(mt.Tier), right, ry, rowH, s);
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

        // 对比视图：把所有会话按模型合并，看每个模型用了多少 token、占比多少；「5h估算」是它在这段时间里消耗的 5 小时额度百分点
        // （按官方价格折算成美元，再乘以每美元消耗的额度百分点，系数由 额度换算.py 用额度快照拟合）；
        // 「倍数」= 它每百万 token 消耗的额度% ÷ 这几个模型里最便宜的那个（同样多的 token，比最便宜的多耗几倍额度）。
        // 「≈」= 这个模型没有实测过，按官方价格推算；「—」= 没有这个模型的价格
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
                DrawTextRect(g, "这段时间内没有模型用量", fSmall, TextDim, new RectangleF(pad, y, inner, rowH), StringAlignment.Near);
                return 1;
            }
            List<KeyValuePair<string, long>> items = new List<KeyValuePair<string, long>>(byModel);
            items.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });

            // 每百万 token 的 5 小时额度%，最便宜的作为 1 倍
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

            // 占比 = 这个模型的额度估算占所有模型额度估算的比例（没有换算时才退回 token 占比）
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
                DrawTextRect(g, "其他 " + (items.Count - shown) + " 个", fSmall, TextDim,
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
                left = weekly ? "重置时间未知" : "尚未开始计时";
                return;
            }
            DateTime t = q.ResetsAt.Value;
            if (t <= now)
            {
                left = "已到重置时间";
                right = "刷新中…";
                return;
            }
            left = "重置 " + FormatReset(t, now, weekly);
            right = "剩 " + (weekly ? CountdownLong(t - now) : CountdownHm(t - now));
        }

        private static string FormatReset(DateTime t, DateTime now, bool weekly)
        {
            string hm = t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (weekly)
            {
                return t.ToString("MM-dd", CultureInfo.InvariantCulture) + " 周" + Weekday[(int)t.DayOfWeek] + " " + hm;
            }
            int days = (int)(t.Date - now.Date).TotalDays;
            if (days == 0)
            {
                return hm;
            }
            if (days == 1)
            {
                return "明天 " + hm;
            }
            return t.ToString("MM-dd", CultureInfo.InvariantCulture) + " " + hm;
        }

        private static string CountdownHm(TimeSpan t)
        {
            if (t.TotalMinutes < 1)
            {
                return "不到 1 分";
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)t.TotalHours, t.Minutes);
        }

        private static string CountdownLong(TimeSpan t)
        {
            int days = (int)t.TotalDays;
            if (days > 0)
            {
                return days + "天 " + t.Hours + "时";
            }
            if (t.TotalHours >= 1)
            {
                return (int)t.TotalHours + "时 " + t.Minutes + "分";
            }
            return t.Minutes + "分";
        }

        private static string Describe(FetchResult p)
        {
            switch (p.Problem)
            {
                case Problem.NoBridge: return string.IsNullOrEmpty(p.Detail) ? "未收到状态栏数据 · 右键设置" : "等 Claude Code 更新（" + p.Detail + "）";
                case Problem.NoLogin: return "未找到 Claude 登录凭据";
                case Problem.Expired: return "令牌已过期，开一下 Claude Code";
                case Problem.Rejected: return "令牌被拒绝，开一下 Claude Code";
                case Problem.Network: return "网络不通，稍后自动重试";
                case Problem.RateLimited: return "查询太频繁，稍后自动重试";
                default: return "服务暂不可用" + (string.IsNullOrEmpty(p.Detail) ? "" : "（" + p.Detail + "）");
            }
        }

        private static Color LevelColor(double percent)
        {
            if (percent >= 85.0) return Red;
            if (percent >= 60.0) return Amber;
            return Green;
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

                // 背景越透，越需要一圈淡淡的黑色投影，文字才不会糊在桌面上；背景接近实心时不画
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

        // ---------------------------------------------------------------- 交互

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

        // 展开了全部会话、窗口又放不下时，用滚轮上下翻
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
            // 点击切换栏：在简洁模式（只看额度）和详细模式（带用量面板）之间切换
            if (toggleRect.Contains(e.Location))
            {
                SetUsageVisible(!showUsage);
                return;
            }
            // 点击用量面板顶部的时间段标签，切换显示
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
                            OpenCustomRange();   // 点「自定义」标签：先设置时段
                        }
                        Redraw();
                        return;
                    }
                }
            }
            // 拖动卡片里的列分隔线：调整各列宽度（优先于标题和会话行的点击）
            int divider = DividerAt(e.Location);
            if (divider >= 0)
            {
                colDrag = divider;
                colDragX0 = e.X;
                Array.Copy(colFr, colDragStart, 6);
                Capture = true;
                return;
            }
            // 点击面板里表头的标题：依次切换 会话 → 模型 → 对比
            if (showUsage && SegAt(e.Location) >= 0)
            {
                CollapseRows();
                usageMode = SegAt(e.Location);
                Redraw();
                SaveSettings();
                return;
            }
            // 模型视图里点会话行首的 ▸ / ▾：展开或折叠它用过的 模型 × 档位（优先于「点会话行打开明细」）
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
            // 订阅视图里点账号行：把统计切到这个账号（登录绑定了别的账号时会说明原因）
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
            // 点击「其他 N 个」/「收起」：展开全部会话，或者收起
            if (showUsage && moreRect.Contains(e.Location))
            {
                ToggleRows();
                return;
            }
            // 点击用量面板里的会话行：打开它的明细
            string hitSession = SessionAt(e.Location);
            if (hitSession != null)
            {
                OpenDrill(hitSession);
                return;
            }
            // 按在窗口边缘：拖动可以调整大小（上下左右和四个角）
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
            // 在窗口任意位置按住左键即可拖动
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
            SaveSettings();
        }

        // 用量统计放到后台线程做，避免第一次读取较大的历史文件时卡住界面；同一时间只跑一个
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
                    // 窗口已经关了，或者这一轮统计出错，下次再试
                }
                finally
                {
                    Interlocked.Exchange(ref usageBusy, 0);
                }
            });
        }

        // 文字或用量数字变了才重绘
        private void CheckRedraw()
        {
            string sig = Signature(DateTime.Now);
            // 缓存剩余时间每秒都在变，打开统计时每次都重绘
            if (sig != lastSignature || showUsage)
            {
                lastSignature = sig;
                Redraw();
            }
        }

        // 本 5 小时周期的结束时间：额度接口给出的 5 小时重置时间（转成 UTC）；拿不到就返回 null，按滚动 5 小时算
        private DateTime? CycleAnchorUtc()
        {
            if (data == null || data.FiveHour == null || !data.FiveHour.ResetsAt.HasValue)
            {
                return null;
            }
            return data.FiveHour.ResetsAt.Value.ToUniversalTime();
        }

        // 窗口大小：默认随内容；用户拖动边缘调整后记住（基础单位）。最小尺寸保证内容放得下
        private int MinW() { return showUsage ? 420 : 240; }
        private int MinH() { return showUsage ? CompactH() + 196 : CompactH(); }
        private int CurW() { return Math.Max(MinW(), winWu > 0 ? winWu : ContentW()); }
        private int CurH() { return Math.Max(MinH(), winHu > 0 ? winHu : ContentH()); }

        // 鼠标在窗口的哪一侧：1 左、2 右、4 上、8 下（可以组合成四个角）；不在边缘返回 0
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
            if (e == 5 || e == 10) return System.Windows.Forms.Cursors.SizeNWSE;   // 左上、右下
            return System.Windows.Forms.Cursors.SizeNESW;                          // 右上、左下
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

        // 拖动边缘时，按鼠标移动的距离改窗口大小，不小于最小尺寸
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

        // 托盘图标：双击显示或隐藏窗口；菜单里可以刷新或退出
        private void BuildTray()
        {
            ContextMenuStrip tm = new ContextMenuStrip();
            tm.Items.Add("显示 / 隐藏", null, delegate { ToggleVisible(); });
            tm.Items.Add("黑白切换", null, delegate { ToggleInvert(); });
            tm.Items.Add("立即刷新", null, delegate { StartFetch(); });
            tm.Items.Add("设置…", null, delegate { OpenSettings(); });
            tm.Items.Add(new ToolStripSeparator());
            tm.Items.Add("退出", null, delegate { exitRequested = true; Close(); });

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

        // 托盘提示：5 小时和每周的已用百分比（托盘提示最多 63 个字符）
        private void UpdateTrayText()
        {
            if (tray == null)
            {
                return;
            }
            string five = data != null && data.FiveHour != null ? Math.Round(data.FiveHour.Percent).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
            string week = data != null && data.SevenDay != null ? Math.Round(data.SevenDay.Percent).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
            Account acct = AccountStore.Current;
            string text = "Stilla " + (acct != null ? acct.Id + "·" + PlanInfo.ShortOf(acct.Plan) + " · " : "") + "5小时 " + five + " · 每周 " + week;
            if (UpdateState.Available)
            {
                text = "● " + text;
            }
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // 鼠标所在的会话行（用量面板里），没有就返回 null
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

        // 打开某个会话的明细窗口（标题用面板里显示的那个）
        private void OpenDrill(string session)
        {
            string title = session.Substring(0, Math.Min(6, session.Length));
            foreach (UsageTracker.SessionTotal t in PeriodFor(usagePeriod).Rows)
            {
                if (t.Session == session)
                {
                    title = t.Title ?? (t.Name + " · 未命名 " + t.ShortId);
                    break;
                }
            }
            new SessionDrillForm(session, title, logger.DetailPath).Show();
        }

        // 表头三个视图按钮：点到哪个返回它的序号（0 会话 / 1 模型 / 2 对比），没有就是 -1
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

        // 鼠标在哪条列分隔线上（只在卡片的列区域里有效），没有就返回 -1
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

        // 拖动分隔线：分隔线左右两列互相让出宽度，各自不小于最小宽度
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

        // 面板的标签数据：5分钟、15分钟、30分钟、1小时、3小时、本5小时、上5小时来自实时统计（Snapshot.Periods）；
        // 今天、昨天、本周、自定义来自日志文件（logBy）
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
            p.Range = "读取中…";
            return p;
        }

        // 自定义时段的开始和结束（没有设置过，就是最近 24 小时）
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

        // 「时间标签设置」：勾选面板里要显示哪些时间标签（至少一个）。确定后立即生效并保存
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
                logRefreshUtc = DateTime.MinValue;   // 新显示的日志类标签马上重新统计
                SaveSettings();
                StartLogRefresh();
                Redraw();
            }
        }

        // 打开「自定义时段」对话框，确定后按新的时段重新统计
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

        // 「今天、昨天、本周、自定义」需要读日志：一次读完日志，同时算出这四段；在后台线程算，每 60 秒一次，算好再回到界面线程显示
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
            DateTime monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // 本周从周一 0 点开始
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
                    // 日志还没有或正在写入，下一轮再算
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

        // 打开或关闭用量面板：窗口高度跟着变；关闭后不再读任何会话文件
        private void SetUsageVisible(bool on)
        {
            showUsage = on;
            if (usageMenuItem != null)
            {
                usageMenuItem.Checked = on;     // 从切换栏改的，右键菜单里的勾选要跟着变
            }
            if (on)
            {
                // 打开统计时窗口至少是完整面板的大小；关闭时回到默认大小
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

        // 面板变高后如果超出屏幕底部，就往上提一点
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
            menu.Items.Add("立即刷新", null, delegate { StartFetch(); });
            ToolStripMenuItem srcMenu = new ToolStripMenuItem("额度数据来源");
            srcBridgeItem = new ToolStripMenuItem("只用 Claude Code 状态栏（不读登录令牌、不联网）");
            srcBridgeItem.Click += delegate { SetSource(QuotaSource.StatusLine); };
            srcApiItem = new ToolStripMenuItem("在线查询（实验性，读取登录令牌）");
            srcApiItem.Click += delegate { SetSource(QuotaSource.Api); };
            srcAutoItem = new ToolStripMenuItem("自动（默认）：状态栏优先，没有数据时用在线查询");
            srcAutoItem.Click += delegate { SetSource(QuotaSource.Auto); };
            srcMenu.DropDownItems.Add(srcBridgeItem);
            srcMenu.DropDownItems.Add(srcApiItem);
            srcMenu.DropDownItems.Add(srcAutoItem);
            srcMenu.DropDownItems.Add(new ToolStripSeparator());
            srcMenu.DropDownItems.Add("设置 Claude Code 状态栏…", null, delegate { ShowBridgeSetup(); });
            srcMenu.DropDownItems.Add("撤销状态栏设置…", null, delegate { RemoveBridgeSetup(); });
            menu.Items.Add(srcMenu);
            UpdateSourceMenu();

            ToolStripMenuItem rootsMenu = new ToolStripMenuItem("Claude 数据目录");
            rootsMenu.DropDownItems.Add("…");
            rootsMenu.DropDownOpening += delegate
            {
                rootsMenu.DropDownItems.Clear();
                FillRootsMenu(rootsMenu.DropDownItems);
            };
            menu.Items.Add(rootsMenu);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(MakeSlider("背景透明度", 0, bgPercent, delegate(int v) { bgPercent = v; Redraw(); }));
            menu.Items.Add(MakeSlider("文字透明度", FgMin, fgPercent, delegate(int v) { fgPercent = v; Redraw(); }));
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem logItem = new ToolStripMenuItem("记录用量到日志");
            logItem.CheckOnClick = true;
            logItem.Checked = logger.Enabled;
            logItem.Click += delegate { logger.Enabled = logItem.Checked; SaveSettings(); };
            menu.Items.Add(logItem);

            ToolStripMenuItem usageItem = usageMenuItem = new ToolStripMenuItem("显示用量统计");
            usageItem.CheckOnClick = true;
            usageItem.Checked = showUsage;
            usageItem.Click += delegate { SetUsageVisible(usageItem.Checked); };
            menu.Items.Add(usageItem);
            menu.Items.Add("时间标签设置…", null, delegate { OpenTabSettings(); });
            ToolStripMenuItem dsItem = new ToolStripMenuItem("显示 DeepSeek 估价行");
            dsItem.CheckOnClick = true;
            dsItem.Checked = showDeepSeek;
            dsItem.Click += delegate { showDeepSeek = dsItem.Checked; SaveSettings(); Redraw(); };
            menu.Items.Add(dsItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem acctMenu = new ToolStripMenuItem("账号与订阅");
            acctMenu.DropDownItems.Add("…");
            acctMenu.DropDownOpening += delegate
            {
                acctMenu.DropDownItems.Clear();
                FillAccountItems(acctMenu.DropDownItems);
            };
            menu.Items.Add(acctMenu);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem skinMenu = new ToolStripMenuItem("界面风格（Ctrl+Alt+N 切换下一个）");
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
            menu.Items.Add("黑白切换（Ctrl+Alt+B）", null, delegate { ToggleInvert(); });
            topItem = new ToolStripMenuItem("始终置顶");
            topItem.CheckOnClick = true;
            topItem.Checked = TopMost;
            topItem.Click += delegate { TopMost = topItem.Checked; };
            menu.Items.Add(topItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("设置…", null, delegate { OpenSettings(); });
            menu.Items.Add("退出", null, delegate { exitRequested = true; Close(); });

            menu.Closed += delegate { SaveSettings(); };
            ContextMenuStrip = menu;
        }

        // ---------------------------------------------------------------- 设置、更新提醒

        private RectangleF badgeRect = RectangleF.Empty;     // 小红点的可点击范围（没有新版本时为空）
        private SettingsForm settingsForm;

        // Stilla 标志右边的小红点：有新版本时才画
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

        // 启动时检查一次（设置里可以关）。延迟几秒再查，不和启动抢资源
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
                    // 窗口已经关了
                }
            });
        }

        private void AfterUpdateCheck()
        {
            UpdateTrayText();
            Redraw();
            if (UpdateState.Available && UpdateState.Notified != UpdateState.Latest)
            {
                // 每个新版本只弹一次托盘气泡
                UpdateState.Notified = UpdateState.Latest;
                UpdateState.Save();
                Balloon("Stilla 有新版本 " + UpdateState.Latest, "当前 " + AppInfo.Version + "。右键 → 设置… → 前往更新页面；或点 Stilla 标志右边的小红点。");
            }
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.RefreshStatus();
            }
        }

        // ---------------------------------------------------------------- 额度数据来源

        private ToolStripMenuItem srcBridgeItem, srcApiItem, srcAutoItem;

        private void UpdateSourceMenu()
        {
            srcBridgeItem.Checked = QuotaSource.Mode == QuotaSource.StatusLine;
            srcApiItem.Checked = QuotaSource.Mode == QuotaSource.Api;
            srcAutoItem.Checked = QuotaSource.Mode == QuotaSource.Auto;
        }

        // 右键 → Claude 数据目录：列出现在读哪些目录；可以手动加（WSL、别的盘、别人的奇怪位置）
        private void FillRootsMenu(ToolStripItemCollection items)
        {
            int n = 0;
            foreach (string r in ClaudeRoots.All())
            {
                string tag = Directory.Exists(Path.Combine(r, "projects")) ? "" : "（还没有会话记录）";
                ToolStripMenuItem it = new ToolStripMenuItem((n == 0 ? "主目录  " : "") + r + tag);
                it.Enabled = false;
                items.Add(it);
                n++;
            }
            items.Add(new ToolStripSeparator());
            items.Add("添加目录…", null, delegate { AddRootDialog(); });
            items.Add("重新检测", null, delegate { ClaudeRoots.Refresh(); });
            if (ClaudeRoots.Custom().Count > 0)
            {
                items.Add("清除手动添加的目录", null, delegate { ClaudeRoots.ClearCustom(); });
            }
            items.Add("打开 Stilla 数据文件夹", null, delegate { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.Home + "\""); } catch (Exception) { } });
        }

        private void AddRootDialog()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选 Claude Code 的数据文件夹（里面有 projects 文件夹，通常叫 .claude；WSL 里的是 \\\\wsl$\\<发行版>\\home\\<用户>\\.claude）";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                string dir = dlg.SelectedPath;
                if (!ClaudeRoots.Looks(dir))
                {
                    if (MessageBox.Show(this, "这个文件夹里没有找到 projects、sessions 或 .credentials.json，看起来不是 Claude Code 的数据文件夹。仍然添加吗？",
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
            // 已经是「自动」但还没同意在线查询时，再点一次「自动」可以重新弹出风险提示
            if (mode == QuotaSource.Mode && !(mode == QuotaSource.Auto && QuotaSource.OnlineConsent != 1))
            {
                return;
            }
            if (mode == QuotaSource.Api || mode == QuotaSource.Auto)
            {
                DialogResult yes = MessageBox.Show(this,
                    "「在线查询」会读取本机 ~/.claude/.credentials.json 里的 Claude 登录令牌，并用它请求一个没有公开文档的接口（和 Claude Code 的 /usage 用的是同一个）。\r\n\r\n"
                    + "· 令牌只在本机使用，不会发给别人，也不会保存。\r\n"
                    + "· 这个接口随时可能改动或失效；Anthropic 的服务条款对「订阅登录凭据用在 Claude Code 以外的工具里」有限制，是否合适请你自己判断。\r\n"
                    + "· 更稳妥的做法是只用「状态栏」方式（右键 → 额度数据来源 → 设置 Claude Code 状态栏）。注意 VS Code 扩展和桌面端不一定会触发状态栏，这种情况下状态栏方式可能一直没有数据。\r\n\r\n"
                    + "确定要切换到在线查询吗？",
                    "Stilla · 在线查询（实验性）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (yes != DialogResult.Yes)
                {
                    UpdateSourceMenu();
                    return;
                }
            }
            if (mode == QuotaSource.Api || mode == QuotaSource.Auto)
            {
                QuotaSource.OnlineConsent = 1;      // 上面的风险提示点过「是」了
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
                MessageBox.Show(this, "没有找到状态栏小程序：\r\n" + StatusLineBridge.CliPath + "\r\n\r\n请把 Stilla.Statusline.exe 和 Stilla.exe 放在同一个文件夹里。",
                    "Stilla · 设置状态栏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string root = ClaudeRoots.Primary();
            string snippet = StatusLineBridge.SettingsSnippet();
            DialogResult r = MessageBox.Show(this,
                "让 Claude Code 把额度交给 Stilla（官方的 statusLine 功能，只在本机，不读登录令牌）。\r\n\r\n"
                + "【是】自动写入 " + ClaudeSettings.PathIn(root) + "\r\n      会先备份；你已有的状态栏不会被覆盖，会继续显示。\r\n"
                + "【否】只把要加的设置复制到剪贴板，你自己粘贴进 settings.json。\r\n"
                + "【取消】什么都不做。\r\n\r\n"
                + "写好后重启 Claude Code，发一条消息就有数据了。\r\n"
                + "注意：命令行版 Claude Code 才一定会运行状态栏；VS Code 扩展和桌面端不一定（官方没有承诺）。"
                + "遇到这种情况，在「额度数据来源」里选「自动」。",
                "Stilla · 设置状态栏", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                bool ok;
                string msg = ClaudeSettings.Install(root, StatusLineBridge.CliPath, out ok);
                MessageBox.Show(this, msg + (ok ? "\r\n\r\n重启 Claude Code 后生效。" : ""), "Stilla · 设置状态栏", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            else if (r == DialogResult.No)
            {
                try
                {
                    Clipboard.SetText(snippet);
                    MessageBox.Show(this, "已复制到剪贴板：\r\n\r\n" + snippet + "\r\n\r\n粘贴进 " + ClaudeSettings.PathIn(root) + "（已经有 statusLine 的话，见 README「串联已有状态栏」）。",
                        "Stilla · 设置状态栏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception)
                {
                    MessageBox.Show(this, snippet, "Stilla · 设置状态栏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void RemoveBridgeSetup()
        {
            bool changed;
            string msg = ClaudeSettings.Uninstall(ClaudeRoots.Primary(), out changed);
            MessageBox.Show(this, msg + (changed ? "\r\n\r\n重启 Claude Code 后生效。" : ""), "Stilla · 撤销状态栏设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 菜单里嵌一个带数值标签的滑块；拖动时窗口实时变化，松开菜单关闭时统一保存
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

            label.Text = caption + "  " + bar.Value + "%（越右越不透明）";
            bar.ValueChanged += delegate
            {
                label.Text = caption + "  " + bar.Value + "%（越右越不透明）";
                onChange(bar.Value);
            };

            panel.Controls.Add(label);
            panel.Controls.Add(bar);

            ToolStripControlHost host = new ToolStripControlHost(panel);
            host.AutoSize = false;
            host.Size = panel.Size;
            return host;
        }

        // ---------------------------------------------------------------- 位置/设置记忆

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
                    else if (k == "h2" && v > 0) winHu = v;     // v16 起布局变了，旧版本保存的 h 不再使用（否则表格会被挤成一行）
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
                    else if (k == "modelview") usageMode = v != 0 ? 1 : 0;     // 旧版本的设置：只有会话和模型两种
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

    // 墨滴风格的绘制：墨滴图标、液面杯、墨滴双圆。全部用 GDI+ 直接画，不读任何图片文件
    internal static class InkPainter
    {
        private static Bitmap diagramBmp;       // 墨滴双圆的缓存：数据没变就不重新算像素
        private static string diagramKey = "";

        // 用量换算成杯里墨的高度（按对数）：约 1 千万 token 接近空杯，约 20 亿 token 是满杯
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

        // 墨滴图标：一大一小两个椭圆
        public static void DrawLogo(Graphics g, float x, float y, float s, Color ink)
        {
            using (SolidBrush b = new SolidBrush(ink))
            {
                g.FillEllipse(b, x + 0.5f * s, y + 1f * s, 13f * s, 12f * s);
                g.FillEllipse(b, x + 12f * s, y + 2f * s, 9f * s, 8f * s);
            }
        }

        // 液面杯：纸色的圆，下面一部分是墨色，墨的高度 = fill（0~1）；选中时外面再套一圈细线
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

        // 墨滴双圆：两个圆环代表 5 小时和每周（圆环颜色随用量变化）；圆里墨滴的面积 = 已用比例，两滴靠近时粘在一起。
        // 墨滴的边缘由「场」决定：离圆心越近越黑，边界处有 1 像素左右的过渡（抗锯齿）。
        public static void DrawDiagram(Graphics g, RectangleF box, double p5, double pw, float s, Color ink, Color paper, Color soft,
            Color ring5, Color ringW, Font numFont, Font tagFont, string t5, string tw)
        {
            int W = Math.Max(2, (int)Math.Ceiling(box.Width));
            int H = Math.Max(2, (int)Math.Ceiling(box.Height));
            float cy = box.Height / 2f;                              // 局部坐标（相对 box 左上角）
            float R = Math.Min(box.Height * 0.40f, box.Width * 0.20f);   // 圆环半径
            float ax = box.Width * 0.33f, bx = box.Width * 0.67f;
            float ra = R * (float)Math.Sqrt(Pct01(p5));
            float rb = R * (float)Math.Sqrt(Pct01(pw));

            using (SolidBrush sb = new SolidBrush(soft))
            {
                g.FillRectangle(sb, box);
            }

            // 坐标轴和刻度
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

            // 外圈：满额度的参考圆
            using (Pen pa = new Pen(ring5, 1.2f * s))
            {
                g.DrawEllipse(pa, box.X + ax - R, box.Y + cy - R, 2f * R, 2f * R);
            }
            using (Pen pb = new Pen(ringW, 1.2f * s))
            {
                g.DrawEllipse(pb, box.X + bx - R, box.Y + cy - R, 2f * R, 2f * R);
            }

            // 墨滴：数据没变就用缓存的位图
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

            // 数字写在墨滴中间：墨滴够大时用纸色字，否则用墨色字
            DrawCenter(g, t5, numFont, ra >= 13f * s ? paper : ink, box.X + ax, box.Y + cy);
            DrawCenter(g, tw, numFont, rb >= 13f * s ? paper : ink, box.X + bx, box.Y + cy);

            // 标注框：5 小时在左上，每周在右下；引线指向对应的圆
            RectangleF tag5 = DrawTag(g, "5 小时", tagFont, ink, paper, box.X + 3f * s, box.Y + 3f * s, s);
            float wWidth = g.MeasureString("每周", tagFont).Width + 8f * s;
            RectangleF tagW = DrawTag(g, "每周", tagFont, ink, paper, box.Right - 3f * s - wWidth, box.Bottom - 3f * s - tag5.Height, s);
            using (Pen lp = new Pen(Color.FromArgb(150, ink), 1f * s))
            {
                float d = R * 0.7071f;
                g.DrawLine(lp, tag5.Right, tag5.Bottom, box.X + ax - d, box.Y + cy - d);
                g.DrawLine(lp, tagW.Left, tagW.Top, box.X + bx + d, box.Y + cy + d);
            }
        }

        // 把墨滴场算成一张带透明度的位图（ARGB），再一次性拷进去
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

        // 标注框：纸色底、墨色细框、墨色字
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
                    // 演示模式：假额度，设置放临时目录，不碰真实设置（截图 / 测试用）
                    Environment.SetEnvironmentVariable("STILLA_DEMO", "1");
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STILLA_HOME")))
                    {
                        Environment.SetEnvironmentVariable("STILLA_HOME", Path.Combine(Path.GetTempPath(), "Stilla-demo"));
                    }
                }
            }
            bool created;
            // 互斥体名带上数据目录：不同数据目录（便携版 / 演示）可以同时开
            string mutexName = "Local\\Stilla_" + (AppPaths.Home.ToLowerInvariant().GetHashCode() & 0x7fffffff).ToString("x");
            using (Mutex single = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    return; // 已经开着一个了
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
