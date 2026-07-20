using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The backend-agnostic queue orchestrator (spec §8 + the M3 cross-backend requirement). It ties the PlayQueue model to the
// active session + the VoiceScheduler preroll: at `ending-soon` it peeks the NEXT queue item, routes it via the MediaRouter
// to whichever backend owns it, and pre-rolls it there (audio → a PreparedSlot voice; video/DRM → the MF session spun up
// ahead of the boundary). Same-kind audio→audio joins commit INTO the shared mixer (crossfade / gapless) with no session
// swap; a cross-backend audio→video join is a clean declicked HARD CUT — the two engines never co-mix (spec §1). Deterministic:
// the caller pumps `Pump(frames)` off a synthetic clock and drains prepares with `DrainPrepareAsync()` — no wall clock.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Drives a <see cref="PlayQueue"/> across backends (spec §8.4 cross-backend). Owns the active <see cref="PcmAudioSession"/>
/// (when the current item is audio) + its <see cref="VoiceScheduler"/>, kicks the next item's backend prepare off the block
/// path, commits the join, and swaps engines on a cross-backend hard cut. Portable + deterministic (pump-driven).
/// </summary>
public sealed class QueuePlaybackCoordinator : IAsyncDisposable
{
    private readonly PlayQueue _queue;
    private readonly MediaRouter _router;
    private readonly MixFormat _format;
    private readonly MediaSignalSink _sink;
    private readonly IAudioEffects? _effects;
    private readonly int _latencyMarginFrames;
    private readonly CancellationTokenSource _cts = new();

    private IMediaSession? _current;
    private PcmAudioSession? _audio;
    private VoiceScheduler? _scheduler;

    private ValueTask<IPreparedItem>? _prepareTask;
    private uint _prepareEpoch;
    private bool _pendingHardCut;
    private bool _disposed;

    /// <summary>Create a coordinator over <paramref name="queue"/> resolving items through <paramref name="router"/> into the
    /// fixed <paramref name="format"/>, writing state to <paramref name="sink"/>. <paramref name="latencyMarginMs"/> is the
    /// worst-case decode+decrypt+seek preroll margin (spec §8.4).</summary>
    public QueuePlaybackCoordinator(PlayQueue queue, MediaRouter router, MixFormat format, MediaSignalSink sink,
        IAudioEffects? effects = null, int latencyMarginMs = 250)
    {
        _queue = queue;
        _router = router;
        _format = format;
        _sink = sink;
        _effects = effects;
        _latencyMarginFrames = Math.Max(0, latencyMarginMs) * format.SampleRate / 1000;
    }

    /// <summary>The active routed session (audio or, after a cross-backend hard cut, the swapped-in backend).</summary>
    public IMediaSession? Current => _current;
    /// <summary>The active audio session (null when a non-audio backend is current).</summary>
    public PcmAudioSession? AudioSession => _audio;
    /// <summary>The active voice scheduler (null when a non-audio backend is current).</summary>
    public VoiceScheduler? Scheduler => _scheduler;
    /// <summary>How many next-item prepares have been kicked (test hook — proves the hook fired BEFORE the boundary).</summary>
    public int PreparesInvoked { get; private set; }
    /// <summary>The most recent join outcome.</summary>
    public TransitionOutcome LastOutcome { get; private set; }
    /// <summary>The active audio sample clock (mixer-domain frame), or 0.</summary>
    public long SampleClock => _audio?.SampleClock ?? 0;

