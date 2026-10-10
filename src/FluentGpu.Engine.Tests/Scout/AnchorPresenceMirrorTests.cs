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
/// A component anchor is layout-transparent: it takes its parent's flex slot exactly as its rendered root would. A root
/// that is out of flow (<c>Visible=false</c>) must take the anchor out with it (no gap slot, no mirrored size), and a
/// root whose bound Visible/Width/Height moves without the component re-rendering must move the anchor's slot too.
/// Before the fix the anchor kept a gap slot and its render-time size (Wavee's lyrics rail header: hidden header
/// buttons left extra Spacing.XS gaps).
/// </summary>
public sealed class AnchorPresenceMirrorTests
{
    private sealed class Off : Component
    {
        public override Element Render() => new BoxEl { Visible = false };
    }

    private sealed class OffSized : Component
    {
        public override Element Render() => new BoxEl { Visible = false, Width = 100f, Height = 20f, Grow = 1f };
    }

    private sealed class BoundWidth(Signal<bool> open) : Component
    {
        public override Element Render() => new BoxEl { Width = Prop.Of(() => open.Value ? 280f : 48f), Height = 20f };
    }

    private sealed class BoundVisible(Signal<bool> shown) : Component
    {
        public override Element Render() => new BoxEl { Width = 30f, Height = 20f, Visible = Prop.Of(() => shown.Value) };
    }

    private sealed class Outer(Signal<bool> on) : Component
    {
        public override Element Render() => Embed.Comp(() => new Inner(on));
    }

    private sealed class Inner(Signal<bool> on) : Component
    {
        public override Element Render() => on.Value ? new BoxEl { Width = 40f, Height = 20f } : new BoxEl { Visible = false, Width = 40f };
    }

    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly FlexLayout Layout;
        public NodeHandle Last;

        public Harness(params Element[] middle)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var strings = new StringTable();
            Recon = new TreeReconciler(Scene, strings);
            Layout = new FlexLayout(Scene, new HeadlessFontSystem(strings));
            var kids = new List<Element> { new BoxEl { Width = 20f, Height = 20f } };
            kids.AddRange(middle);
            kids.Add(new BoxEl { Width = 20f, Height = 20f, OnRealized = n => Last = n });
            Recon.ReconcileRoot(new BoxEl { Direction = 0, Gap = 10f, Width = 800f, Height = 40f, Children = [.. kids] }, null);
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
    public void ARootRenderedCollapsedTakesNoGapSlotAndNoMirroredSize()
    {
        var h = new Harness(Embed.Comp(() => new Off()), Embed.Comp(() => new OffSized()));
        Assert.Equal(30f, h.LastX);   // 20 + one gap: the two hidden components leave the flow entirely
    }

    [Fact]
    public void ABoundRootWidthMovesTheAnchorSlotWithoutARerender()
    {
        var open = new Signal<bool>(false);
        var h = new Harness(Embed.Comp(() => new BoundWidth(open)));
        Assert.Equal(20f + 10f + 48f + 10f, h.LastX);

        open.Value = true;
        h.Solve();
        Assert.Equal(20f + 10f + 280f + 10f, h.LastX);
    }

    [Fact]
    public void ABoundRootVisibleFlipsTheAnchorInAndOutOfFlow()
    {
        var shown = new Signal<bool>(false);
        var h = new Harness(Embed.Comp(() => new BoundVisible(shown)));
        Assert.Equal(30f, h.LastX);

        shown.Value = true;
        h.Solve();
        Assert.Equal(20f + 10f + 30f + 10f, h.LastX);

        shown.Value = false;
        h.Solve();
        Assert.Equal(30f, h.LastX);
    }

    [Fact]
    public void ANestedComponentsRerenderReMirrorsTheOuterAnchor()
    {
        var on = new Signal<bool>(false);
        var h = new Harness(Embed.Comp(() => new Outer(on)));
        Assert.Equal(30f, h.LastX);

        on.Value = true;   // only Inner re-renders; Outer's anchor still mirrors Inner's
        h.Solve();
        Assert.Equal(20f + 10f + 40f + 10f, h.LastX);

        on.Value = false;
        h.Solve();
        Assert.Equal(30f, h.LastX);
    }
}
