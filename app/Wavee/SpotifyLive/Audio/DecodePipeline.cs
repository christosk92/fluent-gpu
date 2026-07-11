using System.Diagnostics;
using System.Net.Http;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive.Audio;

/// <summary>One decode source for the single-output engine: byte stream + body-attach + decoder + per-source
/// normalization gain + decoded/EOF/fault state. It owns NO renderer, NO EQ/limiter, NO COM/session — those stay
/// engine-level so a fade mixes two pipelines into one output. <see cref="BuildDecoder"/> blocks on the head read and
/// must run only on the engine's output thread; <see cref="SeekTo"/> is per-source (decoder position), while device
/// reroute stays engine-level (it re-opens the one renderer).</summary>
internal sealed class DecodePipeline : IDisposable
{
    readonly HttpClient _http;
    readonly WaveeLogger _log;
    readonly Func<string, byte[], CdnDecryptor?> _nativeDecryptorFactory;
    readonly AudioBodyDiskCache? _bodyDisk;
    readonly object _gate = new();

    ISampleSource? _reader;
    IAudioReadStream? _stream;
    ReadOnlyMemory<byte> _pendingHead;
    string _fileIdHex = "";
    string _format = "OggVorbis320";
    float _gainLinear = 1f;
    long _durationMs;
    long _loadStartTicks;
    DecoderKind _kind = DecoderKind.Vorbis;
    int _skipOffset;
    bool _external;
    volatile bool _bodySupplied;
    volatile bool _bodyAttached;
    volatile bool _decoderBuilt;
    volatile bool _eof;
    volatile bool _faulted;

    public DecodePipeline(WaveeLogger log, Func<string, byte[], CdnDecryptor?> nativeDecryptorFactory,
        AudioBodyDiskCache? bodyDisk, HttpClient http)
    {
        _log = log;
        _nativeDecryptorFactory = nativeDecryptorFactory;
        _bodyDisk = bodyDisk;
        _http = http;
    }

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public long SeekBaseMs { get; private set; }
    public long DurationMs => Volatile.Read(ref _durationMs);
    public string FileIdHex => _fileIdHex;
    public bool DecoderBuilt => _decoderBuilt;
    public bool Eof => _eof;
    public bool Faulted => _faulted;
    public bool BodySupplied => _bodySupplied;
    /// <summary>Enough is loaded to construct + read the decoder (a clear head is present, the body is attached, or an
    /// external plain-HTTP source is open). The engine gates <see cref="BuildDecoder"/> on this.</summary>
    public bool CanBuildDecoder => !_faulted && _stream is not null && (_decoderBuilt || _pendingHead.Length > 0 || _bodyAttached || _external);
    public long ElapsedSinceLoadMs
    {
        get { var s = Interlocked.Read(ref _loadStartTicks); return s == 0 ? 0 : (long)((Stopwatch.GetTimestamp() - s) * 1000.0 / Stopwatch.Frequency); }
    }

    // ── source read-position accessors (for the engine's starvation / first-PCM logging) ──────────────────────────────
    public long CurrentOffset { get { var s = _stream; return s?.CurrentOffset ?? 0; } }
    public bool IsBodyAttached { get { var s = _stream; return s?.IsBodyAttached ?? false; } }
    public int ClearHeadLength { get { var s = _stream; return s?.ClearHeadLength ?? 0; } }

    /// <summary>Spotify head-path load (no decoder build, no thread) — mirrors the old LoadFastStart minus the renderer.</summary>
    public void Load(in AudioFastStart cmd)
    {
        _pendingHead = cmd.HeadBytes;
        _fileIdHex = cmd.FileIdHex;
        _format = cmd.Format.ToString();
        _gainLinear = DbToLinear(cmd.NormalizationGainDb);
        Volatile.Write(ref _durationMs, cmd.DurationMs);
        SeekBaseMs = 0;
        _external = false;
        _kind = _format is "Flac" or "Flac24" ? DecoderKind.Flac : DecoderKind.Vorbis;
        _skipOffset = DetectSkipOffset(_pendingHead.Span, _format);
        _loadStartTicks = Stopwatch.GetTimestamp();
        var stream = SpotifyAudioStream.CreateHeadOnly(_http, _pendingHead, _pendingHead.Length, cmd.FileIdHex, _log, _bodyDisk);
        lock (_gate) _stream = stream;
        _log.Info($"pipeline load {cmd.FileIdHex}: head={_pendingHead.Length}B fmt={_format} dur={_durationMs}ms gain={cmd.NormalizationGainDb:0.0}dB");
    }

