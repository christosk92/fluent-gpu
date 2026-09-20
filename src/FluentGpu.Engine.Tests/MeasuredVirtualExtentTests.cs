using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The measured-virtualization invariant <c>FlexLayout.ArrangeVirtualMeasured</c> exists to keep: for a REALIZED row,
/// the layout's extent table and the box that row is actually arranged into never disagree. They may diverge only for
/// the one case the deferral was built for — a FRESH row above the anchor whose first measure came back transiently
/// SHORT (its inner content lands a frame later), where pushing that transient into the table would re-pin the scroll
/// offset down and back up (the felt jitter).
/// <para>Driven headlessly against the real <see cref="FlexLayout"/> + <see cref="MeasuredStackVirtualLayout"/> + the
/// real <see cref="ScrollKernel"/> (the only writer of <c>ScrollState.OffsetY</c>), so what is pinned is the shipping
/// arrange path rather than a re-implementation of it.</para>
/// </summary>
public sealed class MeasuredVirtualExtentTests
{
    private const int Items = 20;
    private const float RowH = 64f;
    private const float ExpandedH = 264f;
    private const float Cross = 300f;
    private const float ViewportH = 300f;
    private const float Offset = 200f;        // ⇒ anchorIndex 3, sitting 8px above the viewport top, while every extent is RowH
    private const int AnchorIndex = 3;
    private const float AnchorWithin = Offset - AnchorIndex * RowH;

    private sealed class Harness
    {
        public SceneStore Scene = null!;
        public FlexLayout Layout = null!;
        public MeasuredStackVirtualLayout Virt = null!;
        public ScrollKernel Kernel = null!;
        public NodeHandle Root = default, Viewport = default, Content = default;
        public NodeHandle[] Rows = null!;

        public void Run() => Layout.Run(Root);

        /// <summary>Drain what layout posted (SetFrame / AnchorShift) and, optionally, jump the offset first.</summary>
        public void Reclamp(float? scrollTo = null)
        {
            if (scrollTo is { } to) Kernel.Port.Post(ScrollInput.ScrollTo((int)Viewport.Raw.Index, to, immediate: true));
            Kernel.Reclamp();
        }

        /// <summary>Pretend the previous arrange saw only [first..last] — which is what a full root layout forced by an
        /// overlay opening looks like to every row outside that window: they all read as FRESH.</summary>
        public void StalePrevWindow(int first, int last)
        {
            ref ScrollState sc = ref Scene.ScrollRef(Viewport);
            sc.PrevArrangedFirst = first;
            sc.PrevArrangedLast = last;
        }

        public void SetRowHeight(int index, float h)
        {
            Scene.Layout(Rows[index]).Height = h;
            Scene.Mark(Rows[index], NodeFlags.LayoutDirty);
        }

        public float TableExtent(int index) => Virt.ItemRect(index, Cross).H;
        public RectF Box(int index) => Scene.Bounds(Rows[index]);
        public float OffsetY { get { Scene.TryGetScroll(Viewport, out var sc); return sc.OffsetY; } }
        public float ContentH { get { Scene.TryGetScroll(Viewport, out var sc); return sc.ContentH; } }
        /// <summary>Where the anchor row's top sits relative to the viewport top — what must not move.</summary>
        public float AnchorOnScreen => Box(AnchorIndex).Y - OffsetY;
    }

    private static Harness Build()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var kernel = new ScrollKernel(new SceneScrollSink(scene, static () => { }), ScrollFeel.Shipping);
        scene.ScrollPort = kernel.Port;   // wired BEFORE the scroll row exists, so its Bind reaches the kernel

        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Layout(root).Direction = 1;
        scene.Layout(root).Width = Cross;
        scene.Layout(root).Height = ViewportH;

        var viewport = scene.CreateNode(2);
        scene.AppendChild(root, viewport);
        scene.Layout(viewport).Width = Cross;
        scene.Layout(viewport).Height = ViewportH;

        var content = scene.CreateNode(3);
        scene.AppendChild(viewport, content);

        var rows = new NodeHandle[Items];
        for (int i = 0; i < Items; i++)
        {
            var row = scene.CreateNode(4);
            scene.AppendChild(content, row);
            scene.Layout(row).Width = Cross;
            scene.Layout(row).Height = RowH;
            rows[i] = row;
        }

        var virt = new MeasuredStackVirtualLayout(RowH);
        {
            ref ScrollState sc = ref scene.ScrollRef(viewport);
            sc.Orientation = 0;
            sc.ItemCount = Items;
            sc.Layout = virt;
            sc.ContentNode = content;
            sc.FirstRealized = 0;
            sc.LastRealized = Items;
            sc.PersistentPrefixCount = 0;
        }

