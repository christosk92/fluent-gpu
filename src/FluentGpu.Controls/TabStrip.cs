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
    // re-read). Defaults to the static FillSolidTertiary (implicit ColorF→Prop); the recorder reads paint.Fill either way.
    public Prop<ColorF> SelectedFill = Tok.FillSolidTertiary;
    public TemplateParts? Parts;

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
        var children = new Element[count + tail + 1];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            bool closeVisible = items[index].IsClosable &&
                                (CloseButtonOverlayMode != TabViewCloseButtonOverlayMode.OnPointerOver ||
                                 isSelected || hovered == index);
            children[i] = Tab(index, items[index], isSelected, closeVisible,
                () => Select(index), () => Close(index), hoveredSig);
        }

        if (IsAddTabButtonVisible)
            children[count] = AddButton(Add);

        children[^1] = new BoxEl { Grow = 1f, MinWidth = 100f };

        var root = new BoxEl
        {
            Direction = 0,
            Height = TitleBar.ExpandedHeight,
            AlignItems = FlexAlign.End,
            Padding = new Edges4(6f, 8f, 0f, 0f),
            Children = children,
        };
        return Parts.Apply(PartRoot, root) with { Children = root.Children };
    }

    Element Tab(int index, TabViewItem item, bool selected, bool closeVisible,
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
            layers.Add(new BoxEl
            {
                Direction = 1,
                Justify = FlexJustify.End,
                HitTestVisible = false,
                Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault }],
            });
        }
        else
        {
            layers.Add(new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.End,
                HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
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

        bool separatorVisible = !selected && index != hoveredSig.Peek() && index + 1 != hoveredSig.Peek();
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
                    Fill = Tok.StrokeDividerDefault,
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

    Element AddButton(Action add)
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
        return Parts.Apply(PartAddButton, button) with { OnClick = add, Role = AutomationRole.Button };
    }
}
