using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Scout.Core;

namespace Scout.App;

internal sealed class FolderChoice(string path, bool automatic, bool contents)
{
    public string Path { get; } = path;
    public bool Automatic { get; } = automatic;
    public bool Contents { get; set; } = contents;
    public bool Saved { get; set; }
}
internal sealed class FolderSelections
{
    public List<FolderChoice> Items { get; } = [];
    public FolderSelections(ScoutSettings settings, IEnumerable<string> defaults)
    {
        foreach (var path in defaults.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)) Add(path, automatic: true);
        foreach (var path in settings.ExtraFolders.Concat(settings.ContentFolders)) Add(path);
        foreach (var item in Items) { item.Saved = true; item.Contents = settings.ContentSearchEnabled && settings.ContentFolders.Any(p => Same(p, item.Path)); }
    }
    private static bool Same(string a, string b) => Path.TrimEndingDirectorySeparator(a.Trim()).Equals(Path.TrimEndingDirectorySeparator(b.Trim()), StringComparison.OrdinalIgnoreCase);
    public FolderChoice? Add(string path, bool automatic = false)
    {
        path = path.Trim(); if (path.Length == 0) return null;
        var existing = Items.FirstOrDefault(i => Same(i.Path, path));
        if (existing != null) return existing;
        var item = new FolderChoice(path, automatic, false); Items.Add(item); return item;
    }
    public void Remove(FolderChoice item) { if (!item.Automatic) Items.Remove(item); }
    public bool ContentsInherited(FolderChoice item) => Items.Any(parent => parent != item && parent.Contents && IndexPolicy.Within(item.Path, parent.Path));
    public string[] ExtraFolders => Items.Where(i => !i.Automatic).Select(i => i.Path).ToArray();
    public string[] ContentFolders => Items.Where(i => i.Contents && !ContentsInherited(i)).Select(i => i.Path).ToArray();
}

internal sealed class FolderSelectionView : StackPanel
{
    internal FolderSelections Selection { get; }
    internal TextBox PathField { get; }
    private readonly StackPanel rows = new();
    private readonly List<(FolderChoice Choice, TextBlock Status, CheckBox Contents)> statuses = [];
    private readonly Func<IndexProgress>? progress;
    private IndexProgress? renderedProgress;
    private bool updatingChecks;

