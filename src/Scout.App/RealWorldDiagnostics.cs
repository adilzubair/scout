using System.Diagnostics;
using System.Text.Json;
using Scout.Core;

namespace Scout.App;

public partial class MainWindow
{
    private async Task WaitForSettledSearch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await WaitForSearch().WaitAsync(timeout.Token);
            var generation = revision; var refining = refinementTask; var enriching = enrichmentTask;
            await Task.WhenAll(refining, enriching).WaitAsync(timeout.Token);
            await Task.Delay(80, timeout.Token);
            if (generation == revision && refining == refinementTask && enriching == enrichmentTask) return;
        }
    }
    internal async Task<object> DiagnoseRealWorldAsync()
    {
        await index.RefreshAsync(wait: true);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(App.DataDirectory, "audit-input.json")));
        await model.WarmAsync(store.Settings);
        if (!store.Settings.AiEnabled) throw new InvalidOperationException("The desktop audit requires the user's configured local Laya.");
        var checks = new List<object>(); var failures = new List<string>();
        foreach (var scenario in manifest.RootElement.GetProperty("checks").EnumerateArray())
        {
            var query = scenario.GetProperty("Query").GetString()!;
            var name = scenario.GetProperty("Name").GetString()!;
            var expected = scenario.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToArray();
            var mode = scenario.GetProperty("mode").GetString();
            var folder = scenario.GetProperty("Folder").GetString();
            bool Correct(IEnumerable<SearchHit> hits)
            {
                var items = hits.ToArray(); var paths = items.Select(h => h.Candidate.Target).ToArray();
                if (folder != null && paths.Any(p => !IndexPolicy.Within(p, folder))) return false;
                return mode switch
                {
                    "title" => items.Length > 0 && items.All(h => h.Candidate.Title.Equals(query, StringComparison.OrdinalIgnoreCase)) && expected.All(e => paths.Contains(e, StringComparer.OrdinalIgnoreCase)),
                    "set" => paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected),
                    _ => paths.FirstOrDefault()?.Equals(expected.FirstOrDefault(), StringComparison.OrdinalIgnoreCase) == true
                };
            }
            groupEdited = false; ClearGroup(); scope = Scope.All; UpdateScopes();
            var timer = Stopwatch.StartNew(); Query.Text = query; await WaitForSearch();
            var localMs = timer.Elapsed.TotalMilliseconds;
            var localCorrect = current?.Query == query && Correct(displayedHits);
            await WaitForSettledSearch();
            var finalCorrect = current?.Query == query && Correct(displayedHits);
            var selectionCorrect = displayedHits.Count == 0 ? Selected == null || Selected.Kind == ItemKind.WebSearch : Selected?.Kind == ItemKind.Group ? group.Count >= 2 && group.Values.All(c => displayedHits.Any(h => h.Candidate.Id == c.Id)) : Selected?.Id == displayedHits.FirstOrDefault()?.Candidate.Id;
            var actionCorrect = current?.Intent.Action == ItemAction.Open;
            var passed = localCorrect && finalCorrect && selectionCorrect && actionCorrect;
            if (!passed) failures.Add(name);
            checks.Add(new { name, query, passed, localCorrect, finalCorrect, selectionCorrect, actionCorrect, localMs, settledMs = timer.Elapsed.TotalMilliseconds, top = displayedHits.FirstOrDefault()?.Candidate.Target, count = displayedHits.Count, selected = Selected?.Title, status = Status.Text, modelNeeded = current != null && RankingPolicy.NeedsModel(current) });
        }
        // Exercise a real inference inside an explicitly resolved folder. It must
        // never introduce out-of-folder results, regardless of model probability.
        Query.Text = "pdf about the company profile in telegram folder"; await WaitForSearch();
        var semantic = current!; await WaitForSettledSearch();
        var semanticScoped = displayedHits.All(h => semantic.Locations.Any(p => IndexPolicy.Within(h.Candidate.Target, p)));
        var modelResponded = judgment is { Scores.Count: > 0 };
        var semanticCorrect = displayedHits.FirstOrDefault()?.Candidate.Title.Contains("Corporate_Profile", StringComparison.OrdinalIgnoreCase) == true;
        if (!semanticScoped || !modelResponded || !semanticCorrect) failures.Add("Scoped real Laya inference");
        checks.Add(new { name = "Scoped real Laya inference", query = Query.Text, passed = semanticScoped && modelResponded && semanticCorrect, top = displayedHits.FirstOrDefault()?.Candidate.Target, count = displayedHits.Count, status = Status.Text });

        Query.Text = "latest 3 files in telegram folder"; await WaitForSettledSearch();
        if (group.Count != 3) failures.Add("Three-file group");
        else
        {
            var removed = group.Values.Last(); group.Remove(removed.Id); groupEdited = true;
            Results.SelectedIndex = 2; manualSelection = true; var selection = Selected?.Id;
            RefreshLocal(); await WaitForSettledSearch();
            if (group.Count != 2 || group.ContainsKey(removed.Id) || Selected?.Id != selection) failures.Add("Edited group or manual selection overwritten");
        }
        groupEdited = false; ClearGroup();
        var rapid = "latest file in telegram folder";
        for (var i = 1; i <= rapid.Length; i++) { Query.Text = rapid[..i]; await Task.Delay(5); }
        await WaitForSettledSearch();
        if (current?.Query != rapid || Selected?.Kind != ItemKind.File) failures.Add("Rapid typing published stale results");
        var folderResult = SearchEngine.Search("open Telegram Desktop folder", Scope.Files, index.Catalog, store.Library, DateTimeOffset.Now).Hits.FirstOrDefault()?.Candidate;
        if (folderResult?.Kind != ItemKind.Folder || !WindowsIntegration.Valid(folderResult)) failures.Add("Folder execution validation");
        var report = new { passed = failures.Count == 0, failures, fileCount = index.Candidates.Count(c => c.Kind == ItemKind.File), checks, editedGroupAndSelection = !failures.Any(f => f.Contains("group", StringComparison.OrdinalIgnoreCase) || f.Contains("selection")), rapidTyping = current?.Query == rapid, folderValidation = folderResult != null && WindowsIntegration.Valid(folderResult) };
        if (failures.Count > 0) Environment.ExitCode = 1;
        return report;
    }
}
