// Claude Usage Tray: tray icon showing plan usage limits (5h session, weekly) for several
// Claude accounts. Each account is a Claude Code config dir with its own .credentials.json:
//   - main:   %CLAUDE_CONFIG_DIR% or %USERPROFILE%\.claude
//   - extras: %USERPROFILE%\.claude-contas\<name>\
// Build with build.ps1 (uses the C# 5 compiler that ships with .NET Framework 4.x).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Claude Usage Tray")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace ClaudeUsageTray
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "Local\\ClaudeUsageTray", out first))
            {
                if (!first) return;
                try { SetProcessDPIAware(); } catch { }
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    class Limit
    {
        public string Kind;
        public string Label;
        public double Percent;
        public DateTime? ResetsAt;

        // A window whose reset time already passed is effectively empty until the next poll.
        public double Effective
        {
            get { return ResetsAt.HasValue && ResetsAt.Value <= DateTime.Now ? 0 : Percent; }
        }
    }

    class Account
    {
        public string Dir;
        public string Folder;
        public bool IsMain;
        public bool HasCredentials;
        public string Email;
        public string Plan;
        public string ProfileToken;
        public List<Limit> Limits = new List<Limit>();
        public string Error;
        public readonly object FileLock = new object();
        public Dictionary<string, double> LastPct = new Dictionary<string, double>();

        public string Name { get { return Email ?? Folder; } }

        // After a login swap the two config dirs hold each other's account: swap what we know about them.
        public void SwapIdentity(Account other)
        {
            Swap(ref HasCredentials, ref other.HasCredentials);
            Swap(ref Email, ref other.Email);
            Swap(ref Plan, ref other.Plan);
            Swap(ref ProfileToken, ref other.ProfileToken);
            Swap(ref Limits, ref other.Limits);
            Swap(ref Error, ref other.Error);
            Swap(ref LastPct, ref other.LastPct);
        }

        static void Swap<T>(ref T x, ref T y)
        {
            T t = x; x = y; y = t;
        }

        // How close the account is to being blocked by any of its limits.
        public double Level
        {
            get { return Limits.Count == 0 ? 0 : Limits.Max(l => l.Effective); }
        }
    }

    static class Paths
    {
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string EnvConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        public static readonly string MainDir = string.IsNullOrEmpty(EnvConfigDir) ? Path.Combine(Home, ".claude") : EnvConfigDir;
        public static readonly string AccountsRoot = Path.Combine(Home, ".claude-contas");
        public static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray");

        // Claude Code keeps oauthAccount in the global config: <dir>/.claude.json when CLAUDE_CONFIG_DIR is set,
        // ~/.claude.json for the default dir; a legacy <dir>/.config.json wins when present.
        public static string GlobalConfig(string dir)
        {
            string legacy = Path.Combine(dir, ".config.json");
            if (File.Exists(legacy)) return legacy;
            bool defaultMain = string.IsNullOrEmpty(EnvConfigDir) && string.Equals(dir, MainDir, StringComparison.OrdinalIgnoreCase);
            return defaultMain ? Path.Combine(Home, ".claude.json") : Path.Combine(dir, ".claude.json");
        }
    }

    // Moves logins between two config dirs instead of copying them: a refresh token must live in exactly one
    // place, otherwise the first refresh rotates it and the other copy stops working.
    static class Switcher
    {
        public static void Swap(string mainCred, string mainGlobal, string otherCred, string otherGlobal)
        {
            if (!File.Exists(otherCred)) throw new ApiException("essa conta não tem login");
            string a = File.Exists(mainCred) ? File.ReadAllText(mainCred) : null;
            string b = File.ReadAllText(otherCred);
            Json.Parse(b);
            if (a != null) Json.Parse(a);

            string oauthA = File.Exists(mainGlobal) ? JsonText.Get(File.ReadAllText(mainGlobal), "oauthAccount") : null;
            string oauthB = File.Exists(otherGlobal) ? JsonText.Get(File.ReadAllText(otherGlobal), "oauthAccount") : null;

            // Main gets B first: until the next line B briefly exists twice, but A is never lost.
            Json.WriteTextAtomic(mainCred, b);
            if (a != null) Json.WriteTextAtomic(otherCred, a);
            else File.Delete(otherCred);

            // Re-read right before writing: Claude Code rewrites its global config often.
            if (oauthB != null && File.Exists(mainGlobal))
                Json.WriteTextAtomic(mainGlobal, JsonText.Set(File.ReadAllText(mainGlobal), "oauthAccount", oauthB));
            if (oauthA != null && File.Exists(otherGlobal))
                Json.WriteTextAtomic(otherGlobal, JsonText.Set(File.ReadAllText(otherGlobal), "oauthAccount", oauthA));
        }
    }

    // Edits one top-level key of a JSON text in place, leaving every other byte untouched
    // (~/.claude.json is large, written by Claude Code, and may hold keys that differ only in case).
    static class JsonText
    {
        public static string Get(string s, string key)
        {
            int start, end;
            return Find(s, key, out start, out end) ? s.Substring(start, end - start) : null;
        }

        public static string Set(string s, string key, string valueJson)
        {
            int start, end;
            if (Find(s, key, out start, out end)) return s.Substring(0, start) + valueJson + s.Substring(end);
            int close = s.LastIndexOf('}');
            string before = s.Substring(0, close).TrimEnd();
            return before + (before.EndsWith("{") ? "" : ",") + "\n  \"" + key + "\": " + valueJson + "\n" + s.Substring(close);
        }

        // Span of the value of the last top-level occurrence of key (JSON.parse keeps the last one).
        static bool Find(string s, string key, out int start, out int end)
        {
            start = end = -1;
            int i = SkipWs(s, 0);
            if (i >= s.Length || s[i] != '{') throw new FormatException("JSON não é um objeto");
            i++;
            while (true)
            {
                i = SkipWs(s, i);
                if (i >= s.Length) throw new FormatException("JSON truncado");
                if (s[i] == '}') return start >= 0;
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') throw new FormatException("chave esperada na posição " + i);
                int keyEnd = SkipString(s, i);
                string k = s.Substring(i + 1, keyEnd - i - 2);
                i = SkipWs(s, keyEnd);
                if (i >= s.Length || s[i] != ':') throw new FormatException("':' esperado na posição " + i);
                i = SkipWs(s, i + 1);
                int valueEnd = SkipValue(s, i);
                if (k == key) { start = i; end = valueEnd; }
                i = valueEnd;
            }
        }

        static int SkipWs(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        static int SkipString(string s, int i)
        {
            for (i++; i < s.Length; i++)
            {
                if (s[i] == '\\') i++;
                else if (s[i] == '"') return i + 1;
            }
            throw new FormatException("string sem fim");
        }

        static int SkipValue(string s, int i)
        {
            if (s[i] == '"') return SkipString(s, i);
            if (s[i] == '{' || s[i] == '[')
            {
                int depth = 0;
                for (; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '"') { i = SkipString(s, i) - 1; continue; }
                    if (c == '{' || c == '[') depth++;
                    else if ((c == '}' || c == ']') && --depth == 0) return i + 1;
                }
                throw new FormatException("objeto sem fim");
            }
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            return i;
        }
    }

    class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
    }

    static class Api
    {
        const string ApiBase = "https://api.anthropic.com";
        const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const string Relogin = "login expirou: clique para relogar";

        public static void Update(Account a)
        {
            string credPath = Path.Combine(a.Dir, ".credentials.json");
            a.HasCredentials = File.Exists(credPath);
            if (!a.HasCredentials)
            {
                a.Limits = new List<Limit>();
                a.Error = "sem login: clique para logar";
                return;
            }
            try
            {
                string token = GetToken(a, false);
                int status;
                string body = Http("GET", ApiBase + "/api/oauth/usage", token, null, out status);
                if (status == 401)
                {
                    token = GetToken(a, true);
                    body = Http("GET", ApiBase + "/api/oauth/usage", token, null, out status);
                }
                if (status == 401 || status == 403) throw new ApiException(Relogin);
                if (status == 429) throw new ApiException("muitas consultas (429), tenta de novo no próximo ciclo");
                if (status != 200) throw new ApiException("HTTP " + status + " ao ler uso");
                a.Limits = ParseUsage(Json.Parse(body));
                if (a.Email == null || a.ProfileToken != token) LoadProfile(a, token);
                a.Error = null;
            }
            catch (ApiException ex) { a.Error = ex.Message; }
            catch (WebException ex) { a.Error = "sem conexão (" + ex.Status + ")"; }
            catch (Exception ex) { a.Error = ex.Message; }
        }

        // Returns a valid access token, refreshing it (and writing it back so Claude Code sees it) when near expiry.
        internal static string GetToken(Account a, bool force, long marginMs = 5 * 60 * 1000)
        {
            lock (a.FileLock)
            {
                string path = Path.Combine(a.Dir, ".credentials.json");
                var root = Json.ReadFile(path);
                var oauth = Json.Get(root, "claudeAiOauth") as Dictionary<string, object>;
                if (oauth == null) throw new ApiException("credencial sem login do claude.ai: clique para logar");
                string access = Json.Str(oauth, "accessToken");
                string refresh = Json.Str(oauth, "refreshToken");
                a.Plan = Json.Str(oauth, "subscriptionType");
                if (!force && access != null && Json.Long(oauth, "expiresAt") - NowMs() > marginMs) return access;
                if (string.IsNullOrEmpty(refresh)) throw new ApiException(Relogin);

                var req = new Dictionary<string, object>();
                req["grant_type"] = "refresh_token";
                req["refresh_token"] = refresh;
                req["client_id"] = ClientId;
                var scopes = Json.Get(oauth, "scopes") as IEnumerable;
                if (scopes != null && !(scopes is string)) req["scope"] = string.Join(" ", scopes.Cast<object>());
                int status;
                string resp = Http("POST", TokenUrl, null, Json.Write(req), out status);
                if (status != 200)
                {
                    // Claude Code may have rotated the refresh token at the same time: use what is on disk now.
                    var again = Json.Get(Json.ReadFile(path), "claudeAiOauth") as Dictionary<string, object>;
                    if (again != null && Json.Str(again, "refreshToken") != refresh && Json.Long(again, "expiresAt") - NowMs() > 60 * 1000)
                        return Json.Str(again, "accessToken");
                    if (status == 400 || status == 401) throw new ApiException(Relogin);
                    throw new ApiException("falha ao renovar token (HTTP " + status + ")");
                }

                var r = Json.Parse(resp);
                access = Json.Str(r, "access_token");
                if (access == null) throw new ApiException("resposta de token inválida");
                long now = NowMs();
                oauth["accessToken"] = access;
                string newRefresh = Json.Str(r, "refresh_token");
                if (!string.IsNullOrEmpty(newRefresh)) oauth["refreshToken"] = newRefresh;
                long expiresIn = Json.Long(r, "expires_in");
                if (expiresIn > 0) oauth["expiresAt"] = now + expiresIn * 1000;
                long refreshExpiresIn = Json.Long(r, "refresh_token_expires_in");
                if (refreshExpiresIn > 0) oauth["refreshTokenExpiresAt"] = now + refreshExpiresIn * 1000;
                string scope = Json.Str(r, "scope");
                if (!string.IsNullOrEmpty(scope)) oauth["scopes"] = scope.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                Json.WriteFileAtomic(path, root);
                return access;
            }
        }

        static void LoadProfile(Account a, string token)
        {
            try
            {
                int status;
                string body = Http("GET", ApiBase + "/api/oauth/profile", token, null, out status);
                if (status != 200) return;
                var account = Json.Get(Json.Parse(body), "account") as Dictionary<string, object>;
                if (account != null) a.Email = Json.Str(account, "email");
                a.ProfileToken = token;
            }
            catch { }
        }

        static List<Limit> ParseUsage(Dictionary<string, object> usage)
        {
            var list = new List<Limit>();
            var limits = Json.Get(usage, "limits") as IEnumerable;
            if (limits != null && !(limits is string))
            {
                foreach (var item in limits)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    string kind = Json.Str(d, "kind") ?? "?";
                    list.Add(new Limit
                    {
                        Kind = kind,
                        Label = LabelFor(kind, Json.Get(d, "scope") as string),
                        Percent = Json.Double(d, "percent"),
                        ResetsAt = Json.Date(d, "resets_at")
                    });
                }
            }
            if (list.Count == 0)
            {
                AddWindow(list, usage, "five_hour", "session");
                AddWindow(list, usage, "seven_day", "weekly_all");
                AddWindow(list, usage, "seven_day_opus", "weekly_opus");
                AddWindow(list, usage, "seven_day_sonnet", "weekly_sonnet");
            }
            return list;
        }

        static void AddWindow(List<Limit> list, Dictionary<string, object> usage, string key, string kind)
        {
            var w = Json.Get(usage, key) as Dictionary<string, object>;
            if (w == null) return;
            list.Add(new Limit { Kind = kind, Label = LabelFor(kind, null), Percent = Json.Double(w, "utilization"), ResetsAt = Json.Date(w, "resets_at") });
        }

        static string LabelFor(string kind, string scope)
        {
            string s;
            if (kind == "session") s = "Sessão (5h)";
            else if (kind == "weekly_all") s = "Semanal";
            else if (kind.StartsWith("weekly_")) s = "Semanal " + Capitalize(kind.Substring(7).Replace('_', ' '));
            else s = Capitalize(kind.Replace('_', ' '));
            if (!string.IsNullOrEmpty(scope)) s += " · " + scope;
            return s;
        }

        static string Capitalize(string s)
        {
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        static string Http(string method, string url, string token, string body, out int status)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.UserAgent = "claude-usage-tray/1.0";
            req.Accept = "application/json";
            if (token != null)
            {
                req.Headers["Authorization"] = "Bearer " + token;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            }
            if (body != null)
            {
                req.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
            }
            using (resp)
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                status = (int)resp.StatusCode;
                return reader.ReadToEnd();
            }
        }
    }

    static class Json
    {
        static JavaScriptSerializer New()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        public static Dictionary<string, object> Parse(string s)
        {
            var d = New().DeserializeObject(s) as Dictionary<string, object>;
            if (d == null) throw new ApiException("JSON inesperado");
            return d;
        }

        public static string Write(object o)
        {
            return New().Serialize(o);
        }

        public static Dictionary<string, object> ReadFile(string path)
        {
            for (int i = 0; ; i++)
            {
                try { return Parse(File.ReadAllText(path, Encoding.UTF8)); }
                catch (IOException) { if (i >= 4) throw; Thread.Sleep(150); }
            }
        }

        public static void WriteFileAtomic(string path, object o)
        {
            WriteTextAtomic(path, Write(o));
        }

        public static void WriteTextAtomic(string path, string text)
        {
            string tmp = path + ".tray.tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            for (int i = 0; ; i++)
            {
                try { File.Replace(tmp, path, null); return; }
                catch (IOException) { if (i >= 4) throw; Thread.Sleep(150); }
            }
        }

        public static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            return Get(d, key) as string;
        }

        public static long Long(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v == null || v is string || v is bool) return 0;
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public static double Double(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v == null || v is string || v is bool) return 0;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static DateTime? Date(Dictionary<string, object> d, string key)
        {
            var s = Str(d, key);
            DateTimeOffset dto;
            if (s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto)) return dto.LocalDateTime;
            return null;
        }
    }

    static class Fmt
    {
        static readonly CultureInfo PtBr = new CultureInfo("pt-BR");

        public static string Pct(double p)
        {
            return Math.Round(p).ToString(CultureInfo.InvariantCulture) + "%";
        }

        public static string Reset(DateTime t)
        {
            var d = t - DateTime.Now;
            if (d.TotalSeconds <= 0) return "resetou";
            t = t.AddSeconds(30); // server times jitter around the minute (19:49:59.9 vs 19:50:00.4)
            if (d.TotalHours < 24) return t.ToString("HH:mm", PtBr) + " · " + Span(d);
            return t.ToString("ddd dd/MM HH:mm", PtBr);
        }

        static string Span(TimeSpan d)
        {
            if (d.TotalMinutes < 60) return Math.Ceiling(d.TotalMinutes) + "min";
            return (int)d.TotalHours + "h" + d.Minutes.ToString("00");
        }
    }

    class Settings
    {
        public int IntervalMin = 5;
        public bool Notify = true;
        public bool IsNew = true;

        static string FilePath { get { return Path.Combine(Paths.AppData, "settings.json"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                var d = Json.Parse(File.ReadAllText(FilePath));
                s.IsNew = false;
                long interval = Json.Long(d, "intervalMin");
                if (interval >= 1) s.IntervalMin = (int)interval;
                if (Json.Get(d, "notify") is bool) s.Notify = (bool)Json.Get(d, "notify");
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.AppData);
                var d = new Dictionary<string, object>();
                d["intervalMin"] = IntervalMin;
                d["notify"] = Notify;
                File.WriteAllText(FilePath, Json.Write(d), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "ClaudeUsageTray";

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(ValueName) != null;
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
        }
    }

    class Theme
    {
        public Color Bg, Text, Muted, Border, Track, Accent, Green, Amber, Red;

        // Tray flyouts follow the taskbar theme, not the app theme.
        public static Theme Current()
        {
            bool light = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    light = v is int && (int)v == 1;
                }
            }
            catch { }
            if (light)
                return new Theme
                {
                    Bg = Color.FromArgb(249, 249, 249), Text = Color.FromArgb(26, 26, 26), Muted = Color.FromArgb(96, 96, 96),
                    Border = Color.FromArgb(214, 214, 214), Track = Color.FromArgb(224, 224, 224), Accent = Color.FromArgb(0, 95, 184),
                    Green = Color.FromArgb(26, 127, 55), Amber = Color.FromArgb(154, 103, 0), Red = Color.FromArgb(207, 34, 46)
                };
            return new Theme
            {
                Bg = Color.FromArgb(32, 32, 32), Text = Color.FromArgb(240, 240, 240), Muted = Color.FromArgb(160, 160, 160),
                Border = Color.FromArgb(64, 64, 64), Track = Color.FromArgb(58, 58, 58), Accent = Color.FromArgb(76, 194, 255),
                Green = Color.FromArgb(63, 185, 80), Amber = Color.FromArgb(210, 153, 34), Red = Color.FromArgb(248, 81, 73)
            };
        }

        public Color ForLevel(double pct)
        {
            return pct >= 90 ? Red : pct >= 70 ? Amber : Green;
        }
    }

    class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        readonly NotifyIcon tray = new NotifyIcon();
        readonly PopupForm popup;
        readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer tickTimer = new System.Windows.Forms.Timer();
        readonly Dictionary<string, Account> known = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
        readonly Settings settings = Settings.Load();
        bool refreshQueued;
        Account pendingSwitch, suggested;
        Icon icon;

        public List<Account> Accounts = new List<Account>();
        public bool Refreshing;
        public DateTime? LastRefresh;
        public int IntervalMin { get { return settings.IntervalMin; } }

        public TrayApp()
        {
            // Start with Windows by default; after that respect the menu toggle, refreshing the path in case the exe moved.
            try
            {
                if (settings.IsNew) { Startup.Set(true); settings.Save(); }
                else if (Startup.IsEnabled()) Startup.Set(true);
            }
            catch { }

            popup = new PopupForm(this);
            tray.Text = "Claude · carregando...";
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) popup.Toggle(); };
            tray.BalloonTipClicked += (s, e) =>
            {
                var target = suggested;
                suggested = null;
                if (target != null) MakeActive(target);
            };
            UpdateTray();
            tray.Visible = true;

            pollTimer.Interval = settings.IntervalMin * 60000;
            pollTimer.Tick += (s, e) => Refresh();
            pollTimer.Start();
            tickTimer.Interval = 30000;
            tickTimer.Tick += (s, e) => OnTick();
            tickTimer.Start();
            Refresh();
        }

        public void Refresh()
        {
            if (Refreshing) { refreshQueued = true; return; }
            Refreshing = true;
            popup.Invalidate();
            var list = DiscoverAccounts();
            Task.Factory.StartNew(() => Parallel.ForEach(list, Api.Update))
                .ContinueWith(t => popup.BeginInvoke((Action)(() => AfterRefresh(list))));
        }

        void AfterRefresh(List<Account> list)
        {
            Refreshing = false;
            LastRefresh = DateTime.Now;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Accounts = list.Where(a => a.Email == null || seen.Add(a.Email)).ToList();
            Notify();
            UpdateTray();
            popup.Relayout();
            if (pendingSwitch != null)
            {
                var target = pendingSwitch;
                pendingSwitch = null;
                MakeActive(target);
            }
            else if (refreshQueued) { refreshQueued = false; Refresh(); }
        }

        // Makes `target` the account Claude Code uses by default, swapping its login with the main config dir.
        public void MakeActive(Account target)
        {
            Account main;
            if (!known.TryGetValue(Paths.MainDir, out main) || target == main || !target.HasCredentials) return;
            if (Refreshing) { pendingSwitch = target; return; }
            Refreshing = true; // keeps polls from reading the files while they move
            popup.Invalidate();
            string mainCred = Path.Combine(main.Dir, ".credentials.json");
            string targetCred = Path.Combine(target.Dir, ".credentials.json");
            Task.Factory.StartNew(() =>
            {
                lock (main.FileLock)
                lock (target.FileLock)
                {
                    // A token near expiry would make a running Claude Code refresh it in the middle of the swap.
                    try { if (File.Exists(mainCred)) Api.GetToken(main, false, 15 * 60 * 1000); } catch { }
                    Switcher.Swap(mainCred, Paths.GlobalConfig(main.Dir), targetCred, Paths.GlobalConfig(target.Dir));
                }
            }).ContinueWith(t => popup.BeginInvoke((Action)(() =>
            {
                Refreshing = false;
                refreshQueued = false;
                if (t.Exception != null)
                    Balloon("Troca de conta falhou", t.Exception.GetBaseException().Message, ToolTipIcon.Error, null);
                else
                {
                    main.SwapIdentity(target);
                    Balloon("Conta trocada", "Claude Code agora usa " + main.Name + ". Abra uma sessão nova; nas abertas confira com /status.", ToolTipIcon.Info, null);
                }
                UpdateTray();
                popup.Relayout();
                Refresh();
            })));
        }

        Account BestAlternative()
        {
            return Accounts.Where(a => !a.IsMain && a.HasCredentials && a.Limits.Count > 0 && a.Level < 90)
                .OrderBy(a => a.Level).FirstOrDefault();
        }

        static string Summary(Account a)
        {
            if (a.Limits.Count == 0) return a.HasCredentials ? (a.Error ?? "carregando") : "sem login";
            return string.Join(" · ", a.Limits.Select(l =>
                (l.Kind == "session" ? "5h" : l.Kind == "weekly_all" ? "semana" : l.Label) + " " + Fmt.Pct(l.Effective)));
        }

        void FillSwitchMenu(ToolStripMenuItem menu)
        {
            menu.DropDownItems.Clear();
            var best = BestAlternative();
            if (best != null)
            {
                menu.DropDownItems.Add("Conta com mais folga: " + best.Name + " (" + Fmt.Pct(best.Level) + ")", null, (s, e) => MakeActive(best));
                menu.DropDownItems.Add(new ToolStripSeparator());
            }
            foreach (var a in Accounts)
            {
                var target = a;
                var item = new ToolStripMenuItem(a.Name + "   —   " + Summary(a)) { Checked = a.IsMain, Enabled = a.IsMain || a.HasCredentials };
                if (!a.IsMain) item.Click += (s, e) => MakeActive(target);
                menu.DropDownItems.Add(item);
            }
        }

        void Balloon(string title, string text, ToolTipIcon kind, Account suggestion)
        {
            suggested = suggestion;
            if (text.Length > 250) text = text.Substring(0, 247) + "...";
            tray.ShowBalloonTip(8000, title, text, kind);
        }

        List<Account> DiscoverAccounts()
        {
            var dirs = new List<string> { Paths.MainDir };
            if (Directory.Exists(Paths.AccountsRoot))
                dirs.AddRange(Directory.GetDirectories(Paths.AccountsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            var list = new List<Account>();
            foreach (var dir in dirs)
            {
                Account a;
                if (!known.TryGetValue(dir, out a))
                {
                    bool main = dir == Paths.MainDir;
                    a = new Account { Dir = dir, IsMain = main, Folder = main ? "principal" : Path.GetFileName(dir) };
                    known[dir] = a;
                }
                list.Add(a);
            }
            return list;
        }

        void OnTick()
        {
            // Poll right after a window resets so the panel shows the fresh numbers.
            var now = DateTime.Now;
            bool resetPassed = Accounts.Any(a => a.Limits.Any(l =>
                l.ResetsAt.HasValue && l.ResetsAt.Value.AddSeconds(20) <= now && LastRefresh < l.ResetsAt.Value.AddSeconds(20)));
            if (resetPassed) Refresh();
            UpdateTray();
            if (popup.Visible) popup.Invalidate();
        }

        void Notify()
        {
            var lines = new List<string>();
            bool mainFilling = false;
            foreach (var a in Accounts)
            {
                foreach (var l in a.Limits)
                {
                    double prev;
                    bool had = a.LastPct.TryGetValue(l.Kind, out prev);
                    double pct = l.Effective;
                    a.LastPct[l.Kind] = pct;
                    if (!had) continue;
                    string when = l.ResetsAt.HasValue ? " · reseta " + Fmt.Reset(l.ResetsAt.Value) : "";
                    string line = null;
                    if (prev < 100 && pct >= 100) line = a.Name + ": " + l.Label + " esgotado" + when;
                    else if (prev < 80 && pct >= 80) line = a.Name + ": " + l.Label + " em " + Fmt.Pct(pct) + when;
                    else if (prev >= 80 && pct < 50) line = a.Name + ": " + l.Label + " liberado (" + Fmt.Pct(pct) + ")";
                    if (line == null) continue;
                    lines.Add(line);
                    if (a.IsMain && pct >= 80) mainFilling = true;
                }
            }
            if (lines.Count == 0 || !settings.Notify) return;
            var best = mainFilling ? BestAlternative() : null;
            if (best != null) lines.Insert(0, "Clique aqui para trocar para " + best.Name + " (" + Fmt.Pct(best.Level) + ")");
            Balloon("Uso do Claude", string.Join("\n", lines), ToolTipIcon.Info, best);
        }

        void UpdateTray()
        {
            var withData = Accounts.Where(a => a.Limits.Count > 0).ToList();
            string tip;
            if (Accounts.Count == 0) tip = "Claude · nenhuma conta";
            else if (withData.Count == 0) tip = Refreshing || LastRefresh == null ? "Claude · carregando..." : "Claude · erro ao ler uso";
            else
            {
                int free = withData.Count(a => a.Level < 90);
                tip = string.Format("Claude · {0}/{1} contas com folga · menor uso {2}", free, Accounts.Count, Fmt.Pct(withData.Min(a => a.Level)));
            }
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

            var old = icon;
            icon = DrawIcon(Accounts);
            tray.Icon = icon;
            if (old != null) old.Dispose();
        }

        // One vertical bar per account, filled by its most constrained limit.
        static Icon DrawIcon(List<Account> accounts)
        {
            int s = SystemInformation.SmallIconSize.Width;
            var theme = Theme.Current();
            var shown = accounts.Take(8).ToList();
            using (var bmp = new Bitmap(s, s))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    if (shown.Count == 0)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var pen = new Pen(Color.Gray, Math.Max(1.5f, s / 10f)))
                            g.DrawEllipse(pen, s * 0.15f, s * 0.15f, s * 0.7f, s * 0.7f);
                    }
                    else
                    {
                        int n = shown.Count;
                        int gap = n > 1 ? 1 : 0;
                        int bw = Math.Min(Math.Max(1, (s - gap * (n - 1)) / n), s * 2 / 5);
                        int x = (s - (bw * n + gap * (n - 1))) / 2;
                        using (var track = new SolidBrush(Color.FromArgb(110, 128, 128, 128)))
                        {
                            foreach (var a in shown)
                            {
                                g.FillRectangle(track, x, 0, bw, s);
                                if (a.Limits.Count > 0)
                                {
                                    double level = Math.Min(100, a.Level);
                                    int h = Math.Max(1, (int)Math.Round(s * level / 100));
                                    using (var fill = new SolidBrush(theme.ForLevel(level)))
                                        g.FillRectangle(fill, x, s - h, bw, h);
                                }
                                x += bw + gap;
                            }
                        }
                    }
                }
                IntPtr h2 = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h2).Clone(); }
                finally { DestroyIcon(h2); }
            }
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Abrir painel", null, (s, e) => popup.Toggle());
            var switchMenu = new ToolStripMenuItem("Trocar conta do Claude Code");
            switchMenu.DropDownItems.Add("..."); // placeholder so the submenu arrow shows; filled on open
            switchMenu.DropDownOpening += (s, e) => FillSwitchMenu(switchMenu);
            m.Items.Add(switchMenu);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Atualizar agora", null, (s, e) => Refresh());
            m.Items.Add("Adicionar conta...", null, (s, e) => AddAccount());
            m.Items.Add("Abrir pasta das contas", null, (s, e) =>
            {
                Directory.CreateDirectory(Paths.AccountsRoot);
                Process.Start("explorer.exe", "\"" + Paths.AccountsRoot + "\"");
            });
            m.Items.Add(new ToolStripSeparator());

            var interval = new ToolStripMenuItem("Atualizar a cada");
            foreach (int min in new[] { 2, 5, 10, 15, 30 })
            {
                var item = new ToolStripMenuItem(min + " min") { Tag = min };
                item.Click += (s, e) =>
                {
                    settings.IntervalMin = (int)((ToolStripMenuItem)s).Tag;
                    settings.Save();
                    pollTimer.Interval = settings.IntervalMin * 60000;
                    popup.Invalidate();
                };
                interval.DropDownItems.Add(item);
            }
            interval.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripMenuItem item in interval.DropDownItems) item.Checked = (int)item.Tag == settings.IntervalMin;
            };
            m.Items.Add(interval);

            var notify = new ToolStripMenuItem("Notificações") { CheckOnClick = true, Checked = settings.Notify };
            notify.CheckedChanged += (s, e) => { settings.Notify = notify.Checked; settings.Save(); };
            m.Items.Add(notify);

            var startup = new ToolStripMenuItem("Iniciar com o Windows") { CheckOnClick = true, Checked = Startup.IsEnabled() };
            startup.CheckedChanged += (s, e) => Startup.Set(startup.Checked);
            m.Items.Add(startup);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Sair", null, (s, e) => ExitThread());
            return m;
        }

        public void AddAccount()
        {
            int n = Directory.Exists(Paths.AccountsRoot) ? Directory.GetDirectories(Paths.AccountsRoot).Length : 0;
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Nome curto para a conta (vira a pasta em " + Paths.AccountsRoot + "):",
                "Adicionar conta Claude", "conta" + (n + 2));
            name = Regex.Replace(name.Trim().ToLowerInvariant().Replace(' ', '-'), "[^a-z0-9_-]", "");
            if (name.Length == 0) return;
            string dir = Path.Combine(Paths.AccountsRoot, name);
            if (File.Exists(Path.Combine(dir, ".credentials.json")))
            {
                MessageBox.Show("Já existe uma conta logada em " + dir, "Adicionar conta Claude");
                return;
            }
            Directory.CreateDirectory(dir);
            Login(dir, false);
            Refresh();
        }

        // Opens a console running `claude auth login` against the account's config dir.
        void Login(string dir, bool isMain)
        {
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo("cmd.exe", "/c title Login Claude - " + (isMain ? "principal" : Path.GetFileName(dir)) + " & claude auth login & echo. & pause");
            psi.UseShellExecute = false;
            if (isMain && string.IsNullOrEmpty(Paths.EnvConfigDir)) psi.EnvironmentVariables.Remove("CLAUDE_CONFIG_DIR");
            else psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = dir;
            var p = Process.Start(psi);
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) => popup.BeginInvoke((Action)Refresh);
        }

        static string UseCommand(Account a)
        {
            if (a.IsMain && string.IsNullOrEmpty(Paths.EnvConfigDir))
                return "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; claude";
            return "$env:CLAUDE_CONFIG_DIR = \"" + a.Dir + "\"; claude";
        }

        public void ShowAccountMenu(Account a, Control owner, Point location)
        {
            var m = new ContextMenuStrip();
            var use = m.Items.Add(a.IsMain ? "Já é a conta do Claude Code" : "Usar no Claude Code (trocar conta)", null, (s, e) => MakeActive(a));
            use.Enabled = !a.IsMain && a.HasCredentials;
            use.Font = new Font(use.Font, FontStyle.Bold);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(a.HasCredentials ? "Relogar esta conta" : "Fazer login nesta conta", null, (s, e) => Login(a.Dir, a.IsMain));
            m.Items.Add("Copiar comando para usar no terminal (PowerShell)", null, (s, e) => Clipboard.SetText(UseCommand(a)));
            m.Items.Add("Abrir pasta da conta", null, (s, e) => Process.Start("explorer.exe", "\"" + a.Dir + "\""));
            m.Closed += (s, e) => owner.BeginInvoke((Action)m.Dispose);
            popup.MenuOpen = true;
            m.Closed += (s, e) => popup.MenuOpen = false;
            m.Show(owner, location);
        }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            tray.Dispose();
            popup.Dispose();
            base.ExitThreadCore();
        }
    }

    class PopupForm : Form
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Layout in logical (96 dpi) pixels.
        const float W = 440, Pad = 16, HeaderH = 28, NameH = 24, LimitH = 22, NoteH = 18, Gap = 10, FooterH = 34;
        const float LabelW = 100, PctW = 44, ResetW = 116;

        readonly TrayApp app;
        readonly float k;
        readonly Font titleFont = new Font("Segoe UI Semibold", 10f);
        readonly Font nameFont = new Font("Segoe UI Semibold", 9f);
        readonly Font bodyFont = new Font("Segoe UI", 9f);
        readonly Font smallFont = new Font("Segoe UI", 8f);
        readonly List<KeyValuePair<Rectangle, Account>> rows = new List<KeyValuePair<Rectangle, Account>>();
        Theme theme = Theme.Current();
        Rectangle refreshRect, addRect;
        DateTime hiddenAt;
        public bool MenuOpen;

        public PopupForm(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            Text = "Uso do Claude";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            Relayout();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: keep out of Alt+Tab
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Windows 11 rounded corners
        }

        int S(float v) { return (int)Math.Round(v * k); }

        public void Toggle()
        {
            if (Visible) { HidePopup(); return; }
            // The tray click that deactivated (and hid) the popup must not reopen it.
            if ((DateTime.Now - hiddenAt).TotalMilliseconds < 400) return;
            theme = Theme.Current();
            Relayout();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - S(12), area.Bottom - Height - S(12));
            Show();
            Activate();
        }

        void HidePopup()
        {
            Hide();
            hiddenAt = DateTime.Now;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Visible && !MenuOpen) HidePopup();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) HidePopup();
            if (e.KeyCode == Keys.F5) app.Refresh();
        }

        float AccountHeight(Account a)
        {
            float h = NameH + Math.Max(1, a.Limits.Count) * LimitH;
            if (a.Limits.Count > 0 && a.Error != null) h += NoteH;
            return h + Gap;
        }

        public void Relayout()
        {
            float h = Pad + HeaderH + 6;
            if (app.Accounts.Count == 0) h += LimitH;
            foreach (var a in app.Accounts) h += AccountHeight(a);
            h += FooterH + Pad / 2;
            var size = new Size(S(W), S(h));
            if (Visible && size.Height != Height) Top += Height - size.Height; // grow upward, stay above the taskbar
            Size = size;
            Invalidate();
        }

        void Text_(Graphics g, string s, Font f, Color c, float x, float y, float w, float h, TextFormatFlags align)
        {
            TextRenderer.DrawText(g, s, f, new Rectangle(S(x), S(y), S(w), S(h)), c,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var t = theme;
            g.Clear(t.Bg);
            using (var pen = new Pen(t.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            rows.Clear();

            float y = Pad;
            Text_(g, "Uso das contas Claude", titleFont, t.Text, Pad, y, W - 2 * Pad - 110, HeaderH, TextFormatFlags.Left);
            refreshRect = new Rectangle(S(W - Pad - 110), S(y), S(110), S(HeaderH));
            Text_(g, app.Refreshing ? "atualizando..." : "Atualizar (F5)", bodyFont, app.Refreshing ? t.Muted : t.Accent, W - Pad - 110, y, 110, HeaderH, TextFormatFlags.Right);
            y += HeaderH + 6;

            if (app.Accounts.Count == 0)
            {
                Text_(g, app.Refreshing ? "carregando..." : "Nenhuma conta encontrada.", bodyFont, t.Muted, Pad, y, W - 2 * Pad, LimitH, TextFormatFlags.Left);
                y += LimitH;
            }

            using (var divider = new Pen(t.Border))
            {
                for (int i = 0; i < app.Accounts.Count; i++)
                {
                    var a = app.Accounts[i];
                    float h = AccountHeight(a);
                    if (i > 0) g.DrawLine(divider, S(Pad), S(y - Gap / 2), S(W - Pad), S(y - Gap / 2));
                    rows.Add(new KeyValuePair<Rectangle, Account>(new Rectangle(0, S(y - Gap / 2), Width, S(h)), a));

                    string badge = a.Plan ?? "";
                    if (a.IsMain) badge = badge.Length > 0 ? badge + " · em uso" : "em uso";
                    Text_(g, badge, smallFont, t.Muted, W - Pad - 120, y, 120, NameH, TextFormatFlags.Right);
                    Text_(g, a.Name, nameFont, t.Text, Pad, y, W - 2 * Pad - 124, NameH, TextFormatFlags.Left);
                    y += NameH;

                    if (a.Limits.Count == 0)
                    {
                        Text_(g, a.Error ?? "carregando...", bodyFont, a.Error != null ? t.Amber : t.Muted, Pad, y, W - 2 * Pad, LimitH, TextFormatFlags.Left);
                        y += LimitH;
                    }
                    foreach (var l in a.Limits)
                    {
                        DrawLimit(g, l, y);
                        y += LimitH;
                    }
                    if (a.Limits.Count > 0 && a.Error != null)
                    {
                        Text_(g, "dado antigo: " + a.Error, smallFont, t.Amber, Pad, y, W - 2 * Pad, NoteH, TextFormatFlags.Left);
                        y += NoteH;
                    }
                    y += Gap;
                }

                g.DrawLine(divider, S(Pad), S(y - Gap / 2), S(W - Pad), S(y - Gap / 2));
            }

            addRect = new Rectangle(S(Pad), S(y), S(140), S(FooterH));
            Text_(g, "+ Adicionar conta", bodyFont, t.Accent, Pad, y, 140, FooterH, TextFormatFlags.Left);
            string footer = (app.LastRefresh.HasValue ? "atualizado " + app.LastRefresh.Value.ToString("HH:mm") : "aguardando") + " · a cada " + app.IntervalMin + " min";
            Text_(g, footer, smallFont, t.Muted, W - Pad - 240, y, 240, FooterH, TextFormatFlags.Right);
        }

        void DrawLimit(Graphics g, Limit l, float y)
        {
            var t = theme;
            double pct = l.Effective;
            float x = Pad;
            Text_(g, l.Label, bodyFont, t.Muted, x, y, LabelW, LimitH, TextFormatFlags.Left);
            x += LabelW + 4;

            float barW = W - 2 * Pad - LabelW - 4 - PctW - ResetW - 8;
            float barH = 6, barY = y + (LimitH - barH) / 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(t.Track)) FillRounded(g, track, S(x), S(barY), S(barW), S(barH));
            float fillW = (float)(barW * Math.Min(100, Math.Max(0, pct)) / 100);
            if (fillW > 0)
                using (var fill = new SolidBrush(t.ForLevel(pct))) FillRounded(g, fill, S(x), S(barY), Math.Max(S(barH), S(fillW)), S(barH));
            g.SmoothingMode = SmoothingMode.None;
            x += barW;

            Text_(g, Fmt.Pct(pct), bodyFont, t.Text, x, y, PctW, LimitH, TextFormatFlags.Right);
            x += PctW + 8;
            Text_(g, l.ResetsAt.HasValue ? Fmt.Reset(l.ResetsAt.Value) : "", smallFont, t.Muted, x, y, ResetW, LimitH, TextFormatFlags.Right);
        }

        static void FillRounded(Graphics g, Brush b, float x, float y, float w, float h)
        {
            float r = h;
            using (var path = new GraphicsPath())
            {
                path.AddArc(x, y, r, r, 90, 180);
                path.AddArc(x + w - r, y, r, r, 270, 180);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        Account RowAt(Point p)
        {
            foreach (var row in rows) if (row.Key.Contains(p)) return row.Value;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool link = refreshRect.Contains(e.Location) || addRect.Contains(e.Location) || RowAt(e.Location) != null;
            Cursor = link ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (refreshRect.Contains(e.Location)) { app.Refresh(); return; }
            if (addRect.Contains(e.Location)) { HidePopup(); app.AddAccount(); return; }
            var a = RowAt(e.Location);
            if (a != null) app.ShowAccountMenu(a, this, e.Location);
        }
    }
}
