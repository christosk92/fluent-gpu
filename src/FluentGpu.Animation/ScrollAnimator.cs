using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Smooth scrolling (phase 7): eases each armed viewport's live offset toward its target (the WinUI inertial wheel feel),
/// applies the content's -offset transform, re-realizes the virtual window at guard-band edges, and drives the
/// auto-hiding scrollbar's WinUI "conscious" state machine. Only viewports actively scrolling/transitioning are ticked;
/// zero work / zero alloc once everything settles (<see cref="HasActive"/> == false).
///
/// Conscious-scrollbar timing — verified against microsoft-ui-xaml
/// controls\dev\CommonStyles\ScrollBar_themeresources.xaml:
/// <list type="bullet">
/// <item>Expand: pointer dwells over the scrollbar lane for ScrollBarExpandBeginTime = 400ms (:188), then the thumb
/// grows / track+buttons fade in over ScrollBarExpandDuration = 167ms (:173) with KeySpline 0,0,0,1 (:587) —
/// <see cref="Easing.FluentPopOpen"/>.</item>
/// <item>Contract (lane left, pointer still inside the viewport): begins after ScrollBarContractBeginTime = 500ms
/// (:189), plays over ScrollBarContractDuration = 167ms (:176), same spline (:543).</item>
/// <item>Indicator fades over ScrollBarOpacityChangeDuration = 83ms (:174), linear (the storyboards' plain
/// DoubleAnimation).</item>
/// <item>Idle auto-hide after a SCROLL: ScrollBarContractDelay = 2s (:178; ScrollViewer's SeparatorContractDelay is
/// the same 2s — ScrollViewer_themeresources.xaml) then the 83ms fade.</item>
/// <item>ENGINE-DELIBERATE (documented deviation): when the pointer leaves the whole viewport and the bar was revealed
/// by hover alone (no scroll since it appeared), it contracts immediately and retires after
/// ContractBeginTime + ContractDuration (≈667ms) + the 83ms fade — WinUI would hold the expanded bar 500ms and the
/// thin indicator the full 2s; an untouched hover-flash retiring early reads cleaner and keeps the frame loop idle.
/// Scroll-revealed bars keep the WinUI 2s ContractDelay.</item>
/// <item>While the pointer rests inside the viewport the thin indicator stays visible (the WinUI MouseIndicator
/// hold); it never idle-hides under the pointer.</item>
/// </list>
/// </summary>
public sealed class ScrollAnimator
{
    // WinUI ScrollBar_themeresources.xaml timing constants (see class remarks for line cites).
    public const float ExpandBeginMs = 400f;     // ScrollBarExpandBeginTime (:188)
    public const float ContractBeginMs = 500f;   // ScrollBarContractBeginTime (:189)
    public const float ExpandContractMs = 167f;  // ScrollBarExpandDuration / ScrollBarContractDuration (:173/:176)
    public const float FadeMs = 83f;             // ScrollBarOpacityChangeDuration (:174)
    public const float IdleHideMs = 2000f;       // ScrollBarContractDelay (:178) — after a scroll, pointer away
    /// <summary>Engine-deliberate hover-flash retire delay (see class remarks): contract-begin + contract.</summary>
    public const float LeaveHideMs = ContractBeginMs + ExpandContractMs;

    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _active = new();
    private readonly HashSet<int> _member = new();
    private readonly Dictionary<int, Conscious> _state = new();   // per-viewport conscious-bar timers (cold; survives drops)

    /// <summary>Per-viewport conscious-state timers + eased FadeT/ExpandT tracks (kept out of ScrollState so the
    /// scene columns stay engine-owned PODs; entries are reclaimed when the bar fully hides or the node dies).</summary>
    private struct Conscious
    {
        public float LaneDwellMs;      // continuous lane hover (toward ExpandBeginMs)
        public float LaneOffDwellMs;   // since lane-leave while still over the viewport (toward ContractBeginMs)
        public float AwayMs;           // since the pointer left the viewport (toward LeaveHideMs for hover-flash bars)
        public bool ScrolledSinceReveal;   // a real scroll happened while visible → WinUI 2s idle hide applies

