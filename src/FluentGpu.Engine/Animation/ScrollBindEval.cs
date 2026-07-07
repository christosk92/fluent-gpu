using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>The evaluator for the generic scroll-binding model (design/plans/generic-hookable-scroll-engine-design.md §6).
/// Owns every per-op math path so the offset-write chokepoint (<see cref="ApplyContinuous"/>), the phase-7 pin+flag pass
/// (<see cref="ApplyPinAndFlagPass"/>), the geometry-anchor bake (<see cref="BakeGeometry"/>) and the change-only
/// observer pass (<see cref="RunObservers"/>) all share one implementation. Allocation-free on the hot path: index
/// arithmetic over the reconciler-owned slab, no closures, no per-frame dictionary growth; managed callbacks fire only
/// on an edge flip / projected-key change.</summary>
public static class ScrollBindEval
{
    /// <summary>Distance (DIP) the offset must travel before the latched scroll-direction bit flips — geometry-derived,
    /// dt-invariant (§6.4). A 1-px jitter never flips it; the crossing is identical at any frame rate.</summary>
    public static readonly float DirHysteresisDip = Env("FG_SCROLL_DIRHYST", 6f);

    /// <summary>Idle time (ms) after which <c>IdleExpired</c> latches (drives the conscious-scrollbar auto-hide, §9).</summary>
    public const float IdleExpireMs = 2000f;

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Continuous pass — offset / band / velocity / signed-phase ops. Called at the offset-write chokepoint
    //  (InputDispatcher.ApplyScrollPosition) and from FlexLayout.ArrangeViewport, so effects stay synchronous with the
    //  content move (no one-frame lag). Pin ops are skipped here — they run in the phase-7 pin pass (need laid-out Y).
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    public static void ApplyContinuous(SceneStore scene, NodeHandle vp, ref ScrollState sc)
    {
        var table = scene.ScrollBinds;
        int s = table.Head((int)vp.Raw.Index);
        if (s < 0) return;
        bool horiz = sc.Orientation == 1;
        float offset = horiz ? sc.OffsetX : sc.OffsetY;          // STAGE 1: scroller progress source, once
        for (; s >= 0; s = table.At(s).Next)
        {
            ref ScrollBind b = ref table.At(s);
            if (b.PinKind != 0) continue;
            if (!scene.IsLive(b.Target)) continue;
            if (b.Has(ScrollBind.FlagStretchClosedForm)) { ApplyStretch(scene, ref b, in sc); continue; }
            float v = EvalScalar(scene, ref b, in sc, offset, horiz, vp);
            WriteScalarSink(scene, ref b, v);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Phase-7 pin + predicate pass — replaces AppHost.ApplyStickyOffsets. Runs every frame after the integrator
    //  settles: pin ops (need the laid-out containing-block clamp), then the per-scroller ScrollFlags bitfield, firing
    //  edge-only OnFlag callbacks on a flip. Iterates every scroller that owns binds.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    public static void ApplyPinAndFlagPass(SceneStore scene)
    {
        var table = scene.ScrollBinds;
        if (!table.HasAny) return;
        foreach (int vpIdx in table.ScrollerIndices)
        {
            int head = table.Head(vpIdx);
            if (head < 0) continue;
            NodeHandle vp = table.At(head).ScrollerHandle;
            if (vp.IsNull || !scene.IsLive(vp)) continue;
            ref ScrollState sc = ref scene.ScrollRef(vp);
            bool horiz = sc.Orientation == 1;

            // 1) Pins (and accumulate StuckTop).
            bool anyStuckTop = false;
            for (int s = head; s >= 0; s = table.At(s).Next)
            {
                ref ScrollBind b = ref table.At(s);
                if (b.PinKind == 0 || !scene.IsLive(b.Target)) continue;
                anyStuckTop |= ApplyPin(scene, ref b, in sc, vp);
            }

            // 2) Recompute the predicate bitfield + the distance-latched direction.
            byte flags = ComputeFlags(scene, ref sc, horiz, anyStuckTop);
            byte prev = sc.ScrollFlagsPrev;
            sc.ScrollFlags = flags;

            // 3) Fire non-pin OnFlag binds whose watched bit flipped (edge-only).
            if (flags != prev)
            {
                for (int s = head; s >= 0; s = table.At(s).Next)
                {
                    ref ScrollBind b = ref table.At(s);
                    if (b.PinKind != 0 || b.OnFlag is null || b.FlagBit == 0) continue;
                    bool now = (flags & b.FlagBit) != 0;
                    bool was = (prev & b.FlagBit) != 0;
                    if (now != was) b.OnFlag.Invoke(now);
                }
            }
            sc.ScrollFlagsPrev = flags;
        }
    }

    /// <summary>Re-apply continuous scroll-driven bindings (opacity / presented-height / parallax) at the current offset
    /// for every scroller that owns binds. Layout and input already call <see cref="ApplyContinuous"/>; this pass runs
    /// before record on steady frames (focus loss, theme chrome, skip-submit repaints) so collapsed heroes and faded copy
    /// stay correct even when no relayout or offset write happened this frame.</summary>
    public static void ApplyContinuousPass(SceneStore scene)
    {
        var table = scene.ScrollBinds;
        if (!table.HasAny) return;
        foreach (int vpIdx in table.ScrollerIndices)
        {
            int head = table.Head(vpIdx);
            if (head < 0) continue;
            NodeHandle vp = table.At(head).ScrollerHandle;
            if (vp.IsNull || !scene.IsLive(vp)) continue;
            ref ScrollState sc = ref scene.ScrollRef(vp);
            ApplyContinuous(scene, vp, ref sc);
        }
    }

    static byte ComputeFlags(SceneStore scene, ref ScrollState sc, bool horiz, bool anyStuckTop)
    {
        float offset = horiz ? sc.OffsetX : sc.OffsetY;
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float maxOff = horiz ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW)
                             : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        byte f = 0;
        if (anyStuckTop) f |= ScrollState.StuckTopBit;
        if (offset > 0.5f) f |= ScrollState.ScrollableUpBit;
        if (offset < maxOff - 0.5f) f |= ScrollState.ScrollableDownBit;
        // MovingNow folds the conscious-scrollbar's "is the scroller in motion" trigger into the generic flag channel:
        // any non-Idle Phase (Fling/WheelAnimating/TouchpadTracking/Overscroll/SnapBack), a held overscroll band, OR a
        // residual eased target gap.
        float tgt = horiz ? sc.TargetX : sc.TargetY;
        if (sc.Phase != ScrollIntegrator.Idle || MathF.Abs(sc.FlingVelocity) > 1f || sc.Overscrolling || MathF.Abs(tgt - offset) > 0.5f)
            f |= ScrollState.MovingNowBit;
        if (sc.HasSnap && !float.IsNaN(sc.FlingSnapTarget) && MathF.Abs(offset - sc.FlingSnapTarget) <= 0.5f) f |= ScrollState.SnappedBit;
        if (sc.IdleMs >= IdleExpireMs) f |= ScrollState.IdleExpiredBit;

        // Distance-latched direction (dt-invariant): ScrolledFwd carries until the offset travels past the hysteresis.
        bool fwd = (sc.ScrollFlagsPrev & ScrollState.ScrolledFwdBit) != 0;
        if (!sc.DirLatched) { sc.OffsetPrev = offset; sc.DirLatched = true; }
        else if (offset - sc.OffsetPrev > DirHysteresisDip) { fwd = true; sc.OffsetPrev = offset; }
        else if (sc.OffsetPrev - offset > DirHysteresisDip) { fwd = false; sc.OffsetPrev = offset; }
        if (fwd) f |= ScrollState.ScrolledFwdBit;
        return f;
    }

    /// <summary>Pin a node at the viewport top (CSS position:sticky), clamped to its containing block. Ported verbatim
    /// from the old ApplyStickyOffsets; returns true when the node is currently pinned (so the caller sets StuckTop).</summary>
    static bool ApplyPin(SceneStore scene, ref ScrollBind b, in ScrollState sc, NodeHandle vp)
    {
        NodeHandle n = b.Target;
        float shift = 0f;
        if (!sc.ContentNode.IsNull)
        {
            float yN = NodeYInContent(scene, n, vp, sc.ContentNode, out bool inContent);
            var par = scene.Parent(n);
            if (inContent && !par.IsNull)
            {
                float yPar = yN - scene.Bounds(n).Y;                          // parent's Y within the content
                float limit = MathF.Max(0f, (yPar + scene.Bounds(par).H) - (yN + scene.Bounds(n).H));
                shift = Math.Clamp(sc.OffsetY + b.Inset - yN, 0f, limit);
            }
        }
        // Device-pixel snap (scroll-feel-rework-v2 §4.6/§8, gate.scroll.subpixel-stability): the pinned shift rounds to a
        // whole device pixel on the SAME grid the content transform uses (OverscrollPhysics.WriteContentTransform:
        // round((offset+band)·s)/s), so a sticky header sharing the scroller's origin never seams a sub-pixel step against
        // the content beneath it during a slow pan. The clamp above stays in logical float; only the applied shift snaps.
        float s = scene.DeviceScale;
        if (float.IsFinite(s) && s > 0f) shift = MathF.Round(shift * s) / s;
        ref NodePaint p = ref scene.Paint(n);
        bool pinned = shift > 0f;
        if (MathF.Abs(p.LocalTransform.Dy - shift) > 0.01f)
        {
            bool wasPinned = (scene.Flags(n) & NodeFlags.StickyPinned) != 0;
            p.LocalTransform = pinned ? Affine2D.Translation(0f, shift) : Affine2D.Identity;
            scene.Mark(n, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            if (pinned) scene.Mark(n, NodeFlags.StickyPinned); else scene.Unmark(n, NodeFlags.StickyPinned);
            // CSS :stuck — once per engage/release transition, never per frame.
            if (pinned != wasPinned) b.OnFlag?.Invoke(pinned);
        }
        return pinned;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Geometry-anchor bake — runs inside ArrangeViewport (Content*/Bounds known), BEFORE the same-frame
    //  ApplyContinuous, so a resize frame never paints a one-frame-stale bound transform (§12 gate 5 / R4).
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    public static void BakeGeometry(SceneStore scene, NodeHandle vp, in ScrollState sc)
    {
        var table = scene.ScrollBinds;
        int s = table.Head((int)vp.Raw.Index);
        if (s < 0) return;
        bool horiz = sc.Orientation == 1;
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float vpExtent = horiz ? sc.ViewportW : sc.ViewportH;
        float maxOff = horiz ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW)
                             : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        float bandLimit = OverscrollPhysics.BandLimit(vpExtent);
        for (; s >= 0; s = table.At(s).Next)
        {
            ref ScrollBind b = ref table.At(s);
            if (!b.Has(ScrollBind.FlagGeometryAnchor)) continue;
            float nodeTop = 0f, nodeH = 0f;
            bool needNode = b.AnchorA is ScrollBindAnchor.NodeEnterViewport or ScrollBindAnchor.NodeExitViewport
                         || b.AnchorB is ScrollBindAnchor.NodeEnterViewport or ScrollBindAnchor.NodeExitViewport;
            if (needNode && scene.IsLive(b.Target) && !sc.ContentNode.IsNull)
            {
                nodeTop = NodeYInContent(scene, b.Target, vp, sc.ContentNode, out _);
                nodeH = scene.Bounds(b.Target).H;
            }
            b.RangeA = ResolveAnchor(b.AnchorA, b.AnchorAv, maxOff, bandLimit, nodeTop, nodeH, vpExtent);
            b.RangeB = ResolveAnchor(b.AnchorB, b.AnchorBv, maxOff, bandLimit, nodeTop, nodeH, vpExtent);
        }
    }

    static float ResolveAnchor(ScrollBindAnchor kind, float val, float maxOff, float bandLimit, float nodeTop, float nodeH, float vpExtent)
        => kind switch
        {
            ScrollBindAnchor.OffsetPx => val,
            ScrollBindAnchor.OffsetFrac => val * maxOff,
            ScrollBindAnchor.OverscrollBand => val <= 0f ? 0f : bandLimit,    // A=0 ⇒ 0, B (default 0) ⇒ bandLimit cap
            ScrollBindAnchor.NodeEnterViewport => nodeTop - vpExtent,
            ScrollBindAnchor.NodeExitViewport => nodeTop + nodeH,
            _ => val,
        };

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Change-only observer pass — the escape hatch (ScrollEl.OnScrollGeometryChanged). Projects each registered
    //  scroller's geometry to a coarse long key and fires the action only when that key changes. UI-thread, pre-publish.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    public static void RunObservers(SceneStore scene)
    {
        var obs = scene.ScrollObservers;
        if (obs.Count == 0) return;
        foreach (var kv in obs)
        {
            var row = kv.Value;
            if (row.Project is null || row.Action is null) continue;
            var h = row.Node;
            if (h.IsNull || !scene.IsLive(h) || !scene.HasScroll(h)) continue;
            ref ScrollState sc = ref scene.ScrollRef(h);
            var g = new ScrollGeometry(sc.OffsetX, sc.OffsetY, sc.ViewportW, sc.ViewportH, sc.ContentW, sc.ContentH,
                                       sc.OverscrollPx, sc.FlingVelocity, sc.ScrollFlags);
            long key = row.Project(g);
            if (row.HasLast && key == row.LastKey) continue;
            row.Action(g);
            // write the updated key back into the dict (struct value)
            ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(obs, kv.Key);
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot)) { slot.LastKey = key; slot.HasLast = true; }
        }
    }

