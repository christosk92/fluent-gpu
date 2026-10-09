using System;
using System.Threading;
using System.Diagnostics;
using FluentGpu.Pal;

namespace FluentGpu.Hosting.Threading;

/// <summary>
/// Single-consumer scene-adopt → record → submit → present loop, independent of UI reactive/layout work.
/// <para><b>ONE present per compositor tick.</b> While render-side motion is live (compositor rows, image crossfades)
/// the loop's turn is the display-clock tick and <see cref="PresentCadence.Decide"/> is the sole presenter: a fresh
/// publication wins; a motion re-present of the retained scene happens only when nothing fresh exists at the tick; a
/// publication that lands after this tick's decision waits for the next tick (DropOldest hands the newest one then).
/// With no motion live the tick rule does not apply — a publish wake presents immediately (idle responsiveness), the
/// present-slot credit alone throttles. Every present holds the credit (<see cref="_takePresentSlot"/>), taken before
/// the frame is chosen so the presented state is the freshest that existed when the slot opened.</para>
/// <para><b>Never queue behind a late frame.</b> A clock-paced turn takes the credit with a short grace
/// (<see cref="SlotCatchUp.GraceMs"/>); a slot still busy after it means the previous present missed its vblank and owns
/// this one. While frames fit the early phase the turn presents nothing and the next tick presents on time
/// (<see cref="SlotCatchUp"/>, <see cref="CatchUpSkips"/>); over-budget frames keep the unbounded wait.</para>
/// <para><b>Pacing is per target.</b> The tick rule, the credit and the motion run above are the PARENT swapchain's and
/// follow the parent's OWN motion; a detached child's presents ride <c>extraDrain</c> on the child's own swapchain, BEFORE the parent's slot wait, probe
/// that swapchain's own slot without waiting, and keep their own evidence (<see cref="ChildPresentPace"/>).</para>
/// Resize/recovery/shutdown take priority over render turns. <see cref="DrainSync"/> is an explicit request/ack
/// rendezvous for deterministic hosts, not the production async path.
/// </summary>
public sealed class RenderThread : IDisposable
{
    /// <summary>The recursive recorder needs the same stack reserve as the UI recording path.</summary>
    public const int RecordingStackBytes = 32 * 1024 * 1024;
    private readonly Thread _thread;
    private readonly SceneFramePublisher _publisher;
    private readonly Action<RenderFrame> _submitPresent;   // runs ON this thread: (suppress vsync?) → SubmitDrawList(arena) → Present
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _done = new(false);
    // Step 2 (async resize rendezvous): a non-destructive park/resume handshake. The UI parks this loop (mutual exclusion)
    // before it mutates the swapchain/back-buffers/fence in Resize, then resumes it. _resizeIdle = loop → UI "I am parked,
    // no ComPtr touch in flight"; _resumeResize = UI → loop "Resize done, proceed". The AutoResetEvent Set/WaitOne pair is
    // a full memory barrier, publishing the UI's advanced fence/back-buffer/frame-index writes to the loop before it un-parks.
    private readonly AutoResetEvent _resizeIdle = new(false);
    private readonly AutoResetEvent _resumeResize = new(false);
    private int _resizeQuiesce;
    // A park request, as a waitable. Quiesce signals it beside _wake.Set() and the quiesce gate resets it once the UI
    // resumed it. Its raw handle is handed to the device (IGpuDevice.SetSubmitAbortHandle) so a present-slot wait this loop is
    // blocked in (up to the 1 s liveness bound) ends the moment the UI asks to park, instead of the UI waiting the whole wait
    // out. Manual-reset: the request must stay visible to every wait of the turn, not only the first one to see it.
    private readonly ManualResetEvent _parkRequested = new(false);
    private readonly Action<nint>? _abortHandleSink;   // tells the device the handle on creation and clears it before the event is disposed
    // UI-side evidence of the rendezvous cost (Quiesce runs on the UI thread, so these are UI-written; the readers are torn-free).
    private long _quiesceWaitMaxQpc, _quiesceCount, _quiesceLogQpc, _quiesceLogSuppressed;
    // Step 4 (async device-lost recovery): the UI observes a lost device, sets RecoverRequest + wakes this loop; the loop
    // rebuilds the device here (render-confined) and signals RecoverDone + nudges the UI. Null ⇒ no recovery wired.
    private readonly DeviceLostCoordinator? _deviceLost;
    private readonly Action? _recover;      // runs ON this thread: _device.RecoverDevice() under AssertRender
    private readonly Action? _windowWake;   // thread-safe UI wake (PostMessage WM_NULL) to nudge the UI out of its clean block
    // Detached-window routing: after draining the parent host's OWN seam each turn, drain any registered CHILD host seams
    // (pop-out video windows) on THIS render thread, so a second AppHost's swapchain presents through the ONE render thread
    // that owns the shared device's submit/present (never a second render thread → no undetected _cmdList/_queue/_fence race).
    // Null on a host with no children (or a child host, which has no render thread of its own). Runs regardless of whether
    // the parent published this turn — a child wake carries no parent publish, so the parent-seam TryAcquire may no-op.
    private readonly Action? _extraDrain;
    // Runs ON this thread at the top of every turn that got past the resize / device-lost gates, BEFORE the primary's present
    // decision and its (possibly long) present-slot wait, also on a bare wake (a quiesce nudge, a child's wake, a video handle
    // arriving). The host's early, structural video drain for the parent and every child (F208): a surface is created and bound
    // without waiting behind the slot. It also applies a parked video-only post (F098: a UI wake that was only its video pump parks
    // the registry's snapshot in the publisher and wakes this loop with WakeForVideo, publishing no frame): such a turn takes no
    // slot, records nothing and presents nothing. Null => nothing early to do.
    private readonly Action? _preTurn;
    // After the parent's present decision, every turn: the host's one composition commit for the turn (AppHost.CommitVideoTurn,
    // F080), now that the children (extraDrain, before the decision) and the parent (inside it) have applied their placements.
    private readonly Action? _postTurn;
    /// <summary>Test-only: which optional turn callbacks the owner wired (the children's drain before the present decision, the
    /// post-present commit) — a host that drops one of them is what these say.</summary>
    internal bool HasExtraDrainForTest => _extraDrain is not null;
    internal bool HasPostTurnForTest => _postTurn is not null;
    private readonly Func<bool>? _needsTick;   // render-side motion live, on this host OR a detached child? (AppHost.HasRenderMotion) — wakes the loop at the display clock
    // Render-side motion live on THIS host alone (AppHost.HasOwnRenderMotion). Only this makes the turn a parent present: the
    // tick rule, the primary present credit, the motion run and the tick-spent mark are the parent swapchain's; a child's motion
    // (needsTick true, this false) only keeps the loop at display rate while extraDrain presents the child on ITS own swapchain
    // (F094). Null ⇒ every motion is the parent's own (tests / hosts without children).
    private readonly Func<bool>? _ownMotion;
    // The [render.pace] line's child= section: BEGIN opens every child's window with the line's, REPORT formats them (null/empty ⇒ none).
    private readonly Action? _childPaceBegin;
    private readonly Func<string?>? _childPaceReport;
    private readonly Action? _tick;            // motion re-present of the retained scene (AppHost.RenderMotion)
    // Whether the motion turn just run reached the swapchain (AppHost.MotionTurnPresented). False = the host found every pose
    // unchanged and elided the record AND the present (or motion ended in the slot wait): nothing reached the glass and the
    // credit the turn took is still held. Null => every motion turn presents (tests / hosts without elision).
    private readonly Func<bool>? _tickPresented;
    // The refresh period (QPC ticks): the motion fallback period when no display clock exists, and the tick backstop.
    private readonly Func<long>? _tickPeriod;
    // Present-slot pacing (IGpuDevice.TryTakePresentSlot): take the swapchain's present-queue credit BEFORE choosing
    // which published frame to present, waiting at most the argument in ms (−1 = the backend's liveness bound; false ⇒
    // the slot did not open in time and nothing was taken). Null ⇒ backend without a latency waitable (headless) or not
    // wired — every take then succeeds at once.
    private readonly Func<int, bool>? _takePresentSlot;
    // The catch-up policy a paced turn consults when its bounded slot take times out (render-thread private, pure value).
    private SlotCatchUp _catchUp;
    // Paced turns that presented nothing because the slot was still busy past the grace and the policy chose to skip
    // (cumulative; render thread writes, UI reads), and its value at the [render.pace] window start.
    private long _catchUpSkips, _paceCatchUp0;
    // The display tick the last catch-up skip gave to the late frame. A wake that re-runs a turn on that same tick (a UI
    // publication landing mid-tick) skips it again without touching the slot or the policy — see PresentTurn.
    private long _catchUpTickSeq;
    /// <summary>Clock-paced turns whose present slot was still busy past the grace (the previous present missed its vblank
    /// and owns this one) and that presented nothing so the next tick presents on time (CUMULATIVE; <see cref="SlotCatchUp"/>).
    /// Each is the one vblank the late frame already owned — the next present's <see cref="MissedMotionTicks"/> charge
    /// (probe Turn row <c>missed=1</c>) is that same tick, never an extra one. 0 while no frame is late, and while frames do
    /// not fit the early phase (they keep the wait). A wake re-running the skipped tick is not counted again.</summary>
    public long CatchUpSkips => Volatile.Read(ref _catchUpSkips);
    // The display clock's TickSeq of the last present (PresentCadenceInput.LastPresentedTickSeq). 0 = none yet, AND the
    // sentinel IRenderDisplayClock.TickSeq returns when it cannot say — PresentCadence never gates on 0.
    private long _lastPresentedTickSeq;
    // The display tick whose motion turn was ELIDED (presented nothing). A fresh publication landing later in that tick presents
    // at once instead of a tick later; a bare re-wake on it skips (it would only elide again). 0 = none.
    private long _elidedTickSeq;
    private bool _lastPresentWasMotion;
    private long _raceTickSeq;   // the tick a race hit was last charged to (one per tick)
    // Always-on pacing evidence (render thread writes, UI reads cumulative totals): which presents were fresh
    // publications vs motion re-presents, how often each waited > 4 ms for its present slot, turns that presented
    // nothing because this tick was already spent, and RACE HITS — a fresh publication deferred to the next tick
    // because a motion re-present had already taken this one (the stale frame reached the glass first).
    private long _freshPresents, _motionPresents, _raceHits, _freshLongWaits, _motionLongWaits, _skippedTicks;
    public long FreshPresents => Volatile.Read(ref _freshPresents);
    public long MotionPresents => Volatile.Read(ref _motionPresents);
    public long RaceHits => Volatile.Read(ref _raceHits);
    public long FreshLongWaits => Volatile.Read(ref _freshLongWaits);
    public long MotionLongWaits => Volatile.Read(ref _motionLongWaits);
    private long _missedMotionTicks;
    // The run the missed-tick counter differences over (MotionTickRun): kept apart from _lastPresentedTickSeq, which is
    // the "this tick is already spent" pacing gate and must not be reset when motion pauses.
    private MotionTickRun _motionRun;
    /// <summary>Compositor ticks that passed with motion live but no present for them — the ticks between two paced
    /// presents of one live-motion run beyond the one each present spends (CUMULATIVE; <see cref="MotionTickRun"/>). A
    /// present-slot wait that crossed a vblank costs exactly the vblanks it crossed; idle stretches (render motion not
    /// live) are never charged. 0 while every tick is presented. The direct measure of the pacing cliff; each present's
    /// share, with its wake lag, slot wait and work, is the probe's Turn row (<c>ScrollProbe.Turn</c>).</summary>
    public long MissedMotionTicks => Volatile.Read(ref _missedMotionTicks);
    /// <summary>Paced turns with work pending (a fresh publication or motion due) that presented nothing because this
    /// compositor tick had already been presented for — the work waits for the next tick.</summary>
    public long SkippedTicks => Volatile.Read(ref _skippedTicks);
    // [render.pace] 1 Hz window (render-thread private): slot-wait sum/max/count and the worst tick→present lag since
    // the window opened, plus the counter values at the window start. Nothing here allocates; the line does, once a second.
    private long _paceWindowStartQpc, _paceWindowTickSeq, _paceFresh0, _paceMotion0, _paceSkipped0, _paceMissed0, _paceRace0;
    private long _slotWaitSumQpc, _slotWaitMaxQpc, _slotWaitCount, _presentLagMaxQpc;
    // F215: the detached children's present drain (extraDrain, which runs BEFORE the primary's present decision and so is in no worst
    // present's work= - but IS in its lag=): sum / max / count of its wall time per turn in the window. A pop-out that submits, spins
    // its Stage-B motion fence (up to the device's MotionFenceCapMs, 16 ms) or waits its back-buffer fence does so between the parent's
    // wake and the parent's slot take, pushing the parent's present later in the SAME tick, and the line must be able to say so.
    private long _childDrainSumQpc, _childDrainMaxQpc, _childDrainCount;
    // Cumulative slot-wait totals for the UI-readable pace snapshot (render thread writes, UI reads — torn-free longs).
    private long _slotWaitTotalCount, _slotWaitTotalQpc, _slotWaitTotalMaxQpc;
    private long _paceIgnored0, _paceSlotDrops0, _paceSlotTimeouts0, _paceChildTimeouts0;
    // The window's WORST present (the one that set _presentLagMaxQpc), split: wake lag (tick → turn start), slot wait, work
    // (slot open → done), the render thread's own running time across the turn (ThreadCycles at a running-max rate), its
    // tick and when it completed — so a [render.pace] line says whether the worst present was a thread that did not wake,
    // a slot that did not open, or work that took long, and whether the thread was even running (F(i), 2026-09-25).
    private long _worstWakeQpc, _worstSlotQpc, _worstWorkQpc, _worstTick, _worstDoneQpc;
    private float _worstRunMs = float.NaN;
    // F244: where that worst present's work went (the host's stamps between the phases of its submit/present turn), and the
    // host's side of it - read right after the turn that set a new worst, never otherwise. Null ⇒ no split (tests / headless).
    private readonly Func<PresentSplit>? _presentSplit;
    private PresentSplit _worstSplit;
    private ulong _turnStartCycles;
    private double _renderCyclesPerMs;
    // The frame ledger's view of the turn (FrameLedger): what the present decision did, when the slot opened and the present
    // returned, the publication it presented and the ticks it skipped. Plain render-thread stores every turn (the decision's own
    // stamps, already taken); the counter reads the hand-off needs (the wait-start QPC, the allocation counter) run only while the
    // ledger is on, and only then is the hand-off called.
    private LedgerTurnKind _turnKind;
    private long _turnSlotOpenQpc, _turnDoneQpc, _turnLedgerTickSeq, _turnLedgerTickQpc, _turnStartAlloc;
    private ulong _turnPublishSeq;
    private int _turnMissed;
    /// <summary>The owning host's frame-ledger hand-off (AppHost.Ledger.cs), called at the end of every turn while
    /// <see cref="FrameLedger.Enabled"/>. Set once, right after construction (the first turn needs a publish or a wake).</summary>
    internal LedgerTurnSink? LedgerSink { get; set; }
    // The host's side of the [render.pace] line (governor, present-queue depth, GPU execution) — read once a second.
    private readonly Func<RenderPaceHostState>? _paceHost;
    private long _requestedDrains, _completedDrains;
    private readonly IRenderDisplayClock? _displayClock;
    private long _turnTickQpc;
    /// <summary>QPC stamp of the compositor tick THIS turn presents for (0 without a display clock, or on an unpaced
    /// turn) — captured when the turn decides, never re-read after the present-slot wait, so the present-time prediction
    /// the scroll poser and animations evaluate at is the one for the vblank this present was decided for.
    /// Render-thread read only.</summary>
    public long DisplayTickQpc => _turnTickQpc;
    /// <summary>QPC stamp of the display clock's CURRENT tick (0 without a clock) — the pair of <see cref="TickSeq"/>, unlike
    /// <see cref="DisplayTickQpc"/> (the parent's last paced turn). Render-thread read only.</summary>
    internal long DisplayClockTickQpc => _displayClock?.TickQpc ?? 0;

