using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// FlipView ported WinUI's wheel rule "delta &lt; 0 = next" verbatim, but WinUI's MouseWheelDelta is positive when the
/// wheel turns AWAY from the user while the engine's wheel delta is "positive = toward the content end". Wheel-down (and
/// a tilt right, and a touchpad swipe's hi-res stream) paged to the PREVIOUS item, and on the first item it failed to
/// move and scrolled the page behind instead.
/// </summary>
public sealed class FlipViewWheelDirectionTests
{
    private static readonly Point2 Over = new(160f, 120f);

    private sealed class Root : Component
    {
        public readonly Signal<int> Selected;
        public Root(int start) => Selected = new(start);

        public override Element Render()
            => FlipView.Create(new[] { "A", "B", "C", "D" }, width: 320f, height: 240f, selectedIndex: Selected);
    }

    private static void Run(int start, Action<AppHost, Root> body)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("flipview-wheel", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root(start);
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            body(host, root);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    // A detented notch in the PAL's own conversion (WHEEL_DELTA convention: +1 = rotated away from the user).
    private static ScrollInputEvent Notch(int wholeV, int wholeH)
        => WheelClassifier.DetentNotch(wholeV, wholeH, 1f, 0, Over, 0, KeyModifiers.None);

    private static bool Send(AppHost host, ScrollInputEvent e)
    {
        bool handled = host.Input.DispatchScroll(e);
        host.RunFrame();   // re-render so the core reads the new selection
        return handled;
    }

    [Fact]
    public void WheelTowardTheUser_PagesNext_AndAwayPagesPrevious()
    {
        Run(1, (host, root) =>
        {
            Assert.True(Send(host, Notch(-1, 0)));   // wheel down
            Assert.Equal(2, root.Selected.Peek());
            Assert.True(Send(host, Notch(+1, 0)));   // wheel up: a direction change flips at once
            Assert.Equal(1, root.Selected.Peek());
        });
    }

    [Fact]
    public void WheelDownOnTheFirstItem_PagesNext_InsteadOfScrollChaining()
    {
        Run(0, (host, root) =>
        {
            Assert.True(Send(host, Notch(-1, 0)));
            Assert.Equal(1, root.Selected.Peek());
        });
    }

    [Fact]
    public void ATiltRight_PagesNext()
    {
        Run(1, (host, root) =>
        {
            Assert.True(Send(host, Notch(0, +1)));
            Assert.Equal(2, root.Selected.Peek());
        });
    }

    [Fact]
    public void AHiResStreamTowardTheUser_PagesNext()
    {
        Run(1, (host, root) =>
        {
            // Sub-notch packets (raw 15 = 1/8 notch ≈ 7 DIP) accumulate past the flip threshold.
            for (int i = 0; i < 12; i++)
                Assert.True(host.Input.DispatchScroll(
                    WheelClassifier.HiResNotch(-15, horizontal: false, qpc: 0, Over, pointerId: 0, KeyModifiers.None)));
            host.RunFrame();
            Assert.Equal(2, root.Selected.Peek());
        });
    }
}
