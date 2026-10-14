namespace Scout.Core;

// Bounded single-file jobs run outside the metadata index's refresh gate.
// The source adapter verifies file identity and scope; extraction reads an owned snapshot.
public sealed class SemanticIndexer(
    SemanticIndex index,
    Func<SemanticSource, CancellationToken, Task<SemanticSource?>> currentSource,
    Func<SemanticSource, CancellationToken, Task<SemanticExtraction>> extract)
{
    private readonly SemaphoreSlim gate = new(1);
    public async Task<SemanticWork> ProcessNextAsync(bool paused, long quotaBytes, DateTimeOffset now, CancellationToken token)
    {
        if (paused) return SemanticWork.Paused;
        await gate.WaitAsync(token).ConfigureAwait(false);
        SemanticJob? job = null;
        try
        {
            token.ThrowIfCancellationRequested();
            job = index.Claim(now);
            if (job == null) return SemanticWork.Empty;
            if (!await Unchanged(job, token).ConfigureAwait(false)) return SemanticWork.Changed;
            var result = await extract(job.Source, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!await Unchanged(job, token).ConfigureAwait(false)) return SemanticWork.Changed;
            token.ThrowIfCancellationRequested();
            return index.Publish(job, job.Source.Revision, result.ContentHash, result.Segments, result.Partial, result.Detail, quotaBytes) switch
            {
                SemanticPublish.Accepted => SemanticWork.Published,
                SemanticPublish.QuotaExceeded => SemanticWork.QuotaExceeded,
                _ => SemanticWork.Changed
            };
        }
        catch (UnsupportedContentException error)
        {
            if (job != null) index.Fail(job, error.Reason, true, now);
            return SemanticWork.Unsupported;
        }
        catch (IOException)
        {
            if (job != null) index.Fail(job, "read_or_decode_failed", false, now);
            return SemanticWork.Retry;
        }
        catch (UnauthorizedAccessException)
        {
            if (job != null) index.Fail(job, "access_denied", false, now);
            return SemanticWork.Retry;
        }
        finally
        {
            if (job != null) index.Release(job);
            gate.Release();
        }
    }
    private async Task<bool> Unchanged(SemanticJob job, CancellationToken token)
    {
        var source = job.Source;
        var current = await currentSource(source, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (current == source) return true;
        index.RefreshSource(job, current);
        return false;
    }
}
