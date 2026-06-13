using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;   // Point2, KeyModifiers, DropEffect
using FluentGpu.Hooks;        // InputHooks.Current
using TerraFX.Interop.Windows;
using ComCcw = FluentGpu.Text.DirectWrite.ComCcw;   // S_OK / IID_IUnknown / IID_IDropTarget (DWriteItemizer.cs)

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The OLE <c>IDropTarget</c> that bridges an OS file/folder drag (Explorer / the desktop) into the engine's
/// external-drop seam, restoring DRAG-OVER HOVER feedback (so an app can light up a "Drop to Deploy"-style overlay
/// while a file is dragged over the window) and the OS drop-effect cursor ("+Copy"/no-drop).
///
/// <para><b>Why a HAND-ROLLED vtable (not <c>[GeneratedComInterface]</c>).</b> The prior source-generated IDropTarget
/// crashed on drop — the suspected cause was the by-value <c>POINTL</c> parameter under the COM source generator's
/// marshalling (an uncatchable AccessViolation). This CCW declares the exact unmanaged thunk ABI itself — <c>POINTL</c>
/// is a real 8-byte by-value struct in <c>[UnmanagedCallersOnly(CallConvMemberFunction)]</c> signatures, honored
/// identically on x64 and ARM64 — the same proven pattern as <c>DWriteItemizer.cs</c>'s CCWs. </para>
///
/// <para><b>De-risked data handling.</b> HOVER (DragEnter/Over) does ZERO data transfer: DragEnter only
/// <c>QueryGetData(CF_HDROP)</c> (a cheap "do you offer files" check) to decide acceptance, then reports an effect.
/// Only DROP reads the file list (<c>GetData(CF_HDROP)</c> → HDROP → <c>DragQueryFileW</c>) — the same enumeration the
/// previous (working) <c>WM_DROPFILES</c> path used, run exactly once. Every thunk swallows managed exceptions and
/// returns an HRESULT — nothing crosses the COM boundary.</para>
///
/// <para><b>Threading.</b> OLE delivers the callbacks on the registering (UI) thread during the drag loop — the same
/// thread the dispatcher runs on — so the forwarded <c>InputHooks.ExternalDrag*</c> calls are synchronous and
/// single-threaded. <c>RegisterDragDrop</c> requires the thread to be STA (the gallery is <c>[STAThread]</c>).</para>
/// </summary>
internal static unsafe partial class Win32DropTarget
{
    private const ushort CF_HDROP = 15;
    private const uint MK_SHIFT = 0x0004, MK_CONTROL = 0x0008;
    private const uint DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;
    private const int S_OK = 0, S_FALSE = 1, RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL { public int x, y; }   // POINTL / POINT: { LONG x; LONG y } — 8 bytes by value

