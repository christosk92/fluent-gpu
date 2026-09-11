using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Signals;

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
    /// <summary>While true, per-frame GPU upload applies are throttled (scroll-scoped fence-stall guard). Default no-op
    /// for decoders without an upload stage (headless fakes).</summary>
    bool ScrollThrottled { get => false; set { } }
    /// <summary>Number of decoded images applied by the most recent <see cref="Pump"/>.</summary>
    int LastPumpAppliedCount => 0;
    /// <summary>Decoded pixel bytes applied by the most recent <see cref="Pump"/>.</summary>
    int LastPumpAppliedBytes => 0;
    /// <summary>Raise the priority of a queued decode (e.g. a prefetch that just scrolled into view). Idempotent; no-op
    /// once the job has started.</summary>
    void Prioritize(int id, ImagePriority priority) { }

    /// <summary>True when a worker has published at least one completion for the UI thread to drain. Unlike an in-flight
    /// request count, this is actionable work and may keep the frame loop awake.</summary>
    bool HasReadyCompletions => false;
    /// <summary>Install the thread-safe host wake callback invoked after a worker publishes a completion. Decoders that
    /// complete synchronously may ignore it.</summary>
    void SetCompletionWake(System.Action? wake) { }

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
    private readonly record struct DerivedKey(int SourceId, int W, int H, int SigmaQ, int ScaleQ);

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
        // Duration of the CURRENT reveal. Normally Transition.DurationMs; halved for a texture that lands mid-scroll
        // (BeginReveal). Every deadline/progress read uses THIS, so a shortened reveal keeps the wake bookkeeping exact.
        public float RevealMs;
        public float RequestedMs = float.NegativeInfinity;   // clock (ms) when the current decode was requested → the cache-adjacent test
        public float LastRestartMs = float.NegativeInfinity;   // backoff gate for visible/transient-failure retries
        // Sticky: true once this entry has EVER reached Ready. Persists through RestartDecode/eviction (the Entry
        // object survives in _byId — a tombstone keeps its key), so a LATER re-decode of an already-known-good key
        // is recognized as a warm hit even after the original reveal fully settled. See BeginReveal's instant-at-rest
        // arm (set AFTER that call on the decode completing this entry — never before it — so entry's FIRST-ever
        // decode still sees WasReady==false there and keeps its authored fade, even under a synchronous test decoder
        // that lands well inside InstantRevealWindowMs).
        public bool WasReady;
        public bool Derived;
        public int SourceId;
        public int BakeTargetW, BakeTargetH;
        public float BakeSigmaTexels;
        public int BakeGeneration;
        public bool BakeQueued;
        public bool BakeUpgradeQueued;
        public byte BakeUpgradeAttempts;
        public BakedBlurQueue.Quality BakeQuality;
        // Allocated lazily only when a UseImage consumer observes this handle. Terminal state changes bump this signal,
        // so one image completion invalidates only its own observers instead of every unsettled image component.
        public Signal<int>? StatusEpoch;
        // The previous entry (older decode size) of the SAME source — a per-source chain threaded through _sourceHead,
        // linked once on the miss path. ResidentRenditionOf walks it; 0 ends the chain. Entries are never removed from
        // _byId (evicted ones stay as tombstones), so the chain never dangles.
        public int PrevSameSource;
        // Image-swap crossfade (BeginSwap): this entry is some node's OUTGOING texture, drawn opaque under the incoming
        // image until this reveal-clock deadline. Folded into the crossfade deadline so both the UI wake and the render
        // thread's clock-driven presents keep going for the whole swap window.
        public float SwapHoldUntilMs = float.NegativeInfinity;
    }

    const float RestartBackoffMs = 2000f;   // min gap between visible retries on the same handle (avoids hammering a dead URL)
    // Mid-scroll reveals (see SuppressReveals): the fade runs at HALF its authored duration — short enough not to trail
    // behind moving content, long enough that a cover landing under the cursor doesn't hard-pop. A texture that lands
    // within InstantRevealWindowMs of its request is cache-adjacent (a disk/OS-cache hit): it gets ShortRevealMs — a
    // quick fade over the one placeholder frame it still had, never an instant pop.
    const float ScrollRevealScale = 0.5f;
    const float InstantRevealWindowMs = 100f;
    /// <summary>The reveal a cache-adjacent landing gets (a warm re-decode, a disk/OS-cache hit inside
    /// <see cref="InstantRevealWindowMs"/>): short, never instant. Such a landing still had a PRESENTED placeholder frame
    /// — the +1-frame latency contract guarantees at least one — so snapping the texture in reads as a pop. Coil and
    /// Glide draw the same line: every result that did not come synchronously out of the MEMORY cache crossfades
    /// (Coil's CrossfadeTransition / Glide's DrawableCrossFadeFactory both skip only DataSource.MEMORY_CACHE). The
    /// engine's memory hit is <see cref="Request"/> returning an already-Ready entry, which never re-runs a reveal.</summary>
    public const float ShortRevealMs = 120f;
    /// <summary>The crossfade a node gets when its displayed texture is replaced by a DIFFERENT image (a new track's
    /// cover, a different CDN rendition) — the reconciler's hold-last-good keeps the old texture until the new one is
    /// decoded, then this dissolve replaces the old hard cut. Short on purpose: it bridges two real pictures, it is not
    /// a reveal. The same image at a new decode size still hard-cuts (nothing visibly changes).</summary>
    public const float SwapCrossfadeMs = 150f;
    /// <summary>Curve of the INCOMING half of a swap crossfade (the outgoing half is <see cref="SwapOutgoingEasing"/>).</summary>
    public const Easing SwapCrossfadeEasing = Easing.EaseInOut;
    /// <summary>Fade-easing sentinel for the OUTGOING texture of a swap crossfade, resolved by <see cref="ResolveFade"/>:
    /// fully opaque for the whole window, then gone — the incoming image fades in OVER it. Glide's
    /// DrawableCrossFadeFactory default (setCrossFadeEnabled(false)) for the same reason: fading the outgoing picture
    /// out while the new one fades in lets whatever sits behind the image (a placeholder tile, the page) show through
    /// mid-dissolve — a dip for exactly the opaque artwork this exists for. Not an <see cref="Easing"/> value.</summary>
    public const int SwapOutgoingEasing = -1;

    private readonly Dictionary<SourceKey, int> _byKey = new();
    // Source → the newest entry of that source (any decode size); older sizes chain through Entry.PrevSameSource.
    // Written only on the miss path (which already allocates the entry), so hits and frames stay allocation-free.
    private readonly Dictionary<string, int> _sourceHead = new(StringComparer.Ordinal);
    private readonly Dictionary<DerivedKey, int> _byDerivedKey = new();
    private readonly Dictionary<int, Entry> _byId = new();
    private readonly Dictionary<int, List<int>> _derivedBySource = new();
    private readonly IImageDecoder _decoder;
    private readonly long _budgetBytes;
    // Soft cap on derived/blur bytes, so blur-hash previews retire faster on a weak tier instead of padding the small
    // LOCAL segment (adreno-hang-fixes.md M5). PASSED IN, never read from GpuProfile here: this is a field initializer,
    // so it ran at construction — and the claim that used to sit on this line, that "the backend has published
    // GpuProfile.Tier by the time the cache is constructed", was simply false. The device is brought up lazily by the
    // first CreateSwapchain, which the AppHost constructor makes AFTER the host has built this cache, so Tier was still
    // Unknown (= not weak) and every UMA machine silently got the 16MB discrete cap. The host now decides the tier
    // once, through GpuMemoryBudgets.For, and hands the answer down.
    private readonly long DerivedSoftBudgetBytes;
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
    private BakedBlurQueue? _bakedBlurs;
    private int _uploadResultId;
    private ImageUploadResult _uploadResult;
    private int _nextId = 1;
    private long _clock = 1;
    private int _pumpCompleted;
    private int _totalRequested, _totalReady, _totalFailed, _totalRetried, _totalEvicted;
    private int _totalBakeQueued, _totalBakeReady, _totalBakeFailed, _totalBakeStale;
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

    // ── image-pipeline trace (DIAGNOSTIC ONLY) ───────────────────────────────────────────────────────────────────
    // Turn on with FG_DIAG=1 (Diag.Enabled); narrow with FG_IMG_TRACE=<substring of the source url>. Answers the
    // questions you cannot answer from pixels: did the SOURCE change (same art, different CDN size hash), did the
    // requested DECODE size change, was the entry resident when a node re-pinned it, or did it restart/evict.
    // Every helper here is called ONLY from inside a `Diag.CompiledIn && Diag.Enabled` guard, so a Release build (the
    // whole Diag.Event call site is [Conditional]-erased) AND a Debug diag-off run both allocate nothing — the
    // VerticalSlice zero-alloc gates run Debug with FG_DIAG unset.
    private static readonly string? DiagTraceFilter = ReadTraceFilter();

    static string? ReadTraceFilter()
    {
        // Const-folded away in Release; the read happens once, at ImageCache type init (host setup, never a frame phase).
        string? v = Diag.CompiledIn ? System.Environment.GetEnvironmentVariable("FG_IMG_TRACE") : null;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>FG_IMG_TRACE gate: unset ⇒ trace everything; set ⇒ only sources CONTAINING it (case-insensitive).</summary>
    public static bool DiagTraced(string? source)
        => DiagTraceFilter is null
        || (source is not null && source.Contains(DiagTraceFilter, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The short, greppable identity of an image source: the last 24 chars of its final path segment (for a
    /// 40-hex Spotify image id that is exactly the size-independent art identity — the first 16 are the size/kind
    /// marker). "-" for an empty source, the whole tail when it is shorter than 24.</summary>
    public static string DiagSourceTail(string? source)
    {
        if (string.IsNullOrEmpty(source)) return "-";
        int slash = source.LastIndexOf('/');
        string seg = slash >= 0 && slash + 1 < source.Length ? source[(slash + 1)..] : source;
        return seg.Length > 24 ? seg[^24..] : seg;
    }

    /// <summary>Raised (UI thread, during <see cref="Pump"/>) when an image reaches a terminal state — Ready or Failed.
    /// Apps subscribe for a broken-art fallback, a retry/offline toast, or telemetry. See also <see cref="FailureOf"/>.</summary>
    public event ImageStatusHandler? ImageStatusChanged;

    /// <param name="derivedSoftBudgetBytes">Soft cap on derived/blur bytes; 0 ⇒ the discrete default. Supplied by the
    /// host from <see cref="GpuMemoryBudgets"/> rather than read from <c>GpuProfile</c> here — see the field.</param>
    public ImageCache(IImageDecoder decoder, long budgetBytes = 96L * 1024 * 1024, long derivedSoftBudgetBytes = 0)
    {
        _decoder = decoder;
        _budgetBytes = budgetBytes;
        DerivedSoftBudgetBytes = derivedSoftBudgetBytes > 0 ? derivedSoftBudgetBytes : GpuMemoryBudgets.DerivedDefault;
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
    internal ImageUploadQueue? RecordingUploadQueue => _asyncUploads;

    /// <summary>Install the render-thread handoff used by <see cref="RequestBakedBlur"/>. Set once by the host.</summary>
    public void SetBakedBlurQueue(BakedBlurQueue queue) => _bakedBlurs = queue;

    public long UsedBytes { get; private set; }
    public long DerivedUsedBytes { get; private set; }
    /// <summary>The derived/blur soft cap this cache was built with — the budget the host chose for the tier, exposed
    /// so a headless gate can prove the cache honoured what it was handed instead of re-deriving one from a global
    /// that is always false outside a real device.</summary>
    public long DerivedBudgetBytes => DerivedSoftBudgetBytes;
    public int Count => _byId.Count;
    public int ReadyCount { get { int n = 0; foreach (var e in _byId.Values) if (e.State == ImageState.Ready) n++; return n; } }
    public int ContentEpoch { get; private set; }
    /// <summary>Entries still decoding (State==Pending) — O(1) maintained counter (was a per-call scan, wake-04).</summary>
    public int PendingCount => _pendingCount;
    /// <summary>True when decoded results are ready to apply on the UI thread. In-flight network/decode work does not
    /// require frame polling because its completion wakes the host.</summary>
    public bool HasReadyCompletions => _decoder.HasReadyCompletions || (_bakedBlurs?.HasResults ?? false);

    /// <summary>Wire the decoder's cross-thread completion notification to the platform message loop.</summary>
    public void SetCompletionWake(System.Action? wake) => _decoder.SetCompletionWake(wake);

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
            bool restart = ShouldRestart(hit, priority);
            if (Diag.CompiledIn && Diag.Enabled && DiagTraced(source))
                Diag.Event("img", $"request {(restart ? "restart" : "hit")} id={id} src={DiagSourceTail(source)} " +
                    $"decode={targetW}x{targetH} prio={priority} state={hit.State} fail={hit.Failure} " +
                    $"refs={hit.Refs} tex={(float.IsNaN(hit.TextureMs) ? 0 : 1)}");
            if (restart)
                RestartDecode(id, hit, priority);
            // A visible node arriving over a prefetch entry promotes the in-flight decode to the front of the queue.
            if (priority < ImagePriority.Prefetch) _decoder.Prioritize(id, priority);
            return new ImageHandle(id);
        }

        id = _nextId++;
        _byKey[key] = id;
        var entry = new Entry { Key = key, State = ImageState.Pending, LastUsed = _clock++, RequestedMs = _clockMs, Transition = transition ?? ImageTransition.Default };
        _byId[id] = entry;
        // Thread the new decode size onto its source's chain (ResidentRenditionOf) — once per key, on this miss path.
        entry.PrevSameSource = _sourceHead.TryGetValue(source, out int sameSourceHead) ? sameSourceHead : 0;
        _sourceHead[source] = id;
        _pendingCount++;   // a miss always creates a Pending entry; OnDecodeComplete decrements when it resolves
        _totalRequested++;
        Diag.Set("media", "requested", _totalRequested);
        if (Diag.CompiledIn && Diag.Enabled && DiagTraced(source))
            Diag.Event("img", $"request miss id={id} src={DiagSourceTail(source)} decode={targetW}x{targetH} " +
                $"prio={priority} blurhash={(string.IsNullOrEmpty(blurHash) ? 0 : 1)}");
        // NB: the decode keeps the requested size; the GPU backend buckets INTERNALLY (pool/atlas) and samples the
        // image's sub-rect of its bucket texture via the inset UV — so residency accounting stays on the real pixels.

        // LQIP: decode the blurhash once and upload it as this image's instant initial texture (replaced by the
        // full-res decode when it lands). Render-edge work, cache-miss only — never per-frame. The reveal fade starts now.
        if (!string.IsNullOrEmpty(blurHash) && (_pixelAttemptSink is not null || _pixelSink != _noPixels)
            && BlurHash.Decode(blurHash, BlurW, BlurH, _blurScratch))
        {
            if (UploadPixels(id, _blurScratch.AsSpan(0, BlurW * BlurH * 4), BlurW, BlurH) == ImageUploadResult.Accepted)
            {
                BeginReveal(entry, id, "blurhash");
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

    /// <summary>Request a persistent blurred derivative of an existing source handle. The key is local to the source
    /// pixels and deliberately excludes viewport position, clipping, focus, overlay, and masking.</summary>
    public ImageHandle RequestBakedBlur(ImageHandle source, int targetW, int targetH, in BakedBlurSpec spec,
                                        ImageTransition? transition = null)
    {
        if (source.IsNull || spec.IsNone || !_byId.ContainsKey(source.Id)) return ImageHandle.Null;
        float scale = spec.ClampedResolutionScale;
        int sourceW = targetW > 0 ? targetW : 512;
        int sourceH = targetH > 0 ? targetH : sourceW;
        int outW = Math.Max(1, BucketFor((int)MathF.Ceiling(sourceW * scale)));
        int outH = Math.Max(1, BucketFor((int)MathF.Ceiling(sourceH * scale)));
        int sigmaQ = Math.Max(1, (int)MathF.Round(spec.SigmaDip * scale * 4f));
        int scaleQ = Math.Max(1, (int)MathF.Round(scale * 16f));
        var key = new DerivedKey(source.Id, outW, outH, sigmaQ, scaleQ);
        if (_byDerivedKey.TryGetValue(key, out int existing))
        {
            var hit = _byId[existing];
            hit.LastUsed = _clock++;
            if (hit.State is ImageState.None or ImageState.Failed) RestartDerived(existing, hit);
            return new ImageHandle(existing);
        }

        int id = _nextId++;
        var entry = new Entry
        {
            Derived = true,
            SourceId = source.Id,
            State = ImageState.Pending,
            W = outW,
            H = outH,
            BakeTargetW = outW,
            BakeTargetH = outH,
            BakeSigmaTexels = sigmaQ * 0.25f,
            BakeGeneration = 1,
            BakeQuality = BakedBlurQueue.Quality.Minimal,
            LastUsed = _clock++,
            RequestedMs = _clockMs,
            Transition = transition ?? ImageTransition.Default,
        };
        _byDerivedKey[key] = id;
        _byId[id] = entry;
        if (!_derivedBySource.TryGetValue(source.Id, out var dependents))
        {
            dependents = new List<int>(1);
            _derivedBySource[source.Id] = dependents;
        }
        dependents.Add(id);
        _pendingCount++;
        TryQueueDerived(id, entry);
        return new ImageHandle(id);
    }

    public ImageState StateOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.State : ImageState.None;

    internal void CopyRecordingInputs(ImageRecordingSnapshot target)
    {
        foreach (var pair in _byId)
        {
            var entry = pair.Value;
            target.Add(pair.Key, entry.State, entry.W, entry.H, entry.TextureMs,
                entry.RevealMs, (int)entry.Transition.Easing, entry.SwapHoldUntilMs);
        }
    }

    /// <summary>Narrowed variant (perf plan item 1): copies only the requested ids instead of every entry — O(referenced)
    /// instead of O(total images ever seen this session), so evicted/off-screen tombstones (kept for re-pin recovery,
    /// see <see cref="Entry"/>'s remarks) never cost a snapshot slot. An id in <paramref name="ids"/> the cache no
    /// longer knows (or never knew) is simply skipped.</summary>
    internal void CopyRecordingInputs(ImageRecordingSnapshot target, System.ReadOnlySpan<int> ids)
    {
        foreach (int id in ids)
        {
            if (_byId.TryGetValue(id, out var entry))
                target.Add(id, entry.State, entry.W, entry.H, entry.TextureMs, entry.RevealMs, (int)entry.Transition.Easing,
                    entry.SwapHoldUntilMs);
        }
    }
    /// <summary>Per-handle status epoch used by <c>UseImage</c>. Lazily allocated on first observation; null for an
    /// unknown handle. Unlike the legacy host-wide epoch, a completion wakes only consumers of this cache entry.</summary>
    public IReadSignal<int>? StatusSignalOf(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e)) return null;
        return e.StatusEpoch ??= new Signal<int>(0);
    }
    public (int W, int H) SizeOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? (e.W, e.H) : (0, 0);
    /// <summary>Why an image is <see cref="ImageState.Failed"/> (None otherwise) — for app fallbacks / retry UI.</summary>
    public ImageFailureKind FailureOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.Failure : ImageFailureKind.None;
    /// <summary>How many fetch attempts the decoder made (≥1 once resolved; &gt;1 means transient retries happened).</summary>
    public int AttemptsOf(ImageHandle h) => _byId.TryGetValue(h.Id, out var e) ? e.Attempts : 0;

    private static void NotifyStatus(Entry e)
    {
        if (e.StatusEpoch is { } epoch)
            epoch.Value = epoch.Peek() + 1;
    }
    /// <summary>The source URL bound to a handle (null when unknown).</summary>
    public string? SourceOf(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e)) return null;
        if (!e.Derived) return e.Key.Source;
        return _byId.TryGetValue(e.SourceId, out var sourceEntry) ? sourceEntry.Key.Source : null;
    }

    /// <summary>Cancel an in-flight decode (row recycled / unmounted) — frees worker + network effort under fast scroll.</summary>
    public void Cancel(ImageHandle h)
    {
        if (_byId.TryGetValue(h.Id, out var e) && !e.Derived)
        {
            if (Diag.CompiledIn && Diag.Enabled && DiagTraced(e.Key.Source))
                Diag.Event("img", $"cancel id={h.Id} src={DiagSourceTail(e.Key.Source)} state={e.State} refs={e.Refs}");
            _decoder.Cancel(h.Id);
        }
    }

    /// <summary>Round a display size up to a decode bucket (64/128/256/512) — the texture-pool / atlas granularity.</summary>
    public static int BucketFor(int px) => px <= 64 ? 64 : px <= 128 ? 128 : px <= 256 ? 256 : 512;

    /// <summary>What a decoded image actually COSTS on the GPU, which is not <c>w × h × 4</c>.
    /// <para>The texture store rounds every image up to a square bucket (<see cref="BucketFor"/>) and commits a
    /// resource of that size, then the driver rounds that up again to the 64 KiB placement granularity. So a 150 px
    /// cover charged 90 000 B of decoded pixels and committed 262 144 B — 2.9× — and a 140 px one 3.3×. Budgeting
    /// against the decoded figure meant the cache believed it was holding its 24 MB cap while the GPU held ~84 MB,
    /// which is the whole of the "88 MB of album art" this was measured at. The two numbers diverging is exactly what
    /// <c>ImageTextureStore</c>'s own comment warns about; this is the cache's side of that agreement.</para>
    /// <para>Approximate in one direction only: the row pitch is 256-aligned by D3D12, which for every bucket width
    /// (64/128/256/512 × 4 B = 256/512/1024/2048) is already exact, so the only images this can under-state are the
    /// oversize ones the store commits at their true size — and those are few and already large.</para></summary>
    public static long CommittedBytesFor(int w, int h)
    {
        int edge = Math.Max(w, h);
        long pixels = edge <= 512
            ? (long)BucketFor(edge) * BucketFor(edge) * 4        // the square bucket the store actually creates
            : (long)Math.Max(1, w) * Math.Max(1, h) * 4;         // oversize: committed at its own size
        const long Placement = 64L * 1024;
        return (pixels + Placement - 1) / Placement * Placement;
    }

    /// <summary>Advance the cross-fade clock by <paramref name="dtMs"/> (call once per painted frame, before record).</summary>
    public void Tick(float dtMs) => _clockMs += dtMs;

    /// <summary>Monotonic reveal clock (ms) — passed to the GPU replay path to resolve fade params baked into DrawImageCmd.</summary>
    public float ClockMs => _clockMs;

    /// <summary>While true (scroll), a newly arriving texture reveals at HALF its authored duration instead of the full
    /// fade — or with <see cref="ShortRevealMs"/> when it landed within <see cref="InstantRevealWindowMs"/> of its
    /// request (a cache-adjacent hit). See <see cref="BeginReveal"/>: this used to finish EVERY mid-scroll reveal
    /// instantly, which reads as a pop now that full-size covers actually land during the gesture.</summary>
    public bool SuppressReveals { get; set; }

    /// <summary>While true, per-frame GPU texture uploads are throttled (see <see cref="DecodeScheduler.ScrollThrottled"/>)
    /// — set alongside <see cref="SuppressReveals"/> during scroll so upload bursts can't feed the present fence stall.</summary>
    public bool ScrollThrottled { get => _decoder.ScrollThrottled; set => _decoder.ScrollThrottled = value; }
    public int LastPumpAppliedCount => _decoder.LastPumpAppliedCount;
    public int LastPumpAppliedBytes => _decoder.LastPumpAppliedBytes;

    /// <summary>Bake-time fade params for <see cref="DrawImageCmd"/>. False when no texture has landed yet.</summary>
    public bool FadeParamsOf(ImageHandle h, out float startMs, out float durationMs, out int easing)
    {
        if (!_byId.TryGetValue(h.Id, out var e) || float.IsNaN(e.TextureMs))
        {
            startMs = float.NaN;
            durationMs = 0f;
            easing = 0;
            return false;
        }
        startMs = e.TextureMs;
        durationMs = e.RevealMs;              // the CURRENT reveal's length (halved for a mid-scroll landing)
        easing = (int)e.Transition.Easing;
        return true;
    }

    /// <summary>Resolve a baked fade to 0..1 at replay time (shared by D3D12 + headless).</summary>
    public static float ResolveFade(float imageClockMs, float fadeStartMs, float fadeDurationMs, int fadeEasing)
    {
        // The OUTGOING half of a swap crossfade: opaque until the window ends, then gone (see SwapOutgoingEasing). A
        // missing/zero window resolves to gone, never to a stuck opaque copy under the incoming image.
        if (fadeEasing == SwapOutgoingEasing)
            return fadeDurationMs > 0f && !float.IsNaN(fadeStartMs) && imageClockMs - fadeStartMs < fadeDurationMs ? 1f : 0f;
        if (fadeDurationMs <= 0f || float.IsNaN(fadeStartMs)) return 1f;
        float elapsed = imageClockMs - fadeStartMs;
        if (elapsed <= 0f) return 0f;
        float t = elapsed / fadeDurationMs;
        if (t >= 1f) return 1f;
        return Easings.Ease((Easing)fadeEasing, t);
    }

    // `id` + `cause` are carried for the trace only (a reveal is exactly the "flashed back in" the user sees, so which
    // texture started it — the blurhash LQIP, the full decode, a baked derivative — is the whole question).
    void BeginReveal(Entry e, int id, string cause)
    {
        if (Diag.CompiledIn && Diag.Enabled && DiagTraced(e.Key.Source))
            Diag.Event("img", $"reveal id={id} src={DiagSourceTail(e.Key.Source)} cause={cause} " +
                $"enabled={(e.Transition.Enabled ? 1 : 0)} revealMs={e.Transition.DurationMs:0.#} " +
                $"sinceRequestMs={(float.IsInfinity(e.RequestedMs) ? -1f : _clockMs - e.RequestedMs):0.#} " +
                $"suppress={(SuppressReveals ? 1 : 0)} " +
                $"size={e.W}x{e.H} derived={(e.Derived ? 1 : 0)}");
        if (!e.Transition.Enabled)
        {
            e.TextureMs = float.NaN;
            e.RevealMs = 0f;
            return;
        }
        e.RevealMs = e.Transition.DurationMs;
        e.TextureMs = _clockMs;
        if (_clockMs - e.RequestedMs <= InstantRevealWindowMs && (e.WasReady || SuppressReveals))
        {
            // A WARM landing — a re-decode of a key that has been Ready before (RestartDecode after eviction/failure, a
            // device-lost ReRealizeAllResident, a disk/OS-cache hit), or a cache-adjacent landing mid-scroll — gets a
            // SHORT fade, no longer an instant one. The instant arm assumed "no visible placeholder period", but the
            // +1-frame latency contract means the placeholder WAS presented for at least one frame, so snapping the
            // picture in over it read as a pop (the thumbnail pops of the 2026-09 visual-continuity audit). Only a
            // synchronous memory hit (Request returning a Ready entry — no reveal at all) skips the fade; see
            // ShortRevealMs for the Coil/Glide rule. A first-ever decode at rest keeps its authored fade however fast
            // the (possibly synchronous test) decoder lands — WasReady is false there.
            e.RevealMs = MathF.Min(e.RevealMs, ShortRevealMs);
        }
        else if (SuppressReveals)
        {
            // Scroll. Textures now LAND mid-gesture (DecodeScheduler admits one completion per frame whatever its
            // size), so the old "already finished" reveal would hard-pop a full-size cover in under a moving finger —
            // more visible than the fade it was avoiding. Give it a half-length fade instead.
            e.RevealMs *= ScrollRevealScale;
        }
        NoteCrossfadeDeadline(e);
    }

    /// <summary>Placeholder→image reveal progress 0..1, eased by the image's <see cref="ImageTransition"/> from when its
    /// first texture appeared (the BlurHash LQIP, or the full-res decode). 1 once settled, instantly if the transition
    /// is disabled, or if there's no texture yet.</summary>
    public float CrossFadeOf(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e) || float.IsNaN(e.TextureMs)) return 1f;
        return ResolveFade(_clockMs, e.TextureMs, e.RevealMs, (int)e.Transition.Easing);
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
        float deadline = e.TextureMs + e.RevealMs;
        if (deadline > _maxCrossfadeDeadlineMs) _maxCrossfadeDeadlineMs = deadline;
    }

    /// <summary>Force an entry's placeholder→image reveal to its settled (<see cref="CrossFadeOf"/>==1) state — used by
    /// the reconciler's hold-last-good commit (Reconciler.cs §hold-last-good: a re-keyed Image node held its OLD Ready
    /// texture while the new key decoded). The node owns that transition — a hard cut for the same picture at a sharper
    /// size, a <see cref="BeginSwap"/> dissolve for a different one — so the entry must not also restart a fade from the
    /// placeholder. Exactly the disabled-transition arm of <see cref="BeginReveal"/>.
    /// <para>Entries are shared by (source, decodeW, decodeH): settling one also instantly finishes any OTHER node's
    /// still-fading reveal of the same key. Rare (two nodes landing on the identical key at once) and benign (a fade
    /// cut a few ms early for the other node).</para></summary>
    public void SettleReveal(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e)) return;
        bool wasActiveDeadline = e.Transition.Enabled && !float.IsNaN(e.TextureMs) && e.TextureMs + e.RevealMs >= _clockMs;
        e.TextureMs = float.NaN;
        e.RevealMs = 0f;
        if (wasActiveDeadline) RecomputeCrossfadeDeadline();
    }

    /// <summary>Recompute the crossfade-deadline high-water from scratch — used only after evicting an entry that could
    /// still have been the active contributor (its deadline ≥ _clockMs), so the steady path never scans.</summary>
    private void RecomputeCrossfadeDeadline()
    {
        float max = float.NegativeInfinity;
        foreach (var e in _byId.Values)
        {
            if (e.Transition.Enabled && !float.IsNaN(e.TextureMs))
            {
                float d = e.TextureMs + e.RevealMs;
                if (d > max) max = d;
            }
            if (e.SwapHoldUntilMs > max) max = e.SwapHoldUntilMs;   // a live swap window keeps the fade clock running too
        }
        _maxCrossfadeDeadlineMs = max;
    }

    /// <summary>Start an image-swap crossfade window of <paramref name="durationMs"/> on this cache's reveal clock and
    /// return its start. <paramref name="outgoing"/> is the texture a node keeps drawing OPAQUE under the incoming image
    /// for the window (<see cref="SwapOutgoingEasing"/>); its deadline is folded into the crossfade high-water so the UI
    /// wake (<see cref="HasActiveCrossfades"/>) and the render thread's clock-driven presents
    /// (<see cref="ImageRecordingSnapshot"/>) both run the dissolve to completion. The caller keeps the outgoing
    /// PINNED for the window — pinning is the reconciler's bookkeeping, not the cache's.</summary>
    public float BeginSwap(ImageHandle outgoing, float durationMs)
    {
        float start = _clockMs;
        float until = start + MathF.Max(0f, durationMs);
        if (_byId.TryGetValue(outgoing.Id, out var e) && until > e.SwapHoldUntilMs) e.SwapHoldUntilMs = until;
        if (until > _maxCrossfadeDeadlineMs) _maxCrossfadeDeadlineMs = until;
        return start;
    }

    /// <summary>True when two handles draw the SAME image — the same source (a derivative counts as its source), only
    /// possibly at another decode size. Swapping between such renditions is a hard cut (nothing visibly changes but
    /// sharpness); a different source is a new picture and crossfades (<see cref="SwapCrossfadeMs"/>).</summary>
    public bool SameSource(ImageHandle a, ImageHandle b)
    {
        string? sa = SourceOf(a), sb = SourceOf(b);
        return sa is not null && string.Equals(sa, sb, StringComparison.Ordinal);
    }

    /// <summary>The best RESIDENT rendition of <paramref name="h"/>'s source at another decode size — the largest
    /// Ready, non-derived entry on its source chain — or <see cref="ImageHandle.Null"/>. What a node shows instead of a
    /// placeholder while <paramref name="h"/> itself decodes: the same picture, resampled, is always a better stand-in
    /// than a grey tile. The reconciler holds it exactly like hold-last-good (it covers the MOUNT case hold-last-good
    /// cannot — a remounted cover whose new decode size is not resident yet). Browsers keep showing the current
    /// rendition until a new srcset candidate has loaded; Coil's placeholderMemoryCacheKey hands a list thumbnail to
    /// the detail image the same way. O(decode sizes of one source), allocation-free.</summary>
    public ImageHandle ResidentRenditionOf(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e) || e.Derived || !_sourceHead.TryGetValue(e.Key.Source, out int id))
            return ImageHandle.Null;
        int best = 0;
        long bestArea = 0;
        for (int guard = 0; id != 0 && guard < 64; guard++)
        {
            if (!_byId.TryGetValue(id, out var r)) break;
            if (id != h.Id && !r.Derived && r.State == ImageState.Ready && (long)r.W * r.H > bestArea)
            {
                best = id;
                bestArea = (long)r.W * r.H;
            }
            id = r.PrevSameSource;
        }
        return new ImageHandle(best);
    }

    /// <summary>Pin = "on screen" (a realized node holds it); never evicted while pinned. Unpin on recycle/unmount.</summary>
    public void Pin(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e)) return;
        e.Refs++;
        e.LastUsed = _clock++;
        // H3: "the node remounted and its cache entry was NOT resident" is exactly `state=None` (evicted tombstone) or
        // `state=Failed` here — a re-pin that has to re-decode is a blank cover for at least one frame.
        if (Diag.CompiledIn && Diag.Enabled && DiagTraced(e.Key.Source))
            Diag.Event("img", $"pin id={h.Id} src={DiagSourceTail(e.Key.Source)} refs={e.Refs} state={e.State} " +
                $"fail={e.Failure} tex={(float.IsNaN(e.TextureMs) ? 0 : 1)} derived={(e.Derived ? 1 : 0)}");
        if (e.Derived)
        {
            if (e.State is ImageState.None or ImageState.Failed) RestartDerived(h.Id, e);
            else if (e.State == ImageState.Pending) TryQueueDerived(h.Id, e);
            else if (e.State == ImageState.Ready) TryQueueDerivedUpgrade(h.Id, e);
        }
        else if (ShouldRestart(e, ImagePriority.Visible)) RestartDecode(h.Id, e, ImagePriority.Visible);
        else if (e.State == ImageState.Pending) _decoder.Prioritize(h.Id, ImagePriority.Visible);
    }
    public void Unpin(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e) || e.Refs <= 0) return;
        if (--e.Refs == 0 && e.Derived) e.BakeUpgradeAttempts = 0;
        if (Diag.CompiledIn && Diag.Enabled && DiagTraced(e.Key.Source))
            Diag.Event("img", $"unpin id={h.Id} src={DiagSourceTail(e.Key.Source)} refs={e.Refs} state={e.State}");
    }

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
    /// structural dictionary change) and RestartDecode only calls the decoder (never adds/removes _byId keys).
    /// <para>Also restarts <see cref="ImageFailureKind.GpuResourceExhausted"/> entries. The dying device's last drain
    /// REJECTS every upload it was handed (the store soft-fails its creates rather than throwing past the render seam),
    /// so a device-loss window leaves a batch of entries Failed-exhausted, not Ready — without this they would stay blank
    /// until something remounted them, and a PINNED one never retries at all (ShouldRestart requires Refs == 0). The
    /// rebuilt device has a fresh, empty texture store, so "GPU could not admit it" is by construction no longer true.
    /// Their Bytes are already 0, so the byte-accounting undo is a no-op for them.</para></summary>
    public void ReRealizeAllResident()
    {
        foreach (var (id, e) in _byId)
            if (!e.Derived && (e.State == ImageState.Ready || IsGpuExhausted(e)))
            {
                UsedBytes -= e.Bytes;   // the texture is gone; RestartDecode re-adds the bytes when the fresh decode completes
                RestartDecode(id, e, e.Refs > 0 ? ImagePriority.Visible : ImagePriority.Prefetch);
                NotifyStatus(e);
            }
        foreach (var (id, e) in _byId)
            if (e.Derived && (e.State == ImageState.Ready || IsGpuExhausted(e)))
            {
                UsedBytes -= e.Bytes;
                DerivedUsedBytes -= e.Bytes;
                RestartDerived(id, e);
                NotifyStatus(e);
            }
    }

    static bool IsGpuExhausted(Entry e)
        => e.State == ImageState.Failed && e.Failure == ImageFailureKind.GpuResourceExhausted;

    private void RestartDerived(int id, Entry e)
    {
        if (!e.Derived || e.State == ImageState.Pending) { TryQueueDerived(id, e); return; }
        // Back off a FAILED bake exactly like RestartDecode's visible-retry gate (same LastRestartMs/RestartBackoffMs
        // idiom). A bake that keeps failing — atlas pressure, a source evicted under memory pressure — used to be
        // re-enqueued UNBOUNDED on every RequestBakedBlur / source-ready pass, which is one way the baked-blur queue
        // ends up permanently full. Upgrades already cap at 3 attempts (TryQueueDerivedUpgrade); initial bakes had no
        // limiter at all. Re-baking a READY derivative (the retheme/evict path) is unaffected — only Failed is gated.
        if (e.State == ImageState.Failed && _clockMs - e.LastRestartMs < RestartBackoffMs) return;
        e.LastRestartMs = _clockMs;
        e.RequestedMs = _clockMs;
        e.State = ImageState.Pending;
        e.Failure = ImageFailureKind.None;
        e.Bytes = 0;
        e.TextureMs = float.NaN;
        e.BakeQueued = false;
        e.BakeUpgradeQueued = false;
        e.BakeUpgradeAttempts = 0;
        e.BakeQuality = BakedBlurQueue.Quality.Minimal;
        e.BakeGeneration++;
        _pendingCount++;
        ContentEpoch++;
        TryQueueDerived(id, e);
    }

    private void TryQueueDerived(int id, Entry e)
    {
        if (!e.Derived || e.State != ImageState.Pending || e.BakeQueued || _bakedBlurs is null) return;
        if (!_byId.TryGetValue(e.SourceId, out var source) || source.State != ImageState.Ready) return;
        e.BakeQueued = true;
        _bakedBlurs.Enqueue(new BakedBlurQueue.Job(id, e.SourceId, e.BakeTargetW, e.BakeTargetH,
            e.BakeSigmaTexels, e.BakeGeneration));
        _totalBakeQueued++;
        Diag.Set("media", "bakedBlurQueued", _totalBakeQueued);
    }

    private void TryQueueDerivedUpgrade(int id, Entry e)
    {
        if (!e.Derived || e.State != ImageState.Ready || e.Refs <= 0
            || e.BakeQuality == BakedBlurQueue.Quality.High || e.BakeUpgradeQueued
            || e.BakeUpgradeAttempts >= 3 || _bakedBlurs is null) return;
        if (!_byId.TryGetValue(e.SourceId, out var source) || source.State != ImageState.Ready) return;
        e.BakeUpgradeQueued = true;
        e.BakeUpgradeAttempts++;
        _bakedBlurs.Enqueue(new BakedBlurQueue.Job(id, e.SourceId, e.BakeTargetW, e.BakeTargetH,
            e.BakeSigmaTexels, e.BakeGeneration, IsUpgrade: true));
        _totalBakeQueued++;
        Diag.Set("media", "bakedBlurQueued", _totalBakeQueued);
    }

    private void QueueSourceDependents(int sourceId, bool sourceReady)
    {
        if (!_derivedBySource.TryGetValue(sourceId, out var dependents)) return;
        for (int i = 0; i < dependents.Count; i++)
        {
            int id = dependents[i];
            if (!_byId.TryGetValue(id, out var e) || !e.Derived) continue;
            if (sourceReady)
            {
                if (e.State is ImageState.None or ImageState.Failed) RestartDerived(id, e);
                else if (e.State == ImageState.Pending) TryQueueDerived(id, e);
                else TryQueueDerivedUpgrade(id, e);
            }
            else if (e.State == ImageState.Pending && !e.BakeQueued)
            {
                _pendingCount--;
                e.State = ImageState.Failed;
                e.Failure = ImageFailureKind.Decode;
                _totalBakeFailed++;
                NotifyStatus(e);
                ImageStatusChanged?.Invoke(id, e.State, e.Failure, 1);
            }
        }
    }

    private void RestartDecode(int id, Entry e, ImagePriority priority)
    {
        bool trace = Diag.CompiledIn && Diag.Enabled && DiagTraced(e.Key.Source);
        if (e.State == ImageState.Pending)
        {
            if (trace)
                Diag.Event("img", $"restart id={id} src={DiagSourceTail(e.Key.Source)} verdict=prioritize prio={priority}");
            _decoder.Prioritize(id, priority); return;
        }
        if (e.State == ImageState.Failed && (IsTransientFailure(e.Failure) || e.Failure == ImageFailureKind.Decode)
            && _clockMs - e.LastRestartMs < RestartBackoffMs)
        {
            if (trace)
                Diag.Event("img", $"restart id={id} src={DiagSourceTail(e.Key.Source)} verdict=backoff fail={e.Failure}");
            return;
        }
        if (trace)
            Diag.Event("img", $"restart id={id} src={DiagSourceTail(e.Key.Source)} verdict=begin " +
                $"decode={e.Key.W}x{e.Key.H} prio={priority} from={e.State} fail={e.Failure} refs={e.Refs}");
        e.LastRestartMs = _clockMs;
        e.RequestedMs = _clockMs;
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
    public int Pump() => Pump(long.MaxValue);

    /// <summary>Frame-budget-aware overload (scroll-v3 §3.3 item 6, <c>Hosting.FrameBudget.DeadlineTicks</c>): while a
    /// viewport is Drag/Ballistic the host bounds decode-apply work to the frame's motion slice, same as the paired
    /// <c>TreeReconciler.ReRealizeVirtuals(long)</c> overload. Only <see cref="Media.DecodeScheduler"/> — the real,
    /// scroll-throttled leaf — honors the deadline (it already caps applies-per-frame internally); the headless/fake
    /// <see cref="IImageDecoder"/> leaves used by tests have no burst to bound, so they fall back to the plain drain.
    /// long.MaxValue (the parameterless overload above; every steady frame) is unbounded either way.</summary>
    public int Pump(long deadlineTicks)
    {
        _pumpCompleted = 0;
        if (_asyncUploads is { } q) DrainAsyncRejections(q);   // fold +1-frame async upload rejections before this pump's decodes
        DrainBakedBlurResults();
        if (_decoder is DecodeScheduler ds) ds.Pump(_onComplete, _onPixels, deadlineTicks);
        else _decoder.Pump(_onComplete, _onPixels);
        if (_pumpCompleted > 0) EvictToBudget();
        return _pumpCompleted;
    }

    private void DrainBakedBlurResults()
    {
        if (_bakedBlurs is not { } q) return;
        while (q.TryDequeueResult(out var result))
        {
            if (!_byId.TryGetValue(result.Id, out var e) || !e.Derived || e.BakeGeneration != result.Generation)
            {
                _totalBakeStale++;
                Diag.Set("media", "bakedBlurStale", _totalBakeStale);
                continue;
            }

            if (result.IsUpgrade)
            {
                if (e.State != ImageState.Ready || !e.BakeUpgradeQueued)
                {
                    _totalBakeStale++;
                    Diag.Set("media", "bakedBlurStale", _totalBakeStale);
                    continue;
                }
                e.BakeUpgradeQueued = false;
                if (!result.Ok)
                {
                    // The provisional texture remains valid. Retry a bounded number of times while it stays visible;
                    // unpinning resets the budget so a later remount can try again after memory pressure subsides.
                    _totalBakeFailed++;
                    Diag.Set("media", "bakedBlurFailed", _totalBakeFailed);
                    TryQueueDerivedUpgrade(result.Id, e);
                    continue;
                }
                long priorBytes = e.Bytes;
                e.W = result.W;
                e.H = result.H;
                e.Bytes = CommittedBytesFor(result.W, result.H);
                e.BakeQuality = result.Quality;
                e.BakeUpgradeAttempts = 0;
                UsedBytes += e.Bytes - priorBytes;
                DerivedUsedBytes += e.Bytes - priorBytes;
                _totalBakeReady++;
                _pumpCompleted++;
                ContentEpoch++;
                Diag.Set("media", "bakedBlurReady", _totalBakeReady);
                continue;
            }

            if (e.State != ImageState.Pending || !e.BakeQueued)
            {
                _totalBakeStale++;
                Diag.Set("media", "bakedBlurStale", _totalBakeStale);
                continue;
            }
            e.BakeQueued = false;
            _pendingCount--;
            e.State = result.Ok ? ImageState.Ready : ImageState.Failed;
            e.Failure = result.Ok ? ImageFailureKind.None : ImageFailureKind.GpuUpload;
            if (result.Ok)
            {
                e.W = result.W;
                e.H = result.H;
                e.Bytes = CommittedBytesFor(result.W, result.H);
                e.BakeQuality = result.Quality;
                UsedBytes += e.Bytes;
                DerivedUsedBytes += e.Bytes;
                BeginReveal(e, result.Id, "derived");
                _totalBakeReady++;
            }
            else { e.Bytes = 0; _totalBakeFailed++; }
            _pumpCompleted++;
            ContentEpoch++;
            Diag.Set("media", "bakedBlurReady", _totalBakeReady);
            Diag.Set("media", "bakedBlurFailed", _totalBakeFailed);
            Diag.Set("media", "pending", PendingCount);
            NotifyStatus(e);
            ImageStatusChanged?.Invoke(result.Id, e.State, e.Failure, 1);
            if (result.Ok) TryQueueDerivedUpgrade(result.Id, e);
        }
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
                && e.TextureMs + e.RevealMs >= _clockMs;
            e.TextureMs = float.NaN;
            e.State = ImageState.Failed;
            e.Failure = r.Result == ImageUploadResult.ResourceExhausted ? ImageFailureKind.GpuResourceExhausted : ImageFailureKind.GpuUpload;
            if (wasActiveDeadline) RecomputeCrossfadeDeadline();
            _totalFailed++;
            ContentEpoch++;
            Diag.Set("media", "failed", _totalFailed);
            NotifyStatus(e);
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
                && e.TextureMs + e.RevealMs >= _clockMs;
            e.TextureMs = float.NaN;
            if (wasActiveDeadline) RecomputeCrossfadeDeadline();
        }
        if (e.State == ImageState.Pending) _pendingCount--;   // leaving Pending (Ready/Failed) — mirror the former scan
        bool restartVisibleCancel = !ok && failure == ImageFailureKind.Canceled && e.Refs > 0;
        e.State = ok ? ImageState.Ready : ImageState.Failed;
        e.Failure = ok ? ImageFailureKind.None : failure;
        e.Attempts = attempts;
        if (ok && float.IsNaN(e.TextureMs)) BeginReveal(e, id, "decode");
        if (ok) e.WasReady = true;   // AFTER BeginReveal: a first-ever decode must still see WasReady==false there
        e.W = w; e.H = h;
        // COMMITTED bytes, not decoded pixels — see CommittedBytesFor. Budgeting against the decoded figure let the
        // cache believe it was holding its cap while the GPU held roughly 3.5x that.
        e.Bytes = ok ? CommittedBytesFor(w, h) : 0;
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
            NotifyStatus(e);
            ImageStatusChanged?.Invoke(id, e.State, e.Failure, attempts);
        }
        QueueSourceDependents(id, ok);
    }

    /// <summary>Shed down to budget NOW, rather than waiting for the next completed decode.
    /// <para>The only thing that used to call this was <see cref="Pump"/>, and only when a decode had just finished —
    /// so an app that navigated away from an image-heavy page and then decoded nothing stayed over budget
    /// indefinitely, holding a parked page's covers until something unrelated happened to complete. Parking a subtree
    /// unpins everything in it, which is exactly the moment the LRU has new candidates and none of them are on
    /// screen. Cheap when there is nothing to do: the loop's first predicate is two long compares.</para></summary>
    public void TrimToBudget() => EvictToBudget();

    private void EvictToBudget()
    {
        while (UsedBytes > _budgetBytes || DerivedUsedBytes > DerivedSoftBudgetBytes)
        {
            bool preferDerived = DerivedUsedBytes > DerivedSoftBudgetBytes;
            if (EvictOneLru(preferDerived) == 0) break;   // everything left is pinned (on screen) — never evict it
        }
    }

    /// <summary>
    /// Evicts the single oldest UNPINNED (<c>Refs == 0</c>) Ready entry — optionally restricted to derived/blur entries
    /// (<paramref name="preferDerived"/>) which are the cheapest to lose — and returns the bytes it freed, or 0 when
    /// nothing is evictable (everything left is pinned/visible). Allocation-free (a struct dictionary-enumerator scan,
    /// no closures). Shared by <see cref="EvictToBudget"/> and <see cref="EvictToVramPressure"/>.
    /// </summary>
    private long EvictOneLru(bool preferDerived)
    {
        int victim = 0; long oldest = long.MaxValue;
            foreach (var (id, e) in _byId)
                if (e.Refs == 0 && e.State == ImageState.Ready && (!preferDerived || e.Derived) && e.LastUsed < oldest)
                { oldest = e.LastUsed; victim = id; }
            if (victim == 0) return 0;   // everything left is pinned (on screen) — never evict it
            var e2 = _byId[victim];
            long freed = e2.Bytes;
            UsedBytes -= e2.Bytes;
            if (e2.Derived) DerivedUsedBytes -= e2.Bytes;
            bool activeDeadline = e2.Transition.Enabled && !float.IsNaN(e2.TextureMs)
                && e2.TextureMs + e2.RevealMs >= _clockMs;
            e2.State = ImageState.None;
            e2.Failure = ImageFailureKind.None;
            e2.Attempts = 0;
            if (!e2.Derived) e2.W = e2.H = 0;
            e2.Bytes = 0;
            e2.TextureMs = float.NaN;
            if (e2.Derived)
            {
                e2.BakeQueued = false;
                e2.BakeUpgradeQueued = false;
                e2.BakeUpgradeAttempts = 0;
                e2.BakeQuality = BakedBlurQueue.Quality.Minimal;
                e2.BakeGeneration++;
                _bakedBlurs?.Invalidate(victim, e2.BakeGeneration);
            }
            // If the evicted entry could still be the crossfade-deadline high-water (an unpinned image whose fade hasn't
            // elapsed), recompute the max so HasActiveCrossfades can't report a stale future deadline. Settled entries
            // (deadline < clock — the overwhelming evict case) skip the recompute, so the steady path stays scan-free.
            if (activeDeadline)
                RecomputeCrossfadeDeadline();
            _totalEvicted++;
            Diag.Set("media", "evicted", _totalEvicted);
            if (Diag.CompiledIn && Diag.Enabled && DiagTraced(e2.Key.Source))
                Diag.Event("img", $"evict id={victim} src={DiagSourceTail(e2.Key.Source)} " +
                    $"decode={e2.Key.W}x{e2.Key.H} derived={(e2.Derived ? 1 : 0)} " +
                    $"usedMB={UsedBytes / (1024.0 * 1024.0):0.0} budgetMB={_budgetBytes / (1024.0 * 1024.0):0.0}");
            NotifyStatus(e2);
            _evictSink(victim);   // free the GPU texture (the device defers the release behind the frame fence)
            return freed;
    }

    /// <summary>
    /// Weak-GPU VRAM-pressure relief (adreno-hang-fixes.md M5). The host samples the device's LOCAL-segment budget
    /// (<see cref="FluentGpu.Rhi.IGpuDevice.TryGetVramUsage"/>) and, when total VRAM crosses ~0.90 of budget, calls this
    /// every frame; it sheds unpinned image-cache LRU down toward the 0.85 soft line.
    ///
    /// <para><b>Approximation.</b> The device figure (<paramref name="usedBytes"/> / <paramref name="budgetBytes"/>) is
    /// TOTAL VRAM — swapchain + every OpacityLayer RT + all textures — but this cache only tracks its OWN image bytes
    /// (<see cref="UsedBytes"/>). We cannot know how much of the overage is ours, so we shed our share: compute the
    /// overage above <c>budget*0.85</c> and evict image-cache LRU bytes to cover it, bounded by what the cache actually
    /// holds. Freeing our portion relieves proportional device pressure without ever evicting pinned/visible entries;
    /// the remaining overage (swapchain / RTs) is bounded by the other M5 levers (RT pool cap, depth-3 swapchain).</para>
    ///
    /// <para>Hysteresis lives on the caller: the host arms this hard path only above 0.90 and stops once back under, so
    /// this method just executes when called. It is allocation-free (the shared <see cref="EvictOneLru"/> scan) and safe
    /// to call every frame.</para>
    /// </summary>
    public void EvictToVramPressure(long budgetBytes, long usedBytes)
    {
        if (budgetBytes <= 0) return;
        long softLine = (long)(budgetBytes * 0.85);
        long overage = usedBytes - softLine;
        if (overage <= 0) return;                       // already under the soft line — nothing to shed
        long target = UsedBytes - overage;              // shed at most our share of the overage…
        if (target < 0) target = 0;                     // …bounded by what the cache holds (never negative)
        while (UsedBytes > target)
        {
            bool preferDerived = DerivedUsedBytes > 0;  // blur/derived first — cheapest to lose, re-baked on demand
            long freed = EvictOneLru(preferDerived);
            if (freed == 0 && preferDerived) freed = EvictOneLru(false);   // derived all pinned → try full images
            if (freed == 0) break;                      // everything left is pinned/visible — never evict it
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
