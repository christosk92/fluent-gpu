using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>The severity preset that picks an <see cref="InfoBadge"/> background and (for Icon badges) a default glyph,
/// mirroring WinUI's {Attention,Informational,Success,Caution,Critical}InfoBadgeStyle set
/// (InfoBadge_themeresources.xaml). Default == the plain accent badge.</summary>
public enum InfoBadgeSeverity : byte { Default = 0, Attention = 1, Informational = 2, Success = 3, Caution = 4, Critical = 5 }

/// <summary>
/// Computed template settings for the <see cref="InfoBadge"/> — the typed-record convention modelled on
/// <see cref="ExpanderTemplateSettings"/>. Mirrors WinUI's generated <c>InfoBadgeTemplateSettings</c>: the display kind
/// (Dot vs Value vs Icon, derived from Value/Icon exactly like <c>OnDisplayKindPropertiesChanged</c>), the resolved badge
/// height/width and the pill corner radius (= ActualHeight/2, per <c>OnSizeChanged</c>), and the per-kind content margin.
/// </summary>
/// <param name="Kind">0 = Dot, 1 = Value (text), 2 = Icon (font/glyph).</param>
/// <param name="Height">Resolved badge height: 4 for a Dot, otherwise clamped to MaxHeight = 16.</param>
/// <param name="MinWidth">Resolved minimum width: 4 for a Dot, otherwise 16 (MeasureOverride squares a too-narrow badge).</param>
/// <param name="CornerRadius">InfoBadgeCornerRadius = Height / 2 — a full pill (2 for a Dot, 8 for a 16-tall badge).</param>
/// <param name="ContentMargin">Per-kind inner margin: Value/FontIcon = 4,0,4,2; (a non-font IconElement would be 4,4,4,4).</param>
public readonly record struct InfoBadgeTemplateSettings(
    byte Kind, float Height, float MinWidth, float CornerRadius, Edges4 ContentMargin)
{
    // WinUI InfoBadge_themeresources.xaml (Default theme dictionary):
    public const float MinDim = 4f;          // InfoBadgeMinHeight / InfoBadgeMinWidth
    public const float MaxHeight = 16f;      // InfoBadgeMaxHeight

    /// <summary>WinUI renders the FontIcon inside a <c>Viewbox</c> (IconPresenter, VerticalAlignment=Stretch) that fills
    /// the RootGrid content box, so the glyph is scaled to the box HEIGHT, not a fixed font size. The Attention &amp;
    /// Informational Icon styles add <c>Padding="0,4,0,2"</c>, shrinking the content box to 16 − 4 − 2 = 10 tall; the
    /// other severities (Success/Caution/Critical) keep the default 0 padding and stretch across the full ~16 box. We
    /// reproduce that by driving the glyph <c>Size</c> off these per-style target heights instead of a flat 12.</summary>
    public const float IconGlyphSizePadded = 10f;  // Attention/Informational: 16 − Padding(0,4,0,2) = 10
    public const float IconGlyphSizeFull   = 16f;  // Success/Caution/Critical: full ~16 content box
    public const float ValueFontSize       = 11f;  // InfoBadgeValueFontSize

    // ValueInfoBadgeTextMargin / IconInfoBadgeFontIconMargin = 4,0,4,2 (both identical in every theme dictionary).
    public static readonly Edges4 ContentMarginValueOrFontIcon = new(4f, 0f, 4f, 2f);

    /// <summary>Pure factory — recomputes the kind/geometry from Value &amp; whether an icon glyph is present, exactly
    /// like WinUI's <c>OnDisplayKindPropertiesChanged</c> (Value &gt;= 0 wins, then Icon, else Dot) and <c>OnSizeChanged</c>
    /// (corner = height/2). Called once at reconcile time, never in a hot bind thunk.</summary>
    public static InfoBadgeTemplateSettings For(int value, bool hasIcon)
    {
        if (value >= 0)
            return new InfoBadgeTemplateSettings(1, MaxHeight, MaxHeight, MaxHeight / 2f, ContentMarginValueOrFontIcon);
        if (hasIcon)
            return new InfoBadgeTemplateSettings(2, MaxHeight, MaxHeight, MaxHeight / 2f, ContentMarginValueOrFontIcon);
        return new InfoBadgeTemplateSettings(0, MinDim, MinDim, MinDim / 2f, default);   // Dot: 4x4, corner 2
    }
}

