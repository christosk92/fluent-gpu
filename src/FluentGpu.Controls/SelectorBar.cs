using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A WinUI SelectorBar: a segmented horizontal row of text (optionally icon+text) items. The selected item
/// is marked by a SHORT CENTERED accent pill flush with the item bottom (not a full-width underline, not bold); text
/// stays <see cref="Tok.TextPrimary"/> at rest for every item and DIMS on hover/press — backgrounds are transparent
/// in every state (SelectorBar_themeresources.xaml:21-25). Stateless — the caller owns <c>selected</c> and reacts to
/// <c>onSelect</c>. Keyboard: ONE tab stop (the selected item — TabNavigation=Once, SelectorBar.xaml:13); Left/Right/
/// Home/End move focus item-to-item and SELECTION FOLLOWS FOCUS (SelectorBarTests.cs:63-66); focus entering a bar
/// without a selection auto-selects (SelectorBar.cpp:98-126). Per-part restyling goes through the optional
/// <c>parts</c> (see <see cref="TemplateParts"/> for the contract).</summary>
public static class SelectorBar
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a parts customization cannot win those).
    /// <summary>Each item's clickable box (WinUI SelectorBarItem). The SAME modifier runs for every item — branch on
    /// caller state for per-item styling. Owned: OnClick (select), Role, TabStop (roving stop), OnKeyDown,
    /// OnFocusChanged, OnRealized (handle capture, chained).</summary>
    public const string PartItem = "Item";
    /// <summary>The short centered selection pill under each item (mounted only on the selected item). Owned:
    /// nothing — pure styling.</summary>
    public const string PartPill = "Pill";

    // ── WinUI dims (SelectorBar.xaml / SelectorBar_themeresources.xaml) ──
    internal const float PillWidth = 4f;        // SelectorBarItemPillWidth (themeresources:97)
    internal const float PillHeight = 3f;       // SelectorBarItemPillHeight (themeresources:96)
    internal const float PillScaleX = 4f;       // PillTransform.ScaleX selected target (SelectorBar.xaml:108-110) → 16px shown
    internal const float IconScale = 0.8f;      // SelectorBarItemIconScale (themeresources:98)
    internal const float IconTextSpacing = 8f;  // SelectorBarItemSpacing (themeresources:99)

    /// <summary><paramref name="icons"/> = optional per-item glyphs (WinUI SelectorBarItem.Icon, SelectorBar.idl):
    /// rendered before the text at 0.8 scale with the −2,0 icon margin, recolored by the same foreground states.
    /// <paramref name="style"/> = an optional non-WinUI look (e.g. a page-title pivot: large semibold labels, no pill,
    /// a subtle hover plate); null keeps the stock WinUI template byte-identical. Behaviour (roving keys, the single
    /// tab stop, OnChange, parts) is the same in every style.</summary>
    public static Element Create(IReadOnlyList<string> items, Signal<int>? selectedIndex = null, Action<int>? onChange = null,
                                 TemplateParts? parts = null, IReadOnlyList<string?>? icons = null, SelectorBarStyle? style = null)
        => Embed.Comp(new Props(items, icons, selectedIndex, onChange, parts, style), () => new SelectorBarCore());

    /// <summary>Controlled props are RE-PUSHED live to the reused core (<c>Embed.Comp(props, …)</c>) — a reused
    /// ComponentEl never re-runs its factory, so the items stay LIVE across parent re-renders via the props channel.
    /// The selected index is a caller <see cref="Signal{T}"/> read directly in the core (null ⇒ auto-materialize); a
    /// select/roving move WRITES the signal then fires OnChange. The core reads props with <c>UseProps</c>.</summary>
    internal sealed record Props(IReadOnlyList<string> Items, IReadOnlyList<string?>? Icons, Signal<int>? Selected,
                                 Action<int>? OnChange, TemplateParts? Parts, SelectorBarStyle? Style = null);
}

