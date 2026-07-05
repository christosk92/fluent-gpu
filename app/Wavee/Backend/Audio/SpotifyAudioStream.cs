using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wavee.Backend.Audio;

public delegate void CdnDecryptor(Span<byte> buffer, long streamOffset);

/// <summary>
/// Seekable read-through stream for Spotify audio. It can start as a clear-head-only stream and later attach the
/// encrypted CDN body. CDN bytes are fetched only with HTTP Range requests; reads below the head boundary come from the
/// clear head, reads at/after the boundary fetch encrypted ranges and decrypt at the absolute file offset.
/// </summary>
public sealed class SpotifyAudioStream : Stream, IAsyncDisposable
{
    const int MinFetchBytes = 64 * 1024;
    const int MaxReadAheadBytes = 256 * 1024;
    const int ProbeBytes = 0xc0;
    const int CdnChunkBytes = 64 * 1024;

    readonly HttpClient _http;
    readonly string _name;
    readonly Action<string>? _log;
    readonly byte[] _head;
    readonly int _headLen;
    readonly RangeSet _ranges = new();
    readonly SemaphoreSlim _fetchGate = new(2, 2);
    readonly CancellationTokenSource _disposeCts = new();
    readonly object _stateGate = new();
    readonly object _dataGate = new();
    readonly Dictionary<int, byte[]> _cdnChunks = new();

    byte[] _key = [];
    CdnDecryptor? _decryptor;
    string[] _cdnUrls = [];
    long _size;
    long _pos;
    bool _bodyAttached;
    bool _failed;
    bool _disposed;
    Exception? _error;
    Task? _readAheadTask;

    SpotifyAudioStream(HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, string name = "", Action<string>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _name = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        _log = log;
        _head = MemoryMarshal.TryGetArray(head, out var segment)
            && segment.Offset == 0
            && segment.Count == segment.Array!.Length
            ? segment.Array
            : head.ToArray();
        _headLen = Math.Max(0, Math.Min(headBoundary, _head.Length));
    }

    /// <summary>Create a stream that can serve clear head bytes immediately. Call <see cref="AttachBodyAsync"/> later.</summary>
    public static SpotifyAudioStream CreateHeadOnly(HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, string name = "", Action<string>? log = null) =>
        new(http, head, headBoundary, name, log);

    /// <summary>Create and attach the ranged CDN body before returning. Kept for the non-fast/full-load path and tests.</summary>
    public static async Task<SpotifyAudioStream> CreateAsync(
        HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, byte[] key, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        var stream = CreateHeadOnly(http, head, headBoundary);
        await stream.AttachBodyAsync(key, cdnUrls, knownSize, ct).ConfigureAwait(false);
        return stream;
    }

    public static async Task<SpotifyAudioStream> CreateWithNativeDecryptorAsync(
        HttpClient http, ReadOnlyMemory<byte> head, int headBoundary, CdnDecryptor decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        var stream = CreateHeadOnly(http, head, headBoundary);
        await stream.AttachBodyWithNativeDecryptorAsync(decryptor, cdnUrls, knownSize, ct).ConfigureAwait(false);
        return stream;
    }

    public Task AttachBodyAsync(byte[] key, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        if (key.Length != 16) throw new ArgumentException("AES key must be 16 bytes", nameof(key));
        return AttachBodyCoreAsync(key.ToArray(), null, cdnUrls, knownSize, ct, eagerFetch: true);
    }

    public Task AttachBodyLazyAsync(byte[] key, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        if (key.Length != 16) throw new ArgumentException("AES key must be 16 bytes", nameof(key));
        return AttachBodyCoreAsync(key.ToArray(), null, cdnUrls, knownSize, ct, eagerFetch: false);
    }

