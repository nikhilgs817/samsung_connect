using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using Microsoft.Win32;

namespace SamsungCONNECT
{
    /// <summary>
    /// SamsungCONNECT - one-click wireless "TV as your only screen" for Windows 11.
    ///
    /// Flow on launch:
    ///   Step 1: open Windows' Connect pane (same engine as Win+K) and click your
    ///           Samsung TV automatically (Miracast over Wi-Fi Direct).
    ///   Step 2: wait until the TV shows up as a real display AND its audio
    ///           endpoint (TV speakers) appears - connectivity is confirmed.
    ///   Step 3: switch display topology to "Second screen only" so the laptop
    ///           panel turns completely OFF and the TV becomes the only screen.
    /// Safety: if the wireless link drops while in TV mode, the laptop screen
    /// comes back on automatically.
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            Mutex m = new Mutex(true, "SamsungCONNECTSingleInstance", out createdNew);
            if (!createdNew) return; // already sitting in the tray

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
            GC.KeepAlive(m);
        }
    }

    class TrayApp : ApplicationContext
    {
        // ----------------------------- state --------------------------------
        private NotifyIcon tray;
        private ContextMenuStrip menuHost;   // doubles as an Invoke() marshaler
        private bool tvMode = false;         // true = TV is the only active screen
        private bool flowRunning = false;
        private string tvName = "SAMSUNG";   // configurable via tray menu
        private HotkeyWindow hotkeyWindow;
        private System.Threading.Timer watchdog;

        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_T = 0x54;
        private const uint VK_ESCAPE = 0x1B;
        private const byte VK_LWIN = 0x5B;
        private const byte VK_K = 0x4B;
        private const uint KEYUP = 0x0002;
        private const uint MOUSE_LEFTDOWN = 0x0002;
        private const uint MOUSE_LEFTUP = 0x0004;

        private const string REG_PATH = @"Software\SamsungCONNECT";
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "SamsungCONNECT";

        public TrayApp()
        {
            LoadSettings();

            menuHost = new ContextMenuStrip();
            tray = new NotifyIcon();
            tray.Icon = BuildIcon(false);
            tray.Visible = true;
            tray.Text = "SamsungCONNECT - click to toggle TV / laptop screen";
            RebuildMenu();
            tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && !flowRunning) Toggle();
            };

            hotkeyWindow = new HotkeyWindow();
            hotkeyWindow.HotkeyPressed += () =>
            {
                if (!flowRunning) Toggle();
            };
            RegisterHotKey(hotkeyWindow.Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_T);

            // watchdog: never leave the user on a dead screen
            watchdog = new System.Threading.Timer(WatchdogTick, null, 2500, 2500);

            // As soon as the app is launched, run the whole show.
            Thread t = new Thread(() => ConnectFlow());
            t.IsBackground = true;
            t.Start();
        }

        // ----------------------------------------------------------- settings

        private void LoadSettings()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(REG_PATH, false))
                    if (k != null)
                    {
                        object v = k.GetValue("TVName");
                        if (v is string && ((string)v).Trim().Length > 0)
                            tvName = ((string)v).Trim();
                    }
            }
            catch { }
        }

        private void SaveTvName(string name)
        {
            tvName = name.Trim();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(REG_PATH))
                    k.SetValue("TVName", tvName);
            }
            catch { }
            RebuildMenu();
        }

        // -------------------------------------------------------- main flow

        private void ConnectFlow()
        {
            if (flowRunning) return;
            flowRunning = true;
            try
            {
                Notify("SamsungCONNECT", "Step 1/3 - looking for your " + tvName + " TV...");

                if (!WifiLooksConnected())
                {
                    DialogResult r = AskUi("Wi-Fi does not look connected.\r\n\r\n" +
                        "Miracast needs Wi-Fi on the laptop. Continue anyway?",
                        "SamsungCONNECT");
                    if (r == DialogResult.No) { Notify("SamsungCONNECT", "Cancelled - Wi-Fi is off."); return; }
                }

                // Baselines for confirmation step
                DisplaySnapshot baseDisp = GetDisplaySnapshot();
                List<string> baseAudio = GetAudioDeviceNames();
                bool alreadyConnected = baseDisp.AttachedCount >= 2 || HasIndirectDisplay(baseDisp);

                bool displayOk = alreadyConnected;
                bool audioOk = ContainsTvName(baseAudio);

                if (!alreadyConnected)
                {
                    // ---- Step 1: open the Connect pane and click the TV ----
                    bool clicked = OpenPaneAndClickTv(60000);
                    if (!clicked)
                    {
                        Notify("TV not found",
                            "The " + tvName + " TV did not appear in the Connect pane. " +
                            "Turn the TV on, open its Screen Mirroring / cast source if needed, " +
                            "then right-click this icon and choose 'Connect to Samsung TV'. " +
                            "See 'Help - first-time setup'.");
                        return;
                    }

                    // ---- Step 2: confirm connectivity (display + TV speakers) ----
                    Notify("SamsungCONNECT", "Step 2/3 - connecting, waiting for the TV (display + speakers)...");
                    Stopwatch sw = Stopwatch.StartNew();
                    bool displaySeenAt = false;
                    long displaySeenMs = -1;
                    while (sw.ElapsedMilliseconds < 90000)
                    {
                        DisplaySnapshot now = GetDisplaySnapshot();
                        if (!displayOk)
                            displayOk = now.AttachedCount > baseDisp.AttachedCount || HasIndirectDisplay(now);
                        if (displayOk && displaySeenMs < 0) { displaySeenMs = sw.ElapsedMilliseconds; displaySeenAt = true; }

                        if (!audioOk && ContainsTvName(GetAudioDeviceNames()))
                            audioOk = true;

                        // once the display is up, give the TV speakers a short grace period
                        if (displayOk && (audioOk || sw.ElapsedMilliseconds - displaySeenMs > 15000))
                            break;

                        Thread.Sleep(2000);
                    }
                    GC.KeepAlive(displaySeenAt);

                    if (!displayOk)
                    {
                        Notify("Connection not confirmed",
                            "The TV did not come up as a display in time. Check that the TV shows " +
                            "'Allow sharing?' and approve it, then retry from the tray menu.");
                        return;
                    }
                    if (!audioOk)
                        Notify("SamsungCONNECT", "TV screen confirmed. TV speakers not detected - audio may stay on the laptop.");
                }
                else
                {
                    Notify("SamsungCONNECT", "TV is already connected - switching screen now.");
                }

                // ---- Step 3: TV becomes the only screen ----
                Notify("SamsungCONNECT", "Step 3/3 - TV is your only screen now. Enjoy!");
                SetMode("/external", true);
                PressEscape(); // close the Connect pane if it is still open
                Ui(() =>
                {
                    tray.BalloonTipTitle = "TV Mode ON";
                    tray.BalloonTipText = "Your " + tvName + " TV is now the only screen (laptop screen is OFF). " +
                                          "Ctrl+Alt+T or click this icon to switch back.";
                    tray.ShowBalloonTip(4000);
                });
            }
            finally
            {
                flowRunning = false;
            }
        }

        private void DisconnectFlow()
        {
            Thread t = new Thread(() =>
            {
                SetMode("/internal", true);
                // try to gracefully disconnect the Miracast session
                SendWinK();
                Thread.Sleep(3000);
                AutomationElement pane = WaitForPane(10000);
                bool done = false;
                if (pane != null)
                {
                    AutomationElement btn = FindByName(pane, "Disconnect");
                    if (btn != null) done = TryActivate(btn);
                }
                PressEscape();
                Notify("SamsungCONNECT",
                    done ? "Disconnected from the TV." :
                           "Laptop screen restored. To fully release the TV, press Win+K and click Disconnect.");
            });
            t.IsBackground = true;
            t.Start();
        }

        // -------------------------------------------- connect-pane automation

        private bool OpenPaneAndClickTv(int timeoutMs)
        {
            SendWinK();
            Stopwatch sw = Stopwatch.StartNew();
            long lastReopen = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                AutomationElement pane = FindConnectPane();
                if (pane == null)
                {
                    if (sw.ElapsedMilliseconds - lastReopen > 5000) { SendWinK(); lastReopen = sw.ElapsedMilliseconds; }
                    Thread.Sleep(1000);
                    continue;
                }

                AutomationElement tv = FindTvElement(pane);
                if (tv == null) { Thread.Sleep(1500); continue; }

                if (TryActivate(tv)) return true;
                Thread.Sleep(1500);
            }
            PressEscape();
            return false;
        }

        private static AutomationElement FindConnectPane()
        {
            try
            {
                AutomationElement root = AutomationElement.RootElement;
                Condition cond = new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Connect"),
                    new PropertyCondition(AutomationElement.NameProperty, "Cast"));
                AutomationElement el = root.FindFirst(TreeScope.Children, cond);
                if (el != null) return el;
                return root.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.NameProperty, "Cast to"));
            }
            catch { return null; }
        }

        private static AutomationElement WaitForPane(int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                AutomationElement p = FindConnectPane();
                if (p != null) return p;
                Thread.Sleep(500);
            }
            return null;
        }

        private AutomationElement FindTvElement(AutomationElement pane)
        {
            AutomationElementCollection all;
            try { all = pane.FindAll(TreeScope.Descendants, Condition.TrueCondition); }
            catch { return null; }

            // 1) best: element whose name contains the configured TV name
            for (int i = 0; i < all.Count; i++)
            {
                string name = SafeName(all[i]);
                if (name.IndexOf(tvName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    name.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) < 0)
                    return all[i];
            }
            // 2) fallback: first real list item (the remembered TV is usually the only one)
            for (int i = 0; i < all.Count; i++)
            {
                AutomationElement el = all[i];
                string name = SafeName(el);
                if (name.Length < 3) continue;
                if (name == "Connect" || name == "Cast" || name == "Cast to") continue;
                if (name.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                ControlType ct = null;
                try { ct = el.Current.ControlType; } catch { continue; }
                if (ct == ControlType.ListItem || ct == ControlType.Button)
                    return el;
            }
            return null;
        }

        private static AutomationElement FindByName(AutomationElement pane, string name)
        {
            try
            {
                AutomationElementCollection all = pane.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                for (int i = 0; i < all.Count; i++)
                {
                    string n = SafeName(all[i]);
                    if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return all[i];
                }
            }
            catch { }
            return null;
        }

        private static string SafeName(AutomationElement el)
        {
            try { return el.Current.Name ?? ""; }
            catch { return ""; }
        }

        private static bool TryActivate(AutomationElement el)
        {
            try
            {
                InvokePattern inv = el.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                if (inv != null) { inv.Invoke(); return true; }
            }
            catch { }
            try
            {
                SelectionItemPattern sel = el.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                if (sel != null) { sel.Select(); return true; }
            }
            catch { }
            try
            {
                System.Windows.Point p = el.GetClickablePoint();
                Cursor.Position = new Point((int)p.X, (int)p.Y);
                mouse_event(MOUSE_LEFTDOWN | MOUSE_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                return true;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------- display & audio

        private static bool WifiLooksConnected()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                Process p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                return output.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return true; }
        }

        private static bool HasIndirectDisplay(DisplaySnapshot snap)
        {
            for (int i = 0; i < snap.Names.Count; i++)
                if (snap.Names[i].IndexOf("indirect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    snap.Names[i].IndexOf("mira", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private bool ContainsTvName(List<string> audioNames)
        {
            for (int i = 0; i < audioNames.Count; i++)
            {
                string n = audioNames[i];
                if (n.IndexOf(tvName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (n.IndexOf("samsung", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (n.IndexOf("wireless", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    n.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private void WatchdogTick(object state)
        {
            if (!tvMode || flowRunning) return;
            try
            {
                DisplaySnapshot snap = GetDisplaySnapshot();
                bool externalThere = snap.AttachedCount >= 2 || HasIndirectDisplay(snap);
                if (!externalThere)
                {
                    // wireless link dropped - bring the laptop screen back NOW
                    SetMode("/internal", true);
                    Notify("TV connection lost",
                        "The wireless display disappeared, so your laptop screen was switched back on.");
                }
            }
            catch { }
        }

        // ---------------------------------------------- display mode switch

        private static string DisplaySwitchPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "displayswitch.exe"); }
        }

        private void SetMode(string arg, bool silent)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(DisplaySwitchPath, arg)
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                AskUi("Could not switch display mode:\r\n" + ex.Message, "SamsungCONNECT");
                return;
            }

            bool nowTv = (arg == "/external");
            Ui(() =>
            {
                tvMode = nowTv;
                tray.Icon = BuildIcon(tvMode);
                tray.Text = tvMode
                    ? "SamsungCONNECT - TV is your only screen (Ctrl+Alt+T to switch back)"
                    : "SamsungCONNECT - click to use the TV as your only screen";
                RebuildMenu();
                if (!silent)
                {
                    tray.BalloonTipTitle = tvMode ? "TV Mode ON" : "Laptop screen ON";
                    tray.BalloonTipText = tvMode
                        ? "The internal display is OFF. Press Ctrl+Alt+T or click the tray icon to switch back."
                        : "Back to the laptop's built-in screen.";
                    tray.ShowBalloonTip(3000);
                }
            });
        }

        private void Toggle()
        {
            SetMode(tvMode ? "/internal" : "/external", false);
        }

        // ---------------------------------------------------------------- UI

        private void RebuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();

            ToolStripMenuItem connect = new ToolStripMenuItem("Connect to " + tvName + " TV", null,
                (s, e) => { Thread t = new Thread(() => ConnectFlow()); t.IsBackground = true; t.Start(); });
            m.Items.Add(connect);

            ToolStripMenuItem tvOnly = new ToolStripMenuItem("TV Mode - laptop screen off, TV only", null,
                (s, e) => { if (!flowRunning) SetMode("/external", false); });
            tvOnly.Checked = tvMode;
            m.Items.Add(tvOnly);

            ToolStripMenuItem laptop = new ToolStripMenuItem("Laptop screen only (keep TV connected)", null,
                (s, e) => { if (!flowRunning) SetMode("/internal", false); });
            laptop.Checked = !tvMode;
            m.Items.Add(laptop);

            m.Items.Add(new ToolStripMenuItem("Disconnect TV completely", null,
                (s, e) => { if (!flowRunning) DisconnectFlow(); }));

            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(new ToolStripMenuItem("Set TV name... (default: SAMSUNG)", null,
                (s, e) =>
                {
                    string name = PromptForm.Show("Set TV name",
                        "Type the brand/name Windows shows for your TV\n(e.g. SAMSUNG, or the full name like SAMSUNG QN65):", tvName);
                    if (name != null && name.Trim().Length > 0) SaveTvName(name);
                }));

            ToolStripMenuItem startup = new ToolStripMenuItem("Start SamsungCONNECT automatically with Windows", null,
                (s, e) => ToggleStartup());
            startup.Checked = IsStartupEnabled();
            m.Items.Add(startup);

            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(new ToolStripMenuItem("Help - first-time setup", null, (s, e) => ShowHelp()));

            m.Items.Add(new ToolStripMenuItem("Exit and turn the laptop screen back on", null,
                (s, e) => ExitAndRestore()));

            Ui(() =>
            {
                ContextMenuStrip old = tray.ContextMenuStrip;
                tray.ContextMenuStrip = m;
                menuHost = m;
                if (old != null) old.Dispose();
            });
        }

        private void ShowHelp()
        {
            AskUi(
                "FIRST-TIME SETUP (only once):\r\n\r\n" +
                "1. Turn your Samsung TV on.\r\n" +
                "2. If Windows cannot find the TV: on the TV open Quick Settings ->\r\n" +
                "    Screen Mirroring (or Source -> Screen Mirroring / Cast).\r\n" +
                "3. Use 'Set TV name...' in this menu to match your TV's name\r\n" +
                "    (default: SAMSUNG).\r\n" +
                "4. Choose 'Connect to TV'. The TV asks 'Allow sharing?' -> Allow\r\n" +
                "    (tick 'Always allow' if it offers).\r\n\r\n" +
                "After that, Windows remembers the TV and everything is one click.\r\n\r\n" +
                "NOTES:\r\n" +
                "- Keep Wi-Fi on; Miracast uses Wi-Fi Direct.\r\n" +
                "- If picture is on the TV but sound stays on the laptop: right-click\r\n" +
                "    the speaker icon and pick the Samsung TV as playback device\r\n" +
                "    (Windows remembers it afterwards).\r\n" +
                "- Hotkey: Ctrl+Alt+T toggles between TV-only and laptop screen.",
                "SamsungCONNECT - Help");
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                return k != null && k.GetValue(APP_NAME) != null;
        }

        private void ToggleStartup()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (IsStartupEnabled()) k.DeleteValue(APP_NAME, false);
                else k.SetValue(APP_NAME, "\"" + Application.ExecutablePath + "\"");
            }
            RebuildMenu();
        }

        private void ExitAndRestore()
        {
            if (tvMode) SetMode("/internal", true);
            Ui(() =>
            {
                tray.Visible = false;
                UnregisterHotKey(hotkeyWindow.Handle, HOTKEY_ID);
                hotkeyWindow.DestroyHandle();
                Application.Exit();
            });
        }

        // ------------------------------------------------------- UI helpers

        private void Ui(Action a)
        {
            try
            {
                Control c = menuHost;
                if (c != null && c.InvokeRequired) c.Invoke(a);
                else a();
            }
            catch { a(); }
        }

        private void Notify(string title, string text)
        {
            Ui(() =>
            {
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = text;
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(4000);
            });
        }

        private DialogResult AskUi(string text, string caption)
        {
            DialogResult result = DialogResult.OK;
            Ui(() => { result = MessageBox.Show(text, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information); });
            return result;
        }

        // ---------------------------------------------------------------- icon

        private static Icon BuildIcon(bool on)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // TV body
                Rectangle body = new Rectangle(3, 4, 26, 18);
                using (SolidBrush fill = new SolidBrush(on ? Color.FromArgb(0, 120, 215) : Color.FromArgb(110, 110, 110)))
                    g.FillRectangle(fill, body);
                using (Pen pen = new Pen(Color.White, 1.6f))
                {
                    g.DrawRectangle(pen, body);
                    g.DrawLine(pen, 16, 22, 16, 27);
                    g.DrawLine(pen, 10, 28, 22, 28);
                }
                // Wi-Fi arcs above the TV (wireless!)
                using (Pen wifi = new Pen(on ? Color.FromArgb(0, 200, 255) : Color.Gray, 1.6f))
                {
                    g.DrawArc(wifi, 10, -6, 12, 12, 300, 120);
                    g.DrawArc(wifi, 12, -3, 8, 8, 300, 120);
                }
                // power LED
                using (SolidBrush led = new SolidBrush(on ? Color.LimeGreen : Color.DarkGray))
                    g.FillEllipse(led, 15, 24, 3, 3);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // -------------------------------------------------------------- input

        private static void SendWinK()
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_K, 0, 0, UIntPtr.Zero);
            keybd_event(VK_K, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
        }

        private static void PressEscape()
        {
            keybd_event((byte)VK_ESCAPE, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VK_ESCAPE, 0, KEYUP, UIntPtr.Zero);
        }

        // ------------------------------------------------------------ native

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("winmm.dll")]
        private static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveOutGetDevCaps(uint uDeviceID, ref WAVEOUTCAPS pwoc, uint cbwoc);

        private static DisplaySnapshot GetDisplaySnapshot()
        {
            DisplaySnapshot snap = new DisplaySnapshot();
            DISPLAY_DEVICE d = new DISPLAY_DEVICE();
            d.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            uint i = 0;
            while (EnumDisplayDevices(null, i, ref d, 0))
            {
                if ((d.StateFlags & 0x1) != 0) // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                {
                    snap.AttachedCount++;
                    snap.Names.Add(d.DeviceString);
                }
                i++;
                d.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            }
            return snap;
        }

        private static List<string> GetAudioDeviceNames()
        {
            List<string> names = new List<string>();
            uint count = waveOutGetNumDevs();
            for (uint i = 0; i < count; i++)
            {
                WAVEOUTCAPS caps = new WAVEOUTCAPS();
                if (waveOutGetDevCaps(i, ref caps, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0)
                    names.Add(caps.szPname ?? "");
            }
            return names;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEOUTCAPS
        {
            public short wMid;
            public short wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szPname;
            public uint dwFormats;
            public short wChannels;
            public short wReserved1;
            public uint dwSupport;
        }

        private class DisplaySnapshot
        {
            public int AttachedCount;
            public List<string> Names = new List<string>();
        }
    }

    /// <summary>Minimal modal prompt with a text box (avoids extra dependencies).</summary>
    class PromptForm : Form
    {
        private TextBox box;
        private DialogResult result = DialogResult.Cancel;
        private string value;

        private PromptForm(string title, string label, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 420;
            Height = 190;

            Label l = new Label();
            l.Text = label;
            l.Left = 12; l.Top = 12; l.Width = 380; l.Height = 45;

            box = new TextBox();
            box.Text = initial;
            box.Left = 12; box.Top = 62; box.Width = 380;

            Button ok = new Button();
            ok.Text = "OK"; ok.DialogResult = DialogResult.OK;
            ok.Left = 230; ok.Top = 95; ok.Width = 75;

            Button cancel = new Button();
            cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel;
            cancel.Left = 317; cancel.Top = 95; cancel.Width = 75;

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(l); Controls.Add(box); Controls.Add(ok); Controls.Add(cancel);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult == DialogResult.OK) value = box.Text;
            base.OnClosing(e);
        }

        public static string Show(string title, string label, string initial)
        {
            PromptForm f = new PromptForm(title, label, initial);
            f.ShowDialog();
            return f.value;
        }
    }

    /// <summary>Message-only window so the global hotkey works without any visible UI.</summary>
    class HotkeyWindow : NativeWindow
    {
        public event Action HotkeyPressed;

        public HotkeyWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "SamsungCONNECTHotkeyWindow";
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY && HotkeyPressed != null)
                HotkeyPressed();
            base.WndProc(ref m);
        }
    }
}
