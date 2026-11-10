using System.Collections.Generic;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Hosting;

/// <summary>The render-thread pose sink over an adopted snapshot's compositor overlay (render thread only,
/// zero-alloc): the content translate and every effect channel land in the overlay, never in the snapshot's authored
/// columns. Transform-class poses (the content translate, a scroll effect's folded transform) and the sticky clip
/// (<see cref="EffectChannel.ClipTop"/> — a composite-time clip on its slice marker) are COMPOSITE parameters
/// (<see cref="SceneRecordingSnapshot.CompositorPosePaint"/> — no dirty trail; one the slices cannot honour is a
/// baked pose the slice recorder checks itself); the other non-transform channels (opacity, presented height, the
/// collapse cut, child shift) change recorded bytes, so they mark the node like any compositor pose and set
/// <see cref="RecordRequired"/> — the render turn cannot be composite-only.</summary>
internal sealed class SnapshotScrollPoseSink : IScrollPoseSink
{
    private SceneRecordingSnapshot? _scene;
    // The last value this sink posed per (node index, non-transform channel): what the slices were recorded with whenever
    // it differs from the snapshot's authored column. Bounded by the scene's node capacity times the channels; an entry
    // of a freed node costs at most one spare re-record of the node that reuses its index.
    private readonly Dictionary<ulong, float> _posed = new();

    /// <summary>A non-transform channel changed on the tick since <see cref="Bind"/>.</summary>
    public bool RecordRequired { get; private set; }

    public void Bind(SceneRecordingSnapshot scene)
    {
        _scene = scene;
        RecordRequired = false;
    }

    public void PoseViewport(int vpNode, double shown) => _scene!.SetPosedOffset(vpNode, shown);

    public void PoseContent(int node, bool horizontal, float trans, bool changed)
    {
        var scene = _scene!;
        var h = scene.HandleAt(node);
        if (h.IsNull || !scene.IsLive(h)) return;
        var parent = scene.Parent(h);
        float zoom = 1f;
        if (!parent.IsNull && scene.TryGetScroll(parent, out var sc)) zoom = sc.ZoomFactor;
        ref NodePaint cp = ref scene.CompositorPosePaint(h);
        ScrollContentPose.WriteContentTransform(ref cp, in scene.Bounds(h), horizontal, trans, zoom);
    }

    public void PoseEffect(int node, EffectChannel channel, float value, bool changed)
    {
        var scene = _scene!;
        var h = scene.HandleAt(node);
        if (h.IsNull || !scene.IsLive(h)) return;
        if (AppHost.IsCompositeEffectChannel(channel))
        {
            AppHost.ApplyEffectChannel(ref scene.CompositorPosePaint(h), channel, value);
            return;
        }
        // A byte-changing pose re-records its slice only when the slice holds another value. It holds what the snapshot
        // presents (its authored column: the UI posed the same effect before the capture and marked the node itself when
        // that moved) unless THIS sink posed something else last (a render pose ahead of the UI's). `changed` cannot tell:
        // the poser reports every row of a newly adopted coverage as changed, so every publication of any UI frame (a
        // lyrics step, a seek tick) used to mark every such node, and a page's hero re-recorded on each of them.
        if (EngineSwitches.ScrollPoseMarkAll)
        {
            // --fg scroll-pose-mark-all: the reference arm (every row the poser reports changed marks its node)
            ref NodePaint all = ref scene.CompositorPaint(h, changed);
            AppHost.ApplyEffectChannel(ref all, channel, value);
            if (changed) RecordRequired = true;
            return;
        }
        NodePaint probe = scene.Paint(h);
        bool write = AppHost.ApplyEffectChannel(ref probe, channel, value);   // the presented paint is not this value
        ulong key = (ulong)(uint)node << 8 | (byte)channel;
        bool moved = _posed.TryGetValue(key, out float last) ? last != value : write;
        _posed[key] = value;
        if (!write && !moved) return;
        ref NodePaint p = ref scene.CompositorPaint(h, moved);
        AppHost.ApplyEffectChannel(ref p, channel, value);
        if (moved) RecordRequired = true;
    }

    public void PoseTransform(int node, in EffectTransform transform, bool changed)
    {
        var scene = _scene!;
        var h = scene.HandleAt(node);
        if (h.IsNull || !scene.IsLive(h)) return;
        ref NodePaint p = ref scene.CompositorPosePaint(h);
        AppHost.ApplyEffectTransform(ref p, in scene.Bounds(h), in transform);
    }
}
