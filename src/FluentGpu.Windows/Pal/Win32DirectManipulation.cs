using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;   // Point2, KeyModifiers, ScrollLog, ScrollTrace
using FluentGpu.Pal;          // InputEvent, InputKind, PointerKind, ScrollDeviceClass
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// Phase D of scroll-feel-rework-v2 (§7): the DirectManipulation touchpad producer — the recommended fidelity ceiling.
///
/// <para>It is a <b>pure producer swap</b>: it emits the exact same <see cref="InputKind.ScrollBegin"/>/<c>Update</c>/<c>End</c>
/// + <see cref="InputKind.MomentumBegin"/>/<c>Update</c>/<c>End</c> phase-contract events the landed integrator already
/// consumes (<c>InputDispatcher.OnScrollPhase</c>), tagged <see cref="ScrollDeviceClass.Touchpad"/>. Nothing downstream
/// changes — DM just replaces the §3.3 wheel-classifier heuristic with real <c>PT_TOUCHPAD</c> contacts, true finger-lift,
/// and OS-curved momentum, eliminating R4 (touchpad-vs-wheel guessing).</para>
///
/// <para><b>COM discipline (com-interop.md).</b> Event-source only, all UI-thread. The DManip objects
/// (<c>Manager</c>/<c>UpdateManager</c>/<c>Viewport</c>/<c>Content</c>) are consumed through TerraFX's hand-vtable RCW
/// structs (the same shape <c>WicImageCodec</c> uses — no <c>ComWrappers</c> on the hot path); the one managed object we
/// hand back to COM, the <c>IDirectManipulationViewportEventHandler</c> sink, is a hand-rolled CCW modeled verbatim on
/// <c>Win32DropTargetCcw</c>/<c>UiaProviderCcw</c> (function-pointer vtable + interlocked refcount). Every COM object is
/// released on every path (<see cref="Teardown"/>), and the whole file lives inside <c>FluentGpu.Windows</c> so the
/// engine / Controls / VerticalSlice closure stays TerraFX-free.</para>
///
/// <para><b>Coexistence with the fallback (§7, "never two owners for one packet").</b> When DM is enabled it owns every
/// touchpad contact — its <c>ProcessInput</c> consumes those packets before the WndProc sees them, so the §3.3
/// wheel-fallback path never double-processes them, and any <c>WM_POINTERWHEEL</c> that still reaches the WndProc is
/// genuinely a mouse (classifier rule 4 becomes exact). If DManip <b>wedges</b> (engaged via <c>SetContact</c> but never
/// reaches <c>RUNNING</c> within <see cref="DmEngageTimeoutMs"/>) <see cref="DmWedgeCountToDisable"/> times, DM
/// session-disables itself edge-triggered (<see cref="Teardown"/>) and the always-compiled §3.3 heuristic takes over.
/// Popups never get a producer, so they keep the fallback unconditionally.</para>
/// </summary>
internal sealed unsafe class Win32DirectManipulation : IDisposable
{
    // ── DIRECTMANIPULATION_STATUS (directmanipulation.h) — the viewport lifecycle we map to phase-contract events ──
    private const int DM_BUILDING = 0, DM_ENABLED = 1, DM_DISABLED = 2, DM_RUNNING = 3, DM_INERTIA = 4, DM_READY = 5,
                      DM_SUSPENDED = 6;

    // ── DIRECTMANIPULATION_CONFIGURATION flags (directmanipulation.h), verified against the dm-probe cell-B PASS ──
    //   INTERACTION|TRANSLATION_X|TRANSLATION_Y|SCALING|TRANSLATION_INERTIA|SCALING_INERTIA. No RAILS_* — the integrator's
    //   axis latch owns railing (§7). SCALING(+INERTIA) is configured only so a pinch is a legal gesture we can DETECT and
    //   suppress (|scale-1|>ε ⇒ emit no pan); we never translate a scale into scroll.
    private const int DM_CFG =
        (int)(DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_INTERACTION
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_X
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_Y
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_SCALING
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_INERTIA
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_SCALING_INERTIA);

    // ── §7/§4.6 tuning: DManip-only constants (they belong to the Windows producer, not the portable ScrollTuning) ──
    /// <summary>Frozen DIP per content-transform unit (§7, "no knee"). The 1000×1000 viewport rect is in physical px, so
    /// the content-transform translation is in physical device px; DIP = px / (dpi/96). DManip returns true OS device
    /// pixels — no <c>HiResUnitDip</c> calibration.</summary>
    private const float DmDipPerTransformUnit = 1.0f;
    /// <summary>|scale−1| beyond this ⇒ the gesture is a pinch, not a pan ⇒ emit no pan (§7).</summary>
    private const float DmPinchScaleEpsilon = 0.01f;
    /// <summary>Transform-unit delta below which a content update is treated as a no-op (skips zero-delta spam; well below
    /// the smallest real inertia-tail delta observed in the probe, ~0.1u).</summary>
    private const float DmMinTransformDelta = 0.01f;
    /// <summary>Wedge watchdog (§7): a <c>SetContact</c> that does not reach <c>RUNNING</c> within this many ms is a wedge.</summary>
    private const long DmEngageTimeoutMs = 120;
    /// <summary>After this many wedges DM session-disables and the §3.3 fallback owns the touchpad forever (§7).</summary>
    private const int DmWedgeCountToDisable = 3;

