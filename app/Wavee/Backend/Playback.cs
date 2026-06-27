using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;

namespace Wavee.Backend;

// ── ENGINE ④ — Playback (pure QueueCore → reducer → event log → projections) ─────────────────────────────────────────

/// <summary>QueueCore — the pure queue algorithm (3 buckets, dual cursor, shuffle, repeat, advance ladder). No I/O.
/// The user-queue drains first WITHOUT advancing the context cursor; the ladder terminates at EndOfContext (autoplay is
/// an async request the reducer surfaces, never network I/O inside this pure core).</summary>
public sealed class QueueCore
{
    readonly List<Track> _context = new();      // the PLAY order (shuffled or natural)
    readonly List<Track> _original = new();     // the natural order — kept so shuffle can be turned back OFF
    readonly List<Track> _userQueue = new();    // q# — drains before the context
    int _cursor = -1;
    int _seedState = 0x5DEECE66 & 0x7fffffff;

    public string? ContextUri { get; private set; }
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; private set; }
    public Track? Current { get; private set; }

    public void SetContext(string uri, IEnumerable<Track> tracks, int startIndex)
    {
        _original.Clear();
        _original.AddRange(tracks);
        _userQueue.Clear();
        ContextUri = uri;
        _context.Clear();
        _context.AddRange(_original);
        _cursor = _context.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _context.Count - 1);
        Current = _cursor >= 0 ? _context[_cursor] : null;
        if (Shuffle) ReshuffleAnchoringCurrent();   // a context set while shuffle is ON is shuffled (was previously ignored)
    }

    public void EnqueueUser(Track t) => _userQueue.Add(t);

    public Track? Next()
    {
        if (Repeat == RepeatMode.Track && Current != null) return Current;          // repeat-one holds
        if (_userQueue.Count > 0) { Current = _userQueue[0]; _userQueue.RemoveAt(0); return Current; }  // q# first; cursor unmoved
        if (_cursor + 1 < _context.Count) { _cursor++; Current = _context[_cursor]; return Current; }
        if (Repeat == RepeatMode.Context && _context.Count > 0) { _cursor = 0; Current = _context[0]; return Current; }
        Current = null;
        return null;   // EndOfContext — the reducer decides whether to request autoplay
    }

    public Track? Prev()
    {
        if (_cursor - 1 >= 0) { _cursor--; Current = _context[_cursor]; }
        return Current;
    }

    public void SetRepeat(RepeatMode m) => Repeat = m;

    public void SetShuffle(bool on)
    {
        if (on == Shuffle) return;
        Shuffle = on;
        if (on) ReshuffleAnchoringCurrent();
        else RestoreOriginalOrder();
    }

    // Fisher–Yates over the NATURAL order, anchoring Current at logical 0 (deterministic LCG — no Random, replayable).
    void ReshuffleAnchoringCurrent()
    {
        if (_original.Count <= 1 || Current is null) return;
        var rest = _original.Where(t => !ReferenceEquals(t, Current)).ToList();
        for (int i = rest.Count - 1; i > 0; i--)
        {
            int j = NextSeed(i);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        _context.Clear();
        _context.Add(Current);
        _context.AddRange(rest);
        _cursor = 0;
    }

    // Turn shuffle OFF: restore the natural order and resume from Current's natural position.
    void RestoreOriginalOrder()
    {
        _context.Clear();
        _context.AddRange(_original);
        _cursor = Current is null ? -1 : _context.FindIndex(t => ReferenceEquals(t, Current));
        if (_cursor < 0 && _context.Count > 0) _cursor = 0;
    }

    int NextSeed(int maxInclusive)
    {
        _seedState = unchecked(_seedState * 1103515245 + 12345) & 0x7fffffff;
        return _seedState % (maxInclusive + 1);
    }

    public IReadOnlyList<QueueEntry> Snapshot()
    {
        var list = new List<QueueEntry>();
        if (Current != null) list.Add(new QueueEntry("now", Current, QueueBucket.NowPlaying, false));
        int i = 0;
        foreach (var t in _userQueue) list.Add(new QueueEntry($"q{i++}", t, QueueBucket.UserQueue, false));
        return list;
    }
}

