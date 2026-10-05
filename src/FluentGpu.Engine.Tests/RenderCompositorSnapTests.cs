using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>F239 on the render-thread compositor: a <see cref="AnimFlags.PixelSnap"/> row (the player-bar marquee) poses whole
/// device pixels, and two such rows with the same explicit cadence re-sample on a shared per-period clock, so they step on the
/// SAME ticks whatever instant each was seeded at. The snap quantises the VALUE only: a display-cadence row (period 0) samples on
/// every tick and the snap decides which ticks change a pixel (the host elides the others as byte-identical frames). There is
/// no background or tier floor on a loop's period: a visible loop runs at the rate its cadence names.</summary>
public sealed class RenderCompositorSnapTests
{
    // 100 DIP/s: 1 px per 10 ms tick at scale 1, so every lattice sample moves and the changes are countable.
    private static readonly Keyframe[] Ramp = [new(0f, 0f, Easing.Linear), new(1f, -1000f, Easing.Linear)];

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(float scale, bool renderOwns = true)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        scene.DeviceScale = scale;
        return (scene, node, new AnimEngine(scene) { RenderOwnsCompositor = renderOwns });
    }

    [Fact]
    public void PixelSnapRows_PoseWholeDevicePixels_AndStepOnTheSameTicks_WhateverInstantEachWasSeeded()
    {
        var (scene, node, anim) = Engine(2f);
        anim.Keyframes(node, AnimChannel.TranslateX, Ramp, 10_000f, loop: true, cadence: Cadence.At(30f), pixelSnap: true);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);

        // A second marquee row (the other line of the bar) seeded 7 ms later: with a per-row "a period after my own last
        // sample" hold it would step on different ticks from the first; on the shared clock it cannot.
        anim.Keyframes(node, AnimChannel.TranslateY, Ramp, 10_000f, loop: true, cadence: Cadence.At(30f), pixelSnap: true);
        anim.CaptureCompositorAnimations(desired, 7);
        renderer.Adopt(desired, snapshot, 7);

        var changedX = new List<double>();
        var changedY = new List<double>();
        var m0 = snapshot.Paint(node).LocalTransform;
        float px = m0.Dx, py = m0.Dy;
        bool onGrid = true;
        for (double t = 10; t <= 400; t += 10)
        {
            renderer.Tick(snapshot, t);
            var m = snapshot.Paint(node).LocalTransform;
            if (m.Dx != px) { changedX.Add(t); px = m.Dx; }
            if (m.Dy != py) { changedY.Add(t); py = m.Dy; }
            onGrid &= MathF.Abs(m.Dx * 2f - MathF.Round(m.Dx * 2f)) < 1e-3f && MathF.Abs(m.Dy * 2f - MathF.Round(m.Dy * 2f)) < 1e-3f;
        }
        Assert.True(onGrid, "every posed translate sits on the 0.5 DIP (one device pixel at 2x) grid");
        Assert.True(changedX.Count >= 8, $"the rows travel (X changed on {changedX.Count} ticks)");
        Assert.Equal(changedX, changedY);   // the two rows damage on exactly the same render ticks
    }

    // 10000 DIP/s: every sample moves tens of pixels, so "did the row re-sample on this tick" is simply "did Dx change".
    private static readonly Keyframe[] FastRamp = [new(0f, 0f, Easing.Linear), new(1f, -100_000f, Easing.Linear)];

    /// <summary>The 1-based ticks (spaced <paramref name="stepMs"/> apart, each nudged by <paramref name="jitterMs"/>) on which a
    /// pixel-snapped row of <paramref name="hz"/> re-sampled.</summary>
    private static List<int> SampledTicks(float hz, double stepMs, int ticks, Func<int, double> jitterMs)
    {
        const double start = 1000;
        var (scene, node, anim) = Engine(1f);
        anim.Keyframes(node, AnimChannel.TranslateX, FastRamp, 10_000f, loop: true, cadence: Cadence.At(hz), pixelSnap: true);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, start);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, start);   // a never-sampled row poses at once: the baseline
        var sampled = new List<int>();
        float prev = snapshot.Paint(node).LocalTransform.Dx;
        for (int i = 1; i <= ticks; i++)
        {
            renderer.Tick(snapshot, start + i * stepMs + jitterMs(i));
            float dx = snapshot.Paint(node).LocalTransform.Dx;
            if (dx != prev) { sampled.Add(i); prev = dx; }
        }
        return sampled;
    }

    [Fact]
    public void PixelSnapRow_AtItsOwnRefreshRate_NeverDropsOrDoublesASample()
    {
        // Cadence.At(60) rounds to a 17 ms period; a 60 Hz panel ticks every 16.667 ms. An absolute floor(now / 17) lattice would drop
        // a step about every 51 ticks (two ticks in one cell); the shared clock's relative rule samples every tick.
        var every = SampledTicks(60f, 1000.0 / 60.0, 600, _ => 0);
        Assert.Equal(600, every.Count);
        // 120 Hz panel, the same 17 ms row: exactly every 2nd tick, however long it runs.
        var second = SampledTicks(60f, 1000.0 / 120.0, 1200, _ => 0);
        Assert.Equal(600, second.Count);
        Assert.All(second, tick => Assert.Equal(0, tick % 2));
    }

    [Fact]
    public void PixelSnapRow_At30Hz_StepsEverySecondTick_DespiteRenderTimeJitter()
    {
        // A 33 ms row on 16.667 ms ticks that each land up to 0.4 ms early or late: the old relative rule's slack absorbs it.
        var steps = SampledTicks(30f, 1000.0 / 60.0, 600, i => ((i * 7) % 5 - 2) * 0.2);
        Assert.Equal(300, steps.Count);
        Assert.All(steps, tick => Assert.Equal(0, tick % 2));
    }

    [Fact]
    public void ARowWithoutPixelSnap_KeepsItsFractionalSamples()
    {
        var (scene, node, anim) = Engine(2f);
        anim.Keyframes(node, AnimChannel.TranslateX, Ramp, 10_000f, loop: true, cadence: Cadence.At(60f), pixelSnap: false);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        bool offGrid = false;
        for (double t = 7; t <= 203; t += 7)   // 7 ms apart: a 10 ms step lands every sample on a whole pixel and proves nothing
        {
            renderer.Tick(snapshot, t);
            float dx = snapshot.Paint(node).LocalTransform.Dx;
            if (MathF.Abs(dx * 2f - MathF.Round(dx * 2f)) > 1e-3f) offGrid = true;
        }
        Assert.True(offGrid, "an un-snapped row is sampled analytically, not rounded");
    }

    /// <summary>A display-cadence snapped row (the marquee's default): sampled on EVERY tick, so no tick that can move a pixel is
    /// missed, and every pose on the device-pixel grid — the quantisation is of the value, never of the rate.</summary>
    [Fact]
    public void PixelSnapRow_AtDisplayCadence_SamplesEveryTick_AndPosesOnlyWholePixels()
    {
        // 100 DIP/s at scale 2: 200 px/s, so a 10 ms tick moves exactly 2 px and every tick must change the pose.
        var (scene, node, anim) = Engine(2f);
        anim.Keyframes(node, AnimChannel.TranslateX, Ramp, 10_000f, loop: true, pixelSnap: true);   // cadence null = display rate
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        int steps = 0;
        bool onGrid = true;
        float prev = snapshot.Paint(node).LocalTransform.Dx;
        for (double t = 10; t <= 1000; t += 10)
        {
            renderer.Tick(snapshot, t);
            float dx = snapshot.Paint(node).LocalTransform.Dx;
            if (dx != prev) { steps++; prev = dx; }
            onGrid &= MathF.Abs(dx * 2f - MathF.Round(dx * 2f)) < 1e-3f;
        }
        Assert.Equal(100, steps);   // every tick moved the text: nothing held it back
        Assert.True(onGrid, "every pose sits on the device-pixel grid");
        Assert.True(renderer.HasActive);

        // A slower row (12 DIP/s at scale 1: 0.12 px per 10 ms tick) changes a pixel about every 8th tick and poses the IDENTICAL
        // value in between — the held frames the host elides. Still sampled every tick: the first tick past a pixel edge moves.
        var (scene2, node2, anim2) = Engine(1f);
        anim2.Keyframes(node2, AnimChannel.TranslateX, [new(0f, 0f, Easing.Linear), new(1f, -120f, Easing.Linear)], 10_000f, loop: true, pixelSnap: true);
        anim2.CaptureCompositorAnimations(desired, 0);
        var snapshot2 = new SceneRecordingSnapshot();
        snapshot2.Capture(scene2);
        var slow = new RenderCompositorAnimations();
        slow.Adopt(desired, snapshot2, 0);
        int slowSteps = 0, held = 0;
        prev = snapshot2.Paint(node2).LocalTransform.Dx;
        for (double t = 10; t <= 1000; t += 10)
        {
            slow.Tick(snapshot2, t);
            float dx = snapshot2.Paint(node2).LocalTransform.Dx;
            if (dx != prev) { slowSteps++; prev = dx; } else held++;
            Assert.Equal(MathF.Round(dx), dx);
        }
        Assert.InRange(slowSteps, 11, 13);   // 12 px of travel in the second: one pixel per step, never a half
        Assert.True(held > 80, $"held={held}: the ticks between pixel edges re-pose the identical value");
    }

    [Fact]
    public void UiScheduler_PixelSnapRow_PosesWholeDevicePixels_AndAReseedWithoutItClearsTheFlag()
    {
        var (scene, node, anim) = Engine(2f, renderOwns: false);
        anim.Keyframes(node, AnimChannel.TranslateX, Ramp, 10_000f, loop: true, cadence: Cadence.Display, pixelSnap: true);
        bool onGrid = true;
        for (int i = 0; i < 30; i++)
        {
            anim.Tick(16.67f);
            float dx = scene.Paint(node).LocalTransform.Dx;
            onGrid &= MathF.Abs(dx * 2f - MathF.Round(dx * 2f)) < 1e-3f;
        }
        Assert.True(onGrid, "the UI-side sample rounds to the device-pixel grid");
        anim.Keyframes(node, AnimChannel.TranslateX, Ramp, 10_000f, loop: true, cadence: Cadence.Display, pixelSnap: false);
        bool offGrid = false;
        for (int i = 0; i < 30; i++)
        {
            anim.Tick(16.67f);
            float dx = scene.Paint(node).LocalTransform.Dx;
            if (MathF.Abs(dx * 2f - MathF.Round(dx * 2f)) > 1e-3f) offGrid = true;
        }
        Assert.True(offGrid, "re-seeding without pixelSnap must drop the snap");
    }
}
