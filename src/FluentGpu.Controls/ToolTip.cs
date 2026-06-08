using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI ToolTip: wraps a target element and surfaces a small text bubble anchored beneath it. Hover infra is
/// limited in this slice, so the demo is click-to-show — clicking the target toggles the tooltip popup via the overlay
/// service (re-click or light-dismiss closes it). The target captures its own node as the popup anchor.</summary>
public sealed class ToolTip : Component
{
    public Element Target = new BoxEl();
    public string Text = "";

    public static Element Wrap(Element target, string text)
        => Embed.Comp(() => new ToolTip { Target = target, Text = text });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (h.Value is { IsOpen: true } o) { o.Close(); return; }
            h.Value = svc.Open(
                () => anchor.Value,
                // WinUI ToolTip bubble: its own chrome — flyout/acrylic-style layer fill (AcrylicInAppFillColorDefault),
                // 1px flyout stroke, 4px corners, a soft elevation shadow, 9,6,9,8 padding, 12px text, capped at 320px wide.
                () => new BoxEl
                {
                    Fill = Tok.FillLayerDefault,
                    BorderColor = Tok.StrokeFlyoutDefault,
                    BorderWidth = 1f,
                    Corners = Radii.ControlAll,
                    Shadow = Elevation.Flyout,
                    MaxWidth = 320f,
                    Padding = new Edges4(9, 6, 9, 8),
                    Children = [new TextEl(Text) { Size = 12f, Color = Tok.TextPrimary, MaxWidth = 302f }],
                },
                FlyoutPlacement.BottomLeft);
        }

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = x => anchor.Value = x,
            OnClick = Toggle,
            Children = [Target],
        };
    }
}
