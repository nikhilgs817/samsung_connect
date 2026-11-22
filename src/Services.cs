using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SamsungCONNECT
{
    sealed class ServiceInfo
    {
        internal string Id, Name, Url, Section, Bg, Fg, Mark;
        internal bool Custom;
    }

    static class Catalog
    {
        internal const string MyTiles = "My tiles";
        internal static readonly string[] Sections = { "Popular", "Regional", "More to watch", "Music", MyTiles };

        static ServiceInfo S(string id, string name, string url, string section, string bg, string fg, string mark)
        {
            return new ServiceInfo { Id = id, Name = name, Url = url, Section = section, Bg = bg, Fg = fg, Mark = mark };
        }

        internal static List<ServiceInfo> BuiltIn()
        {
            return new List<ServiceInfo>
            {
                S("netflix", "Netflix", "https://www.netflix.com/browse", "Popular", "#000000", "#E50914", "N"),
                S("prime", "Prime Video", "https://www.primevideo.com/", "Popular", "#0F171E", "#1A98FF", "prime"),
                S("hotstar", "JioHotstar", "https://www.hotstar.com/in", "Popular", "#0C1F4A", "#FFFFFF", "JH"),
                S("youtube", "YouTube", "https://www.youtube.com/", "Popular", "#FF0000", "#FFFFFF", "▶"),
                S("zee5", "ZEE5", "https://www.zee5.com/", "Popular", "#8230C6", "#FFFFFF", "ZEE5"),
                S("sonyliv", "SonyLIV", "https://www.sonyliv.com/", "Popular", "#111111", "#F2B53A", "LIV"),
                S("jiotv", "JioTV", "https://www.jiotv.com/", "Popular", "#D9008D", "#FFFFFF", "JioTV"),
                S("aha", "aha", "https://www.aha.video/", "Popular", "#FF6D00", "#FFFFFF", "aha"),

                S("sunnxt", "Sun NXT", "https://www.sunnxt.com/", "Regional", "#F15A22", "#FFFFFF", "SUN"),
                S("etvwin", "ETV Win", "https://www.etvwin.com/", "Regional", "#1E2A78", "#FFD200", "WIN"),
                S("hoichoi", "hoichoi", "https://www.hoichoi.tv/", "Regional", "#E6002D", "#FFFFFF", "hoi"),
                S("manoramamax", "Manorama Max", "https://www.manoramamax.com/", "Regional", "#0A0A0A", "#F5C400", "MAX"),
                S("chaupal", "Chaupal", "https://www.chaupal.com/", "Regional", "#E0222F", "#FFFFFF", "CH"),
                S("shemaroome", "ShemarooMe", "https://www.shemaroome.com/", "Regional", "#F9C800", "#1A1A1A", "S"),

                S("appletv", "Apple TV+", "https://tv.apple.com/in", "More to watch", "#000000", "#FFFFFF", "tv+"),
                S("mxplayer", "Amazon MX Player", "https://www.mxplayer.in/", "More to watch", "#0B5CFF", "#FFFFFF", "MX"),
                S("lionsgate", "Lionsgate Play", "https://www.lionsgateplay.com/", "More to watch", "#000000", "#F2C200", "LP"),
                S("airtel", "Airtel Xstream", "https://www.airtelxstream.in/", "More to watch", "#E40000", "#FFFFFF", "X"),
                S("tataplay", "Tata Play Binge", "https://www.tataplaybinge.com/", "More to watch", "#220046", "#E10092", "B"),
                S("fancode", "FanCode", "https://www.fancode.com/", "More to watch", "#111111", "#FF5A00", "FC"),

                S("ytmusic", "YouTube Music", "https://music.youtube.com/", "Music", "#FF0000", "#FFFFFF", "♫"),
                S("spotify", "Spotify", "https://open.spotify.com/", "Music", "#1DB954", "#000000", "S"),
                S("jiosaavn", "JioSaavn", "https://www.jiosaavn.com/", "Music", "#2BC5B4", "#FFFFFF", "JS"),
            };
        }

        // Accepts "example.com" or a full http(s) link. Returns null for anything else.
        internal static Uri ParseUserUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.Length > 2048) return null;
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) text = "https://" + text;
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
            if (uri.Host.IndexOf('.') < 0 && !uri.IsLoopback) return null;
            return uri;
        }

        internal static ServiceInfo MakeCustom(string name, Uri url)
        {
            string label = name.Trim();
            string mark = label.Length <= 3 ? label : label.Substring(0, 1).ToUpperInvariant();
            string[] palette = { "#5B6CFF", "#00A389", "#E0475B", "#F08C00", "#7C4DFF", "#1E88E5" };
            int pick = Math.Abs(url.Host.GetHashCode()) % palette.Length;
            return new ServiceInfo { Id = "custom:" + url.AbsoluteUri, Name = label, Url = url.AbsoluteUri, Section = MyTiles,
                Bg = palette[pick], Fg = "#FFFFFF", Mark = mark, Custom = true };
        }

        // "Name<TAB>URL" per entry; anything invalid is dropped.
        internal static string Encode(ServiceInfo s) { return s.Name.Replace("\t", " ") + "\t" + s.Url; }
        internal static ServiceInfo Decode(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int tab = line.IndexOf('\t');
            if (tab <= 0) return null;
            string name = line.Substring(0, tab).Trim();
            Uri url = ParseUserUrl(line.Substring(tab + 1));
            if (name.Length == 0 || name.Length > 40 || url == null) return null;
            return MakeCustom(name, url);
        }
    }

    // Per-user settings in HKCU\Software\SamsungCONNECT.
    static class Settings
    {
        internal const string Path = @"Software\SamsungCONNECT";
        internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static object Read(string name)
        {
            try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Path)) return key == null ? null : key.GetValue(name); }
            catch { return null; }
        }
        static void Write(string name, object value, RegistryValueKind kind)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Path)) key.SetValue(name, value, kind);
        }
        internal static string GetString(string name, string fallback) { string v = Read(name) as string; return v ?? fallback; }
        internal static void SetString(string name, string value) { Write(name, value ?? "", RegistryValueKind.String); }
        internal static bool GetBool(string name, bool fallback) { object v = Read(name); return v is int ? (int)v != 0 : fallback; }
        internal static void SetBool(string name, bool value) { Write(name, value ? 1 : 0, RegistryValueKind.DWord); }
        internal static List<string> GetList(string name)
        {
            string[] v = Read(name) as string[];
            return v == null ? new List<string>() : new List<string>(v);
        }
        internal static void SetList(string name, List<string> values) { Write(name, values.ToArray(), RegistryValueKind.MultiString); }

        internal static string TvName
        {
            get { string v = GetString("TVName", "SAMSUNG"); return SafetyPolicy.ValidName(v) ? v.Trim() : "SAMSUNG"; }
        }
        internal static string Theme { get { return GetString("Theme", "System"); } set { SetString("Theme", value); } }
        internal static string LastMethod { get { return GetString("LastMethod", "Wireless"); } set { SetString("LastMethod", value); } }
        internal static bool AutoConnect { get { return GetBool("AutoConnect", false); } set { SetBool("AutoConnect", value); } }
        internal static bool LidOnTv { get { return GetBool("LidOnTv", true); } set { SetBool("LidOnTv", value); } }

        internal static List<ServiceInfo> CustomTiles()
        {
            List<ServiceInfo> result = new List<ServiceInfo>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string line in GetList("CustomTiles"))
            {
                ServiceInfo s = Catalog.Decode(line);
                if (s != null && seen.Add(s.Id)) result.Add(s);
            }
            return result;
        }
        internal static void SaveCustomTiles(List<ServiceInfo> tiles)
        {
            List<string> lines = new List<string>();
            foreach (ServiceInfo s in tiles) lines.Add(Catalog.Encode(s));
            SetList("CustomTiles", lines);
        }

        internal static bool StartsWithWindows()
        {
            try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunPath)) return key != null && key.GetValue("SamsungCONNECT") != null; }
            catch { return false; }
        }
        internal static void SetStartsWithWindows(bool enabled, string exePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath))
            {
                if (enabled) key.SetValue("SamsungCONNECT", "\"" + exePath + "\" --startup");
                else key.DeleteValue("SamsungCONNECT", false);
            }
        }
    }

    // Downloads each service's own icon once and caches it as a re-encoded PNG.
    sealed class LogoCache
    {
        readonly string dir;
        readonly Queue<ServiceInfo> queue = new Queue<ServiceInfo>();
        readonly HashSet<string> queued = new HashSet<string>();
        int workers;
        internal event Action<string> Updated; // service id, raised on a worker thread

        internal LogoCache()
        {
            dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamsungCONNECT", "logos");
        }
        internal string Folder { get { return dir; } }

        string FileFor(string id, string ext)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(id));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 10; i++) sb.Append(hash[i].ToString("x2"));
                return System.IO.Path.Combine(dir, sb + ext);
            }
        }

        internal BitmapSource Load(string id)
        {
            string file = FileFor(id, ".png");
            if (!File.Exists(file)) return null;
            try
            {
                BitmapImage image = new BitmapImage();
                using (FileStream fs = File.OpenRead(file))
                {
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = fs; image.EndInit();
                }
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        internal void Store(string id, byte[] png)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(FileFor(id, ".png"), png);
            TryDelete(FileFor(id, ".fail"));
        }

        internal void Fetch(IEnumerable<ServiceInfo> services, bool force)
        {
            try { Directory.CreateDirectory(dir); } catch { return; }
            lock (queue)
            {
                foreach (ServiceInfo s in services)
                {
                    string png = FileFor(s.Id, ".png"), fail = FileFor(s.Id, ".fail");
                    if (force) { TryDelete(png); TryDelete(fail); }
                    else if (File.Exists(png)) continue;
                    else if (File.Exists(fail) && DateTime.UtcNow - File.GetLastWriteTimeUtc(fail) < TimeSpan.FromDays(3)) continue;
                    if (queued.Add(s.Id)) queue.Enqueue(s);
                }
                while (workers < 3 && workers < queue.Count)
                {
                    workers++;
                    Thread t = new Thread(Work) { IsBackground = true, Name = "Logo fetch" };
                    t.SetApartmentState(ApartmentState.STA); // WPF imaging objects expect a dispatcher thread.
                    t.Start();
                }
            }
        }
        static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }

        void Work()
        {
            while (true)
            {
                ServiceInfo s;
                lock (queue)
                {
                    if (queue.Count == 0) { workers--; return; }
                    s = queue.Dequeue();
                }
                bool ok = false;
                try
                {
                    byte[] png = LogoFetcher.Fetch(s.Url);
                    if (png != null) { File.WriteAllBytes(FileFor(s.Id, ".png"), png); ok = true; }
                }
                catch { }
                if (!ok) { try { File.WriteAllText(FileFor(s.Id, ".fail"), DateTime.UtcNow.ToString("o")); } catch { } }
                lock (queue) queued.Remove(s.Id);
                if (ok && Updated != null) { try { Updated(s.Id); } catch { } }
            }
        }
    }

    static class LogoFetcher
    {
        const int MaxPage = 1500000, MaxImage = 2000000;
        static readonly Regex LinkTag = new Regex(@"<link\b[^>]*>", RegexOptions.IgnoreCase);
        static readonly Regex Attr = new Regex(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        static readonly Regex JsonObject = new Regex(@"\{[^{}]*\}");
        static readonly Regex JsonSrc = new Regex(@"""src""\s*:\s*""([^""]+)""");
        static readonly Regex JsonSizes = new Regex(@"""sizes""\s*:\s*""(\d+)x\d+");

        sealed class Candidate { internal Uri Url; internal int Score; }

        static LogoFetcher()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); } // TLS 1.2 + 1.3
            catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
        }

        internal static byte[] Fetch(string pageUrl)
        {
            Uri page = new Uri(pageUrl);
            List<Candidate> candidates = new List<Candidate>();
            Uri finalPage = page;
            try
            {
                byte[] html = Get(page, MaxPage, out finalPage);
                if (html != null) Collect(Encoding.UTF8.GetString(html), finalPage, candidates);
            }
            catch { /* Fall back to well-known icon paths. */ }
            Uri origin = new Uri(finalPage.GetLeftPart(UriPartial.Authority) + "/");
            candidates.Add(new Candidate { Url = new Uri(origin, "/apple-touch-icon.png"), Score = 179 });
            candidates.Add(new Candidate { Url = new Uri(origin, "/favicon.ico"), Score = 16 });
            candidates.Sort(delegate(Candidate a, Candidate b) { return b.Score.CompareTo(a.Score); });

            BitmapSource best = null;
            HashSet<string> tried = new HashSet<string>();
            int attempts = 0;
            foreach (Candidate c in candidates)
            {
                if (attempts >= 7) break;
                if (c.Url.Scheme != Uri.UriSchemeHttps || !tried.Add(c.Url.AbsoluteUri)) continue;
                attempts++;
                BitmapSource image = Decode(c.Url);
                if (image == null) continue;
                if (best == null || image.PixelWidth > best.PixelWidth) best = image;
                if (best.PixelWidth >= 120) break;
            }
            if (best == null || best.PixelWidth < 32) return null;
            return EncodePng(best);
        }

        static void Collect(string html, Uri baseUri, List<Candidate> list)
        {
            foreach (Match tag in LinkTag.Matches(html))
            {
                string rel = null, href = null, sizes = null;
                foreach (Match a in Attr.Matches(tag.Value))
                {
                    string name = a.Groups[1].Value.ToLowerInvariant();
                    string value = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
                    if (name == "rel") rel = value.ToLowerInvariant();
                    else if (name == "href") href = value;
                    else if (name == "sizes") sizes = value;
                }
                if (rel == null || string.IsNullOrEmpty(href) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                Uri url;
                if (!Uri.TryCreate(baseUri, href, out url)) continue;
                if (rel.Contains("manifest")) { CollectManifest(url, list); continue; }
                if (url.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                int size = ParseSize(sizes);
                if (rel.Contains("apple-touch-icon")) list.Add(new Candidate { Url = url, Score = size > 0 ? size : 180 });
                else if (rel.Contains("icon")) list.Add(new Candidate { Url = url, Score = size > 0 ? size : 32 });
            }
        }
        static void CollectManifest(Uri url, List<Candidate> list)
        {
            try
            {
                Uri final;
                byte[] data = Get(url, 300000, out final);
                if (data == null) return;
                foreach (Match obj in JsonObject.Matches(Encoding.UTF8.GetString(data)))
                {
                    Match src = JsonSrc.Match(obj.Value);
                    if (!src.Success || src.Groups[1].Value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                    Match size = JsonSizes.Match(obj.Value);
                    Uri icon;
                    if (Uri.TryCreate(final, src.Groups[1].Value.Replace("\\/", "/"), out icon))
                        list.Add(new Candidate { Url = icon, Score = Math.Min(size.Success ? int.Parse(size.Groups[1].Value) : 100, 512) });
                }
            }
            catch { }
        }
        static int ParseSize(string sizes)
        {
            if (string.IsNullOrEmpty(sizes)) return 0;
            int best = 0;
            foreach (string part in sizes.Split(' '))
            {
                int x = part.IndexOf('x'); int n;
                if (x > 0 && int.TryParse(part.Substring(0, x), out n)) best = Math.Max(best, n);
            }
            return Math.Min(best, 512);
        }

        static byte[] Get(Uri url, int limit, out Uri final)
        {
            final = url;
            if (url.Scheme != Uri.UriSchemeHttps) return null;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
            request.Accept = "text/html,application/xhtml+xml,image/avif,image/webp,image/png,image/*,*/*;q=0.8";
            request.Headers["Accept-Language"] = "en-IN,en;q=0.9";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 12000; request.ReadWriteTimeout = 12000;
            request.AllowAutoRedirect = true; request.MaximumAutomaticRedirections = 5;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                final = response.ResponseUri;
                if (final.Scheme != Uri.UriSchemeHttps || (int)response.StatusCode >= 300) return null;
                using (Stream stream = response.GetResponseStream())
                using (MemoryStream memory = new MemoryStream())
                {
                    byte[] buffer = new byte[16384]; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        memory.Write(buffer, 0, read);
                        if (memory.Length > limit) return null;
                    }
                    return memory.ToArray();
                }
            }
        }

        static BitmapSource Decode(Uri url)
        {
            try
            {
                Uri final;
                byte[] data = Get(url, MaxImage, out final);
                if (data == null || data.Length < 64) return null;
                using (MemoryStream ms = new MemoryStream(data))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    BitmapSource best = null;
                    foreach (BitmapFrame frame in decoder.Frames)
                        if (best == null || frame.PixelWidth > best.PixelWidth) best = frame;
                    if (best == null || best.PixelWidth > 4096 || best.PixelHeight > 4096) return null;
                    double ratio = (double)best.PixelWidth / best.PixelHeight;
                    if (ratio < 0.75 || ratio > 1.34) return null; // Icons only, not banners.
                    if (best.PixelWidth > 256)
                    {
                        double scale = 256.0 / best.PixelWidth;
                        best = new TransformedBitmap(best, new ScaleTransform(scale, scale));
                    }
                    best = new FormatConvertedBitmap(best, PixelFormats.Pbgra32, null, 0);
                    WriteableBitmap copy = new WriteableBitmap(best);
                    copy.Freeze();
                    return copy;
                }
            }
            catch { return null; }
        }

        static byte[] EncodePng(BitmapSource image)
        {
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (MemoryStream ms = new MemoryStream()) { encoder.Save(ms); return ms.ToArray(); }
        }
    }

    // Opens a service as a full-screen Chrome app window using the normal Chrome profile (so logins stay).
    sealed class BrowserLauncher
    {
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
        delegate bool EnumProc(IntPtr hwnd, IntPtr param);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

        readonly string exe, processName;
        IntPtr last;
        internal string LastName { get; private set; }

        internal BrowserLauncher()
        {
            exe = FindBrowser("chrome.exe", @"Google\Chrome\Application\chrome.exe");
            processName = "chrome";
            if (exe == null) { exe = FindBrowser("msedge.exe", @"Microsoft\Edge\Application\msedge.exe"); processName = "msedge"; }
        }
        internal string BrowserName { get { return exe == null ? "your default browser" : processName == "chrome" ? "Chrome" : "Edge"; } }

        static string FindBrowser(string appPath, string relative)
        {
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (RegistryKey key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + appPath))
                    {
                        string path = key == null ? null : key.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(path)) { path = path.Trim('"'); if (File.Exists(path)) return path; }
                    }
                }
                catch { }
            }
            foreach (Environment.SpecialFolder root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(root), relative);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        internal bool CanResume { get { return last != IntPtr.Zero && IsWindow(last); } }

        internal void Resume()
        {
            if (!CanResume) return;
            if (IsIconic(last)) ShowWindow(last, 9);
            SetForegroundWindow(last);
        }

        // Returns a status line for the log. Window discovery and full-screen run in the background.
        internal string Launch(ServiceInfo service)
        {
            Uri uri = Catalog.ParseUserUrl(service.Url);
            if (uri == null) return "That link is not a web address.";
            if (CanResume) PostMessage(last, 0x10, IntPtr.Zero, IntPtr.Zero); // Close the previous app window: one show at a time.
            last = IntPtr.Zero; LastName = null;
            if (exe == null)
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return "Opened " + service.Name + " in your default browser.";
            }
            HashSet<IntPtr> before = new HashSet<IntPtr>(Windows());
            Process.Start(new ProcessStartInfo(exe, "--app=\"" + uri.AbsoluteUri + "\" --start-fullscreen") { UseShellExecute = false });
            string name = service.Name;
            Thread watcher = new Thread(delegate() { FollowUp(before, name); }) { IsBackground = true };
            watcher.Start();
            return "Opening " + service.Name + " in " + BrowserName + ". Press Ctrl+Alt+H to come back.";
        }

        void FollowUp(HashSet<IntPtr> before, string name)
        {
            IntPtr found = IntPtr.Zero;
            Stopwatch watch = Stopwatch.StartNew();
            while (found == IntPtr.Zero && watch.ElapsedMilliseconds < 12000)
            {
                Thread.Sleep(250);
                foreach (IntPtr h in Windows()) if (!before.Contains(h)) { found = h; break; }
            }
            if (found == IntPtr.Zero) return;
            last = found; LastName = name;
            Thread.Sleep(1200); // Let the window settle; it may already be full screen.
            if (!IsWindow(found) || CoversMonitor(found)) return;
            SetForegroundWindow(found);
            Thread.Sleep(150);
            if (GetForegroundWindow() == found && !CoversMonitor(found))
            {
                keybd_event(0x7A, 0, 0, UIntPtr.Zero); keybd_event(0x7A, 0, 2, UIntPtr.Zero); // F11
            }
        }

        static bool CoversMonitor(IntPtr hwnd)
        {
            Rect r; if (!GetWindowRect(hwnd, out r)) return false;
            MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return false;
            return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
        }

        List<IntPtr> Windows()
        {
            List<IntPtr> result = new List<IntPtr>();
            Dictionary<uint, bool> isBrowser = new Dictionary<uint, bool>();
            StringBuilder cls = new StringBuilder(64);
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                cls.Length = 0; GetClassName(h, cls, 64);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                bool match;
                if (!isBrowser.TryGetValue(pid, out match))
                {
                    try { using (Process proc = Process.GetProcessById((int)pid)) match = string.Equals(proc.ProcessName, processName, StringComparison.OrdinalIgnoreCase); }
                    catch { match = false; }
                    isBrowser[pid] = match;
                }
                if (match) result.Add(h);
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}
