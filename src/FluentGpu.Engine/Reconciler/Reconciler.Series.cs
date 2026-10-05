using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// SeriesEl.Samples : Prop<SeriesSamples> — the bound sample-source channel. The ListRowEl.Cells shape
// (Reconciler.ListRow.cs): ONE BindEffect wired at mount, re-wired in place on a bound→bound re-render
// (Reconciler.Rewire.cs); every fire copies the CURRENT view into the scene-owned pooled array (SceneStore.Series.cs)
// and marks the record dirty. The thunk's version read is what subscribes it; nothing allocates after the first fire.
public sealed partial class TreeReconciler
{
    private void BindSeriesSamples(NodeHandle node, SeriesEl se)
    {
        if (se.GradientMix.IsBound)
        {
            var mx = new BindEffect<float>(Runtime, se, static e => e is SeriesEl x ? x.GradientMix : default);
            AddBinding(node, mx.Start(() =>
            {
                NodeBindingFireCount++;
                if (!_scene.IsLive(node)) return;
                float mix = mx.Read();
                float prev = _scene.TryGetGradientMix(node, out float m) ? m : 0f;
                float next = float.IsFinite(mix) ? Math.Clamp(mix, 0f, 1f) : 0f;
                if (prev == next) return;
                NodeBindingWriteCount++;
                _scene.SetGradientMix(node, next);
            }));
        }
        if (!se.Samples.IsBound) return;
        var fx = new BindEffect<SeriesSamples>(Runtime, se, static e => e is SeriesEl x ? x.Samples : default);
        AddBinding(node, fx.Start(() =>
        {
            NodeBindingFireCount++;
            if (!_scene.IsLive(node)) return;
            SeriesSamples current = fx.Read();
            _scene.SetSeriesSamples(node, current.AsSpan());   // NoteCaptureChanged + MarkRecordDirty inside
            NodeBindingWriteCount++;
            _scene.Mark(node, NodeFlags.PaintDirty);
        }));
    }
}
