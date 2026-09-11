using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Scroll;

/// <summary>
/// A single viewport's scrollbar "conscious" chrome row (scroll-v3-plan §3.1/§4/§10 WP-B item 4) — FadeT/ExpandT/
/// PointerOver/PointerOverScrollbar/IdleMs + the WinUI dwell/away timers, moved OUT of <see cref="ScrollState"/> so
/// motion (kernel-owned, <see cref="SceneScrollSink"/>) and chrome (this, UI-ticker-owned) can never share a writer.
/// Chrome NEVER reads or writes Offset/Band/Activity — it only reads <see cref="ScrollBarChromeRow.MotionStamp"/>
/// (stamped by <see cref="SceneScrollSink.Apply"/> via <see cref="ScrollBarChrome.NotifyMoved"/>, compared against
/// <see cref="ScrollBarChrome.FrameIndex"/>) to know "did this body REALLY move THIS frame" — deliberately not
/// <c>ScrollState.LastMovedFrame</c>, which the kernel stamps on every touch (moved or not); see
/// <see cref="SceneScrollSink.Chrome"/>'s remarks.
/// </summary>
public struct ScrollBarChromeRow
{
    public float FadeT;                   // scrollbar indicator opacity 0..1 (eased in on scroll/hover, auto-hides after idle)
    public float ExpandT;                 // WinUI conscious scrollbar expansion 0=thin indicator, 1=full gutter + buttons
    public bool  PointerOver;              // pointer is inside this scroll viewport
    public bool  PointerOverScrollbar;     // pointer is inside this viewport's scrollbar gutter
    public float IdleMs;                   // time since the last scroll movement / hover (drives the auto-hide)

    /// <summary>The <see cref="ScrollBarChrome.FrameIndex"/> value of the last frame <see cref="SceneScrollSink.Apply"/>
    /// reported a REAL offset/band/zoom change for this viewport (<see cref="ScrollBarChrome.NotifyMoved"/>) —
    /// chrome's own "moved this frame" truth, deliberately independent of the kernel's <c>ScrollState.LastMovedFrame</c>
    /// (which stamps on every touch, not just a real one — see <see cref="SceneScrollSink.Chrome"/>'s remarks).</summary>
    public uint  MotionStamp;

    // ── WinUI "conscious" dwell/away timers (ported verbatim from the pre-v3 ScrollIntegrator.Conscious struct,
    // legacy snapshot ScrollIntegrator.cs:114-127) ──
    public float LaneDwellMs;      // continuous lane hover (toward ExpandBeginMs)
    public float LaneOffDwellMs;   // since lane-leave while still over the viewport (toward ContractBeginMs)
    public float AwayMs;           // since the pointer left the viewport (toward LeaveHideMs for hover-flash bars)
    public bool  ScrolledSinceReveal;   // a real scroll happened while visible → WinUI 2s idle hide applies

    // Eased tracks: value animates From → Target over the given duration; ClockMs counts up.
    public float ExpandFrom, ExpandTarget, ExpandClockMs;
    public float FadeFrom, FadeTarget, FadeClockMs;
}

/// <summary>Per-node-index side table of <see cref="ScrollBarChromeRow"/> (scroll-v3-plan §3.1). Always present on
/// <see cref="SceneStore"/> (<c>SceneStore.ScrollChrome</c>) — construction is a bare empty <see cref="Dictionary{TKey,TValue}"/>.
/// <see cref="SceneStore"/> clears a node's row on scroll-row removal (symmetric with the kernel's Unbind post);
/// <see cref="ScrollBarChrome"/> is the only other writer (via <see cref="GetOrAddRow"/>).</summary>
public sealed class ScrollBarChromeTable
{
    private readonly Dictionary<int, ScrollBarChromeRow> _rows = new();

    public bool TryGet(int node, out ScrollBarChromeRow row) => _rows.TryGetValue(node, out row);
    public ScrollBarChromeRow Get(int node) => _rows.TryGetValue(node, out var row) ? row : default;
    public void Clear(int node) => _rows.Remove(node);

