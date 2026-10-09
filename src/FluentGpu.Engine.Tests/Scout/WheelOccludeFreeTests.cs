using FluentGpu.Foundation;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// Element.BlocksBackgroundScroll is an index-keyed side-table that only a BoxEl rewrites. A freed modal surface must not
/// hand its occlusion to the next node (a text, an image, a scroller) that reuses its slot, or wheel routing over
/// that node silently drops the scroller found beneath it.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class WheelOccludeFreeTests
{
    [Fact]
    public void AFreedOccludingSlotIsNotInheritedByItsReuser()
    {
        var scene = new SceneStore();
        var modal = scene.CreateNode(1);
        scene.SetBlocksBackgroundScroll(modal, true);
        Assert.True(scene.GetBlocksBackgroundScroll(modal));

        scene.FreeSubtree(modal);
        var reuser = scene.CreateNode(2);   // a non-box element: WriteColumns never rewrites the flag for it

        Assert.Equal(modal.Raw.Index, reuser.Raw.Index);
        Assert.False(scene.GetBlocksBackgroundScroll(reuser));
    }
}
