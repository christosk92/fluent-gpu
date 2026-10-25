using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Signals;

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
public partial struct SpriteFieldEl : IElementKind<SpriteFieldEl>
{
    public const ushort TypeId = 19;
    static ushort IElementKind<SpriteFieldEl>.Kind => 19;
    public static implicit operator Element(SpriteFieldEl value) => Element.Push(in value);
    public static explicit operator SpriteFieldEl(Element element) => element.As<SpriteFieldEl>();
    public SpriteFieldEl() { }
    // ── base channels (the former Element record), per kind ──
    internal ColdRef<ElementCold> _ecold;
    public string? Key { get; init; }
    public string? MorphId { readonly get => _ecold.Value.MorphId; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.MorphId, value)) { var c = _ecold.Value; c.MorphId = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects { readonly get => _ecold.Value.ScrollEffects; init { if (!EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(_ecold.Value.ScrollEffects, value)) { var c = _ecold.Value; c.ScrollEffects = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? ScrollScope { readonly get => _ecold.Value.ScrollScope; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.ScrollScope, value)) { var c = _ecold.Value; c.ScrollScope = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Prop<bool> Visible { get; init; } = true;
    public SkeletonMode SkeletonMode { readonly get => _ecold.Value.SkeletonMode; init { if (!EqualityComparer<SkeletonMode>.Default.Equals(_ecold.Value.SkeletonMode, value)) { var c = _ecold.Value; c.SkeletonMode = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public Element? SkeletonOverride { readonly get => _ecold.Value.SkeletonOverride; init { if (!EqualityComparer<Element?>.Default.Equals(_ecold.Value.SkeletonOverride, value)) { var c = _ecold.Value; c.SkeletonOverride = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTokenDef? Transition { readonly get => _ecold.Value.Transition; init { if (!EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(_ecold.Value.Transition, value)) { var c = _ecold.Value; c.Transition = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileHover { readonly get => _ecold.Value.WhileHover; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileHover, value)) { var c = _ecold.Value; c.WhileHover = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhilePressed { readonly get => _ecold.Value.WhilePressed; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhilePressed, value)) { var c = _ecold.Value; c.WhilePressed = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Animation.MotionTarget? WhileFocus { readonly get => _ecold.Value.WhileFocus; init { if (!EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(_ecold.Value.WhileFocus, value)) { var c = _ecold.Value; c.WhileFocus = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Enter { readonly get => _ecold.Value.Enter; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Enter, value)) { var c = _ecold.Value; c.Enter = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public EnterExit? Exit { readonly get => _ecold.Value.Exit; init { if (!EqualityComparer<EnterExit?>.Default.Equals(_ecold.Value.Exit, value)) { var c = _ecold.Value; c.Exit = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float Stagger { readonly get => _ecold.Value.Stagger; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.Stagger, value)) { var c = _ecold.Value; c.Stagger = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public LayoutTransition? Layout { readonly get => _ecold.Value.Layout; init { if (!EqualityComparer<LayoutTransition?>.Default.Equals(_ecold.Value.Layout, value)) { var c = _ecold.Value; c.Layout = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget { readonly get => _ecold.Value.WheelTarget; init { if (!EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(_ecold.Value.WheelTarget, value)) { var c = _ecold.Value; c.WheelTarget = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public float ScrollLineDip { readonly get => _ecold.Value.ScrollLineDip; init { if (!EqualityComparer<float>.Default.Equals(_ecold.Value.ScrollLineDip, value)) { var c = _ecold.Value; c.ScrollLineDip = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public string? RelativeTo { readonly get => _ecold.Value.RelativeTo; init { if (!EqualityComparer<string?>.Default.Equals(_ecold.Value.RelativeTo, value)) { var c = _ecold.Value; c.RelativeTo = value; _ecold = ColdRef<ElementCold>.Of(in c); } } }
    public readonly ushort ElementTypeId => 19;

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
    static void IElementKind<SpriteFieldEl>.FreezeMembers(ref SpriteFieldEl v)
    {
        v._ecold = v._ecold.Frozen();
    }
}