    /// <summary>Ref access for <see cref="ScrollBarChrome.Tick"/> — internal, the table's row shape is chrome's own
    /// concern; everyone else reads by value via <see cref="Get"/>.</summary>
    internal ref ScrollBarChromeRow GetOrAddRow(int node) => ref CollectionsMarshal.GetValueRefOrAddDefault(_rows, node, out _);
}

/// <summary>
/// The scrollbar "conscious" state-machine ticker (scroll-v3-plan §3.1/§4/§10 WP-B item 4) — the WinUI
/// ScrollBar_themeresources.xaml dwell/expand/fade timing, ported VERBATIM from the pre-v3
/// <c>FluentGpu.Animation.ScrollIntegrator</c> (legacy snapshot <c>ScrollIntegrator.cs:59-71</c> constants,
/// <c>:677-777</c> the FSM itself). Chrome is a pure UI-side ticker: it reads <see cref="ScrollState"/>'s geometry
/// (Content*/Viewport*/Orientation) and its own <see cref="ScrollBarChromeRow.MotionStamp"/> (see
/// <see cref="NotifyMoved"/>) but NEVER writes Offset/Band/Zoom/Activity — motion is the kernel's alone
/// (<see cref="SceneScrollSink"/>).
/// </summary>
public sealed class ScrollBarChrome
{
    // WinUI ScrollBar_themeresources.xaml timing constants (ScrollIntegrator.cs:59-71 verbatim).
    public const float ExpandBeginMs = 400f;     // ScrollBarExpandBeginTime
    public const float ContractBeginMs = 500f;   // ScrollBarContractBeginTime
    public const float ExpandContractMs = 167f;  // ScrollBarExpandDuration / ScrollBarContractDuration
    public const float FadeMs = 83f;             // ScrollBarOpacityChangeDuration
    public const float IdleHideMs = 2000f;       // ScrollBarContractDelay — after a scroll, pointer away
    /// <summary>Engine-deliberate hover-flash retire delay: contract-begin + contract.</summary>
    public const float LeaveHideMs = ContractBeginMs + ExpandContractMs;
    /// <summary>Minimum overflow (content − viewport, DIP) before the conscious scrollbar may arm.</summary>
    public const float MinBarOverflowPx = 4f;

    private readonly SceneStore _scene;
    private readonly List<int> _active = new();
    private readonly HashSet<int> _member = new();
    private int _needsFrameCount;                    // see NeedsFrame — snapshot taken at the end of the last Tick()
    private float _nextDeadlineMs = float.PositiveInfinity;  // see NextDeadlineMs
    /// <summary>Set the instant a row transitions into needing a tick (a REAL PointerOver/PointerOverScrollbar
    /// flip, or any <see cref="NotifyMoved"/>(true)), cleared at the top of the next <see cref="Tick"/>. See
    /// <see cref="NeedsFrame"/>'s remarks — this is what makes a fresh reveal wake the loop on the SAME frame it
    /// happens, instead of one frame late (which would mean it never wakes at all, since nothing else asks for a
    /// frame in between).</summary>
    private bool _armedSinceTick;

    public ScrollBarChrome(SceneStore scene) => _scene = scene;

    /// <summary>The current frame's index — set by the host once per frame (same counter tick as
    /// <c>SceneScrollSink.FrameIndex</c>, bumped together — see <c>AppHost.Paint</c>'s scroll block), so "moved this
    /// frame" is <c>cs.MotionStamp == FrameIndex</c> (see <see cref="NotifyMoved"/>) without chrome ever touching
    /// motion itself.</summary>
    public uint FrameIndex { get; set; }

    /// <summary>True while any viewport has a pending timer/track (a live conscious cycle) — membership only, i.e.
    /// a row can be <see cref="Active"/> for one frame purely because something re-<see cref="Arm"/>ed it (a hover
    /// poll re-affirming an UNCHANGED PointerOver, or <see cref="NotifyMoved"/>) even though, once <see cref="Tick"/>
    /// actually runs, it settles straight back out with nothing left to animate. NOT a wake signal by itself — use
    /// <see cref="NeedsFrame"/> for that (scroll-v3 idle-power finding: a parked cursor over a hovered, fully
    /// faded-in, idle bar must not hold the render loop awake forever). Kept for existing non-wake callers
    /// (diagnostics/census) that want raw membership.</summary>
    public bool Active => _active.Count > 0;

