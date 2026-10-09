using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// Base-<see cref="Element"/> props (Visible, .Sticky, Stagger, Enter/Exit, …) on the reconciler's boundary kinds. Mount
/// routes ScrollEl/VirtualListEl/Show/For/KeepAlive/SkelRegion/Ctx.Provide past its WriteColumns-then-BindNode pair, so
/// before the fix a BOUND Visible on a scroller was skipped by WriteColumns (BindPresence owns it) yet never wired, and a
/// static Visible or a .Sticky on a Show/For boundary never reached the node at all — each compiled and was silently dropped.
/// </summary>
public sealed class BoundaryBasePropsTests
{
    private sealed class Rebinding(Signal<bool> useB, Signal<bool> a, Signal<bool> b, Action<NodeHandle> realized) : Component
    {
        public override Element Render() => new ScrollEl
        {
            Content = new BoxEl { Height = 200f },
            Width = 30f,
            Height = 20f,
            Visible = useB.Value ? Prop.Of(() => b.Value) : Prop.Of(() => a.Value),
            OnRealized = realized,
        };
    }

    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly FlexLayout Layout;
        public NodeHandle Last;

        public Harness(Element middle)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var strings = new StringTable();
            Recon = new TreeReconciler(Scene, strings);
            Layout = new FlexLayout(Scene, new HeadlessFontSystem(strings));
            Recon.ReconcileRoot(new BoxEl
            {
                Direction = 0, Gap = 10f, Width = 800f, Height = 40f,
                Children = [new BoxEl { Width = 20f, Height = 20f }, middle, new BoxEl { Width = 20f, Height = 20f, OnRealized = n => Last = n }],
            }, null);
            Solve();
        }

        public void Solve()
        {
            Recon.Runtime.Flush();
            Layout.Run(Scene.Root, new Size2(800f, 40f));
        }

        public float LastX => Scene.Bounds(Last).X;
    }

    [Fact]
    public void ABoundVisibleOnAScrollViewCollapsesAndRestoresIt()
    {
        var shown = new Signal<bool>(false);
        NodeHandle scroll = default;
        var h = new Harness(new ScrollEl
        {
            Content = new BoxEl { Height = 200f }, Width = 30f, Height = 20f,
            Visible = Prop.Of(() => shown.Value), OnRealized = n => scroll = n,
        });
        Assert.True(h.Scene.IsCollapsed(scroll));
        Assert.Equal(30f, h.LastX);

        shown.Value = true;
        h.Solve();
        Assert.False(h.Scene.IsCollapsed(scroll));
        Assert.Equal(20f + 10f + 30f + 10f, h.LastX);
    }

    [Fact]
    public void ABoundVisibleOnAVirtualListCollapsesAndRestoresIt()
    {
        var shown = new Signal<bool>(false);
        NodeHandle list = default;
        var h = new Harness(new VirtualListEl
        {
            ItemCount = 3, RenderItem = static _ => new BoxEl { Height = 20f }, Width = 30f, Height = 20f,
            Visible = Prop.Of(() => shown.Value), OnRealized = n => list = n,
        });
        Assert.True(h.Scene.IsCollapsed(list));

        shown.Value = true;
        h.Solve();
        Assert.False(h.Scene.IsCollapsed(list));
    }

    [Fact]
    public void AReRenderedScrollViewReWiresItsBoundVisible()
    {
        var useB = new Signal<bool>(false);
        var a = new Signal<bool>(true);
        var b = new Signal<bool>(false);
        NodeHandle scroll = default;
        var h = new Harness(Embed.Comp(() => new Rebinding(useB, a, b, n => scroll = n)));
        Assert.False(h.Scene.IsCollapsed(scroll));

        useB.Value = true;   // the re-render binds Visible to a NEW thunk (b = false)
        h.Solve();
        Assert.True(h.Scene.IsCollapsed(scroll));

        b.Value = true;
        h.Solve();
        Assert.False(h.Scene.IsCollapsed(scroll));
    }

    [Fact]
    public void AStaticVisibleFalseOnAShowTakesTheBoundaryOutOfFlow()
    {
        NodeHandle branch = default;
        var h = new Harness(Flow.Show(() => true, new BoxEl { Width = 30f, Height = 20f, OnRealized = n => branch = n }) with { Visible = false });
        Assert.True(h.Scene.IsCollapsed(h.Scene.Parent(branch)));
        Assert.Equal(30f, h.LastX);   // before: the boundary mirrored its 30-wide branch → 70
    }

    [Fact]
    public void ABoundVisibleOnAForCollapsesTheBoundary()
    {
        var shown = new Signal<bool>(false);
        IReadOnlyList<string> items = ["a"];
        NodeHandle row = default;
        var h = new Harness(Flow.For(() => items, static s => s, s => (Element)new BoxEl { Width = 30f, Height = 20f, OnRealized = n => row = n })
            with { Visible = Prop.Of(() => shown.Value) });
        var boundary = h.Scene.Parent(row);
        Assert.True(h.Scene.IsCollapsed(boundary));

        shown.Value = true;
        h.Solve();
        Assert.False(h.Scene.IsCollapsed(boundary));
    }

    [Fact]
    public void AStickyOnAShowBakesItsScrollEffectRow()
    {
        NodeHandle branch = default;
        var h = new Harness(Flow.Show(() => true, new BoxEl { Width = 30f, Height = 20f, OnRealized = n => branch = n }).Sticky(0f));
        Assert.True(h.Scene.TryGetScrollEffects((int)h.Scene.Parent(branch).Raw.Index, out var rows));
        Assert.Single(rows);
    }
}
