using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Dialogs;

/// <summary>
/// File and folder pickers over the Vista-era COM common-item dialog
/// (<c>IFileOpenDialog</c>/<c>IFileSaveDialog</c> + <c>IShellItem</c>), the modern replacement for the
/// <c>GetOpenFileName</c> common-dialog API. Each call <c>CoCreateInstance</c>s a fresh dialog object, configures it,
/// shows it modally on the supplied owner window, and projects the picked <c>IShellItem</c>(s) to filesystem paths —
/// the same flat call-OUT COM shape the WIC codec uses (<c>FluentGpu.Windows/Wic/WicImageCodec.cs:28-32</c>): a
/// hand-declared CLSID, <c>__uuidof&lt;T&gt;()</c> for the IID, then <c>iface-&gt;Method(...)</c> through TerraFX's
/// prebuilt vtable structs. Zero CsWinRT, zero <c>ComWrappers</c>, zero reflection — AOT-clean.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading / HWND ownership (read before calling).</b> Every method here is <b>modal and blocking</b>:
/// <c>IModalWindow.Show(hwnd)</c> runs a nested message loop and does not return until the user commits or cancels.
/// Call these on the <b>UI thread</b> that owns <c>ownerHwnd</c> (the FluentGpu window's HWND — obtained
/// from the host/window accessor, never invented on the Engine seam); the dialog parents to that window and disables
/// it for the dialog's lifetime. Passing <c>0</c>/<see cref="nint.Zero"/> as the owner makes the dialog unowned
/// (top-level, not modal to any window) — allowed but discouraged for an app window. Do NOT call from a render or
/// worker thread.
/// </para>
/// <para>
/// <b>Apartment.</b> The common-item dialog requires an STA. <see cref="EnsureSta"/> calls
/// <c>CoInitializeEx(COINIT_APARTMENTTHREADED)</c> once per thread; a UI thread that already initialized STA gets the
/// benign <c>S_FALSE</c>/<c>RPC_E_CHANGED_MODE</c> which is tolerated (it does not re-initialize or balance with
/// <c>CoUninitialize</c> — the host owns apartment teardown).
/// </para>
/// <para>
/// <b>Cancellation.</b> When the user dismisses the dialog, <c>Show</c> returns
/// <c>HRESULT_FROM_WIN32(ERROR_CANCELLED)</c> (<c>0x800704C7</c>); every method maps that to a <see langword="null"/>
/// / empty result rather than throwing. Any other failed <c>HRESULT</c> throws
/// <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// <b>Filters.</b> A filter is a <c>(Name, Spec)</c> pair, e.g. <c>("Audio", "*.mp3;*.flac;*.wav")</c> or
/// <c>("All files", "*.*")</c> — the same shape as <c>COMDLG_FILTERSPEC { pszName, pszSpec }</c>. The spec syntax is
/// the shell's semicolon-separated wildcard list. With no filters supplied the dialog shows all files.
/// </para>
/// <para>
/// <b>Path projection.</b> Results come back as <c>IShellItem</c>; this asks for
/// <c>GetDisplayName(SIGDN_FILESYSPATH)</c> which yields a real filesystem path for filesystem items. Combined with the
/// <c>FOS_FORCEFILESYSTEM</c> option (always set), non-filesystem shell locations (e.g. a search results virtual
/// folder) are excluded, so the returned string is always a usable path. The path buffer is OS-allocated and freed
/// with <c>CoTaskMemFree</c>.
/// </para>
/// <para>
/// References:
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileopendialog">IFileOpenDialog</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifiledialog-setoptions">IFileDialog::SetOptions (FILEOPENDIALOGOPTIONS)</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitem-getdisplayname">IShellItem::GetDisplayName (SIGDN)</see> — <c>SIGDN_FILESYSPATH</c></item>
/// <item>CLSIDs from the Windows SDK <c>ShObjIdl_core.h</c> (TerraFX exposes the interfaces but not the coclass CLSIDs as fields — see the <c>Clsid</c> block).</item>
/// </list>
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")] // The common-item dialog shipped in Windows Vista.
public static unsafe class FilePicker
{
    /// <summary>Default dialog title used when a caller does not supply one.</summary>
    private const string DefaultOpenTitle = "Open";
    private const string DefaultSaveTitle = "Save As";
    private const string DefaultFolderTitle = "Select Folder";

    // wincred-style local #defines TerraFX does not project as fields (the win32-error codes carry no projection).
    private const int ERROR_CANCELLED = 1223;                 // winerror.h — user dismissed the dialog.

