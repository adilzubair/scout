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
    internal static string DataDirectory => Environment.GetEnvironmentVariable("SCOUT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout");
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var uiSmoke = e.Args.Contains("--ui-smoke-test");
        var smoke = uiSmoke || e.Args.Contains("--smoke-test");
        var diagnose = e.Args.Contains("--diagnose");
        var realworld = e.Args.Contains("--realworld-diagnose");
        var bluetooth = e.Args.Contains("--bluetooth-diagnose");
        if ((uiSmoke || realworld || bluetooth) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCOUT_DATA_DIR"))) { Shutdown(1); return; }
        instance = new Mutex(true, smoke || diagnose || realworld || bluetooth ? "Local\\Scout.Test" : "Local\\Scout.Launcher", out var first);
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting("Local\\Scout.Show"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var store = new ScoutStore(Path.Combine(DataDirectory, "scout.db"));
            if (diagnose)
            {
                var testSettings = store.Settings;
                var watchRoot = Path.Combine(DataDirectory, "watcher-fixtures"); Directory.CreateDirectory(watchRoot);
                testSettings.ExtraFolders = testSettings.ExtraFolders.Append(watchRoot).Distinct().ToArray();
                testSettings.WindowsSearch = false; testSettings.AiEnabled = false; testSettings.Hotkey = "Ctrl+Alt+Space";
                store.SaveSettings(testSettings);
            }
            if (smoke)
            {
                store.SaveSettings(new ScoutSettings { WindowsSearch = false });
                var now = DateTimeOffset.UtcNow;
                var demoDownloads = Path.Combine(Path.GetPathRoot(DataDirectory) ?? "C:\\", "Users", "Alex", "Downloads");
                var fixtures = new[] { "Product Roadmap.pdf", "Research Notes.pdf", "Quarterly Review.pdf" }
                    .Select((name, i) => new Candidate("smoke:" + i, ItemKind.File, name, Path.Combine(DataDirectory, name))
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
                    .ToArray();
                store.ReplaceIndex(fixtures);
            }
            index = new IndexService(store); model = new ModelHost();
            if (!smoke && !diagnose && !bluetooth) _ = model.WarmAsync(store.Settings);
            multimodal = new MultimodalHost();
            await multimodal.ConfigureAsync(store.Settings.Multimodal);
            var main = new MainWindow(store, index, model, multimodal); MainWindow = main;
            tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Scout — Ctrl+Alt+Space", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
                Visible = true, ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
            };
            tray.DoubleClick += (_, _) => main.Toggle(true);
            tray.ContextMenuStrip.Items.Add("Open Scout", null, (_, _) => main.Toggle(true));
            tray.ContextMenuStrip.Items.Add("Settings", null, (_, _) => main.ShowSettings());
            tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Shutdown());
            main.Show();
            var indexReady = !smoke && !bluetooth
                ? ConfigureIndexAsync(index, store.Settings, diagnose || realworld ? null : main)
                : index.LoadFixturesAsync();
            if (diagnose || realworld || bluetooth)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop();
                    try { await indexReady; var report = bluetooth ? await main.DiagnoseBluetoothAsync() : realworld ? await main.DiagnoseRealWorldAsync() : await main.DiagnoseAsync(); File.WriteAllText(Path.Combine(DataDirectory, "desktop-diagnostics.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
                    catch (Exception error) { File.WriteAllText(Path.Combine(DataDirectory, "desktop-diagnostics.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = error.ToString() })); Environment.ExitCode = 1; }
                    finally { Shutdown(); }
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
                        await main.SmokeCheck(uiSmoke);
                        // Keep offscreen WPF captures separate from the WinRT PDF renderer.
                        if (uiSmoke) await UiDiagnostics.Snapshot(main, "launcher");
                        File.WriteAllText(Path.Combine(DataDirectory, "smoke-result.txt"), uiSmoke
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
