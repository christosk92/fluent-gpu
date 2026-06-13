using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling; // [GeneratedComInterface], [GeneratedComClass]
using FluentGpu.Foundation;
using FluentGpu.Hooks;                              // InputHooks.Current
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The Windows OLE <c>IDropTarget</c> that bridges an OS file/folder drag (Explorer, the desktop — any source offering
/// <c>CF_HDROP</c>) into the portable engine's drag-drop seam. It is the INBOUND OLE twin of the Win32 PAL's other
/// host→tree hooks: on each OLE callback it converts the screen point to window-DIP, parses the dropped paths once at
/// <c>DragEnter</c>, and forwards to the engine through <see cref="InputHooks"/>' external-drop delegates
/// (<c>ExternalDragEnter/Over/Leave/Drop</c>, wired by the AppHost onto the <see cref="InputHooks.Current"/>
/// channel-default → <c>InputDispatcher.ExternalDrag*</c> → <c>DragDropContext</c>). A <c>BoxEl.DropTarget</c> that
/// accepts <see cref="DropKinds.Files"/> then receives Enter/Over/Leave/Drop exactly like an in-app drag; if no node
/// accepts, the engine reports <see cref="DropEffect.None"/> and the OS shows the no-drop cursor.
///
/// <para><b>COM posture (com-interop.md).</b> Implemented (the OS calls us), so it uses the sanctioned cold-COM
/// <c>[GeneratedComInterface]</c>/<c>[GeneratedComClass]</c> + <see cref="StrategyBasedComWrappers"/> path — NO
/// <c>[ComImport]</c>, NO <c>ComWrappers</c> subclassing, NO reflection — the exact shape proven by the WindowsApi
/// pillar's <c>MediaButtonHandler</c>/<c>ToastActivator</c>. The OLE registration entry points (<c>OleInitialize</c>,
/// <c>RegisterDragDrop</c>, …) are declared locally rather than pulled from TerraFX so the native <c>IDropTarget*</c>
/// passes as an opaque <c>nint</c> (the CCW pointer) — no dependency on a TerraFX vtable shape for the call OUT, and no
/// name clash with TerraFX's own <c>IDropTarget</c> struct used nowhere here.</para>
///
/// <para><b>Threading.</b> Every callback fires on the window's UI thread inside the OLE drag loop, the same thread
/// the dispatcher runs on — so the forwarded calls are synchronous and single-threaded (no marshalling hop). All paths
/// swallow managed exceptions and degrade to <c>DROPEFFECT_NONE</c> rather than throwing across the COM boundary.</para>
/// </summary>
[GeneratedComInterface]
[Guid("00000122-0000-0000-C000-000000000046")]
internal partial interface IOleDropTarget
{
    [PreserveSig] unsafe int DragEnter(void* pDataObj, uint grfKeyState, long pt, uint* pdwEffect);
    [PreserveSig] unsafe int DragOver(uint grfKeyState, long pt, uint* pdwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] unsafe int Drop(void* pDataObj, uint grfKeyState, long pt, uint* pdwEffect);
}

/// <summary>The managed <see cref="IOleDropTarget"/> implementation registered on a window via
/// <see cref="Win32DropTarget.Register"/>. Stateless except the paths parsed at the current drag's <c>DragEnter</c>.</summary>
[GeneratedComClass]
internal sealed partial class Win32DropTarget : IOleDropTarget
{
    // DROPEFFECT (ole2.h): a bitfield the OS interprets as the drag cursor.
    private const uint DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;
    // grfKeyState (winuser.h MK_*): the modifier/button flags carried with the drag.
    private const uint MK_SHIFT = 0x0004, MK_CONTROL = 0x0008;

    private const int S_OK = 0;
    private const ushort CF_HDROP = 15;   // winuser.h — the shell file-list clipboard format

    private readonly Func<int, int, Point2> _screenToDip;   // (screenX, screenY) → window DIP — owned by the Win32Window
    private string[]? _paths;                                // parsed once at DragEnter, cleared at Drop/Leave

    internal Win32DropTarget(Func<int, int, Point2> screenToDip) => _screenToDip = screenToDip;

    public unsafe int DragEnter(void* pDataObj, uint grfKeyState, long pt, uint* pdwEffect)
    {
        try
        {
            _paths = ParsePaths(pDataObj);
            if (_paths is null || _paths.Length == 0) { if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE; return S_OK; }

            var fn = InputHooks.Current.Default.ExternalDragEnter;
            DropEffect want = fn?.Invoke(DipOf(pt), _paths, Mods(grfKeyState)) ?? DropEffect.None;
            if (pdwEffect != null) *pdwEffect = Allowed(want, *pdwEffect);
        }
        catch { if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE; }
        return S_OK;
    }

    public unsafe int DragOver(uint grfKeyState, long pt, uint* pdwEffect)
    {
        try
        {
            var fn = InputHooks.Current.Default.ExternalDragOver;
            DropEffect want = fn?.Invoke(DipOf(pt), Mods(grfKeyState)) ?? DropEffect.None;
            if (pdwEffect != null) *pdwEffect = Allowed(want, *pdwEffect);
        }
        catch { if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE; }
        return S_OK;
    }

    public int DragLeave()
    {
        try { InputHooks.Current.Default.ExternalDragLeave?.Invoke(); }
        catch { /* swallow across the COM boundary */ }
        _paths = null;
        return S_OK;
    }

    public unsafe int Drop(void* pDataObj, uint grfKeyState, long pt, uint* pdwEffect)
    {
        try
        {
            // The engine session already holds the paths from DragEnter; re-parse defensively if it somehow missed.
            if (_paths is null || _paths.Length == 0) _paths = ParsePaths(pDataObj);
            var fn = InputHooks.Current.Default.ExternalDrop;
            bool accepted = _paths is { Length: > 0 } && (fn?.Invoke(DipOf(pt), Mods(grfKeyState)) ?? false);
            if (pdwEffect != null) *pdwEffect = accepted ? Allowed(DropEffect.Copy, *pdwEffect) : DROPEFFECT_NONE;
        }
        catch { if (pdwEffect != null) *pdwEffect = DROPEFFECT_NONE; }
        _paths = null;
        return S_OK;
    }

    // POINTL is passed by value (8 bytes, two LONGs) — ABI-identical to a single 64-bit integer on x64 AND ARM64, so
    // it arrives as `long pt`: x is the low dword (offset 0, little-endian), y the high dword.
    private Point2 DipOf(long pt) => _screenToDip(unchecked((int)(pt & 0xFFFFFFFF)), unchecked((int)(pt >> 32)));

    private static KeyModifiers Mods(uint grfKeyState)
    {
        KeyModifiers m = KeyModifiers.None;
        if ((grfKeyState & MK_CONTROL) != 0) m |= KeyModifiers.Ctrl;
        if ((grfKeyState & MK_SHIFT) != 0) m |= KeyModifiers.Shift;
        return m;
    }

    // Map the engine effect to a DROPEFFECT, intersected with what the OLE source allows (proper drop etiquette).
    private static uint Allowed(DropEffect want, uint allowed)
    {
        uint w = want switch
        {
            DropEffect.Copy => DROPEFFECT_COPY,
            DropEffect.Move => DROPEFFECT_MOVE,
            DropEffect.Link => DROPEFFECT_LINK,
            _ => DROPEFFECT_NONE,
        };
        uint hit = w & allowed;
        return hit != 0 ? hit : (w == DROPEFFECT_NONE ? DROPEFFECT_NONE : (allowed & DROPEFFECT_COPY)); // prefer copy fallback
    }

    /// <summary>Pull the absolute paths out of an OLE data object's <c>CF_HDROP</c> medium (files and/or folders, OLE
    /// order). Returns null when the object carries no file list — a non-file drag the engine ignores.</summary>
    private static unsafe string[]? ParsePaths(void* pDataObj)
    {
        if (pDataObj == null) return null;
        var data = (IDataObject*)pDataObj;

        FORMATETC fmt = new()
        {
            cfFormat = CF_HDROP,
            ptd = null,
            dwAspect = (uint)DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = (uint)TYMED.TYMED_HGLOBAL,
        };
        STGMEDIUM medium;
        if (data->GetData(&fmt, &medium) < 0) return null;
        try
        {
            HDROP hDrop = (HDROP)(void*)medium.hGlobal;
            if (hDrop == HDROP.NULL) return null;

            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) return null;

            var paths = new string[count];
            const int Cap = 32768;   // long-path tolerant (\\?\-prefixed paths fit)
            char* buf = stackalloc char[Cap];
            int filled = 0;
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFileW(hDrop, i, buf, (uint)Cap);
                if (len == 0) continue;
                paths[filled++] = new string(buf, 0, (int)len);
            }
            if (filled == 0) return null;
            if (filled != paths.Length) Array.Resize(ref paths, filled);
            return paths;
        }
        finally
        {
            ReleaseStgMedium(&medium);
        }
    }

    // ── OLE registration (the host calls these from the Win32 window) ───────────────────────────────────────────────
    // Declared locally so the CCW passes as an opaque IDropTarget* (nint); no TerraFX IDropTarget vtable dependency.
    private static readonly StrategyBasedComWrappers s_wrappers = new();

    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint pvReserved);
    [LibraryImport("ole32.dll")] private static partial void OleUninitialize();
    [LibraryImport("ole32.dll")] private static partial int RegisterDragDrop(nint hwnd, nint pDropTarget);
    [LibraryImport("ole32.dll")] private static partial int RevokeDragDrop(nint hwnd);

    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary>
    /// OLE-init this (STA) thread and register a fresh drop target on <paramref name="hwnd"/>. Returns the live
    /// registration token (to pass to <see cref="Revoke"/> on teardown), or null when drag-drop is unavailable
    /// (a non-STA thread, or <c>RegisterDragDrop</c> failed) — the window then simply receives no OS drops.
    /// </summary>
    internal static DropRegistration? Register(nint hwnd, Func<int, int, Point2> screenToDip)
    {
        if (hwnd == 0) return null;
        int oi = OleInitialize(0);
        if (oi == RPC_E_CHANGED_MODE) return null;   // thread is MTA: OLE drag-drop can't run here (honest no-op)
        bool oleInited = oi == S_OK || oi == S_FALSE;

        var target = new Win32DropTarget(screenToDip);
        nint unknown = s_wrappers.GetOrCreateComInterfaceForObject(target, CreateComInterfaceFlags.None);
        int hr = RegisterDragDrop(hwnd, unknown);
        if (hr < 0)
        {
            Marshal.Release(unknown);
            if (oleInited) OleUninitialize();
            return null;
        }
        return new DropRegistration(hwnd, unknown, target, oleInited);
    }

    /// <summary>Revoke the registration and release the CCW + OLE refcount (mirror of <see cref="Register"/>).</summary>
    internal static void Revoke(DropRegistration? reg)
    {
        if (reg is not { } r) return;
        RevokeDragDrop(r.Hwnd);
        if (r.Unknown != 0) Marshal.Release(r.Unknown);
        if (r.OleInited) OleUninitialize();
    }
}

/// <summary>A live OS-drop registration — opaque token the Win32 window holds for the window's lifetime.</summary>
internal readonly record struct DropRegistration(nint Hwnd, nint Unknown, Win32DropTarget Target, bool OleInited);
