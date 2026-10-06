using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// The PCM audio-graph backend (spec §7) — the <see cref="IMediaBackend"/> registered for <see cref="MediaKind.PcmAudio"/>
/// (Spotify/PlayPlay, local audio files routed to the graph, crossfade/EQ/gapless). <see cref="OpenAsync"/> builds the
/// decode-edge decorator stack (Resample(Decode([Decrypt](Fetch)))) into a single voice and wraps it in a
/// <see cref="PcmAudioSession"/> over the fixed internal mix format. The sink + clock are injected: the portable default
/// is the headless NULL sink + SYNTHETIC clock (so the whole graph runs with no device); the Windows leaf injects the
/// WASAPI <see cref="IAudioSink"/>/<see cref="IAudioClockSource"/>. M2 is single-thread-correct — the control thread
/// pumps; the M4 flip moves the pump onto the RT feed thread with no shape change.
/// </summary>
public sealed class PcmAudioPlayer : IMediaBackend, IPreparableBackend
{
    /// <summary>The fixed internal mix format (spec §7.1: f32 interleaved, device-rate, stereo).</summary>
    public MixFormat Format { get; }

    /// <inheritdoc/>
    public MediaKind Kind => MediaKind.PcmAudio;

    /// <summary>Preroll <paramref name="next"/> into a ready audio voice ahead of the join (spec §8.4). Runs OFF the block
    /// path (worker pool): opens the byte-source (a <see cref="DecryptingSource"/> in front is transparent), primes the
    /// decoder (header parsed, resampler armed), and resolves <see cref="GaplessInfo"/>. Cancellation (a Seek/queue-edit
    /// dropping the slot) completes without corrupting anything.</summary>
    public ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct)
        => PrepareCoreAsync(next, ctx, 0, forSeek: false, ct);

    /// <summary>Prepare an independently opened decoder at a requested position before filling its transferable ring.</summary>
    public ValueTask<IPreparedItem> PrepareAtAsync(MediaSource next, PrepareContext ctx, long positionFrames, CancellationToken ct)
        => PrepareCoreAsync(next, ctx, positionFrames, forSeek: true, ct);

    /// <summary>Prepare at a position on a decoder lease the CALLER already holds (<see cref="TryAcquireDecoderLease"/>) — the
    /// seek-swap path opens its second decoder only when a lease is free, so it must never queue behind the producer budget
    /// (V-PE13). Ownership of <paramref name="lease"/> transfers to the prepared voice's <see cref="DecoderAudioSource"/> chain
    /// on entry: it is released when that voice is disposed, and on EVERY failure or cancellation path inside this call.</summary>
    public ValueTask<IPreparedItem> PrepareAtAsync(MediaSource next, PrepareContext ctx, long positionFrames, IDisposable lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return PrepareCoreAsync(next, ctx, positionFrames, forSeek: true, ct, lease);
    }

    private async ValueTask<IPreparedItem> PrepareCoreAsync(MediaSource next, PrepareContext ctx, long positionFrames, bool forSeek,
        CancellationToken ct, IDisposable? providedLease = null)
    {
        // H-8: the session never proceeds without a lease. A caller-supplied lease skips the wait entirely.
        IDisposable lease = providedLease ?? await AcquireDecoderLeaseAsync(ct).ConfigureAwait(false);
        IMediaByteSource? byteSource = null;
        IAudioDecoder? decoder = null;
        IAudioSource? voice = null;
        RingAudioSource? ring = null;
        try
        {
            byteSource = ResolveByteSource(next)
                ?? throw new NotSupportedException("PCM preparation requires a supported byte source.");
            using var cancellation = ct.Register(static state => ((IMediaByteSource)state!).Cancel(), byteSource);
            decoder = _decoderFactory(ctx.Format);
            DecodedInfo info = default;
            bool opened = await Task.Run(() =>
            {
                bool result = decoder.TryOpen(byteSource, ctx.Format, out var decoded);
                info = decoded;
                return result;
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!opened) throw new InvalidOperationException("The prepared source could not be decoded.");
            voice = BuildTrimmedVoice(decoder, info.Loudness, ctx.Format, info.Duration, out long totalFrames, byteSource, lease);
            long achieved = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (positionFrames > 0)
                {
                    if (voice is DecoderAudioSource direct) direct.SeekFrame(positionFrames);
                    else if (voice is TrimmingSource trimmed) trimmed.SeekFrame(positionFrames);
                    else throw new NotSupportedException("The prepared decoder cannot seek.");
                }
                return voice.PositionFrames;
            }, ct).ConfigureAwait(false);
            // D8 (V-PE5): the SAME time-domain sizing as the live voice — a prepared / seek voice holds the identical cushion and
            // keeps the identical kept-behind span, so a backward seek after a swap is as instant as one on the original voice.
            int rate = ctx.Format.SampleRate;
            ring = new RingAudioSource(voice, ctx.Format.Channels, _sizing.RingFrames(rate), _sizing.AheadFrames(rate),
                _sizing.BlockFrames(rate) * 2, _sizing.KeepBehindFrames(rate), startFrames: achieved, rt: _rt);
            ring.StartProducer();
            // A seek voice only has to supply ONE block before it can replace the playing voice (V-PE14); the next-track
            // prepare waits for 500 ms. Never more than the ring will ever hold ahead.
            int readyFrames = Math.Min(forSeek ? Math.Max(1, rate / 100) : Math.Max(1, rate / 2), ring.TargetFrames);
            await ring.WaitUntilReadyAsync(readyFrames, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new AudioPreparedItem(ring, decoder.Gapless, info.Loudness, totalFrames, info.Duration,
                rate, achieved, readyFrames);
        }
        catch
        {
            if (ring is not null) ring.Dispose();
            else if (voice is IDisposable owned) owned.Dispose();
            else
            {
                try { (decoder as IDisposable)?.Dispose(); }
                finally { try { byteSource?.Close(); } finally { lease.Dispose(); } }
            }
            throw;
        }
    }

    // Three live decoder leases cover outgoing, incoming and one prepared/seek replacement voice.
    // A cancelled but uncooperative decoder retains its lease until it actually stops, bounding stuck work.
    private readonly SemaphoreSlim _decoderSlots = new(3, 3);
    /// <summary>Live or not-yet-quiescent decoder producers, bounded to three per backend.</summary>
    public int LiveDecoderCount => 3 - _decoderSlots.CurrentCount;
    private sealed class DecoderLease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }

    /// <summary>Take a decoder lease WITHOUT waiting (V-PE13): the seek-swap path opens a second decoder only when one is free
    /// and otherwise falls back to the in-place seek, so it must never queue behind the three-producer budget. Dispose the lease
    /// to release it, or hand it to <see cref="PrepareAtAsync(MediaSource, PrepareContext, long, IDisposable, CancellationToken)"/>,
    /// which takes ownership. <paramref name="lease"/> is non-null only when this returns true.</summary>
    public bool TryAcquireDecoderLease(out IDisposable lease)
    {
        if (_decoderSlots.Wait(0))
        {
            lease = new DecoderLease(_decoderSlots);
            return true;
        }
        lease = null!;
        return false;
    }

    // H-8: a session never proceeds without a lease. The wait is generous (a retiring producer may be blocked in a slow network
    // read until its cancellation lands) and a timeout is a TYPED failure naming the producers that are still alive, never a
    // silent over-subscription of the decoder budget.
    private async ValueTask<IDisposable> AcquireDecoderLeaseAsync(CancellationToken ct)
    {
        if (!await _decoderSlots.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Audio decoder producers did not retire: {DescribeProducers()}");
        return new DecoderLease(_decoderSlots);
    }

    // The most recent session this backend opened (weak: a retired session must stay collectable): its ring table is the only
    // place a stuck producer is visible, so the H-8 error lists it.
    private WeakReference<PcmAudioSession>? _lastSession;

    private string DescribeProducers()
    {
        var sb = new System.Text.StringBuilder("live=").Append(LiveDecoderCount).Append("/3");
        if (_lastSession is { } weak && weak.TryGetTarget(out var session)) session.DescribeProducers(sb);
        return sb.ToString();
    }

    private readonly Func<MixFormat, IAudioEndpoint> _endpointFactory;
    private readonly Func<MixFormat, IAudioDecoder> _decoderFactory;
    private readonly IAudioEffects? _effects;
    private readonly int _maxBlock;
    private readonly bool _driveWithOwnThread;
    private readonly Action<PcmAudioSession>? _onSessionCreated;   // M4: attach the RT feed + device controller (on-box)
    private readonly IRtThreadCharacteristics? _rt;                // ONE instance for the whole backend: handed to every ring this backend builds
    private readonly RingSizing _sizing;                           // D8: the time-domain ring sizing the feed AND every prepared/seek ring share

    /// <summary>The thread-characteristics seam this backend was built with (the same instance its rings register their
    /// producer threads through), or null on a headless backend. The scrub/seek paths build their own rings with it.</summary>
    public IRtThreadCharacteristics? ThreadCharacteristics => _rt;

    /// <summary>Create a PCM backend. When <paramref name="endpointFactory"/> is omitted the HEADLESS endpoint (null sink +
    /// synthetic clock) is used (deterministic, no device). <paramref name="effects"/> supplies the live
    /// EQ/normalization/volume signals and the visualizer demand: every session <see cref="OpenAsync"/> builds is BOUND to it
    /// (<see cref="PcmAudioSession.BindEffects"/>); <paramref name="driveWithOwnThread"/> starts a single control-thread feeder (for a
    /// real device — NOT the M4 MMCSS RT thread). <paramref name="decoderFactory"/> injects the decode-edge codec (spec §5.5
    /// <see cref="IAudioDecoder"/>): the DEFAULT is the built-in <see cref="WavAudioDecoder"/>; the app supplies a
    /// Vorbis/FLAC/MP3 factory to route real streaming content through the same graph. <paramref name="rt"/> registers the
    /// producer threads of every prepared ring (null ⇒ no registration); <paramref name="ringSizing"/> is the time-domain
    /// sizing of every prepared/seek ring (null ⇒ <see cref="RingSizing.Default"/>) — pass the same value the feed was built with.</summary>
    public PcmAudioPlayer(
        MixFormat? format = null,
        Func<MixFormat, IAudioEndpoint>? endpointFactory = null,
        IAudioEffects? effects = null,
        int maxBlock = 1024,
        bool driveWithOwnThread = false,
        Action<PcmAudioSession>? onSessionCreated = null,
        Func<MixFormat, IAudioDecoder>? decoderFactory = null,
        IRtThreadCharacteristics? rt = null,
        RingSizing? ringSizing = null)
    {
        Format = format ?? new MixFormat(48000, 2);
        _endpointFactory = endpointFactory ?? (fmt => new HeadlessAudioEndpoint(fmt));
        _decoderFactory = decoderFactory ?? (static _ => new WavAudioDecoder());
        _effects = effects;
        _maxBlock = Math.Max(64, maxBlock);
        _driveWithOwnThread = driveWithOwnThread;
        _onSessionCreated = onSessionCreated;
        _rt = rt;
        _sizing = ringSizing ?? RingSizing.Default;
    }

    /// <summary>The decode-edge total-frame count in the fixed mix domain. The built-in WAV decoder reports it exactly; a
    /// pluggable streaming decoder (unknown byte length) derives it from the declared duration so join-arming/duration hold.</summary>
    private static long MixFrames(IAudioDecoder decoder, TimeSpan duration, MixFormat fmt)
        => decoder is WavAudioDecoder wav ? wav.MixFramesTotal
           : duration > TimeSpan.Zero ? (long)Math.Round(duration.TotalSeconds * fmt.SampleRate) : 0;

    /// <summary>THE one gapless-trim seam (spec §8.3), shared by <see cref="OpenAsync"/> and <see cref="PrepareAsync"/> so
    /// every codec's encoder delay / end padding is applied identically: when the decoder reported a non-trivial
    /// <see cref="GaplessInfo"/> (mix-domain frames — the decoder converts from the source rate), the voice is wrapped in a
    /// <see cref="TrimmingSource"/> and <paramref name="totalFrames"/> becomes the TRIMMED length (what join-arming and the
    /// duration clamp must see). A trivial info (no trim, no exact length) keeps the bare voice — the golden-PCM paths stay
    /// byte-identical.</summary>
    private static IAudioSource BuildTrimmedVoice(IAudioDecoder decoder, ReplayGainInfo loudness, MixFormat fmt,
        TimeSpan duration, out long totalFrames, IMediaByteSource? byteSource = null, IDisposable? lifetime = null)
    {
        var voice = new DecoderAudioSource(decoder, loudness, byteSource, lifetime);
        totalFrames = MixFrames(decoder, duration, fmt);
        var g = decoder.Gapless;
        if (g.LeadInFrames <= 0 && g.TrailPadFrames <= 0 && g.ExactFrames < 0) return voice;

        var trimmed = new TrimmingSource(voice, g, fmt.Channels, totalFrames > 0 ? totalFrames : -1);
        totalFrames = g.ExactFrames >= 0 ? g.ExactFrames
            : totalFrames > 0 ? Math.Max(0, totalFrames - Math.Max(0, g.LeadInFrames) - Math.Max(0, g.TrailPadFrames))
            : totalFrames;
        return trimmed;
    }

    /// <inheritdoc/>
    public MediaCapabilities Capabilities { get; } = new(SupportsVideo: false, SupportsAudioGraph: true, SupportsDrm: false)
    {
        IsSupported = static ct => ct.Audio is CodecId.None or CodecId.Pcm or CodecId.Vorbis or CodecId.Aac
                                        or CodecId.Opus or CodecId.Flac or CodecId.Mp3,
    };

    /// <inheritdoc/>
    public async ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        IDisposable lease = await AcquireDecoderLeaseAsync(ct).ConfigureAwait(false);   // H-8
        IMediaByteSource? byteSource = null;
        IAudioEndpoint? endpoint = null;
        IAudioDecoder? decoder = null;
        IAudioSource? voice = null;
        PcmAudioSession? session = null;
        try
        {
            byteSource = ResolveByteSource(source)
                ?? throw new NotSupportedException("PCM playback requires a supported byte source.");
            using var cancellation = ct.Register(static state => ((IMediaByteSource)state!).Cancel(), byteSource);
            endpoint = _endpointFactory(Format);
            var mix = endpoint.Sink.Format;
            decoder = _decoderFactory(mix);
            DecodedInfo info = default;
            bool opened = await Task.Run(() =>
            {
                bool result = decoder.TryOpen(byteSource, mix, out var decoded);
                info = decoded;
                return result;
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!opened) throw new InvalidOperationException("The audio source could not be decoded.");
            var loudness = ResolveLoudness(source, info);
            voice = BuildTrimmedVoice(decoder, loudness, mix, info.Duration, out long totalFrames, byteSource, lease);
            session = new PcmAudioSession(mix, endpoint.Sink, endpoint.Clock, _maxBlock, _driveWithOwnThread, endpoint);
            _lastSession = new WeakReference<PcmAudioSession>(session);
            session.Configure(BuildGraphSpec(_effects, mix));
            // The backend's effects surface is the LIVE one for every session it opens. Without this bind the session's live
            // reconcile (EQ / balance / normalization edits), its level tap and its spectrum ring never run: a lease taken on the
            // surface rotates an epoch nothing renders against, so every level meter and live-FFT face rests (Wavee #166, real
            // tracks only — the app's silent `--fake` session binds itself). Bound HERE: before the RT feed attaches (no race on
            // `_liveEffects`) and before the voice exists, so the voice seeds from the bound EQ design (E-4 preamp included).
            if (_effects is not null) session.BindEffects(_effects);
            _onSessionCreated?.Invoke(session);
            var (norm, refLufs) = ResolveNorm(_effects, opts);
            session.SetVoice(voice, info.Duration, totalFrames, norm, refLufs, initialVolume: 1f);
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else endpoint?.Dispose();
            if (voice is IDisposable owned) owned.Dispose();
            else
            {
                try { (decoder as IDisposable)?.Dispose(); }
                finally { try { byteSource?.Close(); } finally { lease.Dispose(); } }
            }
            throw;
        }
    }

    /// <summary>Map a <see cref="MediaSource"/> to a portable byte source (a <see cref="DecryptingSource"/> in a
    /// <see cref="PullSource"/> is honored transparently).</summary>
    internal static IMediaByteSource? ResolveByteSource(MediaSource source) => source switch
    {
        FileSource f => new FileByteSource(f.Path),
        StreamSource s => new StreamByteSource(s.Stream),
        BytesSource b => new BytesByteSource(b.Bytes),
        PullSource p => p.Source,
        ClipSource c => ResolveByteSource(c.Inner),
        _ => null,
    };

    private static ReplayGainInfo ResolveLoudness(MediaSource source, DecodedInfo info)
    {
        // WAV carries no ReplayGain tags; the decoder reports default. A future tagged decoder fills info.Loudness.
        return info.Loudness;
    }

    private static (NormMode, float) ResolveNorm(IAudioEffects? effects, MediaOpenOptions opts)
    {
        if (effects is null) return (NormMode.Album, -14f);
        return (effects.Normalization.Peek(), effects.ReferenceLufs.Peek());
    }

    /// <summary>Translate the live <see cref="IAudioEffects"/> into a compiled <see cref="AudioGraphSpec"/> (spec §7.4):
    /// per-voice EQ (a fading track keeps its own curve), master balance, terminal limiter. Null effects ⇒ passthrough.</summary>
    public static AudioGraphSpec BuildGraphSpec(IAudioEffects? effects, MixFormat format)
    {
        if (effects is null) return AudioGraphSpec.Passthrough;

        var perVoice = ImmutableArray<EffectSpec>.Empty;
        var eq = effects.Equalizer;
        if (eq.Enabled.Peek() && eq.Bands.Length > 0)
        {
            var bands = ImmutableArray.CreateBuilder<BiquadBand>(eq.Bands.Length);
            foreach (var band in eq.Bands)
                bands.Add(new BiquadBand(band.Type, band.FreqHz.Peek(), band.Q.Peek(), band.GainDb.Peek()));
            perVoice = ImmutableArray.Create<EffectSpec>(new EqSpec(bands.ToImmutable()));
        }

        // Balance is applied by the session-owned smoothed ChannelStage (spec §7.10), NOT baked into the published graph —
        // so a balance tweak ramps without a topology republish. The master chain here carries only post-mix EQ (none in M3).
        return new AudioGraphSpec(perVoice, ImmutableArray<EffectSpec>.Empty, LimiterSpec.Default);
    }
}

/// <summary>
/// A live PCM audio-graph session (spec §7): the 5-stage pull graph
/// (voice → per-voice DSP → <see cref="CrossfadeMixer"/> → master DSP → <see cref="IAudioSink"/>) driven single-thread by
/// <see cref="PumpAudio"/>. The device <see cref="IAudioClockSource"/> is the only clock: <see cref="AudioClockPosition"/>
/// derives <c>Position</c> off it (latency-compensated, <c>IsValid</c>-gated). Transport is idempotent and accepted
/// SYNCHRONOUSLY (never blocks/deadlocks; the pump realizes the state) — mirroring the M0/M1 fix. <see cref="RenderBlock"/>
/// is the pure, alloc-free "pull one block through the full graph" op the golden-PCM + zero-alloc gates drive.
/// </summary>
public sealed partial class PcmAudioSession : IMediaSession
{
    private static readonly double s_qpcTo100ns = 1e7 / Stopwatch.Frequency;

    // Not readonly (spec §7.9 Fix 2): RebuildSink stamps the new endpoint's negotiated rate here BEFORE raising
    // DeviceFormatChanged, so `Format` — read by the host's soft reload via PrepareContext.For(session.Format) — already
    // reports the live rate instead of the one this session was constructed with. The internal graph (mixer/EQ/rings)
    // stays frozen at the old rate until the reload swaps in a brand-new session; only the externally-observable
    // negotiated rate changes here, on the cold device thread, ahead of the fire-and-forget reload signal.
    private MixFormat _format;
    private IAudioSink _out;                    // swapped on a device rebuild (spec §7.9) — sources/voices/position survive
    private IAudioClockSource _clock;           // swapped with the sink (same endpoint); latency is re-measured off it
    private readonly int _maxBlock;
    private readonly bool _driveWithOwnThread;
    private IDisposable? _endpoint;
    private AudioFeedThread? _feed;             // the M4 RT feed (null on the single-thread pull path)

    private readonly AudioGraphHost _graph;
    private readonly CrossfadeMixer _mixer;
    private readonly AudioClockPosition _position = new();
    private readonly ParamPlane _plane = new();
    private readonly GainStage _masterGain = new(1f);
    private readonly ChannelStage _masterChannel = new(0f, false);   // balance (spec §7.10) — smoothed, no republish
    private readonly float[] _mixBuf;

    private const long PrimaryVoiceId = 1;

    // ── mixer-command SPSC (spec §7.9/§12): the CrossfadeMixer voice list is RENDER-thread-owned, so all control-side
    // mutations (SetVoice / AddCrossfadeVoice / SetVoiceEnvelope) enqueue here and are applied at RenderBlock's top on the
    // thread that renders. A producer-side lock serializes the two control producers (the Enqueue chain + the crossfade
    // Timer tick are not mutually serialized); the consumer is lock-free. On a session with NO feed attached the command is
    // drained INLINE right after enqueue, so the single-thread pull path keeps byte-identical golden-PCM/test semantics.
    //
    // Kinds 10–17 (H-4 and the seek/scrub work): CmdSetEq / CmdSetVoiceGain / CmdFadeOutHold / CmdJumpWithinRing / CmdSwapVoice /
    // CmdSilenceVoice are the seek commands (D3); CmdHoldVoice / CmdReleaseVoice are the scrub commands (D4): the numbering is the one
    // fixed contract.
    private struct MixerCmd
    {
        public long Position; public byte Kind; public long Id; public MixVoice Voice; public GainEnvelope? Env; public long Sequence; public int Frames;
        /// <summary>A second per-command envelope shell, for the one command that touches two voices at once: CmdReleaseVoice carries the
        /// MAIN voice's fade-in in <see cref="Env"/> and the GRAIN voice's fade-out here. Built on the control side, stamped on the RT.</summary>
        public GainEnvelope? Env2;
        /// <summary>CmdSetEq / CmdSetVoiceGain: the voice's FINAL gain-slot target — its EQ preamp × its normalization delta, folded
        /// on the CONTROL side (under <see cref="_mixerCmdProducerLock"/>, so command order == compute order) which keeps the RT
        /// arm stateless. <see cref="Frames"/> is the ramp length in samples (0 = immediate).</summary>
        public float Linear;
        /// <summary>CmdSetEq: the target band set and its coefficients, designed OFF the RT thread by <see cref="EqStage.Design"/>.</summary>
        public BiquadCoeffs[]? Coeffs; public BiquadBand[]? Bands;
    }
    private const byte CmdReplacePrimary = 1, CmdAddVoice = 2, CmdSetEnvelope = 3, CmdRemoveVoice = 4, CmdFadeOut = 5, CmdFadeIn = 6, CmdReset = 7, CmdSeekAnchor = 8, CmdResetRate = 9,
        CmdSetEq = 10, CmdSetVoiceGain = 11, CmdJumpWithinRing = 12, CmdSwapVoice = 13, CmdHoldVoice = 14, CmdReleaseVoice = 15, CmdFadeOutHold = 16,
        CmdSilenceVoice = 17;
    private readonly MixerCmd[] _mixerCmdQ = new MixerCmd[64];
    private int _mixerCmdHead, _mixerCmdTail;                 // Volatile head/tail; consumer = whichever thread runs RenderBlock
    private readonly object _mixerCmdProducerLock = new();
    private long _commandSequence, _appliedSequence;
    private long _submittedFrames, _playedFrames, _renderEpoch;
    private long _deviceFrameOrigin;
    private long _rawPlayedFrames;                            // the device's own played count at the last clock sample (X2) — silence INCLUDED
    private readonly SessionAudioClock _presentationClock;