    /// <summary>Count of viewports with a live conscious cycle — the chrome half of the combined scroll-animator
    /// census (<c>AppHost.ScrollActiveCensus</c> = kernel <c>ActiveCount</c> + this), since a revealed-but-
    /// motionless bar (armed purely by hover, or by <see cref="NotifyMoved"/> after the kernel body itself already
    /// settled) is invisible to the kernel's own count. Raw membership — see <see cref="Active"/>'s remarks on why
    /// this is not the wake signal.</summary>
    public int Count => _active.Count;

    /// <summary>Wake-purposed signal, a strict subset of <see cref="Active"/>: true iff there is real chrome work
    /// pending RIGHT NOW — either (a) <see cref="_armedSinceTick"/>: a genuine state transition happened since the
    /// last <see cref="Tick"/> (a REAL PointerOver/PointerOverScrollbar flip via <see cref="SetPointerOver"/>, or
    /// any <see cref="NotifyMoved"/>(true)) that <see cref="Tick"/> has not yet had a chance to process, or (b) as of
    /// the end of the last <see cref="Tick"/>, at least one row still has work pending — moving this frame, an
    /// in-flight fade or expand/contract track, or an unsettled dwell timer counting toward the expand or hide
    /// thresholds (exactly <see cref="Tick"/>'s own "keep it armed" predicate, the negation of its <c>Drop</c>
    /// condition: <c>!movingNow &amp;&amp; expandSettled &amp;&amp; fadeSettled &amp;&amp; !dwellPending</c>).
    /// <para>Term (a) exists because term (b) alone is a snapshot from the END of the PREVIOUS <see cref="Tick"/> —
    /// without it, a fresh reveal (pointer just entered a scrollable viewport, or a wheel notch just landed) would
    /// report false until AFTER a <see cref="Tick"/> ran, but the whole point of this property is to tell the host
    /// WHETHER to run one: nothing would ever wake the loop for the very first frame of a reveal. Term (a) is
    /// change-gated, not membership-gated (see <see cref="SetPointerOver"/>'s <c>changed</c> check) — a hover
    /// hit-test that re-reports an UNCHANGED PointerOver every frame does NOT set it, so a parked cursor resting
    /// over an already-revealed, fully faded-in, idle bar still reports false (the scroll-v3 idle-power finding this
    /// type exists to fix) even though such polling keeps re-<see cref="Arm"/>ing (and hence keeps <see cref="Active"/>
    /// true).</para>
    /// <see cref="AppHost.ComputeWakeReasons"/> should OR this in (not <see cref="Active"/>) for the scrollAnim
    /// chrome term.</summary>
    public bool NeedsFrame => _armedSinceTick || _needsFrameCount > 0;

    /// <summary>Milliseconds until the earliest pending hide/away deadline among rows NOT counted in
    /// <see cref="NeedsFrame"/> would fire (<see cref="float.PositiveInfinity"/> if none is pending) — snapshotted
    /// at the end of the last <see cref="Tick"/>. A hovered idle bar has none (its IdleMs/AwayMs is held at 0 by
    /// <c>over</c>, so nothing is counting down — see <see cref="NeedsFrame"/>'s remarks); the case this exists for
    /// is a bar that just left hover/lane while still visible (fading out is already dwellPending ⇒ counted in
    /// <see cref="NeedsFrame"/> and ticked every frame like today) — this is reserved for a future refinement where
    /// that count-only-waiting state is ALSO pulled out of per-frame ticking in favor of one timer wake at the
    /// deadline; today it is always <see cref="float.PositiveInfinity"/> because that state is still folded into
    /// <see cref="NeedsFrame"/> (see the TODO on <see cref="Tick"/>). Exposed now so <c>AppHost</c>'s timer-wake
    /// scheduling (<c>HostTimerQueue</c> or equivalent) has a seam to consume once that refinement lands, without a
    /// second API change to this type.</summary>
    public float NextDeadlineMs => _nextDeadlineMs;