        // Eased tracks: value animates From → Target over the given duration; ClockMs counts up.
        public float ExpandFrom, ExpandTarget, ExpandClockMs;
        public float FadeFrom, FadeTarget, FadeClockMs;
    }

    public ScrollAnimator(SceneStore scene) => _scene = scene;
    public Action RequestRerender { get; set; } = static () => { };
    public bool HasActive => _active.Count > 0;

    public void Arm(NodeHandle n)
    {
        if (!n.IsNull && _scene.IsLive(n) && _member.Add((int)n.Raw.Index)) _active.Add(n);
    }

    /// <summary>Reveal a viewport's scrollbar (pointer is over the scrollable area) and reset its idle timer so it
    /// stays up while hovered; lane hover arms the WinUI 400ms-delayed expand.</summary>
    public void Hover(NodeHandle n, bool overScrollbar)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        if (sc.ContentH <= sc.ViewportH + 0.5f && sc.ContentW <= sc.ViewportW + 0.5f) return;  // nothing to scroll
        sc.IdleMs = 0f;
        sc.PointerOver = true;
        sc.PointerOverScrollbar = overScrollbar;
        Arm(n);
    }

    public void Leave(NodeHandle n)
    {
        if (n.IsNull || !_scene.IsLive(n) || !_scene.HasScroll(n)) return;
        ref ScrollState sc = ref _scene.ScrollRef(n);
        sc.PointerOver = false;
        sc.PointerOverScrollbar = false;
        Arm(n);
    }

    public void Tick(float dtMs)
    {
        if (_active.Count == 0) return;
        float kOff = 1f - MathF.Exp(-dtMs / 90f);     // offset smoothing (~150ms feel)
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            NodeHandle n = _active[i];
            if (!_scene.IsLive(n)) { Drop(i, n, forget: true); continue; }
            ref ScrollState sc = ref _scene.ScrollRef(n);
            bool horizontal = sc.Orientation == 1;
            float off = horizontal ? sc.OffsetX : sc.OffsetY;
            float tgt = horizontal ? sc.TargetX : sc.TargetY;
            float oldOff = off;
            off += (tgt - off) * kOff;
            if (MathF.Abs(tgt - off) < 0.5f) off = tgt;
            bool moved = off != oldOff;
            if (horizontal) sc.OffsetX = off; else sc.OffsetY = off;

            if (moved)
            {
                var content = sc.ContentNode;
                if (!content.IsNull && _scene.IsLive(content))
                {
                    ref NodePaint cp = ref _scene.Paint(content);
                    cp.LocalTransform = Affine2D.Translation(horizontal ? -off : 0f, horizontal ? 0f : -off);
                    _scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                }
                if (sc.ItemCount > 0)   // virtualization: re-realize only when visible range approaches realized-window edge
                {
                    int visibleFirst, visibleLast;
                    float vp = horizontal ? sc.ViewportW : sc.ViewportH;
                    if (sc.Layout is not null)
                    {
                        float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                        sc.Layout.Window(sc.ItemCount, cross, vp, off, 0, out visibleFirst, out visibleLast);
                    }
                    else if (_scene.TryGetExtents(n, out var t) && t is not null) { visibleFirst = t.IndexAt(off); visibleLast = Math.Min(sc.ItemCount, t.IndexAt(off + vp) + 1); }
                    else { visibleFirst = visibleLast = 0; }
                    if (VirtualWindowing.NeedsRealize(in sc, visibleFirst, visibleLast)) { _scene.Mark(n, NodeFlags.VirtualRangeDirty); RequestRerender(); }
                }
            }

            // ── conscious scrollbar state machine (WinUI timings; see class remarks) ──────────────────────
            int key = (int)n.Raw.Index;
            _state.TryGetValue(key, out Conscious cs);
            bool movingNow = MathF.Abs(tgt - off) > 0.5f;
            bool over = sc.PointerOver;
            bool lane = sc.PointerOverScrollbar;

            if (moved || movingNow) cs.ScrolledSinceReveal = true;

            // Expand/contract dwell timers (ScrollBarExpandBeginTime 400ms / ScrollBarContractBeginTime 500ms).
            if (lane)
            {
                cs.LaneDwellMs = MathF.Min(ExpandBeginMs, cs.LaneDwellMs + dtMs);
                cs.LaneOffDwellMs = 0f;
                if (cs.LaneDwellMs >= ExpandBeginMs && cs.ExpandTarget != 1f) StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, sc.ExpandT, 1f);
            }
            else
            {
                cs.LaneDwellMs = 0f;
                if (cs.ExpandTarget != 0f || sc.ExpandT > 0f)
                {
                    if (over)
                    {
                        cs.LaneOffDwellMs += dtMs;
                        if (cs.LaneOffDwellMs >= ContractBeginMs && cs.ExpandTarget != 0f)
                            StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, sc.ExpandT, 0f);
                    }
                    else if (cs.ExpandTarget != 0f)
                    {
                        // Viewport-leave: contract immediately (engine-deliberate; class remarks).
                        StartTrack(ref cs.ExpandFrom, ref cs.ExpandTarget, ref cs.ExpandClockMs, sc.ExpandT, 0f);
                    }
                }
            }

            // Visibility: visible while moving / lane / over (the MouseIndicator hold); hide after the away/idle delay.
            sc.IdleMs = (movingNow || over) ? 0f : sc.IdleMs + dtMs;
            cs.AwayMs = over ? 0f : cs.AwayMs + dtMs;
            bool show = movingNow || over || lane;
            bool hideDue = !show &&
                (cs.ScrolledSinceReveal ? sc.IdleMs >= IdleHideMs       // WinUI ScrollBarContractDelay 2s
                                        : cs.AwayMs >= LeaveHideMs);    // hover-flash retire (≈667ms; class remarks)
            float fadeWant = show ? 1f : hideDue ? 0f : sc.FadeT > 0f ? 1f : 0f;
            if (fadeWant != cs.FadeTarget) StartTrack(ref cs.FadeFrom, ref cs.FadeTarget, ref cs.FadeClockMs, sc.FadeT, fadeWant);

            // Advance the eased tracks: expand = 167ms KeySpline(0,0,0,1) → FluentPopOpen; fade = 83ms linear.
            float oldExpand = sc.ExpandT, oldFade = sc.FadeT;
            sc.ExpandT = Advance(ref cs.ExpandFrom, cs.ExpandTarget, ref cs.ExpandClockMs, ExpandContractMs, dtMs, Easing.FluentPopOpen, sc.ExpandT);
            sc.FadeT = Advance(ref cs.FadeFrom, cs.FadeTarget, ref cs.FadeClockMs, FadeMs, dtMs, Easing.Linear, sc.FadeT);
            if (sc.ExpandT != oldExpand || sc.FadeT != oldFade) _scene.Mark(n, NodeFlags.PaintDirty);

            bool expandSettled = sc.ExpandT == cs.ExpandTarget;
            bool fadeSettled = sc.FadeT == cs.FadeTarget;
            bool fullyHidden = fadeSettled && sc.FadeT == 0f && expandSettled && sc.ExpandT == 0f;
            if (fullyHidden) cs = default;   // reset all timers — the next reveal starts a fresh conscious cycle
            _state[key] = cs;

            // A pending dwell that will still change state keeps the node armed (timers need ticks to elapse).
            bool dwellPending =
                (lane && cs.LaneDwellMs < ExpandBeginMs && cs.ExpandTarget != 1f) ||
                (!lane && over && sc.ExpandT > 0f && cs.ExpandTarget != 0f) ||
                (!show && sc.FadeT > 0f && !hideDue);

            if (!movingNow && expandSettled && fadeSettled && !dwellPending)
            {
                Drop(i, n, forget: fullyHidden);
            }   // fully settled: statically visible under hover, or hidden after the delays
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
        float t = Math.Clamp(clockMs / MathF.Max(1f, durationMs), 0f, 1f);
        if (t >= 1f) return target;
        return from + (target - from) * Easings.Ease(easing, t);
    }

    private void Drop(int i, NodeHandle n, bool forget)
    {
        _member.Remove((int)n.Raw.Index);
        _active.RemoveAt(i);
        if (forget) _state.Remove((int)n.Raw.Index);
    }
}