    // ── F2 starvation (never stop the device on a dry ring) ─────────────────────────────────────────────────────────────
    // An empty active ring makes the RT submit SILENCE for the block (inside the tripwire) and hold the content timeline: the mixer
    // is not rendered, so ConsumeSeq / the transport ramp / the spectrum tap do not move. The device keeps running — it is never
    // Stop()ped or Reset() on a starve. The clock thread reads _starvationPhase to mirror Stalled/Playing for the UI.
    private volatile int _starvationPhase;     // RT-owned: 0 healthy, 1 starved (silence flowing)
    private RingAudioSource? _starvedRing;     // RT-owned: the ring being waited on (severity accrues on it)
    private volatile int _resumeFrames;        // the cushion a starved ring must reach before the transport resumes
    private long _lastIncidentTick;            // Volatile: TickClockMs at the last resume — the cushion decays 30 s after it
    private const long ResumeDecayMs = 30_000;
    private volatile bool _pendingSilence;     // RT-owned: the retained block is SILENCE (it is not content in the ConsumeSeq domain)

    /// <summary>While starved, silence is topped up only to this many render blocks of device padding (~20 ms at the 10 ms block)
    /// — NOT to the device buffer's usual ~100 ms depth — so real audio returning after a long absence reaches the speaker about
    /// 20 ms after the resume instead of queueing behind a full buffer of silence. A device that underruns over silence is
    /// inaudible; whatever real audio the device still holds when the ring runs dry plays out first. Only silence actually
    /// submitted is recorded in the ledger. A device whose whole buffer is no deeper than this floor is topped up as before.</summary>
    public const int SilencePaddingFloorBlocks = 2;

    /// <summary>Millisecond clock the resume-cushion decay reads. A seam for deterministic tests only.</summary>
    internal Func<long> TickClockMs { get; set; } = static () => Environment.TickCount64;
    /// <summary>The current resume cushion in frames (diagnostics / tests).</summary>
    internal int ResumeFrames => _resumeFrames;
    /// <summary>True while the RT is submitting silence for a dry ring (diagnostics / tests).</summary>
    internal bool IsStarved => _starvationPhase != 0;
    /// <summary>The output-owned transport phase: 0 running, 1 fading, 2 draining, 3 held, 4 held-running at gain 0 (diagnostics / tests).</summary>
    internal int TransportPhase => Volatile.Read(ref _transportPhase);

    // The silence-span LEDGER (X2, V-PE6). Silence the RT submitted during starvation is recorded by device submit index; the
    // presentation clock subtracts the spans the device has already played, so PlayedFrames stays a CONTENT clock while the raw
    // device count keeps running. 64 spans; the RT writes entries + the tail, the control side (under _silenceLock) reads them and
    // advances the head. A full ledger coalesces a contiguous span into the newest one.
    private struct SilenceSpan { public long SubmitIndex; public int Frames; }
    private readonly SilenceSpan[] _silence = new SilenceSpan[64];
    private int _silenceHead, _silenceTail;
    private readonly object _silenceLock = new();   // CONTROL side only — the RT never takes it
    private long _silencePlayedCache;               // control: frames of silence in spans already dropped from the ledger (fully played)
    private int _silenceEpoch, _silenceEpochSeen;   // _silenceEpoch bumps on a ledger reset (CmdReset / RebuildSink); the control cache follows it
    // CONTROL (under _silenceLock): the spans the clock sampler dropped, contiguous runs coalesced, each with the silence before it. The
    // cache alone counts EVERY dropped span — also those after an index the device has since played past (a rebase anchor read after the
    // silence that followed it was played) — so such an index resolves from here. A ring of 64 separate silences; reset with the epoch.
    private struct PlayedSilence { public long SubmitIndex, Frames, Before; }
    private readonly PlayedSilence[] _silenceHistory = new PlayedSilence[64];
    private int _silenceHistoryCount, _silenceHistoryNewest;

    private sealed class SessionAudioClock(PcmAudioSession session) : IAudioClockSource
    {
        public long WrittenFrames => session.SubmittedFrames;
        public long StreamLatencyFrames => session._clock.StreamLatencyFrames;
        public int MixRate => session._clock.MixRate;
        /// <summary>The CONTENT clock: the device's played count minus the silence it has already played. The raw count is kept in
        /// <see cref="_rawPlayedFrames"/> for padding / delay arithmetic. Control tick thread only (it advances the ledger head).</summary>
        public bool TryGetPlayed(out long frames, out long qpc)
        {
            bool valid = session._clock.TryGetPlayed(out long deviceFrames, out qpc);
            long raw = deviceFrames + Interlocked.Read(ref session._deviceFrameOrigin);
            Interlocked.Exchange(ref session._rawPlayedFrames, raw);
            frames = Math.Clamp(raw - session.SilenceBefore(raw, consumePlayed: true), 0, session.SubmittedFrames);
            return valid;
        }
    }
    private int _pendingFrames, _pendingOffset;
    private TransportRamp _transport = new(1f);
    private int _transportPhase; // output-owned: 0 running, 1 fading, 2 draining, 3 held, 4 held-running at gain 0 (the seek hold: renders silence, never stops the device)
    private bool _holdNoStop;    // RT-owned: the fade in flight is a seek hold — its completion is phase 4, not the stop-and-drain of phase 2
    private long _fadeTailSubmitted;
    private int _startRequested;
    private volatile bool _transportHoldRequested;
    private volatile bool _fadeInSpecified;
    private long _transportRevision;
    private long _seekRevision;
    private readonly SemaphoreSlim _replacementGate = new(1, 1);

    // ── seek (D3): the jump inside the ring (B), the voice swap (A) and the in-place seek (fallback) ───────────────────────
    // Engine-issued voice ids start far above both the app's own counter and VoiceScheduler's (1_000_000 + n), so a swap / scrub voice
    // can never collide with a voice id either of them hands out for the same session.
    private const long FirstEngineVoiceId = 1L << 32;
    private long _nextEngineVoiceId = FirstEngineVoiceId - 1;
    private readonly int _fade5Frames;                        // the 5 ms equal-power pair every seek transition uses, at the construction rate
    private readonly float[] _jumpTail, _jumpScratch;         // RT: the old position's next 5 ms (the blend's tail) and its scratch — preallocated
    private int _jumpVerdict;                                 // RT writes (1 accepted / 2 refused), control reads after the command is applied
    private long _jumpSubmitIndex, _swapAtFrame, _swapSubmitIndex;   // RT writes: the device submit index / mixer frame the change becomes audible at
    private int _swapLanded;                                  // 1 once this seek's design-A swap has landed; cleared when the next seek begins
    private long _lastSeekGeneration;                         // the generation the last swap carried (diagnostics)
    // scrub (D4): the hold / release commands answer through these (RT writes, the control side reads once the command is applied)
    private readonly int _scrubFadeFrames;                    // the 20 ms fade of the scrub hold, the grain voice's fade-out and the cancel's fade-in, at the construction rate
    private int _scrubVerdict;                                // 1 accepted / 2 refused (nothing moved) — CmdHoldVoice and CmdReleaseVoice
    private long _releaseSubmitIndex, _releaseFrame;          // CmdReleaseVoice (fade-in): the device submit index the main voice returns at, and the content frame it resumes from
    private readonly System.Collections.Generic.Dictionary<(FadeKind, int), GainEnvelope> _fadeTemplates = new();   // built-once LUT templates; guarded by itself
    private const int FadeHoldWaitMs = 1000;                  // an in-place seek never waits longer than this for a hold fade the RT is not rendering
    private volatile bool _formatRequiresReload;
    private bool _reloadSuppressionLogged;   // one line per rate change when RenderBlock is parked on _formatRequiresReload
    private long _activeMixerStart;
    private readonly System.Collections.Generic.Dictionary<long, long> _voiceStarts = new();
    /// <summary>Output format changed; the graph must be recreated before new-rate PCM may be submitted.</summary>
    public bool RequiresGraphRebuild => _formatRequiresReload;
    /// <summary>Raised after endpoint replacement with the captured source-domain played position.</summary>
    public event Action<MixFormat, long>? DeviceRebuilt;

    /// <summary>Frames accepted by the current endpoint epoch — content AND the silence F2 submitted while the ring was dry.</summary>
    public long SubmittedFrames => Interlocked.Read(ref _submittedFrames);
    /// <summary>The CONTENT clock, sampled off RT and bounded by submitted PCM: the device's played count minus the silence it has
    /// already played (F2 starvation). It holds while the ring is dry, so every consumer of the played position (the position
    /// projection, the spectrum window, queue timing) keeps measuring audio the listener actually heard.</summary>
    public long PlayedFrames => Interlocked.Read(ref _playedFrames);
    /// <summary>The device's own played count at the last clock sample, in the same submit-index domain as
    /// <see cref="SubmittedFrames"/>: it keeps running through starvation silence (X2). Padding and output-delay arithmetic use it.</summary>
    public long RawPlayedFrames => Interlocked.Read(ref _rawPlayedFrames);
    /// <summary>What the current feed keeps decoded ahead of the RT thread, in frames at the live rate; 0 when there is no RT feed.</summary>
    public int TargetAheadFrames => _feed?.TargetAheadFrames ?? 0;
    /// <summary>Changes whenever output buffers are reset or replaced.</summary>
    public long RenderEpoch => Interlocked.Read(ref _renderEpoch);
    /// <summary>Available PCM for the active voice.</summary>
    public int BufferedFrames
    {
        get
        {
            if (_feed is null) return _voice is RingAudioSource r ? r.BufferedFrames : 0;
            foreach (var entry in _feed.RingsSnapshot)
                if (entry.VoiceId == ActiveVoiceIdValue) return entry.Ring.BufferedFrames;
            return 0;
        }
    }

    // ── live effects (spec §7.10): the control-thread reconcile that drives the M2 graph ─────────────────────────────
    private IAudioEffects? _liveEffects;
    private EqStage? _voiceEq;            // the primary voice's EQ stage (a handle for tests/diagnostics — every change reaches it via CmdSetEq)
    private GainStage? _voiceGainStage;   // the primary voice's gain slot (preamp × normalization delta)
    private long _eqTopologySig = long.MinValue;   // last-applied EQ topology (enabled/count/type/freq/Q) — NOT gain
    private float[] _lastBandGains = Array.Empty<float>();
    private volatile bool _eqDirty;       // a CmdSetEq could not be admitted (queue full): the next tick re-sends the freshest design
    private bool _rgDirty;                // a CmdSetVoiceGain from a normalization change could not be admitted: the next tick retries
    private volatile float _balanceTarget;   // control writes, the RT applies it to the master ChannelStage at each block start

    /// <summary>One EQ design, built OFF the RT thread and published as an immutable value: the band set, its coefficients and the
    /// preamp that keeps the boosted cascade out of the limiter (E-4: −max(0, largest band gain) dB). New voices seed from it.</summary>
    private sealed record EqDesign(BiquadBand[] Bands, BiquadCoeffs[] Coeffs, float Preamp)
    {
        public static readonly EqDesign Identity = new(Array.Empty<BiquadBand>(), Array.Empty<BiquadCoeffs>(), 1f);
    }
    private EqDesign? _eqDesign;          // Volatile; null until effects are bound (then voices build from the published graph spec alone)

    /// <summary>What the control side knows of one voice's gain slot: the preamp last sent for its EQ, the normalization delta last
    /// requested (D5: factor_now / factor_baked), and the scalar its decoder/mixer baked at add time. The slot target is always
    /// <c>Preamp × Delta</c>. Guarded by <see cref="_mixerCmdProducerLock"/>.</summary>
    private sealed class VoiceGainTrack
    {
        public float Preamp = 1f, Delta = 1f, BakedRg = 1f;
        public ReplayGainInfo Loudness;
    }
    private readonly System.Collections.Generic.Dictionary<long, VoiceGainTrack> _voiceGain = new();

    // ── visualizer tap (spec §7.3/§7.8): a post-master level/peak snapshot published off the block path ──────────────
    private AudioLevelMailbox _tap;
    private long _tapReadVersion, _visualizerSource, _meterSamples;
    /// <summary>Actual samples analyzed for visible level consumers; no demand means no scans.</summary>
    public long MeterSamples => Interlocked.Read(ref _meterSamples);

    // ── spectrum tap (spec §7.8): a PRE-master-gain mono ring the RT fills only under a SPECTRUM lease; the control tick
    //    analyses the latency-aligned window and publishes bands through AudioEffects (never from the RT path) ──────────
    private SpectrumRing? _spectrumRing;                 // created by the control thread on the first lease; the RT reads it with Volatile
    private long _spectrumEpochSeen, _spectrumRenderEpochSeen, _spectrumNextStart;   // RT-only: the re-arm triggers (demand edge, device rebuild, a block discontinuity)
    private bool _spectrumArmed;                         // RT-only
    private SpectrumAnalyzer? _spectrumAnalyzer;         // control-thread only
    private float[]? _spectrumWindow, _spectrumBands;    // control-thread only
    private OnsetDetector? _onsets;                       // control-thread only; created with the analyzer
    private long _spectrumPublishes;
    private const float SpectrumRingSeconds = 1.5f;      // rounded up to a power of two by the ring (131 072 at 48 kHz = 2.7 s): device latency + a ±500 ms offset + the RT burst
    /// <summary>Spectrum windows analysed and published (diagnostics).</summary>
    public long SpectrumPublishes => Interlocked.Read(ref _spectrumPublishes);
    /// <summary>DIAGNOSTIC ONLY (spec §7.8 AS-BUILT): frames queued between the mixer's newest submitted sample and the
    /// one the listener hears — submitted − RAW played + the endpoint's measured latency (X2: both counts include starvation
    /// silence, so the difference is what is really queued in the device). The spectrum window is NOT aligned by this; it is
    /// aligned in the content domain from <see cref="PlayedFrames"/> (<c>PublishSpectrum</c>).</summary>
    public long OutputDelayFrames => Math.Max(0L, SubmittedFrames - RawPlayedFrames + _clock.StreamLatencyFrames);

    // R-11: every field the RT thread reads and a control thread writes is volatile (or reached through Volatile/Interlocked).
    private volatile MediaSignalSink? _sink;
    private volatile PlaybackState _state = PlaybackState.Idle;
    private volatile bool _playRequested;
    private bool _metaPublished;
    private volatile bool _started;
    private volatile bool _disposed;


    // ── hiccup-hardening fixes (adjacent to the M4 decode-ahead-ring fix; see the class remarks) ─────────────────────────
    // Fix 2: true while a control-requested seek/flush is expected to empty the ring (suppress xrun accounting for it).
    // RT-readable (a single volatile bool read — see SuppressXrunAccounting); only the control thread writes it.
    private volatile bool _seekRebufferActive;
    private long _seekRebufferDeadlineMs;   // control-thread only — bounds fix 2 so a stuck ring can't suppress xruns forever
    private const long RingRefillTimeoutMs = 1500;   // shared safety net for fixes 1 and 2 — "never hang" / "never suppress forever"
    // Fix 3: captured off RegisterDisposable (spec §7.9 on-box wiring registers the controller there) so a sustained
    // sink-write failure can ask for a rebuild without a new cross-file hook.
    private AudioDeviceController? _deviceController;
    private int _consecutiveSinkFailures;
    private const int SinkFailureRebuildThreshold = 8;   // ~8 blocks (~80 ms at a 10 ms block) of total silence-write failure
    // (Fix 4a — the process-wide SustainedLowLatency capture/restore — is DELETED (H-13): it mutated process state per session and
    // saved nothing the producer's MMCSS registration does not.)
    private bool _warmedUp;   // fix 4b — the one-time pre-Start() warm-up pass (see WarmUp())

    private IAudioSource? _voice;
    private long _voiceTotalFrames;
    private TimeSpan _duration;
    private NormMode _norm = NormMode.Album;
    private float _refLufs = -14f;

    private volatile float _volume = 1f;
    private volatile bool _muted;
    private double _rate = 1.0;
    private WsolaAudioSource? _activeRateSource;
    private readonly System.Collections.Generic.Dictionary<long, WsolaAudioSource> _rateSources = new();
    private long _clockAnchorPosition;

    /// <summary>Desired pitch-preserving rate (0.5 through 3); retained while paused.</summary>
    public double PlaybackRate => Volatile.Read(ref _rate);

    /// <summary>The audible position within the active source, in content frames, after endpoint/DSP latency.
    /// Queue timing and progress at non-unity rates must use this instead of output-frame counters.</summary>
    public long ContentPositionFrames => (long)Math.Round(ContentFrameAt(_position.Project(NowTicks100ns()).TotalSeconds * _format.SampleRate));

    /// <summary>Wall-clock output frames remaining at the desired rate; -1 when the source length is unknown.</summary>
    public long RemainingOutputFrames => VoiceTotalFrames > 0
        ? (long)Math.Ceiling(Math.Max(0, VoiceTotalFrames - ContentPositionFrames) / PlaybackRate) : -1;

    private double ContentFrameAt(double clockFrame)
    {
        var source = Volatile.Read(ref _activeRateSource);
        return source is null ? Math.Max(0, clockFrame - _activeMixerStart)
            : source.SourceFrameAt(Math.Max(0, clockFrame - _clockAnchorPosition - _activeMixerStart));
    }

    private static RingAudioSource? SourceRing(IAudioSource? source)
        => source is WsolaAudioSource stretched ? stretched.Ring : source as RingAudioSource;

    // Single control-thread feeder (real device only — NOT the M4 MMCSS RT thread).
    private Thread? _pumpThread;
    private volatile bool _pumpRun;

    /// <summary>Create a session over an opened endpoint (spec §7). Harness-drivable: call <see cref="PumpAudio"/> /
    /// <see cref="RenderBlock"/> deterministically, or set <paramref name="driveWithOwnThread"/> for a real device.</summary>
    public PcmAudioSession(MixFormat format, IAudioSink @out, IAudioClockSource clock, int maxBlock, bool driveWithOwnThread, IDisposable? endpoint = null)
    {
        _format = format;
        _out = @out;
        _clock = clock;
        _presentationClock = new SessionAudioClock(this);
        _maxBlock = maxBlock;
        _driveWithOwnThread = driveWithOwnThread;
        _endpoint = endpoint;
        _graph = new AudioGraphHost(format.Channels, format.SampleRate);
        _mixer = new CrossfadeMixer(format.Channels, maxBlock);
        _mixBuf = new float[maxBlock * format.Channels];
        _resumeFrames = format.SampleRate / 10;   // F2: the base resume cushion is 100 ms; it doubles per incident and decays after 30 quiet seconds
        // Seek (D3): the 5 ms equal-power pair, built ONCE (a LUT per kind) and shared by every per-command envelope; the jump blend's tail.
        _fade5Frames = Math.Max(1, format.SampleRate / 200);
        _scrubFadeFrames = Math.Max(1, format.SampleRate / 50);
        _jumpTail = new float[_fade5Frames * format.Channels];
        _jumpScratch = new float[_fade5Frames * format.Channels];
        NewFade(FadeKind.In, _fade5Frames);
        NewFade(FadeKind.Out, _fade5Frames);
        NewFade(FadeKind.In, _scrubFadeFrames);     // the scrub's 20 ms pair, built once too
        NewFade(FadeKind.Out, _scrubFadeFrames);
    }

    /// <summary>A fresh UNSTAMPED equal-power fade shell for a mixer command (control side): the lookup table is built once per
    /// (direction, length) and shared, so a seek storm allocates one small object per envelope, never a table. The render thread
    /// stamps the start frame (<see cref="GainEnvelope.StampStart"/>) when it applies the command.</summary>
    private GainEnvelope NewFade(FadeKind kind, int frames)
    {
        var key = (kind, frames);
        lock (_fadeTemplates)
        {
            if (!_fadeTemplates.TryGetValue(key, out var template))
                _fadeTemplates[key] = template = GainEnvelope.FadeUnstamped(kind, frames, CrossCurve.EqualPower);
            return template.NewUnstamped();
        }
    }

    /// <summary>Append one line per live decode ring (voice id, inner source type, buffered frames, producer-done) — the H-8
    /// "producers did not retire" error names exactly which producer is stuck. Diagnostic; reads only the published ring table.</summary>
    internal void DescribeProducers(System.Text.StringBuilder sb)
    {
        var feed = _feed;
        if (feed is null) return;
        foreach (var entry in feed.RingsSnapshot)
            sb.Append(" [voice ").Append(entry.VoiceId).Append(' ').Append(entry.Ring.Inner.GetType().Name)
              .Append(" buffered=").Append(entry.Ring.BufferedFrames).Append(" done=").Append(entry.Ring.ProducerDone).Append(']');
    }

    /// <summary>The graph host (for republishing effects / tests).</summary>
    public AudioGraphHost Graph => _graph;
    /// <summary>The crossfade mixer (for the queue/crossfade layer / tests).</summary>
    public CrossfadeMixer Mixer => _mixer;
    /// <summary>The derived-position tracker.</summary>
    public AudioClockPosition PositionTracker => _position;
    /// <summary>The device mix format.</summary>
    public MixFormat Format => _format;
    /// <summary>The current published state.</summary>
    public PlaybackState CurrentState => _state;
    /// <summary>The active voice's trimmed length in mix-domain frames (for the queue scheduler's join arming).</summary>
    public long VoiceTotalFrames { get { long exact = ExactVoiceEndFrame; return exact >= 0 ? exact : _voiceTotalFrames; } }
    /// <summary>Whether the active length is decoded/trimmed truth rather than rounded catalog duration.</summary>
    public bool VoiceLengthIsExact => ExactVoiceEndFrame >= 0;
    /// <summary>Exact source-domain end frame, including producer EOF discovery; -1 while unknown.</summary>
    public long ExactVoiceEndFrame
    {
        get
        {
            IAudioSource? voice = _voice;
            if (_feed is not null)
                foreach (var entry in _feed.RingsSnapshot)
                    if (entry.VoiceId == ActiveVoiceIdValue) { voice = entry.Ring; break; }
            while (voice is RingAudioSource ring)
            {
                if (ring.DecodedEndFrame >= 0) return ring.DecodedEndFrame;
                voice = ring.Inner;
            }
            return voice switch
            {
                DecoderAudioSource decoder when decoder.ExactLengthFrames >= 0 => decoder.ExactLengthFrames,
                TrimmingSource { LengthIsExact: true } => _voiceTotalFrames,
                MemoryAudioSource => _voiceTotalFrames,
                _ => -1
            };
        }
    }
    /// <summary>Estimated queued frames from submitted PCM and the last published RAW played-clock sample (X2: both include the
    /// silence F2 submitted, so the difference is what the device really still holds).
    /// Safe for diagnostics callers: this cached snapshot never queries an endpoint or its native clock.</summary>
    public int DevicePaddingFrames => (int)Math.Clamp(SubmittedFrames - RawPlayedFrames, 0L, int.MaxValue);
    /// <summary>The active normalization mode.</summary>
    public NormMode NormalizationMode => _norm;
    /// <summary>The active reference LUFS.</summary>
    public float ReferenceLufsValue => _refLufs;
    /// <summary>The mixer-domain frame currently consumed (the sample clock the scheduler ticks on).</summary>
    public long SampleClock => _mixer.ConsumeSeq;
    /// <summary>RT-feed ring underruns (silence written) since this session opened. 0 on the single-thread pull path.</summary>
    public long XrunCount => _feed?.XrunCount ?? 0;

