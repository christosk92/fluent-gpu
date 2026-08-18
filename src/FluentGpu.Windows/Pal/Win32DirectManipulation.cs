using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;   // Point2, KeyModifiers, ScrollLog, ScrollTrace, Diag
using FluentGpu.Pal;          // InputEvent, InputKind, PointerKind, ScrollDeviceClass, FrameClock
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The DirectManipulation touchpad producer (scroll-v3-plan-2026-08-17.md §5.2): ONE
/// <c>IDirectManipulationUpdateManager::Update</c> per PRODUCED frame, issued from <c>Win32Platform.PumpScroll</c>
/// (<see cref="FluentGpu.Pal.IPlatformWindow.PumpScroll"/>) AFTER the display-phase gate has already decided this
/// frame is going to be produced, stamped with the SAME <see cref="FrameClock"/> the scroll kernel ticks against and
/// the physics clock reads — DM, physics and present now share one target time. There is no manual-update pacer, no
/// idle heartbeat and no wall-clock deadline anywhere in this file: a live gesture gets exactly one <c>Update</c> per
/// produced frame (<see cref="UpdateFrame"/>), and the MANUALUPDATE queue is otherwise drained on demand
/// (<see cref="UpdateIdle"/>, called by the host roughly every 250ms while <see cref="Enabled"/> and not
/// <see cref="Live"/> — see <c>Win32Platform.PumpInto</c>). <see cref="LastUpdateMs"/> is the one shared clock those
/// two entry points and the host's idle-drain threshold read.
///
/// <para>It emits the <see cref="InputKind.ScrollBegin"/>/<see cref="InputKind.ScrollDelta"/>/<see cref="InputKind.ScrollEnd"/>
/// phase-contract the <c>FluentGpu.Scroll.ScrollKernel</c> consumes, tagged <see cref="ScrollDeviceClass.Touchpad"/> —
/// one <see cref="InputKind.ScrollDelta"/> per produced frame, stamped <c>QpcTicks = clock.FrameQpc</c>, which is why
/// <c>ScrollTrace.ContactStampQuality</c> reads HARDWARE-grade here rather than the old receive-quantised grade (the
/// packet no longer arrives at the producer's own pump rate — it IS the frame's own stamp). PTP inertia is
/// ENGINE-owned: the PRIMARY configuration (<see cref="DM_CFG"/>) does not include
/// <c>TRANSLATION_INERTIA</c>/<c>SCALING_INERTIA</c> at all, so DM lifts straight RUNNING→READY and that lift alone is
/// the kernel's cue (<see cref="InputKind.ScrollEnd"/>) to seed its own Ballistic fling from the trailing frame-delta
/// history — there is no OS-curved coast left to ride, and no <c>Momentum*</c> kind exists to carry one. A
/// compile-time-only fallback (<see cref="UseOsInertiaStopFallback"/>, default OFF) exists for the case dm-probe cell
/// E/F (§5.6) finds the bare lift misbehaving on some hardware: flipping the const keeps <c>TRANSLATION_INERTIA</c> in
/// <see cref="DM_CFG"/>, lets DM enter INERTIA, and stops it from the OUTSIDE — <c>IDirectManipulationViewport::Stop</c>
/// is MSDN-documented legal in any viewport state — at the next pump rather than ride the curve; see
/// <see cref="HandleStatusChanged"/> and the <c>_pendingStop</c> field for why that <c>Stop()</c> cannot run inside the
/// COM sink callback that observes the INERTIA transition (same reason as <c>_pendingStrike</c> below).</para>
///
/// <para><b>COM discipline (com-interop.md).</b> Event-source only, all UI-thread. The DManip objects
/// (<c>Manager</c>/<c>UpdateManager</c>/<c>Viewport</c>/<c>Content</c>) are consumed through TerraFX's hand-vtable RCW
/// structs (the same shape <c>WicImageCodec</c> uses — no <c>ComWrappers</c> on the hot path); the one managed object we
/// hand back to COM, the <c>IDirectManipulationViewportEventHandler</c> sink, is a hand-rolled CCW modeled verbatim on
/// <c>Win32DropTargetCcw</c>/<c>UiaProviderCcw</c> (function-pointer vtable + interlocked refcount). Every COM object is
/// released on every path (<see cref="Teardown"/>), and the whole file lives inside <c>FluentGpu.Windows</c> so the
/// engine / Controls / VerticalSlice closure stays TerraFX-free.</para>
///
/// <para><b>Coexistence with the fallback (§5.3 wheel classifier, "never two owners for one packet").</b> When DM is
/// enabled it owns every touchpad contact — its <c>ProcessInput</c> consumes those packets before the WndProc sees
/// them, so the wheel-fallback path never double-processes them, and any <c>WM_POINTERWHEEL</c> that still reaches the
/// WndProc is genuinely a mouse. The MINIMAL recovery ladder that survives the rewrite — the engage wedge
/// (<see cref="DmEngageTimeoutMs"/>), a SUSPENDED read while an engage is pending, and the silent-owner case
/// (<see cref="DmSilentOwner"/>: DM holds the contacts but never engages) — feeds ONE <see cref="DmRecoveryLadder"/>,
/// which escalates Stop → recycle → session disable (<see cref="Teardown"/>), after which the always-compiled §5.3
/// heuristic takes over. The dedicated inertia-stall watchdog is DELETED: with RUNNING folded into liveness it killed a
/// genuine two-finger hold-then-continue after 250ms — a real bug, not a safety net. Popups never get a producer, so
/// they keep the fallback unconditionally.</para>
/// </summary>
internal sealed unsafe class Win32DirectManipulation : IDisposable
{
    // ── DIRECTMANIPULATION_STATUS (directmanipulation.h) — the viewport lifecycle we map to phase-contract events ──
    // internal, not private: the pure wedge/silent-owner arbiters below (and their headless tests) decide off these values.
    internal const int DM_BUILDING = 0, DM_ENABLED = 1, DM_DISABLED = 2, DM_RUNNING = 3, DM_INERTIA = 4, DM_READY = 5,
                       DM_SUSPENDED = 6;

