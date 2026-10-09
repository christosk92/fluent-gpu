using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private readonly PixelBufferPool _pixels;   // bounded CPU pixel pool for decode dst buffers (fetch buffers go back through IImageFetcher.ReturnBuffer); a buffer the sink TOOK in Pump is returned here by the render thread via ImageUploadQueue.ReturnUploadBuffer
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
    // What one Pump (= one frame) may APPLY is metered against the ONE permanent upload budget, UploadBudget.BytesPerTurn
    // (retained tiles §C — a live tunable, never an environment variable, never scroll-keyed, the same on every GPU tier):
    // applies continue while their decoded bytes fit, and the head of a frame always lands whatever it weighs (a 1 MiB
    // cover can never wedge behind a small budget). An UNBOUNDED drain once uploaded a whole fast-scroll's worth of album
    // art in one frame (a 10-35 ms submit spike); the budget spreads that over frames — un-applied decodes stay queued and
    // their ImageCache entries Pending (rows show their skeleton / blur-hash meanwhile).
    // The two completion lanes split at this size (small thumbs vs covers) — a classification, not a cap: the pump merges
    // both heads back into completion order.
    private const int LargeLaneBytes = 512 * 1024;
    private const int ControlDrainPerFrame = 256;


    /// <summary>The smallest UI-thread slice a deadline-bounded <see cref="Pump(ImageCompleteHandler, ImageReadyHandler, long)"/>
    /// is handed by a caller that bounds it at all (the host pumps unbounded — <c>long.MaxValue</c> — every frame since
    /// the scroll rework: there is no frame budget). 1.5 ms is ample for one head apply (under the async render thread
    /// the apply is an ownership handoff + cache bookkeeping, no memcpy) and small against an 8.3 ms 120 Hz frame.</summary>
    public const float PumpMinSliceMs = 1.5f;
    /// <summary><see cref="PumpMinSliceMs"/> in <see cref="Stopwatch"/> ticks, computed once.</summary>
    public static readonly long PumpMinSliceTicks = (long)(PumpMinSliceMs * Stopwatch.Frequency / 1000.0);

    // ── Decode-buffer ownership handoff (UI thread, call-scoped) ─────────────────────────────────────────────────
    // Pump hands `onPixels` a span over the worker's pooled decode buffer and used to Return that buffer one line
    // later, which forced the async host sink to Rent a SECOND buffer and memcpy the pixels on the UI thread —
    // 256 KB–1 MiB per cover, an LOH-class copy per apply during a fling. The sink may instead TAKE the decode buffer:
    // while `onPixels` runs, the buffer is on loan in these thread-static slots and TryTakeDecodeBuffer transfers
    // ownership when the offered span is exactly the loaned pixels. A taken buffer is NOT returned by Pump; the taker
    // returns it to the SAME PixelBufferPool the scheduler rents from (ImageUploadQueue.ReturnUploadBuffer → its
    // BufferPool, which FluentApp/AppHost.PixelPool point at this scheduler's DecodeOptions.PixelPool) after the
    // render thread staged it — one Rent→Return cycle per cover, zero UI-thread pixel allocation.
    // Thread-static, not an instance member, because the sink reaches this scheduler only through the span-typed
    // ImageReadyHandler → ImageCache → ImageUploadAttemptHandler chain, and a span cannot name its array. The slots
    // are written immediately before and cleared immediately after the synchronous callback (finally-guarded), so a
    // loan never outlives one apply, never crosses threads, and a stale loan can never be taken by a later frame.
    [ThreadStatic] private static byte[]? t_loanBuffer;
    [ThreadStatic] private static int t_loanByteLen;
    [ThreadStatic] private static bool t_loanTaken;

    /// <summary>Host pixel sink: take ownership of the decode buffer behind <paramref name="pixels"/> instead of
    /// copying it. Succeeds only while a <see cref="Pump"/> apply is running the sink on this thread AND
    /// <paramref name="pixels"/> is exactly that apply's loaned span (same start, same length) — any other span (the
    /// blur-hash LQIP scratch, <c>FakeImageDecoder</c>'s scratch, a non-scheduler decoder) returns false and the caller
    /// copies as before. On success the caller owns <paramref name="buffer"/> (<c>buffer.Length ≥ pixels.Length</c>;
    /// only the first <c>pixels.Length</c> bytes are pixels) and MUST return it exactly once to the scheduler's
    /// <see cref="PixelBufferPool"/> (<see cref="DecodeOptions.PixelPool"/>) — the pool drops foreign sizes, never
    /// throws. Zero-alloc; a second call for the same apply returns false.</summary>
    public static bool TryTakeDecodeBuffer(ReadOnlySpan<byte> pixels, [NotNullWhen(true)] out byte[]? buffer)
    {
        byte[]? loan = t_loanBuffer;
        if (loan is not null && !t_loanTaken && pixels.Length == t_loanByteLen && pixels.Length > 0
            && Unsafe.AreSame(ref MemoryMarshal.GetReference(pixels), ref MemoryMarshal.GetArrayDataReference(loan)))
        {
            t_loanTaken = true;
            buffer = loan;
            return true;
        }
        buffer = null;
        return false;
    }
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
    /// <summary>Requests enqueued in the priority lanes but not yet claimed or canceled — O(1) census of <c>_reqs</c>
    /// membership: Begin increments, and whichever of a worker's claim or <see cref="Cancel"/> removes the request
    /// decrements. Stale lane entries (a promotion's duplicate, a canceled id) are not counted. It feeds the off-screen
    /// backpressure gate in <see cref="Begin"/>.</summary>
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

    /// <summary>Test-only hook invoked on the calling thread between <see cref="Prioritize"/>'s read of the queued
    /// request and its write — the window a racing worker claim must not be able to undo. Null (and free) in production.</summary>
    internal Action? PrioritizeBarrier;

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
            // This TryRemove and TryClaim's are the only two ways out of _reqs, and exactly one of them wins an id, so
            // each pays back Begin's increment. Without it every row recycled before a worker claimed it leaked +1 into
            // _queued until the backpressure gate refused every off-screen request for the rest of the session.
            Interlocked.Decrement(ref _queued);
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
            PrioritizeBarrier?.Invoke();   // test-only: the read/write race window, made deterministic
            // TryUpdate against the value read, never the indexer: a worker may claim the id (TryClaim's TryRemove)
            // between the read and the write, and the indexer would put the claimed request BACK — the promoted lane
            // copy then lets a second worker claim it too, so the image decodes twice and lands two ok completions.
            // A failed update means the request is no longer queued: nothing to promote.
            if (!_reqs.TryUpdate(id, r with { Priority = priority }, r)) return;
            _lanes[(int)priority].Enqueue(id);   // a higher-lane copy; the lower-lane copy becomes a no-op (claim dedup)
            _signal.Release();
        }
    }

    // UI thread: drain finished decodes; upload pixels; report completion. Idle ⇒ one empty TryDequeue, zero alloc.
    public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) => Pump(onComplete, onPixels, long.MaxValue);

    /// <summary>Deadline-bounded overload: stops APPLYING further completions once <see cref="Stopwatch.GetTimestamp"/>
    /// passes <paramref name="deadlineTicks"/> — the un-applied decodes stay queued (their <c>ImageCache</c> entries stay
    /// <c>State==Pending</c>) for a later Pump, same visible effect as the per-frame apply cap running out early. The
    /// deadline is an ADDITIONAL, independent stop condition checked BETWEEN applies (never mid-apply); the head-apply
    /// exemption still guarantees a frame's head always makes progress. <c>long.MaxValue</c> (the two-arg overload
    /// above — what the host passes every frame) never reads the clock — zero extra cost. A caller that does bound the
    /// pump must never pass an already-elapsed deadline: hand it <c>max(deadline, now + <see cref="PumpMinSliceTicks"/>)</c>.</summary>
    public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels, long deadlineTicks)
    {
        LastPumpAppliedCount = 0;
        LastPumpAppliedBytes = 0;
        bool bounded = deadlineTicks != long.MaxValue;
        int controlDrained = 0;
        while (controlDrained < ControlDrainPerFrame && _controlOut.TryDequeue(out var control))
        {
            Finish(control.Id);
            onComplete(control.Id, control.Ok, control.W, control.H, control.Failure, control.Attempts);
            if (control.Buffer != null) _pixels.Return(control.Buffer);
            controlDrained++;
        }

        int applied = 0;
        int appliedBytes = 0;
        var meter = new FluentGpu.Rhi.UploadTurnMeter();
        meter.Begin(FluentGpu.Rhi.UploadBudget.BytesPerTurn);
        while ((!bounded || Stopwatch.GetTimestamp() < deadlineTicks) && TryPeekPixels(out var next, out bool large))
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
            // The budget meters bytes, not items: the frame's HEAD is always admitted (an oversized cover can never wedge),
            // and it refuses only the applies BEHIND it once they no longer fit.
            if (!meter.TryAdmit(next.ByteLen)) break;
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
            // Loan the decode buffer to the sink for the duration of the callback (see TryTakeDecodeBuffer). A sink
            // that took it now owns it and returns it to _pixels itself (render thread, after Stage); a sink that only
            // read the span leaves it with us and we return it below. finally: an exception escaping the sink (the
            // sync path's device-lost throw) must not leave a stale loan for a later frame to take.
            bool taken = false;
            if (d.Ok && d.Buffer != null)
            {
                t_loanBuffer = d.Buffer; t_loanByteLen = d.ByteLen; t_loanTaken = false;
                try { onPixels(d.Id, d.Buffer.AsSpan(0, d.ByteLen), d.W, d.H); }
                finally { taken = t_loanTaken; t_loanBuffer = null; t_loanByteLen = 0; t_loanTaken = false; }
            }
            onComplete(d.Id, d.Ok, d.W, d.H, d.Failure, d.Attempts);
            if (d.Buffer != null && !taken) _pixels.Return(d.Buffer);
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
            if (fetch.Buffer != null) _fetcher.ReturnBuffer(fetch.Buffer);
            Complete(req.Id, false, 0, 0, fetch.Failure, attempts, null, 0);
            return;
        }
        Interlocked.Add(ref _bytesDownloaded, fetch.Length);

        try
        {
            if (_canceled.ContainsKey(req.Id)) { Complete(req.Id, false, 0, 0, ImageFailureKind.Canceled, attempts, null, 0); return; }

            int cap = req.W * req.H * 4;
            byte[] dst = _pixels.Rent(cap);                          // bounded pixel pool decode buffer (returned in Pump after upload, or by the sink that took it — TryTakeDecodeBuffer)
            bool ok; int dw = req.W, dh = req.H;
            try { ok = _codec.DecodeConstrained(fetch.Span, req.W, req.H, dst.AsSpan(0, cap), out dw, out dh); }
            catch { ok = false; }

            if (ok && dw > 0 && dh > 0 && dw * dh * 4 <= cap)
                Complete(req.Id, true, dw, dh, ImageFailureKind.None, attempts, dst, dw * dh * 4);
            else { _pixels.Return(dst); Complete(req.Id, false, 0, 0, ImageFailureKind.Decode, attempts, null, 0); }
        }
        finally
        {
            _fetcher.ReturnBuffer(fetch.Buffer!);            // back to the fetcher's own pool after decode reads it
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
            if (byteLen <= LargeLaneBytes) _pixelOut.Enqueue(done);
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
