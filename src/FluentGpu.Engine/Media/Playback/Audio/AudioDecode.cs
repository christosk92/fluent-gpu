using System;
using System.Buffers.Binary;
using System.IO;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The decode edge (spec §5.5 / §7.1): a decoded source voice is the decorator stack Resample(Decode([Decrypt](Fetch))).
// Fetch = a portable IMediaByteSource (file/stream/bytes, or a DecryptingSource in front for PlayPlay). Decode+Resample =
// WavAudioDecoder (the one real M2 codec; Vorbis/AAC/Opus/FLAC are later leaf codecs behind the same IAudioDecoder seam).
// Everything resamples INTO the fixed mix format so the mixer sums homogeneous PCM and the device opens once.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A portable synchronous <see cref="IMediaByteSource"/> over a local file (spec §5.1 fetch). The engine runs it
/// on a firewalled worker thread so blocking reads are fine.</summary>
public sealed class FileByteSource : IMediaByteSource
{
    private readonly string _path;
    private FileStream? _fs;

    /// <summary>Create a byte source over <paramref name="path"/>.</summary>
    public FileByteSource(string path) => _path = path;

    /// <inheritdoc/>
    public bool TryOpen(in DataSpec spec)
    {
        try
        {
            _fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            if (spec.Position > 0) _fs.Seek(spec.Position, SeekOrigin.Begin);
            return true;
        }
        catch { _fs = null; return false; }
    }

    /// <inheritdoc/>
    public int Read(Span<byte> dst) => _fs?.Read(dst) ?? -1;
    /// <inheritdoc/>
    public long Seek(long offset) => _fs?.Seek(offset, SeekOrigin.Begin) ?? -1;
    /// <inheritdoc/>
    public long? Length => _fs?.Length;
    /// <inheritdoc/>
    public SourceCaps Caps => new() { Seekable = true, KnownLength = true };
    /// <inheritdoc/>
    public void Cancel() { }
    /// <inheritdoc/>
    public void Close() { _fs?.Dispose(); _fs = null; }
}

/// <summary>A portable <see cref="IMediaByteSource"/> over an existing <see cref="Stream"/> (spec §5, <c>FromStream</c>).</summary>
public sealed class StreamByteSource : IMediaByteSource
{
    private readonly Stream _stream;

    /// <summary>Create a byte source over <paramref name="stream"/>.</summary>
    public StreamByteSource(Stream stream) => _stream = stream;

    /// <inheritdoc/>
    public bool TryOpen(in DataSpec spec)
    {
        if (spec.Position > 0 && _stream.CanSeek) _stream.Seek(spec.Position, SeekOrigin.Begin);
        return true;
    }
    /// <inheritdoc/>
    public int Read(Span<byte> dst) => _stream.Read(dst);
    /// <inheritdoc/>
    public long Seek(long offset) => _stream.CanSeek ? _stream.Seek(offset, SeekOrigin.Begin) : -1;
    /// <inheritdoc/>
    public long? Length => _stream.CanSeek ? _stream.Length : null;
    /// <inheritdoc/>
    public SourceCaps Caps => new() { Seekable = _stream.CanSeek, KnownLength = _stream.CanSeek };
    /// <inheritdoc/>
    public void Cancel() { }
    /// <inheritdoc/>
    public void Close() { }
}

