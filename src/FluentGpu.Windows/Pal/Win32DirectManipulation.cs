using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;   // Point2, KeyModifiers, Diag
using FluentGpu.Pal;          // InputEvent, PointerKind, FrameClock
using FluentGpu.Scroll.Motion;  // ContactRelease
using FluentGpu.Scroll.Runtime;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The DirectManipulation TOUCHPAD CONTACT PRODUCER (scroll rework §4, owner decision: keep DM for touchpad, stripped):
/// the OS's precision-touchpad contact stream is consumed here and re-emitted as <see cref="ScrollInputEvent"/>s —
/// <see cref="ScrollGesture.Begin"/> on RUNNING, one <see cref="ScrollGesture.Sample"/> (DIP delta) per produced frame
/// in which the fingers moved (a frame with no content update emits nothing), <see cref="ScrollGesture.End"/> on the
/// lift carrying DM's release verdict (<see cref="ScrollInputEvent.Release"/>). WHICH callbacks are motion is decided by
/// the pure <see cref="DmContactStream"/>: an Update that ends in a non-RUNNING status carries no motion (DM's READY
/// whole-pixel snap arrives BEFORE the status edge, inside the same Update). DM is NEVER a physics owner: the viewport
/// is configured <c>INTERACTION|TRANSLATION_X|TRANSLATION_Y|TRANSLATION_INERTIA</c> only so that DM's own release
/// decision is visible — RUNNING→INERTIA = released moving, RUNNING→READY = released at rest — and an INERTIA viewport
/// is stopped at the next pump, so the engine's own <c>ScrollHandle.ContactEnd</c> authors the fling from the contact
/// ring. There is no wedge ladder,
/// no silent-owner watchdog, no recovery escalation: the ONE rule is that a claimed contact that has not reached
/// RUNNING within <see cref="EngageTimeoutMs"/> is released back to the OS, which then promotes it to the hi-res
/// <c>WM_POINTERWHEEL</c> stream the wheel path already handles (the fallback bounds the failure mode to "feels like a
/// mouse wheel", never a stuck gesture).
///
/// <para>The stream is COMPOSITION-TIMED (<see cref="ScrollInputEvent.PresentTimed"/>): each event is stamped with the
/// PRESENT time of the first render turn guaranteed to see it — the next tick's present,
/// <see cref="ContactStamp.ForFrame"/> — so a render tick shows the previous tick's sample whether or not this tick's
/// has been written yet (the UI write and the render read of one tick race within a fraction of a millisecond), and the
/// engine's contact plan never predicts on top of DM's own prediction (<c>ContactClock.Present</c>: between samples it
/// interpolates, past the newest it holds). DM's own latency compensation — the frame-info hint
/// (<see cref="DmFrameInfoProviderCcw.CompositionDeltaMs"/>) — is a separate knob: the hint says what DM predicts to,
/// the stamp says when the sample is shown. <see cref="ScrollInputEvent.ArrivalQpc"/> carries when the event was
/// observed, so latency metrics never read the future stamp.</para>
///
/// <para>ONE <c>IDirectManipulationUpdateManager::Update</c> per PRODUCED frame, issued from
/// <c>Win32Platform.PumpScroll</c> AFTER the display-phase gate (its output stamped from the frame's own
/// <see cref="FrameClock"/>), plus an idle MANUALUPDATE drain (<see cref="UpdateIdle"/>) roughly every 250 ms while
/// enabled and not live.</para>
///
/// <para><b>COM discipline (com-interop.md).</b> Event-source only, all UI-thread. The DManip objects are consumed
/// through TerraFX's hand-vtable RCW structs; the one managed object handed back to COM (the viewport event handler
/// sink) is a hand-rolled CCW (function-pointer vtable + interlocked refcount). Every COM object is released on every
/// path (<see cref="Teardown"/>), and the whole file lives inside <c>FluentGpu.Windows</c> so the engine / Controls /
/// VerticalSlice closure stays TerraFX-free.</para>
/// </summary>
internal sealed unsafe class Win32DirectManipulation : IDisposable, IDmContactSink
{
    // ── DIRECTMANIPULATION_STATUS (directmanipulation.h) ──
    internal const int DM_BUILDING = 0, DM_ENABLED = 1, DM_DISABLED = 2, DM_RUNNING = 3, DM_INERTIA = 4, DM_READY = 5,
                       DM_SUSPENDED = 6;

