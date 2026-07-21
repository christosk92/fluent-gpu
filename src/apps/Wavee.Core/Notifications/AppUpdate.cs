using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>The app-update lifecycle. None = nothing pending. The app has no real updater yet; this is the notification
/// type + a seam so a real updater is a drop-in later (see <see cref="NullAppUpdateService"/>).</summary>
public enum AppUpdateState { None, Available, Downloaded, Completed, Failed }

/// <summary>The app-update seam. App-scoped (one process-wide updater) → NO switchable wrapper; a plain field on Services.
/// Snapshot properties (<see cref="Current"/>/<see cref="Version"/>/<see cref="ReleaseNotesUrl"/>/<see cref="Error"/>)
/// let the bridge build an <see cref="AppUpdateNotification"/>; <see cref="Changed"/> fires when they move.</summary>
public interface IAppUpdateService
{
    AppUpdateState Current { get; }
    string? Version { get; }
    string? ReleaseNotesUrl { get; }
    string? Error { get; }
    IObservable<int> Changed { get; }

    Task CheckAsync(CancellationToken ct);
    Task DownloadAsync(CancellationToken ct);
    void RestartToApply();
    /// <summary>Clears a Completed/Failed notification (the user saw it) — never fired on panel open.</summary>
    void Acknowledge();
}

/// <summary>The default (no updater wired): permanently <see cref="AppUpdateState.None"/>, every action inert.</summary>
public sealed class NullAppUpdateService : IAppUpdateService
{
    readonly SimpleEvent<int> _changed = new();
    public AppUpdateState Current => AppUpdateState.None;
    public string? Version => null;
    public string? ReleaseNotesUrl => null;
    public string? Error => null;
    public IObservable<int> Changed => _changed;
    public Task CheckAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DownloadAsync(CancellationToken ct) => Task.CompletedTask;
    public void RestartToApply() { }
    public void Acknowledge() { }
}
