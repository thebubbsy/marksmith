using System.Runtime.InteropServices;
using MarkSmith.Models;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace MarkSmith.Services;

/// <summary>Where a dialog opens the first time it's used for its purpose.</summary>
public enum StartFolder { Documents, Pictures }

/// <summary>
/// The window a dialog belongs to: it's modal to that window and opens over it. Pass a Window, or
/// any element inside one (a control hosted in Diagram Studio passes itself, so its dialogs open
/// over the studio rather than behind it on the main window).
/// </summary>
public readonly struct DialogOwner
{
    public IntPtr Hwnd { get; }

    private DialogOwner(IntPtr hwnd) => Hwnd = hwnd;

    public static implicit operator DialogOwner(Window window) => new(WindowNative.GetWindowHandle(window));

    public static implicit operator DialogOwner(UIElement element)
    {
        try
        {
            if (element.XamlRoot?.ContentIslandEnvironment is { } island)
            {
                var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId);
                if (hwnd != IntPtr.Zero) return new DialogOwner(hwnd);
            }
        }
        catch { }
        return App.MainAppWindow is { } main ? main : default;
    }
}

/// <summary>
/// Every open, save and folder dialog in the app. Uses the shell's own Common Item Dialog
/// in-process instead of Windows.Storage.Pickers, whose pickerhost.exe RPC broker fails with
/// 0x800706BE (RPC_S_CALL_FAILED) when the app runs elevated or in a restricted session.
///
/// What every dialog gets:
/// - its own thread (STA, as the shell dialogs require) so the window keeps painting behind it;
/// - a purpose (<see cref="Purpose"/>): Windows remembers the last folder per purpose, so picking
///   a logo doesn't open in the folder a table was last exported to;
/// - a first-use folder (Documents or Pictures), or the open document's folder when given;
/// - an OK button that says what happens ("Insert", "Import", "Export");
/// - for saves, the file always gets an extension (<see cref="FileDialogRules.EnsureExtension"/>).
///
/// Save dialogs list their formats with <see cref="FileType.SaveSpec"/> ("*.svg;*.*") so the
/// folder view is never filtered: on a real PC (Windhawk injected, Windows Search disabled) a save
/// dialog showing a filtered folder froze at "Working on it…" before it drew, exactly as WinForms'
/// SaveFileDialog does there.
/// </summary>
public static class NativeFilePicker
{
    /// <summary>The remembered-folder groups. Keep this list short: one per kind of file.</summary>
    public static class Purpose
    {
        public const string Documents = "documents";
        public const string Images = "images";
        public const string Fonts = "fonts";
        public const string Templates = "templates";
        public const string Spreadsheets = "spreadsheets";
        public const string Exports = "exports";
        public const string AutomationFolders = "automation-folders";
        public const string Galaxy = "galaxy";
    }

    private static int _dialogOpen;

    public static Task<string?> PickOpenFileAsync(
        DialogOwner owner, string title, string purpose, IReadOnlyList<FileType> types,
        string? okLabel = null, StartFolder start = StartFolder.Documents, string? folder = null)
        => ShowAsync(new Request(owner, title, purpose, types, okLabel, start, folder, Save: false, PickFolder: false, SuggestedName: null));

    public static Task<string?> PickSaveFileAsync(
        DialogOwner owner, string title, string purpose, string? suggestedName, IReadOnlyList<FileType> types,
        string? okLabel = null, StartFolder start = StartFolder.Documents, string? folder = null)
        => ShowAsync(new Request(owner, title, purpose, types, okLabel, start, folder, Save: true, PickFolder: false, SuggestedName: suggestedName));

    public static Task<string?> PickFolderAsync(
        DialogOwner owner, string title, string purpose,
        string? okLabel = "Select folder", StartFolder start = StartFolder.Documents, string? folder = null)
        => ShowAsync(new Request(owner, title, purpose, Array.Empty<FileType>(), okLabel, start, folder, Save: false, PickFolder: true, SuggestedName: null));

    /// <summary>The folder of a file that exists on disk, for "start next to the open document".</summary>
    public static string? FolderOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private sealed record Request(
        DialogOwner Owner, string Title, string Purpose, IReadOnlyList<FileType> Types, string? OkLabel,
        StartFolder Start, string? Folder, bool Save, bool PickFolder, string? SuggestedName);

