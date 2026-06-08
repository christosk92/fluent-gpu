using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI TeachingTip: an anchored, non-modal callout that teaches or highlights a feature. A trigger button opens an
/// overlay-hosted flyout with a title, a body, and a dismiss ("Got it") button. The flyout is anchored to the trigger's
/// wrapper node and light-dismisses like any other overlay. Resolve the overlay service via <c>UseContext(Overlay.Service)</c>.
/// </summary>
public sealed class TeachingTip : Component
{
    public string TriggerLabel = "Show tip";
    public string Title = "Tip";
    public string Body = "";

    /// <summary>Embed a TeachingTip with the given trigger label, title, and body.</summary>
    public static Element Create(string triggerLabel, string title, string body) =>
        Embed.Comp(() => new TeachingTip { TriggerLabel = triggerLabel, Title = title, Body = body });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (h.Value is { IsOpen: true } open) { open.Close(); }
            else
            {
                h.Value = svc.Open(
                    () => anchor.Value,
                    // WinUI TeachingTip body: MinWidth 320 / MaxWidth 336, content padding 12. The title and body both
                    // use TextFillColorPrimary (TeachingTipTitleForeground / TeachingTipForeground). The footer button
                    // panel carries a 0,12,0,0 top margin (TeachingTip button panel margin). The flyout chrome (border,
                    // corners, shadow) is supplied by the shared FlyoutSurface, so the body adds no background/stroke.
                    () => new BoxEl
                    {
                        Direction = 1,
                        Gap = 6,
                        MinWidth = 320,
                        MaxWidth = 336,
                        Padding = Edges4.All(12),
                        Children = new Element[]
                        {
                            new TextEl(Title) { Size = 16, Bold = true, Color = Tok.TextPrimary },
                            new TextEl(Body) { Size = 14, Color = Tok.TextPrimary },
                            new BoxEl
                            {
                                Direction = 0,
                                Justify = FlexJustify.End,
                                Margin = new Edges4(0, 12, 0, 0),
                                Children = new Element[]
                                {
                                    Button.Accent("Got it", () => h.Value?.Close()),
                                },
                            },
                        },
                    },
                    FlyoutPlacement.BottomLeft);
            }
        }

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            Role = AutomationRole.Button,
            OnRealized = x => anchor.Value = x,
            Children = new Element[]
            {
                Button.Accent(TriggerLabel, Toggle),
            },
        };
    }
}
