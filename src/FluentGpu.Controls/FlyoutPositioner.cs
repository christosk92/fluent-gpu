using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// Where a flyout opens relative to its anchor — the full WinUI <c>FlyoutPlacementMode</c> matrix
/// (FlyoutBase_Partial.cpp:84-110 maps each mode to a major side + an edge justification) plus the engine's
/// pre-existing aliases kept for source compatibility:
/// <list type="bullet">
/// <item><see cref="BottomLeft"/>/<see cref="TopLeft"/> ≡ WinUI <c>BottomEdgeAlignedLeft</c>/<c>TopEdgeAlignedLeft</c>.</item>
/// <item><see cref="BottomCenter"/>/<see cref="TopCenter"/> ≡ WinUI <c>Bottom</c>/<c>Top</c> (center-justified).</item>
/// <item><see cref="BottomStretch"/> = the attached ComboBox/AutoSuggest dropdown (flush, width-matched, corner-joined)
///   — not a FlyoutBase mode; WinUI ComboBox places its own popup over/under the field.</item>
/// </list>
/// </summary>
public enum FlyoutPlacement : byte
{
    BottomLeft,      // below the anchor, left edges aligned (menus / DropDownButton) ≡ BottomEdgeAlignedLeft
    BottomStretch,   // below the anchor, left aligned and width-matched (ComboBox); flush, corner-joined
    BottomCenter,    // below the anchor, centered on the anchor ≡ WinUI Bottom
    TopLeft,         // above the anchor, left edges aligned ≡ TopEdgeAlignedLeft
    TopCenter,       // above the anchor, centered on the anchor ≡ WinUI Top

    // The remaining WinUI FlyoutPlacementMode members (FlyoutBase_Partial.cpp:84-110).
    Top,                      // above, centered            (WinUI default placement, FlyoutBase_Partial.cpp:62)
    Bottom,                   // below, centered
    Left,                     // left side, vertically centered
    Right,                    // right side, vertically centered
    TopEdgeAlignedLeft,       // above, left edges aligned
    TopEdgeAlignedRight,      // above, right edges aligned
    BottomEdgeAlignedLeft,    // below, left edges aligned
    BottomEdgeAlignedRight,   // below, right edges aligned
    LeftEdgeAlignedTop,       // left side, top edges aligned
    LeftEdgeAlignedBottom,    // left side, bottom edges aligned
    RightEdgeAlignedTop,      // right side, top edges aligned
    RightEdgeAlignedBottom,   // right side, bottom edges aligned
    Full,                     // centered in the container, sized to it (FlyoutBase_Partial.cpp:2520-2540)
}

/// <summary>Which popup corners abut (and so should square against) the anchor edge — WinUI <c>UpdateCornerRadius</c>
/// corner-joining for ComboBox/AutoSuggestBox/DropDownButton, where the dropdown reads as one piece with the field.</summary>
[Flags]
public enum CornerJoin : byte { None = 0, Top = 1, Bottom = 2, Left = 4, Right = 8 }

/// <summary>Result of placing an anchored popup: the absolute top-left, which side it opened on (so the open animation
/// can reveal from the correct edge), the width/height that actually fit (after a full collision resize), the EFFECTIVE
/// placement after fallback (WinUI <c>PerformPlacementWithFallback</c> reports the placement that won), and the
/// corner-join against the anchor.</summary>
public readonly record struct PopupPlacementResult(
    float X, float Y, bool OpensUp, float MeasuredH, FlyoutPlacement Placement, CornerJoin CornerJoin, float MeasuredW);

/// <summary>
/// Pure placement math for an anchored flyout — a faithful port of WinUI's <c>FlyoutBase::CalculatePlacementPrivate</c>
/// (FlyoutBase_Partial.cpp:2503-2659): try the requested major side, walk the per-side fallback order
/// (Top:[T,B,L,R] / Bottom:[B,T,L,R] / Left:[L,R,T,B] / Right:[R,L,T,B], cpp:2559-2593), justify on the secondary axis
/// and clamp both axes into the container (<c>TryPlacement</c>, cpp:415-483); when NOTHING fits, keep the first choice
/// and resize along the major axis to the side with more space (<c>ResizeToFit</c>/<c>SelectSideWithMoreSpace</c>,
/// cpp:2605-2614, 581-635); finally shift by <see cref="FlyoutMargin"/> away from the anchor (cpp:2622-2646) and, for
/// non-windowed popups only, clamp the min edge to the container origin (cpp:2648-2653 — windowed popups may go
/// negative, i.e. above/left of the window).
/// </summary>
public static class FlyoutPositioner
{
    /// <summary>Margin between the flyout and its placement target / the window edge — WinUI
    /// <c>FlyoutBase::FlyoutMargin = 4</c> (FlyoutBase_Partial.cpp:65). Attached/stretch dropdowns stay flush (0).</summary>
    public const float FlyoutMargin = 4f;

