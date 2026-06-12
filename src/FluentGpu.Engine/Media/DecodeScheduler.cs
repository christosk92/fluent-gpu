using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Media;

/// <summary>
/// Off-thread, parallel, non-blocking, PRIORITIZED image decoder (media-pipeline.md §3). Implements the portable
/// <see cref="IImageDecoder"/> seam the <see cref="ImageCache"/> drives: <see cref="Begin"/> enqueues into one of three
/// priority lanes (Visible &gt; Overscan &gt; Prefetch) — a non-blocking write on the UI thread; a pool of N worker tasks
/// drain the highest non-empty lane and fetch+decode CONCURRENTLY; and <see cref="Pump"/> drains finished results on the
/// UI thread and never waits on a decode. <see cref="Prioritize"/> promotes a queued prefetch that just scrolled into
/// view; <see cref="Cancel"/> drops a queued/in-flight decode whose row recycled. Robustness: per-attempt timeout,
/// transient retry with exponential backoff, permanent fail-fast (see <see cref="ImageFailureKind"/>). Under backpressure
/// the lowest off-screen lane is dropped — never Visible. Diagnostics post to the <c>media</c> counter group.
/// </summary>
public sealed class DecodeScheduler : IImageDecoder, IDisposable
{
    private readonly record struct Req(int Id, string Src, int W, int H, ImagePriority Priority);
    private struct Done { public int Id; public bool Ok; public int W, H; public ImageFailureKind Failure; public int Attempts; public byte[]? Buffer; public int ByteLen; }

    private readonly IImageCodec _codec;
    private readonly IImageFetcher _fetcher;
    private readonly DecodeOptions _opt;
    private readonly ConcurrentQueue<int>[] _lanes = { new(), new(), new() };   // [Visible, Overscan, Prefetch]
    private readonly ConcurrentDictionary<int, Req> _reqs = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentQueue<Done> _out = new();
    private readonly ConcurrentDictionary<int, byte> _canceled = new();
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _shutdown = new();
    private int _inflight, _queued;
    private long _bytesDownloaded;

    public int WorkerCount => _workers.Length;
    public int Inflight => Volatile.Read(ref _inflight);
    /// <summary>Live entries in the cancellation map. Bounded by <see cref="Inflight"/>: a tombstone is set only when a
    /// cancel races a claimed (in-flight) decode, and is reclaimed at that decode's terminal point (Process's finally /
    /// the Pump drain) — a queued-then-canceled request leaves none. Reaches 0 once all decodes drain. (Census-cadence
    /// only: <c>ConcurrentDictionary.Count</c> takes the bucket locks, but the map holds at most a handful of entries.)</summary>
    public int CanceledPending => _canceled.Count;
    /// <summary>Requests enqueued in the priority lanes but not yet claimed by a worker — O(1) census. NOTE: not
    /// decremented for a cancel-before-claim id (TryClaim dequeues-and-skips it without a successful claim), so this
    /// over-counts after queued cancels until those lane entries are skipped — soft-backpressure heuristic only, never
    /// a drain/idle condition.</summary>
    public int QueueDepth => Volatile.Read(ref _queued);
    /// <summary>Pending request descriptors awaiting claim — census of the <c>_reqs</c> map (bucket-locked Count).</summary>
    public int RequestCount => _reqs.Count;

    // IImageDecoder census passthroughs (MemCensus reads these through ImageCache).
    int IImageDecoder.DiagInflight => Volatile.Read(ref _inflight);
    int IImageDecoder.DiagCanceledPending => _canceled.Count;

    public DecodeScheduler(IImageCodec codec, IImageFetcher fetcher, DecodeOptions? options = null)
    {
        _codec = codec;
        _fetcher = fetcher;
        _opt = options ?? new DecodeOptions();
        int workers = _opt.MaxConcurrency > 0 ? _opt.MaxConcurrency : Math.Clamp(Environment.ProcessorCount - 2, 2, 6);
        _workers = new Task[workers];
        for (int i = 0; i < workers; i++) _workers[i] = Task.Run(WorkerLoop);
    }

