// Second batch of Widget features (v33): pause background reading, time tab presets, speed window menu, saved custom ranges, window size presets,
// reminder settings (cache lead time / quota cap warning / email reminders), export, and the "@" email reminder button above the session column.
// Same class as Widget in Main.cs (partial).

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
        // ------------------------------------------------------------ state
        private bool paused;                                        // Pause background reading (for gaming; not saved, restored after a restart)
        private int alertMin = 10;                                  // Show a system notification when this many minutes of cache are left
        private int limitWarn = 90;                                 // Notify when the 5-hour / weekly quota reaches this %; 0 = never
        private int tabPreset;                                      // "My" time tab preset (bit mask, 0 = never saved)
        private int[] crFrom = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };    // Saved custom ranges (minutes since Epoch)
        private int[] crTo = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private long warnedFive, warnedWeek;                        // Which window (reset time) has already been notified
        private RectangleF mailBtnRect = RectangleF.Empty;          // The "@" button in the session list header
        private RectangleF pauseRect = RectangleF.Empty;            // The "paused" hint (click to resume)

        // ------------------------------------------------------------ pause background reading
        private void TogglePause()
        {
            paused = !paused;
            if (logger != null)
            {
                logger.Paused = paused;
            }
            if (!paused)
            {
                // Resume: do one read right away to catch up
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
            float x1 = pad + 130f * s;
            float x2 = modeSwitchX - 8f * s;
            RectangleF r = new RectangleF(x1, top + 6f * s, Math.Max(10f * s, x2 - x1), 16f * s);
            string full = "⏸ Reading paused (click to resume)";
            string text = TextW(g, full, fSmall) * 0.85f <= r.Width ? full : "⏸ Paused";
            DrawTextFit(g, text, fSmall, StateColor(CacheState.Soon), r, StringAlignment.Far, 0.85f);
            float tw = Math.Min(r.Width, TextW(g, text, fSmall));
            pauseRect = new RectangleF(r.Right - tw, r.Y, tw, r.Height);
        }

        // ------------------------------------------------------------ time tab presets
        private static readonly string[] PresetNames = { "Daily (15 min / This 5h / Today)", "Review (Today / Yesterday / This week)", "Basic (This 5h / Today)", "Default", "All 11 tabs" };
        private static readonly int[] PresetMasks = { 2 + 32 + 128, 128 + 256 + 512, 32 + 128, 1258, 2047 };

        private ToolStripMenuItem BuildTabPresetItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Time tab presets");
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
                ToolStripMenuItem mine = new ToolStripMenuItem("My preset");
                mine.Checked = tabMask == tabPreset;
                mine.Click += delegate { ApplyTabMask(tabPreset); };
                root.DropDownItems.Add(mine);
            }
            root.DropDownItems.Add("Save the tabs now shown as 'My preset'", null, delegate { tabPreset = tabMask; SaveSettings(); });
            return root;
        }

        // ------------------------------------------------------------ speed window
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
                ToolStripMenuItem it = new ToolStripMenuItem("Last " + SpeedSpan(mins));
                it.Checked = speedMin == mins;
                it.Click += delegate { SetSpeedWindow(mins); };
                items.Add(it);
            }
            items.Add(new ToolStripSeparator());
            bool custom = Array.IndexOf(opts, speedMin) < 0;
            ToolStripMenuItem ci = new ToolStripMenuItem(custom ? "Custom... (now " + SpeedSpan(speedMin) + ")" : "Custom...");
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
            ToolStripMenuItem root = new ToolStripMenuItem("Speed window (now " + SpeedSpan(speedMin) + ")");
            FillSpeedItems(root.DropDownItems);
            return root;
        }

        // ------------------------------------------------------------ custom range / saved ranges
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
            ToolStripMenuItem root = new ToolStripMenuItem("Custom range");
            root.DropDownItems.Add("Set custom range...", null, delegate { tabMask |= 1 << TabCustom; CollapseRows(); usagePeriod = TabCustom; OpenCustomRange(); Redraw(); });
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
                ToolStripMenuItem none = new ToolStripMenuItem("(no saved ranges yet)");
                none.Enabled = false;
                root.DropDownItems.Add(none);
            }
            root.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem save = new ToolStripMenuItem("Save current custom range as favorite");
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
            root.DropDownItems.Add("Clear saved ranges", null, delegate
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

        // ------------------------------------------------------------ window size presets
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
            ToolStripMenuItem root = new ToolStripMenuItem("Window size");
            root.DropDownItems.Add("Compact (narrowest; shrinks automatically when not all columns fit)", null, delegate { ApplyWindowPreset(520, 0); });
            root.DropDownItems.Add("Standard (fits exactly 7 columns)", null, delegate { ApplyWindowPreset(600, 3); });
            root.DropDownItems.Add("Wide (roomy, shows a few more rows)", null, delegate { ApplyWindowPreset(760, 9); });
            root.DropDownItems.Add("Tall (standard width, shows 18 more rows)", null, delegate { ApplyWindowPreset(600, 18); });
            return root;
        }

        // ------------------------------------------------------------ reminder settings
        private ToolStripMenuItem BuildAlertItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Reminder settings");
            ToolStripMenuItem a = new ToolStripMenuItem("How early to notify before the cache expires");
            foreach (int m0 in new int[] { 5, 10, 15, 20, 30 })
            {
                int m = m0;
                ToolStripMenuItem it = new ToolStripMenuItem("in " + m + " min");
                it.Checked = alertMin == m;
                it.Click += delegate { alertMin = m; SaveSettings(); };
                a.DropDownItems.Add(it);
            }
            root.DropDownItems.Add(a);
            ToolStripMenuItem b = new ToolStripMenuItem("Quota cap warning (notify when the 5-hour / weekly quota reaches this %)");
            foreach (int v0 in new int[] { 0, 70, 80, 90, 95 })
            {
                int v = v0;
                ToolStripMenuItem it = new ToolStripMenuItem(v == 0 ? "Close" : "At " + v + "%");
                it.Checked = limitWarn == v;
                it.Click += delegate { limitWarn = v; warnedFive = 0; warnedWeek = 0; SaveSettings(); };
                b.DropDownItems.Add(it);
            }
            root.DropDownItems.Add(b);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add("Email reminders... (the @ in the session list header opens the same dialog)", null, delegate { OpenMailForm(); });
            return root;
        }

        // Checked each time a quota reading arrives: if it is above the warning line and this window has not been notified yet, show a notification
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
                Balloon("5-hour quota used: " + r.FiveHour.Percent.ToString("0", CultureInfo.InvariantCulture) + "%", h > 0 ? HoursText(h) + " until reset." : "Resets any moment now.");
            }
            if (r.SevenDay != null && r.SevenDay.ResetsAt.HasValue && r.SevenDay.Percent >= limitWarn && r.SevenDay.ResetsAt.Value.Ticks != warnedWeek)
            {
                warnedWeek = r.SevenDay.ResetsAt.Value.Ticks;
                double h = (r.SevenDay.ResetsAt.Value - now).TotalHours;
                Balloon("Weekly quota used: " + r.SevenDay.Percent.ToString("0", CultureInfo.InvariantCulture) + "%", h > 0 ? HoursText(h) + " until reset." : "Resets any moment now.");
            }
        }

        // ------------------------------------------------------------ email reminders
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
                r.State = st == CacheState.Running ? "Running (auto-renew)" : st == CacheState.Waiting ? "Waiting" + (rem > 0 ? " · in " + Math.Round(rem).ToString("0", CultureInfo.InvariantCulture) + " min" : "")
                    : st == CacheState.None ? (kv.Value.ExpiryUtc.HasValue ? "Expired" : "No cache") : "in " + Math.Round(rem).ToString("0", CultureInfo.InvariantCulture) + " min";
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
                    r.State = "(not in the current list)";
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
                mailPrev.Clear();       // Look again: sessions that are already below the set minutes are not sent late
                Redraw();
            }
        }

        // Checked on every tick: when the cache time left of a ticked session has just dropped to within the set minutes, send one email (once per minute value per cache cycle).
        // It must be seen "dropping": a session already below the threshold at startup / right after ticking is not sent late; a cache lifetime no longer than the minute value never triggers (otherwise every reply would send an email)
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
                    mailPrev[w.Session] = double.MaxValue;      // Running / expired: start timing again next time
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
            string subject = "[Claude] \"" + shortTitle + "\" cache expires in " + left + " min";
            Account acct = AccountStore.Current;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Session: " + title);
            sb.AppendLine("Cache left: about " + left + " min (expires at " + li.ExpiryUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + ")");
            sb.AppendLine("Status: " + (li.Waiting ? "Waiting for you (permission prompt / question, etc.)" : "Idle"));
            sb.AppendLine("Context: " + UsageTracker.FormatTokens(li.Ctx) + " tokens" + (li.TtlMin > 0 ? " (cache lifetime " + li.TtlMin + " min)" : ""));
            string reload = ReloadCostText(li);
            if (reload.Length > 0)
            {
                reload = reload.TrimStart(',', ' ');
                sb.AppendLine(char.ToUpperInvariant(reload[0]) + reload.Substring(1));
            }
            if (acct != null)
            {
                sb.AppendLine("Account: " + acct.Id + " · " + PlanInfo.NameOf(acct.Plan));
            }
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("To keep this session's cache alive, send it a message before it expires.");
            sb.AppendLine("This email was sent automatically because you ticked this session in the widget (reminder at " + MailStore.MinsText(w.Mins) + " min left).");
            MailStore.Attempts++;
            MailSender.SendAsync(MailStore.Snapshot(), subject, sb.ToString(), delegate(string err)
            {
                MailStore.LastStatus = DateTime.Now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + (err == null ? " sent OK" : " failed: " + err);
                if (err != null && (DateTime.UtcNow - mailFailUtc).TotalMinutes > 30)
                {
                    mailFailUtc = DateTime.UtcNow;
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate { Balloon("Email reminder failed to send", err.Length > 120 ? err.Substring(0, 120) : err); }));
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }

        // ------------------------------------------------------------ export
        // Text starting with = + - @ (or tab / carriage return) is executed as a formula when Excel / WPS opens the CSV or when it is pasted (session titles come from conversation content and may have been written by someone else): put a single quote in front. Plain numbers (including negative ones) are left alone
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

        // ------------------------------------------------------------ "Share" column
        // Speed = quota used in the last N minutes / N minutes (per hour); Share = total used in those N minutes (not divided by the window). Both columns use the same statistics window, so Share / window hours = Speed
        private double occ5, occW;                                  // Total share over the window for all sessions (including those not in the list)
        private RectangleF occHeadRect = RectangleF.Empty;          // The "Share" header (click the text = pick the statistics window)
        private static readonly int[] ColBitOf = { -1, 0, 1, 6, 2, 3, 4, 5 };      // Which smask bit the i-th column of the Session view stands for (screen order: Expires, Speed, Share, Input, Output, Cache W, Cache R)
        private bool sawColv;                                       // The settings file was written in the new column order

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

        // Old settings files (without colv=) number the columns "... Cache R, Cost"; "Share" has since moved to right after Speed: the sort column and column widths move with it
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

        // This session's cost in USD at official prices for the current time tab (API equivalent), used in the hover tip; returns null if there is no price
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

        // ------------------------------------------------------------ row spacing (drag the divider above "Total")
        private int rowHu = 16;                                     // Height of each table row (base units)
        private const int RowHuMin = 14, RowHuMax = 40, RowHuDefault = 16;
        private RectangleF gripRect = RectangleF.Empty;             // Drag zone of the divider
        private int gripRows;                                       // How many rows are above the divider (used to convert mouse movement into row height while dragging)
        private bool rowDrag;
        private int rowDragY0, rowDragH0, rowDragN;

        // The divider under the table (above the Total row): three small dots in the middle show that it can be dragged up and down
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

        // Row height for a mouse at vertical position y: dragging down by n base units makes each row above the divider n / rows taller, so the divider roughly follows the mouse
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

        // The vertical dividers used to be drawn only down to the panel bottom; with larger row spacing the table gets longer, so continue them downward (at most to the window bottom)
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
            ToolStripMenuItem root = new ToolStripMenuItem("Row spacing (you can also drag the line above 'Total')");
            string[] names = { "Compact", "Standard", "Comfortable", "Roomy", "Very roomy" };
            int[] vals = { 14, 16, 20, 26, 34 };
            for (int i = 0; i < names.Length; i++)
            {
                int v = vals[i];
                ToolStripMenuItem it = new ToolStripMenuItem(names[i] + " (" + v + ")");
                it.Checked = rowHu == v;
                it.Click += delegate
                {
                    rowHu = v;
                    Redraw();
                    SaveSettings();
                };
                root.DropDownItems.Add(it);
            }
            ToolStripMenuItem note = new ToolStripMenuItem("While dragging, double-click the line = reset to Standard");
            note.Enabled = false;
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(note);
            return root;
        }

        // kind 0 = session table, 1 = model x tier details
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
                t.Add(new string[] { "Session", "Session ID", "Cache status", "Cache left (min)", "5h speed (%/h)", "Weekly speed (%/h)", "Share 5h (%)", "Share weekly (%)", "Input", "Output", "Cache W", "Cache R", "Cost (USD)", "Cost basis" });
                foreach (UsageTracker.SessionTotal st in cur.Rows)
                {
                    UsageTracker.LiveInfo li;
                    usageSnap.Live.TryGetValue(st.Session, out li);
                    double rem;
                    CacheState cs = StateOf(li, st.Active, st.CacheExpiryUtc, now, out rem);
                    string state = cs == CacheState.Running ? "Running" : cs == CacheState.Waiting ? "Waiting" : cs == CacheState.None ? (st.CacheExpiryUtc.HasValue || (li != null && li.ExpiryUtc.HasValue) ? "Expired" : "No cache") : "Cached";
                    SpeedInfo si = SpeedFor(st.Session);
                    bool cs5 = si != null && si.Known && (si.R5 > 0 || si.Rw > 0);
                    double? cost = CostOf(st.Session);
                    t.Add(new string[]
                    {
                        st.Title != null ? st.Title : st.Name + " · " + st.ShortId, st.Session, state, rem > 0 ? N4(rem) : "",
                        cs5 ? N4(si.R5) : "", cs5 ? N4(si.Rw) : "", cs5 ? N4(si.P5) : "", cs5 ? N4(si.Pw) : "", st.Input.ToString(CultureInfo.InvariantCulture), st.Output.ToString(CultureInfo.InvariantCulture),
                        st.CacheWrite.ToString(CultureInfo.InvariantCulture), st.CacheRead.ToString(CultureInfo.InvariantCulture), cost.HasValue ? N4(cost.Value) : "",
                        costScope == 0 && !costApprox ? "Session total" : "Current time tab"
                    });
                }
            }
            else
            {
                t.Add(new string[] { "Session", "Session ID", "Model", "Tier", "Messages", "Thinking tokens", "Input", "Output", "Cache W", "Cache R", "5h quota % (est.)", "Weekly quota % (est.)", "API equiv. (USD)" });
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
                Balloon("Export", "There is no data in the current time tab.");
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
                    Balloon("Copied to clipboard", label + " · " + (table.Count - 1) + " rows, ready to paste into Excel.");
                }
                catch (Exception ex)
                {
                    Balloon("Copy failed", ex.Message);
                }
                return;
            }
            string dir = Path.Combine(LogDir(), "export");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception)
            {
                dir = LogDir();         // The export folder cannot be created (no permission / read-only): the dialog starts in the log folder instead, so a menu click never throws an unhandled exception
            }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.InitialDirectory = dir;
                d.Filter = "CSV files (Excel can open them directly)|*.csv";
                d.FileName = (kind == 0 ? "session_table_" : "model_tier_details_") + label + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".csv";
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
                    Balloon("Exported", d.FileName);
                }
                catch (Exception ex)
                {
                    Balloon("Export failed", ex.Message);
                }
            }
        }

        private ToolStripMenuItem BuildExportItem()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Export data of the current time tab");
            root.DropDownItems.Add("Copy session table to clipboard (paste into Excel)", null, delegate { ExportData(0, true); });
            root.DropDownItems.Add("Save session table as CSV...", null, delegate { ExportData(0, false); });
            root.DropDownItems.Add("Copy model × tier details to clipboard", null, delegate { ExportData(1, true); });
            root.DropDownItems.Add("Save model × tier details as CSV...", null, delegate { ExportData(1, false); });
            return root;
        }

        private ToolStripMenuItem BuildPauseItem()
        {
            ToolStripMenuItem it = new ToolStripMenuItem("Pause background reading (for gaming)");
            it.Checked = paused;
            it.Click += delegate { TogglePause(); };
            return it;
        }
    }
}
