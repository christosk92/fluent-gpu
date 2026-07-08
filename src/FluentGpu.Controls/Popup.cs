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

    /// <summary>WinUI <c>Popup.ShouldConstrainToRootBounds</c> (Popup_Partial.cpp:951-970: false ⇒ a WINDOWED popup
    /// rendered in its own top-level window, placed against the monitor work area — Popup_Partial.cpp:1019
    /// <c>SetIsWindowed</c>). Default true (constrained to the window, like a non-windowed XAML popup). When the
    /// platform/host cannot create popup windows the overlay host silently falls back to constrained placement
    /// (WinUI's <c>DoesPlatformSupportWindowedPopup</c> gate, FlyoutBase_Partial.cpp:3188).</summary>
    public bool ShouldConstrainToRootBounds = true;

    public static Element Create(string triggerLabel, string text)
        => Embed.Comp(() => new Popup { TriggerLabel = triggerLabel, Text = text });

    // Frozen-props tripwire (ReuseGuard): TriggerLabel/Text freeze at mount. A reused instance whose text changed is
    // the frozen-props bug — deliver it reactively or remount with a Key.
    public override bool ChecksReuse => ReuseGuard.CompiledIn;
    public override void DebugCheckReuse(Component next)
    {
        if (next is not Popup n) return;
        if (n.TriggerLabel != TriggerLabel) ReuseGuard.ScalarChanged(this, nameof(TriggerLabel));
        else if (n.Text != Text) ReuseGuard.ScalarChanged(this, nameof(Text));
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var autoOpened = UseRef(false);
        bool constrain = ShouldConstrainToRootBounds;

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
                FlyoutPlacement.BottomLeft,
                new PopupOptions() { ConstrainToRootBounds = constrain });
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
