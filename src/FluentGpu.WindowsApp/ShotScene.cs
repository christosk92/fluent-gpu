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
        // The "Windows APIs" page's below-the-fold pillar cards (Shell / Power / Network), rendered directly so a
        // top-anchored screenshot can verify them without scrolling the full page (the device-height clamp hides them).
        "windowsapi-cards" => Embed.Comp(() => new OverlayHost
        {
            Child = new BoxEl
            {
                Grow = 1, Direction = 1, Fill = PageBg, Gap = 12f, Padding = new Edges4(28, 28, 28, 28),
                Children =
                [
                    Embed.Comp(() => new ShellCard()),
                    Embed.Comp(() => new PowerCard()),
                    Embed.Comp(() => new NetworkCard()),
                ],
            },
        }),
        // The REAL flyout through OverlayHost + the open animation (reproduces the live dropdown the user sees).
        "flyout" => Embed.Comp(() => new OverlayHost { Child = new BoxEl { Grow = 1, Fill = PageBg, Children = [Embed.Comp(() => new FlyoutLiveShot())] } }),
        "combobox-open" => OverlayShot(Embed.Comp(() => new ComboBoxOpenShot())),
        "combobox-editable-open" => OverlayShot(Embed.Comp(() => new ComboBoxEditableOpenShot())),
        "autosuggest-open" => OverlayShot(Embed.Comp(() => new AutoSuggestOpenShot())),
        "dropdown-open" => OverlayShot(Embed.Comp(() => new DropDownOpenShot())),
        "split-open" => OverlayShot(Embed.Comp(() => new SplitOpenShot())),
        "togglesplit-open" => OverlayShot(Embed.Comp(() => new ToggleSplitOpenShot())),
        "tooltip-open" => OverlayShot(Embed.Comp(() => new ToolTipOpenShot())),
        "teachingtip-open" => OverlayShot(Embed.Comp(() => new TeachingTipOpenShot())),
        "popup-open" => OverlayShot(Embed.Comp(() => new PopupOpenShot())),
        "flyout-open" => OverlayShot(Embed.Comp(() => new PlainFlyoutOpenShot())),
        "contentdialog-open" => OverlayShot(Embed.Comp(() => new ContentDialogOpenShot())),
        // Closing-sequence debug shots: run with --frames 45 to capture the real D3D path mid-close.
        "flyout-closing" => OverlayShot(Embed.Comp(() => new OverlayClosingShot(PopupChrome.Flyout))),
        "contentdialog-closing" => OverlayShot(Embed.Comp(() => new OverlayClosingShot(PopupChrome.Modal))),
        "teachingtip-closing" => OverlayShot(Embed.Comp(() => new OverlayClosingShot(PopupChrome.TeachingTip))),
        "atlas-stress" => CenterShot(Embed.Comp(() => new AtlasStressShot())),
        "text-snap" => new BoxEl { Grow = 1, Fill = PageBg, Children = [Embed.Comp(() => new TextSnapShot())] },
        "expander-open" => CenterShot(Embed.Comp(() => new ExpanderOpenShot())),
        "expander-reflow" => CenterShot(Embed.Comp(() => new ExpanderReflowShot())),
        "pips" => CenterShot(Embed.Comp(() => new PipsPagerShot())),
        "selectorbar" => CenterShot(Embed.Comp(() => new SelectorBarShot())),
        "pivot" => CenterShot(Embed.Comp(() => new PivotShot())),
        "radiobuttons" => CenterShot(Embed.Comp(() => new RadioButtonsShot())),
        "menubar-open" => OverlayShot(Embed.Comp(() => new MenuBarOpenShot())),
        "tabview" => CenterShot(Embed.Comp(() => new TabViewShot())),
        "navigationview" => CenterShot(Embed.Comp(() => new NavigationViewShot())),
        "treeview" => CenterShot(Embed.Comp(() => new TreeViewShot())),
        "listview" => CenterShot(Embed.Comp(() => new ItemsViewListShot())),   // id kept stable; renders the List preset
        "itemsview" => CenterShot(Embed.Comp(() => new ItemsViewShot())),
        // Acrylic material proof (needs-pixels loop for Rhi.D3D12 AcrylicCompositor): the flyout acrylic surface over
        // HIGH-CONTRAST saturated bars — the σ=30 DIP backdrop blur (AcrylicBrush.h:64 sc_blurRadius) must read as
        // soft color washes through the surface; over a flat page the blur would be invisible.
        "acrylic" => new BoxEl
        {
            Grow = 1, ZStack = true,
            Children =
            [
                new BoxEl
                {
                    Grow = 1, Direction = 0,   // saturated vertical bars fill the page behind the surface
                    Children =
                    [
                        new BoxEl { Grow = 1, Fill = ColorF.FromRgba(0xE8, 0x3C, 0x3C) },
                        new BoxEl { Grow = 1, Fill = ColorF.FromRgba(0xF5, 0xC5, 0x18) },
                        new BoxEl { Grow = 1, Fill = ColorF.FromRgba(0x18, 0xA0, 0x57) },
                        new BoxEl { Grow = 1, Fill = ColorF.FromRgba(0x2D, 0x7D, 0xF6) },
                        new BoxEl { Grow = 1, Fill = ColorF.FromRgba(0x8B, 0x3C, 0xC9) },
                        new BoxEl { Grow = 1, Fill = ColorF.FromRgba(0x20, 0x20, 0x20) },
                    ],
                },
                new BoxEl
                {
                    Grow = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [MenuPresenter()],
                },
            ],
        },
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

    static Element CenterShot(Element child) => new BoxEl
    {
        Grow = 1,
        Fill = PageBg,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Padding = new Edges4(48, 48, 48, 48),
        Children = [child],
    };

    static Element OverlayShot(Element child) => Embed.Comp(() => new OverlayHost
    {
        Child = new BoxEl
        {
            Grow = 1,
            Fill = PageBg,
            Padding = new Edges4(48, 48, 48, 48),
            Children = [child],
        },
    });

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
            Acrylic = Tok.AcrylicFlyout,
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

