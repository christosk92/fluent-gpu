using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Animation;     // IScrollSource, IScrollHost, DmScrollMath
using FluentGpu.Foundation;    // Point2
using FluentGpu.Scene;         // NodeHandle, SceneStore, ScrollState
using TerraFX.Interop.DirectX; // IDirectManipulation* + DIRECTMANIPULATION_* enums
using TerraFX.Interop.Windows; // HWND, RECT

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The Windows <see cref="IScrollSource"/>: an OS scroll-manipulation driver backed by <b>DirectManipulation in
/// MANUAL-UPDATE mode</b> — the only canon-compatible DM shape for FluentGpu's single D3D12 + DirectComposition
/// swapchain (input-a11y.md §7B ratification; threading-render-seam.md). DM is run as a pure inertia/rubber-band
/// SOLVER: we NEVER bind <c>CLSID_DCompManipulationCompositor</c>, never give it a DComp visual, never let it touch the
/// swapchain. Each frame we poll <c>IDirectManipulationUpdateManager::Update</c> synchronously in the input pump, read
/// the primary content's 2×3 transform in <c>OnContentUpdated</c>, reduce it to a scalar offset+band via the portable
/// <see cref="DmScrollMath"/>, and re-apply it through the host's clamp chokepoint (<c>SetScrollOffset</c> stays the
/// SOLE clamp authority). A genuine TOUCH contact engages a viewport at <c>WM_POINTERDOWN</c> via
/// <see cref="TrySetContact"/>; a precision-touchpad pan arrives as <c>WM_POINTERWHEEL</c> and NEVER reaches here — it
/// rides the dedicated direct-tracking + measured-tail path. Touch-up velocity for a DM-owned contact is handed to DM, not the
/// integrator (AppHost.SeedScrollFling).
///
/// <para><b>COM-ownership posture (the ratified carve-out).</b> DirectManipulation is a UI-THREAD-CONFINED COM domain
/// DISTINCT from the render-thread D3D12/DXGI/DComp ComPtr monopoly (threading-render-seam.md): in manual-update mode it
/// touches NO GPU COM. The manager/viewport/content + the two CCWs are created and pumped on the one UI thread that owns
/// the <c>WndProc</c> message pump (the same carve-out granted OS-marshalled OLE/UIA CCWs). The polled transform is
/// reduced to a scalar on the UI thread BEFORE <c>PUBLISH(13a)</c>, so <c>SceneFrame</c> stays POD and nothing crosses
/// the seam.</para>
///
/// <para><b>Hand-vtable CCWs (not <c>[GeneratedComInterface]</c>).</b> The two callback interfaces are hand-bound exactly
/// like <c>Win32DropTarget</c>: the by-value <c>DIRECTMANIPULATION_STATUS</c> enums and the <c>float[6]</c>-out
/// <c>GetContentTransform</c> crash the COM source generator, so the unmanaged thunk ABI is declared directly with
/// <c>[UnmanagedCallersOnly(CallConvMemberFunction)]</c> (honored identically on x64 and ARM64). The CALLING side uses
/// TerraFX's typed COM structs (the <c>D3D12Device</c> pattern); only the implemented callbacks are hand-rolled.</para>
///
/// <para><b>Validation.</b> The deterministic transform→offset math is <see cref="DmScrollMath"/>, unit-tested headlessly.
/// The live DM physics (inertia/rubber-band/rails) has no deterministic headless analogue and is validated only on a
/// real Windows touchscreen (manual / <c>--screenshot</c>); it never runs in the headless tripwire. Every step degrades
/// gracefully — DM unavailable / a denied create leaves the source null and the engine falls back to the integrator.</para>
/// </summary>
internal sealed unsafe partial class Win32DmScrollSource : IScrollSource, IDisposable
{
    // CLSID + IIDs (directmanipulation.h). Hardcoded as static Guids — the Win32TextInput/Win32DropTarget house style.
    private static readonly Guid CLSID_DirectManipulationManager = new("54E211B6-3650-4F75-8334-FA359598E1C5");
    private static readonly Guid IID_IDirectManipulationManager = new("FBF5D3B4-70C7-4163-9322-5A6F660D6FBC");
    private static readonly Guid IID_IDirectManipulationUpdateManager = new("B0AE62FD-BE34-46E7-9CAA-D361FACBB9CC");
    private static readonly Guid IID_IDirectManipulationViewport = new("28B85A3D-60A0-48BD-9BA1-5CE8D9EA3A6D");
    private static readonly Guid IID_IDirectManipulationContent = new("B89962CB-3D89-442B-BB58-5098FA0F9F16");
    private static readonly Guid IID_IDirectManipulationViewportEventHandler = new("952121DA-D69F-45F9-B0F9-F23944321A6D");
    private static readonly Guid IID_IDirectManipulationFrameInfoProvider = new("FB759DBA-6F4C-4C01-874E-19C8A05907F9");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const int S_OK = 0;

