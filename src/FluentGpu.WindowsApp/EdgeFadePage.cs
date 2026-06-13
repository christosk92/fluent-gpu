using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── The "Edge fade" gallery page ─────────────────────────────────────────────────────────────────────────────────────
// A live showcase of the edge-fade rendering subsystem (LayerKind.EdgeFade): feather an element's content alpha to
// transparent (+ optional blur) over a band near chosen edges, so it dissolves into whatever is behind. The feather
// FOLLOWS the rounded corners (the curve). Customizable per-edge / falloff / intensity / mode. Realized as one offscreen
// layer per faded element, composited with a per-edge distance feather (per-corner arc) in the opacity compositor.
sealed class EdgeFadePage : Component
{
    static readonly ColorF Accent  = ColorF.FromRgba(0x4C, 0x8B, 0xF5);
    static readonly ColorF Stage   = ColorF.FromRgba(0x16, 0x16, 0x1A);
    static readonly ColorF Cell    = ColorF.FromRgba(0x2A, 0x2A, 0x30);
    static readonly ColorF Panel   = ColorF.FromRgba(0x2A, 0x2E, 0x3A);
    static readonly ColorF Muted   = ColorF.FromRgba(0xB0, 0xB6, 0xC4);
    static readonly ColorF RowText = ColorF.FromRgba(0xF2, 0xF2, 0xF6);

    static readonly string[] Months =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    // A dark stage so the bright card's edge feather dissolves into visible contrast.
    static Element OnStage(Element child) => new BoxEl
    {
        Direction = 1, Grow = 0, Padding = Edges4.All(28), Fill = Stage, Corners = CornerRadius4.All(8f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [child],
    };

    // A real card (title + body lines) that dissolves at its edges — like content leaving the viewport. A MODERATE band
    // keeps a crisp, readable core with only the edges feathering away (this is a pure alpha fade — no Gaussian blur).
    static Element Card(EdgeFadeSpec spec, float corners = 16f) => new BoxEl
    {
        Width = 300, Height = 176, Grow = 0, Direction = 1, Gap = 7f, Padding = Edges4.All(20),
        Fill = Panel, Corners = CornerRadius4.All(corners), EdgeFade = spec,
        Children =
        [
            new TextEl("Nebula Dynamics") { Size = 16f, Weight = 700, Color = RowText },
            new TextEl("Client confirmed the budget.") { Size = 13f, Color = Muted },
            new TextEl("Concern: migration timing.") { Size = 13f, Color = Muted },
            new TextEl("Needs a technical review.") { Size = 13f, Color = Muted },
        ],
    };

    public override Element Render()
    {
        // Horizontal auto-fade scroller: content wider than its viewport; only the OVERFLOWING left/right edges feather,
        // ramped with the scroll offset (the cells dissolve into the dark stage behind them).
        var cells = new Element[Months.Length];
        for (int i = 0; i < Months.Length; i++)
            cells[i] = new BoxEl
            {
                Direction = 1, Width = 104, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(0, 20, 0, 20), Margin = new Edges4(0, 0, 8, 0),
                Fill = Cell, Corners = CornerRadius4.All(8f),
                Children = [ new TextEl(Months[i]) { Size = 13f, Color = RowText } ],
            };
        var hScroller = new BoxEl
        {
            Direction = 1, Width = 420, Grow = 0, Fill = Stage, Corners = CornerRadius4.All(10f), Padding = Edges4.All(8),
            Children = [ ScrollView(HStack(0f, cells), horizontal: true) with { AutoEdgeFade = true, EdgeCues = ScrollEdgeCues.None } ],
        };

        return GalleryPage.ShellKeyed("edge-fade", "Edge fade",
            "Feather an element's content alpha to transparent — and optionally blur it — over a band near chosen edges, so " +
            "it dissolves into whatever is behind: horizontal scrollers, cards leaving the viewport, panels. Unlike a " +
            "surface-colour overlay it fades into ANY background (gradients, images, acrylic). The feather FOLLOWS the " +
            "rounded corners (the curve) — where a corner's two adjacent edges both fade, the band hugs the corner arc. " +
            "Realized as one offscreen layer per faded element (LayerKind.EdgeFade), composited with a per-edge distance " +
            "feather; opt in via EdgeFade on any BoxEl/ScrollEl, or AutoEdgeFade on a scroller.",
            ControlExample.Build("Perimeter — follows the rounded corners",
                OnStage(Card(EdgeFadeSpec.Perimeter(26f), 16f)),
                description: "Every edge feathers inward; the band hugs each rounded corner's arc, so the card dissolves cleanly around its curve. The core stays crisp — it's a pure alpha fade, not a blur.",
                code: "new BoxEl { Corners = CornerRadius4.All(16),\n            EdgeFade = EdgeFadeSpec.Perimeter(26) }"),
            ControlExample.Build("Fade + blur",
                OnStage(Card(new EdgeFadeSpec(EdgeMask.All, 26f, 26f, 26f, 26f, FadeFalloff.Smoothstep, 1f, EdgeFadeMode.FadeAndBlur, 6f), 16f)),
                description: "The same feather over a Gaussian-blurred layer — THIS one softens the whole card (blur and fade read as one soft dissolve).",
                code: "EdgeFade = new EdgeFadeSpec(EdgeMask.All, 26, ...,\n            EdgeFadeMode.FadeAndBlur, blurSigma: 6)"),
            ControlExample.Build("Directional — bottom only",
                OnStage(Card(new EdgeFadeSpec(EdgeMask.Bottom, 48f), 16f)),
                description: "Only the chosen edges fade — here the bottom (a card sliding out of view). Top / left / right stay crisp.",
                code: "EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 48)"),
            ControlExample.Build("Auto on a scroller — only the overflowing edges",
                OnStage(hScroller),
                description: "A scroller feathers only the edges that currently overflow, ramped with the scroll offset — the discoverable-overflow affordance as a true alpha fade (the content dissolves, not a surface gradient). Scroll the strip with the wheel.",
                code: "ScrollView(strip, horizontal: true)\n    with { AutoEdgeFade = true }"));
    }
}