    /// <summary>Times the output DEVICE itself ran dry while streaming (<see cref="IBufferedAudioSink.DeviceUnderruns"/>) — the
    /// glitches <see cref="XrunCount"/> cannot see, because the app's ring was full and the stall was downstream of it.</summary>
    public long DeviceUnderrunCount => Interlocked.Read(ref _retiredDeviceUnderruns) + ((_out as IBufferedAudioSink)?.DeviceUnderruns ?? 0);
    private long _retiredDeviceUnderruns;   // underruns of sinks this session has already swapped out (RebuildSink)

    /// <summary>Approximate independent work totals for off-RT diagnostics; not a coherent audio-state snapshot.</summary>
    public (long Gain, long GainSkipped, long Channel, long ChannelSkipped, long Transport, long TransportSkipped,
        long Meter, long ManagerWakes, long ManagerPasses) ReadWorkCounters()
        => (_masterGain.ProcessedSamples, _masterGain.IdentitySamples,
            _masterChannel.ProcessedSamples, _masterChannel.IdentitySamples,
            _transport.ProcessedSamples, _transport.IdentitySamples, MeterSamples,
            _feed?.ManagerWakeCount ?? 0, _feed?.ManagerPassCount ?? 0);

    /// <summary>Total frames of silence written on starve since this session opened — the SEVERITY companion to
    /// <see cref="XrunCount"/> (which counts incidents). 0 on the single-thread pull path.</summary>
    public long XrunFramesLost => _feed?.XrunFramesLost ?? 0;

    /// <summary>Drain per-underrun incident records into <paramref name="dst"/>, returning how many were written. The feed
    /// is a private implementation detail of the session, so this forwarder is how a host surfaces dropouts without
    /// reaching into the RT plumbing. NON-RT callers only; returns 0 on the single-thread pull path (no feed, no rings).</summary>
    public int DrainXrunEvents(Span<AudioFeedThread.XrunEvent> dst) => _feed?.DrainXrunEvents(dst) ?? 0;

    /// <summary>Fix 2 (spec): true while a control-requested seek/flush is EXPECTED to empty the primary ring — a seek
    /// intentionally discards buffered PCM (<see cref="WorkerApplySeek"/>/<see cref="RtConsumeFlush"/> on the ring below),
    /// so the RT loop's very next reads finding it empty are a planned rebuffer, not a real underrun, and must not pollute
    /// <see cref="XrunCount"/>. A single volatile read — safe to poll from the RT thread. Cleared by
    /// <see cref="UpdateSeekRebufferSuppression"/> once the ring has refilled (or a bounded timeout elapses so a
    /// stuck/dead ring can never suppress real xruns forever).
    /// <para><b>Hook required in <c>AudioFeedThread.FeedOnce</c> (owned by the M4 feed-thread agent; NOT applied here per
    /// the file-ownership split for this task):</b> gate the existing xrun increment on this flag —
    /// <c>if (starved &amp;&amp; !_session.SuppressXrunAccounting) Interlocked.Increment(ref _xrunCount);</c> (currently
    /// <c>if (starved) Interlocked.Increment(ref _xrunCount);</c>). That one-line change is the only remaining piece; this
    /// property + its arm/clear lifecycle are fully implemented on the session side.</para></summary>
    public bool SuppressXrunAccounting => _seekRebufferActive;

    // CONTROL: a seek/flush was just requested — start suppressing xrun accounting for the rebuffer it causes (and exempting it
    // from the F2 resume-cushion doubling: a seek rebuffer is not a starvation incident).
    internal void ArmSeekRebufferSuppression()
    {
        _seekRebufferActive = true;
        _seekRebufferDeadlineMs = Environment.TickCount64 + RingRefillTimeoutMs;
    }

    // CONTROL (called once per Advance tick): clear the fix-2 suppression once the ring has genuinely refilled past its
    // decode-ahead target (the rebuffer is over) or the bounded deadline elapses (never suppress real xruns forever).
    private void UpdateSeekRebufferSuppression()
    {
        if (!_seekRebufferActive) return;
        if (_feed is null || Environment.TickCount64 >= _seekRebufferDeadlineMs) { _seekRebufferActive = false; return; }

        var rings = _feed.RingsSnapshot;
        long active = ActiveVoiceIdValue;
        for (int i = 0; i < rings.Length; i++)
        {
            if (rings[i].VoiceId != active) continue;
            var ring = rings[i].Ring;
            if (ring.BufferedFrames >= ring.TargetFrames || ring.Exhausted) _seekRebufferActive = false;
            return;
        }
    }

    /// <summary>Publish an audio graph (spec §7.4 atomic swap). Control-thread only.</summary>
    public void Configure(AudioGraphSpec spec) => _graph.Publish(spec);

    /// <summary>Attach the M4 RT feed (spec §7.9): after this, <see cref="SetVoice"/> installs a decode↔RT firewall ring
    /// around the voice (the worker decodes ahead, the RT thread mixes copy-only). Control-thread only; call before opening.</summary>
    public void AttachFeed(AudioFeedThread feed) => _feed = feed;

    /// <summary>Register an owned resource (the device watcher/controller wired on-box) disposed with the session. Also
    /// captures an <see cref="AudioDeviceController"/> (fix 3) so a sustained sink-write failure in
    /// <see cref="RenderBlock"/> can request a follow-default rebuild — the on-box wiring (<c>WasapiPcm.CreateBackend</c>)
    /// already registers the controller here, so no new cross-file hook is needed for this.</summary>
    public void RegisterDisposable(IDisposable resource)
    {
        (_owned ??= new()).Add(resource);
        if (_deviceController is null && resource is AudioDeviceController adc) _deviceController = adc;
    }
    private System.Collections.Generic.List<IDisposable>? _owned;

    /// <summary>The device-recovery state of the registered <see cref="AudioDeviceController"/> (spec §7.9), or null on a
    /// session with no controller (headless/tests). A host logs its edges (<c>Running → Reinitializing → Retrying → …</c>)
    /// so a silent device switch is diagnosable from the log alone.</summary>
    public IReadSignal<AudioDeviceState>? DeviceState => _deviceController?.State;

    /// <summary>The live render sink — swapped by <see cref="RebuildSink"/>; the same instance survives a refused rebuild
    /// (a not-ready endpoint keeps the previous sink playing). For diagnostics/tests.</summary>
    public IAudioSink Sink => _out;

    /// <summary>True once an RT feed is attached (the render is driven by <see cref="RtRenderOnce"/>, not the inline pump).</summary>
    public bool IsRtDriven => _feed is not null;

    /// <summary>Install the single M2 voice (queue/crossfade prepares more in M3). Bakes ReplayGain per-source (spec §7.7)
    /// under <paramref name="norm"/>/<paramref name="referenceLufs"/> and builds the per-voice DSP chain from the live graph.</summary>
    public void SetVoice(IAudioSource voice, TimeSpan duration, long totalFrames, NormMode norm, float referenceLufs, float initialVolume)
    {
        if (!TrySetVoice(voice, duration, totalFrames, norm, referenceLufs, initialVolume))
            throw new InvalidOperationException("Audio voice command was not accepted: output command capacity exhausted.");
    }

    /// <summary>Try to admit a primary replacement. On false, source ownership stays with the caller.</summary>
    public bool TrySetVoice(IAudioSource voice, TimeSpan duration, long totalFrames, NormMode norm, float referenceLufs, float initialVolume)
    {
        lock (_mixerCmdProducerLock)
        {
            if (_disposed || !HasMixerCapacity(priority: false)) return false;

            _voice = voice;
            _duration = duration;
            _voiceTotalFrames = totalFrames;
            _activeMixerStart = 0;
            _voiceStarts.Clear();
            ActiveVoiceIdValue = PrimaryVoiceId;   // a fresh primary supersedes any hand-off transport pointer
            _norm = norm;
            _refLufs = referenceLufs;
            // D7: everything after the (now pre-volume) limiter is attenuation-only, which only holds while the master gain is ≤ 1.
            _volume = Math.Clamp(initialVolume, 0f, 1f);
            _masterGain.SetLinear(_volume);
            _resumeFrames = _format.SampleRate / 10;   // a fresh voice starts from the base cushion

            float rg = ReplayGain.ScalarLinear(voice.Loudness, norm, referenceLufs);
            var chain = BuildVoiceChain();
            _voiceEq = FindEq(chain);
            _voiceGainStage = FindGain(chain);
            _voiceGain.Clear();
            _voiceGain[PrimaryVoiceId] = new VoiceGainTrack { Preamp = _voiceGainStage?.CurrentGain ?? 1f, BakedRg = rg > 0f ? rg : 1f, Loudness = voice.Loudness };
            // RT path: the mixer reads pre-decoded PCM from a ring the worker fills (decode is off the RT thread; spec §7.9).
            // Single-thread pull path: the mixer reads the decoder directly (unchanged — golden-PCM identical). _voice stays the
            // inner decoder so Seek/loudness address the real source. Wrap publishes the ring table FIRST (immediate); the
            // mixer voice swap goes through the command SPSC (applied at RenderBlock's top on the render thread).
            var mixSrc = new WsolaAudioSource(_feed is not null ? _feed.Wrap(voice) : voice,
                _format.SampleRate, _format.Channels) { Rate = PlaybackRate };
            _rateSources.Clear();
            _rateSources[PrimaryVoiceId] = mixSrc;
            Volatile.Write(ref _activeRateSource, mixSrc);
            EnqueueMixerCmd(new MixerCmd
            {
                Kind = CmdReplacePrimary,
                Voice = new MixVoice
                {
                    Id = PrimaryVoiceId,
                    Src = mixSrc,
                    Env = GainEnvelope.Constant,
                    StartFrame = 0,
                    ReplayGainScalar = rg,
                    Chain = chain,
                },
            });
            return true;
        }
    }

    private static EqStage? FindEq(IDspStage[]? chain)
    {
        if (chain is null) return null;
        for (int i = 0; i < chain.Length; i++) if (chain[i] is EqStage eq) return eq;
        return null;
    }

    private static GainStage? FindGain(IDspStage[]? chain)
    {
        if (chain is null) return null;
        for (int i = 0; i < chain.Length; i++) if (chain[i] is GainStage gain) return gain;
        return null;
    }

    /// <summary>The mixer (queue/crossfade scheduler wires prepared voices into it).</summary>
    public CrossfadeMixer MixerRef => _mixer;

    /// <summary>The mixer voice id of the PRIMARY (currently-playing) voice — the id a crossfade retargets to fade out
    /// (via <see cref="CrossfadeMixer.TrySetVoiceEnvelope"/>). Stable for the session's playing voice.</summary>
    public long PrimaryVoiceIdValue => PrimaryVoiceId;

    /// <summary>The mixer voice id transport (Seek/replay) addresses — the ACTIVE audible track. Starts as the primary
    /// voice; a committed crossfade/gapless hand-off re-points it at the incoming voice via <see cref="SetActiveVoice"/>
    /// so a post-hand-off seek reaches the track the user hears, not the retired outgoing voice.</summary>
    public long ActiveVoiceIdValue { get; private set; } = PrimaryVoiceId;

    /// <summary>Re-point transport at a committed hand-off's INCOMING voice (spec §8.3/§8.4 — the queue layer promotes the
    /// prepared voice inside the LIVE session instead of reopening the device). Updates the seek target (<see cref="SeekAsync"/> /
    /// the RT feed's seek routing address the incoming voice's ring), the duration (seek clamp + buffer health), and the
    /// trimmed length the next join arms against. Pure bookkeeping — the mixer voice itself was already installed via
    /// <see cref="AddCrossfadeVoice"/>; no graph/ring mutation happens here. Control thread only.</summary>
    public void SetActiveVoice(long voiceId, IAudioSource voice, TimeSpan duration, long totalFrames)
    {
        if (_disposed) return;
        long previousVoice = ActiveVoiceIdValue;
        ActiveVoiceIdValue = voiceId;
        if (previousVoice != voiceId)
        {
            _rateSources.Remove(previousVoice);
            lock (_mixerCmdProducerLock) _voiceGain.Remove(previousVoice);   // the outgoing voice's slot is no longer addressed
        }
        _activeMixerStart = _voiceStarts.TryGetValue(voiceId, out long start) ? start : 0;
        if (_rateSources.TryGetValue(voiceId, out var rateSource)) Volatile.Write(ref _activeRateSource, rateSource);
        _voice = voice;
        _duration = duration;
        _voiceTotalFrames = totalFrames;
    }

    /// <summary>Frames consumed out of the mixer (the device-clock / mixer-timeline domain). Crossfade voice
    /// <c>StartFrame</c>s are expressed in THIS domain (spec §8.2).</summary>
    public long ConsumeSeqFrames => _mixer.ConsumeSeq;

    /// <summary>Add a crossfade voice into the mixer (control thread — the queue/crossfade scheduler's incoming voice).
    /// On the RT path the voice is wrapped in its own decode↔RT firewall ring (the worker decodes ahead; the RT thread
    /// mixes copy-only, and disposes the ring OFF-RT when the voice retires — spec §7.9); on the single-thread pull path
    /// the mixer reads the decoder directly. <paramref name="id"/> must be a fresh non-zero voice id.</summary>
    public void AddCrossfadeVoice(IAudioSource voice, GainEnvelope env, long startFrame, float replayGain, IDspStage[]? chain, long id)
    {
        if (!TryAddCrossfadeVoice(voice, env, startFrame, replayGain, chain, id))
            throw new InvalidOperationException("Audio transition was not admitted; source ownership stays with its preparer.");
    }

    /// <summary>Try to install a voice without exceeding command capacity or three live ring voices.</summary>
    public bool TryAddCrossfadeVoice(IAudioSource voice, GainEnvelope env, long startFrame, float replayGain, IDspStage[]? chain, long id)
    {
        lock (_mixerCmdProducerLock)
        {
            if (_disposed || !HasMixerCapacity(priority: false))
                return false;
            if (_feed is not null && _feed.RingCount >= 3) return false;

            var src = new WsolaAudioSource(_feed is not null ? _feed.WrapAdditional(voice, id) : voice,
                _format.SampleRate, _format.Channels) { Rate = PlaybackRate };
            _rateSources[id] = src;
            _voiceStarts[id] = startFrame;
            _voiceGain[id] = new VoiceGainTrack { Preamp = FindGain(chain)?.CurrentGain ?? 1f, BakedRg = replayGain > 0f ? replayGain : 1f, Loudness = voice.Loudness };
            EnqueueMixerCmd(new MixerCmd
            {
                Kind = CmdAddVoice,
                Voice = new MixVoice
                {
                    Id = id,
                    Src = src,
                    Env = env,
                    StartFrame = startFrame,
                    ReplayGainScalar = replayGain,
                    Chain = chain,
                },
            });
            return true;
        }
    }

    /// <summary>Try to admit an outgoing envelope update. False leaves the current envelope unchanged;
    /// callers must retain or cancel the transition rather than assuming the command was applied.</summary>
    public bool SetVoiceEnvelope(long id, GainEnvelope env)
        => TryEnqueueMixerCmd(new MixerCmd { Kind = CmdSetEnvelope, Id = id, Env = env }, out _);

