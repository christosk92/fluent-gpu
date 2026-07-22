using System.Collections.Generic;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Colors & tokens (G8c2 Design page) ────────────────────────────────────────────────────────────────────────────
// The generated design-token browser: every important Tok.* color as a swatch with copy-as-code, plus a radii ruler and
// a spacing ruler, and the on-accent text set layered over an image (the "content over media" tokens). The point is
// that the app never hard-codes a hex — it reaches for a token, and these are the tokens.
[GalleryPage("colors", "Colors & tokens", "Design", Icon = Icons.Brush)]
sealed class ColorsPage : Component
{
    static readonly (string Name, ColorF Value)[] Accent =
    {
        ("Tok.AccentDefault", Tok.AccentDefault), ("Tok.AccentSecondary", Tok.AccentSecondary),
        ("Tok.AccentTertiary", Tok.AccentTertiary), ("Tok.AccentSubtle", Tok.AccentSubtle),
    };
    static readonly (string Name, ColorF Value)[] Fills =
    {
        ("Tok.FillControlDefault", Tok.FillControlDefault), ("Tok.FillControlSecondary", Tok.FillControlSecondary),
        ("Tok.FillCardDefault", Tok.FillCardDefault), ("Tok.FillLayerDefault", Tok.FillLayerDefault),
        ("Tok.FillSolidBase", Tok.FillSolidBase), ("Tok.FillSubtleSecondary", Tok.FillSubtleSecondary),
    };
    static readonly (string Name, ColorF Value)[] Strokes =
    {
        ("Tok.StrokeControlDefault", Tok.StrokeControlDefault), ("Tok.StrokeCardDefault", Tok.StrokeCardDefault),
        ("Tok.StrokeDividerDefault", Tok.StrokeDividerDefault), ("Tok.StrokeSurfaceDefault", Tok.StrokeSurfaceDefault),
    };
    static readonly (string Name, ColorF Value)[] System =
    {
        ("Tok.SystemFillCritical", Tok.SystemFillCritical), ("Tok.SystemFillCaution", Tok.SystemFillCaution),
        ("Tok.SystemFillSuccess", Tok.SystemFillSuccess), ("Tok.SystemFillCriticalBackground", Tok.SystemFillCriticalBackground),
        ("Tok.SystemFillCautionBackground", Tok.SystemFillCautionBackground), ("Tok.SystemFillSuccessBackground", Tok.SystemFillSuccessBackground),
    };
    static readonly (string Name, ColorF Value)[] Texts =
    {
        ("Tok.TextPrimary", Tok.TextPrimary), ("Tok.TextSecondary", Tok.TextSecondary),
        ("Tok.TextTertiary", Tok.TextTertiary), ("Tok.TextDisabled", Tok.TextDisabled),
    };
    static readonly (string Name, float Value)[] RadiiScale =
    {
        ("Radii.None", Radii.None), ("Radii.Control", Radii.Control), ("Radii.Overlay", Radii.Overlay), ("Radii.Pill", Radii.Pill),
    };
    static readonly (string Name, float Value)[] SpaceScale =
    {
        ("XXS", Spacing.XXS), ("XS", Spacing.XS), ("S", Spacing.S), ("M", Spacing.M),
        ("L", Spacing.L), ("XL", Spacing.XL), ("XXL", Spacing.XXL), ("XXXL", Spacing.XXXL),
    };

    public override Element Render() => GalleryPage.ShellKeyed("colors", "Colors & tokens",
        "Every surface, stroke and text color in the engine is a token — a theme-aware, palette-driven value the controls " +
        "read, never a hard-coded hex. Copy any token as code and drop it straight into an Element. Radii and spacing are " +
        "tokens too.",
        Section("Accent", Accent),
        Section("Fills", Fills),
        Section("Strokes", Strokes),
        Section("System (severity)", System),
        TextSection("Text", Texts),
        RadiiRuler(),
        SpacingRuler(),
        OnMediaCard());

