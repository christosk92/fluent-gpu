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
/// The stage's shared chrome: its veils, its ink and its four button shapes.
///
/// <para><b>Why the stage carries its own veils.</b> <c>ImmersiveLyricsSurface</c>'s base scrim FLIPS with the theme —
/// black @ 0.45 in dark, white @ 0.62 in light — because the lyrics column it was built for paints
/// <c>Tok.TextPrimary</c>, which flips too. The stage's chrome does not: it is ON MEDIA, so it paints the
/// theme-INVARIANT <see cref="WaveeOnMedia"/> ink (white in both themes), and white ink over a white scrim is nothing at
/// all. So every region that carries stage chrome carries its own always-dark VEIL — the caption strip, the identity
/// column, and the pane region's bottom pivot band — while the lyrics column keeps the base scrim untouched and keeps
/// reading in both themes. The veils are scrims (a gradient over artwork), not plates: the stage's material rule is
/// "no plates except hover glass, the filled play, and the latched-satellite plate", and a gradient that fades to
/// nothing is not a plate.</para>
///
/// <para>Every veil colour is derived from <see cref="Tok.MediaStage"/> (the engine's opaque media near-black) rather
/// than from a hand-mixed RGBA, for the same reason <see cref="WaveeOnMedia"/>'s light-button ramp is derived from the
/// on-media ink: one token moves them all.</para>
/// </summary>
static class StageChrome
{
    // ── the veil ladder ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The veil at full strength — under the identity column's type and under the pivot.</summary>
    public static ColorF VeilDeep => Tok.MediaStage with { A = 0.74f };
    /// <summary>The veil's mid stop, so the falloff is a curve rather than a ramp.</summary>
    public static ColorF VeilMid => Tok.MediaStage with { A = 0.40f };
    /// <summary>Gone — the base scrim takes over here.</summary>
    public static ColorF VeilClear => Tok.MediaStage with { A = 0f };

    /// <summary>The caption-strip veil's height. Tall enough to carry the close cluster with the fade fully resolved
    /// before the lyrics column's first line.</summary>
    public const float TopVeilH = 88f;

    /// <summary>The pivot band's height — the bottom veil IS the pivot row, so there is no separately-anchored layer to
    /// keep in sync with it.</summary>
    public const float PivotBandH = 72f;

    /// <summary>Top-anchored: deep at the window edge, gone by <see cref="TopVeilH"/>.</summary>
    public static GradientSpec TopVeil() => GradientDown(
        new GradientStop(0f, VeilDeep), new GradientStop(0.55f, VeilMid), new GradientStop(1f, VeilClear));

    /// <summary>Bottom-anchored: the exact mirror of <see cref="TopVeil"/>.</summary>
    public static GradientSpec BottomVeil() => GradientDown(
        new GradientStop(0f, VeilClear), new GradientStop(0.45f, VeilMid), new GradientStop(1f, VeilDeep));

    /// <summary>Left-anchored, held flat across the DESIGNED column and then faded across its falloff — so the column's
    /// type never sits on a moving value and the veil still has no edge. <paramref name="holdStop"/> is
    /// <see cref="StageLayout.VeilHoldStop"/>.</summary>
    public static GradientSpec ColumnVeil(float holdStop) => LinearGradient(0f,
        new GradientStop(0f, VeilDeep),
        new GradientStop(MathF.Max(0.01f, holdStop), VeilDeep),
        new GradientStop(1f, VeilClear));

    /// <summary>The COMPACT header's veil. That header spans the whole width, so there is no horizontal falloff to
    /// author — it holds deep under the type and gives way DOWNWARD into the pane below it.</summary>
    public static GradientSpec HeaderVeil() => GradientDown(
        new GradientStop(0f, VeilDeep), new GradientStop(0.7f, VeilDeep), new GradientStop(1f, VeilMid));

    /// <summary>The queue pane's own veil. The queue is a list of rows with hover glass on them, and a row's glass has
    /// to sit on something dark or it inverts in light theme — so the pane brings its floor with it, and takes it away
    /// again when it cross-fades out to the lyrics.</summary>
    public static GradientSpec PaneVeil() => LinearGradient(0f,
        new GradientStop(0f, VeilClear), new GradientStop(0.18f, VeilMid), new GradientStop(1f, VeilDeep));

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
