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
    private const int STATUS_SUSPENDED = (int)DIRECTMANIPULATION_STATUS.DIRECTMANIPULATION_SUSPENDED;

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
    private bool _originCaptured;   // origin transform captured from the FIRST OnContentTransform of the gesture (no mid-engage pump)
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
        if (!FrameViewportUnder(windowPt, scale)) return false;
        int hr = _viewport->SetContact(pointerId);
        if (FluentGpu.Foundation.ScrollLog.On)
            FluentGpu.Foundation.ScrollLog.Line($"SETC    pid={pointerId} hr=0x{hr:X8} vp=#{_engaged.Raw.Index} status={StatusName(_status)}");
        if (hr == S_OK) return true;
        _engaged = NodeHandle.Null;
        return false;
    }

    /// <summary>The synthetic contact id for mouse/wheel-framed DM input (DIRECTMANIPULATION_MOUSEFOCUS = 0xFFFFFFFE).</summary>
    private const uint DIRECTMANIPULATION_MOUSEFOCUS = 0xFFFFFFFE;
    private uint _lastWheelGestureMs;

    /// <summary>Feed a (precision-touchpad, promoted-to-mouse) WHEEL message INTO DirectManipulation via
    /// <c>SetContact(DIRECTMANIPULATION_MOUSEFOCUS)</c> + <c>IDirectManipulationManager::ProcessInput</c> so DM produces a
    /// smooth, inertial scroll instead of the discrete accelerated notch fallback — the canonical Chromium / MS-sample
    /// pattern. This is the escape hatch for when DM fails to capture the contact directly (EnableMouseInPointer promotes
    /// the pan to WM_POINTERWHEEL): every angle then still scrolls THROUGH DM. The viewport is framed under the cursor once
    /// per gesture (a &gt;120ms gap starts a fresh one); subsequent wheels feed the running manipulation. Returns true iff
    /// DM consumed the wheel — the caller must then suppress its own notch handling.</summary>
    internal bool TryProcessWheel(uint msg, nuint wParam, nint lParam, Point2 windowPt, float scale, uint nowMs, bool horizontal)
    {
        if (_viewport is null || _manager is null) return false;
        // Re-frame at gesture start (>120ms gap) OR when the wheel's axis differs from the engaged viewport's axis — so a
        // vertical pan that starts over a horizontal shelf rebinds to the vertical page (and vice-versa) instead of being
        // eaten by a cross-axis viewport for the rest of the gesture.
        bool axisChanged = !_engaged.IsNull && _horizontal != horizontal;
        bool newGesture = _engaged.IsNull || axisChanged || (uint)(nowMs - _lastWheelGestureMs) > 120u;
        _lastWheelGestureMs = nowMs;
        if (newGesture && !FrameViewportUnder(windowPt, scale, horizontal ? 1 : 0)) return false;
        if (_engaged.IsNull) return false;
        int scHr = _viewport->SetContact(DIRECTMANIPULATION_MOUSEFOCUS);
        if (scHr != S_OK)
        {
            if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"WPROC   SetContact FAILED hr=0x{scHr:X8}");
            return false;
        }
        MSG m = default;
        m.hwnd = (HWND)_hwnd;
        m.message = msg;
        m.wParam = wParam;
        m.lParam = lParam;
        BOOL handled = BOOL.FALSE;
        int piHr = _manager->ProcessInput(&m, &handled);
        _viewport->ReleaseContact(DIRECTMANIPULATION_MOUSEFOCUS);
        if (FluentGpu.Foundation.ScrollLog.On)
            FluentGpu.Foundation.ScrollLog.Line(
                $"WPROC   msg=0x{msg:X4} delta={(short)((ulong)wParam >> 16)} scHr=0x{scHr:X8} new={newGesture} h={horizontal} status={StatusName(_status)} piHr=0x{piHr:X8} handled={(handled != BOOL.FALSE)}");
        return handled != BOOL.FALSE;
    }

    /// <summary>Resolve the scrollable viewport under <paramref name="windowPt"/> and retarget the shared DM viewport to its
    /// geometry (viewport/content rects, framed at the CURRENT scroll position so the pan has room both ways), capturing the
    /// baseline offset and the transform origin. Returns false when nothing scrollable is under the point. The caller then
    /// binds a contact — a touch pointer id (<see cref="TrySetContact"/>) or MOUSEFOCUS for wheel-framed input.</summary>
    private bool FrameViewportUnder(Point2 windowPt, float scale, int axisHint = -1)
    {
        if (_viewport is null) return false;
        // Axis-aware target for a wheel/pan with a KNOWN axis (axisHint 0 = vertical, 1 = horizontal): pick the nearest
        // SAME-AXIS scroller, climbing past a cross-axis inner scroller — so a vertical pan over a horizontal shelf binds
        // the vertical page, not the shelf (which can't move vertically → the gesture would be silently eaten = the
        // "no scroll over a shelf" dead zone). axisHint < 0 (touch contact, axis unknown at down-time) keeps the
        // innermost-scrollable behavior.
        NodeHandle vp = axisHint >= 0 ? _host.ScrollableUnderForAxis(windowPt, axisHint == 1) : _host.ScrollableUnder(windowPt);
        if (vp.IsNull || !_scene.IsLive(vp) || !_scene.HasScroll(vp)) return false;

        ref ScrollState sc = ref _scene.ScrollRef(vp);
        bool horizontal = sc.Orientation == 1;
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float viewportMain = horizontal ? sc.ViewportW : sc.ViewportH;
        float contentMain = horizontal ? sc.ContentW * z : sc.ContentH * z;
        float maxOff = MathF.Max(0f, contentMain - viewportMain);
        if (maxOff <= 0f) return false;   // nothing to scroll → leave it to the engine (tap/click)

        // Un-stick a wedged viewport. On this device DM can get pinned in SUSPENDED (a known old-ScrollViewer DM wart —
        // it's why WinUI 3 moved ScrollView onto InteractionTracker). WinUI's own DM service recovers a viewport by cycling
        // Disable()→Enable() (microsoft-ui-xaml DirectManipulationService.cpp, EnableViewport). Do the same so a fresh
        // gesture starts from a clean ENABLED state instead of engaging into SUSPENDED and instantly collapsing (= dead scroll).
        if (_status == STATUS_SUSPENDED)
        {
            if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line("RECOVER SUSPENDED -> Disable/Enable");
            _viewport->Disable();
            _viewport->Enable();
        }

        // Reset any prior manipulation, then frame the viewport on this scroller's ACTUAL client-pixel bounds.
        // DirectManipulation rectangles are client coordinates, not local DIP rectangles.
        _viewport->Stop();
        // Clear any stale contact before binding a fresh one. On this device DM_POINTERHITTEST engages a TOUCH contact
        // that the OS then never feeds motion to (it promotes the pan to a wheel instead); that motionless contact stays
        // bound and makes a subsequent SetContact(MOUSEFOCUS) a SECOND contact, so DM refuses to pan ("no scroll"). This is
        // only ever reached at gesture start / re-frame (TrySetContact, or TryProcessWheel when DM is NOT actively driving),
        // so nothing live is released — the actively-driving HITTEST case is gated off before it ever gets here.
        _viewport->ReleaseAllContacts();
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
        // callbacks consume only transform movement relative to this origin. Frame DM at the engine's CURRENT scroll
        // position inside the content rect, so the pan has room BOTH ways (up → 0, down → max).
        float baseline = horizontal ? sc.OffsetX : sc.OffsetY;
        float viewportWpx = MathF.Max(1f, sc.ViewportW * _scaleAtEngage);
        float viewportHpx = MathF.Max(1f, sc.ViewportH * _scaleAtEngage);
        float baseLeftPx = horizontal ? baseline * _scaleAtEngage : 0f;
        float baseTopPx = horizontal ? 0f : baseline * _scaleAtEngage;
        _viewport->ZoomToRect(baseLeftPx, baseTopPx, baseLeftPx + viewportWpx, baseTopPx + viewportHpx, false);
        // Do NOT pump _updateManager->Update() + GetContentTransform() here. That synchronous mid-engage pump is what
        // cascaded the viewport through SUSPENDED on this device (the dead-spot root cause: the trace showed the whole
        // SUSPENDED↔INERTIA churn firing at SetContact time, i.e. inside this call). Capture the origin transform LAZILY
        // on the FIRST OnContentTransform of the gesture instead — displacement is 0 on that first frame (we're at the
        // ZoomToRect'd baseline), so the math is identical without the destabilizing pump.
        _originTx = _originTy = 0f;
        _originCaptured = false;

        _engaged = vp;
        _horizontal = horizontal;
        _maxOffset = maxOff;
        _baselineOffset = baseline;
        _renderOffset = _pendingOffset = baseline;   // start the smoothed offset at the engine's current position (no jump)
        _pendingBand = 0f;
        _hasPending = true;
        if (FluentGpu.Foundation.ScrollLog.On)
            FluentGpu.Foundation.ScrollLog.Line($"FRAME   vp=#{vp.Raw.Index} axis={(horizontal ? "H" : "V")} hint={axisHint} base={baseline:0.0} max={maxOff:0}");
        return true;
    }

    /// <summary>Per-frame: poll the OS manipulation synchronously (UI thread). <see cref="OnContentUpdated"/> fires
    /// inside and writes the new offset/band through the chokepoint. No GPU COM, no marshaling, nothing past PUBLISH.</summary>
    public void Tick(float dtMs)
    {
        if (_updateManager is null || _frame is null) return;
        _updateManager->Update((IDirectManipulationFrameInfoProvider*)_frame);   // fires OnContentTransform → updates _pending*
        // RENDER-CLOCK SMOOTHING: ease the rendered offset toward DM's target each frame, low-passing the device's uneven
        // per-frame contact motion that DM relays 1:1 during the live drag. (DM only smooths POST-lift inertia itself; the
        // active drag is raw — this is the missing layer that made the wheel path buttery.) Band is written as-is.
        if (!_hasPending || _engaged.IsNull || !_scene.IsLive(_engaged)) return;
        float kOff = 1f - MathF.Exp(-dtMs / s_dmSmoothTauMs);
        _renderOffset += (_pendingOffset - _renderOffset) * kOff;
        if (MathF.Abs(_pendingOffset - _renderOffset) < 0.5f) _renderOffset = _pendingOffset;
        _host.WriteScrollOffset(_engaged, _renderOffset);
        _host.WriteOverscroll(_engaged, _pendingBand);
        _scene.ScrollRef(_engaged).OverscrollDmOwned = _pendingBand != 0f;
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
    private static string StatusName(int s) => s switch
    {
        0 => "BUILDING", 1 => "ENABLED", 2 => "RUNNING", 3 => "INERTIA", 4 => "READY", 5 => "SUSPENDED", _ => $"?{s}"
    };

    private void OnStatusChanged(int current, int previous)
    {
        _status = current;
        if (FluentGpu.Foundation.ScrollLog.On)
            FluentGpu.Foundation.ScrollLog.Line($"STATUS  {StatusName(previous)}→{StatusName(current)} eng={(_engaged.IsNull ? "-" : "#" + _engaged.Raw.Index)}");
        if (current == STATUS_READY)
        {
            // The manipulation (and any inertia) ended. Do NOT snap the offset to DM's final target — the smoothed
            // _renderOffset is intentionally a hair behind, and snapping it jumps by the lag, which SCALES with scroll
            // speed → the end-of-scroll "flicker"/sudden jump. Leave the rendered offset where the ease left it (the few-px
            // residual is imperceptible); only settle the overscroll band to 0.
            if (!_engaged.IsNull && _scene.IsLive(_engaged))
            {
                _host.WriteOverscroll(_engaged, 0f);
                _scene.ScrollRef(_engaged).OverscrollDmOwned = false;
            }
            _hasPending = false;
            if (_viewport is not null) _viewport->ReleaseAllContacts();
            _engaged = NodeHandle.Null;
        }
        if (current == STATUS_RUNNING || current == STATUS_INERTIA) _host.RequestFrame();
    }

    /// <summary>True while a contact is bound to the DM viewport.</summary>
    internal bool IsEngaged => !_engaged.IsNull;

    private long _lastTransformMs;
    // Render-clock smoothing of DM's offset: DM tracks the contact 1:1 during the live drag, so the device's uneven
    // per-frame motion (e.g. +84px one frame, +23px the next) passes straight through. Tick low-passes it toward DM's
    // target each frame (the same fix the wheel path got). Band = DM's native overscroll, written as-is.
    private float _renderOffset, _pendingOffset, _pendingBand;
    private bool _hasPending;
    private static readonly float s_dmSmoothTauMs =
        float.TryParse(System.Environment.GetEnvironmentVariable("FG_DM_SMOOTH_TAU"), out float t) && t > 0f ? t : 14f;
    /// <summary>True only while DM is ACTIVELY producing motion (a transform within the last few frames) — not merely
    /// engaged. On this device DM often engages (DM_POINTERHITTEST) but fails to capture the gesture (the OS delivers it as
    /// promoted mouse-wheel instead), so "engaged" must NOT gate the wheel fallback: swallowing the wheel while DM engaged
    /// but idle killed scrolling entirely. The wheel is suppressed only when DM is genuinely driving, so it always works.</summary>
    internal bool IsActivelyDriving => !_engaged.IsNull && (System.Environment.TickCount64 - _lastTransformMs) < 64;

    private void OnContentTransform(void* content)
    {
        if (_engaged.IsNull || !_scene.IsLive(_engaged) || content is null) return;
        _lastTransformMs = System.Environment.TickCount64;   // DM is actively driving → wheel fallback is suppressed (IsActivelyDriving)
        float* m = stackalloc float[6];
        if (((IDirectManipulationContent*)content)->GetContentTransform(m, 6) != S_OK) return;
        // Capture the engage-time origin from the FIRST transform of the gesture (replaces the removed mid-engage pump):
        // this frame is at the ZoomToRect'd baseline, so the relative displacement is 0 here and correct thereafter.
        if (!_originCaptured) { _originTx = m[4]; _originTy = m[5]; _originCaptured = true; }
        // Remove the transform already present when this engine viewport engaged, then convert physical px back to DIP.
        m[4] -= _originTx;
        m[5] -= _originTy;
        float displacement = DmScrollMath.DisplacementFromTransform(new ReadOnlySpan<float>(m, 6), _horizontal) / _scaleAtEngage;
        (float offset, float band) = DmScrollMath.Split(_baselineOffset, displacement, _maxOffset);
        if (FluentGpu.Foundation.ScrollLog.On) FluentGpu.Foundation.ScrollLog.Line($"DM      off={offset:0.0} band={band:0.0} disp={displacement:0.0} max={_maxOffset:0}");
        // Don't write the offset here — store the target; Tick eases the rendered offset toward it on the render clock.
        _pendingOffset = offset;
        _pendingBand = band;
        _hasPending = true;
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
