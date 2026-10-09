using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A windowed (out-of-window) popup answers <c>WM_SETCURSOR</c> over its client with the owner's engine cursor.
/// Without it, DefWindowProc applied the popup class arrow on every move and the I-beam/hand the dispatcher published once
/// (on the hover change) was gone after one move. Real HWNDs (the owner is never shown); SendMessage runs the popup's
/// WndProc synchronously on this thread, whose input queue owns the cursor GetCursor reads back.</summary>
public sealed unsafe class PopupSetCursorTests
{
    private const uint WM_SETCURSOR = 0x0020, WM_MOUSEMOVE = 0x0200;
    private const int HTCLIENT = 1, IDC_IBEAM = 32513;

    [Fact]
    public void PopupSetCursor_KeepsTheOwnersEngineCursor()
    {
        using var owner = new Win32Window(new WindowDesc("popup-cursor", new Size2(320, 240), 1f, SkipDropAndTouchpad: true));
        using var popup = new Win32PopupWindow(new PopupWindowDesc(owner.Handle, new RectF(100, 100, 200, 150)));
        HCURSOR ibeam = LoadCursorW(default, (char*)IDC_IBEAM);

        owner.SetCursor(CursorId.IBeam);   // what PublishCursor does once when the hover lands on a text field
        Assert.Equal(ibeam, GetCursor());

        // The next mouse move over the popup: the OS sends it WM_SETCURSOR(HTCLIENT, WM_MOUSEMOVE).
        var hp = (HWND)popup.Handle.Value;
        SendMessageW(hp, WM_SETCURSOR, (WPARAM)(nuint)(nint)hp, (LPARAM)(nint)(HTCLIENT | ((int)WM_MOUSEMOVE << 16)));

        Assert.Equal(ibeam, GetCursor());
    }
}
