using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>Animatable channel. Opacity → PaintDirty; the translate channels → TransformDirty. None mark LayoutDirty.</summary>
public enum AnimChannel : byte { Opacity, TranslateX, TranslateY }

public enum Easing : byte { Linear, EaseIn, EaseOut, EaseInOut }

/// <summary>
/// Composition-style timeline engine (phase 7, design/subsystems/backdrop-effects-animation.md). Eased value tracks
/// drive a node's Opacity / LocalTransform translation and mark only Paint/Transform dirty — animation NEVER relays
/// out. At most one track per (node, channel) — a new Animate retargets. Springs / scale+rotation compose / driven
/// clocks / shared-element are the remaining work; this is the eased-track core.
/// </summary>
public sealed class AnimEngine
{
    private struct Track
    {
        public NodeHandle Node;
        public AnimChannel Channel;
        public float From, To, ElapsedMs, DurationMs;
        public Easing Easing;
    }

    private readonly SceneStore _scene;
    private readonly List<Track> _tracks = new();

    public AnimEngine(SceneStore scene) => _scene = scene;

    public bool HasActive => _tracks.Count > 0;

    /// <summary>Start (or retarget) an eased animation of <paramref name="channel"/> on <paramref name="node"/>.</summary>
    public void Animate(NodeHandle node, AnimChannel channel, float from, float to, float durationMs, Easing easing = Easing.EaseInOut)
    {
        var t = new Track { Node = node, Channel = channel, From = from, To = to, DurationMs = durationMs, Easing = easing, ElapsedMs = 0f };
        for (int i = 0; i < _tracks.Count; i++)
            if (_tracks[i].Node == node && _tracks[i].Channel == channel) { _tracks[i] = t; return; }
        _tracks.Add(t);
    }

    public void CancelAll(NodeHandle node)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Node == node) _tracks.RemoveAt(i);
    }

    /// <summary>Advance every track by <paramref name="dtMs"/>, write the eased value, mark dirty, drop completed/dead.</summary>
    public void Tick(float dtMs)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track t = _tracks[i];
            if (!_scene.IsLive(t.Node)) { _tracks.RemoveAt(i); continue; }

            t.ElapsedMs += dtMs;
            float u = t.DurationMs <= 0f ? 1f : Math.Clamp(t.ElapsedMs / t.DurationMs, 0f, 1f);
            float v = t.From + (t.To - t.From) * Ease(t.Easing, u);
            Apply(t.Node, t.Channel, v);

            if (u >= 1f) _tracks.RemoveAt(i);
            else _tracks[i] = t;
        }
    }

    private void Apply(NodeHandle node, AnimChannel channel, float v)
    {
        ref NodePaint p = ref _scene.Paint(node);
        switch (channel)
        {
            case AnimChannel.Opacity:
                p.Opacity = v;
                _scene.Mark(node, NodeFlags.PaintDirty);
                break;
            case AnimChannel.TranslateX:
                p.LocalTransform = p.LocalTransform with { Dx = v };
                _scene.Mark(node, NodeFlags.TransformDirty);
                break;
            case AnimChannel.TranslateY:
                p.LocalTransform = p.LocalTransform with { Dy = v };
                _scene.Mark(node, NodeFlags.TransformDirty);
                break;
        }
    }

    private static float Ease(Easing e, float t) => e switch
    {
        Easing.EaseIn => t * t,
        Easing.EaseOut => 1f - (1f - t) * (1f - t),
        Easing.EaseInOut => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
        _ => t,   // Linear
    };
}
