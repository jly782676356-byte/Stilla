// The part shared by the widget (Stilla.exe) and the status line helper (Stilla.Statusline.exe): where files go and how status-line data is stored.
// This file must not depend on WinForms, because the status line helper is a console program (started as a command by Claude Code's statusLine, which waits for it to finish and reads its output).
//
// The compiler is the csc that ships with the system (C# 5), so there is no ?. or string interpolation here.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace Stilla
{
    internal static class AppInfo
    {
        public const string Name = "Stilla";
        public const string Version = "0.1.1";
        public const string Disclaimer = "Stilla is an independent community project. It is not made or endorsed by Anthropic. Claude is a trademark of Anthropic, PBC.";

        // The project's GitHub repository: "check for updates", "go to update page" and "donate" all point here.
        // After creating the repository run tools\set-repo.ps1 -Owner your-username, which replaces the OWNER below and the links in the docs together.
        // The Chinese and English editions share one repository: installers of both languages are published in the same Release (file names carry -zh / -en).
        public const string RepoOwner = "jly782676356-byte";
        public const string RepoName = "Stilla";

        // While it is still not a real username the update check does nothing (and uses no network)
        public static bool Configured
        {
            get { return RepoOwner != "OWNER"; }
        }

        public static string RepoUrl
        {
            get { return "https://github.com/" + RepoOwner + "/" + RepoName; }
        }

        public static string ReleasesUrl
        {
            get { return RepoUrl + "/releases/latest"; }
        }

        public static string LatestApiUrl
        {
            get { return "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest"; }
        }

        // Donate entry: currently points to the #donate section of the README; once there is a dedicated donate page only this line changes
        public static string DonateUrl
        {
            get { return RepoUrl + "#donate"; }
        }
    }

    // Where all files go: settings, quota file, logs. Default %APPDATA%\Stilla; with a portable.txt next to the exe they go to the data folder next to the exe;
    // the environment variable STILLA_HOME can point to another location (for tests / screenshots, never touches the real settings).
    internal static class AppPaths
    {
        private static string home;

        public static bool Demo
        {
            get { return Environment.GetEnvironmentVariable("STILLA_DEMO") == "1"; }
        }

        public static string ExeDir
        {
            get { return Path.GetDirectoryName(Assembly.GetEntryAssembly().Location); }
        }

        // Root folder of settings, quota file and logs
        public static string Home
        {
            get
            {
                if (home == null)
                {
                    home = ResolveHome();
                }
                return home;
            }
        }

        private static string ResolveHome()
        {
            string env = Environment.GetEnvironmentVariable("STILLA_HOME");
            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }
            if (File.Exists(Path.Combine(ExeDir, "portable.txt")))
            {
                return Path.Combine(ExeDir, "data");
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stilla");
        }

        public static string SettingsFile
        {
            get { return Path.Combine(Home, "settings.txt"); }
        }

        // CSV files such as the usage log, quota snapshots and conversion table
        public static string DataDir
        {
            get { return Path.Combine(Home, "logs"); }
        }

        // The Python scripts that ship with the program (the scripts folder next to the exe, otherwise the exe's own folder)
        public static string Script(string name)
        {
            string a = Path.Combine(Path.Combine(ExeDir, "scripts"), name);
            return File.Exists(a) ? a : Path.Combine(ExeDir, name);
        }

        // Point the data folder out to a Python script run in the background
        public static void ApplyScriptEnv(ProcessStartInfo psi)
        {
            psi.EnvironmentVariables["STILLA_DATA"] = DataDir;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        }
    }

    // The status line helper: "Stilla.Statusline.exe [--quiet] [--chain "command"]"
    // Claude Code starts it every time it refreshes the status line and sends a JSON document to stdin. It has to be fast, show no window, and an error must not affect Claude Code.
    internal static class StatusLineCli
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // The quota is stored here; the widget reads it (StatusLineBridge.Read)
        public static string FilePath
        {
            get { return Path.Combine(AppPaths.Home, "rate_limits.json"); }
        }

        public static int Run(string[] args)
        {
            bool quiet = false;
            string chain = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--quiet")
                {
                    quiet = true;
                }
                else if (args[i] == "--chain" && i + 1 < args.Length)
                {
                    chain = args[++i];
                }
                else if (args[i] == "--chain-file" && i + 1 < args.Length)
                {
                    // Your original status line command is stored in a file (written by the installer), to avoid escaping quotes on the command line
                    try
                    {
                        chain = File.ReadAllText(args[++i], Encoding.UTF8).Trim();
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            byte[] input = new byte[0];
            try
            {
                using (Stream stdin = Console.OpenStandardInput())
                using (MemoryStream ms = new MemoryStream())
                {
                    stdin.CopyTo(ms);
                    input = ms.ToArray();
                }
            }
            catch (Exception)
            {
            }

            Dictionary<string, object> parsed = null;
            try
            {
                parsed = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(input).TrimStart((char)0xFEFF).Trim()) as Dictionary<string, object>;
                Save(parsed);
            }
            catch (Exception ex)
            {
                // If it cannot be saved, so be it (it must not affect Claude Code); when troubleshooting set the environment variable STILLA_DEBUG=1 to write the reason to stderr
                if (Environment.GetEnvironmentVariable("STILLA_DEBUG") == "1")
                {
                    Console.Error.WriteLine("Stilla.Statusline: " + ex);
                }
            }

            try
            {
                string output = null;
                if (!string.IsNullOrEmpty(chain))
                {
                    output = RunChain(chain, input);
                }
                else if (!quiet)
                {
                    output = Summary(parsed);
                }
                if (!string.IsNullOrEmpty(output))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(output);
                    using (Stream stdout = Console.OpenStandardOutput())
                    {
                        stdout.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception)
            {
            }
            return 0;
        }

        // Save rate_limits into rate_limits.json. When this input carries no window, the old window is kept as long as its reset time has not passed
        private static void Save(Dictionary<string, object> input)
        {
            Dictionary<string, object> rl = Sub(input, "rate_limits");
            if (rl == null)
            {
                return;     // No API response yet, or not a subscription account: write nothing
            }
            double now = (DateTime.UtcNow - Epoch).TotalSeconds;
            Dictionary<string, object> old = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    old = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                }
            }
            catch (Exception)
            {
            }

            Dictionary<string, object> outp = new Dictionary<string, object>();
            outp["saved_at"] = Math.Round(now);
            bool any = false;
            foreach (string key in new string[] { "five_hour", "seven_day" })
            {
                Dictionary<string, object> w = Window(Sub(rl, key), now);
                if (w == null)
                {
                    w = Window(Sub(old, key), now);
                }
                if (w != null)
                {
                    outp[key] = w;
                    any = true;
                }
            }
            if (!any)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string tmp = FilePath + "." + Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(outp), new UTF8Encoding(false));
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Replace(tmp, FilePath, null);
                }
                else
                {
                    File.Move(tmp, FilePath);
                }
            }
            catch (IOException)
            {
                // The widget happens to be reading: write again at the next refresh
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception)
                {
                }
            }
        }

        private static Dictionary<string, object> Window(Dictionary<string, object> w, double nowSecs)
        {
            if (w == null)
            {
                return null;
            }
            object u, t;
            if (!w.TryGetValue("used_percentage", out u) || u == null)
            {
                return null;
            }
            double resets = 0;
            if (w.TryGetValue("resets_at", out t) && t != null)
            {
                resets = ToDouble(t);
            }
            if (resets > 0 && resets <= nowSecs)
            {
                return null;
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["used_percentage"] = ToDouble(u);
            if (resets > 0)
            {
                d["resets_at"] = resets;
            }
            return d;
        }

        private static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o))
            {
                return null;
            }
            return o as Dictionary<string, object>;
        }

        public static double ToDouble(object o)
        {
            try
            {
                return Convert.ToDouble(o, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // The short text shown on the status line: "5h 58% · 7d 61%"
        private static string Summary(Dictionary<string, object> input)
        {
            Dictionary<string, object> rl = Sub(input, "rate_limits");
            if (rl == null)
            {
                return "";
            }
            List<string> parts = new List<string>();
            Dictionary<string, object> five = Sub(rl, "five_hour");
            Dictionary<string, object> week = Sub(rl, "seven_day");
            object v;
            if (five != null && five.TryGetValue("used_percentage", out v) && v != null)
            {
                parts.Add("5h " + Math.Round(ToDouble(v)).ToString("0", CultureInfo.InvariantCulture) + "%");
            }
            if (week != null && week.TryGetValue("used_percentage", out v) && v != null)
            {
                parts.Add("7d " + Math.Round(ToDouble(v)).ToString("0", CultureInfo.InvariantCulture) + "%");
            }
            return string.Join(" · ", parts.ToArray());
        }

        // Hand stdin unchanged to another status line command and print its result (so your original status line is not displaced)
        private static string RunChain(string command, byte[] input)
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/d /c " + command);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                try
                {
                    p.StandardInput.BaseStream.Write(input, 0, input.Length);
                    p.StandardInput.Close();
                }
                catch (Exception)
                {
                }
                string text = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception)
                    {
                    }
                }
                return text;
            }
        }
    }

    // Claude Code's data folder (containing the projects\*.jsonl session records, sessions\*.json and .credentials.json).
    // The command line, the VS Code extension and the JetBrains plugin all use the same one: the folder given by the CLAUDE_CONFIG_DIR environment variable, or ~\.claude if it is not set.
    // It also looks automatically for: sibling folders such as ~\.claude-work for multiple accounts / configurations, and the folders of the Claude desktop app (including the Microsoft Store version) under AppData;
    // ones it cannot find (WSL, another drive, someone's unusual location) can be added by hand from the widget's right-click menu; they are stored in roots.txt, one per line.
    internal static class ClaudeRoots
    {
        private static readonly object gate = new object();
        private static List<string> cache;
        private static DateTime cacheAt = DateTime.MinValue;
        private static bool refreshing;
        private static int generation;      // Incremented when a folder is added / cleared by hand: results of background detection that started before this are discarded, so they do not wash out the folder just added

        private static readonly string[] SkipDirs =
        {
            "cache", "code cache", "gpucache", "blob_storage", "indexeddb", "local storage", "session storage", "logs",
            "crashpad", "network", "dawncache", "service worker", "shared dictionary", "node_modules"
        };

        // Main folder: the same one Claude Code itself uses
        public static string Primary()
        {
            string d = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(d))
            {
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }
            return d;
        }

        public static string CustomFile
        {
            get { return Path.Combine(AppPaths.Home, "roots.txt"); }
        }

        public static List<string> Custom()
        {
            List<string> res = new List<string>();
            try
            {
                if (File.Exists(CustomFile))
                {
                    foreach (string line in File.ReadAllLines(CustomFile, Encoding.UTF8))
                    {
                        string p = line.Trim();
                        if (p.Length > 0 && !p.StartsWith("#"))
                        {
                            res.Add(p);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return res;
        }

        public static void AddCustom(string dir)
        {
            List<string> now = Custom();
            foreach (string p in now)
            {
                if (Same(p, dir))
                {
                    return;
                }
            }
            now.Add(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(CustomFile));
            File.WriteAllLines(CustomFile, now.ToArray(), new UTF8Encoding(false));
            Refresh();
        }

        public static void ClearCustom()
        {
            // Right after being written the file may be held briefly by antivirus / the indexing service: retry a few times and if it still cannot be deleted, empty its content
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(CustomFile))
                    {
                        File.Delete(CustomFile);
                    }
                    break;
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            try
            {
                if (File.Exists(CustomFile))
                {
                    File.WriteAllText(CustomFile, "", new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
            }
            Refresh();
        }

        // Does a folder look like Claude Code's data folder
        public static bool Looks(string root)
        {
            try
            {
                return Directory.Exists(Path.Combine(root, "projects")) || Directory.Exists(Path.Combine(root, "sessions"))
                    || File.Exists(Path.Combine(root, ".credentials.json"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // All data folders; the first one is always the main folder. What you get is a cache (never blocks the UI); detection is redone in the background every 5 minutes
        public static List<string> All()
        {
            lock (gate)
            {
                if (cache == null)
                {
                    cache = Quick();
                }
                if (!refreshing && DateTime.UtcNow - cacheAt > TimeSpan.FromMinutes(5))
                {
                    refreshing = true;
                    cacheAt = DateTime.UtcNow;
                    int gen = generation;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        List<string> full = null;
                        try
                        {
                            full = Discover();
                        }
                        catch (Exception)
                        {
                        }
                        lock (gate)
                        {
                            if (full != null && gen == generation)
                            {
                                cache = full;
                            }
                            refreshing = false;
                        }
                    });
                }
                return new List<string>(cache);
            }
        }

        public static void Refresh()
        {
            lock (gate)
            {
                generation++;
                cache = Quick();
                cacheAt = DateTime.MinValue;
            }
        }

        public static List<string> ProjectDirs()
        {
            List<string> res = new List<string>();
            foreach (string r in All())
            {
                string p = Path.Combine(r, "projects");
                if (Directory.Exists(p))
                {
                    res.Add(p);
                }
            }
            return res;
        }

        public static List<string> SessionDirs()
        {
            List<string> res = new List<string>();
            foreach (string r in All())
            {
                string p = Path.Combine(r, "sessions");
                if (Directory.Exists(p))
                {
                    res.Add(p);
                }
            }
            return res;
        }

        // The login credentials file: look in the main folder first, then in the others
        public static string FindCredentials()
        {
            foreach (string r in All())
            {
                string p = Path.Combine(r, ".credentials.json");
                if (File.Exists(p))
                {
                    return p;
                }
            }
            return Path.Combine(Primary(), ".credentials.json");
        }

        private static string Norm(string p)
        {
            try
            {
                return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant();
            }
            catch (Exception)
            {
                return p.ToLowerInvariant();
            }
        }

        private static bool Same(string a, string b)
        {
            return Norm(a) == Norm(b);
        }

        private static void AddUnique(List<string> list, string dir)
        {
            foreach (string x in list)
            {
                if (Same(x, dir))
                {
                    return;
                }
            }
            list.Add(dir);
        }

        // Demo mode / tests (STILLA_ISOLATED=1): read only the main folder and never look for others, so no real sessions show up in screenshots
        public static bool Isolated
        {
            get { return AppPaths.Demo || Environment.GetEnvironmentVariable("STILLA_ISOLATED") == "1"; }
        }

        // Fast: the main folder + those added by hand + sibling .claude* folders under the user folder (one level only)
        private static List<string> Quick()
        {
            List<string> res = new List<string>();
            res.Add(Primary());
            if (Isolated)
            {
                return res;
            }
            foreach (string c in Custom())
            {
                if (Looks(c))
                {
                    AddUnique(res, c);
                }
            }
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (string d in Directory.GetDirectories(profile, ".claude*"))
                {
                    if (Looks(d))
                    {
                        AddUnique(res, d);
                    }
                }
                string xdg = Path.Combine(Path.Combine(profile, ".config"), "claude");
                if (Looks(xdg))
                {
                    AddUnique(res, xdg);
                }
            }
            catch (Exception)
            {
            }
            return res;
        }

        // Slow: also look for folders named .claude inside the desktop app's folders (the desktop app keeps Claude Code's sessions under its own folder, and the location changes between versions)
        private static List<string> Discover()
        {
            List<string> res = Quick();
            if (Isolated)
            {
                return res;
            }
            List<string> starts = new List<string>();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            starts.Add(Path.Combine(roaming, "Claude"));
            starts.Add(Path.Combine(local, "Claude"));
            starts.Add(Path.Combine(local, "AnthropicClaude"));
            try
            {
                // The Microsoft Store version virtualizes AppData under Packages\<package name>\LocalCache\Roaming
                foreach (string pkg in Directory.GetDirectories(Path.Combine(local, "Packages"), "*laude*"))
                {
                    starts.Add(Path.Combine(Path.Combine(pkg, "LocalCache"), "Roaming"));
                    starts.Add(Path.Combine(Path.Combine(pkg, "LocalCache"), "Local"));
                }
            }
            catch (Exception)
            {
            }
            int visited = 0;
            foreach (string s in starts)
            {
                if (Directory.Exists(s))
                {
                    Walk(s, 0, 7, res, ref visited);
                }
            }
            return res;
        }

        private static void Walk(string dir, int depth, int maxDepth, List<string> res, ref int visited)
        {
            if (depth > maxDepth || visited > 4000)
            {
                return;
            }
            string[] subs;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                return;
            }
            foreach (string d in subs)
            {
                visited++;
                string name = Path.GetFileName(d);
                string lower = name.ToLowerInvariant();
                if (Array.IndexOf(SkipDirs, lower) >= 0)
                {
                    continue;
                }
                if (lower == ".claude" || lower == "claude-config" || lower == "claude_config")
                {
                    if (Looks(d))
                    {
                        AddUnique(res, d);
                    }
                    continue;
                }
                Walk(d, depth + 1, maxDepth, res, ref visited);
            }
        }
    }

    // Writes / undoes statusLine in Claude Code's settings.json. Only called with the user's explicit consent (ticked in the installer, or "Yes" in the setup dialog).
    //   - It makes a backup first as settings.json.stilla-backup-<time>; if the JSON cannot be parsed the file is left alone;
    //   - If the user already has a statusLine of their own: it is not overwritten, the original command is stored in statusline-chain.txt and handed over by Stilla.Statusline.exe (--chain-file), and the original status line keeps being shown;
    //   - On undo the original command is put back.
    internal static class ClaudeSettings
    {
        private const string Marker = "Stilla.Statusline";

        // Your original status line command is stored here. One copy per Claude config folder (the original commands of different folders may differ)
        public static string ChainFile(string root)
        {
            ulong h = 14695981039346656037UL;       // FNV-1a, only used to give the file a stable name
            string norm = root.TrimEnd('\\', '/').ToLowerInvariant();
            for (int i = 0; i < norm.Length; i++)
            {
                h = (h ^ norm[i]) * 1099511628211UL;
            }
            return Path.Combine(AppPaths.Home, "statusline-chain-" + h.ToString("x16").Substring(0, 8) + ".txt");
        }

        public static string PathIn(string root)
        {
            return Path.Combine(root, "settings.json");
        }

        private static Dictionary<string, object> Load(string path, out string error)
        {
            error = null;
            if (!File.Exists(path))
            {
                return new Dictionary<string, object>();
            }
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8).TrimStart((char)0xFEFF).Trim();
                if (text.Length == 0)
                {
                    return new Dictionary<string, object>();
                }
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                if (d == null)
                {
                    error = "not a JSON object";
                }
                return d;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static string Cmd(Dictionary<string, object> doc)
        {
            object sl;
            Dictionary<string, object> d;
            object c;
            if (doc.TryGetValue("statusLine", out sl) && (d = sl as Dictionary<string, object>) != null && d.TryGetValue("command", out c) && c != null)
            {
                return Convert.ToString(c, CultureInfo.InvariantCulture);
            }
            return null;
        }

        private static void Save(string path, Dictionary<string, object> doc)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".stilla.tmp";
            File.WriteAllText(tmp, Pretty(new JavaScriptSerializer().Serialize(doc)) + "\n", new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        private static string Backup(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            string stamp = path + ".stilla-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string b = stamp;
            for (int i = 2; File.Exists(b); i++)
            {
                b = stamp + "-" + i;        // Backing up twice within the same second does not collide, and never overwrites an older backup
            }
            File.Copy(path, b, false);
            return b;
        }

        // The result shown to the user; ok = whether it succeeded ("already set up" also counts as success)
        public static string Install(string root, string cliPath, out bool ok)
        {
            ok = false;
            try
            {
                return InstallCore(root, cliPath, out ok);
            }
            catch (Exception ex)
            {
                ok = false;
                return "Write failed (the original file was not damaged, you can restore from the backup): " + ex.Message;
            }
        }

        private static string InstallCore(string root, string cliPath, out bool ok)
        {
            ok = false;
            string path = PathIn(root);
            string err;
            Dictionary<string, object> doc = Load(path, out err);
            if (doc == null)
            {
                return path + " is not valid JSON (" + err + "); nothing was changed. Please add the statusLine by hand as described in the README.";
            }
            string old = Cmd(doc);
            if (old != null && old.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ok = true;
                return "Already set up: " + path;
            }
            string backup = Backup(path);
            string command = "\"" + cliPath + "\" --quiet";
            string note = "";
            if (!string.IsNullOrEmpty(old) && old.Trim().Length > 0)
            {
                Directory.CreateDirectory(AppPaths.Home);
                File.WriteAllText(ChainFile(root), old, new UTF8Encoding(false));
                command += " --chain-file \"" + ChainFile(root) + "\"";
                note = "\nYour original status line command was kept (it will be handed over to): " + old;
            }
            Dictionary<string, object> sl = new Dictionary<string, object>();
            object prev;
            Dictionary<string, object> pd;
            if (doc.TryGetValue("statusLine", out prev) && (pd = prev as Dictionary<string, object>) != null)
            {
                foreach (KeyValuePair<string, object> kv in pd)
                {
                    sl[kv.Key] = kv.Value;     // Other settings such as padding are kept as they are
                }
            }
            sl["type"] = "command";
            sl["command"] = command;
            doc["statusLine"] = sl;
            Save(path, doc);
            ok = true;
            return "Written to " + path + (backup != null ? "\nBackup: " + backup : "") + note;
        }

        public static string Uninstall(string root, out bool changed)
        {
            changed = false;
            try
            {
                return UninstallCore(root, out changed);
            }
            catch (Exception ex)
            {
                changed = false;
                return "Undo failed: " + ex.Message;
            }
        }

        private static string UninstallCore(string root, out bool changed)
        {
            changed = false;
            string path = PathIn(root);
            string err;
            Dictionary<string, object> doc = Load(path, out err);
            if (doc == null || !File.Exists(path))
            {
                return "There is nothing to undo.";
            }
            string cur = Cmd(doc);
            if (cur == null || cur.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return "The status line in settings.json was not set up by Stilla, so it was left alone.";
            }
            Backup(path);
            string chain = null;
            try
            {
                if (File.Exists(ChainFile(root)))
                {
                    chain = File.ReadAllText(ChainFile(root), Encoding.UTF8).Trim();
                }
            }
            catch (Exception)
            {
            }
            if (!string.IsNullOrEmpty(chain))
            {
                ((Dictionary<string, object>)doc["statusLine"])["command"] = chain;
            }
            else
            {
                doc.Remove("statusLine");
            }
            Save(path, doc);
            try
            {
                File.Delete(ChainFile(root));
            }
            catch (Exception)
            {
            }
            changed = true;
            return !string.IsNullOrEmpty(chain) ? "Undone; your original status line command was put back." : "Undone (the statusLine was removed).";
        }

        // Lay compact JSON out with a two-space indent (JavaScriptSerializer only produces one line)
        public static string Pretty(string json)
        {
            StringBuilder sb = new StringBuilder();
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        sb.Append(json[++i]);
                    }
                    else if (c == '"')
                    {
                        inStr = false;
                    }
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inStr = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(c).Append(json[++i]);
                        }
                        else
                        {
                            sb.Append(c).Append('\n').Append(' ', ++indent * 2);
                        }
                        break;
                    case '}':
                    case ']':
                        sb.Append('\n').Append(' ', --indent * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', indent * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }}
