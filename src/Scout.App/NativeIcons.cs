using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Scout.App;

internal static class NativeIcons
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new();
    private static readonly SemaphoreSlim Gate = new(2);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileInfo { public nint Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, out FileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    public static Task<ImageSource?> Get(string path)
    {
        if (Cache.Count > 512) Cache.Clear();
        return Cache.GetOrAdd(path, async target =>
        {
            await Gate.WaitAsync();
            try
            {
                return await Task.Run<ImageSource?>(() =>
                {
                    if (target.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(target)) return null;
                    if (SHGetFileInfo(target, 0, out var info, (uint)Marshal.SizeOf<FileInfo>(), 0x100) == 0 || info.Icon == 0) return null;
                    try { var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(32, 32)); image.Freeze(); return image; }
                    finally { DestroyIcon(info.Icon); }
                });
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or ArgumentException or COMException) { return null; }
            finally { Gate.Release(); }
        });
    }
}
