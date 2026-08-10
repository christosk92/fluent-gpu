using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Which pane the stage's right-hand region is showing. The signal is <b>static</b> on purpose: the stage's
/// pane choice persists for the SESSION, so re-opening the surface lands on the pane you left it on. It is not a
/// setting — nothing is written to disk — and it is not per-track state either; it is exactly "last pane wins".</summary>
static class StagePane
{
    public const int Lyrics = 0;
    public const int Queue = 1;

    /// <summary>Session-lived. Read it in a Render to subscribe; the two panes stay MOUNTED either way (the switch is
    /// an opacity cross-fade, never a conditional mount — see <c>StagePanes</c>).</summary>
    public static readonly Signal<int> Current = new(Lyrics);
}

/// <summary>
/// The stage's shared chrome: its scrim, its ink and its four button shapes.
///
/// <para><b>THE STAGE IS SINGLE-THEME: always art-dark.</b> In both themes. The room is lit by the playing track, the
/// way every art-forward player lights it, and the whole surface — chrome AND lyrics — paints the theme-INVARIANT
/// <see cref="WaveeOnMedia"/> ink on top of it. There is no light arm anywhere in this file.</para>
///
/// <para><b>What this replaced, and why.</b> The surface shipped with a theme-FLIPPING base scrim (black @ 0.45 dark,
/// white @ 0.62 light) because <c>LyricsView</c> painted <c>Tok.TextPrimary</c>. The chrome could not sit on a white
/// ground, so every chrome region brought its own always-dark BOXED veil — a caption strip, the identity column, the
/// pivot band. In light theme that read as a two-world collage: dark patches with locatable edges (a band across the
/// top, a vertical smear at the column's falloff) over a white ground, with the theme-invariant white title landing on
/// the near-white part and disappearing outright. The lyrics now take an on-media ink (<c>LyricsInk</c>), which is what
/// lets the scrim be ONE continuous thing.</para>
///
/// <para><b>The stack, bottom to top:</b> the opaque <see cref="Tok.MediaStage"/> floor → the σ80 baked-blur artwork →
/// <see cref="Scrim"/> (one full-bleed, continuous vertical gradient: deepened at the top and the bottom, flat through
/// the middle) → <see cref="ColumnShade"/> (one full-bleed, left-anchored layer that deepens the ground under the
/// identity column and feathers to EXACTLY zero over 260 DIP) → content. Two paint layers, no patchwork. Only the
/// QUEUE pane keeps a local shade (<see cref="PaneShade"/>), because it is mounted/cross-faded and its rows carry hover
/// glass that needs a floor while it is up.</para>
///
/// <para><b>Edge-invisibility is a rule, not a taste call.</b> Every shade either reaches its own boundary at alpha 0
/// after a long feather, or ends at a WINDOW edge where there is no outside to contrast with. The numbers live in
/// <see cref="StageLayout"/> (the pure allocator) and <c>StageLayoutTests</c> asserts them in DIP; this file owns only
/// the spelling. Every colour is derived from <see cref="Tok.MediaStage"/> rather than hand-mixed, for the same reason
/// <see cref="WaveeOnMedia"/>'s light-button ramp is derived from the on-media ink: one token moves them all.</para>
/// </summary>
static class StageChrome
{
    // ── the scrim system ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The near-black the whole system is mixed from, at an authored alpha.</summary>
    static ColorF Shade(float a) => Tok.MediaStage with { A = a };

    /// <summary>The caption band's height. It carries the close cluster; it no longer carries a veil of its own — the
    /// scrim's own top deepening does that job, across a feather many times this tall.</summary>
    public const float TopBandH = 88f;

    /// <summary>The pivot band's height. Same story: the row is a row, the darkening under it is the scrim's.</summary>
    public const float PivotBandH = 72f;

    /// <summary>THE scrim — one full-bleed, theme-invariant, continuous vertical gradient over the whole body band.
    /// Deep at the top (the caption cluster), deep at the bottom (the pivot band and the transport), flat at
    /// <see cref="StageLayout.ScrimBaseA"/> through the middle where the lyrics read. The two interior stops carry the
    /// SAME value, which is what makes the middle a plateau rather than a slow sag — and what makes each deepening a
    /// feather hundreds of DIP long instead of a boxed band.</summary>
    public static GradientSpec Scrim() => GradientDown(
        new GradientStop(0f, Shade(StageLayout.ScrimTopA)),
        new GradientStop(StageLayout.ScrimTopStop, Shade(StageLayout.ScrimBaseA)),
        new GradientStop(StageLayout.ScrimBottomStop, Shade(StageLayout.ScrimBaseA)),
        new GradientStop(1f, Shade(StageLayout.ScrimBottomA)));

