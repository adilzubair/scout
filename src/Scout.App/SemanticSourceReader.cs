using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Scout.Core;

namespace Scout.App;

// Only this trusted adapter opens selected sources. Native parsers receive an owned copy.
internal static class SemanticSourceReader
{
    private const FileAttributes Blocked = FileAttributes.Hidden | FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000;
    public static SemanticSource? Read(string path, SemanticScope scope)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path[2..].Contains(':') ||
                new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) return null;
            var family = SemanticFormats.Family(path);
            if (family == null || !scope.Allows(path, family)) return null;
            Ancestors(path);
            using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); // attributes; share R/W/delete; no final reparse following
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return FromHandle(handle, path, family, scope);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return null; }
    }
    public static async Task<SourceSnapshot> SnapshotAsync(SemanticSource expected, SemanticScope scope, string workRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(SemanticFormats.UnsupportedReason(expected.Path) is {} unsupported)throw new UnsupportedContentException(unsupported);
        Ancestors(expected.Path);
        // Block writers and rename/delete while hashing and copying the same validated handle.
        using var source = new FileStream(expected.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = FromHandle(source.SafeFileHandle, expected.Path, expected.Family, scope);
        if (actual != expected) throw new IOException("Source changed before extraction.");
        var budget = expected.Family is "audio" or "video" ? 2L*1024*1024*1024 : 128L*1024*1024;
        if (actual.Length > budget) throw new UnsupportedContentException("source_size_limit");
        var root = Path.GetFullPath(workRoot);
        var directory = MultimodalSetup.OwnedPath(root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshot = new SourceSnapshot(directory, Path.Combine(directory, "input" + Path.GetExtension(expected.Path).ToLowerInvariant()), expected);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(directory)!);
            if (drive.AvailableFreeSpace < expected.Length + 64L*1024*1024) throw new IOException("Insufficient snapshot space.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var target = new FileStream(snapshot.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536]; long copied = 0; int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    copied += count;
                    if (copied > expected.Length) throw new IOException("Source grew during snapshot.");
                    hash.AppendData(buffer.AsSpan(0, count)); await target.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (copied != expected.Length) throw new IOException("Incomplete source snapshot.");
                await target.FlushAsync(token);
            }
            if (FromHandle(source.SafeFileHandle, expected.Path, expected.Family, scope) != expected) throw new IOException("Source changed during snapshot.");
            snapshot.ContentHash = Convert.ToHexString(hash.GetHashAndReset());
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }
    private static SemanticSource FromHandle(SafeFileHandle handle, string requested, string family, SemanticScope scope)
    {
        var basic = Information(handle, 0, 40); // creation/access/write/change times + attributes
        var identity = Information(handle, 18, 24); // 64-bit volume serial and 128-bit file ID
        var standard = Information(handle, 1, 24);
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(32));
        if ((attributes & (Blocked | FileAttributes.Directory)) != 0 || standard[20] != 0 || standard[21] != 0)
            throw new IOException("Source is not a regular local file.");
        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 0);
        if (length == 0 || length >= name.Capacity) throw new IOException("Could not validate source path.");
        var final = name.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
        else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        if (!final.Equals(requested, StringComparison.OrdinalIgnoreCase) || !scope.Allows(final, family)) throw new IOException("Source path changed or escaped scope.");
        Ancestors(final);
        var size = BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8));
        if (size < 0) throw new IOException("Invalid source length.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(final.ToUpperInvariant())); hash.AppendData(identity);
        hash.AppendData(basic.AsSpan(0, 8)); hash.AppendData(basic.AsSpan(16, 16)); hash.AppendData(standard.AsSpan(8, 8));
        return new(Candidate.Key(ItemKind.File, requested), requested, Convert.ToHexString(hash.GetHashAndReset()), family, size);
    }
    private static byte[] Information(SafeFileHandle handle, int kind, int size)
    {
        var buffer = new byte[size];
        if (!GetFileInformationByHandleEx(handle, kind, buffer, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer;
    }
    private static void Ancestors(string path)
    {
        if ((File.GetAttributes(path) & Blocked) != 0) throw new IOException("Linked, hidden or offline source.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent)
        {
            var blocked = directory.Parent == null ? Blocked & ~FileAttributes.Hidden : Blocked;
            if ((directory.Attributes & blocked) != 0) throw new IOException("Linked, hidden or offline source ancestor.");
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, [Out] byte[] buffer, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
}

internal sealed class SourceSnapshot(string directory, string path, SemanticSource source) : IDisposable
{
    public string Directory { get; } = directory;
    public string Path { get; } = path;
    public SemanticSource Source { get; } = source;
    public string ContentHash { get; internal set; } = "";
    public async Task<bool> IsCurrentAsync(SemanticScope scope, CancellationToken token)
    {
        if (SemanticSourceReader.Read(Source.Path, scope) != Source) return false;
        try
        {
            using var input = new FileStream(Source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var hash = await SHA256.HashDataAsync(input, token);
            return Convert.ToHexString(hash) == ContentHash && SemanticSourceReader.Read(Source.Path, scope) == Source;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose()
    {
        // Fixed ownership is recorded at construction; never delete a caller-selected source directory.
        var parent = System.IO.Path.GetDirectoryName(Directory)!;
        var owned = MultimodalSetup.OwnedPath(parent, System.IO.Path.GetFileName(Directory));
        // A transient Windows sharing lock must not mask a parser failure or
        // invalidate successful extraction. The owned source-* name remains a
        // recovery target; the extractor retries it before the next job/start.
        if (System.IO.Directory.Exists(owned)) SemanticExtractor.TryDeleteJob(parent, owned);
    }
}
