using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Windows.Wasapi;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The ONE real audio host (Milestone M6). Implements the app's IAudioHost seam over the unified FluentGpu.Media engine:
// PcmAudioPlayer (the graph — mixer/DSP/limiter/clock) + WasapiPcm (the device leaf) with an APP-supplied IAudioDecoder
// factory (Vorbis/FLAC/MP3) plugged into the engine's decode edge. Encrypted-stream FETCH + DECRYPT + head/body fast-start
// reuse the kept app seams (SpotifyAudioStream + SpotifyAesCtr + the PlayPlay CdnDecryptor) verbatim, in-proc; the engine
// owns decode→mix→output. This REPLACES the old AudioPlayEngine/DecodePipeline/WasapiRenderer/InProcessAudioHost path.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The app AES-CTR primitive behind the engine's <see cref="ICtrCipher"/> seam (spec §5.4): the counter is
/// re-derived from the byte offset per call, so any range decrypts without replay. Reuses <see cref="SpotifyAesCtr"/> —
/// the exact in-proc decrypt the old path used.</summary>
public sealed class SpotifyCtrCipher : ICtrCipher
{
    private readonly byte[] _key;
    private long _pos;

    public SpotifyCtrCipher(ReadOnlyMemory<byte> key) => _key = key.ToArray();

    public void SeekCounter(long bytePosition) => _pos = bytePosition;

    public void XorInPlace(Span<byte> buffer)
    {
        SpotifyAesCtr.DecryptInPlace(buffer, _key, _pos);
        _pos += buffer.Length;
    }
}

/// <summary>Resolves an <see cref="AudioKey"/> for a track behind the engine's <see cref="IAudioKeyProvider"/> seam, over
/// the app's <see cref="AudioKeyResolver"/>. NOTE: in the live flow the key is pre-resolved during track resolution and
/// delivered on the <see cref="AudioStreamHandle"/> (the engine contract — "prefetched at Prepare time, never inside a
/// read"), so this adapter is the portable-seam form; the hot path consumes the handle-carried key directly.</summary>
public sealed class WaveeAudioKeyProvider : IAudioKeyProvider
{
    private readonly AudioKeyResolver _resolver;
    private readonly Func<string, (ReadOnlyMemory<byte> FileId, ReadOnlyMemory<byte> Gid)?> _lookup;

    public WaveeAudioKeyProvider(AudioKeyResolver resolver,
        Func<string, (ReadOnlyMemory<byte> FileId, ReadOnlyMemory<byte> Gid)?> fileLookup)
    { _resolver = resolver; _lookup = fileLookup; }

    public async ValueTask<AudioKey> ResolveKeyAsync(FluentGpu.Foundation.StringId trackUri, CancellationToken ct)
    {
        var id = _lookup(trackUri.ToString() ?? "");
        if (id is not { } ids) throw new InvalidOperationException("no resolved file id for " + trackUri);
        var key = await _resolver.GetKeyAsync(ids.FileId, ids.Gid, ct).ConfigureAwait(false);
        return new AudioKey(key);
    }
}

/// <summary>The decoder kind for a Spotify/podcast file.</summary>
internal enum WaveeDecoderKind { Vorbis, Flac, Mp3 }

/// <summary>The fast-start bridge (spec §5.1) — the engine's <see cref="IMediaByteSource"/> front door. Carries ONE kept
/// <see cref="IAudioReadStream"/> (a <see cref="SpotifyAudioStream"/> whose clear head is present from <c>LoadFastStart</c>
/// and whose encrypted body is attached later by <c>SupplyBody</c>, or a <see cref="PlainHttpAudioStream"/> for external
/// podcasts) plus the codec kind/duration/gain the decoder needs. The engine passes THIS to the injected decoder's
/// <c>TryOpen</c>, which pulls the decoded stream via <see cref="OpenDecodeStream"/>. Decrypt happens inside the kept
/// stream (in-proc) — invisible above this seam.</summary>
internal sealed class SpotifyMediaByteSource : IMediaByteSource
{
    private readonly IAudioReadStream _stream;
    private readonly int _skipOffset;

    public SpotifyMediaByteSource(IAudioReadStream stream, int skipOffset, WaveeDecoderKind kind, long durationMs, float gainLinear)
    { _stream = stream; _skipOffset = skipOffset; Kind = kind; DurationMs = durationMs; GainLinear = gainLinear; }

    public WaveeDecoderKind Kind { get; }
    public long DurationMs { get; }
    public float GainLinear { get; }

    /// <summary>Open a fresh forward decode view (the codec owns it). The <see cref="SkipStream"/> presents byte
    /// <c>skipOffset</c> as logical 0 (past the Spotify container header).</summary>
    public Stream OpenDecodeStream() => new SkipStream(_stream.AsStream(), _skipOffset);