    /// <summary>Compile-time-only switch (scroll-v3-plan §5.2): OFF (default) is the primary producer — no OS
    /// inertia, ever, and DM lifts RUNNING→READY directly. ON keeps <c>TRANSLATION_INERTIA</c> configured and stops a
    /// live INERTIA manipulation from the outside instead of riding its curve (see the class remarks and
    /// <see cref="HandleStatusChanged"/>). Flip this ONE constant — never a runtime knob — if hardware verification
    /// (dm-probe cell E, §5.6) finds the bare RUNNING→READY lift misbehaving (a stray post-lift
    /// <c>WM_POINTERWHEEL</c>, a late/duplicated status callback, …).</summary>
    private const bool UseOsInertiaStopFallback = false;

    // ── DIRECTMANIPULATION_CONFIGURATION flags (directmanipulation.h), verified against the dm-probe cell-B PASS ──
    //   Primary: INTERACTION|TRANSLATION_X|TRANSLATION_Y|SCALING — NO TRANSLATION_INERTIA/SCALING_INERTIA (§5.2: PTP
    //   inertia is engine-owned; there is no OS coast to configure). SCALING stays configured (without SCALING_INERTIA)
    //   only so a pinch is a legal gesture we can DETECT and suppress (|scale-1|>ε ⇒ emit no pan); we never translate a
    //   scale into scroll. No RAILS_* — the integrator's axis latch owns railing. TRANSLATION_INERTIA is added back ONLY
    //   when UseOsInertiaStopFallback flips true (both const, so this folds to a compile-time value — no runtime cost,
    //   no dead branch left in the shipped primary build).
    private const int DM_CFG =
        (int)(DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_INTERACTION
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_X
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_Y
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_SCALING)
        | (UseOsInertiaStopFallback ? (int)DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_INERTIA : 0);

    // ── §5.2 tuning: DManip-only constants (they belong to the Windows producer, not the portable ScrollKernel) ──
    /// <summary>Frozen DIP per content-transform unit (no knee). The 1000×1000 viewport rect is in physical px, so
    /// the content-transform translation is in physical device px; DIP = px / (dpi/96). DManip returns true OS device
    /// pixels — no <c>HiResUnitDip</c> calibration.</summary>
    private const float DmDipPerTransformUnit = 1.0f;
    /// <summary>|scale−1| beyond this ⇒ the gesture is a pinch, not a pan ⇒ emit no pan.</summary>
    private const float DmPinchScaleEpsilon = 0.01f;
    /// <summary>Transform-unit delta below which a content update is treated as a no-op (skips zero-delta spam; well below
    /// the smallest real inertia-tail delta observed in the probe, ~0.1u).</summary>
    private const float DmMinTransformDelta = 0.01f;
    /// <summary>Wedge watchdog: a <c>SetContact</c> that does not reach <c>RUNNING</c> within this many ms is a wedge.</summary>
    private const long DmEngageTimeoutMs = 120;
    /// <summary>Minimum gap (ms) between ScrollTrace note-109 hit-test rows. Normal scrolling hit-tests at contact
    /// cadence and would otherwise flood the ring, hiding the very stall the row exists to characterise.</summary>
    internal const long HitTestNoteMinGapMs = 250;

    // The fake event-source viewport = the real window client rect, with NO SetContentRect. Both browsers
    // (direct_manipulation_helper_win.cc, DirectManipulationOwner.cpp) size the viewport to the window and never call
    // SetContentRect — DM translation against the DEFAULT content is effectively unbounded, so runway exhaustion (the
    // old 200k-content trigger) never occurs. That deletes BOTH self-inflicted defects at the source: the OS chaining an
    // exhausted pan out as synthesized ±120 WM_POINTERWHEEL bursts, and a giant recenter origin amplifying sub-epsilon
    // two-finger scale drift ds into an origin·ds phantom pan. We never DISPLAY this transform — we only read its
    // translation deltas; the viewport recenters to identity only at READY (between gestures). A 1000×1000 fallback is
    // used only when the window rect is unavailable (a 0-size pre-first-layout window).
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
    private uint _contactId;                // stable per gesture ⇒ the ring's ScrollDelta coalescing sums per frame
    private Point2 _contactPos;             // the pan anchor (touchpad cursor at engage) — hit-tests the scroll target
    private byte _seq;                      // per-gesture packet ordinal (velocity side-buffer cross-contamination tag)

    // ── wedge watchdog ──
    private bool _awaitingEngage;           // a SetContact is pending RUNNING
    private long _engageTick;               // Environment.TickCount64 at SetContact
    // ── silent-owner watchdog (see DmSilentOwner) ──
    // Deliberately NOT keyed on a "last progress" stamp: SetContact and every status change would re-stamp such a
    // value, and those are exactly the events that DO keep occurring per contact while DM owns the touchpad without
    // ever engaging — keyed on it the predicate could never fire. These stamps move only on real DM manipulation /
    // real user attempts.
    private long _lastEngagedMs;            // TickCount64 when DM last actually manipulated (RUNNING/INERTIA, or an owned content delta); 0 = never
    private long _lastHitTestMs;            // TickCount64 at the last DM_POINTERHITTEST (see NoteHitTest)
    private int _hitTestsSinceEngage;       // hit-tests observed since DM last engaged — the "unserved attempts" count
    private long _lastHitTestNoteMs;        // TickCount64 at the last note-109 row (rate limit; 0 = never, so the first hit-test always records)
    // ── unified recovery escalation (all detectors feed it) ──
    private DmRecoveryLadder _ladder;
    // A strike raised from inside a COM sink callback, serviced at the next pump. RecordStrike can reach the Disable
    // rung, and Teardown frees the very IDirectManipulationViewportEventHandler CCW whose thunk is on the stack
    // (RemoveEventHandler + NativeMemory.Free) while DM still holds a raw pointer to it. Deferring costs one pump.
    private DmStallDetector _pendingStrike;
    // The fallback's deferred Stop() (see UseOsInertiaStopFallback): set from inside HandleStatusChanged on
    // RUNNING→INERTIA, serviced at the top of the NEXT UpdateFrame/UpdateIdle — never inside the sink callback.
    private bool _pendingStop;

