// QA scenarios for the hover-tip window (TipWindow) and the dialogs: q_tips, q_dialogs.
// Dialogs are shown far off screen, drawn with DrawToBitmap (client area only) at 100% and at a simulated 150% DPI:
// the harness process cannot change the OS DPI, so "150%" = every control font x1.5 while the pixel layout stays the same,
// which is what the real app does (fixed pixel layouts, DPI-aware process, no AutoScale), see the report.

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
        // ------------------------------------------------------------------ hover tips
        private static void CollectTips(Dictionary<string, string> into)
        {
            System.Collections.IList hits = (System.Collections.IList)Get("tipHits");
            foreach (object h in hits)
            {
                Type t = h.GetType();
                string id = (string)t.GetField("Id").GetValue(h);
                string text = (string)t.GetField("Text").GetValue(h);
                Func<string> lazy = (Func<string>)t.GetField("Lazy").GetValue(h);
                if (text == null && lazy != null)
                {
                    try
                    {
                        text = lazy();
                    }
                    catch (Exception ex)
                    {
                        qIssues.Add("EXCEPTION tip " + id + ": " + ex.Message);
                    }
                }
                if (!string.IsNullOrEmpty(text) && !into.ContainsKey(id))
                {
                    into[id] = text;
                }
            }
        }

        private static void QTips(Ds ds, FetchResult fetch)
        {
            foreach (float sc in new float[] { 1.0f, 2.0f })
            {
                Dictionary<string, string> tips = new Dictionary<string, string>();
                // every view in the Everything profile + the simple card
                for (int m = 0; m < 6; m++)
                {
                    QSpec s = new QSpec();
                    s.Skin = 0; s.Data = ds; s.Quotas = fetch; s.Mode = m; s.Profile = 0; s.Mail = true; s.Scale = sc; s.Health = 2; s.Paused = m == 0;
                    s.Caption = "tips " + ViewNames[m];
                    QRender(s).Dispose();
                    CollectTips(tips);
                }
                QSpec simple = new QSpec();
                simple.Skin = 0; simple.Data = ds; simple.Quotas = fetch; simple.Usage = false; simple.Scale = sc; simple.Caption = "tips simple";
                QRender(simple).Dispose();
                CollectTips(tips);

                Color bg = (Color)Call("TipBg"), border = (Color)Call("TipBorder");
                Color fg = (Color)wt.GetField("TextMain", Stat).GetValue(null);
                Font fs = (Font)Get("fSmall");
                float px = Math.Max(12.5f, fs.Size);
                int maxW = (int)(380 * Math.Max(1f, sc));
                List<Cell> cells = new List<Cell>();
                int tooTall = 0, tooWide = 0;
                using (TipWindow tw = new TipWindow())
                {
                    foreach (KeyValuePair<string, string> kv in tips)
                    {
                        Size sz = tw.Prepare(kv.Value, "Microsoft YaHei UI", px, bg, border, fg, maxW);
                        Cell c = new Cell();
                        c.Img = tw.Snapshot(sz);
                        c.Caption = kv.Key + " " + sz.Width + "x" + sz.Height;
                        cells.Add(c);
                        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                        if (sz.Height > wa.Height / 2)
                        {
                            tooTall++;
                            qIssues.Add("TIPTALL " + kv.Key + " height " + sz.Height + " of work area " + wa.Height + " (scale " + sc + ")");
                        }
                        if (sz.Width > maxW + 2 * (int)(px * 0.75) + 4)
                        {
                            tooWide++;
                            qIssues.Add("TIPWIDE " + kv.Key + " width " + sz.Width + " > " + maxW);
                        }
                    }
                }
                // draw in 4 sheets of at most 40 tips each so that they stay readable
                for (int part = 0; part * 40 < cells.Count; part++)
                {
                    int n = Math.Min(40, cells.Count - part * 40);
                    Sheet(cells.GetRange(part * 40, n), sc >= 2f ? 2 : 4, Path.Combine(qOut, "q_tips_x" + sc.ToString(CultureInfo.InvariantCulture) + "_" + part + ".png"), "hover tips, scale " + sc + " (" + tips.Count + " distinct tips)");
                }
                Console.WriteLine("tips at scale " + sc + ": " + tips.Count + " distinct, " + tooTall + " too tall, " + tooWide + " too wide");
            }

            // placement near the screen edges (window kept invisible with Opacity 0)
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size big = new Size(400, 260);
            Size small = new Size(220, 60);
            Point[] pts =
            {
                new Point(area.Left + 2, area.Top + 2), new Point(area.Right - 2, area.Top + 2), new Point(area.Right - 2, area.Bottom - 2), new Point(area.Left + 2, area.Bottom - 2),
                new Point(area.Left + area.Width / 2, area.Top + area.Height / 2), new Point(area.Right - 30, area.Top + area.Height / 2), new Point(area.Left + area.Width / 2, area.Bottom - 10)
            };
            string[] pn = { "top-left", "top-right", "bottom-right", "bottom-left", "center", "right edge", "bottom edge" };
            using (TipWindow tw = new TipWindow())
            {
                tw.Opacity = 0;
                foreach (Size sz in new Size[] { small, big })
                {
                    for (int i = 0; i < pts.Length; i++)
                    {
                        tw.ShowNear(pts[i], sz);
                        Rectangle b = tw.Bounds;
                        bool inside = b.Left >= area.Left && b.Right <= area.Right && b.Top >= area.Top && b.Bottom <= area.Bottom;
                        bool covers = b.Contains(pts[i]);
                        string line = (inside && !covers ? "PASS  " : "FAIL  ") + "tip " + sz.Width + "x" + sz.Height + " near " + pn[i] + " " + pts[i] + " -> " + b + (inside ? "" : "  OUTSIDE work area") + (covers ? "  COVERS the cursor" : "");
                        Console.WriteLine(line);
                        if (!(inside && !covers))
                        {
                            qIssues.Add("TIPPLACE " + line);
                        }
                        tw.Hide();
                    }
                }
            }
        }

        // ------------------------------------------------------------------ dialogs
        private static Bitmap ShotForm(Form f, float fontScale, bool relayout)
        {
            List<Control> all = new List<Control>();
            List<Font> fonts = new List<Font>();
            Stack<Control> st = new Stack<Control>();
            st.Push(f);
            while (st.Count > 0)
            {
                Control c = st.Pop();
                all.Add(c);
                fonts.Add(c.Font);
                foreach (Control k in c.Controls)
                {
                    st.Push(k);
                }
            }
            if (fontScale != 1f)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Font o = fonts[i];
                    all[i].Font = new Font(o.FontFamily, o.Size * fontScale, o.Style, o.Unit);
                }
                if (relayout)
                {
                    f.Scale(new SizeF(fontScale, fontScale));
                }
            }
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-9000, -9000);
            f.Show();
            Application.DoEvents();
            Bitmap full = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(full, new Rectangle(0, 0, f.Width, f.Height));
            int bx = (f.Width - f.ClientSize.Width) / 2, by = f.Height - f.ClientSize.Height - bx;
            Bitmap client = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
            using (Graphics g = Graphics.FromImage(client))
            {
                g.DrawImage(full, -bx, -by);
            }
            full.Dispose();
            return client;
        }

        // a text control whose text does not fit its box is the typical "cut off" bug of a fixed pixel layout
        private static void CheckForm(Form f, string tag)
        {
            Stack<Control> st = new Stack<Control>();
            st.Push(f);
            while (st.Count > 0)
            {
                Control c = st.Pop();
                foreach (Control k in c.Controls)
                {
                    st.Push(k);
                }
                if (c == f || string.IsNullOrEmpty(c.Text) || c is TextBox || c is NumericUpDown || c is ComboBox)
                {
                    continue;
                }
                if (c is Label && ((Label)c).AutoSize)
                {
                    // auto-sized labels grow; what matters is whether they leave the parent
                    Control p = c.Parent;
                    if (p != null && c.Right > p.ClientSize.Width)
                    {
                        qIssues.Add("DIALOG  " + tag + ": label \"" + Cut(c.Text) + "\" sticks out of its parent by " + (c.Right - p.ClientSize.Width) + "px");
                    }
                    continue;
                }
                TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                if (c is CheckBox || c is RadioButton)
                {
                    if (c is RadioButton && ((RadioButton)c).Appearance == Appearance.Button)
                    {
                        Size one = TextRenderer.MeasureText(c.Text, c.Font, new Size(100000, 100000), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                        if (one.Width > c.Width - 8)
                        {
                            qIssues.Add("DIALOG  " + tag + ": \"" + Cut(c.Text) + "\" in a button-like radio needs " + one.Width + "px but has " + c.Width + "px");
                        }
                        continue;
                    }
                    Size need = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(10, c.Width - 18), 100000), fl);
                    if (need.Height > c.Height + 1)
                    {
                        qIssues.Add("DIALOG  " + tag + ": \"" + Cut(c.Text) + "\" in " + c.GetType().Name + " needs " + need.Height + "px height (wrapped) but has " + c.Height + "px, width " + c.Width);
                    }
                }
                else if (c is Button)
                {
                    Size one = TextRenderer.MeasureText(c.Text, c.Font, new Size(100000, 100000), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    if (one.Width > c.Width - 8)
                    {
                        qIssues.Add("DIALOG  " + tag + ": button \"" + Cut(c.Text) + "\" needs " + one.Width + "px but is " + c.Width + "px wide");
                    }
                }
                else if (c is Label)
                {
                    Size need = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(10, c.Width), 100000), fl);
                    if (need.Height > c.Height + 2)
                    {
                        qIssues.Add("DIALOG  " + tag + ": label \"" + Cut(c.Text) + "\" needs " + need.Height + "px height but has " + c.Height + "px");
                    }
                }
            }
            // anything that leaves the client area
            Stack<Control> s2 = new Stack<Control>();
            s2.Push(f);
            while (s2.Count > 0)
            {
                Control c = s2.Pop();
                foreach (Control k in c.Controls)
                {
                    s2.Push(k);
                    if (!(c is ScrollableControl && ((ScrollableControl)c).AutoScroll) && (k.Right > c.ClientSize.Width + 1 || k.Bottom > c.ClientSize.Height + 1) && !(k is Form))
                    {
                        qIssues.Add("DIALOG  " + tag + ": " + k.GetType().Name + " \"" + Cut(k.Text) + "\" leaves its container (" + k.Bounds + " in " + c.ClientSize + ")");
                    }
                }
            }
        }

        private static string Cut(string t)
        {
            t = t.Replace("\r", " ").Replace("\n", " ");
            return t.Length > 40 ? t.Substring(0, 40) + "~" : t;
        }

        private delegate Form MakeForm();

        private static void AddDialog(List<Cell> list, string name, MakeForm make, float fontScale, bool relayout)
        {
            Form f = null;
            try
            {
                f = make();
                // a form that sets AutoScaleMode.Dpi (DialogDpi.Apply) really is scaled by WinForms at 150%; the others keep their fixed pixel layout
                bool real = relayout || f.AutoScaleMode == AutoScaleMode.Dpi;
                string tag = name + " @" + (int)(fontScale * 100) + "%" + (relayout ? " if autoscaled" : real && fontScale != 1f ? " (autoscaled)" : "");
                Bitmap b = ShotForm(f, fontScale, real);
                // CheckForm needs the shown layout
                CheckForm(f, tag);
                Cell c = new Cell();
                c.Img = b;
                c.Caption = tag + " " + b.Width + "x" + b.Height;
                list.Add(c);
            }
            catch (Exception ex)
            {
                qIssues.Add("EXCEPTION dialog " + name + ": " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                if (f != null)
                {
                    f.Dispose();
                }
            }
        }

        private static void QDialogs(Ds ds)
        {
            int[] f1 = Feat.Preset(1);
            int[] f0 = Feat.Preset(0);
            List<MailSessionRow> mrows = new List<MailSessionRow>();
            string[] mt = Titles;
            for (int i = 0; i < 7; i++)
            {
                MailSessionRow r = new MailSessionRow();
                r.Session = "s" + i;
                r.Title = i == 3 ? LongNoSpace : (mt[i % mt.Length] ?? "demo-app · " + i);
                r.State = i == 0 ? "running" : i == 1 ? "waiting 22 min" : i == 2 ? "47 min left" : i == 3 ? "18 min left" : "expired";
                r.TtlMin = i % 2 == 0 ? 60 : 5;
                r.Live = i != 6;
                mrows.Add(r);
            }
            MailStore.Host = "smtp.example.com";
            MailStore.User = "demo@example.com";
            MailStore.To = "demo@example.com";
            MailStore.Enabled = true;
            MailWatch mw = new MailWatch();
            mw.Session = "s2";
            mw.Title = "x";
            mw.Mins.Add(30);
            mw.Mins.Add(10);
            MailStore.Watches.Clear();
            MailStore.Watches.Add(mw);
            string[] tabLabels = (string[])wt.GetField("TabLabels", Stat).GetValue(null);

            DateTime nowL = DateTime.Now;
            PopupColors pc = new PopupColors();
            pc.Bg = Color.FromArgb(250, 250, 247);
            pc.Border = Color.FromArgb(120, 120, 120);
            pc.Main = Color.FromArgb(30, 30, 30);
            pc.Dim = Color.FromArgb(120, 120, 120);
            pc.Track = Color.FromArgb(210, 210, 205);
            pc.Amber = Color.FromArgb(202, 138, 4);

            foreach (float fs in new float[] { 1.0f, 1.5f })
            {
                for (int mode = 0; mode < (fs > 1f ? 2 : 1); mode++)
                {
                    bool relayout = mode == 1;
                    List<Cell> cells = new List<Cell>();
                    AddDialog(cells, "FeatureForm-rec", delegate { return new FeatureForm(1, f1[0], f1[1], f1[2], f1[3]); }, fs, relayout);
                    AddDialog(cells, "FeatureForm-every", delegate { return new FeatureForm(0, f0[0], f0[1], f0[2], f0[3]); }, fs, relayout);
                    AddDialog(cells, "MailForm", delegate { return new MailForm(mrows); }, fs, relayout);
                    AddDialog(cells, "MinutesForm", delegate { return new MinutesForm(15); }, fs, relayout);
                    AddDialog(cells, "TabSettingsForm", delegate { return new TabSettingsForm(1258, tabLabels); }, fs, relayout);
                    AddDialog(cells, "CustomRangeForm", delegate { return new CustomRangeForm(nowL.AddDays(-1), nowL); }, fs, relayout);
                    AddDialog(cells, "SettingsForm", delegate { return new SettingsForm(delegate { }); }, fs, relayout);
                    QSheet(cells, 3, "q_dialogs_" + (int)(fs * 100) + (relayout ? "_autoscale" : ""), "dialogs @" + (int)(fs * 100) + "%" + (relayout ? " with Scale(): what AutoScaleMode.Dpi would give" : ""));
                }
            }

            // the details window (model view -> click a session): owner-drawn, takes the widget scale itself
            List<Cell> dc = new List<Cell>();
            foreach (float sc in new float[] { 1.0f, 1.5f, 2.0f })
            {
                List<DetailRow> tiers = new List<DetailRow>();
                List<DetailRow> runs = new List<DetailRow>();
                string[] tn = { "max", "xhigh", "high", "medium", "low", "ultra+fast" };
                for (int i = 0; i < tn.Length; i++)
                {
                    DetailRow r = new DetailRow();
                    r.Tier = tn[i]; r.Rank = i < 5 ? 5 - i : 7; r.Model = i % 2 == 0 ? "Opus 5.5" : "Sonnet 5.5 · sub"; r.Span = "21:" + (10 + i) + "–" + "22:5" + i;
                    r.Msgs = 12345 + i; r.In = 1234567; r.Out = 345678; r.Think = 99999; r.Cw = 7654321; r.Cr = 123456789;
                    r.Pct5 = "12.5%"; r.PctW = "1.82%"; r.Usd = "$123.45";
                    tiers.Add(r);
                    runs.Add(r);
                }
                DetailRow total = new DetailRow();
                total.Bold = true; total.Msgs = 99999; total.In = 12345678; total.Out = 2345678; total.Think = 999999; total.Cw = 87654321; total.Cr = 1234567890; total.Pct5 = "88.8%"; total.PctW = "12.34%"; total.Usd = "$1,234";
                tiers.Add(total);
                float sc2 = sc;
                AddDialog(dc, "DetailPopup", delegate
                {
                    return new DetailPopup(LongNoSpace, "21:00–22:56", tiers, runs, 17, pc, "Microsoft YaHei UI", sc2, delegate { });
                }, 1f, false);
            }
            QSheet(dc, 1, "q_dialogs_details", "details popup at widget scale 1.0 / 1.5 / 2.0");
        }
    }
}
