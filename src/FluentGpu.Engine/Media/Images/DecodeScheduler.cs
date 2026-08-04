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
    private struct Done { public int Id; public bool Ok; public int W, H; public ImageFailureKind Failure; public int Attempts; public byte[]? Buffer; public int ByteLen; public long Sequence; }

    private readonly IImageCodec _codec;
    private readonly IImageFetcher _fetcher;
    private readonly DecodeOptions _opt;
    private readonly PixelBufferPool _pixels;   // bounded CPU pixel pool for decode dst buffers (fetch buffers stay on ArrayPool.Shared)
    private readonly ConcurrentQueue<int>[] _lanes = { new(), new(), new() };   // [Visible, Overscan, Prefetch]
    private readonly ConcurrentDictionary<int, Req> _reqs = new();
    private readonly SemaphoreSlim _signal = new(0);
    // Control completions (cancel/fail) drain independently from decoded pixels. They must never consume the GPU-upload
    // apply/byte budget or sit behind a scroll-throttled oversized texture.
    private readonly ConcurrentQueue<Done> _controlOut = new();
    // Workers classify decoded pixels by size before publishing them, so the pump can tell a 1–4 MiB cover from a
    // thumbnail without touching the payload. BOTH lanes are visible to every pump (scrolling or not): the sequence
    // stamp merges the two heads back into completion order, and the byte budget bounds only the ADDITIONAL applies
    // behind the head. Hiding the large lane during scroll (the previous rule) meant a 512x512 cover — 1 MiB, i.e.
    // every real album cover — could not land until the gesture ended, which is what pinned the visible BlurHash smear
    // for a whole homepage scroll and then popped it in afterwards.
    private readonly ConcurrentQueue<Done> _pixelOut = new();
    private readonly ConcurrentQueue<Done> _largePixelOut = new();
    // Claimed ids remain active until their terminal completion is consumed by Pump. This lets a late recycle cancel a
    // decode that has already published pixels but has not uploaded yet, without creating unbounded unknown tombstones.
    private readonly ConcurrentDictionary<int, byte> _activeIds = new();
    private readonly ConcurrentDictionary<int, byte> _canceled = new();
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _shutdown = new();
    private Action? _completionWake;
    private int _inflight, _queued;
    private long _completionSequence;
    // Max decoded images APPLIED (GPU-uploaded) per Pump = per frame. An UNBOUNDED drain uploaded a whole fast-scroll's
    // worth of album art in ONE frame → a 10-35ms GPU submit spike (the frame lands late → a stale composited frame =
    // the edge "another viewport" flash). Bounding it spreads uploads over frames: un-applied decodes stay in _out and
    // their ImageCache entries stay State==Pending. HasReadyCompletions keeps only actionable UI work awake until the
    // queue drains (rows show their skeleton/blur-hash meanwhile). FG_IMG_UPLOADS overrides; default tuned for ~120fps.
    private static readonly int s_maxAppliesPerFrame =
        int.TryParse(System.Environment.GetEnvironmentVariable("FG_IMG_UPLOADS"), out int __u) && __u > 0 ? __u : 3;
    private static readonly int s_maxApplyBytesPerFrame =
        int.TryParse(System.Environment.GetEnvironmentVariable("FG_IMG_UPLOAD_BYTES"), out int __b) && __b > 0
            ? __b : 2 * 1024 * 1024;
    // Scroll-time BURST budget + the lane-classification threshold. It is NOT a size ceiling: like the at-rest budget it
    // only refuses ADDITIONAL applies once the frame's head has been applied (see Pump). A frame's head always makes
    // progress, whatever it weighs.
    private const int ScrollApplyBytesPerFrame = 512 * 1024;
    private const int ControlDrainPerFrame = 256;

    /// <summary>Scroll-scoped upload throttle: while a scroll gesture is live the per-frame apply cap drops to 1 —
    /// each apply stages a GPU CopyTextureRegion into the SAME command list the double-buffered present then fences on
    /// (max-latency-1 couples the UI thread to GPU completion), so an upload burst mid-scroll reads as a fence-wait
    /// hitch (traced as the dominant GPU hitch class). ONE completion still lands per frame regardless of its size —
    /// the same "head always makes progress" rule the at-rest budget uses — because the alternative (deferring every
    /// oversized completion to rest) left every 512x512 cover as a BlurHash smear for the whole gesture and popped them
    /// all in at the end. One ~1 MiB upload per frame is amortizable; a permanent LQIP smear is not.
    /// (Triple-buffering was the alternative and is OFF-LIMITS: it correlated with a DXGI_ERROR_DEVICE_HUNG on the
    /// Adreno — see D3D12Device.FRAME_COUNT.)</summary>
    public bool ScrollThrottled { get; set; }
    /// <summary>Number of completions applied by the most recent UI-thread <see cref="Pump"/>.</summary>
    public int LastPumpAppliedCount { get; private set; }
    /// <summary>Decoded pixel bytes applied by the most recent UI-thread <see cref="Pump"/>.</summary>
    public int LastPumpAppliedBytes { get; private set; }
    private long _bytesDownloaded;

    public int WorkerCount => _workers.Length;
    public int Inflight => Volatile.Read(ref _inflight);
    /// <summary>Live entries in the cancellation map. Bounded by claimed terminal work: a tombstone is set only when a
    /// cancel races a claimed decode or its completed-but-unapplied pixels, and is reclaimed by the Pump drain. A
    /// queued-then-canceled request leaves none. (Census cadence only: Count takes the bucket locks.)</summary>
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
    public bool HasReadyCompletions => !_controlOut.IsEmpty || !_pixelOut.IsEmpty || !_largePixelOut.IsEmpty;

    /// <inheritdoc/>
    public void SetCompletionWake(Action? wake) => Volatile.Write(ref _completionWake, wake);

    public DecodeScheduler(IImageCodec codec, IImageFetcher fetcher, DecodeOptions? options = null)
        : this(codec, fetcher, options, startWorkers: true) { }

    /// <summary>Test-only seam: <paramref name="startWorkers"/> false constructs the scheduler with NO worker tasks, so
    /// a test can drive the claim/cancel interleaving by hand (<see cref="TryClaimForTest"/>) with no sleeps and no
    /// timing dependence. Production always uses the public constructor.</summary>
    internal DecodeScheduler(IImageCodec codec, IImageFetcher fetcher, DecodeOptions? options, bool startWorkers)
    {
        _codec = codec;
        _fetcher = fetcher;
        _opt = options ?? new DecodeOptions();
        _pixels = _opt.PixelPool ?? new PixelBufferPool();
        int workers = _opt.MaxConcurrency > 0 ? _opt.MaxConcurrency : Math.Clamp(Environment.ProcessorCount - 2, 2, 6);
        _workers = new Task[startWorkers ? workers : 0];
        for (int i = 0; i < _workers.Length; i++) _workers[i] = Task.Run(WorkerLoop);
    }

    /// <summary>Test-only hook invoked ONCE on the worker thread between a successful claim and TryClaim's return —
    /// i.e. exactly inside the window a racing <see cref="Cancel"/> must survive. Null (and free) in production.</summary>
    internal Action? ClaimBarrier;

    /// <summary>Test-only: run one <c>TryClaim</c> on the calling thread and report the claimed id.</summary>
    internal bool TryClaimForTest(out int id)
    {
        bool claimed = TryClaim(out var req);
        id = req.Id;
        return claimed;
    }

    // UI thread: non-blocking enqueue into the priority lane. Visible is never dropped; off-screen lanes drop under load.
    public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
    {
        _canceled.TryRemove(id, out _);
        if (priority != ImagePriority.Visible && Volatile.Read(ref _queued) >= _opt.QueueCapacity)
        {
            Diag.Count("media", "dropped");
            return false;   // backpressure: drop the off-screen request rather than block or grow unbounded
        }
        _reqs[id] = new Req(id, source ?? "", Math.Max(1, targetW), Math.Max(1, targetH), priority);
        Interlocked.Increment(ref _queued);
        _lanes[(int)priority].Enqueue(id);
        _signal.Release();
        return true;
    }

    // Cancel queued/claimed/unapplied work. A queued request publishes a control completion so ImageCache does not keep
    // a forever-Pending handle. A claimed request uses a tombstone retained through Pump, including the narrow window
    // after pixels were published but before their GPU upload.
    public void Cancel(int id)
    {
        if (_reqs.TryRemove(id, out _))
        {
            Complete(id, false, 0, 0, ImageFailureKind.Canceled, 0, null, 0);
            return;
        }
        // Unknown/already-consumed ids leave no residue. A claimed-or-completed-but-unapplied id stays in _activeIds
        // through Pump, so a late cancel can suppress its pending upload.
        if (_activeIds.ContainsKey(id)) _canceled[id] = 1;
    }

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
        LastPumpAppliedCount = 0;
        LastPumpAppliedBytes = 0;
        int controlDrained = 0;
        while (controlDrained < ControlDrainPerFrame && _controlOut.TryDequeue(out var control))
        {
            Finish(control.Id);
            onComplete(control.Id, control.Ok, control.W, control.H, control.Failure, control.Attempts);
            if (control.Buffer != null) _pixels.Return(control.Buffer);
            controlDrained++;
        }

        int applied = 0;
        int cap = ScrollThrottled ? Math.Min(1, s_maxAppliesPerFrame) : s_maxAppliesPerFrame;
        int byteCap = ScrollThrottled ? Math.Min(ScrollApplyBytesPerFrame, s_maxApplyBytesPerFrame) : s_maxApplyBytesPerFrame;
        int appliedBytes = 0;
        while (applied < cap && TryPeekPixels(out var next, out bool large))
        {
            // A row may recycle after the worker published pixels but before this UI-thread pump. Discard that buffer
            // as control work: no upload, no apply slot, and no byte-budget charge.
            if (_canceled.ContainsKey(next.Id))
            {
                if (!TryDequeuePixels(large, out var canceled)) continue;
                Finish(canceled.Id);
                onComplete(canceled.Id, false, 0, 0, ImageFailureKind.Canceled, canceled.Attempts);
                if (canceled.Buffer != null) _pixels.Return(canceled.Buffer);
                if (++controlDrained >= ControlDrainPerFrame) break;
                continue;
            }
            // The byte budget is a burst budget, not an absolute size ceiling — at rest AND during scroll: one oversized
            // head item may use the whole frame so it can never wedge, and the budget then refuses only the applies
            // BEHIND it. (During scroll the apply cap is 1 anyway, so this bounds the at-rest burst.)
            if (next.ByteLen > byteCap - appliedBytes && applied > 0) break;
            if (!TryDequeuePixels(large, out var d)) continue;
            // UI-thread callers normally serialize Cancel and Pump, but retain the final check for another-thread
            // cancellation between TryPeek and TryDequeue.
            if (_canceled.ContainsKey(d.Id))
            {
                Finish(d.Id);
                onComplete(d.Id, false, 0, 0, ImageFailureKind.Canceled, d.Attempts);
                if (d.Buffer != null) _pixels.Return(d.Buffer);
                if (++controlDrained >= ControlDrainPerFrame) break;
                continue;
            }
            Finish(d.Id);
            if (d.Ok && d.Buffer != null) onPixels(d.Id, d.Buffer.AsSpan(0, d.ByteLen), d.W, d.H);
            onComplete(d.Id, d.Ok, d.W, d.H, d.Failure, d.Attempts);
            if (d.Buffer != null) _pixels.Return(d.Buffer);
            appliedBytes += d.ByteLen;
            applied++;
        }
        LastPumpAppliedCount = applied;
        LastPumpAppliedBytes = appliedBytes;
        int inflight = Volatile.Read(ref _inflight);
        if (applied > 0 || controlDrained > 0 || inflight > 0)
        {
            Diag.Set("media", "inflight", inflight);
            Diag.Set("media", "queued", Volatile.Read(ref _queued));
            Diag.Set("media", "workers", _workers.Length);
            Diag.Set("media", "bytesDownloadedKB", (int)(Interlocked.Read(ref _bytesDownloaded) / 1024));
            Diag.Set("media", "poolRetainedKB", (int)(_pixels.RetainedBytes / 1024));
        }
    }

    // Merge the two size lanes back into ONE completion-ordered stream: the older Sequence wins, so a small completion
    // published before a large one still lands first. Scroll-throttled or not — the lanes exist to classify, never to
    // hide work from the pump.
    private bool TryPeekPixels(out Done done, out bool large)
    {
        bool hasSmall = _pixelOut.TryPeek(out var small);
        bool hasLarge = _largePixelOut.TryPeek(out var big);
        if (!hasSmall)
        {
            large = hasLarge;
            done = big;
            return hasLarge;
        }
        if (!hasLarge || small.Sequence <= big.Sequence)
        {
            large = false;
            done = small;
            return true;
        }
        large = true;
        done = big;
        return true;
    }

    private bool TryDequeuePixels(bool large, out Done done)
        => (large ? _largePixelOut : _pixelOut).TryDequeue(out done);

    private async Task WorkerLoop()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                if (!TryClaim(out var req)) continue;          // stale (promotion dup / canceled) → back to wait
                // NOTE: _activeIds registration happens INSIDE TryClaim, before the claim itself — see the invariant
                // comment there. Registering it here (the old shape) left a window in which the id was in NEITHER map.
                Interlocked.Increment(ref _inflight);
                try { await Process(req).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _inflight); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    // Claim the next real request from the highest non-empty lane. The first worker to TryRemove an id owns it; any
    // duplicate lane entry (from a Prioritize promotion) finds it gone and is skipped — so each request runs once.
    //
    // ID-IN-AT-LEAST-ONE-MAP INVARIANT (enforced here, not merely documented). Cancel(id) looks in exactly two places:
    // _reqs (still queued ⇒ publish a Canceled control completion) and _activeIds (claimed ⇒ set a tombstone). An id
    // must therefore be visible in one of them at EVERY instant between Begin and its terminal completion being
    // consumed by Pump — otherwise a Cancel lands in the hole and is dropped silently, and the decode later publishes
    // pixels for an already-recycled row (the 46d2 flake). The old shape registered _activeIds in WorkerLoop AFTER
    // TryClaim returned, so the claim's TryRemove(_reqs) opened exactly such a hole.
    // Registering BEFORE the claim attempt makes the two memberships OVERLAP instead of leaving a gap, and every
    // interleaving stays sound:
    //   • Cancel wins the _reqs race → it publishes the Canceled completion; our claim fails and we un-register, so
    //     the id ends up in neither map, exactly as before.
    //   • We win → Cancel falls through to the _activeIds probe, which is ALREADY true, so it tombstones; Process
    //     re-checks _canceled on entry (and again after the fetch) and completes as Canceled.
    //   • Both observe the id (the benign overlap) → Cancel completes it AND tombstones; Finish reclaims the tombstone
    //     on the Pump that drains that completion, so the bounded-tombstone contract holds.
    // The un-register is guarded by TryAdd's `added`: a Prioritize DUPLICATE whose original is already claimed must
    // not evict the live claim's registration (that would re-open the very hole this closes). `added == true` proves
    // no live claim was registered at that instant, which is also why dropping a tombstone that attached to our
    // transient entry is correct — nothing would ever consume it.
    private bool TryClaim(out Req req)
    {
        for (int lane = 0; lane < _lanes.Length; lane++)
            while (_lanes[lane].TryDequeue(out int id))
            {
                bool added = _activeIds.TryAdd(id, 0);
                if (_reqs.TryRemove(id, out req))
                {
                    Interlocked.Decrement(ref _queued);
                    ClaimBarrier?.Invoke();   // test-only: the claim/cancel race window, made deterministic
                    return true;
                }
                if (added && _activeIds.TryRemove(id, out _)) _canceled.TryRemove(id, out _);
            }
        req = default;
        return false;
    }

    private async Task Process(Req req)
    {
        // The worker claimed req.Id exclusively (TryClaim's atomic TryRemove), so this is the single owner of the id for
        // its whole lifetime. TryClaim registered it in _activeIds BEFORE the claim, so a Cancel that raced the claim
        // is guaranteed to have found it and tombstoned — this check is where that tombstone is honored. _activeIds and
        // the tombstone stay live through the UI-thread Pump so a row recycled after decode publication can still
        // suppress the pending upload.
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
            byte[] dst = _pixels.Rent(cap);                          // bounded pixel pool decode buffer (returned in Pump after upload)
            bool ok; int dw = req.W, dh = req.H;
            try { ok = _codec.DecodeConstrained(fetch.Span, req.W, req.H, dst.AsSpan(0, cap), out dw, out dh); }
            catch { ok = false; }

            if (ok && dw > 0 && dh > 0 && dw * dh * 4 <= cap)
                Complete(req.Id, true, dw, dh, ImageFailureKind.None, attempts, dst, dw * dh * 4);
            else { _pixels.Return(dst); Complete(req.Id, false, 0, 0, ImageFailureKind.Decode, attempts, null, 0); }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fetch.Buffer!);            // return the POOLED fetch buffer after decode reads it
        }
    }

    private void Complete(int id, bool ok, int w, int h, ImageFailureKind failure, int attempts, byte[]? buffer, int byteLen)
    {
        var done = new Done
        {
            Id = id, Ok = ok, W = w, H = h, Failure = failure, Attempts = attempts,
            Buffer = buffer, ByteLen = byteLen, Sequence = Interlocked.Increment(ref _completionSequence),
        };
        if (ok && buffer is not null && byteLen > 0)
        {
            if (byteLen <= ScrollApplyBytesPerFrame) _pixelOut.Enqueue(done);
            else _largePixelOut.Enqueue(done);
        }
        else _controlOut.Enqueue(done);
        Volatile.Read(ref _completionWake)?.Invoke();
    }

    private void Finish(int id)
    {
        _activeIds.TryRemove(id, out _);
        _canceled.TryRemove(id, out _);
    }

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
        // Workers are joined ⇒ no decode is in flight ⇒ safe to release the codec's native COM state (e.g. the Windows
        // WIC leaf's shared IWICImagingFactory). No-op for codecs that hold none.
        (_codec as IDisposable)?.Dispose();
        _signal.Dispose();
        _shutdown.Dispose();
    }
}