/// <summary>A portable <see cref="IMediaByteSource"/> over an in-memory blob (spec §5, <c>FromBytes</c>).</summary>
public sealed class BytesByteSource : IMediaByteSource
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private int _pos;

    /// <summary>Create a byte source over <paramref name="bytes"/>.</summary>
    public BytesByteSource(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

    /// <inheritdoc/>
    public bool TryOpen(in DataSpec spec) { _pos = (int)Math.Clamp(spec.Position, 0, _bytes.Length); return true; }
    /// <inheritdoc/>
    public int Read(Span<byte> dst)
    {
        int n = Math.Min(dst.Length, _bytes.Length - _pos);
        if (n <= 0) return 0;
        _bytes.Span.Slice(_pos, n).CopyTo(dst);
        _pos += n;
        return n;
    }
    /// <inheritdoc/>
    public long Seek(long offset) { _pos = (int)Math.Clamp(offset, 0, _bytes.Length); return _pos; }
    /// <inheritdoc/>
    public long? Length => _bytes.Length;
    /// <inheritdoc/>
    public SourceCaps Caps => new() { Seekable = true, KnownLength = true };
    /// <inheritdoc/>
    public void Cancel() { }
    /// <inheritdoc/>
    public void Close() { }
}

/// <summary>
/// A real WAV/RIFF decoder (spec §5.5 <see cref="IAudioDecoder"/>) — the one shipping M2 codec. Parses the <c>fmt </c>
/// chunk (PCM16 / PCM24 / IEEE-float32, mono or stereo), conforms channels to the target layout, and RESAMPLES into the
/// fixed mix format at the decode edge (spec §7.1). Lossless ⇒ <see cref="GaplessInfo.None"/>. Vorbis/AAC/Opus/FLAC/MP3
/// plug in behind this same seam later. Per-<see cref="Read"/> alloc-free (fixed scratch); the header parse allocs once.
/// </summary>
public sealed class WavAudioDecoder : IAudioDecoder
{
    private const int MaxSrcFramesPerRead = 4096;

    private IMediaByteSource? _src;
    private MixFormat _target;
    private int _srcRate, _srcChannels, _bitsPerSample;
    private bool _isFloat;
    private int _blockAlign;
    private long _dataStart;          // byte offset of the data chunk
    private long _dataBytes;          // total data-chunk bytes
    private long _srcFramesTotal;     // frames in the source (pre-resample)
    private long _srcFrameCursor;     // frames read from the source
    private bool _eof;

    private LinearResampler? _resampler;
    private byte[] _byteScratch = Array.Empty<byte>();
    private float[] _floatScratch = Array.Empty<float>();   // conformed to target channels, source rate

    /// <inheritdoc/>
    public GaplessInfo Gapless { get; private set; } = GaplessInfo.None;

    /// <inheritdoc/>
    public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
    {
        info = default;
        _src = src;
        _target = target;
        if (!src.TryOpen(new DataSpec { Position = 0, Length = -1 })) return false;
        if (!ParseHeader()) return false;

        _resampler = _srcRate != target.SampleRate ? new LinearResampler(_srcRate, target.SampleRate, target.Channels) : null;
        _byteScratch = new byte[MaxSrcFramesPerRead * _blockAlign];
        _floatScratch = new float[MaxSrcFramesPerRead * target.Channels];

        double durationSec = _srcRate > 0 ? (double)_srcFramesTotal / _srcRate : 0;
        var codec = new MediaContentType(Container.Wav, CodecId.None, CodecId.Pcm);
        info = new DecodedInfo(codec, new MixFormat(_srcRate, _srcChannels), TimeSpan.FromSeconds(durationSec), default);
        Gapless = GaplessInfo.None;
        return true;
    }

    /// <summary>The total frame count in the fixed mix-rate domain (for duration/seek).</summary>
    public long MixFramesTotal => _srcRate > 0 ? (long)((double)_srcFramesTotal * _target.SampleRate / _srcRate) : _srcFramesTotal;