    // UI thread: non-blocking enqueue into the priority lane. Visible is never dropped; off-screen lanes drop under load.
    public void Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
    {
        _canceled.TryRemove(id, out _);
        if (priority != ImagePriority.Visible && Volatile.Read(ref _queued) >= _opt.QueueCapacity)
        {
            Diag.Count("media", "dropped");
            return;   // backpressure: drop the off-screen request rather than block or grow unbounded
        }
        _reqs[id] = new Req(id, source ?? "", Math.Max(1, targetW), Math.Max(1, targetH), priority);
        Interlocked.Increment(ref _queued);
        _lanes[(int)priority].Enqueue(id);
        _signal.Release();
    }

    // Cancel a queued/in-flight decode. If the request is still queued (TryRemove succeeds) the _reqs removal alone
    // suppresses it — TryClaim will dequeue the lane entry, find _reqs empty, and drop it; NO tombstone is needed, so a
    // cancel-before-claim leaves nothing behind (this is the bulk of recycled-row cancels under virtualized scroll). The
    // tombstone is set ONLY when the request was already claimed (TryRemove fails ⇒ a worker owns it and is mid-Process):
    // then _canceled is the sole channel that aborts the in-flight decode/upload, and that worker reclaims it (Process's
    // finally / the Pump drain). Invariant: every live _canceled entry maps to one in-flight decode and is reclaimed at
    // its terminal point — so the map is bounded by Inflight, not by total cancels.
    public void Cancel(int id) { if (!_reqs.TryRemove(id, out _)) _canceled[id] = 1; }

    public void Prioritize(int id, ImagePriority priority)
    {
        if (_reqs.TryGetValue(id, out var r) && priority < r.Priority)   // raise urgency only (lower enum = higher)
        {
            _reqs[id] = r with { Priority = priority };
            _lanes[(int)priority].Enqueue(id);   // a higher-lane copy; the lower-lane copy becomes a no-op (claim dedup)
            _signal.Release();
        }
    }

    // UI thread: drain finished decodes; upload pixels; report completion. Idle ⇒ one empty TryDequeue, zero alloc.
    public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
    {
        int drained = 0;
        while (_out.TryDequeue(out var d))
        {
            if (d.Ok && d.Buffer != null) onPixels(d.Id, d.Buffer.AsSpan(0, d.ByteLen), d.W, d.H);
            onComplete(d.Id, d.Ok, d.W, d.H, d.Failure, d.Attempts);
            if (d.Buffer != null) ArrayPool<byte>.Shared.Return(d.Buffer);
            // This decode is terminal — reclaim any tombstone a cancel set after the worker's finally (the Done was
            // already queued). The apply above is unchanged: a late cancel does NOT suppress it (today's semantics);
            // reclaim only bounds the map. Idempotent with Process's finally (a no-op when it already removed the id).
            _canceled.TryRemove(d.Id, out _);
            drained++;
        }
        int inflight = Volatile.Read(ref _inflight);
        if (drained > 0 || inflight > 0)
        {
            Diag.Set("media", "inflight", inflight);
            Diag.Set("media", "queued", Volatile.Read(ref _queued));
            Diag.Set("media", "workers", _workers.Length);
            Diag.Set("media", "bytesDownloadedKB", (int)(Interlocked.Read(ref _bytesDownloaded) / 1024));
        }
    }

    private async Task WorkerLoop()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                if (!TryClaim(out var req)) continue;          // stale (promotion dup / canceled) → back to wait
                Interlocked.Increment(ref _inflight);
                try { await Process(req).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _inflight); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    // Claim the next real request from the highest non-empty lane. The first worker to TryRemove an id owns it; any
    // duplicate lane entry (from a Prioritize promotion) finds it gone and is skipped — so each request runs once.
    private bool TryClaim(out Req req)
    {
        for (int lane = 0; lane < _lanes.Length; lane++)
            while (_lanes[lane].TryDequeue(out int id))
                if (_reqs.TryRemove(id, out req))
                {
                    Interlocked.Decrement(ref _queued);
                    return true;
                }
        req = default;
        return false;
    }

