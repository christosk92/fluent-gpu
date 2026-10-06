using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>
/// One button on a window's taskbar thumbnail toolbar (<c>ITaskbarList3.ThumbBarAddButtons</c>). The shell caps the
/// toolbar at 7 buttons, added once per HWND; later changes go through <see cref="TaskbarManager.UpdateThumbButton"/>
/// / a subsequent <see cref="TaskbarManager.SetThumbButtons"/>.
/// </summary>
/// <param name="Id">Application-defined identifier, unique within the toolbar. Delivered as the payload of
/// <c>FluentApp.ThumbButtonClicked</c> when the user clicks the button (<c>WM_COMMAND</c> / <c>THBN_CLICKED</c>).</param>
/// <param name="IconPath">Path to an <c>.ico</c> file loaded via <c>LoadImageW(LR_LOADFROMFILE)</c>, or
/// <see langword="null"/> for no glyph.</param>
/// <param name="Tooltip">Hover tooltip (truncated to 259 characters; the shell's <c>THUMBBUTTON.szTip</c> cap).</param>
/// <param name="Enabled"><see langword="true"/> (default) draws an active button; <see langword="false"/> is
/// <c>THBF_DISABLED</c>.</param>
/// <param name="DismissOnClick"><see langword="true"/> closes the thumbnail flyout on click (<c>THBF_DISMISSONCLICK</c>).</param>
public readonly record struct ThumbButton(
    int Id,
    string? IconPath,
    string Tooltip,
    bool Enabled = true,
    bool DismissOnClick = false);