    /// <summary>Attach the encrypted body (or open the external plain-HTTP source). No thread, no renderer, no RaiseState —
    /// the engine's output thread owns those. <paramref name="decoderBuilt"/> tells us whether the engine has already
    /// built the decoder (lazy attach = keep head playback going) or not (eager attach).</summary>
    public async Task SupplyBodyAsync(AudioStreamHandle cmd)
    {
        _bodySupplied = true;
        SpotifyAudioStream? stream = null;
        var cdnUrls = Array.Empty<string>();
        var keyBytes = 0;
        var nativeSeedBytes = 0;
        try
        {
            if (_fileIdHex.Length != 0 && !string.Equals(_fileIdHex, cmd.FileIdHex, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"supply-body {cmd.FileIdHex}: ignored stale body for pipeline file {_fileIdHex}");
                return;
            }

            if (IsExternalMp3(cmd))
            {
                await StartExternalAsync(cmd).ConfigureAwait(false);
                return;
            }

            var key = cmd.Key.ToArray();
            var nativeSeed = cmd.NativeCdnSeed.ToArray();
            keyBytes = key.Length;
            nativeSeedBytes = nativeSeed.Length;
            var nativeDecryptor = nativeSeed.Length == 0 ? null : _nativeDecryptorFactory(cmd.FileIdHex, nativeSeed);
            if (nativeSeed.Length > 0 && nativeDecryptor is null)
            {
                _log.Info($"supply-body {cmd.FileIdHex}: native seed present ({nativeSeed.Length}B) but no native CDN decryptor is available");
                throw new InvalidOperationException("native PlayPlay CDN seed was supplied, but no matching native decryptor is available");
            }
            cdnUrls = cmd.CdnUrls ?? Array.Empty<string>();
            _log.Info($"supply-body {cmd.FileIdHex}: mirrors={cdnUrls.Length} headBoundary={cmd.HeadBoundary}B key={key.Length}B nativeSeed={nativeSeed.Length}B");

            lock (_gate)
            {
                stream = _stream as SpotifyAudioStream;
                if (stream is null)
                {
                    stream = SpotifyAudioStream.CreateHeadOnly(_http, _pendingHead, cmd.HeadBoundary, cmd.FileIdHex, _log, _bodyDisk);
                    _stream = stream;
                }
            }

            // Lazy attach keeps head playback uninterrupted when the decoder is ALREADY reading the clear head. Otherwise
            // (no head yet, or decoder not built) eager-attach so the decoder can be built straight over the full stream.
            var lazyAttach = _decoderBuilt && _pendingHead.Length > 0;
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
            _log.Info($"body stream attached: cdn size={(knownSize > 0 ? knownSize + "B" : "pending")} (clear head={cmd.HeadBoundary}B; ranged CDN; mode={(lazyAttach ? "lazy" : "eager")})");

            if (lazyAttach)
                StartLazyKeyCheck(stream, cmd.FileIdHex, keyBytes, nativeSeedBytes);
            else
                ValidateKeyOrThrow(stream, cmd.FileIdHex, keyBytes, nativeSeedBytes);

            _bodyAttached = true;
        }
        catch (Exception ex)
        {
            if (stream is null) { lock (_gate) stream = _stream as SpotifyAudioStream; }
            try { stream?.AbortBody(ex); } catch { }
            _faulted = true;
            _log.Info($"supply body failed file={cmd.FileIdHex} head={_pendingHead.Length}B mirrors={cdnUrls.Length} key={keyBytes}B nativeSeed={nativeSeedBytes}B: {ex.GetType().Name}: {ex.Message}");
        }
    }

