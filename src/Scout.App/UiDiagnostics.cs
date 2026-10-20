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
    private async Task CaptureInlinePreviewChecks()
    {
        var textPath = Path.Combine(App.DataDirectory, "Preview note.txt");
        const string canary = "The launch review is on Thursday.\nBring the revised roadmap and the updated budget.";
        await File.WriteAllTextAsync(textPath, canary);
        var note = new Candidate("ui:inline-note", ItemKind.File, "Preview note.txt", textPath) { Detail = "Launch review notes" };
        var other = new Candidate("ui:inline-other", ItemKind.App, "Another selection", "another.exe");
        Query.Text = "launch review notes"; await WaitForSearch();
        await refinementTask; await Task.WhenAll(enrichmentTask, semanticTask); ClearGroup();
        uiRefresh.Stop(); indexRefresh.Stop();
        try
        {
            ApplyRows([new(note, 100), new(other, 90)]);
            await inlinePreviewTask; UpdateLayout();
            if (InlinePreview.Content is not ScrollViewer { Content: TextBlock passage } || passage.Text != canary || DetailTitle.Text != note.Title)
                throw new InvalidOperationException("Selected text file did not show its actual contents.");
            if (DetailPane.ActualWidth < 300 || Results.ActualWidth < 280 || DetailPane.TranslatePoint(new System.Windows.Point(), this).X <= Results.TranslatePoint(new System.Windows.Point(), this).X)
                throw new InvalidOperationException("Results and preview were not laid out side by side.");
            WindowVisuals.SetTestTheme(false); await UiDiagnostics.Snapshot(this, "inline-note");
            WindowVisuals.SetTestTheme(true); await UiDiagnostics.Snapshot(this, "inline-note-dark"); WindowVisuals.SetTestTheme(null);
            // A late reader cannot overwrite the next selected result.
            Results.SelectedIndex = 1; Results.SelectedIndex = 0; var pending = inlinePreviewTask;
            Results.SelectedIndex = 1; await pending; await inlinePreviewTask;
            if (DetailTitle.Text != other.Title || UiDiagnostics.Descendants<TextBlock>(InlinePreview).Any(text => text.Text.Contains(canary)))
                throw new InvalidOperationException("A cancelled file preview replaced the current selection.");
            foreach (var (dark, contrast, name) in new[] { (false, false, "inline-symbol"), (true, false, "inline-symbol-dark"), (false, true, "inline-symbol-contrast") })
            {
                WindowVisuals.SetTestTheme(dark, contrast); UpdateLayout();
                if (FooterBar.TranslatePoint(new System.Windows.Point(0, FooterBar.ActualHeight), this).Y > ActualHeight - 1 ||
                    DetailOpen.TranslatePoint(new System.Windows.Point(0, DetailOpen.ActualHeight), this).Y >= FooterBar.TranslatePoint(new System.Windows.Point(), this).Y)
                    throw new InvalidOperationException("The fallback preview clipped the footer or its actions.");
                await UiDiagnostics.Snapshot(this, name);
            }
            WindowVisuals.SetTestTheme(null);
            Results.SelectedIndex = 0; pending = inlinePreviewTask; Query.Text = "different query";
            await pending;
            if (InlinePreview.Content != null || DetailOpen.IsEnabled)
                throw new InvalidOperationException("Changing the query left the old preview actionable.");
            await WaitForSearch();
            ApplyRows([new(note, 100)]); pending = inlinePreviewTask; HidePanel(); await pending;
            if (InlinePreview.Content != null) throw new InvalidOperationException("Hiding Scout retained an automatic file preview.");
            Toggle(true); await WaitForSearch();
            await refinementTask; await Task.WhenAll(enrichmentTask, semanticTask);
            ApplyRows([new(note, 100)]); await inlinePreviewTask;
            if (InlinePreview.Content is not ScrollViewer) throw new InvalidOperationException("Reopening Scout did not restore previews.");
            var fixtureRoot = Path.Combine(App.DataDirectory, "inline-fixtures");
            PreviewDiagnostics.CreateFixtures(fixtureRoot);
            foreach (var name in new[] { "Image.png", "Document.pdf", "Slides.pptx" })
            {
                var item = new Candidate("ui:inline:" + name, ItemKind.File, name, Path.Combine(fixtureRoot, name));
                ApplyRows([new(item, 100)]); await inlinePreviewTask;
                if (name.EndsWith(".pptx", StringComparison.Ordinal) ? InlinePreview.Content is not ScrollViewer : InlinePreview.Content is not System.Windows.Controls.Image { Source: not null })
                    throw new InvalidOperationException($"Inline preview did not read {name}: {DetailTitle.Text}; {DetailEvidence.Text}; {DetailEvidence.ToolTip}");
                if (name.EndsWith(".pdf", StringComparison.Ordinal) && !DetailKind.Text.Contains("Page 1 of 1"))
                    throw new InvalidOperationException("Inline PDF page information was missing.");
                await UiDiagnostics.Snapshot(this, "inline-" + name.Replace('.', '-'));
            }
        }
        finally
        {
            WindowVisuals.SetTestTheme(null); manualSelection = false;
            uiRefresh.Start(); indexRefresh.Start();
            if (!IsVisible) Toggle(true);
            Query.Clear(); RefreshLocal(); await WaitForSearch();
        }
    }

    // Runs only in the isolated --ui-smoke-test flow; no user settings are saved.
    private async Task CaptureUiChecks()
    {
        if(App.PdfSmokeOnly){await CapturePdfNavigationChecks(3);return;}
        if(App.PreviewSmokeExtension is {} extension){await CaptureMediaPreviews(extension=="all"?null:extension);return;}
        // Retain control failures, but still capture and validate the remaining
        // interface when a desktop service (such as the clipboard) is busy.
        IOException? controlFailure = null;
        try { await CaptureControlChecks(); }
        catch (IOException error) { controlFailure = error; }
        if(App.ControlsSmokeOnly) { if(controlFailure != null) throw controlFailure; return; }
        async Task SearchView(string text, Scope filter, string name, int pixelScale = 1)
        {
            scope = filter; UpdateScopes(); Query.Text = text; RefreshLocal(); await WaitForSearch();
            await UiDiagnostics.Snapshot(this, name, pixelScale);
            if (Query.ActualWidth < 200 || Results.ActualHeight < 100) throw new InvalidOperationException("Launcher content was clipped.");
        }
        await SearchView("", Scope.All, "launcher-empty");
        if (ActualHeight > 90 || ScopesRow.IsVisible || ResultsRegion.IsVisible) throw new InvalidOperationException("Empty search did not open compact.");
        if (IndexIndicator.IsVisible) throw new InvalidOperationException("Completed indexing still occupies the launcher.");
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
        await SearchView("/folder projects", Scope.Files, "launcher-folder-filter");
        if (Selected?.Kind != ItemKind.Folder || Selected.Title != "Projects" || ResultsHeading.Text != "/folder results")
            throw new InvalidOperationException("Slash folder filter did not select the independent folder.");
        await SearchView("/image", Scope.All, "launcher-image-filter");
        if (current?.Hits.Single().Candidate.Title != "Landing Page Mockup.png")
            throw new InvalidOperationException("Slash image filter did not constrain file types.");
        if (Results.Items.Cast<ResultRow>().Any(row => row.Candidate.Kind != ItemKind.File))
            throw new InvalidOperationException("Slash image search included a non-file fallback.");
        await SearchView("/folder absent", Scope.All, "launcher-folder-no-results");
        if (Results.Items.Count != 0 || Empty.Visibility != Visibility.Visible)
            throw new InvalidOperationException("An empty slash filter broadened into web search.");
        await SearchView("/", Scope.All, "launcher-slash-help");
        if (Results.Items.Count != 0 || !EmptyDetail.Text.Contains("/folder"))
            throw new InvalidOperationException("Slash filter help was not visible.");
        await SearchView("/pdf roadmap", Scope.All, "launcher-pdf-filter");
        if (Selected?.Title != "Product Roadmap.pdf") throw new InvalidOperationException("Slash PDF name search failed.");
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

        var settings = new SettingsWindow(store.Settings, _ => false, () => { },multimodalHost:multimodal,semantic:index.Semantic,index:index) { Owner = this };
        try
        {
            settings.Show();
            foreach (var name in new[] { "General", "Folders", "Local AI", "Multimodal", "Privacy" })
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
                if(name=="Multimodal")
                {
                    var progress=UiDiagnostics.Descendants<TextBlock>(settings).Single(text=>AutomationProperties.GetName(text)=="Multimodal indexing progress");
                    var retry=UiDiagnostics.Descendants<Button>(settings).Single(button=>button.Content as string=="Retry partial or failed content files");
                    retry.BringIntoView();settings.UpdateLayout();
                    await UiDiagnostics.Snapshot(settings,"settings-multimodal-progress");
                    if(string.IsNullOrWhiteSpace(progress.Text) || !UiDiagnostics.Descendants<Button>(settings).Any(button=>button.Content as string=="Retry partial or failed content files"))
                        throw new InvalidOperationException("Content progress or retry control is missing.");
                }
                if (AutomationProperties.GetItemStatus(navigation) != "Selected") throw new InvalidOperationException("Settings navigation lost its selected state.");
            }
            var foldersNav = UiDiagnostics.Descendants<Button>(settings).Single(button => AutomationProperties.GetName(button) == "Folders");
            foldersNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); settings.UpdateLayout();
            var viewer = UiDiagnostics.Descendants<ScrollViewer>(settings).First(v => v.Content is StackPanel);
            viewer.ScrollToTop(); settings.UpdateLayout();
            viewer.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            settings.UpdateLayout();
            if (Math.Abs(viewer.VerticalOffset - Math.Min(32, viewer.ScrollableHeight)) > 1) throw new InvalidOperationException("Settings wheel did not move 32 pixels.");
            await UiDiagnostics.Snapshot(settings, "settings-folders-scrolled");
            WindowVisuals.SetTestTheme(true); await UiDiagnostics.Snapshot(settings, "settings-folders-dark"); WindowVisuals.SetTestTheme(null);
            viewer.ScrollToTop(); settings.UpdateLayout();
            var view = UiDiagnostics.Descendants<FolderSelectionView>(settings).Single();
            var addBrowse = UiDiagnostics.Descendants<Button>(view).Single(b => AutomationProperties.GetName(b) == "Browse for a search folder");
            if (addBrowse.TranslatePoint(new System.Windows.Point(0, addBrowse.ActualHeight), viewer).Y > viewer.ActualHeight)
                throw new InvalidOperationException("Adding a folder requires scrolling past the folder list.");
            view.PathField.Text = App.DataDirectory;
            view.PathField.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(settings), Environment.TickCount, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            settings.UpdateLayout();
            if (!view.Selection.Items.Any(i => i.Path == App.DataDirectory && !i.Contents) || view.PathField.Text.Length != 0)
                throw new InvalidOperationException("Enter did not add the typed folder with content search off.");
            UiDiagnostics.Descendants<Button>(view).Single(b => AutomationProperties.GetName(b) == "Remove folder " + App.DataDirectory).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var advanced = UiDiagnostics.Descendants<Expander>(settings).Single(e => e.Header as string == "Exclusions and automation folders");
            advanced.IsExpanded = true; settings.UpdateLayout();
            var excluded = UiDiagnostics.Descendants<TextBox>(settings).Single(field => (AutomationProperties.GetLabeledBy(field) as TextBlock)?.Text == "Excluded folders");
            var savedExclusions = excluded.Text;
            excluded.Text = string.Join(Environment.NewLine, Enumerable.Repeat(App.DataDirectory, 100));
            excluded.BringIntoView(); settings.UpdateLayout();
            var nested = UiDiagnostics.Descendants<ScrollViewer>(excluded).First(v => v.ScrollableHeight > 0);
            nested.ScrollToTop(); settings.UpdateLayout(); var pageOffset = viewer.VerticalOffset;
            nested.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            settings.UpdateLayout();
            if (Math.Abs(viewer.VerticalOffset - pageOffset) > 1 || Math.Abs(nested.VerticalOffset - 32) > 1)
                throw new InvalidOperationException("Multiline wheel scrolling moved the page or skipped too far.");
            nested.ScrollToBottom(); viewer.ScrollToVerticalOffset(Math.Max(0, pageOffset - 64)); settings.UpdateLayout();
            pageOffset = viewer.VerticalOffset; var nestedEdge = $"{nested.VerticalOffset}/{nested.ScrollableHeight}";
            nested.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            settings.UpdateLayout();
            if (Math.Abs(viewer.VerticalOffset - Math.Min(viewer.ScrollableHeight, pageOffset + 32)) > 1)
                throw new InvalidOperationException($"Multiline edge did not hand scrolling back to the settings page: page {pageOffset} -> {viewer.VerticalOffset} (max {viewer.ScrollableHeight}), nested {nestedEdge} -> {nested.VerticalOffset}/{nested.ScrollableHeight}.");
            excluded.Text = savedExclusions; advanced.IsExpanded = false;
            viewer.ScrollToTop(); settings.UpdateLayout();
            var folders = UiDiagnostics.Descendants<FolderSelectionView>(settings).Single().PathField;
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
        var matchedItem=new Candidate(Candidate.Key(ItemKind.File,previewPath),ItemKind.File,"Matched note",previewPath);
        var match=new SemanticEvidence("authored",matchedItem.Id,new string('a',64),previewPath,"text",new("lines"){StartLine=1,EndLine=1},"Matched local evidence canary",[]){Partial=true};
        var enabled=true;
        var matched=new PreviewWindow(matchedItem,match,_=>enabled){Owner=this};
        try
        {
            matched.Show();await matched.CheckAsync();await UiDiagnostics.Snapshot(matched,"semantic-matched-lines");
            if(matched.MatchedLocator?.Kind!="lines")throw new InvalidOperationException("Matched line navigation was lost.");
            enabled=false;await Task.Delay(350);await UiDiagnostics.Snapshot(matched,"semantic-revoked-evidence");
            if(matched.MatchedLocator!=null || UiDiagnostics.Descendants<TextBox>(matched).Any(text=>text.Text.Contains("evidence canary",StringComparison.Ordinal)))
                throw new InvalidOperationException("Revoked preview retained private evidence.");
        }
        finally{matched.Close();}
        await CaptureSemanticFormatChecks();
        await CapturePdfNavigationChecks();
        await SearchView("preview benefits",Scope.Files,"semantic-before-arrival");
        var manualItem=new Candidate("ui:manual",ItemKind.File,"Benefits notes.txt",previewPath);
        var baseline=current! with {Hits=[new(manualItem,100)]};current=baseline;ApplyRows(baseline.Hits);
        manualSelection=true;Results.SelectedIndex=0;
        var fused=SemanticFusion.Apply(baseline,[new(matchedItem,.9,[match])]);current=fused;ApplyRows(fused.Hits);
        if(Selected?.Id!=manualItem.Id || !((ResultRow[])Results.ItemsSource).Any(row=>row.Candidate.Id==matchedItem.Id && row.Detail.StartsWith("Content match",StringComparison.Ordinal)))
            throw new InvalidOperationException("Late content results lost manual selection or evidence labels.");
        await UiDiagnostics.Snapshot(this,"semantic-results-retained-selection");
        try{ValidateSemanticAction(matchedItem);throw new InvalidOperationException("An out-of-scope content action was admitted.");}
        catch(IOException){}
        manualSelection=false;RefreshLocal();await WaitForSearch();
        await CaptureMediaPreviews();
        if (controlFailure != null) throw controlFailure;
    }
    private async Task CapturePdfNavigationChecks(int repetitions=1)
    {
        var scanPath=Path.GetFullPath("artifacts/diagnostics/multimodal/scan-fixtures-v1/f003.pdf");
        if(!File.Exists(scanPath))throw new IOException("The authored PDF navigation fixture is missing.");
        var runs=new List<object>();
        for(var iteration=0;iteration<repetitions;iteration++)
        {
            var clock=Stopwatch.StartNew();
            try{await Run();runs.Add(new{iteration,status="passed",elapsedMs=clock.ElapsedMilliseconds,error=(string?)null});}
            catch(Exception error){runs.Add(new{iteration,status="failed",elapsedMs=clock.ElapsedMilliseconds,error=error.ToString()});throw;}
            finally{File.WriteAllText(Path.Combine(App.DataDirectory,"pdf-checks.json"),System.Text.Json.JsonSerializer.Serialize(runs,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));}
        }
        async Task Run()
        {
            var scanItem=new Candidate(Candidate.Key(ItemKind.File,scanPath),ItemKind.File,"Authored scanned PDF",scanPath);
            var scanEvidence=new SemanticEvidence("authored-scan",scanItem.Id,new string('a',64),scanPath,"text",new("page"){Page=2},"RIVER DELTA PURCHASE Invoice total 73510",[]);
            var scanWindow=new PreviewWindow(scanItem,scanEvidence,_=>true){Owner=this};
            try
            {
                scanWindow.Show();await scanWindow.CheckAsync();
                if(scanWindow.RenderedPdfPage!=2)throw new InvalidOperationException("Scanned PDF did not navigate to its actual matched page.");
                await scanWindow.CheckPdfNavigationAsync();
                await UiDiagnostics.Snapshot(scanWindow,"semantic-scanned-pdf-page-two");
            }
            finally{scanWindow.Close();}
            using(var cancel=new CancellationTokenSource())
            {
                System.Diagnostics.Process? renderer=null;
                try
                {
                    try
                    {
                        _=await PdfPreviewRenderer.RenderAsync(scanPath,0,cancel.Token,pid=>{renderer=System.Diagnostics.Process.GetProcessById(pid);_=renderer.Handle;cancel.Cancel();});
                        throw new IOException("Cancelled PDF rendering unexpectedly succeeded.");
                    }
                    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
                    if(renderer==null || !renderer.HasExited)throw new IOException("Closing a PDF render left its worker alive.");
                }
                finally{renderer?.Dispose();}
            }
            var malformed=Path.Combine(App.DataDirectory,"Malformed preview.pdf");
            await File.WriteAllTextAsync(malformed,"Authored non-PDF input");
            var unavailable=new PreviewWindow(new("ui:bad-pdf",ItemKind.File,"Malformed preview.pdf",malformed)){Owner=this};
            try
            {
                unavailable.Show();
                try{await unavailable.CheckAsync();throw new IOException("Malformed PDF preview unexpectedly succeeded.");}
                catch(InvalidOperationException error)when(error.Message.StartsWith("PDF preview unavailable:",StringComparison.Ordinal)){}
            }
            finally{unavailable.Close();}
            var sessions=Path.Combine(App.DataDirectory,"pdf-previews","sessions");
            if(Directory.Exists(sessions) && Directory.EnumerateDirectories(sessions).Any())throw new IOException("PDF preview left temporary source/raster data behind.");
        }
    }
    private async Task CaptureMediaPreviews(string? extension=null)
    {
        var mediaPack=Path.GetFullPath("artifacts/diagnostics/multimodal/media-fixtures-v1");
        if(Directory.Exists(mediaPack))
        {
            var addon=Path.GetFullPath(Environment.GetEnvironmentVariable("SCOUT_UI_TEST_ADDON_ROOT")??"artifacts/diagnostics/multimodal/addon-no-office");
            var installation=MultimodalSetup.Find(addon)??throw new IOException("The authored media smoke needs its test add-on.");
            var pythonHome=Path.GetFullPath(Environment.GetEnvironmentVariable("SCOUT_UI_TEST_PYTHON_HOME")??".runtime/python/cpython-3.12.13-windows-x86_64-none");
            var mediaScope=new SemanticScope(true,[mediaPack],[],["audio","video"],installation.Revision,"preview-test");
            foreach(var name in new[]{"tone.wav","tone.mp3","tone.m4a","tone.flac","scene.mp4","scene.mov","scene.mkv","scene.webm"})
            {
                if(extension!=null && Path.GetExtension(name)!="."+extension)continue;
                var path=Path.Combine(mediaPack,name);var candidate=new Candidate(Candidate.Key(ItemKind.File,path),ItemKind.File,name,path);
                var source=SemanticSourceReader.Read(path,mediaScope)??throw new IOException("Missing authored media source.");
                var proof=new SemanticEvidence("authored-media",candidate.Id,source.Revision,path,SemanticFormats.Family(path)=="video"?"video":"audio",new("time"){StartSeconds=.5,EndSeconds=2.5},"Authored media window",[]);
                var window=new PreviewWindow(candidate,proof,e=>SemanticSourceReader.Read(e.Path,mediaScope)?.Revision==e.Revision,
                    (e,token)=>SemanticPlayback.PrepareAsync(addon,installation,e,()=>mediaScope,token,pythonHome)){Owner=this};
                try{window.Show();await window.CheckAsync();await window.CheckMediaAsync();await UiDiagnostics.Snapshot(window,"semantic-media-"+Path.GetExtension(name).TrimStart('.'));}
                finally{window.Close();}
            }
        }
    }
}

// Native dialog audits reuse app resources without starting the launcher or opening user data.
internal sealed class DialogDiagnosticApplication : App
{
    protected override void OnStartup(StartupEventArgs e) { }
}

internal static class UiDiagnostics
{
    internal static void AddSearchFolder(SettingsWindow window,string path,bool contents=false)
    {
        var view=Descendants<FolderSelectionView>(window).Single();
        view.PathField.Text=path;
        Descendants<Button>(view).Single(button=>button.Content as string=="Add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        if(!view.Selection.Items.Any(item=>item.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))throw new IOException("The actual Add folder handler did not add its fixture.");
        if(contents)Descendants<CheckBox>(view).Single(box=>AutomationProperties.GetName(box)=="Search contents in "+path).IsChecked=true;
    }
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
