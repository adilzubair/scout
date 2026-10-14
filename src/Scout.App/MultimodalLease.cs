using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scout.App;

// Shared runtime leases conflict with the install/remove entry points' exclusive
// byte-range locks. More than one owned model/parser can hold a shared lease.
internal sealed class MultimodalLease : IDisposable
{
    private readonly List<FileStream> files = [];
    private bool disposed;
    public MultimodalLease(string root)
    {
        try
        {
            foreach (var name in new[] { "bootstrap.lock", "setup.lock" })
            {
                var file = new FileStream(MultimodalSetup.OwnedPath(root,name), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                var offset = new NativeOverlapped();
                if (!LockFileEx(file.SafeFileHandle, 1, 0, 1, 0, ref offset)) // FAIL_IMMEDIATELY, shared
                {
                    var error = Marshal.GetLastWin32Error(); file.Dispose();
                    throw new IOException("Add-on installation or removal is active.", new Win32Exception(error));
                }
                files.Add(file);
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if(disposed) return; disposed = true;
        foreach(var file in files.AsEnumerable().Reverse()) file.Dispose();
        files.Clear();
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(SafeFileHandle file,uint flags,uint reserved,uint low,uint high,ref NativeOverlapped offset);
}
