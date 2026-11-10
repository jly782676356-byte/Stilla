// Stilla-Setup.exe：安装 / 卸载程序。不需要管理员权限，装在当前用户目录下，卸载干净。
//
//   安装：双击；或  Stilla-Setup.exe /S [/D=安装目录] [/nostatusline] [/nostartmenu] [/desktop] [/autostart] [/nolaunch]   （/S = 静默安装）
//   卸载：「设置 → 应用」里卸载；或  Stilla-Setup.exe /uninstall [/S] [/purge]   （/purge = 同时删掉设置和日志）
//
// 安装包里带着 Stilla.exe、Stilla.Statusline.exe 和 Python 脚本（作为资源编译进这个 exe），所以只有一个文件。
// 「配置 Claude Code 状态栏」会修改 settings.json，所以默认勾选但要用户看得见、可以取消；改之前会备份，卸载时撤销。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Stilla
{
    internal sealed class SetupOptions
    {
        public string Dir;
        public bool StartMenu = true;
        public bool Desktop;
        public bool AutoStart;
        public bool StatusLine = true;
        public bool Launch = true;
    }

    internal static class SetupProgram
    {
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Stilla";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string DefaultDir()
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "Stilla");
        }

        [STAThread]
        private static int Main(string[] args)
        {
            SetupOptions o = new SetupOptions();
            o.Dir = DefaultDir();
            bool silent = false, uninstall = false, purge = false;
            for (int ai = 0; ai < args.Length; ai++)
            {
                string a = args[ai];
                string l = a.ToLowerInvariant();
                if (l == "/s" || l == "/silent") silent = true;
                else if (l == "/uninstall") uninstall = true;
                else if (l == "/purge") purge = true;
                else if (l == "/nostatusline") o.StatusLine = false;
                else if (l == "/nostartmenu") o.StartMenu = false;
                else if (l == "/desktop") o.Desktop = true;
                else if (l == "/autostart") o.AutoStart = true;
                else if (l == "/nolaunch") o.Launch = false;
                else if (l.StartsWith("/d="))
                {
                    // /D= 之后的所有参数都算路径（路径里有空格又没加引号时，命令行会把它拆成好几段）
                    StringBuilder pb = new StringBuilder(a.Substring(3));
                    while (ai + 1 < args.Length && !args[ai + 1].StartsWith("/"))
                    {
                        pb.Append(' ').Append(args[++ai]);
                    }
                    o.Dir = pb.ToString().Trim('"');
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (uninstall)
            {
                return Uninstall(silent, purge) ? 0 : 1;
            }
            if (silent)
            {
                try
                {
                    Install(o, delegate(string s) { });
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }
            Application.Run(new SetupForm(o));
            return 0;
        }

        // ------------------------------------------------------------ 安装
        public static void Install(SetupOptions o, Action<string> log)
        {
            string dir = Path.GetFullPath(o.Dir);
            string exe = Path.Combine(dir, "Stilla.exe");

            log("正在关闭正在运行的 Stilla…");
            StopRunning(dir);

            log("复制文件到 " + dir);
            Directory.CreateDirectory(dir);
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith("res/", StringComparison.Ordinal))
                {
                    continue;
                }
                string rel = name.Substring(4).Replace('/', '\\');
                string target = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream src = asm.GetManifestResourceStream(name))
                using (FileStream dst = new FileStream(target, FileMode.Create, FileAccess.Write))
                {
                    src.CopyTo(dst);
                }
            }
            string setupCopy = Path.Combine(dir, "Stilla-Setup.exe");
            if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Application.ExecutablePath, setupCopy, true);
            }

            log("登记到「设置 → 应用」…");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Stilla");
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", "Stilla contributors");
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + setupCopy + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + setupCopy + "\" /uninstall /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 700, RegistryValueKind.DWord);
            }

            if (o.StartMenu)
            {
                log("创建开始菜单快捷方式…");
                MakeShortcut(Path.Combine(StartMenuDir(), "Stilla.lnk"), exe, dir);
            }
            if (o.Desktop)
            {
                log("创建桌面快捷方式…");
                MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Stilla.lnk"), exe, dir);
            }
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (o.AutoStart)
                {
                    run.SetValue("Stilla", "\"" + exe + "\"");
                }
                else
                {
                    run.DeleteValue("Stilla", false);
                }
            }

            if (o.StatusLine)
            {
                log("配置 Claude Code 状态栏…");
                bool ok;
                string msg = ClaudeSettings.Install(ClaudeRoots.Primary(), Path.Combine(dir, "Stilla.Statusline.exe"), out ok);
                log(msg);
            }

            if (o.Launch)
            {
                log("启动 Stilla…");
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir });
                }
                catch (Exception)
                {
                }
            }
            log("完成。");
        }

        // ------------------------------------------------------------ 卸载
        public static bool Uninstall(bool silent, bool purge)
        {
            string dir = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (k != null)
                {
                    dir = k.GetValue("InstallLocation") as string;
                }
            }
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.GetDirectoryName(Application.ExecutablePath);
            }
            if (!silent)
            {
                if (MessageBox.Show("要卸载 Stilla 吗？\n\n安装目录：" + dir + "\n\n会撤销 Claude Code 状态栏设置，你的设置和日志默认保留。",
                    "卸载 Stilla", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return false;
                }
                if (Directory.Exists(AppPaths.Home) && !purge)
                {
                    purge = MessageBox.Show("同时删除 Stilla 的设置和用量日志吗？\n\n" + AppPaths.Home + "\n\n选「否」会保留，以后重装还能接着用。",
                        "卸载 Stilla", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                }
            }

            StopRunning(dir);
            bool changed;
            ClaudeSettings.Uninstall(ClaudeRoots.Primary(), out changed);
            foreach (string lnk in new string[]
            {
                Path.Combine(StartMenuDir(), "Stilla.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Stilla.lnk")
            })
            {
                TryDelete(lnk);
            }
            try
            {
                Directory.Delete(StartMenuDir(), false);
            }
            catch (Exception)
            {
            }
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (run != null)
                {
                    run.DeleteValue("Stilla", false);
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (purge)
            {
                try
                {
                    Directory.Delete(AppPaths.Home, true);
                }
                catch (Exception)
                {
                }
            }

            // 自己正在运行，删不了自己所在的目录：交给一个延迟执行的 cmd
            string self = Application.ExecutablePath;
            bool selfInside = self.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
            foreach (string f in Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : new string[0])
            {
                if (!string.Equals(f, self, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(f);
                }
            }
            if (selfInside)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                catch (Exception)
                {
                }
            }
            else
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                }
            }
            if (!silent)
            {
                MessageBox.Show("Stilla 已卸载。", "卸载 Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }

        // ------------------------------------------------------------ 小工具
        private static string StartMenuDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Stilla");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        // 只关掉装在这个目录里的 Stilla，不碰别的位置的（便携版、开发版）
        private static void StopRunning(string dir)
        {
            string want = Path.Combine(Path.GetFullPath(dir), "Stilla.exe");
            foreach (Process p in Process.GetProcessesByName("Stilla"))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, want, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // 创建快捷方式：用系统自带的 WScript.Shell（晚绑定，不需要引用任何东西）
        private static void MakeShortcut(string lnkPath, string target, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Stilla：Claude Code 额度悬浮窗" });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly SetupOptions o;
        private TextBox dir;
        private CheckBox cStart, cDesk, cAuto, cStatus, cLaunch;
        private TextBox log;
        private Button install, cancel;
        private bool done;

        public SetupForm(SetupOptions options)
        {
            o = options;
            Text = "安装 Stilla " + AppInfo.Version;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 520);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
            }

            Label title = new Label();
            title.Text = "Stilla " + AppInfo.Version + " · Claude Code 额度悬浮窗";
            title.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            title.Location = new Point(16, 14);
            title.AutoSize = true;
            Controls.Add(title);

            Label disc = new Label();
            disc.Text = AppInfo.Disclaimer;
            disc.ForeColor = Color.DimGray;
            disc.Location = new Point(16, 42);
            disc.Size = new Size(528, 34);
            Controls.Add(disc);

            Label l1 = new Label();
            l1.Text = "安装位置（不需要管理员权限）：";
            l1.Location = new Point(16, 84);
            l1.AutoSize = true;
            Controls.Add(l1);
            dir = new TextBox();
            dir.Text = o.Dir;
            dir.Location = new Point(16, 104);
            dir.Size = new Size(446, 24);
            Controls.Add(dir);
            Button browse = new Button();
            browse.Text = "浏览…";
            browse.Location = new Point(470, 102);
            browse.Size = new Size(74, 27);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.SelectedPath = dir.Text;
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        dir.Text = Path.Combine(d.SelectedPath, "Stilla");
                    }
                }
            };
            Controls.Add(browse);

            cStart = Check("创建开始菜单快捷方式", o.StartMenu, 142);
            cDesk = Check("创建桌面快捷方式", o.Desktop, 166);
            cAuto = Check("开机自动启动", o.AutoStart, 190);
            cStatus = Check("配置 Claude Code 状态栏（让它把额度交给 Stilla）", o.StatusLine, 214);
            Label sub = new Label();
            sub.Text = "会修改 " + ClaudeSettings.PathIn(ClaudeRoots.Primary()) + "：先备份，你已有的状态栏会保留，卸载时撤销。不勾选也行，装好后在 Stilla 右键菜单里再设。";
            sub.ForeColor = Color.DimGray;
            sub.Location = new Point(40, 236);
            sub.Size = new Size(504, 34);
            Controls.Add(sub);
            cLaunch = Check("安装完成后启动", o.Launch, 276);

            Label l2 = new Label();
            l2.Text = "检测到的 Claude 数据目录（只读，用来统计会话用量；命令行、VS Code、桌面端都会找）：";
            l2.Location = new Point(16, 308);
            l2.Size = new Size(528, 20);
            Controls.Add(l2);
            TextBox roots = new TextBox();
            roots.Multiline = true;
            roots.ReadOnly = true;
            roots.ScrollBars = ScrollBars.Vertical;
            roots.Location = new Point(16, 330);
            roots.Size = new Size(528, 56);
            StringBuilder sb = new StringBuilder();
            foreach (string r in ClaudeRoots.All())
            {
                sb.AppendLine(r + (Directory.Exists(Path.Combine(r, "projects")) ? "" : "   （还没有会话记录）"));
            }
            roots.Text = sb.ToString();
            Controls.Add(roots);

            log = new TextBox();
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Location = new Point(16, 396);
            log.Size = new Size(528, 66);
            log.Text = "没找到你的目录？装好后右键 → Claude 数据目录 → 添加目录。";
            Controls.Add(log);

            install = new Button();
            install.Text = "安装";
            install.Location = new Point(376, 476);
            install.Size = new Size(80, 30);
            install.Click += delegate { if (done) { Close(); } else { DoInstall(); } };
            Controls.Add(install);
            cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(464, 476);
            cancel.Size = new Size(80, 30);
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            AcceptButton = install;
        }

        private CheckBox Check(string text, bool on, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.Location = new Point(18, y);
            c.Size = new Size(526, 22);
            Controls.Add(c);
            return c;
        }

        private void DoInstall()
        {
            o.Dir = dir.Text.Trim();
            o.StartMenu = cStart.Checked;
            o.Desktop = cDesk.Checked;
            o.AutoStart = cAuto.Checked;
            o.StatusLine = cStatus.Checked;
            o.Launch = cLaunch.Checked;
            install.Enabled = false;
            cancel.Enabled = false;
            log.Text = "";
            try
            {
                SetupProgram.Install(o, delegate(string s)
                {
                    log.AppendText(s + "\r\n");
                    Application.DoEvents();
                });
                done = true;
                install.Text = "完成";
                install.Enabled = true;
                cancel.Visible = false;
            }
            catch (Exception ex)
            {
                log.AppendText("安装失败：" + ex.Message + "\r\n");
                install.Enabled = true;
                cancel.Enabled = true;
            }
        }
    }
}
