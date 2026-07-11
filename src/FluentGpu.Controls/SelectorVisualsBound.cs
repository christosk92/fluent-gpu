using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// The per-row scope handed to a BOUND <see cref="ItemsView"/> row template (<see cref="ItemsView.CreateBound"/>).
/// A bound row is built ONCE per slot and never rebuilt; everything that varies by index is read REACTIVELY through
/// this scope so recycling/selection/now-playing re-skin in place (a signal write), never a remount.
///
/// <list type="bullet">
/// <item><see cref="Index"/> — the slot's persistent index SIGNAL. Read <c>Index.Value</c> inside a bind thunk to map
///   to your item (<c>data[Index.Value]</c>); a recycle writes it, re-running only this row's binds.</item>
/// <item><see cref="IsSelected"/>/<see cref="IsCurrent"/>/<see cref="IsEnabled"/> — derived reactive PREDICATES (thunks
///   that read the SelectionModel / current-item / enabled state plus <see cref="Index"/>). Call them INSIDE a bind
///   (<c>Opacity = Prop.Of(() =&gt; scope.IsSelected() ? 1f : 0f)</c>) so the channel subscribes and re-skins on change —
///   with NO list re-render and NO row rebuild.</item>
/// <item><see cref="OnInteraction"/>/<see cref="OnFocusChanged"/> — wire these onto the slot root (press/Enter/Space →
///   the selector; focus → keyboard-current tracking), exactly like the <see cref="ItemContainerFactory"/> seam.</item>
/// </list>
/// The slot root should be <c>Focusable = false</c>: the ItemsView owns the single roving tab stop and toggles the
/// current slot's focusability imperatively (no re-render). <see cref="SelectorVisualsBound"/> builds standard chrome
/// around your content; a custom skin can read the scope directly.
/// </summary>
public readonly record struct RowScope(
    IReadSignal<int> Index,
    Func<bool> IsSelected,
    Func<bool> IsCurrent,
    Func<bool> IsEnabled,
    Action<ItemContainerTrigger, KeyModifiers> OnInteraction,
    Action<bool> OnFocusChanged);

/// <summary>
/// The <see cref="SelectorVisual"/> presets as BOUND, shape-stable row chrome for <see cref="ItemsView.CreateBound"/> —
/// the signals-first analogue of <see cref="SelectorVisuals"/>. Where <see cref="SelectorVisuals"/> bakes
/// selected/current as VALUES and flips the element SHAPE (adding/removing the pill child) — correct on the recyclable
/// RenderItem path — these build the cue ONCE (shape-stable: the pill is always present) and reveal it with a BOUND
/// <c>Opacity</c>/<c>Fill</c> that reads the <see cref="RowScope"/> predicates. So selection re-skins as a
/// compositor-only repaint of the affected rows, with no list re-render, no remount, and no Enter-transition replay.
/// </summary>
public static class SelectorVisualsBound
{
    // WinUI ListViewItem metrics (mirror SelectorVisuals.cs; cited there against ListViewBaseItemChrome.cpp).
    private const float ListMinRowHeight = 40f;          // ListViewItemMinHeight
    private const float ListRowMarginX = 4f, ListRowMarginY = 2f;   // s_backplateMargin {4,2,4,2}
    private const float ListContentPadLeft = 16f, ListContentPadRight = 12f;   // Padding 16,0,12,0
    private const float ListIndicatorPressScale = 10f / 16f;   // 16 − s_selectionIndicatorHeightShrinkage(6)
    private const float CheckboxContentOffset = 28f;           // ListViewBaseItemChrome s_multiSelectRoundedContentOffset
    private const float CheckboxSize = 20f;
    private const float MultiSelectAnimMs = 333f;              // MultiSelect storyboards, KeySpline 0.1,0.9,0.2,1

    /// <summary>The WinUI <c>ListViewItem</c> accent-pill chrome, bound: a 3×16 accent bar (key "lv-pill", always
    /// present) revealed by a bound opacity on <see cref="RowScope.IsSelected"/>, over a bound selected backplate.</summary>
    public static BoxEl AccentPill(in RowScope s, Element content, Func<bool>? showCheckbox = null)
        => BuildListRow(in s, content, fullRow: false, showCheckbox);

