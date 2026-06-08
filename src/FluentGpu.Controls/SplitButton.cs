using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI SplitButton: a primary action button joined to a dropdown arrow. The two halves are independently
/// clickable — the left runs <see cref="OnInvoke"/>, the right opens a <see cref="MenuFlyout"/> anchored to the whole control.</summary>
public sealed class SplitButton : Component
{
    public string Label = "";
    public string? Glyph;
    public Action? OnInvoke;
    public IReadOnlyList<MenuFlyoutItem> Items = [];

    public static Element Create(string label, Action onInvoke, IReadOnlyList<MenuFlyoutItem> items, string? glyph = null)
        => Embed.Comp(() => new SplitButton { Label = label, OnInvoke = onInvoke, Items = items, Glyph = glyph });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void ToggleMenu()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, () => MenuFlyout.Build(Items, () => handle.Value?.Close()), FlyoutPlacement.BottomLeft);
        }

        var primaryContent = new List<Element>();
        if (Glyph is { Length: > 0 } g) primaryContent.Add(new TextEl(g) { Size = 14f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont });
        primaryContent.Add(new TextEl(Label) { Size = 14f, Color = Tok.TextPrimary });

        var primary = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, MinHeight = 32f, Padding = new Edges4(11, 5, 11, 6),
            Corners = new CornerRadius4(Radii.Control, 0f, 0f, Radii.Control),
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            Role = AutomationRole.Button, OnClick = OnInvoke,
            Children = primaryContent.ToArray(),
        };
        var divider = new BoxEl { Width = 1f, Height = 16f, Fill = Tok.StrokeControlDefault, AlignSelf = FlexAlign.Center };
        var drop = new BoxEl
        {
            Width = 32f, MinHeight = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = new CornerRadius4(0f, Radii.Control, Radii.Control, 0f),
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            Role = AutomationRole.Button, OnClick = ToggleMenu,
            Children = [new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont }],
        };

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder, Corners = Radii.ControlAll,
            OnRealized = h => anchor.Value = h,
            Children = [primary, divider, drop],
        };
    }
}
