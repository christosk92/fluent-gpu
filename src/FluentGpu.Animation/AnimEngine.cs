using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>Animatable channels. Transform channels compose into LocalTransform (TransformDirty); Opacity + the presented
/// SizeW/SizeH (the "Reveal" presented extent the recorder draws the fill + child-clip at) → PaintDirty. LayoutW/LayoutH
/// are the one deliberate exception to "animation never relays out": a SizeMode.Reflow track writes the interpolated
/// size into LayoutInput each tick and the host re-solves the nearest layout boundary, so neighbours reflow smoothly.</summary>
public enum AnimChannel : byte { TranslateX, TranslateY, ScaleX, ScaleY, Rotation, Opacity, SizeW, SizeH, StrokeTrimStart, StrokeTrimEnd, ClipL, ClipT, ClipR, ClipB, LayoutW, LayoutH }

// Easing (the enum + evaluator) now lives in FluentGpu.Foundation (a foundational motion primitive shared by Dsl/Scene/
// Render + the image cross-fade). Animation imports Foundation, so `Easing` here resolves to FluentGpu.Foundation.Easing.

public enum IntegrationMode : byte { Eased, Spring }

/// <summary>How a track combines with other tracks on the same channel (CSS animation-composition).</summary>
public enum CompositeOp : byte { Replace, Add, Accumulate }

/// <summary>A spring (stateful ODE) — no duration; integrates toward Target, carrying velocity across retargets.</summary>
public readonly struct SpringParams
{
    public readonly float Stiffness, Damping, Mass, RestEps;
    public SpringParams(float stiffness, float damping, float mass = 1f, float restEps = 0.001f)
        => (Stiffness, Damping, Mass, RestEps) = (stiffness, damping, mass, restEps);

    /// <summary>response = approx settle time (s); dampingRatio 1 = critical (no overshoot), &lt;1 = bouncy.</summary>
    public static SpringParams FromResponse(float responseSec, float dampingRatio = 1f, float mass = 1f)
    {
        float w = (2f * MathF.PI) / MathF.Max(responseSec, 1e-3f);   // natural frequency
        return new SpringParams(w * w * mass, 2f * dampingRatio * w * mass, mass);
    }
    public static SpringParams Default => FromResponse(0.35f, 0.75f);
}

/// <summary>One keyframe: a normalized offset (0..1), its value, and the easing of the segment leading INTO it.</summary>
public readonly record struct Keyframe
{
    public float Offset { get; init; }
    public float Value { get; init; }
    public EasingSpec Easing { get; init; }

    public Keyframe(float offset, float value) : this(offset, value, EasingSpec.Default) { }
    public Keyframe(float offset, float value, Easing easing) : this(offset, value, (EasingSpec)easing) { }
    public Keyframe(float offset, float value, EasingSpec easing)
    {
        Offset = offset;
        Value = value;
        Easing = easing;
    }
}

/// <summary>A value source (scroll offset, playback ms, a custom MotionValue) that can drive a timeline instead of wall-time.</summary>
public sealed class DrivenClockTable
{
    private readonly List<Func<float>> _sources = new();
    public int Register(Func<float> source) { _sources.Add(source); return _sources.Count - 1; }
    public float Sample(int i) => (uint)i < (uint)_sources.Count ? _sources[i]() : 0f;
}

/// <summary>
/// Generic, composition-style animation runtime (phase 7) — the Web-Animations model on a fixed channel set. Tracks are
/// eased (multi-keyframe + per-segment easing, time- OR driven-timeline) or springs (ODE with velocity handoff on
/// retarget). Per (node,channel) tracks combine by Replace/Add/Accumulate. Each tick composes the surviving channel
/// values into NodePaint.LocalTransform (T∘R∘S) + Opacity and marks Transform/PaintDirty — animation NEVER relays out.
/// </summary>
public sealed class AnimEngine
{
    private sealed class Track
    {
        public NodeHandle Node;
        public AnimChannel Channel;
        public IntegrationMode Mode;
        public CompositeOp Composite;
        // eased / driven
        public Keyframe[] Keys = [];
        public float DurationMs, ElapsedMs;
        public float DelayRemainingMs;
        public bool Loop;
        public int DrivenRef = -1;            // -1 = wall-clock; else index into the DrivenClockTable
        public bool JustSeeded;
        public float DomainMin, DomainMax;    // driven: maps source value → progress
        // spring
        public float Pos, Vel, Target;
        public SpringParams Spring;
        public bool Done;
        public float Value;   // value computed this tick (folded after advancing all tracks)
        // SizeMode.Reflow (LayoutW/LayoutH) and SizeMode.Relayout (SizeW/SizeH with RestoreLayout) bookkeeping: the
        // element-DECLARED LayoutInput value stashed at seed time (restored at settle so layout ownership returns to
        // normal — without it the node stays frozen at the last interpolated solve, e.g. an auto height never re-wraps),
        // and whether the spec anchors content trailing.
        public float RestoreTo;
        public bool RestoreLayout;
        public bool TrailingAnchor;

        /// <summary>The destination this track is flying to (spring target / last keyframe value).</summary>
        public float TargetValue => Mode == IntegrationMode.Spring ? Target : (Keys.Length > 0 ? Keys[^1].Value : 0f);
    }