    /// <summary>Animated multi-select checkbox lane (WinUI ListView inline check): slides in from −28px over 333ms
    /// FluentDecelerate when <paramref name="visible"/> is true. The check itself is the REAL <see cref="CheckBox"/>
    /// control (WinUI draw-on checkmark), hosted in a per-slot component so its checked VALUE re-renders reactively —
    /// a bound row never rebuilds, so the value can't be baked into the template. Click synthesizes Ctrl+Tap.</summary>
    public static Element BoundCheckLane(Func<bool> visible, Func<bool> isChecked,
                                         Action<ItemContainerTrigger, KeyModifiers> interact,
                                         float leftMargin = 14f)
    {
        var vis = visible;
        var chk = isChecked;
        var onInteract = interact;
        Element lane = new BoxEl
        {
            Key = "lv-check",
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Width = leftMargin + CheckboxSize + 4f,
            Padding = new Edges4(leftMargin, 0f, 4f, 0f),
            Animate = new LayoutTransition(
                TransitionChannels.Opacity,
                TransitionDynamics.Tween(MultiSelectAnimMs, Easing.FluentDecelerate),
                Enter: new EnterExit(Dx: -CheckboxContentOffset, Opacity: 0f, Active: true),
                Exit: new EnterExit(Dx: -CheckboxContentOffset, Opacity: 0f, Active: true)),
            Children = [Embed.Comp(() => new BoundRowCheckBox(chk, onInteract))],
        };
        return Flow.Show(vis, lane);
    }

    /// <summary>The full-bleed selected-backplate superset (no left pill — the bound plate fill IS the cue).</summary>
    public static BoxEl FullRow(in RowScope s, Element content) => BuildListRow(in s, content, fullRow: true);

    /// <summary>WinUI multi-select tap semantics: while the check lane is visible, a PLAIN tap/space toggles the row
    /// into the selection (synthesized Ctrl) instead of replacing it — Ctrl/Shift taps keep their Extended meaning.
    /// Apply to Tap/SpaceKey only (DoubleTap/Enter still invoke).</summary>
    public static KeyModifiers MultiSelectMods(bool checksVisible, KeyModifiers mods)
        => checksVisible && (mods & (KeyModifiers.Ctrl | KeyModifiers.Shift)) == 0 ? mods | KeyModifiers.Ctrl : mods;