    /// <summary>Open the queue's current item as the active session and arm the scheduler for its join.</summary>
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        int idx = _queue.CurrentIndex.Peek();
        if (idx < 0 || idx >= _queue.Items.Count) return;
        await OpenAtAsync(idx, ct).ConfigureAwait(false);
    }

    private async ValueTask OpenAtAsync(int index, CancellationToken ct)
    {
        var item = _queue.Items[index];
        var kind = MediaKindSniffer.Sniff(item.Source);
        var backend = _router.Resolve(kind) ?? throw new NotSupportedException($"No backend registered for {kind}.");
        var opts = new MediaOpenOptions { StartPaused = true };
        var session = await backend.OpenAsync(item.Source, opts, ct).ConfigureAwait(false);
        session.ConnectSignals(_sink);

        _current = session;
        if (session is PcmAudioSession audio)
        {
            _audio = audio;
            if (_effects is not null) audio.BindEffects(_effects);
            var norm = audio.NormalizationMode;
            float refl = audio.ReferenceLufsValue;
            _scheduler = new VoiceScheduler(_format.SampleRate, _format.Channels,
                voiceChainFactory: audio.BuildVoiceChain);
            // Install the incoming crossfade voice through the session so it is ring-wrapped on the RT feed path (the worker
            // decodes ahead, the RT thread mixes copy-only) — RT-safe crossfade THROUGH the feed (spec §7.9/§8). On the
            // single-thread pull path AddCrossfadeVoice adds the source directly (identical to the bare mixer.AddVoice).
            _scheduler.SetVoiceInstaller(audio.AddCrossfadeVoice);
            ArmScheduler(index, primaryVoiceId: audio.PrimaryVoiceIdValue, startFrame: 0, lenFrames: audio.VoiceTotalFrames, norm, refl);
        }
        else
        {
            _audio = null;
            _scheduler = null;
        }
    }

    private void ArmScheduler(int fromIndex, long primaryVoiceId, long startFrame, long lenFrames, NormMode norm, float refLufs)
    {
        var transition = ResolveTransition(fromIndex);
        _scheduler!.BeginActive(primaryVoiceId, startFrame, lenFrames, transition, _latencyMarginFrames, norm, refLufs);
    }

    private ScheduledTransition ResolveTransition(int fromIndex)
    {
        var t = _queue.TransitionAfter(fromIndex);
        // A live effects CrossfadeMs overrides a default gapless with an equal-power crossfade (spec §7.10).
        if (_effects is not null && t.Kind == TransitionKind.Gapless)
        {
            float ms = _effects.CrossfadeMs.Peek();
            if (ms > 0f)
            {
                var curve = _effects.CrossfadeCurve.Peek() == CrossCurve.Linear ? Foundation.Easing.Linear : Foundation.Easing.EaseInOut;
                t = new ScheduledTransition(TransitionKind.Crossfade, TimeSpan.FromMilliseconds(ms), null, null, curve);
            }
        }
        return t;
    }

    /// <summary>Advance the active session one block and drive the scheduler: kick the next-item prepare when the preroll
    /// window opens, submit a completed prepare, and commit the join. Cross-backend joins set a pending hard-cut the caller
    /// resolves with <see cref="AdvanceIfNeededAsync"/>. Deterministic (pump-driven).</summary>
    public void Pump(int frames)
    {
        if (_disposed || _audio is null || _scheduler is null) return;
        _audio.PumpAudio(frames);
        long clock = _audio.SampleClock;

        // Submit a finished prepare (epoch-guarded — a stale one is dropped by the scheduler; the scar fix).
        if (_prepareTask is { IsCompleted: true } t)
        {
            _prepareTask = null;
            TrySubmit(t);
        }

        // Kick the next-item prepare exactly when the ending-soon window opens.
        if (_prepareTask is null && _scheduler.NeedsPrepare(clock))
        {
            var next = _queue.PeekNext();
            if (next is not null && _router.Resolve(MediaKindSniffer.Sniff(next.Source)) is IPreparableBackend prep)
            {
                _prepareEpoch = _scheduler.MarkPreparing();
                var ctx = PrepareContext.For(_format, _audio.NormalizationMode, _audio.ReferenceLufsValue);
                _prepareTask = prep.PrepareAsync(next.Source, ctx, _cts.Token);
                PreparesInvoked++;
            }
        }

        // Commit at the join.
        var outcome = _scheduler.Commit(clock, _audio.MixerRef);
        if (outcome != TransitionOutcome.None && outcome != LastOutcome) LastOutcome = outcome;
        if (outcome == TransitionOutcome.HardCut) _pendingHardCut = true;
    }

    private void TrySubmit(ValueTask<IPreparedItem> task)
    {
        try
        {
            var item = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            _scheduler!.SubmitPrepared(_prepareEpoch, item);
        }
        catch (OperationCanceledException) { /* invalidated preroll — dropped, never corrupts (spec §8.4) */ }
        catch { /* a failed prepare degrades the join; never crashes (spec §8.4) */ }
    }

    /// <summary>Await any in-flight next-item prepare and submit it under its epoch (deterministic test hook — no wall clock).
    /// A prepare invalidated by a Seek/queue-edit (epoch bumped) is dropped by the scheduler.</summary>
    public async ValueTask DrainPrepareAsync()
    {
        if (_prepareTask is not { } t) return;
        _prepareTask = null;
        try
        {
            var item = await t.ConfigureAwait(false);
            _scheduler?.SubmitPrepared(_prepareEpoch, item);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>Advance the queue model + swap engines when a cross-backend hard cut is pending (spec §8.4). Idempotent —
    /// a no-op when no hard cut is pending. After the swap <see cref="Current"/> is the next backend's session.</summary>
    public async ValueTask AdvanceIfNeededAsync(CancellationToken ct = default)
    {
        if (!_pendingHardCut || _disposed) return;
        _pendingHardCut = false;

        int next = _queue.NextTargetIndex();
        await _queue.NextAsync().ConfigureAwait(false);   // advance the model (Current/CurrentIndex)

        var old = _current;
        var oldAudio = _audio;
        if (next >= 0) await OpenAtAsync(next, ct).ConfigureAwait(false);

        if (old is not null && !ReferenceEquals(old, _current)) await old.DisposeAsync().ConfigureAwait(false);
        if (oldAudio is not null && !ReferenceEquals(oldAudio, _audio)) { /* old audio session disposed above */ }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_prepareTask is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        if (_current is not null) { try { await _current.DisposeAsync().ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
