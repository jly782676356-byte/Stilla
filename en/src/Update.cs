// Update check: at startup (can be turned off in Settings) asks GitHub once "which version is the latest Release".
//
//   - It sends a single GET request to https://api.github.com/repos/<owner>/<repo>/releases/latest, carrying only the User-Agent "Stilla/<version>" and none of your data.
//   - When there is a new version: a small red dot appears to the right of the Stilla mark and the tray balloon notifies once; click the dot (or right-click -> Settings...) -> "Go to update page" to open the Release page in the browser.
//     Nothing is downloaded or installed automatically - whether to install is up to you.
//   - While the repository address is still the placeholder OWNER (AppInfo.Configured == false) it does nothing and uses no network.
//   - Demo mode (--demo) and the environment variable STILLA_NO_UPDATE_CHECK=1 also use no network.
//   - For tests: the environment variable STILLA_UPDATE_URL can point the request at a fake server on this machine (see CONTRIBUTING.md).
//
// The compiler is the csc that ships with the system (C# 5), so there is no ?. or string interpolation here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Stilla
{
    internal enum UpdateResult
    {
        None,           // Not checked yet
        Newer,          // A new version exists
        UpToDate,       // Up to date
        NotConfigured,  // The repository address is not configured / it was switched off
        Failed          // No network, rate-limited, the repository does not exist...
    }

    // Update state: stored in %APPDATA%\Stilla\update.txt (the latest version last known, and which version has already been announced)
    internal static class UpdateState
    {
        public static bool Auto = true;             // Check automatically at every launch (upd= in settings.txt, on by default)
        public static string Latest = "";           // The latest version number last known, such as "0.2.0"
        public static string Url = "";              // Update page
        public static string Notified = "";         // The version for which a tray balloon was already shown
        public static DateTime CheckedUtc = DateTime.MinValue;
        public static UpdateResult Last = UpdateResult.None;
        public static string Detail = "";           // Reason for the failure
        public static volatile bool Checking;

        // Is it newer than the current version (computed on the spot from the latest version last known: once the user installs the new version the red dot disappears by itself)
        public static bool Available
        {
            get { return Latest.Length > 0 && UpdateChecker.IsNewer(Latest, AppInfo.Version); }
        }

        public static string PageUrl
        {
            get { return Url.Length > 0 ? Url : AppInfo.ReleasesUrl; }
        }

        private static string FilePath
        {
            get { return Path.Combine(AppPaths.Home, "update.txt"); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "latest") Latest = UpdateChecker.CleanVersion(v);
                    else if (k == "url" && UpdateChecker.SafeUrl(v)) Url = v;
                    else if (k == "notified") Notified = UpdateChecker.CleanVersion(v);
                    else if (k == "checked")
                    {
                        long ticks;
                        if (long.TryParse(v, out ticks))
                        {
                            CheckedUtc = new DateTime(ticks, DateTimeKind.Utc);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Home);
                File.WriteAllLines(FilePath, new string[]
                {
                    "latest=" + Latest,
                    "url=" + Url,
                    "notified=" + Notified,
                    "checked=" + CheckedUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                }, new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }
    }

    internal static class UpdateChecker
    {
        // "v0.2.0", "0.2.0-beta.1" -> "0.2.0"; empty if it cannot be recognised
        public static string CleanVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return "";
            }
            int[] p = Parse(tag);
            if (p == null)
            {
                return "";
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < p.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('.');
                }
                sb.Append(p[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // Take the leading numeric part: "v1.2.3" -> [1,2,3]; "1.2" -> [1,2]
        public static int[] Parse(string tag)
        {
            if (tag == null)
            {
                return null;
            }
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(1);
            }
            List<int> parts = new List<int>();
            int i = 0;
            while (i < s.Length)
            {
                int start = i;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    i++;
                }
                if (i == start)
                {
                    break;
                }
                int n;
                if (!int.TryParse(s.Substring(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out n))
                {
                    return null;
                }
                parts.Add(n);
                if (i < s.Length && s[i] == '.' && i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '9')
                {
                    i++;
                    continue;
                }
                break;
            }
            return parts.Count > 0 ? parts.ToArray() : null;
        }

        public static bool IsNewer(string remote, string local)
        {
            int[] r = Parse(remote), l = Parse(local);
            if (r == null || l == null)
            {
                return false;
            }
            int n = Math.Max(r.Length, l.Length);
            for (int i = 0; i < n; i++)
            {
                int a = i < r.Length ? r[i] : 0, b = i < l.Length ? l[i] : 0;
                if (a != b)
                {
                    return a > b;
                }
            }
            return false;
        }

        // Only open GitHub pages (except the local test address): a link returned by the API cannot be trusted as is
        public static bool SafeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }
            if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return Environment.GetEnvironmentVariable("STILLA_UPDATE_URL") != null && url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static string ApiUrl()
        {
            string over = Environment.GetEnvironmentVariable("STILLA_UPDATE_URL");
            return string.IsNullOrEmpty(over) ? AppInfo.LatestApiUrl : over;
        }

        public static bool Enabled()
        {
            if (AppPaths.Demo || Environment.GetEnvironmentVariable("STILLA_NO_UPDATE_CHECK") == "1")
            {
                return false;
            }
            return AppInfo.Configured || Environment.GetEnvironmentVariable("STILLA_UPDATE_URL") != null;
        }

        // Check once, blocking (call it on a background thread). The result is written into UpdateState; the caller goes back to the UI thread to refresh
        public static UpdateResult CheckBlocking()
        {
            if (!Enabled())
            {
                UpdateState.Last = UpdateResult.NotConfigured;
                return UpdateState.Last;
            }
            UpdateState.Checking = true;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ApiUrl());
                req.Method = "GET";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "Stilla/" + AppInfo.Version;
                req.Accept = "application/vnd.github+json";
                string text;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                }
                Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                string tag = root != null && root.ContainsKey("tag_name") ? root["tag_name"] as string : null;
                string ver = CleanVersion(tag);
                if (ver.Length == 0)
                {
                    UpdateState.Detail = "bad-response";
                    UpdateState.Last = UpdateResult.Failed;
                    return UpdateState.Last;
                }
                bool draft = root.ContainsKey("draft") && root["draft"] is bool && (bool)root["draft"];
                bool pre = root.ContainsKey("prerelease") && root["prerelease"] is bool && (bool)root["prerelease"];
                if (draft || pre)
                {
                    ver = "";       // Drafts / pre-releases do not trigger a notice
                }
                string url = root.ContainsKey("html_url") ? root["html_url"] as string : null;
                UpdateState.CheckedUtc = DateTime.UtcNow;
                UpdateState.Url = SafeUrl(url) ? url : "";
                if (ver.Length > 0)
                {
                    UpdateState.Latest = ver;
                }
                UpdateState.Last = ver.Length > 0 && IsNewer(ver, AppInfo.Version) ? UpdateResult.Newer : UpdateResult.UpToDate;
                UpdateState.Detail = "";
                UpdateState.Save();
                return UpdateState.Last;
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                int code = r == null ? 0 : (int)r.StatusCode;
                if (code == 404)
                {
                    // The repository has not published a Release yet: not an error, treat it as up to date
                    UpdateState.CheckedUtc = DateTime.UtcNow;
                    UpdateState.Last = UpdateResult.UpToDate;
                    UpdateState.Detail = "no-release";
                    UpdateState.Save();
                    return UpdateState.Last;
                }
                UpdateState.Detail = code == 0 ? "network" : (code == 403 || code == 429 ? "rate-limit" : "http-" + code);
                UpdateState.Last = UpdateResult.Failed;
                return UpdateState.Last;
            }
            catch (Exception)
            {
                UpdateState.Detail = "bad-response";
                UpdateState.Last = UpdateResult.Failed;
                return UpdateState.Last;
            }
            finally
            {
                UpdateState.Checking = false;
            }
        }

        // Check once in the background; done is called on the background thread (use BeginInvoke yourself before touching the UI)
        public static void CheckAsync(int delayMs, Action done)
        {
            if (UpdateState.Checking)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }
                try
                {
                    CheckBlocking();
                }
                catch (Exception)
                {
                }
                if (done != null)
                {
                    done();
                }
            });
        }
    }
}
