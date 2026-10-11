using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
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

    /// <summary>The <b>tier-gated, estimate-seeded</b> size-reactive box (E22): <paramref name="build"/> receives the TIER
    /// index of <paramref name="ladder"/> (the caller's own enum value) and is re-invoked only when the tier CROSSES, never for
    /// a width that stays inside its tier. There is no fixed fallback width: the first frame resolves the tier from
    /// <paramref name="estimate"/> (a same-frame width such as the viewport minus the shell's chrome), so the first frame is
    /// already in its final tier. Once the box has been measured the measured width drives the ladder, with its hysteresis.
    /// <para>A crossing after mount rides <c>UseTier</c>'s pending-tier commit: on a live resize the tier lands on the first
    /// non-resized frame, right before FLIP capture, so the rebuild animates (moved nodes FLIP, swapped subtrees cross-fade)
    /// instead of snapping. <paramref name="key"/> is the box's identity (a stable Key keeps the measurement and the tier across
    /// parent re-renders and reorders). <paramref name="build"/> is a live closure here exactly like the ungated
    /// <see cref="Of(Func{float,Element},float,float)"/>: a parent re-render pushes a new delegate and rebuilds the child; use
    /// the state-gated overload below to rebuild on a state change only.</para></summary>
    public static Element Of(string key, TierLadder ladder, Func<float> estimate, Func<int, Element> build, float grow = 0f)
    {
        var props = new TierBox.Props(ladder, estimate, build, grow);
        return Embed.Comp(props, static () => new TierBox())
           with
           {
               Key = key,
               SkeletonProxy = () => Embed.Comp(props, static () => new TierBox()) with { DeriveRenderedOutput = true },
           };
    }

    /// <summary>The tier-gated overload with a <b>state gate</b>: the child is rebuilt when the tier crosses or when
    /// <paramref name="state"/> changes by VALUE; <paramref name="build"/> must be a function of <c>(state, tier)</c> and the
    /// delegate itself is ignored by the gate (see <see cref="Of{TState}(TState,Func{TState,float,Element},float,float)"/>).</summary>
    public static Element Of<TState>(string key, TState state, TierLadder ladder, Func<float> estimate, Func<TState, int, Element> build, float grow = 0f)
    {
        Func<int, Element> bound = tier => build(state, tier);
        object? gate = state;
        var props = new TierBox.Props(ladder, estimate, bound, grow, gate);
        return Embed.Comp(props, static () => new TierBox())
           with
           {
               Key = key,
               SkeletonProxy = () => Embed.Comp(props, static () => new TierBox()) with { DeriveRenderedOutput = true },
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

/// <summary>The tier-gated <see cref="Responsive.Of(string,TierLadder,Func{float},Func{int,Element},float)"/> component. It
/// measures its own slot width (a private signal written from <c>OnBoundsChanged</c>) but never reads it in render: the
/// width feeds <c>UseTier</c> through <see cref="TierSource.Measured"/> (seeded by the estimate until the first measure), so
/// the box re-renders ONLY when the tier crosses. The estimate thunk is re-pointed each render, so the newest closure seeds
/// a late first measure.</summary>
public sealed class TierBox : Component
{
    /// <summary>The re-pushed props; equality is the reconciler's re-render gate, exactly as <see cref="ResponsiveBox.Props"/>:
    /// the ladder (by reference) and <see cref="Grow"/> always; <see cref="Gate"/> non-null ⇒ the state value decides and
    /// <see cref="Build"/> is IGNORED, null ⇒ <see cref="Build"/> is compared as a delegate (a fresh closure rebuilds).
    /// <see cref="Estimate"/> is never compared (the newest is used from the next evaluation on).</summary>
    public sealed record Props(TierLadder Ladder, Func<float> Estimate, Func<int, Element> Build, float Grow, object? Gate = null)
    {
        public bool Equals(Props? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null || !ReferenceEquals(Ladder, other.Ladder) || Grow != other.Grow) return false;
            if (Gate is null || other.Gate is null)
                return Gate is null && other.Gate is null && EqualityComparer<Func<int, Element>>.Default.Equals(Build, other.Build);
            return Gate.Equals(other.Gate);
        }

        public override int GetHashCode() => HashCode.Combine(Ladder, Grow, Gate);
    }

    readonly Signal<float> _w = new(0f);
    Func<float> _estimate = static () => 0f;
    readonly Func<float> _estimateProxy;
    readonly Action<RectF> _onBounds;

    public TierBox()
    {
        _estimateProxy = () => _estimate();
        _onBounds = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; };
    }

    public override Element Render()
    {
        var props = UseProps<Props>();
        _estimate = props.Estimate;
        var tier = UseTier(props.Ladder, TierSource.Measured(_w, _estimateProxy));
        return new BoxEl
        {
            // Direction=1 so the built child CROSS-STRETCHES to the measured slot (as ResponsiveBox); no explicit Width.
            Direction = 1, Grow = props.Grow,
            OnBoundsChanged = _onBounds,
            Children = [ props.Build(tier.Value) ],   // tier.Value subscribes: a rebuild happens on a crossing only
        };
    }
}
