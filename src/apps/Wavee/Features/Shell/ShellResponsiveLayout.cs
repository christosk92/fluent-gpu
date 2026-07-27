using System;

namespace Wavee;

/// <summary>Pure whole-shell breakpoints. The bands are hysteretic so a boundary crossing during pointer resize
/// commits one structural change instead of oscillating on adjacent frames.</summary>
public static class ShellResponsiveLayout
{
    public const float NarrowEnterW = 720f;
    public const float NarrowLeaveW = 760f;
    public const float CompactRailW = 56f;
    public const float DrawerMinW = 240f;
    public const float DrawerViewportInset = 32f;

    public const float ToolbarNarrowEnterW = 520f;
    public const float ToolbarNarrowLeaveW = 560f;

    public static bool NarrowFor(float width, bool current, bool initialized)
    {
        if (width <= 0f) return current;
        if (!initialized) return width <= NarrowEnterW;
        return current ? width < NarrowLeaveW : width <= NarrowEnterW;
    }

    public static bool ToolbarNarrowFor(float width, bool current, bool initialized)
    {
        if (width <= 0f) return current;
        if (!initialized) return width <= ToolbarNarrowEnterW;
        return current ? width < ToolbarNarrowLeaveW : width <= ToolbarNarrowEnterW;
    }

    public static float DrawerWidth(float viewportWidth, float preferredWidth)
    {
        float cap = MathF.Max(CompactRailW, viewportWidth - DrawerViewportInset);
        return MathF.Min(MathF.Max(DrawerMinW, preferredWidth), cap);
    }

    public static float DrawerRestingOpacity(bool open) => open ? 1f : 0f;
    public static float DrawerRestingTranslateX(bool open, float width) => open ? 0f : -width;
}