        return new Harness
        {
            Scene = scene,
            Layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable())),
            Virt = virt,
            Kernel = kernel,
            Root = root, Viewport = viewport, Content = content, Rows = rows,
        };
    }

    /// <summary>Seed every extent, then park the viewport mid-list so rows 0..2 sit above the anchor.</summary>
    private static Harness Parked()
    {
        var h = Build();
        h.Run();
        h.Reclamp(scrollTo: Offset);
        Assert.Equal(Offset, h.OffsetY, 3);
        Assert.Equal(-AnchorWithin, h.AnchorOnScreen, 3);
        return h;
    }

    // THE REGRESSION. A row whose measure GREW past its table slot must correct the table even when it is "fresh above
    // the anchor" — that combination is not exotic, it is every full root layout an overlay forces onto a list whose
    // previous arrange window has moved on. Deferring the write there leaves pass 2 arranging the row at its tall
    // measure while every following row is positioned from the short table entry, so the rows below paint over it.
    [Fact]
    public void AFreshRowAboveTheAnchorThatGrewCorrectsTheExtentTableInTheSamePass()
    {
        var h = Parked();
        h.StalePrevWindow(1, Items - 1);   // only row 0 reads as fresh
        h.SetRowHeight(0, ExpandedH);      // its drawer is open; the extra height exists ONLY as this measure
        h.Run();

        Assert.Equal(ExpandedH, h.TableExtent(0), 3);                  // the table took the correction…
        Assert.Equal(ExpandedH, h.Box(0).H, 3);                        // …and agrees with the arranged box
        Assert.Equal(ExpandedH, h.Box(1).Y, 3);                        // the next row starts AFTER the grown row
        Assert.Equal(ExpandedH + RowH, h.Box(2).Y, 3);                 // …and the ladder below stays contiguous
        Assert.Equal((Items - 1) * RowH + ExpandedH, h.ContentH, 3);   // the published extent is Σ rows
    }

    // The compensation that makes the write above safe: correcting a row ABOVE the anchor moves the anchor row down in
    // content space, and the re-pin posts exactly that delta as an AnchorShift — so once the kernel drains it, the
    // anchor row is back where it was on screen and the user sees no jump.
    [Fact]
    public void GrowingARowAboveTheAnchorDoesNotMoveTheAnchorOnScreen()
    {
        var h = Parked();
        h.StalePrevWindow(1, Items - 1);
        h.SetRowHeight(0, ExpandedH);
        h.Run();
        h.Reclamp();   // drain the AnchorShift + SetFrame this arrange posted

        Assert.Equal(Offset + (ExpandedH - RowH), h.OffsetY, 3);   // the offset rode the correction
        Assert.Equal(-AnchorWithin, h.AnchorOnScreen, 3);          // …so the visible top never moved
    }

    // The other half of the invariant: the transient the deferral exists for is still deferred. A fresh row above the
    // anchor that measured SHORTER than its slot keeps the slot (no table write ⇒ no offset re-pin), while pass 2 still
    // arranges it at what it actually measured, and the viewport asks for the follow-up arrange that commits it.
    [Fact]
    public void AFreshRowAboveTheAnchorThatShrankStillDefersItsCorrection()
    {
        var h = Parked();
        h.StalePrevWindow(1, Items - 1);
        h.SetRowHeight(0, 32f);            // transiently short — inner content lands next frame
        h.Run();

        Assert.Equal(RowH, h.TableExtent(0), 3);          // the slot is untouched…
        Assert.Equal(32f, h.Box(0).H, 3);                 // …while the child is arranged at its real measure
        Assert.Equal(RowH, h.Box(1).Y, 3);                // positions still come from the table
        Assert.Equal(Offset, h.OffsetY, 3);               // nothing re-pinned the offset
        Assert.True((h.Scene.Flags(h.Viewport) & NodeFlags.LayoutDirty) != 0,
            "a deferred correction must ask for the follow-up arrange that commits it");
    }

    // A fresh row BELOW the anchor was never deferred (a correction there cannot move the visible top); the grown-row
    // rule must not disturb that.
    [Fact]
    public void AFreshRowBelowTheAnchorCorrectsTheTableAsBefore()
    {
        var h = Parked();
        h.StalePrevWindow(0, 4);           // rows 5..19 read as fresh
        h.SetRowHeight(9, ExpandedH);
        h.Run();

        Assert.Equal(ExpandedH, h.TableExtent(9), 3);
        Assert.Equal(ExpandedH, h.Box(9).H, 3);
        Assert.Equal(9 * RowH, h.Box(9).Y, 3);
        Assert.Equal(9 * RowH + ExpandedH, h.Box(10).Y, 3);
    }
}
