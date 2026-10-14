using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Scout.Core;

namespace Scout.App;

// Trusted bridge: sources -> no-capability parser -> validated memory -> full-model host.
// The parser never receives an original file path or the embedding worker's credentials.
internal sealed class SemanticExtractor : IAsyncDisposable
{
    private readonly string root, addonRoot, pythonHome, runtime, tokenizer;
    private readonly MultimodalInstallation installation;
    private readonly Func<object, CancellationToken, Task<EmbeddingResult>> embed;
    private readonly Func<SemanticScope> currentScope;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private ExtractionSandbox? sandbox;
    private MultimodalLease? runtimeLease;
    private bool disposed;
    internal string WorkRoot => root;

    public SemanticExtractor(string addonRoot, MultimodalInstallation installation, Func<SemanticScope> currentScope,
        Func<object, CancellationToken, Task<EmbeddingResult>> embed, string? developmentPythonHome = null)
    {
        this.installation = installation; this.currentScope = currentScope; this.embed = embed;
        this.addonRoot = Path.GetFullPath(addonRoot);
        root = MultimodalSetup.OwnedPath(addonRoot, "extraction/" + installation.Generation);
        runtime = Path.Combine(AppContext.BaseDirectory, "runtime");
        pythonHome = PythonHome(addonRoot, installation.Python, developmentPythonHome);
        tokenizer = Path.Combine(root, "assets", "tokenizer.json");
    }
    internal static string PythonHome(string addonRoot, string python, string? developmentHome = null)
    {
        var venv = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(python)))!;
        var cfg = MultimodalSetup.OwnedPath(venv, "pyvenv.cfg");
        if (new FileInfo(cfg).Length > 16384) throw new IOException("Invalid extraction interpreter configuration.");
        var homes = File.ReadLines(cfg).Where(line => line.StartsWith("home = ", StringComparison.Ordinal)).ToArray();
        if (homes.Length != 1 || !Path.IsPathFullyQualified(homes[0][7..])) throw new IOException("Missing private extraction interpreter.");
        var home = Path.GetFullPath(homes[0][7..]);
        var allowed = developmentHome == null ? IndexPolicy.Within(home, Path.Combine(Path.GetFullPath(addonRoot), "python")) :
            home.Equals(Path.GetFullPath(developmentHome), StringComparison.OrdinalIgnoreCase);
        if (!allowed || !File.Exists(MultimodalSetup.OwnedPath(home, "python.exe"))) throw new IOException("Extraction interpreter is outside its private runtime.");
        return home;
    }
    private async Task InitializeAsync(CancellationToken token)
    {
        if (sandbox != null) return;
        runtimeLease ??= new MultimodalLease(addonRoot);
        Directory.CreateDirectory(root);
        var assets = MultimodalSetup.OwnedPath(root, "assets"); Directory.CreateDirectory(assets);
        using var model = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(runtime, "embeddinggemma2_model.json"), token).ConfigureAwait(false));
        var expected = model.RootElement.GetProperty("files").GetProperty("tokenizer.json");
        var path = MultimodalSetup.OwnedPath(installation.Model, "tokenizer.json");
        // A private copy exposes only the tokenizer to parsers, never model weights.
        if (!File.Exists(tokenizer) || !await VerifiedAsync(tokenizer, expected.GetProperty("bytes").GetInt64(), expected.GetProperty("sha256").GetString()!, token).ConfigureAwait(false))
        {
            var temporary = MultimodalSetup.OwnedPath(assets, "tokenizer.partial");
            File.Copy(path, temporary, true);
            if (!await VerifiedAsync(temporary, expected.GetProperty("bytes").GetInt64(), expected.GetProperty("sha256").GetString()!, token).ConfigureAwait(false))
                throw new IOException("Extraction tokenizer integrity failed.");
            File.Move(temporary, tokenizer, true);
        }
        var venv = Path.GetDirectoryName(Path.GetDirectoryName(installation.Python))!;
        sandbox = new ExtractionSandbox(root, [pythonHome, venv, runtime, assets]);
        // Constructor recovery restores the previous job grants before any old copies are removed.
        foreach (var directory in Directory.EnumerateDirectories(root, "source-*"))
        {
            var name = Path.GetFileName(directory);
            if (name.Length == 39 && name[7..].All(char.IsAsciiHexDigit)) DeleteJob(root, directory);
        }
    }
    public async Task<SemanticExtraction> ExtractAsync(SemanticSource source, CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        await gate.WaitAsync(request.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await InitializeAsync(request.Token).ConfigureAwait(false);
            var scope = currentScope();
            if (SemanticSourceReader.Read(source.Path, scope) != source) throw new IOException("Source changed before parsing.");
            using var snapshot = await SemanticSourceReader.SnapshotAsync(source, scope, root, request.Token).ConfigureAwait(false);
            try
            {
                var path = Path.Combine(snapshot.Directory, "request.json");
                await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, source = Path.GetFileName(snapshot.Path),
                    sourceSha256 = snapshot.ContentHash.ToLowerInvariant(), family = source.Family, tokenizer }), request.Token).ConfigureAwait(false);
                var exit = await sandbox!.RunAsync(installation.Python, [Path.Combine(runtime, "semantic_search", "extract.py"), "--request", path],
                    snapshot.Directory, TimeSpan.FromMinutes(3), request.Token).ConfigureAwait(false);
                if (exit != 0)
                {
                    using var failed = await ReadJsonAsync(MultimodalSetup.OwnedPath(snapshot.Directory, "failed.json"), 16384, request.Token).ConfigureAwait(false);
                    Fields(failed.RootElement, ["schemaVersion", "reason"]);
                    var reason = failed.RootElement.GetProperty("reason").GetString() ?? "";
                    if (failed.RootElement.GetProperty("schemaVersion").GetInt32() != 1 || !Reason(reason)) throw new IOException("Invalid extraction failure receipt.");
                    if (reason is "extraction_failed" or "snapshot_changed" or "snapshot_hash_mismatch") throw new IOException("Extraction failed.");
                    throw new UnsupportedContentException(reason);
                }
                var ready = await PreparedManifest.ReadAsync(snapshot.Directory, snapshot.ContentHash, request.Token).ConfigureAwait(false);
                var segments = new List<ContentSegment>();
                foreach (var receipt in ready.Segments)
                {
                    request.Token.ThrowIfCancellationRequested();
                    if (SemanticSourceReader.Read(source.Path, currentScope()) != source) throw new IOException("Source changed during embedding.");
                    var prepared = await PreparedManifest.ReadSegmentAsync(snapshot.Directory, receipt, source.Family, request.Token).ConfigureAwait(false);
                    EmbeddingResult result;
                    try { result = await embed(prepared.Content, request.Token).ConfigureAwait(false); }
                    catch (System.Net.Http.HttpRequestException error) { throw new IOException("Embedding worker unavailable.", error); }
                    if (result.SchemaVersion != 1 || result.Tokens is < 1 or > 8192 || result.Vector.Length != 768 ||
                        result.Vector.Any(v => !float.IsFinite(v)) || Math.Abs(result.Vector.Sum(v => (double)v*v)-1) > .001)
                        throw new IOException("Invalid embedding receipt.");
                    segments.Add(new(prepared.Modality, prepared.Locator, prepared.Evidence, result.Vector) { Thumbnail = prepared.Thumbnail });
                    // Release the model gate between segments, giving interactive queries a scheduling point.
                    await Task.Yield();
                }
                if (!await snapshot.IsCurrentAsync(currentScope(), request.Token).ConfigureAwait(false)) throw new IOException("Source changed before publication.");
                return new(snapshot.ContentHash, segments, ready.Partial, ready.Detail);
            }
            finally { sandbox!.ReleaseJob(snapshot.Directory); }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { throw new IOException("Invalid extraction output.", error); }
        finally { gate.Release(); }
    }
    private static bool Reason(string value) => value.Length is > 0 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    internal static void Fields(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected extraction object.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count() || required.Any(name => !names.Contains(name, StringComparer.Ordinal)) ||
            names.Any(name => !required.Contains(name, StringComparer.Ordinal) && !optional.Contains(name, StringComparer.Ordinal))) throw new InvalidDataException("Unexpected extraction fields.");
    }
    internal static async Task<JsonDocument> ReadJsonAsync(string path, long maximum, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length is < 2 || file.Length > maximum) throw new IOException("Extraction output size limit.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }
    internal static async Task<bool> VerifiedAsync(string path, long bytes, string hash, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return file.Length == bytes && Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    internal static void DeleteJob(string ownerRoot, string directory)
    {
        var owned = MultimodalSetup.OwnedPath(ownerRoot, Path.GetRelativePath(ownerRoot, directory));
        var pending = new Stack<string>(); pending.Push(owned);
        while (pending.TryPop(out var folder))
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked extraction workspace.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        Directory.Delete(owned, true);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            try { sandbox?.Dispose(); sandbox = null; }
            finally { runtimeLease?.Dispose(); runtimeLease = null; }
            if (Directory.Exists(root))
                foreach (var directory in Directory.EnumerateDirectories(root, "source-*"))
                    if (Path.GetFileName(directory) is { Length: 39 } name && name[7..].All(char.IsAsciiHexDigit)) DeleteJob(root, directory);
        }
        finally { gate.Release(); }
    }
}

