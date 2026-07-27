using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

public sealed class MinTrackSizingTests
{
    [Theory]
    [InlineData(360f, 96u, 360)]
    [InlineData(360f, 144u, 540)]
    [InlineData(360f, 192u, 720)]
    [InlineData(360.1f, 96u, 361)]
    public void DipToPx_UsesLiveDpiAndRoundsUp(float dip, uint dpi, int expected)
        => Assert.Equal(expected, MinTrackSizing.DipToPx(dip, dpi));

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void DipToPx_EmptyAxisStaysUnspecified(float dip)
        => Assert.Equal(0, MinTrackSizing.DipToPx(dip, 192u));
}
