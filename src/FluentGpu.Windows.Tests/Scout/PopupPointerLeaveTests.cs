using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using TerraFX.Interop.Windows;
using Xunit;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A windowed (out-of-window) popup forwards its <c>WM_POINTERLEAVE</c> to the owner, which parks the pointer
/// offscreen exactly like its own leave. Without it, leaving the popup onto the desktop sent the owner nothing and the
/// last hovered menu item stayed latched. Real HWNDs (the owner is never shown). The leave is delivered with SendMessage
/// on this thread, so the popup's WndProc runs synchronously and the owner's queue is read back through PumpInto.</summary>
public sealed unsafe class PopupPointerLeaveTests
{
    private const uint WM_POINTERLEAVE = 0x024A;

    [Fact]
    public void PopupLeave_ParksThePointerOffscreen_OnTheOwner()
    {
        using var owner = new Win32Window(new WindowDesc("popup-leave", new Size2(320, 240), 1f, SkipDropAndTouchpad: true));
        var ring = new InputEventRing();
        owner.PumpInto(ring);
        ring.Clear();   // drop anything window creation queued

        using var popup = new Win32PopupWindow(new PopupWindowDesc(owner.Handle, new RectF(100, 100, 200, 150)));
        const uint mouseId = 1;
        SendMessageW((HWND)popup.Handle.Value, WM_POINTERLEAVE, (WPARAM)(nuint)mouseId, (LPARAM)0);
        owner.PumpInto(ring);

        int parks = 0;
        foreach (var e in ring.Drain())
            if (e.Kind == InputKind.PointerMove && e.PointerId == mouseId
                && e.PositionPx.X == -10000f && e.PositionPx.Y == -10000f)
                parks++;
        Assert.Equal(1, parks);
    }
}