    // ── pump-time stamping (scroll-jitter §B.1 / scroll-v3 §5.2) ──
    private long _pumpQpc;                   // the frame instant this pump's content deltas are stamped with (FrameClock.FrameQpc for a produced frame)
    private long _pumpMs;                    // Environment.TickCount64 captured at the same instant (coarse ms clock, kept in step)
    // A stamp older than this (relative to now) was NOT produced by the current pump — an Emit reached from a
    // ProcessInput-time status change (contact-engage → RUNNING) — so Emit re-reads now instead of back-dating it.
    // ~20ms ≈ 1.2 vsync frames: comfortably larger than any same-pump Update body, smaller than a cross-frame gap.
    private static readonly long StaleStampTicks = Stopwatch.Frequency / 50;

    /// <summary>Environment.TickCount64 of the last <c>IDirectManipulationUpdateManager::Update</c> call this producer
    /// issued — of EITHER kind, <see cref="UpdateFrame"/> or <see cref="UpdateIdle"/>. The host reads this to decide
    /// when the idle MANUALUPDATE queue is next owed a drain (roughly every 250ms while <see cref="Enabled"/> and not
    /// <see cref="Live"/>) — there is no pacer/heartbeat left in this file to do that on its own.</summary>
    internal long LastUpdateMs { get; private set; }

    private Win32DirectManipulation(Win32Window window, HWND hwnd)
    {
        _window = window;
        _hwnd = hwnd;
    }

    /// <summary>Create + wire the producer for <paramref name="hwnd"/>, or return null if DirectManipulation is
    /// unavailable (MTA thread, CoCreate failed, any setup HRESULT failed) — the caller then relies on the §5.3
    /// fallback. Mirrors <c>Win32DropTarget.Register</c>'s best-effort posture.</summary>
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

    /// <summary>True while a produced-frame pump is owed to DM: a contact is engaged or pending engagement, or a
    /// manipulation is RUNNING (plus INERTIA when <see cref="UseOsInertiaStopFallback"/> is on). Drives the host's
    /// per-refresh production decision (<see cref="FluentGpu.Pal.IPlatformWindow.ScrollProducerLive"/>), the idle-drain
    /// gate (<c>Enabled &amp;&amp; !Live</c> in <c>PumpInto</c>), and every internal "is DM genuinely live right now"
    /// check in this file (the silent-owner watchdog, <see cref="TryStopForPhysicalWheel"/>'s wheel-arbitration guard)
    /// — one boolean, one definition, no drift between what gates production and what gates recovery.</summary>
    internal bool Live => _enabled && (_awaitingEngage || _status == DM_RUNNING
                                        || (UseOsInertiaStopFallback && _status == DM_INERTIA));

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
        // to the instant this pump's frame actually lands, instead of sampling its curve at the raw pump instant.
        // CreateViewport treats it as _In_opt_. Left at the CCW's own XAML-parity default (16ms) until the first real
        // UpdateFrame/UpdateIdle call overwrites it with a computed lead.
        _frameInfo = DmFrameInfoProviderCcw.Create();

        Guid iidVp = IID_IDirectManipulationViewport;
        IDirectManipulationViewport* vp = null;
        if (_mgr->CreateViewport((IDirectManipulationFrameInfoProvider*)_frameInfo, _hwnd, &iidVp, (void**)&vp).FAILED || vp == null) return false;
        _vp = vp;

        // Viewport rect = the window client rect (browsers size the DM viewport to the window). Fallback to 1000×1000
        // only if the rect is unavailable (a 0-size pre-first-layout window).
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

        // Hold the primary content for GetContentTransform (deltas) + the READY identity-skip. NO SetContentRect — the
        // default content gives unbounded translation, so there is no runway to exhaust and no giant origin to amplify
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

    // ── message pump hooks (called from Win32Window.PumpInto / Win32Platform.PumpScroll, UI thread) ──

    /// <summary>Feed one pumped message to DManip BEFORE dispatch (§5.2 "per-message ProcessInput"). Returns true when
    /// DManip consumed it (an active touchpad-contact packet) so the caller skips Translate/Dispatch — this is what keeps
    /// the fallback from ever seeing a packet DM owns.</summary>
    internal bool ProcessInput(MSG* msg)
    {
        if (!_enabled || _mgr == null) return false;
        BOOL handled = default;
        _mgr->ProcessInput(msg, &handled);
        return (bool)handled;
    }