    // Contact producer only: INTERACTION|TRANSLATION_X|TRANSLATION_Y, NO scaling, NO rails. TRANSLATION_INERTIA is on
    // ONLY so DM's release decision shows as the status edge (RUNNING→INERTIA moving, RUNNING→READY at rest); an INERTIA
    // viewport is stopped at the next pump (DmStatusEffects.StopAtNextPump) — the coast is the engine's.
    private const int DM_CFG =
        (int)(DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_INTERACTION
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_X
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_Y
            | DIRECTMANIPULATION_CONFIGURATION.DIRECTMANIPULATION_CONFIGURATION_TRANSLATION_INERTIA);
    /// <summary>The ONE rule (design §4): a claimed contact that has not reached RUNNING within this many ms is
    /// released so the OS falls back to the hi-res wheel stream.</summary>
    internal const long EngageTimeoutMs = 250;
    private const int ViewportFallbackSize = 1000;

    // ── COM (all created + released on the UI thread) ──
    private IDirectManipulationManager* _mgr;
    private IDirectManipulationUpdateManager* _upd;
    private IDirectManipulationViewport* _vp;
    private IDirectManipulationContent* _content;
    private DmViewportEventHandlerCcw* _sink;
    private DmFrameInfoProviderCcw* _frameInfo;
    private uint _cookie;
    private GCHandle _self;
    private bool _coInited;

    private readonly Win32Window _window;
    private readonly HWND _hwnd;
    private float _vpW = ViewportFallbackSize, _vpH = ViewportFallbackSize;

    private bool _enabled;
    private bool _torn;

    // ── gesture state (single latched gesture; all UI-thread) ──
    private readonly DmContactStream _stream;   // which callbacks are motion, Begin/End + the release verdict
    private uint _contactId;
    private Point2 _contactPos;
    private bool _awaitingEngage;
    private long _engageTick;
    private bool _pendingStop;
    private long _pumpQpc;        // the stamp (QPC, plan clock) the current/last Update's output carries — ContactStamp.ForFrame on a produced frame
    private long _pumpAtQpc;      // when (QPC, wall clock) the current/last Update was issued — OnContactEvent's staleness reference
    private int _engageTimeouts;
    private static readonly long StaleStampTicks = Stopwatch.Frequency / 50;

    /// <summary>TickCount64 of the last <c>Update</c> this producer issued (either kind) — the host's idle-drain clock.</summary>
    internal long LastUpdateMs { get; private set; }

    private Win32DirectManipulation(Win32Window window, HWND hwnd)
    {
        _window = window;
        _hwnd = hwnd;
        _stream = new DmContactStream(this);
    }

    /// <summary>Create + wire the producer for <paramref name="hwnd"/>, or null if DirectManipulation is unavailable.</summary>
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

    /// <summary>True while a produced-frame pump is owed: a contact is engaged or pending engagement, a manipulation
    /// is RUNNING, or an INERTIA viewport still has to be stopped (the next pump stops it). Drives
    /// <see cref="IPlatformWindow.ScrollProducerLive"/>.</summary>
    internal bool Live => _enabled && (_awaitingEngage || _stream.Status == DM_RUNNING || _pendingStop);

