using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;

namespace FluentGpu.Scene;

public enum ImageState : byte { None = 0, Pending = 1, Ready = 2, Failed = 3 }

/// <summary>Decode urgency (media-pipeline.md §3). Workers drain higher priority first; under backpressure the lowest
/// off-screen lane (Prefetch, then Overscan) is dropped — never <see cref="Visible"/>.</summary>
public enum ImagePriority : byte { Visible = 0, Overscan = 1, Prefetch = 2 }

/// <summary>What <c>UseImage</c> returns (media-pipeline.md §5): the stable handle + current load state, so a component
/// can render a spinner / a broken-art fallback / read the failure kind, all without prop-drilling.</summary>
public readonly record struct ImageBinding(ImageHandle Handle, ImageState State, ImageFailureKind Failure, int Attempts)
{
    public bool IsReady => State == ImageState.Ready;
    public bool IsLoading => State is ImageState.None or ImageState.Pending;
    public bool IsFailed => State == ImageState.Failed;
}

/// <summary>Why an image decode ended (for diagnostics + app fallbacks). Transient kinds are retried by the decoder
/// before a request is reported <see cref="ImageState.Failed"/>; permanent kinds fail immediately.</summary>
public enum ImageFailureKind : byte
{
    None = 0,
    Network = 1,      // transient: connection reset / DNS / socket — retried
    Timeout = 2,      // transient: slow internet exceeded the per-request deadline — retried
    ServerError = 3,  // transient: HTTP 5xx — retried
    NotFound = 4,     // permanent: HTTP 404/410 — not retried
    HttpError = 5,    // permanent: other 4xx — not retried
    Decode = 6,       // bytes fetched but not decodable — retried while visible (stale disk poison / CDN glitch)
    Canceled = 7,     // request was canceled (row recycled / unmounted) before completion
    GpuResourceExhausted = 8, // transient across a later remount: backend could not admit another resident texture/SRV
    GpuUpload = 9,    // permanent for this decode: backend rejected invalid pixels/dimensions
}

/// <summary>GPU admission result for decoded pixels. A decode is Ready only after the backend accepted its synchronous
/// staging copy; a rejected upload must never publish a texture-less Ready handle.</summary>
public enum ImageUploadResult : byte
{
    Accepted = 0,
    ResourceExhausted = 1,
    Invalid = 2,
}

/// <summary>Image lifecycle notification for app observability (e.g. a retry toast, a broken-art fallback, telemetry).
/// Raised on the UI thread during <see cref="ImageCache.Pump"/>. <paramref name="attempts"/> is the fetch attempt count
/// (≥1; &gt;1 means transient retries happened).</summary>
public delegate void ImageStatusHandler(int id, ImageState state, ImageFailureKind failure, int attempts);

/// <summary>A generational-free image handle into the <see cref="ImageCache"/> (0 = none).</summary>
public readonly record struct ImageHandle(int Id)
{
    public bool IsNull => Id == 0;
    public static ImageHandle Null => default;
}

/// <summary>Forwards decoded PREMULTIPLIED BGRA8 pixels to the GPU backend. The span is valid only for the duration of
/// the synchronous call (it is never stored — the backend copies it into its upload heap), so the cache need not own
/// pixel memory: it flows decoder → cache.Pump → host sink → IGpuDevice.UploadImage in one stack.</summary>
public delegate void ImageReadyHandler(int id, System.ReadOnlySpan<byte> bgra8, int w, int h);

/// <summary>Admission-aware variant of <see cref="ImageReadyHandler"/>. The span has the same call-scoped lifetime.</summary>
public delegate ImageUploadResult ImageUploadAttemptHandler(int id, System.ReadOnlySpan<byte> bgra8, int w, int h);

