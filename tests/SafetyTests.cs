using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SamsungCONNECT
{
    // These tests never call SetDisplayConfig, open Cast, register startup, or connect a TV.
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
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Check(!SafetyPolicy.ValidName(null) && !SafetyPolicy.ValidName("  "), "Empty names are rejected");
                Check(!SafetyPolicy.ValidName("TV\nFake") && !SafetyPolicy.ValidName(new string('a', 65)), "Invalid and oversized names are rejected");
                Check(SafetyPolicy.Matches("Samsung Living Room", " samsung "), "Matching ignores case and trims the saved name");
                Check(!SafetyPolicy.Matches("Other TV", "Samsung"), "Unrelated devices do not match");
                DisplayTarget laptop = Target("Laptop", "internal", false, true);
                DisplayTarget tv = Target("Samsung Living Room", "tv1", true, false);
                DisplayTarget wired = Target("Samsung monitor", "wired", false, false);
                DisplayTarget other = Target("Other TV", "tv2", true, false);
                Check(SafetyPolicy.Select(new List<DisplayTarget> { laptop, wired }, "Samsung") == null, "Wired Samsung monitor cannot satisfy TV verification");
                Check(SafetyPolicy.Select(new List<DisplayTarget> { laptop, tv }, "Samsung") == tv, "A named wireless TV can be selected");
                bool ambiguous = false;
                try { SafetyPolicy.Select(new List<DisplayTarget> { tv, Target(tv.Name, "tv-duplicate", true, false) }, "Samsung"); }
                catch (InvalidOperationException) { ambiguous = true; }
                Check(ambiguous, "Two TVs with identical names are rejected");
                Check(SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, tv }, tv), "Verified TV and internal panel permit switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, tv, wired }, tv), "Another external monitor blocks switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, tv, other }, tv), "Another wireless display blocks switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop }, tv), "A disappeared TV blocks switching");
                Check(!SafetyPolicy.CanSwitch(new List<DisplayTarget> { laptop, wired }, wired), "Wired target cannot enter TV mode");
                Check(SafetyPolicy.IsOnlyTv(new List<DisplayTarget> { tv }, "tv1"), "A single active TV remains valid with the laptop off");
                Check(!SafetyPolicy.IsOnlyTv(new List<DisplayTarget> { other }, "tv1"), "A different wireless display cannot satisfy the watchdog");
                Check(!SafetyPolicy.IsOnlyTv(new List<DisplayTarget> { laptop, tv }, "tv1"), "Laptop still active is not reported as TV-only");
                Check(!SafetyPolicy.IsOnlyTv(new List<DisplayTarget>(), "tv1"), "An empty topology requires recovery");
                tv.Path.Source.Id = 17; tv.Path.Target.Id = 29; tv.Path.Source.Mode = 4; tv.Path.Target.Mode = 5;
                Native.PathInfo path = Native.TvPath(tv);
                Check(path.Source.Id == 17 && path.Target.Id == 29 && path.Flags == 1, "TV-only request preserves the selected physical path");
                Check(path.Source.Mode == uint.MaxValue && path.Target.Mode == uint.MaxValue, "Stale mode indexes are not reused");
                bool rejectWired = false;
                try { Native.TvPath(wired); } catch (InvalidOperationException) { rejectWired = true; }
                Check(rejectWired, "Native TV-path builder rejects wired devices");
                Check(Marshal.SizeOf(typeof(Native.PathInfo)) == 72 && Marshal.SizeOf(typeof(Native.TargetInfo)) == 48, "Display path native layouts match Windows");
                Check(Marshal.SizeOf(typeof(Native.ModeInfo)) == 64 && Marshal.SizeOf(typeof(Native.TargetName)) == 420, "Display mode and target-name layouts match Windows");
                Check(Marshal.SizeOf(typeof(Native.WaveCaps)) == 84, "Audio capability layout uses the native 32-character name");
                using (NotifyIcon icon = new NotifyIcon())
                {
                    icon.Text = "SamsungCONNECT - TV only";
                    bool originalFails = false;
                    try { icon.Text = "SamsungCONNECT - TV is your only screen (Ctrl+Alt+T to switch back)"; }
                    catch (ArgumentException) { originalFails = true; }
                    Check(originalFails, "Original oversized tray tooltip crash reproduced; replacement fits");
                }
                List<DisplayTarget> live = Native.GetTargets();
                Check(live.Count > 0, "Read-only native display enumeration succeeds on this Windows machine");
                Console.WriteLine("Read-only audio enumeration completed. TV audio present: " + Native.HasTvAudio("Samsung"));
                string directory = args.Length == 0 ? Environment.CurrentDirectory : args[0];
                using (Dashboard view = new Dashboard("SAMSUNG"))
                {
                    IntPtr handle = view.Handle;
                    view.Log("Ready. No display settings have been changed.");
                    view.PerformLayout();
                    Capture(view, Path.Combine(directory, "dashboard.png"));
                    view.SetState("Your TV is the only screen", "Preview of TV mode. Ctrl+Alt+T returns to the laptop.", 3, false, true);
                    Capture(view, Path.Combine(directory, "dashboard-tv.png"));
                    view.SetState("Connection needs attention", "Check the TV permission prompt, TV name and Win+K connection.", 0, false, false);
                    Capture(view, Path.Combine(directory, "dashboard-error.png"));
                    view.SetState("Ready for your TV", "Turn on your TV and keep Wi-Fi enabled.", 0, false, false);
                    view.Scale(new SizeF(1.5f, 1.5f));
                    Capture(view, Path.Combine(directory, "dashboard-scaled.png"));
                    Check(true, "Dashboard renders ready, TV-only, error and scaled states without connecting");
                }
                using (ConfirmForm confirm = new ConfirmForm())
                {
                    IntPtr handle = confirm.Handle; confirm.SetSeconds(9);
                    Capture(confirm, Path.Combine(directory, "confirmation.png"));
                    Check(true, "Recovery confirmation dialog renders without switching screens");
                }
                Console.WriteLine("Passed " + passed + " checks. No real display mode was changed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        static void Capture(Form form, string file)
        {
            // WinForms does not paint invisible child controls. Show a fully transparent offscreen
            // test window so WM_PRINT can render them; no production controller is instantiated.
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false; form.TopMost = false; form.Opacity = 0;
            if (!form.Visible) form.Show();
            form.PerformLayout(); Application.DoEvents();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
