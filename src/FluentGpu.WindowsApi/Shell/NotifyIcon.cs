using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentGpu.Foundation;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static FluentGpu.WindowsApi.Shell.NotifyIconProtocol;
// TerraFX projects the shellapi.h NIM_*/NIF_* constants too; the protocol's own (the ones its tests pin) are spelled P.*.
using P = FluentGpu.WindowsApi.Shell.NotifyIconProtocol;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>
/// A notification-area ("system tray") icon over <c>Shell_NotifyIconW</c>, speaking <c>NOTIFYICON_VERSION_4</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own window.</b> The icon owns a hidden, never-shown TOP-LEVEL callback window created on the constructing
/// thread. Top-level is load-bearing: explorer's <c>TaskbarCreated</c> broadcast (sent when the shell restarts) skips
/// message-only windows, and without it the icon silently vanishes for the rest of the session — the same reason
/// <c>MessagePump</c>'s sentinel is top-level. Every event is decoded in that window's procedure and raised through
/// <see cref="Activated"/> on the creating thread, from inside that thread's ordinary message pump (the engine's UI loop
/// pumps every window of its thread, so a UI-thread icon needs no hop). A handler that throws is logged, never allowed
/// to unwind into the shell's callback.
/// </para>
/// <para>
/// <b>Identity.</b> A non-empty GUID uses <c>NIF_GUID</c> — the documented recommendation, under which the shell
/// remembers the user's "show this icon" choice. The shell binds that GUID to the exe's PATH and refuses it from any
/// other path unless both binaries carry the same Authenticode signer (<c>NOTIFYICONDATAW</c> › Troubleshooting), so a
/// GUID is only safe for a signed install with a stable identity; pass <see cref="Guid.Empty"/> for window + id identity
/// (an unsigned dev build). The choice is the app's.
/// </para>
/// <para>
/// <b>State survives the shell.</b> The icon keeps what it was told — shown or not, the icon image, the tooltip — so
/// <see cref="Show"/> before explorer is up, an explorer restart, or a failed add all converge: the next
/// <c>TaskbarCreated</c> re-adds it (<c>NIM_ADD</c> + <c>NIM_SETVERSION</c>) and raises
/// <see cref="NotifyIconEvent.Recreated"/>. <see cref="NotifyIconEvent.ShellChanged"/> reports a taskbar DPI or light/dark
/// change. No balloons: <c>NIF_INFO</c> is never used (toasts are the notification surface).
/// </para>
/// <para>
/// <b>Fail-soft.</b> A shell that refuses the icon leaves <see cref="IsShown"/> false and every call a harmless no-op
/// (logged once per failure with an always-on <c>[notifyicon]</c> line). Only the callback window itself is required:
/// the constructor throws if it cannot be created. The process-exit path deletes the icon best-effort, so a clean
/// shutdown never leaves a ghost icon that disappears on hover.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.1")]
public sealed unsafe class NotifyIcon : IDisposable
{
    /// <summary><c>szTip</c> holds 128 <c>WCHAR</c>s including the terminator; longer tips are cut.</summary>
    public const int MaxTipChars = NotifyIconProtocol.MaxTipChars;

    private const string ClassName = "FluentGpuNotifyIcon";
    private const uint IconUid = 1;
    private const int GWLP_USERDATA = -21;
    private const uint WM_NULL = 0x0000, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_SETTINGCHANGE = 0x001A,
                       WM_DISPLAYCHANGE = 0x007E, WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082, WM_DPICHANGED = 0x02E0;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MSGFLT_ALLOW = 1;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x00000010;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int SM_MENUDROPALIGNMENT = 40;
    private const uint RRF_RT_REG_DWORD = 0x00000010;
    private static readonly HKEY HKEY_CURRENT_USER = (HKEY)(void*)unchecked((nint)0x80000001u);

    private static uint s_taskbarCreatedMsg;   // RegisterWindowMessageW("TaskbarCreated") — the same id in every caller