    /// <summary>Hover state changed for this viewport (dispatcher's <c>UpdateScrollHover</c>): arms/keeps the node
    /// ticking until its conscious cycle fully settles.</summary>
    public void SetPointerOver(int node, bool over, bool overLane)
    {
        ref var row = ref _scene.ScrollChrome.GetOrAddRow(node);
        bool changed = row.PointerOver != over || row.PointerOverScrollbar != overLane;
        row.PointerOver = over;
        row.PointerOverScrollbar = overLane;
        Arm(node);
        // Only a REAL flip needs a frame — a hover-hit-test re-reporting the SAME over/overLane every frame must not
        // perpetually re-dirty an already-settled, fully faded-in bar (see NeedsFrame's remarks).
        if (changed) _armedSinceTick = true;
    }

    /// <summary>Called by <see cref="SceneScrollSink.Apply"/> for every kernel-touched node (see
    /// <see cref="SceneScrollSink.Chrome"/>), with <paramref name="moved"/> true iff THAT call's write actually
    /// changed offset/band/zoom (not merely an idempotent geometry re-touch — see the remarks on
    /// <see cref="SceneScrollSink.Chrome"/> for why that distinction can't be read off <c>ScrollState.LastMovedFrame</c>
    /// alone: the kernel's write mask sets it on EVERY touch, including a same-geometry <c>SetFrame</c> repost). A
    /// no-op when <paramref name="moved"/> is false — a geometry-only touch must neither reveal an idle bar nor keep
    /// an already-fading one alive. When true, stamps <see cref="ScrollBarChromeRow.MotionStamp"/> — what
    /// <see cref="Tick"/> compares against <see cref="FrameIndex"/> for "moved this frame" — and arms the node into
    /// the ticker exactly like a hover event does, WITHOUT touching PointerOver/PointerOverScrollbar. This is the
    /// ONLY way a wheel scroll or a touch pan (neither of which ever sets hover — touch never latches PointerOver at
    /// all, and a wheel notch can land with no prior PointerMove) reveals the thin indicator. Still an identity+bool
    /// signal, not a raw motion value, so "chrome never touches motion, motion never touches chrome" holds at the
    /// VALUE level.</summary>
    public void NotifyMoved(int node, bool moved)
    {
        if (!moved) return;
        ref var row = ref _scene.ScrollChrome.GetOrAddRow(node);
        row.MotionStamp = FrameIndex;
        Arm(node);
        // moved is already gated to true-only by the early return above — every call reaching here is a genuine new
        // event (a real scroll/wheel/touch delta), so it always needs a frame (see NeedsFrame's remarks).
        _armedSinceTick = true;
    }

    /// <summary>KeepAlive park edge (<c>TreeReconciler.OnNodeParkedChanged</c>). Parking lands the bar AT REST — hidden,
    /// contracted, hover forgotten — and retires its row: a parked page is off screen and its pointer state is gone (the
    /// reconciler lands authored hover poses the same way on both edges, <c>SetSubtreeParked</c>). The row used to be
    /// frozen mid-cycle instead, left in <see cref="_active"/> where <see cref="NeedsFrame"/> counted it: a page left
    /// within the 2 s idle-hide of a scroll held the render loop at panel rate for as long as it stayed parked, and for
    /// the rest of the session once KeepAlive evicted it — the frozen row was skipped before <see cref="Tick"/>'s
    /// liveness check, so it was never dropped (the <c>[wake]</c> census read <c>kept: scrollAnim</c> on every run with
    /// <c>idleAgo</c> never resetting). Un-parking restores nothing: the next hover or scroll starts a fresh cycle, and
    /// "is this viewport parked" is read off the scene's own <see cref="NodeFlags.Parked"/>, never a mirror here that a
    /// freed-and-reused node index could inherit.</summary>
    public void SetNodeParked(int node, bool parked)
    {
        if (!parked) return;
        if (_scene.ScrollChrome.TryGet(node, out var row))
        {
            // A visible or expanded bar changes pixels as it lands, so dirty the node: the un-park must re-record it
            // rather than replay a retained span that still shows the bar.
            if (row.FadeT != 0f || row.ExpandT != 0f)
            {
                NodeHandle h = _scene.HandleAt(node);
                if (!h.IsNull && _scene.IsLive(h)) _scene.Mark(h, NodeFlags.PaintDirty);
            }
            _scene.ScrollChrome.Clear(node);
        }
        if (_member.Remove(node)) _active.Remove(node);   // park edges are navigation-rate; _active holds a handful
    }

