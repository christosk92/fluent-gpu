using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.SpotifyLive.Audio.Host.Dsp;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Host-side play engine: fetch → AES-CTR decrypt → decode (Vorbis via vendored NVorbis, FLAC via FlacBox) →
/// WASAPI. Minimal first cut (whole-file body); true instant-start-from-head and progressive/.enc caching are follow-ups.
/// Single decode thread paced by the blocking WASAPI Write; control (Play/Pause/Seek/Volume) is thread-safe through the
/// renderer's lock.</summary>
internal sealed class AudioPlayEngine : IDisposable
{
    const int RendererBufferMs = 800;
    const int StartPrebufferMs = 420;
    const int WriteStallWarnMs = 650;
    const int EndSeekGuardMs = 250;

    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly Action<string> _log;
    readonly Func<string, byte[], CdnDecryptor?> _nativeDecryptorFactory;
    readonly AudioBodyDiskCache? _bodyDisk;
    readonly WasapiRenderer _renderer = new();
    readonly EqualizerProcessor _equalizer = new();
    readonly Limiter _limiter = new();
    readonly object _gate = new();
    readonly Timer _tick;

    ISampleSource? _reader;
    IAudioReadStream? _stream;   // the decode stream (Spotify encrypted OR plain-HTTP external); disposed by StopDecode
    Thread? _decodeThread;
    CancellationTokenSource? _cts;

    ReadOnlyMemory<byte> _pendingHead;
    string _fileIdHex = "";
    string _format = "OggVorbis320";   // AudioFormat name string (over IPC); remembered from LoadFastStart (SupplyBody carries none)
    float _gainLinear = 1f;
    long _durationMs;                  // expected track length — lets the natural-EOF log flag a head-only early finish
    long _loadStartTicks;
    long _seekBaseMs;
    long _pendingSeekMs = -1;
    volatile bool _playing;
    volatile bool _prebuffering;
    volatile bool _buffering;
    volatile bool _rendererPrimed;

    public event Action<AudioHostSignal>? State;
    public event Action? TrackFinished;

    public AudioPlayEngine(Action<string> log, Func<string, byte[], CdnDecryptor?>? nativeDecryptorFactory = null,
        AudioBodyDiskCache? bodyDisk = null, HttpClient? http = null, bool ownsHttp = false)
    {
        _http = http ?? HttpPools.Get(HttpPool.Cdn);
        _ownsHttp = ownsHttp;
        _log = log;
        _nativeDecryptorFactory = nativeDecryptorFactory ?? ((_, _) => null);
        _bodyDisk = bodyDisk;
        _tick = new Timer(_ => { if (_playing) RaiseState(); }, null, 1000, 1000);
    }

