using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A transparent boundary that swaps its branch in its OWN effect (<c>Flow.Show</c>, <c>Skel.Region</c>,
/// <c>Flow.KeepAlive</c>) re-mirrors its own anchor, but the component (or provider) anchor above it mirrors THAT anchor
/// and did not re-render. Before the fix the upper anchor kept the old branch's participation: a collapsed Show left the
/// expanded width as a blank gap, a Ready region under a component kept the shimmer's Grow=0 (the empty-Liked list), and
/// a PageHost-shaped component → provider → KeepAlive kept the first page's size on both upper anchors. A parent
/// re-render does not save it either: RunComponent mirrors before the boundary's scheduled effect swaps the branch.
/// </summary>
public sealed class BoundaryRemirrorAncestorsTests
{
    private static readonly Context<int> Channel = new(0);

    private sealed class ShowRoot(Signal<bool> expanded) : Component
    {
        public override Element Render()
            => Flow.Show(() => expanded.Value, new BoxEl { Width = 320f, Height = 20f }, new BoxEl { Width = 48f, Height = 20f });
    }

    private sealed class RerenderingShowRoot(Signal<bool> expanded) : Component
    {
        public override Element Render()
        {
            bool open = expanded.Value;   // tracked by THIS render: the parent re-renders in the same flush as the Show
            return Flow.Show(() => open, new BoxEl { Width = 320f, Height = 20f }, new BoxEl { Width = 48f, Height = 20f });
        }
    }

    private sealed class RegionRoot(Signal<bool> pending) : Component
    {
        public override Element Render() => new SkelRegionEl(
            Pending: () => pending.Value,
            Failed: () => false,
            Content: () => new BoxEl { Height = 20f, Grow = 1f },
            ShimmerSource: () => new BoxEl { Width = 30f, Height = 20f },
            OnFailed: null,
            Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
    }

    private sealed class PageHostRoot(Signal<string> page) : Component
    {
        public override Element Render() => Ctx.Provide(Channel, 1, Flow.KeepAlive(() => page.Value, static k => k,
            static k => k == "a" ? new BoxEl { Width = 30f, Height = 20f } : new BoxEl { Height = 20f, Grow = 1f }));
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

    // 20 + gap + (middle) + gap; a Grow=1 middle pushes the 20-wide probe to the far end of the 800 row.
    private static float After(float middle) => 20f + 10f + middle + 10f;
    private const float FarEnd = 800f - 20f;

    [Fact]
    public void AShowSwapUnderAComponentMovesTheComponentSlot()
    {
        var expanded = new Signal<bool>(true);
        var h = new Harness(Embed.Comp(() => new ShowRoot(expanded)));
        Assert.Equal(After(320f), h.LastX);

        expanded.Value = false;   // only the Show's effect runs; the component does not re-render
        h.Solve();
        Assert.Equal(After(48f), h.LastX);

        expanded.Value = true;
        h.Solve();
        Assert.Equal(After(320f), h.LastX);
    }

    [Fact]
    public void AShowSwapInTheSameFlushAsItsComponentsRerenderStillLands()
    {
        var expanded = new Signal<bool>(true);
        var h = new Harness(Embed.Comp(() => new RerenderingShowRoot(expanded)));
        Assert.Equal(After(320f), h.LastX);

        expanded.Value = false;
        h.Solve();
        Assert.Equal(After(48f), h.LastX);
    }

    [Fact]
    public void ARegionGoingReadyUnderAComponentInheritsTheContentGrow()
    {
        var pending = new Signal<bool>(true);
        var h = new Harness(Embed.Comp(() => new RegionRoot(pending)));
        Assert.Equal(After(30f), h.LastX);   // the shimmer's own 30-wide box

        pending.Value = false;
        h.Solve();
        Assert.Equal(FarEnd, h.LastX);
    }

    [Fact]
    public void AKeepAliveNavigationUnderAProviderAndAComponentMovesBothAnchors()
    {
        var page = new Signal<string>("a");
        var h = new Harness(Embed.Comp(() => new PageHostRoot(page)));
        Assert.Equal(After(30f), h.LastX);

        page.Value = "b";
        h.Solve();
        Assert.Equal(FarEnd, h.LastX);

        page.Value = "a";
        h.Solve();
        Assert.Equal(After(30f), h.LastX);
    }
}