    /// <summary>Cumulative present-slot waits: count, total and maximum (QPC ticks). UI-readable (torn-free longs).</summary>
    public long SlotWaitCount => Volatile.Read(ref _slotWaitTotalCount);
    /// <inheritdoc cref="SlotWaitCount"/>
    public long SlotWaitTotalQpc => Volatile.Read(ref _slotWaitTotalQpc);
    /// <inheritdoc cref="SlotWaitCount"/>
    public long SlotWaitMaxQpc => Volatile.Read(ref _slotWaitTotalMaxQpc);
    /// <summary>The display clock's delivered-tick sequence (0 without a clock).</summary>
    public long TickSeq => _displayClock?.TickSeq ?? 0;

    /// <summary>Whether this thread's display clock exists and is delivering (the capability, not whether a tick is fresh).
    /// Safe from any thread: a detached child's UI loop asks it to pick a tick-paced wait.</summary>
    internal bool DisplayClockAvailable => _displayClock?.IsAvailable == true;

    // A tick older than this is not the clock's CURRENT beat: the clock is only armed while some host asks for ticks, so a seq
    // that stopped moving describes a vblank from before it was parked, not a frame slot a child may still owe. Two refreshes
    // at a 30 Hz panel: far above any live tick interval, far below an idle stretch.
    private static readonly long s_tickMaxAgeQpc = Stopwatch.Frequency / 15;