    // ── OLE / shell / user32 entry points (declared locally so the CCW passes as an opaque nint) ──
    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint pvReserved);
    [LibraryImport("ole32.dll")] private static partial void OleUninitialize();
    [LibraryImport("ole32.dll")] private static partial int RegisterDragDrop(nint hwnd, nint pDropTarget);
    [LibraryImport("ole32.dll")] private static partial int RevokeDragDrop(nint hwnd);
    [LibraryImport("ole32.dll")] private static partial void ReleaseStgMedium(STGMEDIUM* pmedium);
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")] private static partial uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ScreenToClient(nint hWnd, PointL* lpPoint);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint hWnd);

    /// <summary>OLE-init this STA thread + register a fresh CCW on <paramref name="hwnd"/>. Returns a token to pass to
    /// <see cref="Revoke"/>, or null when drag-drop is unavailable (non-STA thread, or RegisterDragDrop failed).</summary>
    internal static DropRegistration? Register(nint hwnd)
    {
        if (hwnd == 0) return null;
        int oi = OleInitialize(0);
        if (oi == RPC_E_CHANGED_MODE) return null;   // thread is MTA → OLE drag-drop can't run here
        bool oleInited = oi == S_OK || oi == S_FALSE;

        Win32DropTargetCcw* ccw = Win32DropTargetCcw.Create(hwnd);
        int hr = RegisterDragDrop(hwnd, (nint)ccw);
        if (hr < 0)
        {
            Win32DropTargetCcw.Destroy(ccw);
            if (oleInited) OleUninitialize();
            return null;
        }
        return new DropRegistration(hwnd, (nint)ccw, oleInited);
    }

    /// <summary>Revoke the registration and free the CCW (mirror of <see cref="Register"/>).</summary>
    internal static void Revoke(DropRegistration? reg)
    {
        if (reg is not { } r) return;
        RevokeDragDrop(r.Hwnd);                       // releases OLE's ref → back to our single ref
        if (r.Ccw != 0) Win32DropTargetCcw.Destroy((Win32DropTargetCcw*)r.Ccw);
        if (r.OleInited) OleUninitialize();
    }

    /// <summary>
    /// In-process vtable round-trip — proves the hand-rolled CCW dispatches correctly through its function-pointer
    /// vtable, EXERCISING THE BY-VALUE <c>POINTL</c> ABI (the element that crashed the source-gen attempt) without a
    /// real drag. Calls QI(IID_IDropTarget) + all four IDropTarget methods with a NULL data object (the no-files fast
    /// path) and a synthetic by-value point; asserts no crash and sane effects. Returns true on success.
    /// </summary>
    internal static bool SelfTest(out string detail)
    {
        Win32DropTargetCcw* ccw = Win32DropTargetCcw.Create(0);
        try
        {
            void** vt = ccw->Vtbl;

            Guid iid = ComCcw.IID_IDropTarget;
            void* ppv = null;
            var qi = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, Guid*, void**, int>)vt[0];
            int hrQi = qi(ccw, &iid, &ppv);
            bool qiOk = hrQi == S_OK && ppv == ccw;

            var enter = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, PointL, uint*, int>)vt[3];
            var over  = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint, PointL, uint*, int>)vt[4];
            var leave = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, int>)vt[5];
            var drop  = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, PointL, uint*, int>)vt[6];

            var pt = new PointL { x = 123, y = 456 };           // the by-value POINTL under test

            uint eff = DROPEFFECT_COPY | DROPEFFECT_MOVE;
            int hrE = enter(ccw, null, 0, pt, &eff);             // null data → no files → effect NONE
            bool enterOk = hrE == S_OK && eff == DROPEFFECT_NONE;

            eff = DROPEFFECT_COPY;
            int hrO = over(ccw, 0, pt, &eff);
            int hrL = leave(ccw);

            eff = DROPEFFECT_COPY;
            int hrD = drop(ccw, null, 0, pt, &eff);              // null data → no paths → not accepted → NONE
            bool dropOk = hrD == S_OK && eff == DROPEFFECT_NONE;

            detail = $"qi={qiOk}(hr=0x{hrQi:X}) enter={enterOk} over=hr0x{hrO:X} leave=hr0x{hrL:X} drop={dropOk}";
            return qiOk && enterOk && hrO == S_OK && hrL == S_OK && dropOk;
        }
        finally { Win32DropTargetCcw.Destroy(ccw); }
    }

    // ── helpers shared by the CCW thunks ───────────────────────────────────────────────────────────────────────────
    private static FORMATETC HdropFormat() => new()
    {
        cfFormat = CF_HDROP,
        ptd = null,
        dwAspect = (uint)DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = (uint)TYMED.TYMED_HGLOBAL,
    };

    /// <summary>True iff the data object offers a file list — a cheap format probe (NO data transfer).</summary>
    internal static bool OffersFiles(void* pDataObj)
    {
        if (pDataObj == null) return false;
        FORMATETC fmt = HdropFormat();
        return ((IDataObject*)pDataObj)->QueryGetData(&fmt) == S_OK;
    }

    /// <summary>Pull the dropped absolute paths out of the data object's CF_HDROP medium (the once-per-drop read).</summary>
    internal static string[] ReadDroppedPaths(void* pDataObj)
    {
        if (pDataObj == null) return Array.Empty<string>();
        var data = (IDataObject*)pDataObj;
        FORMATETC fmt = HdropFormat();
        STGMEDIUM medium;
        if (data->GetData(&fmt, &medium) < 0) return Array.Empty<string>();
        try
        {
            nint hDrop = (nint)(void*)medium.hGlobal;
            if (hDrop == 0) return Array.Empty<string>();
            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) return Array.Empty<string>();
            var paths = new string[count];
            int filled = 0;
            for (uint i = 0; i < count; i++)
            {
                uint need = DragQueryFileW(hDrop, i, null, 0);
                if (need == 0) continue;
                var arr = new char[need + 1];
                fixed (char* p = arr)
                {
                    uint got = DragQueryFileW(hDrop, i, p, need + 1);
                    if (got == 0) continue;
                    paths[filled++] = new string(p, 0, (int)got);
                }
            }
            if (filled != paths.Length) Array.Resize(ref paths, filled);
            return paths;
        }
        finally { ReleaseStgMedium(&medium); }
    }

    /// <summary>OLE screen POINTL (by-value) → window-DIP point, via ScreenToClient + the window DPI scale.</summary>
    internal static Point2 ToDip(nint hwnd, PointL pt)
    {
        ScreenToClient(hwnd, &pt);
        uint dpi = GetDpiForWindow(hwnd);
        float s = dpi == 0 ? 1f : dpi / 96f;
        return new Point2(pt.x / s, pt.y / s);
    }

    internal static KeyModifiers Mods(uint grfKeyState)
    {
        KeyModifiers m = KeyModifiers.None;
        if ((grfKeyState & MK_CONTROL) != 0) m |= KeyModifiers.Ctrl;
        if ((grfKeyState & MK_SHIFT) != 0) m |= KeyModifiers.Shift;
        return m;
    }

    /// <summary>Map the engine effect to a DROPEFFECT, intersected with what the source allows (proper drop etiquette).</summary>
    internal static uint MapEffect(DropEffect want, uint allowed)
    {
        uint w = want switch
        {
            DropEffect.Copy => DROPEFFECT_COPY,
            DropEffect.Move => DROPEFFECT_MOVE,
            DropEffect.Link => DROPEFFECT_LINK,
            _ => DROPEFFECT_NONE,
        };
        uint hit = w & allowed;
        return hit != 0 ? hit : (w == DROPEFFECT_NONE ? DROPEFFECT_NONE : (allowed & DROPEFFECT_COPY));
    }
}

