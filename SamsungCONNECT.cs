using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SamsungCONNECT")]
[assembly: AssemblyDescription("Wireless TV display controls for Windows")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace SamsungCONNECT
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (Mutex instance = new Mutex(true, "Local\\SamsungCONNECTSingleInstance", out first))
            {
                if (!first) return;
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                TrayApp app = null;
                try { app = new TrayApp(!Has(args, "--no-connect"), Has(args, "--tray")); Application.Run(app); }
                catch (Exception ex)
                {
                    if (app != null) app.RecoverBeforeExit();
                    MessageBox.Show("SamsungCONNECT could not continue. Use Win+P > PC screen only if needed.\n\n" + ex.Message,
                        "SamsungCONNECT", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { if (app != null) app.RecoverBeforeExit(); instance.ReleaseMutex(); }
            }
        }
        static bool Has(string[] args, string value)
        {
            foreach (string arg in args) if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // Pure rules can be tested without opening Cast or changing the real display topology.
    static class SafetyPolicy
    {
        internal static bool ValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64) return false;
            foreach (char c in name) if (char.IsControl(c)) return false;
            return true;
        }
        internal static bool Matches(string value, string name)
        {
            return ValidName(name) && !string.IsNullOrEmpty(value) && value.IndexOf(name.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
        internal static DisplayTarget Select(List<DisplayTarget> targets, string name)
        {
            DisplayTarget selected = null;
            foreach (DisplayTarget target in targets)
            {
                if (!target.Wireless || !Matches(target.Name, name)) continue;
                if (selected != null) throw new InvalidOperationException("Multiple TVs match. Save a more specific TV name.");
                selected = target;
            }
            return selected;
        }
        internal static bool CanSwitch(List<DisplayTarget> targets, DisplayTarget selected)
        {
            if (selected == null || !selected.Wireless) return false;
            bool found = false;
            foreach (DisplayTarget target in targets)
            {
                if (target.Key == selected.Key) { if (!target.Wireless) return false; found = true; }
                else if (!target.Internal) return false;
            }
            return found;
        }
        internal static bool IsOnlyTv(List<DisplayTarget> targets, string key)
        {
            return targets.Count == 1 && targets[0].Wireless && targets[0].Key == key;
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const string SettingsPath = @"Software\SamsungCONNECT";
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly Dashboard view;
        readonly NotifyIcon tray;
        readonly HotkeyWindow hotkey;
        readonly object topologyLock = new object();
        readonly object operationLock = new object();
        readonly System.Threading.Timer watchdog;
        readonly Icon idleIcon, activeIcon;
        CancellationTokenSource operation;
        volatile bool tvMode, closing;
        int busy, watching, misses;
        string tvName = "SAMSUNG", activeKey;
        ConfirmForm confirm;

        internal TrayApp(bool autoConnect, bool hidden)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsPath))
                {
                    string saved = key == null ? null : key.GetValue("TVName") as string;
                    if (SafetyPolicy.ValidName(saved)) tvName = saved.Trim();
                }
            }
            catch { /* A read-only profile can still use the app. */ }
            view = new Dashboard(tvName);
            IntPtr uiHandle = view.Handle; // Stable marshaler, created on the UI thread before workers start.
            view.ConnectRequested += StartConnect;
            view.RestoreRequested += delegate { Restore("Laptop screen requested."); };
            view.CastRequested += delegate { Native.OpenCast(); };
            view.DisconnectRequested += StartDisconnect;
            view.SaveRequested += SaveName;
            view.StartupRequested += SetStartup;
            view.ExitRequested += ExitAndRestore;
            view.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (closing || e.CloseReason == CloseReason.WindowsShutDown) return;
                e.Cancel = true;
                view.Hide();
                Notify("Running in the tray", "Click the TV icon to open controls. Ctrl+Alt+T switches screens.");
            };
            idleIcon = DrawIcon(false); activeIcon = DrawIcon(true);
            view.Icon = idleIcon;
            tray = new NotifyIcon { Icon = idleIcon, Text = "SamsungCONNECT - Laptop", Visible = true };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowControls(); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open SamsungCONNECT", null, delegate { ShowControls(); });
            menu.Items.Add("Connect and use TV", null, delegate { StartConnect(); });
            menu.Items.Add("Laptop screen / cancel", null, delegate { Restore("Laptop screen requested."); });
            menu.Items.Add("Disconnect TV...", null, delegate { StartDisconnect(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit and restore laptop", null, delegate { ExitAndRestore(); });
            tray.ContextMenuStrip = menu;
            hotkey = new HotkeyWindow();
            hotkey.Pressed += delegate
            {
                if (tvMode || Interlocked.CompareExchange(ref busy, 0, 0) != 0) Restore("Laptop screen requested with Ctrl+Alt+T.");
                else StartConnect();
            };
            if (!Native.RegisterHotKey(hotkey.Handle, 9000, 0x4003, 0x54))
                view.Log("Ctrl+Alt+T is in use by another app. Use the buttons or tray menu.");
            view.SetStartup(IsStartup());
            SystemEvents.SessionEnding += OnSessionEnding;
            watchdog = new System.Threading.Timer(Watchdog, null, 2500, 2500);
            view.SetState("Ready for your TV", "Turn on the TV and keep Wi-Fi enabled. Your laptop screen stays on until the TV is verified.", 0, false, false);
            if (!hidden) view.Show();
            if (autoConnect) view.BeginInvoke((Action)StartConnect);
        }
        void Ui(Action action)
        {
            if (closing || view.IsDisposed) return;
            try
            {
                if (view.InvokeRequired) view.BeginInvoke((Action)delegate { if (!closing && !view.IsDisposed) action(); });
                else action();
            }
            catch (InvalidOperationException) { /* Shutting down: never retry UI work on the worker. */ }
        }
        void Report(string title, string detail, int step)
        {
            Ui(delegate { view.SetState(title, detail, step, busy != 0, tvMode); view.Log(detail); });
        }
        void Notify(string title, string text)
        {
            Ui(delegate { tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); });
        }
        void ShowControls() { view.Show(); view.WindowState = FormWindowState.Normal; view.Activate(); }
        void SaveName(string name)
        {
            if (busy != 0 || tvMode) { view.Log("Return to the laptop before changing the TV name."); return; }
            if (!SafetyPolicy.ValidName(name)) { view.Log("Enter a TV name of 1 to 64 characters, without control characters."); return; }
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsPath)) key.SetValue("TVName", name.Trim());
                tvName = name.Trim(); view.Log("Saved TV name: " + tvName);
            }
            catch (Exception ex) { view.Log("Could not save TV name: " + ex.Message); }
        }
        void StartConnect()
        {
            if (closing || tvMode || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            CancellationTokenSource current = new CancellationTokenSource();
            lock (operationLock) operation = current;
            string name = tvName;
            Report("Finding your TV", "Looking for " + name + ". Cancel with Laptop screen or Ctrl+Alt+T.", 1);
            Thread worker = new Thread(delegate() { ConnectFlow(name, current); });
            worker.IsBackground = true; worker.SetApartmentState(ApartmentState.MTA); worker.Start();
        }
        void ConnectFlow(string name, CancellationTokenSource current)
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
                            Report("Approve the connection on your TV", "Allow the connection on the TV. Waiting for Windows to report the named wireless display.", 2);
                        }
                        Pause(token, 1000);
                    }
                    if (target == null)
                    {
                        Report("Waiting for a verified TV", "If automatic selection is unavailable, press Win+K and select your TV manually. Save its exact display name and retry.", 2);
                        Stopwatch wait = Stopwatch.StartNew();
                        while (wait.ElapsedMilliseconds < 90000 && target == null)
                        {
                            Pause(token, 1500); target = SafetyPolicy.Select(Native.GetTargets(), name);
                        }
                    }
                    if (target == null) throw new InvalidOperationException("The named Miracast display was not confirmed. Check the TV permission prompt, TV name and Win+K connection.");
                }
                Report("TV display verified", target.Name + " is connected. Checking for a TV audio device (up to 15 seconds).", 2);
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
                        throw new InvalidOperationException("The display changed, or another external monitor is active. Disconnect other external displays and retry.");
                    switched = true; activeKey = fresh.Key; tvMode = true; misses = 0;
                    Native.SetTv(fresh);
                }
                bool verified = false;
                for (int i = 0; i < 12; i++)
                {
                    Pause(token, 500);
                    if (SafetyPolicy.IsOnlyTv(Native.GetTargets(), activeKey)) { verified = true; break; }
                }
                if (!verified) throw new InvalidOperationException("Windows did not confirm the TV-only layout. Returning to the laptop.");
                Report("Keep this TV screen?", "Confirm within 15 seconds. Otherwise the laptop screen is restored automatically.", 3);
                int answer = 0;
                Ui(delegate
                {
                    if (token.IsCancellationRequested) return;
                    ShowControls(); confirm = new ConfirmForm();
                    confirm.KeepRequested += delegate { Interlocked.Exchange(ref answer, 1); };
                    confirm.RevertRequested += delegate { Interlocked.Exchange(ref answer, -1); };
                    confirm.Show(view);
                });
                Stopwatch deadline = Stopwatch.StartNew();
                while (Interlocked.CompareExchange(ref answer, 0, 0) == 0 && deadline.ElapsedMilliseconds < 15000)
                {
                    Pause(token, 100);
                    int seconds = Math.Max(0, 15 - (int)(deadline.ElapsedMilliseconds / 1000));
                    Ui(delegate { if (confirm != null) confirm.SetSeconds(seconds); });
                }
                token.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref answer, 0, 0) != 1) throw new InvalidOperationException("TV mode was not kept. Laptop screen restored.");
                Report("Your TV is the only screen", audio ? "TV audio device detected. If sound stays on the laptop, choose the TV in Windows Sound settings." :
                    "TV audio device not detected. Choose the TV in Windows Sound settings if available.", 3);
                Notify("TV mode is on", "Ctrl+Alt+T returns to your laptop screen.");
            }
            catch (OperationCanceledException) { if (switched) RestoreNow("Connection cancelled."); }
            catch (Exception ex)
            {
                if (switched) RestoreNow("TV mode could not be kept.");
                Report("Connection needs attention", ex.Message, 0); Notify("Connection needs attention", ex.Message);
            }
            finally
            {
                lock (operationLock)
                {
                    current.Cancel();
                    if (operation == current) operation = null;
                    current.Dispose();
                }
                Interlocked.Exchange(ref busy, 0);
                Ui(delegate
                {
                    if (confirm != null) { confirm.Finish(); confirm = null; }
                    view.SetBusy(false, tvMode); tray.Icon = tvMode ? activeIcon : idleIcon;
                    tray.Text = tvMode ? "SamsungCONNECT - TV only" : "SamsungCONNECT - Laptop";
                });
            }
        }
        static void Pause(CancellationToken token, int milliseconds)
        {
            if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
        }
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
                Ui(delegate
                {
                    if (confirm != null) { confirm.Finish(); confirm = null; }
                    tray.Icon = idleIcon; tray.Text = "SamsungCONNECT - Laptop";
                    view.SetState("Laptop screen is on", reason, 0, busy != 0, false); view.Log(reason);
                });
                return true;
            }
            catch (Exception ex)
            {
                Report("Restore needs attention", "Press Win+P > PC screen only. " + ex.Message, 0);
                Notify("Restore needs attention", "Press Win+P > PC screen only."); return false;
            }
        }
        void Watchdog(object state)
        {
            if (closing || !tvMode || Interlocked.CompareExchange(ref watching, 1, 0) != 0) return;
            try
            {
                bool present = false;
                try { present = SafetyPolicy.IsOnlyTv(Native.GetTargets(), activeKey); }
                catch { /* Repeated query failures also restore instead of assuming the TV is safe. */ }
                if (present) Interlocked.Exchange(ref misses, 0);
                else if (Interlocked.Increment(ref misses) >= 2)
                {
                    CancelOperation();
                    if (RestoreNow("TV connection changed or disappeared. Laptop screen restored."))
                        Notify("Laptop restored", "TV connection changed or disappeared.");
                }
            }
            finally { Interlocked.Exchange(ref watching, 0); }
        }
        void StartDisconnect()
        {
            // Restore first; the user selects Disconnect for the intended TV in Windows' own UI.
            CancelOperation();
            Thread worker = new Thread(delegate()
            {
                if (RestoreNow("Laptop restored. Select Disconnect beside your TV in Windows Cast.")) Native.OpenCast();
            });
            worker.IsBackground = true; worker.Start();
        }
        bool IsStartup()
        {
            try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunPath)) return key != null && key.GetValue("SamsungCONNECT") != null; }
            catch { return false; }
        }
        void SetStartup(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath))
                {
                    if (enabled) key.SetValue("SamsungCONNECT", "\"" + Application.ExecutablePath + "\" --tray");
                    else key.DeleteValue("SamsungCONNECT", false);
                }
                view.Log(enabled ? "Startup enabled. The app will try to connect when you sign in." : "Startup disabled.");
            }
            catch (Exception ex) { view.SetStartup(IsStartup()); view.Log("Could not change startup: " + ex.Message); }
        }
        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            CancelOperation();
            if (tvMode) RestoreNow("Windows session ending.");
        }
        void ExitAndRestore()
        {
            CancelOperation();
            if ((tvMode || busy != 0) && !RestoreNow("Exiting SamsungCONNECT.")) return;
            closing = true; SystemEvents.SessionEnding -= OnSessionEnding; watchdog.Dispose();
            Native.UnregisterHotKey(hotkey.Handle, 9000); hotkey.DestroyHandle();
            tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); idleIcon.Dispose(); activeIcon.Dispose();
            view.Close(); view.Dispose(); ExitThread();
        }
        internal void RecoverBeforeExit()
        {
            CancelOperation();
            lock (topologyLock)
            {
                if (tvMode) { try { Native.RestoreInternal(); tvMode = false; } catch { /* Manual Win+P remains available. */ } }
            }
        }
        static Icon DrawIcon(bool active)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                using (Pen pen = new Pen(active ? Color.FromArgb(29, 78, 216) : Color.FromArgb(89, 101, 121), 2.5f))
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

    sealed class Dashboard : Form
    {
        internal event Action ConnectRequested, RestoreRequested, CastRequested, DisconnectRequested, ExitRequested;
        internal event Action<string> SaveRequested;
        internal event Action<bool> StartupRequested;
        readonly Label heading, detail, stage;
        readonly TextBox nameBox, activity;
        readonly Button connect, save;
        readonly CheckBox startup;
        readonly DisplayArt art;
        bool settingStartup;
        internal Dashboard(string tvName)
        {
            Text = "SamsungCONNECT"; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(244, 246, 250); ForeColor = Color.FromArgb(23, 35, 58);
            ClientSize = new Size(728, 728); MinimumSize = new Size(600, 480); AutoScroll = true;
            StartPosition = FormStartPosition.CenterScreen;
            AddLabel(this, "SamsungCONNECT", 28, 22, 650, 36, 23, true);
            AddLabel(this, "Your laptop. Your TV. One wireless workspace.", 30, 66, 650, 24, 11, false);
            Panel hero = new Panel { Left = 28, Top = 112, Width = 672, Height = 198, BackColor = Color.White }; Controls.Add(hero);
            art = new DisplayArt { Left = 20, Top = 23, Width = 150, Height = 140 }; hero.Controls.Add(art);
            stage = AddLabel(hero, "READY TO CONNECT", 188, 20, 460, 23, 9, true); stage.ForeColor = Color.FromArgb(29, 78, 216);
            heading = AddLabel(hero, "Ready for your TV", 186, 53, 460, 35, 18, true);
            detail = AddLabel(hero, "Turn on your TV and keep Wi-Fi enabled. Your laptop screen stays on until the TV is verified.", 188, 99, 452, 78, 10, false);
            AddLabel(this, "YOUR TV", 30, 332, 500, 22, 9, true);
            nameBox = new TextBox { Left = 28, Top = 361, Width = 476, Height = 32, Text = tvName, MaxLength = 64, AccessibleName = "TV name to match" }; Controls.Add(nameBox);
            save = MakeButton("Save TV name", 516, 357, 184, 38, false);
            save.Click += delegate { if (SaveRequested != null) SaveRequested(nameBox.Text); };
            AddLabel(this, "Use the unique name shown in Win+K. Only matching TVs are selected.", 30, 402, 665, 22, 9, false);
            connect = MakeButton("Connect & use TV", 28, 442, 326, 48, true);
            connect.Click += delegate { if (ConnectRequested != null) ConnectRequested(); };
            Button restore = MakeButton("Laptop screen / cancel", 370, 442, 330, 48, false);
            restore.Click += delegate { if (RestoreRequested != null) RestoreRequested(); };
            Button cast = MakeButton("Open Windows Cast", 28, 503, 218, 36, false);
            cast.Click += delegate { if (CastRequested != null) CastRequested(); };
            Button disconnect = MakeButton("Disconnect TV...", 258, 503, 218, 36, false);
            disconnect.Click += delegate { if (DisconnectRequested != null) DisconnectRequested(); };
            Button exit = MakeButton("Exit & restore laptop", 488, 503, 212, 36, false);
            exit.Click += delegate { if (ExitRequested != null) ExitRequested(); };
            startup = new CheckBox { Left = 30, Top = 554, Width = 650, Height = 27, Text = "Connect automatically when I sign in to Windows", AccessibleName = "Start and connect at Windows sign-in" };
            startup.CheckedChanged += delegate { if (!settingStartup && StartupRequested != null) StartupRequested(startup.Checked); }; Controls.Add(startup);
            activity = new TextBox { Left = 28, Top = 595, Width = 672, Height = 76, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Connection activity" }; Controls.Add(activity);
            AddLabel(this, "Ctrl+Alt+T  Switch screens     |     Close this window to keep running in the tray", 30, 688, 670, 23, 9, false);
            Shown += delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Height > area.Height) Height = area.Height;
                if (Width > area.Width) Width = area.Width;
            };
        }
        static Label AddLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold)
        {
            Label label = new Label { Text = text, Left = x, Top = y, Width = w, Height = h, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            parent.Controls.Add(label); return label;
        }
        Button MakeButton(string text, int x, int y, int w, int h, bool primary)
        {
            Button button = new Button { Text = text, Left = x, Top = y, Width = w, Height = h, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(29, 78, 216) : Color.White, ForeColor = primary ? Color.White : ForeColor, Cursor = Cursors.Hand, AccessibleName = text, UseMnemonic = false };
            button.FlatAppearance.BorderColor = primary ? button.BackColor : Color.FromArgb(205, 213, 225); Controls.Add(button); return button;
        }
        internal void SetStartup(bool enabled) { settingStartup = true; startup.Checked = enabled; settingStartup = false; }
        internal void SetState(string title, string message, int step, bool busy, bool active)
        {
            heading.Text = title; detail.Text = message;
            stage.Text = active ? "TV MODE  /  LAPTOP DISPLAY OFF" : step == 1 ? "01  /  FIND YOUR TV" : step == 2 ? "02  /  VERIFY CONNECTION" : "READY TO CONNECT";
            art.Active = active; art.Invalidate(); SetBusy(busy, active);
        }
        internal void SetBusy(bool busy, bool active)
        {
            connect.Enabled = !busy && !active; save.Enabled = !busy && !active; nameBox.Enabled = !busy && !active;
            connect.Text = busy ? "Connecting..." : active ? "TV mode is active" : "Connect & use TV";
            connect.BackColor = connect.Enabled ? Color.FromArgb(29, 78, 216) : Color.FromArgb(229, 233, 240);
            connect.ForeColor = connect.Enabled ? Color.White : Color.FromArgb(23, 35, 58);
            connect.FlatAppearance.BorderColor = connect.BackColor;
        }
        internal void Log(string text)
        {
            if (activity.TextLength > 10000) activity.Clear();
            activity.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        }
    }
    sealed class DisplayArt : Control
    {
        internal bool Active;
        internal DisplayArt() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(Width / 150f, Height / 140f); g.ScaleTransform(scale, scale);
            using (Pen pen = new Pen(Color.FromArgb(29, 78, 216), 3))
            using (SolidBrush screen = new SolidBrush(Active ? Color.FromArgb(219, 234, 254) : Color.FromArgb(240, 244, 250)))
            {
                g.FillRectangle(screen, 12, 38, 125, 70); g.DrawRectangle(pen, 12, 38, 125, 70);
                g.DrawLine(pen, 75, 108, 75, 121); g.DrawLine(pen, 53, 123, 98, 123);
                g.DrawArc(pen, 51, 6, 48, 36, 215, 110); g.DrawArc(pen, 62, 18, 26, 20, 215, 110);
            }
        }
    }
    sealed class ConfirmForm : Form
    {
        internal event Action KeepRequested, RevertRequested;
        readonly Label countdown;
        bool finished;
        internal ConfirmForm()
        {
            Text = "Keep TV mode?"; Font = new Font("Segoe UI", 11); ClientSize = new Size(470, 185); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            countdown = new Label { Left = 20, Top = 20, Width = 430, Height = 72 }; Controls.Add(countdown); SetSeconds(15);
            Button keep = new Button { Left = 20, Top = 115, Width = 205, Height = 42, Text = "Keep TV mode" };
            Button revert = new Button { Left = 240, Top = 115, Width = 210, Height = 42, Text = "Return to laptop" };
            keep.Click += delegate { if (KeepRequested != null) KeepRequested(); };
            revert.Click += delegate { if (RevertRequested != null) RevertRequested(); };
            Controls.Add(keep); Controls.Add(revert); CancelButton = revert;
            FormClosing += delegate { if (!finished && RevertRequested != null) RevertRequested(); };
        }
        internal void SetSeconds(int seconds) { countdown.Text = "Can you see this window on your TV?\nReturning to your laptop in " + seconds + " seconds unless you keep TV mode."; }
        internal void Finish() { finished = true; Close(); Dispose(); }
    }
    sealed class HotkeyWindow : NativeWindow
    {
        internal event Action Pressed;
        internal HotkeyWindow() { CreateHandle(new CreateParams { Caption = "SamsungCONNECT hotkey", Parent = new IntPtr(-3) }); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x312 && m.WParam.ToInt32() == 9000 && Pressed != null) Pressed();
            base.WndProc(ref m);
        }
    }
    static class CastAutomation
    {
        internal static bool TryConnect(string name)
        {
            AutomationElement pane = FindPane(); if (pane == null) return false;
            try
            {
                List<AutomationElement> candidates = new List<AutomationElement>();
                AutomationElementCollection items = pane.FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                foreach (AutomationElement item in items)
                {
                    string label = item.Current.Name ?? "";
                    if (!SafetyPolicy.Matches(label, name) || !item.Current.IsEnabled || item.Current.IsOffscreen ||
                        label.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    candidates.Add(item);
                }
                if (candidates.Count > 1) throw new InvalidOperationException("More than one cast device matches. Use the TV's full unique name.");
                foreach (AutomationElement item in candidates)
                {
                    object pattern;
                    if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) { ((InvokePattern)pattern).Invoke(); return true; }
                    if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) { ((SelectionItemPattern)pattern).Select(); return true; }
                }
            }
            catch (ElementNotAvailableException) { }
            return false;
        }
        static AutomationElement FindPane()
        {
            try
            {
                AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Cast"), new PropertyCondition(AutomationElement.NameProperty, "Connect"),
                        new PropertyCondition(AutomationElement.NameProperty, "Cast to")));
                string windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
                foreach (AutomationElement window in windows)
                {
                    using (Process process = Process.GetProcessById(window.Current.ProcessId))
                    {
                        string path = Path.GetFullPath(process.MainModule.FileName), file = Path.GetFileName(path);
                        bool shellApp = (file.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("ShellHost.exe", StringComparison.OrdinalIgnoreCase)) &&
                            path.StartsWith(windowsRoot + "SystemApps\\", StringComparison.OrdinalIgnoreCase);
                        bool systemShell = path.Equals(windowsRoot + "explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                            path.Equals(windowsRoot + "System32\\ShellHost.exe", StringComparison.OrdinalIgnoreCase);
                        if (shellApp || systemShell) return window;
                    }
                }
            }
            catch { /* Unrecognized shell: let the user select the TV manually. */ }
            return null;
        }
    }
    sealed class DisplayTarget
    {
        internal string Name, Key;
        internal bool Wireless, Internal;
        internal Native.PathInfo Path;
    }
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct SourceInfo { internal Luid Adapter; internal uint Id, Mode, Status; }
        [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
        {
            internal Luid Adapter; internal uint Id, Mode, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, Scanline;
            internal int Available; internal uint Status;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { internal SourceInfo Source; internal TargetInfo Target; internal uint Flags; }
        // 16-byte mode header plus 48-byte native union. Mode data is not interpreted here.
        [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo { [FieldOffset(0)] internal uint Type; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceHeader { internal uint Type, Size; internal Luid Adapter; internal uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TargetName
        {
            internal DeviceHeader Header; internal uint Flags, Technology; internal ushort Manufacturer, Product; internal uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DevicePath;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WaveCaps
        {
            internal ushort Manufacturer, Product; internal uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal uint Formats; internal ushort Channels, Reserved; internal uint Support;
        }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, [Out] ModeInfo[] modeArray, IntPtr topology);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)] static extern int GetTargetName(ref TargetName name);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint paths, IntPtr pathArray, uint modes, IntPtr modeArray, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")] static extern int SetDisplayPaths(uint paths, [In] PathInfo[] pathArray, uint modes, IntPtr modeArray, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)] static extern uint waveOutGetDevCaps(UIntPtr device, ref WaveCaps caps, uint size);
        internal static List<DisplayTarget> GetTargets()
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint paths, modes; int result = GetDisplayConfigBufferSizes(2, out paths, out modes);
                if (result != 0) throw new Win32Exception(result, "Cannot read Windows display configuration.");
                if (paths > 256 || modes > 1024) throw new InvalidOperationException("Unexpected Windows display buffer size.");
                PathInfo[] pathArray = new PathInfo[Math.Max(paths, 1)]; ModeInfo[] modeArray = new ModeInfo[Math.Max(modes, 1)];
                result = QueryDisplayConfig(2, ref paths, pathArray, ref modes, modeArray, IntPtr.Zero);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception(result, "Cannot query active Windows displays.");
                List<DisplayTarget> targets = new List<DisplayTarget>(); HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths; i++)
                {
                    TargetInfo target = pathArray[i].Target; TargetName name = new TargetName();
                    name.Header.Type = 2; name.Header.Size = (uint)Marshal.SizeOf(typeof(TargetName)); name.Header.Adapter = target.Adapter; name.Header.Id = target.Id;
                    result = GetTargetName(ref name);
                    if (result != 0) throw new Win32Exception(result, "Cannot identify an active display.");
                    string key = target.Adapter.High + ":" + target.Adapter.Low + ":" + target.Id + ":" + name.DevicePath;
                    if (!seen.Add(key)) continue;
                    uint technology = name.Technology; // Query the base connector type, including Miracast.
                    targets.Add(new DisplayTarget { Name = name.Name ?? "", Key = key, Wireless = technology == 15,
                        Internal = technology == 0x80000000 || technology == 6 || technology == 11 || technology == 13, Path = pathArray[i] });
                }
                return targets;
            }
            throw new InvalidOperationException("Displays are changing. Wait a moment and retry.");
        }
        internal static PathInfo TvPath(DisplayTarget target)
        {
            if (target == null || !target.Wireless) throw new InvalidOperationException("A verified wireless display is required.");
            PathInfo path = target.Path;
            path.Source.Mode = uint.MaxValue; path.Target.Mode = uint.MaxValue; path.Flags = 1;
            return path;
        }
        internal static void SetTv(DisplayTarget target)
        {
            PathInfo[] paths = new PathInfo[] { TvPath(target) };
            // Supply only the verified TV path. Never ask Windows to choose a remembered external monitor.
            int result = SetDisplayPaths(1, paths, 0, IntPtr.Zero, 0x460); // VALIDATE | USE_SUPPLIED | ALLOW_CHANGES
            if (result != 0) throw new Win32Exception(result, "Windows could not validate the TV-only layout.");
            result = SetDisplayPaths(1, paths, 0, IntPtr.Zero, 0x4A0); // APPLY | USE_SUPPLIED | ALLOW_CHANGES
            if (result != 0) throw new Win32Exception(result, "Windows could not apply the TV-only layout.");
        }
        internal static void RestoreInternal()
        {
            int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x81);
            if (result != 0) throw new Win32Exception(result, "Windows could not change display mode.");
        }
        internal static bool HasTvAudio(string name)
        {
            uint count = Math.Min(waveOutGetNumDevs(), 256);
            for (uint i = 0; i < count; i++)
            {
                WaveCaps caps = new WaveCaps();
                if (waveOutGetDevCaps(new UIntPtr(i), ref caps, (uint)Marshal.SizeOf(typeof(WaveCaps))) == 0 &&
                    (SafetyPolicy.Matches(caps.Name, name) || SafetyPolicy.Matches(caps.Name, "Samsung") || SafetyPolicy.Matches(caps.Name, "Wireless Display"))) return true;
            }
            return false;
        }
        internal static void OpenCast()
        {
            keybd_event(0x5B, 0, 0, UIntPtr.Zero);
            try { keybd_event(0x4B, 0, 0, UIntPtr.Zero); }
            finally { keybd_event(0x4B, 0, 2, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero); }
        }
    }
}