    /// <summary>The display clock's CURRENT tick, sampled for a detached child's UI loop (its production gate and frame
    /// clock): the child has no compositor clock of its own and rides this one. Reads the clock's volatile seq FIRST and its
    /// stamp second (the clock writes the stamp before the seq), so a new seq is never paired with an older stamp. False
    /// - and the child then paces exactly as it did without a clock - when there is no clock, it is unavailable, it cannot
    /// say (<c>TickSeq</c>/<c>TickQpc</c> 0), or the tick is older than <see cref="s_tickMaxAgeQpc"/>. Any thread (the
    /// Win32 subscription reads two volatile longs); no allocation.</summary>
    internal bool TryGetDisplayTick(long nowQpc, out long tickSeq, out long tickQpc)
    {
        tickSeq = 0;
        tickQpc = 0;
        var clock = _displayClock;
        if (clock is null || !clock.IsAvailable) return false;
        long seq = clock.TickSeq;
        long qpc = clock.TickQpc;
        if (seq == 0 || qpc == 0 || nowQpc - qpc > s_tickMaxAgeQpc) return false;
        tickSeq = seq;
        tickQpc = qpc;
        return true;
    }
    private readonly WaitHandle[]? _displayWaits;
    // Wall-clock housekeeping (TrimIdleResources & co): called between turns with Environment.TickCount64, returns the ms until
    // it wants to run again (-1 = never). A clean-idle wait is bounded by it so an app that stops presenting still trims.
    private readonly Func<long, int>? _idleTrim;
    private int _idleTrimWaitMs = -1;
    private volatile bool _running = true;
    private ulong _presentAck;

