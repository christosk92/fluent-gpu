using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Two review fixes to the render-thread caption / marquee work.
/// <list type="bullet">
/// <item><see cref="GlyphWipe.Run"/>: a wipe split the render thread poses steps in whole DIPs of the run the author steps
/// along (a wrapped caption's measured reading-order run), not of the node's width, so it moves exactly as the UI wrote it.</item>
/// <item><c>SliceRecorder.ExtentWithinMarkerClip</c>: an effect slice's extent is cut to its marker clip only when it
/// composites under that clip unchanged; every other kind keeps its painted bounds.</item>
/// </list></summary>
public sealed class WipeRunAndExtentTests
{
    [Theory]
    [InlineData(300f, 0f)]       // a single line: the run IS the node's width
    [InlineData(300f, 527.3f)]   // a wrapped run: two lines end to end, longer than the node
    public void TheRenderThreadStepsTheSplitAlongTheAuthoredRun(float nodeWidth, float run)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 1000, 200);
        var text = scene.CreateNode(2);
        scene.AppendChild(root, text);
        scene.Bounds(text) = new(0, 0, nodeWidth, 80);
        scene.Paint(text).VisualKind = VisualKind.Text;
        scene.SetGlyphWipe(text, new GlyphWipe(new ColorF(1, 1, 1, 1), new ColorF(1, 1, 1, 0.45f), 0f) { Run = run });
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = true };
        animation.Keyframes(text, AnimChannel.GlyphWipeSplit, [new(0f, 0f), new(1f, 1f, Easing.Linear)], 1000f);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);

        float steps = run > 0f ? run : nodeWidth;
        foreach (double at in new[] { 123.4, 377.7, 501.9, 888.8 })
        {
            renderer.Tick(snapshot, at);
            Assert.True(snapshot.TryGetGlyphWipe(text, out var posed));
            float raw = (float)(at / 1000.0);
            float uiStep = MathF.Round(raw * steps) / steps;   // the caption's own UI write (DriveWipe)
            Assert.Equal(uiStep, posed.Split, 5);
            Assert.Equal(run, posed.Run);
        }
        snapshot.ReleaseResources();
    }

    [Fact]
    public void Quantize_KeepsTheSettledEndsExact_AndATinyRunUnstepped()
    {
        Assert.Equal(0f, GlyphWipe.Quantize(0f, 300f));
        Assert.Equal(1f, GlyphWipe.Quantize(1f, 300f));
        Assert.Equal(0.3333f, GlyphWipe.Quantize(0.3333f, 0.5f));
        var wipe = new GlyphWipe(default, default, 0f) { Run = 200f };
        Assert.Equal(MathF.Round(0.4567f * 200f) / 200f, wipe.QuantizeSplit(0.4567f, 120f));
        Assert.Equal(MathF.Round(0.4567f * 120f) / 120f, (wipe with { Run = 0f }).QuantizeSplit(0.4567f, 120f));
    }

    private static readonly RectF Bounds = new(10, 20, 600, 40);
    private static readonly RectF Clip = new(40.5f, 22f, 151f, 19f);

    private static SliceRecorder.ExtentFacts Plain() => new(Clip, SliceRecorder.PoseKind.None, Sticky: false,
        Role: (int)SliceRole.Main, LowRes: 0, Feedback: false, Acrylic: false, MarkerFlags: 0, Layer: default);

    [Fact]
    public void APlainClippedEffectSlice_IsCutToItsMarkerClipPlusOneDevicePixel()
    {
        RectF cut = SliceRecorder.ExtentWithinMarkerClip(in Bounds, 1.5f, Plain());
        float slack = 1f / 1.5f;
        Assert.Equal(Clip.X - slack, cut.X, 4);
        Assert.Equal(Clip.Right + slack, cut.Right, 4);
        Assert.Equal(Clip.Y - slack, cut.Y, 4);
        Assert.Equal(Clip.Bottom + slack, cut.Bottom, 4);
        // an edge fade (fade mode) and an opacity group composite their content unchanged: cut too
        var fade = Plain() with { MarkerFlags = (int)CompositeSliceFlags.Layer, Layer = DrawList.EdgeFadeLayerCmd(Clip, Clip, default, 1f, 5, 24, 0, 24, 0, 1, 1f, 0f) };
        Assert.Equal(cut, SliceRecorder.ExtentWithinMarkerClip(in Bounds, 1.5f, fade));
    }

    [Fact]
    public void EverySliceThatDoesNotCompositeUnderItsClipUnchanged_KeepsItsBounds()
    {
        var plain = Plain();
        SliceRecorder.ExtentFacts[] keep =
        [
            plain with { MarkerClip = RectF.Infinite },                                   // no clip at all
            plain with { Pose = SliceRecorder.PoseKind.Effect },                          // a posed (translated) slice
            plain with { Pose = SliceRecorder.PoseKind.Content },                         // scroll content
            plain with { Pose = SliceRecorder.PoseKind.Thumb },
            plain with { Sticky = true },                                                 // a composite-time sticky clip
            plain with { Role = (int)SliceRole.Chrome },
            plain with { Role = (int)SliceRole.Band },
            plain with { LowRes = 2 },                                                    // no tiles
            plain with { Feedback = true },
            plain with { Acrylic = true },
            plain with { MarkerFlags = (int)CompositeSliceFlags.InnerClip },              // a self-blur's source clip
            plain with { MarkerFlags = (int)CompositeSliceFlags.ParamsUp },               // params in another slice's space
            plain with { MarkerFlags = (int)CompositeSliceFlags.Layer, Layer = DrawList.BlurLayerCmd(Clip, default, 6f, 1f, Clip) },
            plain with { MarkerFlags = (int)CompositeSliceFlags.Layer,
                         Layer = DrawList.EdgeFadeLayerCmd(Clip, Clip, default, 1f, 5, 24, 0, 24, 0, 1, 1f, 4f) },   // a blurred fade
        ];
        foreach (var facts in keep)
            Assert.Equal(Bounds, SliceRecorder.ExtentWithinMarkerClip(in Bounds, 1.5f, in facts));
    }
}
