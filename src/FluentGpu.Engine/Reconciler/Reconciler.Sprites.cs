using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// SpriteFieldEl.Instances : Prop<SpriteInstances> — the bound instance-buffer channel (visualizer F5). Reconciler.Series.cs
// with the type swapped: ONE BindEffect wired at mount; every fire copies the CURRENT view into the scene-owned pooled array
// (SceneStore.Sprites.cs) and marks the record dirty. Nothing allocates after the first fire.
public sealed partial class TreeReconciler
{
    private void BindSprites(NodeHandle node, SpriteFieldEl sf)
    {
        if (!sf.Instances.IsBound) return;
        var fx = new BindEffect<SpriteInstances>(Runtime, sf, static e => e is SpriteFieldEl x ? x.Instances : default);
        AddBinding(node, fx.Start(() =>
        {
            NodeBindingFireCount++;
            if (!_scene.IsLive(node)) return;
            SpriteInstances current = fx.Read();
            _scene.SetSprites(node, current.AsSpan());   // NoteCaptureChanged + MarkRecordDirty inside
            NodeBindingWriteCount++;
            _scene.Mark(node, NodeFlags.PaintDirty);
        }));
    }
}
