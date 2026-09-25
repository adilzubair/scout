using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;

namespace Scout.App;

public partial class MainWindow
{
    // Runs only in the isolated --ui-smoke-test flow; no user settings are saved.
    private async Task CaptureUiChecks()
    {
        async Task SearchView(string text, Scope filter, string name, int pixelScale = 1)
        {
            scope = filter; UpdateScopes(); Query.Text = text; RefreshLocal(); await WaitForSearch();
            await UiDiagnostics.Snapshot(this, name, pixelScale);
            if (Query.ActualWidth < 200 || Results.ActualHeight < 100) throw new InvalidOperationException("Launcher content was clipped.");
        }
        await SearchView("", Scope.All, "launcher-empty");
        if (ActualHeight > 90 || ScopesRow.IsVisible || ResultsRegion.IsVisible) throw new InvalidOperationException("Empty search did not open compact.");
        var workArea = WindowVisuals.WorkArea(this);
        if (Top + ActualHeight / 2 >= workArea.Top + workArea.Height * .39)
            throw new InvalidOperationException("Compact search did not open near the upper third.");
        WindowVisuals.SetTestTheme(true);
        await UiDiagnostics.Snapshot(this, "launcher-empty-dark");
        WindowVisuals.SetTestTheme(null);
        RevealSuggestions(); UpdateLayout();
        if (ActualHeight < 300 || !ScopesRow.IsVisible) throw new InvalidOperationException("Down/Tab suggestions did not expand the launcher.");
        if (Top + ActualHeight > workArea.Bottom - 8)
            throw new InvalidOperationException("Expanded search fell below the work area.");
        await UiDiagnostics.Snapshot(this, "launcher-suggestions");
        Query.Text = "pdf"; await WaitForSearch();
        Query.Clear(); await WaitForSearch();
        if (ActualHeight > 90) throw new InvalidOperationException("Clearing search did not collapse the launcher.");
        WindowVisuals.SetTestMotion(false);
        RevealSuggestions(); UpdateLayout();
        if (ActualHeight < 300) throw new InvalidOperationException("Reduced-motion expansion did not complete immediately.");
        Query.Clear(); suggestionsRevealed = false; UpdateExpansion();
        WindowVisuals.SetTestMotion(null);
        await SearchView("pdfs", Scope.All, "launcher-group");
        if (GroupBar.Visibility != Visibility.Visible || GroupBar.ActualHeight < 40) throw new InvalidOperationException("Group controls are not visible.");
        ClearGroup();
        await SearchView("the pdf I just downloaded", Scope.All, "site-recent-download", pixelScale: 3);
        if (Selected?.Title != "Product Roadmap.pdf") throw new InvalidOperationException("Site arrival example selected the wrong file.");
        await SearchView("latest 3 files in downloads", Scope.All, "site-download-group", pixelScale: 3);
        if (Selected?.Kind != ItemKind.Group || group.Count != 3) throw new InvalidOperationException("Site download example did not form a three-file group.");
        ClearGroup();
        await SearchView("15% of 240", Scope.All, "launcher-calculator");
        await SearchView("", Scope.Workspaces, "launcher-workspaces");
        await SearchView("no-such-file", Scope.Files, "launcher-no-results");
        await SearchView("pdf", Scope.All, "launcher-results");
        await UiDiagnostics.CaptureBackdrop(this, "launcher-light-on-white", System.Windows.Media.Colors.White);
        await UiDiagnostics.CaptureBackdrop(this, "launcher-light-on-black", System.Windows.Media.Colors.Black);
        WindowVisuals.SetTestTheme(true);
        await UiDiagnostics.Snapshot(this, "launcher-results-dark");
        await UiDiagnostics.CaptureBackdrop(this, "launcher-dark-on-white", System.Windows.Media.Colors.White);
        await UiDiagnostics.CaptureBackdrop(this, "launcher-dark-on-black", System.Windows.Media.Colors.Black);
        WindowVisuals.SetTestTheme(null);
        WindowVisuals.SetTestTheme(null, true);
        await UiDiagnostics.Snapshot(this, "launcher-results-contrast");
        WindowVisuals.SetTestTheme(null);
        WindowVisuals.SetTestTransparency(false);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !ReferenceEquals(PanelShell.Background, WindowVisuals.Brush("Canvas")))
            throw new InvalidOperationException("Disabled transparency did not use a solid surface.");
        await UiDiagnostics.Snapshot(this, "launcher-results-solid");
        WindowVisuals.SetTestTransparency(null);
        var longTitle = new Candidate("ui:long", ItemKind.File, new string('W', 180) + ".pdf", "C:\\Documents\\Long title.pdf") { Detail = new string('x', 240), Source = "Files" };
        ApplyRows([new SearchHit(longTitle, 1000)]);
        await UiDiagnostics.Snapshot(this, "launcher-long-title");
        if (ResultCount.TranslatePoint(new System.Windows.Point(ResultCount.ActualWidth, 0), this).X > ActualWidth) throw new InvalidOperationException("Long result text overflowed the panel.");
        RefreshLocal(); await WaitForSearch();
        var typing = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++) { Query.Text = i % 2 == 0 ? "pdf" : "roadmap"; await Task.Delay(5); }
        Query.Text = "pdf"; await WaitForSearch(); typing.Stop();
        File.WriteAllText(Path.Combine(App.DataDirectory, "ui-typing-ms.txt"), typing.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        if (typing.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Rapid typing blocked the launcher.");

        var groupWindow = new GroupWindow(current!.Hits.Select(hit => hit.Candidate).ToArray()) { Owner = this };
        try
        {
            groupWindow.Show(); await UiDiagnostics.Snapshot(groupWindow, "review-group");
            WindowVisuals.SetTestTheme(true); await UiDiagnostics.Snapshot(groupWindow, "review-group-dark");
            WindowVisuals.SetTestTheme(null);
        }
        finally { groupWindow.Close(); }
        var nameWindow = new NameWindow("") { Owner = this };
        try
        {
            nameWindow.Show(); await UiDiagnostics.Snapshot(nameWindow, "save-workspace");
            WindowVisuals.SetTestTheme(true); await UiDiagnostics.Snapshot(nameWindow, "save-workspace-dark");
            WindowVisuals.SetTestTheme(null);
            var save = UiDiagnostics.Descendants<Button>(nameWindow).Single(button => button.Content as string == "Save workspace");
            if (save.IsEnabled) throw new InvalidOperationException("An empty workspace name can be saved.");
            UiDiagnostics.Descendants<TextBox>(nameWindow).Single().Text = "Research";
            if (!save.IsEnabled) throw new InvalidOperationException("Workspace naming did not enable Save.");
        }
        finally { nameWindow.Close(); }

        var settings = new SettingsWindow(store.Settings, _ => false, () => { }) { Owner = this };
        try
        {
            settings.Show();
            foreach (var name in new[] { "General", "Folders", "Local AI", "Privacy" })
            {
                var navigation = UiDiagnostics.Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == name);
                navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UiDiagnostics.Snapshot(settings, "settings-" + name.ToLowerInvariant().Replace(' ', '-'));
                if (name == "General")
                {
                    WindowVisuals.SetTestTheme(true);
                    await UiDiagnostics.Snapshot(settings, "settings-general-dark");
                    WindowVisuals.SetTestTheme(null);
                }
                if (AutomationProperties.GetItemStatus(navigation) != "Selected") throw new InvalidOperationException("Settings navigation lost its selected state.");
            }
            var foldersNav = UiDiagnostics.Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Folders");
            foldersNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); settings.UpdateLayout();
            var folders = UiDiagnostics.Descendants<TextBox>(settings).Single(field => (AutomationProperties.GetLabeledBy(field) as TextBlock)?.Text == "Additional folders");
            folders.Text = "not an absolute path";
            var privacyNav = UiDiagnostics.Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Privacy");
            privacyNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiDiagnostics.Descendants<Button>(settings).Single(button => button.Content as string == "Save settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UiDiagnostics.Snapshot(settings, "settings-validation");
            if (AutomationProperties.GetItemStatus(foldersNav) != "Selected" || !folders.IsKeyboardFocusWithin) throw new InvalidOperationException("Invalid settings did not reveal and focus the field.");
        }
        finally { settings.Close(); }
        var previewPath = Path.Combine(App.DataDirectory, "Note.txt");
        await File.WriteAllTextAsync(previewPath, "Scout preview fixture");
        var preview = new PreviewWindow(new("ui:preview", ItemKind.File, "Note.txt", previewPath)) { Owner = this };
        try
        {
            preview.Show(); await preview.CheckAsync(); await UiDiagnostics.Snapshot(preview, "quick-look");
            WindowVisuals.SetTestTheme(true); await UiDiagnostics.Snapshot(preview, "quick-look-dark");
            WindowVisuals.SetTestTheme(null);
        }
        finally { preview.Close(); }
    }
}