static class ShotCards
{
    public static Element Column(params Element[] rows) => new BoxEl
    {
        Direction = 1,
        Gap = 16f,
        Padding = new Edges4(24, 24, 24, 24),
        Fill = ColorF.FromRgba(0x2B, 0x2B, 0x2B),
        Corners = Radii.OverlayAll,
        Children = rows,
    };
}

sealed class ComboBoxOpenShot : Component
{
    static readonly string[] Colors =
    [
        "Blue",
        "Green",
        "Red",
        "Yellow",
    ];

    public override Element Render()
    {
        var selected = UseSignal(0);
        return new BoxEl
        {
            Direction = 1,
            Gap = 16f,
            Children =
            [
                new TextEl("A ComboBox") { Size = 20f, Bold = true, Color = Tok.TextPrimary },
                Embed.Comp(() => new ComboBox
                {
                    Items = Colors,
                    SelectedIndex = selected,
                    Width = 298f,
                    OpenOnMount = true,
                }),
            ],
        };
    }
}

sealed class ComboBoxEditableOpenShot : Component
{
    static readonly string[] Colors =
    [
        "Blue",
        "Green",
        "Red",
        "Yellow",
    ];

    public override Element Render()
    {
        var selected = UseSignal(1);
        var text = UseSignal("Green");
        return new BoxEl
        {
            Direction = 1,
            Gap = 16f,
            Children =
            [
                new TextEl("An editable ComboBox") { Size = 20f, Bold = true, Color = Tok.TextPrimary },
                Embed.Comp(() => new ComboBox
                {
                    Items = Colors,
                    SelectedIndex = selected,
                    Editable = true,
                    Text = text,
                    Width = 298f,
                    OpenOnMount = true,
                }),
            ],
        };
    }
}

sealed class AutoSuggestOpenShot : Component
{
    static readonly string[] Suggestions =
    [
        "Apple",
        "Apricot",
        "Banana",
        "Blueberry",
        "Cherry",
        "Grape",
        "Mango",
        "Orange",
        "Peach",
        "Pear",
    ];

    public override Element Render()
    {
        var text = UseSignal("sa");
        return new BoxEl
        {
            Direction = 1,
            Gap = 16f,
            Children =
            [
                new TextEl("An AutoSuggestBox") { Size = 20f, Bold = true, Color = Tok.TextPrimary },
                Embed.Comp(() => new AutoSuggestBox
                {
                    Suggestions = Suggestions,
                    Placeholder = "Search",
                    Width = 450f,
                    Text = text,
                    DebounceMs = 0f,
                }),
            ],
        };
    }
}

