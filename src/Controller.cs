using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace SamsungCONNECT
{
    sealed class Controller : IAppActions
    {
        readonly MainWindow ui;
        readonly WF.NotifyIcon tray;
        readonly HotkeyWindow hotkeys;
        readonly System.Threading.Timer watchdog;
        readonly DispatcherTimer awake;
        readonly Icon idleIcon, activeIcon;
        readonly object topologyLock = new object(), operationLock = new object();
        readonly BrowserLauncher browser = new BrowserLauncher();
        readonly LogoCache logos = new LogoCache();
        CancellationTokenSource operation;
        volatile bool tvMode, closing;
        int busy, watching, misses;
        string activeKey, activeLabel;

        internal Controller(bool startHidden, bool autoConnect)
        {
            string lid = LidPolicy.Restore(); // Undo a lid change left behind by a crash or power cut.
            Theme.Apply(Settings.Theme);
            ui = new MainWindow(this);
            ui.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (closing) return;
                e.Cancel = true;
                if (tvMode) { ui.WindowState = System.Windows.WindowState.Minimized; return; }
                ui.Hide();
                Notify("Still running", "SamsungCONNECT is in the tray. Ctrl+Alt+H opens the launcher.");
            };
            idleIcon = DrawIcon(false); activeIcon = DrawIcon(true);
            try { ui.Icon = Imaging.CreateBitmapSourceFromHIcon(idleIcon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); } catch { }
            tray = new WF.NotifyIcon { Icon = idleIcon, Text = "SamsungCONNECT - Laptop", Visible = true };
            tray.MouseClick += delegate(object sender, WF.MouseEventArgs e) { if (e.Button == WF.MouseButtons.Left) ui.BringHome(); };
            WF.ContextMenuStrip menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open SamsungCONNECT", null, delegate { ui.BringHome(); });
            menu.Items.Add("Connect wireless TV", null, delegate { ConnectWireless(); });
            menu.Items.Add("Connect HDMI TV", null, delegate { ConnectHdmi(); });
            menu.Items.Add("Laptop screen / cancel", null, delegate { Restore("Back to the laptop screen."); });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Exit and restore laptop", null, delegate { Exit(); });
            tray.ContextMenuStrip = menu;

            hotkeys = new HotkeyWindow();
            hotkeys.Pressed += delegate(int id)
            {
                if (id == 9000)
                {
                    if (tvMode || Interlocked.CompareExchange(ref busy, 0, 0) != 0) Restore("Back to the laptop screen (Ctrl+Alt+T).");
                    else ConnectLast();
                }
                else if (id == 9001) ui.BringHome();
            };
            if (!Native.RegisterHotKey(hotkeys.Handle, 9000, 0x4003, 0x54)) Log("Ctrl+Alt+T is used by another app. Use the buttons instead.");
            if (!Native.RegisterHotKey(hotkeys.Handle, 9001, 0x4003, 0x48)) Log("Ctrl+Alt+H is used by another app. Use the tray icon instead.");

            KeepAwake.Start();
            awake = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            awake.Tick += delegate { KeepAwake.Refresh(); };
            awake.Start();

            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            watchdog = new System.Threading.Timer(Watchdog, null, 2500, 2500);

            logos.Updated += delegate(string id) { Ui(delegate { ui.LogoUpdated(id); }); };
            List<ServiceInfo> all = Catalog.BuiltIn(); all.AddRange(CustomTiles);
            logos.Fetch(all, false);

            if (lid != null) Log(lid);
            Log("Ready. Your laptop will stay awake while this app is open.");
            if (!startHidden) { ui.Show(); ui.Activate(); }
            if (autoConnect) ui.Dispatcher.BeginInvoke((Action)ConnectLast, DispatcherPriority.ApplicationIdle);
        }

        internal MainWindow Window { get { return ui; } }

        // ---------- Plumbing ----------

        void Ui(Action action)
        {
            if (closing) return;
            try
            {
                if (ui.Dispatcher.CheckAccess()) action();
                else ui.Dispatcher.BeginInvoke((Action)delegate { if (!closing) action(); });
            }
            catch (InvalidOperationException) { /* Shutting down. */ }
        }
        void Report(string title, string detail, int step, bool error)
        {
            Log(detail);
            Ui(delegate { ui.SetStatus(title, detail, step, busy != 0, error); });
        }
        void Notify(string title, string text)
        {
            Ui(delegate { tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.Info); });
        }
        void Log(string text)
        {
            ActivityLog.Write(text);
            Ui(delegate { ui.SetLog(DateTime.Now.ToString("HH:mm") + "  " + text); });
        }
        static void Pause(CancellationToken token, int milliseconds)
        {
            if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
        }
        void SetTray()
        {
            tray.Icon = tvMode ? activeIcon : idleIcon;
            tray.Text = tvMode ? "SamsungCONNECT - TV only" : "SamsungCONNECT - Laptop";
        }

        // ---------- Connect ----------

        void ConnectLast() { if (Settings.LastMethod == "HDMI") ConnectHdmi(); else ConnectWireless(); }
        public void ConnectWireless() { Start(true); }
        public void ConnectHdmi() { Start(false); }

        void Start(bool wireless)
        {
            if (closing || tvMode || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            CancellationTokenSource current = new CancellationTokenSource();
            lock (operationLock) operation = current;
            string name = Settings.TvName;
            if (wireless) Report("Finding your TV", "Looking for \"" + name + "\". Your laptop screen stays on until the TV is checked.", 1, false);
            else Report("Finding the HDMI TV", "Checking the cable connection.", 1, false);
            Ui(delegate { ui.ShowConnect(); ui.BringHome(); });
            Thread worker = new Thread(delegate() { if (wireless) WirelessFlow(name, current); else HdmiFlow(current); });
            worker.IsBackground = true; worker.SetApartmentState(ApartmentState.MTA); worker.Start();
        }

        void WirelessFlow(string name, CancellationTokenSource current)
        {
            CancellationToken token = current.Token;
            bool switched = false;
            try
            {
                DisplayTarget target = SafetyPolicy.Select(Native.GetTargets(), name);
                if (target == null)
                {
                    Native.OpenCast();
                    Stopwatch find = Stopwatch.StartNew();
                    bool clicked = false;
                    while (find.ElapsedMilliseconds < 60000)
                    {
                        token.ThrowIfCancellationRequested();
                        target = SafetyPolicy.Select(Native.GetTargets(), name);
                        if (target != null) break;
                        if (!clicked && CastAutomation.TryConnect(name))
                        {
                            clicked = true;
                            Report("Allow the connection on your TV", "Accept the prompt on the TV with its remote. Waiting for Windows to report the TV.", 2, false);
                        }
                        Pause(token, 1000);
                    }
                    if (target == null)
                    {
                        Report("Waiting for your TV", "If it doesn't connect, press Win+K and pick your TV. Check that the saved TV name matches.", 2, false);
                        Stopwatch wait = Stopwatch.StartNew();
                        while (wait.ElapsedMilliseconds < 90000 && target == null)
                        {
                            Pause(token, 1500); target = SafetyPolicy.Select(Native.GetTargets(), name);
                        }
                    }
                    if (target == null) throw new InvalidOperationException("The TV \"" + name + "\" was not found. Check the prompt on the TV, the TV name in Settings, and Win+K.");
                }
                Report("TV found", target.Name + " is connected. Checking for TV sound (up to 15 seconds).", 2, false);
                bool audio = Native.HasTvAudio(name);
                Stopwatch audioWait = Stopwatch.StartNew();
                while (!audio && audioWait.ElapsedMilliseconds < 15000) { Pause(token, 1000); audio = Native.HasTvAudio(name); }
                // Cancellation and emergency restore use this same lock. Revalidate just before changing topology.
                lock (topologyLock)
                {
                    token.ThrowIfCancellationRequested();
                    List<DisplayTarget> targets = Native.GetTargets();
                    DisplayTarget fresh = SafetyPolicy.Select(targets, name);
                    if (fresh == null || fresh.Key != target.Key || !SafetyPolicy.CanSwitch(targets, fresh))
                        throw new InvalidOperationException("The display changed, or another monitor is plugged in. Unplug other monitors and retry.");
                    switched = true; activeKey = fresh.Key; tvMode = true; misses = 0;
                    Native.SetTv(fresh);
                }
                bool verified = false;
                for (int i = 0; i < 12; i++)
                {
                    Pause(token, 500);
                    if (SafetyPolicy.IsOnlyTv(Native.GetTargets(), activeKey)) { verified = true; break; }
                }
                if (!verified) throw new InvalidOperationException("Windows did not confirm the TV-only layout. Back to the laptop.");
                activeLabel = "On TV · Wireless · " + target.Name;
                ConfirmKeep(token);
                Kept("Wireless", audio ? null : "If sound plays on the laptop, pick the TV in Windows sound settings.");
            }
            catch (OperationCanceledException) { if (switched) RestoreNow("Connection cancelled."); else Report("Cancelled", "Still on the laptop screen.", 0, false); }
            catch (Exception ex)
            {
                if (switched) RestoreNow("TV mode could not be kept.");
                Report("Connection needs attention", ex.Message, 0, true); Notify("Connection needs attention", ex.Message);
            }
            finally { Finish(current); }
        }

        void HdmiFlow(CancellationTokenSource current)
        {
            CancellationToken token = current.Token;
            bool switched = false;
            try
            {
                DisplayTarget target = null;
                Stopwatch find = Stopwatch.StartNew();
                while (find.ElapsedMilliseconds < 10000)
                {
                    target = SafetyPolicy.SelectWired(Native.GetConnectedTargets());
                    if (target != null) break;
                    Pause(token, 1000);
                }
                if (target == null) throw new InvalidOperationException("No HDMI screen found. Plug in the cable, turn the TV on and choose that HDMI input on the TV.");
                string label = target.Name.Length > 0 ? target.Name : "HDMI screen";
                Report("Switching to " + label, "Your laptop screen will turn off and the launcher opens on the TV.", 2, false);
                Pause(token, 800);
                lock (topologyLock)
                {
                    token.ThrowIfCancellationRequested();
                    DisplayTarget fresh = SafetyPolicy.SelectWired(Native.GetConnectedTargets());
                    if (fresh == null || fresh.Key != target.Key) throw new InvalidOperationException("The HDMI screen changed. Check the cable and retry.");
                    switched = true; activeKey = fresh.Key; tvMode = true; misses = 0;
                    Native.SetExternalOnly();
                }
                bool verified = false;
                for (int i = 0; i < 16; i++)
                {
                    Pause(token, 500);
                    List<DisplayTarget> active = Native.GetTargets();
                    if (active.Count == 1 && !active[0].Internal && !active[0].Wireless) { activeKey = active[0].Key; verified = true; break; }
                }
                if (!verified) throw new InvalidOperationException("Windows did not confirm the HDMI-only layout. Back to the laptop.");
                activeLabel = "On TV · HDMI · " + label;
                ConfirmKeep(token);
                Kept("HDMI", null);
            }
            catch (OperationCanceledException) { if (switched) RestoreNow("Connection cancelled."); else Report("Cancelled", "Still on the laptop screen.", 0, false); }
            catch (Exception ex)
            {
                if (switched) RestoreNow("TV mode could not be kept.");
                Report("Connection needs attention", ex.Message, 0, true); Notify("Connection needs attention", ex.Message);
            }
            finally { Finish(current); }
        }

        // Show the launcher on the TV with a 15-second "keep?" prompt. No answer means back to the laptop.
        void ConfirmKeep(CancellationToken token)
        {
            int answer = 0;
            string label = activeLabel;
            Report("Keep this TV screen?", "Confirm on the TV within 15 seconds.", 3, false);
            Ui(delegate
            {
                if (token.IsCancellationRequested) return;
                ui.ShowLauncher(label, true); ui.BringHome();
                ui.ShowConfirm(delegate(bool keep) { Interlocked.Exchange(ref answer, keep ? 1 : -1); });
            });
            Stopwatch deadline = Stopwatch.StartNew();
            while (Interlocked.CompareExchange(ref answer, 0, 0) == 0 && deadline.ElapsedMilliseconds < 15000)
            {
                Pause(token, 100);
                int seconds = Math.Max(0, 15 - (int)(deadline.ElapsedMilliseconds / 1000));
                Ui(delegate { ui.SetConfirmSeconds(seconds); });
            }
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref answer, 0, 0) != 1) throw new InvalidOperationException("TV mode was not kept, so the laptop screen is back.");
        }

        void Kept(string method, string note)
        {
            try { Settings.LastMethod = method; } catch { }
            string lid = null;
            if (Settings.LidOnTv) { try { lid = LidPolicy.Apply(); } catch (Exception ex) { lid = "Lid setting unchanged: " + ex.Message; } }
            Log("TV mode is on (" + method + ")." + (lid != null ? " " + lid : ""));
            Ui(delegate
            {
                ui.CloseConfirm();
                ui.Toast(note ?? lid ?? "You're on the TV. Enjoy the show!");
            });
            Notify("TV mode is on", "Ctrl+Alt+T returns to your laptop. Ctrl+Alt+H opens the launcher.");
        }

        void Finish(CancellationTokenSource current)
        {
            lock (operationLock)
            {
                current.Cancel();
                if (operation == current) operation = null;
                current.Dispose();
            }
            Interlocked.Exchange(ref busy, 0);
            Ui(delegate { ui.CloseConfirm(); SetTray(); ui.SetBusy(false); });
        }

        // ---------- Restore ----------

        void Restore(string reason)
        {
            CancelOperation();
            Thread worker = new Thread(delegate() { RestoreNow(reason); }); worker.IsBackground = true; worker.Start();
        }
        void CancelOperation()
        {
            lock (operationLock) { if (operation != null) operation.Cancel(); }
        }
        bool RestoreNow(string reason)
        {
            try
            {
                lock (topologyLock)
                {
                    Native.RestoreInternal(); tvMode = false; activeKey = null; misses = 0;
                }
                string lid = LidPolicy.Restore();
                Log(reason + (lid != null ? " " + lid : ""));
                Ui(delegate
                {
                    ui.CloseConfirm(); SetTray();
                    ui.ShowConnect();
                    ui.SetStatus("Laptop screen is on", reason, 0, busy != 0, false);
                });
                return true;
            }
            catch (Exception ex)
            {
                Report("Restore needs attention", "Press Win+P and choose PC screen only. " + ex.Message, 0, true);
                Notify("Restore needs attention", "Press Win+P and choose PC screen only."); return false;
            }
        }
        void Watchdog(object state)
        {
            if (closing || !tvMode || Interlocked.CompareExchange(ref busy, 0, 0) != 0 || Interlocked.CompareExchange(ref watching, 1, 0) != 0) return;
            try
            {
                bool present = false;
                try { present = SafetyPolicy.IsOnlyDisplay(Native.GetTargets(), activeKey); }
                catch { /* Repeated query failures also restore instead of assuming the TV is safe. */ }
                if (present) Interlocked.Exchange(ref misses, 0);
                else if (Interlocked.Increment(ref misses) >= 2)
                {
                    CancelOperation();
                    if (RestoreNow("The TV connection dropped, so the laptop screen is back."))
                        Notify("Laptop restored", "The TV connection changed or disappeared. Press Ctrl+Alt+T to reconnect.");
                }
            }
            finally { Interlocked.Exchange(ref watching, 0); }
        }
        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            CancelOperation();
            if (tvMode) RestoreNow("Windows is signing out.");
            LidPolicy.Restore();
        }
        void OnDisplayChanged(object sender, EventArgs e)
        {
            Ui(delegate { if (tvMode) ui.Refit(); });
        }

        // ---------- IAppActions ----------

        public void Cancel() { Restore("Cancelled. Still on the laptop screen."); }
        public void OpenLauncherHere()
        {
            ui.ShowLauncher("This screen", true);
            Log("Launcher opened on this screen.");
        }
        public void BackToLaptop()
        {
            if (tvMode || busy != 0) Restore("Back to the laptop screen.");
            else ui.ShowConnect();
        }
        public void Exit()
        {
            CancelOperation();
            if ((tvMode || busy != 0) && !RestoreNow("Exiting SamsungCONNECT.")) return;
            closing = true;
            SystemEvents.SessionEnding -= OnSessionEnding; SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            watchdog.Dispose(); awake.Stop();
            Native.UnregisterHotKey(hotkeys.Handle, 9000); Native.UnregisterHotKey(hotkeys.Handle, 9001); hotkeys.DestroyHandle();
            KeepAwake.Stop(); LidPolicy.Restore();
            tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); idleIcon.Dispose(); activeIcon.Dispose();
            ActivityLog.Write("Exited.");
            ui.Close();
            System.Windows.Application.Current.Shutdown();
        }
        internal void RecoverBeforeExit()
        {
            CancelOperation();
            lock (topologyLock)
            {
                if (tvMode) { try { Native.RestoreInternal(); tvMode = false; } catch { /* Manual Win+P remains available. */ } }
            }
            try { LidPolicy.Restore(); } catch { }
            try { KeepAwake.Stop(); } catch { }
        }

        public void Launch(ServiceInfo service)
        {
            try
            {
                string msg = browser.Launch(service);
                Log(msg); ui.Toast(msg);
            }
            catch (Exception ex) { Log("Could not open " + service.Name + ": " + ex.Message); ui.ShowMessage("Could not open " + service.Name, ex.Message); }
        }
        public void Resume() { browser.Resume(); }
        public string ResumeName { get { return browser.CanResume ? browser.LastName : null; } }
        public string TvName { get { return Settings.TvName; } }
        public string SaveTvName(string name)
        {
            if (busy != 0 || tvMode) return "Go back to the laptop screen before changing the TV name.";
            if (!SafetyPolicy.ValidName(name)) return "Enter a TV name of 1 to 64 characters.";
            try { Settings.SetString("TVName", name.Trim()); Log("Saved TV name: " + name.Trim()); ui.SetStatus("Ready when you are", "Wireless will look for \"" + name.Trim() + "\".", 0, false, false); return null; }
            catch (Exception ex) { return "Could not save the TV name: " + ex.Message; }
        }
        public bool Startup { get { return Settings.StartsWithWindows(); } }
        public string SetStartup(bool on)
        {
            try
            {
                Settings.SetStartsWithWindows(on, Process.GetCurrentProcess().MainModule.FileName);
                return on ? "SamsungCONNECT will start when you sign in." : "Won't start with Windows.";
            }
            catch (Exception ex) { return "Could not change startup: " + ex.Message; }
        }
        public bool DetectHdmi(out string label)
        {
            label = null;
            if (tvMode) return false;
            try
            {
                DisplayTarget t = SafetyPolicy.SelectWired(Native.GetConnectedTargets());
                if (t == null) return false;
                label = t.Name.Length > 0 ? t.Name : "HDMI screen";
                return true;
            }
            catch (InvalidOperationException ex) { label = ex.Message; return false; }
        }
        public void OpenCast() { Native.OpenCast(); }
        public void RefreshLogos()
        {
            List<ServiceInfo> all = Catalog.BuiltIn(); all.AddRange(CustomTiles);
            logos.Fetch(all, true);
        }
        public void SetTheme(string mode) { try { Settings.Theme = mode; } catch { } }
        public string BrowserName { get { return browser.BrowserName; } }
        public LogoCache Logos { get { return logos; } }
        public List<ServiceInfo> CustomTiles { get { try { return Settings.CustomTiles(); } catch { return new List<ServiceInfo>(); } } }
        public void SaveCustomTiles(List<ServiceInfo> tiles) { try { Settings.SaveCustomTiles(tiles); } catch (Exception ex) { ui.Toast("Could not save: " + ex.Message); } }
        public List<string> HiddenTiles { get { try { return Settings.GetList("HiddenTiles"); } catch { return new List<string>(); } } }
        public void SaveHiddenTiles(List<string> ids) { try { Settings.SetList("HiddenTiles", ids); } catch (Exception ex) { ui.Toast("Could not save: " + ex.Message); } }

        internal static Icon DrawIcon(bool active)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                using (Pen pen = new Pen(active ? Color.FromArgb(84, 104, 255) : Color.FromArgb(89, 101, 121), 2.5f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                    g.DrawRectangle(pen, 3, 9, 25, 16); g.DrawLine(pen, 16, 25, 16, 29); g.DrawLine(pen, 10, 29, 22, 29);
                    g.DrawArc(pen, 9, 1, 14, 11, 210, 120);
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { Native.DestroyIcon(handle); }
            }
        }
    }

    sealed class HotkeyWindow : WF.NativeWindow
    {
        internal event Action<int> Pressed;
        internal HotkeyWindow() { CreateHandle(new WF.CreateParams { Caption = "SamsungCONNECT hotkey", Parent = new IntPtr(-3) }); }
        protected override void WndProc(ref WF.Message m)
        {
            if (m.Msg == 0x312 && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    // Plain-text activity log for troubleshooting: %LOCALAPPDATA%\SamsungCONNECT\activity.log
    static class ActivityLog
    {
        static readonly object gate = new object();
        internal static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamsungCONNECT", "activity.log"); }
        }
        internal static void Write(string text)
        {
            lock (gate)
            {
                try
                {
                    string file = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    if (File.Exists(file) && new FileInfo(file).Length > 262144) File.Delete(file);
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
