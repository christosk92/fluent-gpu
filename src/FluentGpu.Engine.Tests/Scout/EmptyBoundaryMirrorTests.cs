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
/// A transparent boundary whose branch goes away (a <c>Flow.Show</c> turned false with no Else, a Failed
/// <c>Skel.Region</c> with no OnFailed) has nothing left to mirror, so it must measure like a boundary whose branch never
/// mounted. Before the fix MirrorParticipation returned early for an empty boundary, so the anchor kept the previous
/// branch's mirrored Width/Height/Grow: a hidden 30x20 badge left a 30-wide hole, and a hidden Grow=1 branch kept pushing
/// its siblings to the far end of the row.
/// </summary>
public sealed class EmptyBoundaryMirrorTests
{
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

    // 20 + gap + (empty boundary, 0 wide) + gap: an empty boundary keeps its gap slot, exactly as before it ever showed.
    private const float EmptyX = 20f + 10f + 0f + 10f;

    [Fact]
    public void AShowThatHidesItsBranchDropsTheMirroredSize()
    {
        var shown = new Signal<bool>(false);
        var h = new Harness(Flow.Show(() => shown.Value, new BoxEl { Width = 30f, Height = 20f }));
        Assert.Equal(EmptyX, h.LastX);

        shown.Value = true;
        h.Solve();
        Assert.Equal(20f + 10f + 30f + 10f, h.LastX);

        shown.Value = false;
        h.Solve();
        Assert.Equal(EmptyX, h.LastX);
    }

    [Fact]
    public void AShowThatHidesAGrowBranchStopsEatingFreeSpace()
    {
        var shown = new Signal<bool>(true);
        var h = new Harness(Flow.Show(() => shown.Value, new BoxEl { Height = 20f, Grow = 1f }));
        Assert.Equal(800f - 20f, h.LastX);   // the grown branch pushes the probe to the far end

        shown.Value = false;
        h.Solve();
        Assert.Equal(EmptyX, h.LastX);
    }

    [Fact]
    public void ARegionThatFailsWithoutOnFailedDropsTheMirroredSize()
    {
        var failed = new Signal<bool>(false);
        var h = new Harness(new SkelRegionEl(
            Pending: () => false,
            Failed: () => failed.Value,
            Content: () => new BoxEl { Width = 30f, Height = 20f },
            ShimmerSource: null,
            OnFailed: null,
            Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: null, SmoothResize: false));
        Assert.Equal(20f + 10f + 30f + 10f, h.LastX);

        failed.Value = true;
        h.Solve();
        Assert.Equal(EmptyX, h.LastX);
    }
}
