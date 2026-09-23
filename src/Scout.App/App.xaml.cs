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
    internal static string DataDirectory => Environment.GetEnvironmentVariable("SCOUT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test");
        var diagnose = e.Args.Contains("--diagnose");
        var realworld = e.Args.Contains("--realworld-diagnose");
        var bluetooth = e.Args.Contains("--bluetooth-diagnose");
        if ((realworld || bluetooth) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCOUT_DATA_DIR"))) { Shutdown(1); return; }
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
                var fixtures = new[] { "Product Roadmap.pdf", "Research Notes.pdf", "Quarterly Review.pdf" }.Select((name, i) => new Candidate("smoke:" + i, ItemKind.File, name, Path.Combine(DataDirectory, name)) { Detail = "Documents", Source = "Files", AddedAt = DateTimeOffset.UtcNow.AddMinutes(-i - 1), ModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-i - 1) }).ToArray();
                store.ReplaceIndex(fixtures);
            }
            index = new IndexService(store); model = new ModelHost();
            if (!smoke && !diagnose && !bluetooth) _ = model.WarmAsync(store.Settings);
            var main = new MainWindow(store, index, model); MainWindow = main;
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
            if (!smoke && !bluetooth) _ = index.ConfigureAsync(store.Settings);
            else _ = index.LoadFixturesAsync();
            if (diagnose || realworld || bluetooth)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop();
                    try { var report = bluetooth ? await main.DiagnoseBluetoothAsync() : realworld ? await main.DiagnoseRealWorldAsync() : await main.DiagnoseAsync(); File.WriteAllText(Path.Combine(DataDirectory, "desktop-diagnostics.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
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
                        await main.SmokeCheck();
                        var renderer = new System.Windows.Media.Imaging.RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        renderer.Render(main);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(renderer));
                        using var output = File.Create(Path.Combine(DataDirectory, "launcher.png")); encoder.Save(output);
                        File.WriteAllText(Path.Combine(DataDirectory, "smoke-result.txt"), "Desktop checks passed: rendering, scopes, manual selection, arrival selection, editable Open all, calculator, web fallback, PDF/image/Office/text previews, model host (when configured).");
                    }
                    catch (Exception error) { File.WriteAllText(Path.Combine(DataDirectory, "smoke-result.txt"), error.ToString()); Environment.ExitCode = 1; }
                    finally { Shutdown(); }
                }; timer.Start();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Scout could not start. " + ex.Message, "Scout", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose(); index?.Dispose(); model?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
}
