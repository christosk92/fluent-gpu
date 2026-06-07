using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

public enum RepeatKind : byte { Stack, Grid, Custom, Wrap, Inline }

/// <summary>
/// A pluggable layout for <see cref="Repeater.ItemsRepeater"/> — the equivalent of WinUI's <c>Layout</c> object. A
/// readonly struct (no per-call allocation). Stack/Grid/Custom virtualize (only the window is realized); Wrap/Inline
/// are non-virtual (build the whole child list — use for SMALL collections like a nav pane or a toolbar).
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
    /// <summary>Non-virtual wrap (flex-wrap) — for small collections.</summary>
    public static RepeatLayout Wrap(float gap = 8f) => new(RepeatKind.Wrap, 0, gap, 0, false, null);
    /// <summary>Non-virtual stack (column/row) — for small collections (nav panes, toolbars).</summary>
    public static RepeatLayout Inline(bool horizontal = false, float gap = 0f) => new(RepeatKind.Inline, 0, gap, 0, horizontal, null);
}

public static class Repeater
{
    /// <summary>
    /// A data-driven items panel (WinUI's ItemsRepeater): <paramref name="count"/> items × a <paramref name="template"/>
    /// over a pluggable <paramref name="layout"/>. Virtualizing layouts (Stack/Grid/Custom) realize only the window and
    /// recycle over the slab free-list — 0-alloc steady scroll; non-virtual layouts (Wrap/Inline) build the full child
    /// list (use for small collections). No built-in selection/focus — compose that above it (e.g. NavigationView).
    /// </summary>
    public static Element ItemsRepeater(int count, Func<int, Element> template, in RepeatLayout layout,
                                        Func<int, string>? keyOf = null, int overscan = 4)
    {
        switch (layout.Kind)
        {
            case RepeatKind.Stack:
                return layout.Horizontal
                    ? Virtual.Custom(count, new StackVirtualLayout(layout.Extent, true), template, keyOf, overscan, horizontal: true)
                    : Virtual.List(count, layout.Extent, template, keyOf, overscan);
            case RepeatKind.Grid:
                return Virtual.Grid(count, layout.Columns, layout.Extent, layout.Gap, template, keyOf, overscan);
            case RepeatKind.Custom:
                return Virtual.Custom(count, layout.CustomLayout!, template, keyOf, overscan, layout.Horizontal);
            default:   // Wrap / Inline — non-virtual: build the whole keyed child list (small collections)
            {
                var children = count <= 0 ? [] : new Element[count];
                for (int i = 0; i < count; i++)
                {
                    var el = template(i);
                    children[i] = keyOf is null ? el : el with { Key = keyOf(i) };
                }
                return layout.Kind == RepeatKind.Wrap
                    ? new BoxEl { Direction = 0, Wrap = true, Gap = layout.Gap, Children = children }
                    : new BoxEl { Direction = layout.Horizontal ? (byte)0 : (byte)1, Gap = layout.Gap, Children = children };
            }
        }
    }
}
