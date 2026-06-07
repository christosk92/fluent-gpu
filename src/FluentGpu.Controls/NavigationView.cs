using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A NavigationView pane entry: a stable key, an icon glyph, and a label.</summary>
public sealed record NavItem(string Key, string Glyph, string Label, bool IsHeader = false);

public enum PaneMode : byte { Expanded = 0, Compact = 1, Minimal = 2 }

/// <summary>
/// Adaptive left NavigationView modeled on WinUI's SplitView-backed template. It supports expanded, compact, and
/// minimal display modes, a WinUI-style pane toggle row, section headers, pinned footer items, and a sliding selection
/// indicator for the primary menu.
/// </summary>
public sealed class NavigationView : Component
{
    public NavItem[] Items = [];
    public NavItem[] Footer = [];
    public string Initial = "";
    public Action<string>? OnSelect;
    public Func<string, Element>? Content;
    public string? Header;
    public bool ShowBackButton;
    public Action? OnBack;

    /// <summary>Ambient navigate action for descendants that need to drive selection without prop threading.</summary>
    public static readonly Context<Action<string>> Nav = new(static _ => { });

    internal static readonly Context<float> IndicatorTarget = new(-1000f);

    // DIAG: last-reported width/mode so the pane-mode log only fires on change (see Render()).
    static float _diagWidth = float.NaN;
    static PaneMode _diagMode = (PaneMode)255;

    public float CompactThreshold = 1008f;
    public float MinimalThreshold = 641f;
    public float PaneWidth = 320f;
    public float CompactWidth = 48f;

    const float TopPaneHeight = 48f;
    const float PaneHeaderRowHeight = 40f;
    const float PaneToggleWidth = 40f;
    const float PaneToggleHeight = 36f;
    const float ItemHeight = 36f;
    const float ItemOuterHeight = 40f;
    const float ItemMarginX = 4f;
    const float ItemMarginY = 2f;
    const float HeaderHeight = 36f;
    const float IconColumnWidth = 40f;
    const float IconSize = 16f;
    const float IndicatorW = 3f;
    const float IndicatorH = 16f;

    static readonly CornerRadius4 PaneOverlayCorners = new(0f, Radii.Overlay, Radii.Overlay, 0f);
    static readonly CornerRadius4 ContentLeftTopCorner = new(Radii.Overlay, 0f, 0f, 0f);
    static readonly ColorF LightDismissOverlay = ColorF.FromRgba(0, 0, 0, 0x1A);

    public override Element Render()
    {
        var (selected, setSelected) = UseState(Initial.Length > 0 ? Initial : (Items.Length > 0 ? FirstSelectable() : ""));
        var (paneOpen, setPaneOpen) = UseState(false);
        var (collapsed, setCollapsed) = UseState(false);

        float width = UseContext(Viewport.Size).Width;
        PaneMode autoMode = width <= 0f || width >= CompactThreshold ? PaneMode.Expanded
                          : width >= MinimalThreshold ? PaneMode.Compact
                          : PaneMode.Minimal;
        // The hamburger manually collapses the full-width pane to the icon rail (WinUI title-bar toggle).
        PaneMode mode = autoMode == PaneMode.Expanded && collapsed ? PaneMode.Compact : autoMode;
        float openPaneWidth = width > 0f ? MathF.Min(PaneWidth, width) : PaneWidth;

        // DIAG: report the pane-mode decision whenever the viewport width or resulting mode changes.
        if (width != _diagWidth || mode != _diagMode)
        {
            _diagWidth = width; _diagMode = mode;
            Console.Error.WriteLine($"[NavView] viewport.width={width:0.#} autoMode={autoMode} mode={mode} collapsed={collapsed} paneOpen={paneOpen} items={Items.Length} paneW={openPaneWidth:0.#}");
        }

        void Select(string key)
        {
            setSelected(key);
            setPaneOpen(false);
            OnSelect?.Invoke(key);
        }

        // Hamburger action by mode: at full width it collapses to the rail; from a manually-collapsed rail it expands
        // back; in adaptive compact/minimal it toggles the overlay flyout.
        Action toggle = mode == PaneMode.Expanded ? () => setCollapsed(true)
                      : autoMode == PaneMode.Expanded ? () => setCollapsed(false)
                      : () => setPaneOpen(!paneOpen);

        var content = ContentFrame(selected, Select, mode);
        Element baseLayer = mode switch
        {
            PaneMode.Expanded => new BoxEl
            {
                Direction = 0,
                Grow = 1,
                Children = [FullPane(openPaneWidth, selected, Select, toggle, overlay: false), content],
            },
            PaneMode.Compact => new BoxEl
            {
                Direction = 0,
                Grow = 1,
                Children = [CompactPane(selected, Select, toggle, paneOpen), content],
            },
            _ => new BoxEl
            {
                Direction = 1,
                Grow = 1,
                Children = [TopBar(toggle), content],
            },
        };

        if (mode == PaneMode.Expanded || !paneOpen)
            return baseLayer;

        return Ui.ZStack(
            baseLayer,
            new BoxEl { Fill = LightDismissOverlay, OnClick = () => setPaneOpen(false) },
            FullPane(openPaneWidth, selected, Select, () => setPaneOpen(false), overlay: true)
        ) with { Grow = 1f };
    }

