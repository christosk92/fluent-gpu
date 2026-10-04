using System.Diagnostics;
using System.Globalization;

namespace FluentGpu.Hosting;

/// <summary>
/// One bit per term of <see cref="AppHost.HasActiveWork"/>: the reason(s) the frame loop refused to idle this frame.
/// The bit order mirrors the OR-chain in <c>AppHost.ComputeWakeReasons</c> (AppHost.cs) one-to-one.
/// </summary>
[Flags]
public enum WakeReasons
{
    None = 0,
    FrameNeeded = 1 << 0,       // _frameNeeded (input/resize/explicit wake pending)
    RuntimePending = 1 << 1,    // _runtime.HasPending (scheduled render-effects)
    DynamicText = 1 << 2,       // _scene.HasDynamicText (FPS/draw-count HUD strings)
    Anim = 1 << 3,              // an AnimEngine row is DUE now (NextDueMs <= 0) — a row paced at a slower cadence sets no bit until its next edge; the host shapes the wait from NextDueMs instead
    // 1 << 4 is retired: it was Interact (_interact.HasActive), which nothing has set since hover/press fades became
    // animation rows (the Anim bit). The position stays reserved so the [wake] census columns keep their meaning.
    ScrollAnim = 1 << 5,        // _scrollAnim.HasActive (smooth scroll + scrollbar fade)
    Repeat = 1 << 6,            // _repeat.HasActive (RepeatButton auto-repeat)
    Caret = 1 << 7,             // the focused-editor caret blink is DUE now (CaretBlinker.NextDueMs <= 0); between edges it shapes the wait, not the mask
    BrushAnims = 1 << 8,        // _scene.HasBrushAnims (implicit BrushTransition)
    ImagesPending = 1 << 9,     // device-side texture uploads/copies still need a submit
    ImageCrossfades = 1 << 10,  // _images.HasActiveCrossfades (reveal fades in flight)
    Orphans = 1 << 11,          // _scene.OrphanCount > 0 (exit-animating orphans)
    DragDropWork = 1 << 12,     // _dispatcher.Drag.HasActiveWork || _dispatcher.DragDrop.HasActiveWork (E5 easing/edge-scroll)
    DragActive = 1 << 13,       // _dispatcher.Drag.IsActive (E5 reorder dwell keep-alive)
    GestureHold = 1 << 14,      // _dispatcher.HasArmedHold (§7A touch long-press timer keep-alive on a stationary held finger)
    PopupAnim = 1 << 15,        // a windowed popup is mid-open-reveal (CompositionBackdrop) OR still owes its FIRST content present (ISwapchain.HasPresentedContent false — its window is hidden waiting for that paint) — keep presenting until both settle
    TouchPress = 1 << 16,       // delayed 100ms pressed visual for touch inside a scrollable viewport
    VideoPresenting = 1 << 17,  // retained diagnostic bit; native video presentation no longer drives the host cadence
    Timer = 1 << 18,            // a HostTimerQueue timer is DUE this frame (UseTimeout/UseInterval/UseDebouncedValue/UseThrottledValue) — a pending-but-future timer sets NO bit (it only shapes RecommendedWaitMs, so the loop still idles)
    WarmCadence = 1 << 19,      // post-input warm-cadence hold: keep rendering ~1s after the last input before full quiesce (GPUI ProMotion re-ramp lesson) — real window only
    ImageReady = 1 << 20,       // a decode worker published a completion that the UI thread can apply now
    BakedBlurPending = 1 << 21, // queued static derivatives, serviced at a low 30 Hz budget only after interaction settles
    FrameClockPoller = 1 << 22, // an explicit FrameClock.Tick subscriber (for example the smooth compositor-bound playhead)
    VideoPumpPending = 1 << 23, // one coalesced native-video / geometry pump must run after layout settles
    FrameClockPaceable = 1 << 25, // a FrameClock.PaceableTick subscriber: per-frame motion the GPU governor MAY pace (a visualizer)
    WarmingVirtuals = 1 << 24,  // a KeepAlive unpark replay still in flight
    ScrollProducer = 1 << 26,  // IPlatformWindow.ScrollProducerLive: a frame-aligned producer (DM
                               // engaged/pending, or a hi-res wheel-fallback gesture live) needs one PumpScroll per refresh
    TextRepaintPending = 1 << 27, // IGpuDevice.TextRepaintPending: a glyph-atlas overflow deferred its flush and drew
                                   // text blank last frame — one more un-skippable frame is owed to re-record it clean
    ImageLeftoverDue = 1 << 28,   // ImageCache.LeftoverRetryDueMs has passed: a PINNED canceled leftover (a decode that
                                  // was canceled / refused by Begin while a node still holds it) is owed its sweep
                                  // restart in the next Pump — a pending-but-future one sets NO bit (it only shapes
                                  // RecommendedWaitMs, like Timer), so an idle page still wakes for it without input
}

