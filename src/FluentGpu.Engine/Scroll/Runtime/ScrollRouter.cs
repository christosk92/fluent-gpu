using FluentGpu.Foundation;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Scroll.Runtime;

/// <summary>The scroller tree as the router sees it — an abstraction over the last laid-out scene so the routing
/// decision is pure and unit-testable.</summary>
public interface IScrollerQuery
{
    /// <summary>The nearest ancestor scroller of <paramref name="vp"/>, or -1 at the root.</summary>
    int ParentScroller(int vp);

    /// <summary>True when <paramref name="vp"/> can still move along <paramref name="horizontal"/>'s axis in
    /// direction <paramref name="sign"/> (+1 toward the content end, -1 toward the start) at <paramref name="tNow"/>,
    /// i.e. its plan's displayed position is not already at that edge (and the axis is scrollable at all).</summary>
    bool CanMove(int vp, bool horizontal, int sign, double tNow);

    /// <summary>Who <paramref name="vp"/> is while it can take input: a nonzero identity (its node generation) while
    /// the scroller is mounted and on screen, 0 once it is gone (unmounted, parked by a navigation). A latch holds a
    /// scroller by index across a gesture; this is how the router knows the index still means THAT scroller.</summary>
    uint Identity(int vp) => 1;
}

/// <summary>The router's answer: which scroller receives the input, on which axis. <see cref="Vp"/> is -1 when
/// there is no scroller at all under the hit.</summary>
public readonly record struct RouteDecision(int Vp, bool Horizontal)
{
    public bool IsNone => Vp < 0;
}

/// <summary>
/// Pure UI-thread routing (design §4): picks the scroller an input drives and latches it for the gesture.
/// <list type="bullet">
/// <item>Nearest scroller on the axis that can still move at <c>tNow</c>, walking up from the hit; else the outermost.</item>
/// <item>Latch: a wheel latch holds until <c>WheelLatchSilenceS</c> of notch silence; a contact latch holds
/// <see cref="ScrollGesture.Begin"/> → <see cref="ScrollGesture.End"/>.</item>
/// <item>Chain to the parent ONLY when the latched child is at its edge AND the gesture began at that edge (in that
/// direction). A shelf that reaches its end mid-spin does not hand off — the notch is absorbed (WinUI shelf rule).</item>
/// <item>Shift + wheel routes horizontal.</item>
/// <item>Keyboard/Thumb/Programmatic inputs are not latched: they go to the hit (focused/owning) scroller, walking up
/// only when it cannot move.</item>
/// </list>
/// One instance per window; no allocation after construction.
/// </summary>
public sealed class ScrollRouter
{
    private readonly IScrollerQuery _q;
    private readonly double? _wheelLatchSilenceS;

    private int _latchVp = -1;          // the HIT scroller the gesture latched on (chaining resolves from here)
    private uint _latchId;               // its IScrollerQuery.Identity when it latched
    private bool _latchHorizontal;
    private sbyte _latchEdgeSign;        // the direction the latched scroller was ALREADY at the edge of when the gesture began (0 = it could move)
    private double _latchLastT;
    private bool _latchContact;          // a Begin→End contact latch (does not expire on silence)

    /// <summary><paramref name="wheelLatchSilenceS"/> null reads <see cref="ScrollTunables.Current"/> per call (live-tunable).</summary>
    public ScrollRouter(IScrollerQuery query, double? wheelLatchSilenceS = null)
    {
        _q = query;
        _wheelLatchSilenceS = wheelLatchSilenceS;
    }

    /// <summary>The scroller currently latched, or -1.</summary>
    public int LatchedScroller => _latchVp;

    /// <summary>Drops any latch (e.g. on focus loss / window deactivate).</summary>
    public void ResetLatch()
    {
        _latchVp = -1;
        _latchContact = false;
        _latchEdgeSign = 0;
    }

    /// <summary>Routes a wheel notch (the simple form the spec names): the scroller to drive, or -1.</summary>
    public int Route(int hitScroller, bool horizontal, int sign, double tNow, ScrollSource src)
        => Decide(hitScroller, horizontal, sign, tNow, src, ScrollGesture.Notch, KeyModifiers.None).Vp;