    private enum Major : byte { Top, Bottom, Left, Right, Full }
    private enum Justify : byte { Center, Left, Right, Top, Bottom }

    /// <summary>Viewport-constrained placement (the container is the window's DIP rect at origin).</summary>
    public static PopupPlacementResult Place(in RectF anchor, in Size2 popup, in Size2 viewport, FlyoutPlacement placement)
        => Place(in anchor, in popup, new RectF(0f, 0f, viewport.Width, viewport.Height), placement, isWindowed: false);

    /// <summary>
    /// Container-rect placement. <paramref name="container"/> is the collision box in the SAME coordinate space as
    /// <paramref name="anchor"/> — the viewport rect for constrained popups, the monitor WORK AREA (translated into
    /// window space) for windowed out-of-bounds popups (FlyoutBase_Partial.cpp:3382-3392 <c>useMonitorBounds</c>).
    /// <paramref name="isWindowed"/> skips the final min-edge clamp (cpp:2648-2653) so a windowed popup may extend
    /// above/left of the window.
    /// </summary>
    public static PopupPlacementResult Place(in RectF anchor, in Size2 popup, in RectF container, FlyoutPlacement placement, bool isWindowed)
    {
        // ── attached stretch dropdown (ComboBox/AutoSuggest): flush, flip, clamp to the larger side, corner-join ──
        if (placement == FlyoutPlacement.BottomStretch)
            return PlaceAttached(in anchor, in popup, in container);

        // ── Full: the presenter fills the container, centered when smaller (FlyoutBase_Partial.cpp:2520-2540) ──
        if (placement == FlyoutPlacement.Full)
        {
            float fw = MathF.Min(popup.Width, container.W);
            float fh = MathF.Min(popup.Height, container.H);
            return new PopupPlacementResult(
                container.X + (container.W - fw) * 0.5f, container.Y + (container.H - fh) * 0.5f,
                OpensUp: false, fh, FlyoutPlacement.Full, CornerJoin.None, fw);
        }

        Major major = MajorOf(placement);
        Justify justify = JustifyOf(placement);

        // Per-side fallback order (FlyoutBase_Partial.cpp:2559-2593).
        Span<Major> order = stackalloc Major[4];
        switch (major)
        {
            case Major.Top: order[0] = Major.Top; order[1] = Major.Bottom; order[2] = Major.Left; order[3] = Major.Right; break;
            case Major.Bottom: order[0] = Major.Bottom; order[1] = Major.Top; order[2] = Major.Left; order[3] = Major.Right; break;
            case Major.Left: order[0] = Major.Left; order[1] = Major.Right; order[2] = Major.Top; order[3] = Major.Bottom; break;
            default: order[0] = Major.Right; order[1] = Major.Left; order[2] = Major.Top; order[3] = Major.Bottom; break;
        }

        // PerformPlacementWithFallback (cpp:488-537): first fit wins; none fits → first choice's clamped position.
        float x = 0f, y = 0f;
        Major effective = order[0];
        bool fitted = false;
        float firstX = 0f, firstY = 0f;
        for (int i = 0; i < order.Length; i++)
        {
            bool fits = TryPlacement(in anchor, in popup, in container, order[i], justify, out float cx, out float cy);
            if (i == 0) { firstX = cx; firstY = cy; }
            if (fits)
            {
                effective = order[i];
                x = cx; y = cy;
                fitted = true;
                break;
            }
        }

        float measuredW = popup.Width, measuredH = popup.Height;
        if (!fitted)
        {
            // ResizeToFit on the first choice (cpp:2605-2614): pick the side of the major AXIS with more room
            // (SelectSideWithMoreSpace, cpp:581-635) and shrink the popup's major extent to that available space.
            effective = order[0];
            x = firstX; y = firstY;
            if (effective is Major.Top or Major.Bottom)
            {
                float above = MathF.Max(0f, MathF.Min(container.H, anchor.Y - container.Y));
                float below = MathF.Max(0f, MathF.Min(container.H, container.Bottom - anchor.Bottom));
                if (above > below) { effective = Major.Top; measuredH = above; }
                else { effective = Major.Bottom; measuredH = below; }
                measuredH = MathF.Min(measuredH, popup.Height);
                y = effective == Major.Top ? anchor.Y - measuredH : anchor.Bottom;
                TestAndJustify(anchor.X, anchor.W, measuredW, container.X, container.Right, justify, out x);
            }
            else
            {
                float left = MathF.Max(0f, MathF.Min(container.W, anchor.X - container.X));
                float right = MathF.Max(0f, MathF.Min(container.W, container.Right - anchor.Right));
                if (left > right) { effective = Major.Left; measuredW = left; }
                else { effective = Major.Right; measuredW = right; }
                measuredW = MathF.Min(measuredW, popup.Width);
                x = effective == Major.Left ? anchor.X - measuredW : anchor.Right;
                TestAndJustify(anchor.Y, anchor.H, measuredH, container.Y, container.Bottom, VerticalJustify(justify), out y);
            }
        }

        // FlyoutMargin shift away from the anchor, in the major direction only (cpp:2622-2646).
        switch (effective)
        {
            case Major.Top: y -= FlyoutMargin; break;
            case Major.Bottom: y += FlyoutMargin; break;
            case Major.Left: x -= FlyoutMargin; break;
            case Major.Right: x += FlyoutMargin; break;
        }

        // Non-windowed popups clamp the MIN edge only (cpp:2648-2653); windowed popups may go negative.
        if (!isWindowed)
        {
            x = MathF.Max(container.X, x);
            y = MathF.Max(container.Y, y);
        }

        // Free flyouts/menus keep all corners rounded — only attached dropdowns corner-join (BottomStretch path above).
        return new PopupPlacementResult(x, y, effective == Major.Top, measuredH, Compose(effective, justify), CornerJoin.None, measuredW);
    }