    public RenderThread(SceneFramePublisher publisher, Action<RenderFrame> submitPresent, bool async = false,
                        DeviceLostCoordinator? deviceLost = null, Action? recover = null, Action? windowWake = null,
                        Action? extraDrain = null, Func<bool>? needsTick = null, Action? tick = null,
                        Func<long>? tickPeriod = null, IRenderDisplayClock? displayClock = null,
                        Func<int, bool>? takePresentSlot = null, Func<RenderPaceHostState>? paceHost = null,
                        Action<nint>? submitAbortHandleSink = null, Func<bool>? ownMotion = null,
                        Action? childPaceBegin = null, Func<string?>? childPaceReport = null,
                        Func<PresentSplit>? presentSplit = null, Action? preTurn = null, Action? postTurn = null,
                        Func<long, int>? idleTrim = null, Func<bool>? tickPresented = null)
    {
        _idleTrim = idleTrim;
        _idleTrimWaitMs = idleTrim is null ? -1 : 0;   // due at once: the first clean-idle wait times out into the first pass
        _presentSplit = presentSplit;
        _preTurn = preTurn;
        _postTurn = postTurn;
        _paceHost = paceHost;
        _ownMotion = ownMotion;
        _childPaceBegin = childPaceBegin;
        _childPaceReport = childPaceReport;
        _publisher = publisher;
        _submitPresent = submitPresent;
        _deviceLost = deviceLost;
        _recover = recover;
        _windowWake = windowWake;
        _extraDrain = extraDrain;
        _needsTick = needsTick;
        _tick = tick;
        _tickPresented = tickPresented;
        _tickPeriod = tickPeriod;
        _takePresentSlot = takePresentSlot;
        _displayClock = displayClock;
        if (displayClock is not null) _displayWaits = [_wake, displayClock.Tick];
        _abortHandleSink = submitAbortHandleSink;
        submitAbortHandleSink?.Invoke(ParkRequestedHandle);   // BEFORE the loop starts: the device never sees a handle it can race
        // F102: the one thread that records, composites, submits and presents for every window, so its turns are deadline
        // work: AboveNormal for its whole life, matching the vblank waiter (Win32CompositorClock) it is woken by and the audio
        // threads. Deliberately plain priority, NOT MMCSS: a registered class on a loop that can spin through a motion burst
        // would starve the UI thread and the media threads that share the cores.
        _thread = new Thread(Loop, RecordingStackBytes) { Name = "fgpu-render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The park-request event's raw handle (a manual-reset Win32 event owned by this thread, closed in
    /// <see cref="Dispose"/> after the device was told to forget it). What <c>IGpuDevice.SetSubmitAbortHandle</c> receives.</summary>
    internal nint ParkRequestedHandle => _parkRequested.SafeWaitHandle.DangerousGetHandle();

    /// <summary>The park-request event as a wait handle: signaled from <see cref="Quiesce"/> until the parked loop is
    /// resumed. A blocking present-slot wait that also waits on it never holds the UI for its full bound (the test seam).</summary>
    internal WaitHandle ParkRequested => _parkRequested;

    /// <summary>Longest UI-thread wait inside <see cref="Quiesce"/> so far (ms): how long the UI was blocked until this loop
    /// reached its quiesce gate. A healthy host stays under a refresh; the interruptible slot wait is what keeps a 1 s present
    /// slot wait out of it. Surfaced as <c>FrameStats.QuiesceWaitMsMax</c>.</summary>
    public double QuiesceWaitMsMax => Volatile.Read(ref _quiesceWaitMaxQpc) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Park requests served so far (cumulative).</summary>
    public long QuiesceCount => Volatile.Read(ref _quiesceCount);

    /// <summary>The publish-seq of the last frame this thread presented (acquire read) — the "how far behind is render"
    /// diagnostic the UI reads; never a pacer.</summary>
    public ulong PresentAck => Volatile.Read(ref _presentAck);

    private long PeriodQpc() => Math.Max(1, _tickPeriod?.Invoke() ?? Stopwatch.Frequency / 60);

    /// <summary>The bound (ms) of an UNPACED turn's liveness take: <c>max(2 x refresh, 34 ms)</c> (F208). It replaces the plain
    /// 1 s liveness wait: a slot that never opens costs two refreshes, then the turn proceeds with the credit held, exactly as
    /// the 1 s wait did. Chromium's DComp pacing wait is bounded the same way.</summary>
    public static int UnpacedSlotBoundMs(double refreshMs) => (int)Math.Max(34.0, Math.Ceiling(refreshMs * 2.0));

    // The takePresentSlot argument of an unpaced turn: a NEGATIVE value is the liveness-bounded take (it proceeds, credit held,
    // even if the slot never opened), here with a bound of UnpacedSlotBoundMs (-1 alone would be the backend's 1 s default).
    private static int UnpacedLivenessTake(double refreshMs) => -UnpacedSlotBoundMs(refreshMs);

    /// <summary>Backstop for a tick-paced wait: two refresh periods, clamped 8–34 ms (the UI's TickBackstopMs rule). The
    /// tick normally ends the wait; this fires only when the compositor stalls or a clock capability loss signalled the
    /// handle once. Never a pacer in itself.</summary>
    private int BackstopMs()
    {
        int ms = (int)Math.Round(PeriodQpc() * 2000.0 / Stopwatch.Frequency);
        return ms < 8 ? 8 : ms > 34 ? 34 : ms;
    }

    private long _idleTrimDueMs;
    private bool _idleTrimStarted;
    private int _idleTrimForce;

    /// <summary>Render thread: run the idle trim after THIS turn even though its timed wait is not yet due (a hidden-window release
    /// that still waits on a fence asks for its own follow-up pass instead of waiting out a longer timer already armed).</summary>
    internal void RequestIdleTrimThisTurn() => Volatile.Write(ref _idleTrimForce, 1);
    private int RunIdleTrim()
    {
        _idleTrimStarted = true;
        long now = Environment.TickCount64;
        int next;
        try { next = _idleTrim!(now); }
        catch (Exception ex) { Console.Error.WriteLine($"[render] idle trim THREW: {ex}"); next = 5000; }   // housekeeping must never kill the loop
        if (next >= 0) { if (next < 1) next = 1; _idleTrimDueMs = now + next; }
        return next;
    }

    private void Loop()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Render);   // this thread is the SOLE ComPtr owner for submit/present
        while (true)
        {
            long waitStart = FrameLedger.Enabled ? Stopwatch.GetTimestamp() : 0;   // the ledger's turn record only
            bool motionDue = _needsTick?.Invoke() == true;
            if (!motionDue) { _motionRun.Break(); _catchUp.Break(); }   // idle is not a missed tick: the next paced present starts a new run
            _displayClock?.SetActive(motionDue);
            // A publication still pending with no motion live is unpaced work (a skip above left it when motion ended
            // in the same turn): present it now instead of blocking on a wake that already happened.
            if (!(!motionDue && _publisher.HasPendingFrame))
            {
                if (motionDue && _displayClock?.IsAvailable == true)
                    WaitHandle.WaitAny(_displayWaits!, BackstopMs());   // the tick is the turn (or a wake / the backstop)
                else if (motionDue)
                    _wake.WaitOne((int)Math.Max(1, Math.Round(PeriodQpc() * 1000.0 / Stopwatch.Frequency)));   // no display clock: refresh-derived fallback
                else if (_idleTrimWaitMs >= 0)
                {
                    // Clean idle with housekeeping due: a timed-out wait runs it (no turn: nothing woke us) and waits again.
                    if (!_wake.WaitOne((int)Math.Max(1, _idleTrimDueMs - Environment.TickCount64)))
                    {
                        _idleTrimWaitMs = RunIdleTrim();
                        continue;
                    }
                }
                else
                    _wake.WaitOne();   // clean idle: block without releasing the retained scene
            }
            long turnStart = Stopwatch.GetTimestamp();
            _turnStartCycles = FluentGpu.Foundation.ThreadCycles.Read();
            if (FrameLedger.Enabled) _turnStartAlloc = GC.GetAllocatedBytesForCurrentThread();
            _turnKind = LedgerTurnKind.Bare;
            _turnSlotOpenQpc = _turnDoneQpc = _turnLedgerTickSeq = _turnLedgerTickQpc = 0;
            _turnPublishSeq = 0;
            _turnMissed = 0;
            long requestedDrain = Volatile.Read(ref _requestedDrains);
            if (!_running) break;
            // Step 4: device-lost recovery takes priority. The UI observed a lost device and is BLOCKING (not publishing)
            // until RecoverDone. Rebuild the device here (render-confined — this thread is the sole ComPtr owner), mark
            // done, and nudge the UI out of its clean block. No resize/present can be pending (the UI blocks both).
            if (_deviceLost is { } dl && dl.RecoverRequest != 0 && dl.RecoverDone == 0)
            {
                FluentGpu.Foundation.Diag.Line("[dl] render: recover gate — invoking RecoverDevice");
                try { _recover?.Invoke(); }
                catch (Exception ex) { Console.Error.WriteLine($"[dl] render: RecoverDevice THREW: {ex}"); }
                dl.RecoverDone = 1;   // set even on failure so the UI unblocks (it re-detects if still lost) — never hang
                _windowWake?.Invoke();
                FluentGpu.Foundation.Diag.Line("[dl] render: RecoverDone set + UI nudged");
                continue;
            }
            // Step 2: a resize is pending. Park HERE (before any TryAcquire/submit/present ComPtr touch), tell the UI the
            // loop is idle, and block until the UI finishes the fenced swapchain Resize + calls Resume. Then re-loop and
            // wait for the next real wake (the post-resize full-relayout republish).
            if (Volatile.Read(ref _resizeQuiesce) != 0)
            {
                _displayClock?.SetActive(false);
                // Consume this request before acknowledging it. The UI may Resume then immediately
                // request another park; clearing after the resume wait would erase that newer request.
                Volatile.Write(ref _resizeQuiesce, 0);
                _resizeIdle.Set();
                _resumeResize.WaitOne();
                // Clear the park request only AFTER the resume: Quiesce raises the flag before it signals the event, so this
                // gate can consume the flag before that Set lands, and a reset before the resume could leave the event
                // signaled with no park pending (every later slot wait would end at once as "aborted"). The Set happens
                // before the UI's Resume, so this reset always follows it. A request made after the resume may be cleared
                // too, but its flag survives and the gate is reached again with no slot wait in between.
                _parkRequested.Reset();
                continue;
            }
            // Before the present decision: the slot wait inside it may take far longer than any structural video work (F208), and
            // the bare-wake early-out inside it must not skip this.
            _preTurn?.Invoke();
            // Detached child hosts FIRST (F241): present any freshly-published child frame on ITS own swapchain, on this same
            // render thread, BEFORE the parent's present decision. That decision is the one place a turn blocks — the
            // primary's present-slot wait (up to a vblank while paced, the liveness bound when the queue is full) — and a
            // pop-out's present must never queue behind it: the main window's autonomous frames (a stepper, a loop) would
            // otherwise push every pop-out present towards the next vblank. The drain itself never blocks (the child's slot is
            // probed, not waited for), so the parent's present is delayed only by the child's own record + submit, which the
            // pace line reports as childDrain. Runs every turn (a child's wake may carry no parent publish, so it must not hang
            // off the parent TryAcquire); a cheap no-op when no child has published since its last present (dedup in TryAcquire).
            if (_extraDrain is { } extraDrain)
            {
                long drainStart = Stopwatch.GetTimestamp();
                extraDrain();
                long drainQpc = Stopwatch.GetTimestamp() - drainStart;
                _childDrainSumQpc += drainQpc;
                _childDrainCount++;
                if (drainQpc > _childDrainMaxQpc) _childDrainMaxQpc = drainQpc;
            }
            bool motionLive = PresentTurn(turnStart);
            _postTurn?.Invoke();   // the turn's one composition commit: the children's and the parent's placements, one DWM frame (F080)
            if (FrameLedger.Enabled && LedgerSink is { } ledger) HandLedgerTurn(ledger, waitStart, turnStart);
            ReportPace(turnStart, motionLive);
            bool forceIdleTrim = Interlocked.Exchange(ref _idleTrimForce, 0) != 0;
            if (_idleTrim is not null && (forceIdleTrim || !_idleTrimStarted || _idleTrimWaitMs < 0 || Environment.TickCount64 >= _idleTrimDueMs)) _idleTrimWaitMs = RunIdleTrim();
            if (requestedDrain > Volatile.Read(ref _completedDrains))
            {
                Volatile.Write(ref _completedDrains, requestedDrain);
                _done.Set();
            }
        }
        _displayClock?.SetActive(false);
    }

    /// <summary>The frame ledger's hand-off at a turn's end (only while it records): the loop's own facts of the turn - stamps, the
    /// present decision, this thread's cycles and allocations - to the owning host, which adds its submit's and records the turn.</summary>
    private void HandLedgerTurn(LedgerTurnSink sink, long waitStart, long turnStart)
    {
        long end = Stopwatch.GetTimestamp();
        ulong cycles = FluentGpu.Foundation.ThreadCycles.Read();
        var facts = new LedgerTurnFacts
        {
            WaitStartQpc = waitStart, StartQpc = turnStart, SlotOpenQpc = _turnSlotOpenQpc, DoneQpc = _turnDoneQpc, EndQpc = end,
            TickSeq = _turnLedgerTickSeq, TickQpc = _turnLedgerTickQpc, PublishSeq = _turnPublishSeq, Kind = _turnKind,
            MissedTicks = _turnMissed,
            Cycles = cycles >= _turnStartCycles && _turnStartCycles != 0 ? cycles - _turnStartCycles : 0, CyclesTotal = cycles,
            AllocBytes = GC.GetAllocatedBytesForCurrentThread() - _turnStartAlloc,
        };
        sink(in facts);
    }

