using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace Wavee.Backend.Audio;

/// <summary>
/// Decrypt-agnostic ranged-HTTP byte source: HTTP Range GETs with mirror failover, a background read-ahead, and a
/// buffered raw-chunk store tracked by a <see cref="RangeSet"/>. It stores RAW (untransformed) bytes only — any decrypt
/// transform is applied by the CALLER on copy-out (see <see cref="ReadRaw"/>), which is what keeps range re-reads and
/// clean-span reuse correct. Extracted verbatim from <see cref="SpotifyAudioStream"/> (Stage 1) so the plain-HTTP and
/// Spotify-CDN paths can share one fetch layer. Knows nothing about clear heads, decrypt, or <see cref="Stream"/>.
/// </summary>
internal sealed class RangedHttpSource : IDisposable
{
    const int MinFetchBytes = 64 * 1024;
    const int MaxReadAheadBytes = 256 * 1024;
    const int CdnChunkBytes = 64 * 1024;
    static readonly bool RangeTrace = string.Equals(
        Environment.GetEnvironmentVariable("WAVEE_AUDIO_RANGE_TRACE"), "1", StringComparison.Ordinal);

    readonly HttpClient _http;
    readonly string _name;
    readonly Action<string>? _log;
    readonly int _headFloor;                 // read-ahead never dips below this (the caller's clear-head length)
    readonly Action? _onRangeAvailable;      // wake the caller's readers after a fetch / resume (caller pulses its gate)
    readonly bool _requireRange;             // false = tolerate a 200 (server ignored Range) by buffering the whole body
    readonly int _maxRetries;                // per-mirror attempts for transient 5xx / network faults
    readonly int _baseBackoffMs;             // exponential backoff base: _baseBackoffMs << attempt
    readonly RangeSet _ranges = new();
    readonly SemaphoreSlim _fetchGate = new(2, 2);
    readonly CancellationTokenSource _disposeCts = new();
    readonly object _sizeGate = new();
    readonly object _dataGate = new();
    readonly Dictionary<int, byte[]> _cdnChunks = new();

    string[] _cdnUrls = [];
    long _size;
    long _readAheadOffset;
    int _readAheadPauseCount;
    int _readAheadResourcesDisposed;
    volatile bool _stopped;
    Task? _readAheadTask;