    private static BoxEl BuildListRow(in RowScope s, Element content, bool fullRow, Func<bool>? showCheckbox = null)
    {
        // Capture the predicates/handlers so the bind thunks below don't capture the `in` scope by ref.
        Func<bool> isSel = s.IsSelected, isEn = s.IsEnabled;
        var interact = s.OnInteraction;
        Action<bool> focusChanged = s.OnFocusChanged;
        var showChecks = showCheckbox;

        Element[] contentChildren = showChecks is null
            ? [content]
            : [BoundCheckLane(showChecks, isSel, interact, leftMargin: 4f), content];
        Element contentLane = new BoxEl
        {
            Key = "lv-content", Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f,
            Padding = new Edges4(ListContentPadLeft, 0f, ListContentPadRight, 0f),
            Animate = showChecks is null ? null : new LayoutTransition(
                TransitionChannels.Position,
                TransitionDynamics.Tween(MultiSelectAnimMs, Easing.FluentDecelerate)),
            Children = contentChildren,
        };

        Element[] children = fullRow
            ? [contentLane]
            : [contentLane, new BoxEl
            {
                // Selection indicator (3×16 @ r1.5, left 4, vertically centred). ALWAYS present (shape-stable) —
                // revealed by a BOUND opacity instead of a mount-Enter spring, because the slot never remounts.
                Key = "lv-pill", Width = 3f, Height = 16f, Margin = new Edges4(ListRowMarginX, 0f, 0f, 0f),
                Corners = CornerRadius4.All(1.5f), Fill = Tok.AccentDefault, AlignSelf = FlexAlign.Center,
                HitTestVisible = false, PressScale = ListIndicatorPressScale,
                Opacity = Prop.Of(() => isSel() && !(showChecks?.Invoke() ?? false) ? 1f : 0f),
            }];

        return new BoxEl
        {
            ZStack = true,
            MinHeight = ListMinRowHeight,
            AlignItems = FlexAlign.Center,
            Margin = new Edges4(ListRowMarginX, ListRowMarginY, ListRowMarginX, ListRowMarginY),
            Corners = Radii.ControlAll,
            // Rest backplate BOUND to selection (the WinUI selected fill). Hover/Pressed stay static — they are
            // resolved by the recorder's state-brush path, not the bind path, so they cannot carry a signal; the
            // (subtle) selected-vs-unselected hover-ramp difference is dropped, the pill is the dominant cue.
            Fill = Prop.Of(() => isSel() ? Tok.FillSubtleSecondary : ColorF.Transparent),
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            Opacity = Prop.Of(() => isEn() ? 1f : ItemContainer.DisabledOpacity),
            Focusable = false,                              // the ItemsView roving effect owns the single tab stop
            FocusVisualMargin = Edges4.All(1f),
            Role = AutomationRole.Button,
            OnPointerReleased = args =>
            {
                if (args.ClickCount >= 2) interact(ItemContainerTrigger.DoubleTap, args.Mods);
                else interact(ItemContainerTrigger.Tap, MultiSelectMods(showChecks?.Invoke() ?? false, args.Mods));
            },
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, MultiSelectMods(showChecks?.Invoke() ?? false, args.Mods)); args.Handled = true; }
            },
            OnFocusChanged = focusChanged,
            Children = children,
        };
    }

    /// <summary>No selection chrome: a bare bound container with the press/key/focus wiring (for app-drawn selection).
    /// Disabled dims to the shared <see cref="ItemContainer.DisabledOpacity"/>.</summary>
    public static BoxEl None(in RowScope s, Element content)
    {
        Func<bool> isEn = s.IsEnabled;
        var interact = s.OnInteraction;
        Action<bool> focusChanged = s.OnFocusChanged;
        return new BoxEl
        {
            ZStack = true,
            Focusable = false,
            Role = AutomationRole.Button,
            Opacity = Prop.Of(() => isEn() ? 1f : ItemContainer.DisabledOpacity),
            OnPointerReleased = args => interact(
                args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = focusChanged,
            Children = [new BoxEl { Children = [content] }],
        };
    }
}

/// <summary>The inline multi-select check cell: hosts the REAL <see cref="CheckBox"/> control (the WinUI draw-on
/// checkmark timeline) in a per-slot component so its checked VALUE re-renders reactively off the
/// <see cref="RowScope.IsSelected"/> predicate — a bound row template is built once and never rebuilt, so a checked
/// state baked as a value would freeze. Clicking synthesizes Ctrl+Tap (toggle-into-selection). Not focusable: the
/// ItemsView owns the roving tab stop.</summary>
internal sealed class BoundRowCheckBox : Component
{
    static readonly TemplateParts NotFocusable = new() { [CheckBox.PartRoot] = b => b with { Focusable = false } };

    readonly Func<bool> _isChecked;
    readonly Action<ItemContainerTrigger, KeyModifiers> _interact;

    public BoundRowCheckBox(Func<bool> isChecked, Action<ItemContainerTrigger, KeyModifiers> interact)
    { _isChecked = isChecked; _interact = interact; }

    public override Element Render()
    {
        bool on = _isChecked();   // reads SelectionModel.Version + the slot Index signal → re-renders on both
        var style = CheckBox.DefaultStyle with
        {
            MinWidth = 0f, MinHeight = 20f, ContentGap = 0f,   // compact: a bare 20px box, no label lane
            FocusVisualMargin = Edges4.All(1f),                // the labeled control's −7,−3 halo would spill the row
        };
        return CheckBox.Create("", on, () => _interact(ItemContainerTrigger.Tap, KeyModifiers.Ctrl), style, parts: NotFocusable);
    }
}
