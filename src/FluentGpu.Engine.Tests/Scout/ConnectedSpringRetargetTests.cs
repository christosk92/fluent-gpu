using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A SPRING connected fly whose dest re-lays mid-flight (the cover slot's estimated size fixing up) is retargeted:
/// the overlay's model box moves to the new rect and its transform is re-based so the frame's pixels hold still. The
/// spring rows were re-aimed with <c>Spring(initial:)</c>, whose retarget branch ignores <c>initial</c> and continues from
/// the row's OLD-basis scale/translate, so the next tick applied the old scale to the new, larger box: the overlay jumped.</summary>
public sealed class ConnectedSpringRetargetTests
{
    private sealed class NeverDecoder : IImageDecoder
    {
        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible) => false;
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }
    }

    // The rect the recorder draws: T(bounds) ∘ about-centre(local transform).
    private static RectF Visual(SceneStore scene, NodeHandle node)
    {
        RectF b = scene.Bounds(node);
        var t = scene.Paint(node).LocalTransform;
        float w = b.W * t.M11, h = b.H * t.M22;
        float cx = b.X + b.W * 0.5f + t.Dx, cy = b.Y + b.H * 0.5f + t.Dy;
        return new RectF(cx - w * 0.5f, cy - h * 0.5f, w, h);
    }

    /// <summary>Flies a 100px cover to a 200px dest for a few frames, optionally grows the dest to 240px, and returns the
    /// overlay's visual width before and after the next frame.</summary>
    private static (float Before, float After) StepAcrossRelayout(ConnectedMotion motion, bool relayout)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var images = new ImageCache(new NeverDecoder());
        var connected = new ConnectedAnimation(scene, anim, images);
        var image = images.Request("cover", 100, 100);

        scene.Root = scene.CreateNode(1);
        scene.Bounds(scene.Root) = new RectF(0f, 0f, 1000f, 800f);
        var source = scene.CreateNode(8);
        var dest = scene.CreateNode(8);
        scene.AppendChild(scene.Root, source);
        scene.AppendChild(scene.Root, dest);
        scene.Bounds(source) = new RectF(20f, 20f, 100f, 100f);
        scene.Bounds(dest) = new RectF(400f, 100f, 200f, 200f);
        foreach (var n in new[] { source, dest })
        {
            scene.SetFlagBits(n, NodeFlags.Visible);
            scene.Paint(n).VisualKind = VisualKind.Image;
            scene.Paint(n).ImageId = image.Id;
        }
        connected.NoteTagged(source, "cover");
        connected.NoteTagged(dest, "cover");
        connected.Begin(new ConnectedTransitionRequest("cover", motion));

        void Frame() { connected.Tick65(); anim.Tick(16.67f); connected.Settle(); }
        for (int i = 0; i < 7; i++) Frame();

        var overlay = scene.OverlayAt(0);
        if (relayout) scene.Bounds(dest) = new RectF(400f, 100f, 240f, 240f);
        connected.Tick65();
        connected.Settle();                                   // the retarget: re-bases, pixels unchanged this frame
        float before = Visual(scene, overlay).W;
        Frame();
        return (before, Visual(scene, overlay).W);
    }

    [Fact]
    public void ASpringFly_RetargetedAtARelaidDest_DoesNotJump()
    {
        var spring = ConnectedMotion.Springy(SpringParams.FromResponse(0.5f, 1f));
        var (b0, a0) = StepAcrossRelayout(spring, relayout: false);
        var (b1, a1) = StepAcrossRelayout(spring, relayout: true);

        Assert.Equal(b0, b1, 0.01f);                         // the retarget frame itself holds the pixels
        // One frame of the bent flight moves about as far as the un-bent one (the target moved by 40px, so the spring
        // pulls only slightly harder); an old-basis scale applied to the 240px box jumps by ~0.68 * 40 = 27px.
        Assert.InRange(a1 - b1, (a0 - b0) - 1f, (a0 - b0) + 6f);
    }
}
