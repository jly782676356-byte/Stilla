// Layout QA scenarios for the 0.2.0 usage panel (status lights, jump arrows, speed / share columns, sort carets,
// forecast row, health badge, API / Trend views, the ⋮ button, hover tips, dialogs).
//   run.ps1 -Scenario q_default | q_simple | q_views | q_every | q_sizes | q_scale | q_edge | q_dialogs | q_tips | q_all
// Everything is drawn from FAKE data built in memory (sessions, history, price table from make_demo_data.py --rich).
// Besides the PNG sheets every scenario writes issues_<scenario>.txt: each text that is clipped ("xxx..."), overlapped or
// touching the window border, found by UiProbe while the widget draws itself.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace Stilla
{
    internal static partial class Harness
    {
        // ------------------------------------------------------------------ fake sessions
        private sealed class Msg
        {
            public DateTime Utc;
            public string Model, Tier;
            public readonly long[] Tok = new long[10];
        }

        private sealed class Fs
        {
            public string Id, Title, Project, Model, Tier, Tier2;
            public int State;           // 0 running, 1 waiting for you, 2 fresh (white), 3 soon (yellow), 4 urgent (red), 5 expired, 6 no cache
            public int StartMin, LastMin;
            public double W;            // weight: how heavy the session is
            public int Sub;
            public bool Watch;
        }

        private sealed class Ds
        {
            public string Name;
            public UsageTracker.Snapshot Snap;
            public readonly UsageTracker.PeriodSummary[] LogBy = new UsageTracker.PeriodSummary[11];
            public Dictionary<string, List<UsageTracker.RecentUse>> Hist = new Dictionary<string, List<UsageTracker.RecentUse>>();
            public List<LogSummary.ApiTotal> Api = new List<LogSummary.ApiTotal>();
            public readonly Dictionary<string, UsageTracker.PeriodSummary> Sub = new Dictionary<string, UsageTracker.PeriodSummary>();
            public List<Fs> Sessions;
        }

        private static bool Zh
        {
            get { return Feat.ProfileNames[0].Length > 0 && Feat.ProfileNames[0][0] > '⺀'; }
        }

        private static readonly string[] TitlesZh =
        {
            "重构登录模块", "修复偶发测试失败", "整理发布说明", "一个特别特别长的会话标题用来测试省略号显示是否正常", "探索数据管道", null, "升级依赖并跑一遍回归测试", "评审设计稿"
        };

        private static readonly string[] TitlesEn =
        {
            "Refactor the login module", "Fix the flaky test", "Write the release notes", "A very very long session title used to check that the ellipsis shows up properly", "Explore the data pipeline", null,
            "Upgrade dependencies and run the regression tests", "Review the design draft"
        };

        private const string LongNoSpace = "ThisIsAVeryLongSessionTitleWithoutAnySpacesToCheckTheTruncationOfTheTableRow_v2_final_FINAL_really";
        private const string LongCjk = "这是一个没有任何空格的超长中文会话标题用来检查表格行在名字列很窄时的截断和省略号是否正常工作而且还要继续写下去";

        private static string[] Titles
        {
            get { return Zh ? TitlesZh : TitlesEn; }
        }

        private static Random rnd0 = new Random(4711);

        private static List<Fs> MakeSessions(int count, int style)
        {
            // style 0 = normal mix, 1 = long titles, 2 = CJK titles (long, no spaces), 3 = all running
            string[] projects = { "demo-app", "api-server", "docs-site", "mobile", "infra" };
            string[] models = { "claude-sonnet-5-5", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5" };
            string[] tiers = { "high", "xhigh", "medium", "low", "high", "max" };
            // state, start, last, weight, sub
            int[][] spec =
            {
                new int[] { 0, 215, 0, 14, 2 },     // running, heavy, 2 sub-agents
                new int[] { 0, 160, 0, 10, 0 },
                new int[] { 1, 180, 38, 5, 0 },     // waiting for you
                new int[] { 2, 120, 13, 7, 1 },     // fresh 47 min
                new int[] { 3, 205, 42, 4, 0 },     // soon 18 min
                new int[] { 4, 150, 54, 3, 0 },     // urgent 6 min
                new int[] { 5, 200, 80, 2, 0 },     // expired
                new int[] { 6, 60, 50, 1, 0 }       // no cache at all
            };
            List<Fs> list = new List<Fs>();
            for (int i = 0; i < count; i++)
            {
                int[] sp = spec[i % spec.Length];
                Fs f = new Fs();
                f.Id = "s" + i.ToString("000") + "ff-0000-4000-8000-" + (100000000000L + i * 7919).ToString();
                string t = Titles[i % Titles.Length];
                if (style == 1)
                {
                    t = i % 3 == 0 ? LongNoSpace : Titles[3];
                }
                else if (style == 2)
                {
                    t = i % 2 == 0 ? LongCjk : TitlesZh[i % TitlesZh.Length];
                }
                if (t != null && i >= spec.Length)
                {
                    t += " (" + (i + 1) + ")";
                }
                f.Title = t;
                f.Project = projects[i % projects.Length];
                f.Model = models[i % models.Length];
                f.Tier = tiers[i % tiers.Length];
                f.Tier2 = i % 4 == 0 ? "max" : null;
                f.State = style == 3 ? 0 : sp[0];
                f.StartMin = sp[1];
                f.LastMin = style == 3 ? 0 : sp[2];
                f.W = sp[3] / 10.0 * (i >= spec.Length ? 0.35 + (i % 5) * 0.1 : 1.0);
                f.Sub = sp[4];
                f.Watch = i == 2 || i == 5;
                list.Add(f);
            }
            return list;
        }

        private static List<Msg> MakeMsgs(Fs f, DateTime now, Random r)
        {
            List<Msg> res = new List<Msg>();
            double span = f.StartMin - f.LastMin;
            int n = Math.Max(4, (int)(20.0 * f.W * span / 60.0));
            for (int i = 0; i < n; i++)
            {
                double minAgo = i == n - 1 ? f.LastMin : f.LastMin + r.NextDouble() * span;
                Msg m = new Msg();
                m.Utc = now.AddMinutes(-minAgo);
                m.Model = f.Model;
                m.Tier = f.Tier2 != null && minAgo < f.LastMin + span * 0.4 ? f.Tier2 : f.Tier;
                m.Tok[0] = 12 + r.Next(30);
                m.Tok[1] = (long)(f.W * 1400 * (0.4 + r.NextDouble() * 1.4));
                m.Tok[3] = (long)(f.W * 5200 * (0.3 + r.NextDouble() * 1.7));
                m.Tok[4] = (long)(f.W * 70000 * (0.5 + r.NextDouble()));
                res.Add(m);
            }
            for (int k = 0; k < f.Sub * 6; k++)
            {
                Msg m = new Msg();
                m.Utc = now.AddMinutes(-(f.LastMin + r.NextDouble() * span));
                m.Model = "claude-haiku-5-5";
                m.Tier = "low";
                m.Tok[0] = 8;
                m.Tok[1] = 420 + r.Next(500);
                m.Tok[3] = 2600 + r.Next(2000);
                m.Tok[4] = 30000 + r.Next(30000);
                res.Add(m);
            }
            res.Sort(delegate(Msg a, Msg b) { return a.Utc.CompareTo(b.Utc); });
            return res;
        }

        private static string Clock(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private static UsageTracker.PeriodSummary FillPeriod(string label, DateTime from, DateTime to, List<Fs> fs, List<List<Msg>> msgs, DateTime now, string range)
        {
            UsageTracker.PeriodSummary p = new UsageTracker.PeriodSummary();
            p.Label = label;
            p.Range = range;
            p.From = from;
            p.To = to;
            Dictionary<string, UsageTracker.ModelTotal> byM = new Dictionary<string, UsageTracker.ModelTotal>();
            Dictionary<string, UsageTracker.TierTotal> byT = new Dictionary<string, UsageTracker.TierTotal>();
            for (int i = 0; i < fs.Count; i++)
            {
                Fs f = fs[i];
                UsageTracker.SessionTotal st = null;
                foreach (Msg m in msgs[i])
                {
                    if (m.Utc < from || m.Utc >= to)
                    {
                        continue;
                    }
                    if (st == null)
                    {
                        st = new UsageTracker.SessionTotal();
                        st.Session = f.Id;
                        st.Name = f.Project;
                        st.Title = f.Title;
                        st.SubCount = f.Sub;
                        st.Active = f.State == 0;
                        if (f.State <= 5)
                        {
                            st.CacheExpiryUtc = f.State == 0 ? now.AddMinutes(59) : now.AddMinutes(60 - f.LastMin);
                        }
                    }
                    long all = m.Tok[0] + m.Tok[1] + m.Tok[2] + m.Tok[3] + m.Tok[4];
                    st.Total += all;
                    st.Input += m.Tok[0];
                    st.Output += m.Tok[1];
                    st.CacheWrite += m.Tok[2] + m.Tok[3];
                    st.CacheRead += m.Tok[4];
                    if (m.Utc > st.LastUtc)
                    {
                        st.LastUtc = m.Utc;
                    }
                    p.Total += all;
                    p.Input += m.Tok[0];
                    p.Output += m.Tok[1];
                    p.CacheWrite += m.Tok[2] + m.Tok[3];
                    p.CacheRead += m.Tok[4];
                    string mk = f.Id + "\n" + m.Model + "\n" + m.Tier;
                    UsageTracker.ModelTotal mt;
                    if (!byM.TryGetValue(mk, out mt))
                    {
                        mt = new UsageTracker.ModelTotal();
                        mt.Session = f.Id;
                        mt.Model = m.Model;
                        mt.Tier = m.Tier;
                        byM[mk] = mt;
                    }
                    mt.Msgs++;
                    mt.Think += m.Tok[1] / 3;
                    mt.Total += all;
                    mt.CacheRead += m.Tok[4];
                    if (m.Utc < mt.FirstUtc) mt.FirstUtc = m.Utc;
                    if (m.Utc > mt.LastUtc) mt.LastUtc = m.Utc;
                    string tk = m.Model + "\n" + m.Tier;
                    UsageTracker.TierTotal tt;
                    if (!byT.TryGetValue(tk, out tt))
                    {
                        tt = new UsageTracker.TierTotal();
                        tt.Model = m.Model;
                        tt.Tier = m.Tier;
                        byT[tk] = tt;
                    }
                    tt.Msgs++;
                    tt.Total += all;
                    tt.Out += m.Tok[1];
                    tt.Think += m.Tok[1] / 3;
                    for (int q = 0; q < 10; q++)
                    {
                        mt.Tok[q] += m.Tok[q];
                        tt.Tok[q] += m.Tok[q];
                    }
                }
                if (st != null)
                {
                    p.Rows.Add(st);
                }
            }
            p.Rows.Sort(delegate(UsageTracker.SessionTotal a, UsageTracker.SessionTotal b) { return b.Total.CompareTo(a.Total); });
            p.ModelRows.AddRange(byM.Values);
            p.ModelRows.Sort(delegate(UsageTracker.ModelTotal a, UsageTracker.ModelTotal b) { return b.Total.CompareTo(a.Total); });
            p.TierRows.AddRange(byT.Values);
            p.TierRows.Sort(delegate(UsageTracker.TierTotal a, UsageTracker.TierTotal b) { return b.Total.CompareTo(a.Total); });
            return p;
        }

        private static UsageTracker.PeriodSummary Scaled(UsageTracker.PeriodSummary src, double k, string label, string range)
        {
            UsageTracker.PeriodSummary p = new UsageTracker.PeriodSummary();
            p.Label = label;
            p.Range = range;
            p.Total = (long)(src.Total * k);
            p.Input = (long)(src.Input * k);
            p.Output = (long)(src.Output * k);
            p.CacheWrite = (long)(src.CacheWrite * k);
            p.CacheRead = (long)(src.CacheRead * k);
            foreach (UsageTracker.SessionTotal a in src.Rows)
            {
                UsageTracker.SessionTotal b = new UsageTracker.SessionTotal();
                b.Session = a.Session; b.Name = a.Name; b.Title = a.Title; b.SubCount = a.SubCount; b.Active = a.Active; b.CacheExpiryUtc = a.CacheExpiryUtc; b.LastUtc = a.LastUtc;
                b.Total = (long)(a.Total * k); b.Input = (long)(a.Input * k); b.Output = (long)(a.Output * k); b.CacheWrite = (long)(a.CacheWrite * k); b.CacheRead = (long)(a.CacheRead * k);
                p.Rows.Add(b);
            }
            foreach (UsageTracker.ModelTotal a in src.ModelRows)
            {
                UsageTracker.ModelTotal b = new UsageTracker.ModelTotal();
                b.Session = a.Session; b.Model = a.Model; b.Tier = a.Tier; b.Msgs = (long)(a.Msgs * k); b.Think = (long)(a.Think * k); b.FirstUtc = a.FirstUtc; b.LastUtc = a.LastUtc;
                b.Total = (long)(a.Total * k); b.CacheRead = (long)(a.CacheRead * k);
                for (int q = 0; q < 10; q++) b.Tok[q] = (long)(a.Tok[q] * k);
                p.ModelRows.Add(b);
            }
            foreach (UsageTracker.TierTotal a in src.TierRows)
            {
                UsageTracker.TierTotal b = new UsageTracker.TierTotal();
                b.Model = a.Model; b.Tier = a.Tier; b.Msgs = (long)(a.Msgs * k); b.Total = (long)(a.Total * k); b.Out = (long)(a.Out * k); b.Think = (long)(a.Think * k);
                for (int q = 0; q < 10; q++) b.Tok[q] = (long)(a.Tok[q] * k);
                p.TierRows.Add(b);
            }
            return p;
        }

        // sessions: how many fake sessions (0..N); style: see MakeSessions
        private static Ds BuildDs(string name, int sessions, int style)
        {
            Ds ds = new Ds();
            ds.Name = name;
            DateTime now = DateTime.UtcNow;
            Random r = new Random(1234 + sessions * 7 + style);
            List<Fs> fs = MakeSessions(sessions, style);
            ds.Sessions = fs;
            List<List<Msg>> msgs = new List<List<Msg>>();
            foreach (Fs f in fs)
            {
                msgs.Add(MakeMsgs(f, now, r));
            }
            UsageTracker.Snapshot snap = new UsageTracker.Snapshot();
            DateTime cycleStart = now.AddMinutes(-229);
            snap.Periods[0] = FillPeriod("15", now.AddMinutes(-15), DateTime.MaxValue, fs, msgs, now, Clock(now.AddMinutes(-15)) + "–" + Clock(now));
            snap.Periods[1] = FillPeriod("5h", cycleStart, DateTime.MaxValue, fs, msgs, now, Clock(cycleStart) + "–" + Clock(now.AddMinutes(71)));
            snap.Periods[2] = FillPeriod("prev5h", now.AddMinutes(-529), cycleStart, fs, msgs, now, Clock(now.AddMinutes(-529)) + "–" + Clock(cycleStart));
            snap.Periods[3] = FillPeriod("1h", now.AddMinutes(-60), DateTime.MaxValue, fs, msgs, now, Clock(now.AddMinutes(-60)) + "–" + Clock(now));
            snap.Periods[4] = FillPeriod("5m", now.AddMinutes(-5), DateTime.MaxValue, fs, msgs, now, Clock(now.AddMinutes(-5)) + "–" + Clock(now));
            snap.Periods[5] = FillPeriod("30m", now.AddMinutes(-30), DateTime.MaxValue, fs, msgs, now, Clock(now.AddMinutes(-30)) + "–" + Clock(now));
            snap.Periods[6] = FillPeriod("3h", now.AddMinutes(-180), DateTime.MaxValue, fs, msgs, now, Clock(now.AddMinutes(-180)) + "–" + Clock(now));
            for (int i = 0; i < fs.Count; i++)
            {
                Fs f = fs[i];
                UsageTracker.LiveInfo li = new UsageTracker.LiveInfo();
                li.Active = f.State == 0;
                li.Waiting = f.State == 1;
                li.LastUtc = now.AddMinutes(-f.LastMin);
                li.TtlMin = f.State == 6 ? 0 : 60;
                if (f.State <= 5)
                {
                    li.ExpiryUtc = f.State == 0 ? now.AddMinutes(59) : now.AddMinutes(60 - f.LastMin);
                }
                li.Ctx = 90000 + (long)(f.W * 60000);
                li.Model = f.Model;
                foreach (Msg m in msgs[i])
                {
                    if (m.Utc >= now.AddMinutes(-180))
                    {
                        UsageTracker.RecentUse ru = new UsageTracker.RecentUse();
                        ru.Utc = m.Utc;
                        ru.Model = m.Model;
                        ru.Tier = m.Tier;
                        Array.Copy(m.Tok, ru.Tok, 10);
                        li.Recent.Add(ru);
                    }
                }
                snap.Live[f.Id] = li;
            }
            snap.Signature = "fake-" + name + "-" + Guid.NewGuid().ToString("N");
            ds.Snap = snap;

            // today / yesterday / this week / custom: scaled copies of the 5-hour cycle
            UsageTracker.PeriodSummary basis = snap.Periods[1];
            ds.LogBy[7] = Scaled(basis, 2.3, "today", "11-16 00:00 ~ 11-16 23:00");
            ds.LogBy[8] = Scaled(basis, 3.1, "yesterday", "11-15 00:00 ~ 11-16 00:00");
            ds.LogBy[9] = Scaled(basis, 11.0, "week", "11-16 00:00 ~ 11-16 23:00");
            ds.LogBy[10] = Scaled(basis, 5.0, "custom", "11-14 00:00 ~ 11-15 00:00");

            // history for 8 days (speed over long windows, trend view)
            for (int i = 0; i < fs.Count; i++)
            {
                List<UsageTracker.RecentUse> hl = new List<UsageTracker.RecentUse>();
                foreach (Msg m in msgs[i])
                {
                    UsageTracker.RecentUse ru = new UsageTracker.RecentUse();
                    ru.Utc = m.Utc; ru.Model = m.Model; ru.Tier = m.Tier;
                    Array.Copy(m.Tok, ru.Tok, 10);
                    hl.Add(ru);
                }
                ds.Hist[fs[i].Id] = hl;
            }
            string[] hm = { "claude-sonnet-5-5", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };
            for (int d = 0; d < 8; d++)
            {
                for (int k = 0; k < 3; k++)
                {
                    List<UsageTracker.RecentUse> hl = new List<UsageTracker.RecentUse>();
                    int cnt = 40 + r.Next(90) + (d == 2 && k == 1 ? 260 : 0);       // one burst day so that a bar goes red
                    DateTime t = now.AddDays(-d).AddHours(-10 + 3 * k - (d == 0 ? 6 : 0)).AddMinutes(r.Next(30));
                    if (d == 2 && k == 1)
                    {
                        t = now.AddDays(-2).Date.AddHours(14).ToUniversalTime();
                    }
                    for (int i = 0; i < cnt; i++)
                    {
                        t = t.AddMinutes(d == 2 && k == 1 ? 0.2 : 1.0 + r.NextDouble() * 3.0);
                        if (t > now.AddMinutes(-200))
                        {
                            break;
                        }
                        UsageTracker.RecentUse ru = new UsageTracker.RecentUse();
                        ru.Utc = t;
                        ru.Model = hm[(d + k) % hm.Length];
                        ru.Tier = "high";
                        ru.Tok[0] = 20;
                        ru.Tok[1] = 1500 + r.Next(2500);
                        ru.Tok[3] = 6000 + r.Next(4000);
                        ru.Tok[4] = 80000 + r.Next(60000);
                        hl.Add(ru);
                    }
                    if (hl.Count > 0)
                    {
                        ds.Hist["h" + d + k] = hl;
                    }
                }
            }

            // API view
            string[] am = { "deepseek-v4.1-flash-1017", "qwen3-coder-plus", "glm-5" };
            for (int i = 0; i < am.Length; i++)
            {
                LogSummary.ApiTotal a = new LogSummary.ApiTotal();
                a.Model = am[i];
                a.Msgs = 5230 / (i + 1);
                a.In = 18200000 / (i + 1);
                a.Cw = 4100000 / (i + 1);
                a.Out = 1200000 / (i + 1);
                a.Cr = 96000000 / (i + 1);
                a.First = DateTime.Now.AddDays(-30 + i * 5);
                a.Last = DateTime.Now.AddDays(-1);
                ds.Api.Add(a);
            }

            // subscription view: cost of the current billing cycle per account
            string[] ids = { "A", "B" };
            double[] ks = { 1.0, 0.045 };
            for (int i = 0; i < ids.Length; i++)
            {
                UsageTracker.PeriodSummary sp = new UsageTracker.PeriodSummary();
                UsageTracker.ModelTotal mt = new UsageTracker.ModelTotal();
                mt.Model = i == 0 ? "claude-sonnet-5-5" : "claude-opus-5-5";
                mt.Tok[0] = (long)(4000000 * ks[i]);
                mt.Tok[1] = (long)(8000000 * ks[i]);
                mt.Tok[3] = (long)(20000000 * ks[i]);
                mt.Tok[4] = (long)(600000000 * ks[i]);
                sp.ModelRows.Add(mt);
                ds.Sub[ids[i]] = sp;
            }
            return ds;
        }

        // ------------------------------------------------------------------ render one frame
        private sealed class QSpec
        {
            public int Skin;
            public bool Dark;
            public float Scale = 1f;
            public bool Usage = true;
            public int Mode;
            public int Period = 5;
            public int WinW, WinH;          // base units, 0 = default
            public int Profile = 1;         // Feat.Preset: 0 everything, 1 recommended, 2 lean
            public int SMask = -1;          // -1 = from the profile
            public int VMask = -1;
            public int DMask = -1;
            public Ds Data;
            public FetchResult Quotas;
            public FetchResult Problem;
            public bool NoQuotaTable;
            public int Health;              // 0 natural, 1 ok, 2 high, 3 low
            public int Sort = -1;
            public bool SortAsc;
            public bool Paused;
            public bool Badge;
            public int BgPercent = 85;
            public int RowHu = 16;
            public int SpeedMin = 15;
            public int TabMask = -1;
            public int TrendRange;
            public bool Mail;
            public bool Expanded;
            public string Caption = "";
        }

        private static FetchResult QQuotas(double p5, double pw, int resetMin5, int resetHoursW)
        {
            FetchResult r = Quotas(p5, pw, resetMin5, resetHoursW);
            r.Via = "bridge";
            return r;
        }

        private static readonly List<string> qIssues = new List<string>();
        private static string qScenario = "";
        private static int qFrames;

        private static Bitmap QRender(QSpec s)
        {
            Skin[] skins = (Skin[])wt.GetField("Skins", Stat).GetValue(null);
            Environment.SetEnvironmentVariable("STILLA_DEMO", null);
            UpdateState.Latest = s.Badge ? "9.9.9" : "";
            Set("skinIndex", s.Skin);
            Set("skin", skins[s.Skin]);
            Set("inverted", s.Dark);
            wt.GetMethod("ApplyPalette", Stat).Invoke(null, new object[] { skins[s.Skin], s.Dark });
            int[] pm = Feat.Preset(s.Profile);
            Set("fmask", pm[0]);
            Set("vmask", s.VMask >= 0 ? s.VMask : pm[1]);
            Set("smask", s.SMask >= 0 ? s.SMask : pm[2]);
            Set("dmask", s.DMask >= 0 ? s.DMask : pm[3]);
            Set("profile", s.Profile);
            Set("showUsage", s.Usage);
            Set("usageMode", s.Mode);
            Set("usagePeriod", s.Period);
            Set("winWu", s.WinW);
            Set("winHu", s.WinH);
            Set("rowsExpanded", s.Expanded);
            Set("rowScroll", 0);
            Set("bgPercent", s.BgPercent);
            Set("data", s.Quotas);
            Set("problem", s.Problem);
            Set("scale", s.Scale);
            Set("rowHu", s.RowHu);
            Set("speedMin", s.SpeedMin);
            Set("paused", s.Paused);
            Set("trendRange", s.TrendRange);
            Set("tabMask", s.TabMask >= 0 ? s.TabMask : 1258);
            Set("trendKey", "");
            int[] sc = (int[])Get("sortCol");
            bool[] sd = (bool[])Get("sortDesc");
            for (int i = 0; i < sc.Length; i++)
            {
                sc[i] = -1;
                sd[i] = true;
            }
            if (s.Sort >= 0)
            {
                sc[s.Mode < 3 ? s.Mode : 0] = s.Sort;
                sd[s.Mode < 3 ? s.Mode : 0] = !s.SortAsc;
            }
            MailStore.Watches.Clear();
            MailStore.Enabled = s.Mail;
            if (s.Mail)
            {
                MailStore.Host = "smtp.example.com";
                MailStore.User = "demo@example.com";
                MailStore.To = "demo@example.com";
                MailStore.Password = "x";
                foreach (Fs f in s.Data.Sessions)
                {
                    if (f.Watch)
                    {
                        MailWatch mw = new MailWatch();
                        mw.Session = f.Id;
                        mw.Title = f.Title ?? "";
                        mw.Mins.Add(10);
                        MailStore.Watches.Add(mw);
                    }
                }
            }
            if (s.Data != null)
            {
                Set("usageSnap", s.Data.Snap);
                UsageTracker.PeriodSummary[] lb = (UsageTracker.PeriodSummary[])Get("logBy");
                for (int i = 7; i < 11; i++)
                {
                    lb[i] = s.Data.LogBy[i];
                }
                Set("speedHist", s.Data.Hist);
                Set("apiRows", s.Data.Api);
                IDictionary<string, UsageTracker.PeriodSummary> sub = (Dictionary<string, UsageTracker.PeriodSummary>)Get("subBy");
                sub.Clear();
                foreach (KeyValuePair<string, UsageTracker.PeriodSummary> kv in s.Data.Sub)
                {
                    sub[kv.Key] = kv.Value;
                }
            }
            if (s.NoQuotaTable)
            {
                Set("quota", null);
                Set("quotaReadUtc", DateTime.UtcNow);
            }
            else if (Get("quota") == null)
            {
                Set("quotaReadUtc", DateTime.MinValue);
            }
            if (s.Health > 0)
            {
                Type ht = wt.GetNestedType("HealthInfo", BindingFlags.NonPublic);
                object h = Activator.CreateInstance(ht, true);
                ht.GetField("Level").SetValue(h, s.Health == 1 ? 1 : s.Health == 2 ? 2 : 3);
                ht.GetField("Short").SetValue(h, s.Health == 1 ? (Zh ? "估算✓吻合" : "Estimate OK") : s.Health == 2 ? (Zh ? "估算偏高 1.8×" : "Estimate high 1.8×") : (Zh ? "估算偏低 2.1×" : "Estimate low 2.1×"));
                ht.GetField("Line").SetValue(h, s.Health == 1 ? "" : (Zh ? "⚠ 估算比额度读数涨得快 1.8×（近 90 分钟估 17 实涨 9）：速度 / 触顶预测会偏高，系数可能偏大" : "⚠ The estimate rises 1.8× faster than the quota reading (17 est vs 9 actual in 90 min): speed and cap forecast will read high, the factors may be too large"));
                Set("healthCache", h);
                Set("healthAt", DateTime.UtcNow);
            }
            else
            {
                Set("healthCache", null);
                Set("healthAt", DateTime.MinValue);
            }
            Call("CreateFonts");
            int cw = (int)Call("CurW");
            int ch = (int)Call("CurH");
            w.ClientSize = new Size((int)Math.Round(cw * s.Scale), (int)Math.Round(ch * s.Scale));
            UiProbe.Begin();
            try
            {
                Call("Redraw");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                qIssues.Add("EXCEPTION  " + qScenario + " | " + s.Caption + " | " + inner.GetType().Name + ": " + inner.Message + " @ " + (inner.StackTrace ?? "").Split('\n')[0].Trim());
            }
            Bitmap surf = (Bitmap)Get("surface");
            foreach (string iss in UiProbe.Issues(surf.Width, surf.Height))
            {
                qIssues.Add(iss + "   <= " + qScenario + " | " + s.Caption);
            }
            qFrames++;
            Bitmap copy = new Bitmap(surf.Width, surf.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.DrawImage(surf, 0, 0);
            }
            qLastPanel = null;
            if (s.Usage)
            {
                int top = (int)Math.Round((int)Call("CompactH") * s.Scale);
                if (top < copy.Height - 10)
                {
                    qLastPanel = copy.Clone(new Rectangle(0, top, copy.Width, copy.Height - top), copy.PixelFormat);
                }
            }
            return copy;
        }

        private static Bitmap qLastPanel;
        private static readonly Dictionary<Cell, Bitmap> qPanelOf = new Dictionary<Cell, Bitmap>();

        private static void QAdd(List<Cell> list, QSpec s)
        {
            Bitmap b = QRender(s);
            Cell c = new Cell();
            c.Img = b;
            c.Caption = s.Caption + " " + b.Width + "x" + b.Height;
            list.Add(c);
            if (qLastPanel != null)
            {
                qPanelOf[c] = qLastPanel;
                qLastPanel = null;
            }
        }

        private static string SkinTag(int i)
        {
            return SkinName(i).Substring(0, 2);
        }

        private static string Mark(bool dark)
        {
            return dark ? "ink" : "paper";
        }

        // ------------------------------------------------------------------ scenarios
        private static readonly int[] AllSkins = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        // representative subset for the expensive scenarios: the odd skins (2-C, 2-D, 3-F, 3-G, 3-H, 3-J), 3 mix skins, the reference skin and 2-A / 3-I
        private static readonly int[] OddSkins = { 0, 2, 3, 5, 6, 8, 9, 11, 12, 13, 14, 15 };

        private static void QSheet(List<Cell> cells, int cols, string name, string title)
        {
            // panel-only crops (the usage table at 1:1) in chunks of 8, two columns: much easier to read than the full frames
            List<Cell> panels = new List<Cell>();
            foreach (Cell c in cells)
            {
                Bitmap pb;
                if (qPanelOf.TryGetValue(c, out pb))
                {
                    Cell pc = new Cell();
                    pc.Img = pb;
                    pc.Caption = c.Caption;
                    panels.Add(pc);
                    qPanelOf.Remove(c);
                }
            }
            for (int part = 0; part * 8 < panels.Count; part++)
            {
                int n = Math.Min(8, panels.Count - part * 8);
                Sheet(panels.GetRange(part * 8, n), 2, Path.Combine(qOut, name + "_panel" + part + ".png"), title + " (panel only)");
            }
            Sheet(cells, cols, Path.Combine(qOut, name + ".png"), title);
        }

        private static string qOut = "";

        private static int RunQa(string scenario, string outDir)
        {
            qOut = outDir;
            qScenario = scenario;
            qIssues.Clear();
            qFrames = 0;
            // the account timeline entry created by Main() says "since now": make it 30 days old, like a real installation
            System.Collections.IList tl = (System.Collections.IList)typeof(AccountStore).GetField("timeline", Stat).GetValue(null);
            foreach (object te in tl)
            {
                ((TimelineEntry)te).Utc = DateTime.UtcNow.AddDays(-30);
            }
            UiProbe.Ignore.Clear();
            UiProbe.Ignore.Add("一个特别特别长");           // the long titles of the fake data are meant to be cut
            UiProbe.Ignore.Add("A very very long session title");
            UiProbe.Ignore.Add("ThisIsAVeryLongSessionTitle");
            UiProbe.Ignore.Add("这是一个没有任何空格");
            Ds normal = BuildDs("normal", 8, 0);
            FetchResult fetch = QQuotas(58, 61, 71, 64);
            string[] list = scenario == "q_all"
                ? new string[] { "q_default", "q_simple", "q_views", "q_every", "q_sizes", "q_scale", "q_edge", "q_tips", "q_dialogs" }
                : new string[] { scenario };
            foreach (string sc in list)
            {
                qScenario = sc;
                int before = qIssues.Count;
                if (sc == "q_default") QDefault(normal, fetch);
                else if (sc == "q_simple") QSimple(normal, fetch);
                else if (sc == "q_views") QViews(normal, fetch);
                else if (sc == "q_every") QEvery(normal, fetch);
                else if (sc == "q_sizes") QSizes(normal, fetch);
                else if (sc == "q_scale") QScale(normal, fetch);
                else if (sc == "q_edge") QEdge(normal, fetch);
                else if (sc == "q_tips") QTips(normal, fetch);
                else if (sc == "q_dialogs") QDialogs(normal);
                else if (sc == "q_shots") QShots(normal, fetch);
                else
                {
                    Console.WriteLine("unknown QA scenario " + sc);
                    return 2;
                }
                Console.WriteLine(sc + ": " + (qIssues.Count - before) + " issue line(s)");
            }
            string file = Path.Combine(outDir, "issues_" + scenario + ".txt");
            File.WriteAllLines(file, qIssues.ToArray(), new UTF8Encoding(false));
            // summary: the same problem over many frames collapsed into one line with the list of frames
            Dictionary<string, List<string>> grp = new Dictionary<string, List<string>>();
            List<string> order = new List<string>();
            foreach (string iss in qIssues)
            {
                int at = iss.IndexOf("   <= ", StringComparison.Ordinal);
                string key = at > 0 ? iss.Substring(0, at) : iss;
                string where = at > 0 ? iss.Substring(at + 6) : "";
                List<string> l;
                if (!grp.TryGetValue(key, out l))
                {
                    l = new List<string>();
                    grp[key] = l;
                    order.Add(key);
                }
                l.Add(where);
            }
            List<string> sum = new List<string>();
            foreach (string key in order)
            {
                List<string> l = grp[key];
                sum.Add(key + "   [" + l.Count + "x] " + string.Join(" ; ", l.GetRange(0, Math.Min(l.Count, 6)).ToArray()) + (l.Count > 6 ? " ..." : ""));
            }
            File.WriteAllLines(Path.Combine(outDir, "issues_" + scenario + "_summary.txt"), sum.ToArray(), new UTF8Encoding(false));
            Console.WriteLine("frames " + qFrames + ", issues " + qIssues.Count + " -> " + file);
            // short summary by kind
            Dictionary<string, int> kinds = new Dictionary<string, int>();
            foreach (string i in qIssues)
            {
                string k = i.Split(' ')[0];
                int c;
                kinds.TryGetValue(k, out c);
                kinds[k] = c + 1;
            }
            foreach (KeyValuePair<string, int> kv in kinds)
            {
                Console.WriteLine("  " + kv.Key + " x" + kv.Value);
            }
            w.Dispose();
            return 0;
        }

        // README pictures: single frames (shot_*.png, transparent background like the real window); a script assembles the sheets / GIFs from them.
        // Not part of q_all. Same rich fake data as the other scenarios.
        private static void SaveShot(QSpec s, string name)
        {
            using (Bitmap b = QRender(s))
            {
                b.Save(Path.Combine(qOut, name + ".png"), ImageFormat.Png);
            }
        }

        private static void QShots(Ds ds, FetchResult fetch)
        {
            foreach (int m in new int[] { 0, 1, 2, 3, 5 })                // Session, Model, Compare, Plan, Trend
            {
                QSpec s = new QSpec();
                s.Skin = 0; s.Dark = false; s.Data = ds; s.Quotas = fetch; s.Mode = m; s.Period = 5; s.Mail = true; s.Health = 1;     // 1 = self-check says "OK": no red warning line in a README picture
                SaveShot(s, "shot_view_" + m);
            }
            foreach (int i in AllSkins)
            {
                for (int d = 0; d < 2; d++)
                {
                    QSpec s = new QSpec();
                    s.Skin = i; s.Dark = d == 1; s.Data = ds; s.Quotas = fetch; s.Usage = false;
                    SaveShot(s, "shot_skin_" + i.ToString("00") + "_" + Mark(d == 1));
                }
            }
            int[] hs = { 0, 0, 12, 13 };
            bool[] hd = { false, true, false, true };
            bool[] hu = { true, true, true, false };
            for (int q = 0; q < hs.Length; q++)
            {
                QSpec s = new QSpec();
                s.Skin = hs[q]; s.Dark = hd[q]; s.Data = ds; s.Quotas = fetch; s.Usage = hu[q]; s.Period = 5; s.Mail = hu[q]; s.Health = 1;
                SaveShot(s, "shot_hero_" + q);
            }
        }

        // 1. default state: Recommended profile, usage panel on, session view; all skins, paper + ink, 1.0 scale, default size
        private static void QDefault(Ds ds, FetchResult fetch)
        {
            foreach (int period in new int[] { 5, 1 })
            {
                for (int d = 0; d < 2; d++)
                {
                    List<Cell> row = new List<Cell>();
                    foreach (int i in AllSkins)
                    {
                        QSpec s = new QSpec();
                        s.Skin = i; s.Dark = d == 1; s.Data = ds; s.Quotas = fetch; s.Period = period; s.Mail = true;
                        s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " p" + period;
                        QAdd(row, s);
                    }
                    QSheet(row, 4, "q_default_p" + period + "_" + Mark(d == 1), "default state / Recommended / session view / period " + period + " / " + Mark(d == 1));
                }
            }
        }

        // 1b. simple mode (usage panel off) and the toggle bar
        private static void QSimple(Ds ds, FetchResult fetch)
        {
            for (int d = 0; d < 2; d++)
            {
                List<Cell> row = new List<Cell>();
                foreach (int i in AllSkins)
                {
                    QSpec s = new QSpec();
                    s.Skin = i; s.Dark = d == 1; s.Data = ds; s.Quotas = fetch; s.Usage = false;
                    s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " simple";
                    QAdd(row, s);
                }
                QSheet(row, 4, "q_simple_" + Mark(d == 1), "simple mode (usage panel off) / " + Mark(d == 1));
            }
            // with an update badge, an account chip and a paused note the compact card is at its busiest
            List<Cell> busy = new List<Cell>();
            foreach (int i in AllSkins)
            {
                QSpec s = new QSpec();
                s.Skin = i; s.Dark = i % 2 == 1; s.Data = ds; s.Quotas = fetch; s.Usage = false; s.Badge = true;
                s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " simple+badge";
                QAdd(busy, s);
            }
            QSheet(busy, 4, "q_simple_badge", "simple mode with the update badge");
        }

        private static readonly string[] ViewNames = { "session", "model", "compare", "plan", "api", "trend" };

        // 2. the other views, default and minimum size; API only exists in the Everything profile
        private static void QViews(Ds ds, FetchResult fetch)
        {
            for (int m = 0; m < 6; m++)
            {
                for (int z = 0; z < 2; z++)
                {
                    List<Cell> row = new List<Cell>();
                    foreach (int i in AllSkins)
                    {
                        QSpec s = new QSpec();
                        s.Skin = i; s.Dark = i % 2 == 1; s.Data = ds; s.Quotas = fetch; s.Mode = m; s.Profile = 0; s.Mail = true;
                        if (z == 1)
                        {
                            s.WinW = 1;
                            s.WinH = 1;
                        }
                        s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " " + ViewNames[m] + (z == 1 ? " min" : "");
                        QAdd(row, s);
                    }
                    QSheet(row, 4, "q_view_" + ViewNames[m] + (z == 1 ? "_min" : "_default"), "view " + ViewNames[m] + " / Everything profile / " + (z == 1 ? "minimum size" : "default size"));
                }
            }
            // trend, 7-day range
            List<Cell> tr7 = new List<Cell>();
            foreach (int i in AllSkins)
            {
                QSpec s = new QSpec();
                s.Skin = i; s.Dark = i % 2 == 1; s.Data = ds; s.Quotas = fetch; s.Mode = 5; s.Profile = 1; s.TrendRange = 1;
                s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " trend 7d";
                QAdd(tr7, s);
            }
            QSheet(tr7, 4, "q_view_trend7d", "trend view, 7-day range");
            // Recommended profile (what the user gets): Model / Compare / Plan / Trend at default size
            List<Cell> rec = new List<Cell>();
            foreach (int m in new int[] { 1, 2, 3, 5 })
            {
                foreach (int i in OddSkins)
                {
                    QSpec s = new QSpec();
                    s.Skin = i; s.Dark = i % 2 == 1; s.Data = ds; s.Quotas = fetch; s.Mode = m; s.Profile = 1;
                    s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " rec-" + ViewNames[m];
                    QAdd(rec, s);
                }
            }
            QSheet(rec, 4, "q_view_recommended", "Recommended profile views");
        }

        // 2b. Everything profile (all columns on = widest case) in the session view
        private static void QEvery(Ds ds, FetchResult fetch)
        {
            for (int z = 0; z < 2; z++)
            {
                for (int d = 0; d < 2; d++)
                {
                    List<Cell> row = new List<Cell>();
                    foreach (int i in AllSkins)
                    {
                        QSpec s = new QSpec();
                        s.Skin = i; s.Dark = d == 1; s.Data = ds; s.Quotas = fetch; s.Profile = 0; s.Mail = true; s.Health = 2;
                        if (z == 1)
                        {
                            s.WinW = 1;
                            s.WinH = 1;
                        }
                        s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " every" + (z == 1 ? " min" : "");
                        QAdd(row, s);
                    }
                    QSheet(row, 4, "q_every_" + (z == 1 ? "min_" : "default_") + Mark(d == 1), "Everything profile, session view, " + (z == 1 ? "minimum" : "default") + " size / " + Mark(d == 1));
                }
            }
        }

        // 3. window sizes
        private static void QSizes(Ds ds, FetchResult fetch)
        {
            int[,] sizes = { { 1, 1 }, { 0, 0 }, { 900, 0 }, { 600, 760 }, { 520, 760 }, { 1200, 400 } };
            string[] names = { "min", "default", "wide900", "tall760", "narrow-tall", "very wide" };
            for (int z = 0; z < sizes.GetLength(0); z++)
            {
                List<Cell> row = new List<Cell>();
                foreach (int i in OddSkins)
                {
                    QSpec s = new QSpec();
                    s.Skin = i; s.Dark = i % 2 == 1; s.Data = ds; s.Quotas = fetch; s.Mail = true;
                    s.WinW = sizes[z, 0]; s.WinH = sizes[z, 1];
                    s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " " + names[z];
                    QAdd(row, s);
                }
                QSheet(row, z >= 2 ? 3 : 4, "q_size_" + names[z].Replace(' ', '_'), "size " + names[z] + " / Recommended / session view");
            }
        }

        // 3b. DPI scales
        private static void QScale(Ds ds, FetchResult fetch)
        {
            foreach (float sc in new float[] { 1.25f, 1.5f, 2.0f })
            {
                foreach (int mode in new int[] { 0, 5 })
                {
                    List<Cell> row = new List<Cell>();
                    foreach (int i in OddSkins)
                    {
                        QSpec s = new QSpec();
                        s.Skin = i; s.Dark = i % 2 == 1; s.Data = ds; s.Quotas = fetch; s.Scale = sc; s.Mode = mode; s.Mail = true; s.Profile = mode == 0 ? 1 : 0;
                        s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " x" + sc.ToString(CultureInfo.InvariantCulture) + " " + ViewNames[mode];
                        QAdd(row, s);
                    }
                    QSheet(row, sc >= 2f ? 3 : sc >= 1.5f ? 3 : 4, "q_scale_" + sc.ToString(CultureInfo.InvariantCulture) + "_" + ViewNames[mode], "scale " + sc + " / " + ViewNames[mode]);
                }
                // simple mode at this scale
                List<Cell> simple = new List<Cell>();
                foreach (int i in OddSkins)
                {
                    QSpec s = new QSpec();
                    s.Skin = i; s.Dark = i % 2 == 1; s.Data = ds; s.Quotas = fetch; s.Scale = sc; s.Usage = false;
                    s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " x" + sc.ToString(CultureInfo.InvariantCulture) + " simple";
                    QAdd(simple, s);
                }
                QSheet(simple, 4, "q_scale_" + sc.ToString(CultureInfo.InvariantCulture) + "_simple", "scale " + sc + " / simple mode");
            }
        }

        // 4. edge data
        private static void QEdge(Ds normal, FetchResult fetch)
        {
            FetchResult problem = new FetchResult();
            problem.Problem = Problem.NoBridge;
            FetchResult extremes = QQuotas(100, 99.6, 3, 167);
            FetchResult zero = QQuotas(0, 0, 299, 167);
            FetchResult noWeek = QQuotas(7, -1, int.MinValue, 0);
            FetchResult staleReset = QQuotas(88.5, 100, -5, -2);
            FetchResult stale = QQuotas(34, 12, 200, 100);
            stale.FetchedAt = DateTime.Now.AddMinutes(-125);
            FetchResult oldProblem = new FetchResult();
            oldProblem.Problem = Problem.NoBridge;
            oldProblem.Detail = "x";
            Ds empty = BuildDs("empty", 0, 0);
            Ds one = BuildDs("one", 1, 0);
            Ds many = BuildDs("many", 34, 0);
            Ds longT = BuildDs("long", 8, 1);
            Ds cjk = BuildDs("cjk", 9, 2);
            Ds allRun = BuildDs("running", 8, 3);
            Ds big = BuildDs("big", 8, 0);        // billions of tokens: "1500.00M" in the widest columns
            for (int pi = 0; pi < big.Snap.Periods.Length; pi++)
            {
                UsageTracker.PeriodSummary src = big.Snap.Periods[pi];
                big.Snap.Periods[pi] = Scaled(src, 120.0, src.Label, src.Range);
            }

            QSpec[] v = new QSpec[]
            {
                new QSpec { Data = empty, Quotas = null, Problem = problem, Caption = "no data (NoBridge)" },
                new QSpec { Data = empty, Quotas = fetch, Caption = "zero sessions" },
                new QSpec { Data = one, Quotas = fetch, Caption = "one session" },
                new QSpec { Data = many, Quotas = fetch, Caption = "34 sessions" },
                new QSpec { Data = many, Quotas = fetch, Expanded = true, Caption = "34 sessions expanded" },
                new QSpec { Data = longT, Quotas = fetch, Caption = "very long titles" },
                new QSpec { Data = cjk, Quotas = fetch, Caption = "long CJK titles" },
                new QSpec { Data = allRun, Quotas = fetch, Caption = "all running" },
                new QSpec { Data = normal, Quotas = extremes, Caption = "100% / 99.6%" },
                new QSpec { Data = normal, Quotas = zero, Caption = "0% / 0%" },
                new QSpec { Data = normal, Quotas = noWeek, Caption = "no weekly, no reset time" },
                new QSpec { Data = normal, Quotas = staleReset, Caption = "reset passed" },
                new QSpec { Data = normal, Quotas = stale, Caption = "stale 125 min" },
                new QSpec { Data = normal, Quotas = stale, Problem = oldProblem, Caption = "old data + problem" },
                new QSpec { Data = normal, Quotas = fetch, NoQuotaTable = true, Caption = "no price table" },
                new QSpec { Data = normal, Quotas = fetch, Health = 2, Caption = "health high" },
                new QSpec { Data = normal, Quotas = fetch, Health = 3, Caption = "health low" },
                new QSpec { Data = normal, Quotas = fetch, Health = 1, Paused = true, Caption = "paused + health ok" },
                new QSpec { Data = normal, Quotas = fetch, Sort = 2, Caption = "sorted by speed desc" },
                new QSpec { Data = normal, Quotas = fetch, Sort = 1, SortAsc = true, Caption = "sorted by cache asc" },
                new QSpec { Data = normal, Quotas = fetch, SpeedMin = 360, Caption = "speed window 6h" },
                new QSpec { Data = normal, Quotas = fetch, SpeedMin = 2880, Caption = "speed window 48h" },
                new QSpec { Data = normal, Quotas = fetch, RowHu = 34, Caption = "row height 34" },
                new QSpec { Data = normal, Quotas = fetch, BgPercent = 20, Caption = "background 20%" },
                new QSpec { Data = big, Quotas = fetch, Caption = "billions of tokens" },
                new QSpec { Data = big, Quotas = fetch, Profile = 0, Caption = "billions, Everything" }
            };
            for (int k = 0; k < v.Length; k++)
            {
                List<Cell> row = new List<Cell>();
                foreach (int i in OddSkins)
                {
                    QSpec s = new QSpec();
                    QSpec src = v[k];
                    s.Skin = i; s.Dark = i % 2 == 1; s.Data = src.Data; s.Quotas = src.Quotas; s.Problem = src.Problem; s.Expanded = src.Expanded;
                    s.NoQuotaTable = src.NoQuotaTable; s.Health = src.Health; s.Paused = src.Paused; s.Sort = src.Sort; s.SortAsc = src.SortAsc;
                    s.SpeedMin = src.SpeedMin; s.RowHu = src.RowHu; s.BgPercent = src.BgPercent == 85 ? 85 : src.BgPercent;
                    s.Mail = true;
                    s.Profile = src.Profile;
                    s.Caption = SkinTag(i) + " " + Mark(s.Dark) + " " + src.Caption;
                    QAdd(row, s);
                }
                QSheet(row, 4, "q_edge_" + k.ToString("00"), "edge: " + v[k].Caption);
            }
        }
    }
}