    /// <summary>The identity column's deepening: left-anchored, held flat across the DESIGNED column so the column's
    /// type never sits on a moving value, then curved to EXACTLY zero across
    /// <see cref="StageLayout.ColumnShadeFalloffW"/>. It is a full-bleed paint layer with no layout consequence, so it
    /// is free to be much wider than the column BOX — which is the entire reason its right edge cannot be found.</summary>
    public static GradientSpec ColumnShade() => GradientRight(
        new GradientStop(0f, Shade(StageLayout.ColumnShadeA)),
        new GradientStop(MathF.Max(0.01f, StageLayout.ColumnShadeHoldStop), Shade(StageLayout.ColumnShadeA)),
        new GradientStop(StageLayout.ColumnShadeMidStop,
            Shade(StageLayout.ColumnShadeA * StageLayout.ColumnShadeMidFrac)),
        new GradientStop(1f, Shade(0f)));

    /// <summary>The QUEUE pane's floor. The queue is a list of rows with hover glass on them, and glass has to sit on
    /// something — so the pane brings its floor with it and takes it away again when it cross-fades out to the lyrics.
    /// It comes up out of ZERO on the pane's left; its deep end is the window edge.</summary>
    public static GradientSpec PaneShade() => GradientRight(
        new GradientStop(0f, Shade(0f)),
        new GradientStop(StageLayout.PaneShadeFeatherStop, Shade(StageLayout.PaneShadeA * 0.4f)),
        new GradientStop(1f, Shade(StageLayout.PaneShadeA)));

    // ── the accent ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The art-derived chrome accent of the PLAYING cover — the same derivation the queue panel and the player
    /// bar's media CTAs use (the lifted, saturation-floored role; the raw grading role can be deliberately dark). The
    /// stage spends it on exactly four jobs: a latched satellite, the saved heart, the pivot underline + the section
    /// rule, and the ∞ when autoplay is on.</summary>
    public static ColorF AccentFor(Track? track) =>
        Surfaces.ChromeSchemeFor(track?.Image?.Url) is { } p ? WaveePalette.ChromeAccent(p) : Tok.AccentDefault;

    // ── the four button shapes ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A PLATELESS on-media glyph button — the stage's default control. Nothing at rest, glass on hover, ink
    /// from the <see cref="WaveeOnMedia"/> ladder. Used for prev/next, the caption cluster, the compact overflow.</summary>
    public static BoxEl Glyph(string glyph, Action? onClick, float box, float glyphSize, bool enabled = true,
                             string? font = null, Action<NodeHandle>? onRealized = null) => new()
    {
        Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Fill = WaveeOnMedia.GlassRest,
        HoverFill = enabled ? WaveeOnMedia.GlassHover : WaveeOnMedia.GlassRest,
        PressedFill = enabled ? WaveeOnMedia.GlassPressed : WaveeOnMedia.GlassRest,
        BrushTransitionMs = WaveeMotion.Faster,
        HoverScale = WaveeMotion.ScaleEmphatic.HoverIf(enabled), PressScale = WaveeMotion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = enabled ? onClick : null,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        OnRealized = onRealized,
        Children =
        [
            new TextEl(glyph)
            {
                Size = glyphSize, FontFamily = font ?? Theme.IconFont,
                Color = enabled ? WaveeOnMedia.InkSecondary : WaveeOnMedia.InkTertiary,
                HoverColor = enabled ? WaveeOnMedia.Ink : WaveeOnMedia.InkTertiary,
            },
        ],
    };

