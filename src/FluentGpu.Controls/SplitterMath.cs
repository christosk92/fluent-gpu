using System;

namespace FluentGpu.Controls;

/// <summary>Which edge of the pane the splitter sits on. Trailing: pointer-positive grows the size (a left pane's
/// right seam, or a top pane's bottom seam). Leading: pointer-positive shrinks (a right pane's left seam).</summary>
public enum SplitterPolarity : sbyte { Trailing = 1, Leading = -1 }

/// <summary>Which dimension the splitter writes. Horizontal (default): window-X, <c>SizeWE</c>, width.
/// Vertical: window-Y, <c>SizeNS</c>, height. <see cref="SplitterMath.RawWidth"/> is axis-agnostic — pass X or Y.</summary>
public enum SplitterAxis : byte { Horizontal = 0, Vertical = 1 }

/// <summary>
/// Pure drag / detent / fade arithmetic for <see cref="Splitter"/>. Allocation-free so VerticalSlice can gate it
/// without a scene, and so the live <c>OnDrag</c> path never diverges from the tests.
/// </summary>
public static class SplitterMath
{
    /// <summary>Prospective width from a pointer delta. <paramref name="polarity"/> scales the delta so a Leading
    /// seam (right-rail left edge) shrinks as the pointer moves right.</summary>
    public static float RawWidth(float startW, float startPx, float px, SplitterPolarity polarity)
        => startW + (sbyte)polarity * (px - startPx);

    public static float ClampWidth(float w, float min, float max) => Math.Clamp(w, min, max);

    /// <summary>How far past <paramref name="fadeStart"/> the pointer has pushed (positive ⇒ inside the resist zone).</summary>
    public static float Into(float fadeStart, float rawW) => fadeStart - rawW;

    /// <summary>Sticky width inside the resist zone: the pane shrinks only by <paramref name="resist"/> of the
    /// overshoot, so it feels like it is holding at the floor.</summary>
    public static float ResistWidth(float fadeStart, float into, float resist)
        => fadeStart - into * resist;

    /// <summary>Content opacity in the resist zone. <c>into = 0</c> → 1; <c>into = fadeDistance</c> →
    /// <paramref name="minFade"/>. A non-positive distance holds opacity at 1 (no fade).</summary>
    public static float Fade(float into, float fadeDistance, float minFade)
    {
        if (fadeDistance <= 0f) return 1f;
        float t = into / fadeDistance;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        return 1f - t * (1f - minFade);
    }

    /// <summary>True once the pointer has travelled <paramref name="forcePush"/> DIP past the fade start.</summary>
    public static bool ShouldCollapse(float into, float forcePush)
        => forcePush > 0f && into >= forcePush;
}