    /// <summary>Fade the audible mix to zero, drain its tail and stop the endpoint.</summary>
    public async ValueTask FadeOutAsync(TimeSpan duration, CancellationToken ct = default)
    {
        long revision = Interlocked.Increment(ref _transportRevision);
        _transportHoldRequested = true;
        _fadeInSpecified = false;
        long sequence = await PostMixerCommandAsync(new MixerCmd { Kind = CmdFadeOut,
            Frames = Math.Max(1, (int)Math.Round(duration.TotalSeconds * _format.SampleRate)) }, ct).ConfigureAwait(false);
        await WaitAppliedAsync(sequence, ct).ConfigureAwait(false);
        // Done when the hold is acknowledged (phase 3) or a newer transport command superseded this one. A session torn down
        // mid-fade simply ends the wait (the pre-F7 loop's contract) instead of surfacing as an exception to PauseAsync.
        try
        {
            await UntilAsync(_phaseWake, () => Interlocked.Read(ref _transportRevision) != revision || Volatile.Read(ref _transportPhase) == 3, ct)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    /// <summary>Release a transport hold without changing saved volume.</summary>
    public void FadeIn(TimeSpan duration)
    {
        Interlocked.Increment(ref _transportRevision);
        _transportHoldRequested = false;
        _fadeInSpecified = true;
        EnqueueMixerCmd(new MixerCmd { Kind = CmdFadeIn,
            Frames = Math.Max(1, (int)Math.Round(duration.TotalSeconds * _format.SampleRate)) });
    }

    /// <summary>Acknowledge mixer retirement before releasing the voice source.</summary>
    public async ValueTask RemoveVoiceAsync(long id, CancellationToken ct = default)
    {
        long sequence = await PostMixerCommandAsync(new MixerCmd { Kind = CmdRemoveVoice, Id = id }, ct).ConfigureAwait(false);
        await WaitAppliedAsync(sequence, ct).ConfigureAwait(false);
        lock (_mixerCmdProducerLock) _voiceGain.Remove(id);
    }

    /// <summary>Install prepared PCM into the existing stopped endpoint and rebase its timeline.</summary>
    public async ValueTask ReplacePreparedAsync(IPreparedItem prepared, long positionFrames = 0, CancellationToken ct = default)
    {
        await ReplacePreparedCoreAsync(prepared, positionFrames, useSourcePosition: false, ct).ConfigureAwait(false);
    }

    /// <summary>Promote an installed incoming ring into the primary voice without decoding or reopening it.
    /// Keep its transition gate alive until this acknowledgement; cancelling it beforehand would retire the ring.
    /// Returns the actual incoming source position, including any frames heard while the outgoing fade completed.</summary>
    public ValueTask<long> PromoteScheduledVoiceAsync(long voiceId, IAudioSource source, TimeSpan duration,
        long totalFrames, CancellationToken ct = default)
    {
        if (source is not RingAudioSource ring)
            throw new InvalidOperationException("A scheduled promotion requires its existing PCM ring.");
        if (_feed is not null)
        {
            bool installed = false;
            foreach (var entry in _feed.RingsSnapshot)
                if (entry.VoiceId == voiceId && ReferenceEquals(entry.Ring, ring)) { installed = true; break; }
            if (!installed) throw new InvalidOperationException("The scheduled incoming ring is no longer installed.");
        }
        var prepared = new AudioPreparedItem(ring, ring.Gapless, ring.Loudness, totalFrames, duration,
            _format.SampleRate, readinessFrames: 1);
        prepared.TransferOwnership(); // The live mixer already owns this ring; this wrapper never owns its disposal.
        return ReplacePreparedCoreAsync(prepared, 0, useSourcePosition: true, ct);
    }

    private async ValueTask<long> ReplacePreparedCoreAsync(IPreparedItem prepared, long positionFrames,
        bool useSourcePosition, CancellationToken ct)
    {
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (prepared.MixRate != Format.SampleRate || prepared.AudioVoice is not { } voice || !prepared.IsReady)
                throw new InvalidOperationException("Prepared PCM is unavailable or belongs to another output format.");
            if (voice is RingAudioSource readyRing)
                await readyRing.WaitUntilReadyAsync(Math.Min(StartupReadinessFrames, readyRing.TargetFrames), ct).ConfigureAwait(false);
            // A newer transport command may have superseded the caller's outgoing fade while preparation awaited I/O.
            // Reach an acknowledged hold before admitting the replacement transaction and resetting queued device PCM.
            while (!_disposed && _started && Volatile.Read(ref _transportPhase) != 3)
                await FadeOutAsync(TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
            if (_disposed) throw new ObjectDisposedException(nameof(PcmAudioSession));
            long reset = await PostMixerCommandAsync(new MixerCmd { Kind = CmdReset }, ct).ConfigureAwait(false);
            // Admission transfers transaction ownership: later cancellation cannot free a voice already posted to output.
            await WaitAppliedAsync(reset, CancellationToken.None).ConfigureAwait(false);
            while (true)
            {
                _appliedWake.Reset();   // Reset → try → wait: a drain that frees a slot after this point wakes the wait below
                if (TrySetVoice(voice, prepared.Duration, prepared.TotalFrames, _norm, _refLufs, _volume)) break;
                if (_disposed) throw new ObjectDisposedException(nameof(PcmAudioSession));
                await WaitAsync(_appliedWake, 20, CancellationToken.None).ConfigureAwait(false);
            }
            if (prepared is AudioPreparedItem audio) audio.TransferOwnership();
            await WaitAppliedAsync(Interlocked.Read(ref _commandSequence), CancellationToken.None).ConfigureAwait(false);
            long installedPosition = useSourcePosition ? Math.Max(0, voice.PositionFrames) : Math.Max(0, positionFrames);
            _position.Reset();
            _position.Rebase(0, installedPosition);
            _clockAnchorPosition = installedPosition;
            _sink?.Duration(prepared.Duration);
            _sink?.Position(TimeSpan.FromSeconds((double)installedPosition / Format.SampleRate));
            _sink?.SettleTransport();
            Publish(_playRequested ? PlaybackState.Ready : PlaybackState.Paused);
            return installedPosition;
        }
        finally { _replacementGate.Release(); }
    }

    // ── F7: signalled completions (V-PE9) ───────────────────────────────────────────────────────────────────────────────
    // No control-plane poll: the RT thread SETS these kernel events outside the tripwire (the same carve-out as the low-water
    // wake) and every waiter follows Reset → check → wait, so a Set that lands between the check and the wait is never lost.
    // They are MANUAL-reset (an auto-reset event hands one wake to one of several waiters and starves the rest) and are never
    // disposed: a late RT Set must never hit a closed handle, and the finalizer reclaims them with the session.
    private readonly ManualResetEvent _appliedWake = new(false);   // RT: Set at the end of DrainMixerCmds when ≥ 1 command applied
    private readonly ManualResetEvent _phaseWake = new(false);     // RT/cold thread: Set whenever _transportPhase changes (SetPhase)

    /// <summary>Await a kernel event without a timer tick: the pool's registered-wait thread completes the TCS when the handle
    /// signals or <paramref name="timeoutMs"/> elapses (false). A timeout is NEVER success — callers re-check their condition.
    /// The registration is always unregistered, and cancellation completes the wait as cancelled.</summary>
    internal static async Task<bool> WaitAsync(WaitHandle handle, int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle reg = ThreadPool.RegisterWaitForSingleObject(handle,
            static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut), tcs, timeoutMs, executeOnlyOnce: true);
        using var registration = ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { reg.Unregister(null); }
    }

    /// <summary>Loop until <paramref name="condition"/> holds or the session is disposed (then throws). The event is RESET before
    /// each check so a Set that lands between the check and the wait wakes the next iteration; the 20 ms recheck bounds any
    /// lost wake-up when several waiters share one event (one waiter's Reset can swallow another's signal).</summary>
    private async ValueTask UntilAsync(ManualResetEvent wake, Func<bool> condition, CancellationToken ct)
    {
        while (!_disposed)
        {
            wake.Reset();
            if (condition()) return;
            await WaitAsync(wake, 20, ct).ConfigureAwait(false);
        }
        throw new ObjectDisposedException(nameof(PcmAudioSession));
    }

    private ValueTask WaitAppliedAsync(long sequence, CancellationToken ct)
        => UntilAsync(_appliedWake, () => Interlocked.Read(ref _appliedSequence) >= sequence, ct);

    private ValueTask WaitPhaseAsync(int phase, CancellationToken ct)
        => UntilAsync(_phaseWake, () => Volatile.Read(ref _transportPhase) == phase, ct);

    /// <summary>Wait until <paramref name="ring"/> holds ≥ <paramref name="minFrames"/> frames (or its producer is done) on the
    /// ring's own manual-reset <see cref="RingAudioSource.ReadyWake"/>, which the producer Sets after every pump. A producer
    /// fault surfaces here exactly as it does from <see cref="RingAudioSource.WaitUntilReadyAsync(int, CancellationToken)"/>.</summary>
    private static async ValueTask WaitRingReadyAsync(RingAudioSource ring, int minFrames, CancellationToken ct)
    {
        Volatile.Write(ref ring.ReadyMinimum, minFrames);   // the producer Sets ReadyWake only when this is met
        while (true)
        {
            ring.ReadyWake.Reset();
            if (ring.IsReady(minFrames)) break;
            if (!await WaitAsync(ring.ReadyWake, 20, ct).ConfigureAwait(false)) ct.ThrowIfCancellationRequested();
        }
        if (ring.ProducerFault is { } fault) throw fault;
    }

    // A transport phase change is observable by every WaitPhaseAsync/FadeOutAsync waiter (RT-safe: one kernel Set, outside the tripwire).
    private void SetPhase(int phase)
    {
        Volatile.Write(ref _transportPhase, phase);
        _phaseWake.Set();
    }

    private async ValueTask<long> PostMixerCommandAsync(MixerCmd command, CancellationToken ct)
    {
        while (!_disposed)
        {
            ct.ThrowIfCancellationRequested();
            _appliedWake.Reset();   // a drain after this point frees a slot AND wakes the wait below
            if (TryEnqueueMixerCmd(command, out long sequence)) return sequence;
            await WaitAsync(_appliedWake, 20, ct).ConfigureAwait(false);
        }
        throw new ObjectDisposedException(nameof(PcmAudioSession));
    }

    private void EnqueueMixerCmd(in MixerCmd cmd)
    {
        if (!TryEnqueueMixerCmd(cmd, out _))
            throw new InvalidOperationException("Audio command capacity exhausted; operation was not accepted.");
    }

    private bool HasMixerCapacity(bool priority)
    {
        int used = (Volatile.Read(ref _mixerCmdTail) - Volatile.Read(ref _mixerCmdHead)) & (_mixerCmdQ.Length - 1);
        return used < (priority ? _mixerCmdQ.Length - 1 : _mixerCmdQ.Length - 5);
    }

    private bool TryEnqueueMixerCmd(MixerCmd cmd, out long sequence)
    {
        sequence = 0;
        lock (_mixerCmdProducerLock)
        {
            // CmdReleaseVoice is priority: it gives a held main voice back (a hold the queue could not release would leave the track silent).
            if (_disposed || !HasMixerCapacity(cmd.Kind is CmdFadeOut or CmdFadeIn or CmdRemoveVoice or CmdReset or CmdFadeOutHold or CmdReleaseVoice)) return false;
            int tail = _mixerCmdTail;
            int next = (tail + 1) & (_mixerCmdQ.Length - 1);
            if (next == Volatile.Read(ref _mixerCmdHead)) return false;
            cmd.Sequence = sequence = ++_commandSequence;
            _mixerCmdQ[tail] = cmd;
            Volatile.Write(ref _mixerCmdTail, next);
        }
        if (_feed is null) DrainMixerCmds();
        else _feed.WakeOutput();
        return true;
    }

    // RENDER thread (RenderBlock top): apply queued mixer mutations. Alloc-free (List ops within capacity 8).
    // Runs BEFORE the tripwire window: the kernel Set that wakes the control-side waiters at the end sits outside it.
    private void DrainMixerCmds()
    {
        int head = _mixerCmdHead;
        bool applied = false;
        while (head != Volatile.Read(ref _mixerCmdTail))
        {
            applied = true;
            ref var c = ref _mixerCmdQ[head];
            switch (c.Kind)
            {
                case CmdReplacePrimary:
                    if (_feed is not null)
                        foreach (var old in _mixer.VoicesSpan)
                            if (SourceRing(old.Src) is { } retired && !ReferenceEquals(retired, SourceRing(c.Voice.Src))) _feed.EnqueueRetire(retired);
                    if (_feed is null)
                        foreach (var old in _mixer.VoicesSpan)
                            if (!ReferenceEquals(old.Src is WsolaAudioSource oldRate ? oldRate.Inner : old.Src,
                                c.Voice.Src is WsolaAudioSource newRate ? newRate.Inner : c.Voice.Src))
                                (old.Src as IDisposable)?.Dispose();
                    _mixer.Clear();
                    _mixer.AddVoice(in c.Voice);
                    break;
                case CmdAddVoice:       _mixer.AddVoice(in c.Voice); break;
                case CmdSetEnvelope:    _mixer.TrySetVoiceEnvelope(c.Id, c.Env!); break;
                case CmdRemoveVoice:
                    foreach (var removed in _mixer.VoicesSpan)
                        if (removed.Id == c.Id)
                        {
                            if (_feed is not null && SourceRing(removed.Src) is { } ring) _feed.EnqueueRetire(ring);
                            else if (_feed is null) (removed.Src as IDisposable)?.Dispose();
                            break;
                        }
                    _mixer.RemoveVoice(c.Id);
                    break;
                case CmdFadeOut:
                    _holdNoStop = false;   // a real pause supersedes a seek hold: its completion must STOP the device
                    if (!_started)
                    { _transport = new TransportRamp(0f); SetPhase(3); }
                    else if (_starvationPhase != 0)
                    { _fadeTailSubmitted = _submittedFrames; SetPhase(2); }
                    else
                    { _transport.Retarget(0f, _mixer.ConsumeSeq, c.Frames); SetPhase(1); }
                    break;
                case CmdFadeIn:
                    if (!_started && _submittedFrames == 0) _transport = new TransportRamp(0f);
                    _transport.Retarget(1f, _mixer.ConsumeSeq, c.Frames);
                    _holdNoStop = false;   // from phase 4 (the seek hold) too: gain ramps 0 → 1 and the transport runs again
                    SetPhase(0);
                    if (!_started && _playRequested) Volatile.Write(ref _startRequested, 1);
                    break;
                case CmdFadeOutHold:
                    // Seek hold: the fade-out lands in phase 4 (gain 0, STILL rendering — through F2's silence path when the ring is
                    // dry), never phase 2/3, so the device is neither stopped nor drained and nothing needs a restart afterwards.
                    // Only while something is audible: the RT renders a fade only while the transport runs, so a hold requested on a
                    // device that is not started (or is draining / held for a pause) would strand the transport in phase 1.
                    if (!_started || Volatile.Read(ref _transportPhase) is 2 or 3) break;
                    _transport.Retarget(0f, _mixer.ConsumeSeq, c.Frames);
                    _holdNoStop = true;
                    SetPhase(1);
                    break;
                case CmdJumpWithinRing:
                {
                    // Seek B (D3): the whole decision is made HERE, on the thread that owns the ring cursor — the control side only
                    // posts the absolute target. A refusal moves nothing.
                    ref MixVoice jumpVoice = ref _mixer.VoiceRef(c.Id);
                    bool jumped = !Unsafe.IsNullRef(ref jumpVoice) && SourceRing(jumpVoice.Src) is { } jumpRing
                        && RtJumpWithinRing(ref jumpVoice, jumpRing, c.Position, c.Frames);
                    if (jumped) Volatile.Write(ref _jumpSubmitIndex, _submittedFrames + _pendingFrames);   // the next block starts the new position
                    Volatile.Write(ref _jumpVerdict, jumped ? 1 : 2);
                    break;
                }
                case CmdSwapVoice:
                    RtSwapVoice(ref c);
                    break;
                case CmdSilenceVoice:
                {
                    // The seek stale-fade: the old voice (still playing a position the user left) fades to silence and is PARKED —
                    // never retired, never read again — until an envelope is installed on it or a swap replaces it.
                    ref MixVoice silenced = ref _mixer.VoiceRef(c.Id);
                    // A voice that is already HELD (a scrub hold, an earlier silence) is already fading to silence or parked there: a fresh
                    // Out stamped from unity would make it audible again for the length of the fade — a burst of audio from a position the
                    // listener left. The stale timer fires on a prepare that outlives it, including one that follows a scrub release.
                    if (!Unsafe.IsNullRef(ref silenced) && !silenced.Held)
                    {
                        long silenceAt = _mixer.ConsumeSeq;
                        silenced.Env = c.Env!.StampStart(silenceAt);
                        // A voice that cannot supply the fade (its ring is dry) is parked at once: waiting for frames that are not coming
                        // would hold the whole mixer on it, and the silence is what the listener already hears.
                        silenced.HoldAtFrame = RtCanSupplyFade(ref silenced, c.Frames) ? silenceAt + c.Frames : silenceAt;
                        silenced.Held = true;
                    }
                    break;
                }
                case CmdHoldVoice:
                    RtHoldVoice(ref c);
                    break;
                case CmdReleaseVoice:
                    RtReleaseVoice(ref c);
                    break;
                case CmdSetEq:
                {
                    // H-4: the ONLY place an EQ stage's cascade changes — at a block boundary on the thread that renders it. The band
                    // set and coefficients were designed off-thread; AdoptPending copies them into the stage's fixed 16-band storage.
                    var span = _mixer.VoicesSpan;
                    for (int i = 0; i < span.Length; i++)
                    {
                        if (span[i].Id != c.Id) continue;
                        var chain = span[i].Chain;
                        FindEq(chain)?.AdoptPending(c.Bands, c.Coeffs);
                        FindGain(chain)?.SetTargetLinear(c.Linear, c.Frames);   // preamp × normalization delta, ramped
                        break;
                    }
                    break;
                }
                case CmdSetVoiceGain:
                {
                    var span = _mixer.VoicesSpan;
                    for (int i = 0; i < span.Length; i++)
                    {
                        if (span[i].Id != c.Id) continue;
                        FindGain(span[i].Chain)?.SetTargetLinear(c.Linear, c.Frames);
                        break;
                    }
                    break;
                }
                case CmdSeekAnchor:
                    for (int i = _mixer.VoicesSpan.Length - 1; i >= 0; i--)
                    {
                        var voice = _mixer.VoicesSpan[i];
                        if (voice.Id != c.Id)
                        {
                            _mixer.RemoveVoice(voice.Id);
                            if (SourceRing(voice.Src) is { } ring) _feed?.EnqueueRetire(ring);
                            if (_feed is null) (voice.Src as IDisposable)?.Dispose();
                        }
                        else
                        {
                            _mixer.VoicesSpan[i].StartFrame = 0;
                            _mixer.VoicesSpan[i].Env = GainEnvelope.Constant;
                        }
                    }
                    break;
                case CmdResetRate:
                    foreach (var live in _mixer.VoicesSpan)
                        if (live.Id == c.Id && live.Src is WsolaAudioSource stretched) stretched.Reset(c.Position);
                    break;
                case CmdReset:
                    // Device-lost only: a Stop/Reset on an invalidated endpoint is a sink failure (→ rebuild), never a
                    // render fault — any other exception still propagates as before.
                    try
                    {
                        _out.Stop();
                        _started = false;
                        if (_out is IBufferedAudioSink buffered) buffered.Reset();
                    }
                    catch (AudioDeviceLostException) { _started = false; RecordDeviceLost(); }
                    if (_clock is SyntheticAudioClock synthetic) synthetic.Reset();
                    _pendingFrames = _pendingOffset = 0;
                    _submittedFrames = _playedFrames = _deviceFrameOrigin = 0;
                    Interlocked.Exchange(ref _rawPlayedFrames, 0);
                    ResetSilenceLedger();
                    _starvationPhase = 0;
                    _starvedRing = null;
                    _holdNoStop = false;
                    _graph.Live.TerminalLimiter.Reset();   // its ~2 ms lookahead delay line holds pre-reset audio: it must not replay after the reset
                    _mixer.ConsumeSeq = 0;
                    Interlocked.Increment(ref _renderEpoch);
                    break;
            }
            Volatile.Write(ref _appliedSequence, c.Sequence);
            c = default;   // release refs (Src / Chain / Env / Bands / Coeffs)
            head = (head + 1) & (_mixerCmdQ.Length - 1);
        }
        Volatile.Write(ref _mixerCmdHead, head);
        if (applied) _appliedWake.Set();   // F7: wake every WaitAppliedAsync / full-queue retry — one Set per drain, outside the tripwire
    }

    // ── seek arms (RT; alloc-free: voice slots by reference, preallocated tail buffers, stamped envelopes) ────────────────────

    /// <summary>RT: does the listener hear the active voice's audio right now? True while the device runs, nothing is starved and the
    /// transport is at (or fading from) full level. False while paused / draining / starting / starved / held at silence — then a seek
    /// transition has nothing to blend with and is a plain cut.</summary>
    private bool RtOldVoiceAudible()
    {
        int phase = Volatile.Read(ref _transportPhase);
        return _started && _starvationPhase == 0 && (phase == 0 || (phase == 1 && !_holdNoStop));
    }

    /// <summary>RT: can this voice supply <paramref name="fade"/> more frames without waiting on its producer? A voice that cannot
    /// (its ring is dry or mid-flush) must not be asked to crossfade: it would stall the whole mixer on frames that are not coming.
    /// A voice whose producer is done ends by itself.</summary>
    private static bool RtCanSupplyFade(ref MixVoice voice, int fade)
    {
        var ring = SourceRing(voice.Src);
        if (ring is null || ring.ProducerDone) return true;
        if (ring.HasPendingFlush) return false;
        int have = voice.Src is WsolaAudioSource stretched ? stretched.ReadableFrames(fade) : ring.BufferedFrames;
        return have >= fade;
    }

    /// <summary>RT: hand a retired voice's source off for disposal OFF this thread — its ring goes to the feed's worker; on the
    /// single-thread pull path (control IS the render thread) it is disposed inline.</summary>
    private void RtRetireVoiceSource(IAudioSource source)
    {
        if (_feed is not null) { if (SourceRing(source) is { } ring) _feed.EnqueueRetire(ring); }
        else (source as IDisposable)?.Dispose();
    }

    /// <summary>RT, seek B: move <paramref name="voice"/>'s ring cursor to the ABSOLUTE content frame <paramref name="target"/> inside the
    /// audio the ring still holds, and leave a 5 ms equal-power blend (the old position's next frames × Out ⊕ the new position's × In) in
    /// the voice's slot. Returns false — NOTHING moved — when the target is outside the intact kept-behind span or the decoded ahead span
    /// minus one fade and one block.
    /// <para>The tail is read from the voice itself, so a stretched voice's own lookahead and rate are honoured, and the jump is then
    /// measured from where the ring cursor has got to. The reach is checked BEFORE the tail is read, with the worst case of what that
    /// read may consume (<c>slack</c>) held back from the rewind span — a refusal after the tail is gone would skip audio. A blend
    /// still running from an earlier jump is folded into the new tail, so back-to-back jumps stay click-free. A voice nobody hears
    /// (paused, held, starved) is jumped without a tail.</para></summary>
    private bool RtJumpWithinRing(ref MixVoice voice, RingAudioSource ring, long target, int fadeFrames)
    {
        int ch = _format.Channels;
        int fade = Math.Min(fadeFrames, _jumpTail.Length / ch);
        if (fade <= 0 || ring.HasPendingFlush) return false;
        int block = RenderBlockFrames;
        bool audible = !voice.Held && RtOldVoiceAudible();
        var stretched = voice.Src as WsolaAudioSource;

        int slack = 0;   // the most ring frames the tail read can consume (a stretched voice pulls its window ahead of its output)
        if (audible)
        {
            int hop = _format.SampleRate / 50, search = _format.SampleRate / 100;
            slack = stretched is null ? fade : stretched.Rate == 1.0 ? fade + 2 * hop : fade * 3 + hop * 8 + search * 2;
        }
        long delta = target - ring.PositionFrames;
        long lowest = audible ? slack - ring.KeptBehindFrames : -(long)ring.KeptBehindFrames;
        long highest = (long)ring.BufferedFrames - fade - block;
        if (delta < lowest || delta > highest) return false;

        if (!audible)
        {
            if (!ring.RtTryJump((int)delta)) return false;
            stretched?.Reset(target);
            voice.Blend = default;
            return true;
        }

        var scratch = _jumpScratch.AsSpan(0, fade * ch);
        int got = Math.Max(0, voice.Src.Read(scratch, ch));          // the audio that WOULD have played next, from the voice itself
        if (got < fade) scratch[(got * ch)..].Clear();
        if (got > 0 && voice.Blend.Remaining > 0) voice.Blend.Apply(scratch[..(got * ch)], got);   // fold a blend still in flight
        scratch.CopyTo(_jumpTail);
        long remaining = target - ring.PositionFrames;               // from where the tail read left the cursor
        if (!ring.RtTryJump((int)remaining)) return false;           // unreachable: the pre-check reserved the rewind
        stretched?.Reset(target);
        voice.Blend = got > 0 ? JumpBlend.Start(_jumpTail, fade, ch) : default;
        return true;
    }

    /// <summary>RT, seek A: install the prepared voice and replace the old one in ONE step, at the first frame of the block about to be
    /// rendered — no block renders with half a swap. The old voice keeps playing at unity right up to that frame; then either the 5 ms
    /// equal-power pair (the listener hears it, it can supply the fade) or a cut (paused / starved / parked / dry: nothing to blend).
    /// A cut retires the old voice here, off the render path; a crossfaded one retires when its Out window has passed.</summary>
    private void RtSwapVoice(ref MixerCmd c)
    {
        long at = _mixer.ConsumeSeq;   // the next block's first mixer frame: a retained (already rendered) block is counted in it
        int fade = c.Frames;
        ref MixVoice old = ref _mixer.VoiceRef(c.Id);
        bool hasOld = !Unsafe.IsNullRef(ref old);
        bool crossfade = hasOld && c.Env is not null && !old.Held && RtOldVoiceAudible() && RtCanSupplyFade(ref old, fade);

        c.Voice.StartFrame = at;
        c.Voice.Env.StampStart(at);                       // the incoming voice's In
        if (crossfade) old.Env = c.Env!.StampStart(at);   // the outgoing voice's Out (power-complementary with the In)
        else if (hasOld)
        {
            IAudioSource oldSource = old.Src;
            _mixer.RemoveVoice(c.Id);                     // `old` is dead from here
            RtRetireVoiceSource(oldSource);
        }
        _mixer.AddVoice(in c.Voice);
        if (_starvationPhase != 0) _starvedRing = SourceRing(c.Voice.Src);   // the incident continues on the live voice (the old ring is retiring)

        // An aborted in-place seek can leave the transport held at silence: the swap is the resume (a pause clears _holdNoStop first).
        if (_holdNoStop && Volatile.Read(ref _transportPhase) is 1 or 4)
        {
            _transport.Retarget(1f, at, fade);
            _holdNoStop = false;
            SetPhase(0);
        }
        Volatile.Write(ref _swapAtFrame, at);
        Volatile.Write(ref _swapSubmitIndex, _submittedFrames + _pendingFrames);   // the device submit index the swap block is written at
    }

    /// <summary>RT, scrub begin (CmdHoldVoice): in ONE step at the first frame of the block about to be rendered, the MAIN voice starts
    /// its Out envelope and is HELD — parked, never read, never retired, never waited for, once the fade has ended (the same hold the seek
    /// stale-fade uses; its ring, decoder and byte source stay alive and untouched) — and the GRAIN voice is added with its In envelope and
    /// its gain slot at the scrub level. No block renders with half a hold. Refused (nothing moved) when the main voice is gone or
    /// already held, or the grain id is taken; the verdict tells the control side to retire the grain ring.</summary>
    private void RtHoldVoice(ref MixerCmd c)
    {
        long at = _mixer.ConsumeSeq;
        ref MixVoice main = ref _mixer.VoiceRef(c.Id);
        if (Unsafe.IsNullRef(ref main) || main.Held || _mixer.HasVoice(c.Voice.Id))
        {
            Volatile.Write(ref _scrubVerdict, 2);
            return;
        }
        int fade = c.Frames;
        main.Env = c.Env!.StampStart(at);                                              // Out; power-complementary with the grain's In
        // A voice that cannot supply the fade (its ring is dry) is parked at once: waiting for frames that are not coming would hold the
        // whole mixer on it, and the grain voice is what the listener should hear.
        main.HoldAtFrame = RtCanSupplyFade(ref main, fade) ? at + fade : at;
        main.Held = true;

        c.Voice.StartFrame = at;
        c.Voice.Env.StampStart(at);                                                    // the grain voice's In
        FindGain(c.Voice.Chain)?.SetLinear(c.Linear);                                  // the scrub level lives in the GAIN SLOT; the normalization scalar is untouched
        _mixer.AddVoice(in c.Voice);                                                   // `main` is dead from here (the list may move)
        Volatile.Write(ref _scrubVerdict, 1);
    }

    /// <summary>RT, scrub end (CmdReleaseVoice). The grain voice (<see cref="MixerCmd.Position"/>) starts its fade-out (<see cref="MixerCmd.Env2"/>)
    /// and retires itself when it completes — unless it is already fading, which a repeated release must not restart from unity. The MAIN
    /// voice depends on <see cref="MixerCmd.Frames"/>:
    /// <list type="bullet">
    /// <item><b>&gt; 0 — cancel.</b> A held main voice is un-held with a fade-in (<see cref="MixerCmd.Env"/>) and resumes exactly where the hold
    /// left its content cursor; the device submit index and content frame it returns at are published for the rebase. A main voice that
    /// is not held is left alone (idempotent: the verdict says so and nothing is touched, grain included).</item>
    /// <item><b>0 — silent.</b> The main voice STAYS held (parked at gain 0): the release is the first half of a seek. Un-parking it with its
    /// completed Out envelope would retire it (<see cref="MixVoice.IsFinished"/>); un-parking it at unity would be audible, and the
    /// seek's own swap would then crossfade a voice nobody was hearing. Parked, it is exactly the state the seek stale-fade leaves, so
    /// every seek path already serves it: a ring jump and the in-place fallback are followed by an envelope on it (which un-holds it), a
    /// design-A swap cuts it.</item>
    /// </list></summary>
    private void RtReleaseVoice(ref MixerCmd c)
    {
        long at = _mixer.ConsumeSeq;
        ref MixVoice main = ref _mixer.VoiceRef(c.Id);
        bool held = !Unsafe.IsNullRef(ref main) && main.Held;
        if (c.Frames > 0)
        {
            if (!held) { Volatile.Write(ref _scrubVerdict, 2); return; }
            long position = main.Src.PositionFrames;
            if (main.Src is WsolaAudioSource stretched && stretched.Rate != 1.0 && SourceRing(main.Src) is { } ring)
            {
                // A stretched voice's content cursor trails its ring's read cursor by the lookahead it pulled; resume from the ring's, so the
                // label and the audio agree (the lookahead the hold left behind is skipped, under the fade-in).
                position = ring.PositionFrames;
                stretched.Reset(position);
            }
            Volatile.Write(ref _releaseFrame, position);
            Volatile.Write(ref _releaseSubmitIndex, _submittedFrames + _pendingFrames);
            main.Held = false;
            main.Env = c.Env!.StampStart(at);                                          // In
        }
        if (c.Position != 0 && c.Env2 is { } fadeOut)
        {
            ref MixVoice grain = ref _mixer.VoiceRef(c.Position);
            if (!Unsafe.IsNullRef(ref grain) && grain.Env.Kind != FadeKind.Out) grain.Env = fadeOut.StampStart(at);
        }
        Volatile.Write(ref _scrubVerdict, 1);
    }

    // The ledger is empty again: the RT moves its tail onto the head (it only ever writes the tail) and bumps the epoch so the
    // control-side cache of already-played silence is dropped on its next read. Called on CmdReset (RT) and RebuildSink (cold
    // thread, with the feed parked) — both restart the submit-index domain at 0.
    private void ResetSilenceLedger()
    {
        Volatile.Write(ref _silenceTail, Volatile.Read(ref _silenceHead));
        Interlocked.Increment(ref _silenceEpoch);
    }

    private bool MixerCmdsPending => Volatile.Read(ref _mixerCmdHead) != Volatile.Read(ref _mixerCmdTail);

    /// <summary>Compute the per-source ReplayGain linear scalar under the session's current normalization/reference-LUFS
    /// for a crossfade voice (spec §7.7) — the scalar to pass to <see cref="AddCrossfadeVoice"/>.</summary>
    public float ReplayGainScalarFor(in ReplayGainInfo loudness) => ReplayGain.ScalarLinear(loudness, _norm, _refLufs);
    /// <summary>Build a per-voice DSP chain (the queue scheduler's chain factory, and this session's own primary voice). The chain is
    /// ALWAYS <c>[GainStage, EqStage, …]</c> (H-4): the gain slot carries EQ preamp × normalization so a live change ramps without
    /// a reopen, and the EQ stage exists even while the EQ is off so enabling it later ramps in from identity (E-6). The chain
    /// is seeded from the LIVE EQ design when effects are bound — gain-only edits are never republished into the graph spec, so
    /// a voice built after one must not start from the stale published bands.</summary>
    public IDspStage[] BuildVoiceChain()
    {
        var chain = _graph.Live.BuildVoiceChain();
        if (Volatile.Read(ref _eqDesign) is { } design)
        {
            FindEq(chain)?.Seed(design.Bands, design.Coeffs);
            FindGain(chain)?.SetLinear(design.Preamp);
        }
        return chain;
    }
    /// <summary>The primary voice's live EQ stage (for effects tests) — present on every primary voice, an identity cascade while
    /// the EQ is off. Its cascade changes only through <c>CmdSetEq</c>, at a block boundary.</summary>
    public EqStage? PrimaryVoiceEq => _voiceEq;
    /// <summary>The primary voice's gain slot — EQ preamp × normalization delta (for effects/normalization tests).</summary>
    internal GainStage? PrimaryVoiceGainStage => _voiceGainStage;
    /// <summary>The preamp (linear) of the last designed EQ: <c>10^(−max(0, largest band gain)/20)</c>, 1 with the EQ off.</summary>
    internal float EqPreampLinear => Volatile.Read(ref _eqDesign)?.Preamp ?? 1f;
    /// <summary>True while a <c>CmdSetEq</c> could not be admitted (the command queue was full) and awaits the next tick's retry.</summary>
    internal bool EqCommandPending => _eqDirty;
    /// <summary>The primary voice's current ReplayGain scalar — the scalar baked when the voice was added times the normalization
    /// delta last requested for it (for effects/normalization tests).</summary>
    public float PrimaryVoiceReplayGainScalar
    {
        get
        {
            lock (_mixerCmdProducerLock)
                return _voiceGain.TryGetValue(PrimaryVoiceId, out var track) ? track.BakedRg * track.Delta : 1f;
        }
    }

    /// <summary>Ramp a voice's gain slot to <paramref name="factor"/> × its EQ preamp over <paramref name="rampMs"/> ms (D5): the
    /// live normalization change — <paramref name="factor"/> is <c>factor_now / factor_baked_for_that_voice</c> (absolute, relative
    /// to what its decoder folded in), so no decoder is reopened and the change is audible within the ramp. Returns false when
    /// the voice is unknown to the session or the command queue could not admit it (the previous factor stays in force).</summary>
    public bool SetVoiceGain(long voiceId, float factor, int rampMs = 50)
    {
        if (_disposed || !float.IsFinite(factor) || factor < 0f) return false;
        lock (_mixerCmdProducerLock)
        {
            if (!_voiceGain.TryGetValue(voiceId, out var track)) return false;
            int ramp = Math.Max(0, (int)Math.Round(rampMs * (double)_format.SampleRate / 1000.0));
            if (!TryEnqueueMixerCmd(new MixerCmd { Kind = CmdSetVoiceGain, Id = voiceId, Linear = track.Preamp * factor, Frames = ramp }, out _)) return false;
            track.Delta = factor;
            return true;
        }
    }

    // ── live effects surface (spec §7.10) ────────────────────────────────────────────────────────────────────────────

    /// <summary>Bind the live <see cref="IAudioEffects"/> surface so control-thread reconciles drive the M2 graph: a
    /// gain-only EQ tweak ramps via the param plane; a freq/Q/topology change recompiles coefficients OFF-block and
    /// re-<see cref="AudioGraphHost.Publish"/>es the graph; balance/normalization/reference-LUFS update the smoothed plane
    /// and the per-voice scalar (spec §7.10). Snapshots the current EQ so the first reconcile is a no-op.</summary>
    public void BindEffects(IAudioEffects effects)
    {
        _liveEffects = effects;
        ActivateVisualizerSource();
        _eqTopologySig = EqTopologySignature(effects.Equalizer);
        SnapshotBandGains(effects.Equalizer);

        // Voices built from here on seed from this design. A voice that already exists was built from the published graph spec
        // (bands, but no preamp): stamp its gain slot now so a boosted EQ is not audible un-attenuated until the next edit.
        var design = DesignEq(effects.Equalizer);
        Volatile.Write(ref _eqDesign, design);
        lock (_mixerCmdProducerLock)
        {
            long id = ActiveVoiceIdValue;
            if (_voiceGain.TryGetValue(id, out var track) && track.Preamp != design.Preamp
                && TryEnqueueMixerCmd(new MixerCmd { Kind = CmdSetVoiceGain, Id = id, Linear = design.Preamp * track.Delta, Frames = 0 }, out _))
                track.Preamp = design.Preamp;
        }
    }

    /// <summary>Make this already-bound session the visualizer source again after a replacement rollback.
    /// Rotates only the source token/visibility epoch; does not rebind effects or mutate EQ reconciliation state.
    /// Call off the output thread after restoring session ownership.</summary>
    public void ActivateVisualizerSource()
    {
        long source = _liveEffects is AudioEffects live ? live.BindVisualizerSource() : 0;
        Volatile.Write(ref _visualizerSource, source);
    }

    /// <summary>Reconcile the bound effects into the graph (control thread; spec §7.10). Called every pump; only CHANGES
    /// act (idempotent — no zipper, no gratuitous republish). Safe to call when no effects are bound (a no-op).</summary>
    public void ReconcileEffects()
    {
        var fx = _liveEffects;
        if (fx is null) return;

        var eq = fx.Equalizer;
        long sig = EqTopologySignature(eq);
        if (sig != _eqTopologySig)
        {
            // A freq/Q/type/count/enabled change (spec §7.8): RE-PUBLISH the graph so voices built later start from the new topology
            // (old graph retires under RenderInFlightDepth+1 quarantine); the LIVE voice's cascade changes below, via CmdSetEq.
            _eqTopologySig = sig;
            _graph.Publish(PcmAudioPlayer.BuildGraphSpec(fx, _format));
            SnapshotBandGains(eq);
            _eqDirty = true;
        }
        else if (eq.Enabled.Peek() && BandGainsChanged(eq))
        {
            // Same topology → a gain-only edit (spec §7.10: set-vs-ramp is a value, no zipper). No republish.
            SnapshotBandGains(eq);
            _eqDirty = true;
        }
        // H-4: coefficients are designed HERE, off the RT, and reach the stage only as a CmdSetEq applied at a block boundary. A
        // command the queue could not admit stays dirty and is re-sent — freshly designed from the latest bands — next tick.
        if (_eqDirty) _eqDirty = !SendEq(eq);

        // Balance: the RT reads the target at each block start (a smoothed param written from this thread raced the RT's Advance).
        _balanceTarget = fx.Balance.Peek();

        // Normalization / reference LUFS → each voice's gain slot (spec §7.7): delta = new scalar / the scalar its voice baked.
        var norm = fx.Normalization.Peek();
        float refl = fx.ReferenceLufs.Peek();
        if (norm != _norm || refl != _refLufs)
        {
            _norm = norm;
            _refLufs = refl;
            _rgDirty = true;
        }
        if (_rgDirty) _rgDirty = !RebaseReplayGain();
    }

    // Re-derive every tracked voice's normalization delta from the current mode/reference and ramp its gain slot to it. Returns
    // false when any command was not admitted (the caller retries next tick; a voice already at its delta is skipped).
    private bool RebaseReplayGain()
    {
        bool allAdmitted = true;
        lock (_mixerCmdProducerLock)
        {
            foreach (var (id, track) in _voiceGain)
            {
                float rg = ReplayGain.ScalarLinear(track.Loudness, _norm, _refLufs);
                float delta = rg / track.BakedRg;
                if (delta == track.Delta) continue;
                if (TryEnqueueMixerCmd(new MixerCmd { Kind = CmdSetVoiceGain, Id = id, Linear = track.Preamp * delta, Frames = (int)_plane.DefaultRampSamples }, out _))
                    track.Delta = delta;
                else allAdmitted = false;
            }
        }
        return allAdmitted;
    }

    // Design the live EQ (control thread) and hand it to the active voice as ONE command; publish it for voices built later.
    private bool SendEq(Equalizer eq)
    {
        var design = DesignEq(eq);
        Volatile.Write(ref _eqDesign, design);
        lock (_mixerCmdProducerLock)
        {
            long id = ActiveVoiceIdValue;
            if (!_voiceGain.TryGetValue(id, out var track)) return true;   // no live voice yet: BuildVoiceChain seeds it from the design
            var cmd = new MixerCmd
            {
                Kind = CmdSetEq, Id = id, Bands = design.Bands, Coeffs = design.Coeffs,
                Linear = design.Preamp * track.Delta, Frames = (int)_plane.DefaultRampSamples,
            };
            if (!TryEnqueueMixerCmd(cmd, out _)) return false;
            track.Preamp = design.Preamp;
            return true;
        }
    }

    // E-4: the band set, its coefficients and the preamp (−max(0, largest band gain) dB: a boosted cascade never reaches the
    // limiter hotter than the unboosted signal). EQ off, or no bands, designs to the identity (preamp 1).
    private EqDesign DesignEq(Equalizer eq)
    {
        var source = eq.Bands;
        if (!eq.Enabled.Peek() || source.Length == 0) return EqDesign.Identity;
        int n = Math.Min(source.Length, EqStage.MaxBands);
        var bands = new BiquadBand[n];
        float maxGainDb = 0f;
        for (int i = 0; i < n; i++)
        {
            var b = source[i];
            bands[i] = new BiquadBand(b.Type, b.FreqHz.Peek(), b.Q.Peek(), b.GainDb.Peek());
            if (bands[i].GainDb > maxGainDb) maxGainDb = bands[i].GainDb;
        }
        return new EqDesign(bands, EqStage.Design(bands, _format.SampleRate), LimiterStage.DbToLinear(-maxGainDb));
    }

    private bool BandGainsChanged(Equalizer eq)
    {
        var bands = eq.Bands;
        for (int i = 0; i < bands.Length && i < _lastBandGains.Length; i++)
            if (bands[i].GainDb.Peek() != _lastBandGains[i]) return true;
        return false;
    }

    private void SnapshotBandGains(Equalizer eq)
    {
        if (_lastBandGains.Length != eq.Bands.Length) _lastBandGains = new float[eq.Bands.Length];
        for (int i = 0; i < eq.Bands.Length; i++) _lastBandGains[i] = eq.Bands[i].GainDb.Peek();
    }

    private static long EqTopologySignature(Equalizer eq)
    {
        long h = eq.Enabled.Peek() ? 1 : 0;
        var bands = eq.Bands;
        h = h * 31 + bands.Length;
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            h = h * 1000003 + (byte)b.Type;
            h = h * 1000003 + BitConverter.SingleToInt32Bits(b.FreqHz.Peek());
            h = h * 1000003 + BitConverter.SingleToInt32Bits(b.Q.Peek());
        }
        return h;
    }

