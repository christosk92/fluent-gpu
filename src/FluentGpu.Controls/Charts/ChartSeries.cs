using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>One series of a chart — the port of a shadcn/ui <c>ChartConfig</c> entry: a stable <paramref name="Key"/>
/// (what data rows, legends and tooltips are matched by), the human <paramref name="Label"/>, the ink the series is
/// drawn in, and an optional icon glyph a legend/tooltip shows instead of the colour swatch. Colour follows the ENTITY,
/// never its rank: a filter that hides series must not repaint the survivors, so callers assign colours once per key
/// (see <see cref="ChartPalette"/>) and pass the same <see cref="ChartSeries"/> instances back.</summary>
public sealed record ChartSeries(string Key, string Label, ColorF Color, string? Glyph = null);

/// <summary>A categorical chart table: <paramref name="Categories"/> along the category axis (months, buckets…),
/// the <paramref name="Series"/> drawn, and <c>Values[series][point]</c> (NaN = a gap). Rows are painted in series
/// order (first series at the bottom of a stack).</summary>
public sealed record CartesianData(IReadOnlyList<string> Categories, IReadOnlyList<ChartSeries> Series, float[][] Values)
{
    /// <summary>Number of points (the widest series row).</summary>
    public int PointCount => ChartMath.PointCount(Values);

    /// <summary>The value of one cell, or NaN when the series has no such point.</summary>
    public float ValueAt(int series, int point)
    {
        if (series < 0 || series >= Values.Length) return float.NaN;
        var row = Values[series];
        return row is null || point < 0 || point >= row.Length ? float.NaN : row[point];
    }
}

/// <summary>
/// Series colours. The DEFAULT is a single-hue ramp off the live accent (<see cref="Ramp"/>) — the same choice the
/// shadcn v4 documentation site makes for itself (blue-300…800 in both themes): one hue reads as one system, survives
/// a theme flip by construction, and never needs a colour-blindness validation. Adjacent steps alternate light/dark so
/// neighbouring series stay distinguishable. A multi-hue <see cref="Categorical"/> palette is an OPT-IN the app
/// supplies — in fixed order, never cycled (a 9th series folds into "Other"), and validated with the dataviz palette
/// script before it ships.
/// </summary>
public static class ChartPalette
{
    /// <summary><paramref name="count"/> steps of the accent ramp: Base, Light2, Dark2, Light1, Dark1, Light3, Dark3,
    /// then the tail repeats Light3 (a series past the seventh has no distinct step left — fold it into "Other").</summary>
    public static ColorF[] Ramp(int count, ColorF? accent = null)
    {
        var r = AccentRamp.Derive(accent ?? Tok.AccentDefault);
        ReadOnlySpan<ColorF> order = [r.Base, r.Light2, r.Dark2, r.Light1, r.Dark1, r.Light3, r.Dark3];
        var result = new ColorF[Math.Max(0, count)];
        for (int i = 0; i < result.Length; i++) result[i] = order[Math.Min(i, order.Length - 1)];
        return result;
    }

    /// <summary>An app-supplied fixed-order categorical palette (null = none; <see cref="Ramp"/> is used). Set once at
    /// startup; charts read it when a caller asks for <see cref="Series"/> without explicit colours.</summary>
    public static ColorF[]? Categorical;

    /// <summary>Build series from keys/labels with palette colours assigned in FIXED order: the categorical palette
    /// when one is set and covers the count, else the accent ramp. Colour i belongs to key i for the life of the app,
    /// so hiding a series never recolours the others.</summary>
    public static ChartSeries[] Series(ReadOnlySpan<(string Key, string Label)> keys, ColorF? accent = null)
    {
        var cat = Categorical;
        var ramp = cat is { Length: > 0 } && cat.Length >= keys.Length ? cat : Ramp(keys.Length, accent);
        var result = new ChartSeries[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            result[i] = new ChartSeries(keys[i].Key, keys[i].Label, ramp[Math.Min(i, ramp.Length - 1)]);
        return result;
    }
}
