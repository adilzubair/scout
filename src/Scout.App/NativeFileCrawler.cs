using System.Runtime.InteropServices;
using System.Threading.Channels;
using Scout.Core;

namespace Scout.App;

internal static class NativeFileCrawler
{
    internal sealed record Entry(string Path, string Name, long Size, DateTimeOffset Created, DateTimeOffset Modified);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
    }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint First(string path, int level, out FindData data, int search, nint filter, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Next(nint handle, out FindData data);
    [DllImport("kernel32.dll", EntryPoint = "FindClose")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Close(nint handle);
    private static DateTimeOffset Time(System.Runtime.InteropServices.ComTypes.FILETIME t) => DateTimeOffset.FromFileTime(((long)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime);
    internal static bool IsAvailable(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return false;
            // Windows marks volume roots Hidden/System; those flags still exclude children.
            var isVolumeRoot = Path.GetFullPath(root).TrimEnd('\\', '/').Equals(Path.GetPathRoot(Path.GetFullPath(root))?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            var rejected = FileAttributes.ReparsePoint | FileAttributes.Offline;
            if (!isVolumeRoot) rejected |= FileAttributes.Hidden | FileAttributes.System;
            if ((File.GetAttributes(root) & rejected) != 0) return false;
            var search = Extended(Path.Combine(root,"*")); var handle = First(search,1,out _,0,0,2);
            if (handle == -1 && Marshal.GetLastWin32Error() is 87 or 50) handle = First(search,1,out _,0,0,0);
            if (handle == -1) return Marshal.GetLastWin32Error() == 2;
            Close(handle); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    private static string Extended(string path) => path.StartsWith(@"\\?\",StringComparison.Ordinal) ? path : path.StartsWith(@"\\",StringComparison.Ordinal) ? @"\\?\UNC\"+path[2..] : @"\\?\"+path;
    internal static async Task ScanAsync(string root, Func<string, bool> excluded, Action<Entry> accept, Action<string> unavailable, CancellationToken token, Action<Entry>? acceptDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        if (!IsAvailable(root)) { unavailable(root); return; }
        var queue = Channel.CreateUnbounded<(string Path, Entry? Metadata)>(new() { SingleWriter = false, SingleReader = false });
        Entry? rootMetadata = null;
        if (acceptDirectory != null)
        {
            try { var info = new DirectoryInfo(root); rootMetadata = new(root, info.Name, 0, info.CreationTimeUtc, info.LastWriteTimeUtc); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unavailable(root); return; }
        }
        var remaining = 1; queue.Writer.TryWrite((root, rootMetadata));
        await Task.WhenAll(Enumerable.Range(0, Math.Min(4, Environment.ProcessorCount)).Select(_ => Task.Run(async () =>
        {
            await foreach (var (directory, metadata) in queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    if (excluded(directory) || File.Exists(Path.Combine(directory, "pyvenv.cfg"))) continue;
                    var search = Extended(Path.Combine(directory, "*"));
                    var handle = First(search, 1, out var data, 0, 0, 2);
                    if (handle == -1 && Marshal.GetLastWin32Error() is 87 or 50) handle = First(search, 1, out data, 0, 0, 0);
                    if (handle == -1)
                    {
                        if (Marshal.GetLastWin32Error() != 2) unavailable(directory);
                        else if (metadata != null) acceptDirectory?.Invoke(metadata);
                        continue;
                    }
                    try
                    {
                        if (metadata != null) acceptDirectory?.Invoke(metadata);
                        do
                        {
                            token.ThrowIfCancellationRequested();
                            if (data.Name is "." or "..") continue;
                            var path = Path.Combine(directory, data.Name);
                            if (excluded(path) || ((FileAttributes)data.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                            if (((FileAttributes)data.Attributes & FileAttributes.Directory) != 0) {
                                Entry? child = null;
                                if (acceptDirectory != null)
                                {
                                    try { child = new(path, data.Name, 0, Time(data.Creation), Time(data.Write)); }
                                    catch (ArgumentOutOfRangeException) { }
                                }
                                Interlocked.Increment(ref remaining); queue.Writer.TryWrite((path, child));
                            }
                            else { try { accept(new(path, data.Name, ((long)data.SizeHigh << 32) | data.SizeLow, Time(data.Creation), Time(data.Write))); } catch (ArgumentOutOfRangeException) { } }
                        } while (Next(handle, out data));
                        if (Marshal.GetLastWin32Error() != 18) unavailable(directory);
                    }
                    finally { Close(handle); }
                }
                finally { if (Interlocked.Decrement(ref remaining) == 0) queue.Writer.TryComplete(); }
            }
        }, token)));
    }
}
