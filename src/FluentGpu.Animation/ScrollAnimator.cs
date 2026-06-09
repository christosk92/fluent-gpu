using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Smooth scrolling (phase 7): eases each armed viewport's live offset toward its target (the WinUI inertial wheel feel),
/// applies the content's -offset transform, re-realizes the virtual window on item-boundary crossings, and drives the
/// auto-hiding scrollbar fade. Only viewports actively scrolling are ticked; zero work / zero alloc once everything
/// settles and the thumb has faded out (<see cref="HasActive"/> == false).
/// </summary>
public sealed class ScrollAnimator
{
    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _active = new();
    private readonly HashSet<int> _member = new();

    public ScrollAnimator(SceneStore scene) => _scene = scene;
    public Action RequestRerender { get; set; } = static () => { };
    public bool HasActive => _active.Count > 0;

    public void Arm(NodeHandle n)
    {
        if (!n.IsNull && _scene.IsLive(n) && _member.Add((int)n.Raw.Index)) _active.Add(n);
    }

    /// <summary>Reveal a viewport's scrollbar (pointer is over the scrollable area) and reset its idle timer so it
    /// stays up while hovered, then auto-hides ~700ms after the pointer stops/leaves — the WinUI behaviour.</summary>
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
        float kFade = 1f - MathF.Exp(-dtMs / 80f);
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            NodeHandle n = _active[i];
            if (!_scene.IsLive(n)) { Drop(i, n); continue; }
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
                if (sc.ItemCount > 0)   // virtualization: re-realize when the window's first item crosses
                {
                    int oldFirst, newFirst;
                    if (sc.Layout is not null)
                    {
                        float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                        float vpx = horizontal ? sc.ViewportW : sc.ViewportH;
                        sc.Layout.Window(sc.ItemCount, cross, vpx, oldOff, sc.Overscan, out oldFirst, out _);
                        sc.Layout.Window(sc.ItemCount, cross, vpx, off, sc.Overscan, out newFirst, out _);
                    }
                    else if (_scene.TryGetExtents(n, out var t) && t is not null) { oldFirst = t.IndexAt(oldOff); newFirst = t.IndexAt(off); }
                    else { oldFirst = newFirst = 0; }
                    if (oldFirst != newFirst) { _scene.Mark(n, NodeFlags.VirtualRangeDirty); RequestRerender(); }
                }
            }

            // auto-hide the scrollbar: visible while moving / for 700ms after, then fade out.
            bool movingNow = MathF.Abs(tgt - off) > 0.5f;
            sc.IdleMs = (movingNow || sc.PointerOver) ? 0f : sc.IdleMs + dtMs;
            float fadeTarget = (movingNow || sc.PointerOver || sc.IdleMs < 700f) ? 1f : 0f;
            float expandTarget = sc.PointerOverScrollbar && fadeTarget > 0f ? 1f : 0f;
            float oldFade = sc.FadeT;
            float oldExpand = sc.ExpandT;
            sc.FadeT += (fadeTarget - sc.FadeT) * kFade;
            sc.ExpandT += (expandTarget - sc.ExpandT) * kFade;

            bool fadeSettled = MathF.Abs(fadeTarget - sc.FadeT) < 0.01f;
            bool expandSettled = MathF.Abs(expandTarget - sc.ExpandT) < 0.01f;
            if (fadeSettled) sc.FadeT = fadeTarget;
            if (expandSettled) sc.ExpandT = expandTarget;
            if (sc.FadeT != oldFade || sc.ExpandT != oldExpand) _scene.Mark(n, NodeFlags.PaintDirty);

            if (!movingNow && fadeSettled && expandSettled && (sc.PointerOver || sc.IdleMs >= 700f))
            {
                Drop(i, n);
            }   // fully settled: either statically visible under hover, or hidden after the idle delay
        }
    }

    private void Drop(int i, NodeHandle n) { _member.Remove((int)n.Raw.Index); _active.RemoveAt(i); }
}
