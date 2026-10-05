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

    /// <summary>Random creates and frees against a model of the free set: every create that can reuse a slot takes the
    /// MINIMUM free index, a create with nothing free takes fresh capacity, and no index is ever live twice.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void RandomCreateAndFreeAlwaysReusesTheMinimumFreeIndex(int seed)
    {
        var rng = new Random(seed);
        var scene = new SceneStore(initialCapacity: 16);
        var live = new List<NodeHandle>();
        var liveIndices = new HashSet<int>();
        var free = new SortedSet<int>();
        int high = 0;
        for (int step = 0; step < 5000; step++)
        {
            if (live.Count == 0 || rng.Next(100) < 55)
            {
                var h = scene.CreateNode(1);
                int expected = free.Count > 0 ? free.Min : high + 1;
                Assert.Equal(expected, Index(h));
                if (free.Count > 0) free.Remove(expected); else high = expected;
                Assert.True(liveIndices.Add(Index(h)), $"index {Index(h)} handed out while live (step {step})");
                live.Add(h);
            }
            else
            {
                int at = rng.Next(live.Count);
                var h = live[at];
                live[at] = live[^1];
                live.RemoveAt(live.Count - 1);
                scene.FreeSubtree(h);
                liveIndices.Remove(Index(h));
                free.Add(Index(h));
            }
        }
        foreach (var h in live) Assert.True(scene.IsLive(h));
        Assert.Equal(live.Count, scene.LiveCount);
    }

    [Fact]
    public void ATrimStraddlingTheTargetKeepsTheLowFreeSlotsInOrder()
    {
        // Live: 1..20. Free: 5, 9, 13 (below the highest live index) and 21..999 (the tail), so the trim target sits
        // between the two groups of free slots.
        var scene = new SceneStore();
        var nodes = new NodeHandle[1000];
        for (int i = 1; i < nodes.Length; i++) nodes[i] = scene.CreateNode(1);
        for (int i = 21; i < nodes.Length; i++) scene.FreeSubtree(nodes[i]);
        foreach (int i in new[] { 13, 5, 9 }) scene.FreeSubtree(nodes[i]);

        int trimmed = scene.TrimExcessCapacity();
        Assert.True(trimmed > 0);

        // The free slots below the cut come back lowest first; then fresh capacity right after the highest live index.
        Assert.Equal(5, Index(scene.CreateNode(1)));
        Assert.Equal(9, Index(scene.CreateNode(1)));
        Assert.Equal(13, Index(scene.CreateNode(1)));
        Assert.Equal(21, Index(scene.CreateNode(1)));
        for (int i = 1; i <= 20; i++) if (i is not (5 or 9 or 13)) Assert.True(scene.IsLive(nodes[i]));
    }

    [Fact]
    public void GrowingWithFreeSlotsPendingKeepsThem()
    {
        var scene = new SceneStore(initialCapacity: 16);
        var nodes = new List<NodeHandle>();
        while (scene.Capacity == 16) nodes.Add(scene.CreateNode(1));   // fill to the first Grow
        int grownOnce = scene.Capacity;
        scene.FreeSubtree(nodes[3]);
        scene.FreeSubtree(nodes[1]);

        // Two creates reuse the pending slots, lowest first; then push past the next growth.
        var more = new List<NodeHandle> { scene.CreateNode(1), scene.CreateNode(1) };
        Assert.Equal(Index(nodes[1]), Index(more[0]));
        Assert.Equal(Index(nodes[3]), Index(more[1]));
        scene.FreeSubtree(more[1]);                                     // one free slot pending across the Grow
        while (scene.Capacity == grownOnce) more.Add(scene.CreateNode(1));
        Assert.Equal(Index(nodes[3]), Index(more[2]));                  // it was reused before any fresh slot
        var all = new HashSet<int>();
        foreach (var n in nodes) if (scene.IsLive(n)) Assert.True(all.Add(Index(n)));
        foreach (var n in more) if (scene.IsLive(n)) Assert.True(all.Add(Index(n)), $"index {Index(n)} handed out twice");
        Assert.Equal(scene.LiveCount, all.Count);
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