    /// <inheritdoc/>
    public int Read(Span<float> dst)
    {
        if (_src is null || _eof) return 0;
        int ch = _target.Channels;
        int wantFrames = dst.Length / ch;
        if (wantFrames <= 0) return 0;

        // How many source frames to pull to produce ~wantFrames output frames. With no resampler, output frames == input
        // frames, so cap to wantFrames (never over-fill dst). With a resampler, pull a small margin; the resampler bounds
        // its own output to dst's capacity.
        int srcFrames;
        if (_resampler is { IsActive: true })
        {
            double ratio = (double)_srcRate / _target.SampleRate;
            srcFrames = (int)Math.Min(MaxSrcFramesPerRead, Math.Ceiling(wantFrames * ratio) + 2);
        }
        else
        {
            srcFrames = Math.Min(MaxSrcFramesPerRead, wantFrames);
        }
        long remainingSrc = _srcFramesTotal - _srcFrameCursor;
        srcFrames = (int)Math.Min(srcFrames, remainingSrc);
        if (srcFrames <= 0) { _eof = true; return 0; }

        // Fetch + decode + channel-conform into _floatScratch (source rate, target channels).
        int gotSrc = DecodeSourceFrames(srcFrames);
        if (gotSrc <= 0) { _eof = true; return 0; }
        _srcFrameCursor += gotSrc;

        var conformed = _floatScratch.AsSpan(0, gotSrc * ch);
        if (_resampler is { IsActive: true } rs)
        {
            int outFrames = rs.Process(conformed, gotSrc, dst);
            if (outFrames <= 0 && _srcFrameCursor >= _srcFramesTotal) _eof = true;
            return outFrames;
        }

        conformed.CopyTo(dst);
        if (_srcFrameCursor >= _srcFramesTotal) _eof = true;
        return gotSrc;
    }

    /// <inheritdoc/>
    public long Seek(long frame)
    {
        if (_src is null) return -1;
        // frame is in the fixed mix-rate domain; map back to a source frame.
        double ratio = _srcRate > 0 && _target.SampleRate > 0 ? (double)_srcRate / _target.SampleRate : 1.0;
        long srcFrame = Math.Clamp((long)(frame * ratio), 0, _srcFramesTotal);
        long bytePos = _dataStart + srcFrame * _blockAlign;
        _src.Seek(bytePos);
        _srcFrameCursor = srcFrame;
        _eof = false;
        _resampler?.Reset();
        return frame;
    }

    private int DecodeSourceFrames(int srcFrames)
    {
        int ch = _target.Channels;
        int bytesWanted = srcFrames * _blockAlign;
        int got = ReadExact(_byteScratch.AsSpan(0, bytesWanted));
        int framesGot = got / _blockAlign;
        if (framesGot <= 0) return 0;

        var outFloat = _floatScratch.AsSpan(0, framesGot * ch);
        for (int f = 0; f < framesGot; f++)
        {
            int inBase = f * _blockAlign;
            // Decode source channels for this frame.
            float l = SampleAt(inBase, 0);
            float r = _srcChannels >= 2 ? SampleAt(inBase, 1) : l;

            // Conform to the target channel count.
            int ob = f * ch;
            if (ch == 1) { outFloat[ob] = _srcChannels >= 2 ? (l + r) * 0.5f : l; }
            else
            {
                outFloat[ob] = l;
                outFloat[ob + 1] = r;
                for (int c = 2; c < ch; c++) outFloat[ob + c] = 0f;
            }
        }
        return framesGot;
    }