    private readonly Guid _identity;
    private readonly EventHandler _onProcessExit;
    private HWND _hwnd;
    private GCHandle _self;
    private bool _wantShown;      // what the app asked for (survives shell restarts and failed adds)
    private bool _added;          // what the shell currently holds
    private bool _disposed;
    private HICON _icon;          // owned; kept for the icon's lifetime so a re-add has something to send
    private string? _iconPath;
    private bool _iconAutoSize;   // SetIcon(path): reloaded at the new frame size when the taskbar DPI changes
    private string _tip;
    private NotifyIconShellState _shell;

    /// <param name="identity">A non-empty GUID for <c>NIF_GUID</c> identity (signed, stable install only — see remarks),
    /// or <see cref="Guid.Empty"/> for window + id identity.</param>
    /// <param name="tip">The initial tooltip (also the icon's accessible name); cut to <see cref="MaxTipChars"/>.</param>
    /// <exception cref="InvalidOperationException">The hidden callback window could not be created.</exception>
    public NotifyIcon(Guid identity, string tip)
    {
        _identity = identity;
        _tip = ClampTip(tip);
        _self = GCHandle.Alloc(this);
        _hwnd = CreateCallbackWindow(_self);
        if (_hwnd == HWND.NULL)
        {
            int err = Marshal.GetLastPInvokeError();
            _self.Free();
            throw new InvalidOperationException($"NotifyIcon: the callback window could not be created (GetLastError=0x{(uint)err:X8}).");
        }
        // UIPI: an elevated process otherwise never sees explorer's broadcast or its callbacks. Best-effort.
        if (s_taskbarCreatedMsg != 0) ChangeWindowMessageFilterEx(_hwnd, s_taskbarCreatedMsg, MSGFLT_ALLOW, null);
        ChangeWindowMessageFilterEx(_hwnd, CallbackMessage, MSGFLT_ALLOW, null);
        _shell = ReadShellState();
        _onProcessExit = (_, _) => DeleteFromShell();
        AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
    }

    /// <summary>The identity this icon registers with (<see cref="Guid.Empty"/> = window + id).</summary>
    public Guid Identity => _identity;

    /// <summary>True while the shell holds the icon (<c>NIM_ADD</c> succeeded and no <see cref="Hide"/> since). False after a
    /// refused add, until the next shell restart re-adds it.</summary>
    public bool IsShown => _added;

    /// <summary>The tooltip the icon carries (already cut to <see cref="MaxTipChars"/>).</summary>
    public string Tip => _tip;

    /// <summary>The effective DPI of the monitor the taskbar's notification area is on (the primary taskbar; 96 when
    /// unknown). Re-read on every shell broadcast; see <see cref="SmallIconSizeForDpi"/> for the frame it selects.</summary>
    public uint TaskbarDpi => _shell.TaskbarDpi;

    /// <summary>True when the taskbar is light (<c>Personalize\SystemUsesLightTheme</c> — the Windows mode, not the app
    /// mode). Pick the glyph variant from this. Re-read on every shell broadcast.</summary>
    public bool TaskbarUsesLightTheme => _shell.TaskbarLight;

    /// <summary>What the user did: (event, anchorX, anchorY), anchor in physical screen px (0,0 for
    /// <see cref="NotifyIconEvent.Recreated"/> / <see cref="NotifyIconEvent.ShellChanged"/>). Raised on the creating
    /// thread.</summary>
    public event Action<NotifyIconEvent, int, int>? Activated;

    /// <summary>The tray icon size for a taskbar DPI — <c>SM_CXSMICON</c> scaled like <c>GetSystemMetricsForDpi</c>:
    /// 16 @ 96, 20 @ 120, 24 @ 144, 28 @ 168, 32 @ 192, 40 @ 240, 48 @ 288 (0 = unknown → 16). Asking
    /// <c>LoadImageW</c> for exactly this size picks the matching <c>.ico</c> frame instead of letting the shell resample a
    /// larger one.</summary>
    public static int SmallIconSizeForDpi(uint dpi) => dpi == 0 ? 16 : (int)((16u * dpi + 48u) / 96u);

