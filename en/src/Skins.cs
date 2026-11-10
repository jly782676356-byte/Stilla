using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Stilla
{
    // Skins: each skin draws its own compact card and tabs; the table, data, hotkeys and settings are shared
    // Colors: every skin has a paper (light) and an ink (dark) set, and the light/dark switch swaps the set

    internal sealed class SkinPalette
    {
        public Color Bg, Surface, Border, Main, Dim, Track, Accent, Ok, Warn, Bad;
    }

    internal sealed class Fonts
    {
        public Font Title, Label, Small, Display, Big;
    }

    // Data needed by the compact card (all already strings that can be displayed directly)
    internal sealed class CompactData
    {
        public bool Has5, HasW;
        public double P5, PW;
        public string Clock5 = "—", Left5 = "", ClockW = "—", LeftW = "";   // Reset time / time left
        public string Foot = "", Updated = "";
        public Color FootColor;
        public bool Alert;                                  // A notice at the bottom that people should see (no data, error, stale data): skins that draw their own footer must also show Foot
        public string Source = "every 60 s", SourceEn = "EVERY 60S";   // Where the data came from: skins that draw their own footer splice it into the text
    }

    // Data needed by a time tab
    internal sealed class TabData
    {
        public int Index;
        public string Label, Value, Roman;
        public double Fill;             // Size of the usage (0-1, logarithmic scale)
        public bool Selected;
        public float LabelScale = 1f;   // Shrunk when there are too many tabs to fit
    }

    internal abstract class Skin
    {
        public abstract string Name { get; }
        public abstract int CompactH { get; }                       // Height of the compact card (base size, before scaling)
        public virtual float ShareWidth { get { return 11f; } }     // Width of the small share graphic (base size)
        public virtual bool ShowAccountChip { get { return true; } }   // The account chip at the top right of the compact card; turned off for skins whose top is filled by their own big number / title
        public virtual string UiFamily { get { return "Microsoft YaHei UI"; } }
        public virtual string DisplayFamily { get { return "Segoe UI Black"; } }
        public virtual FontStyle DisplayStyle { get { return FontStyle.Italic; } }

        public abstract SkinPalette Palette(bool dark);

        // Position of the "new version" red dot (center, physical pixels): to the right of the Stilla mark / title text. Default style: ink-drop icon + title
        public virtual PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            float pad = 14f * s;
            return new PointF(SkinDraw.TextEnd(g, "STILLA", f.Display, pad + 24f * s) + 10f * s, 5f * s + 11f * s);
        }

        protected static Color Lv(SkinPalette p, double pct)
        {
            return SkinDraw.Lv(p, pct);
        }

        protected static string Num(double pct)
        {
            return SkinDraw.Num(pct);
        }

        // Draw the compact card (title row, the two quotas, the bottom). close / refresh are the positions of "x" and "Refresh", which must stay clickable
        public abstract void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w);

        // Draw one time tab. cell: the extent of this slot (66 base units high, counted from where the tabs start)
        public abstract void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s);

        // Background and border of the panel (rect: the extent of the whole panel, height UsagePanelH)
        public virtual void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s) { }

        // Panel title (top left), by default the ink-drop icon + text
        public virtual void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            InkPainter.DrawLogo(g, x, y + 1f * s, s, p.Main);
            SkinDraw.Text(g, text, f.Display, p.Main, new RectangleF(x + 24f * s, y - 1f * s, 160f * s, 22f * s), StringAlignment.Near);
        }

        // The three view buttons of the header (session / model / compare). rects holds each button's position, sel is the currently selected one
        public virtual void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            RectangleF all = RectangleF.Union(rects[0], rects[rects.Length - 1]);
            SkinDraw.Rr(g, p.Track, new RectangleF(all.X - 2f * s, all.Y - 1f * s, all.Width + 4f * s, all.Height + 2f * s), 9f * s);
            for (int i = 0; i < rects.Length; i++)
            {
                bool on = i == sel;
                if (on)
                {
                    SkinDraw.Rr(g, p.Main, rects[i], 9f * s);
                }
                SkinDraw.Fit(g, names[i], f.Small, on ? p.Bg : p.Dim, rects[i], StringAlignment.Center);
            }
        }

        // Small share graphic: x, y is the top-left corner, width ShareWidth, height 11 (base units); fill = 0-1
        public virtual void DrawShare(Graphics g, float x, float y, double share, SkinPalette p, float s)
        {
            InkPainter.DrawCup(g, new RectangleF(x, y, 11f * s, 11f * s), share, false, s, p.Main, p.Bg);
        }

        // Common helper for drawing tabs: the tab font size shrinks by LabelScale
        protected static Font ShrinkFont(Font f, float k)
        {
            return new Font(f.FontFamily, f.Size * k, f.Style, f.Unit);
        }

        // One row of the compact card: 5H / 7D and the name on the left, reset time and time left on the right
        protected static void QuotaRow(Graphics g, Fonts f, SkinPalette p, string tag, string label, string clock, string left, Color tagColor, float pad, float w, float y, float s)
        {
            float inner = w - 2f * pad;
            SkinDraw.Text(g, tag, f.Display, tagColor, new RectangleF(pad, y - 2f * s, 30f * s, 18f * s), StringAlignment.Near);
            SkinDraw.Text(g, label, f.Small, p.Main, new RectangleF(pad + 30f * s, y, inner * 0.42f, 16f * s), StringAlignment.Near);
            string right = left.Length > 0 ? clock + " · " + left : clock;
            SkinDraw.Text(g, right, f.Small, p.Dim, new RectangleF(pad + inner * 0.4f, y, inner * 0.6f, 16f * s), StringAlignment.Far);
        }

        // Title row of the compact card: ink-drop icon + STILLA, with "Refresh" and "x" on the right (default style)
        protected static void DefaultHeader(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float pad)
        {
            InkPainter.DrawLogo(g, pad, 7f * s, s, p.Main);
            SkinDraw.Text(g, "STILLA", f.Display, p.Main, new RectangleF(pad + 24f * s, 5f * s, 140f * s, 22f * s), StringAlignment.Near);
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "Reload", f.Small, p.Dim, refresh, StringAlignment.Center);
        }
    }

    // Drawing helpers: text, rounded rectangles, dithering, ink-drop field, label boxes
    internal static class SkinDraw
    {
        public static int Shadow;       // When the background is transparent the text needs a faint dark drop shadow (same as DrawTextRect)

        public static Color C(int r, int g, int b)
        {
            return Color.FromArgb(r, g, b);
        }

        public static Color A(int a, Color c)
        {
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Lv(SkinPalette p, double pct)
        {
            if (pct >= 85.0)
            {
                return p.Bad;
            }
            return pct >= 60.0 ? p.Warn : p.Ok;
        }

        public static string Num(double p)
        {
            return Math.Round(p).ToString("0", CultureInfo.InvariantCulture);
        }

        public static Font F(string family, float px, FontStyle st)
        {
            return new Font(family, px, st, GraphicsUnit.Pixel);
        }

        // Where a piece of left-aligned text (drawn from x) actually ends: used to put the red dot right next to the title text
        public static float TextEnd(Graphics g, string text, Font font, float x)
        {
            using (StringFormat sf = new StringFormat())
            {
                sf.FormatFlags = StringFormatFlags.NoWrap;
                return x + font.Size / 6f + g.MeasureString(text, font, 100000, sf).Width * 0.9f;
            }
        }

        public static void Text(Graphics g, string text, Font font, Color c, RectangleF r, StringAlignment a)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = a;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                if (Shadow >= 12)
                {
                    RectangleF r2 = r;
                    r2.Offset(1f, 1f);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(Shadow, 0, 0, 0)))
                    {
                        g.DrawString(text, font, sb, r2, sf);
                    }
                }
                using (SolidBrush b = new SolidBrush(c))
                {
                    g.DrawString(text, font, b, r, sf);
                }
            }
        }

        // When it does not fit shrink the font first (down to 60%), and only if it still does not fit use an ellipsis: so "100%" does not become "1..." and "Not started" does not become "Not..."
        public static void Fit(Graphics g, string text, Font font, Color c, RectangleF r, StringAlignment a)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            float need;
            using (StringFormat sf = new StringFormat())
            {
                sf.FormatFlags = StringFormatFlags.NoWrap;
                need = g.MeasureString(text, font, 100000, sf).Width;
            }
            if (need <= r.Width || r.Width <= 0f)
            {
                Text(g, text, font, c, r, a);
                return;
            }
            float k = Math.Max(0.6f, r.Width / need * 0.97f);
            using (Font small = new Font(font.FontFamily, font.Size * k, font.Style, font.Unit))
            {
                Text(g, text, small, c, r, a);
            }
        }

        // The same number: the top half in the top color, the bottom half in the bottom color (used for "white on ink, black outside the ink")
        public static void SplitText(Graphics g, string text, Font font, Color top, Color bottom, float cx, float cy, float yLine)
        {
            RectangleF r = new RectangleF(cx - 60f, cy - 20f, 120f, 40f);
            GraphicsState st = g.Save();
            g.SetClip(new RectangleF(r.X - 2f, r.Y - 2f, r.Width + 4f, yLine - r.Y + 2f));
            Text(g, text, font, top, r, StringAlignment.Center);
            g.Restore(st);
            st = g.Save();
            g.SetClip(new RectangleF(r.X - 2f, yLine, r.Width + 4f, r.Bottom - yLine + 4f));
            Text(g, text, font, bottom, r, StringAlignment.Center);
            g.Restore(st);
        }

        public static GraphicsPath Path(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (rad <= 0f)
            {
                p.AddRectangle(r);
                return p;
            }
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Rr(Graphics g, Color c, RectangleF r, float rad)
        {
            if (r.Width <= 0f || r.Height <= 0f)
            {
                return;
            }
            using (GraphicsPath p = Path(r, rad))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, p);
            }
        }

        public static void RrLine(Graphics g, Color c, RectangleF r, float rad, float width)
        {
            if (r.Width <= 0f || r.Height <= 0f)
            {
                return;
            }
            using (GraphicsPath p = Path(r, rad))
            using (Pen pen = new Pen(c, width))
            {
                g.DrawPath(pen, p);
            }
        }

        // Label box: fill + thin border + text
        public static RectangleF Tag(Graphics g, string text, Font font, Color ink, Color paper, float x, float y, float s)
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

        // Thresholds of the 4x4 ordered dither (normalized to 0-1)
        private static readonly float[,] Bayer = BuildBayer();

        private static float[,] BuildBayer()
        {
            int[,] b = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
            float[,] r = new float[4, 4];
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    r[y, x] = (b[y, x] + 0.5f) / 16f;
                }
            }
            return r;
        }

        // Dither bar: cells within pct use the on color, sparse dots outside use the dim color
        public static void DitherBar(Graphics g, RectangleF r, double pct, Color on, Color dim, float cell)
        {
            int cols = Math.Max(1, (int)(r.Width / cell));
            int rows = Math.Max(1, (int)(r.Height / cell));
            int edge = Math.Max(2, (int)Math.Round(cols * Math.Max(0.0, Math.Min(100.0, pct)) / 100.0));
            using (SolidBrush bo = new SolidBrush(on))
            using (SolidBrush bd = new SolidBrush(dim))
            {
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        float t = x >= edge - 1 && x < edge ? 1f : x < edge ? 0.38f + 0.62f * x / edge : 0.09f;
                        if (t > Bayer[y & 3, x & 3])
                        {
                            g.FillRectangle(x < edge ? bo : bd, r.X + x * cell, r.Y + y * cell, cell, cell);
                        }
                    }
                }
            }
        }

        // Dither ball: light comes from the top left
        public static void DitherOrb(Graphics g, RectangleF r, Color on, float cell)
        {
            int n = Math.Max(4, (int)(r.Width / cell));
            float R = n / 2f;
            float lx = -0.5f, ly = -0.6f, lz = 0.62f;
            float ln = (float)Math.Sqrt(lx * lx + ly * ly + lz * lz);
            using (SolidBrush b = new SolidBrush(on))
            {
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f - R) / R, dy = (y + 0.5f - R) / R;
                        float d2 = dx * dx + dy * dy;
                        if (d2 > 1f)
                        {
                            continue;
                        }
                        float dz = (float)Math.Sqrt(1f - d2);
                        float bright = Math.Max(0f, (dx * lx + dy * ly + dz * lz) / ln);
                        if (0.1f + 0.9f * bright > Bayer[y & 3, x & 3])
                        {
                            g.FillRectangle(b, r.X + x * cell, r.Y + y * cell, cell, cell);
                        }
                    }
                }
            }
        }

        // Ink-drop field (two circles): anti-aliased edge, the same algorithm as InkPainter.DrawDiagram, but the colors and position are decided by the caller
        private static Bitmap BuildField(int W, int H, float ax, float ay, float ra, float bx, float by, float rb, Color ink)
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

        // Two drops of ink (merged): box is the area, ra / rb are the radii of the two circles (already converted by area)
        public static void Blobs(Graphics g, RectangleF box, double p5, double pw, float R, Color ink, Color paper, float s, Font numFont, string t5, string tw, float ax, float bx)
        {
            int W = Math.Max(2, (int)Math.Ceiling(box.Width));
            int H = Math.Max(2, (int)Math.Ceiling(box.Height));
            float cy = box.Height / 2f;
            float ra = R * (float)Math.Sqrt(Math.Max(0.0, Math.Min(100.0, p5)) / 100.0);
            float rb = R * (float)Math.Sqrt(Math.Max(0.0, Math.Min(100.0, pw)) / 100.0);
            using (Bitmap bmp = BuildField(W, H, ax, cy, ra, bx, cy, rb, ink))
            {
                g.DrawImage(bmp, box.X, box.Y, W, H);
            }
            Text(g, t5, numFont, ra >= 13f * s ? paper : ink, new RectangleF(box.X + ax - 40f, box.Y + cy - 16f, 80f, 32f), StringAlignment.Center);
            Text(g, tw, numFont, rb >= 13f * s ? paper : ink, new RectangleF(box.X + bx - 40f, box.Y + cy - 16f, 80f, 32f), StringAlignment.Center);
        }
    }

    // ---------------------------------------------------------------- Colors

    internal static class SkinPalettes
    {
        // Default: paper background + ink text; after the light/dark switch it is an ink background + paper text
        public static SkinPalette Ink(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(14, 14, 14), Surface = SkinDraw.C(14, 14, 14), Border = SkinDraw.C(70, 70, 68),
                    Main = SkinDraw.C(250, 250, 248), Dim = SkinDraw.C(150, 150, 146), Track = SkinDraw.C(46, 46, 44),
                    Accent = SkinDraw.C(232, 72, 28), Ok = SkinDraw.C(250, 250, 248), Warn = SkinDraw.C(232, 72, 28), Bad = SkinDraw.C(255, 96, 80)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(250, 250, 248), Surface = SkinDraw.C(250, 250, 248), Border = SkinDraw.C(217, 217, 212),
                Main = SkinDraw.C(14, 14, 14), Dim = SkinDraw.C(122, 122, 118), Track = SkinDraw.C(228, 228, 224),
                Accent = SkinDraw.C(232, 72, 28), Ok = SkinDraw.C(14, 14, 14), Warn = SkinDraw.C(232, 72, 28), Bad = SkinDraw.C(184, 32, 30)
            };
        }
    }

    // ---------------------------------------------------------------- Combo (current version): ink-drop double circle + liquid cups

    internal class SkinCurrent : Skin
    {
        public override string Name { get { return "Combo (default)"; } }
        public override int CompactH { get { return 224; } }
        public override SkinPalette Palette(bool dark) { return SkinPalettes.Ink(dark); }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            DefaultHeader(g, d, f, p, close, refresh, s, pad);
            RectangleF box = new RectangleF(pad, 34f * s, w - 2f * pad, 130f * s);
            InkPainter.DrawDiagram(g, box, d.P5, d.PW, s, p.Main, p.Bg, p.Track, Lv(p, d.P5), Lv(p, d.PW), f.Big, f.Small,
                d.Has5 ? Num(d.P5) : "—", d.HasW ? Num(d.PW) : "—");
            float rowY = box.Bottom + 6f * s;
            QuotaRow(g, f, p, "5H", "5-hour quota", d.Clock5, d.Left5, Lv(p, d.P5), pad, w, rowY, s);
            QuotaRow(g, f, p, "7D", "Weekly quota", d.ClockW, d.LeftW, Lv(p, d.PW), pad, w, rowY + 16f * s, s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, rowY + 34f * s, w - 2f * pad, 16f * s), StringAlignment.Near);
        }

        public override void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s)
        {
            using (Pen pen = new Pen(p.Track))
            {
                g.DrawLine(pen, rect.X + 14f * s, rect.Y + 2f * s, rect.Right - 14f * s, rect.Y + 2f * s);
            }
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float D = 26f * s;
            float y0 = cell.Y + 3f * s;
            float cx = cell.X + cell.Width / 2f;
            InkPainter.DrawCup(g, new RectangleF(cx - D / 2f, y0, D, D), t.Fill, t.Selected, s, p.Main, p.Bg);
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + D + 4f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            SkinDraw.Fit(g, t.Value, f.Label, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + D + 18f * s, cell.Width, 16f * s), StringAlignment.Center);
        }
    }

    // ---------------------------------------------------------------- Mix 1: nested ink drop (compact card) + circle on a line (tab)

    internal sealed class SkinMix1 : SkinCurrent
    {
        public override string Name { get { return "Mix 1 Nested Ink"; } }
        public override float ShareWidth { get { return 40f; } }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float y0 = cell.Y + 3f * s;
            float lineY = y0 + 13f * s;
            float cx = cell.X + cell.Width / 2f;
            using (Pen line = new Pen(SkinDraw.A(120, p.Main), 1f * s))
            {
                g.DrawLine(line, cell.X, lineY, cell.Right, lineY);
            }
            float d = (8f + 18f * (float)t.Fill) * s;
            RectangleF r = new RectangleF(cx - d / 2f, lineY - d / 2f, d, d);
            using (SolidBrush fill = new SolidBrush(t.Selected ? p.Main : p.Bg))
            {
                g.FillEllipse(fill, r);
            }
            using (Pen ring = new Pen(p.Main, 1f * s))
            {
                g.DrawEllipse(ring, r);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 26f * s + 4f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            SkinDraw.Fit(g, t.Value, f.Label, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 26f * s + 18f * s, cell.Width, 16f * s), StringAlignment.Center);
        }

        // Share: a line + a dot, with the dot at the end of the line
        public override void DrawShare(Graphics g, float x, float y, double share, SkinPalette p, float s)
        {
            float len = 40f * s * (float)Math.Max(0.0, Math.Min(1.0, share));
            float cy = y + 5.5f * s;
            using (Pen line = new Pen(p.Main, 1f * s))
            {
                g.DrawLine(line, x, cy, x + len, cy);
            }
            float r = 4f * s;
            using (SolidBrush b = new SolidBrush(p.Main))
            {
                g.FillEllipse(b, x + len - r, cy - r, 2f * r, 2f * r);
            }
        }
    }

    // ---------------------------------------------------------------- Mix 2: liquid ring (compact card) + liquid cup (tab)

    internal sealed class SkinMix2 : SkinCurrent
    {
        public override string Name { get { return "Mix 2 Liquid Rings"; } }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            DefaultHeader(g, d, f, p, close, refresh, s, pad);
            RectangleF box = new RectangleF(pad, 34f * s, w - 2f * pad, 130f * s);
            float cy = box.Y + 65f * s;
            float R = 48f * s;
            float[] cxs = { box.X + 76f * s, box.X + 160f * s };
            double[] ps = { d.P5, d.PW };
            string[] ts = { d.Has5 ? Num(d.P5) : "—", d.HasW ? Num(d.PW) : "—" };
            string[] tags = { "5 h", "Weekly" };
            for (int i = 0; i < 2; i++)
            {
                float cx = cxs[i];
                double pct = Math.Max(0.0, Math.Min(100.0, ps[i]));
                float surf = cy + R - 2f * R * (float)(pct / 100.0);
                Color col = Lv(p, ps[i]);
                GraphicsState st = g.Save();
                using (GraphicsPath cp = new GraphicsPath())
                {
                    cp.AddEllipse(cx - R, cy - R, 2f * R, 2f * R);
                    g.SetClip(cp);
                }
                using (SolidBrush ink = new SolidBrush(p.Main))
                {
                    g.FillRectangle(ink, cx - R, surf, 2f * R, cy + R - surf + 1f);
                }
                g.Restore(st);
                SkinDraw.SplitText(g, ts[i], f.Big, p.Main, p.Bg, cx, cy + 6f * s, surf);
                using (Pen ring = new Pen(col, 1f * s))
                {
                    g.DrawEllipse(ring, cx - R, cy - R, 2f * R, 2f * R);
                }
                int side = i == 0 ? -1 : 1;
                using (Pen tick = new Pen(col, 1f * s))
                {
                    for (int k = 0; k <= 10; k++)
                    {
                        float ty = cy + R - 2f * R * k / 10f;
                        float x1 = cx + side * (R + 3f * s);
                        float x2 = x1 + side * (k % 5 == 0 ? 7f : 4f) * s;
                        g.DrawLine(tick, x1, ty, x2, ty);
                    }
                }
            }
            SkinDraw.Tag(g, tags[0], f.Small, p.Main, p.Bg, box.X + 6f * s, box.Y + 6f * s, s);
            float wTag = g.MeasureString(tags[1], f.Small).Width + 8f * s;
            SkinDraw.Tag(g, tags[1], f.Small, p.Main, p.Bg, box.Right - 6f * s - wTag, box.Y + 6f * s, s);
            float rowY = box.Bottom + 6f * s;
            QuotaRow(g, f, p, "5H", "5-hour quota", d.Clock5, d.Left5, Lv(p, d.P5), pad, w, rowY, s);
            QuotaRow(g, f, p, "7D", "Weekly quota", d.ClockW, d.LeftW, Lv(p, d.PW), pad, w, rowY + 16f * s, s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, rowY + 34f * s, w - 2f * pad, 16f * s), StringAlignment.Near);
        }
    }

    // ---------------------------------------------------------------- Mix 3: orbiting ink dots

    internal sealed class SkinMix3 : SkinCurrent
    {
        public override string Name { get { return "Mix 3 Orbit Dots"; } }
        public override float ShareWidth { get { return 16f; } }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            DefaultHeader(g, d, f, p, close, refresh, s, pad);
            RectangleF box = new RectangleF(pad, 34f * s, w - 2f * pad, 130f * s);
            float cx = box.X + box.Width / 2f, cy = box.Y + 65f * s;
            float rIn = 40f * s, rOut = 58f * s;
            using (Pen tk = new Pen(SkinDraw.A(110, p.Main), 1f * s))
            {
                for (int i = 0; i < 40; i++)
                {
                    double t = i / 40.0 * 2 * Math.PI;
                    float r0 = rOut + 5f * s, r1 = rOut + (i % 10 == 0 ? 11f : 7f) * s;
                    g.DrawLine(tk, cx + r0 * (float)Math.Sin(t), cy - r0 * (float)Math.Cos(t), cx + r1 * (float)Math.Sin(t), cy - r1 * (float)Math.Cos(t));
                }
            }
            DrawOrbit(g, cx, cy, rIn, Math.Max(0.0, Math.Min(99.5, d.P5)), Lv(p, d.P5), p, s);
            DrawOrbit(g, cx, cy, rOut, Math.Max(0.0, Math.Min(99.5, d.PW)), Lv(p, d.PW), p, s);
            SkinDraw.Text(g, d.Has5 ? Num(d.P5) + "%" : "—", f.Big, p.Main, new RectangleF(cx - 40f * s, cy - 16f * s, 80f * s, 18f * s), StringAlignment.Center);
            SkinDraw.Text(g, d.HasW ? Num(d.PW) + "%" : "—", f.Big, p.Main, new RectangleF(cx - 40f * s, cy + 2f * s, 80f * s, 18f * s), StringAlignment.Center);
            SkinDraw.Tag(g, "5H", f.Small, p.Main, p.Bg, cx - 12f * s, cy - rIn - 9f * s, s);
            SkinDraw.Tag(g, "7D", f.Small, p.Main, p.Bg, cx - 12f * s, cy - rOut - 9f * s, s);
            float rowY = box.Bottom + 6f * s;
            QuotaRow(g, f, p, "5H", "5-hour quota", d.Clock5, d.Left5, Lv(p, d.P5), pad, w, rowY, s);
            QuotaRow(g, f, p, "7D", "Weekly quota", d.ClockW, d.LeftW, Lv(p, d.PW), pad, w, rowY + 16f * s, s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, rowY + 34f * s, w - 2f * pad, 16f * s), StringAlignment.Near);
        }

        private static void DrawOrbit(Graphics g, float cx, float cy, float r, double pct, Color col, SkinPalette p, float s)
        {
            using (Pen ring = new Pen(col, 1f * s))
            {
                g.DrawEllipse(ring, cx - r, cy - r, 2f * r, 2f * r);
            }
            float sweep = (float)(pct * 3.6);
            if (sweep > 0.5f)
            {
                using (Pen arc = new Pen(p.Main, 7f * s))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, cx - r, cy - r, 2f * r, 2f * r, -90f, sweep);
                }
                double t = (pct / 100.0) * 2 * Math.PI;
                float ex = cx + r * (float)Math.Sin(t), ey = cy - r * (float)Math.Cos(t);
                using (SolidBrush b = new SolidBrush(p.Main))
                {
                    g.FillEllipse(b, ex - 5f * s, ey - 5f * s, 10f * s, 10f * s);
                }
            }
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float D = 30f * s;
            float y0 = cell.Y + 3f * s;
            float cx = cell.X + cell.Width / 2f;
            RectangleF r = new RectangleF(cx - D / 2f, y0, D, D);
            using (Pen ring = new Pen(p.Main, (t.Selected ? 2f : 1f) * s))
            {
                g.DrawEllipse(ring, r);
            }
            float dot = (10f + 24f * (float)t.Fill) * 0.76f * s;
            using (SolidBrush b = new SolidBrush(p.Main))
            {
                g.FillEllipse(b, cx - dot / 2f, y0 + D / 2f - dot / 2f, dot, dot);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + D + 4f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            SkinDraw.Fit(g, t.Value, f.Label, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + D + 18f * s, cell.Width, 16f * s), StringAlignment.Center);
        }

        public override void DrawShare(Graphics g, float x, float y, double share, SkinPalette p, float s)
        {
            RectangleF r = new RectangleF(x, y, 16f * s, 16f * s);
            using (Pen ring = new Pen(p.Main, 1f * s))
            {
                g.DrawEllipse(ring, r);
            }
            float dot = (2f + 12f * (float)share) * s;
            using (SolidBrush b = new SolidBrush(p.Main))
            {
                g.FillEllipse(b, r.X + 8f * s - dot / 2f, r.Y + 8f * s - dot / 2f, dot, dot);
            }
        }
    }

    // ---------------------------------------------------------------- Mix 4: ruler ink drop (no table rows)

    internal sealed class SkinMix4 : SkinCurrent
    {
        public override string Name { get { return "Mix 4 Ruler Ink"; } }
        public override int CompactH { get { return 220; } }
        public override float ShareWidth { get { return 40f; } }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            DefaultHeader(g, d, f, p, close, refresh, s, pad);
            RectangleF box = new RectangleF(pad, 34f * s, w - 2f * pad, 150f * s);
            Ruler(g, box, 50f, d.P5, d.Has5, "5H quota", d.Clock5, d.Left5, Lv(p, d.P5), p, f, s);
            Ruler(g, box, 124f, d.PW, d.HasW, "7D quota", d.ClockW, d.LeftW, Lv(p, d.PW), p, f, s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, box.Bottom + 8f * s, w - 2f * pad, 16f * s), StringAlignment.Near);
        }

        private static void Ruler(Graphics g, RectangleF box, float yOff, double pct, bool has, string label, string clock, string left, Color col, SkinPalette p, Fonts f, float s)
        {
            float y = box.Y + yOff * s;
            float x0 = box.X + 18f * s, len = box.Width - 36f * s;
            using (Pen line = new Pen(col, 1f * s))
            {
                g.DrawLine(line, x0, y, x0 + len, y);
                for (int k = 0; k <= 10; k++)
                {
                    float tx = x0 + len * k / 10f;
                    g.DrawLine(line, tx, y, tx, y + (k % 5 == 0 ? 8f : 4f) * s);
                    if (k % 5 == 0)
                    {
                        SkinDraw.Text(g, (k * 10).ToString(CultureInfo.InvariantCulture), f.Small, p.Dim, new RectangleF(tx - 17f * s, y + 11f * s, 34f * s, 14f * s), StringAlignment.Center);
                    }
                }
            }
            double pc = Math.Max(0.0, Math.Min(100.0, pct));
            float pos = x0 + len * (float)(pc / 100.0);
            float R = 11f * s;
            int n = 24;
            List<PointF> top = new List<PointF>(), bot = new List<PointF>();
            for (int j = 0; j <= n; j++)
            {
                float t = (float)j / n;
                float x = x0 + (pos - x0) * t;
                float wdt = (1.2f * s) + (R - 1.2f * s) * (float)Math.Pow(t, 1.8);
                top.Add(new PointF(x, y - wdt));
                bot.Insert(0, new PointF(x, y + wdt));
            }
            top.AddRange(bot);
            if (has && pc > 0)
            {
                using (SolidBrush ink = new SolidBrush(p.Main))
                {
                    g.FillPolygon(ink, top.ToArray());
                    g.FillEllipse(ink, pos - R, y - R, 2f * R, 2f * R);
                }
                SkinDraw.Text(g, Skin_Num(pct), f.Small, p.Bg, new RectangleF(pos - 14f * s, y - 8f * s, 28f * s, 16f * s), StringAlignment.Center);
            }
            SkinDraw.Tag(g, label, f.Small, p.Main, p.Bg, box.X + 8f * s, y - 44f * s, s);
            string meta = clock + (left.Length > 0 ? " · " + left : "");
            SkinDraw.Text(g, meta, f.Small, p.Dim, new RectangleF(box.X + 8f * s, y - 26f * s, box.Width - 16f * s, 14f * s), StringAlignment.Far);
        }

        private static string Skin_Num(double pct)
        {
            return SkinDraw.Num(pct);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float y0 = cell.Y + 3f * s;
            float lineY = y0 + 13f * s;
            float cx = cell.X + cell.Width / 2f;
            using (Pen tk = new Pen(SkinDraw.A(170, p.Main), 1f * s))
            {
                for (float x = cell.X; x < cell.Right; x += 11.6f * s)
                {
                    g.DrawLine(tk, x, lineY - 2.5f * s, x, lineY + 2.5f * s);
                }
            }
            float d = (t.Selected ? 22f : 7f) * s;
            using (SolidBrush b = new SolidBrush(p.Main))
            {
                g.FillEllipse(b, cx - d / 2f, lineY - d / 2f, d, d);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 26f * s + 4f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            SkinDraw.Fit(g, t.Value, f.Label, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 26f * s + 18f * s, cell.Width, 16f * s), StringAlignment.Center);
        }

        public override void DrawShare(Graphics g, float x, float y, double share, SkinPalette p, float s)
        {
            float len = 40f * s * (float)Math.Max(0.0, Math.Min(1.0, share));
            float cy = y + 5.5f * s;
            using (Pen line = new Pen(p.Main, 3f * s))
            {
                g.DrawLine(line, x, cy, x + len, cy);
            }
            float r = 5f * s;
            using (SolidBrush b = new SolidBrush(p.Main))
            {
                g.FillEllipse(b, x + len - r, cy - r, 2f * r, 2f * r);
            }
        }
    }

    // ---------------------------------------------------------------- Mix 5: measuring cylinder (compact card) + small cylinder (tab)

    internal sealed class SkinMix5 : SkinCurrent
    {
        public override string Name { get { return "Mix 5 Cylinders"; } }
        public override float ShareWidth { get { return 46f; } }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            DefaultHeader(g, d, f, p, close, refresh, s, pad);
            RectangleF box = new RectangleF(pad, 34f * s, w - 2f * pad, 130f * s);
            Cylinder(g, box, box.X + box.Width * 0.17f, d.P5, d.Has5, "5H", Lv(p, d.P5), p, f, s);
            Cylinder(g, box, box.X + box.Width * 0.58f, d.PW, d.HasW, "7D", Lv(p, d.PW), p, f, s);
            float rowY = box.Bottom + 6f * s;
            QuotaRow(g, f, p, "5H", "5-hour quota", d.Clock5, d.Left5, Lv(p, d.P5), pad, w, rowY, s);
            QuotaRow(g, f, p, "7D", "Weekly quota", d.ClockW, d.LeftW, Lv(p, d.PW), pad, w, rowY + 16f * s, s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, rowY + 34f * s, w - 2f * pad, 16f * s), StringAlignment.Near);
        }

        private static GraphicsPath CylinderPath(float x, float top, float bot, float wdt)
        {
            float rb = wdt / 2f;
            GraphicsPath gp = new GraphicsPath();
            gp.AddLine(x, top, x, bot - rb);
            gp.AddArc(x, bot - wdt, wdt, wdt, 180, -180);
            gp.AddLine(x + wdt, bot - rb, x + wdt, top);
            gp.CloseFigure();
            return gp;
        }

        private static void Cylinder(Graphics g, RectangleF box, float x, double pct, bool has, string tag, Color col, SkinPalette p, Fonts f, float s)
        {
            float top = box.Y + 22f * s, bot = box.Bottom - 6f * s, wdt = 46f * s;
            double pc = Math.Max(0.0, Math.Min(100.0, pct));
            float level = bot - (bot - top) * (float)(pc / 100.0);
            using (GraphicsPath cp = CylinderPath(x, top, bot, wdt))
            {
                GraphicsState st = g.Save();
                g.SetClip(cp);
                using (SolidBrush ink = new SolidBrush(p.Main))
                {
                    g.FillRectangle(ink, x - 2f, level, wdt + 4f, bot - level + 2f);
                }
                g.Restore(st);
                using (Pen pen = new Pen(col, 1.2f * s))
                {
                    g.DrawPath(pen, cp);
                }
            }
            if (has)
            {
                SkinDraw.SplitText(g, SkinDraw.Num(pct), f.Big, p.Main, p.Bg, x + wdt / 2f, (top + bot) / 2f, level);
            }
            using (Pen tk = new Pen(col, 1f * s))
            {
                for (int k = 0; k <= 10; k++)
                {
                    float yy = bot - (bot - top) * k / 10f;
                    float x1 = x + wdt + 3f * s, x2 = x1 + (k % 5 == 0 ? 9f : 5f) * s;
                    g.DrawLine(tk, x1, yy, x2, yy);
                    if (k % 5 == 0)
                    {
                        SkinDraw.Text(g, (k * 10).ToString(CultureInfo.InvariantCulture), f.Small, p.Dim, new RectangleF(x2 + 2f * s, yy - 7f * s, 34f * s, 14f * s), StringAlignment.Near);
                    }
                }
            }
            SkinDraw.Tag(g, tag, f.Small, p.Main, p.Bg, x, box.Y + 2f * s, s);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float y0 = cell.Y + 3f * s;
            float cx = cell.X + cell.Width / 2f;
            float cw = 13f * s, ch = 34f * s;
            RectangleF r = new RectangleF(cx - cw / 2f, y0, cw, ch);
            if (t.Selected)
            {
                using (Pen ring = new Pen(p.Main, 1f * s))
                {
                    g.DrawRectangle(ring, r.X - 3f * s, r.Y - 3f * s, cw + 6f * s, ch + 6f * s);
                }
            }
            using (GraphicsPath cp = CylinderPath(r.X, r.Y, r.Bottom, cw))
            {
                GraphicsState st = g.Save();
                g.SetClip(cp);
                float level = r.Bottom - ch * (float)t.Fill;
                using (SolidBrush ink = new SolidBrush(p.Main))
                {
                    g.FillRectangle(ink, r.X, level, cw, r.Bottom - level);
                }
                g.Restore(st);
                using (Pen pen = new Pen(p.Main, 1f * s))
                {
                    g.DrawPath(pen, cp);
                }
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 34f * s + 6f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            SkinDraw.Fit(g, t.Value, f.Label, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 34f * s + 20f * s, cell.Width, 16f * s), StringAlignment.Center);
        }

        // Share: a small cylinder lying on its side
        public override void DrawShare(Graphics g, float x, float y, double share, SkinPalette p, float s)
        {
            RectangleF r = new RectangleF(x, y + 1f * s, 46f * s, 9f * s);
            SkinDraw.RrLine(g, p.Main, r, 4.5f * s, 1f * s);
            float fillW = (r.Width - 2f * s) * (float)Math.Max(0.0, Math.Min(1.0, share));
            if (fillW > 0f)
            {
                SkinDraw.Rr(g, p.Main, new RectangleF(r.X + 1f * s, r.Y + 1f * s, fillW, r.Height - 2f * s), 3.5f * s);
            }
        }
    }
}
