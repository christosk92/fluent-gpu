using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>Under mouse-in-pointer every physical button arrives over the caption as WM_NCPOINTERDOWN/UP. Only the
/// primary button may press and click an engine-drawn Min/Max/Close: a right, middle or side click used to become a
/// left click and close, minimize or maximize the window. Real HWND (never shown); the NC messages are delivered with
/// SendMessage so the window proc runs synchronously and the queue is read back through PumpInto.</summary>
public sealed unsafe class NcPointerButtonTests
{
    private const uint WM_NCPOINTERDOWN = 0x0242, WM_NCPOINTERUP = 0x0243;
    private const uint NcSyntheticPointerId = 0xFFFFFFFE;
    private const uint PtTouch = 2, PtPen = 3, PtMouse = 4;
    private const uint FirstDown = 1, FirstUp = 2, SecondDown = 3, SecondUp = 4, ThirdDown = 5, ThirdUp = 6,
                       FourthDown = 7, FourthUp = 8, FifthDown = 9, FifthUp = 10;

    [Theory]
    [InlineData(PointerKind.Mouse, PtMouse, FirstDown, true)]
    [InlineData(PointerKind.Mouse, PtMouse, FirstUp, true)]
    [InlineData(PointerKind.Mouse, PtMouse, SecondDown, false)]
    [InlineData(PointerKind.Mouse, PtMouse, SecondUp, false)]
    [InlineData(PointerKind.Mouse, PtMouse, ThirdDown, false)]
    [InlineData(PointerKind.Mouse, PtMouse, ThirdUp, false)]
    [InlineData(PointerKind.Mouse, PtMouse, FourthDown, false)]
    [InlineData(PointerKind.Mouse, PtMouse, FourthUp, false)]
    [InlineData(PointerKind.Mouse, PtMouse, FifthDown, false)]
    [InlineData(PointerKind.Mouse, PtMouse, FifthUp, false)]
    [InlineData(PointerKind.Touchpad, PtMouse, SecondDown, false)]
    [InlineData(PointerKind.Touch, PtTouch, FirstDown, true)]
    [InlineData(PointerKind.Pen, PtPen, FirstDown, true)]
    [InlineData(PointerKind.Pen, PtPen, SecondDown, false)]
    public void OnlyThePrimaryButtonDrivesACaptionButton(PointerKind kind, uint pointerType, uint change, bool expected)
        => Assert.Equal(expected, Win32Window.NcPrimaryChange(kind, pointerType, change));

    [Fact]
    public void NcPointerWithoutAPrimaryButton_OnClose_RunsNoEngineClick()
    {
        using var win = new Win32Window(new WindowDesc("nc-button", new Size2(400, 300), 1f, CustomFrame: true, SkipDropAndTouchpad: true));
        win.SetTitleBarRegions([
            new TitleBarRegion(new RectF(350, 0, 50, 32), TitleBarHit.CloseButton),
            new TitleBarRegion(new RectF(0, 0, 350, 32), TitleBarHit.Caption),
        ]);
        var ring = new InputEventRing();
        win.PumpInto(ring);
        ring.Clear();   // drop anything window creation queued

        HWND hwnd = (HWND)(nint)win.Handle.Value;
        POINT pt = new() { x = 375, y = 16 };   // the Close button's centre, client px (scale 1)
        ClientToScreen(hwnd, &pt);
        LPARAM lp = (LPARAM)(nint)(((pt.y & 0xFFFF) << 16) | (pt.x & 0xFFFF));
        // A pointer id with no live contact: GetPointerInfo cannot name the button, so no primary press is proven.
        WPARAM wp = (WPARAM)(nuint)((HTCLOSE << 16) | 0x7777);
        SendMessageW(hwnd, WM_NCPOINTERDOWN, wp, lp);
        SendMessageW(hwnd, WM_NCPOINTERUP, wp, lp);
        win.PumpInto(ring);

        int presses = 0;
        foreach (var e in ring.Drain())
            if (e.PointerId == NcSyntheticPointerId && e.Kind is InputKind.PointerDown or InputKind.PointerUp)
                presses++;
        Assert.Equal(0, presses);
    }
}
