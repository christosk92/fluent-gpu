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

    /// <summary>Opt-in hitch census (<c>FG_SCROLL_PERF=1</c>). Zero cost when off — counters are never touched.</summary>
    public static readonly bool PerfEnabled = Diag.EnvFlag("FG_SCROLL_PERF");
    public static int StickyClipEvals;
    public static int StickyClipDirties;
    public static int StickyClipFullyHidden;
    public static int PinDirties;
    public static int ContinuousDirties;
    public static int ScrollBindCount;

    /// <summary>Reset the per-frame census and snapshot the live bind count. Call once at paint start when
    /// <see cref="PerfEnabled"/>.</summary>
    public static void BeginPerfFrame(SceneStore scene)
    {
        StickyClipEvals = 0;
        StickyClipDirties = 0;
        StickyClipFullyHidden = 0;
        PinDirties = 0;
        ContinuousDirties = 0;
        int binds = 0;
        var table = scene.ScrollBinds;
        if (table.HasAny)
        {
            foreach (int vpIdx in table.ScrollerIndices)
            {
                for (int s = table.Head(vpIdx); s >= 0; s = table.At(s).Next)
                    binds++;
            }
        }
        ScrollBindCount = binds;
    }

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
            WriteScalarSink(scene, ref b, v, vp);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Phase-7 pin + predicate pass — replaces AppHost.ApplyStickyOffsets. Runs every frame after the integrator
    //  settles: pin ops (need the laid-out containing-block clamp), then the per-scroller ScrollFlags bitfield, firing
    //  edge-only OnFlag callbacks on a flip. Iterates every scroller that owns binds.
    //
    //  SCOPE CONSTRAINT (the one thing to know before reading ScrollFlags anywhere): this pass iterates
    //  ScrollBindTable.ScrollerIndices — BIND OWNERS ONLY. A viewport with no ScrollBind row never has ComputeFlags run
    //  on it, so its ScrollState.ScrollFlags stays 0 forever. Flags is therefore NOT a general per-viewport channel, and
    //  ScrollGeometry.Flags is 0 for every observer-only scroller (see the note there). Consumers wanting motion state on
    //  an arbitrary viewport read ScrollState.UserScrollActive / Phase, which the integrator maintains for every ARMED
    //  viewport. Widening this pass to every observer-owning scroller was considered and rejected: the flag channel's
    //  whole point is edge-fired OnFlag binds, and computing it for scrollers with no bind to fire buys nothing but a
    //  second O(scrollers) walk per frame.
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
                if (b.PinKind == 3) { ApplyStickyClip(scene, ref b, in sc, vp); continue; }   // clip, not a pin — no StuckTop
                anyStuckTop |= ApplyPin(scene, ref b, in sc, vp);
            }

            // 2) Recompute the predicate bitfield + the distance-latched direction.
            byte flags = ComputeFlags(scene, ref sc, horiz, anyStuckTop, vp);
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

    static byte ComputeFlags(SceneStore scene, ref ScrollState sc, bool horiz, bool anyStuckTop, NodeHandle vp)
    {
        float offset = horiz ? sc.OffsetX : sc.OffsetY;
        float z = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        float maxOff = horiz ? MathF.Max(0f, sc.ContentW * z - sc.ViewportW)
                             : MathF.Max(0f, sc.ContentH * z - sc.ViewportH);
        byte f = 0;
        if (anyStuckTop) f |= ScrollState.StuckTopBit;
        if (offset > 0.5f) f |= ScrollState.ScrollableUpBit;
        if (offset < maxOff - 0.5f) f |= ScrollState.ScrollableDownBit;
        // MovingNow folds the conscious-scrollbar's "is the scroller in motion" trigger into the generic flag channel
        // (scroll-v3-plan §3.1): any non-Idle kernel Activity, a live coast/chase velocity, OR a held rubber-band.
        // The old "residual eased-target gap" term read the kernel-internal TargetX/Y column, which no longer exists
        // on ScrollState (it's kernel body state now) — Activity/Velocity/Band already cover every case that term
        // caught (a live chase is never Idle with zero velocity).
        if (sc.Activity != FluentGpu.Scroll.ScrollActivity.Idle || MathF.Abs(sc.Velocity) > 1f || sc.BandMain != 0f)
            f |= ScrollState.MovingNowBit;
        // SnappedBit: geometry-derived (is the RESTING offset within tolerance of a configured snap value), not a read
        // of the kernel's in-flight retarget bookkeeping (the old FlingSnapTarget column, deleted — that was kernel
        // body state, not a scene column). Correct even for a snap the viewport arrived at some other way (a
        // programmatic ScrollTo landing on it, not just a retargeted fling).
        if (sc.HasSnap && sc.Activity == FluentGpu.Scroll.ScrollActivity.Idle && IsAtSnapValue(in sc, offset, 0.5f))
            f |= ScrollState.SnappedBit;
        // IdleExpiredBit now reads FluentGpu.Scroll.ScrollBarChromeTable's IdleMs (moved out of ScrollState, §3.1/§4)
        // instead of the deleted ScrollState.IdleMs — chrome owns the idle timer, this pass only reads it.
        if (scene.ScrollChrome.Get((int)vp.Raw.Index).IdleMs >= IdleExpireMs) f |= ScrollState.IdleExpiredBit;

        // Distance-latched direction (dt-invariant): ScrolledFwd carries until the offset travels past the hysteresis.
        bool fwd = (sc.ScrollFlagsPrev & ScrollState.ScrolledFwdBit) != 0;
        if (!sc.DirLatched) { sc.OffsetPrev = offset; sc.DirLatched = true; }
        else if (offset - sc.OffsetPrev > DirHysteresisDip) { fwd = true; sc.OffsetPrev = offset; }
        else if (sc.OffsetPrev - offset > DirHysteresisDip) { fwd = false; sc.OffsetPrev = offset; }
        if (fwd) f |= ScrollState.ScrolledFwdBit;
        return f;
    }

    /// <summary>True when <paramref name="value"/> is within <paramref name="tol"/> DIP of some configured snap value
    /// (interval or explicit point) — the resting-value half of the old <c>ScrollSnap</c> evaluator (now
    /// <c>FluentGpu.Scroll.ScrollPhysics.SnapTarget</c> for the kernel's own fling-landing math); this is a lighter,
    /// LOCAL "am I sitting on one" predicate for <see cref="ScrollState.SnappedBit"/>, with no impulse/ignored-start
    /// rule (that only matters while a fling is actively retargeting, which MovingNowBit already excludes here).</summary>
    static bool IsAtSnapValue(in ScrollState sc, float value, float tol)
    {
        if (sc.SnapInterval > 0f)
        {
            float prev = MathF.Floor((value - sc.SnapStart) / sc.SnapInterval) * sc.SnapInterval + sc.SnapStart;
            float next = prev + sc.SnapInterval;
            float nearest = (value - prev) <= (next - value) ? prev : next;
            if (sc.SnapEnd > sc.SnapStart) nearest = Math.Clamp(nearest, sc.SnapStart, sc.SnapEnd);
            else if (nearest < sc.SnapStart) nearest = sc.SnapStart;
            if (MathF.Abs(nearest - value) <= tol) return true;
        }
        if (sc.SnapPoints is { Length: > 0 } pts)
        {
            for (int i = 0; i < pts.Length; i++)
                if (MathF.Abs(pts[i] - value) <= tol) return true;
        }
        return false;
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
        // NO device-pixel snap. This shift and the content transform must live on the SAME grid or a sticky header
        // seams a sub-pixel step against the content beneath it during a slow pan — that shared-grid requirement is why
        // the snap existed here (scroll-feel-rework-v2 §4.6/§8) and it is exactly why it had to be removed here too when
        // OverscrollPhysics.WriteContentTransform went sub-pixel. Both sides are now continuous float, so they agree
        // EXACTLY rather than agreeing only at the quantum. Crisp text under sub-pixel motion is handled where it
        // belongs, in the glyph renderer's sub-pixel phase atlas.
        ref NodePaint p = ref scene.Paint(n);
        bool pinned = shift > 0f;
        if (MathF.Abs(p.LocalTransform.Dy - shift) > 0.01f)
        {
            p.LocalTransform = pinned ? Affine2D.Translation(0f, shift) : Affine2D.Identity;
            scene.Mark(n, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            if (PerfEnabled) PinDirties++;
        }
        // Pin state is not derived from whether this pass happened to change the transform. Reconcile/layout can restore
        // the literal identity transform before the pin pass; a stale StickyPinned bit must still release in that frame.
        bool nodeWasPinned = (scene.Flags(n) & NodeFlags.StickyPinned) != 0;
        if (pinned != nodeWasPinned)
        {
            if (pinned) scene.Mark(n, NodeFlags.StickyPinned); else scene.Unmark(n, NodeFlags.StickyPinned);
            // CSS :stuck — once per retained presented-state edge, even when reconcile already restored the matching
            // transform. The node flag survives a bind re-bake, so it also carries the correct previous state here.
            b.OnFlag?.Invoke(pinned);
        }
        return pinned;
    }

    /// <summary>Sticky clip-top (PinKind 3, <c>ScrollBindDsl.ClipTopAtViewport</c>): the paint dual of
    /// <see cref="ApplyPin"/> — instead of translating the node to HOLD the viewport line, write the node-local
    /// ClipRect TOP so the node's pixels STOP at it (viewport top + inset). Content scrolling under chrome pinned on
    /// that line is guillotined there, so the page's real backdrop (Mica/tint) shows behind the chrome instead of the
    /// content sliding through it. Same content-space geometry + device-grid snap as the pin. While the line sits
    /// at/above the node's top the clip is RELEASED back to <see cref="RectF.Infinite"/> (never left stale); engaged
    /// clips keep their sides at ±<see cref="NodePaint.StickyClipSpan"/> — inside the sentinel band, since
    /// <see cref="RectF.IsInfinite"/> keys off X. Those sides are also what marks this cut as the one clip class that
    /// gates INPUT as well as paint (see <c>InputDispatcher.ClipRectAdmits</c>): a band that paints nothing is only a
    /// band if the content cut away at its edge stops taking its clicks too.
    /// This bind owns the node's whole ClipRect (documented in the DSL). OnFlag fires per engage/release edge.</summary>
    static void ApplyStickyClip(SceneStore scene, ref ScrollBind b, in ScrollState sc, NodeHandle vp)
    {
        NodeHandle n = b.Target;
        if (sc.ContentNode.IsNull) return;
        float yN = NodeYInContent(scene, n, vp, sc.ContentNode, out bool inContent);
        if (!inContent) return;
        if (PerfEnabled) StickyClipEvals++;
        float top = sc.OffsetY + b.Inset - yN;                     // node-local y of the viewport-anchored line
        // Sub-pixel, on the same continuous grid as the content transform and the pinned shift above: a clip edge that
        // snapped while the content it cuts did not would crawl a pixel against the moving rows under it.
        float nodeH = scene.Bounds(n).H;
        // Fully above the sticky line: freeze ClipRect.Y at nodeH so further offset advances do not re-Mark every
        // pixel (overscan rows under translucent chrome were the playlist sticky hitch — O(window) PaintDirty/frame).
        bool fullyHidden = top >= nodeH && nodeH > 0f;
        if (PerfEnabled && fullyHidden) StickyClipFullyHidden++;
        bool clipping = top > 0f;
        float applied = !clipping ? -1e9f
                      : fullyHidden ? nodeH
                      : top;
        // Change-gate against the LIVE paint (exactly like ApplyPin's LocalTransform.Dy compare), NOT a cached
        // last-written: paint can be re-derived between passes, and a cached gate would skip the healing re-write,
        // leaving the clip permanently released.
        ref NodePaint p = ref scene.Paint(n);
        float cur = p.ClipRect.IsInfinite ? -1e9f : p.ClipRect.Y;
        if (MathF.Abs(applied - cur) > 0.01f)
        {
            p.ClipRect = clipping
                ? RectF.FromLTRB(-NodePaint.StickyClipSpan, applied, NodePaint.StickyClipSpan, NodePaint.StickyClipSpan)
                : RectF.Infinite;
            scene.Mark(n, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            if (PerfEnabled) StickyClipDirties++;
        }
        // Edge-only, and the unset state counts as "not clipping" — the first evaluation of a released clip must NOT
        // fire a spurious false (mirrors ApplyPin, whose initial unpinned state fires nothing).
        if (b.OnFlag is { } flag)
        {
            bool was = b.OnFlagHasLast && b.OnFlagLast;
            if (clipping != was) flag.Invoke(clipping);
            b.OnFlagLast = clipping; b.OnFlagHasLast = true;
        }
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
        float bandLimit = FluentGpu.Scroll.ScrollPhysics.BandLimit(vpExtent);
        for (; s >= 0; s = table.At(s).Next)
        {
            ref ScrollBind b = ref table.At(s);
            if (!b.Has(ScrollBind.FlagGeometryAnchor)) continue;
            b.RangeA = ResolveAnchor(b.AnchorA, b.AnchorAv, maxOff, bandLimit);
            b.RangeB = ResolveAnchor(b.AnchorB, b.AnchorBv, maxOff, bandLimit);
        }
    }

    static float ResolveAnchor(ScrollBindAnchor kind, float val, float maxOff, float bandLimit)
        => kind switch
        {
            ScrollBindAnchor.OffsetPx => val,
            ScrollBindAnchor.OffsetFrac => val * maxOff,
            ScrollBindAnchor.OverscrollBand => val <= 0f ? 0f : bandLimit,    // A=0 ⇒ 0, B (default 0) ⇒ bandLimit cap
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
                                       sc.BandMain, sc.Velocity, sc.ScrollFlags, sc.UserScrollActive);
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
            ScrollChannel.OverscrollBand => -sc.BandMain,                // top pull positive
            _ => offset,
        };
        float a = b.RangeA, bb = b.RangeB;
        float t;
        if (MathF.Abs(bb - a) < 1e-4f) t = 0f;                      // degenerate range ⇒ inactive (writes OutLo)
        else { t = (sample - a) / (bb - a); if (b.Has(ScrollBind.FlagClampOut)) t = Math.Clamp(t, 0f, 1f); }
        if (b.Ease != Easing.Linear) t = Easings.Ease(b.Ease, t);
        return b.OutLo + (b.OutHi - b.OutLo) * t;
    }

    static void WriteScalarSink(SceneStore scene, ref ScrollBind b, float v, NodeHandle vp)
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
            if (PerfEnabled) ContinuousDirties++;
            b.LastWritten = v;
            return;
        }
        var lt = p.LocalTransform;
        if (MathF.Abs(v - b.LastWritten) <= 1e-3f) return;
        switch (b.Sink)
        {
            case BindSink.TransY: p.LocalTransform = new Affine2D(lt.M11, lt.M12, lt.M21, lt.M22, lt.Dx, v); break;
            case BindSink.TransX: p.LocalTransform = new Affine2D(lt.M11, lt.M12, lt.M21, lt.M22, v, lt.Dy); break;
            case BindSink.ScaleUniform: p.LocalTransform = new Affine2D(v, lt.M12, lt.M21, v, lt.Dx, lt.Dy); break;
            case BindSink.Opacity: p.Opacity = Math.Clamp(v, 0f, 1f); break;
            case BindSink.PresentedH: p.PresentedH = v; break;
            case BindSink.ClipTop:
            {
                var c = p.ClipRect.IsInfinite ? RectF.FromLTRB(-1e9f, v, 1e9f, 1e9f) : RectF.FromLTRB(p.ClipRect.X, v, p.ClipRect.Right, p.ClipRect.Bottom);
                p.ClipRect = c; break;
            }
        }
        scene.Mark(b.Target, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        if (PerfEnabled) ContinuousDirties++;
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
        float band = sc.BandMain;                                        // Orientation==0 here, so BandMain == BandY
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
            if (PerfEnabled) ContinuousDirties++;
        }
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
