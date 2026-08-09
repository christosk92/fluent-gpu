using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>How a <see cref="TabStrip"/> draws itself. <see cref="Chrome"/> is the MUX TabView rail grammar (plates,
/// selected flare, separators, the bottom rail). <see cref="Text"/> is the text-first strip a merged title-bar row
/// wants: no plate, no rail, no separators — weight + opacity carry selection, and one strip-owned sliding underline
/// marks it. Mount-time (a component's plain fields freeze at mount), so a strip never switches modes in place.</summary>
public enum TabStripAppearance : byte { Chrome, Text }

/// <summary>When the "+" new-tab button shows, in the <see cref="TabStripAppearance.Text"/> strip. Gated BY
/// <see cref="TabStrip.IsAddTabButtonVisible"/> (false there means no add button whatever this says), and ignored
/// entirely by <see cref="TabStripAppearance.Chrome"/>, which keeps the plain bool it always had.
/// <list type="bullet">
/// <item><see cref="Never"/> — no button, no slot.</item>
/// <item><see cref="Always"/> — the standing button (the Chrome behaviour, in the text grammar).</item>
/// <item><see cref="OnStripPointerOver"/> — the slot is RESERVED at all times (so the strip's laid-out width, and
/// therefore a title bar's non-client region report, never moves) but the button paints nothing until the pointer is
/// over the strip, then cross-fades in.</item>
/// </list></summary>
public enum TabStripAddButtonVisibility : byte { Never, Always, OnStripPointerOver }

/// <summary>
/// Header-only WinUI-style tab strip for custom title bars. It shares the <see cref="TabViewItem"/> model and the
/// important TabView header metrics, but intentionally renders no selected-content presenter.
/// </summary>
public sealed class TabStrip : Component
{
    public const string PartRoot = "Root";
    /// <summary>Text appearance only: the sliding selection underline. Owned: nothing (pure styling).</summary>
    public const string PartSelectionIndicator = "SelectionIndicator";
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
    /// <summary>Text appearance only: WHEN the "+" shows (see <see cref="TabStripAddButtonVisibility"/>). The default
    /// reproduces the historical always-on button, so this is purely opt-in. MOUNT-TIME, like <see cref="Appearance"/>.</summary>
    public TabStripAddButtonVisibility AddButtonVisibility = TabStripAddButtonVisibility.Always;
    public TabViewCloseButtonOverlayMode CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto;
    public float TabWidth = 320f;
    public float MinTabWidth = 100f;
    public float MaxTabWidth = 360f;
    // Prop (not a raw ColorF) so a theme-dependent fill can be passed as a thunk (Prop.Of(() => Tok.X)) and follow a live
    // theme switch — a raw ColorF here freezes at mount (TabStrip is a long-lived component; its constructor args don't
    // re-read). The default is itself a live semantic thunk so callers that do not style the strip also follow retheme.
    public Prop<ColorF> SelectedFill = Prop.Of(static () => Tok.FillSolidTertiary);
    /// <summary>Chrome (the MUX rail grammar) or Text (the merged-title-bar, text-first strip). MOUNT-TIME.</summary>
    public TabStripAppearance Appearance = TabStripAppearance.Chrome;
    /// <summary>Text appearance: the tab label point size (the hit rect stays 32 DIP tall regardless).</summary>
    public float TextFontSize = 14f;
    /// <summary>Text appearance: the sliding underline's ink. A Prop (like <see cref="SelectedFill"/>) so the default is
    /// a LIVE semantic thunk and a theme switch repaints it in place — the same idiom NavigationView's selection pill
    /// uses (<c>Tok.AccentDefault</c>).</summary>
    public Prop<ColorF> IndicatorFill = Prop.Of(static () => Tok.AccentDefault);
    public TemplateParts? Parts;

    /// <summary>Text appearance: the selected tab's strip-local x (X) and width (Y), handed to the underline through
    /// context — the NavigationView <c>IndicatorTarget</c> idiom, so the indicator retargets its springs without the
    /// strip writing a signal mid-render (a backwards write).</summary>
    internal static readonly Context<Point2> IndicatorTarget = new(default);

    // Text appearance geometry.
    const float TextTabPadX = 12f;        // label inset — also the underline's inset, so the bar tracks the TEXT
    internal const float TextTabHeight = 32f;   // the hit rect, independent of TextFontSize
    const float IndicatorThickness = 2f;
    const float IndicatorMinWidth = 8f;

    /// <summary>Text appearance: the strip-local y of a tab plate's BOTTOM edge — where the selection underline has to
    /// sit. The tab row is <see cref="TitleBar.ExpandedHeight"/> tall with <c>AlignItems=Center</c>, so a
    /// <see cref="TextTabHeight"/> plate spans [(H−h)/2, (H+h)/2] and its bottom is (H+h)/2 = 40 in the stock 48-DIP
    /// row. Derived, never a literal: the underline must follow the plate if either metric is ever re-tuned.
    /// <para>The indicator used to hang off the strip's own bottom (a 48-tall host with <c>AlignItems=End</c>), which
    /// floated it 6 DIP BELOW the tab it marks — reading as a rule under the whole bar rather than as that tab's
    /// underline.</para></summary>
    internal const float TextTabBaseline = (TitleBar.ExpandedHeight + TextTabHeight) * 0.5f;

    // Text appearance: the selected tab's laid-out rect, harvested per tab through Element.OnBoundsChanged (local to
    // the strip's row, which is the underline's own coordinate space). Plain arrays because the component instance
    // outlives every render; the paired handlers are cached so a re-render installs the SAME delegate (an install is a
    // redelivery, and a fresh closure each render would redeliver every frame).
    float[] _tabX = [];
    float[] _tabW = [];
    Action<RectF>[] _tabBounds = [];

    // ── Text appearance: the hover-revealed "+" ────────────────────────────────────────────────────────────────────
    /// <summary>No tab (and no add button) is under the pointer — the resting value of the strip's hover signal.</summary>
    const int NoHover = -1;
    /// <summary>The add button's sentinel in that SAME signal. Reusing the close-button hover tracker (rather than
    /// adding a hover handler to the strip ROOT) is deliberate: an interactive ancestor becomes a hover CONTAINER, and
    /// the scheduler then drives every descendant reveal/opacity affordance in its subtree — which would light all the
    /// text tabs to their hover tier at once (AnimScheduler.Hover's SetHoverDescendants). A sentinel in the existing
    /// leaf-level signal has no such reach: "the strip is hovered" is simply <c>hovered != NoHover</c>, and moving
    /// between a tab and the button is one dispatch (exit then enter), so the reveal never blinks.</summary>
    const int AddHover = -2;
    NodeHandle _addNode;        // the realized "+" — the fade's target
    bool _addFadeArmed;         // false until the first render has settled, so the mount is not itself a fade

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
        var menuOverlay = UseContext(Overlay.Service);
        int count = items.Count;

        var internalSelected = UseSignal(0);
        var selectedSig = SelectedIndex ?? internalSelected;
        int selected = count == 0 ? -1 : Math.Clamp(selectedSig.Value, 0, count - 1);

        var hoveredSig = UseSignal(-1);
        int hovered = hoveredSig.Value;

        // Text appearance: bumped from a tab's arranged-bounds edge so the strip re-renders and re-targets the
        // underline. Read unconditionally (hook order is per-instance-stable either way); nothing bumps it in Chrome.
        var geomVer = UseSignal(0);
        _ = geomVer.Value;

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

        // The "+" slot is RESERVED (mounted at a fixed width) whenever the mode is not Never, and merely painted or not
        // painted by `addRevealed`. A conditional MOUNT would have been the smaller diff, but the strip hugs its
        // content and a title bar reports that hug as one non-client Client region — so mounting on hover would move
        // the region rect on every pointer entry and force the presence into the host's ContentVersion fold. 32 DIP of
        // reserved space inside an island that is already wholly client-hit-tested costs nothing by comparison.
        bool addMounted = IsAddTabButtonVisible
            && (Appearance != TabStripAppearance.Text || AddButtonVisibility != TabStripAddButtonVisibility.Never);
        bool addRevealed = Appearance != TabStripAppearance.Text
            || AddButtonVisibility != TabStripAddButtonVisibility.OnStripPointerOver
            || hovered != NoHover;

        // The cross-fade. Seeded through a motion TOKEN (never a raw spring) so the reduced-motion policy travels with
        // it — ControlFast is KeepFade, i.e. an opacity cross-fade survives reduced motion because a fade is not
        // motion. The RESTING Opacity below equals the terminal, per the settled-track-frees-without-resetting rule.
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || _addNode.IsNull) { _addFadeArmed = true; return; }
            if (_addFadeArmed)
            {
                var fade = MotionTok.ControlFast;
                anim.SeedValue(_addNode, AnimChannel.Opacity, addRevealed ? 1f : 0f, in fade,
                               from: addRevealed ? 0f : 1f);
            }
            _addFadeArmed = true;
        }, DepKey.From(addRevealed ? 1 : 0));

        if (Appearance == TabStripAppearance.Text)
            return RenderText(items, count, selected, hovered, hoveredSig, geomVer, menuOverlay, Select, Close, Add,
                              addMounted, addRevealed);

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
                () => Select(index), () => Close(index), hoveredSig, menuOverlay);
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
                Action select, Action close, Signal<int> hoveredSig, IOverlayService menuOverlay)
    {
        float tabW = Math.Clamp(TabWidth, MinTabWidth, MaxTabWidth);
        var main = new List<Element>(2);
        if (item.Icon is { Length: > 0 } icon)
        {
            main.Add(new TextEl(icon)
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
        main.Add(Parts.Apply(PartTabLabel, label));

        var content = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Draggable = item.Drag,
                Children = main.ToArray(),
            },
        };

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
        if (item.ContextMenu is { } menu) plate = plate.WithContextMenu(menuOverlay, menu);

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
            // The whole tab header is the drop surface (the spring-load hover area) — not the inner body the drag
            // source sits on, whose padding gaps would make the dwell drop out as the pointer wandered.
            DropTarget = item.DropTarget,
            Children = layers.ToArray(),
        };
    }

    // ── Text appearance ───────────────────────────────────────────────────────────────────────────────────────────
    //  No plate, no flare, no separators, no rail: a tab IS its label. Selection reads as weight + the PRIMARY text
    //  token; an unselected tab is the theme's SECONDARY text token and ramps to primary under the pointer. One
    //  strip-owned underline slides between tabs.
    //
    //  WHY TOKENS AND NOT OPACITY. The strip used to express de-selection as `Opacity = 0.6` on the tab plate (ramping
    //  to 0.85 on hover), which is a different thing that happens to look similar: an alpha multiplier over the whole
    //  subtree, applied on top of a foreground that was ALREADY TextSecondary. That compounds — an inactive label
    //  rendered at 0.6 × secondary, i.e. below every "dimmed text" rung the theme actually defines, and it dimmed the
    //  tab's icon and its close-button glyph by the same factor even though neither is expressing selection. It is
    //  also invisible to theming: a high-contrast or custom palette can retune TextSecondary, but it cannot retune a
    //  hard-coded 0.6. The de-selection is now exactly the token ladder — TextSecondary at rest, TextPrimary on hover
    //  and when selected — with the plate left at full strength.
    //
    //  The hover ramp is still the engine's own eased fade, not a hovered-index branch: TextEl.HoverColor interpolates
    //  with the nearest interactive ancestor's HoverT (SceneRecorder.ResolveTextColorCore), and the plate IS that
    //  ancestor — so it eases in and back out on its own, with no per-tab state machine.

    /// <summary>Size the per-tab geometry mirror + its CACHED bounds handlers to the current tab count. The handlers are
    /// equality-gated (no signal write when a re-arrange produced the same rect), so a redelivery is free.</summary>
    void EnsureTabGeometry(int count, Signal<int> geomVer)
    {
        if (_tabBounds.Length == count) return;
        Array.Resize(ref _tabX, count);
        Array.Resize(ref _tabW, count);
        var handlers = new Action<RectF>[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            handlers[i] = r =>
            {
                if (_tabX[index] == r.X && _tabW[index] == r.W) return;
                _tabX[index] = r.X;
                _tabW[index] = r.W;
                geomVer.Value++;      // written during layout ⇒ the strip re-renders NEXT frame (never re-entrant)
            };
        }
        _tabBounds = handlers;
    }

    Element RenderText(IReadOnlyList<TabViewItem> items, int count, int selected, int hovered,
                       Signal<int> hoveredSig, Signal<int> geomVer, IOverlayService menuOverlay,
                       Action<int> select, Action<int> close, Action add, bool addMounted, bool addRevealed)
    {
        EnsureTabGeometry(count, geomVer);

        int tail = addMounted ? 1 : 0;
        var kids = new Element[count + tail];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            // Hover-only close for EVERY tab, INCLUDING the selected one (Chrome keeps a standing × on the selected
            // plate; a text strip has no plate to anchor it, and a permanent × beside bold text reads as noise). Auto
            // therefore resolves to hover-gated here — only an explicit `Always` pins the button.
            // The SLOT is reserved whenever the tab is closable, independent of hover: a text tab is content-hug, so a
            // hover-MOUNTED × changed the tab's width and reflowed everything after it (and slid the underline) on
            // every hover. Same reserved-slot contract as the '+' button — only the glyph's opacity/hit-test toggles.
            bool closeSlot = items[index].IsClosable;
            bool closeVisible = closeSlot &&
                                (CloseButtonOverlayMode == TabViewCloseButtonOverlayMode.Always ||
                                 hovered == index);
            kids[i] = TextTab(index, items[index], index == selected, closeSlot, closeVisible,
                              () => select(index), () => close(index), hoveredSig, menuOverlay);
        }
        if (addMounted) kids[count] = TextAddButton(add, addRevealed, hoveredSig);

        // The strip HUGS (same contract as Chrome): TitleBar reports it wholesale as ONE TitleBarHit.Client island, so
        // any Grow filler in here would turn caption drag space into dead client area.
        var row = new BoxEl
        {
            Direction = 0,
            Height = TitleBar.ExpandedHeight,
            AlignItems = FlexAlign.Center,
            Shrink = 1f,
            JustifySelf = FlexAlign.Start,
            AlignSelf = FlexAlign.Stretch,
            Children = kids,
        };

        // The underline target, in the ROW's coordinate space — which is also the ZStack's, since the row is a
        // stack child at the origin. Inset by the label padding so the bar tracks the TEXT, not the hit rect.
        float ux = 0f, uw = 0f;
        if ((uint)selected < (uint)_tabW.Length && _tabW[selected] > 0f)
        {
            ux = _tabX[selected] + TextTabPadX;
            uw = MathF.Max(_tabW[selected] - 2f * TextTabPadX, IndicatorMinWidth);
        }

        var stack = new BoxEl
        {
            ZStack = true,
            Height = TitleBar.ExpandedHeight,
            Shrink = 1f,
            Justify = FlexJustify.Start,
            Children =
            [
                row,
                // Strip-owned, hit-test invisible, and OUT of the row — so it can never disturb tab layout. Its own
                // box always ends inside the selected tab, so it can never widen the ZStack's hug either.
                Ctx.Provide(IndicatorTarget, new Point2(ux, uw),
                    Embed.Comp(() => new TabTextIndicator { Fill = IndicatorFill, Parts = Parts })),
            ],
        };
        return Parts.Apply(PartRoot, stack) with { Children = stack.Children };
    }

    Element TextTab(int index, TabViewItem item, bool selected, bool closeSlot, bool closeVisible,
                    Action select, Action close, Signal<int> hoveredSig, IOverlayService menuOverlay)
    {
        var main = new List<Element>(2);
        if (item.Icon is { Length: > 0 } icon)
        {
            main.Add(new TextEl(icon)
            {
                Size = 16f,
                FontFamily = Theme.IconFont,
                Margin = new Edges4(0f, 0f, 8f, 0f),
                Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
                // A==0 on the selected tab ⇒ "no state color" (the recorder leaves Color alone), so a selected glyph
                // simply stays primary instead of ramping toward a colour it is already at.
                HoverColor = selected ? default : Tok.TextPrimary,
            });
        }

        var label = new TextEl(item.Header)
        {
            Size = TextFontSize,
            // Selection is WEIGHT + the PRIMARY text token; de-selection is the SECONDARY token, ramping to primary
            // under the pointer. See the section header for why this is a token ladder and not a plate opacity tier.
            Weight = selected ? (ushort)650 : (ushort)0,
            Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
            HoverColor = selected ? default : Tok.TextPrimary,
            Grow = 1f,
            Shrink = 1f,
            Trim = TextTrim.CharacterEllipsis,
        };
        main.Add(Parts.Apply(PartTabLabel, label));

        var content = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Draggable = item.Drag,
                Children = main.ToArray(),
            },
        };

        if (closeSlot)
        {
            // The slot is ALWAYS mounted for a closable tab; only the glyph fades and only a visible × takes hits.
            // A hover-mounted × changed the content-hug tab's width — layout flicker on every hover (and the underline,
            // anchored to the tab's measured width, slid with it).
            var closeButton = new BoxEl
            {
                Direction = 0,
                Width = 20f,
                Height = 20f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Margin = new Edges4(6f, 0f, 0f, 0f),
                Corners = Radii.ControlAll,
                Fill = ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button,
                OnClick = close,
                TabStop = false,
                Opacity = closeVisible ? 1f : 0f,
                HitTestVisible = closeVisible,
                Children =
                [
                    new TextEl(Icons.Cancel)
                    {
                        Size = 10f,
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
            Height = TextTabHeight,                                  // the comfortable hit rect, whatever TextFontSize is
            AlignItems = FlexAlign.Center,
            Padding = closeSlot
                ? new Edges4(TextTabPadX, 0f, 6f, 0f)
                : new Edges4(TextTabPadX, 0f, TextTabPadX, 0f),
            // No Fill/HoverFill/PressedFill at all: a text strip has no plate. And no Opacity tier either — the state
            // ramp is the FOREGROUND TOKEN on the label/glyph above, so it composes with the theme instead of dimming
            // the whole subtree (icon and close glyph included) by a hard-coded alpha. This node stays the interactive
            // ancestor whose eased HoverT drives that colour ramp.
            Role = AutomationRole.Tab,
            OnClick = select,
            // Middle-click close (WinUI TabViewItem.cpp:418-425/:449-462 — the dispatcher delivers Button==2 on a
            // middle-release over the same node). Text appearance only; Chrome keeps its existing behaviour verbatim.
            OnPointerPressed = e =>
            {
                if (e.Button == 2 && item.IsClosable) { close(); e.Handled = true; }
            },
            OnHoverMove = _ => { if (hoveredSig.Peek() != index) hoveredSig.Value = index; },
            OnPointerExit = () => { if (hoveredSig.Peek() == index) hoveredSig.Value = -1; },
            Children = content.ToArray(),
        };
        plate = Parts.Apply(PartTabItem, plate) with { OnClick = select, Role = AutomationRole.Tab, Children = plate.Children };
        if (item.ContextMenu is { } menu) plate = plate.WithContextMenu(menuOverlay, menu);

        // Content-hug between the MinTabWidth floor and the MaxTabWidth cap (no fixed Width — that is the Chrome
        // grammar). OnBoundsChanged on the WRAPPER is what feeds the underline.
        return new BoxEl
        {
            Key = "tab#" + index,
            ZStack = true,
            MinWidth = MinTabWidth,
            MaxWidth = MaxTabWidth,
            Height = TextTabHeight,
            Shrink = 1f,
            DropTarget = item.DropTarget,
            OnBoundsChanged = (uint)index < (uint)_tabBounds.Length ? _tabBounds[index] : null,
            Children = [plate],
        };
    }

    /// <summary>The shared "+" plate. <paramref name="glyphInk"/> lets the text grammar quiet the glyph without
    /// duplicating the box (Chrome's "+" is a primary-ink control on a rail; the text strip's is a secondary-ink hint).</summary>
    BoxEl AddPlate(Action add, Edges4 margin, ColorF glyphInk) => new()
    {
        Direction = 0,
        Width = 32f,
        Height = 24f,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Margin = margin,
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
                Color = glyphInk,
                PressedColor = Tok.TextSecondary,
            },
        ],
    };

    /// <summary>
    /// The TEXT strip's "+": the same plate, quieted to <c>TextSecondary</c>, keeping the strip's own hover-plate
    /// idiom (FillSubtleSecondary/Tertiary), tooltipped, and — under
    /// <see cref="TabStripAddButtonVisibility.OnStripPointerOver"/> — resting at zero opacity.
    /// <para>It stays HIT-TESTABLE while invisible, on purpose: it is what makes its own reserved slot part of the
    /// strip's hover area, so approaching the "+" from ANY direction reveals it. (It cannot be clicked blind — the
    /// pointer has to be over it to press it, and being over it is exactly what reveals it.)</para></summary>
    Element TextAddButton(Action add, bool revealed, Signal<int> hoverSig)
    {
        // No Margin: the last tab's own 12-DIP label padding is the separation (the island contract — a gap between two
        // island children is dead window-drag space). A ToolTip wrapper mirrors Width/Height but NOT Margin anyway.
        var button = AddPlate(add, default, Tok.TextSecondary) with
        {
            Opacity = revealed ? 1f : 0f,
            OnHoverMove = _ => { if (hoverSig.Peek() != AddHover) hoverSig.Value = AddHover; },
            OnPointerExit = () => { if (hoverSig.Peek() == AddHover) hoverSig.Value = NoHover; },
        };
        var applied = Parts.Apply(PartAddButton, button) with { OnClick = add, Role = AutomationRole.Button };
        applied = applied with { OnRealized = TemplateParts.Chain<NodeHandle>(h => _addNode = h, applied.OnRealized) };
        return ToolTip.Wrap(applied, Loc.Get(Strings.TabStrip.NewTab));
    }

    Element AddButton(Action add, bool followsSelected, bool railed = true)
    {
        var button = AddPlate(add, new Edges4(3f, 0f, 0f, 3f), Tok.TextPrimary);
        var applied = Parts.Apply(PartAddButton, button) with { OnClick = add, Role = AutomationRole.Button };
        // Text appearance: there is no rail, so the button is the whole thing.
        if (!railed) return applied;
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

/// <summary>
/// The Text-appearance strip's sliding selection underline. The target (strip-local x + width) rides a CONTEXT value —
/// the NavigationView <c>NavIndicator</c> idiom — so retargeting costs the parent no signal write mid-render.
///
/// <para><b>Layout is the resting state; the springs animate only the DELTA (FLIP).</b> A window resize CANCELS every
/// in-flight structural track by design — <c>AnimScheduler.SnapStructuralToLayout</c>/<c>CancelStructuralAll</c> collapse
/// TranslateX/ScaleX straight to the final bounds (docs/design/subsystems/gpu-renderer.md §window-resize snap, whose
/// damage accumulator has rules built around that cancellation; docs/plans/butter-smooth-resize-v2.md:263 "FLIP capture
/// is skipped when `resized` — resizes snap by design"). That is NOT a bug to work around: it means an indicator whose
/// POSITION lives in the anim channel collapses to the strip origin on the first resize and stays there (observed).
/// So the bar's x/width are pure layout truth (Margin.Left/Width, re-derived from the selected tab's arranged rect in
/// every state — selection change, tab close, reorder, resize), and the springs carry only the leftover offset/scale
/// from the PREVIOUS laid-out position, decaying to identity. Cancel them at any instant and the bar is already exactly
/// under the selected tab.</para>
///
/// <para><b>Hug safety:</b> the strip root is a ZStack, which measures to its widest child — but this host's desired
/// width is the indicator's right edge, which is always inside the selected tab and therefore inside the row. It can
/// never widen the strip's hug (and so never inflate the TitleBarHit.Client island the title bar reports).</para>
///
/// <para><b>Why the bar is a CHILD of the component root, not the root itself:</b> a component anchor mirrors its
/// child's Width/Height but NOT its Margin (<c>Reconciler.MirrorParticipation</c>), so a root that positions itself
/// with Margin+Width lands in the wrong slot. The root here keeps a stable, unsized footprint and the animation targets
/// the inner bar handle (the <c>Expander</c> chevron idiom).</para>
///
/// <para><b>Reduced motion:</b> seeded through <c>SeedValue(..., MotionTokenDef)</c> rather than the raw
/// <c>UseSpring</c> hook — the raw spring path carries no <see cref="ReducedMotionPolicy"/>, so it would slide even
/// under reduced motion. A token carries the policy and the scheduler snaps at the SEED (reduced-motion-as-a-value:
/// authoring code never branches on the mutable global).</para>
/// </summary>
internal sealed class TabTextIndicator : Component
{
    public Prop<ColorF> Fill = Prop.Of(static () => Tok.AccentDefault);
    public TemplateParts? Parts;

    const float Thickness = 2f;

    // The previous target, for the FLIP delta. Component-local state on an instance that outlives every render.
    float _prevX, _prevW;
    bool _seeded;
    NodeHandle _bar;

    public override Element Render()
    {
        Point2 target = UseContext(TabStrip.IndicatorTarget);
        float x = target.X, w = target.Y;
        bool visible = w > 0.5f;

        // FLIP: where the bar must appear to come FROM to land on its new laid-out position/size.
        float fromDx = _seeded && visible ? _prevX - x : 0f;
        float fromScale = _seeded && visible && _prevW > 0.5f ? _prevW / w : 1f;
        if (visible) { _prevX = x; _prevW = w; _seeded = true; }

        var motion = MotionTokenDef.SpringOf(MotionSprings.SelectorPill, ReducedMotionPolicy.SnapEnd);
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || _bar.IsNull) return;
            anim.SeedValue(_bar, AnimChannel.TranslateX, 0f, in motion, from: fromDx);
            anim.SeedValue(_bar, AnimChannel.ScaleX, 1f, in motion, from: fromScale);
        }, DepKey.From(HashCode.Combine(MathF.Round(x), MathF.Round(w), visible)));

        var bar = new BoxEl
        {
            Width = visible ? w : 0f,
            Height = Thickness,
            Margin = new Edges4(x, 0f, 0f, 0f),   // pure layout truth — the resize snap lands here
            TransformOriginX = 0f,                // the FLIP scale pivots on the bar's LEFT edge
            TransformOriginY = 1f,
            HitTestVisible = false,
            Fill = Fill,
            // State-dependent RESTING opacity (the NavIndicator gotcha): a settled track frees WITHOUT resetting
            // Opacity, so the static must equal the terminal or a later re-render re-asserts 1f and shows a stale bar.
            Opacity = visible ? 1f : 0f,
        };
        var applied = Parts.Apply(TabStrip.PartSelectionIndicator, bar);
        applied = applied with { OnRealized = TemplateParts.Chain<NodeHandle>(h => _bar = h, applied.OnRealized) };

        // The unsized, hit-test-invisible host: stable layout participation for the component anchor to mirror.
        // Its height is the TAB PLATE's baseline, not the strip's — AlignItems=End then parks the bar on the plate's
        // bottom edge (y = 38..40 in the stock 48-DIP row) instead of the strip's floor (y = 46..48), which left the
        // underline hanging 6 DIP under the tab it marks. TabStrip.TextTabBaseline derives the number from the row
        // height and the plate height, so re-tuning either keeps the bar attached.
        return new BoxEl
        {
            Direction = 0,
            Height = TabStrip.TextTabBaseline,
            Justify = FlexJustify.Start,
            AlignItems = FlexAlign.End,
            HitTestVisible = false,
            Children = [applied],
        };
    }
}
