using FluentGpu.Foundation;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A pen tap with the barrel held is Windows' pen right-click: it arrives as button 1 (the dispatcher's
/// context-request path, never an activation), and a pen contact's release keeps the button its press decided.</summary>
public sealed class PenBarrelButtonTests
{
    private const uint PtPen = 3;
    private const uint NoChange = 0, FirstDown = 1, FirstUp = 2, SecondDown = 3, SecondUp = 4;

    [Theory]
    [InlineData(SecondDown, 1)]
    [InlineData(SecondUp, 1)]
    [InlineData(FirstDown, 0)]
    [InlineData(FirstUp, 0)]
    [InlineData(NoChange, 0)]
    public void BarrelMapsToTheSecondaryButton(uint change, int expected)
        => Assert.Equal(expected, Win32Window.PointerButton(PointerKind.Pen, PtPen, change));

    [Fact]
    public void BarrelTapStaysSecondaryWhenTheBarrelIsReleasedFirst()
    {
        bool barrel = false;
        Assert.Equal(1, Win32Window.PenContactButton(true, Win32Window.PointerButton(PointerKind.Pen, PtPen, SecondDown), ref barrel));
        Assert.Equal(1, Win32Window.PenContactButton(false, Win32Window.PointerButton(PointerKind.Pen, PtPen, FirstUp), ref barrel));
        Assert.False(barrel);
    }

    [Fact]
    public void PlainTapStaysPrimaryWhenTheBarrelIsPressedMidStroke()
    {
        bool barrel = false;
        Assert.Equal(0, Win32Window.PenContactButton(true, Win32Window.PointerButton(PointerKind.Pen, PtPen, FirstDown), ref barrel));
        Assert.Equal(0, Win32Window.PenContactButton(false, Win32Window.PointerButton(PointerKind.Pen, PtPen, SecondUp), ref barrel));
        Assert.False(barrel);
    }
}
