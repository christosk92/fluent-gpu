using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Lottie;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <see cref="LottieCompiler"/> unit gates — hand-built <see cref="LottieDocument"/> fixtures (bypassing the parser,
/// same posture as the VerticalSlice <c>gate.lottie.synthetic.*</c> gates) verifying the layer-tree time mapping,
/// the animated-bezier switch-group sample count, per-axis easing, the stroke-width proxy, <c>SampleAt</c>, and
/// sibling paint order (docs/plans/wavee lottie-heroes-implementation.md §6).
/// </summary>
public sealed class LottieCompilerTests
{
    private static LottieTransform Xform() => new();

    private static LottieNode Find(LottiePlan plan, string name) => plan.Nodes.First(n => n.Name == name);
    private static LottieTrack Track(LottieNode node, AnimChannel ch) => node.Tracks.First(t => t.Channel == ch);

    [Fact]
    public void TimeBase_SumsStartTimeAcrossPrecompAncestors()
    {
        var opacity = new AnimScalar
        {
            Keys =
            [
                new ScalarKey { T = 0f, Value = 100f },
                new ScalarKey { T = 50f, Value = 0f },
            ],
        };
        var leaf = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "L", Type = LottieLayerType.Shape,   // Null opacity is inert in Lottie (LottieCompiler forces it to 1) — use Shape to exercise the Opacity track
            InPoint = 0f, OutPoint = 200f, StartTime = 5f, Stretch = 1f,
            Transform = new LottieTransform { Opacity = opacity },
        };
        var precomp = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "P1", Type = LottieLayerType.Precomp,
            InPoint = 0f, OutPoint = 200f, StartTime = 10f, Stretch = 1f,
            RefId = "child", Width = 100f, Height = 100f, Transform = Xform(),
        };
        var doc = new LottieDocument
        {
            FrameRate = 60f, InPoint = 0f, OutPoint = 200f, Width = 100f, Height = 100f,
            Layers = [precomp],
            Precomps = new Dictionary<string, LottieLayer[]>(StringComparer.Ordinal) { ["child"] = [leaf] },
        };

        LottiePlan plan = LottieCompiler.Compile(doc);
        LottieTrack o = Track(Find(plan, "L"), AnimChannel.Opacity);

        // globalFrame = t*sr + st(leaf=5) + ancestorStSum(P1.st=10); U = globalFrame / 200.
        Assert.Equal((0f + 5f + 10f) / 200f, o.Keys[0].Offset, 4);
        Assert.Equal((50f + 5f + 10f) / 200f, o.Keys[1].Offset, 4);
    }

    [Fact]
    public void AnimatedBezier_TwoKeysAt333ms_ProducesSevenSamples()
    {
        LottiePlan plan = CompileSwitchDoc(t1: 0f, t2: 20f, frameRate: 60f);   // 20 frames @ 60fps = 333.33ms
        int fills = plan.Nodes.Count(node => node.Kind == LottieNodeKind.FillPath);
        Assert.Equal(7, fills);
    }

    [Fact]
    public void AnimatedBezier_ThreeKeys_CapsAtTwelveSamples()
    {
        // Two segments, each wanting 6 sub-samples (833ms spans @ 60fps) -> 1 + 6 + 6 = 13, capped to 12.
        var bez0 = SimpleTri(0f);
        var bez1 = SimpleTri(1f);
        var bez2 = SimpleTri(2f);
        var anim = new AnimPath
        {
            Keys =
            [
                new PathKey { T = 0f, Shape = bez0 },
                new PathKey { T = 50f, Shape = bez1 },
                new PathKey { T = 100f, Shape = bez2 },
            ],
        };
        LottiePlan plan = CompileSwitchDocFromAnim(anim, frameRate: 60f, docEnd: 200f);
        int fills = plan.Nodes.Count(n => n.Kind == LottieNodeKind.FillPath);
        Assert.Equal(12, fills);
    }

    [Fact]
    public void Scale_PerAxisEasingDiffersBetweenXAndY()
    {
        // Ease lives on the DEPARTING key (key0 -> key1's segment): per-axis easing that differs between X and Y.
        var scale = new AnimVec2
        {
            Keys =
            [
                new Vec2Key
                {
                    T = 0f, Value = new Point2(100f, 100f),
                    EaseX = new LottieEase(0.42f, 0f, 1f, 1f, false),     // CSS ease-in — slow start
                    EaseY = new LottieEase(0f, 0f, 0.58f, 1f, false),     // CSS ease-out — fast start
                },
                new Vec2Key { T = 10f, Value = new Point2(200f, 50f) },
            ],
        };
        var layer = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "S", Type = LottieLayerType.Null,
            InPoint = 0f, OutPoint = 10f, StartTime = 0f, Stretch = 1f,
            Transform = new LottieTransform { Scale = scale },
        };
        var doc = new LottieDocument { FrameRate = 60f, InPoint = 0f, OutPoint = 10f, Width = 10f, Height = 10f, Layers = [layer] };
        LottiePlan plan = LottieCompiler.Compile(doc);
        LottieNode node = Find(plan, "S");
        Keyframe kx = Track(node, AnimChannel.ScaleX).Keys[1];
        Keyframe ky = Track(node, AnimChannel.ScaleY).Keys[1];
        float atHalfX = Easings.Ease(kx.Easing, 0.5f);
        float atHalfY = Easings.Ease(ky.Easing, 0.5f);
        Assert.NotEqual(atHalfX, atHalfY);
    }

    [Fact]
    public void StrokeWidth_Animated_BecomesAnOpacityProxy()
    {
        var width = new AnimScalar { Keys = [new ScalarKey { T = 0f, Value = 0f }, new ScalarKey { T = 10f, Value = 2.5f }] };
        var pathShape = new LottieShape
        {
            Type = LottieShapeType.Path,
            Path = new AnimPath { Static = new LottieBezier { V = [new Point2(0, 0), new Point2(5, 0)], I = [default, default], O = [default, default], Closed = false } },
        };
        var strokeShape = new LottieShape { Type = LottieShapeType.Stroke, Color = new ColorF(1f, 1f, 1f, 1f), Width = width };
        var layer = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "Shape", Type = LottieLayerType.Shape,
            InPoint = 0f, OutPoint = 10f, StartTime = 0f, Stretch = 1f,
            Transform = Xform(), Shapes = [pathShape, strokeShape],
        };
        var doc = new LottieDocument { FrameRate = 60f, InPoint = 0f, OutPoint = 10f, Width = 10f, Height = 10f, Layers = [layer] };
        LottiePlan plan = LottieCompiler.Compile(doc);

        LottieNode stroke = plan.Nodes.First(n => n.Kind == LottieNodeKind.StrokePath);
        LottieTrack proxy = Track(stroke, AnimChannel.Opacity);
        Assert.Equal(0f, proxy.Keys[0].Value, 3);
        Assert.Equal(1f, proxy.Keys[1].Value, 3);
        Assert.True(plan.Approximations > 0);
    }

    [Fact]
    public void SampleAt_Half_InterpolatesTheOpacityTrack()
    {
        var opacity = new AnimScalar { Keys = [new ScalarKey { T = 0f, Value = 0f }, new ScalarKey { T = 10f, Value = 100f }] };
        var layer = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "N", Type = LottieLayerType.Shape,   // Null opacity is inert (see TimeBase test) — Shape exercises the real Opacity track
            InPoint = 0f, OutPoint = 10f, StartTime = 0f, Stretch = 1f,
            Transform = new LottieTransform { Opacity = opacity },
        };
        var doc = new LottieDocument { FrameRate = 60f, InPoint = 0f, OutPoint = 10f, Width = 10f, Height = 10f, Layers = [layer] };
        LottiePlan plan = LottieCompiler.Compile(doc);
        int idx = Array.FindIndex(plan.Nodes, n => n.Name == "N");

        LottiePose pose = plan.SampleAt(0.5f);
        Assert.Equal(0.5f, pose.Opacity[idx], 3);
    }

    [Fact]
    public void SiblingOrder_DescendingIndPaintsLowIndOnTop()
    {
        var top = new LottieLayer { Index = 1, Parent = -1, Name = "Top", Type = LottieLayerType.Null, InPoint = 0f, OutPoint = 10f, Transform = Xform() };
        var bottom = new LottieLayer { Index = 2, Parent = -1, Name = "Bottom", Type = LottieLayerType.Null, InPoint = 0f, OutPoint = 10f, Transform = Xform() };
        var doc = new LottieDocument { FrameRate = 60f, InPoint = 0f, OutPoint = 10f, Width = 10f, Height = 10f, Layers = [top, bottom] };
        LottiePlan plan = LottieCompiler.Compile(doc);

        int topIdx = Array.FindIndex(plan.Nodes, n => n.Name == "Top");
        int bottomIdx = Array.FindIndex(plan.Nodes, n => n.Name == "Bottom");
        // Higher `ind` (Bottom=2) compiles FIRST (§4 "descending ind") so it lands EARLIER in the flat node list —
        // Top (ind=1, AE's topmost) compiles later, i.e. is the later/"on top" sibling.
        Assert.True(bottomIdx < topIdx, $"bottomIdx={bottomIdx} topIdx={topIdx}");
    }

    [Fact]
    public void NullLayer_OpacityIsInert_DoesNotSuppressDescendants()
    {
        // Regression fixture for the "hero rail renders nothing" bug: a Lottie NULL layer is never itself
        // rendered -- After Effects never composites a Null's own "o" into anything -- but the shipped Eula asset's
        // root Null wrapper ("Null 76") carries a static o:0. LottieCompiler must NOT bake that into the compiled
        // Group's Opacity (the engine composes nested BoxEl opacity multiplicatively, so a 0 there would blank the
        // whole subtree). A real content layer's own opacity is unaffected.
        var nullLayer = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "Wrapper", Type = LottieLayerType.Null,
            InPoint = 0f, OutPoint = 10f, StartTime = 0f, Stretch = 1f,
            Transform = new LottieTransform { Opacity = AnimScalar.Of(0f) },   // the poisoned static value seen in the wild
        };
        var fillShape = new LottieShape { Type = LottieShapeType.Fill, Color = new ColorF(1f, 0f, 0f, 1f), Opacity = AnimScalar.Of(80f) };
        var pathShape = new LottieShape
        {
            Type = LottieShapeType.Path,
            Path = new AnimPath { Static = new LottieBezier { V = [new Point2(0, 0), new Point2(1, 0), new Point2(1, 1)], I = [default, default, default], O = [default, default, default], Closed = true } },
        };
        var child = new LottieLayer
        {
            Index = 2, Parent = 1, Name = "Child", Type = LottieLayerType.Shape,
            InPoint = 0f, OutPoint = 10f, StartTime = 0f, Stretch = 1f,
            Transform = Xform(), Shapes = [pathShape, fillShape],
        };
        var doc = new LottieDocument { FrameRate = 60f, InPoint = 0f, OutPoint = 10f, Width = 10f, Height = 10f, Layers = [nullLayer, child] };
        LottiePlan plan = LottieCompiler.Compile(doc);

        LottieNode wrapper = Find(plan, "Wrapper");
        Assert.Equal(1f, wrapper.Opacity);            // forced inert, NOT the authored 0
        Assert.DoesNotContain(wrapper.Tracks, t => t.Channel == AnimChannel.Opacity);

        LottieNode fill = plan.Nodes.First(n => n.Kind == LottieNodeKind.FillPath);
        Assert.Equal(0.8f, fill.Opacity, 3);           // the real content layer's own opacity is untouched
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static LottieBezier SimpleTri(float seed) => new()
    {
        V = [new Point2(seed, 0), new Point2(seed + 5, 0), new Point2(seed + 5, 5)],
        I = [default, default, default],
        O = [default, default, default],
        Closed = true,
    };

    private static LottiePlan CompileSwitchDoc(float t1, float t2, float frameRate)
    {
        var anim = new AnimPath { Keys = [new PathKey { T = t1, Shape = SimpleTri(0f) }, new PathKey { T = t2, Shape = SimpleTri(1f) }] };
        return CompileSwitchDocFromAnim(anim, frameRate, docEnd: 200f);
    }

    private static LottiePlan CompileSwitchDocFromAnim(AnimPath anim, float frameRate, float docEnd)
    {
        var pathShape = new LottieShape { Type = LottieShapeType.Path, Path = anim };
        var fillShape = new LottieShape { Type = LottieShapeType.Fill, Color = new ColorF(1f, 0f, 0f, 1f) };
        var layer = new LottieLayer
        {
            Index = 1, Parent = -1, Name = "Shape", Type = LottieLayerType.Shape,
            InPoint = 0f, OutPoint = docEnd, StartTime = 0f, Stretch = 1f,
            Transform = Xform(), Shapes = [pathShape, fillShape],
        };
        var doc = new LottieDocument { FrameRate = frameRate, InPoint = 0f, OutPoint = docEnd, Width = 10f, Height = 10f, Layers = [layer] };
        return LottieCompiler.Compile(doc);
    }
}
