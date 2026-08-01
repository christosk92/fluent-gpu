using Bench.Contracts;
using Xunit;

namespace Bench.Tests;

public sealed class FrameIdProbeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1_500)]
    [InlineData(16_383)]
    public void EncodedId_RoundTripsAcrossBenchmarkRange(int frameId)
    {
        FrameIdProbe.Encode(frameId, out byte r, out byte g, out byte b);

        Assert.InRange(r, (byte)16, (byte)143);
        Assert.InRange(g, (byte)16, (byte)143);
        Assert.InRange(b, (byte)16, (byte)31);
        Assert.True(FrameIdProbe.TryDecode(r, g, b, out int decoded));
        Assert.Equal(frameId, decoded);
    }

    [Fact]
    public void CorruptedParity_IsRejected()
    {
        FrameIdProbe.Encode(1_500, out byte r, out byte g, out byte b);

        Assert.False(FrameIdProbe.TryDecode(r, g, (byte)(16 + ((b - 16 + 1) & 0x0F)), out _));
    }
}