sealed class DropDownOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new DropDownButton
    {
        Label = "Options",
        Items = MenuItems(),
        OpenOnMount = true,
    });

    internal static IReadOnlyList<MenuFlyoutItem> MenuItems() =>
    [
        new MenuFlyoutItem("Open", Icons.Document),
        new MenuFlyoutItem("Save", Icons.Accept),
        new MenuFlyoutItem("Refresh", Icons.Refresh),
        MenuFlyoutItem.Separator,
        new MenuFlyoutItem("Delete", Icons.Cancel, false),
    ];
}

sealed class SplitOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new SplitButton
    {
        Label = "Paste",
        Glyph = Icons.Document,
        OnInvoke = () => { },
        Items =
        [
            new MenuFlyoutItem("Paste as text", Icons.Document),
            new MenuFlyoutItem("Paste special", Icons.Document),
        ],
        OpenOnMount = true,
    });
}

sealed class ToggleSplitOpenShot : Component
{
    public override Element Render()
    {
        var on = UseSignal(true);
        return Embed.Comp(() => new ToggleSplitButton
        {
            Label = "Toggle split",
            IsChecked = on,
            Items = DropDownOpenShot.MenuItems(),
            OpenOnMount = true,
        });
    }
}

sealed class ToolTipOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new ToolTip
    {
        Target = Button.Standard("Hover target", () => { }),
        Text = "Use this control to choose the active option.",
        OpenOnMount = true,
    });
}

sealed class TeachingTipOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new TeachingTip
    {
        TriggerLabel = "Show tip",
        Title = "Try filters",
        Subtitle = "Narrow results quickly",
        Body = "Use filters to reduce the list before opening a detail view.",
        IconGlyph = Icons.Search,
        ActionButtonContent = "Got it",
        CloseButtonContent = "Close",
        OpenOnMount = true,
    });
}

sealed class PopupOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new Popup
    {
        TriggerLabel = "Show popup",
        Text = "This content is displayed in a popup above the page.",
        OpenOnMount = true,
    });
}

sealed class PlainFlyoutOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new FlyoutButton
    {
        Label = "Open flyout",
        OpenOnMount = true,
        Content = () => new BoxEl
        {
            Direction = 1,
            Gap = 12f,
            Children =
            [
                new TextEl("All items will be removed.") { Size = 14f, Bold = true, Color = Tok.TextPrimary },
                Button.Accent("Yes, delete", () => { }),
            ],
        },
    });
}

sealed class ContentDialogOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new ContentDialog
    {
        TriggerLabel = "Show dialog",
        Title = "Save your work?",
        Message = "Lorem ipsum dolor sit amet, adipisicing elit.",
        PrimaryText = "Save",
        SecondaryText = "Don't Save",
        CloseText = "Cancel",
        DefaultButton = ContentDialog.DefaultBtn.Primary,
        OpenOnMount = true,
    });
}

/// <summary>
/// Deterministic close-animation shot. The overlay opens after the anchor realizes, settles, then closes at a fixed
/// frame-clock tick. Capture with <c>--frames 45</c> to inspect a mid-close frame on the real D3D path.
/// </summary>
sealed class OverlayClosingShot : Component
{
    private readonly PopupChrome _chrome;

    public OverlayClosingShot(PopupChrome chrome) => _chrome = chrome;

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        long tick = UseContext(FrameClock.Tick);

        UseLayoutEffect(() =>
        {
            if (anchor.Value.IsNull) return;
            if (handle.Value is null)
            {
                var opts = new PopupOptions(
                    FocusTrap: _chrome == PopupChrome.Modal,
                    DismissBehavior: _chrome == PopupChrome.Modal ? DismissBehavior.Modal : DismissBehavior.LightDismiss,
                    Chrome: _chrome);
                handle.Value = svc.Open(() => anchor.Value, Body, FlyoutPlacement.BottomLeft, opts);
            }
            else if (tick >= 42 && handle.Value.IsOpen)
            {
                handle.Value.Close();
            }
        }, tick);

