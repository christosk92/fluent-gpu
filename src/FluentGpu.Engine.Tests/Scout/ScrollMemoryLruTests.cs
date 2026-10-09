using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>ScrollPositionMemory evicted by FIRST insertion: a key saved again kept its original queue place, so a page the
/// user kept returning to was dropped as soon as Capacity other keys had arrived since its first visit, even right after
/// its latest save. Forget left a stale queue copy that later deleted the key's re-saved entry. Eviction now takes
/// the least recently saved key.</summary>
public sealed class ScrollMemoryLruTests
{
    [Fact]
    public void AReSavedKeySurvivesTheNextEviction()
    {
        var memory = new ScrollPositionMemory();
        memory.Save("browse", 100.0);
        for (int i = 0; i < ScrollPositionMemory.Capacity - 1; i++) memory.Save("visit:" + i, i);
        Assert.Equal(ScrollPositionMemory.Capacity, memory.Count);

        memory.Save("browse", 120.0);    // the user came back and left again
        memory.Save("visit:new", 5.0);   // one more key: evicts the least recently saved, visit:0

        Assert.True(memory.TryGet("browse", out double browse));
        Assert.Equal(120.0, browse);
        Assert.False(memory.TryGet("visit:0", out _));
        Assert.True(memory.TryGet("visit:new", out _));
        Assert.Equal(ScrollPositionMemory.Capacity, memory.Count);
    }

    [Fact]
    public void AForgottenKeySavedAgainIsNotEvictedByItsStaleCopy()
    {
        var memory = new ScrollPositionMemory();
        memory.Save("page", 1.0);
        memory.Forget("page");
        memory.Save("page", 2.0);
        for (int i = 0; i < ScrollPositionMemory.Capacity - 1; i++) memory.Save("other:" + i, i);

        Assert.True(memory.TryGet("page", out double page));
        Assert.Equal(2.0, page);
        Assert.Equal(ScrollPositionMemory.Capacity, memory.Count);
    }
}