    async Task StartExternalAsync(AudioStreamHandle cmd)
    {
        _fileIdHex = cmd.FileIdHex;
        _format = "Mp3";
        _gainLinear = 1f;
        Volatile.Write(ref _durationMs, cmd.DurationMs);
        _loadStartTicks = Stopwatch.GetTimestamp();
        SeekBaseMs = 0;
        _external = true;
        _skipOffset = 0;
        _log.Info($"supply-body external MP3 host={WaveeLogRedaction.UrlHost(cmd.CdnUrl)} dur={cmd.DurationMs}ms");
        IAudioReadStream? old;
        lock (_gate) { old = _stream; _stream = null; }
        try { old?.Dispose(); } catch { }
        var httpStream = await ExternalMp3Stream.OpenAsync(_http, cmd.CdnUrl, _log).ConfigureAwait(false);
        var kind = PickExternalDecoderKind(httpStream.ContentType) ?? SniffMagicKind(httpStream);
        if (kind is null)
        {
            _log.Warn($"external audio host={WaveeLogRedaction.UrlHost(cmd.CdnUrl)}: unsupported codec (content-type={httpStream.ContentType ?? "?"}) — no vendored decoder (Vorbis/MP3/FLAC only)");
            try { httpStream.Dispose(); } catch { }
            _faulted = true;
            return;
        }
        _kind = kind.Value;
        lock (_gate) _stream = httpStream;
        _bodyAttached = true;
    }

    /// <summary>Build the decoder over the current stream. BLOCKS on the head read (constructs the codec) so it MUST be
    /// called from the engine output thread, never from a control/IPC thread.</summary>
    public void BuildDecoder()
    {
        if (_decoderBuilt) return;
        IAudioReadStream? stream;
        lock (_gate) stream = _stream;
        if (stream is null) throw new InvalidOperationException("BuildDecoder called before a stream was loaded");
        var skip = new SkipStream(stream.AsStream(), _skipOffset);
        var reader = CreateDecoder(skip, _kind);
        SampleRate = reader.SampleRate;
        Channels = reader.Channels;
        lock (_gate) _reader = reader;
        _decoderBuilt = true;
        _log.Info($"decode: {_kind} -> {reader.SampleRate}Hz {reader.Channels}ch skip={_skipOffset}B file={_fileIdHex}");
    }