    // The fake event-source viewport = the real window client rect, with NO SetContentRect (scroll-feel-v2.1 §A.5). Both
    // browsers (direct_manipulation_helper_win.cc, DirectManipulationOwner.cpp) size the viewport to the window and never
    // call SetContentRect — DM translation against the DEFAULT content is effectively unbounded, so runway exhaustion (the
    // old 200k-content trigger) never occurs. That deletes BOTH self-inflicted defects at the source: F3 (the OS chaining
    // an exhausted pan out as synthesized ±120 WM_POINTERWHEEL bursts) and F4 (a giant ~1e5 recenter origin amplifying
    // sub-epsilon two-finger scale drift ds into origin·ds phantom pan). We never DISPLAY this transform — we only read its
    // translation deltas; the viewport recenters to identity only at READY (between gestures). A 1000×1000 fallback is used
    // only when the window rect is unavailable (a 0-size pre-first-layout window).
    private const int ViewportFallbackSize = 1000;

    // ── COM (all created + released on the UI thread) ──
    private IDirectManipulationManager* _mgr;
    private IDirectManipulationUpdateManager* _upd;
    private IDirectManipulationViewport* _vp;
    private IDirectManipulationContent* _content;
    private DmViewportEventHandlerCcw* _sink;
    private DmFrameInfoProviderCcw* _frameInfo;   // IDirectManipulationFrameInfoProvider CCW: reports composition-latency to DM
    private uint _cookie;
    private GCHandle _self;                 // pins THIS so the CCW thunks can reach it via self->Owner
    private bool _coInited;                 // we owe a CoUninitialize (CoInitializeEx returned S_OK or S_FALSE)

    private readonly Win32Window _window;   // the owner — we enqueue phase events onto its input queue
    private readonly HWND _hwnd;
    private float _vpW = ViewportFallbackSize, _vpH = ViewportFallbackSize;   // viewport rect (window client size) — the READY recenter target

    private bool _enabled;                  // false after a wedge-disable / teardown — the fallback then owns
    private bool _torn;                     // Teardown idempotency

    // ── gesture state (single latched gesture; all UI-thread, no locking) ──
    private int _status = DM_READY;
    private float _lastTx, _lastTy;
    private bool _haveBaseline;             // false ⇒ the next content update establishes the baseline (emits nothing)
    private uint _contactId;                // stable per gesture ⇒ the ring's ScrollUpdate coalescing sums per frame
    private Point2 _contactPos;             // the pan anchor (touchpad cursor at engage) — hit-tests the scroll target
    private byte _seq;                      // per-gesture packet ordinal (velocity side-buffer cross-contamination tag)

    // ── wedge watchdog ──
    private bool _awaitingEngage;           // a SetContact is pending RUNNING
    private long _engageTick;               // Environment.TickCount64 at SetContact
    private int _wedgeCount;

    // ── pump-time stamping (scroll-jitter §B.1) ──
    private long _pumpQpc;                   // Stopwatch.GetTimestamp() captured once at the top of Update() — the frame instant
    private long _pumpMs;                    // Environment.TickCount64 captured at the same instant (coarse ms clock, kept in step)
    private float _pumpEmaMs = 8f;           // EMA of the pump-to-pump interval ≈ this machine's vblank period during a gesture
                                             // (present-paced loop) — feeds CompositionDeltaMs so DM's lead matches the display
    // A stamp older than this (relative to now) was NOT produced by the current Update pump — an Emit reached from a
    // ProcessInput-time status change (contact-engage → RUNNING) — so Emit re-reads now instead of back-dating it.
    // ~20ms ≈ 1.2 vsync frames: comfortably larger than any same-pump Update body, smaller than a cross-frame gap.
    private static readonly long StaleStampTicks = Stopwatch.Frequency / 50;

    /// <summary>Estimated milliseconds from this pump's <c>UpdateManager.Update</c> until the resulting pixels are on
    /// screen — handed to DM through the <see cref="DmFrameInfoProviderCcw"/> so it evaluates its curve at the
    /// composition instant, not the pump instant (Microsoft's documented frame-info purpose). The platform may set this
    /// per frame from real present cadence; absent that it defaults to 8ms (~one 120Hz vblank). XAML hardcodes 16 (one
    /// 60Hz vblank) — on a 120Hz panel that over-leads the finger by a full extra vblank, which a feel session reported
    /// as "too fast / easy to lose control". See §B.2 — a follow-up knob once host present timing is threaded here.</summary>
    public float NextPresentDeltaMs { get; set; } = 8f;

