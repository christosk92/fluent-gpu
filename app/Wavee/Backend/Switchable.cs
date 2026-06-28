using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── Switchable playback facade — bind once, swap the backend live ─────────────────────────────────────────────────────
// The UI's PlaybackBridge captures its seams (player/state/devices) in its ctor and subscribes them once. To go from the
// in-memory fake to the live Connect backend WITHOUT rebuilding the UI, the bridge binds to these stable facades; on
// connect the composition root calls SetInner(live) and the facade re-points + re-emits, so every existing subscription
// keeps working against the new backend. (Better than WaveeMusic's DI-rebuild-on-login; the bridge never re-points.)

public sealed class SwitchableState : IPlaybackState, IDisposable
{
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<long> _ticks = new();
    readonly object _gate = new();
    IPlaybackState _inner;
    IDisposable? _changeSub, _tickSub;

    public SwitchableState(IPlaybackState inner) { _inner = inner; Wire(inner); }

    public void SetInner(IPlaybackState inner)
    {
        lock (_gate)
        {
            _changeSub?.Dispose(); _tickSub?.Dispose();
            _inner = inner; Wire(inner);
        }
        _changes.OnNext(this);   // refresh every reader against the new backend
    }

    void Wire(IPlaybackState s)
    {
        _changeSub = s.Changes.Subscribe(Observers.From<IPlaybackState>(_ => _changes.OnNext(this)));
        _tickSub = s.PositionTicks.Subscribe(Observers.From<long>(ms => _ticks.OnNext(ms)));
    }

    IPlaybackState Cur { get { lock (_gate) return _inner; } }

    public Track? CurrentTrack => Cur.CurrentTrack;
    public string? ContextUri => Cur.ContextUri;
    public bool IsPlaying => Cur.IsPlaying;
    public bool IsBuffering => Cur.IsBuffering;
    public long PositionMs => Cur.PositionMs;
    public long DurationMs => Cur.DurationMs;
    public double Volume => Cur.Volume;
    public bool IsShuffle => Cur.IsShuffle;
    public RepeatMode Repeat => Cur.Repeat;
    public Palette? Palette => Cur.Palette;
    public IReadOnlyList<QueueEntry> Queue => Cur.Queue;
    public bool IsLoading => Cur.IsLoading;
    public string? Error => Cur.Error;
    public bool CanSkipNext => Cur.CanSkipNext;
    public bool CanSkipPrev => Cur.CanSkipPrev;
    public bool CanSeek => Cur.CanSeek;
    public string? ActiveDeviceId => Cur.ActiveDeviceId;
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<long> PositionTicks => _ticks;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }   // consumers use Changes

    public void Dispose() { _changeSub?.Dispose(); _tickSub?.Dispose(); }
}

public sealed class SwitchablePlayer : IPlaybackPlayer
{
    readonly SwitchableState _state;
    readonly object _gate = new();
    IPlaybackPlayer _inner;

    public SwitchablePlayer(IPlaybackPlayer inner) { _inner = inner; _state = new SwitchableState(inner.State); }

    public void SetInner(IPlaybackPlayer inner) { lock (_gate) _inner = inner; _state.SetInner(inner.State); }
    IPlaybackPlayer Cur { get { lock (_gate) return _inner; } }

    public IPlaybackState State => _state;
    public Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default) => Cur.PlayAsync(contextUri, startIndex, ct);
    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default) => Cur.PlayTrackAsync(trackUri, ct);
    public Task PauseAsync(CancellationToken ct = default) => Cur.PauseAsync(ct);
    public Task ResumeAsync(CancellationToken ct = default) => Cur.ResumeAsync(ct);
    public Task NextAsync(CancellationToken ct = default) => Cur.NextAsync(ct);
    public Task PreviousAsync(CancellationToken ct = default) => Cur.PreviousAsync(ct);
    public Task SeekAsync(long positionMs, CancellationToken ct = default) => Cur.SeekAsync(positionMs, ct);
    public Task SetVolumeAsync(double volume01, CancellationToken ct = default) => Cur.SetVolumeAsync(volume01, ct);
    public Task SetShuffleAsync(bool on, CancellationToken ct = default) => Cur.SetShuffleAsync(on, ct);
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default) => Cur.SetRepeatAsync(mode, ct);
    public Task MoveQueueAsync(string entryId, int toIndex, CancellationToken ct = default) => Cur.MoveQueueAsync(entryId, toIndex, ct);
    public Task RemoveFromQueueAsync(string entryId, CancellationToken ct = default) => Cur.RemoveFromQueueAsync(entryId, ct);
    public Task EnqueueAsync(string trackUri, CancellationToken ct = default) => Cur.EnqueueAsync(trackUri, ct);
}

