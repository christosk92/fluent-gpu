using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E6: a pointer LISTENER (<see cref="BoxEl.HoverScopeTransparent"/> + <c>OnPointerPressed</c>, i.e. the ToolTip wrapper) must
/// hear a press that lands on an INTERACTIVE descendant. The press is delivered to the hit node, and a tool-tipped button
/// wins the hit over its wrapper, so the wrapper never heard it and the "a press dismisses the bubble" rule only held for a
/// non-interactive target: clicking the video options chevron left its bubble open over the menu that click opened.
/// The listener still never owns the gesture: the release and the click reach the button.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class PressListenerTests
{
    private sealed class Root : Component
    {
        public int ListenerPresses, Clicks;

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children =
            [
                new BoxEl
                {
                    Width = 120f, Height = 40f, HoverScopeTransparent = true,
                    OnPointerPressed = _ => ListenerPresses++,
                    Children = [new BoxEl { Width = 120f, Height = 40f, OnClick = () => Clicks++ }],
                },
            ],
        };
    }

    [Fact]
    public void AListenerAboveAnInteractiveTarget_HearsThePress_AndTheTargetStillGetsTheClick()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("press-listener", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var at = new Point2(20f, 20f);

            window.QueueInput(new InputEvent(InputKind.PointerMove, at, 0, 0, TimestampMs: 1000));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerDown, at, 0, 0, TimestampMs: 1010));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, at, 0, 0, TimestampMs: 1050));
            host.RunFrame();

            Assert.Equal(1, root.ListenerPresses);   // the wrapper heard the press the button took
            Assert.Equal(1, root.Clicks);            // ...and the gesture still belongs to the button
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
