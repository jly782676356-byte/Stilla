using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Stilla
{
    // 界面风格：方案 2（A–E）和方案 3（F–J）。每个风格都画自己的紧凑卡、标签、按钮和边框

    internal static class SkinLevel
    {
        public static string Text(double pct)
        {
            if (pct >= 85.0)
            {
                return "告急";
            }
            return pct >= 60.0 ? "偏高" : "正常";
        }
    }

    // ---------------------------------------------------------------- A 桌面小组件（暗色小卡片，大数字，底部一条进度线）

    internal sealed class SkinA : Skin
    {
        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            return new PointF(SkinDraw.TextEnd(g, "Stilla · 额度", f.Small, 14f * s) + 9f * s, 16f * s);
        }

        public override string Name { get { return "2-A 桌面小组件"; } }
        public override int CompactH { get { return 200; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(17, 17, 19), Surface = SkinDraw.C(17, 17, 19), Border = SkinDraw.C(44, 44, 48),
                    Main = SkinDraw.C(243, 243, 244), Dim = SkinDraw.C(140, 140, 148), Track = SkinDraw.A(22, SkinDraw.C(255, 255, 255)),
                    Accent = SkinDraw.C(255, 69, 58), Ok = SkinDraw.C(48, 209, 88), Warn = SkinDraw.C(255, 214, 10), Bad = SkinDraw.C(255, 69, 58)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(246, 246, 246), Surface = SkinDraw.C(246, 246, 246), Border = SkinDraw.C(220, 220, 222),
                Main = SkinDraw.C(20, 20, 22), Dim = SkinDraw.C(120, 120, 128), Track = SkinDraw.A(24, SkinDraw.C(0, 0, 0)),
                Accent = SkinDraw.C(255, 59, 48), Ok = SkinDraw.C(36, 168, 70), Warn = SkinDraw.C(230, 160, 0), Bad = SkinDraw.C(255, 59, 48)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            SkinDraw.Text(g, "Stilla · 额度", f.Small, p.Dim, new RectangleF(pad, 6f * s, 160f * s, 20f * s), StringAlignment.Near);
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "刷新", f.Small, p.Dim, refresh, StringAlignment.Center);
            float tw = (w - 2f * pad - 8f * s) / 2f;
            float th = 136f * s, ty = 30f * s;
            DrawTile(g, new RectangleF(pad, ty, tw, th), "5 小时", d.P5, d.Has5, d.Clock5, d.Left5, f, p, s);
            DrawTile(g, new RectangleF(pad + tw + 8f * s, ty, tw, th), "每周", d.PW, d.HasW, d.ClockW, d.LeftW, f, p, s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, ty + th + 10f * s, w - 2f * pad, 16f * s), StringAlignment.Near);
        }

        private static void DrawTile(Graphics g, RectangleF r, string label, double pct, bool has, string clock, string left, Fonts f, SkinPalette p, float s)
        {
            SkinDraw.Rr(g, p.Track, r, 16f * s);
            Color c = has ? Lv(p, pct) : p.Dim;
            using (SolidBrush dot = new SolidBrush(c))
            {
                g.FillEllipse(dot, r.X + 14f * s, r.Y + 13f * s, 6f * s, 6f * s);
            }
            SkinDraw.Text(g, label, f.Small, p.Dim, new RectangleF(r.X + 26f * s, r.Y + 8f * s, r.Width - 30f * s, 16f * s), StringAlignment.Near);
            string num = has ? Num(pct) : "—";
            using (Font big = SkinDraw.F("Segoe UI", 40f * s, FontStyle.Bold))
            using (Font unit = SkinDraw.F("Segoe UI", 18f * s, FontStyle.Regular))
            {
                SkinDraw.Text(g, num, big, p.Main, new RectangleF(r.X + 12f * s, r.Y + 30f * s, r.Width - 24f * s, 46f * s), StringAlignment.Near);
                if (has)
                {
                    float nw = g.MeasureString(num, big).Width;
                    SkinDraw.Text(g, "%", unit, p.Dim, new RectangleF(r.X + 12f * s + nw, r.Y + 46f * s, 30f * s, 22f * s), StringAlignment.Near);
                }
            }
            using (Font clockFont = SkinDraw.F("Segoe UI", 11f * s, FontStyle.Regular))
            {
                SkinDraw.Fit(g, "重置 " + clock, clockFont, p.Dim, new RectangleF(r.X + 12f * s, r.Y + 88f * s, r.Width - 24f * s, 16f * s), StringAlignment.Near);
            }
            SkinDraw.Text(g, left, f.Small, p.Main, new RectangleF(r.X + 12f * s, r.Y + 106f * s, r.Width - 24f * s, 16f * s), StringAlignment.Near);
            RectangleF track = new RectangleF(r.X + 14f * s, r.Bottom - 14f * s, r.Width - 28f * s, 3f * s);
            SkinDraw.Rr(g, p.Dim, track, 1.5f * s);
            if (has)
            {
                float fill = track.Width * (float)(Math.Max(0.0, Math.Min(100.0, pct)) / 100.0);
                if (fill > 0f)
                {
                    SkinDraw.Rr(g, c, new RectangleF(track.X, track.Y, fill, track.Height), 1.5f * s);
                }
            }
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            RectangleF r = new RectangleF(cell.X + 2f * s, cell.Y + 2f * s, cell.Width - 4f * s, 58f * s);
            if (t.Selected)
            {
                SkinDraw.Rr(g, p.Track, r, 10f * s);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Accent : p.Dim, new RectangleF(r.X, r.Y + 8f * s, r.Width, 14f * s), StringAlignment.Center);
            }
            using (Font vf = SkinDraw.F("Segoe UI", 12.5f * s, FontStyle.Bold))
            {
                SkinDraw.Fit(g, t.Value, vf, t.Selected ? p.Main : p.Dim, new RectangleF(r.X, r.Y + 26f * s, r.Width, 22f * s), StringAlignment.Center);
            }
        }

        public override void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s)
        {
            SkinDraw.Rr(g, SkinDraw.A(10, p.Main), new RectangleF(rect.X + 6f * s, rect.Y + 6f * s, rect.Width - 12f * s, rect.Height - 12f * s), 18f * s);
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            SkinDraw.Text(g, text, f.Small, p.Dim, new RectangleF(x, y, 160f * s, 22f * s), StringAlignment.Near);
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            RectangleF all = RectangleF.Union(rects[0], rects[rects.Length - 1]);
            SkinDraw.Rr(g, p.Track, all, 9f * s);
            for (int i = 0; i < rects.Length; i++)
            {
                if (i == sel)
                {
                    SkinDraw.Rr(g, SkinDraw.Mix(p.Bg, p.Main, 0.18f), rects[i], 7f * s);
                }
                SkinDraw.Text(g, names[i], f.Small, i == sel ? p.Main : p.Dim, rects[i], StringAlignment.Center);
            }
        }
    }

    // ---------------------------------------------------------------- B 应用界面 + 柔光（白卡片、状态胶囊、大数字、细进度条）

    internal sealed class SkinB : Skin
    {
        public override bool ShowAccountChip { get { return false; } }     // 标题 + 两个按键已经占满顶部，标签会压在 quota 字上

        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            using (Font serif = SkinDraw.F("Georgia", 15f * s, FontStyle.Italic))
            {
                return new PointF(SkinDraw.TextEnd(g, "quota", serif, 14f * s + 74f * s) + 9f * s, 17f * s);
            }
        }

        public override string Name { get { return "2-B 应用界面"; } }
        public override int CompactH { get { return 300; } }
        public override string DisplayFamily { get { return "Segoe UI"; } }
        public override FontStyle DisplayStyle { get { return FontStyle.Bold; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(24, 24, 27), Surface = SkinDraw.C(24, 24, 27), Border = SkinDraw.C(60, 60, 66),
                    Main = SkinDraw.C(250, 250, 250), Dim = SkinDraw.C(150, 150, 158), Track = SkinDraw.C(40, 40, 44),
                    Accent = SkinDraw.C(250, 250, 250), Ok = SkinDraw.C(52, 211, 153), Warn = SkinDraw.C(251, 191, 36), Bad = SkinDraw.C(248, 113, 113)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(255, 255, 255), Surface = SkinDraw.C(255, 255, 255), Border = SkinDraw.C(232, 232, 236),
                Main = SkinDraw.C(24, 24, 27), Dim = SkinDraw.C(118, 118, 127), Track = SkinDraw.C(243, 243, 245),
                Accent = SkinDraw.C(24, 24, 27), Ok = SkinDraw.C(26, 159, 90), Warn = SkinDraw.C(217, 131, 0), Bad = SkinDraw.C(214, 64, 69)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            // 标题：小圆点图案 + Stilla + 斜体衬线 quota
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    using (SolidBrush b = new SolidBrush(p.Main))
                    {
                        g.FillEllipse(b, pad + i * 4.7f * s, 8f * s + j * 4.7f * s, 2.2f * s, 2.2f * s);
                    }
                }
            }
            SkinDraw.Text(g, "Stilla", f.Label, p.Main, new RectangleF(pad + 18f * s, 6f * s, 80f * s, 22f * s), StringAlignment.Near);
            using (Font serif = SkinDraw.F("Georgia", 15f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, "quota", serif, p.Dim, new RectangleF(pad + 74f * s, 6f * s, 80f * s, 22f * s), StringAlignment.Near);
            }
            KeyCap(g, refresh, "R", p, s);
            KeyCap(g, close, "×", p, s);

            Block(g, d.P5, d.Has5, "5 小时额度", d.Clock5, d.Left5, new RectangleF(pad, 40f * s, inner, 112f * s), f, p, s);
            Block(g, d.PW, d.HasW, "每周额度", d.ClockW, d.LeftW, new RectangleF(pad, 160f * s, inner, 112f * s), f, p, s);
            RectangleF foot = new RectangleF(pad, 280f * s, inner, 16f * s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, foot, StringAlignment.Near);
        }

        private static void KeyCap(Graphics g, RectangleF r, string text, SkinPalette p, float s)
        {
            SkinDraw.Rr(g, p.Bg, r, 5f * s);
            SkinDraw.RrLine(g, p.Border, r, 5f * s, 1f * s);
            using (Font kf = SkinDraw.F("Segoe UI", 10f * s, FontStyle.Regular))
            {
                SkinDraw.Text(g, text, kf, p.Dim, r, StringAlignment.Center);
            }
        }

        private static void Block(Graphics g, double pct, bool has, string label, string clock, string left, RectangleF r, Fonts f, SkinPalette p, float s)
        {
            Color c = has ? Lv(p, pct) : p.Dim;
            SkinDraw.Text(g, label, f.Small, p.Dim, new RectangleF(r.X, r.Y, r.Width - 80f * s, 16f * s), StringAlignment.Near);
            RectangleF chip = new RectangleF(r.Right - 52f * s, r.Y + 1f * s, 52f * s, 16f * s);
            if (has)
            {
                SkinDraw.Rr(g, SkinDraw.A(38, c), chip, 8f * s);
                using (Font cf = SkinDraw.F("Microsoft YaHei UI", 10f * s, FontStyle.Regular))
                {
                    SkinDraw.Text(g, SkinLevel.Text(pct), cf, c, chip, StringAlignment.Center);
                }
            }
            using (Font big = SkinDraw.F("Segoe UI Semibold", 46f * s, FontStyle.Regular))
            using (Font unit = SkinDraw.F("Segoe UI", 20f * s, FontStyle.Regular))
            {
                string num = has ? Num(pct) : "—";
                SkinDraw.Text(g, num, big, p.Main, new RectangleF(r.X, r.Y + 18f * s, r.Width, 52f * s), StringAlignment.Near);
                if (has)
                {
                    float nw = g.MeasureString(num, big).Width;
                    SkinDraw.Text(g, "%", unit, p.Dim, new RectangleF(r.X + nw + 2f * s, r.Y + 36f * s, 30f * s, 24f * s), StringAlignment.Near);
                }
            }
            RectangleF track = new RectangleF(r.X, r.Y + 76f * s, r.Width, 5f * s);
            SkinDraw.Rr(g, p.Track, track, 2.5f * s);
            if (has)
            {
                float fill = track.Width * (float)(Math.Max(0.0, Math.Min(100.0, pct)) / 100.0);
                if (fill > 0f)
                {
                    SkinDraw.Rr(g, p.Main, new RectangleF(track.X, track.Y, fill, track.Height), 2.5f * s);
                }
            }
            SkinDraw.Text(g, "重置 " + clock, f.Small, p.Dim, new RectangleF(r.X, r.Y + 88f * s, r.Width / 2f, 16f * s), StringAlignment.Near);
            SkinDraw.Text(g, left, f.Small, p.Main, new RectangleF(r.X + r.Width / 2f, r.Y + 88f * s, r.Width / 2f, 16f * s), StringAlignment.Far);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            RectangleF r = new RectangleF(cell.X + 2f * s, cell.Y, cell.Width - 4f * s, 60f * s);
            SkinDraw.Rr(g, t.Selected ? p.Bg : p.Track, r, 8f * s);
            if (t.Selected)
            {
                SkinDraw.RrLine(g, p.Border, r, 8f * s, 1f * s);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(r.X, r.Y + 8f * s, r.Width, 14f * s), StringAlignment.Center);
            }
            using (Font vf = SkinDraw.F("Segoe UI", 11.5f * s, FontStyle.Bold))
            {
                SkinDraw.Fit(g, t.Value, vf, t.Selected ? p.Main : p.Dim, new RectangleF(r.X, r.Y + 28f * s, r.Width, 18f * s), StringAlignment.Center);
            }
        }

        public override void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s)
        {
            RectangleF r = new RectangleF(rect.X + 6f * s, rect.Y + 6f * s, rect.Width - 12f * s, rect.Height - 12f * s);
            SkinDraw.Rr(g, p.Surface, r, 14f * s);
            SkinDraw.RrLine(g, p.Border, r, 14f * s, 1f * s);
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            SkinDraw.Text(g, text, f.Label, p.Main, new RectangleF(x, y, 160f * s, 22f * s), StringAlignment.Near);
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            RectangleF all = RectangleF.Union(rects[0], rects[rects.Length - 1]);
            SkinDraw.Rr(g, p.Track, all, 9f * s);
            for (int i = 0; i < rects.Length; i++)
            {
                if (i == sel)
                {
                    SkinDraw.Rr(g, p.Bg, rects[i], 7f * s);
                    SkinDraw.RrLine(g, p.Border, rects[i], 7f * s, 1f * s);
                }
                SkinDraw.Text(g, names[i], f.Small, i == sel ? p.Main : p.Dim, rects[i], StringAlignment.Center);
            }
        }
    }

    // ---------------------------------------------------------------- C 编号索引（米白纸、裁切的大数字、细线分栏）

    internal sealed class SkinC : Skin
    {
        public override bool ShowAccountChip { get { return false; } }

        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            return new PointF(refresh.X - 4f * s, refresh.Y + refresh.Height / 2f);
        }

        public override string Name { get { return "2-C 编号索引"; } }
        public override int CompactH { get { return 236; } }
        public override string DisplayFamily { get { return "Segoe UI"; } }
        public override FontStyle DisplayStyle { get { return FontStyle.Bold; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(27, 27, 27), Surface = SkinDraw.C(27, 27, 27), Border = SkinDraw.C(70, 70, 68),
                    Main = SkinDraw.C(246, 245, 241), Dim = SkinDraw.C(120, 120, 114), Track = SkinDraw.C(70, 70, 68),
                    Accent = SkinDraw.C(232, 72, 28), Ok = SkinDraw.C(246, 245, 241), Warn = SkinDraw.C(232, 72, 28), Bad = SkinDraw.C(232, 72, 28)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(246, 245, 241), Surface = SkinDraw.C(246, 245, 241), Border = SkinDraw.C(214, 213, 207),
                Main = SkinDraw.C(27, 27, 27), Dim = SkinDraw.C(154, 153, 147), Track = SkinDraw.C(211, 210, 204),
                Accent = SkinDraw.C(232, 72, 28), Ok = SkinDraw.C(27, 27, 27), Warn = SkinDraw.C(232, 72, 28), Bad = SkinDraw.C(232, 72, 28)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "刷新", f.Small, p.Dim, refresh, StringAlignment.Center);
            // 两个裁切的大数字：上面被切掉一截，两个数字互相压着
            GraphicsState st = g.Save();
            g.SetClip(new RectangleF(pad, 0f, inner, 90f * s));
            string bigW = d.HasW ? Num(d.PW) : "—", big5 = d.Has5 ? Num(d.P5) : "—";
            bool three = bigW.Length > 2 || big5.Length > 2;       // 100 是三位数：字号缩小，免得两个数字叠成一团
            using (Font big = SkinDraw.F("Segoe UI", (three ? 64f : 104f) * s, FontStyle.Bold))
            {
                float w5 = g.MeasureString(big5, big).Width;
                float xW = pad - 4f * s + Math.Max(0.83f * w5, 0f);
                SkinDraw.Text(g, bigW, big, p.Track, new RectangleF(xW, three ? -4f * s : -20f * s, 260f * s, 120f * s), StringAlignment.Near);
                SkinDraw.Text(g, big5, big, p.Main, new RectangleF(pad - 4f * s, three ? -4f * s : -20f * s, 260f * s, 120f * s), StringAlignment.Near);
            }
            g.Restore(st);
            Col(g, d.P5, d.Has5, "5 小时额度", d.Clock5, d.Left5, new RectangleF(pad, 96f * s, 88f * s, 80f * s), f, p, s);
            Col(g, d.PW, d.HasW, "每周额度", d.ClockW, d.LeftW, new RectangleF(pad + 104f * s, 96f * s, inner - 104f * s, 80f * s), f, p, s);
            using (Pen line = new Pen(p.Track, 1f * s))
            {
                g.DrawLine(line, pad, 190f * s, w - pad, 190f * s);
            }
            float legend = g.MeasureString("(R) 刷新  (X) 关闭", f.Small).Width;
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, 198f * s, Math.Max(40f * s, inner - legend - 4f * s), 16f * s), StringAlignment.Near);
            SkinDraw.Text(g, "(R) 刷新  (X) 关闭", f.Small, p.Dim, new RectangleF(pad, 198f * s, inner, 16f * s), StringAlignment.Far);
        }

        private static void Col(Graphics g, double pct, bool has, string label, string clock, string left, RectangleF r, Fonts f, SkinPalette p, float s)
        {
            Color c = has ? Lv(p, pct) : p.Dim;
            RectangleF rule = new RectangleF(r.X, r.Y, r.Width - 12f * s, 2f * s);
            SkinDraw.Rr(g, p.Track, rule, 0f);
            if (has)
            {
                float fill = rule.Width * (float)(Math.Max(0.0, Math.Min(100.0, pct)) / 100.0);
                if (fill > 0f)
                {
                    SkinDraw.Rr(g, c, new RectangleF(rule.X, rule.Y, fill, rule.Height), 0f);
                }
            }
            SkinDraw.Text(g, label, f.Small, p.Main, new RectangleF(r.X, r.Y + 8f * s, r.Width, 16f * s), StringAlignment.Near);
            SkinDraw.Fit(g, "重置 " + clock, f.Small, p.Dim, new RectangleF(r.X, r.Y + 24f * s, r.Width, 16f * s), StringAlignment.Near);
            SkinDraw.Fit(g, left, f.Small, p.Main, new RectangleF(r.X, r.Y + 40f * s, r.Width, 16f * s), StringAlignment.Near);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float y = cell.Y + 2f * s;
            using (Pen rule = new Pen(t.Selected ? p.Main : p.Track, (t.Selected ? 2f : 1f) * s))
            {
                g.DrawLine(rule, cell.X + 2f * s, y, cell.Right - 2f * s, y);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X + 4f * s, y + 6f * s, cell.Width, 14f * s), StringAlignment.Near);
            }
            using (Font vf = SkinDraw.F("Segoe UI", 18f * s, FontStyle.Regular))
            {
                SkinDraw.Fit(g, t.Value, vf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X + 4f * s, y + 20f * s, cell.Width, 26f * s), StringAlignment.Near);
            }
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            SkinDraw.Text(g, text, f.Label, p.Main, new RectangleF(x, y, 200f * s, 22f * s), StringAlignment.Near);
            using (Pen line = new Pen(p.Main, 1f * s))
            {
                g.DrawLine(line, x, y + 26f * s, x + 392f * s, y + 26f * s);
            }
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            for (int i = 0; i < rects.Length; i++)
            {
                SkinDraw.Text(g, names[i], f.Small, i == sel ? p.Main : p.Dim, rects[i], StringAlignment.Center);
                if (i == sel)
                {
                    float tw = g.MeasureString(names[i], f.Small).Width;
                    float cx = rects[i].X + rects[i].Width / 2f;
                    using (Pen u = new Pen(p.Main, 1.5f * s))
                    {
                        g.DrawLine(u, cx - tw / 2f, rects[i].Bottom - 2f * s, cx + tw / 2f, rects[i].Bottom - 2f * s);
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- D 抖动点阵（纯黑、等宽、方括号、抖动条）

    internal sealed class SkinD : Skin
    {
        public override bool ShowAccountChip { get { return false; } }

        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            return new PointF(SkinDraw.TextEnd(g, "[:: STILLA ::]", f.Display, 14f * s + 36f * s) + 15f * s, 19f * s);
        }

        public override string Name { get { return "2-D 抖动点阵"; } }
        public override int CompactH { get { return 292; } }
        public override float ShareWidth { get { return 44f; } }
        public override string DisplayFamily { get { return "Consolas"; } }
        public override FontStyle DisplayStyle { get { return FontStyle.Bold; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(0, 0, 0), Surface = SkinDraw.C(0, 0, 0), Border = SkinDraw.C(42, 42, 42),
                    Main = SkinDraw.C(236, 236, 236), Dim = SkinDraw.C(108, 108, 108), Track = SkinDraw.C(38, 38, 38),
                    Accent = SkinDraw.C(236, 236, 236), Ok = SkinDraw.C(236, 236, 236), Warn = SkinDraw.C(255, 176, 0), Bad = SkinDraw.C(255, 77, 61)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(236, 236, 232), Surface = SkinDraw.C(236, 236, 232), Border = SkinDraw.C(190, 190, 186),
                Main = SkinDraw.C(14, 14, 14), Dim = SkinDraw.C(110, 110, 106), Track = SkinDraw.C(206, 206, 202),
                Accent = SkinDraw.C(14, 14, 14), Ok = SkinDraw.C(14, 14, 14), Warn = SkinDraw.C(200, 120, 0), Bad = SkinDraw.C(200, 40, 30)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            SkinDraw.DitherOrb(g, new RectangleF(pad, 6f * s, 28f * s, 28f * s), p.Main, 2f * s);
            SkinDraw.Text(g, "[:: STILLA ::]", f.Display, p.Main, new RectangleF(pad + 36f * s, 8f * s, 170f * s, 22f * s), StringAlignment.Near);
            SkinDraw.Text(g, "[X]", f.Small, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "[R]", f.Small, p.Dim, refresh, StringAlignment.Center);
            Block(g, d.P5, d.Has5, "[ 5H ]", "FIVE HOURS", d.Clock5, d.Left5, new RectangleF(pad, 34f * s, inner, 110f * s), f, p, s);
            using (Pen dash = new Pen(p.Border, 1f * s))
            {
                dash.DashStyle = DashStyle.Dot;
                g.DrawLine(dash, pad, 148f * s, w - pad, 148f * s);
            }
            Block(g, d.PW, d.HasW, "[ 7D ]", "WEEKLY", d.ClockW, d.LeftW, new RectangleF(pad, 156f * s, inner, 110f * s), f, p, s);
            SkinDraw.Text(g, d.Alert ? d.Foot : "[ " + d.Updated + " :: " + d.SourceEn + " ]", f.Small, d.Alert ? d.FootColor : p.Dim, new RectangleF(pad, 266f * s, inner, 16f * s), StringAlignment.Center);
        }

        private static void Block(Graphics g, double pct, bool has, string tag, string caption, string clock, string left, RectangleF r, Fonts f, SkinPalette p, float s)
        {
            Color c = has ? Lv(p, pct) : p.Dim;
            SkinDraw.Text(g, tag, f.Display, p.Main, new RectangleF(r.X, r.Y, 120f * s, 16f * s), StringAlignment.Near);
            SkinDraw.Text(g, caption, f.Small, p.Dim, new RectangleF(r.X, r.Y, r.Width, 16f * s), StringAlignment.Far);
            using (Font big = SkinDraw.F("Consolas", 44f * s, FontStyle.Bold))
            using (Font unit = SkinDraw.F("Consolas", 16f * s, FontStyle.Bold))
            {
                string num = has ? Num(pct) : "—";
                SkinDraw.Text(g, num, big, c, new RectangleF(r.X, r.Y + 18f * s, r.Width, 50f * s), StringAlignment.Near);
                if (has)
                {
                    float nw = g.MeasureString(num, big).Width;
                    SkinDraw.Text(g, "%", unit, p.Dim, new RectangleF(r.X + nw + 2f * s, r.Y + 42f * s, 30f * s, 22f * s), StringAlignment.Near);
                }
            }
            SkinDraw.DitherBar(g, new RectangleF(r.X, r.Y + 72f * s, r.Width, 16f * s), has ? pct : 0, c, p.Track, 2f * s);
            SkinDraw.Text(g, "RESET " + clock, f.Small, p.Dim, new RectangleF(r.X, r.Y + 92f * s, r.Width / 2f, 16f * s), StringAlignment.Near);
            SkinDraw.Text(g, left, f.Small, p.Main, new RectangleF(r.X + r.Width / 2f, r.Y + 92f * s, r.Width / 2f, 16f * s), StringAlignment.Far);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            if (t.Index > 0)
            {
                using (Pen sep = new Pen(p.Border, 1f * s))
                {
                    g.DrawLine(sep, cell.X, cell.Y + 4f * s, cell.X, cell.Bottom - 4f * s);
                }
            }
            if (t.Selected)
            {
                SkinDraw.Rr(g, p.Main, new RectangleF(cell.X + 1f * s, cell.Y + 2f * s, cell.Width - 2f * s, 58f * s), 0f);
            }
            Color fg = t.Selected ? p.Bg : p.Dim;
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, fg, new RectangleF(cell.X, cell.Y + 10f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            SkinDraw.Fit(g, t.Value, f.Label, t.Selected ? p.Bg : p.Main, new RectangleF(cell.X, cell.Y + 30f * s, cell.Width, 16f * s), StringAlignment.Center);
        }

        public override void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s)
        {
            using (Pen line = new Pen(p.Border, 1f * s))
            {
                g.DrawRectangle(line, rect.X + 6f * s, rect.Y + 4f * s, rect.Width - 12f * s, rect.Height - 8f * s);
            }
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            SkinDraw.Text(g, "[:: USAGE ::]", f.Display, p.Main, new RectangleF(x, y, 200f * s, 22f * s), StringAlignment.Near);
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            for (int i = 0; i < rects.Length; i++)
            {
                if (i == sel)
                {
                    SkinDraw.Rr(g, p.Main, rects[i], 0f);
                }
                SkinDraw.Fit(g, names[i], f.Small, i == sel ? p.Bg : p.Dim, rects[i], StringAlignment.Center);   // 按钮只有 34px 宽，方括号放不下；选中的反白块已经够明显
            }
        }

        public override void DrawShare(Graphics g, float x, float y, double share, SkinPalette p, float s)
        {
            SkinDraw.DitherBar(g, new RectangleF(x, y + 1f * s, 44f * s, 8f * s), share * 100.0, p.Main, p.Track, 2f * s);
        }
    }

    // ---------------------------------------------------------------- E 线稿几何（两个相交的圆、标注框和引线、线上的圆）

    internal sealed class SkinE : Skin
    {
        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            return new PointF(SkinDraw.TextEnd(g, "Stilla 额度", f.Label, 14f * s) + 9f * s, 17f * s);
        }

        public override string Name { get { return "2-E 线稿几何"; } }
        public override int CompactH { get { return 280; } }
        public override string DisplayFamily { get { return "Segoe UI"; } }
        public override FontStyle DisplayStyle { get { return FontStyle.Bold; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(20, 20, 20), Surface = SkinDraw.C(20, 20, 20), Border = SkinDraw.C(70, 70, 68),
                    Main = SkinDraw.C(240, 240, 238), Dim = SkinDraw.C(130, 130, 126), Track = SkinDraw.C(42, 42, 42),
                    Accent = SkinDraw.C(232, 72, 28), Ok = SkinDraw.C(240, 240, 238), Warn = SkinDraw.C(232, 72, 28), Bad = SkinDraw.C(232, 72, 28)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(250, 250, 248), Surface = SkinDraw.C(250, 250, 248), Border = SkinDraw.C(217, 217, 212),
                Main = SkinDraw.C(17, 17, 17), Dim = SkinDraw.C(122, 122, 118), Track = SkinDraw.C(230, 230, 226),
                Accent = SkinDraw.C(232, 72, 28), Ok = SkinDraw.C(17, 17, 17), Warn = SkinDraw.C(232, 72, 28), Bad = SkinDraw.C(232, 72, 28)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            SkinDraw.Text(g, "Stilla 额度", f.Label, p.Main, new RectangleF(pad, 6f * s, 160f * s, 22f * s), StringAlignment.Near);
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "刷新", f.Small, p.Dim, refresh, StringAlignment.Center);

            RectangleF box = new RectangleF(pad, 34f * s, inner, 150f * s);
            SkinDraw.Rr(g, p.Track, box, 7f * s);
            float k = inner / 236f;
            float cy = box.Y + 75f * s;
            float cxL = box.X + 94f * k, cxR = box.X + 148f * k;
            float rL = 46f * s, rR = 36f * s;
            using (Pen axis = new Pen(SkinDraw.A(90, p.Main), 1f * s))
            {
                g.DrawLine(axis, box.X, cy, box.Right, cy);
                g.DrawLine(axis, box.X + 118f * k, box.Y, box.X + 118f * k, box.Bottom);
                for (int i = 0; i <= 8; i++)
                {
                    float tx = box.X + 29.5f * i * k;
                    g.DrawLine(axis, tx, cy - 4f * s, tx, cy + 4f * s);
                }
            }
            Ring(g, cxL, cy, rL, d.P5, d.Has5, p, s);
            Ring(g, cxR, cy, rR, d.PW, d.HasW, p, s);
            string t5 = (d.Has5 ? Num(d.P5) : "—") + "%";
            string tw = (d.HasW ? Num(d.PW) : "—") + "%";
            RectangleF tag5 = SkinDraw.Tag(g, "5H " + t5, f.Small, p.Main, p.Bg, box.X + 6f * s, box.Y + 8f * s, s);
            float wTag = g.MeasureString("7D " + tw, f.Small).Width + 8f * s;
            RectangleF tag7 = SkinDraw.Tag(g, "7D " + tw, f.Small, p.Main, p.Bg, box.Right - 6f * s - wTag, box.Bottom - 8f * s - tag5.Height, s);
            using (Pen lead = new Pen(SkinDraw.A(150, p.Main), 1f * s))
            {
                g.DrawLine(lead, tag5.Right, tag5.Bottom, cxL - rL * 0.7071f, cy - rL * 0.7071f);
                g.DrawLine(lead, tag7.Left, tag7.Top, cxR + rR * 0.7071f, cy + rR * 0.7071f);
            }
            float y = box.Bottom + 8f * s;
            Row(g, f, p, "5 小时", d.P5, d.Has5, d.Clock5, d.Left5, new RectangleF(pad, y, inner, 26f * s), s);
            Row(g, f, p, "每周", d.PW, d.HasW, d.ClockW, d.LeftW, new RectangleF(pad, y + 30f * s, inner, 26f * s), s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, y + 62f * s, inner, 16f * s), StringAlignment.Near);
        }

        private static void Ring(Graphics g, float cx, float cy, float r, double pct, bool has, SkinPalette p, float s)
        {
            using (Pen track = new Pen(SkinDraw.A(150, p.Main), 0.8f * s))
            {
                g.DrawEllipse(track, cx - r, cy - r, 2f * r, 2f * r);
            }
            if (has)
            {
                float sweep = (float)(Math.Max(0.0, Math.Min(100.0, pct)) * 3.6);
                if (sweep > 0.5f)
                {
                    using (Pen arc = new Pen(Lv(p, pct), 3f * s))
                    {
                        g.DrawArc(arc, cx - r, cy - r, 2f * r, 2f * r, -90f, sweep);
                    }
                }
            }
        }

        private static void Row(Graphics g, Fonts f, SkinPalette p, string label, double pct, bool has, string clock, string left, RectangleF r, float s)
        {
            SkinDraw.Text(g, label, f.Small, p.Main, new RectangleF(r.X, r.Y, r.Width / 2f, r.Height), StringAlignment.Near);
            SkinDraw.Text(g, has ? Num(pct) + "%" : "—", f.Label, has ? Lv(p, pct) : p.Dim, new RectangleF(r.X + r.Width / 2f - 58f * s, r.Y, 58f * s, r.Height), StringAlignment.Far);
            SkinDraw.Fit(g, clock + (left.Length > 0 ? " · " + left : ""), f.Small, p.Dim, new RectangleF(r.X + r.Width / 2f + 6f * s, r.Y, r.Width / 2f - 6f * s, r.Height), StringAlignment.Far);
            using (Pen line = new Pen(p.Track, 1f * s))
            {
                g.DrawLine(line, r.X, r.Bottom, r.Right, r.Bottom);
            }
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float y0 = cell.Y + 3f * s;
            float lineY = y0 + 13f * s;
            float cx = cell.X + cell.Width / 2f;
            using (Pen line = new Pen(p.Main, 1f * s))
            {
                g.DrawLine(line, cell.X, lineY, cell.Right, lineY);
            }
            float dd = (6f + 22f * (float)t.Fill) * s;
            RectangleF r = new RectangleF(cx - dd / 2f, lineY - dd / 2f, dd, dd);
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
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 30f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            SkinDraw.Fit(g, t.Value, f.Label, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, y0 + 44f * s, cell.Width, 16f * s), StringAlignment.Center);
        }

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

    // ---------------------------------------------------------------- F 陈列线（小人排成一排：亮起的个数 = 用量）

    internal sealed class SkinF : Skin
    {
        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            return new PointF(SkinDraw.TextEnd(g, "STILLA", f.Display, 14f * s + 8f * s) + 9f * s, 19f * s);
        }

        public override string Name { get { return "3-F 陈列线"; } }
        public override int CompactH { get { return 292; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(18, 18, 18), Surface = SkinDraw.C(30, 30, 30), Border = SkinDraw.C(50, 50, 50),
                    Main = SkinDraw.C(246, 246, 245), Dim = SkinDraw.C(120, 120, 118), Track = SkinDraw.C(52, 52, 52),
                    Accent = SkinDraw.C(240, 80, 50), Ok = SkinDraw.C(246, 246, 245), Warn = SkinDraw.C(240, 80, 50), Bad = SkinDraw.C(240, 80, 50)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(240, 240, 238), Surface = SkinDraw.C(255, 255, 255), Border = SkinDraw.C(224, 224, 222),
                Main = SkinDraw.C(12, 12, 12), Dim = SkinDraw.C(155, 155, 153), Track = SkinDraw.C(230, 230, 228),
                Accent = SkinDraw.C(217, 56, 30), Ok = SkinDraw.C(12, 12, 12), Warn = SkinDraw.C(217, 56, 30), Bad = SkinDraw.C(217, 56, 30)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            SkinDraw.Rr(g, p.Surface, new RectangleF(pad, 6f * s, inner, 26f * s), 0f);
            SkinDraw.Text(g, "STILLA", f.Display, p.Main, new RectangleF(pad + 8f * s, 6f * s, 120f * s, 26f * s), StringAlignment.Near);
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "刷新", f.Small, p.Dim, refresh, StringAlignment.Center);
            SkinDraw.Text(g, d.Alert ? d.Foot : "QUOTA · 更新 " + d.Updated, f.Small, d.Alert ? d.FootColor : p.Dim, new RectangleF(pad, 40f * s, inner, 16f * s), StringAlignment.Near);
            using (Font title = SkinDraw.F("Segoe UI Black", 34f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, "QUOTA", title, p.Main, new RectangleF(pad, 52f * s, inner, 40f * s), StringAlignment.Near);
            }
            Item(g, "5 小时额度", d.P5, d.Has5, d.Clock5, d.Left5, 100f * s, pad, inner, f, p, s);
            Item(g, "每周额度", d.PW, d.HasW, d.ClockW, d.LeftW, 186f * s, pad, inner, f, p, s);
            SkinDraw.Rr(g, p.Surface, new RectangleF(pad, 258f * s, inner, 26f * s), 0f);
            SkinDraw.Text(g, d.Source + " · 不耗 token", f.Small, p.Dim, new RectangleF(pad + 8f * s, 258f * s, inner * 0.6f, 26f * s), StringAlignment.Near);
            SkinDraw.Text(g, "用量统计 →", f.Small, p.Main, new RectangleF(pad, 258f * s, inner - 8f * s, 26f * s), StringAlignment.Far);
        }

        private static void Item(Graphics g, string label, double pct, bool has, string clock, string left, float y, float pad, float inner, Fonts f, SkinPalette p, float s)
        {
            Color c = has ? Lv(p, pct) : p.Dim;
            SkinDraw.Text(g, label, f.Small, p.Main, new RectangleF(pad, y, inner * 0.6f, 16f * s), StringAlignment.Near);
            using (Font big = SkinDraw.F("Segoe UI Black", 22f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, has ? Num(pct) + "%" : "—", big, c, new RectangleF(pad, y - 4f * s, inner, 26f * s), StringAlignment.Far);
            }
            int lit = has ? (int)Math.Round(Math.Max(0.0, Math.Min(100.0, pct)) / 5.0) : 0;
            float step = inner / 20f;
            for (int j = 0; j < 20; j++)
            {
                float x = pad + j * step + step / 2f;
                bool on = j < lit;
                Color col = on ? c : p.Track;
                using (SolidBrush b = new SolidBrush(col))
                {
                    g.FillEllipse(b, x - 2.5f * s, y + 22f * s, 5f * s, 5f * s);
                }
                using (Pen stem = new Pen(col, 2f * s))
                {
                    g.DrawLine(stem, x, y + 28f * s, x, y + 48f * s);
                }
            }
            SkinDraw.Text(g, "重置 " + clock, f.Small, p.Dim, new RectangleF(pad, y + 54f * s, inner / 2f, 16f * s), StringAlignment.Near);
            SkinDraw.Text(g, left, f.Small, p.Main, new RectangleF(pad + inner / 2f, y + 54f * s, inner / 2f, 16f * s), StringAlignment.Far);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float cx = cell.X + cell.Width / 2f;
            float h = (8f + 26f * (float)t.Fill) * s;
            Color col = t.Selected ? p.Accent : p.Main;
            using (SolidBrush b = new SolidBrush(col))
            {
                g.FillEllipse(b, cx - 2.5f * s, cell.Y + 4f * s, 5f * s, 5f * s);
            }
            using (Pen stem = new Pen(col, 2f * s))
            {
                g.DrawLine(stem, cx, cell.Y + 9f * s, cx, cell.Y + 9f * s + h);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, cell.Y + 38f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            using (Font vf = SkinDraw.F("Segoe UI Black", 11.5f * s, FontStyle.Italic))
            {
                SkinDraw.Fit(g, t.Value, vf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, cell.Y + 52f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            using (Font title = SkinDraw.F("Segoe UI Black", 14f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, "STILLA", title, p.Main, new RectangleF(x, y, 120f * s, 22f * s), StringAlignment.Near);
            }
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            for (int i = 0; i < rects.Length; i++)
            {
                SkinDraw.Text(g, names[i], f.Small, i == sel ? p.Main : p.Dim, rects[i], StringAlignment.Center);
                if (i == sel)
                {
                    using (Pen u = new Pen(p.Main, 1f * s))
                    {
                        g.DrawLine(u, rects[i].X + 6f * s, rects[i].Bottom - 3f * s, rects[i].Right - 6f * s, rects[i].Bottom - 3f * s);
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- G 图录（深绿底、细线框、衬线斜体、罗马数字）

    internal sealed class SkinG : Skin
    {
        public override bool ShowAccountChip { get { return false; } }

        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            using (Font bold = SkinDraw.F("Segoe UI", 17f * s, FontStyle.Bold))
            using (Font serif = SkinDraw.F("Georgia", 17f * s, FontStyle.Italic))
            {
                float x1 = 14f * s + 4f * s;
                float wa = g.MeasureString("The Quota ", bold).Width;
                return new PointF(SkinDraw.TextEnd(g, "Edition", serif, x1 + wa) + 10f * s, 29f * s);
            }
        }

        public override string Name { get { return "3-G 图录"; } }
        public override int CompactH { get { return 264; } }
        public override string DisplayFamily { get { return "Georgia"; } }
        public override FontStyle DisplayStyle { get { return FontStyle.Italic; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(16, 25, 18), Surface = SkinDraw.C(27, 42, 32), Border = SkinDraw.A(120, SkinDraw.C(239, 230, 210)),
                    Main = SkinDraw.C(239, 230, 210), Dim = SkinDraw.C(160, 154, 138), Track = SkinDraw.C(70, 84, 74),
                    Accent = SkinDraw.C(238, 127, 104), Ok = SkinDraw.C(239, 230, 210), Warn = SkinDraw.C(238, 127, 104), Bad = SkinDraw.C(255, 90, 122)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(239, 230, 210), Surface = SkinDraw.C(246, 240, 226), Border = SkinDraw.A(120, SkinDraw.C(27, 42, 32)),
                Main = SkinDraw.C(27, 42, 32), Dim = SkinDraw.C(110, 112, 98), Track = SkinDraw.C(205, 198, 180),
                Accent = SkinDraw.C(176, 80, 60), Ok = SkinDraw.C(27, 42, 32), Warn = SkinDraw.C(176, 80, 60), Bad = SkinDraw.C(170, 40, 60)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            Card(g, p, new RectangleF(0f, 0f, w, CompactH * s), s);
            using (Font bold = SkinDraw.F("Segoe UI", 17f * s, FontStyle.Bold))
            using (Font serif = SkinDraw.F("Georgia", 17f * s, FontStyle.Italic))
            {
                float x1 = pad + 4f * s;
                SkinDraw.Text(g, "The Quota ", bold, p.Main, new RectangleF(x1, 14f * s, 200f * s, 30f * s), StringAlignment.Near);
                float wa = g.MeasureString("The Quota ", bold).Width;
                SkinDraw.Text(g, "Edition", serif, p.Main, new RectangleF(x1 + wa, 14f * s, 200f * s, 30f * s), StringAlignment.Near);
            }
            using (Font sub = SkinDraw.F("Georgia", 11.5f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, "Five hours, one week, two clocks.", sub, p.Dim, new RectangleF(pad + 4f * s, 44f * s, inner, 16f * s), StringAlignment.Near);
            }
            // 两个按钮正好压在内侧那条边框线上，先垫一块纸色，文字才不会被线划过
            RectangleF btnBack = RectangleF.Union(refresh, close);
            btnBack.Inflate(2f * s, 0f);
            SkinDraw.Rr(g, p.Surface, btnBack, 0f);
            SkinDraw.Text(g, "刷新", f.Small, p.Dim, refresh, StringAlignment.Center);
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            Item(g, "5 小时额度", d.P5, d.Has5, d.Clock5, d.Left5, "I", 70f * s, pad, inner, p, s);
            Item(g, "每周额度", d.PW, d.HasW, d.ClockW, d.LeftW, "II", 150f * s, pad, inner, p, s);
            using (Font foot = SkinDraw.F("Georgia", 10.5f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, d.Alert ? d.Foot : "更新于 " + d.Updated + " · " + d.Source + " · 不耗 token", foot, d.Alert ? d.FootColor : p.Dim, new RectangleF(pad + 4f * s, 226f * s, inner, 16f * s), StringAlignment.Near);
            }
        }

        private static void Card(Graphics g, SkinPalette p, RectangleF r, float s)
        {
            RectangleF outer = new RectangleF(r.X + 6f * s, r.Y + 6f * s, r.Width - 12f * s, r.Height - 12f * s);
            SkinDraw.Rr(g, p.Surface, outer, 0f);
            SkinDraw.RrLine(g, p.Border, outer, 0f, 1f * s);
            SkinDraw.RrLine(g, p.Border, new RectangleF(r.X + 15f * s, r.Y + 15f * s, r.Width - 30f * s, r.Height - 30f * s), 0f, 1f * s);
        }

        private static void Item(Graphics g, string label, double pct, bool has, string clock, string left, string roman, float y, float pad, float inner, SkinPalette p, float s)
        {
            Color c = has ? Lv(p, pct) : p.Dim;
            using (Font lf = SkinDraw.F("Segoe UI", 16f * s, FontStyle.Bold))
            {
                SkinDraw.Text(g, label, lf, p.Main, new RectangleF(pad + 4f * s, y, inner * 0.6f, 24f * s), StringAlignment.Near);
            }
            using (Font serif = SkinDraw.F("Georgia", 19f * s, FontStyle.Italic))
            using (Font rn = SkinDraw.F("Georgia", 11f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, has ? Num(pct) + "%" : "—", serif, c, new RectangleF(pad, y, inner - 44f * s, 24f * s), StringAlignment.Far);
                SkinDraw.Text(g, roman, rn, p.Dim, new RectangleF(pad + inner - 34f * s, y + 4f * s, 26f * s, 16f * s), StringAlignment.Far);
            }
            RectangleF bar = new RectangleF(pad + 4f * s, y + 30f * s, inner - 8f * s, 2f * s);
            SkinDraw.Rr(g, p.Track, bar, 0f);
            if (has)
            {
                float fill = bar.Width * (float)(Math.Max(0.0, Math.Min(100.0, pct)) / 100.0);
                if (fill > 0f)
                {
                    SkinDraw.Rr(g, c, new RectangleF(bar.X, bar.Y, fill, bar.Height), 0f);
                }
            }
            using (Font meta = SkinDraw.F("Georgia", 11f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, "重置 " + clock + (left.Length > 0 ? " — " + left : ""), meta, p.Dim, new RectangleF(pad + 4f * s, y + 38f * s, inner, 16f * s), StringAlignment.Near);
            }
        }

        private static Font SkinFont(float s, float px, FontStyle st)
        {
            return SkinDraw.F("Segoe UI", px * s, st);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            float x = cell.X + 6f * s;
            using (Pen top = new Pen(t.Selected ? p.Accent : SkinDraw.A(90, p.Main), (t.Selected ? 1.5f : 1f) * s))
            {
                g.DrawLine(top, cell.X, cell.Y + 2f * s, cell.Right, cell.Y + 2f * s);
            }
            using (Font rn = SkinDraw.F("Georgia", 10f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, t.Roman, rn, p.Dim, new RectangleF(x, cell.Y + 8f * s, cell.Width, 14f * s), StringAlignment.Near);
            }
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(x, cell.Y + 20f * s, cell.Width, 14f * s), StringAlignment.Near);
            }
            using (Font vf = SkinDraw.F("Georgia", 12f * s, FontStyle.Italic))
            {
                SkinDraw.Fit(g, t.Value, vf, t.Selected ? p.Accent : p.Dim, new RectangleF(x, cell.Y + 36f * s, cell.Width, 16f * s), StringAlignment.Near);
            }
        }

        public override void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s)
        {
            Card(g, p, rect, s);
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            using (Font bold = SkinDraw.F("Segoe UI", 17f * s, FontStyle.Bold))
            using (Font serif = SkinDraw.F("Georgia", 17f * s, FontStyle.Italic))
            {
                SkinDraw.Text(g, "The Usage ", bold, p.Main, new RectangleF(x, y, 200f * s, 24f * s), StringAlignment.Near);
                float wa = g.MeasureString("The Usage ", bold).Width;
                SkinDraw.Text(g, "Edition", serif, p.Main, new RectangleF(x + wa, y, 200f * s, 24f * s), StringAlignment.Near);
            }
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            using (Font serif = SkinDraw.F("Georgia", 12.5f * s, FontStyle.Italic))
            {
                for (int i = 0; i < rects.Length; i++)
                {
                    SkinDraw.Text(g, names[i], serif, i == sel ? p.Main : p.Dim, rects[i], StringAlignment.Center);
                    if (i == sel)
                    {
                        using (Pen u = new Pen(p.Accent, 1f * s))
                        {
                            g.DrawLine(u, rects[i].X + 4f * s, rects[i].Bottom - 2f * s, rects[i].Right - 4f * s, rects[i].Bottom - 2f * s);
                        }
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- H 彩盘与药丸（圆盘里液面升起，药丸标签）

    internal sealed class SkinH : Skin
    {
        public override bool ShowAccountChip { get { return false; } }

        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            using (Font logo = SkinDraw.F("Segoe UI Black", 21f * s, FontStyle.Regular))
            {
                return new PointF(SkinDraw.TextEnd(g, "quota", logo, 14f * s + 4f * s) + 10f * s, 27f * s);
            }
        }

        public override string Name { get { return "3-H 彩盘与药丸"; } }
        public override int CompactH { get { return 282; } }
        public override string DisplayFamily { get { return "Segoe UI Black"; } }
        public override FontStyle DisplayStyle { get { return FontStyle.Regular; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(18, 18, 18), Surface = SkinDraw.C(34, 34, 34), Border = SkinDraw.C(60, 60, 60),
                    Main = SkinDraw.C(246, 246, 244), Dim = SkinDraw.C(130, 130, 128), Track = SkinDraw.C(52, 52, 52),
                    Accent = SkinDraw.C(246, 246, 244), Ok = SkinDraw.C(215, 243, 28), Warn = SkinDraw.C(255, 106, 61), Bad = SkinDraw.C(255, 61, 90)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(235, 235, 233), Surface = SkinDraw.C(255, 255, 255), Border = SkinDraw.C(235, 235, 233),
                Main = SkinDraw.C(11, 11, 11), Dim = SkinDraw.C(154, 154, 152), Track = SkinDraw.C(240, 240, 238),
                Accent = SkinDraw.C(11, 11, 11), Ok = SkinDraw.C(215, 243, 28), Warn = SkinDraw.C(255, 106, 61), Bad = SkinDraw.C(255, 61, 90)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            SkinDraw.Rr(g, p.Surface, new RectangleF(4f * s, 4f * s, w - 8f * s, CompactH * s - 8f * s), 26f * s);
            using (Font logo = SkinDraw.F("Segoe UI Black", 21f * s, FontStyle.Regular))
            {
                SkinDraw.Text(g, "quota", logo, p.Main, new RectangleF(pad + 4f * s, 14f * s, 120f * s, 26f * s), StringAlignment.Near);
            }
            SkinDraw.Rr(g, p.Track, refresh, refresh.Height / 2f);
            SkinDraw.Text(g, "刷新", f.Small, p.Main, refresh, StringAlignment.Center);
            SkinDraw.Rr(g, p.Main, close, close.Height / 2f);
            SkinDraw.Text(g, "×", f.Label, p.Bg, close, StringAlignment.Center);

            float D = Math.Min((inner - 10f * s) / 2f, 118f * s);
            float x0 = pad + (inner - (2f * D + 10f * s)) / 2f;
            float y = 48f * s;
            Disc(g, x0, y, D, d.P5, d.Has5, "5 小时额度", d.Clock5, d.Left5, f, p, s);
            Disc(g, x0 + D + 10f * s, y, D, d.PW, d.HasW, "每周额度", d.ClockW, d.LeftW, f, p, s);
            SkinDraw.Fit(g, d.Alert ? d.Foot : "更新于 " + d.Updated + " · " + d.Source + " · 不耗 token", f.Small, d.Alert ? d.FootColor : p.Dim, new RectangleF(pad, 254f * s, inner, 16f * s), StringAlignment.Center);
        }

        private static void Disc(Graphics g, float x, float y, float D, double pct, bool has, string label, string clock, string left, Fonts f, SkinPalette p, float s)
        {
            Color c = has ? Lv(p, pct) : p.Dim;
            RectangleF r = new RectangleF(x, y, D, D);
            using (SolidBrush bg = new SolidBrush(c))
            {
                g.FillEllipse(bg, r);
            }
            Color ink = SkinDraw.C(11, 11, 11);
            double pc = has ? Math.Max(0.0, Math.Min(100.0, pct)) : 0.0;
            float liquid = y + D * (float)(1.0 - pc / 100.0);
            GraphicsState st = g.Save();
            using (GraphicsPath cp = new GraphicsPath())
            {
                cp.AddEllipse(r);
                g.SetClip(cp);
            }
            using (SolidBrush lb = new SolidBrush(ink))
            {
                g.FillRectangle(lb, x - 1f, liquid, D + 2f, y + D - liquid + 2f);
            }
            g.Restore(st);
            using (Font big = SkinDraw.F("Segoe UI Black", 34f * s, FontStyle.Regular))
            {
                string num = has ? Num(pct) + "%" : "—";
                SkinDraw.SplitText(g, num, big, ink, c, x + D / 2f, y + D / 2f, liquid);
            }
            SkinDraw.Text(g, label, f.Small, p.Dim, new RectangleF(x, y + D + 8f * s, D, 16f * s), StringAlignment.Near);
            SkinDraw.Fit(g, clock, f.Label, p.Main, new RectangleF(x, y + D + 24f * s, D, 18f * s), StringAlignment.Near);
            SkinDraw.Fit(g, left, f.Small, p.Dim, new RectangleF(x, y + D + 42f * s, D, 16f * s), StringAlignment.Near);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            RectangleF r = new RectangleF(cell.X + 2f * s, cell.Y + 12f * s, cell.Width - 4f * s, 28f * s);
            SkinDraw.Rr(g, t.Selected ? p.Main : p.Track, r, r.Height / 2f);
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Bg : p.Dim, r, StringAlignment.Center);
            }
        }

        public override void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s)
        {
            SkinDraw.Rr(g, p.Surface, new RectangleF(rect.X + 4f * s, rect.Y + 4f * s, rect.Width - 8f * s, rect.Height - 8f * s), 26f * s);
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            using (Font logo = SkinDraw.F("Segoe UI Black", 21f * s, FontStyle.Regular))
            {
                SkinDraw.Text(g, "usage", logo, p.Main, new RectangleF(x, y, 120f * s, 26f * s), StringAlignment.Near);
            }
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            for (int i = 0; i < rects.Length; i++)
            {
                bool on = i == sel;
                SkinDraw.Rr(g, on ? p.Main : p.Track, rects[i], rects[i].Height / 2f);
                SkinDraw.Text(g, names[i], f.Small, on ? p.Bg : p.Dim, rects[i], StringAlignment.Center);
            }
        }
    }

    // ---------------------------------------------------------------- I 墨滴（两滴墨粘在一起，粗斜体，没有框）

    internal sealed class SkinI : Skin
    {
        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            return new PointF(SkinDraw.TextEnd(g, "STILLA", f.Display, 14f * s + 30f * s) + 10f * s, 21f * s);
        }

        public override string Name { get { return "3-I 墨滴"; } }
        public override int CompactH { get { return 240; } }
        public override float ShareWidth { get { return 44f; } }

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(16, 16, 16), Surface = SkinDraw.C(26, 26, 26), Border = SkinDraw.C(52, 52, 52),
                    Main = SkinDraw.C(250, 250, 250), Dim = SkinDraw.C(130, 130, 128), Track = SkinDraw.C(42, 42, 42),
                    Accent = SkinDraw.C(250, 250, 250), Ok = SkinDraw.C(250, 250, 250), Warn = SkinDraw.C(250, 250, 250), Bad = SkinDraw.C(250, 250, 250)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(246, 246, 244), Surface = SkinDraw.C(255, 255, 255), Border = SkinDraw.C(232, 232, 230),
                Main = SkinDraw.C(11, 11, 11), Dim = SkinDraw.C(143, 143, 141), Track = SkinDraw.C(237, 237, 235),
                Accent = SkinDraw.C(11, 11, 11), Ok = SkinDraw.C(11, 11, 11), Warn = SkinDraw.C(11, 11, 11), Bad = SkinDraw.C(11, 11, 11)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            float inner = w - 2f * pad;
            SkinDraw.Rr(g, p.Surface, new RectangleF(6f * s, 6f * s, w - 12f * s, CompactH * s - 12f * s), 16f * s);
            SkinDraw.RrLine(g, p.Border, new RectangleF(6f * s, 6f * s, w - 12f * s, CompactH * s - 12f * s), 16f * s, 1f * s);
            InkPainter.DrawLogo(g, pad + 4f * s, 14f * s, s, p.Main);
            SkinDraw.Text(g, "STILLA", f.Display, p.Main, new RectangleF(pad + 30f * s, 10f * s, 120f * s, 22f * s), StringAlignment.Near);
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "刷新", f.Small, p.Dim, refresh, StringAlignment.Center);
            using (Font blobFont = SkinDraw.F("Segoe UI Black", 24f * s, FontStyle.Italic))
            {
                SkinDraw.Blobs(g, new RectangleF(pad, 40f * s, inner, 96f * s), d.P5, d.PW, 40f * s, p.Main, p.Surface, s, blobFont,
                    d.Has5 ? Num(d.P5) : "—", d.HasW ? Num(d.PW) : "—", inner * 0.34f, inner * 0.66f);
            }
            Row(g, f, p, "5H", d.P5, d.Has5, "5 小时 · 重置 " + d.Clock5, d.Left5, 140f * s, pad, inner, s);
            Row(g, f, p, "7D", d.PW, d.HasW, "每周 · 重置 " + d.ClockW, d.LeftW, 182f * s, pad, inner, s);
            SkinDraw.Text(g, d.Foot, f.Small, d.FootColor, new RectangleF(pad, 214f * s, inner, 16f * s), StringAlignment.Near);
        }

        private static void Row(Graphics g, Fonts f, SkinPalette p, string tag, double pct, bool has, string caption, string left, float y, float pad, float inner, float s)
        {
            SkinDraw.Text(g, tag, f.Display, p.Dim, new RectangleF(pad, y, 30f * s, 22f * s), StringAlignment.Near);
            using (Font big = SkinDraw.F("Segoe UI Black", 24f * s, FontStyle.Italic))
            {
                SkinDraw.Fit(g, has ? Num(pct) + "%" : "—", big, p.Main, new RectangleF(pad + 30f * s, y - 4f * s, 74f * s, 30f * s), StringAlignment.Near);
            }
            SkinDraw.Fit(g, caption, f.Small, p.Dim, new RectangleF(pad + 106f * s, y, inner - 106f * s, 16f * s), StringAlignment.Far);
            SkinDraw.Fit(g, left, f.Small, p.Main, new RectangleF(pad + 106f * s, y + 14f * s, inner - 106f * s, 16f * s), StringAlignment.Far);
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label, lf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, cell.Y + 6f * s, cell.Width, 14f * s), StringAlignment.Center);
            }
            using (Font vf = SkinDraw.F("Segoe UI Black", 14f * s, FontStyle.Italic))
            {
                SkinDraw.Fit(g, t.Value, vf, t.Selected ? p.Main : p.Dim, new RectangleF(cell.X, cell.Y + 22f * s, cell.Width, 20f * s), StringAlignment.Center);
            }
            if (t.Selected)
            {
                SkinDraw.Rr(g, p.Main, new RectangleF(cell.X + cell.Width / 2f - 10f * s, cell.Y + 44f * s, 20f * s, 5f * s), 2.5f * s);
            }
        }

        public override void DrawShare(Graphics g, float x, float y, double share, SkinPalette p, float s)
        {
            float wdt = (4f + 40f * (float)Math.Max(0.0, Math.Min(1.0, share))) * s;
            using (SolidBrush b = new SolidBrush(p.Main))
            {
                g.FillEllipse(b, x, y + 2f * s, wdt, 7f * s);
            }
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            for (int i = 0; i < rects.Length; i++)
            {
                SkinDraw.Text(g, names[i].ToUpperInvariant(), f.Small, i == sel ? p.Main : p.Dim, rects[i], StringAlignment.Center);
            }
        }
    }

    // ---------------------------------------------------------------- J 拼贴（四块有颜色的色块、大写粗体名字、彩色标签）

    internal sealed class SkinJ : Skin
    {
        public override bool ShowAccountChip { get { return false; } }     // 顶部正中有个「+」，标签会压在它和「刷新」上

        public override PointF BadgeCenter(Graphics g, Fonts f, float s, float w, RectangleF close, RectangleF refresh)
        {
            return new PointF(w / 2f + 16f * s, 19f * s);
        }

        public override string Name { get { return "3-J 拼贴"; } }
        public override int CompactH { get { return 366; } }
        public override string DisplayFamily { get { return "Segoe UI Black"; } }
        public override FontStyle DisplayStyle { get { return FontStyle.Regular; } }

        private static readonly Color[] TileBg =
        {
            SkinDraw.C(47, 74, 71), SkinDraw.C(205, 176, 138), SkinDraw.C(37, 38, 42),
            SkinDraw.C(176, 80, 60), SkinDraw.C(125, 143, 126), SkinDraw.C(92, 77, 99)
        };

        private static readonly Color[] TileFg =
        {
            SkinDraw.C(235, 240, 236), SkinDraw.C(20, 20, 20), SkinDraw.C(235, 240, 236),
            SkinDraw.C(250, 244, 238), SkinDraw.C(250, 250, 248), SkinDraw.C(250, 244, 238)
        };

        public override SkinPalette Palette(bool dark)
        {
            if (dark)
            {
                return new SkinPalette
                {
                    Bg = SkinDraw.C(30, 28, 26), Surface = SkinDraw.C(40, 37, 34), Border = SkinDraw.C(70, 66, 60),
                    Main = SkinDraw.C(240, 236, 226), Dim = SkinDraw.C(140, 136, 128), Track = SkinDraw.C(60, 56, 52),
                    Accent = SkinDraw.C(200, 110, 90), Ok = SkinDraw.C(240, 236, 226), Warn = SkinDraw.C(200, 110, 90), Bad = SkinDraw.C(200, 110, 90)
                };
            }
            return new SkinPalette
            {
                Bg = SkinDraw.C(232, 229, 222), Surface = SkinDraw.C(246, 244, 239), Border = SkinDraw.C(220, 216, 207),
                Main = SkinDraw.C(20, 20, 20), Dim = SkinDraw.C(141, 138, 130), Track = SkinDraw.C(231, 227, 217),
                Accent = SkinDraw.C(176, 80, 60), Ok = SkinDraw.C(20, 20, 20), Warn = SkinDraw.C(176, 80, 60), Bad = SkinDraw.C(176, 80, 60)
            };
        }

        public override void DrawCompact(Graphics g, CompactData d, Fonts f, SkinPalette p, RectangleF close, RectangleF refresh, float s, float w)
        {
            float pad = 14f * s;
            SkinDraw.Rr(g, p.Surface, new RectangleF(6f * s, 6f * s, w - 12f * s, CompactH * s - 12f * s), 0f);
            SkinDraw.RrLine(g, p.Border, new RectangleF(6f * s, 6f * s, w - 12f * s, CompactH * s - 12f * s), 0f, 1f * s);
            SkinDraw.Text(g, "+", f.Display, p.Main, new RectangleF(w / 2f - 20f * s, 4f * s, 40f * s, 30f * s), StringAlignment.Center);
            SkinDraw.Text(g, "×", f.Label, p.Dim, close, StringAlignment.Center);
            SkinDraw.Text(g, "刷新", f.Small, p.Dim, refresh, StringAlignment.Center);

            Tile(g, new RectangleF(pad, 46f * s, 120f * s, 92f * s), 0, "5H · 5 小时", d.Has5 ? Num(d.P5) + "%" : "—", f, s);
            Tile(g, new RectangleF(w - pad - 102f * s, 74f * s, 102f * s, 130f * s), 1, "7D · 每周", d.HasW ? Num(d.PW) + "%" : "—", f, s);
            Tile(g, new RectangleF(pad, 232f * s, 104f * s, 98f * s), 2, "5H 重置", d.Clock5, f, s, d.Left5);
            string[] wk = d.ClockW.Split(new char[] { ' ' }, 2);
            Tile(g, new RectangleF(w - pad - 120f * s, 220f * s, 120f * s, 98f * s), 3, "7D 重置", wk[0], f, s, (wk.Length > 1 ? wk[1] + "  " : "") + d.LeftW);

            using (Font name = SkinDraw.F("Segoe UI Black", 26f * s, FontStyle.Regular))
            {
                SkinDraw.Text(g, "STILLA", name, p.Main, new RectangleF(pad + 1f * s, 150f * s, 130f * s, 28f * s), StringAlignment.Near);
                SkinDraw.Text(g, "QUOTA", name, p.Main, new RectangleF(pad + 1f * s, 176f * s, 130f * s, 28f * s), StringAlignment.Near);
            }
            SkinDraw.Fit(g, d.Alert ? d.Foot : "更新于 " + d.Updated + " · " + d.Source, f.Small, d.Alert ? d.FootColor : p.Dim, new RectangleF(pad, 344f * s, w - 2f * pad, 16f * s), StringAlignment.Center);
        }

        private static void Tile(Graphics g, RectangleF r, int i, string label, string value, Fonts f, float s, string sub = null)
        {
            SkinDraw.Rr(g, TileBg[i], r, 0f);
            Color fg = TileFg[i];
            SkinDraw.Text(g, label, f.Small, fg, new RectangleF(r.X + 10f * s, r.Y + 8f * s, r.Width - 20f * s, 16f * s), StringAlignment.Near);
            if (sub == null)
            {
                using (Font big = SkinDraw.F("Segoe UI Black", 33f * s, FontStyle.Regular))
                {
                    SkinDraw.Fit(g, value, big, fg, new RectangleF(r.X + 10f * s, r.Y + 28f * s, r.Width - 20f * s, 40f * s), StringAlignment.Near);
                }
            }
            else
            {
                using (Font big = SkinDraw.F("Segoe UI Black", 23f * s, FontStyle.Regular))
                {
                    SkinDraw.Fit(g, value, big, fg, new RectangleF(r.X + 10f * s, r.Y + 28f * s, r.Width - 20f * s, 30f * s), StringAlignment.Near);
                }
                SkinDraw.Fit(g, sub, f.Small, fg, new RectangleF(r.X + 10f * s, r.Y + 56f * s, r.Width - 20f * s, 16f * s), StringAlignment.Near);
            }
        }

        public override void DrawTab(Graphics g, RectangleF cell, TabData t, Fonts f, SkinPalette p, float s)
        {
            int i = t.Index % 6;
            float lift = t.Selected ? 6f * s : 0f;
            float h = t.Selected ? 58f * s : 52f * s;
            RectangleF r = new RectangleF(cell.X + 2f * s, cell.Y + 2f * s + (t.Selected ? 0f : 0f) - lift, cell.Width - 4f * s, h);
            SkinDraw.Rr(g, TileBg[i], r, 0f);
            Color fg = TileFg[i];
            using (Font lf = ShrinkFont(f.Small, t.LabelScale))
            {
                SkinDraw.Text(g, t.Label.ToUpperInvariant(), lf, fg, new RectangleF(r.X + 6f * s, r.Y + 6f * s, r.Width - 12f * s, 14f * s), StringAlignment.Near);
            }
            using (Font vf = SkinDraw.F("Segoe UI Black", 11f * s, FontStyle.Regular))
            {
                SkinDraw.Fit(g, t.Value, vf, fg, new RectangleF(r.X + 4f * s, r.Bottom - 24f * s, r.Width - 6f * s, 20f * s), StringAlignment.Near);
            }
        }

        public override void DrawFrame(Graphics g, RectangleF rect, SkinPalette p, float s)
        {
            RectangleF r = new RectangleF(rect.X + 6f * s, rect.Y + 6f * s, rect.Width - 12f * s, rect.Height - 12f * s);
            SkinDraw.Rr(g, p.Surface, r, 0f);
            SkinDraw.RrLine(g, p.Border, r, 0f, 1f * s);
        }

        public override void DrawTitle(Graphics g, float x, float y, string text, Fonts f, SkinPalette p, float s)
        {
            SkinDraw.Text(g, "STILLA QUOTA", f.Display, p.Main, new RectangleF(x, y, 240f * s, 24f * s), StringAlignment.Near);
        }

        public override void DrawModes(Graphics g, RectangleF[] rects, string[] names, int sel, Fonts f, SkinPalette p, float s)
        {
            for (int i = 0; i < rects.Length; i++)
            {
                SkinDraw.Text(g, names[i].ToUpperInvariant(), f.Small, i == sel ? p.Main : p.Dim, rects[i], StringAlignment.Center);
                if (i == sel)
                {
                    using (Pen u = new Pen(p.Main, 1.5f * s))
                    {
                        g.DrawLine(u, rects[i].X + 4f * s, rects[i].Bottom - 2f * s, rects[i].Right - 4f * s, rects[i].Bottom - 2f * s);
                    }
                }
            }
        }
    }
}