internal sealed record PreparedReceipt(string Name, long Bytes, string Sha256);
internal sealed record PreparedSegment(JsonElement Content, string Modality, ContentLocator Locator, string Evidence, byte[] Thumbnail);
internal sealed record PreparedManifest(PreparedReceipt[] Segments, bool Partial, string Detail)
{
    public static async Task<PreparedManifest> ReadAsync(string job, string sourceHash, CancellationToken token)
    {
        using var json = await SemanticExtractor.ReadJsonAsync(MultimodalSetup.OwnedPath(job, "output/ready.json"), 128*1024, token).ConfigureAwait(false);
        var data = json.RootElement;
        SemanticExtractor.Fields(data, ["schemaVersion", "sourceSha256", "segments", "partial", "reasons", "bytes"], "coverage");
        if (data.GetProperty("schemaVersion").GetInt32() != 1 || !sourceHash.Equals(data.GetProperty("sourceSha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Extraction source mismatch.");
        var items = data.GetProperty("segments").EnumerateArray().ToArray();
        if (items.Length is < 1 or > 256) throw new InvalidDataException("Extraction segment limit.");
        var receipts = new List<PreparedReceipt>(); long total = 0;
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i]; SemanticExtractor.Fields(item, ["name", "bytes", "sha256"]);
            var name = item.GetProperty("name").GetString()!; var bytes = item.GetProperty("bytes").GetInt64(); var hash = item.GetProperty("sha256").GetString()!;
            if (name != $"segment-{i:0000}.json" || bytes is < 2 or > 32*1024*1024 || hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid extraction receipt.");
            total += bytes; if (total > 256L*1024*1024) throw new InvalidDataException("Extraction output limit.");
            receipts.Add(new(name, bytes, hash));
        }
        if (total != data.GetProperty("bytes").GetInt64()) throw new InvalidDataException("Extraction size mismatch.");
        var reasons = data.GetProperty("reasons").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
        var detail = string.Join(',', reasons); var partial = data.GetProperty("partial").GetBoolean();
        if (reasons.Length > 16 || detail.Length > 1000 || partial != (reasons.Length > 0) || reasons.Distinct().Count() != reasons.Length ||
            reasons.Any(reason => reason.Length is < 1 or > 80 || reason.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))) throw new InvalidDataException("Invalid extraction coverage reasons.");
        return new(receipts.ToArray(), partial, detail);
    }
    public static async Task<PreparedSegment> ReadSegmentAsync(string job, PreparedReceipt receipt, string family, CancellationToken token)
    {
        var path = MultimodalSetup.OwnedPath(job, "output/" + receipt.Name);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length != receipt.Bytes || file.Length > 32*1024*1024 || file.Length < 2) throw new InvalidDataException("Extraction segment size mismatch.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Extraction segment hash mismatch.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); var data = json.RootElement;
        SemanticExtractor.Fields(data, ["content", "modality", "locator", "evidence", "thumbnail"], "coverage");
        var modality = data.GetProperty("modality").GetString()!; var content = data.GetProperty("content");
        if (content.GetProperty("type").GetString() != modality || !Allowed(family, modality)) throw new InvalidDataException("Extraction modality mismatch.");
        ValidateContent(content);
        var locator = JsonSerializer.Deserialize<ContentLocator>(data.GetProperty("locator"), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? throw new InvalidDataException("Missing extraction locator.");
        locator.Validate(); var evidence = data.GetProperty("evidence").GetString() ?? "";
        if (evidence.Length > 32000) throw new InvalidDataException("Extraction evidence limit.");
        var thumbnail = Bytes(data.GetProperty("thumbnail"), 128*1024, empty: true);
        if (modality is "audio" or "video" && (locator.Kind != "time" || locator.EndSeconds <= locator.StartSeconds)) throw new InvalidDataException("Invalid media locator.");
        if (modality == "audio" && Math.Abs(Bytes(content.GetProperty("data"),60*16000*4).Length/(4.0*16000)-(locator.EndSeconds!.Value-locator.StartSeconds!.Value)) > 2.0/16000)
            throw new InvalidDataException("Audio duration mismatch.");
        if (modality == "video")
        {
            if (!data.TryGetProperty("coverage", out var coverage)) throw new InvalidDataException("Missing sampled-frame coverage.");
            SemanticExtractor.Fields(coverage, ["sampleTimesSeconds"]);
            var times = coverage.GetProperty("sampleTimesSeconds").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            var indices = content.GetProperty("indices").EnumerateArray().Select(item => item.GetInt32()).ToArray(); var fps = content.GetProperty("fps").GetDouble();
            if (times.Length != indices.Length || Math.Abs(content.GetProperty("totalFrames").GetInt32()/fps-(locator.EndSeconds!.Value-locator.StartSeconds!.Value)) > .0101 ||
                times.Where((t,i) => !double.IsFinite(t) || t < locator.StartSeconds || t >= locator.EndSeconds || Math.Abs(t-locator.StartSeconds!.Value-indices[i]/fps) > .0101).Any()) throw new InvalidDataException("Invalid sampled-frame timestamps.");
        }
        return new(content.Clone(), modality, locator, evidence, thumbnail);
    }
    private static bool Allowed(string family, string modality) => family switch
    {
        "documents" or "presentations" or "spreadsheets" or "images" => modality is "text" or "image",
        "code" => modality == "text", "audio" => modality == "audio", "video" => modality is "video" or "audio", _ => false
    };
    private static byte[] Bytes(JsonElement value, int maximum, bool empty = false)
    {
        var text = value.GetString() ?? throw new InvalidDataException("Missing encoded media.");
        if (empty && text.Length == 0) return [];
        if (text.Length is < 4 || text.Length > (maximum+2)/3*4 || text.Any(char.IsWhiteSpace)) throw new InvalidDataException("Encoded media limit.");
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length is < 1 || bytes.Length > maximum) throw new InvalidDataException("Decoded media limit.");
        return bytes;
    }
    private static void Image(JsonElement value)
    {
        var bytes = Bytes(value, 8*1024*1024);
        // Actual decoding/pixel/shape checks also occur in the bounded model protocol.
        if (!(bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ||
              bytes.Length >= 8 && bytes.AsSpan(0,8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }))) throw new InvalidDataException("Unexpected prepared image encoding.");
    }
    private static void ValidateContent(JsonElement content)
    {
        switch (content.GetProperty("type").GetString())
        {
            case "text":
                SemanticExtractor.Fields(content, ["type", "text"]);
                if (content.GetProperty("text").GetString() is not { Length: > 0 and <= 200000 } text || string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Invalid prepared text.");
                break;
            case "image": SemanticExtractor.Fields(content, ["type", "data"]); Image(content.GetProperty("data")); break;
            case "audio":
                SemanticExtractor.Fields(content, ["type", "data", "sampleRate"]);
                var audio = Bytes(content.GetProperty("data"), 60*16000*4);
                if (content.GetProperty("sampleRate").GetInt32() != 16000 || audio.Length % 4 != 0) throw new InvalidDataException("Invalid prepared audio.");
                for (var i=0; i<audio.Length; i+=4)
                {
                    var sample = BinaryPrimitives.ReadSingleLittleEndian(audio.AsSpan(i,4));
                    if (!float.IsFinite(sample) || Math.Abs(sample) > 1) throw new InvalidDataException("Invalid audio samples.");
                }
                break;
            case "video":
                SemanticExtractor.Fields(content, ["type", "frames", "fps", "indices", "totalFrames"]);
                var frames = content.GetProperty("frames").EnumerateArray().ToArray(); var indices = content.GetProperty("indices").EnumerateArray().Select(item => item.GetInt32()).ToArray();
                var fps = content.GetProperty("fps").GetDouble(); var total = content.GetProperty("totalFrames").GetInt32();
                if (frames.Length is < 1 or > 16 || indices.Length != frames.Length || !double.IsFinite(fps) || fps is <= 0 or > 240 || total is < 1 or > 1000000 || total/fps > 60 ||
                    indices.Where((value,i) => value < 0 || value >= total || i > 0 && value <= indices[i-1]).Any()) throw new InvalidDataException("Invalid prepared video metadata.");
                foreach (var frame in frames) Image(frame);
                break;
            default: throw new InvalidDataException("Unsupported prepared content.");
        }
    }
}
