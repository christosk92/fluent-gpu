using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>Every mouse move over a FlipView re-armed its 3s nav-button fade by mounting a DebounceTicker, a
/// FrameClock.Tick poller that re-rendered (and allocated) every vsync, so a static photo viewer ran the UI loop at panel
/// rate for 3s after each move. The fade is a host one-shot now: no poller, and it fires on the host timer clock.</summary>
public sealed class FlipViewFadeTimerTests
{
    private sealed class Root : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 300f, Height = 200f,
            Children = [FlipView.Create(["0", "1", "2"], 200f, 120f)],
        };
    }

    private sealed class Rig : System.IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly HeadlessWindow Window;
        public readonly AppHost Host;

        public Rig()
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("flipview-fade", new Size2(300, 200), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root());
            Frames(4);
        }

        public void Frames(int n = 3) { for (int i = 0; i < n; i++) Host.RunFrame(); }

        public void MoveTo(float x, float y)
        {
            Window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(x, y), 0, 0));
            Frames();
        }

        public void Advance(double ms) { Host.AdvanceFrameClockForTest(ms); Frames(); }

        /// <summary>Counts the nav buttons that are in the tree. An unmounted bar keeps its node alive but detached
        /// (no parent), so a live Button with no parent is not on screen and is skipped.</summary>
        public int NavButtons()
        {
            var scene = Host.Scene;
            int n = 0;
            for (int i = 0; i < scene.Capacity; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && !scene.Parent(h).IsNull && scene.Interaction(h).Role == AutomationRole.Button) n++;
            }
            return n;
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    [Fact]
    public void A_hover_shows_the_nav_buttons_without_holding_the_frame_clock()
    {
        using var r = new Rig();
        Assert.Equal(0, r.NavButtons());
        r.MoveTo(100f, 60f);
        Assert.Equal(1, r.NavButtons());                       // index 0: only the next bar
        Assert.Equal(0, r.Host.FrameClockPollerCount);         // was 1: the DebounceTicker
        Assert.Equal(WakeReasons.None, r.Host.CurrentWakeReasons & WakeReasons.FrameClockPoller);
    }

    [Fact]
    public void The_buttons_fade_three_seconds_after_the_last_hover_on_the_host_timer_clock()
    {
        using var r = new Rig();
        r.MoveTo(100f, 60f);
        r.Advance(2000);
        r.MoveTo(250f, 150f);                                  // leave the FlipView...
        r.MoveTo(120f, 60f);                                   // ...and re-enter: re-arms the fade from now
        r.Advance(2000);
        Assert.Equal(1, r.NavButtons());                       // 4s since the first hover, 2s since the last
        r.Advance(1100);
        Assert.Equal(0, r.NavButtons());                       // 3.1s since the last hover
    }
}
