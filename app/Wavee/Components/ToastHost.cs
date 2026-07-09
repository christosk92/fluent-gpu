using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public enum ToastSeverity { Info, Success, Caution, Critical }
// Seq gives each toast an identity: the host keys the rendered toast on it (enter/exit animate per toast, and a
// replacement reads as a swap), and the auto-dismiss timer dep re-arms even for identical consecutive messages.
public readonly record struct ToastModel(string Message, ToastSeverity Severity, string? ActionLabel, Action? OnAction, int Seq = 0);

/// <summary>Transient-notification service (one at a time). Supports the optimistic "Undo" affordance: show a toast with
/// an action that reverses the optimistic write. Mount <see cref="ToastHost"/> once at the app root to render it.</summary>
public static class Toasts
{
    public static readonly Signal<ToastModel?> Current = new(null);
    static int _seq;

    public static void Show(string message, ToastSeverity severity = ToastSeverity.Info, string? actionLabel = null, Action? onAction = null)
        => Current.Value = new ToastModel(message, severity, actionLabel, onAction, ++_seq);

    public static void Dismiss() => Current.Value = null;
}

/// <summary>Renders the current toast as a real WinUI <see cref="InfoBar"/> (severity icon + message + optional action +
/// the 38×38 ✕ close). Re-renders only when <see cref="Toasts.Current"/> changes; auto-dismisses after a few seconds.</summary>
public sealed class ToastHost : Component
{
    const int AutoDismissMs = 6000;

    // transitions.dev panel-reveal in (rise from below + fade + blur, ~400ms) / quick blurred fade out (~150ms).
    // The host sits above the player bar (bottom-anchored), so the enter rises FROM below (+Dy).
    static readonly LayoutTransition ToastMotion = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Slow, Easing.SmoothOut),
        Enter: new EnterExit(Dy: Expressive.DistLarge, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall),
        Exit: new EnterExit(Opacity: 0f, Active: true, Blur: Expressive.BlurSmall),
        ExitDynamics: TransitionDynamics.Tween(Expressive.Quick, Easing.SmoothOut));

    public override Element Render()
    {
        // Hooks FIRST (stable order — rule #7), before the early return. Auto-dismiss the toast after a few seconds: a
        // one-shot Timer per toast, generation-guarded so a rapid replacement cancels the stale dismiss, cleared via post()
        // on the UI thread (the toast is a UI-thread signal). Mirrors WaveeShell's _railLockTimer generation pattern.
        var toastOpt = Toasts.Current.Value;   // subscribe → re-render (and re-arm the timer) on every toast change
        var post = UsePost();
        var timer = UseRef<System.Threading.Timer?>(null);
        var gen = UseRef(0);
        UseEffect(() =>
        {
            int g = ++gen.Value;
            timer.Value?.Dispose();
            timer.Value = null;
            if (toastOpt is null) return;
            timer.Value = new System.Threading.Timer(
                _ => post(() => { if (g == gen.Value && Toasts.Current.Peek() is not null) Toasts.Dismiss(); }),
                null, AutoDismissMs, System.Threading.Timeout.Infinite);
        }, toastOpt);

        // The root is ALWAYS a plain BoxEl (stable single-child diff); the toast itself is a KEYED child — keys are
        // honored only in child arrays, and the keyed remount is what plays Enter on show and the Exit orphan fade on
        // dismiss/replace (a new Seq exits the old toast while the new one rises — reads as a swap).
        if (toastOpt is not { } toast) return new BoxEl();

        var severity = toast.Severity switch
        {
            ToastSeverity.Success => InfoBarSeverity.Success,
            ToastSeverity.Caution => InfoBarSeverity.Warning,
            ToastSeverity.Critical => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

        // The optional CTA (e.g. "Choose device" / "Undo") becomes the InfoBar's trailing action button; clicking it runs
        // the action then dismisses the toast. The ✕ close is the InfoBar's own button (onClose → Dismiss).
        Element? action = toast is { ActionLabel: { } label, OnAction: { } act }
            ? Button.Standard(label, () => { act(); Toasts.Dismiss(); })
            : null;

        return new BoxEl
        {
            Children =
            [
                new BoxEl
                {
                    Key = "toast:" + toast.Seq,
                    Animate = ToastMotion,
                    Children = [InfoBar.Create(severity, title: string.Empty, message: toast.Message,
                                               onClose: Toasts.Dismiss, actionButton: action)],
                },
            ],
        };
    }
}