    /// <summary>
    /// THE one <c>IDirectManipulationUpdateManager::Update</c> issued for a PRODUCED frame — called from
    /// <c>Win32Platform.PumpScroll</c> (<see cref="FluentGpu.Pal.IPlatformWindow.PumpScroll"/>) AFTER the
    /// display-phase gate has already decided this frame is going to be produced (scroll-v3-plan-2026-08-17.md §5.2
    /// "hole found &amp; fixed": the old pacer could fire an Update against a declined, non-lattice instant).
    /// <paramref name="clock"/> is the SAME <see cref="FrameClock"/> the scroll kernel ticks against.
    ///
    /// <para>Sets the DManip frame-info hint (<c>DmFrameInfoProviderCcw.CompositionDeltaMs</c>) to this frame's
    /// CONTACT LEAD — <c>clamp(PresentQpc − now, 0, one refresh)</c>, the contact-lead policy of min(actual lead, one
    /// refresh) — so DM evaluates/predicts to the instant this frame's pixels actually land, never leading by more
    /// than a single vblank (the "16ms felt too fast / easy to lose control" field report was two refreshes' worth of
    /// lead on a 120Hz panel — see §5.6). Floored to 0 while <see cref="FrameClockFlags.Unpaced"/> is set (an unpaced
    /// present has no meaningful lead to predict to).</para>
    ///
    /// <para>Every content delta this Update produces is stamped <c>QpcTicks = clock.FrameQpc</c> (see
    /// <see cref="Emit"/>) — exactly one <see cref="InputKind.ScrollDelta"/> per produced frame per contact, at the
    /// frame's own stamp, hardware-grade by construction.</para>
    /// </summary>
    internal void UpdateFrame(in FrameClock clock)
    {
        if (!_enabled) return;
        long nowMs = Environment.TickCount64;
        ServiceWatchdogs(nowMs);
        if (!_enabled) return;   // the ladder may have session-disabled + torn down mid-service

        if (UseOsInertiaStopFallback && _pendingStop)
        {
            _pendingStop = false;
            if (_vp != null) _vp->Stop();   // never called from inside the COM sink that observed INERTIA — see the class remarks
        }

        ScrollTrace.ContactStampQuality = GenStampQuality.Hardware;

        long nowQpc = Stopwatch.GetTimestamp();
        double refreshMs = clock.RefreshQpc > 0 ? clock.RefreshQpc * 1000.0 / Stopwatch.Frequency : 0.0;
        double leadMs = (clock.Flags & FrameClockFlags.Unpaced) != 0
            ? 0.0
            : Math.Clamp((clock.PresentQpc - nowQpc) * 1000.0 / Stopwatch.Frequency, 0.0, refreshMs);

        PumpOnce(clock.FrameQpc, nowMs, leadMs);
    }

    /// <summary>The idle MANUALUPDATE drain (§5.2 "idle drain replacement"): a bare <c>Update</c> with lead 0, so a
    /// queue DM has been holding (a content update from the READY recenter, a status transition) is never left to sit
    /// forever between produced frames. Called by the host (<c>Win32Platform.PumpInto</c>) at most every ~250ms while
    /// <see cref="Enabled"/> and not <see cref="Live"/> — while <see cref="Live"/>, <see cref="UpdateFrame"/> already
    /// pumps every produced frame and this would double-pump. Content updates surfaced here while <c>!Owns</c>
    /// re-baseline silently (<see cref="HandleContentUpdated"/>'s <c>!Owns</c> guard) — they never reach
    /// <see cref="Emit"/>.</summary>
    internal void UpdateIdle()
    {
        if (!_enabled) return;
        long nowMs = Environment.TickCount64;
        ServiceWatchdogs(nowMs);
        if (!_enabled) return;
        PumpOnce(Stopwatch.GetTimestamp(), nowMs, 0.0);
    }

    /// <summary>Shared pump body for <see cref="UpdateFrame"/>/<see cref="UpdateIdle"/>/<see cref="TryStopForPhysicalWheel"/>:
    /// stamp, set the composition-latency hint, call <c>Update</c>, record <see cref="LastUpdateMs"/>.</summary>
    private void PumpOnce(long stampQpc, long nowMs, double leadMs)
    {
        _pumpQpc = stampQpc;
        _pumpMs = nowMs;
        if (_frameInfo != null) _frameInfo->CompositionDeltaMs = (ulong)Math.Round(Math.Max(0.0, leadMs));
        if (_upd != null) _upd->Update((IDirectManipulationFrameInfoProvider*)_frameInfo);
        LastUpdateMs = nowMs;
    }

    /// <summary>The minimal recovery-ladder poll (§5.2 "minimal"): a strike raised from inside a COM sink callback
    /// (see <see cref="_pendingStrike"/>), the engage wedge (a <c>SetContact</c> that never reached RUNNING within
    /// <see cref="DmEngageTimeoutMs"/>), and the silent-owner case (DM holds the touchpad but never engages while
    /// hit-tests keep arriving — <see cref="DmSilentOwner"/>). Called from BOTH pump entry points
    /// (<see cref="UpdateFrame"/>, <see cref="UpdateIdle"/>) so every produced-frame pump and every idle drain
    /// services the same ladder — there is no separate pacer/heartbeat loop driving it any more. The SUSPENDED-while-
    /// pending case is event-driven (see <see cref="HandleStatusChanged"/>) and reaches the ladder through
    /// <see cref="_pendingStrike"/> like the rest; the dedicated inertia-stall watchdog that used to live here is
    /// DELETED (class remarks).</summary>
    private void ServiceWatchdogs(long nowMs)
    {
        if (_pendingStrike != DmStallDetector.None)
        {
            DmStallDetector pending = _pendingStrike;
            _pendingStrike = DmStallDetector.None;
            RecordStrike(pending);
            if (!_enabled) return;
        }
        if (_awaitingEngage && nowMs - _engageTick > DmEngageTimeoutMs)
        {
            _awaitingEngage = false;
            RecordStrike(DmStallDetector.EngageWedge);
            if (!_enabled) return;
        }
        if (DmSilentOwner.IsSilentOwner(Live, nowMs, _lastHitTestMs, _lastEngagedMs, _hitTestsSinceEngage))
        {
            RecordStrike(DmStallDetector.SilentOwner);   // first, so its note still carries the true blackout age
            if (!_enabled) return;
            _hitTestsSinceEngage = 0;
            _lastEngagedMs = nowMs;
        }
    }

