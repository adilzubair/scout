using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

namespace Scout.App;

internal enum WindowSurface { Transient, Document }

internal static class WindowVisuals
{
    private static readonly List<(WeakReference<Window> Window, WindowSurface Surface)> windows = [];
    private static readonly Dictionary<string, SolidColorBrush> codeBrushes = [];
    private static bool watchingTheme;
    private static bool? darkPalette, contrastPalette;
    private static bool? testDark, testContrast;
    private static bool? testMotion;
    private static bool? testTransparency;
    internal static bool MotionEnabled => testMotion ?? SystemParameters.ClientAreaAnimation;
    internal static Brush Brush(string key)
    {
        if (!codeBrushes.TryGetValue(key, out var brush))
            codeBrushes[key] = brush = ((SolidColorBrush)Application.Current.FindResource(key)).CloneCurrentValue();
        return brush;
    }
    internal static Style Style(string key) => (Style)Application.Current.FindResource(key);

    internal static void Configure(Window window, WindowSurface surface = WindowSurface.Transient)
    {
        windows.Add((new WeakReference<Window>(window), surface));
        if (!watchingTheme)
        {
            watchingTheme = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshTheme));
        }
        RefreshTheme();
        // Code-created Window subclasses do not pick up the implicit Window style.
        window.Style = (Style)Application.Current.FindResource(typeof(Window));
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0, CornerRadius = new CornerRadius(window is MainWindow ? 18 : 12), GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 5), UseAeroCaptionButtons = false
        });
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var rounded = 2;
            _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
            var border = unchecked((int)0xFFFFFFFE); // Let the fine inner rim define the edge.
            _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            // Keep the compositor's native shadow without a layered/software-rendered window.
            var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
            HwndSource.FromHwnd(handle)?.AddHook((nint _, int message, nint _, nint _, ref bool _) =>
            {
                if (message is 0x001A or 0x031A) window.Dispatcher.BeginInvoke(new Action(RefreshTheme));
                return 0;
            });
            ApplyMaterial(window, surface);
            var area = WorkArea(window);
            window.MaxWidth = Math.Max(1, area.Width - 24);
            window.MaxHeight = Math.Max(1, area.Height - 24);
            window.MinWidth = Math.Min(window.MinWidth, window.MaxWidth);
            window.MinHeight = Math.Min(window.MinHeight, window.MaxHeight);
        };
        window.Loaded += (_, _) => { RefreshTheme(); KeepOnScreen(window); Reveal(window); };
        window.SizeChanged += (_, _) => { if (window.IsLoaded) KeepOnScreen(window); };
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible && window.IsLoaded) { RefreshTheme(); KeepOnScreen(window); Reveal(window); } };
    }

    internal static void RefreshTheme()
    {
        if (Application.Current == null) return;
        var contrast = testContrast ?? SystemParameters.HighContrast;
        var dark = testDark ?? (!contrast && Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0);
        if (darkPalette != dark || contrastPalette != contrast)
        {
            darkPalette = dark; contrastPalette = contrast;
            var colors = contrast ? new Dictionary<string, Color>
            {
                ["Ink"] = SystemColors.WindowTextColor, ["Muted"] = SystemColors.WindowTextColor,
                ["Accent"] = SystemColors.HighlightColor, ["OnAccent"] = SystemColors.HighlightTextColor,
                ["Canvas"] = SystemColors.WindowColor, ["Glass"] = SystemColors.WindowColor,
                ["DocumentGlass"] = SystemColors.WindowColor, ["Rim"] = SystemColors.ActiveBorderColor,
                ["Divider"] = SystemColors.ActiveBorderColor, ["PageSurface"] = SystemColors.WindowColor,
                ["Surface"] = SystemColors.WindowColor, ["Line"] = SystemColors.ActiveBorderColor,
                ["Hover"] = SystemColors.ControlColor, ["Selection"] = SystemColors.ControlColor,
                ["SelectionLine"] = SystemColors.HighlightColor, ["KeycapFill"] = SystemColors.WindowColor,
                ["CheckSurface"] = SystemColors.WindowColor, ["CheckBorder"] = SystemColors.WindowTextColor,
                ["SwitchTrack"] = SystemColors.ActiveBorderColor, ["ScrollThumb"] = SystemColors.WindowTextColor,
                ["ScrollThumbHover"] = SystemColors.HighlightColor, ["Success"] = SystemColors.WindowTextColor,
                ["Error"] = SystemColors.WindowTextColor, ["IconSurface"] = SystemColors.WindowColor,
                ["IconGreenSurface"] = SystemColors.WindowColor, ["IconInk"] = SystemColors.WindowTextColor,
                ["IconGreenInk"] = SystemColors.WindowTextColor, ["TextSelection"] = SystemColors.HighlightColor,
                ["SwitchKnob"] = SystemColors.WindowTextColor,
                ["BrandAmber"] = SystemColors.WindowTextColor, ["Tertiary"] = SystemColors.WindowTextColor,
                ["PreviewSurface"] = SystemColors.WindowColor
            } : Palette(dark);
            foreach (var (key, color) in colors)
                Application.Current.Resources[key] = new SolidColorBrush(color);
            foreach (var (key, brush) in codeBrushes) brush.Color = colors[key];
        }
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var (reference, surface) = windows[i];
            if (reference.TryGetTarget(out var window)) ApplyMaterial(window, surface);
            else windows.RemoveAt(i);
        }
    }

    internal static void SetTestTheme(bool? dark, bool? contrast = null)
    {
        testDark = dark; testContrast = contrast; RefreshTheme();
    }

    internal static void SetTestMotion(bool? enabled) => testMotion = enabled;

    internal static void SetTestTransparency(bool? enabled)
    {
        testTransparency = enabled; RefreshTheme();
    }

    private static Dictionary<string, Color> Palette(bool dark)
    {
        var colors = new Dictionary<string, (string Light, string Dark)>
        {
            ["BrandAmber"] = ("#B8660B", "#F2A22A"),
            ["Tertiary"] = ("#85858C", "#94949E"),
            ["PreviewSurface"] = ("#F3F3F4", "#262629"),
            ["Ink"] = ("#1D1D1F", "#F2F2F7"),
            ["Muted"] = ("#6E6E73", "#A1A1A6"),
            ["Accent"] = ("#0A6FD8", "#70B5FF"),
            ["OnAccent"] = ("#FFFFFF", "#132234"),
            ["Canvas"] = ("#FBFBFB", "#1F1F21"),
            ["Glass"] = ("#F2FBFBFB", "#F21F1F21"),
            ["DocumentGlass"] = ("#EBFBFBFB", "#E61F1F21"),
            ["Rim"] = ("#D8D8DC", "#4A4A4E"),
            ["Divider"] = ("#E4E4E7", "#38383C"),
            ["PageSurface"] = ("#F3F3F4", "#27272A"),
            ["Surface"] = ("#FFFFFF", "#2C2C2E"),
            ["Line"] = ("#DCDCE0", "#454549"),
            ["Hover"] = ("#F0F0F2", "#303034"),
            ["Selection"] = ("#CEE5FC", "#233F60"),
            ["SelectionLine"] = ("#86B9ED", "#568BC0"),
            ["Success"] = ("#356652", "#A2C78E"),
            ["Error"] = ("#A8303A", "#FF9B9B"),
            ["KeycapFill"] = ("#F0F0F2", "#303034"),
            ["CheckSurface"] = ("#FFFFFF", "#2C2C2E"),
            ["CheckBorder"] = ("#AEAEB4", "#787880"),
            ["SwitchTrack"] = ("#C8C8CE", "#53535C"),
            ["ScrollThumb"] = ("#B8B8BF", "#62626A"),
            ["ScrollThumbHover"] = ("#92929B", "#8E8E97"),
            ["IconSurface"] = ("#ECECEE", "#303034"),
            ["IconGreenSurface"] = ("#E8F0E9", "#2A3930"),
            ["IconInk"] = ("#6E6E73", "#B4B4BD"),
            ["IconGreenInk"] = ("#356652", "#A2C78E"),
            ["TextSelection"] = ("#C9E3FC", "#385E85"),
            ["SwitchKnob"] = ("#FFFFFF", "#F2F2F7")
        };
        return colors.ToDictionary(pair => pair.Key,
            pair => (Color)ColorConverter.ConvertFromString(dark ? pair.Value.Dark : pair.Value.Light)!);
    }

    private static void ApplyMaterial(Window window, WindowSurface surface)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var dark = darkPalette == true ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        var transparent = testTransparency ?? (Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is not 0);
        var backdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && contrastPalette != true && transparent;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            var kind = backdrop ? surface == WindowSurface.Document ? 2 : 3 : 1;
            backdrop = DwmSetWindowAttribute(handle, 38, ref kind, sizeof(int)) == 0 && backdrop;
        }
        WindowChrome.GetWindowChrome(window).GlassFrameThickness = backdrop ? new Thickness(-1) : new Thickness(0);
        window.Background = backdrop ? Brushes.Transparent : Brush("Canvas");
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = backdrop ? Colors.Transparent : ((SolidColorBrush)Brush("Canvas")).Color;
        if (window.Content is Border frame)
            frame.Background = Brush(backdrop ? surface == WindowSurface.Document ? "DocumentGlass" : "Glass" : "Canvas");
    }

    internal static Rect WorkArea(Window window)
    {
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(transform.Transform(new Point(area.Left, area.Top)), transform.Transform(new Point(area.Right, area.Bottom)));
    }

    private static void KeepOnScreen(Window window)
    {
        var area = WorkArea(window);
        window.Left = Math.Clamp(window.Left, area.Left + 12, Math.Max(area.Left + 12, area.Right - window.ActualWidth - 12));
        window.Top = Math.Clamp(window.Top, area.Top + 12, Math.Max(area.Top + 12, area.Bottom - window.ActualHeight - 12));
    }

    internal static void PlaceLauncher(Window window)
    {
        var area = WorkArea(window);
        // Place the field near the upper third, with space for six results below it.
        var centeredTop = area.Top + area.Height * .35 - window.ActualHeight / 2;
        var expandedTop = area.Bottom - Math.Min(window.MaxHeight, 610) - 12;
        window.Top = Math.Max(area.Top + 12, Math.Min(centeredTop, expandedTop));
    }

    private static void Reveal(Window window)
    {
        if (!MotionEnabled || window.Content is not FrameworkElement content) return;
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.78, 1, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
        var transform = new TranslateTransform();
        content.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    internal static Border Frame(UIElement content) => new()
    {
        Background = Brush("Canvas"), BorderBrush = Brush("Rim"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = content
    };

    internal static Grid Header(Window window, string title, string? subtitle = null)
    {
        var header = new Grid { Margin = new Thickness(24, 19, 18, 19), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle != null) text.Children.Add(new TextBlock { Text = subtitle, Foreground = Brush("Muted"), FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(text);
        var close = new Button
        {
            Content = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12 },
            Style = Style("QuietButton"), Padding = new Thickness(9), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close (Escape)"
        };
        AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => window.Close(); Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && !close.IsMouseOver) window.DragMove();
        };
        // Let controls consume Escape (for example, to dismiss a dropdown) first.
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        return header;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
