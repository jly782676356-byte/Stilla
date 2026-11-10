// 额度数据来源（只在悬浮窗 Stilla.exe 里用；文件路径和状态栏小程序在 Shared.cs）
//
//   QuotaSource       5 小时 / 每周额度从哪来：
//                       · 状态栏：Claude Code 官方的 statusLine 功能，把额度用 stdin 传给 Stilla.Statusline.exe，
//                         它写到 rate_limits.json，悬浮窗读这个文件。不读登录令牌、不联网。
//                       · 在线查询（实验性）：原来的做法，读本机登录令牌，请求 Claude Code /usage 用的接口（UsageClient）。
//                       · 自动（默认）：状态栏优先；状态栏没有新数据时才在线查询，而且只有用户点过「允许」之后（OnlineConsent）。
//                       · 演示：STILLA_DEMO=1 / --demo，返回固定的假数据，截图和界面测试用。
//   StatusLineBridge  读状态栏小程序写下的 rate_limits.json。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Stilla
{
    internal static class QuotaSource
    {
        public const int StatusLine = 0;
        public const int Api = 1;
        public const int Auto = 2;

        // 0 = 只用官方状态栏，1 = 在线查询（实验性），2 = 自动（默认：状态栏优先，没有新数据时才在线查询）。保存在 settings.txt 的 src=
        // 为什么默认「自动」：Claude Code 命令行会定期运行状态栏命令，VS Code 扩展 / 桌面端不一定会（官方没有明确承诺），
        // 只靠状态栏的话，这两种用户一打开可能什么数据都没有。
        public static int Mode = Auto;

        // 「自动」模式回退到在线查询前要先问用户（在线查询会读登录令牌、请求没有公开文档的接口）：0 = 还没问过，1 = 同意，2 = 拒绝。保存在 settings.txt 的 onl=
        // 在线查询模式（Mode = Api）是用户自己选的，选的时候已经弹过风险提示，不再看这个值。
        public static int OnlineConsent = 0;

        // 这一轮查询里「自动」模式本来要回退到在线查询，但用户还没回答过：悬浮窗看到它为 true 就弹一次提问
        public static bool WantsOnline;

        private const int FreshMinutes = 10;

        public static FetchResult Query()
        {
            if (AppPaths.Demo)
            {
                return DemoResult();
            }
            if (Mode == Api)
            {
                return UsageClient.Query();
            }
            FetchResult b = StatusLineBridge.Read();
            if (Mode != Auto)
            {
                return b;
            }
            if (b.Problem == Problem.None && (DateTime.Now - b.FetchedAt).TotalMinutes < FreshMinutes)
            {
                return b;
            }
            if (OnlineConsent != 1)
            {
                // 没有得到同意就不碰登录令牌、不联网：只显示状态栏的结果（可能是旧数据或「没收到」）
                WantsOnline = OnlineConsent == 0;
                return b;
            }
            FetchResult a = UsageClient.Query();
            if (a.Problem == Problem.None)
            {
                return a;
            }
            return b.Problem == Problem.None ? b : a;       // 在线查询也失败：有旧的状态栏数据就先显示旧的
        }

        private static FetchResult DemoResult()
        {
            FetchResult r = new FetchResult();
            DateTime now = DateTime.Now;
            r.FiveHour = new Quota();
            r.FiveHour.Percent = 58;
            r.FiveHour.ResetsAt = StatusLineBridge.RoundMinute(now.AddMinutes(71));
            r.SevenDay = new Quota();
            r.SevenDay.Percent = 61;
            r.SevenDay.ResetsAt = StatusLineBridge.RoundMinute(now.AddHours(64));
            r.FetchedAt = now;
            r.Via = "demo";
            r.Problem = Problem.None;
            r.RetrySeconds = 60;
            return r;
        }
    }

    internal static class StatusLineBridge
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DateTime RoundMinute(DateTime local)
        {
            long m = TimeSpan.TicksPerMinute;
            return new DateTime((local.Ticks + m / 2) / m * m, local.Kind);
        }

        public static FetchResult Read()
        {
            FetchResult r = new FetchResult();
            r.Problem = Problem.NoBridge;
            r.RetrySeconds = 10;

            string text;
            try
            {
                using (FileStream fs = new FileStream(StatusLineCli.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                }
            }
            catch (FileNotFoundException)
            {
                return r;
            }
            catch (DirectoryNotFoundException)
            {
                return r;
            }
            catch (Exception)
            {
                r.Detail = "读取失败";
                return r;
            }

            Dictionary<string, object> root = null;
            try
            {
                root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (Exception)
            {
            }
            if (root == null)
            {
                r.Detail = "文件格式不对";
                return r;
            }

            DateTime now = DateTime.Now;
            r.FiveHour = ReadWindow(root, "five_hour", now);
            r.SevenDay = ReadWindow(root, "seven_day", now);
            double saved = 0;
            object so;
            if (root.TryGetValue("saved_at", out so) && so != null)
            {
                saved = StatusLineCli.ToDouble(so);
            }
            r.FetchedAt = saved > 0 ? FromEpoch(saved) : now;
            if (r.FiveHour == null && r.SevenDay == null)
            {
                // 两个窗口都已经重置过了：旧数字没有意义，等 Claude Code 下一次更新
                r.Detail = "窗口已重置";
                return r;
            }
            r.Via = "bridge";
            r.Problem = Problem.None;
            r.RetrySeconds = 5;     // 只是读一个本地小文件，可以勤一点
            return r;
        }

        // 没有这个窗口、或者它的重置时间已经过了，就当没有
        private static Quota ReadWindow(Dictionary<string, object> root, string key, DateTime now)
        {
            object o;
            Dictionary<string, object> d;
            if (!root.TryGetValue(key, out o) || (d = o as Dictionary<string, object>) == null)
            {
                return null;
            }
            object u, t;
            if (!d.TryGetValue("used_percentage", out u) || u == null)
            {
                return null;
            }
            Quota q = new Quota();
            q.Percent = Math.Max(0.0, StatusLineCli.ToDouble(u));
            if (d.TryGetValue("resets_at", out t) && t != null)
            {
                double secs = StatusLineCli.ToDouble(t);
                if (secs > 0)
                {
                    q.ResetsAt = RoundMinute(FromEpoch(secs));
                    if (q.ResetsAt.Value <= now)
                    {
                        return null;
                    }
                }
            }
            return q;
        }

        private static DateTime FromEpoch(double secs)
        {
            return Epoch.AddSeconds(secs).ToLocalTime();
        }

        // 状态栏小程序和 Stilla.exe 放在同一个文件夹
        public static string CliPath
        {
            get { return Path.Combine(AppPaths.ExeDir, "Stilla.Statusline.exe"); }
        }

        // 给用户抄的 settings.json 片段
        public static string SettingsSnippet()
        {
            string exe = CliPath.Replace("\\", "\\\\");
            return "{\r\n"
                + "  \"statusLine\": {\r\n"
                + "    \"type\": \"command\",\r\n"
                + "    \"command\": \"\\\"" + exe + "\\\" --quiet\"\r\n"
                + "  }\r\n"
                + "}";
        }
    }
}
