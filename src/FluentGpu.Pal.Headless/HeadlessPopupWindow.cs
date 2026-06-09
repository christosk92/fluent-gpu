using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Pal.Headless;

/// <summary>
/// Recorder implementation of <see cref="IPlatformPopupWindow"/> — the headless analogue of the Win32 windowed-popup
/// HWND (WinUI windowed <c>CPopup</c>, Popup_Partial.cpp:1019). Records every bounds/show/hide transition so checks can
/// assert virtual-screen placement of out-of-bounds popups (multi-monitor clamping, owner-relative position) without
/// pixels. The host still creates a real (headless) swapchain on <see cref="Handle"/> and records the popup subtree
/// into its own DrawList — the complete render path minus the D3D12 present (needs-pixels).
/// </summary>
public sealed class HeadlessPopupWindow : IPlatformPopupWindow
{
    private readonly List<RectF> _boundsHistory = new(4);

    public HeadlessPopupWindow(in PopupWindowDesc desc)
    {
        Owner = desc.Owner;
        BoundsPx = desc.BoundsPx;
        if (!desc.BoundsPx.IsEmpty) _boundsHistory.Add(desc.BoundsPx);
    }

    public NativeHandle Owner { get; }
    public NativeHandle Handle => new(0, NativeHandleKind.Headless);
    public RectF BoundsPx { get; private set; }
    public bool IsShown { get; private set; }
    public bool Disposed { get; private set; }
    public int ShowCount { get; private set; }
    public int HideCount { get; private set; }

    /// <summary>Every rect passed to <see cref="SetBoundsPx"/> (oldest first) — placement-tracking assertions.</summary>
    public IReadOnlyList<RectF> BoundsHistory => _boundsHistory;

    public void SetBoundsPx(in RectF px)
    {
        BoundsPx = px;
        _boundsHistory.Add(px);
    }

    public void Show()
    {
        IsShown = true;
        ShowCount++;
    }

    public void Hide()
    {
        IsShown = false;
        HideCount++;
    }

    public void Dispose()
    {
        IsShown = false;
        Disposed = true;
    }
}
