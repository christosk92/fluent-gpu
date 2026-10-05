using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>A fixed-identity array+count+version view over ≤ <see cref="SeriesSpec.MaxSamples"/> floats — the
/// <c>RowCells</c>/<c>TextSpans</c> shape for a numeric series. <see cref="SeriesEl.Samples"/> binds this AS ONE channel;
/// the reconciler copies it into a scene-owned array on every fire (Reconciler.Series.cs), so refilling a reused
/// buffer for the next tick never mutates what the scene already committed. Equal by array IDENTITY, count and
/// version: a producer bumps <see cref="Version"/> after rewriting the buffer and the bind effect re-fires.</summary>
public readonly struct SeriesSamples(float[] array, int count, uint version) : IEquatable<SeriesSamples>
{
    public readonly float[] Array = array;
    public readonly int Count = count;
    public readonly uint Version = version;
    public static readonly SeriesSamples Empty = new(System.Array.Empty<float>(), 0, 0u);
    public ReadOnlySpan<float> AsSpan() => Array is null ? default : Array.AsSpan(0, Math.Min(Count, Array.Length));
    public bool Equals(SeriesSamples other) => ReferenceEquals(Array, other.Array) && Count == other.Count && Version == other.Version;
    public override bool Equals(object? obj) => obj is SeriesSamples o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Array is null ? 0 : RuntimeHelpers.GetHashCode(Array), Count, Version);
    public static bool operator ==(SeriesSamples a, SeriesSamples b) => a.Equals(b);
    public static bool operator !=(SeriesSamples a, SeriesSamples b) => !a.Equals(b);
}

/// <summary>Dynamic geometry from a BOUND sample source: a baseline area, a mirrored area or a stroke ribbon through
/// N ≤ 512 samples (0..1), with a solid colour or a ≤ 4-stop gradient by amplitude. No <c>PathData</c>, no
/// tessellation cache: the recorder emits fixed-size <c>DrawSeriesCmd</c> chunks straight from the scene-owned
/// sample copy (gpu-renderer.md §3.1), so a per-frame rewrite costs one bind fire and one re-record of this node.
/// A leaf: no children, no pointer handlers, no declarative motion of its own (wrap it in a <see cref="BoxEl"/> for
/// Enter/Exit/Layout, exactly like <see cref="PathEl"/>).</summary>
public sealed record SeriesEl : Element
{
    public override ushort ElementTypeId => 18;

    /// <summary>The samples (0..1 heights). Static, a <c>Prop.Of(() => …)</c> thunk that reads a version signal and
    /// returns a view over a reused buffer, or a signal — like every other <see cref="Prop{T}"/> channel.</summary>
    public Prop<SeriesSamples> Samples { get; init; } = SeriesSamples.Empty;
    public SeriesShape Shape { get; init; } = SeriesShape.Baseline;
    /// <summary>The fill when <see cref="Gradient"/> is null.</summary>
    public ColorF Color { get; init; } = ColorF.FromRgba(255, 255, 255);
    /// <summary>≤ 4 stops BY AMPLITUDE (offset 0 = the baseline, 1 = a sample of 1.0); <c>Shape</c>/<c>AngleDeg</c> are ignored.</summary>
    public GradientSpec? Gradient { get; init; }
    /// <summary>Stroke width in DIP (<see cref="SeriesShape.Stroke"/> only).</summary>
    public float Thickness { get; init; } = 2f;
    /// <summary>Baseline as a fraction of the box height; NaN = the shape's default.</summary>
    public float Baseline { get; init; } = float.NaN;
    /// <summary>The box-height fraction a sample of 1.0 reaches (Mirrored: of HALF the box height).</summary>
    public float Amplitude { get; init; } = 1f;
    public float Opacity { get; init; } = 1f;
    /// <summary>What the gradient runs along (default: amplitude, the v1 behaviour).</summary>
    public SeriesGradientAxis GradientAxis { get; init; } = SeriesGradientAxis.Amplitude;
    /// <summary>A 1-DIP analytic fringe on the Stroke / Polar ribbon edges (on by default).</summary>
    public bool AntiAlias { get; init; } = true;
    /// <summary>A second gradient the colours blend toward by <see cref="GradientMix"/> (same stop count as <see cref="Gradient"/>).</summary>
    public GradientSpec? GradientTo { get; init; }
    /// <summary>The 0..1 blend toward <see cref="GradientTo"/>; bindable, paint-only.</summary>
    public Prop<float> GradientMix { get; init; } = 0f;
    /// <summary>Additive adds light instead of covering (glow); see <see cref="PaintBlend"/>.</summary>
    public PaintBlend Blend { get; init; } = PaintBlend.SrcOver;

    // layout (the PolylineStrokeEl block, Element.cs)
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public FlexAlign JustifySelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
}
