using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Header-only WinUI-style tab strip for custom title bars. It shares the <see cref="TabViewItem"/> model and the
/// important TabView header metrics, but intentionally renders no selected-content presenter.
/// </summary>
public sealed class TabStrip : Component
{
    public const string PartRoot = "Root";
    public const string PartTabItem = "TabItem";
    public const string PartTabLabel = "TabLabel";
    public const string PartTabCloseButton = "TabCloseButton";
    public const string PartAddButton = "AddButton";

    public IReadOnlyList<TabViewItem> Items = [];
    public Func<IReadOnlyList<TabViewItem>>? ItemsSource;
    public Func<int>? ItemsVersion;
    public Signal<int>? SelectedIndex;
    public Action<int>? OnSelectionChanged;
    public Action<int>? OnTabCloseRequested;
    public Func<TabViewItem?>? OnAddTabButtonClick;
    public bool IsAddTabButtonVisible = true;
    public TabViewCloseButtonOverlayMode CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto;
    public float TabWidth = 320f;
    public float MinTabWidth = 100f;
    public float MaxTabWidth = 360f;
    // Prop (not a raw ColorF) so a theme-dependent fill can be passed as a thunk (Prop.Of(() => Tok.X)) and follow a live
    // theme switch — a raw ColorF here freezes at mount (TabStrip is a long-lived component; its constructor args don't
    // re-read). The default is itself a live semantic thunk so callers that do not style the strip also follow retheme.
    public Prop<ColorF> SelectedFill = Prop.Of(static () => Tok.FillSolidTertiary);
    public TemplateParts? Parts;

    // MUX TabViewBorderBrush expressed as an alpha ink over the LIVE Mica rail. Dark's divider token is already the
    // stock white-alpha seam; seeded light palettes publish opaque card/divider colours, so use stock black@6% there.
    // Keep this bound: TabStrip and TitleBar are long-lived shell nodes and RethemeAll must update the rail in place.
    internal static Prop<ColorF> RailBaselineFill => Prop.Of(static () =>
        Tok.Theme == ThemeKind.Light ? ColorF.FromRgba(0, 0, 0, 0x0F) : Tok.StrokeDividerDefault);

    internal static BoxEl RailBaselineHost(float width = float.NaN, float grow = 0f,
                                            Edges4 lineMargin = default) => new()
    {
        Direction = 1,
        Width = width,
        Grow = grow,
        Justify = FlexJustify.End,
        HitTestVisible = false,
        Children = [new BoxEl { Height = 1f, Margin = lineMargin, Fill = RailBaselineFill }],
    };

    public override Element Render()
    {
        _ = ItemsVersion?.Invoke();
        var items = ItemsSource?.Invoke() ?? Items;
        int count = items.Count;

        var internalSelected = UseSignal(0);
        var selectedSig = SelectedIndex ?? internalSelected;
        int selected = count == 0 ? -1 : Math.Clamp(selectedSig.Value, 0, count - 1);

        var hoveredSig = UseSignal(-1);
        int hovered = hoveredSig.Value;

        void Select(int index)
        {
            if ((uint)index >= (uint)count) return;
            if (selectedSig.Peek() != index) selectedSig.Value = index;
            OnSelectionChanged?.Invoke(index);
        }

        void Close(int index)
        {
            if ((uint)index >= (uint)count || !items[index].IsClosable) return;
            OnTabCloseRequested?.Invoke(index);
        }

        void Add() => OnAddTabButtonClick?.Invoke();

        int tail = IsAddTabButtonVisible ? 1 : 0;
        var children = new Element[count + tail + 2];
        // The MUX LeftContentColumn + header-cell inset carries the same baseline as the unselected tabs.
        children[0] = RailBaselineHost(6f,
            lineMargin: selected == 0 ? new Edges4(0f, 0f, 4f, 0f) : default);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            bool closeVisible = items[index].IsClosable &&
                                (CloseButtonOverlayMode != TabViewCloseButtonOverlayMode.OnPointerOver ||
                                 isSelected || hovered == index);
            children[i + 1] = Tab(index, selected, items[index], isSelected, closeVisible,
                () => Select(index), () => Close(index), hoveredSig);
        }

        if (IsAddTabButtonVisible)
            children[count + 1] = AddButton(Add, count > 0 && selected == count - 1);

        int trailing = count + tail + 1;
        children[trailing] = RailBaselineHost(5f,
            lineMargin: !IsAddTabButtonVisible && count > 0 && selected == count - 1
                ? new Edges4(4f, 0f, 0f, 0f)
                : default);