    /// <summary>Add the icon to the notification area (<c>NIM_ADD</c> with <c>NIF_SHOWTIP</c>, then
    /// <c>NIM_SETVERSION(4)</c>). Idempotent. Returns whether the shell holds it now; a refusal is remembered and retried
    /// when explorer next (re)starts.</summary>
    public bool Show()
    {
        if (_disposed) return false;
        _wantShown = true;
        return _added || AddToShell();
    }

    /// <summary>Remove the icon (<c>NIM_DELETE</c>). Idempotent; the icon keeps its image and tip for a later
    /// <see cref="Show"/>.</summary>
    public void Hide()
    {
        _wantShown = false;
        DeleteFromShell();
    }

    /// <summary>Load <paramref name="icoPath"/> at the frame size the taskbar's DPI selects
    /// (<see cref="SmallIconSizeForDpi"/>), push it if the icon is shown, and reload it automatically when that size
    /// changes. False when the file cannot be loaded (the previous image stays).</summary>
    public bool SetIcon(string icoPath) => LoadIcon(icoPath, SmallIconSizeForDpi(_shell.TaskbarDpi), autoSize: true);

    /// <summary>Load <paramref name="icoPath"/> at exactly <paramref name="sizePx"/> (<c>LoadImageW</c> with an explicit
    /// size picks the matching <c>ICONDIR</c> frame) and push it if the icon is shown (<c>NIM_MODIFY(NIF_ICON)</c>). The
    /// previous icon handle is destroyed after the shell has the new one. A non-positive size means
    /// <see cref="SetIcon(string)"/>. False when the file cannot be loaded (the previous image stays).</summary>
    public bool SetIcon(string icoPath, int sizePx)
        => sizePx <= 0 ? SetIcon(icoPath) : LoadIcon(icoPath, sizePx, autoSize: false);

    /// <summary>Set the tooltip (<c>NIM_MODIFY(NIF_TIP | NIF_SHOWTIP)</c> when shown), cut to <see cref="MaxTipChars"/>.
    /// Returns false only when the shell refused a push to a shown icon.</summary>
    public bool SetTip(string tip)
    {
        if (_disposed) return false;
        _tip = ClampTip(tip);
        return !_added || Modify(P.NIF_TIP | P.NIF_SHOWTIP);
    }

    /// <summary>The icon's rectangle in physical screen px (<c>Shell_NotifyIconGetRect</c>) — the anchor for anything
    /// shown next to it. False while the icon is not shown or the shell cannot say (an icon in the overflow flyout while
    /// it is closed).</summary>
    public bool TryGetRect(out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (!TryGetRectCore(out RECT rc)) return false;
        x = rc.left; y = rc.top; width = rc.right - rc.left; height = rc.bottom - rc.top;
        return true;
    }

    /// <summary>
    /// Show a native popup menu at the anchor (use the anchor <see cref="NotifyIconEvent.ContextMenu"/> delivered) and
    /// return the chosen row's id, or 0 when the menu was dismissed. Blocks in the menu's modal loop (messages keep
    /// flowing to every window of the thread). Does the documented dance: <c>SetForegroundWindow</c> on the callback
    /// window before <c>TrackPopupMenuEx</c> and a posted <c>WM_NULL</c> after, or the menu would not close when the user
    /// clicks elsewhere (KB Q135788); keeps clear of the icon's rect; hands focus back to the notification area
    /// (<c>NIM_SETFOCUS</c>) when a keyboard-opened menu is cancelled. On a dark taskbar the menu is dark on Windows 10
    /// 1903+ through <c>uxtheme</c>'s undocumented ordinals 135/136 — probed once, fail-soft to a light menu.
    /// </summary>
    public int ShowMenu(ReadOnlySpan<NotifyMenuItem> items, int anchorX, int anchorY)
    {
        if (_disposed || _hwnd == HWND.NULL || items.IsEmpty) return 0;
        HMENU menu = CreatePopupMenu();
        if (menu == HMENU.NULL) return 0;
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                ref readonly NotifyMenuItem item = ref items[i];
                uint flags = NotifyMenuModel.AppendFlags(in item);
                if (NotifyMenuModel.IsSeparator(in item)) { AppendMenuW(menu, flags, 0, null); continue; }
                fixed (char* text = item.Text ?? string.Empty)
                    AppendMenuW(menu, flags, NotifyMenuModel.CommandId(in item), text);
            }
            int defaultIndex = NotifyMenuModel.DefaultIndex(items);
            if (defaultIndex >= 0) SetMenuDefaultItem(menu, (uint)defaultIndex, 1 /* by position */);

