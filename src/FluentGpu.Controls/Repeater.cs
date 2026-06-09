using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Reconciler;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

public enum RepeatKind : byte { Stack, Grid, Custom, Wrap, Inline }

/// <summary>
/// A pluggable layout for <see cref="Repeater.ItemsRepeater"/> — the equivalent of WinUI's <c>Layout</c> object. A
/// readonly struct (no per-call allocation). Stack/Grid/Custom virtualize (only the window is realized); Wrap/Inline
/// are non-virtual (build the whole child list — use for SMALL collections like a nav pane or a toolbar).
/// Measured/LinedFlow/SpanGrid/HorizontalGrid ride the Custom slot (every layout object IS an
/// <see cref="IVirtualLayout"/>; variable-extent ones are <see cref="IMeasuredVirtualLayout"/> — E11-L0's one seam).
/// NOTE: the stateful layouts (Measured/LinedFlow/SpanGrid/Grouped) own realize tables — when the OWNING component
/// re-renders, hoist the layout instance (e.g. <c>UseMemo</c>) instead of calling the factory inline each render.
/// </summary>
public readonly struct RepeatLayout
{
    public readonly RepeatKind Kind;
    public readonly float Extent;          // Stack: item extent; Grid: item height; Wrap/Inline: gap
    public readonly float Gap;             // Grid: cell gap
    public readonly int Columns;           // Grid: columns
    public readonly bool Horizontal;
    public readonly IVirtualLayout? CustomLayout;

    private RepeatLayout(RepeatKind kind, float extent, float gap, int columns, bool horizontal, IVirtualLayout? custom)
        => (Kind, Extent, Gap, Columns, Horizontal, CustomLayout) = (kind, extent, gap, columns, horizontal, custom);

    /// <summary>Virtualized uniform stack (1-D).</summary>
    public static RepeatLayout Stack(float itemExtent, bool horizontal = false) => new(RepeatKind.Stack, itemExtent, 0, 0, horizontal, null);
    /// <summary>Virtualized uniform card grid (2-D, by row).</summary>
    public static RepeatLayout Grid(int columns, float itemHeight, float gap = 0f) => new(RepeatKind.Grid, itemHeight, gap, columns, false, null);
    /// <summary>Virtualized with ANY custom <see cref="IVirtualLayout"/>.</summary>
    public static RepeatLayout Custom(IVirtualLayout layout, bool horizontal = false) => new(RepeatKind.Custom, 0, 0, 0, horizontal, layout);
    /// <summary>Virtualized with a variable-extent <see cref="IMeasuredVirtualLayout"/> (estimate-then-correct + anchoring).</summary>
    public static RepeatLayout Measured(IMeasuredVirtualLayout layout, bool horizontal = false) => new(RepeatKind.Custom, 0, 0, 0, horizontal, layout);
    /// <summary>The WinUI <c>LinedFlowLayout</c> photo-wall (uniform-height lines, aspect-ratio widths). Stateful —
    /// hoist when the owner re-renders (see the struct remarks).</summary>
    public static RepeatLayout LinedFlow(float lineHeight, Func<int, float>? aspectRatio = null, float lineSpacing = 0f, float minItemSpacing = 0f)
        => new(RepeatKind.Custom, 0, 0, 0, false, new LinedFlowLayout(lineHeight, aspectRatio, lineSpacing, minItemSpacing));
    /// <summary>Uniform-row grid with item spanning (hero rows). Stateful — hoist when the owner re-renders.</summary>
    public static RepeatLayout SpanGrid(int columns, float rowHeight, float gap, Func<int, int> spanOf)
        => new(RepeatKind.Custom, 0, 0, 0, false, new SpanningGridVirtualLayout(columns, rowHeight, gap, spanOf));
    /// <summary>Horizontally-scrolling uniform grid (a shelf <paramref name="rows"/> cells tall), by column.</summary>
    public static RepeatLayout HorizontalGrid(int rows, float itemWidth, float gap = 0f)
        => new(RepeatKind.Custom, 0, 0, 0, true, new HorizontalGridVirtualLayout(rows, itemWidth, gap));
    /// <summary>Non-virtual wrap (flex-wrap) — for small collections.</summary>
    public static RepeatLayout Wrap(float gap = 8f) => new(RepeatKind.Wrap, 0, gap, 0, false, null);
    /// <summary>Non-virtual stack (column/row) — for small collections (nav panes, toolbars).</summary>
    public static RepeatLayout Inline(bool horizontal = false, float gap = 0f) => new(RepeatKind.Inline, 0, gap, 0, horizontal, null);
}