/// <summary>A live OS-drop registration token (the CCW + its OLE init state) the window holds for its lifetime.</summary>
internal readonly record struct DropRegistration(nint Hwnd, nint Ccw, bool OleInited);

/// <summary>The hand-rolled <c>IDropTarget</c> CCW (vtable + refcount + owner HWND) — modeled on
/// <c>DWriteItemizer.cs</c>'s CCWs. The thunks forward to the engine's <c>InputHooks.ExternalDrag*</c> seam.</summary>
internal unsafe struct Win32DropTargetCcw
{
    public void** Vtbl;     // MUST be first (COM "this" vptr)
    public int Rc;
    public nint Hwnd;       // owner window — for ScreenToClient + the DPI scale

    private static readonly void** _vtbl = Build();

    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, Win32DropTarget.PointL, uint*, int>)&DragEnter;
        v[4] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, uint, Win32DropTarget.PointL, uint*, int>)&DragOver;
        v[5] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, int>)&DragLeave;
        v[6] = (delegate* unmanaged[MemberFunction]<Win32DropTargetCcw*, void*, uint, Win32DropTarget.PointL, uint*, int>)&Drop;
        return v;
    }

    public static Win32DropTargetCcw* Create(nint hwnd)
    {
        var p = (Win32DropTargetCcw*)NativeMemory.Alloc((nuint)sizeof(Win32DropTargetCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Hwnd = hwnd;
        return p;
    }

    public static void Destroy(Win32DropTargetCcw* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(Win32DropTargetCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return unchecked((int)0x80004003);   // E_POINTER
        if (*riid == ComCcw.IID_IUnknown || *riid == ComCcw.IID_IDropTarget)
        { Interlocked.Increment(ref self->Rc); *ppv = self; return ComCcw.S_OK; }
        *ppv = null; return unchecked((int)0x80004002);       // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(Win32DropTargetCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(Win32DropTargetCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int DragEnter(Win32DropTargetCcw* self, void* pDataObj, uint grfKeyState, Win32DropTarget.PointL pt, uint* pdwEffect)
    {
        try
        {
            if (!Win32DropTarget.OffersFiles(pDataObj)) { if (pdwEffect != null) *pdwEffect = 0; return ComCcw.S_OK; }
            var fn = InputHooks.Current.Default.ExternalDragEnter;   // hover: NO data read — empty paths
            DropEffect want = fn?.Invoke(Win32DropTarget.ToDip(self->Hwnd, pt), Array.Empty<string>(), Win32DropTarget.Mods(grfKeyState)) ?? DropEffect.None;
            if (pdwEffect != null) *pdwEffect = Win32DropTarget.MapEffect(want, *pdwEffect);
        }
        catch { if (pdwEffect != null) *pdwEffect = 0; }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int DragOver(Win32DropTargetCcw* self, uint grfKeyState, Win32DropTarget.PointL pt, uint* pdwEffect)
    {
        try
        {
            var fn = InputHooks.Current.Default.ExternalDragOver;
            DropEffect want = fn?.Invoke(Win32DropTarget.ToDip(self->Hwnd, pt), Win32DropTarget.Mods(grfKeyState)) ?? DropEffect.None;
            if (pdwEffect != null) *pdwEffect = Win32DropTarget.MapEffect(want, *pdwEffect);
        }
        catch { if (pdwEffect != null) *pdwEffect = 0; }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int DragLeave(Win32DropTargetCcw* self)
    {
        try { InputHooks.Current.Default.ExternalDragLeave?.Invoke(); }
        catch { /* swallow across the COM boundary */ }
        return ComCcw.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int Drop(Win32DropTargetCcw* self, void* pDataObj, uint grfKeyState, Win32DropTarget.PointL pt, uint* pdwEffect)
    {
        try
        {
            string[] paths = Win32DropTarget.ReadDroppedPaths(pDataObj);   // the once-per-drop file read
            var dip = Win32DropTarget.ToDip(self->Hwnd, pt);
            var mods = Win32DropTarget.Mods(grfKeyState);
            bool accepted = paths.Length > 0 && (InputHooks.Current.Default.ExternalDropFiles?.Invoke(dip, paths, mods) ?? false);
            if (pdwEffect != null) *pdwEffect = accepted ? Win32DropTarget.MapEffect(DropEffect.Copy, *pdwEffect) : 0;
        }
        catch { if (pdwEffect != null) *pdwEffect = 0; }
        return ComCcw.S_OK;
    }
}