            POINT cursor;
            bool keyboardInvoked = GetCursorPos(&cursor) != 0
                && NotifyMenuModel.IsKeyboardInvocation(anchorX, anchorY, cursor.x, cursor.y);
            bool haveRect = TryGetRectCore(out RECT iconRect);
            uint trackFlags = NotifyMenuModel.TrackFlags(GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0, haveRect);
            TPMPARAMS tpm = default;
            tpm.cbSize = (uint)sizeof(TPMPARAMS);
            tpm.rcExclude = iconRect;

            // The taskbar may have flipped since the last broadcast reached us; the menu follows it, not the app theme.
            bool dark = DarkMenuPolicy.WantDark(ReadTaskbarLight());
            int previousMode = 0;
            bool themed = dark && DarkMenu.TryEnter(out previousMode);
            int result;
            SetForegroundWindow(_hwnd);
            try
            {
                result = (int)TrackPopupMenuEx(menu, trackFlags, anchorX, anchorY, _hwnd, haveRect ? &tpm : null);
            }
            finally
            {
                if (themed) DarkMenu.Leave(previousMode);
            }
            PostMessageW(_hwnd, WM_NULL, 0, 0);

            int chosen = NotifyMenuModel.ChosenId(result, items);
            if (NotifyMenuModel.ShouldReturnFocus(chosen, keyboardInvoked) && _added)
            {
                NOTIFYICONDATAW nid = NewData(0);
                Shell_NotifyIconW(P.NIM_SETFOCUS, &nid);
            }
            return chosen;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary><c>NIM_DELETE</c>, then destroy the callback window (on its own thread; posted there when disposed from
    /// another) and the icon handle. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
        _wantShown = false;
        DeleteFromShell();
        HWND hwnd = _hwnd;
        if (hwnd != HWND.NULL)
        {
            // A window may only be destroyed by its own thread; WM_NCDESTROY frees the GCHandle either way.
            if (GetWindowThreadProcessId(hwnd, null) == GetCurrentThreadId()) DestroyWindow(hwnd);
            else PostMessageW(hwnd, DestroyMessage, 0, 0);
        }
        if (_icon != HICON.NULL) { DestroyIcon(_icon); _icon = HICON.NULL; }
    }

    // ── shell calls ──────────────────────────────────────────────────────────────────────────────────────────────────

    private NOTIFYICONDATAW NewData(uint flags)
    {
        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.hWnd = _hwnd;
        nid.uID = IconUid;
        nid.uFlags = flags;
        if (_identity != Guid.Empty)
        {
            nid.uFlags |= P.NIF_GUID;
            nid.guidItem = _identity;
        }
        return nid;
    }

    private void Fill(ref NOTIFYICONDATAW nid)
    {
        nid.uCallbackMessage = CallbackMessage;
        nid.hIcon = _icon;
        string tip = _tip;
        int n = tip.Length;   // already ≤ MaxTipChars
        for (int i = 0; i < n; i++) nid.szTip[i] = tip[i];
        nid.szTip[n] = '\0';
    }

