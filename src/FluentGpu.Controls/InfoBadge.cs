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
/// <param name="ContentMargin">Per-kind inner margin: Value/FontIcon = 4,0,4,2; a non-font IconElement = 4,4,4,4
/// (IconInfoBadgeIconMargin, InfoBadge_themeresources.xaml:16).</param>
public readonly record struct InfoBadgeTemplateSettings(
    byte Kind, float Height, float MinWidth, float CornerRadius, Edges4 ContentMargin)
{
    // WinUI InfoBadge_themeresources.xaml (Default theme dictionary):
    public const float MinDim = 4f;          // InfoBadgeMinHeight / InfoBadgeMinWidth
    public const float MaxHeight = 16f;      // InfoBadgeMaxHeight

    /// <summary>WinUI renders the icon inside a <c>Viewbox</c> (IconPresenter, VerticalAlignment=Stretch) that fills
    /// the RootGrid content box, so the glyph is scaled to the box HEIGHT, not a fixed font size. The box height is
    /// 16 minus the RootGrid Padding AND the IconPresenter margin: the FontIcon state margin is 4,0,4,2
    /// (IconInfoBadgeFontIconMargin, InfoBadge_themeresources.xaml:14, applied at :71) — the Attention/Informational
    /// Icon STYLES additionally pad the root <c>0,4,0,2</c> (:96/:108) → 16 − (4+2) − (0+2) = 8 tall, while an unpadded
    /// FontIcon keeps 16 − (0+2) = 14; the non-font 'Icon' state (SymbolIconSource) margin is 4,4,4,4
    /// (IconInfoBadgeIconMargin, :16, applied at :65) → 16 − (4+4) = 8. We reproduce that by driving the glyph
    /// <c>Size</c> off these per-state target heights.</summary>
    public const float IconGlyphSizePadded = 8f;   // Attention/Informational FontIcon: 16 − Padding(4+2) − margin(0+2) = 8
    public const float IconGlyphSizeFull   = 14f;  // unpadded FontIcon: 16 − margin(0+2) = 14
    public const float IconGlyphSizeSymbol = 8f;   // SymbolIcon 'Icon' state: 16 − margin(4+4) = 8
    public const float ValueFontSize       = 11f;  // InfoBadgeValueFontSize

    // ValueInfoBadgeTextMargin / IconInfoBadgeFontIconMargin = 4,0,4,2 (InfoBadge_themeresources.xaml:14-15,
    // identical in every theme dictionary).
    public static readonly Edges4 ContentMarginValueOrFontIcon = new(4f, 0f, 4f, 2f);
    // IconInfoBadgeIconMargin = 4,4,4,4 — the non-font 'Icon' state (InfoBadge_themeresources.xaml:16, applied :65).
    // Also the EFFECTIVE insets of the padded FontIcon severities: root Padding 0,4,0,2 + FontIcon margin 4,0,4,2.
    public static readonly Edges4 ContentMarginSymbolIcon = new(4f, 4f, 4f, 4f);

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
/// IconPresenter Viewbox to the content-box height (8 for the padded Attention/Informational FontIcon styles and the
/// SymbolIcon severities, 14 for an unpadded FontIcon — see <see cref="InfoBadgeTemplateSettings"/>).</item>
/// <item><b>Dot</b> (neither): a bare 4x4 disc.</item>
/// </list>
/// Background = AccentFillColorDefault (Default), or a SystemFillColor* per <see cref="InfoBadgeSeverity"/>; foreground =
/// TextOnAccentFillColorPrimary. The corner radius is the computed pill (height/2) from
/// <see cref="InfoBadgeTemplateSettings"/>.
/// </summary>
public static class InfoBadge
{
    // Template parts (see TemplateParts). The part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The badge pill itself (WinUI RootGrid — the control's only named element). Owned: Children (the
    /// value text / icon glyph — the display-kind content; the Dot has none). The per-kind geometry and severity
    /// fill are stock per-render styling a modifier may override.</summary>
    public const string PartRoot = "Root";

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
    public static BoxEl Dot(ColorF? color = null, TemplateParts? parts = null) => DotSeverity(color, parts);

    /// <summary>Severity dot — same geometry as <see cref="Dot"/> with the severity background.</summary>
    public static BoxEl Dot(InfoBadgeSeverity severity, TemplateParts? parts = null) => DotSeverity(SeverityFill(severity), parts);

    private static BoxEl DotSeverity(ColorF? color, TemplateParts? parts)
    {
        var ts = InfoBadgeTemplateSettings.For(value: -1, hasIcon: false);   // Kind 0
        // Parts: the Dot has no children — everything on it is open to the modifier.
        return parts.Apply(PartRoot, new BoxEl
        {
            Width = ts.MinWidth,
            Height = ts.Height,
            Corners = CornerRadius4.All(ts.CornerRadius),
            Fill = color ?? Tok.AccentDefault,
        });
    }

    /// <summary>The Value badge: a notification count. WinUI clamps to a 16-tall pill; single digits stay circular via the
    /// MinWidth = Height square. Pass <paramref name="severity"/> to recolor.</summary>
    public static BoxEl Count(int value, ColorF? color = null, TemplateParts? parts = null) =>
        CountCore(value, color ?? Tok.AccentDefault, parts);

    public static BoxEl Count(int value, InfoBadgeSeverity severity, TemplateParts? parts = null) =>
        CountCore(value, SeverityFill(severity), parts);

    private static BoxEl CountCore(int value, ColorF fill, TemplateParts? parts)
    {
        // WinUI: Value < -1 throws hresult_out_of_bounds (InfoBadge.cpp:44-49); Value == -1 with no IconSource goes to
        // the bare Dot state — OnDisplayKindPropertiesChanged only shows the value text for Value >= 0
        // (InfoBadge.cpp:59-82). Never emit text into the 4x4 dot geometry.
        if (value < 0)
            return DotSeverity(fill, parts);

        var ts = InfoBadgeTemplateSettings.For(value, hasIcon: false);   // Kind 1
        var root = new BoxEl
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
        // Parts: restyle the pill; the value text (the display-kind content) always wins.
        return parts.Apply(PartRoot, root) with { Children = root.Children };
    }

    /// <summary>The Icon badge: a Segoe Fluent glyph in the 16-tall pill. A custom glyph is WinUI's FontIconSource →
    /// the FontIcon state (margin 4,0,4,2, InfoBadge_themeresources.xaml:14/:71) — the IconPresenter Viewbox scales the
    /// glyph to the content-box height (14 = 16 − 2 for the ad-hoc, no-severity overloads; the Attention/Informational
    /// severity styles pad the root 0,4,0,2 → 8).</summary>
    public static BoxEl Icon(string glyph, ColorF? color = null, TemplateParts? parts = null) =>
        IconCore(glyph, color ?? Tok.AccentDefault, InfoBadgeTemplateSettings.IconGlyphSizeFull,
                 InfoBadgeTemplateSettings.ContentMarginValueOrFontIcon, parts);

    public static BoxEl Icon(string glyph, InfoBadgeSeverity severity, TemplateParts? parts = null) =>
        IconCore(glyph, SeverityFill(severity), FontIconGlyphSize(severity), FontIconGlyphMargin(severity), parts);

    /// <summary>Severity icon using WinUI's default per-severity IconSource (Attention 0xEA38, Informational 0xF13F —
    /// FontIconSource; Success Accept, Caution Important, Critical Cancel — SymbolIconSource), scaled by the per-state
    /// IconPresenter Viewbox target (8 in every severity style; see <see cref="InfoBadgeTemplateSettings"/>).</summary>
    public static BoxEl Icon(InfoBadgeSeverity severity, TemplateParts? parts = null) =>
        IconCore(DefaultGlyph(severity), SeverityFill(severity), DefaultGlyphSize(severity), DefaultGlyphMargin(severity), parts);

    // A custom glyph is a FontIconSource → always the 'FontIcon' state (margin 4,0,4,2, InfoBadge_themeresources.xaml
    // :14/:71); only the Attention/Informational severity STYLES add root Padding 0,4,0,2 (:96/:108) → content box 8
    // with effective insets 4,4,4,4 — the other styles keep Padding 0 → box 14.
    private static float FontIconGlyphSize(InfoBadgeSeverity severity) => severity switch
    {
        InfoBadgeSeverity.Attention or InfoBadgeSeverity.Informational => InfoBadgeTemplateSettings.IconGlyphSizePadded, // 8
        _                                                              => InfoBadgeTemplateSettings.IconGlyphSizeFull,   // 14
    };

    private static Edges4 FontIconGlyphMargin(InfoBadgeSeverity severity) => severity switch
    {
        // Root Padding 0,4,0,2 + FontIcon margin 4,0,4,2 fold to 4,4,4,4 (our pill box has no separate padding).
        InfoBadgeSeverity.Attention or InfoBadgeSeverity.Informational => InfoBadgeTemplateSettings.ContentMarginSymbolIcon,
        _                                                              => InfoBadgeTemplateSettings.ContentMarginValueOrFontIcon,
    };

    // The STOCK per-severity IconSource: Attention/Informational are FontIconSource + style Padding 0,4,0,2 → box 8,
    // effective insets 4,4,4,4 (InfoBadge_themeresources.xaml:96-99/:108-111); Success/Caution/Critical are
    // SymbolIconSource (:122/:133/:144) → the non-font 'Icon' state → IconInfoBadgeIconMargin 4,4,4,4 (:16/:65), box
    // 16 − 8 = 8. Default has no stock IconSource in WinUI — treat it as an unpadded FontIcon (box 14).
    private static float DefaultGlyphSize(InfoBadgeSeverity severity) =>
        severity == InfoBadgeSeverity.Default
            ? InfoBadgeTemplateSettings.IconGlyphSizeFull     // 14
            : InfoBadgeTemplateSettings.IconGlyphSizeSymbol;  // 8 (== the padded FontIcon box for Attention/Informational)

    private static Edges4 DefaultGlyphMargin(InfoBadgeSeverity severity) =>
        severity == InfoBadgeSeverity.Default
            ? InfoBadgeTemplateSettings.ContentMarginValueOrFontIcon   // 4,0,4,2
            : InfoBadgeTemplateSettings.ContentMarginSymbolIcon;       // 4,4,4,4

    public static string DefaultGlyph(InfoBadgeSeverity severity) => severity switch
    {
        InfoBadgeSeverity.Informational => GlyphInformational,
        InfoBadgeSeverity.Success       => GlyphSuccess,
        InfoBadgeSeverity.Caution       => GlyphCaution,
        InfoBadgeSeverity.Critical      => GlyphCritical,
        _                               => GlyphAttention,
    };

    private static BoxEl IconCore(string glyph, ColorF fill, float glyphSize, Edges4 contentMargin, TemplateParts? parts)
    {
        var ts = InfoBadgeTemplateSettings.For(value: -1, hasIcon: true);   // Kind 2
        var root = new BoxEl
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
                    Size = glyphSize,   // IconPresenter Viewbox target: 8 (padded FontIcon / SymbolIcon) or 14 (unpadded FontIcon)
                    FontFamily = Theme.IconFont,
                    Color = Tok.TextOnAccentPrimary,
                    Margin = contentMargin,   // FontIcon 4,0,4,2 / 'Icon' (symbol) 4,4,4,4 — per the display state
                },
            ],
        };
        // Parts: restyle the pill; the glyph (the display-kind content) always wins.
        return parts.Apply(PartRoot, root) with { Children = root.Children };
    }
}
