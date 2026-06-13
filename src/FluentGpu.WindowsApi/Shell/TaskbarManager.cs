using System;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>
/// Taskbar button progress and overlay-icon control over <c>ITaskbarList3</c> (the Windows 7+ taskbar API). One
/// process-wide <c>ITaskbarList3</c> is <c>CoCreateInstance</c>d and <c>HrInit</c>'d lazily on first use and reused for
/// the process lifetime — the same flat call-OUT COM shape as the WIC codec
/// (<c>FluentGpu.Windows/Wic/WicImageCodec.cs:28-32</c>): a hand-declared CLSID, <c>__uuidof&lt;T&gt;()</c> for the
/// IID, then <c>iface-&gt;Method(hwnd, ...)</c> through TerraFX's prebuilt vtable struct. AOT-clean — no CsWinRT, no
/// <c>ComWrappers</c>, no reflection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading / HWND ownership.</b> <c>ITaskbarList3</c> methods take the target window's <c>HWND</c> explicitly, so
/// they are not bound to the thread that created the object — but the canonical, safest usage is to call these from the
/// <b>UI thread that owns <c>hwnd</c></b> (the FluentGpu window handle), matching how the shell expects
/// taskbar updates. The cached object is created in the apartment of whatever thread first calls in;
/// <see cref="EnsureTaskbar"/> initializes COM (STA) on that thread if needed. Do not pass a window owned by another
/// process.
/// </para>
/// <para>
/// <b>The shell must be ready.</b> Calls made before the taskbar button exists (very early in startup) are silently
/// ignored by the shell; this is harmless. If <c>CoCreateInstance</c>/<c>HrInit</c> fails (e.g. a session with no
/// shell), every method becomes a no-op rather than throwing — taskbar adornment is best-effort chrome, not a feature
/// an app should fail to launch without.
/// </para>
/// <para>
/// <b>Overlay icons.</b> <see cref="SetOverlayIcon"/> loads an <c>.ico</c> from disk with
/// <c>LoadImageW(LR_LOADFROMFILE)</c>, hands the <c>HICON</c> to <c>ITaskbarList3::SetOverlayIcon</c>, then destroys it
/// with <c>DestroyIcon</c> — the shell copies what it needs during the call, so the icon is freed immediately after.
/// Passing a <see langword="null"/> path clears any existing overlay (and skips the load entirely).
/// </para>
/// <para>
/// References:
/// <list type="bullet">
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-itaskbarlist3">ITaskbarList3</see></item>
/// <item><see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-setprogressvalue">SetProgressValue</see> / <see href="https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-setprogressstate">SetProgressState</see></item>
/// <item>CLSID_TaskbarList <c>{56FDF344-FD6D-11D0-958A-006097C9A090}</c> from the Windows SDK <c>ShObjIdl_core.h</c> (TerraFX exposes <c>ITaskbarList3</c> but not the coclass CLSID as a field).</item>
/// </list>
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.1")] // ITaskbarList3 shipped in Windows 7.
public static unsafe class TaskbarManager
{
    // CLSID_TaskbarList {56FDF344-FD6D-11D0-958A-006097C9A090} (ShObjIdl_core.h). TerraFX projects ITaskbarList3 + the
    // empty TaskbarList coclass marker but not a CLSID_* GUID field; restated here in the house style.
    private static readonly Guid CLSID_TaskbarList =
        new(0x56FDF344, 0xFD6D, 0x11D0, 0x95, 0x8A, 0x00, 0x60, 0x97, 0xC9, 0xA0, 0x90);

    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    // LoadImageW image-type + flags (winuser.h #defines TerraFX does not project as fields).
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private static readonly object _gate = new();
    private static ITaskbarList3* _taskbar;   // process-cached, AddRef-owned; created+HrInit'd once.
    private static bool _initFailed;          // once true, all methods no-op (shell unavailable).

    /// <summary>
    /// Set the determinate progress fraction on <paramref name="hwnd"/>'s taskbar button. Combine with
    /// <see cref="SetProgressState"/> (<see cref="TaskbarProgressState.Normal"/>/<see cref="TaskbarProgressState.Error"/>/
    /// <see cref="TaskbarProgressState.Paused"/>) to choose the bar color; on its own this sets the fill level. A no-op
    /// if the shell is unavailable.
    /// </summary>
    /// <param name="hwnd">The owning window handle (UI thread).</param>
    /// <param name="completed">Work done so far (numerator).</param>
    /// <param name="total">Total work (denominator); a <c>0</c> total is treated as no-progress.</param>
    public static void SetProgress(nint hwnd, ulong completed, ulong total)
    {
        lock (_gate)
        {
            ITaskbarList3* tb = EnsureTaskbar();
            if (tb == null) return;
            tb->SetProgressValue((HWND)hwnd, completed, total);
        }
    }

    /// <summary>
    /// Set the taskbar button's progress mode (none / indeterminate / normal / error / paused). A no-op if the shell is
    /// unavailable.
    /// </summary>
    /// <param name="hwnd">The owning window handle (UI thread).</param>
    /// <param name="state">The progress mode; see <see cref="TaskbarProgressState"/>.</param>
    public static void SetProgressState(nint hwnd, TaskbarProgressState state)
    {
        lock (_gate)
        {
            ITaskbarList3* tb = EnsureTaskbar();
            if (tb == null) return;
            tb->SetProgressState((HWND)hwnd, ToTbpFlag(state));
        }
    }

