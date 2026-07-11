using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// The selector-VISUAL presets — the user-pickable item-chrome skins the premiere <see cref="ItemsView"/> exposes
/// (the WinUI "ListView's accent bar" the user likes, GridView's corner check, ItemContainer's accent ring, …) as a
/// single DATA enum so any layout × any selection mode can wear any selector (no WinUI-style capability cliffs):
/// <list type="bullet">
/// <item><see cref="AccentPill"/> — the WinUI <c>ListViewItem</c> SelectionIndicator (the 3×16 accent bar at the row's
/// left edge over a subtle backplate); the ListView preset's default.</item>
/// <item><see cref="Check"/> — the WinUI <c>GridViewItem</c> dual-border + top-right corner check; the GridView preset's default.</item>
/// <item><see cref="FullRow"/> — a documented SUPERSET (no exact WinUI analogue): the AccentPill row plate WITHOUT the
/// left pill, so the whole row fills with the accent-subtle selected highlight (full-bleed selected backplate).</item>
/// <item><see cref="Border"/> — the WinUI <c>ItemContainer</c> chrome (3px accent ring + 1px inner stroke + checkbox).</item>
/// <item><see cref="None"/> — no selection chrome at all (a bare recyclable container) for app-drawn selection.</item>
/// </list>
/// </summary>
public enum SelectorVisual : byte { AccentPill, Check, FullRow, Border, None }

/// <summary>Per-item VARIATION as VALUES (the newly-legal per-item-customization seam — supersedes the banned
/// per-item TemplateParts modifier in recycled scroll paths). Every field is a nullable override: null ⇒ keep the
/// preset default. Applied DURING chrome construction (a plain `with`-swap into the already-allocated BoxEl/TextEl
/// record), so per-item variation costs ZERO extra allocation and is provably shape-stable (the realizer
/// shape-hash guard + a steady-scroll HotPhaseAllocBytes==0 check enforce it; docs/guide/control-fidelity.md §6).
/// The producing Func must be pure-value: no new/box/LINQ/Animate per call.</summary>
public readonly record struct PartDelta(
    ColorF? Fill = null,
    ColorF? Foreground = null,
    float? Opacity = null,
    CornerRadius4? Corners = null,
    Edges4? Padding = null,
    ColorF? Border = null,
    string? Glyph = null)
{
    public static readonly PartDelta None = default;
}

/// <summary>
/// The <see cref="SelectorVisual"/> presets as <see cref="ItemContainerFactory"/>-shaped builders: each returns the
/// WinUI item chrome for one preset around the engine's ONE selection + keyboard substrate, signature-identical to the
/// <c>ItemContainerFactory</c> delegate (ItemsView.cs:52-54) so a preset is just <c>ContainerFactory = SelectorVisuals.&lt;Name&gt;</c>.
///
/// Each builder is PLAIN/recyclable — a keyed <see cref="BoxEl"/> tree with NO Component / no reactive bind
/// (TransformBind/OpacityBind/FillBind/WidthBind/HeightBind/OnRealized would break <c>TreeReconciler.IsRecyclable</c>,
/// Reconciler.cs:748-749); <c>Animate</c> (a <see cref="LayoutTransition"/>) is allowed because it is declarative.
/// Per-item chrome SKIN goes through the ContainerFactory/SelectorVisual seam; per-item VARIATION goes through the
/// <see cref="PartDelta"/> value seam (fill/fg/opacity/corner/padding/glyph as values, applied during construction —
/// shape-stable, 0-alloc, CI-enforced; docs/guide/control-fidelity.md §6). WinUI chrome values are cited inline; they are
/// re-verified against the live ListViewBaseItemChrome.cpp metrics (s_selectionIndicatorSize {3,16},
/// s_selectionIndicatorHeightShrinkage 6, s_selectionIndicatorMargin.left 4, s_backplateMargin {4,2},
/// s_multiSelectRoundedSquareInlineMargin 14, s_multiSelectRoundedContentOffset 28, s_multiSelectSquareOverlayMargin
/// {0,2,2,0}) and the per-state brush ramps in {ListView,GridView,ItemContainer}_themeresources.xaml.
/// </summary>
public static class SelectorVisuals
{
    // ── WinUI ListViewItem metrics (ListViewBaseItemChrome.cpp / ListViewItem_themeresources.xaml) ──
    private const float ListMinRowHeight = 40f;        // ListViewItemMinHeight (:14)
    private const float ListRowMarginX = 4f, ListRowMarginY = 2f;   // s_backplateMargin {4,2,4,2} (:79)
    private const float ListContentPadLeft = 16f, ListContentPadRight = 12f;   // Padding 16,0,12,0 (:241)
    private const float ListCheckboxContentOffset = 28f;   // s_multiSelectRoundedContentOffset (:91)
    private const float ListCheckboxLeftMargin = 14f;  // s_multiSelectRoundedSquareInlineMargin (:73)
    private const float ListCheckboxSize = 20f;        // s_multiSelectSquareSize (:61)
    private const float ListMultiSelectAnimMs = 333f;  // MultiSelect storyboards (:385-430), KeySpline 0.1,0.9,0.2,1
    private const float ListDisabledOpacity = 0.3f;    // ListViewItemDisabledThemeOpacity (:6)
    private const float ListIndicatorPressScale = 10f / 16f;   // 16 − s_selectionIndicatorHeightShrinkage(6) (:55)