/// <summary>
/// Taskbar button progress, overlay-icon, and thumbnail-toolbar control over <c>ITaskbarList3</c> (the Windows 7+
/// taskbar API). One process-wide <c>ITaskbarList3</c> is <c>CoCreateInstance</c>d and <c>HrInit</c>'d lazily on first
/// use and reused for the process lifetime — the same flat call-OUT COM shape as the WIC codec
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
/// <b>Overlay and thumb-button icons.</b> An <c>.ico</c> path is loaded with <c>LoadImageW(LR_LOADFROMFILE |
/// LR_DEFAULTSIZE)</c> ONCE per (path, <c>SM_CXICON</c> x <c>SM_CYICON</c>) and the <c>HICON</c> is kept in a small
/// process-wide cache (<see cref="IconHandleCache"/>, 16 handles, LRU), so a badge or a glyph applied on every
/// play/pause is not re-read from disk each time. The shell copies the icon during <c>SetOverlayIcon</c> /
/// <c>ThumbBarAddButtons</c> / <c>ThumbBarUpdateButtons</c>, so the cache may destroy a handle the shell has already
/// seen (LRU eviction, a change of the system icon size). <see cref="ReleaseIcons"/> destroys them all (shutdown).
/// Passing a <see langword="null"/> overlay path clears any existing overlay (and skips the load entirely). An icon
/// file replaced on disk while the process runs keeps its cached image until <see cref="ReleaseIcons"/>.
/// </para>
/// <para>
/// <b>Thumbnail toolbar.</b> <see cref="SetThumbButtons"/> adds up to 7 buttons (<c>ThumbBarAddButtons</c>) the first
/// time it is called for an HWND; the shell forbids adding again, so later calls (and
/// <see cref="UpdateThumbButton"/>) go through <c>ThumbBarUpdateButtons</c>. Call after the window is shown — the
/// shell ignores (or fails) an add made before the taskbar button exists. Explorer broadcasts the registered
/// <c>TaskbarButtonCreated</c> message when the button appears and again if explorer restarts; subscribe to
/// <c>FluentApp.TaskbarButtonCreated</c>, call <see cref="NotifyTaskbarButtonCreated"/> to drop the add-once latch,
/// then <see cref="SetThumbButtons"/> again. Clicks arrive as <c>FluentApp.ThumbButtonClicked</c> with
/// <see cref="ThumbButton.Id"/>.
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
    private const int SM_CXICON = 11, SM_CYICON = 12;   // winuser.h: the size LR_DEFAULTSIZE resolves an icon to.

    private const int MaxThumbButtons = 7;    // ITaskbarList3 contract (ThumbBarAddButtons cButtons cap).

    private static readonly object _gate = new();
    private static ITaskbarList3* _taskbar;   // process-cached, AddRef-owned; created+HrInit'd once.
    private static bool _initFailed;          // once true, all methods no-op (shell unavailable).
    private static readonly Dictionary<nint, ThumbBarState> _thumbBars = new(); // per-HWND add-once latch + button ids.
    private static readonly IconHandleCache _icons = new(LoadIcon, static h => DestroyIcon((HICON)h)); // owns every HICON.

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

            // Cached and cache-owned: the shell copies the icon during SetOverlayIcon, and the next badge from the same
            // file reuses this handle instead of reading the .ico from disk again.
            HICON icon = CachedIcon(iconPath, "overlay");
            fixed (char* pDesc = description ?? string.Empty)
                tb->SetOverlayIcon((HWND)hwnd, icon, pDesc);
        }
    }

    /// <summary>
    /// Destroy every cached overlay / thumb-button <c>HICON</c>. Call on shutdown (after the last taskbar call), or after
    /// replacing an icon file on disk. Safe at any time: the shell holds its own copies, and the next call reloads.
    /// </summary>
    public static void ReleaseIcons()
    {
        lock (_gate) _icons.Clear();
    }

    /// <summary>
    /// Install (first call per HWND) or refresh the thumbnail toolbar on <paramref name="hwnd"/>. The shell accepts at
    /// most 7 buttons and they can be <c>ThumbBarAddButtons</c>'d only once per window; later calls diff against that
    /// set and <c>ThumbBarUpdateButtons</c> (omitted ids are hidden with <c>THBF_HIDDEN</c>; new ids after the first add
    /// are ignored — the count/order is frozen). A no-op if the shell is unavailable or the add is too early (retry
    /// after show / <c>TaskbarButtonCreated</c>).
    /// </summary>
    /// <param name="hwnd">The owning window handle (UI thread; typically <c>FluentApp.WindowHandle</c>).</param>
    /// <param name="buttons">The full toolbar (≤ 7). Empty hides every previously added button.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buttons"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">More than 7 buttons were supplied.</exception>
    /// <exception cref="InvalidOperationException">An <see cref="ThumbButton.IconPath"/> was supplied but could not be loaded.</exception>
    public static void SetThumbButtons(nint hwnd, params ThumbButton[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Length > MaxThumbButtons)
            throw new ArgumentException(
                $"Thumbnail toolbar is capped at {MaxThumbButtons} buttons (ITaskbarList3 contract).", nameof(buttons));

        lock (_gate)
        {
            ITaskbarList3* tb = EnsureTaskbar();
            if (tb == null) return;

            if (!_thumbBars.TryGetValue(hwnd, out ThumbBarState? state))
                _thumbBars[hwnd] = state = new ThumbBarState();

            if (!state.Added)
            {
                if (buttons.Length == 0) return;
                TryAddButtons(tb, hwnd, state, buttons);
                return;
            }

            if (!TryUpdateButtons(tb, hwnd, state, buttons))
            {
                // Explorer may have restarted (toolbar gone). Re-add the requested set; leave prior state if Add fails.
                TryAddButtons(tb, hwnd, state, buttons);
            }
        }
    }

    /// <summary>
    /// Update a single previously-added thumbnail button (icon / tooltip / enabled / dismiss-on-click). A no-op if the
    /// shell is unavailable, the toolbar has not been added yet, or <paramref name="button"/>'s id was not in the
    /// original <see cref="SetThumbButtons"/> set.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="ThumbButton.IconPath"/> was supplied but could not be loaded.</exception>
    public static void UpdateThumbButton(nint hwnd, ThumbButton button)
    {
        lock (_gate)
        {
            ITaskbarList3* tb = EnsureTaskbar();
            if (tb == null) return;
            if (!_thumbBars.TryGetValue(hwnd, out ThumbBarState? state) || !state.Added) return;

            int slot = FindSlot(state, button.Id);
            if (slot < 0) return;

            THUMBBUTTON native = default;
            FillNative(&native, in button, LoadThumbIcon(button.IconPath), hidden: false);
            tb->ThumbBarUpdateButtons((HWND)hwnd, 1, &native);
        }
    }

    /// <summary>
    /// Drop the add-once latch for <paramref name="hwnd"/> so the next
    /// <see cref="SetThumbButtons"/> uses <c>ThumbBarAddButtons</c> again. Call from
    /// <c>FluentApp.TaskbarButtonCreated</c> after an explorer restart (the shell discards the previous toolbar).
    /// </summary>
    public static void NotifyTaskbarButtonCreated(nint hwnd)
    {
        lock (_gate)
        {
            if (!_thumbBars.TryGetValue(hwnd, out ThumbBarState? state)) return;
            state.Added = false;
            state.Count = 0;
        }
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cache's loader. <c>LR_DEFAULTSIZE</c> resolves to <c>SM_CXICON</c> x <c>SM_CYICON</c>, the size
    /// <see cref="CachedIcon"/> keys on, so the cached image is exactly what an uncached load would return.</summary>
    private static nint LoadIcon(string path, int cx, int cy)
    {
        // LoadImageW returns a HANDLE; convert through void* (the TerraFX handle structs interconvert via void*,
        // cf. FluentGpu.Windows/Pal/Win32TextServices.cs:32 `(HANDLE)(void*)h`).
        fixed (char* p = path)
            return (nint)(void*)LoadImageW(HINSTANCE.NULL, p, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
    }

    /// <summary>The cache-owned icon for <paramref name="path"/> at the current system icon size. Caller holds
    /// <see cref="_gate"/> and must NOT destroy the result. Throws when the file cannot be loaded.</summary>
    private static HICON CachedIcon(string path, string what)
    {
        nint h = _icons.Get(path, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON));
        if (h == 0)
            throw new InvalidOperationException(
                $"LoadImageW failed to load {what} icon '{path}' " +
                $"(GetLastError=0x{(uint)System.Runtime.InteropServices.Marshal.GetLastPInvokeError():X8}).");
        return (HICON)(void*)h;
    }

    private static HICON LoadThumbIcon(string? iconPath)
        => string.IsNullOrEmpty(iconPath) ? HICON.NULL : CachedIcon(iconPath, "thumb-button");

    private sealed class ThumbBarState
    {
        public bool Added;
        public int Count;
        public readonly int[] Ids = new int[MaxThumbButtons];
    }

    private static int FindSlot(ThumbBarState state, int id)
    {
        for (int i = 0; i < state.Count; i++)
            if (state.Ids[i] == id) return i;
        return -1;
    }

    private static void FillNative(THUMBBUTTON* native, in ThumbButton button, HICON icon, bool hidden)
    {
        *native = default;
        native->iId = (uint)button.Id;
        native->dwMask = THUMBBUTTONMASK.THB_TOOLTIP | THUMBBUTTONMASK.THB_FLAGS;
        if (icon != HICON.NULL)
        {
            native->dwMask |= THUMBBUTTONMASK.THB_ICON;
            native->hIcon = icon;
        }
        WriteTip(native, button.Tooltip);
        THUMBBUTTONFLAGS flags = hidden
            ? THUMBBUTTONFLAGS.THBF_HIDDEN
            : (button.Enabled ? THUMBBUTTONFLAGS.THBF_ENABLED : THUMBBUTTONFLAGS.THBF_DISABLED);
        if (!hidden && button.DismissOnClick)
            flags |= THUMBBUTTONFLAGS.THBF_DISMISSONCLICK;
        native->dwFlags = flags;
    }

    private static void FillHidden(THUMBBUTTON* native, int id)
    {
        *native = default;
        native->iId = (uint)id;
        native->dwMask = THUMBBUTTONMASK.THB_FLAGS;
        native->dwFlags = THUMBBUTTONFLAGS.THBF_HIDDEN;
    }

    private static void WriteTip(THUMBBUTTON* native, string? tooltip)
    {
        string tip = tooltip ?? string.Empty;
        int n = tip.Length;
        if (n > 259) n = 259;
        for (int i = 0; i < n; i++)
            native->szTip[i] = tip[i];
        native->szTip[n] = '\0';
    }

    private static void TryAddButtons(ITaskbarList3* tb, nint hwnd, ThumbBarState state, ThumbButton[] buttons)
    {
        int n = buttons.Length;
        if (n == 0) return;

        THUMBBUTTON* natives = stackalloc THUMBBUTTON[MaxThumbButtons];
        for (int i = 0; i < n; i++)
            FillNative(&natives[i], in buttons[i], LoadThumbIcon(buttons[i].IconPath), hidden: false);

        HRESULT hr = tb->ThumbBarAddButtons((HWND)hwnd, (uint)n, natives);
        if (hr.FAILED) return;

        state.Count = n;
        for (int i = 0; i < n; i++)
            state.Ids[i] = buttons[i].Id;
        state.Added = true;
    }

    /// <summary>Returns <see langword="false"/> when Update failed (caller may retry as Add after explorer restart).</summary>
    private static bool TryUpdateButtons(ITaskbarList3* tb, nint hwnd, ThumbBarState state, ThumbButton[] buttons)
    {
        int n = state.Count;
        if (n == 0) return true;

        THUMBBUTTON* natives = stackalloc THUMBBUTTON[MaxThumbButtons];
        for (int i = 0; i < n; i++)
        {
            int id = state.Ids[i];
            int found = -1;
            for (int j = 0; j < buttons.Length; j++)
                if (buttons[j].Id == id) { found = j; break; }

            if (found < 0)
            {
                FillHidden(&natives[i], id);
                continue;
            }

            FillNative(&natives[i], in buttons[found], LoadThumbIcon(buttons[found].IconPath), hidden: false);
        }

        return tb->ThumbBarUpdateButtons((HWND)hwnd, (uint)n, natives).SUCCEEDED;
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