/// <summary>
/// A WinUI InfoBadge: a small accent (or severity) pill that surfaces a notification count, an icon, or a bare dot.
/// Three display kinds, resolved exactly as WinUI's <c>InfoBadge::OnDisplayKindPropertiesChanged</c> does:
/// <list type="bullet">
/// <item><b>Value</b> (Value &gt;= 0): an 11px count, clamped to a 16-tall pill (single digits stay circular because
/// WinUI's MeasureOverride squares a badge narrower than it is tall).</item>
/// <item><b>Icon</b> (Value &lt; 0, glyph supplied): a Segoe Fluent glyph in the same 16-tall pill, scaled by the WinUI
/// IconPresenter Viewbox to the content-box height (10 for Attention/Informational's 0,4,0,2 padding, ~16 otherwise).</item>
/// <item><b>Dot</b> (neither): a bare 4x4 disc.</item>
/// </list>
/// Background = AccentFillColorDefault (Default), or a SystemFillColor* per <see cref="InfoBadgeSeverity"/>; foreground =
/// TextOnAccentFillColorPrimary. The corner radius is the computed pill (height/2) from
/// <see cref="InfoBadgeTemplateSettings"/>.
/// </summary>
public static class InfoBadge
{
    // WinUI severity Icon-style default glyphs (InfoBadge_themeresources.xaml *IconInfoBadgeStyle IconSource):
    //   Attention      = FontIcon  0xEA38
    //   Informational  = FontIcon  0xF13F
    //   Success        = SymbolIcon "Accept"    (Segoe Fluent 0xE73E == Icons.Accept)
    //   Caution        = SymbolIcon "Important" (0xE171)
    //   Critical       = SymbolIcon "Cancel"    (0xE711 == Icons.Cancel)
    public const string GlyphAttention     = Icons.Attention;  // 0xEA38 (InfoBadge_themeresources.xaml:99)
    public const string GlyphInformational = Icons.StatusInfo; // 0xF13F (:111)
    public const string GlyphSuccess       = Icons.Accept;     // 0xE73E - SymbolIconSource Symbol="Accept" (:122)
    public const string GlyphCaution       = Icons.Important;  // 0xE171 - SymbolIconSource Symbol="Important" (:133)
    public const string GlyphCritical      = Icons.Cancel;     // 0xE711 - SymbolIconSource Symbol="Cancel" (:144)

    /// <summary>WinUI InfoBadge background per severity. Default/Attention follow the live OS accent
    /// (SystemFillColorAttention == SystemAccentColor); Success/Caution/Critical use the saturated SystemFillColor*.
    /// Informational maps to WinUI <c>SystemFillColorSolidNeutralBrush</c> (opaque #9D9D9D dark / #8A8A8A light) ==
    /// <see cref="Tok.SystemFillSolidNeutral"/> — an OPAQUE neutral gray, not the translucent FillControlStrong.</summary>
    private static ColorF SeverityFill(InfoBadgeSeverity severity) => severity switch
    {
        InfoBadgeSeverity.Success       => Tok.SystemFillSuccess,
        InfoBadgeSeverity.Caution       => Tok.SystemFillCaution,
        InfoBadgeSeverity.Critical      => Tok.SystemFillCritical,
        InfoBadgeSeverity.Informational => Tok.SystemFillSolidNeutral, // SystemFillColorSolidNeutral (#9D9D9D/#8A8A8A, opaque)
        InfoBadgeSeverity.Attention     => Tok.SystemFillAttention,    // == AccentDefault
        _                               => Tok.AccentDefault,          // Default
    };

    /// <summary>The bare 4x4 dot. WinUI Dot state (no Value, no Icon): 4x4, corner = 2 (pill at that height).</summary>
    public static BoxEl Dot(ColorF? color = null) => DotSeverity(color);

    /// <summary>Severity dot — same geometry as <see cref="Dot"/> with the severity background.</summary>
    public static BoxEl Dot(InfoBadgeSeverity severity) => DotSeverity(SeverityFill(severity));

    private static BoxEl DotSeverity(ColorF? color)
    {
        var ts = InfoBadgeTemplateSettings.For(value: -1, hasIcon: false);   // Kind 0
        return new BoxEl
        {
            Width = ts.MinWidth,
            Height = ts.Height,
            Corners = CornerRadius4.All(ts.CornerRadius),
            Fill = color ?? Tok.AccentDefault,
        };
    }