    // ── per-op scalar evaluation ──────────────────────────────────────────────────────────────────────────────
    static float EvalScalar(SceneStore scene, ref ScrollBind b, in ScrollState sc, float offset, bool horiz, NodeHandle vp)
    {
        float sample = b.Source switch
        {
            ScrollChannel.Offset => offset,
            ScrollChannel.OverscrollBand => -sc.OverscrollPx,            // top pull positive
            ScrollChannel.Velocity => sc.FlingVelocity,
            ScrollChannel.SignedPhase => SignedPhase(scene, b.Target, in sc, horiz, vp),
            _ => offset,
        };
        float a = b.RangeA, bb = b.RangeB;
        float t;
        if (b.Source == ScrollChannel.SignedPhase) t = Math.Clamp(sample, -1f, 1f);
        else if (MathF.Abs(bb - a) < 1e-4f) t = 0f;                      // degenerate range ⇒ inactive (writes OutLo)
        else { t = (sample - a) / (bb - a); if (b.Has(ScrollBind.FlagClampOut)) t = Math.Clamp(t, 0f, 1f); }
        if (b.Ease != Easing.Linear) t = Easings.Ease(b.Ease, t);
        return b.OutLo + (b.OutHi - b.OutLo) * t;
    }

    static void WriteScalarSink(SceneStore scene, ref ScrollBind b, float v)
    {
        ref NodePaint p = ref scene.Paint(b.Target);
        if (b.Sink == BindSink.PresentedHTrailing)
        {
            float h = MathF.Max(0f, v);
            float shift = h - scene.Bounds(b.Target).H;
            bool sameH = !float.IsNaN(p.PresentedH) && MathF.Abs(p.PresentedH - h) <= 1e-3f;
            if (sameH && MathF.Abs(p.ChildShiftY - shift) <= 1e-3f) return;
            p.PresentedH = h;
            p.ChildShiftY = shift;
            scene.Mark(b.Target, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            b.LastWritten = v;
            return;
        }
        if (MathF.Abs(v - b.LastWritten) <= 1e-3f) return;
        var lt = p.LocalTransform;
        switch (b.Sink)
        {
            case BindSink.TransY: p.LocalTransform = new Affine2D(lt.M11, lt.M12, lt.M21, lt.M22, lt.Dx, v); break;
            case BindSink.TransX: p.LocalTransform = new Affine2D(lt.M11, lt.M12, lt.M21, lt.M22, v, lt.Dy); break;
            case BindSink.ScaleUniform: p.LocalTransform = new Affine2D(v, lt.M12, lt.M21, v, lt.Dx, lt.Dy); break;
            case BindSink.ScaleY: p.LocalTransform = new Affine2D(lt.M11, lt.M12, lt.M21, v, lt.Dx, lt.Dy); break;
            case BindSink.Opacity: p.Opacity = Math.Clamp(v, 0f, 1f); break;
            case BindSink.Blur: p.BlurSigma = MathF.Max(0f, v); break;
            case BindSink.PresentedH: p.PresentedH = v; break;
            case BindSink.ClipBottom:
            {
                var c = p.ClipRect.IsInfinite ? RectF.FromLTRB(-1e9f, -1e9f, 1e9f, v) : RectF.FromLTRB(p.ClipRect.X, p.ClipRect.Y, p.ClipRect.Right, v);
                p.ClipRect = c; break;
            }
            case BindSink.ClipTop:
            {
                var c = p.ClipRect.IsInfinite ? RectF.FromLTRB(-1e9f, v, 1e9f, 1e9f) : RectF.FromLTRB(p.ClipRect.X, v, p.ClipRect.Right, p.ClipRect.Bottom);
                p.ClipRect = c; break;
            }
        }
        scene.Mark(b.Target, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        if (b.Has(ScrollBind.FlagPaintAbove)) scene.Mark(b.Target, NodeFlags.StickyPinned);
        b.LastWritten = v;
    }

    /// <summary>iOS/Spotify stretchy header: the (h+pull)/h scale + band-cancel matrix on the target node directly
    /// (no leading-child walk). Ported verbatim from OverscrollPhysics.ApplyStretchHeader; the <c>!=</c> check IS the
    /// change-gate. The hero authors origin (0.5, 0); the recorder conjugates about it, so this matrix is scale + the
    /// band-cancel translation only.</summary>
    static void ApplyStretch(SceneStore scene, ref ScrollBind b, in ScrollState sc)
    {
        if (sc.Orientation == 1) return;                                  // vertical scrollers only
        float band = sc.OverscrollPx;
        float pull = band < 0f ? -band : 0f;                             // top overscroll only (band < 0)
        float h = scene.Bounds(b.Target).H;
        Affine2D target;
        if (h <= 1f) target = Affine2D.Identity;
        else if (pull > 0.5f) { float s = (h + pull) / h; target = new Affine2D(s, 0f, 0f, s, 0f, -pull); }
        else target = Affine2D.Identity;
        ref NodePaint hp = ref scene.Paint(b.Target);
        if (hp.LocalTransform != target)
        {
            hp.LocalTransform = target;
            scene.Mark(b.Target, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }

    /// <summary>SwiftUI signed phase in [-1,+1] (identity 0): item-center vs viewport-center on the scroll axis.</summary>
    static float SignedPhase(SceneStore scene, NodeHandle node, in ScrollState sc, bool horiz, NodeHandle vp)
    {
        if (sc.ContentNode.IsNull || !scene.IsLive(node)) return 0f;
        float vpExtent = horiz ? sc.ViewportW : sc.ViewportH;
        if (vpExtent <= 1f) return 0f;
        float offset = horiz ? sc.OffsetX : sc.OffsetY;
        float nodePos = NodeYInContent(scene, node, vp, sc.ContentNode, out bool inContent);
        if (!inContent) return 0f;
        var bnd = scene.Bounds(node);
        float nodeExtent = horiz ? bnd.W : bnd.H;
        float centerInViewport = (nodePos - offset) + nodeExtent * 0.5f;
        float phase = (centerInViewport - vpExtent * 0.5f) / (vpExtent * 0.5f);
        return Math.Clamp(phase, -1f, 1f);
    }

    /// <summary>Sum the local Y (or X) of a node up to — but excluding — the scroll content node, giving its
    /// pure-layout position within the content (transforms excluded; the pin must not feed back on itself).</summary>
    static float NodeYInContent(SceneStore scene, NodeHandle node, NodeHandle vp, NodeHandle contentNode, out bool inContent)
    {
        float y = 0f;
        inContent = false;
        bool horiz = scene.HasScroll(vp) && scene.ScrollRef(vp).Orientation == 1;
        for (var a = node; !a.IsNull && a != vp; a = scene.Parent(a))
        {
            if (a == contentNode) { inContent = true; break; }
            var bnd = scene.Bounds(a);
            y += horiz ? bnd.X : bnd.Y;
        }
        return y;
    }

    static float Env(string name, float dflt)
    {
        var s = Environment.GetEnvironmentVariable(name);
        return s is not null && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : dflt;
    }
}
