using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A bound <c>Visible</c> flips presence without a re-render, so nothing re-applies the element's authored
/// <c>HitTestVisible</c> afterwards. The collapse used to clear and the reveal used to SET NodeFlags.HitTestVisible, so
/// a decorative layer authored HitTestVisible=false (a scrim, a gradient — Wavee's stage smoke) became hit-testable
/// after one hide/show and took wheel, drop and middle-click input from the content beneath it until its component
/// re-rendered. Presence now owns only NodeFlags.Visible; every hit walk already requires both bits.
/// </summary>
public sealed class CollapseHitTestVisibleTests
{
    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly FlexLayout Layout;
        public NodeHandle Target;

        public Harness(BoxEl target)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var strings = new StringTable();
            Recon = new TreeReconciler(Scene, strings);
            Layout = new FlexLayout(Scene, new HeadlessFontSystem(strings));
            Recon.ReconcileRoot(new BoxEl
            {
                Width = 200f, Height = 100f,
                Children = [new BoxEl { Width = 200f, Height = 50f }, target with { OnRealized = n => Target = n }],
            }, null);
            Solve();
        }

        public void Solve()
        {
            Recon.Runtime.Flush();
            Layout.Run(Scene.Root, new Size2(200f, 100f));
        }

        public bool Visible => (Scene.Flags(Target) & NodeFlags.Visible) != 0;
        public bool HitTestVisible => (Scene.Flags(Target) & NodeFlags.HitTestVisible) != 0;
    }

    [Fact]
    public void AStaticHitTestVisibleFalseSurvivesABoundVisibleHideShow()
    {
        var shown = new Signal<bool>(true);
        var h = new Harness(new BoxEl { Width = 200f, Height = 50f, HitTestVisible = false, Visible = Prop.Of(() => shown.Value) });
        Assert.True(h.Visible);
        Assert.False(h.HitTestVisible);

        shown.Value = false;
        h.Solve();
        Assert.False(h.Visible);

        shown.Value = true;
        h.Solve();
        Assert.True(h.Visible);
        Assert.False(h.HitTestVisible);   // the reveal must not re-enable a layer authored non-hit-testable
    }

    [Fact]
    public void ABoundHitTestVisibleFalseSurvivesABoundVisibleHideShow()
    {
        var shown = new Signal<bool>(true);
        var hit = new Signal<bool>(false);
        var h = new Harness(new BoxEl
        {
            Width = 200f, Height = 50f, HitTestVisible = Prop.Of(() => hit.Value), Visible = Prop.Of(() => shown.Value),
        });
        Assert.False(h.HitTestVisible);

        shown.Value = false;
        h.Solve();
        shown.Value = true;
        h.Solve();
        Assert.False(h.HitTestVisible);   // the HitTestVisible binding did not change, so it never fires to correct it

        hit.Value = true;
        h.Solve();
        Assert.True(h.HitTestVisible);    // the binding still owns the bit
    }

    [Fact]
    public void ADefaultNodeIsUnhittableWhileCollapsedAndHittableAgainOnReveal()
    {
        var shown = new Signal<bool>(true);
        var h = new Harness(new BoxEl { Width = 200f, Height = 50f, Visible = Prop.Of(() => shown.Value) });

        shown.Value = false;
        h.Solve();
        Assert.False(h.Visible);          // hit walks require Visible|HitTestVisible, so this alone prunes it

        shown.Value = true;
        h.Solve();
        Assert.True(h.Visible);
        Assert.True(h.HitTestVisible);
    }

    [Fact]
    public void ACollapsedDropTargetIsNotSpotlighted()
    {
        var shown = new Signal<bool>(true);
        var h = new Harness(new BoxEl
        {
            Width = 200f, Height = 50f, Visible = Prop.Of(() => shown.Value),
            DropTarget = new DropTargetSpec(["k"]) { VisualPolicy = DropTargetVisualPolicy.Spotlight },
        });
        var session = new DragSession { Kind = "k" };

        h.Scene.RefreshDropSpotlight(session);
        Assert.Equal(1, h.Scene.DropSpotlightRootCount);

        shown.Value = false;
        h.Solve();
        h.Scene.RefreshDropSpotlight(session);
        Assert.Equal(0, h.Scene.DropSpotlightRootCount);   // collapsed ⇒ unreachable, now via Visible not HitTestVisible

        shown.Value = true;
        h.Solve();
        h.Scene.RefreshDropSpotlight(session);
        Assert.Equal(1, h.Scene.DropSpotlightRootCount);
    }
}