/// <summary>
/// WinUI <c>ItemCollectionTransition</c> mapped onto the engine's FLIP/<see cref="LayoutTransition"/> pipeline
/// (E11-L2): Moves = position FLIP (a reorder/removal slides the survivors), Adds = enter fade-in, Removes = exit
/// fade-out, all over WinUI's <c>ControlFastAnimationDuration</c> 167ms / decelerate spline (0,0,0,1) — the timing the
/// ItemContainer/Repeater storyboards use (ItemContainer.xaml:54-56 KeySpline="0,0,0,1" at ControlFastAnimationDuration).
/// Virtualized caveat (documented, deliberate): enter transitions also play when an item SCROLLS into the realized
/// window as a fresh mount — WinUI's LinedFlow behaves the same way for newly realized elements.
/// </summary>
public readonly record struct ItemCollectionTransition(
    bool AnimateAdds = true,
    bool AnimateRemoves = true,
    bool AnimateMoves = true,
    float DurationMs = 167f)
{
    public static ItemCollectionTransition Default => new();

    /// <summary>The engine-level spec the repeater stamps onto each item root (<see cref="BoxEl.Animate"/>).</summary>
    internal LayoutTransition ToSpec()
        => new(
            (AnimateMoves ? TransitionChannels.Position : TransitionChannels.None)
            | (AnimateAdds || AnimateRemoves ? TransitionChannels.Opacity : TransitionChannels.None),
            TransitionDynamics.Tween(DurationMs, Easing.FluentDecelerate),
            SizeMode.Auto,
            Enter: AnimateAdds ? new EnterExit(Opacity: 0f, Active: true) : default,
            Exit: AnimateRemoves ? new EnterExit(Opacity: 0f, Active: true) : default);
}

