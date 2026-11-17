// 功能开关、面板显示内容、三个「调度策略」（全开 / 推荐 / 精简）和设置对话框。
//
// 设置保存在 %APPDATA%\Stilla\settings.txt（都是 整数，位掩码）：
//   profile=0/1/2/3   当前档位：0 全开、1 推荐、2 精简、3 自定义（手动改过）
//   fmask   后台功能（见 Feat.F*）          vmask   面板里显示哪些视图（会话 / 模型 / 对比 / 订阅 / API / 趋势）
//   smask   会话视图里显示哪些列             dmask   其他显示内容（触顶预测行、费用行、账号小标签……）
//
// 档位 = 一组预设的 fmask / vmask / smask / dmask + 后台刷新的节奏（Cadence）。你在对话框里改任何一项，档位就变成「自定义」；
// 改回和某个档位完全一样，会自动认成那个档位。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Stilla
{
    internal static class Feat
    {
        // 后台功能
        public const int Alert = 1;         // 缓存到期提醒（读会话状态；面板关着也低频跟踪）
        public const int History = 2;       // 速度历史读取（日志最近 8 天：长窗口速度和占用、灰色平均值、趋势图）
        public const int Fit = 4;           // 额度换算自动重算（后台跑 Python 拟合脚本）
        public const int VSync = 8;         // VS Code 会话标题同步（后台跑 Python）
        public const int Health = 16;       // 估算健康度自检
        public const int Weekly = 32;       // 每周自动生成小结
        public const int Log = 64;          // 把用量记录到日志文件
        public const int Trim = 128;        // 占用过大时自动回收内存
        public const int AllF = 255;

        // 面板视图（和 usageMode 一致）：0 会话 1 模型 2 对比 3 订阅 4 API 5 趋势
        public const int ViewCount = 6;
        public const int AllV = 63;

        // 会话视图的列
        public const int ColCount = 7;
        public const int AllS = 127;        // 位：0 缓存剩余 1 速度 2 输入 3 输出 4 缓存写 5 缓存读 6 占用（屏幕上的顺序是 缓存剩余 速度 占用 输入 输出 缓存写 缓存读，位的含义不变）

        // 其他显示内容
        public const int Forecast = 1;      // 合计行下面的「触顶预测」
        public const int DsLine = 2;        // 最下面一行 DeepSeek 费用估算
        public const int Chip = 4;          // 紧凑卡右上角的账号小标签
        public const int Jump = 8;          // 会话行名字右边的 ↗ 跳转
        public const int HealthBadge = 16;  // 合计行里的估算自检提示
        public const int AllD = 31;

        public static readonly string[] ProfileNames = { "全开", "推荐", "精简", "自定义" };
        public static readonly string[] ProfileDesc =
        {
            "所有功能和显示内容全部打开，后台刷新最勤快。适合电脑空闲、想把信息看全的时候。",
            "日常使用：功能全开，但隐藏价值不高的「输入」列、API 视图和 DeepSeek 费用行，后台刷新节奏适中。",
            "最省资源：只保留缓存提醒、用量日志和内存回收；不读历史、不跑 Python、不做自检和周报；只有会话和模型两个视图。适合玩游戏或电脑吃紧的时候。",
            "你手动调整过设置。点上面任一档位，会按那个档位重新设置所有选项。"
        };

        // 四个整数：fmask、vmask、smask、dmask
        public static int[] Preset(int profile)
        {
            switch (profile)
            {
                case 0:
                    return new int[] { AllF, AllV, AllS, AllD };
                case 2:
                    return new int[] { Alert | Log | Trim, 1 | 2, 1 | 2 | 8 | 64, Forecast | Chip };
                default:
                    return new int[] { AllF, 1 | 2 | 4 | 8 | 32, AllS & ~4, Forecast | Chip | Jump | HealthBadge };
            }
        }

        // 后台节奏。自定义档位用推荐档的节奏
        public sealed class Cadence
        {
            public int BgPollSec;       // 面板关着时跟踪会话状态的间隔（秒）
            public int HistMin;         // 重读日志历史的间隔（分钟）
            public int FitMin;          // 额度换算重算的间隔（分钟）：系数是实测的
            public int FitDerivedMin;   // 同上：系数是推算的（数据还不够，尽快换成实测）
        }

        public static Cadence CadenceOf(int profile)
        {
            Cadence c = new Cadence();
            if (profile == 0)
            {
                c.BgPollSec = 15; c.HistMin = 3; c.FitMin = 30; c.FitDerivedMin = 5;
            }
            else if (profile == 2)
            {
                c.BgPollSec = 30; c.HistMin = 15; c.FitMin = 180; c.FitDerivedMin = 30;
            }
            else
            {
                c.BgPollSec = 15; c.HistMin = 5; c.FitMin = 60; c.FitDerivedMin = 10;
            }
            return c;
        }

        // 这四个整数正好是哪个档位；都不是就是 3（自定义）
        public static int Identify(int f, int v, int s, int d)
        {
            for (int p = 0; p < 3; p++)
            {
                int[] a = Preset(p);
                if (a[0] == f && a[1] == v && a[2] == s && a[3] == d)
                {
                    return p;
                }
            }
            return 3;
        }

        public static readonly string[] FNames =
        {
            "缓存到期提醒（读会话状态，快过期时系统通知；面板关着也低频跟踪）",
            "速度历史读取（日志最近 8 天：长窗口速度和占用、灰色平均值、趋势图）",
            "额度换算自动重算（后台跑 Python 拟合脚本，让估算系数跟着数据更新）",
            "VS Code 会话标题同步（后台跑 Python，每 10 分钟一次）",
            "估算健康度自检（拿额度读数的涨速核对估算，偏差大就提醒）",
            "每周自动小结（周一起生成上周的 HTML 报告并通知；手动生成不受影响）",
            "把用量记录到日志文件（关掉后历史、趋势、周报都不再更新）",
            "占用过大时自动回收内存"
        };

        public static readonly string[] VNames = { "会话", "模型", "对比", "订阅", "API", "趋势" };
        public static readonly string[] SNames = { "缓存剩余", "速度", "输入", "输出", "缓存写", "缓存读", "占用（近 N 分钟占用的 5h / 周额度，窗口同「速度」）" };
        public static readonly string[] DNames =
        {
            "合计行下面的触顶预测",
            "最下面的 DeepSeek 费用估算行",
            "紧凑卡右上角的账号小标签",
            "会话行名字右边的 ↗ 跳转",
            "合计行里的估算自检提示"
        };
    }

    // The widget is per-monitor DPI aware, but the dialogs are built in code with fixed pixel layouts. At 125% / 150% the fonts grow and the controls do not,
    // so texts overlap and get cut. AutoScaleMode.Dpi makes WinForms scale the whole layout with the DPI when the form is first shown (at 100% nothing changes).
    internal static class DialogDpi
    {
        public static void Apply(Form f)
        {
            f.AutoScaleDimensions = new SizeF(96f, 96f);
            f.AutoScaleMode = AutoScaleMode.Dpi;
        }

        // scale factor of the screen the form will be shown on (1.0 = 100%)
        public static float Factor(Form f)
        {
            SizeF d = f.CurrentAutoScaleDimensions;
            return d.Height > 0f ? d.Height / 96f : 1f;
        }
    }

    internal sealed class FeatureForm : Form
    {
        private readonly CheckBox[] fb = new CheckBox[8];
        private readonly CheckBox[] vb = new CheckBox[Feat.ViewCount];
        private readonly CheckBox[] sb = new CheckBox[Feat.ColCount];
        private readonly CheckBox[] db = new CheckBox[5];
        private readonly RadioButton[] pr = new RadioButton[4];
        private readonly Label desc = new Label();
        private bool busy;

        public int F, V, S, D, Profile;

        private static int Mask(CheckBox[] boxes)
        {
            int m = 0;
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i].Checked)
                {
                    m |= 1 << i;
                }
            }
            return m;
        }

        private static void SetMask(CheckBox[] boxes, int m)
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                boxes[i].Checked = (m & (1 << i)) != 0;
            }
        }

        private CheckBox Box(Control parent, string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = false;
            // a label that is too long for its box wraps onto a second line instead of being cut off (the English texts are longer)
            Size need = TextRenderer.MeasureText(text, Font, new Size(Math.Max(20, w - 20), 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            c.Size = new Size(w, Math.Max(20, need.Height + 4));
            c.Location = new Point(x, y);
            c.CheckedChanged += delegate { OnBoxChanged(); };
            parent.Controls.Add(c);
            return c;
        }

        // check boxes in a grid (cols columns, colW apart, boxW wide); a row is as high as its highest box; returns the y below the grid
        private int Grid(Control parent, CheckBox[] boxes, string[] names, int count, int cols, int colW, int boxW, int y)
        {
            for (int row = 0; row * cols < count; row++)
            {
                int rowH = 20;
                for (int c = 0; c < cols && row * cols + c < count; c++)
                {
                    int i = row * cols + c;
                    boxes[i] = Box(parent, names[i], 12 + c * colW, y, boxW);
                    rowH = Math.Max(rowH, boxes[i].Height);
                }
                y += rowH + 2;
            }
            return y;
        }

        private static Label Head(Control parent, string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            l.AutoSize = true;
            l.Location = new Point(8, y);
            parent.Controls.Add(l);
            return l;
        }

        public FeatureForm(int profile, int f, int v, int s, int d)
        {
            Text = "功能与显示设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            DialogDpi.Apply(this);
            // 700 high does not fit a 768-pixel screen (or 720 logical pixels at 150%): take the height from the work area, the body scrolls
            int hAvail = (int)(Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea.Height / DialogDpi.Factor(this)) - 80;
            int H = Math.Max(440, Math.Min(700, hAvail));
            ClientSize = new Size(560, H);

            Label t = new Label();
            t.Text = "调度策略（一键套用一整套设置）：";
            t.AutoSize = true;
            t.Location = new Point(12, 12);
            Controls.Add(t);
            for (int i = 0; i < 4; i++)
            {
                RadioButton r = new RadioButton();
                r.Text = Feat.ProfileNames[i];
                r.Appearance = Appearance.Button;
                r.TextAlign = ContentAlignment.MiddleCenter;
                r.Size = new Size(84, 28);
                r.Location = new Point(12 + i * 90, 36);
                int idx = i;
                r.Click += delegate { PickProfile(idx); };
                pr[i] = r;
                Controls.Add(r);
            }
            desc.AutoSize = false;
            desc.Size = new Size(536, 36);
            desc.Location = new Point(12, 70);
            desc.ForeColor = Color.FromArgb(90, 90, 90);
            Controls.Add(desc);

            Panel body = new Panel();
            body.AutoScroll = true;
            body.BorderStyle = BorderStyle.FixedSingle;
            body.Location = new Point(12, 110);
            body.Size = new Size(536, H - 160);
            Controls.Add(body);

            int y = 6;
            Head(body, "后台功能（关掉能省资源）", y);
            y += 24;
            for (int i = 0; i < 8; i++)
            {
                fb[i] = Box(body, Feat.FNames[i], 12, y, 490);
                y += fb[i].Height + 2;
            }
            y += 8;
            Head(body, "面板里显示哪些视图（右上角的按钮）", y);
            y += 24;
            y = Grid(body, vb, Feat.VNames, Feat.ViewCount, 3, 160, 150, y);
            y += 6;
            Head(body, "会话视图里显示哪些列", y);
            y += 24;
            // the last column ("share") has a long label of its own: it gets a full-width row instead of a 112-pixel cell
            y = Grid(body, sb, Feat.SNames, Feat.ColCount - 1, 4, 120, 112, y);
            sb[Feat.ColCount - 1] = Box(body, Feat.SNames[Feat.ColCount - 1], 12, y, 490);
            y += sb[Feat.ColCount - 1].Height + 2;
            y += 6;
            Head(body, "其他显示内容", y);
            y += 24;
            for (int i = 0; i < 5; i++)
            {
                db[i] = Box(body, Feat.DNames[i], 12, y, 490);
                y += db[i].Height + 2;
            }

            Button ok = new Button();
            ok.Text = "确定";
            ok.Size = new Size(80, 28);
            ok.Location = new Point(386, H - 40);
            ok.Click += delegate
            {
                if (Mask(vb) == 0)
                {
                    MessageBox.Show(this, "至少要显示一个视图。", "功能与显示设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (Mask(sb) == 0)
                {
                    MessageBox.Show(this, "会话视图里至少要显示一列。", "功能与显示设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                F = Mask(fb);
                V = Mask(vb);
                S = Mask(sb);
                D = Mask(db);
                Profile = Feat.Identify(F, V, S, D);
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(80, 28);
            cancel.Location = new Point(472, H - 40);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            busy = true;
            SetMask(fb, f);
            SetMask(vb, v);
            SetMask(sb, s);
            SetMask(db, d);
            busy = false;
            ShowProfile(Feat.Identify(f, v, s, d));
        }

        private void ShowProfile(int p)
        {
            for (int i = 0; i < 4; i++)
            {
                pr[i].Checked = i == p;
            }
            desc.Text = Feat.ProfileDesc[p];
        }

        private void PickProfile(int p)
        {
            if (p == 3)
            {
                ShowProfile(Feat.Identify(Mask(fb), Mask(vb), Mask(sb), Mask(db)));
                return;
            }
            int[] a = Feat.Preset(p);
            busy = true;
            SetMask(fb, a[0]);
            SetMask(vb, a[1]);
            SetMask(sb, a[2]);
            SetMask(db, a[3]);
            busy = false;
            ShowProfile(p);
        }

        private void OnBoxChanged()
        {
            if (!busy)
            {
                ShowProfile(Feat.Identify(Mask(fb), Mask(vb), Mask(sb), Mask(db)));
            }
        }
    }
}
