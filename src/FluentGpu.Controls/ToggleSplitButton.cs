using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI <c>ToggleSplitButton</c> (SplitButton + checked state). Like <see cref="SplitButton"/> it is one joined chrome
/// with two independently-clickable halves — a primary half on the left and a dropdown half on the right separated by a
/// 1px divider — but the primary half <em>toggles</em> on/off instead of running a one-shot action: clicking it (or
/// Enter/Space activation) flips <see cref="IsChecked"/> and raises <see cref="OnToggle"/>; the dropdown half opens a
/// <see cref="MenuFlyout"/>. WinUI derives ToggleSplitButton from SplitButton and they share one ControlTemplate; the
/// checked visual-states are driven by <c>InternalIsChecked()</c> (SplitButton.cpp UpdateVisualStates) — so this control
/// is the SplitButton chrome with the accent <c>Checked*</c> token ramps folded in.
/// <para>
/// When checked, both halves fill accent (SplitButtonBackgroundChecked = AccentFillColorDefault), the foreground is
/// TextOnAccent, the outer border is AccentControlElevationBorder, and the inner divider switches to the on-accent
/// divider stroke. Open/close goes through the overlay popup service (<see cref="Overlay.Service"/>): re-click toggles,
/// Escape / click-outside light-dismiss, focus captured on open + restored on close (host-wired), FocusTrap roves the
/// menu, and ExpandCollapse Collapsed↔Expanded is raised by the overlay lifecycle. Keyboard: Enter/Space toggle the
/// primary (the engine's focused-clickable activation), Down opens the flyout. <see cref="IsEnabled"/> gates all
/// interaction via the engine and folds the chrome to the disabled tokens. The checked state is a caller
/// <see cref="Signal{T}"/> so a page can read/display it; pass <see cref="OnToggle"/> to observe flips.
/// </para>
/// </summary>
public sealed class ToggleSplitButton : Component
{
    public string Label = "";
    public string? Glyph;
    public Element? PrimaryContent;
    public Signal<bool> IsChecked = new(false);
    public bool IsEnabled = true;
    public Action<bool>? OnToggle;
    public IReadOnlyList<MenuFlyoutItem> Items = [];
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real popup after first mount

    // SplitButton_themeresources.xaml: SplitButtonPrimaryButtonSize = SplitButtonSecondaryButtonSize = 35; the joined
    // chrome is one Border with ControlCornerRadius (= Radii.Control) + a 1px Separator column carrying the divider.
    const float PrimaryMinWidth = 35f;      // SplitButtonPrimaryButtonSize  (PrimaryButtonColumn MinWidth)
    const float SecondaryWidth = 35f;       // SplitButtonSecondaryButtonSize (SecondaryButtonColumn width)
    const float ControlHeight = 32f;        // effective WinUI SplitButton height
    const float DividerWidth = 1f;          // Separator column width / DividerBackgroundGrid Width
    const float ChevronBoxSize = 12f;       // AnimatedIcon Height/Width = 12
    const float ChevronGlyphSize = 8f;      // FontIconSource FallbackIconSource FontSize = 8
    static readonly Edges4 Padding = new(11, 6, 11, 7);   // SplitButtonPadding "11,6,11,7"

    public static Element Create(string label, Signal<bool> isChecked, IReadOnlyList<MenuFlyoutItem> items, Action<bool>? onToggle = null, string? glyph = null, bool isEnabled = true)
        => Embed.Comp(() => new ToggleSplitButton { Label = label, IsChecked = isChecked, Items = items, OnToggle = onToggle, Glyph = glyph, IsEnabled = isEnabled });

    public static Element Create(Element primaryContent, Signal<bool> isChecked, IReadOnlyList<MenuFlyoutItem> items, Action<bool>? onToggle = null, bool isEnabled = true)
        => Embed.Comp(() => new ToggleSplitButton { PrimaryContent = primaryContent, IsChecked = isChecked, Items = items, OnToggle = onToggle, IsEnabled = isEnabled });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var open = UseSignal(false);
        var autoOpened = UseRef(false);
        var svc = UseContext(Overlay.Service);
        bool on = IsChecked.Value;
        bool enabled = IsEnabled;
        bool menuOpen = open.Value;