    string FirstSelectable()
    {
        foreach (var it in Items)
            if (!it.IsHeader)
                return it.Key;

        return Items.Length > 0 ? Items[0].Key : "";
    }

    Element ContentFrame(string selected, Action<string> select, PaneMode mode)
    {
        var child = Ctx.Provide(Nav, (Action<string>)select, Content?.Invoke(selected) ?? new BoxEl());
        return new BoxEl
        {
            Direction = 1,
            Grow = 1,
            Fill = Tok.FillLayerDefault,
            BorderColor = Tok.StrokeCardDefault,
            BorderWidth = 1f,
            Corners = mode == PaneMode.Minimal ? default : ContentLeftTopCorner,
            Children = [child],
        };
    }

    Element FullPane(float width, string selected, Action<string> select, Action toggle, bool overlay)
    {
        var mainItems = BuildItems(Items, selected, select, expanded: true, ownIndicator: false);
        var footerItems = BuildItems(Footer, selected, select, expanded: true, ownIndicator: true);
        // Pane background, per the shipped WinUI generic.xaml: the EXPANDED (always-visible) pane =
        // NavigationViewExpandedPaneBackground = SolidBackgroundFillColorTransparent → FULLY TRANSPARENT, so DWM's Mica
        // window backdrop shows through. Only the transient OVERLAY (minimal/compact flyout) pane uses in-app acrylic
        // (NavigationViewDefaultPaneBackground) — engine acrylic samples the app canvas, which is correct over content
        // but would override window transparency (kill Mica) if used on the always-visible pane.
        var paneFill = ColorF.Transparent;

        var pane = new BoxEl
        {
            Width = width,
            Direction = 1,
            Fill = paneFill,
            Acrylic = overlay ? AcrylicSpec.InAppDefault : null,
            BorderColor = Tok.StrokeDividerDefault,
            BorderWidth = 1f,
            Corners = overlay ? PaneOverlayCorners : default,
            Shadow = overlay ? Elevation.Flyout : null,
            Children =
            [
                PaneTitleRow(toggle, expanded: true),
                // Menu items scroll (overflow → scrollbar on hover); the footer stays pinned at the bottom.
                Ui.ScrollView(Ui.ZStack(
                    new BoxEl { Direction = 1, Children = mainItems },
                    Ctx.Provide(IndicatorTarget, SelectedY(Items, selected), Embed.Comp(() => new NavIndicator()))
                )),
                new BoxEl { Direction = 1, Children = footerItems },
                new BoxEl { Height = 4 },
            ],
        };

        return pane;
    }

    Element CompactPane(string selected, Action<string> select, Action toggle, bool paneOpen)
    {
        var children = new List<Element>
        {
            PaneToggleButton(toggle),
            new BoxEl { Height = 4 },
        };

        foreach (var it in Items)
            children.Add(it.IsHeader ? new BoxEl { Height = 8 } : Item(it, selected, select, expanded: false, ownIndicator: true));

        children.Add(new BoxEl { Grow = 1 });

        foreach (var it in Footer)
            children.Add(it.IsHeader ? new BoxEl { Height = 8 } : Item(it, selected, select, expanded: false, ownIndicator: true));

        return new BoxEl
        {
            Width = CompactWidth,
            Direction = 1,
            Fill = Tok.AcrylicBase,                    // translucent over Mica (see FullPane note)
            BorderColor = Tok.StrokeDividerDefault,
            BorderWidth = 1f,
            AlignItems = FlexAlign.Center,
            Children = children.ToArray(),
        };
    }

    Element TopBar(Action toggle) => new BoxEl
    {
        Direction = 0,
        Height = TopPaneHeight,
        AlignItems = FlexAlign.Center,
        Fill = Tok.AcrylicBase,
        BorderColor = Tok.StrokeDividerDefault,
        BorderWidth = 1f,
        Children =
        [
            PaneToggleButton(toggle),
            Header is null
                ? new BoxEl { Grow = 1 }
                : new BoxEl
                {
                    Direction = 0,
                    Grow = 1,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(4, 0, 16, 0),
                    Children = [new TextEl(Header) { Size = 14f, Bold = true, Color = Tok.TextPrimary }],
                },
        ],
    };