    // The decoder reads via OpenDecodeStream, not this seam — these satisfy the interface but are inert on this path.
    public bool TryOpen(in DataSpec spec) => true;
    public int Read(Span<byte> dst) => 0;
    public long Seek(long offset) => 0;
    public long? Length => _stream.KnownSize > 0 ? _stream.KnownSize : null;
    public SourceCaps Caps => new() { Seekable = false, KnownLength = false };
    public void Cancel() { }
    public void Close() { }   // the host owns the underlying stream lifecycle
}

/// <summary>The app-side <see cref="IAudioDecoder"/> that plugs the kept codec leaves (<see cref="ISampleSource"/> —
/// Vorbis/FLAC/MP3) into the engine's decode edge. Reads interleaved f32 from the codec at the SOURCE rate, conforms to
/// the target channel count, and resamples INTO the fixed mix format via the engine's <see cref="LinearResampler"/> — so
/// the engine mixer/DSP/output stay codec-agnostic (spec §5.5). Per-track normalization gain is baked here (matching the
/// old DecodePipeline), so engine ReplayGain stays unity.</summary>
internal sealed class SpotifyEngineAudioDecoder : IAudioDecoder
{
    private const int MaxSrcFramesPerRead = 4096;

    private WaveeDecoderKind _kind;
    private long _durationMs;
    private float _gainLinear;

    private ISampleSource? _reader;
    private MixFormat _target;
    private int _srcChannels;
    private LinearResampler? _resampler;
    private float[] _srcScratch = Array.Empty<float>();      // codec-native channels, source rate
    private float[] _conformed = Array.Empty<float>();       // target channels, source rate
    private bool _eof;

    public GaplessInfo Gapless => GaplessInfo.None;

    public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
    {
        info = default;
        if (src is not SpotifyMediaByteSource sp)
            throw new NotSupportedException("SpotifyEngineAudioDecoder requires a SpotifyMediaByteSource.");
        _target = target;
        _kind = sp.Kind;
        _durationMs = sp.DurationMs;
        _gainLinear = sp.GainLinear;
        var stream = sp.OpenDecodeStream();
        _reader = _kind switch
        {
            WaveeDecoderKind.Flac => new FlacSampleSource(stream),
            WaveeDecoderKind.Mp3 => new Mp3SampleSource(stream),
            _ => new VorbisSampleSource(stream),
        };
        _srcChannels = Math.Max(1, _reader.Channels);
        int srcRate = _reader.SampleRate > 0 ? _reader.SampleRate : target.SampleRate;

        _resampler = srcRate != target.SampleRate ? new LinearResampler(srcRate, target.SampleRate, target.Channels) : null;
        _srcScratch = new float[MaxSrcFramesPerRead * _srcChannels];
        _conformed = new float[MaxSrcFramesPerRead * target.Channels];

        WaveeLog.Instance.Event(WaveeLogLevel.Info, "audio", "audiodiag.decoder",   // TEMP: confirm the resample edge
            $"[audiodiag] decoder kind={_kind} srcRate={srcRate} targetRate={target.SampleRate} srcCh={_srcChannels} targetCh={target.Channels} resampler={(_resampler is { IsActive: true } ? "active" : "passthrough")} gain={_gainLinear:0.000}");

        var codec = _kind switch
        {
            WaveeDecoderKind.Flac => new MediaContentType(Container.Flac, CodecId.None, CodecId.Flac),
            WaveeDecoderKind.Mp3 => new MediaContentType(Container.Mp3, CodecId.None, CodecId.Mp3),
            _ => new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis),
        };
        var dur = _durationMs > 0 ? TimeSpan.FromMilliseconds(_durationMs) : TimeSpan.Zero;
        info = new DecodedInfo(codec, new MixFormat(srcRate, _srcChannels), dur, default);
        return true;
    }

    public int Read(Span<float> dst)
    {
        if (_reader is null || _eof) return 0;
        // A late worker pump against a stream torn down by a concurrent session dispose is silence/EOF, never a throw — the
        // engine's per-loop containment is the outer net; this keeps the decode edge itself non-fatal.
        try
        {
            int ch = _target.Channels;
            int wantFrames = dst.Length / ch;
            if (wantFrames <= 0) return 0;

            int srcFrames;
            if (_resampler is { IsActive: true })
            {
                double ratio = (double)(_reader.SampleRate <= 0 ? _target.SampleRate : _reader.SampleRate) / _target.SampleRate;
                srcFrames = (int)Math.Min(MaxSrcFramesPerRead, Math.Ceiling(wantFrames * ratio) + 2);
            }
            else srcFrames = Math.Min(MaxSrcFramesPerRead, wantFrames);

            int gotSrc = ReadSource(srcFrames);
            if (gotSrc <= 0) { _eof = true; return 0; }

            var conformed = _conformed.AsSpan(0, gotSrc * ch);
            int outFrames;
            if (_resampler is { IsActive: true } rs) outFrames = rs.Process(conformed, gotSrc, dst);
            else { conformed.CopyTo(dst); outFrames = gotSrc; }

            if (_gainLinear != 1f)
            {
                int n = outFrames * ch;
                for (int i = 0; i < n; i++) dst[i] *= _gainLinear;
            }
            return outFrames;
        }
        catch (ObjectDisposedException) { _eof = true; return 0; }
    }

    // Pull up to srcFrames codec frames and channel-conform into _conformed (target channels, source rate).
    private int ReadSource(int srcFrames)
    {
        int wantSamples = srcFrames * _srcChannels;
        int got = _reader!.ReadSamples(_srcScratch, 0, wantSamples);
        int framesGot = got / _srcChannels;
        if (framesGot <= 0) return 0;

        int ch = _target.Channels;
        var outv = _conformed.AsSpan(0, framesGot * ch);
        for (int f = 0; f < framesGot; f++)
        {
            int ib = f * _srcChannels;
            float l = _srcScratch[ib];
            float r = _srcChannels >= 2 ? _srcScratch[ib + 1] : l;
            int ob = f * ch;
            if (ch == 1) outv[ob] = _srcChannels >= 2 ? (l + r) * 0.5f : l;
            else { outv[ob] = l; outv[ob + 1] = r; for (int c = 2; c < ch; c++) outv[ob + c] = 0f; }
        }
        return framesGot;
    }

    public long Seek(long frame)
    {
        if (_reader is null) return -1;
        double sec = _target.SampleRate > 0 ? (double)frame / _target.SampleRate : 0;
        try { _reader.SeekTo(TimeSpan.FromSeconds(sec)); } catch { /* streaming source not seekable yet — best effort */ }
        _resampler?.Reset();
        _eof = false;
        return frame;
    }
}