    private bool SetUp()
    {
        HRESULT hrCo = CoInitializeEx(null, (uint)COINIT.COINIT_APARTMENTTHREADED);
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        if (hrCo == RPC_E_CHANGED_MODE) return false;
        _coInited = hrCo.SUCCEEDED;

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

        _frameInfo = DmFrameInfoProviderCcw.Create();

        Guid iidVp = IID_IDirectManipulationViewport;
        IDirectManipulationViewport* vp = null;
        if (_mgr->CreateViewport((IDirectManipulationFrameInfoProvider*)_frameInfo, _hwnd, &iidVp, (void**)&vp).FAILED || vp == null) return false;
        _vp = vp;

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
        _cookie = cookie;

        Guid iidContent = IID_IDirectManipulationContent;
        IDirectManipulationContent* content = null;
        if (_vp->GetPrimaryContent(&iidContent, (void**)&content).SUCCEEDED && content != null)
            _content = content;

        if (_vp->Enable().FAILED) return false;
        if (_mgr->Activate(_hwnd).FAILED) return false;

        _enabled = true;
        Diag.Set("dm", "enabled", 1);
        return true;
    }

    // ── message pump hooks (UI thread) ──

    /// <summary>Feed one pumped message to DManip BEFORE dispatch. True when DManip consumed it (an owned touchpad
    /// packet) so the caller skips Translate/Dispatch — the wheel path never double-processes a packet DM owns.</summary>
    internal bool ProcessInput(MSG* msg)
    {
        if (!_enabled || _mgr == null) return false;
        BOOL handled = default;
        _mgr->ProcessInput(msg, &handled);
        return (bool)handled;
    }

    /// <summary>THE one <c>Update</c> for a PRODUCED frame (<see cref="IPlatformWindow.PumpScroll"/>). Two numbers, two
    /// jobs:
    /// <list type="bullet">
    /// <item>The frame-info HINT (DM's own latency compensation — what DM evaluates the manipulation for) is this frame's
    /// contact lead <c>clamp(PresentQpc − now, 0, one refresh)</c> in whole milliseconds (0 on an unpaced clock).</item>
    /// <item>The STAMP every event of this Update carries is <see cref="ContactStamp.ForFrame"/> — the NEXT tick's present
    /// (<c>PresentQpc + RefreshQpc</c>), the first render turn guaranteed to see the sample. The render thread reads
    /// <c>PlanSlots</c> for this same tick within a fraction of a millisecond of this write; stamped for this tick, each
    /// frame showed whichever side won (the 2026-09-29 +2/0 sample steps). Stamped one tick on, render tick k shows
    /// sample k−1 either way and interpolates between real samples.</item>
    /// </list>
    /// Also services the one rule: an engage older than <see cref="EngageTimeoutMs"/> is released.</summary>
    internal void UpdateFrame(in FrameClock clock)
    {
        if (!_enabled) return;
        long nowMs = Environment.TickCount64;
        ServiceEngageTimeout(nowMs);
        long nowQpc = Stopwatch.GetTimestamp();
        double refreshMs = clock.RefreshQpc > 0 ? clock.RefreshQpc * 1000.0 / Stopwatch.Frequency : 0.0;
        double leadMs = (clock.Flags & FrameClockFlags.Unpaced) != 0
            ? 0.0
            : Math.Clamp((clock.PresentQpc - nowQpc) * 1000.0 / Stopwatch.Frequency, 0.0, refreshMs);
        PumpOnce(nowQpc, nowMs, leadMs, ContactStamp.ForFrame(in clock, nowQpc));
    }

    /// <summary>The idle MANUALUPDATE drain: a bare <c>Update</c> with lead 0 so a queued content update (the READY
    /// recenter) is never left to sit forever between produced frames. No frame clock ⇒ its output is stamped now (the
    /// ring's <c>PlaceContactTime</c> never lets such a stamp rewind behind the newest sample).</summary>
    internal void UpdateIdle()
    {
        if (!_enabled) return;
        long nowMs = Environment.TickCount64;
        ServiceEngageTimeout(nowMs);
        long nowQpc = Stopwatch.GetTimestamp();
        PumpOnce(nowQpc, nowMs, 0.0, nowQpc);
    }

