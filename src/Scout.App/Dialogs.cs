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
    public SettingsWindow(ScoutSettings settings, Func<string, bool> applyHotkey, Action clearHistory, MultimodalHost? multimodalHost = null, Action? addonRemoved = null, SemanticService? semantic = null, Func<Window,string?>? pickFolder = null)
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
        var save = new Button { Content = "Save settings", IsDefault = true, Style = WindowVisuals.Style("PrimaryButton") }; buttons.Children.Add(save); actions.Children.Add(buttons); footer.Children.Add(actions);
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
        Button Browse(Panel panel, TextBox field, string name)
        {
            var browse = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 6) };
            AutomationProperties.SetName(browse, name); panel.Children.Add(browse);
            browse.Click += (_, _) =>
            {
                try
                {
                    var picked = pickFolder == null ? FolderPicker.Pick(this) : pickFolder(this);
                    if (picked != null) field.Text = FolderList.Append(field.Text, picked);
                }
                catch (Exception error) when (error is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    feedback.Text = "The folder browser couldn't open. Enter the full folder path in the box above, one folder per line.";
                    feedback.Visibility = Visibility.Visible;
                    field.Focus();
                }
            };
            return browse;
        }
        var general = Page("General", "\uE713", "Make Scout fit the way you work.");
        var shortcut = Field(general, "Open Scout", settings.Hotkey); Note(general, "Your global keyboard shortcut. For example, Ctrl+Alt+Space.");
        Heading(general, "Search behavior");
        var windows = Check(general, "Find more files with Windows Search", settings.WindowsSearch);
        var recent = Check(general, "Include recently opened files", settings.RecentItems);
        var web = Check(general, "Offer a web search in All and Links", settings.WebFallback);
        Note(general, "Windows Recent Items helps Scout find files you describe by when you last opened them.");

        var search = Page("Folders", "\uE8B7", "Choose where Scout looks for your files and shortcuts.");
        Note(search, "Desktop, Documents, Downloads, and Pictures are included automatically. Add one full folder path per line below, or choose Browse… under a list.");
        Heading(search, "Content search");
        var contentSearch = Check(search, "Search inside files in selected folders", settings.ContentSearchEnabled);
        Note(search, "Off by default. Scout indexes supported text and Office files only; PDFs and OCR are not included. Extracted text stays on this PC. Saving with this off clears all persisted extracted text.");
        var contentFolders = Field(search, "Content search folders", string.Join(Environment.NewLine, settings.ContentFolders), true);
        var contentBrowse = Browse(search, contentFolders, "Browse for a content search folder");
        contentFolders.IsEnabled = contentBrowse.IsEnabled = contentSearch.IsChecked == true;
        contentSearch.Checked += (_, _) => contentFolders.IsEnabled = contentBrowse.IsEnabled = true;
        contentSearch.Unchecked += (_, _) => contentFolders.IsEnabled = contentBrowse.IsEnabled = false;
        Note(search, "Use Browse… or add one full, existing folder path per line. Only the folders listed here are included in content search.");
        var folders = Field(search, "Additional folders", string.Join(Environment.NewLine, settings.ExtraFolders), true);
        Browse(search, folders, "Browse for an additional folder");
        var excluded = Field(search, "Excluded folders", string.Join(Environment.NewLine, settings.Exclusions), true);
        Browse(search, excluded, "Browse for an excluded folder");
        var automations = Field(search, "Automation folders", string.Join(Environment.NewLine, settings.AutomationFolders), true);
        Browse(search, automations, "Browse for an automation folder");
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

        var multimodalOptions = settings.Multimodal ?? new MultimodalSettings();
        var multimodal = Page("Multimodal", "\uE8A5", "Prepare local content search across documents, images, audio, and video.");
        Note(multimodal, "Development preview: searches selected content and shows matched evidence. Quality and hardware validation are still in progress.");
        var embeddingEnabled = Check(multimodal, "Enable the multimodal add-on", multimodalOptions.Enabled);
        var embeddingPaused = Check(multimodal, "Pause background content indexing", multimodalOptions.Paused);
        var embeddingBattery = Check(multimodal,"Pause content indexing on battery power",multimodalOptions.PauseOnBattery);
        Note(multimodal, "Independent of Laya. Selected content will be processed on this PC; it is not sent to Laya or a cloud model.");
        var embeddingFolders = Field(multimodal, "Multimodal content folders", string.Join(Environment.NewLine, multimodalOptions.Folders), true);
        Browse(multimodal, embeddingFolders, "Browse for a multimodal content folder");
        Heading(multimodal, "Content types");
        var embeddingTypes = MultimodalSettings.ContentTypes.ToDictionary(type => type, type => Check(multimodal,
            char.ToUpperInvariant(type[0]) + type[1..], multimodalOptions.Modalities.Contains(type)));
        var ocrLanguages=Field(multimodal,"OCR languages",string.Join(", ",multimodalOptions.OcrLanguages));
        string installedOcr;
        try{installedOcr=string.Join(", ",Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(language=>language.LanguageTag));}
        catch(Exception error)when(error is System.Runtime.InteropServices.COMException or TypeLoadException){installedOcr="unavailable";}
        Note(multimodal,"Installed OCR languages: "+(installedOcr.Length>0?installedOcr:"none")+". Leave blank to use Windows preferences, or enter up to three tags such as en-US, ar-SA. Add OCR language packs in Windows Settings. Visual search continues when OCR is unavailable; changing languages rebuilds the content index.");
        Heading(multimodal, "Processing device");
        var embeddingDevice = new ComboBox { ItemsSource = new[] { "cpu", "cuda" }, SelectedItem = multimodalOptions.Device, Padding = new Thickness(10) };
        AutomationProperties.SetName(embeddingDevice, "Multimodal processing device"); multimodal.Children.Add(embeddingDevice);
        Note(multimodal, "Use cpu, or cuda for a supported NVIDIA GPU. Changing the device requires installing that version of the add-on.");
        Heading(multimodal, "CPU threads");
        var embeddingThreads = new ComboBox { ItemsSource = new[] { 1, 2, 4, 8, Math.Clamp(multimodalOptions.CpuThreads, 1, 32) }.Distinct().Order().ToArray(), SelectedItem = Math.Clamp(multimodalOptions.CpuThreads, 1, 32), Padding = new Thickness(10) };
        AutomationProperties.SetName(embeddingThreads, "Multimodal CPU threads"); multimodal.Children.Add(embeddingThreads);
        Heading(multimodal, "Content index limit");
        var quota = Math.Clamp(multimodalOptions.IndexQuotaBytes / (1024L*1024*1024), 1, 32);
        var embeddingQuota = new ComboBox { ItemsSource = new[] { 2L, 4L, 8L, 16L, quota }.Distinct().Order().ToArray(), SelectedItem = quota, Padding = new Thickness(10) };
        AutomationProperties.SetName(embeddingQuota, "Multimodal index limit in GB"); multimodal.Children.Add(embeddingQuota);
        Note(multimodal, "Limit in GB, separate from downloaded runtime and model storage. Indexing will pause when it reaches this limit.");
        var embeddingStatus = new TextBlock { Text = MultimodalSetup.Find() == null ? "Add-on not installed" : "Add-on installed", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), Foreground = WindowVisuals.Brush("Muted") };
        AutomationProperties.SetLiveSetting(embeddingStatus, AutomationLiveSetting.Polite);
        var embeddingInstall = new Button { Content = "Install or verify add-on", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 6) };
        var embeddingCancel = new Button { Content = "Cancel setup", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        var embeddingTest = new Button { Content = "Test add-on", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        var embeddingRemove = new Button { Content = "Remove add-on", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        var embeddingCleanup = new Button { Content = "Free unused add-on storage", IsEnabled=MultimodalSetup.Find()!=null, HorizontalAlignment=HorizontalAlignment.Left, Margin=new Thickness(0,0,0,6) };
        AutomationProperties.SetName(embeddingCleanup,"Free unused add-on storage");
        multimodal.Children.Add(embeddingCleanup);
        multimodal.Children.Add(embeddingInstall); multimodal.Children.Add(embeddingCancel); multimodal.Children.Add(embeddingTest); multimodal.Children.Add(embeddingRemove); multimodal.Children.Add(embeddingStatus);
        Note(multimodal, "Install downloads several GB and needs additional temporary disk space. Installation and removal take effect immediately. Removing the add-on preserves your ordinary file library and Laya.");
        // Keep setup actions near the top so an uninstalled add-on is easy to prepare.
        var downloadNote = multimodal.Children[multimodal.Children.Count - 1];
        multimodal.Children.Remove(downloadNote);
        var setupActions = new WrapPanel();
        foreach (var button in new[] { embeddingInstall, embeddingCancel, embeddingTest, embeddingCleanup, embeddingRemove })
        {
            multimodal.Children.Remove(button); button.Margin = new Thickness(0, 0, 8, 8); setupActions.Children.Add(button);
        }
        multimodal.Children.Remove(embeddingStatus);
        multimodal.Children.Insert(3, setupActions); multimodal.Children.Insert(4, embeddingStatus); multimodal.Children.Insert(5, downloadNote);
        CancellationTokenSource? embeddingOperation = null;
        var indexingProgress=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12),Foreground=WindowVisuals.Brush("Muted")};
        AutomationProperties.SetName(indexingProgress,"Multimodal indexing progress");multimodal.Children.Add(indexingProgress);
        var progressTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
        void UpdateProgress()
        {
            var value=semantic?.Activity;
            indexingProgress.Text=value==null?"Content scheduler unavailable in this preview session":value.Message+
                (value.Progress is {} p?$"\n{p.Ready:N0} indexed · {p.Pending:N0} queued · {p.Running:N0} processing · {p.Partial:N0} partial · {p.Unsupported:N0} unsupported · {p.Failed:N0} failed\n{p.DatabaseBytes/(1024.0*1024):N1} MB index":"");
        }
        progressTimer.Tick+=(_,_)=>UpdateProgress();UpdateProgress();progressTimer.Start();
        Closed+=(_,_)=>progressTimer.Stop();
        var retryContent=new Button{Content="Retry partial or failed content files",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,8),IsEnabled=semantic!=null};
        multimodal.Children.Add(retryContent);
        var coverage=new Button{Content="Review content coverage",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,8),IsEnabled=semantic!=null};
        coverage.Click+=(_,_)=>{if(semantic is {IsDisposed:false})new SemanticCoverageWindow(semantic){Owner=this}.ShowDialog();};
        multimodal.Children.Add(coverage);
        retryContent.Click+=async(_,_)=>
        {
            retryContent.IsEnabled=false;
            try{if(semantic is {IsDisposed:false})await semantic.RetryFailedAsync();}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {indexingProgress.Text="Content retry could not start. Check access to Scout's data folder.";}
            finally{retryContent.IsEnabled=semantic is {IsDisposed:false};}
        };
        void Busy(bool value)
        {
            embeddingInstall.IsEnabled = embeddingTest.IsEnabled = embeddingRemove.IsEnabled = embeddingDevice.IsEnabled = !value;
            embeddingCleanup.IsEnabled=!value && MultimodalSetup.Find()!=null;
            embeddingCancel.IsEnabled = value;
            retryContent.IsEnabled = !value && semantic!=null;
            save.IsEnabled = !value;
        }
        embeddingCancel.Click += (_, _) => embeddingOperation?.Cancel();
        Closed += (_, _) => embeddingOperation?.Cancel();
        embeddingInstall.Click += async (_, _) =>
        {
            Busy(true);
            embeddingOperation = CancellationTokenSource.CreateLinkedTokenSource(setupLifetime.Token);
            try
            {
                if(semantic!=null)await semantic.SuspendAsync();
                if (multimodalHost != null) await multimodalHost.BeginSetupAsync();
                await MultimodalSetup.InstallAsync((string)embeddingDevice.SelectedItem, new Progress<string>(text => embeddingStatus.Text = text), embeddingOperation.Token);
                embeddingStatus.Text = "Add-on verified. Choose folders and save settings to enable it.";
            }
            catch (OperationCanceledException) { embeddingStatus.Text = "Setup cancelled. Retry to reuse completed downloads."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { embeddingStatus.Text = error.Message; }
            finally
            {
                embeddingOperation.Dispose(); embeddingOperation = null; Busy(false);
                if(semantic is {IsDisposed:false})await semantic.ResumeAsync();
                else if (multimodalHost is { IsDisposed: false }) await multimodalHost.ConfigureAsync(multimodalOptions);
            }
        };
        embeddingTest.Click += async (_, _) =>
        {
            Busy(true); embeddingStatus.Text = "Loading the add-on for a local test…";
            embeddingOperation = CancellationTokenSource.CreateLinkedTokenSource(setupLifetime.Token);
            using var testHost = new MultimodalHost();
            try
            {
                if(semantic!=null)await semantic.SuspendAsync();
                if (multimodalHost != null) await multimodalHost.ConfigureAsync(new());
                await testHost.ConfigureAsync(new() { Enabled = true, Device = (string)embeddingDevice.SelectedItem, CpuThreads = (int)embeddingThreads.SelectedItem });
                await testHost.QueryAsync("local model readiness", embeddingOperation.Token);
                embeddingStatus.Text = "Local test passed. Text, image, audio and video encoders are available.";
            }
            catch (OperationCanceledException) { embeddingStatus.Text = "Local test cancelled."; }
            catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException) { embeddingStatus.Text = "Add-on test failed. Verify setup for the selected device and try again."; }
            finally
            {
                embeddingOperation.Dispose(); embeddingOperation = null; Busy(false);
                testHost.Dispose();
                if(semantic is {IsDisposed:false})await semantic.ResumeAsync();
                else if (multimodalHost is { IsDisposed: false }) await multimodalHost.ConfigureAsync(multimodalOptions);
            }
        };
        embeddingRemove.Click += async (_, _) =>
        {
            Busy(true);
            try
            {
                if(semantic!=null)await semantic.RemoveIndexAsync();
                if (multimodalHost != null) await multimodalHost.ConfigureAsync(new());
                embeddingEnabled.IsChecked = false;
                addonRemoved?.Invoke();
                await Task.Run(() => MultimodalSetup.Remove());
                embeddingStatus.Text = "Add-on removed. Your ordinary file library and Laya are kept.";
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { embeddingStatus.Text = "Content indexing is disabled, but the setting could not be saved. The add-on was kept. Try saving settings before removing it."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { embeddingStatus.Text = "Removal did not finish. Close other add-on workers and try again."; }
            finally { Busy(false); }
        };
        embeddingCleanup.Click+=async(_,_)=>
        {
            Busy(true);embeddingStatus.Text="Freeing unused add-on storage...";
            embeddingOperation=CancellationTokenSource.CreateLinkedTokenSource(setupLifetime.Token);
            try
            {
                if(semantic!=null)await semantic.SuspendAsync();
                if(multimodalHost!=null)await multimodalHost.BeginSetupAsync();
                var result=await Task.Run(()=>MultimodalMaintenance.Cleanup(clearDownloadCaches:true,token:embeddingOperation.Token));
                embeddingStatus.Text=$"Unused storage cleaned: {result.GenerationsRemoved} old or unfinished generations removed. Download caches cleared. Current installation, recent completed generations, content index and ordinary library kept.";
                if(result.RecoveryGenerationsKept>0)embeddingStatus.Text+=" Generations with unfinished sandbox recovery were kept.";
            }
            catch(OperationCanceledException){embeddingStatus.Text="Storage cleanup stopped. The active installation is kept; retry to finish unused data cleanup.";}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){embeddingStatus.Text="Storage cleanup did not finish. Verify the add-on, close other workers and retry.";}
            finally
            {
                embeddingOperation.Dispose();embeddingOperation=null;Busy(false);
                if(semantic is {IsDisposed:false})await semantic.ResumeAsync();
                else if(multimodalHost is {IsDisposed:false})await multimodalHost.ConfigureAsync(multimodalOptions);
            }
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
            var extra = Lines(folders.Text); var exclusions = Lines(excluded.Text); var automationFolders = Lines(automations.Text); var indexedContentFolders = Lines(contentFolders.Text);
            foreach (var field in new[] { folders, excluded, automations })
                if (Lines(field.Text).Any(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p))) { Error("Use existing, full folder paths, such as C:\\Users\\you\\Projects.", 1, field); return; }
            if (contentSearch.IsChecked == true && indexedContentFolders.Any(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p))) { Error("Use existing, full folder paths, such as C:\\Users\\you\\Projects.", 1, contentFolders); return; }
            if (ai.IsChecked == true && !File.Exists(python.Text.Trim())) { Error("Choose the Python executable created by local AI setup.", 2, python); return; }
            var embeddingRoots = Lines(embeddingFolders.Text);
            if (embeddingEnabled.IsChecked == true && (embeddingRoots.Length == 0 || embeddingRoots.Any(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p)))) { Error("Choose at least one existing folder for multimodal content search.", 3, embeddingFolders); return; }
            var selectedTypes = embeddingTypes.Where(entry => entry.Value.IsChecked == true).Select(entry => entry.Key).ToArray();
            string[] selectedOcr;
            try{selectedOcr=SemanticScope.NormalizeOcrLanguages(ocrLanguages.Text.Split([',',';',' ','\r','\n'],StringSplitOptions.RemoveEmptyEntries));}
            catch(ArgumentException error){Error(error.Message,3,ocrLanguages);return;}
            if (embeddingEnabled.IsChecked == true && selectedTypes.Length == 0) { Error("Choose at least one content type.", 3, embeddingTypes.First().Value); return; }
            if (embeddingEnabled.IsChecked == true && MultimodalSetup.Find()?.Device != (string)embeddingDevice.SelectedItem) { Error("Install the add-on for the selected processing device first.", 3, embeddingInstall); return; }
            if (!applyHotkey(shortcut.Text.Trim())) { Error("That shortcut is invalid or already in use. Try Ctrl+Alt+S.", 0, shortcut); return; }
            Value = new ScoutSettings { ChromeHistory = chrome.IsChecked == true, EdgeHistory = edge.IsChecked == true, WindowsSearch = windows.IsChecked == true, RecentItems = recent.IsChecked == true, WebFallback = web.IsChecked == true, AutomationFolders = automationFolders, ContentSearchEnabled = contentSearch.IsChecked == true, ContentFolders = indexedContentFolders, AiEnabled = ai.IsChecked == true, SemanticHelper = semanticHelper?.IsChecked == true, PythonPath = python.Text.Trim(), Model = (string)checkpoint.SelectedItem, Device = (string)device.SelectedItem, CpuThreads = settings.CpuThreads, Hotkey = shortcut.Text.Trim(), ExtraFolders = extra, Exclusions = exclusions };
            Value.Multimodal = new() { Enabled = embeddingEnabled.IsChecked == true, Paused = embeddingPaused.IsChecked == true,
                PauseOnBattery = embeddingBattery.IsChecked == true,
                Folders = embeddingRoots, Modalities = selectedTypes,OcrLanguages=selectedOcr, Device = (string)embeddingDevice.SelectedItem,
                CpuThreads = (int)embeddingThreads.SelectedItem, IndexQuotaBytes = (long)embeddingQuota.SelectedItem * 1024*1024*1024 };
            DialogResult = true;
        };
        SelectPage(pages[0].Page);
    }
}