/// <summary>
/// The ONE real audio host: the app's <see cref="IAudioHost"/> seam over the unified FluentGpu.Media engine. A single
/// <see cref="PcmAudioPlayer"/>/<see cref="WasapiPcm"/> backend (the graph + device) with the app's Vorbis/FLAC/MP3
/// decoder plugged into its decode edge; encrypted fetch+decrypt+fast-start reuse the kept app seams in-proc. Transport,
/// EQ, volume/mute, and the clock are forwarded to/derived from the engine; a per-track engine session is opened (and the
/// prior one disposed) on each load. Crossfade/prepared-next (engine PlayQueue) and per-endpoint device selection are the
/// documented follow-ups — this host delivers correct single-track decode→mix→output with graceful natural-end advance.
/// </summary>
public sealed class FluentMediaAudioHost : IAudioHost, IAudioDspControl, IAudioOutputDeviceControl, IPreparedAudioHost
{
    const int MaxCrossfadeMs = 12_000;

    readonly WaveeLogger _log;
    readonly AudioBodyDiskCache? _bodyDisk;
    readonly System.Net.Http.HttpClient _http;
    readonly Func<string, byte[], CdnDecryptor?> _nativeDecryptorFactory;

    readonly AudioEffects _effects = new();
    readonly MediaPlayerCore _core;
    readonly MediaSignalSink _sink;
    readonly PcmAudioPlayer _backend;

    readonly SimpleSubject<AudioHostSignal> _signals = new();
    readonly object _gate = new();
    Task _tail = Task.CompletedTask;                 // serializes session transitions (Load → Play → SupplyBody order)
    readonly Timer _ticker;

    IMediaSession? _session;
    SpotifyAudioStream? _activeStream;               // the kept fast-start stream (head now, body later); null for external
    string _activeFileIdHex = "";
    long _loadEpoch;

    // intents (applied to the session as it becomes ready)
    bool _playIntent;
    double _volume = 1.0;
    bool _muted;
    string? _outputDeviceId;
    bool _crossfadeEnabled;
    int _crossfadeMs;

    // last-published state (for edge-triggered signal emission off the poll tick)
    PlaybackState _lastState = PlaybackState.Idle;
    bool _errorReported;
    bool _disposed;