/// <summary>
/// ALWAYS-ON attribution of WHY the frame loop stays awake — the smoking gun behind a process that never idles.
/// Records the <see cref="WakeReasons"/> mask of every awake frame and emits one <c>[wake]</c> line per
/// <see cref="ReportSeconds"/> through <see cref="FluentGpu.Foundation.Diag.Line"/>: the observed frame rate and its
/// focus split (<c>focus act=…fps/…s inact=…fps/…s</c>, <see cref="AppendFocusSplit"/>), per-reason kept-awake counts, SOLE-reason counts (frames where exactly one bit was set — the cleanest
/// attribution), the consecutive-awake streak, seconds since the loop last went fully idle, frames spent minimized,
/// a reconcile/layout/record-only work split, the live <c>FrameClock.Tick</c> subscribers at print time
/// (<c>pollers=</c>), and every subscriber that held ANY frame awake this window even if it had already unmounted by
/// print time (<c>pollersSeen=</c>, <see cref="AppendPollersSeen"/>).</summary>
///
/// <para>This used to be <c>FG_WAKE_DIAG=1</c> to stderr once a second. That is exactly the shape this codebase has
/// learned not to ship: "the loop is pinned at panel rate and nothing in the log says which term holds it" is
/// unanswerable after the fact, and a switch the operator never knew to set is no better than no instrument at all —
/// the same lesson as the compositor-clock latch, which logged at Debug and was dropped
/// (<c>docs/plans/wavee/scroll-feel-investigation-2026-09-10.md</c> §3.4). The cost is one
/// <c>ComputeWakeReasons()</c> per frame (a few dozen field reads and bitwise ORs, no allocation) plus one reused
/// StringBuilder line every 30 s, which respects the "never per-frame" cadence contract on <c>Diag.Line</c>.</para>
/// </summary>
internal sealed class WakeDiagnostics
{
    /// <summary>Census window. 30 s rather than 1 s: two lines a minute is a log an operator can send after an
    /// intermittent "it went bad", and a term that holds the loop awake holds it for far longer than one window.</summary>
    private const double ReportSeconds = 30.0;

    // Per-reason awake-frame counts this window, indexed by bit position (0..ReasonCount-1).
    // MUST cover every bit in WakeReasons. This was 26 while the enum already had 28, so scrollProducer and
    // textRepaintPending — a frame-aligned scroll producer and a deferred glyph-atlas flush, EITHER of which can hold
    // the loop at panel rate — were silently absent from every report this instrument ever printed.
    private const int ReasonCount = 29;
    private static readonly string[] s_reasonNames =
    [
        "frameNeeded", "runtimePending", "dynamicText", "anim", "retired4", "scrollAnim", "repeat", "caret",
        "brushAnims", "imagesPending", "imageCrossfades", "orphans", "dragDropWork", "dragActive", "gestureHold",
        "popupAnim", "touchPress", "videoPresenting", "timer", "warmCadence", "imageReady", "bakedBlurPending",
        "frameClockPoller", "videoPumpPending", "warmingVirtuals", "budgetDeferredVirtuals",
        "scrollProducer", "textRepaintPending", "imageLeftoverDue",
    ];

    private readonly long[] _reasonFrames = new long[ReasonCount];   // frames where reason i kept the loop awake
    private readonly long[] _soleFrames = new long[ReasonCount];     // frames where reason i was the ONLY bit set
    private long _framesRun;          // frames where RunFrame did real work (awake)
    private long _framesRendered;     // of those, frames that actually rendered (FrameStats.Rendered)
    private long _framesMinimized;    // awake frames observed while the window was minimized
    private long _reconciledFrames;   // awake frames that reconciled
    private long _layoutFrames;       // awake frames that ran layout
    private long _recordOnlyFrames;   // awake frames that neither reconciled nor laid out (compositor-only)

    // The focus split: awake frames and wall time with the window NOT active (focus elsewhere), so "it gets slow when I
    // click away" is a number in the log rather than an impression. Time is attributed to the state the previous Record
    // observed — a focus edge is a window message, so it always wakes the loop and lands a Record of its own.
    private readonly Func<bool> _windowActive;
    private long _framesInactive;     // awake frames run while the window was not active
    private long _activeTicks, _inactiveTicks;
    private long _lastRecordTicks;
    private bool _lastActive = true;

