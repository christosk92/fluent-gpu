using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>The rarely-set channels of the <see cref="Element"/> base (motion, scroll effects, skeleton, shared-element), split
/// out so an element that sets none of them costs one null reference. Copy-on-write exactly like <c>BoxCold</c>: `with`
/// copies share the instance until a setter that changes a value runs on the copy, which clones it first (<see cref="Owner"/> marks a clone made
/// for that element). Defaults mirror the former inline initializers. Owner keeps the element it was cloned for alive; that is bounded (one per with-chain link that mutated the block).</summary>
internal sealed class ElementCold : IEquatable<ElementCold>
{
    public static readonly ElementCold Default = new();

    /// <summary>The element this instance was cloned for (null on <see cref="Default"/>); excluded from equality.</summary>
    public Element? Owner;

    public string? MorphId;
    public FluentGpu.Scroll.Effects.ScrollEffectSpec[] ScrollEffects = [];
    public string? ScrollScope;
    public SkeletonMode SkeletonMode;
    public Element? SkeletonOverride;
    public FluentGpu.Animation.MotionTokenDef? Transition;
    public FluentGpu.Animation.MotionTarget? WhileHover;
    public FluentGpu.Animation.MotionTarget? WhilePressed;
    public FluentGpu.Animation.MotionTarget? WhileFocus;
    public EnterExit? Enter;
    public EnterExit? Exit;
    public float Stagger;
    public LayoutTransition? Layout;
    public FluentGpu.Scroll.Runtime.ScrollHandle? WheelTarget;
    public float ScrollLineDip;
    public string? RelativeTo;

    public ElementCold CloneFor(Element owner)
    {
        var c = (ElementCold)MemberwiseClone();
        c.Owner = owner;
        return c;
    }

    public bool Equals(ElementCold? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null) return false;
        return EqualityComparer<string?>.Default.Equals(MorphId, o.MorphId)
            && EqualityComparer<FluentGpu.Scroll.Effects.ScrollEffectSpec[]>.Default.Equals(ScrollEffects, o.ScrollEffects)
            && EqualityComparer<string?>.Default.Equals(ScrollScope, o.ScrollScope)
            && EqualityComparer<SkeletonMode>.Default.Equals(SkeletonMode, o.SkeletonMode)
            && EqualityComparer<Element?>.Default.Equals(SkeletonOverride, o.SkeletonOverride)
            && EqualityComparer<FluentGpu.Animation.MotionTokenDef?>.Default.Equals(Transition, o.Transition)
            && EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(WhileHover, o.WhileHover)
            && EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(WhilePressed, o.WhilePressed)
            && EqualityComparer<FluentGpu.Animation.MotionTarget?>.Default.Equals(WhileFocus, o.WhileFocus)
            && EqualityComparer<EnterExit?>.Default.Equals(Enter, o.Enter)
            && EqualityComparer<EnterExit?>.Default.Equals(Exit, o.Exit)
            && EqualityComparer<float>.Default.Equals(Stagger, o.Stagger)
            && EqualityComparer<LayoutTransition?>.Default.Equals(Layout, o.Layout)
            && EqualityComparer<FluentGpu.Scroll.Runtime.ScrollHandle?>.Default.Equals(WheelTarget, o.WheelTarget)
            && EqualityComparer<float>.Default.Equals(ScrollLineDip, o.ScrollLineDip)
            && EqualityComparer<string?>.Default.Equals(RelativeTo, o.RelativeTo);
    }

    public override bool Equals(object? obj) => Equals(obj as ElementCold);
    public override int GetHashCode() => 0;
}
