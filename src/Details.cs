// 模型视图里点一个会话弹出的小窗口：这个会话在当前时间范围里，每个「模型 × 档位」的详细数据，
// 以及中途切换档位 / 模型的时间线（按先后，每一段的起止时间和用量）。可以复制成文本留存。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace Stilla
{
    // 弹窗用的配色（取自悬浮窗当前的皮肤和黑白设置）
    internal sealed class PopupColors
    {
        public Color Bg, Border, Main, Dim, Track, Amber;
    }

    // 表里的一行（已经算好、可以直接显示）
    internal sealed class DetailRow
    {
        public string Tier = "", Model = "", Span = "";
        public int Rank;
        public long Msgs, In, Out, Think, Cw, Cr;
        public string Pct5 = "—", PctW = "—", Usd = "—";
        public bool Bold;
    }

    // 一段连续的 模型 + 档位（中间没换过）。ReadRuns 从日志里按时间顺序切出来
    internal sealed class SessionRun
    {
        public string Model = "", Tier = "";
        public bool Sub;                                                         // 子任务（子智能体 / workflow）里的，按 模型+档位 合并成一行
        public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;     // 本地时间
        public long Msgs, Think;
        public readonly long[] Tok = new long[10];
    }

    // 会话视图「速度」的统计窗口：自定义分钟数（1~2880，最长 48 小时）
    internal sealed class MinutesForm : Form
    {
        private readonly NumericUpDown num = new NumericUpDown();

        public int Minutes
        {
            get { return (int)num.Value; }
        }

        public MinutesForm(int current)
        {
            Text = "速度统计窗口";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            DialogDpi.Apply(this);
            ClientSize = new Size(360, 120);
            Label l = new Label();
            l.Text = "按最近多少分钟的消耗来算速度：";
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
            u.Text = "分钟（1~2880，最长 48 小时）";
            u.Location = new Point(112, 46);
            u.AutoSize = true;
            Controls.Add(u);
            Button ok = new Button();
            ok.Text = "确定";
            ok.Location = new Point(184, 80);
            ok.Size = new Size(80, 28);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "取消";
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
        private static readonly string[] Heads = { "档位", "模型", "时间段", "条数", "输入", "输出", "思考", "缓存写", "缓存读", "5h估算", "周估算", "API等价" };
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
            float h = pad + 22f * s + 20f * s;                       // 标题、说明
            h += SectionHeight(tiers.Count);
            if (runs != null && runs.Count > 0)
            {
                h += SectionHeight(runs.Count) + (runsHidden > 0 ? rowH : 0);
            }
            h += 40f * s;                                            // 底部按钮
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
            return 22f * s + rowH * (rows + 1) + 8f * s;           // 小标题 + 表头 + 行
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
                    // 剪贴板正被别的程序占用
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = btnClose.Contains(e.Location) || btnDrill.Contains(e.Location) || btnCopy.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        // ------------------------------------------------------------ 绘制
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

        // 档位小标签：和模型视图里一样的样式，左对齐画在 x 处
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
                        Str(g, "合计", fBold, c.Main, cell, StringAlignment.Near);
                    }
                    else if (r.Tier.Length > 0 && r.Tier != "未标注")
                    {
                        Badge(g, r.Tier, r.Rank, x, y);
                    }
                    else
                    {
                        Str(g, "未标注", fSmall, c.Dim, cell, StringAlignment.Near);
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
            Str(g, label, fBold, c.Main, new RectangleF(pad, y, 400f * s, 20f * s), StringAlignment.Near);
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
            Str(g, range + "　条数 = API 消息数；输出含思考；API 等价 = 按官方标价折算，不是实际扣的额度",
                fSmall, c.Dim, new RectangleF(pad, y, ClientSize.Width - pad * 2, 18f * s), StringAlignment.Near);
            y += 22f * s;

            y = Section(g, "各档位汇总", tiers, true, y);
            if (runs != null && runs.Count > 0)
            {
                y = Section(g, "切换记录（按时间先后，每一段中间没换过模型和档位）", runs, false, y);
                if (runsHidden > 0)
                {
                    Str(g, "… 更早的 " + runsHidden + " 段没有列出", fSmall, c.Dim, new RectangleF(pad, y - 8f * s, 400f * s, rowH), StringAlignment.Near);
                    y += rowH - 8f * s;
                }
            }

            float bw = 92f * s, bh = 24f * s;
            btnDrill = new RectangleF(pad, ClientSize.Height - pad - bh, bw, bh);
            btnCopy = new RectangleF(pad + bw + 8f * s, ClientSize.Height - pad - bh, bw, bh);
            SkinDraw.RrLine(g, c.Dim, btnDrill, 4f * s, 1f * s);
            Str(g, "会话明细", fSmall, c.Main, btnDrill, StringAlignment.Center);
            SkinDraw.RrLine(g, c.Dim, btnCopy, 4f * s, 1f * s);
            Str(g, copied ? "已复制" : "复制为文本", fSmall, c.Main, btnCopy, StringAlignment.Center);
        }

        // ------------------------------------------------------------ 复制成文本（制表符分隔，贴到表格或笔记里都行）
        // 贴进 Excel 时，以 = + - @ 开头的文字会被当成公式执行（会话标题来自对话内容，可能是别人写的）：前面补一个单引号，让它只当文字
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
            sb.AppendLine(Safe(title) + "　" + range);
            AppendTable(sb, "各档位汇总", tiers);
            if (runs != null && runs.Count > 0)
            {
                sb.AppendLine();
                AppendTable(sb, "切换记录", runs);
                if (runsHidden > 0)
                {
                    sb.AppendLine("（更早的 " + runsHidden + " 段没有列出）");
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