    public Task AttachBodyWithNativeDecryptorAsync(CdnDecryptor decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decryptor);
        return AttachBodyCoreAsync([], decryptor, cdnUrls, knownSize, ct, eagerFetch: true);
    }

    public Task AttachBodyWithNativeDecryptorLazyAsync(CdnDecryptor decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decryptor);
        return AttachBodyCoreAsync([], decryptor, cdnUrls, knownSize, ct, eagerFetch: false);
    }

    async Task AttachBodyCoreAsync(byte[] key, CdnDecryptor? decryptor, string[] cdnUrls, long? knownSize, CancellationToken ct, bool eagerFetch)
    {
        if (cdnUrls is null || cdnUrls.Length == 0) throw new InvalidOperationException("no CDN urls");

        lock (_stateGate)
        {
            ThrowIfDisposedLocked();
            if (_bodyAttached) return;
            _key = key;
            _decryptor = decryptor;
            _cdnUrls = cdnUrls.Where(static u => !string.IsNullOrWhiteSpace(u)).ToArray();
            if (_cdnUrls.Length == 0) throw new InvalidOperationException("no CDN urls");
            if (knownSize is > 0) SetSizeLocked(knownSize.Value);
            _bodyAttached = true;
            Monitor.PulseAll(_stateGate);
        }
        _log?.Invoke($"stream {_name}: body attach accepted headBoundary={_headLen}B knownSize={(knownSize ?? 0)} mirrors={_cdnUrls.Length} mode={(eagerFetch ? "eager" : "lazy")}");

        try
        {
            StartReadAhead();
            if (!eagerFetch)
            {
                _log?.Invoke($"stream {_name}: lazy body attached; decoder can continue on clear head while read-ahead runs");
                return;
            }

            var size = Volatile.Read(ref _size);
            var initialEnd = Math.Min(size > 0 ? size : MinFetchBytes, MinFetchBytes);
            await FetchRangeAsync(0, initialEnd, ct).ConfigureAwait(false);
            await PrefetchHeadBoundaryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    void StartReadAhead()
    {
        if (!_bodyAttached) return;
        if (_readAheadTask is { IsCompleted: false }) return;
        _readAheadTask = Task.Run(ReadAheadLoopAsync, CancellationToken.None);
    }

    async Task PrefetchHeadBoundaryAsync(CancellationToken ct)
    {
        var size = Volatile.Read(ref _size);
        if (_headLen <= 0 || (size > 0 && _headLen >= size)) return;

        var start = _headLen;
        var end = size > 0
            ? Math.Min(size, start + MaxReadAheadBytes)
            : start + MaxReadAheadBytes;
        if (_ranges.ContainsRange(start, end)) return;

        var sw = Stopwatch.StartNew();
        _log?.Invoke($"stream {_name}: prefetch boundary start range=[{start},{end})");
        await FetchRangeAsync(start, end, ct).ConfigureAwait(false);
        _log?.Invoke($"stream {_name}: prefetch boundary ok bytes={end - start} elapsed={sw.ElapsedMilliseconds}ms");
    }

    async Task ReadAheadLoopAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            try
            {
                if (!_bodyAttached || _failed) break;
                var size = Volatile.Read(ref _size);

                var start = Math.Max(Volatile.Read(ref _pos), _headLen);
                if (size > 0 && start >= size) break;
                var end = size > 0 ? Math.Min(size, start + MaxReadAheadBytes) : start + MaxReadAheadBytes;
                if (!_ranges.ContainsRange(start, end))
                    await FetchRangeAsync(start, end, _disposeCts.Token).ConfigureAwait(false);

                await Task.Delay(100, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(250).ConfigureAwait(false); }
        }
    }

    public bool IsBodyAttached
    {
        get { lock (_stateGate) return _bodyAttached; }
    }

    public long CurrentOffset => Volatile.Read(ref _pos);
    public int ClearHeadLength => _headLen;
    public long KnownSize => Volatile.Read(ref _size);

    public void AbortBody(Exception error) => Fail(error);

    public bool TryValidateKey(string format, out string detail)
    {
        try { EnsureRangeAvailable(0, ProbeBytes); }
        catch (Exception ex) { detail = "body unavailable for key-check: " + ex.Message; return false; }

        var n = (int)Math.Min(Volatile.Read(ref _size) > 0 ? Volatile.Read(ref _size) : ProbeBytes, ProbeBytes);
        if (!_ranges.ContainsRange(0, Math.Min(n, ProbeBytes)))
        {
            detail = "body prefix unavailable for key-check";
            return false;
        }

        var dec = new byte[n];
        ReadCdnBytes(0, dec, 0, n);
        Decrypt(dec, 0);

        ReadOnlySpan<byte> magic = format is "Flac" or "Flac24" ? "fLaC"u8 : SpotifyAesCtr.OggMagic;
        var magicName = format is "Flac" or "Flac24" ? "fLaC" : "OggS";

        bool foundAt0 = HasMagic(dec, 0, magic);
        bool foundAtHdr = HasMagic(dec, SpotifyAesCtr.OggMagicOffset, magic);
        detail = foundAt0 ? $"{magicName} at offset 0 (no Spotify header) - key OK"
               : foundAtHdr ? $"{magicName} at 0xa7 (167-byte header) - key OK"
               : $"no {magicName} at 0 or 0xa7 after decrypt - key/layout check failed; got 0:{HexAt(dec, 0, magic.Length)}, 0xa7:{HexAt(dec, SpotifyAesCtr.OggMagicOffset, magic.Length)}";
        return foundAt0 || foundAtHdr;
    }

    static bool HasMagic(byte[] bytes, int offset, ReadOnlySpan<byte> magic) =>
        offset >= 0 && bytes.Length >= offset + magic.Length && bytes.AsSpan(offset, magic.Length).SequenceEqual(magic);

    static string HexAt(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || bytes.Length <= offset) return "(none)";
        var n = Math.Min(count, bytes.Length - offset);
        return Convert.ToHexString(bytes.AsSpan(offset, n));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count <= 0) return 0;

        while (true)
        {
            var size = Volatile.Read(ref _size);
            var pos = Volatile.Read(ref _pos);
            if (size > 0 && pos >= size) return 0;

            if (pos < _headLen)
            {
                var n = (int)Math.Min(count, _headLen - pos);
                Array.Copy(_head, (int)pos, buffer, offset, n);
                Volatile.Write(ref _pos, pos + n);
                return n;
            }

            WaitForBodyOrSeekIntoHead();
            pos = Volatile.Read(ref _pos);
            if (pos < _headLen) continue;

            size = Volatile.Read(ref _size);
            if (size > 0 && pos >= size) return 0;

            var wanted = size > 0 ? (int)Math.Min(count, size - pos) : count;
            if (wanted <= 0) return 0;
            EnsureRangeAvailable(pos, wanted);

            var available = _ranges.ContainedLengthFrom(pos);
            size = Volatile.Read(ref _size);
            if (size > 0) available = Math.Min(available, size - pos);
            if (available <= 0) return 0;

            var m = (int)Math.Min(wanted, available);
            ReadCdnBytes(pos, buffer, offset, m);
            Decrypt(buffer.AsSpan(offset, m), pos);
            Volatile.Write(ref _pos, pos + m);
            return m;
        }
    }

    void EnsureRangeAvailable(long start, int length)
    {
        WaitForBody();
        var size = Volatile.Read(ref _size);
        var end = size > 0 ? Math.Min(size, start + length) : start + length;
        if (start >= end) return;
        if (_ranges.ContainsRange(start, end)) return;
        var sw = Stopwatch.StartNew();
        _log?.Invoke($"stream {_name}: decode range miss range=[{start},{end}) requested={length}B pos={Volatile.Read(ref _pos)}");
        FetchRangeAsync(start, end, _disposeCts.Token).GetAwaiter().GetResult();
        _log?.Invoke($"stream {_name}: decode range ready range=[{start},{end}) elapsed={sw.ElapsedMilliseconds}ms");
    }

    async Task FetchRangeAsync(long start, long end, CancellationToken ct)
    {
        ThrowIfFailedOrDisposed();
        start = Math.Max(0, start);
        var size = Volatile.Read(ref _size);
        if (size > 0) end = Math.Min(end, size);
        if (start >= end) return;

        var gaps = _ranges.GetGaps(start, end);
        if (gaps.Count == 0) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        await _fetchGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            gaps = _ranges.GetGaps(start, end);
            foreach (var gap in gaps)
            {
                size = Volatile.Read(ref _size);
                var fetchStart = gap.Start;
                var fetchEnd = Math.Max(gap.End, gap.Start + MinFetchBytes);
                if (size > 0) fetchEnd = Math.Min(fetchEnd, size);
                if (fetchStart >= fetchEnd) continue;
                if (_ranges.ContainsRange(fetchStart, gap.End)) continue;
                await FetchChunkWithMirrorsAsync(fetchStart, fetchEnd, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    async Task FetchChunkWithMirrorsAsync(long start, long end, CancellationToken ct)
    {
        Exception? last = null;
        string[] urls;
        lock (_stateGate) urls = _cdnUrls;
        var sw = Stopwatch.StartNew();
        _log?.Invoke($"stream {_name}: range fetch start range=[{start},{end}) bytes={end - start}");

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(start, end - 1);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    last = new HttpRequestException("CDN ignored Range request");
                    continue;
                }
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    last = new HttpRequestException($"CDN {(int)resp.StatusCode}");
                    continue;
                }

                var contentRange = resp.Content.Headers.ContentRange;
                if (contentRange?.From is long from && from != start)
                    throw new HttpRequestException($"CDN returned unexpected range start {from}, expected {start}");
                if (contentRange?.Length is long total && total > 0)
                    SetSize(total);
                else if (Volatile.Read(ref _size) <= 0)
                    throw new HttpRequestException("CDN range response missing total length");

                var maxBytes = (int)Math.Min(end - start, int.MaxValue);
                var buf = new byte[maxBytes];
                var read = 0;
                await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                while (read < buf.Length)
                {
                    var n = await body.ReadAsync(buf.AsMemory(read, buf.Length - read), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }
                if (read <= 0)
                {
                    last = new IOException($"CDN returned no bytes for range [{start},{end})");
                    continue;
                }

                WriteCdnBytes(start, buf, read);
                _ranges.AddRange(start, start + read);
                lock (_stateGate) Monitor.PulseAll(_stateGate);
                _log?.Invoke($"stream {_name}: range fetch ok range=[{start},{start + read}) bytes={read} elapsed={sw.ElapsedMilliseconds}ms");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = ex;
            }
        }

        _log?.Invoke($"stream {_name}: range fetch failed range=[{start},{end}) elapsed={sw.ElapsedMilliseconds}ms error={last?.GetType().Name}: {last?.Message}");
        throw last ?? new IOException($"all CDN mirrors failed for range [{start},{end})");
    }

    void Decrypt(Span<byte> buffer, long streamOffset)
    {
        if (_decryptor is not null)
            _decryptor(buffer, streamOffset);
        else
            SpotifyAesCtr.DecryptInPlace(buffer, _key, streamOffset);
    }

    void WaitForBody()
    {
        lock (_stateGate)
        {
            while (!_bodyAttached && !_failed && !_disposed)
                Monitor.Wait(_stateGate, 50);
            ThrowIfDisposedLocked();
            if (_failed) throw _error ?? new IOException("CDN body unavailable");
        }
    }

    void WaitForBodyOrSeekIntoHead()
    {
        lock (_stateGate)
        {
            while (!_bodyAttached && !_failed && !_disposed && Volatile.Read(ref _pos) >= _headLen)
                Monitor.Wait(_stateGate, 50);
            ThrowIfDisposedLocked();
            if (_failed) throw _error ?? new IOException("CDN body unavailable");
        }
    }

    void SetSize(long size)
    {
        lock (_stateGate) SetSizeLocked(size);
    }

    void SetSizeLocked(long size)
    {
        if (size <= 0) return;
        if (size > int.MaxValue) throw new NotSupportedException("audio files larger than 2GB are not supported");
        if (_size == size) return;
        if (_size > 0 && _size != size) throw new IOException($"CDN size changed from {_size} to {size}");
        _size = size;
    }

    void WriteCdnBytes(long start, byte[] source, int count)
    {
        lock (_dataGate)
        {
            int src = 0;
            long pos = start;
            while (src < count)
            {
                int chunkIndex = (int)(pos / CdnChunkBytes);
                int chunkOffset = (int)(pos % CdnChunkBytes);
                int n = Math.Min(count - src, CdnChunkBytes - chunkOffset);
                if (!_cdnChunks.TryGetValue(chunkIndex, out var chunk))
                {
                    chunk = new byte[CdnChunkBytes];
                    _cdnChunks[chunkIndex] = chunk;
                }
                Buffer.BlockCopy(source, src, chunk, chunkOffset, n);
                src += n;
                pos += n;
            }
        }
    }

    void ReadCdnBytes(long start, byte[] destination, int destinationOffset, int count)
    {
        lock (_dataGate)
        {
            int dst = destinationOffset;
            long pos = start;
            int remaining = count;
            while (remaining > 0)
            {
                int chunkIndex = (int)(pos / CdnChunkBytes);
                int chunkOffset = (int)(pos % CdnChunkBytes);
                int n = Math.Min(remaining, CdnChunkBytes - chunkOffset);
                if (!_cdnChunks.TryGetValue(chunkIndex, out var chunk))
                    throw new IOException($"CDN range [{start},{start + count}) is not buffered");
                Buffer.BlockCopy(chunk, chunkOffset, destination, dst, n);
                dst += n;
                pos += n;
                remaining -= n;
            }
        }
    }

    void Fail(Exception ex)
    {
        lock (_stateGate)
        {
            _error = ex;
            _failed = true;
            Monitor.PulseAll(_stateGate);
        }
    }

    void ThrowIfFailedOrDisposed()
    {
        lock (_stateGate)
        {
            ThrowIfDisposedLocked();
            if (_failed) throw _error ?? new IOException("CDN body unavailable");
        }
    }

    void ThrowIfDisposedLocked()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpotifyAudioStream));
    }

    public override long Length
    {
        get
        {
            var size = Volatile.Read(ref _size);
            return size > 0 ? size : _headLen;
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek
    {
        get { lock (_stateGate) return !_disposed && _bodyAttached; }
    }
    public override bool CanWrite => false;

    public override long Position
    {
        get => Volatile.Read(ref _pos);
        set
        {
            Volatile.Write(ref _pos, Math.Max(0, value));
            lock (_stateGate) Monitor.PulseAll(_stateGate);
            StartReadAhead();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Volatile.Read(ref _pos) + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Volatile.Write(ref _pos, Math.Max(0, next));
        lock (_stateGate) Monitor.PulseAll(_stateGate);
        StartReadAhead();
        return Volatile.Read(ref _pos);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
            Monitor.PulseAll(_stateGate);
        }
        _disposeCts.Dispose();
        _fetchGate.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Dispose();
        if (_readAheadTask is not null)
        {
            try { await _readAheadTask.ConfigureAwait(false); }
            catch { }
        }
    }

    sealed class RangeSet
    {
        readonly object _lock = new();
        readonly List<ByteRange> _ranges = new();

        public bool ContainsRange(long start, long end)
        {
            if (start >= end) return true;
            lock (_lock)
            {
                var idx = FindRangeContaining(start);
                return idx >= 0 && _ranges[idx].End >= end;
            }
        }

        public long ContainedLengthFrom(long start)
        {
            lock (_lock)
            {
                var idx = FindRangeContaining(start);
                return idx < 0 ? 0 : _ranges[idx].End - start;
            }
        }

        public List<ByteRange> GetGaps(long start, long end)
        {
            var gaps = new List<ByteRange>();
            if (start >= end) return gaps;
            lock (_lock)
            {
                var cur = start;
                foreach (var range in _ranges)
                {
                    if (range.End <= cur) continue;
                    if (range.Start >= end) break;
                    if (range.Start > cur) gaps.Add(new ByteRange(cur, Math.Min(range.Start, end)));
                    cur = Math.Max(cur, range.End);
                    if (cur >= end) break;
                }
                if (cur < end) gaps.Add(new ByteRange(cur, end));
            }
            return gaps;
        }

        public void AddRange(long start, long end)
        {
            if (start >= end) return;
            lock (_lock)
            {
                var mergeStart = start;
                var mergeEnd = end;
                var first = -1;
                var last = -1;
                for (int i = 0; i < _ranges.Count; i++)
                {
                    var r = _ranges[i];
                    if (r.End >= mergeStart && r.Start <= mergeEnd)
                    {
                        if (first < 0) first = i;
                        last = i;
                        mergeStart = Math.Min(mergeStart, r.Start);
                        mergeEnd = Math.Max(mergeEnd, r.End);
                    }
                }

                var merged = new ByteRange(mergeStart, mergeEnd);
                if (first >= 0)
                {
                    _ranges.RemoveRange(first, last - first + 1);
                    _ranges.Insert(first, merged);
                }
                else
                {
                    var insert = _ranges.FindIndex(r => r.Start > end);
                    if (insert < 0) _ranges.Add(merged);
                    else _ranges.Insert(insert, merged);
                }
            }
        }

        int FindRangeContaining(long position)
        {
            for (int i = 0; i < _ranges.Count; i++)
            {
                var r = _ranges[i];
                if (position >= r.Start && position < r.End) return i;
                if (r.Start > position) break;
            }
            return -1;
        }
    }

    readonly record struct ByteRange(long Start, long End);
}

/// <summary>
/// Forward-offset wrapper: presents byte <paramref name="skip"/> of the inner stream as logical position 0.
/// </summary>
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
