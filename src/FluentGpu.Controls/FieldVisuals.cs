using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Shared form-validation visuals (form-validation.md): the error message row every input control renders below its
/// field. Kept in one place so the reveal animation + critical-color styling stay identical across TextBox / PasswordBox
/// / NumberBox / ComboBox / AutoSuggestBox. (The invalid BORDER is the control's chrome <c>BoxEl.Validation</c> channel,
/// driven by the same error memo via <see cref="FieldBinding"/>.)
/// </summary>
internal static class FieldVisuals
{
    // Size=Reflow + Opacity enter/exit: on appear the row's height eases from 0 (the content below reflows to make room)
    // and it cross-fades in; on disappear it shrinks + fades out (deferred-free keeps it live through the exit).
    static readonly LayoutTransition Reveal = new(
        TransitionChannels.Size | TransitionChannels.Opacity,
        TransitionDynamics.Tween(180f, Easing.FluentDecelerate),
        Size: SizeMode.Reflow,
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The error message row, bound to a field's gated error memo: it occupies ZERO layout space while the field
    /// is valid and only mounts (animating in) when an error appears. The loc key resolves at the bound thunk
    /// (culture-reactive, no re-render; a no-arg key hit allocates nothing).</summary>
    public static Element MessageRow(IReadSignal<FieldError> error)
        => Flow.Show(() => !error.Value.IsValid,
            new BoxEl
            {
                Direction = 1,
                Margin = new Edges4(0, 4, 0, 0),
                Animate = Reveal,
                Children =
                [
                    new TextEl("")
                    {
                        Size = 12f,
                        Color = Tok.SystemFillCritical,
                        Text = Prop.Of(() => Msg.Resolve(error.Value.First)),
                    },
                ],
            });
}