    private Win32DirectManipulation(Win32Window window, HWND hwnd)
    {
        _window = window;
        _hwnd = hwnd;
    }

    /// <summary>Create + wire the producer for <paramref name="hwnd"/>, or return null if DirectManipulation is
    /// unavailable (MTA thread, CoCreate failed, any setup HRESULT failed) — the caller then relies on the §3.3 fallback.
    /// Mirrors <c>Win32DropTarget.Register</c>'s best-effort posture.</summary>
    internal static Win32DirectManipulation? TryCreate(Win32Window window, HWND hwnd)
    {
        if (hwnd == HWND.NULL) return null;
        var dm = new Win32DirectManipulation(window, hwnd);
        if (!dm.SetUp())
        {
            dm.Dispose();
            return null;
        }
        return dm;
    }

    internal bool Enabled => _enabled;

    /// <summary>True while a DManip manipulation is live (RUNNING contact or INERTIA momentum). While live, the wheel
    /// fallback must not accept "residual = genuinely mouse" ±120 packets at burst cadence — the OS can synthesize
    /// legacy wheel messages for the SAME gesture (observed at runway exhaustion), and a second producer taking over a
    /// live gesture snaps the band and hijacks the coast.</summary>
    internal bool GestureLive => _enabled && (_status == DM_RUNNING || _status == DM_INERTIA);

    private bool SetUp()
    {
        // DManip needs an initialized apartment. The window already OleInitialize'd (Win32DropTarget) on the STA UI
        // thread, so this typically returns S_FALSE (already STA); RPC_E_CHANGED_MODE ⇒ MTA ⇒ DManip can't run here.
        HRESULT hrCo = CoInitializeEx(null, (uint)COINIT.COINIT_APARTMENTTHREADED);
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        if (hrCo == RPC_E_CHANGED_MODE) return false;
        _coInited = hrCo.SUCCEEDED;   // S_OK / S_FALSE both owe a CoUninitialize

        Guid clsidMgr = CLSID_DirectManipulationManager;
        Guid iidMgr = IID_IDirectManipulationManager;
        IDirectManipulationManager* mgr = null;
        if (CoCreateInstance(&clsidMgr, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iidMgr, (void**)&mgr).FAILED || mgr == null)
            return false;
        _mgr = mgr;

        Guid iidUpd = IID_IDirectManipulationUpdateManager;
        IDirectManipulationUpdateManager* upd = null;
        if (_mgr->GetUpdateManager(&iidUpd, (void**)&upd).FAILED || upd == null) return false;
        _upd = upd;

        // The frame-info provider must exist before CreateViewport (DM holds it for the viewport's life) and is also
        // passed to every UpdateManager.Update. It carries a composition-latency hint so DM predicts content position
        // instead of sampling its curve at the raw pump instant (§B.2). CreateViewport treats it as _In_opt_.
        _frameInfo = DmFrameInfoProviderCcw.Create();
        _frameInfo->CompositionDeltaMs = (ulong)MathF.Round(MathF.Max(0f, NextPresentDeltaMs));

        Guid iidVp = IID_IDirectManipulationViewport;
        IDirectManipulationViewport* vp = null;
        if (_mgr->CreateViewport((IDirectManipulationFrameInfoProvider*)_frameInfo, _hwnd, &iidVp, (void**)&vp).FAILED || vp == null) return false;
        _vp = vp;

        // §A.5: viewport rect = the window client rect (browsers size the DM viewport to the window). Fallback to
        // 1000×1000 only if the rect is unavailable (a 0-size pre-first-layout window).
        RECT client;
        int vw = ViewportFallbackSize, vh = ViewportFallbackSize;
        if (GetClientRect(_hwnd, &client) && client.right > client.left && client.bottom > client.top)
        {
            vw = client.right - client.left;
            vh = client.bottom - client.top;
        }
        _vpW = vw; _vpH = vh;
        RECT rect = new() { left = 0, top = 0, right = vw, bottom = vh };
        if (_vp->SetViewportRect(&rect).FAILED) return false;
        if (_vp->AddConfiguration((DIRECTMANIPULATION_CONFIGURATION)DM_CFG).FAILED) return false;
        if (_vp->ActivateConfiguration((DIRECTMANIPULATION_CONFIGURATION)DM_CFG).FAILED) return false;
        if (_vp->SetViewportOptions(DIRECTMANIPULATION_VIEWPORT_OPTIONS.DIRECTMANIPULATION_VIEWPORT_OPTIONS_MANUALUPDATE).FAILED) return false;

        _self = GCHandle.Alloc(this);
        _sink = DmViewportEventHandlerCcw.Create(GCHandle.ToIntPtr(_self));
        uint cookie;
        if (_vp->AddEventHandler(_hwnd, (IDirectManipulationViewportEventHandler*)_sink, &cookie).FAILED) return false;
        _cookie = cookie;   // AddEventHandler AddRef'd the sink (Rc 1→2); RemoveEventHandler drops it back to our 1

        // Hold the primary content for GetContentTransform (deltas) + the READY identity-skip. §A.5: NO SetContentRect —
        // the default content gives unbounded translation, so there is no runway to exhaust and no giant origin to amplify
        // scale drift.
        Guid iidContent = IID_IDirectManipulationContent;
        IDirectManipulationContent* content = null;
        if (_vp->GetPrimaryContent(&iidContent, (void**)&content).SUCCEEDED && content != null)
            _content = content;

        if (_vp->Enable().FAILED) return false;
        if (_mgr->Activate(_hwnd).FAILED) return false;
        // No initial ZoomToRect: with no content rect the transform starts at identity — no centering needed.

        _enabled = true;
        if (ScrollLog.On) ScrollLog.Line("DM enabled (DirectManipulation touchpad producer)");
        return true;
    }