/// <summary>The decode seam: the portable cache asks a leaf to decode a source to a target size, off the UI thread.
/// The Windows leaf is WIC→GPU (needs-pixels); the headless leaf is deterministic. <see cref="Pump"/> drains completions
/// onto the UI thread (the +1-frame latency contract: a request is never ready the same frame), invoking
/// <paramref name="onPixels"/> with the bucket pixels then <paramref name="onComplete"/> with the state transition.</summary>
public interface IImageDecoder
{
    bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible);
    /// <summary>Drain finished decodes. <paramref name="onPixels"/> gets the bucket pixels (transient span);
    /// <paramref name="onComplete"/> gets the final state transition incl. <see cref="ImageFailureKind"/> + attempt count
    /// (after any transient retries the decoder performed internally).</summary>
    void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels);
    /// <summary>Cancel an in-flight (or queued) decode — e.g. a virtualized row recycled off-screen. Idempotent.</summary>
    void Cancel(int id) { }
    /// <summary>Raise the priority of a queued decode (e.g. a prefetch that just scrolled into view). Idempotent; no-op
    /// once the job has started.</summary>
    void Prioritize(int id, ImagePriority priority) { }

    /// <summary>Census (MemCensus): decodes currently in flight on worker threads. Defaults to 0 for decoders that
    /// don't track it (e.g. the synchronous test decoder). O(1).</summary>
    int DiagInflight => 0;
    /// <summary>Census (MemCensus): live entries in the decoder's cancellation map. Defaults to 0. O(1).</summary>
    int DiagCanceledPending => 0;
}

/// <summary>Decode-completion callback: id, success, decoded dims, the failure kind (if any), and the fetch attempt count.</summary>
public delegate void ImageCompleteHandler(int id, bool ok, int w, int h, ImageFailureKind failure, int attempts);

/// <summary>
/// Portable image residency: source→handle dedup, a Pending/Ready/Failed state machine, **liveness ref-counting**
/// (a pinned/on-screen image is never evicted — the single biggest real-world cache bug, per the research), and an
/// LRU byte budget over decoded images. Decode happens off-thread behind <see cref="IImageDecoder"/>; <see cref="Pump"/>
/// applies completions (+1-frame latency). The GPU texture upload is a needs-pixels leaf concern; this owns the logic.
/// </summary>
public sealed class ImageCache
{
    /// <summary>Dedup key: (source, target size) as a value type — a cache HIT allocates nothing (no `$"{src}@{w}x{h}"`
    /// string per request, which mattered: every realized image row calls <see cref="Request"/>).</summary>
    private readonly record struct SourceKey(string Source, int W, int H);

    private sealed class Entry
    {
        public SourceKey Key;
        public ImageState State;
        public int W, H;
        public int Refs;        // liveness: >0 ⇒ on screen ⇒ never evicted
        public long LastUsed;
        public long Bytes;
        public ImageFailureKind Failure;   // why it Failed (None while Pending/Ready)
        public int Attempts;               // fetch attempts the decoder made (>1 ⇒ transient retries occurred)
        public float TextureMs = float.NaN;   // clock (ms) when the FIRST texture (blurhash or full-res) appeared → fade origin
        public ImageTransition Transition;     // the placeholder→image reveal (duration + easing); set at request
        public float LastRestartMs = float.NegativeInfinity;   // backoff gate for visible/transient-failure retries
    }

    const float RestartBackoffMs = 2000f;   // min gap between visible retries on the same handle (avoids hammering a dead URL)

