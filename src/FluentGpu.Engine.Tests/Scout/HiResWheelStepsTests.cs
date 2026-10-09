using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A hi-res (free-spin) mouse wheel sends one detent as several sub-notch packets (8 × raw 15 on a resolution-multiplier
/// wheel), and the dispatcher handed each packet to element wheel handlers on its own: a handler that acts once per
/// event (the media seek bar's ±5 s, the player's Alt+wheel volume step) fired eight times per detent.
/// <see cref="WheelEventArgs.Steps"/> carries the whole detents an event completes, so a discrete action steps once.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class HiResWheelStepsTests
{
    private static readonly Point2 Over = new(50f, 50f);

    private sealed class Root : Component
    {
        public int Events, Steps;

        public override Element Render() => new BoxEl
        {
            Width = 200f, Height = 100f,
            OnPointerWheel = e => { Events++; Steps += e.Steps; e.Handled = true; },
        };
    }

    private static void Run(Action<AppHost, Root> body)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("hires-wheel-steps", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            body(host, root);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    private static ScrollInputEvent HiRes(int raw)
        => WheelClassifier.HiResNotch(raw, horizontal: false, qpc: 0, Over, pointerId: 0, KeyModifiers.None);

    [Fact]
    public void AHiResDetentOfEightPackets_CompletesOneStep()
    {
        Run((host, root) =>
        {
            for (int i = 0; i < 8; i++) Assert.True(host.Input.DispatchScroll(HiRes(-15)));   // one detent toward the user
            Assert.Equal(8, root.Events);   // every packet still reaches (and is consumed by) the handler
            Assert.Equal(1, root.Steps);    // but together they are one step toward the content end, not eight
        });
    }

    [Fact]
    public void ADetentedNotch_IsOneStep()
    {
        Run((host, root) =>
        {
            Assert.True(host.Input.DispatchScroll(new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, Over,
                0f, 1f, 0, KeyModifiers.None)));
            Assert.Equal(1, root.Events);
            Assert.Equal(1, root.Steps);
        });
    }

    [Fact]
    public void AReversalMidDetent_RestartsTheDetentInTheNewDirection()
    {
        Run((host, root) =>
        {
            for (int i = 0; i < 4; i++) host.Input.DispatchScroll(HiRes(-15));   // half a detent toward the user
            Assert.Equal(0, root.Steps);
            for (int i = 0; i < 8; i++) host.Input.DispatchScroll(HiRes(15));    // one full detent away
            Assert.Equal(-1, root.Steps);
        });
    }
}