    /// <summary>The one present decision of a turn. Returns whether render motion is live (for the pace report).
    /// <para><b>The turn presents for the tick it woke for.</b> The tick is read ONCE, before anything can block; the
    /// present-slot wait that follows may cross the next vblank, and the present still belongs to the tick it was decided
    /// for — the next turn then finds the NEXT tick already delivered and presents for it at once. Re-reading the tick
    /// after the wait (as this used to) charged the present to the vblank the wait crossed, so the following turn found
    /// "its" tick already spent and skipped it: with a present queue that cannot absorb a frame costing slightly more
    /// than one refresh, every present then cost two ticks — exactly half the refresh rate.</para>
    /// <para><b>The turn never queues behind a late frame.</b> A clock-paced turn whose slot is still busy past the grace
    /// (the previous present missed its vblank and owns this one) presents nothing while frames fit the early phase
    /// (<see cref="SlotCatchUp.ShouldSkip"/>): the tick stays un-presented, the loop goes back to its tick wait, and the
    /// next tick finds the slot free and presents on time — one vblank for the late frame instead of a run of late
    /// presents. Frames that do not fit keep the wait above (skipping them is the half-rate cliff).</para>
    /// <para><b>The turn never waits for the UI.</b> A fresh publication pending at the decision wins; otherwise motion
    /// re-poses the retained scene at this tick's present time. A UI frame that lands after the decision is presented on
    /// the next tick (DropOldest hands the newest then): a late UI frame only delays the fresh CONTENT by a tick, while
    /// every render-side pose (scroll, compositor animations) keeps its tick — and coverage clamping means a late frame
    /// can never show a blank row. (A handshake that blocked this decision for the UI's in-flight frame is deleted: it
    /// traded render-side ticks for the chance of an earlier fresh frame, and whenever the UI ran late it held motion
    /// presents hostage to it.)</para></summary>
    private bool PresentTurn(long turnStart)
    {
        bool motion = _needsTick?.Invoke() == true;   // any render motion: this host's or a detached child's
        // Only THIS host's own motion makes the turn a parent present (F094). A child's motion keeps the loop at display rate
        // (the Loop's clock wait) and its presents run in extraDrain on the child's swapchain; it must not take the primary
        // credit, count a motion present, or mark the parent's tick spent — that deferred every parent frame landing later in
        // the tick by a whole vblank while the pop-out animated. Pacing bookkeeping is per target: this is the parent's.
        bool ownMotion = motion && (_ownMotion?.Invoke() ?? true);
        if (!ownMotion) { _motionRun.Break(); _catchUp.Break(); }
        // Paced = the tick rule applies: THIS host's motion is live AND a display clock is delivering ticks. With no own
        // motion the UI's own production gate already bounds publications to one per tick, so a publish wake presents
        // immediately; with no clock (headless, RDP, not ticked yet) the credit alone throttles.
        bool clockPaced = ownMotion && _displayClock?.IsAvailable == true;
        long tickSeq = clockPaced ? _displayClock!.TickSeq : 0;
        long tickQpc = clockPaced ? _displayClock!.TickQpc : 0;
        // A stamp older than s_tickMaxAgeQpc is not this turn's vblank, and its seq is no tick this turn may find spent
        // (TryGetDisplayTick's rule, and the UI production gate's: AppHost.GateTickSeq). Either the clock STALLED (its waits
        // time out, a timeout is never a tick, so the seq freezes while the loop keeps waking on BackstopMs), or the loop
        // disarmed it while no motion was live and its last tick is from before that park. A FRESH publication pending must
        // not wait on that frozen seq: gated on it, the UI's backstop-paced frames (input feedback, timers) never reached the
        // glass until the clock ticked again. Such a turn runs unpaced (the credit's liveness take bounds it, as with no
        // clock), and its poses anchor on now: a row whose motion just came back (an unpark or un-occlusion:
        // HasOwnRenderMotion re-anchors every row at now) must not be posed at its anchor from the old stamp (a loop at
        // phase 0, a fade rewound). With no fresh publication the seq stays in force: a clock that stops mid-motion presents
        // nothing more for the tick it already spent, so the stale stamp alone never re-presents motion on every backstop wake.
        if (tickQpc != 0 && turnStart - tickQpc > s_tickMaxAgeQpc) { if (_publisher.HasPendingFrame) tickSeq = 0; tickQpc = 0; }
        bool fresh = _publisher.HasPendingFrame;
        if (!fresh && !ownMotion) return motion;   // bare wake (child drain, quiesce nudge) or child-only motion: nothing for the parent, no slot reserved (extraDrain already ran, before this decision)
        // This tick has already been presented for: the work waits for the next tick. Decided BEFORE the slot wait so
        // the loop goes back to its tick wait instead of blocking for a credit it would not spend on this vblank.
        if (tickSeq != 0 && tickSeq == _lastPresentedTickSeq)
        {
            _turnKind = LedgerTurnKind.TickSpent;
            Volatile.Write(ref _skippedTicks, _skippedTicks + 1);
            if (fresh && _lastPresentWasMotion && tickSeq != _raceTickSeq)
            {
                _raceTickSeq = tickSeq;
                Volatile.Write(ref _raceHits, _raceHits + 1);
            }
            return motion;
        }
        // A catch-up already gave this tick to the late frame (below) and a wake re-ran the turn on it — in production a UI
        // publication landing mid-tick wakes the loop. The vblank is still the late frame's: skip again, uncounted, without
        // a slot take or a policy call. Retaking would either be charged by the policy as a catch-up that did not hold (a
        // re-run is not a new busy tick — it would back off for a second on every such wake), or, near the end of the
        // tick, open the slot just after the next vblank and present THIS tick's frame there: the late phase the skip escaped.
        if (tickSeq != 0 && tickSeq == _catchUpTickSeq) { _turnKind = LedgerTurnKind.CatchUp; return motion; }
        // This tick's motion turn was ELIDED: nothing reached the glass and the credit it took is still held. Only a fresh
        // publication has anything to show on this vblank (a hover or keystroke landing mid-tick presents now, not a tick later);
        // a bare re-wake would re-pose the same tick into the same bytes and elide again.
        if (tickSeq != 0 && tickSeq == _elidedTickSeq && !fresh) { _turnKind = LedgerTurnKind.TickSpent; return motion; }
        _turnTickQpc = tickQpc;
        _turnLedgerTickSeq = tickSeq;
        _turnLedgerTickQpc = tickQpc;
        // The tick this turn's pose is decided for, for the render poser's engaged-edge crossings (same thread).
        FluentGpu.Scroll.Diag.EngagedCrossings.CurrentTickSeq = tickSeq;
        FluentGpu.Scroll.Diag.EngagedCrossings.CurrentTickQpc = tickQpc;
        // Take the credit (Windows Terminal AtlasEngine / makepad order): the present queue is what limits how fast
        // frames reach the glass; blocking for its slot first and choosing the frame second means the presented state
        // is the freshest one that existed when the slot opened. A motion re-present spends a present too, so it takes
        // the credit the same way — every present holds one. Held credits are idempotent (TryTakePresentSlot returns true).
        // A CLOCK-PACED turn takes it with a grace (SlotCatchUp.GraceMs, 2 ms at 120 Hz): a present that landed after a
        // vblank retires only at the NEXT vblank (+0.2–0.7 ms), so a slot still busy past the grace means the previous
        // present missed its vblank and owns this one. Waiting would present this tick's frame after the following vblank
        // too — and every later turn would inherit that late phase for as long as motion continues (09-29: runs of 55–86
        // turns one tick behind). Unpaced turns (no clock / no motion) keep the liveness-bounded wait, bounded at
        // max(2 x refresh, 34 ms) (F208): a slot that never opens (a minimized / cloaked / occluded primary whose presents do
        // not retire) costs that, not a second.
        long slotWait0 = Stopwatch.GetTimestamp();
        bool paced = tickSeq != 0;
        double refreshMs = PeriodQpc() * 1000.0 / Stopwatch.Frequency;
        bool held = _takePresentSlot?.Invoke(paced ? SlotCatchUp.GraceMs(refreshMs) : UnpacedLivenessTake(refreshMs)) ?? true;
        // The UI asked to park while the slot was being waited for (the take returns false WITHOUT taking the credit when the
        // park request interrupts it): present nothing, and do not retake below — the retake is the wait the UI is blocked
        // behind. The gate at the top of the loop parks next; the publication stays pending for the turn after Resume.
        if (!held && Volatile.Read(ref _resizeQuiesce) != 0) { _turnKind = LedgerTurnKind.Parked; return motion; }
        if (!held && paced)
        {
            // The previous present missed its vblank and owns this one. Skip while frames fit the early phase (the next
            // tick presents on time); otherwise wait as before (the depth policy owns over-budget frames).
            if (_catchUp.ShouldSkip(tickSeq, refreshMs))
            {
                _catchUpTickSeq = tickSeq;
                Volatile.Write(ref _catchUpSkips, _catchUpSkips + 1);
                _turnKind = LedgerTurnKind.CatchUp;
                return motion;   // tick NOT marked presented: MotionTickRun charges it at the next present (the next Turn
                                 // row shows missed=1 — no new probe row needed); the credit was not taken, nothing to undo
            }
            // Liveness-bounded: proceeds (credit held) even if the slot never opens. An interrupting park request is the one
            // false it can return — nothing was taken, so there is nothing to undo.
            if (!_takePresentSlot!.Invoke(-1) && Volatile.Read(ref _resizeQuiesce) != 0) { _turnKind = LedgerTurnKind.Parked; return motion; }
        }
        long slotOpen = Stopwatch.GetTimestamp();
        _turnSlotOpenQpc = slotOpen;
        ulong slotOpenCycles = FluentGpu.Foundation.ThreadCycles.Read();
        long slotWait = slotOpen - slotWait0;
        _slotWaitSumQpc += slotWait; _slotWaitCount++;
        if (slotWait > _slotWaitMaxQpc) _slotWaitMaxQpc = slotWait;
        Volatile.Write(ref _slotWaitTotalCount, _slotWaitTotalCount + 1);
        Volatile.Write(ref _slotWaitTotalQpc, _slotWaitTotalQpc + slotWait);
        if (slotWait > _slotWaitTotalMaxQpc) Volatile.Write(ref _slotWaitTotalMaxQpc, slotWait);
        bool longWait = slotWait > Stopwatch.Frequency / 250;   // > 4 ms
        // Decide on the publication state as it is NOW (a publication that landed during the slot wait wins over the
        // motion re-present) — but for the tick this turn woke for (see the remarks).
        var verdict = PresentCadence.Decide(new PresentCadenceInput
        {
            TickSeq = tickSeq, LastPresentedTickSeq = _lastPresentedTickSeq,
            HasFreshPublication = _publisher.HasPendingFrame, MotionDue = ownMotion,
            CreditHeld = true, Unpaced = tickSeq == 0,
        });
        bool presented = false, motionElided = false;
        ulong freshSeq = 0;
        if (verdict == PresentVerdict.PresentFresh)
        {
            // Acquire the LATEST published frame (DropOldest coalesce — intermediate publishes since the last turn are
            // dropped, §11). The arena the UI is now writing is a DIFFERENT ring slot than the published one this
            // reads, so there is no torn read. A false acquire here means the UI is mid-claim of a slot: its publish
            // wake follows; if motion is due the turn falls through to the motion re-present instead.
            if (_publisher.TryAcquire(out var rf))
            {
                Volatile.Write(ref _freshPresents, _freshPresents + 1);
                if (longWait) Volatile.Write(ref _freshLongWaits, _freshLongWaits + 1);
                _submitPresent(rf);
                freshSeq = rf.PublishSeq;
                // The publish seq this thread last presented (release write) — the UI reads it as a diagnostic
                // (how far behind render is); production pacing is on the compositor tick, not on this ack.
                Volatile.Write(ref _presentAck, rf.PublishSeq);
                _lastPresentWasMotion = false;
                presented = true;
                _turnKind = LedgerTurnKind.Fresh;
                _turnPublishSeq = rf.PublishSeq;
            }
            else if (ownMotion) verdict = PresentVerdict.PresentMotion;
        }
        if (verdict == PresentVerdict.PresentMotion && !presented)
        {
            Volatile.Write(ref _motionPresents, _motionPresents + 1);
            if (longWait) Volatile.Write(ref _motionLongWaits, _motionLongWaits + 1);
            _tick?.Invoke();
            motionElided = _tickPresented?.Invoke() == false;
            _lastPresentWasMotion = true;
            presented = true;
            _turnKind = LedgerTurnKind.Motion;
        }
        if (presented)
        {
            long done = Stopwatch.GetTimestamp();
            long missed = _motionRun.Presented(tickSeq);
            if (missed > 0) Volatile.Write(ref _missedMotionTicks, _missedMotionTicks + missed);
            _turnDoneQpc = done;
            _turnMissed = (int)Math.Min(missed, int.MaxValue);
            // An elided motion turn serviced the tick (the run above is charged) but did not SPEND it: the vblank stays open for a
            // fresh publication (the gate at the top). A real present spends it.
            if (motionElided && tickSeq != 0) _elidedTickSeq = tickSeq;
            else _lastPresentedTickSeq = tickSeq;
            long tickBase = tickQpc != 0 && tickQpc <= turnStart ? tickQpc : turnStart;
            long lag = done - tickBase;
            ulong doneCycles = FluentGpu.Foundation.ThreadCycles.Read();
            if (doneCycles > slotOpenCycles)
                _renderCyclesPerMs = FluentGpu.Foundation.ThreadCycles.Calibrate(_renderCyclesPerMs, doneCycles - slotOpenCycles,
                    (done - slotOpen) * 1000.0 / Stopwatch.Frequency);
            if (lag > _presentLagMaxQpc)
            {
                _presentLagMaxQpc = lag;
                _worstWakeQpc = turnStart - tickBase;
                _worstSlotQpc = slotWait;
                _worstWorkQpc = done - slotOpen;
                _worstRunMs = FluentGpu.Foundation.ThreadCycles.ToMs(_turnStartCycles, doneCycles, _renderCyclesPerMs);
                _worstTick = tickSeq;
                _worstDoneQpc = done;
                _worstSplit = _presentSplit?.Invoke() ?? default;
            }
            if (freshSeq != 0) PresentLedger.Record(freshSeq, tickSeq, tickQpc, done);   // "when did publication N reach the glass"
            // Per-present evidence (the render ring): which tick, what it skipped, and where its time went — wake lag
            // (tick → turn start), the present-slot wait, and the work (record + submit + back-buffer fence + Present).
            double toMs = 1000.0 / Stopwatch.Frequency;
            FluentGpu.Scroll.Diag.ScrollProbe.Turn(tickBase, tickSeq, (int)Math.Min(missed, int.MaxValue),
                (turnStart - tickBase) * toMs, slotWait * toMs, (done - slotOpen) * toMs, !_lastPresentWasMotion);
            // Feed the catch-up policy the frame's cost WITHOUT its slot wait or its wake lag (in a late run both are the
            // lateness itself, not frame cost — counting either calls every late run over budget and never catches up):
            // render work + the latest retired GPU execution. RenderPaceHostState is a readonly record struct returned by value through a cached delegate — no
            // allocation on this per-present path.
            if (paced)
                _catchUp.Observe((done - slotOpen) * toMs, _paceHost?.Invoke().GpuExecutionMs ?? 0);
        }
        return motion;
    }

