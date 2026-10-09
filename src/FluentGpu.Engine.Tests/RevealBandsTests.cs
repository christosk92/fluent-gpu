using System;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The reveal-band slots inside ScrollState (docs/plans/smooth-reveal-implementation.md §10.1): plain named fields
/// with explicit equality, so the parity check's default ScrollState comparison works and sees every band field.</summary>
public sealed class RevealBandsTests
{
    [Fact]
    public void ScrollStateWithBandsComparesByValue()
    {
        var a = ScrollState.Default;
        a.Bands.Set(1, new RevealBand { First = 3, Count = 2, Top = 90f, Extent = 64f, Presented = 20f, Opening = true });
        a.BandMask = 0b10;
        var b = a;
        Assert.True(a.Equals(b));                       // never throws (an [InlineArray] field would)
        var band = b.Bands.Get(1);
        band.Presented = 21f;
        b.Bands.Set(1, in band);
        Assert.False(a.Equals(b));                      // a band field difference is a ScrollState difference
    }

    [Fact]
    public void ANaNPresentedEqualsItself()
    {
        var a = new RevealBands();
        a.Set(0, new RevealBand { Count = 1, Extent = 10f, Presented = float.NaN });
        var b = a;
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ACommittedBandStopsPresentingTheFlushItsRowsLeave()
    {
        var bands = new RevealBands();
        bands.Set(0, new RevealBand { First = 15, Count = 5, Top = 480f, Extent = 160f, Presented = 0f, Committed = true, CommitCount = 20 });
        bands.Set(1, new RevealBand { First = 2, Count = 1, Top = 64f, Extent = 32f, Presented = 10f, Opening = true });
        Assert.True(bands.Get(0).Presents(20));                  // rows still modelled: it keeps presenting them at 0
        Assert.False(bands.Get(0).Presents(15));                 // the commit's flush dropped them: no delta, no clip
        Assert.Equal((byte)0b11, bands.PresentingMask(0b11, 20));
        Assert.Equal((byte)0b10, bands.PresentingMask(0b11, 15));
        Assert.True(bands.Get(1).Presents(15));                  // an uncommitted band never stops presenting
    }

    [Fact]
    public void SlotsOutsideTheCapacityThrow()
    {
        var bands = new RevealBands();
        Assert.Throws<ArgumentOutOfRangeException>(() => bands.Get(RevealBands.Capacity));
        Assert.Throws<ArgumentOutOfRangeException>(() => bands.Set(-1, default));
    }
}