    /// <summary>WinUI <c>TryPlacement</c> (cpp:415-483): place on the major side and clamp into the container
    /// (<c>TestAgainstLimitsAndPlace</c>, cpp:367-411 — fits iff the extra space ≥ 0; position clamps either way),
    /// justify + clamp the secondary axis (<c>TestAndCenterAlignWithinLimits</c>). Fit requires BOTH axes.</summary>
    private static bool TryPlacement(in RectF anchor, in Size2 popup, in RectF container, Major major, Justify justify,
                                     out float x, out float y)
    {
        bool fits;
        if (major is Major.Top or Major.Bottom)
        {
            fits = TestAgainstLimitsAndPlace(anchor.Y, anchor.H, increasing: major == Major.Bottom, popup.Height,
                container.Y, container.Bottom, out y);
            fits &= TestAndJustify(anchor.X, anchor.W, popup.Width, container.X, container.Right, justify, out x);
        }
        else
        {
            fits = TestAgainstLimitsAndPlace(anchor.X, anchor.W, increasing: major == Major.Right, popup.Width,
                container.X, container.Right, out x);
            fits &= TestAndJustify(anchor.Y, anchor.H, popup.Height, container.Y, container.Bottom, VerticalJustify(justify), out y);
        }
        return fits;
    }

    // WinUI TestAgainstLimitsAndPlace (FlyoutBase_Partial.cpp:367-411): flush against the anchor edge on the major
    // axis, fits = remaining space ≥ 0, clamp the position into [low, high - size] either way.
    private static bool TestAgainstLimitsAndPlace(float anchorPos, float anchorSize, bool increasing, float controlSize,
                                                  float lowLimit, float highLimit, out float pos)
    {
        bool fits = true;
        float extra;
        if (increasing)
        {
            extra = highLimit - (anchorPos + anchorSize) - controlSize;
            pos = anchorPos + anchorSize;
        }
        else
        {
            extra = anchorPos - lowLimit - controlSize;
            pos = anchorPos - controlSize;
        }
        if (extra < 0f) fits = false;
        if (pos < lowLimit) pos = lowLimit;
        else if (pos + controlSize > highLimit) pos = highLimit - controlSize;
        return fits;
    }

    // WinUI TestAndCenterAlignWithinLimits: justify on the secondary axis (Center on the anchor, or align the
    // matching edges), clamp into the container; fits iff the control fits inside the container extent.
    private static bool TestAndJustify(float anchorPos, float anchorSize, float controlSize,
                                       float lowLimit, float highLimit, Justify justify, out float pos)
    {
        pos = justify switch
        {
            Justify.Left or Justify.Top => anchorPos,                              // matching-edge alignment
            Justify.Right or Justify.Bottom => anchorPos + anchorSize - controlSize,
            _ => anchorPos + (anchorSize - controlSize) * 0.5f,                    // centered on the anchor
        };
        bool fits = controlSize <= highLimit - lowLimit;
        if (pos < lowLimit) pos = lowLimit;
        else if (pos + controlSize > highLimit) pos = MathF.Max(lowLimit, highLimit - controlSize);
        return fits;
    }

