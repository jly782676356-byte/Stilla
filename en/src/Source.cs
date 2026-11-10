// Quota data sources (only used in the widget Stilla.exe; file paths and the status line helper are in Shared.cs)
//
//   QuotaSource       where the 5-hour / weekly quota comes from:
//                       · status line: Claude Code's official statusLine feature passes the quota on stdin to Stilla.Statusline.exe,
//                         which writes it to rate_limits.json, and the widget reads that file. It reads no login token and uses no network.
//                       - Online query (experimental): the original approach, which reads the local login token and calls the endpoint Claude Code's /usage uses (UsageClient).
//                       · Auto (default): status line first; the online query is only used when the status line has no fresh data, and only after the user has clicked "Allow" (OnlineConsent).
//                       - Demo: STILLA_DEMO=1 / --demo returns fixed fake data, for screenshots and UI tests.
//   StatusLineBridge  reads the rate_limits.json written by the status line helper.

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

        // 0 = status line only, 1 = online query (experimental), 2 = Auto (default: status line first, online query only when there is no fresh data). Stored as src= in settings.txt
        // Why "Auto" is the default: the Claude Code command line runs the status-line command regularly, but the VS Code extension / desktop app may not (the official docs make no promise),
        // so with the status line alone these users might see no data at all when they first open Stilla.
        public static int Mode = Auto;

        // Before the "Auto" mode falls back to the online query it has to ask the user (the online query reads the login token and calls an undocumented endpoint): 0 = not asked yet, 1 = agreed, 2 = declined. Stored as onl= in settings.txt
        // The online-query mode (Mode = Api) is chosen by the user and already showed the risk notice when it was chosen, so this value is not consulted there.
        public static int OnlineConsent = 0;

        // True when, in this query, the "Auto" mode wanted to fall back to the online query but the user has not answered yet: the widget shows the question once when it sees this
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
                // Without consent: do not touch the login token and do not use the network, just show the status-line result (which may be old data or "nothing received")
                WantsOnline = OnlineConsent == 0;
                return b;
            }
            FetchResult a = UsageClient.Query();
            if (a.Problem == Problem.None)
            {
                return a;
            }
            return b.Problem == Problem.None ? b : a;       // Online query failed too: if there is old status line data show that for now
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
                r.Detail = "Read failed";
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
                r.Detail = "Bad file format";
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
                // Both windows have already reset: the old numbers mean nothing, wait for Claude Code's next update
                r.Detail = "Window reset";
                return r;
            }
            r.Via = "bridge";
            r.Problem = Problem.None;
            r.RetrySeconds = 5;     // It is only reading a small local file, so it can be frequent
            return r;
        }

        // There is no such window, or its reset time has already passed, so treat it as absent
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

        // The status line helper and Stilla.exe are in the same folder
        public static string CliPath
        {
            get { return Path.Combine(AppPaths.ExeDir, "Stilla.Statusline.exe"); }
        }

        // The settings.json snippet for the user to copy
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