    // ── message pump hooks (called from Win32Window.PumpInto, UI thread) ──

    /// <summary>Feed one pumped message to DManip BEFORE dispatch (§7 "per-message ProcessInput"). Returns true when
    /// DManip consumed it (an active touchpad-contact packet) so the caller skips Translate/Dispatch — this is what keeps
    /// the fallback from ever seeing a packet DM owns.</summary>
    internal bool ProcessInput(MSG* msg)
    {
        if (!_enabled || _mgr == null) return false;
        BOOL handled = default;
        _mgr->ProcessInput(msg, &handled);
        return (bool)handled;
    }

    /// <summary>One <c>UpdateManager.Update</c> per pump drain (§7) — advances the OS inertia curve and pumps content
    /// updates; also runs the wedge watchdog. Idempotent / cheap when idle.</summary>
    internal void Update()
    {
        if (!_enabled) return;
        // §B.1: stamp this pump once. Every content update DM advances inside the _upd->Update() below fires on this
        // thread synchronously, so all of them (which Emit coalesces into one per-frame ScrollUpdate downstream) share
        // this single instant — the resampler's QPC x-axis then carries no per-packet receive jitter.
        long prevPump = _pumpQpc;
        _pumpQpc = Stopwatch.GetTimestamp();
        _pumpMs = Environment.TickCount64;
        // Self-measured pump cadence (EMA): during a gesture the loop is present-paced, so the pump interval IS this
        // machine's vblank period (8.3ms @120Hz, 16.7 @60Hz) — no refresh-rate query exists in the PAL, and hardcoding
        // either value over/under-leads the finger on the other class of machine. Idle gaps (>25ms) are excluded.
        if (prevPump != 0)
        {
            float iv = (_pumpQpc - prevPump) * 1000f / Stopwatch.Frequency;
            if (iv > 2f && iv < 25f) _pumpEmaMs += (iv - _pumpEmaMs) * 0.1f;
        }
        // Wedge watchdog: a SetContact that never reached RUNNING within the engage window is a wedge (DManip did not
        // engage). Reaching RUNNING clears _awaitingEngage, so a legitimate finger-down-then-pause is NOT a wedge.
        if (_awaitingEngage && Environment.TickCount64 - _engageTick > DmEngageTimeoutMs)
        {
            _awaitingEngage = false;
            OnWedge();
            if (!_enabled) return;   // OnWedge may have session-disabled + torn down
        }
        if (_upd != null)
        {
            // Refresh the composition-latency hint for this pump before DM samples its curve (§B.2) — but ONLY during
            // INERTIA. DM's prediction is exact for its own smooth inertia curve (pure latency win), while during
            // CONTACT it linearly extrapolates the ragged digitizer motion and AMPLIFIES the noise: traced roughness
            // rose 10.4%→13-20% with a contact lead, and a 16ms lead felt "too fast / easy to lose control". Contact
            // stays sample-at-pump (the Chromium/Firefox behavior); the engine's own resampler smooths it downstream.
            float delta = _status != DM_INERTIA ? 0f
                        : float.IsNaN(NextPresentDeltaMs) ? Math.Clamp(_pumpEmaMs, 4f, 20f)
                        : MathF.Max(0f, NextPresentDeltaMs);
            if (_frameInfo != null) _frameInfo->CompositionDeltaMs = (ulong)MathF.Round(delta);
            _upd->Update((IDirectManipulationFrameInfoProvider*)_frameInfo);
        }
    }

