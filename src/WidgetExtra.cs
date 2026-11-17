// Widget 的新增功能（v31）：功能开关和档位、会话「成本」列、估算健康度自检、小时用量趋势图、周报入口。
// 和 Main.cs 里的 Widget 是同一个类（partial）。

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
        // ------------------------------------------------------------ 功能开关 / 显示内容 / 档位
        private int profile;                                        // 0 全开 1 推荐 2 精简 3 自定义
        // 新装的默认档位是「推荐」：功能全开，但隐藏价值不高的「输入」列、API 视图和 DeepSeek 费用行（它们在「全开」档位里都能打开）
        private int fmask = Feat.Preset(1)[0], vmask = Feat.Preset(1)[1], smask = Feat.Preset(1)[2], dmask = Feat.Preset(1)[3];
        private bool sawFmask;
        private int costScope;                                      // 成本列：0 = 会话累计（全部历史），1 = 当前时间标签里的
        private int trendRange;                                     // 趋势图：0 = 最近 24 小时（每小时），1 = 最近 7 天（每天）

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

        // 把 alert / log 这两个老开关和 fmask 对齐（老菜单、老设置文件里它们是单独的）
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
            speedHistUtc = DateTime.MinValue;       // 开了历史 / 成本列：马上重读一遍
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
            ToolStripMenuItem prof = new ToolStripMenuItem("调度策略");
            ToolStripMenuItem[] items = new ToolStripMenuItem[4];
            for (int i = 0; i < 3; i++)
            {
                int p = i;
                items[i] = new ToolStripMenuItem(Feat.ProfileNames[i] + " —— " + Feat.ProfileDesc[i]);
                items[i].Click += delegate { ApplyPreset(p); };
                prof.DropDownItems.Add(items[i]);
            }
            items[3] = new ToolStripMenuItem("自定义（在「功能与显示设置」里手动调整）");
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
            ToolStripMenuItem rep = new ToolStripMenuItem("周报");
            rep.DropDownItems.Add("生成并打开本周小结", null, delegate { MakeReport(false, true); });
            rep.DropDownItems.Add("生成并打开上周小结", null, delegate { MakeReport(true, true); });
            rep.DropDownItems.Add("打开周报文件夹", null, delegate { OpenReportFolder(); });
            return rep;
        }

        private void AddFeatureMenu(ContextMenuStrip menu)
        {
            menu.Items.Add("功能与显示设置…", null, delegate { OpenFeatureSettings(); });
            menu.Items.Add(BuildProfileItem());
            menu.Items.Add("估算自检详情…", null, delegate { ShowHealthDialog(); });
            menu.Items.Add(BuildReportItem());
            ToolStripMenuItem pauseItem = BuildPauseItem();
            menu.Opening += delegate { pauseItem.Checked = paused; };
            menu.Items.Add(pauseItem);
            ToolStripMenuItem tipsItem = BuildTipsItem();
            menu.Opening += delegate { tipsItem.Checked = tipsOn; };
            menu.Items.Add(tipsItem);
            menu.Items.Add("邮件提醒…", null, delegate { OpenMailForm(); });
        }

        // ------------------------------------------------------------ 时间标签条右边的「⋯」扩展按钮
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
                    g.FillEllipse(b, cx - d / 2f, cy + i * gap - d / 2f, d, d);        // 竖着的三个点
                }
            }
        }

        // 选择显示哪些时间标签：勾选 = 显示，点一下立即生效、菜单不关；下面是其他常用设置的快捷入口
        private void ShowTabMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem head = new ToolStripMenuItem("显示哪些时间标签（勾选 = 显示）");
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
                        return;         // 至少留一个
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
            m.Items.Add("恢复默认时间标签", null, delegate { ApplyTabMask(TabDefaultMask); });
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
            m.Items.Add(MaskMenu("显示哪些视图", Feat.VNames, vmask, 0, 1));
            m.Items.Add(MaskMenu("会话视图的列", Feat.SNames, smask, 1, 63));
            m.Items.Add(MaskMenu("其他显示内容", Feat.DNames, dmask, 2, 0));
            m.Items.Add(BuildRowHeightItem());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(BuildPauseItem());
            m.Items.Add(BuildTipsItem());
            m.Items.Add(BuildAlertItem());
            m.Items.Add(BuildExportItem());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(BuildProfileItem());
            m.Items.Add("功能与显示设置…", null, delegate { OpenFeatureSettings(); });
            m.Items.Add("估算自检详情…", null, delegate { ShowHealthDialog(); });
            m.Items.Add(BuildReportItem());
            m.Closed += delegate { BeginInvoke(new MethodInvoker(delegate { m.Dispose(); })); };
            m.Show(this, new Point((int)tabMoreRect.Left, (int)tabMoreRect.Bottom));
        }

        // kind：0 视图（vmask）、1 会话列（smask）、2 其他显示（dmask）。mustKeep：这些位里至少要留一个
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
                        return;     // 至少留一列
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
            logRefreshUtc = DateTime.MinValue;      // 新显示的日志类标签（今天 / 昨天 / 本周 / 自定义）马上重新统计
            SaveSettings();
            StartLogRefresh();
            Redraw();
        }

        // ------------------------------------------------------------ 跟着账号:计划走
        private DateTime keyStartCache = DateTime.MinValue;
        private DateTime keyStartAt = DateTime.MinValue;

        // 当前口径（账号:计划）是从什么时候开始的（UTC）；没有时间线就是 MinValue。每 20 秒刷新一次
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

        // 某个口径自己的系数表（趋势图按「当时的账号:计划」折算用）。每 5 分钟重读
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
                    q = QuotaTable.Load(Path.Combine(LogDir(), "额度换算.csv"), key);
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

        // ------------------------------------------------------------ 成本列
        private Dictionary<string, Dictionary<string, long[]>> sessionLife;      // 会话 → 模型 → token（日志里这个会话的全部历史）
        private Dictionary<string, double> costBy = new Dictionary<string, double>();   // 本次绘制：会话 → 成本（美元）
        private bool costApprox;                                                  // 累计口径还没读完日志，先用时间段的

        // 成本 = 这个会话的 token 按官方价格折成美元（订阅用户的「API 等价金额」，不是实际扣款）
        private void PrepareCost(UsageTracker.PeriodSummary cur)
        {
            costBy.Clear();
            costApprox = false;
            bool life = costScope == 0 && Has(Feat.History) && sessionLife != null;
            if (costScope == 0 && !life)
            {
                costApprox = true;      // 想看累计但历史没读入（没开 / 还没读完）：暂时显示时间段内的
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
                    costBy[t.Session] = Math.Max(sum, period);      // 日志比实时统计慢一点，取大的
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

        // ------------------------------------------------------------ 估算健康度自检
        // 拿「额度读数涨了多少」核对「按系数估算涨了多少」：读数是服务器给的真值（整数百分比），估算是我们用价格 × 系数算的。
        // 两边差得远，说明系数不对（推算的 / 刚换套餐 / 换账号），或者有别处（网页、别的电脑）在用同一个额度
        private sealed class FivePoint
        {
            public DateTime T;
            public double Pct;
            public long Reset;
        }

        private sealed class HealthInfo
        {
            public int Level;               // 0 数据不够 1 吻合 2 估算偏高 3 估算偏低
            public string Short = "";       // 合计行里的小提示
            public string Line = "";        // 替换最下面一行的整句提示（只有偏差大时）
            public string Detail = "";      // 详情对话框
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

        // 所有会话在 [fromUtc, toUtc] 里消耗的额度百分点（5 小时 / 每周），只用实时追踪里的最近 3 小时
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
            d.AppendLine("口径（账号:计划）：" + (key.Length == 0 ? "未设置" : key));
            if (quota == null)
            {
                h.Detail = d + "还没有额度换算表（日志\\额度换算.csv），没法核对。";
                return h;
            }
            if (quota.Stale)
            {
                h.Detail = d + "当前口径还没有自己的系数（刚换账号 / 套餐），速度和估算都显示「—」，等数据够了会自动算出来。";
                return h;
            }
            d.AppendLine(quota.PlanDerived ? "系数来源：按套餐倍率从别的口径推算（不可靠，可能差好几倍）" : "系数来源：本口径自己的数据实测");
            if (quota.CalibErr >= 0)
            {
                d.AppendLine("事前预测误差：≈" + (quota.CalibErr * 100).ToString("0", CultureInfo.InvariantCulture) + "%（" + quota.CalibBlocks + " 块数据）");
            }
            if (data == null || data.FiveHour == null || !data.FiveHour.ResetsAt.HasValue)
            {
                h.Detail = d + "还没有取到额度读数，没法核对。";
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
                h.Detail = d + "近 2.5 小时里 5 小时额度读数涨幅不足 6 个点（整数百分比误差太大），暂时核对不了。用量上来之后会自动核对。";
                return h;
            }
            double est5, estW;
            DateTime fromUtc = DateTime.SpecifyKind(p0.T, DateTimeKind.Local).ToUniversalTime();
            DateTime toUtc = DateTime.SpecifyKind(end, DateTimeKind.Local).ToUniversalTime();
            if (!EstimateSince(fromUtc, toUtc, out est5, out estW))
            {
                h.Detail = d + "这段时间没有可折算的用量（模型没有系数），核对不了。";
                return h;
            }
            double actual = pctNow - p0.Pct;
            double ratio = est5 / actual;
            double span = (end - p0.T).TotalMinutes;
            d.AppendLine();
            d.AppendLine("核对窗口：" + p0.T.ToString("HH:mm") + " → " + end.ToString("HH:mm") + "（" + span.ToString("0", CultureInfo.InvariantCulture) + " 分钟）");
            d.AppendLine("5 小时额度读数：" + p0.Pct.ToString("0", CultureInfo.InvariantCulture) + "% → " + pctNow.ToString("0", CultureInfo.InvariantCulture) + "%，涨了 " + actual.ToString("0", CultureInfo.InvariantCulture) + " 个点（整数读数，±1 个点的误差）");
            d.AppendLine("按系数估算：" + est5.ToString("0.0", CultureInfo.InvariantCulture) + " 个点");
            d.AppendLine("估算 ÷ 读数 = " + ratio.ToString("0.00", CultureInfo.InvariantCulture));
            string reasons = quota.PlanDerived ? "系数是推算的" : "";
            if (ratio > 1.35)
            {
                h.Level = 2;
                h.Short = "估算偏高 " + Times(ratio);
                h.Line = "⚠ 估算比额度读数涨得快 " + Times(ratio) + "（近 " + span.ToString("0", CultureInfo.InvariantCulture) + " 分钟估 " + est5.ToString("0", CultureInfo.InvariantCulture) + " 实涨 " + actual.ToString("0", CultureInfo.InvariantCulture) + "）：速度 / 触顶预测会偏高"
                    + (reasons.Length > 0 ? "，" + reasons : "，系数可能偏大");
                d.AppendLine();
                d.AppendLine("结论：估算偏高。速度和触顶预测会比真实情况更悲观。");
                d.AppendLine("可能的原因：" + (quota.PlanDerived ? "当前口径的系数是按套餐倍率推算的（数据还不够实测）；" : "") + "刚换了套餐 / 账号，系数还在用旧数据；某些模型的价格表和实际不一致。");
            }
            else if (ratio < 0.72)
            {
                h.Level = 3;
                h.Short = "估算偏低 " + Times(1 / Math.Max(ratio, 0.01));
                h.Line = "⚠ 额度读数比估算涨得快 " + Times(1 / Math.Max(ratio, 0.01)) + "（近 " + span.ToString("0", CultureInfo.InvariantCulture) + " 分钟估 " + est5.ToString("0", CultureInfo.InvariantCulture) + " 实涨 " + actual.ToString("0", CultureInfo.InvariantCulture) + "）：可能有别处在用同一个额度";
                d.AppendLine();
                d.AppendLine("结论：估算偏低。");
                d.AppendLine("可能的原因：同一账号在网页 / 手机 / 别的电脑上也在用额度（本机记录不到）；系数偏小；有本机没记录到的会话。");
            }
            else
            {
                h.Level = 1;
                h.Short = "估算✓吻合";
                d.AppendLine();
                d.AppendLine("结论：估算和读数吻合（差在 ±30% 以内，整数读数本身就有误差）。");
            }
            // 每周额度也核对一次（涨幅够 3 个点才有意义）
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
                    d.AppendLine("每周额度：读数 " + w0.Pct.ToString("0", CultureInfo.InvariantCulture) + "% → " + data.SevenDay.Percent.ToString("0", CultureInfo.InvariantCulture) + "%（涨 " + act.ToString("0", CultureInfo.InvariantCulture)
                        + " 个点），估算 " + eww.ToString("0.0", CultureInfo.InvariantCulture) + " 个点，比值 " + (eww / act).ToString("0.00", CultureInfo.InvariantCulture));
                }
            }
            h.Detail = d.ToString();
            return h;
        }

        private void ShowHealthDialog()
        {
            healthAt = DateTime.MinValue;
            HealthInfo h = ComputeHealth();
            string text = h.Detail.Length > 0 ? h.Detail : "没有可显示的内容。";
            MessageBox.Show(this, text, "估算健康度自检", MessageBoxButtons.OK, h.Level >= 2 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private Color HealthColor(HealthInfo h)
        {
            return h.Level == 2 ? StateColor(CacheState.Urgent) : h.Level == 3 ? StateColor(CacheState.Soon) : TextDim;
        }

        // ------------------------------------------------------------ 小时用量趋势图
        private string trendKey = "";
        private double[] trendP5, trendPw, trendUsd;
        private string[] trendLabel;
        private bool trendHistMissing;
        private int trendUnknown, trendDerived, trendAll;                       // 没有系数 / 用的是推算系数的消息数、总消息数
        private List<KeyValuePair<DateTime, string>> trendSwitches = new List<KeyValuePair<DateTime, string>>();   // 范围内的账号 / 套餐切换（本地时间，说明）
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
                trendLabel[i] = trendRange == 0 ? start.AddHours(i).Hour.ToString("00", CultureInfo.InvariantCulture) : "周" + Weekday[(int)start.AddDays(i).DayOfWeek];
            }
            DateTime startUtc = start.ToUniversalTime();
            DateTime histCut = DateTime.UtcNow.AddMinutes(-180);
            trendHistMissing = speedHist == null || !Has(Feat.History);
            trendUnknown = 0;
            trendDerived = 0;
            trendAll = 0;
            // 每条消息按它当时的账号:计划的系数折算（换过账号 / 套餐的话，不同时间段的额度不是同一个东西）
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
                    trendDerived++;         // 按套餐倍率推算的系数不可靠（能差好几倍），不画进去
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
                            continue;       // 最近 3 小时的用实时追踪的（更新鲜），免得算两遍
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
            double line = hourly ? 20.0 : 100.0 / 7.0;                  // 撑满整个窗口的速度：5 小时额度 20 %/小时，每周额度约 14.3 %/天
            string unit = hourly ? "%/小时" : "%/天";
            float y0 = top + 128f * s;

            // 顶上一行：标题 + 范围切换
            DrawTextRect(g, hourly ? "每小时消耗的 5 小时额度" : "每天消耗的每周额度", fSmall, TextMain, new RectangleF(pad, y0, inner * 0.6f, 14f * s), StringAlignment.Near);
            Tip("trtitle", new RectangleF(pad, y0, inner * 0.6f, 14f * s), hourly
                ? "每小时消耗的 5 小时额度\n每根柱子 = 那一小时里用掉的 5 小时额度（单位：百分点）。折算用的是当时那个「账号:计划」的系数。"
                : "每天消耗的每周额度\n每根柱子 = 那一天用掉的每周额度（单位：百分点）。折算用的是当时那个「账号:计划」的系数。");
            string[] tabs = { "24小时", "7天" };
            float tw = 46f * s;
            for (int i = 0; i < 2; i++)
            {
                RectangleF r = new RectangleF(pad + inner - tw * (2 - i), y0 - 1f * s, tw, 15f * s);
                trendTabs[i] = r;
                Tip("trtab" + i, r, i == 0 ? "24 小时\n每小时一根柱子，看最近 24 小时。" : "7 天\n每天一根柱子，看最近 7 天。", 1);
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

            // 图
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
                step = Math.Pow(10, Math.Ceiling(Math.Log10(max / 5.0)));       // 比最大的刻度还大（系数异常）：按 10 的幂取刻度，免得下面的网格循环转上亿次
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
                    c = Color.FromArgb(150, c);     // 最后一格是还没走完的这一小时 / 这一天
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
            // 换账号 / 套餐的时刻（之前的柱子是按当时那个口径折算的）
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
                    Tip("trsw" + swIdx, new RectangleF(sx - 3f * s, cy, 6f * s, ch), "换账号 / 套餐 → " + sw.Value + "\n这条线之后的额度按新的「账号:计划」折算；线之前的柱子是按当时那个口径折算的。", 2);
                }
            }
            // 「刚好撑满整个窗口」的参考线
            float ly = cy + ch - (float)(line / max) * ch;
            using (Pen lp = new Pen(StateColor(CacheState.Soon), 1f))
            {
                lp.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawLine(lp, cx, ly, cx + cw, ly);
            }
            DrawTextRect(g, (hourly ? "撑满 5 小时额度的速度 " : "撑满每周额度的速度 ") + line.ToString("0.#", CultureInfo.InvariantCulture) + unit, fSmall, StateColor(CacheState.Soon),
                new RectangleF(cx + 2f * s, ly - 14f * s, cw, 13f * s), StringAlignment.Near);
            Tip("trline", new RectangleF(cx + 2f * s, ly - 14f * s, MeasureW(g, (hourly ? "撑满 5 小时额度的速度 " : "撑满每周额度的速度 ") + line.ToString("0.#", CultureInfo.InvariantCulture) + unit) + 4f * s, 13f * s),
                hourly ? "撑满速度\n5 小时额度正好在 5 小时里用完的速度 = 20 %/小时。柱子高过这条线（红色）= 那一小时的强度如果持续 5 小时，就会把 5 小时额度用光。"
                : "撑满速度\n每周额度正好在 7 天里用完的速度 ≈ 14.3 %/天。柱子高过这条线（红色）= 那一天的强度如果持续一周，就会把每周额度用光。", 1);

            // 底下两行小结
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
            string total = (quota != null && !quota.Stale ? (hourly ? "合计 " + sum.ToString("0", CultureInfo.InvariantCulture) + "% 的 5 小时额度（≈ " + (sum / 100).ToString("0.0", CultureInfo.InvariantCulture) + " 个窗口）"
                : "合计 " + sum.ToString("0.#", CultureInfo.InvariantCulture) + "% 的每周额度") : "当前账号还没有额度系数，只能显示金额")
                + " · API 等价 " + UsdText(usd);
            DrawTextFit(g, total, fSmall, TextMain, new RectangleF(pad, sy, inner, 14f * s), StringAlignment.Near);
            Tip("trsum", new RectangleF(pad, sy, inner, 14f * s), "合计\n这段时间所有柱子加起来：折合多少额度（括号里 = 相当于几个 5 小时窗口），再加上按官方 API 价折算的金额（API 等价，不是实际扣费）。");
            string peakText = vals[peak] > 0.05
                ? "峰值 " + (hourly ? trendLabel[peak] + ":00" : trendLabel[peak]) + " " + vals[peak].ToString("0", CultureInfo.InvariantCulture) + unit + " · " + (hourly ? "活跃小时" : "活跃天") + "平均 "
                    + (active > 0 ? (sum / active).ToString("0", CultureInfo.InvariantCulture) : "0") + unit
                : "这段时间没有用量";
            if (trendUnknown > 0 || trendDerived > 0)
            {
                peakText += " · 未画入 " + (trendUnknown + trendDerived) + " 条（当时的账号:计划没有可靠系数）";
            }
            if (trendHistMissing)
            {
                peakText = "只有最近 3 小时的数据：" + (Has(Feat.History) ? "日志历史还在读取，稍等" : "没开「速度历史读取」（设置里打开后能看 24 小时 / 7 天）");
            }
            DrawTextFit(g, peakText, fSmall, trendHistMissing ? StateColor(CacheState.Soon) : TextDim, new RectangleF(pad, sy + 15f * s, inner, 14f * s), StringAlignment.Near);
            Tip("trpeak", new RectangleF(pad, sy + 15f * s, inner, 14f * s), "峰值与平均\n峰值 = 最高的那一格；「活跃小时 / 天平均」只算有用量的那些格。\n「未画入 N 条」= 那些记录当时的「账号:计划」没有可靠的额度系数，没法折算成百分比。");
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

        // ------------------------------------------------------------ 周报
        private string reportPending;       // 通知被点击时要打开的文件
        private int reportBusy;
        private DateTime reportFailUtc = DateTime.MinValue;     // 上一次弹「周报生成失败」的时间（自动生成每小时重试一次，失败通知一天最多弹一次）
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

        // 生成周报（后台线程）。last = 上周（完整的一周）；否则是本周（到现在为止）。open = 生成完直接打开
        private void MakeReport(bool last, bool open)
        {
            if (Interlocked.CompareExchange(ref reportBusy, 1, 0) != 0)
            {
                Balloon("周报", "正在生成，请稍等。");
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
                                Balloon("周报生成失败", err);
                            }
                        }
                        else if (open)
                        {
                            OpenFile(path);
                        }
                        else
                        {
                            reportPending = path;
                            Balloon("周报已生成", start.ToString("MM-dd") + " ~ " + end.AddDays(-1).ToString("MM-dd") + " 的小结。点这条通知打开。");
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
            string dir = Path.Combine(LogDir(), "周报");
            Directory.CreateDirectory(dir);
            OpenFile(dir);
        }

        // 每小时看一次：上一个完整的一周还没有周报，就在后台生成一份并通知（「每周自动小结」开着才做）
        private void CheckWeeklyReport()
        {
            if (!Has(Feat.Weekly) || paused || DateTime.UtcNow - reportCheckUtc < TimeSpan.FromHours(1))
            {
                return;
            }
            reportCheckUtc = DateTime.UtcNow;
            DateTime start = MondayOf(DateTime.Now).AddDays(-7);
            string path = WeeklyReport.PathFor(LogDir(), start);
            string detail = Path.Combine(LogDir(), "用量明细_v2.csv");
            // 日志从上周一之后就没再写过（上周和这周都没用过 Claude Code）：不生成一份空周报、也不弹通知
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