    private long _skipMisses;        // maybe-unchanged frames whose draw-list hash missed the elision baseline
    private readonly System.Text.StringBuilder _sb = new(512);   // reused: no per-window buffer allocation

    private int _awakeStreak;         // consecutive awake frames (reset when the loop last saw None)
    private long _lastIdleTicks;      // timestamp of the last fully-idle observation (seconds-since-idle base)
    private long _windowStartTicks;

    // The FrameClock.Tick ambient signal (AppHost._frameClockSig). Its subscriber list IS the set of live per-frame
    // pollers — the frameClockPoller wake bit is nothing but `HasSubscribers` (AppHost.ComputeWakeReasons). Held here
    // so the census can NAME them: the count alone (AppHost.FrameClockPollerCount) existed for a whole release and was
    // printed nowhere, which left "frameClockPoller held the loop on 100 % of runs, subscriber unknown" as an
    // unanswerable report — exactly the kind this instrument was built to make answerable.
    private readonly FluentGpu.Signals.Signal<object?> _frameClockSig;

    /// <summary>Most poller names printed per line; the rest fold into a <c>+k</c> tail. A leak of forty identical rows
    /// is answered by the first eight names and the count — the census must stay ONE log line.</summary>
    private const int MaxPollerNames = 8;

    // ── pollersSeen: every FrameClock.Tick subscriber alive during ANY kept-awake frame this window, named, with a
    // frame count — not just the survivors `pollers=` sees at print time. A poller that mounts, holds the loop awake
    // for hundreds of frames, and unmounts before the 30 s report prints leaves `pollers=0`: the log has a hole
    // exactly where the culprit was. Fixed-capacity, reused across windows — no per-window array allocation, no
    // LINQ, no per-frame string concatenation.
    private const int MaxPollersSeen = 8;
    private readonly string?[] _pollersSeenNames = new string?[MaxPollersSeen];
    private readonly long[] _pollersSeenFrames = new long[MaxPollersSeen];
    private int _pollersSeenCount;

    // Cached snapshot of the CURRENT subscriber set's names. Refreshed only when Signal.SubscriberSetVersion moves
    // (an actual subscribe/unsubscribe), not on every kept-awake frame — walking SubscriberAt + DiagOwner for a
    // steady-state set that isn't changing would be pure waste on a bit that can be set every frame for minutes.
    private readonly string?[] _currentPollerNames = new string?[MaxPollersSeen];
    private int _currentPollerCount;
    private int _lastPollerSetVersion = -1;

    // The animation engine + scene, for the live-track and orphan census below. A compositor-owned track is the ONE
    // wake source this line could not see: WakeReasons.Anim is masked while RenderOwnsCompositor, so a page pinned at
    // panel rate by four wedged rows printed `kept: frameNeeded timer` and nothing else — a report that names the
    // cheap terms and hides the expensive one.
    private readonly FluentGpu.Animation.AnimEngine _anim;
    private readonly FluentGpu.Scene.SceneStore _scene;

    private readonly Action<System.Text.StringBuilder> _appendRenderCensus;

    public WakeDiagnostics(FluentGpu.Signals.Signal<object?> frameClockSig, FluentGpu.Animation.AnimEngine anim, FluentGpu.Scene.SceneStore scene,
        Action<System.Text.StringBuilder> appendRenderCensus, Func<bool> windowActive)
    { _frameClockSig = frameClockSig; _anim = anim; _scene = scene; _appendRenderCensus = appendRenderCensus; _windowActive = windowActive; }

    /// <summary>Close the elapsed span into the focus bucket the PREVIOUS observation saw, then re-sample focus.</summary>
    private bool SampleFocus(long now)
    {
        if (_lastRecordTicks != 0)
        {
            long dt = now - _lastRecordTicks;
            if (_lastActive) _activeTicks += dt; else _inactiveTicks += dt;
        }
        _lastRecordTicks = now;
        return _lastActive = _windowActive();
    }

    internal long FramesInactiveForTest => _framesInactive;

    /// <summary>Append <c> | focus act=Nfps/Ss inact=Nfps/Ss</c>: the awake-frame rate and wall time with the window
    /// active vs not, for the window just closed. Exposed for tests the same way <see cref="AppendPollersSeen"/> is.</summary>
    internal void AppendFocusSplit(System.Text.StringBuilder sb)
    {
        double f = Stopwatch.Frequency;
        double actSec = _activeTicks / f, inactSec = _inactiveTicks / f;
        long actFrames = _framesRun - _framesInactive;
        sb.Append(CultureInfo.InvariantCulture,
            $" | focus act={(actSec > 0 ? actFrames / actSec : 0):0.0}fps/{actSec:0.0}s inact={(inactSec > 0 ? _framesInactive / inactSec : 0):0.0}fps/{inactSec:0.0}s");
    }

