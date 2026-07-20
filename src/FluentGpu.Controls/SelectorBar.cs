using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

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
    /// rendered before the text at 0.8 scale with the −2,0 icon margin, recolored by the same foreground states.</summary>
    public static Element Create(IReadOnlyList<string> items, int selected, Action<int> onSelect,
                                 TemplateParts? parts = null, IReadOnlyList<string?>? icons = null)
        => Embed.Comp(new Props(items, icons, selected, onSelect, parts), () => new SelectorBarCore());

    /// <summary>Controlled props are RE-PUSHED live to the reused core (<c>Embed.Comp(props, …)</c>) — a reused
    /// ComponentEl never re-runs its factory, so the items/selection stay LIVE across parent re-renders via the props
    /// channel (record value equality coalesces an unchanged re-push). The core reads them with <c>UseProps</c>.</summary>
    internal sealed record Props(IReadOnlyList<string> Items, IReadOnlyList<string?>? Icons, int Selected,
                                 Action<int> OnSelect, TemplateParts? Parts);
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
        var p = UseProps<SelectorBar.Props>();   // re-pushed live props (items/selection stay current across re-renders)
        var hooks = UseContext(InputHooks.Current);
        var handles = UseRef(new List<NodeHandle>()).Value;

        int count = p.Items?.Count ?? 0;
        while (handles.Count < count) handles.Add(NodeHandle.Null);

        // The single roving tab stop: the selected item, or the first when nothing is selected (TabNavigation=Once,
        // SelectorBar.xaml:13 — focus enters the bar exactly once).
        int tabStop = (uint)p.Selected < (uint)count ? p.Selected : 0;

        void MoveTo(int target)
        {
            if ((uint)target >= (uint)count) return;
            p.OnSelect(target);                                  // selection follows focus (SelectorBarTests.cs:63-66)
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

        var tabs = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == p.Selected;
            Action select = () => p.OnSelect(index);
            Action<NodeHandle> onRealized = h => { if (index < handles.Count) handles[index] = h; };

            // Foreground states (SelectorBar_themeresources.xaml): rest/Selected = TextFillColorPrimary (:16, :18);
            // PointerOver = TextFillColorSecondary (:17) for BOTH legs (selected hover dims too, SelectorBar.xaml:
            // 118-119); Pressed = TextFillColorTertiary (:19) — but SelectedPressed stays on the PointerOver
            // brush/Secondary (SelectorBar.xaml:135-136). Backgrounds transparent in every state (:21-25): no
            // hover/press fill plate.
            ColorF pressedFg = isSelected ? Tok.TextSecondary : Tok.TextTertiary;

            var label = new TextEl(p.Items![index])
            {
                Size = 14f,                          // ControlContentThemeFontSize, FontWeight Normal (SelectorBar.xaml:58-59)
                Color = Tok.TextPrimary,
                HoverColor = Tok.TextSecondary,
                PressedColor = pressedFg,
            };

            string? glyph = p.Icons is { } ic && index < ic.Count ? ic[index] : null;
            Element[] rowKids = glyph is { Length: > 0 }
                ? [new TextEl(glyph)
                   {
                       FontFamily = Theme.IconFont,
                       Size = 16f * SelectorBar.IconScale,       // 16px IconElement × SelectorBarItemIconScale 0.8 (SelectorBar.xaml:186-188)
                       Color = Tok.TextPrimary,                  // PART_IconVisual recolors with the same states (SelectorBar.xaml:78-79, :92-93)
                       HoverColor = Tok.TextSecondary,
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
                Padding = new Edges4(12, 10, 12, 7), // SelectorBarItemPadding (themeresources:32)
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
                Children = isSelected ? [p.Parts.Apply(SelectorBar.PartPill, pill)] : [],
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
                OnFocusChanged = got => { if (got && p.Selected < 0) p.OnSelect(index); },
                OnRealized = onRealized,
                Children = [content, pillSlot],
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
            Gap = 0f,                            // horizontal StackLayout, no Spacing (SelectorBar.xaml:35-37)
            Padding = new Edges4(0, 4, 0, 4),    // SelectorBarPadding 0,4 (themeresources:26)
            AlignItems = FlexAlign.Center,       // SelectorBarItem VerticalAlignment=Center (SelectorBar.xaml:55)
            // No container Role: only the items expose tab-like peers (SelectorBarItemAutomationPeer.cpp).
            Children = tabs,
        };
    }
}