    /// <summary>Clear the progress indicator (equivalent to <see cref="SetProgressState"/> with
    /// <see cref="TaskbarProgressState.None"/>). A no-op if the shell is unavailable.</summary>
    /// <param name="hwnd">The owning window handle (UI thread).</param>
    public static void ClearProgress(nint hwnd)
    {
        lock (_gate)
        {
            ITaskbarList3* tb = EnsureTaskbar();
            if (tb == null) return;
            tb->SetProgressState((HWND)hwnd, TBPFLAG.TBPF_NOPROGRESS);
        }
    }

    /// <summary>
    /// Set (or clear) the small overlay icon drawn on the corner of the taskbar button — e.g. a "playing" badge, an
    /// unread count, or a status glyph. A no-op if the shell is unavailable.
    /// </summary>
    /// <param name="hwnd">The owning window handle (UI thread).</param>
    /// <param name="iconPath">Path to an <c>.ico</c> file to load and apply, or <see langword="null"/> to remove the
    /// current overlay.</param>
    /// <param name="description">An accessibility/alt-text description of the overlay's meaning (shown to assistive
    /// tech); ignored when clearing.</param>
    /// <exception cref="InvalidOperationException"><paramref name="iconPath"/> was supplied but could not be loaded.</exception>
    public static void SetOverlayIcon(nint hwnd, string? iconPath, string description)
    {
        lock (_gate)
        {
            ITaskbarList3* tb = EnsureTaskbar();
            if (tb == null) return;

            if (iconPath is null)
            {
                // Clear: pass a null HICON. The description is irrelevant when removing.
                tb->SetOverlayIcon((HWND)hwnd, HICON.NULL, null);
                return;
            }

            HICON icon = LoadIconFromFile(iconPath);
            if (icon == HICON.NULL)
                throw new InvalidOperationException(
                    $"LoadImageW failed to load overlay icon '{iconPath}' " +
                    $"(GetLastError=0x{(uint)System.Runtime.InteropServices.Marshal.GetLastPInvokeError():X8}).");
            try
            {
                fixed (char* pDesc = description ?? string.Empty)
                    tb->SetOverlayIcon((HWND)hwnd, icon, pDesc);
            }
            finally
            {
                // The shell copies the icon during SetOverlayIcon; destroy our copy immediately after the call.
                DestroyIcon(icon);
            }
        }
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static HICON LoadIconFromFile(string path)
    {
        // LoadImageW returns a HANDLE; convert through void* to HICON (the TerraFX handle structs interconvert via void*,
        // cf. FluentGpu.Windows/Pal/Win32TextServices.cs:32 `(HANDLE)(void*)h`).
        fixed (char* p = path)
            return (HICON)(void*)LoadImageW(HINSTANCE.NULL, p, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
    }

    private static TBPFLAG ToTbpFlag(TaskbarProgressState state) => state switch
    {
        TaskbarProgressState.None => TBPFLAG.TBPF_NOPROGRESS,
        TaskbarProgressState.Indeterminate => TBPFLAG.TBPF_INDETERMINATE,
        TaskbarProgressState.Normal => TBPFLAG.TBPF_NORMAL,
        TaskbarProgressState.Error => TBPFLAG.TBPF_ERROR,
        TaskbarProgressState.Paused => TBPFLAG.TBPF_PAUSED,
        _ => TBPFLAG.TBPF_NOPROGRESS,
    };

    /// <summary>
    /// Return the process-cached <c>ITaskbarList3</c>, creating and <c>HrInit</c>-ing it on first call. Returns
    /// <see langword="null"/> (and latches <see cref="_initFailed"/>) if the shell is unavailable, turning every public
    /// method into a no-op. Caller holds <see cref="_gate"/>.
    /// </summary>
    private static ITaskbarList3* EnsureTaskbar()
    {
        if (_taskbar != null) return _taskbar;
        if (_initFailed) return null;

        // ITaskbarList3 is apartment-threaded; ensure this thread is in an STA. Benign already-init results tolerated.
        int coHr = (int)CoInitializeEx(null, (uint)COINIT.COINIT_APARTMENTTHREADED);
        if (coHr < 0 && coHr != RPC_E_CHANGED_MODE && coHr != S_FALSE)
        {
            _initFailed = true;
            return null;
        }

        Guid clsid = CLSID_TaskbarList;
        Guid iid = __uuidof<ITaskbarList3>();
        ITaskbarList3* tb = null;
        HRESULT hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&tb);
        if (hr.FAILED || tb == null)
        {
            _initFailed = true;
            return null;
        }

        // HrInit must be called once before any other method (it attaches to the taskbar). On failure, release and latch.
        if (tb->HrInit().FAILED)
        {
            tb->Release();
            _initFailed = true;
            return null;
        }

        _taskbar = tb;
        return _taskbar;
    }
}
