using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI DropDownButton: clicking anywhere opens a <see cref="MenuFlyout"/> of choices below it. Captures its
/// own node as the flyout anchor; toggling re-click closes it.</summary>
public sealed class DropDownButton : Component
{
    public string Label = "";
    public string? Glyph;
    public IReadOnlyList<MenuFlyoutItem> Items = [];

    public static Element Create(string label, IReadOnlyList<MenuFlyoutItem> items, string? glyph = null)
        => Embed.Comp(() => new DropDownButton { Label = label, Items = items, Glyph = glyph });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, () => MenuFlyout.Build(Items, () => handle.Value?.Close()), FlyoutPlacement.BottomLeft);
        }

        var children = new List<Element>();
        if (Glyph is { Length: > 0 } g) children.Add(new TextEl(g) { Size = 14f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont });
        if (Label.Length > 0) children.Add(new TextEl(Label) { Size = 14f, Color = Tok.TextPrimary });
        children.Add(new TextEl(Icons.ChevronDown) { Size = 10f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont });

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 8f,
            MinHeight = 32f,
            Padding = new Edges4(11, 5, 11, 6),
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            PressedFill = Tok.FillControlTertiary,
            Role = AutomationRole.Button,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = children.ToArray(),
        };
    }
}