    /// <summary>Append <c>pollers=N</c> and, when N &gt; 0, <c>:Name,Name,…</c> — each live <c>FrameClock.Tick</c>
    /// subscriber's owning component type (<c>Computation.DiagOwner</c>), or <c>?</c> plus the computation's own
    /// type for one built outside a component. Report cadence ONLY: a plain loop over the live subscriber list
    /// appending into the caller's reused builder, so there is no snapshot and no per-frame cost. Shared with
    /// <c>AppHost.DescribeFrameClockPollers</c> so a gate asserts exactly what the log prints.</summary>
    internal static void AppendPollers(System.Text.StringBuilder sb, FluentGpu.Signals.Signal<object?> sig)
    {
        int n = sig.SubscriberCount;
        sb.Append(CultureInfo.InvariantCulture, $" | pollers={n}");
        if (n == 0) return;
        sb.Append(':');
        int shown = Math.Min(n, MaxPollerNames);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(',');
            var c = sig.SubscriberAt(i);
            if (c.DiagOwner is { } owner) sb.Append(owner.GetType().Name);
            else sb.Append('?').Append(c.GetType().Name);
        }
        if (n > shown) sb.Append(CultureInfo.InvariantCulture, $",+{n - shown}");
    }

    /// <summary>A maybe-unchanged frame failed the draw-list-hash elision check and submitted after all. Folded into
    /// the census instead of a per-occurrence stderr line: it is a rate, not an event.</summary>
    public void NoteSkipMiss() => _skipMisses++;

    /// <summary>Record one frame's wake mask + classification. <paramref name="reasons"/> is the mask the loop
    /// computed; <paramref name="awake"/> is whether the frame actually did work (a frame can run for a completed
    /// image pump with reasons==None — counted toward the streak reset, not toward an awake reason).</summary>
    public void Record(WakeReasons reasons, bool awake, bool rendered, bool reconciled, bool laidOut, bool minimized)
    {
        long now = Stopwatch.GetTimestamp();
        if (_windowStartTicks == 0) { _windowStartTicks = now; _lastIdleTicks = now; }
        bool active = SampleFocus(now);

        if (reasons == WakeReasons.None)
        {
            _awakeStreak = 0;
            _lastIdleTicks = now;
        }
        else
        {
            _awakeStreak++;
        }

        if (!awake) return;   // image-pump-only frame: classified as idle above, no per-reason attribution

        _framesRun++;
        if (rendered) _framesRendered++;
        if (minimized) _framesMinimized++;
        if (!active) _framesInactive++;
        // 3-way split (one bucket per frame): reconciled wins over layout-only wins over compositor-only record.
        if (reconciled) _reconciledFrames++;
        else if (laidOut) _layoutFrames++;
        else _recordOnlyFrames++;

        int bits = (int)reasons;
        int set = System.Numerics.BitOperations.PopCount((uint)bits);
        for (int i = 0; i < ReasonCount; i++)
        {
            if ((bits & (1 << i)) == 0) continue;
            _reasonFrames[i]++;
            if (set == 1) _soleFrames[i]++;
        }

        if ((reasons & WakeReasons.FrameClockPoller) != 0) NotePollersSeen();
    }

    /// <summary>Attribute this kept-awake frame to every currently-live <c>FrameClock.Tick</c> subscriber, by name.
    /// Re-walks the subscriber list only when <see cref="FluentGpu.Signals.Signal{T}.SubscriberSetVersion"/> moved
    /// since the last call — steady state (the common case: the same 1-3 pollers ticking for seconds) is a handful of
    /// string== compares against the reused tally, no allocation.</summary>
    private void NotePollersSeen()
    {
        int ver = _frameClockSig.SubscriberSetVersion;
        if (ver != _lastPollerSetVersion)
        {
            _lastPollerSetVersion = ver;
            int n = Math.Min(_frameClockSig.SubscriberCount, MaxPollersSeen);
            _currentPollerCount = n;
            for (int i = 0; i < n; i++)
            {
                var c = _frameClockSig.SubscriberAt(i);
                _currentPollerNames[i] = c.DiagOwner is { } owner ? owner.GetType().Name : c.GetType().Name;
            }
        }

        for (int i = 0; i < _currentPollerCount; i++)
        {
            string name = _currentPollerNames[i]!;
            int idx = -1;
            for (int j = 0; j < _pollersSeenCount; j++)
                if (_pollersSeenNames[j] == name) { idx = j; break; }
            if (idx < 0)
            {
                if (_pollersSeenCount >= MaxPollersSeen) continue;   // rare overflow: window already names 8 distinct pollers
                idx = _pollersSeenCount++;
                _pollersSeenNames[idx] = name;
                _pollersSeenFrames[idx] = 0;
            }
            _pollersSeenFrames[idx]++;
        }
    }

    /// <summary>Append <c>pollersSeen=N:Name×frames,…</c> (or <c>pollersSeen=0</c>) for the window just closed — every
    /// distinct <c>FrameClock.Tick</c> subscriber that held a frame awake, INCLUDING one that unmounted before this
    /// report ran (see <see cref="AppendPollers"/>'s doc for why that survivorship gap matters). Exposed for tests the
    /// same way <c>DescribeFrameClockPollers</c> exposes <see cref="AppendPollers"/>.</summary>
    internal void AppendPollersSeen(System.Text.StringBuilder sb)
    {
        sb.Append(CultureInfo.InvariantCulture, $" | pollersSeen={_pollersSeenCount}");
        if (_pollersSeenCount == 0) return;
        sb.Append(':');
        for (int i = 0; i < _pollersSeenCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(_pollersSeenNames[i]).Append(CultureInfo.InvariantCulture, $"×{_pollersSeenFrames[i]}");
        }
    }

    /// <summary>Emit the census line if the window has elapsed, then reset it. Cheap timestamp check otherwise.
    /// Call once per frame from the host.</summary>
    public void MaybeReport()
    {
        long now = Stopwatch.GetTimestamp();
        if (_windowStartTicks == 0) { _windowStartTicks = now; _lastIdleTicks = now; return; }
        double sec = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        if (sec < ReportSeconds) return;

        double idleSec = (now - _lastIdleTicks) / (double)Stopwatch.Frequency;
        var sb = _sb;
        sb.Clear();
        // fps is the headline — "the loop ran at panel rate for 30 s" is the symptom; the kept/sole lists below are
        // the answer to "which term did that", which is the whole reason this instrument exists.
        sb.Append(CultureInfo.InvariantCulture,
            $"[wake] {sec:0.0}s fps={(sec > 0 ? _framesRun / sec : 0):0.0} run={_framesRun} rendered={_framesRendered}");
        SampleFocus(now);   // close the span since the last frame into its bucket, so the split covers the whole window
        AppendFocusSplit(sb);
        sb.Append(CultureInfo.InvariantCulture,
            $" | reconciled={_reconciledFrames} layout={_layoutFrames} recordOnly={_recordOnlyFrames} skipMiss={_skipMisses}");
        sb.Append(CultureInfo.InvariantCulture, $" | streak={_awakeStreak} idleAgo={idleSec:0.0}s minimized={_framesMinimized}");

        sb.Append(" | kept:");
        for (int i = 0; i < ReasonCount; i++)
            if (_reasonFrames[i] > 0) sb.Append(CultureInfo.InvariantCulture, $" {s_reasonNames[i]}={_reasonFrames[i]}");

        sb.Append(" | sole:");
        bool anySole = false;
        for (int i = 0; i < ReasonCount; i++)
            if (_soleFrames[i] > 0) { sb.Append(CultureInfo.InvariantCulture, $" {s_reasonNames[i]}={_soleFrames[i]}"); anySole = true; }
        if (!anySole) sb.Append(" none");

        // Live per-frame pollers, NAMED. `kept: frameClockPoller=N` above says the bit held the loop awake; this says WHO.
        AppendPollers(sb, _frameClockSig);
        // Every poller that held a frame awake this window, even one that already unmounted — pollers= above only
        // sees who is STILL subscribed at print time.
        AppendPollersSeen(sb);
        // UI desired tracks survive until completion feedback is imported. They do not establish that the
        // renderer is moving: report its actual motion decision and presented-frame delta alongside them.
        _anim.AppendLiveTrackCensus(sb);
        sb.Append(CultureInfo.InvariantCulture, $" orphans={_scene.OrphanCount}");
        _appendRenderCensus(sb);

        FluentGpu.Foundation.Diag.Line(sb.ToString());

        Array.Clear(_reasonFrames);
        Array.Clear(_soleFrames);
        _pollersSeenCount = 0;   // names/frames stay stale in the arrays past this index — harmless, count gates reads
        _skipMisses = 0;
        _framesRun = 0;
        _framesRendered = 0;
        _framesMinimized = 0;
        _framesInactive = 0;
        _activeTicks = 0;
        _inactiveTicks = 0;
        _reconciledFrames = 0;
        _layoutFrames = 0;
        _recordOnlyFrames = 0;
        _windowStartTicks = now;
    }
}
