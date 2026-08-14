namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// The fields a data-bound <c>&lt;progress&gt;</c> bar reflects — paired with <see cref="ToastBuilder.Progress"/>
/// (data-bound). Write these into a live toast via <see cref="ToastNotifier.Update"/> using the placeholder keys
/// <c>progressValue</c> / <c>progressStatus</c> / <c>progressTitle</c> / <c>progressValueString</c> (any omitted
/// key is left unchanged by the platform).
/// </summary>
/// <param name="Value">0.0..1.0 determinate fraction, or <see langword="null"/> for an indeterminate (marquee) bar.</param>
/// <param name="Status">The caption under the bar (e.g. "Downloading…").</param>
/// <param name="Title">Optional bold label above the bar.</param>
/// <param name="ValueStringOverride">Optional text replacing the default "NN%" readout.</param>
public readonly record struct ToastProgress(double? Value = null, string? Status = null, string? Title = null, string? ValueStringOverride = null);

/// <summary>The outcome of <see cref="ToastNotifier.Update"/> — the WinRT
/// <c>NotificationUpdateResult</c> tri-state (an expired/dismissed toast is <see cref="NotificationNotFound"/>, NOT an error).</summary>
public enum ToastUpdateResult
{
    /// <summary>The live toast was updated in place.</summary>
    Succeeded = 0,
    /// <summary>The platform failed to apply the update.</summary>
    Failed = 1,
    /// <summary>No matching toast (by tag/group) is currently showing / in the Action Center.</summary>
    NotificationNotFound = 2,
}