internal static class UiDiagnostics
{
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    internal static async Task Snapshot(Window window, string name, int pixelScale = 1)
    {
        await Task.Delay(200); window.UpdateLayout();
        window.BeginAnimation(Window.HeightProperty, null);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
        if (window.Content is FrameworkElement content) content.BeginAnimation(UIElement.OpacityProperty, null);
        // DWM's desktop backdrop is outside the WPF visual tree. Use the solid
        // fallback surface for deterministic, readable client-area screenshots.
        var frame = window.Content as Border;
        var material = frame?.Background;
        var renderer = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * pixelScale),
            (int)Math.Ceiling(window.ActualHeight * pixelScale),
            96 * pixelScale, 96 * pixelScale, PixelFormats.Pbgra32);
        try { if (frame != null) frame.Background = WindowVisuals.Brush("Canvas"); renderer.Render(window); }
        finally { if (frame != null) frame.Background = material; }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(renderer));
        using var output = File.Create(Path.Combine(App.DataDirectory, name + ".png")); encoder.Save(output);
    }

    internal static async Task CaptureBackdrop(Window window, string name, System.Windows.Media.Color background)
    {
        // Capture the real compositor output with a controlled backdrop. RenderTargetBitmap
        // cannot include DWM acrylic, shadows, or the application behind the launcher.
        var fixture = new Window
        {
            Title = "Scout backdrop fixture", Width = window.ActualWidth + 240, Height = window.ActualHeight + 240,
            Left = window.Left - 120, Top = window.Top - 120, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Background = new SolidColorBrush(background)
        };
        try
        {
            fixture.Show(); window.Activate();
            await Task.Delay(180);
            var origin = window.PointToScreen(new System.Windows.Point(0, 0));
            var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var width = Math.Max(1, (int)Math.Round(window.ActualWidth * scale.M11));
            var height = Math.Max(1, (int)Math.Round(window.ActualHeight * scale.M22));
            const int shadowMargin = 32;
            using var bitmap = new System.Drawing.Bitmap(width + 2 * shadowMargin, height + 2 * shadowMargin);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen((int)Math.Round(origin.X) - shadowMargin,
                    (int)Math.Round(origin.Y) - shadowMargin, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(App.DataDirectory, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 6)
        {
            // Some test hosts have no interactive desktop capture handle. Keep the
            // deterministic WPF checks running and record why this capture is absent.
            File.WriteAllText(Path.Combine(App.DataDirectory, "desktop-capture-unavailable.txt"), error.Message);
        }
        finally { fixture.Close(); window.Activate(); }
    }
}
