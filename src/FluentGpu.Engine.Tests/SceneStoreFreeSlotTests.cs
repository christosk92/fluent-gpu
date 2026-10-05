using FluentGpu.Foundation;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Covers how <see cref="SceneStore"/> hands freed slots back: the lowest free index first (so the live set packs toward
/// the bottom of the slab and <see cref="SceneStore.TrimExcessCapacity"/> can return the tail), and a freed viewport's
/// index-keyed extent table going with it.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SceneStoreFreeSlotTests
{
    static int Index(NodeHandle h) => (int)h.Raw.Index;

    [Fact]
    public void CreateReusesTheLowestFreeIndexFirst()
    {
        var scene = new SceneStore();
        var nodes = new NodeHandle[10];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = scene.CreateNode(1);

        // Freed out of order, high first: a LIFO list would hand 3 back first only by accident of the order.
        scene.FreeSubtree(nodes[7]);
        scene.FreeSubtree(nodes[3]);
        scene.FreeSubtree(nodes[5]);

        Assert.Equal(Index(nodes[3]), Index(scene.CreateNode(1)));
        Assert.Equal(Index(nodes[5]), Index(scene.CreateNode(1)));
        Assert.Equal(Index(nodes[7]), Index(scene.CreateNode(1)));
        Assert.Equal(Index(nodes[9]) + 1, Index(scene.CreateNode(1)));   // the heap is empty: fresh capacity again
    }

    [Fact]
    public void ARecycledSlotIsAFreshHandle()
    {
        var scene = new SceneStore();
        var a = scene.CreateNode(1);
        scene.FreeSubtree(a);
        var b = scene.CreateNode(1);

        Assert.Equal(Index(a), Index(b));
        Assert.False(scene.IsLive(a));   // the generation moved on: the old handle is dead
        Assert.True(scene.IsLive(b));
    }

    [Fact]
    public void TheSlabShrinksBackAfterABigPageGoesAway()
    {
        // A session mounts a huge page (6,000 nodes), leaves it, then keeps living with a small tree that was rebuilt
        // after the page went away. The survivors must land at the bottom of the slab so the idle trim can cut the rest.
        var scene = new SceneStore();
        var big = new NodeHandle[6000];
        for (int i = 0; i < big.Length; i++) big[i] = scene.CreateNode(1);
        int grown = scene.Capacity;
        for (int i = 0; i < big.Length; i++) scene.FreeSubtree(big[i]);

        var survivors = new NodeHandle[200];
        for (int i = 0; i < survivors.Length; i++) survivors[i] = scene.CreateNode(1);
        foreach (var s in survivors) Assert.True(Index(s) <= survivors.Length, $"survivor at index {Index(s)} did not pack low");

        int trimmed = scene.TrimExcessCapacity();

        Assert.True(trimmed > 0);
        Assert.True(scene.Capacity < grown, $"capacity {scene.Capacity} did not shrink from {grown}");
        foreach (var s in survivors) Assert.True(scene.IsLive(s));
        // Allocation still works after the trim, and still prefers the bottom of the slab.
        var next = scene.CreateNode(1);
        Assert.Equal(survivors.Length + 1, Index(next));
    }

    [Fact]
    public void FreeingAViewportDropsItsExtentTable()
    {
        var scene = new SceneStore();
        var viewport = scene.CreateNode(1);
        var extents = scene.ExtentTableFor(viewport, itemCount: 50, estimate: 48f);
        Assert.True(scene.TryGetExtents(viewport, out var found));
        Assert.Same(extents, found);

        scene.FreeSubtree(viewport);
        var reuse = scene.CreateNode(1);   // the lowest free index: the viewport's own slot

        Assert.Equal(Index(viewport), Index(reuse));
        Assert.False(scene.TryGetExtents(reuse, out _));   // the next owner of the slot starts from its own estimates
    }
}