    /// <summary>Read up to <paramref name="count"/> interleaved float samples, applying this source's normalization gain
    /// before the mix. 0 marks natural end-of-stream (latches <see cref="Eof"/>).</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        var reader = _reader;
        if (reader is null) return 0;
        int got = reader.ReadSamples(buffer, offset, count);
        if (got <= 0) { _eof = true; return got; }
        if (_gainLinear != 1f)
            for (int i = 0; i < got; i++) buffer[offset + i] *= _gainLinear;
        return got;
    }

    /// <summary>Fill exactly <paramref name="count"/> samples (looping the decoder) so a mix block stays frame-aligned
    /// with the other pipeline; the unfilled tail (source ended early) is left untouched for the caller to zero.</summary>
    public int Fill(float[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int got = Read(buffer, offset + total, count - total);
            if (got <= 0) break;
            total += got;
        }
        return total;
    }

    public enum SeekOutcome { Deferred, Applied, Failed }

    /// <summary>Apply a decoder seek (minus the renderer reset — that's the engine's). Deferred while the CDN body length
    /// isn't known yet (the engine re-posts).</summary>
    public SeekOutcome SeekTo(long ms)
    {
        var reader = _reader; var stream = _stream;
        if (reader is null || stream is null) return SeekOutcome.Deferred;
        if (!stream.IsBodyAttached || stream.KnownSize <= 0) return SeekOutcome.Deferred;
        try
        {
            using (stream.PauseReadAhead()) reader.SeekTo(TimeSpan.FromMilliseconds(ms));
            stream.ResumeReadAheadAtCurrentOffset();
            SeekBaseMs = ms;
            _eof = false;
            return SeekOutcome.Applied;
        }
        catch (Exception ex)
        {
            _log.Info($"seek failed target={ms}ms: {ex.GetType().Name}: {ex.Message}");
            return SeekOutcome.Failed;
        }
    }

    public bool SeekReady { get { var s = _stream; return s is { IsBodyAttached: true } && s.KnownSize > 0; } }

    public string DescribeReadSource(long beforeOffset, long afterOffset)
    {
        var stream = _stream;
        int headLen = stream?.ClearHeadLength ?? 0;
        if (headLen <= 0) return "body/ranged-cdn";
        if (beforeOffset < headLen && afterOffset <= headLen) return "clear-head";
        if (beforeOffset < headLen) return "clear-head+body";
        return "body/ranged-cdn";
    }

    void StartLazyKeyCheck(SpotifyAudioStream stream, string fileIdHex, int keyBytes, int nativeSeedBytes)
    {
        _ = Task.Run(() =>
        {
            try { ValidateKeyOrThrow(stream, fileIdHex, keyBytes, nativeSeedBytes); }
            catch (Exception ex)
            {
                try { stream.AbortBody(ex); } catch { }
                _faulted = true;
                _log.Info($"lazy key check failed {fileIdHex}: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    void ValidateKeyOrThrow(SpotifyAudioStream stream, string fileIdHex, int keyBytes, int nativeSeedBytes)
    {
        if (stream.TryValidateKey(_format, out var keyDetail))
        {
            _log.Info($"key check {fileIdHex}: OK - {keyDetail}");
            return;
        }
        _log.Info($"KEY CHECK FAILED {fileIdHex}: format={_format} head={_pendingHead.Length}B key={keyBytes}B nativeSeed={nativeSeedBytes}B detail={keyDetail}");
        stream.InvalidateBodyCache();
        throw new InvalidOperationException("audio key validation failed: " + keyDetail);
    }

    public void Dispose()
    {
        ISampleSource? reader; IAudioReadStream? stream;
        lock (_gate) { reader = _reader; stream = _stream; _reader = null; _stream = null; }
        try { stream?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
    }

    // ── static helpers (moved verbatim from AudioPlayEngine — single-source concerns) ─────────────────────────────────
    enum DecoderKind { Vorbis, Flac, Mp3 }

    static ISampleSource CreateDecoder(Stream skip, DecoderKind kind) => kind switch
    {
        DecoderKind.Flac => new FlacSampleSource(skip),
        DecoderKind.Mp3 => new Mp3SampleSource(skip),
        _ => new VorbisSampleSource(skip),
    };

    static bool IsExternalMp3(in AudioStreamHandle cmd) =>
        cmd.SourceKind == AudioSourceKind.ExternalPlain && cmd.Format == AudioFormat.Mp3
        && cmd.CdnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    static DecoderKind? PickExternalDecoderKind(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        var ct = contentType.ToLowerInvariant();
        if (ct.Contains("mpeg") || ct.Contains("mp3")) return DecoderKind.Mp3;
        if (ct.Contains("ogg") || ct.Contains("vorbis")) return DecoderKind.Vorbis;
        if (ct.Contains("flac")) return DecoderKind.Flac;
        return null;
    }

    DecoderKind? SniffMagicKind(PlainHttpAudioStream stream)
    {
        var probe = new byte[16];
        int n;
        try { n = stream.Read(probe, 0, probe.Length); }
        catch (Exception ex) { _log.Info($"external audio magic sniff read failed: {ex.Message}"); return null; }
        try { stream.Seek(0, SeekOrigin.Begin); } catch { }
        if (n < 4) return null;
        if (probe[0] == (byte)'I' && probe[1] == (byte)'D' && probe[2] == (byte)'3') return DecoderKind.Mp3;
        if (probe[0] == 0xFF && (probe[1] & 0xE0) == 0xE0) return DecoderKind.Mp3;
        if (probe[0] == (byte)'O' && probe[1] == (byte)'g' && probe[2] == (byte)'g' && probe[3] == (byte)'S') return DecoderKind.Vorbis;
        if (probe[0] == (byte)'f' && probe[1] == (byte)'L' && probe[2] == (byte)'a' && probe[3] == (byte)'C') return DecoderKind.Flac;
        return null;
    }

    static float DbToLinear(float db) => db == 0f ? 1f : (float)Math.Pow(10, db / 20.0);

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
        return hdr;
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
}
