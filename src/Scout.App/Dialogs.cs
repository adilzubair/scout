using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Scout.Core;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Panel = System.Windows.Controls.Panel;

namespace Scout.App;

internal sealed class NameWindow : Window
{
    private readonly TextBox name;
    public string Value => name.Text.Trim();
    public NameWindow(string initial)
    {
        Title = "Save workspace"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowVisuals.Configure(this);
        var panel = new StackPanel();
        panel.Children.Add(WindowVisuals.Header(this, "Save workspace", "Keep these items together for next time."));
        var body = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };
        var label = new TextBlock { Text = "Workspace name", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(label);
        name = new TextBox { Text = initial, MaxLength = 80 }; AutomationProperties.SetLabeledBy(name, label); body.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        var save = new Button { Content = "Save workspace", IsDefault = true, Style = WindowVisuals.Style("PrimaryButton"), IsEnabled = Value.Length > 0 };
        name.TextChanged += (_, _) => save.IsEnabled = Value.Length > 0;
        save.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; }; actions.Children.Add(save); body.Children.Add(actions);
        panel.Children.Add(body); Content = WindowVisuals.Frame(panel); Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}

internal sealed class GroupWindow : Window
{
    private readonly List<(CheckBox Box, Candidate Item)> entries = [];
    public Candidate[] Selected => entries.Where(e => e.Box.IsChecked == true).Select(e => e.Item).ToArray();
    public GroupWindow(Candidate[] members)
    {
        Title = "Review group"; Width = 580; Height = 480; MinWidth = 420; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowVisuals.Configure(this);
        var dock = new DockPanel();
        var header = WindowVisuals.Header(this, "Open together", "Choose the items to include in this group."); DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var footer = new DockPanel { Margin = new Thickness(24, 16, 24, 20) }; DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var count = new TextBlock { Text = $"{members.Length} items selected", Foreground = WindowVisuals.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center }; footer.Children.Add(count);
        var save = new Button { Content = "Keep selected items", IsDefault = true, Style = WindowVisuals.Style("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => DialogResult = true; footer.Children.Add(save);
        var panel = new StackPanel { Margin = new Thickness(24, 0, 24, 0) };
        foreach (var c in members)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = c.Target, FontSize = 11, Foreground = WindowVisuals.Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
            var box = new CheckBox { Content = label, ToolTip = c.Target, IsChecked = true, Margin = new Thickness(0) };
            AutomationProperties.SetName(box, c.Title);
            box.Click += (_, _) => count.Text = $"{Selected.Length} items selected";
            entries.Add((box, c));
            panel.Children.Add(new Border { Child = box, BorderBrush = WindowVisuals.Brush("Divider"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 15, 0, 15) });
        }
        dock.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = WindowVisuals.Frame(dock);
    }
}

internal sealed class SettingsWindow : Window
{
    public ScoutSettings Value { get; private set; }
    public SettingsWindow(ScoutSettings settings, Func<string, bool> applyHotkey, Action clearHistory)
    {
        Value = settings;
        Title = "Scout settings"; Width = 800; Height = 654; MinWidth = 680; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowVisuals.Configure(this, WindowSurface.Document);
        var root = new DockPanel();
        var header = WindowVisuals.Header(this, "Settings"); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(24, 12, 24, 20) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = WindowVisuals.Brush("Error"), Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        footer.Children.Add(feedback);
        var actions = new DockPanel();
        actions.Children.Add(new TextBlock { Text = "Changes apply when you save.", FontSize = 11, Foreground = WindowVisuals.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var save = new Button { Content = "Save settings", Style = WindowVisuals.Style("PrimaryButton") }; buttons.Children.Add(save); actions.Children.Add(buttons); footer.Children.Add(actions);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        var navigation = new StackPanel { Margin = new Thickness(14, 18, 12, 0) }; body.Children.Add(navigation);
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var pageBorder = new Border { Background = WindowVisuals.Brush("PageSurface"), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 16, 0), Child = pageHost };
        Grid.SetColumn(pageBorder, 1); body.Children.Add(pageBorder); root.Children.Add(body); Content = WindowVisuals.Frame(root);
        var pages = new List<(Button Button, ScrollViewer Page)>();
        void SelectPage(ScrollViewer page)
        {
            pageHost.Content = page;
            foreach (var entry in pages)
            {
                var selected = entry.Page == page;
                entry.Button.Background = selected ? WindowVisuals.Brush("Selection") : Brushes.Transparent;
                entry.Button.Foreground = WindowVisuals.Brush("Ink");
                entry.Button.FontWeight = selected ? FontWeights.Medium : FontWeights.Normal;
                AutomationProperties.SetItemStatus(entry.Button, selected ? "Selected" : "");
            }
        }
        StackPanel Page(string title, string glyph, string description)
        {
            var panel = new StackPanel { Margin = new Thickness(26, 24, 26, 24) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = WindowVisuals.Brush("Muted"), Margin = new Thickness(0, 7, 0, 24), LineHeight = 20 });
            var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = title });
            var button = new Button { Content = label, Style = WindowVisuals.Style("QuietButton"), Padding = new Thickness(12, 11, 10, 11), Margin = new Thickness(0, 0, 0, 5), HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(button, title);
            button.Click += (_, _) => SelectPage(scroller); navigation.Children.Add(button); pages.Add((button, scroller));
            return panel;
        }
        void Note(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = WindowVisuals.Brush("Muted"), FontSize = 12, LineHeight = 19, Margin = new Thickness(0, 7, 0, 14) });
        void Heading(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) });
        CheckBox Check(Panel panel, string text, bool value)
        {
            var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Style = WindowVisuals.Style("Switch") }; AutomationProperties.SetName(box, text); panel.Children.Add(box); return box;
        }
        TextBox Field(Panel panel, string label, string text, bool multiline = false)
        {
            var caption = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) }; panel.Children.Add(caption);
            var field = new TextBox { Text = text, AcceptsReturn = multiline, MinHeight = multiline ? 76 : 0, MaxHeight = multiline ? 140 : double.PositiveInfinity, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
            AutomationProperties.SetLabeledBy(field, caption); panel.Children.Add(field); return field;
        }
        var general = Page("General", "\uE713", "Make Scout fit the way you work.");
        var shortcut = Field(general, "Open Scout", settings.Hotkey); Note(general, "Your global keyboard shortcut. For example, Ctrl+Alt+Space.");
        Heading(general, "Search behavior");
        var windows = Check(general, "Find more files with Windows Search", settings.WindowsSearch);
        var recent = Check(general, "Include recently opened files", settings.RecentItems);
        var web = Check(general, "Offer a web search in All and Links", settings.WebFallback);
        Note(general, "Windows Recent Items helps Scout find files you describe by when you last opened them.");

        var search = Page("Folders", "\uE8B7", "Choose where Scout looks for your files and shortcuts.");
        Note(search, "Desktop, Documents, Downloads, and Pictures are included automatically. Add one full folder path per line below.");
        var folders = Field(search, "Additional folders", string.Join(Environment.NewLine, settings.ExtraFolders), true);
        var excluded = Field(search, "Excluded folders", string.Join(Environment.NewLine, settings.Exclusions), true);
        var automations = Field(search, "Automation folders", string.Join(Environment.NewLine, settings.AutomationFolders), true);
        Note(search, "Add folders containing Windows shortcuts (.lnk) or Power Automate links (.url), then search a shortcut's name to run it.");

        var local = Page("Local AI", "\uE945", "Find things with a description, even when you forget the name.");
        var ai = Check(local, "Use Laya to understand descriptions", settings.AiEnabled);
        Note(local, "Runs on this PC. Setup downloads the runtime and model once; your searches stay local.");
        CheckBox? semanticHelper = null;
        if (LocalSetup.ExperimentalSemanticHelper)
        {
            semanticHelper = Check(local, "Experimental 4B semantic helper", settings.SemanticHelper);
            Note(local, "Research only. Adds a 2.5 GB model and requires a compatible GPU.");
        }
        Heading(local, "Processing device");
        var device = new ComboBox { ItemsSource = new[] { "cpu", "cuda" }, SelectedItem = settings.Device, Padding = new Thickness(10), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left }; AutomationProperties.SetName(device, "Processing device"); local.Children.Add(device);
        Note(local, "Choose cuda for a supported NVIDIA GPU, or cpu for any PC. Setup needs several GB of free space.");
        var setup = new Button { Content = "Set up local AI", Style = WindowVisuals.Style("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        var setupStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = WindowVisuals.Brush("Muted"), Margin = new Thickness(0, 10, 0, 10) };
        local.Children.Add(setup); local.Children.Add(setupStatus);
        var python = Field(local, "Python executable", settings.PythonPath.Length > 0 ? settings.PythonPath : LocalSetup.FindPython());
        var browse = new Button { Content = "Choose python.exe…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 6) };
        browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Python|python.exe", CheckFileExists = true }; if (picker.ShowDialog(this) == true) python.Text = picker.FileName; }; local.Children.Add(browse);
        Heading(local, "Checkpoint");
        var checkpoint = new ComboBox { ItemsSource = new[] { "english", "multilingual", "typed-decisions" }, SelectedItem = settings.Model, Padding = new Thickness(10) }; AutomationProperties.SetName(checkpoint, "Checkpoint"); local.Children.Add(checkpoint);
        Note(local, "English is recommended for the most reliable results.");
        var setupLifetime = new CancellationTokenSource(); Closed += (_, _) => setupLifetime.Cancel();
        setup.Click += async (_, _) =>
        {
            setup.IsEnabled = false; setupStatus.Text = "Setting up local AI…";
            try
            {
                python.Text = await LocalSetup.InstallAsync((string)device.SelectedItem == "cuda", new Progress<string>(text => setupStatus.Text = text), setupLifetime.Token, semanticHelper?.IsChecked == true);
                checkpoint.SelectedItem = "english"; ai.IsChecked = true; setupStatus.Text = "Laya is ready. Save settings to enable it.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { setupStatus.Text = error.Message; }
            finally { setup.IsEnabled = true; }
        };

        var privacy = Page("Privacy", "\uE72E", "Your search history belongs on your PC.");
        Heading(privacy, "Browser history");
        var chrome = Check(privacy, "Include Chrome visits and downloads", settings.ChromeHistory);
        var edge = Check(privacy, "Include Edge visits and downloads", settings.EdgeHistory);
        Note(privacy, "Search the last 90 days of regular browsing. Scout never reads incognito history, cookies, or open tabs. Matching titles and hosts are used only by the local AI when enabled.");
        Heading(privacy, "Launch history");
        Note(privacy, "Scout remembers the items you open to improve future results. Clearing this history removes learned aliases and launch boosts. Pins and workspaces are kept.");
        var clear = new Button { Content = "Clear launch history", HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => { clearHistory(); clear.Content = "Launch history cleared"; }; privacy.Children.Add(clear);
        Note(privacy, "Clearing history takes effect immediately.");

        void Error(string text, int page, Control field)
        {
            feedback.Text = text; feedback.Visibility = Visibility.Visible; SelectPage(pages[page].Page);
            // The target may have just been reattached from another settings page.
            pageHost.UpdateLayout(); field.BringIntoView(); field.Focus();
        }
        save.Click += (_, _) =>
        {
            string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var extra = Lines(folders.Text); var exclusions = Lines(excluded.Text); var automationFolders = Lines(automations.Text);
            foreach (var field in new[] { folders, excluded, automations })
                if (Lines(field.Text).Any(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p))) { Error("Use existing, full folder paths, such as C:\\Users\\you\\Projects.", 1, field); return; }
            if (ai.IsChecked == true && !File.Exists(python.Text.Trim())) { Error("Choose the Python executable created by local AI setup.", 2, python); return; }
            if (!applyHotkey(shortcut.Text.Trim())) { Error("That shortcut is invalid or already in use. Try Ctrl+Alt+S.", 0, shortcut); return; }
            Value = new ScoutSettings { ChromeHistory = chrome.IsChecked == true, EdgeHistory = edge.IsChecked == true, WindowsSearch = windows.IsChecked == true, RecentItems = recent.IsChecked == true, WebFallback = web.IsChecked == true, AutomationFolders = automationFolders, AiEnabled = ai.IsChecked == true, SemanticHelper = semanticHelper?.IsChecked == true, PythonPath = python.Text.Trim(), Model = (string)checkpoint.SelectedItem, Device = (string)device.SelectedItem, CpuThreads = settings.CpuThreads, Hotkey = shortcut.Text.Trim(), ExtraFolders = extra, Exclusions = exclusions };
            DialogResult = true;
        };
        SelectPage(pages[0].Page);
    }
}
