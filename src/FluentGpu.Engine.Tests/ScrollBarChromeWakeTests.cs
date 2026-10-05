using System;
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
/// The scrollbar chrome asks for frames only while something on screen changes. A bar counting a dwell (the 2 s idle
/// hide after a scroll, the hover dwells) is fully drawn and still: it publishes when the dwell expires and the host
/// sleeps until then. A viewport whose bar is suppressed (a lyrics column, a pager-driven shelf) never draws one, so it
/// never arms the chrome at all.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ScrollBarChromeWakeTests
{
    // ── the pure timeline ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AVisibleIdleBarAsksForNoFramesAndIsDueWhenItsIdleHideExpires()
    {
        var cs = new ScrollBarChromeRow();
        // One moving frame reveals the bar; the fade-in then runs per frame.
        ScrollBarTimeline.Advance(ref cs, 16f, 16f, scrollable: true, movingNow: true, out _, out _, out float due);
        Assert.Equal(0f, due);
        float idle = 0f;
        while (cs.FadeT < 1f)
        {
            ScrollBarTimeline.Advance(ref cs, 16f, 16f, true, false, out _, out _, out due);
            idle += 16f;
        }
        // Fully shown and still: only the idle hide is counting.
        Assert.True(due > 0f);
        Assert.Equal(ScrollBarTimeline.IdleHideMs - idle, due, 3);
    }

    [Fact]
    public void OneAdvanceAcrossTheSleptDwellLandsOnTheSameFrameAsPerFrameTicks()
    {
        static ScrollBarChromeRow Revealed()
        {
            var cs = new ScrollBarChromeRow();
            ScrollBarTimeline.Advance(ref cs, 16f, 16f, true, true, out _, out _, out _);
            while (cs.FadeT < 1f) ScrollBarTimeline.Advance(ref cs, 16f, 16f, true, false, out _, out _, out _);
            return cs;
        }

        // Per-frame ticking: count how long until the fade-out starts.
        var a = Revealed();
        float waitedA = 0f;
        while (a.FadeTarget == 1f) { ScrollBarTimeline.Advance(ref a, 16f, 16f, true, false, out _, out _, out _); waitedA += 16f; }

        // Sleeping: one advance by exactly the published due time.
        var b = Revealed();
        ScrollBarTimeline.Advance(ref b, 16f, 16f, true, false, out _, out _, out float due);
        ScrollBarTimeline.Advance(ref b, due, 16f, true, false, out _, out bool changed, out float after);
        Assert.Equal(0f, b.FadeTarget);       // the hide started on the wake frame…
        Assert.True(changed);                 // …and the fade's first step was drawn there
        Assert.Equal(0f, after);              // from here the fade runs per frame
        Assert.True(b.FadeT < 1f && b.FadeT > 0f);
        Assert.InRange(16f + due, waitedA - 16f, waitedA);   // the same moment the per-frame path got there, to a frame
    }

    [Fact]
    public void TheEasedTracksStepByTheFrameDeltaNotTheSleptGap()
    {
        var cs = new ScrollBarChromeRow();
        ScrollBarTimeline.Advance(ref cs, 16f, 16f, true, true, out _, out _, out _);
        while (cs.FadeT < 1f) ScrollBarTimeline.Advance(ref cs, 16f, 16f, true, false, out _, out _, out _);
        // A 5 s gap: the dwell is long past, but the 83 ms fade-out must still be drawn, not skipped.
        ScrollBarTimeline.Advance(ref cs, 5_000f, 8f, true, false, out bool hidden, out _, out _);
        Assert.False(hidden);
        Assert.True(cs.FadeT > 0.8f && cs.FadeT < 1f);
    }

    // ── the host ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Root(bool suppressBar) : Component
    {
        public override Element Render()
            => new ScrollEl { Content = new BoxEl { Height = 20_000f, Direction = 1 }, Grow = 1f, SuppressScrollBar = suppressBar };
    }

    private static NodeHandle FindViewport(SceneStore scene)
    {
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) return h;
        }
        return default;
    }

    [Fact]
    public void ASuppressedBarNeverArmsTheChrome()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("suppressed-bar", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(suppressBar: true));
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var vp = FindViewport(host.Scene);
            Assert.False(vp.IsNull);
            host.TryGetScrollHandle(vp)!.ScrollTo(4_000.0, ScrollMove.Immediate);
            host.RunFrame();
            Assert.Equal(4_000.0, host.Scene.ScrollRef(vp).Offset);   // it moved…
            Assert.False(host.ScrollChrome.Active);                   // …and no chrome cycle started
            Assert.False(host.ScrollChrome.NeedsFrame);
            Assert.Equal(0, (int)(host.CurrentWakeReasons & WakeReasons.ScrollAnim));
        }
        finally { host.Dispose(); }
    }

    [Fact]
    public void AnIdleBarSleepsThroughItsHideDelayAndStillHides()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("idle-bar", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(suppressBar: false));
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var vp = FindViewport(host.Scene);
            Assert.False(vp.IsNull);
            host.TryGetScrollHandle(vp)!.ScrollTo(4_000.0, ScrollMove.Immediate);
            host.ScrollChrome.NotifyMoved((int)vp.Raw.Index);   // the user's move: a programmatic ScrollTo alone no longer arms the bar (F238)
            for (int i = 0; i < 20; i++) host.RunFrame();   // the move, then the 83 ms fade-in

            Assert.Equal(1f, host.Scene.ScrollChrome.Get((int)vp.Raw.Index).FadeT);
            Assert.True(host.ScrollChrome.Active);
            Assert.False(host.ScrollChrome.NeedsFrame);                                   // still: no per-frame ticks
            Assert.Equal(0, (int)(host.CurrentWakeReasons & WakeReasons.ScrollAnim));
            Assert.True(host.ScrollChrome.TryPeekDue(out double due));
            double clock = host.FrameClockMsForTest;
            Assert.True(due > clock);
            // The host would sleep until the hide is due (as for a pending timeout), and no frame runs in between.
            int wait = host.RecommendedWaitMs();
            Assert.InRange(wait, (int)Math.Floor(due - clock), (int)Math.Ceiling(due - clock));
            for (int i = 0; i < 8; i++) Assert.False(host.RunFrame().Rendered);
            Assert.Equal(clock, host.FrameClockMsForTest);

            // Let the slept time pass (headless: the clock only moves when a frame paints), then the due frame runs.
            int guard = 0;
            while (host.FrameClockMsForTest < due && guard++ < 1_000) host.Paint(0);
            Assert.True(host.ScrollChrome.NeedsFrame || host.Scene.ScrollChrome.Get((int)vp.Raw.Index).FadeT < 1f);
            for (int i = 0; i < 20; i++) host.RunFrame();
            Assert.Equal(0f, host.Scene.ScrollChrome.Get((int)vp.Raw.Index).FadeT);       // the bar hid on time
            Assert.False(host.ScrollChrome.Active);
            Assert.False(host.ScrollChrome.TryPeekDue(out _));
        }
        finally { host.Dispose(); }
    }
}
