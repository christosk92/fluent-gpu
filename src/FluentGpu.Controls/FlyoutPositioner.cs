using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>Where a flyout opens relative to its anchor.</summary>
public enum FlyoutPlacement : byte
{
    BottomLeft,      // below the anchor, left edges aligned (menus / DropDownButton)
    BottomStretch,   // below the anchor, left aligned and width-matched (ComboBox)
    TopLeft,         // above the anchor, left edges aligned
}

/// <summary>
/// Pure placement math for an anchored flyout: starts from the requested edge, flips vertically when there isn't room,
/// and nudges horizontally to stay inside the viewport. Returns the absolute top-left the popup should occupy.
/// </summary>
internal static class FlyoutPositioner
{
    public static (float X, float Y) Place(in RectF anchor, in Size2 popup, in Size2 viewport, FlyoutPlacement placement)
    {
        float vw = viewport.Width, vh = viewport.Height;
        float x = anchor.X;
        float below = anchor.Y + anchor.H;
        float above = anchor.Y - popup.Height;
        float y = placement == FlyoutPlacement.TopLeft ? above : below;

        // Vertical flip: if the preferred side overflows the viewport and the other side fits, flip.
        if (placement == FlyoutPlacement.TopLeft)
        {
            if (above < 0f && below + popup.Height <= vh) y = below;
        }
        else
        {
            if (below + popup.Height > vh && above >= 0f) y = above;
        }

        // Horizontal nudge into the viewport.
        if (vw > 0f && x + popup.Width > vw) x = vw - popup.Width;
        if (x < 0f) x = 0f;
        if (y < 0f) y = 0f;
        return (x, y);
    }
}
