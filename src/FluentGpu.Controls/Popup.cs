using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI Popup: displays arbitrary content above other content, opened and closed programmatically.
/// A trigger button toggles a small anchored popup; re-clicking (or invoking again) closes it. Reuses the overlay
/// service, which already wraps the content thunk in an acrylic card with shadow/border/corners.</summary>
public sealed class Popup : Component
{
    public string TriggerLabel = "Show popup";
    public string Text = "Popup content";
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real popup after first mount

    public static Element Create(string triggerLabel, string text)
        => Embed.Comp(() => new Popup { TriggerLabel = triggerLabel, Text = text });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var autoOpened = UseRef(false);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => new BoxEl
                {
                    Direction = 1,
                    Padding = Edges4.All(16),
                    MinWidth = 180f,
                    Children = new Element[]
                    {
                        new TextEl(Text) { Size = 14f, Color = Tok.TextPrimary },
                    },
                },
                FlyoutPlacement.BottomLeft);
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            Toggle();
        }, OpenOnMount);

        return new BoxEl
        {
            AlignSelf = FlexAlign.Start,
            OnRealized = h => anchor.Value = h,
            Children = new Element[]
            {
                Button.Standard(TriggerLabel, Toggle),
            },
        };
    }
}
