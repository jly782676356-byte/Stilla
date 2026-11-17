// 跳转到会话所在的窗口，以及速度统计用的历史（从日志里读更长时间的消息）。
//
// 跳转（会话视图每行名字右边的 ↗）：
//   · 会话是 VS Code 扩展里开的（entrypoint = claude-vscode，几乎都是）：先按会话所在文件夹名找到标题里带这个名字的 VS Code 窗口，切到前台；
//     再用 vscode://anthropic.claude-code/open?session=<会话ID> 让扩展在这个窗口里打开 / 回到这个会话
//     （扩展 2.1.318 里这个地址的处理器会在「当前获得焦点的 VS Code 窗口」里执行 primaryEditor.open，所以必须先把对的窗口切到前台）。
//   · 会话是终端里开的：顺着进程的父进程往上找有窗口的那个（Windows Terminal / 控制台），切到前台。Windows Terminal 里多个标签只能切到窗口，选不了标签。

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

        // 现在所有可见的 VS Code 顶层窗口（进程名 Code、类名 Chrome_WidgetWin_1、标题不为空）
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

        // 挑「工作区文件夹名出现在窗口标题里」的那个窗口：先试会话所在文件夹（cwd 的最后一级），再往上最多 3 级。挑不到返回 null
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
            // 先找标题里有「一整段」正好是这个文件夹名的（VS Code 标题：文件 - 文件夹名 - Visual Studio Code），免得文件夹叫 src / app 这类短名字时随便抓到一个无关的窗口
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
            // 找不到再退一步：标题里带这几个字（太短的名字不算，一两个字母几乎什么窗口都能匹配上）
            foreach (string c in comps)
            {
                if (c.Length < 3)
                {
                    continue;
                }
                foreach (Win w in wins)
                {
                    string t = w.Title.Replace("Visual Studio Code", "");     // 每个 VS Code 窗口的标题都带这几个字，不能参与匹配（文件夹叫 code / studio 时会抓到任意窗口）
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

        // 终端里的会话：顺着父进程往上，第一个有主窗口的进程（Windows Terminal / 控制台）切到前台
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

        // 跳到会话。返回 null = 一切顺利；否则是一句要告诉你的话（没找到窗口等）。在后台线程里调用（中间要等窗口切过去）
        public static string Go(string sessionId, string projectName)
        {
            SessionStatus.Info si = SessionStatus.Get(sessionId);
            string cwd = si != null ? si.Cwd : "";
            bool vscode = si == null || si.Entrypoint.Length == 0 || si.Entrypoint.StartsWith("claude-vscode", StringComparison.OrdinalIgnoreCase);
            if (!vscode && si != null && si.Pid > 0)
            {
                return FocusTerminalOf(si.Pid) ? null : "没找到这个终端会话的窗口。";
            }
            string note = null;
            List<Win> wins = VsCodeWindows();
            Win w = PickWindow(wins, cwd, projectName);
            if (w != null)
            {
                Focus(w.Hwnd);
                Thread.Sleep(350);          // 等窗口真正拿到焦点；URI 会交给「当前获得焦点的 VS Code 窗口」
            }
            else if (wins.Count > 0)
            {
                note = "没找到标题里带「" + (string.IsNullOrEmpty(projectName) ? "这个会话的文件夹" : projectName) + "」的 VS Code 窗口，会话会在当前最前面的 VS Code 窗口里打开。";
            }
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                "vscode://anthropic.claude-code/open?session=" + Uri.EscapeDataString(sessionId));
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
            return note;
        }
    }

    // 速度统计用的历史：从用量日志里读最近 8 天每个会话的每条消息（时间、模型、档位、各类 token）。
    // 实时追踪只保留最近 3 小时，速度窗口拉长到几十小时、或者要算已经闲下来的会话「过去活动时的平均速度」时就用它。
    // 同一遍还可以顺手累计每个会话的「全部历史」用量（按模型分开的 token），给会话视图的「成本」列（累计）用
    internal static class SpeedHistory
    {
        public sealed class Result
        {
            public Dictionary<string, List<UsageTracker.RecentUse>> Recent = new Dictionary<string, List<UsageTracker.RecentUse>>();
            public Dictionary<string, Dictionary<string, long[]>> Life = new Dictionary<string, Dictionary<string, long[]>>();   // 会话 → 模型 → 10 项 token
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
