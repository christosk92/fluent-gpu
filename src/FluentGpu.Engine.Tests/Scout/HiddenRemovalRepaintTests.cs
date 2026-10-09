using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A node mounted inside a collapsed (<c>Visible=false</c>) or KeepAlive-parked subtree is never laid out or walked: it
/// keeps its 0x0 box and never stores a span. Freeing it later (a hidden section's data lands, a row is remounted while
/// its page is parked) left the removal ledger with no prior extent and an empty model rect, and the recorder forced a
/// full repaint (MissingRemovalExtent): every retained tile of the visible page re-rastered for a node that never painted
/// a pixel. A hidden removal vacates nothing; a visible one whose extent is unknown still forces full.
/// </summary>
public sealed class HiddenRemovalRepaintTests
{
    private static RepaintFullReason FreeRowMountedUnder(string mode, bool withSpans)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.Root = scene.CreateNode(1);
        scene.Bounds(root) = new RectF(0, 0, 400, 300);
        var page = scene.CreateNode(2);
        scene.AppendChild(root, page);
        scene.Bounds(page) = new RectF(0, 0, 200, 100);
        var spans = withSpans ? new SpanTable() : null;
        var slices = new SliceRecorder();
        RepaintFullReason Record() => SceneRecorder.Record(scene, new DrawList(), spans: spans, slices: slices).RepaintDamage.FullReason;
        Record();
        Record();

        if (mode == "collapsed") scene.SetCollapsed(page, true);
        else if (mode == "parked") { scene.Mark(page, NodeFlags.Parked); scene.Detach(page); }
        Record();
        Record();

        // mounted while hidden: no layout reaches it (0x0 box) and no walk stores its span
        var row = scene.CreateNode(2);
        scene.AppendChild(page, row);
        if (mode == "parked") scene.Mark(row, NodeFlags.Parked);   // the reconciler's mount-under-parked inheritance
        Record();

        scene.FreeSubtree(row);
        return Record();
    }

    [Theory]
    [InlineData("collapsed")]
    [InlineData("parked")]
    public void FreeingANodeThatNeverPresentedInsideAHiddenSubtree_DoesNotForceAFullRepaint(string mode)
        => Assert.Equal(RepaintFullReason.None, FreeRowMountedUnder(mode, withSpans: true));

    [Fact]
    public void AVisibleRemovalWithNoKnownExtent_StillForcesFull()
        // no span table and a 0x0 box: the vacated band of a visible node is genuinely unknown
        => Assert.Equal(RepaintFullReason.MissingRemovalExtent, FreeRowMountedUnder("visible", withSpans: false));
}
