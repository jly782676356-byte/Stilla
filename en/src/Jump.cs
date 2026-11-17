// Jump to the window a session lives in, and the history used for speed statistics (reads longer stretches of messages from the log).
//
// Jump (the ↗ to the right of each session name in the Session view):
//   - Session started from the VS Code extension (entrypoint = claude-vscode, almost always): first find the VS Code window whose title contains the session's folder name and bring it to the front;
//     then use vscode://anthropic.claude-code/open?session=<session ID> to have the extension open / return to this session in that window
//     (in extension 2.1.318 the handler of this address runs primaryEditor.open in "the VS Code window that currently has focus", so the right window must be brought to the front first).
//   - Session started in a terminal: walk up the parent processes to the one that has a window (Windows Terminal / console) and bring it to the front. With several tabs in Windows Terminal only the window can be raised, not a tab.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Stilla
{
    internal static class SessionJump
    {
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        public sealed class Win
        {
            public IntPtr Hwnd;
            public string Title = "";
            public uint Pid;
        }

        private static string ProcName(uint pid)
        {
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    return p.ProcessName;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        // All currently visible top-level VS Code windows (process name Code, class name Chrome_WidgetWin_1, non-empty title)
        public static List<Win> VsCodeWindows()
        {
            List<Win> list = new List<Win>();
            Dictionary<uint, string> names = new Dictionary<uint, string>();
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (!IsWindowVisible(h))
                {
                    return true;
                }
                int n = GetWindowTextLength(h);
                if (n <= 0)
                {
                    return true;
                }
                StringBuilder cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                if (cls.ToString() != "Chrome_WidgetWin_1")
                {
                    return true;
                }
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                string pn;
                if (!names.TryGetValue(pid, out pn))
                {
                    pn = ProcName(pid);
                    names[pid] = pn;
                }
                if (!pn.StartsWith("Code", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                StringBuilder sb = new StringBuilder(n + 1);
                GetWindowText(h, sb, n + 1);
                Win w = new Win();
                w.Hwnd = h;
                w.Title = sb.ToString();
                w.Pid = pid;
                list.Add(w);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // Pick the window whose title contains the workspace folder name: try the session's folder first (last level of cwd), then up to 3 levels above it. Returns null if none matches
        public static Win PickWindow(List<Win> wins, string cwd, string projectName)
        {
            List<string> comps = new List<string>();
            if (!string.IsNullOrEmpty(cwd))
            {
                string[] parts = cwd.TrimEnd('\\', '/').Split('\\', '/');
                for (int i = parts.Length - 1; i >= 0 && comps.Count < 4; i--)
                {
                    if (parts[i].Length > 0 && parts[i].IndexOf(':') < 0)
                    {
                        comps.Add(parts[i]);
                    }
                }
            }
            if (!string.IsNullOrEmpty(projectName) && !comps.Contains(projectName))
            {
                comps.Insert(0, projectName);
            }
            // First look for a window whose title has a whole segment equal to this folder name (VS Code title: file - folder name - Visual Studio Code), so that a short folder name such as src / app does not grab an unrelated window
            foreach (string c in comps)
            {
                foreach (Win w in wins)
                {
                    foreach (string seg in w.Title.Split(new string[] { " - " }, StringSplitOptions.None))
                    {
                        string s = seg.Trim();
                        if (string.Equals(s, c, StringComparison.OrdinalIgnoreCase) || s.StartsWith(c + " (", StringComparison.OrdinalIgnoreCase) || s.StartsWith(c + " [", StringComparison.OrdinalIgnoreCase))
                        {
                            return w;
                        }
                    }
                }
            }
            // If that fails, fall back: the title contains these characters (names that are too short do not count; one or two letters match almost any window)
            foreach (string c in comps)
            {
                if (c.Length < 3)
                {
                    continue;
                }
                foreach (Win w in wins)
                {
                    string t = w.Title.Replace("Visual Studio Code", "");     // Every VS Code window title contains these words, so they must not take part in the match (a folder called code / studio would match any window)
                    if (t.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return w;
                    }
                }
            }
            return null;
        }

        private static void Focus(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, 9);        // SW_RESTORE
            }
            SetForegroundWindow(hwnd);
        }

        private static Dictionary<uint, uint> ParentMap()
        {
            Dictionary<uint, uint> map = new Dictionary<uint, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);       // TH32CS_SNAPPROCESS
            if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            {
                return map;
            }
            try
            {
                ProcessEntry e = new ProcessEntry();
                e.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                if (Process32FirstW(snap, ref e))
                {
                    do
                    {
                        map[e.th32ProcessID] = e.th32ParentProcessID;
                    } while (Process32NextW(snap, ref e));
                }
            }
            finally
            {
                CloseHandle(snap);
            }
            return map;
        }

        // Session in a terminal: walk up the parent processes to the first one with a main window (Windows Terminal / console) and bring it to the front
        private static bool FocusTerminalOf(int pid)
        {
            Dictionary<uint, uint> parents = ParentMap();
            uint cur = (uint)pid;
            for (int depth = 0; depth < 8 && cur != 0; depth++)
            {
                try
                {
                    using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)cur))
                    {
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero)
                        {
                            Focus(h);
                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                }
                uint next;
                if (!parents.TryGetValue(cur, out next) || next == cur)
                {
                    break;
                }
                cur = next;
            }
            return false;
        }

        // Jump to the session. Returns null = all went well; otherwise a sentence to tell the user (window not found, etc.). Call it on a background thread (it has to wait for the window switch)
        public static string Go(string sessionId, string projectName)
        {
            SessionStatus.Info si = SessionStatus.Get(sessionId);
            string cwd = si != null ? si.Cwd : "";
            bool vscode = si == null || si.Entrypoint.Length == 0 || si.Entrypoint.StartsWith("claude-vscode", StringComparison.OrdinalIgnoreCase);
            if (!vscode && si != null && si.Pid > 0)
            {
                return FocusTerminalOf(si.Pid) ? null : "Could not find the window of this terminal session.";
            }
            string note = null;
            List<Win> wins = VsCodeWindows();
            Win w = PickWindow(wins, cwd, projectName);
            if (w != null)
            {
                Focus(w.Hwnd);
                Thread.Sleep(350);          // Wait until the window really has focus; the URI is handed to "the VS Code window that currently has focus"
            }
            else if (wins.Count > 0)
            {
                note = "No VS Code window with '" + (string.IsNullOrEmpty(projectName) ? "this session's folder" : projectName) + "' in its title was found; the session will open in the frontmost VS Code window.";
            }
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                "vscode://anthropic.claude-code/open?session=" + Uri.EscapeDataString(sessionId));
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
            return note;
        }
    }

    // History used for speed statistics: reads every message of every session of the last 8 days from the usage logs (time, model, tier, token types).
    // Live tracking keeps only the last 3 hours; this is used when the speed window is stretched to dozens of hours, or to compute the "average speed while it was active" of a session that has gone idle.
    // The same pass can also add up each session's "all-time" usage (tokens per model) for the Session view's "Cost" column (cumulative)
    internal static class SpeedHistory
    {
        public sealed class Result
        {
            public Dictionary<string, List<UsageTracker.RecentUse>> Recent = new Dictionary<string, List<UsageTracker.RecentUse>>();
            public Dictionary<string, Dictionary<string, long[]>> Life = new Dictionary<string, Dictionary<string, long[]>>();   // Session -> model -> 10 token fields
        }

        public static Dictionary<string, List<UsageTracker.RecentUse>> Read(string detailPath, DateTime sinceLocal)
        {
            return ReadAll(detailPath, sinceLocal, false).Recent;
        }

        public static Result ReadAll(string detailPath, DateTime sinceLocal, bool wantLife)
        {
            Result res = new Result();
            if (!File.Exists(detailPath))
            {
                return res;
            }
            TagStore.Refresh();
            using (FileStream fs = new FileStream(detailPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    int comma = line.IndexOf(',');
                    DateTime t;
                    if (comma <= 0 || !DateTime.TryParseExact(line.Substring(0, comma), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        continue;
                    }
                    bool recent = t >= sinceLocal;
                    if (!recent && !wantLife)
                    {
                        continue;
                    }
                    string[] f = LogSummary.SplitCsv(line);
                    if (f.Length < 11 || f[3].Length == 0 || f[3].StartsWith("<", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    long inp = LogSummary.ToLong(f[4]), outp = LogSummary.ToLong(f[5]), cw5 = LogSummary.ToLong(f[6]), cw1 = LogSummary.ToLong(f[7]), cr = LogSummary.ToLong(f[8]);
                    if (recent)
                    {
                        UsageTracker.RecentUse ru = new UsageTracker.RecentUse();
                        ru.Utc = t.ToUniversalTime();
                        ru.Model = f[3];
                        ru.Tier = TagStore.TierKey(TagStore.Get(f[10]));
                        UsageTracker.AddTokens(ru.Tok, inp, outp, cw5, cw1, cr);
                        List<UsageTracker.RecentUse> list;
                        if (!res.Recent.TryGetValue(f[1], out list))
                        {
                            list = new List<UsageTracker.RecentUse>();
                            res.Recent[f[1]] = list;
                        }
                        list.Add(ru);
                    }
                    if (wantLife)
                    {
                        Dictionary<string, long[]> byModel;
                        if (!res.Life.TryGetValue(f[1], out byModel))
                        {
                            byModel = new Dictionary<string, long[]>();
                            res.Life[f[1]] = byModel;
                        }
                        long[] tok;
                        if (!byModel.TryGetValue(f[3], out tok))
                        {
                            tok = new long[10];
                            byModel[f[3]] = tok;
                        }
                        UsageTracker.AddTokens(tok, inp, outp, cw5, cw1, cr);
                    }
                }
            }
            foreach (List<UsageTracker.RecentUse> l in res.Recent.Values)
            {
                l.Sort(delegate(UsageTracker.RecentUse a, UsageTracker.RecentUse b) { return a.Utc.CompareTo(b.Utc); });
            }
            return res;
        }
    }
}