    private readonly Dictionary<SourceKey, int> _byKey = new();
    private readonly Dictionary<int, Entry> _byId = new();
    private readonly IImageDecoder _decoder;
    private readonly long _budgetBytes;
    private readonly ImageCompleteHandler _onComplete;   // cached → Pump allocates nothing
    private readonly ImageReadyHandler _onPixels;         // cached admission bridge → Pump allocates nothing
    private static readonly ImageReadyHandler _noPixels = static (int id, System.ReadOnlySpan<byte> p, int w, int h) => { };
    private static readonly System.Action<int> _noEvict = static _ => { };
    private ImageReadyHandler _pixelSink;
    private ImageUploadAttemptHandler? _pixelAttemptSink;
    private System.Action<int> _evictSink;
    private ImageUploadQueue? _asyncUploads;   // non-null ⇒ async render thread: GPU work is handed off, not called inline
    // IImageDecoder guarantees a successful completion calls onPixels immediately before onComplete for the same id.
    // Remember that one admission result so OnDecodeComplete can fold GPU rejection into the terminal cache state.
    private int _uploadResultId;
    private ImageUploadResult _uploadResult;
    private int _nextId = 1;
    private long _clock = 1;
    private int _pumpCompleted;
    private int _totalRequested, _totalReady, _totalFailed, _totalRetried, _totalEvicted;
    // O(1) maintained mirrors of the former PendingCount / HasActiveCrossfades scans (wake-04): these ran on every
    // HasActiveWork call every frame. _pendingCount tracks State==Pending entries (a miss creates one; OnDecodeComplete
    // resolves it; eviction never removes a Pending entry). _maxCrossfadeDeadlineMs is the high-water MAX of
    // TextureMs+DurationMs over entries with an enabled reveal — HasActiveCrossfades is then exactly (_clockMs < max),
    // since (∃e: clockMs<deadlineₑ) ⟺ (clockMs < maxₑ deadlineₑ). The max only goes stale if a still-fading entry is
    // evicted; the evict path recomputes it then (rare — evicting an unpinned mid-fade image), keeping the getter O(1).
    private int _pendingCount;
    private float _maxCrossfadeDeadlineMs = float.NegativeInfinity;
    private float _clockMs;   // monotonic ms clock for cross-fade timing (advanced by Tick once per painted frame)
    private const int BlurW = 32, BlurH = 32;
    private readonly byte[] _blurScratch = new byte[BlurW * BlurH * 4];   // reused (UI thread only) for LQIP decode

    /// <summary>Raised (UI thread, during <see cref="Pump"/>) when an image reaches a terminal state — Ready or Failed.
    /// Apps subscribe for a broken-art fallback, a retry/offline toast, or telemetry. See also <see cref="FailureOf"/>.</summary>
    public event ImageStatusHandler? ImageStatusChanged;

    public ImageCache(IImageDecoder decoder, long budgetBytes = 96L * 1024 * 1024)
    {
        _decoder = decoder;
        _budgetBytes = budgetBytes;
        _onComplete = OnDecodeComplete;
        _onPixels = OnPixels;
        _pixelSink = _noPixels;
        _evictSink = _noEvict;
    }

    /// <summary>The host wires this to <c>IGpuDevice.UploadImage</c>; the cache forwards each decode's pixels through it
    /// during <see cref="Pump"/> (transiently — the sink must copy, not store). Set once at composition.</summary>
    public void SetPixelSink(ImageReadyHandler sink)
    {
        _pixelSink = sink ?? _noPixels;
        _pixelAttemptSink = null;
    }

    /// <summary>Admission-aware GPU sink. Unlike the legacy <see cref="SetPixelSink"/>, rejection becomes a terminal
    /// image failure instead of publishing a Ready handle with no resident texture.</summary>
    public void SetPixelAttemptSink(ImageUploadAttemptHandler sink)
    {
        _pixelAttemptSink = sink;
        _pixelSink = _noPixels;
    }

    /// <summary>The host wires this to <c>IGpuDevice.EvictImage</c>; the cache calls it when residency evicts an image so
    /// the backend frees the GPU texture. Set once at composition. Under the async render thread the host wires this to
    /// <see cref="ImageUploadQueue.EnqueueEvict"/> instead, so the eviction is drained + freed on the render thread.</summary>
    public void SetEvictSink(System.Action<int> sink) => _evictSink = sink ?? _noEvict;

    /// <summary>ASYNC render thread only (render-thread-seam landing plan §9, Step 1). When set, the pixel/evict sinks
    /// hand GPU work to the render thread via this queue instead of touching the device on the UI thread; an upload is
    /// optimistically admitted <c>Ready</c> and the render thread posts a REJECTION back here, drained each <see cref="Pump"/>
    /// by <see cref="DrainAsyncRejections"/>. Null in default/force-sync (the direct sinks run with no cross-thread overlap).</summary>
    public void SetAsyncUploadQueue(ImageUploadQueue queue) => _asyncUploads = queue;

