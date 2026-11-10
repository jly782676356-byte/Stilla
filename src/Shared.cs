// 悬浮窗 (Stilla.exe) 和状态栏小程序 (Stilla.Statusline.exe) 共用的部分：文件放哪、状态栏数据怎么存。
// 这个文件不能依赖 WinForms，因为状态栏小程序是控制台程序（被 Claude Code 的 statusLine 当命令启动，要等它跑完、读它的输出）。
//
// 编译器是系统自带的 csc（C# 5），所以这里不用 ?. 和字符串插值。

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
        public const string Version = "0.1.0";
        public const string Disclaimer = "Stilla 是独立的社区项目，不是 Anthropic 出品，也没有得到 Anthropic 的认可。Claude 是 Anthropic, PBC 的商标。";

        // 项目的 GitHub 仓库：「检查更新」「前往更新页面」「捐赠」都指向这里。
        // 建好仓库后运行 tools\set-repo.ps1 -Owner 你的用户名，会把下面的 OWNER 和文档里的链接一起换掉。
        // 中文版和英文版共用同一个仓库：两种语言的安装包都发在同一个 Release 里（文件名带 -zh / -en）。
        public const string RepoOwner = "jly782676356-byte";
        public const string RepoName = "Stilla";

        // 还没换成真实用户名时，更新检查什么都不做（也不会联网）
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

        // 捐赠入口：现在指向 README 里的 #donate 小节；以后有了专门的捐赠页，只改这一行
        public static string DonateUrl
        {
            get { return RepoUrl + "#donate"; }
        }
    }

    // 所有文件放哪：设置、额度文件、日志。默认 %APPDATA%\Stilla；exe 旁有 portable.txt 时放到 exe 旁的 data 文件夹；
    // 环境变量 STILLA_HOME 可以指定别的位置（测试 / 截图用，不会碰到真实设置）。
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

        // 设置、额度文件、日志的根目录
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

        // 用量日志、额度快照、换算表等 CSV
        public static string DataDir
        {
            get { return Path.Combine(Home, "logs"); }
        }

        // 随程序带的 Python 脚本（exe 旁的 scripts 文件夹，没有就找 exe 同目录）
        public static string Script(string name)
        {
            string a = Path.Combine(Path.Combine(ExeDir, "scripts"), name);
            return File.Exists(a) ? a : Path.Combine(ExeDir, name);
        }

        // 后台跑 Python 脚本时给它指数据目录
        public static void ApplyScriptEnv(ProcessStartInfo psi)
        {
            psi.EnvironmentVariables["STILLA_DATA"] = DataDir;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        }
    }

    // 状态栏小程序：「Stilla.Statusline.exe [--quiet] [--chain "命令"]」
    // Claude Code 每次刷新状态栏都会启动一次，把一段 JSON 送到 stdin。要快，不弹窗，出错也不影响 Claude Code。
    internal static class StatusLineCli
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 额度存在这里；悬浮窗读它（StatusLineBridge.Read）
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
                    // 原来的状态栏命令存在文件里（安装程序写的），免得在命令行里转义引号
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
                // 存不下来就算了（不能影响 Claude Code）；排查问题时设环境变量 STILLA_DEBUG=1 会把原因写到 stderr
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

        // 把 rate_limits 存到 rate_limits.json。这次没带某个窗口时，旧的窗口只要还没到重置时间就保留
        private static void Save(Dictionary<string, object> input)
        {
            Dictionary<string, object> rl = Sub(input, "rate_limits");
            if (rl == null)
            {
                return;     // 还没有 API 响应、或者不是订阅账号：什么都不写
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
                // 悬浮窗正好在读：下次刷新再写
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

        // 状态栏上显示的一小段字：「5h 58% · 7d 61%」
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

        // 把 stdin 原样交给另一个状态栏命令，输出它的结果（这样不会顶掉你原来的状态栏）
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

    // Claude Code 的数据目录（里面有 projects\*.jsonl 会话记录、sessions\*.json、.credentials.json）。
    // 命令行、VS Code 扩展、JetBrains 插件都用同一个：环境变量 CLAUDE_CONFIG_DIR 指定的目录，没设就是 ~\.claude。
    // 除此之外还会自动找：~\.claude-work 这种多账号 / 多配置的并列目录、Claude 桌面端（含 Microsoft Store 版）在 AppData 里的目录；
    // 找不到的（WSL、别的盘、别人的奇怪位置）可以在悬浮窗右键菜单里手动加，存在 roots.txt 里，一行一个。
    internal static class ClaudeRoots
    {
        private static readonly object gate = new object();
        private static List<string> cache;
        private static DateTime cacheAt = DateTime.MinValue;
        private static bool refreshing;
        private static int generation;      // 手动加 / 清除目录时加一：这之前开始的后台检测结果作废，免得把刚加的目录冲掉

        private static readonly string[] SkipDirs =
        {
            "cache", "code cache", "gpucache", "blob_storage", "indexeddb", "local storage", "session storage", "logs",
            "crashpad", "network", "dawncache", "service worker", "shared dictionary", "node_modules"
        };

        // 主目录：和 Claude Code 自己用的一致
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
            // 文件刚写完时可能被杀毒软件 / 索引服务短暂占着：重试几次，实在删不掉就清空内容
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

        // 一个目录看起来像 Claude Code 的数据目录吗
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

        // 所有数据目录，第一个永远是主目录。拿到的是缓存（不会卡界面）；后台每 5 分钟重新检测一次
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

        // 登录凭据文件：先看主目录，再看别的目录
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

        // 演示模式 / 测试（STILLA_ISOLATED=1）：只读主目录，绝不去找别的目录，免得截图里出现真实会话
        public static bool Isolated
        {
            get { return AppPaths.Demo || Environment.GetEnvironmentVariable("STILLA_ISOLATED") == "1"; }
        }

        // 快：主目录 + 手动加的 + 用户目录下的 .claude* 兄弟目录（只列一层）
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

        // 慢：再去桌面端的目录里找名叫 .claude 的文件夹（桌面端把 Claude Code 的会话放在它自己的目录下，位置随版本变）
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
                // Microsoft Store 版把 AppData 虚拟化到 Packages\<包名>\LocalCache\Roaming 下面
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

    // 往 Claude Code 的 settings.json 里写 / 撤销 statusLine。只在用户明确同意时调用（安装程序里勾选、或设置对话框里点「自动写入」）。
    //   · 写之前先备份成 settings.json.stilla-backup-时间；JSON 解析不了就不动它；
    //   · 用户已经有自己的 statusLine：不覆盖，原命令存到 statusline-chain.txt，由 Stilla.Statusline.exe 转交（--chain-file），原来的状态栏照常显示；
    //   · 撤销时把原命令放回去。
    internal static class ClaudeSettings
    {
        private const string Marker = "Stilla.Statusline";

        // 用户原来的状态栏命令存在这里。每个 Claude 配置目录各存一份（不同目录的原命令可能不一样）
        public static string ChainFile(string root)
        {
            ulong h = 14695981039346656037UL;       // FNV-1a，只是为了给文件起个稳定的名字
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
                    error = "不是一个 JSON 对象";
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
                b = stamp + "-" + i;        // 同一秒里备份两次也不会冲突，更不会覆盖旧备份
            }
            File.Copy(path, b, false);
            return b;
        }

        // 返回给用户看的结果；ok = 是否成功（「已经设置过」也算成功）
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
                return "写入失败（没有破坏原文件，可以从备份恢复）：" + ex.Message;
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
                return path + " 不是有效的 JSON（" + err + "），没有改动。请按 README 手动加入 statusLine。";
            }
            string old = Cmd(doc);
            if (old != null && old.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ok = true;
                return "已经设置过了：" + path;
            }
            string backup = Backup(path);
            string command = "\"" + cliPath + "\" --quiet";
            string note = "";
            if (!string.IsNullOrEmpty(old) && old.Trim().Length > 0)
            {
                Directory.CreateDirectory(AppPaths.Home);
                File.WriteAllText(ChainFile(root), old, new UTF8Encoding(false));
                command += " --chain-file \"" + ChainFile(root) + "\"";
                note = "\n你原来的状态栏命令已保留（会转交给它）：" + old;
            }
            Dictionary<string, object> sl = new Dictionary<string, object>();
            object prev;
            Dictionary<string, object> pd;
            if (doc.TryGetValue("statusLine", out prev) && (pd = prev as Dictionary<string, object>) != null)
            {
                foreach (KeyValuePair<string, object> kv in pd)
                {
                    sl[kv.Key] = kv.Value;     // padding 之类的别的设置原样保留
                }
            }
            sl["type"] = "command";
            sl["command"] = command;
            doc["statusLine"] = sl;
            Save(path, doc);
            ok = true;
            return "已写入 " + path + (backup != null ? "\n备份：" + backup : "") + note;
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
                return "撤销失败：" + ex.Message;
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
                return "没有需要撤销的设置。";
            }
            string cur = Cmd(doc);
            if (cur == null || cur.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return "settings.json 里的状态栏不是 Stilla 设置的，没有动它。";
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
            return !string.IsNullOrEmpty(chain) ? "已撤销，原来的状态栏命令已放回去。" : "已撤销（删掉了 statusLine）。";
        }

        // 把紧凑的 JSON 排成缩进两格的样子（JavaScriptSerializer 只会输出一行）
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
