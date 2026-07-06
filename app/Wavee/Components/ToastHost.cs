using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public enum ToastSeverity { Info, Success, Caution, Critical }
public readonly record struct ToastModel(string Message, ToastSeverity Severity, string? ActionLabel, Action? OnAction);

/// <summary>Transient-notification service (one at a time). Supports the optimistic "Undo" affordance: show a toast with
/// an action that reverses the optimistic write. Mount <see cref="ToastHost"/> once at the app root to render it.</summary>
public static class Toasts
{
    public static readonly Signal<ToastModel?> Current = new(null);

    public static void Show(string message, ToastSeverity severity = ToastSeverity.Info, string? actionLabel = null, Action? onAction = null)
        => Current.Value = new ToastModel(message, severity, actionLabel, onAction);

    public static void Dismiss() => Current.Value = null;
}

/// <summary>Renders the current toast as a real WinUI <see cref="InfoBar"/> (severity icon + message + optional action +
/// the 38×38 ✕ close). Re-renders only when <see cref="Toasts.Current"/> changes; auto-dismisses after a few seconds.</summary>
public sealed class ToastHost : Component
{
    const int AutoDismissMs = 6000;

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

        if (toastOpt is not { } toast) return new BoxEl { Height = 0 };

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

        return InfoBar.Create(severity, title: string.Empty, message: toast.Message,
            onClose: Toasts.Dismiss, actionButton: action);
    }
}