public static class Repeater
{
    /// <summary>
    /// A data-driven items panel (WinUI's ItemsRepeater): <paramref name="count"/> items × a <paramref name="template"/>
    /// over a pluggable <paramref name="layout"/>. Virtualizing layouts (Stack/Grid/Custom/Measured/LinedFlow/SpanGrid)
    /// realize only the window and recycle over the slab free-list — 0-alloc steady scroll; non-virtual layouts
    /// (Wrap/Inline) build the full child list (use for small collections). No built-in selection/focus — compose that
    /// above it (ItemsView is exactly that composition). E11-L2 surface:
    /// <paramref name="elementPrepared"/>/<paramref name="elementClearing"/>/<paramref name="elementIndexChanged"/> are
    /// WinUI's ItemsRepeater lifecycle events (ItemsRepeater.idl:186-188 ElementPrepared/ElementClearing/
    /// ElementIndexChanged), fired with item indices at realize time; <paramref name="visibleRange"/> is the prefetch
    /// hook (first,last); <paramref name="transition"/> maps ItemCollectionTransition onto the FLIP/Animate pipeline.
    /// </summary>
    public static Element ItemsRepeater(int count, Func<int, Element> template, in RepeatLayout layout,
                                        Func<int, string>? keyOf = null, int overscan = 4,
                                        Action<int>? elementPrepared = null, Action<int>? elementClearing = null,
                                        Action<int, int>? elementIndexChanged = null, Action<int, int>? visibleRange = null,
                                        ItemCollectionTransition? transition = null)
    {
        Func<int, Element> tpl = transition is { } tr ? WrapTransition(template, tr.ToSpec()) : template;

        switch (layout.Kind)
        {
            case RepeatKind.Stack:
            {
                var el = layout.Horizontal
                    ? Virtual.Custom(count, new StackVirtualLayout(layout.Extent, true), tpl, keyOf, overscan, horizontal: true)
                    : Virtual.List(count, layout.Extent, tpl, keyOf, overscan);
                return WithLifecycle(el, elementPrepared, elementClearing, elementIndexChanged, visibleRange);
            }
            case RepeatKind.Grid:
            {
                var el = Virtual.Grid(count, layout.Columns, layout.Extent, layout.Gap, tpl, keyOf, overscan);
                return WithLifecycle(el, elementPrepared, elementClearing, elementIndexChanged, visibleRange);
            }
            case RepeatKind.Custom:
            {
                var el = Virtual.Custom(count, layout.CustomLayout!, tpl, keyOf, overscan, layout.Horizontal);
                return WithLifecycle(el, elementPrepared, elementClearing, elementIndexChanged, visibleRange);
            }
            default:   // Wrap / Inline — non-virtual: build the whole keyed child list (small collections)
            {
                var children = count <= 0 ? [] : new Element[count];
                for (int i = 0; i < count; i++)
                {
                    var el = tpl(i);
                    children[i] = keyOf is null ? el : el with { Key = keyOf(i) };
                    elementPrepared?.Invoke(i);   // non-virtual: every item is realized up front
                }
                visibleRange?.Invoke(0, count);
                return layout.Kind == RepeatKind.Wrap
                    ? new BoxEl { Direction = 0, Wrap = true, Gap = layout.Gap, Children = children }
                    : new BoxEl { Direction = layout.Horizontal ? (byte)0 : (byte)1, Gap = layout.Gap, Children = children };
            }
        }
    }

    /// <summary>
    /// TYPED data binding (E11-L2): the WinUI <c>ItemsSource</c> + <c>ItemTemplate</c> pair as a generic overload —
    /// the template receives <c>(index, item)</c> with no cast and no per-item boxing. Count and item identity come
    /// from <paramref name="items"/>; re-render with a new list (or wrap in a signal read) to update.
    /// </summary>
    public static Element ItemsRepeater<T>(IReadOnlyList<T> items, Func<int, T, Element> template, in RepeatLayout layout,
                                           Func<int, string>? keyOf = null, int overscan = 4,
                                           Action<int>? elementPrepared = null, Action<int>? elementClearing = null,
                                           Action<int, int>? elementIndexChanged = null, Action<int, int>? visibleRange = null,
                                           ItemCollectionTransition? transition = null)
    {
        var list = items;
        var tpl = template;
        return ItemsRepeater(items.Count, i => tpl(i, list[i]), in layout, keyOf, overscan,
                             elementPrepared, elementClearing, elementIndexChanged, visibleRange, transition);
    }

    /// <summary>Attach the realize-time lifecycle callbacks to a virtualized viewport (no-op when all null).</summary>
    private static VirtualListEl WithLifecycle(VirtualListEl el, Action<int>? prepared, Action<int>? clearing,
                                               Action<int, int>? indexChanged, Action<int, int>? visibleRange)
        => prepared is null && clearing is null && indexChanged is null && visibleRange is null
            ? el
            : el with { OnItemPrepared = prepared, OnItemClearing = clearing, OnItemIndexChanged = indexChanged, OnVisibleRange = visibleRange };

    /// <summary>Stamp the collection-transition spec onto each item ROOT (only a <see cref="BoxEl"/> can carry
    /// <c>Animate</c>; an explicit author spec wins).</summary>
    private static Func<int, Element> WrapTransition(Func<int, Element> template, LayoutTransition spec)
        => i =>
        {
            var el = template(i);
            return el is BoxEl b && b.Animate is null ? b with { Animate = spec } : el;
        };
}