        void ToggleMenu()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            // FocusTrap (Tab roves the menu) + light dismiss (Escape / click-outside); overlay captures + restores focus.
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(Items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomLeft,                 // FlyoutShowOptions Placement = BottomEdgeAlignedLeft (SplitButton.cpp OpenFlyout)
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
            handle.Value.ClosedAction = () => { handle.Value = null; open.Value = false; };
            open.Value = true;
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            ToggleMenu();
        }, OpenOnMount);
        // ToggleSplitButton::OnClickPrimary → Toggle() → IsChecked(!IsChecked()), then base raises the Click event.
        void Flip() { bool next = !on; IsChecked.Value = next; OnToggle?.Invoke(next); }

        // ── Per-state colour matrix (SplitButton.xaml CommonStates; checked rows are ToggleSplitButton's). The engine eases
        //    pointer (hover/press) states from these resting tokens; the checked/disabled axis re-renders via the signal.
        //
        // Background (PrimaryBackgroundGrid / SecondaryBackgroundGrid share the same per-half ramp):
        //   unchecked: SplitButtonBackground(Default) / ...PointerOver(Secondary) / ...Pressed(Tertiary) / ...Disabled
        //   checked:   SplitButtonBackgroundChecked(Accent) / ...CheckedPointerOver(AccentSecondary) / ...CheckedPressed(AccentTertiary) / ...CheckedDisabled(AccentDisabled)
        ColorF restFill = !enabled ? (on ? Tok.AccentDisabled : Tok.FillControlDisabled)
                                   : (on
                                       ? (menuOpen ? Tok.AccentTertiary : Tok.AccentDefault)
                                       : (menuOpen ? Tok.FillControlTertiary : Tok.FillControlDefault));
        ColorF hoverFill = on ? Tok.AccentSecondary : Tok.FillControlSecondary;
        ColorF pressFill = on ? Tok.AccentTertiary  : Tok.FillControlTertiary;

        // Primary foreground: SplitButtonForeground ramp.
        //   unchecked: Foreground(TextPrimary) / PointerOver(TextPrimary) / Pressed(TextSecondary) / Disabled(TextDisabled)
        //   checked:   ForegroundChecked(TextOnAccentPrimary) / ...CheckedPointerOver(TextOnAccentPrimary) / ...CheckedPressed(TextOnAccentSecondary) / ...CheckedDisabled(TextDisabled)
        ColorF primRestFg  = !enabled ? Tok.TextDisabled
                                      : (menuOpen
                                          ? (on ? Tok.TextOnAccentSecondary : Tok.TextSecondary)
                                          : (on ? Tok.TextOnAccentPrimary : Tok.TextPrimary));
        ColorF primHoverFg = on ? Tok.TextOnAccentPrimary   : Tok.TextPrimary;
        ColorF primPressFg = on ? Tok.TextOnAccentSecondary : Tok.TextSecondary;

        // Secondary (chevron) foreground:
        //   unchecked: SplitButtonForegroundSecondary(TextSecondary) / PointerOver→Foreground...PointerOver(TextPrimary) / Pressed→...SecondaryPressed(TextTertiary)
        //   checked:   ForegroundChecked(TextOnAccentPrimary) / ...CheckedPointerOver(TextOnAccentPrimary) / ...CheckedPressed(TextOnAccentSecondary)
        ColorF secRestFg  = !enabled ? Tok.TextDisabled
                                     : (menuOpen
                                         ? (on ? Tok.TextOnAccentSecondary : Tok.TextTertiary)
                                         : (on ? Tok.TextOnAccentPrimary : Tok.TextSecondary));
        ColorF secHoverFg = on ? Tok.TextOnAccentPrimary   : Tok.TextPrimary;
        ColorF secPressFg = on ? Tok.TextOnAccentSecondary : Tok.TextTertiary;

        // Outer border (Border / PrimaryButtonBorder / SecondaryButtonBorder share BorderBrush):
        //   unchecked: SplitButtonBorderBrush(ControlElevationBorder) ; Pressed/Disabled → StrokeControlDefault (solid)
        //   checked:   SplitButtonBorderBrushChecked(AccentControlElevationBorder) ; CheckedPressed/CheckedDisabled → ControlFillColorTransparent (null/none)
        GradientSpec restBorder = enabled
            ? (menuOpen
                ? (on ? GradientSpec.Solid(ColorF.Transparent) : GradientSpec.Solid(Tok.StrokeControlDefault))
                : (on ? Tok.AccentControlElevationBorder : Tok.ControlElevationBorder))
            : (on ? GradientSpec.Solid(ColorF.Transparent) : GradientSpec.Solid(Tok.StrokeControlDefault));
        // Pressed border: unchecked → StrokeControlDefault (solid); checked → ControlFillColorTransparent.
        GradientSpec pressBorder = on ? GradientSpec.Solid(ColorF.Transparent) : GradientSpec.Solid(Tok.StrokeControlDefault);

        // DividerBackgroundGrid: SplitButtonBorderBrushDivider (StrokeControlDefault) when unchecked;
        // SplitButtonBorderBrushCheckedDivider = ControlStrokeColorOnAccentTertiary (#37000000) in every checked state.
        ColorF dividerFill = (enabled && on) ? Tok.StrokeControlOnAccentTertiary : Tok.StrokeControlDefault;

        // ── Primary half (Grid.Column 0). Toggles; Role = ToggleButton so a11y exposes the on/off Toggle pattern.
        Element[] primaryChildren;
        if (PrimaryContent is { } custom)
        {
            primaryChildren = [custom];
        }
        else
        {
            var list = new List<Element>(2);
            if (Glyph is { Length: > 0 } g)
                list.Add(new TextEl(g)
                {
                    Size = 14f, Color = primRestFg, FontFamily = Theme.IconFont,
                    HoverColor = primHoverFg, PressedColor = primPressFg, DisabledColor = Tok.TextDisabled,
                });
            list.Add(new TextEl(Label)
            {
                Size = 14f, Color = primRestFg,
                HoverColor = primHoverFg, PressedColor = primPressFg, DisabledColor = Tok.TextDisabled,
            });
            primaryChildren = list.ToArray();
        }

        var primary = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Height = ControlHeight, MinWidth = PrimaryMinWidth, Padding = Padding,
            Fill = ColorF.Transparent, HoverFill = hoverFill, PressedFill = pressFill,
            Role = AutomationRole.ToggleButton,
            IsEnabled = enabled,
            OnClick = Flip,                         // Enter/Space activation routes here via the engine
            Children = primaryChildren,
        };

        // Full-height 1px divider (DividerBackgroundGrid stretches column 1 — not a centred pill).
        var divider = new BoxEl { Width = DividerWidth, AlignSelf = FlexAlign.Stretch, Fill = dividerFill };

        // ── Dropdown half (Grid.Column 2, width 35). Opens the MenuFlyout; chevron is a 12×12 box, glyph @ 8.
        var drop = new BoxEl
        {
            Width = SecondaryWidth, Height = ControlHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = ColorF.Transparent, HoverFill = hoverFill, PressedFill = pressFill,
            Role = AutomationRole.Button,
            IsEnabled = enabled,
            OnClick = ToggleMenu,
            Children =
            [
                new BoxEl
                {
                    Width = ChevronBoxSize, Height = ChevronBoxSize, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(Icons.ChevronDownSmall)
                        {
                            Size = ChevronGlyphSize, Color = secRestFg, FontFamily = Theme.IconFont,
                            HoverColor = secHoverFg, PressedColor = secPressFg, DisabledColor = Tok.TextDisabled,
                        },
                    ],
                },
            ],
        };

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            MinHeight = ControlHeight,
            Fill = restFill,
            BorderWidth = 1f, BorderBrush = restBorder, PressedBorderBrush = pressBorder, Corners = Radii.ControlAll,
            ClipToBounds = true,
            IsEnabled = enabled,                    // engine gate: no hit-test/focus/keyboard/click while disabled
            Focusable = enabled,                    // Tab reaches it → Enter/Space toggle, Down opens
            Role = AutomationRole.ToggleButton,
            OnRealized = h => anchor.Value = h,
            // Down opens the flyout (SplitButton.cpp OnSplitButtonKeyUp); Alt+Down / F4 need a modifier+key the slice
            // KeyEventArgs doesn't carry yet (see deferral note). Enter/Space toggle via the primary's OnClick.
            OnKeyDown = a => { if (a.KeyCode == Keys.Down && handle.Value is not { IsOpen: true }) { ToggleMenu(); a.Handled = true; } },
            Children = [primary, divider, drop],
        };
    }
}
