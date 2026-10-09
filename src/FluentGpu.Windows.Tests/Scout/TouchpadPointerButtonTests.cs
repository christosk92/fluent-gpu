using FluentGpu.Foundation;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A precision-touchpad cursor (PT_MOUSE whose sourceDevice is a touchpad, which Decode tags
/// <see cref="PointerKind.Touchpad"/>) keeps its right/middle buttons and rides the mouse capture hardening.</summary>
public sealed class TouchpadPointerButtonTests
{
    private const uint PtTouch = 2, PtPen = 3, PtMouse = 4, PtTouchpad = 5;
    private const uint FirstDown = 1, SecondDown = 3, SecondUp = 4, ThirdDown = 5, ThirdUp = 6;

    [Theory]
    [InlineData(PointerKind.Touchpad, PtMouse, SecondDown, 1)]
    [InlineData(PointerKind.Touchpad, PtMouse, SecondUp, 1)]
    [InlineData(PointerKind.Touchpad, PtMouse, ThirdDown, 2)]
    [InlineData(PointerKind.Touchpad, PtMouse, ThirdUp, 2)]
    [InlineData(PointerKind.Touchpad, PtMouse, FirstDown, 0)]
    [InlineData(PointerKind.Mouse, PtMouse, SecondDown, 1)]
    [InlineData(PointerKind.Touchpad, PtTouchpad, FirstDown, 0)]
    [InlineData(PointerKind.Pen, PtPen, SecondDown, 0)]
    [InlineData(PointerKind.Touch, PtTouch, FirstDown, 0)]
    public void ButtonFollowsTheMouseStream(PointerKind kind, uint pointerType, uint change, int expected)
        => Assert.Equal(expected, Win32Window.PointerButton(kind, pointerType, change));

    [Fact]
    public void TouchpadCursorTakesThePrimaryContactHardening()
    {
        Assert.True(Win32Window.IsMouseButtonStream(PointerKind.Touchpad, PtMouse));
        Assert.True(Win32Window.IsMouseButtonStream(PointerKind.Mouse, PtMouse));
        Assert.False(Win32Window.IsMouseButtonStream(PointerKind.Touchpad, PtTouchpad));
        Assert.False(Win32Window.IsMouseButtonStream(PointerKind.Pen, PtPen));
        Assert.False(Win32Window.IsMouseButtonStream(PointerKind.Touch, PtTouch));
    }
}
