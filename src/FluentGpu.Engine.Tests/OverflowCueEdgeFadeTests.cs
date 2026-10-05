using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary><see cref="EdgeFadeSpec.OverflowTail"/>: an edge fade that cues a translated first child (a marquee's moving
/// line) feathers only an edge with content hidden past it, ramped over the runway, resolved by the RECORDER from the
/// content's posed translate (down the first-child chain). On the render thread that is the compositor's pose of the tick, so the fade follows a
/// render-owned scroll on the same frame with nothing on the UI thread.</summary>
public sealed class OverflowCueEdgeFadeTests
{
    private static readonly EdgeFadeSpec Cue = new EdgeFadeSpec(EdgeMask.Horizontal, 24f) { OverflowTail = 200f };

    [Fact]
    public void AtRest_OnlyTheRightEdgeFeathers()
    {
        var fade = Cue.ResolveOverflow(0f);
        Assert.Equal(EdgeMask.Right, fade.Edges);
        Assert.Equal(24f, fade.BandRight);
        Assert.Equal(0f, fade.BandLeft);
        Assert.Equal(0f, fade.OverflowTail);   // a resolved spec is a plain fade
    }

    [Fact]
    public void MidScroll_BothEdgesFeatherAtTheirFullBand()
    {
        var fade = Cue.ResolveOverflow(-100f);
        Assert.Equal(EdgeMask.Horizontal, fade.Edges);
        Assert.Equal(24f, fade.BandLeft);
        Assert.Equal(24f, fade.BandRight);
    }

    [Fact]
    public void AtTheTail_OnlyTheLeftEdgeFeathers()
    {
        var fade = Cue.ResolveOverflow(-200f);
        Assert.Equal(EdgeMask.Left, fade.Edges);
        Assert.Equal(24f, fade.BandLeft);
    }

    [Fact]
    public void TheBandsRampOverTheRunway()
    {
        var leaving = Cue.ResolveOverflow(-6f);       // 6 DIP hidden on the left: a quarter of the 24-DIP runway
        Assert.Equal(6f, leaving.BandLeft, 4);
        Assert.Equal(24f, leaving.BandRight);
        var arriving = Cue.ResolveOverflow(-190f);    // 10 DIP still hidden on the right
        Assert.Equal(10f, arriving.BandRight, 4);
        Assert.Equal(24f, arriving.BandLeft);
    }

    [Fact]
    public void HalfADipHiddenIsNoEdge()
    {
        Assert.Equal(EdgeMask.Right, Cue.ResolveOverflow(-0.4f).Edges);
        Assert.Equal(EdgeMask.Left, Cue.ResolveOverflow(-199.6f).Edges);
    }

    [Fact]
    public void WithoutATail_TheSpecIsUnchanged_AndVerticalEdgesPassThrough()
    {
        var plain = new EdgeFadeSpec(EdgeMask.Horizontal, 24f);
        Assert.Equal(plain, plain.ResolveOverflow(-50f));
        var perimeter = new EdgeFadeSpec(EdgeMask.All, 12f) { OverflowTail = 40f };
        var fade = perimeter.ResolveOverflow(0f);
        Assert.Equal(EdgeMask.Vertical | EdgeMask.Right, fade.Edges);
        Assert.Equal(12f, fade.BandTop);
        Assert.Equal(12f, fade.BandBottom);
    }

    [Fact]
    public void TheRecorderFeathersByTheChildsRenderPosedTranslate_WithoutTheUiScene()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 400, 100);
        scene.Paint(root).VisualKind = VisualKind.Box;
        var host = scene.CreateNode(2);
        scene.AppendChild(root, host);
        scene.Bounds(host) = new(0, 0, 100, 20);
        scene.Paint(host).VisualKind = VisualKind.Box;
        scene.SetEdgeFade(host, Cue);
        var anchor = scene.CreateNode(3);          // a component anchor between the fade and its moving root: transparent
        scene.AppendChild(host, anchor);
        scene.Bounds(anchor) = new(0, 0, 300, 20);
        var line = scene.CreateNode(4);
        scene.AppendChild(anchor, line);
        scene.Bounds(line) = new(0, 0, 300, 20);
        scene.Paint(line).VisualKind = VisualKind.Box;
        scene.Paint(line).Fill = new ColorF(1f, 1f, 1f, 1f);
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = true };
        animation.Keyframes(line, AnimChannel.TranslateX, [new(0f, 0f), new(1f, -200f, Easing.Linear)], 1000f);

        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);

        var atRest = RecordedFade(snapshot);
        Assert.Equal((int)EdgeMask.Right, atRest.FadeEdges);

        renderer.Tick(snapshot, 500);                 // the render thread poses the line at -100
        var mid = RecordedFade(snapshot);
        Assert.Equal((int)EdgeMask.Horizontal, mid.FadeEdges);
        Assert.Equal(24f, mid.FadeBandL, 3);
        Assert.Equal(24f, mid.FadeBandR, 3);

        renderer.Tick(snapshot, 1000);                // ... and at the tail
        var tail = RecordedFade(snapshot);
        Assert.Equal((int)EdgeMask.Left, tail.FadeEdges);
        Assert.Equal(0f, tail.FadeBandR);

        Assert.Equal(0f, scene.Paint(line).LocalTransform.Dx);   // the UI scene never moved: nothing ran there
        snapshot.ReleaseResources();
    }

    private static PushLayerCmd RecordedFade(SceneRecordingSnapshot snapshot)
    {
        var draw = new DrawList();
        snapshot.Recording.Record(snapshot, draw);
        ReadOnlySpan<byte> bytes = draw.Bytes;
        int pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            if (op == DrawOp.PushLayer)
            {
                var layer = MemoryMarshal.Read<PushLayerCmd>(bytes.Slice(pos));
                if (layer.Kind == (int)LayerKind.EdgeFade) return layer;
            }
            pos += PayloadSize(op);
        }
        Assert.Fail("no edge-fade layer was recorded");
        return default;
    }

    private static int PayloadSize(DrawOp op) => op switch
    {
        DrawOp.FillRoundRect => Unsafe.SizeOf<FillRoundRectCmd>(),
        DrawOp.PushClip => Unsafe.SizeOf<ClipCmd>(),
        DrawOp.PopClip => 0,
        DrawOp.PushLayer => Unsafe.SizeOf<PushLayerCmd>(),
        DrawOp.PopLayer => Unsafe.SizeOf<PopLayerCmd>(),
        DrawOp.SetBlend => Unsafe.SizeOf<SetBlendCmd>(),
        _ => throw new InvalidOperationException("unexpected op " + op),
    };
}
