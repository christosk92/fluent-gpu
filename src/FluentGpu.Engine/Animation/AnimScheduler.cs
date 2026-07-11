using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION ENGINE — the slab-based engine (the animation rework, LANDED and LIVE).
//
//  Despite the AnimScheduler.* file names, the class IS `AnimEngine` (a sealed partial split across these files): the
//  rework replaced the old `class Track` / `AnimEngine.cs` model in place (that file + InteractionAnimator.cs were
//  deleted). It owns the AnimValueSlab, the AnimClock, and the per-frame advance→fold→compose→apply tick built on
//  Generators (analytical spring) + absolute-time sampling. AppHost constructs it (`_anim = new AnimEngine`) and drives
//  it every frame (`_anim.Tick`) — this is the production engine, not a dormant substrate.
//
//  Core model implemented: spring (rebase/velocity-handoff retarget) + two-point eased, over the transform / opacity /
//  blur / presented-size / stroke-trim / clip channels, with the proven FromPaint-fold (preserves un-animated channels)
//  + compose ported verbatim from AnimEngine. Wake is DisplayRate-while-active (Cadence-classed sources are a follow-up).
//  DONE since: structural enter/exit seeding, SizeMode.Relayout/Reflow (host worklists), ScaleCorrect, the Color channel
//  (brush subsumption), driven sources (Drive + Clocks). Residual PERF follow-ups (within the design's near-zero edge-alloc
//  bound — steady frames are already 0-alloc): the index-based SignalSource table (retires the DrivenClockTable closures)
//  + a shared multi-keyframe arena (retires the per-call Keyframe[] at one-shot Animate/Keyframes seeds).
//  Design: docs/plans/animation-engine-rework-design.md §6.4–§6.7.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    private readonly SceneStore _scene;
    private readonly AnimValueSlab _slab = new();
    private readonly List<int> _doneScratch = new(64);   // dead-node slots (reclaim, no SettleRestore); pre-sized so a burst never grows the backing int[] mid-frame (0-alloc steady)
    private readonly List<int> _settledScratch = new(64);   // live rows that reached Done THIS tick, collected during PASS1 so CollectAndFreeDone needn't re-walk all nodes (finding #14)
    private AnimClock _clock;
    private int _parked;                                // live rows on KeepAlive-parked nodes (excluded from HasActive)

    public AnimEngine(SceneStore scene) => _scene = scene;

    // FG_MOTION_DIAG=1: projected-motion discrimination trace (structural seed/snap/tick). Gated — nothing when off.
    private static readonly bool s_motionDiag = Diag.EnvFlag("FG_MOTION_DIAG");

    /// <summary>Live, non-parked rows drive the loop — a parked subtree's looping animation can't defeat the idle stop.</summary>
    public bool HasActive => _slab.Count - _parked > 0;

    /// <summary>ms until the next animation frame is due: 0 (present-now) while anything is active, else +∞.
    /// (The Cadence-classed `min(next-due)` refinement — Hz shimmer, Driven event-wake — lands with the SignalSource table.)</summary>
    public float NextDueMs(double now) => HasActive ? 0f : float.PositiveInfinity;

    // ── frame entry ───────────────────────────────────────────────────────────────────────────────
    /// <summary>Advance the clock by the clamped wall delta (or the resume quantum) and run one tick. The headless
    /// determinism replay calls this with wallNowMs = lastNow + dtFixture, wasIdle=false ⇒ delta == dtFixture exactly.</summary>
    public void RunFrame(double wallNowMs, bool wasIdleOrThrottled)
    {
        _clock.Advance(wallNowMs, wasIdleOrThrottled);
        Tick(in _clock);
    }

    // ── the tick: advance (Generators) → fold (FromPaint) → compose → free ───────────────────────────
    public void Tick(in AnimClock clock)
    {
        if (_slab.Count == 0) return;                  // steady frame: zero work
        float step = clock.DeltaMs;
        _doneScratch.Clear();
        _settledScratch.Clear();

        // PASS 1 — advance every live, non-parked row to its value at absolute ElapsedMs.
        foreach (int nodeIndex in _slab.NodeIndices)
        {
            for (int s = _slab.HeadOnNode(nodeIndex); s >= 0; s = _slab.At(s).NextOnNode)
            {
                ref AnimValue r = ref _slab.At(s);
                if (!_scene.IsLive(r.Node)) { _doneScratch.Add(s); continue; }   // node unmounted → reclaim
                if (r.Has(AnimFlags.Parked)) continue;

                float stepMs = step;
                bool justSeeded = r.Has(AnimFlags.JustSeeded);
                if (r.DelayRemainingMs > 0f && stepMs > 0f && !justSeeded)
                {
                    float consume = MathF.Min(r.DelayRemainingMs, stepMs);
                    r.DelayRemainingMs -= consume;
                    stepMs -= consume;
                    if (r.DelayRemainingMs > 0f)
                    {
                        // hold the start value while delayed (Eval at t=0 returns From/the seeded Position)
                        r.Position = Generators.Eval(in r.Gen, r.Kind, r.Gen.FromV, r.To, 0f).Value;
                        continue;
                    }
                }

                // The SEED frame renders the initial value (ElapsedMs stays 0); the advance begins next frame — matches
                // the old engine's first-frame hold (which the gates encode). Absolute-time sampling keeps it deterministic.
                if (justSeeded) r.Flags &= ~AnimFlags.JustSeeded;
                else r.ElapsedMs += stepMs;
                if (r.Kind == GenKind.Spring)
                {
                    float rd = RestDeltaFor(r.Channel);
                    Sample sp = Generators.EvalSpring(in r.Gen, r.To, r.ElapsedMs, rd, Generators.RestSpeed, out float vel);
                    r.Position = sp.Value; r.Velocity = vel;
                    if (sp.Done) r.Flags |= AnimFlags.Done;
                }
                else
                {
                    r.Position = AdvanceTimeline(s, ref r);   // Eased two-point / Keyframes / Driven (uses _keysBySlot + Clocks)
                }
                if (IsSideTableChannel(r.Channel)) WriteSideTable(r.Channel, r.Node, r.Position);   // brush/hover/press → side-table (not NodePaint)
                if (s_motionDiag && IsStructuralChannel(r.Channel))   // once per frame per animated structural row (SizeW/H/LayoutW/H/TranslateX/Y/ScaleX/Y)
                    System.Console.Error.WriteLine($"[motion-diag]   tick node={r.Node.Raw.Index} ch={r.Channel} val={r.Position:0.0}");
                if (r.Has(AnimFlags.Done)) _settledScratch.Add(s);   // #14: collect the just-settled live row now (PASS3 frees it without re-walking every node)
            }
        }

        // PASS 2 — per node: fold its live rows over the node's CURRENT paint (FromPaint preserves un-animated
        // channels — proven; the plan's seed-Default belongs to the Fork-1 compose pass), compose, mark dirty.
        foreach (int nodeIndex in _slab.NodeIndices)
        {
            int head = _slab.HeadOnNode(nodeIndex);
            if (head < 0) continue;
            // skip a node whose rows are all parked / not live
            NodeHandle node = _slab.At(head).Node;
            if (!_scene.IsLive(node)) continue;

            Accum acc = Accum.FromPaint(in _scene.Paint(node));
            bool any = false;
            // Replace first, then additive — order can't clobber the base.
            for (int s = head; s >= 0; s = _slab.At(s).NextOnNode)
            {
                ref AnimValue r = ref _slab.At(s);
                if (r.Has(AnimFlags.Parked) || r.Has(AnimFlags.Additive) || IsSideTableChannel(r.Channel)) continue;
                acc.Fold(r.Channel, r.Position, replace: true); any = true;
            }
            for (int s = head; s >= 0; s = _slab.At(s).NextOnNode)
            {
                ref AnimValue r = ref _slab.At(s);
                if (r.Has(AnimFlags.Parked) || !r.Has(AnimFlags.Additive) || IsSideTableChannel(r.Channel)) continue;
                acc.Fold(r.Channel, r.Position, replace: false); any = true;
            }
            if (any) Compose(node, in acc);
        }

        // PASS 3 — free settled / dead rows.
        foreach (int s in _doneScratch) FreeSlot(s);
        // settled (Done) rows discovered in PASS1's spring/eased branches:
        CollectAndFreeDone();
    }

    private void CollectAndFreeDone()
    {
        // Free the rows that reached Done this tick, collected during PASS1's advance walk (finding #14) — no third walk
        // over every node. Kept separate from PASS1 so a free never mutates the chain mid-advance; frees route through
        // FreeSlot (parked census exact) after SettleRestore (resting value), preserving the original per-slot order.
        foreach (int s in _settledScratch) { SettleRestore(s); FreeSlot(s); }
    }

    private void FreeSlot(int slot)
    {
        ref AnimValue r = ref _slab.At(slot);
        if (r.Has(AnimFlags.Parked)) _parked--;
        ClearKeys(slot);
        _slab.Free(slot);
    }

    // ── compose (ported from AnimEngine.Tick, lines 698-727) ─────────────────────────────────────────
    private void Compose(NodeHandle node, in Accum acc)
    {
        ref NodePaint p = ref _scene.Paint(node);
        var tf = Affine2D.Translation(acc.Tx, acc.Ty);
        if (acc.Rot != 0f) tf = tf.Multiply(Affine2D.Rotation(acc.Rot * (MathF.PI / 180f)));
        if (acc.Sx != 1f || acc.Sy != 1f) tf = tf.Multiply(Affine2D.Scale(acc.Sx, acc.Sy));
        p.LocalTransform = tf;
        p.Opacity = acc.Op;
        p.BlurSigma = MathF.Max(0f, acc.Blur);
        if (!float.IsNaN(acc.Sw)) p.PresentedW = acc.Sw;
        if (!float.IsNaN(acc.Sh)) p.PresentedH = acc.Sh;
        if (!float.IsNaN(acc.TrimStart)) p.StrokeTrimStart = acc.TrimStart;
        if (!float.IsNaN(acc.TrimEnd)) p.StrokeTrimEnd = acc.TrimEnd;
        if (!float.IsNaN(acc.ClipL) || !float.IsNaN(acc.ClipT) || !float.IsNaN(acc.ClipR) || !float.IsNaN(acc.ClipB))
        {
            ref RectF cb = ref _scene.Bounds(node);
            float cl = float.IsNaN(acc.ClipL) ? 0f : acc.ClipL;
            float ct = float.IsNaN(acc.ClipT) ? 0f : acc.ClipT;
            float cr = float.IsNaN(acc.ClipR) ? cb.W : acc.ClipR;
            float cbm = float.IsNaN(acc.ClipB) ? cb.H : acc.ClipB;
            p.ClipRect = RectF.FromLTRB(cl, ct, cr, cbm);
        }
        _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        // Relayout-mode presented-size change → the host re-solves this subtree (live re-wrap).
        if ((!float.IsNaN(acc.Sw) || !float.IsNaN(acc.Sh)) && (_scene.Flags(node) & NodeFlags.Relayouting) != 0)
            IncrementalRoots.Add(node);
        // SizeMode.Reflow: write the interpolated size into LAYOUT input + dirty the parent — the host re-solves the
        // boundary scope right after this tick, so siblings reflow at the eased size before record (port AnimEngine:733-742).
        if (!float.IsNaN(acc.Lw) || !float.IsNaN(acc.Lh))
        {
            ref LayoutInput li = ref _scene.Layout(node);
            if (!float.IsNaN(acc.Lw)) li.Width = acc.Lw;
            if (!float.IsNaN(acc.Lh)) li.Height = acc.Lh;
            var rp = _scene.Parent(node);
            _scene.Mark(rp.IsNull ? node : rp, NodeFlags.LayoutDirty);
            _reflowWrote = true;
            ReflowRoots.Add(node);
        }
    }

    // ── seeding (the core public API; mirrors AnimEngine so the AppHost flip is near-drop-in) ────────
    // Animate routes through Keyframes (a 2-keyframe track) exactly as the old AnimEngine did — so the EasingSpec
    // overload carries custom cubic-beziers with full fidelity (the segment easing is preserved). The per-call
    // Keyframe[] alloc matches AnimEngine (parity); the shared keyframe arena is the follow-up.
    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs,
                        Easing easing = Easing.EaseInOut, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f)
        => Keyframes(node, channel, [new(0f, from, Easing.Linear), new(1f, to, easing)], durationMs, false, composite, delayMs);

    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs,
                        EasingSpec easing, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f)
        => Keyframes(node, channel, [new(0f, from, Easing.Linear), new(1f, to, easing)], durationMs, false, composite, delayMs);

    /// <summary>Evaluate an easing curve (kept for source compatibility with AnimEngine.Ease; impl lives in Foundation).</summary>
    public static float Ease(Easing e, float t) => Easings.Ease(e, t);

    /// <summary>Seed the implicit brush cross-fade as a unified-engine track (0→1 over durationMs) whose value is written
    /// to <c>BrushAnim.T</c> each tick — replacing the deleted per-frame <c>AdvanceBrushAnims</c> ticker. No first-frame
    /// hold (the old ticker advanced immediately), so the brush-transition gates (E3/cp7.g) are unaffected.</summary>
    public void SeedBrushFade(NodeHandle node, float durationMs)
        => SeedEased(node, AnimChannel.BrushFade, 0f, 1f, durationMs, Easing.Linear);

    /// <summary>Seed (or retarget) a 0-ALLOC two-point eased track From→To over durationMs with a NAMED curve — no
    /// Keyframe[] (AdvanceTimeline's two-point branch reads Gen.FromV/To/EaseId directly). No first-frame hold: the fade
    /// advances immediately, matching the deleted AdvanceBrushAnims/InteractionAnimator tickers. This is the 0-alloc seed
    /// the side-table fades (brush/hover/press) need on the hot path — Animate() routes through Keyframes(), which
    /// allocates a Keyframe[] per call (fine for one-shot enter/exit, not for frequent hover/press/brush edges).</summary>
    public void SeedEased(NodeHandle node, AnimChannel channel, float from, float to, float durationMs, Easing ease)
    {
        int s = Get(node, channel, false);
        ref AnimValue r = ref _slab.At(s);
        r.Kind = GenKind.Eased;
        r.Gen = Generators.BakeEased(from, durationMs <= 0f ? 1f : durationMs, ease);
        r.To = to;
        r.Position = from; r.Velocity = 0f; r.ElapsedMs = 0f; r.DelayRemainingMs = 0f;
        r.Flags &= ~(AnimFlags.Done | AnimFlags.Driven | AnimFlags.Loop | AnimFlags.JustSeeded);
        r.DrivenSrc = AnimValue.WallClock;
        ClearKeys(s);   // ensure no stale Keyframe[] → AdvanceTimeline takes the 0-alloc two-point branch
    }

    /// <summary>Channels that drive a side-table (BrushAnim.T / InteractionAnim.HoverT/PressT) rather than NodePaint —
    /// PASS1 writes them directly; PASS2 skips them so they never touch the transform/paint accumulator. The subsumption
    /// seam for the deleted AdvanceBrushAnims + InteractionAnimator tickers.</summary>
    private static bool IsSideTableChannel(AnimChannel ch)
        => ch == AnimChannel.BrushFade || ch == AnimChannel.HoverFade || ch == AnimChannel.PressFade;

    private void WriteSideTable(AnimChannel ch, NodeHandle node, float v)
    {
        switch (ch)
        {
            case AnimChannel.BrushFade: _scene.SetBrushAnimT((int)node.Raw.Index, v); break;
            case AnimChannel.HoverFade: _scene.SetInteractT(node, press: false, v); break;
            case AnimChannel.PressFade: _scene.SetInteractT(node, press: true, v); break;
        }
    }

    /// <summary>Active hover/press fade rows — the census that replaces <c>InteractionAnimator.ActiveCount</c> (a walk
    /// over active rows; read by the MemCensus diagnostic, not the hot path).</summary>
    public int HoverPressTrackCount
    {
        get
        {
            int n = 0;
            foreach (int nodeIndex in _slab.NodeIndices)
                for (int s = _slab.HeadOnNode(nodeIndex); s >= 0; s = _slab.At(s).NextOnNode)
                {
                    AnimChannel c = _slab.At(s).Channel;
                    if (c == AnimChannel.HoverFade || c == AnimChannel.PressFade) n++;
                }
            return n;
        }
    }

    /// <summary>Spring toward <paramref name="to"/>. If a spring already runs on this node+channel it RETARGETS by
    /// rebase (read current value+velocity, reset ElapsedMs, re-bake A/B) — velocity-continuous, no snap (plan §6.7).
    /// <paramref name="initialVelocity"/> seeds v0 on the FRESH-seed path only (a gesture release injecting its lift
    /// speed into the settle spring); a retarget already carries the live row's velocity and ignores it.</summary>
    public void Spring(NodeHandle node, AnimChannel channel, float to, in SpringParams spring,
                       float? initial = null, float initialVelocity = 0f,
                       CompositeOp composite = CompositeOp.Replace, float delayMs = 0f)
    {
        int existing = Find(node, channel);
        if (existing >= 0 && _slab.At(existing).Kind == GenKind.Spring)
        {
            ref AnimValue e = ref _slab.At(existing);
            float curV = e.Position, curVel = e.Velocity;     // current state from the last tick
            e.To = to;
            e.Gen = Generators.BakeSpring(in spring, x0: curV - to, v0: curVel);
            e.ElapsedMs = 0f; e.DelayRemainingMs = 0f;        // retarget keeps moving (no first-frame hold)
            e.Flags &= ~(AnimFlags.Done | AnimFlags.JustSeeded);
            return;
        }
        int s = Get(node, channel, composite != CompositeOp.Replace);
        ref AnimValue r = ref _slab.At(s);
        float start = initial ?? CurrentValue(node, channel);
        r.Kind = GenKind.Spring;
        r.To = to; r.Position = start; r.Velocity = initialVelocity; r.ElapsedMs = 0f;
        r.Gen = Generators.BakeSpring(in spring, x0: start - to, v0: initialVelocity);
        r.DelayRemainingMs = MathF.Max(0f, delayMs);
        r.Flags = (r.Flags & ~(AnimFlags.Done | AnimFlags.Loop)) | AnimFlags.JustSeeded;
    }

    public void Cancel(NodeHandle node, AnimChannel channel)
    {
        int s = Find(node, channel);
        if (s >= 0) FreeSlot(s);
    }

    public void CancelAll(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        // adjust the parked census before the bulk clear
        for (int s = _slab.HeadOnNode(idx); s >= 0; s = _slab.At(s).NextOnNode)
            if (_slab.At(s).Has(AnimFlags.Parked)) _parked--;
        _slab.ClearNode(idx);
    }

    /// <summary>Quiesce / resume a node's rows on a KeepAlive park edge (idempotent; keeps the parked census exact).</summary>
    public void SetNodeParked(NodeHandle node, bool parked)
    {
        for (int s = _slab.HeadOnNode((int)node.Raw.Index); s >= 0; s = _slab.At(s).NextOnNode)
        {
            ref AnimValue r = ref _slab.At(s);
            bool was = r.Has(AnimFlags.Parked);
            if (was == parked) continue;
            if (parked) { r.Flags |= AnimFlags.Parked; _parked++; }
            else { r.Flags &= ~AnimFlags.Parked; _parked--; }
        }
    }

    /// <summary>The live value of an in-flight row (so an interrupting tween departs from where it is, not a recomputed
    /// endpoint). False = no live row → caller uses its resting value.</summary>
    public bool TryGetTrackValue(NodeHandle node, AnimChannel channel, out float value)
    {
        int s = Find(node, channel);
        if (s >= 0 && !_slab.At(s).Has(AnimFlags.Done)) { value = _slab.At(s).Position; return true; }
        value = 0f; return false;
    }

    // ── chain helpers ────────────────────────────────────────────────────────────────────────────
    private int Find(NodeHandle node, AnimChannel ch)
    {
        for (int s = _slab.HeadOnNode((int)node.Raw.Index); s >= 0; s = _slab.At(s).NextOnNode)
            if (_slab.At(s).Channel == ch) return s;
        return -1;
    }

    private int Get(NodeHandle node, AnimChannel ch, bool additive)
    {
        int idx = (int)node.Raw.Index;
        if (!additive)
            for (int s = _slab.HeadOnNode(idx); s >= 0; s = _slab.At(s).NextOnNode)
                if (_slab.At(s).Channel == ch && !_slab.At(s).Has(AnimFlags.Additive)) { ClearKeys(s); return s; }   // retarget the base
        var seed = new AnimValue { Node = node, Channel = ch };
        if (additive) seed.Flags |= AnimFlags.Additive;
        if ((_scene.Flags(node) & NodeFlags.Parked) != 0) { seed.Flags |= AnimFlags.Parked; _parked++; }
        return _slab.Add(idx, in seed);
    }

    private static float RestDeltaFor(AnimChannel ch)
        => ch is AnimChannel.SizeW or AnimChannel.SizeH or AnimChannel.LayoutW or AnimChannel.LayoutH ? 0.5f : Generators.RestDelta;

    /// <summary>The node's current value on a channel (read from composited paint) — the fresh spring's start point.
    /// Ported from AnimEngine.CurrentValue.</summary>
    private float CurrentValue(NodeHandle node, AnimChannel ch)
    {
        ref NodePaint p = ref _scene.Paint(node);
        return ch switch
        {
            AnimChannel.TranslateX => p.LocalTransform.Dx,
            AnimChannel.TranslateY => p.LocalTransform.Dy,
            AnimChannel.ScaleX => p.LocalTransform.M11,
            AnimChannel.ScaleY => p.LocalTransform.M22,
            AnimChannel.Opacity => p.Opacity,
            AnimChannel.SizeW => !float.IsNaN(p.PresentedW) ? p.PresentedW : _scene.Bounds(node).W,
            AnimChannel.SizeH => !float.IsNaN(p.PresentedH) ? p.PresentedH : _scene.Bounds(node).H,
            AnimChannel.LayoutW => _scene.Bounds(node).W,
            AnimChannel.LayoutH => _scene.Bounds(node).H,
            AnimChannel.BlurSigma => p.BlurSigma,
            AnimChannel.StrokeTrimStart => !float.IsNaN(p.StrokeTrimStart) ? p.StrokeTrimStart : 0f,
            AnimChannel.StrokeTrimEnd => !float.IsNaN(p.StrokeTrimEnd) ? p.StrokeTrimEnd : 1f,
            _ => 0f,   // Rotation: not recoverable from a scaled matrix; springs from 0
        };
    }

    // ── fold accumulator (ported from AnimEngine.Accum) ──────────────────────────────────────────────
    private struct Accum
    {
        public float Tx, Ty, Sx, Sy, Rot, Op, Sw, Sh, TrimStart, TrimEnd;
        public float ClipL, ClipT, ClipR, ClipB;
        public float Lw, Lh;
        public float Blur;

        public static Accum FromPaint(in NodePaint p)
        {
            var tf = p.LocalTransform;
            float sx = MathF.Sqrt(tf.M11 * tf.M11 + tf.M12 * tf.M12);
            float sy = MathF.Sqrt(tf.M21 * tf.M21 + tf.M22 * tf.M22);
            float rot = (tf.M11 != 0f || tf.M12 != 0f) ? MathF.Atan2(tf.M12, tf.M11) * (180f / MathF.PI) : 0f;
            var a = new Accum
            {
                Tx = tf.Dx, Ty = tf.Dy, Sx = sx == 0f ? 1f : sx, Sy = sy == 0f ? 1f : sy, Rot = rot,
                Op = p.Opacity, Sw = p.PresentedW, Sh = p.PresentedH,
                TrimStart = p.StrokeTrimStart, TrimEnd = p.StrokeTrimEnd,
                ClipL = float.NaN, ClipT = float.NaN, ClipR = float.NaN, ClipB = float.NaN,
                Lw = float.NaN, Lh = float.NaN, Blur = p.BlurSigma,
            };
            if (!p.ClipRect.IsInfinite)
            {
                a.ClipL = p.ClipRect.X; a.ClipT = p.ClipRect.Y; a.ClipR = p.ClipRect.Right; a.ClipB = p.ClipRect.Bottom;
            }
            return a;
        }

        public void Fold(AnimChannel ch, float v, bool replace)
        {
            bool add = !replace;
            switch (ch)
            {
                case AnimChannel.TranslateX: Tx = add ? Tx + v : v; break;
                case AnimChannel.TranslateY: Ty = add ? Ty + v : v; break;
                case AnimChannel.ScaleX: Sx = add ? Sx * v : v; break;
                case AnimChannel.ScaleY: Sy = add ? Sy * v : v; break;
                case AnimChannel.Rotation: Rot = add ? Rot + v : v; break;
                case AnimChannel.Opacity: Op = add ? Op * v : v; break;
                case AnimChannel.SizeW: Sw = v; break;
                case AnimChannel.SizeH: Sh = v; break;
                case AnimChannel.StrokeTrimStart: TrimStart = v; break;
                case AnimChannel.StrokeTrimEnd: TrimEnd = v; break;
                case AnimChannel.ClipL: ClipL = v; break;
                case AnimChannel.ClipT: ClipT = v; break;
                case AnimChannel.ClipR: ClipR = v; break;
                case AnimChannel.ClipB: ClipB = v; break;
                case AnimChannel.LayoutW: Lw = v; break;
                case AnimChannel.LayoutH: Lh = v; break;
                case AnimChannel.BlurSigma: Blur = add ? Blur + v : v; break;
            }
        }
    }
}
