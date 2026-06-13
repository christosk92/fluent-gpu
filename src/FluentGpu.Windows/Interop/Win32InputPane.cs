using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The Windows SIP (software input panel / touch keyboard) implementation of the <c>IPlatformTextInput</c> trigger
/// seam (input-a11y.md §10): WinRT <c>Windows.UI.ViewManagement.InputPane</c> obtained for the engine HWND through the
/// classic-COM bridge <c>IInputPaneInterop::GetForWindow</c> (the desktop-window analogue of <c>InputPane.GetForCurrentView</c>,
/// which has no CoreWindow here), then <c>IInputPane2::TryShow</c>/<c>TryShide</c> to raise/dismiss the keyboard and
/// <c>IInputPane</c>'s <c>OccludedRect</c> + <c>Showing</c>/<c>Hiding</c> events to drive the focused-editor reflow
/// (WinUI <c>CInputPaneHandler::Showing</c> — dxaml\xcp\core\input\InputPaneHandler.cpp). All WinRT is confined to this
/// FluentGpu.Windows leaf; the portable engine only ever sees the <c>IPlatformTextInput</c> surface.
///
/// <para>NativeAOT/COM posture (com-interop.md): a hand-vtable consume path (<c>lpVtbl[n]</c> calli) — no
/// <c>ComWrappers</c>/source-gen marshalling — and the cold WinRT entry points (<c>RoGetActivationFactory</c>,
/// <c>WindowsCreateString</c>) declared locally, exactly as this PAL declares the Win32 ABI shapes TerraFX omits. This
/// is a COLD path (focus transitions, user-rate), so the typing-edge zero-alloc rule does not bind it. Every step
/// degrades gracefully: a desktop without a touch keyboard, or a denied activation, leaves the field disabled and the
/// <c>TryShow/Hide</c> calls return false — never throw (the WinUI <c>TryShow</c> is equally best-effort).</para>
/// </summary>
public sealed unsafe partial class Win32TextInput
{
    // WinRT activation + HSTRING (combase.dll) — not in the TerraFX static-import surface, declared locally.
    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, void** factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    // The WinRT runtime class whose activation factory implements IInputPaneInterop.
    private const string RuntimeClass_InputPane = "Windows.UI.ViewManagement.InputPane";

    // IIDs (winrt\inputpaneinterop.h + the Windows.UI.ViewManagement projection). IInputPaneInterop is the classic-COM
    // bridge factory interface; IInputPane2 carries TryShow/TryHide; IInputPane carries OccludedRect + Showing/Hiding.
    private static readonly Guid IID_IInputPaneInterop = new("75CF2C57-9195-4931-8332-F0B409E916AF");
    private static readonly Guid IID_IInputPane2       = new("8A6B3F26-7090-4793-944C-C3F2CDE26276");
    private static readonly Guid IID_IInputPane        = new("640ADE6A-D502-4615-9B92-DC242188AF0F");
    // TypedEventHandler<InputPane, InputPaneVisibilityEventArgs> parameterized IID (the add_Showing/add_Hiding delegate
    // type). add_* QIs the handler for THIS iid before invoking it, so the hand-built callback answers it (and IUnknown).
    private static readonly Guid IID_VisibilityHandler = new("9DDB325F-DEFA-5C36-A4D9-2767732CB7FF");

    // WinRT Rect (Windows.Foundation.Rect): four floats (X, Y, Width, Height) in DIPs — InputPane.OccludedRect is in
    // device-independent pixels relative to the app's coordinate space, which for a per-monitor-aware desktop window
    // matches the engine's window-DIP space after the client-origin shift below.
    [StructLayout(LayoutKind.Sequential)]
    private struct WinRtRect { public float X, Y, Width, Height; }

