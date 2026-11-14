using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

public sealed partial class TreeReconciler
{
    /// <summary>Nodes that asked their image to animate (<c>ImageEl.Animate</c>), live or not yet swept (diagnostics).</summary>
    internal int AnimatedImageNodeCount => _animatedImageNodes.Count;

    /// <summary>UI thread, once per painted frame after layout and the image pump: name every animated image node that is in
    /// view this frame (<see cref="SceneStore.IsInView"/>) to the cache, which then arms, runs or pauses each playback
    /// (<see cref="ImageCache.TickAnimations"/>). No animated node: one count read. Allocation-free (a HashSet struct
    /// enumerator and scalar walks).</summary>
    internal void TickAnimatedImages(bool reducedMotion)
    {
        if (Images is null) return;
        if (_animatedImageNodes.Count > 0)
        {
            foreach (int idx in _animatedImageNodes)
            {
                var node = _scene.HandleAt(idx);
                if (node.IsNull || !_scene.IsLive(node)) continue;
                ref NodePaint paint = ref _scene.Paint(node);
                if (paint.VisualKind != VisualKind.Image || paint.ImageId == 0) continue;
                if (_scene.IsInView(node)) Images.NoteAnimationShown(paint.ImageId);
            }
        }
        Images.TickAnimations(reducedMotion);
    }
}
