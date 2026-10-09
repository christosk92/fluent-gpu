using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// Hooks installed ON a component's rendered root (UseMeasuredBounds/Width, UseGesture) must follow that root when a
/// root-TYPE change remounts it (loading Text -> content Box). Before the fix both registered once on the first node:
/// the measured width froze at the placeholder's width and taps on the new root reached nothing.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class HostNodeSwapTests
{
    private sealed class Host(Component child) : Component
    {
        public override Element Render() => new BoxEl { Grow = 1f, Direction = 0, Children = [Embed.Comp(() => child)] };
    }

    private sealed class SwapRoot : Component
    {
        public readonly Signal<bool> Loaded = new(false);
        public readonly FloatSignal BoxW = new(300f);
        public float Seen = -1f;
        public NodeHandle HostNode => Context.HostNode;

        public override Element Render()
        {
            Seen = UseMeasuredWidth().Value;
            UseGesture(GestureType.Tap, static _ => { });
            if (!Loaded.Value) return Ui.Text("Loading...");
            return new BoxEl { Grow = 0f, Shrink = 0f, Height = 50f, Width = Prop.Of(() => BoxW.Value) };
        }
    }

    private static void Run(bool nested)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("swap", new Size2(800, 600), 1f));
        window.Show();
        var probe = new SwapRoot();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
            nested ? new Host(probe) : probe);
        for (int i = 0; i < 5; i++) host.RunFrame();

        probe.Loaded.Value = true;                       // root Text -> Box: the reconciler frees the old node, mounts a new one
        for (int i = 0; i < 5; i++) host.RunFrame();
        Assert.Equal(300f, probe.Seen);
        Assert.True(host.Scene.WantsGesture(probe.HostNode, GestureType.Tap));

        probe.BoxW.Value = 420f;                         // the hook must now track the NEW root's resizes
        for (int i = 0; i < 5; i++) host.RunFrame();
        Assert.Equal(420f, probe.Seen);
    }

    [Fact]
    public void NestedComponentHooksFollowARootTypeSwap() => Run(nested: true);

    [Fact]
    public void RootComponentHooksFollowARootTypeSwap() => Run(nested: false);
}