    private void* _inputPane;      // IInputPane*  (the OccludedRect + events surface) — null = SIP unavailable
    private void* _inputPane2;     // IInputPane2* (TryShow/TryHide)
    private bool _sipResolved;     // GetForWindow attempted (success or failure cached — never retried per call)
    private bool _sipShown;        // a TryShow not yet balanced by a TryHide (best-effort bookkeeping)
    private long _showingToken, _hidingToken;
    private GCHandle _handlerSelf; // pins the TypedEventHandler callback object (its native vtable points back here)

    /// <summary>Public SIP event (the portable seam): the host subscribes and reflows the focused editor's caret above
    /// the reported region. Fired from the InputPane Showing/Hiding callbacks on the UI thread (the WinRT event is
    /// delivered on the window's dispatcher thread), or synchronously from <see cref="TryShowTouchKeyboard"/>/<see cref="TryHideTouchKeyboard"/>.</summary>
    public event Action<RectF>? OccludedRectChanged;

    public bool TryShowTouchKeyboard()
    {
        if (!EnsureInputPane()) return false;
        // IInputPane2::TryShow(out boolean) — vtable slot 6 (IInspectable is 0..5). Best-effort: a denied request (no
        // touch keyboard / policy) returns S_OK with *shown == false, which we surface as false without throwing.
        byte shown = 0;
        int hr = ((delegate* unmanaged<void*, byte*, int>)(*(void***)_inputPane2)[6])(_inputPane2, &shown);
        if (hr < 0) return false;
        _sipShown = shown != 0;
        // Fire the occluded-rect now too (the Showing event lands asynchronously; an immediate read lets the reflow run
        // this same frame). A still-animating pane may report a zero rect here — the async Showing then supplies the
        // final docked rect, and the host's reflow is idempotent (a zero Y is a no-op).
        FireCurrentOccludedRect();
        return _sipShown;
    }

    public bool TryHideTouchKeyboard()
    {
        if (_inputPane2 is null) return false;
        byte hidden = 0;
        int hr = ((delegate* unmanaged<void*, byte*, int>)(*(void***)_inputPane2)[7])(_inputPane2, &hidden);   // TryHide @ slot 7
        if (hr < 0) return false;
        _sipShown = false;
        RaiseOccluded(default);   // the keyboard is gone → empty rect undoes the reflow
        return hidden != 0;
    }

    /// <summary>Lazily obtain the per-window <c>InputPane</c> (once; the result is cached). Returns false on any desktop
    /// without WinRT InputPane support, so the caller leaves the SIP disabled.</summary>
    private bool EnsureInputPane()
    {
        if (_sipResolved) return _inputPane2 is not null;
        _sipResolved = true;

        // 1) Activation factory for "Windows.UI.ViewManagement.InputPane" as IInputPaneInterop.
        nint classId = 0;
        if (WindowsCreateString(RuntimeClass_InputPane, (uint)RuntimeClass_InputPane.Length, &classId) < 0 || classId == 0)
            return false;
        void* interop = null;
        try
        {
            Guid iid = IID_IInputPaneInterop;
            if (RoGetActivationFactory(classId, &iid, &interop) < 0 || interop is null) return false;
        }
        finally { WindowsDeleteString(classId); }

        try
        {
            // 2) IInputPaneInterop::GetForWindow(HWND, REFIID iid, void** ppv) — vtable slot 6 (IInspectable 0..5).
            //    Ask straight for IInputPane (the OccludedRect/events surface).
            void* pane = null;
            Guid paneIid = IID_IInputPane;
            int hr = ((delegate* unmanaged<void*, nint, Guid*, void**, int>)(*(void***)interop)[6])(interop, _hwnd, &paneIid, &pane);
            if (hr < 0 || pane is null) return false;
            _inputPane = pane;

            // 3) QI IInputPane2 (TryShow/TryHide). QueryInterface is IUnknown slot 0.
            void* pane2 = null;
            Guid pane2Iid = IID_IInputPane2;
            hr = ((delegate* unmanaged<void*, Guid*, void**, int>)(*(void***)pane)[0])(pane, &pane2Iid, &pane2);
            if (hr < 0 || pane2 is null) { Release(ref _inputPane); return false; }
            _inputPane2 = pane2;

            // 4) Subscribe Showing/Hiding for live updates (rotation / pane resize). Best-effort — a failure here leaves
            //    the get_OccludedRect-after-TryShow path (above) as the reflow source, so the SIP still works.
            SubscribeVisibilityEvents();
            return true;
        }
        finally { Release(ref interop); }
    }