    /// <summary>The attached BottomStretch dropdown (ComboBox/AutoSuggest): flush against the field (no FlyoutMargin),
    /// flips above when below doesn't fit, clamps the height to the larger side under a full collision, and reports
    /// the corner-join so the host squares the abutting popup corners (WinUI <c>UpdateCornerRadius</c> joining).</summary>
    private static PopupPlacementResult PlaceAttached(in RectF anchor, in Size2 popup, in RectF container)
    {
        float x = anchor.X;
        float belowY = anchor.Bottom;
        float aboveTop = anchor.Y;
        float popH = popup.Height;
        float roomBelow = MathF.Max(0f, container.Bottom - belowY);
        float roomAbove = MathF.Max(0f, aboveTop - container.Y);

        bool opensUp;
        float y;
        float measuredH = popH;
        if (belowY + popH <= container.Bottom) { opensUp = false; y = belowY; }
        else if (aboveTop - popH >= container.Y) { opensUp = true; y = aboveTop - popH; }
        else if (roomBelow >= roomAbove) { opensUp = false; measuredH = roomBelow; y = belowY; }
        else { opensUp = true; measuredH = roomAbove; y = aboveTop - measuredH; }

        if (x + popup.Width > container.Right) x = container.Right - popup.Width;
        if (x < container.X) x = container.X;
        if (y < container.Y) y = container.Y;

        CornerJoin join = opensUp ? CornerJoin.Bottom : CornerJoin.Top;
        return new PopupPlacementResult(x, y, opensUp, measuredH, FlyoutPlacement.BottomStretch, join, popup.Width);
    }

    // FlyoutBase_Partial.cpp:115-150 GetMajorPlacementFromPlacement.
    private static Major MajorOf(FlyoutPlacement p) => p switch
    {
        FlyoutPlacement.Top or FlyoutPlacement.TopCenter or FlyoutPlacement.TopLeft
            or FlyoutPlacement.TopEdgeAlignedLeft or FlyoutPlacement.TopEdgeAlignedRight => Major.Top,
        FlyoutPlacement.Left or FlyoutPlacement.LeftEdgeAlignedTop or FlyoutPlacement.LeftEdgeAlignedBottom => Major.Left,
        FlyoutPlacement.Right or FlyoutPlacement.RightEdgeAlignedTop or FlyoutPlacement.RightEdgeAlignedBottom => Major.Right,
        _ => Major.Bottom,
    };

    // FlyoutBase_Partial.cpp:78-113 GetJustificationFromPlacementMode.
    private static Justify JustifyOf(FlyoutPlacement p) => p switch
    {
        FlyoutPlacement.TopLeft or FlyoutPlacement.BottomLeft
            or FlyoutPlacement.TopEdgeAlignedLeft or FlyoutPlacement.BottomEdgeAlignedLeft => Justify.Left,
        FlyoutPlacement.TopEdgeAlignedRight or FlyoutPlacement.BottomEdgeAlignedRight => Justify.Right,
        FlyoutPlacement.LeftEdgeAlignedTop or FlyoutPlacement.RightEdgeAlignedTop => Justify.Top,
        FlyoutPlacement.LeftEdgeAlignedBottom or FlyoutPlacement.RightEdgeAlignedBottom => Justify.Bottom,
        _ => Justify.Center,
    };

    // A horizontal-edge justification falls back to Center when the fallback flipped the major AXIS (a Left-justified
    // vertical placement that fell back to a horizontal side has no horizontal edge to align — WinUI keeps the
    // justification enum but it only applies to the perpendicular axis; Center is the only meaningful mapping).
    private static Justify VerticalJustify(Justify j) => j switch
    {
        Justify.Top => Justify.Top,
        Justify.Bottom => Justify.Bottom,
        _ => Justify.Center,
    };

    // Effective (major, justification) → the public placement value reported in the result.
    private static FlyoutPlacement Compose(Major major, Justify justify) => major switch
    {
        Major.Top => justify switch
        {
            Justify.Left => FlyoutPlacement.TopEdgeAlignedLeft,
            Justify.Right => FlyoutPlacement.TopEdgeAlignedRight,
            _ => FlyoutPlacement.Top,
        },
        Major.Bottom => justify switch
        {
            Justify.Left => FlyoutPlacement.BottomEdgeAlignedLeft,
            Justify.Right => FlyoutPlacement.BottomEdgeAlignedRight,
            _ => FlyoutPlacement.Bottom,
        },
        Major.Left => justify switch
        {
            Justify.Top => FlyoutPlacement.LeftEdgeAlignedTop,
            Justify.Bottom => FlyoutPlacement.LeftEdgeAlignedBottom,
            _ => FlyoutPlacement.Left,
        },
        _ => justify switch
        {
            Justify.Top => FlyoutPlacement.RightEdgeAlignedTop,
            Justify.Bottom => FlyoutPlacement.RightEdgeAlignedBottom,
            _ => FlyoutPlacement.Right,
        },
    };
}