    /// <summary>The on-media SCRIM FAB — the shape for a control that sits directly on ARTWORK rather than on the
    /// scrim's ground: a 40-DIP circle carrying the on-media scrim plate AT REST, the hairline on-media ring, and the
    /// ink ladder's secondary→primary glyph. It is the <c>MediaCard</c> cover-FAB recipe verbatim
    /// (<c>ScrimRest/ScrimHover/ScrimPressed</c> + <see cref="WaveeOnMedia.Stroke"/>), and circles are the sanctioned
    /// on-media shape.
    /// <para>WHY IT EXISTS. <see cref="Glyph"/> is plateless — <c>GlassRest</c> is alpha ZERO — which is correct for a
    /// control standing on the scrim's own deepening (the transport, the column). The surface's TOP band is not that:
    /// it is the thinnest part of the scrim and it sits over whatever the cover happens to be, so a plateless close
    /// button over bright art is an invisible way out. This shape brings its own ground.</para></summary>
    public const float FabBox = 40f;

    /// <inheritdoc cref="FabBox"/>
    public static BoxEl ScrimFab(string glyph, Action onClick, float glyphSize, ColorF accent, bool latched = false,
                                 float box = FabBox) => new()
    {
        Width = box, Height = box, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(box),
        Fill = WaveeOnMedia.ScrimRest,
        HoverFill = WaveeOnMedia.ScrimHover,
        PressedFill = WaveeOnMedia.ScrimPressed,
        BorderWidth = 1f, BorderColor = WaveeOnMedia.Stroke,
        BrushTransitionMs = WaveeMotion.Faster,
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        OnClick = onClick, Cursor = CursorId.Hand,
        Children =
        [
            new TextEl(glyph)
            {
                Size = glyphSize, FontFamily = Theme.IconFont,
                Color = latched ? accent : WaveeOnMedia.InkSecondary,
                HoverColor = latched ? accent : WaveeOnMedia.Ink,
            },
        ],
    };

    /// <summary>A transport SATELLITE (shuffle / repeat) — a toggle, so it speaks the app's wave-4 toggle grammar: a
    /// subtle plate plus an ACCENT glyph while latched, and a completely unpainted box while it is not. Same rule as
    /// <c>PlayerBarContent.Transport</c>, translated to the on-media ladder (glass instead of <c>Tok.FillSubtle*</c>).</summary>
    public static BoxEl Satellite(string glyph, Action onClick, bool enabled, bool latched, ColorF accent,
                                  float box, float glyphSize) => new()
    {
        Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Fill = latched && enabled ? WaveeOnMedia.GlassHover : WaveeOnMedia.GlassRest,
        HoverFill = latched && enabled ? WaveeOnMedia.GlassPressed : enabled ? WaveeOnMedia.GlassHover : WaveeOnMedia.GlassRest,
        PressedFill = enabled ? WaveeOnMedia.GlassPressed : WaveeOnMedia.GlassRest,
        BrushTransitionMs = WaveeMotion.Faster,
        HoverScale = WaveeMotion.ScaleEmphatic.HoverIf(enabled), PressScale = WaveeMotion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children =
        [
            new TextEl(glyph)
            {
                Size = glyphSize, FontFamily = Theme.IconFont,
                Color = !enabled ? WaveeOnMedia.InkTertiary : latched ? accent : WaveeOnMedia.InkSecondary,
                HoverColor = !enabled ? WaveeOnMedia.InkTertiary : latched ? accent : WaveeOnMedia.Ink,
            },
        ],
    };

    /// <summary>THE filled control — the stage's play/pause, and the only plate on the surface that is not a hover
    /// state. It takes the on-media INK as its fill and the engine's opaque media stage as its glyph (the
    /// <see cref="WaveeOnMedia"/> light-button ramp, verbatim), so it reads identically in both themes: the stage is on
    /// media, and on media the loudest affordance is a white disc.</summary>
    public static BoxEl Play(string glyph, Action onClick, bool enabled, float box, float glyphSize) => new()
    {
        Width = box, Height = box, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(box),
        Fill = enabled ? WaveeOnMedia.LightButton : WaveeOnMedia.ScrimRest,
        HoverFill = enabled ? WaveeOnMedia.LightButtonHover : WaveeOnMedia.ScrimRest,
        PressedFill = enabled ? WaveeOnMedia.LightButtonPressed : WaveeOnMedia.ScrimRest,
        BrushTransitionMs = WaveeMotion.Faster,
        HoverScale = WaveeMotion.ScaleEmphatic.HoverIf(enabled), PressScale = WaveeMotion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children =
        [
            new TextEl(glyph)
            {
                Size = glyphSize, FontFamily = Theme.IconFont,
                Color = enabled ? WaveeOnMedia.LightButtonInk : WaveeOnMedia.InkTertiary,
            },
        ],
    };