    /// <summary>Read <c>IInputPane::get_OccludedRect</c> (slot 6) and raise it through the portable seam.</summary>
    private void FireCurrentOccludedRect()
    {
        if (_inputPane is null) return;
        WinRtRect r;
        int hr = ((delegate* unmanaged<void*, WinRtRect*, int>)(*(void***)_inputPane)[6])(_inputPane, &r);
        if (hr < 0) return;
        RaiseOccluded(ToClientDip(in r));
    }

    private void RaiseOccluded(in RectF dip) => OccludedRectChanged?.Invoke(dip);

    // InputPane.OccludedRect is in screen DIPs (the app's coordinate space). Convert to CLIENT DIP: the engine's window
    // space starts at the client (0,0). The pane is screen-physical-pixel docked; the WinRT rect is already DIP, so only
    // the client-origin shift remains (origin is physical px → DIP via the window scale, the same bridge the wheel uses).
    private RectF ToClientDip(in WinRtRect r)
    {
        if (r.Width <= 0f && r.Height <= 0f) return default;   // hidden / zero-height (HoloLens) → the no-op empty rect
        POINT origin = default;
        ClientToScreen((HWND)_hwnd, &origin);
        float s = ScaleHint <= 0f ? 1f : ScaleHint;
        return new RectF(r.X - origin.x / s, r.Y - origin.y / s, r.Width, r.Height);
    }

    /// <summary>The window DPI scale (px per DIP). The SIP rect arithmetic needs it for the client-origin shift; queried
    /// live from the HWND so a per-monitor DPI move is reflected without re-plumbing.</summary>
    private float ScaleHint
    {
        get { uint dpi = GetDpiForWindow((HWND)_hwnd); return dpi == 0 ? 1f : dpi / 96f; }
    }

    private static void Release(ref void* p)
    {
        if (p is null) return;
        ((delegate* unmanaged<void*, uint>)(*(void***)p)[2])(p);   // IUnknown::Release @ slot 2
        p = null;
    }

    // ── TypedEventHandler<InputPane, InputPaneVisibilityEventArgs> as a hand-built COM callback ───────────────────────
    // add_Showing/add_Hiding take a delegate object; WinRT QIs it for IID_VisibilityHandler then calls Invoke (vtable
    // slot 3, after QI/AddRef/Release) with (sender, args). We don't read the args — the occluded rect is pulled from
    // get_OccludedRect when the event fires (the WinUI handler likewise re-reads state on the notification). One shared
    // static vtable; the GCHandle in *(this+1) routes Invoke back to the owning Win32TextInput. A non-refcounted
    // singleton-lifetime object (lives as long as the window), so AddRef/Release are no-ops and never free it.

    [StructLayout(LayoutKind.Sequential)]
    private struct HandlerObject { public void** Vtbl; public nint GcHandle; public int Which; }   // Which: 0 = showing, 1 = hiding

    private static void** s_handlerVtbl;
    private HandlerObject* _showingHandler, _hidingHandler;

