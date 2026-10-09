using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>An enter reflow aims at the node's natural size, which counts its children's margins, while the host's
/// natural-extent retarget read only their border-box bounds. A drawer whose items carry a trailing margin was re-aimed
/// short by the last item's margin on its first tick, eased to that, and snapped the margin open at settle (along with
/// every sibling after it). Serial: it constructs a host.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class ReflowExtentMarginTests
{
    private const float Item = 40f, Gap = 8f;
    private const float Natural = 3 * (Item + Gap);   // 144: the solved auto size, trailing margin included

    private sealed class Root(Signal<bool> open, bool horizontal) : Component
    {
        private byte Dir => horizontal ? (byte)0 : (byte)1;

        public override Element Render() => new BoxEl
        {
            Direction = Dir,
            Children = open.Value ? [Edge("h"), Drawer(), Edge("f")] : [Edge("h"), Edge("f")],
        };

        private static Element Edge(string key) => new BoxEl { Key = key, Width = 20f, Height = 20f };

        private Element Drawer() => new BoxEl
        {
            Key = "d",
            Direction = Dir,
            Animate = new LayoutTransition(TransitionChannels.Size, TransitionDynamics.Tween(300f, Easing.Linear),
                SizeMode.Reflow, Enter: new EnterExit(Active: true)),
            Children = [Cell(), Cell(), Cell()],
        };

        private Element Cell() => new BoxEl
        {
            Width = Item, Height = Item,
            Margin = horizontal ? new Edges4(0f, 0f, Gap, 0f) : new Edges4(0f, 0f, 0f, Gap),
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADrawerWhoseLastItemHasATrailingMarginEasesToItsFullNaturalSize(bool horizontal)
    {
        var ch = horizontal ? AnimChannel.LayoutW : AnimChannel.LayoutH;
        var open = new Signal<bool>(false);
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("reflow-extent-margin", new Size2(400, 400), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
            new Root(open, horizontal));
        for (int i = 0; i < 3; i++) host.RunFrame();

        open.Value = true;
        host.RunFrame();
        var scene = host.Scene;
        var drawer = scene.NextSibling(scene.FirstChild(scene.Root));
        Assert.True(host.Animation.TryGetLiveReflow(drawer, ch, out _, out bool natural, out _), "no enter reflow was seeded");
        Assert.True(natural);

        int live = 0;
        for (int i = 0; i < 40; i++)
        {
            if (host.Animation.TryGetLiveReflow(drawer, ch, out float to, out _, out _))
            {
                live++;
                Assert.Equal(Natural, to, 0.5f);   // was 136: re-aimed short by the last item's margin
            }
            host.RunFrame();
        }
        Assert.True(live > 5, $"the reveal settled after {live} frames");
        var b = scene.Bounds(drawer);
        Assert.Equal(Natural, horizontal ? b.W : b.H, 0.5f);
    }
}