    private void PublishVisualizer()
    {
        if (_liveEffects is not AudioEffects ae) return;
        if (_tap.TryRead(out float rms, out float peak, out long epoch, out long version) && version != _tapReadVersion)
        {
            _tapReadVersion = version;
            ae.PublishVisualizerFrame(Volatile.Read(ref _visualizerSource), epoch, rms, peak);
        }
        PublishSpectrum(ae);
    }

    /// <summary>Control thread (~60 Hz, Playing only — the same cadence as the level publish): analyse the window that is
    /// AUDIBLE now and publish the bands. Allocation is legal HERE (first-lease arming, a sample-rate change); the steady
    /// state allocates nothing. Alignment is done in the CONTENT domain: the ring is indexed by
    /// <c>BlockCtx.StartFrame</c> (the mixer's <c>ConsumeSeq</c> at the block start) and the audible frame is
    /// <see cref="PlayedFrames"/> brought into that domain, minus the endpoint's measured latency (NOT the master chain's: the tap
    /// is downstream of it), minus the user offset, plus half a window so the window is CENTRED on the audible instant. A negative
    /// user offset saturates at the newest rendered sample.</summary>
    private void PublishSpectrum(AudioEffects ae)
    {
        long source = Volatile.Read(ref _visualizerSource);
        long epoch = ae.SpectrumDemand(source);
        if (epoch == 0) return;
        int rate = _format.SampleRate;
        var analyzer = _spectrumAnalyzer;
        float[] window = _spectrumWindow!, bands = _spectrumBands!;   // all three are created together, below
        if (analyzer is null || analyzer.SampleRate != rate)
        {
            analyzer = _spectrumAnalyzer = new SpectrumAnalyzer(rate);
            window = _spectrumWindow = new float[analyzer.FftSize];
            bands = _spectrumBands = new float[analyzer.BandCount];
            _onsets = new OnsetDetector(analyzer.BandCount);
        }
        var ring = _spectrumRing;
        if (ring is null)
        {
            ring = new SpectrumRing((int)(rate * SpectrumRingSeconds), _maxBlock);
            Volatile.Write(ref _spectrumRing, ring);      // the RT starts filling (and arms) on its next block
            return;
        }
        // PlayedFrames is the CONTENT clock (F2: the silence the device already played is subtracted), and it counts frames since
        // the endpoint epoch, which restarts at 0 on RebuildSink while the mixer's ConsumeSeq does not (CmdReset resets both
        // together). The content-domain offset is therefore ConsumeSeq − (content submitted) − (the rendered-but-unsubmitted
        // CONTENT remainder): 0 in the common case, and unchanged by starvation — the silence F2 submitted counts in neither
        // ConsumeSeq nor the content clock, so the window holds on the last real frames and no re-arm fires.
        long pendingContent = _pendingSilence ? 0 : Volatile.Read(ref _pendingFrames);
        long domainOffset = ConsumeSeqFrames - ContentSubmittedFrames() - pendingContent;
        // The spectrum tap sits AFTER the master chain's EQ + lookahead limiter (D7) and the ring is keyed by output frame, so the
        // graph's own latency is already inside the ring's frame index: only the endpoint's latency lies between the tap and the ear.
        long audible = PlayedFrames + domainOffset - _clock.StreamLatencyFrames;
        long offsetFrames = (long)Math.Round(ae.SpectrumOffsetMs * rate / 1000.0);
        long newest = ring.NewestContent;
        long end = Math.Min(audible - offsetFrames + analyzer.FftSize / 2, newest);
        if (!ring.TryCopyContentWindow(end, window)) { _onsets?.Reset(); return; }   // a gap: the next frame must not read as an onset   // not yet filled after an arm, lapped, torn or re-armed: skip this tick
        long t0 = Stopwatch.GetTimestamp();
        analyzer.Analyze(window, bands);
        float fftMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        double sumSq = 0;
        foreach (float v in window) sumSq += (double)v * v;
        float windowRms = (float)Math.Sqrt(sumSq / window.Length);   // PRE-gain: the tap sits before _masterGain
        bool muted = _muted || _volume <= 0.0005f;
        // Flux/onset from consecutive band frames; the waveform is the CENTRE of the same aligned window (no extra copy here).
        bool onset = _onsets!.Step(bands, t0 * 1000.0 / Stopwatch.Frequency, out float flux, out float onsetStrength);
        int wave = Math.Min(AudioEffects.WaveformSamples, window.Length);
        var centre = window.AsSpan((window.Length - wave) / 2, wave);
        if (ae.PublishSpectrum(source, epoch, bands, muted, windowRms, newest - end, fftMs, centre, rate, flux, onset, onsetStrength))
            Interlocked.Increment(ref _spectrumPublishes);
    }