    // ── WinUI GridViewItem metrics (GridViewItem_themeresources.xaml) ──
    private const float GridSelectedBorder = 2f;       // GridViewItemSelectedBorderThickness (:25)
    private const float GridDisabledOpacity = 0.3f;    // ListViewItemDisabledThemeOpacity (GridViewItem template :157)
    private const float GridCheckSize = 20f;           // s_multiSelectSquareSize (ListViewBaseItemChrome.cpp:61)
    private const float GridItemGap = 4f;              // GridViewItem Margin 0,0,4,4 (:144)

    /// <summary>WinUI <c>ItemContainer</c> chrome (3px accent ring + 1px inner stroke + multi-select checkbox) —
    /// delegates straight to <see cref="ItemContainer.Build"/> (the verified container; ItemContainer.xaml).</summary>
    internal static BoxEl Border(int index, Element content, in ItemChromeState state,
                                 Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged,
                                 in PartDelta delta = default)
        => ItemContainer.Build(
            content,
            isSelected: state.IsSelected,
            onInteraction: onInteraction,
            isEnabled: state.IsEnabled,
            showSelectionCheckbox: state.ShowCheckbox,
            isChecked: state.IsChecked,
            onFocusChanged: onFocusChanged,
            isTabStop: state.IsCurrent,
            partDelta: delta);