    /// <summary>DM_POINTERHITTEST (0x0250) → claim this contact for DManip. Gated to <c>PT_TOUCHPAD</c> by the caller
    /// (§7). Returns true iff SetContact succeeded (the caller then consumes the message, matching the dm-probe).</summary>
    internal bool SetContact(uint pointerId, Point2 contactDip)
    {
        if (!_enabled || _vp == null) return false;
        HRESULT hr = _vp->SetContact(pointerId);
        if (hr.FAILED)
        {
            if (ScrollLog.On) ScrollLog.Line($"DM SetContact FAILED hr=0x{(int)hr:X8} pid={pointerId}");
            return false;
        }
        // Latch the anchor once per gesture so mid-gesture second-finger hit-tests don't move the target / flip the id
        // (a stable id keeps the ring's ScrollUpdate coalescing summing per frame).
        if (!Owns)
        {
            _contactId = pointerId;
            _contactPos = contactDip;
        }
        _awaitingEngage = true;
        _engageTick = Environment.TickCount64;
        return true;
    }

    private bool Owns => _status == DM_RUNNING || _status == DM_INERTIA;

    // ── the CCW sink callbacks (forwarded from the hand-vtable thunks; UI thread) ──

    internal void HandleStatusChanged(int current, int previous)
    {
        _status = current;
        if (ScrollLog.On) ScrollLog.Line($"DM STATUS {StatusName(previous)}->{StatusName(current)}");

        if (current == DM_RUNNING)
        {
            _awaitingEngage = false;   // engaged — not a wedge
            if (previous == DM_INERTIA) Emit(InputKind.MomentumEnd, 0f, 0f);   // fingers re-landed mid-coast (stop-on-contact)
            Emit(InputKind.ScrollBegin, 0f, 0f);
            _haveBaseline = false;     // the next content update captures the baseline, emits nothing
            _seq = 0;
        }
        else if (current == DM_INERTIA && previous == DM_RUNNING)
        {
            Emit(InputKind.MomentumBegin, 0f, 0f);
            // keep the baseline — inertia continues from the same transform, deltas flow seamlessly as MomentumUpdate
        }
        else if (current == DM_READY)
        {
            if (previous == DM_INERTIA) Emit(InputKind.MomentumEnd, 0f, 0f);
            else if (previous == DM_RUNNING) Emit(InputKind.ScrollEnd, 0f, 0f);   // hold-release, no OS momentum
            _awaitingEngage = false;
            ResetViewport();           // §7 "viewport reset" — recenter so the next gesture has fresh runway
            _haveBaseline = false;     // the recenter's content update re-baselines silently (Owns is false now)
        }
    }

    internal void HandleContentUpdated(IDirectManipulationContent* content)
    {
        if (content == null) return;
        float* m = stackalloc float[6];
        if (content->GetContentTransform(m, 6).FAILED) return;
        float scale = m[0], tx = m[4], ty = m[5];

        // CONTENT-SPACE position: the content coordinate under the viewport origin, p = −t/s (scroll-feel-v2.1 §A.5, kept).
        // At s=1 the diff equals the browsers' negated raw-translation diff (the F1 sign convention) — a strict superset:
        // content space also stays robust to residual two-finger scale drift (ds contributes only its bounded zoom-center
        // shift, not the origin·ds phantom pan) EVEN NOW THAT the giant recenter origin is gone (§A.5 deleted the 200k
        // runway that used to amplify F4). Deliberately differs from the browsers' raw diff because it subsumes it.
        float invS = scale > 0.001f ? 1f / scale : 1f;
        float px = -tx * invS, py = -ty * invS;

        // Not owning (idle / the ResetViewport recenter) OR first frame of a gesture: capture the baseline, emit nothing.
        // The first RUNNING content update carries the full absolute transform (probe: dx≈-9500) — this is what swallows it.
        if (!Owns || !_haveBaseline)
        {
            _lastTx = px; _lastTy = py; _haveBaseline = true;
            return;
        }

        float dx = px - _lastTx, dy = py - _lastTy;
        _lastTx = px; _lastTy = py;   // advance the baseline even on suppressed frames so no jump accumulates

        if (MathF.Abs(scale - 1f) > DmPinchScaleEpsilon) return;              // pinch: suppress the pan (§7)
        if (MathF.Abs(dx) < DmMinTransformDelta && MathF.Abs(dy) < DmMinTransformDelta) return;

        float wscale = _window.ScaleInternal;
        if (wscale <= 0f) wscale = 1f;
        // Content-space px → DIP. p = −t/s already carries the engine's delta convention: for a pure pan (s=1) the
        // diff equals the NEGATED raw-translation diff, which the on-hardware verification (2026-07-02) fixed as the
        // convention matching the WM_POINTERWHEEL fallback ("−delta = scroll toward content end") — fingers up ⇒
        // advance toward content end.
        float dipX = dx * DmDipPerTransformUnit / wscale;
        float dipY = dy * DmDipPerTransformUnit / wscale;

        Emit(_status == DM_INERTIA ? InputKind.MomentumUpdate : InputKind.ScrollUpdate, dipX, dipY);
    }