/// <summary>Optional look for SelectorBar. null = the stock WinUI look, byte-identical.</summary>
/// <remarks>Every field defaults to the stock value, so <c>new SelectorBarStyle()</c> differs from null only in what the
/// stock template leaves implicit (an explicit 400 label weight, transparent state fills). The label weight follows
/// selection (<see cref="RestWeight"/> / <see cref="SelectedWeight"/>); the hover/press fills ride the item box's
/// engine-serviced HoverFill/PressedFill ramp (the 83 ms ControlFaster fade) on the item's ControlCornerRadius (4).</remarks>
public sealed record SelectorBarStyle
{
    public float LabelSize { get; init; } = 14f;
    public float LineHeight { get; init; } = float.NaN;      // NaN = font-natural (today's)
    public string? FontFamily { get; init; }                  // null = default UI face
    public float CharSpacing { get; init; }                   // 1/1000 em, 0 = none
    public int RestWeight { get; init; } = 400;
    public int SelectedWeight { get; init; } = 400;
    public ColorF? RestColor { get; init; }                   // null = stock (TextPrimary)
    public ColorF? SelectedColor { get; init; }               // null = stock (TextPrimary)
    public ColorF? HoverColor { get; init; }                  // null = stock (TextSecondary) — applies to both legs
    public ColorF? PressedColor { get; init; }                // null = stock rule (selected ? TextSecondary : TextTertiary)
    public ColorF? HoverFill { get; init; }                   // null = transparent (stock)
    public ColorF? PressedFill { get; init; }                 // null = transparent (stock)
    public bool ShowPill { get; init; } = true;               // false = no pill and NO reserved pill slot
    public Edges4? ItemPadding { get; init; }                 // null = stock (12,10,12,7)
    public float ItemHeight { get; init; } = float.NaN;       // NaN = content height (stock)
    public float ItemGap { get; init; }                       // gap between items, 0 = stock
    public float LeadingInset { get; init; }                  // e.g. -8: pulls the first item left so its TEXT aligns with the page edge
}

