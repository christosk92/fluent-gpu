using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>A fixed-identity array+count+version view over ≤ <see cref="SpriteFieldEl.MaxSprites"/> sprites — the
/// <see cref="SeriesSamples"/> shape. <see cref="SpriteFieldEl.Instances"/> binds this AS ONE channel; the reconciler copies
/// it into a scene-owned array on every fire, so refilling the reused buffer for the next tick never mutates what the scene
/// committed. Equal by array identity, count and version: a producer bumps the version after rewriting the buffer.</summary>
public readonly struct SpriteInstances(Sprite[] array, int count, uint version) : IEquatable<SpriteInstances>
{
    public readonly Sprite[] Array = array;
    public readonly int Count = count;
    public readonly uint Version = version;
    public static readonly SpriteInstances Empty = new(System.Array.Empty<Sprite>(), 0, 0u);
    public ReadOnlySpan<Sprite> AsSpan() => Array is null ? default : Array.AsSpan(0, Math.Min(Count, Array.Length));
    public bool Equals(SpriteInstances other) => ReferenceEquals(Array, other.Array) && Count == other.Count && Version == other.Version;
    public override bool Equals(object? obj) => obj is SpriteInstances o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Array is null ? 0 : RuntimeHelpers.GetHashCode(Array), Count, Version);
    public static bool operator ==(SpriteInstances a, SpriteInstances b) => a.Equals(b);
    public static bool operator !=(SpriteInstances a, SpriteInstances b) => !a.Equals(b);
}

/// <summary>Many small shapes from ONE node (visualizer F5): discs, capsules, streaks or segments from a BOUND instance
/// buffer. A tick is one bind fire, one copy of ≤ 32 KB and one re-record of this node; the recorder emits fixed-size
/// <c>DrawSpritesCmd</c> chunks of 16 and the GPU expands each sprite into an SDF quad — nothing is tessellated or cached.
/// A leaf like <see cref="SeriesEl"/>: no children, no pointer handlers; wrap it in a <see cref="BoxEl"/> for
/// Enter/Exit/Layout/Transform.</summary>
public sealed record SpriteFieldEl : Element
{
    public override ushort ElementTypeId => 19;

    /// <summary>The most sprites one node carries; extras are dropped (and counted) at write time.</summary>
    public const int MaxSprites = 1024;

    public Prop<SpriteInstances> Instances { get; init; } = SpriteInstances.Empty;
    public SpriteKernel Kernel { get; init; } = SpriteKernel.Disc;
    /// <summary>Additive adds light (glow); see <see cref="PaintBlend"/>.</summary>
    public PaintBlend Blend { get; init; } = PaintBlend.SrcOver;
    public float Opacity { get; init; } = 1f;

    // layout (the SeriesEl block)
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