    // DIRECTMANIPULATION_STATUS values we branch on (the manipulation is "active" while RUNNING or INERTIA).
    private const int STATUS_RUNNING = (int)DIRECTMANIPULATION_STATUS.DIRECTMANIPULATION_RUNNING;
    private const int STATUS_INERTIA = (int)DIRECTMANIPULATION_STATUS.DIRECTMANIPULATION_INERTIA;
    private const int STATUS_READY = (int)DIRECTMANIPULATION_STATUS.DIRECTMANIPULATION_READY;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* rclsid, void* pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);

    private readonly nint _hwnd;
    private readonly IScrollHost _host;
    private readonly SceneStore _scene;

    private IDirectManipulationManager* _manager;
    private IDirectManipulationUpdateManager* _updateManager;
    private IDirectManipulationViewport* _viewport;
    private IDirectManipulationContent* _content;
    private EventHandlerCcw* _evt;
    private FrameInfoCcw* _frame;
    private GCHandle _self;       // pins THIS so the CCW thunks route back through GCHandle
    private uint _evtCookie;

    // The viewport DM currently owns (one shared DM viewport, retargeted per gesture), the engine offset captured at
    // contact-start (DM integrates a delta from there), the scroll axis, and the clamp max — all read at SetContact.
    private NodeHandle _engaged;
    private float _baselineOffset;
    private bool _horizontal;
    private float _maxOffset;
    private float _scaleAtEngage = 1f;
    private float _originTx, _originTy;
    private volatile int _status;   // last DIRECTMANIPULATION_STATUS (written in OnViewportStatusChanged, read in HasActive)

    private Win32DmScrollSource(nint hwnd, IScrollHost host)
    {
        _hwnd = hwnd;
        _host = host;
        _scene = host.Scene;
    }

    /// <summary>Create the DM source on <paramref name="hwnd"/>, or null when DirectManipulation is unavailable / any
    /// create step fails (the engine then runs the integrator alone — graceful degradation). UI thread only.</summary>
    internal static Win32DmScrollSource? Create(nint hwnd, IScrollHost host)
    {
        if (hwnd == 0) return null;
        var s = new Win32DmScrollSource(hwnd, host);
        if (!s.Init()) { s.Dispose(); return null; }
        return s;
    }

    private bool Init()
    {
        IDirectManipulationManager* mgr = null;
        Guid clsid = CLSID_DirectManipulationManager, iidMgr = IID_IDirectManipulationManager;
        if (CoCreateInstance(&clsid, null, CLSCTX_INPROC_SERVER, &iidMgr, (void**)&mgr) != S_OK || mgr is null) return false;
        _manager = mgr;

        IDirectManipulationUpdateManager* um = null;
        Guid iidUm = IID_IDirectManipulationUpdateManager;
        if (_manager->GetUpdateManager(&iidUm, (void**)&um) != S_OK || um is null) return false;
        _updateManager = um;

        // The two CCWs (pinned via a single GCHandle to THIS, routed in the thunks).
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        nint ownerToken = GCHandle.ToIntPtr(_self);
        _frame = FrameInfoCcw.Create(ownerToken);
        _evt = EventHandlerCcw.Create(ownerToken);

        // Create ONE viewport bound to the top-level hwnd, fed by our frame-info provider.
        IDirectManipulationViewport* vp = null;
        Guid iidVp = IID_IDirectManipulationViewport;
        if (_manager->CreateViewport((IDirectManipulationFrameInfoProvider*)_frame, (HWND)_hwnd, &iidVp, (void**)&vp) != S_OK || vp is null) return false;
        _viewport = vp;

        // MANUAL update (no DComp compositor): we poll Update() ourselves and own the content transform.
        if (_viewport->SetViewportOptions(DIRECTMANIPULATION_VIEWPORT_OPTIONS.DIRECTMANIPULATION_VIEWPORT_OPTIONS_MANUALUPDATE) != S_OK) return false;

        // Pan + inertia + axis rails (WinUI ScrollPresenter's CapableTouchpadAndPointerWheel translation manipulation).
        var configuration =
            DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_INTERACTION |
            DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_X |
            DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_Y |
            DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_INERTIA |
            DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_RAILS_X |
            DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_RAILS_Y;
        if (_viewport->AddConfiguration(configuration) != S_OK) return false;
        if (_viewport->ActivateConfiguration(configuration) != S_OK) return false;

        uint cookie;
        if (_viewport->AddEventHandler((HWND)_hwnd, (IDirectManipulationViewportEventHandler*)_evt, &cookie) != S_OK) return false;
        _evtCookie = cookie;

        IDirectManipulationContent* content = null;
        Guid iidContent = IID_IDirectManipulationContent;
        if (_viewport->GetPrimaryContent(&iidContent, (void**)&content) != S_OK || content is null) return false;
        _content = content;

        if (_viewport->Enable() != S_OK) return false;
        if (_manager->Activate((HWND)_hwnd) != S_OK) return false;
        return true;
    }

    /// <summary>Bind a genuine touch contact to a scrollable viewport at <c>WM_POINTERDOWN</c> (called from
    /// <c>Win32Window</c>). Resolves the engaged viewport under the press, retargets the shared DM viewport to its
    /// geometry, captures the baseline offset, and hands DM the contact. Returns false (no engagement) when the press is
    /// not over a scrollable viewport — the contact then falls through to the engine's own touch gesture path.</summary>
    internal bool TrySetContact(uint pointerId, Point2 windowPt, float scale)
    {
        if (_viewport is null) return false;
        NodeHandle vp = _host.ScrollableUnder(windowPt);
        if (vp.IsNull || !_scene.IsLive(vp) || !_scene.HasScroll(vp)) return false;

        ref ScrollState sc = ref _scene.ScrollRef(vp);
        bool horizontal = sc.Orientation == 1;
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float viewportMain = horizontal ? sc.ViewportW : sc.ViewportH;
        float contentMain = horizontal ? sc.ContentW * z : sc.ContentH * z;
        float maxOff = MathF.Max(0f, contentMain - viewportMain);
        if (maxOff <= 0f) return false;   // nothing to scroll → leave it to the engine (tap/click)

        // Reset any prior manipulation, then frame the viewport on this scroller's ACTUAL client-pixel bounds.
        // DirectManipulation rectangles are client coordinates, not local DIP rectangles.
        _viewport->Stop();
        _engaged = NodeHandle.Null;
        _scaleAtEngage = scale > 0f ? scale : 1f;
        RectF abs = _scene.AbsoluteRect(vp);
        RECT viewportRect = new()
        {
            left = (int)MathF.Round(abs.X * _scaleAtEngage),
            top = (int)MathF.Round(abs.Y * _scaleAtEngage),
            right = (int)MathF.Round((abs.X + abs.W) * _scaleAtEngage),
            bottom = (int)MathF.Round((abs.Y + abs.H) * _scaleAtEngage),
        };
        if (_viewport->SetViewportRect(&viewportRect) != S_OK) return false;
        if (_content is not null)
        {
            RECT contentRect = new()
            {
                left = 0,
                top = 0,
                right = (int)MathF.Round(MathF.Max(sc.ViewportW, sc.ContentW * z) * _scaleAtEngage),
                bottom = (int)MathF.Round(MathF.Max(sc.ViewportH, sc.ContentH * z) * _scaleAtEngage),
            };
            if (_content->SetContentRect(&contentRect) != S_OK) return false;
        }

        // Rebase the shared DM viewport for each engine viewport. The engine's absolute offset remains authoritative;
        // callbacks consume only transform movement relative to this origin.
        float viewportWpx = MathF.Max(1f, sc.ViewportW * _scaleAtEngage);
        float viewportHpx = MathF.Max(1f, sc.ViewportH * _scaleAtEngage);
        _viewport->ZoomToRect(0f, 0f, viewportWpx, viewportHpx, false);
        _updateManager->Update((IDirectManipulationFrameInfoProvider*)_frame);
        float* origin = stackalloc float[6];
        if (_content is not null && _content->GetContentTransform(origin, 6) == S_OK)
        {
            _originTx = origin[4];
            _originTy = origin[5];
        }
        else _originTx = _originTy = 0f;

        _engaged = vp;
        _horizontal = horizontal;
        _maxOffset = maxOff;
        _baselineOffset = horizontal ? sc.OffsetX : sc.OffsetY;

        if (_viewport->SetContact(pointerId) == S_OK) return true;
        _engaged = NodeHandle.Null;
        return false;
    }

    /// <summary>Per-frame: poll the OS manipulation synchronously (UI thread). <see cref="OnContentUpdated"/> fires
    /// inside and writes the new offset/band through the chokepoint. No GPU COM, no marshaling, nothing past PUBLISH.</summary>
    public void Tick(float dtMs)
    {
        if (_updateManager is null || _frame is null) return;
        _updateManager->Update((IDirectManipulationFrameInfoProvider*)_frame);
    }

    public bool HasActive => _status == STATUS_RUNNING || _status == STATUS_INERTIA;

    public void SetNodeParked(NodeHandle viewport, bool parked)
    {
        // A parked (backgrounded) viewport must not keep an OS manipulation alive: stop DM if it owns the parked node.
        if (parked && _engaged == viewport && _viewport is not null) { _viewport->Stop(); }
    }

    public void ClearForIndex(int index)
    {
        if (!_engaged.IsNull && (int)_engaged.Raw.Index == index)
        {
            _engaged = NodeHandle.Null;
            if (_viewport is not null) _viewport->ReleaseAllContacts();
        }
    }

    // ── callbacks (run on the UI thread, synchronously inside Tick's Update) ───────────────────────────────────────
    private void OnStatusChanged(int current, int previous)
    {
        _status = current;
        if (current == STATUS_READY)
        {
            // The manipulation (and any inertia) ended: settle the band to 0 and release the contact for the next gesture.
            if (!_engaged.IsNull && _scene.IsLive(_engaged)) _host.WriteOverscroll(_engaged, 0f);
            if (_viewport is not null) _viewport->ReleaseAllContacts();
            _engaged = NodeHandle.Null;
        }
        if (current == STATUS_RUNNING || current == STATUS_INERTIA) _host.RequestFrame();
    }

    private void OnContentTransform(void* content)
    {
        if (_engaged.IsNull || !_scene.IsLive(_engaged) || content is null) return;
        float* m = stackalloc float[6];
        if (((IDirectManipulationContent*)content)->GetContentTransform(m, 6) != S_OK) return;
        // Remove the transform already present when this engine viewport engaged, then convert physical px back to DIP.
        m[4] -= _originTx;
        m[5] -= _originTy;
        float displacement = DmScrollMath.DisplacementFromTransform(new ReadOnlySpan<float>(m, 6), _horizontal) / _scaleAtEngage;
        (float offset, float band) = DmScrollMath.Split(_baselineOffset, displacement, _maxOffset);
        _host.WriteScrollOffset(_engaged, offset);     // through SetScrollOffset (clamp + transform + virtual re-realize)
        _host.WriteOverscroll(_engaged, band);          // the visual past-edge rubber band (offset untouched)
    }

    public void Dispose()
    {
        if (_viewport is not null)
        {
            if (_evtCookie != 0) _viewport->RemoveEventHandler(_evtCookie);
            _viewport->Stop();
            _viewport->Disable();
        }
        if (_manager is not null) _manager->Deactivate((HWND)_hwnd);
        ReleaseCom(_content); _content = null;
        ReleaseCom(_viewport); _viewport = null;
        ReleaseCom(_updateManager); _updateManager = null;
        ReleaseCom(_manager); _manager = null;
        if (_evt is not null) { EventHandlerCcw.Destroy(_evt); _evt = null; }
        if (_frame is not null) { FrameInfoCcw.Destroy(_frame); _frame = null; }
        if (_self.IsAllocated) _self.Free();
    }

    private static void ReleaseCom(void* p)
    {
        if (p is null) return;
        ((delegate* unmanaged[MemberFunction]<void*, uint>)(*(void***)p)[2])(p);   // IUnknown::Release @ slot 2
    }

    private static Win32DmScrollSource? Owner(nint token)
        => token == 0 ? null : GCHandle.FromIntPtr(token).Target as Win32DmScrollSource;

    // ── IDirectManipulationViewportEventHandler CCW ───────────────────────────────────────────────────────────────
    // Vtable: 0 QI, 1 AddRef, 2 Release, 3 OnViewportStatusChanged, 4 OnViewportUpdated, 5 OnContentUpdated.
    private struct EventHandlerCcw
    {
        public void** Vtbl;   // MUST be first (COM "this" vptr)
        public int Rc;
        public nint Owner;

        private static readonly void** _vtbl = Build();

        private static void** Build()
        {
            void** v = (void**)NativeMemory.Alloc(6, (nuint)sizeof(void*));
            v[0] = (delegate* unmanaged[MemberFunction]<EventHandlerCcw*, Guid*, void**, int>)&QI;
            v[1] = (delegate* unmanaged[MemberFunction]<EventHandlerCcw*, uint>)&AddRef;
            v[2] = (delegate* unmanaged[MemberFunction]<EventHandlerCcw*, uint>)&Release;
            v[3] = (delegate* unmanaged[MemberFunction]<EventHandlerCcw*, void*, int, int, int>)&OnViewportStatusChanged;
            v[4] = (delegate* unmanaged[MemberFunction]<EventHandlerCcw*, void*, int>)&OnViewportUpdated;
            v[5] = (delegate* unmanaged[MemberFunction]<EventHandlerCcw*, void*, void*, int>)&OnContentUpdated;
            return v;
        }

        public static EventHandlerCcw* Create(nint owner)
        {
            var p = (EventHandlerCcw*)NativeMemory.Alloc((nuint)sizeof(EventHandlerCcw));
            p->Vtbl = _vtbl; p->Rc = 1; p->Owner = owner;
            return p;
        }

        public static void Destroy(EventHandlerCcw* p) => NativeMemory.Free(p);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int QI(EventHandlerCcw* self, Guid* riid, void** ppv)
        {
            if (ppv is null) return unchecked((int)0x80004003);   // E_POINTER
            if (riid is not null && (*riid == IID_IUnknown || *riid == IID_IDirectManipulationViewportEventHandler))
            { Interlocked.Increment(ref self->Rc); *ppv = self; return S_OK; }
            *ppv = null; return unchecked((int)0x80004002);       // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint AddRef(EventHandlerCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint Release(EventHandlerCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);   // memory freed by Destroy

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int OnViewportStatusChanged(EventHandlerCcw* self, void* viewport, int current, int previous)
        {
            try { Owner(self->Owner)?.OnStatusChanged(current, previous); } catch { /* never cross the COM boundary */ }
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int OnViewportUpdated(EventHandlerCcw* self, void* viewport) => S_OK;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int OnContentUpdated(EventHandlerCcw* self, void* viewport, void* content)
        {
            try { Owner(self->Owner)?.OnContentTransform(content); } catch { /* swallow */ }
            return S_OK;
        }
    }

    // ── IDirectManipulationFrameInfoProvider CCW ──────────────────────────────────────────────────────────────────
    // Vtable: 0 QI, 1 AddRef, 2 Release, 3 GetNextFrameInfo. Drives DM's inertia clock for the manual-update poll.
    private struct FrameInfoCcw
    {
        public void** Vtbl;
        public int Rc;
        public nint Owner;

        private static readonly void** _vtbl = Build();

        private static void** Build()
        {
            void** v = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
            v[0] = (delegate* unmanaged[MemberFunction]<FrameInfoCcw*, Guid*, void**, int>)&QI;
            v[1] = (delegate* unmanaged[MemberFunction]<FrameInfoCcw*, uint>)&AddRef;
            v[2] = (delegate* unmanaged[MemberFunction]<FrameInfoCcw*, uint>)&Release;
            v[3] = (delegate* unmanaged[MemberFunction]<FrameInfoCcw*, ulong*, ulong*, ulong*, int>)&GetNextFrameInfo;
            return v;
        }

        public static FrameInfoCcw* Create(nint owner)
        {
            var p = (FrameInfoCcw*)NativeMemory.Alloc((nuint)sizeof(FrameInfoCcw));
            p->Vtbl = _vtbl; p->Rc = 1; p->Owner = owner;
            return p;
        }

        public static void Destroy(FrameInfoCcw* p) => NativeMemory.Free(p);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int QI(FrameInfoCcw* self, Guid* riid, void** ppv)
        {
            if (ppv is null) return unchecked((int)0x80004003);
            if (riid is not null && (*riid == IID_IUnknown || *riid == IID_IDirectManipulationFrameInfoProvider))
            { Interlocked.Increment(ref self->Rc); *ppv = self; return S_OK; }
            *ppv = null; return unchecked((int)0x80004002);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint AddRef(FrameInfoCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint Release(FrameInfoCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetNextFrameInfo(FrameInfoCcw* self, ulong* time, ulong* processTime, ulong* compositionTime)
        {
            // IDirectManipulationFrameInfoProvider uses MILLISECONDS. QPC ticks here make the solver's clock run
            // millions of times too fast and destroy inertia.
            ulong now = unchecked((ulong)Environment.TickCount64);
            if (time is not null) *time = now;
            if (processTime is not null) *processTime = now;
            if (compositionTime is not null) *compositionTime = now + 16;
            return S_OK;
        }
    }
}
