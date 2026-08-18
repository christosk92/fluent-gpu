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
    private readonly HashSet<int> _parked = new();   // KeepAlive-parked: excluded from Tick

    public ScrollBarChrome(SceneStore scene) => _scene = scene;

    /// <summary>The current frame's index — set by the host once per frame (same counter tick as
    /// <c>SceneScrollSink.FrameIndex</c>, bumped together — see <c>AppHost.Paint</c>'s scroll block), so "moved this
    /// frame" is <c>cs.MotionStamp == FrameIndex</c> (see <see cref="NotifyMoved"/>) without chrome ever touching
    /// motion itself.</summary>
    public uint FrameIndex { get; set; }

    /// <summary>True while any viewport has a pending timer/track (a live conscious cycle) — the host's wake-reason
    /// gate ORs this in alongside the kernel's own ActiveCount.</summary>
    public bool Active => _active.Count > 0;

    /// <summary>Count of viewports with a live conscious cycle — the chrome half of the combined scroll-animator
    /// census (<c>AppHost.ScrollActiveCensus</c> = kernel <c>ActiveCount</c> + this), since a revealed-but-
    /// motionless bar (armed purely by hover, or by <see cref="NotifyMoved"/> after the kernel body itself already
    /// settled) is invisible to the kernel's own count.</summary>
    public int Count => _active.Count;

    /// <summary>Hover state changed for this viewport (dispatcher's <c>UpdateScrollHover</c>): arms/keeps the node
    /// ticking until its conscious cycle fully settles.</summary>
    public void SetPointerOver(int node, bool over, bool overLane)
    {
        ref var row = ref _scene.ScrollChrome.GetOrAddRow(node);
        row.PointerOver = over;
        row.PointerOverScrollbar = overLane;
        Arm(node);
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
    }

    /// <summary>KeepAlive-parked exclusion: a parked subtree's bar is frozen mid-cycle, not ticked or settled
    /// (ScrollIntegrator._parkedActive parity).</summary>
    public void SetNodeParked(int node, bool parked)
    {
        if (parked) _parked.Add(node);
        else _parked.Remove(node);
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
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            int node = _active[i];
            if (_parked.Contains(node)) continue;

            NodeHandle h = _scene.HandleAt(node);
            if (h.IsNull || !_scene.IsLive(h) || !_scene.TryGetScroll(h, out var sc))
            {
                // The scroll row is gone (freed underneath us — SceneStore.FreeSubtree already cleared the chrome
                // row too) — drop the tracking entry without touching the (already-cleared) table row.
                Drop(i, node, forget: false);
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