    public long UsedBytes { get; private set; }
    public int Count => _byId.Count;
    public int ReadyCount { get { int n = 0; foreach (var e in _byId.Values) if (e.State == ImageState.Ready) n++; return n; } }
    public int ContentEpoch { get; private set; }
    /// <summary>Entries still decoding (State==Pending) — O(1) maintained counter (was a per-call scan, wake-04).</summary>
    public int PendingCount => _pendingCount;

    /// <summary>Census (MemCensus): decodes in flight on the backing decoder's workers (0 for non-tracking decoders). O(1).</summary>
    public int DecodeInflight => _decoder.DiagInflight;
    /// <summary>Census (MemCensus): live entries in the backing decoder's cancellation map (0 for non-tracking decoders). O(1).</summary>
    public int DecodeCanceledPending => _decoder.DiagCanceledPending;

    /// <summary>Request (or dedup) a decode of <paramref name="source"/> at a target size; returns a stable handle. On
    /// a cache MISS, an optional <paramref name="blurHash"/> is decoded ONCE into a tiny LQIP texture (uploaded under
    /// this id) so a blurred preview shows instantly until the full-res art lands. A cache HIT returns before any of
    /// this — so re-renders of the same image do no work.</summary>
    public ImageHandle Request(string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible,
                               string? blurHash = null, ImageTransition? transition = null)
    {
        var key = new SourceKey(source, targetW, targetH);
        if (_byKey.TryGetValue(key, out int id))
        {
            var hit = _byId[id];
            hit.LastUsed = _clock++;
            // Evicted entries keep their key as a tombstone so a retained ImageEl node can re-pin the same handle and
            // recover without needing its original URL. Capacity rejection is likewise retryable after all owners release.
            if (ShouldRestart(hit, priority))
                RestartDecode(id, hit, priority);
            // A visible node arriving over a prefetch entry promotes the in-flight decode to the front of the queue.
            if (priority < ImagePriority.Prefetch) _decoder.Prioritize(id, priority);
            return new ImageHandle(id);
        }

        id = _nextId++;
        _byKey[key] = id;
        var entry = new Entry { Key = key, State = ImageState.Pending, LastUsed = _clock++, Transition = transition ?? ImageTransition.Default };
        _byId[id] = entry;
        _pendingCount++;   // a miss always creates a Pending entry; OnDecodeComplete decrements when it resolves
        _totalRequested++;
        Diag.Set("media", "requested", _totalRequested);
        // NB: the decode keeps the requested size; the GPU backend buckets INTERNALLY (pool/atlas) and samples the
        // image's sub-rect of its bucket texture via the inset UV — so residency accounting stays on the real pixels.

        // LQIP: decode the blurhash once and upload it as this image's instant initial texture (replaced by the
        // full-res decode when it lands). Render-edge work, cache-miss only — never per-frame. The reveal fade starts now.
        if (!string.IsNullOrEmpty(blurHash) && (_pixelAttemptSink is not null || _pixelSink != _noPixels)
            && BlurHash.Decode(blurHash, BlurW, BlurH, _blurScratch))
        {
            if (UploadPixels(id, _blurScratch.AsSpan(0, BlurW * BlurH * 4), BlurW, BlurH) == ImageUploadResult.Accepted)
            {
                entry.TextureMs = _clockMs;
                NoteCrossfadeDeadline(entry);
                Diag.Count("media", "blurhash");
            }
        }

        if (!_decoder.Begin(id, source, targetW, targetH, priority))
        {
            _pendingCount--;
            entry.State = ImageState.None;
            entry.Failure = ImageFailureKind.Canceled;
        }
        return new ImageHandle(id);
    }

    /// <summary>Warm the cache for a source the UI is ABOUT to need (the next scroll page) at <see cref="ImagePriority.Prefetch"/>
    /// — decoded ahead, never pinned, so it's instantly Resident when a row scrolls in (Nuke/Coil prefetch). Evictable
    /// until a real node pins it. No-op if already requested.</summary>
    public ImageHandle Prefetch(string source, int targetW, int targetH)
        => Request(source, targetW, targetH, ImagePriority.Prefetch);

