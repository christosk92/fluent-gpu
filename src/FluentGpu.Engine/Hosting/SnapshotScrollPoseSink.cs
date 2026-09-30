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
        ref NodePaint p = ref scene.CompositorPaint(h, changed);
        AppHost.ApplyEffectChannel(ref p, channel, value);
        if (changed) RecordRequired = true;
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
