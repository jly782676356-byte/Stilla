// Widget 的第二批新增功能（v33）：暂停后台读取、时间标签预设、速度窗口菜单、常用自定义时段、窗口大小预设、
// 提醒设置（缓存提前量 / 额度触顶预警 / 邮件提醒）、导出、会话列最上方的「@」邮件提醒按钮。
// 和 Main.cs 里的 Widget 是同一个类（partial）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Stilla
{
    internal sealed partial class Widget
    {
        // ------------------------------------------------------------ 状态
        private bool paused;                                        // 暂停后台读取（打游戏用，不保存，重启后恢复）
        private int alertMin = 10;                                  // 缓存剩多少分钟时弹系统提醒
        private int limitWarn = 90;                                 // 5 小时 / 每周额度用到多少 % 时提醒；0 = 不提醒
        private int tabPreset;                                      // 「我的」时间标签预设（位掩码，0 = 还没存过）
        private int[] crFrom = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };    // 常用自定义时段（距 Epoch 的分钟数）
        private int[] crTo = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private long warnedFive, warnedWeek;                        // 已经为哪个窗口（重置时间）提醒过
        private RectangleF mailBtnRect = RectangleF.Empty;          // 会话列表头的「@」按钮
        private RectangleF pauseRect = RectangleF.Empty;            // 「已暂停」提示（点一下恢复）

        // ------------------------------------------------------------ 暂停后台读取
        private void TogglePause()
        {
            paused = !paused;
            if (logger != null)
            {
                logger.Paused = paused;
            }
            if (!paused)
            {
                // 恢复：马上补一次读取
                lastBgUsageUtc = DateTime.MinValue;
                speedHistUtc = DateTime.MinValue;
                healthAt = DateTime.MinValue;
                RefreshUsage();
            }
            UpdateTrayText();
            Redraw();
        }

        private void DrawPauseNote(Graphics g, float pad, float top, float s)
        {
            pauseRect = RectangleF.Empty;
            if (!paused)
            {
                return;
            }
            // right-aligned next to the view buttons: the skins' titles ("The Usage Edition", "STILLA QUOTA") are wider than the old fixed spot allowed;
            // when the free space is too small for the full sentence, the short variant is used
            float x1 = pad + 180f * s;
            float x2 = modeSwitchX - 8f * s;
            RectangleF r = new RectangleF(x1, top + 6f * s, Math.Max(10f * s, x2 - x1), 16f * s);
            string full = "⏸ 后台读取已暂停（点这里恢复）";
            string text = TextW(g, full, fSmall) * 0.85f <= r.Width ? full : "⏸ 已暂停";
            DrawTextFit(g, text, fSmall, StateColor(CacheState.Soon), r, StringAlignment.Far, 0.85f);
            float tw = Math.Min(r.Width, TextW(g, text, fSmall));
            pauseRect = new RectangleF(r.Right - tw, r.Y, tw, r.Height);
        }

        // ------------------------------------------------------------ 时间标签预设
        private static readonly string[] PresetNames = { "日常（15分钟 / 本5小时 / 今天）", "复盘（今天 / 昨天 / 本周）", "精简（本5小时 / 今天）", "默认", "全部 11 个" };
        private static readonly int[] PresetMasks = { 2 + 32 + 128, 128 + 256 + 512, 32 + 128, 1258, 2047 };

        private ToolStripMenuItem BuildTabPresetItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("时间标签预设");
            for (int i = 0; i < PresetNames.Length; i++)
            {
                int mask = PresetMasks[i];
                ToolStripMenuItem it = new ToolStripMenuItem(PresetNames[i]);
                it.Checked = tabMask == mask;
                it.Click += delegate { ApplyTabMask(mask); };
                root.DropDownItems.Add(it);
            }
            root.DropDownItems.Add(new ToolStripSeparator());
            if (tabPreset != 0)
            {
                ToolStripMenuItem mine = new ToolStripMenuItem("我的预设");
                mine.Checked = tabMask == tabPreset;
                mine.Click += delegate { ApplyTabMask(tabPreset); };
                root.DropDownItems.Add(mine);
            }
            root.DropDownItems.Add("把现在显示的标签存为「我的预设」", null, delegate { tabPreset = tabMask; SaveSettings(); });
            return root;
        }

        // ------------------------------------------------------------ 速度统计窗口
        private void FillSpeedItems(ToolStripItemCollection items)
        {
            int[] opts = { 5, 15, 30, 60, 180, 360, 1440, 2880 };
            foreach (int mins0 in opts)
            {
                int mins = mins0;
                if (mins == 180)
                {
                    items.Add(new ToolStripSeparator());
                }
                ToolStripMenuItem it = new ToolStripMenuItem("近 " + SpeedSpan(mins));
                it.Checked = speedMin == mins;
                it.Click += delegate { SetSpeedWindow(mins); };
                items.Add(it);
            }
            items.Add(new ToolStripSeparator());
            bool custom = Array.IndexOf(opts, speedMin) < 0;
            ToolStripMenuItem ci = new ToolStripMenuItem(custom ? "自定义…（现在是 " + SpeedSpan(speedMin) + "）" : "自定义…");
            ci.Checked = custom;
            ci.Click += delegate
            {
                using (MinutesForm f = new MinutesForm(speedMin))
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        SetSpeedWindow(f.Minutes);
                    }
                }
            };
            items.Add(ci);
        }

        private ToolStripMenuItem BuildSpeedItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("速度统计窗口（现在 " + SpeedSpan(speedMin) + "）");
            FillSpeedItems(root.DropDownItems);
            return root;
        }

        // ------------------------------------------------------------ 自定义时段 / 常用时段
        private string RangeText(int from, int to)
        {
            return Epoch.AddMinutes(from).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " ~ " + Epoch.AddMinutes(to).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private void UseCustomRange(int from, int to)
        {
            customFromMin = from;
            customToMin = to;
            tabMask |= 1 << TabCustom;
            CollapseRows();
            usagePeriod = TabCustom;
            logRefreshUtc = DateTime.MinValue;
            SaveSettings();
            StartLogRefresh();
            Redraw();
        }

        private ToolStripMenuItem BuildCustomRangeItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("自定义时段");
            root.DropDownItems.Add("设置自定义时段…", null, delegate { tabMask |= 1 << TabCustom; CollapseRows(); usagePeriod = TabCustom; OpenCustomRange(); Redraw(); });
            root.DropDownItems.Add(new ToolStripSeparator());
            bool any = false;
            for (int i = 0; i < 5; i++)
            {
                if (crFrom[i] == int.MinValue || crTo[i] == int.MinValue)
                {
                    continue;
                }
                any = true;
                int f = crFrom[i], t = crTo[i];
                root.DropDownItems.Add(RangeText(f, t), null, delegate { UseCustomRange(f, t); });
            }
            if (!any)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("（还没有常用时段）");
                none.Enabled = false;
                root.DropDownItems.Add(none);
            }
            root.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem save = new ToolStripMenuItem("把当前自定义时段存为常用");
            save.Enabled = customFromMin != int.MinValue && customToMin != int.MinValue;
            save.Click += delegate
            {
                for (int i = 4; i > 0; i--)
                {
                    crFrom[i] = crFrom[i - 1];
                    crTo[i] = crTo[i - 1];
                }
                crFrom[0] = customFromMin;
                crTo[0] = customToMin;
                for (int i = 1; i < 5; i++)
                {
                    if (crFrom[i] == crFrom[0] && crTo[i] == crTo[0])
                    {
                        crFrom[i] = int.MinValue;
                        crTo[i] = int.MinValue;
                    }
                }
                SaveSettings();
            };
            root.DropDownItems.Add(save);
            root.DropDownItems.Add("清空常用时段", null, delegate
            {
                for (int i = 0; i < 5; i++)
                {
                    crFrom[i] = int.MinValue;
                    crTo[i] = int.MinValue;
                }
                SaveSettings();
            });
            return root;
        }

        // ------------------------------------------------------------ 窗口大小预设
        private void ApplyWindowPreset(int widthU, int extraRows)
        {
            if (!showUsage)
            {
                SetUsageVisible(true);
            }
            rowsExpanded = false;
            rowScroll = 0;
            autoWinHu = 0;
            winWu = widthU;
            winHu = CompactH() + UsagePanelH + extraRows * 16;
            ApplyScale();
            KeepOnScreen();
            SaveSettings();
        }

        private ToolStripMenuItem BuildSizeItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("窗口大小");
            root.DropDownItems.Add("紧凑（最窄，放不下全部列时自动缩小）", null, delegate { ApplyWindowPreset(520, 0); });
            root.DropDownItems.Add("标准（刚好放下 7 列）", null, delegate { ApplyWindowPreset(600, 3); });
            root.DropDownItems.Add("宽屏（宽松，多显示几行）", null, delegate { ApplyWindowPreset(760, 9); });
            root.DropDownItems.Add("高（标准宽度，多显示 18 行）", null, delegate { ApplyWindowPreset(600, 18); });
            return root;
        }

        // ------------------------------------------------------------ 提醒设置
        private ToolStripMenuItem BuildAlertItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("提醒设置");
            ToolStripMenuItem a = new ToolStripMenuItem("缓存快过期时系统提醒的提前量");
            foreach (int m0 in new int[] { 5, 10, 15, 20, 30 })
            {
                int m = m0;
                ToolStripMenuItem it = new ToolStripMenuItem("剩 " + m + " 分钟时");
                it.Checked = alertMin == m;
                it.Click += delegate { alertMin = m; SaveSettings(); };
                a.DropDownItems.Add(it);
            }
            root.DropDownItems.Add(a);
            ToolStripMenuItem b = new ToolStripMenuItem("额度触顶预警（5 小时 / 每周额度用到多少 % 时通知）");
            foreach (int v0 in new int[] { 0, 70, 80, 90, 95 })
            {
                int v = v0;
                ToolStripMenuItem it = new ToolStripMenuItem(v == 0 ? "关闭" : "用到 " + v + "% 时");
                it.Checked = limitWarn == v;
                it.Click += delegate { limitWarn = v; warnedFive = 0; warnedWeek = 0; SaveSettings(); };
                b.DropDownItems.Add(it);
            }
            root.DropDownItems.Add(b);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add("邮件提醒…（会话列表头的 @ 也是这里）", null, delegate { OpenMailForm(); });
            return root;
        }

        // 每次取到额度读数时看一眼：超过预警线、而且这个窗口还没提醒过，就弹一条通知
        private void CheckLimitWarn(FetchResult r)
        {
            if (limitWarn <= 0 || r == null)
            {
                return;
            }
            DateTime now = DateTime.Now;
            if (r.FiveHour != null && r.FiveHour.ResetsAt.HasValue && r.FiveHour.Percent >= limitWarn && r.FiveHour.ResetsAt.Value.Ticks != warnedFive)
            {
                warnedFive = r.FiveHour.ResetsAt.Value.Ticks;
                double h = (r.FiveHour.ResetsAt.Value - now).TotalHours;
                Balloon("5 小时额度已用 " + r.FiveHour.Percent.ToString("0", CultureInfo.InvariantCulture) + "%", h > 0 ? HoursText(h) + "后重置。" : "马上重置。");
            }
            if (r.SevenDay != null && r.SevenDay.ResetsAt.HasValue && r.SevenDay.Percent >= limitWarn && r.SevenDay.ResetsAt.Value.Ticks != warnedWeek)
            {
                warnedWeek = r.SevenDay.ResetsAt.Value.Ticks;
                double h = (r.SevenDay.ResetsAt.Value - now).TotalHours;
                Balloon("每周额度已用 " + r.SevenDay.Percent.ToString("0", CultureInfo.InvariantCulture) + "%", h > 0 ? HoursText(h) + "后重置。" : "马上重置。");
            }
        }

        // ------------------------------------------------------------ 邮件提醒
        private readonly Dictionary<string, double> mailPrev = new Dictionary<string, double>();
        private readonly HashSet<string> mailSent = new HashSet<string>();
        private DateTime mailFailUtc = DateTime.MinValue;

        private void DrawMailButton(Graphics g, float x, float y, float h, float s)
        {
            int n = MailStore.WatchCount;
            bool on = MailStore.Enabled && MailStore.Configured && n > 0;
            string text = on ? "@" + n : "@";
            float w = (on ? 26f : 17f) * s;
            RectangleF r = new RectangleF(x, y - 1f * s, w, h + 1f * s);
            Color c = on ? Accent : TextDim;
            SkinDraw.RrLine(g, SkinDraw.Mix(Bg, c, on ? 0.9f : 0.5f), r, 4f * s, 1f);
            DrawTextRect(g, text, fSmall, c, r, StringAlignment.Center);
            mailBtnRect = new RectangleF(r.X - 1f * s, r.Y - 2f * s, r.Width + 2f * s, r.Height + 4f * s);
        }

        private List<MailSessionRow> BuildMailRows()
        {
            DateTime now = DateTime.UtcNow;
            List<KeyValuePair<string, UsageTracker.LiveInfo>> live = new List<KeyValuePair<string, UsageTracker.LiveInfo>>(usageSnap.Live);
            live.Sort(delegate(KeyValuePair<string, UsageTracker.LiveInfo> a, KeyValuePair<string, UsageTracker.LiveInfo> b)
            {
                bool wa = MailStore.Find(a.Key) != null, wb = MailStore.Find(b.Key) != null;
                if (wa != wb)
                {
                    return wa ? -1 : 1;
                }
                return b.Value.LastUtc.CompareTo(a.Value.LastUtc);
            });
            List<MailSessionRow> rows = new List<MailSessionRow>();
            HashSet<string> seen = new HashSet<string>();
            foreach (KeyValuePair<string, UsageTracker.LiveInfo> kv in live)
            {
                MailSessionRow r = new MailSessionRow();
                r.Session = kv.Key;
                r.Title = LiveTitle(kv.Key);
                r.TtlMin = kv.Value.TtlMin;
                double rem;
                CacheState st = StateOf(kv.Value, now, out rem);
                r.State = st == CacheState.Running ? "运行中（自动续期）" : st == CacheState.Waiting ? "等你处理" + (rem > 0 ? " · 剩 " + Math.Round(rem).ToString("0", CultureInfo.InvariantCulture) + " 分" : "")
                    : st == CacheState.None ? (kv.Value.ExpiryUtc.HasValue ? "已过期" : "无缓存") : "剩 " + Math.Round(rem).ToString("0", CultureInfo.InvariantCulture) + " 分";
                rows.Add(r);
                seen.Add(kv.Key);
            }
            List<MailWatch> ws;
            lock (MailStore.Watches)
            {
                ws = new List<MailWatch>(MailStore.Watches);
            }
            foreach (MailWatch w in ws)
            {
                if (!seen.Contains(w.Session))
                {
                    MailSessionRow r = new MailSessionRow();
                    r.Session = w.Session;
                    r.Title = w.Title;
                    r.State = "（不在当前列表）";
                    r.Live = false;
                    rows.Add(r);
                }
            }
            return rows;
        }

        private void OpenMailForm()
        {
            using (MailForm f = new MailForm(BuildMailRows()))
            {
                if (f.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                MailStore.Enabled = f.ResultEnabled;
                lock (MailStore.Watches)
                {
                    MailStore.Watches.Clear();
                    MailStore.Watches.AddRange(f.ResultWatches);
                }
                MailStore.Save();
                mailPrev.Clear();       // 重新看一遍：已经低于设定分钟数的不补发
                Redraw();
            }
        }

        // 每个 tick 看一遍：勾选的会话缓存剩余刚好掉到设定的分钟数以内，就发一封邮件（每个缓存周期每个分钟数只发一次）。
        // 要「亲眼看到掉下去」才发：刚启动 / 刚勾选时已经低于的不补发；缓存有效期不比这个分钟数长的永远不会触发（否则每回复一次就发一封）
        private void CheckMailAlerts(DateTime now)
        {
            if (paused || !MailStore.Enabled || !MailStore.Configured || MailStore.WatchCount == 0)
            {
                return;
            }
            List<MailWatch> ws;
            lock (MailStore.Watches)
            {
                ws = new List<MailWatch>(MailStore.Watches);
            }
            foreach (MailWatch w in ws)
            {
                UsageTracker.LiveInfo li;
                if (!usageSnap.Live.TryGetValue(w.Session, out li))
                {
                    mailPrev.Remove(w.Session);
                    continue;
                }
                double rem;
                CacheState st = StateOf(li, now, out rem);
                bool counting = st == CacheState.Urgent || st == CacheState.Soon || st == CacheState.Fresh || (st == CacheState.Waiting && rem > 0);
                if (!counting || !li.ExpiryUtc.HasValue)
                {
                    mailPrev[w.Session] = double.MaxValue;      // 运行中 / 已过期：下次重新计时
                    continue;
                }
                double prev;
                bool had = mailPrev.TryGetValue(w.Session, out prev);
                mailPrev[w.Session] = rem;
                int ttl = li.TtlMin > 0 ? li.TtlMin : 5;
                int firedMin = 0;
                foreach (int m in w.Mins)
                {
                    string key = w.Session + "|" + li.ExpiryUtc.Value.Ticks + "|" + m;
                    if (mailSent.Contains(key) || rem > m)
                    {
                        continue;
                    }
                    mailSent.Add(key);
                    if (had && prev > m && m < ttl && (firedMin == 0 || m < firedMin))
                    {
                        firedMin = m;
                    }
                }
                if (firedMin > 0)
                {
                    SendCacheMail(w, li, rem, firedMin);
                }
            }
            if (mailSent.Count > 3000)
            {
                mailSent.Clear();
            }
        }

        private void SendCacheMail(MailWatch w, UsageTracker.LiveInfo li, double rem, int threshold)
        {
            LoadQuota();
            string title = LiveTitle(w.Session);
            if (title.Length == 0)
            {
                title = w.Title;
            }
            int left = Math.Max(1, (int)Math.Round(rem));
            title = title.Replace('\r', ' ').Replace('\n', ' ');
            string shortTitle = title.Length > 22 ? title.Substring(0, 22) + "…" : title;
            string subject = "【Claude】「" + shortTitle + "」缓存还剩 " + left + " 分钟";
            Account acct = AccountStore.Current;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("会话：" + title);
            sb.AppendLine("缓存还剩：约 " + left + " 分钟（" + li.ExpiryUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " 过期）");
            sb.AppendLine("当前状态：" + (li.Waiting ? "在等你处理（权限确认 / 提问等）" : "空闲"));
            sb.AppendLine("上下文：" + UsageTracker.FormatTokens(li.Ctx) + " token" + (li.TtlMin > 0 ? "（缓存有效期 " + li.TtlMin + " 分钟）" : ""));
            string reload = ReloadCostText(li);
            if (reload.Length > 0)
            {
                sb.AppendLine(reload.TrimStart(',', '，', ' '));
            }
            if (acct != null)
            {
                sb.AppendLine("账号：" + acct.Id + " · " + PlanInfo.NameOf(acct.Plan));
            }
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("想续上这个会话的缓存，在它过期前给它发一条消息就行。");
            sb.AppendLine("这封邮件是你在悬浮窗里勾选了这个会话（剩 " + MailStore.MinsText(w.Mins) + " 分钟时提醒）后自动发的。");
            MailStore.Attempts++;
            MailSender.SendAsync(MailStore.Snapshot(), subject, sb.ToString(), delegate(string err)
            {
                MailStore.LastStatus = DateTime.Now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + (err == null ? " 发送成功" : " 失败：" + err);
                if (err != null && (DateTime.UtcNow - mailFailUtc).TotalMinutes > 30)
                {
                    mailFailUtc = DateTime.UtcNow;
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate { Balloon("邮件提醒发送失败", err.Length > 120 ? err.Substring(0, 120) : err); }));
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }

        // ------------------------------------------------------------ 导出
        // 以 = + - @（或制表符 / 回车）开头的文字，Excel / WPS 打开 CSV 或粘贴时会当公式执行（会话标题来自对话内容，可能是别人写的）：前面补一个单引号。纯数字（含负数）不动
        private static string NoFormula(string s)
        {
            s = s ?? "";
            if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' || s[0] == '\t' || s[0] == '\r'))
            {
                double d;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    return "'" + s;
                }
            }
            return s;
        }

        private static string CsvQ(string s)
        {
            s = NoFormula(s);
            return s.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        private static string N4(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------ 「占用」列
        // 速度 = 近 N 分钟用掉的额度 ÷ N 分钟（每小时多少）；占用 = 这 N 分钟里一共用掉了多少（不除窗口）。两列用同一个统计窗口，所以占用 ÷ 窗口小时数 = 速度
        private double occ5, occW;                                  // 所有会话（含不在列表里的）窗口里的占用合计
        private RectangleF occHeadRect = RectangleF.Empty;          // 「占用」表头（点文字 = 选统计窗口）
        private static readonly int[] ColBitOf = { -1, 0, 1, 6, 2, 3, 4, 5 };      // 会话视图第 i 列对应 smask 的哪一位（屏幕顺序：缓存剩余 速度 占用 输入 输出 缓存写 缓存读）
        private bool sawColv;                                       // 设置文件是新版列顺序写的

        private string OccText(double p5, double pw)
        {
            if (p5 <= 0 && pw <= 0)
            {
                return "—";
            }
            return (quota != null && quota.PlanDerived ? "≈" : "") + RateText(p5, false) + " / " + RateText(pw, true);
        }

        private void DrawOccCell(Graphics g, UsageTracker.SessionTotal t, float x, float y, float w, float h, Color normal)
        {
            SpeedInfo si = SpeedFor(t.Session);
            bool cur = si != null && si.Known && (si.P5 > 0 || si.Pw > 0);
            bool gray = !cur && si != null && si.HasGray && (si.GP5 > 0 || si.GPw > 0);
            DrawTextFit(g, cur ? OccText(si.P5, si.Pw) : gray ? OccText(si.GP5, si.GPw) : "—", fSmall, cur ? normal : TextDim, new RectangleF(x, y, w, h), StringAlignment.Far);
        }

        // 旧版设置文件（没有 colv=）里的列序号是「…缓存读 成本」，现在「占用」挪到了速度后面：排序列和列宽跟着换位置
        private void MigrateColumns()
        {
            if (sawColv)
            {
                return;
            }
            if (sortCol[0] == 7)
            {
                sortCol[0] = 3;
            }
            else if (sortCol[0] >= 3 && sortCol[0] <= 6)
            {
                sortCol[0]++;
            }
            double c7 = colFr[7];
            colFr[7] = colFr[6];
            colFr[6] = colFr[5];
            colFr[5] = colFr[4];
            colFr[4] = colFr[3];
            colFr[3] = c7;
            sawColv = true;
        }

        // 这个会话在当前时间标签里按官方价折的美元（API 等价），悬停说明里用；没有价格返回 null
        private double? PeriodUsd(string session)
        {
            double sum = 0;
            bool any = false;
            foreach (UsageTracker.ModelTotal mt in PeriodFor(usagePeriod).ModelRows)
            {
                if (mt.Session != session)
                {
                    continue;
                }
                double? u = UsdOf(mt.Model, mt.Tok);
                if (u.HasValue)
                {
                    sum += u.Value;
                    any = true;
                }
            }
            return any ? (double?)sum : null;
        }

        // ------------------------------------------------------------ 行距（拖「合计」上面那条分隔线）
        private int rowHu = 16;                                     // 表格每一行的高度（基础单位）
        private const int RowHuMin = 14, RowHuMax = 40, RowHuDefault = 16;
        private RectangleF gripRect = RectangleF.Empty;             // 分隔线的拖动区
        private int gripRows;                                       // 分隔线上面有几行（拖动时用它把鼠标位移换算成行高）
        private bool rowDrag;
        private int rowDragY0, rowDragH0, rowDragN;

        // 表格底下（合计行上面）的那条分隔线：中间画三个小点，提示可以上下拖
        private void DrawRowGrip(Graphics g, float pad, float inner, float s, float lineY, int rows, bool drawLine)
        {
            gripRect = new RectangleF(pad, lineY - 4f * s, inner, 9f * s);
            gripRows = Math.Max(1, rows);
            if (drawLine)
            {
                using (Pen p = new Pen(Track))
                {
                    g.DrawLine(p, pad, lineY, pad + inner, lineY);
                }
            }
            float cx = pad + inner / 2f, d = 2.2f * s, gap = 5f * s;
            using (SolidBrush b = new SolidBrush(SkinDraw.Mix(Bg, TextDim, 0.55f)))
            {
                for (int i = -1; i <= 1; i++)
                {
                    g.FillEllipse(b, cx + i * gap - d / 2f, lineY - d / 2f, d, d);
                }
            }
        }

        private bool GripAt(Point p)
        {
            return showUsage && usageMode <= 4 && gripRect.Width > 1f && gripRect.Contains(p);
        }

        // 鼠标拖到纵坐标 y 时，行高应该是多少：往下拖 n 个基础单位，分隔线上面的每一行各长 n / 行数，分隔线大致跟着鼠标走
        private int RowDragTarget(int y)
        {
            int d = (int)Math.Round((y - rowDragY0) / (double)scale / rowDragN);
            return Math.Max(RowHuMin, Math.Min(RowHuMax, rowDragH0 + d));
        }

        private void DoRowDrag(int y)
        {
            int nh = RowDragTarget(y);
            if (nh != rowHu)
            {
                rowHu = nh;
                Redraw();
            }
        }

        // 竖分隔线原来只画到面板底；行距拉大后表格会更长，把它们接着画下去（最多到窗口底）
        private void ExtendDividers(Graphics g, int h, float bottom)
        {
            bottom = Math.Min(bottom, h - 4f * scale);
            if (usageMode >= 3 || bottom <= colBottom)
            {
                return;
            }
            using (Pen p = new Pen(Track))
            {
                for (int i = FirstEdge(); i < 7; i++)
                {
                    if (colOn[i] && colNext[i] >= 0)
                    {
                        g.DrawLine(p, colEdgeX[i], colBottom, colEdgeX[i], bottom);
                    }
                }
            }
            colBottom = bottom;
        }

        private ToolStripMenuItem BuildRowHeightItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("行距（也可以直接拖「合计」上面那条线）");
            string[] names = { "紧凑", "标准", "舒适", "宽松", "很宽松" };
            int[] vals = { 14, 16, 20, 26, 34 };
            for (int i = 0; i < names.Length; i++)
            {
                int v = vals[i];
                ToolStripMenuItem it = new ToolStripMenuItem(names[i] + "（" + v + "）");
                it.Checked = rowHu == v;
                it.Click += delegate
                {
                    rowHu = v;
                    Redraw();
                    SaveSettings();
                };
                root.DropDownItems.Add(it);
            }
            ToolStripMenuItem note = new ToolStripMenuItem("拖动时双击那条线 = 恢复标准");
            note.Enabled = false;
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(note);
            return root;
        }

        // kind 0 = 会话表，1 = 模型 × 档位明细
        private List<string[]> ExportTable(int kind)
        {
            UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
            List<string[]> t = new List<string[]>();
            LoadQuota();
            DateTime now = DateTime.UtcNow;
            if (kind == 0)
            {
                double t5, tw;
                bool known;
                speedBy = ComputeSpeeds(now, out t5, out tw, out known);
                PrepareCost(cur);
                t.Add(new string[] { "会话", "会话ID", "缓存状态", "缓存剩余(分钟)", "5h速度(%/小时)", "周速度(%/小时)", "占用5h(%)", "占用周(%)", "输入", "输出", "缓存写", "缓存读", "成本(美元)", "成本口径" });
                foreach (UsageTracker.SessionTotal st in cur.Rows)
                {
                    UsageTracker.LiveInfo li;
                    usageSnap.Live.TryGetValue(st.Session, out li);
                    double rem;
                    CacheState cs = StateOf(li, st.Active, st.CacheExpiryUtc, now, out rem);
                    string state = cs == CacheState.Running ? "运行中" : cs == CacheState.Waiting ? "等你处理" : cs == CacheState.None ? (st.CacheExpiryUtc.HasValue || (li != null && li.ExpiryUtc.HasValue) ? "已过期" : "无缓存") : "有缓存";
                    SpeedInfo si = SpeedFor(st.Session);
                    bool cs5 = si != null && si.Known && (si.R5 > 0 || si.Rw > 0);
                    double? cost = CostOf(st.Session);
                    t.Add(new string[]
                    {
                        st.Title != null ? st.Title : st.Name + " · " + st.ShortId, st.Session, state, rem > 0 ? N4(rem) : "",
                        cs5 ? N4(si.R5) : "", cs5 ? N4(si.Rw) : "", cs5 ? N4(si.P5) : "", cs5 ? N4(si.Pw) : "", st.Input.ToString(CultureInfo.InvariantCulture), st.Output.ToString(CultureInfo.InvariantCulture),
                        st.CacheWrite.ToString(CultureInfo.InvariantCulture), st.CacheRead.ToString(CultureInfo.InvariantCulture), cost.HasValue ? N4(cost.Value) : "",
                        costScope == 0 && !costApprox ? "会话累计" : "当前时间标签"
                    });
                }
            }
            else
            {
                t.Add(new string[] { "会话", "会话ID", "模型", "档位", "消息数", "思考token", "输入", "输出", "缓存写", "缓存读", "5h额度%(估算)", "周额度%(估算)", "API等价(美元)" });
                foreach (UsageTracker.ModelTotal mt in cur.ModelRows)
                {
                    double p5, pw;
                    bool ap;
                    bool k5 = TierPct(mt.Model, mt.Tier, mt.Tok, false, out p5, out ap);
                    bool kw = TierPct(mt.Model, mt.Tier, mt.Tok, true, out pw, out ap);
                    double? usd = UsdOf(mt.Model, mt.Tok);
                    string title = LiveTitle(mt.Session);
                    t.Add(new string[]
                    {
                        title, mt.Session, mt.Model, mt.Tier, mt.Msgs.ToString(CultureInfo.InvariantCulture), mt.Think.ToString(CultureInfo.InvariantCulture),
                        (mt.Tok[0] + mt.Tok[5]).ToString(CultureInfo.InvariantCulture), (mt.Tok[1] + mt.Tok[6]).ToString(CultureInfo.InvariantCulture),
                        (mt.Tok[2] + mt.Tok[3] + mt.Tok[7] + mt.Tok[8]).ToString(CultureInfo.InvariantCulture), (mt.Tok[4] + mt.Tok[9]).ToString(CultureInfo.InvariantCulture),
                        k5 ? N4(p5) : "", kw ? N4(pw) : "", usd.HasValue ? N4(usd.Value) : ""
                    });
                }
            }
            return t;
        }

        private void ExportData(int kind, bool toClipboard)
        {
            List<string[]> table = ExportTable(kind);
            if (table.Count <= 1)
            {
                Balloon("导出", "当前时间标签里没有数据。");
                return;
            }
            string label = TabLabels[usagePeriod];
            if (toClipboard)
            {
                StringBuilder sb = new StringBuilder();
                foreach (string[] row in table)
                {
                    for (int i = 0; i < row.Length; i++)
                    {
                        sb.Append(i > 0 ? "\t" : "").Append(NoFormula(row[i]).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
                    }
                    sb.Append("\r\n");
                }
                try
                {
                    Clipboard.SetText(sb.ToString());
                    Balloon("已复制到剪贴板", label + " · " + (table.Count - 1) + " 行，可以直接粘贴到 Excel。");
                }
                catch (Exception ex)
                {
                    Balloon("复制失败", ex.Message);
                }
                return;
            }
            string dir = Path.Combine(LogDir(), "导出");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception)
            {
                dir = LogDir();         // 建不了「导出」文件夹（没权限 / 只读）：对话框改从日志文件夹开始，别让菜单点击抛出未处理的异常
            }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.InitialDirectory = dir;
                d.Filter = "CSV 文件（Excel 可以直接打开）|*.csv";
                d.FileName = (kind == 0 ? "会话表_" : "模型档位明细_") + label + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                StringBuilder sb = new StringBuilder();
                foreach (string[] row in table)
                {
                    List<string> cells = new List<string>();
                    foreach (string c in row)
                    {
                        cells.Add(CsvQ(c));
                    }
                    sb.Append(string.Join(",", cells.ToArray())).Append("\r\n");
                }
                try
                {
                    File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true));
                    Balloon("已导出", d.FileName);
                }
                catch (Exception ex)
                {
                    Balloon("导出失败", ex.Message);
                }
            }
        }

        private ToolStripMenuItem BuildExportItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("导出当前时间标签的数据");
            root.DropDownItems.Add("会话表 → 复制到剪贴板（粘贴到 Excel）", null, delegate { ExportData(0, true); });
            root.DropDownItems.Add("会话表 → 保存为 CSV…", null, delegate { ExportData(0, false); });
            root.DropDownItems.Add("模型 × 档位明细 → 复制到剪贴板", null, delegate { ExportData(1, true); });
            root.DropDownItems.Add("模型 × 档位明细 → 保存为 CSV…", null, delegate { ExportData(1, false); });
            return root;
        }

        private ToolStripMenuItem BuildPauseItem()
        {
            ToolStripMenuItem it = new ToolStripMenuItem("暂停后台读取（打游戏用）");
            it.Checked = paused;
            it.Click += delegate { TogglePause(); };
            return it;
        }
    }
}