    /// <summary>Full routing decision for one input.</summary>
    public RouteDecision Decide(int hitScroller, bool horizontal, int sign, double tNow, ScrollSource src, ScrollGesture phase, KeyModifiers mods)
    {
        bool isWheel = src is ScrollSource.MouseWheel or ScrollSource.MouseWheelHiRes;
        bool isContact = src is ScrollSource.Touchpad or ScrollSource.Touch or ScrollSource.Pen;
        if (isWheel && (mods & KeyModifiers.Shift) != 0) horizontal = true;

        if (!isWheel && !isContact)
        {
            // Keyboard / thumb / programmatic: no latch; the owning scroller, walking up only if it is pinned.
            return new RouteDecision(NearestMovable(hitScroller, horizontal, sign, tNow), horizontal);
        }

        // A latch whose scroller is gone (the page it scrolled was navigated away from, or freed and its index reused)
        // can never take input again: drop it. A wheel then latches on what is under the pointer now; the rest of a
        // contact that lost its scroller is dropped until the next Begin (it never re-targets mid-gesture).
        if (_latchVp >= 0 && _q.Identity(_latchVp) != _latchId)
        {
            bool contact = _latchContact;
            ResetLatch();
            if (contact && phase != ScrollGesture.Begin) return new RouteDecision(-1, horizontal);
        }

        // Expire / refresh the latch.
        if (_latchVp >= 0)
        {
            bool expired;
            if (_latchContact) expired = !isContact || phase == ScrollGesture.Begin;
            else expired = isContact || (tNow - _latchLastT) > WheelLatchSilenceS || _latchHorizontal != horizontal;
            if (expired) ResetLatch();
        }

        if (_latchVp < 0)
        {
            if (hitScroller < 0) return new RouteDecision(-1, horizontal);
            _latchVp = hitScroller;
            _latchId = _q.Identity(hitScroller);
            _latchHorizontal = horizontal;
            _latchEdgeSign = _q.CanMove(hitScroller, horizontal, sign, tNow) ? (sbyte)0 : (sbyte)sign;
            _latchContact = isContact;
        }
        _latchLastT = tNow;

        int target = Resolve(_latchVp, horizontal, sign, tNow);

        if (isContact && phase == ScrollGesture.End) ResetLatch();
        return new RouteDecision(target, horizontal);
    }

    /// <summary>Chaining: the latched child while it can move; the nearest movable ancestor when the child is at
    /// the edge it was ALREADY at when the gesture began (in this direction); otherwise the child itself (the input is
    /// absorbed at the edge — no mid-gesture hand-off).</summary>
    private int Resolve(int latched, bool horizontal, int sign, double tNow)
    {
        if (_q.CanMove(latched, horizontal, sign, tNow)) return latched;
        if (_latchEdgeSign != 0 && _latchEdgeSign == sign)
        {
            int parent = _q.ParentScroller(latched);
            while (parent >= 0)
            {
                if (_q.CanMove(parent, horizontal, sign, tNow)) return parent;
                parent = _q.ParentScroller(parent);
            }
        }
        return latched;
    }

    /// <summary>The nearest scroller from <paramref name="hit"/> upward that can move; else the outermost ancestor
    /// of <paramref name="hit"/> (so a pinned tree still owns the input rather than dropping it).</summary>
    private int NearestMovable(int hit, bool horizontal, int sign, double tNow)
    {
        if (hit < 0) return -1;
        int cur = hit, outermost = hit;
        while (cur >= 0)
        {
            if (_q.CanMove(cur, horizontal, sign, tNow)) return cur;
            outermost = cur;
            cur = _q.ParentScroller(cur);
        }
        return outermost;
    }

    private double WheelLatchSilenceS => _wheelLatchSilenceS ?? ScrollTunables.Current.WheelLatchSilenceS;

    // ── keyboard mapping ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Maps a virtual key (<see cref="Keys"/>) to the <see cref="KeyMove"/> <see cref="PlanAuthor.Key"/>
    /// authors (line = <c>MotionFeel.KeyLineDip</c>, page = <c>PageFraction·viewport</c>, Home/End jump). Arrows
    /// follow the scroller's axis: Up/Down on a vertical scroller, Left/Right on a horizontal one. Returns false for
    /// a key that does not scroll.</summary>
    public static bool TryMapKey(int key, bool horizontal, out KeyMove move)
    {
        switch (key)
        {
            case Keys.Up when !horizontal:
            case Keys.Left when horizontal:
                move = KeyMove.LineUp; return true;
            case Keys.Down when !horizontal:
            case Keys.Right when horizontal:
                move = KeyMove.LineDown; return true;
            case Keys.PageUp:
                move = KeyMove.PageUp; return true;
            case Keys.PageDown:
                move = KeyMove.PageDown; return true;
            case Keys.Home:
                move = KeyMove.Home; return true;
            case Keys.End:
                move = KeyMove.End; return true;
            default:
                move = default; return false;
        }
    }

    /// <summary>The direction sign a <see cref="KeyMove"/> moves in (+1 toward the content end).</summary>
    public static int SignOf(KeyMove move) => move switch
    {
        KeyMove.LineUp or KeyMove.PageUp or KeyMove.Home => -1,
        _ => 1,
    };
}