    /// <summary>One <c>Update</c>: DM is told the whole-millisecond <paramref name="leadMs"/> as its composition hint
    /// (<see cref="DmFrameInfoProviderCcw.CompositionDeltaMs"/> — DM's latency compensation, what it predicts to), and
    /// every event the Update produces is stamped <paramref name="stampQpc"/> (when the sample is SHOWN —
    /// <see cref="ContactStamp.ForFrame"/> on a produced frame, now on the idle drain / the wheel takeover); the two are
    /// deliberately independent. <paramref name="nowQpc"/> is the wall time the Update is issued at — the reference a late
    /// status edge's staleness is measured against (<see cref="IDmContactSink.OnContactEvent"/>), never the future stamp.
    /// The Update's content motion is delivered when it RETURNS, and only if the viewport is still RUNNING
    /// (<see cref="DmContactStream.OnUpdateReturned"/>): DM raises the lift's whole-pixel snap before the status edge.</summary>
    private void PumpOnce(long nowQpc, long nowMs, double leadMs, long stampQpc)
    {
        ulong leadWholeMs = (ulong)Math.Round(Math.Max(0.0, leadMs));
        _pumpQpc = stampQpc;
        _pumpAtQpc = nowQpc;
        if (_pendingStop) { _pendingStop = false; if (_vp != null) _vp->Stop(); }   // never inside the COM sink callback
        if (_frameInfo != null) _frameInfo->CompositionDeltaMs = leadWholeMs;
        if (_upd != null) _upd->Update((IDirectManipulationFrameInfoProvider*)_frameInfo);
        _stream.OnUpdateReturned(_window.ScaleInternal);
        LastUpdateMs = nowMs;
    }

    /// <summary>The ONE rule: no RUNNING within <see cref="EngageTimeoutMs"/> of a claimed contact ⇒ release it (the OS
    /// then delivers the gesture as hi-res wheel packets, which the wheel path handles).</summary>
    private void ServiceEngageTimeout(long nowMs)
    {
        if (!_awaitingEngage || nowMs - _engageTick <= EngageTimeoutMs) return;
        _awaitingEngage = false;
        if (_vp != null) { _vp->Stop(); _vp->ReleaseAllContacts(); }
        Diag.Set("dm", "engageTimeouts", ++_engageTimeouts);
    }

    /// <summary>DM_POINTERHITTEST → claim this contact (the caller gates to PT_TOUCHPAD). True iff SetContact succeeded
    /// (the caller then consumes the message).</summary>
    internal bool SetContact(uint pointerId, Point2 contactDip)
    {
        if (!_enabled || _vp == null) return false;
        if (_vp->SetContact(pointerId).FAILED) return false;
        if (_stream.Status != DM_RUNNING)
        {
            _contactId = pointerId;
            _contactPos = contactDip;
        }
        _awaitingEngage = true;
        _engageTick = Environment.TickCount64;
        return true;
    }

    // ── the CCW sink callbacks (UI thread) ──

    internal void HandleStatusChanged(int current, int previous)
    {
        Diag.Set("dm", "status", current);
        if (current == DM_RUNNING) _awaitingEngage = false;
        // Begin / End (+ DM's release verdict: INERTIA = moving, READY = at rest) — DmContactStream decides.
        DmStatusEffects effects = _stream.OnStatus(current);
        if ((effects & DmStatusEffects.StopAtNextPump) != 0) _pendingStop = true;   // the coast is the engine's
        if ((effects & DmStatusEffects.ResetViewport) != 0) ResetViewport();
    }

    /// <summary>A positively identified physical mouse takes over a live touchpad manipulation at once. What this drain
    /// emits is stamped now (lead 0, no frame clock) — usually behind the newest sample's next-present stamp, where the
    /// engine places it AT that sample (<c>PlaceContactTime</c>), so the takeover never rewinds what was shown.</summary>
    internal bool TryStopForPhysicalWheel()
    {
        if (!Live || _vp == null) return false;
        _vp->Stop();
        long nowQpc = Stopwatch.GetTimestamp();
        PumpOnce(nowQpc, Environment.TickCount64, 0.0, nowQpc);
        return true;
    }