        // The strip HUGS its content: a 6px seamed lead, tabs, the optional "+" button, then 5px of seamed flare room —
        // and nothing else. There used to
        // be a trailing `Grow=1, MinWidth=100` filler here, but in a custom title bar the strip is reported as ONE
        // TitleBarHit.Client island (TitleBar.Tabs), so that filler turned ≥100px of would-be caption band into
        // non-draggable client area. Trailing space belongs to the HOST (TitleBar already puts a Grow=1 Caption drag band
        // after the island; a plain container leaves it as free space).
        // Shrink=1: the strip is what gives when the bar overruns (the WinUI sizing contract — the caption cluster never
        // moves); the tabs then compress toward MinTabWidth instead of the row overflowing at full tab width.
        var root = new BoxEl
        {
            Direction = 0,
            Height = TitleBar.ExpandedHeight,
            AlignItems = FlexAlign.End,
            Shrink = 1f,
            // Lead/trail room is represented by real children (rather than padding) because the rail baseline must paint
            // through it. The 5-DIP tail also contains the selected plate's 4-DIP flare + one-DIP outset rim.
            Padding = new Edges4(0f, 8f, 0f, 0f),
            Children = children,
        };
        return Parts.Apply(PartRoot, root) with { Children = root.Children };
    }

    Element Tab(int index, int selectedIndex, TabViewItem item, bool selected, bool closeVisible,
                Action select, Action close, Signal<int> hoveredSig)
    {
        float tabW = Math.Clamp(TabWidth, MinTabWidth, MaxTabWidth);
        var content = new List<Element>(3);
        if (item.Icon is { Length: > 0 } icon)
        {
            content.Add(new TextEl(icon)
            {
                Size = 16f,
                FontFamily = Theme.IconFont,
                Margin = new Edges4(0f, 0f, 10f, 0f),
                Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
                PressedColor = selected ? Tok.TextPrimary : Tok.TextTertiary,
            });
        }

        var label = new TextEl(item.Header)
        {
            Size = 12f,
            Weight = selected ? (ushort)600 : (ushort)0,
            Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
            PressedColor = selected ? Tok.TextPrimary : Tok.TextTertiary,
            Grow = 1f,
            Shrink = 1f,
            Trim = TextTrim.CharacterEllipsis,
        };
        content.Add(Parts.Apply(PartTabLabel, label));

        if (closeVisible)
        {
            var closeButton = new BoxEl
            {
                Direction = 0,
                Width = 32f,
                Height = 24f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Margin = new Edges4(4f, 0f, 0f, 0f),
                Corners = Radii.ControlAll,
                Fill = ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button,
                OnClick = close,
                TabStop = false,
                Children =
                [
                    new TextEl(Icons.Cancel)
                    {
                        Size = 12f,
                        FontFamily = Theme.IconFont,
                        Color = Tok.TextPrimary,
                        PressedColor = Tok.TextSecondary,
                    },
                ],
            };
            content.Add(Parts.Apply(PartTabCloseButton, closeButton) with { OnClick = close, Role = AutomationRole.Button });
        }

        var plate = new BoxEl
        {
            Direction = 0,
            Height = 32f,
            AlignItems = FlexAlign.Center,
            Padding = closeVisible ? new Edges4(8f, 3f, 4f, 3f) : new Edges4(8f, 3f, 8f, 3f),
            Corners = Radii.OverlayTop,
            Fill = ColorF.Transparent,
            HoverFill = selected ? ColorF.Transparent : Tok.FillSubtleSecondary,
            PressedFill = selected ? ColorF.Transparent : Tok.FillSubtleTertiary,
            Role = AutomationRole.Tab,
            OnClick = select,
            OnHoverMove = _ => { if (hoveredSig.Peek() != index) hoveredSig.Value = index; },
            OnPointerExit = () => { if (hoveredSig.Peek() == index) hoveredSig.Value = -1; },
            Children = content.ToArray(),
        };
        plate = Parts.Apply(PartTabItem, plate) with { OnClick = select, Role = AutomationRole.Tab, Children = plate.Children };

        var layers = new List<Element>(4);
        if (!selected)
        {
            // The unselected tab's baseline hairline. It used to be StrokeCardDefault in both themes, which is BLACK
            // (dark: #19000000) — over the bare Mica of a transparent title bar that reads as a dark scar, not a
            // hairline. In dark the divider tier is the right ink (#15FFFFFF, white-alpha, so Mica still shows through).
            // In light the TOKEN is now wrong: StrokeCardDefault is #0F000000 only in the stock/neutral palette — every
            // seeded light preset derives it as an OPAQUE gray (warm #DCDAD4; slate/accent Darken(page, 0.08)), and an
            // opaque gray line drawn across a wallpaper-TINTED Mica bar is its own small disjoint slab. So light uses the
            // alpha literal instead: black@6% IS the stock value, and being an ink rather than a colour it stays
            // tint-safe on live Mica in all four presets. (A token would be the right home for it if one were
            // alpha-guaranteed in light; none is.)
            // Stop at the selected TabShape's 4-DIP curve-out, rather than painting through its translucent flare.
            layers.Add(RailBaselineHost(lineMargin: index == selectedIndex - 1
                ? new Edges4(0f, 0f, 4f, 0f)
                : index == selectedIndex + 1
                    ? new Edges4(4f, 0f, 0f, 0f)
                    : default));
        }
        else
        {
            // (a) the DEFINITION hairline, as a 1px OUTSET SILHOUETTE behind the plate — not a BorderWidth/BorderColor
            // on the plate itself: VisualKind.TabShape resolves the fill only and DROPS the border (SceneRecorder.cs
            // `out _`), so a border on a TabShape node is a silent no-op. A plain bordered Box can't stand in either —
            // its SDF ring is a rounded RECT, so it would miss the bottom flares and draw a closed bottom edge across
            // the tab, breaking the "tab merges into the surface below" read. A same-shape silhouette 1px larger on
            // top/left/right (height 33 grows UPWARD off AlignSelf.End, so its bottom edge stays flush and paints no
            // ink under the plate) leaves exactly a 1px rim tracing the real silhouette, flares included; corner radius
            // is Overlay+1 so the rim is a true parallel offset at the top corners.
            // Wavee supplies the RAW translucent body plate here, so a same-material tab follows live wallpaper hue and
            // luminance instead of becoming a fixed neutral chip. The silhouette therefore comes from the low-alpha
            // CardStroke ink, not a white lift that would brighten the whole translucent plate. Light keeps stock
            // black@6%; dark uses the semantic CardStroke token (#19000000 in the neutral preset).
            layers.Add(new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.End,
                HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "selected-edge",
                        Width = tabW + 10f,
                        Height = 33f,
                        OffsetX = -5f,
                        AlignSelf = FlexAlign.End,
                        TabShape = true,
                        TabFlareRadius = 4f,
                        Corners = new CornerRadius4(Radii.Overlay + 1f, Radii.Overlay + 1f, 0f, 0f),
                        Fill = Prop.Of(static () => Tok.Theme == ThemeKind.Light
                            ? ColorF.FromRgba(0, 0, 0, 0x0F)
                            : Tok.StrokeCardDefault),
                    },
                ],
            });
            // (b) the plate itself.
            layers.Add(new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.End,
                HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "selected-bg",
                        Width = tabW + 8f,
                        Height = 32f,
                        OffsetX = -4f,
                        AlignSelf = FlexAlign.End,
                        TabShape = true,
                        TabFlareRadius = 4f,
                        Corners = Radii.OverlayTop,
                        Fill = SelectedFill,
                    },
                ],
            });
        }

        // WinUI SetTabSeparatorOpacity: hide on the selected tab AND its left neighbour, on this tab's hover, and when
        // the right neighbour is hovered. The left-neighbour clause is what keeps a tick from piercing the left flare.
        bool separatorVisible = !selected && index + 1 != selectedIndex
            && index != hoveredSig.Peek() && index + 1 != hoveredSig.Peek();
        layers.Add(new BoxEl
        {
            Direction = 0,
            Justify = FlexJustify.End,
            HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Width = 1f,
                    Margin = new Edges4(0f, 8f, 0f, 8f),
                    // Same story as the baseline hairline above: light StrokeDividerDefault is an OPAQUE gray in every
                    // seeded preset (warm #E3E2DF; slate/accent Darken(page, 0.10)) and only #0F000000 in the stock one,
                    // so a token here paints an opaque gray tick across a tinted-Mica bar. The literal is the stock
                    // value AND preset-invariant, and as an alpha ink it composites over whatever Mica is behind it.
                    // Dark keeps the token: #15FFFFFF is already white-alpha, so Mica reads through it.
                    Fill = RailBaselineFill,
                    Opacity = separatorVisible ? 1f : 0f,
                },
            ],
        });
        layers.Add(plate);

        return new BoxEl
        {
            Key = "tab#" + index,
            ZStack = true,
            Width = tabW,
            MinWidth = MinTabWidth,
            MaxWidth = MaxTabWidth,
            Shrink = 1f,
            Children = layers.ToArray(),
        };
    }

    Element AddButton(Action add, bool followsSelected)
    {
        var button = new BoxEl
        {
            Direction = 0,
            Width = 32f,
            Height = 24f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Margin = new Edges4(3f, 0f, 0f, 3f),
            Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button,
            OnClick = add,
            Children =
            [
                new TextEl(Icons.Add)
                {
                    Size = 12f,
                    FontFamily = Theme.IconFont,
                    Color = Tok.TextPrimary,
                    PressedColor = Tok.TextSecondary,
                },
            ],
        };
        var applied = Parts.Apply(PartAddButton, button) with { OnClick = add, Role = AutomationRole.Button };
        // The add-button container belongs to the rail, so its baseline continues behind the transparent button. Leave
        // the selected flare's four-DIP curve-out clear when the last tab is selected.
        return new BoxEl
        {
            Key = "add-host",
            ZStack = true,
            Children =
            [
                RailBaselineHost(lineMargin: followsSelected ? new Edges4(4f, 0f, 0f, 0f) : default),
                applied,
            ],
        };
    }
}