    private bool AddToShell()
    {
        if (_hwnd == HWND.NULL) return false;
        uint flags = P.NIF_MESSAGE | P.NIF_TIP | P.NIF_SHOWTIP | (_icon != HICON.NULL ? P.NIF_ICON : 0);
        NOTIFYICONDATAW nid = NewData(flags);
        Fill(ref nid);
        bool ok = Shell_NotifyIconW(P.NIM_ADD, &nid) != 0;
        if (!ok)
        {
            // A registration the shell still holds (a previous instance that died without NIM_DELETE) blocks the add.
            // Delete it and try exactly once more.
            NOTIFYICONDATAW stale = NewData(0);
            Shell_NotifyIconW(P.NIM_DELETE, &stale);
            ok = Shell_NotifyIconW(P.NIM_ADD, &nid) != 0;
        }
        if (!ok)
        {
            Diag.Line(_identity != Guid.Empty
                ? $"[notifyicon] NIM_ADD refused (guid={_identity}): no shell yet, or the GUID is bound to another exe path — retried on TaskbarCreated"
                : "[notifyicon] NIM_ADD refused: no shell yet — retried on TaskbarCreated");
            return false;
        }
        // "Must be called every time a notification area icon is added" — version 4 events, anchors, NIN_KEYSELECT.
        nid.uVersion = P.NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(P.NIM_SETVERSION, &nid);
        _added = true;
        return true;
    }

    private bool Modify(uint flags)
    {
        if (!_added || _hwnd == HWND.NULL) return false;
        NOTIFYICONDATAW nid = NewData(flags);
        Fill(ref nid);
        return Shell_NotifyIconW(P.NIM_MODIFY, &nid) != 0;
    }

    private void DeleteFromShell()
    {
        if (!_added || _hwnd == HWND.NULL) return;
        _added = false;
        NOTIFYICONDATAW nid = NewData(0);
        Shell_NotifyIconW(P.NIM_DELETE, &nid);
    }

    private bool LoadIcon(string icoPath, int sizePx, bool autoSize)
    {
        if (_disposed || string.IsNullOrEmpty(icoPath)) return false;
        HICON loaded;
        fixed (char* p = icoPath)
            loaded = (HICON)(void*)LoadImageW(HINSTANCE.NULL, p, IMAGE_ICON, sizePx, sizePx, LR_LOADFROMFILE);
        if (loaded == HICON.NULL)
        {
            Diag.Line($"[notifyicon] icon not loaded: {icoPath} @ {sizePx}px (GetLastError=0x{(uint)Marshal.GetLastPInvokeError():X8})");
            return false;
        }
        HICON previous = _icon;
        _icon = loaded;
        _iconPath = icoPath;
        _iconAutoSize = autoSize;
        bool ok = !_added || Modify(P.NIF_ICON);
        if (previous != HICON.NULL) DestroyIcon(previous);   // the shell copied the new one during the call
        return ok;
    }