    private static async Task<string?> ShowAsync(Request request)
    {
        // A second click on a Browse button while a dialog is still opening would stack a second
        // dialog behind the first; one at a time.
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1) return null;
        try
        {
            var hwnd = request.Owner.Hwnd;
            if (hwnd == IntPtr.Zero && App.MainAppWindow is { } main) hwnd = WindowNative.GetWindowHandle(main);
            return await RunOnStaThread(() => Show(hwnd, request));
        }
        catch (Exception ex)
        {
            ReportFailure(request, ex);
            return null;
        }
        finally
        {
            Volatile.Write(ref _dialogOpen, 0);
        }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "MarkSmith file dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

    private static string? Show(IntPtr hwnd, Request request)
    {
        using var dialog = Dialog.Create(request.Save);

        var options = dialog.GetOptions() | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
        if (request.PickFolder) options |= FOS_PICKFOLDERS;
        else if (request.Save) options |= FOS_OVERWRITEPROMPT;
        else options |= FOS_FILEMUSTEXIST;
        dialog.SetOptions(options);

        dialog.SetClientGuid(FileDialogRules.ClientGuid(request.Purpose));
        if (!string.IsNullOrEmpty(request.Title)) dialog.SetTitle(request.Title);
        if (!string.IsNullOrEmpty(request.OkLabel)) dialog.SetOkButtonLabel(request.OkLabel);
        dialog.SetDefaultFolder(KnownFolderPath(request.Start));
        dialog.SetFolder(request.Folder);

        if (request.Types.Count > 0)
        {
            dialog.SetFileTypes(request.Types, request.Save);
            dialog.SetFileTypeIndex(1);
        }
        if (request.Save)
        {
            // Set once, the dialog keeps the default extension in step with the chosen type.
            if (FileDialogRules.DefaultExtension(request.Types) is { } ext) dialog.SetDefaultExtension(ext);
            if (!string.IsNullOrEmpty(request.SuggestedName)) dialog.SetFileName(request.SuggestedName);
        }

        var hr = dialog.Show(hwnd);
        if (hr == HRESULT_CANCELLED) return null;
        Marshal.ThrowExceptionForHR(hr);

        var path = dialog.GetResultPath();
        if (string.IsNullOrEmpty(path)) return null;
        return request.Save ? FileDialogRules.EnsureExtension(path, request.Types) : path;
    }

    // A dialog is never silently missing: if Windows can't show one, the status bar says so.
    private static void ReportFailure(Request request, Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[NativeFilePicker] '{request.Title}' failed: {ex}");
        try
        {
            App.ViewModel.StatusText = $"Windows couldn't show the “{request.Title}” dialog: {ex.Message}";
            App.ViewModel.StatusSeverity = StatusSeverity.Error;
        }
        catch { }
    }

