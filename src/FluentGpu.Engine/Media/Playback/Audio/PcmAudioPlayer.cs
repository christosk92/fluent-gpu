using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

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

    private async ValueTask<IPreparedItem> PrepareCoreAsync(MediaSource next, PrepareContext ctx, long positionFrames, bool forSeek, CancellationToken ct)
    {
        if (!await _decoderSlots.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            throw new TimeoutException("Audio decoder producers did not retire within the two-second budget.");
        var lease = new DecoderLease(_decoderSlots);
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
            ring = new RingAudioSource(voice, ctx.Format.Channels, ctx.Format.SampleRate,
                Math.Max(1, ctx.Format.SampleRate / 2), Math.Max(1, ctx.Format.SampleRate / 50));
            ring.StartProducer();
            int readyFrames = Math.Max(1, ctx.Format.SampleRate / (forSeek ? 10 : 2));
            await ring.WaitUntilReadyAsync(readyFrames, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new AudioPreparedItem(ring, decoder.Gapless, info.Loudness, totalFrames, info.Duration,
                ctx.Format.SampleRate, achieved, readyFrames);
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

    private readonly Func<MixFormat, IAudioEndpoint> _endpointFactory;
    private readonly Func<MixFormat, IAudioDecoder> _decoderFactory;
    private readonly IAudioEffects? _effects;
    private readonly int _maxBlock;
    private readonly bool _driveWithOwnThread;
    private readonly Action<PcmAudioSession>? _onSessionCreated;   // M4: attach the RT feed + device controller (on-box)

    /// <summary>Create a PCM backend. When <paramref name="endpointFactory"/> is omitted the HEADLESS endpoint (null sink +
    /// synthetic clock) is used (deterministic, no device). <paramref name="effects"/> supplies the live
    /// EQ/normalization/volume signals; <paramref name="driveWithOwnThread"/> starts a single control-thread feeder (for a
    /// real device — NOT the M4 MMCSS RT thread). <paramref name="decoderFactory"/> injects the decode-edge codec (spec §5.5
    /// <see cref="IAudioDecoder"/>): the DEFAULT is the built-in <see cref="WavAudioDecoder"/>; the app supplies a
    /// Vorbis/FLAC/MP3 factory to route real streaming content through the same graph.</summary>
    public PcmAudioPlayer(
        MixFormat? format = null,
        Func<MixFormat, IAudioEndpoint>? endpointFactory = null,
        IAudioEffects? effects = null,
        int maxBlock = 1024,
        bool driveWithOwnThread = false,
        Action<PcmAudioSession>? onSessionCreated = null,
        Func<MixFormat, IAudioDecoder>? decoderFactory = null)
    {
        Format = format ?? new MixFormat(48000, 2);
        _endpointFactory = endpointFactory ?? (fmt => new HeadlessAudioEndpoint(fmt));
        _decoderFactory = decoderFactory ?? (static _ => new WavAudioDecoder());
        _effects = effects;
        _maxBlock = Math.Max(64, maxBlock);
        _driveWithOwnThread = driveWithOwnThread;
        _onSessionCreated = onSessionCreated;
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
        if (!await _decoderSlots.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            throw new TimeoutException("Audio decoder producers did not retire within the two-second budget.");
        var lease = new DecoderLease(_decoderSlots);
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
            session.Configure(BuildGraphSpec(_effects, mix));
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
public sealed class PcmAudioSession : IMediaSession
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
    private struct MixerCmd { public byte Kind; public long Id; public MixVoice Voice; public GainEnvelope? Env; public long Sequence; public int Frames; }
    private const byte CmdReplacePrimary = 1, CmdAddVoice = 2, CmdSetEnvelope = 3, CmdRemoveVoice = 4, CmdFadeOut = 5, CmdFadeIn = 6, CmdReset = 7, CmdSeekAnchor = 8;
    private readonly MixerCmd[] _mixerCmdQ = new MixerCmd[64];
    private int _mixerCmdHead, _mixerCmdTail;                 // Volatile head/tail; consumer = whichever thread runs RenderBlock
    private readonly object _mixerCmdProducerLock = new();
    private long _commandSequence, _appliedSequence;
    private long _submittedFrames, _playedFrames, _renderEpoch;
    private long _deviceFrameOrigin;
    private readonly SessionAudioClock _presentationClock;
    private int _starvationPhase; // 0 healthy, 1 drain buffered output, 2 stopped awaiting PCM
    private RingAudioSource? _starvedRing;
    private long _starvedAt;

    private sealed class SessionAudioClock(PcmAudioSession session) : IAudioClockSource
    {
        public long WrittenFrames => session.SubmittedFrames;
        public long StreamLatencyFrames => session._clock.StreamLatencyFrames;
        public int MixRate => session._clock.MixRate;
        public bool TryGetPlayed(out long frames, out long qpc)
        {
            bool valid = session._clock.TryGetPlayed(out long deviceFrames, out qpc);
            frames = Math.Clamp(deviceFrames + Interlocked.Read(ref session._deviceFrameOrigin), 0, session.SubmittedFrames);
            return valid;
        }
    }
    private int _pendingFrames, _pendingOffset;
    private TransportRamp _transport = new(1f);
    private int _transportPhase; // output-owned: 0 running, 1 fading, 2 draining, 3 held
    private long _fadeTailSubmitted;
    private int _startRequested;
    private volatile bool _transportHoldRequested;
    private volatile bool _fadeInSpecified;
    private long _transportRevision;
    private long _seekRevision;
    private readonly SemaphoreSlim _replacementGate = new(1, 1);
    private bool _formatRequiresReload;
    private long _activeMixerStart;
    private readonly System.Collections.Generic.Dictionary<long, long> _voiceStarts = new();
    /// <summary>Output format changed; the graph must be recreated before new-rate PCM may be submitted.</summary>
    public bool RequiresGraphRebuild => _formatRequiresReload;
    /// <summary>Raised after endpoint replacement with the captured source-domain played position.</summary>
    public event Action<MixFormat, long>? DeviceRebuilt;

    /// <summary>Frames accepted by the current endpoint epoch.</summary>
    public long SubmittedFrames => Interlocked.Read(ref _submittedFrames);
    /// <summary>Played frames sampled off RT, bounded by submitted PCM.</summary>
    public long PlayedFrames => Interlocked.Read(ref _playedFrames);
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
    private EqStage? _voiceEq;            // the primary voice's EQ stage (gain-only ramps land here, no republish)
    private long _eqTopologySig = long.MinValue;   // last-applied EQ topology (enabled/count/type/freq/Q) — NOT gain
    private float[] _lastBandGains = Array.Empty<float>();

    // ── visualizer tap (spec §7.3/§7.8): a post-master level/peak snapshot published off the block path ──────────────
    private float _tapRms, _tapPeak;
    private bool _tapDirty;

    private MediaSignalSink? _sink;
    private PlaybackState _state = PlaybackState.Idle;
    private bool _playRequested;
    private bool _metaPublished;
    private bool _started;
    private bool _disposed;


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
    // Fix 4a: the previous process-wide GC latency mode, restored on dispose; set for the lifetime of a live session so a
    // Gen2 collection can't suspend the RT thread mid-callback.
    private GCLatencyMode? _prevGcLatencyMode;
    private bool _warmedUp;   // fix 4b — the one-time pre-Start() warm-up pass (see WarmUp())

    private IAudioSource? _voice;
    private long _voiceTotalFrames;
    private TimeSpan _duration;
    private NormMode _norm = NormMode.Album;
    private float _refLufs = -14f;

    private float _volume = 1f;
    private bool _muted;
    private double _rate = 1.0;

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
    /// <summary>Estimated queued frames from submitted PCM and the last published played-clock sample.
    /// Safe for diagnostics callers: this cached snapshot never queries an endpoint or its native clock.</summary>
    public int DevicePaddingFrames => (int)Math.Clamp(SubmittedFrames - PlayedFrames, 0L, int.MaxValue);
    /// <summary>The active normalization mode.</summary>
    public NormMode NormalizationMode => _norm;
    /// <summary>The active reference LUFS.</summary>
    public float ReferenceLufsValue => _refLufs;
    /// <summary>The mixer-domain frame currently consumed (the sample clock the scheduler ticks on).</summary>
    public long SampleClock => _mixer.ConsumeSeq;
    /// <summary>RT-feed ring underruns (silence written) since this session opened. 0 on the single-thread pull path.</summary>
    public long XrunCount => _feed?.XrunCount ?? 0;

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

    // CONTROL: a seek/flush was just requested — start suppressing xrun accounting for the rebuffer it causes.
    private void ArmSeekRebufferSuppression()
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
            _volume = initialVolume;
            _masterGain.SetLinear(initialVolume);

            float rg = ReplayGain.ScalarLinear(voice.Loudness, norm, referenceLufs);
            var chain = _graph.Live.BuildVoiceChain();
            _voiceEq = FindEq(chain);
            // RT path: the mixer reads pre-decoded PCM from a ring the worker fills (decode is off the RT thread; spec §7.9).
            // Single-thread pull path: the mixer reads the decoder directly (unchanged — golden-PCM identical). _voice stays the
            // inner decoder so Seek/loudness address the real source. Wrap publishes the ring table FIRST (immediate); the
            // mixer voice swap goes through the command SPSC (applied at RenderBlock's top on the render thread).
            var mixSrc = _feed is not null ? _feed.Wrap(voice) : voice;
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
        ActiveVoiceIdValue = voiceId;
        _activeMixerStart = _voiceStarts.TryGetValue(voiceId, out long start) ? start : 0;
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

            var src = _feed is not null ? _feed.WrapAdditional(voice, id) : voice;
            _voiceStarts[id] = startFrame;
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
        while (!_disposed && Interlocked.Read(ref _transportRevision) == revision && Volatile.Read(ref _transportPhase) != 3)
            await Task.Delay(2, ct).ConfigureAwait(false);
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
            while (!TrySetVoice(voice, prepared.Duration, prepared.TotalFrames, _norm, _refLufs, _volume))
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PcmAudioSession));
                await Task.Delay(2).ConfigureAwait(false);
            }
            if (prepared is AudioPreparedItem audio) audio.TransferOwnership();
            await WaitAppliedAsync(Interlocked.Read(ref _commandSequence), CancellationToken.None).ConfigureAwait(false);
            long installedPosition = useSourcePosition ? Math.Max(0, voice.PositionFrames) : Math.Max(0, positionFrames);
            _position.Reset();
            _position.Rebase(0, installedPosition);
            _sink?.Duration(prepared.Duration);
            _sink?.Position(TimeSpan.FromSeconds((double)installedPosition / Format.SampleRate));
            _sink?.SettleTransport();
            Publish(_playRequested ? PlaybackState.Ready : PlaybackState.Paused);
            return installedPosition;
        }
        finally { _replacementGate.Release(); }
    }

    private async ValueTask<long> PostMixerCommandAsync(MixerCmd command, CancellationToken ct)
    {
        while (!_disposed)
        {
            ct.ThrowIfCancellationRequested();
            if (TryEnqueueMixerCmd(command, out long sequence)) return sequence;
            await Task.Delay(2, ct).ConfigureAwait(false);
        }
        throw new ObjectDisposedException(nameof(PcmAudioSession));
    }

    private async ValueTask WaitAppliedAsync(long sequence, CancellationToken ct)
    {
        while (!_disposed && Interlocked.Read(ref _appliedSequence) < sequence)
            await Task.Delay(2, ct).ConfigureAwait(false);
        if (_disposed) throw new ObjectDisposedException(nameof(PcmAudioSession));
        ct.ThrowIfCancellationRequested();
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
            if (_disposed || !HasMixerCapacity(cmd.Kind is CmdFadeOut or CmdFadeIn or CmdRemoveVoice or CmdReset)) return false;
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
    private void DrainMixerCmds()
    {
        int head = _mixerCmdHead;
        while (head != Volatile.Read(ref _mixerCmdTail))
        {
            ref var c = ref _mixerCmdQ[head];
            switch (c.Kind)
            {
                case CmdReplacePrimary:
                    if (_feed is not null)
                        foreach (var old in _mixer.VoicesSpan)
                            if (old.Src is RingAudioSource retired && !ReferenceEquals(retired, c.Voice.Src)) _feed.EnqueueRetire(retired);
                    if (_feed is null)
                        foreach (var old in _mixer.VoicesSpan)
                            if (!ReferenceEquals(old.Src, c.Voice.Src)) (old.Src as IDisposable)?.Dispose();
                    _mixer.Clear();
                    _mixer.AddVoice(in c.Voice);
                    break;
                case CmdAddVoice:       _mixer.AddVoice(in c.Voice); break;
                case CmdSetEnvelope:    _mixer.TrySetVoiceEnvelope(c.Id, c.Env!); break;
                case CmdRemoveVoice:
                    foreach (var removed in _mixer.VoicesSpan)
                        if (removed.Id == c.Id)
                        {
                            if (_feed is not null && removed.Src is RingAudioSource ring) _feed.EnqueueRetire(ring);
                            else if (_feed is null) (removed.Src as IDisposable)?.Dispose();
                            break;
                        }
                    _mixer.RemoveVoice(c.Id);
                    break;
                case CmdFadeOut:
                    if (!_started)
                    { _transport = new TransportRamp(0f); Volatile.Write(ref _transportPhase, 3); }
                    else if (_starvationPhase != 0)
                    { _fadeTailSubmitted = _submittedFrames; Volatile.Write(ref _transportPhase, 2); }
                    else
                    { _transport.Retarget(0f, _mixer.ConsumeSeq, c.Frames); Volatile.Write(ref _transportPhase, 1); }
                    break;
                case CmdFadeIn:
                    if (!_started && _submittedFrames == 0) _transport = new TransportRamp(0f);
                    _transport.Retarget(1f, _mixer.ConsumeSeq, c.Frames);
                    Volatile.Write(ref _transportPhase, 0);
                    if (!_started && _playRequested) Volatile.Write(ref _startRequested, 1);
                    break;
                case CmdSeekAnchor:
                    for (int i = _mixer.VoicesSpan.Length - 1; i >= 0; i--)
                    {
                        var voice = _mixer.VoicesSpan[i];
                        if (voice.Id != c.Id)
                        {
                            _mixer.RemoveVoice(voice.Id);
                            if (voice.Src is RingAudioSource ring) _feed?.EnqueueRetire(ring);
                            if (_feed is null) (voice.Src as IDisposable)?.Dispose();
                        }
                        else
                        {
                            _mixer.VoicesSpan[i].StartFrame = 0;
                            _mixer.VoicesSpan[i].Env = GainEnvelope.Constant;
                        }
                    }
                    break;
                case CmdReset:
                    _out.Stop();
                    _started = false;
                    if (_out is IBufferedAudioSink buffered) buffered.Reset();
                    if (_clock is SyntheticAudioClock synthetic) synthetic.Reset();
                    _pendingFrames = _pendingOffset = 0;
                    _submittedFrames = _playedFrames = _deviceFrameOrigin = 0;
                    _starvationPhase = 0;
                    _starvedAt = 0;
                    _starvedRing = null;
                    _mixer.ConsumeSeq = 0;
                    Interlocked.Increment(ref _renderEpoch);
                    break;
            }
            Volatile.Write(ref _appliedSequence, c.Sequence);
            c = default;   // release refs (Src / Chain / Env)
            head = (head + 1) & (_mixerCmdQ.Length - 1);
        }
        Volatile.Write(ref _mixerCmdHead, head);
    }

    private bool MixerCmdsPending => Volatile.Read(ref _mixerCmdHead) != Volatile.Read(ref _mixerCmdTail);

    /// <summary>Compute the per-source ReplayGain linear scalar under the session's current normalization/reference-LUFS
    /// for a crossfade voice (spec §7.7) — the scalar to pass to <see cref="AddCrossfadeVoice"/>.</summary>
    public float ReplayGainScalarFor(in ReplayGainInfo loudness) => ReplayGain.ScalarLinear(loudness, _norm, _refLufs);
    /// <summary>The fixed-format graph (for the queue scheduler's per-voice chain factory).</summary>
    public IDspStage[]? BuildVoiceChain() => _graph.Live.BuildVoiceChain();
    /// <summary>The primary voice's live EQ stage (for effects tests), or null when EQ is disabled.</summary>
    public EqStage? PrimaryVoiceEq => _voiceEq;
    /// <summary>The primary voice's current ReplayGain scalar (for effects/normalization tests).</summary>
    public float PrimaryVoiceReplayGainScalar
    {
        get
        {
            var span = _mixer.VoicesSpan;
            for (int i = 0; i < span.Length; i++) if (span[i].Id == PrimaryVoiceId) return span[i].ReplayGainScalar;
            return 1f;
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
        _eqTopologySig = EqTopologySignature(effects.Equalizer);
        SnapshotBandGains(effects.Equalizer);
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
            // A freq/Q/type/count/enabled change (spec §7.8): recompute coefficients OFF the block path and RE-PUBLISH the
            // graph (old graph retires under RenderInFlightDepth+1 quarantine); cross-ramp the live voice EQ so it is audible.
            _eqTopologySig = sig;
            _graph.Publish(PcmAudioPlayer.BuildGraphSpec(fx, _format));
            ApplyBandsToVoiceEq(eq);
            SnapshotBandGains(eq);
        }
        else if (_voiceEq is not null && eq.Enabled.Peek())
        {
            // Same topology → gain-only ramps per band (spec §7.10: set-vs-ramp is a value, no zipper).
            var bands = eq.Bands;
            for (int i = 0; i < bands.Length && i < _lastBandGains.Length; i++)
            {
                float g = bands[i].GainDb.Peek();
                if (g != _lastBandGains[i]) { _voiceEq.SetBandGain(i, g); _lastBandGains[i] = g; }
            }
        }

        // Balance (smoothed, no republish).
        _masterChannel.SetTargetBalance(fx.Balance.Peek(), _plane.DefaultRampSamples);

        // Normalization / reference LUFS → the per-voice ReplayGain scalar (spec §7.7).
        var norm = fx.Normalization.Peek();
        float refl = fx.ReferenceLufs.Peek();
        if (norm != _norm || refl != _refLufs)
        {
            _norm = norm;
            _refLufs = refl;
            RebaseReplayGain();
        }
    }

    private void RebaseReplayGain()
    {
        var span = _mixer.VoicesSpan;
        for (int i = 0; i < span.Length; i++)
        {
            float rg = ReplayGain.ScalarLinear(span[i].Src.Loudness, _norm, _refLufs);
            span[i].ReplayGainScalar = rg;
        }
    }

    private void ApplyBandsToVoiceEq(Equalizer eq)
    {
        if (_voiceEq is null) return;
        if (!eq.Enabled.Peek() || eq.Bands.Length == 0) { _voiceEq.SetBands(ReadOnlySpan<BiquadBand>.Empty, _format.SampleRate); return; }
        Span<BiquadBand> bands = eq.Bands.Length <= 32 ? stackalloc BiquadBand[eq.Bands.Length] : new BiquadBand[eq.Bands.Length];
        for (int i = 0; i < eq.Bands.Length; i++)
        {
            var b = eq.Bands[i];
            bands[i] = new BiquadBand(b.Type, b.FreqHz.Peek(), b.Q.Peek(), b.GainDb.Peek());
        }
        _voiceEq.SetBands(bands, _format.SampleRate);
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
        if (!_tapDirty || _liveEffects is not AudioEffects ae) return;
        _tapDirty = false;
        ae.PublishVisualizerFrame(new VisualizerFrame(ReadOnlyMemory<float>.Empty, _tapRms, _tapPeak));
    }

    // ── IMediaSession ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        _position.Reset();
        EnterSustainedLowLatency();   // fix 4a — for the lifetime of this live session (restored in DisposeAsync)
        sink.PlayRequested(_playRequested);
        Publish(PlaybackState.Opening);
        if (_driveWithOwnThread) StartFeeder();
    }

    /// <summary>Fix 4a (spec): a blocking Gen2 collection on the managed RT feed thread suspends it mid-callback — a direct
    /// hiccup cause. <see cref="GCSettings.LatencyMode"/> is process-wide, so this captures whatever was in effect and
    /// restores it on <see cref="DisposeAsync"/>; idempotent (a second <see cref="ConnectSignals"/> on the same session
    /// never re-captures over its own already-applied mode).</summary>
    private void EnterSustainedLowLatency()
    {
        if (_prevGcLatencyMode is not null) return;
        try
        {
            _prevGcLatencyMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        catch { _prevGcLatencyMode = null; /* best-effort — an unsupported host must never block session open */ }
    }

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = true;
        _sink?.PlayRequested(true);
        if (!_fadeInSpecified || _transportHoldRequested) FadeIn(TimeSpan.FromMilliseconds(20));
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

    /// <inheritdoc/>
    public async ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return;
        long revision = Interlocked.Increment(ref _seekRevision);
        ArmSeekRebufferSuppression();
        await _replacementGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || revision != Interlocked.Read(ref _seekRevision)) return;
            double hi = _duration > TimeSpan.Zero ? _duration.TotalSeconds : double.MaxValue;
            long frame = (long)Math.Round(Math.Clamp(to.TotalSeconds, 0, hi) * _format.SampleRate);
            await FadeOutAsync(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
            if (_disposed || revision != Interlocked.Read(ref _seekRevision)) return;
            long reset = await PostMixerCommandAsync(new MixerCmd { Kind = CmdReset }, CancellationToken.None).ConfigureAwait(false);
            await WaitAppliedAsync(reset, CancellationToken.None).ConfigureAwait(false);
            long anchor = await PostMixerCommandAsync(new MixerCmd { Kind = CmdSeekAnchor, Id = ActiveVoiceIdValue }, CancellationToken.None).ConfigureAwait(false);
            await WaitAppliedAsync(anchor, CancellationToken.None).ConfigureAwait(false);
            RingAudioSource? ring = null;
            if (_feed is not null)
                foreach (var entry in _feed.RingsSnapshot)
                    if (entry.VoiceId == ActiveVoiceIdValue) { ring = entry.Ring; break; }
            ring ??= _voice as RingAudioSource;
            long achieved;
            if (ring is not null)
            {
                achieved = await ring.SeekFrameAsync(frame).ConfigureAwait(false);
                _feed?.WakeOutput();
                while (!_disposed && revision == Interlocked.Read(ref _seekRevision) && !BufferingReady() && !ring.Exhausted)
                    await Task.Delay(2).ConfigureAwait(false);
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
            _position.Reset();
            _position.Rebase(0, achieved);
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

    /// <inheritdoc/>
    public void SetRate(double rate) { if (!_disposed) _rate = rate <= 0 ? 1.0 : rate; }
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
        if (phase == 2)
        {
            if (_pendingFrames > 0) return SubmitPending();
            bool drained = _out is IBufferedAudioSink buffered
                ? buffered.WritableFrames >= buffered.CapacityFrames
                : PlayedFrames >= _fadeTailSubmitted;
            if (drained)
            {
                _out.Stop();
                _started = false;
                Volatile.Write(ref _transportPhase, 3);
            }
            return 0;
        }
        if (phase == 3 || (_state is not (PlaybackState.Playing or PlaybackState.Stalled)) || (!_playRequested && phase != 1)) return 0;
        if (_starvationPhase != 0 && !RecoverStarvation()) return 0;
        return RenderBlock(frames);
    }

    private bool RecoverStarvation()
    {
        if (_starvationPhase == 1)
        {
            // Recover before the queued cushion drains when possible; no content frames were consumed while waiting.
            if (_mixer.PcmReady(StartupReadinessFrames)) { _starvationPhase = 0; _starvedRing = null; return true; }
            bool drained = _out is IBufferedAudioSink buffered
                ? buffered.WritableFrames >= buffered.CapacityFrames : PlayedFrames >= SubmittedFrames;
            if (!drained) return false;
            _out.Stop();
            _started = false;
            if (_out is IBufferedAudioSink resettable) resettable.Reset();
            if (_clock is SyntheticAudioClock synthetic) synthetic.Reset();
            Interlocked.Exchange(ref _deviceFrameOrigin, SubmittedFrames);
            Interlocked.Increment(ref _renderEpoch);
            _starvedAt = Stopwatch.GetTimestamp();
            _starvationPhase = 2;
        }
        if (!_mixer.PcmReady(StartupReadinessFrames)) return false;
        if (_starvedAt != 0)
        {
            double elapsed = (Stopwatch.GetTimestamp() - _starvedAt) / (double)Stopwatch.Frequency;
            _starvedRing?.RecordStarvedFrames((int)Math.Clamp(Math.Round(elapsed * _format.SampleRate), 1, int.MaxValue));
        }
        _starvedRing = null;
        _starvedAt = 0;
        _starvationPhase = 0;
        _transport = new TransportRamp(0);
        _transport.Retarget(1, _mixer.ConsumeSeq, Math.Max(1, _format.SampleRate / 200));
        Volatile.Write(ref _startRequested, 1);
        return true;
    }

    /// <summary>Wait outside DSP for endpoint capacity or a new transport command.</summary>
    internal void WaitForOutput(WaitHandle controlWake, int timeoutMs)
    {
        if (_out is IBufferedAudioSink buffered) buffered.WaitForWritable(controlWake, timeoutMs);
        else controlWake.WaitOne(timeoutMs);
    }

    private int StartupReadinessFrames
    {
        get
        {
            int capacity = _out is IBufferedAudioSink buffered ? buffered.CapacityFrames : 0;
            int block = _feed?.BlockFrames ?? Math.Min(_maxBlock, Math.Max(1, _format.SampleRate / 100));
            return Math.Max(_format.SampleRate / 10, capacity + 2 * block);
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
                break;

            case PlaybackState.Ready:
            case PlaybackState.Paused:
                if (_playRequested && !_transportHoldRequested) { EnsureStarted(); Publish(PlaybackState.Playing); }
                break;

            case PlaybackState.Stalled:
                if (renderInline) RtRenderOnce(frames);
                PublishPosition(sink);
                if (_starvationPhase == 0 && _playRequested && !_transportHoldRequested) Publish(PlaybackState.Playing);
                else if (!_playRequested && _transportPhase == 3) Publish(PlaybackState.Paused);
                break;

            case PlaybackState.Playing:
                if (_starvationPhase != 0) { Publish(PlaybackState.Stalled); break; }
                if (!_playRequested && Volatile.Read(ref _transportPhase) == 3) { PublishPosition(sink); Publish(PlaybackState.Paused); break; }
                if (renderInline) RtRenderOnce(frames);   // single-thread path; RT path renders on the feed thread instead
                PublishPosition(sink);
                PublishVisualizer();
                // RT path: read the RT-published drained flag (never the render-thread-owned voice list), and never declare
                // Ended while a voice-add command is still queued (spec §12). Single-thread path: read the mixer directly.
                bool drained = _feed is not null ? (_mixer.DrainedPublished && !MixerCmdsPending) : _mixer.IsDrained(_mixer.ConsumeSeq);
                if (drained && _pendingFrames == 0 && (_out is not IBufferedAudioSink drainSink || drainSink.WritableFrames >= drainSink.CapacityFrames))
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
        if (_formatRequiresReload) return 0;
        DrainMixerCmds();
        if (_pendingFrames > 0) return SubmitPending();
        frames = Math.Clamp(frames, 1, _maxBlock);
        if (_out is IBufferedAudioSink buffered)
        {
            int writable = buffered.WritableFrames;
            if (writable < 0) { RecordSinkFailure(); return 0; }
            frames = Math.Min(frames, writable);
            if (frames == 0) return 0;
        }
        int readable = _mixer.ReadableFrames(frames, out var waitingFor);
        if (readable <= 0 && waitingFor is not null)
        {
            _starvedRing = waitingFor;
            _starvationPhase = 1;
            return 0;
        }
        if (readable > 0) frames = Math.Min(frames, readable);
        if (Volatile.Read(ref _transportPhase) == 1)
            frames = (int)Math.Min(frames, Math.Max(1, _transport.EndFrame + 1 - _mixer.ConsumeSeq));
        var buf = _mixBuf.AsSpan(0, frames * _format.Channels);
        var ctx = new BlockCtx(_mixer.ConsumeSeq, _format.SampleRate, _format.Channels, _plane);
        var graph = _graph.Live;
        AudioTripwire.BeginBlock();
        _masterGain.SetTargetLinear(_muted ? 0f : _volume, _plane.DefaultRampSamples);
        _mixer.Render(buf, frames, ctx);
        if (_feed is not null)
        {
            foreach (var retired in _mixer.RetiredSourcesThisBlock)
                if (retired is RingAudioSource ring) _feed.EnqueueRetire(ring);
        }
        _mixer.PublishDrained(_mixer.ConsumeSeq);
        _masterGain.Process(buf, buf, frames, ctx);
        _masterChannel.Process(buf, buf, frames, ctx);
        graph.RenderMaster(buf, frames, ctx);
        for (int frame = 0; frame < frames; frame++)
        {
            float gain = _transport.At(ctx.StartFrame + frame);
            for (int channel = 0; channel < _format.Channels; channel++)
                buf[frame * _format.Channels + channel] *= gain;
        }
        TapBlock(buf, frames);
        _graph.MarkConsumed();
        AudioTripwire.EndBlock();
        if (_feed is null)
            foreach (var retired in _mixer.RetiredSourcesThisBlock) (retired as IDisposable)?.Dispose();
        _position.ExtraLatencySamples = graph.TotalLatencySamples;
        _pendingFrames = frames;
        _pendingOffset = 0;
        if (_transportPhase == 1 && _mixer.ConsumeSeq > _transport.EndFrame)
        {
            _fadeTailSubmitted = _submittedFrames + frames;
            Volatile.Write(ref _transportPhase, 2);
        }
        return SubmitPending();
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
            _out.Start();
            _started = true;
        }
        return written;
    }

    private void RecordSinkFailure()
    {
        if (++_consecutiveSinkFailures < SinkFailureRebuildThreshold) return;
        _consecutiveSinkFailures = 0;
        _deviceController?.RequestRebuild();
    }

    private void TapBlock(ReadOnlySpan<float> buf, int frames)
    {
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
        _tapPeak = peak;
        _tapRms = (float)Math.Sqrt(sumSq / n);
        _tapDirty = true;
    }

    private void PublishPosition(MediaSignalSink sink)
    {
        _position.SubmittedFrameLimit = SubmittedFrames;
        _position.IsAdvancing = _started && _transportPhase != 3;
        _position.Sample(_presentationClock);
        if (_presentationClock.TryGetPlayed(out long played, out _))
            Interlocked.Exchange(ref _playedFrames, Math.Clamp(played, 0, SubmittedFrames));
        sink.Position(_position.Project(NowTicks100ns()));
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
    /// short fade-in avoids a resume click. Runs OFF the RT thread (the cold device thread). Never throws. Returns false if
    /// the session was disposed.</summary>
    public bool RebuildSink(IAudioEndpoint newEndpoint)
    {
        if (_disposed || newEndpoint is null) return false;

        // Capture the current timeline position (frames) so it continues seamlessly across the swap.
        long posFrames = Math.Max(0, _position.PlayedFramesCompensated);
        long sourcePosition = Math.Max(0, posFrames - _activeMixerStart);
        int previousRate = _format.SampleRate;

        var oldSink = _out;
        var oldEndpoint = _endpoint;
        try { oldSink.Stop(); } catch { /* teardown never throws */ }

        _out = newEndpoint.Sink;
        _clock = newEndpoint.Clock;
        _endpoint = newEndpoint;
        _started = false;

        // Re-anchor: the new device clock starts at 0 played frames == the current timeline position (spec §7.6).
        _position.Reset();
        _position.Rebase(0, posFrames);

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
        Interlocked.Increment(ref _renderEpoch);
        _submittedFrames = _playedFrames = _deviceFrameOrigin = 0;
        _starvationPhase = 0;
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

        var onRebuilt = DeviceRebuilt;
        if (onRebuilt is not null)
        {
            _transportHoldRequested = true;
            _transport = new TransportRamp(0);
            Volatile.Write(ref _transportPhase, 3);
            ThreadPool.QueueUserWorkItem(static state =>
            {
                try { state.Callback(state.Format, state.Position); } catch { }
            }, (Callback: onRebuilt, Format: newFormat, Position: (long)Math.Round(sourcePosition * (double)newFormat.SampleRate / previousRate)), preferLocal: false);
        }
        else if (!rateChanged && (_state == PlaybackState.Playing || _playRequested)) EnsureStarted();
        return true;
    }

    private void SeekToStart()
    {
        // RT path: worker-routed (sole inner-decoder toucher — spec §7.9/§12); single-thread path: inline.
        if (_feed is not null) { _feed.RequestSeek(0); ArmSeekRebufferSuppression(); }
        else if (_voice is DecoderAudioSource das) das.SeekFrame(0);
        else if (_voice is TrimmingSource ts) ts.SeekFrame(0);
        else if (_voice is MemoryAudioSource mas) mas.SeekFrame(0);
        _clock.TryGetPlayed(out long playedNow, out _);
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
        // Fix 4a: restore whatever process-wide GC latency mode was in effect before this session went live.
        if (_prevGcLatencyMode is GCLatencyMode prevMode)
        {
            try { GCSettings.LatencyMode = prevMode; } catch { /* teardown never throws */ }
            _prevGcLatencyMode = null;
        }
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
