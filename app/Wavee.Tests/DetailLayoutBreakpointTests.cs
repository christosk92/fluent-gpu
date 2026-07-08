using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class DetailLayoutBreakpointTests
{
    [Fact]
    public void TierFor_Oscillates860PlusMinus24_HoldsTier1Until884()
    {
        int tier = DetailLayoutBreakpoints.TierFor(850f, 1);
        Assert.Equal(1, tier);

        tier = DetailLayoutBreakpoints.TierFor(836f, tier);
        Assert.Equal(1, tier);   // 836 nominal tier 1 — hold while prev is 1

        tier = DetailLayoutBreakpoints.TierFor(884f, tier);
        Assert.Equal(0, tier);   // widen back only after w - 24 crosses 860

        tier = DetailLayoutBreakpoints.TierFor(835f, tier);
        Assert.Equal(1, tier);   // narrow from tier 0 drops immediately at 860 boundary
    }

    [Fact]
    public void TierFor_MultiTierJump_WidensImmediately()
    {
        int tier = DetailLayoutBreakpoints.TierFor(500f, 5);
        Assert.Equal(3, tier);

        tier = DetailLayoutBreakpoints.TierFor(900f, tier);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void ModeFor_Oscillates820PlusMinus24_HoldsMidUntil844()
    {
        int mode = DetailLayoutBreakpoints.ModeFor(810f, 1, initialized: true);
        Assert.Equal(1, mode);

        mode = DetailLayoutBreakpoints.ModeFor(796f, mode, initialized: true);
        Assert.Equal(1, mode);   // still nominal mid while prev is 1

        mode = DetailLayoutBreakpoints.ModeFor(844f, mode, initialized: true);
        Assert.Equal(0, mode);   // widen to wide only after w - 24 crosses 820

        mode = DetailLayoutBreakpoints.ModeFor(795f, 0, initialized: true);
        Assert.Equal(1, mode);   // narrow from wide drops at 820 boundary
    }

    [Fact]
    public void ModeFor_VerticalBand540580_Unchanged()
    {
        int mode = DetailLayoutBreakpoints.ModeFor(600f, 2, initialized: true);
        Assert.Equal(2, mode);

        mode = DetailLayoutBreakpoints.ModeFor(530f, mode, initialized: true);
        Assert.Equal(DetailLayoutBreakpoints.VerticalMode, mode);

        mode = DetailLayoutBreakpoints.ModeFor(570f, mode, initialized: true);
        Assert.Equal(DetailLayoutBreakpoints.VerticalMode, mode);

        mode = DetailLayoutBreakpoints.ModeFor(590f, mode, initialized: true);
        Assert.Equal(2, mode);
    }
}