    /// <summary>No selection chrome: a bare recyclable container with the same press/key/focus wiring as
    /// <see cref="ItemContainer"/> (press-edge Tap/DoubleTap, Enter/Space distinct) but no plate/ring/checkbox — for
    /// apps that draw their own selection. Disabled dims to 0.3 (the shared ItemContainer disabled opacity).</summary>
    internal static BoxEl None(int index, Element content, in ItemChromeState state,
                               Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged,
                               in PartDelta delta = default)
    {
        bool enabled = state.IsEnabled;
        // No plate: only Opacity / Foreground / Padding are honored (Fill/Corners/Border/Glyph have no surface here).
        Element laneContent = content is TextEl ct && delta.Foreground is { } fg ? ct with { Color = fg } : content;
        return new BoxEl
        {
            ZStack = true,
            // Roving single tab stop (ItemContainer idiom): only the keyboard-current container sits in the tab order.
            TabStop = state.IsCurrent && enabled,
            Role = AutomationRole.Button,
            Opacity = delta.Opacity ?? (enabled ? 1f : ItemContainer.DisabledOpacity),
            IsEnabled = enabled,
            OnPointerReleased = args => onInteraction(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            Children = [new BoxEl { Padding = delta.Padding ?? default, Children = [laneContent] }],
        };
    }

    /// <summary>The WinUI <c>ListViewItem</c> SelectionIndicator chrome (the accent pill the user likes), pixel-exact —
    /// see the file-level doc for per-value cites. The pill (key "lv-pill") renders ONLY when
    /// <c>IsSelected &amp;&amp; IsEnabled &amp;&amp; !ShowCheckbox</c>: WinUI shows the checkbox OR the pill in
    /// Multiple mode, never both (the parity rule — a Multiple-mode row must not show both).</summary>
    internal static BoxEl AccentPill(int index, Element content, in ItemChromeState state,
                                     Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged,
                                     in PartDelta delta = default)
        => BuildListRow(content, in state, onInteraction, onFocusChanged, fullRow: false, in delta);

    /// <summary>A documented SUPERSET preset (WinUI has no exact analogue): identical to <see cref="AccentPill"/>'s row
    /// plate ramp BUT with NO left pill — the selected subtle fill reads as a full-bleed selected backplate over the
    /// whole row. Keeps the multi-select checkbox + interaction wiring.</summary>
    internal static BoxEl FullRow(int index, Element content, in ItemChromeState state,
                                  Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged,
                                  in PartDelta delta = default)
        => BuildListRow(content, in state, onInteraction, onFocusChanged, fullRow: true, in delta);

    /// <summary>The shared ListViewItemPresenter row body for <see cref="AccentPill"/> (fullRow=false) and
    /// <see cref="FullRow"/> (fullRow=true — drops the "lv-pill" child; the plate ramp is the full-row highlight).</summary>
    private static BoxEl BuildListRow(Element content, in ItemChromeState state,
                                      Action<ItemContainerTrigger, KeyModifiers> onInteraction,
                                      Action<bool> onFocusChanged, bool fullRow, in PartDelta delta = default)
    {
        bool selected = state.IsSelected, enabled = state.IsEnabled;
        // The pill is suppressed when the multi-select checkbox is shown (Multiple mode shows checkbox OR pill, not
        // both) and in the FullRow superset (the selected plate IS the cue).
        bool showPill = !fullRow && selected && enabled && !state.ShowCheckbox;
        float contentLeft = ListContentPadLeft + (state.ShowCheckbox ? ListCheckboxContentOffset : 0f);

        int n = 1 + (state.ShowCheckbox ? 1 : 0) + (showPill ? 1 : 0);
        var children = new Element[n];
        int w = 0;

        // Content lane: ListViewItem Padding 16,0,12,0 (+28 content offset in multi-select). The lane carries a
        // position FLIP so the ±28 shift slides over the MultiSelect storyboard timing (333ms FluentDecelerate).
        Element laneContent = content is TextEl ct && delta.Foreground is { } fg ? ct with { Color = fg } : content;
        children[w++] = new BoxEl
        {
            Key = "lv-content",
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Grow = 1f,
            Padding = delta.Padding ?? new Edges4(contentLeft, 0, ListContentPadRight, 0),
            Animate = new LayoutTransition(TransitionChannels.Position,
                TransitionDynamics.Tween(ListMultiSelectAnimMs, Easing.FluentDecelerate)),
            Children = [laneContent],
        };

        if (state.ShowCheckbox)
        {
            // Inline multi-select checkbox: 20×20 @ left 14, slide-in from −28 over 333ms (storyboard :385-430).
            children[w++] = new BoxEl
            {
                Key = "lv-check",
                Direction = 0,
                AlignItems = FlexAlign.Center,
                HitTestVisible = false,
                Padding = new Edges4(ListCheckboxLeftMargin, 0, 0, 0),
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(ListMultiSelectAnimMs, Easing.FluentDecelerate),
                    Enter: new EnterExit(Dx: -ListCheckboxContentOffset, Opacity: 0f, Active: true),
                    Exit: new EnterExit(Dx: -ListCheckboxContentOffset, Opacity: 0f, Active: true)),
                Children = [BuildListCheckPlate(state.IsChecked, enabled)],
            };
        }

        if (showPill)
        {
            // Selection indicator: 3×16 @ r1.5, left 4, vertically centred; spring-grows from centre on reveal
            // (NavPill preset 0.30/0.85); pressed shrinks toward 10/16 (chrome height shrinkage 6).
            children[w++] = new BoxEl
            {
                Key = "lv-pill",
                Width = 3f,
                Height = 16f,
                Margin = new Edges4(4f, 0f, 0f, 0f),       // s_selectionIndicatorMargin.left = 4 (:58)
                Corners = CornerRadius4.All(1.5f),         // ListViewItemSelectionIndicatorCornerRadius (:60)
                Fill = Tok.AccentDefault,                  // ListViewItemSelectionIndicatorBrush (:75-77, all states)
                AlignSelf = FlexAlign.Center,
                HitTestVisible = false,
                PressScale = ListIndicatorPressScale,      // s_selectionIndicatorHeightShrinkage analogue (uniform scale)
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    new TransitionDynamics(DynamicsKind.Spring, 0.30f, 0.85f),   // MotionSprings.NavPill values
                    Enter: new EnterExit(Sy: 0f, Opacity: 0f, Active: true)),
            };
        }