    public ImageState StateOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.State : ImageState.None;
    public (int W, int H) SizeOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? (e.W, e.H) : (0, 0);
    /// <summary>Why an image is <see cref="ImageState.Failed"/> (None otherwise) — for app fallbacks / retry UI.</summary>
    public ImageFailureKind FailureOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.Failure : ImageFailureKind.None;
    /// <summary>How many fetch attempts the decoder made (≥1 once resolved; &gt;1 means transient retries happened).</summary>
    public int AttemptsOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.Attempts : 0;
    /// <summary>The source URL bound to a handle (null when unknown).</summary>
    public string? SourceOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.Key.Source : null;

    /// <summary>Cancel an in-flight decode (row recycled / unmounted) — frees worker + network effort under fast scroll.</summary>
    public void Cancel(ImageHandle h) => _decoder.Cancel(h.Id);

    /// <summary>Round a display size up to a decode bucket (64/128/256/512) — the texture-pool / atlas granularity.</summary>
    public static int BucketFor(int px) => px <= 64 ? 64 : px <= 128 ? 128 : px <= 256 ? 256 : 512;

    /// <summary>Advance the cross-fade clock by <paramref name="dtMs"/> (call once per painted frame, before record).</summary>
    public void Tick(float dtMs) => _clockMs += dtMs;