        return new BoxEl
        {
            Direction = 1,
            Gap = 16f,
            Children =
            [
                new TextEl(_chrome == PopupChrome.Modal ? "ContentDialog closing" : _chrome == PopupChrome.TeachingTip ? "TeachingTip closing" : "Flyout closing")
                {
                    Size = 20f, Bold = true, Color = Tok.TextPrimary,
                },
                Button.Accent("Anchor", () => { }) with { OnRealized = h => anchor.Value = h },
            ],
        };
    }

    Element Body()
    {
        if (_chrome == PopupChrome.Modal)
        {
            return new BoxEl
            {
                Direction = 1,
                Width = 480f,
                Corners = Radii.OverlayAll,
                Fill = Tok.FillSolidBase,
                BorderColor = Tok.StrokeSurfaceDefault,
                BorderWidth = 1f,
                Shadow = Elevation.Dialog,
                ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1,
                        Padding = Edges4.All(24f),
                        Gap = 12f,
                        Fill = Tok.FillCardDefault,
                        Children =
                        [
                            new TextEl("Save your work?") { Size = 20f, Bold = true, Color = Tok.TextPrimary },
                            new TextEl("Lorem ipsum dolor sit amet, adipisicing elit.") { Size = 14f, Color = Tok.TextPrimary },
                        ],
                    },
                    new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault },
                    new BoxEl
                    {
                        Direction = 0,
                        Gap = 8f,
                        Padding = Edges4.All(24f),
                        Fill = Tok.FillSolidBase,
                        Children =
                        [
                            Button.Accent("Save", () => { }) with { MinWidth = 130f, MaxWidth = 202f, Height = 32f },
                            Button.Standard("Don't Save", () => { }) with { MinWidth = 130f, MaxWidth = 202f, Height = 32f },
                            Button.Standard("Cancel", () => { }) with { MinWidth = 130f, MaxWidth = 202f, Height = 32f },
                        ],
                    },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 1,
            Width = _chrome == PopupChrome.TeachingTip ? 320f : 260f,
            Gap = 8f,
            Padding = Edges4.All(16f),
            Corners = Radii.OverlayAll,
            Fill = Tok.FillCardDefault,
            BorderColor = Tok.StrokeSurfaceDefault,
            BorderWidth = 1f,
            Shadow = _chrome == PopupChrome.TeachingTip ? Elevation.Dialog : null,
            Children =
            [
                new TextEl(_chrome == PopupChrome.TeachingTip ? "This is the title" : "All items will be removed.")
                {
                    Size = 16f, Bold = true, Color = Tok.TextPrimary,
                },
                new TextEl(_chrome == PopupChrome.TeachingTip ? "And this is the subtitle" : "Do you want to continue?")
                {
                    Size = 14f, Color = Tok.TextPrimary,
                },
            ],
        };
    }
}

sealed class ExpanderOpenShot : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 420f,
        Children =
        [
            Expander.Create("Advanced options",
                new BoxEl
                {
                    Direction = 1,
                    Gap = 8f,
                    Children =
                    [
                        new TextEl("Hidden content, revealed when the Expander is expanded.") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        Button.Standard("An action", () => { }),
                    ],
                },
                initiallyExpanded: true),
        ],
    };
}

// REPRO: a NARROW expander (body wraps to 2 lines) driven through collapse -> re-expand, so a --frames 70 screenshot
// captures the settled post-reflow state. Mirrors the user's gallery case (width-constrained wrapping body + the toggle).
sealed class ExpanderReflowShot : Component
{
    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // subscribe -> re-render every frame so the toggle schedule advances
        var open = UseSignal(true);               // start expanded
        var frame = UseRef(0);
        UseEffect(() =>
        {
            frame.Value++;
            if (frame.Value == 4) open.Value = false;        // collapse
            else if (frame.Value == 28) open.Value = true;   // re-expand after the 167ms collapse settles
        }, tick);
        return new BoxEl
        {
            Direction = 1,
            Width = 440f,   // gallery-like content width: the body wraps boundary-proximate (2 lines)
            Children =
            [
                ControlExample.Build("A simple Expander",
                    Embed.Comp(() => new Expander
                    {
                        Header = "This text is collapsible",
                        Content = new BoxEl
                        {
                            Direction = 1,
                            Gap = 8f,
                            Children =
                            [
                                new TextEl("Hidden content, revealed when the Expander is expanded.") { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                                Button.Standard("An action", () => { }),
                            ],
                        },
                        IsExpanded = open,
                    }),
                    code: "Expander.Create(\"This text is collapsible\", VStack(8, Body(text), Button), initiallyExpanded: true)"),
            ],
        };
    }
}