    public void LoadFastStart(in AudioFastStart cmd)
    {
        StopDecode();
        _pendingHead = cmd.HeadBytes;
        _fileIdHex = cmd.FileIdHex;
        _format = cmd.Format.ToString();
        _gainLinear = DbToLinear(cmd.NormalizationGainDb);
        Volatile.Write(ref _durationMs, cmd.DurationMs);   // read by Seek() from other threads (the clamp)
        _loadStartTicks = Stopwatch.GetTimestamp();
        _rendererPrimed = false;
        _prebuffering = _pendingHead.Length > 0;
        _buffering = _pendingHead.Length == 0;
        _log($"load-fast-start {cmd.FileIdHex}: head={_pendingHead.Length}B fmt={_format} dur={_durationMs}ms gain={cmd.NormalizationGainDb:0.0}dB");
        _seekBaseMs = 0;
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, _pendingHead, _pendingHead.Length, cmd.FileIdHex, _log, _bodyDisk);
        if (_pendingHead.Length > 0)
            StartDecode(stream, cmd.FileIdHex, _pendingHead, _format);
        else
        {
            _log($"load-fast-start {cmd.FileIdHex}: no clear head available; waiting for CDN body before decoder construction");
            lock (_gate)
            {
                _stream = stream;
                _reader = null;
                _seekBaseMs = 0;
            }
        }
        RaiseState();
    }

    public void SupplyBody(in AudioStreamHandle cmd) => _ = StartBodyAsync(cmd);

    async Task StartBodyAsync(AudioStreamHandle cmd)
    {
        SpotifyAudioStream? stream = null;
        var cdnUrls = Array.Empty<string>();
        var keyBytes = 0;
        var nativeSeedBytes = 0;
        var startDecode = false;
        try
        {
            lock (_gate)
            {
                if (_fileIdHex.Length != 0 && !string.Equals(_fileIdHex, cmd.FileIdHex, StringComparison.OrdinalIgnoreCase))
                {
                    _log($"supply-body {cmd.FileIdHex}: ignored stale body for active file {_fileIdHex}");
                    return;
                }
            }

            if (IsExternalMp3(cmd))
            {
                await StartExternalMp3Async(cmd).ConfigureAwait(false);
                return;
            }

            var key = cmd.Key.ToArray();
            var nativeSeed = cmd.NativeCdnSeed.ToArray();
            keyBytes = key.Length;
            nativeSeedBytes = nativeSeed.Length;
            var nativeDecryptor = nativeSeed.Length == 0 ? null : _nativeDecryptorFactory(cmd.FileIdHex, nativeSeed);
            if (nativeSeed.Length > 0 && nativeDecryptor is null)
            {
                _log($"supply-body {cmd.FileIdHex}: native seed present ({nativeSeed.Length}B) but no native CDN decryptor is available");
                throw new InvalidOperationException("native PlayPlay CDN seed was supplied, but no matching native decryptor is available");
            }
            cdnUrls = cmd.CdnUrls ?? Array.Empty<string>();
            bool headDecodeActive;
            lock (_gate) headDecodeActive = _pendingHead.Length > 0 && _decodeThread is { IsAlive: true };
            if (headDecodeActive)
                _log($"supply-body {cmd.FileIdHex}: attaching body while head decode is active; keeping head playback state");
            else
            {
                _buffering = true; _prebuffering = false; RaiseState();
            }
            _log($"supply-body {cmd.FileIdHex}: mirrors={cdnUrls.Length} headBoundary={cmd.HeadBoundary}B key={key.Length}B nativeSeed={nativeSeed.Length}B");

            lock (_gate)
            {
                if (_fileIdHex.Length != 0 && !string.Equals(_fileIdHex, cmd.FileIdHex, StringComparison.OrdinalIgnoreCase))
                {
                    _log($"supply-body {cmd.FileIdHex}: ignored stale body for active file {_fileIdHex}");
                    return;
                }

                stream = _stream as SpotifyAudioStream;   // Spotify-only path (external returned above); recover the head-only stream
                if (stream is null)
                {
                    stream = SpotifyAudioStream.CreateHeadOnly(_http, _pendingHead, cmd.HeadBoundary, cmd.FileIdHex, _log, _bodyDisk);
                }
                startDecode = _decodeThread is null || !_decodeThread.IsAlive;
            }
            if (startDecode)
                _log($"supply-body {cmd.FileIdHex}: decoder is waiting for body; will start after attach+key-check");

            var lazyAttach = !startDecode && _pendingHead.Length > 0;
            if (nativeDecryptor is null)
            {
                if (lazyAttach)
                    await stream.AttachBodyLazyAsync(key, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
                else
                    await stream.AttachBodyAsync(key, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                if (lazyAttach)
                    await stream.AttachBodyWithNativeDecryptorLazyAsync(nativeDecryptor, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
                else
                    await stream.AttachBodyWithNativeDecryptorAsync(nativeDecryptor, cdnUrls, null, CancellationToken.None).ConfigureAwait(false);
            }

            var knownSize = stream.KnownSize;
            _log($"body stream attached: cdn size={(knownSize > 0 ? knownSize + "B" : "pending")} (clear head={cmd.HeadBoundary}B; ranged CDN; mode={(lazyAttach ? "lazy" : "eager")})");

            if (lazyAttach)
                StartLazyKeyCheck(stream, cmd.FileIdHex, keyBytes, nativeSeedBytes);
            else
                ValidateKeyOrThrow(stream, cmd.FileIdHex, keyBytes, nativeSeedBytes);

            if (startDecode)
            {
                _log($"supply-body {cmd.FileIdHex}: starting decoder after body attach");
                StartDecode(stream, cmd.FileIdHex, _pendingHead, _format);
            }
        }
        catch (Exception ex)
        {
            if (stream is null)
            {
                lock (_gate)
                {
                    if (_fileIdHex.Length == 0 || string.Equals(_fileIdHex, cmd.FileIdHex, StringComparison.OrdinalIgnoreCase))
                        stream = _stream as SpotifyAudioStream;
                }
            }
            try { stream?.AbortBody(ex); } catch { }
            string active;
            lock (_gate) active = _fileIdHex;
            _log($"supply body failed file={cmd.FileIdHex} active={active} head={_pendingHead.Length}B mirrors={cdnUrls.Length} key={keyBytes}B nativeSeed={nativeSeedBytes}B startDecode={startDecode}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static bool IsExternalMp3(in AudioStreamHandle cmd) =>
        cmd.SourceKind == AudioSourceKind.ExternalPlain && cmd.Format == AudioFormat.Mp3
        && cmd.CdnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    async Task StartExternalMp3Async(AudioStreamHandle cmd)
    {
        StopDecode();
        _fileIdHex = cmd.FileIdHex;
        _format = "Mp3";
        _gainLinear = 1f;
        Volatile.Write(ref _durationMs, cmd.DurationMs);
        _loadStartTicks = Stopwatch.GetTimestamp();
        _rendererPrimed = false;
        _prebuffering = false;
        _buffering = true;
        _seekBaseMs = 0;
        _log($"supply-body external MP3 {cmd.CdnUrl} dur={cmd.DurationMs}ms");
        RaiseState();
        var httpStream = await ExternalMp3Stream.OpenAsync(_http, cmd.CdnUrl, _log).ConfigureAwait(false);
        var kind = PickExternalDecoderKind(httpStream.ContentType) ?? SniffMagicKind(httpStream);
        if (kind is null)
        {
            _log($"external audio {cmd.CdnUrl}: unsupported codec (content-type={httpStream.ContentType ?? "?"}) — no vendored decoder (Vorbis/MP3/FLAC only)");
            try { httpStream.Dispose(); } catch { }
            _buffering = false; RaiseState();
            return;
        }
        StartDecodeThread(httpStream, cmd.FileIdHex, kind.Value, skipOffset: 0);   // unified decode path (no head, no decrypt)
    }

    static DecoderKind? PickExternalDecoderKind(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        var ct = contentType.ToLowerInvariant();
        if (ct.Contains("mpeg") || ct.Contains("mp3")) return DecoderKind.Mp3;
        if (ct.Contains("ogg") || ct.Contains("vorbis")) return DecoderKind.Vorbis;
        if (ct.Contains("flac")) return DecoderKind.Flac;
        return null;   // aac / mp4 / x-m4a / unknown → try magic bytes, else fail cleanly (no AAC decoder vendored)
    }

    DecoderKind? SniffMagicKind(PlainHttpAudioStream stream)
    {
        var probe = new byte[16];
        int n;
        try { n = stream.Read(probe, 0, probe.Length); }
        catch (Exception ex) { _log($"external audio magic sniff read failed: {ex.Message}"); return null; }
        try { stream.Seek(0, SeekOrigin.Begin); } catch { }
        if (n < 4) return null;
        if (probe[0] == (byte)'I' && probe[1] == (byte)'D' && probe[2] == (byte)'3') return DecoderKind.Mp3;   // ID3v2
        if (probe[0] == 0xFF && (probe[1] & 0xE0) == 0xE0) return DecoderKind.Mp3;                              // MPEG frame sync
        if (probe[0] == (byte)'O' && probe[1] == (byte)'g' && probe[2] == (byte)'g' && probe[3] == (byte)'S') return DecoderKind.Vorbis;
        if (probe[0] == (byte)'f' && probe[1] == (byte)'L' && probe[2] == (byte)'a' && probe[3] == (byte)'C') return DecoderKind.Flac;
        return null;
    }

    void StartLazyKeyCheck(SpotifyAudioStream stream, string fileIdHex, int keyBytes, int nativeSeedBytes)
    {
        _ = Task.Run(() =>
        {
            try
            {
                ValidateKeyOrThrow(stream, fileIdHex, keyBytes, nativeSeedBytes);
            }
            catch (Exception ex)
            {
                try { stream.AbortBody(ex); } catch { }
                _log($"lazy key check failed {fileIdHex}: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    void ValidateKeyOrThrow(SpotifyAudioStream stream, string fileIdHex, int keyBytes, int nativeSeedBytes)
    {
        // Prove the key decrypts the body before the decoder reaches CDN bytes. The clear head is not used as the
        // oracle here; match WaveeMusic by validating decrypted container magic at byte 0 or after the 0xa7 header.
        if (stream.TryValidateKey(_format, out var keyDetail))
        {
            _log($"key check {fileIdHex}: OK - {keyDetail}");
            return;
        }

        _log($"KEY CHECK FAILED {fileIdHex}: format={_format} head={_pendingHead.Length}B key={keyBytes}B nativeSeed={nativeSeedBytes}B detail={keyDetail}");
        if (stream is SpotifyAudioStream sas) sas.InvalidateBodyCache();
        throw new InvalidOperationException("audio key validation failed: " + keyDetail);
    }

    public void Play()
    {
        _playing = true;
        if (_rendererPrimed) _renderer.Start();
        else _log($"play intent accepted for {_fileIdHex}: waiting for first PCM buffer before starting renderer");
        RaiseState();
    }
    public void Pause() { _playing = false; _renderer.Pause(); RaiseState(); }
    public void Stop() { _playing = false; StopDecode(); _seekBaseMs = 0; RaiseState(); }
    public void Seek(long ms)
    {
        // Clamp into the track. A target at/past the end (a stale-duration UI, a race with a track change) would make
        // the decoder bisect to EOF and fail the seek; landing just shy of the end still finishes the track naturally.
        var dur = Volatile.Read(ref _durationMs);
        if (dur > 0 && ms > dur - EndSeekGuardMs) ms = dur - EndSeekGuardMs;
        Interlocked.Exchange(ref _pendingSeekMs, Math.Max(0, ms));
    }
    public void SetVolume(double v) => _renderer.SetVolume((float)v);
    public void SetEqualizer(EqualizerSettings settings) => _equalizer.Configure(settings);

    // Spotify entry: compute the 0xa7-header skip + codec from the clear head, then start the shared decode thread.
    void StartDecode(SpotifyAudioStream stream, string fileIdHex, ReadOnlyMemory<byte> head, string format)
    {
        int skipOffset = DetectSkipOffset(head.Span, format);   // 0 or the 167-byte Spotify header
        var kind = format is "Flac" or "Flac24" ? DecoderKind.Flac : DecoderKind.Vorbis;
        StartDecodeThread(stream, fileIdHex, kind, skipOffset);
    }

    enum DecoderKind { Vorbis, Flac, Mp3 }

    static ISampleSource CreateDecoder(Stream skip, DecoderKind kind) => kind switch
    {
        DecoderKind.Flac => new FlacSampleSource(skip),
        DecoderKind.Mp3 => new Mp3SampleSource(skip),
        _ => new VorbisSampleSource(skip),
    };

    // The single decode entry for BOTH sources — Spotify encrypted CDN and plain-HTTP external.
    void StartDecodeThread(IAudioReadStream stream, string fileIdHex, DecoderKind kind, int skipOffset)
    {
        var cts = new CancellationTokenSource();
        var thread = new Thread(() => DecodeThreadMain(stream, fileIdHex, kind, skipOffset, cts))
        {
            IsBackground = true,
            Name = "wavee-decode",
            Priority = ThreadPriority.AboveNormal,
        };

        lock (_gate)
        {
            _stream = stream;
            _reader = null;
            _seekBaseMs = 0;
            _cts = cts;
            _decodeThread = thread;
        }

        thread.Start();
    }

    void DecodeThreadMain(IAudioReadStream stream, string fileIdHex, DecoderKind kind, int skipOffset, CancellationTokenSource cts)
    {
        ISampleSource? reader = null;
        using var audioThread = AudioThreadPriority.TryEnter(_log, fileIdHex);
        try
        {
            // Spotify prepends a 0xa7 (167-byte) header before the OggS/fLaC bitstream on some files (skipOffset strips it);
            // the external MP3 path passes skipOffset 0, so the SkipStream is a harmless passthrough there.
            var skip = new SkipStream(stream.AsStream(), skipOffset);
            reader = CreateDecoder(skip, kind);
            _log($"decode: {kind} -> {reader.SampleRate}Hz {reader.Channels}ch skip={skipOffset}B");
            _renderer.Init(reader.SampleRate, reader.Channels, RendererBufferMs);
            _log($"decode {fileIdHex}: renderer initialized buffer={RendererBufferMs}ms startPrebuffer={StartPrebufferMs}ms");

            lock (_gate)
            {
                if (!ReferenceEquals(_stream, stream) || !ReferenceEquals(_cts, cts))
                {
                    _log($"decode setup superseded {fileIdHex}: active stream changed before renderer init completed");
                    try { reader.Dispose(); } catch { }
                    return;
                }

                _reader = reader;
            }

            if (_playing)
                _log($"decode {fileIdHex}: renderer start deferred until {StartPrebufferMs}ms PCM is queued");
            DecodeLoop(reader, stream, fileIdHex, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { if (!cts.IsCancellationRequested) _log($"decode setup interrupted {fileIdHex}: stream disposed"); }
        catch (Exception ex)
        {
            try { reader?.Dispose(); } catch { }
            if (!cts.IsCancellationRequested)
                _log($"decode setup failed {fileIdHex}: " + ex.Message);
        }
    }

    void DecodeLoop(ISampleSource reader, IAudioReadStream stream, string fileIdHex, CancellationToken ct)
    {
        var buf = new float[16384];
        long totalSamples = 0;
        bool rendererStartedAfterBuffer = false;
        bool loggedFirstPcm = false;
        bool loggedBodyPcm = false;
        long preStartFrames = 0;
        long lastWriteTicks = 0;
        int lastGen0 = GC.CollectionCount(0);
        int lastGen1 = GC.CollectionCount(1);
        int lastGen2 = GC.CollectionCount(2);
        bool loggedSeekWaitingForBody = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long seek = Interlocked.Exchange(ref _pendingSeekMs, -1);
                if (seek >= 0)
                {
                    if (!stream.IsBodyAttached || stream.KnownSize <= 0)
                    {
                        Interlocked.CompareExchange(ref _pendingSeekMs, seek, -1);
                        if (!loggedSeekWaitingForBody)
                        {
                            _log($"seek deferred target={seek}ms: waiting for CDN body length bodyAttached={stream.IsBodyAttached} knownSize={stream.KnownSize}B head={stream.ClearHeadLength}B");
                            loggedSeekWaitingForBody = true;
                        }
                    }
                    else
                    {
                        loggedSeekWaitingForBody = false;
                        bool seekOk;
                        try
                        {
                            using (stream.PauseReadAhead())
                            {
                                reader.SeekTo(TimeSpan.FromMilliseconds(seek));
                            }
                            stream.ResumeReadAheadAtCurrentOffset();
                            seekOk = true;
                        }
                        catch (Exception ex) { seekOk = false; _log($"seek failed target={seek}ms: {ex.GetType().Name}: {ex.Message}"); }
                        if (seekOk)
                        {
                            _renderer.Reset();
                            _rendererPrimed = false;
                            rendererStartedAfterBuffer = false;
                            preStartFrames = 0;
                            lastWriteTicks = 0;
                            _seekBaseMs = seek;
                            _log($"head-check {fileIdHex}: seek applied target={seek}ms; renderer will restart after {StartPrebufferMs}ms PCM is queued");
                        }
                        else
                        {
                            // The decoder is still at its old position — applying _seekBaseMs anyway would desync the
                            // reported position from the audio for the rest of the track. Snap the UI back instead.
                            RaiseState();
                        }
                    }
                }

                long beforeOffset = stream.CurrentOffset;
                bool bodyAttachedBeforeRead = stream.IsBodyAttached;
                int got = reader.ReadSamples(buf, 0, buf.Length);
                if (got <= 0)
                {
                    if (_playing && !rendererStartedAfterBuffer && preStartFrames > 0)
                    {
                        long preStartMs = reader.SampleRate > 0 ? preStartFrames * 1000L / reader.SampleRate : 0;
                        _renderer.Start();
                        rendererStartedAfterBuffer = true;
                        _log($"head-check {fileIdHex}: renderer started with short prebuffer={preStartMs}ms before EOF elapsed={ElapsedSinceLoadMs()}ms");
                    }
                    break;
                }
                long afterOffset = stream.CurrentOffset;
                totalSamples += got;
                if (_gainLinear != 1f) for (int i = 0; i < got; i++) buf[i] *= _gainLinear;
                _equalizer.Process(buf.AsSpan(0, got), reader.SampleRate, reader.Channels);
                _limiter.Process(buf.AsSpan(0, got));
                _prebuffering = false; _buffering = false;
                var writeStartTicks = Stopwatch.GetTimestamp();
                _renderer.Write(buf.AsSpan(0, got), ct);
                var writeEndTicks = Stopwatch.GetTimestamp();
                _rendererPrimed = true;
                var wroteFrames = reader.Channels > 0 ? got / reader.Channels : got;
                if (!rendererStartedAfterBuffer) preStartFrames += wroteFrames;

                if (lastWriteTicks != 0)
                {
                    var writeGapMs = TicksToMs(writeEndTicks - lastWriteTicks);
                    if (writeGapMs >= WriteStallWarnMs)
                    {
                        var gen0 = GC.CollectionCount(0);
                        var gen1 = GC.CollectionCount(1);
                        var gen2 = GC.CollectionCount(2);
                        _log($"audio starvation {fileIdHex}: writeGap={writeGapMs}ms source={DescribeReadSource(stream, beforeOffset, afterOffset)} offset={beforeOffset} queuedFrames={_renderer.ReleasedFrames} gen0+={gen0 - lastGen0} gen1+={gen1 - lastGen1} gen2+={gen2 - lastGen2}");
                        lastGen0 = gen0;
                        lastGen1 = gen1;
                        lastGen2 = gen2;
                    }
                }
                lastWriteTicks = writeEndTicks;

                if (!loggedFirstPcm)
                {
                    loggedFirstPcm = true;
                    long queuedMs = reader.SampleRate > 0 && reader.Channels > 0 ? got / reader.Channels * 1000L / reader.SampleRate : 0;
                    _log($"head-check {fileIdHex}: first PCM queued from={DescribeReadSource(stream, beforeOffset, afterOffset)} samples={got} approx={queuedMs}ms bodyAttachedBeforeRead={bodyAttachedBeforeRead} bodyAttachedNow={stream.IsBodyAttached} elapsed={ElapsedSinceLoadMs()}ms");
                }
                if (!loggedBodyPcm && beforeOffset >= stream.ClearHeadLength)
                {
                    loggedBodyPcm = true;
                    _log($"head-check {fileIdHex}: PCM reads are now using attached body/ranged CDN offset={beforeOffset} elapsed={ElapsedSinceLoadMs()}ms");
                }

                if (_playing && !rendererStartedAfterBuffer)
                {
                    long preStartMs = reader.SampleRate > 0 ? preStartFrames * 1000L / reader.SampleRate : 0;
                    if (preStartMs >= StartPrebufferMs)
                    {
                        _renderer.Start();
                        rendererStartedAfterBuffer = true;
                        _log($"head-check {fileIdHex}: renderer started after queued PCM prebuffer={preStartMs}ms elapsed={ElapsedSinceLoadMs()}ms");
                    }
                }
                RaiseState();
            }

            if (!ct.IsCancellationRequested)   // natural EOF → drain, then report finished
            {
                // Sanity line: a decode that "finishes" at ~the head length (a few %) is NOT a real track end — it's a body
                // failure (no CDN body, wrong key → garbage past the head, or an early-EOF stream) masquerading as complete.
                long ms = reader.SampleRate > 0 && reader.Channels > 0 ? totalSamples / reader.Channels * 1000L / reader.SampleRate : 0;
                long pct = _durationMs > 0 ? ms * 100 / _durationMs : 0;
                _log($"decode ended naturally at ~{ms}ms" + (_durationMs > 0 ? $" of {_durationMs}ms ({pct}%){(pct < 25 ? " ⚠ HEAD-ONLY: body never decoded (check CDN/key above)" : "")}" : ""));
                while (!ct.IsCancellationRequested && _renderer.PlayedFrames < _renderer.ReleasedFrames) Thread.Sleep(20);
                _playing = false; RaiseState();
                TrackFinished?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { if (!ct.IsCancellationRequested) _log("decode loop interrupted: stream disposed"); }
        catch (Exception ex) { _log("decode loop error: " + ex.Message); }
    }

    void StopDecode()
    {
        CancellationTokenSource? cts; Thread? thread; ISampleSource? reader; IAudioReadStream? stream;
        lock (_gate)
        {
            cts = _cts;
            thread = _decodeThread;
            reader = _reader;
            stream = _stream;
            _cts = null;
            _decodeThread = null;
            _reader = null;
            _stream = null;
        }
        try { cts?.Cancel(); } catch { }
        try { stream?.Dispose(); } catch { }
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            try
            {
                if (!thread.Join(120))
                    _log("decode stop timed out after 120ms; continuing with stream disposed");
            }
            catch { }
        }
        try { _renderer.Reset(); } catch { }
        _rendererPrimed = false;
        try { reader?.Dispose(); } catch { }
        cts?.Dispose();
    }

    public long PositionMs => _seekBaseMs + _renderer.PositionMs;

    void RaiseState()
    {
        var kind = _prebuffering ? AudioHostSignalKind.Prebuffering
            : _buffering ? AudioHostSignalKind.Buffering
            : _playing ? AudioHostSignalKind.Playing
            : AudioHostSignalKind.Paused;
        State?.Invoke(new AudioHostSignal(kind, PositionMs));
    }

    static float DbToLinear(float db) => db == 0f ? 1f : (float)Math.Pow(10, db / 20.0);

    long ElapsedSinceLoadMs()
    {
        var start = Interlocked.Read(ref _loadStartTicks);
        return start == 0 ? 0 : (long)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
    }

    static long TicksToMs(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);

    static string DescribeReadSource(IAudioReadStream stream, long beforeOffset, long afterOffset)
    {
        int headLen = stream.ClearHeadLength;
        if (headLen <= 0) return "body/ranged-cdn";
        if (beforeOffset < headLen && afterOffset <= headLen) return "clear-head";
        if (beforeOffset < headLen) return "clear-head+body";
        return "body/ranged-cdn";
    }

    // Where the real bitstream starts inside the decrypted audio: Vorbis ("OggS" + first packet 0x01 "vorbis") / FLAC
    // ("fLaC") at offset 0 or after the 0xa7 Spotify header. Inspected from the clear head; defaults to 167 when there's
    // no head to look at. Mirrors WaveeMusic's VorbisDecoder.TryFindVorbisStartOffset instead of trusting a bare OggS.
    static int DetectSkipOffset(ReadOnlySpan<byte> clearHead, string format)
    {
        if (format is "Flac" or "Flac24")
        {
            ReadOnlySpan<byte> flac = "fLaC"u8;
            if (clearHead.Length >= flac.Length && clearHead[..flac.Length].SequenceEqual(flac)) return 0;
            int flacHdr = SpotifyAesCtr.SpotifyHeaderSize;
            if (clearHead.Length >= flacHdr + flac.Length && clearHead.Slice(flacHdr, flac.Length).SequenceEqual(flac)) return flacHdr;
            return flacHdr;
        }

        if (HasVorbisHeaderAt(clearHead, 0)) return 0;
        int hdr = SpotifyAesCtr.SpotifyHeaderSize;
        if (HasVorbisHeaderAt(clearHead, hdr)) return hdr;
        return hdr;   // no clear head to inspect → assume the standard 167-byte Spotify header
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
        for (int i = 0; i < lacing.Length; i++)
        {
            packetLength += lacing[i];
            if (lacing[i] < 255) break;
        }
        if (packetLength < 7 || page.Length < 27 + segments + 7) return false;
        return page[27 + segments] == 1
            && page.Slice(28 + segments, 6).SequenceEqual("vorbis"u8);
    }

    public void Dispose()
    {
        _tick.Dispose();
        StopDecode();
        _renderer.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}

sealed class AudioThreadPriority : IDisposable
{
    readonly Action<string> _log;
    readonly string _fileIdHex;
    IntPtr _handle;

    AudioThreadPriority(Action<string> log, string fileIdHex, IntPtr handle)
    {
        _log = log;
        _fileIdHex = fileIdHex;
        _handle = handle;
    }

    public static AudioThreadPriority? TryEnter(Action<string> log, string fileIdHex)
    {
        try
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }

            uint taskIndex = 0;
            var handle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            var task = "Pro Audio";
            if (handle == IntPtr.Zero)
            {
                var firstError = Marshal.GetLastWin32Error();
                taskIndex = 0;
                handle = AvSetMmThreadCharacteristics("Audio", ref taskIndex);
                task = "Audio";
                if (handle == IntPtr.Zero)
                {
                    log($"audio thread priority {fileIdHex}: MMCSS registration failed proAudioError={firstError} audioError={Marshal.GetLastWin32Error()} managedPriority={Thread.CurrentThread.Priority}");
                    return null;
                }
            }

            if (!AvSetMmThreadPriority(handle, 1))
                log($"audio thread priority {fileIdHex}: MMCSS task={task} registered but priority raise failed error={Marshal.GetLastWin32Error()} managedPriority={Thread.CurrentThread.Priority}");
            else
                log($"audio thread priority {fileIdHex}: MMCSS task={task} priority=High managedPriority={Thread.CurrentThread.Priority}");

            return new AudioThreadPriority(log, fileIdHex, handle);
        }
        catch (Exception ex)
        {
            log($"audio thread priority {fileIdHex}: setup failed {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle == IntPtr.Zero) return;
        _handle = IntPtr.Zero;
        try
        {
            if (!AvRevertMmThreadCharacteristics(handle))
                _log($"audio thread priority {_fileIdHex}: MMCSS revert failed error={Marshal.GetLastWin32Error()}");
        }
        catch { }
    }

    [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);

    [DllImport("avrt.dll", SetLastError = true)]
    static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
