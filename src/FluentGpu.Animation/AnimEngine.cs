using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>Animatable channels. Transform channels compose into LocalTransform (TransformDirty); Opacity + the presented
/// SizeW/SizeH (the "Reveal" presented extent the recorder draws the fill + child-clip at) → PaintDirty. None relayout.</summary>
public enum AnimChannel : byte { TranslateX, TranslateY, ScaleX, ScaleY, Rotation, Opacity, SizeW, SizeH, StrokeTrimStart, StrokeTrimEnd, ClipL, ClipT, ClipR, ClipB }

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
        public bool Loop;
        public int DrivenRef = -1;            // -1 = wall-clock; else index into the DrivenClockTable
        public bool JustSeeded;
        public float DomainMin, DomainMax;    // driven: maps source value → progress
        // spring
        public float Pos, Vel, Target;
        public SpringParams Spring;
        public bool Done;
        public float Value;   // value computed this tick (folded after advancing all tracks)
    }

    private struct Accum
    {
        public float Tx, Ty, Sx, Sy, Rot, Op, Sw, Sh, TrimStart, TrimEnd;
        public float ClipL, ClipT, ClipR, ClipB;   // authored clip-rect edges (node-local); NaN = that edge not animated
        public static Accum Default => new() { Tx = 0, Ty = 0, Sx = 1, Sy = 1, Rot = 0, Op = 1, Sw = float.NaN, Sh = float.NaN, TrimStart = float.NaN, TrimEnd = float.NaN, ClipL = float.NaN, ClipT = float.NaN, ClipR = float.NaN, ClipB = float.NaN };
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

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>Eased two-point tween (retargets any live track on the same node+channel).</summary>
    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs,
                        Easing easing = Easing.EaseInOut, CompositeOp composite = CompositeOp.Replace)
        => Keyframes(node, channel, [new(0f, from, Easing.Linear), new(1f, to, easing)], durationMs, false, composite);

    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs,
                        EasingSpec easing, CompositeOp composite = CompositeOp.Replace)
        => Keyframes(node, channel, [new(0f, from, Easing.Linear), new(1f, to, easing)], durationMs, false, composite);

    /// <summary>Multi-keyframe eased track (@keyframes). Offsets must be ascending in 0..1.</summary>
    public void Keyframes(NodeHandle node, AnimChannel channel, Keyframe[] keys, float durationMs,
                          bool loop = false, CompositeOp composite = CompositeOp.Replace)
    {
        var t = Get(node, channel, composite);
        t.Mode = IntegrationMode.Eased; t.Keys = keys; t.DurationMs = durationMs; t.ElapsedMs = 0f;
        t.Loop = loop; t.DrivenRef = -1; t.Done = false; t.JustSeeded = true;
        if (Diag.Enabled) Diag.Event("anim", $"keyframes SEED {channel} dur={durationMs:0}ms keys={keys.Length} loop={loop}");
    }

    /// <summary>Scroll/value-driven track: progress comes from a DrivenClock source mapped through [domainMin,domainMax].</summary>
    public void Drive(NodeHandle node, AnimChannel channel, Keyframe[] keys, int drivenRef, float domainMin, float domainMax,
                      CompositeOp composite = CompositeOp.Replace)
    {
        var t = Get(node, channel, composite);
        t.Mode = IntegrationMode.Eased; t.Keys = keys; t.DrivenRef = drivenRef;
        t.DomainMin = domainMin; t.DomainMax = domainMax; t.Done = false; t.JustSeeded = true;
    }

    /// <summary>Spring toward <paramref name="to"/>. If a spring already runs on this node+channel, it RETARGETS — keeping
    /// position + velocity (no snap), the iOS/Compose velocity-handoff.</summary>
    public void Spring(NodeHandle node, AnimChannel channel, float to, in SpringParams spring,
                       float? initial = null, CompositeOp composite = CompositeOp.Replace)
    {
        Track? existing = Find(node, channel);
        if (existing is { Mode: IntegrationMode.Spring })
        {
            if (Diag.Enabled) Diag.Event("anim", $"spring RETARGET {channel} pos={existing.Pos:0.###} vel={existing.Vel:0.##} → {to:0.###}");
            existing.Target = to; existing.Spring = spring; existing.Done = false; existing.Composite = composite; existing.JustSeeded = false;
            return;   // keep Pos + Vel → smooth handoff
        }
        var t = Get(node, channel, composite);
        t.Mode = IntegrationMode.Spring; t.Target = to; t.Spring = spring;
        // a FRESH spring starts from the node's CURRENT value (not the target) so it actually travels — else it snaps.
        t.Pos = initial ?? CurrentValue(node, channel); t.Vel = 0f; t.Done = false; t.JustSeeded = true;
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
            AnimChannel.StrokeTrimStart => !float.IsNaN(p.StrokeTrimStart) ? p.StrokeTrimStart : (_scene.TryGetPolylineStroke(node, out var ps) ? ps.TrimStart : 0f),
            AnimChannel.StrokeTrimEnd => !float.IsNaN(p.StrokeTrimEnd) ? p.StrokeTrimEnd : (_scene.TryGetPolylineStroke(node, out var pe) ? pe.TrimEnd : 1f),
            _ => 0f,   // Rotation: not cleanly recoverable from a scaled matrix; springs from 0
        };
    }

    public void Cancel(NodeHandle node, AnimChannel channel)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Node == node && _tracks[i].Channel == channel) _tracks.RemoveAt(i);
    }
    public void CancelAll(NodeHandle node)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Node == node) _tracks.RemoveAt(i);
    }

    // ── Layout-transition side-table (node index → spec) ──────────────────────────────────────────
    /// <summary>Attach (or replace) a node's layout-transition spec. Called by the reconciler from BoxEl.Animate.</summary>
    public void SetTransition(NodeHandle node, in LayoutTransition t) => _transitions[(int)node.Raw.Index] = t;
    /// <summary>Read a node's layout-transition spec (set by the reconciler this commit).</summary>
    public bool TryGetTransition(NodeHandle node, out LayoutTransition t) => _transitions.TryGetValue((int)node.Raw.Index, out t);
    /// <summary>Drop a node's layout-transition spec (the element stopped declaring Animate, or the slot was freed).</summary>
    public void ClearTransition(NodeHandle node) => _transitions.Remove((int)node.Raw.Index);

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
            ReframePosition(node, AnimChannel.TranslateX, fromAbs.X - toAbs.X, dyn);
            ReframePosition(node, AnimChannel.TranslateY, fromAbs.Y - toAbs.Y, dyn);
        }
        if ((spec.Channels & TransitionChannels.Size) != 0)
        {
            SizeMode mode = spec.Size == SizeMode.Auto ? SizeMode.Reveal : spec.Size;
            switch (mode)
            {
                case SizeMode.Reveal:
                    RevealSize(node, AnimChannel.SizeW, fromAbs.W, toAbs.W, dyn);
                    RevealSize(node, AnimChannel.SizeH, fromAbs.H, toAbs.H, dyn);
                    break;
                case SizeMode.ScaleCorrect:   // GPU scale toward 1 (children that opt in counter-scale in the recorder)
                    if (toAbs.W > 0.5f) ScaleReveal(node, AnimChannel.ScaleX, fromAbs.W / toAbs.W, dyn);
                    if (toAbs.H > 0.5f) ScaleReveal(node, AnimChannel.ScaleY, fromAbs.H / toAbs.H, dyn);
                    break;
                case SizeMode.Relayout:        // re-solve the subtree at the interpolated size each tick (live reflow)
                    RevealSize(node, AnimChannel.SizeW, fromAbs.W, toAbs.W, dyn);
                    RevealSize(node, AnimChannel.SizeH, fromAbs.H, toAbs.H, dyn);
                    _scene.Mark(node, NodeFlags.Relayouting);
                    break;
            }
        }
    }

    /// <summary>Nodes whose presented SIZE changed this tick under SizeMode.Relayout — the host re-solves just these
    /// subtrees (scoped layout) so their text re-wraps live. Cleared by the host after it consumes them.</summary>
    public List<NodeHandle> IncrementalRoots { get; } = new();

    // Presented-extent reveal: spring/tween the recorder's drawn size old → new (fresh starts at the old size; a running
    // reveal retargets keeping Pos+Vel). Works for grow AND shrink — the presented size can exceed the model bounds.
    private void RevealSize(NodeHandle node, AnimChannel ch, float fromSize, float toSize, in TransitionDynamics dyn)
    {
        if (MathF.Abs(fromSize - toSize) < 0.5f && Find(node, ch) is null) return;   // no change and nothing in flight
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, toSize, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: fromSize);
        else
            Animate(node, ch, fromSize, toSize, dyn.DurationMs, dyn.Easing);
    }

    // ScaleCorrect: spring a scale channel from old/new → 1 (the recorder composites it about the node centre; opted-in
    // children counter-scale to stay undistorted). Cheap + compositor-only, but distorts text/borders — chrome only.
    private void ScaleReveal(NodeHandle node, AnimChannel ch, float fromRatio, in TransitionDynamics dyn)
    {
        if (MathF.Abs(fromRatio - 1f) < 0.001f && Find(node, ch) is null) return;
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, 1f, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: fromRatio);
        else
            Animate(node, ch, fromRatio, 1f, dyn.DurationMs, dyn.Easing);
    }

    // ── enter / exit (appearing & disappearing nodes) ────────────────────────────────────────────
    /// <summary>An inserted node animates FROM the enter terminal (offset/scale/opacity) TO identity.</summary>
    public void SeedEnter(NodeHandle node, in EnterExit e, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.Dynamics);
        if (e.Opacity != 1f) SeedTerminal(node, AnimChannel.Opacity, 1f, dyn, initial: e.Opacity);
        if (e.Dx != 0f) SeedTerminal(node, AnimChannel.TranslateX, 0f, dyn, initial: e.Dx);
        if (e.Dy != 0f) SeedTerminal(node, AnimChannel.TranslateY, 0f, dyn, initial: e.Dy);
        if (e.Sx != 1f) SeedTerminal(node, AnimChannel.ScaleX, 1f, dyn, initial: e.Sx);
        if (e.Sy != 1f) SeedTerminal(node, AnimChannel.ScaleY, 1f, dyn, initial: e.Sy);
    }

    /// <summary>A removed node (now an Exiting orphan) animates FROM its current state TO the exit terminal; when all its
    /// tracks settle (<see cref="HasTracks"/> == false) the host reclaims it.</summary>
    public void SeedExit(NodeHandle node, in EnterExit e, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(spec.ExitDynamics ?? spec.Dynamics);   // asymmetric exit timing when set
        SeedTerminal(node, AnimChannel.Opacity, e.Opacity, dyn);   // always (the exit-settle signal)
        if (e.Dx != 0f) SeedTerminal(node, AnimChannel.TranslateX, e.Dx, dyn);
        if (e.Dy != 0f) SeedTerminal(node, AnimChannel.TranslateY, e.Dy, dyn);
        if (e.Sx != 1f) SeedTerminal(node, AnimChannel.ScaleX, e.Sx, dyn);
        if (e.Sy != 1f) SeedTerminal(node, AnimChannel.ScaleY, e.Sy, dyn);
    }

    private void SeedTerminal(NodeHandle node, AnimChannel ch, float to, in TransitionDynamics dyn, float? initial = null)
    {
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial);
        else
            Animate(node, ch, initial ?? CurrentValue(node, ch), to, dyn.DurationMs, dyn.Easing);
    }

    /// <summary>True while any track targets this node (used by the host to detect a settled exit orphan).</summary>
    public bool HasTracks(NodeHandle node)
    {
        for (int i = 0; i < _tracks.Count; i++) if (_tracks[i].Node == node) return true;
        return false;
    }

    /// <summary>A default-constructed spec (all-zero dynamics) means "use the defaults" — fill them in.</summary>
    private static TransitionDynamics Normalize(in TransitionDynamics d)
        => d.Kind == DynamicsKind.Spring
            ? (d.Response > 0f ? d : TransitionDynamics.Default)
            : (d.DurationMs > 0f ? d : TransitionDynamics.Tween(200f, d.Easing));

    private void ReframePosition(NodeHandle node, AnimChannel ch, float delta, in TransitionDynamics dyn)
    {
        if (dyn.Kind == DynamicsKind.Spring)
        {
            var sp = SpringParams.FromResponse(dyn.Response, dyn.DampingRatio);
            Track? ex = Find(node, ch);
            if (ex is { Mode: IntegrationMode.Spring })
            {
                ex.Pos += delta;              // coordinate frame shifted by the layout move → shift offset, keep velocity
                ex.Target = 0f; ex.Spring = sp; ex.Done = false;
            }
            else Spring(node, ch, 0f, sp, initial: delta);   // fresh: presented stays put, the offset springs delta → 0
        }
        else
        {
            float cur = CurrentValue(node, ch);
            Animate(node, ch, cur + delta, 0f, dyn.DurationMs, dyn.Easing);   // tween: interruption restarts (spring is default)
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
            if (!_scene.IsLive(t.Node)) { _tracks.RemoveAt(i); continue; }

            if (t.Mode == IntegrationMode.Spring)
            {
                if (!t.Done)
                {
                    if (t.JustSeeded || dtMs <= 0f)
                    {
                        t.JustSeeded = false;
                    }
                    else
                    {
                        // semi-implicit (symplectic) Euler, sub-stepped for stability at frame spikes
                        float dt = dtMs * 0.001f;
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
                    else t.ElapsedMs += dtMs;
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
        }

        // free finished tracks (eased non-loop completion / settled springs). Driven + loop tracks persist.
        // A settled Reveal resets its presented extent to NaN so the recorder falls back to the (equal) layout size —
        // otherwise a later model resize without a reveal would draw at the stale presented size.
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track t = _tracks[i];
            if (!t.Done) continue;
            bool isReveal = t.Channel is AnimChannel.SizeW or AnimChannel.SizeH or AnimChannel.StrokeTrimStart or AnimChannel.StrokeTrimEnd
                or AnimChannel.ClipL or AnimChannel.ClipT or AnimChannel.ClipR or AnimChannel.ClipB;
            if (isReveal && _scene.IsLive(t.Node))
            {
                ref NodePaint p = ref _scene.Paint(t.Node);
                if (t.Channel == AnimChannel.SizeW) { p.PresentedW = float.NaN; _scene.Unmark(t.Node, NodeFlags.Relayouting); }
                else if (t.Channel == AnimChannel.SizeH) p.PresentedH = float.NaN;
                else if (t.Channel == AnimChannel.StrokeTrimStart) p.StrokeTrimStart = float.NaN;
                else if (t.Channel == AnimChannel.StrokeTrimEnd) p.StrokeTrimEnd = float.NaN;
                else p.ClipRect = RectF.Infinite;   // any clip edge settling clears the authored clip override
                _scene.Mark(t.Node, NodeFlags.PaintDirty);
            }
            _tracks.RemoveAt(i);
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