sealed class PipsPagerShot : Component
{
    public override Element Render()
    {
        var sel = UseSignal(2);
        return ShotCards.Column(PipsPager.Create(7, sel.Value, i => sel.Value = i));
    }
}

sealed class SelectorBarShot : Component
{
    static readonly string[] Items = ["Recent", "Favorites", "Shared"];

    public override Element Render()
    {
        var (sel, setSel) = UseState(1);
        return ShotCards.Column(SelectorBar.Create(Items, sel, setSel));
    }
}

sealed class PivotShot : Component
{
    static readonly string[] Headers = ["Photos", "Albums", "People"];

    public override Element Render() => new BoxEl
    {
        Width = 520f,
        Height = 220f,
        Corners = Radii.OverlayAll,
        BorderColor = Tok.StrokeCardDefault,
        BorderWidth = 1f,
        ClipToBounds = true,
        Children = [Pivot.Create(Headers)],
    };
}

sealed class RadioButtonsShot : Component
{
    static readonly string[] Options = ["Small", "Medium", "Large"];

    public override Element Render()
    {
        var (sel, setSel) = UseState(1);
        return ShotCards.Column(RadioButton.Group(Options, sel, setSel));
    }
}

sealed class MenuBarOpenShot : Component
{
    public override Element Render() => Embed.Comp(() => new MenuBar
    {
        OpenIndexOnMount = 0,
        Menus =
        [
            new MenuBarItem("File",
            [
                new MenuFlyoutItem("New", Icons.Document),
                new MenuFlyoutItem("Open", Icons.Folder),
                MenuFlyoutItem.Separator,
                new MenuFlyoutItem("Exit", Icons.Cancel),
            ]),
            new MenuBarItem("Edit",
            [
                new MenuFlyoutItem("Cut"),
                new MenuFlyoutItem("Copy"),
                new MenuFlyoutItem("Paste"),
            ]),
            new MenuBarItem("View",
            [
                new MenuFlyoutItem("Zoom in"),
                new MenuFlyoutItem("Zoom out"),
            ]),
        ],
    });
}

sealed class TabViewShot : Component
{
    static readonly string[] Tabs = ["Home", "Documents", "Settings"];

    public override Element Render() => new BoxEl
    {
        Width = 560f,
        Height = 240f,
        Corners = Radii.OverlayAll,
        BorderColor = Tok.StrokeCardDefault,
        BorderWidth = 1f,
        ClipToBounds = true,
        Children = [TabView.Create(Tabs)],
    };
}

sealed class NavigationViewShot : Component
{
    public override Element Render() => new BoxEl
    {
        Width = 760f,
        Height = 420f,
        Corners = Radii.OverlayAll,
        BorderColor = Tok.StrokeCardDefault,
        BorderWidth = 1f,
        ClipToBounds = true,
        Children =
        [
            Embed.Comp(() => new NavigationView
            {
                Initial = "home",
                Header = "Library",
                Items =
                [
                    new NavItem("home", Icons.Home, "Home"),
                    new NavItem("files", Icons.Folder, "Files")
                    {
                        InitiallyExpanded = true,
                        Children =
                        [
                            new NavItem("recent", Icons.Clock, "Recent"),
                            new NavItem("shared", Icons.Share, "Shared"),
                        ],
                    },
                    new NavItem("settings", Icons.Settings, "Settings"),
                ],
                Content = key => new BoxEl
                {
                    Padding = Edges4.All(24f),
                    Children = [new TextEl("Content: " + key) { Size = 18f, Color = Tok.TextPrimary }],
                },
            }),
        ],
    };
}

sealed class TreeViewShot : Component
{
    static readonly TreeNode[] Roots =
    [
        new("Documents", new TreeNode("Invoices"), new TreeNode("Reports")),
        new("Pictures", new TreeNode("Screenshots"), new TreeNode("Wallpapers")),
        new("Music"),
    ];

    public override Element Render() => new BoxEl
    {
        Width = 320f,
        Corners = Radii.OverlayAll,
        BorderColor = Tok.StrokeCardDefault,
        BorderWidth = 1f,
        Padding = new Edges4(0, 6, 0, 6),
        Children = [TreeView.Create(Roots)],
    };
}

