using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

// The one primary call-to-action skin: a styled stock engine Button, NOT a hand-rolled box. Every LABELED Play/Resume/
// Shuffle CTA on a MEDIA surface routes through here, so it inherits Button's internals verbatim — the keyboard focus
// ring (FocusVisualMargin −3), AutomationRole.Button, the Space/Enter key mechanics, the ButtonPalette color seam that
// carries an artwork-derived accent, and the 83ms WinUI brush ramp on every state flip.
//
// What this skin substitutes on top of those internals is the media PERSONALITY, and only these five values:
//   · CornerRadius Radii.Full  — a capsule (the engine clamps to half the box, so 18 at the 36 height)
//   · MinHeight 36             — one step above the 32 control ladder, matching the Follow pill it sits beside
//   · Padding 18,6,18,7        — the wider capsule waist; the 1px bottom bias is the optical baseline nudge
//   · Bold label               — Style exposes no numeric weight, only Bold (=700); a Regular 400 label does not read
//                                as the page's primary action next to 32-48px hero type
//   · HoverScale/PressScale/Cursor.Hand — WinUI Button has NO scale cue and keeps the arrow; a media CTA over artwork
//                                does, and that divergence is CONFINED to this skin.
// UTILITY surfaces (settings, dialogs, empty-state actions) must keep stock Fluent rectangles by calling Button.*
// directly — this pill is for media primaries only.
//
// This is also NOT the skin for circular play FABs sitting ON artwork: those keep their round geometry and their own
// scale cues (MediaCard.PlayFab, the album-expand / episode / search circles, the hero Shuffle Fab, video overlays).
static class WaveeCta
{
    /// <summary>The media pill's height. One step above the 32-DIP control ladder so a labeled media primary reads as
    /// the page's dominant action; also the height of the Follow pill it shares hero rows with.</summary>
    public const float PillHeight = 36f;

    const float PillHoverScale = 1.04f;
    const float PillPressScale = 0.97f;

    /// <summary>The primary Play CTA on an artwork-derived <paramref name="accent"/>. <paramref name="label"/> defaults
    /// to the shared detail-surface "Play" string; surfaces with their own wording pass it.</summary>
    public static BoxEl Play(ColorF accent, Action onClick, string? label = null)
        => Accent(label ?? Loc.Get(Strings.Detail.Play), accent, onClick);

    /// <summary>A labeled primary CTA on an arbitrary (usually artwork-derived) fill. <paramref name="ink"/> overrides
    /// the WCAG-picked on-fill ink for a surface that has already resolved it.</summary>
    public static BoxEl Accent(string label, ColorF accent, Action onClick, string? glyph = null, ColorF? ink = null,
        float minHeight = PillHeight)
        => Pill(label, onClick, palette: Palette(accent, ink), glyph: glyph ?? Icons.Play, minHeight: minHeight);

    /// <summary>The media pill on an explicit palette (a photo-local white ramp, an immersive white-on-media pair) or on
    /// a stock appearance ramp when <paramref name="palette"/> is null — a labeled neutral secondary passes
    /// <see cref="ButtonAppearance.Standard"/> and no palette. <paramref name="minHeight"/> lets a surface that owns its
    /// own control ladder (the vertical hero's three orientations) keep its slot height: <c>Style.MinHeight</c> is a
    /// FLOOR, so a call site declaring <c>Height</c> below the default 36 would otherwise still measure 36.</summary>
    public static BoxEl Pill(string label, Action onClick, ButtonAppearance appearance = ButtonAppearance.Accent,
        Button.ButtonPalette? palette = null, string? glyph = null, float minHeight = PillHeight)
        => Button.Create(label, onClick, appearance, glyph: glyph,
            // Style and palette are NOT independent parameters on Button.Create — a supplied style WINS and the palette
            // argument is dropped (Button.cs: `style ?? DefaultStyle(appearance, cs, palette)`). So the palette must ride
            // INSIDE the style: DefaultStyle folds it into the 24-member record, and the pill geometry is a `with` on
            // top. Everything not listed stays on the stock ladder (BorderWidth 1, 14px, center alignment, focus
            // margin −3, 83ms brush).
            style: Button.DefaultStyle(appearance, palette: palette) with
            {
                CornerRadius = Radii.Full,
                MinHeight = minHeight,
                Padding = new Edges4(18f, 6f, 18f, 7f),
                Bold = true,
            }) with
        {
            HoverScale = PillHoverScale,
            PressScale = PillPressScale,
            Cursor = CursorId.Hand,
        };

    /// <summary>The stock <c>ButtonPalette.For(ButtonAppearance.Accent)</c> ramp with <paramref name="fill"/>
    /// substituted for AccentFillColorDefault. Shades mirror Tok's AccentFillShade exactly (the SAME color at @0.90 /
    /// @0.80 alpha for Secondary/Tertiary); the disabled legs stay on the system tokens, because a disabled control
    /// must not advertise the page's accent. Border = AccentControlElevationBorder for rest/hover, transparent for
    /// pressed/disabled; BackgroundSizing = OuterBorderEdge — the AccentButtonStyle setters.</summary>
    public static Button.ButtonPalette Palette(ColorF fill, ColorF? ink = null, GradientSpec? border = null)
    {
        ColorF fg = ink ?? ColorContrast.PickContrast(fill);
        GradientSpec? rest = border ?? Tok.AccentControlElevationBorder;
        var transparent = GradientSpec.Solid(ColorF.Transparent);
        return new Button.ButtonPalette(
            Background: new StateBrush(fill, fill with { A = 0.90f }, fill with { A = 0.80f }, Tok.AccentDisabled),
            Foreground: new StateBrush(fg, fg, fg with { A = OnFillSecondaryAlpha(fg) }, Tok.TextOnAccentDisabled),
            Border: new Button.BorderRamp(rest, rest, transparent, transparent),
            Sizing: BackgroundSizing.OuterBorderEdge);
    }

    // WinUI TextOnAccentFillColorSecondary (the PRESSED label): the primary on-accent ink dimmed — 0x80 where that ink
    // is dark (PaletteBuilder's near-black stop), 0xB3 where it is light (white). Keyed off the ink's LUMINANCE, not off
    // Tok.Theme (an artwork accent can invert the ink against the theme) and not off token equality (callers may pass an
    // explicit ink, e.g. pure black, that is not the NearBlackInk constant).
    static float OnFillSecondaryAlpha(in ColorF ink)
        => ColorContrast.RelativeLuminance(ink) < 0.5f ? 0x80 / 255f : 0xB3 / 255f;
}
