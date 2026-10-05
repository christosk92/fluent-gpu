using System.Threading;
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
/// pixel memory: it flows decoder → cache.Pump → host sink → IGpuDevice.UploadImage in one stack. Under the async
/// render thread the host sink may instead TAKE the buffer behind the span — <c>Media.DecodeScheduler.TryTakeDecodeBuffer</c>
/// transfers ownership of the scheduler's pooled decode buffer for exactly the span it loaned — so no second copy is
/// made on the UI thread; the cache stays uninvolved (the span contract here is unchanged, the loan is the scheduler's).</summary>
public delegate void ImageReadyHandler(int id, System.ReadOnlySpan<byte> bgra8, int w, int h);

/// <summary>Admission-aware variant of <see cref="ImageReadyHandler"/>. The span has the same call-scoped lifetime and
/// the same take-instead-of-copy option (<c>Media.DecodeScheduler.TryTakeDecodeBuffer</c>).</summary>
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
/// <para><b>State machine</b> of a full-image entry (<c>State/Failure</c>; derived bakes mirror it through
/// <c>RestartDerived</c>). <c>Refs</c> is the pin count — a realized node holding the handle:</para>
/// <code>
///  miss ─Request─▶ Pending ─decode ok (upload admitted)──────────▶ Ready ─LRU evict (Refs==0)─▶ None/None tombstone
///                  Pending ─Begin refused (off-screen backpressure)▶ None/Canceled    ┐ "canceled leftover"
///                  Pending ─decode Canceled, Refs==0──────────────▶ Failed/Canceled   ┘
///                  Pending ─decode Canceled, Refs&gt;0──────────────▶ RestartDecode(Visible)
///                  Pending ─decode/upload failed──────────────────▶ Failed/{Network,Timeout,ServerError,Decode,
///                                                                    NotFound,HttpError,GpuUpload,GpuResourceExhausted}
///  None | Failed ─ShouldRestart (Request / Pin)─▶ RestartDecode ─▶ Pending (a refused Begin lands None/Canceled again)
///  canceled leftover, Refs&gt;0 ─Promote (a virtualized row turning visible)─▶ RestartDecode(promoted lane)
///  canceled leftover, Refs&gt;0 ─Pump: RestartPinnedLeftovers, once CanceledLeftoverRetryMs has passed since the
///                               entry's last restart; no Request/Pin needed─▶ RestartDecode(Visible)
///  Failed/GpuResourceExhausted, Refs&gt;0 ─Pump: RetryPinnedExhausted (RestartBackoffMs)─▶ RestartDecode(Visible)
///  Failed/{NotFound,HttpError,GpuUpload} are terminal: never swept, never restarted by a re-pin.
/// </code>
/// The leftover sweep exists because a STATIC mounted node never calls Request/Pin again: a card unmounted mid-decode
/// (UnpinImageNode → Cancel) and remounted in an Overscan lane that is full, or any pinned re-Begin the scheduler
/// dropped, would otherwise paint its placeholder forever — neither Ready, Pending nor Failed-with-a-reason.
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
        // The fields ImageRecordingSnapshot copies (State, W, H, TextureMs, RevealMs, SwapHoldUntilMs, Transition) are properties
        // so EVERY write bumps RecordingInputSerial: a publication whose snapshot inputs did not move can then be proven
        // unchanged in O(1) instead of re-copying every referenced entry (SceneRenderFrame.Capture) or publishing at all
        // (the host's no-op publication skip). The setters are the only write path; nothing takes a ref to these.
        public ImageState State { get => _state; set { _state = value; NoteRecordingInputChanged(); } }
        public int W { get => _w; set { _w = value; NoteRecordingInputChanged(); } }
        public int H { get => _h; set { _h = value; NoteRecordingInputChanged(); } }
        private ImageState _state;
        private int _w, _h;
        public int Refs;        // liveness: >0 ⇒ on screen ⇒ never evicted
        public long LastUsed;
        public long Bytes;
        public ImageFailureKind Failure;   // why it Failed (None while Pending/Ready)
        public int Attempts;               // fetch attempts the decoder made (>1 ⇒ transient retries occurred)
        // clock (ms) when the FIRST texture (blurhash or full-res) appeared → fade origin
        public float TextureMs { get => _textureMs; set { _textureMs = value; NoteRecordingInputChanged(); } }
        private float _textureMs = float.NaN;
        // the placeholder→image reveal (duration + easing); set at request. Its easing is a recording input (see State).
        public ImageTransition Transition { get => _transition; set { _transition = value; NoteRecordingInputChanged(); } }
        private ImageTransition _transition;
        // Duration of the CURRENT reveal. Normally Transition.DurationMs; shortened to ShortRevealMs for a warm re-landing
        // (BeginReveal). Every deadline/progress read uses THIS, so a shortened reveal keeps the wake bookkeeping exact.
        public float RevealMs { get => _revealMs; set { _revealMs = value; NoteRecordingInputChanged(); } }
        private float _revealMs;
        public float RequestedMs = float.NegativeInfinity;   // clock (ms) when the current decode was requested → the cache-adjacent test
        public long RequestedTicks;   // Stopwatch ticks of that request; ImageLatencyCensus.Fetch at the first texture (0 = noted)
        public float LastRestartMs = float.NegativeInfinity;   // backoff gate for visible/transient-failure retries
        // Sticky: true once this entry has EVER reached Ready. Persists through RestartDecode/eviction (the Entry
        // object survives in _byId — a tombstone keeps its key), so a LATER re-decode of an already-known-good key
        // is recognized as a warm hit even after the original reveal fully settled. See BeginReveal's instant-at-rest
        // arm (set AFTER that call on the decode completing this entry — never before it — so entry's FIRST-ever
        // decode still sees WasReady==false there and keeps its authored fade, even under a synchronous test decoder
        // that lands well inside InstantRevealWindowMs).
        public bool WasReady;
        // Clock (ms) at which this entry MOST RECENTLY became Ready — refreshed on every Ready landing (a fresh
        // decode, a bake, a bake-quality upgrade), never on a byte-accounting-only touch. EvictOneLru's minReadyAgeMs
        // reads this to give a just-landed texture a grace window before VRAM-pressure relief can shed it again
        // (E4, ImageCache.ReadyGraceMs) — otherwise a landing that pushed VRAM over the arm ratio evicts on the very
        // next frame, re-requests on the next scroll tick, and re-lands into the same pressure (the eviction loop
        // adreno-hang-fixes.md documents). 0 for an entry that has never been Ready.
        public float ReadyMs;
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
        // linked once on the miss path. ResidentRenditionOf walks it; 0 ends the chain. A reclaimed tombstone is
        // unlinked from its chain (ReclaimEntry), so the chain never dangles.
        public int PrevSameSource;
        // Image-swap crossfade (BeginSwap): this entry is some node's OUTGOING texture, drawn opaque under the incoming
        // image until this reveal-clock deadline. Folded into the crossfade deadline so both the UI wake and the render
        // thread's clock-driven presents keep going for the whole swap window.
        public float SwapHoldUntilMs { get => _swapHoldUntilMs; set { _swapHoldUntilMs = value; NoteRecordingInputChanged(); } }
        private float _swapHoldUntilMs = float.NegativeInfinity;
        // Indexed LRU (EvictOneLru): an entry is LINKED into one of the two eviction lists exactly while it is evictable
        // (Ready and unpinned) — LruList 1 = full images, 2 = derived — ordered by when it became evictable or was last
        // used (LastUsed is re-stamped on every link/touch). 0 ids end the list (ids start at 1).
        public int LruPrev, LruNext;
        public byte LruList;
        // A derived entry's own dedup key, so a reclaim can drop it from _byDerivedKey without a scan.
        public DerivedKey DKey;
        // True while this id sits in _leftovers (the pinned canceled-leftover retry list) — dedups NoteCanceledLeftover.
        public bool LeftoverListed;
    }

    // Process-wide (every cache, every entry): bumped on any write to a field ImageRecordingSnapshot copies, and on an entry's
    // removal. A change detector, not a count — readers only compare it for equality, so a racing increment can never make
    // two different states read equal. Interlocked because a write is rare and the reader may sit on another host.
    private static long s_recordingInputSerial;
    private static void NoteRecordingInputChanged() => Interlocked.Increment(ref s_recordingInputSerial);

    /// <summary>Moves whenever any entry's recording inputs (state, size, reveal/fade parameters, swap hold) change or an entry
    /// is dropped, in ANY cache. Equal values bracket a span in which every <see cref="ImageRecordingSnapshot"/> input is
    /// unchanged. O(1).</summary>
    internal static long RecordingInputSerial => Interlocked.Read(ref s_recordingInputSerial);

    const float RestartBackoffMs = 2000f;   // min gap between visible retries on the same handle (avoids hammering a dead URL)
    /// <summary>How long a PINNED canceled leftover (None/Canceled or Failed/Canceled — a dropped or canceled decode, not
    /// a failure of the URL) waits after its last restart before Pump's leftover sweep re-begins it at
    /// <see cref="ImagePriority.Visible"/>. Shorter than <see cref="RestartBackoffMs"/> on purpose: that backoff guards a
    /// dead URL from being hammered, and a cancel says nothing about the URL. It still has to be long enough for a
    /// fling's overscan halo to pass: a row that scrolls away inside this window unpins and is dropped from the sweep,
    /// so only nodes that STAY mounted are promoted to the Visible lane (which the scheduler never refuses).</summary>
    public const float CanceledLeftoverRetryMs = 500f;
    // Per-pump cap on leftover restarts, so a burst of dropped requests re-Begins over a few frames, not in one.
    const int MaxLeftoverRestartsPerPump = 32;
    // A texture that lands within InstantRevealWindowMs of its request is cache-adjacent (a disk/OS-cache hit): it gets
    // ShortRevealMs — a quick fade over the one placeholder frame it still had, never an instant pop. Reveal length is
    // never keyed on scrolling (scroll rework §7).
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
    /// <summary>Grace window (ms) a freshly-Ready entry is exempt from VRAM-pressure relief (E4,
    /// <see cref="EvictToVramPressure"/>) — long enough that the texture that just pushed VRAM over the arm ratio
    /// is not itself the first thing shed, which was the whole loop: evict → re-request on the next scroll tick →
    /// re-land → evict again (adreno-hang-fixes.md M5). <see cref="EvictToBudget"/> (the ordinary byte-budget path)
    /// is unaffected — it keeps 0 ms, because it must always be able to reach its cap.</summary>
    internal const float ReadyGraceMs = 2000f;

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
    // O(1) maintained mirror of the former ReadyCount scan (E8): incremented at every transition INTO Ready (a fresh
    // decode landing, a bake completing), decremented at every transition OUT of Ready (LRU eviction, a device-lost
    // RestartDecode/RestartDerived, an async upload rejection). It counts the LIVE Ready set only — never the historical
    // id count (tombstones are neither Ready nor Pending; see ReclaimTombstonesIfDue).
    private int _readyCount;
    private float _maxCrossfadeDeadlineMs = float.NegativeInfinity;
    private float _clockMs;   // monotonic ms clock for cross-fade timing (advanced by Tick once per painted frame)
    // E1: ids whose CONTENT changed this pump cycle (a restart, a bake landing, an upload rejection — every site that
    // used to just bump ContentEpoch) without a corresponding ImageStatusChanged event describing them to the host's
    // dirty-marking pass. AppHost folds these into MarkImageDirty each pump, then clears via ClearContentChanged —
    // the ONE clear site, on both the skip and submit branches. Fixed capacity: a landing burst past it sets the
    // overflow flag instead of growing, and AppHost's over-inclusion fallback (ForceFull(ImageContent)) covers it.
    private readonly int[] _contentChanged = new int[64];
    private int _contentChangedCount;
    private bool _contentChangedOverflow;
    // E1: ids with a live crossfade — appended by NoteCrossfadeDeadline (BeginReveal's reveal, BeginSwap's outgoing
    // hold). RevealingIds prunes settled ids IN PLACE on every read (the host reads it once per pump to describe
    // crossfade repaint rects), so the array only ever holds ids someone still needs to know about.
    private readonly int[] _revealing = new int[128];
    private int _revealingCount;
    private bool _revealingOverflow;
    // E7: a PINNED (Refs>0) entry can go GpuResourceExhausted and then never self-heal — Pin/Request only run again
    // on a remount, which a realized on-screen node does not do. Set true wherever such a failure lands
    // (DrainAsyncRejections, OnDecodeComplete); Pump's RetryPinnedExhausted scans and restarts every matching entry
    // once RestartBackoffMs has passed since the last failure/scan, then clears the flag.
    private bool _exhaustedPinnedSeen;
    private float _lastExhaustedScanMs;
    // T10: ids of PINNED canceled leftovers (see the class state table) awaiting a retry — appended at the one
    // transition that can produce one with Refs>0 (RestartDecode's refused Begin; see NoteCanceledLeftover), pruned
    // IN PLACE by RestartPinnedLeftovers. Fixed capacity like _contentChanged: past it _leftoverOverflow makes the sweep
    // fall back to one bounded scan of _byId. _leftoverDueMs is the earliest image-clock time any listed entry becomes
    // due (+∞ = nothing listed), so a steady pump pays one float compare and a sweep never polls ahead of time.
    private readonly int[] _leftovers = new int[256];
    private int _leftoverCount;
    private bool _leftoverOverflow;
    private float _leftoverDueMs = float.PositiveInfinity;
    private const int BlurW = 32, BlurH = 32;
    private readonly byte[] _blurScratch = new byte[BlurW * BlurH * 4];   // reused (UI thread only) for LQIP decode

    // ── image-pipeline trace (DIAGNOSTIC ONLY) ───────────────────────────────────────────────────────────────────
    // Turn on with --fg diag (Diag.Enabled); narrow with --fg img=FILTER=<substring of the source url>. Answers the
    // questions you cannot answer from pixels: did the SOURCE change (same art, different CDN size hash), did the
    // requested DECODE size change, was the entry resident when a node re-pinned it, or did it restart/evict.
    // Every helper here is called ONLY from inside a `Diag.CompiledIn && Diag.Enabled` guard, so a Release build (the
    // whole Diag.Event call site is [Conditional]-erased) AND a Debug diag-off run both allocate nothing — the
    // VerticalSlice zero-alloc gates run Debug with Diag off.
    private static string? DiagTraceFilter => FluentGpu.Hosting.EngineSwitches.ImageTrace;

    /// <summary>`--fg img=FILTER` gate: unset ⇒ trace everything; set ⇒ only sources CONTAINING it (case-insensitive).</summary>
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
    /// <param name="weak">The tier decision, made ONCE by the host from <see cref="GpuMemoryBudgets"/> and handed
    /// down (never read from <c>GpuProfile</c> here — see <see cref="DerivedSoftBudgetBytes"/>'s remark for why that
    /// matters).</param>
    public ImageCache(IImageDecoder decoder, long budgetBytes = 96L * 1024 * 1024, long derivedSoftBudgetBytes = 0, bool weak = false)
    {
        _decoder = decoder;
        _budgetBytes = budgetBytes;
        DerivedSoftBudgetBytes = derivedSoftBudgetBytes > 0 ? derivedSoftBudgetBytes : GpuMemoryBudgets.DerivedDefault;
        _onComplete = OnDecodeComplete;
        _onPixels = OnPixels;
        _pixelSink = _noPixels;
        _evictSink = _noEvict;
        IsWeakTier = weak;
    }

    /// <summary>The tier this cache was constructed for, instead of the process-global <c>GpuProfile.IsWeak</c>, which is
    /// always false headlessly and lands too late in production (see the ctor's <c>weak</c> parameter).</summary>
    public bool IsWeakTier { get; }

    /// <summary>Always-on latency census (request to texture, reveal kinds, cancels; the reconciler adds the source
    /// waits). The host drains it per window.</summary>
    public ImageLatencyCensus Latency { get; } = new();

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

    /// <summary>Entries the cache holds (live + tombstones). Diagnostics/gates.</summary>
    internal int EntryCount => _byId.Count;

    /// <summary>Entries the most recent LRU eviction examined. Diagnostics/gates.</summary>
    internal int LastEvictVisited { get; private set; }

    /// <summary>Install the host's enumeration of image ids something still HOLDS (a scene node's paint — parked nodes
    /// included —, a row cell, an image effect's derived/outgoing id, a hold-last-good target) — the proof a tombstone
    /// needs before it can be reclaimed. Without one the cache never reclaims (every tombstone may still be held).</summary>
    public void SetHeldImageSource(System.Action<HashSet<int>> collectHeld) => _collectHeld = collectHeld;

    private System.Action<HashSet<int>>? _collectHeld;
    private readonly HashSet<int> _held = new();
    private readonly List<int> _reclaimScratch = new();
    // Indexed LRU heads/tails: [1] = full images, [2] = derived (index 0 unused). 0 = empty.
    private readonly int[] _lruHead = new int[3], _lruTail = new int[3];

    /// <summary>A reclaim sweep runs only once tombstones outnumber BOTH this floor and the live (Ready + Pending) set —
    /// so the O(entries) sweep is amortized over at least as many evictions as it reclaims.</summary>
    internal const int ReclaimFloor = 1024;

    /// <summary>Entries neither Ready nor Pending (evicted / failed / canceled). O(1).</summary>
    internal int TombstoneCount => _byId.Count - _readyCount - _pendingCount;

    public long UsedBytes { get; private set; }
    public long DerivedUsedBytes { get; private set; }
    /// <summary>The derived/blur soft cap this cache was built with — the budget the host chose for the tier, exposed
    /// so a headless gate can prove the cache honoured what it was handed instead of re-deriving one from a global
    /// that is always false outside a real device.</summary>
    public long DerivedBudgetBytes => DerivedSoftBudgetBytes;
    public int Count => _byId.Count;
    public int ReadyCount => _readyCount;
    public int ContentEpoch { get; private set; }
    /// <summary>Ids whose content changed this pump cycle without their own <see cref="ImageStatusChanged"/> event
    /// (E1) — a restart, a bake landing, an upload rejection. The host folds these into its per-node dirty marking
    /// each pump, then MUST call <see cref="ClearContentChanged"/> (the only clear site). Overflows past 64 in one
    /// pump set <see cref="ContentChangedOverflow"/> instead of growing.</summary>
    internal ReadOnlySpan<int> ContentChangedIds => _contentChanged.AsSpan(0, _contentChangedCount);
    /// <summary>True when more than 64 ids changed content in one pump cycle — the host's named-full surrender
    /// (<c>RepaintFullReason.ImageContent</c>) instead of an itemized per-node describe.</summary>
    internal bool ContentChangedOverflow => _contentChangedOverflow;
    /// <summary>The only clear site for <see cref="ContentChangedIds"/>/<see cref="ContentChangedOverflow"/> — called
    /// by the host once it has folded this cycle's ids into its dirty marking, on both the skip and submit branches.</summary>
    internal void ClearContentChanged()
    {
        _contentChangedCount = 0;
        _contentChangedOverflow = false;
    }

    /// <summary>Ids with a still-live crossfade (a reveal in progress, or an outgoing swap-hold) — E1's per-node
    /// alternative to a full-frame <c>DetachedContent</c> force. Settled ids are pruned (and the backing array
    /// compacted) on every read, so the host can call this once per pump and get exactly the still-fading set.</summary>
    internal ReadOnlySpan<int> RevealingIds
    {
        get
        {
            int w = 0;
            for (int r = 0; r < _revealingCount; r++)
            {
                int id = _revealing[r];
                if (!_byId.TryGetValue(id, out var e)) continue;   // reclaimed since it was listed — nothing left to fade
                bool hasSwapHold = e.SwapHoldUntilMs > float.NegativeInfinity;
                if (float.IsNaN(e.TextureMs) && !hasSwapHold) continue;   // no active reveal, no outgoing hold — settled
                float textureDeadline = float.IsNaN(e.TextureMs) ? float.NegativeInfinity : e.TextureMs + e.RevealMs;
                float deadline = MathF.Max(textureDeadline, e.SwapHoldUntilMs);
                if (deadline < _clockMs) continue;                        // settled
                _revealing[w++] = id;
            }
            _revealingCount = w;
            // Compaction freed room, so a stale overflow (set the last time the array was genuinely full) no longer
            // describes the current state — a fresh overflow can only be re-armed by a later AddRevealing.
            if (w < _revealing.Length) _revealingOverflow = false;
            return _revealing.AsSpan(0, w);
        }
    }
    /// <summary>True when more than 128 crossfades were concurrently live at some point since the last
    /// <see cref="RevealingIds"/> read that had room to compact — the host's named-full surrender
    /// (<c>RepaintFullReason.DetachedContent</c>) instead of an itemized per-node describe.</summary>
    internal bool RevealingOverflow => _revealingOverflow;

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
            LruTouch(id, hit);
            // Evicted entries keep their key as a tombstone so a retained ImageEl node can re-pin the same handle and
            // recover without needing its original URL. Capacity rejection is likewise retryable after all owners release.
            bool restart = ShouldRestart(hit, priority);
            if (!restart) Latency.NoteHit();
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
        var entry = new Entry { Key = key, State = ImageState.Pending, LastUsed = _clock++, RequestedMs = _clockMs,
            RequestedTicks = System.Diagnostics.Stopwatch.GetTimestamp(), Transition = transition ?? ImageTransition.Default };
        Latency.NoteMiss();
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
            // A canceled leftover with Refs==0 (a fresh entry has no pin yet), so nothing is listed for the sweep here:
            // the node's Pin that follows re-Begins it through ShouldRestart, and if THAT is refused too it lands in
            // RestartDecode's refusal arm with Refs>0, which lists it (NoteCanceledLeftover).
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
            LruTouch(existing, hit);
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
            DKey = key,
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
    /// <summary>The (source, decode W, decode H) a non-derived handle was requested at; false for a derived / unknown handle.</summary>
    public bool TryGetTarget(ImageHandle h, out string source, out int w, out int hgt)
    {
        if (_byId.TryGetValue(h.Id, out var e) && !e.Derived) { source = e.Key.Source; w = e.Key.W; hgt = e.Key.H; return true; }
        source = ""; w = hgt = 0;
        return false;
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
            if (e.State == ImageState.Pending) Latency.NoteCanceled();
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

    // Real-window image time is independent of the animator's clamped/resynced delta. The timestamp travels with
    // recording inputs so a sparse publication cannot replace render-side wall-time progress with an older anchor.
    internal double ClockCapturedAtMs { get; private set; } = double.NaN;
    private FluentGpu.Hosting.ImagePresentationClock _presentationClock;
    internal void AdvancePresentationClock(double sampledAtMs)
    {
        _clockMs = (float)_presentationClock.Sample(sampledAtMs);
        ClockCapturedAtMs = sampledAtMs;
    }

    /// <summary>Monotonic reveal clock (ms) — passed to the GPU replay path to resolve fade params baked into DrawImageCmd.</summary>
    public float ClockMs => _clockMs;

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
        durationMs = e.RevealMs;              // the CURRENT reveal length (ShortRevealMs for a warm re-landing)
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

    /// <summary>E1: bump <see cref="ContentEpoch"/> AND note <paramref name="id"/> in <see cref="ContentChangedIds"/> —
    /// replaces every bare <c>ContentEpoch++</c> site so the host can describe the landing with a per-node repaint
    /// rect instead of forcing a full frame. Fixed capacity; past it, <see cref="ContentChangedOverflow"/> covers it.</summary>
    private void NoteContentChanged(int id)
    {
        ContentEpoch++;
        if (_contentChangedCount < _contentChanged.Length) _contentChanged[_contentChangedCount++] = id;
        else _contentChangedOverflow = true;
    }

    /// <summary>E1: note <paramref name="id"/> in <see cref="RevealingIds"/>. Fixed capacity; past it,
    /// <see cref="RevealingOverflow"/> covers it.</summary>
    private void AddRevealing(int id)
    {
        if (_revealingCount < _revealing.Length) _revealing[_revealingCount++] = id;
        else _revealingOverflow = true;
    }

    // `id` + `cause` are carried for the trace only (a reveal is exactly the "flashed back in" the user sees, so which
    // texture started it — the blurhash LQIP, the full decode, a baked derivative — is the whole question).
    void BeginReveal(Entry e, int id, string cause)
    {
        if (Diag.CompiledIn && Diag.Enabled && DiagTraced(e.Key.Source))
            Diag.Event("img", $"reveal id={id} src={DiagSourceTail(e.Key.Source)} cause={cause} " +
                $"enabled={(e.Transition.Enabled ? 1 : 0)} revealMs={e.Transition.DurationMs:0.#} " +
                $"sinceRequestMs={(float.IsInfinity(e.RequestedMs) ? -1f : _clockMs - e.RequestedMs):0.#} " +
                $"size={e.W}x{e.H} derived={(e.Derived ? 1 : 0)}");
        if (e.RequestedTicks != 0) { Latency.NoteFetched(e.RequestedTicks); e.RequestedTicks = 0; }
        if (!e.Transition.Enabled)
        {
            e.TextureMs = float.NaN;
            e.RevealMs = 0f;
            Latency.NoteReveal(0f, 0f);
            return;
        }
        e.RevealMs = e.Transition.DurationMs;
        e.TextureMs = _clockMs;
        if (_clockMs - e.RequestedMs <= InstantRevealWindowMs && e.WasReady)
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
        Latency.NoteReveal(e.RevealMs, e.Transition.DurationMs);
        NoteCrossfadeDeadline(id, e);
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
    /// set. Disabled reveals (Dur==0) and NaN TextureMs don't contribute to the max (mirrors the scan's guards
    /// exactly), but <paramref name="id"/> is unconditionally noted into <see cref="RevealingIds"/> (E1) — a swap's
    /// outgoing hold (<see cref="BeginSwap"/>) has no enabled Transition of its own yet still needs tracking, and
    /// <see cref="RevealingIds"/>'s own read-time prune is what actually decides when an id drops out.</summary>
    private void NoteCrossfadeDeadline(int id, Entry e)
    {
        if (e.Transition.Enabled && !float.IsNaN(e.TextureMs))
        {
            float deadline = e.TextureMs + e.RevealMs;
            if (deadline > _maxCrossfadeDeadlineMs) _maxCrossfadeDeadlineMs = deadline;
        }
        AddRevealing(id);
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
        if (_byId.TryGetValue(outgoing.Id, out var e))
        {
            if (until > e.SwapHoldUntilMs) e.SwapHoldUntilMs = until;
            if (until > _maxCrossfadeDeadlineMs) _maxCrossfadeDeadlineMs = until;
            NoteCrossfadeDeadline(outgoing.Id, e);   // E1: track the outgoing hold in RevealingIds too
        }
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

    /// <summary>Pin = "on screen" (a realized node holds it); never evicted while pinned. Unpin on recycle/unmount.
    /// <paramref name="priority"/> is the lane the pinning node REQUESTED at (W2-E3): a still-Pending entry is
    /// re-prioritized to it — Visible promotes an overscan/prefetch decode, Overscan leaves an Overscan-lane decode where
    /// the reconciler queued it (the scheduler's Prioritize is raise-only, so a pin can never demote). Before this every
    /// pin forced Visible, which silently undid the reconciler's lane choice one line after the request.</summary>
    public void Pin(ImageHandle h, ImagePriority priority = ImagePriority.Visible)
    {
        if (!_byId.TryGetValue(h.Id, out var e)) return;
        e.Refs++;
        e.LastUsed = _clock++;
        SyncLru(h.Id, e);   // pinned ⇒ no longer evictable
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
        else if (ShouldRestart(e, priority)) RestartDecode(h.Id, e, priority);
        else if (e.State == ImageState.Pending) _decoder.Prioritize(h.Id, priority);
    }

    /// <summary>W2-E3: raise a still-Pending decode to <paramref name="priority"/>'s lane — the reconciler calls it for a
    /// row that was realized in the overscan halo and has just scrolled into the visible band. No ref change, no restart;
    /// a no-op for a settled or derived entry (and the scheduler's Prioritize is itself raise-only and claim-deduped).</summary>
    public void Promote(ImageHandle h, ImagePriority priority)
    {
        if (!_byId.TryGetValue(h.Id, out var e) || e.Derived) return;
        if (e.State == ImageState.Pending) { _decoder.Prioritize(h.Id, priority); return; }
        // An off-screen request the scheduler DROPPED under backpressure (Begin returned false ⇒ a None / Canceled
        // tombstone; a pin at the same lane is dropped again) is restarted at the promoted lane while a node still holds
        // it — otherwise the cover would stay blank until the row happened to re-render. Anything else (Ready, a real
        // failure under its backoff) is left alone.
        if (e.Refs > 0 && (e.State == ImageState.None || (e.State == ImageState.Failed && e.Failure == ImageFailureKind.Canceled)))
            RestartDecode(h.Id, e, priority);
    }

    public void Unpin(ImageHandle h)
    {
        if (!_byId.TryGetValue(h.Id, out var e) || e.Refs <= 0) return;
        if (--e.Refs == 0 && e.Derived) e.BakeUpgradeAttempts = 0;
        SyncLru(h.Id, e);   // a Ready entry scrolled off screen becomes the most recently used evictable one
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
            // Refs==0 stays required here: a realized on-screen node never calls Request/Pin again on its own once
            // mounted, so this branch only ever fires from an actual unmount+remount. E7's pinned-retry story is
            // delivered by Pump's RetryPinnedExhausted sweep instead (it calls RestartDecode directly, bypassing
            // ShouldRestart) — NOT by loosening this check, which a realized node re-rendering (a plain Request()
            // hit while still pinned, no remount) would otherwise hit on every frame; 45b's "still pinned: no retry
            // loop" gate pins exactly that this stays a no-op.
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
    /// until something remounted them. (E7: a PINNED exhausted entry is no longer stuck forever even without this call
    /// — Pump's RetryPinnedExhausted sweep gets to it within one RestartBackoffMs window — but a device-lost recovery
    /// still wants every resident id re-decoded in one pass rather than waiting out that backoff one entry at a time.)
    /// The rebuilt device has a fresh, empty texture store, so "GPU could not admit it" is by construction no longer
    /// true. Their Bytes are already 0, so the byte-accounting undo is a no-op for them.</para></summary>
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
        // E8: this can restart an entry that is currently Ready (ReRealizeAllResident's device-lost pass) — leaving
        // Ready is a ReadyCount decrement wherever it happens, this restart included.
        if (e.State == ImageState.Ready) _readyCount--;
        e.LastRestartMs = _clockMs;
        e.RequestedMs = _clockMs;
        e.RequestedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        Latency.NoteMiss();
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
        SyncLru(id, e);
        NoteContentChanged(id);
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
        // E7: GpuResourceExhausted now backs off the same as a transient/decode failure — a safety net for every
        // caller of RestartDecode on an exhausted entry (an unpin+re-pin remount via ShouldRestart, a device-lost
        // ReRealizeAllResident pass, Pump's RetryPinnedExhausted sweep), so none of them can re-Begin faster than
        // RestartBackoffMs even if called back-to-back.
        if (e.State == ImageState.Failed
            && (IsTransientFailure(e.Failure) || e.Failure is ImageFailureKind.Decode or ImageFailureKind.GpuResourceExhausted)
            && _clockMs - e.LastRestartMs < RestartBackoffMs)
        {
            if (trace)
                Diag.Event("img", $"restart id={id} src={DiagSourceTail(e.Key.Source)} verdict=backoff fail={e.Failure}");
            return;
        }
        if (trace)
            Diag.Event("img", $"restart id={id} src={DiagSourceTail(e.Key.Source)} verdict=begin " +
                $"decode={e.Key.W}x{e.Key.H} prio={priority} from={e.State} fail={e.Failure} refs={e.Refs}");
        // E8: this can restart an entry that is currently Ready (ReRealizeAllResident's device-lost pass) — leaving
        // Ready is a ReadyCount decrement wherever it happens, this restart included.
        if (e.State == ImageState.Ready) _readyCount--;
        e.LastRestartMs = _clockMs;
        e.RequestedMs = _clockMs;
        e.RequestedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        Latency.NoteMiss();
        e.State = ImageState.Pending;
        e.Failure = ImageFailureKind.None;
        e.Attempts = 0;
        e.W = e.H = 0;
        e.Bytes = 0;
        e.TextureMs = float.NaN;
        _pendingCount++;
        SyncLru(id, e);
        _totalRequested++;
        NoteContentChanged(id);
        Diag.Set("media", "requested", _totalRequested);
        if (!_decoder.Begin(id, e.Key.Source, e.Key.W, e.Key.H, priority))
        {
            _pendingCount--;
            e.State = ImageState.None;
            e.Failure = ImageFailureKind.Canceled;
            NoteCanceledLeftover(id, e);   // pinned ⇒ Pump's RestartPinnedLeftovers retries it; unpinned ⇒ no-op
        }
    }

    static bool IsCanceledLeftover(Entry e)
        => !e.Derived && e.Failure == ImageFailureKind.Canceled && e.State is ImageState.None or ImageState.Failed;

    /// <summary>T10: list a PINNED canceled leftover for <see cref="RestartPinnedLeftovers"/>. The only transition that
    /// can leave one with <c>Refs&gt;0</c> is a refused re-Begin in <see cref="RestartDecode"/> (a Pin/Request restart
    /// dropped under off-screen backpressure, or a Visible-cancel restart refused by a decoder that refuses Visible): a
    /// Request miss has no pin yet, and a Canceled completion with <c>Refs&gt;0</c> restarts itself. An unpinned leftover
    /// is not listed — its next Pin/Request restarts it (ShouldRestart). Allocation-free: a fixed array + overflow flag.</summary>
    private void NoteCanceledLeftover(int id, Entry e)
    {
        if (e.Refs <= 0 || e.LeftoverListed || !IsCanceledLeftover(e)) return;
        float due = e.LastRestartMs + CanceledLeftoverRetryMs;
        if (due < _leftoverDueMs) _leftoverDueMs = due;   // folded before the capacity test: an overflow must stay due too
        if (_leftoverCount < _leftovers.Length)
        {
            _leftovers[_leftoverCount++] = id;
            e.LeftoverListed = true;
        }
        else _leftoverOverflow = true;
    }

    /// <summary>Image-clock time (ms) at which the canceled-leftover sweep next has work (+∞ = none). The host turns it
    /// into an idle wake (<c>AppHost.ImageLeftoverDueInMs</c>): a future due time shapes <c>RecommendedWaitMs</c>, a
    /// passed one sets <c>WakeReasons.ImageLeftoverDue</c> so the woken frame's <see cref="Pump()"/> runs the sweep.</summary>
    internal float LeftoverRetryDueMs => _leftoverDueMs;

    /// <summary>Apply finished decodes (UI thread, once per frame) then evict to budget. Returns completions this pump.
    /// Allocation-free when idle (cached callback; empty-queue Pump does nothing) — safe in the hot phase.</summary>
    public int Pump() => Pump(long.MaxValue);

    /// <summary>Deadline-bounded overload. Only <see cref="Media.DecodeScheduler"/> — the real leaf — honors the deadline
    /// (it already caps applies-per-frame internally); the headless/fake <see cref="IImageDecoder"/> leaves used by tests
    /// have no burst to bound, so they fall back to the plain drain. The host pumps unbounded (long.MaxValue, the
    /// parameterless overload above) every frame — the scroll rework removed the frame budget.</summary>
    public int Pump(long deadlineTicks)
    {
        _pumpCompleted = 0;
        if (_asyncUploads is { } q) DrainAsyncRejections(q);   // fold +1-frame async upload rejections before this pump's decodes
        DrainBakedBlurResults();
        if (_decoder is DecodeScheduler ds) ds.Pump(_onComplete, _onPixels, deadlineTicks);
        else _decoder.Pump(_onComplete, _onPixels);
        if (_pumpCompleted > 0) EvictToBudget();
        RetryPinnedExhausted();
        RestartPinnedLeftovers();
        ReclaimTombstonesIfDue();
        return _pumpCompleted;
    }

    /// <summary>T10: re-begin every PINNED canceled leftover (None/Canceled or Failed/Canceled with <c>Refs&gt;0</c> —
    /// see the class state table) whose last restart is at least <see cref="CanceledLeftoverRetryMs"/> old, at
    /// <see cref="ImagePriority.Visible"/> (never refused by the scheduler, so a pinned node converges instead of
    /// being dropped at its off-screen lane again), with no Request/Pin from the node. Real failures (NotFound,
    /// HttpError, Decode, GpuUpload, transient network kinds) are never touched — the predicate is Failure==Canceled.
    /// Cost: one float compare per pump while nothing is due; a due sweep walks only the listed ids (≤256), restarts at
    /// most <see cref="MaxLeftoverRestartsPerPump"/>, and prunes in place — no allocation. Only an overflowed list
    /// (more than 256 pinned leftovers at once) falls back to one scan of <c>_byId</c>, re-listing what it cannot
    /// restart yet.</summary>
    private void RestartPinnedLeftovers()
    {
        if (_clockMs < _leftoverDueMs) return;
        int budget = MaxLeftoverRestartsPerPump;
        float nextDue = float.PositiveInfinity;
        int n = _leftoverCount, w = 0;
        for (int r = 0; r < n; r++)
        {
            int id = _leftovers[r];
            if (!_byId.TryGetValue(id, out var e)) continue;   // reclaimed tombstone — nothing left to retry
            // Unpinned since it was listed (a later Pin re-Begins it through ShouldRestart), or no longer a leftover
            // (a Pin/Promote/Request restart got it going): drop it.
            if (e.Refs <= 0 || !IsCanceledLeftover(e)) { e.LeftoverListed = false; continue; }
            float due = e.LastRestartMs + CanceledLeftoverRetryMs;
            if (_clockMs >= due)
            {
                if (budget > 0)
                {
                    budget--;
                    RestartLeftover(id, e);
                    if (!IsCanceledLeftover(e)) { e.LeftoverListed = false; continue; }   // Pending now
                    due = e.LastRestartMs + CanceledLeftoverRetryMs;   // refused again: wait out a fresh window
                }
                else due = _clockMs;   // over this pump's cap — next pump
            }
            if (due < nextDue) nextDue = due;
            _leftovers[w++] = id;
        }
        // Defensive: anything appended while the loop ran (RestartDecode's refusal arm only lists an UNLISTED id, and
        // the loop's own id is still listed, so nothing is expected here) is kept, compacted behind the survivors.
        for (int r = n; r < _leftoverCount; r++)
        {
            int id = _leftovers[r];
            _leftovers[w++] = id;
            if (_byId.TryGetValue(id, out var t)) nextDue = MathF.Min(nextDue, t.LastRestartMs + CanceledLeftoverRetryMs);
        }
        _leftoverCount = w;
        _leftoverDueMs = nextDue;
        if (!_leftoverOverflow) return;
        // Overflow fallback: some pinned leftovers never made the list. Restart what is due (within the cap) and re-list
        // the rest — NoteCanceledLeftover re-arms the overflow (and keeps _leftoverDueMs finite) if there is still no room.
        _leftoverOverflow = false;
        foreach (var (id, e) in _byId)   // mutates Entry fields only, never _byId's structure (RetryPinnedExhausted's rule)
        {
            if (e.LeftoverListed || e.Refs <= 0 || !IsCanceledLeftover(e)) continue;
            if (budget > 0 && _clockMs >= e.LastRestartMs + CanceledLeftoverRetryMs)
            {
                budget--;
                RestartLeftover(id, e);
                if (!IsCanceledLeftover(e)) continue;
            }
            NoteCanceledLeftover(id, e);
        }
    }

    private void RestartLeftover(int id, Entry e)
    {
        if (Diag.CompiledIn && Diag.Enabled && DiagTraced(e.Key.Source))
            Diag.Event("img", $"leftover-restart id={id} src={DiagSourceTail(e.Key.Source)} from={e.State} " +
                $"refs={e.Refs} sinceLastRestartMs={_clockMs - e.LastRestartMs:0.#}");
        Diag.Count("media", "leftoverRestart");
        RestartDecode(id, e, ImagePriority.Visible);
    }

    /// <summary>E7: a PINNED (Refs&gt;0) entry that went <see cref="ImageFailureKind.GpuResourceExhausted"/> cannot
    /// self-heal through <see cref="Request"/>/<see cref="Pin"/> — a realized on-screen node calls neither again once
    /// mounted (see <see cref="ReRealizeAllResident"/>'s remark). This sweeps once every <see cref="RestartBackoffMs"/>
    /// while <see cref="_exhaustedPinnedSeen"/> is armed, restarting every matching entry at
    /// <see cref="ImagePriority.Visible"/> — <see cref="RestartDecode"/>'s own backoff predicate still gates each
    /// entry individually, this only supplies the tick that lets a pinned one retry with nobody re-rendering it.</summary>
    private void RetryPinnedExhausted()
    {
        if (!_exhaustedPinnedSeen || _clockMs - _lastExhaustedScanMs < RestartBackoffMs) return;
        _lastExhaustedScanMs = _clockMs;
        foreach (var (id, e) in _byId)
            if (e.Refs > 0 && e.State == ImageState.Failed && e.Failure == ImageFailureKind.GpuResourceExhausted)
                RestartDecode(id, e, ImagePriority.Visible);
        _exhaustedPinnedSeen = false;   // re-armed by OnDecodeComplete/DrainAsyncRejections if a retry fails exhausted again
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
                e.ReadyMs = _clockMs;   // E4: a quality upgrade re-lands content — grace protects it too
                _totalBakeReady++;
                _pumpCompleted++;
                NoteContentChanged(result.Id);
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
                e.ReadyMs = _clockMs;   // E4
                _readyCount++;          // E8: Pending → Ready
                _totalBakeReady++;
            }
            else { e.Bytes = 0; _totalBakeFailed++; }
            SyncLru(result.Id, e);
            _pumpCompleted++;
            NoteContentChanged(result.Id);
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
            _readyCount--;   // E8: leaving Ready
            SyncLru(r.Id, e);
            if (e.Refs > 0 && e.Failure == ImageFailureKind.GpuResourceExhausted)
            {
                // E7: a PINNED entry just went exhausted with nobody re-rendering it to trigger a retry — arm the
                // backoff-paced sweep (Pump's RetryPinnedExhausted) and restart its clock from THIS failure.
                _exhaustedPinnedSeen = true;
                _lastExhaustedScanMs = _clockMs;
            }
            if (wasActiveDeadline) RecomputeCrossfadeDeadline();
            _totalFailed++;
            NoteContentChanged(r.Id);
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
        if (ok)
        {
            e.WasReady = true;   // AFTER BeginReveal: a first-ever decode must still see WasReady==false there
            e.ReadyMs = _clockMs;   // E4: grace-window anchor for EvictToVramPressure
            _readyCount++;           // E8: Pending → Ready
        }
        else if (e.Refs > 0 && e.Failure == ImageFailureKind.GpuResourceExhausted)
        {
            // E7: a PINNED entry just went exhausted with nobody re-rendering it to trigger a retry — arm the
            // backoff-paced sweep (Pump's RetryPinnedExhausted) and restart its clock from THIS failure.
            _exhaustedPinnedSeen = true;
            _lastExhaustedScanMs = _clockMs;
        }
        e.W = w; e.H = h;
        // COMMITTED bytes, not decoded pixels — see CommittedBytesFor. Budgeting against the decoded figure let the
        // cache believe it was holding its cap while the GPU held roughly 3.5x that.
        e.Bytes = ok ? CommittedBytesFor(w, h) : 0;
        UsedBytes += e.Bytes;
        SyncLru(id, e);
        _pumpCompleted++;
        NoteContentChanged(id);

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
    public void TrimToBudget() { EvictToBudget(); ReclaimTombstonesIfDue(); }

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
    /// (<paramref name="preferDerived"/>) which are the cheapest to lose, and optionally excluding anything READY for
    /// less than <paramref name="minReadyAgeMs"/> (E4's grace window — see <see cref="ReadyGraceMs"/>) — and returns
    /// the bytes it freed, or 0 when nothing is evictable (everything left is pinned/visible/too-freshly-landed).
    /// O(1) and allocation-free: the victim is the head of an indexed LRU list (<see cref="SyncLru"/>). Shared by <see cref="EvictToBudget"/>
    /// (0 ms — it must always be able to reach its cap) and <see cref="EvictToVramPressure"/> (<see cref="ReadyGraceMs"/>).
    /// </summary>
    private long EvictOneLru(bool preferDerived, float minReadyAgeMs = 0f)
    {
            // Indexed: each list holds exactly the evictable entries in LRU order, so the victim is a list head — the
            // walk past the head only skips entries inside the VRAM path's freshly-landed grace window.
            int visited = 0;
            int victim = FirstEvictable(2, minReadyAgeMs, ref visited);
            if (!preferDerived)
            {
                int full = FirstEvictable(1, minReadyAgeMs, ref visited);
                if (victim == 0 || (full != 0 && _byId[full].LastUsed < _byId[victim].LastUsed)) victim = full;
            }
            LastEvictVisited = visited;
            if (victim == 0) return 0;   // everything left is pinned (on screen) / too fresh to shed — never evict it
            var e2 = _byId[victim];
            long freed = e2.Bytes;
            _readyCount--;   // E8: leaving Ready
            UsedBytes -= e2.Bytes;
            if (e2.Derived) DerivedUsedBytes -= e2.Bytes;
            bool activeDeadline = e2.Transition.Enabled && !float.IsNaN(e2.TextureMs)
                && e2.TextureMs + e2.RevealMs >= _clockMs;
            LruUnlink(victim, e2);
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

    // ── indexed LRU ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first entry of eviction list <paramref name="list"/> Ready for at least <paramref name="minReadyAgeMs"/>.</summary>
    private int FirstEvictable(int list, float minReadyAgeMs, ref int visited)
    {
        for (int id = _lruHead[list]; id != 0;)
        {
            var e = _byId[id];
            visited++;
            if (_clockMs - e.ReadyMs >= minReadyAgeMs) return id;
            id = e.LruNext;
        }
        return 0;
    }

    /// <summary>Links / unlinks <paramref name="e"/> so it sits in an eviction list exactly while it is evictable
    /// (Ready, unpinned). Called at every state/pin transition; O(1).</summary>
    private void SyncLru(int id, Entry e)
    {
        byte want = e.State == ImageState.Ready && e.Refs == 0 ? (byte)(e.Derived ? 2 : 1) : (byte)0;
        if (e.LruList == want) return;
        if (e.LruList != 0) LruUnlink(id, e);
        if (want != 0) LruAppend(id, e, want);
    }

    /// <summary>A use of an evictable entry: it becomes the most recently used of its list. O(1).</summary>
    private void LruTouch(int id, Entry e)
    {
        byte list = e.LruList;
        if (list == 0 || _lruTail[list] == id) return;
        LruUnlink(id, e);
        LruAppend(id, e, list);
    }

    private void LruAppend(int id, Entry e, byte list)
    {
        e.LastUsed = _clock++;
        e.LruList = list;
        e.LruNext = 0;
        e.LruPrev = _lruTail[list];
        if (e.LruPrev != 0) _byId[e.LruPrev].LruNext = id; else _lruHead[list] = id;
        _lruTail[list] = id;
    }

    private void LruUnlink(int id, Entry e)
    {
        byte list = e.LruList;
        if (list == 0) return;
        if (e.LruPrev != 0) _byId[e.LruPrev].LruNext = e.LruNext; else _lruHead[list] = e.LruNext;
        if (e.LruNext != 0) _byId[e.LruNext].LruPrev = e.LruPrev; else _lruTail[list] = e.LruPrev;
        e.LruPrev = e.LruNext = 0;
        e.LruList = 0;
    }

    // ── tombstone reclaim ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reclaims the tombstones (neither Ready nor Pending, unpinned) that nothing HOLDS any more. A tombstone exists so a
    /// retained node — a parked page's cover, a hold-last-good target — can re-pin its handle and recover without its
    /// URL; once no holder remains (the host's <see cref="SetHeldImageSource"/> enumeration: every scene node's paint,
    /// image effects and row cells, the reconciler's pending targets) it is dead weight that grew with every image ever
    /// seen. A derived entry that is kept holds its source. Runs only when tombstones outnumber both
    /// <see cref="ReclaimFloor"/> and the live set, so the O(entries) sweep amortizes to O(1) per eviction. Ids are never
    /// reused (monotonic), so a stale id anywhere can only miss, never alias another image.
    /// </summary>
    private void ReclaimTombstonesIfDue()
    {
        if (_collectHeld is null) return;
        int live = _readyCount + _pendingCount;
        if (TombstoneCount <= Math.Max(ReclaimFloor, live)) return;
        _held.Clear();
        _collectHeld(_held);
        foreach (var (id, e) in _byId)
            if (e.Derived && (e.Refs > 0 || e.State is ImageState.Pending or ImageState.Ready || _held.Contains(id)))
                _held.Add(e.SourceId);
        _reclaimScratch.Clear();
        foreach (var (id, e) in _byId)
            if (e.Refs == 0 && e.State is ImageState.None or ImageState.Failed && !_held.Contains(id))
                _reclaimScratch.Add(id);
        for (int i = 0; i < _reclaimScratch.Count; i++) ReclaimEntry(_reclaimScratch[i]);
        _reclaimScratch.Clear();
        _held.Clear();
    }

    private void ReclaimEntry(int id)
    {
        var e = _byId[id];
        LruUnlink(id, e);   // tombstones are never linked; defensive
        if (e.Derived)
        {
            if (_byDerivedKey.TryGetValue(e.DKey, out int keyed) && keyed == id) _byDerivedKey.Remove(e.DKey);
            if (_derivedBySource.TryGetValue(e.SourceId, out var dependents))
            {
                dependents.Remove(id);
                if (dependents.Count == 0) _derivedBySource.Remove(e.SourceId);
            }
        }
        else
        {
            if (_byKey.TryGetValue(e.Key, out int keyed) && keyed == id) _byKey.Remove(e.Key);
            string source = e.Key.Source;
            if (_sourceHead.TryGetValue(source, out int head))
            {
                if (head == id)
                {
                    if (e.PrevSameSource != 0) _sourceHead[source] = e.PrevSameSource;
                    else _sourceHead.Remove(source);
                }
                else
                {
                    for (int at = head; at != 0;)
                    {
                        var link = _byId[at];
                        if (link.PrevSameSource == id) { link.PrevSameSource = e.PrevSameSource; break; }
                        at = link.PrevSameSource;
                    }
                }
            }
            _derivedBySource.Remove(id);   // only ever empty here: a kept dependent would have held this source
        }
        _byId.Remove(id);
        NoteRecordingInputChanged();   // a snapshot that held this id must not be reused as-is
    }

    /// <summary>
    /// Weak-GPU VRAM-pressure relief (adreno-hang-fixes.md M5). The host samples the device's LOCAL-segment budget
    /// (<see cref="FluentGpu.Rhi.IGpuDevice.TryGetVramUsage"/>) and, gated by <c>VramShedPolicy</c>'s arm/disarm
    /// hysteresis + same-sample suppression + cooldown (E4 — the caller owns ALL of that; this method just executes
    /// when called), sheds unpinned image-cache LRU down toward the 0.85 soft line. Returns the bytes actually freed
    /// so the caller can feed <c>VramShedPolicy.NoteShed</c>.
    ///
    /// <para><b>Approximation.</b> The device figure (<paramref name="usedBytes"/> / <paramref name="budgetBytes"/>) is
    /// TOTAL VRAM — swapchain + every OpacityLayer RT + all textures — but this cache only tracks its OWN image bytes
    /// (<see cref="UsedBytes"/>). We cannot know how much of the overage is ours, so we shed our share: compute the
    /// overage above <c>budget*0.85</c> and evict image-cache LRU bytes to cover it, bounded by what the cache actually
    /// holds. Freeing our portion relieves proportional device pressure without ever evicting pinned/visible entries;
    /// the remaining overage (swapchain / RTs) is bounded by the other M5 levers (RT pool cap, depth-3 swapchain).</para>
    ///
    /// <para><b>Grace + floor.</b> A texture Ready for less than <see cref="ReadyGraceMs"/> is never a candidate — it
    /// may be the very landing that pushed VRAM over the arm ratio, and shedding it immediately reopens the loop this
    /// exists to break (evict → re-request next scroll tick → re-land → evict again). The shed target is also
    /// clamped to never go below half of what is actually RESIDENT right now — <c>Math.Min(_budgetBytes / 2,
    /// UsedBytes / 2)</c>, not a bare <c>_budgetBytes / 2</c>: this cache is very often far under its own budget (a
    /// small screen's worth of pinned covers against a 96 MB cap), and a floor stated purely in terms of the budget
    /// would then sit ABOVE everything the cache holds, so the shed loop's `while (UsedBytes > target)` never even
    /// starts — the floor would silently veto every legitimate shed instead of merely bounding a deep one. Taking the
    /// smaller of the two keeps the intent (don't walk a sustained device-wide overage — swapchain/RT growth, not
    /// image bytes — all the way to zero one frame at a time) without that trap. <see cref="EvictToBudget"/> (the
    /// ordinary byte-budget path, answering the CACHE's own cap) has neither restriction — 0 ms grace, no floor —
    /// because it must always be able to reach its cap.</para>
    /// </summary>
    public long EvictToVramPressure(long budgetBytes, long usedBytes)
    {
        if (budgetBytes <= 0) return 0;
        long softLine = (long)(budgetBytes * 0.85);
        long overage = usedBytes - softLine;
        if (overage <= 0) return 0;                     // already under the soft line — nothing to shed
        long target = UsedBytes - overage;              // shed at most our share of the overage…
        long floor = Math.Min(_budgetBytes / 2, UsedBytes / 2);
        if (target < floor) target = floor;             // …never below half the RESIDENT set (never above it either)
        long freedTotal = 0;
        while (UsedBytes > target)
        {
            bool preferDerived = DerivedUsedBytes > 0;  // blur/derived first — cheapest to lose, re-baked on demand
            long freed = EvictOneLru(preferDerived, ReadyGraceMs);
            if (freed == 0 && preferDerived) freed = EvictOneLru(false, ReadyGraceMs);   // derived all pinned → try full images
            if (freed == 0) break;                      // everything left is pinned/visible/too-freshly-landed
            freedTotal += freed;
        }
        ReclaimTombstonesIfDue();
        return freedTotal;
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