    private float SampleAt(int frameByteBase, int channel)
    {
        int off = frameByteBase + channel * (_bitsPerSample / 8);
        var s = _byteScratch.AsSpan();
        if (_isFloat) return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(s.Slice(off, 4)));
        return _bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(s.Slice(off, 2)) / 32768f,
            24 => Read24(s, off) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(s.Slice(off, 4)) / 2147483648f,
            8 => (s[off] - 128) / 128f,
            _ => 0f
        };
    }

    private static int Read24(ReadOnlySpan<byte> s, int off)
    {
        int v = s[off] | (s[off + 1] << 8) | (s[off + 2] << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);   // sign-extend
        return v;
    }

    private int ReadExact(Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = _src!.Read(dst[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private bool ParseHeader()
    {
        Span<byte> hdr = stackalloc byte[12];
        if (ReadExact(hdr) < 12) return false;
        if (hdr[0] != 'R' || hdr[1] != 'I' || hdr[2] != 'F' || hdr[3] != 'F') return false;
        if (hdr[8] != 'W' || hdr[9] != 'A' || hdr[10] != 'V' || hdr[11] != 'E') return false;

        long pos = 12;
        Span<byte> ck = stackalloc byte[8];
        while (true)
        {
            if (ReadExact(ck) < 8) return false;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(ck[4..]);
            pos += 8;

            if (ck[0] == 'f' && ck[1] == 'm' && ck[2] == 't' && ck[3] == ' ')
            {
                Span<byte> fmt = stackalloc byte[40];
                int take = (int)Math.Min(size, (uint)fmt.Length);
                if (ReadExact(fmt[..take]) < take) return false;
                ushort audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                _srcChannels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                _srcRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                _blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                _bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                _isFloat = audioFormat == 3;
                if (audioFormat == 0xFFFE && take >= 40)   // WAVE_FORMAT_EXTENSIBLE: subformat GUID's first 2 bytes
                    _isFloat = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]) == 3;

                // Skip any remaining fmt bytes beyond what we read, plus a pad byte if the chunk size is odd.
                long rest = size - take;
                if (rest > 0) SkipBytes(rest);
                if ((size & 1) != 0) SkipBytes(1);
                pos += size + (size & 1);
                if (_srcChannels < 1 || _srcRate < 1 || _blockAlign < 1) return false;
            }
            else if (ck[0] == 'd' && ck[1] == 'a' && ck[2] == 't' && ck[3] == 'a')
            {
                _dataStart = pos;
                _dataBytes = size;
                _srcFramesTotal = _blockAlign > 0 ? _dataBytes / _blockAlign : 0;
                return _srcFramesTotal > 0;
            }
            else
            {
                SkipBytes(size + (size & 1));
                pos += size + (size & 1);
            }
        }
    }

    private void SkipBytes(long count)
    {
        Span<byte> junk = stackalloc byte[256];
        while (count > 0)
        {
            int take = (int)Math.Min(count, junk.Length);
            int n = _src!.Read(junk[..take]);
            if (n <= 0) break;
            count -= n;
        }
    }
}

/// <summary>
/// Adapts an <see cref="IAudioDecoder"/> into an <see cref="IAudioSource"/> voice (spec §7.2) — the Decode node of the
/// decorator stack. Carries the decoder's <see cref="GaplessInfo"/> and the resolved <see cref="ReplayGainInfo"/> so the
/// voice bakes ReplayGain pre-mix. Alloc-free reads.
/// </summary>
public sealed class DecoderAudioSource : IAudioSource
{
    private readonly IAudioDecoder _decoder;
    private long _pos;
    private bool _eof;

    /// <summary>Wrap <paramref name="decoder"/>; <paramref name="loudness"/> is the resolved ReplayGain (from tags/metadata).</summary>
    public DecoderAudioSource(IAudioDecoder decoder, ReplayGainInfo loudness = default)
    {
        _decoder = decoder;
        Loudness = loudness;
        Gapless = decoder.Gapless;
    }

    /// <inheritdoc/>
    public long PositionFrames => _pos;
    /// <inheritdoc/>
    public bool Exhausted => _eof;
    /// <inheritdoc/>
    public GaplessInfo Gapless { get; }
    /// <inheritdoc/>
    public ReplayGainInfo Loudness { get; }

    /// <summary>Seek the underlying decoder to a mix-domain frame.</summary>
    public void SeekFrame(long frame) { _decoder.Seek(frame); _pos = frame; _eof = false; }

    /// <inheritdoc/>
    public int Read(Span<float> dst, int channels)
    {
        if (_eof) return 0;
        int got = _decoder.Read(dst);
        if (got <= 0) { _eof = true; return 0; }
        _pos += got;
        return got;
    }
}