    /// <summary>Placeholder→image reveal progress 0..1, eased by the image's <see cref="ImageTransition"/> from when its
    /// first texture appeared (the BlurHash LQIP, or the full-res decode). 1 once settled, instantly if the transition
    /// is disabled, or if there's no texture yet.</summary>
    public float CrossFadeOf(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e) || float.IsNaN(e.TextureMs)) return 1f;
        return e.Transition.Progress(_clockMs - e.TextureMs);
    }

    /// <summary>True while any image is still revealing — the host keeps painting so the fade animates to completion.
    /// O(1): equivalent to the former per-frame scan (∃ enabled reveal with clockMs &lt; TextureMs+Dur) because that is
    /// exactly (_clockMs &lt; max deadline). See <see cref="_maxCrossfadeDeadlineMs"/> (wake-04).</summary>
    public bool HasActiveCrossfades => _clockMs < _maxCrossfadeDeadlineMs;

    /// <summary>Fold an entry's reveal deadline (TextureMs+Dur) into the high-water max — called wherever TextureMs is
    /// set. Disabled reveals (Dur==0) and NaN TextureMs don't contribute (mirrors the scan's guards exactly).</summary>
    private void NoteCrossfadeDeadline(Entry e)
    {
        if (!e.Transition.Enabled || float.IsNaN(e.TextureMs)) return;
        float deadline = e.TextureMs + e.Transition.DurationMs;
        if (deadline > _maxCrossfadeDeadlineMs) _maxCrossfadeDeadlineMs = deadline;
    }

    /// <summary>Recompute the crossfade-deadline high-water from scratch — used only after evicting an entry that could
    /// still have been the active contributor (its deadline ≥ _clockMs), so the steady path never scans.</summary>
    private void RecomputeCrossfadeDeadline()
    {
        float max = float.NegativeInfinity;
        foreach (var e in _byId.Values)
            if (e.Transition.Enabled && !float.IsNaN(e.TextureMs))
            {
                float d = e.TextureMs + e.Transition.DurationMs;
                if (d > max) max = d;
            }
        _maxCrossfadeDeadlineMs = max;
    }

    /// <summary>Pin = "on screen" (a realized node holds it); never evicted while pinned. Unpin on recycle/unmount.</summary>
    public void Pin(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e)) return;
        e.Refs++;
        e.LastUsed = _clock++;
        if (ShouldRestart(e, ImagePriority.Visible)) RestartDecode(h.Id, e, ImagePriority.Visible);
        else if (e.State == ImageState.Pending) _decoder.Prioritize(h.Id, ImagePriority.Visible);
    }
    public void Unpin(ImageHandle h) { if (_byId.TryGetValue(h.Id, out var e) && e.Refs > 0) e.Refs--; }

    /// <summary>Whether a cache entry should (re)start decoding at <paramref name="priority"/>.</summary>
    static bool ShouldRestart(Entry e, ImagePriority priority)
    {
        if (e.State == ImageState.None) return true;
        if (e.State == ImageState.Failed)
        {
            if (e.Failure == ImageFailureKind.Canceled) return true;
            if (e.Failure == ImageFailureKind.GpuResourceExhausted && e.Refs == 0) return true;
            if (e.Failure == ImageFailureKind.Decode && (e.Refs > 0 || priority == ImagePriority.Visible))
                return true;   // stale disk poison / transient codec miss — backoff applied in RestartDecode
            if (IsTransientFailure(e.Failure) && (e.Refs > 0 || priority == ImagePriority.Visible))
                return true;   // backoff applied in RestartDecode
        }
        return false;
    }

    static bool IsTransientFailure(ImageFailureKind k)
        => k is ImageFailureKind.Network or ImageFailureKind.Timeout or ImageFailureKind.ServerError;
    public int RefsOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.Refs : 0;

    /// <summary>Device-lost recovery (threading-render-seam.md §9): the backend's image textures are gone (the store was
    /// recreated by RecoverDevice). Re-decode every resident (Ready) image from its retained source so it re-uploads
    /// through the Step-1 handoff to the fresh store. UI thread (invoked on the recover-done frame). Ready→Pending, which
    /// keeps the frame loop awake until the re-uploads land. Safe to iterate: Entry is a class (mutated in place, no
    /// structural dictionary change) and RestartDecode only calls the decoder (never adds/removes _byId keys).</summary>
    public void ReRealizeAllResident()
    {
        foreach (var (id, e) in _byId)
            if (e.State == ImageState.Ready)
            {
                UsedBytes -= e.Bytes;   // the texture is gone; RestartDecode re-adds the bytes when the fresh decode completes
                RestartDecode(id, e, e.Refs > 0 ? ImagePriority.Visible : ImagePriority.Prefetch);
            }
    }

    private void RestartDecode(int id, Entry e, ImagePriority priority)
    {
        if (e.State == ImageState.Pending) { _decoder.Prioritize(id, priority); return; }
        if (e.State == ImageState.Failed && (IsTransientFailure(e.Failure) || e.Failure == ImageFailureKind.Decode)
            && _clockMs - e.LastRestartMs < RestartBackoffMs)
            return;
        e.LastRestartMs = _clockMs;
        e.State = ImageState.Pending;
        e.Failure = ImageFailureKind.None;
        e.Attempts = 0;
        e.W = e.H = 0;
        e.Bytes = 0;
        e.TextureMs = float.NaN;
        _pendingCount++;
        _totalRequested++;
        ContentEpoch++;
        Diag.Set("media", "requested", _totalRequested);
        if (!_decoder.Begin(id, e.Key.Source, e.Key.W, e.Key.H, priority))
        {
            _pendingCount--;
            e.State = ImageState.None;
            e.Failure = ImageFailureKind.Canceled;
        }
    }

    /// <summary>Apply finished decodes (UI thread, once per frame) then evict to budget. Returns completions this pump.
    /// Allocation-free when idle (cached callback; empty-queue Pump does nothing) — safe in the hot phase.</summary>
    public int Pump()
    {
        _pumpCompleted = 0;
        if (_asyncUploads is { } q) DrainAsyncRejections(q);   // fold +1-frame async upload rejections before this pump's decodes
        _decoder.Pump(_onComplete, _onPixels);
        if (_pumpCompleted > 0) EvictToBudget();
        return _pumpCompleted;
    }

    // ASYNC only (Step 1): the render thread stages uploads a frame after the UI optimistically admitted them Ready.
    // On rejection (atlas/pool exhaustion) it posts the result here; fold it into the terminal Failed state, undo the
    // optimistic byte accounting, and release any partial placement — the deferred analogue of OnDecodeComplete's
    // synchronous admission-failure path (381-390). Only a still-optimistically-Ready entry is downgraded: if it has
    // since been evicted, re-requested, or already failed, the reject is stale → skip (a rare ABA the sync path shares).
    private void DrainAsyncRejections(ImageUploadQueue q)
    {
        while (q.TryDequeueResult(out var r))
        {
            if (!_byId.TryGetValue(r.Id, out var e) || e.State != ImageState.Ready) continue;
            UsedBytes -= e.Bytes;
            e.Bytes = 0;
            _evictSink(r.Id);   // async ⇒ enqueues an evict job (releases any resident blur-hash/partial placement)
            bool wasActiveDeadline = e.Transition.Enabled && !float.IsNaN(e.TextureMs)
                && e.TextureMs + e.Transition.DurationMs >= _clockMs;
            e.TextureMs = float.NaN;
            e.State = ImageState.Failed;
            e.Failure = r.Result == ImageUploadResult.ResourceExhausted ? ImageFailureKind.GpuResourceExhausted : ImageFailureKind.GpuUpload;
            if (wasActiveDeadline) RecomputeCrossfadeDeadline();
            _totalFailed++;
            ContentEpoch++;
            Diag.Set("media", "failed", _totalFailed);
            ImageStatusChanged?.Invoke(r.Id, e.State, e.Failure, e.Attempts);
        }
    }

    private ImageUploadResult UploadPixels(int id, System.ReadOnlySpan<byte> pixels, int w, int h)
    {
        if (_pixelAttemptSink is { } attempt) return attempt(id, pixels, w, h);
        _pixelSink(id, pixels, w, h);
        return ImageUploadResult.Accepted;
    }

    private void OnPixels(int id, System.ReadOnlySpan<byte> pixels, int w, int h)
    {
        _uploadResultId = id;
        _uploadResult = UploadPixels(id, pixels, w, h);
    }

    private void OnDecodeComplete(int id, bool ok, int w, int h, ImageFailureKind failure, int attempts)
    {
        if (!_byId.TryGetValue(id, out var e)) return;
        bool uploadRejected = false;
        if (ok && _uploadResultId == id && _uploadResult != ImageUploadResult.Accepted)
        {
            uploadRejected = true;
            ok = false;
            failure = _uploadResult == ImageUploadResult.ResourceExhausted
                ? ImageFailureKind.GpuResourceExhausted
                : ImageFailureKind.GpuUpload;
            w = h = 0;
        }
        if (_uploadResultId == id)
        {
            _uploadResultId = 0;
            _uploadResult = ImageUploadResult.Accepted;
        }
        if (uploadRejected)
        {
            // A blur-hash preview may already be resident under this id. Admission failure makes the whole handle
            // non-drawable, so release that partial placement too; otherwise a zero-byte Failed entry leaks one SRV.
            _evictSink(id);
            bool wasActiveDeadline = e.Transition.Enabled && !float.IsNaN(e.TextureMs)
                && e.TextureMs + e.Transition.DurationMs >= _clockMs;
            e.TextureMs = float.NaN;
            if (wasActiveDeadline) RecomputeCrossfadeDeadline();
        }
        if (e.State == ImageState.Pending) _pendingCount--;   // leaving Pending (Ready/Failed) — mirror the former scan
        bool restartVisibleCancel = !ok && failure == ImageFailureKind.Canceled && e.Refs > 0;
        e.State = ok ? ImageState.Ready : ImageState.Failed;
        e.Failure = ok ? ImageFailureKind.None : failure;
        e.Attempts = attempts;
        if (ok && float.IsNaN(e.TextureMs)) { e.TextureMs = _clockMs; NoteCrossfadeDeadline(e); }   // no LQIP → the reveal fade starts when the full-res lands
        e.W = w; e.H = h;
        e.Bytes = ok ? (long)w * h * 4 : 0;
        UsedBytes += e.Bytes;
        _pumpCompleted++;
        ContentEpoch++;

        if (ok) _totalReady++; else _totalFailed++;
        if (attempts > 1) _totalRetried++;
        Diag.Set("media", "ready", _totalReady);
        Diag.Set("media", "failed", _totalFailed);
        Diag.Set("media", "retried", _totalRetried);
        Diag.Set("media", "pending", PendingCount);
        if (restartVisibleCancel)
        {
            RestartDecode(id, e, ImagePriority.Visible);
            Diag.Set("media", "pending", PendingCount);
        }
        else
        {
            ImageStatusChanged?.Invoke(id, e.State, e.Failure, attempts);
        }
    }

    private void EvictToBudget()
    {
        while (UsedBytes > _budgetBytes)
        {
            int victim = 0; long oldest = long.MaxValue;
            foreach (var (id, e) in _byId)
                if (e.Refs == 0 && e.State == ImageState.Ready && e.LastUsed < oldest) { oldest = e.LastUsed; victim = id; }
            if (victim == 0) break;   // everything left is pinned (on screen) — never evict it
            var e2 = _byId[victim];
            UsedBytes -= e2.Bytes;
            bool activeDeadline = e2.Transition.Enabled && !float.IsNaN(e2.TextureMs)
                && e2.TextureMs + e2.Transition.DurationMs >= _clockMs;
            e2.State = ImageState.None;
            e2.Failure = ImageFailureKind.None;
            e2.Attempts = 0;
            e2.W = e2.H = 0;
            e2.Bytes = 0;
            e2.TextureMs = float.NaN;
            // If the evicted entry could still be the crossfade-deadline high-water (an unpinned image whose fade hasn't
            // elapsed), recompute the max so HasActiveCrossfades can't report a stale future deadline. Settled entries
            // (deadline < clock — the overwhelming evict case) skip the recompute, so the steady path stays scan-free.
            if (activeDeadline)
                RecomputeCrossfadeDeadline();
            _totalEvicted++;
            Diag.Set("media", "evicted", _totalEvicted);
            _evictSink(victim);   // free the GPU texture (the device defers the release behind the frame fence)
        }
    }
}

