using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace MarkSmith.Services;

/// <summary>
/// Bulletproof native Windows file and folder picker service for WinUI 3 desktop apps.
/// Avoids the fragile UWP pickerhost.exe RPC broker (Windows.Storage.Pickers) which crashes
/// with 0x800706BE (RPC_S_CALL_FAILED) when running elevated as Administrator or in restricted
/// user sessions. Uses native in-process COM (IFileOpenDialog / IFileSaveDialog) with Win32 fallback.
/// </summary>
public static class NativeFilePicker
{
    public static Task<string?> PickOpenFileAsync(
        Window? parent,
        string title,
        params (string Name, string Spec)[] filters)
        => PickOpenFileAsync(parent, title, filters, defaultExt: null);

    public static async Task<string?> PickOpenFileAsync(
        Window? parent,
        string title,
        (string Name, string Spec)[]? filters,
        string? defaultExt = null)
    {
        return await Task.Run(() =>
        {
            IntPtr hwnd = GetHwnd(parent);
            try
            {
                var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
                try
                {
                    dialog.GetOptions(out var options);
                    dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);

                    if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                    if (!string.IsNullOrEmpty(defaultExt)) dialog.SetDefaultExtension(defaultExt.TrimStart('.'));

                    if (filters != null && filters.Length > 0)
                    {
                        var specs = filters.Select(f => new COMDLG_FILTERSPEC { pszName = f.Name, pszSpec = f.Spec }).ToArray();
                        dialog.SetFileTypes((uint)specs.Length, specs);
                    }

                    int hr = dialog.Show(hwnd);
                    if (hr == 0)
                    {
                        dialog.GetResult(out IShellItem item);
                        if (item != null)
                        {
                            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
                            return path;
                        }
                    }
                    return null;
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeFilePicker.PickOpenFileAsync] IFileOpenDialog error: {ex}");
                return FallbackGetOpenFileName(hwnd, title, filters, defaultExt);
            }
        });
    }

    public static async Task<string[]?> PickOpenFilesAsync(
        Window? parent,
        string title,
        (string Name, string Spec)[]? filters = null,
        string? defaultExt = null)
    {
        return await Task.Run(() =>
        {
            IntPtr hwnd = GetHwnd(parent);
            try
            {
                var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
                try
                {
                    dialog.GetOptions(out var options);
                    dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT);

                    if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                    if (!string.IsNullOrEmpty(defaultExt)) dialog.SetDefaultExtension(defaultExt.TrimStart('.'));

                    if (filters != null && filters.Length > 0)
                    {
                        var specs = filters.Select(f => new COMDLG_FILTERSPEC { pszName = f.Name, pszSpec = f.Spec }).ToArray();
                        dialog.SetFileTypes((uint)specs.Length, specs);
                    }

                    int hr = dialog.Show(hwnd);
                    if (hr == 0)
                    {
                        dialog.GetResults(out IShellItemArray items);
                        if (items != null)
                        {
                            items.GetCount(out uint count);
                            var list = new List<string>((int)count);
                            for (uint i = 0; i < count; i++)
                            {
                                items.GetItemAt(i, out var item);
                                if (item != null)
                                {
                                    item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string p);
                                    if (!string.IsNullOrEmpty(p)) list.Add(p);
                                    Marshal.ReleaseComObject(item);
                                }
                            }
                            Marshal.ReleaseComObject(items);
                            return list.ToArray();
                        }
                    }
                    return null;
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeFilePicker.PickOpenFilesAsync] Error: {ex}");
                var single = FallbackGetOpenFileName(hwnd, title, filters, defaultExt);
                return single != null ? new[] { single } : null;
            }
        });
    }

    public static Task<string?> PickSaveFileAsync(
        Window? parent,
        string title,
        string? suggestedFileName,
        params (string Name, string Spec)[] filters)
        => PickSaveFileAsync(parent, title, suggestedFileName, filters, defaultExt: null);

    public static async Task<string?> PickSaveFileAsync(
        Window? parent,
        string title,
        string? suggestedFileName,
        (string Name, string Spec)[]? filters,
        string? defaultExt = null)
    {
        return await Task.Run(() =>
        {
            IntPtr hwnd = GetHwnd(parent);
            try
            {
                var dialog = (IFileSaveDialog)new FileSaveDialogRCW();
                try
                {
                    dialog.GetOptions(out var options);
                    dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);

                    if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                    if (!string.IsNullOrEmpty(suggestedFileName)) dialog.SetFileName(suggestedFileName);
                    if (!string.IsNullOrEmpty(defaultExt)) dialog.SetDefaultExtension(defaultExt.TrimStart('.'));

                    if (filters != null && filters.Length > 0)
                    {
                        var specs = filters.Select(f => new COMDLG_FILTERSPEC { pszName = f.Name, pszSpec = f.Spec }).ToArray();
                        dialog.SetFileTypes((uint)specs.Length, specs);
                    }

                    int hr = dialog.Show(hwnd);
                    if (hr == 0)
                    {
                        dialog.GetResult(out IShellItem item);
                        if (item != null)
                        {
                            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
                            return path;
                        }
                    }
                    return null;
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeFilePicker.PickSaveFileAsync] Error: {ex}");
                return FallbackGetSaveFileName(hwnd, title, suggestedFileName, filters, defaultExt);
            }
        });
    }

    public static async Task<string?> PickFolderAsync(Window? parent, string title)
    {
        return await Task.Run(() =>
        {
            IntPtr hwnd = GetHwnd(parent);
            try
            {
                var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
                try
                {
                    dialog.GetOptions(out var options);
                    dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);

                    if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);

                    int hr = dialog.Show(hwnd);
                    if (hr == 0)
                    {
                        dialog.GetResult(out IShellItem item);
                        if (item != null)
                        {
                            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
                            return path;
                        }
                    }
                    return null;
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeFilePicker.PickFolderAsync] Error: {ex}");
                return null;
            }
        });
    }

    private static IntPtr GetHwnd(Window? parent)
    {
        try
        {
            if (parent != null) return WindowNative.GetWindowHandle(parent);
            if (App.MainAppWindow != null) return WindowNative.GetWindowHandle(App.MainAppWindow);
        }
        catch { }
        return IntPtr.Zero;
    }

    // ---- Win32 fallback via comdlg32.dll --------------------------------------------------------

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetSaveFileName([In, Out] OpenFileName ofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class OpenFileName
    {
        public int structSize = Marshal.SizeOf<OpenFileName>();
        public IntPtr hwndOwner = IntPtr.Zero;
        public IntPtr hInstance = IntPtr.Zero;
        public string? filter = null;
        public string? customFilter = null;
        public int maxCustFilter = 0;
        public int filterIndex = 0;
        public string? file = null;
        public int maxFile = 0;
        public string? fileTitle = null;
        public int maxFileTitle = 0;
        public string? initialDir = null;
        public string? title = null;
        public int flags = 0;
        public short fileOffset = 0;
        public short fileExtension = 0;
        public string? defExt = null;
        public IntPtr custData = IntPtr.Zero;
        public IntPtr fnHook = IntPtr.Zero;
        public string? templateName = null;
        public IntPtr reservedPtr = IntPtr.Zero;
        public int reservedInt = 0;
        public int flagsEx = 0;
    }

    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;

    private static string FormatFilterString((string Name, string Spec)[]? filters)
    {
        if (filters == null || filters.Length == 0) return "All Files (*.*)\0*.*\0\0";
        var sb = new System.Text.StringBuilder();
        foreach (var (name, spec) in filters)
        {
            sb.Append(name);
            sb.Append('\0');
            sb.Append(spec);
            sb.Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }

    private static string? FallbackGetOpenFileName(IntPtr hwnd, string title, (string Name, string Spec)[]? filters, string? defaultExt)
    {
        try
        {
            var ofn = new OpenFileName
            {
                hwndOwner = hwnd,
                filter = FormatFilterString(filters),
                file = new string(new char[2048]),
                maxFile = 2048,
                title = title,
                flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST,
                defExt = defaultExt?.TrimStart('.')
            };
            if (GetOpenFileName(ofn)) return ofn.file;
        }
        catch { }
        return null;
    }

    private static string? FallbackGetSaveFileName(IntPtr hwnd, string title, string? suggestedFileName, (string Name, string Spec)[]? filters, string? defaultExt)
    {
        try
        {
            var initial = (suggestedFileName ?? "").PadRight(2048, '\0');
            var ofn = new OpenFileName
            {
                hwndOwner = hwnd,
                filter = FormatFilterString(filters),
                file = initial,
                maxFile = 2048,
                title = title,
                flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT,
                defExt = defaultExt?.TrimStart('.')
            };
            if (GetSaveFileName(ofn)) return ofn.file?.TrimEnd('\0');
        }
        catch { }
        return null;
    }

    // ---- COM P/Invoke declarations -------------------------------------------------------------

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B"), ClassInterface(ClassInterfaceType.None)]
    private class FileSaveDialogRCW { }

    [ComImport, Guid("42f85109-d1e2-4392-8649-0d888842127b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FILEOPENDIALOGOPTIONS fos);
        void GetOptions(out FILEOPENDIALOGOPTIONS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("d57c72be-8860-470e-9103-1e6d60e43e16"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        [PreserveSig] new int Show(IntPtr parent);
        new void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        new void SetFileTypeIndex(uint iFileType);
        new void GetFileTypeIndex(out uint piFileType);
        new void Advise(IntPtr pfde, out uint pdwCookie);
        new void Unadvise(uint dwCookie);
        new void SetOptions(FILEOPENDIALOGOPTIONS fos);
        new void GetOptions(out FILEOPENDIALOGOPTIONS pfos);
        new void SetDefaultFolder(IShellItem psi);
        new void SetFolder(IShellItem psi);
        new void GetFolder(out IShellItem ppsi);
        new void GetCurrentSelection(out IShellItem ppsi);
        new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        new void GetResult(out IShellItem ppsi);
        new void AddPlace(IShellItem psi, int fdap);
        new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        new void Close(int hr);
        new void SetClientGuid(ref Guid guid);
        new void ClearClientData();
        new void SetFilter(IntPtr pFilter);
        void GetResults(out IShellItemArray penum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("84bccd23-5fde-4cdb-aea4-af64b83d78ab"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog : IFileDialog
    {
        [PreserveSig] new int Show(IntPtr parent);
        new void SetFileTypes(uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        new void SetFileTypeIndex(uint iFileType);
        new void GetFileTypeIndex(out uint piFileType);
        new void Advise(IntPtr pfde, out uint pdwCookie);
        new void Unadvise(uint dwCookie);
        new void SetOptions(FILEOPENDIALOGOPTIONS fos);
        new void GetOptions(out FILEOPENDIALOGOPTIONS pfos);
        new void SetDefaultFolder(IShellItem psi);
        new void SetFolder(IShellItem psi);
        new void GetFolder(out IShellItem ppsi);
        new void GetCurrentSelection(out IShellItem ppsi);
        new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        new void GetResult(out IShellItem ppsi);
        new void AddPlace(IShellItem psi, int fdap);
        new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        new void Close(int hr);
        new void SetClientGuid(ref Guid guid);
        new void ClearClientData();
        new void SetFilter(IntPtr pFilter);
        void SetSaveAsItem(IShellItem psi);
        void SetProperties(IntPtr pStore);
        void SetCollectedProperties(IntPtr pList, int fAppendDefault);
        void GetProperties(out IntPtr ppStore);
        void ApplyProperties(IShellItem psi, IntPtr pStore, IntPtr hwnd, IntPtr pSink);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [Flags]
    private enum FILEOPENDIALOGOPTIONS : uint
    {
        FOS_OVERWRITEPROMPT = 0x00000002,
        FOS_STRICTFILETYPES = 0x00000004,
        FOS_NOCHANGEDIR = 0x00000008,
        FOS_PICKFOLDERS = 0x00000020,
        FOS_FORCEFILESYSTEM = 0x00000040,
        FOS_ALLNONSTORAGEITEMS = 0x00000080,
        FOS_NOVALIDATE = 0x00000100,
        FOS_ALLOWMULTISELECT = 0x00000200,
        FOS_PATHMUSTEXIST = 0x00000800,
        FOS_FILEMUSTEXIST = 0x00001000,
        FOS_CREATEPROMPT = 0x00002000,
        FOS_SHAREAWARE = 0x00004000,
        FOS_NOREADONLYRETURN = 0x00008000,
        FOS_NOTESTFILECREATE = 0x00010000,
        FOS_HIDEMRUPLACES = 0x00020000,
        FOS_HIDEPINNEDPLACES = 0x00040000,
        FOS_NODEREFERENCELINKS = 0x00100000,
        FOS_OKBUTTONNEEDSINTERACTION = 0x00200000,
        FOS_DONTADDTORECENT = 0x02000000,
        FOS_FORCESHOWHIDDEN = 0x10000000,
        FOS_DEFAULTNOMINIMODE = 0x20000000,
        FOS_FORCEPREVIEWPANEON = 0x40000000,
        FOS_SUPPORTSTREAMABLEITEMS = 0x80000000
    }

    private enum SIGDN : uint
    {
        SIGDN_NORMALDISPLAY = 0x00000000,
        SIGDN_PARENTRELATIVEPARSING = 0x80018001,
        SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000,
        SIGDN_PARENTRELATIVEEDITING = 0x80031001,
        SIGDN_DESKTOPABSOLUTEEDITING = 0x8004c000,
        SIGDN_FILESYSPATH = 0x80058000,
        SIGDN_URL = 0x80068000,
        SIGDN_PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
    }
}
