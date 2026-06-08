using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu;

/// <summary>
/// Deterministic single-scene host for <c>--screenshot</c> fidelity captures. Renders a chosen control/surface on a
/// known dark page so the engine's real D3D12 output can be diffed against a WinUI 3 reference (the screenshot loop
/// that replaces eyeball-guessing). Grows scene-by-scene as the 1:1 sweep proceeds.
/// </summary>
sealed class ShotScene : Component
{
    private readonly string _id;
    public ShotScene(string id) => _id = id;

    static readonly ColorF PageBg = ColorF.FromRgba(0x20, 0x20, 0x20);   // WinUI dark page background (#202020)

    public override Element Render() => _id switch
    {
        // Full-bleed: the whole gallery (regression check), optionally deep-linked to a nav page via "gallery:<navkey>".
        "gallery" => Embed.Comp(() => new GalleryApp()),
        _ when _id.StartsWith("gallery:") => Embed.Comp(() => new GalleryApp { InitialPage = _id.Substring("gallery:".Length) }),
        _ => new BoxEl
        {
            Grow = 1,
            Fill = PageBg,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = [Content(_id)],
        },
    };

    static Element Content(string id) => id switch
    {
        // Sanity scene: a flat known-color rounded rect (proves the readback→PNG pipeline before trusting acrylic shots).
        "swatch" => new BoxEl { Width = 200, Height = 120, Corners = Radii.OverlayAll, Fill = ColorF.FromRgba(0x10, 0x7C, 0x10) },
        // Shadow diagnostics (no acrylic): does an opaque card cast the flyout shadow? a strong one?
        "shadowonly" => new BoxEl { Width = 160, Height = 200, Corners = Radii.OverlayAll, Fill = ColorF.FromRgba(0x2C, 0x2C, 0x2C), Shadow = Elevation.Flyout },
        "shadowstrong" => new BoxEl { Width = 160, Height = 200, Corners = Radii.OverlayAll, Fill = ColorF.FromRgba(0xFF, 0xFF, 0xFF), Shadow = new ShadowSpec(Blur: 40f, OffsetY: 12f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0xC0)) },
        "menu" => MenuPresenter(),
        _ => new TextEl($"unknown shot '{id}'") { Size = 16f, Color = Tok.TextPrimary },
    };

    // The flyout presenter card as OverlayHost builds it (transparent fill + flyout acrylic + 1px stroke + corner 8),
    // rendered statically so the acrylic material can be diffed against WinUI without overlay/animation timing.
    static Element MenuPresenter()
    {
        var items = new[]
        {
            new MenuFlyoutItem("Open", Icons.Document),
            new MenuFlyoutItem("Save", Icons.Accept),
            new MenuFlyoutItem("Refresh", Icons.Refresh),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem("Rename", Icons.Tag),
            new MenuFlyoutItem("Delete", Icons.Cancel, false),
        };
        return new BoxEl
        {
            Direction = 1,
            Fill = ColorF.Transparent,
            Acrylic = AcrylicSpec.Flyout,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Corners = Radii.OverlayAll,
            Shadow = Elevation.Flyout,
            Padding = new Edges4(0, 2, 0, 2),
            Children = [MenuFlyout.Build(items, () => { })],
        };
    }
}