    /// <summary>The Value badge: a notification count. WinUI clamps to a 16-tall pill; single digits stay circular via the
    /// MinWidth = Height square. Pass <paramref name="severity"/> to recolor.</summary>
    public static BoxEl Count(int value, ColorF? color = null) =>
        CountCore(value, color ?? Tok.AccentDefault);

    public static BoxEl Count(int value, InfoBadgeSeverity severity) =>
        CountCore(value, SeverityFill(severity));

    private static BoxEl CountCore(int value, ColorF fill)
    {
        var ts = InfoBadgeTemplateSettings.For(value, hasIcon: false);   // Kind 1
        return new BoxEl
        {
            Direction = 0,
            MinWidth = ts.MinWidth,        // 16 — MeasureOverride squares a narrower badge (single digit stays a circle)
            Height = ts.Height,            // 16
            MaxHeight = InfoBadgeTemplateSettings.MaxHeight,
            Corners = CornerRadius4.All(ts.CornerRadius),   // pill: corner = height/2 = 8
            Fill = fill,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(value.ToString())
                {
                    Size = InfoBadgeTemplateSettings.ValueFontSize,   // 11
                    Color = Tok.TextOnAccentPrimary,
                    Margin = ts.ContentMargin,                        // 4,0,4,2
                },
            ],
        };
    }

    /// <summary>The Icon badge: a Segoe Fluent glyph in the 16-tall pill. WinUI FontIcon state (Value &lt; 0, IconSource
    /// present) — the IconPresenter Viewbox scales the glyph to the content-box height (defaults to the full ~16 box
    /// for the ad-hoc, no-severity overloads); FontIcon margin 4,0,4,2.</summary>
    public static BoxEl Icon(string glyph, ColorF? color = null) =>
        IconCore(glyph, color ?? Tok.AccentDefault, InfoBadgeTemplateSettings.IconGlyphSizeFull);

    public static BoxEl Icon(string glyph, InfoBadgeSeverity severity) =>
        IconCore(glyph, SeverityFill(severity), GlyphSize(severity));

    /// <summary>Severity icon using WinUI's default per-severity glyph (Attention 0xEA38, Informational 0xF13F,
    /// Success Accept, Caution Important, Critical Cancel), scaled by the per-style IconPresenter Viewbox target.</summary>
    public static BoxEl Icon(InfoBadgeSeverity severity) =>
        IconCore(DefaultGlyph(severity), SeverityFill(severity), GlyphSize(severity));

    /// <summary>The IconPresenter-Viewbox glyph target height per severity Icon style: Attention/Informational shrink to
    /// 10 (their <c>Padding="0,4,0,2"</c> = 16 − 6), Success/Caution/Critical (and Default) fill the full ~16 box.</summary>
    private static float GlyphSize(InfoBadgeSeverity severity) => severity switch
    {
        InfoBadgeSeverity.Attention     => InfoBadgeTemplateSettings.IconGlyphSizePadded, // Padding 0,4,0,2 → 10
        InfoBadgeSeverity.Informational => InfoBadgeTemplateSettings.IconGlyphSizePadded, // Padding 0,4,0,2 → 10
        _                               => InfoBadgeTemplateSettings.IconGlyphSizeFull,   // full ~16 box
    };

    public static string DefaultGlyph(InfoBadgeSeverity severity) => severity switch
    {
        InfoBadgeSeverity.Informational => GlyphInformational,
        InfoBadgeSeverity.Success       => GlyphSuccess,
        InfoBadgeSeverity.Caution       => GlyphCaution,
        InfoBadgeSeverity.Critical      => GlyphCritical,
        _                               => GlyphAttention,
    };

    private static BoxEl IconCore(string glyph, ColorF fill, float glyphSize)
    {
        var ts = InfoBadgeTemplateSettings.For(value: -1, hasIcon: true);   // Kind 2
        return new BoxEl
        {
            Direction = 0,
            MinWidth = ts.MinWidth,        // 16 (squares to a circle)
            Height = ts.Height,            // 16
            MaxHeight = InfoBadgeTemplateSettings.MaxHeight,
            Corners = CornerRadius4.All(ts.CornerRadius),   // pill: corner = 8
            Fill = fill,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = glyphSize,   // IconPresenter Viewbox target: 10 (Attention/Informational) or ~16 (others)
                    FontFamily = Theme.IconFont,
                    Color = Tok.TextOnAccentPrimary,
                    Margin = ts.ContentMargin,                        // 4,0,4,2 (FontIcon margin)
                },
            ],
        };
    }
}