        return new BoxEl
        {
            ZStack = true,
            MinHeight = ListMinRowHeight,
            AlignItems = FlexAlign.Center,
            Margin = new Edges4(ListRowMarginX, ListRowMarginY, ListRowMarginX, ListRowMarginY),   // s_backplateMargin 4,2
            Corners = delta.Corners ?? Radii.ControlAll,                          // ListViewItemCornerRadius 4 (:58)
            // Backplate ramp (:17-22, :74): selected rest=Secondary / hover=Tertiary / pressed=Secondary;
            // unselected rest=Transparent / hover=Secondary / pressed=Tertiary. FullRow reads the selected fill as the
            // whole-row highlight (no pill drawn above it).
            Fill = delta.Fill ?? (selected ? Tok.FillSubtleSecondary : ColorF.Transparent),
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            // The row plate has no border by default; a PartDelta.Border opts one in (width 1).
            BorderColor = delta.Border ?? ColorF.Transparent,
            BorderWidth = delta.Border is null ? 0f : 1f,
            Opacity = delta.Opacity ?? (enabled ? 1f : ListDisabledOpacity), // ListViewItemDisabledThemeOpacity (:6)
            IsEnabled = enabled,
            Focusable = true,                              // UseSystemFocusVisuals (:247)
            FocusVisualMargin = Edges4.All(1f),            // FocusVisualMargin = 1 (:248)
            Role = AutomationRole.Button,
            OnPointerReleased = args => onInteraction(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            Children = children,
        };
    }

    /// <summary>The 20×20 ListView inline check plate: ControlAltFillColorSecondary plate + ControlStrongStroke 1px @ r3
    /// unchecked (:34/:70/:59); checked = Accent fill + TextOnAccentPrimary drawn checkmark (:66/:33);
    /// disabled checked = AccentFillColorDisabled + TextOnAccentDisabled (:69/:62).</summary>
    private static BoxEl BuildListCheckPlate(bool isChecked, bool enabled)
        => new()
        {
            Width = ListCheckboxSize,
            Height = ListCheckboxSize,
            Corners = CornerRadius4.All(3f),               // ListViewItemCheckBoxCornerRadius (:59)
            BorderWidth = 1f,                              // s_multiSelectRoundedSquareThickness (:67)
            Fill = isChecked
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : Tok.FillControlAltSecondary,
            BorderColor = isChecked
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : (enabled ? Tok.StrokeControlStrongDefault : Tok.StrokeControlStrongDisabled),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = isChecked
                ?
                [
                    new PolylineStrokeEl
                    {
                        Width = 14f,
                        Height = 14f,
                        P0 = new Point2(0.18f * 14f, 0.50f * 14f),
                        P1 = new Point2(0.42f * 14f, 0.72f * 14f),
                        P2 = new Point2(0.80f * 14f, 0.26f * 14f),
                        PointCount = 3,
                        Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled,
                        Thickness = 1.8f,
                        RoundCaps = true,
                    },
                ]
                : [],
        };

