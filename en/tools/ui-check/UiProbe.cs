// Layout probe: every string that the widget draws through Widget.DrawTextRect / SkinDraw.Text is reported here
// (only when the harness is compiled with /define:UICHECK, see run.ps1; release builds never contain the hook).
// After a frame is drawn, Issues() lists
//   CLIP     the text is wider (or taller) than its rectangle, so GDI+ cuts it to "xxx..." or clips the glyphs
//   OVERLAP  two different texts overlap each other
//   EDGE     the text touches / crosses the window border
// The probe measures with the same Graphics and font that draw the text, so the numbers are what the user sees.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Stilla
{
    internal static class UiProbe
    {
        public static bool On;
        public static readonly List<string> Ignore = new List<string>();      // texts that are meant to be ellipsized (very long session titles of the fake data)

        private sealed class Item
        {
            public string Text;
            public RectangleF Rect;     // the layout rectangle that was passed to DrawString
            public RectangleF Ink;      // where the glyphs really are (approximate)
            public float Need, LineH;
            public bool ClipW, ClipH, ClipR;      // ClipR: GDI+ really trims the text (also after DrawTextFit shrank the font)
        }

        private static readonly List<Item> items = new List<Item>();

        public static void Begin()
        {
            items.Clear();
            On = true;
        }

        public static void Text(Graphics g, string text, Font font, RectangleF r, StringAlignment a)
        {
            if (!On || string.IsNullOrEmpty(text))
            {
                return;
            }
            Item it = new Item();
            it.Text = text;
            it.Rect = r;
            using (StringFormat sf = new StringFormat())
            {
                sf.FormatFlags = StringFormatFlags.NoWrap;
                SizeF sz = g.MeasureString(text, font, 100000, sf);
                it.Need = sz.Width;
                it.LineH = sz.Height;
            }
            using (StringFormat sf2 = new StringFormat())
            {
                sf2.FormatFlags = StringFormatFlags.NoWrap;
                sf2.Trimming = StringTrimming.Character;
                int fitted, lines;
                g.MeasureString(text, font, new SizeF(Math.Max(1f, r.Width), Math.Max(r.Height, font.Height * 2f)), sf2, out fitted, out lines);
                it.ClipR = fitted < text.Length;
            }
            float pad = font.Size / 6f;
            it.ClipW = it.Need > r.Width + 0.6f;
            it.ClipH = r.Height < it.LineH * 0.72f;
            float inkW = Math.Max(1f, Math.Min(it.Need, r.Width) - 2f * pad);
            float inkH = it.LineH * 0.78f;
            float x = a == StringAlignment.Near ? r.X + pad : a == StringAlignment.Far ? r.Right - pad - inkW : r.X + (r.Width - inkW) / 2f;
            float y = r.Y + (r.Height - inkH) / 2f;
            it.Ink = new RectangleF(x, y, inkW, inkH);
            items.Add(it);
        }

        private static string Short(Item i)
        {
            string t = i.Text.Replace('\n', ' ');
            return "\"" + (t.Length > 34 ? t.Substring(0, 34) + "~" : t) + "\"";
        }

        // W, H = size of the frame in pixels (the drawing surface)
        public static List<string> Issues(float W, float H)
        {
            On = false;
            List<string> res = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (Item i in items)
            {
                string key = i.Text + "|" + i.Rect.ToString();
                if (!seen.Add(key))
                {
                    continue;        // SplitText & friends draw the same text twice on purpose
                }
                bool ignored = false;
                foreach (string ig in Ignore)
                {
                    if (i.Text.Contains(ig))
                    {
                        ignored = true;
                    }
                }
                if (ignored)
                {
                    continue;
                }
                if (i.ClipW)
                {
                    res.Add("CLIP    " + Short(i) + "  needs " + i.Need.ToString("0") + "px, has " + i.Rect.Width.ToString("0") + "px");
                }
                else if (i.ClipR)
                {
                    res.Add("CLIPR   " + Short(i) + "  real trimming in " + i.Rect.Width.ToString("0") + "px (needs " + i.Need.ToString("0") + ")");
                }
                else if (i.ClipH)
                {
                    res.Add("CLIPH   " + Short(i) + "  line " + i.LineH.ToString("0") + "px, rect " + i.Rect.Height.ToString("0") + "px");
                }
                if (i.Ink.Left < 3f || i.Ink.Right > W - 3f)
                {
                    res.Add("EDGE    " + Short(i) + "  ink x " + i.Ink.Left.ToString("0") + ".." + i.Ink.Right.ToString("0") + " of " + W.ToString("0"));
                }
            }
            seen.Clear();
            for (int a = 0; a < items.Count; a++)
            {
                for (int b = a + 1; b < items.Count; b++)
                {
                    Item p = items[a], q = items[b];
                    if (p.Text == q.Text && p.Rect == q.Rect)
                    {
                        continue;
                    }
                    float ox = Math.Min(p.Ink.Right, q.Ink.Right) - Math.Max(p.Ink.Left, q.Ink.Left);
                    float oy = Math.Min(p.Ink.Bottom, q.Ink.Bottom) - Math.Max(p.Ink.Top, q.Ink.Top);
                    if (ox > 1.5f && oy > 0.45f * Math.Min(p.Ink.Height, q.Ink.Height))
                    {
                        string k = p.Text + "|" + q.Text;
                        if (seen.Add(k))
                        {
                            res.Add("OVERLAP " + Short(p) + " x " + Short(q) + "  by " + ox.ToString("0") + "px");
                        }
                    }
                }
            }
            return res;
        }
    }
}
