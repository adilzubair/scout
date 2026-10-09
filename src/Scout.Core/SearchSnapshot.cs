using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scout.Core;

internal unsafe sealed class PackedRows : IEnumerable<CatalogRow>
{
    private CatalogRow[]? array;
    private MemoryMappedFile? map;
    private MemoryMappedViewAccessor? view;
    private byte* address;
    private string? file;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Readers = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Retired = new();
    public bool IsMapped => array == null;
    public int Length { get; }
    public PackedRows(CatalogRow[] rows) { array = rows; Length = rows.Length; }
    public PackedRows(string path, long offset, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* pointer = null; view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        address = pointer + view.PointerOffset + offset; Length = count; file = path; Readers.AddOrUpdate(path, 1, (_, n) => n + 1);
    }
    public ref readonly CatalogRow this[int index]
    {
        get { if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException(); if (array != null) return ref array[index]; return ref Unsafe.AsRef<CatalogRow>(address + (long)index * Unsafe.SizeOf<CatalogRow>()); }
    }
    public void SetOrder(int i, int order) { if (array == null) throw new InvalidOperationException(); array[i].Order = order; }
    public static void Retire(string path) { Retired[path] = 0; Cleanup(path); }
    private static void Cleanup(string path) { if (Readers.GetValueOrDefault(path) > 0 || !Retired.ContainsKey(path)) return; try { File.Delete(path); Retired.TryRemove(path, out _); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public IEnumerator<CatalogRow> GetEnumerator() { for (var i = 0; i < Length; i++) yield return this[i]; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    ~PackedRows()
    {
        if (view != null) { view.SafeMemoryMappedViewHandle.ReleasePointer(); view.Dispose(); map?.Dispose(); }
        if (file != null) { Readers.AddOrUpdate(file, 0, (_, n) => n - 1); Cleanup(file); }
    }
}

public sealed class SearchSnapshot
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private const string Magic = "SCOUTIDX4";
    public static string Fingerprint(IEnumerable<string> roots, IEnumerable<string> exclusions) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', roots.Select(Path.GetFullPath).Select(p => p.ToUpperInvariant()).Order().Prepend($"policy:{IndexPolicy.CacheVersion}").Concat(["--"]).Concat(exclusions.Select(Path.GetFullPath).Select(p => p.ToUpperInvariant()).Order())))));
    internal sealed record Manifest(string File, long Generation, string Fingerprint);
    public static SearchCatalog Load(ScoutStore store, string[] roots, string[] exclusions, bool reconcileCoverage = false)
    {
        lock (Gates.GetOrAdd(store.IndexDirectory, _ => new object())) return LoadCore(store, roots, exclusions, reconcileCoverage);
    }
    private static SearchCatalog LoadCore(ScoutStore store, string[] roots, string[] exclusions, bool reconcileCoverage)
    {
        var fingerprint = Fingerprint(roots, exclusions);
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(store.IndexDirectory, "manifest.json")))!;
            if (manifest.Fingerprint != fingerprint || manifest.Generation > store.IndexGeneration || manifest.Generation < store.IndexJournalFloor || Path.GetFileName(manifest.File) != manifest.File) throw new InvalidDataException();
            var path = Path.Combine(store.IndexDirectory, manifest.File);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length < 48 || input.Length > 8L * 1024 * 1024 * 1024) throw new InvalidDataException();
            var end = input.Length - 32;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long left = end;
            while (left > 0) { var n = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length)); if (n == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, n); left -= n; }
            var checksum = new byte[32]; input.ReadExactly(checksum); if (!CryptographicOperations.FixedTimeEquals(checksum, hash.GetHashAndReset())) throw new InvalidDataException();
            input.Position = 0; using var reader = new BinaryReader(input, Encoding.UTF8, true);
            if (reader.ReadString() != Magic || reader.ReadInt64() != manifest.Generation || reader.ReadString() != fingerprint) throw new InvalidDataException();
            int Count(int max) { var n = reader.ReadInt32(); if (n < 0 || n > max || n > end - input.Position) throw new InvalidDataException(); return n; }
            var strings = new string[Count(20_000_000)];
            for (var i = 0; i < strings.Length; i++) strings[i] = reader.ReadString();
            var texts = JsonSerializer.Deserialize<SearchText[]>(reader.ReadBytes(Count(256_000_000)))!;
            var folders = JsonSerializer.Deserialize<string[][]>(reader.ReadBytes(Count(256_000_000)))!;
            var count = Count(10_000_000); var offset = input.Position;
            if (offset + (long)count * Unsafe.SizeOf<CatalogRow>() != end) throw new InvalidDataException();
            var data = new CatalogData { Strings = strings, Texts = texts, FolderWords = folders, Rows = new PackedRows(path, offset, count) };
            bool StringIndex(int i) => (uint)i < strings.Length;
            bool Ticks(long t) => t >= 0 && t <= DateTimeOffset.MaxValue.UtcTicks;
            foreach (var row in data.Rows)
            {
                if (!StringIndex(row.Id) || !StringIndex(row.Title) || !StringIndex(row.Detail) || !StringIndex(row.Directory) || !StringIndex(row.Source) || !StringIndex(row.AddedSource) || !StringIndex(row.OpenedSource) || !StringIndex(row.DownloadSource)) throw new InvalidDataException();
                if (row.Target < -1 || row.Target >= strings.Length || (uint)row.Text >= texts.Length || (uint)row.Folder >= folders.Length || !Enum.IsDefined(row.Kind)) throw new InvalidDataException();
                if (!Ticks(row.Modified) || !Ticks(row.Created) || !Ticks(row.Added) || !Ticks(row.Opened) || !Ticks(row.Downloaded) || !Ticks(row.Visited)) throw new InvalidDataException();
            }
            data.Prepare();
            var catalog = new SearchCatalog(data, roots);
            var delta = store.ReadIndexChanges(manifest.Generation);
            var eligible = delta.Changed.Where(c => c.Kind is (ItemKind.File or ItemKind.Folder) && !IndexPolicy.Excluded(c.Target, exclusions) && roots.Any(r => IndexPolicy.Within(c.Target, r))).ToArray();
            var eligibleIds = eligible.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            return catalog.WithChanges(eligible, delta.Removed.Concat(delta.Changed.Where(c => !eligibleIds.Contains(c.Id)).Select(c => c.Id)));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException or NullReferenceException)
        {
            var removed = new List<string>();
            bool Eligible(Candidate candidate)
            {
                if (candidate.Kind is not (ItemKind.File or ItemKind.Folder)) return false;
                if (!IndexPolicy.Excluded(candidate.Target, exclusions) && roots.Any(root => IndexPolicy.Within(candidate.Target,root))) return true;
                removed.Add(candidate.Id); return false;
            }
            var rebuilt = new SearchCatalog(store.StreamCandidates().Where(Eligible), roots: roots);
            if (reconcileCoverage && removed.Count > 0) store.UpdateIndex([],removed);
            return rebuilt;
        }
    }
    public static void Save(ScoutStore store, SearchCatalog catalog, long generation, string[] roots, string[] exclusions)
    {
        lock (Gates.GetOrAdd(store.IndexDirectory, _ => new object())) SaveCore(store, catalog, generation, roots, exclusions);
    }
    private static void SaveCore(ScoutStore store, SearchCatalog catalog, long generation, string[] roots, string[] exclusions)
    {
        Directory.CreateDirectory(store.IndexDirectory);
        var fingerprint = Fingerprint(roots, exclusions);
        try
        {
            var published = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(store.IndexDirectory, "manifest.json")));
            if (published?.Generation > generation) return; // A late compaction cannot move the checkpoint backwards.
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        var file = $"index-{generation}-{Guid.NewGuid():N}.bin"; var path = Path.Combine(store.IndexDirectory, file); var temp = path + ".tmp";
        var data = catalog.Base;
        if (catalog.PendingCount != 0) data = catalog.Compact().Base;
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(generation); writer.Write(fingerprint); writer.Write(data.Strings.Length);
                foreach (var s in data.Strings) writer.Write(s);
                foreach (var bytes in new[] { JsonSerializer.SerializeToUtf8Bytes(data.Texts), JsonSerializer.SerializeToUtf8Bytes(data.FolderWords) }) { writer.Write(bytes.Length); writer.Write(bytes); }
                writer.Write(data.Rows.Length);
                for (var i = 0; i < data.Rows.Length; i++) { var row = data.Rows[i]; writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref row, 1))); }
            }
            stream.Flush(); stream.Position = 0; var checksum = SHA256.HashData(stream); stream.Write(checksum); stream.Flush(true);
        }
        File.Move(temp, path);
        var manifest = Path.Combine(store.IndexDirectory, "manifest.json"); string? old = null;
        try { old = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest))?.File; } catch (Exception e) when (e is IOException or JsonException) { }
        using (var manifestStream = new FileStream(manifest + ".tmp", FileMode.Create, FileAccess.Write))
        { JsonSerializer.Serialize(manifestStream, new Manifest(file, generation, fingerprint)); manifestStream.Flush(true); }
        File.Move(manifest + ".tmp", manifest, true);
        store.PruneIndexJournal(generation);
        if (old != null && Path.GetFileName(old) == old)
        {
            var oldPath = Path.Combine(store.IndexDirectory, old);
            // A mapped search owns the old rows until its catalog/lease is collected.
            PackedRows.Retire(oldPath);
        }
    }
}