    Element PaneTitleRow(Action toggle, bool expanded)
    {
        var children = new List<Element>();

        if (ShowBackButton)
            children.Add(PaneGlyphButton(Icons.Back, () => OnBack?.Invoke()));

        children.Add(PaneToggleButton(toggle));

        if (expanded && Header is { Length: > 0 } title)
        {
            children.Add(new BoxEl
            {
                Direction = 0,
                Grow = 1,
                Height = PaneHeaderRowHeight,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(4, 0, 16, 0),
                Children = [new TextEl(title) { Size = 14f, Bold = true, Color = Tok.TextPrimary }],
            });
        }

        return new BoxEl
        {
            Direction = 0,
            Height = TopPaneHeight,
            AlignItems = FlexAlign.Center,
            Children = children.ToArray(),
        };
    }

    static Element PaneToggleButton(Action onClick) => PaneGlyphButton(Icons.Menu, onClick);

    static Element PaneGlyphButton(string glyph, Action onClick) => new BoxEl
    {
        Width = PaneToggleWidth,
        Height = PaneToggleHeight,
        Margin = new Edges4(ItemMarginX, ItemMarginY, ItemMarginX, ItemMarginY),
        Direction = 0,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        OnClick = onClick,
        Children = [new TextEl(glyph) { Size = IconSize, Color = Tok.TextPrimary, FontFamily = Theme.IconFont }],
    };

    static Element[] BuildItems(NavItem[] items, string selected, Action<string> select, bool expanded, bool ownIndicator)
    {
        var result = new Element[items.Length];
        for (int i = 0; i < items.Length; i++)
            result[i] = Item(items[i], selected, select, expanded, ownIndicator);

        return result;
    }

    static float SelectedY(NavItem[] items, string selected)
    {
        float y = 0f;
        foreach (var it in items)
        {
            if (it.IsHeader)
            {
                y += HeaderHeight;
                continue;
            }

            if (it.Key == selected)
                return y + ItemMarginY + (ItemHeight - IndicatorH) * 0.5f;

            y += ItemOuterHeight;
        }

        return -1000f;
    }

    static Element Item(NavItem it, string selected, Action<string> select, bool expanded, bool ownIndicator)
    {
        if (it.IsHeader)
            return HeaderItem(it, expanded);

        bool sel = it.Key == selected;
        var foreground = sel ? Tok.TextPrimary : Tok.TextSecondary;
        var children = new List<Element>
        {
            new BoxEl
            {
                Width = IndicatorW,
                Height = IndicatorH,
                Corners = CornerRadius4.All(2f),
                Fill = ownIndicator && sel ? Tok.AccentDefault : ColorF.Transparent,
                AlignSelf = FlexAlign.Center,
            },
            new BoxEl
            {
                Width = expanded ? IconColumnWidth - IndicatorW : 37f,
                Height = ItemHeight,
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = [new TextEl(it.Glyph) { Size = IconSize, Color = foreground, FontFamily = Theme.IconFont }],
            },
        };

        if (expanded)
        {
            children.Add(new BoxEl
            {
                Direction = 0,
                Grow = 1,
                Height = ItemHeight,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(4, 0, 8, 0),
                Children = [new TextEl(it.Label) { Size = 14f, Color = foreground }],
            });
        }

        return new BoxEl
        {
            Direction = 0,
            Role = AutomationRole.NavigationItem,
            Width = expanded ? float.NaN : PaneToggleWidth,
            Height = ItemHeight,
            Margin = new Edges4(ItemMarginX, ItemMarginY, ItemMarginX, ItemMarginY),
            AlignItems = FlexAlign.Center,
            Corners = Radii.OverlayAll,   // WinUI nav items round to OverlayCornerRadius (8), not ControlCornerRadius (4)
            Fill = sel ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = sel ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = sel ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            OnClick = () => select(it.Key),
            Children = children.ToArray(),
        };
    }

    static Element HeaderItem(NavItem it, bool expanded)
    {
        if (!expanded)
            return new BoxEl { Height = 8 };

        return new BoxEl
        {
            Height = HeaderHeight,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(16, 0, 16, 0),
            Children = [new TextEl(it.Label) { Size = 14f, Bold = true, Color = Tok.TextSecondary }],
        };
    }
}

internal sealed class NavIndicator : Component
{
    public override Element Render()
    {
        float target = UseContext(NavigationView.IndicatorTarget);
        bool visible = target > -500f;
        UseSpring(AnimChannel.TranslateY, visible ? target : 0f, SpringParams.FromResponse(0.30f, 0.85f), visible ? MathF.Round(target) : -1f);
        UseTransition(AnimChannel.Opacity, visible ? 0f : 1f, visible ? 1f : 0f, 150f, Easing.EaseOut, visible);
        return new BoxEl
        {
            Width = 3f,
            Height = 16f,
            Margin = new Edges4(4f, 0f, 0f, 0f),
            Corners = CornerRadius4.All(2f),
            Fill = Tok.AccentDefault,
            AlignSelf = FlexAlign.Start,
        };
    }
}