    /// <summary>The WinUI <c>GridViewItem</c> dual-border + corner-check chrome (GridViewItem_themeresources.xaml) —
    /// see the file-level doc for per-value cites.</summary>
    internal static BoxEl Check(int index, Element content, in ItemChromeState state,
                                Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged,
                                in PartDelta delta = default)
    {
        bool selected = state.IsSelected, enabled = state.IsEnabled;

        int n = 1 + (selected && enabled ? 1 : 0) + (state.ShowCheckbox ? 1 : 0);
        var children = new Element[n];
        int w = 0;

        Element laneContent = content is TextEl ct && delta.Foreground is { } fg ? ct with { Color = fg } : content;
        children[w++] = new BoxEl { Key = "gv-content", Padding = delta.Padding ?? default, Children = [laneContent] };

        if (selected && enabled)
        {
            // Inner 1px ControlSolid ring inset by the 2px accent border (SelectedInnerBorderBrush :31;
            // s_innerSelectionBorderThickness; inner corner radius floor 3 — s_innerBorderCornerRadius).
            children[w++] = new BoxEl
            {
                Key = "gv-inner",
                Margin = Edges4.All(GridSelectedBorder),
                BorderColor = Tok.FillControlSolid,
                BorderWidth = 1f,
                Corners = CornerRadius4.All(3f),
                HitTestVisible = false,
            };
        }

        if (state.ShowCheckbox)
        {
            // Corner check (Overlay mode): 20×20 top-right, margin 0,2,2,0 (s_multiSelectSquareOverlayMargin :76).
            children[w++] = new BoxEl
            {
                Key = "gv-check",
                Direction = 0,
                Justify = FlexJustify.End,
                AlignItems = FlexAlign.Start,
                HitTestVisible = false,
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(167f, Easing.FluentDecelerate),
                    Enter: new EnterExit(Opacity: 0f, Active: true),
                    Exit: new EnterExit(Opacity: 0f, Active: true)),
                Children = [BuildCornerCheck(state.IsChecked, enabled)],
            };
        }

        return new BoxEl
        {
            ZStack = true,
            Margin = new Edges4(0f, 0f, GridItemGap, GridItemGap),   // GridViewItem Margin 0,0,4,4 (:144)
            Corners = delta.Corners ?? Radii.ControlAll,      // GridViewItemCornerRadius 4 (:23)
            // Plate ramp (:5-10, :45): selected rest/hover = Tertiary, pressed = Secondary;
            // unselected rest = Transparent, hover = Secondary, pressed = Tertiary.
            Fill = delta.Fill ?? (selected ? Tok.FillSubtleTertiary : ColorF.Transparent),
            HoverFill = selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            // Border ramp: selected 2px accent (:25/:27) hover→Secondary (:28) press→Tertiary (:29);
            // unselected rest none, hover 1px ControlStrokeColorOnAccentTertiary (:26, chrome s_borderThickness 1).
            BorderWidth = selected ? GridSelectedBorder : 1f,
            BorderColor = delta.Border ?? (selected ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled) : ColorF.Transparent),
            HoverBorderColor = selected ? Tok.AccentSecondary : Tok.StrokeControlOnAccentTertiary,
            PressedBorderColor = selected ? Tok.AccentTertiary : Tok.StrokeControlOnAccentTertiary,
            Opacity = delta.Opacity ?? (enabled ? 1f : GridDisabledOpacity),
            IsEnabled = enabled,
            Focusable = true,
            FocusVisualMargin = Edges4.All(-3f),              // FocusVisualMargin −3 (:149)
            Role = AutomationRole.Button,
            OnPointerReleased = args => onInteraction(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            Children = children,
        };
    }

    /// <summary>The 20×20 overlay check square (GridViewItem corner check; per-value cites in the file doc).</summary>
    private static BoxEl BuildCornerCheck(bool isChecked, bool enabled)
        => new()
        {
            Width = GridCheckSize,
            Height = GridCheckSize,
            Margin = new Edges4(0f, 2f, 2f, 0f),   // s_multiSelectSquareOverlayMargin {0,2,2,0} (:76)
            Corners = CornerRadius4.All(3f),       // GridViewItemCheckBoxCornerRadius (:24)
            BorderWidth = 1f,
            Fill = isChecked
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : Tok.FillControlOnImage,          // GridViewItemCheckBoxBrush = ControlOnImageFillColorDefault (:19)
            BorderColor = isChecked
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : (enabled ? Tok.StrokeControlStrongDefault : Tok.StrokeControlStrongDisabled),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = isChecked
                ?
                [
                    new PolylineStrokeEl
                    {
                        Width = 14f,
                        Height = 14f,
                        P0 = new Point2(0.18f * 14f, 0.50f * 14f),
                        P1 = new Point2(0.42f * 14f, 0.72f * 14f),
                        P2 = new Point2(0.80f * 14f, 0.26f * 14f),
                        PointCount = 3,
                        Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled,   // (:18/:33)
                        Thickness = 1.8f,
                        RoundCaps = true,
                    },
                ]
                : [],
        };
}
