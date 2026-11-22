using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SamsungCONNECT
{
    // These tests never change the display layout, open Cast, register startup, change power settings or connect a TV.
    static class SafetyTests
    {
        static int passed;
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        static DisplayTarget Target(string name, string key, bool wireless, bool internalPanel)
        {
            return new DisplayTarget { Name = name, Key = key, Wireless = wireless, Internal = internalPanel };
        }
        static bool Throws(Action action)
        {
            try { action(); return false; } catch (InvalidOperationException) { return true; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                string outDir = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
                if (args.Length > 1 && args[1] == "--logos") return FetchLogos();

                Check(!SafetyPolicy.ValidName(null) && !SafetyPolicy.ValidName("  "), "Empty names are rejected");
                Check(!SafetyPolicy.ValidName("TV\nFake") && !SafetyPolicy.ValidName(new string('a', 65)), "Invalid and oversized names are rejected");
                Check(SafetyPolicy.Matches("Samsung Living Room", " samsung "), "Matching ignores case and trims the saved name");
                Check(!SafetyPolicy.Matches("Other TV", "Samsung"), "Unrelated devices do not match");
                DisplayTarget laptop = Target("Laptop", "internal", false, true);
                DisplayTarget tv = Target("Samsung Living Room", "tv1", true, false);
                DisplayTarget wired = Target("Samsung monitor", "wired", false, false);
                DisplayTarget wired2 = Target("Office monitor", "wired2", false, false);
                DisplayTarget other = Target("Other TV", "tv2", true, false);
                Check(SafetyPolicy.Select(new List<DisplayTarget> { laptop, wired }, "Samsung") == null, "Wired Samsung monitor cannot satisfy wireless TV verification");
                Check(SafetyPolicy.Select(new List<DisplayTarget> { laptop, tv }, "Samsung") == tv, "A named wireless TV can be selected");
                Check(Throws(delegate { SafetyPolicy.Select(new List<DisplayTarget> { tv, Target(tv.Name, "tv-duplicate", true, false) }, "Samsung"); }), "Two TVs with identical names are rejected");
                Check(SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, tv }, tv), "Verified TV and internal panel permit switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, tv, wired }, tv), "Another external monitor blocks switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, tv, other }, tv), "Another wireless display blocks switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop }, tv), "A disappeared TV blocks switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, wired }, wired), "Wired target cannot enter wireless TV mode");
                Check(SafetyPolicy.IsOnlyTv(new List<DisplayTarget> { tv }, "tv1"), "A single active TV remains valid with the laptop off");
                Check(!SafetyPolicy.IsOnlyTv(new List<DisplayTarget> { other }, "tv1"), "A different wireless display cannot satisfy the watchdog");
                Check(!SafetyPolicy.IsOnlyTv(new List<DisplayTarget> { laptop, tv }, "tv1"), "Laptop still active is not reported as TV-only");
                Check(!SafetyPolicy.IsOnlyTv(new List<DisplayTarget>(), "tv1"), "An empty topology requires recovery");

                // HDMI rules
                Check(SafetyPolicy.SelectWired(new List<DisplayTarget> { laptop, wired }) == wired, "HDMI: the one wired external display is selected");
                Check(SafetyPolicy.SelectWired(new List<DisplayTarget> { laptop }) == null, "HDMI: nothing is selected when no cable is plugged in");
                Check(Throws(delegate { SafetyPolicy.SelectWired(new List<DisplayTarget> { laptop, wired, wired2 }); }), "HDMI: two external monitors are refused");
                Check(Throws(delegate { SafetyPolicy.SelectWired(new List<DisplayTarget> { laptop, wired, tv }); }), "HDMI: a wired + wireless mix is refused");
                Check(Throws(delegate { SafetyPolicy.SelectWired(new List<DisplayTarget> { laptop, tv }); }), "HDMI: a wireless display alone is not treated as HDMI");
                Check(SafetyPolicy.IsOnlyDisplay(new List<DisplayTarget> { wired }, "wired"), "Watchdog: the verified HDMI screen alone is healthy");
                Check(!SafetyPolicy.IsOnlyDisplay(new List<DisplayTarget> { laptop }, "wired"), "Watchdog: laptop-only after unplugging triggers restore");
                Check(!SafetyPolicy.IsOnlyDisplay(new List<DisplayTarget> { wired2 }, "wired"), "Watchdog: a different monitor does not count");
                Check(!SafetyPolicy.IsOnlyDisplay(new List<DisplayTarget> { wired }, null), "Watchdog: no verified key means not healthy");
                Check(SafetyPolicy.IsOnlyDisplay(new List<DisplayTarget> { tv }, "tv1"), "Watchdog: wireless TV-only still passes the shared rule");

                tv.Path.Source.Id = 17; tv.Path.Target.Id = 29; tv.Path.Source.Mode = 4; tv.Path.Target.Mode = 5;
                Native.PathInfo path = Native.TvPath(tv);
                Check(path.Source.Id == 17 && path.Target.Id == 29 && path.Flags == 1, "TV-only request preserves the selected physical path");
                Check(path.Source.Mode == uint.MaxValue && path.Target.Mode == uint.MaxValue, "Stale mode indexes are not reused");
                Check(Throws(delegate { Native.TvPath(wired); }), "Native TV-path builder rejects wired devices");
                Check(Marshal.SizeOf(typeof(Native.PathInfo)) == 72 && Marshal.SizeOf(typeof(Native.TargetInfo)) == 48, "Display path native layouts match Windows");
                Check(Marshal.SizeOf(typeof(Native.ModeInfo)) == 64 && Marshal.SizeOf(typeof(Native.TargetName)) == 420, "Display mode and target-name layouts match Windows");
                Check(Marshal.SizeOf(typeof(Native.WaveCaps)) == 84, "Audio capability layout uses the native 32-character name");

                // Catalog and user tiles
                List<ServiceInfo> catalog = Catalog.BuiltIn();
                HashSet<string> ids = new HashSet<string>();
                bool allHttps = true, allSections = true, allColors = true;
                foreach (ServiceInfo s in catalog)
                {
                    ids.Add(s.Id);
                    Uri u = Catalog.ParseUserUrl(s.Url);
                    if (u == null || u.Scheme != "https") allHttps = false;
                    if (Array.IndexOf(Catalog.Sections, s.Section) < 0) allSections = false;
                    try { Theme.Hex(s.Bg); Theme.Hex(s.Fg); } catch { allColors = false; }
                }
                Check(ids.Count == catalog.Count, "Every service has a unique id");
                Check(allHttps && allSections && allColors, "Every service has an https link, a known section and valid brand colours");
                foreach (string must in new[] { "netflix", "prime", "hotstar", "zee5", "sonyliv", "jiotv", "aha", "youtube" })
                    Check(ids.Contains(must), "Catalog includes " + must);
                Check(Catalog.ParseUserUrl("example.com").AbsoluteUri == "https://example.com/", "A bare domain becomes an https link");
                Check(Catalog.ParseUserUrl("javascript:alert(1)") == null && Catalog.ParseUserUrl("file:///C:/Windows") == null, "Only web links are accepted for tiles");
                Check(Catalog.ParseUserUrl("notadomain") == null && Catalog.ParseUserUrl("") == null, "Junk addresses are refused");
                Check(Catalog.ParseUserUrl("https://x.com/\"--kiosk").AbsoluteUri.IndexOf('"') < 0, "Quotes cannot break out of the browser command line");
                ServiceInfo custom = Catalog.MakeCustom("My Channel", Catalog.ParseUserUrl("https://example.org/live"));
                ServiceInfo round = Catalog.Decode(Catalog.Encode(custom));
                Check(round != null && round.Name == "My Channel" && round.Url == custom.Url && round.Custom, "Custom tiles survive saving and loading");
                Check(Catalog.Decode("Bad\tjavascript:alert(1)") == null && Catalog.Decode("no tab") == null, "Corrupt saved tiles are dropped");

                // Read-only live checks on this PC
                List<DisplayTarget> live = Native.GetTargets();
                Check(live.Count > 0, "Read-only active display enumeration succeeds on this PC");
                List<DisplayTarget> connected = Native.GetConnectedTargets();
                Check(connected.Count >= live.Count, "Read-only connected display enumeration succeeds on this PC");
                foreach (DisplayTarget t in connected) Console.WriteLine("  display: " + (t.Name.Length > 0 ? t.Name : "(unnamed)") + (t.Internal ? " [internal]" : t.Wireless ? " [wireless]" : " [wired]"));
                Console.WriteLine("Read-only audio enumeration completed. TV audio present: " + Native.HasTvAudio("Samsung"));

                Screenshots(outDir);
                Console.WriteLine("Passed " + passed + " checks. No real display, power or startup setting was changed.");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
        }

        // ---------- Screenshots of every screen, rendered off-screen ----------

        sealed class FakeApp : IAppActions
        {
            readonly LogoCache logos = new LogoCache();
            public void ConnectWireless() { } public void ConnectHdmi() { } public void Cancel() { } public void OpenLauncherHere() { }
            public void BackToLaptop() { } public void Exit() { } public void Launch(ServiceInfo service) { } public void Resume() { }
            public string ResumeName { get { return "Netflix"; } }
            public string TvName { get { return "SAMSUNG"; } }
            public string SaveTvName(string name) { return null; }
            public bool Startup { get { return true; } }
            public string SetStartup(bool on) { return null; }
            public bool DetectHdmi(out string label) { label = "SAMSUNG"; return true; }
            public void OpenCast() { } public void RefreshLogos() { } public void SetTheme(string mode) { }
            public string BrowserName { get { return "Chrome"; } }
            public LogoCache Logos { get { return logos; } }
            public List<ServiceInfo> CustomTiles { get { return new List<ServiceInfo> { Catalog.MakeCustom("Doordarshan", Catalog.ParseUserUrl("https://prasarbharati.gov.in/")) }; } }
            public void SaveCustomTiles(List<ServiceInfo> tiles) { }
            public List<string> HiddenTiles { get { return new List<string>(); } }
            public void SaveHiddenTiles(List<string> ids) { }
        }

        static void Pump(int ms)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        static void Snap(MainWindow w, string file, double width, double height)
        {
            w.Width = width; w.Height = height;
            Pump(450);
            FrameworkElement root = (FrameworkElement)w.Content;
            root.UpdateLayout();
            RenderTargetBitmap bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            DrawingVisual bg = new DrawingVisual();
            using (DrawingContext dc = bg.RenderOpen()) dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg); bmp.Render(root);
            PngBitmapEncoder png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(file)) png.Save(fs);
            Check(File.Exists(file) && new FileInfo(file).Length > 10000, "Rendered " + Path.GetFileName(file));
        }
        static void Screenshots(string outDir)
        {
            Directory.CreateDirectory(outDir);
            FakeApp fake = new FakeApp();
            foreach (string mode in new[] { "Light", "Dark" })
            {
                Theme.Apply(mode);
                MainWindow w = new MainWindow(fake) { WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Left = -30000, Top = -30000,
                    WindowStartupLocation = WindowStartupLocation.Manual };
                w.Show();
                string m = mode.ToLowerInvariant();
                w.SetLog("21:04  Ready. Your laptop will stay awake while this app is open.");
                w.ThemeChanged();
                Snap(w, Path.Combine(outDir, "connect-" + m + ".png"), 1280, 800);
                w.SetStatus("Allow the connection on your TV", "Accept the prompt on the TV with its remote. Waiting for Windows to report the TV.", 2, true, false);
                Snap(w, Path.Combine(outDir, "connecting-" + m + ".png"), 1280, 800);
                w.SetStatus("Ready", "", 0, false, false);
                w.ShowLauncher("On TV \u00B7 Wireless \u00B7 SAMSUNG", false);
                Snap(w, Path.Combine(outDir, "launcher-" + m + ".png"), 1920, 1080);
                w.ShowConfirm(delegate { });
                w.SetConfirmSeconds(9);
                Snap(w, Path.Combine(outDir, "confirm-" + m + ".png"), 1920, 1080);
                w.CloseConfirm();
                w.ShowSettingsForTest();
                Snap(w, Path.Combine(outDir, "settings-" + m + ".png"), 1920, 1080);
                w.Hide();
            }
        }

        // ---------- Optional: download every logo now and report (network) ----------

        static int FetchLogos()
        {
            LogoCache cache = new LogoCache();
            List<ServiceInfo> all = Catalog.BuiltIn();
            int ok = 0, done = 0;
            object gate = new object();
            List<Thread> threads = new List<Thread>();
            foreach (ServiceInfo service in all)
            {
                ServiceInfo s = service;
                Thread t = new Thread(delegate()
                {
                    string result;
                    try
                    {
                        byte[] png = LogoFetcher.Fetch(s.Url);
                        if (png != null) { cache.Store(s.Id, png); result = "OK   " + png.Length + " bytes"; lock (gate) ok++; }
                        else result = "none (falls back to styled tile)";
                    }
                    catch (Exception ex) { result = "error " + ex.Message; }
                    lock (gate) { done++; Console.WriteLine(s.Name.PadRight(18) + result); }
                });
                t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); threads.Add(t);
            }
            foreach (Thread t in threads) t.Join(90000);
            Console.WriteLine(ok + " of " + all.Count + " logos downloaded to " + cache.Folder);
            return 0;
        }
    }
}
