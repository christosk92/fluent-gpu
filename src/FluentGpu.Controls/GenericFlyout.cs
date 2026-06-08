using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI Flyout: a button that opens an anchored, light-dismissable flyout holding arbitrary content (not a
/// menu). Reuses the <see cref="OverlayHost"/> overlay service exactly like <see cref="DropDownButton"/>; the host already
/// wraps the body in an acrylic <c>FlyoutSurface</c> (shadow/border/corners), so the content closure returns just the
/// inner content wrapped in a padded presenter card. Toggling re-click closes it.</summary>
public sealed class FlyoutButton : Component
{
    public string Label = "";
    public Func<Element> Content = () => new BoxEl();

    public static Element Create(string label, Func<Element> content)
        => Embed.Comp(() => new FlyoutButton { Label = label, Content = content });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => new BoxEl
                {
                    Direction = 1,
                    Padding = Edges4.All(16),
                    MinWidth = 160f,
                    Children = [Content()],
                },
                FlyoutPlacement.BottomLeft);
        }

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
            ClipToBounds = true,
            Role = AutomationRole.Button,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [new TextEl(Label) { Size = 14f, Color = Tok.TextPrimary }],
        };
    }
}
