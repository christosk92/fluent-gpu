using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The <c>--fg test-input</c> wire format (<see cref="Win32TestInput"/>) round-trips kind, signed client px and the wheel notch.</summary>
public sealed class Win32TestInputTests
{
    [Theory]
    [InlineData((ushort)Win32TestInput.Kind.Move, 10, 20, 0)]
    [InlineData((ushort)Win32TestInput.Kind.Down, 1919, 1079, 0)]
    [InlineData((ushort)Win32TestInput.Kind.RightUp, -5, -7, 0)]
    [InlineData((ushort)Win32TestInput.Kind.Wheel, 640, 360, -120)]
    [InlineData((ushort)Win32TestInput.Kind.Wheel, 640, 360, 240)]
    public void RoundTrips(ushort rawKind, int x, int y, short notch)
    {
        var kind = (Win32TestInput.Kind)rawKind;
        Win32TestInput.Decode(Win32TestInput.EncodeW(kind, notch), Win32TestInput.EncodeL(x, y), out var k, out int dx, out int dy, out short dn);
        Assert.Equal(kind, k);
        Assert.Equal(x, dx);
        Assert.Equal(y, dy);
        Assert.Equal(notch, dn);
    }
}
