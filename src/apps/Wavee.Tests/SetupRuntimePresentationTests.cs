using Xunit;

namespace Wavee.Tests;

public class SetupRuntimePresentationTests
{
    [Theory]
    [InlineData(-1, 100, 0f)]
    [InlineData(0, 100, 0f)]
    [InlineData(50, 100, 0.5f)]
    [InlineData(100, 100, 1f)]
    [InlineData(150, 100, 1f)]
    [InlineData(50, 0, 0f)]
    public void ProgressFraction_ClampsLiveByteCounts(long received, long total, float expected)
        => Assert.Equal(expected, SetupRuntimePresentation.ProgressFraction(received, total));

    [Theory]
    [InlineData("9f31d02ac4a7", "9f31…c4a7")]
    [InlineData("12345678", "12345678")]
    [InlineData("", "")]
    public void ShortHash_PreservesUsefulEnds(string hash, string expected)
        => Assert.Equal(expected, SetupRuntimePresentation.ShortHash(hash));
}
