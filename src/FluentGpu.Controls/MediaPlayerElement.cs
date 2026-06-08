using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using System;

namespace FluentGpu.Controls;

/// <summary>A WinUI MediaPlayerElement: a 16:9 video surface with a centered play glyph, sitting above a transport
/// bar (play, seek progress, time label, volume). The engine has no real video, so this renders the chrome only.</summary>
public static class MediaPlayerElement
{
    public static BoxEl Create(float width = 480f)
    {
        var surface = new BoxEl
        {
            Width = width,
            Height = width * 9f / 16f,
            Fill = ColorF.FromRgba(0x0A, 0x0A, 0x0A),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = new Element[]
            {
                new BoxEl
                {
                    Width = 56f, Height = 56f, Corners = Radii.Circle(56f),
                    Fill = ColorF.FromRgba(0, 0, 0, 0x80),
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = new Element[]
                    {
                        new TextEl(Icons.Play) { Size = 24f, Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF), FontFamily = Theme.IconFont },
                    },
                },
            },
        };

        // A 40x40 transport button (MTCMediaButton): a centered FontIcon glyph with subtle hover/press fills.
        static BoxEl TransportButton(string glyph) => new BoxEl
        {
            Width = 40f, Height = 40f, Corners = Radii.ControlAll,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button,
            Children = new Element[] { new TextEl(glyph) { Size = 16f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont } },
        };

        var transport = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 10f,
            Padding = new Edges4(12, 8, 12, 8),
            // MediaTransportControlsPanelBackground = AcrylicInAppFillColorDefault (a layered translucent fill) — mapped
            // to FillLayerDefault (closest token; no dedicated in-app acrylic token). 1px StrokeFlyoutDefault border.
            Fill = Tok.FillLayerDefault,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Children = new Element[]
            {
                TransportButton(Icons.Play),
                new BoxEl
                {
                    // Slider: 4px track + a 120px accent fill + a 24px thumb (FillControlSolid) on the seam head.
                    Grow = 1f, Height = 24f, AlignItems = FlexAlign.Center,
                    Children = new Element[]
                    {
                        new BoxEl
                        {
                            Grow = 1f, Height = 4f, Corners = Radii.Circle(4f), Fill = Tok.FillControlStrong,
                            Children = new Element[]
                            {
                                new BoxEl { Width = 120f, Height = 4f, Corners = Radii.Circle(4f), Fill = Tok.AccentDefault },
                            },
                        },
                    },
                },
                new TextEl("1:23 / 4:56") { Size = 12f, Color = Tok.TextSecondary },
                TransportButton(Icons.Volume),
            },
        };

        return new BoxEl
        {
            Direction = 1,
            Width = width,
            Corners = Radii.OverlayAll,
            ClipToBounds = true,
            // WinUI MediaTransportControls border = SurfaceStrokeColorFlyout (= StrokeFlyoutDefault), not card stroke.
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Children = new Element[] { surface, transport },
        };
    }
}
