using System;
using System.Collections.Immutable;
using System.Diagnostics;
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
    public async ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct)
    {
        var byteSource = ResolveByteSource(next)
            ?? throw new NotSupportedException("PcmAudioPlayer prepares FromFile/FromStream/FromBytes/FromPull sources.");
        var decoder = _decoderFactory(ctx.Format);
        DecodedInfo info = default;
        bool ok = await Task.Run(() =>
        {
            bool r = decoder.TryOpen(byteSource, ctx.Format, out var i);
            info = i;
            return r;
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!ok) throw new InvalidOperationException("The next audio source could not be decoded (unsupported or corrupt WAV/PCM).");

        var loudness = info.Loudness;
        var voice = new DecoderAudioSource(decoder, loudness);
        return new AudioPreparedItem(voice, decoder.Gapless, loudness, MixFrames(decoder, info.Duration, ctx.Format), info.Duration);
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

    /// <inheritdoc/>
    public MediaCapabilities Capabilities { get; } = new(SupportsVideo: false, SupportsAudioGraph: true, SupportsDrm: false)
    {
        IsSupported = static ct => ct.Audio is CodecId.None or CodecId.Pcm or CodecId.Vorbis or CodecId.Aac
                                        or CodecId.Opus or CodecId.Flac or CodecId.Mp3,
    };

    /// <inheritdoc/>
    public async ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
    {
        var byteSource = ResolveByteSource(source)
            ?? throw new NotSupportedException("PcmAudioPlayer supports FromFile/FromStream/FromBytes/FromPull sources.");

        var decoder = _decoderFactory(Format);
        DecodedInfo info = default;
        bool opened = await Task.Run(() =>
        {
            bool ok = decoder.TryOpen(byteSource, Format, out var i);
            info = i;
            return ok;
        }, ct).ConfigureAwait(false);

        if (!opened)
            throw new InvalidOperationException("The audio source could not be decoded (unsupported or corrupt WAV/PCM).");

        ct.ThrowIfCancellationRequested();

        var loudness = ResolveLoudness(source, info);
        var voice = new DecoderAudioSource(decoder, loudness);
        long totalFrames = MixFrames(decoder, info.Duration, Format);

        var endpoint = _endpointFactory(Format);
        var session = new PcmAudioSession(Format, endpoint.Sink, endpoint.Clock, _maxBlock, _driveWithOwnThread, endpoint);
        session.Configure(BuildGraphSpec(_effects, Format));
        _onSessionCreated?.Invoke(session);   // M4 (on-box): attach the RT feed BEFORE SetVoice so the voice is ring-wrapped
        var (norm, refLufs) = ResolveNorm(_effects, opts);
        session.SetVoice(voice, info.Duration, totalFrames, norm, refLufs, initialVolume: 1f);
        return session;
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

    private readonly MixFormat _format;
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
    private bool _everPlayed;
    private bool _metaPublished;
    private bool _started;
    private bool _disposed;
    private int _prefillPumps = 1;

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
    public long VoiceTotalFrames => _voiceTotalFrames;
    /// <summary>The active normalization mode.</summary>
    public NormMode NormalizationMode => _norm;
    /// <summary>The active reference LUFS.</summary>
    public float ReferenceLufsValue => _refLufs;
    /// <summary>The mixer-domain frame currently consumed (the sample clock the scheduler ticks on).</summary>
    public long SampleClock => _mixer.ConsumeSeq;

    /// <summary>Publish an audio graph (spec §7.4 atomic swap). Control-thread only.</summary>
    public void Configure(AudioGraphSpec spec) => _graph.Publish(spec);

    /// <summary>Attach the M4 RT feed (spec §7.9): after this, <see cref="SetVoice"/> installs a decode↔RT firewall ring
    /// around the voice (the worker decodes ahead, the RT thread mixes copy-only). Control-thread only; call before opening.</summary>
    public void AttachFeed(AudioFeedThread feed) => _feed = feed;

    /// <summary>Register an owned resource (the device watcher/controller wired on-box) disposed with the session.</summary>
    public void RegisterDisposable(IDisposable resource) => (_owned ??= new()).Add(resource);
    private System.Collections.Generic.List<IDisposable>? _owned;

    /// <summary>True once an RT feed is attached (the render is driven by <see cref="RtRenderOnce"/>, not the inline pump).</summary>
    public bool IsRtDriven => _feed is not null;

    /// <summary>Install the single M2 voice (queue/crossfade prepares more in M3). Bakes ReplayGain per-source (spec §7.7)
    /// under <paramref name="norm"/>/<paramref name="referenceLufs"/> and builds the per-voice DSP chain from the live graph.</summary>
    public void SetVoice(IAudioSource voice, TimeSpan duration, long totalFrames, NormMode norm, float referenceLufs, float initialVolume)
    {
        _voice = voice;
        _duration = duration;
        _voiceTotalFrames = totalFrames;
        _norm = norm;
        _refLufs = referenceLufs;
        _volume = initialVolume;
        _masterGain.SetLinear(initialVolume);

        float rg = ReplayGain.ScalarLinear(voice.Loudness, norm, referenceLufs);
        var chain = _graph.Live.BuildVoiceChain();
        _voiceEq = FindEq(chain);
        _mixer.Clear();
        // RT path: the mixer reads pre-decoded PCM from a ring the worker fills (decode is off the RT thread; spec §7.9).
        // Single-thread pull path: the mixer reads the decoder directly (unchanged — golden-PCM identical). _voice stays the
        // inner decoder so Seek/loudness address the real source.
        var mixSrc = _feed is not null ? _feed.Wrap(voice) : voice;
        _mixer.AddVoice(new MixVoice
        {
            Id = PrimaryVoiceId,
            Src = mixSrc,
            Env = GainEnvelope.Constant,
            StartFrame = 0,
            ReplayGainScalar = rg,
            Chain = chain,
        });
    }

    private static EqStage? FindEq(IDspStage[]? chain)
    {
        if (chain is null) return null;
        for (int i = 0; i < chain.Length; i++) if (chain[i] is EqStage eq) return eq;
        return null;
    }

    /// <summary>The mixer (queue/crossfade scheduler wires prepared voices into it).</summary>
    public CrossfadeMixer MixerRef => _mixer;
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
        sink.PlayRequested(_playRequested);
        Publish(PlaybackState.Opening);
        if (_driveWithOwnThread) StartFeeder();
    }

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = true;
        _everPlayed = true;
        _sink?.PlayRequested(true);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = false;
        _sink?.PlayRequested(false);
        if (_state == PlaybackState.Playing) Publish(PlaybackState.Paused);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return ValueTask.CompletedTask;
        double hi = _duration > TimeSpan.Zero ? _duration.TotalSeconds : double.MaxValue;
        double sec = Math.Clamp(to.TotalSeconds, 0.0, hi);
        long frame = (long)Math.Round(sec * _format.SampleRate);

        if (_voice is DecoderAudioSource das) das.SeekFrame(frame);
        else if (_voice is MemoryAudioSource mas) mas.SeekFrame(frame);

        // Anchor the position domain to the device clock's current count (spec §7.6): device keeps counting; origin re-maps.
        _clock.TryGetPlayed(out long playedNow, out _);
        _position.Rebase(playedNow, frame);

        _sink?.Position(TimeSpan.FromSeconds((double)frame / _format.SampleRate));
        _sink?.SettleTransport();
        return ValueTask.CompletedTask;
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
        if (_disposed || _sink is null) return 0;
        if (_state != PlaybackState.Playing || !_playRequested) return 0;
        return RenderBlock(frames);
    }

    private PlaybackState Advance(int frames, bool renderInline)
    {
        if (_disposed || _sink is null) return _state;
        var sink = _sink;
        frames = Math.Clamp(frames, 1, _maxBlock);

        ReconcileEffects();   // control-thread: fold live effect-signal changes into the graph/plane (spec §7.10)

        switch (_state)
        {
            case PlaybackState.Opening:
                PublishMetadata(sink);
                Publish(PlaybackState.Buffering);
                break;

            case PlaybackState.Buffering:
                if (--_prefillPumps <= 0)
                {
                    sink.Buffer(new BufferHealth(Array.Empty<TimeRange>(),
                        _duration < TimeSpan.FromSeconds(30) ? _duration : TimeSpan.FromSeconds(30), false, StallPolicy.Rebuffer));
                    Publish(PlaybackState.Ready);
                    if (_playRequested) { EnsureStarted(); Publish(PlaybackState.Playing); }
                }
                break;

            case PlaybackState.Ready:
            case PlaybackState.Paused:
                if (_playRequested) { EnsureStarted(); Publish(PlaybackState.Playing); }
                break;

            case PlaybackState.Playing:
                if (!_playRequested) { Publish(PlaybackState.Paused); break; }
                if (renderInline) RenderBlock(frames);   // single-thread path; RT path renders on the feed thread instead
                PublishPosition(sink);
                PublishVisualizer();
                if (_mixer.IsDrained(_mixer.ConsumeSeq))
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

    /// <summary>Pull ONE block through the full graph (mixer → master volume → master chain incl. limiter → sink), advance
    /// the clock, and mark one graph-consume step. Pure DSP: zero managed allocation (the §7.9 RT tripwire scope). This is
    /// what the golden-PCM + zero-alloc gates drive directly.</summary>
    public int RenderBlock(int frames)
    {
        frames = Math.Clamp(frames, 1, _maxBlock);
        int n = frames * _format.Channels;
        var buf = _mixBuf.AsSpan(0, n);
        var ctx = new BlockCtx(_mixer.ConsumeSeq, _format.SampleRate, _format.Channels, _plane);

        AudioTripwire.BeginBlock();

        _masterGain.SetTargetLinear(_muted ? 0f : _volume, _plane.DefaultRampSamples);
        var graph = _graph.Live;

        _mixer.Render(buf, frames, ctx);
        _masterGain.Process(buf, buf, frames, ctx);
        _masterChannel.Process(buf, buf, frames, ctx);
        graph.RenderMaster(buf, frames, ctx);
        _out.Write(buf, frames);

        TapBlock(buf, frames);   // post-master level/peak for the visualizer (spec §7.3 Tap node) — alloc-free

        if (_clock is SyntheticAudioClock sc) sc.Advance(frames);   // headless: the pump IS the device
        _graph.MarkConsumed();

        AudioTripwire.EndBlock();

        _position.ExtraLatencySamples = graph.TotalLatencySamples;
        return frames;
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
        _position.Sample(_clock);
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
        if (_started) return;
        _started = true;
        _out.Start();
    }

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

        if (_state == PlaybackState.Playing || _playRequested) EnsureStarted();
        return true;
    }

    private void SeekToStart()
    {
        if (_voice is DecoderAudioSource das) das.SeekFrame(0);
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
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _feed?.Dispose(); } catch { /* teardown never throws */ }   // stop the RT/worker/clock threads first
        _feed = null;
        if (_owned is not null) { foreach (var d in _owned) { try { d.Dispose(); } catch { } } _owned = null; }
        _pumpRun = false;
        _pumpThread?.Join(200);
        _pumpThread = null;
        try { _out.Stop(); } catch { /* teardown never throws */ }
        try { _endpoint?.Dispose(); } catch { /* teardown never throws */ }
        _sink = null;
        _voice = null;
        return ValueTask.CompletedTask;
    }
}
