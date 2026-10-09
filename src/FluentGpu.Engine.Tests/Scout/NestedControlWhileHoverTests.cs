using FluentGpu.Animation;
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

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A clickable WhileHover card keeps its hover pose while the pointer is on its own nested button. The
/// dispatcher sets the card's HoverWithin and then sends its leaf-off edge; SetHover folded the flags in, but the While*
/// resolver took the raw false and sprang the card back to rest until the pointer left the button again.
/// Serial: it constructs a host (process-static seams).</summary>
[Collection(SerialTestCollection.Name)]
public sealed class NestedControlWhileHoverTests
{
    private sealed class Card : Component
    {
        public NodeHandle Node, Button;
        public override Element Render() => new BoxEl
        {
            Width = 200f, Height = 100f, OnClick = static () => { },
            WhileHover = new MotionTarget { OffsetY = -8f },
            Transition = MotionTok.ControlFast,
            OnRealized = n => Node = n,
            Children = [new BoxEl { Width = 40f, Height = 40f, OnClick = static () => { }, OnRealized = n => Button = n }],
        };
    }

    private static void Settle(AppHost host)
    {
        for (int i = 0; i < 120 && (i < 2 || host.HasActiveWork); i++) host.RunFrame();
    }

    [Fact]
    public void TheCardKeepsItsHoverPoseWhileThePointerIsOnItsNestedButton()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scout-nested-while-hover", new Size2(400, 300), 1f));
        window.Show();
        var card = new Card();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, card);
        try
        {
            Settle(host);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(150f, 50f), 0, 0));   // the card's body
            Settle(host);
            Assert.Equal(-8f, host.Scene.Paint(card.Node).LocalTransform.Dy, 2);

            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(20f, 20f), 0, 0));    // onto the nested button
            Settle(host);
            Assert.True((host.Scene.Flags(card.Button) & NodeFlags.Hovered) != 0);   // the button won the hit
            Assert.True((host.Scene.Flags(card.Node) & NodeFlags.HoverWithin) != 0);
            Assert.Equal(-8f, host.Scene.Paint(card.Node).LocalTransform.Dy, 2);    // was 0: the leaf-off edge released it

            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(150f, 50f), 0, 0));   // back onto the card
            Settle(host);
            Assert.Equal(-8f, host.Scene.Paint(card.Node).LocalTransform.Dy, 2);

            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(350f, 250f), 0, 0));  // off the card
            Settle(host);
            Assert.Equal(0f, host.Scene.Paint(card.Node).LocalTransform.Dy, 2);    // a real exit still returns to rest
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
