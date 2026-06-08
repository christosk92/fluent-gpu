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
                () => new BoxEl
                {
                    Padding = new Edges4(10, 6, 10, 6),
                    Children = [new TextEl(Text) { Size = 12f, Color = Tok.TextPrimary }],
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