    private void Arm(int node)
    {
        if (_member.Add(node)) _active.Add(node);
    }

    private void Drop(int i, int node, bool forget)
    {
        _member.Remove(node);
        _active.RemoveAt(i);
        if (forget) _scene.ScrollChrome.Clear(node);
    }

    public void Tick(float dtMs)
    {
        // Consume the "armed since last Tick" latch — everything it was set for is about to be processed by this
        // very call (the rows it named are, by construction, already members of _active via Arm). See NeedsFrame's
        // remarks on why this must be a latch cleared here, not a live re-derivation.
        _armedSinceTick = false;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            int node = _active[i];
            NodeHandle h = _scene.HandleAt(node);
            if (h.IsNull || !_scene.IsLive(h) || !_scene.TryGetScroll(h, out var sc))
            {
                // The scroll row is gone (freed underneath us — SceneStore.FreeSubtree already cleared the chrome
                // row too) — drop the tracking entry without touching the (already-cleared) table row.
                Drop(i, node, forget: false);
                continue;
            }
            // Re-armed after its park edge (a hover leave, or a kernel touch reaching the parked body): an off-screen
            // bar has nothing to animate and SetNodeParked already landed it at rest. Retire it — every row still in
            // _active after this loop is counted by NeedsFrame.
            if ((_scene.Flags(h) & NodeFlags.Parked) != 0)
            {
                Drop(i, node, forget: true);
                continue;
            }

            ref var cs = ref _scene.ScrollChrome.GetOrAddRow(node);

            // NOT sc.LastMovedFrame == FrameIndex — that kernel column stamps on every SceneScrollSink.Apply TOUCH,
            // including an idempotent same-geometry SetFrame repost from a relayout, which would keep "moved this
            // frame" permanently true and defeat the AwayMs/IdleMs auto-hide entirely (never retiring). MotionStamp
            // is chrome's own truth, set only by NotifyMoved(moved:true) — see its remarks.
            bool movingNow = cs.MotionStamp == FrameIndex;
            bool over = cs.PointerOver;
            float overflow = sc.Orientation == 1 ? sc.ContentW - sc.ViewportW : sc.ContentH - sc.ViewportH;
            bool scrollable = overflow > MinBarOverflowPx;
            bool lane = cs.PointerOverScrollbar && scrollable;

            if (scrollable && movingNow) cs.ScrolledSinceReveal = true;

            // Expand/contract dwell timers (ScrollBarExpandBeginTime 400ms / ScrollBarContractBeginTime 500ms).
            if (lane)
            {
                cs.LaneDwellMs = MathF.Min(ExpandBeginMs, cs.LaneDwellMs + dtMs);
                cs.LaneOffDwellMs = 0f;
                if (cs.LaneDwellMs >= ExpandBeginMs && cs.ExpandTarget != 1f)
                    StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, cs.ExpandT, 1f);
            }
            else
            {
                cs.LaneDwellMs = 0f;
                if (cs.ExpandTarget != 0f || cs.ExpandT > 0f)
                {
                    if (over)
                    {
                        cs.LaneOffDwellMs += dtMs;
                        if (cs.LaneOffDwellMs >= ContractBeginMs && cs.ExpandTarget != 0f)
                            StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, cs.ExpandT, 0f);
                    }
                    else if (cs.ExpandTarget != 0f)
                    {
                        // Viewport-leave: contract immediately (engine-deliberate; class remarks in the legacy source).
                        StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, cs.ExpandT, 0f);
                    }
                }
            }

            // Visibility: visible while moving / lane / over (the MouseIndicator hold); hide after the away/idle delay.
            cs.IdleMs = (movingNow || over) ? 0f : cs.IdleMs + dtMs;
            cs.AwayMs = over ? 0f : cs.AwayMs + dtMs;
            bool show = scrollable && (movingNow || over || lane);
            bool hideDue = !show &&
                ((!scrollable && !movingNow)
                 || (cs.ScrolledSinceReveal ? cs.IdleMs >= IdleHideMs
                                            : cs.AwayMs >= LeaveHideMs));
            float fadeWant = show ? 1f : hideDue ? 0f : cs.FadeT > 0f ? 1f : 0f;
            if (fadeWant != cs.FadeTarget) StartTrack(ref cs.FadeFrom, ref cs.FadeTarget, ref cs.FadeClockMs, cs.FadeT, fadeWant);

            // Advance the eased tracks: expand = 167ms KeySpline(0,0,0,1) → FluentPopOpen; fade = 83ms linear.
            float oldExpand = cs.ExpandT, oldFade = cs.FadeT;
            cs.ExpandT = Advance(ref cs.ExpandFrom, cs.ExpandTarget, ref cs.ExpandClockMs, ExpandContractMs, dtMs, Easing.FluentPopOpen, cs.ExpandT);
            cs.FadeT = Advance(ref cs.FadeFrom, cs.FadeTarget, ref cs.FadeClockMs, FadeMs, dtMs, Easing.Linear, cs.FadeT);
            if (cs.ExpandT != oldExpand || cs.FadeT != oldFade) _scene.Mark(h, NodeFlags.PaintDirty);

            bool expandSettled = cs.ExpandT == cs.ExpandTarget;
            bool fadeSettled = cs.FadeT == cs.FadeTarget;
            bool fullyHidden = fadeSettled && cs.FadeT == 0f && expandSettled && cs.ExpandT == 0f;
            if (fullyHidden)
            {
                // Reset the FSM timers only — a fresh conscious cycle starts clean next reveal. PointerOver/
                // PointerOverScrollbar are NOT reset here (they track live hover state, set by SetPointerOver, not
                // by this FSM) — same split as the pre-v3 ScrollIntegrator, where they lived on ScrollState and were
                // never touched by `cs = default`.
                cs.LaneDwellMs = 0f; cs.LaneOffDwellMs = 0f; cs.AwayMs = 0f; cs.ScrolledSinceReveal = false;
                cs.ExpandFrom = 0f; cs.ExpandTarget = 0f; cs.ExpandClockMs = 0f;
                cs.FadeFrom = 0f; cs.FadeTarget = 0f; cs.FadeClockMs = 0f;
            }

            // A pending dwell that will still change state keeps the node armed (timers need ticks to elapse).
            bool dwellPending =
                (lane && cs.LaneDwellMs < ExpandBeginMs && cs.ExpandTarget != 1f) ||
                (!lane && over && cs.ExpandT > 0f && cs.ExpandTarget != 0f) ||
                (!show && cs.FadeT > 0f && !hideDue);

            if (!movingNow && expandSettled && fadeSettled && !dwellPending)
                Drop(i, node, forget: fullyHidden);
        }

        // NeedsFrame snapshot: every row still in _active at this point is live, NOT parked, and satisfies the negation
        // of the Drop condition above (movingNow || !expandSettled || !fadeSettled || dwellPending) — i.e. it genuinely
        // has work pending for the next frame. A row that was merely re-Arm()ed this frame (unchanged PointerOver from
        // a hover poll, or a NotifyMoved with nothing left to animate) already settled back out of _active in the
        // loop above, so it is correctly excluded here — see NeedsFrame's remarks.
        _needsFrameCount = _active.Count;
        // NextDeadlineMs: the "tick every frame just to notice a hide countdown" case is still folded into
        // dwellPending/NeedsFrame above (no correctness regression — the countdown is still observed every frame,
        // same as before this change), so there is nothing to schedule a one-shot timer wake FOR yet. Reserved for
        // the refinement described on NextDeadlineMs.
        _nextDeadlineMs = float.PositiveInfinity;
    }

    /// <summary>Retarget an eased track from the live value (mid-flight retargets stay continuous).</summary>
    private static void StartTrack(ref float from, ref float target, ref float clockMs, float current, float to)
    {
        from = current;
        target = to;
        clockMs = 0f;
    }

    private static float Advance(ref float from, float target, ref float clockMs, float durationMs, float dtMs, Easing easing, float current)
    {
        if (current == target) return current;
        clockMs += dtMs;
        float t = System.Math.Clamp(clockMs / MathF.Max(1f, durationMs), 0f, 1f);
        if (t >= 1f) return target;
        return from + (target - from) * Easings.Ease(easing, t);
    }
}
