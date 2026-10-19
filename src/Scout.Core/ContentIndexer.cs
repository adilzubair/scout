namespace Scout.Core;

public sealed class ContentIndexer
{
    private readonly ContentIndex index;
    private readonly Func<string, CancellationToken, Task<string?>> readText;
    private readonly Func<CancellationToken, Task> clear;

    public const long MaximumFileBytes = 16 * 1024 * 1024;

    public ContentIndexer(ContentIndex index)
        : this(index, PreviewText.ExtractAsync, token => Task.Run(index.Clear, token)) { }

    public ContentIndexer(ContentIndex index, Func<string, CancellationToken, Task<string?>> readText, Func<CancellationToken, Task>? clear = null)
    {
        this.index = index;
        this.readText = readText;
        this.clear = clear ?? (token => Task.Run(index.Clear, token));
    }

    public async Task SynchronizeAsync(IEnumerable<Candidate> candidates, IEnumerable<string> selectedFolders, IEnumerable<string> roots, IEnumerable<string> exclusions, CancellationToken token, Action<int>? progress = null)
    {
        var activeRoots = roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = selectedFolders.Select(Normalize).Where(folder => activeRoots.Any(root => IndexPolicy.Within(folder, root) || IndexPolicy.Within(root, folder))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var excluded = exclusions.ToArray();
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0; var nextReport = DateTime.UtcNow;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Kind != ItemKind.File || !InScope(candidate.Target, selected, activeRoots, excluded)) continue;
            eligible.Add(candidate.Id);
            await UpdateAsync(candidate, selected, activeRoots, excluded, token).ConfigureAwait(false);
            processed++;
            if (DateTime.UtcNow >= nextReport) { progress?.Invoke(processed); nextReport = DateTime.UtcNow.AddMilliseconds(250); }
        }
        token.ThrowIfCancellationRequested();
        index.RemoveExcept(eligible);
        progress?.Invoke(processed);
    }

    public async Task UpdateAsync(Candidate candidate, IEnumerable<string> selectedFolders, IEnumerable<string> roots, IEnumerable<string> exclusions, CancellationToken token)
    {
        var selected = selectedFolders.Select(Normalize).ToArray();
        var activeRoots = roots.Select(Normalize).ToArray();
        var excluded = exclusions.ToArray();
        if (candidate.Kind != ItemKind.File) return;
        if (!InScope(candidate.Target, selected, activeRoots, excluded))
        {
            index.Remove([candidate.Id]);
            return;
        }
        FileInfo info;
        try
        {
            info = new(candidate.Target);
            if (!info.Exists || info.Length > MaximumFileBytes || !PreviewText.Supports(candidate.Target))
            {
                index.Remove([candidate.Id]);
                return;
            }
            if (index.IsCurrent(candidate.Id, candidate.Target, info.LastWriteTimeUtc.Ticks, info.Length)) return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            index.Remove([candidate.Id]);
            return;
        }
        try
        {
            var body = await readText(candidate.Target, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(body)) index.Remove([candidate.Id]);
            else index.Put(candidate.Id, candidate.Target, info.LastWriteTimeUtc.Ticks, info.Length, body);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            index.Remove([candidate.Id]);
        }
    }

    public void Remove(IEnumerable<string> candidateIds) => index.Remove(candidateIds);
    public void Clear() => index.Clear();
    public Task ClearAsync(CancellationToken token) => clear(token);

    private static bool InScope(string path, string[] selectedFolders, string[] roots, string[] exclusions) =>
        selectedFolders.Length > 0 && roots.Any(root => IndexPolicy.Within(path, root)) && selectedFolders.Any(folder => IndexPolicy.Within(path, folder)) && !IndexPolicy.Excluded(path, exclusions);

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
