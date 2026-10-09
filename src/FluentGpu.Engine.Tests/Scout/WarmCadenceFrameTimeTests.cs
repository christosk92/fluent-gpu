using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A warm-cadence idle turn (the post-input hold's display-tick wake that runs no Paint) must still consume the frame delta
/// under a real window. The wait it paces on reads as display-rate, so Paint's step-up Resync never fires after it; an
/// unconsumed delta made the next input's Paint advance every animation by the whole gap since the last painted frame.
/// Driven through a non-headless window handle (the headless idle turn already consumes it) with a manual frame clock, and
/// a hold long enough that the wall clock cannot end it mid-test.
/// Serial: it constructs a host and touches process-static theme state.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class WarmCadenceFrameTimeTests
{
    private sealed class Root : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 200f, Height = 100f, Fill = ColorF.FromRgba(20, 60, 120),
            HoverFill = ColorF.FromRgba(60, 120, 200), OnClick = static () => { },
        };
    }

    /// <summary>A headless window that reports a non-headless handle, so the host takes the real-window paths.</summary>
    private sealed class WindowedWindow(HeadlessWindow inner) : IPlatformWindow
    {
        public NativeHandle Handle => new(0, NativeHandleKind.None);
        public Size2 ClientSizePx => inner.ClientSizePx;
        public float Scale => inner.Scale;
        public int PumpInto(InputEventRing ring) => inner.PumpInto(ring);
        public void WaitForWork(int timeoutMs) { }
        public Action? PaintRequested { get => inner.PaintRequested; set => inner.PaintRequested = value; }
        public void SetCursor(CursorId id) => inner.SetCursor(id);
        public void SetTitle(StringId title) => inner.SetTitle(title);
        public void Show() => inner.Show();
        public IPlatformTextInput TextInput => inner.TextInput;
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public void TheFirstPaintAfterWarmIdleTurns_AdvancesOneTick_NotTheWholeHold()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var inner = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        var window = new WindowedWindow(inner);
        window.Show();
        var frameTime = new ManualFrameTimeSource();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(),
            frameTime: frameTime);
        host.WarmCadenceHoldMs = 600_000f;   // the hold runs off the wall clock here: keep it live for the whole test
        try
        {
            for (int i = 0; i < 4; i++) { frameTime.Advance(16f); host.RunFrame(); }
            // A press and release outside the box: an interaction edge that arms the hold and changes nothing on screen.
            inner.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(300f, 200f), 0, 0));
            inner.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(300f, 200f), 0, 0));
            frameTime.Advance(16f); host.RunFrame();
            Assert.True((host.CurrentWakeReasons & WakeReasons.WarmCadence) != 0, "the hold is armed");
            for (int i = 0; i < 8 && host.CurrentWakeReasons != WakeReasons.WarmCadence; i++) { frameTime.Advance(16f); host.RunFrame(); }
            Assert.Equal(WakeReasons.WarmCadence, host.CurrentWakeReasons);

            long idle = host.WarmCadenceIdleTurns;
            for (int i = 0; i < 20; i++)
            {
                host.RecommendedWaitMs();   // the loop's wait for this tick: the hold's display-rate wait
                frameTime.Advance(16f);
                host.RunFrame();
            }
            Assert.Equal(idle + 20, host.WarmCadenceIdleTurns);   // none of them painted

            host.RecommendedWaitMs();   // the wait the loop was in when the hover arrived
            frameTime.Advance(16f);
            inner.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(10f, 10f), 0, 0));
            double clock = host.FrameClockMsForTest;
            host.RunFrame();
            Assert.Equal(16.0, host.FrameClockMsForTest - clock, 3);   // one tick, not the 20 idle turns' 320 ms on top
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
