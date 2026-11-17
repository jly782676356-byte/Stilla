// Hover tips (v34): when the mouse rests on something in the widget for a moment, a small note pops up next to it: what it is, how it is calculated, what a click does.
//
// Structure (to make translating / extending easier now that the project is open source):
//   TipWindow  a small window that never takes focus and never blocks the mouse; it only draws the text
//   TipText    all the 'fixed' tip texts live here; to change a text, only edit this class
//   Widget     (partial) the hover engine: every frame it re-registers which area belongs to which tip; once the mouse rests it looks the area up in the table and shows the tip
//              BuildTips() is the entry point for the registration; tips that can only be written from current data (cache state, speed, row details...) are the XxxTip() methods below,
//              which are lazy: they are only computed at the moment a tip is really shown, so they cost nothing the rest of the time
// Tip text format: the first line is the title (bold), the rest is the body, \n is a line break; text that is too long wraps automatically.
// The ⋮ menu / right-click menu has a 'Hover tips' switch (on by default).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Stilla
{
    // ================================================================ Small window
    internal sealed class TipWindow : Form
    {
        private string title = "", body = "";
        private Font fBody, fTitle;
        private string fontKey = "";
        private Color cBg, cBorder, cText;
        private int padX = 9, padY = 7, titleH, bodyH, innerW;
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public TipWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(10, 10);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008;     // NOACTIVATE | TOOLWINDOW | TOPMOST
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)          // WM_NCHITTEST: completely transparent to the mouse, so it never blocks the widget below
            {
                m.Result = new IntPtr(-1);
                return;
            }
            base.WndProc(ref m);
        }

        // Lay the text out (compute the width and height); nothing is shown
        public Size Prepare(string text, string family, float px, Color bg, Color border, Color fg, int maxW)
        {
            string key = family + "|" + px.ToString("0.##", CultureInfo.InvariantCulture);
            if (key != fontKey || fBody == null)
            {
                if (fBody != null) fBody.Dispose();
                if (fTitle != null) fTitle.Dispose();
                fBody = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
                fTitle = new Font(family, px, FontStyle.Bold, GraphicsUnit.Pixel);
                fontKey = key;
            }
            cBg = Color.FromArgb(255, bg);
            cBorder = Color.FromArgb(255, border);
            cText = Color.FromArgb(255, fg);
            int nl = text.IndexOf('\n');
            title = nl > 0 ? text.Substring(0, nl) : "";
            body = nl > 0 ? text.Substring(nl + 1) : text;
            padX = (int)Math.Round(px * 0.75);
            padY = (int)Math.Round(px * 0.55);
            Size box = new Size(maxW, int.MaxValue);
            Size ts = title.Length > 0 ? TextRenderer.MeasureText(title, fTitle, box, Flags) : Size.Empty;
            Size bs = body.Length > 0 ? TextRenderer.MeasureText(body, fBody, box, Flags) : Size.Empty;
            innerW = Math.Min(maxW, Math.Max(ts.Width, bs.Width) + 2);
            titleH = ts.Height;
            bodyH = bs.Height;
            int gap = title.Length > 0 && body.Length > 0 ? (int)Math.Round(px * 0.35) : 0;
            titleH += gap;
            return new Size(innerW + 2 * padX + 2, titleH + bodyH + 2 * padY + 2);
        }

        // Draw onto a bitmap (used by the window's own painting and by the off-screen test)
        public void PaintTip(Graphics g, Size size)
        {
            using (SolidBrush b = new SolidBrush(cBg))
            using (Pen p = new Pen(cBorder))
            {
                g.FillRectangle(b, 0, 0, size.Width, size.Height);
                g.DrawRectangle(p, 0, 0, size.Width - 1, size.Height - 1);
            }
            int x = 1 + padX, y = 1 + padY;
            if (title.Length > 0)
            {
                TextRenderer.DrawText(g, title, fTitle, new Rectangle(x, y, innerW, titleH), cText, Flags);
                y += titleH;
            }
            if (body.Length > 0)
            {
                TextRenderer.DrawText(g, body, fBody, new Rectangle(x, y, innerW, bodyH + 2), cText, Flags);
            }
        }

        public Bitmap Snapshot(Size size)
        {
            Bitmap bmp = new Bitmap(size.Width, size.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                PaintTip(g, size);
            }
            return bmp;
        }

        // Show next to the mouse (screen coordinates): bottom right by default; flip to the left / above if it does not fit
        public void ShowNear(Point cursor, Size size)
        {
            Rectangle wa = Screen.FromPoint(cursor).WorkingArea;
            int x = cursor.X + 16, y = cursor.Y + 22;
            if (x + size.Width > wa.Right - 4)
            {
                x = cursor.X - size.Width - 10;
                if (x < wa.Left + 4) x = Math.Max(wa.Left + 4, wa.Right - size.Width - 4);
            }
            if (y + size.Height > wa.Bottom - 4)
            {
                y = cursor.Y - size.Height - 14;
                if (y < wa.Top + 4) y = wa.Top + 4;
            }
            SetBounds(x, y, size.Width, size.Height);
            if (!Visible)
            {
                Show();
            }
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // Topmost: NOSIZE | NOMOVE | NOACTIVATE
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintTip(e.Graphics, ClientSize);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (fBody != null) fBody.Dispose();
                if (fTitle != null) fTitle.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ================================================================ Fixed tip texts
    internal static class TipText
    {
        public const string Close = "Close\nClicking x only shrinks the widget to the system tray; Stilla keeps running in the background.\nTo really quit: right-click the tray icon (or the widget) → Exit.";
        public const string Refresh = "Refresh now\nReads the quota again right away. It is read automatically every 60 seconds, so there is no need to click.";
        public const string Pause = "Background reading paused\nWhile paused, session logs are not read and there are no cache or email reminders (meant for gaming). Quota readings continue as usual.\nClick: resume.";
        public const string TabMore = "More settings (⋮)\nChoose which time tabs / views / columns to show, time tab presets, the speed window, window size, pause background reading, reminder settings, export, the hover tips switch, and more.";
        public const string Divider = "Drag to resize columns\nHold this divider and drag left or right; the two neighboring columns trade width.";
        public const string Mail = "Email reminder (@)\nTick the sessions you want to be reminded about and set how many minutes before the cache expires an email is sent.\nThe orange number on the button = how many sessions are ticked.\nClick: open settings.";
        public const string Jump = "Jump to session window\nClick: switch to the VS Code window of this session (the window title is matched by working folder) and bring the Claude Code extension back to this session.";
        public const string Share = "Share mini chart\nThe share of this row in the total of the current time tab: the longer the line (or circle), the bigger the share.";
        public const string Total = "Total\nThe total of all rows in the current time tab; it lines up with the columns above.";
        public const string TotalSessions = "Total\nThe total of all sessions in the current time tab; it lines up with the columns above. (The Expires / Speed / Share columns are not calculated per time tab; see their own tips.)";
        public const string Grip = "Adjust row spacing\nHold this divider and drag up or down: drag down to increase the spacing around each row, drag up to reduce it. Double-click to restore the default.\nYou can also pick it directly in the ⋮ menu → 'Row spacing'.";
        public const string Calib = "Estimate error\n'5h est. / Wk est.' are converted from price x a fitted factor. Each past segment of data is fitted using only the data before it and then predicted; this is the average relative error of the most recent segments: smaller = more trustworthy.";
        public const string Tiers = "Tier = thinking effort\nlow < medium < high < xhigh < max; ultra = Ultracode is on; +fast at the end = fast mode.\nThe higher the tier, the more quota the same tokens are worth (see the multiples in the Compare view). xhigh → max means it was switched from xhigh to max during this period.";

        public static readonly string[] View =
        {
            "Session view\nOne row per session: how long its cache will last, its recent quota speed and share, and its token usage in this period.\nClick a row: open its details; click ↗: jump to its window; click a header: sort.",
            "Model view\nOne row per session; the small tag on the right is the model and tier (thinking effort). '5h est. / Wk est.' = what this usage is worth in 5-hour / weekly quota (a fitted estimate, for reference only).\nClick a row: a popup with detailed data and the switch history for each model x tier.",
            "Compare view\nAll sessions merged and summed by 'model · tier', to compare how much quota each tier costs.\nThe last column 'Mult.' = how many times more quota each token costs than in the cheapest tier.",
            "Plan view\nOne row per account: this cycle's (current subscription period) usage converted at official API prices, the monthly fee, the payback multiple (usage / fee) and the next renewal date.\nClick a row: switch the statistics to this account.",
            "API view\nOnly counts non-Claude models used through an API (DeepSeek etc. in the local logs): records, input, output and estimated cost.\nClaude subscription usage is in the 'Plan' view.",
            "Trend view\nBar chart of the quota used per hour (24 hours) or per day (7 days). The dashed line = the pace that would exactly fill the whole quota window; a bar above it (red) means usage was too heavy in that period.\nHover a bar to see the exact numbers."
        };

        // Time tabs (index = tab id)
        public static readonly string[] Tab =
        {
            "5 min\nUsage in the last 5 minutes (rolling window), counted live.",
            "15 min\nUsage in the last 15 minutes (rolling window), counted live.",
            "30 min\nUsage in the last 30 minutes (rolling window), counted live.",
            "1 h\nUsage in the last hour (rolling window), counted live.",
            "3 h\nUsage in the last 3 hours (rolling window), counted live.",
            "This 5h\nUsage in the current 5-hour quota window: from the window start to the reset time (if the reset time cannot be read, 'the last 5 hours up to now' is used).\nIt is the same window as the 5-hour quota reading at the top, so you can see who used the most in it.",
            "Prev 5h\nUsage in the previous 5-hour window (the 5 hours before this window started), for comparison.",
            "Today\nUsage from 0:00 today until now, taken from the local session logs (updates a little slower than the live tabs).",
            "Yesterday\nUsage from 0:00 to 24:00 yesterday, taken from the local session logs.",
            "This week\nUsage from 0:00 on Monday of this week until now, taken from the local session logs.",
            "Custom\nA start and end time you set yourself (click to set it). The ⋮ menu can save up to 5 favorite ranges so one click switches to them."
        };
        public const string TabFoot = "\nA fuller / larger graphic = more usage in this period (log scale); the number below = total tokens. Click to switch.";

        // Note lines at the bottom of the Plan / API views
        public static readonly string[] SubInfo =
        {
            "Subscription cycle\nThe subscription info of the current account: plan, start date, which day of the cycle it is. 'Calendar month' = the start date is unknown, so the cycle runs from the 1st of each month to the end of the month.",
            "API-equivalent this cycle\nThis cycle's usage converted at official API prices; the number in brackets = how many times the monthly fee it is; daily = amount / days elapsed; cycle ≈ = daily x total days in the cycle.\nThis is not an actual charge: you only pay the monthly fee.",
            "Weekly quota forecast\nBased on recent usage, predicts whether the weekly quota will run out before it resets."
        };

        public static readonly string[] ApiInfo =
        {
            "API total\nAll non-Claude models used through an API in the log: records, input (including cache writes), output, and cache-hit tokens.",
            "Estimated cost\nEstimated at DeepSeek off-peak prices (CNY). Only DeepSeek has a price table; other models have no price and are not counted.",
            "Note\nOnly non-Claude models from the local logs are counted here. API usage in the Anthropic console needs an Admin Key, which this machine does not have, so it cannot be shown."
        };
    }

    // ================================================================ Engine
    internal sealed partial class Widget
    {
        private bool tipsOn = true;                                     // Hover tips switch (in the ⋮ menu), on by default

        private sealed class TipHit
        {
            public string Id;               // Stable id: the id of the same thing stays the same after a redraw, so the timer is not restarted again and again
            public RectangleF R;
            public string Text;
            public Func<string> Lazy;       // Tips that can only be written from current data: computed at the moment they are shown
            public int Prio;                // When areas overlap: the larger Prio wins; on a tie the smaller area (the more specific one) wins
        }

        private readonly List<TipHit> tipHits = new List<TipHit>();
        private System.Windows.Forms.Timer tipTimer;
        private TipWindow tipWin;
        private string tipCurId, tipSuppressId;
        private bool tipShown;
        private DateTime tipShownAt;
        private const int TipDelayMs = 550;

        // Positions recorded by DrawUsagePanel at the end of every frame (BuildTips needs them)
        private RectangleF tipForecast = RectangleF.Empty, tipDs = RectangleF.Empty;
        private float tipYTotal = -1f, tipRowsY;
        private int tipRowsN, tipInfoN;
        private bool tipRowsOther;
        private float tipInfoY = -1f;
        private double tipTot5, tipTotW;
        private bool tipSpeedKnown;

        private void InitTips()
        {
            tipTimer = new System.Windows.Forms.Timer();
            tipTimer.Interval = TipDelayMs;
            tipTimer.Tick += TipTick;
        }

        // ---------------------------------------------------------------- Registration
        private void Tip(string id, RectangleF r, string text)
        {
            AddTip(id, r, text, null, 0);
        }

        private void Tip(string id, RectangleF r, string text, int prio)
        {
            AddTip(id, r, text, null, prio);
        }

        private void TipL(string id, RectangleF r, Func<string> lazy)
        {
            AddTip(id, r, null, lazy, 0);
        }

        private void TipL(string id, RectangleF r, Func<string> lazy, int prio)
        {
            AddTip(id, r, null, lazy, prio);
        }

        private void AddTip(string id, RectangleF r, string text, Func<string> lazy, int prio)
        {
            if (!tipsOn || r.Width < 1f || r.Height < 1f)
            {
                return;
            }
            TipHit h = new TipHit();
            h.Id = id;
            h.R = r;
            h.Text = text;
            h.Lazy = lazy;
            h.Prio = prio;
            tipHits.Add(h);
        }

        private TipHit TipAt(Point p)
        {
            TipHit best = null;
            float bestArea = float.MaxValue;
            for (int i = 0; i < tipHits.Count; i++)
            {
                TipHit h = tipHits[i];
                if (!h.R.Contains(p))
                {
                    continue;
                }
                float a = h.R.Width * h.R.Height;
                if (best == null || h.Prio > best.Prio || (h.Prio == best.Prio && a <= bestArea))
                {
                    best = h;
                    bestArea = a;
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- Mouse
        private void TipMove(Point p)
        {
            if (!tipsOn || tipTimer == null)
            {
                TipReset();
                return;
            }
            TipHit h = TipAt(p);
            string id = h == null ? null : h.Id;
            if (id == null)
            {
                TipReset();
                return;
            }
            if (id == tipSuppressId)
            {
                // Just clicked here: do not pop up again until the mouse leaves and comes back
                tipCurId = id;
                TipHide();
                return;
            }
            tipSuppressId = null;
            if (id == tipCurId)
            {
                return;                 // Still on the same thing: if the tip is already shown leave it alone; if still waiting keep waiting
            }
            TipHide();
            tipCurId = id;
            tipTimer.Interval = TipDelayMs;
            tipTimer.Start();
        }

        private void TipTick(object sender, EventArgs e)
        {
            tipTimer.Stop();
            if (!tipsOn || !Visible || tipCurId == null || Control.MouseButtons != MouseButtons.None)
            {
                TipHide();
                return;
            }
            Point sp = System.Windows.Forms.Cursor.Position;
            if (!Bounds.Contains(sp))
            {
                TipReset();
                return;
            }
            TipHit h = TipAt(PointToClient(sp));
            if (h == null || h.Id != tipCurId)
            {
                TipReset();
                return;
            }
            if (tipShown)
            {
                // Already shown: every half second check whether the mouse is still there; hide it after 25 seconds of staying put
                if (DateTime.UtcNow - tipShownAt > TimeSpan.FromSeconds(25))
                {
                    tipSuppressId = tipCurId;
                    TipHide();
                    return;
                }
                tipTimer.Interval = 500;
                tipTimer.Start();
                return;
            }
            string text;
            try
            {
                text = h.Lazy != null ? h.Lazy() : h.Text;
            }
            catch (Exception)
            {
                text = h.Text;
            }
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (tipWin == null || tipWin.IsDisposed)
            {
                tipWin = new TipWindow();
            }
            // The font is fixed to Microsoft YaHei UI: some skins use handwriting / serif fonts, which are harder to read inside a tip
            Size size = tipWin.Prepare(text, "Microsoft YaHei UI", Math.Max(12.5f, fSmall.Size), TipBg(), TipBorder(), TextMain, (int)(380 * Math.Max(1f, scale)));
            tipWin.ShowNear(sp, size);
            tipShown = true;
            tipShownAt = DateTime.UtcNow;
            tipTimer.Interval = 500;
            tipTimer.Start();
        }

        private Color TipBg()
        {
            return SkinDraw.Mix(Bg, TextMain, 0.10f);
        }

        private Color TipBorder()
        {
            return SkinDraw.Mix(Bg, TextMain, 0.40f);
        }

        private void TipHide()
        {
            if (tipWin != null && !tipWin.IsDisposed && tipWin.Visible)
            {
                tipWin.Hide();
            }
            tipShown = false;
        }

        private void TipReset()
        {
            if (tipTimer != null)
            {
                tipTimer.Stop();
            }
            TipHide();
            tipCurId = null;
            tipSuppressId = null;
        }

        // Mouse clicked: hide the tip and do not show it again until the mouse leaves this thing
        private void TipClicked()
        {
            if (tipTimer != null)
            {
                tipTimer.Stop();
            }
            TipHide();
            tipSuppressId = tipCurId;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            TipReset();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible)
            {
                TipReset();
            }
        }

        private ToolStripMenuItem BuildTipsItem()
        {
            ToolStripMenuItem it = new ToolStripMenuItem("Hover tips (explain things when the mouse rests on them)");
            it.Checked = tipsOn;
            it.Click += delegate
            {
                tipsOn = !tipsOn;
                it.Checked = tipsOn;
                TipReset();
                Redraw();
                SaveSettings();
            };
            return it;
        }

        // ---------------------------------------------------------------- Master registration: called once after every frame is drawn
        private void BuildTips(int w, int h)
        {
            float s = scale;
            float pad = 14f * s;
            float inner = w - 2f * pad;
            float top = CompactH() * s;
            float rowH = rowHu * s;

            // Compact card
            TipL("card", new RectangleF(0, 0, w, showUsage ? top : h), CardTip);
            Tip("close", closeRect, TipText.Close);
            Tip("refresh", refreshRect, TipText.Refresh);
            TipL("chip", accountRect, ChipTip);
            if (!showUsage)
            {
                return;
            }

            // Usage panel: title row, view buttons, time tabs, big number
            Tip("pause", pauseRect, TipText.Pause);
            for (int i = 0; i < segRects.Length && i < TipText.View.Length; i++)
            {
                Tip("seg" + i, segRects[i], TipText.View[i]);
            }
            for (int i = 0; i < TabCount; i++)
            {
                int ti = i;
                TipL("tab" + i, usageTabs[i], delegate { return TabTip(ti); });
            }
            Tip("tabmore", tabMoreRect, TipText.TabMore);
            TipL("big", new RectangleF(pad, top + 98f * s, inner, 26f * s), BigTip);

            if (usageMode == 5)
            {
                return;             // The tips of the trend view are registered in DrawTrendView (only it knows where the bars are)
            }

            int md = usageMode;
            Tip("mail", mailBtnRect, TipText.Mail, 1);

            // Header + the body area of each column
            float hh = 14f * s;
            int nameSpan = md == 0 ? 1 : md == 2 ? 2 : 3;
            float nameWidth = 0;
            for (int c = 0; c < nameSpan; c++)
            {
                nameWidth += colWidth[c];
            }
            TipL("headname" + md, new RectangleF(pad, colTop - 2f * s, nameWidth, hh + 4f * s), delegate { return NameHeadTip(md); });
            float x = pad + nameWidth;
            for (int c = nameSpan; c < 8; c++)
            {
                if (colWidth[c] < 1f)
                {
                    continue;
                }
                int cc = c;
                TipL("head" + md + "_" + c, new RectangleF(x, colTop - 2f * s, colWidth[c], hh + 4f * s), delegate { return ColumnTip(md, cc, true); });
                TipL("col" + md + "_" + c, new RectangleF(x, colTop + hh + 2f * s, colWidth[c], Math.Max(1f, colBottom - colTop - hh - 2f * s)), delegate { return ColumnTip(md, cc, false); }, -1);
                x += colWidth[c];
            }
            if (md < 3)
            {
                float dz = Math.Max(4f, 4f * s);
                for (int i = FirstEdge(); i < 7; i++)
                {
                    if (colOn[i] && colNext[i] >= 0)
                    {
                        Tip("div" + i, new RectangleF(colEdgeX[i] - dz, colTop, 2f * dz, colBottom - colTop), TipText.Divider, 5);
                    }
                }
            }

            // Rows
            float nameW = colWidth[0];
            if (md == 0 || md == 1)
            {
                UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
                Dictionary<string, UsageTracker.SessionTotal> byId = new Dictionary<string, UsageTracker.SessionTotal>();
                foreach (UsageTracker.SessionTotal t0 in cur.Rows)
                {
                    byId[t0.Session] = t0;
                }
                bool jumpOn = Show(Feat.Jump);
                float cacheW = colWidth[1], spW = colWidth[2];
                float shareW = (skin.ShareWidth + 17f) * s - 3f * s;
                foreach (KeyValuePair<RectangleF, string> kv in sessionHits)
                {
                    RectangleF R = kv.Key;
                    string sid = kv.Value;
                    if (md == 1)
                    {
                        Tip("share1:" + sid, new RectangleF(R.X, R.Y, shareW, R.Height), TipText.Share + "\nThis row = its share of the total tokens in the current time tab.", 1);
                        TipL("mrow:" + sid, new RectangleF(R.X + shareW, R.Y, Math.Max(10f * s, nameW + colWidth[1] + colWidth[2] - shareW), R.Height), delegate { return ModelRowTip(sid); });
                        continue;
                    }
                    UsageTracker.SessionTotal t;
                    if (!byId.TryGetValue(sid, out t))
                    {
                        continue;
                    }
                    UsageTracker.SessionTotal tt = t;
                    bool watched = MailStore.Enabled && MailStore.Find(sid) != null;
                    float rightPad = (jumpOn ? 16f * s : 0f) + (watched ? 11f * s : 0f);
                    TipL("name:" + sid, new RectangleF(R.X, R.Y, Math.Max(10f * s, nameW - rightPad), R.Height), delegate { return SessionNameTip(tt); });
                    if (watched)
                    {
                        TipL("mailrow:" + sid, new RectangleF(R.X + nameW - rightPad, R.Y, 11f * s, R.Height), delegate { return MailRowTip(tt.Session); }, 1);
                    }
                    if (cacheW >= 1f)
                    {
                        TipL("cache:" + sid, new RectangleF(R.X + nameW, R.Y, cacheW, R.Height), delegate { return CacheTip(tt); });
                    }
                    if (spW >= 1f)
                    {
                        TipL("speed:" + sid, new RectangleF(R.X + nameW + cacheW, R.Y, spW, R.Height), delegate { return SpeedCellTip(tt); });
                    }
                    if (colWidth[3] >= 1f)
                    {
                        TipL("occ:" + sid, new RectangleF(R.X + nameW + cacheW + spW, R.Y, colWidth[3], R.Height), delegate { return OccCellTip(tt); });
                    }
                }
                if (md == 0)
                {
                    foreach (KeyValuePair<RectangleF, UsageTracker.SessionTotal> kv in jumpHits)
                    {
                        Tip("jump:" + kv.Value.Session, kv.Key, TipText.Jump, 1);
                    }
                }
            }
            else if (md == 2 || md == 3 || md == 4)
            {
                float shareW = (skin.ShareWidth + 17f) * s - 3f * s;
                int rows = tipRowsOther && md == 2 ? tipRowsN - 1 : tipRowsN;
                if ((md == 2 && PeriodFor(usagePeriod).TierRows.Count == 0) || (md == 3 && AccountStore.Accounts.Count == 0) || (md == 4 && (apiRows == null || apiRows.Count == 0)))
                {
                    rows = 0;           // These rows are the 'no data' hint text, not real data rows
                }
                for (int i = 0; i < rows; i++)
                {
                    int ri = i;
                    float ry = tipRowsY + i * rowH;
                    string shareText = md == 2 ? TipText.Share + "\nThis row = its share of the converted quota (models without a quota factor are counted by tokens)."
                        : md == 3 ? TipText.Share + "\nThis row = its share of the total of all accounts' amounts this cycle." : TipText.Share + "\nThis row = its share of the total estimated cost.";
                    Tip("shareb" + md + "_" + i, new RectangleF(pad, ry, shareW, rowH), shareText, 1);
                    float nameSpanW = md == 2 ? colWidth[0] + colWidth[1] : colWidth[0] + colWidth[1] + colWidth[2];
                    TipL("brow" + md + "_" + i, new RectangleF(pad + shareW, ry, Math.Max(10f * s, nameSpanW - shareW), rowH), delegate { return BodyRowTip(md, ri); });
                }
            }

            if (moreRect.Width > 1f)
            {
                TipL("more", moreRect, MoreTip, 1);
            }

            // The total row and the rows below it
            if (tipYTotal >= 0f)
            {
                Tip("total", new RectangleF(pad, tipYTotal, 34f * s, rowH), md == 0 ? TipText.TotalSessions : TipText.Total, 1);
                if (md >= 1 && quota != null && quota.CalibErr >= 0)
                {
                    Tip("calib", new RectangleF(pad + 34f * s, tipYTotal, Math.Max(1f, nameW - 34f * s), rowH), TipText.Calib, 1);
                }
                if (md == 0 && colWidth[2] >= 1f)
                {
                    TipL("totspeed", new RectangleF(pad + nameW + colWidth[1], tipYTotal, colWidth[2], rowH), TotalSpeedTip, 1);
                }
                if (md == 0 && colWidth[3] >= 1f)
                {
                    TipL("totocc", new RectangleF(pad + nameW + colWidth[1] + colWidth[2], tipYTotal, colWidth[3], rowH), TotalOccTip, 1);
                }
            }
            Tip("grip", gripRect, TipText.Grip, 5);
            if (healthRect.Width > 1f)
            {
                TipL("health", healthRect, HealthTip, 2);
            }
            if (tipForecast.Width > 1f)
            {
                TipL("forecast", tipForecast, ForecastTip, 1);
            }
            if (tipDs.Width > 1f)
            {
                TipL("ds", tipDs, DsTip, 1);
            }
            if (tipInfoY >= 0f)
            {
                for (int i = 0; i < tipInfoN; i++)
                {
                    string[] arr = md == 3 ? TipText.SubInfo : TipText.ApiInfo;
                    Tip("info" + md + "_" + i, new RectangleF(pad, tipInfoY + i * rowH, inner, rowH), arr[i], 1);
                }
            }
        }

        // ---------------------------------------------------------------- Tips that need current data
        private string CardTip()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Quota card\n");
            sb.Append("• The percentage = the share already used (not what is left).\n");
            sb.Append("• 5-hour quota: counted for 5 hours from your first message; it resets to zero when the time is up.\n");
            sb.Append("• Weekly quota: a 7-day cycle that resets to zero at the reset time.\n");
            sb.Append("• Clock = the reset time; the countdown next to it = how long until the reset.\n");
            sb.Append("• Small text at the bottom = time of the last read (once every 60 seconds; it only reads the quota, sends no message and uses no quota).");
            if (data != null)
            {
                DateTime now = DateTime.Now;
                string c5, l5, cw, lw;
                ResetParts(data.FiveHour, now, false, out c5, out l5);
                ResetParts(data.SevenDay, now, true, out cw, out lw);
                if (data.FiveHour != null)
                {
                    sb.Append("\n\nNow · 5-hour quota used " + data.FiveHour.Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + (l5.Length > 0 ? " (" + c5 + " reset; time left: " + l5 + ")" : ""));
                }
                if (data.SevenDay != null)
                {
                    sb.Append("\nNow · weekly quota used " + data.SevenDay.Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + (lw.Length > 0 ? " (" + cw + " reset; time left: " + lw + ")" : ""));
                }
            }
            return sb.ToString();
        }

        private string ChipTip()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Account and plan\nThe account currently logged in: id · plan (for example B · Pro). Quota conversion factors are recorded separately per 'account:plan'.\n");
            if (indicatorState != CacheState.None)
            {
                sb.Append("The small dot on the left = the most urgent cache state among all sessions: red = about to expire; yellow = 10-30 min left; green = a session is running; blue = a session is waiting for you.\n");
            }
            if (AccountStore.Current == null)
            {
                sb.Append("'Not bound' = the current login has no matching account profile yet.\n");
            }
            sb.Append("Click: switch account / manage accounts and subscriptions.");
            return sb.ToString();
        }

        private string TabTip(int i)
        {
            string t = TipText.Tab[i];
            string range = "";
            try
            {
                range = PeriodFor(i).Range;
            }
            catch (Exception)
            {
            }
            if (!string.IsNullOrEmpty(range) && range != "Reading...")
            {
                t += "\nCurrent range: " + range;
            }
            return t + TipText.TabFoot;
        }

        private string BigTip()
        {
            UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
            return "Total TOKENS\n= input + output + cache write + cache read, the sum of those four token counts, for the currently selected time tab (the time on the right = the range counted).\n"
                + "Cache reads usually make up the vast majority (they are billed at a very low price), so this number looks huge without meaning that the same amount of quota was used.\n"
                + "Now: input " + UsageTracker.FormatTokens(cur.Input) + " - Output " + UsageTracker.FormatTokens(cur.Output)
                + " · cache write " + UsageTracker.FormatTokens(cur.CacheWrite) + " · cache read " + UsageTracker.FormatTokens(cur.CacheRead);
        }

        private string NameHeadTip(int md)
        {
            string sortNote = md <= 2 ? "\nClick: sort by this column (click again to reverse, a third click restores the default order)." : "";
            switch (md)
            {
                case 0:
                    return "Session\nShows the title if there is one, otherwise 'project name · #session id'. '+N sub' = the N subagent tasks it spawned.\nThe color of the small dot on the far left = the cache state (see 'Expires')." + sortNote;
                case 1:
                    return "Model\nOne row per session (title). The small tag at the end of the row = the model and tier (thinking effort)." + sortNote;
                case 2:
                    return "Model · tier\nDifferent tiers (thinking effort) of the same model are listed separately. The small tag at the end of the row = the tier." + sortNote;
                case 3:
                    return "Account\nThe account profiles you created in 'Manage accounts and subscriptions'. 'Current' = the one logged in now.";
                default:
                    return "API model\nThe name of a non-Claude model used through an API; the dates on the right = its first and last record in the log.";
            }
        }

        private string ColumnTip(int md, int col, bool header)
        {
            string t;
            string click = "";
            bool sortable = md <= 2 && header;
            switch (md)
            {
                case 0:
                    switch (col)
                    {
                        case 1:
                            t = "Expires\nHow much longer the prompt cache will last. Once it expires, the next message has to write the whole context into the cache again, which costs more than ten times as much as continuing it, so when it is about to expire it is best to send a message to keep it alive.\n"
                                + "Colors: green = running (the cache renews itself) · blue = waiting for you (permission prompt / question etc.; the cache is still counting down) · white = 30-60 min left · yellow = 10-30 min · red = under 10 min (a system notification pops up) · gray = expired / no cache.";
                            break;
                        case 2:
                            t = "Speed (last " + SpeedSpan(speedMin) + ")\nLeft = 5-hour quota speed, right = weekly quota speed, both in quota percentage points per hour.\n"
                                + "Example: 30 / 3.97 = at this rate you use 30% of the 5-hour quota and 3.97% of the weekly quota per hour (quota actually used in the window, scaled to one hour).\n"
                                + "The window is wall-clock time, so a session that goes quiet slows down. Gray = idle / cache expired: shows its average speed when it was last active, for reference only and not counted in the total.\n"
                                + "'≈' = the quota factor is derived from the plan multiplier and may be off by several times.";
                            if (header)
                            {
                                click = "\nClick the 'Speed' label: pick the window (5 min ~ 48 h); click the small triangle on the right: sort by speed.";
                                sortable = false;
                            }
                            break;
                        case 3:
                            t = "Share (last " + SpeedSpan(speedMin) + ")\nHow much quota this session used in the window: left = 5-hour quota, right = weekly quota, both in quota percentage points.\n"
                                + "Example: 2.1 / 0.28 = in this period it used 2.1% of the 5-hour quota and 0.28% of the weekly quota.\n"
                                + "The window is the same one as the 'Speed' column; share / window hours = speed. Gray = no share in the window: shows its share in a window of the same length when it was last active, for reference only and not counted in the total.\n"
                                + "'≈' = the quota factor is derived from the plan multiplier and may be off by several times.";
                            if (header)
                            {
                                click = "\nClick the 'Share' label: pick the window (5 min ~ 48 h); click the small triangle on the right: sort by share.";
                                sortable = false;
                            }
                            break;
                        case 4:
                            t = "Input\nNew input tokens that did not hit the cache (what you just sent, tool results, etc.), billed at the standard input price.";
                            break;
                        case 5:
                            t = "Output\nTokens generated by the model (including thinking), the most expensive per token.";
                            break;
                        case 6:
                            t = "Cache write\nTokens newly written to the cache this turn: building the cache for the first time, or re-writing it after it expired. Priced at about 1.25x the input price (5-minute cache) or 2x (1-hour cache).";
                            break;
                        default:
                            t = "Cache read\nTokens that hit the cache and are read at a very low price (about 1/10 of the input price). A long session re-reads its whole context every turn, so this is usually the biggest item - most of the 'TOKENS' total comes from here.";
                            break;
                    }
                    break;
                case 1:
                    t = col == 3 ? "Tokens\nTotal tokens of this session in this period (input + output + cache write + cache read)."
                        : col == 4 ? "5h est.\nThese tokens converted to 5-hour quota (model factor x tier correction). A fitted estimate, not a true value from the server; '≈' = the factor is derived, not measured."
                        : col == 5 ? "Wk est.\nThese tokens converted to weekly quota (same method as '5h est.'). A fitted estimate; '≈' = the factor is derived, not measured."
                        : "Cache read\nTokens that hit the cache and are read at a very low price (about 1/10 of the input price).";
                    break;
                case 2:
                    t = col == 2 ? "Tokens\nTotal tokens of this model · tier in this period (input + output + cache write + cache read)."
                        : col == 3 ? "Output\nTokens generated by the model (including thinking)."
                        : col == 4 ? "Thinking\nThe part of the output tokens used for thinking; already included in 'Output'."
                        : col == 5 ? "5h est.\nThese tokens converted to 5-hour quota (model factor x tier correction). A fitted estimate; '≈' = the factor is derived, not measured."
                        : "Multiple\nHow many times more quota each token costs compared with the cheapest tier (based on the 5-hour quota). ×1.0 = cheapest; '≈×' = at least one side is a derived value.";
                    break;
                case 3:
                    t = col == 3 ? "Usage this period\nUsage in the current subscription cycle, as a dollar amount at official API prices (API-equivalent, not an actual charge)."
                        : col == 4 ? "Monthly fee\nThe subscription fee you entered (change it in 'Manage accounts and subscriptions')."
                        : col == 5 ? "Payback\nUsage this period / monthly fee. ×3 = the same usage via the API would cost 3 times the monthly fee."
                        : "Renews\nNext renewal date. '—' = counted by calendar month (start date unknown).";
                    break;
                default:
                    t = col == 3 ? "Messages\nHow many reply records of this model are in the log."
                        : col == 4 ? "Input\nInput + cache-write tokens (both priced at the cache-miss rate)."
                        : col == 5 ? "Output\nTokens generated by the model."
                        : "Estimated cost (CNY)\nEstimated at DeepSeek off-peak prices: input ¥" + DsMissPerM.ToString("0.##", CultureInfo.InvariantCulture)
                            + ", cache hit ¥" + DsHitPerM.ToString("0.##", CultureInfo.InvariantCulture) + ", out ¥" + DsOutPerM.ToString("0.##", CultureInfo.InvariantCulture)
                            + " / million tokens. Only DeepSeek has a price table; other models show —.";
                    break;
            }
            if (sortable)
            {
                click = "\nClick: sort by this column (click again to reverse, a third click restores the default order).";
            }
            return t + click;
        }

        private string SessionNameTip(UsageTracker.SessionTotal t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(t.Title != null ? t.Title : t.Name + " · #" + t.ShortId);
            sb.Append("\nProject: " + t.Name);
            sb.Append("\nSession ID: " + t.Session);
            if (t.SubCount > 0)
            {
                sb.Append("\nSubagent tasks: " + t.SubCount + " (subagents; their usage is merged into this row)");
            }
            if (t.LastUtc > DateTime.MinValue)
            {
                sb.Append("\nLast activity in this period: " + t.LastUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }
            double? usd = PeriodUsd(t.Session);
            if (usd.HasValue)
            {
                sb.Append("\nUsage in the current time tab at official prices ≈ " + UsdText(usd.Value) + " (API-equivalent, not an actual charge)");
            }
            sb.Append("\nClick: open this session's details.");
            return sb.ToString();
        }

        private string MailRowTip(string session)
        {
            MailWatch mw = MailStore.Find(session);
            if (mw == null)
            {
                return "";
            }
            return "Email reminder on\nYou will get an email when this session's cache has " + MailStore.MinsText(mw.Mins) + " min left.\nClick '@' in the list header to change it.";
        }

        private static string RemainText(double remMin)
        {
            return FormatRemain(TimeSpan.FromMinutes(remMin));
        }

        private string CacheTip(UsageTracker.SessionTotal t)
        {
            UsageTracker.LiveInfo li;
            usageSnap.Live.TryGetValue(t.Session, out li);
            DateTime nowUtc = DateTime.UtcNow;
            double remMin;
            CacheState st = StateOf(li, t.Active, t.CacheExpiryUtc, nowUtc, out remMin);
            DateTime? exp = t.CacheExpiryUtc.HasValue ? t.CacheExpiryUtc : (li != null ? li.ExpiryUtc : null);
            string ctx = li != null && li.Ctx > 0 ? " (about " + UsageTracker.FormatTokens(li.Ctx) + " tokens)" : "";
            string at = exp.HasValue ? exp.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "";
            string ttl = li != null && li.TtlMin > 0 ? "This session's cache lifetime is " + (li.TtlMin >= 60 && li.TtlMin % 60 == 0 ? (li.TtlMin / 60) + " h" : li.TtlMin + " min") + "; every message you send restarts the timer.\n" : "";
            switch (st)
            {
                case CacheState.Running:
                    return "Running\nWriting or generating (new records in the last 60 seconds, or Claude Code reports it is busy). The cache renews with every request and does not expire.";
                case CacheState.Waiting:
                    return "Waiting for you\nClaude Code is waiting for you to act (permission prompt / question / plan approval, etc.). It is neither running nor finished; the cache is still counting down"
                        + (remMin > 0 ? "; time left: " + RemainText(remMin) + " (" + at + ")" : "") + ". ";
                case CacheState.None:
                    if (exp.HasValue)
                    {
                        return "Expired\nThe cache expired at " + at + ". The next message has to write the whole context" + ctx + " into the cache again, over 10x as expensive as continuing.";
                    }
                    return "No cache\nNo cache record for this session: nothing has been cached yet, or the record is outside the counted range.";
                default:
                    return "Cache left " + RemainText(remMin) + "\nExpires at " + at + ".\n" + ttl
                        + "After it expires, your next message has to write the whole context" + ctx + " into the cache again, over 10x as expensive as continuing."
                        + (st == CacheState.Urgent ? "\nAbout to expire: to keep going, send a message now to renew the cache." : "");
            }
        }

        private string SpeedCellTip(UsageTracker.SessionTotal t)
        {
            SpeedInfo si = SpeedFor(t.Session);
            string span = SpeedSpan(speedMin);
            string approx = quota != null && quota.PlanDerived ? "\n'≈': the quota factor is derived from the plan multiplier and may be off by several times." : "";
            if (si != null && si.Known && (si.R5 > 0 || si.Rw > 0))
            {
                return "Last " + span + " speed\n5-hour quota: " + RateText(si.R5, false) + " points per hour; weekly quota: " + RateText(si.Rw, true) + " points per hour.\n"
                    + "At this pace the 5-hour quota would run out in about " + (si.R5 > 0 ? HoursText(100.0 / si.R5) : "—") + " (this session only)." + approx;
            }
            if (si != null && si.HasGray && (si.G5 > 0 || si.Gw > 0))
            {
                return "Gray: idle\nIn the last " + span + ", this session used no quota (idle, or its cache has expired). The gray numbers are its average speed in a window of the same length when it was last active: "
                    + "5-hour quota " + RateText(si.G5, false) + " points per hour, weekly quota " + RateText(si.Gw, true) + " points per hour.\nFor reference only; not counted in the total or the cap forecast." + approx;
            }
            return "No speed\nIn the last " + span + ", this session used no quota and has no usable history (or the current account has no quota conversion factors yet, see the Plan view).";
        }

        private string OccCellTip(UsageTracker.SessionTotal t)
        {
            SpeedInfo si = SpeedFor(t.Session);
            string span = SpeedSpan(speedMin);
            string approx = quota != null && quota.PlanDerived ? "\n'≈': the quota factor is derived from the plan multiplier and may be off by several times." : "";
            if (si != null && si.Known && (si.P5 > 0 || si.Pw > 0))
            {
                return "Last " + span + " share\nThis session's usage over the last " + span + ": " + RateText(si.P5, false) + "% of the 5-hour quota and " + RateText(si.Pw, true) + "% of the weekly quota." + approx;
            }
            if (si != null && si.HasGray && (si.GP5 > 0 || si.GPw > 0))
            {
                return "Gray: idle\nIn the last " + span + ", this session used no quota. The gray numbers are its share in a window of the same length when it was last active: " + RateText(si.GP5, false) + "% of the 5-hour quota and "
                    + RateText(si.GPw, true) + "% of the weekly quota.\nFor reference only; not counted in the total." + approx;
            }
            return "No share\nIn the last " + span + ", this session used no quota and has no usable history (or the current account has no quota conversion factors yet, see the Plan view).";
        }

        private string TotalOccTip()
        {
            string span = SpeedSpan(speedMin);
            if (!tipSpeedKnown || (occ5 <= 0 && occW <= 0))
            {
                return "Total share\nIn the last " + span + ", no session used any quota (or there are no quota conversion factors yet).";
            }
            return "Total share (last " + span + ")\nAll sessions combined, over the last " + span + ": " + RateText(occ5, false) + "% of the 5-hour quota and " + RateText(occW, true)
                + "% of the weekly quota (including sessions no longer in the list; gray ones are not counted).";
        }

        private string TotalSpeedTip()
        {
            string span = SpeedSpan(speedMin);
            if (!tipSpeedKnown || (tipTot5 <= 0 && tipTotW <= 0))
            {
                return "Total speed\nIn the last " + span + ", no session used any quota (or there are no quota conversion factors yet).";
            }
            return "Total speed (last " + span + ")\nCombined speed of all sessions: 5-hour quota " + RateText(tipTot5, false) + " points per hour, weekly quota " + RateText(tipTotW, true)
                + " points per hour.\nOnly counts sessions that used quota in the last " + span + " (gray ones are not counted). The 'Cap forecast' row below is calculated from this.";
        }

        private string ForecastTip()
        {
            return "Cap forecast\nAssuming the 'Total' speed above continues, how long the 5-hour quota and the weekly quota will each last (= remaining quota / speed).\n"
                + "• Red + '(before reset by X)' = at this speed it runs out before the reset.\n• '(after reset)' = it will not run out before the reset.\n"
                + "This is only an extrapolation: a window as short as the last " + SpeedSpan(speedMin) + " swings a lot. For a steadier forecast, click the 'Speed' header and use a longer window (15~60 min).";
        }

        private string DsTip()
        {
            return "DeepSeek off-peak price estimate\nWhat these tokens would cost in CNY if billed as DeepSeek-Flash usage (input ¥" + DsMissPerM.ToString("0.##", CultureInfo.InvariantCulture)
                + ", cache hit ¥" + DsHitPerM.ToString("0.##", CultureInfo.InvariantCulture) + ", out ¥" + DsOutPerM.ToString("0.##", CultureInfo.InvariantCulture)
                + " / million tokens, cache writes at the input price). For comparison only, you did not actually pay this.\nTo hide this row: ⋮ menu → 'Other display items', untick 'DeepSeek cost estimate row at the bottom'.";
        }

        private string HealthTip()
        {
            HealthInfo hi = GetHealth();
            string now = hi != null && hi.Line.Length > 0 ? hi.Line : hi != null ? hi.Short : "";
            return "Estimate self-check\nCompares 'how much the quota reading rose' with 'how much the factor-based estimate rose': the reading is the server's true value, the estimate is what the program computes. A big gap means the factors are wrong (derived / plan just changed / account changed), "
                + "or somewhere else (web, another computer) is using the same quota.\n" + (now.Length > 0 ? "Now: " + now + "\n" : "") + "Click: see details.";
        }

        private string MoreTip()
        {
            if (rowsExpanded)
            {
                return "Collapse\nClick to collapse; the window returns to its original height. When rows do not fit, use the mouse wheel to scroll.";
            }
            return "More rows\nClick to show all rows (the window grows automatically; use the mouse wheel to scroll when they do not fit). The numbers on the right = the total of the collapsed rows.";
        }

        private string ModelRowTip(string session)
        {
            ModelGroup grp = null;
            foreach (ModelGroup g in BuildModelGroups(PeriodFor(usagePeriod)))
            {
                if (g.Session == session)
                {
                    grp = g;
                    break;
                }
            }
            if (grp == null)
            {
                return "";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(SessionTitle(session) + "\nUsed in this period:");
            int n = 0;
            foreach (UsageTracker.ModelTotal p in grp.Parts)
            {
                if (++n > 6)
                {
                    sb.Append("\n... and " + (grp.Parts.Count - 6) + " more (click to see all)");
                    break;
                }
                string est = PartEstimate(p, false);
                sb.Append("\n• " + ShortModel(p.Model) + (p.Tier.Length > 0 && p.Tier != "Unlabeled" ? " · " + p.Tier : "") + ": " + UsageTracker.FormatTokens(p.Total) + " tokens"
                    + (est == "—" ? " (no quota factor)" : ", equal to " + est + " of the 5-hour quota"));
            }
            if (grp.Parts.Count > 1)
            {
                sb.Append("\n▾ at the start of a row = the model or tier changed during this period.");
            }
            sb.Append("\nTier = thinking effort (low < medium < high < xhigh < max; Ultracode = ultra; +fast = fast mode).\nClick: show the detailed data and the switch history.");
            return sb.ToString();
        }

        private string BodyRowTip(int md, int i)
        {
            if (md == 2)
            {
                return TipText.Tiers;
            }
            if (md == 3)
            {
                List<Account> items = SortedAccounts();
                if (i < 0 || i >= items.Count)
                {
                    return "";
                }
                Account a = items[i];
                StringBuilder sb = new StringBuilder();
                sb.Append(a.Label + " · " + PlanInfo.NameOf(a.Plan));
                if (a == AccountStore.Current)
                {
                    sb.Append(" (current login)");
                }
                sb.Append("\nMonthly fee: " + (a.Fee > 0 ? Money(a.Fee) : "not set"));
                sb.Append("\nSubscription start: " + (a.Start == DateTime.MinValue ? "unknown" : a.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                if (a.BindOrg.Length == 0)
                {
                    sb.Append("\n'Unbound' = no login info is linked to this account profile yet.");
                }
                sb.Append("\nClick: switch the statistics to this account.");
                return sb.ToString();
            }
            return "API model\nDates to the right of the model name = its first ~ last record in the log; the small chart on the left = its share of the total estimated cost.";
        }

        // Trend view (called from DrawTrendView)
        private void TipTrendBar(int i, RectangleF slotRect, bool hourly, double val, double usd, string label, double line, bool last)
        {
            string id = "tbar" + (hourly ? "h" : "d") + i;
            double pct = line > 0 ? val / line * 100.0 : 0;
            TipL(id, slotRect, delegate
            {
                string when = hourly ? label + ":00 ~ " + label + ":59" : label;
                string unit = hourly ? "% of the 5-hour quota for a reload" : "% of the weekly quota";
                return when + "\nUsed " + val.ToString("0.#", CultureInfo.InvariantCulture) + unit + "\nShare of the full-quota speed: " + pct.ToString("0", CultureInfo.InvariantCulture) + "%"
                    + "\nAPI value: " + UsdText(usd) + (last ? "\n(this " + (hourly ? "hour" : "day") + " is not over yet, so the number will still grow)" : "");
            });
        }
    }
}
