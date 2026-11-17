// Feature switches, what the panel shows, the three profiles (Everything / Recommended / Minimal) and the settings dialog.
//
// Settings are saved in %APPDATA%\Stilla\settings.txt (all integers, bit masks):
//   profile=0/1/2/3   current profile: 0 Everything, 1 Recommended, 2 Minimal, 3 Custom (changed by hand)
//   fmask   background features (see Feat.F*)          vmask   which views the panel shows (Session / Model / Compare / Plan / API / Trend)
//   smask   which columns the Session view shows             dmask   other display items (cap forecast row, cost row, account chip, ...)
//
// Profile = a preset set of fmask / vmask / smask / dmask + the background refresh pace (Cadence). Changing any single option in the dialog turns the profile into "Custom";
// changing it back to exactly match a profile is recognized as that profile again.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Stilla
{
    internal static class Feat
    {
        // Background features
        public const int Alert = 1;         // Cache expiry reminder (reads session state; also tracked at a low rate while the panel is closed)
        public const int History = 2;       // Speed history reading (last 8 days of logs: long-window speed and share, gray averages, trend chart)
        public const int Fit = 4;           // Automatic quota conversion refit (runs the Python fit script in the background)
        public const int VSync = 8;         // VS Code session title sync (runs Python in the background)
        public const int Health = 16;       // Estimate self-check
        public const int Weekly = 32;       // Automatic weekly summary
        public const int Log = 64;          // Log usage to files
        public const int Trim = 128;        // Free memory automatically when usage gets too high
        public const int AllF = 255;

        // Panel views (same as usageMode): 0 Session 1 Model 2 Compare 3 Plan 4 API 5 Trend
        public const int ViewCount = 6;
        public const int AllV = 63;

        // Columns of the Session view
        public const int ColCount = 7;
        public const int AllS = 127;        // Bits: 0 Expires 1 Speed 2 Input 3 Output 4 Cache W 5 Cache R 6 Share (the on-screen order is Expires, Speed, Share, Input, Output, Cache W, Cache R; the meaning of the bits is unchanged)

        // Other display items
        public const int Forecast = 1;      // The "cap forecast" under the Total row
        public const int DsLine = 2;        // The DeepSeek cost estimate on the last row
        public const int Chip = 4;          // The account chip at the top right of the compact card
        public const int Jump = 8;          // The ↗ jump arrow to the right of the session name
        public const int HealthBadge = 16;  // The estimate self-check hint in the Total row
        public const int AllD = 31;

        public static readonly string[] ProfileNames = { "Everything", "Recommended", "Minimal", "Custom" };
        public static readonly string[] ProfileDesc =
        {
            "All features and display items are on, with the most frequent background refresh. Good when your PC is idle and you want to see everything.",
            "Everyday use: all features on, but hides the less useful Input column, the API view and the DeepSeek cost row; moderate background refresh.",
            "Lowest resource use: keeps only cache reminders, the usage log and memory trimming; no history reading, no Python, no self-check or weekly report; only the Session and Model views. Good for gaming or a busy PC.",
            "You have adjusted the settings by hand. Click any profile above to reset all options to that profile."
        };

        // Four integers: fmask, vmask, smask, dmask
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

        // Background pace. The Custom profile uses the pace of Recommended
        public sealed class Cadence
        {
            public int BgPollSec;       // Interval (seconds) for tracking session state while the panel is closed
            public int HistMin;         // Interval (minutes) for re-reading the log history
            public int FitMin;          // Interval (minutes) for recomputing the quota conversion: the factor is measured
            public int FitDerivedMin;   // Same, but the factor is only inferred (not enough data yet; replace it with a measured one as soon as possible)
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

        // Which profile these four integers match exactly; if none, 3 (Custom)
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
            "Cache expiry reminder (system notice before expiry; light tracking when panel closed)",
            "Speed history (last 8 days of logs: long-window speed / share, gray averages, trend)",
            "Auto-refit quota conversion (runs the Python fit script so estimates follow the data)",
            "VS Code session title sync (runs Python in the background every 10 minutes)",
            "Estimate self-check (compares estimates with quota growth; warns on big deviations)",
            "Automatic weekly summary (HTML report from Monday + notice; manual runs unaffected)",
            "Log usage to file (if off, history, trend and weekly report stop updating)",
            "Free memory automatically when usage gets too high"
        };

        public static readonly string[] VNames = { "Session", "Model", "Compare", "Plan", "API", "Trend" };
        public static readonly string[] SNames = { "Expires", "Speed", "Input", "Output", "Cache W", "Cache R", "Share (5h / weekly quota used in the last N min; same window as Speed)" };
        public static readonly string[] DNames =
        {
            "Cap forecast under the Total row",
            "DeepSeek cost estimate row at the bottom",
            "Account chip at the top right of the compact card",
            "Jump arrow (↗) to the right of the session name",
            "Estimate self-check hint in the Total row"
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
            Text = "Features and display settings";
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
            t.Text = "Profile (applies a whole set of settings in one click):";
            t.AutoSize = true;
            t.Location = new Point(12, 12);
            Controls.Add(t);
            for (int i = 0; i < 4; i++)
            {
                RadioButton r = new RadioButton();
                r.Text = Feat.ProfileNames[i];
                r.Appearance = Appearance.Button;
                r.TextAlign = ContentAlignment.MiddleCenter;
                r.Size = new Size(108, 28);
                r.Location = new Point(12 + i * 114, 36);
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
            Head(body, "Background features (turn off to save resources)", y);
            y += 24;
            for (int i = 0; i < 8; i++)
            {
                fb[i] = Box(body, Feat.FNames[i], 12, y, 490);
                y += fb[i].Height + 2;
            }
            y += 8;
            Head(body, "Views shown in the panel (the buttons at the top right)", y);
            y += 24;
            y = Grid(body, vb, Feat.VNames, Feat.ViewCount, 3, 160, 150, y);
            y += 6;
            Head(body, "Columns shown in the Session view", y);
            y += 24;
            // the last column ("share") has a long label of its own: it gets a full-width row instead of a 112-pixel cell
            y = Grid(body, sb, Feat.SNames, Feat.ColCount - 1, 4, 120, 112, y);
            sb[Feat.ColCount - 1] = Box(body, Feat.SNames[Feat.ColCount - 1], 12, y, 490);
            y += sb[Feat.ColCount - 1].Height + 2;
            y += 6;
            Head(body, "Other display items", y);
            y += 24;
            for (int i = 0; i < 5; i++)
            {
                db[i] = Box(body, Feat.DNames[i], 12, y, 490);
                y += db[i].Height + 2;
            }

            Button ok = new Button();
            ok.Text = "OK";
            ok.Size = new Size(80, 28);
            ok.Location = new Point(386, H - 40);
            ok.Click += delegate
            {
                if (Mask(vb) == 0)
                {
                    MessageBox.Show(this, "At least one view must be shown.", "Features and display settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (Mask(sb) == 0)
                {
                    MessageBox.Show(this, "The Session view needs at least one column.", "Features and display settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            cancel.Text = "Cancel";
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
