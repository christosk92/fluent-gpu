using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

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

/// <summary>Renders the current toast (InfoBar-flavored). Re-renders only when <see cref="Toasts.Current"/> changes.</summary>
public sealed class ToastHost : Component
{
    public override Element Render()
    {
        if (Toasts.Current.Value is not { } toast) return new BoxEl { Height = 0 };

        var (bg, fg) = toast.Severity switch
        {
            ToastSeverity.Success => (Tok.SystemFillSuccessBackground, Tok.SystemFillSuccess),
            ToastSeverity.Caution => (Tok.SystemFillCautionBackground, Tok.SystemFillCaution),
            ToastSeverity.Critical => (Tok.SystemFillCriticalBackground, Tok.SystemFillCritical),
            _ => (Tok.FillCardDefault, Tok.AccentDefault),
        };

        var kids = new List<Element>
        {
            Icon(Icons.InfoBarBackgroundCircle, 16f, fg),
            WaveeType.TrackMeta(toast.Message),
            new BoxEl { Grow = 1 },
        };
        if (toast is { ActionLabel: { } label, OnAction: { } act })
            kids.Add(Button.Standard(label, () => { act(); Toasts.Dismiss(); }));
        kids.Add(Button.Standard("Dismiss", Toasts.Dismiss));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Padding = Edges4.All(WaveeSpace.M), Fill = bg, Corners = CornerRadius4.All(WaveeRadius.Card),
            Children = kids.ToArray(),
        };
    }
}