    // Benign CoInitializeEx result on a thread already initialized to a different model (gate on FAILED, not != S_OK).
    // S_FALSE (already-initialized, same model) is a positive HRESULT, so the `hr < 0` check already tolerates it.
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    // CLSIDs of the dialog coclasses (Windows SDK ShObjIdl_core.h). TerraFX projects the interfaces and the empty
    // coclass marker types (FileOpenDialog/FileSaveDialog) but NOT a CLSID_* GUID field, so they are restated here in
    // the house style (cf. FluentGpu.Windows/Wic/WicImageCodec.cs:17). __uuidof<FileOpenDialog>() would also yield this
    // value; the literal is used to keep the CoCreateInstance call self-evident.
    private static class Clsid
    {
        // CLSID_FileOpenDialog {DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7}
        public static readonly Guid FileOpenDialog =
            new(0xDC1C5A9C, 0xE88A, 0x4DDE, 0xA5, 0xA1, 0x60, 0xF8, 0x2A, 0x20, 0xAE, 0xF7);
        // CLSID_FileSaveDialog {C0B4E2F3-BA21-4773-8DBA-335EC946EB8B}
        public static readonly Guid FileSaveDialog =
            new(0xC0B4E2F3, 0xBA21, 0x4773, 0x8D, 0xBA, 0x33, 0x5E, 0xC9, 0x46, 0xEB, 0x8B);
    }

    /// <summary>
    /// Show a modal open-file dialog and return the chosen file's full path, or <see langword="null"/> if the user
    /// cancelled. Single selection.
    /// </summary>
    /// <param name="ownerHwnd">The owner window handle (the FluentGpu window). <c>0</c> for an unowned dialog. The call
    /// blocks this thread until the dialog closes; it MUST be the UI thread that owns the window.</param>
    /// <param name="title">The dialog title bar text.</param>
    /// <param name="filters">Optional file-type filters, e.g. <c>("Audio", "*.mp3;*.flac")</c>. None = all files.</param>
    /// <returns>The selected file path, or <see langword="null"/> on cancel.</returns>
    /// <exception cref="InvalidOperationException">A COM step failed for a reason other than user cancellation.</exception>
    public static string? OpenFile(nint ownerHwnd, string title = DefaultOpenTitle,
                                   params (string Name, string Spec)[] filters)
    {
        EnsureSta();
        IFileOpenDialog* dialog = CreateOpenDialog();
        try
        {
            ConfigureOpen(dialog, title, filters,
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST |
                FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);

            if (!Show((IModalWindow*)dialog, ownerHwnd))
                return null;

            IShellItem* item = null;
            ThrowIfFailed(dialog->GetResult(&item), "IFileOpenDialog.GetResult");
            try { return GetPath(item); }
            finally { if (item != null) item->Release(); }
        }
        finally { dialog->Release(); }
    }

