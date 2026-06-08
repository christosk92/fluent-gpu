using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── RichTextBlock / Canvas / ToolTip / CommandBarFlyout demo pages (batch 6) ──────────

sealed class RichTextBlockPage : Component
{
    static readonly string[] Paras =
    {
        "FluentGpu is a from-scratch, GPU-rendered UI engine for .NET — the React/Reactor programming model over a custom Direct3D 12 renderer.",
        "Controls are immutable element records composed by stateless factories and components, reconciled into a retained scene and recorded to a POD draw-list.",
        "This RichTextBlock is a column of body paragraphs — read-only, formatted text laid out by the engine's flex layout.",
    };
    public override Element Render() => GalleryPage.Shell("RichTextBlock",
        "Displays read-only rich text with multiple paragraphs and inline formatting.",
        ControlExample.Build("An article", RichTextBlock.Article("About fluent-gpu", Paras)));
}

sealed class CanvasPage : Component
{
    static Element Dot(ColorF c) => new BoxEl { Width = 48, Height = 48, Corners = Radii.Circle(48), Fill = c };

    public override Element Render() => GalleryPage.Shell("Canvas",
        "A panel that positions its children by explicit X/Y coordinates.",
        ControlExample.Build("A Canvas",
            new BoxEl
            {
                Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, ClipToBounds = true,
                Children =
                [
                    Canvas.Create(380, 180, new[]
                    {
                        new CanvasChild(20, 20, Dot(Tok.AccentDefault)),
                        new CanvasChild(130, 80, Dot(ColorF.FromRgba(0x6C, 0xCB, 0x5F))),
                        new CanvasChild(260, 30, Dot(ColorF.FromRgba(0xFF, 0x99, 0xA4))),
                        new CanvasChild(90, 120, Dot(ColorF.FromRgba(0xFC, 0xE1, 0x00))),
                    }),
                ],
            }));
}

sealed class ToolTipPage : Component
{
    public override Element Render() => GalleryPage.Shell("ToolTip",
        "A short description shown in a small popup, anchored to its target.",
        ControlExample.Build("A ToolTip (click the target)",
            ToolTip.Wrap(
                new BoxEl
                {
                    Padding = new Edges4(11, 6, 11, 6), Corners = Radii.ControlAll, Fill = Tok.FillControlDefault,
                    BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, HoverFill = Tok.FillControlSecondary,
                    Children = [new TextEl("Click me") { Size = 14f, Color = Tok.TextPrimary }],
                },
                "I am a ToolTip with helpful information.")));
}

sealed class CommandBarFlyoutPage : Component
{
    public override Element Render() => GalleryPage.Shell("CommandBarFlyout",
        "A contextual toolbar of commands, shown in a flyout.",
        ControlExample.Build("A CommandBarFlyout", CommandBarFlyout.Create("Commands")));
}
