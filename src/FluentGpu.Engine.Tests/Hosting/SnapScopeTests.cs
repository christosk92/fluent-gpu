using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;
// `FrameClock` is ambiguous between FluentGpu.Hooks (the app-facing static) and FluentGpu.Pal (the host's per-frame record).
using AppFrameClock = FluentGpu.Hooks.FrameClock;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// EP17 / E17a-b: <see cref="SnapScope"/>. While a scope is open on a host, brush transitions, Enter / Exit / Layout (FLIP)
/// and every other seed land at their END value in the publication that carries the commit (no animation row left behind),
/// image reveals complete instantly, and the per-frame clock and the host timer queue stand still without a catch-up burst
/// on exit. Every landing test has a control: the identical change OUTSIDE a scope still animates, so a regression that
/// snaps unconditionally cannot pass. Serial: it builds headless hosts and reads the process-static motion switches.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SnapScopeTests
{
    private static readonly ColorF Start = ColorF.FromRgba(200, 30, 30);
    private static readonly ColorF End = ColorF.FromRgba(30, 30, 200);

    // Unique widths identify the node under test (a component anchor and its box are different nodes).
    private const float BrushW = 111f, FlipW = 112f, EnterW = 113f, ExitW = 114f;

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly AppHost Host;

        public Rig(Component root)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var window = new HeadlessWindow(new WindowDesc("snap-scope", new Size2(320, 240), 1f));
            window.Show();
            Host = new AppHost(App, window, new HeadlessGpuDevice(), new HeadlessFontSystem(Strings), Strings, root);
            Paint(3);   // mount + settle
        }

        public SceneStore Scene => Host.Scene;

        public void Paint(int frames = 1)
        {
            for (int i = 0; i < frames; i++) Host.Paint(0);
        }

        public NodeHandle Find(float width) => Find(Scene.Root, width);

        private NodeHandle Find(NodeHandle n, float width)
        {
            if (n.IsNull) return n;
            if (Scene.Layout(n).Width == width) return n;
            for (var c = Scene.FirstChild(n); !c.IsNull; c = Scene.NextSibling(c))
            {
                var r = Find(c, width);
                if (!r.IsNull) return r;
            }
            return default;
        }

        public void Dispose()
        {
            Host.Dispose();
            App.Dispose();
        }
    }

    // ── roots ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class BrushRoot : Component
    {
        public readonly Signal<bool> Alt = new(false);
        public override Element Render()
            => new BoxEl { Width = BrushW, Height = 40f, Fill = Alt.Value ? End : Start, BrushTransitionMs = 250f };
    }

    private sealed class FlipRoot : Component
    {
        public readonly Signal<float> TopHeight = new(40f);
        public override Element Render()
            => new BoxEl
            {
                Direction = 1, Width = 300f, Height = 300f,
                Children =
                [
                    new BoxEl { Width = 100f, Height = TopHeight.Value, Fill = Start },
                    new BoxEl { Width = FlipW, Height = 20f, Fill = End, Layout = LayoutTransition.Slide },
                ],
            };
    }

    private sealed class EnterRoot : Component
    {
        public readonly Signal<bool> Show = new(false);
        public override Element Render()
        {
            Element[] kids = Show.Value
                ? [new BoxEl { Width = EnterW, Height = 20f, Fill = End, Enter = new EnterExit(Opacity: 0f, Active: true) }]
                : [];
            return new BoxEl { Direction = 1, Width = 300f, Height = 300f, Children = kids };
        }
    }

    private sealed class ExitRoot : Component
    {
        public readonly Signal<bool> Show = new(true);
        public override Element Render()
        {
            Element[] kids = Show.Value
                ? [new BoxEl { Width = ExitW, Height = 20f, Fill = End, Exit = new EnterExit(Opacity: 0f, Active: true) }]
                : [];
            return new BoxEl { Direction = 1, Width = 300f, Height = 300f, Children = kids };
        }
    }

    private sealed class IntervalRoot : Component
    {
        public int Ticks;
        public override Element Render()
        {
            UseInterval(() => Ticks++, 48f);
            return new BoxEl { Width = 10f, Height = 10f };
        }
    }

    private sealed class TickRoot : Component
    {
        public int Renders;
        public override Element Render()
        {
            _ = UseContext(AppFrameClock.Tick);   // re-render every frame while mounted
            Renders++;
            return new BoxEl { Width = 10f, Height = 10f };
        }
    }

    // ── brush ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Brush_LandsAtItsEndColourInTheSamePublication()
    {
        var root = new BrushRoot();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(BrushW);
        Assert.False(n.IsNull);
        Assert.True(rig.Scene.Paint(n).Fill == Start);

        using (SnapScope.Enter(rig.Host))
        {
            root.Alt.Value = true;
            rig.Paint();
        }

        Assert.True(rig.Scene.Paint(n).Fill == End);
        Assert.False(rig.Scene.TryGetBrushAnim(n, out _));      // no cross-fade left to run
        Assert.False(rig.Host.Animation.HasTracks(n));
    }

    [Fact]
    public void Brush_OutsideAScope_StillCrossFades()
    {
        var root = new BrushRoot();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(BrushW);

        root.Alt.Value = true;
        rig.Paint();

        Assert.True(rig.Scene.TryGetBrushAnim(n, out var fade));
        Assert.True(fade.T < 1f);
        Assert.True(rig.Host.Animation.HasTracks(n));
    }

    // ── layout FLIP ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LayoutFlip_LandsAtItsTargetWithNoTracks()
    {
        var root = new FlipRoot();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(FlipW);
        Assert.False(n.IsNull);
        Assert.Equal(40f, rig.Scene.Bounds(n).Y, 2);

        using (SnapScope.Enter(rig.Host))
        {
            root.TopHeight.Value = 100f;
            rig.Paint();
        }

        Assert.Equal(100f, rig.Scene.Bounds(n).Y, 2);                    // laid out at the target
        Assert.Equal(0f, rig.Scene.Paint(n).LocalTransform.Dy, 3);       // and PRESENTED there: no inverted offset
        Assert.False(rig.Host.Animation.HasTracks(n));
    }

    [Fact]
    public void LayoutFlip_OutsideAScope_StillAnimates()
    {
        var root = new FlipRoot();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(FlipW);

        root.TopHeight.Value = 100f;
        rig.Paint();

        Assert.Equal(100f, rig.Scene.Bounds(n).Y, 2);
        Assert.True(rig.Host.Animation.HasTracks(n));
        Assert.NotEqual(0f, rig.Scene.Paint(n).LocalTransform.Dy);       // still presented at the old position
    }

    // ── enter / exit ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_LandsVisibleWithNoTracks()
    {
        var root = new EnterRoot();
        using var rig = new Rig(root);

        using (SnapScope.Enter(rig.Host))
        {
            root.Show.Value = true;
            rig.Paint();
        }

        NodeHandle n = rig.Find(EnterW);
        Assert.False(n.IsNull);
        Assert.Equal(1f, rig.Scene.Paint(n).Opacity, 3);
        Assert.False(rig.Host.Animation.HasTracks(n));
    }

    [Fact]
    public void Enter_OutsideAScope_StillFadesIn()
    {
        var root = new EnterRoot();
        using var rig = new Rig(root);

        root.Show.Value = true;
        rig.Paint();

        NodeHandle n = rig.Find(EnterW);
        Assert.False(n.IsNull);
        Assert.True(rig.Scene.Paint(n).Opacity < 1f);
        Assert.True(rig.Host.Animation.HasTracks(n));
    }

    [Fact]
    public void Exit_FreesTheNodeAtOnce()
    {
        var root = new ExitRoot();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(ExitW);
        Assert.False(n.IsNull);

        using (SnapScope.Enter(rig.Host))
        {
            root.Show.Value = false;
            rig.Paint();
        }

        Assert.False(rig.Scene.IsLive(n));
        Assert.Equal(0, rig.Scene.OrphanCount);                          // no fading orphan
        Assert.Equal(0, rig.Host.Animation.TrackCount);
    }

    [Fact]
    public void Exit_OutsideAScope_LeavesAFadingOrphan()
    {
        var root = new ExitRoot();
        using var rig = new Rig(root);

        root.Show.Value = false;
        rig.Paint();

        Assert.True(rig.Scene.OrphanCount > 0);
        Assert.True(rig.Host.Animation.TrackCount > 0);
    }

    // ── timers and the frame clock ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Interval_DoesNotFireInsideTheScope_AndResumesWithoutABurst()
    {
        var root = new IntervalRoot();
        using var rig = new Rig(root);
        rig.Paint(12);
        int before = root.Ticks;
        Assert.True(before > 0);                                         // control: it ticks outside a scope

        using (SnapScope.Enter(rig.Host))
        {
            rig.Paint(30);                                               // ~480 ms, ten periods of 48 ms
            Assert.Equal(before, root.Ticks);
        }

        rig.Paint();
        Assert.True(root.Ticks - before <= 1);                           // the missed periods are not replayed
        rig.Paint(30);
        Assert.InRange(root.Ticks - before, 8, 12);                      // ~10 periods of live time, not ~20
    }

    [Fact]
    public void FrameClockTick_StandsStillInsideTheScope_AndResumesOnExit()
    {
        var root = new TickRoot();
        using var rig = new Rig(root);
        int start = root.Renders;
        rig.Paint(3);
        Assert.Equal(start + 3, root.Renders);                           // control: one tick-driven render per frame

        using (SnapScope.Enter(rig.Host))
        {
            rig.Paint(5);
            Assert.Equal(start + 3, root.Renders);                       // the Tick did not advance
        }

        rig.Paint(3);
        Assert.Equal(start + 6, root.Renders);                           // restored
    }

    [Fact]
    public void ScopeExit_RestoresEveryFlag()
    {
        using var rig = new Rig(new IntervalRoot());
        Assert.False(rig.Host.IsSnapping);

        var scope = SnapScope.Enter(rig.Host);
        Assert.True(rig.Host.IsSnapping);
        Assert.True(rig.Host.Animation.Snapping);
        Assert.True(rig.Host.Images.SnapReveals);
        Assert.True(rig.Host.TimersForTest.IsHeld);
        scope.Dispose();

        Assert.False(rig.Host.IsSnapping);
        Assert.False(rig.Host.Animation.Snapping);
        Assert.False(rig.Host.Images.SnapReveals);
        Assert.False(rig.Host.TimersForTest.IsHeld);
    }

    [Fact]
    public void Nesting_OnlyTheOutermostExitRestores()
    {
        var root = new BrushRoot();
        using var rig = new Rig(root);
        NodeHandle n = rig.Find(BrushW);

        var outer = SnapScope.Enter(rig.Host);
        var inner = SnapScope.Enter(rig.Host);
        inner.Dispose();
        Assert.True(rig.Host.IsSnapping);                                // still inside the outer scope

        root.Alt.Value = true;
        rig.Paint();
        Assert.True(rig.Scene.Paint(n).Fill == End);
        Assert.False(rig.Scene.TryGetBrushAnim(n, out _));               // the outer scope still snaps

        outer.Dispose();
        Assert.False(rig.Host.IsSnapping);

        root.Alt.Value = false;
        rig.Paint();
        Assert.True(rig.Scene.TryGetBrushAnim(n, out _));                // outside every scope it animates again
    }

    [Fact]
    public void DefaultScope_DisposeIsANoOp()
    {
        default(SnapScope).Dispose();
    }

    // ── image reveal and the timer queue on their own ────────────────────────────────────────────────────────────────

    [Fact]
    public void ImageReveal_CompletesInstantlyForAnImageDecodedInsideTheScope()
    {
        var cache = new ImageCache(new FakeImageDecoder());

        cache.PushSnap();
        var snapped = cache.Request("snap", 16, 16);
        cache.Pump();                                                    // the decode lands inside the scope
        cache.PopSnap();
        Assert.Equal(1f, cache.CrossFadeOf(snapped));
        Assert.False(cache.HasActiveCrossfades);

        var faded = cache.Request("fade", 16, 16);                       // control: the same decode outside a scope
        cache.Pump();
        Assert.True(cache.CrossFadeOf(faded) < 1f);
        Assert.True(cache.HasActiveCrossfades);
    }

    [Fact]
    public void TimerQueue_HoldFreezesDueTimes_ReleaseSlidesThemByTheHeldSpan()
    {
        double now = 0;
        var queue = new HostTimerQueue(() => now);
        int fired = 0;
        queue.Schedule(100, 1, _ => fired++);

        now = 40;
        queue.Hold();
        now = 500;                                                       // far past the due time while held
        Assert.False(queue.HasDue(now));
        Assert.False(queue.TryPeekEarliest(out _));
        queue.Drain();
        Assert.Equal(0, fired);

        queue.Release();                                                 // held 460 ms: due 100 -> 560
        queue.Drain();
        Assert.Equal(0, fired);                                          // 500 < 560: it did not fire the instant it resumed
        now = 559;
        queue.Drain();
        Assert.Equal(0, fired);
        now = 560;
        queue.Drain();
        Assert.Equal(1, fired);                                          // 60 ms of live time remained, and it spent exactly that
    }

    [Fact]
    public void TimerQueue_HoldNests()
    {
        double now = 0;
        var queue = new HostTimerQueue(() => now);
        int fired = 0;
        queue.Schedule(10, 1, _ => fired++);

        queue.Hold();
        queue.Hold();
        now = 100;
        queue.Release();
        queue.Drain();
        Assert.Equal(0, fired);                                          // the outer hold is still open
        queue.Release();
        queue.Drain();
        Assert.Equal(0, fired);                                          // slid to 110
        now = 110;
        queue.Drain();
        Assert.Equal(1, fired);
    }
}