    // ── event emission ──

    private void Emit(InputKind kind, float dipX, float dipY)
    {
        // §B.1: prefer this pump's frame timestamp so every content update from one UpdateManager.Update shares one
        // instant. Emit is ALSO reachable outside the Update pump: a contact-engage status change (RUNNING) can fire
        // synchronously inside ProcessInput, where _pumpQpc still holds the PREVIOUS pump's value (~1 frame stale). We
        // detect that (stamp older than ~1 frame, or never set) and read now instead, so phase markers aren't
        // back-dated; content updates — the resampler-critical path — always run inside Update and see a fresh
        // _pumpQpc. ms is taken from the same instant (Stopwatch and TickCount64 both advance in real time).
        long now = Stopwatch.GetTimestamp();
        long qpc = _pumpQpc;
        long ms64 = _pumpMs;
        if (qpc == 0 || now - qpc > StaleStampTicks) { qpc = now; ms64 = Environment.TickCount64; }
        uint ms = unchecked((uint)ms64);
        bool isUpdate = kind is InputKind.ScrollUpdate or InputKind.MomentumUpdate;
        _window.EnqueueExternal(new InputEvent(
            kind, _contactPos, 0, 0, dipY, KeyModifiers.None,
            Pointer: PointerKind.Touchpad, TimestampMs: ms, PointerId: _contactId, Pressure: 1f,
            ScrollDeltaX: dipX, QpcTicks: qpc, ScrollPhaseSeq: _seq,
            DeviceClassRaw: (byte)ScrollDeviceClass.Touchpad));
        if (isUpdate) unchecked { _seq++; }
        if (ScrollLog.On && !isUpdate) ScrollLog.Line($"DM {kind} pos=({_contactPos.X:0},{_contactPos.Y:0})");
    }

    private void ResetViewport()
    {
        if (_vp == null) return;
        // §A.5: recenter the runway-free viewport back to identity between gestures — but SKIP the reset when the content
        // transform is ALREADY identity (both browsers' OnViewportStatusChanged skip a no-op reset; a gesture that netted
        // zero, or the first READY, needs no recenter). ZoomToRect(0,0,w,h) with the window-sized viewport maps the content
        // back to origin. The caller then clears _haveBaseline so the recenter's async content update re-baselines silently.
        if (_content != null)
        {
            float* m = stackalloc float[6];
            if (_content->GetContentTransform(m, 6).SUCCEEDED
                && MathF.Abs(m[0] - 1f) <= 1e-4f && MathF.Abs(m[4]) <= 0.5f && MathF.Abs(m[5]) <= 0.5f)
                return;   // already identity — skip
        }
        _vp->ZoomToRect(0f, 0f, _vpW, _vpH, false);
    }

    private void OnWedge()
    {
        _wedgeCount++;
        if (ScrollLog.On) ScrollLog.Line($"DM WEDGE #{_wedgeCount} (engage>{DmEngageTimeoutMs}ms, no RUNNING)");
        if (_wedgeCount >= DmWedgeCountToDisable)
        {
            // Session-disable, edge-triggered: tear down so ProcessInput/SetContact are no longer called and the §3.3
            // fallback owns every subsequent packet. There is never a window with two owners for one packet.
            if (ScrollLog.On) ScrollLog.Line("DM DISABLED (wedge threshold) — §3.3 heuristic fallback now owns the touchpad");
            _enabled = false;
            Teardown();
            return;
        }
        if (_vp != null) _vp->Stop();   // abort the stuck manipulation so the next gesture can retry
    }

    private static string StatusName(int s) => s switch
    {
        DM_BUILDING => "BUILDING", DM_ENABLED => "ENABLED", DM_DISABLED => "DISABLED", DM_RUNNING => "RUNNING",
        DM_INERTIA => "INERTIA", DM_READY => "READY", DM_SUSPENDED => "SUSPENDED", _ => "?"
    };

    // ── teardown / dispose (release on every path) ──

    /// <summary>Release every COM object + free the CCW. Idempotent (a wedge-disable and Dispose both call it). Mirrors
    /// the dm-probe TearDown order: Stop → RemoveEventHandler → Disable → Abandon → Deactivate, then Release children
    /// before parents.</summary>
    private void Teardown()
    {
        if (_torn) return;
        _torn = true;
        _enabled = false;

        if (_vp != null)
        {
            _vp->Stop();
            if (_cookie != 0) { _vp->RemoveEventHandler(_cookie); _cookie = 0; }   // drops the viewport's ref on the sink
            _vp->Disable();
            _vp->Abandon();
        }
        if (_mgr != null && _hwnd != HWND.NULL) _mgr->Deactivate(_hwnd);

        if (_content != null) { _content->Release(); _content = null; }
        if (_vp != null) { _vp->Release(); _vp = null; }
        if (_upd != null) { _upd->Release(); _upd = null; }
        if (_mgr != null) { _mgr->Release(); _mgr = null; }

        if (_sink != null) { DmViewportEventHandlerCcw.Destroy(_sink); _sink = null; }   // back to our 1 ref → free
        // Freed only AFTER the viewport/update-manager/manager are released above — DM can no longer call GetNextFrameInfo,
        // so there is no live reference into this native block (mirrors the sink's post-Release free).
        if (_frameInfo != null) { DmFrameInfoProviderCcw.Destroy(_frameInfo); _frameInfo = null; }
        if (_self.IsAllocated) _self.Free();
    }