    // ── prepared-next / real overlapping crossfade (IPreparedAudioHost) ──────────────────────────────────────────────
    readonly SimpleSubject<AudioTransitionSignal> _transitions = new();
    // the prepared slot (track B) — built/attached ahead of the active track's natural end
    string? _prepToken;
    SpotifyAudioStream? _prepStream;
    IPreparedItem? _prepItem;
    string _prepUri = "";
    long _prepDurMs;
    bool _prepOverlap;
    // the CURRENTLY-PLAYING (active) track's mixer state, so PositionMs reports active-relative time
    long _activeStartMs;          // raw session ms at which the active track's frame-0 played (0 for a fresh load)
    long _activeDurMs;            // the active track's duration (drives the fade-window trigger)
    long _activePrimaryId;        // the mixer voice id currently carrying the active track
    string _activeUri = "";       // the active track uri (for the Completed edge)
    long _nextVoiceId;            // monotonic crossfade voice id source
    bool _crossfadeInFlight;      // set at commit, cleared on the Completed edge — guards a single commit per hand-off
    string? _committedToken;      // the token whose crossfade is committed (CancelPrepared → AlreadyStarted)
    SpotifyAudioStream? _retiringStream;   // track A's stream, disposed on the Completed edge once its voice retires

    public event Action<OutputDeviceNotice>? OutputDeviceNotice;
    public event Action<double, bool>? ExternalVolumeChanged;

