using System.Collections.Generic;
using Wavee.Backend.Residency;
using Xunit;

namespace Wavee.Tests;

// The cross-arena shedding coordinator: arenas shed in a FIXED priority, escalating with pressure, so pinned working sets
// survive. The concrete arenas (engine ImageCache, Store eviction) plug in at the app level; the ordering is the governor's.
public class MemoryGovernorTests
{
    [Fact]
    public void Trim_ShedsArenas_InPriorityOrder_UpToPressureLevel()
    {
        var order = new List<string>();
        var gov = new MemoryGovernor();
        gov.Register(1, "prefetch-art", () => { order.Add("art"); return 10; });
        gov.Register(3, "drop-entities", () => { order.Add("entities"); return 30; });   // registered out of order
        gov.Register(2, "warm-demote", () => { order.Add("warm"); return 20; });

        long freed = gov.Trim(MemoryPressure.Moderate);                 // priority <= 2
        Assert.Equal(new[] { "art", "warm" }, order);                  // entities NOT shed at Moderate; order is by priority
        Assert.Equal(30, freed);

        order.Clear();
        freed = gov.Trim(MemoryPressure.Critical);                      // all priorities
        Assert.Equal(new[] { "art", "warm", "entities" }, order);
        Assert.Equal(60, freed);
    }

    [Fact]
    public void Trim_Normal_OnlyShedsTheCheapestArena()
    {
        var order = new List<string>();
        var gov = new MemoryGovernor();
        gov.Register(1, "prefetch-art", () => { order.Add("art"); return 5; });
        gov.Register(2, "warm-demote", () => { order.Add("warm"); return 5; });

        gov.Trim(MemoryPressure.Normal);                               // routine self-trim — priority 1 only
        Assert.Equal(new[] { "art" }, order);
    }
}
