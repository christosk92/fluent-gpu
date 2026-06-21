using System.ComponentModel;

namespace Wavee.Core;

/// <summary>
/// The in-process fake player: implements the command surface (<see cref="IPlaybackPlayer"/>), the observable state
/// (<see cref="IPlaybackState"/>), and lyrics (<see cref="ILyricsProvider"/>). A 1 Hz clock advances position while
/// playing and emits <see cref="PositionTicks"/> — modelling the real IPC snapshot cadence the UI interpolates between.
/// </summary>
public sealed class FakePlaybackProvider : IPlaybackPlayer, IPlaybackState, ILyricsProvider, IDisposable
{
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<long> _ticks = new();
    readonly CancellationTokenSource _cts = new();
    readonly List<QueueEntry> _queue = [.. FakeData.DefaultQueue()];
    int _cursor;

    /// <summary>Simulated audio-resolve / buffering latency before a freshly-started track begins to play — the row's
    /// #-cell spinner and the player-bar activity sweep show for this long, then playback starts.</summary>
    const int BufferingMs = 700;

    public FakePlaybackProvider()
    {
        var first = _queue[0].Track;
        CurrentTrack = first;
        DurationMs = first.DurationMs;
        Palette = FakeData.PaletteFor(0);
        Queue = _queue;
        _ = RunClockAsync(_cts.Token);
    }

    // ── IPlaybackState ──────────────────────────────────────────────────────────────────────────────────────────────
    public Track? CurrentTrack { get; private set; }
    public string? ContextUri { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsBuffering { get; private set; }
    public long PositionMs { get; private set; }
    public long DurationMs { get; private set; }
    public double Volume { get; private set; } = 0.7;
    public bool IsShuffle { get; private set; }
    public RepeatMode Repeat { get; private set; }
    public Palette? Palette { get; private set; }
    public IReadOnlyList<QueueEntry> Queue { get; private set; }
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<long> PositionTicks => _ticks;
    public event PropertyChangedEventHandler? PropertyChanged;

    public IPlaybackState State => this;

    // ── IPlaybackPlayer (commands) ──────────────────────────────────────────────────────────────────────────────────
    public async Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default)
    {
        // Resolve the context to ITS ordered track list (the same deterministic catalog the detail page loads) and
        // replace the queue with it, so we start the ACTUAL track the clicked row shows (startIndex into THIS context),
        // not a fixed default queue. Next/Previous then walk this context.
        var tracks = FakeData.ContextTracks(contextUri);
        if (tracks.Count == 0) return;
        ContextUri = contextUri;          // the now-playing context (cards compare their uri to this)
        _queue.Clear();
        for (int i = 0; i < tracks.Count; i++)
            _queue.Add(new QueueEntry("q" + i, tracks[i], i == 0 ? QueueBucket.NowPlaying : QueueBucket.NextUp, false));
        Queue = _queue.ToArray();
        _cursor = Math.Clamp(startIndex, 0, _queue.Count - 1);
        await StartWithBufferingAsync(_queue[_cursor].Track, ct).ConfigureAwait(false);
    }
    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default) { IsPlaying = true; Bump(); return Task.CompletedTask; }
    public Task PauseAsync(CancellationToken ct = default) { IsPlaying = false; Bump(); return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken ct = default) { IsPlaying = true; Bump(); return Task.CompletedTask; }
    public Task NextAsync(CancellationToken ct = default) { _cursor = (_cursor + 1) % _queue.Count; Load(_queue[_cursor].Track); Bump(); return Task.CompletedTask; }
    public Task PreviousAsync(CancellationToken ct = default) { if (PositionMs > 3000) { PositionMs = 0; } else { _cursor = (_cursor - 1 + _queue.Count) % _queue.Count; Load(_queue[_cursor].Track); } Bump(); return Task.CompletedTask; }
    public Task SeekAsync(long positionMs, CancellationToken ct = default) { PositionMs = Math.Clamp(positionMs, 0, DurationMs); _ticks.OnNext(PositionMs); Bump(); return Task.CompletedTask; }
    public Task SetVolumeAsync(double volume01, CancellationToken ct = default) { Volume = Math.Clamp(volume01, 0, 1); Bump(); return Task.CompletedTask; }
    public Task SetShuffleAsync(bool on, CancellationToken ct = default) { IsShuffle = on; Bump(); return Task.CompletedTask; }
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default) { Repeat = mode; Bump(); return Task.CompletedTask; }

    public Task MoveQueueAsync(string entryId, int toIndex, CancellationToken ct = default)
    {
        int from = _queue.FindIndex(q => q.EntryId == entryId);
        if (from >= 0) { var e = _queue[from]; _queue.RemoveAt(from); _queue.Insert(Math.Clamp(toIndex, 0, _queue.Count), e); Queue = _queue.ToArray(); Bump(); }
        return Task.CompletedTask;
    }

    public Task RemoveFromQueueAsync(string entryId, CancellationToken ct = default)
    {
        if (_queue.RemoveAll(q => q.EntryId == entryId) > 0) { Queue = _queue.ToArray(); Bump(); }
        return Task.CompletedTask;
    }

    // ── ILyricsProvider ─────────────────────────────────────────────────────────────────────────────────────────────
    public async Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
    {
        await Task.Delay(150, ct).ConfigureAwait(false);
        return FakeData.Lyrics(trackId);
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────────────────────────
    void Load(Track t)
    {
        CurrentTrack = t;
        DurationMs = t.DurationMs;
        PositionMs = 0;
        Palette = FakeData.PaletteFor(Math.Abs(t.Id.GetHashCode()));
        _ticks.OnNext(0);
    }

    /// <summary>Switch to <paramref name="t"/> and show buffering, wait a simulated resolve latency, then start playing
    /// — unless a newer Play superseded us in the meantime (the current track changed, or the run was cancelled).</summary>
    async Task StartWithBufferingAsync(Track t, CancellationToken ct)
    {
        Load(t);
        IsBuffering = true;
        IsPlaying = true;
        Bump();                                  // the bar shows the new track + the activity sweep; the row keeps its spinner
        try { await Task.Delay(BufferingMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || CurrentTrack?.Id != t.Id) return;   // a newer Play took over → don't clobber it
        IsBuffering = false;
        Bump();
    }

    void Bump()
    {
        PropertyChanged?.Invoke(this, _allChanged);
        _changes.OnNext(this);
    }
    static readonly PropertyChangedEventArgs _allChanged = new(null);

    async Task RunClockAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!IsPlaying || CurrentTrack is null) continue;
                PositionMs = Math.Min(DurationMs, PositionMs + 1000);
                _ticks.OnNext(PositionMs);
                if (PositionMs >= DurationMs) await NextAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}
