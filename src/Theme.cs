using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SamsungCONNECT
{
    sealed class Palette
    {
        internal bool IsDark;
        internal Color Base, LightShadow, DarkShadow, Text, SubText, Accent, AccentText, Ok, Danger;
        internal double LightOpacity, DarkOpacity;
    }

    static class Theme
    {
        internal static readonly Palette Light = new Palette
        {
            IsDark = false, Base = Hex("#E4E9F0"), LightShadow = Hex("#FFFFFF"), DarkShadow = Hex("#A3B1C6"),
            Text = Hex("#2B3445"), SubText = Hex("#6A778C"), Accent = Hex("#5468FF"), AccentText = Colors.White,
            Ok = Hex("#17A673"), Danger = Hex("#E0475B"), LightOpacity = 0.95, DarkOpacity = 0.75
        };
        internal static readonly Palette Dark = new Palette
        {
            IsDark = true, Base = Hex("#262A33"), LightShadow = Hex("#3A404D"), DarkShadow = Hex("#0F1115"),
            Text = Hex("#E8ECF3"), SubText = Hex("#97A2B6"), Accent = Hex("#7D8BFF"), AccentText = Hex("#10131A"),
            Ok = Hex("#3DD598"), Danger = Hex("#FF6B7D"), LightOpacity = 0.55, DarkOpacity = 0.9
        };
        internal static Palette Current = Light;
        internal static string Mode = "System";

        internal static void Apply(string mode)
        {
            Mode = mode == "Light" || mode == "Dark" ? mode : "System";
            Current = Mode == "Dark" || (Mode == "System" && SystemIsDark()) ? Dark : Light;
        }
        internal static bool SystemIsDark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }
        internal static string NextMode(string mode) { return mode == "System" ? "Light" : mode == "Light" ? "Dark" : "System"; }

        internal static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        internal static SolidColorBrush Brush(Color c) { SolidColorBrush b = new SolidColorBrush(c); b.Freeze(); return b; }
        internal static SolidColorBrush Brush(Color c, double alpha) { return Brush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B)); }
        internal static Color Shade(Color c, double amount) // amount > 0 lightens, < 0 darkens
        {
            Func<byte, byte> f = delegate(byte v)
            {
                double r = amount >= 0 ? v + (255 - v) * amount : v * (1 + amount);
                return (byte)Math.Max(0, Math.Min(255, Math.Round(r)));
            };
            return Color.FromRgb(f(c.R), f(c.G), f(c.B));
        }
        // Gentle convex surface (raised) or concave (pressed / inset).
        internal static Brush Surface(bool inset)
        {
            Palette p = Current;
            Color a = Shade(p.Base, p.IsDark ? 0.035 : 0.25), b = Shade(p.Base, p.IsDark ? -0.12 : -0.05);
            LinearGradientBrush brush = new LinearGradientBrush(inset ? b : a, inset ? a : b, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }
        internal static Brush AccentSurface()
        {
            Color a = Current.Accent;
            LinearGradientBrush brush = new LinearGradientBrush(Shade(a, 0.14), Shade(a, -0.1), new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        internal const string FontName = "Segoe UI Variable Display, Segoe UI";
        internal static readonly FontFamily Font = new FontFamily(FontName);
        internal static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        internal static TextBlock Text(string text, double size, FontWeight weight, Color color)
        {
            return new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = Brush(color), FontFamily = Font, TextWrapping = TextWrapping.Wrap };
        }
        internal static TextBlock Glyph(string code, double size, Color color)
        {
            return new TextBlock { Text = code, FontSize = size, Foreground = Brush(color), FontFamily = Icons,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        }
        // Inset well: used for text fields, toggle tracks, countdown dial.
        internal static Border Inset(UIElement child, double radius)
        {
            Palette p = Current;
            return new Border
            {
                CornerRadius = new CornerRadius(radius), Background = Surface(true), Child = child,
                BorderThickness = new Thickness(1.5),
                BorderBrush = new LinearGradientBrush(Color.FromArgb((byte)(p.IsDark ? 140 : 90), p.DarkShadow.R, p.DarkShadow.G, p.DarkShadow.B),
                    Color.FromArgb((byte)(p.IsDark ? 50 : 200), p.LightShadow.R, p.LightShadow.G, p.LightShadow.B), new Point(0, 0), new Point(1, 1))
            };
        }
        // Rotating arc for "working on it".
        internal static FrameworkElement Spinner(double size, Color color)
        {
            double r = size / 2 - 4;
            PathFigure fig = new PathFigure { StartPoint = new Point(size / 2, 4) };
            fig.Segments.Add(new ArcSegment(new Point(size / 2 + r, size / 2), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            PathGeometry geo = new PathGeometry(); geo.Figures.Add(fig);
            RotateTransform spin = new RotateTransform(0, size / 2, size / 2);
            Path arc = new Path { Data = geo, Stroke = Brush(color), StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = size, Height = size, RenderTransform = spin };
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            Grid g = new Grid { Width = size, Height = size };
            g.Children.Add(new Ellipse { Stroke = Brush(Current.SubText, 0.2), StrokeThickness = 6, Margin = new Thickness(4) });
            g.Children.Add(arc);
            return g;
        }
        // HDMI plug outline (no system glyph exists for it).
        internal static FrameworkElement HdmiIcon(double size, Color color)
        {
            Geometry plug = Geometry.Parse("M 4,8 L 44,8 L 44,20 L 38,28 L 10,28 L 4,20 Z M 12,15 L 36,15 M 18,28 L 18,40 M 30,28 L 30,40 M 14,40 L 34,40");
            return new Path { Data = plug, Stroke = Brush(color), StrokeThickness = 3.2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.Uniform, Width = size, Height = size };
        }
    }

    static class Glyphs
    {
        internal const string Tv = "", Laptop = "", Cast = "", Settings = "", Add = "", Back = "",
            Home = "", Play = "", Power = "", Sun = "", Moon = "", Auto = "", Check = "",
            Close = "", Wifi = "", Music = "";
        internal static string ForMode(string mode) { return mode == "Light" ? Sun : mode == "Dark" ? Moon : Auto; }
    }

    // A soft "extruded" surface: one light shadow top-left, one dark shadow bottom-right.
    // Interactive surfaces take keyboard focus, grow and get an accent ring when focused or hovered, and sink when pressed.
    class Neu : Grid
    {
        internal event Action Click, RightClick;
        readonly Border darkLayer, lightLayer, face;
        readonly DropShadowEffect darkShadow, lightShadow;
        readonly ScaleTransform scale = new ScaleTransform(1, 1);
        readonly bool interactive;
        bool primary, selected, hot, pressed;
        readonly double depth, hotScale;
        internal Border Face { get { return face; } }
        internal bool Interactive { get { return interactive; } }

        internal Neu(UIElement content, double radius, double depth, bool interactive, bool primary = false, double hotScale = 1.05, string name = null)
        {
            Palette p = Theme.Current;
            this.depth = depth; this.interactive = interactive; this.primary = primary; this.hotScale = hotScale;
            CornerRadius corner = new CornerRadius(radius);
            darkShadow = new DropShadowEffect { Color = p.DarkShadow, Direction = 315, ShadowDepth = depth, BlurRadius = depth * 2.6, Opacity = p.DarkOpacity };
            lightShadow = new DropShadowEffect { Color = p.LightShadow, Direction = 135, ShadowDepth = depth, BlurRadius = depth * 2.6, Opacity = p.LightOpacity };
            darkLayer = new Border { CornerRadius = corner, Background = Theme.Brush(p.Base), Effect = darkShadow };
            lightLayer = new Border { CornerRadius = corner, Background = Theme.Brush(p.Base), Effect = lightShadow };
            face = new Border { CornerRadius = corner, Child = content, BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent };
            Children.Add(darkLayer); Children.Add(lightLayer); Children.Add(face);
            RenderTransformOrigin = new Point(0.5, 0.5); RenderTransform = scale;
            if (interactive)
            {
                Focusable = true; FocusVisualStyle = null; Cursor = Cursors.Hand;
                KeyboardNavigation.SetIsTabStop(this, true);
            }
            if (name != null) AutomationProperties.SetName(this, name);
            Paint();
        }

        internal bool Primary { set { primary = value; Paint(); } }
        internal bool Selected { get { return selected; } set { selected = value; Paint(); } }

        void Paint()
        {
            Palette p = Theme.Current;
            bool sunk = pressed || selected;
            face.Background = primary && !pressed ? Theme.AccentSurface() : Theme.Surface(sunk);
            face.BorderBrush = interactive && hot ? Theme.Brush(primary ? p.Text : p.Accent, primary ? 0.55 : 0.9) : Brushes.Transparent;
            double d = sunk ? Math.Max(1, depth * 0.15) : hot ? depth * 1.35 : depth;
            Animate(darkShadow, d); Animate(lightShadow, d);
            double s = pressed ? 0.97 : hot ? hotScale : 1.0;
            DoubleAnimation grow = new DoubleAnimation(s, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        static void Animate(DropShadowEffect e, double d)
        {
            e.BeginAnimation(DropShadowEffect.ShadowDepthProperty, new DoubleAnimation(d, TimeSpan.FromMilliseconds(150)));
        }
        void SetHot(bool value) { if (hot != value) { hot = value; Paint(); } }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); if (interactive) SetHot(true); }
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); if (interactive) SetHot(IsMouseOver); }
        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            if (!interactive) return;
            SetHot(true);
            if (!(Keyboard.FocusedElement is TextBox)) Focus(); // Mouse and arrow keys share one "current" tile, like a TV.
        }
        protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (interactive) SetHot(IsKeyboardFocused); }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!interactive) return;
            Focus(); pressed = true; CaptureMouse(); Paint(); e.Handled = true;
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!interactive || !pressed) return;
            pressed = false; ReleaseMouseCapture(); Paint(); e.Handled = true;
            if (IsMouseOver) Fire();
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (pressed) { pressed = false; Paint(); } }
        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (interactive && RightClick != null) { e.Handled = true; RightClick(); }
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!interactive) return;
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                e.Handled = true;
                pressed = true; Paint();
                DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                t.Tick += delegate { t.Stop(); pressed = false; Paint(); Fire(); };
                t.Start();
            }
            else if (e.Key == Key.Apps && RightClick != null) { e.Handled = true; RightClick(); }
        }
        void Fire() { if (Click != null) Click(); }
    }

    // A whole settings row that flips a switch when clicked.
    sealed class NeuToggle : Neu
    {
        readonly Border track; readonly Neu knob;
        bool on;
        internal event Action<bool> Changed;
        NeuToggle(Grid content, Border track, Neu knob, bool on, string name)
            : base(content, 24, 6, true, false, 1.02, name)
        {
            this.track = track; this.knob = knob; this.on = on; Render();
            Click += delegate { this.on = !this.on; Render(); if (Changed != null) Changed(this.on); };
        }
        internal static NeuToggle Create(string title, string detail, bool on)
        {
            Palette p = Theme.Current;
            Grid content = new Grid { Margin = new Thickness(26, 18, 22, 18) };
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Theme.Text(title, 21, FontWeights.SemiBold, p.Text));
            if (detail != null) { TextBlock d = Theme.Text(detail, 16, FontWeights.Normal, p.SubText); d.Margin = new Thickness(0, 4, 24, 0); text.Children.Add(d); }
            content.Children.Add(text);
            Neu knob = new Neu(null, 15, 3, false) { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 4, 0) };
            Grid inner = new Grid { Width = 74, Height = 38 };
            Border track = Theme.Inset(inner, 19);
            inner.Children.Add(knob);
            Grid.SetColumn(track, 1); track.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(track);
            return new NeuToggle(content, track, knob, on, title);
        }
        void Render()
        {
            Palette p = Theme.Current;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            track.Background = on ? Theme.AccentSurface() : Theme.Surface(true);
            AutomationProperties.SetItemStatus(this, on ? "On" : "Off");
        }
        internal bool IsOn { get { return on; } }
    }
}
