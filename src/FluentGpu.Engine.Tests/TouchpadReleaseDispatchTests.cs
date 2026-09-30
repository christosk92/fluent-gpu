using System;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The touchpad lift end to end through <see cref="InputDispatcher.DispatchScroll"/>: Wavee's HID contact 66 (the owner's
/// 2026-09-25 capture) replayed as the composition-timed DirectManipulation stream it was — Begin, three samples, then
/// the End at the RUNNING→READY/INERTIA status edge 33 ms after the last movement. The stream the producer emitted then
/// (its whole-pixel snap as a Sample at the End stamp, no verdict) held the flick dead; the stream it emits now (no snap,
/// the End carrying DM's verdict) flings from the newest real sample.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class TouchpadReleaseDispatchTests
{
    private sealed class Root : Component
    {
        public override Element Render()
            => Ui.ScrollView(new BoxEl { Height = 40_000f, Direction = 1 });
    }

    private static readonly Point2 At = new(100, 100);

    // Contact 66 (plan-clock seconds; DIP deltas as the producer emitted them).
    private const double TBegin = 270962.425174, TLastReal = 270962.466812, TEnd = 270962.500163;
    private static readonly (double T, float Dy)[] Samples =
    {
        (270962.450159, 253.4397f), (270962.458502, 170.9045f), (TLastReal, 94.0318f),
    };
    private const double Travel = 253.4397 + 170.9045 + 94.0318;

    private static ScrollInputEvent Ev(ScrollGesture phase, double sec, float dy, ContactRelease release = ContactRelease.Unknown)
        => new(ScrollSource.Touchpad, phase, (long)Math.Round(sec * Stopwatch.Frequency), At, 0f, dy, 66, KeyModifiers.None)
            { PresentTimed = true, Release = release };

    private static void WithList(Action<AppHost, ScrollHandle> body)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("touchpad-release", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root());
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            NodeHandle vp = default;
            var scene = host.Scene;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);
            var handle = host.TryGetScrollHandle(vp);
            Assert.NotNull(handle);
            body(host, handle!);
        }
        finally { host.Dispose(); }
    }

    private static void DispatchContact(AppHost host)
    {
        Assert.True(host.Input.DispatchScroll(Ev(ScrollGesture.Begin, TBegin, 0f)));
        foreach (var (t, dy) in Samples) Assert.True(host.Input.DispatchScroll(Ev(ScrollGesture.Sample, t, dy)));
    }

    /// <summary>The stream as the producer emitted it before the fix: the READY snap (+0.2111 DIP) as a Sample at the End
    /// stamp, then a verdict-less End — the release reads ~6 DIP/s and the flick is held.</summary>
    [Fact]
    public void ReadySnapSampleThenVerdictlessEnd_HoldsTheFlick_TheDefectShape()
        => WithList((host, handle) =>
        {
            DispatchContact(host);
            host.Input.DispatchScroll(Ev(ScrollGesture.Sample, TEnd, 0.2111f));
            host.Input.DispatchScroll(Ev(ScrollGesture.End, TEnd, 0f));
            Assert.Equal(MotionKind.Idle, handle.Plan.Kind);
        });

    /// <summary>The stream the producer emits now for the same flick: no snap, and an End carrying DM's
    /// RUNNING→INERTIA verdict. The plan is a Fling authored from the newest real sample, starting where the contact
    /// shows at the End stamp (no jump).</summary>
    [Fact]
    public void MovingReleaseAtTheStatusEdge_FlingsFromTheNewestRealSample()
        => WithList((host, handle) =>
        {
            DispatchContact(host);
            double shownAtEnd = handle.EvalAt(TEnd, out _, out _);
            Assert.Equal(Travel, shownAtEnd, 3);
            host.Input.DispatchScroll(Ev(ScrollGesture.End, TEnd, 0f, ContactRelease.Moving));
            var plan = handle.Plan;
            Assert.Equal(MotionKind.Fling, plan.Kind);
            Assert.True(plan.S0.V0 > 10_000.0, $"V0={plan.S0.V0}");
            Assert.Equal(shownAtEnd, handle.EvalAt(TEnd, out _, out _), 6);
            Assert.True(handle.EvalAt(TEnd + 0.1, out _, out _) > shownAtEnd + 500.0);
        });

    /// <summary>The same contact released AT REST (DM went RUNNING→READY) holds where it shows.</summary>
    [Fact]
    public void StoppedReleaseAtTheStatusEdge_Holds()
        => WithList((host, handle) =>
        {
            DispatchContact(host);
            host.Input.DispatchScroll(Ev(ScrollGesture.End, TEnd, 0f, ContactRelease.Stopped));
            var plan = handle.Plan;
            Assert.Equal(MotionKind.Idle, plan.Kind);
            Assert.Equal(Travel, plan.Dest, 3);
        });
}