    /// <summary>A DM_POINTERHITTEST reached the window — the user is trying to manipulate us RIGHT NOW. Counted and
    /// stamped on the <see cref="Environment.TickCount64"/> clock for <see cref="DmSilentOwner"/>; the caller's own
    /// <c>_lastDmHitTestMs</c> is a different (GetMessageTime) clock base serving the wheel-burst log and stays
    /// untouched. The counter zeroes when DM engages, so it reads "attempts DM has not served".</summary>
    internal void NoteHitTest()
    {
        long nowMs = Environment.TickCount64;
        _lastHitTestMs = nowMs;
        if (_hitTestsSinceEngage < int.MaxValue) _hitTestsSinceEngage++;
        // ScrollTrace note 109 (registered in ScrollTrace.cs), rate-limited on its OWN stamp: the next stall must be
        // decidable from the ring alone — a blackout with NO 109 rows means no contact ever reached us (DM or the OS
        // swallowed the stream), while 109 rows through the blackout mean hit-tests arrived and went unserved, which is
        // the silent-owner shape. Without it the two are indistinguishable in a capture.
        if (nowMs - _lastHitTestNoteMs >= HitTestNoteMinGapMs)
        {
            _lastHitTestNoteMs = nowMs;
            ScrollTrace.Note(109, (float)(nowMs - _lastEngagedMs), _status, _hitTestsSinceEngage);
        }
    }