    /// <summary>
    /// Show a modal open-file dialog allowing multiple selection (<c>FOS_ALLOWMULTISELECT</c>) and return every chosen
    /// path. An empty array means the user cancelled (or, defensively, selected nothing).
    /// </summary>
    /// <param name="ownerHwnd">The owner window handle (UI thread; blocks). <c>0</c> for unowned.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="filters">Optional file-type filters. None = all files.</param>
    /// <returns>The selected paths (possibly empty); never <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">A COM step failed for a reason other than user cancellation.</exception>
    public static string[] OpenFiles(nint ownerHwnd, string title = DefaultOpenTitle,
                                     params (string Name, string Spec)[] filters)
    {
        EnsureSta();
        IFileOpenDialog* dialog = CreateOpenDialog();
        try
        {
            ConfigureOpen(dialog, title, filters,
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST |
                FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT);

            if (!Show((IModalWindow*)dialog, ownerHwnd))
                return Array.Empty<string>();

            IShellItemArray* results = null;
            ThrowIfFailed(dialog->GetResults(&results), "IFileOpenDialog.GetResults");
            try
            {
                uint count = 0;
                ThrowIfFailed(results->GetCount(&count), "IShellItemArray.GetCount");
                if (count == 0)
                    return Array.Empty<string>();

                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    IShellItem* item = null;
                    ThrowIfFailed(results->GetItemAt(i, &item), "IShellItemArray.GetItemAt");
                    try
                    {
                        string? p = TryGetPath(item);
                        if (p is not null) paths.Add(p);
                    }
                    finally { if (item != null) item->Release(); }
                }
                return paths.ToArray();
            }
            finally { if (results != null) results->Release(); }
        }
        finally { dialog->Release(); }
    }

    /// <summary>
    /// Show a modal save-file dialog and return the chosen target path (with overwrite confirmation), or
    /// <see langword="null"/> if cancelled. The path is returned whether or not the file already exists — the caller
    /// performs the write.
    /// </summary>
    /// <param name="ownerHwnd">The owner window handle (UI thread; blocks). <c>0</c> for unowned.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="defaultFileName">Pre-filled file name (and, via its extension, the default type). May be empty.</param>
    /// <param name="filters">Optional file-type filters; the first is selected by default.</param>
    /// <returns>The target path, or <see langword="null"/> on cancel.</returns>
    /// <exception cref="InvalidOperationException">A COM step failed for a reason other than user cancellation.</exception>
    public static string? SaveFile(nint ownerHwnd, string title, string defaultFileName,
                                   params (string Name, string Spec)[] filters)
    {
        EnsureSta();
        IFileSaveDialog* dialog = CreateSaveDialog();
        try
        {
            // FOS_OVERWRITEPROMPT is the save-dialog default, set explicitly for clarity.
            SetTitle((IFileDialog*)dialog, title ?? DefaultSaveTitle);
            SetOptions((IFileDialog*)dialog,
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT |
                FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_NOREADONLYRETURN);
            SetFileTypes((IFileDialog*)dialog, filters);

            if (!string.IsNullOrEmpty(defaultFileName))
                fixed (char* pName = defaultFileName)
                    ThrowIfFailed(dialog->SetFileName(pName), "IFileSaveDialog.SetFileName");

            if (!Show((IModalWindow*)dialog, ownerHwnd))
                return null;

            IShellItem* item = null;
            ThrowIfFailed(dialog->GetResult(&item), "IFileSaveDialog.GetResult");
            try { return GetPath(item); }
            finally { if (item != null) item->Release(); }
        }
        finally { dialog->Release(); }
    }

    /// <summary>
    /// Show a modal folder picker (the open dialog in <c>FOS_PICKFOLDERS</c> mode) and return the chosen directory
    /// path, or <see langword="null"/> if cancelled.
    /// </summary>
    /// <param name="ownerHwnd">The owner window handle (UI thread; blocks). <c>0</c> for unowned.</param>
    /// <param name="title">The dialog title.</param>
    /// <returns>The selected folder path, or <see langword="null"/> on cancel.</returns>
    /// <exception cref="InvalidOperationException">A COM step failed for a reason other than user cancellation.</exception>
    public static string? PickFolder(nint ownerHwnd, string title = DefaultFolderTitle)
    {
        EnsureSta();
        IFileOpenDialog* dialog = CreateOpenDialog();
        try
        {
            SetTitle((IFileDialog*)dialog, title);
            SetOptions((IFileDialog*)dialog,
                FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM |
                FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);

            if (!Show((IModalWindow*)dialog, ownerHwnd))
                return null;

            IShellItem* item = null;
            ThrowIfFailed(dialog->GetResult(&item), "IFileOpenDialog.GetResult");
            try { return GetPath(item); }
            finally { if (item != null) item->Release(); }
        }
        finally { dialog->Release(); }
    }

    // ── creation ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static IFileOpenDialog* CreateOpenDialog()
    {
        Guid clsid = Clsid.FileOpenDialog;
        Guid iid = __uuidof<IFileOpenDialog>();
        IFileOpenDialog* dialog = null;
        ThrowIfFailed(
            CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&dialog),
            "CoCreateInstance(CLSID_FileOpenDialog)");
        return dialog;
    }

    private static IFileSaveDialog* CreateSaveDialog()
    {
        Guid clsid = Clsid.FileSaveDialog;
        Guid iid = __uuidof<IFileSaveDialog>();
        IFileSaveDialog* dialog = null;
        ThrowIfFailed(
            CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&dialog),
            "CoCreateInstance(CLSID_FileSaveDialog)");
        return dialog;
    }

    // ── configuration (shared between open/save via the IFileDialog base vtable) ───────────────────────────────────

    private static void ConfigureOpen(IFileOpenDialog* dialog, string title,
                                      (string Name, string Spec)[] filters, FILEOPENDIALOGOPTIONS options)
    {
        SetTitle((IFileDialog*)dialog, title);
        SetOptions((IFileDialog*)dialog, options);
        SetFileTypes((IFileDialog*)dialog, filters);
    }

    private static void SetTitle(IFileDialog* dialog, string title)
    {
        if (string.IsNullOrEmpty(title))
            return;
        fixed (char* pTitle = title)
            ThrowIfFailed(dialog->SetTitle(pTitle), "IFileDialog.SetTitle");
    }

    private static void SetOptions(IFileDialog* dialog, FILEOPENDIALOGOPTIONS options)
    {
        // Merge onto the dialog's existing defaults rather than clobbering them.
        uint current = 0;
        ThrowIfFailed(dialog->GetOptions(&current), "IFileDialog.GetOptions");
        ThrowIfFailed(dialog->SetOptions(current | (uint)options), "IFileDialog.SetOptions");
    }

    /// <summary>
    /// Hand the dialog a <c>COMDLG_FILTERSPEC[]</c> built from the caller's filters. A dynamic count of UTF-16 strings
    /// cannot be pinned with a fixed number of nested <c>fixed</c> blocks, so each name/spec is materialized as an
    /// owned null-terminated <c>char[]</c> and pinned with a <see cref="System.Runtime.InteropServices.GCHandle"/> for
    /// the duration of the (copying) <c>SetFileTypes</c> call — all handles freed in <c>finally</c>. This is a cold,
    /// one-shot UI path, not a frame phase, so the small pinned allocation is acceptable.
    /// </summary>
    private static void SetFileTypes(IFileDialog* dialog, (string Name, string Spec)[] filters)
    {
        if (filters is null || filters.Length == 0)
            return;

        int n = filters.Length;
        var specs = new COMDLG_FILTERSPEC[n];
        var handles = new System.Runtime.InteropServices.GCHandle[2 * n];
        try
        {
            for (int i = 0; i < n; i++)
            {
                char[] name = ToNullTerminated(filters[i].Name);
                char[] spec = ToNullTerminated(string.IsNullOrEmpty(filters[i].Spec) ? "*.*" : filters[i].Spec);
                handles[2 * i] = System.Runtime.InteropServices.GCHandle.Alloc(name,
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                handles[2 * i + 1] = System.Runtime.InteropServices.GCHandle.Alloc(spec,
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                specs[i].pszName = (char*)handles[2 * i].AddrOfPinnedObject();
                specs[i].pszSpec = (char*)handles[2 * i + 1].AddrOfPinnedObject();
            }

            fixed (COMDLG_FILTERSPEC* pSpecs = specs)
                ThrowIfFailed(dialog->SetFileTypes((uint)n, pSpecs), "IFileDialog.SetFileTypes");
        }
        finally
        {
            for (int i = 0; i < handles.Length; i++)
                if (handles[i].IsAllocated) handles[i].Free();
        }
    }

    private static char[] ToNullTerminated(string s)
    {
        var buf = new char[s.Length + 1];
        s.CopyTo(buf);
        buf[s.Length] = '\0';
        return buf;
    }

    // ── show + result projection ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Show the dialog modally; returns <see langword="true"/> on commit, <see langword="false"/> on cancel.</summary>
    private static bool Show(IModalWindow* dialog, nint ownerHwnd)
    {
        HRESULT hr = dialog->Show((HWND)ownerHwnd);
        if (!hr.FAILED)
            return true;                    // S_OK (or any non-failure) → the user committed.
        if ((int)hr == CancelledHr)
            return false;                   // HRESULT_FROM_WIN32(ERROR_CANCELLED) → the user dismissed it.
        ThrowIfFailed(hr, "IModalWindow.Show");
        return false; // unreachable (ThrowIfFailed always throws on a failed, non-cancel HR).
    }

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_CANCELLED)</c> — the HRESULT a cancelled dialog returns (0x800704C7).</summary>
    private static int CancelledHr => (int)HRESULT_FROM_WIN32(unchecked((uint)ERROR_CANCELLED));

    /// <summary>Resolve a shell item to a filesystem path; throws if it has none.</summary>
    private static string GetPath(IShellItem* item)
        => TryGetPath(item) ?? throw new InvalidOperationException(
            "The selected item has no filesystem path (SIGDN_FILESYSPATH returned null).");

    /// <summary>Resolve a shell item to a filesystem path, or <see langword="null"/> if it is not a filesystem item.</summary>
    private static string? TryGetPath(IShellItem* item)
    {
        char* psz = null;
        HRESULT hr = item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &psz);
        if (hr.FAILED || psz == null)
            return null;
        try { return new string(psz); }
        finally { CoTaskMemFree(psz); }   // the path buffer is OS-allocated; free with CoTaskMemFree.
    }

    // ── apartment ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure the calling thread is in an STA (the common-item dialog requires it). Tolerates the benign
    /// already-initialized results; does not balance with <c>CoUninitialize</c> (the host owns apartment lifetime).
    /// </summary>
    private static void EnsureSta()
    {
        int hr = (int)CoInitializeEx(null, (uint)COINIT.COINIT_APARTMENTTHREADED);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE)
            ThrowIfFailed(hr, "CoInitializeEx(APARTMENTTHREADED)");
    }

    private static void ThrowIfFailed(HRESULT hr, string what)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{what} failed (0x{(uint)hr:X8}).");
    }
}
