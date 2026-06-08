using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>Where a flyout opens relative to its anchor.</summary>
public enum FlyoutPlacement : byte
{
    BottomLeft,      // below the anchor, left edges aligned (menus / DropDownButton)
    BottomStretch,   // below the anchor, left aligned and width-matched (ComboBox)
    TopLeft,         // above the anchor, left edges aligned
}

/// <summary>Which popup corners abut (and so should square against) the anchor edge — WinUI <c>UpdateCornerRadius</c>
/// corner-joining for ComboBox/AutoSuggestBox/DropDownButton, where the dropdown reads as one piece with the field.</summary>
[Flags]
public enum CornerJoin : byte { None = 0, Top = 1, Bottom = 2, Left = 4, Right = 8 }

/// <summary>Result of placing an anchored popup: the absolute top-left, which side it opened on (so the open animation
/// can reveal from the correct edge), the height that actually fits (after collision clamping), the resolved placement,
/// and the corner-join against the anchor.</summary>
public readonly record struct PopupPlacementResult(float X, float Y, bool OpensUp, float MeasuredH, FlyoutPlacement Placement, CornerJoin CornerJoin);

/// <summary>
/// Pure placement math for an anchored flyout: starts from the requested edge, flips vertically when there isn't room,
/// clamps to the larger side under a full collision, nudges horizontally into the viewport, and reports the side +
/// fitted height + corner-join so the host can drive the reveal animation and corner squaring.
/// </summary>
public static class FlyoutPositioner
{
    public static PopupPlacementResult Place(in RectF anchor, in Size2 popup, in Size2 viewport, FlyoutPlacement placement)
    {
        float vw = viewport.Width, vh = viewport.Height;
        float x = anchor.X;
        float belowY = anchor.Y + anchor.H;
        float aboveTop = anchor.Y;
        float popH = popup.Height;
        float roomBelow = MathF.Max(0f, vh - belowY);
        float roomAbove = MathF.Max(0f, aboveTop);

        bool fitsBelow = belowY + popH <= vh;
        bool fitsAbove = aboveTop - popH >= 0f;
        bool preferAbove = placement == FlyoutPlacement.TopLeft;

        bool opensUp;
        float y;
        float measuredH = popH;
        if (preferAbove)
        {
            if (fitsAbove) { opensUp = true; y = aboveTop - popH; }
            else if (fitsBelow) { opensUp = false; y = belowY; }
            else if (roomAbove >= roomBelow) { opensUp = true; measuredH = roomAbove; y = aboveTop - measuredH; }
            else { opensUp = false; measuredH = roomBelow; y = belowY; }
        }
        else
        {
            if (fitsBelow) { opensUp = false; y = belowY; }
            else if (fitsAbove) { opensUp = true; y = aboveTop - popH; }
            else if (roomBelow >= roomAbove) { opensUp = false; measuredH = roomBelow; y = belowY; }
            else { opensUp = true; measuredH = roomAbove; y = aboveTop - measuredH; }
        }

        // Horizontal nudge into the viewport.
        if (vw > 0f && x + popup.Width > vw) x = vw - popup.Width;
        if (x < 0f) x = 0f;
        if (y < 0f) y = 0f;

        // The popup edge that abuts the anchor squares its corners (the dropdown joins the field).
        CornerJoin join = opensUp ? CornerJoin.Bottom : CornerJoin.Top;
        return new PopupPlacementResult(x, y, opensUp, measuredH, placement, join);
    }
}
