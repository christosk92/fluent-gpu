using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

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
        // The REAL flyout through OverlayHost + the open animation (reproduces the live dropdown the user sees).
        "flyout" => Embed.Comp(() => new OverlayHost { Child = new BoxEl { Grow = 1, Fill = PageBg, Children = [Embed.Comp(() => new FlyoutLiveShot())] } }),
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
        // Mimics a FOCUSED EditableText (accent 2px border + ~6% control fill) — must read as a hollow accent ring, NOT a filled blue box.
        "textfocus" => new BoxEl
        {
            Direction = 0, Width = 280, Height = 36, AlignItems = FlexAlign.Center, Padding = new Edges4(10, 0, 10, 0),
            Corners = Radii.ControlAll, Fill = Tok.FillControlDefault, BorderBrush = GradientSpec.Solid(Tok.AccentDefault), BorderWidth = 2f,
            Children = [new TextEl("saas") { Size = 14f, Color = Tok.TextPrimary }],
        },
        // Shadow diagnostics (no acrylic): does an opaque card cast the flyout shadow? a strong one?
        "shadowonly" => new BoxEl { Width = 160, Height = 200, Corners = Radii.OverlayAll, Fill = ColorF.FromRgba(0x2C, 0x2C, 0x2C), Shadow = Elevation.Flyout },
        "shadowstrong" => new BoxEl { Width = 160, Height = 200, Corners = Radii.OverlayAll, Fill = ColorF.FromRgba(0xFF, 0xFF, 0xFF), Shadow = new ShadowSpec(Blur: 40f, OffsetY: 12f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0xC0)) },
        "ring" => ProgressRing.Determinate(0.7f, 160f),
        // Diagnostic: does composited BoxEl.Rotation work? A 30° red bar should render TILTED, not horizontal.
        "rottest" => new BoxEl { Width = 200, Height = 24, Corners = Radii.ControlAll, Fill = ColorF.FromRgba(0xE0, 0x30, 0x30), Rotation = 30f },
        "menu" => MenuPresenter(),
        // Corner-AA diagnostic: a translucent-fill + 1px solid-border rounded box (the unchecked-CheckBox case) blown up so
        // the corner smoothness is unmistakable. The 1px ring must follow a single smooth concentric arc, not a rough notch.
        "borderzoom" => new BoxEl
        {
            Direction = 0, Gap = 40f, AlignItems = FlexAlign.Center,
            Children =
            [
                // Faithful 8× magnification of the unchecked CheckBox box (20→160, radius 4→32, border 1→8).
                new BoxEl { Width = 160, Height = 160, Corners = CornerRadius4.All(32f), BorderWidth = 8f,
                    BorderColor = Tok.StrokeControlStrongDefault, Fill = Tok.FillControlAltSecondary },
                new BoxEl { Width = 160, Height = 160, Corners = CornerRadius4.All(32f), BorderWidth = 2f,
                    BorderColor = Tok.StrokeControlStrongDefault, Fill = Tok.FillControlAltSecondary },
            ],
        },
        // Control-parity shots: every interaction state on a card, diffed 1:1 against WinUI. The unchecked CheckBox /
        // unselected RadioButton must read as an OUTLINED box/ring (hairline strong-stroke + ~10% fill), never a solid
        // grey chip (the donut bug). The TextBox placeholder must be DIM and the caret would sit at x=0 (empty).
        "checkbox" => CardColumn(
            CheckBox.Create("Unchecked", CheckState.Unchecked, _ => { }),
            CheckBox.Create("Checked", CheckState.Checked, _ => { }),
            CheckBox.Create("Indeterminate", CheckState.Indeterminate, _ => { })),
        "radiobutton" => CardColumn(
            RadioButton.Create("Option A", false, () => { }),
            RadioButton.Create("Option B (selected)", true, () => { })),
        "toggle" => CardColumn(
            ToggleSwitch.Create(false, () => { }, "Off"),
            ToggleSwitch.Create(true, () => { }, "On")),
        "textbox" => CardColumn(
            TextBox.Create("Enter your name"),
            TextBox.Create("you@example.com", 280f, "Email")),
        _ => new TextEl($"unknown shot '{id}'") { Size = 16f, Color = Tok.TextPrimary },
    };

    // The WinUI-Gallery "example card" surface (a slightly elevated dark panel) the controls sit on — this is the exact
    // context where the unchecked-CheckBox grey-chip bug was visible, so shots reproduce it 1:1.
    static Element CardColumn(params Element[] rows) => new BoxEl
    {
        Direction = 1, Gap = 16f, Padding = new Edges4(24, 24, 24, 24),
        Fill = ColorF.FromRgba(0x2B, 0x2B, 0x2B), Corners = Radii.OverlayAll,
        Children = rows,
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

/// <summary>A real DropDownButton that auto-opens its MenuFlyout through OverlayHost on mount — so a screenshot captures
/// the ACTUAL flyout path (overlay + acrylic + open animation), reproducing the live dropdown the user complained about.</summary>
sealed class FlyoutLiveShot : Component
{
    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var opened = UseRef<bool>(false);
        long tick = UseContext(FrameClock.Tick);   // re-run the open effect each frame until the anchor is realized

        UseLayoutEffect(() =>
        {
            if (opened.Value || anchor.Value.IsNull) return;
            opened.Value = true;
            var items = new[]
            {
                new MenuFlyoutItem("Send", Icons.Document),
                new MenuFlyoutItem("Reply", Icons.Accept),
                new MenuFlyoutItem("Reply All", Icons.More),
                MenuFlyoutItem.Separator,
                new MenuFlyoutItem("Delete", Icons.Cancel, false),
            };
            svc.Open(() => anchor.Value, () => MenuFlyout.Build(items, () => { }), FlyoutPlacement.BottomLeft);
        }, tick);

        return new BoxEl
        {
            Direction = 1,
            Padding = new Edges4(48, 48, 48, 48),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignSelf = FlexAlign.Start, AlignItems = FlexAlign.Center, Gap = 8f,
                    MinHeight = 32f, Padding = new Edges4(11, 5, 11, 6), Corners = Radii.ControlAll,
                    BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder, Fill = Tok.FillControlDefault,
                    OnRealized = h => anchor.Value = h,
                    Children =
                    [
                        new TextEl("Email") { Size = 14f, Color = Tok.TextPrimary },
                        new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },
                    ],
                },
            ],
        };
    }
}
