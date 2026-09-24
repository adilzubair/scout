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
        async Task SearchView(string text, Scope filter, string name)
        {
            scope = filter; UpdateScopes(); Query.Text = text; RefreshLocal(); await WaitForSearch();
            await UiDiagnostics.Snapshot(this, name);
            if (Query.ActualWidth < 200 || Results.ActualHeight < 100) throw new InvalidOperationException("Launcher content was clipped.");
        }
        await SearchView("", Scope.All, "launcher-empty");
        await SearchView("pdfs", Scope.All, "launcher-group");
        if (GroupBar.Visibility != Visibility.Visible || GroupBar.ActualHeight < 40) throw new InvalidOperationException("Group controls are not visible.");
        ClearGroup();
        await SearchView("15% of 240", Scope.All, "launcher-calculator");
        await SearchView("", Scope.Workspaces, "launcher-workspaces");
        await SearchView("no-such-file", Scope.Files, "launcher-no-results");
        await SearchView("pdf", Scope.All, "launcher-results");
        var longTitle = new Candidate("ui:long", ItemKind.File, new string('W', 180) + ".pdf", "C:\\Documents\\Long title.pdf") { Detail = new string('x', 240), Source = "Files" };
        ApplyRows([new SearchHit(longTitle, 1000)]);
        await UiDiagnostics.Snapshot(this, "launcher-long-title");
        if (ResultCount.TranslatePoint(new System.Windows.Point(ResultCount.ActualWidth, 0), this).X > ActualWidth) throw new InvalidOperationException("Long result text overflowed the panel.");
        RefreshLocal(); await WaitForSearch();

        var groupWindow = new GroupWindow(current!.Hits.Select(hit => hit.Candidate).ToArray()) { Owner = this };
        try { groupWindow.Show(); await UiDiagnostics.Snapshot(groupWindow, "review-group"); }
        finally { groupWindow.Close(); }
        var nameWindow = new NameWindow("") { Owner = this };
        try
        {
            nameWindow.Show(); await UiDiagnostics.Snapshot(nameWindow, "save-workspace");
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
        try { preview.Show(); await preview.CheckAsync(); await UiDiagnostics.Snapshot(preview, "quick-look"); }
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

    internal static async Task Snapshot(Window window, string name)
    {
        await Task.Delay(200); window.UpdateLayout();
        window.BeginAnimation(Window.HeightProperty, null);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
        if (window.Content is FrameworkElement content) content.BeginAnimation(UIElement.OpacityProperty, null);
        // DWM's desktop backdrop is outside the WPF visual tree. Use the solid
        // fallback surface for deterministic, readable client-area screenshots.
        var frame = window.Content as Border;
        var material = frame?.Background;
        var renderer = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        try { if (frame != null) frame.Background = WindowVisuals.Brush("Canvas"); renderer.Render(window); }
        finally { if (frame != null) frame.Background = material; }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(renderer));
        using var output = File.Create(Path.Combine(App.DataDirectory, name + ".png")); encoder.Save(output);
    }
}
