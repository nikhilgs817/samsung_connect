using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SamsungCONNECT
{
    // Everything the window can ask the app to do. Tests supply a harmless stand-in.
    interface IAppActions
    {
        void ConnectWireless();
        void ConnectHdmi();
        void Cancel();
        void OpenLauncherHere();
        void BackToLaptop();
        void Exit();
        void Launch(ServiceInfo service);
        void Resume();
        string ResumeName { get; }
        string TvName { get; }
        string SaveTvName(string name);
        bool Startup { get; }
        string SetStartup(bool on);
        bool DetectHdmi(out string label);
        void OpenCast();
        void RefreshLogos();
        void SetTheme(string mode);
        string BrowserName { get; }
        LogoCache Logos { get; }
        List<ServiceInfo> CustomTiles { get; }
        void SaveCustomTiles(List<ServiceInfo> tiles);
        List<string> HiddenTiles { get; }
        void SaveHiddenTiles(List<string> ids);
    }

    sealed class MainWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        readonly IAppActions app;
        readonly Grid shell = new Grid(), pageHost = new Grid(), overlayHost = new Grid(), toastHost = new Grid();
        readonly ScaleTransform pageScale = new ScaleTransform(1, 1), overlayScale = new ScaleTransform(1, 1);
        readonly DispatcherTimer clock, hdmiPoll;
        bool launcher, fullscreen, busy, statusError, hdmiFound;
        int statusStep;
        string connectionLabel = "This screen", statusTitle = "Ready when you are", statusDetail = "Turn the TV on first. Your laptop screen stays on until the TV is checked.", lastLog = "", hdmiLabel;
        string overlayKind; Func<UIElement> overlayFactory; Action overlayCancel;
        Action<bool> confirmAnswer; int confirmLeft = 15;
        TextBlock clockText, dateText, greetText, confirmSeconds, confirmDetail;
        Neu resumeChip; TextBlock resumeText;
        Dictionary<string, Border> iconHosts = new Dictionary<string, Border>();
        Dictionary<string, ServiceInfo> services = new Dictionary<string, ServiceInfo>();
        FrameworkElement pageDefault;
        string rememberFocus;
        DispatcherTimer toastTimer;

        internal MainWindow(IAppActions app)
        {
            this.app = app;
            Title = "SamsungCONNECT"; Width = 1180; Height = 760; MinWidth = 820; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font; UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            pageHost.LayoutTransform = pageScale;
            overlayHost.LayoutTransform = overlayScale; overlayHost.Visibility = Visibility.Collapsed;
            toastHost.LayoutTransform = overlayScale; toastHost.IsHitTestVisible = false;
            shell.Children.Add(pageHost); shell.Children.Add(overlayHost); shell.Children.Add(toastHost);
            Content = shell;
            shell.SizeChanged += delegate { Rescale(); };
            SourceInitialized += delegate { ApplyChrome(); };
            PreviewKeyDown += OnKeys;

            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += delegate { Tick(); };
            clock.Start();
            hdmiPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            hdmiPoll.Tick += delegate { PollHdmi(); };
            hdmiPoll.Start();
            PollHdmi(false);
            Build();
        }

        internal bool IsLauncher { get { return launcher; } }

        // ---------- Public surface used by the controller ----------

        internal void ShowConnect()
        {
            launcher = false; ExitFullscreen(); Build();
        }
        internal void ShowLauncher(string label, bool full)
        {
            launcher = true; connectionLabel = label;
            if (full) EnterFullscreen();
            Build();
        }
        internal void Refit() { if (fullscreen) { fullscreen = false; EnterFullscreen(); } }
        internal void SetStatus(string title, string detail, int step, bool isBusy, bool isError)
        {
            statusTitle = title; statusDetail = detail; statusStep = step; statusError = isError;
            bool rebuild = !launcher; busy = isBusy;
            if (rebuild) Build();
        }
        internal void SetBusy(bool value)
        {
            if (busy == value) return;
            busy = value;
            if (!launcher) Build();
        }
        internal void SetLog(string text) { lastLog = text; }
        internal void ThemeChanged() { Build(); }
        internal void BringHome()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = fullscreen ? WindowState.Maximized : WindowState.Normal;
            Activate(); Topmost = true; Topmost = false; Focus();
            FocusDefault();
        }
        internal void LogoUpdated(string id)
        {
            Border host; ServiceInfo s;
            if (iconHosts.TryGetValue(id, out host) && services.TryGetValue(id, out s)) host.Child = IconBox(s, host.Width);
        }

        internal void ShowConfirm(Action<bool> answer)
        {
            bool done = false;
            Action<bool> reply = delegate(bool keep) { if (done) return; done = true; answer(keep); };
            confirmAnswer = reply; confirmLeft = 15;
            ShowOverlay("confirm", delegate
            {
                Palette p = Theme.Current;
                StackPanel body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                TextBlock title = Theme.Text("Can you see this on your TV?", 38, FontWeights.Bold, p.Text);
                title.TextAlignment = TextAlignment.Center; body.Children.Add(title);
                confirmDetail = Theme.Text(ConfirmText(), 20, FontWeights.Normal, p.SubText);
                confirmDetail.TextAlignment = TextAlignment.Center; confirmDetail.Margin = new Thickness(0, 10, 0, 0); confirmDetail.MaxWidth = 640;
                body.Children.Add(confirmDetail);
                confirmSeconds = Theme.Text(confirmLeft.ToString(), 64, FontWeights.Bold, p.Accent);
                confirmSeconds.HorizontalAlignment = HorizontalAlignment.Center; confirmSeconds.VerticalAlignment = VerticalAlignment.Center;
                Border dial = Theme.Inset(confirmSeconds, 80); dial.Width = 160; dial.Height = 160; dial.Margin = new Thickness(0, 30, 0, 34);
                dial.HorizontalAlignment = HorizontalAlignment.Center;
                body.Children.Add(dial);
                StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                Neu keep = Pill(Glyphs.Check, "Keep TV mode", true, delegate { reply(true); });
                keep.Tag = "default"; keep.MinWidth = 300; keep.Margin = new Thickness(0, 0, 28, 0);
                buttons.Children.Add(keep);
                buttons.Children.Add(Pill(Glyphs.Laptop, "Return to laptop", false, delegate { reply(false); }));
                body.Children.Add(buttons);
                return Card(body, 820);
            }, delegate { reply(false); });
        }
        string ConfirmText() { return "Going back to your laptop screen in " + confirmLeft + " seconds unless you keep TV mode. Press Enter to keep it."; }
        internal void SetConfirmSeconds(int seconds)
        {
            if (seconds == confirmLeft) return;
            confirmLeft = seconds;
            if (overlayKind != "confirm") return;
            if (confirmSeconds != null) confirmSeconds.Text = seconds.ToString();
            if (confirmDetail != null) confirmDetail.Text = ConfirmText();
        }
        internal void CloseConfirm()
        {
            confirmAnswer = null;
            if (overlayKind == "confirm") CloseOverlay();
        }

        internal void ShowMessage(string title, string text)
        {
            ShowOverlay("message", delegate
            {
                Palette p = Theme.Current;
                StackPanel body = new StackPanel();
                body.Children.Add(Theme.Text(title, 32, FontWeights.Bold, p.Text));
                TextBlock t = Theme.Text(text, 20, FontWeights.Normal, p.SubText); t.Margin = new Thickness(0, 12, 0, 32);
                body.Children.Add(t);
                Neu ok = Pill(Glyphs.Check, "OK", true, CloseOverlay); ok.Tag = "default"; ok.HorizontalAlignment = HorizontalAlignment.Right;
                body.Children.Add(ok);
                return Card(body, 760);
            }, null);
        }

        internal void Toast(string text)
        {
            Palette p = Theme.Current;
            toastHost.Children.Clear();
            TextBlock t = Theme.Text(text, 19, FontWeights.SemiBold, p.Text); t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.Margin = new Thickness(28, 16, 28, 16); t.MaxWidth = 1000;
            Neu chip = new Neu(t, 28, 8, false) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40) };
            toastHost.Children.Add(chip);
            chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            if (toastTimer != null) toastTimer.Stop();
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            toastTimer.Tick += delegate
            {
                toastTimer.Stop();
                DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
                fade.Completed += delegate { if (toastHost.Children.Contains(chip)) toastHost.Children.Remove(chip); };
                chip.BeginAnimation(OpacityProperty, fade);
            };
            toastTimer.Start();
        }

        // ---------- Window mechanics ----------

        void EnterFullscreen()
        {
            fullscreen = true;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            Left = 0; Top = 0; // The primary screen (the TV in TV mode) always starts at 0,0.
            WindowState = WindowState.Maximized;
        }
        void ExitFullscreen()
        {
            if (!fullscreen) return;
            fullscreen = false;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
            Width = 1180; Height = 760;
            Rect area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2); Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }
        void Rescale()
        {
            double w = shell.ActualWidth, h = shell.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double rw = launcher ? 1920 : 1280, rh = launcher ? 1080 : 800;
            double s = Math.Max(0.4, Math.Min(w / rw, h / rh));
            pageScale.ScaleX = s; pageScale.ScaleY = s;
            double o = Math.Max(0.4, Math.Min(w / 1280, h / 800));
            overlayScale.ScaleX = o; overlayScale.ScaleY = o;
        }
        void ApplyChrome()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Palette p = Theme.Current;
            int dark = p.IsDark ? 1 : 0, caption = p.Base.R | (p.Base.G << 8) | (p.Base.B << 16), text = p.Text.R | (p.Text.G << 8) | (p.Text.B << 16);
            try
            {
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);    // Dark title bar
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4); // Title bar colour (Windows 11)
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);    // Title text colour (Windows 11)
            }
            catch { /* Older Windows: standard title bar. */ }
        }

        void Build()
        {
            FrameworkElement focused = Keyboard.FocusedElement as FrameworkElement;
            if (focused != null && focused.Tag is string && IsDescendant(focused, pageHost)) rememberFocus = (string)focused.Tag;
            Background = Theme.Brush(Theme.Current.Base);
            iconHosts = new Dictionary<string, Border>(); services = new Dictionary<string, ServiceInfo>();
            resumeChip = null; clockText = null;
            pageHost.Children.Clear();
            pageHost.Children.Add(launcher ? BuildLauncher() : BuildConnect());
            Tick();
            if (overlayFactory != null) RenderOverlay(); else FocusDefault();
            ApplyChrome(); Rescale();
        }

        void Tick()
        {
            DateTime now = DateTime.Now;
            if (clockText != null)
            {
                clockText.Text = now.ToString("h:mm tt");
                dateText.Text = now.ToString("dddd, d MMMM");
                greetText.Text = now.Hour < 12 ? "Good morning" : now.Hour < 17 ? "Good afternoon" : "Good evening";
            }
            if (resumeChip != null)
            {
                string name = app.ResumeName;
                resumeChip.Visibility = name == null ? Visibility.Collapsed : Visibility.Visible;
                if (name != null) resumeText.Text = "Resume " + name;
            }
        }
        void PollHdmi() { PollHdmi(true); }
        void PollHdmi(bool rebuild)
        {
            if (rebuild && (launcher || busy || !IsVisible || WindowState == WindowState.Minimized)) return;
            string label; bool found = false;
            try { found = app.DetectHdmi(out label); }
            catch { label = null; }
            if (found == hdmiFound && label == hdmiLabel) return;
            hdmiFound = found; hdmiLabel = label;
            if (rebuild && overlayFactory == null) Build();
        }

        // ---------- Connect page (laptop, 1280x800 reference) ----------

        UIElement BuildConnect()
        {
            Palette p = Theme.Current;
            Grid page = new Grid { Margin = new Thickness(56, 34, 56, 28) };
            for (int i = 0; i < 4; i++) page.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel brand = new StackPanel { Orientation = Orientation.Horizontal };
            Neu mark = new Neu(Theme.Glyph(Glyphs.Tv, 30, p.Accent), 36, 7, false) { Width = 72, Height = 72 };
            brand.Children.Add(mark);
            StackPanel names = new StackPanel { Margin = new Thickness(22, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Theme.Text("SamsungCONNECT", 32, FontWeights.Bold, p.Text));
            names.Children.Add(Theme.Text("Your laptop. Your TV. One big screen.", 18, FontWeights.Normal, p.SubText));
            brand.Children.Add(names);
            header.Children.Add(brand);
            StackPanel tools = Row(); Grid.SetColumn(tools, 1); tools.VerticalAlignment = VerticalAlignment.Center;
            tools.Children.Add(IconButton(Glyphs.ForMode(Theme.Mode), "Theme: " + Theme.Mode, "theme", CycleTheme));
            tools.Children.Add(IconButton(Glyphs.Settings, "Settings", "settings", ShowSettings));
            header.Children.Add(tools);
            page.Children.Add(header);

            StackPanel heading = new StackPanel { Margin = new Thickness(0, 34, 0, 26) };
            heading.Children.Add(Theme.Text(busy ? "Connecting your TV" : "How is your TV connected?", 30, FontWeights.SemiBold, p.Text));
            if (!busy)
            {
                StackPanel line = Row(); line.Margin = new Thickness(0, 8, 0, 0);
                if (statusError) line.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Theme.Brush(p.Danger), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                TextBlock status = Theme.Text(statusError ? statusTitle + ": " + statusDetail : statusDetail, 18, FontWeights.Normal, statusError ? p.Danger : p.SubText);
                status.MaxWidth = 1100;
                line.Children.Add(status);
                heading.Children.Add(line);
            }
            Grid.SetRow(heading, 1); page.Children.Add(heading);

            UIElement middle;
            if (busy) middle = StatusCard();
            else
            {
                Grid cards = new Grid();
                cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); cards.ColumnDefinitions.Add(new ColumnDefinition());
                Neu wireless = MethodCard("wireless", Theme.Glyph(Glyphs.Cast, 50, p.Accent), "Wireless", "Screen Mirroring (Miracast). Turn on the TV and keep Wi-Fi on.",
                    "TV name: " + app.TvName, p.Accent, "1", app.ConnectWireless);
                Neu hdmi = MethodCard("hdmi", Theme.HdmiIcon(52, p.Accent), "HDMI cable", "Plug the cable into the laptop and pick that HDMI input on the TV.",
                    hdmiFound ? "Found: " + hdmiLabel : hdmiLabel ?? "Not detected yet", hdmiFound ? p.Ok : p.SubText, "2", app.ConnectHdmi);
                Grid.SetColumn(hdmi, 2);
                cards.Children.Add(wireless); cards.Children.Add(hdmi);
                pageDefault = hdmiFound ? hdmi : wireless;
                middle = cards;
            }
            Grid.SetRow(middle, 2); page.Children.Add(middle);

            Grid bottom = new Grid { Margin = new Thickness(0, 28, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bottom.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel actions = Row();
            Neu here = Pill(Glyphs.Home, "Open launcher here", false, app.OpenLauncherHere); here.Tag = "here"; here.Margin = new Thickness(0, 0, 22, 0);
            actions.Children.Add(here);
            Neu exit = Pill(Glyphs.Power, "Exit", false, app.Exit); exit.Tag = "exit";
            actions.Children.Add(exit);
            bottom.Children.Add(actions);
            StackPanel hints = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(hints, 1);
            hints.Children.Add(Hint("Ctrl+Alt+T  switch screens    Ctrl+Alt+H  launcher"));
            if (!string.IsNullOrEmpty(lastLog)) { TextBlock l = Hint(lastLog); l.TextTrimming = TextTrimming.CharacterEllipsis; l.TextWrapping = TextWrapping.NoWrap; l.MaxWidth = 640; l.Margin = new Thickness(0, 6, 0, 0); hints.Children.Add(l); }
            bottom.Children.Add(hints);
            Grid.SetRow(bottom, 3); page.Children.Add(bottom);
            return page;
        }

        Neu MethodCard(string key, FrameworkElement icon, string title, string detail, string pill, Color pillColor, string number, Action act)
        {
            Palette p = Theme.Current;
            StackPanel body = new StackPanel { Margin = new Thickness(36, 32, 36, 30) };
            Grid top = new Grid();
            Border well = Theme.Inset(icon, 52); well.Width = 104; well.Height = 104; well.HorizontalAlignment = HorizontalAlignment.Left;
            top.Children.Add(well);
            TextBlock n = Theme.Text(number, 18, FontWeights.Bold, p.SubText); n.HorizontalAlignment = HorizontalAlignment.Center; n.VerticalAlignment = VerticalAlignment.Center;
            Border badge = Theme.Inset(n, 20); badge.Width = 40; badge.Height = 40; badge.HorizontalAlignment = HorizontalAlignment.Right; badge.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetName(badge, "Shortcut key " + number);
            top.Children.Add(badge);
            body.Children.Add(top);
            TextBlock t = Theme.Text(title, 30, FontWeights.Bold, p.Text); t.Margin = new Thickness(0, 26, 0, 0); body.Children.Add(t);
            TextBlock d = Theme.Text(detail, 18, FontWeights.Normal, p.SubText); d.Margin = new Thickness(0, 8, 0, 18); body.Children.Add(d);
            StackPanel chip = Row();
            chip.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Theme.Brush(pillColor), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            TextBlock pt = Theme.Text(pill, 17, FontWeights.SemiBold, pillColor); pt.TextWrapping = TextWrapping.NoWrap; pt.TextTrimming = TextTrimming.CharacterEllipsis; pt.MaxWidth = 420;
            chip.Children.Add(pt);
            body.Children.Add(new Border { Child = chip, CornerRadius = new CornerRadius(18), Background = Theme.Brush(pillColor, 0.12), Padding = new Thickness(16, 8, 18, 8), HorizontalAlignment = HorizontalAlignment.Left });
            Neu card = new Neu(body, 38, 12, true, false, 1.03, title + ". " + detail) { Tag = key, VerticalAlignment = VerticalAlignment.Top };
            card.Click += act;
            return card;
        }

        UIElement StatusCard()
        {
            Palette p = Theme.Current;
            Grid body = new Grid { Margin = new Thickness(48, 44, 48, 40) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); body.ColumnDefinitions.Add(new ColumnDefinition());
            Border well = Theme.Inset(Theme.Spinner(84, p.Accent), 64); well.Width = 128; well.Height = 128; well.VerticalAlignment = VerticalAlignment.Top;
            body.Children.Add(well);
            StackPanel text = new StackPanel { Margin = new Thickness(40, 0, 0, 0) }; Grid.SetColumn(text, 1);
            text.Children.Add(Theme.Text("STEP " + Math.Max(1, statusStep) + " OF 3", 16, FontWeights.Bold, p.Accent));
            TextBlock t = Theme.Text(statusTitle, 32, FontWeights.Bold, p.Text); t.Margin = new Thickness(0, 8, 0, 0); text.Children.Add(t);
            TextBlock d = Theme.Text(statusDetail, 19, FontWeights.Normal, p.SubText); d.Margin = new Thickness(0, 10, 0, 22); text.Children.Add(d);
            StackPanel dots = Row(); dots.Margin = new Thickness(0, 0, 0, 30);
            for (int i = 1; i <= 3; i++)
                dots.Children.Add(new Border { Width = 56, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0),
                    Background = i <= Math.Max(1, statusStep) ? Theme.Brush(p.Accent) : Theme.Brush(p.SubText, 0.25) });
            text.Children.Add(dots);
            Neu cancel = Pill(Glyphs.Back, "Cancel, stay on laptop", false, app.Cancel); cancel.HorizontalAlignment = HorizontalAlignment.Left;
            text.Children.Add(cancel);
            pageDefault = cancel;
            body.Children.Add(text);
            return new Neu(body, 38, 12, false) { VerticalAlignment = VerticalAlignment.Top };
        }

        // ---------- Launcher (TV, 1920x1080 reference) ----------

        UIElement BuildLauncher()
        {
            Palette p = Theme.Current;
            Grid page = new Grid();
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition());

            Grid header = new Grid { Margin = new Thickness(76, 44, 76, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel time = new StackPanel();
            greetText = Theme.Text("", 24, FontWeights.SemiBold, p.SubText);
            clockText = Theme.Text("", 72, FontWeights.Bold, p.Text); clockText.Margin = new Thickness(0, -4, 0, -6);
            dateText = Theme.Text("", 22, FontWeights.Normal, p.SubText);
            time.Children.Add(greetText); time.Children.Add(clockText); time.Children.Add(dateText);
            header.Children.Add(time);

            StackPanel right = Row(); right.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(right, 1);
            StackPanel statusRow = Row();
            statusRow.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Theme.Brush(p.Ok), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            statusRow.Children.Add(Theme.Text(connectionLabel, 19, FontWeights.SemiBold, p.Text));
            statusRow.Margin = new Thickness(24, 14, 26, 14);
            right.Children.Add(new Neu(statusRow, 28, 6, false) { Margin = new Thickness(0, 0, 26, 0), VerticalAlignment = VerticalAlignment.Center });
            resumeText = Theme.Text("Resume", 19, FontWeights.SemiBold, p.AccentText);
            StackPanel rc = Row(); rc.Margin = new Thickness(22, 14, 26, 14);
            rc.Children.Add(Theme.Glyph(Glyphs.Play, 18, p.AccentText)); resumeText.Margin = new Thickness(12, 0, 0, 0); rc.Children.Add(resumeText);
            resumeChip = new Neu(rc, 28, 6, true, true, 1.05, "Resume") { Margin = new Thickness(0, 0, 26, 0), Tag = "resume", Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            resumeChip.Click += app.Resume;
            right.Children.Add(resumeChip);
            right.Children.Add(IconButton(Glyphs.Add, "Add a site", "addsite", ShowAddTile));
            right.Children.Add(IconButton(Glyphs.ForMode(Theme.Mode), "Theme: " + Theme.Mode, "theme", CycleTheme));
            right.Children.Add(IconButton(Glyphs.Settings, "Settings", "settings", ShowSettings));
            right.Children.Add(IconButton(Glyphs.Laptop, "Back to laptop screen", "laptop", app.BackToLaptop));
            right.Children.Add(IconButton(Glyphs.Power, "Exit and restore laptop", "exit", app.Exit));
            header.Children.Add(right);
            page.Children.Add(header);

            StackPanel content = new StackPanel { Margin = new Thickness(76, 4, 48, 80) };
            List<string> hidden = app.HiddenTiles;
            List<ServiceInfo> all = Catalog.BuiltIn(); all.AddRange(app.CustomTiles);
            pageDefault = null;
            foreach (string section in Catalog.Sections)
            {
                WrapPanel wrap = new WrapPanel();
                foreach (ServiceInfo s in all)
                {
                    if (s.Section != section || (!s.Custom && hidden.Contains(s.Id))) continue;
                    Neu tile = Tile(s);
                    wrap.Children.Add(tile);
                    if (pageDefault == null) pageDefault = tile;
                }
                if (section == Catalog.MyTiles) wrap.Children.Add(AddTile());
                if (wrap.Children.Count == 0) continue;
                TextBlock h = Theme.Text(section, 26, FontWeights.SemiBold, p.SubText); h.Margin = new Thickness(4, 30, 0, 22);
                content.Children.Add(h); content.Children.Add(wrap);
            }
            ScrollViewer scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.VerticalOnly };
            Grid.SetRow(scroll, 1);
            page.Children.Add(scroll);
            return page;
        }

        Neu Tile(ServiceInfo s)
        {
            Palette p = Theme.Current;
            StackPanel body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Border host = new Border { Width = 112, Height = 112, Child = IconBox(s, 112) };
            iconHosts[s.Id] = host; services[s.Id] = s;
            body.Children.Add(host);
            TextBlock name = Theme.Text(s.Name, 20, FontWeights.SemiBold, p.Text);
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.MaxWidth = 170;
            name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 18, 0, 0);
            body.Children.Add(name);
            Neu tile = new Neu(body, 34, 10, true, false, 1.08, "Open " + s.Name) { Width = 196, Height = 214, Margin = new Thickness(0, 0, 28, 34), Tag = "tile:" + s.Id };
            tile.Click += delegate { app.Launch(s); };
            tile.RightClick += delegate { ShowTileMenu(s); };
            return tile;
        }

        Neu AddTile()
        {
            Palette p = Theme.Current;
            StackPanel body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Border well = Theme.Inset(Theme.Glyph(Glyphs.Add, 40, p.Accent), 30); well.Width = 112; well.Height = 112;
            body.Children.Add(well);
            TextBlock name = Theme.Text("Add a site", 20, FontWeights.SemiBold, p.SubText); name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 18, 0, 0);
            body.Children.Add(name);
            Neu tile = new Neu(body, 34, 10, true, false, 1.08, "Add your own site") { Width = 196, Height = 214, Margin = new Thickness(0, 0, 28, 34), Tag = "tile:add" };
            tile.Click += ShowAddTile;
            return tile;
        }

        UIElement IconBox(ServiceInfo s, double size)
        {
            Color bg = Theme.Hex(s.Bg), fg = Theme.Hex(s.Fg);
            CornerRadius corner = new CornerRadius(size * 0.26);
            Grid g = new Grid { Width = size, Height = size };
            g.Children.Add(new Border { CornerRadius = corner, Background = Theme.Brush(bg) });
            BitmapSource logo = app.Logos == null ? null : app.Logos.Load(s.Id);
            if (logo != null && (logo.PixelWidth >= 96 || IsOpaque(logo)))
            {
                // A full app icon: let it fill the rounded square.
                ImageBrush brush = new ImageBrush(logo) { Stretch = Stretch.UniformToFill }; brush.Freeze();
                Border art = new Border { CornerRadius = corner, Background = brush };
                RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
                g.Children.Add(art);
            }
            else if (logo != null)
            {
                // A small transparent favicon: show it on a white plate so brand-coloured marks stay visible.
                g.Children.Clear();
                g.Children.Add(new Border { CornerRadius = corner, Background = Brushes.White });
                Image img = new Image { Source = logo, Width = size * 0.62, Height = size * 0.62, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                g.Children.Add(img);
            }
            else
            {
                int n = s.Mark.Length;
                double fs = size * (n <= 1 ? 0.56 : n == 2 ? 0.42 : n == 3 ? 0.34 : n == 4 ? 0.27 : 0.21);
                TextBlock t = new TextBlock { Text = s.Mark, FontSize = fs, FontWeight = FontWeights.Black, Foreground = Theme.Brush(fg), FontFamily = Theme.Font,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(t);
            }
            g.Children.Add(new Border { CornerRadius = corner, BorderThickness = new Thickness(1), BorderBrush = Theme.Brush(Colors.White, Theme.Current.IsDark ? 0.08 : 0.35) });
            return g;
        }

        static bool IsOpaque(BitmapSource logo)
        {
            try
            {
                FormatConvertedBitmap px = new FormatConvertedBitmap(logo, PixelFormats.Bgra32, null, 0);
                byte[] a = new byte[4], b = new byte[4];
                px.CopyPixels(new Int32Rect(1, 1, 1, 1), a, 4, 0);
                px.CopyPixels(new Int32Rect(px.PixelWidth - 2, px.PixelHeight - 2, 1, 1), b, 4, 0);
                return a[3] > 240 && b[3] > 240;
            }
            catch { return false; }
        }

        // ---------- Overlays ----------

        void ShowOverlay(string kind, Func<UIElement> factory, Action cancel)
        {
            if (overlayKind == null)
            {
                FrameworkElement focused = Keyboard.FocusedElement as FrameworkElement;
                if (focused != null && focused.Tag is string && IsDescendant(focused, pageHost)) rememberFocus = (string)focused.Tag;
            }
            overlayKind = kind; overlayFactory = factory; overlayCancel = cancel;
            RenderOverlay();
        }
        void RenderOverlay()
        {
            overlayHost.Children.Clear();
            Grid backdrop = new Grid { Background = Theme.Brush(Theme.Current.Base, Theme.Current.IsDark ? 0.86 : 0.8) };
            UIElement card = overlayFactory();
            backdrop.Children.Add(card);
            overlayHost.Children.Add(backdrop);
            overlayHost.Visibility = Visibility.Visible;
            pageHost.IsHitTestVisible = false;
            card.Opacity = 0;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            FocusDefault();
        }
        void CloseOverlay()
        {
            overlayKind = null; overlayFactory = null; overlayCancel = null;
            overlayHost.Children.Clear(); overlayHost.Visibility = Visibility.Collapsed;
            pageHost.IsHitTestVisible = true;
            FocusDefault();
        }
        Neu Card(UIElement body, double width)
        {
            Border pad = new Border { Child = body, Padding = new Thickness(52, 46, 52, 44) };
            return new Neu(pad, 40, 16, false) { Width = width, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        internal void ShowSettingsForTest() { ShowSettings(); }
        void ShowSettings()
        {
            ShowOverlay("settings", delegate
            {
                Palette p = Theme.Current;
                StackPanel body = new StackPanel();
                Grid top = new Grid();
                top.Children.Add(Theme.Text("Settings", 34, FontWeights.Bold, p.Text));
                Neu close = IconButton(Glyphs.Close, "Close settings", "close", CloseOverlay); close.HorizontalAlignment = HorizontalAlignment.Right;
                top.Children.Add(close);
                body.Children.Add(top);

                body.Children.Add(Label("Your TV name (for wireless)"));
                Grid nameRow = new Grid();
                nameRow.ColumnDefinitions.Add(new ColumnDefinition()); nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                TextBox box = Field(app.TvName, 64, "TV name");
                Border field = Theme.Inset(box, 20); field.Margin = new Thickness(0, 0, 22, 0);
                nameRow.Children.Add(field);
                Neu save = Pill(Glyphs.Check, "Save", true, delegate { string err = app.SaveTvName(box.Text); Toast(err ?? "Saved. Wireless will look for \"" + app.TvName + "\"."); });
                Grid.SetColumn(save, 1); nameRow.Children.Add(save);
                body.Children.Add(nameRow);
                body.Children.Add(Note("Use the name shown when you press Win+K. Only a TV with this name is selected."));

                body.Children.Add(Label("Appearance"));
                StackPanel modes = Row();
                foreach (string mode in new[] { "System", "Light", "Dark" })
                {
                    string m = mode;
                    bool chosen = Theme.Mode == m;
                    Color ink = chosen ? p.AccentText : p.Text;
                    StackPanel c = Row(); c.Margin = new Thickness(24, 12, 26, 12);
                    c.Children.Add(Theme.Glyph(chosen ? Glyphs.Check : Glyphs.ForMode(m), 20, ink));
                    TextBlock mt = Theme.Text(m, 19, FontWeights.SemiBold, ink); mt.Margin = new Thickness(12, 0, 0, 0); c.Children.Add(mt);
                    Neu b = new Neu(c, 24, 6, true, chosen, 1.04, m + " theme" + (chosen ? " (selected)" : "")) { Margin = new Thickness(0, 0, 20, 0), Tag = "mode:" + m };
                    b.Click += delegate { SetTheme(m); };
                    modes.Children.Add(b);
                }
                body.Children.Add(modes);

                body.Children.Add(Label("Behaviour"));
                NeuToggle lid = NeuToggle.Create("Keep playing with the lid closed", "While on the TV, closing the lid does nothing. Your normal lid setting comes back when you leave TV mode.", Settings.LidOnTv);
                lid.Changed += delegate(bool on) { try { Settings.LidOnTv = on; } catch { } };
                NeuToggle auto = NeuToggle.Create("Connect automatically when the app opens", "Uses the last way you connected.", Settings.AutoConnect);
                auto.Changed += delegate(bool on) { try { Settings.AutoConnect = on; } catch { } };
                NeuToggle start = NeuToggle.Create("Start with Windows", null, app.Startup);
                start.Changed += delegate(bool on) { string msg = app.SetStartup(on); if (msg != null) Toast(msg); };
                foreach (NeuToggle t in new[] { lid, auto, start }) { t.Margin = new Thickness(0, 0, 0, 16); body.Children.Add(t); }
                body.Children.Add(Note("While this app is open your laptop never sleeps, dims or locks."));

                body.Children.Add(Label("Tiles"));
                WrapPanel tileActions = new WrapPanel();
                Neu refresh = Pill(Glyphs.Auto, "Refresh logos", false, delegate { app.RefreshLogos(); Toast("Downloading logos again."); });
                Neu unhide = Pill(Glyphs.Home, "Show hidden tiles", false, delegate { app.SaveHiddenTiles(new List<string>()); Toast("All tiles are back."); if (launcher) Build(); });
                Neu cast = Pill(Glyphs.Cast, "Open Windows Cast", false, app.OpenCast);
                Neu addSite = Pill(Glyphs.Add, "Add a site", true, ShowAddTile);
                foreach (Neu b in new[] { addSite, refresh, unhide, cast }) { b.Margin = new Thickness(0, 0, 20, 16); tileActions.Children.Add(b); }
                body.Children.Add(tileActions);
                body.Children.Add(Note("Shows open full screen in " + app.BrowserName + " with your usual logins. Logos come from each service's own website."));

                ScrollViewer scroll = new ScrollViewer { Content = new Border { Child = body, Padding = new Thickness(8, 8, 18, 8) }, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    MaxHeight = 640, Focusable = false };
                close.Tag = "default";
                return Card(scroll, 900);
            }, null);
        }

        void ShowAddTile()
        {
            ShowOverlay("add", delegate
            {
                Palette p = Theme.Current;
                StackPanel body = new StackPanel();
                body.Children.Add(Theme.Text("Add a site", 34, FontWeights.Bold, p.Text));
                body.Children.Add(Label("Name"));
                TextBox name = Field("", 40, "Tile name"); name.Tag = "default";
                body.Children.Add(Theme.Inset(name, 20));
                body.Children.Add(Label("Web address"));
                TextBox url = Field("", 2048, "Web address");
                body.Children.Add(Theme.Inset(url, 20));
                TextBlock error = Theme.Text("", 17, FontWeights.SemiBold, p.Danger); error.Margin = new Thickness(0, 12, 0, 0);
                body.Children.Add(error);
                StackPanel buttons = Row(); buttons.HorizontalAlignment = HorizontalAlignment.Right; buttons.Margin = new Thickness(0, 22, 0, 0);
                Neu add = Pill(Glyphs.Add, "Add tile", true, delegate
                {
                    Uri uri = Catalog.ParseUserUrl(url.Text);
                    if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Trim().Length > 40) { error.Text = "Give the tile a name (up to 40 letters)."; return; }
                    if (uri == null) { error.Text = "Enter a web address, for example example.com"; return; }
                    ServiceInfo s = Catalog.MakeCustom(name.Text, uri);
                    List<ServiceInfo> list = app.CustomTiles;
                    list.RemoveAll(delegate(ServiceInfo x) { return x.Id == s.Id; });
                    list.Add(s);
                    app.SaveCustomTiles(list);
                    if (app.Logos != null) app.Logos.Fetch(new[] { s }, false);
                    rememberFocus = "tile:" + s.Id;
                    CloseOverlay(); Build(); Toast("Added " + s.Name + ".");
                });
                add.Margin = new Thickness(0, 0, 20, 0);
                buttons.Children.Add(add);
                buttons.Children.Add(Pill(Glyphs.Close, "Cancel", false, CloseOverlay));
                body.Children.Add(buttons);
                return Card(body, 760);
            }, null);
        }

        void ShowTileMenu(ServiceInfo s)
        {
            ShowOverlay("tile", delegate
            {
                Palette p = Theme.Current;
                StackPanel body = new StackPanel();
                StackPanel head = Row();
                head.Children.Add(IconBox(s, 72));
                StackPanel names = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(Theme.Text(s.Name, 30, FontWeights.Bold, p.Text));
                TextBlock u = Theme.Text(s.Url, 16, FontWeights.Normal, p.SubText); u.TextWrapping = TextWrapping.NoWrap; u.TextTrimming = TextTrimming.CharacterEllipsis; u.MaxWidth = 480;
                names.Children.Add(u);
                head.Children.Add(names);
                body.Children.Add(head);
                StackPanel buttons = Row(); buttons.Margin = new Thickness(0, 34, 0, 0);
                Neu open = Pill(Glyphs.Play, "Open", true, delegate { CloseOverlay(); app.Launch(s); }); open.Tag = "default"; open.Margin = new Thickness(0, 0, 20, 0);
                buttons.Children.Add(open);
                Neu remove = Pill(Glyphs.Close, s.Custom ? "Remove tile" : "Hide tile", false, delegate
                {
                    if (s.Custom)
                    {
                        List<ServiceInfo> list = app.CustomTiles;
                        list.RemoveAll(delegate(ServiceInfo x) { return x.Id == s.Id; });
                        app.SaveCustomTiles(list);
                    }
                    else
                    {
                        List<string> hidden = app.HiddenTiles;
                        if (!hidden.Contains(s.Id)) hidden.Add(s.Id);
                        app.SaveHiddenTiles(hidden);
                    }
                    rememberFocus = null; CloseOverlay(); Build();
                    Toast(s.Custom ? "Removed " + s.Name + "." : "Hidden. Bring it back in Settings > Show hidden tiles.");
                });
                remove.Margin = new Thickness(0, 0, 20, 0);
                buttons.Children.Add(remove);
                buttons.Children.Add(Pill(Glyphs.Back, "Cancel", false, CloseOverlay));
                body.Children.Add(buttons);
                return Card(body, 760);
            }, null);
        }

        // ---------- Small builders ----------

        static StackPanel Row() { return new StackPanel { Orientation = Orientation.Horizontal }; }
        static TextBlock Hint(string text) { TextBlock t = Theme.Text(text, 15, FontWeights.Normal, Theme.Current.SubText); t.HorizontalAlignment = HorizontalAlignment.Right; return t; }
        static TextBlock Label(string text) { TextBlock t = Theme.Text(text.ToUpperInvariant(), 15, FontWeights.Bold, Theme.Current.SubText); t.Margin = new Thickness(4, 30, 0, 14); return t; }
        static TextBlock Note(string text) { TextBlock t = Theme.Text(text, 16, FontWeights.Normal, Theme.Current.SubText); t.Margin = new Thickness(4, 12, 0, 0); return t; }

        static TextBox Field(string text, int max, string name)
        {
            Palette p = Theme.Current;
            TextBox box = new TextBox { Text = text, MaxLength = max, FontSize = 21, FontFamily = Theme.Font, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Theme.Brush(p.Text), CaretBrush = Theme.Brush(p.Accent), SelectionBrush = Theme.Brush(p.Accent), Padding = new Thickness(18, 12, 18, 12),
                VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, name);
            return box;
        }

        Neu IconButton(string glyph, string name, string key, Action act)
        {
            Neu b = new Neu(Theme.Glyph(glyph, 22, Theme.Current.Text), 31, 6, true, false, 1.08, name) { Width = 62, Height = 62, Margin = new Thickness(0, 0, 0, 0), Tag = key };
            b.Margin = new Thickness(18, 0, 0, 0);
            b.ToolTip = name;
            b.Click += act;
            return b;
        }

        Neu Pill(string glyph, string text, bool primary, Action act)
        {
            Palette p = Theme.Current;
            Color c = primary ? p.AccentText : p.Text;
            StackPanel content = Row(); content.HorizontalAlignment = HorizontalAlignment.Center; content.Margin = new Thickness(26, 15, 30, 15);
            content.Children.Add(Theme.Glyph(glyph, 19, c));
            TextBlock t = Theme.Text(text, 19, FontWeights.SemiBold, c); t.Margin = new Thickness(12, 0, 0, 0); t.TextWrapping = TextWrapping.NoWrap;
            content.Children.Add(t);
            Neu b = new Neu(content, 26, 7, true, primary, 1.05, text);
            b.Click += act;
            return b;
        }

        void CycleTheme() { SetTheme(Theme.NextMode(Theme.Mode)); Toast("Theme: " + Theme.Mode + (Theme.Mode == "System" ? " (follows Windows)" : "")); }
        void SetTheme(string mode)
        {
            app.SetTheme(mode);
            Theme.Apply(mode);
            Build();
        }

        // ---------- Keyboard: TV-style arrow navigation ----------

        void OnKeys(object sender, KeyEventArgs e)
        {
            bool inText = Keyboard.FocusedElement is TextBox;
            if (e.Key == Key.Escape)
            {
                if (overlayFactory != null) { e.Handled = true; Action c = overlayCancel; if (c != null) c(); else CloseOverlay(); }
                else if (busy) { e.Handled = true; app.Cancel(); }
                return;
            }
            if (inText && (e.Key == Key.Left || e.Key == Key.Right)) return;
            if (inText && e.Key == Key.Enter) { e.Handled = true; MoveFocus(Key.Down); return; }
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down) { e.Handled = true; MoveFocus(e.Key); return; }
            if (!launcher && overlayFactory == null && !busy && !inText)
            {
                if (e.Key == Key.D1 || e.Key == Key.NumPad1) { e.Handled = true; app.ConnectWireless(); }
                else if (e.Key == Key.D2 || e.Key == Key.NumPad2) { e.Handled = true; app.ConnectHdmi(); }
            }
        }

        FrameworkElement Scope { get { return overlayFactory != null ? (FrameworkElement)overlayHost : pageHost; } }

        void MoveFocus(Key key)
        {
            FrameworkElement scope = Scope;
            List<FrameworkElement> items = new List<FrameworkElement>();
            Collect(scope, items);
            if (items.Count == 0) return;
            FrameworkElement current = null;
            DependencyObject f = Keyboard.FocusedElement as DependencyObject;
            while (f != null && current == null)
            {
                FrameworkElement fe = f as FrameworkElement;
                if (fe != null && items.Contains(fe)) current = fe;
                f = f is Visual ? VisualTreeHelper.GetParent(f) : null;
            }
            if (current == null) { FocusItem(items[0]); return; }
            Point c = Center(current, scope);
            bool horizontal = key == Key.Left || key == Key.Right;
            FrameworkElement best = null; double bestScore = double.MaxValue;
            foreach (FrameworkElement item in items)
            {
                if (item == current) continue;
                Point q = Center(item, scope);
                double dx = q.X - c.X, dy = q.Y - c.Y;
                double along = key == Key.Right ? dx : key == Key.Left ? -dx : key == Key.Down ? dy : -dy;
                double across = horizontal ? Math.Abs(dy) : Math.Abs(dx);
                if (along < 10) continue;
                double score = along + across * (horizontal ? 4 : 1.6);
                if (score < bestScore) { bestScore = score; best = item; }
            }
            if (best != null) FocusItem(best);
        }
        static Point Center(FrameworkElement e, FrameworkElement scope)
        {
            try { return e.TransformToAncestor(scope).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2)); }
            catch { return new Point(double.MaxValue / 4, double.MaxValue / 4); }
        }
        static void Collect(DependencyObject node, List<FrameworkElement> items)
        {
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                Neu neu = child as Neu;
                if (neu != null && neu.Interactive)
                {
                    if (neu.IsVisible && neu.IsEnabled) items.Add(neu);
                    continue;
                }
                TextBox box = child as TextBox;
                if (box != null) { if (box.IsVisible) items.Add(box); continue; }
                Collect(child, items);
            }
        }
        static void FocusItem(FrameworkElement item)
        {
            item.Focus();
            item.BringIntoView(new Rect(-40, -70, item.ActualWidth + 80, item.ActualHeight + 120));
        }
        static bool IsDescendant(DependencyObject node, DependencyObject ancestor)
        {
            while (node != null) { if (node == ancestor) return true; node = node is Visual ? VisualTreeHelper.GetParent(node) : null; }
            return false;
        }
        static FrameworkElement FindTag(DependencyObject node, string tag)
        {
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                FrameworkElement fe = child as FrameworkElement;
                if (fe != null && tag.Equals(fe.Tag as string) && fe.Focusable) return fe;
                FrameworkElement found = FindTag(child, tag);
                if (found != null) return found;
            }
            return null;
        }
        void FocusDefault()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
            {
                if (!IsVisible) return;
                FrameworkElement target;
                if (overlayFactory != null)
                {
                    target = FindTag(overlayHost, "default");
                    if (target == null) { List<FrameworkElement> items = new List<FrameworkElement>(); Collect(overlayHost, items); target = items.Count > 0 ? items[0] : null; }
                }
                else
                {
                    target = rememberFocus == null ? null : FindTag(pageHost, rememberFocus);
                    if (target == null) target = pageDefault;
                }
                if (target != null) FocusItem(target);
            });
        }
    }
}
