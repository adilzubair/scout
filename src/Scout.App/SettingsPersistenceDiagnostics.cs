using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Scout.Core;

namespace Scout.App;

// Opt-in application integration test. A second OS process reads the same private
// profile without reseeding it, so this covers restart rather than just JSON.
internal static class SettingsPersistenceDiagnostics
{
    private static readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool block = true, fail;
    private static string Root => Path.Combine(App.DataDirectory,"settings-fixtures");
    private static string Folder(string name) => Path.Combine(Root,name);
    private static readonly string[] names = ["extra","content","excluded","automation","multimodal-one","multimodal-two","multimodal-pending","multimodal-final"];
    public static void Prepare(ScoutStore store)
    {
        if(store.Settings.ExtraFolders.Length>0 || File.Exists(Path.Combine(App.DataDirectory,"settings-e2e-write.txt")))throw new IOException("Use a new isolated settings test profile.");
        foreach(var name in names)Directory.CreateDirectory(Folder(name));
        File.WriteAllText(Path.Combine(Folder("content"),"unrelated.txt"),"settingspersistencecanary inside an ordinary document");
        File.WriteAllText(Path.Combine(Folder("extra"),"Saved folder note.txt"),"ordinary filename search fixture");
        store.SaveSettings(new(){WindowsSearch=false,RecentItems=false,Hotkey="Ctrl+Alt+Shift+F11"});
    }
    public static IndexService CreateIndex(ScoutStore store)
    {
        block=!File.Exists(Path.Combine(App.DataDirectory,"settings-e2e-write.txt"));
        var reader=new ContentIndexer(store.ContentIndex,async (path,token)=>
        {
            if(block){entered.TrySetResult();await release.Task.WaitAsync(token);}
            return await File.ReadAllTextAsync(path,token);
        },token=>
        {
            if(fail)throw new IOException("Injected content-index failure after saving settings");
            return Task.Run(store.ContentIndex.Clear,token);
        });
        return new(store,reader,()=>[],()=>[]);
    }
    public static void ReleaseIndex(){block=false;release.TrySetResult();}
    private static Button Button(SettingsWindow window,string text) => UiDiagnostics.Descendants<Button>(window).Single(button=>button.Content as string==text || AutomationProperties.GetName(button)==text);
    private static TextBox Field(SettingsWindow window,string text) => UiDiagnostics.Descendants<TextBox>(window).Single(box=>(AutomationProperties.GetLabeledBy(box) as TextBlock)?.Text==text);
    private static void Check(bool value,string message){if(!value)throw new IOException(message);}
    private static void AssertFolders(ScoutSettings settings,string[] expected)
    {
        Check(settings.ExtraFolders.SequenceEqual([Folder("extra")]),"Additional folders did not persist.");
        Check(settings.ContentSearchEnabled && settings.ContentFolders.SequenceEqual([Folder("content")]),"Keyword content folders did not persist.");
        Check(settings.Exclusions.SequenceEqual([Folder("excluded")]),"Excluded folders did not persist.");
        Check(settings.AutomationFolders.SequenceEqual([Folder("automation")]),"Automation folders did not persist.");
        Check(settings.Multimodal.Folders.SequenceEqual(expected),"Multimodal folders did not persist before indexing completed.");
        Check(settings.Multimodal.Modalities.SequenceEqual(MultimodalSettings.ContentTypes),"The saved multimodal content families changed.");
    }
    // Drive the real modal Settings and its actual Save/Cancel buttons on the
    // application's dispatcher. No external desktop input or personal profile.
    private static Task Dialog(MainWindow main,Action<SettingsWindow> interact)
    {
        Exception? error=null;var attempted=false;var ticks=0;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(75)};
        timer.Tick+=(_,_)=>
        {
            var windows=Application.Current.Windows.OfType<SettingsWindow>().Where(window=>window.IsVisible).ToArray();
            if(windows.Length!=1){if(++ticks>100)error=new IOException("Settings did not open.");return;}
            var window=windows[0];
            if(attempted){error??=new IOException("Settings validation unexpectedly rejected Save.");window.Close();timer.Stop();return;}
            attempted=true;
            try{interact(window);}
            catch(Exception failure){error=failure;window.Close();}
        };
        timer.Start();
        Task operation;
        try{operation=main.ShowSettingsAsync();}
        finally{timer.Stop();}
        if(error!=null)throw new IOException("Settings UI action failed.",error);
        Check(attempted,"Settings interaction was skipped.");
        return operation;
    }
    private static Task Inspect(MainWindow main,string[] folders,bool invalid=false) => Dialog(main,window=>
    {
        Button(window,"Multimodal").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.UpdateLayout();
        var multimodal=Field(window,"Multimodal content folders");
        Check(multimodal.Text==string.Join(Environment.NewLine,folders),"Reopening Settings lost the saved multimodal folders.");
        if(invalid)
        {
            Button(window,"Folders").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));window.UpdateLayout();
            Field(window,"Additional folders").Text="not an existing absolute path";
            Button(window,"Save settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(window.IsVisible,"Invalid folder validation closed Settings.");
            Check(UiDiagnostics.Descendants<TextBlock>(window).Any(text=>text.IsVisible && text.Text.StartsWith("Use existing, full folder paths",StringComparison.Ordinal)),"Invalid folder validation was not visible.");
        }
        multimodal.Text=Folder("discarded-draft");
        Button(window,"Cancel").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    });
    private static void CheckStorageFailure(MainWindow main,ScoutStore store,string[] saved)
    {
        Exception? error=null;
        var window=new SettingsWindow(store.Settings,_=>true,()=>{},persistSettings:_=>throw new IOException("Injected settings write failure")){Owner=main};
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(75)};
        timer.Tick+=(_,_)=>
        {
            timer.Stop();
            try
            {
                Button(window,"Multimodal").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));window.UpdateLayout();
                var field=Field(window,"Multimodal content folders");var draft=Folder("multimodal-one");field.Text=draft;
                Button(window,"Save settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(window.IsVisible && field.Text==draft,"Storage failure closed Settings or discarded the user's edits.");
                Check(UiDiagnostics.Descendants<TextBlock>(window).Any(text=>text.IsVisible && text.Text.StartsWith("Settings couldn't be saved.",StringComparison.Ordinal)),"Storage failure did not show retry guidance.");
                AssertFolders(new ScoutStore(Path.Combine(App.DataDirectory,"scout.db")).Settings,saved);
                Button(window,"Cancel").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }
            catch(Exception failure){error=failure;window.Close();}
        };
        timer.Start();
        try{_ = window.ShowDialog();}
        finally{timer.Stop();}
        if(error!=null)throw new IOException("Settings storage-failure UI check failed.",error);
    }
    public static async Task Run(MainWindow main,ScoutStore store,IndexService index,string phase)
    {
        string[] original=[Folder("multimodal-one"),Folder("multimodal-two")];
        string[] final=[..original,Folder("multimodal-final")];
        var database=Path.Combine(App.DataDirectory,"scout.db");
        if(phase=="write")
        {
            var secondApply=Task.CompletedTask;
            var apply=Dialog(main,window=>
            {
                Button(window,"Folders").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));window.UpdateLayout();
                Field(window,"Additional folders").Text=Folder("extra");Field(window,"Content search folders").Text=Folder("content");
                Field(window,"Excluded folders").Text=Folder("excluded");Field(window,"Automation folders").Text=Folder("automation");
                UiDiagnostics.Descendants<CheckBox>(window).Single(box=>AutomationProperties.GetName(box)=="Search inside files in selected folders").IsChecked=true;
                Button(window,"Multimodal").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));window.UpdateLayout();
                Field(window,"Multimodal content folders").Text=string.Join(Environment.NewLine,original);
                Button(window,"Save settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(!apply.IsCompleted,"The slow-index regression did not hold indexing pending.");
                AssertFolders(store.Settings,original);AssertFolders(new ScoutStore(database).Settings,original);
                await Inspect(main,original);
                AssertFolders(new ScoutStore(database).Settings,original);
                string[] pending=[..original,Folder("multimodal-pending")];
                secondApply=Dialog(main,window=>
                {
                    Button(window,"Multimodal").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));window.UpdateLayout();
                    Field(window,"Multimodal content folders").Text=string.Join(Environment.NewLine,pending);
                    Button(window,"Save settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                });
                AssertFolders(store.Settings,pending);AssertFolders(new ScoutStore(database).Settings,pending);
                await Inspect(main,pending);
            }
            finally{ReleaseIndex();}
            await Task.WhenAll(apply,secondApply).WaitAsync(TimeSpan.FromSeconds(20));
            AssertFolders(new ScoutStore(database).Settings,[..original,Folder("multimodal-pending")]);
            fail=true;
            try
            {
                await Dialog(main,window=>
                {
                    Button(window,"Multimodal").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));window.UpdateLayout();
                    Field(window,"Multimodal content folders").Text=string.Join(Environment.NewLine,final);
                    Button(window,"Save settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                });
                AssertFolders(store.Settings,final);AssertFolders(new ScoutStore(database).Settings,final);
                await Inspect(main,final,invalid:true);
                CheckStorageFailure(main,store,final);
                AssertFolders(new ScoutStore(database).Settings,final);
            }
            finally{fail=false;}
        }
        else{AssertFolders(store.Settings,final);await Inspect(main,final);}
        await index.ConfigureAsync(store.Settings);
        var matches=index.SearchContent("settingspersistencecanary",Scope.Files,DateTimeOffset.Now,CancellationToken.None);
        Check(matches.Length==1,"Saved content folders did not reach the real keyword content index.");
        var result=SearchEngine.Search("Saved folder note",Scope.Files,index.Catalog,store.Library,DateTimeOffset.Now);
        Check(result.Hits.Any(hit=>hit.Candidate.Title=="Saved folder note.txt"),"Saved additional folders did not reach ordinary search.");
        AssertFolders(new ScoutStore(database).Settings,final);
    }
}
