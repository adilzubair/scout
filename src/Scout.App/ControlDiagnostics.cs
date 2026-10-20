using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Scout.Core;

namespace Scout.App;

public partial class MainWindow
{
    // Invoked only by isolated --ui-smoke-test / --controls-smoke-test flows.
    // Uses production WPF handlers; fixture callbacks replace folder selection
    // and persistence only in detached settings dialogs. Native picker and
    // durable Save/restart are exercised separately by test-app-e2e.ps1.
    private async Task CaptureControlChecks()
    {
        if(!App.IsIsolatedDiagnostic || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCOUT_DATA_DIR")))
            throw new InvalidOperationException("Control checks require an isolated diagnostic profile.");
        var checks=new List<object>();var failed=0;
        var inventory=new List<object>();
        void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        async Task Check(string name,Func<Task> body)
        {
            var clock=Stopwatch.StartNew();
            try{await body();checks.Add(new{name,status="passed",elapsedMs=clock.ElapsedMilliseconds,error=(string?)null});}
            catch(Exception error){failed++;checks.Add(new{name,status="failed",elapsedMs=clock.ElapsedMilliseconds,error=error.ToString()});}
            File.WriteAllText(Path.Combine(App.DataDirectory,"control-checks.json"),JsonSerializer.Serialize(new{schemaVersion=1,failed,checks,inventory},new JsonSerializerOptions{WriteIndented=true}));
        }
        static string Label(FrameworkElement element)
        {
            var name=AutomationProperties.GetName(element);
            if(name.Length>0)return name;
            if(AutomationProperties.GetLabeledBy(element) is TextBlock label)return label.Text;
            return element is ContentControl control?control.Content?.ToString()??element.Name:element.Name;
        }
        static void Click(Button button)
        {
            if(!button.IsEnabled)throw new InvalidOperationException("Disabled button: "+Label(button));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window.GetWindow(button)?.UpdateLayout();
        }
        static Button Find(Window window,string label)=>UiDiagnostics.Descendants<Button>(window).Single(button=>Label(button)==label);
        static TextBox Field(Window window,string label)=>UiDiagnostics.Descendants<TextBox>(window).Single(box=>Label(box)==label);
        void Inventory(Window window,string page)
        {
            foreach(var control in UiDiagnostics.Descendants<Control>(window).Where(control=>control is Button or CheckBox or ComboBox or TextBox))
                inventory.Add(new{page,type=control.GetType().Name,label=Label(control),enabled=control.IsEnabled,visible=control.IsVisible});
        }
        void Modal<T>(Action trigger,Action<T> interact)where T:Window
        {
            Exception? failure=null;var attempted=false;var ticks=0;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
            timer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.OfType<T>().SingleOrDefault(window=>window.IsVisible);
                if(dialog==null){if(++ticks>100){failure=new IOException("Modal did not open.");timer.Stop();}return;}
                if(attempted){failure??=new IOException("Modal did not close after its action.");dialog.Close();return;}
                attempted=true;
                try{interact(dialog);}catch(Exception error){failure=error;dialog.Close();}
            };
            timer.Start();try{trigger();}finally{timer.Stop();}
            if(failure!=null)throw new IOException("Modal interaction failed.",failure);
            Require(attempted,"Modal interaction did not run.");
        }
        await Check("launcher: five scope buttons",async()=>
        {
            Query.Text="pdf";await WaitForSearch();
            foreach(var button in Scopes.Children.OfType<Button>())
            {Click(button);await WaitForSearch();Require((int)scope==int.Parse((string)button.Tag),"Wrong scope after click.");}
            scope=Scope.All;UpdateScopes();RefreshLocal();await WaitForSearch();Inventory(this,"launcher");
        });
        await Check("launcher: split preview, file readers and cancellation", CaptureInlinePreviewChecks);
        await Check("launcher: example buttons and clear search",async()=>
        {
            foreach(var button in UiDiagnostics.Descendants<Button>(this).Where(button=>button.Tag is string text && !int.TryParse(text,out _)).ToArray())
            {Click(button);await WaitForSearch();Require(Query.Text==(string)button.Tag,"Example did not populate search.");}
            Query.Text="pdf";await WaitForSearch();Click(ClearQuery);await WaitForSearch();Require(Query.Text.Length==0 && !expanded,"Clear did not collapse search.");
        });
        await Check("group: review edits actual selected members",async()=>
        {
            ClearGroup();scope=Scope.All;Query.Text="pdfs";await WaitForSearch();
            var members=current!.Hits.Select(hit=>hit.Candidate).Where(item=>item.IsGroupable).Take(3).ToArray();
            Require(members.Length>=2,"Missing group fixtures.");
            foreach(var item in members)group[item.Id]=item;groupEdited=true;UpdateGroup();
            Modal<GroupWindow>(()=>Click(Find(this,"Review group")),dialog=>
            {
                Inventory(dialog,"review group");var boxes=UiDiagnostics.Descendants<CheckBox>(dialog).ToArray();
                ((IToggleProvider)new CheckBoxAutomationPeer(boxes[0]).GetPattern(PatternInterface.Toggle)).Toggle();
                Require(dialog.Selected.Length==members.Length-1,"Membership toggle did not update selection.");
                Click(Find(dialog,"Keep selected items"));
            });
            Require(group.Count==members.Length-1,"Group edits did not return to launcher.");ClearGroup();
        });
        await Check("workspace: empty-name guard, Save, recall and Cancel",async()=>
        {
            ClearGroup();Query.Text="pdfs";await WaitForSearch();
            foreach(var item in current!.Hits.Select(hit=>hit.Candidate).Where(item=>item.IsGroupable).Take(2))group[item.Id]=item;
            groupEdited=true;UpdateGroup();Require(group.Count==2,"Missing workspace fixtures.");
            Modal<NameWindow>(()=>Click(Find(this,"Save workspace")),dialog=>
            {
                Inventory(dialog,"workspace name");var save=Find(dialog,"Save workspace");Require(!save.IsEnabled,"Empty name admitted.");
                UiDiagnostics.Descendants<TextBox>(dialog).Single().Text="E2E authored workspace";Require(save.IsEnabled,"Valid name rejected.");Click(save);
            });
            Require(store.Workspaces.Any(workspace=>workspace.Name=="E2E authored workspace"),"Workspace not saved.");
            scope=Scope.Workspaces;UpdateScopes();Query.Text="E2E authored workspace";await WaitForSearch();
            Require(current!.Hits.Any(hit=>hit.Candidate.Kind==ItemKind.Workspace),"Saved workspace not searchable.");
            Modal<NameWindow>(()=>new NameWindow("Cancelled"){Owner=this}.ShowDialog(),dialog=>Click(Find(dialog,"Cancel")));
            Require(!store.Workspaces.Any(workspace=>workspace.Name=="Cancelled"),"Cancel saved a workspace.");
        });
        await Check("actions: workspace review, edit cancellation and deletion",async()=>
        {
            scope=Scope.Workspaces;UpdateScopes();Query.Text="E2E authored workspace";await WaitForSearch();
            var menu=ShowActions()??throw new IOException("Workspace actions did not open.");
            var edit=menu.Items.OfType<MenuItem>().Single(item=>item.Header as string=="Review and edit workspace");
            Modal<GroupWindow>(()=>edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)),dialog=>dialog.Close());menu.IsOpen=false;
            Require(editingWorkspace!=null && group.Count==2,"Workspace edit did not retain members.");ClearGroup();
            RefreshLocal();await WaitForSearch();
            menu=ShowActions()??throw new IOException("Workspace delete action did not open.");
            menu.Items.OfType<MenuItem>().Single(item=>item.Header as string=="Delete workspace").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;
            Require(!store.Workspaces.Any(workspace=>workspace.Name=="E2E authored workspace"),"Delete left the authored workspace behind.");
        });
        await Check("actions: pin/unpin, group membership and stale menu rejection",async()=>
        {
            ClearGroup();scope=Scope.All;UpdateScopes();Query.Text="Product Roadmap.pdf";await WaitForSearch();
            var item=Selected??throw new IOException("Missing file action fixture.");
            void Action(string label)
            {
                var menu=ShowActions()??throw new IOException("File actions did not open.");
                try{menu.Items.OfType<MenuItem>().Single(entry=>entry.Header as string==label).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}
                finally{menu.IsOpen=false;}
            }
            var pinned=store.Library.GetValueOrDefault(item.Id)?.Pinned==true;
            Action("Pin / unpin");await WaitForSearch();Require((store.Library.GetValueOrDefault(item.Id)?.Pinned==true)!=pinned,"Pin action did not persist.");
            Action("Pin / unpin");await WaitForSearch();Require((store.Library.GetValueOrDefault(item.Id)?.Pinned==true)==pinned,"Unpin action did not restore state.");
            Action("Include / exclude from group");Require(group.ContainsKey(item.Id),"Include did not add member.");
            Action("Include / exclude from group");Require(!group.ContainsKey(item.Id),"Exclude did not remove member.");
            var stale=ShowActions()??throw new IOException("Missing stale menu.");Query.Text="Quarterly Review.pdf";await WaitForSearch();
            stale.Items.OfType<MenuItem>().Single(entry=>entry.Header as string=="Pin / unpin").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));stale.IsOpen=false;
            Require(Status.Text.Contains("Results changed",StringComparison.Ordinal),"Stale action did not explain rejection.");ClearGroup();
        });
        await Check("actions: unavailable Open group reports failure",async()=>
        {
            ClearGroup();scope=Scope.All;UpdateScopes();Query.Text="pdfs";await WaitForSearch();
            var members=current!.Hits.Select(hit=>hit.Candidate).Where(item=>item.IsGroupable && !File.Exists(item.Target)).Take(2).ToArray();
            Require(members.Length==2,"Unavailable group fixtures missing.");foreach(var item in members)group[item.Id]=item;groupEdited=true;UpdateGroup();
            Click(Find(this,"Open group"));
            var clock=Stopwatch.StartNew();while(executing && clock.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(30);
            Require(!executing && Status.Text.StartsWith("Opened 0; unavailable:",StringComparison.Ordinal),"Unavailable group dispatch was not reported.");ClearGroup();
        });
        await Check("actions: copy calculator result and file path",async()=>
        {
            // Retain the existing clipboard object in memory; never log its data.
            // Another desktop process can briefly own the clipboard. Retry only
            // that specific transport failure, without retrying Scout's actions.
            async Task<T> ClipboardAccess<T>(Func<T> operation)
            {
                var clock=Stopwatch.StartNew();
                while(true)
                {
                    try{return operation();}
                    catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x800401D0) && clock.Elapsed<TimeSpan.FromSeconds(3))
                    {await Task.Delay(100);}
                }
            }
            var original=await ClipboardAccess(Clipboard.GetDataObject);
            try
            {
                ClearGroup();scope=Scope.All;UpdateScopes();Query.Text="15% of 240";await WaitForSearch();
                await OpenSelected();Require(await ClipboardAccess(Clipboard.GetText)=="36","Enter did not copy the calculator result.");
                Query.Text="Product Roadmap.pdf";await WaitForSearch();var item=Selected??throw new IOException("Missing copy fixture.");
                var menu=ShowActions()??throw new IOException("Copy actions did not open.");
                try{menu.Items.OfType<MenuItem>().Single(entry=>entry.Header as string=="Copy path or URL").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}
                finally{menu.IsOpen=false;}
                Require(await ClipboardAccess(Clipboard.GetText)==item.Target,"Copy path did not use the selected result.");
            }
            finally{await ClipboardAccess(()=>{if(original!=null)Clipboard.SetDataObject(original,true);else Clipboard.Clear();return true;});}
        });
        var fixture=Path.Combine(App.DataDirectory,"control-fixtures");Directory.CreateDirectory(fixture);
        foreach(var page in new[]{"General","Folders","Local AI","Multimodal","Privacy"})
        {
            await Check("settings: "+page+" controls",async()=>
            {
                var selected=0;var cleared=false;var options=new ScoutSettings{WindowsSearch=false,RecentItems=false};
                var dialog=new SettingsWindow(options,_=>true,()=>cleared=true,pickFolder:_=>{selected++;return fixture;}){Owner=this};
                try
                {
                    dialog.Show();Click(Find(dialog,page));dialog.UpdateLayout();
                    CheckBox? wholeDisk=null;
                    if(page=="Folders")
                    {
                        foreach(var advanced in UiDiagnostics.Descendants<Expander>(dialog).ToArray())advanced.IsExpanded=true;
                        wholeDisk=UiDiagnostics.Descendants<CheckBox>(dialog).Single(box=>Label(box)=="Index file names on selected drives");wholeDisk.IsChecked=true;
                        dialog.UpdateLayout();
                    }
                    Inventory(dialog,page);
                    foreach(var box in UiDiagnostics.Descendants<CheckBox>(dialog).ToArray())
                    {
                        var before=box.IsChecked;var provider=(IToggleProvider)new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle);
                        provider.Toggle();Require(box.IsChecked!=before,"Checkbox did not toggle: "+Label(box));provider.Toggle();Require(box.IsChecked==before,"Checkbox did not restore: "+Label(box));
                    }
                    foreach(var combo in UiDiagnostics.Descendants<ComboBox>(dialog).ToArray())
                    {
                        var before=combo.SelectedIndex;
                        for(var i=0;i<combo.Items.Count;i++){combo.SelectedIndex=i;Require(combo.SelectedIndex==i,"Combo selection failed: "+Label(combo));}
                        combo.SelectedIndex=before;
                    }
                    if(page=="Folders")
                    {
                        wholeDisk!.IsChecked=false;
                        UiDiagnostics.AddSearchFolder(dialog,fixture,contents:true);UiDiagnostics.AddSearchFolder(dialog,fixture);
                        var folders=UiDiagnostics.Descendants<FolderSelectionView>(dialog).Single();
                        Require(folders.Selection.Items.Count(item=>item.Path==fixture)==1 && folders.Selection.ContentFolders.Contains(fixture),"Add/deduplication/content opt-in did not preserve the selected folder.");
                        Click(UiDiagnostics.Descendants<Button>(folders).Single(button=>AutomationProperties.GetName(button)=="Remove folder "+fixture));
                        Require(!folders.Selection.Items.Any(item=>item.Path==fixture),"The actual Remove folder handler did not remove its fixture.");
                    }
                    foreach(var browse in UiDiagnostics.Descendants<Button>(dialog).Where(button=>AutomationProperties.GetName(button).StartsWith("Browse for ",StringComparison.Ordinal)).ToArray())
                    {
                        Click(browse);Click(browse);
                    }
                    if(page is "Folders" or "Multimodal")Require(selected>0,"Browse callback was not reached.");
                    if(page=="Privacy"){Click(Find(dialog,"Clear launch history"));Require(cleared && Find(dialog,"Launch history cleared")!=null,"Clear history did not acknowledge completion.");}
                    if(page=="Multimodal")
                    {
                        Require(!Find(dialog,"Cancel setup").IsEnabled,"Cancel enabled without setup.");
                        Require(!Find(dialog,"Free unused add-on storage").IsEnabled,"Cleanup enabled without installation.");
                    }
                    await UiDiagnostics.Snapshot(dialog,"e2e-controls-"+page.ToLowerInvariant().Replace(' ','-'));
                    if (page == "General")
                    {
                        foreach (var (dark, contrast, name) in new[] { (false, false, "settings-unified-light"), (true, false, "settings-unified-dark"), (false, true, "settings-unified-contrast") })
                        {
                            WindowVisuals.SetTestTheme(dark, contrast);
                            await UiDiagnostics.Snapshot(dialog, name);
                        }
                        WindowVisuals.SetTestTheme(null);
                        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; dialog.UpdateLayout();
                        var saveButton = Find(dialog, "Save settings");
                        var field = Field(dialog, "Open Scout");
                        var saveEdge = saveButton.TranslatePoint(new System.Windows.Point(saveButton.ActualWidth, saveButton.ActualHeight), dialog);
                        var fieldStart = field.TranslatePoint(new System.Windows.Point(), dialog);
                        var fieldEnd = field.TranslatePoint(new System.Windows.Point(field.ActualWidth, 0), dialog);
                        Require(saveEdge.X < dialog.ActualWidth && saveEdge.Y < dialog.ActualHeight && fieldStart.X > 170 && fieldEnd.X < dialog.ActualWidth,
                            "Settings fields or Save were clipped at the minimum window size.");
                        await UiDiagnostics.Snapshot(dialog, "settings-unified-small");
                    }
                }finally{dialog.Close();}
            });
        }
        foreach(var label in new[]{"Folder path","Excluded folders","Automation folders"})
        {
            await Check("settings validation: "+label,()=>
            {
                Modal<SettingsWindow>(()=>new SettingsWindow(new(){ContentSearchEnabled=true},_=>true,()=>{}){Owner=this}.ShowDialog(),dialog=>
                {
                    Click(Find(dialog,"Folders"));dialog.UpdateLayout();
                    foreach(var advanced in UiDiagnostics.Descendants<Expander>(dialog).ToArray())advanced.IsExpanded=true;
                    dialog.UpdateLayout();Field(dialog,label).Text="relative\\invalid";Click(Find(dialog,"Save settings"));
                    Require(dialog.IsVisible && Field(dialog,label).IsKeyboardFocusWithin,"Invalid folder did not retain dialog and focus field.");Click(Find(dialog,"Cancel"));
                });return Task.CompletedTask;
            });
        }
        await Check("settings validation: Add folder and empty drive selection",()=>
        {
            var applied=0;
            Modal<SettingsWindow>(()=>new SettingsWindow(new(),_=>{applied++;return true;},()=>{}){Owner=this}.ShowDialog(),dialog=>
            {
                Click(Find(dialog,"Folders"));dialog.UpdateLayout();var folders=UiDiagnostics.Descendants<FolderSelectionView>(dialog).Single();
                folders.PathField.Text="relative\\invalid";Click(UiDiagnostics.Descendants<Button>(folders).Single(button=>button.Content as string=="Add"));
                Require(folders.PathField.IsKeyboardFocusWithin && !folders.Selection.Items.Any(item=>item.Path=="relative\\invalid"),"Invalid Add did not retain focus and reject its path.");
                folders.PathField.Clear();
                UiDiagnostics.Descendants<CheckBox>(dialog).Single(box=>Label(box)=="Index file names on selected drives").IsChecked=true;
                Click(Find(dialog,"Save settings"));
                Require(dialog.IsVisible && applied==0,"Invalid drive selection changed the active hotkey or closed settings.");Click(Find(dialog,"Cancel"));
            });return Task.CompletedTask;
        });
        await Check("settings: all preferences survive Save",()=>
        {
            ScoutSettings? saved=null;
            Modal<SettingsWindow>(()=>new SettingsWindow(new(){WindowsSearch=false,RecentItems=false},_=>true,()=>{},persistSettings:value=>saved=value){Owner=this}.ShowDialog(),dialog=>
            {
                Click(Find(dialog,"General"));Field(dialog,"Open Scout").Text="Ctrl+Alt+Shift+F10";
                Click(Find(dialog,"Multimodal"));
                var combo=UiDiagnostics.Descendants<ComboBox>(dialog).Single(box=>Label(box)=="Multimodal CPU threads");combo.SelectedItem=2;
                Field(dialog,"OCR languages").Text="en-US";Click(Find(dialog,"Save settings"));
            });
            Require(saved is {Hotkey:"Ctrl+Alt+Shift+F10"} && saved.Multimodal.CpuThreads==2 && saved.Multimodal.OcrLanguages.SequenceEqual(["en-us"]),"Saved preferences differ from selected controls.");return Task.CompletedTask;
        });
        await Check("settings: retry, coverage and close buttons",async()=>
        {
            Require(index.Semantic!=null,"The production content scheduler is missing.");
            var dialog=new SettingsWindow(new(),_=>true,()=>{},semantic:index.Semantic){Owner=this};
            try
            {
                dialog.Show();Click(Find(dialog,"Multimodal"));var retry=Find(dialog,"Retry partial or failed content files");Click(retry);
                var clock=Stopwatch.StartNew();while(!retry.IsEnabled && clock.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(30);
                Require(retry.IsEnabled,"Retry left its control disabled.");
                Modal<SemanticCoverageWindow>(()=>Click(Find(dialog,"Review content coverage")),coverage=>
                {
                    Inventory(coverage,"content coverage");
                    Click(UiDiagnostics.Descendants<Button>(coverage).Single(button=>button.Content as string=="Close"));
                });
                Click(Find(dialog,"Close"));Require(!dialog.IsVisible,"Settings header Close left the dialog open.");
            }finally{dialog.Close();}
        });
        await Check("tray: Open Scout and Settings actions",()=>
        {
            var field=typeof(App).GetField("tray",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            var tray=field?.GetValue(Application.Current) as System.Windows.Forms.NotifyIcon??throw new IOException("The real notification icon is missing.");
            var items=tray.ContextMenuStrip?.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray()??[];
            HidePanel();items.Single(item=>item.Text=="Open Scout").PerformClick();Require(IsVisible,"Tray Open Scout did not show the launcher.");
            Modal<SettingsWindow>(()=>items.Single(item=>item.Text=="Settings").PerformClick(),dialog=>Click(Find(dialog,"Cancel")));
            Require(items.Any(item=>item.Text=="Quit"),"Tray Quit is missing.");return Task.CompletedTask;
        });
        await Check("add-on buttons: missing-install test, cancel setup and remove",async()=>
        {
            Require(!Directory.Exists(MultimodalSetup.Root),"Use a fresh profile for add-on button checks.");
            var dialog=new SettingsWindow(new(),_=>true,()=>{}){Owner=this};
            try
            {
                dialog.Show();Click(Find(dialog,"Multimodal"));
                async Task Settled()
                {
                    var clock=Stopwatch.StartNew();while(!Find(dialog,"Save settings").IsEnabled && clock.Elapsed<TimeSpan.FromSeconds(20))await Task.Delay(50);
                    Require(Find(dialog,"Save settings").IsEnabled,"Add-on operation left settings busy.");
                }
                Click(Find(dialog,"Test add-on"));await Settled();
                Require(UiDiagnostics.Descendants<TextBlock>(dialog).Any(text=>text.Text.StartsWith("Add-on test failed.",StringComparison.Ordinal)),"Missing install did not produce actionable test failure.");
                Click(Find(dialog,"Install or verify add-on"));
                Require(Find(dialog,"Cancel setup").IsEnabled && !Find(dialog,"Save settings").IsEnabled,"Setup did not lock conflicting controls.");
                Click(Find(dialog,"Cancel setup"));await Settled();
                Require(UiDiagnostics.Descendants<TextBlock>(dialog).Any(text=>text.Text.StartsWith("Setup cancelled.",StringComparison.Ordinal)),"Cancellation was not acknowledged.");
                Click(Find(dialog,"Remove add-on"));await Settled();
                Require(!Directory.Exists(MultimodalSetup.Root),"Remove left the cancelled private add-on behind.");
                Require(UiDiagnostics.Descendants<TextBlock>(dialog).Any(text=>text.Text.StartsWith("Add-on removed.",StringComparison.Ordinal)),"Removal did not acknowledge completion.");
            }finally{dialog.Close();}
        });
        await Check("launcher: dismiss button and reopen",()=>
        {
            Click(Find(this,"Dismiss Scout"));Require(!IsVisible,"Dismiss left panel visible.");Toggle(true);Require(IsVisible,"Reopen failed.");return Task.CompletedTask;
        });
        ClearGroup();scope=Scope.All;UpdateScopes();Query.Text="pdf";await WaitForSearch();
        if(failed>0)throw new IOException($"{failed} control checks failed; see control-checks.json.");
    }
}
