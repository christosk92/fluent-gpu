using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A WinUI ToggleSplitButton: like <see cref="SplitButton"/>, but the primary half toggles on/off (accent when on)
/// instead of running a one-shot action. The dropdown half opens a <see cref="MenuFlyout"/>. The on/off state is a caller
/// signal so a page can display it.</summary>
public sealed class ToggleSplitButton : Component
{
    public string Label = "";
    public string? Glyph;
    public Signal<bool> IsOn = new(false);
    public bool IsEnabled = true;
    public Action<bool>? OnToggle;
    public IReadOnlyList<MenuFlyoutItem> Items = [];

    public static Element Create(string label, Signal<bool> isOn, IReadOnlyList<MenuFlyoutItem> items, Action<bool>? onToggle = null, string? glyph = null)
        => Embed.Comp(() => new ToggleSplitButton { Label = label, IsOn = isOn, Items = items, OnToggle = onToggle, Glyph = glyph });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        bool on = IsOn.Value;
        bool enabled = IsEnabled;

        void ToggleMenu()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, () => MenuFlyout.Build(Items, () => handle.Value?.Close()), FlyoutPlacement.BottomLeft);
        }
        void Flip() { IsOn.Value = !on; OnToggle?.Invoke(!on); }

        // Per-state colour matrix (disabled folds the whole control to the disabled tokens).
        var primFill  = !enabled ? Tok.FillControlDisabled : (on ? Tok.AccentDefault   : Tok.FillControlDefault);
        var primHover = !enabled ? Tok.FillControlDisabled : (on ? Tok.AccentSecondary : Tok.FillControlSecondary);
        var primPress = !enabled ? Tok.FillControlDisabled : (on ? Tok.AccentTertiary  : Tok.FillControlTertiary);
        var primFg    = !enabled ? Tok.TextDisabled : (on ? Tok.TextOnAccentPrimary : Tok.TextPrimary);
        // SplitButtonForegroundSecondary: TextSecondary unchecked; TextOnAccentSecondary in checked states.
        var secondaryFg = !enabled ? Tok.TextDisabled : (on ? Tok.TextOnAccentSecondary : Tok.TextSecondary);
        // DividerBackgroundGrid: StrokeControlDefault unchecked; SplitButtonBorderBrushCheckedDivider
        // (WinUI ControlStrokeColorOnAccentTertiary — no token; closest is StrokeControlOnAccentSecondary) when checked.
        var dividerFill = (!enabled || !on) ? Tok.StrokeControlDefault : Tok.StrokeControlOnAccentSecondary;

        var primaryContent = new List<Element>();
        if (Glyph is { Length: > 0 } g) primaryContent.Add(new TextEl(g) { Size = 14f, Color = primFg, FontFamily = Theme.IconFont });
        primaryContent.Add(new TextEl(Label) { Size = 14f, Color = primFg });

        var primary = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Height = 32f, MinWidth = 35f, Padding = new Edges4(11, 5, 11, 6),
            Fill = ColorF.Transparent, HoverFill = enabled ? primHover : primFill, PressedFill = enabled ? primPress : primFill,
            Role = AutomationRole.ToggleButton, OnClick = enabled ? Flip : null,
            Children = primaryContent.ToArray(),
        };
        var divider = new BoxEl { Width = 1f, Height = 16f, Fill = dividerFill, AlignSelf = FlexAlign.Center };
        var drop = new BoxEl
        {
            Width = 35f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,  // SplitButtonSecondaryButtonSize = 35
            Fill = ColorF.Transparent, HoverFill = enabled ? primHover : primFill, PressedFill = enabled ? primPress : primFill,
            Role = AutomationRole.Button, OnClick = enabled ? ToggleMenu : null,
            Children = [new TextEl(Icons.ChevronDown) { Size = 12f, Color = secondaryFg, FontFamily = Theme.IconFont }],
        };

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            MinHeight = 32f,
            Fill = primFill,
            BorderWidth = 1f, BorderBrush = (enabled && on) ? Tok.AccentControlElevationBorder : Tok.ControlElevationBorder, Corners = Radii.ControlAll,
            ClipToBounds = true,
            OnRealized = h => anchor.Value = h,
            Children = [primary, divider, drop],
        };
    }
}
