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
                    () => new BoxEl
                    {
                        Direction = 1,
                        Gap = 6,
                        MinWidth = 240,
                        MaxWidth = 320,
                        Padding = Edges4.All(16),
                        Children = new Element[]
                        {
                            new TextEl(Title) { Size = 16, Bold = true, Color = Tok.TextPrimary },
                            new TextEl(Body) { Size = 14, Color = Tok.TextSecondary },
                            new BoxEl
                            {
                                Direction = 0,
                                Justify = FlexJustify.End,
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
