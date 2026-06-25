using System;
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
    /// <paramref name="grow"/> &gt; 0 makes it fill a ROW parent (a column parent cross-stretches it at grow 0).</summary>
    public static Element Of(Func<float, Element> build, float fallback = 0f, float grow = 0f)
        // Pending uses another real ResponsiveBox. The engine derives each output it renders, so the skeleton follows the
        // measured slot width instead of freezing at `fallback` (the previous 900-DIP proxy left a visible right gap).
        => Embed.Comp(() => new ResponsiveBox(build, fallback, grow))
           with
           {
               SkeletonProxy = () => Embed.Comp(() => new ResponsiveBox(build, fallback, grow))
                   with { DeriveRenderedOutput = true }
           };
}

/// <summary>The <see cref="Responsive.Of"/> component (see that summary). Reading <c>_w.Value</c> in <c>Render</c>
/// subscribes the component to its own measured width, so a resize rebuilds the child at the new width — scoped to this
/// subtree, no page-level broker.</summary>
public sealed class ResponsiveBox : Component
{
    readonly Func<float, Element> _build;
    readonly float _fallback, _grow;
    readonly Signal<float> _w = new(0f);

    public ResponsiveBox(Func<float, Element> build, float fallback = 0f, float grow = 0f)
    { _build = build; _fallback = fallback; _grow = grow; }

    public override Element Render()
    {
        float w = _w.Value;                                  // subscribe → rebuild on width change
        float effective = w > 0.5f ? w : _fallback;
        return new BoxEl
        {
            // Direction=1 so the built child CROSS-STRETCHES to our measured width (else a row wrapper would let the
            // child keep its natural width — the "spotlight doesn't stretch full-width" bug).
            Direction = 1, Grow = _grow,                     // grow 0 ⇒ column cross-stretch; >0 ⇒ fill a row parent
            // No explicit Width: the parent sizes us, so OnBoundsChanged reports the real available slot.
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
            Children = [ _build(effective) ],
        };
    }
}
