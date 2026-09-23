using System.Windows;
using System.Windows.Controls;
using Scout.Core;

namespace Scout.App;

internal sealed class NameWindow : Window
{
    private readonly TextBox name;
    public string Value => name.Text.Trim();
    public NameWindow(string initial)
    {
        Title = "Save workspace"; Width = 400; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Name this workspace", FontSize = 20, Margin = new Thickness(0, 0, 0, 16) });
        name = new TextBox { Text = initial, MaxLength = 80 }; panel.Children.Add(name);
        var save = new Button { Content = "Save workspace", IsDefault = true, Margin = new Thickness(0, 16, 0, 0) }; save.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; }; panel.Children.Add(save);
        Content = panel; Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
internal sealed class GroupWindow : Window
{
    private readonly List<(CheckBox Box, Candidate Item)> entries = [];
    public Candidate[] Selected => entries.Where(e => e.Box.IsChecked == true).Select(e => e.Item).ToArray();
    public GroupWindow(Candidate[] members)
    {
        Title = "Review group"; Width = 540; Height = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(24) };
        var save = new Button { Content = "Keep selected items", IsDefault = true }; save.Click += (_, _) => DialogResult = true; DockPanel.SetDock(save, Dock.Bottom); dock.Children.Add(save);
        var heading = new TextBlock { Text = "Choose what opens together", FontSize = 20, Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        var panel = new StackPanel();
        foreach (var c in members)
        {
            var box = new CheckBox { Content = c.Title, ToolTip = c.Target, IsChecked = true }; entries.Add((box, c)); panel.Children.Add(box);
        }
        dock.Children.Add(new ScrollViewer { Content = panel }); Content = dock;
    }
}
internal sealed class SettingsWindow : Window
{
    public ScoutSettings Value { get; private set; }
    public SettingsWindow(ScoutSettings settings, Func<string, bool> applyHotkey, Action clearHistory)
    {
        Value = settings;
        Title = "Scout settings"; Width = 610; Height = 740; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "Make Scout yours", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) });
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 15, 0, 6) });
        CheckBox Check(string text, bool value) { var c = new CheckBox { Content = text, IsChecked = value }; panel.Children.Add(c); return c; }
        TextBox Field(string text) { var t = new TextBox { Text = text }; panel.Children.Add(t); return t; }
        Label("Search sources");
        var windows = Check("Use Windows Search to find additional results", settings.WindowsSearch);
        var recent = Check("Include Windows Recent Items for opened-file searches", settings.RecentItems);
        var web = Check("Offer web search when searching All or Links", settings.WebFallback);
        var chrome = Check("Include Chrome visits and downloads from the last 90 days", settings.ChromeHistory);
        var edge = Check("Include Edge visits and downloads from the last 90 days", settings.EdgeHistory);
        panel.Children.Add(new TextBlock { Text = "Browser history stays on this PC. If AI is enabled, matching titles and hosts are sent only to your local Laya process.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        Label("Additional folders (one absolute path per line)"); var folders = Field(string.Join(Environment.NewLine, settings.ExtraFolders)); folders.AcceptsReturn = true; folders.MinHeight = 60;
        Label("Excluded folders (one absolute path per line)"); var excluded = Field(string.Join(Environment.NewLine, settings.Exclusions)); excluded.AcceptsReturn = true; excluded.MinHeight = 60;
        Label("Automation folders (one absolute path per line)"); var automations = Field(string.Join(Environment.NewLine, settings.AutomationFolders)); automations.AcceptsReturn = true; automations.MinHeight = 50;
        panel.Children.Add(new TextBlock { Text = "Put your Windows shortcuts (.lnk) or Power Automate desktop links (.url) in these folders. Search a shortcut's name to run it.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        Label("Keyboard shortcut"); var shortcut = Field(settings.Hotkey);
        Label("Local AI"); var ai = Check("Use Laya to understand descriptions", settings.AiEnabled);
        panel.Children.Add(new TextBlock { Text = "Runs on this PC. Setup downloads Python and model weights once; searches stay local. CUDA setup requires a supported NVIDIA GPU and several GB of disk space.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        Label("Laya Python executable"); var python = Field(settings.PythonPath.Length > 0 ? settings.PythonPath : LocalSetup.FindPython());
        var browse = new Button { Content = "Choose python.exe", HorizontalAlignment = HorizontalAlignment.Left }; browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Python|python.exe", CheckFileExists = true }; if (picker.ShowDialog(this) == true) python.Text = picker.FileName; }; panel.Children.Add(browse);
        Label("Checkpoint"); var checkpoint = new ComboBox { ItemsSource = new[] { "english", "multilingual", "typed-decisions" }, SelectedItem = settings.Model, Padding = new Thickness(7) }; panel.Children.Add(checkpoint);
        Label("Device"); var device = new ComboBox { ItemsSource = new[] { "cpu", "cuda" }, SelectedItem = settings.Device, Padding = new Thickness(7) }; panel.Children.Add(device);
        var setup = new Button { Content = "Set up local AI", HorizontalAlignment = HorizontalAlignment.Left };
        var setupStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        panel.Children.Add(setup); panel.Children.Add(setupStatus);
        var setupLifetime = new CancellationTokenSource(); Closed += (_, _) => setupLifetime.Cancel();
        setup.Click += async (_, _) =>
        {
            setup.IsEnabled = false; setupStatus.Text = "Setting up local AI…";
            try
            {
                python.Text = await LocalSetup.InstallAsync((string)device.SelectedItem == "cuda", new Progress<string>(text => setupStatus.Text = text), setupLifetime.Token);
                checkpoint.SelectedItem = "english"; ai.IsChecked = true; setupStatus.Text = "Laya is ready. Save settings to enable it.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { setupStatus.Text = error.Message; }
            finally { setup.IsEnabled = true; }
        };
        var clear = new Button { Content = "Clear launch history", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) };
        clear.Click += (_, _) => { clearHistory(); clear.Content = "Launch history cleared"; }; panel.Children.Add(clear);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = System.Windows.Media.Brushes.Firebrick }; panel.Children.Add(feedback);
        var save = new Button { Content = "Save settings", Margin = new Thickness(0, 18, 0, 0) }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var extra = Lines(folders.Text); var exclusions = Lines(excluded.Text); var automationFolders = Lines(automations.Text);
            if (extra.Concat(exclusions).Concat(automationFolders).Any(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p))) { feedback.Text = "Use existing, absolute folder paths."; return; }
            if (ai.IsChecked == true && !File.Exists(python.Text.Trim())) { feedback.Text = "Choose the Python executable created by setup-laya.ps1."; return; }
            if (!applyHotkey(shortcut.Text.Trim())) { feedback.Text = "That hotkey is invalid or already in use. Try Ctrl+Alt+S."; return; }
            Value = new ScoutSettings { ChromeHistory = chrome.IsChecked == true, EdgeHistory = edge.IsChecked == true, WindowsSearch = windows.IsChecked == true, RecentItems = recent.IsChecked == true, WebFallback = web.IsChecked == true, AutomationFolders = automationFolders, AiEnabled = ai.IsChecked == true, PythonPath = python.Text.Trim(), Model = (string)checkpoint.SelectedItem, Device = (string)device.SelectedItem, CpuThreads = settings.CpuThreads, Hotkey = shortcut.Text.Trim(), ExtraFolders = extra, Exclusions = exclusions };
            DialogResult = true;
        };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