    private static string? KnownFolderPath(StartFolder start)
    {
        var path = Environment.GetFolderPath(start == StartFolder.Pictures
            ? Environment.SpecialFolder.MyPictures
            : Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;

    /// <summary>
    /// The Common Item Dialog, called through its vtable: IFileOpenDialog and IFileSaveDialog both
    /// start with IFileDialog's slots (3-26), so one small slot table drives either dialog without
    /// re-declaring three COM interfaces. The IIDs are the shobjidl ones (System.Windows.Forms ships
    /// the same); the first version of this class had invented IIDs for IFileDialog and
    /// IFileOpenDialog, so every dialog silently fell back to comdlg32's GetOpenFileName.
    /// </summary>
    private sealed class Dialog : IDisposable
    {
        private static readonly Guid ClsidOpen = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        private static readonly Guid ClsidSave = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
        private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IidFileDialog = new("42f85136-db7e-439c-85f1-e4075d135fc8");
        private static readonly Guid IidFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");
        private static readonly Guid IidFileSaveDialog = new("84bccd23-5fde-4cdb-aea4-af64b83d78ab");
        private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        private readonly IntPtr _p;
        private readonly List<IntPtr> _allocations = new();

        private Dialog(IntPtr p) => _p = p;

        public static Dialog Create(bool save)
        {
            var clsid = save ? ClsidSave : ClsidOpen;
            var iunk = IidUnknown;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iunk, out var unk));
            try
            {
                foreach (var candidate in new[] { save ? IidFileSaveDialog : IidFileOpenDialog, IidFileDialog })
                {
                    var iid = candidate;
                    if (Marshal.QueryInterface(unk, ref iid, out var p) == 0) return new Dialog(p);
                }
                throw new COMException("The Windows file dialog is unavailable.", unchecked((int)0x80004002));
            }
            finally { Marshal.Release(unk); }
        }

        private static T Slot<T>(IntPtr target, int slot) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(target), slot * IntPtr.Size));

        public int Show(IntPtr hwnd) => Slot<ShowFn>(_p, 3)(_p, hwnd);

        public void SetFileTypes(IReadOnlyList<FileType> types, bool save)
        {
            // COMDLG_FILTERSPEC[]: pairs of string pointers, kept alive until the dialog is gone.
            var array = Marshal.AllocCoTaskMem(types.Count * 2 * IntPtr.Size);
            _allocations.Add(array);
            for (var i = 0; i < types.Count; i++)
            {
                var name = Marshal.StringToCoTaskMemUni(types[i].Label);
                var spec = Marshal.StringToCoTaskMemUni(save ? types[i].SaveSpec : types[i].Spec);
                _allocations.Add(name);
                _allocations.Add(spec);
                Marshal.WriteIntPtr(array, 2 * i * IntPtr.Size, name);
                Marshal.WriteIntPtr(array, (2 * i + 1) * IntPtr.Size, spec);
            }
            Check(Slot<SetFileTypesFn>(_p, 4)(_p, (uint)types.Count, array));
        }

        public void SetFileTypeIndex(uint index) => Check(Slot<UIntFn>(_p, 5)(_p, index));
        public void SetOptions(uint options) => Check(Slot<UIntFn>(_p, 9)(_p, options));

        public uint GetOptions()
        {
            Check(Slot<GetUIntFn>(_p, 10)(_p, out var options));
            return options;
        }

        public void SetDefaultFolder(string? path) => WithShellItem(path, item => Slot<PtrFn>(_p, 11)(_p, item));
        public void SetFolder(string? path) => WithShellItem(path, item => Slot<PtrFn>(_p, 12)(_p, item));
        public void SetFileName(string name) => Check(Slot<StringFn>(_p, 15)(_p, name));
        public void SetTitle(string title) => Check(Slot<StringFn>(_p, 17)(_p, title));
        public void SetOkButtonLabel(string label) => Check(Slot<StringFn>(_p, 18)(_p, label));
        public void SetDefaultExtension(string ext) => Check(Slot<StringFn>(_p, 22)(_p, ext));
        public void SetClientGuid(Guid guid) => Check(Slot<GuidFn>(_p, 24)(_p, ref guid));

        public string? GetResultPath()
        {
            Check(Slot<GetPtrFn>(_p, 20)(_p, out var item));
            try
            {
                // IShellItem::GetDisplayName(SIGDN_FILESYSPATH)
                Check(Slot<DisplayNameFn>(item, 5)(item, 0x80058000, out var name));
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.Release(item); }
        }

        // Folders are hints: one that's gone or unreachable is skipped, never an error.
        private static void WithShellItem(string? path, Func<IntPtr, int> use)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            var iid = IidShellItem;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0 || item == IntPtr.Zero) return;
            try { use(item); }
            finally { Marshal.Release(item); }
        }

        private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

        public void Dispose()
        {
            Marshal.Release(_p);
            foreach (var p in _allocations) Marshal.FreeCoTaskMem(p);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ShowFn(IntPtr self, IntPtr hwnd);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFileTypesFn(IntPtr self, uint count, IntPtr specs);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UIntFn(IntPtr self, uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUIntFn(IntPtr self, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtrFn(IntPtr self, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPtrFn(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StringFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GuidFn(IntPtr self, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DisplayNameFn(IntPtr self, uint sigdn, out IntPtr name);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr item);
    }
}
