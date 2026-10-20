using System.Diagnostics;
using System.Threading;
using System.Windows;
using Scout.Core;

namespace Scout.App;

public partial class App : Application
{
    private Mutex? instance;
    private System.Windows.Forms.NotifyIcon? tray;
    private IndexService? index;
    private ModelHost? model;
    private MultimodalHost? multimodal;
    internal static bool IsIsolatedDiagnostic { get; private set; }
    internal static string? PreviewSmokeExtension { get; private set; }
    internal static bool ControlsSmokeOnly { get; private set; }
    internal static bool PdfSmokeOnly { get; private set; }
    internal static string ShowEventName => IsIsolatedDiagnostic ? "Local\\Scout.Test.Show" : "Local\\Scout.Show";
    internal static string DataDirectory => Environment.GetEnvironmentVariable("SCOUT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout");
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args is ["--pdf-preview-worker"])
        {
            if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCOUT_DATA_DIR"))){Shutdown(1);return;}
            try{await PdfPreviewRenderer.RunWorkerAsync(DataDirectory);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {Shutdown(1);}
            return;
        }
        var previewSmoke = e.Args.Length==2 && e.Args[0]=="--media-preview-smoke-test" && new[]{"all","wav","mp3","m4a","flac","mp4","mov","mkv","webm"}.Contains(e.Args[1]);
        if(e.Args.Contains("--media-preview-smoke-test") && !previewSmoke){Shutdown(1);return;}
        PreviewSmokeExtension=previewSmoke?e.Args[1]:null;
        ControlsSmokeOnly = e.Args.Contains("--controls-smoke-test");
        PdfSmokeOnly = e.Args.Contains("--pdf-preview-smoke-test");
        var uiSmoke = PdfSmokeOnly || ControlsSmokeOnly || previewSmoke || e.Args.Contains("--ui-smoke-test");
        var folderPickerSmoke = e.Args.Contains("--folder-picker-smoke-test");
        var settingsE2e = e.Args.Length == 2 && e.Args[0] == "--settings-e2e-test" && e.Args[1] is "write" or "read";
        if(e.Args.Contains("--settings-e2e-test") && !settingsE2e){Shutdown(1);return;}
        var smoke = uiSmoke || folderPickerSmoke || e.Args.Contains("--smoke-test");
        var diagnose = e.Args.Contains("--diagnose");
        var realworld = e.Args.Contains("--realworld-diagnose");
        var bluetooth = e.Args.Contains("--bluetooth-diagnose");
        IsIsolatedDiagnostic = smoke || settingsE2e || diagnose || realworld || bluetooth;
        if ((uiSmoke || folderPickerSmoke || settingsE2e || realworld || bluetooth) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCOUT_DATA_DIR"))) { Shutdown(1); return; }
        instance = new Mutex(true, IsIsolatedDiagnostic ? "Local\\Scout.Test" : "Local\\Scout.Launcher", out var first);
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting(ShowEventName); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var store = new ScoutStore(Path.Combine(DataDirectory, "scout.db"));
            if (settingsE2e)
            {
                if (e.Args[1] == "write") SettingsPersistenceDiagnostics.Prepare(store);
                index = SettingsPersistenceDiagnostics.CreateIndex(store);
                model = new ModelHost(); multimodal = new MultimodalHost();
                index.EnableSemantic(multimodal,DataDirectory);
                var testMain = new MainWindow(store,index,model,multimodal); MainWindow = testMain;
                testMain.Show();
                await testMain.Dispatcher.InvokeAsync(() => { });
                try
                {
                    await SettingsPersistenceDiagnostics.Run(testMain,store,index,e.Args[1]);
                    File.WriteAllText(Path.Combine(DataDirectory,"settings-e2e-"+e.Args[1]+".txt"),"PASS settings UI, durable persistence, immediate reopen, index failure, validation, cancellation, ordinary search and restart verification.");
                }
                catch(Exception error)
                {
                    File.WriteAllText(Path.Combine(DataDirectory,"settings-e2e-"+e.Args[1]+".txt"),error.ToString());
                    Environment.ExitCode=1;
                }
                finally { SettingsPersistenceDiagnostics.ReleaseIndex(); Shutdown(); }
                return;
            }
            if (diagnose)
            {
                var testSettings = store.Settings;
                var watchRoot = Path.Combine(DataDirectory, "watcher-fixtures"); Directory.CreateDirectory(watchRoot);
                testSettings.ExtraFolders = testSettings.ExtraFolders.Append(watchRoot).Distinct().ToArray();
                testSettings.WindowsSearch = false; testSettings.AiEnabled = false; testSettings.Hotkey = ScoutSettings.DefaultHotkey;
                store.SaveSettings(testSettings);
            }
            if (smoke)
            {
                store.SaveSettings(new ScoutSettings { WindowsSearch = false });
                var now = DateTimeOffset.UtcNow;
                var demoDownloads = Path.Combine(Path.GetPathRoot(DataDirectory) ?? "C:\\", "Users", "Alex", "Downloads");
                var demoDocuments = Path.Combine(Path.GetDirectoryName(demoDownloads)!, "Documents");
                var fixtures = new[] { "Product Roadmap.pdf", "Research Notes.pdf", "Quarterly Review.pdf" }
                    .Select((name, i) => new Candidate("smoke:" + i, ItemKind.File, name, Path.Combine(demoDocuments, name))
                    {
                        Detail = "Documents", Source = "Files", AddedAt = now.AddMinutes(-i - 1),
                        ModifiedAt = now.AddMinutes(-i - 1), DownloadedAt = i == 0 ? now.AddMinutes(-1) : null,
                        DownloadSource = i == 0 ? "Browser download" : ""
                    })
                    .Concat(new[] { "Q4 Product Launch.pptx", "Campaign Budget.xlsx", "Landing Page Mockup.png" }
                        .Select((name, i) => new Candidate("smoke:download:" + i, ItemKind.File, name, Path.Combine(demoDownloads, name))
                        {
                            Detail = "Downloads", Source = "Files", AddedAt = now.AddMinutes(-i * 3 - 2),
                            ModifiedAt = now.AddMinutes(-i * 3 - 2)
                        }))
                    .Append(new Candidate("smoke:folder", ItemKind.Folder, "Projects", Path.Combine(DataDirectory, "Projects")) { Source = "Folder" })
                    .ToArray();
                store.ReplaceIndex(fixtures);
            }
            if (diagnose) Environment.SetEnvironmentVariable("SCOUT_INDEX_AUDIT", "1");
            index = new IndexService(store); model = new ModelHost();
            if (!smoke && !diagnose && !bluetooth) _ = model.WarmAsync(store.Settings);
            multimodal = new MultimodalHost();
            await multimodal.ConfigureAsync(store.Settings.Multimodal);
            index.EnableSemantic(multimodal,DataDirectory);
            var main = new MainWindow(store, index, model, multimodal); MainWindow = main;
            tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Scout — Ctrl+Space", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
                Visible = true, ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
            };
            tray.DoubleClick += (_, _) => main.Toggle(true);
            tray.ContextMenuStrip.Items.Add("Open Scout", null, (_, _) => main.Toggle(true));
            tray.ContextMenuStrip.Items.Add("Settings", null, (_, _) => main.ShowSettings());
            tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Shutdown());
            main.Show();
            var startupClock = Stopwatch.StartNew(); var startupGaps = new List<double>(); double startupTick = 0;
            var startupHeartbeat = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
            startupHeartbeat.Tick += (_,_) => { var elapsed = startupClock.Elapsed.TotalMilliseconds; startupGaps.Add(elapsed-startupTick); startupTick=elapsed; };
            if (diagnose) startupHeartbeat.Start();
            var indexReady = !smoke && !bluetooth
                ? ConfigureIndexAsync(index, store.Settings, diagnose || realworld ? null : main)
                : index.LoadFixturesAsync();
            if (diagnose || realworld || bluetooth)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop(); double? initialCrawlMs = null;
                    try
                    {
                        await indexReady; startupHeartbeat.Stop(); initialCrawlMs = startupClock.Elapsed.TotalMilliseconds;
                        var report = bluetooth ? await main.DiagnoseBluetoothAsync() : realworld ? await main.DiagnoseRealWorldAsync() : await main.DiagnoseAsync();
                        File.WriteAllText(Path.Combine(DataDirectory, "desktop-diagnostics.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch (Exception error) { File.WriteAllText(Path.Combine(DataDirectory, "desktop-diagnostics.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = error.ToString() })); Environment.ExitCode = 1; }
                    finally
                    {
                        startupHeartbeat.Stop();
                        if (diagnose) File.WriteAllText(Path.Combine(DataDirectory,"startup-responsiveness.json"),System.Text.Json.JsonSerializer.Serialize(new { initialCrawlMs, samples=startupGaps.Count, p95UiGapMs=startupGaps.Count==0?0:startupGaps.Order().ElementAt((int)(startupGaps.Count*.95)), worstUiGapMs=startupGaps.DefaultIfEmpty(0).Max() }));
                        Shutdown();
                    }
                }; timer.Start();
            }
            if (smoke)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        await indexReady;
                        if (folderPickerSmoke) await main.CheckFolderPickerAsync();
                        else await main.SmokeCheck(uiSmoke);
                        // Keep offscreen WPF captures separate from the WinRT PDF renderer.
                        if (uiSmoke) await UiDiagnostics.Snapshot(main, "launcher");
                        File.WriteAllText(Path.Combine(DataDirectory, "smoke-result.txt"), folderPickerSmoke
                            ? "Folder picker checks passed: actual native select and cancel, repeated settings browsing, duplicate prevention and recoverable picker failures."
                            : ControlsSmokeOnly ? "Control checks passed; see control-checks.json for interactions and inventory."
                            : PdfSmokeOnly ? "Repeated PDF navigation, worker cancellation, malformed input and cleanup checks passed; see pdf-checks.json."
                            : uiSmoke
                            ? "UI checks passed: rendering, scopes, manual selection, arrival selection, editable Open all, calculator, web fallback, settings navigation and validation focus, workspace naming, UI screenshots."
                            : "Desktop checks passed: rendering, scopes, manual selection, arrival selection, editable Open all, calculator, web fallback, PDF/image/Office/text previews, model host (when configured).");
                    }
                    catch (Exception error) { File.WriteAllText(Path.Combine(DataDirectory, "smoke-result.txt"), error.ToString()); Environment.ExitCode = 1; }
                    finally { Shutdown(); }
                }; timer.Start();
            }
            if (!smoke && !diagnose && !realworld && !bluetooth) await indexReady;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Scout could not start. " + ex.Message, "Scout", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    private static async Task ConfigureIndexAsync(IndexService index, ScoutSettings settings, Window? owner)
    {
        try { await index.ConfigureAsync(settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (owner == null) throw;
            MessageBox.Show(owner,
                "Scout couldn't update its content index. Check access to Scout's data folder, then restart Scout. Searching inside files will stay unavailable until this succeeds.",
                "Scout content search", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose(); index?.Dispose(); model?.Dispose(); multimodal?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
}
