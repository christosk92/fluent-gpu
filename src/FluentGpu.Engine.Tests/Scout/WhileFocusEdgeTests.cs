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

/// <summary>Element.WhileFocus is stashed by the reconciler and ranked above hover by the While* resolver, but no focus
/// edge reached it: the host wired only the hover and press edges. A WhileFocus card stayed at rest when tabbed onto or
/// clicked, and a WhilePressed + WhileFocus control fell back to rest on release instead of holding its focus pose.
/// Serial: it constructs a host (process-static seams).</summary>
[Collection(SerialTestCollection.Name)]
public sealed class WhileFocusEdgeTests
{
    private sealed class Card(MotionTarget? pressed) : Component
    {
        public NodeHandle Node;
        public override Element Render() => new BoxEl
        {
            Width = 100f, Height = 40f, Focusable = true, OnClick = static () => { },
            WhilePressed = pressed,
            WhileFocus = new MotionTarget { Scale = 1.5f },
            Transition = MotionTok.ControlFast,
            OnRealized = n => Node = n,
        };
    }

    private static void Settle(AppHost host)
    {
        for (int i = 0; i < 120 && (i < 2 || host.HasActiveWork); i++) host.RunFrame();
    }

    private static (HeadlessPlatformApp App, HeadlessWindow Window, AppHost Host) Boot(Card card)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scout-while-focus", new Size2(200, 200), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, card);
        Settle(host);
        Assert.False(card.Node.IsNull);
        return (app, window, host);
    }

    [Fact]
    public void KeyboardFocusSpringsToTheFocusPoseAndBlurReturnsToRest()
    {
        var card = new Card(null);
        var (app, _, host) = Boot(card);
        try
        {
            Assert.Equal(1f, host.Scene.Paint(card.Node).LocalTransform.M11, 3);

            host.Input.SetFocus(card.Node, visual: true);   // what Tab does (MoveFocus -> SetFocus)
            Settle(host);
            Assert.Equal(1.5f, host.Scene.Paint(card.Node).LocalTransform.M11, 3);   // was 1: no focus edge

            host.Input.SetFocus(NodeHandle.Null);
            Settle(host);
            Assert.Equal(1f, host.Scene.Paint(card.Node).LocalTransform.M11, 3);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void AClickedPressAndFocusControlHoldsItsFocusPoseAfterRelease()
    {
        var card = new Card(new MotionTarget { Scale = 0.9f });
        var (app, window, host) = Boot(card);
        try
        {
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(20f, 20f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(20f, 20f), 0, 0));
            Settle(host);
            Assert.Equal(card.Node, host.Input.Focused);                               // pointer focus landed on it
            Assert.Equal(0.9f, host.Scene.Paint(card.Node).LocalTransform.M11, 3);    // press outranks focus

            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(20f, 20f), 0, 0));
            Settle(host);
            Assert.Equal(1.5f, host.Scene.Paint(card.Node).LocalTransform.M11, 3);   // was 1: focus leg never won
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