    private bool TryGetRectCore(out RECT rect)
    {
        rect = default;
        if (!_added || _hwnd == HWND.NULL) return false;
        NOTIFYICONIDENTIFIER id = default;
        id.cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER);
        id.hWnd = _hwnd;
        id.uID = IconUid;
        id.guidItem = _identity;
        RECT rc;
        if (Shell_NotifyIconGetRect(&id, &rc) < 0) return false;
        rect = rc;
        return rc.right > rc.left && rc.bottom > rc.top;
    }

    // ── shell facts ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static NotifyIconShellState ReadShellState() => new(ReadTaskbarDpi(), ReadTaskbarLight());

    private static uint ReadTaskbarDpi()
    {
        HMONITOR monitor = HMONITOR.NULL;
        fixed (char* tray = "Shell_TrayWnd")
        {
            HWND taskbar = FindWindowW(tray, null);   // the primary taskbar: the only one with a notification area
            if (taskbar != HWND.NULL) monitor = MonitorFromWindow(taskbar, MONITOR_DEFAULTTOPRIMARY);
        }
        if (monitor == HMONITOR.NULL) monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        uint dpiX = 0, dpiY = 0;
        if (monitor == HMONITOR.NULL || GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY) < 0 || dpiX == 0)
            return 96;
        return dpiX;
    }

    private static bool ReadTaskbarLight()
    {
        uint data = 0, cb = sizeof(uint);
        int status;
        fixed (char* key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
        fixed (char* value = "SystemUsesLightTheme")
            status = RegGetValueW(HKEY_CURRENT_USER, key, value, RRF_RT_REG_DWORD, null, &data, &cb);
        return NotifyIconShellState.TaskbarLightFromRegistry(status, data);
    }

    /// <summary>Re-read the shell facts; when they changed, reload an auto-sized icon whose frame size moved, then raise
    /// <see cref="NotifyIconEvent.ShellChanged"/> (unless <paramref name="raise"/> is false).</summary>
    private void RefreshShellState(bool raise)
    {
        NotifyIconShellState next = ReadShellState();
        if (!NotifyIconShellState.Changed(_shell, next)) return;
        bool reload = _iconAutoSize && _iconPath is not null && NotifyIconShellState.IconSizeChanged(_shell, next);
        _shell = next;
        if (reload) LoadIcon(_iconPath!, SmallIconSizeForDpi(next.TaskbarDpi), autoSize: true);
        if (raise) Raise(NotifyIconEvent.ShellChanged, 0, 0);
    }

    private void OnTaskbarCreated()
    {
        _added = false;                  // explorer restarted: whatever it held is gone
        RefreshShellState(raise: false); // its DPI or theme may have changed while it was down; Recreated says "refresh all"
        if (_wantShown) AddToShell();
        Raise(NotifyIconEvent.Recreated, 0, 0);
    }

    private void Raise(NotifyIconEvent e, int x, int y)
    {
        Action<NotifyIconEvent, int, int>? handler = Activated;
        if (handler is null) return;
        try { handler(e, x, y); }
        catch (Exception ex) { Diag.Line($"[notifyicon] Activated handler threw on {e}: {ex}"); }
    }

    // ── the callback window ──────────────────────────────────────────────────────────────────────────────────────────

    private static HWND CreateCallbackWindow(GCHandle self)
    {
        HINSTANCE hinst = GetModuleHandleW(null);
        if (s_taskbarCreatedMsg == 0)
            fixed (char* name = "TaskbarCreated")
                s_taskbarCreatedMsg = RegisterWindowMessageW(name);
        fixed (char* cn = ClassName)
        {
            // Registered once per process and never unregistered. A concurrent second registration fails with
            // ERROR_CLASS_ALREADY_EXISTS, which is harmless: the window below is created by class NAME.
            WNDCLASSEXW wc = default;
            wc.cbSize = (uint)sizeof(WNDCLASSEXW);
            wc.lpfnWndProc = &WndProc;
            wc.hInstance = hinst;
            wc.lpszClassName = cn;
            RegisterClassExW(&wc);
        }
        fixed (char* cn = ClassName)
        fixed (char* title = "FluentGpu notify icon")
        {
            // A zero-sized, never-shown, unowned top-level tool window: in no taskbar, no Alt+Tab, yet on the
            // broadcast list for TaskbarCreated / WM_SETTINGCHANGE / WM_DISPLAYCHANGE.
            return CreateWindowExW(WS_EX_TOOLWINDOW, cn, title, 0, 0, 0, 0, 0, HWND.NULL, HMENU.NULL, hinst,
                (void*)GCHandle.ToIntPtr(self));
        }
    }

    [UnmanagedCallersOnly]
    private static LRESULT WndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WM_NCCREATE)
        {
            CREATESTRUCTW* cs = (CREATESTRUCTW*)(nint)lParam;
            SetWindowLongPtrW(hWnd, GWLP_USERDATA, (nint)cs->lpCreateParams);
        }
        nint ud = GetWindowLongPtrW(hWnd, GWLP_USERDATA);
        if (ud != 0 && GCHandle.FromIntPtr(ud).Target is NotifyIcon self && self.HandleMessage(hWnd, msg, wParam, lParam, out LRESULT result))
            return result;
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool HandleMessage(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = 0;
        if (msg == CallbackMessage)
        {
            if (TryDecode((nuint)wParam, (nint)lParam, out NotifyIconEvent e, out int x, out int y)) Raise(e, x, y);
            return true;
        }
        if (s_taskbarCreatedMsg != 0 && msg == s_taskbarCreatedMsg)
        {
            OnTaskbarCreated();
            return true;
        }
        switch (msg)
        {
            case WM_SETTINGCHANGE:   // "ImmersiveColorSet" (light/dark) and scaling changes both arrive here
            case WM_DISPLAYCHANGE:   // monitor topology / primary monitor / resolution
            case WM_DPICHANGED:
                if (!_disposed) RefreshShellState(raise: true);
                return false;        // DefWindowProc still runs
            case WM_QUERYENDSESSION:
                result = 1;          // never block a logoff; the app's own window decides how the process leaves
                return true;
            case WM_CLOSE:
                return true;         // Dispose owns this window's lifetime — DefWindowProc would destroy it under the icon
            case DestroyMessage:
                DestroyWindow(hWnd);
                return true;
            case WM_NCDESTROY:
                SetWindowLongPtrW(hWnd, GWLP_USERDATA, 0);
                _hwnd = HWND.NULL;
                if (_self.IsAllocated) _self.Free();
                return false;
        }
        return false;
    }

    /// <summary>The <c>uxtheme</c> dark-menu opt-in (<see cref="DarkMenuPolicy"/>): probed once per process, by ordinal.</summary>
    private static class DarkMenu
    {
        private static int s_probe;   // 0 = not probed, 1 = usable, -1 = unavailable (logged once)
        private static delegate* unmanaged<int, int> s_setPreferredAppMode;
        private static delegate* unmanaged<void> s_flushMenuThemes;

        public static bool TryEnter(out int previousMode)
        {
            previousMode = 0;
            if (!EnsureProbed()) return false;
            previousMode = s_setPreferredAppMode(DarkMenuPolicy.ForceDark);
            s_flushMenuThemes();
            return true;
        }

        public static void Leave(int previousMode)
        {
            s_setPreferredAppMode(previousMode);
            s_flushMenuThemes();
        }

        private static bool EnsureProbed()
        {
            if (s_probe != 0) return s_probe > 0;
            s_probe = -1;
            if (!DarkMenuPolicy.OrdinalsUsable(Packaging.PackageIdentity.OsBuild))
            {
                Diag.Line($"[notifyicon] dark menu unavailable: build {Packaging.PackageIdentity.OsBuild} < {DarkMenuPolicy.MinimumBuild}");
                return false;
            }
            HMODULE uxtheme;
            fixed (char* name = "uxtheme.dll")
            {
                uxtheme = GetModuleHandleW(name);
                if (uxtheme == HMODULE.NULL) uxtheme = LoadLibraryExW(name, HANDLE.NULL, 0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
            }
            if (uxtheme == HMODULE.NULL)
            {
                Diag.Line("[notifyicon] dark menu unavailable: uxtheme.dll not loaded");
                return false;
            }
            void* set = GetProcAddress(uxtheme, (sbyte*)DarkMenuPolicy.SetPreferredAppModeOrdinal);
            void* flush = GetProcAddress(uxtheme, (sbyte*)DarkMenuPolicy.FlushMenuThemesOrdinal);
            if (set == null || flush == null)
            {
                Diag.Line("[notifyicon] dark menu unavailable: uxtheme ordinals 135/136 not exported");
                return false;
            }
            s_setPreferredAppMode = (delegate* unmanaged<int, int>)set;
            s_flushMenuThemes = (delegate* unmanaged<void>)flush;
            s_probe = 1;
            return true;
        }
    }
}