/// <summary>The stateful core: captures item node handles (for the roving focus moves) and routes the arrow keys —
/// selection follows focus, like the WinUI ItemsView host (SelectorBar.xaml:29-38).</summary>
internal sealed class SelectorBarCore : Component
{
    // PART_SelectionVisual storyboard: PillTransform.ScaleX → 4 + Opacity → 1 over ComboBoxItemScaleAnimationDuration
    // = 167ms (ComboBox_themeresources.xaml:330), KeySpline 0,0,0,1 (SelectorBar.xaml:108-113). The final 16px pill
    // mounts on select with an enter terminal at ScaleX 1/4 + opacity 0 through the same 167ms 0,0,0,1 tween — the
    // engine's insert/remove substitute for WinUI's ScaleX storyboard; deselect plays the reverse fade (WinUI snaps
    // the rect back to base — small sanctioned deviation, per the parity plan).
    private static readonly LayoutTransition PillTransition = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Motion.ControlFast, Easing.FluentPopOpen),
        Enter: new EnterExit(Sx: 1f / SelectorBar.PillScaleX, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: 1f / SelectorBar.PillScaleX, Opacity: 0f, Active: true));

    public override Element Render()
    {
        // Hooks — stable order, unconditionally, before any early-out.
        var p = UseProps<SelectorBar.Props>();   // re-pushed live props (items stay current across re-renders)
        var hooks = UseContext(InputHooks.Current);
        var handles = UseRef(new List<NodeHandle>()).Value;
        var own = UseSignal(-1);                 // auto-materialize (unconditional hook)
        var sig = p.Selected ?? own;             // caller's value signal, else the internal one (one code path)
        int selected = sig.Value;                // read directly (live); a programmatic write re-skins with no OnChange echo

        int count = p.Items?.Count ?? 0;
        while (handles.Count < count) handles.Add(NodeHandle.Null);

        // The single roving tab stop: the selected item, or the first when nothing is selected (TabNavigation=Once,
        // SelectorBar.xaml:13 — focus enters the bar exactly once).
        int tabStop = (uint)selected < (uint)count ? selected : 0;

        void MoveTo(int target)
        {
            if ((uint)target >= (uint)count) return;
            sig.Value = target; p.OnChange?.Invoke(target);      // selection follows focus (SelectorBarTests.cs:63-66)
            if (target < handles.Count && !handles[target].IsNull)
                (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(handles[target]);   // keyboard move → focus visual
        }

        void OnItemKey(int i, KeyEventArgs a)
        {
            if (a.Handled || count == 0) return;
            // The WinUI items host is an ItemsView whose navigation keys include Left/Right/Home/End
            // (ItemsViewInteractions.cpp:599-610); focus moves item-to-item and the SelectorBar syncs SelectedItem
            // to the focused item (OnItemsViewSelectedItemPropertyChanged, SelectorBar.cpp:89-96).
            int target = a.KeyCode switch
            {
                Keys.Left => i - 1,
                Keys.Right => i + 1,
                Keys.Home => 0,
                Keys.End => count - 1,
                _ => int.MinValue,
            };
            if (target == int.MinValue) return;
            a.Handled = true;                                    // swallowed at the edges too
            if (target != i) MoveTo(target);
        }

        var st = p.Style;                        // null = the stock WinUI look (every branch below keeps it byte-identical)
        bool showPill = st?.ShowPill ?? true;

        var tabs = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            Action select = () => { sig.Value = index; p.OnChange?.Invoke(index); };
            Action<NodeHandle> onRealized = h => { if (index < handles.Count) handles[index] = h; };

            // Foreground states (SelectorBar_themeresources.xaml): rest/Selected = TextFillColorPrimary (:16, :18);
            // PointerOver = TextFillColorSecondary (:17) for BOTH legs (selected hover dims too, SelectorBar.xaml:
            // 118-119); Pressed = TextFillColorTertiary (:19) — but SelectedPressed stays on the PointerOver
            // brush/Secondary (SelectorBar.xaml:135-136). Backgrounds transparent in every state (:21-25): no
            // hover/press fill plate.
            ColorF pressedFg = isSelected ? Tok.TextSecondary : Tok.TextTertiary;
            // SelectorBarStyle (null = stock): only the fields a style names diverge; each unset one resolves to the
            // stock value above, so the stock tree below is untouched when no style is passed.
            ColorF restFg = Tok.TextPrimary, hoverFg = Tok.TextSecondary;
            if (st is not null)
            {
                restFg = (isSelected ? st.SelectedColor : st.RestColor) ?? Tok.TextPrimary;
                hoverFg = st.HoverColor ?? Tok.TextSecondary;
                pressedFg = st.PressedColor ?? pressedFg;
            }

            var label = new TextEl(p.Items![index])
            {
                Size = 14f,                          // ControlContentThemeFontSize, FontWeight Normal (SelectorBar.xaml:58-59)
                Color = restFg,
                HoverColor = hoverFg,
                PressedColor = pressedFg,
            };
            if (st is not null)
                label = label with
                {
                    Size = st.LabelSize,
                    LineHeight = st.LineHeight,
                    FontFamily = st.FontFamily,
                    CharSpacing = st.CharSpacing,
                    Weight = (ushort)Math.Clamp(isSelected ? st.SelectedWeight : st.RestWeight, 1, 999),   // weight follows selection
                };

            string? glyph = p.Icons is { } ic && index < ic.Count ? ic[index] : null;
            Element[] rowKids = glyph is { Length: > 0 }
                ? [new TextEl(glyph)
                   {
                       FontFamily = Theme.IconFont,
                       Size = 16f * SelectorBar.IconScale,       // 16px IconElement × SelectorBarItemIconScale 0.8 (SelectorBar.xaml:186-188)
                       Color = restFg,                           // PART_IconVisual recolors with the same states (SelectorBar.xaml:78-79, :92-93)
                       HoverColor = hoverFg,
                       PressedColor = pressedFg,
                       Margin = new Edges4(-2, 0, -2, 0),        // SelectorBarItemIconVisualMargin −2,0 (themeresources:30)
                       AlignSelf = FlexAlign.Center,
                   },
                   label]
                : [label];

            // SelectorBarItemPadding applies to the icon/text StackPanel only (Margin="{TemplateBinding Padding}",
            // SelectorBar.xaml:174-177) — NOT around the pill row, so the item is text + 20 tall.
            var content = new BoxEl
            {
                Direction = 0,
                Gap = SelectorBar.IconTextSpacing,   // StackPanel Spacing = SelectorBarItemSpacing 8 (SelectorBar.xaml:178, themeresources:99)
                AlignItems = FlexAlign.Center,
                Padding = st?.ItemPadding ?? new Edges4(12, 10, 12, 7), // SelectorBarItemPadding (themeresources:32)
                Children = rowKids,
            };

            // PART_SelectionVisual: 4×3 base rect, RadiusX 0.5/RadiusY 1 (SelectorBar.xaml:200-214, themeresources:
            // 96-97), shown at ScaleX 4 → 16px when selected; SelectionVisualMargin = 0 (themeresources:33) keeps it
            // flush with the item bottom.
            var pill = new BoxEl
            {
                Width = SelectorBar.PillWidth * SelectorBar.PillScaleX,
                Height = SelectorBar.PillHeight,
                Corners = CornerRadius4.All(1f),     // RadiusX 0.5/RadiusY 1 — no elliptical corners in the engine; 1px reads the same
                Fill = Tok.AccentDefault,            // SelectorBarItemPillFill = AccentFillColorDefaultBrush (themeresources:9)
                Animate = PillTransition,
            };
            // The pill's row is ALWAYS reserved (PART_SelectionVisual occupies grid row 1 at Opacity 0 when
            // unselected, SelectorBar.xaml:200-210), so selection never changes the item height.
            var pillSlot = new BoxEl
            {
                Width = SelectorBar.PillWidth * SelectorBar.PillScaleX,
                Height = SelectorBar.PillHeight,
                Children = isSelected && showPill ? [p.Parts.Apply(SelectorBar.PartPill, pill)] : [],
            };

            var item = new BoxEl
            {
                Direction = 1,
                AlignItems = FlexAlign.Center,
                Corners = Radii.ControlAll,          // ControlCornerRadius (SelectorBar.xaml:53)
                Role = AutomationRole.Tab,
                TabStop = index == tabStop,          // roving single stop (TabNavigation=Once, SelectorBar.xaml:13)
                FocusVisualMargin = Edges4.All(-2f), // SelectorBarItemFocusVisualMargin = −2 (themeresources:34)
                OnClick = select,
                OnKeyDown = a => OnItemKey(index, a),
                // Focus entering a bar with no selection auto-selects the focused item (SelectorBar::OnGotFocus,
                // SelectorBar.cpp:98-126).
                OnFocusChanged = got => { if (got && selected < 0) { sig.Value = index; p.OnChange?.Invoke(index); } },
                OnRealized = onRealized,
                Children = [content, pillSlot],
            };
            if (st is not null)
                item = item with
                {
                    // ShowPill false: no pill AND no reserved pill row — the item is just its label box.
                    Children = showPill ? [content, pillSlot] : [content],
                    // Optional hover/press plate on the item's ControlCornerRadius: the engine's HoverFill/PressedFill
                    // ramp (HoverFade/PressFade, the 83 ms ControlFaster default). Transparent = the stock no-plate.
                    HoverFill = st.HoverFill ?? ColorF.Transparent,
                    PressedFill = st.PressedFill ?? ColorF.Transparent,
                    // A fixed item height centres the label box vertically (NaN = content height, stock).
                    Height = st.ItemHeight,
                    Justify = float.IsNaN(st.ItemHeight) ? FlexJustify.Start : FlexJustify.Center,
                    // LeadingInset pulls the FIRST item outward so its text (not its padding) sits on the page edge.
                    Margin = index == 0 && st.LeadingInset != 0f ? new Edges4(st.LeadingInset, 0, 0, 0) : default,
                };
            // Parts: restyle anything; the select/roving mechanics always win.
            var styled = p.Parts.Apply(SelectorBar.PartItem, item);
            tabs[index] = styled with
            {
                OnClick = select,
                Role = AutomationRole.Tab,
                TabStop = item.TabStop,
                OnKeyDown = item.OnKeyDown,
                OnFocusChanged = item.OnFocusChanged,
                OnRealized = TemplateParts.Chain(onRealized, styled.OnRealized),
            };
        }

        return new BoxEl
        {
            Direction = 0,
            Gap = st?.ItemGap ?? 0f,             // horizontal StackLayout, no Spacing (SelectorBar.xaml:35-37)
            Padding = new Edges4(0, 4, 0, 4),    // SelectorBarPadding 0,4 (themeresources:26)
            AlignItems = FlexAlign.Center,       // SelectorBarItem VerticalAlignment=Center (SelectorBar.xaml:55)
            // No container Role: only the items expose tab-like peers (SelectorBarItemAutomationPeer.cpp).
            Children = tabs,
        };
    }
}