    private struct Accum
    {
        public float Tx, Ty, Sx, Sy, Rot, Op, Sw, Sh, TrimStart, TrimEnd;
        public float ClipL, ClipT, ClipR, ClipB;   // authored clip-rect edges (node-local); NaN = that edge not animated
        public float Lw, Lh;                       // SizeMode.Reflow interpolated LAYOUT size; NaN = axis not reflowing
        public static Accum Default => new() { Tx = 0, Ty = 0, Sx = 1, Sy = 1, Rot = 0, Op = 1, Sw = float.NaN, Sh = float.NaN, TrimStart = float.NaN, TrimEnd = float.NaN, ClipL = float.NaN, ClipT = float.NaN, ClipR = float.NaN, ClipB = float.NaN, Lw = float.NaN, Lh = float.NaN };
        public static Accum FromPaint(in NodePaint p)
        {
            // Preserve channels that do NOT have an active track this tick. Without this, a longer scale/size track can
            // reset opacity to 1 after a shorter opacity close track has already settled at 0 (dialog/flyout pop-back).
            var tf = p.LocalTransform;
            float sx = MathF.Sqrt(tf.M11 * tf.M11 + tf.M12 * tf.M12);
            float sy = MathF.Sqrt(tf.M21 * tf.M21 + tf.M22 * tf.M22);
            float rot = (tf.M11 != 0f || tf.M12 != 0f) ? MathF.Atan2(tf.M12, tf.M11) * (180f / MathF.PI) : 0f;
            var a = new Accum
            {
                Tx = tf.Dx, Ty = tf.Dy, Sx = sx == 0f ? 1f : sx, Sy = sy == 0f ? 1f : sy, Rot = rot,
                Op = p.Opacity,
                Sw = p.PresentedW, Sh = p.PresentedH,
                TrimStart = p.StrokeTrimStart, TrimEnd = p.StrokeTrimEnd,
                ClipL = float.NaN, ClipT = float.NaN, ClipR = float.NaN, ClipB = float.NaN,
                Lw = float.NaN, Lh = float.NaN,
            };
            if (!p.ClipRect.IsInfinite)
            {
                a.ClipL = p.ClipRect.X;
                a.ClipT = p.ClipRect.Y;
                a.ClipR = p.ClipRect.Right;
                a.ClipB = p.ClipRect.Bottom;
            }
            return a;
        }
        public void Fold(AnimChannel ch, float v, CompositeOp op)
        {
            bool add = op != CompositeOp.Replace;
            switch (ch)
            {
                case AnimChannel.TranslateX: Tx = add ? Tx + v : v; break;
                case AnimChannel.TranslateY: Ty = add ? Ty + v : v; break;
                case AnimChannel.ScaleX: Sx = add ? Sx * v : v; break;   // scale composes multiplicatively
                case AnimChannel.ScaleY: Sy = add ? Sy * v : v; break;
                case AnimChannel.Rotation: Rot = add ? Rot + v : v; break;
                case AnimChannel.Opacity: Op = add ? Op * v : v; break;
                case AnimChannel.SizeW: Sw = v; break;   // presented width (Reveal) — replace, never relayout
                case AnimChannel.SizeH: Sh = v; break;
                case AnimChannel.StrokeTrimStart: TrimStart = v; break;
                case AnimChannel.StrokeTrimEnd: TrimEnd = v; break;
                case AnimChannel.ClipL: ClipL = v; break;   // clip edges replace (a clip rect, not an additive transform)
                case AnimChannel.ClipT: ClipT = v; break;
                case AnimChannel.ClipR: ClipR = v; break;
                case AnimChannel.ClipB: ClipB = v; break;
                case AnimChannel.LayoutW: Lw = v; break;    // reflow layout size — replace (one owner per axis)
                case AnimChannel.LayoutH: Lh = v; break;
            }
        }
    }

    private readonly SceneStore _scene;
    private readonly List<Track> _tracks = new();
    private readonly Dictionary<NodeHandle, Accum> _scratch = new();
    // Per-node layout-transition spec, keyed by node INDEX (not handle): slot reuse self-cleans, so the table is bounded
    // by the slab size. The reconciler Set/Clears it from BoxEl.Animate on every reconcile; capture/apply read it.
    private readonly Dictionary<int, LayoutTransition> _transitions = new();
    public DrivenClockTable Clocks { get; } = new();

    public AnimEngine(SceneStore scene) => _scene = scene;
    public bool HasActive => _tracks.Count > 0;

    // ── census (read by the MemCensus sampler) ────────────────────────────────────────────────────
    // _loopTrackCount is maintained at add/remove/loop-flag-change (NOT a per-call scan): a fresh track from Get
    // starts Loop=false, so only Keyframes flips it; every _tracks removal funnels through RemoveTrackAt to decrement.
    private int _loopTrackCount;
    /// <summary>Active animation tracks (springs + eased/driven), all channels — O(1) census.</summary>
    public int TrackCount => _tracks.Count;
    /// <summary>Looping tracks (Loop==true) — O(1) maintained counter, not a scan.</summary>
    public int LoopTrackCount => _loopTrackCount;
    /// <summary>Live per-node layout-transition specs (the <c>_transitions</c> side-table) — O(1) census.</summary>
    public int TransitionCount => _transitions.Count;

