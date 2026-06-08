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

        void ToggleMenu()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, () => MenuFlyout.Build(Items, () => handle.Value?.Close()), FlyoutPlacement.BottomLeft);
        }
        void Flip() { IsOn.Value = !on; OnToggle?.Invoke(!on); }

        var primFill = on ? Tok.AccentDefault : Tok.FillControlDefault;
        var primHover = on ? Tok.AccentSecondary : Tok.FillControlSecondary;
        var primPress = on ? Tok.AccentTertiary : Tok.FillControlTertiary;
        var fg = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary;

        var primaryContent = new List<Element>();
        if (Glyph is { Length: > 0 } g) primaryContent.Add(new TextEl(g) { Size = 14f, Color = fg, FontFamily = Theme.IconFont });
        primaryContent.Add(new TextEl(Label) { Size = 14f, Color = fg });

        var primary = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, MinHeight = 32f, Padding = new Edges4(11, 5, 11, 6),
            Corners = new CornerRadius4(Radii.Control, 0f, 0f, Radii.Control),
            Fill = primFill, HoverFill = primHover, PressedFill = primPress,
            Role = AutomationRole.ToggleButton, OnClick = Flip,
            Children = primaryContent.ToArray(),
        };
        var divider = new BoxEl { Width = 1f, Height = 16f, Fill = on ? Tok.StrokeControlOnAccentSecondary : Tok.StrokeControlDefault, AlignSelf = FlexAlign.Center };
        var drop = new BoxEl
        {
            Width = 32f, MinHeight = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = new CornerRadius4(0f, Radii.Control, Radii.Control, 0f),
            Fill = primFill, HoverFill = primHover, PressedFill = primPress,
            Role = AutomationRole.Button, OnClick = ToggleMenu,
            Children = [new TextEl(Icons.ChevronDown) { Size = 10f, Color = fg, FontFamily = Theme.IconFont }],
        };

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            BorderWidth = 1f, BorderBrush = on ? Tok.AccentControlElevationBorder : Tok.ControlElevationBorder, Corners = Radii.ControlAll,
            OnRealized = h => anchor.Value = h,
            Children = [primary, divider, drop],
        };
    }
}
