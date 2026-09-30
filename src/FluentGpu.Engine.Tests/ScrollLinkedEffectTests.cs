using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The scroll-linked APIs of the scroll-GPU plan §F as pure arithmetic: hero collapse (presented height + the
/// trailing child shift), the top overscroll stretch and its top-centre pivot against any authored transform origin,
/// the per-node transform fold (a parallax and a stretch on one node both land), the progress map behind
/// <c>UseScrollProgress</c>, and the determinism rule — the UI poser and the render poser pose byte-identical values.</summary>
public sealed class ScrollLinkedEffectTests
{
    static EffectGeometry Node(double y, double h) => new(y, h, 100_000.0, 100_000.0, 800.0, 0f, 0f);

    // ── Collapse ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Collapse_PresentedHeight_RunsFullToMin_MonotoneAndClamped()
    {
        var e = ScrollEffect.Collapse(over: 244.0, minH: 56f);
        var g = Node(0.0, 300.0);
        Assert.Equal(300f, ScrollEffectEval.Evaluate(e, -40.0, g));   // overpan: the full height, never taller
        Assert.Equal(300f, ScrollEffectEval.Evaluate(e, 0.0, g));
        Assert.Equal(178f, ScrollEffectEval.Evaluate(e, 122.0, g));
        Assert.Equal(56f, ScrollEffectEval.Evaluate(e, 244.0, g));
        Assert.Equal(56f, ScrollEffectEval.Evaluate(e, 5000.0, g));
        float prev = float.PositiveInfinity;
        for (double p = -50.0; p <= 400.0; p += 0.37)
        {
            float h = ScrollEffectEval.Evaluate(e, p, g);
            Assert.True(h <= prev, $"presented height rose at p={p}: {prev} -> {h}");
            Assert.InRange(h, 56f, 300f);
            prev = h;
        }
    }

    [Fact]
    public void Collapse_ChildShift_IsPresentedMinusFull()
    {
        var h = ScrollEffect.Collapse(200.0, 50f);
        var shift = ScrollEffect.CollapseChildShift(200.0, 50f);
        var g = Node(10.0, 250.0);
        foreach (double p in new[] { -10.0, 0.0, 33.3, 100.0, 199.9, 200.0, 900.0 })
            Assert.Equal(ScrollEffectEval.Evaluate(h, p, g) - 250f, ScrollEffectEval.Evaluate(shift, p, g), 3);   // one rounding apart
        Assert.Equal(0f, ScrollEffectEval.Evaluate(shift, 0.0, g));
        Assert.Equal(-200f, ScrollEffectEval.Evaluate(shift, 200.0, g));
    }

    // ── Stretch ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stretch_ScalesOnlyWhileOverpannedPastTheStart()
    {
        var e = ScrollEffect.StretchFromTop();
        var g = Node(0.0, 300.0);
        Assert.Equal(1f, ScrollEffectEval.Evaluate(e, 0.0, g));
        Assert.Equal(1f, ScrollEffectEval.Evaluate(e, 250.0, g));
        Assert.Equal(1.2f, ScrollEffectEval.Evaluate(e, -60.0, g), 5);
        Assert.Equal(1.5f, ScrollEffectEval.Evaluate(e, -150.0, g), 5);
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]   // the default centre origin
    [InlineData(0.5f, 0f)]     // the hero photo's authored top-centre origin
    [InlineData(0f, 1f)]       // anything else
    public void Stretch_PivotsAtTopCentre_WhateverTheAuthoredOrigin(float originX, float originY)
    {
        const float w = 400f, h = 300f, pull = 60f;
        float s = ScrollEffectEval.StretchScale(-pull, h);
        var m = EffectTransform.Identity.Add(EffectChannel.ScaleXY, EffectKind.Stretch, s).ToLocal(w, h, w * originX, h * originY);
        // The recorder draws node-local p as T(o)·L·T(−o): p' = L(p − o) + o. The content under the node is translated
        // +pull by the rubber band, so the node's drawn top must land at −pull (viewport top) and its bottom at h.
        Point2 Draw(float x, float y)
        {
            float ox = w * originX, oy = h * originY;
            float lx = x - ox, ly = y - oy;
            return new Point2(lx * m.M11 + ly * m.M21 + m.Dx + ox, lx * m.M12 + ly * m.M22 + m.Dy + oy);
        }
        Assert.Equal(-pull, Draw(w * 0.5f, 0f).Y, 3);        // top edge on the viewport top
        Assert.Equal(h, Draw(w * 0.5f, h).Y, 3);              // bottom edge where the gap ends (h + pull − pull)
        Assert.Equal(w * 0.5f, Draw(w * 0.5f, 0f).X, 3);     // the centre column does not drift sideways
        Assert.Equal(-(s - 1f) * w * 0.5f, Draw(0f, 0f).X, 3);
    }

    [Fact]
    public void TransformFold_ParallaxAndStretch_BothLand()
    {
        // A hero photo carries Stretch + a parallax TransY: the fold adds the translation and keeps the stretch's own
        // pull-cancelling term (the last row no longer overwrites the other).
        const float w = 400f, h = 300f;
        var t = EffectTransform.Identity
            .Add(EffectChannel.ScaleXY, EffectKind.Stretch, 1.2f)
            .Add(EffectChannel.TransY, EffectKind.Map, 7f);
        var m = t.ToLocal(w, h, w * 0.5f, 0f);
        Assert.Equal(1.2f, m.M11, 5);
        Assert.Equal(1.2f, m.M22, 5);
        Assert.Equal(7f - 0.2f * h, m.Dy, 3);
        var plainScale = EffectTransform.Identity.Add(EffectChannel.ScaleXY, EffectKind.Map, 0.9f).Add(EffectChannel.TransX, EffectKind.Map, 3f);
        Assert.Equal(new Affine2D(0.9f, 0f, 0f, 0.9f, 3f, 0f), plainScale.ToLocal(w, h, 200f, 150f));   // scale about the authored origin
        Assert.Equal(Affine2D.Identity, EffectTransform.Identity.ToLocal(w, h, 200f, 150f));
    }

    // ── UseScrollProgress ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Progress_ClampsAndHandlesADegenerateRange()
    {
        Assert.Equal(0f, ScrollObservation.Progress(-5.0, 0.0, 100.0));
        Assert.Equal(0.25f, ScrollObservation.Progress(25.0, 0.0, 100.0));
        Assert.Equal(1f, ScrollObservation.Progress(250.0, 0.0, 100.0));
        Assert.Equal(0.5f, ScrollObservation.Progress(150.0, 200.0, 100.0));   // a reversed range runs backwards
        Assert.Equal(0f, ScrollObservation.Progress(49.0, 50.0, 50.0));
        Assert.Equal(1f, ScrollObservation.Progress(50.0, 50.0, 50.0));
    }

    // ── both posers agree ────────────────────────────────────────────────────────────────────────────────────

    sealed class CaptureSink : IScrollPoseSink
    {
        public readonly List<(int Node, EffectChannel Channel, float Value)> Effects = new();
        public readonly List<(int Node, EffectTransform T)> Transforms = new();
        public void PoseViewport(int vpNode, double shown) { }
        public void PoseContent(int node, bool horizontal, float trans, bool changed) { }
        public void PoseEffect(int node, EffectChannel channel, float value, bool changed) => Effects.Add((node, channel, value));
        public void PoseTransform(int node, in EffectTransform transform, bool changed) => Transforms.Add((node, transform));
        public void Clear() { Effects.Clear(); Transforms.Clear(); }
    }

    [Fact]
    public void UiAndRenderPosers_PoseCollapseAndStretch_ByteIdentically()
    {
        var vp = new ScrollViewportId(3, 1);
        var slots = new PlanSlots();
        var seg = new MotionSeg(SegKind.Cubic, 0.0, 1.0, -80.0, 600.0);
        slots.Allocate(vp, new ScrollPlan(seg, default, default, default, 1, vp.Node, 0, 1, 0.0, 1e9, 0.0, 0.0, 0.0,
            OverpanPolicy.RubberBand, MotionKind.Wheel, default));
        var geometry = Node(0.0, 320.0);
        var rows = new[]
        {
            new ScrollEffectRow(40, ScrollEffect.Sticky(0f), geometry),
            new ScrollEffectRow(40, ScrollEffect.Collapse(264.0, 56f), geometry),
            new ScrollEffectRow(40, ScrollEffect.CollapseChildShift(264.0, 56f), geometry),
            new ScrollEffectRow(41, ScrollEffect.StretchFromTop(), geometry),
            new ScrollEffectRow(41, ScrollEffect.Parallax(0.0, 320.0, 0f, 160f), geometry),
        };
        var cov = new ScrollCoverageTable();
        cov.AddRow(new ScrollCoverageRow(vp.Node, vp.Gen, 100, 0.0, 0.0, 5000.0, 800.0, 5000.0, false, 0, 0, 0.0), rows);
        var ui = new ScrollPoser();
        var render = new ScrollPoser();
        ui.Adopt(cov);
        render.Adopt(cov);
        var a = new CaptureSink();
        var b = new CaptureSink();
        for (double t = 0.0; t <= 1.0; t += 0.0171)
        {
            a.Clear(); b.Clear();
            ui.Tick(slots, t, 1.25f, a);
            render.Tick(slots, t, 1.25f, b);
            Assert.Equal(a.Effects, b.Effects);
            Assert.Equal(a.Transforms, b.Transforms);
            // One PoseTransform per node: the sticky row on 40, the stretch+parallax pair on 41.
            Assert.Equal(2, a.Transforms.Count);
            Assert.Equal(2, a.Effects.Count);   // PresentedH + ChildShiftY on 40
        }
    }
}