// D1 — the exact gallery ItemsView List-preset card (Width=280, 8 natural rows, accent pill on row 0): Width=280
// with NO height anywhere above the list. Must render 8 natural-sized rows (8 × 44) with the accent selection pill
// on row 0 — this card rendered as an empty panel before the D1 fix.
sealed class ItemsViewListShot : Component
{
    static readonly string[] Items = ["Cappuccino", "Latte", "Espresso", "Macchiato", "Americano", "Mocha", "Flat White", "Cortado"];
    public override Element Render()
    {
        var selected = UseSignal(0);
        return new BoxEl
        {
            Width = 280f,
            Corners = Radii.OverlayAll,
            BorderColor = Tok.StrokeCardDefault,
            BorderWidth = 1f,
            Padding = new Edges4(0, 4, 0, 4),
            Children = [ItemsView.List(Items, selected)],
        };
    }
}

// Glyph vertical pixel-snap proof: 12 copies of the SAME 14px label, each pushed down by an extra 0.1 DIP of top
// padding, so the run's device baseline phase sweeps a bit more than one device pixel down the column. With the
// glyph-run Y snap, all 12 rows rasterize byte-identical (modulo a whole-pixel shift); without it, the fractional
// rows blit an integer-baseline atlas bitmap at a fractional device Y through the LINEAR sampler and the bottom
// coverage row fades — the intermittent "text shaved at the bottom" report. Verified numerically off the PNG.
sealed class TextSnapShot : Component
{
    public override Element Render()
    {
        var rows = new List<Element>(12);
        for (int i = 0; i < 12; i++)
            rows.Add(new BoxEl
            {
                Height = 30f,
                Padding = new Edges4(0f, i * 0.1f, 0f, 0f),
                Children = [new TextEl("Source code") { Size = 14f, Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF) }],
            });
        return new BoxEl { Grow = 1, Direction = 1, Padding = Edges4.All(20f), Children = [.. rows] };
    }
}

// Glyph-atlas overflow proof (run with --frames 14): the first frames rasterize ~1,200 distinct Segoe Fluent glyphs
// at THREE sizes (≈6.2 M px² of coverage vs the 4.19 M px² atlas — guaranteed overflow → generational reset), then
// the storm unmounts and only a sentinel line remains. A correct reset re-shapes the sentinel from the fresh
// generation → the screenshot shows crisp readable text. The pre-fix behavior cached overflowed glyphs at the atlas
// origin, so the sentinel (and every later glyph) rendered as corrupt fragments.
sealed class AtlasStressShot : Component
{
    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);
        bool storm = tick < 6;

        var kids = new List<Element>
        {
            new TextEl("Atlas generation reset OK — the quick brown fox 0123456789") { Size = 16f, Color = ColorF.FromRgba(0xFF, 0xFF, 0xFF) },
            new TextEl("       ") { Size = 20f, Color = ColorF.FromRgba(0x4C, 0xC2, 0xFF), FontFamily = Theme.IconFont },
        };

        if (storm)
        {
            // A FRESH glyph set per frame: the cache key includes the quantized size, so size = 20 + tick rasterizes
            // 1,200 new entries each storm frame (~1 M px² of coverage per frame → guaranteed overflow by frame ~5).
            // All rows sit inside the viewport — record-time culling must not skip the shaping.
            const int Count = 1200, PerRow = 60;
            float size = 20f + tick;
            for (int row = 0; row < Count / PerRow; row++)
            {
                var sb = new System.Text.StringBuilder(PerRow);
                for (int i = 0; i < PerRow; i++) sb.Append((char)(0xE700 + row * PerRow + i));
                kids.Add(new TextEl(sb.ToString()) { Size = size, Color = ColorF.FromRgba(0x80, 0x80, 0x80), FontFamily = Theme.IconFont });
            }
        }

        return new BoxEl { Direction = 1, Gap = 2f, Width = 1320f, Children = kids.ToArray() };
    }
}

// D1 — the gallery ItemsView shape (MiscPages.cs): the legacy 4-column tile grid in an auto-height host; the view
// must size to its grid's ContentExtent (2 rows × 80 + 8 gap = 168) — was an empty panel before the D1 fix.
sealed class ItemsViewShot : Component
{
    static readonly string[] Items = ["Photo 1", "Photo 2", "Photo 3", "Photo 4", "Photo 5", "Photo 6", "Photo 7", "Photo 8"];
    public override Element Render() => new BoxEl
    {
        Width = 420f,
        Children = [ItemsView.Create(Items, columns: 4)],
    };
}
