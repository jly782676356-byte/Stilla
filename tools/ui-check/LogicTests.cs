// Logic tests for Stilla 0.2.0: no screenshots, no window is ever shown, only fake data in temp folders.
//
// Run with tools\ui-check\run-logic.ps1 (it compiles this file together with the product sources, makes the fake data and sets the
// environment variables). Direct use:  LogicTests.exe <workDir> [groupFilter] [--strict]
//
// How it works (same trick as Harness.cs): a real Widget is created but never shown; private fields / methods are reached by reflection.
// AppPaths.Home is cached by the product in a private static, so every test group points it at its own temp folder with UseHome().
//
// Output: "PASS  name", "FAIL  name  -- detail", "KNOWN-BUG  BUG-id  name  -- detail".
//   FAIL        = a regression or an unexpected result: makes the exit code 1.
//   KNOWN-BUG   = the test reproduces a known edge-case bug (listed under "Known issues" in CHANGELOG.md); it does not change the exit code
//                 unless --strict is given. When the bug gets fixed the same line turns into a PASS automatically.
// Non-ASCII text in detail strings is escaped (\uXXXX) so the output survives any console code page.
//
// C# 5 only (the product is compiled with the csc that ships with Windows): no ?., no $"", no nameof, no expression-bodied members.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Stilla
{
    internal static class LogicTests
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private static string work;
        private static string filter = "";
        private static bool strict;
        private static int homeSeq;
        private static int pass, fail, knownBugs;
        private static readonly List<string> failures = new List<string>();
        private static readonly List<string> knownList = new List<string>();
        private static readonly List<string> notes = new List<string>();
        private static readonly List<Widget> live = new List<Widget>();
        private static readonly StringBuilder outLog = new StringBuilder();
        private static bool haveDemoData;

        // ------------------------------------------------------------------ reporting
        private static string Esc(string s)
        {
            if (s == null)
            {
                return "null";
            }
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32 || c > 126) sb.Append("\\u" + ((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void Emit(string line)
        {
            Console.WriteLine(line);
            outLog.AppendLine(line);
        }

        private static void Check(string name, bool ok)
        {
            Check(name, ok, null);
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                pass++;
                Emit("PASS  " + name);
            }
            else
            {
                fail++;
                failures.Add(name);
                Emit("FAIL  " + name + (detail != null ? "  -- " + detail : ""));
            }
        }

        // A product bug that is already written up: prints KNOWN-BUG while it reproduces, turns into PASS once fixed.
        private static void KnownBug(string id, string name, bool ok, string detail)
        {
            if (ok)
            {
                pass++;
                Emit("PASS  " + name + "  (was " + id + ", looks fixed now)");
            }
            else
            {
                knownBugs++;
                knownList.Add(id + "  " + name);
                Emit("KNOWN-BUG  " + id + "  " + name + (detail != null ? "  -- " + detail : ""));
            }
        }

        private static void Note(string text)
        {
            notes.Add(text);
            Emit("NOTE  " + text);
        }

        private static void Group(string name, Action body)
        {
            if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }
            Emit("");
            Emit("== " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                fail++;
                failures.Add(name + " (group threw)");
                Emit("FAIL  " + name + "  -- group threw " + ex.GetType().Name + ": " + Esc(ex.Message));
                Emit(Esc(ex.ToString()));
            }
            finally
            {
                foreach (Widget w in live)
                {
                    try
                    {
                        w.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
                live.Clear();
            }
        }

        // ------------------------------------------------------------------ reflection helpers
        private static object Get(object o, string field)
        {
            FieldInfo f = o.GetType().GetField(field, Inst);
            if (f == null)
            {
                throw new Exception("no field " + field);
            }
            return f.GetValue(o);
        }

        private static T G<T>(object o, string field)
        {
            return (T)Get(o, field);
        }

        private static void Set(object o, string field, object v)
        {
            FieldInfo f = o.GetType().GetField(field, Inst);
            if (f == null)
            {
                throw new Exception("no field " + field);
            }
            f.SetValue(o, v);
        }

        private static object Call(object o, string method, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethod(method, Inst);
            if (m == null)
            {
                throw new Exception("no method " + method);
            }
            try
            {
                return m.Invoke(o, args);
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(method + " threw " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message, ex.InnerException);
            }
        }

        // private static method of Widget (no overloads of that name)
        private static object CallStatic(Type t, string method, object[] args)
        {
            MethodInfo m = t.GetMethod(method, Stat);
            if (m == null)
            {
                throw new Exception("no static method " + method);
            }
            try
            {
                return m.Invoke(null, args);
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(method + " threw " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message, ex.InnerException);
            }
        }

        private static string WS(string method, params object[] args)
        {
            return (string)CallStatic(typeof(Widget), method, args);
        }

        // ------------------------------------------------------------------ temp homes / state
        private static string NewHome(string tag)
        {
            homeSeq++;
            string h = Path.Combine(work, "h" + homeSeq.ToString("00", CultureInfo.InvariantCulture) + "_" + tag);
            Directory.CreateDirectory(h);
            return h;
        }

        // Point the product at this folder as %APPDATA%\Stilla. AppPaths caches the folder in a private static, so reset it by reflection.
        private static void UseHome(string home)
        {
            string full = Path.GetFullPath(home);
            if (!full.StartsWith(work, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("refusing to use a home outside the work folder: " + full);
            }
            Directory.CreateDirectory(full);
            Environment.SetEnvironmentVariable("STILLA_HOME", full);
            typeof(AppPaths).GetField("home", Stat).SetValue(null, null);
            if (!string.Equals(AppPaths.Home, full, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("AppPaths.Home did not follow STILLA_HOME: " + AppPaths.Home);
            }
        }

        // What a freshly started process has (the product keeps some settings in statics)
        private static void ResetStatics()
        {
            QuotaSource.Mode = QuotaSource.Auto;
            QuotaSource.OnlineConsent = 0;
            QuotaSource.WantsOnline = false;
            UpdateState.Auto = true;
            UpdateState.Latest = "";
            MailReset();
        }

        private static void MailReset()
        {
            MailStore.Enabled = false;
            MailStore.Host = "smtp.gmail.com";
            MailStore.Port = 587;
            MailStore.Ssl = true;
            MailStore.User = "";
            MailStore.From = "";
            MailStore.To = "";
            MailStore.Password = "";
            MailStore.Attempts = 0;
            MailStore.LastStatus = "";
            lock (MailStore.Watches)
            {
                MailStore.Watches.Clear();
            }
        }

        private static Widget NewWidget(string home, string[] settingsLines)
        {
            UseHome(home);
            ResetStatics();
            string path = Path.Combine(home, "settings.txt");
            if (settingsLines != null)
            {
                File.WriteAllLines(path, settingsLines, new UTF8Encoding(false));
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            Widget w = new Widget();
            live.Add(w);
            return w;
        }

        private static Dictionary<string, string> ReadKv(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    d[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            return d;
        }

        private static string KvGet(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : "<missing>";
        }

        private static string Snap(Widget w)
        {
            StringBuilder sb = new StringBuilder();
            string[] names = { "bgPercent", "fgPercent", "showUsage", "winWu", "winHu", "usageMode", "speedMin", "skinIndex", "inverted", "tabMask",
                "rowHu", "alertMin", "limitWarn", "tabPreset", "costScope", "trendRange", "tipsOn", "alertsOn", "profile", "fmask", "vmask", "smask", "dmask",
                "customFromMin", "customToMin" };      // savedX / savedY are left out on purpose: an unshown form saves Left/Top = 0
            foreach (string n in names)
            {
                sb.Append(n).Append('=').Append(Convert.ToString(Get(w, n), CultureInfo.InvariantCulture)).Append(' ');
            }
            sb.Append("TopMost=").Append(w.TopMost).Append(' ');
            sb.Append("sortCol=").Append(string.Join(",", Array.ConvertAll<int, string>(G<int[]>(w, "sortCol"), delegate(int i) { return i.ToString(CultureInfo.InvariantCulture); }))).Append(' ');
            sb.Append("colFr=").Append(string.Join(",", Array.ConvertAll<double, string>(G<double[]>(w, "colFr"), delegate(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }))).Append(' ');
            sb.Append("src=").Append(QuotaSource.Mode).Append(" onl=").Append(QuotaSource.OnlineConsent).Append(" upd=").Append(UpdateState.Auto);
            return sb.ToString();
        }

        // the 0.1.1 settings.txt as published (keys of SaveSettings in Stilla_V1.1 / 0.1.1)
        private static string[] V011(int mode)
        {
            return new string[]
            {
                "x=1500", "y=60", "bg=70", "fg=85", "top=1", "usage=1", "w=420", "h2=500", "log=1", "src=2", "onl=1", "ds=0", "upd=1",
                "mode=" + mode, "dark=1", "skin=7", "tabs=1258", "c0=280", "c1=110", "c2=130", "c3=120", "c4=120", "c5=120", "cfrom=1000", "cto=2000"
            };
        }

        private static bool Has(string s, string part)
        {
            return s != null && s.IndexOf(part, StringComparison.Ordinal) >= 0;
        }

        private static bool HasI(string s, string part)
        {
            return s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ================================================================== entry
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage: LogicTests <workDir> [groupFilter] [--strict]");
                return 2;
            }
            Console.OutputEncoding = new UTF8Encoding(false);
            work = Path.GetFullPath(args[0]);
            string cultureName = "";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--strict") strict = true;
                else if (args[i].StartsWith("--culture=", StringComparison.Ordinal)) cultureName = args[i].Substring(10);
                else if (args[i].Length > 0) filter = args[i];
            }
            if (cultureName.Length > 0)
            {
                // run the whole suite as a user with another regional setting (decimal comma, other minus sign, Turkish i ...)
                CultureInfo ci = new CultureInfo(cultureName);
                CultureInfo.DefaultThreadCurrentCulture = ci;
                CultureInfo.DefaultThreadCurrentUICulture = ci;
                Thread.CurrentThread.CurrentCulture = ci;
                Thread.CurrentThread.CurrentUICulture = ci;
            }
            Directory.CreateDirectory(work);

            // everything the product could read or write must live inside the work folder
            Environment.SetEnvironmentVariable("STILLA_ISOLATED", "1");
            Environment.SetEnvironmentVariable("STILLA_DEMO", "1");
            Environment.SetEnvironmentVariable("STILLA_NO_UPDATE_CHECK", "1");
            string claude = Path.Combine(work, "claude");
            Directory.CreateDirectory(claude);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claude);
            haveDemoData = Directory.Exists(Path.Combine(claude, "projects"));
            UseHome(NewHome("boot"));
            if (!AppPaths.Home.StartsWith(work, StringComparison.OrdinalIgnoreCase) || !ClaudeRoots.Primary().StartsWith(work, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("ABORT: the product would use a folder outside the work folder.");
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Emit("Stilla logic tests  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  work=" + work);
            Emit("fake Claude data present: " + haveDemoData + "   .NET " + Environment.Version + "   culture " + CultureInfo.CurrentCulture.Name + (strict ? "   (strict)" : ""));

            Group("A1 fresh-process defaults (statics, before any widget exists)", TestFreshStatics);
            Group("B1 settings: upgrade from 0.1.1", TestUpgrade011);
            Group("B2 settings: brand-new install", TestFreshInstall);
            Group("B3 settings: corrupted / hostile settings.txt", TestCorrupted);
            Group("B4 settings: masks and views after load", TestMasksOnLoad);
            Group("B5 settings: window size / position sanity", TestWindowSanity);
            Group("C1 Features.cs: presets, Identify, Cadence, names", TestFeatures);
            Group("C2 Features.cs: SetMasks / ApplyPreset on a live widget", TestApplyPreset);
            Group("D1 Mail.cs: store, DPAPI password, ParseMins, corrupted files", TestMailStore);
            Group("D2 Mail.cs: line-break injection, message building (no network)", TestMailInjection);
            Group("D3 Mail.cs: settings dialog validation (MailForm.CurrentConfig)", TestMailForm);
            Group("D4 Mail trigger guards (CheckMailAlerts, send path never reached)", TestMailTriggerGuards);
            Group("E1 Report.cs: weekly HTML report from fake log data", TestReport);
            Group("E2 Report.cs: accounts / plans / hostile ids", TestReportAccounts);
            Group("F1 WidgetExtra.cs: weekly auto-report scheduling", TestWeeklySchedule);
            Group("G1 speed / occupancy / grey average math", TestSpeedMath);
            Group("G2 cap forecast text, HoursText / RateText / UsdText, NaN and Infinity", TestForecastText);
            Group("G3 weekly forecast", TestWeeklyForecast);
            Group("G4 estimate self-check (ComputeHealth)", TestHealth);
            Group("G5 status lights (CacheState) and overall indicator", TestStatusLights);
            Group("G6 cache-expiry system notification rules and text", TestCacheAlerts);
            Group("G7 quota limit warning (once per window)", TestLimitWarn);
            Group("M1 status-line file reader (rate_limits.json)", TestStatusLineFile);
            Group("O1 API view data and DeepSeek price estimate", TestApiView);
            Group("H1 Jump.cs: window matching (pure parts)", TestJump);
            Group("I1 exports: CSV quoting, ExportTable, formula cells", TestExports);
            Group("J1 sorting: click cycle, persistence, SortStable, ApplySessionSort", TestSorting);
            Group("K1 other: update checker, hover-tip hit test, QuotaTable.Load, counts", TestMisc);
            Group("L1 render smoke: draw frames for views x profiles x masks x sort x data", TestRenderSmoke);
            // @@MORE-CALLS@@

            Emit("");
            Emit("================================================================");
            Emit("PASS " + pass + "   FAIL " + fail + "   KNOWN-BUG " + knownBugs);
            if (failures.Count > 0)
            {
                Emit("Failed:");
                foreach (string f in failures)
                {
                    Emit("  " + f);
                }
            }
            if (knownList.Count > 0)
            {
                Emit("Known bugs reproduced (see the report; not counted unless --strict):");
                foreach (string k in knownList)
                {
                    Emit("  " + k);
                }
            }
            try
            {
                File.WriteAllText(Path.Combine(work, "results.txt"), outLog.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
            if (fail > 0 || (strict && knownBugs > 0))
            {
                return 1;
            }
            return 0;
        }

        // ================================================================== A1
        private static void TestFreshStatics()
        {
            // These are the values a process has before anything is loaded: checked first, before any other group touches the statics.
            Check("quota source default = Auto", QuotaSource.Mode == QuotaSource.Auto);
            Check("online-query consent default = 0 (not asked)", QuotaSource.OnlineConsent == 0);
            Check("update check default = on", UpdateState.Auto);
            Check("mail reminders disabled by default", !MailStore.Enabled);
            Check("mail: no password stored by default", !MailStore.HasPassword && MailStore.Password == "");
            Check("mail: not 'Configured' by default", !MailStore.Configured);
            Check("mail: no session watches by default", MailStore.WatchCount == 0);
            Check("mail defaults: gmail / 587 / ssl", MailStore.Host == "smtp.gmail.com" && MailStore.Port == 587 && MailStore.Ssl);
            Check("mail: Snapshot().From falls back to User when From is empty", MailStore.Snapshot().From == MailStore.User);
            Check("AppPaths.Home follows STILLA_HOME (test isolation works)", AppPaths.Home.StartsWith(work, StringComparison.OrdinalIgnoreCase), AppPaths.Home);
            Check("ClaudeRoots.Primary follows CLAUDE_CONFIG_DIR", ClaudeRoots.Primary().StartsWith(work, StringComparison.OrdinalIgnoreCase), ClaudeRoots.Primary());
        }

        // ================================================================== B1 upgrade from 0.1.1
        private static void TestUpgrade011()
        {
            string home = NewHome("upgrade011");
            Widget w = NewWidget(home, V011(3));
            string tag = "0.1.1 file: ";
            Check(tag + "view mode=3 (Tier) -> Compare (2)", G<int>(w, "usageMode") == 2, "usageMode=" + G<int>(w, "usageMode"));
            Check(tag + "skin 7 kept", G<int>(w, "skinIndex") == 7, "skinIndex=" + G<int>(w, "skinIndex"));
            Check(tag + "dark mode kept", G<bool>(w, "inverted"));
            Check(tag + "tabs mask kept (1258)", G<int>(w, "tabMask") == 1258);
            Check(tag + "background / text transparency kept (70 / 85)", G<int>(w, "bgPercent") == 70 && G<int>(w, "fgPercent") == 85);
            Check(tag + "always-on-top kept", w.TopMost);
            Check(tag + "usage panel kept on", G<bool>(w, "showUsage"));
            Check(tag + "quota source Auto kept", QuotaSource.Mode == QuotaSource.Auto);
            Check(tag + "online consent (onl=1) kept", QuotaSource.OnlineConsent == 1);
            Check(tag + "auto update check kept", UpdateState.Auto);
            Check(tag + "logging kept on", ((UsageLogger)Get(w, "logger")).Enabled);
            Check(tag + "saved window position kept", G<int>(w, "savedX") == 1500 && G<int>(w, "savedY") == 60);
            Check(tag + "custom range kept (cfrom/cto)", G<int>(w, "customFromMin") == 1000 && G<int>(w, "customToMin") == 2000);
            Check(tag + "saved window size dropped once (sv missing)", G<int>(w, "winWu") == 0 && G<int>(w, "winHu") == 0,
                "winWu=" + G<int>(w, "winWu") + " winHu=" + G<int>(w, "winHu"));
            int f = G<int>(w, "fmask"), v = G<int>(w, "vmask"), s = G<int>(w, "smask"), d = G<int>(w, "dmask");
            Check(tag + "profile after loading is Recommended", Feat.Identify(f, v, s, d) == 1 && G<int>(w, "profile") == 1, "profile=" + G<int>(w, "profile"));
            Check(tag + "DeepSeek row hidden", (d & Feat.DsLine) == 0);
            Check(tag + "API view hidden", (v & (1 << 4)) == 0);
            Check(tag + "Input column hidden", (s & (1 << 2)) == 0);
            Check(tag + "alert notifications on by default", G<bool>(w, "alertsOn"));
            Check(tag + "old per-column widths (c0..c5) ignored: cc0 default", Math.Abs(((double[])Get(w, "colFr"))[0] - 0.28) < 0.0005);

            // view mapping for every 0.1.1 view number
            int[] expect = { 0, 1, 2, 2, 3 };
            for (int m = 0; m <= 4; m++)
            {
                Widget wm = NewWidget(home, V011(m));
                Check("0.1.1 view " + m + " (" + new string[] { "Session", "Model", "Compare", "Tier", "Plan" }[m] + ") maps to new view " + expect[m],
                    G<int>(wm, "usageMode") == expect[m], "usageMode=" + G<int>(wm, "usageMode"));
            }
            // old 'modelview=1' (pre-0.1) and a 0.2 file that has both view= and mode=
            Check("pre-0.1 'modelview=1' (no mode=) -> Model view (1)", G<int>(NewWidget(home, new string[] { "modelview=1", "skin=3" }), "usageMode") == 1);
            Check("pre-0.1 'modelview=0' (no mode=) -> Session view (0)", G<int>(NewWidget(home, new string[] { "modelview=0" }), "usageMode") == 0);
            // 0.1.1 users who had switched the DeepSeek row on (ds=1) keep it on after the upgrade; the profile then shows as Custom
            List<string> withDs = new List<string>(V011(3));
            withDs[withDs.IndexOf("ds=0")] = "ds=1";
            Widget wds = NewWidget(home, withDs.ToArray());
            int dd = G<int>(wds, "dmask");
            Check("0.1.1 ds=1 (DeepSeek row on) is carried over: DeepSeek row visible after the upgrade", (dd & Feat.DsLine) != 0);
            Check("... and the rest of the Recommended defaults stay (API view / Input column hidden), profile reads Custom (3)",
                (G<int>(wds, "vmask") & (1 << 4)) == 0 && (G<int>(wds, "smask") & (1 << 2)) == 0 && G<int>(wds, "profile") == 3, "profile=" + G<int>(wds, "profile"));
            Check("0.1.1 ds=0 (the default) leaves the DeepSeek row hidden", (G<int>(NewWidget(home, V011(3)), "dmask") & Feat.DsLine) == 0);
            // a 0.2 file that already has dmask= keeps its own value (ds= is a leftover then)
            List<string> dm = new List<string>(V011(3));
            dm[dm.IndexOf("ds=0")] = "ds=1";
            dm.Add("dmask=" + (Feat.Preset(1)[3] & ~Feat.DsLine));
            Check("dmask= in the file wins over a leftover ds=1", (G<int>(NewWidget(home, dm.ToArray()), "dmask") & Feat.DsLine) == 0);
            List<string> both = new List<string>(V011(3));
            both.Add("view=1");
            Check("view= wins over a leftover mode=", G<int>(NewWidget(home, both.ToArray()), "usageMode") == 1);

            // save -> sv=2, then a second load keeps the user's window size
            w = NewWidget(home, V011(3));
            Call(w, "SaveSettings");
            Dictionary<string, string> kv = ReadKv(Path.Combine(home, "settings.txt"));
            Check("after SaveSettings: sv=2 written", KvGet(kv, "sv") == "2", KvGet(kv, "sv"));
            Check("after SaveSettings: view=2, skin=7, dark=1, tabs=1258, src=2, onl=1, upd=1, usage=1",
                KvGet(kv, "view") == "2" && KvGet(kv, "skin") == "7" && KvGet(kv, "dark") == "1" && KvGet(kv, "tabs") == "1258" && KvGet(kv, "src") == "2"
                && KvGet(kv, "onl") == "1" && KvGet(kv, "upd") == "1" && KvGet(kv, "usage") == "1");
            Check("after SaveSettings: bg=70 fg=85 top=1 cfrom=1000 cto=2000 colv=2 profile=1",
                KvGet(kv, "bg") == "70" && KvGet(kv, "fg") == "85" && KvGet(kv, "top") == "1" && KvGet(kv, "cfrom") == "1000" && KvGet(kv, "cto") == "2000"
                && KvGet(kv, "colv") == "2" && KvGet(kv, "profile") == "1");
            Check("after SaveSettings: legacy keys (mode, ds, c0..c5) are gone", !kv.ContainsKey("mode") && !kv.ContainsKey("ds") && !kv.ContainsKey("c0") && !kv.ContainsKey("c5"));
            Check("after SaveSettings: the dropped window size is written as 0, not as the old 420/500", KvGet(kv, "w") == "0" && KvGet(kv, "h2") == "0");

            Widget w2 = new Widget();
            live.Add(w2);
            Check("second load (same file): view 2 / skin 7 / dark / tabs / consent still there",
                G<int>(w2, "usageMode") == 2 && G<int>(w2, "skinIndex") == 7 && G<bool>(w2, "inverted") && G<int>(w2, "tabMask") == 1258 && QuotaSource.OnlineConsent == 1);
            Check("second load: still Recommended, nothing re-hidden / re-shown", G<int>(w2, "profile") == 1);
            Set(w2, "winWu", 610);
            Set(w2, "winHu", 430);
            Call(w2, "SaveSettings");
            Widget w3 = new Widget();
            live.Add(w3);
            Check("user's window size (610 x 430) survives the next load", G<int>(w3, "winWu") == 610 && G<int>(w3, "winHu") == 430,
                "winWu=" + G<int>(w3, "winWu") + " winHu=" + G<int>(w3, "winHu"));
            Check("whole state identical after save + load", Snap(w2) == Snap(w3), Snap(w2) + "  <>  " + Snap(w3));

            // a 0.1.1 user who had turned logging off: the new masks have to follow
            List<string> nolog = new List<string>(V011(0));
            nolog[nolog.IndexOf("log=1")] = "log=0";
            Widget wn = NewWidget(home, nolog.ToArray());
            Check("0.1.1 'log=0' is honoured (logger off)", !((UsageLogger)Get(wn, "logger")).Enabled);
            Check("0.1.1 'log=0' -> fmask loses the Log bit and the profile reads Custom (3)", (G<int>(wn, "fmask") & Feat.Log) == 0 && G<int>(wn, "profile") == 3,
                "profile=" + G<int>(wn, "profile") + " fmask=" + G<int>(wn, "fmask"));
            Note("a 0.1.1 user with log=0 lands in profile 'Custom' (not Recommended): by design of Identify, but the lead may want to know");
        }

        // ================================================================== B2 fresh install
        private static void TestFreshInstall()
        {
            string home = NewHome("fresh");
            Widget w = NewWidget(home, null);
            string tag = "no settings.txt: ";
            Check(tag + "usage panel on", G<bool>(w, "showUsage"));
            Check(tag + "quota source Auto", QuotaSource.Mode == QuotaSource.Auto);
            Check(tag + "online consent 0", QuotaSource.OnlineConsent == 0);
            Check(tag + "profile Recommended (1)", G<int>(w, "profile") == 1, "profile=" + G<int>(w, "profile"));
            Check(tag + "alerts on", G<bool>(w, "alertsOn"));
            Check(tag + "logging on", ((UsageLogger)Get(w, "logger")).Enabled);
            Check(tag + "update check on", UpdateState.Auto);
            Check(tag + "view = Session (0)", G<int>(w, "usageMode") == 0);
            Check(tag + "default tabs 1258", G<int>(w, "tabMask") == 1258);
            Check(tag + "window size = automatic (0 / 0)", G<int>(w, "winWu") == 0 && G<int>(w, "winHu") == 0);
            Check(tag + "no sort active in any view", G<int[]>(w, "sortCol")[0] == -1 && G<int[]>(w, "sortCol")[1] == -1 && G<int[]>(w, "sortCol")[2] == -1);
            Check(tag + "hover tips on, cost scope 0, speed window 15 min", G<bool>(w, "tipsOn") && G<int>(w, "costScope") == 0 && G<int>(w, "speedMin") == 15);
            Check(tag + "masks = Recommended preset", Feat.Identify(G<int>(w, "fmask"), G<int>(w, "vmask"), G<int>(w, "smask"), G<int>(w, "dmask")) == 1);
            Check(tag + "alert lead time 10 min, limit warning 90 %", G<int>(w, "alertMin") == 10 && G<int>(w, "limitWarn") == 90);

            // first save of a new install writes sv=2 and a window size of 0 (= automatic)
            Call(w, "SaveSettings");
            Dictionary<string, string> kv = ReadKv(Path.Combine(home, "settings.txt"));
            Check(tag + "first save writes sv=2, w=0, h2=0", KvGet(kv, "sv") == "2" && KvGet(kv, "w") == "0" && KvGet(kv, "h2") == "0");
            Widget w2 = new Widget();
            live.Add(w2);
            Check(tag + "save + load round trip leaves every field unchanged", Snap(w) == Snap(w2), Snap(w) + "  <>  " + Snap(w2));

            // zero-byte file, whitespace-only file, file with only comments
            Check("empty settings.txt (0 bytes) behaves like a new install", Snap(NewWidgetRaw(home, new byte[0])) == Snap(NewWidget(NewHome("fresh2"), null)));
            Check("whitespace-only settings.txt behaves like a new install", Snap(NewWidgetRaw(home, Encoding.UTF8.GetBytes("   \r\n\r\n\t\r\n"))) == Snap(NewWidget(NewHome("fresh3"), null)));
        }

        private static Widget NewWidgetRaw(string home, byte[] content)
        {
            UseHome(home);
            ResetStatics();
            File.WriteAllBytes(Path.Combine(home, "settings.txt"), content);
            Widget w = new Widget();
            live.Add(w);
            return w;
        }

        // ================================================================== B3 corrupted
        private static void TestCorrupted()
        {
            string home = NewHome("corrupt");
            string defaults = Snap(NewWidget(NewHome("corrupt-ref"), null));

            // every line below is invalid: the loaded state must equal a new install, without exceptions
            string[] bad =
            {
                "garbage", "=", "====", "x", "x=notanumber", "x=99999999999999999999", "y=-2147483649", "y=",
                "bg=-5", "bg=101", "fg=0", "fg=19", "fg=1000", "w=-420", "w=0", "h2=0", "h2=-1",
                "src=7", "src=-1", "onl=9", "onl=-1", "upd=abc", "alert=x", "log=",
                "tabs=-1", "tabs=0", "tabs=2048", "tabs=999999", "mode=99", "mode=-3", "view=99", "view=-1",
                "speedwin=0", "speedwin=99999", "sort0=-1", "sort1=-2147483648", "sort2=abc", "sort5=3", "sorta=1", "sort=1", "sort00=1",
                "skin=-1", "skin=99999", "costscope=5", "trendr=-2", "tips=9", "rowh=0", "rowh=13", "rowh=41", "rowh=9999",
                "alertmin=0", "alertmin=121", "alertmin=-4", "limitwarn=-3", "limitwarn=101", "tabpreset=-1", "tabpreset=0", "tabpreset=4096",
                "crf9=5", "crt9=5", "crf-1=1", "crf=1", "cc8=1", "cc9=5", "cc0=-5", "cc1=0", "c9=3",
                "\u0001\u0002\u0003=\u0004", "äöü=1", new string('a', 300000), new string('=', 1000) + "1", "x" + new string('9', 400) + "=1", "view=" + new string('9', 400)
            };
            Widget wb = NewWidget(home, bad);
            string got = Snap(wb);
            Check("file with only invalid lines: state equals a new install", got == defaults, "\n   got      " + got + "\n   defaults " + defaults);
            Check("... and the invalid file does not throw while saving back", Try(delegate { Call(wb, "SaveSettings"); }));
            Dictionary<string, string> kv = ReadKv(Path.Combine(home, "settings.txt"));
            Check("... the saved file is clean again (sv=2, no junk keys)", KvGet(kv, "sv") == "2" && !kv.ContainsKey("garbage") && !kv.ContainsKey("sort5"));

            // binary content: NULs, invalid UTF-8, no line breaks
            byte[] bin = new byte[4096];
            Random rnd = new Random(12345);
            rnd.NextBytes(bin);
            Check("random binary settings.txt loads without throwing", Try(delegate { NewWidgetRaw(home, bin); }));
            Check("... and keeps every index in range", RangesOk(live[live.Count - 1]));

            // valid-looking numbers at the edges of int
            string[] edge = { "x=2147483647", "y=-2147483648", "bg=100", "bg=0", "fg=20", "fg=100", "tabs=2047", "view=5", "mode=5", "speedwin=2880", "sort0=2147483647", "sort1=0", "sort2=1", "skin=15", "rowh=40", "alertmin=120", "limitwarn=100" };
            Widget we = NewWidget(home, edge);
            Check("int-edge values load without throwing and in range", RangesOk(we));
            Check("last duplicate wins (bg=100 then bg=0 -> 0; fg=20 then fg=100 -> 100)", G<int>(we, "bgPercent") == 0 && G<int>(we, "fgPercent") == 100);
            Check("rowh=40 / alertmin=120 / limitwarn=100 accepted as given", G<int>(we, "rowHu") == 40 && G<int>(we, "alertMin") == 120 && G<int>(we, "limitWarn") == 100);
            Check("tabs=2047 (all 11 tabs) accepted", G<int>(we, "tabMask") == 2047);
            Check("skin=15 (last skin) accepted", G<int>(we, "skinIndex") == 15);
            Check("speedwin=2880 accepted", G<int>(we, "speedMin") == 2880);

            // out-of-range sort column: accepted as-is (see KNOWN-BUG below)
            int sc = G<int[]>(we, "sortCol")[0];
            KnownBug("BUG-L1", "sort0=2147483647 (column 1073741823) is ignored/clamped instead of loaded as a bogus sort column", sc < 0 || sc <= 7, "sortCol[0]=" + sc);
        }

        private static bool Try(Action a)
        {
            try
            {
                a();
                return true;
            }
            catch (Exception ex)
            {
                Emit("      (threw " + ex.GetType().Name + ": " + Esc(ex.Message) + ")");
                return false;
            }
        }

        // all the indices / masks the product later uses as array indices or bit positions are in range
        private static bool RangesOk(Widget w)
        {
            int[] sc = G<int[]>(w, "sortCol");
            Skin[] skins = (Skin[])typeof(Widget).GetField("Skins", Stat).GetValue(null);
            int um = G<int>(w, "usageMode"), sk = G<int>(w, "skinIndex"), tm = G<int>(w, "tabMask"), up = G<int>(w, "usagePeriod");
            bool ok = um >= 0 && um < Feat.ViewCount && sk >= 0 && sk < skins.Length && tm > 0 && tm < 2048 && up >= 0 && up < 11;
            ok = ok && ((G<int>(w, "vmask") >> um) & 1) == 1 && (G<int>(w, "vmask") & Feat.AllV) != 0 && (G<int>(w, "smask") & Feat.AllS) != 0;
            ok = ok && G<int>(w, "speedMin") >= 1 && G<int>(w, "speedMin") <= 2880 && G<int>(w, "rowHu") >= 14 && G<int>(w, "rowHu") <= 40;
            ok = ok && G<int>(w, "bgPercent") >= 0 && G<int>(w, "bgPercent") <= 100 && G<int>(w, "fgPercent") >= 20 && G<int>(w, "fgPercent") <= 100;
            return ok;
        }

        // ================================================================== B4 masks after load
        private static void TestMasksOnLoad()
        {
            string home = NewHome("masks");
            Widget w = NewWidget(home, new string[] { "fmask=-1", "vmask=0", "smask=0", "dmask=-1", "view=4" });
            Check("vmask=0 -> all views visible again (never zero views)", (G<int>(w, "vmask") & Feat.AllV) == Feat.AllV, "vmask=" + G<int>(w, "vmask"));
            Check("smask=0 -> all columns visible again (never zero columns)", (G<int>(w, "smask") & Feat.AllS) == Feat.AllS, "smask=" + G<int>(w, "smask"));
            Check("view=4 with all views visible stays 4", G<int>(w, "usageMode") == 4);
            Check("garbage masks give profile Custom (3), not a crash", G<int>(w, "profile") == 3);

            w = NewWidget(home, new string[] { "fmask=255", "vmask=64", "smask=128", "dmask=0", "view=3" });
            Check("vmask with only an unknown high bit (64) -> reset to all views", (G<int>(w, "vmask") & Feat.AllV) == Feat.AllV, "vmask=" + G<int>(w, "vmask"));
            Check("smask with only an unknown high bit (128) -> reset to all columns", (G<int>(w, "smask") & Feat.AllS) == Feat.AllS, "smask=" + G<int>(w, "smask"));

            w = NewWidget(home, new string[] { "fmask=255", "vmask=3", "smask=3", "dmask=0", "view=4" });
            Check("view=4 (API) while only views 0,1 are enabled -> falls back to the first visible view (0)", G<int>(w, "usageMode") == 0, "usageMode=" + G<int>(w, "usageMode"));
            w = NewWidget(home, new string[] { "fmask=255", "vmask=32", "smask=3", "dmask=0", "view=0" });
            Check("only the Trend view enabled and view=0 -> view becomes 5", G<int>(w, "usageMode") == 5, "usageMode=" + G<int>(w, "usageMode"));
            w = NewWidget(home, new string[] { "fmask=255", "vmask=47", "smask=123", "dmask=29", "view=4" });
            Check("Recommended masks written by hand + view=4 (API hidden) -> view 0", G<int>(w, "usageMode") == 0 && G<int>(w, "profile") == 1);

            // fmask present: alert / log keys must not fight with it
            w = NewWidget(home, new string[] { "fmask=" + (255 & ~Feat.Alert), "alert=1", "log=0" });
            Check("fmask without Alert bit wins over alert=1", !G<bool>(w, "alertsOn"));
            Check("fmask with Log bit wins over log=0", ((UsageLogger)Get(w, "logger")).Enabled);
            w = NewWidget(home, new string[] { "alert=0", "log=0" });
            Check("no fmask: legacy alert=0 / log=0 are turned into mask bits", (G<int>(w, "fmask") & (Feat.Alert | Feat.Log)) == 0 && !G<bool>(w, "alertsOn"));

            // old (pre colv) files: sort column and column widths are rotated once, idempotently
            w = NewWidget(home, new string[] { "sort0=" + ((7 + 1) * 2 + 1), "cc0=1000", "cc1=2000", "cc2=3000", "cc3=4000", "cc4=5000", "cc5=6000", "cc6=7000", "cc7=8000" });
            Check("pre-colv file: sort column 7 (cost) moves to 3 (occupancy slot)", G<int[]>(w, "sortCol")[0] == 3, "sortCol[0]=" + G<int[]>(w, "sortCol")[0]);
            double[] cf = G<double[]>(w, "colFr");
            Check("pre-colv file: column widths rotated (c3 <- old c7)", Math.Abs(cf[3] - 8.0) < 1e-9 && Math.Abs(cf[4] - 4.0) < 1e-9 && Math.Abs(cf[7] - 7.0) < 1e-9, string.Join(",", Array.ConvertAll<double, string>(cf, delegate(double d) { return d.ToString("0.#", CultureInfo.InvariantCulture); })));
            Call(w, "SaveSettings");
            Widget w2 = new Widget();
            live.Add(w2);
            Check("... after save (colv=2) a second load does NOT rotate again", Snap(w) == Snap(w2), Snap(w) + "  <>  " + Snap(w2));
            w = NewWidget(home, new string[] { "colv=2", "sort0=" + ((7 + 1) * 2), "cc3=4000" });
            Check("colv=2 file: sort column 7 stays 7, no rotation", G<int[]>(w, "sortCol")[0] == 7 && Math.Abs(G<double[]>(w, "colFr")[3] - 4.0) < 1e-9);
            w = NewWidget(home, new string[] { "sort0=" + ((4 + 1) * 2 + 1) });
            Check("pre-colv file: sort column 4 (output) moves to 5", G<int[]>(w, "sortCol")[0] == 5);
            w = NewWidget(home, new string[] { "sort0=" + ((2 + 1) * 2 + 1) });
            Check("pre-colv file: sort column 2 (speed) is untouched", G<int[]>(w, "sortCol")[0] == 2);
        }

        // ================================================================== B5 window size / position
        private static void TestWindowSanity()
        {
            string home = NewHome("winsize");
            Widget w = NewWidget(home, new string[] { "sv=2", "w=2147483647", "h2=2147483647" });
            int cw = (int)Call(w, "CurW"), ch = (int)Call(w, "CurH");
            KnownBug("BUG-L2", "w=2147483647 / h2=2147483647 in settings.txt gives a sane window size (<= 10000 base units)", cw <= 10000 && ch <= 10000, "CurW=" + cw + " CurH=" + ch);
            w = NewWidget(home, new string[] { "sv=2", "w=1", "h2=1" });
            int cw2 = (int)Call(w, "CurW"), ch2 = (int)Call(w, "CurH");
            Check("tiny saved size (w=1,h2=1) is raised to the minimum size", cw2 >= 240 && ch2 >= 60, "CurW=" + cw2 + " CurH=" + ch2);
            w = NewWidget(home, new string[] { "sv=2", "w=640", "h2=480" });
            Check("sv=2: saved size (640 x 480) is kept", G<int>(w, "winWu") == 640 && G<int>(w, "winHu") == 480);
            w = NewWidget(home, new string[] { "w=640", "h2=480" });
            Check("no sv: saved size is dropped", G<int>(w, "winWu") == 0 && G<int>(w, "winHu") == 0);

            // a window on a monitor to the left of / above the primary one has negative coordinates: they must survive save + load
            // (written with the culture's own minus sign, read back with the same parser; run with -Culture sv-SE for the U+2212 minus)
            w = NewWidget(home, new string[] { "sv=2" });
            w.Left = -1500;
            w.Top = -40;
            Call(w, "SaveSettings");
            Widget wneg = new Widget();
            live.Add(wneg);
            Check("negative window position (-1500, -40) round trips through settings.txt", G<int>(wneg, "savedX") == -1500 && G<int>(wneg, "savedY") == -40, "savedX=" + G<int>(wneg, "savedX") + " savedY=" + G<int>(wneg, "savedY"));
            File.WriteAllLines(Path.Combine(home, "settings.txt"), new string[] { "sv=2", "x=-1500", "y=-40" }, new UTF8Encoding(false));
            Widget wasc = new Widget();
            live.Add(wasc);
            Check("a settings file written with the ASCII minus ('x=-1500') is read correctly in every culture", G<int>(wasc, "savedX") == -1500 && G<int>(wasc, "savedY") == -40, "savedX=" + G<int>(wasc, "savedX"));

            // PlaceInitial: a saved position that is on no screen must fall back to the top-right corner of the primary screen
            w = NewWidget(home, new string[] { "sv=2", "x=90000", "y=90000" });
            try
            {
                Call(w, "PlaceInitial");
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Check("off-screen saved position (90000,90000) falls back to the primary screen", wa.Contains(w.Location), "Location=" + w.Location + " work area=" + wa);
            }
            catch (Exception ex)
            {
                Check("PlaceInitial is callable without a display handle", false, ex.GetType().Name + " " + Esc(ex.Message));
            }
        }

        // ================================================================== C1 Features.cs
        private static void TestFeatures()
        {
            string[] pn = Feat.ProfileNames;
            Check("4 profile names / descriptions", pn.Length == 4 && Feat.ProfileDesc.Length == 4);
            Check("name arrays match their masks (8 features, 6 views, 7 columns, 5 display items)",
                Feat.FNames.Length == 8 && Feat.AllF == 255 && Feat.VNames.Length == Feat.ViewCount && Feat.AllV == (1 << Feat.ViewCount) - 1
                && Feat.SNames.Length == Feat.ColCount && Feat.AllS == (1 << Feat.ColCount) - 1 && Feat.DNames.Length == 5 && Feat.AllD == 31);

            for (int p = 0; p < 3; p++)
            {
                int[] a = Feat.Preset(p);
                Check("Preset(" + p + ") has 4 masks, Identify round trip gives " + p, a.Length == 4 && Feat.Identify(a[0], a[1], a[2], a[3]) == p);
                Check("Preset(" + p + ") masks within their legal range", (a[0] & ~Feat.AllF) == 0 && (a[1] & ~Feat.AllV) == 0 && (a[2] & ~Feat.AllS) == 0 && (a[3] & ~Feat.AllD) == 0 && a[1] != 0 && a[2] != 0);
            }
            Check("Preset(3) / Preset(-1) / Preset(99) fall back to Recommended", Same(Feat.Preset(3), Feat.Preset(1)) && Same(Feat.Preset(-1), Feat.Preset(1)) && Same(Feat.Preset(99), Feat.Preset(1)));
            Check("Everything = all bits", Same(Feat.Preset(0), new int[] { Feat.AllF, Feat.AllV, Feat.AllS, Feat.AllD }));
            int[] rec = Feat.Preset(1), min = Feat.Preset(2);
            Check("Recommended hides API view, Input column, DeepSeek row; keeps Trend, Forecast, Chip, Jump, HealthBadge",
                (rec[1] & (1 << 4)) == 0 && (rec[1] & (1 << 5)) != 0 && (rec[2] & (1 << 2)) == 0 && (rec[3] & Feat.DsLine) == 0
                && (rec[3] & (Feat.Forecast | Feat.Chip | Feat.Jump | Feat.HealthBadge)) == (Feat.Forecast | Feat.Chip | Feat.Jump | Feat.HealthBadge));
            Check("Minimal: only Alert + Log + Trim; only Session + Model views",
                min[0] == (Feat.Alert | Feat.Log | Feat.Trim) && min[1] == 3 && (min[2] & (1 << 1)) != 0);
            Check("Minimal does not read history / run python / self-check / weekly report",
                (min[0] & (Feat.History | Feat.Fit | Feat.VSync | Feat.Health | Feat.Weekly)) == 0);
            Check("Recommended / Everything keep every background feature", rec[0] == Feat.AllF && Feat.Preset(0)[0] == Feat.AllF);

            // flipping any single bit leaves the preset -> Custom (3); bits above the legal range too
            int single = 0, singleBad = 0;
            for (int p = 0; p < 3; p++)
            {
                for (int which = 0; which < 4; which++)
                {
                    for (int bit = 0; bit < 12; bit++)
                    {
                        int[] a = (int[])Feat.Preset(p).Clone();
                        a[which] ^= 1 << bit;
                        int id = Feat.Identify(a[0], a[1], a[2], a[3]);
                        single++;
                        if (id != 3)
                        {
                            singleBad++;
                            Emit("      flipping bit " + bit + " of mask " + which + " in preset " + p + " gives profile " + id);
                        }
                    }
                }
            }
            Check("every single-bit change of a preset (" + single + " cases) is 'Custom'", singleBad == 0, singleBad + " cases did not give 3");
            Check("Identify of zero / negative masks is Custom", Feat.Identify(0, 0, 0, 0) == 3 && Feat.Identify(-1, -1, -1, -1) == 3);

            // cadence
            Feat.Cadence c0 = Feat.CadenceOf(0), c1 = Feat.CadenceOf(1), c2 = Feat.CadenceOf(2), c3 = Feat.CadenceOf(3);
            Check("Cadence Everything = 15 s / 3 min / 30 min / 5 min", c0.BgPollSec == 15 && c0.HistMin == 3 && c0.FitMin == 30 && c0.FitDerivedMin == 5);
            Check("Cadence Recommended = 15 s / 5 min / 60 min / 10 min", c1.BgPollSec == 15 && c1.HistMin == 5 && c1.FitMin == 60 && c1.FitDerivedMin == 10);
            Check("Cadence Minimal = 30 s / 15 min / 180 min / 30 min", c2.BgPollSec == 30 && c2.HistMin == 15 && c2.FitMin == 180 && c2.FitDerivedMin == 30);
            Check("Cadence Custom (3) uses the Recommended pace", SameCad(c3, c1));
            Check("Cadence for invalid profile numbers (-1, 7, int.MaxValue) = Recommended pace", SameCad(Feat.CadenceOf(-1), c1) && SameCad(Feat.CadenceOf(7), c1) && SameCad(Feat.CadenceOf(int.MaxValue), c1));
            Check("Cadence: Minimal is never faster than Recommended, Recommended never faster than Everything",
                c2.BgPollSec >= c1.BgPollSec && c1.BgPollSec >= c0.BgPollSec && c2.HistMin >= c1.HistMin && c1.HistMin >= c0.HistMin && c2.FitMin >= c1.FitMin && c1.FitMin >= c0.FitMin
                && c2.FitDerivedMin >= c1.FitDerivedMin && c1.FitDerivedMin >= c0.FitDerivedMin);
            Check("Cadence: all intervals positive; 'derived' refit is never slower than the normal refit",
                c0.BgPollSec > 0 && c2.HistMin > 0 && c0.FitDerivedMin > 0 && c0.FitDerivedMin <= c0.FitMin && c1.FitDerivedMin <= c1.FitMin && c2.FitDerivedMin <= c2.FitMin);
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameCad(Feat.Cadence a, Feat.Cadence b)
        {
            return a.BgPollSec == b.BgPollSec && a.HistMin == b.HistMin && a.FitMin == b.FitMin && a.FitDerivedMin == b.FitDerivedMin;
        }

        // ================================================================== C2 SetMasks / ApplyPreset
        private static void TestApplyPreset()
        {
            string home = NewHome("preset");
            Widget w = NewWidget(home, new string[] { "view=3" });
            Check("start: Recommended, view 3 (Plan)", G<int>(w, "profile") == 1 && G<int>(w, "usageMode") == 3);
            Call(w, "ApplyPreset", 2);
            Check("ApplyPreset(Minimal): profile 2", G<int>(w, "profile") == 2);
            Check("ApplyPreset(Minimal): the Plan view (3) is not in the Minimal view set -> falls back to Session (0)", G<int>(w, "usageMode") == 0, "usageMode=" + G<int>(w, "usageMode"));
            Check("ApplyPreset(Minimal): alerts stay on, logger stays on", G<bool>(w, "alertsOn") && ((UsageLogger)Get(w, "logger")).Enabled);
            Dictionary<string, string> kv = ReadKv(Path.Combine(home, "settings.txt"));
            Check("ApplyPreset saves immediately (profile=2, vmask=3, fmask=193)", KvGet(kv, "profile") == "2" && KvGet(kv, "vmask") == "3" && KvGet(kv, "fmask") == "193",
                KvGet(kv, "profile") + "/" + KvGet(kv, "vmask") + "/" + KvGet(kv, "fmask"));
            Widget w2 = new Widget();
            live.Add(w2);
            Check("Minimal survives a restart (profile 2, same masks)", G<int>(w2, "profile") == 2 && Snap(w) == Snap(w2), Snap(w) + "  <>  " + Snap(w2));
            for (int p = 0; p <= 2; p++)
            {
                Call(w, "ApplyPreset", p);
                Widget wr = new Widget();
                live.Add(wr);
                Check("ApplyPreset(" + p + ") -> restart -> profile " + p, G<int>(wr, "profile") == p && G<int>(wr, "fmask") == Feat.Preset(p)[0] && G<int>(wr, "vmask") == Feat.Preset(p)[1]
                    && G<int>(wr, "smask") == Feat.Preset(p)[2] && G<int>(wr, "dmask") == Feat.Preset(p)[3]);
            }
            // one box changed -> Custom, survives restart
            Call(w, "ApplyPreset", 1);
            Call(w, "SetMasks", G<int>(w, "fmask"), G<int>(w, "vmask"), G<int>(w, "smask") ^ 1, G<int>(w, "dmask"));
            Check("SetMasks with one column bit flipped -> Custom (3)", G<int>(w, "profile") == 3);
            Widget wc = new Widget();
            live.Add(wc);
            Check("Custom survives a restart", G<int>(wc, "profile") == 3 && Snap(w) == Snap(wc));
            // turning the alert feature off through the mask also clears alertsOn; the legacy flag and the mask never disagree after SaveSettings
            Call(w, "SetMasks", G<int>(w, "fmask") & ~Feat.Alert, G<int>(w, "vmask"), G<int>(w, "smask"), G<int>(w, "dmask"));
            Check("SetMasks without the Alert bit turns alertsOn off", !G<bool>(w, "alertsOn"));
            Set(w, "alertsOn", true);
            Call(w, "SyncMasksFromFields");
            Check("SyncMasksFromFields: alertsOn=true puts the Alert bit back", (G<int>(w, "fmask") & Feat.Alert) != 0);
            Check("SetMasks masks off bits above the legal range", Try(delegate { Call(w, "SetMasks", 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF); }) && G<int>(w, "vmask") == Feat.AllV && G<int>(w, "smask") == Feat.AllS && G<int>(w, "dmask") == Feat.AllD);
        }

        // ================================================================== D Mail.cs
        private static string MailFile(string home)
        {
            return Path.Combine(home, "mail.txt");
        }

        // the message building of MailSender.SendAsync (Mail.cs), statement for statement, WITHOUT the smtp.Send(): the product method cannot
        // be called without opening a socket. Throws what the product would throw.
        private static System.Net.Mail.MailMessage BuildMessage(MailConfig c, string subject, string body)
        {
            System.Net.Mail.MailMessage m = new System.Net.Mail.MailMessage();
            try
            {
                m.From = new System.Net.Mail.MailAddress(c.From.Length > 0 ? c.From : c.User);
                foreach (string to in c.To.Split(new char[] { ',', ';', '，', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    m.To.Add(to.Trim());
                }
                m.Subject = subject;
                m.SubjectEncoding = Encoding.UTF8;
                m.Body = body;
                m.BodyEncoding = Encoding.UTF8;
                return m;
            }
            catch (Exception)
            {
                m.Dispose();
                throw;
            }
        }

        private static void TestMailStore()
        {
            string home = NewHome("mail");
            UseHome(home);
            ResetStatics();
            string path = MailFile(home);

            // ---- round trip
            string pw = "S3cret,=|\"pass wörd 密码 😀";
            MailStore.Enabled = true;
            MailStore.Host = "smtp.example.test";
            MailStore.Port = 2525;
            MailStore.Ssl = false;
            MailStore.User = "user@example.test";
            MailStore.From = "from@example.test";
            MailStore.To = "me@example.test, you@example.test";
            MailStore.Password = pw;
            MailWatch w1 = new MailWatch();
            w1.Session = "aaaaaaaa-0000-4000-8000-000000000001";
            w1.Title = "Title | with pipe\r\nand newline 中文标题";
            w1.Mins = MailStore.ParseMins("30, 10");
            MailWatch w2 = new MailWatch();
            w2.Session = "bbbbbbbb-0000-4000-8000-000000000002";
            w2.Title = "";
            w2.Mins = MailStore.ParseMins("5");
            lock (MailStore.Watches)
            {
                MailStore.Watches.Add(w1);
                MailStore.Watches.Add(w2);
            }
            MailStore.Save();
            Check("mail.txt written under AppPaths.Home", File.Exists(path) && Path.GetDirectoryName(path) == AppPaths.Home);
            string text = File.ReadAllText(path, Encoding.UTF8);
            Check("password is NOT in the file in plaintext (whole, parts, CJK, emoji)", !Has(text, pw) && !Has(text, "S3cret") && !Has(text, "wörd") && !Has(text, "密码") && !Has(text, "pass w"));
            string passLine = null;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.StartsWith("pass=", StringComparison.Ordinal)) passLine = line.Substring(5);
            }
            Check("pass= line exists and is base64", passLine != null && passLine.Length >= 60 && Regex.IsMatch(passLine, "^[A-Za-z0-9+/]+={0,2}$"));
            byte[] blob = null;
            try
            {
                blob = Convert.FromBase64String(passLine);
            }
            catch (Exception)
            {
            }
            byte[] dpapi = { 0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11, 0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB };
            bool isDpapi = blob != null && blob.Length > dpapi.Length;
            for (int i = 0; isDpapi && i < dpapi.Length; i++)
            {
                isDpapi = blob[i] == dpapi[i];
            }
            Check("pass= is a real DPAPI blob (starts with the DPAPI provider GUID), not just base64 of the text", isDpapi);
            Check("file has no password hint (no 'password=' key) and is UTF-8 without BOM", !Has(text, "password") && File.ReadAllBytes(path)[0] != 0xEF);

            MailReset();
            Check("(state cleared) not Configured, no password", !MailStore.Configured && !MailStore.HasPassword);
            MailStore.Load();
            Check("reload: enabled / host / port / ssl / user / from / to round trip",
                MailStore.Enabled && MailStore.Host == "smtp.example.test" && MailStore.Port == 2525 && !MailStore.Ssl && MailStore.User == "user@example.test"
                && MailStore.From == "from@example.test" && MailStore.To == "me@example.test, you@example.test");
            Check("reload: password decrypts to the original text", MailStore.Password == pw, Esc(MailStore.Password));
            Check("reload: Configured is true", MailStore.Configured);
            Check("reload: two watches, minutes sorted high -> low", MailStore.WatchCount == 2 && MailStore.Find(w1.Session) != null && MailStore.MinsText(MailStore.Find(w1.Session).Mins) == "30,10"
                && MailStore.MinsText(MailStore.Find(w2.Session).Mins) == "5");
            Check("reload: '|' and line breaks in the title are replaced, CJK kept", MailStore.Find(w1.Session).Title == "Title / with pipe  and newline 中文标题", Esc(MailStore.Find(w1.Session).Title));
            MailConfig snap = MailStore.Snapshot();
            Check("Snapshot: copies the settings and hands out the decrypted password", snap.Host == "smtp.example.test" && snap.Port == 2525 && !snap.Ssl && snap.Pass == pw && snap.From == "from@example.test");
            MailStore.From = "";
            Check("Snapshot: empty From falls back to User", MailStore.Snapshot().From == "user@example.test");

            // ---- a second Save with an empty password field must not wipe the stored one (the dialog only sets Password when the box is non-empty)
            string before = passLine;
            MailStore.Save();
            string again = null;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.StartsWith("pass=", StringComparison.Ordinal)) again = line.Substring(5);
            }
            Check("saving again keeps the same encrypted blob (no re-encrypt churn, no wipe)", again == before);
            MailStore.Password = "";
            Check("Password = \"\" clears it (HasPassword false)", !MailStore.HasPassword && !MailStore.Configured);
            MailStore.Save();
            Check("... and the file then has an empty pass=", Regex.IsMatch(File.ReadAllText(path, Encoding.UTF8), "(^|\n)pass=\r?(\n|$)"));

            // ---- wrong / corrupted encrypted blobs
            string realBlob = before;
            byte[] flipped = (byte[])blob.Clone();
            flipped[flipped.Length - 3] ^= 0x55;
            byte[] truncated = new byte[blob.Length / 2];
            Array.Copy(blob, truncated, truncated.Length);
            string[] blobs =
            {
                "AAAA", "!!!not base64!!!", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")), Convert.ToBase64String(flipped), Convert.ToBase64String(truncated),
                new string('A', 5000), "=", "密码", realBlob + realBlob
            };
            int ok = 0, throwsCount = 0;
            foreach (string b in blobs)
            {
                MailReset();
                File.WriteAllLines(path, new string[] { "enabled=1", "host=h.example.test", "user=u@example.test", "to=t@example.test", "pass=" + b }, new UTF8Encoding(false));
                try
                {
                    MailStore.Load();
                    string p = MailStore.Password;
                    MailConfig s = MailStore.Snapshot();
                    if (p == "" && s.Pass == "") ok++;
                }
                catch (Exception)
                {
                    throwsCount++;
                }
            }
            Check("undecryptable pass= blobs (" + blobs.Length + " variants): no exception, Password = \"\"", throwsCount == 0 && ok == blobs.Length, "threw " + throwsCount + ", ok " + ok);
            MailReset();
            File.WriteAllLines(path, new string[] { "enabled=1", "host=h.example.test", "user=u@example.test", "to=t@example.test", "pass=AAAA" }, new UTF8Encoding(false));
            MailStore.Load();
            KnownBug("BUG-L3", "'Configured' is false when the saved password cannot be decrypted (other Windows account / file copied)", !MailStore.Configured,
                "Configured=" + MailStore.Configured + " HasPassword=" + MailStore.HasPassword + " Password.Length=" + MailStore.Password.Length);

            // ---- garbage in the file
            MailReset();
            File.WriteAllLines(path, new string[]
            {
                "garbage", "=", "port=0", "port=99999", "port=abc", "port=-5", "ssl=", "enabled=true", "w=", "w=|10|x", "w=abc", "w=abc|", "w=abc|0|x", "w=abc|601|x",
                "w=ok1|601,5,5,abc|T", "w=ok2|30,10|A|extra|more", "host=", "user=  spaced  "
            }, new UTF8Encoding(false));
            Check("garbage mail.txt loads without throwing", Try(delegate { MailStore.Load(); }));
            Check("invalid ports (0, 99999, abc, -5) leave the default 587", MailStore.Port == 587, "Port=" + MailStore.Port);
            Check("'ssl=' (empty) = on, 'enabled=true' (not '1') = off", MailStore.Ssl && !MailStore.Enabled);
            Check("only watches with a session id and at least one valid minute survive (ok1 -> [5], ok2 -> [30,10])",
                MailStore.WatchCount == 2 && MailStore.MinsText(MailStore.Find("ok1").Mins) == "5" && MailStore.MinsText(MailStore.Find("ok2").Mins) == "30,10" && MailStore.Find("ok2").Title == "A");
            Check("values are trimmed", MailStore.User == "spaced");
            Check("missing file / empty file: Load leaves the current values and does not throw", Try(delegate
            {
                File.Delete(path);
                MailStore.Load();
                File.WriteAllBytes(path, new byte[0]);
                MailStore.Load();
            }));

            // ---- ParseMins / MinsText
            Check("ParseMins('30, 10；5') = [30,10,5]", MailStore.MinsText(MailStore.ParseMins("30, 10；5")) == "30,10,5");
            Check("ParseMins sorts high -> low and removes duplicates", MailStore.MinsText(MailStore.ParseMins("5,10,5,30,10")) == "30,10,5");
            Check("ParseMins separators: , ; space 、 / full-width", MailStore.MinsText(MailStore.ParseMins("1,2;3 4、5/6，7；8")) == "8,7,6,5,4,3,2,1");
            Check("ParseMins range is 1..600", MailStore.MinsText(MailStore.ParseMins("0,1,600,601,-5")) == "600,1");
            Check("ParseMins rejects 5.5, 1e3, abc, empty, overflow, full-width digits", MailStore.ParseMins("5.5,1e3,abc,,99999999999999999999,１０").Count == 0);
            Check("ParseMins(null) / ('') / ('   ') = empty list", MailStore.ParseMins(null).Count == 0 && MailStore.ParseMins("").Count == 0 && MailStore.ParseMins("   ").Count == 0);
            Check("MinsText(empty) = ''", MailStore.MinsText(new List<int>()) == "");

            // ---- no mail.txt anywhere else, no accidental writes outside the temp home
            Check("only mail.txt (and its folder) was written by the mail store", !File.Exists(Path.Combine(Path.GetDirectoryName(home), "mail.txt")));
        }

        private static void TestMailInjection()
        {
            string home = NewHome("mailinj");
            UseHome(home);
            ResetStatics();
            string path = MailFile(home);
            // Which settings are free text? Host, User, From, To (all four come from single-line text boxes) and the watch Session id;
            // the watch Title is sanitised by Save(). A CR/LF in a free-text field would start a new line in mail.txt.
            string[] fields = { "Host", "User", "From", "To", "Session" };
            foreach (string f in fields)
            {
                MailReset();
                if (File.Exists(path)) File.Delete(path);
                string evil = "x.example.test\r\nenabled=1\r\nw=evil-session|5|pwned\r\nuser=attacker@example.test";
                if (f == "Host") MailStore.Host = evil;
                else if (f == "User") MailStore.User = evil;
                else if (f == "From") MailStore.From = evil;
                else if (f == "To") MailStore.To = evil;
                MailWatch w = new MailWatch();
                w.Session = f == "Session" ? evil : "legit-session";
                w.Title = "t";
                w.Mins = MailStore.ParseMins("10");
                lock (MailStore.Watches)
                {
                    MailStore.Watches.Add(w);
                }
                MailStore.Save();
                string raw = File.ReadAllText(path, Encoding.UTF8);
                MailReset();
                MailStore.Load();
                bool enabledInjected = MailStore.Enabled;
                bool watchInjected = MailStore.Find("evil-session") != null;
                bool userInjected = MailStore.User == "attacker@example.test";
                int lines = File.ReadAllLines(path, Encoding.UTF8).Length;
                KnownBug("BUG-L5", "CR/LF in " + f + " cannot add settings lines to mail.txt (no injected enabled= / w= / user=)", !enabledInjected && !watchInjected && !userInjected,
                    "lines in file=" + lines + " enabledInjected=" + enabledInjected + " watchInjected=" + watchInjected + " userInjected=" + userInjected);
            }
            MailReset();

            // ---- message building: no header injection through From / To / Subject (replica of the product statements, see BuildMessage)
            MailConfig c = new MailConfig();
            c.Host = "127.0.0.1";
            c.Port = 1;
            c.User = "user@example.test";
            c.From = "";
            c.To = "a@example.test, b@example.test；c@example.test d@example.test";
            string body = "line 1\r\n.\r\nBcc: not-a-header@example.test\r\n";
            using (System.Net.Mail.MailMessage m = BuildMessage(c, "Subject 中文 — ok", body))
            {
                Check("[replica] normal message: 4 recipients from ', ; space and full-width ;' separators, From falls back to User", m.To.Count == 4 && m.From.Address == "user@example.test");
                Check("[replica] normal message: UTF-8 subject and body, no extra headers (Bcc/Cc empty)", m.SubjectEncoding == Encoding.UTF8 && m.BodyEncoding == Encoding.UTF8 && m.Bcc.Count == 0 && m.CC.Count == 0 && m.Headers.Count == 0);
            }
            string[] hostile = { "S\r\nBcc: victim@example.test", "S\nBcc: victim@example.test", "S\rBcc: victim@example.test" };
            int safeSubjects = 0;
            foreach (string subj in hostile)
            {
                try
                {
                    using (BuildMessage(c, subj, "b"))
                    {
                    }
                }
                catch (ArgumentException)
                {
                    safeSubjects++;
                }
                catch (FormatException)
                {
                    safeSubjects++;
                }
            }
            Check("[replica] a Subject containing CR/LF is rejected by System.Net.Mail (no header injection)", safeSubjects == hostile.Length, safeSubjects + " of " + hostile.Length + " rejected");
            int safeAddr = 0;
            string[] badAddr = { "a@example.test\r\nBcc: victim@example.test", "a@example.test\nBcc: v@example.test", "\"x\r\nBcc: v@example.test\" <a@example.test>" };
            foreach (string a in badAddr)
            {
                MailConfig c2 = new MailConfig();
                c2.User = "user@example.test";
                c2.From = "";
                c2.To = a;
                try
                {
                    using (BuildMessage(c2, "s", "b"))
                    {
                    }
                    // accepted: check that nothing leaked into Bcc
                }
                catch (FormatException)
                {
                    safeAddr++;
                }
                catch (ArgumentException)
                {
                    safeAddr++;
                }
            }
            Check("[replica] a To address containing CR/LF is rejected (FormatException)", safeAddr == badAddr.Length, safeAddr + " of " + badAddr.Length + " rejected");
            int safeFrom = 0;
            foreach (string a in badAddr)
            {
                MailConfig c3 = new MailConfig();
                c3.User = "user@example.test";
                c3.From = a;
                c3.To = "t@example.test";
                try
                {
                    using (BuildMessage(c3, "s", "b"))
                    {
                    }
                }
                catch (FormatException)
                {
                    safeFrom++;
                }
                catch (ArgumentException)
                {
                    safeFrom++;
                }
            }
            Check("[replica] a From address containing CR/LF is rejected", safeFrom == badAddr.Length, safeFrom + " of " + badAddr.Length + " rejected");

            // the product's own SendAsync, with inputs that throw BEFORE smtp.Send() (so no socket is opened). Only run when the replica proved
            // that the framework throws for them; the product must report the error through the callback instead of crashing the thread pool.
            MailConfig c4 = new MailConfig();
            c4.Host = "127.0.0.1";
            c4.Port = 1;
            c4.User = "user@example.test";
            c4.Pass = "x";
            c4.From = "";
            c4.To = "t@example.test";
            string subjBad = "S\r\nBcc: victim@example.test";
            bool replicaThrows = false;
            try
            {
                using (BuildMessage(c4, subjBad, "b"))
                {
                }
            }
            catch (Exception)
            {
                replicaThrows = true;
            }
            if (replicaThrows)
            {
                string err = "<no callback>";
                ManualResetEvent done = new ManualResetEvent(false);
                MailSender.SendAsync(c4, subjBad, "b", delegate(string e)
                {
                    err = e;
                    done.Set();
                });
                bool got = done.WaitOne(8000);
                Check("MailSender.SendAsync with a CR/LF subject: callback gets an error text, nothing is sent", got && err != null && err.Length > 0, "callback=" + got + " err=" + Esc(err));
                Note("BUG-L6 (reasoned, not reproducible without a socket): Extras.cs SendCacheMail puts the session title into the Subject unsanitised; a title with a line break makes System.Net.Mail throw, so that reminder mail is never sent (reported through the failure balloon)");
            }
            else
            {
                Check("a CR/LF subject was accepted by System.Net.Mail on this runtime: SendAsync not exercised (it would open a socket)", false, "header injection possible?");
            }
            string src = ReadSource("Mail.cs");
            Check("[source check] Mail.cs never builds headers by hand (no Headers.Add / Bcc / CC)", src != null && !Has(src, "Headers.Add") && !Has(src, ".Bcc") && !Has(src, ".CC."), src == null ? "Mail.cs not found" : "");
            Note("MailSender.SendAsync cannot be called with a valid message without opening a socket: delivery, STARTTLS and authentication are NOT tested");
        }

        private static string ReadSource(string name)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            // the exe lives in <work>; the sources are found through the work folder's marker (written by run-logic.ps1) -> repo root recorded in sources.txt
            string rec = Path.Combine(work, "sources.txt");
            if (File.Exists(rec))
            {
                string p = Path.Combine(File.ReadAllText(rec).Trim(), name);
                if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
            }
            return null;
        }

        private static void TestMailForm()
        {
            string home = NewHome("mailform");
            UseHome(home);
            ResetStatics();
            using (MailForm f = new MailForm(new List<MailSessionRow>()))
            {
                Type t = typeof(MailForm);
                Action<string, string> setText = delegate(string field, string val)
                {
                    ((TextBox)t.GetField(field, Inst).GetValue(f)).Text = val;
                };
                Func<string> err = delegate
                {
                    object[] a = { null };
                    t.GetMethod("CurrentConfig", Inst).Invoke(f, a);
                    return (string)a[0];
                };
                setText("host", "smtp.example.test");
                setText("port", "587");
                setText("user", "u@example.test");
                setText("pass", "pw");
                setText("to", "t@example.test");
                Check("MailForm validation: complete input -> no error", err() == null, err());
                setText("port", "abc");
                Check("MailForm validation: port 'abc' -> error", err() != null);
                setText("port", "0");
                Check("MailForm validation: port 0 -> error", err() != null);
                setText("port", "65536");
                Check("MailForm validation: port 65536 -> error", err() != null);
                setText("port", "-1");
                Check("MailForm validation: port -1 -> error", err() != null);
                setText("port", " 25 ");
                Check("MailForm validation: ' 25 ' (spaces) is accepted", err() == null);
                setText("port", "587");
                setText("host", "");
                Check("MailForm validation: empty host -> error", err() != null);
                setText("host", "smtp.example.test");
                setText("to", "   ");
                Check("MailForm validation: blank recipient -> error", err() != null);
                setText("to", "t@example.test");
                setText("pass", "");
                Check("MailForm validation: no typed password and no stored one -> error", err() != null);
                MailStore.Host = "smtp.example.test";
                MailStore.User = "u@example.test";
                MailStore.Password = "stored";
                Check("MailForm validation: empty password box + stored password (same server and user) -> accepted, the stored one is used", err() == null, "err=" + Esc(err()) + " HasPassword=" + MailStore.HasPassword);
                setText("host", "other.example.test");
                Check("MailForm validation: server changed + empty password box -> error (the stored password is never sent to a new server)", err() != null);
                setText("pass", "typed-again");
                Check("MailForm validation: server changed + a newly typed password -> accepted", err() == null, Esc(err()));
                setText("pass", "");
                setText("host", "SMTP.EXAMPLE.TEST");
                Check("MailForm validation: server name differing only by case counts as unchanged", err() == null, Esc(err()));
                setText("user", "someone-else@example.test");
                Check("MailForm validation: user changed + empty password box -> error", err() != null);
                setText("user", "u@example.test");
                setText("to", "not-an-address");
                if (err() == null) Note("MailForm.CurrentConfig does not validate address syntax: 'not-an-address' passes and only fails at send time (low severity, UX)");
                else Check("MailForm validation: a syntactically bad recipient is rejected by the dialog", true);
            }
            MailReset();
        }

        // ================================================================== E Report.cs
        private const string EvilModel = "evil\"><svg onload=alert(1)>";
        private const string XssTitle1 = "<script>alert(1)</script>";
        private const string XssTitle2 = "\"><img src=x onerror=alert(1)>";

        private sealed class Fx
        {
            public string Home, Logs;
            public DateTime Start, End;
            public int MsgsWeek, MsgsPrev, Sessions, UnknownCoef;
            public double UsdWeek, UsdPrev, P5Week, PwWeek;
        }

        private static string CsvQuote(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static string DetailRow(DateTime t, string session, string project, string model, long inp, long outp, long cw5, long cw1, long cr, bool sub, string msgId)
        {
            return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "," + session + "," + CsvQuote(project) + "," + CsvQuote(model) + "," + inp + "," + outp + "," + cw5 + "," + cw1 + "," + cr
                + "," + (sub ? 1 : 0) + "," + msgId + ",prompt1,0,";
        }

        private const string DetailHeader = "时间,会话ID,项目,模型,输入,输出,缓存写入" + "5分钟,缓存写入" + "1小时,缓存读取,子任务,消息ID,任务ID,工具次数,工具名";

        // one row of the quota table (额度换算.csv, 23 columns; see scripts\quota_fit.py)
        private static string QuotaModelRow(string name, double p0, double p1, double p2, double p3, double p4, double k5, double kw, string state5, string key)
        {
            string[] f = new string[23];
            for (int i = 0; i < f.Length; i++) f[i] = "";
            f[0] = "模型";
            f[1] = name;
            double[] p = { p0, p1, p2, p3, p4 };
            for (int i = 0; i < 5; i++) f[2 + i] = p[i].ToString("0.####", CultureInfo.InvariantCulture);
            f[7] = k5.ToString("0.####", CultureInfo.InvariantCulture);
            f[8] = "0.1";
            f[9] = kw.ToString("0.####", CultureInfo.InvariantCulture);
            f[10] = "0.1";
            f[11] = state5;
            f[12] = "实测";
            f[13] = "2026-11-01 00:00:00";
            f[14] = "100000";
            f[15] = "1";
            f[16] = "1";
            f[22] = key;
            return string.Join(",", f);
        }

        private static string QuotaGlobalRow(bool derived, string key)
        {
            string[] f = new string[23];
            for (int i = 0; i < f.Length; i++) f[i] = "";
            f[0] = "全局";
            f[11] = derived ? "按套餐倍率推算自 A:max5（×0.5）" : "实测";
            f[12] = "实测";
            f[13] = "2026-11-01 00:00:00";
            f[16] = "1";
            f[17] = "0.12";
            f[18] = "5";
            f[22] = key;
            return string.Join(",", f);
        }

        private static string QuotaTierRow(string tier, double d5, double dw, string key)
        {
            string[] f = new string[23];
            for (int i = 0; i < f.Length; i++) f[i] = "";
            f[0] = "档位";
            f[1] = tier;
            f[7] = d5.ToString("0.####", CultureInfo.InvariantCulture);
            f[9] = dw.ToString("0.####", CultureInfo.InvariantCulture);
            f[22] = key;
            return string.Join(",", f);
        }

        private const string QuotaHeaderLine = "类型,名称,输入价,输出价,写" + "5m价,写" + "1h价,读价,5h每美元额度%,5h误差,周每美元额度%,周误差,5h状态,周状态,更新时间,长上下文阈值,长上下文倍率,缓存读取缩放,近期误差,误差块数,早期误差,近期实际/预测,误差口径,账号键";

        private static void WriteQuotaCsv(string logs, string key, bool derived)
        {
            List<string> q = new List<string>();
            q.Add(QuotaHeaderLine);
            q.Add(QuotaGlobalRow(derived, key));
            q.Add(QuotaTierRow("high", 1.5, 1.5, key));
            q.Add(QuotaModelRow("claude-sonnet-5-5", 3, 15, 3.75, 6, 0.3, 2.0, 0.5, derived ? "按套餐倍率推算" : "实测", key));
            q.Add(QuotaModelRow("claude-opus-5-5", 15, 75, 18.75, 30, 1.5, 3.0, 0.8, derived ? "按套餐倍率推算" : "实测", key));
            File.WriteAllLines(Path.Combine(logs, "额度换算.csv"), q.ToArray(), new UTF8Encoding(true));
        }

        private static double UsdOfRow(string model, long inp, long outp, long cw5, long cw1, long cr)
        {
            double[] p;
            if (model == "claude-sonnet-5-5") p = new double[] { 3, 15, 3.75, 6, 0.3 };
            else if (model == "claude-opus-5-5") p = new double[] { 15, 75, 18.75, 30, 1.5 };
            else return -1;
            return (inp * p[0] + outp * p[1] + cw5 * p[2] + cw1 * p[3] + cr * p[4]) / 1000000.0;
        }

        private static string UsdFmt(double v)
        {
            return "$" + (v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture));
        }

        // a week of fake log data (2 hostile titles, 1 hostile model name, boundary rows, garbage lines, a previous week for the comparison)
        private static Fx MakeFixture(string home, DateTime monday, bool withTags, bool addGarbage)
        {
            Fx fx = new Fx();
            fx.Home = home;
            fx.Logs = Path.Combine(home, "logs");
            Directory.CreateDirectory(fx.Logs);
            fx.Start = monday;
            fx.End = monday.AddDays(7);
            List<string> rows = new List<string>();
            List<string> tags = new List<string>();
            tags.Add("消息ID,档位,Ultra,速度,思考token,入口");
            Random rnd = new Random(20261102);
            HashSet<string> sessions = new HashSet<string>();
            int idn = 0;
            Action<DateTime, string, string, long, long, long, long, long> add = delegate(DateTime t, string session, string model, long inp, long outp, long cw5, long cw1, long cr)
            {
                idn++;
                string id = "msg_" + idn;
                bool high = withTags && idn % 3 == 0;
                if (high) tags.Add(id + ",high,0,,100,cli");
                rows.Add(DetailRow(t, session, "proj", model, inp, outp, cw5, cw1, cr, idn % 7 == 0, id));
                double usd = UsdOfRow(model, inp, outp, cw5, cw1, cr);
                bool inWeek = t >= fx.Start && t < fx.End;
                bool inPrev = t >= fx.Start.AddDays(-7) && t < fx.Start;
                if (inPrev)
                {
                    fx.MsgsPrev++;
                    fx.UsdPrev += Math.Max(0, usd);
                }
                if (inWeek)
                {
                    fx.MsgsWeek++;
                    sessions.Add(session);
                    if (usd >= 0)
                    {
                        fx.UsdWeek += usd;
                        double k5 = model == "claude-sonnet-5-5" ? 2.0 : 3.0, kw = model == "claude-sonnet-5-5" ? 0.5 : 0.8;
                        fx.P5Week += usd * k5 * (high ? 1.5 : 1.0);
                        fx.PwWeek += usd * kw * (high ? 1.5 : 1.0);
                    }
                    else
                    {
                        fx.UnknownCoef++;
                    }
                }
            };
            for (int i = 0; i < 60; i++)
            {
                DateTime t = monday.AddDays(rnd.Next(7)).AddHours(rnd.Next(24)).AddMinutes(rnd.Next(60)).AddSeconds(rnd.Next(60));
                string model = i % 4 == 0 ? "claude-opus-5-5" : "claude-sonnet-5-5";
                add(t, "sess-" + (i % 9), model, rnd.Next(1, 500), rnd.Next(100, 20000), rnd.Next(0, 50000), rnd.Next(0, 20000), rnd.Next(0, 150000));
            }
            for (int i = 0; i < 14; i++)       // previous week
            {
                add(monday.AddDays(-7 + rnd.Next(7)).AddHours(rnd.Next(24)), "sess-prev" + (i % 3), "claude-sonnet-5-5", 100, rnd.Next(1000, 90000), 2000, 0, rnd.Next(0, 90000));
            }
            // the two hostile-title sessions are the biggest consumers so they appear in the top-10 table
            for (int i = 0; i < 4; i++)
            {
                add(monday.AddDays(1 + i).AddHours(10), "sess-xss1", "claude-opus-5-5", 100, 400000, 10000, 0, 50000);
                add(monday.AddDays(1 + i).AddHours(11), "sess-xss2", "claude-opus-5-5", 100, 300000, 10000, 0, 50000);
            }
            for (int i = 0; i < 3; i++)
            {
                add(monday.AddDays(2).AddHours(3 + i), "sess-evilmodel", EvilModel, 10, 20, 30, 0, 40);
            }
            // week boundaries
            add(monday, "sess-edge", "claude-sonnet-5-5", 0, 1000000, 0, 0, 0);                              // Monday 00:00:00 -> in
            add(monday.AddDays(7).AddSeconds(-1), "sess-edge", "claude-sonnet-5-5", 0, 1000000, 0, 0, 0);     // Sunday 23:59:59 -> in
            add(monday.AddDays(7), "sess-edge2", "claude-sonnet-5-5", 0, 1000000, 0, 0, 0);                  // next Monday 00:00:00 -> out
            add(monday.AddSeconds(-1), "sess-edge3", "claude-sonnet-5-5", 0, 1000000, 0, 0, 0);              // previous Sunday 23:59:59 -> previous week
            // synthetic / empty-model rows are skipped by the report
            rows.Add(DetailRow(monday.AddHours(5), "sess-synth", "proj", "<synthetic>", 1, 1, 1, 1, 1, false, "msg_synth"));
            rows.Add(DetailRow(monday.AddHours(6), "sess-synth", "proj", "", 1, 1, 1, 1, 1, false, "msg_empty"));
            if (addGarbage)
            {
                rows.Insert(3, "");
                rows.Insert(5, "garbage line without commas");
                rows.Insert(7, monday.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                rows.Insert(9, "2026-13-45 99:99:99,x,y,claude-sonnet-5-5,1,2,3,4,5,0,m,p,0,");
                rows.Insert(11, monday.AddHours(9).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ",s,p,claude-sonnet-5-5,1,2");
                rows.Insert(13, "\"unterminated,quote," + new string('x', 5000));
                // non-numeric token cells: the row still counts as one message with zero tokens
                add(monday.AddDays(3).AddHours(1), "sess-nonnum", "claude-sonnet-5-5", 0, 0, 0, 0, 0);
                rows[rows.Count - 1] = rows[rows.Count - 1].Replace(",0,0,0,0,0,", ",abc,def,ghi,jkl,mno,");
            }
            fx.Sessions = sessions.Count;
            File.WriteAllLines(Path.Combine(fx.Logs, "用量明细_v2.csv"), new string[] { DetailHeader }, new UTF8Encoding(true));
            File.AppendAllLines(Path.Combine(fx.Logs, "用量明细_v2.csv"), rows.ToArray(), new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(fx.Logs, "用量标注.csv"), tags.ToArray(), new UTF8Encoding(true));
            List<string> idx = new List<string>();
            idx.Add("会话ID,项目,标题,开始时间,最后时间,消息数,子任务消息数,输入,输出,缓存写入,缓存读取,主会话文件");
            idx.Add("sess-xss1,proj," + CsvQuote(XssTitle1) + ",,,0,0,0,0,0,0,");
            idx.Add("sess-xss2,proj," + CsvQuote(XssTitle2) + ",,,0,0,0,0,0,0,");
            idx.Add("sess-0,proj," + CsvQuote("Plain, title with comma and \"quotes\" 中文标题 😀") + ",,,0,0,0,0,0,0,");
            idx.Add("sess-edge,proj," + CsvQuote(new string('L', 3000)) + ",,,0,0,0,0,0,0,");
            File.WriteAllLines(Path.Combine(fx.Logs, "会话索引.csv"), idx.ToArray(), new UTF8Encoding(true));
            TagStore.Configure(Path.Combine(fx.Logs, "用量标注.csv"));
            return fx;
        }

        private static string HtmlProblems(string html)
        {
            List<string> p = new List<string>();
            string h = html.TrimStart('﻿');
            if (!h.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal)) p.Add("no doctype");
            if (!h.TrimEnd().EndsWith("</html>", StringComparison.Ordinal)) p.Add("no </html> at the end");
            string[] tags = { "html", "head", "body", "main", "style", "table", "tr", "td", "th", "div", "span", "h1", "h2", "ul", "li", "b", "p", "title" };
            foreach (string t in tags)
            {
                int open = Regex.Matches(h, "<" + t + "[\\s>]", RegexOptions.IgnoreCase).Count;
                int close = Regex.Matches(h, "</" + t + ">", RegexOptions.IgnoreCase).Count;
                if (open != close) p.Add("<" + t + "> opened " + open + " closed " + close);
            }
            if (Regex.IsMatch(h, "<(script|img|iframe|frame|link|object|embed|svg|form|input|base|audio|video|source|math)[\\s/>]", RegexOptions.IgnoreCase)) p.Add("dangerous tag: " + Regex.Match(h, "<(script|img|iframe|frame|link|object|embed|svg|form|input|base|audio|video|source|math)[\\s/>]", RegexOptions.IgnoreCase).Value);
            // the only meta http-equiv the report may have is a strict Content-Security-Policy (default-src 'none', no remote sources)
            foreach (Match mm in Regex.Matches(h, "<meta[^>]*http-equiv[^>]*>", RegexOptions.IgnoreCase))
            {
                bool csp = Regex.IsMatch(mm.Value, "http-equiv=\"Content-Security-Policy\"", RegexOptions.IgnoreCase) && Has(mm.Value, "default-src 'none'")
                    && !Regex.IsMatch(mm.Value, "unsafe-eval|script-src|connect-src|img-src|\\*|https?:", RegexOptions.IgnoreCase);
                if (!csp) p.Add("meta http-equiv that is not a strict CSP: " + mm.Value);
            }
            if (Regex.IsMatch(h, "<[a-zA-Z][^>]*\\son[a-zA-Z]+\\s*=", RegexOptions.IgnoreCase)) p.Add("event handler attribute: " + Regex.Match(h, "<[a-zA-Z][^>]*\\son[a-zA-Z]+\\s*=", RegexOptions.IgnoreCase).Value);
            if (Regex.IsMatch(h, "<[^>]*\\s(src|href|action|formaction|xlink:href|poster|data)\\s*=", RegexOptions.IgnoreCase)) p.Add("src/href attribute");
            if (Regex.IsMatch(h, "(javascript|vbscript):", RegexOptions.IgnoreCase)) p.Add("javascript: url");
            if (Regex.IsMatch(h, "https?:", RegexOptions.IgnoreCase)) p.Add("http(s) reference");
            if (Regex.IsMatch(h, "@import|url\\(", RegexOptions.IgnoreCase)) p.Add("css import/url()");
            if (Has(h, "NaN") || Has(h, "Infinity") || Has(h, "∞")) p.Add("NaN/Infinity text");
            if (Regex.IsMatch(h, "System\\.\\w+Exception|\\{[0-9]\\}")) p.Add("exception / format artifact");
            return string.Join("; ", p.ToArray());
        }

        private static double FirstNumberAfter(string html, string pattern)
        {
            Match m = Regex.Match(html, pattern);
            double v;
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return double.NaN;
            return v;
        }

        private static void TestReport()
        {
            string home = NewHome("report");
            UseHome(home);
            ResetStatics();
            DateTime monday = new DateTime(2026, 11, 2);
            Fx fx = MakeFixture(home, monday, true, true);
            WriteQuotaCsv(fx.Logs, "", false);
            AccountStore.Init(fx.Logs);
            string path = WeeklyReport.Generate(fx.Logs, fx.Start, fx.End, false);
            Check("report file is written to <Home>\\logs\\<weekly folder>\\...<monday>.html", File.Exists(path) && path == WeeklyReport.PathFor(fx.Logs, monday) && path.StartsWith(AppPaths.Home, StringComparison.OrdinalIgnoreCase) && path.EndsWith("2026-11-02.html"), path);
            Check("no .tmp file is left behind", !File.Exists(path + ".tmp") && Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0);
            byte[] raw = File.ReadAllBytes(path);
            Check("file is UTF-8 with BOM (browsers open it correctly)", raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF);
            string html = File.ReadAllText(path, Encoding.UTF8);
            string problems = HtmlProblems(html);
            Check("HTML looks valid: doctype, balanced tags, no script / event handlers / http(s) / NaN", problems == "", problems);
            Check("hostile title <script>alert(1)</script> is shown HTML-encoded", Has(html, "&lt;script&gt;alert(1)&lt;/script&gt;") && !Has(html, "<script"));
            Check("hostile title \"><img src=x onerror=alert(1)> is shown HTML-encoded", Has(html, "&quot;&gt;&lt;img src=x onerror=alert(1)&gt;") && !Has(html, "\"><img"));
            Check("hostile model name is shown HTML-encoded", Has(html, "evil&quot;&gt;&lt;svg onload=alert(1)&gt;") && !Has(html, "<svg"));
            Check("title with comma, quotes, CJK and emoji survives (decoded text is identical)", Has(System.Net.WebUtility.HtmlDecode(html), "Plain, title with comma and \"quotes\" 中文标题 😀"));
            Check("a 3000-character title does not break the page", HtmlProblems(html) == "" && Has(html, new string('L', 3000)));
            Check("2026-11-02 ~ 11-08 in the heading", Has(html, "2026-11-02 ~ 11-08"));
            Check("no 'still running' note on a finished week", !Has(html, "这一周还没过完"));
            int msgs = (int)FirstNumberAfter(html, "token (?:·|&#183;) (\\d+) 条消息");
            Check("message count in the overview = rows of that week (" + fx.MsgsWeek + " incl. boundary rows, excl. next Monday 00:00:00, synthetic and empty-model rows)", msgs == fx.MsgsWeek, "html=" + msgs + " expected=" + fx.MsgsWeek);
            int sess = (int)FirstNumberAfter(html, "<b>(\\d+)</b><span>活跃会话");
            Check("active sessions in the overview = distinct sessions in that week (" + fx.Sessions + ")", sess == fx.Sessions, "html=" + sess + " expected=" + fx.Sessions);
            Match usdM = Regex.Match(html, "<b>(\\$[0-9.]+)</b><span>API 等价成本");
            Check("API-equivalent cost = sum of the rows priced by the table (" + UsdFmt(fx.UsdWeek) + ")", usdM.Success && usdM.Groups[1].Value == UsdFmt(fx.UsdWeek), "html=" + (usdM.Success ? usdM.Groups[1].Value : "<none>") + " expected=" + fx.UsdWeek);
            Check("the report has a strict Content-Security-Policy (default-src 'none') as a second line of defence", Has(html, "Content-Security-Policy") && Has(html, "default-src 'none'"));
            double p5 = FirstNumberAfter(html, "折合 5 小时额度窗口（([0-9.]+)%）");
            Check("5 h quota % = cost x K5 x tier factor (" + fx.P5Week.ToString("0.0", CultureInfo.InvariantCulture) + "%)", Math.Abs(p5 - fx.P5Week) <= Math.Max(0.11, fx.P5Week * 1e-6), "html=" + p5 + " expected=" + fx.P5Week);
            double pw = FirstNumberAfter(html, "<b>([0-9.]+)%</b><span>折合每周额度");
            Check("weekly quota % = cost x KW x tier factor (" + fx.PwWeek.ToString("0.0", CultureInfo.InvariantCulture) + "%)", Math.Abs(pw - fx.PwWeek) <= Math.Max(0.11, fx.PwWeek * 1e-6), "html=" + pw + " expected=" + fx.PwWeek);
            double unk = FirstNumberAfter(html, "有 (\\d+) 条消息没有额度系数");
            Check("'N messages have no quota coefficient' = rows of the model without a price entry (" + fx.UnknownCoef + ")", (int)unk == fx.UnknownCoef, "html=" + unk);
            double prevPct = FirstNumberAfter(html, "较上周 [+−]([0-9]+)%");
            double expPrev = Math.Abs((fx.UsdWeek / fx.UsdPrev - 1) * 100);
            Check("'vs last week' percentage is right (" + expPrev.ToString("0", CultureInfo.InvariantCulture) + "%), previous-week rows incl. Sunday 23:59:59", Math.Abs(prevPct - Math.Round(expPrev)) <= 1, "html=" + prevPct + " expected=" + expPrev);
            Check("tier labels from the tag file show up ('high')", Has(html, "claude-sonnet-5-5 · high") && Has(html, "· 未标注"));
            Check("the top-10 table lists both hostile sessions first (largest consumers)", html.IndexOf("&lt;script&gt;", StringComparison.Ordinal) > 0 && html.IndexOf("&lt;script&gt;", StringComparison.Ordinal) < html.IndexOf("缓存过期后的重载", StringComparison.Ordinal));
            Check("coefficient table says the coefficients are measured", Has(html, "实测"));
            Check("heat map has 7 x 24 cells", Regex.Matches(html, "<td title=\"周[一二三四五六日] \\d\\d:00").Count == 168);
            Check("daily table has 7 rows (Mon..Sun)", Regex.Matches(html, "<tr><td>周[一二三四五六日] 11-0[2-8]</td>").Count == 7);

            // generate again over the same file: replaced atomically, still valid
            string again = WeeklyReport.Generate(fx.Logs, fx.Start, fx.End, true);
            string html2 = File.ReadAllText(again, Encoding.UTF8);
            Check("second Generate (partial=true) overwrites the same file, adds the 'week not over' note, no .tmp left", again == path && Has(html2, "这一周还没过完") && HtmlProblems(html2) == "" && !File.Exists(path + ".tmp"));

            // ---------- other weeks / missing pieces
            DateTime emptyWeek = new DateTime(2026, 9, 7);
            string p2 = WeeklyReport.Generate(fx.Logs, emptyWeek, emptyWeek.AddDays(7), false);
            string h2 = File.ReadAllText(p2, Encoding.UTF8);
            Check("week without any rows: report is still produced and valid", HtmlProblems(h2) == "", HtmlProblems(h2));
            Check("... says there is no usage, 'last week had none', and shows $0.00 (no NaN / division by zero)", Has(h2, "没有用量") && Has(h2, "上周没有用量") && Has(h2, "$0.00") && !Has(h2, "NaN"));

            string noQuotaHome = NewHome("report-noquota");
            Fx fx2 = MakeFixture(noQuotaHome, monday, false, false);
            UseHome(noQuotaHome);
            AccountStore.Init(fx2.Logs);
            string p3 = WeeklyReport.Generate(fx2.Logs, fx2.Start, fx2.End, false);
            string h3 = File.ReadAllText(p3, Encoding.UTF8);
            Check("no quota table at all (额度换算.csv missing): report still produced and valid", HtmlProblems(h3) == "", HtmlProblems(h3));
            Check("... every message is flagged as having no coefficient, cost shows $0.00", (int)FirstNumberAfter(h3, "有 (\\d+) 条消息没有额度系数") == fx2.MsgsWeek && Has(h3, "<b>$0.00</b>"));

            string derHome = NewHome("report-derived");
            Fx fx3 = MakeFixture(derHome, monday, false, false);
            UseHome(derHome);
            WriteQuotaCsv(fx3.Logs, "", true);
            AccountStore.Init(fx3.Logs);
            string h4 = File.ReadAllText(WeeklyReport.Generate(fx3.Logs, fx3.Start, fx3.End, false), Encoding.UTF8);
            Check("plan-derived coefficients are called 'unreliable' in the report", Has(h4, "按套餐倍率推算（不可靠）") && HtmlProblems(h4) == "", HtmlProblems(h4));

            string noDetHome = NewHome("report-nodetail");
            UseHome(noDetHome);
            Directory.CreateDirectory(Path.Combine(noDetHome, "logs"));
            bool threw = false;
            try
            {
                WeeklyReport.Generate(Path.Combine(noDetHome, "logs"), monday, monday.AddDays(7), false);
            }
            catch (FileNotFoundException)
            {
                threw = true;
            }
            Check("no history file: Generate throws FileNotFoundException (callers check first / show a balloon)", threw);
            Check("... and leaves no report folder behind", !Directory.Exists(Path.Combine(noDetHome, "logs", "周报")));

            // ---------- hostile numbers
            string hostHome = NewHome("report-hostile");
            Fx fx4 = MakeFixture(hostHome, monday, false, false);
            UseHome(hostHome);
            WriteQuotaCsv(fx4.Logs, "", false);
            AccountStore.Init(fx4.Logs);
            File.AppendAllLines(Path.Combine(fx4.Logs, "用量明细_v2.csv"), new string[]
            {
                DetailRow(monday.AddDays(1).AddHours(1), "sess-big", "p", "claude-opus-5-5", long.MaxValue / 4, long.MaxValue / 4, long.MaxValue / 4, 0, long.MaxValue / 4, false, "msg_big1"),
                DetailRow(monday.AddDays(1).AddHours(2), "sess-neg", "p", "claude-sonnet-5-5", -5, -1000000, -3, -4, -100, false, "msg_neg1"),
                DetailRow(monday.AddDays(1).AddHours(3), "sess-max", "p", "claude-sonnet-5-5", long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, false, "msg_max1")
            }, new UTF8Encoding(false));
            string h5 = null;
            bool ok5 = Try(delegate { h5 = File.ReadAllText(WeeklyReport.Generate(fx4.Logs, fx4.Start, fx4.End, false), Encoding.UTF8); });
            Check("absurd token counts (huge / negative / long.MaxValue): Generate does not throw", ok5);
            if (ok5)
            {
                Check("... and the page is still valid, without NaN / Infinity text", HtmlProblems(h5) == "", HtmlProblems(h5));
            }
        }

        private static void TestReportAccounts()
        {
            string home = NewHome("report-acct");
            UseHome(home);
            ResetStatics();
            DateTime monday = new DateTime(2026, 11, 2);
            Fx fx = MakeFixture(home, monday, false, false);
            string evilKey = "<img src=x onerror=alert(1)>";
            List<string> tl = new List<string>();
            tl.Add("时间UTC,账号,计划,来源");
            tl.Add("2026-10-01 00:00:00," + CsvQuote(evilKey) + ",\"max5\",\"setup\"");
            tl.Add("2026-11-04 04:00:00,\"B\",\"pro\"," + CsvQuote("manual <script>alert(2)</script>"));
            File.WriteAllLines(Path.Combine(fx.Logs, "账号时段.csv"), tl.ToArray(), new UTF8Encoding(true));
            List<string> q = new List<string>();
            q.Add(QuotaHeaderLine);
            foreach (string key in new string[] { evilKey + ":max5", "B:pro" })
            {
                q.Add(QuotaGlobalRow(false, key));
                q.Add(QuotaModelRow("claude-sonnet-5-5", 3, 15, 3.75, 6, 0.3, key == "B:pro" ? 3.5 : 2.0, 0.5, "实测", key));
                q.Add(QuotaModelRow("claude-opus-5-5", 15, 75, 18.75, 30, 1.5, 3.0, 0.8, "实测", key));
            }
            File.WriteAllLines(Path.Combine(fx.Logs, "额度换算.csv"), q.ToArray(), new UTF8Encoding(true));
            AccountStore.Init(fx.Logs);
            Check("(setup) timeline with a hostile account id loaded", AccountStore.TimelineCopy().Count == 2 && AccountStore.CurrentKey == "B:pro");
            string html = File.ReadAllText(WeeklyReport.Generate(fx.Logs, fx.Start, fx.End, false), Encoding.UTF8);
            string problems = HtmlProblems(html);
            Check("two accounts / plans: page valid, no script / handlers / http", problems == "", problems);
            Check("hostile account id is HTML-encoded in the coefficient table", Has(html, "&lt;img src=x onerror=alert(1)&gt;:max5") && !Has(html, "<img"));
            Check("account switch list present and the hostile source text is encoded", Has(html, "账号 / 计划切换") && Has(html, "manual &lt;script&gt;alert(2)&lt;/script&gt;") && !Has(html, "<script"));
            int coefStart = html.IndexOf("<h2>估算系数</h2>", StringComparison.Ordinal);
            int coefEnd = coefStart < 0 ? -1 : html.IndexOf("</table>", coefStart, StringComparison.Ordinal);
            string coef = coefStart >= 0 && coefEnd > coefStart ? html.Substring(coefStart, coefEnd - coefStart) : "";
            Check("coefficient table: one row per account:plan key (2 keys), both keys named, none called 'no coefficients yet'",
                Regex.Matches(coef, "<tr><td>").Count == 2 && Has(coef, "&lt;img src=x onerror=alert(1)&gt;:max5") && Has(coef, ">B:pro<") && !Has(coef, "还没有自己的拟合结果"),
                "rows=" + Regex.Matches(coef, "<tr><td>").Count);
            int msgs = (int)FirstNumberAfter(html, "token (?:·|&#183;) (\\d+) 条消息");
            Check("message count unaffected by account splitting", msgs == fx.MsgsWeek, "html=" + msgs + " expected=" + fx.MsgsWeek);
            AccountStore.Init(Path.Combine(NewHome("acct-reset"), "logs"));
        }

        // ================================================================== F weekly auto-report scheduling
        private static DateTime MondayOf(DateTime d)
        {
            return (DateTime)CallStatic(typeof(Widget), "MondayOf", new object[] { d });
        }

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                Thread.Sleep(25);
            }
            return cond();
        }

        private static void TestWeeklySchedule()
        {
            // ---- MondayOf: boundary cases (the product cannot inject a clock, so the date math is tested on its own)
            string[] cases =
            {
                "2026-11-09 00:00:00.000>2026-11-09", "2026-11-09 00:00:00.001>2026-11-09", "2026-11-08 23:59:59.999>2026-11-02", "2026-11-15 23:59:59.999>2026-11-09",
                "2026-11-16 00:00:00.000>2026-11-16", "2026-11-12 13:30:00.000>2026-11-09", "2027-01-01 12:00:00.000>2026-12-28", "2026-01-01 00:00:00.000>2025-12-29",
                "2024-03-01 08:00:00.000>2024-02-26", "2028-03-05 00:00:00.000>2028-02-28", "2026-03-29 02:30:00.000>2026-03-23", "2026-10-25 01:30:00.000>2026-10-19", "0001-01-01 00:00:00.000>0001-01-01"
            };
            int bad = 0;
            foreach (string c in cases)
            {
                string[] p = c.Split('>');
                DateTime d = DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                DateTime exp = DateTime.ParseExact(p[1], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                DateTime got;
                try
                {
                    got = MondayOf(d);
                }
                catch (Exception ex)
                {
                    bad++;
                    Emit("      MondayOf(" + p[0] + ") threw " + ex.InnerException.GetType().Name);
                    continue;
                }
                if (got != exp || got.DayOfWeek != DayOfWeek.Monday || got.TimeOfDay != TimeSpan.Zero)
                {
                    bad++;
                    Emit("      MondayOf(" + p[0] + ") = " + got.ToString("yyyy-MM-dd HH:mm:ss") + " expected " + p[1]);
                }
            }
            Check("MondayOf: Monday 00:00:00.000, Sunday 23:59:59.999, year / leap / DST dates (" + cases.Length + " cases)", bad == 0, bad + " wrong");
            bool anyOld = false;
            for (int i = 0; i < 400; i++)
            {
                DateTime d = new DateTime(2026, 1, 1).AddDays(i).AddMinutes(i * 7);
                DateTime m = MondayOf(d);
                if (m.DayOfWeek != DayOfWeek.Monday || m > d || (d - m).TotalDays >= 7) anyOld = true;
            }
            Check("MondayOf: for 400 consecutive days the result is a Monday within the 7 days before", !anyOld);

            // ---- CheckWeeklyReport
            string home = NewHome("weekly");
            Widget w = NewWidget(home, null);
            string logs = AppPaths.DataDir;
            DateTime lastMonday = MondayOf(DateTime.Now).AddDays(-7);
            string report = WeeklyReport.PathFor(logs, lastMonday);
            Func<bool> busy = delegate { return G<int>(w, "reportBusy") != 0; };
            Action<string> clean = delegate(string why)
            {
                if (File.Exists(report)) File.Delete(report);
                Set(w, "reportCheckUtc", DateTime.MinValue);
                Set(w, "paused", false);
            };
            Check("Recommended profile has the weekly report feature on", (G<int>(w, "fmask") & Feat.Weekly) != 0);

            // 1. no history file at all -> nothing happens
            Call(w, "CheckWeeklyReport");
            Thread.Sleep(300);
            Check("no history (no usage log): no report, no background job", !File.Exists(report) && !busy());
            Check("... the check still counts as done for this hour (throttled)", G<DateTime>(w, "reportCheckUtc") > DateTime.UtcNow.AddMinutes(-1));

            // 2. history exists -> exactly one report for the last full week
            Fx fx = MakeFixture(home, lastMonday, false, false);
            WriteQuotaCsv(fx.Logs, "", false);
            clean("history");
            Call(w, "CheckWeeklyReport");
            Check("history present: the report for the last full week appears (" + Path.GetFileName(report) + ")", WaitFor(delegate { return File.Exists(report); }, 15000));
            Check("... and the background job finishes (reportBusy back to 0)", WaitFor(delegate { return !busy(); }, 5000));
            DateTime stamp = File.Exists(report) ? File.GetLastWriteTimeUtc(report) : DateTime.MinValue;
            Check("... the generated page is valid", File.Exists(report) && HtmlProblems(File.ReadAllText(report, Encoding.UTF8)) == "");

            // 3. runs once: second call within the hour does nothing even if the file vanished; after the hour the existing file prevents a re-run
            File.Delete(report);
            Call(w, "CheckWeeklyReport");
            Thread.Sleep(500);
            Check("second check within the same hour does nothing (throttle), even though the report is gone", !File.Exists(report) && !busy());
            File.WriteAllText(report, "placeholder", new UTF8Encoding(false));
            stamp = File.GetLastWriteTimeUtc(report);
            Set(w, "reportCheckUtc", DateTime.UtcNow.AddHours(-2));
            Call(w, "CheckWeeklyReport");
            Thread.Sleep(500);
            Check("an hour later with the report already there: not generated again (file untouched)", File.ReadAllText(report) == "placeholder" && File.GetLastWriteTimeUtc(report) == stamp && !busy());

            // 4. profile turns it off
            clean("profile");
            Call(w, "ApplyPreset", 2);
            Check("(Minimal profile has no weekly report feature)", (G<int>(w, "fmask") & Feat.Weekly) == 0);
            Set(w, "reportCheckUtc", DateTime.MinValue);
            Call(w, "CheckWeeklyReport");
            Thread.Sleep(500);
            Check("profile Minimal: no report is generated and the hourly timer is not even consumed", !File.Exists(report) && !busy() && G<DateTime>(w, "reportCheckUtc") == DateTime.MinValue);
            Call(w, "ApplyPreset", 1);
            Check("back to Recommended: the next check generates it", Try(delegate { Call(w, "CheckWeeklyReport"); }) && WaitFor(delegate { return File.Exists(report); }, 15000));
            WaitFor(delegate { return !busy(); }, 5000);

            // 5. paused
            clean("paused");
            Set(w, "paused", true);
            Call(w, "CheckWeeklyReport");
            Thread.Sleep(400);
            Check("paused (background reading suspended): no report", !File.Exists(report) && !busy() && G<DateTime>(w, "reportCheckUtc") == DateTime.MinValue);
            Set(w, "paused", false);

            // 6. an empty history file (header only / no rows in that week)
            File.WriteAllLines(Path.Combine(logs, "用量明细_v2.csv"), new string[] { DetailHeader }, new UTF8Encoding(true));
            clean("empty");
            Call(w, "CheckWeeklyReport");
            bool made = WaitFor(delegate { return File.Exists(report); }, 8000);
            WaitFor(delegate { return !busy(); }, 5000);
            Note("history file exists but has no rows for last week: report " + (made ? "IS generated (an empty page + notification every Monday for an idle user)" : "is skipped"));
            Check("history file with only a header: no exception, page (if any) is valid", !made || HtmlProblems(File.ReadAllText(report, Encoding.UTF8)) == "");

            // 7. job failure does not leave the busy flag stuck
            File.Delete(Path.Combine(logs, "用量明细_v2.csv"));
            Call(w, "MakeReport", true, false);
            Check("MakeReport with the log deleted meanwhile: job fails quietly and reportBusy returns to 0", WaitFor(delegate { return !busy(); }, 5000));
            Set(w, "reportBusy", 1);
            Call(w, "MakeReport", true, false);
            Check("MakeReport while another job runs: returns at once, does not start a second job", G<int>(w, "reportBusy") == 1);
            Set(w, "reportBusy", 0);
        }

        // ================================================================== G forecast / speed / occupancy math
        private static QuotaTable MakeQuota(bool derived, bool stale)
        {
            QuotaTable q = new QuotaTable();
            QuotaTable.Entry e = new QuotaTable.Entry();
            double[] p = { 3, 15, 3.75, 6, 0.3 };
            for (int i = 0; i < 5; i++) e.Price[i] = p[i];
            e.K5 = 2.0;
            e.KW = 0.5;
            e.Measured5 = true;
            e.MeasuredW = true;
            q.Models["claude-sonnet-5-5"] = e;
            q.PlanDerived = derived;
            q.Stale = stale;
            return q;
        }

        // one assistant message of the sonnet model with <outTokens> output tokens: costs outTokens*15/1e6 USD = 5h: x2, week: x0.5
        private static UsageTracker.RecentUse Use(DateTime utc, long outTokens)
        {
            UsageTracker.RecentUse u = new UsageTracker.RecentUse();
            u.Utc = utc;
            u.Model = "claude-sonnet-5-5";
            u.Tier = "medium";
            u.Tok[1] = outTokens;
            return u;
        }

        private static UsageTracker.LiveInfo LiveWith(params UsageTracker.RecentUse[] uses)
        {
            UsageTracker.LiveInfo li = new UsageTracker.LiveInfo();
            foreach (UsageTracker.RecentUse u in uses)
            {
                li.Recent.Add(u);
                if (u.Utc > li.LastUtc) li.LastUtc = u.Utc;
            }
            li.Model = "claude-sonnet-5-5";
            return li;
        }

        private static FetchResult Quotas(double p5, double pw, int resetMin5, int resetHoursW)
        {
            FetchResult r = new FetchResult();
            DateTime now = DateTime.Now;
            if (p5 >= 0)
            {
                r.FiveHour = new Quota();
                r.FiveHour.Percent = p5;
                r.FiveHour.ResetsAt = resetMin5 == int.MinValue ? (DateTime?)null : StatusLineBridge.RoundMinute(now.AddMinutes(resetMin5));
            }
            if (pw >= 0)
            {
                r.SevenDay = new Quota();
                r.SevenDay.Percent = pw;
                r.SevenDay.ResetsAt = resetHoursW == int.MinValue ? (DateTime?)null : StatusLineBridge.RoundMinute(now.AddHours(resetHoursW));
            }
            r.FetchedAt = now;
            r.Via = "bridge";
            return r;
        }

        private static bool BadNumberText(string s)
        {
            return s != null && (Has(s, "NaN") || Has(s, "Infinity") || Has(s, "\u221e") || HasI(s, "\u65e0\u7a77"));
        }

        private static double F(object d, string key, string field)
        {
            object si = ((System.Collections.IDictionary)d)[key];
            return (double)Get(si, field);
        }

        private static void TestSpeedMath()
        {
            string home = NewHome("speed");
            Widget w = NewWidget(home, null);
            Set(w, "quota", MakeQuota(false, false));
            Set(w, "quotaReadUtc", DateTime.UtcNow);
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            DateTime now = DateTime.UtcNow;

            Func<int, object[]> run = delegate(int speedMin)
            {
                Set(w, "speedMin", speedMin);
                Set(w, "keyStartAt", DateTime.MinValue);
                object[] args = { DateTime.UtcNow, 0.0, 0.0, false };
                object dict = Call(w, "ComputeSpeeds", args);
                return new object[] { dict, args[1], args[2], args[3] };
            };

            // 1. nothing at all
            object[] r = run(15);
            Check("no sessions: speed 0, 'known' false, empty result", (double)r[1] == 0 && (double)r[2] == 0 && !(bool)r[3] && ((System.Collections.IDictionary)r[0]).Count == 0);

            // 2. one message 2 minutes ago = 1M output tokens = $15 -> 30 % of the 5h quota, 7.5 % of the weekly quota
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-2), 1000000));
            r = run(15);
            Check("one $15 message in a 15 min window: 5h speed 120 %/h, weekly 30 %/h", Math.Abs((double)r[1] - 120) < 0.01 && Math.Abs((double)r[2] - 30) < 0.01 && (bool)r[3], (double)r[1] + " / " + (double)r[2]);
            Check("... occupancy (P5 / Pw) = 30 % / 7.5 %: window total, not divided by the window", Math.Abs(F(r[0], "s1", "P5") - 30) < 0.01 && Math.Abs(F(r[0], "s1", "Pw") - 7.5) < 0.01);
            Check("... all-sessions occupancy fields occ5 / occW = 30 / 7.5", Math.Abs(G<double>(w, "occ5") - 30) < 0.01 && Math.Abs(G<double>(w, "occW") - 7.5) < 0.01);
            r = run(60);
            Check("same message, 60 min window: speed 30 %/h (= occupancy 30 / 1 h), occupancy still 30", Math.Abs((double)r[1] - 30) < 0.01 && Math.Abs(F(r[0], "s1", "P5") - 30) < 0.01);
            r = run(5);
            Check("same message, 5 min window: speed 360 %/h, occupancy still 30", Math.Abs((double)r[1] - 360) < 0.01 && Math.Abs(F(r[0], "s1", "P5") - 30) < 0.01);
            Check("speed x window hours = occupancy for every window length (1, 5, 15, 60, 180 min)", AllWindowsConsistent(w, run));

            // 3. outside the window / in the future
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-2), 1000000));
            r = run(1);
            Check("1 min window, message 2 min ago: not counted (speed 0, known false)", (double)r[1] == 0 && !(bool)r[3]);
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-20), 1000000));
            r = run(15);
            Check("message 20 min ago, 15 min window: not counted", (double)r[1] == 0 && !(bool)r[3]);
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(10), 1000000));
            r = run(15);
            Check("message in the future (clock skew): not counted", (double)r[1] == 0 && !(bool)r[3]);

            // 4. tiny speed / zero tokens / negative tokens
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-1), 1));
            r = run(15);
            Check("1 output token: tiny but positive and finite speed, no exception", (double)r[1] > 0 && (double)r[1] < 0.01 && !double.IsNaN((double)r[1]) && !double.IsInfinity((double)r[1]));
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-1), 0));
            r = run(15);
            Check("0 tokens: known but speed exactly 0", (double)r[1] == 0 && (bool)r[3]);
            string st0 = (string)Call(w, "SpeedText", true, 0.0, 0.0);
            Check("SpeedText(known, 0, 0) = '\u2014' (a dash, not '0 / 0')", st0 == "\u2014", Esc(st0));
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-1), -1000000));
            r = run(15);
            Check("negative token counts (corrupt log): negative speed, no exception", (double)r[1] < 0);
            Check("... SpeedText hides a negative speed ('\u2014')", (string)Call(w, "SpeedText", true, (double)r[1], (double)r[2]) == "\u2014");
            Check("SpeedText(unknown) = '\u2014'", (string)Call(w, "SpeedText", false, 5.0, 5.0) == "\u2014");
            Check("SpeedText(120, 30) = '120 / 30'", (string)Call(w, "SpeedText", true, 120.0, 30.0) == "120 / 30", Esc((string)Call(w, "SpeedText", true, 120.0, 30.0)));
            Set(w, "quota", MakeQuota(true, false));
            Check("SpeedText with plan-derived coefficients starts with the approx sign", ((string)Call(w, "SpeedText", true, 120.0, 30.0)).StartsWith("\u2248"));
            Check("OccText(0,0) = dash; OccText(30,7.5) = '\u224830 / 7.50' when derived", (string)Call(w, "OccText", 0.0, 0.0) == "\u2014" && (string)Call(w, "OccText", 30.0, 7.5) == "\u224830 / 7.50", Esc((string)Call(w, "OccText", 30.0, 7.5)));
            Set(w, "quota", MakeQuota(false, false));

            // 5. tier factor
            QuotaTable qt = MakeQuota(false, false);
            qt.Tiers["medium"] = new double[] { 1.5, 1.2 };
            Set(w, "quota", qt);
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-2), 1000000));
            r = run(15);
            Check("tier factor (medium x1.5 / x1.2) applied to speed: 180 / 36 %/h", Math.Abs((double)r[1] - 180) < 0.01 && Math.Abs((double)r[2] - 36) < 0.01, (double)r[1] + " / " + (double)r[2]);
            Set(w, "quota", MakeQuota(false, false));

            // 6. two sessions + history cut at 3 hours, no double counting
            snap.Live.Clear();
            snap.Live["s1"] = LiveWith(Use(now.AddMinutes(-2), 1000000), Use(now.AddMinutes(-60), 1000000));
            Dictionary<string, List<UsageTracker.RecentUse>> hist = new Dictionary<string, List<UsageTracker.RecentUse>>();
            hist["s1"] = new List<UsageTracker.RecentUse>();
            hist["s1"].Add(Use(now.AddHours(-5), 1000000));
            hist["s1"].Add(Use(now.AddMinutes(-60), 1000000));       // same message as in Live: must not be counted twice
            hist["s1"].Add(Use(now.AddMinutes(-2), 1000000));        // newer than 3 h: the live list is the source
            hist["gone"] = new List<UsageTracker.RecentUse>();
            hist["gone"].Add(Use(now.AddHours(-6), 1000000));        // session no longer listed
            Set(w, "speedHist", hist);
            r = run(1440);
            // window 24 h: s1 = live(-2min) + live(-60min) + hist(-5h) = 3 x 30 = 90 %; gone = 30 % -> total 120 % / 24 h = 5 %/h
            Check("24 h window with history: s1 counts live (2) + older history (1) = 90 %, no double counting", Math.Abs(F(r[0], "s1", "P5") - 90) < 0.01, "P5=" + F(r[0], "s1", "P5"));
            Check("... total speed includes sessions that are gone from the list (120 % / 24 h = 5 %/h)", Math.Abs((double)r[1] - 5.0) < 0.001 && Math.Abs(G<double>(w, "occ5") - 120) < 0.01, (double)r[1] + " occ5=" + G<double>(w, "occ5"));
            r = run(120);
            Check("2 h window: the history (older than 3 h) is not used, s1 = the two live messages = 60 %", Math.Abs(F(r[0], "s1", "P5") - 60) < 0.01, "P5=" + F(r[0], "s1", "P5"));
            Set(w, "speedHist", null);

            // 7. grey 'idle' average
            snap.Live.Clear();
            snap.Live["idle"] = LiveWith(Use(now.AddHours(-2), 1000000));
            r = run(15);
            Set(w, "speedBy", r[0]);
            object si = Call(w, "SpeedFor", "idle");
            Check("idle session: no current speed, but a grey 'average while it was active' (120 %/h over its last 15 min window)",
                !(bool)Get(si, "Known") && (bool)Get(si, "HasGray") && Math.Abs((double)Get(si, "G5") - 120) < 0.01 && Math.Abs((double)Get(si, "GP5") - 30) < 0.01, "G5=" + Get(si, "G5"));
            Check("SpeedFor of a session nobody knows: no exception, nothing known", !(bool)Get(Call(w, "SpeedFor", "nobody"), "HasGray"));
            Set(w, "speedBy", null);
            Check("SpeedFor before the first draw (speedBy null) = null, no exception", Call(w, "SpeedFor", "x") == null);

            // 8. account / plan change: only data after the switch counts, divided by at least 5 minutes
            snap.Live.Clear();
            AccountStore.SetCurrent("A", "max5", "test");
            snap.Live["s1"] = LiveWith(Use(DateTime.UtcNow.AddMinutes(-10), 1000000), Use(DateTime.UtcNow, 1000000));
            r = run(15);
            // only the message after the switch: 30 %; hours = max((now-ks), min(0.25, 5/60)) = 5/60 -> 360 %/h
            Check("just after an account / plan switch: older messages ignored, window = max(elapsed, 5 min) -> 360 %/h (not inflated by dividing by ~0)",
                Math.Abs(F(r[0], "s1", "P5") - 30) < 0.01 && (double)r[1] > 300 && (double)r[1] < 400, "P5=" + F(r[0], "s1", "P5") + " speed=" + (double)r[1]);
        }

        private static bool AllWindowsConsistent(Widget w, Func<int, object[]> run)
        {
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            snap.Live["s1"] = LiveWith(Use(DateTime.UtcNow.AddSeconds(-20), 1000000));
            foreach (int m in new int[] { 1, 5, 15, 60, 180 })
            {
                object[] r = run(m);
                double hours = m / 60.0;
                if (Math.Abs((double)r[1] * hours - F(r[0], "s1", "P5")) > 0.01) return false;
            }
            return true;
        }

        private static void TestForecastText()
        {
            string home = NewHome("forecast");
            Widget w = NewWidget(home, null);
            Set(w, "quota", MakeQuota(false, false));
            Set(w, "speedMin", 15);
            DateTime now = DateTime.Now;

            Func<Quota, double, string> lim = delegate(Quota q, double rate)
            {
                object[] a = { "5h", q, rate, now, false };
                return (string)CallStatic(typeof(Widget), "LimitText", a);
            };
            Func<Quota, double, bool> limRisk = delegate(Quota q, double rate)
            {
                object[] a = { "5h", q, rate, now, false };
                CallStatic(typeof(Widget), "LimitText", a);
                return (bool)a[4];
            };
            Quota q50 = new Quota { Percent = 50, ResetsAt = now.AddHours(3) };
            Check("LimitText: no quota object -> '5h \u2014'", lim(null, 10) == "5h \u2014");
            Check("LimitText: 100 % -> already full + risk", Has(lim(new Quota { Percent = 100 }, 10), "\u5df2\u6ee1") && limRisk(new Quota { Percent = 100 }, 10));
            Check("LimitText: 120 % (over) -> already full", Has(lim(new Quota { Percent = 120 }, 10), "\u5df2\u6ee1"));
            Check("LimitText: zero speed -> 'no consumption', no risk", Has(lim(q50, 0), "\u6ca1\u6709\u6d88\u8017") && !limRisk(q50, 0));
            Check("LimitText: negative speed (quota reset) -> 'no consumption'", Has(lim(q50, -25), "\u6ca1\u6709\u6d88\u8017"));
            Check("LimitText: speed exactly 0.001 -> 'no consumption'; 0.0011 -> a (very long) forecast", Has(lim(q50, 0.001), "\u6ca1\u6709\u6d88\u8017") && Has(lim(q50, 0.0011), "\u540e\u89e6\u9876"));
            Check("LimitText: 50 % at 25 %/h = 2.0 hours, reset in 3 h -> risk (hits before reset)", Has(lim(q50, 25), "2.0 \u5c0f\u65f6") && Has(lim(q50, 25), "\u65e9\u4e8e\u91cd\u7f6e") && limRisk(q50, 25));
            Check("LimitText: 50 % at 5 %/h = 10 hours, reset in 3 h -> no risk, 'not before reset'", Has(lim(q50, 5), "10 \u5c0f\u65f6") && Has(lim(q50, 5), "\u91cd\u7f6e\u524d\u4e0d\u4f1a") && !limRisk(q50, 5));
            Check("LimitText: no reset time -> forecast without the 'before reset' part", Has(lim(new Quota { Percent = 50 }, 25), "\u540e\u89e6\u9876") && !Has(lim(new Quota { Percent = 50 }, 25), "\u91cd\u7f6e"));
            Check("LimitText: reset time already past -> no 'before reset' part, no exception", !Has(lim(new Quota { Percent = 50, ResetsAt = now.AddMinutes(-5) }, 25), "\u91cd\u7f6e"));
            Check("LimitText: 0 % used at 1000 %/h = 6 minutes", Has(lim(new Quota { Percent = 0 }, 1000), "6 \u5206\u949f"));
            string[] weird = new string[0];
            List<string> texts = new List<string>();
            foreach (double rate in new double[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, double.MinValue, double.Epsilon, 1e-300, 1e300 })
            {
                foreach (double pct in new double[] { 0, 50, 99.999, 100, -5, double.NaN, double.PositiveInfinity, 1e300 })
                {
                    texts.Add(lim(new Quota { Percent = pct, ResetsAt = now.AddHours(2) }, rate));
                }
            }
            bool anyBad = false;
            string firstBad = null;
            foreach (string t in texts)
            {
                if (BadNumberText(t))
                {
                    anyBad = true;
                    if (firstBad == null) firstBad = t;
                }
            }
            Check("LimitText: 64 combinations of NaN / Infinity / extreme rates and percentages: never throws, never prints NaN / Infinity", !anyBad, Esc(firstBad));

            // HoursText
            Func<double, string> ht = delegate(double h) { return WS("HoursText", h); };
            Check("HoursText: 0 / 0.0001 / negative -> '1 \u5206\u949f' (at least one minute)", ht(0) == "1 \u5206\u949f" && ht(0.0001) == "1 \u5206\u949f" && ht(-3) == "1 \u5206\u949f", ht(0) + "|" + ht(-3));
            Check("HoursText: 0.5 -> 30 min, 1 -> '1.0 h', 9.96 -> '10.0 h', 10 -> '10 h', 47.9 -> '48 h', 48 -> '2.0 days'",
                ht(0.5) == "30 \u5206\u949f" && ht(1) == "1.0 \u5c0f\u65f6" && ht(9.96) == "10.0 \u5c0f\u65f6" && ht(10) == "10 \u5c0f\u65f6" && ht(47.9) == "48 \u5c0f\u65f6" && ht(48) == "2.0 \u5929", ht(0.5) + "|" + ht(1) + "|" + ht(9.96) + "|" + ht(10) + "|" + ht(47.9) + "|" + ht(48));
            if (ht(0.9999) == "60 \u5206\u949f") Note("HoursText(0.9999 h) reads '60 min' instead of '1.0 h' (cosmetic rounding boundary, harmless)");
            Check("HoursText(NaN) never prints NaN", !BadNumberText(ht(double.NaN)), ht(double.NaN));
            KnownBug("BUG-L4", "HoursText(+Infinity) does not print 'Infinity'", !BadNumberText(ht(double.PositiveInfinity)), ht(double.PositiveInfinity));

            // RateText / UsdText / FormatPct
            Func<double, bool, string> rt = delegate(double v, bool wk) { return WS("RateText", v, wk); };
            Check("RateText: 0 -> '0.00', 0.004 -> '0.00', 1 -> '1.0', 9.96 -> '10.0', 10 -> '10', 99.5 -> '100'",
                rt(0, false) == "0.00" && rt(0.004, false) == "0.00" && rt(1, false) == "1.0" && rt(9.96, false) == "10.0" && rt(10, false) == "10" && rt(99.5, false) == "100",
                rt(0, false) + "|" + rt(0.004, false) + "|" + rt(1, false) + "|" + rt(9.96, false) + "|" + rt(10, false) + "|" + rt(99.5, false));
            Check("RateText weekly: 0.05 -> '0.05', 0.1 -> '0.10', 1 -> '1.00'", rt(0.05, true) == "0.05" && rt(0.1, true) == "0.10" && rt(1, true) == "1.00");
            Check("UsdText: 0 -> '$0.00', 12.345 -> '$12.35', 99.995 -> '$100.00' (!), 100 -> '$100', 1500 -> '$1.5k'",
                WS("UsdText", 0.0) == "$0.00" && WS("UsdText", 12.345) == "$12.35" && WS("UsdText", 100.0) == "$100" && WS("UsdText", 1500.0) == "$1.5k", WS("UsdText", 0.0) + "|" + WS("UsdText", 12.345) + "|" + WS("UsdText", 100.0) + "|" + WS("UsdText", 1500.0));
            Check("FormatPct: 5 -> '5.00%', approx 5 -> '\u22485.0%', 50 -> '50%'", WS("FormatPct", 5.0, false) == "5.00%" && WS("FormatPct", 5.0, true) == "\u22485.0%" && WS("FormatPct", 50.0, false) == "50%");
            bool nanOk = !BadNumberText(rt(double.NaN, false)) && !BadNumberText(rt(double.PositiveInfinity, true)) && !BadNumberText(WS("UsdText", double.NaN)) && !BadNumberText(WS("UsdText", double.PositiveInfinity))
                && !BadNumberText(WS("FormatPct", double.NaN, false)) && !BadNumberText(WS("FormatPct", double.PositiveInfinity, true));
            KnownBug("BUG-L4", "RateText / UsdText / FormatPct never print NaN or Infinity",
                nanOk, "RateText(NaN)=" + rt(double.NaN, false) + " RateText(+Inf)=" + rt(double.PositiveInfinity, true) + " UsdText(NaN)=" + WS("UsdText", double.NaN) + " UsdText(+Inf)=" + WS("UsdText", double.PositiveInfinity) + " FormatPct(NaN)=" + WS("FormatPct", double.NaN, false));

            // SpeedForecast (instance)
            Set(w, "data", Quotas(58, 61, 71, 64));
            Func<double, double, bool, object[]> fc = delegate(double r5, double rw, bool known)
            {
                object[] a = { r5, rw, known, false };
                string s = (string)Call(w, "SpeedForecast", a);
                return new object[] { s, a[3] };
            };
            object[] f1 = fc(120, 30, true);
            Check("SpeedForecast: 58 % at 120 %/h -> ~21 min, before the reset in 71 min -> risk", Has((string)f1[0], "21 \u5206\u949f") && Has((string)f1[0], "\u65e9\u4e8e\u91cd\u7f6e") && (bool)f1[1], Esc((string)f1[0]));
            f1 = fc(0.5, 0.01, true);
            Check("SpeedForecast: tiny speeds -> days, 'not before reset', no risk", Has((string)f1[0], "\u91cd\u7f6e\u524d\u4e0d\u4f1a") && !(bool)f1[1] && !BadNumberText((string)f1[0]), Esc((string)f1[0]));
            f1 = fc(0, 0, true);
            Check("SpeedForecast: zero speed -> 'no consumption' for both", Regex.Matches((string)f1[0], "\u6ca1\u6709\u6d88\u8017").Count == 2 && !(bool)f1[1], Esc((string)f1[0]));
            f1 = fc(-40, -2, true);
            Check("SpeedForecast: negative speeds (after a reset) -> 'no consumption', no risk", Regex.Matches((string)f1[0], "\u6ca1\u6709\u6d88\u8017").Count == 2 && !(bool)f1[1]);
            f1 = fc(double.NaN, double.PositiveInfinity, true);
            Check("SpeedForecast: NaN / Infinity speeds: no exception, no 'NaN' / 'Infinity' text", !BadNumberText((string)f1[0]), Esc((string)f1[0]));
            Set(w, "data", Quotas(100, 100, 71, 64));
            f1 = fc(10, 10, true);
            Check("SpeedForecast: 100 % already reached -> 'full' for both, risk", Regex.Matches((string)f1[0], "\u5df2\u6ee1").Count == 2 && (bool)f1[1], Esc((string)f1[0]));
            Set(w, "data", Quotas(58, -1, int.MinValue, 0));
            f1 = fc(10, 10, true);
            Check("SpeedForecast: no weekly quota and no reset time at all -> still a sentence, weekly shows a dash", Has((string)f1[0], "\u5468 \u2014") && !BadNumberText((string)f1[0]), Esc((string)f1[0]));
            Set(w, "data", null);
            Check("SpeedForecast: no data yet -> empty string", (string)fc(1, 1, true)[0] == "");
            Set(w, "data", Quotas(58, 61, 71, 64));
            Set(w, "quota", null);
            Check("SpeedForecast: not known and no coefficient table -> explains why", Has((string)fc(0, 0, false)[0], "\u8fd8\u6ca1\u7b97\u51fa\u6765"));
            Set(w, "quota", MakeQuota(false, false));
            Check("SpeedForecast: not known with a table -> '... no consumption in the last N minutes'", Has((string)fc(0, 0, false)[0], "\u6ca1\u6709\u6d88\u8017") && Has((string)fc(0, 0, false)[0], "15 \u5206\u949f"));
            Set(w, "quota", MakeQuota(true, false));
            Check("SpeedForecast: plan-derived coefficients are flagged as unreliable", Has((string)fc(10, 1, true)[0], "\u5dee\u597d\u51e0\u500d"));
            Set(w, "quota", MakeQuota(false, true));
            Check("SpeedForecast: stale table (just switched account) -> explains instead of guessing", Has((string)fc(0, 0, false)[0], "\u8fd8\u6ca1\u7b97\u51fa\u6765"));
            Set(w, "quota", MakeQuota(false, false));
        }

        private static object NewWeekPoint(DateTime localT, double pct, long resetTicks)
        {
            Type t = typeof(Widget).GetNestedType("WeekPoint", BindingFlags.NonPublic);
            object p = Activator.CreateInstance(t, true);
            Set(p, "T", localT);
            Set(p, "Pct", pct);
            Set(p, "Reset", resetTicks);
            return p;
        }

        private static void TestWeeklyForecast()
        {
            string home = NewHome("weekfc");
            Widget w = NewWidget(home, null);
            Set(w, "weekHistLoaded", true);
            System.Collections.IList hist = (System.Collections.IList)Get(w, "weekHist");
            DateTime now = DateTime.Now;
            Func<double, double, object[]> run = delegate(double pct, double hoursToReset)
            {
                Set(w, "data", Quotas(10, pct, 60, (int)Math.Round(hoursToReset)));
                FetchResult d = G<FetchResult>(w, "data");
                d.SevenDay.ResetsAt = now.AddHours(hoursToReset);
                object[] a = { false };
                string s = (string)Call(w, "WeeklyForecast", a);
                return new object[] { s, a[0] };
            };
            long resetTicks = now.AddHours(100).Ticks;
            object[] r = run(50, 100);
            Check("weekly forecast: no samples yet -> 'not enough data'", Has((string)r[0], "\u6570\u636e\u8fd8\u4e0d\u591f") && !(bool)r[1], Esc((string)r[0]));
            hist.Add(NewWeekPoint(now.AddMinutes(-30), 49, now.AddHours(100).Ticks));
            r = run(50, 100);
            Check("weekly forecast: only 30 min of samples (< 1.5 h) -> 'not enough data'", Has((string)r[0], "\u6570\u636e\u8fd8\u4e0d\u591f"));
            hist.Clear();
            hist.Add(NewWeekPoint(now.AddHours(-3), 40, now.AddHours(100).Ticks));
            r = run(50, 100);
            // 10 points in 3 h = 3.33 %/h; 50 left -> 15 h; reset in 100 h -> risk
            Check("weekly forecast: +10 points in 3 h with 50 % left, reset in 100 h -> 'runs out in ~15 h', risk", Has((string)r[0], "15 \u5c0f\u65f6\u540e\u7528\u5b8c") && (bool)r[1], Esc((string)r[0]));
            r = run(40.5, 100);
            Check("weekly forecast: +0.5 point in 3 h -> 'barely moved', no risk", Has((string)r[0], "\u51e0\u4e4e\u6ca1\u52a8") && !(bool)r[1], Esc((string)r[0]));
            r = run(41.4, 100);
            Check("weekly forecast: +1.4 points (< 1.5 threshold) -> 'barely moved'", Has((string)r[0], "\u51e0\u4e4e\u6ca1\u52a8"));
            r = run(10, 100);
            Check("weekly forecast: reading went DOWN inside the same window (corrupt sample) -> 'barely moved', no exception, no negative hours", Has((string)r[0], "\u51e0\u4e4e\u6ca1\u52a8") && !Has((string)r[0], "-"), Esc((string)r[0]));
            hist.Clear();
            hist.Add(NewWeekPoint(now.AddHours(-3), 90, now.AddHours(100).Ticks + TimeSpan.TicksPerHour * 168));
            r = run(5, 100);
            Check("weekly forecast: samples from the PREVIOUS window (different reset time, quota has since reset) are ignored", Has((string)r[0], "\u6570\u636e\u8fd8\u4e0d\u591f"), Esc((string)r[0]));
            hist.Clear();
            hist.Add(NewWeekPoint(now.AddHours(-3), 97, now.AddHours(100).Ticks));
            r = run(100, 100);
            Check("weekly forecast: already at 100 % and rising -> 'runs out' with a >= 1 minute text, risk", (bool)r[1] && !BadNumberText((string)r[0]), Esc((string)r[0]));
            hist.Clear();
            hist.Add(NewWeekPoint(now.AddHours(-3), 100, now.AddHours(100).Ticks));
            r = run(100, 100);
            KnownBug("BUG-L7", "weekly forecast at 100 % does not claim 'will not run out before the reset'", !Has((string)r[0], "\u91cd\u7f6e\u524d\u4e0d\u4f1a\u7528\u5b8c"), Esc((string)r[0]));
            r = run(50, -3);
            Check("weekly forecast: reset time already passed -> empty string", (string)r[0] == "");
            Set(w, "data", null);
            object[] a2 = { false };
            Check("weekly forecast: no data -> empty string", (string)Call(w, "WeeklyForecast", a2) == "");
        }

        private static object NewFivePoint(DateTime localT, double pct, long resetTicks)
        {
            Type t = typeof(Widget).GetNestedType("FivePoint", BindingFlags.NonPublic);
            object p = Activator.CreateInstance(t, true);
            Set(p, "T", localT);
            Set(p, "Pct", pct);
            Set(p, "Reset", resetTicks);
            return p;
        }

        private static void TestHealth()
        {
            string home = NewHome("health");
            Widget w = NewWidget(home, null);
            Set(w, "fiveLoaded", true);
            Set(w, "weekHistLoaded", true);
            System.Collections.IList hist = (System.Collections.IList)Get(w, "fiveHist");
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            Func<object> compute = delegate { return Call(w, "ComputeHealth"); };
            Func<object, int> lvl = delegate(object h) { return (int)Get(h, "Level"); };

            Check("self-check: no coefficient table -> level 0 (not enough data), explains", lvl(compute()) == 0 && Has((string)Get(compute(), "Detail"), "\u6ca1\u6cd5\u6838\u5bf9"));
            Set(w, "quota", MakeQuota(false, true));
            Check("self-check: stale table (just switched account) -> level 0", lvl(compute()) == 0);
            Set(w, "quota", MakeQuota(false, false));
            Check("self-check: no quota reading yet -> level 0", lvl(compute()) == 0);
            DateTime now = DateTime.Now;
            Set(w, "data", Quotas(40, 10, 120, 100));
            G<FetchResult>(w, "data").FetchedAt = now;
            long reset = G<FetchResult>(w, "data").FiveHour.ResetsAt.Value.Ticks;
            Check("self-check: no history of readings -> level 0", lvl(compute()) == 0);
            hist.Add(NewFivePoint(now.AddMinutes(-3), 20, reset));
            Check("self-check: reading from only 3 min ago (< 5 min) is not used -> level 0", lvl(compute()) == 0);
            hist.Clear();
            hist.Add(NewFivePoint(now.AddMinutes(-60), 37, reset));
            Check("self-check: reading rose only 3 points (< 6) -> level 0", lvl(compute()) == 0);
            hist.Clear();
            hist.Add(NewFivePoint(now.AddMinutes(-60), 20, reset + 12345));
            Check("self-check: earlier reading belongs to another window (reset time differs) -> level 0", lvl(compute()) == 0);
            hist.Clear();
            hist.Add(NewFivePoint(now.AddMinutes(-60), 20, reset));
            Check("self-check: readings fine but no tracked usage in between -> level 0", lvl(compute()) == 0);

            // reading rose 20 points in the last hour; estimated usage in that hour = X tokens
            Func<long, object> withTokens = delegate(long outTok)
            {
                snap.Live.Clear();
                snap.Live["s1"] = LiveWith(Use(DateTime.UtcNow.AddMinutes(-30), outTok));
                return compute();
            };
            object h1 = withTokens(666667);       // est = 20.0 -> ratio 1.0
            Check("self-check: estimate 20 vs reading +20 -> level 1 'matches'", lvl(h1) == 1 && Has((string)Get(h1, "Short"), "\u543b\u5408"), "level=" + lvl(h1) + " " + Esc((string)Get(h1, "Short")));
            h1 = withTokens(866667);              // est = 26 -> ratio 1.3
            Check("self-check: ratio 1.3 is still within tolerance -> level 1", lvl(h1) == 1);
            h1 = withTokens(1000000);             // est 30 -> ratio 1.5
            Check("self-check: estimate 30 vs +20 (x1.5) -> level 2 'estimate too high'", lvl(h1) == 2 && Has((string)Get(h1, "Short"), "\u504f\u9ad8") && Has((string)Get(h1, "Line"), "1.5"), "level=" + lvl(h1) + " " + Esc((string)Get(h1, "Short")));
            h1 = withTokens(500000);              // est 15 -> ratio 0.75
            Check("self-check: ratio 0.75 is still within tolerance -> level 1", lvl(h1) == 1);
            h1 = withTokens(333333);              // est 10 -> ratio 0.5
            Check("self-check: estimate 10 vs +20 (x0.5) -> level 3 'estimate too low' (2.0x)", lvl(h1) == 3 && Has((string)Get(h1, "Short"), "\u504f\u4f4e") && Has((string)Get(h1, "Short"), "2.0"), "level=" + lvl(h1) + " " + Esc((string)Get(h1, "Short")));
            h1 = withTokens(0);                   // est 0 -> ratio 0 -> 1/max(0, 0.01) = 100x
            Check("self-check: zero estimated usage -> level 3 with a finite '100x', no NaN / Infinity / division by zero", lvl(h1) == 3 && !BadNumberText((string)Get(h1, "Short")) && !BadNumberText((string)Get(h1, "Line")) && !BadNumberText((string)Get(h1, "Detail")), Esc((string)Get(h1, "Short")));
            h1 = withTokens(long.MaxValue / 1000000);
            Check("self-check: absurdly large usage -> no exception, no NaN / Infinity text", !BadNumberText((string)Get(h1, "Short")) && !BadNumberText((string)Get(h1, "Detail")), Esc((string)Get(h1, "Short")));
            // plan-derived table: the detail says the coefficients are guessed
            Set(w, "quota", MakeQuota(true, false));
            h1 = withTokens(1000000);
            Check("self-check: plan-derived coefficients are mentioned in the explanation", Has((string)Get(h1, "Detail"), "\u63a8\u7b97") && Has((string)Get(h1, "Line"), "\u7cfb\u6570\u662f\u63a8\u7b97\u7684"), Esc((string)Get(h1, "Line")));
            Set(w, "quota", MakeQuota(false, false));
            // the 5h reading went down (window reset) between the samples: no negative 'actual'
            hist.Clear();
            hist.Add(NewFivePoint(now.AddMinutes(-60), 90, reset));
            h1 = withTokens(1000000);
            Check("self-check: reading went down inside the 'same' window -> not used (level 0), no division by a negative number", lvl(h1) == 0);
        }

        // ================================================================== H Jump.cs (pure parts only)
        private static SessionJump.Win W(string title)
        {
            SessionJump.Win w = new SessionJump.Win();
            w.Title = title;
            w.Hwnd = new IntPtr(1000 + title.Length);
            return w;
        }

        private static void TestJump()
        {
            List<SessionJump.Win> wins = new List<SessionJump.Win>();
            wins.Add(W("other - Visual Studio Code"));
            wins.Add(W("main.cs - my-project - Visual Studio Code"));
            wins.Add(W("\u9879\u76ee\u4e00 - Visual Studio Code"));
            Func<List<SessionJump.Win>, string, string, int> pick = delegate(List<SessionJump.Win> ws, string cwd, string proj)
            {
                SessionJump.Win p = SessionJump.PickWindow(ws, cwd, proj);
                return p == null ? -1 : ws.IndexOf(p);
            };
            Check("PickWindow: cwd folder name found in a window title", pick(wins, "C:\\Users\\me\\code\\my-project", null) == 1);
            Check("PickWindow: case-insensitive", pick(wins, "C:\\Users\\me\\code\\MY-Project", null) == 1);
            Check("PickWindow: forward slashes and a trailing slash", pick(wins, "C:/Users/me/code/my-project/", null) == 1 && pick(wins, "C:\\Users\\me\\code\\my-project\\", null) == 1);
            Check("PickWindow: CJK folder name", pick(wins, "D:\\\u5de5\u4f5c\\\u9879\u76ee\u4e00", null) == 2);
            Check("PickWindow: session started in a sub folder -> matches the workspace folder one level up", pick(wins, "C:\\Users\\me\\code\\my-project\\src\\deep", null) == 1);
            Check("PickWindow: only 4 levels are tried (a match 5 levels up is not found)", pick(wins, "C:\\my-project\\a\\b\\c\\d", null) == -1);
            Check("PickWindow: deepest folder wins over a parent that also matches another window", pick(new List<SessionJump.Win> { W("parent - Visual Studio Code"), W("child - Visual Studio Code") }, "C:\\x\\parent\\child", null) == 1);
            Check("PickWindow: projectName argument has priority over the cwd folders", pick(wins, "C:\\Users\\me\\code\\other", "my-project") == 1);
            Check("PickWindow: projectName alone (no cwd)", pick(wins, "", "my-project") == 1 && pick(wins, null, "my-project") == 1);
            Check("PickWindow: no match -> null", pick(wins, "C:\\zzz1\\zzz2\\zzz3", null) == -1);
            Check("PickWindow: empty window list / null cwd and project -> null", pick(new List<SessionJump.Win>(), "C:\\a\\b", "c") == -1 && pick(wins, null, null) == -1 && pick(wins, "", "") == -1);
            Check("PickWindow: cwd 'C:\\' or 'C:' has no usable folder -> null", pick(wins, "C:\\", null) == -1 && pick(wins, "C:", null) == -1 && pick(wins, "\\", null) == -1);
            Check("PickWindow: UNC path (\\\\server\\share\\my-project)", pick(wins, "\\\\server\\share\\my-project", null) == 1);
            Check("PickWindow: drive-qualified odd names ('C:foo') are skipped, not matched", pick(new List<SessionJump.Win> { W("C:foo - Visual Studio Code") }, "C:foo", null) == -1);
            Check("PickWindow: window with empty title is never picked", pick(new List<SessionJump.Win> { W(""), W("x") }, "C:\\x", null) == 1);

            KnownBug("BUG-L8", "PickWindow: folder 'app' picks the 'app' window, not 'my-app-server' (substring collision)",
                pick(new List<SessionJump.Win> { W("my-app-server - Visual Studio Code"), W("app - Visual Studio Code") }, "C:\\x\\app", null) == 1,
                "picked index " + pick(new List<SessionJump.Win> { W("my-app-server - Visual Studio Code"), W("app - Visual Studio Code") }, "C:\\x\\app", null) + " of 2");
            KnownBug("BUG-L8", "PickWindow: a folder called 'code' (or 'studio', 'visual') does not match every VS Code window through the title suffix",
                pick(new List<SessionJump.Win> { W("alpha - Visual Studio Code"), W("beta - Visual Studio Code") }, "C:\\Users\\me\\code", null) == -1,
                "picked index " + pick(new List<SessionJump.Win> { W("alpha - Visual Studio Code"), W("beta - Visual Studio Code") }, "C:\\Users\\me\\code", null));

            string src = ReadSource("Jump.cs");
            Check("[source check] the session id goes through Uri.EscapeDataString into the vscode:// URI",
                src != null && Has(src, "\"vscode://anthropic.claude-code/open?session=\" + Uri.EscapeDataString(sessionId)"), src == null ? "Jump.cs not found" : "");
            Check("[framework] Uri.EscapeDataString neutralises '&', '=', '#', '/', spaces and CR/LF in a hostile session id",
                Uri.EscapeDataString("a&b=c#d/e f\r\ng") == "a%26b%3Dc%23d%2Fe%20f%0D%0Ag", Uri.EscapeDataString("a&b=c#d/e f\r\ng"));
            Note("SessionJump.Go (window focus + Process.Start of the vscode:// URI) and VsCodeWindows (EnumWindows) are NOT tested: they touch real windows / launch a process");
        }

        // ================================================================== I Exports
        private static string CsvQ(string s)
        {
            return (string)CallStatic(typeof(Widget), "CsvQ", new object[] { s });
        }

        // a small RFC 4180 reader (Excel-style): quoted cells may contain commas, doubled quotes and line breaks
        private static List<string[]> ParseRfc4180(string text)
        {
            List<string[]> rows = new List<string[]>();
            List<string> cur = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cur.Add(cell.ToString()); cell.Length = 0; }
                else if (c == '\r') { }
                else if (c == '\n') { cur.Add(cell.ToString()); cell.Length = 0; rows.Add(cur.ToArray()); cur = new List<string>(); }
                else cell.Append(c);
            }
            if (cell.Length > 0 || cur.Count > 0) { cur.Add(cell.ToString()); rows.Add(cur.ToArray()); }
            return rows;
        }

        private static string[] TrickyCells()
        {
            return new string[]
            {
                "plain", "a,b", "He said \"hi\"", "\"", "\"\"", "line1\nline2", "line1\r\nline2", "cr\rhere", ",", ",,,", "\u4e2d\u6587,\u6807\u9898 \"\u5f15\u53f7\"", "\ud83d\ude00 emoji, \"q\"",
                " leading and trailing ", "", "tab\there", "semi;colon", "a\"b,c\"d", "ends with quote\"", new string('x', 40000)
            };
        }

        private static void TestExports()
        {
            // ---- CsvQ
            Check("CsvQ: plain text is not quoted", CsvQ("plain") == "plain" && CsvQ("\u4e2d\u6587") == "\u4e2d\u6587");
            Check("CsvQ: null / '' -> ''", CsvQ(null) == "" && CsvQ("") == "");
            Check("CsvQ: comma -> quoted", CsvQ("a,b") == "\"a,b\"");
            Check("CsvQ: quote -> quoted with doubled quote", CsvQ("He said \"hi\"") == "\"He said \"\"hi\"\"\"");
            Check("CsvQ: LF / CR / CRLF -> quoted", CsvQ("a\nb") == "\"a\nb\"" && CsvQ("a\rb") == "\"a\rb\"" && CsvQ("a\r\nb") == "\"a\r\nb\"");
            Check("CsvQ: CJK and emoji with a comma are kept intact inside the quotes", CsvQ("\u4e2d\u6587,\u6807\u9898 \ud83d\ude00") == "\"\u4e2d\u6587,\u6807\u9898 \ud83d\ude00\"");
            string[] cells = TrickyCells();
            StringBuilder csv = new StringBuilder();
            List<string> q = new List<string>();
            foreach (string c in cells) q.Add(CsvQ(c));
            csv.Append(string.Join(",", q.ToArray())).Append("\r\n").Append(string.Join(",", q.ToArray())).Append("\r\n");
            List<string[]> back = ParseRfc4180(csv.ToString());
            bool same = back.Count == 2 && back[0].Length == cells.Length && back[1].Length == cells.Length;
            for (int i = 0; same && i < cells.Length; i++) same = back[0][i] == cells[i] && back[1][i] == cells[i];
            Check("CsvQ -> join -> RFC 4180 parse gives back all " + cells.Length + " tricky cells (commas, quotes, CR/LF, CJK, emoji, 40 000 chars) exactly", same);
            // the product's own reader (LogSummary.SplitCsv, used for the history files) on single-line cells
            bool ownOk = true;
            foreach (string c in cells)
            {
                if (c.IndexOf('\n') >= 0 || c.IndexOf('\r') >= 0) continue;
                string[] one = LogSummary.SplitCsv(CsvQ("x") + "," + CsvQ(c) + "," + CsvQ("y"));
                if (one.Length != 3 || one[1] != c) ownOk = false;
            }
            Check("the product's own CSV reader (LogSummary.SplitCsv) reads CsvQ output back exactly (single-line cells)", ownOk);

            // formula injection (cells that Excel / LibreOffice would evaluate)
            string[] formulas = { "=1+1", "+1+1", "-1+1", "@SUM(A1:A2)", "=HYPERLINK(\"http://example.invalid\",\"x\")", "\t=1+1", "\r=1+1", "=cmd|' /C calc'!A0" };
            int unsafeCount = 0;
            string firstUnsafe = null;
            foreach (string f in formulas)
            {
                string cellText = ParseRfc4180(CsvQ(f) + "\r\n")[0][0];
                char c0 = cellText.Length > 0 ? cellText[0] : 'x';
                if (c0 == '=' || c0 == '+' || c0 == '-' || c0 == '@' || c0 == '\t' || c0 == '\r')
                {
                    unsafeCount++;
                    if (firstUnsafe == null) firstUnsafe = f;
                }
            }
            KnownBug("BUG-M1", "CSV export neutralises cells that start with = + - @ TAB CR (formula injection through session titles)", unsafeCount == 0, unsafeCount + " of " + formulas.Length + " formula-looking titles are written unchanged, e.g. " + Esc(firstUnsafe));

            // ---- ExportTable on a live widget with planted rows
            string home = NewHome("export");
            Widget w = NewWidget(home, null);
            Set(w, "quota", MakeQuota(false, false));
            Set(w, "quotaReadUtc", DateTime.UtcNow);
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            UsageTracker.PeriodSummary per = snap.Periods[0];       // tab 1 = 15 minutes
            string[] titles = { "a,b", "say \"x\"", "multi\nline title", "\u4e2d\u6587\u6807\u9898, \u5e26\u9017\u53f7 \ud83d\ude00", "=HYPERLINK(\"http://example.invalid\",\"x\")", null, "+1", "tab\there" };
            for (int i = 0; i < titles.Length; i++)
            {
                UsageTracker.SessionTotal st = new UsageTracker.SessionTotal();
                st.Session = "00000000-0000-4000-8000-00000000000" + i;
                st.Name = "proj";
                st.Title = titles[i];
                st.Input = 100 + i;
                st.Output = 2000 + i;
                st.CacheWrite = 3000;
                st.CacheRead = 40000;
                st.Total = st.Input + st.Output + st.CacheWrite + st.CacheRead;
                st.LastUtc = DateTime.UtcNow.AddMinutes(-i);
                per.Rows.Add(st);
                UsageTracker.ModelTotal mt = new UsageTracker.ModelTotal();
                mt.Session = st.Session;
                mt.Model = "claude-sonnet-5-5";
                mt.Tier = "high";
                mt.Msgs = 3;
                mt.Think = 7;
                mt.Total = st.Total;
                mt.Tok[0] = st.Input;
                mt.Tok[1] = st.Output;
                mt.Tok[2] = 1000;
                mt.Tok[3] = 2000;
                mt.Tok[4] = st.CacheRead;
                per.ModelRows.Add(mt);
            }
            List<string[]> t0 = (List<string[]>)Call(w, "ExportTable", 0);
            Check("ExportTable(sessions): header + " + titles.Length + " rows, 14 columns each", t0.Count == titles.Length + 1 && AllLen(t0, 14), t0.Count + " rows");
            bool titlesOk = true;
            for (int i = 0; i < titles.Length; i++)
            {
                string exp = titles[i] != null ? titles[i] : "proj \u00b7 000000";
                if (t0[i + 1][0] != exp) { titlesOk = false; Emit("      row " + i + ": got '" + Esc(t0[i + 1][0]) + "' expected '" + Esc(exp) + "'"); }
            }
            Check("ExportTable(sessions): titles are exported verbatim (comma, quotes, line break, CJK, emoji, '=', '+', tab); missing title = 'project \u00b7 shortid'", titlesOk);
            Check("ExportTable(sessions): session id column and token columns", t0[1][1] == "00000000-0000-4000-8000-000000000000" && t0[1][8] == "100" && t0[1][9] == "2000" && t0[1][10] == "3000" && t0[1][11] == "40000", string.Join("|", t0[1]));
            Check("ExportTable(sessions): numbers use '.' and no thousands separators, regardless of culture", !Regex.IsMatch(string.Join("|", t0[1]), "\\d,\\d"));
            List<string[]> t1 = (List<string[]>)Call(w, "ExportTable", 1);
            Check("ExportTable(model x tier): header + " + titles.Length + " rows, 13 columns", t1.Count == titles.Length + 1 && AllLen(t1, 13));
            Check("ExportTable(model x tier): model, tier, message count, think tokens", t1[1][2] == "claude-sonnet-5-5" && t1[1][3] == "high" && t1[1][4] == "3" && t1[1][5] == "7", string.Join("|", t1[1]));
            Check("ExportTable(model x tier): title column = the session's title", t1[1][0] == "a,b" && t1[4][0] == titles[3]);
            // whole table -> CSV text (same code as the Save-as button) -> parse -> identical
            StringBuilder sb = new StringBuilder();
            foreach (string[] row in t0)
            {
                List<string> cs = new List<string>();
                foreach (string c in row) cs.Add(CsvQ(c));
                sb.Append(string.Join(",", cs.ToArray())).Append("\r\n");
            }
            List<string[]> parsed = ParseRfc4180(sb.ToString());
            bool tableSame = parsed.Count == t0.Count;
            string firstDiff = "rows " + parsed.Count + " vs " + t0.Count;
            for (int i = 0; tableSame && i < t0.Count; i++)
            {
                tableSame = parsed[i].Length == t0[i].Length;
                if (!tableSame) firstDiff = "row " + i + " has " + parsed[i].Length + " cells, expected " + t0[i].Length;
                for (int j = 0; tableSame && j < t0[i].Length; j++)
                {
                    string orig = t0[i][j] ?? "";
                    tableSame = parsed[i][j] == orig || parsed[i][j] == "'" + orig;       // a formula-looking cell is written with a leading apostrophe
                    if (!tableSame) firstDiff = "row " + i + " col " + j + ": got '" + Esc(parsed[i][j]) + "' expected '" + Esc(orig) + "'";
                }
            }
            Check("whole session table -> CSV text -> parse gives the same table (multi-line title survives inside quotes; formula-looking cells only differ by a leading apostrophe)", tableSame, firstDiff);
            Check("... the '=HYPERLINK(...)' title is the only cell written with an apostrophe (numbers such as +1 / -5 are left alone)",
                parsed.Count == t0.Count && parsed[5][0] == "'" + titles[4] && parsed[7][0] == "+1" && CsvQ("-5") == "-5" && CsvQ("-1.5e3") == "-1.5e3" && CsvQ("0") == "0");
            Note("the clipboard export (tab-separated, Clipboard.SetText) and the Save-as dialog are inline in ExportData and need a clipboard / dialog: NOT tested");
        }

        private static bool AllLen(List<string[]> t, int n)
        {
            foreach (string[] r in t) if (r.Length != n) return false;
            return true;
        }

        // ================================================================== J sorting
        private static void TestSorting()
        {
            string home = NewHome("sort");
            Widget w = NewWidget(home, null);
            int[] sc = G<int[]>(w, "sortCol");
            bool[] sd = G<bool[]>(w, "sortDesc");
            Func<int, int, string> state = delegate(int m, int dummy) { return sc[m] + (sd[m] ? "d" : "a"); };

            Set(w, "usageMode", 0);
            Check("session view: initial state is 'default order' (-1) descending", state(0, 0) == "-1d");
            Call(w, "ToggleSort", 2);
            Check("click 'speed' header: sorted by column 2, descending first", state(0, 0) == "2d", state(0, 0));
            Call(w, "ToggleSort", 2);
            Check("click again: ascending", state(0, 0) == "2a", state(0, 0));
            Call(w, "ToggleSort", 2);
            Check("click a third time: back to the default order", state(0, 0) == "-1d", state(0, 0));
            Call(w, "ToggleSort", 2);
            Call(w, "ToggleSort", 2);
            Call(w, "ToggleSort", 5);
            Check("click another column while ascending: new column, descending first again", state(0, 0) == "5d", state(0, 0));
            Check("sort state is per view: models / compare untouched", sc[1] == -1 && sc[2] == -1);

            Set(w, "usageMode", 2);
            Call(w, "ToggleSort", 0);
            Check("compare view: the name column sorts A->Z first (ascending), not descending", state(2, 0) == "0a", state(2, 0));
            Call(w, "ToggleSort", 0);
            Check("compare view: second click on the name column = Z->A", state(2, 0) == "0d", state(2, 0));
            Call(w, "ToggleSort", 0);
            Check("compare view: third click resets", state(2, 0) == "-1d", state(2, 0));
            Call(w, "ToggleSort", 3);
            Check("compare view: a numeric column starts descending", state(2, 0) == "3d");

            Set(w, "usageMode", 1);
            Call(w, "ToggleSort", 4);
            Call(w, "ToggleSort", 4);
            Check("model view: two clicks = ascending on column 4", state(1, 0) == "4a");

            foreach (int mode in new int[] { 3, 4, 5 })
            {
                Set(w, "usageMode", mode);
                string before = state(0, 0) + state(1, 0) + state(2, 0);
                Call(w, "ToggleSort", 1);
                Check("view " + mode + " (plan / API / trend) has no sorting: ToggleSort changes nothing", before == state(0, 0) + state(1, 0) + state(2, 0));
            }

            // persistence: the three states are saved as sortN = (col+1)*2 + (desc ? 1 : 0)
            Set(w, "usageMode", 0);
            Call(w, "SaveSettings");
            Dictionary<string, string> kv = ReadKv(Path.Combine(home, "settings.txt"));
            Check("settings.txt: sort0 / sort1 / sort2 = (5+1)*2+1 / (4+1)*2+0 / (3+1)*2+1 = 13 / 10 / 9", KvGet(kv, "sort0") == "13" && KvGet(kv, "sort1") == "10" && KvGet(kv, "sort2") == "9",
                KvGet(kv, "sort0") + "/" + KvGet(kv, "sort1") + "/" + KvGet(kv, "sort2"));
            Widget w2 = new Widget();
            live.Add(w2);
            int[] sc2 = G<int[]>(w2, "sortCol");
            bool[] sd2 = G<bool[]>(w2, "sortDesc");
            Check("reload: sort columns and directions come back exactly", sc2[0] == 5 && sd2[0] && sc2[1] == 4 && !sd2[1] && sc2[2] == 3 && sd2[2], sc2[0] + "," + sc2[1] + "," + sc2[2]);
            // every (column, direction) pair of every view round-trips
            bool allOk = true;
            for (int m = 0; m < 3; m++)
            {
                for (int col = -1; col <= 7; col++)
                {
                    foreach (bool desc in new bool[] { true, false })
                    {
                        if (col == -1 && !desc) continue;
                        Widget wx = NewWidget(home, null);
                        G<int[]>(wx, "sortCol")[m] = col;
                        G<bool[]>(wx, "sortDesc")[m] = desc;
                        Call(wx, "SaveSettings");
                        Widget wy = new Widget();
                        live.Add(wy);
                        if (G<int[]>(wy, "sortCol")[m] != col || (col >= 0 && G<bool[]>(wy, "sortDesc")[m] != desc)) allOk = false;
                    }
                }
            }
            Check("round trip of every column (-1..7) x direction x view through settings.txt", allOk);
            Check("no-sort state saved as sortN=0 reloads as 'default order'", Try(delegate
            {
                Widget wz = NewWidget(home, new string[] { "sv=2", "sort0=0", "sort1=1", "sort2=0" });
                if (G<int[]>(wz, "sortCol")[0] != -1 || G<int[]>(wz, "sortCol")[1] != -1) throw new Exception("not default");
            }));
            // invalid values
            Widget wi = NewWidget(home, new string[] { "sv=2", "sort0=-1", "sort1=abc", "sort2=-99999", "sort3=5", "sort9=5", "sort=5", "SORT0=9" });
            Check("invalid sort values (negative, text, unknown view numbers, wrong case) are ignored", G<int[]>(wi, "sortCol")[0] == -1 && G<int[]>(wi, "sortCol")[1] == -1 && G<int[]>(wi, "sortCol")[2] == -1);
            Widget wj = NewWidget(home, new string[] { "sv=2", "sort0=99999999" });
            Check("a huge sort value is loaded without throwing (see BUG-L1 for the range)", Try(delegate { Call(wj, "ApplySessionSort", new List<UsageTracker.SessionTotal>()); }));

            // ---- SortStable
            MethodInfo ss = typeof(Widget).GetMethod("SortStable", Stat).MakeGenericMethod(typeof(int));
            Func<int[], double?[], bool, int[]> sort = delegate(int[] items, double?[] keys, bool desc)
            {
                List<int> list = new List<int>(items);
                Func<int, double?> key = delegate(int i) { return keys[i]; };
                ss.Invoke(null, new object[] { list, key, desc });
                return list.ToArray();
            };
            double?[] k = { 3, 1, 2, 1, null, 3, null };
            int[] ids = { 0, 1, 2, 3, 4, 5, 6 };
            Check("SortStable descending: highest first, ties keep their order, empty values last (original order)", Same(sort(ids, k, true), new int[] { 0, 5, 2, 1, 3, 4, 6 }), string.Join(",", Array.ConvertAll<int, string>(sort(ids, k, true), delegate(int i) { return i.ToString(); })));
            Check("SortStable ascending: lowest first, ties keep their order, empty values STILL last", Same(sort(ids, k, false), new int[] { 1, 3, 2, 0, 5, 4, 6 }), string.Join(",", Array.ConvertAll<int, string>(sort(ids, k, false), delegate(int i) { return i.ToString(); })));
            Check("SortStable: all keys empty -> original order; single / empty list fine", Same(sort(new int[] { 2, 0, 1 }, new double?[] { null, null, null }, true), new int[] { 2, 0, 1 }) && sort(new int[0], new double?[0], true).Length == 0 && sort(new int[] { 0 }, new double?[] { 1 }, false).Length == 1);
            Check("SortStable: NaN / Infinity keys do not make the comparer inconsistent (no exception)", Try(delegate { sort(new int[] { 0, 1, 2, 3, 4, 5 }, new double?[] { double.NaN, 1, double.PositiveInfinity, double.NaN, double.NegativeInfinity, 0 }, true); }));
            bool bigOk = true;
            Random rnd = new Random(5);
            int[] big = new int[2000];
            double?[] bk = new double?[2000];
            for (int i = 0; i < big.Length; i++) { big[i] = i; bk[i] = rnd.Next(20) == 0 ? (double?)null : rnd.Next(30); }
            int[] sorted = sort(big, bk, true);
            for (int i = 1; i < sorted.Length; i++)
            {
                double? a = bk[sorted[i - 1]], b = bk[sorted[i]];
                if (!a.HasValue && b.HasValue) bigOk = false;
                else if (a.HasValue && b.HasValue && (a.Value < b.Value || (a.Value == b.Value && sorted[i - 1] > sorted[i]))) bigOk = false;
                else if (!a.HasValue && !b.HasValue && sorted[i - 1] > sorted[i]) bigOk = false;
            }
            Check("SortStable: 2000 random rows (with 5 % empty, many ties) are ordered and stable", bigOk);

            // ---- ApplySessionSort on real rows
            Widget ws = NewWidget(home, null);
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(ws, "usageSnap");
            List<UsageTracker.SessionTotal> rows = new List<UsageTracker.SessionTotal>();
            for (int i = 0; i < 5; i++)
            {
                UsageTracker.SessionTotal st = new UsageTracker.SessionTotal();
                st.Session = "sess-" + i;
                st.Name = "n";
                st.Input = new long[] { 50, 10, 30, 20, 40 }[i];
                st.Output = new long[] { 5, 4, 3, 2, 1 }[i];
                st.CacheWrite = 7;
                st.CacheRead = new long[] { 100, 500, 300, 200, 400 }[i];
                st.LastUtc = DateTime.UtcNow.AddMinutes(-new int[] { 30, 10, 50, 20, 40 }[i]);
                rows.Add(st);
            }
            Func<int, bool, string> order = delegate(int col, bool desc)
            {
                G<int[]>(ws, "sortCol")[0] = col;
                G<bool[]>(ws, "sortDesc")[0] = desc;
                List<UsageTracker.SessionTotal> copy = new List<UsageTracker.SessionTotal>(rows);
                Call(ws, "ApplySessionSort", copy);
                StringBuilder s = new StringBuilder();
                foreach (UsageTracker.SessionTotal t in copy) s.Append(t.Session.Substring(5));
                return s.ToString();
            };
            Check("ApplySessionSort: by time (newest first): 1,3,0,4,2", order(0, true) == "13042", order(0, true));
            Check("ApplySessionSort: by input descending: 0,4,2,3,1", order(4, true) == "04231", order(4, true));
            Check("ApplySessionSort: by output ascending: 4,3,2,1,0", order(5, false) == "43210", order(5, false));
            Check("ApplySessionSort: by cache write (all equal): original order kept in both directions", order(6, true) == "01234" && order(6, false) == "01234");
            Check("ApplySessionSort: by cache read descending: 1,4,2,3,0", order(7, true) == "14230", order(7, true));
            Check("ApplySessionSort: out-of-range column (99) leaves the order alone", order(99, true) == "01234" && order(-1, true) == "01234");
            Check("ApplySessionSort: speed / occupancy columns without a computed speed table do not throw", Try(delegate { order(2, true); order(3, false); order(1, true); }));
        }

        // ================================================================== K other cheap checks
        private static void TestMisc()
        {
            // update checker
            Check("IsNewer: 0.2.0 > 0.1.1; equal / older are not newer", UpdateChecker.IsNewer("0.2.0", "0.1.1") && !UpdateChecker.IsNewer("0.1.1", "0.1.1") && !UpdateChecker.IsNewer("0.1.0", "0.1.1"));
            Check("IsNewer: numeric, not alphabetical (0.10.0 > 0.9.0; 1.0 == 1.0.0)", UpdateChecker.IsNewer("0.10.0", "0.9.0") && !UpdateChecker.IsNewer("1.0", "1.0.0") && !UpdateChecker.IsNewer("1.0.0", "1.0") && UpdateChecker.IsNewer("1.0.1", "1.0"));
            Check("IsNewer: 'v' prefix and pre-release suffix ('v0.2.1-beta.1' counts as 0.2.1)", UpdateChecker.IsNewer("v0.2.1-beta.1", "0.2.0") && UpdateChecker.CleanVersion("v0.2.1-beta.1") == "0.2.1");
            Check("IsNewer: garbage / empty / null / overflow never claims 'newer'", !UpdateChecker.IsNewer("latest", "0.1.1") && !UpdateChecker.IsNewer("", "0.1.1") && !UpdateChecker.IsNewer(null, "0.1.1") && !UpdateChecker.IsNewer("99999999999999999999.0", "0.1.1") && !UpdateChecker.IsNewer("0.2.0", "garbage"));
            Check("AppInfo.Version is a parsable version newer than 0.1.1 (the release being tested)", UpdateChecker.Parse(AppInfo.Version) != null && UpdateChecker.IsNewer(AppInfo.Version, "0.1.1"), AppInfo.Version);
            Check("CleanVersion: 'V1.2.3', ' 1.2 ', '1.2.3.4', '1..2' ", UpdateChecker.CleanVersion("V1.2.3") == "1.2.3" && UpdateChecker.CleanVersion(" 1.2 ") == "1.2" && UpdateChecker.CleanVersion("1.2.3.4") == "1.2.3.4" && UpdateChecker.CleanVersion("1..2") == "1");
            Check("SafeUrl: only https://github.com/... is opened", UpdateChecker.SafeUrl("https://github.com/jly782676356-byte/Stilla/releases/tag/v0.2.0") && !UpdateChecker.SafeUrl("https://github.com.evil.example/x") && !UpdateChecker.SafeUrl("https://github.com@evil.example/x")
                && !UpdateChecker.SafeUrl("http://github.com/x") && !UpdateChecker.SafeUrl("javascript:alert(1)") && !UpdateChecker.SafeUrl("file:///c:/windows/system32/calc.exe") && !UpdateChecker.SafeUrl("") && !UpdateChecker.SafeUrl(null) && !UpdateChecker.SafeUrl("\\\\evil\\share"));
            Check("SafeUrl: localhost is refused unless the test variable STILLA_UPDATE_URL is set", !UpdateChecker.SafeUrl("http://localhost:1/x"));
            Check("update check is off in this test run (STILLA_NO_UPDATE_CHECK / demo): no network", !UpdateChecker.Enabled());

            // hover-tip tables and the hit test
            Check("TipText: one text per time tab (11) and per view (6), none empty, each with a title line and a body", TipText.Tab.Length == 11 && TipText.View.Length == Feat.ViewCount && AllTips());
            string home = NewHome("tips");
            Widget w = NewWidget(home, null);
            Type tw = typeof(Widget);
            MethodInfo add = tw.GetMethod("AddTip", Inst), at = tw.GetMethod("TipAt", Inst);
            Func<object, string> idOf = delegate(object hit) { return hit == null ? null : (string)hit.GetType().GetField("Id", Inst).GetValue(hit); };
            add.Invoke(w, new object[] { "big", new RectangleF(0, 0, 100, 100), "big", null, 0 });
            add.Invoke(w, new object[] { "small", new RectangleF(40, 40, 20, 20), "small", null, 0 });
            add.Invoke(w, new object[] { "prio", new RectangleF(0, 0, 100, 100), "prio", null, 5 });
            Check("hover tips: among overlapping areas the higher priority wins, then the smaller area", idOf(at.Invoke(w, new object[] { new Point(50, 50) })) == "prio" && idOf(at.Invoke(w, new object[] { new Point(5, 5) })) == "prio");
            ((System.Collections.IList)Get(w, "tipHits")).Clear();
            add.Invoke(w, new object[] { "big", new RectangleF(0, 0, 100, 100), "big", null, 0 });
            add.Invoke(w, new object[] { "small", new RectangleF(40, 40, 20, 20), "small", null, 0 });
            Check("hover tips: same priority -> the smaller (more specific) area wins; outside everything -> none",
                idOf(at.Invoke(w, new object[] { new Point(50, 50) })) == "small" && idOf(at.Invoke(w, new object[] { new Point(10, 10) })) == "big" && at.Invoke(w, new object[] { new Point(500, 500) }) == null);
            ((System.Collections.IList)Get(w, "tipHits")).Clear();
            add.Invoke(w, new object[] { "zero", new RectangleF(10, 10, 0, 5), "z", null, 0 });
            Check("hover tips: an empty rectangle registers nothing", ((System.Collections.IList)Get(w, "tipHits")).Count == 0);
            Set(w, "tipsOn", false);
            add.Invoke(w, new object[] { "off", new RectangleF(0, 0, 50, 50), "t", null, 0 });
            Check("hover tips: switched off -> nothing is registered", ((System.Collections.IList)Get(w, "tipHits")).Count == 0);

            // counts that must stay in step
            FieldInfo tc = typeof(Widget).GetField("TabCount", Stat);
            Check("TabCount (11) == TabLabels.Length == tab preset masks range", (int)tc.GetRawConstantValue() == 11 && ((string[])typeof(Widget).GetField("TabLabels", Stat).GetValue(null)).Length == 11
                && ((int[])typeof(Widget).GetField("PresetMasks", Stat).GetValue(null))[4] == 2047);
            int[] presets = (int[])typeof(Widget).GetField("PresetMasks", Stat).GetValue(null);
            bool presetsOk = true;
            foreach (int m in presets) if (m <= 0 || m >= 2048) presetsOk = false;
            Check("time-tab presets are all valid masks (1..2047) and the default 1258 is among them", presetsOk && Array.IndexOf(presets, 1258) >= 0);
            Check("sessions per view: SpeedSpan / SpeedLabel text (5, 60, 90, 1440 min)",
                WS("SpeedSpan", 5) == "5 \u5206\u949f" && WS("SpeedSpan", 60) == "1 \u5c0f\u65f6" && WS("SpeedSpan", 90) == "1.5 \u5c0f\u65f6" && WS("SpeedSpan", 1440) == "24 \u5c0f\u65f6"
                && WS("SpeedLabel", 5) == "5m" && WS("SpeedLabel", 60) == "1h" && WS("SpeedLabel", 90) == "1.5h" && WS("SpeedLabel", 1440) == "24h");
            Check("UsageTracker.FormatTokens: 999, 1000, 99999, 100000, 1e6, 1e9, 0, negative", UsageTracker.FormatTokens(999) == "999" && UsageTracker.FormatTokens(1000) == "1.0k" && UsageTracker.FormatTokens(0) == "0"
                && UsageTracker.FormatTokens(100000) == "100k" && !BadNumberText(UsageTracker.FormatTokens(long.MaxValue)) && !BadNumberText(UsageTracker.FormatTokens(long.MinValue)),
                UsageTracker.FormatTokens(1000) + "|" + UsageTracker.FormatTokens(100000) + "|" + UsageTracker.FormatTokens(1000000));

            // QuotaTable.Load robustness: NaN / Infinity / garbage rows in 额度换算.csv
            string qh = NewHome("quotafile");
            UseHome(qh);
            string qp = Path.Combine(qh, "q.csv");
            List<string> ql = new List<string>();
            ql.Add(QuotaHeaderLine);
            ql.Add(QuotaGlobalRow(false, ""));
            ql.Add(QuotaModelRow("claude-sonnet-5-5", 3, 15, 3.75, 6, 0.3, 2, 0.5, "\u5b9e\u6d4b", ""));
            ql.Add(QuotaModelRow("nan-model", 3, 15, 3.75, 6, 0.3, 2, 0.5, "\u5b9e\u6d4b", "").Replace(",2,", ",NaN,"));
            ql.Add(QuotaModelRow("inf-model", 3, 15, 3.75, 6, 0.3, 2, 0.5, "\u5b9e\u6d4b", "").Replace(",2,", ",Infinity,"));
            ql.Add(QuotaModelRow("negative-price", -3, 15, 3.75, 6, 0.3, 2, 0.5, "\u5b9e\u6d4b", ""));
            ql.Add("garbage,row");
            ql.Add("\u6a21\u578b,short,1,2,3");
            ql.Add("\u6a21\u578b,bad-price,x,y,z,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18");
            File.WriteAllLines(qp, ql.ToArray(), new UTF8Encoding(true));
            QuotaTable qt = QuotaTable.Load(qp, "");
            Check("QuotaTable.Load: garbage / short / non-numeric rows are skipped, good rows kept", qt != null && qt.Models.ContainsKey("claude-sonnet-5-5") && !qt.Models.ContainsKey("bad-price") && !qt.Models.ContainsKey("short"));
            KnownBug("BUG-L9", "QuotaTable.Load rejects NaN / Infinity coefficients and negative prices (they would show up as 'NaN' in the speed columns)",
                qt != null && (!qt.Models.ContainsKey("nan-model") || !double.IsNaN(qt.Models["nan-model"].K5)) && (!qt.Models.ContainsKey("inf-model") || !double.IsInfinity(qt.Models["inf-model"].K5))
                && (!qt.Models.ContainsKey("negative-price") || qt.Models["negative-price"].Price[0] >= 0),
                "nan-model K5=" + (qt != null && qt.Models.ContainsKey("nan-model") ? qt.Models["nan-model"].K5.ToString(CultureInfo.InvariantCulture) : "(rejected)")
                + " inf-model K5=" + (qt != null && qt.Models.ContainsKey("inf-model") ? qt.Models["inf-model"].K5.ToString(CultureInfo.InvariantCulture) : "(rejected)")
                + " negative-price=" + (qt != null && qt.Models.ContainsKey("negative-price") ? qt.Models["negative-price"].Price[0].ToString(CultureInfo.InvariantCulture) : "(rejected)"));
            Check("QuotaTable.Load: missing file -> null (no exception)", QuotaTable.Load(Path.Combine(qh, "nope.csv"), "") == null);
            File.WriteAllText(Path.Combine(qh, "empty.csv"), "");
            Check("QuotaTable.Load: empty file / header only -> null", QuotaTable.Load(Path.Combine(qh, "empty.csv"), "") == null);
            string qp2 = Path.Combine(qh, "q2.csv");
            File.WriteAllLines(qp2, new string[] { QuotaHeaderLine, QuotaGlobalRow(false, "A:max5"), QuotaModelRow("claude-sonnet-5-5", 3, 15, 3.75, 6, 0.3, 2, 0.5, "实测", "A:max5") }, new UTF8Encoding(true));
            Check("QuotaTable.Load: asking for a key the file does not have -> 'Stale' table with prices but no coefficients",
                Try(delegate
                {
                    QuotaTable st = QuotaTable.Load(qp2, "Z:max20");
                    if (st == null || !st.Stale || st.Models["claude-sonnet-5-5"].K5 != 0 || st.Models["claude-sonnet-5-5"].Price[1] != 15) throw new Exception("not stale");
                }));
            Check("QuotaTable.Load: the right key -> not stale, coefficients present", Try(delegate
            {
                QuotaTable ok = QuotaTable.Load(qp2, "A:max5");
                if (ok == null || ok.Stale || ok.Models["claude-sonnet-5-5"].K5 != 2) throw new Exception("stale or wrong");
            }));

            // update.txt: only safe links, only parsable versions
            UseHome(NewHome("update"));
            UpdateState.Latest = "";
            UpdateState.Url = "";
            UpdateState.Notified = "";
            File.WriteAllLines(Path.Combine(AppPaths.Home, "update.txt"), new string[] { "latest=v9.9.9-beta.2", "url=https://evil.example/phish", "notified=0.1.0", "checked=abc" }, new UTF8Encoding(false));
            UpdateState.Load();
            Check("update.txt: version cleaned ('v9.9.9-beta.2' -> '9.9.9'), unsafe url ignored, red dot logic says 'update available'", UpdateState.Latest == "9.9.9" && UpdateState.Url == "" && UpdateState.Available && UpdateState.PageUrl == AppInfo.ReleasesUrl, UpdateState.Latest + " | " + UpdateState.Url);
            File.WriteAllLines(Path.Combine(AppPaths.Home, "update.txt"), new string[] { "latest=garbage", "url=javascript:alert(1)", "notified=" }, new UTF8Encoding(false));
            UpdateState.Latest = "";
            UpdateState.Load();
            Check("update.txt: garbage version -> '' (no red dot), 'javascript:' url ignored", UpdateState.Latest == "" && !UpdateState.Available && UpdateState.Url == "");
            UpdateState.Latest = AppInfo.Version;
            Check("the version we are running is never 'newer than itself' (no red dot after updating)", !UpdateState.Available);
            UpdateState.Latest = "";
        }

        private static bool AllTips()
        {
            foreach (string t in TipText.Tab) if (string.IsNullOrEmpty(t) || t.IndexOf('\n') <= 0) return false;
            foreach (string t in TipText.View) if (string.IsNullOrEmpty(t) || t.IndexOf('\n') <= 0) return false;
            return true;
        }

        // ================================================================== L render smoke (no pixels compared; just 'does drawing throw?')
        private static string firstRenderStack;

        private static bool RenderOnce(Widget w, int skin, bool dark, float scale, int mode, int period, FetchResult data, Problem? problem, string label)
        {
            try
            {
                Skin[] skins = (Skin[])typeof(Widget).GetField("Skins", Stat).GetValue(null);
                Set(w, "skinIndex", skin);
                Set(w, "skin", skins[skin]);
                Set(w, "inverted", dark);
                typeof(Widget).GetMethod("ApplyPalette", Stat).Invoke(null, new object[] { skins[skin], dark });
                Set(w, "showUsage", true);
                Set(w, "usageMode", mode);
                Set(w, "usagePeriod", period);
                Set(w, "data", data);
                if (problem.HasValue)
                {
                    FetchResult pr = new FetchResult();
                    pr.Problem = problem.Value;
                    Set(w, "problem", pr);
                }
                else
                {
                    Set(w, "problem", null);
                }
                Set(w, "scale", scale);
                Call(w, "CreateFonts");
                int cw = (int)Call(w, "CurW"), ch = (int)Call(w, "CurH");
                w.ClientSize = new Size((int)Math.Round(cw * scale), (int)Math.Round(ch * scale));
                Call(w, "Redraw");
                Bitmap surf = (Bitmap)Get(w, "surface");
                return surf != null && surf.Width > 100 && surf.Height > 100;
            }
            catch (Exception ex)
            {
                if (firstRenderStack == null)
                {
                    Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                    firstRenderStack = label + ": " + inner.GetType().Name + ": " + inner.Message + "  " + inner.StackTrace;
                }
                return false;
            }
        }

        private static void TestRenderSmoke()
        {
            string home = NewHome("render");
            UseHome(home);
            ResetStatics();
            string logs = Path.Combine(home, "logs");
            Directory.CreateDirectory(logs);
            // an account + quota table with the three demo models, so speed / occupancy / forecast columns have numbers
            File.WriteAllLines(Path.Combine(logs, "账号.csv"), new string[] { "编号,名称,计划,开始日期,月费,绑定组织,登录提示,备注", "A,demo,max5,2026-10-01,100,,," }, new UTF8Encoding(true));
            File.WriteAllLines(Path.Combine(logs, "账号时段.csv"), new string[] { "时间UTC,账号,计划,来源", "2026-01-01 00:00:00,\"A\",\"max5\",\"test\"" }, new UTF8Encoding(true));
            List<string> q = new List<string>();
            q.Add(QuotaHeaderLine);
            q.Add(QuotaGlobalRow(false, "A:max5"));
            q.Add(QuotaTierRow("high", 1.3, 1.2, "A:max5"));
            q.Add(QuotaModelRow("claude-sonnet-5-5", 3, 15, 3.75, 6, 0.3, 2.0, 0.5, "实测", "A:max5"));
            q.Add(QuotaModelRow("claude-opus-5-5", 15, 75, 18.75, 30, 1.5, 3.0, 0.8, "实测", "A:max5"));
            q.Add(QuotaModelRow("claude-haiku-5-5", 1, 5, 1.25, 2, 0.1, 1.0, 0.3, "实测", "A:max5"));
            File.WriteAllLines(Path.Combine(logs, "额度换算.csv"), q.ToArray(), new UTF8Encoding(true));
            Widget w = NewWidget(home, null);
            IntPtr handle = w.Handle;       // handle only, the window is never shown
            FetchResult normal = Quotas(58, 61, 71, 64);
            UsageTracker tracker = (UsageTracker)Get(w, "usage");
            DateTime? anchor = normal.FiveHour.ResetsAt.Value.ToUniversalTime();
            Set(w, "usageSnap", tracker.Take(anchor));
            Thread.Sleep(150);
            Set(w, "usageSnap", tracker.Take(anchor));
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            Check("fake Claude data produces live sessions for the render tests", !haveDemoData || snap.Live.Count > 0, "Live=" + snap.Live.Count);
            if (!haveDemoData) Note("no fake Claude data folder: render tests ran on an empty session list (run through run-logic.ps1 for the full set)");
            Set(w, "quotaReadUtc", DateTime.MinValue);

            int total = 0, okCount = 0;
            int tipTexts = 0, tipEmpty = 0, tipThrew = 0, tipBadNumbers = 0;
            string tipFirstProblem = null;
            List<string> bad = new List<string>();
            Action<int, bool, float, int, int, FetchResult, Problem?, string> go = delegate(int skin, bool dark, float sc, int mode, int period, FetchResult d, Problem? p, string label)
            {
                total++;
                if (RenderOnce(w, skin, dark, sc, mode, period, d, p, label))
                {
                    okCount++;
                    // every hover tip registered by this frame: build its text now (the real code builds them lazily when the mouse rests on them)
                    foreach (object hit in new List<object>(((System.Collections.IList)Get(w, "tipHits")).Cast<object>()))
                    {
                        string text;
                        try
                        {
                            Func<string> lazy = (Func<string>)Get(hit, "Lazy");
                            text = lazy != null ? lazy() : (string)Get(hit, "Text");
                        }
                        catch (Exception ex)
                        {
                            tipThrew++;
                            if (tipFirstProblem == null) tipFirstProblem = label + ": tip '" + Get(hit, "Id") + "' threw " + ex.GetType().Name + ": " + ex.Message;
                            continue;
                        }
                        tipTexts++;
                        if (string.IsNullOrEmpty(text)) tipEmpty++;
                        else if (BadNumberText(text))
                        {
                            tipBadNumbers++;
                            if (tipFirstProblem == null) tipFirstProblem = label + ": tip '" + Get(hit, "Id") + "' text has NaN/Infinity: " + text;
                        }
                    }
                }
                else bad.Add(label);
            };

            // 1. every view x several time tabs x skins x light / dark, with the Recommended profile
            for (int mode = 0; mode < 6; mode++)
            {
                foreach (int period in new int[] { 1, 5, 7 })
                {
                    foreach (int skin in new int[] { 0, 8 })
                    {
                        foreach (bool dark in new bool[] { false, true })
                        {
                            go(skin, dark, 1f, mode, period, normal, null, "profile1 view" + mode + " tab" + period + " skin" + skin + (dark ? " dark" : " light"));
                        }
                    }
                }
            }
            Check("render: 6 views x 3 time tabs x 2 skins x light/dark (Recommended profile): no exception (" + okCount + "/" + total + ")", okCount == total && bad.Count == 0, bad.Count > 0 ? bad[0] + "\n      " + Esc(firstRenderStack) : null);

            // 2. all three profiles x all views (views hidden by the profile are forced anyway: the code must not crash on a stale usageMode)
            int before = total;
            foreach (int prof in new int[] { 0, 1, 2 })
            {
                Call(w, "ApplyPreset", prof);
                for (int mode = 0; mode < 6; mode++)
                {
                    go(6, false, 1f, mode, 5, normal, null, "preset" + prof + " view" + mode);
                }
            }
            Check("render: presets Everything / Recommended / Minimal x 6 views (also views the profile hides)", bad.Count == 0, bad.Count > 0 ? bad[bad.Count - 1] + "\n      " + Esc(firstRenderStack) : null);

            // 3. random column / view / display masks (fixed seed)
            Random rnd = new Random(35);
            for (int i = 0; i < 40; i++)
            {
                int f = rnd.Next(256), v = rnd.Next(1, 64), s = rnd.Next(1, 128), d = rnd.Next(32);
                Call(w, "SetMasks", f, v, s, d);
                go(rnd.Next(16), rnd.Next(2) == 1, 1f, rnd.Next(6), new int[] { 1, 5, 7 }[rnd.Next(3)], normal, null, "random masks f=" + f + " v=" + v + " s=" + s + " d=" + d);
            }
            Check("render: 40 random mask combinations (functions / views / columns / display items), random skins: no exception", bad.Count == 0, bad.Count > 0 ? bad[bad.Count - 1] + "\n      " + Esc(firstRenderStack) : null);

            // 4. every sort column on every sortable view
            Call(w, "ApplyPreset", 0);
            int badBefore = bad.Count;
            int[] sc0 = G<int[]>(w, "sortCol");
            bool[] sd0 = G<bool[]>(w, "sortDesc");
            for (int m = 0; m < 3; m++)
            {
                for (int col = -1; col <= 8; col++)
                {
                    foreach (bool desc in new bool[] { true, false })
                    {
                        sc0[m] = col;
                        sd0[m] = desc;
                        go(0, false, 1f, m, 5, normal, null, "sort view" + m + " col" + col + (desc ? " desc" : " asc"));
                    }
                }
                sc0[m] = -1;
                sd0[m] = true;
            }
            Check("render: every sort column (-1..8) x direction on the session / model / compare views: no exception", bad.Count == badBefore, bad.Count > badBefore ? bad[bad.Count - 1] + "\n      " + Esc(firstRenderStack) : null);

            // 5. unusual data
            badBefore = bad.Count;
            FetchResult extremes = Quotas(100, 99.6, 3, 167), zero = Quotas(0, 0, 299, 167), noWeek = Quotas(7, -1, int.MinValue, 0), pastReset = Quotas(88.5, 100, -5, -2), over = Quotas(150, 300, 10, 10), neg = Quotas(-1, -1, 0, 0);
            foreach (FetchResult d in new FetchResult[] { extremes, zero, noWeek, pastReset, over, neg, null })
            {
                for (int mode = 0; mode < 6; mode += 1)
                {
                    go(3, true, 1f, mode, 1, d, d == null ? (Problem?)Problem.NoBridge : null, "data " + (d == null ? "none" : (d.FiveHour != null ? d.FiveHour.Percent.ToString(CultureInfo.InvariantCulture) : "-")) + " view" + mode);
                }
            }
            Check("render: 100 % / 0 % / no weekly quota / reset already past / 150 % / negative / no data at all, all views: no exception", bad.Count == badBefore, bad.Count > badBefore ? bad[bad.Count - 1] + "\n      " + Esc(firstRenderStack) : null);

            // 6. scaling
            badBefore = bad.Count;
            foreach (float scl in new float[] { 1.25f, 1.5f, 2f })
            {
                for (int mode = 0; mode < 6; mode++) go(12, false, scl, mode, 5, normal, null, "scale " + scl + " view" + mode);
            }
            Check("render: DPI scale 1.25 / 1.5 / 2.0 x 6 views: no exception", bad.Count == badBefore, bad.Count > badBefore ? bad[bad.Count - 1] + "\n      " + Esc(firstRenderStack) : null);

            // 7. hostile settings values are survivable by the renderer too
            badBefore = bad.Count;
            Widget wh = NewWidget(home, new string[] { "sv=2", "fmask=-1", "vmask=-1", "smask=-1", "dmask=-1", "view=5", "sort0=2147483647", "sort1=2147483647", "sort2=2147483647", "tabs=2047", "cc0=1", "cc1=1", "cc2=2147483647", "rowh=14", "w=800", "h2=900", "speedwin=2880" });
            IntPtr h2 = wh.Handle;
            Set(wh, "usageSnap", tracker.Take(anchor));
            Set(wh, "quotaReadUtc", DateTime.MinValue);
            for (int mode = 0; mode < 6; mode++)
            {
                total++;
                if (!RenderOnce(wh, 0, false, 1f, mode, 5, normal, null, "hostile settings view" + mode)) bad.Add("hostile settings view" + mode);
            }
            Check("render: widget loaded from a hostile settings.txt (masks -1, sort 2^31-1, 2047 tabs, cc2 huge, 2880-min speed window): no exception", bad.Count == badBefore, bad.Count > badBefore ? bad[bad.Count - 1] + "\n      " + Esc(firstRenderStack) : null);
            Check("hover tips: " + tipTexts + " tip texts built across all those frames (the lazy ones included): none throws, none prints NaN / Infinity", tipThrew == 0 && tipBadNumbers == 0 && tipTexts > 100, tipFirstProblem + " (texts=" + tipTexts + ")");
            Note("hover tips: " + tipTexts + " texts built, " + tipEmpty + " of them empty (allowed: the tip is then simply not shown)");
            Note("render smoke test: " + total + " frames drawn into memory (surface bitmaps), pixels are not compared");
        }

        // ================================================================== N status lights, cache alerts, limit warning, mail trigger guards
        private static int StateOf(Widget w, UsageTracker.LiveInfo li, DateTime nowUtc, out double rem)
        {
            MethodInfo m = typeof(Widget).GetMethod("StateOf", Inst, null, new Type[] { typeof(UsageTracker.LiveInfo), typeof(DateTime), typeof(double).MakeByRefType() }, null);
            object[] a = { li, nowUtc, 0.0 };
            object r = m.Invoke(w, a);
            rem = (double)a[2];
            return Convert.ToInt32(r, CultureInfo.InvariantCulture);
        }

        private static UsageTracker.LiveInfo Cache(DateTime? expiry, int ttl, long ctx, bool active, bool waiting)
        {
            UsageTracker.LiveInfo li = new UsageTracker.LiveInfo();
            li.ExpiryUtc = expiry;
            li.TtlMin = ttl;
            li.Ctx = ctx;
            li.Active = active;
            li.Waiting = waiting;
            li.Model = "claude-sonnet-5-5";
            li.LastUtc = DateTime.UtcNow;
            return li;
        }

        private static void TestStatusLights()
        {
            // CacheState: None 0, Fresh 1, Running 2, Soon 3, Waiting 4, Urgent 5
            Widget w = NewWidget(NewHome("lights"), null);
            DateTime now = DateTime.UtcNow;
            double rem;
            Check("light: no cache info at all -> grey (None)", StateOf(w, null, now, out rem) == 0 && StateOf(w, Cache(null, 60, 1, false, false), now, out rem) == 0);
            Check("light: cache expired (expiry in the past / exactly now) -> grey", StateOf(w, Cache(now.AddMinutes(-1), 60, 1, false, false), now, out rem) == 0 && StateOf(w, Cache(now, 60, 1, false, false), now, out rem) == 0);
            Check("light: 45 min left -> white (Fresh), rem reported", StateOf(w, Cache(now.AddMinutes(45), 60, 1, false, false), now, out rem) == 1 && Math.Abs(rem - 45) < 0.01);
            Check("light: 30.01 min -> Fresh, exactly 30 -> yellow (Soon)", StateOf(w, Cache(now.AddMinutes(30.01), 60, 1, false, false), now, out rem) == 1 && StateOf(w, Cache(now.AddMinutes(30), 60, 1, false, false), now, out rem) == 3);
            Check("light: 10.01 min -> Soon, exactly 10 -> red (Urgent)", StateOf(w, Cache(now.AddMinutes(10.01), 60, 1, false, false), now, out rem) == 3 && StateOf(w, Cache(now.AddMinutes(10), 60, 1, false, false), now, out rem) == 5);
            Check("light: 0.5 min left is still red", StateOf(w, Cache(now.AddMinutes(0.5), 60, 1, false, false), now, out rem) == 5);
            Check("light: running (Active) -> green (Running), even with a cache about to expire", StateOf(w, Cache(now.AddMinutes(3), 60, 1, true, false), now, out rem) == 2);
            Check("light: waiting for you -> blue (Waiting) and the countdown is still reported", StateOf(w, Cache(now.AddMinutes(20), 60, 1, false, true), now, out rem) == 4 && Math.Abs(rem - 20) < 0.01);
            Check("light: Waiting beats Running; an expired waiting session reports remaining 0, never negative", StateOf(w, Cache(now.AddMinutes(20), 60, 1, true, true), now, out rem) == 4 && StateOf(w, Cache(now.AddMinutes(-5), 60, 1, false, true), now, out rem) == 4 && rem == 0);

            // overall (tray icon / card dot): the most urgent state wins, counts per colour
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            Func<object[]> overall = delegate
            {
                object[] a = { 0, 0, 0, 0 };
                object best = Call(w, "OverallState", a);
                return new object[] { Convert.ToInt32(best, CultureInfo.InvariantCulture), a[0], a[1], a[2], a[3] };
            };
            object[] o = overall();
            Check("overall: no sessions -> grey, all counts 0", (int)o[0] == 0 && (int)o[1] == 0 && (int)o[2] == 0 && (int)o[3] == 0 && (int)o[4] == 0);
            DateTime real = DateTime.UtcNow;
            snap.Live["fresh"] = Cache(real.AddMinutes(50), 60, 1, false, false);
            snap.Live["soon"] = Cache(real.AddMinutes(20), 60, 1, false, false);
            snap.Live["run"] = Cache(real.AddMinutes(20), 60, 1, true, false);
            snap.Live["gone"] = Cache(real.AddMinutes(-20), 60, 1, false, false);
            o = overall();
            Check("overall: fresh + soon + running + expired -> worst is yellow (Soon=3), counts urgent 0 / soon 1 / running 1 / waiting 0", (int)o[0] == 3 && (int)o[1] == 0 && (int)o[2] == 1 && (int)o[3] == 1 && (int)o[4] == 0, string.Join(",", Array.ConvertAll<object, string>(o, delegate(object x) { return x.ToString(); })));
            snap.Live["wait"] = Cache(real.AddMinutes(40), 60, 1, false, true);
            o = overall();
            Check("overall: a waiting session (blue) outranks yellow", (int)o[0] == 4 && (int)o[4] == 1);
            snap.Live["urgent"] = Cache(real.AddMinutes(5), 60, 1, false, false);
            o = overall();
            Check("overall: a red session outranks everything", (int)o[0] == 5 && (int)o[1] == 1);
        }

        private static void TestCacheAlerts()
        {
            string home = NewHome("cachealert");
            Widget w = NewWidget(home, null);
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            DateTime now = DateTime.UtcNow;
            Func<DateTime, string> hit = delegate(DateTime t)
            {
                List<string> l = (List<string>)Call(w, "CollectCacheAlerts", t);
                l.Sort(StringComparer.Ordinal);
                return string.Join(",", l.ToArray());
            };
            snap.Live["s1"] = Cache(now.AddMinutes(25), 60, 100000, false, false);
            Check("alert: first sight of a session at 25 min -> nothing yet", hit(now) == "");
            snap.Live["s1"].ExpiryUtc = now.AddMinutes(11);
            Check("alert: 11 min left (still above the 10 min lead time) -> nothing", hit(now) == "");
            snap.Live["s1"].ExpiryUtc = now.AddMinutes(9);
            Check("alert: falls to 9 min (1-hour cache, 100k context, idle) -> notify", hit(now) == "s1");
            snap.Live["s1"].ExpiryUtc = now.AddMinutes(8);
            Check("alert: 8 min left -> NOT notified a second time", hit(now) == "");
            snap.Live["s1"].Active = true;
            Check("alert: the session starts running -> nothing", hit(now) == "");
            snap.Live["s1"].Active = false;
            snap.Live["s1"].ExpiryUtc = now.AddMinutes(60);
            Check("alert: cache renewed (60 min) -> nothing", hit(now) == "");
            snap.Live["s1"].ExpiryUtc = now.AddMinutes(9);
            Check("alert: falls under 10 min again -> notified again (once per cycle)", hit(now) == "s1");

            // rules
            snap.Live.Clear();
            ((Dictionary<string, double>)Get(w, "cacheRem")).Clear();
            snap.Live["a"] = Cache(now.AddMinutes(30), 5, 100000, false, false);
            snap.Live["b"] = Cache(now.AddMinutes(30), 29, 100000, false, false);
            snap.Live["c"] = Cache(now.AddMinutes(30), 30, 100000, false, false);
            snap.Live["d"] = Cache(now.AddMinutes(30), 60, 49999, false, false);
            snap.Live["e"] = Cache(now.AddMinutes(30), 60, 50000, false, false);
            snap.Live["f"] = Cache(now.AddMinutes(30), 60, 100000, false, true);
            hit(now);
            foreach (string k in new string[] { "a", "b", "c", "d", "e", "f" }) snap.Live[k].ExpiryUtc = now.AddMinutes(9);
            string ruleHit = hit(now);
            Check("alert rules: only TTL >= 30 min and context >= 50 000 qualify (5 min / 29 min TTL and 49 999 context do not); waiting sessions do", ruleHit == "c,e,f", ruleHit);
            // already red when first seen (app just started)
            snap.Live.Clear();
            ((Dictionary<string, double>)Get(w, "cacheRem")).Clear();
            snap.Live["x"] = Cache(now.AddMinutes(4), 60, 100000, false, false);
            Check("alert: a session already red when the widget starts is not announced", hit(now) == "");
            // exactly at the lead time
            ((Dictionary<string, double>)Get(w, "cacheRem")).Clear();
            snap.Live["x"].ExpiryUtc = now.AddMinutes(30);
            hit(now);
            snap.Live["x"].ExpiryUtc = now.AddMinutes(10);
            Check("alert: remaining exactly = lead time (10) counts (<=)", hit(now) == "x");
            // expired before anyone looked: no alert for a dead cache
            ((Dictionary<string, double>)Get(w, "cacheRem")).Clear();
            snap.Live["x"].ExpiryUtc = now.AddMinutes(30);
            hit(now);
            snap.Live["x"].ExpiryUtc = now.AddMinutes(-1);
            Check("alert: cache already expired -> no notification", hit(now) == "");
            // switches
            ((Dictionary<string, double>)Get(w, "cacheRem")).Clear();
            snap.Live["x"].ExpiryUtc = now.AddMinutes(30);
            hit(now);
            snap.Live["x"].ExpiryUtc = now.AddMinutes(9);
            Set(w, "alertsOn", false);
            Check("alert: notifications off -> nothing", hit(now) == "");
            Set(w, "alertsOn", true);
            ((Dictionary<string, double>)Get(w, "cacheRem")).Clear();
            snap.Live["x"].ExpiryUtc = now.AddMinutes(30);
            hit(now);
            snap.Live["x"].ExpiryUtc = now.AddMinutes(9);
            Set(w, "paused", true);
            Check("alert: background reading paused -> nothing", hit(now) == "");
            Set(w, "paused", false);
            // lead time setting
            foreach (int lead in new int[] { 5, 20, 30 })
            {
                Set(w, "alertMin", lead);
                ((Dictionary<string, double>)Get(w, "cacheRem")).Clear();
                snap.Live["x"].ExpiryUtc = now.AddMinutes(lead + 1);
                hit(now);
                snap.Live["x"].ExpiryUtc = now.AddMinutes(lead - 0.5);
                Check("alert: lead time " + lead + " min -> fires when the cache drops below " + lead, hit(now) == "x");
            }
            Set(w, "alertMin", 10);

            // text
            snap.Live.Clear();
            for (int i = 0; i < 5; i++)
            {
                UsageTracker.PeriodSummary per = snap.Periods[0];
                UsageTracker.SessionTotal st = new UsageTracker.SessionTotal();
                st.Session = "sess-" + i;
                st.Name = "proj";
                st.Title = i == 0 ? "A very long session title that goes on and on and on" : "Title " + i;
                per.Rows.Add(st);
                snap.Live[st.Session] = Cache(now.AddMinutes(8), 60, 123456, false, i == 1);
            }
            List<string> hits = new List<string>(new string[] { "sess-0", "sess-1", "sess-2", "sess-3", "sess-4" });
            object[] ta = { hits, now, null };
            string text = (string)Call(w, "BuildAlertText", ta);
            string title = (string)ta[2];
            Check("alert text: title says how many sessions, lead time and count (5 sessions)", Has(title, "10") && Has(title, "5"), Esc(title));
            Check("alert text: max 3 sessions listed, rest summarised; text <= 250 chars; long title cut at 16 chars + ellipsis", Regex.Matches(text, "「").Count == 3 && Has(text, "另外 2 个会话") && text.Length <= 250 && Has(text, "A very long sess…"), Esc(text));
            Check("alert text: shows remaining minutes (at least 1), context size, and the 'waiting for you' hint where it applies", Has(text, "剩 8 分钟") && Has(text, "123k") && Has(text, "它在等你处理"), Esc(text));
            ta = new object[] { new List<string>(new string[] { "sess-2" }), now, null };
            Call(w, "BuildAlertText", ta);
            Check("alert text: a single session -> title has no '(N sessions)' part", !Has((string)ta[2], "（"), Esc((string)ta[2]));
            // a title with a line break must not break the notification
            snap.Periods[0].Rows[2].Title = "line1\nline2\r\nline3";
            ta = new object[] { new List<string>(new string[] { "sess-2" }), now, null };
            Check("alert text: a session title containing line breaks does not throw", Try(delegate { Call(w, "BuildAlertText", ta); }));
        }

        private static void TestMailTriggerGuards()
        {
            // CheckMailAlerts is only run in situations in which it can NOT reach SendCacheMail (5-minute caches never fire for thresholds > 5),
            // because the send path opens a socket. MailStore.Attempts counts every send attempt and must stay 0.
            string home = NewHome("mailtrigger");
            Widget w = NewWidget(home, null);
            MailReset();
            MailStore.Enabled = true;
            MailStore.Host = "127.0.0.1";
            MailStore.Port = 1;
            MailStore.Ssl = false;
            MailStore.User = "u@example.test";
            MailStore.To = "t@example.test";
            MailStore.Password = "pw";
            MailWatch mw = new MailWatch();
            mw.Session = "s1";
            mw.Mins = MailStore.ParseMins("10");
            lock (MailStore.Watches) { MailStore.Watches.Add(mw); }
            Check("(setup) mail enabled, configured, one watched session", MailStore.Enabled && MailStore.Configured && MailStore.WatchCount == 1);
            UsageTracker.Snapshot snap = G<UsageTracker.Snapshot>(w, "usageSnap");
            System.Collections.IDictionary prev = (System.Collections.IDictionary)Get(w, "mailPrev");
            HashSet<string> sent = (HashSet<string>)Get(w, "mailSent");
            DateTime now = DateTime.UtcNow;
            UsageTracker.LiveInfo li = Cache(now.AddMinutes(25), 5, 100000, false, false);
            snap.Live["s1"] = li;

            Set(w, "paused", true);
            Call(w, "CheckMailAlerts", now);
            Check("mail trigger: paused -> nothing tracked", prev.Count == 0 && sent.Count == 0);
            Set(w, "paused", false);
            MailStore.Enabled = false;
            Call(w, "CheckMailAlerts", now);
            Check("mail trigger: disabled -> nothing tracked", prev.Count == 0);
            MailStore.Enabled = true;
            Call(w, "CheckMailAlerts", now);
            Check("mail trigger: first sight at 25 min -> remembered (25), nothing sent", prev.Contains("s1") && Math.Abs((double)prev["s1"] - 25) < 0.01 && sent.Count == 0 && MailStore.Attempts == 0);
            li.ExpiryUtc = now.AddMinutes(9);
            Call(w, "CheckMailAlerts", now);
            Check("mail trigger: falls to 9 min with a 5-min-TTL cache -> the 10-min threshold is marked done but NOT sent (a 5-min cache never reaches it from above)", sent.Count == 1 && MailStore.Attempts == 0);
            Call(w, "CheckMailAlerts", now.AddMinutes(1));          // one minute later, same cache cycle (same expiry): 8 min left
            Check("mail trigger: one minute later (same cache cycle) it stays marked, still nothing sent", sent.Count == 1 && MailStore.Attempts == 0, "sent=" + sent.Count);
            li.Active = true;
            Call(w, "CheckMailAlerts", now);
            Check("mail trigger: session running -> timer restarts (prev = MaxValue)", (double)prev["s1"] == double.MaxValue && MailStore.Attempts == 0);
            snap.Live.Clear();
            Call(w, "CheckMailAlerts", now);
            Check("mail trigger: session gone from the list -> its state is dropped", !prev.Contains("s1"));
            lock (MailStore.Watches) { MailStore.Watches.Clear(); }
            snap.Live["s1"] = Cache(now.AddMinutes(5), 5, 1, false, false);
            Call(w, "CheckMailAlerts", now);
            Check("mail trigger: no watches -> nothing tracked", prev.Count == 0 && MailStore.Attempts == 0);
            MailReset();
        }

        private static void TestLimitWarn()
        {
            Widget w = NewWidget(NewHome("limitwarn"), null);
            Check("limit warning default is 90 %", G<int>(w, "limitWarn") == 90);
            FetchResult r = Quotas(89, 50, 120, 100);
            Call(w, "CheckLimitWarn", r);
            Check("limit warning: 89 % / 50 % -> nothing yet", G<long>(w, "warnedFive") == 0 && G<long>(w, "warnedWeek") == 0);
            r = Quotas(90, 91, 120, 100);
            Call(w, "CheckLimitWarn", r);
            Check("limit warning: 90 % and 91 % -> both windows marked as warned (keyed by their reset time)", G<long>(w, "warnedFive") == r.FiveHour.ResetsAt.Value.Ticks && G<long>(w, "warnedWeek") == r.SevenDay.ResetsAt.Value.Ticks);
            long f1 = G<long>(w, "warnedFive");
            FetchResult r2 = Quotas(95, 99, 120, 100);
            r2.FiveHour.ResetsAt = r.FiveHour.ResetsAt;
            r2.SevenDay.ResetsAt = r.SevenDay.ResetsAt;
            Call(w, "CheckLimitWarn", r2);
            Check("limit warning: same window, higher percentage -> state unchanged (no repeat notification)", G<long>(w, "warnedFive") == f1);
            FetchResult r3 = Quotas(92, 99, 120, 100);
            r3.FiveHour.ResetsAt = r.FiveHour.ResetsAt.Value.AddHours(5);
            Call(w, "CheckLimitWarn", r3);
            Check("limit warning: next 5 h window (new reset time) -> warned again", G<long>(w, "warnedFive") == r3.FiveHour.ResetsAt.Value.Ticks);
            Set(w, "limitWarn", 0);
            Set(w, "warnedFive", 0L);
            Call(w, "CheckLimitWarn", Quotas(100, 100, 120, 100));
            Check("limit warning: setting 0 = off", G<long>(w, "warnedFive") == 0);
            Set(w, "limitWarn", 90);
            Check("limit warning: null result / no reset time / NaN percentage do not throw or mark anything", Try(delegate
            {
                Call(w, "CheckLimitWarn", new object[] { null });
                Call(w, "CheckLimitWarn", Quotas(95, 95, int.MinValue, int.MinValue));
                Call(w, "CheckLimitWarn", Quotas(double.NaN, double.NaN, 100, 100));
            }) && G<long>(w, "warnedFive") == 0);
        }

        // ================================================================== M status-line file reader
        private static void TestStatusLineFile()
        {
            string home = NewHome("statusline");
            UseHome(home);
            ResetStatics();
            string path = StatusLineCli.FilePath;
            Func<string, FetchResult> read = delegate(string json)
            {
                File.WriteAllText(path, json, new UTF8Encoding(false));
                return StatusLineBridge.Read();
            };
            double nowSecs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            string n = nowSecs.ToString("0", CultureInfo.InvariantCulture), later = (nowSecs + 7200).ToString("0", CultureInfo.InvariantCulture), past = (nowSecs - 600).ToString("0", CultureInfo.InvariantCulture);
            if (File.Exists(path)) File.Delete(path);
            Check("rate_limits.json missing -> 'no status line' problem, no exception", StatusLineBridge.Read().Problem == Problem.NoBridge);
            FetchResult r = read("{\"saved_at\":" + n + ",\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + later + "},\"seven_day\":{\"used_percentage\":17.5,\"resets_at\":" + later + "}}");
            Check("valid file: 42 % / 17.5 %, via bridge, no problem", r.Problem == Problem.None && r.Via == "bridge" && (int)r.FiveHour.Percent == 42 && Math.Abs(r.SevenDay.Percent - 17.5) < 1e-9);
            r = read("{\"saved_at\":" + n + ",\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + past + "},\"seven_day\":{\"used_percentage\":17,\"resets_at\":" + later + "}}");
            Check("window whose reset time has passed is dropped, the other one stays", r.FiveHour == null && r.SevenDay != null && r.Problem == Problem.None);
            r = read("{\"saved_at\":" + n + ",\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + past + "},\"seven_day\":{\"used_percentage\":17,\"resets_at\":" + past + "}}");
            Check("both windows past their reset -> no data, 'window reset' hint", r.FiveHour == null && r.SevenDay == null && r.Problem != Problem.None);
            r = read("{\"five_hour\":{\"used_percentage\":-5,\"resets_at\":" + later + "},\"seven_day\":{\"used_percentage\":\"33\",\"resets_at\":" + later + "}}");
            Check("negative percentage -> 0, numeric string '33' -> 33", r.FiveHour.Percent == 0 && r.SevenDay.Percent == 33);
            r = read("{\"five_hour\":{\"used_percentage\":150,\"resets_at\":" + later + "}}");
            Check("150 % is kept as given (the UI must cope with it; see render smoke test)", r.FiveHour != null && r.FiveHour.Percent == 150 && r.SevenDay == null);
            r = read("{\"five_hour\":{\"used_percentage\":\"abc\",\"resets_at\":\"xyz\"},\"seven_day\":{\"used_percentage\":null}}");
            Check("non-numeric percentage -> 0 %, non-numeric reset -> no reset time, null window ignored", r.FiveHour != null && r.FiveHour.Percent == 0 && !r.FiveHour.ResetsAt.HasValue && r.SevenDay == null);
            string[] broken = { "", "   ", "garbage", "[1,2,3]", "{", "{\"five_hour\":", "null", "\"text\"", "12345", new string('[', 5000), "{\"a\":" + new string('{', 500) + "}" };
            int okBroken = 0;
            foreach (string b in broken)
            {
                try
                {
                    FetchResult fb = read(b);
                    if (fb.Problem != Problem.None && fb.FiveHour == null) okBroken++;
                }
                catch (Exception)
                {
                }
            }
            Check("broken files (empty, garbage, arrays, truncated, 5000 brackets, deep nesting): never throw, always 'no data'", okBroken == broken.Length, okBroken + " of " + broken.Length);
            bool t1 = true, t2 = true;
            try { read("{\"five_hour\":{\"used_percentage\":10,\"resets_at\":1e18}}"); } catch (Exception) { t1 = false; }
            try { read("{\"saved_at\":1e18,\"five_hour\":{\"used_percentage\":10,\"resets_at\":" + later + "}}"); } catch (Exception) { t2 = false; }
            KnownBug("BUG-L10", "rate_limits.json with an absurd timestamp (resets_at / saved_at = 1e18) does not make the reader throw", t1 && t2, "resets_at=1e18 threw: " + !t1 + ", saved_at=1e18 threw: " + !t2);
            r = read("{\"five_hour\":{\"used_percentage\":\"NaN\",\"resets_at\":" + later + "}}");
            KnownBug("BUG-L10", "used_percentage given as the text 'NaN' does not become Quota.Percent = NaN", r.FiveHour == null || !double.IsNaN(r.FiveHour.Percent), r.FiveHour != null ? "Percent=" + r.FiveHour.Percent.ToString(CultureInfo.InvariantCulture) : "(no window)");
            File.Delete(path);
        }

        // ================================================================== O API view data (DeepSeek and other non-Claude models)
        private static void TestApiView()
        {
            string home = NewHome("apiview");
            UseHome(home);
            string logs = Path.Combine(home, "logs");
            Directory.CreateDirectory(logs);
            DateTime d0 = new DateTime(2026, 11, 1, 10, 0, 0);
            List<string> rows = new List<string>();
            rows.Add(DetailHeader);
            rows.Add(DetailRow(d0, "s1", "p", "deepseek-flash-20251017", 1000, 2000, 300, 100, 5000, false, "m1"));
            rows.Add(DetailRow(d0.AddDays(2), "s1", "p", "deepseek-flash", 10, 20, 30, 40, 50, true, "m2"));
            rows.Add(DetailRow(d0.AddDays(1), "s2", "p", "qwen3-coder", 5, 6, 7, 8, 9, false, "m3"));
            rows.Add(DetailRow(d0, "s2", "p", "claude-sonnet-5-5", 999, 999, 999, 999, 999, false, "m4"));
            rows.Add(DetailRow(d0, "s2", "p", "CLAUDE-OPUS-5-5", 999, 999, 999, 999, 999, false, "m5"));
            rows.Add(DetailRow(d0, "s2", "p", "<synthetic>", 999, 999, 999, 999, 999, false, "m6"));
            rows.Add(DetailRow(d0, "s2", "p", "", 999, 999, 999, 999, 999, false, "m7"));
            rows.Add("garbage");
            rows.Add(d0.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ",short");
            rows.Add(DetailRow(d0.AddDays(5), "s3", "p", "deepseek-chat", -5, 0, 0, 0, 0, false, "m8").Replace(",-5,0,0,0,0,", ",abc,0,0,0,0,"));
            File.WriteAllLines(Path.Combine(logs, "用量明细_v2.csv"), rows.ToArray(), new UTF8Encoding(true));
            List<LogSummary.ApiTotal> api = LogSummary.ReadApiTotals(Path.Combine(logs, "用量明细_v2.csv"));
            Check("API view: only non-Claude models (claude* in any case, <synthetic>, empty model are left out): deepseek-chat, deepseek-flash, qwen3-coder", api.Count == 3, api.Count + " rows");
            Check("API view: newest model first (deepseek-chat, deepseek-flash, qwen3-coder)", api.Count == 3 && api[0].Model == "deepseek-chat" && api[1].Model == "deepseek-flash" && api[2].Model == "qwen3-coder");
            LogSummary.ApiTotal flash = api.Count == 3 ? api[1] : null;
            Check("API view: date suffix stripped, rows of the same model summed (2 msgs, in 1010, out 2020, cache write 470, cache read 5050)",
                flash != null && flash.Msgs == 2 && flash.In == 1010 && flash.Out == 2020 && flash.Cw == 300 + 100 + 30 + 40 && flash.Cr == 5050, flash == null ? "" : flash.Msgs + "/" + flash.In + "/" + flash.Out + "/" + flash.Cw + "/" + flash.Cr);
            Check("API view: first / last time of a model", flash != null && flash.First == d0 && flash.Last == d0.AddDays(2));
            Check("API view: a row with non-numeric tokens still counts as a message with 0 tokens", api.Count == 3 && api[0].Msgs == 1 && api[0].In == 0);
            Check("API view: missing log file -> empty list, no exception", LogSummary.ReadApiTotals(Path.Combine(logs, "nope.csv")).Count == 0);

            // price estimate: DeepSeek idle-time prices (CNY per million tokens): input miss 1.0, hit 0.02, output 4.0; cache write is billed as a miss
            Func<long, long, long, long, double> cny = delegate(long i, long o, long cw, long cr) { return (double)CallStatic(typeof(Widget), "EstimateCostCny", new object[] { i, o, cw, cr }); };
            Check("DeepSeek estimate: 1M input = 1.00, 1M output = 4.00, 1M cache write = 1.00, 1M cache read = 0.02, zero = 0",
                Math.Abs(cny(1000000, 0, 0, 0) - 1.0) < 1e-9 && Math.Abs(cny(0, 1000000, 0, 0) - 4.0) < 1e-9 && Math.Abs(cny(0, 0, 1000000, 0) - 1.0) < 1e-9 && Math.Abs(cny(0, 0, 0, 1000000) - 0.02) < 1e-9 && cny(0, 0, 0, 0) == 0);
            Check("DeepSeek estimate: mixed 2M in + 1M out + 3M cache read = 2 + 4 + 0.06", Math.Abs(cny(2000000, 1000000, 0, 3000000) - 6.06) < 1e-9);
            Check("DeepSeek estimate: huge counts stay finite", !double.IsNaN(cny(long.MaxValue / 4, long.MaxValue / 4, long.MaxValue / 4, long.MaxValue / 4)) && !double.IsInfinity(cny(long.MaxValue / 4, long.MaxValue / 4, long.MaxValue / 4, long.MaxValue / 4)));
            Func<string, string> priced = delegate(string model)
            {
                LogSummary.ApiTotal a = new LogSummary.ApiTotal();
                a.Model = model;
                a.In = 1000000;
                return WS("CnyText", CallStatic(typeof(Widget), "ApiCostCny", new object[] { a }));
            };
            Check("ApiCostCny: deepseek models (any case) are priced, other models show a dash (no price)", priced("deepseek-flash") == "¥1.00" && priced("DeepSeek-V4") == "¥1.00" && priced("qwen3-coder") == "—" && priced("") == "—", priced("deepseek-flash") + "|" + priced("qwen3-coder"));
            Check("CnyText: NaN -> dash, 0 -> '¥0.00', 99.5 -> '¥99.50', 100 -> '¥100', 12345.6 -> '¥12346'", WS("CnyText", double.NaN) == "—" && WS("CnyText", 0.0) == "¥0.00" && WS("CnyText", 99.5) == "¥99.50" && WS("CnyText", 100.0) == "¥100" && WS("CnyText", 12345.6) == "¥12346");
        }

        // @@MORE-GROUPS@@
    }
}