    public FolderSelectionView(Window owner, ScoutSettings settings, Func<IndexProgress>? progress, Action<string, Control> error, Func<Window,string?>? pickFolder = null)
    {
        this.progress = progress;
        Selection = new(settings, WindowsIntegration.DefaultFolders());
        Children.Add(new TextBlock { Text = "Search folders", FontSize = 17, FontWeight = FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = "Search file names in these folders. Choose Search contents only where you want text and Office documents indexed.", TextWrapping = TextWrapping.Wrap, Foreground = WindowVisuals.Brush("Muted"), FontSize = 12, LineHeight = 18, Margin = new Thickness(0, 6, 0, 12) });
        var add = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition()); add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        PathField = new TextBox { MinWidth = 80, ToolTip = "Enter a full folder path" }; AutomationProperties.SetName(PathField, "Folder path");
        add.Children.Add(PathField);
        var append = new Button { Content = "Add", Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(append, 1); add.Children.Add(append);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(browse, 2); add.Children.Add(browse); AutomationProperties.SetName(browse, "Browse for a search folder");
        void AddPath(string path)
        {
            if (!System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path)) { error("Enter an existing, full folder path, then choose Add.", PathField); return; }
            Selection.Add(path); PathField.Clear(); Render();
        }
        append.Click += (_, _) => AddPath(PathField.Text.Trim());
        browse.Click += (_, _) =>
        {
            try { var picked = pickFolder == null ? FolderPicker.Pick(owner) : pickFolder(owner); if (picked != null) AddPath(picked); }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { error("Scout couldn't open the folder picker. Enter the full path and choose Add instead.", PathField); }
        };
        PathField.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true; AddPath(PathField.Text.Trim());
        };
        var caption = new TextBlock { Text = "Add a folder", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetLabeledBy(PathField, caption); Children.Add(caption);
        Children.Add(add);
        Children.Add(rows);
        Children.Add(new TextBlock { Text = "Temporary folders (temp, tmp and pytest-of-*), folders starting with a dot, and dependency/build folders are excluded automatically. Content search supports text and Office files up to 16 MB. PDFs, images and empty or unreadable documents are skipped. Removing Search contents clears that folder's extracted text when you save.", TextWrapping = TextWrapping.Wrap, Foreground = WindowVisuals.Brush("Muted"), FontSize = 12, LineHeight = 18, Margin = new Thickness(0, 10, 0, 16) });
        Render();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => RefreshStatuses();
        owner.Loaded += (_, _) => timer.Start(); owner.Closed += (_, _) => timer.Stop();
    }
    private void Render()
    {
        rows.Children.Clear(); statuses.Clear();
        foreach (var item in Selection.Items)
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel(); row.Children.Add(labels);
            labels.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(item.Path)) is { Length: > 0 } name ? name : item.Path, FontWeight = FontWeights.SemiBold });
            labels.Children.Add(new TextBlock { Text = item.Path, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Path, Foreground = WindowVisuals.Brush("Muted"), FontSize = 11, Margin = new Thickness(0, 3, 12, 4) });
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = WindowVisuals.Brush("Muted") }; AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); labels.Children.Add(status);
            var options = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(options, 1); row.Children.Add(options);
            var contents = new CheckBox { Content = "Search contents", IsChecked = item.Contents, Margin = new Thickness(0), FontSize = 12 }; AutomationProperties.SetName(contents, "Search contents in " + item.Path);
            contents.Checked += (_, _) => { if (updatingChecks) return; item.Contents = true; RefreshStatuses(force: true); };
            contents.Unchecked += (_, _) => { if (updatingChecks) return; item.Contents = false; RefreshStatuses(force: true); }; options.Children.Add(contents); statuses.Add((item, status, contents));
            if (!item.Automatic)
            {
                var remove = new Button { Content = "Remove", Style = WindowVisuals.Style("QuietButton"), Padding = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 }; AutomationProperties.SetName(remove, "Remove folder " + item.Path);
                remove.Click += (_, _) => { Selection.Remove(item); Render(); }; options.Children.Add(remove);
            }
            else options.Children.Add(new TextBlock { Text = "Included automatically", FontSize = 10, Foreground = WindowVisuals.Brush("Muted"), Margin = new Thickness(0, 5, 0, 0) });
            rows.Children.Add(new Border { Child = row, BorderBrush = WindowVisuals.Brush("Divider"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
        RefreshStatuses(force: true);
    }
    private void RefreshStatuses(bool force = false)
    {
        var snapshot = progress?.Invoke(); if (!force && ReferenceEquals(snapshot, renderedProgress)) return;
        renderedProgress = snapshot;
        foreach (var (choice, status, checkbox) in statuses)
        {
            var inherited = Selection.ContentsInherited(choice);
            var selected = choice.Contents || inherited;
            updatingChecks = true;
            try { checkbox.IsChecked = selected; checkbox.IsEnabled = !inherited; checkbox.ToolTip = inherited ? "Included by a parent folder. Change Search contents on that parent to turn it off." : null; }
            finally { updatingChecks = false; }
            var known = snapshot?.FindFolder(choice.Path);
            var excluded = IndexPolicy.Excluded(choice.Path, snapshot?.Exclusions);
            var changed = known != null && known.ContentsSelected != selected;
            status.ToolTip = known?.SkippedPaths is { Length: > 0 } skipped ? "Could not read these folders:\n" + string.Join("\n", skipped.Take(20))
                + (skipped.Length > 20 ? $"\n…and {skipped.Length - 20:N0} more." : "") : null;
            status.Text = excluded ? "Excluded from search" : known == null ? choice.Saved ? snapshot?.Phase == IndexPhase.Ready ? "Location unavailable or outside the active index" : "Waiting for indexing" : "Save settings to start indexing" : known.NamesText
                + (selected ? "\n" + (changed ? "Contents will be indexed after saving" : known.ContentsText + (inherited ? " (from parent folder)" : "")) : changed ? "\nContents will be removed after saving" : "");
        }
    }
}
