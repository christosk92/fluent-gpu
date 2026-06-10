using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI <c>DropDownButton</c>: a standard <see cref="Button"/> with a trailing chevron that opens a
/// <see cref="MenuFlyout"/> of choices below it on click. WinUI's DropDownButton derives from Button, so it IS the Button
/// template (RootGrid background/border/corner + an 83ms BrushTransition) plus a 2-column grid — content (<c>*</c>) and a
/// 12×12 chevron AnimatedIcon (<c>Auto</c>, <c>Margin 8,0,0,0</c>). The chevron foreground is
/// <c>DropDownButtonForegroundSecondary</c> (TextFillColorSecondaryBrush; Tertiary on hover/press, the disabled foreground
/// when disabled); its FontIcon fallback is glyph <c>&#xE96E;</c> at FontSize 8.
/// <para>
/// Open/close goes through the overlay popup service (<see cref="Overlay.Service"/>): re-click toggles, Escape / click-outside
/// light-dismiss, and focus is captured on open + restored on close (host-wired). Keyboard: Enter/Space (the engine's focused-
/// clickable activation) and Down open the flyout. The overlay raises the WinUI ExpandCollapse semantics (Collapsed↔Expanded);
/// <see cref="IsEnabled"/> gates all interaction via the engine and swaps the resting fill/border/foreground to the disabled
/// tokens. The chevron itself does NOT rotate on open — matching WinUI's AnimatedChevronDownSmall, whose only states are
/// Normal/PointerOver/Pressed (no Expanded state). See the deferral note in the control's design comments.
/// </para>
/// </summary>
public sealed class DropDownButton : Component
{
    public string Label = "";
    public string? Glyph;
    public IReadOnlyList<MenuFlyoutItem> Items = [];
    public bool IsEnabled = true;
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real popup after first mount

    // WinUI DropDownButton chevron AnimatedIcon fallback: FontIcon Glyph "" at FontSize 8 in a 12×12 box, Margin 8,0,0,0.
    const string ChevronGlyph = Icons.ChevronDownSmall;
    const float ChevronGlyphSize = 8f;     // FontIconSource FontSize="8"
    const float ChevronBoxSize = 12f;      // AnimatedIcon Width/Height = 12
    const float ChevronMargin = 8f;        // AnimatedIcon Margin="8,0,0,0"

    public static Element Create(string label, IReadOnlyList<MenuFlyoutItem> items, string? glyph = null, bool isEnabled = true)
        => Embed.Comp(() => new DropDownButton { Label = label, Items = items, Glyph = glyph, IsEnabled = isEnabled });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var autoOpened = UseRef(false);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            // Light dismiss (Escape / click-outside) + FocusTrap (Tab/Shift-Tab roves the menu); the overlay captures focus on
            // open and restores it on close (host-wired). ExpandCollapse Collapsed↔Expanded is raised by the overlay lifecycle.
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(Items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomLeft,
                // WinUI menus are WINDOWED popups (FlyoutBase_Partial.cpp:3181-3205 SetIsWindowedPopup) — a tall
                // menu may escape the window when the platform supports popup windows (constrained fallback otherwise).
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            Toggle();
        }, OpenOnMount);

        // WinUI Button state matrix (DropDownButton IS a Button — ButtonBackground/BorderBrush/Foreground per state). Disabled
        // is a logical state: resting fill/border/foreground swap to the disabled tokens; the engine gate stops hover/press/click.
        ColorF restFg = IsEnabled ? Tok.TextPrimary : Tok.TextDisabled;
        ColorF chevronFg = IsEnabled ? Tok.TextSecondary : Tok.TextDisabled;   // DropDownButtonForegroundSecondary = TextFillColorSecondaryBrush

        var children = new List<Element>();
        if (Glyph is { Length: > 0 } g)
            children.Add(new TextEl(g)
            {
                Size = 14f, Color = restFg, FontFamily = Theme.IconFont,
                HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary, DisabledColor = Tok.TextDisabled,  // ButtonForeground ramp
            });
        if (Label.Length > 0)
            children.Add(new TextEl(Label)
            {
                Size = 14f, Color = restFg, Grow = 1f,                          // ContentPresenter column = "*"
                HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary, DisabledColor = Tok.TextDisabled,  // ButtonForeground / ...PointerOver / ...Pressed / ...Disabled
            });

        // Chevron column ("Auto"): WinUI AnimatedIcon (12×12) over the FontIcon fallback (E96E @ 8), Margin 8,0,0,0. Foreground
        // ramp = DropDownButtonForegroundSecondary (TextSecondary) → ...PointerOver/Pressed (TextTertiary) → Disabled (TextDisabled).
        children.Add(new BoxEl
        {
            Width = ChevronBoxSize,
            Height = ChevronBoxSize,
            Margin = new Edges4(ChevronMargin, 0, 0, 0),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(ChevronGlyph)
                {
                    Size = ChevronGlyphSize, Color = chevronFg, FontFamily = Theme.IconFont,
                    HoverColor = Tok.TextTertiary, PressedColor = Tok.TextTertiary, DisabledColor = Tok.TextDisabled,
                },
            ],
        });

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 32f,                                  // effective WinUI button height
            Padding = new Edges4(11, 5, 11, 6),               // ButtonPadding
            Corners = Radii.ControlAll,                       // ControlCornerRadius = 4
            BorderWidth = 1f,                                 // ButtonBorderThemeThickness = 1
            // ButtonBackground / ...PointerOver / ...Pressed / ...Disabled.
            Fill = IsEnabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
            HoverFill = Tok.FillControlSecondary,
            PressedFill = Tok.FillControlTertiary,
            // RootGrid BackgroundTransition = BrushTransition Duration 0:0:0.083 (DropDownButton.xaml:23) — the same
            // explicit 83ms/FastOutSlowIn pair SplitButton/ToggleSplitButton carry (audit minor: was engine-default).
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,   // ControlFastOutSlowInKeySpline = 0,0,0,1
            // ButtonBorderBrush (elevation) rest/hover; ButtonBorderBrushPressed/Disabled = StrokeControlDefault (solid).
            BorderBrush = IsEnabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
            HoverBorderBrush = Tok.ControlElevationBorder,
            PressedBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault),
            ClipToBounds = true,
            IsEnabled = IsEnabled,                            // engine gate: no hit-test/focus/keyboard/click while disabled
            Focusable = IsEnabled,                            // Tab reaches it → Enter/Space/Down open
            Role = AutomationRole.Button,                     // WinUI ControlType = Button (+ ExpandCollapse pattern)
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,                                 // Enter/Space activation routes here via the engine
            // Down / Alt+Down / F4 open the flyout (WinUI DropDownButton + ComboBox-family open chords).
            OnKeyDown = a => { if ((a.KeyCode == Keys.Down || a.KeyCode == Keys.F4) && handle.Value is not { IsOpen: true }) { Toggle(); a.Handled = true; } },
            Children = children.ToArray(),
        };
    }
}