    static Element Section(string title, (string Name, ColorF Value)[] tokens)
    {
        var tiles = new Element[tokens.Length];
        for (int i = 0; i < tokens.Length; i++) tiles[i] = Swatch(tokens[i].Name, tokens[i].Value);
        return Group(title, AutoGrid(230f, 12f, float.NaN, tiles));
    }

    static Element Swatch(string name, ColorF value) => new BoxEl
    {
        Direction = 1, Gap = 8f, Padding = Edges4.All(10), Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
        Children =
        [
            new BoxEl { Height = 44f, Corners = Radii.ControlAll, Fill = value, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f },
            new BoxEl
            {
                Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                Children =
                [
                    new BoxEl { Grow = 1f, Children = [new TextEl(name) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Trim = TextTrim.CharacterEllipsis, MaxLines = 1 }] },
                    Embed.Comp(() => new CopyButton { Text = name, Glyph = Icons.Copy, Tip = "Copy as code" }),
                ],
            },
        ],
    };

    static Element TextSection(string title, (string Name, ColorF Value)[] tokens)
    {
        var rows = new Element[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var (name, value) = tokens[i];
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, MinHeight = 36f,
                Children =
                [
                    new BoxEl { Width = 220f, Children = [new TextEl("The quick brown fox") { Size = 16f, Color = value }] },
                    new BoxEl { Grow = 1f, Children = [new TextEl(name) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" }] },
                    Embed.Comp(() => new CopyButton { Text = name, Glyph = Icons.Copy, Tip = "Copy as code" }),
                ],
            };
        }
        return Group(title, new BoxEl { Direction = 1, Gap = 4f, Children = rows });
    }

    static Element RadiiRuler()
    {
        var tiles = new Element[RadiiScale.Length];
        for (int i = 0; i < RadiiScale.Length; i++)
        {
            var (name, r) = RadiiScale[i];
            tiles[i] = new BoxEl
            {
                Direction = 1, Gap = 8f, AlignItems = FlexAlign.Center,
                Children =
                [
                    new BoxEl { Width = 72f, Height = 72f, Corners = CornerRadius4.All(r), Fill = Tok.AccentSubtle, BorderColor = Tok.AccentDefault, BorderWidth = 1f },
                    new TextEl($"{name} ({r:0})") { Size = 11.5f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
                ],
            };
        }
        return Group("Corner radii", new BoxEl { Direction = 0, Gap = 20f, Wrap = true, Children = tiles });
    }

    static Element SpacingRuler()
    {
        var rows = new Element[SpaceScale.Length];
        for (int i = 0; i < SpaceScale.Length; i++)
        {
            var (name, v) = SpaceScale[i];
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, MinHeight = 28f,
                Children =
                [
                    new BoxEl { Width = 96f, Children = [new TextEl($"Spacing.{name}") { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" }] },
                    new BoxEl { Width = v, Height = 16f, Corners = Radii.ControlAll, Fill = Tok.AccentDefault },
                    new TextEl($"{v:0}") { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" },
                ],
            };
        }
        return Group("Spacing scale", new BoxEl { Direction = 1, Gap = 4f, Children = rows });
    }

    static Element OnMediaCard() => Group("Content over media (on-accent set)", new BoxEl
    {
        Height = 160f, Corners = Radii.OverlayAll, ClipToBounds = true,
        Fill = Tok.AccentDefault, Justify = FlexJustify.End, Direction = 1, Padding = Edges4.All(16),
        Children =
        [
            new TextEl("Now playing") { Size = 13f, Color = Tok.TextOnAccentSecondary },
            new TextEl("On-accent text set") { Size = 22f, Bold = true, Color = Tok.TextOnAccentPrimary },
            new TextEl("Tok.TextOnAccentPrimary / Secondary stay legible over an accent or image surface.") { Size = 12.5f, Color = Tok.TextOnAccentSecondary, Wrap = TextWrap.Wrap },
        ],
    });

    static Element Group(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = 12f, Margin = new Edges4(0, 8, 0, 0),
        Children = [Subtitle(title), body],
    };
}