    public void Dispose()
    {
        Teardown();
        if (_coInited) { CoUninitialize(); _coInited = false; }
    }

    // ── CLSID / IIDs (directmanipulation.h 10.0.26100.0; hardcoded like WicImageCodec/UiaProviderCcw) ──
    private static readonly Guid CLSID_DirectManipulationManager =
        new(0x54E211B6, 0x3650, 0x4F75, 0x83, 0x34, 0xFA, 0x35, 0x95, 0x98, 0xE1, 0xC5);
    private static readonly Guid IID_IDirectManipulationManager =
        new(0xFBF5D3B4, 0x70C7, 0x4163, 0x93, 0x22, 0x5A, 0x6F, 0x66, 0x0D, 0x6F, 0xBC);
    private static readonly Guid IID_IDirectManipulationUpdateManager =
        new(0xB0AE62FD, 0xBE34, 0x46E7, 0x9C, 0xAA, 0xD3, 0x61, 0xFA, 0xCB, 0xB9, 0xCC);
    private static readonly Guid IID_IDirectManipulationViewport =
        new(0x28B85A3D, 0x60A0, 0x48BD, 0x9B, 0xA1, 0x5C, 0xE8, 0xD9, 0xEA, 0x3A, 0x6D);
    private static readonly Guid IID_IDirectManipulationContent =
        new(0xB89962CB, 0x3D89, 0x442B, 0xBB, 0x58, 0x50, 0x98, 0xFA, 0x0F, 0x9F, 0x16);
}

/// <summary>The hand-rolled <c>IDirectManipulationViewportEventHandler</c> CCW (vtable + refcount + owner GCHandle) —
/// modeled verbatim on <c>Win32DropTargetCcw</c>/<c>UiaProviderCcw</c>. The three sink thunks forward to the owning
/// <see cref="Win32DirectManipulation"/> (reached via <c>self->Owner</c>), swallowing any managed exception so nothing
/// crosses the COM boundary. Native-memory backed; the owner frees it in <c>Teardown</c> after RemoveEventHandler.</summary>
internal unsafe struct DmViewportEventHandlerCcw
{
    public void** Vtbl;   // MUST be first (the COM "this" vptr)
    public int Rc;
    public nint Owner;    // GCHandle.ToIntPtr(Win32DirectManipulation); 0 = detached

    // IID_IDirectManipulationViewportEventHandler {952121DA-D69F-45F9-B0F9-F23944321A6D}
    private static readonly Guid IID_IDirectManipulationViewportEventHandler =
        new(0x952121DA, 0xD69F, 0x45F9, 0xB0, 0xF9, 0xF2, 0x39, 0x44, 0x32, 0x1A, 0x6D);
    private static readonly Guid IID_IUnknown =
        new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
    private const int S_OK = 0, E_POINTER = unchecked((int)0x80004003), E_NOINTERFACE = unchecked((int)0x80004002);

