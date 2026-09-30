using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Scroll.Runtime;

/// <summary>Per-viewport scrollbar CHROME state (fade/expand/hover/idle) — the WinUI "conscious scrollbar" FSM's
/// row. Presentation only: it never carries a motion value (design §9 "chrome never touches motion").</summary>
public struct ScrollBarChromeRow
{
    public float FadeT;                   // scrollbar indicator opacity 0..1 (eased in on scroll/hover, auto-hides after idle)
    public float ExpandT;                 // WinUI conscious scrollbar expansion 0=thin indicator, 1=full gutter + buttons
    public bool  PointerOver;              // pointer is inside this scroll viewport
    public bool  PointerOverScrollbar;     // pointer is inside this viewport's scrollbar gutter
    public float IdleMs;                   // time since the last scroll movement / hover (drives the auto-hide)
    /// <summary>The frame index of the last frame the viewport's shown offset actually changed (stamped by
    /// <see cref="ScrollBarChrome.NotifyMoved"/>); "moved this frame" is <c>MotionStamp == FrameIndex</c>.</summary>
    public uint  MotionStamp;
    public float LaneDwellMs;      // continuous lane hover (toward ExpandBeginMs)
    public float LaneOffDwellMs;   // since lane-leave while still over the viewport (toward ContractBeginMs)
    public float AwayMs;           // since the pointer left the viewport (toward LeaveHideMs for hover-flash bars)
    public bool  ScrolledSinceReveal;   // a real scroll happened while visible → WinUI 2s idle hide applies
    public float ExpandFrom, ExpandTarget, ExpandClockMs;
    public float FadeFrom, FadeTarget, FadeClockMs;
}

/// <summary>Sparse per-viewport table of <see cref="ScrollBarChromeRow"/>s (O(viewports)), owned by the
/// <see cref="SceneStore"/> and captured by value into the recording snapshot.</summary>
public sealed class ScrollBarChromeTable
{
    private readonly Dictionary<int, ScrollBarChromeRow> _rows = new();

    public bool TryGet(int node, out ScrollBarChromeRow row) => _rows.TryGetValue(node, out row);
    public ScrollBarChromeRow Get(int node) => _rows.TryGetValue(node, out var row) ? row : default;
    public void Clear(int node) => _rows.Remove(node);
    public int Count => _rows.Count;
    internal ref ScrollBarChromeRow GetOrAddRow(int node) => ref CollectionsMarshal.GetValueRefOrAddDefault(_rows, node, out _);
}

/// <summary>
/// The pure WinUI scrollbar chrome TIMELINE (design §9): the dwell/expand/fade timings from
/// <c>ScrollBar_themeresources.xaml</c>, advanced per row by <see cref="Advance"/>. No scene access — the ticker
/// (<see cref="ScrollBarChrome"/>) feeds it the three facts it needs (scrollable, moving, hover) and marks paint.
/// </summary>
public static class ScrollBarTimeline
{
    public const float ExpandBeginMs = 400f;     // ScrollBarExpandBeginTime
    public const float ContractBeginMs = 500f;   // ScrollBarContractBeginTime
    public const float ExpandContractMs = 167f;  // ScrollBarExpandDuration / ScrollBarContractDuration
    public const float FadeMs = 83f;             // ScrollBarOpacityChangeDuration
    public const float IdleHideMs = 2000f;       // ScrollBarContractDelay — after a scroll, pointer away
    /// <summary>Engine-deliberate hover-flash retire delay: contract-begin + contract.</summary>
    public const float LeaveHideMs = ContractBeginMs + ExpandContractMs;
    /// <summary>Minimum overflow (content − viewport, DIP) before the conscious scrollbar may arm.</summary>
    public const float MinBarOverflowPx = 4f;

    /// <summary>Advances one row by <paramref name="dtMs"/>. Returns true while the row still has work pending
    /// (moving, a track in flight or a dwell timer counting) — the ticker keeps it armed; false when it may retire.
    /// <paramref name="fullyHidden"/> reports a row that landed at rest, hidden and contracted.</summary>
    public static bool Advance(ref ScrollBarChromeRow cs, float dtMs, bool scrollable, bool movingNow, out bool fullyHidden, out bool changed)
    {
        bool over = cs.PointerOver;
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
        cs.ExpandT = Step(ref cs.ExpandFrom, cs.ExpandTarget, ref cs.ExpandClockMs, ExpandContractMs, dtMs, Easing.FluentPopOpen, cs.ExpandT);
        cs.FadeT = Step(ref cs.FadeFrom, cs.FadeTarget, ref cs.FadeClockMs, FadeMs, dtMs, Easing.Linear, cs.FadeT);
        changed = cs.ExpandT != oldExpand || cs.FadeT != oldFade;

        bool expandSettled = cs.ExpandT == cs.ExpandTarget;
        bool fadeSettled = cs.FadeT == cs.FadeTarget;
        fullyHidden = fadeSettled && cs.FadeT == 0f && expandSettled && cs.ExpandT == 0f;
        if (fullyHidden)
        {
            cs.LaneDwellMs = 0f; cs.LaneOffDwellMs = 0f; cs.AwayMs = 0f; cs.ScrolledSinceReveal = false;
            cs.ExpandFrom = 0f; cs.ExpandTarget = 0f; cs.ExpandClockMs = 0f;
            cs.FadeFrom = 0f; cs.FadeTarget = 0f; cs.FadeClockMs = 0f;
        }

        bool dwellPending =
            (lane && cs.LaneDwellMs < ExpandBeginMs && cs.ExpandTarget != 1f) ||
            (!lane && over && cs.ExpandT > 0f && cs.ExpandTarget != 0f) ||
            (!show && cs.FadeT > 0f && !hideDue);

        return movingNow || !expandSettled || !fadeSettled || dwellPending;
    }