public sealed class SwitchableDevices : IConnectDevices, IDisposable
{
    readonly SimpleSubject<IReadOnlyList<PlaybackDevice>> _changed = new(Array.Empty<PlaybackDevice>());
    readonly object _gate = new();
    IConnectDevices _inner;
    IDisposable? _sub;

    public SwitchableDevices(IConnectDevices inner) { _inner = inner; Wire(inner); }

    public void SetInner(IConnectDevices inner)
    {
        lock (_gate) { _sub?.Dispose(); _inner = inner; Wire(inner); }
        _changed.OnNext(inner.Devices);
    }

    void Wire(IConnectDevices d) => _sub = d.DevicesChanged.Subscribe(Observers.From<IReadOnlyList<PlaybackDevice>>(x => _changed.OnNext(x)));
    IConnectDevices Cur { get { lock (_gate) return _inner; } }

    public IReadOnlyList<PlaybackDevice> Devices => Cur.Devices;
    public IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged => _changed;
    public Task TransferAsync(string deviceId, CancellationToken ct = default) => Cur.TransferAsync(deviceId, ct);

    public void Dispose() => _sub?.Dispose();
}

public sealed class SwitchableSession : ISpotifySession, IDisposable
{
    readonly SimpleSubject<AuthStatus> _status;
    readonly object _gate = new();
    ISpotifySession _inner;
    IDisposable? _sub;

    public SwitchableSession(ISpotifySession inner) { _inner = inner; _status = new SimpleSubject<AuthStatus>(inner.Status); Wire(inner); }

    public void SetInner(ISpotifySession inner)
    {
        lock (_gate) { _sub?.Dispose(); _inner = inner; Wire(inner); }
        _status.OnNext(inner.Status);   // refresh the auth chip / user against the live session
    }

    void Wire(ISpotifySession s) => _sub = s.StatusChanged.Subscribe(Observers.From<AuthStatus>(st => _status.OnNext(st)));
    ISpotifySession Cur { get { lock (_gate) return _inner; } }

    public AuthStatus Status => Cur.Status;
    public WaveeUser? CurrentUser => Cur.CurrentUser;
    public IObservable<AuthStatus> StatusChanged => _status;
    public Task<bool> ConnectAsync(CancellationToken ct = default) => Cur.ConnectAsync(ct);
    public Task LogoutAsync(CancellationToken ct = default) => Cur.LogoutAsync(ct);
    public void Dispose() => _sub?.Dispose();
}

/// <summary>An ISpotifySession reflecting a completed live login (Authenticated + the real account).</summary>
public sealed class LiveSpotifySession : ISpotifySession
{
    readonly SimpleSubject<AuthStatus> _status = new(AuthStatus.Authenticated);
    public LiveSpotifySession(string account, bool isPremium) : this(account, account, null, isPremium) { }
    public LiveSpotifySession(string account, string displayName, string? avatarUrl, bool isPremium, string? email = null)
        => CurrentUser = new WaveeUser(account, string.IsNullOrWhiteSpace(displayName) ? account : displayName, avatarUrl, isPremium, email);
    public AuthStatus Status { get; private set; } = AuthStatus.Authenticated;
    public WaveeUser? CurrentUser { get; private set; }
    public IObservable<AuthStatus> StatusChanged => _status;
    public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task LogoutAsync(CancellationToken ct = default) { Status = AuthStatus.LoggedOut; CurrentUser = null; _status.OnNext(AuthStatus.LoggedOut); return Task.CompletedTask; }
}
