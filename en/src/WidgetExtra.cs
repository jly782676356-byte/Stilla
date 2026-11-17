// New features of Widget (v31): feature switches and profiles, the session 'Cost' column, the estimate health self-check, the hourly usage trend chart, the weekly report entry.
// Same class as Widget in Main.cs (partial).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Stilla
{
    internal sealed partial class Widget
    {
        // ------------------------------------------------------------ Feature switches / display items / profiles
        private int profile;                                        // 0 Everything, 1 Recommended, 2 Minimal, 3 Custom
        // The default profile of a new install is Recommended: all features on, but the low-value Input column, the API view and the DeepSeek cost line are hidden (all of them can be switched on in the Everything profile)
        private int fmask = Feat.Preset(1)[0], vmask = Feat.Preset(1)[1], smask = Feat.Preset(1)[2], dmask = Feat.Preset(1)[3];
        private bool sawFmask;
        private int costScope;                                      // Cost column: 0 = session total (all history), 1 = within the current time tab
        private int trendRange;                                     // Trend chart: 0 = last 24 hours (per hour), 1 = last 7 days (per day)

        private bool Has(int f)
        {
            return (fmask & f) != 0;
        }

        private bool Show(int d)
        {
            return (dmask & d) != 0;
        }

        private bool ColShown(int bit)
        {
            return (smask & (1 << bit)) != 0;
        }

        private Feat.Cadence Cad()
        {
            return Feat.CadenceOf(profile);
        }

        // Keep the two old switches alert / log in line with fmask (in the old menus and old settings files they were separate)
        private void SyncMasksFromFields()
        {
            fmask = alertsOn ? (fmask | Feat.Alert) : (fmask & ~Feat.Alert);
            fmask = logger != null && logger.Enabled ? (fmask | Feat.Log) : (fmask & ~Feat.Log);
        }

        private void FinishSettingsLoad()
        {
            if (sawFmask)
            {
                alertsOn = Has(Feat.Alert);
                if (logger != null)
                {
                    logger.Enabled = Has(Feat.Log);
                }
            }
            else
            {
                SyncMasksFromFields();
            }
            if ((vmask & Feat.AllV) == 0)
            {
                vmask = Feat.AllV;
            }
            if ((smask & Feat.AllS) == 0)
            {
                smask = Feat.AllS;
            }
            MigrateColumns();
            if (((vmask >> usageMode) & 1) == 0)
            {
                usageMode = FirstView();
            }
            profile = Feat.Identify(fmask, vmask, smask, dmask);
        }

        private int FirstView()
        {
            for (int i = 0; i < Feat.ViewCount; i++)
            {
                if ((vmask & (1 << i)) != 0)
                {
                    return i;
                }
            }
            return 0;
        }

        private void ApplyPreset(int p)
        {
            int[] a = Feat.Preset(p);
            SetMasks(a[0], a[1], a[2], a[3]);
        }

        private void SetMasks(int f, int v, int s, int d)
        {
            fmask = f;
            vmask = v & Feat.AllV;
            smask = s & Feat.AllS;
            dmask = d & Feat.AllD;
            alertsOn = Has(Feat.Alert);
            if (logger != null)
            {
                logger.Enabled = Has(Feat.Log);
            }
            profile = Feat.Identify(fmask, vmask, smask, dmask);
            if (((vmask >> usageMode) & 1) == 0)
            {
                CollapseRows();
                usageMode = FirstView();
            }
            if (!alertsOn && !showUsage)
            {
                usageSnap = new UsageTracker.Snapshot();
                indicatorKey = -1;
                UpdateCacheIndicators();
            }
            healthAt = DateTime.MinValue;
            trendKey = "";
            speedHistUtc = DateTime.MinValue;       // History / cost column was turned on: re-read right away
            Redraw();
            SaveSettings();
        }

        private void OpenFeatureSettings()
        {
            SyncMasksFromFields();
            using (FeatureForm f = new FeatureForm(profile, fmask, vmask, smask, dmask))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    SetMasks(f.F, f.V, f.S, f.D);
                }
            }
        }

        private ToolStripMenuItem BuildProfileItem()
        {
            ToolStripMenuItem prof = new ToolStripMenuItem("Profile");
            ToolStripMenuItem[] items = new ToolStripMenuItem[4];
            for (int i = 0; i < 3; i++)
            {
                int p = i;
                items[i] = new ToolStripMenuItem(Feat.ProfileNames[i] + " —— " + Feat.ProfileDesc[i]);
                items[i].Click += delegate { ApplyPreset(p); };
                prof.DropDownItems.Add(items[i]);
            }
            items[3] = new ToolStripMenuItem("Custom (adjust manually in 'Features and display settings')");
            items[3].Enabled = false;
            prof.DropDownItems.Add(items[3]);
            prof.DropDownOpening += delegate
            {
                SyncMasksFromFields();
                int now = Feat.Identify(fmask, vmask, smask, dmask);
                for (int i = 0; i < 4; i++)
                {
                    items[i].Checked = i == now;
                }
            };
            return prof;
        }

        private ToolStripMenuItem BuildReportItem()
        {
            ToolStripMenuItem rep = new ToolStripMenuItem("Weekly report");
            rep.DropDownItems.Add("Generate and open this week's summary", null, delegate { MakeReport(false, true); });
            rep.DropDownItems.Add("Generate and open last week's summary", null, delegate { MakeReport(true, true); });
            rep.DropDownItems.Add("Open weekly report folder", null, delegate { OpenReportFolder(); });
            return rep;
        }

        private void AddFeatureMenu(ContextMenuStrip menu)
        {
            menu.Items.Add("Features and display settings...", null, delegate { OpenFeatureSettings(); });
            menu.Items.Add(BuildProfileItem());
            menu.Items.Add("Estimate self-check details...", null, delegate { ShowHealthDialog(); });
            menu.Items.Add(BuildReportItem());
            ToolStripMenuItem pauseItem = BuildPauseItem();
            menu.Opening += delegate { pauseItem.Checked = paused; };
            menu.Items.Add(pauseItem);
            ToolStripMenuItem tipsItem = BuildTipsItem();
            menu.Opening += delegate { tipsItem.Checked = tipsOn; };
            menu.Items.Add(tipsItem);
            menu.Items.Add("Email reminders...", null, delegate { OpenMailForm(); });
        }

        // ------------------------------------------------------------ The '⋯' extra button at the right of the time tab strip
        private RectangleF tabMoreRect = RectangleF.Empty;

        private void DrawTabMore(Graphics g, float x, float y0, float w, float s)
        {
            RectangleF r = new RectangleF(x + 2f * s, y0 + 4f * s, w - 2f * s, 34f * s);
            tabMoreRect = new RectangleF(x, y0 - 3f * s, w + 2f * s, 66f * s);
            SkinDraw.Rr(g, SkinDraw.Mix(Bg, TextDim, 0.16f), r, 6f * s);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, d = 2.6f * s, gap = 6f * s;
            using (SolidBrush b = new SolidBrush(TextDim))
            {
                for (int i = -1; i <= 1; i++)
                {
                    g.FillEllipse(b, cx - d / 2f, cy + i * gap - d / 2f, d, d);        // Three vertical dots
                }
            }
        }

        // Choose which time tabs to show: ticked = shown, a click takes effect at once and the menu stays open; below are shortcuts to other common settings
        private void ShowTabMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem head = new ToolStripMenuItem("Time tabs to show (ticked = shown)");
            head.Enabled = false;
            m.Items.Add(head);
            bool keepOpen = false;
            ToolStripMenuItem[] tabItems = new ToolStripMenuItem[TabCount];
            for (int i = 0; i < TabCount; i++)
            {
                int idx = i;
                ToolStripMenuItem it = new ToolStripMenuItem(TabLabels[i]);
                it.Checked = (tabMask & (1 << i)) != 0;
                it.Click += delegate
                {
                    int nm = tabMask ^ (1 << idx);
                    if (nm == 0)
                    {
                        return;         // Keep at least one
                    }
                    keepOpen = true;
                    ApplyTabMask(nm);
                    for (int k = 0; k < TabCount; k++)
                    {
                        tabItems[k].Checked = (tabMask & (1 << k)) != 0;
                    }
                };
                tabItems[i] = it;
                m.Items.Add(it);
            }
            m.Items.Add("Restore default time tabs", null, delegate { ApplyTabMask(TabDefaultMask); });
            m.Closing += delegate(object sender, ToolStripDropDownClosingEventArgs e)
            {
                if (keepOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                {
                    e.Cancel = true;
                    keepOpen = false;
                }
            };
            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(BuildTabPresetItem());
            m.Items.Add(BuildCustomRangeItem());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(BuildSpeedItem());
            m.Items.Add(BuildSizeItem());
            m.Items.Add(MaskMenu("Views to show", Feat.VNames, vmask, 0, 1));
            m.Items.Add(MaskMenu("Session view columns", Feat.SNames, smask, 1, 63));
            m.Items.Add(MaskMenu("Other display items", Feat.DNames, dmask, 2, 0));
            m.Items.Add(BuildRowHeightItem());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(BuildPauseItem());
            m.Items.Add(BuildTipsItem());
            m.Items.Add(BuildAlertItem());
            m.Items.Add(BuildExportItem());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(BuildProfileItem());
            m.Items.Add("Features and display settings...", null, delegate { OpenFeatureSettings(); });
            m.Items.Add("Estimate self-check details...", null, delegate { ShowHealthDialog(); });
            m.Items.Add(BuildReportItem());
            m.Closed += delegate { BeginInvoke(new MethodInvoker(delegate { m.Dispose(); })); };
            m.Show(this, new Point((int)tabMoreRect.Left, (int)tabMoreRect.Bottom));
        }

        // kind: 0 views (vmask), 1 session columns (smask), 2 other display items (dmask). mustKeep: at least one of these bits must stay on
        private ToolStripMenuItem MaskMenu(string title, string[] names, int mask, int kind, int mustKeepBits)
        {
            ToolStripMenuItem root = new ToolStripMenuItem(title);
            for (int i = 0; i < names.Length; i++)
            {
                int bit = 1 << i;
                ToolStripMenuItem it = new ToolStripMenuItem(names[i]);
                it.Checked = (mask & bit) != 0;
                it.Click += delegate
                {
                    int cur = kind == 0 ? vmask : kind == 1 ? smask : dmask;
                    int nv = cur ^ bit;
                    if (kind == 0 && (nv & Feat.AllV) == 0)
                    {
                        return;
                    }
                    if (kind == 1 && (nv & Feat.AllS) == 0)
                    {
                        return;     // Keep at least one column
                    }
                    SetMasks(fmask, kind == 0 ? nv : vmask, kind == 1 ? nv : smask, kind == 2 ? nv : dmask);
                };
                root.DropDownItems.Add(it);
            }
            return root;
        }

        private void ApplyTabMask(int nm)
        {
            tabMask = nm;
            if ((tabMask & (1 << usagePeriod)) == 0)
            {
                usagePeriod = VisibleTabs()[0];
            }
            logRefreshUtc = DateTime.MinValue;      // Log-based tabs that were just shown (today / yesterday / this week / custom) are recomputed right away
            SaveSettings();
            StartLogRefresh();
            Redraw();
        }

        // ------------------------------------------------------------ Follow the account:plan
        private DateTime keyStartCache = DateTime.MinValue;
        private DateTime keyStartAt = DateTime.MinValue;

        // When the current basis (account:plan) started (UTC); MinValue if there is no timeline. Refreshed every 20 seconds
        private DateTime KeyStart()
        {
            if ((DateTime.UtcNow - keyStartAt).TotalSeconds > 20)
            {
                keyStartAt = DateTime.UtcNow;
                List<TimelineEntry> tl = AccountStore.TimelineCopy();
                keyStartCache = tl.Count == 0 ? DateTime.MinValue : tl[tl.Count - 1].Utc;
            }
            return keyStartCache;
        }

        private readonly Dictionary<string, QuotaTable> keyTables = new Dictionary<string, QuotaTable>();
        private DateTime keyTablesAt = DateTime.MinValue;

        // The factor table of one basis (the trend chart uses it to convert by 'the account:plan at the time'). Re-read every 5 minutes
        private QuotaTable TableFor(string key)
        {
            if ((DateTime.UtcNow - keyTablesAt).TotalMinutes > 5)
            {
                keyTables.Clear();
                keyTablesAt = DateTime.UtcNow;
            }
            if (key == AccountStore.CurrentKey && quota != null)
            {
                return quota;
            }
            QuotaTable q;
            if (!keyTables.TryGetValue(key, out q))
            {
                try
                {
                    q = QuotaTable.Load(Path.Combine(LogDir(), "quota_conversion.csv"), key);
                }
                catch (IOException)
                {
                    q = null;
                }
                keyTables[key] = q;
            }
            return q;
        }

        private static bool TablePct(QuotaTable q, UsageTracker.RecentUse u, bool week, out double pct)
        {
            pct = 0;
            QuotaTable.Entry e;
            if (q == null || q.Stale || !q.Models.TryGetValue(BaseModel(u.Model), out e))
            {
                return false;
            }
            double k = week ? e.KW : e.K5;
            if (k <= 0)
            {
                return false;
            }
            pct = q.CostUsd(e, u.Tok) * k * q.TierFactor(u.Tier, week);
            return true;
        }

        // ------------------------------------------------------------ Cost column
        private Dictionary<string, Dictionary<string, long[]>> sessionLife;      // session -> model -> tokens (the whole history of this session in the log)
        private Dictionary<string, double> costBy = new Dictionary<string, double>();   // This paint: session -> cost (USD)
        private bool costApprox;                                                  // The cumulative figures have not finished reading the log yet, use the time-tab ones for now

        // Cost = this session's tokens converted to dollars at official prices (the 'API-equivalent amount' for subscribers, not an actual charge)
        private void PrepareCost(UsageTracker.PeriodSummary cur)
        {
            costBy.Clear();
            costApprox = false;
            bool life = costScope == 0 && Has(Feat.History) && sessionLife != null;
            if (costScope == 0 && !life)
            {
                costApprox = true;      // Wants the cumulative figure but the history is not loaded (turned off / not finished): show the figure for the time tab for now
            }
            foreach (UsageTracker.ModelTotal mt in cur.ModelRows)
            {
                double? u = UsdOf(mt.Model, mt.Tok);
                if (u.HasValue)
                {
                    double v;
                    costBy.TryGetValue(mt.Session, out v);
                    costBy[mt.Session] = v + u.Value;
                }
            }
            if (life)
            {
                foreach (UsageTracker.SessionTotal t in cur.Rows)
                {
                    Dictionary<string, long[]> byModel;
                    if (!sessionLife.TryGetValue(t.Session, out byModel))
                    {
                        continue;
                    }
                    double sum = 0;
                    foreach (KeyValuePair<string, long[]> kv in byModel)
                    {
                        double? u = UsdOf(kv.Key, kv.Value);
                        if (u.HasValue)
                        {
                            sum += u.Value;
                        }
                    }
                    double period;
                    costBy.TryGetValue(t.Session, out period);
                    costBy[t.Session] = Math.Max(sum, period);      // The log is a little slower than the live tracking, take the larger one
                }
            }
        }

        private double? CostOf(string session)
        {
            double v;
            return costBy.TryGetValue(session, out v) ? (double?)v : null;
        }

        private static string CostText(double? v)
        {
            return v.HasValue ? UsdText(v.Value) : "—";
        }

        // ------------------------------------------------------------ Estimate health self-check
        // Compare 'how much the quota reading rose' with 'how much the factor-based estimate rose': the reading is the server's true value (a whole percent), the estimate is what we compute from price x factor.
        // A big gap means the factors are wrong (derived / plan just changed / account changed), or something else (web, another computer) is using the same quota
        private sealed class FivePoint
        {
            public DateTime T;
            public double Pct;
            public long Reset;
        }

        private sealed class HealthInfo
        {
            public int Level;               // 0 not enough data, 1 matches, 2 estimate too high, 3 estimate too low
            public string Short = "";       // Small hint in the total row
            public string Line = "";        // Full sentence that replaces the bottom row (only when the deviation is large)
            public string Detail = "";      // Detail dialog
        }

        private readonly List<FivePoint> fiveHist = new List<FivePoint>();
        private bool fiveLoaded;
        private HealthInfo healthCache;
        private DateTime healthAt = DateTime.MinValue;
        private RectangleF healthRect = RectangleF.Empty;

        private void EnsureFiveHist()
        {
            if (fiveLoaded)
            {
                return;
            }
            fiveLoaded = true;
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
                            && DateTime.TryParseExact(f[2], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out rs)
                            && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out p))
                        {
                            FivePoint w = new FivePoint();
                            w.T = t;
                            w.Pct = p;
                            w.Reset = rs.Ticks;
                            fiveHist.Add(w);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void NoteFive(FetchResult r)
        {
            if (r == null || r.FiveHour == null || !r.FiveHour.ResetsAt.HasValue)
            {
                return;
            }
            EnsureFiveHist();
            long reset = r.FiveHour.ResetsAt.Value.Ticks;
            FivePoint last = fiveHist.Count > 0 ? fiveHist[fiveHist.Count - 1] : null;
            if (last != null && last.Reset == reset && Math.Abs(last.Pct - r.FiveHour.Percent) < 0.001 && (r.FetchedAt - last.T).TotalMinutes < 10)
            {
                return;
            }
            FivePoint w = new FivePoint();
            w.T = r.FetchedAt;
            w.Pct = r.FiveHour.Percent;
            w.Reset = reset;
            fiveHist.Add(w);
            while (fiveHist.Count > 0 && (r.FetchedAt - fiveHist[0].T).TotalHours > 12)
            {
                fiveHist.RemoveAt(0);
            }
        }

        // Quota points (5-hour / weekly) used by all sessions in [fromUtc, toUtc]; only uses the last 3 hours of the live tracking
        private bool EstimateSince(DateTime fromUtc, DateTime toUtc, out double p5, out double pw)
        {
            p5 = 0;
            pw = 0;
            bool known = false;
            foreach (KeyValuePair<string, UsageTracker.LiveInfo> kv in usageSnap.Live)
            {
                foreach (UsageTracker.RecentUse u in kv.Value.Recent)
                {
                    if (u.Utc >= fromUtc && u.Utc <= toUtc)
                    {
                        known |= AddUse(u, ref p5, ref pw);
                    }
                }
            }
            return known;
        }

        private HealthInfo GetHealth()
        {
            if (healthCache != null && (DateTime.UtcNow - healthAt).TotalSeconds < 20)
            {
                return healthCache;
            }
            healthAt = DateTime.UtcNow;
            healthCache = ComputeHealth();
            return healthCache;
        }

        private static string Times(double v)
        {
            return v.ToString(v >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + "×";
        }

        private HealthInfo ComputeHealth()
        {
            HealthInfo h = new HealthInfo();
            StringBuilder d = new StringBuilder();
            string key = AccountStore.CurrentKey;
            d.AppendLine("Basis (account:plan): " + (key.Length == 0 ? "Not set" : key));
            if (quota == null)
            {
                h.Detail = d + "No quota conversion table yet (logs\\quota_conversion.csv), so nothing can be checked.";
                return h;
            }
            if (quota.Stale)
            {
                h.Detail = d + "The current basis has no factors of its own yet (account / plan just changed), so speed and estimates show '—'; they will be calculated automatically once there is enough data.";
                return h;
            }
            d.AppendLine(quota.PlanDerived ? "Factor source: derived from the plan multiplier of another basis (unreliable, may be off by several times)" : "Factor source: measured from this basis's own data");
            if (quota.CalibErr >= 0)
            {
                d.AppendLine("Prediction error on unseen data: ≈" + (quota.CalibErr * 100).ToString("0", CultureInfo.InvariantCulture) + "% (" + quota.CalibBlocks + " data blocks)");
            }
            if (data == null || data.FiveHour == null || !data.FiveHour.ResetsAt.HasValue)
            {
                h.Detail = d + "No quota reading yet, so nothing can be checked.";
                return h;
            }
            EnsureFiveHist();
            DateTime end = data.FetchedAt;
            double pctNow = data.FiveHour.Percent;
            long reset = data.FiveHour.ResetsAt.Value.Ticks;
            FivePoint p0 = null;
            foreach (FivePoint p in fiveHist)
            {
                if (p.Reset == reset && (end - p.T).TotalMinutes <= 150 && (end - p.T).TotalMinutes >= 5 && pctNow - p.Pct >= 6 && (p0 == null || p.T > p0.T))
                {
                    p0 = p;
                }
            }
            if (p0 == null)
            {
                h.Detail = d + "In the last 2.5 hours the 5-hour quota reading rose by less than 6 points (a whole-percent reading is too coarse), so it cannot be checked yet. It will be checked automatically once usage picks up.";
                return h;
            }
            double est5, estW;
            DateTime fromUtc = DateTime.SpecifyKind(p0.T, DateTimeKind.Local).ToUniversalTime();
            DateTime toUtc = DateTime.SpecifyKind(end, DateTimeKind.Local).ToUniversalTime();
            if (!EstimateSince(fromUtc, toUtc, out est5, out estW))
            {
                h.Detail = d + "No usage in this period can be converted (the models have no factors), so nothing can be checked.";
                return h;
            }
            double actual = pctNow - p0.Pct;
            double ratio = est5 / actual;
            double span = (end - p0.T).TotalMinutes;
            d.AppendLine();
            d.AppendLine("Check window: " + p0.T.ToString("HH:mm") + " → " + end.ToString("HH:mm") + " (" + span.ToString("0", CultureInfo.InvariantCulture) + " min)");
            d.AppendLine("5-hour quota reading: " + p0.Pct.ToString("0", CultureInfo.InvariantCulture) + "% → " + pctNow.ToString("0", CultureInfo.InvariantCulture) + "%, up " + actual.ToString("0", CultureInfo.InvariantCulture) + " points (whole-number reading, ±1 point error)");
            d.AppendLine("Estimated from factors: " + est5.ToString("0.0", CultureInfo.InvariantCulture) + " points");
            d.AppendLine("Estimate / reading = " + ratio.ToString("0.00", CultureInfo.InvariantCulture));
            string reasons = quota.PlanDerived ? "factors are derived" : "";
            if (ratio > 1.35)
            {
                h.Level = 2;
                h.Short = "Est. high " + Times(ratio);
                h.Line = "⚠ Estimate high " + Times(ratio) + " (last " + span.ToString("0", CultureInfo.InvariantCulture) + " min: est. " + est5.ToString("0", CultureInfo.InvariantCulture) + ", actual " + actual.ToString("0", CultureInfo.InvariantCulture) + "): speed / cap forecast high"
                    + (reasons.Length > 0 ? ", " + reasons : ", factors may be too large");
                d.AppendLine();
                d.AppendLine("Conclusion: the estimate is too high. Speed and the cap forecast will look more pessimistic than reality.");
                d.AppendLine("Possible causes: " + (quota.PlanDerived ? "the current basis's factors are derived from the plan multiplier (not enough data to measure yet); " : "") + "the plan / account was just changed and the factors still use old data; the price table of some models differs from reality.");
            }
            else if (ratio < 0.72)
            {
                h.Level = 3;
                h.Short = "Est. low " + Times(1 / Math.Max(ratio, 0.01));
                h.Line = "⚠ Estimate low " + Times(1 / Math.Max(ratio, 0.01)) + " (last " + span.ToString("0", CultureInfo.InvariantCulture) + " min: est. " + est5.ToString("0", CultureInfo.InvariantCulture) + ", actual " + actual.ToString("0", CultureInfo.InvariantCulture) + "): something else may be using the same quota";
                d.AppendLine();
                d.AppendLine("Conclusion: the estimate is too low.");
                d.AppendLine("Possible causes: the same account is also using quota on the web / phone / another computer (not recorded on this PC); the factors are too small; sessions that this PC did not record.");
            }
            else
            {
                h.Level = 1;
                h.Short = "Est. ✓ OK";
                d.AppendLine();
                d.AppendLine("Conclusion: the estimate matches the reading (within ±30%; a whole-number reading has its own error).");
            }
            // Check the weekly quota too (a rise of at least 3 points is needed to be meaningful)
            if (data.SevenDay != null && data.SevenDay.ResetsAt.HasValue)
            {
                EnsureWeekHist();
                WeekPoint w0 = null;
                foreach (WeekPoint p in weekHist)
                {
                    if (p.Reset == data.SevenDay.ResetsAt.Value.Ticks && (end - p.T).TotalMinutes <= 150 && data.SevenDay.Percent - p.Pct >= 3 && (w0 == null || p.T > w0.T))
                    {
                        w0 = p;
                    }
                }
                double ew5, eww;
                if (w0 != null && EstimateSince(DateTime.SpecifyKind(w0.T, DateTimeKind.Local).ToUniversalTime(), toUtc, out ew5, out eww))
                {
                    double act = data.SevenDay.Percent - w0.Pct;
                    d.AppendLine();
                    d.AppendLine("Weekly quota: reading " + w0.Pct.ToString("0", CultureInfo.InvariantCulture) + "% → " + data.SevenDay.Percent.ToString("0", CultureInfo.InvariantCulture) + "% (up " + act.ToString("0", CultureInfo.InvariantCulture)
                        + " points), estimate " + eww.ToString("0.0", CultureInfo.InvariantCulture) + " points, ratio " + (eww / act).ToString("0.00", CultureInfo.InvariantCulture));
                }
            }
            h.Detail = d.ToString();
            return h;
        }

        private void ShowHealthDialog()
        {
            healthAt = DateTime.MinValue;
            HealthInfo h = ComputeHealth();
            string text = h.Detail.Length > 0 ? h.Detail : "Nothing to show.";
            MessageBox.Show(this, text, "Estimate self-check", MessageBoxButtons.OK, h.Level >= 2 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private Color HealthColor(HealthInfo h)
        {
            return h.Level == 2 ? StateColor(CacheState.Urgent) : h.Level == 3 ? StateColor(CacheState.Soon) : TextDim;
        }

        // ------------------------------------------------------------ Hourly usage trend chart
        private string trendKey = "";
        private double[] trendP5, trendPw, trendUsd;
        private string[] trendLabel;
        private bool trendHistMissing;
        private int trendUnknown, trendDerived, trendAll;                       // Number of messages with no factor / with a derived factor, and the total number of messages
        private List<KeyValuePair<DateTime, string>> trendSwitches = new List<KeyValuePair<DateTime, string>>();   // Account / plan switches inside the range (local time, description)
        private readonly RectangleF[] trendTabs = new RectangleF[2];

        private void BuildTrend()
        {
            int n = trendRange == 0 ? 24 : 7;
            DateTime nowL = DateTime.Now;
            DateTime start = trendRange == 0 ? new DateTime(nowL.Year, nowL.Month, nowL.Day, nowL.Hour, 0, 0).AddHours(-(n - 1)) : nowL.Date.AddDays(-(n - 1));
            string key = trendRange + "|" + (speedHist != null ? speedHist.Count : -1) + "|" + (quota != null ? quota.Models.Count : -1) + "|" + start.Ticks + "|" + (usageSnap != null ? usageSnap.Signature : "")
                + "|" + nowL.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            if (key == trendKey && trendP5 != null)
            {
                return;
            }
            trendKey = key;
            trendP5 = new double[n];
            trendPw = new double[n];
            trendUsd = new double[n];
            trendLabel = new string[n];
            for (int i = 0; i < n; i++)
            {
                trendLabel[i] = trendRange == 0 ? start.AddHours(i).Hour.ToString("00", CultureInfo.InvariantCulture) : "Week" + Weekday[(int)start.AddDays(i).DayOfWeek];
            }
            DateTime startUtc = start.ToUniversalTime();
            DateTime histCut = DateTime.UtcNow.AddMinutes(-180);
            trendHistMissing = speedHist == null || !Has(Feat.History);
            trendUnknown = 0;
            trendDerived = 0;
            trendAll = 0;
            // Each message is converted with the factor of the account:plan it belonged to at the time (after an account / plan change, quota from different periods is not the same thing)
            AccountIndex aidx = AccountStore.Index();
            trendSwitches = new List<KeyValuePair<DateTime, string>>();
            foreach (TimelineEntry te in AccountStore.TimelineCopy())
            {
                if (te.Utc >= startUtc)
                {
                    Account a = AccountStore.Get(te.AccountId);
                    trendSwitches.Add(new KeyValuePair<DateTime, string>(te.Utc.ToLocalTime(), (a != null ? a.Id : te.AccountId) + "·" + PlanInfo.ShortOf(te.Plan)));
                }
            }
            Action<UsageTracker.RecentUse> add = delegate(UsageTracker.RecentUse u)
            {
                DateTime lt = u.Utc.ToLocalTime();
                int idx = trendRange == 0 ? (int)Math.Floor((lt - start).TotalHours) : (int)Math.Floor((lt.Date - start).TotalDays);
                if (idx < 0 || idx >= n)
                {
                    return;
                }
                trendAll++;
                string k = aidx.Empty ? AccountStore.CurrentKey : aidx.AccountAt(u.Utc) + ":" + aidx.PlanAt(u.Utc);
                QuotaTable q = TableFor(k);
                double p5, pw;
                if (q != null && q.PlanDerived)
                {
                    trendDerived++;         // Factors derived from plan multipliers are unreliable (they can be off by several times), so they are not drawn
                }
                else if (TablePct(q, u, false, out p5))
                {
                    trendP5[idx] += p5;
                    if (TablePct(q, u, true, out pw))
                    {
                        trendPw[idx] += pw;
                    }
                }
                else
                {
                    trendUnknown++;
                }
                double? usd = UsdOf(u.Model, u.Tok);
                if (usd.HasValue)
                {
                    trendUsd[idx] += usd.Value;
                }
            };
            if (speedHist != null && Has(Feat.History))
            {
                foreach (KeyValuePair<string, List<UsageTracker.RecentUse>> kv in speedHist)
                {
                    bool live = usageSnap.Live.ContainsKey(kv.Key);
                    List<UsageTracker.RecentUse> l = kv.Value;
                    for (int i = l.Count - 1; i >= 0; i--)
                    {
                        if (l[i].Utc < startUtc)
                        {
                            break;
                        }
                        if (live && l[i].Utc >= histCut)
                        {
                            continue;       // The last 3 hours come from the live tracking (fresher), so nothing is counted twice
                        }
                        add(l[i]);
                    }
                }
            }
            foreach (KeyValuePair<string, UsageTracker.LiveInfo> kv in usageSnap.Live)
            {
                foreach (UsageTracker.RecentUse u in kv.Value.Recent)
                {
                    if (u.Utc >= startUtc && (u.Utc >= histCut || speedHist == null || !Has(Feat.History)))
                    {
                        add(u);
                    }
                }
            }
        }

        private void DrawTrendView(Graphics g, float pad, float inner, float s, float top)
        {
            BuildTrend();
            LoadQuota();
            int n = trendP5.Length;
            bool hourly = trendRange == 0;
            double[] vals = hourly ? trendP5 : trendPw;
            double line = hourly ? 20.0 : 100.0 / 7.0;                  // Speed that fills the whole window: 5-hour quota 20 %/hour, weekly quota about 14.3 %/day
            string unit = hourly ? "%/h" : "%/day";
            float y0 = top + 128f * s;

            // Top row: title + range switch
            DrawTextRect(g, hourly ? "5-hour quota used per hour" : "Weekly quota used per day", fSmall, TextMain, new RectangleF(pad, y0, inner * 0.6f, 14f * s), StringAlignment.Near);
            Tip("trtitle", new RectangleF(pad, y0, inner * 0.6f, 14f * s), hourly
                ? "5-hour quota used per hour\nEach bar = the 5-hour quota used in that hour (in percentage points). Converted with the factors of the account:plan in force at the time."
                : "Weekly quota used per day\nEach bar = the weekly quota used on that day (in percentage points). Converted with the factors of the account:plan in force at the time.");
            string[] tabs = { "24h", "7d" };
            float tw = 46f * s;
            for (int i = 0; i < 2; i++)
            {
                RectangleF r = new RectangleF(pad + inner - tw * (2 - i), y0 - 1f * s, tw, 15f * s);
                trendTabs[i] = r;
                Tip("trtab" + i, r, i == 0 ? "24 hours\nOne bar per hour, showing the last 24 hours." : "7 days\nOne bar per day, showing the last 7 days.", 1);
                bool on = i == trendRange;
                if (on)
                {
                    using (SolidBrush b = new SolidBrush(SkinDraw.Mix(Bg, TextMain, 0.22f)))
                    {
                        g.FillRectangle(b, r);
                    }
                }
                DrawTextRect(g, tabs[i], fSmall, on ? TextMain : TextDim, r, StringAlignment.Center);
            }

            // Chart
            float cx = pad + 26f * s, cw = inner - 26f * s;
            // the chart is 96 high in the default window and shrinks (down to 36) in a short window so that the two summary lines below it stay inside
            float cy = y0 + 20f * s, ch = Math.Max(36f * s, Math.Min(96f * s, ClientSize.Height - top - 201f * s));
            double max = line * 1.15;
            for (int i = 0; i < n; i++)
            {
                max = Math.Max(max, vals[i] * 1.08);
            }
            double[] nice = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 };
            double step = nice[nice.Length - 1];
            foreach (double nv in nice)
            {
                if (max / nv <= 5.01)
                {
                    step = nv;
                    break;
                }
            }
            if (max / step > 5.01)
            {
                step = Math.Pow(10, Math.Ceiling(Math.Log10(max / 5.0)));       // Larger than the largest tick (abnormal coefficient): take a power of 10 as the step, so that the grid loop below does not run hundreds of millions of times
            }
            max = Math.Ceiling(max / step) * step;
            using (Pen grid = new Pen(SkinDraw.Mix(Bg, TextDim, 0.25f)))
            {
                for (double v = 0; v <= max + 0.001; v += step)
                {
                    float yy = cy + ch - (float)(v / max) * ch;
                    g.DrawLine(grid, cx, yy, cx + cw, yy);
                    DrawTextRect(g, v.ToString("0", CultureInfo.InvariantCulture), fSmall, TextDim, new RectangleF(pad - 4f * s, yy - 7f * s, 26f * s, 14f * s), StringAlignment.Far);
                }
            }
            float slot = cw / n;
            float bw = Math.Max(2f, slot * 0.68f);
            int peak = 0;
            for (int i = 0; i < n; i++)
            {
                if (vals[i] > vals[peak])
                {
                    peak = i;
                }
            }
            for (int i = 0; i < n; i++)
            {
                float h = (float)(vals[i] / max) * ch;
                RectangleF r = new RectangleF(cx + slot * i + (slot - bw) / 2f, cy + ch - h, bw, Math.Max(vals[i] > 0 ? 1.5f : 0f, h));
                TipTrendBar(i, new RectangleF(cx + slot * i, cy, slot, ch), hourly, vals[i], trendUsd[i], trendLabel[i], line, i == n - 1);
                Color c = vals[i] > line ? StateColor(CacheState.Urgent) : Accent;
                if (i == n - 1)
                {
                    c = Color.FromArgb(150, c);     // The last slot is the hour / day that has not finished yet
                }
                using (SolidBrush b = new SolidBrush(c))
                {
                    g.FillRectangle(b, r);
                }
                bool labelHere = hourly ? (i % 4 == 0 || i == n - 1) : true;
                if (labelHere)
                {
                    DrawTextRect(g, trendLabel[i], fSmall, TextDim, new RectangleF(cx + slot * i - 6f * s, cy + ch + 2f * s, slot + 12f * s, 13f * s), StringAlignment.Center);
                }
            }
            // Moment of an account / plan switch (the bars before it were converted with the basis in force at the time)
            if (hourly)
            {
                DateTime t0 = DateTime.Now;
                DateTime startL = new DateTime(t0.Year, t0.Month, t0.Day, t0.Hour, 0, 0).AddHours(-(n - 1));
                int swIdx = 0;
                foreach (KeyValuePair<DateTime, string> sw in trendSwitches)
                {
                    float sx = cx + (float)((sw.Key - startL).TotalHours / n) * cw;
                    if (sx < cx || sx > cx + cw)
                    {
                        continue;
                    }
                    using (Pen sp = new Pen(StateColor(CacheState.Waiting), 1f))
                    {
                        sp.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                        g.DrawLine(sp, sx, cy, sx, cy + ch);
                    }
                    DrawTextRect(g, "→" + sw.Value, fSmall, StateColor(CacheState.Waiting), new RectangleF(sx + 2f * s, cy + (swIdx++ % 2) * 13f * s, 70f * s, 13f * s), StringAlignment.Near);
                    Tip("trsw" + swIdx, new RectangleF(sx - 3f * s, cy, 6f * s, ch), "Account / plan change → " + sw.Value + "\nQuota after this line is converted with the new account:plan; bars before the line were converted with the basis in force at the time.", 2);
                }
            }
            // Reference line for 'exactly fills the whole window'
            float ly = cy + ch - (float)(line / max) * ch;
            using (Pen lp = new Pen(StateColor(CacheState.Soon), 1f))
            {
                lp.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawLine(lp, cx, ly, cx + cw, ly);
            }
            DrawTextRect(g, (hourly ? "Speed that uses up the 5-hour quota: " : "Speed that uses up the weekly quota: ") + line.ToString("0.#", CultureInfo.InvariantCulture) + unit, fSmall, StateColor(CacheState.Soon),
                new RectangleF(cx + 2f * s, ly - 14f * s, cw, 13f * s), StringAlignment.Near);
            Tip("trline", new RectangleF(cx + 2f * s, ly - 14f * s, MeasureW(g, (hourly ? "Speed that uses up the 5-hour quota: " : "Speed that uses up the weekly quota: ") + line.ToString("0.#", CultureInfo.InvariantCulture) + unit) + 4f * s, 13f * s),
                hourly ? "Full-quota speed\nThe speed at which the 5-hour quota is used up in exactly 5 hours = 20 %/hour. A bar above this line (red) = if that hour's intensity lasted 5 hours, it would use up the 5-hour quota."
                : "Full-quota speed\nThe speed at which the weekly quota is used up in exactly 7 days ≈ 14.3 %/day. A bar above this line (red) = if that day's intensity lasted a week, it would use up the weekly quota.", 1);

            // The two summary lines at the bottom
            double sum = 0, usd = 0;
            int active = 0;
            for (int i = 0; i < n; i++)
            {
                sum += vals[i];
                usd += trendUsd[i];
                if (vals[i] > 0.05)
                {
                    active++;
                }
            }
            float sy = cy + ch + 18f * s;
            string total = (quota != null && !quota.Stale ? (hourly ? "Total " + sum.ToString("0", CultureInfo.InvariantCulture) + "% of the 5-hour quota (≈ " + (sum / 100).ToString("0.0", CultureInfo.InvariantCulture) + " windows)"
                : "Total " + sum.ToString("0.#", CultureInfo.InvariantCulture) + "% of the weekly quota") : "The current account has no quota factors yet, so only dollar amounts can be shown")
                + " · API value " + UsdText(usd);
            DrawTextFit(g, total, fSmall, TextMain, new RectangleF(pad, sy, inner, 14f * s), StringAlignment.Near);
            Tip("trsum", new RectangleF(pad, sy, inner, 14f * s), "Total\nAll bars in this period added up: how much quota that is (the number in brackets = how many 5-hour windows it equals), plus the amount at official API prices (API-equivalent, not an actual charge).");
            string peakText = vals[peak] > 0.05
                ? "Peak " + (hourly ? trendLabel[peak] + ":00" : trendLabel[peak]) + " " + vals[peak].ToString("0", CultureInfo.InvariantCulture) + unit + " · " + (hourly ? "active-hour " : "active-day ") + "avg "
                    + (active > 0 ? (sum / active).ToString("0", CultureInfo.InvariantCulture) : "0") + unit
                : "No usage in this period";
            if (trendUnknown > 0 || trendDerived > 0)
            {
                peakText += " · not plotted: " + (trendUnknown + trendDerived) + " messages (no reliable factor for the account:plan at the time)";
            }
            if (trendHistMissing)
            {
                peakText = "Only the last 3 hours of data: " + (Has(Feat.History) ? "log history is still being read, please wait" : "'Speed history reading' is off (turn it on in settings to see 24 hours / 7 days)");
            }
            DrawTextFit(g, peakText, fSmall, trendHistMissing ? StateColor(CacheState.Soon) : TextDim, new RectangleF(pad, sy + 15f * s, inner, 14f * s), StringAlignment.Near);
            Tip("trpeak", new RectangleF(pad, sy + 15f * s, inner, 14f * s), "Peak and average\nPeak = the highest slot; 'active-hour / active-day avg' only counts slots that have usage.\n'Not plotted: N messages' = for those records the account:plan at the time had no reliable quota factor, so they cannot be converted to percentages.");
        }

        private int TrendTabAt(Point p)
        {
            if (!showUsage || usageMode != 5)
            {
                return -1;
            }
            for (int i = 0; i < 2; i++)
            {
                if (trendTabs[i].Contains(p))
                {
                    return i;
                }
            }
            return -1;
        }

        // ------------------------------------------------------------ Weekly report
        private string reportPending;       // The file to open when the notification is clicked
        private int reportBusy;
        private DateTime reportFailUtc = DateTime.MinValue;     // When the "weekly report failed" balloon was last shown (automatic generation retries every hour; the failure balloon is shown at most once a day)
        private DateTime reportCheckUtc = DateTime.MinValue;

        private static string LogDir()
        {
            return AppPaths.DataDir;
        }

        private static DateTime MondayOf(DateTime d)
        {
            int back = ((int)d.DayOfWeek + 6) % 7;
            return d.Date.AddDays(-back);
        }

        // Generate the weekly report (background thread). last = last week (the complete week); otherwise this week (up to now). open = open it as soon as it is generated
        private void MakeReport(bool last, bool open)
        {
            if (Interlocked.CompareExchange(ref reportBusy, 1, 0) != 0)
            {
                Balloon("Weekly report", "Generating, please wait.");
                return;
            }
            DateTime start = MondayOf(DateTime.Now).AddDays(last ? -7 : 0);
            DateTime end = start.AddDays(7);
            string logDir = LogDir();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null;
                string err = null;
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    path = WeeklyReport.Generate(logDir, start, end, end > DateTime.Now);
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
                Interlocked.Exchange(ref reportBusy, 0);
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (err != null)
                        {
                            if (open || (DateTime.UtcNow - reportFailUtc).TotalHours > 24)
                            {
                                reportFailUtc = DateTime.UtcNow;
                                Balloon("Weekly report failed", err);
                            }
                        }
                        else if (open)
                        {
                            OpenFile(path);
                        }
                        else
                        {
                            reportPending = path;
                            Balloon("Weekly report ready", start.ToString("MM-dd") + " ~ " + end.AddDays(-1).ToString("MM-dd") + " summary. Click this notification to open it.");
                        }
                    }));
                }
                catch (Exception)
                {
                }
            });
        }

        private static void OpenFile(string path)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(path);
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception)
            {
            }
        }

        private void OpenReportFolder()
        {
            string dir = Path.Combine(LogDir(), "weekly_reports");
            Directory.CreateDirectory(dir);
            OpenFile(dir);
        }

        // Checked once an hour: if the last complete week has no report yet, generate one in the background and notify (only when 'automatic weekly summary' is on)
        private void CheckWeeklyReport()
        {
            if (!Has(Feat.Weekly) || paused || DateTime.UtcNow - reportCheckUtc < TimeSpan.FromHours(1))
            {
                return;
            }
            reportCheckUtc = DateTime.UtcNow;
            DateTime start = MondayOf(DateTime.Now).AddDays(-7);
            string path = WeeklyReport.PathFor(LogDir(), start);
            string detail = Path.Combine(LogDir(), "usage_detail_v2.csv");
            // The log has not been written since last Monday (Claude Code was not used last week or this week): do not generate an empty weekly report and do not show a balloon
            if (!File.Exists(path) && File.Exists(detail) && File.GetLastWriteTime(detail) >= start)
            {
                MakeReport(true, false);
            }
        }

        private void InitExtras()
        {
            MailStore.Load();
            InitTips();
            if (tray != null)
            {
                tray.BalloonTipClicked += delegate
                {
                    if (reportPending != null)
                    {
                        string p = reportPending;
                        reportPending = null;
                        OpenFile(p);
                    }
                };
            }
        }
    }
}