    private static void StartTrack(ref float from, ref float target, ref float clockMs, float current, float to)
    {
        from = current;
        target = to;
        clockMs = 0f;
    }

    private static float Step(ref float from, float target, ref float clockMs, float durationMs, float dtMs, Easing easing, float current)
    {
        if (current == target) return current;
        clockMs += dtMs;
        float t = Math.Clamp(clockMs / MathF.Max(1f, durationMs), 0f, 1f);
        if (t >= 1f) return target;
        return from + (target - from) * Easings.Ease(easing, t);
    }
}

/// <summary>
/// The UI-side scrollbar chrome ticker over the scene's <see cref="ScrollBarChromeTable"/>: arms a viewport's row on
/// hover flips (<see cref="SetPointerOver"/>) and on real motion (<see cref="NotifyMoved"/>), advances every armed row
/// through <see cref="ScrollBarTimeline.Advance"/> once per frame (<see cref="Tick"/>) and marks the viewport
/// paint-dirty when its pixels change. <see cref="NeedsFrame"/> is the wake signal.
/// </summary>
public sealed class ScrollBarChrome
{
    private readonly SceneStore _scene;
    private readonly List<int> _active = new();
    private readonly HashSet<int> _member = new();
    private int _needsFrameCount;
    private bool _armedSinceTick;

    public ScrollBarChrome(SceneStore scene) => _scene = scene;

    /// <summary>The current frame's index — bumped by the host once per frame before <see cref="Tick"/>.</summary>
    public uint FrameIndex { get; set; }

    /// <summary>Raw membership: any viewport with a live conscious cycle.</summary>
    public bool Active => _active.Count > 0;
    public int Count => _active.Count;

    /// <summary>Wake signal: a real state transition since the last tick, or rows that still have work pending.</summary>
    public bool NeedsFrame => _armedSinceTick || _needsFrameCount > 0;

    public void SetPointerOver(int node, bool over, bool overLane)
    {
        ref var row = ref _scene.ScrollChrome.GetOrAddRow(node);
        bool changed = row.PointerOver != over || row.PointerOverScrollbar != overLane;
        row.PointerOver = over;
        row.PointerOverScrollbar = overLane;
        Arm(node);
        if (changed) _armedSinceTick = true;
    }

    /// <summary>The viewport's shown offset changed this frame (the host's scroll step reports it).</summary>
    public void NotifyMoved(int node)
    {
        ref var row = ref _scene.ScrollChrome.GetOrAddRow(node);
        row.MotionStamp = FrameIndex;
        Arm(node);
        _armedSinceTick = true;
    }

    /// <summary>KeepAlive park edge: a parked viewport's bar lands at rest and its row retires.</summary>
    public void SetNodeParked(int node, bool parked)
    {
        if (!parked) return;
        if (_scene.ScrollChrome.TryGet(node, out var row))
        {
            if (row.FadeT != 0f || row.ExpandT != 0f)
            {
                NodeHandle h = _scene.HandleAt(node);
                if (!h.IsNull && _scene.IsLive(h)) _scene.Mark(h, NodeFlags.PaintDirty);
            }
            _scene.ScrollChrome.Clear(node);
        }
        if (_member.Remove(node)) _active.Remove(node);
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
        _armedSinceTick = false;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            int node = _active[i];
            NodeHandle h = _scene.HandleAt(node);
            if (h.IsNull || !_scene.IsLive(h) || !_scene.TryGetScroll(h, out var sc))
            {
                Drop(i, node, forget: false);
                continue;
            }
            if ((_scene.Flags(h) & NodeFlags.Parked) != 0)
            {
                Drop(i, node, forget: true);
                continue;
            }
            ref var cs = ref _scene.ScrollChrome.GetOrAddRow(node);
            bool movingNow = cs.MotionStamp == FrameIndex;
            float overflow = sc.ContentMain - sc.ViewportMain;
            bool scrollable = overflow > ScrollBarTimeline.MinBarOverflowPx;
            bool pending = ScrollBarTimeline.Advance(ref cs, dtMs, scrollable, movingNow, out bool fullyHidden, out bool changed);
            if (changed) _scene.Mark(h, NodeFlags.PaintDirty);
            if (!pending) Drop(i, node, forget: fullyHidden);
        }
        _needsFrameCount = _active.Count;
    }
}