    private async Task Process(Req req)
    {
        // The worker claimed req.Id exclusively (TryClaim's atomic TryRemove), so this is the single owner of the id for
        // its whole lifetime. Every in-Process _canceled check above has run by the time control reaches the finally; the
        // tombstone has discharged its only duty (abort this in-flight decode), so reclaim it here on EVERY exit path
        // (entry-cancel, fetch-fail, post-fetch-cancel, decode result). A cancel racing in after the finally has no
        // in-flight decode left to abort and is reclaimed by the Pump drain instead — so no tombstone outlives its decode.
        try
        {
            if (_canceled.ContainsKey(req.Id)) { Complete(req.Id, false, 0, 0, ImageFailureKind.Canceled, 0, null, 0); return; }

            var (fetch, attempts) = await FetchWithRetry(req.Src, req.Id).ConfigureAwait(false);
            if (!fetch.Ok)
            {
                if (fetch.Buffer != null) ArrayPool<byte>.Shared.Return(fetch.Buffer);
                Complete(req.Id, false, 0, 0, fetch.Failure, attempts, null, 0);
                return;
            }
            Interlocked.Add(ref _bytesDownloaded, fetch.Length);

            try
            {
                if (_canceled.ContainsKey(req.Id)) { Complete(req.Id, false, 0, 0, ImageFailureKind.Canceled, attempts, null, 0); return; }

                int cap = req.W * req.H * 4;
                byte[] dst = ArrayPool<byte>.Shared.Rent(cap);           // POOLED decode buffer (returned in Pump after upload)
                bool ok; int dw = req.W, dh = req.H;
                try { ok = _codec.DecodeConstrained(fetch.Span, req.W, req.H, dst.AsSpan(0, cap), out dw, out dh); }
                catch { ok = false; }

                if (ok && dw > 0 && dh > 0 && dw * dh * 4 <= cap)
                    Complete(req.Id, true, dw, dh, ImageFailureKind.None, attempts, dst, dw * dh * 4);
                else { ArrayPool<byte>.Shared.Return(dst); Complete(req.Id, false, 0, 0, ImageFailureKind.Decode, attempts, null, 0); }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(fetch.Buffer!);            // return the POOLED fetch buffer after decode reads it
            }
        }
        finally
        {
            _canceled.TryRemove(req.Id, out _);   // reclaim this id's tombstone: its decode is terminal (bounds _canceled by Inflight)
        }
    }

    private void Complete(int id, bool ok, int w, int h, ImageFailureKind failure, int attempts, byte[]? buffer, int byteLen)
        => _out.Enqueue(new Done { Id = id, Ok = ok, W = w, H = h, Failure = failure, Attempts = attempts, Buffer = buffer, ByteLen = byteLen });

    private async Task<(FetchResult result, int attempts)> FetchWithRetry(string src, int id)
    {
        ImageFailureKind last = ImageFailureKind.Network;
        for (int attempt = 1; attempt <= _opt.MaxAttempts; attempt++)
        {
            if (_shutdown.IsCancellationRequested || _canceled.ContainsKey(id)) return (FetchResult.Fail(ImageFailureKind.Canceled), attempt);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cts.CancelAfter(_opt.RequestTimeout);   // slow-internet deadline → maps to a transient Timeout
            FetchResult r;
            try { r = await _fetcher.FetchAsync(src, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return (FetchResult.Fail(ImageFailureKind.Canceled), attempt); }
            catch (OperationCanceledException) { r = FetchResult.Fail(ImageFailureKind.Timeout); }   // per-attempt deadline
            catch { r = FetchResult.Fail(ImageFailureKind.Network); }                                // HttpRequestException / IO / DNS

            if (r.Ok) return (r, attempt);
            last = r.Failure;
            if (!IsTransient(last) || attempt == _opt.MaxAttempts) return (FetchResult.Fail(last), attempt);

            double ms = Math.Min(_opt.BackoffMax.TotalMilliseconds, _opt.BackoffBase.TotalMilliseconds * Math.Pow(2, attempt - 1));
            try { await Task.Delay(TimeSpan.FromMilliseconds(ms), _shutdown.Token).ConfigureAwait(false); }   // backoff on the WORKER, never the UI
            catch (OperationCanceledException) { return (FetchResult.Fail(ImageFailureKind.Canceled), attempt); }
        }
        return (FetchResult.Fail(last), _opt.MaxAttempts);
    }

    private static bool IsTransient(ImageFailureKind k)
        => k is ImageFailureKind.Network or ImageFailureKind.Timeout or ImageFailureKind.ServerError;

    public void Dispose()
    {
        _shutdown.Cancel();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(2)); } catch { /* best-effort drain on shutdown */ }
        _signal.Dispose();
        _shutdown.Dispose();
    }
}
