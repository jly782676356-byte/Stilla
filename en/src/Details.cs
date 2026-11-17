// Small window opened by clicking a session in the Model view: for this session in the current time range, the detailed numbers of every "model x tier",
// plus a timeline of mid-session tier / model switches (in time order, with start, end and usage of each run). It can be copied as text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace Stilla
{
    // Colors used by the popup (taken from the widget's current skin and light/dark setting)
    internal sealed class PopupColors
    {
        public Color Bg, Border, Main, Dim, Track, Amber;
    }

    // One table row (already computed, ready to display)
    internal sealed class DetailRow
    {
        public string Tier = "", Model = "", Span = "";
        public int Rank;
        public long Msgs, In, Out, Think, Cw, Cr;
        public string Pct5 = "—", PctW = "—", Usd = "—";
        public bool Bold;
    }

    // One continuous run of model + tier (no change in between). ReadRuns cuts these out of the log in time order
    internal sealed class SessionRun
    {
        public string Model = "", Tier = "";
        public bool Sub;                                                         // Rows from subagent tasks (subagents / workflow), merged into one row per model + tier
        public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;     // Local time
        public long Msgs, Think;
        public readonly long[] Tok = new long[10];
    }

    // Statistics window for the Session view "Speed": a custom number of minutes (1-2880, at most 48 hours)
    internal sealed class MinutesForm : Form
    {
        private readonly NumericUpDown num = new NumericUpDown();

        public int Minutes
        {
            get { return (int)num.Value; }
        }

        public MinutesForm(int current)
        {
            Text = "Speed window";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            DialogDpi.Apply(this);
            ClientSize = new Size(360, 120);
            Label l = new Label();
            l.Text = "Compute speed from the usage in the last:";
            l.Location = new Point(14, 14);
            l.AutoSize = true;
            Controls.Add(l);
            num.Location = new Point(14, 42);
            num.Size = new Size(90, 24);
            num.Minimum = 1;
            num.Maximum = 2880;
            num.Value = Math.Max(1, Math.Min(2880, current));
            Controls.Add(num);
            Label u = new Label();
            u.Text = "minutes (1-2880, up to 48 hours)";
            u.Location = new Point(112, 46);
            u.AutoSize = true;
            Controls.Add(u);
            Button ok = new Button();
            ok.Text = "OK";
            ok.Location = new Point(184, 80);
            ok.Size = new Size(80, 28);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Location = new Point(270, 80);
            cancel.Size = new Size(80, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    internal sealed class DetailPopup : Form
    {
        private static readonly string[] Heads = { "Tier", "Model", "Period", "Msgs", "Input", "Output", "Think", "Cache W", "Cache R", "5h est.", "Wk est.", "API equiv." };
        private static readonly float[] Wid = { 84, 128, 122, 56, 60, 62, 62, 64, 80, 72, 68, 62 };

        private readonly string title, range, family;
        private readonly List<DetailRow> tiers;
        private readonly List<DetailRow> runs;
        private readonly int runsHidden;
        private readonly PopupColors c;
        private readonly float s;
        private readonly Action openDrill;
        private readonly Font fBody, fBold, fSmall;
        private readonly float pad, rowH;
        private RectangleF btnDrill, btnCopy, btnClose;
        private bool copied;

        public DetailPopup(string title, string range, List<DetailRow> tiers, List<DetailRow> runs, int runsHidden, PopupColors colors, string family, float scale, Action openDrill)
        {
            this.title = title;
            this.range = range;
            this.tiers = tiers;
            this.runs = runs;
            this.runsHidden = runsHidden;
            this.c = colors;
            this.family = family;
            this.s = scale;
            this.openDrill = openDrill;
            pad = 12f * s;
            rowH = 20f * s;
            fBody = new Font(family, 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmall = new Font(family, 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fBold = new Font(family, 12.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = c.Bg;

            float w = pad * 2;
            foreach (float x in Wid)
            {
                w += x * s;
            }
            float h = pad + 22f * s + 20f * s;                       // Title and description
            h += SectionHeight(tiers.Count);
            if (runs != null && runs.Count > 0)
            {
                h += SectionHeight(runs.Count) + (runsHidden > 0 ? rowH : 0);
            }
            h += 40f * s;                                            // Buttons at the bottom
            ClientSize = new Size((int)Math.Ceiling(w), (int)Math.Ceiling(h));

            Deactivate += delegate
            {
                if (!IsDisposed)
                {
                    BeginInvoke(new MethodInvoker(delegate { if (!IsDisposed) Close(); }));
                }
            };
        }

        private float SectionHeight(int rows)
        {
            return 22f * s + rowH * (rows + 1) + 8f * s;           // Subheading + header + rows
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            fBody.Dispose();
            fSmall.Dispose();
            fBold.Dispose();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (btnClose.Contains(e.Location))
            {
                Close();
            }
            else if (btnDrill.Contains(e.Location))
            {
                Close();
                if (openDrill != null)
                {
                    openDrill();
                }
            }
            else if (btnCopy.Contains(e.Location))
            {
                try
                {
                    Clipboard.SetText(AsText());
                    copied = true;
                    Invalidate();
                }
                catch (Exception)
                {
                    // The clipboard is in use by another program
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = btnClose.Contains(e.Location) || btnDrill.Contains(e.Location) || btnCopy.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        // ------------------------------------------------------------ drawing
        private void Str(Graphics g, string t, Font f, Color col, RectangleF r, StringAlignment a)
        {
            if (string.IsNullOrEmpty(t))
            {
                return;
            }
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            using (SolidBrush b = new SolidBrush(col))
            {
                sf.Alignment = a;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(t, f, b, r, sf);
            }
        }

        private float Measure(Graphics g, string t, Font f)
        {
            return g.MeasureString(t, f, 4096, StringFormat.GenericTypographic).Width;
        }

        // Small tier label: same style as in the Model view, drawn left-aligned at x
        private void Badge(Graphics g, string t, int rank, float x, float y)
        {
            float bw = Measure(g, t, fSmall) + 9f * s;
            float bh = rowH - 4f * s;
            RectangleF r = new RectangleF(x, y + (rowH - bh) / 2f, bw, bh);
            Color txt;
            if (rank >= 6)
            {
                SkinDraw.Rr(g, c.Amber, r, 4f * s);
                txt = c.Bg;
            }
            else if (rank == 5)
            {
                SkinDraw.Rr(g, c.Main, r, 4f * s);
                txt = c.Bg;
            }
            else if (rank == 4)
            {
                SkinDraw.RrLine(g, c.Main, r, 4f * s, 1.2f * s);
                txt = c.Main;
            }
            else
            {
                SkinDraw.RrLine(g, c.Dim, r, 4f * s, 1f * s);
                txt = c.Dim;
            }
            Str(g, t, fSmall, txt, r, StringAlignment.Center);
        }

        private float DrawRow(Graphics g, DetailRow r, float y, bool total)
        {
            float x = pad;
            Font f = total || r.Bold ? fBold : fBody;
            Color col = total ? c.Main : c.Main;
            string[] cells =
            {
                null, r.Model, r.Span, N(r.Msgs), T(r.In), T(r.Out), T(r.Think), T(r.Cw), T(r.Cr), r.Pct5, r.PctW, r.Usd
            };
            for (int i = 0; i < Wid.Length; i++)
            {
                float cw = Wid[i] * s;
                RectangleF cell = new RectangleF(x, y, cw - 4f * s, rowH);
                if (i == 0)
                {
                    if (total)
                    {
                        Str(g, "Total", fBold, c.Main, cell, StringAlignment.Near);
                    }
                    else if (r.Tier.Length > 0 && r.Tier != "Unlabeled")
                    {
                        Badge(g, r.Tier, r.Rank, x, y);
                    }
                    else
                    {
                        Str(g, "Unlabeled", fSmall, c.Dim, cell, StringAlignment.Near);
                    }
                }
                else
                {
                    Str(g, cells[i], f, i == 2 ? c.Dim : col, cell, i >= 3 ? StringAlignment.Far : StringAlignment.Near);
                }
                x += cw;
            }
            return y + rowH;
        }

        private static string N(long v)
        {
            return v.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string T(long v)
        {
            return UsageTracker.FormatTokens(v);
        }

        private float Section(Graphics g, string label, List<DetailRow> rows, bool withTotal, float y)
        {
            Str(g, label, fBold, c.Main, new RectangleF(pad, y, 700f * s, 20f * s), StringAlignment.Near);
            y += 22f * s;
            float x = pad;
            for (int i = 0; i < Wid.Length; i++)
            {
                Str(g, Heads[i], fSmall, c.Dim, new RectangleF(x, y, Wid[i] * s - 4f * s, rowH), i >= 3 ? StringAlignment.Far : StringAlignment.Near);
                x += Wid[i] * s;
            }
            y += rowH;
            using (Pen p = new Pen(c.Track))
            {
                g.DrawLine(p, pad, y, ClientSize.Width - pad, y);
            }
            foreach (DetailRow r in rows)
            {
                y = DrawRow(g, r, y, r.Bold);
            }
            return y + 8f * s;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(c.Bg);
            using (Pen p = new Pen(c.Border))
            {
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            float y = pad;
            Str(g, title, fBold, c.Main, new RectangleF(pad, y, ClientSize.Width - pad * 2 - 30f * s, 20f * s), StringAlignment.Near);
            btnClose = new RectangleF(ClientSize.Width - pad - 22f * s, y, 24f * s, 20f * s);
            Str(g, "×", fBody, c.Dim, btnClose, StringAlignment.Center);
            y += 22f * s;
            Str(g, range + "  Msgs = API messages; Output includes thinking; API equiv. = converted at official list prices, not the quota actually deducted",
                fSmall, c.Dim, new RectangleF(pad, y, ClientSize.Width - pad * 2, 18f * s), StringAlignment.Near);
            y += 22f * s;

            y = Section(g, "Totals by tier", tiers, true, y);
            if (runs != null && runs.Count > 0)
            {
                y = Section(g, "Switch log (in time order; model and tier unchanged within each run)", runs, false, y);
                if (runsHidden > 0)
                {
                    Str(g, "... " + runsHidden + (runsHidden == 1 ? " earlier run not shown" : " earlier runs not shown"), fSmall, c.Dim, new RectangleF(pad, y - 8f * s, 400f * s, rowH), StringAlignment.Near);
                    y += rowH - 8f * s;
                }
            }

            float bw = 92f * s, bh = 24f * s;
            btnDrill = new RectangleF(pad, ClientSize.Height - pad - bh, bw, bh);
            btnCopy = new RectangleF(pad + bw + 8f * s, ClientSize.Height - pad - bh, bw, bh);
            SkinDraw.RrLine(g, c.Dim, btnDrill, 4f * s, 1f * s);
            Str(g, "Session details", fSmall, c.Main, btnDrill, StringAlignment.Center);
            SkinDraw.RrLine(g, c.Dim, btnCopy, 4f * s, 1f * s);
            Str(g, copied ? "Copied" : "Copy as text", fSmall, c.Main, btnCopy, StringAlignment.Center);
        }

        // ------------------------------------------------------------ copy as text (tab-separated; pastes fine into a spreadsheet or notes)
        // When pasted into Excel, text starting with = + - @ is executed as a formula (session titles come from conversation content and may have been written by someone else): put a single quote in front so it is only text
        private static string Safe(string s)
        {
            if (!string.IsNullOrEmpty(s) && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' || s[0] == '\t' || s[0] == '\r'))
            {
                return "'" + s;
            }
            return s ?? "";
        }

        private string AsText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Safe(title) + " " + range);
            AppendTable(sb, "Totals by tier", tiers);
            if (runs != null && runs.Count > 0)
            {
                sb.AppendLine();
                AppendTable(sb, "Switch records", runs);
                if (runsHidden > 0)
                {
                    sb.AppendLine("(" + runsHidden + (runsHidden == 1 ? " earlier run not shown)" : " earlier runs not shown)"));
                }
            }
            return sb.ToString();
        }

        private static void AppendTable(StringBuilder sb, string label, List<DetailRow> rows)
        {
            sb.AppendLine(label);
            sb.AppendLine(string.Join("\t", Heads));
            foreach (DetailRow r in rows)
            {
                sb.AppendLine(string.Join("\t", new string[]
                {
                    Safe(r.Tier), Safe(r.Model), r.Span, r.Msgs.ToString(CultureInfo.InvariantCulture), r.In.ToString(CultureInfo.InvariantCulture),
                    r.Out.ToString(CultureInfo.InvariantCulture), r.Think.ToString(CultureInfo.InvariantCulture),
                    r.Cw.ToString(CultureInfo.InvariantCulture), r.Cr.ToString(CultureInfo.InvariantCulture), r.Pct5, r.PctW, r.Usd
                }));
            }
        }
    }
}