    public RangedHttpSource(HttpClient http, string name, Action<string>? log, int headFloor,
        Action? onRangeAvailable, bool requireRange = true, int maxRetries = 3, int baseBackoffMs = 150)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _name = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        _log = log;
        _headFloor = Math.Max(0, headFloor);
        _onRangeAvailable = onRangeAvailable;
        _requireRange = requireRange;
        _maxRetries = Math.Max(1, maxRetries);
        _baseBackoffMs = Math.Max(0, baseBackoffMs);
    }

    public long KnownSize => Volatile.Read(ref _size);
    public bool ContainsRange(long start, long end) => _ranges.ContainsRange(start, end);
    public long ContainedLengthFrom(long start) => _ranges.ContainedLengthFrom(start);

    /// <summary>Set the mirror list (+ optional known size). Called once before <see cref="StartReadAhead"/>.</summary>
    public void Configure(string[] cdnUrls, long? knownSize)
    {
        var urls = cdnUrls.Where(static u => !string.IsNullOrWhiteSpace(u)).ToArray();
        if (urls.Length == 0) throw new InvalidOperationException("no CDN urls");
        _cdnUrls = urls;
        if (knownSize is > 0) SetSize(knownSize.Value);
    }

    /// <summary>Eager priming for the non-lazy attach path: first <see cref="MinFetchBytes"/> + the head-boundary window.</summary>
    public async Task PrimeAsync(CancellationToken ct)
    {
        var size = Volatile.Read(ref _size);
        var initialEnd = Math.Min(size > 0 ? size : MinFetchBytes, MinFetchBytes);
        await FetchRangeAsync(0, initialEnd, ct).ConfigureAwait(false);
        await PrefetchHeadBoundaryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stop read-ahead (the caller failed). Idempotent; the loop exits at its next check.</summary>
    public void Stop() => _stopped = true;

    public void StartReadAhead()
    {
        if (_stopped || _disposeCts.IsCancellationRequested) return;
        if (_readAheadTask is { IsCompleted: false }) return;
        _readAheadTask = Task.Run(ReadAheadLoopAsync, CancellationToken.None);
    }

    public void MarkProgress(long offset)
    {
        if (Volatile.Read(ref _readAheadPauseCount) > 0) return;
        Volatile.Write(ref _readAheadOffset, Math.Max(0, offset));
        StartReadAhead();
    }

    public IDisposable PauseReadAhead()
    {
        Interlocked.Increment(ref _readAheadPauseCount);
        return new ReadAheadPause(this);
    }

    public void ResumeReadAheadAt(long offset)
    {
        Volatile.Write(ref _readAheadOffset, Math.Max(offset, _headFloor));
        _onRangeAvailable?.Invoke();
        StartReadAhead();
    }

    void ReleaseReadAheadPause()
    {
        if (Interlocked.Decrement(ref _readAheadPauseCount) < 0)
            Interlocked.Exchange(ref _readAheadPauseCount, 0);
        _onRangeAvailable?.Invoke();
    }

    async Task PrefetchHeadBoundaryAsync(CancellationToken ct)
    {
        var size = Volatile.Read(ref _size);
        if (_headFloor <= 0 || (size > 0 && _headFloor >= size)) return;

        var start = _headFloor;
        var end = size > 0
            ? Math.Min(size, start + MaxReadAheadBytes)
            : start + MaxReadAheadBytes;
        if (_ranges.ContainsRange(start, end)) return;

        var sw = Stopwatch.StartNew();
        if (RangeTrace) _log?.Invoke($"stream {_name}: prefetch boundary start range=[{start},{end})");
        await FetchRangeAsync(start, end, ct).ConfigureAwait(false);
        if (RangeTrace) _log?.Invoke($"stream {_name}: prefetch boundary ok bytes={end - start} elapsed={sw.ElapsedMilliseconds}ms");
    }

    async Task ReadAheadLoopAsync()
    {
        while (!_disposeCts.IsCancellationRequested)
        {
            try
            {
                if (_stopped) break;
                var size = Volatile.Read(ref _size);

                if (Volatile.Read(ref _readAheadPauseCount) > 0)
                {
                    await Task.Delay(50, _disposeCts.Token).ConfigureAwait(false);
                    continue;
                }

                var start = Math.Max(Volatile.Read(ref _readAheadOffset), _headFloor);
                if (size > 0 && start >= size) break;
                var end = size > 0 ? Math.Min(size, start + MaxReadAheadBytes) : start + MaxReadAheadBytes;
                if (!_ranges.ContainsRange(start, end))
                    await FetchRangeAsync(start, end, _disposeCts.Token).ConfigureAwait(false);

                await Task.Delay(100, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(250).ConfigureAwait(false); }
        }
    }

    /// <summary>Blocking: ensure [start, start+length) is buffered, fetching synchronously on a miss. Throws on
    /// unrecoverable fetch failure (the caller's read path surfaces it exactly as before).</summary>
    public void EnsureRange(long start, int length)
    {
        var size = Volatile.Read(ref _size);
        var end = size > 0 ? Math.Min(size, start + length) : start + length;
        if (start >= end) return;
        if (_ranges.ContainsRange(start, end)) return;
        var sw = Stopwatch.StartNew();
        if (RangeTrace) _log?.Invoke($"stream {_name}: decode range miss range=[{start},{end}) requested={length}B");
        FetchRangeAsync(start, end, _disposeCts.Token).GetAwaiter().GetResult();
        if (RangeTrace) _log?.Invoke($"stream {_name}: decode range ready range=[{start},{end}) elapsed={sw.ElapsedMilliseconds}ms");
    }

    async Task FetchRangeAsync(long start, long end, CancellationToken ct)
    {
        if (_stopped) throw new IOException("ranged source stopped");
        if (_disposeCts.IsCancellationRequested) throw new ObjectDisposedException(nameof(RangedHttpSource));
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
        var urls = _cdnUrls;
        var sw = Stopwatch.StartNew();
        if (RangeTrace) _log?.Invoke($"stream {_name}: range fetch start range=[{start},{end}) bytes={end - start}");

        foreach (var url in urls)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new RangeHeaderValue(start, end - 1);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        if (_requireRange) { last = new HttpRequestException("CDN ignored Range request"); break; }
                        // Range-optional (plain-HTTP server ignored Range): buffer the whole body once and serve all reads from it.
                        await BufferFullBodyAsync(resp, ct).ConfigureAwait(false);
                        _onRangeAvailable?.Invoke();
                        if (RangeTrace) _log?.Invoke($"stream {_name}: full-body fetch ok (range ignored) size={Volatile.Read(ref _size)} elapsed={sw.ElapsedMilliseconds}ms");
                        return;
                    }
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                    {
                        last = new HttpRequestException($"CDN {(int)resp.StatusCode}");
                        if ((int)resp.StatusCode >= 500 && attempt + 1 < _maxRetries)
                        {
                            await Task.Delay(_baseBackoffMs << Math.Min(attempt, 5), ct).ConfigureAwait(false);   // transient 5xx: retry same mirror
                            continue;
                        }
                        break;   // 4xx / exhausted → next mirror
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
                        break;
                    }

                    WriteCdnBytes(start, buf, read);
                    _ranges.AddRange(start, start + read);
                    _onRangeAvailable?.Invoke();
                    if (RangeTrace) _log?.Invoke($"stream {_name}: range fetch ok range=[{start},{start + read}) bytes={read} elapsed={sw.ElapsedMilliseconds}ms");
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    last = ex;
                    if (attempt + 1 >= _maxRetries) break;   // exhausted this mirror's retries → next mirror
                    await Task.Delay(_baseBackoffMs << Math.Min(attempt, 5), ct).ConfigureAwait(false);   // transient network fault: backoff + retry (ct cancels)
                }
            }
        }

        _log?.Invoke($"stream {_name}: range fetch failed range=[{start},{end}) elapsed={sw.ElapsedMilliseconds}ms error={last?.GetType().Name}: {last?.Message}");
        throw last ?? new IOException($"all CDN mirrors failed for range [{start},{end})");
    }

    /// <summary>Range-optional path: the server ignored our Range and returned 200 with the whole file. Buffer it once
    /// from offset 0 and record the size, so every subsequent read is satisfied from the store.</summary>
    async Task BufferFullBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct).ConfigureAwait(false);
        var len = (int)ms.Length;
        if (len <= 0) throw new IOException("plain-HTTP server returned an empty body");
        SetSize(len);
        WriteCdnBytes(0, ms.GetBuffer(), len);
        _ranges.AddRange(0, len);
    }

    /// <summary>Copy buffered RAW (untransformed) bytes into <paramref name="destination"/>. The caller applies any
    /// decrypt transform afterwards. Throws if the range is not buffered — same contract as the old ReadCdnBytes.</summary>
    public void ReadRaw(long start, byte[] destination, int destinationOffset, int count)
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

    void SetSize(long size)
    {
        lock (_sizeGate)
        {
            if (size <= 0) return;
            if (size > int.MaxValue) throw new NotSupportedException("audio files larger than 2GB are not supported");
            if (_size == size) return;
            if (_size > 0 && _size != size) throw new IOException($"CDN size changed from {_size} to {size}");
            _size = size;
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _disposeCts.Cancel();
        var readAhead = _readAheadTask;
        if (readAhead is { IsCompleted: false })
        {
            try { readAhead.Wait(250); } catch { }
        }
        DisposeReadAheadResources();
    }

    void DisposeReadAheadResources()
    {
        if (Interlocked.Exchange(ref _readAheadResourcesDisposed, 1) != 0) return;
        _disposeCts.Dispose();
        _fetchGate.Dispose();
    }

    sealed class ReadAheadPause : IDisposable
    {
        RangedHttpSource? _owner;

        public ReadAheadPause(RangedHttpSource owner) => _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseReadAheadPause();
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