    private static readonly void** _vtbl = Build();

    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(6, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<DmViewportEventHandlerCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<DmViewportEventHandlerCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<DmViewportEventHandlerCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<DmViewportEventHandlerCcw*, void*, int, int, int>)&OnViewportStatusChanged;
        v[4] = (delegate* unmanaged[MemberFunction]<DmViewportEventHandlerCcw*, void*, int>)&OnViewportUpdated;
        v[5] = (delegate* unmanaged[MemberFunction]<DmViewportEventHandlerCcw*, void*, void*, int>)&OnContentUpdated;
        return v;
    }

    public static DmViewportEventHandlerCcw* Create(nint owner)
    {
        var p = (DmViewportEventHandlerCcw*)NativeMemory.Alloc((nuint)sizeof(DmViewportEventHandlerCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Owner = owner;
        return p;
    }

    public static void Destroy(DmViewportEventHandlerCcw* p) => NativeMemory.Free(p);

    private static Win32DirectManipulation? OwnerOf(DmViewportEventHandlerCcw* self)
        => self->Owner != 0 && GCHandle.FromIntPtr(self->Owner).Target is Win32DirectManipulation p ? p : null;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(DmViewportEventHandlerCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return E_POINTER;
        if (*riid == IID_IUnknown || *riid == IID_IDirectManipulationViewportEventHandler)
        { Interlocked.Increment(ref self->Rc); *ppv = self; return S_OK; }
        *ppv = null; return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(DmViewportEventHandlerCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(DmViewportEventHandlerCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnViewportStatusChanged(DmViewportEventHandlerCcw* self, void* viewport, int current, int previous)
    {
        try { OwnerOf(self)?.HandleStatusChanged(current, previous); }
        catch { /* never throw across the COM boundary */ }
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnViewportUpdated(DmViewportEventHandlerCcw* self, void* viewport) => S_OK;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int OnContentUpdated(DmViewportEventHandlerCcw* self, void* viewport, void* content)
    {
        try { OwnerOf(self)?.HandleContentUpdated((IDirectManipulationContent*)content); }
        catch { /* never throw across the COM boundary */ }
        return S_OK;
    }
}

/// <summary>The hand-rolled <c>IDirectManipulationFrameInfoProvider</c> CCW (vtable + refcount + a POD composition-delta
/// field) — same hand-vtable shape as <see cref="DmViewportEventHandlerCcw"/>. DM calls <c>GetNextFrameInfo</c> once per
/// <c>UpdateManager.Update</c> to learn when the frame it is about to compute will hit the screen, and evaluates its
/// manipulation/inertia curve at that composition instant instead of the raw pump instant (Microsoft's documented
/// frame-info purpose — the DM-side latency compensation this file's §B.2 fix adds).
///
/// <para>Unlike the event-handler sink this CCW does NOT carry an owner <c>GCHandle</c>: the per-query callback must be
/// POD-only (no managed transition on the hot path), so the owner writes the answer into <see cref="CompositionDeltaMs"/>
/// (a plain native field) once per pump and the thunk just reads it back. IID verified against the Windows 10.0.26100.0
/// SDK header <c>directmanipulation.h</c> (<c>MIDL_INTERFACE("fb759dba-6f4c-4c01-874e-19c8a05907f9")</c>) and the shipped
/// TerraFX 10.0.26100.6 binding; the 4-slot vtable order (IUnknown ×3 + <c>GetNextFrameInfo</c>) matches the same header.
/// Native-memory backed; the owner frees it in <c>Teardown</c> after every DM object is released.</para></summary>
internal unsafe struct DmFrameInfoProviderCcw
{
    public void** Vtbl;         // MUST be first (the COM "this" vptr)
    public int Rc;
    public ulong CompositionDeltaMs;   // owner-written per pump: ms from this Update until the frame is on screen

    // IID_IDirectManipulationFrameInfoProvider {fb759dba-6f4c-4c01-874e-19c8a05907f9}
    private static readonly Guid IID_IDirectManipulationFrameInfoProvider =
        new(0xFB759DBA, 0x6F4C, 0x4C01, 0x87, 0x4E, 0x19, 0xC8, 0xA0, 0x59, 0x07, 0xF9);
    private static readonly Guid IID_IUnknown =
        new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
    private const int S_OK = 0, E_POINTER = unchecked((int)0x80004003), E_NOINTERFACE = unchecked((int)0x80004002);

    private static readonly void** _vtbl = Build();

    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<DmFrameInfoProviderCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<DmFrameInfoProviderCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<DmFrameInfoProviderCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<DmFrameInfoProviderCcw*, ulong*, ulong*, ulong*, int>)&GetNextFrameInfo;
        return v;
    }

    public static DmFrameInfoProviderCcw* Create()
    {
        var p = (DmFrameInfoProviderCcw*)NativeMemory.Alloc((nuint)sizeof(DmFrameInfoProviderCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->CompositionDeltaMs = 16;   // XAML-parity default: one 60Hz vblank until re-set
        return p;
    }

    public static void Destroy(DmFrameInfoProviderCcw* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(DmFrameInfoProviderCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return E_POINTER;
        if (*riid == IID_IUnknown || *riid == IID_IDirectManipulationFrameInfoProvider)
        { Interlocked.Increment(ref self->Rc); *ppv = self; return S_OK; }
        *ppv = null; return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(DmFrameInfoProviderCcw* self) => (uint)Interlocked.Increment(ref self->Rc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(DmFrameInfoProviderCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);

    // POD-only, zero-alloc: DM asks for the next frame's timing. We mirror XAML's DirectManipulationFrameInfoProvider
    // (returns time=0, processTime=0, compositionTime=delta-to-present in ms) — the shipped, proven shape — rather than
    // the absolute-time triple the plan sketched; the DM contract does not crisply document units, so this parity choice
    // is deliberately the safe one. See the reviewer flag in the change notes.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetNextFrameInfo(DmFrameInfoProviderCcw* self, ulong* time, ulong* processTime, ulong* compositionTime)
    {
        if (time != null) *time = 0;
        if (processTime != null) *processTime = 0;
        if (compositionTime != null) *compositionTime = self->CompositionDeltaMs;
        return S_OK;
    }
}
