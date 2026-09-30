using FluentGpu.Foundation;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Covers <see cref="SceneStore.CapacityFloor"/> and the <c>SceneStore(int initialCapacity)</c> constructor added for
/// the scroll-itch plan's item E (a host-set floor so the idle <see cref="SceneStore.TrimExcessCapacity"/> stops
/// re-growing the slab back onto the LOH every cold navigation — see docs/plans/wavee's scroll-itch investigation).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SceneStoreCapacityFloorTests
{
    /// <summary>Grows a store well past <paramref name="capacity"/>'s initial slab, then frees every node — leaving
    /// an all-free tail for <see cref="SceneStore.TrimExcessCapacity"/> to consider trimming.</summary>
    static SceneStore GrowThenEmpty(int initialCapacity, int nodeCount)
    {
        var scene = new SceneStore(initialCapacity);
        var nodes = new NodeHandle[nodeCount];
        for (int i = 0; i < nodeCount; i++) nodes[i] = scene.CreateNode(1);
        foreach (var node in nodes) scene.FreeSubtree(node);
        return scene;
    }

    [Fact]
    public void ParameterlessConstructorBehaviorIsUnchanged()
    {
        var byDefault = new SceneStore();
        var byExplicitDefault = new SceneStore(initialCapacity: 64);
        Assert.Equal(byExplicitDefault.Capacity, byDefault.Capacity);
        Assert.Equal(0, byDefault.CapacityFloor);
    }

    [Fact]
    public void TrimNeverGoesBelowAPositiveCapacityFloor()
    {
        const int floor = 4096;
        var scene = GrowThenEmpty(initialCapacity: 64, nodeCount: floor * 2);
        scene.CapacityFloor = floor;
        Assert.True(scene.Capacity > floor, $"fixture must have grown past the floor first (capacity={scene.Capacity})");

        int trimmed = scene.TrimExcessCapacity();

        Assert.True(scene.Capacity >= floor,
            $"trim must never cut below CapacityFloor={floor}, got Capacity={scene.Capacity} (trimmed={trimmed})");
    }

    [Fact]
    public void ZeroCapacityFloorKeepsTodaysTrimBehaviour()
    {
        // A store grown well past the built-in 256 floor, with CapacityFloor left at its default (0), must trim
        // exactly as it did before this field existed: an empty store's live span is 0, so the pow2-rounded target
        // collapses to the built-in floor (256) — CapacityFloor never widens that.
        var scene = GrowThenEmpty(initialCapacity: 64, nodeCount: 4096);
        Assert.Equal(0, scene.CapacityFloor);
        Assert.True(scene.Capacity > 256, $"fixture must have grown past the built-in floor first (capacity={scene.Capacity})");

        int trimmed = scene.TrimExcessCapacity();

        Assert.True(trimmed > 0);
        Assert.Equal(256, scene.Capacity);
    }
}