    /// <summary>The saved HEART — the stage's <c>SaveButton</c> face: a 32-square on the control radius whose glyph is
    /// the accent when the track is in the saved set and on-media secondary when it is not.</summary>
    public static BoxEl Heart(bool saved, Action? onLike, ColorF accent, float box = WaveeCta.IconButtonSize) => new()
    {
        Width = box, Height = box, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Fill = WaveeOnMedia.GlassRest,
        HoverFill = onLike is null ? WaveeOnMedia.GlassRest : WaveeOnMedia.GlassHover,
        PressedFill = onLike is null ? WaveeOnMedia.GlassRest : WaveeOnMedia.GlassPressed,
        BrushTransitionMs = WaveeMotion.Faster,
        HoverScale = WaveeMotion.ScaleEmphatic.HoverIf(onLike is not null),
        PressScale = WaveeMotion.ScaleEmphatic.PressIf(onLike is not null),
        Role = AutomationRole.Button, Focusable = onLike is not null, AllowFocusOnInteraction = false,
        OnClick = onLike, Cursor = onLike is null ? (CursorId?)null : CursorId.Hand,
        BlocksDragArm = true,
        Children =
        [
            new BoxEl
            {
                Key = saved ? "sh:on" : "sh:off",
                Children =
                [
                    new TextEl(saved ? Icons.HeartFill : Icons.Heart)
                    {
                        Size = 16f, FontFamily = Theme.IconFont,
                        Color = saved ? accent : WaveeOnMedia.InkSecondary,
                        HoverColor = saved ? accent : WaveeOnMedia.Ink,
                    },
                ],
            },
        ],
    };

    // ── the pane pivot ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One pivot link — the Zune text-pivot grammar, on the SHARED rung constants
    /// (<see cref="WaveeCta.TextActionSize"/> / <c>TextActionWeight</c> / <c>TextActionLineHeight</c>) and the shared
    /// underline geometry (<see cref="ContextBandLayout.UnderlineHeight"/> / <c>UnderlineGap</c>), translated to the
    /// on-media ink ladder. The underline is ALWAYS mounted and switches COLOUR (transparent when inactive) rather than
    /// being conditionally mounted or flown between items — the same decision, for the same reasons, the context band's
    /// pivot documents.
    /// <para>NOTE: this is deliberately NOT a <c>WaveeCta.TextAction</c> call. That grammar is fenced to context bands
    /// (its own file header says so, and <c>ContextBandLayoutTests.OnlyContextBandSurfaces_CallTextAction</c> enforces
    /// it), and a pivot is a different control anyway — a tab, not an action. What is shared is the RUNG, which is
    /// exactly what the constants are for.</para></summary>
    public static Element PivotLink(string label, bool active, ColorF accent, Action go) => new BoxEl
    {
        Direction = 1, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(ContextBandLayout.PivotPadX, Spacing.XS, ContextBandLayout.PivotPadX, Spacing.XXS),
        Corners = Radii.ControlAll,
        Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, OnClick = go,
        Children =
        [
            new TextEl(label)
            {
                Size = WaveeCta.TextActionSize, LineHeight = WaveeCta.TextActionLineHeight,
                Weight = WaveeCta.TextActionWeight,
                Color = active ? WaveeOnMedia.Ink : WaveeOnMedia.InkTertiary,
                HoverColor = WaveeOnMedia.Ink,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            },
            new BoxEl
            {
                Height = ContextBandLayout.UnderlineHeight, AlignSelf = FlexAlign.Stretch,
                Margin = new Edges4(0f, ContextBandLayout.UnderlineGap, 0f, 0f),
                Fill = active ? accent : ColorF.Transparent,
                BrushTransitionMs = WaveeMotion.Fast,
                HitTestVisible = false,
            },
        ],
    };

    /// <summary>The 20 × 2 accent RULE — the app's section ornament (a rule, never a selection bar; see the accent-role
    /// rules in <c>WaveeTokens</c>).</summary>
    public const float SectionRuleW = 20f;
    /// <inheritdoc cref="SectionRuleW"/>
    public static Element SectionRule(ColorF accent) => new BoxEl
    {
        Width = SectionRuleW, Height = ContextBandLayout.UnderlineHeight, Shrink = 0f,
        Fill = accent, HitTestVisible = false,
    };
}
