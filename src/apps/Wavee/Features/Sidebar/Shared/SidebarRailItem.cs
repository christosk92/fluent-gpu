using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// F.1.4 — the 56-DIP compact rail's shared 40×40 TILE.
//
// Locked decision 7 makes the rail's CONTENT mode-specific (Classic shows pins → shortcuts → playlist covers; V3 shows
// its filter/entity strip; Curated shows the sections that opted into the rail). The TILE is shared, so all three rails
// have the same hit box, the same 6/8-DIP corner ladder, the same selected treatment and the same tooltip behaviour.
//
// Every visual below is copied verbatim from the landed WaveeSidebar CompactIcon / CompactArt / CompactDivider /
// CompactSkeleton so relocating those call sites is a pure refactor.

static class SidebarRailItem
{
    /// <summary>The rail tile box (40×40 inside the 56-DIP rail).</summary>
    public const float Box = 40f;

    /// <summary>The art edge inside an <see cref="Art"/> tile (the 2-DIP accent ring needs the 2-DIP inset).</summary>
    public const float ArtEdge = 36f;

    /// <summary>A GLYPH tile — a library shortcut, an app route, a folder pin. Carries the same 4-state
    /// selection-aware ramp as <see cref="SidebarEntityRow"/> (rest/selected × hover × pressed), so a selected rail tile
    /// darkens on hover instead of flattening.</summary>
    public static Element Icon(string key, string glyph, bool selected, Action? onClick, string? tooltip = null)
    {
        var tile = new BoxEl
        {
            Key = key,
            Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(6f),
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
            OnClick = onClick,
            Children = [Ui.Icon(glyph, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary)],
        };
        return Tip(tile, tooltip);
    }

    /// <summary>An ART tile — an entity pin or a playlist cover. <paramref name="art"/> comes from
    /// <see cref="SidebarCover"/> at <see cref="ArtEdge"/>; the 2-DIP accent border is the rail's selection cue (the
    /// overlay pill is expanded-only).</summary>
    public static Element Art(string key, Element art, bool selected, Action? onClick, string? tooltip = null)
    {
        var tile = new BoxEl
        {
            Key = key,
            Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(8f),
            BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent, BorderWidth = selected ? 2f : 0f,
            Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
            OnClick = onClick,
            Children = [art],
        }.Interactive(Interaction.Subtle);
        return Tip(tile, tooltip);
    }

    /// <summary>The short centred rule that separates rail bands (24×1 at <c>Tok.TextTertiary</c> A=0.3).</summary>
    public static Element Divider() => new BoxEl
    {
        Width = 24f, Height = 1f, Margin = new Edges4(0f, 4f, 0f, 4f), Fill = Tok.TextTertiary with { A = 0.3f },
    };

    /// <summary>A pending rail tile (the rail's shimmer placeholder).</summary>
    public static Element Skeleton() => new BoxEl
    {
        Width = Box, Height = Box, Corners = CornerRadius4.All(8f), Fill = Tok.FillSubtleSecondary,
    };

    /// <summary>The tooltip IS the tile's label — a 56-DIP rail has no room for text, so the row label must be reachable
    /// on hover/focus or the rail is unusable for anything but the five icons a user has memorised.</summary>
    static Element Tip(BoxEl tile, string? tooltip)
        => tooltip is { Length: > 0 } t ? ToolTip.Wrap(tile, t) : tile;
}