    /// <summary>Remove a track, keeping <see cref="_loopTrackCount"/> exact. Every <c>_tracks</c> removal routes here.</summary>
    private void RemoveTrackAt(int i)
    {
        if (_tracks[i].Loop) _loopTrackCount--;
        _tracks.RemoveAt(i);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The LIVE value of an in-flight track on <paramref name="node"/>+<paramref name="channel"/> — so an
    /// interrupting eased tween can start FROM where the animation currently is instead of a recomputed endpoint
    /// (WinUI AnimatedIcon queues/blends transitions, AnimatedIcon.cpp:235-267 — a mid-flight chevron reversal must
    /// not snap). False = no live track (caller uses its resting value).</summary>
    public bool TryGetTrackValue(NodeHandle node, AnimChannel channel, out float value)
    {
        // Value is the per-tick folded result for EVERY mode (eased keyframes and springs both write it);
        // a just-seeded track hasn't produced one yet — the caller falls back to its resting value.
        if (Find(node, channel) is { Done: false, JustSeeded: false } t)
        {
            value = t.Value;
            return true;
        }
        value = 0f;
        return false;
    }

    /// <summary>Eased two-point tween (retargets any live track on the same node+channel).</summary>
    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs,
                        Easing easing = Easing.EaseInOut, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f)
        => Keyframes(node, channel, [new(0f, from, Easing.Linear), new(1f, to, easing)], durationMs, false, composite, delayMs);

    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs,
                        EasingSpec easing, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f)
        => Keyframes(node, channel, [new(0f, from, Easing.Linear), new(1f, to, easing)], durationMs, false, composite, delayMs);

    /// <summary>Multi-keyframe eased track (@keyframes). Offsets must be ascending in 0..1.</summary>
    public void Keyframes(NodeHandle node, AnimChannel channel, Keyframe[] keys, float durationMs,
                          bool loop = false, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f)
    {
        var t = Get(node, channel, composite);
        t.Mode = IntegrationMode.Eased; t.Keys = keys; t.DurationMs = durationMs; t.ElapsedMs = 0f;
        t.DelayRemainingMs = MathF.Max(0f, delayMs);
        if (t.Loop != loop) _loopTrackCount += loop ? 1 : -1;   // maintain the loop-track census (Get-fresh tracks start false)
        t.Loop = loop; t.DrivenRef = -1; t.Done = false; t.JustSeeded = true;
        t.RestoreLayout = false;   // reused track: the seeder re-opts-in per mode (Relayout/Reflow set it after seeding)
        if (Diag.Enabled) Diag.Event("anim", $"keyframes SEED {channel} dur={durationMs:0}ms keys={keys.Length} loop={loop}");
    }

    /// <summary>Scroll/value-driven track: progress comes from a DrivenClock source mapped through [domainMin,domainMax].</summary>
    public void Drive(NodeHandle node, AnimChannel channel, Keyframe[] keys, int drivenRef, float domainMin, float domainMax,
                      CompositeOp composite = CompositeOp.Replace)
    {
        var t = Get(node, channel, composite);
        // A reused track may carry Loop=true from a prior Keyframes(loop:true) seed — stale Loop on a DRIVEN track
        // wraps u instead of clamping at the domain edge (and mislabels the loop-track census). Reset on re-seed.
        if (t.Loop) { _loopTrackCount--; t.Loop = false; }
        t.Mode = IntegrationMode.Eased; t.Keys = keys; t.DrivenRef = drivenRef;
        t.DomainMin = domainMin; t.DomainMax = domainMax; t.Done = false; t.JustSeeded = true;
    }

    /// <summary>Spring toward <paramref name="to"/>. If a spring already runs on this node+channel, it RETARGETS — keeping
    /// position + velocity (no snap), the iOS/Compose velocity-handoff.</summary>
    public void Spring(NodeHandle node, AnimChannel channel, float to, in SpringParams spring,
                       float? initial = null, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f)
    {
        Track? existing = Find(node, channel);
        if (existing is { Mode: IntegrationMode.Spring })
        {
            if (Diag.Enabled) Diag.Event("anim", $"spring RETARGET {channel} pos={existing.Pos:0.###} vel={existing.Vel:0.##} → {to:0.###}");
            existing.Target = to; existing.Spring = spring; existing.Done = false; existing.Composite = composite; existing.JustSeeded = false;
            existing.DelayRemainingMs = 0f;   // interruption/retarget keeps moving; do not re-delay a live spring
            existing.RestoreLayout = false;   // the seeder re-opts-in per mode
            return;   // keep Pos + Vel → smooth handoff
        }
        var t = Get(node, channel, composite);
        if (t.Loop) { _loopTrackCount--; t.Loop = false; }   // reused track: clear a stale Keyframes loop flag (census + Tick wrap)
        t.Mode = IntegrationMode.Spring; t.Target = to; t.Spring = spring;
        t.RestoreLayout = false;
        // a FRESH spring starts from the node's CURRENT value (not the target) so it actually travels — else it snaps.
        t.Pos = initial ?? CurrentValue(node, channel); t.Vel = 0f; t.Done = false; t.JustSeeded = true;
        t.DelayRemainingMs = MathF.Max(0f, delayMs);
        if (Diag.Enabled) Diag.Event("anim", $"spring SEED {channel} from={t.Pos:0.###} → {to:0.###} (k={spring.Stiffness:0} c={spring.Damping:0})");
    }

    /// <summary>The node's current value on a channel (read from its composited paint) — the spring's natural start point.</summary>
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
            AnimChannel.StrokeTrimStart => !float.IsNaN(p.StrokeTrimStart) ? p.StrokeTrimStart : (_scene.TryGetPolylineStroke(node, out var ps) ? ps.TrimStart : 0f),
            AnimChannel.StrokeTrimEnd => !float.IsNaN(p.StrokeTrimEnd) ? p.StrokeTrimEnd : (_scene.TryGetPolylineStroke(node, out var pe) ? pe.TrimEnd : 1f),
            _ => 0f,   // Rotation: not cleanly recoverable from a scaled matrix; springs from 0
        };
    }

    public void Cancel(NodeHandle node, AnimChannel channel)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Node == node && _tracks[i].Channel == channel) RemoveTrackAt(i);
    }

    /// <summary>Cancel + reset the channel's paint to its settle-time resting sentinel — symmetric with the settle
    /// path (StrokeTrim/PresentedW/H → NaN, Clip → Infinite). A bare <see cref="Cancel"/> only drops the track,
    /// freezing the last interpolated value in paint with no spec fallback (a deactivated ProgressRing froze a
    /// partial arc). Channels without a rest sentinel (transform/opacity/layout) behave exactly like Cancel —
    /// their resting value is the reconciler's static re-assert.</summary>
    public void CancelToRest(NodeHandle node, AnimChannel channel)
    {
        Cancel(node, channel);
        if (!_scene.IsLive(node)) return;
        ref NodePaint p = ref _scene.Paint(node);
        switch (channel)
        {
            case AnimChannel.SizeW: p.PresentedW = float.NaN; _scene.Unmark(node, NodeFlags.Relayouting); break;
            case AnimChannel.SizeH: p.PresentedH = float.NaN; break;
            case AnimChannel.StrokeTrimStart: p.StrokeTrimStart = float.NaN; break;
            case AnimChannel.StrokeTrimEnd: p.StrokeTrimEnd = float.NaN; break;
            case AnimChannel.ClipL or AnimChannel.ClipT or AnimChannel.ClipR or AnimChannel.ClipB: p.ClipRect = RectF.Infinite; break;
            default: return;   // no rest sentinel — identical to Cancel
        }
        _scene.Mark(node, NodeFlags.PaintDirty);
    }
    public void CancelAll(NodeHandle node)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Node == node) RemoveTrackAt(i);
    }

    // ── Layout-transition side-table (node index → spec) ──────────────────────────────────────────
    /// <summary>Attach (or replace) a node's layout-transition spec. Called by the reconciler from BoxEl.Animate.</summary>
    public void SetTransition(NodeHandle node, in LayoutTransition t) => _transitions[(int)node.Raw.Index] = t;
    /// <summary>Read a node's layout-transition spec (set by the reconciler this commit).</summary>
    public bool TryGetTransition(NodeHandle node, out LayoutTransition t) => _transitions.TryGetValue((int)node.Raw.Index, out t);
    /// <summary>Drop a node's layout-transition spec (the element stopped declaring Animate, or the slot was freed).</summary>
    public void ClearTransition(NodeHandle node) => _transitions.Remove((int)node.Raw.Index);

    /// <summary>Symmetric teardown when a scene slot is FREED (wired to <see cref="SceneStore.OnFreeIndex"/>): drop the
    /// index-keyed transition spec so a freed node leaves no dormant spec the NEXT node reusing that slot would inherit.
    /// In-flight tracks are keyed by HANDLE (gen-checked) and self-prune at the next Tick's IsLive guard, so they need no
    /// teardown here. 0-alloc; a no-op when the slot had no spec.</summary>
    public void ClearForIndex(int index) => _transitions.Remove(index);

    // ── Layout-transition projection (continuous, retained FLIP) ──────────────────────────────────
    /// <summary>FLIP the node from its captured presented rect to its new laid-out rect, seeding/retargeting the channels
    /// the spec requests. Position is velocity-continuous: a running spring keeps its velocity and shifts its offset by the
    /// layout delta (no jump on interruption); a fresh spring starts offset by the full delta and settles to 0. The host
    /// calls this once per commit for every BoundsAnimated node that moved; Tick then advances it every frame.</summary>
    public void AnimateBounds(NodeHandle node, in RectF fromAbs, in RectF toAbs, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.Dynamics);
        if ((spec.Channels & TransitionChannels.Position) != 0)
        {
            ReframePosition(node, AnimChannel.TranslateX, fromAbs.X - toAbs.X, dyn, spec.DelayMs);
            ReframePosition(node, AnimChannel.TranslateY, fromAbs.Y - toAbs.Y, dyn, spec.DelayMs);
        }
        if ((spec.Channels & TransitionChannels.Size) != 0)
        {
            SizeMode mode = spec.Size == SizeMode.Auto ? SizeMode.Reveal : spec.Size;
            switch (mode)
            {
                case SizeMode.Reveal:
                    RevealSize(node, AnimChannel.SizeW, fromAbs.W, toAbs.W, dyn, spec.DelayMs);
                    RevealSize(node, AnimChannel.SizeH, fromAbs.H, toAbs.H, dyn, spec.DelayMs);
                    break;
                case SizeMode.ScaleCorrect:   // GPU scale toward 1 (children that opt in counter-scale in the recorder)
                    if (toAbs.W > 0.5f) ScaleReveal(node, AnimChannel.ScaleX, fromAbs.W / toAbs.W, dyn, spec.DelayMs);
                    if (toAbs.H > 0.5f) ScaleReveal(node, AnimChannel.ScaleY, fromAbs.H / toAbs.H, dyn, spec.DelayMs);
                    break;
                case SizeMode.Relayout:        // re-solve the subtree at the interpolated size each tick (live reflow)
                    RevealSize(node, AnimChannel.SizeW, fromAbs.W, toAbs.W, dyn, spec.DelayMs);
                    RevealSize(node, AnimChannel.SizeH, fromAbs.H, toAbs.H, dyn, spec.DelayMs);
                    // The host writes the interpolated size into LayoutInput each tick (RunIncrementalLayout), so the
                    // DECLARED value must be stashed and restored at settle — else an auto axis stays frozen at the
                    // last solve. Seed/retarget time is when LayoutInput provably holds the declared value.
                    if (Find(node, AnimChannel.SizeW) is { } tw) { tw.RestoreTo = _scene.Layout(node).Width; tw.RestoreLayout = true; }
                    if (Find(node, AnimChannel.SizeH) is { } th) { th.RestoreTo = _scene.Layout(node).Height; th.RestoreLayout = true; }
                    _scene.Mark(node, NodeFlags.Relayouting);
                    break;
                case SizeMode.Reflow:          // the interpolated size participates in PARENT layout — neighbours reflow
                    ReflowSize(node, AnimChannel.LayoutW, fromAbs.W, toAbs.W, spec);
                    ReflowSize(node, AnimChannel.LayoutH, fromAbs.H, toAbs.H, spec);
                    break;
            }
        }
    }

    // SizeMode.Reflow seeding/retargeting. Two guards kill the inherent feedback loop (the track itself writes the
    // layout size, so on every reconcile frame the host's projection diff sees "old size ≠ new size" again):
    //   target guard — phase-6 re-solved to the SAME destination (a no-op recommit mid-flight): keep flying;
    //   echo guard   — layout still holds OUR OWN interp (the node's scope wasn't re-solved this commit): keep flying.
    // Only a genuinely new destination passes both → retarget (tween restarts from the current interp — the WinUI
    // fixed-duration storyboard feel; a spring keeps Pos+Vel). Shrink legs select ExitDynamics (asymmetric open/close).
    private void ReflowSize(NodeHandle node, AnimChannel ch, float from, float to, in LayoutTransition spec)
    {
        Track? ex = Find(node, ch);
        if (ex is not null)
        {
            if (MathF.Abs(ex.TargetValue - to) < 0.5f) return;
            if (MathF.Abs(ex.Value - to) < 0.5f) return;
            from = ex.Value;                                  // genuine retarget — depart from the current interp
        }
        else if (MathF.Abs(from - to) < 0.5f) return;         // no change and nothing in flight

        TransitionDynamics dyn = Normalize(to < from && spec.ExitDynamics is { } ed ? ed : spec.Dynamics);
        // Stash the element-DECLARED LayoutInput value before the track starts overwriting it. At seed/retarget time
        // it provably holds the declared value (NaN/auto or explicit): the commit that produced this new target has
        // just rewritten it. Restored at settle so the node returns to normal layout ownership.
        float declared = ch == AnimChannel.LayoutW ? _scene.Layout(node).Width : _scene.Layout(node).Height;
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: from, delayMs: spec.DelayMs);
        else
            Animate(node, ch, from, to, dyn.DurationMs, dyn.Easing, delayMs: spec.DelayMs);
        Track t = Find(node, ch);
        t.RestoreTo = declared;
        t.TrailingAnchor = spec.Anchor == SizeAnchor.Trailing;
        if (Diag.Enabled) Diag.Event("anim", $"reflow SEED {ch} {from:0.#} → {to:0.#} restore={declared:0.#}");
    }

    /// <summary>Nodes whose REFLOW size advanced this tick — the host re-solves their boundary scope, then refreshes
    /// the Trailing child-shift from the fresh bounds. Cleared by the host after it consumes them.</summary>
    public List<NodeHandle> ReflowRoots { get; } = new();

    private bool _reflowWrote;
    /// <summary>True if any reflow track wrote LayoutInput this tick (interp advance or settle restore) — the host
    /// runs a boundary-scoped re-solve before record. Self-clearing.</summary>
    public bool ConsumeReflowWrites() { bool w = _reflowWrote; _reflowWrote = false; return w; }

    /// <summary>Nodes whose presented SIZE changed this tick under SizeMode.Relayout — the host re-solves just these
    /// subtrees (scoped layout) so their text re-wraps live. Cleared by the host after it consumes them.</summary>
    public List<NodeHandle> IncrementalRoots { get; } = new();

    // Presented-extent reveal: spring/tween the recorder's drawn size old → new (fresh starts at the old size; a running
    // reveal retargets keeping Pos+Vel). Works for grow AND shrink — the presented size can exceed the model bounds.
    private void RevealSize(NodeHandle node, AnimChannel ch, float fromSize, float toSize, in TransitionDynamics dyn, float delayMs = 0f)
    {
        if (MathF.Abs(fromSize - toSize) < 0.5f && Find(node, ch) is null) return;   // no change and nothing in flight
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, toSize, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: fromSize, delayMs: delayMs);
        else
            Animate(node, ch, fromSize, toSize, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
    }

    // ScaleCorrect: spring a scale channel from old/new → 1 (the recorder composites it about the node centre; opted-in
    // children counter-scale to stay undistorted). Cheap + compositor-only, but distorts text/borders — chrome only.
    private void ScaleReveal(NodeHandle node, AnimChannel ch, float fromRatio, in TransitionDynamics dyn, float delayMs = 0f)
    {
        if (MathF.Abs(fromRatio - 1f) < 0.001f && Find(node, ch) is null) return;
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, 1f, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: fromRatio, delayMs: delayMs);
        else
            Animate(node, ch, fromRatio, 1f, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
    }

    // ── enter / exit (appearing & disappearing nodes) ────────────────────────────────────────────
    /// <summary>An inserted node animates FROM the enter terminal (offset/scale/opacity) TO identity.</summary>
    public void SeedEnter(NodeHandle node, in EnterExit e, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.Dynamics);
        if (e.Opacity != 1f) SeedTerminal(node, AnimChannel.Opacity, 1f, dyn, initial: e.Opacity, delayMs: spec.DelayMs);
        if (e.Dx != 0f) SeedTerminal(node, AnimChannel.TranslateX, 0f, dyn, initial: e.Dx, delayMs: spec.DelayMs);
        if (e.Dy != 0f) SeedTerminal(node, AnimChannel.TranslateY, 0f, dyn, initial: e.Dy, delayMs: spec.DelayMs);
        if (e.Sx != 1f) SeedTerminal(node, AnimChannel.ScaleX, 1f, dyn, initial: e.Sx, delayMs: spec.DelayMs);
        if (e.Sy != 1f) SeedTerminal(node, AnimChannel.ScaleY, 1f, dyn, initial: e.Sy, delayMs: spec.DelayMs);
    }

    /// <summary>A removed node (now an Exiting orphan) animates FROM its current state TO the exit terminal; when all its
    /// tracks settle (<see cref="HasTracks"/> == false) the host reclaims it.</summary>
    public void SeedExit(NodeHandle node, in EnterExit e, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.ExitDynamics ?? spec.Dynamics);   // asymmetric exit timing when set
        SeedTerminal(node, AnimChannel.Opacity, e.Opacity, dyn, delayMs: spec.DelayMs);   // always (the exit-settle signal)
        if (e.Dx != 0f) SeedTerminal(node, AnimChannel.TranslateX, e.Dx, dyn, delayMs: spec.DelayMs);
        if (e.Dy != 0f) SeedTerminal(node, AnimChannel.TranslateY, e.Dy, dyn, delayMs: spec.DelayMs);
        if (e.Sx != 1f) SeedTerminal(node, AnimChannel.ScaleX, e.Sx, dyn, delayMs: spec.DelayMs);
        if (e.Sy != 1f) SeedTerminal(node, AnimChannel.ScaleY, e.Sy, dyn, delayMs: spec.DelayMs);
    }

    private void SeedTerminal(NodeHandle node, AnimChannel ch, float to, in TransitionDynamics dyn, float? initial = null, float delayMs = 0f)
    {
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial, delayMs: delayMs);
        else
            Animate(node, ch, initial ?? CurrentValue(node, ch), to, dyn.DurationMs, dyn.Easing, delayMs: delayMs);
    }

    /// <summary>True while any track targets this node (used by the host to detect a settled exit orphan).</summary>
    public bool HasTracks(NodeHandle node)
    {
        for (int i = 0; i < _tracks.Count; i++) if (_tracks[i].Node == node) return true;
        return false;
    }

    /// <summary>A default-constructed spec (all-zero dynamics) means "use the defaults" — fill them in. A spring with
    /// no response gets the standard 0.30s/0.85ζ; a zero damping ratio is treated as unset too (an undamped spring at
    /// any stiffness never settles and destabilizes the integrator). A default EasingSpec (no curve chosen) becomes
    /// FluentDecelerate, the engine's transition default.</summary>
    private static TransitionDynamics Normalize(in TransitionDynamics d)
    {
        var n = d.Kind == DynamicsKind.Spring
            ? (d.Response > 0f ? (d.DampingRatio > 0f ? d : d with { DampingRatio = 0.85f }) : TransitionDynamics.Default)
            : (d.DurationMs > 0f ? d : TransitionDynamics.Tween(200f, d.Easing));
        return n.Kind == DynamicsKind.Tween && n.Easing.IsDefault ? n with { Easing = Easing.FluentDecelerate } : n;
    }

    private void ReframePosition(NodeHandle node, AnimChannel ch, float delta, in TransitionDynamics dyn, float delayMs = 0f)
    {
        if (MathF.Abs(delta) < 0.01f && Find(node, ch) is null) return;   // no move and nothing in flight — mirror RevealSize's deadband
        if (dyn.Kind == DynamicsKind.Spring)
        {
            var sp = SpringParams.FromResponse(dyn.Response, dyn.DampingRatio);
            Track? ex = Find(node, ch);
            if (ex is { Mode: IntegrationMode.Spring })
            {
                ex.Pos += delta;              // coordinate frame shifted by the layout move → shift offset, keep velocity
                ex.Target = 0f; ex.Spring = sp; ex.Done = false;
            }
            else Spring(node, ch, 0f, sp, initial: delta, delayMs: delayMs);   // fresh: presented stays put, the offset springs delta → 0
        }
        else
        {
            float cur = CurrentValue(node, ch);
            Animate(node, ch, cur + delta, 0f, dyn.DurationMs, dyn.Easing, delayMs: delayMs);   // tween: interruption restarts (spring is default)
        }
    }

    private Track Find(NodeHandle node, AnimChannel ch)
    {
        for (int i = 0; i < _tracks.Count; i++) if (_tracks[i].Node == node && _tracks[i].Channel == ch) return _tracks[i];
        return null!;
    }
    private Track Get(NodeHandle node, AnimChannel ch, CompositeOp composite)
    {
        // Replace = the single "base" track per channel (retargets). Add/Accumulate = additive layers (stack).
        if (composite == CompositeOp.Replace)
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].Node == node && _tracks[i].Channel == ch && _tracks[i].Composite == CompositeOp.Replace)
                    return _tracks[i];
        var nt = new Track { Node = node, Channel = ch, Composite = composite };
        _tracks.Add(nt);
        return nt;
    }

    // ── Tick (phase 7) ──────────────────────────────────────────────────────────────────────────
    public void Tick(float dtMs)
    {
        if (_tracks.Count == 0) return;   // steady frame: zero work / zero alloc
        _scratch.Clear();
        if (Diag.Enabled) Diag.Event("anim", $"── tick dt={dtMs:0.#}ms tracks={_tracks.Count} ──");

        // pass 0: advance every track, compute its value (sets Done on eased completion / spring rest)
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track t = _tracks[i];
            if (!_scene.IsLive(t.Node)) { RemoveTrackAt(i); continue; }
            float stepMs = dtMs;
            // A JUST-seeded track must not consume its delay from THIS frame's dt: the dt accumulated BEFORE the
            // seed (idle frames roll their pending time into the next active frame), so charging it against the
            // delay starts the track early — a delayed track seeded after an idle gap lost its entire begin time.
            // JustSeeded already guards ElapsedMs below for exactly this reason; the delay needs the same guard.
            if (!t.Done && t.DelayRemainingMs > 0f && stepMs > 0f && !t.JustSeeded)
            {
                float consume = MathF.Min(t.DelayRemainingMs, stepMs);
                t.DelayRemainingMs -= consume;
                stepMs -= consume;
                if (t.DelayRemainingMs > 0f || stepMs <= 0f)
                {
                    // Hold the terminal's initial value while delayed. The track still contributes to scratch so the
                    // first delayed frames are recorded at the authored starting transform/opacity instead of snapping.
                    t.Value = t.Mode == IntegrationMode.Spring ? t.Pos : Sample(t.Keys, 0f);
                    if (!_scratch.ContainsKey(t.Node)) _scratch[t.Node] = Accum.FromPaint(in _scene.Paint(t.Node));
                    continue;
                }
            }

            if (t.Mode == IntegrationMode.Spring)
            {
                if (!t.Done)
                {
                    if (t.JustSeeded || stepMs <= 0f)
                    {
                        t.JustSeeded = false;
                    }
                    else
                    {
                        // semi-implicit (symplectic) Euler, sub-stepped for stability at frame spikes
                        float dt = stepMs * 0.001f;
                        int n = Math.Clamp((int)MathF.Ceiling(dt / 0.004f), 1, 8);
                        float h = dt / n;
                        for (int s = 0; s < n; s++)
                        {
                            float a = (t.Spring.Stiffness * (t.Target - t.Pos) - t.Spring.Damping * t.Vel) / t.Spring.Mass;
                            t.Vel += a * h;
                            t.Pos += t.Vel * h;
                        }
                    }
                    if (MathF.Abs(t.Target - t.Pos) < t.Spring.RestEps && MathF.Abs(t.Vel) < t.Spring.RestEps * 50f)
                    { t.Pos = t.Target; t.Vel = 0f; t.Done = true; }
                }
                t.Value = t.Pos;
            }
            else
            {
                float u;
                if (t.DrivenRef >= 0)
                {
                    t.JustSeeded = false;
                    float src = Clocks.Sample(t.DrivenRef);
                    u = t.DomainMax == t.DomainMin ? 0f : Math.Clamp((src - t.DomainMin) / (t.DomainMax - t.DomainMin), 0f, 1f);
                }
                else
                {
                    if (t.JustSeeded) t.JustSeeded = false;
                    else t.ElapsedMs += stepMs;
                    u = t.DurationMs <= 0f ? 1f : t.ElapsedMs / t.DurationMs;
                    if (t.Loop) u -= MathF.Floor(u); else if (u >= 1f) { u = 1f; t.Done = true; }
                }
                t.Value = Sample(t.Keys, u);
            }

            if (Diag.Enabled)
            {
                Diag.Event("anim", $"  {t.Channel} {t.Mode} val={t.Value:0.###}" +
                    (t.Mode == IntegrationMode.Spring ? $" vel={t.Vel:0.##} tgt={t.Target:0.###} done={t.Done}" : $" elapsed={t.ElapsedMs:0}ms done={t.Done}"));
            }
            if (!_scratch.ContainsKey(t.Node)) _scratch[t.Node] = Accum.FromPaint(in _scene.Paint(t.Node));
        }

        // fold Replace tracks first (the base), then additive layers — so order can't clobber the base
        for (int i = 0; i < _tracks.Count; i++)
            if (_tracks[i].Composite == CompositeOp.Replace) Fold(_tracks[i]);
        for (int i = 0; i < _tracks.Count; i++)
            if (_tracks[i].Composite != CompositeOp.Replace) Fold(_tracks[i]);

        // compose each animated node's channels → LocalTransform (T∘R∘S) + Opacity
        foreach (var kv in _scratch)
        {
            ref NodePaint p = ref _scene.Paint(kv.Key);
            Accum acc = kv.Value;
            var tf = Affine2D.Translation(acc.Tx, acc.Ty);
            if (acc.Rot != 0f) tf = tf.Multiply(Affine2D.Rotation(acc.Rot * (MathF.PI / 180f)));
            if (acc.Sx != 1f || acc.Sy != 1f) tf = tf.Multiply(Affine2D.Scale(acc.Sx, acc.Sy));
            p.LocalTransform = tf;
            p.Opacity = acc.Op;
            // Presented extent: Reveal draws the fill + child-clip at this size (no layout). Relayout instead feeds it to
            // the host, which writes it to LayoutInput and re-solves the subtree (live reflow).
            if (!float.IsNaN(acc.Sw)) p.PresentedW = acc.Sw;
            if (!float.IsNaN(acc.Sh)) p.PresentedH = acc.Sh;
            if (!float.IsNaN(acc.TrimStart)) p.StrokeTrimStart = acc.TrimStart;
            if (!float.IsNaN(acc.TrimEnd)) p.StrokeTrimEnd = acc.TrimEnd;
            // Authored clip-rect (node-local): an un-animated edge defaults to the node's own box edge (= no clip there),
            // so a one-edge reveal (e.g. ClipB 0→H) clips only that side. Composed with ClipsToBounds by the recorder.
            if (!float.IsNaN(acc.ClipL) || !float.IsNaN(acc.ClipT) || !float.IsNaN(acc.ClipR) || !float.IsNaN(acc.ClipB))
            {
                ref RectF cb = ref _scene.Bounds(kv.Key);
                float cl = float.IsNaN(acc.ClipL) ? 0f : acc.ClipL;
                float ct = float.IsNaN(acc.ClipT) ? 0f : acc.ClipT;
                float cr = float.IsNaN(acc.ClipR) ? cb.W : acc.ClipR;
                float cbm = float.IsNaN(acc.ClipB) ? cb.H : acc.ClipB;
                p.ClipRect = RectF.FromLTRB(cl, ct, cr, cbm);
            }
            _scene.Mark(kv.Key, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            if ((!float.IsNaN(acc.Sw) || !float.IsNaN(acc.Sh)) && (_scene.Flags(kv.Key) & NodeFlags.Relayouting) != 0)
                IncrementalRoots.Add(kv.Key);
            // SizeMode.Reflow: write the interpolated size into LAYOUT input and dirty the PARENT (the node itself
            // could self-classify as a boundary once its size is explicit) — the host re-solves the boundary scope
            // right after this tick, so siblings reflow at the eased size before record.
            if (!float.IsNaN(acc.Lw) || !float.IsNaN(acc.Lh))
            {
                ref LayoutInput li = ref _scene.Layout(kv.Key);
                if (!float.IsNaN(acc.Lw)) li.Width = acc.Lw;
                if (!float.IsNaN(acc.Lh)) li.Height = acc.Lh;
                var rp = _scene.Parent(kv.Key);
                _scene.Mark(rp.IsNull ? kv.Key : rp, NodeFlags.LayoutDirty);
                _reflowWrote = true;
                ReflowRoots.Add(kv.Key);   // one entry per node per tick (scratch is per-node)
            }
        }

        // free finished tracks (eased non-loop completion / settled springs). Driven + loop tracks persist.
        // A settled Reveal resets its presented extent to NaN so the recorder falls back to the (equal) layout size —
        // otherwise a later model resize without a reveal would draw at the stale presented size.
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track t = _tracks[i];
            if (!t.Done) continue;
            // A settled REFLOW track restores the element-DECLARED LayoutInput (NaN/auto or explicit) — numerically
            // equal to the just-reached target, so the final boundary re-solve is visually a no-op — and returns the
            // node to normal layout ownership. The Trailing child-shift rests at 0.
            if (t.Channel is AnimChannel.LayoutW or AnimChannel.LayoutH && _scene.IsLive(t.Node))
            {
                ref LayoutInput rli = ref _scene.Layout(t.Node);
                if (t.Channel == AnimChannel.LayoutW) rli.Width = t.RestoreTo; else rli.Height = t.RestoreTo;
                ref NodePaint rp = ref _scene.Paint(t.Node);
                rp.ChildShiftX = 0f; rp.ChildShiftY = 0f;
                var rpar = _scene.Parent(t.Node);
                _scene.Mark(rpar.IsNull ? t.Node : rpar, NodeFlags.LayoutDirty);
                _scene.Mark(t.Node, NodeFlags.PaintDirty);
                _reflowWrote = true;
            }
            bool isReveal = t.Channel is AnimChannel.SizeW or AnimChannel.SizeH or AnimChannel.StrokeTrimStart or AnimChannel.StrokeTrimEnd
                or AnimChannel.ClipL or AnimChannel.ClipT or AnimChannel.ClipR or AnimChannel.ClipB;
            if (isReveal && _scene.IsLive(t.Node))
            {
                // A settled Relayout-mode size track restores the element-DECLARED LayoutInput (the host overwrote it
                // with the interpolated value every tick) and queues one final re-solve at the declared value.
                if (t.RestoreLayout && t.Channel is AnimChannel.SizeW or AnimChannel.SizeH)
                {
                    ref LayoutInput rli = ref _scene.Layout(t.Node);
                    if (t.Channel == AnimChannel.SizeW) rli.Width = t.RestoreTo; else rli.Height = t.RestoreTo;
                    _scene.Mark(t.Node, NodeFlags.LayoutDirty);
                    _reflowWrote = true;   // the host's phase-7 layout pass consumes this frame's marks
                }
                ref NodePaint p = ref _scene.Paint(t.Node);
                if (t.Channel == AnimChannel.SizeW) { p.PresentedW = float.NaN; _scene.Unmark(t.Node, NodeFlags.Relayouting); }
                else if (t.Channel == AnimChannel.SizeH) p.PresentedH = float.NaN;
                else if (t.Channel == AnimChannel.StrokeTrimStart) p.StrokeTrimStart = float.NaN;
                else if (t.Channel == AnimChannel.StrokeTrimEnd) p.StrokeTrimEnd = float.NaN;
                else p.ClipRect = RectF.Infinite;   // any clip edge settling clears the authored clip override
                _scene.Mark(t.Node, NodeFlags.PaintDirty);
            }
            RemoveTrackAt(i);
        }
    }

    private void Fold(Track t)
    {
        Accum acc = _scratch.TryGetValue(t.Node, out var a) ? a : Accum.Default;
        acc.Fold(t.Channel, t.Value, t.Composite);
        _scratch[t.Node] = acc;
    }

    // sample multi-keyframe track at progress u (0..1), per-segment easing
    private static float Sample(Keyframe[] keys, float u)
    {
        if (keys.Length == 0) return 0f;
        if (keys.Length == 1 || u <= keys[0].Offset) return keys[0].Value;
        if (u >= keys[^1].Offset) return keys[^1].Value;
        int i = 0;
        while (i < keys.Length - 1 && keys[i + 1].Offset < u) i++;
        Keyframe a = keys[i], b = keys[i + 1];
        float span = b.Offset - a.Offset;
        float local = span <= 0f ? 1f : (u - a.Offset) / span;
        return a.Value + (b.Value - a.Value) * Easings.Ease(b.Easing, local);
    }

    /// <summary>Evaluate an easing curve (kept for source compatibility; the implementation lives in Foundation).</summary>
    public static float Ease(Easing e, float t) => Easings.Ease(e, t);
}
