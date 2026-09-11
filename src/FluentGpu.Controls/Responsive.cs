using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Size-reactive container — the reusable "measure my own available width and build from it" primitive. It drops its
/// explicit width so a COLUMN parent cross-stretches it (or a ROW parent grows it when <c>grow &gt; 0</c>), self-measures
/// via <c>OnBoundsChanged</c> into a private signal, and rebuilds its child through <paramref name="build"/> whenever the
/// width changes. This replaces the app-level "measure at the page root and thread a width signal down" broker: any
/// size-reactive section (a clamped hero, a responsive grid, a paged shelf) just wraps itself in <see cref="Of"/>.
/// Before the first bounds report it builds at <paramref name="fallback"/> for one frame (mirrors
/// <c>AutoSuggestBox</c>'s self-measure). Lives INSIDE a vertical <c>ScrollView</c> safely: a vertical viewport with a
/// finite offered width adopts it (FlexLayout overflow-y), so the cross-stretch reports the real viewport width.
/// </summary>
public static class Responsive
{
    /// <summary>A size-reactive box: <paramref name="build"/> is re-invoked with the measured available width whenever it
    /// changes. <paramref name="fallback"/> is the width passed for the one frame before the first measure;
    /// <paramref name="grow"/> &gt; 0 makes it fill a ROW parent (a column parent cross-stretches it at grow 0).
    /// <para><b>UNGATED overload.</b> <paramref name="build"/> is a live closure: a parent re-render pushes a NEW closure
    /// (a lambda allocates one every render), which can never compare equal, so this box rebuilds its child whenever its
    /// parent re-renders. That is what makes data captured by the closure stay correct — and it is why a hot section
    /// should use the state-gated overload below instead of memoizing its closure.</para></summary>
    public static Element Of(Func<float, Element> build, float fallback = 0f, float grow = 0f)
        // Pending uses another real ResponsiveBox. The engine derives each output it renders, so the skeleton follows the
        // measured slot width instead of freezing at `fallback` (the previous 900-DIP proxy left a visible right gap).
        => Embed.Comp(new ResponsiveBox.Props(build, fallback, grow), static () => new ResponsiveBox())
           with
           {
               SkeletonProxy = () => Embed.Comp(new ResponsiveBox.Props(build, fallback, grow), static () => new ResponsiveBox())
                   with { DeriveRenderedOutput = true }
           };

    /// <summary>The <b>state-gated</b> size-reactive box: the child is rebuilt when the measured width changes or when
    /// <paramref name="state"/> changes by VALUE — never merely because the parent re-rendered.
    /// <para>The rule the gate rests on: <paramref name="build"/> must be a function of <c>(state, width)</c>. Hand it
    /// every value the subtree paints as <paramref name="state"/> — one immutable record or a tuple of scalars/records,
    /// so <c>Equals</c> means what it says — and capture only stable behaviour (a navigate/play callback) in the lambda.
    /// The delegate itself is IGNORED by the gate (a fresh closure every render can never compare equal); the newest one
    /// is used from the next rebuild on. A null <paramref name="state"/> degrades to the ungated overload's
    /// semantics.</para></summary>
    public static Element Of<TState>(TState state, Func<TState, float, Element> build, float fallback = 0f, float grow = 0f)
    {
        // One closure per render — allocated, then ignored by the gate. Cheaper by orders of magnitude than the subtree
        // rebuild it replaces, and it keeps the callable shape identical for the component (Func<float, Element>).
        Func<float, Element> bound = w => build(state, w);
        object? gate = state;
        return Embed.Comp(new ResponsiveBox.Props(bound, fallback, grow, gate), static () => new ResponsiveBox())
           with
           {
               SkeletonProxy = () => Embed.Comp(new ResponsiveBox.Props(bound, fallback, grow, gate), static () => new ResponsiveBox())
                   with { DeriveRenderedOutput = true }
           };
    }
}

/// <summary>The <see cref="Responsive.Of"/> component (see that summary). Reading <c>_w.Value</c> in <c>Render</c>
/// subscribes the component to its own measured width, so a resize rebuilds the child at the new width — scoped to this
/// subtree, no page-level broker.</summary>
public sealed class ResponsiveBox : Component
{
    /// <summary>The re-pushed props. <b>Equality IS the reconciler's re-render gate</b> (the props signal coalesces an
    /// equal write), so what it compares decides when the child is rebuilt:
    /// <list type="bullet">
    /// <item><description><see cref="Fallback"/>, <see cref="Grow"/> — always by value.</description></item>
    /// <item><description><see cref="Gate"/> non-null (<see cref="Responsive.Of{TState}"/>) — the state value decides,
    /// and <see cref="Build"/> is IGNORED. The contract: the builder must be a function of <c>(state, width)</c>.</description></item>
    /// <item><description><see cref="Gate"/> null (<see cref="Responsive.Of(Func{float,Element},float,float)"/>) —
    /// <see cref="Build"/> is compared as a delegate (target + method), i.e. a fresh closure never compares equal and the
    /// child rebuilds with its parent. Deliberate: the ungated overload's closure IS its data channel, and freezing it
    /// at mount is the stale-content bug class this box used to have.</description></item>
    /// </list></summary>
    public sealed record Props(Func<float, Element> Build, float Fallback, float Grow, object? Gate = null)
    {
        public bool Equals(Props? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null || Fallback != other.Fallback || Grow != other.Grow) return false;
            if (Gate is null || other.Gate is null)
                return Gate is null && other.Gate is null && EqualityComparer<Func<float, Element>>.Default.Equals(Build, other.Build);
            return Gate.Equals(other.Gate);
        }

        public override int GetHashCode() => HashCode.Combine(Fallback, Grow, Gate);
    }

    readonly Signal<float> _w = new(0f);

    public override Element Render()
    {
        var props = UseProps<Props>();
        float w = _w.Value;                                  // subscribe → rebuild on width change
        float effective = w > 0.5f ? w : props.Fallback;
        return new BoxEl
        {
            // Direction=1 so the built child CROSS-STRETCHES to our measured width (else a row wrapper would let the
            // child keep its natural width — the "spotlight doesn't stretch full-width" bug).
            Direction = 1, Grow = props.Grow,                     // grow 0 ⇒ column cross-stretch; >0 ⇒ fill a row parent
            // No explicit Width: the parent sizes us, so OnBoundsChanged reports the real available slot.
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
            Children = [ props.Build(effective) ],
        };
    }
}