    // ── IMediaSession ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        _position.Reset();
        sink.PlayRequested(_playRequested);
        Publish(PlaybackState.Opening);
        if (_driveWithOwnThread) StartFeeder();
    }

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        // F5: the RT thread may start rendering (and the device may Start) the moment play is requested and the ring is ready —
        // before the clock thread's next tick — so the one-time page-in pass must already be behind us (see WarmUp).
        if (!_started) WarmUp();
        _playRequested = true;
        _sink?.PlayRequested(true);
        if (!_fadeInSpecified || _transportHoldRequested) FadeIn(TimeSpan.FromMilliseconds(20));
        _deviceController?.Rearm();   // no live endpoint (the retry ladder ran out): Play tries the device again now
        _feed?.WakeOutput();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask PauseAsync()
    {
        if (_disposed) return;
        _playRequested = false;
        _transportHoldRequested = true;
        _fadeInSpecified = false;
        _sink?.PlayRequested(false);
        if (_state is PlaybackState.Playing or PlaybackState.Stalled)
        {
            await FadeOutAsync(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
            if (!_disposed && !_playRequested && Volatile.Read(ref _transportPhase) == 3)
            {
                if (_sink is { } sink) PublishPosition(sink);
                Publish(PlaybackState.Paused);
            }
        }
        else if (_state == PlaybackState.Ready) Publish(PlaybackState.Paused);
    }

    // ── seek (D3) ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // THE DEVICE IS NEVER STOPPED OR RESET (no CmdReset, no Stop): the mixer clock, the submit index and the silence ledger run on, so
    // the spectrum ring's content→index mapping and every clock stay valid across a seek. Three ways to serve a seek, tried in this order:
    //   B  TryJumpWithinRingAsync  — the target lies inside audio the active ring still holds: the RT jumps its cursor and blends 5 ms.
    //   A  SwapToPreparedAsync     — a second decoder (opened by the app on a non-owning view of the bytes) replaces the voice at a block.
    //   —  SeekInPlaceAsync        — the single-decoder fallback: hold the transport at silence, flush + seek the decoder, fade back in.
    // Position follows by RebaseAtSubmit: anchored at the device submit index where the new audio is written (block-exact).

    /// <summary>A new voice id for a voice the CALLER builds (a scrub grain voice). Engine ids start at 2^32 — far above the app's own
    /// counter and VoiceScheduler's — so they never collide with a voice id handed out elsewhere for the same session.</summary>
    public long NextEngineVoiceId() => Interlocked.Increment(ref _nextEngineVoiceId);

    /// <summary>True once the CURRENT seek's design-A swap has landed. Cleared when the next seek begins (<see cref="TryJumpWithinRingAsync"/>,
    /// <see cref="SeekInPlaceAsync"/>, <see cref="SeekAsync"/>) and at the start of <see cref="SwapToPreparedAsync"/> — never sticky across
    /// seeks. The pump's stale timer asks it before silencing the old voice.</summary>
    public bool SwapLanded => Volatile.Read(ref _swapLanded) != 0;

    /// <summary>The seek generation the last landed swap carried (diagnostics; the engine does not interpret it).</summary>
    public long LastSeekGeneration => Interlocked.Read(ref _lastSeekGeneration);

    /// <summary>The ring of the ACTIVE voice (the one transport addresses), or null when there is none.</summary>
    private RingAudioSource? ActiveRing()
    {
        if (_feed is { } feed)
            foreach (var entry in feed.RingsSnapshot)
                if (entry.VoiceId == ActiveVoiceIdValue) return entry.Ring;
        return _voice as RingAudioSource;
    }

    private long SeekFrameFor(TimeSpan to)
    {
        double hi = _duration > TimeSpan.Zero ? _duration.TotalSeconds : double.MaxValue;
        return (long)Math.Round(Math.Clamp(to.TotalSeconds, 0, hi) * _format.SampleRate);
    }

    /// <summary>Seek B: post the ABSOLUTE content frame <paramref name="targetFrame"/>; the RT decides (it owns the ring cursor) and
    /// publishes accepted / refused. Accepted when the target lies inside the intact kept-behind span or the decoded ahead span minus one
    /// fade and one block; the position is rebased only on accept. Applies with the transport running, paused or not yet started. Returns
    /// false when there is no RT feed, no active voice, or the target is out of reach.</summary>
    public async ValueTask<bool> TryJumpWithinRingAsync(long targetFrame, CancellationToken ct)
    {
        Volatile.Write(ref _swapLanded, 0);   // a new seek begins
        if (_disposed || _feed is null) return false;
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await JumpWithinRingLockedAsync(targetFrame, ct).ConfigureAwait(false); }
        finally { _replacementGate.Release(); }
    }

    // Seek B with the replacement gate held (the gate also keeps PublishPosition from sampling between the jump and the rebase).
    private async ValueTask<bool> JumpWithinRingLockedAsync(long targetFrame, CancellationToken ct)
    {
        if (_disposed || _feed is null || ActiveRing() is null) return false;
        Volatile.Write(ref _jumpVerdict, 0);
        long seq = await PostMixerCommandAsync(new MixerCmd { Kind = CmdJumpWithinRing, Id = ActiveVoiceIdValue, Position = targetFrame, Frames = _fade5Frames }, ct)
            .ConfigureAwait(false);
        await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
        if (Volatile.Read(ref _jumpVerdict) != 1) return false;
        RebaseAtSubmit(Volatile.Read(ref _jumpSubmitIndex), targetFrame);
        _sink?.Position(TimeSpan.FromSeconds((double)targetFrame / _format.SampleRate));
        // A transport hold an earlier (superseded) in-place seek left behind is over: the position is where the listener will be.
        if (_playRequested && !_transportHoldRequested && Volatile.Read(ref _transportPhase) == 4) FadeIn(TimeSpan.FromMilliseconds(5));
        return true;
    }

    /// <summary>Seek A: a prepared voice at the target (opened by the pump through <c>PrepareAtAsync</c> on a non-owning byte-source view)
    /// replaces the active voice under a 5 ms equal-power pair, applied as ONE compound command on the RT — the RT resolves "the next block"
    /// itself, so no block renders with half a swap. The old voice keeps playing at unity until that block (then fades under it, or is cut
    /// when paused / starved / parked / dry). The device is never stopped. Works with the transport running, paused or not yet started.
    /// <para>Returns the prepared item's <c>StartPositionFrames</c> — the achieved CONTENT frame. Before the command is admitted any failure
    /// (a mismatched voice, a cancelled token, disposal) throws and leaves the live voice untouched; once it is admitted the swap
    /// completes regardless of <paramref name="ct"/> (a newer seek waits its turn on the gate). On success the prepared item's ring belongs
    /// to the mixer (<see cref="AudioPreparedItem.TransferOwnership"/>).</para></summary>
    public async ValueTask<long> SwapToPreparedAsync(IPreparedItem prepared, long seekGeneration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.MixRate != Format.SampleRate || prepared.AudioVoice is not RingAudioSource ring || prepared is not AudioPreparedItem audio)
            throw new InvalidOperationException("The prepared voice does not match this session's format.");
        if (_disposed) throw new ObjectDisposedException(nameof(PcmAudioSession));
        Volatile.Write(ref _swapLanded, 0);
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await WaitRingReadyAsync(ring, RenderBlockFrames, ct).ConfigureAwait(false);   // ONE block of post-seek PCM (V-PE14)
            ArmSeekRebufferSuppression();
            long oldId = ActiveVoiceIdValue;
            long id = NextEngineVoiceId();
            IAudioSource inner = _feed is not null ? _feed.WrapAdditional(ring, id) : ring;   // publishes the ring to the feed (its producer is already running)
            var src = new WsolaAudioSource(inner, _format.SampleRate, _format.Channels) { Rate = PlaybackRate };
            var chain = BuildVoiceChain();
            float scalar = ReplayGainScalarFor(audio.Loudness);
            var cmd = new MixerCmd
            {
                Kind = CmdSwapVoice, Id = oldId, Frames = _fade5Frames,
                Env = NewFade(FadeKind.Out, _fade5Frames),
                Voice = new MixVoice
                {
                    Id = id, Src = src, Env = NewFade(FadeKind.In, _fade5Frames), StartFrame = -1,   // start stamped on the RT
                    ReplayGainScalar = scalar, Chain = chain,
                },
            };
            long seq;
            try { seq = await PostMixerCommandAsync(cmd, ct).ConfigureAwait(false); }
            catch { _feed?.EnqueueRetire(ring); throw; }   // not admitted: the published ring must not outlive the failed swap
            await WaitAppliedAsync(seq, CancellationToken.None).ConfigureAwait(false);   // admission transfers ownership: ct no longer applies
            audio.TransferOwnership();

            lock (_mixerCmdProducerLock)
                _voiceGain[id] = new VoiceGainTrack { Preamp = FindGain(chain)?.CurrentGain ?? 1f, BakedRg = scalar > 0f ? scalar : 1f, Loudness = audio.Loudness };
            _rateSources[id] = src;
            _voiceStarts[id] = Volatile.Read(ref _swapAtFrame);
            SetActiveVoice(id, ring, prepared.Duration, prepared.TotalFrames);
            _eqDirty = true;   // an EQ edit made while the new voice was being built went to the old id: re-send the freshest design to the new active voice

            long achieved = audio.StartPositionFrames;
            RebaseAtSubmit(Volatile.Read(ref _swapSubmitIndex), achieved);
            _sink?.Position(TimeSpan.FromSeconds((double)achieved / _format.SampleRate));
            Interlocked.Exchange(ref _lastSeekGeneration, seekGeneration);
            Volatile.Write(ref _swapLanded, 1);
            return achieved;
        }
        finally { _replacementGate.Release(); }
    }

    /// <summary>The seek stale-fade (V-PE14): the pump's prepare has produced no block in time, so the OLD voice — still playing a position
    /// the user already left — fades to silence over <paramref name="duration"/> and is PARKED: never retired (its ring and byte source stay
    /// alive for the prepare that is reading the same bytes), never read, never waited for. Installing an envelope on it
    /// (<see cref="SetVoiceEnvelope"/>) restores it; a swap replaces it. Takes the replacement gate, so it can never silence a NEW voice: when
    /// the swap has landed first (<see cref="SwapLanded"/>) it does nothing.</summary>
    public async ValueTask FadeActiveToSilenceAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (_disposed) return;
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || SwapLanded) return;
            int frames = Math.Max(1, (int)Math.Round(duration.TotalSeconds * _format.SampleRate));
            long seq = await PostMixerCommandAsync(new MixerCmd { Kind = CmdSilenceVoice, Id = ActiveVoiceIdValue, Frames = frames, Env = NewFade(FadeKind.Out, frames) }, ct)
                .ConfigureAwait(false);
            await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
        }
        finally { _replacementGate.Release(); }
    }

    // ── scrub (D4): hold the main voice, play grains from a grain voice, give the main voice back ──────────────────────────
    // The grain voice is built by the CALLER (the app's ScrubGrainSource over a second decoder, wrapped in a RingAudioSource so the grain
    // decode runs on the ring's producer thread and the RT only copies). The session adds it, holds the main voice, and later releases:
    //   cancel   CancelScrubAsync       — ONE command: the main voice fades back in where the hold left it, the grain voice fades out.
    //   commit   ReleaseHeldSilentAsync — the grain voice fades out; the main voice stays PARKED at gain 0 because the release is the first
    //            half of a seek, served by SeekAsync's paths (ring jump / design-A swap / in-place) — see RtReleaseVoice for why parked.
    // A scrub grain voice is audible audio like any other: it passes through the master chain and the spectrum tap, and — not being a
    // tracked voice — never receives the EQ/normalization gain commands that address the active voice.

    /// <summary>The scrub level (−6 dB, D4), applied through the grain voice's GAIN SLOT — never the normalization scalar.</summary>
    public const float ScrubGainLinear = 0.501f;

    /// <summary>Scrub begin: ONE command holds the active (main) voice — a 20 ms fade-out, then parked — and adds <paramref name="grainVoice"/>
    /// under <paramref name="grainVoiceId"/> with a 20 ms fade-in and its gain slot at <see cref="ScrubGainLinear"/> (× the live EQ
    /// preamp, the part of the slot the scrub level must not replace). The main voice keeps its ring, its decoder and its byte source; it is
    /// neither read nor retired while held, and nothing waits on it. Build the voice with an id from <see cref="NextEngineVoiceId"/>.
    /// <para>Ready the grain voice first: a <see cref="RingAudioSource"/> that has not produced yet would starve the mixer the moment the main
    /// voice leaves it. With a feed attached the voice is published to it (<see cref="AudioFeedThread.WrapAdditional"/>) and a non-ring source
    /// is wrapped in a ring; the voice retires itself through its fade-out envelope (no <see cref="RemoveVoiceAsync"/>).</para>
    /// <para>Throws when the voice cannot be held — a disposed session, or no active voice / one already held — after retiring the grain voice
    /// (its source is disposed by the ring's retire path), so the caller need not unwind the mixer. Once the command is admitted
    /// <paramref name="ct"/> no longer applies.</para></summary>
    public async ValueTask BeginScrubAsync(IAudioSource grainVoice, long grainVoiceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grainVoice);
        if (_disposed) throw new ObjectDisposedException(nameof(PcmAudioSession));
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(PcmAudioSession));
            IAudioSource src = _feed is not null ? _feed.WrapAdditional(grainVoice, grainVoiceId) : grainVoice;   // publishes the ring to the feed
            long seq;
            try
            {
                int fade = _scrubFadeFrames;
                float preamp = Volatile.Read(ref _eqDesign)?.Preamp ?? 1f;
                Volatile.Write(ref _scrubVerdict, 0);
                seq = await PostMixerCommandAsync(new MixerCmd
                {
                    Kind = CmdHoldVoice, Id = ActiveVoiceIdValue, Frames = fade, Linear = ScrubGainLinear * preamp,
                    Env = NewFade(FadeKind.Out, fade),                                                      // the main voice's Out
                    Voice = new MixVoice
                    {
                        Id = grainVoiceId, Src = src, Env = NewFade(FadeKind.In, fade), StartFrame = -1,    // start stamped on the RT
                        ReplayGainScalar = 1f, Chain = BuildVoiceChain(),
                    },
                }, ct).ConfigureAwait(false);
            }
            catch { RetireScrubVoice(src); throw; }   // not admitted: the published ring must not outlive the failed begin
            await WaitAppliedAsync(seq, CancellationToken.None).ConfigureAwait(false);   // admission transfers ownership: ct no longer applies
            if (Volatile.Read(ref _scrubVerdict) != 1)
            {
                RetireScrubVoice(src);   // the RT refused: the grain voice never reached the mixer
                throw new InvalidOperationException("The active voice cannot be held for a scrub.");
            }
        }
        finally { _replacementGate.Release(); }
    }

    // A grain voice that never reached (or has left) the mixer: hand its ring to the worker for off-RT disposal, or dispose it inline on the
    // single-thread pull path.
    private void RetireScrubVoice(IAudioSource src)
    {
        if (_feed is not null) { if (SourceRing(src) is { } ring) _feed.EnqueueRetire(ring); }
        else (src as IDisposable)?.Dispose();
    }

    /// <summary>Scrub cancel: ONE command un-holds the main voice with a 20 ms fade-in — it resumes exactly where the hold left its content
    /// cursor — while the grain voice fades out over the same 20 ms and retires itself. The position is rebased to the resume point at the
    /// device submit index the main voice returns at. Idempotent: with no held voice (never held, already released, a second call) it does
    /// nothing and the grain voice is left to the fade-out it already has.</summary>
    public async ValueTask CancelScrubAsync(long grainVoiceId, CancellationToken ct)
    {
        if (_disposed) return;
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            int fade = _scrubFadeFrames;
            Volatile.Write(ref _scrubVerdict, 0);
            long seq = await PostMixerCommandAsync(new MixerCmd
            {
                Kind = CmdReleaseVoice, Id = ActiveVoiceIdValue, Position = grainVoiceId, Frames = fade,
                Env = NewFade(FadeKind.In, fade), Env2 = NewFade(FadeKind.Out, fade),
            }, ct).ConfigureAwait(false);
            await WaitAppliedAsync(seq, CancellationToken.None).ConfigureAwait(false);
            if (Volatile.Read(ref _scrubVerdict) != 1) return;   // nothing was held
            long position = Volatile.Read(ref _releaseFrame);
            RebaseAtSubmit(Volatile.Read(ref _releaseSubmitIndex), position);
            _sink?.Position(TimeSpan.FromSeconds((double)position / _format.SampleRate));
        }
        catch (ObjectDisposedException) when (_disposed) { }
        finally { _replacementGate.Release(); }
    }

    /// <summary>Scrub commit, first half: the grain voice fades out over 20 ms and retires itself; the MAIN voice stays held — parked at gain 0,
    /// alive, not retired — for the seek the caller issues next (<see cref="SeekAsync"/> / <see cref="TryJumpWithinRingAsync"/> /
    /// <see cref="SwapToPreparedAsync"/> / <see cref="SeekInPlaceAsync"/>). That is the state <see cref="FadeActiveToSilenceAsync"/> leaves, so
    /// the same contract applies: a design-A swap replaces (cuts) it; after a ring jump or an in-place seek the caller gives it its level back
    /// by installing an envelope on it (<see cref="SetVoiceEnvelope"/> — installing one ends the hold), e.g. a short fade-in, or unwinds with
    /// <see cref="CancelScrubAsync"/> (which fades it in where it was held). Until then the track is silent and the voice's ring cursor does
    /// not move. Safe to repeat. The seek that follows needs nothing else from the engine: its fade-out hold and fade-in act on the whole
    /// mix as ever (the hold leaves the transport running, so the in-place fallback fades the mix out itself).</summary>
    public async ValueTask ReleaseHeldSilentAsync(long grainVoiceId, CancellationToken ct)
    {
        if (_disposed) return;
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            long seq = await PostMixerCommandAsync(new MixerCmd
            {
                Kind = CmdReleaseVoice, Id = ActiveVoiceIdValue, Position = grainVoiceId, Frames = 0,
                Env2 = NewFade(FadeKind.Out, _scrubFadeFrames),
            }, ct).ConfigureAwait(false);
            await WaitAppliedAsync(seq, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed) { }
        finally { _replacementGate.Release(); }
    }

    /// <summary>Seek fallback (no decoder lease free, or the swap failed): the single-decoder "seek marker". While the listener hears the
    /// old audio the transport fades out and HOLDS in phase 4 (gain 0, still rendering — never phase 3, which renders nothing and stops the
    /// device); the decoder seeks only after the fade has ENDED; the position is rebased at the first content block; the transport fades
    /// back in. The device is never stopped or reset. When nothing is audible (paused, not started, a hold already in place) it degrades
    /// to a plain flush-and-seek with no fade. Returns the achieved CONTENT frame.
    /// <para>Every wait is bounded by <paramref name="ct"/>, the seek revision (a newer seek supersedes this one — it returns without
    /// completing and the newer seek finishes the hold) or session disposal, so a pause or a stalled device mid-seek never hangs it.</para></summary>
    public async ValueTask<long> SeekInPlaceAsync(long targetFrame, CancellationToken ct)
    {
        long revision = Interlocked.Increment(ref _seekRevision);
        Volatile.Write(ref _swapLanded, 0);
        await _replacementGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SeekInPlaceLockedAsync(targetFrame, revision, ct).ConfigureAwait(false); }
        finally { _replacementGate.Release(); }
    }

    // The in-place seek with the replacement gate held.
    private async ValueTask<long> SeekInPlaceLockedAsync(long targetFrame, long revision, CancellationToken ct)
    {
        if (_disposed || revision != Interlocked.Read(ref _seekRevision)) return targetFrame;
        if (ActiveRing() is not { } ring) throw new InvalidOperationException("No active ring.");
        ArmSeekRebufferSuppression();
        try
        {
            // 1. Silence — only when the listener hears the old audio now. A transport that is paused, not started or already held at
            //    silence (a stale hold, a superseded seek) has nothing to fade; asking it to would strand it in phase 1.
            if (_started && _playRequested && !_transportHoldRequested && Volatile.Read(ref _transportPhase) == 0)
            {
                long hold = await PostMixerCommandAsync(new MixerCmd { Kind = CmdFadeOutHold, Frames = _fade5Frames }, ct).ConfigureAwait(false);
                await WaitAppliedAsync(hold, ct).ConfigureAwait(false);
            }
            // 2. The fade has ENDED (phase 1 → 4; a pause that took over lands in 2/3 and also ends the wait). A pause arriving here
            //    clears the hold, so phase 4 may never come: the wait is bounded by the revision, the deadline and the device state.
            long fadeDeadline = Environment.TickCount64 + FadeHoldWaitMs;
            await UntilAsync(_phaseWake, () => Volatile.Read(ref _transportPhase) != 1 || !_started
                || revision != Interlocked.Read(ref _seekRevision) || Environment.TickCount64 >= fadeDeadline, ct).ConfigureAwait(false);
            if (revision != Interlocked.Read(ref _seekRevision)) return targetFrame;

            // 3. The decoder seeks on its producer; the RT flushes the pre-seek PCM on its next wake. The voice's rate/lookahead restarts at
            //    the achieved frame RIGHT AWAY, before the ring refills: until the flush is consumed the pending-flush flag keeps the mixer
            //    from reading the ring, and after it the ring is empty — and the F2 resume gate wants a whole cushion — so no post-seek frame
            //    can be consumed under the stale lookahead, whether or not the transport is rendering (it may not be: paused / starting).
            long achieved = await ring.SeekFrameAsync(targetFrame).WaitAsync(ct).ConfigureAwait(false);
            if (revision != Interlocked.Read(ref _seekRevision)) return achieved;
            long rateReset = await PostMixerCommandAsync(new MixerCmd { Kind = CmdResetRate, Id = ActiveVoiceIdValue, Position = achieved }, ct).ConfigureAwait(false);
            await WaitAppliedAsync(rateReset, ct).ConfigureAwait(false);

            // 4. Real post-seek audio: the resume cushion (the F2 gate would hold the transport behind it anyway — waiting here avoids a
            //    starve the moment the fade-in starts). Never more than the ring will ever hold.
            int block = RenderBlockFrames;
            int ready = Math.Min(Math.Max(_resumeFrames, block), Math.Max(block, ring.TargetFrames));
            if (!await WaitRingReadyOrSupersededAsync(ring, ready, revision, ct).ConfigureAwait(false)) return achieved;

            // 5. The position anchors where the first content block lands: silence (F2, the hold) never moves the content clock, so the
            //    content index of the first new frame is exact.
            RebaseAtSubmit(SubmittedFrames + Volatile.Read(ref _pendingFrames), achieved);
            _sink?.Position(TimeSpan.FromSeconds((double)achieved / _format.SampleRate));

            // 6. Resume — only if the user still wants sound (a pause during the seek keeps the transport paused).
            bool resume = _playRequested && !_transportHoldRequested;
            if (resume)
            {
                await FadeInAppliedAsync(TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
                if (_started)
                {
                    // The seek rebuffer is over once the transport has resumed: until then the exemption keeps the resume from counting as
                    // a starvation incident (which would double the cushion). Bounded: the revision, the transport state, the deadline.
                    long resumeDeadline = Environment.TickCount64 + RingRefillTimeoutMs;
                    await UntilAsync(_phaseWake, () => _starvationPhase == 0 || Volatile.Read(ref _transportPhase) != 0
                        || revision != Interlocked.Read(ref _seekRevision) || Environment.TickCount64 >= resumeDeadline, ct).ConfigureAwait(false);
                }
            }
            if (revision == Interlocked.Read(ref _seekRevision)) _seekRebufferActive = false;
            return achieved;
        }
        catch when (!_disposed)
        {
            // A failed or cancelled seek must not leave the transport held at silence: give it back (the old voice plays on where it was).
            try
            {
                if (_playRequested && !_transportHoldRequested && Volatile.Read(ref _transportPhase) is 1 or 4) FadeIn(TimeSpan.FromMilliseconds(5));
            }
            catch { /* best effort — the original failure is what surfaces */ }
            throw;
        }
    }

    // FadeIn that waits until the RT has applied it (so a following phase test sees phase 0, not the stale hold).
    private async ValueTask FadeInAppliedAsync(TimeSpan duration, CancellationToken ct)
    {
        Interlocked.Increment(ref _transportRevision);
        _transportHoldRequested = false;
        _fadeInSpecified = true;
        long seq = await PostMixerCommandAsync(new MixerCmd { Kind = CmdFadeIn, Frames = Math.Max(1, (int)Math.Round(duration.TotalSeconds * _format.SampleRate)) }, ct)
            .ConfigureAwait(false);
        await WaitAppliedAsync(seq, ct).ConfigureAwait(false);
    }

    // Wait (signalled, never polled) until the ring holds ≥ minFrames of post-flush PCM (or its producer is done). Returns false when a newer
    // seek superseded this one or the session ended; a producer fault surfaces as it does from WaitRingReadyAsync.
    private async ValueTask<bool> WaitRingReadyOrSupersededAsync(RingAudioSource ring, int minFrames, long revision, CancellationToken ct)
    {
        Volatile.Write(ref ring.ReadyMinimum, minFrames);
        while (!_disposed && revision == Interlocked.Read(ref _seekRevision))
        {
            ring.ReadyWake.Reset();
            if (ring.IsReady(minFrames))
            {
                if (ring.ProducerFault is { } fault) throw fault;
                return true;
            }
            await WaitAsync(ring.ReadyWake, 20, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Anchor the derived position at the device submit index where the change becomes audible: the block written at
    /// <paramref name="submitIndex"/> is heard when the device's played count reaches it. The position's played-frame domain is the CONTENT
    /// clock (the raw device count — which already folds in <c>_deviceFrameOrigin</c> — minus the silence it has played), so the anchor is
    /// converted with <see cref="ContentIndexAt"/>; the origin is NOT subtracted again. Block-exact (≤ one block), not sample-exact.</summary>
    private void RebaseAtSubmit(long submitIndex, long positionFrames)
    {
        _activeMixerStart = 0;
        _position.Rebase(ContentIndexAt(submitIndex), positionFrames);
        _clockAnchorPosition = positionFrames;
        _sink?.SettleTransport();
    }

    /// <summary>The IMediaSession seek, the dispatcher (V-PE16): with no RT feed the single-thread direct path; otherwise B (jump inside the
    /// ring) then the in-place fallback. Design A (a second decoder) is driven by the app's pump through
    /// <see cref="SwapToPreparedAsync"/>, because only the app owns the byte source. Arms the xrun suppression synchronously.</summary>
    public async ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return;
        long revision = Interlocked.Increment(ref _seekRevision);
        ArmSeekRebufferSuppression();
        if (_feed is null) { await SeekDirectAsync(to, revision).ConfigureAwait(false); return; }
        Volatile.Write(ref _swapLanded, 0);
        await _replacementGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || revision != Interlocked.Read(ref _seekRevision)) return;
            long frame = SeekFrameFor(to);
            if (await JumpWithinRingLockedAsync(frame, CancellationToken.None).ConfigureAwait(false)) { _seekRebufferActive = false; return; }   // no rebuffer: the ring was not touched
            await SeekInPlaceLockedAsync(frame, revision, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _replacementGate.Release(); }
    }

    // The single-thread pull path (no RT feed): control IS the render thread, so the seek is the classic fade → reset → anchor → decoder seek.
    private async ValueTask SeekDirectAsync(TimeSpan to, long revision)
    {
        await _replacementGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || revision != Interlocked.Read(ref _seekRevision)) return;
            long frame = SeekFrameFor(to);
            await FadeOutAsync(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
            if (_disposed || revision != Interlocked.Read(ref _seekRevision)) return;
            long reset = await PostMixerCommandAsync(new MixerCmd { Kind = CmdReset }, CancellationToken.None).ConfigureAwait(false);
            await WaitAppliedAsync(reset, CancellationToken.None).ConfigureAwait(false);
            long anchor = await PostMixerCommandAsync(new MixerCmd { Kind = CmdSeekAnchor, Id = ActiveVoiceIdValue }, CancellationToken.None).ConfigureAwait(false);
            await WaitAppliedAsync(anchor, CancellationToken.None).ConfigureAwait(false);
            RingAudioSource? ring = _voice as RingAudioSource;   // no feed here: a ring voice is one the caller wrapped itself
            long achieved;
            if (ring is not null)
            {
                achieved = await ring.SeekFrameAsync(frame).ConfigureAwait(false);
                await WaitBufferingReadyAsync(ring, revision).ConfigureAwait(false);
            }
            else
            {
                achieved = await Task.Run(() =>
                {
                    if (_voice is DecoderAudioSource decoder) decoder.SeekFrame(frame);
                    else if (_voice is TrimmingSource trimmed) trimmed.SeekFrame(frame);
                    else if (_voice is MemoryAudioSource memory) memory.SeekFrame(frame);
                    else throw new NotSupportedException("The active source does not support seeking.");
                    return _voice.PositionFrames;
                }).ConfigureAwait(false);
            }
            if (_disposed || revision != Interlocked.Read(ref _seekRevision)) return;
            long rateReset = await PostMixerCommandAsync(new MixerCmd
                { Kind = CmdResetRate, Id = ActiveVoiceIdValue, Position = achieved }, CancellationToken.None).ConfigureAwait(false);
            await WaitAppliedAsync(rateReset, CancellationToken.None).ConfigureAwait(false);
            _activeMixerStart = 0;
            _position.Reset();
            _position.Rebase(0, achieved);
            _clockAnchorPosition = achieved;
            _sink?.Position(TimeSpan.FromSeconds((double)achieved / _format.SampleRate));
            _sink?.SettleTransport();
            _seekRebufferActive = false;
            if (_playRequested)
            {
                FadeIn(TimeSpan.FromMilliseconds(5));
                Publish(PlaybackState.Ready);
            }
            else Publish(PlaybackState.Paused);
        }
        finally { _replacementGate.Release(); }
    }

    // Wait (signalled, never polled) until the seeked ring holds the startup cushion, its producer is done, a newer seek superseded
    // this one, or the session is disposed — the former timer-polled loop's exact condition, now on the ring's own ReadyWake.
    private async ValueTask WaitBufferingReadyAsync(RingAudioSource ring, long revision)
    {
        Volatile.Write(ref ring.ReadyMinimum, Math.Min(StartupReadinessFrames, ring.TargetFrames));
        while (!_disposed && revision == Interlocked.Read(ref _seekRevision))
        {
            ring.ReadyWake.Reset();
            if (BufferingReady() || ring.Exhausted) return;
            await WaitAsync(ring.ReadyWake, 20, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void SetRate(double rate)
    {
        if (_disposed) return;
        Volatile.Write(ref _rate, WsolaAudioSource.ClampRate(rate));
        _feed?.WakeOutput();
    }
    /// <inheritdoc/>
    public void SetVolume(double volume) { if (!_disposed) _volume = (float)Math.Clamp(volume, 0, 1); }
    /// <inheritdoc/>
    public void SetMuted(bool muted) { if (!_disposed) { _muted = muted; _sink?.Muted(muted); } }

    /// <inheritdoc/>
    public VideoDelivery Video => VideoDelivery.None;

    // ── the pump (spec M2: control thread pumps the graph, writes the sink) ──────────────────────────────────────────

    /// <summary>Advance the session one pump: drive the state machine and, while Playing, render + present one block, then
    /// derive + publish the clock position. The deterministic headless op ("pull N frames") — the single-thread pull path.
    /// Returns the new state. (M4: the RT flip splits this into <see cref="TickControl"/> + <see cref="RtRenderOnce"/>.)</summary>
    public PlaybackState PumpAudio(int frames) => Advance(frames, renderInline: true);

    /// <summary>M4 control/clock tick (spec §7.6/§7.9): drive the state machine, reconcile effects, and — while Playing —
    /// sample the clock + publish <c>Position</c>. Does NOT render (the RT feed thread owns <see cref="RtRenderOnce"/>).
    /// Runs off the RT thread. On the single-thread path use <see cref="PumpAudio"/> instead.</summary>
    public PlaybackState TickControl(int frames) => Advance(frames, renderInline: false);

    /// <summary>True when a control tick can change nothing until a transport command arrives: nothing is playing or requested, the
    /// device is stopped and no seek-rebuffer window is open. The clock thread then blocks on its wake event
    /// (<see cref="AudioFeedThread.WakeOutput"/>) instead of ticking every 15 ms; every command that matters wakes it.</summary>
    internal bool ControlIdle => !_playRequested && !_started && !_seekRebufferActive
        && _state is PlaybackState.Paused or PlaybackState.Ended or PlaybackState.Ready;

    /// <summary>The listener wants sound: play was requested or the session is playing. The device controller keeps a slow
    /// retry going past its ladder while this holds.</summary>
    internal bool WantsOutput => _playRequested || _state == PlaybackState.Playing;

    /// <summary>M4 RT feed callback (spec §7.9): if Playing, render+present exactly one block through the published graph
    /// (lock-free consume + quarantine) reading pre-decoded PCM from the voice rings — copy+mix ONLY, alloc/lock/syscall-free
    /// (the <see cref="AudioTripwire"/> around <see cref="RenderBlock"/> enforces it). Returns frames presented (0 if not
    /// Playing). This is the ONLY method the MMCSS RT thread runs against the session.</summary>
    public int RtRenderOnce(int frames)
    {
        if (_disposed) return 0;
        DrainMixerCmds();
        if (_sink is null || _formatRequiresReload) return 0;
        int phase = Volatile.Read(ref _transportPhase);

        // H-3 (V-PE8): a fade in flight (phase 1) or draining (phase 2) on a sink that has gone dead can never finish — the drain
        // test needs WritableFrames ≥ CapacityFrames and a dead sink reads −1, and the fade's last block is never accepted. Both used
        // to return before RenderBlock's dead-sink report was reachable, so a pause on a lost device hung forever. Handled BEFORE the
        // phase gate: drop what a dead device will never accept, report the loss ONCE (a typed device-lost report starts the rebuild
        // without waiting out the 8-block threshold — this path never repeats), stop, and call the fade over.
        if (phase is 1 or 2 && _out is IBufferedAudioSink probe && probe.WritableFrames < 0)
        {
            _pendingFrames = _pendingOffset = 0;
            RecordDeviceLost();
            try { _out.Stop(); } catch (AudioDeviceLostException) { }
            _started = false;
            SetPhase(3);
            return 0;
        }

        if (phase == 2)
        {
            if (_pendingFrames > 0) return SubmitPending();
            // RAW played: the content clock excludes starvation silence the device has also played, which would never reach the tail.
            bool drained = _out is IBufferedAudioSink buffered
                ? buffered.WritableFrames >= buffered.CapacityFrames
                : RawPlayedFrames >= _fadeTailSubmitted;
            if (drained)
            {
                try { _out.Stop(); } catch (AudioDeviceLostException) { RecordDeviceLost(); }
                _started = false;
                SetPhase(3);
            }
            return 0;
        }

        // F5 (V-PE29): the RT evaluates "ready to start" ITSELF instead of waiting for the clock thread's 15 ms tick to flip
        // Ready/Buffering → Playing — the first block no longer pays a tick of latency, and a stalled clock thread cannot
        // delay an already-prepared start. The state/flags it reads are volatile (R-11).
        var state = _state;
        bool mayRender = state is PlaybackState.Playing or PlaybackState.Stalled
            || (state is PlaybackState.Ready or PlaybackState.Buffering && _playRequested && !_transportHoldRequested && _mixer.PcmReady(StartupReadinessFrames));
        if (phase == 3 || !mayRender || (!_playRequested && phase is not (1 or 4))) return 0;
        return RenderBlock(frames);
    }

    /// <summary>Wait outside DSP for endpoint capacity or a new transport command. R-3: while the transport is held and the device
    /// stopped there is nothing to render until a control command arrives, so the wait is INFINITE (a negative timeout — the sink
    /// and the null sink both honour it) instead of a 10 ms spin; every command wakes the output wait through
    /// <see cref="AudioFeedThread.WakeOutput"/>.</summary>
    internal void WaitForOutput(WaitHandle controlWake, int timeoutMs)
    {
        if (Volatile.Read(ref _transportPhase) == 3 && !_started) timeoutMs = -1;
        if (_out is IBufferedAudioSink buffered) buffered.WaitForWritable(controlWake, timeoutMs);
        else controlWake.WaitOne(timeoutMs);
    }

    // The render block in frames: the feed's, or on the single-thread pull path the largest block that is still ≤ 10 ms.
    private int RenderBlockFrames => _feed?.BlockFrames ?? Math.Min(_maxBlock, Math.Max(1, _format.SampleRate / 100));

    private int StartupReadinessFrames
    {
        get
        {
            int capacity = _out is IBufferedAudioSink buffered ? buffered.CapacityFrames : 0;
            return Math.Max(_format.SampleRate / 10, capacity + 2 * RenderBlockFrames);
        }
    }

    private bool BufferingReady()
    {
        if (_feed is null && _voice is not RingAudioSource) return true;
        RingAudioSource? ring = _voice as RingAudioSource;
        if (_feed is not null)
            foreach (var entry in _feed.RingsSnapshot)
                if (entry.VoiceId == ActiveVoiceIdValue) { ring = entry.Ring; break; }
        if (ring is null || ring.HasPendingFlush) return false;
        int required = StartupReadinessFrames;
        return ring.BufferedFrames >= Math.Min(required, ring.TargetFrames)
            || ring.ProducerDone && ring.BufferedFrames > 0;
    }

    private bool BufferingReachedEmptyEof()
    {
        RingAudioSource? ring = _voice as RingAudioSource;
        if (_feed is not null)
            foreach (var entry in _feed.RingsSnapshot)
                if (entry.VoiceId == ActiveVoiceIdValue) { ring = entry.Ring; break; }
        if (ring is null) return _voice?.Exhausted == true;
        bool emptyEof = !ring.HasPendingFlush && ring.ProducerDone && ring.BufferedFrames == 0;
        if (emptyEof && ring.ProducerFault is { } failure) ReportBackgroundFault(failure);
        return emptyEof;
    }

    private PlaybackState Advance(int frames, bool renderInline)
    {
        if (_disposed || _sink is null) return _state;
        var sink = _sink;
        frames = Math.Clamp(frames, 1, _maxBlock);

        ReconcileEffects();   // control-thread: fold live effect-signal changes into the graph/plane (spec §7.10)
        if (_replacementGate.CurrentCount != 0) UpdateSeekRebufferSuppression();   // fix 2: clear the seek/flush xrun-suppression window once the ring refills

        switch (_state)
        {
            case PlaybackState.Opening:
                PublishMetadata(sink);
                Publish(PlaybackState.Buffering);
                break;

            case PlaybackState.Buffering:
                if (BufferingReachedEmptyEof())
                {
                    _playRequested = false;
                    Volatile.Write(ref _startRequested, 0);
                    sink.PlayRequested(false);
                    Publish(PlaybackState.Ended);
                }
                else if (BufferingReady())
                {
                    sink.Buffer(new BufferHealth(Array.Empty<TimeRange>(),
                        _duration < TimeSpan.FromSeconds(30) ? _duration : TimeSpan.FromSeconds(30), false, StallPolicy.Rebuffer));
                    Publish(PlaybackState.Ready);
                    if (_playRequested && !_transportHoldRequested) { EnsureStarted(); Publish(PlaybackState.Playing); }
                }
                // F5: the RT may already be rendering (and the device started) while the state is still Ready/Buffering —
                // the position and meters must follow the audio, not the state flip.
                if (_started) { PublishPosition(sink); PublishVisualizer(); }
                break;

            case PlaybackState.Ready:
            case PlaybackState.Paused:
                if (_playRequested && !_transportHoldRequested) { EnsureStarted(); Publish(PlaybackState.Playing); }
                if (_started) { PublishPosition(sink); PublishVisualizer(); }
                break;

            case PlaybackState.Stalled:
                if (renderInline) RtRenderOnce(frames);
                PublishPosition(sink);   // the content clock holds through the silence, so the position holds with it
                PublishVisualizer();     // the level meter sees the dropout (the RT feeds it the silence block)
                if (_starvationPhase == 0 && _playRequested && !_transportHoldRequested) Publish(PlaybackState.Playing);
                else if (!_playRequested && _transportPhase == 3) Publish(PlaybackState.Paused);
                break;

            case PlaybackState.Playing:
                if (_starvationPhase != 0) { Publish(PlaybackState.Stalled); break; }
                if (!_playRequested && Volatile.Read(ref _transportPhase) == 3) { PublishPosition(sink); Publish(PlaybackState.Paused); break; }
                if (renderInline) RtRenderOnce(frames);   // single-thread path; RT path renders on the feed thread instead
                DecayResumeCushion();
                PublishPosition(sink);
                PublishVisualizer();
                // RT path: read the RT-published drained flag (never the render-thread-owned voice list) — RenderBlock
                // now publishes it even on a voiceless tick (see DrainVerdict), so a fully-drained RT session is never
                // stuck waiting for a render that will never come. Single-thread path: read the mixer directly. Either
                // way, never declare Ended while a voice-add command is still queued (spec §12) or the sink still
                // holds unplayed filler.
                bool mixerDrained = _feed is not null ? _mixer.DrainedPublished : _mixer.IsDrained(_mixer.ConsumeSeq);
                var drainSink = _out as IBufferedAudioSink;
                var endVerdict = DrainVerdict.Decide(mixerDrained, MixerCmdsPending, _pendingFrames,
                    drainSink?.WritableFrames ?? 0, drainSink?.CapacityFrames ?? 0, drainSink is not null);
                if (endVerdict.PublishEnded)
                {
                    _playRequested = false;
                    sink.PlayRequested(false);
                    Publish(PlaybackState.Ended);
                }
                break;

            case PlaybackState.Ended:
                if (_playRequested)   // replay from the start
                {
                    SeekToStart();
                    Publish(PlaybackState.Playing);
                }
                break;
        }
        return _state;
    }

    /// <summary>Render available content through the voice/master graph, then submit as much as the endpoint accepts.
    /// Unaccepted samples remain in the retained block; neither decoder nor mixer advances again until they are written.
    /// PCM preflight holds the content timeline during starvation. Endpoint capacity, writes and waits stay outside the
    /// allocation-free DSP tripwire. Sustained endpoint failures request recovery on the cold device thread.</summary>
    public int RenderBlock(int frames)
    {
        if (_formatRequiresReload)
        {
            // Log ONCE per rate change (Wavee #112): a session parked here is silent until the host's graph reload lands,
            // and without this line a stalled reload looks exactly like a dead device. Before the tripwire; one allocation
            // per rate change is the same trade the WASAPI leaf makes for its one-shot format warning.
            if (!_reloadSuppressionLogged)
            {
                _reloadSuppressionLogged = true;
                FluentGpu.Foundation.Diag.Line($"[audio] render suppressed: output format changed to {_format.SampleRate} Hz/{_format.Channels}ch — awaiting the host's graph reload");
            }
            return 0;
        }
        DrainMixerCmds();
        if (_pendingFrames > 0) return SubmitPending();

        // Nothing-to-render short-circuit (spec §12; Wavee: playback stuck at the tail on WASAPI — the mixer clock
        // ran on past the end forever). A voiceless mixer with nothing queued to arrive has no content: rendering it
        // anyway would keep submitting zero-filled blocks into a buffered sink, which is therefore never empty, so
        // the sink-drained half of the Ended check (Advance, below) can never observe true silence on a real device.
        // Gated to the RUNNING transport phase (0) only: a pause fade-out (phase 1) still needs its tail rendered —
        // over silence if the mixer is already voiceless — so the transport ramp's own EndFrame comparison further
        // down can still fire and carry the pause through phase 2 → 3 → Paused.
        if (Volatile.Read(ref _transportPhase) == 0)
        {
            var drainProbe = _out as IBufferedAudioSink;
            var verdict = DrainVerdict.Decide(
                mixerDrained: _mixer.VoiceCount == 0,
                cmdsPending: MixerCmdsPending,
                pendingFrames: _pendingFrames,
                writableFrames: drainProbe?.WritableFrames ?? 0,
                capacityFrames: drainProbe?.CapacityFrames ?? 0,
                bufferedSink: drainProbe is not null);
            if (!verdict.RenderAllowed)
            {
                _mixer.PublishDrained(_mixer.ConsumeSeq);
                return 0;
            }
        }

        frames = Math.Clamp(frames, 1, _maxBlock);
        if (_out is IBufferedAudioSink buffered)
        {
            int writable = buffered.WritableFrames;
            if (writable < 0) { RecordSinkFailure(); return 0; }
            frames = Math.Min(frames, writable);
            if (frames == 0) return 0;
        }
        foreach (var voice in _mixer.VoicesSpan)
            if (voice.Src is WsolaAudioSource stretched) stretched.Rate = PlaybackRate;
        // F2: an empty active ring is NOT a reason to stop the device. The block is SILENCE (inside the tripwire), the content
        // timeline holds (the mixer is not rendered: ConsumeSeq, envelopes, the transport ramp and the spectrum tap stay put) and
        // the silence is recorded in the ledger so the content clock can subtract it. One xrun per incident (the ring's own latch);
        // severity accrues every silent block. The device is never Stop()ped or Reset() — a stop/start cycle per dropout was the stutter
        // loop. Silence is topped up only to a shallow padding floor (SilencePaddingFloorBlocks), see RenderSilence.
        int readable = _mixer.ReadableFrames(frames, out var waitingFor);
        if (readable <= 0 && waitingFor is not null)
        {
            if (_starvationPhase == 0) { _starvationPhase = 1; _starvedRing = waitingFor; }
            return RenderSilence(frames, waitingFor);
        }
        if (_starvationPhase != 0)
        {
            // Resume only behind the cushion AND only while the transport is running (phase 0): a pause fade in flight must not be
            // retargeted away by the 5 ms fade-in below. A cushion beyond what a stretched voice's full ring can supply is bounded by
            // CrossfadeMixer.PcmReady (WsolaAudioSource.ReachableFrames), so it can never wedge this gate.
            if (!_mixer.PcmReady(_resumeFrames) || Volatile.Read(ref _transportPhase) != 0) return RenderSilence(frames, _starvedRing);
            _starvationPhase = 0;
            _starvedRing = null;
            // Each incident doubles the cushion (capped at the decode-ahead target) so a persistently slow producer settles on a
            // cushion it can hold; a SEEK rebuffer is a planned flush, not an incident, and never grows it.
            if (!_seekRebufferActive) _resumeFrames = Math.Min(_resumeFrames * 2, _feed?.TargetAheadFrames ?? _resumeFrames);
            Volatile.Write(ref _lastIncidentTick, TickClockMs());
            _transport.Retarget(0f, _mixer.ConsumeSeq, 1);                                  // snap to silence …
            _transport.Retarget(1f, _mixer.ConsumeSeq, Math.Max(1, _format.SampleRate / 200));   // … and fade in over 5 ms
            // Silence → content: wake the waiters that poll the transport on this event (SeekInPlace's "resumed" wait, among them) the
            // moment the edge happens, not on their next 20 ms recheck. One kernel Set outside the tripwire window — the same carve-out
            // SetPhase and the applied-wake use; no allocation.
            _phaseWake.Set();
        }
        if (readable > 0) frames = Math.Min(frames, readable);
        if (Volatile.Read(ref _transportPhase) == 1)
            frames = (int)Math.Min(frames, Math.Max(1, _transport.EndFrame + 1 - _mixer.ConsumeSeq));
        var buf = _mixBuf.AsSpan(0, frames * _format.Channels);
        var ctx = new BlockCtx(_mixer.ConsumeSeq, _format.SampleRate, _format.Channels, _plane);
        var graph = _graph.Live;
        AudioTripwire.BeginBlock();
        _masterGain.SetTargetLinear(_muted ? 0f : _volume, _plane.DefaultRampSamples);
        _masterChannel.SetTargetBalance(_balanceTarget, _plane.DefaultRampSamples);   // applied HERE: the control thread only publishes the target
        _mixer.Render(buf, frames, ctx);
        if (_feed is not null)
        {
            foreach (var retired in _mixer.RetiredSourcesThisBlock)
                if (SourceRing(retired) is { } ring) _feed.EnqueueRetire(ring);
        }
        _mixer.PublishDrained(_mixer.ConsumeSeq);
        // D7: the master EQ and the TERMINAL LIMITER run BEFORE the master volume, so tone and limiting do not change with the
        // slider; everything after the limiter can only attenuate (_masterGain ≤ 1 — SetVolume/TrySetVoice clamp; the balance pan
        // is cos(·) ≤ 1; the transport ramp is in [0,1]).
        graph.RenderMaster(buf, frames, ctx);
        // spectrum tap: immediately before master gain (#166)
        TapSpectrumBlock(buf, frames, ctx.StartFrame);   // post-EQ/limiter, PRE-volume: the picture must not follow the volume slider (mute rides a flag instead)
        _masterGain.Process(buf, buf, frames, ctx);
        _masterChannel.Process(buf, buf, frames, ctx);
        _transport.Apply(buf, frames, _format.Channels, ctx.StartFrame);
        TapBlock(buf, frames);
        _graph.MarkConsumed();
        AudioTripwire.EndBlock();
        if (_feed is null)
            foreach (var retired in _mixer.RetiredSourcesThisBlock) (retired as IDisposable)?.Dispose();
        _position.ExtraLatencySamples = graph.TotalLatencySamples;
        _pendingFrames = frames;
        _pendingOffset = 0;
        _pendingSilence = false;
        if (_transportPhase == 1 && _mixer.ConsumeSeq > _transport.EndFrame)
        {
            if (_holdNoStop) SetPhase(4);   // a seek hold: gain is 0 and the transport keeps RUNNING — no drain, no Stop, no restart
            else
            {
                _fadeTailSubmitted = _submittedFrames + frames;
                SetPhase(2);
            }
        }
        return SubmitPending();
    }

    // F2: submit one block of SILENCE for a dry ring. RT, alloc-free; the tripwire window covers the clear, the severity accrual and
    // the level tap. The spectrum tap is deliberately NOT fed: the content clock does not advance during silence, so the flagship's
    // content-domain window holds on the last real frames (no StartFrame discontinuity, no re-arm).
    private int RenderSilence(int frames, RingAudioSource? ring)
    {
        // A pause or seek-hold fade that was in flight when the ring ran dry can no longer progress (the timeline holds), and what
        // the listener hears is silence already: call the fade over — no block needed. A pause drains the device and stops it
        // (phase 2, tail = everything submitted so far, exactly as for a pause on a starved session); a seek hold lands in phase 4
        // (gain 0, still rendering — through this very path).
        if (Volatile.Read(ref _transportPhase) == 1)
        {
            _transport = new TransportRamp(0f);
            if (_holdNoStop) SetPhase(4);
            else
            {
                _fadeTailSubmitted = _submittedFrames;
                SetPhase(2);
            }
            return 0;
        }

        // SHALLOW silence: keep the device FIFO at a small floor while starved, never topped up to its usual ~100 ms depth. Silence
        // queued ahead of the device delays real audio that returns after a long absence by exactly that depth (the legacy
        // stop/restart path did not pay it), and a device that underruns over silence is inaudible anyway. Whatever real audio
        // the device still holds plays out first; the ledger only ever records silence that was actually submitted, so the content
        // clock (raw played − silence played) and the spectrum window stay exact however many blocks are skipped. Resume is
        // unaffected — the first real block then queues behind at most this floor.
        if (_out is IBufferedAudioSink device
            && device.CapacityFrames - device.WritableFrames >= SilencePaddingFloorBlocks * RenderBlockFrames) return 0;

        var silence = _mixBuf.AsSpan(0, frames * _format.Channels);
        AudioTripwire.BeginBlock();
        silence.Clear();
        ring?.RecordStarvedFrames(frames);   // severity every block; the ring's incident latch makes the whole starve ONE xrun
        TapBlock(silence, frames);           // the level meter shows the dropout
        AudioTripwire.EndBlock();
        _pendingFrames = frames;
        _pendingOffset = 0;
        _pendingSilence = true;
        _mixer.PublishDrained(_mixer.ConsumeSeq);
        RecordSilenceSpan(_submittedFrames, frames);
        return SubmitPending();
    }

    // RT: record a silence span at its device submit index. A full ledger coalesces a CONTIGUOUS span into the newest one (the
    // control tick is far faster than 64 blocks; this only matters for a hand-driven harness).
    private void RecordSilenceSpan(long submitIndex, int frames)
    {
        int tail = _silenceTail, next = (tail + 1) & (_silence.Length - 1);
        if (next == Volatile.Read(ref _silenceHead))
        {
            ref var newest = ref _silence[(tail - 1) & (_silence.Length - 1)];
            if (newest.SubmitIndex + newest.Frames == submitIndex) newest.Frames += frames;
            return;
        }
        _silence[tail] = new SilenceSpan { SubmitIndex = submitIndex, Frames = frames };
        Volatile.Write(ref _silenceTail, next);
    }

    /// <summary>CONTROL: the number of silence frames submitted before device submit index <paramref name="index"/> — the
    /// already-dropped (fully played) spans plus the live spans clamped to <paramref name="index"/>; an index inside the dropped prefix
    /// (one the device has already played past) is answered from the coalesced history of dropped spans, so silence that FOLLOWED it is
    /// never counted. With <paramref name="consumePlayed"/> the fully-played prefix is folded into the cache and the history and dropped
    /// from the ledger (the clock sampler passes the device's raw played count; everything else is a non-consuming read).</summary>
    internal long SilenceBefore(long index, bool consumePlayed = false)
    {
        lock (_silenceLock)
        {
            int epoch = Volatile.Read(ref _silenceEpoch);
            if (epoch != _silenceEpochSeen) { _silenceEpochSeen = epoch; _silencePlayedCache = 0; _silenceHistoryCount = 0; }   // a reset restarted the index domain
            if (_silenceHistoryCount > 0)
            {
                ref readonly var newest = ref _silenceHistory[_silenceHistoryNewest];
                if (index < newest.SubmitIndex + newest.Frames) return PlayedSilenceBefore(index);
            }
            long sum = _silencePlayedCache;
            int head = Volatile.Read(ref _silenceHead), tail = Volatile.Read(ref _silenceTail), start = head;
            while (head != tail)
            {
                var span = _silence[head];
                long end = span.SubmitIndex + span.Frames;
                if (index >= end)
                {
                    sum += span.Frames;
                    // Folded from this copy (not a re-read): a full ledger lets the RT extend its newest span in place. Should a reset
                    // land mid-walk, the next call sees the new epoch and clears the cache and the history again.
                    if (consumePlayed) RecordPlayedSilence(span.SubmitIndex, span.Frames);
                    head = (head + 1) & (_silence.Length - 1);
                    continue;
                }
                if (index > span.SubmitIndex) sum += index - span.SubmitIndex;   // a partially played / submitted span counts its played part
                break;
            }
            if (consumePlayed && head != start && epoch == Volatile.Read(ref _silenceEpoch))
                Volatile.Write(ref _silenceHead, head);
            return sum;
        }
    }

    // CONTROL, under _silenceLock: fold one fully played span into the cache and the history — a span contiguous with the newest entry
    // extends it, anything else opens a new entry stamped with the silence before it (the cache, since spans drop in submit order).
    private void RecordPlayedSilence(long submitIndex, int frames)
    {
        if (_silenceHistoryCount > 0)
        {
            ref var newest = ref _silenceHistory[_silenceHistoryNewest];
            if (newest.SubmitIndex + newest.Frames == submitIndex)
            {
                newest.Frames += frames;
                _silencePlayedCache += frames;
                return;
            }
        }
        _silenceHistoryNewest = (_silenceHistoryNewest + 1) & (_silenceHistory.Length - 1);
        _silenceHistory[_silenceHistoryNewest] = new PlayedSilence { SubmitIndex = submitIndex, Frames = frames, Before = _silencePlayedCache };
        if (_silenceHistoryCount < _silenceHistory.Length) _silenceHistoryCount++;
        _silencePlayedCache += frames;
    }

    // CONTROL, under _silenceLock: the silence before an index that lies inside the dropped prefix, newest entry first. An index older than
    // the whole history (64 separate silences back) gets the oldest entry's lower edge — the closest bound the ledger still holds.
    private long PlayedSilenceBefore(long index)
    {
        int i = _silenceHistoryNewest;
        long before = 0;
        for (int k = 0; k < _silenceHistoryCount; k++, i = (i - 1) & (_silenceHistory.Length - 1))
        {
            ref readonly var span = ref _silenceHistory[i];
            if (index > span.SubmitIndex) return span.Before + Math.Min(index - span.SubmitIndex, span.Frames);
            before = span.Before;
        }
        return before;
    }

    /// <summary>The content-domain count of frames submitted so far: <see cref="SubmittedFrames"/> minus the silence among them.
    /// The device's played clock minus this is the silence-free head start the position projection may not extrapolate past.</summary>
    internal long ContentSubmittedFrames()
    {
        long submitted = SubmittedFrames;
        return submitted - SilenceBefore(submitted);
    }

    /// <summary>A device submit index → the CONTENT frame index at that point (the index minus every silence frame before it): the
    /// anchor a rebase at a swap/jump block needs, because the position's played-frame domain is the content clock.</summary>
    internal long ContentIndexAt(long submitIndex) => submitIndex - SilenceBefore(submitIndex);

    // CLOCK THREAD (Playing, once per tick): the cushion a starved ring must reach before resuming doubles per incident; it halves
    // each time 30 s pass without one so a one-off hiccup does not tax the rest of a long listen.
    private void DecayResumeCushion()
    {
        int floor = _format.SampleRate / 10;
        int cushion = _resumeFrames;
        if (cushion <= floor) return;
        long now = TickClockMs();
        if (now - Volatile.Read(ref _lastIncidentTick) <= ResumeDecayMs) return;
        _resumeFrames = Math.Max(floor, cushion / 2);
        Volatile.Write(ref _lastIncidentTick, now);
    }

    private int SubmitPending()
    {
        if (_pendingFrames <= 0) return 0;
        int written = _out.Write(_mixBuf.AsSpan(_pendingOffset * _format.Channels,
            _pendingFrames * _format.Channels), _pendingFrames);
        if (written < 0 || written > _pendingFrames)
            throw new InvalidOperationException("Audio sink returned an invalid accepted-frame count.");
        if (written == 0)
        {
            if (_out is not IBufferedAudioSink buffered || buffered.WritableFrames < 0) RecordSinkFailure();
            return 0;
        }
        _consecutiveSinkFailures = 0;
        _pendingOffset += written;
        _pendingFrames -= written;
        Interlocked.Add(ref _submittedFrames, written);
        if (_clock is SyntheticAudioClock synthetic) synthetic.Advance(written);
        if (Interlocked.Exchange(ref _startRequested, 0) != 0 && !_started)
        {
            // A Start() on a device invalidated by a jack switch is a SINK failure (Wavee #112): ask the cold device
            // thread for a rebuild instead of letting the exception reach RenderBurst → RecordFault → a user-visible
            // MediaError. The accepted frames stay accepted; the rebuilt sink restarts via RebuildSink → EnsureStarted.
            try { _out.Start(); _started = true; }
            catch (AudioDeviceLostException) { RecordDeviceLost(); return written; }
        }
        return written;
    }

    // A sustained run of dead writes (a torn/invalidated device with no follow-default notification): REPORT — never
    // re-stamp — a rebuild request. RequestRebuild is the watcher's debounced entry; a report every ~80 ms through it
    // postponed the 250 ms debounce forever (the 0.2.8 livelock, Wavee #112). Alloc-free; outside the DSP tripwire.
    private void RecordSinkFailure()
    {
        if (++_consecutiveSinkFailures < SinkFailureRebuildThreshold) return;
        _consecutiveSinkFailures = 0;
        _deviceController?.ReportSinkFailure();
    }

    // A typed device-lost failure is unambiguous — no need to wait out the threshold.
    private void RecordDeviceLost()
    {
        _consecutiveSinkFailures = 0;
        _deviceController?.ReportSinkFailure();
    }

    private void TapBlock(ReadOnlySpan<float> buf, int frames)
    {
        if (_liveEffects is not AudioEffects ae) return;
        long epoch = ae.VisualizerDemand(Volatile.Read(ref _visualizerSource));
        if (epoch == 0) return;
        int n = frames * _format.Channels;
        if (n <= 0) return;
        float peak = 0f;
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            float a = buf[i];
            float m = a < 0 ? -a : a;
            if (m > peak) peak = m;
            sumSq += (double)a * a;
        }
        _meterSamples += n;
        _tap.Publish((float)Math.Sqrt(sumSq / n), peak, epoch);
    }

    /// <summary>RT: feed the spectrum ring with the PRE-gain mix. Demand-gated exactly like <see cref="TapBlock"/> (two
    /// volatile reads, no lock), alloc-free, no blocking. <paramref name="startFrame"/> is <c>BlockCtx.StartFrame</c> — the
    /// content frame of <c>buf[0]</c>. The ring is (re-)ARMED with that base on a demand edge, on a device rebuild
    /// (<see cref="RenderEpoch"/> — CmdReset, RebuildSink) and on any block discontinuity (a frame the
    /// RT did not render through this tap), so the reader's content→ring mapping is exact. F2 starvation silence never passes
    /// through here, and it does not move <c>StartFrame</c> either, so a dry ring produces no discontinuity.</summary>
    private void TapSpectrumBlock(ReadOnlySpan<float> buf, int frames, long startFrame)
    {
        if (_liveEffects is not AudioEffects ae) return;
        long epoch = ae.SpectrumDemand(Volatile.Read(ref _visualizerSource));
        if (epoch == 0) { _spectrumArmed = false; return; }
        var ring = Volatile.Read(ref _spectrumRing);
        if (ring is null) return;                              // the control thread has not created it yet
        long renderEpoch = Volatile.Read(ref _renderEpoch);
        if (!_spectrumArmed || epoch != _spectrumEpochSeen || renderEpoch != _spectrumRenderEpochSeen || startFrame != _spectrumNextStart)
        {
            _spectrumArmed = true;
            _spectrumEpochSeen = epoch;
            _spectrumRenderEpochSeen = renderEpoch;
            ring.Arm(startFrame);
        }
        ring.Write(buf[..(frames * _format.Channels)], _format.Channels);
        _spectrumNextStart = startFrame + frames;
    }

    private void PublishPosition(MediaSignalSink sink)
    {
        // S-6: while a replacement/seek transaction holds the gate it is rewriting the position domain (Reset / Rebase / the
        // content cursor); a sample taken mid-way would publish a position from the OLD domain against the NEW origin.
        if (_replacementGate.CurrentCount == 0) return;
        // The projection may not run past the CONTENT submitted so far — including through starvation silence, where it therefore
        // holds (the clock it extrapolates from is the content clock, whose domain this limit must share).
        _position.SubmittedFrameLimit = ContentSubmittedFrames();
        _position.IsAdvancing = _started && Volatile.Read(ref _transportPhase) != 3;
        _position.Sample(_presentationClock);
        if (_presentationClock.TryGetPlayed(out long played, out _))
            Interlocked.Exchange(ref _playedFrames, Math.Clamp(played, 0, SubmittedFrames));
        double clockFrame = _position.Project(NowTicks100ns()).TotalSeconds * _format.SampleRate;
        var rateSource = Volatile.Read(ref _activeRateSource);
        double contentFrame = rateSource is null ? clockFrame
            : _clockAnchorPosition + _activeMixerStart + ContentFrameAt(clockFrame) - rateSource.InitialSourceFrame;
        sink.Position(TimeSpan.FromSeconds(Math.Max(0, contentFrame) / _format.SampleRate));
    }

    private long NowTicks100ns()
        => _clock is SyntheticAudioClock sc ? sc.NowTicks100ns : (long)(Stopwatch.GetTimestamp() * s_qpcTo100ns);

    private void PublishMetadata(MediaSignalSink sink)
    {
        if (_metaPublished) return;
        _metaPublished = true;
        sink.Duration(_duration);
        sink.NaturalSize(SizeI.Zero);   // audio-only
        sink.Commands(MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate
                      | MediaCommandFlags.Next | MediaCommandFlags.Previous);
    }

    private void EnsureStarted()
    {
        if (_started || _formatRequiresReload) return;
        WarmUp();
        Volatile.Write(ref _startRequested, 1);
        _feed?.WakeOutput();
    }

    /// <summary>Fix 4b (spec): run the render path once, silently, before the FIRST <see cref="_out"/>.Start(). NativeAOT has
    /// no JIT, but static-constructor initialization and binary page-in still happen on first touch — leaving that to the
    /// first real RT callback is a guaranteed first-seconds glitch. This deliberately does NOT reuse the session's live
    /// <see cref="_masterGain"/>/<see cref="_masterChannel"/>/<see cref="_graph"/>/<see cref="_mixer"/> or the real sink:
    /// those carry state a stray pass could perturb (a gain ramp mid-flight, a limiter envelope) or — via
    /// <see cref="IAudioSink.Write"/> — actually present a leading silent block ahead of the track's first real frame
    /// (which would corrupt a golden-PCM capture). Instead it exercises fresh, throwaway instances of the exact same
    /// stage types over a private scratch buffer, so the method bodies/static state get paged in with zero observable
    /// effect on this (or any) session. The DECODE side of the pipeline needs no separate warm-up: fix 1 already holds
    /// Buffering until the worker has genuinely decoded ahead into the ring, so the decoder/resampler code is already hot
    /// by the time this runs. Best-effort — any failure here must never block <see cref="EnsureStarted"/>.</summary>
    private void WarmUp()
    {
        if (_warmedUp) return;
        _warmedUp = true;
        try
        {
            var scratch = new float[_maxBlock * _format.Channels];
            var warmCtx = new BlockCtx(0, _format.SampleRate, _format.Channels, new ParamPlane());
            new GainStage(1f).Process(scratch, scratch, _maxBlock, warmCtx);
            new ChannelStage(0f, false).Process(scratch, scratch, _maxBlock, warmCtx);
            new AudioGraphHost(_format.Channels, _format.SampleRate).Live.RenderMaster(scratch, _maxBlock, warmCtx);
        }
        catch { /* best-effort — a warm-up fault must never block Start() */ }
    }

    /// <summary>Raised when a device rebuild adopts an endpoint that clocks at a DIFFERENT sample rate than the session's
    /// fixed mix format (spec §7.9): the sink swap keeps audio alive, but the decoder/graph/mixer/rings/EQ are all frozen at
    /// the old rate, so the currently-playing track needs a full re-arm at the new rate. The host subscribes and drives a
    /// soft reload (re-open rate-correct through the OpenAsync path) — the session does NOT re-rate itself in place. Raised
    /// fire-and-forget OFF the cold device thread; a throwing subscriber can never stall or fault the device switch.</summary>
    public event Action<MixFormat>? DeviceFormatChanged;

    /// <summary>Device-loss / follow-default rebuild (spec §7.9): swap ONLY the sink+clock endpoint under a LIVE graph. The
    /// sources, mixer voices, queue/<c>PreparedSlot</c>, published graph, and the derived timeline position ALL survive — the
    /// position domain is re-anchored to the new device's zero, the stream latency is re-measured on the next poll, and a
    /// short fade-in avoids a resume click. Runs OFF the RT thread (the cold device thread). Never throws. Returns false —
    /// keeping the CURRENT sink untouched — if the session was disposed or <paramref name="newEndpoint"/> is not
    /// <see cref="IAudioEndpoint.IsReady"/> (its open failed; adopting it would silence playback — Wavee #112).</summary>
    public bool RebuildSink(IAudioEndpoint newEndpoint)
    {
        if (_disposed || newEndpoint is null || !newEndpoint.IsReady) return false;

        // Capture the current timeline position (frames) so it continues seamlessly across the swap.
        long posFrames = Math.Max(0, _position.PlayedFramesCompensated);
        long sourcePosition = Math.Max(0, (long)Math.Round(ContentFrameAt(posFrames)));
        int previousRate = _format.SampleRate;

        var oldSink = _out;
        var oldEndpoint = _endpoint;
        try { oldSink.Stop(); } catch { /* teardown never throws */ }
        // The device-underrun count lives on the sink: fold the old sink's into the session's so the total stays monotonic across a swap.
        if (oldSink is IBufferedAudioSink oldBuffered) Interlocked.Add(ref _retiredDeviceUnderruns, oldBuffered.DeviceUnderruns);

        _out = newEndpoint.Sink;
        _clock = newEndpoint.Clock;
        _endpoint = newEndpoint;
        _started = false;
        _holdNoStop = false;

        // Re-anchor: the new device clock starts at 0 played frames == the current timeline position (spec §7.6).
        _position.Reset();
        _position.Rebase(0, posFrames);
        // Keep the output-domain anchor: the WSOLA map still describes the surviving mixer timeline.

        // Short fade-in on resume (spec §7.9): drop to silence and ramp back to the live master volume — no resume click.
        _masterGain.SetLinear(0f);
        _masterGain.SetTargetLinear(_muted ? 0f : _volume, _format.SampleRate * 0.03f);

        if (oldEndpoint is not null && !ReferenceEquals(oldEndpoint, newEndpoint))
            try { oldEndpoint.Dispose(); } catch { /* teardown never throws */ }

        // Rate change (e.g. the new default endpoint clocks at 44100 vs the graph's 48000): the swap above kept audio alive,
        // but the decoder/graph/mixer/rings/EQ are frozen at the old rate. Signal the host to drive a soft reload (re-open
        // rate-correct via OpenAsync — Fix 1); we do NOT re-rate the graph in place. Fire-and-forget OFF this cold device
        // thread so a throwing subscriber can neither stall nor fault the switch.
        var onFormatChanged = DeviceFormatChanged;
        var newFormat = newEndpoint.Sink.Format;
        bool rateChanged = newFormat != _format;
        _formatRequiresReload = rateChanged;
        _reloadSuppressionLogged = false;
        Interlocked.Increment(ref _renderEpoch);
        _submittedFrames = _playedFrames = _deviceFrameOrigin = 0;
        Interlocked.Exchange(ref _rawPlayedFrames, 0);
        ResetSilenceLedger();   // the submit-index domain restarts at 0 on the new endpoint (the feed is parked around every rebuild)
        _starvationPhase = 0;
        _starvedRing = null;
        _pendingFrames = _pendingOffset = 0;
        if (rateChanged)
        {
            // Stamp BEFORE raising: the soft-reload subscriber (and anything racing it, e.g. a queue preroll reading
            // `session.Format` for PrepareContext.For) must see the new rate the moment the switch is signaled, not
            // after the fire-and-forget reload eventually lands (Fix 2 — was the stale-`Format` root cause of a
            // gapless join priming the next voice at the OLD rate and playing it off-pitch after a reopen).
            _format = newFormat;
        }
        if (onFormatChanged is not null && rateChanged)
            ThreadPool.QueueUserWorkItem(
                static s => { try { s.h(s.f); } catch { /* a soft-reload subscriber never faults the device switch */ } },
                (h: onFormatChanged, f: newFormat), preferLocal: false);

        // A same-rate swap while playing resumes at once: the graph, voices and position are all still valid, and the 30 ms
        // fade-in above covers the seam. DeviceRebuilt is a NOTIFICATION, never a hand-off: holding the transport until a
        // subscriber released it left Wavee "Playing" in silence after every same-rate switch (Bluetooth headphones at
        // 48 kHz). A rate change still holds, because the graph cannot render at the new rate until the host's reload
        // replaces it; a paused session stays held as it was.
        bool resume = !rateChanged && (_state == PlaybackState.Playing || _playRequested);
        var onRebuilt = DeviceRebuilt;
        if (onRebuilt is not null)
        {
            if (!resume)
            {
                _transportHoldRequested = true;
                _transport = new TransportRamp(0);
                SetPhase(3);
            }
            ThreadPool.QueueUserWorkItem(static state =>
            {
                try { state.Callback(state.Format, state.Position); } catch { }
            }, (Callback: onRebuilt, Format: newFormat, Position: (long)Math.Round(sourcePosition * (double)newFormat.SampleRate / previousRate)), preferLocal: false);
        }
        if (resume) EnsureStarted();
        return true;
    }

    private void SeekToStart()
    {
        // RT path: worker-routed (sole inner-decoder toucher — spec §7.9/§12); single-thread path: inline.
        if (_feed is not null) { _feed.RequestSeek(0); ArmSeekRebufferSuppression(); }
        else if (_voice is DecoderAudioSource das) das.SeekFrame(0);
        else if (_voice is TrimmingSource ts) ts.SeekFrame(0);
        else if (_voice is MemoryAudioSource mas) mas.SeekFrame(0);
        // The position's played-frame domain is the CONTENT clock (it samples _presentationClock), so the rebase anchor is read from it.
        _presentationClock.TryGetPlayed(out long playedNow, out _);
        _position.Rebase(playedNow, 0);
    }

    private void Publish(PlaybackState state)
    {
        if (state == _state) return;
        _state = state;
        _sink?.State(state);
    }

    /// <summary>Surface a contained background-loop fault (RT/worker/clock) as a typed <see cref="MediaError"/> on the
    /// <c>Error</c> signal — OFF the RT thread (called from <see cref="AudioFeedThread.ControlTickOnce"/>; spec §11/§12). A
    /// late-after-dispose fault is <see cref="MediaErrorCategory.Lifecycle"/>, a decode fault is
    /// <see cref="MediaErrorCategory.Decode"/>; both are recoverable (the source retries / advances). Never process-fatal.</summary>
    internal void ReportBackgroundFault(Exception e)
        => _sink?.Error(new MediaError(
            e is ObjectDisposedException ? MediaErrorCategory.Lifecycle : MediaErrorCategory.Decode,
            e.Message, null, null, MediaRecovery.Retryable));

    private void StartFeeder()
    {
        if (_pumpThread is not null) return;
        _pumpRun = true;
        _pumpThread = new Thread(FeederLoop) { IsBackground = true, Name = "FluentGpu.PcmAudioFeeder" };
        _pumpThread.Start();
    }

    // The single control-thread feeder for a real device (M2). Plain thread + short sleep — the M4 flip replaces this with
    // the MMCSS Pro-Audio RT feed callback driving PumpAudio per device period.
    private void FeederLoop()
    {
        while (_pumpRun && !_disposed)
        {
            PumpAudio(_maxBlock);
            Thread.Sleep(5);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pumpRun = false;
        // Wake every signalled waiter (WaitAppliedAsync / WaitPhaseAsync / FadeOutAsync): they re-check _disposed and end.
        _appliedWake.Set();
        _phaseWake.Set();
        // Stop recovery before stopping output, so a completed cold rebuild cannot restart a retiring feed.
        if (_owned is not null) { foreach (var d in _owned) { try { d.Dispose(); } catch { } } _owned = null; }
        bool hadFeed = _feed is not null;
        if (_feed is not null)
        {
            _feed.Stop();
            while (!_feed.IsStopped)
            {
                // An uncooperative sink retains its endpoint until its last callback returns.
                await Task.Delay(20).ConfigureAwait(false);
                _feed.Stop();
            }
            DrainMixerCmds();
        }
        try { _feed?.Dispose(); } catch { /* teardown never throws */ }   // stop the RT/worker/clock threads first
        _feed = null;
        try { (_voice as ICancellableAudioSource)?.CancelPendingRead(); } catch { }
        _pumpThread?.Join(2000);
        while (_pumpThread is { IsAlive: true }) await Task.Delay(20).ConfigureAwait(false);
        _pumpThread = null;
        try { _out.Stop(); } catch { /* teardown never throws */ }
        try { _endpoint?.Dispose(); } catch { /* teardown never throws */ }
        // Publish the terminal state BEFORE severing the sink — without this, a torn-down session leaves
        // MediaPlayerCore.State pinned at whatever it last was (often Playing) forever, because nothing else ever
        // writes to it again once _sink goes null.
        Publish(PlaybackState.Idle);
        _sink = null;
        if (!hadFeed) DisposeLiveMixerSources();
        _voice = null;
    }

    private void DisposeLiveMixerSources()
    {
        foreach (var live in _mixer.VoicesSpan) (live.Src as IDisposable)?.Dispose();
        _mixer.Clear();
    }
}