// ── IAudioEngine — in-process decode/decrypt/output behind a swappable seam; STUBBED now (silent + stub decrypt) ───────
public interface IAudioEngine
{
    void Load(Track t);
    void Play();
    void Pause();
    void Stop();
    void Seek(long ms);
    long PositionMs { get; }
    bool IsPlaying { get; }
}

/// <summary>The one audio function the plan says to build now: a stub decrypt. Real engine = AES-128-CTR with the AudioKey;
/// here a passthrough so the pipeline shape exists and the reducer/projections are exercisable.</summary>
public static class StubCrypto
{
    public static ReadOnlySpan<byte> Decrypt(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> key) => encrypted;
}

public sealed class StubAudioEngine : IAudioEngine
{
    public Track? LoadedTrack { get; private set; }
    public string LastCmd { get; private set; } = "";
    public long PositionMs { get; private set; }
    public bool IsPlaying { get; private set; }

    public void Load(Track t) { LoadedTrack = t; PositionMs = 0; LastCmd = "load"; }
    public void Play() { IsPlaying = true; LastCmd = "play"; }
    public void Pause() { IsPlaying = false; LastCmd = "pause"; }
    public void Stop() { IsPlaying = false; LastCmd = "stop"; }
    public void Seek(long ms) { PositionMs = ms; LastCmd = "seek"; }
}

// ── Event log → projections ──────────────────────────────────────────────────────────────────────────────────────────
public enum EvKind { Started, Paused, Resumed, TrackChanged, Ended }
public readonly record struct PlaybackEvent(EvKind Kind, Track? Track, long AtMs);
public interface IPlaybackProjection { void OnEvent(in PlaybackEvent e); }

/// <summary>The reducer: intents drive the pure QueueCore + the IAudioEngine, and emit a PlaybackEvent log that
/// independent projections consume (history/gabo/PutState/now-playing). The seam IPlaybackState is one such projection.</summary>
public sealed class PlaybackReducer
{
    readonly QueueCore _queue = new();
    readonly IAudioEngine _audio;
    readonly List<IPlaybackProjection> _projections = new();

    public PlaybackReducer(IAudioEngine audio) => _audio = audio;

    public bool IsPlaying { get; private set; }
    public Track? Current => _queue.Current;
    public string? ContextUri => _queue.ContextUri;
    public IReadOnlyList<QueueEntry> Queue => _queue.Snapshot();

    public void Subscribe(IPlaybackProjection p) => _projections.Add(p);

    void Emit(EvKind k)
    {
        var e = new PlaybackEvent(k, _queue.Current, _audio.PositionMs);
        foreach (var p in _projections) p.OnEvent(in e);
    }

    public void Play(string ctxUri, IEnumerable<Track> tracks, int start = 0)
    {
        _queue.SetContext(ctxUri, tracks, start);
        if (_queue.Current != null) { _audio.Load(_queue.Current); _audio.Play(); IsPlaying = true; Emit(EvKind.Started); }
    }

    public void Pause() { _audio.Pause(); IsPlaying = false; Emit(EvKind.Paused); }
    public void Resume() { _audio.Play(); IsPlaying = true; Emit(EvKind.Resumed); }

    public void Next()
    {
        var t = _queue.Next();
        if (t != null) { _audio.Load(t); _audio.Play(); IsPlaying = true; Emit(EvKind.TrackChanged); }
        else { _audio.Stop(); IsPlaying = false; Emit(EvKind.Ended); }
    }

    public void EnqueueUser(Track t) => _queue.EnqueueUser(t);
    public void SetShuffle(bool on) => _queue.SetShuffle(on);
    public void SetRepeat(RepeatMode m) => _queue.SetRepeat(m);
}

/// <summary>A projection: the play-history counter (the real one appends play_history + feeds PlayCount/recently-played/gabo).</summary>
public sealed class HistoryProjection : IPlaybackProjection
{
    public int Plays { get; private set; }
    public Track? LastStarted { get; private set; }

    public void OnEvent(in PlaybackEvent e)
    {
        if (e.Kind is EvKind.Started or EvKind.TrackChanged)
        {
            Plays++;
            LastStarted = e.Track;
        }
    }
}