    private void SubscribeVisibilityEvents()
    {
        if (_inputPane is null) return;
        EnsureHandlerVtbl();
        _handlerSelf = GCHandle.Alloc(this, GCHandleType.Normal);

        _showingHandler = (HandlerObject*)NativeMemory.Alloc((nuint)sizeof(HandlerObject));
        _showingHandler->Vtbl = s_handlerVtbl; _showingHandler->GcHandle = GCHandle.ToIntPtr(_handlerSelf); _showingHandler->Which = 0;
        _hidingHandler = (HandlerObject*)NativeMemory.Alloc((nuint)sizeof(HandlerObject));
        _hidingHandler->Vtbl = s_handlerVtbl; _hidingHandler->GcHandle = GCHandle.ToIntPtr(_handlerSelf); _hidingHandler->Which = 1;

        // add_Showing(handler, EventRegistrationToken*) @ slot 7; add_Hiding @ slot 9 (IInputPane: IInspectable 0..5,
        // get_OccludedRect 6, add_Showing 7, remove_Showing 8, add_Hiding 9, remove_Hiding 10).
        long tok;
        if (((delegate* unmanaged<void*, void*, long*, int>)(*(void***)_inputPane)[7])(_inputPane, _showingHandler, &tok) >= 0) _showingToken = tok;
        if (((delegate* unmanaged<void*, void*, long*, int>)(*(void***)_inputPane)[9])(_inputPane, _hidingHandler, &tok) >= 0) _hidingToken = tok;
    }

    private static void EnsureHandlerVtbl()
    {
        if (s_handlerVtbl is not null) return;
        // QI(0) / AddRef(1) / Release(2) / Invoke(3) — the IUnknown-derived TypedEventHandler shape.
        void** v = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&HandlerQueryInterface;
        v[1] = (delegate* unmanaged<void*, uint>)&HandlerAddRef;
        v[2] = (delegate* unmanaged<void*, uint>)&HandlerRelease;
        v[3] = (delegate* unmanaged<void*, void*, void*, int>)&HandlerInvoke;
        s_handlerVtbl = v;
    }

    [UnmanagedCallersOnly]
    private static int HandlerQueryInterface(void* self, Guid* iid, void** ppv)
    {
        // Answer IUnknown and the parameterized TypedEventHandler IID (the only two add_* asks for).
        if (iid is not null && (*iid == IID_VisibilityHandler || *iid == IID_IUnknown))
        {
            *ppv = self;
            return 0;   // S_OK (no refcount — singleton-lifetime)
        }
        *ppv = null;
        return unchecked((int)0x80004002);   // E_NOINTERFACE
    }

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    [UnmanagedCallersOnly] private static uint HandlerAddRef(void* self) => 1;
    [UnmanagedCallersOnly] private static uint HandlerRelease(void* self) => 1;

    [UnmanagedCallersOnly]
    private static int HandlerInvoke(void* self, void* sender, void* args)
    {
        var h = (HandlerObject*)self;
        if (GCHandle.FromIntPtr(h->GcHandle).Target is Win32TextInput owner)
        {
            if (h->Which == 1) owner.RaiseOccluded(default);     // Hiding → empty rect
            else owner.FireCurrentOccludedRect();                // Showing → re-read get_OccludedRect (the WinUI shape)
        }
        return 0;   // S_OK
    }

    /// <summary>Release the WinRT InputPane references + the event subscriptions (called from the window teardown).</summary>
    internal void DisposeSip()
    {
        if (_inputPane is not null)
        {
            if (_showingToken != 0) ((delegate* unmanaged<void*, long, int>)(*(void***)_inputPane)[8])(_inputPane, _showingToken);   // remove_Showing @ 8
            if (_hidingToken != 0) ((delegate* unmanaged<void*, long, int>)(*(void***)_inputPane)[10])(_inputPane, _hidingToken);    // remove_Hiding @ 10
        }
        Release(ref _inputPane2);
        Release(ref _inputPane);
        if (_showingHandler is not null) { NativeMemory.Free(_showingHandler); _showingHandler = null; }
        if (_hidingHandler is not null) { NativeMemory.Free(_hidingHandler); _hidingHandler = null; }
        if (_handlerSelf.IsAllocated) _handlerSelf.Free();
    }
}
