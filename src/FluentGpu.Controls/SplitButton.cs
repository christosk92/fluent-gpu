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
    public Element? PrimaryContent;
    public Action? OnInvoke;
    public IReadOnlyList<MenuFlyoutItem> Items = [];

    public static Element Create(string label, Action onInvoke, IReadOnlyList<MenuFlyoutItem> items, string? glyph = null)
        => Embed.Comp(() => new SplitButton { Label = label, OnInvoke = onInvoke, Items = items, Glyph = glyph });

    public static Element Create(Element primaryContent, Action onInvoke, IReadOnlyList<MenuFlyoutItem> items)
        => Embed.Comp(() => new SplitButton { PrimaryContent = primaryContent, OnInvoke = onInvoke, Items = items });

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

        Element[] primaryContent;
        if (PrimaryContent is { } custom)
        {
            primaryContent = [custom];
        }
        else
        {
            var list = new List<Element>();
            if (Glyph is { Length: > 0 } g) list.Add(new TextEl(g) { Size = 14f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont });
            list.Add(new TextEl(Label) { Size = 14f, Color = Tok.TextPrimary });
            primaryContent = list.ToArray();
        }

        var primary = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Height = 32f, MinWidth = 35f,   // SplitButtonPrimaryButtonSize
            Padding = new Edges4(11, 5, 11, 6),
            Fill = ColorF.Transparent, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            Role = AutomationRole.Button, OnClick = OnInvoke,
            Children = primaryContent,
        };
        var divider = new BoxEl { Width = 1f, Height = 16f, Fill = Tok.StrokeControlDefault, AlignSelf = FlexAlign.Center };  // SplitButtonBorderBrushDivider = StrokeControlDefault
        var drop = new BoxEl
        {
            Width = 35f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,  // SplitButtonSecondaryButtonSize = 35
            Fill = ColorF.Transparent, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            Role = AutomationRole.Button, OnClick = ToggleMenu,
            // SplitButtonForegroundSecondary = TextSecondary; chevron AnimatedIcon is 12x12.
            Children = [new TextEl(Icons.ChevronDown) { Size = 12f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont }],
        };

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            MinHeight = 32f,
            Fill = Tok.FillControlDefault,
            BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder, Corners = Radii.ControlAll,
            ClipToBounds = true,
            OnRealized = h => anchor.Value = h,
            Children = [primary, divider, drop],
        };
    }
}