    /// <summary>DM_POINTERHITTEST (0x0250) → claim this contact for DManip. Gated to <c>PT_TOUCHPAD</c> by the caller.
    /// Returns true iff SetContact succeeded (the caller then consumes the message, matching the dm-probe).</summary>
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
        // (a stable id keeps the ring's ScrollDelta coalescing summing per frame).
        if (!Owns)
        {
            _contactId = pointerId;
            _contactPos = contactDip;
        }
        _awaitingEngage = true;
        _engageTick = Environment.TickCount64;
        return true;
    }

    /// <summary>True while DM is genuinely manipulating the primary content right now (RUNNING, plus INERTIA under
    /// the fallback) — distinct from <see cref="Live"/>, which also counts a pending-but-not-yet-engaged contact.
    /// Governs whether a fresh <c>SetContact</c> may re-latch the gesture anchor and whether a content-transform read
    /// is a real delta or a re-baseline.</summary>
    private bool Owns => _status == DM_RUNNING || (UseOsInertiaStopFallback && _status == DM_INERTIA);

    // ── the CCW sink callbacks (forwarded from the hand-vtable thunks; UI thread) ──

    internal void HandleStatusChanged(int current, int previous)
    {
        // OUR tracked prior status drives every emit/disarm decision, not DM's `previous` param. They are identical in
        // normal flow; they diverge exactly when the recovery ladder has already fabricated the terminal event and set
        // _status = DM_READY itself (rungs >=2), and then a late real READY(previous=RUNNING) must NOT re-emit it.
        // DM's own account of the transition stays in the log line, where the divergence is the interesting part.
        int prior = _status;
        long nowMs = Environment.TickCount64;
        _status = current;
        Diag.Set("dm", "status", current);
        if (ScrollLog.On) ScrollLog.Line($"DM STATUS {StatusName(previous)}->{StatusName(current)}");

        if (current == DM_RUNNING)
        {
            _awaitingEngage = false;   // engaged — not a wedge
            if (_ladder.Strikes > 0)
            {
                // The one row that names WHICH rung revived input on a recurrence — emitted before the reset so it
                // still carries the strike count and the blackout age.
                NoteStrike(DmStallDetector.Recovered, DmRecoveryAction.None, nowMs);
                if (ScrollLog.On) ScrollLog.Line($"DM RECOVERED after {_ladder.Strikes} strike(s)");
                _ladder.Reset();
                Diag.Set("dm", "strikes", 0);
            }
            _lastEngagedMs = nowMs;
            _hitTestsSinceEngage = 0;
            // fingers re-landed mid-coast (stop-on-contact): the prior INERTIA entry already closed the gesture with
            // a ScrollEnd (below) — no second terminal event needed, just open the new one.
            Emit(InputKind.ScrollBegin, 0f, 0f);
            _haveBaseline = false;     // the next content update captures the baseline, emits nothing
            _seq = 0;
        }
        else if (current == DM_INERTIA && prior == DM_RUNNING)
        {
            _lastEngagedMs = nowMs;
#pragma warning disable CS0162 // UseOsInertiaStopFallback folds: exactly one arm is live per the flipped const (Component.cs precedent)
            if (UseOsInertiaStopFallback)
            {
                // Never call COM from inside a sink callback (same reason as _pendingStrike) — serviced at the top of
                // the NEXT UpdateFrame/UpdateIdle. ScrollEnd is deferred to the READY that Stop() produces ("stamped
                // at the edge" — see the READY branch below), not emitted here.
                _pendingStop = true;
            }
            else
            {
                // The primary configuration never enables TRANSLATION_INERTIA, so DM should never report INERTIA; if a
                // driver still does, PTP inertia is engine-owned regardless (§5.2) — treat it exactly like an ordinary
                // lift, same as the RUNNING→READY hold-release path below.
                Emit(InputKind.ScrollEnd, 0f, 0f);
            }
#pragma warning restore CS0162
            // Keep the baseline live either way — HandleContentUpdated still tracks DM's transform while INERTIA runs
            // (so a re-contact mid-coast has a sane reference), it just stops forwarding it (see HandleContentUpdated).
        }
        else if (current == DM_READY)
        {
            // Natural RUNNING→READY finish (hold-release, no OS momentum): the ordinary lift.
            if (prior == DM_RUNNING) Emit(InputKind.ScrollEnd, 0f, 0f);
            // Fallback only: the deferred Stop() (see the INERTIA branch above) completed — the lift fires HERE, at
            // the edge, per §5.2. Unreachable when UseOsInertiaStopFallback is false (DM never enters INERTIA then).
            else if (UseOsInertiaStopFallback && prior == DM_INERTIA) Emit(InputKind.ScrollEnd, 0f, 0f);
            _pendingStop = false;
            // Cancel the engage wedge ONLY on a READY that genuinely terminates an engage. A spurious READY (from
            // ENABLED/BUILDING/SUSPENDED) used to disarm it unconditionally, which is precisely how a DM that owned
            // the touchpad but never engaged silenced the watchdog. A tap-to-click contact DM legitimately declines
            // now wedges; that is harmless, because rung 1 on a READY viewport is a no-op Stop and the tap safety
            // lives in the ladder's reset-on-RUNNING plus its 10-second decay, not in this disarm.
            if (DmEngageWedge.Disarms(current, prior)) _awaitingEngage = false;
            ResetViewport();           // recenter so the next gesture has fresh runway
            _haveBaseline = false;     // the recenter's content update re-baselines silently (Owns is false now)
        }
        else if (DmEngageWedge.WedgesImmediately(current, _awaitingEngage))
        {
            // SUSPENDED while a SetContact is pending RUNNING means DM parked the contact instead of engaging it —
            // there is no timeout left worth waiting out.
            _awaitingEngage = false;
            _pendingStrike = DmStallDetector.Suspended;   // out of the COM sink — see the _pendingStrike remarks
        }
    }

    /// <summary>Give a positively identified physical mouse immediate ownership over a live touchpad manipulation.
    /// Stop covers both contact and inertia; one synchronous manual update emits the terminal phase before the caller
    /// queues the same wheel packet. Unknown/touchpad sources must never call this method.
    /// False = there was no live manipulation left to stop, which also means the caller's live-gesture wheel state (the
    /// ±120 burst latch) is stale — Win32Platform clears it on this return so it cannot eat the next gesture.</summary>
    internal bool TryStopForPhysicalWheel()
    {
        if (!Live || _vp == null) return false;
        _vp->Stop();
        PumpOnce(Stopwatch.GetTimestamp(), Environment.TickCount64, 0.0);
        return true;
    }

    internal void HandleContentUpdated(IDirectManipulationContent* content)
    {
        if (content == null) return;
        float* m = stackalloc float[6];
        if (content->GetContentTransform(m, 6).FAILED) return;
        float scale = m[0], tx = m[4], ty = m[5];

        // CONTENT-SPACE position: the content coordinate under the viewport origin, p = −t/s. At s=1 the diff equals
        // the browsers' negated raw-translation diff (the F1 sign convention) — a strict superset: content space also
        // stays robust to residual two-finger scale drift (ds contributes only its bounded zoom-center shift, not an
        // origin·ds phantom pan) now that the giant recenter origin is gone (no SetContentRect deletes the runway that
        // used to amplify it). Deliberately differs from the browsers' raw diff because it subsumes it.
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

        // The silent-owner watchdog measures DM PRODUCTION, not emitted scroll: a pinch is suppressed downstream but
        // DM is very much alive, so stamp before the suppression returns. Sub-epsilon no-op deltas deliberately do NOT
        // stamp — a manipulation that only ever produces those is exactly the stuck state the watchdog exists to end.
        if (MathF.Abs(scale - 1f) > DmPinchScaleEpsilon) { _lastEngagedMs = Environment.TickCount64; return; }   // pinch: suppress the pan (§5.2)
        if (MathF.Abs(dx) < DmMinTransformDelta && MathF.Abs(dy) < DmMinTransformDelta) return;
        _lastEngagedMs = Environment.TickCount64;

        float wscale = _window.ScaleInternal;
        if (wscale <= 0f) wscale = 1f;
        // Content-space px → DIP. p = −t/s already carries the engine's delta convention: for a pure pan (s=1) the
        // diff equals the NEGATED raw-translation diff, matching the WM_POINTERWHEEL fallback ("−delta = scroll toward
        // content end") — fingers up ⇒ advance toward content end.
        float dipX = dx * DmDipPerTransformUnit / wscale;
        float dipY = dy * DmDipPerTransformUnit / wscale;

        // PTP inertia is engine-owned (§5.2): under the primary config DM never reports INERTIA, so this check never
        // trips there. Under the fallback, the RUNNING→INERTIA transition already closed the gesture (deferred to the
        // READY the Stop() produces — see HandleStatusChanged), so DM's own INERTIA-phase content deltas are tracked
        // (the baseline advance above) but never forwarded — the kernel's own Ballistic fling is already running.
        if (_status != DM_INERTIA) Emit(InputKind.ScrollDelta, dipX, dipY);
    }

    // ── event emission ──

    private void Emit(InputKind kind, float dipX, float dipY)
    {
        // Prefer this pump's frame timestamp so every content update from one UpdateManager.Update shares one instant.
        // Emit is ALSO reachable outside the Update pump: a contact-engage status change (RUNNING) can fire
        // synchronously inside ProcessInput, where _pumpQpc still holds the PREVIOUS pump's value (~1 frame stale). We
        // detect that (stamp older than ~1 frame, or never set) and read now instead, so phase markers aren't
        // back-dated; content updates — the resampler-critical path — always run inside a pump and see a fresh
        // _pumpQpc. ms is taken from the same instant (Stopwatch and TickCount64 both advance in real time).
        long now = Stopwatch.GetTimestamp();
        long qpc = _pumpQpc;
        long ms64 = _pumpMs;
        if (qpc == 0 || now - qpc > StaleStampTicks) { qpc = now; ms64 = Environment.TickCount64; }
        uint ms = unchecked((uint)ms64);
        bool isUpdate = kind == InputKind.ScrollDelta;
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
        // Recenter the runway-free viewport back to identity between gestures — but SKIP the reset when the content
        // transform is ALREADY identity (both browsers' OnViewportStatusChanged skip a no-op reset; a gesture that
        // netted zero, or the first READY, needs no recenter). ZoomToRect(0,0,w,h) with the window-sized viewport maps
        // the content back to origin. The caller then clears _haveBaseline so the recenter's async content update
        // re-baselines silently.
        if (_content != null)
        {
            float* m = stackalloc float[6];
            if (_content->GetContentTransform(m, 6).SUCCEEDED
                && MathF.Abs(m[0] - 1f) <= 1e-4f && MathF.Abs(m[4]) <= 0.5f && MathF.Abs(m[5]) <= 0.5f)
                return;   // already identity — skip
        }
        _vp->ZoomToRect(0f, 0f, _vpW, _vpH, false);
    }

    /// <summary>ScrollTrace note 107 (registered in ScrollTrace.cs) — the one record that survives a feel session run
    /// with <c>FG_SCROLL_LOG</c> off, which is how the live 218-second blackout went forensically silent. Scalars only,
    /// self-gating, allocation-free.</summary>
    private void NoteStrike(DmStallDetector detector, DmRecoveryAction action, long nowMs)
        => ScrollTrace.Note(107, (float)(nowMs - _lastEngagedMs),
            (int)detector | ((int)action << 4) | (_status << 8),
            _ladder.Strikes, (float)(nowMs - _lastHitTestMs));

    /// <summary>One strike on the unified recovery ladder, whatever detector saw it, and the escalation it earns.
    /// Rung 1 is the historical behavior (Stop); rung 2 recycles the DM input session; rung 3 is the production-proven
    /// session disable. Never called from inside a COM sink callback — see <c>_pendingStrike</c>.</summary>
    private void RecordStrike(DmStallDetector detector)
    {
        long nowMs = Environment.TickCount64;
        DmRecoveryAction action = _ladder.Record(nowMs);
        NoteStrike(detector, action, nowMs);
        Diag.Set("dm", "strikes", _ladder.Strikes);
        if (ScrollLog.On)
            ScrollLog.Line($"DM STRIKE #{_ladder.Strikes} {detector} -> {action} (status={StatusName(_status)}, "
                + $"sinceEngage={nowMs - _lastEngagedMs}ms, sinceHitTest={nowMs - _lastHitTestMs}ms)");

        switch (action)
        {
            case DmRecoveryAction.Stop:
                if (_vp != null) _vp->Stop();   // abort the stuck manipulation so the next gesture can retry
                break;
            case DmRecoveryAction.Recycle:
                if (TryRecycle()) break;
                goto case DmRecoveryAction.Disable;   // any FAILED HRESULT escalates to the proven Teardown rung
            case DmRecoveryAction.Disable:
                FabricateTerminalIfLive();
                // Session-disable, edge-triggered: tear down so ProcessInput/SetContact are no longer called and the
                // §5.3 fallback owns every subsequent packet. There is never a window with two owners for one packet.
                // The cost is calibrated: the user keeps a working scroll and loses exact device classification until
                // the next launch — bounded, unlike minutes of dead input.
                if (ScrollLog.On) ScrollLog.Line("DM DISABLED (recovery ladder) — §5.3 heuristic fallback now owns the touchpad");
                Diag.Set("dm", "enabled", 0);
                _enabled = false;
                Teardown();
                break;
        }
    }

    /// <summary>Rung 2 — the code-level alt-tab. Enable/Disable toggle input processing only (the configuration, the
    /// event-handler cookie and the primary content all survive; <c>RemoveEventHandler</c> exists solely in
    /// <see cref="Teardown"/>), and Deactivate/Activate is Chromium's <c>DirectManipulationHelper</c> pattern — the
    /// in-code twin of the activation reset that provably revived the live stall. <c>Abandon</c> is deliberately
    /// excluded: it is terminal and belongs to Teardown. Returns false on any FAILED HRESULT so the caller escalates
    /// rather than leaving a half-recycled session. Never hand-sets <c>_status</c> past the fabricated terminal — the
    /// still-registered sink updates it truthfully.</summary>
    private bool TryRecycle()
    {
        if (_vp == null || _mgr == null || _hwnd == HWND.NULL) return false;
        FabricateTerminalIfLive();
        if (_vp->Stop().FAILED) return false;
        if (_vp->ReleaseAllContacts().FAILED) return false;
        if (_vp->Disable().FAILED) return false;
        if (_vp->Enable().FAILED) return false;
        if (_mgr->Deactivate(_hwnd).FAILED) return false;
        if (_mgr->Activate(_hwnd).FAILED) return false;
        _awaitingEngage = false;
        _haveBaseline = false;
        _pendingStrike = DmStallDetector.None;   // status chatter from the toggles above is ours, not a fresh wedge
        _pendingStop = false;
        return true;
    }

    /// <summary>Close an open phase contract the kernel would otherwise hold forever. This is a DELIBERATE, confined
    /// exception to this file's never-fabricate doctrine. At ladder rungs >=2 DM has already refused to fire the
    /// ordinary terminal callback and the recovery re-creates the input session underneath it, so nothing else will
    /// ever end the gesture. Double emission is impossible: <see cref="HandleStatusChanged"/> gates on OUR tracked
    /// prior status, which this sets to READY, so a late real READY(previous=RUNNING) emits nothing — and, under the
    /// fallback, an INERTIA gesture's ScrollEnd is already pending via <c>_pendingStop</c>'s eventual READY, so only a
    /// still genuinely-open RUNNING gesture needs one fabricated here.</summary>
    private void FabricateTerminalIfLive()
    {
        if (_status != DM_RUNNING && !(UseOsInertiaStopFallback && _status == DM_INERTIA)) return;
        if (_status == DM_RUNNING) Emit(InputKind.ScrollEnd, 0f, 0f);
        _status = DM_READY;
        _pendingStop = false;
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

internal enum DmWheelSourceEvidence : byte
{
    Unknown,
    PhysicalMouse,
    Touchpad,
}

internal enum DmWheelRoute : byte
{
    ExistingClassifier,
    StopDmAndPass,
    DmOwned,
}

/// <summary>Which watchdog raised a strike. The values are wire-visible (the low nibble of ScrollTrace note 107's i1):
/// append only, never renumber — 3 is unassigned.</summary>
internal enum DmStallDetector : byte
{
    None = 0,
    EngageWedge = 1,
    Suspended = 2,
    SilentOwner = 4,
    /// <summary>Not a strike — the row emitted when DM engages again with strikes outstanding, so a capture names the
    /// rung that actually revived input.</summary>
    Recovered = 5,
}

/// <summary>The escalation a strike earns. Also wire-visible (note 107 i1 bits 4-7).</summary>
internal enum DmRecoveryAction : byte
{
    None = 0,
    Stop = 1,
    Recycle = 2,
    Disable = 3,
}

/// <summary>Pure engage-wedge arbitration — the decision half of the 120ms wedge watchdog, split out so the Windows
/// headless tests can lock it without a real HWND or viewport.</summary>
internal static class DmEngageWedge
{
    /// <summary>True only for a READY that genuinely TERMINATES an engage. A READY arriving from
    /// ENABLED/BUILDING/SUSPENDED means the engage never ran, and cancelling the wedge on it is how a DM that owned
    /// the touchpad for 218 seconds produced zero watchdog fires. <paramref name="prior"/> is the integrator's own
    /// tracked status, not DM's <c>previous</c> callback parameter.</summary>
    internal static bool Disarms(int current, int prior)
        => current == Win32DirectManipulation.DM_READY
        && (prior == Win32DirectManipulation.DM_RUNNING || prior == Win32DirectManipulation.DM_INERTIA);

    /// <summary>SUSPENDED while a SetContact is pending RUNNING is a wedge with no timeout left worth waiting out.
    /// Gated on the pending engage so an occlusion-SUSPENDED between gestures records nothing.</summary>
    internal static bool WedgesImmediately(int current, bool awaitingEngage)
        => current == Win32DirectManipulation.DM_SUSPENDED && awaitingEngage;
}

/// <summary>Pure silent-owner arbitration: DM holds the touchpad with a NON-live status while contacts keep reaching
/// the window and none of them engages. The engage wedge alone is structurally inert in that state (it needs a
/// pending engage) — which is exactly why a stall could run for minutes undetected before this watchdog existed. The
/// predicate deliberately reads stamps that only real DM manipulation and real user attempts move; keying it on a
/// generic "last progress" stamp would be self-defeating, because SetContact and status chatter re-stamp that one on
/// every contact of the stall.</summary>
internal static class DmSilentOwner
{
    /// <summary>How long since DM last manipulated before ownership counts as silent, and how recent a hit-test must
    /// be to prove the user is trying NOW. Do not soften these — they set the worst-case recovery latency.</summary>
    internal const long TimeoutMs = 1500, RecentHitTestMs = 1500;
    /// <summary>A single unserved hit-test is an ordinary two-finger tap DM correctly declines; it must never fire.</summary>
    internal const int MinUnservedHitTests = 2;

    internal static bool IsSilentOwner(bool statusLive, long nowMs, long lastHitTestMs,
        long lastEngagedMs, int unservedHitTests)
        => !statusLive && unservedHitTests >= MinUnservedHitTests
        && nowMs - lastHitTestMs <= RecentHitTestMs      // the user is trying NOW
        && lastHitTestMs > lastEngagedMs                 // and has been since DM last manipulated
        && nowMs - lastEngagedMs > TimeoutMs;
}

/// <summary>The unified recovery escalation: one strike counter fed by every detector (engage wedge, SUSPENDED read,
/// silent owner), so a real episode escalates on its second and third symptom whichever watchdog happens to see them.
/// Pure and time-parameterised for the headless tests.
///
/// <para>Stale strikes decay before the increment: a real episode strikes every 120-1500ms, while isolated tap-wedges
/// an hour apart must never accumulate into a session disable — which a previous never-reset wedge counter did.</para></summary>
internal struct DmRecoveryLadder
{
    internal const int StrikesToDisable = 3;
    internal const long StrikeDecayMs = 10_000;

    private int _strikes;
    private long _lastStrikeMs;

    internal readonly int Strikes => _strikes;

    internal DmRecoveryAction Record(long nowMs)
    {
        if (_strikes > 0 && nowMs - _lastStrikeMs > StrikeDecayMs) _strikes = 0;
        _lastStrikeMs = nowMs;
        _strikes++;
        return _strikes >= StrikesToDisable ? DmRecoveryAction.Disable
             : _strikes == 2 ? DmRecoveryAction.Recycle
             : DmRecoveryAction.Stop;
    }

    /// <summary>A healthy engage clears the record. Called on every RUNNING, which is what keeps isolated wedges from
    /// ever reaching rung 3 across a long session.</summary>
    internal void Reset()
    {
        _strikes = 0;
        _lastStrikeMs = 0;
    }
}

/// <summary>Pure device-evidence arbitration. Only a positively identified physical mouse can preempt live DM.</summary>
internal static class DmWheelArbitration
{
    internal static DmWheelRoute Decide(bool dmLive, DmWheelSourceEvidence source)
        => !dmLive ? DmWheelRoute.ExistingClassifier
         : source == DmWheelSourceEvidence.PhysicalMouse ? DmWheelRoute.StopDmAndPass
         : source == DmWheelSourceEvidence.Touchpad ? DmWheelRoute.DmOwned
         : DmWheelRoute.ExistingClassifier;
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
/// frame-info purpose — the DM-side latency compensation <see cref="Win32DirectManipulation.UpdateFrame"/> feeds).
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
