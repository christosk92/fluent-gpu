using System.Net.Http;

namespace Wavee.Backend.Audio;

/// <summary>Seekable read-through stream presenting the decrypted track: <c>[clear head 0..N)</c> ++
/// <c>[AES-CTR-decrypted CDN body N..S)</c>. The CDN file is encrypted from byte 0 (position-keyed CTR); the head file
/// is a separate plaintext artifact, so reads below N come from the head and reads at/above N decrypt the CDN bytes at
/// their stream offset. Minimal body source: whole-file GET with mirror failover, filled progressively so reads block
/// only until their range has arrived. Portable (HttpClient + SpotifyAesCtr only) — linked into Wavee.AudioHost and
/// unit-tested here. (Ranged/`.enc`-cache upgrade is a later step.)</summary>
public sealed class SpotifyAudioStream : Stream
{
    readonly byte[] _head;
    readonly int _headLen;
    readonly byte[] _key;
    byte[] _cdn;
    long _size;
    long _downloaded;
    long _pos;
    volatile bool _failed;
    volatile bool _complete;
    Exception? _error;

    SpotifyAudioStream(byte[] head, int headLen, byte[] key, byte[] cdn, long size)
    {
        _head = head;
        _headLen = headLen;
        _key = key;
        _cdn = cdn;
        _size = size;
    }

    /// <summary>Open the first responding CDN mirror, learn the size, and start streaming the body in the background.</summary>
    public static async Task<SpotifyAudioStream> CreateAsync(
        HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, byte[] key, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        if (key.Length != 16) throw new ArgumentException("AES key must be 16 bytes");
        if (cdnUrls is null || cdnUrls.Length == 0) throw new InvalidOperationException("no CDN urls");

        HttpResponseMessage? resp = null;
        Exception? last = null;
        foreach (var url in cdnUrls)
        {
            if (string.IsNullOrEmpty(url)) continue;
            try
            {
                var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) { last = new HttpRequestException($"CDN {(int)r.StatusCode}"); r.Dispose(); continue; }
                resp = r;
                break;
            }
            catch (Exception ex) { last = ex; }
        }
        if (resp is null) throw last ?? new InvalidOperationException("all CDN mirrors failed");

        long size = resp.Content.Headers.ContentLength ?? knownSize ?? 0;
        var headArr = head.ToArray();
        int headLen = Math.Max(0, Math.Min(headBoundary, headArr.Length));
        var cdn = new byte[size > 0 ? size : 8 * 1024 * 1024];
        var stream = new SpotifyAudioStream(headArr, headLen, key, cdn, size);
        _ = Task.Run(() => stream.DownloadLoopAsync(resp), CancellationToken.None);
        return stream;
    }

    async Task DownloadLoopAsync(HttpResponseMessage resp)
    {
        try
        {
            using (resp)
            await using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var tmp = new byte[64 * 1024];
                int off = 0, n;
                while ((n = await body.ReadAsync(tmp).ConfigureAwait(false)) > 0)
                {
                    if (off + n > _cdn.Length) Array.Resize(ref _cdn, Math.Max(_cdn.Length * 2, off + n));
                    Array.Copy(tmp, 0, _cdn, off, n);
                    off += n;
                    Interlocked.Exchange(ref _downloaded, off);
                }
                _size = off;
                Interlocked.Exchange(ref _downloaded, off);
                _complete = true;
            }
        }
        catch (Exception ex) { _error = ex; _failed = true; }
    }

    void WaitFor(long neededExclusiveEnd)
    {
        while (Interlocked.Read(ref _downloaded) < neededExclusiveEnd && !_complete && !_failed)
            Thread.Sleep(3);
        if (_failed) throw _error ?? new IOException("CDN download failed");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long len = Length;
        if (_pos >= len) return 0;
        int produced = 0;
        while (produced < count && _pos < len)
        {
            if (_pos < _headLen)
            {
                int n = (int)Math.Min(count - produced, _headLen - _pos);
                Array.Copy(_head, (int)_pos, buffer, offset + produced, n);
                _pos += n; produced += n;
            }
            else
            {
                long want = Math.Min(len, _pos + (count - produced));
                WaitFor(want);
                long avail = Math.Min(Interlocked.Read(ref _downloaded), len) - _pos;
                if (avail <= 0) break;
                int n = (int)Math.Min(count - produced, avail);
                Array.Copy(_cdn, (int)_pos, buffer, offset + produced, n);
                SpotifyAesCtr.DecryptInPlace(buffer.AsSpan(offset + produced, n), _key, _pos);
                _pos += n; produced += n;
            }
        }
        return produced;
    }

    public override long Length => _size > 0 ? _size : Interlocked.Read(ref _downloaded);
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _pos;
        set => _pos = Math.Max(0, value);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            SeekOrigin.End => Length + offset,
            _ => _pos,
        };
        _pos = Math.Max(0, _pos);
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Forward-offset wrapper: presents byte <paramref name="skip"/> of the inner stream as logical position 0
/// (used to drop the 0xa7 Spotify header so NVorbis sees a clean Ogg bitstream at position 0).</summary>
public sealed class SkipStream : Stream
{
    readonly Stream _inner;
    readonly long _skip;

    public SkipStream(Stream inner, long skip)
    {
        _inner = inner;
        _skip = skip;
        _inner.Seek(skip, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Length => Math.Max(0, _inner.Length - _skip);
    public override long Position
    {
        get => _inner.Position - _skip;
        set => _inner.Position = value + _skip;
    }
    public override long Seek(long offset, SeekOrigin origin) => origin switch
    {
        SeekOrigin.Begin => _inner.Seek(offset + _skip, SeekOrigin.Begin) - _skip,
        SeekOrigin.Current => _inner.Seek(offset, SeekOrigin.Current) - _skip,
        SeekOrigin.End => _inner.Seek(offset, SeekOrigin.End) - _skip,
        _ => _inner.Position - _skip,
    };
    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
