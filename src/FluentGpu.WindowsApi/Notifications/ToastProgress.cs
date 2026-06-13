namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// The fields a data-bound <c>&lt;progress&gt;</c> bar reflects — paired with <see cref="ToastBuilder.Progress"/>
/// (data-bound). The in-place update path (write these into a LIVE toast's NotificationData without re-showing it) is a
/// documented fast-follow; today the values are baked at build time via <c>Progress(dataBound: false)</c> or seeded
/// data-bound. Any <c>null</c> field is left unchanged when the update path lands.
/// </summary>
/// <param name="Value">0.0..1.0 determinate fraction, or <see langword="null"/> for an indeterminate (marquee) bar.</param>
/// <param name="Status">The caption under the bar (e.g. "Downloading…").</param>
/// <param name="Title">Optional bold label above the bar.</param>
/// <param name="ValueStringOverride">Optional text replacing the default "NN%" readout.</param>
public readonly record struct ToastProgress(double? Value = null, string? Status = null, string? Title = null, string? ValueStringOverride = null);

/// <summary>The outcome of the in-place progress-update path (a documented fast-follow) — the WinRT
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