/// <summary>Deterministic headless/offline decoder: completes on the NEXT <see cref="ImageCache.Pump"/> (the +1-frame
/// latency contract), reporting the requested target size and synthesizing a stable per-id BGRA pattern so the GPU
/// upload + sample path is exercisable without real codecs or network. Proves the residency/state logic.</summary>
public sealed class FakeImageDecoder : IImageDecoder
{
    private readonly Queue<(int id, int w, int h)> _pending = new();
    private byte[] _scratch = System.Array.Empty<byte>();

    public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
    {
        _pending.Enqueue((id, targetW <= 0 ? 1 : targetW, targetH <= 0 ? 1 : targetH));
        return true;
    }

    public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
    {
        while (_pending.Count > 0)
        {
            var (id, w, h) = _pending.Dequeue();
            int bytes = w * h * 4;
            if (_scratch.Length < bytes) _scratch = new byte[bytes];
            FillPattern(_scratch, id, w, h);
            onPixels(id, _scratch.AsSpan(0, bytes), w, h);   // premultiplied BGRA (opaque ⇒ premul == straight)
            onComplete(id, true, w, h, ImageFailureKind.None, 1);
        }
    }

    // A stable per-id diagonal gradient with an id-derived hue so each decoded tile looks distinct on screen.
    private static void FillPattern(byte[] buf, int id, int w, int h)
    {
        uint s = unchecked((uint)id * 2654435761u);
        byte br = (byte)(80 + (s & 0x7F)), bg = (byte)(80 + ((s >> 7) & 0x7F)), bb = (byte)(80 + ((s >> 14) & 0x7F));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float t = (x + y) / (float)(w + h);            // 0..1 corner→corner
                buf[i + 0] = (byte)(bb * (0.45f + 0.55f * t));  // B
                buf[i + 1] = (byte)(bg * (0.45f + 0.55f * t));  // G
                buf[i + 2] = (byte)(br * (0.45f + 0.55f * t));  // R
                buf[i + 3] = 255;                                // A (opaque)
            }
        }
    }
}
