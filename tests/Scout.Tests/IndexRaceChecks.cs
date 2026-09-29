using System.Text;
using Scout.App;
using Scout.Core;

static class IndexRaceChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Configuration serializes content publication across disable and folder changes", () => RunConfigurationRaceAsync().GetAwaiter().GetResult());
        test("Configuration reports a caught content reconciliation failure", () => RunConfigurationFailureAsync().GetAwaiter().GetResult());
    }

    private static async Task RunConfigurationFailureAsync()
    {
        const string token = "failedconfigurationcanary";
        var publicRoot = Environment.GetEnvironmentVariable("PUBLIC") ?? throw new InvalidOperationException("PUBLIC is unavailable");
        var fixture = Path.Combine(publicRoot, "Scout-index-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var app = new Candidate("failure-app", ItemKind.App, "Failure App", @"C:\Apps\Failure App.lnk");
            var selectedRoot = Path.Combine(fixture, "selected");
            Directory.CreateDirectory(selectedRoot);
            var path = Path.Combine(selectedRoot, "failure.txt");
            await File.WriteAllTextAsync(path, token);
            var store = new ScoutStore(Path.Combine(fixture, "scout.db"));
            var indexer = new ContentIndexer(store.ContentIndex, (_, _) =>
                Task.FromException<string?>(new Microsoft.Data.Sqlite.SqliteException("Injected reconciliation failure", 1)));
            using var service = new IndexService(store, indexer, () => [], () => [app]);

            Exception? failure = null;
            try { await service.ConfigureAsync(Enabled(selectedRoot)); }
            catch (IOException error) { failure = error; }

            Check(failure != null, "ConfigureAsync completed successfully after content reconciliation failed");
            Check(service.SearchContent(token, Scope.Files, DateTimeOffset.UtcNow, CancellationToken.None).Length == 0,
                "Content search became available after content reconciliation failed");
            Check(service.Catalog.Contains(app.Id), "The discovered application was not published after content reconciliation failed");
            Check(SearchEngine.Search("Failure App", Scope.Apps, service.Catalog, store.Library, DateTimeOffset.UtcNow).Hits.Single().Candidate.Id == app.Id,
                "The discovered application was unavailable to ordinary app search after content reconciliation failed");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(fixture, true);
        }
    }

    private static async Task RunConfigurationRaceAsync()
    {
        const string oldToken = "oldscopeprivacycanary";
        const string newToken = "newscopeprivacycanary";
        var publicRoot = Environment.GetEnvironmentVariable("PUBLIC") ?? throw new InvalidOperationException("PUBLIC is unavailable");
        var fixture = Path.Combine(publicRoot, "Scout-index-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var oldRoot = Path.Combine(fixture, "old-root");
            var newRoot = Path.Combine(fixture, "new-root");
            Directory.CreateDirectory(oldRoot);
            Directory.CreateDirectory(newRoot);
            var oldPath = Path.Combine(oldRoot, "old.txt");
            var newPath = Path.Combine(newRoot, "new.txt");
            await File.WriteAllTextAsync(oldPath, string.Join(' ', Enumerable.Repeat(oldToken, 200)));
            await File.WriteAllTextAsync(newPath, string.Join(' ', Enumerable.Repeat(newToken, 200)));

            var oldExtractionStarted = NewSignal();
            var releaseOldExtraction = NewSignal();
            var newExtractionStarted = NewSignal();
            var releaseNewExtraction = NewSignal();
            var store = new ScoutStore(Path.Combine(fixture, "scout.db"));
            var indexer = new ContentIndexer(store.ContentIndex, async (path, token) =>
            {
                if (path.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    oldExtractionStarted.TrySetResult();
                    await releaseOldExtraction.Task.WaitAsync(token);
                    return string.Join(' ', Enumerable.Repeat(oldToken, 200));
                }
                if (path.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                {
                    newExtractionStarted.TrySetResult();
                    await releaseNewExtraction.Task.WaitAsync(token);
                    return string.Join(' ', Enumerable.Repeat(newToken, 200));
                }
                return null;
            }, _ =>
            {
                store.ContentIndex.Clear();
                return Task.CompletedTask;
            });
            using var service = new IndexService(store, indexer, () => [], () => []);
            var oldSettings = Enabled(oldRoot);
            var disabledSettings = new ScoutSettings { ExtraFolders = [newRoot], RecentItems = false, WindowsSearch = false };
            var newSettings = Enabled(newRoot);

            var oldConfiguration = service.ConfigureAsync(oldSettings);
            await WaitFor(oldExtractionStarted.Task, "old extraction");
            var disabledConfiguration = service.ConfigureAsync(disabledSettings);
            var newConfiguration = service.ConfigureAsync(newSettings);
            releaseOldExtraction.TrySetResult();
            await WaitFor(newExtractionStarted.Task, "new extraction");

            Check(service.SearchContent(oldToken, Scope.Files, DateTimeOffset.UtcNow, CancellationToken.None).Length == 0,
                "Removed-folder body content was searchable while the replacement configuration was still reconciling");

            releaseNewExtraction.TrySetResult();
            await WaitFor(Task.WhenAll(oldConfiguration, disabledConfiguration, newConfiguration), "configuration completion");

            var oldId = Candidate.Key(ItemKind.File, oldPath);
            var newId = Candidate.Key(ItemKind.File, newPath);
            Check(service.Roots.SequenceEqual([newRoot], StringComparer.OrdinalIgnoreCase), "The final roots did not match the last configuration");
            Check(!service.Catalog.Contains(oldId) && service.Catalog.Contains(newId), "The final catalog retained the removed folder or omitted the new folder");
            Check(store.ContentIndex.Count == 1, "The final content index did not contain exactly the new scope");
            Check(store.ContentIndex.Search([oldToken]).Length == 0, "The removed-folder body ID remained in the content index");
            Check(store.ContentIndex.Search([newToken]).Single().CandidateId == newId, "The new folder body was not indexed");
            Check(!PersistedContentContains(store.ContentIndex, fixture, oldToken), "Removed-folder body bytes remained in the content sidecar");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(fixture, true);
        }
    }

    private static ScoutSettings Enabled(string root) => new()
    {
        ContentSearchEnabled = true,
        ContentFolders = [root],
        ExtraFolders = [root],
        RecentItems = false,
        WindowsSearch = false
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitFor(Task task, string operation)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new Exception("Timed out waiting for " + operation); }
    }

    private static bool PersistedContentContains(ContentIndex index, string fixture, string token)
    {
        _ = index.Count;
        var needle = Encoding.UTF8.GetBytes(token);
        return Directory.EnumerateFiles(fixture, "scout.db.content*")
            .Any(path => File.ReadAllBytes(path).AsSpan().IndexOf(needle) >= 0);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
