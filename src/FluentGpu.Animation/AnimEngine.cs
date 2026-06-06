using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>Animatable channels. Transform channels compose into LocalTransform (TransformDirty); Opacity → PaintDirty. None relayout.</summary>
public enum AnimChannel : byte { TranslateX, TranslateY, ScaleX, ScaleY, Rotation, Opacity }

public enum Easing : byte { Linear, EaseIn, EaseOut, EaseInOut, Sine, Quad, Cubic, Expo, Back, Elastic, Bounce }

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
public readonly record struct Keyframe(float Offset, float Value, Easing Easing = Easing.EaseInOut);

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
        public float DomainMin, DomainMax;    // driven: maps source value → progress
        // spring
        public float Pos, Vel, Target;
        public SpringParams Spring;
        public bool Done;
        public float Value;   // value computed this tick (folded after advancing all tracks)
    }

    private struct Accum
    {
        public float Tx, Ty, Sx, Sy, Rot, Op;
        public static Accum Default => new() { Tx = 0, Ty = 0, Sx = 1, Sy = 1, Rot = 0, Op = 1 };
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
            }
        }
    }

    private readonly SceneStore _scene;
    private readonly List<Track> _tracks = new();
    private readonly Dictionary<NodeHandle, Accum> _scratch = new();
    public DrivenClockTable Clocks { get; } = new();

    public AnimEngine(SceneStore scene) => _scene = scene;
    public bool HasActive => _tracks.Count > 0;

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>Eased two-point tween (retargets any live track on the same node+channel).</summary>
    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs,
                        Easing easing = Easing.EaseInOut, CompositeOp composite = CompositeOp.Replace)
        => Keyframes(node, channel, [new(0f, from, Easing.Linear), new(1f, to, easing)], durationMs, false, composite);

    /// <summary>Multi-keyframe eased track (@keyframes). Offsets must be ascending in 0..1.</summary>
    public void Keyframes(NodeHandle node, AnimChannel channel, Keyframe[] keys, float durationMs,
                          bool loop = false, CompositeOp composite = CompositeOp.Replace)
    {
        var t = Get(node, channel, composite);
        t.Mode = IntegrationMode.Eased; t.Keys = keys; t.DurationMs = durationMs; t.ElapsedMs = 0f;
        t.Loop = loop; t.DrivenRef = -1; t.Done = false;
    }

    /// <summary>Scroll/value-driven track: progress comes from a DrivenClock source mapped through [domainMin,domainMax].</summary>
    public void Drive(NodeHandle node, AnimChannel channel, Keyframe[] keys, int drivenRef, float domainMin, float domainMax,
                      CompositeOp composite = CompositeOp.Replace)
    {
        var t = Get(node, channel, composite);
        t.Mode = IntegrationMode.Eased; t.Keys = keys; t.DrivenRef = drivenRef;
        t.DomainMin = domainMin; t.DomainMax = domainMax; t.Done = false;
    }

    /// <summary>Spring toward <paramref name="to"/>. If a spring already runs on this node+channel, it RETARGETS — keeping
    /// position + velocity (no snap), the iOS/Compose velocity-handoff.</summary>
    public void Spring(NodeHandle node, AnimChannel channel, float to, in SpringParams spring,
                       float? initial = null, CompositeOp composite = CompositeOp.Replace)
    {
        Track? existing = Find(node, channel);
        if (existing is { Mode: IntegrationMode.Spring })
        {
            existing.Target = to; existing.Spring = spring; existing.Done = false; existing.Composite = composite;
            return;   // keep Pos + Vel → smooth handoff
        }
        var t = Get(node, channel, composite);
        t.Mode = IntegrationMode.Spring; t.Target = to; t.Spring = spring;
        t.Pos = initial ?? to; t.Vel = 0f; t.Done = false;
    }

    public void Cancel(NodeHandle node, AnimChannel channel)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Node == node && _tracks[i].Channel == channel) _tracks.RemoveAt(i);
    }
    public void CancelAll(NodeHandle node)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Node == node) _tracks.RemoveAt(i);
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

        // pass 0: advance every track, compute its value (sets Done on eased completion / spring rest)
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track t = _tracks[i];
            if (!_scene.IsLive(t.Node)) { _tracks.RemoveAt(i); continue; }

            if (t.Mode == IntegrationMode.Spring)
            {
                if (!t.Done)
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
                    float src = Clocks.Sample(t.DrivenRef);
                    u = t.DomainMax == t.DomainMin ? 0f : Math.Clamp((src - t.DomainMin) / (t.DomainMax - t.DomainMin), 0f, 1f);
                }
                else
                {
                    t.ElapsedMs += dtMs;
                    u = t.DurationMs <= 0f ? 1f : t.ElapsedMs / t.DurationMs;
                    if (t.Loop) u -= MathF.Floor(u); else if (u >= 1f) { u = 1f; t.Done = true; }
                }
                t.Value = Sample(t.Keys, u);
            }

            if (!_scratch.ContainsKey(t.Node)) _scratch[t.Node] = Accum.Default;
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
            _scene.Mark(kv.Key, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }

        // free finished tracks (eased non-loop completion / settled springs). Driven + loop tracks persist.
        // The final value was just applied above; it stays in NodePaint until a re-render re-establishes the target.
        for (int i = _tracks.Count - 1; i >= 0; i--)
            if (_tracks[i].Done) _tracks.RemoveAt(i);
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
        return a.Value + (b.Value - a.Value) * Ease(b.Easing, local);
    }

    public static float Ease(Easing e, float t) => e switch
    {
        Easing.EaseIn => t * t,
        Easing.EaseOut => 1f - (1f - t) * (1f - t),
        Easing.EaseInOut => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
        Easing.Sine => 1f - MathF.Cos(t * MathF.PI * 0.5f),
        Easing.Quad => t * t,
        Easing.Cubic => t * t * t,
        Easing.Expo => t <= 0f ? 0f : MathF.Pow(2f, 10f * (t - 1f)),
        Easing.Back => t * t * (2.70158f * t - 1.70158f),
        Easing.Elastic => t == 0f || t == 1f ? t : -MathF.Pow(2f, 10f * (t - 1f)) * MathF.Sin((t - 1.075f) * (2f * MathF.PI) / 0.3f),
        Easing.Bounce => Bounce(t),
        _ => t,   // Linear
    };

    private static float Bounce(float t)
    {
        const float n1 = 7.5625f, d1 = 2.75f;
        if (t < 1f / d1) return n1 * t * t;
        if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1; return n1 * t * t + 0.984375f;
    }
}
