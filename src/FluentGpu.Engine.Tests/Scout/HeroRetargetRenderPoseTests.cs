using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A connected fly whose dest re-lays mid-flight is retargeted on the UI thread from ITS view of the overlay - the
/// last compositor feedback pose it imported - while the render thread, which owns the overlay's rows, has kept flying it.
/// The re-based start must continue from the pixels on screen, never step the cover back to that older pose.</summary>
public sealed class HeroRetargetRenderPoseTests
{
    private sealed class NeverDecoder : IImageDecoder
    {
        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible) => false;
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }
    }

    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly AnimEngine Animation;
        public readonly ConnectedAnimation Connected;
        public readonly NodeHandle Dest;
        public readonly SceneRecordingSnapshot Snapshot = new();
        public readonly CompositorAnimationSnapshot Desired = new();
        public readonly RenderCompositorAnimations Renderer = new();

        public Rig(ConnectedMotion motion)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Animation = new AnimEngine(Scene) { RenderOwnsCompositor = true };
            var images = new ImageCache(new NeverDecoder());
            Connected = new ConnectedAnimation(Scene, Animation, images);
            var image = images.Request("cover", 100, 100);
            Scene.Root = Scene.CreateNode(1);
            Scene.Bounds(Scene.Root) = new RectF(0f, 0f, 1000f, 800f);
            var source = Scene.CreateNode(8);
            Dest = Scene.CreateNode(8);
            Scene.AppendChild(Scene.Root, source);
            Scene.AppendChild(Scene.Root, Dest);
            Scene.Bounds(source) = new RectF(20f, 20f, 100f, 100f);
            Scene.Bounds(Dest) = new RectF(400f, 100f, 200f, 200f);
            foreach (var n in new[] { source, Dest })
            {
                Scene.SetFlagBits(n, NodeFlags.Visible);
                Scene.Paint(n).VisualKind = VisualKind.Image;
                Scene.Paint(n).ImageId = image.Id;
            }
            Connected.NoteTagged(source, "cover");
            Connected.NoteTagged(Dest, "cover");
            Connected.Begin(new ConnectedTransitionRequest("cover", motion));
            Connected.Tick65();   // the dest is laid out: the fly seeds
        }

        public NodeHandle Overlay => Scene.OverlayAt(0);

        /// <summary>One UI publication adopted by the render thread at <paramref name="nowMs"/>.</summary>
        public void Publish(double nowMs)
        {
            Snapshot.Capture(Scene);
            Animation.CaptureCompositorAnimations(Desired, nowMs);
            Renderer.Adopt(Desired, Snapshot, nowMs);
        }

        /// <summary>The rect the render thread draws the overlay at: T(bounds) ∘ about-centre(posed transform).</summary>
        public RectF Posed
        {
            get
            {
                RectF b = Snapshot.Bounds(Overlay);
                var t = Snapshot.Paint(Overlay).LocalTransform;
                float w = b.W * t.M11, h = b.H * t.M22;
                float cx = b.X + b.W * 0.5f + t.Dx, cy = b.Y + b.H * 0.5f + t.Dy;
                return new RectF(cx - w * 0.5f, cy - h * 0.5f, w, h);
            }
        }
    }

    public static TheoryData<string> Motions => new() { "eased", "spring" };

    private static ConnectedMotion MotionOf(string name) => name == "eased"
        ? ConnectedMotion.Eased(EasingSpec.Named(Easing.Linear), 1000f)
        : ConnectedMotion.Springy(SpringParams.FromResponse(0.5f, 1f));

    [Theory]
    [MemberData(nameof(Motions))]
    public void ARetargetedFly_ContinuesFromTheRenderPose(string motion)
    {
        var rig = new Rig(MotionOf(motion));
        rig.Publish(0);
        rig.Renderer.Tick(rig.Snapshot, 100);
        rig.Animation.ApplyCompositorFeedback(rig.Renderer.Feedback);   // the UI's view: the 100 ms pose
        rig.Connected.Settle();
        rig.Renderer.Tick(rig.Snapshot, 200);                           // on screen: the 200 ms pose, not imported yet
        RectF shown = rig.Posed;

        rig.Scene.Bounds(rig.Dest) = new RectF(400f, 100f, 240f, 240f);   // the cover slot settles bigger
        rig.Connected.Tick65();
        rig.Connected.Settle();                                         // retarget: re-base, pixels unchanged
        rig.Publish(200);
        RectF after = rig.Posed;

        Assert.Equal(shown.X, after.X, 0.05f);
        Assert.Equal(shown.Y, after.Y, 0.05f);
        Assert.Equal(shown.W, after.W, 0.05f);
        Assert.Equal(shown.H, after.H, 0.05f);
        rig.Snapshot.ReleaseResources();
    }
}
