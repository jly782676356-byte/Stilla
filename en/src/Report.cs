// Weekly report: puts one week of usage (Monday 0:00 to Sunday 24:00, local time) into one HTML file (logs\Weekly report\weekly_report_<Monday date>.html); double-click to open it in a browser.
// Reads only the usage detail / quota conversion table / account timeline; touches no token and uses no network. Contents:
//   overview (API-equivalent cost, converted quota, change vs last week), daily bars, hourly heatmap, model x tier, the sessions that burned the most quota,
//   reloads after cache expiry (estimate), estimate factors (measured or derived), account / plan switches.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Stilla
{
    internal static class WeeklyReport
    {
        private static readonly string[] Wd = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        public static string PathFor(string logDir, DateTime mondayLocal)
        {
            return Path.Combine(logDir, "weekly_reports", "weekly_report_" + mondayLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".html");
        }

        private class Acc
        {
            public long Msgs;
            public double Usd, P5, Pw;
            public long Tokens, Out;
        }

        private sealed class SessAcc : Acc
        {
            public string Id = "";
            public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;
            public int Reloads;
            public double ReloadUsd;
        }

        private sealed class Prev
        {
            public DateTime T;
            public long Ctx;
            public int Ttl;
        }

        private static string BaseModel(string m)
        {
            return System.Text.RegularExpressions.Regex.Replace(m ?? "", @"-\d{8}$", "");
        }

        private static string F1(double v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Usd(double v)
        {
            return "$" + (v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture));
        }

        private static string Tok(long v)
        {
            return UsageTracker.FormatTokens(v);
        }

        private static string H(string s)
        {
            return WebUtility.HtmlEncode(s ?? "");
        }

        // partial = this week is not over yet (the current week)
        public static string Generate(string logDir, DateTime start, DateTime end, bool partial)
        {
            string detail = Path.Combine(logDir, "usage_detail_v2.csv");
            string quotaPath = Path.Combine(logDir, "quota_conversion.csv");
            if (!File.Exists(detail))
            {
                throw new FileNotFoundException("No usage log (logs\\usage_detail_v2.csv)");
            }
            TagStore.Refresh();
            AccountIndex idx = AccountStore.Index();
            Dictionary<string, QuotaTable> tables = new Dictionary<string, QuotaTable>();
            QuotaTable anyQ = null;

            // Session titles: reuse the existing period statistics (the same set as in the panel)
            Dictionary<string, string> titles = new Dictionary<string, string>();
            try
            {
                UsageTracker.PeriodSummary[] ps = LogSummary.ComputeMany(detail, Path.Combine(logDir, "session_index.csv"), new DateTime[] { start }, new DateTime[] { end }, new string[] { "Week" });
                foreach (UsageTracker.SessionTotal st in ps[0].Rows)
                {
                    titles[st.Session] = st.Title != null ? st.Title : st.Name + " · " + st.ShortId;
                }
            }
            catch (Exception)
            {
            }

            DateTime prevStart = start.AddDays(-7);
            double prevUsd = 0;
            long prevMsgs = 0;
            Acc total = new Acc();
            Acc[] day = new Acc[7];
            for (int i = 0; i < 7; i++)
            {
                day[i] = new Acc();
            }
            double[,] heat = new double[7, 24];
            Dictionary<string, Acc> byModel = new Dictionary<string, Acc>();
            Dictionary<string, SessAcc> bySess = new Dictionary<string, SessAcc>();
            Dictionary<string, Prev> prevMsg = new Dictionary<string, Prev>();
            Dictionary<string, double> usedByKeyModel = new Dictionary<string, double>();
            Dictionary<string, long> keyMsgs = new Dictionary<string, long>();
            long unknownPct = 0;
            int reloads = 0;
            double reloadUsd = 0;

            using (FileStream fs = new FileStream(detail, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    int comma = line.IndexOf(',');
                    DateTime t;
                    if (comma <= 0 || !DateTime.TryParseExact(line.Substring(0, comma), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        continue;
                    }
                    if (t < prevStart || t >= end)
                    {
                        continue;
                    }
                    string[] f = LogSummary.SplitCsv(line);
                    if (f.Length < 11 || f[3].Length == 0 || f[3].StartsWith("<", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    long inp = LogSummary.ToLong(f[4]), outp = LogSummary.ToLong(f[5]), cw5 = LogSummary.ToLong(f[6]), cw1 = LogSummary.ToLong(f[7]), cr = LogSummary.ToLong(f[8]);
                    long[] tok = new long[10];
                    UsageTracker.AddTokens(tok, inp, outp, cw5, cw1, cr);
                    DateTime utc = t.ToUniversalTime();
                    string key = idx.Empty ? "" : idx.AccountAt(utc) + ":" + idx.PlanAt(utc);
                    QuotaTable q;
                    if (!tables.TryGetValue(key, out q))
                    {
                        q = QuotaTable.Load(quotaPath, key);
                        tables[key] = q;
                    }
                    if (q != null && anyQ == null)
                    {
                        anyQ = q;
                    }
                    QuotaTable.Entry e = null;
                    if (q != null)
                    {
                        q.Models.TryGetValue(BaseModel(f[3]), out e);
                    }
                    if (e == null && anyQ != null)
                    {
                        anyQ.Models.TryGetValue(BaseModel(f[3]), out e);
                    }
                    double usd = e != null ? (q ?? anyQ).CostUsdAt(e, tok, 1.0) : 0;
                    if (t < start)
                    {
                        prevUsd += usd;
                        prevMsgs++;
                        continue;
                    }
                    string tier = TagStore.TierKey(TagStore.Get(f[10]));
                    double p5 = 0, pw = 0;
                    bool haveK = false;
                    if (q != null && !q.Stale && e != null)
                    {
                        QuotaTable.Entry e2;
                        if (q.Models.TryGetValue(BaseModel(f[3]), out e2) && e2.K5 > 0)
                        {
                            p5 = q.CostUsd(e2, tok) * e2.K5 * q.TierFactor(tier, false);
                            pw = q.CostUsd(e2, tok) * e2.KW * q.TierFactor(tier, true);
                            haveK = true;
                        }
                    }
                    if (!haveK)
                    {
                        unknownPct++;
                    }
                    long all = inp + outp + cw5 + cw1 + cr;
                    int d = (int)((t.Date - start.Date).TotalDays);
                    if (d < 0 || d > 6)
                    {
                        continue;
                    }
                    Add(total, usd, p5, pw, all, outp);
                    Add(day[d], usd, p5, pw, all, outp);
                    heat[d, t.Hour] += p5;
                    string mk = BaseModel(f[3]) + "\n" + tier;
                    Acc ma;
                    if (!byModel.TryGetValue(mk, out ma))
                    {
                        ma = new Acc();
                        byModel[mk] = ma;
                    }
                    Add(ma, usd, p5, pw, all, outp);
                    SessAcc sa;
                    if (!bySess.TryGetValue(f[1], out sa))
                    {
                        sa = new SessAcc();
                        sa.Id = f[1];
                        bySess[f[1]] = sa;
                    }
                    Add(sa, usd, p5, pw, all, outp);
                    if (t < sa.First)
                    {
                        sa.First = t;
                    }
                    if (t > sa.Last)
                    {
                        sa.Last = t;
                    }
                    string kk = key + "\n" + BaseModel(f[3]);
                    double kv;
                    usedByKeyModel.TryGetValue(kk, out kv);
                    usedByKeyModel[kk] = kv + usd;
                    long kc;
                    keyMsgs.TryGetValue(key, out kc);
                    keyMsgs[key] = kc + 1;

                    // Reload after cache expiry: in a main session, the gap between two messages is longer than the previous message's cache lifetime, and this message wrote almost the whole context into the cache again
                    if (f[9] != "1")
                    {
                        Prev pv;
                        long ctx = inp + cw5 + cw1 + cr;
                        if (prevMsg.TryGetValue(f[1], out pv) && pv.Ctx >= 50000 && (t - pv.T).TotalMinutes > pv.Ttl + 1 && (cw5 + cw1) >= 0.6 * pv.Ctx && e != null)
                        {
                            double extra = (cw5 * (e.Price[2] - e.Price[4]) + cw1 * (e.Price[3] - e.Price[4])) / 1000000.0;
                            if (extra > 0)
                            {
                                reloads++;
                                reloadUsd += extra;
                                sa.Reloads++;
                                sa.ReloadUsd += extra;
                            }
                        }
                        Prev np = new Prev();
                        np.T = t;
                        np.Ctx = ctx;
                        np.Ttl = cw1 > 0 ? 60 : 5;
                        prevMsg[f[1]] = np;
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            string title = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ~ " + end.AddDays(-1).ToString("MM-dd", CultureInfo.InvariantCulture) + " Weekly usage report";
            // Content security policy: this page contains only text and inline styles, no scripts and no external resources (even if an escape is missed somewhere later, no script can run and no request can be sent)
            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>" + H(title) + "</title><style>");
            sb.Append(":root{--bg:#fafaf7;--fg:#22201c;--dim:#8a857b;--line:#e3e0d8;--card:#fff;--acc:#c4623a;--warn:#c0392b}");
            sb.Append("@media(prefers-color-scheme:dark){:root{--bg:#161513;--fg:#e8e4da;--dim:#8f8a80;--line:#2e2c28;--card:#1e1d1a;--acc:#e08a63;--warn:#ef6b5b}}");
            sb.Append("body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.55 'Microsoft YaHei UI',system-ui,sans-serif}");
            sb.Append("main{max-width:900px;margin:0 auto;padding:28px 20px 60px}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:30px 0 10px;border-bottom:1px solid var(--line);padding-bottom:6px}");
            sb.Append(".sub{color:var(--dim);font-size:12px}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin-top:14px}");
            sb.Append(".card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 12px}.card b{display:block;font-size:20px}.card span{color:var(--dim);font-size:12px}");
            sb.Append("table{border-collapse:collapse;width:100%;background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}");
            sb.Append("th,td{padding:6px 10px;text-align:right;border-bottom:1px solid var(--line);font-size:13px}th:first-child,td:first-child{text-align:left}th{color:var(--dim);font-weight:500}");
            sb.Append(".bar{display:inline-block;height:8px;background:var(--acc);border-radius:4px;vertical-align:middle}.heat td{padding:0;border:1px solid var(--bg);height:18px;min-width:18px}");
            sb.Append(".heat th{padding:2px 6px;font-size:11px}.note{color:var(--dim);font-size:12px;margin-top:6px}.warn{color:var(--warn)}</style></head><body><main>");
            sb.Append("<h1>" + H(title) + "</h1><div class=\"sub\">Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + (partial ? " · This week is not over yet; the numbers are up to the time of generation" : "") + " · Amounts are API-equivalent at official prices, not what your subscription actually charges; quota percentage points are estimates converted with this machine's estimate factors</div>");

            // Overview
            int activeHours = 0;
            double peakV = 0;
            int peakD = 0, peakH = 0;
            for (int d = 0; d < 7; d++)
            {
                for (int h = 0; h < 24; h++)
                {
                    if (heat[d, h] > 0.05)
                    {
                        activeHours++;
                    }
                    if (heat[d, h] > peakV)
                    {
                        peakV = heat[d, h];
                        peakD = d;
                        peakH = h;
                    }
                }
            }
            string vs = prevUsd > 0.01 ? (total.Usd >= prevUsd ? "vs last week +" : "vs last week -") + Math.Abs((total.Usd / prevUsd - 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "% (last week " + Usd(prevUsd) + ")" : "No usage last week";
            sb.Append("<div class=\"cards\">");
            Card(sb, Usd(total.Usd), "API-equivalent cost · " + vs);
            Card(sb, F1(total.P5 / 100.0), "5-hour quota windows used (" + F1(total.P5) + "%)");
            Card(sb, F1(total.Pw) + "%", "weekly quota used (estimate)");
            Card(sb, Tok(total.Tokens), "tokens · " + total.Msgs + " messages");
            Card(sb, bySess.Count.ToString(CultureInfo.InvariantCulture), "Active sessions · " + activeHours + " active hours");
            Card(sb, peakV > 0 ? Wd[peakD] + " " + peakH.ToString("00", CultureInfo.InvariantCulture) + ":00" : "—", peakV > 0 ? "Peak hour (" + F1(peakV) + "% of a 5-hour quota)" : "No usage");
            sb.Append("</div>");
            if (unknownPct > 0)
            {
                sb.Append("<div class=\"note warn\">Messages without a quota factor: " + unknownPct + " (the account:plan at the time has no fit of its own yet); they are counted in dollars only and not converted into quota percentage points.</div>");
            }

            // Daily
            sb.Append("<h2>Daily</h2>");
            double maxDay = 0.01;
            for (int d = 0; d < 7; d++)
            {
                maxDay = Math.Max(maxDay, day[d].Usd);
            }
            sb.Append("<table><tr><th>Date</th><th>API-equivalent</th><th>5-hour quota %</th><th>Weekly quota %</th><th>Messages</th><th style=\"width:30%\"></th></tr>");
            for (int d = 0; d < 7; d++)
            {
                DateTime dd = start.AddDays(d);
                sb.Append("<tr><td>" + Wd[d] + " " + dd.ToString("MM-dd", CultureInfo.InvariantCulture) + "</td><td>" + Usd(day[d].Usd) + "</td><td>" + F1(day[d].P5) + "</td><td>" + F1(day[d].Pw) + "</td><td>" + day[d].Msgs
                    + "</td><td style=\"text-align:left\"><span class=\"bar\" style=\"width:" + (int)(day[d].Usd / maxDay * 100) + "%\"></span></td></tr>");
            }
            sb.Append("</table>");

            // Heatmap
            sb.Append("<h2>5-hour quota used per hour (darker = more)</h2><table class=\"heat\"><tr><th></th>");
            for (int h = 0; h < 24; h++)
            {
                sb.Append("<th>" + (h % 3 == 0 ? h.ToString("00", CultureInfo.InvariantCulture) : "") + "</th>");
            }
            sb.Append("</tr>");
            for (int d = 0; d < 7; d++)
            {
                sb.Append("<tr><th>" + Wd[d] + "</th>");
                for (int h = 0; h < 24; h++)
                {
                    double v = heat[d, h];
                    double a = peakV > 0 ? Math.Min(1.0, v / peakV) : 0;
                    sb.Append("<td title=\"" + Wd[d] + " " + h.ToString("00", CultureInfo.InvariantCulture) + ":00 · " + F1(v) + "%\" style=\"background:rgba(196,98,58," + (v > 0.05 ? (0.12 + 0.88 * a).ToString("0.00", CultureInfo.InvariantCulture) : "0") + ")\"></td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");

            // Model x tier
            sb.Append("<h2>Model × tier</h2><table><tr><th>Model · tier</th><th>Messages</th><th>Tokens</th><th>Output</th><th>API-equivalent</th><th>5-hour quota %</th><th style=\"width:20%\">Cost share</th></tr>");
            List<KeyValuePair<string, Acc>> ml = new List<KeyValuePair<string, Acc>>(byModel);
            ml.Sort(delegate(KeyValuePair<string, Acc> a, KeyValuePair<string, Acc> b) { return b.Value.Usd.CompareTo(a.Value.Usd); });
            foreach (KeyValuePair<string, Acc> kv in ml)
            {
                string[] mt = kv.Key.Split('\n');
                double share = total.Usd > 0 ? kv.Value.Usd / total.Usd : 0;
                sb.Append("<tr><td>" + H(mt[0]) + " · " + H(mt[1]) + "</td><td>" + kv.Value.Msgs + "</td><td>" + Tok(kv.Value.Tokens) + "</td><td>" + Tok(kv.Value.Out) + "</td><td>" + Usd(kv.Value.Usd) + "</td><td>" + F1(kv.Value.P5)
                    + "</td><td style=\"text-align:left\"><span class=\"bar\" style=\"width:" + (int)(share * 100) + "%\"></span> " + (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%</td></tr>");
            }
            sb.Append("</table>");

            // Sessions
            sb.Append("<h2>Top 10 sessions by quota used</h2><table><tr><th>Session</th><th>API-equivalent</th><th>5-hour quota %</th><th>Tokens</th><th>Messages</th><th>Reloads</th></tr>");
            List<SessAcc> sl = new List<SessAcc>(bySess.Values);
            sl.Sort(delegate(SessAcc a, SessAcc b) { return b.Usd.CompareTo(a.Usd); });
            for (int i = 0; i < sl.Count && i < 10; i++)
            {
                string name;
                if (!titles.TryGetValue(sl[i].Id, out name))
                {
                    name = sl[i].Id.Substring(0, Math.Min(8, sl[i].Id.Length));
                }
                sb.Append("<tr><td>" + H(name) + "<div class=\"sub\">" + sl[i].First.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " ~ " + sl[i].Last.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                    + "</div></td><td>" + Usd(sl[i].Usd) + "</td><td>" + F1(sl[i].P5) + "</td><td>" + Tok(sl[i].Tokens) + "</td><td>" + sl[i].Msgs + "</td><td>" + (sl[i].Reloads > 0 ? sl[i].Reloads + "x" : "") + "</td></tr>");
            }
            sb.Append("</table>");

            // Cache reloads
            sb.Append("<h2>Reloads after the cache expired (estimate)</h2>");
            if (reloads > 0)
            {
                sb.Append("<p>Sessions woken up after their cache expired this week: <b>" + reloads + "</b>. Each time, the whole context was written into the cache again, costing about <b>" + Usd(reloadUsd)
                    + "</b> more in total than resuming while the cache was still alive (API-equivalent amount; share of this week's cost: about " + (total.Usd > 0 ? (reloadUsd / total.Usd * 100).ToString("0", CultureInfo.InvariantCulture) : "0") + "%). </p>");
                sb.Append("<div class=\"note\">How it is detected: in a main session, the gap between two messages is longer than the cache lifetime of the previous one (1 hour if it wrote a 1-hour cache, otherwise 5 minutes), and this message's cache write is at least 60% of the previous context, with a context of at least 50k tokens. This is an estimate, not an exact value. "
                    + "The cache countdown in the panel plus the system notification exist to help you avoid this.</div>");
            }
            else
            {
                sb.Append("<p>No reloads after a cache expiry were detected.</p>");
            }

            // Factors
            sb.Append("<h2>Estimate factors</h2><table><tr><th>Account:plan</th><th>Messages this week</th><th>Main model</th><th>5-hour factor (%/USD)</th><th>Source</th></tr>");
            foreach (KeyValuePair<string, long> kv in keyMsgs)
            {
                QuotaTable q;
                tables.TryGetValue(kv.Key, out q);
                string topModel = "";
                double topUsd = -1;
                foreach (KeyValuePair<string, double> u in usedByKeyModel)
                {
                    string[] p = u.Key.Split('\n');
                    if (p[0] == kv.Key && u.Value > topUsd)
                    {
                        topUsd = u.Value;
                        topModel = p[1];
                    }
                }
                string kText = "—", src = "No fit of its own yet";
                QuotaTable.Entry te;
                if (q != null && !q.Stale && q.Models.TryGetValue(topModel, out te) && te.K5 > 0)
                {
                    kText = te.K5.ToString("0.000", CultureInfo.InvariantCulture);
                    src = q.PlanDerived ? "<span class=\"warn\">Derived from the plan multiplier (unreliable)</span>" : "Measured" + (q.CalibErr >= 0 ? ", prediction error ~" + (q.CalibErr * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "");
                }
                sb.Append("<tr><td>" + H(kv.Key.Length == 0 ? "(no per-account split)" : kv.Key) + "</td><td>" + kv.Value + "</td><td>" + H(topModel) + "</td><td>" + kText + "</td><td style=\"text-align:left\">" + src + "</td></tr>");
            }
            sb.Append("</table>");

            // Account / plan switches
            List<TimelineEntry> tl = AccountStore.TimelineCopy();
            DateTime su = start.ToUniversalTime(), eu = end.ToUniversalTime();
            List<string> sw = new List<string>();
            foreach (TimelineEntry te in tl)
            {
                if (te.Utc >= su && te.Utc < eu)
                {
                    sw.Add(te.Utc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " switched to " + te.AccountId + " · " + PlanInfo.NameOf(te.Plan) + " (" + te.Source + ")");
                }
            }
            if (sw.Count > 0)
            {
                sb.Append("<h2>Account / plan switches</h2><ul>");
                foreach (string x in sw)
                {
                    sb.Append("<li>" + H(x) + "</li>");
                }
                sb.Append("</ul>");
            }
            sb.Append("</main></body></html>");

            string path = PathFor(logDir, start);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
            return path;
        }

        private static void Add(Acc a, double usd, double p5, double pw, long tokens, long outp)
        {
            a.Msgs++;
            a.Usd += usd;
            a.P5 += p5;
            a.Pw += pw;
            a.Tokens += tokens;
            a.Out += outp;
        }

        private static void Card(StringBuilder sb, string big, string small)
        {
            sb.Append("<div class=\"card\"><b>" + H(big) + "</b><span>" + H(small) + "</span></div>");
        }
    }
}
