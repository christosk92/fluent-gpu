using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace Wavee;

/// <summary>THE responsive text-link column grid. One list of names, laid out as plain typographic links in
/// alphabetised, responsive columns — Browse's category directory and Search's genre results are the same problem and
/// now the same code.
///
/// Deliberately typographic rather than a wall of coloured tiles. These surfaces are tables of contents — their job is
/// to let someone find one of ~70 names fast, and text in columns is scannable in a way that 70 colour blocks are not.
/// An item's own colour is kept for the page it opens, where it means something; nothing here carries colour, so a
/// genre link and a browse-category link are indistinguishable.
///
/// <para>A STATIC FACTORY, not a <see cref="Component"/>: the item list changes as search/browse data lands, and a
/// component ctor arg would freeze at mount (see <c>docs/design/subsystems/component-props-contract.md</c>).</para></summary>
static class LinkColumns
{
    /// <summary>One cell: the visible <paramref name="Title"/>, a stable reconciler <paramref name="Key"/> (the item's
    /// uri), and what a click does.</summary>
    internal readonly record struct Item(string Title, string Key, Action OnOpen);

    // Column widths chosen so a long localised title ("Cooking & Dining", "Fiction & Literature") fits without
    // wrapping at the widest tier, then the column count steps down rather than the text truncating.
    const float MinColumnWidth = 190f;
    const int MaxColumns = 6;

    /// <summary>The grid itself, already wrapped in its own width measurement — callers just hand it items.</summary>
    internal static Element Create(IReadOnlyList<Item> items)
        => Responsive.Of(width => Columns(items, width > 0f ? width : 900f), fallback: 900f);

    static Element Columns(IReadOnlyList<Item> items, float width)
    {
        int cols = Math.Clamp((int)(width / MinColumnWidth), 1, MaxColumns);
        int rows = (items.Count + cols - 1) / cols;

        // Column-major fill so the eye reads DOWN each column — the order the alphabetised list is sorted in. A
        // row-major fill would scatter the alphabet across the row and defeat the sort entirely.
        var columnEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            var cells = new List<Element>(rows);
            for (int r = 0; r < rows; r++)
            {
                int idx = c * rows + r;
                if (idx >= items.Count) break;
                cells.Add(Link(items[idx]));
            }
            columnEls[c] = new BoxEl
            {
                Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = cells.ToArray(),
            };
        }
        return new BoxEl { Direction = 0, Gap = Spacing.M, MinWidth = 0f, Children = columnEls };
    }

    static Element Link(Item item) => new BoxEl
    {
        // A link, not a button: it navigates. Role + Focusable give it keyboard reach and the right screen-reader verb.
        Key = item.Key,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
        Padding = new Edges4(Spacing.XS, 4f, Spacing.XS, 4f),
        Corners = CornerRadius4.All(Radii.Control),
        HoverFill = Tok.FillControlSecondary,
        MinWidth = 0f,
        OnClick = item.OnOpen,
        Children =
        [
            new TextEl(item.Title)
            {
                Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The loading shape: <paramref name="rows"/> rows of bars at the SAME column count and row rhythm the
    /// loaded grid uses, so the links land into their own places without the page jumping.</summary>
    internal static Element Skeleton(int rows)
        => Responsive.Of(width => SkeletonColumns(rows, width > 0f ? width : 900f), fallback: 900f);

    static Element SkeletonColumns(int rows, float width)
    {
        int cols = Math.Clamp((int)(width / MinColumnWidth), 1, MaxColumns);
        var columnEls = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            var cells = new List<Element>(rows);
            for (int r = 0; r < rows; r++)
                cells.Add(new BoxEl
                {
                    // Varied widths so the band reads as a list of NAMES rather than a solid block of identical bars.
                    Width = 92f + ((c * 7 + r * 13) % 5) * 22f,
                    // FillSubtleTertiary, not FillCardSecondary: on the light canvas Secondary sits ~2% off the page and the
                    // whole skeleton read as a blank screen rather than as loading content.
                    Height = 13f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleTertiary,
                });
            // Spacing.XS gap — the SAME rhythm Columns() gives the real links, so nothing shifts on landing.
            columnEls[c] = new BoxEl { Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = cells.ToArray() };
        }
        return new BoxEl { Direction = 0, Gap = Spacing.M, MinWidth = 0f, Children = columnEls };
    }
}
