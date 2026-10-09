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
/// A fixed-size ClipToBounds box is a scoped-relayout boundary, and RunSubtree re-solves it in place. A grid or a virtual
/// list arranges each cell at a slot it computes (track width × row height, the list's item rect) and ignores the cell's
/// explicit Width/Height, so a full layout gives a 160-wide card in a 240 track a 240-wide box. Before the fix the scoped
/// pass re-arranged it at its authored 160 instead: a change inside one card narrowed that card alone until the next full
/// layout. Driven through the shipping reconciler + <see cref="LayoutInvalidator"/> path.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SlotPlacedBoundaryTests
{
    private static readonly Size2 Window = new(800f, 600f);

    private sealed class Rig
    {
        public readonly StringTable Strings = new();
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly FlexLayout Layout;
        public readonly LayoutInvalidator Invalidator;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, Strings);
            Layout = new FlexLayout(Scene, new HeadlessFontSystem(Strings));
            Invalidator = new LayoutInvalidator(Scene, Layout);
        }

        public void FullLayout() { Layout.Run(Scene.Root, Window); Scene.ClearLayoutDirty(); }

        public void ScopedFrame() { Recon.Runtime.Flush(); Invalidator.RunDirty(Window); Scene.ClearLayoutDirty(); }
    }

    [Fact]
    public void AChangeInsideAGridCellKeepsItsTrackSize()
    {
        var rig = new Rig();
        var inner = new Signal<float>(20f);
        NodeHandle cell = default;
        rig.Recon.ReconcileRoot(new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Start,
            Children =
            [
                new GridEl
                {
                    Width = 480f,
                    Columns = [TrackSize.Star(), TrackSize.Star()],
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 160f, Height = 160f, ClipToBounds = true, OnRealized = n => cell = n,
                            Children = [new BoxEl { Width = Prop.Of(() => inner.Value), Height = 10f }],
                        },
                        new BoxEl { Width = 160f, Height = 160f, ClipToBounds = true },
                    ],
                },
            ],
        }, null);
        rig.FullLayout();
        RectF full = rig.Scene.Bounds(cell);
        Assert.Equal(240f, full.W);   // the track, not the authored 160

        inner.Value = 80f;
        rig.ScopedFrame();

        Assert.Equal(0, rig.Invalidator.EscapesThisFrame);   // still firewalled at the cell
        Assert.Equal(full, rig.Scene.Bounds(cell));          // was 160 wide: the one card narrowed
    }

    [Fact]
    public void AChangeInsideAVirtualRowKeepsItsItemSize()
    {
        var rig = new Rig();
        var inner = new Signal<float>(20f);
        NodeHandle row = default;
        rig.Recon.ReconcileRoot(new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Start,
            Children =
            [
                new VirtualListEl
                {
                    ItemCount = 3, Width = 240f, Height = 480f,
                    RenderItem = i => new BoxEl
                    {
                        Width = 160f, Height = 160f, ClipToBounds = true,
                        OnRealized = n => { if (i == 0) row = n; },
                        Children = [new BoxEl { Width = Prop.Of(() => inner.Value), Height = 10f }],
                    },
                },
            ],
        }, null);
        rig.FullLayout();
        Assert.False(row.IsNull);
        RectF full = rig.Scene.Bounds(row);
        Assert.Equal(240f, full.W);   // the list's cross width, not the authored 160

        inner.Value = 80f;
        rig.ScopedFrame();

        Assert.Equal(full, rig.Scene.Bounds(row));   // was 160 wide until the next full layout
    }
}
