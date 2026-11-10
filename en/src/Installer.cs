// Stilla-Setup.exe: installer / uninstaller. Needs no administrator rights, installs under the current user's folder and uninstalls cleanly.
//
//   Install: double-click; or  Stilla-Setup.exe /S [/D=install dir] [/nostatusline] [/nostartmenu] [/desktop] [/autostart] [/nolaunch]   (/S = silent install)
//   Uninstall: from "Settings -> Apps"; or  Stilla-Setup.exe /uninstall [/S] [/purge]   (/purge = also delete settings and logs)
//
// The installer carries Stilla.exe, Stilla.Statusline.exe and the Python scripts (compiled into this exe as resources), so it is a single file.
// "Configure the Claude Code status line" edits settings.json, so it is ticked by default but visible and can be unticked; it makes a backup first and is undone on uninstall.

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
                    // Everything after /D= belongs to the path (an unquoted path with spaces gets split into several arguments by the command line)
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

        // ------------------------------------------------------------ Install
        public static void Install(SetupOptions o, Action<string> log)
        {
            string dir = Path.GetFullPath(o.Dir);
            string exe = Path.Combine(dir, "Stilla.exe");

            log("Closing any running Stilla...");
            StopRunning(dir);

            log("Copying files to " + dir);
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

            log("Registering in \"Settings -> Apps\"...");
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
                log("Creating the Start Menu shortcut...");
                MakeShortcut(Path.Combine(StartMenuDir(), "Stilla.lnk"), exe, dir);
            }
            if (o.Desktop)
            {
                log("Creating the desktop shortcut...");
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
                log("Configuring the Claude Code status line...");
                bool ok;
                string msg = ClaudeSettings.Install(ClaudeRoots.Primary(), Path.Combine(dir, "Stilla.Statusline.exe"), out ok);
                log(msg);
            }

            if (o.Launch)
            {
                log("Starting Stilla...");
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir });
                }
                catch (Exception)
                {
                }
            }
            log("Done.");
        }

        // ------------------------------------------------------------ Uninstall
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
                if (MessageBox.Show("Uninstall Stilla?\n\nInstall folder: " + dir + "\n\nThe Claude Code status line setting will be undone; your settings and logs are kept by default.",
                    "Uninstall Stilla", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return false;
                }
                if (Directory.Exists(AppPaths.Home) && !purge)
                {
                    purge = MessageBox.Show("Also delete Stilla's settings and usage logs?\n\n" + AppPaths.Home + "\n\nChoose \"No\" to keep them so a later reinstall can pick up where you left off.",
                        "Uninstall Stilla", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
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

            // The uninstaller is running and cannot delete its own folder: hand that to a delayed cmd
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
                MessageBox.Show("Stilla has been uninstalled.", "Uninstall Stilla", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }

        // ------------------------------------------------------------ Helpers
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

        // Only stop the Stilla installed in this folder; leave others (portable, development builds) alone
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

        // Create a shortcut: uses the system's WScript.Shell (late-bound, nothing to reference)
        private static void MakeShortcut(string lnkPath, string target, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Stilla: quota widget for Claude Code" });
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
            Text = "Install Stilla " + AppInfo.Version;
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
            title.Text = "Stilla " + AppInfo.Version + " - quota widget for Claude Code";
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
            l1.Text = "Install location (no administrator rights needed):";
            l1.Location = new Point(16, 84);
            l1.AutoSize = true;
            Controls.Add(l1);
            dir = new TextBox();
            dir.Text = o.Dir;
            dir.Location = new Point(16, 104);
            dir.Size = new Size(446, 24);
            Controls.Add(dir);
            Button browse = new Button();
            browse.Text = "Browse...";
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

            cStart = Check("Create a Start Menu shortcut", o.StartMenu, 142);
            cDesk = Check("Create a desktop shortcut", o.Desktop, 166);
            cAuto = Check("Start with Windows", o.AutoStart, 190);
            cStatus = Check("Configure the Claude Code status line (so it hands the quota to Stilla)", o.StatusLine, 214);
            Label sub = new Label();
            sub.Text = "This will edit " + ClaudeSettings.PathIn(ClaudeRoots.Primary()) + ": a backup is made first, any status line you already have is kept, and it is undone on uninstall. You can leave it unticked and set it up later from Stilla's right-click menu.";
            sub.ForeColor = Color.DimGray;
            sub.Location = new Point(40, 236);
            sub.Size = new Size(504, 34);
            Controls.Add(sub);
            cLaunch = Check("Launch after installing", o.Launch, 276);

            Label l2 = new Label();
            l2.Text = "Detected Claude data folders (read-only, used to count session usage; the command line, VS Code and the desktop app are all searched):";
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
                sb.AppendLine(r + (Directory.Exists(Path.Combine(r, "projects")) ? "" : "   (no session records yet)"));
            }
            roots.Text = sb.ToString();
            Controls.Add(roots);

            log = new TextBox();
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Location = new Point(16, 396);
            log.Size = new Size(528, 66);
            log.Text = "Can't see your folder? After installing, right-click -> Claude data folders -> Add folder.";
            Controls.Add(log);

            install = new Button();
            install.Text = "Install";
            install.Location = new Point(376, 476);
            install.Size = new Size(80, 30);
            install.Click += delegate { if (done) { Close(); } else { DoInstall(); } };
            Controls.Add(install);
            cancel = new Button();
            cancel.Text = "Cancel";
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
                install.Text = "Finish";
                install.Enabled = true;
                cancel.Visible = false;
            }
            catch (Exception ex)
            {
                log.AppendText("Install failed: " + ex.Message + "\r\n");
                install.Enabled = true;
                cancel.Enabled = true;
            }
        }
    }
}
