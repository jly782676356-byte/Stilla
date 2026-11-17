// 悬停说明（v34）：鼠标在悬浮窗里的某个东西上停一会儿，就在旁边弹出一小块说明——这是什么、怎么算的、点了会怎样。
//
// 结构（方便开源后翻译 / 补充）：
//   TipWindow  一个不抢焦点、不挡鼠标的小窗，只负责把文字画出来
//   TipText    所有「固定」说明文字，都在这里，改文字只改这个类
//   Widget     （partial）悬停的引擎：每画一帧重新登记「哪块区域对应哪段说明」，鼠标停住后查表、显示
//              BuildTips() 是登记的总入口；需要读当前数据才能写出来的说明（缓存状态、速度、行详情…）是下面的 XxxTip() 方法，
//              它们是「懒」的：只有真的要显示的那一刻才算，平时不花时间
// 说明文字的格式：第一行是标题（粗体），后面是正文，\n 换行；太长会自动折行。
// 在 ⋮ 菜单 / 右键菜单里有「悬停说明」开关（默认开）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Stilla
{
    // ================================================================ 小窗
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
            if (m.Msg == 0x84)          // WM_NCHITTEST：对鼠标完全透明，永远不会挡住下面的悬浮窗
            {
                m.Result = new IntPtr(-1);
                return;
            }
            base.WndProc(ref m);
        }

        // 把文字排好版（算出宽高）；不显示
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

        // 画到一张图上（窗口自己的绘制和离屏测试都用它）
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

        // 显示在鼠标旁边（屏幕坐标）：默认在右下；放不下就翻到左边 / 上边
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
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // 置顶：NOSIZE | NOMOVE | NOACTIVATE
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

    // ================================================================ 固定的说明文字
    internal static class TipText
    {
        public const string Close = "关闭\n点 × 只是把悬浮窗缩到系统托盘，程序还在后台运行。\n要真正退出：右键托盘图标（或悬浮窗）→ 退出。";
        public const string Refresh = "立即刷新\n马上重新读一次额度。平时每 60 秒自动读一次，不用手动点。";
        public const string Pause = "后台读取已暂停\n暂停期间不读会话日志，也不做缓存提醒和邮件提醒（打游戏时用）。额度读数照常。\n点击：恢复。";
        public const string TabMore = "更多设置（⋮）\n选择显示哪些时间标签 / 视图 / 列，时间标签预设，速度统计窗口，窗口大小，暂停后台读取，提醒设置，导出，悬停说明开关，等等。";
        public const string Divider = "拖动调整列宽\n按住这条分隔线左右拖，相邻的两列互相让出宽度。";
        public const string Mail = "邮件提醒（@）\n勾选要提醒的会话，设置缓存剩几分钟时发邮件给你。\n按钮上橙色的数字 = 已经勾选了几个会话。\n点击：打开设置。";
        public const string Jump = "跳转到会话窗口\n点击：切到这个会话所在的 VS Code 窗口（按工作目录匹配窗口标题），并让 Claude Code 扩展回到这个会话。";
        public const string Share = "占比小图\n这一行占当前时间标签合计的比例：线（或圆）越长 = 占比越大。";
        public const string Total = "合计\n当前时间标签里所有行的合计，和上面的列一一对应。";
        public const string TotalSessions = "合计\n当前时间标签里所有会话的合计，和上面的列一一对应。（缓存剩余 / 速度 / 占用这三列不是按时间标签算的，见各自的说明。）";
        public const string Grip = "调整行距\n按住这条分隔线上下拖：往下拖，每一行的上下间距变大；往上拖变小。双击恢复标准。\n⋮ 菜单 →「行距」里也能直接选。";
        public const string Calib = "估算误差\n「5h估算 / 周估算」是用价格 × 拟合出来的系数折算的。程序把每一段历史数据先用它之前的数据拟合、再去预测它，这里是最近这些段预测的平均相对误差：越小越可信。";
        public const string Tiers = "档位 = 思考强度（effort）\nlow < medium < high < xhigh < max；开着 Ultracode 的是 ultra；后面带 +fast 是快速模式。\n档位越高，同样的 token 折合的额度越多（见「对比」视图的倍数）。xhigh→max 表示这段时间里中途从 xhigh 切到了 max。";

        public static readonly string[] View =
        {
            "会话视图\n每个会话一行：缓存还能撑多久、近期消耗额度的速度和占用，以及这段时间的 token 用量。\n点一行：打开这个会话的明细；点 ↗：跳到它所在的窗口；点表头：排序。",
            "模型视图\n每个会话一行，右边的小标签是用的模型和档位（思考强度）。「5h估算 / 周估算」= 这些用量折合成多少 5 小时 / 每周额度（拟合出来的估算，仅供参考）。\n点一行：弹出每个 模型×档位 的详细数据和切换记录。",
            "对比视图\n把所有会话合并，按「模型 · 档位」汇总，用来对比不同档位有多费额度。\n最后一列「倍数」= 相对最省的那一档，每个 token 要多耗几倍额度。",
            "订阅视图\n每个账号一行：本期（当前订阅周期）用量按官方 API 价折算的金额、月费、回本倍数（用量 ÷ 月费）和下次续费日。\n点一行：把统计切到这个账号。",
            "API 视图\n只统计走 API 的非 Claude 模型（本机日志里是 DeepSeek 等）：条数、输入、输出和估算费用。\nClaude 订阅的用量看「订阅」视图。",
            "趋势视图\n每小时（24 小时）或每天（7 天）消耗了多少额度的柱状图。虚线 = 刚好撑满整个额度窗口的速度，柱子超过它（红色）说明那段时间用得太猛。\n鼠标停在某根柱子上能看到具体数字。"
        };

        // 时间标签（下标 = 标签 id）
        public static readonly string[] Tab =
        {
            "5 分钟\n最近 5 分钟（滚动窗口）的用量，实时统计。",
            "15 分钟\n最近 15 分钟（滚动窗口）的用量，实时统计。",
            "30 分钟\n最近 30 分钟（滚动窗口）的用量，实时统计。",
            "1 小时\n最近 1 小时（滚动窗口）的用量，实时统计。",
            "3 小时\n最近 3 小时（滚动窗口）的用量，实时统计。",
            "本 5 小时\n当前这个 5 小时额度窗口里的用量：从窗口开始到重置时刻（读不到重置时间时按「现在往前 5 小时」）。\n和顶部的 5 小时额度读数是同一个窗口，能看出这个窗口里谁用得多。",
            "上 5 小时\n上一个 5 小时窗口（本窗口开始之前的 5 小时）的用量，用来对比。",
            "今天\n今天 0:00 到现在的用量，来自本机会话日志（比实时标签更新得慢一点）。",
            "昨天\n昨天 0:00 ~ 24:00 的用量，来自本机会话日志。",
            "本周\n本周一 0:00 到现在的用量，来自本机会话日志。",
            "自定义\n你自己设的起止时间（点一下可以设置）。⋮ 菜单里可以存最多 5 个常用时段，一点就切过去。"
        };
        public const string TabFoot = "\n图形越满 / 越大 = 这段时间用量越大（对数刻度），下面的数字 = token 总数。点击切换。";

        // 订阅 / API 视图底部的说明行
        public static readonly string[] SubInfo =
        {
            "订阅周期\n当前账号的订阅信息：套餐、开始日期、本期是第几天。「按自然月」= 开始日期未知，按每月 1 号到月底算。",
            "本期 API 等价\n本期用量按官方 API 价折算的金额；括号里 = 它是月费的几倍；日均 = 金额 ÷ 已过天数；整期约 = 日均 × 本期总天数。\n这不是实际扣款，订阅你只付月费。",
            "每周额度预测\n按近期的用法，预测每周额度会不会在重置前用完。"
        };

        public static readonly string[] ApiInfo =
        {
            "API 合计\n日志里所有走 API 的非 Claude 模型：条数、输入（含缓存写入）、输出、缓存命中的 token。",
            "估算费用\n按 DeepSeek 闲时价估算（人民币）。只有 DeepSeek 有价格表，别的模型没价格，不计入。",
            "说明\n这里只统计本机日志里的非 Claude 模型。Anthropic 控制台的 API 用量需要 Admin Key，本机没有，所以查不到。"
        };
    }

    // ================================================================ 引擎
    internal sealed partial class Widget
    {
        private bool tipsOn = true;                                     // 悬停说明开关（⋮ 菜单里），默认开

        private sealed class TipHit
        {
            public string Id;               // 稳定的编号：同一个东西重画后编号不变，才不会反复重新计时
            public RectangleF R;
            public string Text;
            public Func<string> Lazy;       // 要读当前数据才能写的说明：显示的那一刻才算
            public int Prio;                // 重叠时：Prio 大的赢；一样大就取面积小的（更具体的）
        }

        private readonly List<TipHit> tipHits = new List<TipHit>();
        private System.Windows.Forms.Timer tipTimer;
        private TipWindow tipWin;
        private string tipCurId, tipSuppressId;
        private bool tipShown;
        private DateTime tipShownAt;
        private const int TipDelayMs = 550;

        // 每一帧画完界面时由 DrawUsagePanel 记下的位置（BuildTips 要用）
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

        // ---------------------------------------------------------------- 登记
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

        // ---------------------------------------------------------------- 鼠标
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
                // 刚点过这里：不再弹，直到鼠标离开再回来
                tipCurId = id;
                TipHide();
                return;
            }
            tipSuppressId = null;
            if (id == tipCurId)
            {
                return;                 // 还在同一个东西上：已经显示了就不动；还在等就继续等
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
                // 已经在显示：每半秒看一眼鼠标还在不在；一直放着 25 秒就收起来
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
            // 字体固定用 Microsoft YaHei UI：有的皮肤用手写 / 衬线字体，放进说明里反而不好读
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

        // 点了鼠标：说明收起来，在鼠标离开这个东西之前不再弹
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
            ToolStripMenuItem it = new ToolStripMenuItem("悬停说明（鼠标停一会儿显示解释）");
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

        // ---------------------------------------------------------------- 总登记：每画完一帧调用一次
        private void BuildTips(int w, int h)
        {
            float s = scale;
            float pad = 14f * s;
            float inner = w - 2f * pad;
            float top = CompactH() * s;
            float rowH = rowHu * s;

            // 紧凑卡
            TipL("card", new RectangleF(0, 0, w, showUsage ? top : h), CardTip);
            Tip("close", closeRect, TipText.Close);
            Tip("refresh", refreshRect, TipText.Refresh);
            TipL("chip", accountRect, ChipTip);
            if (!showUsage)
            {
                return;
            }

            // 用量面板：标题行、视图按钮、时间标签、大数字
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
                return;             // 趋势视图的说明是在 DrawTrendView 里登记的（柱子的位置只有那里知道）
            }

            int md = usageMode;
            Tip("mail", mailBtnRect, TipText.Mail, 1);

            // 表头 + 每一列的正文区
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

            // 各行
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
                        Tip("share1:" + sid, new RectangleF(R.X, R.Y, shareW, R.Height), TipText.Share + "\n这里 = 占当前时间标签总 token 的比例。", 1);
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
                    rows = 0;           // 这几行是「没有数据」的提示文字，不是真正的数据行
                }
                for (int i = 0; i < rows; i++)
                {
                    int ri = i;
                    float ry = tipRowsY + i * rowH;
                    string shareText = md == 2 ? TipText.Share + "\n这里 = 占折合额度的比例（没有额度系数的模型按 token 算）。"
                        : md == 3 ? TipText.Share + "\n这里 = 占各账号本期金额合计的比例。" : TipText.Share + "\n这里 = 占估算费用合计的比例。";
                    Tip("shareb" + md + "_" + i, new RectangleF(pad, ry, shareW, rowH), shareText, 1);
                    float nameSpanW = md == 2 ? colWidth[0] + colWidth[1] : colWidth[0] + colWidth[1] + colWidth[2];
                    TipL("brow" + md + "_" + i, new RectangleF(pad + shareW, ry, Math.Max(10f * s, nameSpanW - shareW), rowH), delegate { return BodyRowTip(md, ri); });
                }
            }

            if (moreRect.Width > 1f)
            {
                TipL("more", moreRect, MoreTip, 1);
            }

            // 合计行及下面的几行
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

        // ---------------------------------------------------------------- 需要读当前数据的说明
        private string CardTip()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("额度卡片\n");
            sb.Append("• 百分比 = 已经用掉的比例（不是剩余）。\n");
            sb.Append("• 5 小时额度：从你发出第一条消息起算 5 小时，到点清零重新计。\n");
            sb.Append("• 每周额度：7 天一个周期，到重置时刻清零。\n");
            sb.Append("• 时钟 = 重置的时刻；旁边的倒计时 = 距离重置还有多久。\n");
            sb.Append("• 底部小字 = 最近一次读取的时间（每 60 秒一次，只读额度，不发消息、不消耗额度）。");
            if (data != null)
            {
                DateTime now = DateTime.Now;
                string c5, l5, cw, lw;
                ResetParts(data.FiveHour, now, false, out c5, out l5);
                ResetParts(data.SevenDay, now, true, out cw, out lw);
                if (data.FiveHour != null)
                {
                    sb.Append("\n\n现在 · 5 小时额度已用 " + data.FiveHour.Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + (l5.Length > 0 ? "（" + c5 + " 重置，还有 " + l5 + "）" : ""));
                }
                if (data.SevenDay != null)
                {
                    sb.Append("\n现在 · 每周额度已用 " + data.SevenDay.Percent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + (lw.Length > 0 ? "（" + cw + " 重置，还有 " + lw + "）" : ""));
                }
            }
            return sb.ToString();
        }

        private string ChipTip()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("账号与计划\n当前登录的账号：编号 · 计划（例如 B · Pro）。额度换算系数按「账号:计划」分开记录。\n");
            if (indicatorState != CacheState.None)
            {
                sb.Append("左边的小圆点 = 所有会话里最要紧的缓存状态：红 = 快过期；黄 = 10~30 分钟；绿 = 有会话在运行；蓝 = 有会话在等你处理。\n");
            }
            if (AccountStore.Current == null)
            {
                sb.Append("「未绑定」= 当前登录还没有对应的账号档案。\n");
            }
            sb.Append("点击：切换账号 / 管理账号与订阅。");
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
            if (!string.IsNullOrEmpty(range) && range != "读取中…")
            {
                t += "\n当前范围：" + range;
            }
            return t + TipText.TabFoot;
        }

        private string BigTip()
        {
            UsageTracker.PeriodSummary cur = PeriodFor(usagePeriod);
            return "TOKENS 总数\n= 输入 + 输出 + 缓存写入 + 缓存读取 四项 token 之和，范围是当前选中的时间标签（右边的时间 = 统计范围）。\n"
                + "其中缓存读取通常占绝大多数（它按很低的价格计费），所以这个数字看起来很大，并不代表额度消耗同样大。\n"
                + "现在：输入 " + UsageTracker.FormatTokens(cur.Input) + " · 输出 " + UsageTracker.FormatTokens(cur.Output)
                + " · 缓存写 " + UsageTracker.FormatTokens(cur.CacheWrite) + " · 缓存读 " + UsageTracker.FormatTokens(cur.CacheRead);
        }

        private string NameHeadTip(int md)
        {
            string sortNote = md <= 2 ? "\n点击：按这一列排序（再点反向，第三次恢复默认顺序）。" : "";
            switch (md)
            {
                case 0:
                    return "会话\n有标题就显示标题，没有就是「项目名 · 未命名 + 会话 id」。「+N子」= 它派生出的 N 个子任务（subagent）。\n最左边的小圆点颜色 = 缓存状态（见「缓存剩余」）。" + sortNote;
                case 1:
                    return "模型\n每个会话一行（标题）。行尾的小标签 = 用的模型和档位（思考强度）。" + sortNote;
                case 2:
                    return "模型 · 档位\n同一个模型的不同档位（思考强度）分开列。行尾的小标签 = 档位。" + sortNote;
                case 3:
                    return "账号\n你在「管理账号与订阅」里建的账号档案。「当前」= 现在登录的这个。";
                default:
                    return "API 模型\n走 API 的非 Claude 模型的名字，右边的日期 = 日志里它的第一条 ~ 最后一条记录。";
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
                            t = "缓存剩余\n提示缓存还能撑多久。缓存一旦过期，下一条消息要把整个上下文重新写进缓存，比续上贵十几倍——所以快过期时最好发一条消息续上。\n"
                                + "颜色：绿 = 运行中（缓存自动续期）· 蓝 = 在等你处理（权限确认 / 提问等，缓存仍在倒数）· 白 = 还有 30~60 分钟 · 黄 = 10~30 分钟 · 红 = 10 分钟内（会弹系统通知）· 灰 = 已过期 / 没有缓存。";
                            break;
                        case 2:
                            t = "速度（近 " + SpeedSpan(speedMin) + "）\n左 = 5 小时额度的消耗速度，右 = 每周额度的消耗速度，单位都是「额度百分点 / 小时」。\n"
                                + "例：30 / 3.97 = 照这个强度，每小时用掉 30% 的 5 小时额度、3.97% 的每周额度（窗口里实际用掉的额度，按窗口长度折成每小时）。\n"
                                + "窗口按墙上时间算，闲下来的会话速度会降下去。灰色 = 已空闲 / 缓存过期，显示它最后活跃时的平均速度，仅供参考，不计入合计。\n"
                                + "「≈」= 额度系数是按套餐倍率推算的，可能差好几倍。";
                            if (header)
                            {
                                click = "\n点「速度」文字：选统计窗口（5 分钟 ~ 48 小时）；点右边的小三角：按速度排序。";
                                sortable = false;
                            }
                            break;
                        case 3:
                            t = "占用（近 " + SpeedSpan(speedMin) + "）\n这个会话在统计窗口里一共占用了多少额度：左 = 5 小时额度，右 = 每周额度，单位都是「额度百分点」。\n"
                                + "例：2.1 / 0.28 = 这段时间用掉了 2.1% 的 5 小时额度和 0.28% 的每周额度。\n"
                                + "统计窗口和「速度」列是同一个；占用 ÷ 窗口小时数 = 速度。灰色 = 窗口里没有占用，显示它最后一次活跃时同样长度窗口里的占用，仅供参考，不计入合计。\n"
                                + "「≈」= 额度系数是按套餐倍率推算的，可能差好几倍。";
                            if (header)
                            {
                                click = "\n点「占用」文字：选统计窗口（5 分钟 ~ 48 小时）；点右边的小三角：按占用排序。";
                                sortable = false;
                            }
                            break;
                        case 4:
                            t = "输入\n没有命中缓存的新输入 token（你新发的内容、工具返回的结果等），按标准输入价计费。";
                            break;
                        case 5:
                            t = "输出\n模型生成的 token（含思考内容），单价最贵。";
                            break;
                        case 6:
                            t = "缓存写\n这一轮新写进缓存的 token：首次建立缓存，或缓存过期后重新写入。单价约为输入的 1.25 倍（5 分钟缓存）或 2 倍（1 小时缓存）。";
                            break;
                        default:
                            t = "缓存读\n命中缓存、按很低价格（约输入价的 1/10）读取的 token。长会话每一轮都要把整个上下文读一遍，所以它通常是最大的一项——「TOKENS」总数大多来自这里。";
                            break;
                    }
                    break;
                case 1:
                    t = col == 3 ? "token\n这个会话在这段时间的 token 总和（输入 + 输出 + 缓存写 + 缓存读）。"
                        : col == 4 ? "5h估算\n这些用量折合成多少 5 小时额度（模型系数 × 档位修正）。是拟合出来的估算，不是服务器给的真值；「≈」= 系数是推算的、没实测过。"
                        : col == 5 ? "周估算\n这些用量折合成多少每周额度（算法同「5h估算」）。是拟合出来的估算；「≈」= 系数是推算的、没实测过。"
                        : "缓存读\n命中缓存、按很低价格（约输入价的 1/10）读取的 token。";
                    break;
                case 2:
                    t = col == 2 ? "token\n这个 模型·档位 在这段时间的 token 总和（输入 + 输出 + 缓存写 + 缓存读）。"
                        : col == 3 ? "输出\n模型生成的 token（含思考内容）。"
                        : col == 4 ? "思考\n输出里用于思考（thinking）的那部分 token，已经包含在「输出」里。"
                        : col == 5 ? "5h估算\n这些用量折合成多少 5 小时额度（模型系数 × 档位修正）。是拟合出来的估算；「≈」= 系数是推算的、没实测过。"
                        : "倍数\n相对最省的那一档，每个 token 要多耗几倍额度（按 5 小时额度算）。×1.0 = 最省；「≈×」= 至少有一端是推算值。";
                    break;
                case 3:
                    t = col == 3 ? "本期用量\n当前订阅周期内的用量，按官方 API 价折算的美元金额（API 等价，不是实际扣费）。"
                        : col == 4 ? "月费\n你填的订阅月费（在「管理账号与订阅」里改）。"
                        : col == 5 ? "回本\n本期用量 ÷ 月费。×3 = 同样的用量走 API 要付月费的 3 倍。"
                        : "续费\n下次续费日。「—」= 按自然月算（开始日期未知）。";
                    break;
                default:
                    t = col == 3 ? "条数\n日志里这个模型一共有多少条回复记录。"
                        : col == 4 ? "输入\n输入 + 缓存写入的 token（都按未命中价算）。"
                        : col == 5 ? "输出\n模型生成的 token。"
                        : "估算费用（人民币）\n按 DeepSeek 闲时价估算：输入 ¥" + DsMissPerM.ToString("0.##", CultureInfo.InvariantCulture)
                            + "、缓存命中 ¥" + DsHitPerM.ToString("0.##", CultureInfo.InvariantCulture) + "、输出 ¥" + DsOutPerM.ToString("0.##", CultureInfo.InvariantCulture)
                            + " / 百万 token。只有 DeepSeek 有价格表，别的模型显示 —。";
                    break;
            }
            if (sortable)
            {
                click = "\n点击：按这一列排序（再点反向，第三次恢复默认顺序）。";
            }
            return t + click;
        }

        private string SessionNameTip(UsageTracker.SessionTotal t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(t.Title != null ? t.Title : t.Name + " · 未命名 " + t.ShortId);
            sb.Append("\n项目：" + t.Name);
            sb.Append("\n会话 ID：" + t.Session);
            if (t.SubCount > 0)
            {
                sb.Append("\n子任务：" + t.SubCount + " 个（subagent，用量已合并在这一行里）");
            }
            if (t.LastUtc > DateTime.MinValue)
            {
                sb.Append("\n这段时间里最后一次活动：" + t.LastUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }
            double? usd = PeriodUsd(t.Session);
            if (usd.HasValue)
            {
                sb.Append("\n当前时间标签里的用量按官方价折算 ≈ " + UsdText(usd.Value) + "（API 等价，不是实际扣款）");
            }
            sb.Append("\n点击：打开这个会话的明细。");
            return sb.ToString();
        }

        private string MailRowTip(string session)
        {
            MailWatch mw = MailStore.Find(session);
            if (mw == null)
            {
                return "";
            }
            return "已勾选邮件提醒\n这个会话的缓存剩 " + MailStore.MinsText(mw.Mins) + " 分钟时，会发一封邮件提醒你。\n点列表头的「@」可以修改。";
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
            string ctx = li != null && li.Ctx > 0 ? "（约 " + UsageTracker.FormatTokens(li.Ctx) + " token）" : "";
            string at = exp.HasValue ? exp.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "";
            string ttl = li != null && li.TtlMin > 0 ? "这个会话的缓存有效期是 " + (li.TtlMin >= 60 && li.TtlMin % 60 == 0 ? (li.TtlMin / 60) + " 小时" : li.TtlMin + " 分钟") + "，每发一条消息就重新计时。\n" : "";
            switch (st)
            {
                case CacheState.Running:
                    return "运行中\n正在写入或生成（最近 60 秒有新记录，或 Claude Code 报告它在忙）。缓存随每一次请求自动续期，不会过期。";
                case CacheState.Waiting:
                    return "在等你处理\nClaude Code 在等你操作（权限确认 / 提问 / 计划审批等）。它既没在跑，也不是做完了；缓存还在倒数"
                        + (remMin > 0 ? "，还剩 " + RemainText(remMin) + "（" + at + " 过期）" : "") + "。";
                case CacheState.None:
                    if (exp.HasValue)
                    {
                        return "已过期\n缓存在 " + at + " 过期了。下一条消息要把整个上下文" + ctx + "重新写入缓存，比续上贵十几倍。";
                    }
                    return "没有缓存\n没有这个会话的缓存记录：还没写过缓存，或者记录不在统计范围里。";
                default:
                    return "缓存还剩 " + RemainText(remMin) + "\n到 " + at + " 过期。\n" + ttl
                        + "过期后再发消息，要把整个上下文" + ctx + "重新写入缓存，比续上贵十几倍。"
                        + (st == CacheState.Urgent ? "\n快过期了：想继续的话，现在发一条消息把缓存续上。" : "");
            }
        }

        private string SpeedCellTip(UsageTracker.SessionTotal t)
        {
            SpeedInfo si = SpeedFor(t.Session);
            string span = SpeedSpan(speedMin);
            string approx = quota != null && quota.PlanDerived ? "\n「≈」：额度系数是按套餐倍率推算的，可能差好几倍。" : "";
            if (si != null && si.Known && (si.R5 > 0 || si.Rw > 0))
            {
                return "近 " + span + "的速度\n5 小时额度：每小时 " + RateText(si.R5, false) + " 个百分点；每周额度：每小时 " + RateText(si.Rw, true) + " 个百分点。\n"
                    + "照这个强度一直跑，5 小时额度大约 " + (si.R5 > 0 ? HoursText(100.0 / si.R5) : "—") + "用完（只算这一个会话）。" + approx;
            }
            if (si != null && si.HasGray && (si.G5 > 0 || si.Gw > 0))
            {
                return "灰色：已空闲\n近 " + span + "里这个会话没有消耗（闲着，或缓存已过期）。灰色数字是它最后一次活跃时、同样长度窗口里的平均速度："
                    + "5 小时额度每小时 " + RateText(si.G5, false) + " 个百分点，每周额度每小时 " + RateText(si.Gw, true) + " 个百分点。\n仅供参考，不计入合计和触顶预测。" + approx;
            }
            return "没有速度\n近 " + span + "里这个会话没有消耗，也没有可用的历史记录（或当前账号还没有额度换算系数，见「订阅」视图）。";
        }

        private string OccCellTip(UsageTracker.SessionTotal t)
        {
            SpeedInfo si = SpeedFor(t.Session);
            string span = SpeedSpan(speedMin);
            string approx = quota != null && quota.PlanDerived ? "\n「≈」：额度系数是按套餐倍率推算的，可能差好几倍。" : "";
            if (si != null && si.Known && (si.P5 > 0 || si.Pw > 0))
            {
                return "近 " + span + "占用\n这个会话在近 " + span + "里一共占用了 " + RateText(si.P5, false) + "% 的 5 小时额度、" + RateText(si.Pw, true) + "% 的每周额度。" + approx;
            }
            if (si != null && si.HasGray && (si.GP5 > 0 || si.GPw > 0))
            {
                return "灰色：已空闲\n近 " + span + "里这个会话没有占用额度。灰色数字是它最后一次活跃时、同样长度窗口里的占用：" + RateText(si.GP5, false) + "% 的 5 小时额度、"
                    + RateText(si.GPw, true) + "% 的每周额度。\n仅供参考，不计入合计。" + approx;
            }
            return "没有占用\n近 " + span + "里这个会话没有占用额度，也没有可用的历史记录（或当前账号还没有额度换算系数，见「订阅」视图）。";
        }

        private string TotalOccTip()
        {
            string span = SpeedSpan(speedMin);
            if (!tipSpeedKnown || (occ5 <= 0 && occW <= 0))
            {
                return "合计占用\n近 " + span + "里所有会话都没有占用额度（或还没有额度换算系数）。";
            }
            return "合计占用（近 " + span + "）\n所有会话加起来，近 " + span + "里一共占用了 " + RateText(occ5, false) + "% 的 5 小时额度、" + RateText(occW, true)
                + "% 的每周额度（含已经不在列表里的会话；灰色的不算）。";
        }

        private string TotalSpeedTip()
        {
            string span = SpeedSpan(speedMin);
            if (!tipSpeedKnown || (tipTot5 <= 0 && tipTotW <= 0))
            {
                return "合计速度\n近 " + span + "里所有会话都没有消耗额度（或还没有额度换算系数）。";
            }
            return "合计速度（近 " + span + "）\n所有会话加起来的消耗速度：5 小时额度每小时 " + RateText(tipTot5, false) + " 个百分点，每周额度每小时 " + RateText(tipTotW, true)
                + " 个百分点。\n只算近 " + span + "里有消耗的会话（灰色的不算）。下面一行的「触顶预测」就是用它算出来的。";
        }

        private string ForecastTip()
        {
            return "触顶预测\n假设接下来一直保持上面「合计」的速度，5 小时额度和每周额度各还能撑多久（= 剩余额度 ÷ 速度）。\n"
                + "• 红色 +「早于重置 X」= 照这个速度会在重置前用完。\n• 「重置前不会」= 到重置时还用不完。\n"
                + "这只是外推：近 " + SpeedSpan(speedMin) + "的窗口很短时波动很大。想要更稳的预测，点「速度」表头把统计窗口改长（15~60 分钟）。";
        }

        private string DsTip()
        {
            return "DeepSeek 闲时价估算\n如果把这段时间的 token 当成 DeepSeek-Flash 的用量来付费，大约要多少人民币（输入 ¥" + DsMissPerM.ToString("0.##", CultureInfo.InvariantCulture)
                + "、缓存命中 ¥" + DsHitPerM.ToString("0.##", CultureInfo.InvariantCulture) + "、输出 ¥" + DsOutPerM.ToString("0.##", CultureInfo.InvariantCulture)
                + " / 百万 token，缓存写入按输入价）。只是个对比参考，你实际没有付这笔钱。\n不想看到这一行：⋮ 菜单 →「其他显示内容」，取消「最下面的 DeepSeek 费用估算行」。";
        }

        private string HealthTip()
        {
            HealthInfo hi = GetHealth();
            string now = hi != null && hi.Line.Length > 0 ? hi.Line : hi != null ? hi.Short : "";
            return "估算自检\n拿「额度读数涨了多少」核对「按系数估算涨了多少」：读数是服务器给的真值，估算是程序折算的。两边差得远，说明系数不对（推算的 / 刚换套餐 / 换账号），"
                + "或者有别处（网页、别的电脑）在用同一个额度。\n" + (now.Length > 0 ? "现在：" + now + "\n" : "") + "点击：查看详情。";
        }

        private string MoreTip()
        {
            if (rowsExpanded)
            {
                return "收起\n点击收起，窗口恢复原来的高度。放不下时可以用鼠标滚轮翻页。";
            }
            return "其他的行\n点击展开全部行（窗口会自动长高；放不下时用鼠标滚轮翻页）。右边的数字 = 被折叠起来的这些行的合计。";
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
            sb.Append(SessionTitle(session) + "\n这段时间用过：");
            int n = 0;
            foreach (UsageTracker.ModelTotal p in grp.Parts)
            {
                if (++n > 6)
                {
                    sb.Append("\n…… 还有 " + (grp.Parts.Count - 6) + " 段（点击看全部）");
                    break;
                }
                string est = PartEstimate(p, false);
                sb.Append("\n• " + ShortModel(p.Model) + (p.Tier.Length > 0 && p.Tier != "未标注" ? " · " + p.Tier : "") + "：" + UsageTracker.FormatTokens(p.Total) + " token"
                    + (est == "—" ? "（没有额度系数）" : "，折合 " + est + " 的 5 小时额度"));
            }
            if (grp.Parts.Count > 1)
            {
                sb.Append("\n行首的 ▾ = 这段时间里换过模型或档位。");
            }
            sb.Append("\n档位 = 思考强度（low < medium < high < xhigh < max；Ultracode = ultra；+fast = 快速模式）。\n点击：弹出详细数据和切换记录。");
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
                    sb.Append("（当前登录）");
                }
                sb.Append("\n月费：" + (a.Fee > 0 ? Money(a.Fee) : "没填"));
                sb.Append("\n订阅开始：" + (a.Start == DateTime.MinValue ? "未知" : a.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                if (a.BindOrg.Length == 0)
                {
                    sb.Append("\n「未绑定」= 还没有登录信息对应到这个档案。");
                }
                sb.Append("\n点击：把统计切到这个账号。");
                return sb.ToString();
            }
            return "API 模型\n模型名右边的日期 = 日志里它的第一条 ~ 最后一条记录；左边的小图 = 占估算费用合计的比例。";
        }

        // 趋势视图（在 DrawTrendView 里调用）
        private void TipTrendBar(int i, RectangleF slotRect, bool hourly, double val, double usd, string label, double line, bool last)
        {
            string id = "tbar" + (hourly ? "h" : "d") + i;
            double pct = line > 0 ? val / line * 100.0 : 0;
            TipL(id, slotRect, delegate
            {
                string when = hourly ? label + ":00 ~ " + label + ":59" : label;
                string unit = hourly ? "% 的 5 小时额度" : "% 的每周额度";
                return when + "\n消耗 " + val.ToString("0.#", CultureInfo.InvariantCulture) + unit + "\n相当于撑满速度的 " + pct.ToString("0", CultureInfo.InvariantCulture) + "%"
                    + "\nAPI 等价 " + UsdText(usd) + (last ? "\n（这一" + (hourly ? "小时" : "天") + "还没走完，数字还会增加）" : "");
            });
        }
    }
}
