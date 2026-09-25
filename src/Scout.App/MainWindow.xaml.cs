using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Scout.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Scout.App;

public partial class MainWindow : Window
{
    private readonly ScoutStore store;
    private readonly IndexService index;
    private readonly ModelHost model;
    private Scope scope;
    private CancellationTokenSource search = new();
    private long revision;
    private bool manualSelection;
    private bool applying;
    private bool groupEdited;
    private bool executing;
    private readonly Dictionary<string, Candidate> group = [];
    private readonly Dictionary<string, bool> groupChoices = [];
    private string? editingWorkspace;
    private string? editingName;
    private Candidate[] enriched = [];
    private SearchResult? current;
    private IReadOnlyList<SearchHit> displayedHits = [];
    private RankResult? judgment;
    private readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, "Local\\Scout.Show");
    private readonly RegisteredWaitHandle wait;
    private readonly DispatcherTimer indexRefresh;
    private readonly DispatcherTimer uiRefresh;
    private readonly DispatcherTimer modelIdle;
    private bool indexChanged;
    private string indexStatus = "Updating index…";
    private string hotkey = "";
    private Task localSearchTask = Task.CompletedTask;
    private Task refinementTask = Task.CompletedTask, enrichmentTask = Task.CompletedTask;
    private long appliedRevision, observedIndexVersion = -1;
    private double requestedHeight = 76;
    private bool suggestionsRevealed, expanded;
    private string lastRenderedQuery = "";
    private HashSet<string> lastRenderedIds = [];

    internal MainWindow(ScoutStore store, IndexService index, ModelHost model)
    {
        this.store = store; this.index = index; this.model = model;
        InitializeComponent();
        WindowVisuals.Configure(this);
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            ApplyHotkey(store.Settings.Hotkey);
        };
        Loaded += (_, _) => { WindowVisuals.PlaceLauncher(this); UpdateScopes(); UpdateExpansion(); Query.Focus(); RefreshLocal(); };
        Closing += (_, e) => { if (!Application.Current.Dispatcher.HasShutdownStarted) { e.Cancel = true; HidePanel(); } };
        index.Changed += text => Dispatcher.BeginInvoke(() => { indexStatus = text; Status.ToolTip = indexStatus; indexChanged = true; });
        uiRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        uiRefresh.Tick += (_, _) =>
        {
            if (!indexChanged || !IsVisible) return;
            indexChanged = false;
            if (observedIndexVersion != index.Version) { observedIndexVersion = index.Version; ScheduleSearch(false); }
            else if (Query.Text.Length == 0) Status.Text = "Scout";
        }; uiRefresh.Start();
        modelIdle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        modelIdle.Tick += async (_, _) =>
        {
            modelIdle.Stop();
            if (!IsVisible) await model.ResetAsync();
        };
        indexRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        indexRefresh.Tick += async (_, _) => await index.RefreshAsync(); indexRefresh.Start();
        wait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(() => Toggle(true)), null, -1, false);
        Application.Current.Exit += (_, _) => { search.Cancel(); wait.Unregister(null); showEvent.Dispose(); WindowsIntegration.ReleaseHotkey(this); };
    }
    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    { if (msg == 0x0312 && wParam == 42) { Toggle(); handled = true; } return 0; }
    internal bool ApplyHotkey(string value)
    {
        if (hotkey.Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
        if (!WindowsIntegration.SetHotkey(this, value)) { Status.Text = "Hotkey unavailable. Choose another in Settings."; return false; }
        hotkey = value; return true;
    }
    internal void Toggle(bool show = false)
    {
        if (IsVisible && !show) { HidePanel(); return; }
        modelIdle.Stop();
        _ = model.WarmAsync(store.Settings);
        WindowVisuals.RefreshTheme(); UpdateExpansion();
        model.Context = WindowsIntegration.CaptureContext();
        Show(); Activate(); WindowsIntegration.SetForegroundWindow(new WindowInteropHelper(this).Handle); Query.Focus(); Query.SelectAll();
        _ = index.RefreshAsync(forceHistory: true); ScheduleSearch(false);
    }
    private void HidePanel() { search.Cancel(); revision++; suggestionsRevealed = false; UpdateExpansion(); Hide(); modelIdle.Stop(); modelIdle.Start(); }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        for (var source = e.OriginalSource as DependencyObject; source != null; source = VisualTreeHelper.GetParent(source))
            if (source is Button or TextBox) return;
        DragMove();
    }
    private void ClearQueryClick(object sender, RoutedEventArgs e) { Query.Clear(); Query.Focus(); }
    private void DismissClick(object sender, RoutedEventArgs e) => HidePanel();
    private void QueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || Placeholder == null) return;
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearQuery.Visibility = Query.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Query.Text.Length == 0) suggestionsRevealed = false;
        enriched = []; manualSelection = false;
        if (!groupEdited && editingWorkspace == null) { group.Clear(); groupChoices.Clear(); UpdateGroup(); }
        UpdateExpansion();
        ScheduleSearch(true);
    }
    private void ScopeClick(object sender, RoutedEventArgs e) { scope = (Scope)int.Parse((string)((Button)sender).Tag); suggestionsRevealed = true; UpdateExpansion(); manualSelection = false; UpdateScopes(); ScheduleSearch(false); }

    private void RevealSuggestions()
    {
        if (expanded) return;
        suggestionsRevealed = true;
        UpdateExpansion();
    }

    private void UpdateExpansion()
    {
        if (!IsInitialized) return;
        var shouldExpand = Query.Text.Length > 0 || suggestionsRevealed || group.Count > 0 || editingWorkspace != null;
        if (expanded == shouldExpand) return;
        expanded = shouldExpand;
        ScopesRow.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ResultsRegion.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        FooterBar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        GroupBar.Visibility = expanded && (group.Count > 0 || editingWorkspace != null) ? Visibility.Visible : Visibility.Collapsed;
        if (expanded && WindowVisuals.MotionEnabled && IsVisible)
        {
            ResultsRegion.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
            var shift = new TranslateTransform();
            ResultsRegion.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-5, 0, TimeSpan.FromMilliseconds(130))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        ResizeToResults(Results.Items.Count, (Results.Items.Cast<ResultRow>().FirstOrDefault()?.Candidate.Kind == ItemKind.Calculation));
    }
    private void UpdateScopes()
    {
        foreach (Button button in Scopes.Children)
        {
            var selected = int.Parse((string)button.Tag) == (int)scope;
            button.Background = selected ? WindowVisuals.Brush("Selection") : Brushes.Transparent;
            button.Foreground = WindowVisuals.Brush(selected ? "Ink" : "Muted");
            button.BorderBrush = Brushes.Transparent;
            button.FontWeight = selected ? FontWeights.Medium : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "Selected" : "");
        }
    }
    private void ScheduleSearch(bool enrich)
    {
        search.Cancel(); search = new CancellationTokenSource(); var token = search.Token; var generation = ++revision;
        Results.IsEnabled = false; ActionsButton.IsEnabled = false; judgment = null;
        if (Results.ItemsSource is ResultRow[] previous) foreach (var row in previous) row.ClearReady();
        localSearchTask = RunLocalSearch(generation, Query.Text, scope, enrich, token);
    }
    private void RefreshLocal() => ScheduleSearch(false);
    private async Task RunLocalSearch(long generation, string query, Scope requestedScope, bool enrich, CancellationToken token)
    {
        try
        {
            await Task.Delay(30, token);
            var catalog = index.Catalog;
            var library = store.Library;
            var extra = enriched.Concat(store.Workspaces.Select(w => w.ToCandidate())).Concat(library.Values.Where(l => (l.Pinned || l.LastLaunch != null) && l.Candidate.IsPinnable && !catalog.Contains(l.Candidate.Id)).Select(l => l.Candidate)).ToArray();
            var result = await Task.Run(() => SearchEngine.Search(query, requestedScope, catalog, library, DateTimeOffset.Now, cancellationToken: token, extra: extra), token);
            if (token.IsCancellationRequested || generation != revision) return;
            current = result; appliedRevision = generation;
            judgment = null;
            if (!groupEdited)
            {
                group.Clear();
                var members = RankingPolicy.Group(result);
                if (members.Length >= 2) foreach (var member in members) group[member.Id] = member;
            }
            UpdateGroup(); ApplyRows(result.Hits); Results.IsEnabled = true;
            Status.Text = ResultStatus(result);
            refinementTask = RefineAsync(generation, enrich && requestedScope is Scope.All or Scope.Files, token);
        }
        catch (OperationCanceledException) { }
    }
    private string ResultStatus(SearchResult result)
    {
        if (result.Query.Length == 0) return "Scout";
        if (result.Notice.Length > 0) return result.Notice;
        return result.Locations.Length == 1 ? result.Locations[0] : "Scout";
    }
    private async Task RefineAsync(long generation, bool enrich, CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
            if (enrich && current?.Hits.FirstOrDefault() is not { Candidate.Kind: ItemKind.App, Score: >= 1000 })
            {
                enrichmentTask = EnrichSeparately(generation, current!, token);
            }
            if (!store.Settings.AiEnabled || string.IsNullOrWhiteSpace(Query.Text) || current == null || !RankingPolicy.NeedsModel(current)) return;
            var local = current;
            Status.Text = "Finding the best match…";
            var ranked = await model.RankAsync(store.Settings, Query.Text, local.ModelShortlist.Select(h => h.Candidate).ToArray(), token, local.Intent);
            if (token.IsCancellationRequested || generation != revision || !IsVisible) return;
            if (ranked != null)
            {
                judgment = ranked;
                var rows = RankingPolicy.Refine(local, ranked);
                // Structured local membership is complete. Only offer semantic-only groups when
                // all shortlisted members were judged, and never overwrite a user's edits.
                if (!groupEdited && group.Count == 0 && local.Intent.Plural && ranked.All >= .75 && ranked.GroupMembers.Count >= 2)
                    foreach (var hit in rows.Where(h => h.Candidate.IsGroupable && ranked.GroupMembers.Contains(h.Candidate.Id)).Take(local.Intent.Count ?? 25)) group[hit.Candidate.Id] = hit.Candidate;
                ApplyRows(rows); UpdateGroup();
            }
            Status.Text = ResultStatus(local);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException) { if (generation == revision) Status.Text = "Local results ready"; }
    }
    private async Task EnrichSeparately(long generation, SearchResult local, CancellationToken token)
    {
        try
        {
            var found = await index.EnrichAsync(local, token);
            if (token.IsCancellationRequested || revision != generation || found.Length == 0) return;
            enriched = found;
            ScheduleSearch(false);
        }
        catch (OperationCanceledException) { }
    }
    private void ApplyRows(IEnumerable<SearchHit> hits)
    {
        var previousQuery = lastRenderedQuery;
        var previousIds = lastRenderedIds;
        var selected = manualSelection ? (Results.SelectedItem as ResultRow)?.Candidate.Id : null;
        applying = true;
        displayedHits = hits.ToArray();
        var effective = displayedHits.ToList();
        if (scope == Scope.All && current?.Intent.Literal != true && current?.Intent.Folder == null && SyntheticResults.Calculation(Query.Text) is { } calculation) effective.Insert(0, new(calculation, 2000));
        if (store.Settings.WebFallback && current?.Intent.Literal != true && current?.Intent.Folder == null && scope is Scope.All or Scope.Links && SyntheticResults.Web(Query.Text) is { } web) effective.Add(new(web, -100));
        if (group.Count >= 2)
        {
            var kinds = group.Values.Select(c => c.Kind).Distinct().ToArray();
            var noun = kinds.Length == 1 ? kinds[0] switch { ItemKind.File => "files", ItemKind.App => "apps", ItemKind.Link => "links", _ => "items" } : "items";
            effective.Insert(0, new(new Candidate("group:current", ItemKind.Group, $"Open all {group.Count} {noun}", "") { Detail = string.Join(", ", group.Values.Take(3).Select(c => c.Title)) + (group.Count > 3 ? ", …" : ""), Source = "Group" }, 2000));
        }
        var ready = current != null && RankingPolicy.Ready(current, effective, judgment);
        var rows = effective.Select((h, i) => new ResultRow(h.Candidate, h.Pinned, group.ContainsKey(h.Candidate.Id), current?.Intent.Recency ?? RecencyBasis.None, i == 0 && (ready || h.Candidate.Kind == ItemKind.Group), group.Count > 0)).ToArray();
        Results.ItemsSource = rows;
        lastRenderedQuery = Query.Text;
        lastRenderedIds = rows.Select(row => row.Candidate.Id).ToHashSet();
        Results.SelectedItem = rows.FirstOrDefault(r => r.Candidate.Id == selected) ?? rows.FirstOrDefault();
        applying = false;
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = Query.Text.Length == 0 ? "What can we help you find?" : "No matches yet";
        EmptyDetail.Text = Query.Text.Length == 0 ? "An app, a file, or a few words to get started." : "Try a different name or search in All.";
        if (Query.Text.Length == 0 && scope != Scope.All)
        {
            EmptyTitle.Text = scope == Scope.Workspaces ? "Your work, ready to reopen" : $"Find your {scope.ToString().ToLowerInvariant()}";
            EmptyDetail.Text = scope == Scope.Workspaces ? "Add results to a group, then save a workspace to open them together." : "Start typing a name. Pinned and recently opened items appear here.";
        }
        if (rows.Length == 0 && current?.Intent.Recency is RecencyBasis.Added or RecencyBasis.Downloaded) EmptyDetail.Text = "No matching arrival dates. Browser history can supply completed-download dates when enabled in Settings.";
        if (rows.Length == 0 && current?.Intent.Recency == RecencyBasis.Opened) EmptyDetail.Text = "No matching openings in Windows Recent Items or Scout launch history.";
        if (current?.Notice is { Length: > 0 } notice) EmptyDetail.Text = notice;
        ResultsHeading.Text = rows.Length == 0 ? "" : Query.Text.Length == 0 ? "Suggestions" : "Results";
        ResultCount.Text = rows.Length == 0 ? "" : $"{rows.Length} {(rows.Length == 1 ? "item" : "items")}";
        Examples.Visibility = Query.Text.Length == 0 && scope == Scope.All ? Visibility.Visible : Visibility.Collapsed;
        ResultHints.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FooterActions.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionsButton.IsEnabled = rows.Length > 0;
        ResizeToResults(rows.Length, rows.FirstOrDefault()?.Candidate.Kind == ItemKind.Calculation);
        if (expanded && previousQuery == Query.Text && WindowVisuals.MotionEnabled)
        {
            var arrivals = rows.Where(row => !previousIds.Contains(row.Candidate.Id)).ToArray();
            if (arrivals.Length > 0) Dispatcher.BeginInvoke(() => AnimateArrivals(arrivals), DispatcherPriority.Loaded);
        }
    }

    private void AnimateArrivals(ResultRow[] arrivals)
    {
        if (!expanded || !WindowVisuals.MotionEnabled) return;
        foreach (var row in arrivals)
        {
            if (Results.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) continue;
            item.BeginAnimation(OpacityProperty, new DoubleAnimation(.68, 1, TimeSpan.FromMilliseconds(110)) { FillBehavior = FillBehavior.Stop });
        }
    }
    private void ResizeToResults(int count, bool calculation)
    {
        var target = !expanded ? 76 : count == 0 ? (Examples.Visibility == Visibility.Visible ? 448 : 370)
            : Math.Max(370, 250 + Math.Min(6, count) * 60 + (calculation ? 22 : 0));
        if (GroupBar.Visibility == Visibility.Visible) target += 56;
        var height = Math.Min(target, MaxHeight);
        if (Math.Abs(requestedHeight - height) < 1) return;
        requestedHeight = height;
        BeginAnimation(HeightProperty, null);
        Height = height;
        // Resizing the native acrylic HWND on every animation frame stalls
        // query updates in the compositor. Fit immediately; keep the open fade.
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!applying && Results.IsKeyboardFocusWithin) manualSelection = true;
        if (!applying && WindowVisuals.MotionEnabled)
        {
            AnimateSelection(e.RemovedItems, false);
            AnimateSelection(e.AddedItems, true);
        }
        if (PrimaryActionHint != null)
            PrimaryActionHint.Text = Selected?.Kind == ItemKind.Calculation || current?.Intent.Action == ItemAction.Copy ? "Copy"
                : current?.Intent.Action == ItemAction.Preview && Selected?.Kind == ItemKind.File ? "Preview"
                : current?.Intent.Action == ItemAction.Reveal && Selected?.Kind is ItemKind.File or ItemKind.App or ItemKind.Folder ? "Reveal" : "Open";
    }
    private void AnimateSelection(System.Collections.IList changed, bool selected)
    {
        foreach (var row in changed)
        {
            if (Results.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item ||
                item.Template.FindName("SelectionFill", item) is not Border fill) continue;
            fill.BeginAnimation(OpacityProperty, new DoubleAnimation(selected ? 0 : 1, selected ? 1 : 0,
                TimeSpan.FromMilliseconds(selected ? 105 : 80))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }
    private void ResultMouseDown(object sender, MouseButtonEventArgs e) { manualSelection = true; }
    private Candidate? Selected => appliedRevision == revision ? (Results.SelectedItem as ResultRow)?.Candidate : null;
    private async void OpenDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Results, source) is ListBoxItem && Selected != null)
        { e.Handled = true; await OpenSelected(); }
    }
    private async Task OpenSelected()
    {
        try { await OpenSelectedCore(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception) { Status.Text = e.Message; }
    }
    private async Task OpenSelectedCore()
    {
        if (Selected is not { } item) return;
        if (item.Kind == ItemKind.Group) { await Execute(group.Values.ToArray()); return; }
        if (item.Kind == ItemKind.Calculation) { Clipboard.SetText(item.Target); Status.Text = "Result copied"; return; }
        if (item.Kind == ItemKind.System)
        {
            if (executing) return;
            executing = true;
            var generation = revision;
            try { await WindowsCommands.ExecuteAsync(item.Target, new WindowInteropHelper(this).Handle); if (generation == revision) HidePanel(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) { if (generation == revision) Status.Text = e.Message; }
            finally { executing = false; }
            return;
        }
        if (current?.Intent.Action == ItemAction.Preview && item.Kind == ItemKind.File) { Preview(item); return; }
        if (current?.Intent.Action == ItemAction.Reveal && item.Kind is ItemKind.File or ItemKind.App or ItemKind.Folder) { WindowsIntegration.Reveal(item); return; }
        if (current?.Intent.Action == ItemAction.Copy) { Clipboard.SetText(item.Target); Status.Text = "Copied"; return; }
        if (item.Kind == ItemKind.Workspace)
        {
            var workspace = store.Workspaces.FirstOrDefault(w => w.Id == item.Id);
            if (workspace != null) await Execute(workspace.Members, item);
        }
        else await Execute([item]);
    }
    private async Task Execute(IEnumerable<Candidate> items, Candidate? workspace = null)
    {
        if (executing) return;
        executing = true;
        try
        {
            var failed = new List<string>(); var successes = 0; var query = Query.Text; var generation = revision;
            foreach (var item in items.DistinctBy(c => c.Id).Take(25).ToArray())
            {
                try { WindowsIntegration.Open(item); if (item.IsPinnable) await Task.Run(() => store.RecordLaunch(item, query, DateTimeOffset.Now)); successes++; }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { failed.Add(item.Title); }
                await Task.Delay(60);
            }
            if (failed.Count == 0)
            {
                if (workspace != null) await Task.Run(() => store.RecordLaunch(workspace, query, DateTimeOffset.Now));
                if (generation == revision) HidePanel();
            }
            else if (generation == revision) Status.Text = $"Opened {successes}; unavailable: {string.Join(", ", failed)}";
        }
        finally { executing = false; }
    }
    private void ToggleGroup()
    {
        if (Selected is not { IsGroupable: true } c) return;
        groupEdited = true;
        if (group.Remove(c.Id)) groupChoices[c.Id] = false;
        else if (group.Count < 25) { group[c.Id] = c; groupChoices[c.Id] = true; }
        else { Status.Text = "Groups can contain up to 25 items."; return; }
        UpdateGroup(); if (current != null) ApplyRows(displayedHits);
    }
    private void MemberClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ResultRow row || !row.Candidate.IsGroupable) return;
        groupEdited = true;
        if (!group.Remove(row.Candidate.Id) && group.Count < 25) group[row.Candidate.Id] = row.Candidate;
        groupChoices[row.Candidate.Id] = group.ContainsKey(row.Candidate.Id); UpdateGroup(); ApplyRows(displayedHits); e.Handled = true;
    }
    private void Preview(Candidate item) { if (item.Kind == ItemKind.File) new PreviewWindow(item) { Owner = this }.ShowDialog(); }
    private void UpdateGroup() { GroupBar.Visibility = expanded && (group.Count > 0 || editingWorkspace != null) ? Visibility.Visible : Visibility.Collapsed; GroupLabel.Text = $"{group.Count} selected"; UpdateExpansion(); }
    private void ActionsClick(object sender, RoutedEventArgs e) => ShowActions();
    private void ShowActions()
    {
        RevealSuggestions();
        if (Selected is not { } item) return;
        var menu = new ContextMenu { PlacementTarget = ActionsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Add(string label, Action action) { var parts = label.Split("    ", 2); var entry = new MenuItem { Header = parts[0], InputGestureText = parts.Length > 1 ? parts[1] : "" }; entry.Click += (_, _) => { try { action(); } catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception) { Status.Text = e.Message; } }; menu.Items.Add(entry); }
        Add(item.Kind == ItemKind.Calculation ? "Copy result    Enter" : "Open    Enter", () => _ = OpenSelected());
        if (item.IsPinnable) Add("Pin / unpin    Ctrl+P", () => { store.TogglePin(item); RefreshLocal(); });
        if (item.IsGroupable || item.Kind is ItemKind.Calculation or ItemKind.WebSearch or ItemKind.Automation)
        {
            Add("Copy path or URL    Ctrl+Shift+C", () => Clipboard.SetText(item.Target));
            if (item.Kind is ItemKind.File or ItemKind.App or ItemKind.Folder) Add("Reveal in Explorer    Ctrl+R", () => WindowsIntegration.Reveal(item));
            if (item.Kind == ItemKind.File) Add("Preview    Ctrl+Space", () => Preview(item));
            if (item.IsGroupable) Add("Include / exclude from group    Ctrl+Shift+Space", ToggleGroup);
        }
        else if (item.Kind == ItemKind.Workspace)
        {
            Add("Review and edit workspace", () =>
            {
                var workspace = store.Workspaces.First(w => w.Id == item.Id);
                editingWorkspace = workspace.Id; editingName = workspace.Name; group.Clear();
                foreach (var c in workspace.Members) group[c.Id] = c;
                groupEdited = true; UpdateGroup(); ReviewGroup();
            });
            Add("Delete workspace", () => { store.DeleteWorkspace(item.Id); if (editingWorkspace == item.Id) ClearGroup(); RefreshLocal(); });
        }
        menu.IsOpen = true;
    }
    private void ReviewGroupClick(object sender, RoutedEventArgs e) => ReviewGroup();
    private void ReviewGroup()
    {
        var dialog = new GroupWindow(group.Values.ToArray()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            group.Clear(); foreach (var c in dialog.Selected) group[c.Id] = c;
            groupEdited = true; UpdateGroup(); if (current != null) ApplyRows(displayedHits);
        }
    }
    private void ClearGroup() { group.Clear(); groupChoices.Clear(); editingWorkspace = null; editingName = null; groupEdited = false; UpdateGroup(); }
    private void SaveWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (group.Count < 2) { Status.Text = "Select at least two items to save a workspace."; return; }
        var name = new NameWindow(editingName ?? "") { Owner = this };
        if (name.ShowDialog() != true) return;
        try { store.SaveWorkspace(name.Value, group.Values, editingWorkspace); ClearGroup(); RefreshLocal(); Status.Text = "Workspace saved"; }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
    }
    private async void OpenGroupClick(object sender, RoutedEventArgs e) => await Execute(group.Values.ToArray());
    private void SettingsClick(object sender, RoutedEventArgs e) => ShowSettings();
    internal async void ShowSettings()
    {
        Toggle(true);
        var dialog = new SettingsWindow(store.Settings, ApplyHotkey, () => store.ClearLaunchHistory()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            search.Cancel(); revision++; await model.ResetAsync(); store.SaveSettings(dialog.Value); enriched = []; ClearGroup();
            _ = index.ConfigureAsync(dialog.Value); _ = model.WarmAsync(dialog.Value); ScheduleSearch(false);
        }
        Query.Focus();
    }
    private void ExampleClick(object sender, RoutedEventArgs e) { Query.Text = (string)((Button)sender).Tag; Query.Focus(); Query.CaretIndex = Query.Text.Length; }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Escape) { if (group.Count > 0 || editingWorkspace != null) { ClearGroup(); RefreshLocal(); } else HidePanel(); }
        else if (ctrl && e.Key == Key.K) ShowActions();
        else if (ctrl && e.Key == Key.OemComma) ShowSettings();
        else if (ctrl && e.Key == Key.Q) Application.Current.Shutdown();
        else if (ctrl && shift && e.Key == Key.Space) ToggleGroup();
        else if (ctrl && e.Key == Key.Space && Selected is { Kind: ItemKind.File } preview) Preview(preview);
        else if (ctrl && e.Key == Key.P && Selected is { } pin) { store.TogglePin(pin); RefreshLocal(); }
        else if (ctrl && shift && e.Key == Key.C && Selected is { } copy && copy.Kind is not (ItemKind.Workspace or ItemKind.Group or ItemKind.System))
        { try { Clipboard.SetText(copy.Target); Status.Text = "Copied"; } catch (System.Runtime.InteropServices.COMException) { Status.Text = "Clipboard is busy. Try again."; } }
        else if (ctrl && e.Key == Key.R && Selected is { } reveal)
        { try { WindowsIntegration.Reveal(reveal); } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Status.Text = ex.Message; } }
        else if (e.Key == Key.Tab) { RevealSuggestions(); scope = (Scope)(((int)scope + (shift ? 4 : 1)) % 5); UpdateScopes(); ScheduleSearch(false); }
        else if (e.Key is Key.Up or Key.Down)
        {
            RevealSuggestions();
            manualSelection = true; var delta = e.Key == Key.Down ? 1 : -1;
            if (Results.Items.Count > 0) { Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1); Results.ScrollIntoView(Results.SelectedItem); }
        }
        else if (e.Key == Key.Enter && e.OriginalSource is not Button) { e.Handled = true; await OpenSelected(); }
        else return;
        e.Handled = true;
    }
    private sealed class ResultRow(Candidate candidate, bool pinned, bool grouped, RecencyBasis basis, bool ready, bool membership) : System.ComponentModel.INotifyPropertyChanged
    {
        public Candidate Candidate => candidate;
        public bool Grouped => grouped;
        public Visibility MembershipVisibility => membership && candidate.IsGroupable ? Visibility.Visible : Visibility.Collapsed;
        public string ReadyMark => ready ? "↵" : "";
        public bool IsReady => ready;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void ClearReady() { ready = false; PropertyChanged?.Invoke(this, new(nameof(ReadyMark))); PropertyChanged?.Invoke(this, new(nameof(IsReady))); }
        private ImageSource? icon;
        private bool requestedIcon;
        public ImageSource? Icon { get { if (!requestedIcon && candidate.Kind is ItemKind.File or ItemKind.App or ItemKind.Automation or ItemKind.Folder) { requestedIcon = true; _ = LoadIcon(); } return icon; } }
        private async Task LoadIcon() { icon = await NativeIcons.Get(candidate.Target); PropertyChanged?.Invoke(this, new(nameof(Icon))); PropertyChanged?.Invoke(this, new(nameof(GlyphVisibility))); PropertyChanged?.Invoke(this, new(nameof(IconBackground))); }
        public Visibility GlyphVisibility => icon == null ? Visibility.Visible : Visibility.Collapsed;
        public string Title => Candidate.Title;
        public string Detail => Candidate.Date(basis) is { } date
            ? $"{(basis == RecencyBasis.Downloaded && Candidate.DownloadedAt == null ? "Arrived" : basis)} {Age(date)} · {Candidate.Detail}" : Candidate.Detail.Length > 0 ? Candidate.Detail : Candidate.Source;
        public string Evidence => Candidate.Evidence(basis) + (Candidate.Date(basis) is { } date ? $"\n{date.LocalDateTime:F}" : "") + "\n" + Candidate.Target;
        private static string Age(DateTimeOffset date)
        {
            var span = DateTimeOffset.Now - date;
            return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} hr ago" : $"{(int)span.TotalDays} days ago";
        }
        public string Marker => pinned ? "\uE718" : "";
        public string KindLabel => Candidate.Kind switch { ItemKind.File => Path.GetExtension(Candidate.Title).TrimStart('.').ToUpperInvariant(), ItemKind.WebSearch => "Web", ItemKind.Calculation => "Calculator", ItemKind.System => "System", ItemKind.Group => "Group", _ => Candidate.Kind.ToString() };
        public Brush IconBackground => icon != null ? Brushes.Transparent : WindowVisuals.Brush(Candidate.Kind == ItemKind.Calculation ? "IconGreenSurface" : "IconSurface");
        public Brush IconForeground => WindowVisuals.Brush(Candidate.Kind == ItemKind.Calculation ? "IconGreenInk" : "IconInk");
        public string Glyph => Candidate.Kind switch { ItemKind.App or ItemKind.Automation => "\uE71D", ItemKind.Folder => "\uE8B7", ItemKind.Link or ItemKind.WebSearch => "\uE774", ItemKind.Workspace or ItemKind.Group => "\uE8F1", ItemKind.Calculation => "\uE8EF", ItemKind.System => "\uE713", _ => "\uE8A5" };
    }
    internal async Task SmokeCheck(bool captureUi = false)
    {
        Query.Text = "pdf"; await WaitForSearch();
        if (current!.Hits.Count != 3) throw new InvalidOperationException("Fixture search failed");
        scope = Scope.Apps; RefreshLocal(); await WaitForSearch();
        if (Results.Items.Count != 0) throw new InvalidOperationException("Scope leaked file results");
        scope = Scope.All; RefreshLocal(); await WaitForSearch();
        Results.SelectedIndex = 1; manualSelection = true;
        var selected = Selected!.Id;
        ApplyRows(current!.Hits.Reverse());
        if (Selected?.Id != selected) throw new InvalidOperationException("Late ranking replaced manual selection");
        ToggleGroup(); Query.Text = "roadmap"; await WaitForSearch();
        if (group.Count != 1) throw new InvalidOperationException("Query change lost manual group");
        ClearGroup(); Query.Text = "pdf"; await WaitForSearch();
        if (Environment.GetEnvironmentVariable("SCOUT_TEST_PYTHON") is { Length: > 0 } python)
        {
            var settings = new ScoutSettings { AiEnabled = true, PythonPath = python, Device = Environment.GetEnvironmentVariable("SCOUT_TEST_DEVICE") ?? "cpu" };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ranked = await model.RankAsync(settings, "roadmap", current!.Hits.Select(h => h.Candidate).ToArray(), deadline.Token);
            if (ranked == null || ranked.Scores.Count == 0) throw new InvalidOperationException("Model host failed: " + model.Status);
        }
        Query.Text = "the pdf I just downloaded"; await WaitForSearch();
        if (Selected?.Title != "Product Roadmap.pdf") throw new InvalidOperationException("Arrival selection failed");
        Query.Text = "pdfs"; await WaitForSearch();
        if (Selected?.Kind != ItemKind.Group || group.Count != 3) throw new InvalidOperationException("Open all row missing");
        var excluded = group.Values.Last();
        group.Remove(excluded.Id); groupEdited = true; ApplyRows(displayedHits.Reverse());
        if (group.Count != 2 || group.ContainsKey(excluded.Id)) throw new InvalidOperationException("Reranking restored an excluded member");
        ClearGroup();
        Query.Text = "15% of 240"; await WaitForSearch();
        if (Selected is not { Kind: ItemKind.Calculation, Target: "36" }) throw new InvalidOperationException("Calculator row failed");
        Query.Text = "zzzxqv nonexistent"; await WaitForSearch();
        if (Selected?.Kind != ItemKind.WebSearch || current!.Hits.Count != 0) throw new InvalidOperationException("Web fallback failed");
        if (captureUi) await CaptureUiChecks();
        else await PreviewDiagnostics.CheckAsync(Path.Combine(App.DataDirectory, "preview-fixtures"));
        Query.Text = "pdf"; RefreshLocal(); await WaitForSearch(); UpdateLayout();
    }
    private async Task WaitForSearch()
    {
        Task pending;
        do { pending = localSearchTask; await pending; } while (pending != localSearchTask);
    }
    internal async Task<object> DiagnoseAsync()
    {
        var gaps = new List<double>(); var ticks = Stopwatch.StartNew(); var lastTick = ticks.Elapsed.TotalMilliseconds;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        heartbeat.Tick += (_, _) => { var now = ticks.Elapsed.TotalMilliseconds; gaps.Add(now - lastTick); lastTick = now; }; heartbeat.Start();
        try
        {
            var startup = Stopwatch.StartNew();
            while (!index.Candidates.Any(c => c.Kind == ItemKind.App))
            { if (startup.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Applications were not discovered"); await Task.Delay(25); }
            var appsReadyMs = startup.Elapsed.TotalMilliseconds;
            var apps = index.Candidates.Where(c => c.Kind == ItemKind.App).ToArray();
            var checks = new List<object>();
            foreach (var query in new[] { "notepad", "chrome", "edge", "vscode", "code", "excel", "word", "terminal" })
            {
                var expected = apps.FirstOrDefault(a => a.Title.Equals(query, StringComparison.OrdinalIgnoreCase) || a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Keywords.Contains(query));
                if (expected == null) continue;
                var timer = Stopwatch.StartNew(); Query.Text = query; await WaitForSearch();
                if (current?.Query != query || Selected?.Kind != ItemKind.App) throw new InvalidOperationException($"{query} failed to select an installed application");
                checks.Add(new { query, top = Selected.Title, endToEndMs = timer.Elapsed.TotalMilliseconds, searchMs = current.ElapsedMs });
            }
            // Send the same events as real rapid typing, while background indexing runs.
            var rapid = Stopwatch.StartNew();
            for (var i = 0; i < 60; i++) { Query.Text = i % 2 == 0 ? "research report" : "notepad"; await Task.Delay(5); }
            Query.Text = "notepad"; await WaitForSearch();
            if (current?.Query != "notepad") throw new InvalidOperationException("A superseded query replaced the current results");
            var inputCycleMs = rapid.Elapsed.TotalMilliseconds;
            if (inputCycleMs > 3000) throw new InvalidOperationException("Rapid typing blocked the interface");
            await index.RefreshAsync(wait: true);
            Query.Text = "notepad"; RefreshLocal(); await WaitForSearch();
            if (Selected?.Kind != ItemKind.App) throw new InvalidOperationException("Files displaced the matching app after indexing");
            Query.Text = "zzzxqv nonexistent";
            if (Selected != null) throw new InvalidOperationException("A stale result remained executable after the query changed");
            await WaitForSearch();
            if (current!.Hits.Count != 0) throw new InvalidOperationException("An unrelated query returned spurious results");
            Query.Text = "notepad"; await WaitForSearch();
            var fullScans = index.FullScanCount;
            await index.RefreshAsync(wait: true);
            if (index.FullScanCount != fullScans) throw new InvalidOperationException("Reopening triggered a full rescan");
            var watchFile = Path.Combine(App.DataDirectory, "watcher-fixtures", "Scout-watch-" + Guid.NewGuid().ToString("N") + ".txt");
            var watchId = Candidate.Key(ItemKind.File, watchFile);
            async Task Until(Func<bool> ready)
            {
                var timeout = Stopwatch.StartNew();
                while (!ready()) { if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException("A desktop action timed out"); await Task.Delay(25); }
            }
            await File.WriteAllTextAsync(watchFile, "Scout diagnostic fixture");
            await Until(() => index.Catalog.Contains(watchId));
            var added = index.Candidates.First(c => c.Id == watchId);
            if (added.AddedAt == null) throw new InvalidOperationException("File arrival evidence was not recorded");
            await File.AppendAllTextAsync(watchFile, " updated");
            await Until(() => index.Candidates.FirstOrDefault(c => c.Id == watchId)?.ModifiedAt > added.ModifiedAt);
            File.Delete(watchFile);
            await Until(() => !index.Catalog.Contains(watchId));
            if (index.FullScanCount != fullScans) throw new InvalidOperationException("A file change triggered a full scan");
            WindowsIntegration.TestDefaultHotkey(); await Until(() => !IsVisible);
            WindowsIntegration.TestDefaultHotkey(); await Until(() => IsVisible); await WaitForSearch();
            if (gaps.Count > 0 && gaps.Order().ElementAt((int)(gaps.Count * .95)) > 100) throw new InvalidOperationException("UI responsiveness exceeded 100 ms p95");
            return new { passed = true, appsReadyMs, appCount = apps.Length, fileCount = index.Candidates.Count(c => c.Kind == ItemKind.File), checks, inputCycleMs, worstUiGapMs = gaps.DefaultIfEmpty(0).Max(), p95UiGapMs = gaps.Count == 0 ? 0 : gaps.Order().ElementAt((int)(gaps.Count * .95)), fullScans, watcherCreateModifyDelete = true, globalHotkey = true };
        }
        finally { heartbeat.Stop(); }
    }
}
