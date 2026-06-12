namespace FluentGpu.Foundation;

/// <summary>A CSS-Grid track sizing function. (Lives in Foundation so both the DSL and the layout/scene see it.)</summary>
public enum TrackKind : byte { Pixel = 0, Star = 1, Auto = 2 }

public readonly record struct TrackSize(TrackKind Kind, float Value)
{
    /// <summary>A fixed-width track (px / DIP).</summary>
    public static TrackSize Px(float v) => new(TrackKind.Pixel, v);
    /// <summary>A fractional (fr) track: shares the remaining space by <paramref name="weight"/>.</summary>
    public static TrackSize Star(float weight = 1f) => new(TrackKind.Star, weight);
    /// <summary>A track sized to its widest cell content.</summary>
    public static TrackSize Auto => new(TrackKind.Auto, 0f);
}