    internal void HandleContentUpdated(IDirectManipulationContent* content)
    {
        if (content == null) return;
        float* m = stackalloc float[6];
        if (content->GetContentTransform(m, 6).FAILED) return;
        _stream.OnContent(m[0], m[4], m[5]);   // buffered: delivered when the Update returns, iff still RUNNING
    }

    /// <summary>Emits one contact event (<see cref="IDmContactSink"/>, decided by <see cref="DmContactStream"/>) stamped
    /// with the stamp of the Update that produced it (<see cref="ContactStamp.ForFrame"/> — the next tick's present — on a
    /// produced frame). A status edge raised inside <c>ProcessInput</c> more than <see cref="StaleStampTicks"/> after the
    /// last pump was ISSUED (measured against the pump's wall time, never its future stamp) is stamped now — the engine
    /// never lets such a stamp rewind the contact behind a sample already stamped for a later present. Every event
    /// carries <see cref="ScrollInputEvent.ArrivalQpc"/> = now, the instant it was observed, for the latency metrics.</summary>
    void IDmContactSink.OnContactEvent(ScrollGesture phase, float dipX, float dipY, ContactRelease release)
    {
        long now = Stopwatch.GetTimestamp();
        long qpc = _pumpQpc;
        if (_pumpAtQpc == 0 || now - _pumpAtQpc > StaleStampTicks) qpc = now;   // a status edge inside ProcessInput: stamp now, not the stale pump
        var e = new ScrollInputEvent(ScrollSource.Touchpad, phase, qpc, _contactPos, dipX, dipY, _contactId, KeyModifiers.None)
            { PresentTimed = true, Release = release, ArrivalQpc = now };
        _window.EnqueueExternal(InputEvent.ForScroll(in e, PointerKind.Touchpad, unchecked((uint)Environment.TickCount64)));
    }

    private void ResetViewport()
    {
        if (_vp == null) return;
        if (_content != null)
        {
            float* m = stackalloc float[6];
            if (_content->GetContentTransform(m, 6).SUCCEEDED
                && MathF.Abs(m[0] - 1f) <= 1e-4f && MathF.Abs(m[4]) <= 0.5f && MathF.Abs(m[5]) <= 0.5f)
                return;   // already identity
        }
        _vp->ZoomToRect(0f, 0f, _vpW, _vpH, false);
    }

    // ── teardown / dispose (release on every path) ──

    private void Teardown()
    {
        if (_torn) return;
        _torn = true;
        _enabled = false;
        if (_vp != null)
        {
            _vp->Stop();
            if (_cookie != 0) { _vp->RemoveEventHandler(_cookie); _cookie = 0; }
            _vp->Disable();
            _vp->Abandon();
        }
        if (_mgr != null && _hwnd != HWND.NULL) _mgr->Deactivate(_hwnd);
        if (_content != null) { _content->Release(); _content = null; }
        if (_vp != null) { _vp->Release(); _vp = null; }
        if (_upd != null) { _upd->Release(); _upd = null; }
        if (_mgr != null) { _mgr->Release(); _mgr = null; }
        if (_sink != null) { DmViewportEventHandlerCcw.Destroy(_sink); _sink = null; }
        if (_frameInfo != null) { DmFrameInfoProviderCcw.Destroy(_frameInfo); _frameInfo = null; }
        if (_self.IsAllocated) _self.Free();
    }

    public void Dispose()
    {
        Teardown();
        if (_coInited) { CoUninitialize(); _coInited = false; }
    }

    // ── CLSID / IIDs (directmanipulation.h 10.0.26100.0) ──
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

/// <summary>Device evidence behind a <c>WM_POINTERWHEEL</c> packet (<c>GetPointerInfo</c>/<c>GetPointerDevice</c>).</summary>
internal enum DmWheelSourceEvidence : byte
{
    Unknown,
    PhysicalMouse,
    Touchpad,
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