    /// <summary>One always-on <c>[render.pace]</c> line per second while motion is live (Diag.Line, render thread):
    /// the tick seq (+ticks in the window), presents by kind, skipped turns, race hits, MISSED motion ticks (the pacing
    /// cliff's own counter), slot-wait avg/max and the worst tick→present lag; then the display clock's filter state
    /// (measured beat, ignored double-tick returns, slot drops, decimating) and the host's pacing state (present-queue
    /// depth, adaptive GPU governor EMA + engaged, the UI loop's last wait kind, the latest retired GPU execution), the
    /// worst present's split (wake / slot / work, then the host's phases of that work and the blocking one named:
    /// <see cref="PresentSplit"/>), and the slot catch-up (<see cref="SlotCatchUp"/>): skips in the window, the smoothed frame
    /// cost it compares against the refresh, and whether it is backing off, then <c>childDrain(avg max n)</c>: the wall time of the detached children's present
    /// drain per turn in the window (F215; it runs before the primary's present decision, so no worst present's <c>work=</c> contains it, while its
    /// <c>lag=</c> does: the child's submit, its Stage-B fence spin (up to 16 ms) and its back-buffer fence waits sit before the parent's present). Every figure up to there is the PRIMARY
    /// swapchain's; a trailing <c>child=[t&lt;id&gt;(presents deferred skipped slotWaitAvg/Max lagMax workMax) ...]</c> section carries each
    /// detached pop-out's own (<see cref="ChildPresentPace"/>), present only while a child presented or was deferred in the window.
    /// Allocation only here, on the 1 Hz path; the window resets when motion stops.</summary>
    private void ReportPace(long now, bool motionLive)
    {
        if (!motionLive) { _paceWindowStartQpc = 0; return; }
        long tickSeq = _displayClock?.TickSeq ?? 0;
        if (_paceWindowStartQpc == 0)
        {
            _paceWindowStartQpc = now; _paceWindowTickSeq = tickSeq;
            _paceFresh0 = _freshPresents; _paceMotion0 = _motionPresents; _paceSkipped0 = _skippedTicks;
            _paceMissed0 = _missedMotionTicks; _paceRace0 = _raceHits; _paceCatchUp0 = _catchUpSkips;
            _paceIgnored0 = _displayClock?.IgnoredReturns ?? 0; _paceSlotDrops0 = _displayClock?.SlotDrops ?? 0;
            RenderPaceHostState host0 = _paceHost?.Invoke() ?? default;
            _paceSlotTimeouts0 = host0.SlotLivenessTimeouts;
            _paceChildTimeouts0 = host0.NonPrimaryLatencyTimeouts;
            _slotWaitSumQpc = 0; _slotWaitMaxQpc = 0; _slotWaitCount = 0; _presentLagMaxQpc = 0;
            _childDrainSumQpc = 0; _childDrainMaxQpc = 0; _childDrainCount = 0;
            _worstWakeQpc = _worstSlotQpc = _worstWorkQpc = _worstTick = _worstDoneQpc = 0; _worstRunMs = float.NaN;
            _worstSplit = default;
            _childPaceBegin?.Invoke();
            return;
        }
        if (now - _paceWindowStartQpc < Stopwatch.Frequency) return;
        double toMs = 1000.0 / Stopwatch.Frequency;
        double slotAvg = _slotWaitCount == 0 ? 0 : _slotWaitSumQpc * toMs / _slotWaitCount;
        var clock = _displayClock;
        long ignored = clock?.IgnoredReturns ?? 0, slotDrops = clock?.SlotDrops ?? 0;
        RenderPaceHostState host = _paceHost?.Invoke() ?? default;
        string child = _childPaceReport?.Invoke() ?? "";
        long childTimeouts = host.NonPrimaryLatencyTimeouts - _paceChildTimeouts0;
        // Quiet windows are counted, not printed: a second of motion that presented, missed no tick, raced nothing, needed no
        // catch-up, timed no slot out, ran ungoverned at queue depth 1 without decimation and kept every present within two
        // refresh periods has nothing to report, and printing it anyway was one formatted line (and its strings) per second
        // for as long as anything moved. A window whose pacing STATE differs from the last printed line's prints too (an edge
        // back to quiet is news), and every PaceHeartbeatWindows-th quiet window prints as a heartbeat. The next printed line
        // carries the count (quiet=N); `--fg pace` prints every window as before.
        var facts = new PaceWindowFacts(
            Missed: _missedMotionTicks - _paceMissed0, CatchUps: _catchUpSkips - _paceCatchUp0,
            SlotTimeouts: host.SlotLivenessTimeouts - _paceSlotTimeouts0, ChildTimeouts: childTimeouts,
            SlotDrops: slotDrops - _paceSlotDrops0, Races: _raceHits - _paceRace0,
            Presents: (_freshPresents - _paceFresh0) + (_motionPresents - _paceMotion0),
            PresentLagMaxMs: Math.Max(_presentLagMaxQpc, _childDrainMaxQpc) * toMs, RefreshMs: PeriodQpc() * toMs,
            State: new PaceState(host.GovernorEngaged, clock?.Decimating ?? false, host.PresentQueueDepth));
        if (!ShouldPrintPaceWindow(in facts, _pacePrinted, _paceQuietWindows, child.Length != 0, EngineSwitchesPaceLog))
        {
            _paceQuietWindows++;
            _paceWindowStartQpc = 0;
            return;
        }
        _pacePrinted = facts.State;
        long quiet = _paceQuietWindows;
        _paceQuietWindows = 0;
        string timeoutTarget = PaceTimeoutTarget(host.SlotLivenessTimeouts - _paceSlotTimeouts0, childTimeouts);
        double childDrainAvg = _childDrainCount == 0 ? 0 : _childDrainSumQpc * toMs / _childDrainCount;
        FluentGpu.Foundation.Diag.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[render.pace] tick={tickSeq}(+{tickSeq - _paceWindowTickSeq}) fresh={_freshPresents - _paceFresh0} motion={_motionPresents - _paceMotion0} skipped={_skippedTicks - _paceSkipped0} race={_raceHits - _paceRace0} missed={_missedMotionTicks - _paceMissed0} slotWaitAvg={slotAvg:F2} slotWaitMax={_slotWaitMaxQpc * toMs:F2} slotTimeouts={host.SlotLivenessTimeouts - _paceSlotTimeouts0} timeoutTarget={timeoutTarget} childWaitMax={host.NonPrimaryLatencyWaitMaxMs:F2} childTimeouts={childTimeouts} presentLagMax={_presentLagMaxQpc * toMs:F2} clockPeriod={(clock?.MeasuredPeriodQpc ?? 0) * toMs:F3} ignored={ignored - _paceIgnored0} slotDrops={slotDrops - _paceSlotDrops0} decimating={((clock?.Decimating ?? false) ? 1 : 0)} depth={host.PresentQueueDepth} governorEma={host.GovernorEmaMs:F2} governor={(host.GovernorEngaged ? 1 : 0)} wait={host.LastWaitKind} gpuMs={host.GpuExecutionMs:F2} worst(lag={_presentLagMaxQpc * toMs:F2} wake={_worstWakeQpc * toMs:F2} slot={_worstSlotQpc * toMs:F2} work={_worstWorkQpc * toMs:F2} run={(float.IsNaN(_worstRunMs) ? "?" : _worstRunMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))} {_worstSplit.Format(_worstSlotQpc * toMs, _worstWorkQpc * toMs)} tick={_worstTick} atMs={_worstDoneQpc * toMs:F1}) catchUp={_catchUpSkips - _paceCatchUp0} costEma={_catchUp.CostEmaMs:F2} backoff={(_catchUp.BackingOff(tickSeq) ? 1 : 0)} childDrain(avg={childDrainAvg:F2} max={_childDrainMaxQpc * toMs:F2} n={_childDrainCount}) quiet={quiet}{child}"));
        _paceWindowStartQpc = 0;   // next turn opens a fresh window
    }

    private long _paceQuietWindows;   // quiet [render.pace] windows since the last printed line
    private PaceState? _pacePrinted;  // the pacing state the last printed line reported (null: none printed yet)
    private static bool EngineSwitchesPaceLog => FluentGpu.Hosting.EngineSwitches.PaceLog;

    /// <summary>Quiet <c>[render.pace]</c> windows between heartbeat lines (~30 s of smooth motion).</summary>
    internal const int PaceHeartbeatWindows = 30;

    /// <summary>The pacing STATE of a window: whether the GPU governor is engaged, whether the display clock decimates, and
    /// the present-queue depth. A change of any of these since the last printed line is itself worth a line.</summary>
    internal readonly record struct PaceState(bool GovernorEngaged, bool Decimating, int Depth);

    /// <summary>One <c>[render.pace]</c> window's counters, as the print decision reads them.</summary>
    internal readonly record struct PaceWindowFacts(long Missed, long CatchUps, long SlotTimeouts, long ChildTimeouts,
        long SlotDrops, long Races, long Presents, double PresentLagMaxMs, double RefreshMs, PaceState State);

    /// <summary>Whether a window is anomalous on its own: a missed motion tick, a catch-up skip, a liveness slot timeout
    /// (primary or child), a display-clock slot drop, a present race, NO present at all while motion was live (frozen
    /// motion), the governor engaged, a decimating clock, a present queue deeper than one, or a present (or a detached
    /// child's drain) that took more than two refresh periods. Pure; the tests pin it.</summary>
    internal static bool PaceWindowAnomalous(in PaceWindowFacts f)
        => f.Missed > 0 || f.CatchUps > 0 || f.SlotTimeouts > 0 || f.ChildTimeouts > 0 || f.SlotDrops > 0 || f.Races > 0
           || f.Presents == 0 || f.State.GovernorEngaged || f.State.Decimating || f.State.Depth > 1
           || f.PresentLagMaxMs > 2.0 * f.RefreshMs;

    /// <summary>Whether a window gets a line: <c>--fg pace</c>, a child section to report, an anomalous window, a pacing
    /// state that differs from the last printed line's (or no line printed yet), or the heartbeat after
    /// <see cref="PaceHeartbeatWindows"/> quiet windows. Pure; the tests pin it.</summary>
    internal static bool ShouldPrintPaceWindow(in PaceWindowFacts f, PaceState? lastPrinted, long quietSincePrinted,
        bool hasChildSection, bool printAll)
        => printAll || hasChildSection || PaceWindowAnomalous(in f) || lastPrinted != f.State
           || quietSincePrinted >= PaceHeartbeatWindows;

    /// <summary>Which swapchain's liveness-bounded slot waits timed out in a pace window (F235): <c>none</c>, <c>primary</c> (the
    /// main window), <c>child</c> (a pop-out or popup, whose blocking wait runs on this shared thread) or <c>both</c>. Pure.</summary>
    internal static string PaceTimeoutTarget(long primaryTimeouts, long childTimeouts)
        => primaryTimeouts > 0 ? (childTimeouts > 0 ? "both" : "primary") : childTimeouts > 0 ? "child" : "none";

    /// <summary>UI thread, FORCE-SYNC (Step 4): wake the render thread and block until it has submitted+presented the
    /// just-published frame.</summary>
    public void DrainSync()
    {
        ThreadGuard.AssertUi();
        if (_disposed) return;   // teardown race (window closed / thread joined): nothing to present, don't touch a disposed event
        long requested = Interlocked.Increment(ref _requestedDrains);
        _wake.Set();
        while (Volatile.Read(ref _completedDrains) < requested) _done.WaitOne();
    }

    /// <summary>UI thread, ASYNC (Step 5): wake the render thread and RETURN immediately — the UI proceeds while the
    /// render thread submits/presents on its own timeline (the smoothness win: the GPU fence-wait stall no longer bounds
    /// back to the UI thread). EXPERIMENTAL / default-off — safe shipping additionally requires the UploadImage
    /// producer→consumer handoff + the resize/device-lost rendezvous + a green GPU soak (landing plan §9).</summary>
    public void WakeAsync()
    {
        ThreadGuard.AssertUi();
        if (_disposed) return;   // teardown race (e.g. a detached child's last publish after the parent thread joined): drop it
        _wake.Set();
    }

    /// <summary>ANY thread: wake the loop with no publication (a video surface handle arrived, F208) so its <c>preTurn</c> runs
    /// on the next turn instead of waiting for a UI frame to be published. A wake the loop has not consumed yet coalesces
    /// (auto-reset). Silently dropped once the thread is disposed.</summary>
    public void WakeForVideo()
    {
        if (_disposed) return;
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }   // teardown race: Dispose closed the event between the check and the Set
    }

    /// <summary>UI thread, ASYNC (Step 2): PARK the render loop before mutating the swapchain in Resize, and BLOCK until
    /// it confirms it is idle (no submit/present in flight) — mutual exclusion so the UI's fenced <c>ResizeBuffers</c> +
    /// back-buffer release can't race a concurrent present. Pair with <see cref="Resume"/> in a try/finally. The final
    /// pre-park frame (if the loop was mid-submit) completes at the OLD size before the park; the stale published frame is
    /// dropped (DropOldest) and the post-resize relayout republishes at the new size.</summary>
    public void Quiesce()
    {
        ThreadGuard.AssertUi();
        ThreadGuard.EnterRenderOwnership();   // BEFORE the disposed early-out: a joined loop leaves the UI the owner anyway
        if (_disposed) return;   // teardown race: the loop is gone, nothing to park
        long t0 = Stopwatch.GetTimestamp();
        Volatile.Write(ref _resizeQuiesce, 1);
        _parkRequested.Set();      // ends a present-slot wait the loop is blocked in (the device waits on this beside the waitable)
        _wake.Set();               // nudge the loop so it reaches the quiesce gate even if idle-parked on _wake
        _resizeIdle.WaitOne();     // acquire barrier: the loop is now parked on _resumeResize
        NoteQuiesceWait(Stopwatch.GetTimestamp() - t0);
    }

    // Always-on evidence: the longest rendezvous and a rate-limited [render.quiesce] line for any that cost more than 4 ms
    // (one line per second, with the count it stood in for), so a UI stall behind the park names itself in the log.
    private void NoteQuiesceWait(long waitQpc)
    {
        Volatile.Write(ref _quiesceCount, _quiesceCount + 1);
        if (waitQpc > _quiesceWaitMaxQpc) Volatile.Write(ref _quiesceWaitMaxQpc, waitQpc);
        double ms = waitQpc * 1000.0 / Stopwatch.Frequency;
        if (ms < 4.0) return;
        long now = Stopwatch.GetTimestamp();
        if (_quiesceLogQpc != 0 && now - _quiesceLogQpc < Stopwatch.Frequency) { _quiesceLogSuppressed++; return; }
        FluentGpu.Foundation.Diag.Line(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[render.quiesce] ms={ms:F1} max={QuiesceWaitMsMax:F1} suppressed={_quiesceLogSuppressed}"));
        _quiesceLogQpc = now;
        _quiesceLogSuppressed = 0;
    }

    /// <summary>UI thread, ASYNC (Step 2): release the render loop after the swapchain Resize completed.</summary>
    public void Resume()
    {
        ThreadGuard.AssertUi();
        ThreadGuard.ExitRenderOwnership();
        if (_disposed) return;   // teardown race: paired with a Quiesce that also no-op'd
        _resumeResize.Set();
    }

    private bool _disposed;

    /// <summary>Stop + join the render thread. Idempotent (a pre-capture quiesce may call it before AppHost.Dispose).
    /// After this returns the render thread is gone, so the caller (UI) is the sole GPU-ComPtr owner again.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _wake.Set();               // unblock the loop so it can observe !_running and exit
        _resumeResize.Set();       // Step 2: also release a loop parked mid-quiesce, so teardown can't hit the Join timeout
        // A timeout is not proof of termination: disposing events or GPU state while a blocked
        // submit is still running causes use-after-dispose. Ownership returns only after the join.
        _thread.Join();
        ThreadGuard.AdoptRenderOwnership();   // the joining thread is the sole GPU-ComPtr owner from here on (CaptureBgra, Dispose)
        _abortHandleSink?.Invoke(0);          // the device must not wait on an event that is about to be closed
        _displayClock?.Dispose();
        _wake.Dispose();
        _done.Dispose();
        _resizeIdle.Dispose();
        _resumeResize.Dispose();
        _parkRequested.Dispose();
    }
}

/// <summary>The render loop's own facts of one turn, handed to <see cref="RenderThread.LedgerSink"/> (FrameLedger).</summary>
internal struct LedgerTurnFacts
{
    public long WaitStartQpc, StartQpc, SlotOpenQpc, DoneQpc, EndQpc, TickSeq, TickQpc, AllocBytes;
    public ulong PublishSeq, Cycles, CyclesTotal;
    public LedgerTurnKind Kind;
    public int MissedTicks;
}

/// <summary>The owning host's side of a ledgered render turn (render thread; must not allocate).</summary>
internal delegate void LedgerTurnSink(in LedgerTurnFacts facts);
