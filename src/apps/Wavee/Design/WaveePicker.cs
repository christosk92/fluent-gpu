using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// Wavee's ONE preview-card radio. Four pickers had grown drifted copies of the same four things — the accent/neutral
// ink pair, the card shell, the selected-label treatment, and the wrapping strip: Settings → row density, Settings →
// track page layout, Settings → palette, and the sidebar design chooser (which is ALSO the fresh-install dialog). The
// differences that actually matter are the wireframe drawn inside a card and its footprint, so those are the
// parameters and everything else lives here.
//
// FOUR PIECES, NOT ONE RECORD. A palette swatch is a 30-DIP circle, not a card — forcing it through a card shell would
// be worse than the duplication it removes. So each picker composes only what it genuinely shares: all four take
// Strip + Label, three take Ink, two take Tile.
//
// WHY Strip IS THE POINT. It delegates group behaviour to FluentGpu.Controls.RadioButtons instead of leaving every
// card its own tab stop, which closes the gap SidebarDesignPicker documented as an engine follow-up ("Element has no
// radio-GROUP role and no arrow-key group traversal"): ONE tab stop per picker that lands on the current value,
// Up/Down/Left/Right roving, selection following focus (Ctrl+arrow to move without applying), Space to select. The two
// things RadioButtons was missing for this — a glyph-less item and a wrapping container — are now
// RadioButton.Style.ShowGlyph and RadioButtons.PartGrid/PartColumn.
static class WaveePicker
{
    /// <summary>The accent/neutral ink pair every miniature tints itself with: <c>Block</c> for the solid shapes
    /// (covers, tiles, pills), <c>Faint</c> for the skeleton bars behind them. The selected card tints its WHOLE
    /// wireframe, so the choice reads from across the page and not only from the border.</summary>
    public readonly record struct Ink(ColorF Block, ColorF Faint)
    {
        public static Ink For(bool on) => on
            ? new(Tok.AccentDefault, Tok.AccentDefault with { A = 0.45f })
            : new(Tok.FillSubtleTertiary, Tok.FillSubtleTertiary with { A = 0.7f });
    }

    /// <summary>A card footprint. <paramref name="Inset"/> is the RESTING padding: a selected card spends 1 DIP of it
    /// on the 1→2 border growth, so the border draws INWARD and the wireframe never shifts by a pixel.
    /// <paramref name="Height"/> may be <see cref="float.NaN"/> for a content-sized card; <paramref name="Gap"/> is
    /// the spacing between the card's own stacked children.</summary>
    public readonly record struct Shell(float Width, float Height, float Inset, float Gap);

    /// <summary>The settings wireframe tile — row density, track page layout.</summary>
    public static readonly Shell Tile = new(116f, 84f, 8f, 4f);
    /// <summary>The sidebar design card as it appears in Settings, where it shares a page column.</summary>
    public static readonly Shell PaneCompact = new(200f, float.NaN, 10f, 7f);
    /// <summary>The sidebar design card at full size — the fresh-install chooser.</summary>
    public static readonly Shell Pane = new(224f, float.NaN, 10f, 7f);

    /// <summary>The card shell: fill, radius, the accent border that grows inward on selection, the subtle
    /// hover/press scale. Deliberately carries NO <c>Role</c>/<c>Focusable</c>/<c>OnClick</c> — inside
    /// <see cref="Strip"/> the RadioButtons item root owns all three, and a second radio role here would announce the
    /// card twice. <c>ClipToBounds</c> is on: a miniature that outgrows its card must be cut, not painted over its
    /// neighbours (the failure mode that produced the overlapping Sidebar header).</summary>
    public static BoxEl Card(bool on, in Shell s, params Element[] body) => new()
    {
        Width = s.Width,
        Height = s.Height,
        Shrink = 0f,
        Direction = 1,
        Gap = s.Gap,
        Padding = Edges4.All(on ? s.Inset - 1f : s.Inset),
        ClipToBounds = true,
        Corners = CornerRadius4.All(Radii.Card),
        Fill = Tok.FillSubtleSecondary,
        BorderWidth = on ? 2f : 1f,
        BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
        HoverScale = WaveeMotion.ScaleSubtle.Hover,
        PressScale = WaveeMotion.ScaleSubtle.Press,
        Cursor = CursorId.Hand,
        Children = body,
    };

    /// <summary>The selected-label treatment: the current choice goes semibold and primary, the rest stay regular and
    /// secondary — so the selection survives a colour-blind read of the accent border.</summary>
    public static TextEl Label(string text, bool on, float size = 12f) => new(text)
    {
        Size = size,
        LineHeight = size + 4f,
        Weight = (ushort)(on ? 600 : 400),
        Color = on ? Tok.TextPrimary : Tok.TextSecondary,
        MaxLines = 1,
        Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A card (or swatch) over its label — the shape three of the four pickers want. Returns a
    /// <see cref="BoxEl"/> so a caller can <c>with</c>-adjust it (the palette column pins its own width).</summary>
    public static BoxEl Titled(Element card, string label, bool on, float gap = Spacing.S, float labelSize = 12f) => new()
    {
        Direction = 1,
        Gap = gap,
        AlignItems = FlexAlign.Center,
        Children = [card, Label(label, on, labelSize)],
    };

    // ── the strip ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The glyph-less radio: the CARD is the control, so the ring/dot column is not built at all and the item
    /// root shrinks to its content. The focus ring insets 2 DIP (WinUI's −7,−3 would draw it through the card's own
    /// border).</summary>
    static readonly RadioButton.Style s_bare = RadioButton.DefaultStyle with
    {
        ShowGlyph = false,
        MinWidth = 0f,
        MinHeight = 0f,
        ContentGap = 0f,
        FocusVisualMargin = Edges4.All(2f),
    };

    /// <summary>Container styling for the items grid. RadioButtons lays items out column-major and never wraps (WinUI's
    /// ColumnMajorUniformToLargestGridLayout has no wrap state); a strip of fixed-width preview cards has to drop to
    /// fewer columns on a narrow window instead of overflowing, so the grid wraps and the columns hold their width.</summary>
    static readonly TemplateParts s_strip = new()
    {
        [RadioButtons.PartGrid] = g => g with { Wrap = true, Gap = Spacing.M },
        [RadioButtons.PartColumn] = c => c with { Shrink = 0f },
    };

    /// <summary>Mount <paramref name="count"/> cards as ONE radio group. <paramref name="item"/>(index, isSelected)
    /// builds each card; <paramref name="onChange"/> is the single apply path and fires on click AND on a keyboard
    /// rove (selection follows focus — the WinUI RadioButtons contract), so it must be safe to call repeatedly.</summary>
    public static Element Strip(int count, int selected, Func<int, bool, Element> item, Action<int> onChange)
        => RadioButtons.Create(
            count,
            i => item(i, i == selected),
            // A FRESH signal per render carrying the live value — NOT a mirror kept in step by a write-during-render
            // (the BackwardsWriteGuard's exact tripwire). RadioButtons re-pushes its props, so the throwaway is
            // re-seeded from the caller's truth every render and discarded; the real write happens in onChange. The
            // same contract the SelectorBar/ComboBox settings rows already rely on.
            selectedIndex: new Signal<int>(selected),
            onChange: onChange,
            // One item per column ⇒ a single horizontal strip, so Left/Right and Up/Down both move ±1 in data order.
            maxColumns: Math.Max(1, count),
            style: s_bare,
            parts: s_strip);
}