    public FluentMediaAudioHost(Func<IPlayPlayCdnDecryptorFactory?> decryptors, System.Net.Http.HttpClient http,
        WaveeLogger log = default, AudioBodyDiskCache? bodyDisk = null)
    {
        _log = log;
        _bodyDisk = bodyDisk;
        _http = http;
        _nativeDecryptorFactory = (_, seed) => decryptors()?.CreateCdnDecryptor(seed);
        _core = new MediaPlayerCore(_effects);
        _sink = new MediaSignalSink(_core);
        WasapiAudioDevice.DiagSink = s => _log.Info("[audiodiag] " + s);   // TEMP: device format + feed/play throughput
        _backend = WasapiPcm.CreateBackend(_effects, decoderFactory: static _ => new SpotifyEngineAudioDecoder());
        _ticker = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // The raw session clock (track A's decode position, in ms). After an overlapping crossfade the active track is a
    // later mixer voice, so PositionMs subtracts the active track's start offset to stay active-track-relative.
    long RawPositionMs => (long)_core.Position.Peek().TotalMilliseconds;
    public long PositionMs => Math.Max(0, RawPositionMs - _activeStartMs);
    public bool IsPlaying => _core.IsPlaying.Peek();
    public bool IsBuffering => _core.IsBuffering.Peek();
    public IObservable<AudioHostSignal> Signals => _signals;
    public IObservable<AudioTransitionSignal> Transitions => _transitions;

    // ── IAudioHost transport ─────────────────────────────────────────────────────────────────────────────────────────

    public void Load(in AudioStreamHandle stream)
    {
        // Non-fast path (ghost resume / tests): no clear head — open once the encrypted body is attached.
        var head = new AudioFastStart(stream.TrackUri, stream.FileIdHex, stream.Format, stream.DurationMs,
            stream.NormalizationGainDb, default);
        LoadFastStart(head);
        SupplyBody(stream);
    }

    public void LoadFastStart(in AudioFastStart start)
    {
        long epoch = Interlocked.Increment(ref _loadEpoch);
        var s = start;   // capture (can't use 'in' inside async)
        Enqueue(() => LoadFastStartAsync(s, epoch));
    }

    public void SupplyBody(in AudioStreamHandle body)
    {
        var b = body;
        long epoch = Volatile.Read(ref _loadEpoch);
        Enqueue(() => SupplyBodyAsync(b, epoch));
    }

    public void Play() { _playIntent = true; _log.Info($"[posdiag] play-intent raw={RawPositionMs} pos={PositionMs} activeStart={_activeStartMs} lastState={_lastState}"); _diagResumeTicks = 12; Enqueue(async () => { if (_session is not null) await _session.PlayAsync().ConfigureAwait(false); StartTicker(); }); }
    // Stop the poll tick once paused: position is frozen and no crossfade commit / Ended / Error can occur while paused
    // (all Playing-only), and the paused UI state is driven by the controller's optimistic EmitState — not this tick — so
    // quiescing the 200ms wakeups here is free idle CPU. StartTicker resumes it on the next Play.
    public void Pause() { _playIntent = false; _log.Info($"[posdiag] pause raw={RawPositionMs} pos={PositionMs} activeStart={_activeStartMs} lastState={_lastState}"); Enqueue(async () => { if (_session is not null) await _session.PauseAsync().ConfigureAwait(false); StopTicker(); }); }

    // TEMP DIAGNOSTIC (#3 resume overshoot): log raw/derived position for a few ticks after a resume, then self-disable.
    int _diagResumeTicks;

    public void Stop()
    {
        _playIntent = false;
        long epoch = Interlocked.Increment(ref _loadEpoch);   // invalidate any in-flight open
        Enqueue(async () =>
        {
            StopTicker();
            await DisposeSessionAsync().ConfigureAwait(false);
        });
    }

    public void Seek(long positionMs)
    {
        long ms = Math.Max(0, positionMs);
        Enqueue(async () => { if (_session is not null) await _session.SeekAsync(TimeSpan.FromMilliseconds(ms), SeekMode.Accurate).ConfigureAwait(false); });
    }

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0, 1);
        _core.Volume.Value = (float)_volume;
        var v = _volume;
        Enqueue(() => { _session?.SetVolume(v); return Task.CompletedTask; });
    }

    // ── IAudioDspControl ─────────────────────────────────────────────────────────────────────────────────────────────

    public void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb, float preampDb = 0f)
    {
        // 10-band graphic EQ (matches the app's persisted band set). A gain-only change ramps in the live graph; enable/
        // disable toggles the topology. Frequencies mirror the classic 10-band layout.
        var eq = _effects.Equalizer;
        if (eq.Bands.Length != 10)
        {
            var freqs = new[] { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
            eq.Apply(new EqPreset(freqs, new float[10]));
        }
        for (int i = 0; i < eq.Bands.Length && i < gainsDb.Length; i++)
            eq.Bands[i].GainDb.Value = Math.Clamp(gainsDb[i], -12f, 12f);
        eq.Enabled.Value = enabled;
    }

    public void SetCrossfade(bool enabled, int durationMs)
    {
        _crossfadeMs = Math.Clamp(durationMs, 0, MaxCrossfadeMs);
        _crossfadeEnabled = enabled && _crossfadeMs > 0;
        // Publish to the engine effects surface (consumed once prepared-next/queue crossfade is wired). 0 == gapless.
        _effects.CrossfadeMs.Value = _crossfadeEnabled ? _crossfadeMs : 0f;
    }

    // ── IAudioOutputDeviceControl ────────────────────────────────────────────────────────────────────────────────────

    public void SetOutputDevice(string? deviceId)
    {
        // v1: the engine WASAPI leaf follows the default endpoint (auto device-loss rebuild). Per-endpoint selection is a
        // follow-up (the WasapiAudioDevice leaf must accept a device id). Store the intent so the picker round-trips.
        _outputDeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
    }

    public void SetOutputMuted(bool muted)
    {
        _muted = muted;
        Enqueue(() => { _session?.SetMuted(muted); return Task.CompletedTask; });
    }

    // ── the serialized session pump ──────────────────────────────────────────────────────────────────────────────────

    void Enqueue(Func<Task> op)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(async _ =>
            {
                try { await op().ConfigureAwait(false); }
                catch (Exception ex) { _log.Info($"fluent-audio-host op failed: {ex.GetType().Name}: {ex.Message}"); }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    async Task LoadFastStartAsync(AudioFastStart start, long epoch)
    {
        await DisposeSessionAsync().ConfigureAwait(false);
        _errorReported = false;
        _lastState = PlaybackState.Idle;

        if (start.HeadBytes.Length == 0)
        {
            // No clear head → defer session open until SupplyBody attaches the body (Spotify non-fast / external).
            _activeStream = null;
            _activeFileIdHex = start.FileIdHex;
            _pendingFmt = start.Format; _pendingDurMs = start.DurationMs; _pendingGainDb = start.NormalizationGainDb;
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
            return;
        }

        var kind = KindOf(start.Format);
        int skip = DetectSkipOffset(start.HeadBytes.Span, start.Format);
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, start.HeadBytes, start.HeadBytes.Length, start.FileIdHex, _log, _bodyDisk);
        _activeStream = stream;
        _activeFileIdHex = start.FileIdHex;
        var bytes = new SpotifyMediaByteSource(stream, skip, kind, start.DurationMs, DbToLinear(start.NormalizationGainDb));
        await OpenSessionAsync(bytes, epoch).ConfigureAwait(false);
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
    }

    AudioFormat _pendingFmt;
    long _pendingDurMs;
    float _pendingGainDb;

    async Task SupplyBodyAsync(AudioStreamHandle body, long epoch)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) { _log.Info($"supply-body ignored stale epoch file={body.FileIdHex}"); return; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, PositionMs));

        // External plain-HTTP (podcast MP3) — open a plain stream and the session now.
        if (body.SourceKind == AudioSourceKind.ExternalPlain)
        {
            var http = await PlainHttpAudioStream.OpenAsync(_http, body.CdnUrl, _log).ConfigureAwait(false);
            var kind = SniffExternalKind(http.ContentType) ?? WaveeDecoderKind.Mp3;
            var extBytes = new SpotifyMediaByteSource(http, 0, kind, body.DurationMs, 1f);
            _activeStream = null;
            await OpenSessionAsync(extBytes, epoch).ConfigureAwait(false);
            return;
        }

        var decryptor = BuildDecryptor(body);
        var cdnUrls = body.CdnUrls ?? (string.IsNullOrEmpty(body.CdnUrl) ? Array.Empty<string>() : new[] { body.CdnUrl });

        if (_activeStream is { } s)
        {
            // Fast path: attach the encrypted body to the already-open, already-playing head stream.
            await s.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // Deferred (Load / non-fast): build a head-less stream, attach the body, then open the session.
        var kind2 = KindOf(_pendingFmt);
        int skip = SpotifyAesCtr.SpotifyHeaderSize;   // no head to inspect → the standard Spotify container offset
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, ReadOnlyMemory<byte>.Empty, 0, body.FileIdHex, _log, _bodyDisk);
        await stream.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
        _activeStream = stream;
        var bytes = new SpotifyMediaByteSource(stream, skip, kind2, _pendingDurMs, DbToLinear(_pendingGainDb));
        await OpenSessionAsync(bytes, epoch).ConfigureAwait(false);
    }

    CdnDecryptor BuildDecryptor(in AudioStreamHandle body)
    {
        var seed = body.NativeCdnSeed;
        if (seed.Length > 0)
        {
            var native = _nativeDecryptorFactory(body.FileIdHex, seed.ToArray());
            if (native is null) throw new InvalidOperationException("native PlayPlay CDN seed supplied but no native decryptor is available");
            return native;
        }
        // AP-key path: decrypt in-proc through the ICtrCipher (SpotifyAesCtr). A fresh cipher per chunk keeps read-ahead
        // threads race-free (the counter is re-derived from the byte offset anyway).
        var key = body.Key.ToArray();
        return (buffer, streamOffset) =>
        {
            var cipher = new SpotifyCtrCipher(key);
            cipher.SeekCounter(streamOffset);
            cipher.XorInPlace(buffer);
        };
    }

    async Task OpenSessionAsync(SpotifyMediaByteSource bytes, long epoch)
    {
        if (epoch != Volatile.Read(ref _loadEpoch)) return;
        try
        {
            var source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
            var session = await _backend.OpenAsync(source, new MediaOpenOptions { StartPaused = true }, CancellationToken.None).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _loadEpoch)) { await session.DisposeAsync().ConfigureAwait(false); return; }
            session.ConnectSignals(_sink);
            _session = session;
            // Fresh active track: reset the crossfade/offset bookkeeping to this session's primary voice.
            _activeStartMs = 0;
            _activeDurMs = bytes.DurationMs;
            _activeUri = "";
            _crossfadeInFlight = false;
            if (session is PcmAudioSession pcm) _activePrimaryId = pcm.PrimaryVoiceIdValue;
            _core.Volume.Value = (float)_volume;
            session.SetVolume(_volume);
            session.SetMuted(_muted);
            if (_playIntent) { await session.PlayAsync().ConfigureAwait(false); StartTicker(); }
        }
        catch (Exception ex)
        {
            _log.Info($"fluent-audio-host open failed file={_activeFileIdHex}: {ex.GetType().Name}: {ex.Message}");
            _signals.OnNext(AudioHostSignal.Fault(PositionMs, AudioKeyFailureReason.None, ex.Message));
        }
    }

    async Task DisposeSessionAsync()
    {
        var old = _session;
        _session = null;
        var stream = _activeStream;
        _activeStream = null;
        var retiring = _retiringStream;
        _retiringStream = null;
        // A manual load/stop supersedes any prepared next and any in-flight crossfade.
        await DisposePreparedSlotAsync().ConfigureAwait(false);
        _crossfadeInFlight = false;
        _committedToken = null;
        _activeUri = "";
        _activeStartMs = 0;
        _activeDurMs = 0;
        if (old is not null) { try { await old.DisposeAsync().ConfigureAwait(false); } catch { } }
        if (stream is not null) { try { stream.Dispose(); } catch { } }
        if (retiring is not null) { try { retiring.Dispose(); } catch { } }
    }

    // Dispose the prepared (not-yet-committed) slot and clear its fields. The prepared voice has NOT entered the mixer,
    // so disposing the IPreparedItem here is correct; once committed we clear the fields WITHOUT disposing (see Tick).
    async Task DisposePreparedSlotAsync()
    {
        var item = _prepItem;
        var stream = _prepStream;
        _prepItem = null;
        _prepStream = null;
        _prepToken = null;
        _prepUri = "";
        _prepDurMs = 0;
        _prepOverlap = false;
        if (item is not null) { try { await item.DisposeAsync().ConfigureAwait(false); } catch { } }
        if (stream is not null) { try { stream.Dispose(); } catch { } }
    }

    // ── IPreparedAudioHost: prepared-next + real overlapping crossfade ───────────────────────────────────────────────

    public Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default)
    {
        var req = request;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try { await PrepareNextCoreAsync(req, ct).ConfigureAwait(false); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    async Task PrepareNextCoreAsync(AudioPrepareRequest req, CancellationToken ct)
    {
        if (_session is not PcmAudioSession session) return;   // no live session to hand off from — nothing to prepare
        var start = req.Start;
        var kind = KindOf(start.Format);
        int skip = DetectSkipOffset(start.HeadBytes.Span, start.Format);
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, start.HeadBytes, start.HeadBytes.Length, start.FileIdHex, _log, _bodyDisk);
        var bytes = new SpotifyMediaByteSource(stream, skip, kind, start.DurationMs, DbToLinear(start.NormalizationGainDb));
        var source = MediaSource.FromPull(bytes).WithKind(MediaKind.PcmAudio);
        var pctx = PrepareContext.For(session.Format, session.NormalizationMode, session.ReferenceLufsValue);
        var result = await _backend.PrepareAsync(source, pctx, ct).ConfigureAwait(false);

        // Supersede any stale prepared slot (a queue edit re-prepares); keep only the newest.
        if (_prepToken is not null && !ReferenceEquals(_prepStream, stream))
            await DisposePreparedSlotAsync().ConfigureAwait(false);
        _prepToken = req.Token;
        _prepStream = stream;
        _prepItem = result;
        _prepUri = start.TrackUri;
        _prepDurMs = start.DurationMs;
        _prepOverlap = req.AllowOverlap;
    }

    public Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default)
    {
        var b = body;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                if (token == _prepToken && _prepStream is { } s)
                {
                    var cdnUrls = b.CdnUrls ?? (string.IsNullOrEmpty(b.CdnUrl) ? Array.Empty<string>() : new[] { b.CdnUrl });
                    await s.AttachBodyWithNativeDecryptorAsync(BuildDecryptor(b), cdnUrls, null, ct).ConfigureAwait(false);
                }
                tcs.TrySetResult();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AudioPrepareCancelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                AudioPrepareCancelResult result;
                if (token == _prepToken && _prepItem is not null)
                {
                    await DisposePreparedSlotAsync().ConfigureAwait(false);
                    result = AudioPrepareCancelResult.Cancelled;
                }
                else if (token == _committedToken)
                {
                    result = AudioPrepareCancelResult.AlreadyStarted;   // crossfade already committed — too late to cancel
                }
                else result = AudioPrepareCancelResult.NotFound;
                tcs.TrySetResult(result);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // Commit the overlapping crossfade IN the live session (called from Tick, RT-safe): add B's voice fading in and fade
    // the active voice out over the same window, then re-point the active state at B. Runs exactly once per hand-off.
    void CommitCrossfade(PcmAudioSession sess, IPreparedItem item, long rawPos)
    {
        _crossfadeInFlight = true;
        long id = ++_nextVoiceId;
        long start = sess.SampleClock;
        int fadeFrames = _crossfadeMs * sess.Format.SampleRate / 1000;
        var curve = CrossCurve.EqualPower;
        float rg = ReplayGain.ScalarLinear(item.Loudness, sess.NormalizationMode, sess.ReferenceLufsValue);

        sess.AddCrossfadeVoice(item.AudioVoice!, GainEnvelope.Fade(FadeKind.In, start, fadeFrames, curve), start, rg, sess.BuildVoiceChain(), id);
        sess.SetVoiceEnvelope(_activePrimaryId, GainEnvelope.Fade(FadeKind.Out, start, fadeFrames, curve));

        // Hand streams over: A retires (disposed on the Completed edge), B's kept stream becomes the active stream.
        _retiringStream = _activeStream;
        _activeStream = _prepStream;
        _committedToken = _prepToken;
        string token = _prepToken ?? "";
        string uri = _prepUri;

        // Re-point the active bookkeeping at B so PositionMs reads B-relative from here on.
        _activeStartMs = rawPos;
        _activePrimaryId = id;
        _activeDurMs = _prepDurMs;
        _activeUri = uri;

        // Clear the prepared slot WITHOUT disposing the item — its voice is now live in the mixer.
        _prepItem = null;
        _prepStream = null;
        _prepToken = null;
        _prepUri = "";
        _prepDurMs = 0;
        _prepOverlap = false;

        _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Started, token, uri, 0, _crossfadeMs));
    }

    // ── the poll tick: derive AudioHostSignals from the engine's reactive state ──────────────────────────────────────

    void StartTicker() => _ticker.Change(200, 200);
    void StopTicker() => _ticker.Change(Timeout.Infinite, Timeout.Infinite);

    void Tick()
    {
        if (_disposed) return;
        var state = _core.State.Peek();
        long rawPos = RawPositionMs;
        long pos = PositionMs;

        if (_diagResumeTicks > 0)   // TEMP (#3): trace position for a few ticks after resume to locate the overshoot
        {
            _diagResumeTicks--;
            _log.Info($"[posdiag] tick raw={rawPos} pos={pos} activeStart={_activeStartMs} state={state} lastState={_lastState}");
        }

        if (!_errorReported && _core.Error.Peek() is { } err)
        {
            _errorReported = true;
            _signals.OnNext(AudioHostSignal.Fault(pos, AudioKeyFailureReason.None, err.Message));
            return;
        }

        // ── prepared-next overlapping crossfade: commit when the active track enters its fade window ─────────────────
        if (state == PlaybackState.Playing && !_crossfadeInFlight
            && _prepItem is { IsReady: true } item && _prepOverlap && _crossfadeMs > 0 && _activeDurMs > 0
            && _session is PcmAudioSession sess
            && (rawPos - _activeStartMs) >= _activeDurMs - _crossfadeMs)
        {
            CommitCrossfade(sess, item, rawPos);
            pos = PositionMs;   // re-read: now B-relative (≈0 at the hand-off)
        }
        // ── close the hand-off: once the fade has elapsed, retire A's stream and report Completed ─────────────────────
        else if (_crossfadeInFlight && (rawPos - _activeStartMs) >= _crossfadeMs)
        {
            _crossfadeInFlight = false;
            var retiring = _retiringStream;
            _retiringStream = null;
            if (retiring is not null) { try { retiring.Dispose(); } catch { } }
            _transitions.OnNext(new AudioTransitionSignal(AudioTransitionKind.Completed, _committedToken ?? "", _activeUri, PositionMs, _crossfadeMs));
        }

        switch (state)
        {
            case PlaybackState.Playing:
                _signals.OnNext(_lastState == PlaybackState.Playing
                    ? new AudioHostSignal(AudioHostSignalKind.PositionTick, pos)
                    : new AudioHostSignal(AudioHostSignalKind.Playing, pos));
                break;
            case PlaybackState.Paused:
                if (_lastState != PlaybackState.Paused) _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Paused, pos));
                break;
            case PlaybackState.Opening:
            case PlaybackState.Buffering:
            case PlaybackState.Stalled:
                if (_lastState != state) _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, pos));
                break;
            case PlaybackState.Ended:
                if (_lastState != PlaybackState.Ended)
                {
                    StopTicker();
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
                }
                break;
        }
        _lastState = state;
    }

    // ── helpers (codec kind + skip-offset detection extracted from the old DecodePipeline) ───────────────────────────

    static WaveeDecoderKind KindOf(AudioFormat fmt) => fmt switch
    {
        AudioFormat.Flac or AudioFormat.Flac24 => WaveeDecoderKind.Flac,
        AudioFormat.Mp3 => WaveeDecoderKind.Mp3,
        _ => WaveeDecoderKind.Vorbis,
    };

    static WaveeDecoderKind? SniffExternalKind(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        var ct = contentType.ToLowerInvariant();
        if (ct.Contains("mpeg") || ct.Contains("mp3")) return WaveeDecoderKind.Mp3;
        if (ct.Contains("ogg") || ct.Contains("vorbis")) return WaveeDecoderKind.Vorbis;
        if (ct.Contains("flac")) return WaveeDecoderKind.Flac;
        return null;
    }

    static float DbToLinear(float db) => db == 0f ? 1f : (float)Math.Pow(10, db / 20.0);

    static int DetectSkipOffset(ReadOnlySpan<byte> clearHead, AudioFormat format)
    {
        if (format is AudioFormat.Flac or AudioFormat.Flac24)
        {
            ReadOnlySpan<byte> flac = "fLaC"u8;
            if (clearHead.Length >= flac.Length && clearHead[..flac.Length].SequenceEqual(flac)) return 0;
            return SpotifyAesCtr.SpotifyHeaderSize;
        }
        if (HasVorbisHeaderAt(clearHead, 0)) return 0;
        return SpotifyAesCtr.SpotifyHeaderSize;
    }

    static bool HasVorbisHeaderAt(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || bytes.Length < offset + 27) return false;
        var page = bytes[offset..];
        if (!page[..4].SequenceEqual(SpotifyAesCtr.OggMagic)) return false;
        int segments = page[26];
        if (page.Length < 27 + segments) return false;
        var lacing = page.Slice(27, segments);
        int packetLength = 0;
        for (int i = 0; i < lacing.Length; i++) { packetLength += lacing[i]; if (lacing[i] < 255) break; }
        if (packetLength < 7 || page.Length < 27 + segments + 7) return false;
        return page[27 + segments] == 1 && page.Slice(28 + segments, 6).SequenceEqual("vorbis"u8);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _loadEpoch);
        StopTicker();
        try { await _ticker.DisposeAsync().ConfigureAwait(false); } catch { }
        await DisposeSessionAsync().ConfigureAwait(false);
    }
}
