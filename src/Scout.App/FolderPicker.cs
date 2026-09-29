using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Scout.App;

/// <summary>Native Windows folder picker with no extra dependencies.</summary>
internal static class FolderPicker
{
    private const uint FOS_PICKFOLDERS = 0x20;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    /// <summary>Returns the chosen folder path, or null when the user cancels.</summary>
    public static string? Pick(Window owner, string title = "Choose a folder")
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
            dialog.SetTitle(title);
            if (dialog.Show(new WindowInteropHelper(owner).Handle) == ERROR_CANCELLED) return null;
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SIGDN.FILESYSPATH, out var path);
                return path;
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (COMException error) when (error.HResult == ERROR_CANCELLED) { return null; }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    // IFileOpenDialog shares IFileDialog's vtable layout, so only the used prefix is declared.
    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show([In] IntPtr hwndOwner);
        void SetFileTypes([In] uint cFileTypes, [In] IntPtr rgFilterSpec);
        void SetFileTypeIndex([In] uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise([In] IntPtr pfde, out uint pdwCookie);
        void Unadvise([In] uint dwCookie);
        void SetOptions([In] uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder([In] IntPtr psi);
        void SetFolder([In] IntPtr psi);
        void GetFolder([MarshalAs(UnmanagedType.Interface)] out object ppsi);
        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out object ppsi);
        void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void SetFilter([In] IntPtr pFilter);
        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
        int Compare([In] IShellItem psi, [In] uint hint, out int piOrder);
    }

    private enum SIGDN : uint { FILESYSPATH = 0x80058000 }

    [ComImport, Guid("725F6455-D786-4647-A8DA-7964F069CE13")]
    private class FileOpenDialog;
}

/// <summary>Pure folder-list editing so settings tests can cover it without a window.</summary>
internal static class FolderList
{
    public static string Append(string existing, string picked)
    {
        picked = picked.Trim();
        if (picked.Length == 0) return existing;
        var lines = existing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!lines.Contains(picked, StringComparer.OrdinalIgnoreCase)) lines.Add(picked);
        return string.Join(Environment.NewLine, lines);
    }
}
