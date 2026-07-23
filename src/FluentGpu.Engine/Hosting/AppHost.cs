using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal;
using FluentGpu.Reconciler;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;

namespace FluentGpu.Hosting;

public readonly record struct FrameStats(int DrawCommandCount, int ClicksHandled, long HotPhaseAllocBytes, bool Rendered)
{
    public int NodesVisited { get; init; }
    public int DrawNodeCount { get; init; }
    public int CulledNodeCount { get; init; }
    public int BlurCandidateCount { get; init; }
    public int BlurGroupCount { get; init; }
    public int BlurSuppressedByScrollCount { get; init; }
    public int BlurHoldCandidateCount { get; init; }
    public int EdgeFadeGroupCount { get; init; }
    public int SpansReused { get; init; }
    public int SpansRebased { get; init; }
    public int SpansReRecorded { get; init; }
    public int SpanBytesCopied { get; init; }
    public int NodesCulled { get; init; }
    public SpanReuseDisabledReason SpanReuseDisabledReasons { get; init; }
    // Per-frame layout-cost counters (FlexLayout diag; valid only when FG_LAYOUT_DIAG=1, else 0). MeasureCount/ArrangeCount
    // are total node visits across the frame's full + scoped + phase-7 reflow layout passes; TextShapeMisses is DirectWrite
    // re-shapes (measure-cache misses). A projected (Reveal/FLIP) size animation must keep these ~0 on every anim tick —
    // only the commit frame is large. The reflow-per-tick defect (backdrop-effects-animation §5.8) is exactly a nonzero here.
    public int MeasureCount { get; init; }
    public int ArrangeCount { get; init; }
    public int TextShapeMisses { get; init; }
    // Relayout-escape diagnostic (ALWAYS-ON, incl. Release): the number of dirty nodes this frame whose scoped-relayout
    // search (LayoutInvalidator.FindRelayoutRoot) walked a node at depth > 1 ALL the way to the scene root — i.e. found no
    // layout boundary, forcing a full-subtree relayout from the top. A sustained nonzero value during interaction means a
    // hot subtree is missing a fixed-size ClipToBounds boundary (or a `.Boundary()`); set FG_DIAG to log the offending node.
    // 0 on a well-firewalled tree (and on full-layout frames — the counter is a SCOPED-relayout metric).
    public int RootRelayoutEscapes { get; init; }
    public double Fps { get; init; }
    public double FrameMs { get; init; }
    public int ComponentsRendered { get; init; }
    // Always-on per-segment timing of the last Paint (ms): flush=reconcile/component-render, layout=FlexLayout,
    // anim=phase-7 ticks, record=SceneRecorder (+ text shaping), submit=command build + GPU submit + present. ~5
    // Stopwatch reads/frame, zero alloc — so a profiler/probe can attribute a frame-time spike to a phase without FG_ALLOC_DIAG.
    public double FlushMs { get; init; }
    /// <summary>Of <see cref="FlushMs"/>: wall time inside <c>_runtime.Flush()</c> (render-effects + bindings), including the
    /// same-frame second flush after pre-layout virtual realize. Always-on Stopwatch; 0 when nothing flushed.</summary>
    public double ReactiveFlushMs { get; init; }
    /// <summary>Of <see cref="FlushMs"/>: wall time inside the pre-layout <c>ReRealizeVirtuals()</c> call. Always-on; post-
    /// layout / scroll-catchup realize is charged to <see cref="RealizeCatchupMs"/> instead.</summary>
    public double VirtualRealizeMs { get; init; }
    public double LayoutMs { get; init; }
    public double AnimMs { get; init; }
    public double RecordMs { get; init; }
    public double SubmitMs { get; init; }
    // RecordMs sub-split (hitch attribution): the phase-7.5 image pump/tick and the phase-7.6 scroll re-realize
    // catch-up both run between tAnim and tRecord, so their cost was invisibly charged to "record" — a realize spike
    // on a fast fling read as SceneRecorder cost. RecordMs still covers the whole segment; these carve it up.
    public double ImagePumpMs { get; init; }
    public double RealizeCatchupMs { get; init; }
    // Submit sub-split (diagnostics for the #1 hotspot — GPU fence/present pacing is charged to "submit" on the UI thread
    // until the render-thread seam lands). FenceWaitMs = wall-time BLOCKED on the frame fence + present-latency waitable
    // INSIDE SubmitDrawList; PresentMs = the Present() call. cmdBuild = SubmitMs − FenceWaitMs − PresentMs is the real CPU
    // command-build cost. Lets a probe attribute a 27 ms "submit" spike to the stall vs the build without an external profiler.
    public double FenceWaitMs { get; init; }
    public double PresentMs { get; init; }
    // This frame actually submitted + presented (skip-submit did NOT elide it). A probe uses it to see how often a
    // "static" scene is force-presented anyway (a sustained loop animation marking TransformDirty defeats skip-submit).
    public bool Presented { get; init; }
    // Probe-only record-time scroll capture (all default 0/false; populated only when AppHost.ProbeLyricsViewport /
    // ProbeMainViewport are set). Captured INSIDE RunFrame right after record, BEFORE ClearTransformDirty wipes the
    // content-node TransformDirty bit — so a probe can read the exact state that drove SceneRecorder's DoF-defer decision
    // (the post-RunFrame read always shows content-dirty == 0, which is why the previous probe couldn't attribute it).
    public int LyricsScrollMode { get; init; }
    public bool LyricsUserScrollActive { get; init; }
    public bool LyricsContentDirtyAtRecord { get; init; }
    public int MainScrollMode { get; init; }
    public bool MainContentDirtyAtRecord { get; init; }
}

/// <summary>
/// Composition root + the single-UI-thread frame loop. Signals-first: a setState writes a signal that schedules ONLY
/// the owning component's render-effect (granular), and a bound high-frequency scalar (slider/scroll) writes a node
/// channel directly — a compositor-only frame with no render/reconcile/layout. The host drains the reactive runtime
/// once per frame (phase 3), runs (scoped) layout only when a reconcile/layout-bind changed something, then records.
/// </summary>
/// <summary>
/// One out-of-bounds popup window leased by an overlay (E4 windowed popups — WinUI windowed <c>CPopup</c>): a PAL
/// popup window + its own swapchain + its own DrawList, re-recorded each frame from the popup SUBTREE (which stays in
/// the single SceneStore — the recorder root-override). Exposed for headless verification: decode <see cref="DrawList"/>
/// with a scratch <c>HeadlessGpuDevice.SubmitDrawList</c> and assert against <see cref="BoundsDip"/>/<see cref="Window"/>.
/// </summary>
public sealed class PopupWindowSlot
{
    internal PopupWindowSlot(int token, IPlatformPopupWindow window, NodeHandle root, PopupWindowMaterial material)
    {
        Token = token;
        Window = window;
        Root = root;
        Material = material;
    }

    public int Token { get; }
    public IPlatformPopupWindow Window { get; }
    /// <summary>The overlay wrapper node whose subtree renders into this popup window.</summary>
    public NodeHandle Root { get; }
    /// <summary>Popup bounds in main-window DIP space (origin = main-window client (0,0)) — the record origin.</summary>
    public RectF BoundsDip { get; internal set; }
    /// <summary>Actual popup-window bounds in main-window DIP. OS-backed acrylic flyouts inflate this beyond
    /// <see cref="BoundsDip"/> so transparent shadow margins survive the separate HWND/swapchain clip.</summary>
    public RectF WindowBoundsDip { get; internal set; }
    public PopupWindowMaterial Material { get; }
    public ISwapchain? Swapchain { get; internal set; }
    /// <summary>The popup's own command stream, re-recorded each frame via <c>SceneRecorder.RecordSubtree</c>.</summary>
    public DrawList DrawList { get; } = new();
}

/// <summary>Which branch of <see cref="AppHost.RecommendedWaitMs"/> produced the last wait — the diagnostic that
/// distinguishes ambient software-pacing from display-rate free-run. <c>Ambient</c> means the loop was throttled to
/// <see cref="AppHost.AmbientAnimationFps"/> (the software 60 Hz cap); <c>DisplayRate</c>/<c>PaceAsync</c> mean the loop
/// ran at panel rate and any lock is downstream (Present/GPU miss-vblank). Surfaced via <see cref="AppHost.LastWaitKind"/>.</summary>
public enum HostWaitKind : byte
{
    Idle,            // -1: fully idle / minimized — block until a message
    Hud,             // 100: DynamicText-only readout throttle
    Baked,           // baked-blur queue cadence
    Ambient,         // AmbientFrameWaitMs — the software fps cap (the maximize-lock suspect)
    PaceSkipSubmit,  // AsyncDisplayPaceMs after an elided submit (sync path)
    PaceAsync,       // AsyncDisplayPaceMs — async present pace cap
    DisplayRate,     // 0: latency-sensitive / one-shot motion — sync present-throttled (panel rate)
}

public sealed class AppHost : IDisposable
{
    private readonly IPlatformApp _app;
    private readonly IPlatformWindow _window;
    private readonly IGpuDevice _device;
    private readonly ISwapchain _swapchain;
    private readonly Component _root;
    private readonly StringTable _strings;
    private readonly IFontSystem _fonts;   // retained so a detached child host (pop-out video window) can be constructed with the same font system
    private readonly FluentGpu.Media.VideoSurfaceRegistry _videoSurfaces = new();   // UI-thread video-surface intents, drained into IVideoPresenter at phase 11

    // Detached child hosts (the pop-out video mini-player): each is a full AppHost over its OWN top-level window +
    // composited swapchain + presenter, sharing this device/fonts/strings/images. Ticked by the loop via
    // TickDetachedHosts() on THIS (the parent's) UI+render thread. Empty on child hosts (no recursion).
    private readonly List<AppHost> _detachedHosts = new(1);
    private bool _isDetachedChild;   // true on a child host: it must not dispose the shared device, nor manage its own detached windows

    // E4 windowed out-of-bounds popups: one slot per leased popup window (see PopupWindowSlot).
    private readonly List<PopupWindowSlot> _popupWindows = new(2);
    private readonly List<NodeHandle> _popupSkipRoots = new(2);
    private readonly List<NodeHandle> _reuseBlockRoots = new(4);   // W5: connected-anim fly anchors whose span-reuse ancestor chains the recorder blocks (spatial scoping)
    private int _popupTokenSeq;

    private readonly SceneStore _scene = new();
    private readonly ReactiveRuntime _runtime = new();
    private readonly TreeReconciler _reconciler;
    private readonly FlexLayout _layout;
    private readonly LayoutInvalidator _invalidator;
    private readonly DrawList _drawList = new();
    private readonly SpanTable _spanTable = new();
    // Last image-content epoch included in a submitted frame. It does not invalidate retained spans; it only defeats
    // byte-hash submit elision for the one frame where a same-handle texture (for example a baked-blur upgrade) changed.
    private int _recordedImageContentEpoch;
    private bool _imageCrossfadeWasActive;
    // Render-thread seam (Cut A, submit-only; docs/plans/render-thread-seam-landing-plan.md · design/subsystems/threading-render-seam.md).
    // STEP 1 — single-thread pass-through: the UI records into _drawList, copies it into a render-readable arena, then
    // PUBLISHes + ACQUIREs it on THIS (UI) thread and submits from the acquired arena — byte-identical to a direct
    // submit, no behaviour/perf change. This only establishes the seam SHAPE so the later (soak-gated) render-thread
    // spawn — which moves submit/present/the GPU fence-wait stall off the UI thread — is an additive change, not a rewrite.
    private readonly Threading.SceneFramePublisher _renderSeam = new();
    // STEP 4 (force-sync): the dedicated render thread, constructed only when FG_RENDER_THREAD is set. null ⇒ the Step-1
    // single-thread inline pass-through (the default shipping path). It runs submit/present off the UI thread but the UI
    // still blocks on it (no async overlap until the soak-gated Step 5 flip).
    private readonly Threading.RenderThread? _renderThread;
    // Step 1 (ASYNC only): the image upload/evict handoff. Non-null ⇒ ImageCache hands GPU work to the render thread
    // through this queue (drained in SubmitPresentOnRenderThread before submit) instead of touching the device on the UI
    // thread. Null in default/force-sync — there the direct device sinks run with no cross-thread overlap.
    private readonly Threading.ImageUploadQueue? _imageQueue;
    private readonly Threading.BakedBlurQueue _bakedBlurQueue = new();
    // Step 4 (ASYNC): device-lost recovery rendezvous. Foreground recovery is synchronous and reuses RecoverDevice
    // directly; async parks the render loop and drives RecoverDevice through this coordinator.
    private readonly Threading.DeviceLostCoordinator? _deviceLost;
    private static readonly int s_forceLostFrame =
        int.TryParse(System.Environment.GetEnvironmentVariable("FG_FORCE_DEVICE_LOST"), out int __fl) && __fl > 0 ? __fl : -1;
    private static readonly bool s_dlTrace = Diag.EnvFlag("FG_DL_TRACE");   // device-lost recovery trace (diagnosis)
    private int _frameOrdinal;
    private const int DeviceLostFrameRingSize = 64;
    private readonly DeviceLostFrameSnapshot[] _deviceLostFrames = new DeviceLostFrameSnapshot[DeviceLostFrameRingSize];
    private int _deviceLostFrameSeq;
    private int _deviceLostRecoveryCount;
    // The effective async gate: s_renderAsync AND a REAL (non-headless) GPU backend. The render thread offloads real GPU
    // submit/present; a headless (test) backend has none, and its device seam methods (DrainImageJobs/RecoverDevice/…) are
    // no-ops — so headless always stays on the deterministic synchronous inline path regardless of the flag. Every async
    // branch keys off THIS, not s_renderAsync directly, so the VerticalSlice headless gates are unperturbed by the flip.
    private readonly bool _asyncActive;
    private readonly InputDispatcher _dispatcher;
    private readonly InputEventRing _ring = new();
    private readonly IFrameTimeSource _frameTime;
    private readonly bool _isHeadless;   // headless: FixedFrameTimeSource + FrameQpcSec stays 0 (resampler uses the latest sample, deterministic)
    private readonly AnimEngine _anim;
    private readonly ConnectedAnimation _connected;
    private readonly ScrollIntegrator _scrollAnim;   // the deterministic, engine-owned scroll integrator (wheel/touchpad/touch/spring) — the ONLY scroll source
    private readonly RepeatTicker _repeat;
    private readonly CaretBlinker _caretBlinker;
    private readonly ImageCache _images;
    private readonly Dictionary<NodeHandle, ProjCapture> _projectBefore = new();   // captured presented rects of BoundsAnimated nodes (FLIP "First")
    private readonly List<NodeHandle> _projectionSuppressionRoots = new();          // changed projected containers that own descendant motion this commit
    private readonly List<RenderContext> _pendingLayoutEffectContexts = new();
    private readonly List<RenderContext> _pendingPassiveEffectContexts = new();
    // Nonzero monotonic per-record epoch (§2.3/E9): baked into each freshly-walked cached-acrylic PushLayerCmd and carried
    // in FrameInfo.FrameEpoch, so the compositor trusts a layer's own-subtree damage carve-out only for THIS frame's data
    // (a span-copied layer keeps a stale epoch ⇒ safe fallback to the whole-frame damage union).
    private ulong _damageEpoch;

    /// <summary>FLIP "First" snapshot of a BoundsAnimated node, in PARENT-RELATIVE presented space (its own layout
    /// origin + in-flight LocalTransform). Parent-relative is what makes projections respond only to LOCAL movement:
    /// an ancestor reflow (an Expander reveal, a pane resize) shifts parent and child equally, the relative rect is
    /// unchanged, and the node rides the reflow RIGIDLY instead of re-FLIPping every frame. The parent handle is kept
    /// purely as a reparent guard — across different parents the relative frames are incomparable, so we snap.</summary>
    private readonly record struct ProjCapture(RectF Rel, NodeHandle Parent);

    private readonly record struct DeviceLostFrameSnapshot(
        int Seq, int FrameOrdinal, int RenderMode, int WidthPx, int HeightPx, float Scale, int Clicks, int PumpedEvents,
        bool KeepAlive, bool Resized, bool Reconciled, bool LayoutNeeded, bool TransformWrote, bool MaybeUnchanged,
        bool SkipSubmit, bool HasPendingUploads, int CommandCount, int CommandBytes, int SortKeyCount,
        DrawListOpcodeStats OpcodeStats, int NodesVisited, int DrawNodeCount, int CulledNodeCount,
        int BlurCandidateCount, int BlurGroupCount, int BlurSuppressedByScrollCount, int BlurHoldCandidateCount,
        int EdgeFadeGroupCount, RectF Damage, double FlushMs, double LayoutMs, double AnimMs, double RecordMs)
    {
        public readonly bool IsValid => Seq != 0;
    }

    // Ambient context signals (read via UseContext): published by the host, consumers subscribe granularly.
    private readonly Signal<object?> _viewportSig = new(default(Size2));
    private readonly Signal<object?> _viewportScaleSig = new(1f);   // Viewport.Scale ambient (DIP→device px)
    private readonly Signal<object?> _frameStatsSig = new(default(FrameStats));
    private readonly InputHooks _inputHooks = new();
    private readonly Signal<object?> _inputHooksSig;
    private readonly Signal<object?> _frameClockSig = new(0L);
    private long _frameClock;
    private readonly Signal<int> _imageEpoch = new(0);   // bumped on any image status change → re-renders UseImage consumers
    private readonly Signal<int> _dragEpoch = new(0);    // bumped each frame while a typed drag is live (+once on end) → UseDragState
    private bool _dragWasActive;
    private Size2 _lastViewportDip;
    // Window-visibility ambient (Activation.IsActive): false while minimized OR while the app has signalled a power
    // suspend (SetWindowActive(false)). UseIsActive AND-folds it with each component's KeepAlive-parked state. Written
    // on the minimize/restore EDGE in RunFrame (and by SetWindowActive); value-eq-gated, so a steady frame is a no-op.
    private readonly Signal<bool> _windowVisible = new(true);
    private bool _windowActiveApp = true;                // app-side power suspend/resume gate (AND-ed into _windowVisible)

    // Cross-thread UI dispatch (HostDispatch.Post / UsePost): worker / OS-callback / agile-COM threads enqueue
    // UI-thread actions and Wake() the loop; drained inside a reactive Batch at the top of each frame's flush so the
    // posted signal writes coalesce into one re-render. The engine-owned replacement for hand-rolled post-to-UI plumbing
    // (and for the UseContext(FrameClock.Tick)-to-drain anti-pattern that re-rendered every frame just to poll).
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _uiPosts = new();
    private readonly Signal<object?> _hostPostSig;
    private readonly Action<Action> _uiPoster;   // cached Post delegate (one instance) — ambient signal + HostDispatch.Current

    // Frame-clock timer queue (UseDebouncedValue/UseThrottledValue/UseTimeout/UseInterval). Drained at frame top INSIDE
    // the hot-phase window, before the reactive flush, so a fired timer's signal writes land in the SAME flush (the
    // DrainUiPosts rationale). Its clock is the wall clock for a real window (idle quiesce stays accurate across a
    // blocked WaitForWork — the animation frame delta is clamped and would drift) and the deterministic accumulated
    // frame delta (_frameClockMs) headless (the VerticalSlice gates ride it). NOT the media clock — playback position is
    // device-clock-derived and never routes through here (WS-Media non-goal).
    private readonly HostTimerQueue _timers;
    private readonly Action _drainTimers;   // cached (one instance) so the per-frame drain call allocates nothing
    private double _frameClockMs;           // monotonic accumulated frame delta — the headless timer clock (+= NextDeltaMs each Paint)
    // Post-input warm-cadence (research #10 — GPUI ProMotion re-ramp lesson): after the last input, keep the loop
    // rendering for WarmCadenceHoldMs before allowing full quiesce so a follow-up interaction pays no cold-start ramp.
    // On for a real window; OFF headless by default (a synthetic-input gate flips it via WarmCadenceEnabledForTest) so
    // every existing headless idle gate that injects input still quiesces exactly as before.
    /// <summary>Post-input warm-cadence hold (ms) — how long the loop keeps rendering after the last input before it is
    /// allowed to fully quiesce (research #10; default 1000). App-settable via <c>AppOptions.WarmCadenceMs</c>; 0 disables
    /// the hold entirely (each idle frame quiesces immediately). Only takes effect on a real window (headless gates flip
    /// <c>_warmCadenceEnabled</c> per-test).</summary>
    public float WarmCadenceHoldMs { get; set; } = 1000f;
    private bool _warmCadenceEnabled;
    private double _warmCadenceUntilMs;

    // ── FG_ALLOC_DIAG=1: once-per-second allocation/CPU attribution (stderr) ──
    // UI-thread bytes + ticks per frame segment (GetAllocatedBytesForCurrentThread deltas) and the process-wide
    // allocation total, so scroll-time churn can be pinned to a phase (or to a worker thread) without a profiler.
    private static readonly bool s_allocDiag = Diag.EnvFlag("FG_ALLOC_DIAG");
    // Append-only segment ids: existing numbering 0..9 is STABLE; SegDynText/SegPublish are the two new tail segments
    // (alloc-05: the dynamic-text update + frame-stat publish costs previously hid in "untracked").
    private const int SegPump = 0, SegDispatch = 1, SegFlip = 2, SegFlush = 3, SegLayout = 4, SegAnim = 5,
                      SegImages = 6, SegRecord = 7, SegSubmit = 8, SegEffects = 9, SegDynText = 10, SegPublish = 11, SegCount = 12;
    private static readonly string[] s_segNames = ["pump", "dispatch", "flip", "flush", "layout", "anim", "images", "record", "submit", "effects", "dyntext", "publish"];
    private readonly long[] _segBytes = new long[SegCount];
    private readonly long[] _segTicks = new long[SegCount];
    private long _diagUiBytes, _diagProcStart, _diagWindowStart;
    private int _diagFrames;
    private System.Text.StringBuilder? _diagSb;   // reused across reports (one alloc, not new-per-report) — FG_ALLOC_DIAG only

    // ── FG_WAKE_DIAG=1 / FG_MEM_DIAG=1 / FG_ALLOC_TYPES=1: opt-in diagnostics tools (each behind its own cached flag; nothing when off) ──
    private static readonly bool s_wakeDiag = Diag.EnvFlag("FG_WAKE_DIAG");
    private static readonly bool s_memDiag = Diag.EnvFlag("FG_MEM_DIAG");
    // The AllocTypeProfiler listener is constructed by the app layer (FluentApp.Run); the host only drives its
    // once-per-second report on the frame cadence (no extra timer thread). Reads are no-ops when not started.
    private static readonly bool s_allocTypes = Diag.EnvFlag("FG_ALLOC_TYPES");

    // ── FG_RESIZE_DIAG=1: per-tick timing of the keep-alive (modal move/size loop) paint, so smoothness is measurable. ──
    // One line per modal-loop tick to stderr — total/ensureSize/layout/submit+present ms — gated entirely so the normal
    // hot path and the zero-alloc gates are untouched (no work, no allocation, when the flag is off).
    private static readonly bool s_resizeDiag = Diag.EnvFlag("FG_RESIZE_DIAG");
    // ── FG_MOTION_DIAG=1: projected-motion (Reveal/FLIP) discrimination trace (why a structural transition snapped vs animated). ──
    // One [motion-diag] line per reconciling frame (capture summary) + one per captured node in ApplyProjections (branch OUTCOME)
    // + AnimEngine seed/snap lines + per-frame structural tick values. Entirely gated — no work, no allocation, when the flag is off.
    private static readonly bool s_motionDiag = Diag.EnvFlag("FG_MOTION_DIAG");
    // Render-thread seam rollout gate (Step 4/5), default OFF: FG_RENDER_THREAD spawns the fgpu-render thread and routes
    // submit/present onto it (FORCE-SYNC in Step 4 — the UI still blocks). The engine ships the proven single-thread
    // inline path until the seam.race soak is green; this flag is the staged flip mechanism, not a user quality knob.
    private static readonly bool s_renderThread = Diag.EnvFlag("FG_RENDER_THREAD");
    // Step 5 async flip — REVERTED TO DEFAULT-OFF (opt-in FG_RENDER_ASYNC=1). Async correctly decouples the UI from the
    // GPU (the back buffer renders byte-identically — proven by --screenshot), BUT presenting from the render thread to
    // the DComp-composited swapchain produces a DIM/wrong ON-SCREEN composite (a desktop capture shows it, though the
    // back-buffer CaptureBgra passes — the blind spot that hid it). Until that on-screen present path is fixed, async
    // ships OFF: the proven single-thread inline path renders correctly. The Step 1-4 machinery stays wired for opt-in
    // debugging. (Separately: the lyrics choppiness this was meant to fix is GPU-bound — the DoF blur exceeds the vblank —
    // which async would not have fixed regardless; that needs a DoF cost reduction, not a threading change.)
    private static readonly bool s_renderAsync = Diag.EnvFlag("FG_RENDER_ASYNC");
    private readonly WakeDiagnostics? _wakeDiag;
    private readonly MemCensus? _memCensus;

    /// <summary>MemCensus GPU-residency hook (FluentApp wires <c>D3D12Device.DiagResourceTotals</c>); headless leaves null.</summary>
    public Func<(long bytes, int count)>? GpuResources { get; set; }
    /// <summary>MemCensus GPU one-line detail hook (glyph/texture-store summary); headless leaves null.</summary>
    public Func<string>? GpuDetail { get; set; }

    // The bounded CPU pixel pool the async-upload sink copies decode pixels into (returned render-side via the queue). A
    // ctor-default keeps headless/census null-free; FluentApp replaces it with the SHARED pipeline pool before first
    // RunFrame, and the setter re-points the already-constructed async queue's BufferPool so both draw on one budget.
    private FluentGpu.Media.PixelBufferPool _pixelPool = new();

    /// <summary>The bounded CPU pixel pool for async-upload copies. Set to the pipeline-shared pool (the one the
    /// <c>DecodeScheduler</c> rents decode buffers from) BEFORE the first RunFrame so decode + upload draw on one
    /// retained-bytes budget; the setter re-points the async <c>ImageUploadQueue.BufferPool</c> if it already exists.</summary>
    public FluentGpu.Media.PixelBufferPool PixelPool
    {
        get => _pixelPool;
        set { _pixelPool = value; if (_imageQueue is not null) _imageQueue.BufferPool = value; }
    }

    // ── single-instance activation redirect (IPlatformApp.ActivationRedirected → app code) ──────────────────────────
    // The PAL raises IPlatformApp.ActivationRedirected on the UI thread when a second app launch is forwarded here (the
    // WM_COPYDATA path). The ctor stashes the payload and wakes a frame; Paint() drains it at the top and re-raises the
    // public event below — so app handlers run on the UI thread, inside the frame, free to write signals that re-render.
    private string? _pendingActivation;
    private Action<string>? _onActivationRedirected;   // cached subscription (unsubscribed in Dispose)
    private Action<RectF>? _onOccludedRectChanged;     // SIP OccludedRect → caret reflow (unsubscribed in Dispose)
    private bool _pendingSystemColors;                 // OS color-settings change (WM_SETTINGCHANGE) pending; drained at Paint top
    private Action? _onSystemColorsChanged;            // cached subscription (unsubscribed in Dispose)

    /// <summary>Raised on the UI thread when the OS color settings change (Windows app dark/light flip or accent change),
    /// delivered at the top of the next frame so handlers may freely mutate the theme / write signals. App code reacts by
    /// re-reading the OS state and calling <see cref="RequestThemeTransition"/> (typically only while it follows the OS).
    /// Wired from <see cref="FluentGpu.Pal.IPlatformApp.SystemColorsChanged"/>; never fires under the headless PAL.</summary>
    public event Action? SystemColorsChanged;

    /// <summary>
    /// Raised on the UI thread when a SECOND launch of a single-instance app is redirected to this running instance,
    /// carrying the new launch's activation payload (the deep-link URI, e.g. <c>wavee://callback?…</c>, or the empty
    /// string for a focus-only relaunch). Wired from <see cref="IPlatformApp.ActivationRedirected"/> and delivered at the
    /// top of the next frame, so handlers may freely mutate signals (a re-render is already scheduled). Set up by
    /// <c>FluentGpu.WindowsApi.Activation.SingleInstanceGate</c> on the sender side; never fires under the headless PAL.
    /// </summary>
    public event Action<string>? ActivationRedirected;

    // ── live re-theme (Tok.Use/SetAccent → animated in-place re-render, no remount) ──────────────────
    // A theme mutation bumps Tok.Epoch. Paint() detects the change at the top of the flush, re-renders every mounted
    // component in place (so each re-reads the new token set), and arms a cross-fade window around exactly that flush so
    // the fill/border/text color diffs animate. RequestThemeTransition is the explicit entry (app toggle / OS follow).
    private int _lastThemeEpoch;                 // last Tok.Epoch the host rethemed for (seeded just after the root mount)
    private float _pendingThemeMs = float.NaN;   // explicit RequestThemeTransition duration for this frame; NaN = none requested

    /// <summary>Host seam set by the windowing backend: re-apply the OS window material (DWM immersive-dark + Mica) when
    /// the theme flips. Invoked on the UI thread on every theme change with the new "is dark" flag. Headless leaves it null;
    /// the material flip is instant (the OS cannot cross-fade it) while the in-app content cross-fades.</summary>
    public Action<bool>? OnApplyThemeMaterial { get; set; }

    /// <summary>Request a live, animated theme switch: re-render every mounted component IN PLACE and cross-fade the
    /// resulting color diffs over <paramref name="ms"/> (default 250ms — WinUI ControlNormalAnimationDuration). Call AFTER
    /// mutating the theme (<c>Theme.Dark = …</c>, <c>Tok.Use</c>/<c>SetAccent</c>). Pass 0 to snap. UI-thread only; wakes an
    /// idle loop. Reachable from app code via the ambient <see cref="FluentGpu.Hooks.ThemeControl.Request"/> context.</summary>
    public void RequestThemeTransition(float ms = 250f) { _pendingThemeMs = ms; WakeFrame(); }

    private long Probe(int seg, long sinceBytes, long sinceTicks)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        long nowBytes = GC.GetAllocatedBytesForCurrentThread();
        _segBytes[seg] += nowBytes - sinceBytes;
        _segTicks[seg] += nowTicks - sinceTicks;
        return nowBytes;
    }

    // FG_RESIZE_DIAG: stopwatch ticks since <paramref name="sinceTicks"/> as milliseconds (modal-loop tick segment timing).
    private static double ElapsedMs(long sinceTicks) => (Stopwatch.GetTimestamp() - sinceTicks) * 1000.0 / Stopwatch.Frequency;
    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;   // FrameStats per-segment timing

    // FG_RESIZE_DIAG: one line per modal move/size-loop keep-alive tick — total paint, ensureSize (swapchain resize),
    // layout (flush/reconcile/relayout), and submit+present spans — so the live-resize cost split is measurable. Only
    // reached when (keepAlive && s_resizeDiag); the string interpolation here is the lone alloc and it's flag-gated off
    // on the normal hot path, so the zero-alloc gates are unaffected.
    private void ReportResizeTick(long frameStart, double ensureMs, double layoutMs, long submitStart,
                                  bool resized, string layoutPath, int componentsRendered,
                                  int nodesVisited, int drawCommands, long hotAlloc)
    {
        double submitMs = ElapsedMs(submitStart);
        double totalMs = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency;
        Console.Error.WriteLine(
            $"[FG_RESIZE_DIAG t={Environment.TickCount64}] tick total={totalMs:F2}ms ensureSize={ensureMs:F2}ms layout={layoutMs:F2}ms submit+present={submitMs:F2}ms " +
            $"resized={resized} path={layoutPath} comps={componentsRendered} nodes={nodesVisited} cmds={drawCommands} hotAlloc={hotAlloc}");
    }

    private void DiagMaybeReport()
    {
        long now = Stopwatch.GetTimestamp();
        if (_diagWindowStart == 0)
        {
            _diagWindowStart = now;
            _diagProcStart = GC.GetTotalAllocatedBytes(precise: false);
            return;
        }
        double sec = (now - _diagWindowStart) / (double)Stopwatch.Frequency;
        if (sec < 1.0) return;

        long proc = GC.GetTotalAllocatedBytes(precise: false);
        double total = (proc - _diagProcStart) / sec / 1024.0;
        long segSum = 0;
        foreach (long b in _segBytes) segSum += b;
        double ui = _diagUiBytes / sec / 1024.0;
        double untracked = (_diagUiBytes - segSum) / sec / 1024.0;
        double other = total - ui;

        var sb = _diagSb ??= new System.Text.StringBuilder(256);
        sb.Clear();
        sb.Append(CultureInfo.InvariantCulture, $"[allocdiag] total {total:0.0} KB/s | ui {ui:0.0} | other {other:0.0} | untracked {untracked:0.0} | frames {_diagFrames}");
        for (int i = 0; i < SegCount; i++)
        {
            double kb = _segBytes[i] / sec / 1024.0;
            double ms = _segTicks[i] * 1000.0 / Stopwatch.Frequency / sec;
            if (kb >= 0.05 || ms >= 0.05)
                sb.Append(CultureInfo.InvariantCulture, $" | {s_segNames[i]} {kb:0.0}KB {ms:0.00}ms");
        }
        Console.Error.WriteLine(sb.ToString());

        Array.Clear(_segBytes);
        Array.Clear(_segTicks);
        _diagUiBytes = 0;
        _diagFrames = 0;
        _diagWindowStart = now;
        _diagProcStart = proc;
    }

    /// <summary>Probe-only (default Null = off): a scroll viewport whose record-time ScrollMode / UserScrollActive /
    /// content-node TransformDirty are snapshotted into <see cref="FrameStats"/> each frame, captured BEFORE the per-frame
    /// ClearTransformDirty so the DoF-defer inputs are observable. Set by WaveeNavProbe's lyrics-advance probe.</summary>
    public NodeHandle ProbeLyricsViewport;
    public NodeHandle ProbeMainViewport;

    private void CaptureProbeScroll(NodeHandle vp, out int mode, out bool userScroll, out bool contentDirty)
    {
        mode = 0; userScroll = false; contentDirty = false;
        if (vp.IsNull || !_scene.IsLive(vp) || !_scene.HasScroll(vp)) return;
        ref var sc = ref _scene.ScrollRef(vp);
        mode = sc.Phase;
        userScroll = sc.UserScrollActive;
        var c = sc.ContentNode;
        contentDirty = !c.IsNull && _scene.IsLive(c) && (_scene.Flags(c) & NodeFlags.TransformDirty) != 0;
    }

    // Runs ON the fgpu-render thread (bound Render) when FG_RENDER_THREAD / FG_RENDER_ASYNC is set — the sole toucher of
    // the device/swapchain ComPtrs for submit+present in that mode. Reads the frame's bytes from the publisher's per-slot
    // arena (PickFreeSlot guarantees the UI is not writing that slot). Force-sync (Step 4) blocks the UI in DrainSync;
    // async (Step 5) presents on its own timeline. Device/swapchain CREATION + UploadImage staging + resize/device-lost
    // are still UI-side — the documented async residuals (landing plan §9); force-sync makes those splits safe meanwhile.
    /// <summary>Stop + join the fgpu-render thread so the UI thread becomes the SOLE GPU-ComPtr owner again — required
    /// before a one-shot UI-thread GPU op like <c>CaptureBgra</c> (--screenshot), which resets the command allocator +
    /// fence the render thread is otherwise using (the async capture race). No-op when no render thread; the host must
    /// not paint after this (Dispose's join is idempotent). This is the screenshot-path stand-in for the full async
    /// capture coordination (landing plan §9); it does not make windowed async safe (UploadImage/resize still race).</summary>
    public void QuiesceRenderThread() => _renderThread?.Dispose();

    private void SubmitPresentOnRenderThread(Threading.RenderFrame rf)
    {
        Threading.ThreadGuard.AssertRender();
        // Step 1 (async): stage uploads / free evictions on the render thread, BEFORE the submit opens its command list —
        // so a texture is resident before the draw that references it, and the store stays single-toucher (no lock).
        if (_imageQueue is { } q) _device.DrainImageJobs(q);
        try
        {
            if (rf.SuppressVsync) { _device.SuppressVsyncOnce(); _device.SuppressLatencyWaitOnce(); }
            _device.SubmitDrawList(_renderSeam.Bytes(rf), _renderSeam.SortKeys(rf), in rf.Submit);
            _swapchain.Present();
        }
        catch (System.Exception) when (_asyncActive)
        {
            // Step 4: a submit/present threw on the render thread. If the device is lost, record it (the UI recover gate
            // fires next frame) and SWALLOW — an unobserved background exception here would kill the process. A
            // non-device-loss throw is a genuine bug: rethrow so it isn't masked.
            if (!_device.NoteIfDeviceLost()) throw;
        }
    }

    private void RecoverDeviceAfterDump()
    {
        _deviceLostRecoveryCount++;
        DumpDeviceLostFrames(null, "async-render");
        _device.DumpDeviceLostDiagnostics(WriteDeviceLostLine);
        _device.RecoverDevice();
    }

    private bool TryRecoverForegroundDeviceLost(Exception ex, int clicks)
    {
        if (!_device.NoteIfDeviceLost()) return false;
        _deviceLostRecoveryCount++;
        DumpDeviceLostFrames(ex, "foreground");
        _device.DumpDeviceLostDiagnostics(WriteDeviceLostLine);
        _device.RecoverDevice();
        _scene.MarkAllPaintDirty();
        _needFullLayout = true;
        _lastPresentedDrawListHash = 0;
        _images.ReRealizeAllResident();
        _frameAfterPaint = true;
        LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, FrameMs = _frameMs };
        PublishFrameStats(LastStats);
        return true;
    }

    private void RememberDeviceLostFrame(int clicks, bool keepAlive, bool resized, bool reconciled, bool layoutNeeded,
                                         bool transformWrote, bool maybeUnchanged, bool skipSubmit,
                                         in SceneRecordStats recordStats, long frameStart, long tFlush, long tLayout,
                                         long tAnim, long tRecord)
    {
        int seq = ++_deviceLostFrameSeq;
        var size = _window.ClientSizePx;
        int mode = _asyncActive ? 2 : (_renderThread is null ? 0 : 1);
        _deviceLostFrames[(seq - 1) % DeviceLostFrameRingSize] = new DeviceLostFrameSnapshot(
            seq, _frameOrdinal, mode, (int)MathF.Round(size.Width), (int)MathF.Round(size.Height),
            _window.Scale, clicks, _tracePumpedEvents, keepAlive, resized, reconciled, layoutNeeded, transformWrote,
            maybeUnchanged, skipSubmit, _device.HasPendingUploads, _drawList.CommandCount, _drawList.Bytes.Length,
            _drawList.SortKeys.Length, _drawList.OpcodeStats, recordStats.NodesVisited, recordStats.DrawnNodeCount,
            recordStats.CulledNodeCount, recordStats.BlurCandidateCount, recordStats.BlurGroupCount,
            recordStats.BlurSuppressedByScrollCount, recordStats.BlurHoldCandidateCount,
            recordStats.EdgeFadeGroupCount, recordStats.Damage, ToMs(tFlush - frameStart),
            ToMs(tLayout - tFlush), ToMs(tAnim - tLayout), ToMs(tRecord - tAnim));
    }

    private void DumpDeviceLostFrames(Exception? ex, string path)
    {
        WriteDeviceLostLine($"[device-lost] path={path} backend={_device.BackendName} recoveries={_deviceLostRecoveryCount}" + (ex is null ? "" : $" exception={ex.GetType().Name}: {ex.Message}"));
        int count = Math.Min(_deviceLostFrameSeq, DeviceLostFrameRingSize);
        if (count == 0) { WriteDeviceLostLine("[device-lost] no frame breadcrumbs captured"); return; }
        WriteDeviceLostLine($"[device-lost] last {count} frame breadcrumbs (oldest to newest)");
        int start = _deviceLostFrameSeq - count + 1;
        for (int i = 0; i < count; i++)
        {
            var f = _deviceLostFrames[(start + i - 1) % DeviceLostFrameRingSize];
            if (!f.IsValid) continue;
            string mode = f.RenderMode == 2 ? "async" : (f.RenderMode == 1 ? "render-thread" : "foreground");
            WriteDeviceLostLine($"[device-lost] seq={f.Seq} frame={f.FrameOrdinal} mode={mode} size={f.WidthPx}x{f.HeightPx}@{f.Scale:0.##} clicks={f.Clicks} events={f.PumpedEvents} keepAlive={f.KeepAlive} resized={f.Resized} reconciled={f.Reconciled} layout={f.LayoutNeeded} xform={f.TransformWrote} unchanged={f.MaybeUnchanged} skip={f.SkipSubmit} uploads={f.HasPendingUploads}");
            WriteDeviceLostLine($"[device-lost]   draw cmds={f.CommandCount} bytes={f.CommandBytes} sort={f.SortKeyCount} nodes={f.NodesVisited}/{f.DrawNodeCount}/{f.CulledNodeCount} blur={f.BlurCandidateCount}/{f.BlurGroupCount}/{f.BlurSuppressedByScrollCount}/{f.BlurHoldCandidateCount} edgeFade={f.EdgeFadeGroupCount} damage=({f.Damage.X:0.#},{f.Damage.Y:0.#},{f.Damage.W:0.#},{f.Damage.H:0.#})");
            WriteDeviceLostLine($"[device-lost]   ms flush={f.FlushMs:0.###} layout={f.LayoutMs:0.###} anim={f.AnimMs:0.###} record={f.RecordMs:0.###} ops={f.OpcodeStats}");
        }
    }

    private static void WriteDeviceLostLine(string line)
    {
        if (Diag.Sink is { } sink) sink(line);
        else Console.Error.WriteLine(line);
    }

    private bool _frameNeeded = true;        // a frame is required (reactive work pending, input, resize, …)
    private bool _frameAfterPaint;           // a wake arrived during paint → run another frame
    private bool _needFullLayout = true;     // first frame / resize / DPI / root structural change
    private bool _everLaidOut;               // suppress FLIP capture until the first layout (freshly-mounted nodes have no "before")
    private bool _wasMinimized;              // previous frame's minimize state — the restore EDGE forces a repaint
    private bool _inPaint;
    private Size2 _lastSize;
    private float _lastScale;
    private readonly long[] _presentTimes = new long[240];
    private int _presentTimeNext;
    private int _presentTimeCount;
    private double _fps;
    private double _frameMs;
    private const double FpsWindowSeconds = 1.0;

    // Ambient-animation frame-rate cap (FG_ANIM_FPS env, default 30 Hz). 0 is the explicit diagnostic/app override for
    // UNCAPPED/display-rate ambient motion; a positive cap paces perpetual loops (a spinner, skeleton shimmer,
    // equalizer/media playhead, reveal fade, implicit brush transition, caret blink) where a sub-refresh rate is
    // imperceptible and idles the CPU. WARNING: a positive cap BELOW the panel's refresh BEATS against the vsync-locked
    // present (the software wait stacks onto the vblank quantization), so e.g. a 60 cap on a 120 Hz panel reads ~40–60,
    // not a clean 60. Latency-SENSITIVE motion (scroll/hover/press/drag/repeat — motion the user actively drives) is
    // exempt and always runs at display rate; and input/worker-posts wake the loop instantly regardless of the wait, so
    // the cap NEVER adds input latency.
    private long _lastFrameStartTicks;
    // Pacing → timestep coupling (fps consistency). The wait the loop used to pace INTO the current frame: 0 = display
    // rate; >0 = ambient-throttled / HUD; -1 = blocked idle. A non-zero value means the frame clock's pending delta is a
    // STALE throttle/idle gap, not a real render interval — so Paint resyncs the clock before the anim tick when this
    // frame drives interactive or one-shot motion, killing the first-frame lurch on a scroll-start or a connected fly.
    private int _lastWaitMs;
    private HostWaitKind _lastWaitKind;   // which RecommendedWaitMsCore branch produced _lastWaitMs (present/pacing diagnosis)
    private int _traceGc0, _traceGc1, _traceGc2;   // GC collection counts at the last note-113 gap sample (hitch attribution)
    // Post-scroll grace window: keep display-rate pacing for a short tail after the last scroll-active frame so the eased
    // settle + any in-flight art reveal finish smoothly instead of snapping to the 30 Hz ambient cadence mid-motion.
    // 0.25s (was 0.15): a slow wheel-notch cadence (~1 notch / 300-500ms) over an ambient loop (skeleton shimmer) kept
    // falling out of the shorter grace between notches — a 30Hz↔display-rate oscillation felt as a per-notch lurch.
    private long _scrollGraceUntil;
    private static readonly long ScrollGraceTicks = (long)(0.25 * Stopwatch.Frequency);
    // One-bit latch: did ANY viewport's scroll offset actually advance LAST frame (ScrollIntegrator.AnyOffsetWroteThisFrame,
    // captured right after the phase-7 scroll tick)? Read at the TOP of the next Paint — before FLIP capture — to gate the
    // MotionSuppressionSource.Scroll layout-transition suppression on REAL offset motion, not merely the hold window.
    private bool _anyOffsetWroteLastFrame;
    private long _selfBlurHoldUntil;
    private static readonly long SelfBlurHoldAfterScrollTicks = (long)(0.12 * Stopwatch.Frequency);
    private long _mainScrollHoldUntil;   // any-viewport user scroll — apps peek via Reconciler.PeekMainScrollBusy
    private static readonly long MainScrollHoldTicks = (long)(0.45 * Stopwatch.Frequency);
    public int AmbientAnimationFps { get; set; } = s_ambientFpsDefault;
    private static readonly int s_ambientFpsDefault = ReadAmbientFps();
    private static int ReadAmbientFps() => int.TryParse(Environment.GetEnvironmentVariable("FG_ANIM_FPS"), out var v) && v >= 0 ? v : 30;
    // FG_ADAPTIVE_FPS governor (default off): when the GPU genuinely cannot sustain the panel rate at the current size
    // (smoothed fence-wait over the ~120Hz budget — e.g. a maximized frame that rasters in ~14ms), pace CONTINUOUS
    // animation (playhead/shimmer) to the ambient cap instead of free-running the loop into vblank-misses. A steady 60
    // beats a jittery 60 and halves GPU/power; it NEVER engages for latency-sensitive frames (no added input/scroll
    // latency) and routes through the Resync-exempt AmbientFrameWaitMs so it can't trip the frozen-anim clock guard.
    // DEFAULT ON (opt out with FG_ADAPTIVE_FPS=0): on a fast GPU the EMA stays under budget so it NEVER engages — a no-op;
    // it only acts when the GPU is genuinely bound, turning a thrashing 60 into a steady one. Escape hatch keeps it safe.
    private static readonly bool s_adaptiveFps = Environment.GetEnvironmentVariable("FG_ADAPTIVE_FPS") is not ("0" or "false" or "FALSE" or "off");
    private double _gpuBoundEma;   // smoothed recent GPU fence-wait (ms); governor input
    private const double GpuBoundBudgetMs = 10.0;   // sustained fence-wait above this ⇒ can't hold 120 (8.3ms) → pace to ambient
    // The governor NEVER paces these: genuine interactions (would add input/scroll latency) + active video (needs the panel
    // rate). It DOES pace art-reveal crossfades / one-shot transitions / ambient loops when GPU-bound (a 60Hz crossfade is
    // imperceptible, and the GPU can't do better than ~60 at that size anyway). Narrower than LatencySensitiveWake — which
    // includes the Image* bits — so the governor reliably engages during maximized playback where those bits stay set.
    private const WakeReasons GovernorNeverPace =
        WakeReasons.Interact | WakeReasons.ScrollAnim | WakeReasons.Repeat |
        WakeReasons.DragActive | WakeReasons.DragDropWork | WakeReasons.GestureHold | WakeReasons.TouchPress |
        WakeReasons.VideoPresenting;
    private const WakeReasons LatencySensitiveWake =
        WakeReasons.Interact | WakeReasons.ScrollAnim | WakeReasons.Repeat |
        WakeReasons.DragActive | WakeReasons.DragDropWork | WakeReasons.GestureHold | WakeReasons.TouchPress |
        // Album-art reveals (decode → crossfade) fire DURING and right after a homepage scroll, and they are transient,
        // user-visible motion — keep them at the display rate instead of letting the ambient cap drop the reveal to 30 Hz
        // the instant the fling settles (a driver of the "scroll feels 24 fps then 120 fps" inconsistency). Both bits
        // clear the moment decode/reveal finishes, so this never holds the loop awake the way a perpetual loop would.
        WakeReasons.ImageCrossfades | WakeReasons.ImagesPending | WakeReasons.ImageReady |
        // Active video presentation is DISPLAY-rate motion (a playing video advances every refresh) — exempt it from the
        // 30 Hz ambient cap so playback runs at the panel's full frame rate, not the ambient-throttled cadence.
        WakeReasons.VideoPresenting;
    // Modal-loop keep-alive paints must still run when any of these wake bits are set — even if ambient animation is
    // also live (playback seek ticker). Without this mask the InModalLoop+AnimIsAmbient bail swallowed warming virtual
    // lists mid-drag (detail-resize-flicker fix).
    private const WakeReasons ModalLoopEssentialWake =
        WakeReasons.FrameNeeded | WakeReasons.RuntimePending | WakeReasons.ScrollAnim |
        WakeReasons.DragDropWork | WakeReasons.DragActive | WakeReasons.GestureHold | WakeReasons.TouchPress |
        WakeReasons.PopupAnim | WakeReasons.ImagesPending | WakeReasons.ImageReady | WakeReasons.ImageCrossfades | WakeReasons.Orphans |
        // A video presenting under a modal/seek loop must keep pumping so the frame keeps advancing.
        WakeReasons.VideoPresenting |
        // A due frame-clock timer (a debounce/timeout/interval) must still fire while the user drags/resizes the window.
        WakeReasons.Timer;
    private static bool OnlyAmbientWakeReasons(WakeReasons reasons) => (reasons & ModalLoopEssentialWake) == 0;
    // Dynamic-text (HUD) intern-on-change cache, indexed by (int)DynamicTextKind (None..FrameMs = 0..5). Each slot
    // holds the last DISPLAYED quantized value (the int fps / int cmd|draw|cull / 0.1-rounded ms — exactly the display
    // granularity) and the StringId it interned to (the host holds ONE ref per cached id). When a kind's quantized
    // value is unchanged we reuse the cached id with no ToString and no Intern — so a jittering readout that rounds to
    // the same number produces zero string churn and burns no new ids; when ALL five are unchanged the per-node scan
    // is skipped entirely. Sentinel _dynTextQuant=long.MinValue ⇒ "not computed yet" (first frame always interns).
    private readonly long[] _dynTextQuant = InitDynTextQuant();
    private readonly StringId[] _dynTextId = new StringId[6];
    private static long[] InitDynTextQuant() { var a = new long[6]; Array.Fill(a, long.MinValue); return a; }
    private static ColorF Clear => Theme.WindowBackground;

    public SceneStore Scene => _scene;
    public AnimEngine Animation => _anim;
    /// <summary>The host-owned video-surface intent buffer (published on <c>VideoCompositor.Current</c>). A media player
    /// façade writes surface rect/visibility/handle here; the host drains it into the render-thread presenter at phase 11.</summary>
    public FluentGpu.Media.VideoSurfaceRegistry VideoSurfaces => _videoSurfaces;

    // ── detached video window (the pop-out mini-player) ──────────────────────────────────────────────────────────────

    /// <summary>Open a detached, movable/resizable, (by default) always-on-top top-level window hosting
    /// <see cref="DetachedWindowRequest.Content"/> in its OWN composited window + AppHost + swapchain + video presenter.
    /// Reuses the full frame loop (this is a real second AppHost sharing the device/fonts/strings/images), ticked by the
    /// parent loop on the same UI+render thread via <see cref="TickDetachedHosts"/>. Returns null when unavailable: a
    /// child host (no recursion), headless, the async render path (a second UI-thread submit source — matches the popup
    /// gate), or a backend without secondary swapchains. Host-wired to <c>InputHooks.OpenDetachedWindow</c>.</summary>
    public IDetachedVideoWindow? OpenDetachedWindow(DetachedWindowRequest request)
    {
        if (_isDetachedChild || _isHeadless || _asyncActive || !_device.SupportsSecondarySwapchains || request.Content is null)
            return null;
        var desc = new WindowDesc(request.Title, request.InitialSizeDip, _window.Scale, Composited: true);
        var win = _app.CreateWindow(desc);
        var child = new AppHost(_app, win, _device, _fonts, _strings, request.Content, images: _images,
            compositeSwapchain: true, isDetachedChild: true);
        win.Show();
        if (request.AlwaysOnTop) win.SetTopmost(true);
        _detachedHosts.Add(child);
        WakeFrame();
        return new DetachedWindowHandle(this, child, win);
    }

    /// <summary>Tick every live detached child host one frame (called by the loop right after the parent's own
    /// <c>RunFrame</c>, same thread). Reaps a window the user closed (dispose + remove). No-op with no detached windows.</summary>
    public void TickDetachedHosts()
    {
        for (int i = _detachedHosts.Count - 1; i >= 0; i--)
        {
            var child = _detachedHosts[i];
            if (child._window.IsClosed) { _detachedHosts.RemoveAt(i); child.Dispose(); continue; }
            child.RunFrame();
        }
    }

    /// <summary>The loop's wait, folded across this host and every detached child (so a playing pop-out keeps the loop at
    /// display rate even while the main window is idle/minimized). Calls <see cref="RecommendedWaitMs"/> (preserving its
    /// LastWaitKind/Ms side effects for logging), then combines each child's recommended wait.</summary>
    public int WaitMsWithDetached()
    {
        int w = RecommendedWaitMs();
        for (int i = 0; i < _detachedHosts.Count; i++)
            w = CombineWait(w, _detachedHosts[i].RecommendedWaitMs());
        return w;
    }

    // -1 = "block until a message" (no preference); any finite wait wins; min of two finite waits.
    private static int CombineWait(int a, int b) => a < 0 ? b : b < 0 ? a : Math.Min(a, b);

    /// <summary>Probe/diagnostic: count of live detached video windows.</summary>
    public int DetachedWindowCount => _detachedHosts.Count;

    private sealed class DetachedWindowHandle : IDetachedVideoWindow
    {
        private readonly AppHost _parent;
        private readonly AppHost _child;
        private readonly IPlatformWindow _window;
        public DetachedWindowHandle(AppHost parent, AppHost child, IPlatformWindow window)
        { _parent = parent; _child = child; _window = window; }
        public bool IsOpen => !_window.IsClosed && _parent._detachedHosts.Contains(_child);
        public void SetTopmost(bool topmost) => _window.SetTopmost(topmost);
        public void SetBounds(RectF outerBoundsPx) => _window.SetBoundsPx(outerBoundsPx);
        public void Close() => _window.CloseWindow();   // WM_CLOSE → IsClosed → reaped by TickDetachedHosts
    }

    /// <summary>Probe/diagnostic only: a live shared-element (connected-animation) key, so a harness can trigger a REAL Hero fly.</summary>
    public string? FirstMorphKey => _connected.FirstTaggedKey;
    /// <summary>Probe/diagnostic only: collect distinct live <c>pl:</c> shared-element keys (home cards) for fresh-page fly measurement.</summary>
    public void CollectMorphKeys(System.Collections.Generic.List<string> into) => _connected.CollectTaggedKeys(into);

    /// <summary>The input dispatcher. Exposed for the validation.md §12.6 arena-determinism gate (the harness attaches a
    /// gesture-arena recorder to <c>Input.Arena</c> and reads the resolution trace after a scripted sequence). The
    /// dispatcher's hot APIs are already public; the arena seam it surfaces is <c>internal</c> to the Input assembly.</summary>
    public InputDispatcher Input => _dispatcher;
    public FrameStats LastStats { get; private set; }
    public bool HasActiveWork => ComputeWakeReasons() != WakeReasons.None;

    // Async UI-loop pace cap (~142fps). In the SYNC path, latency-sensitive frames returned a 0 wait and Present blocked
    // the UI thread at vsync — THAT is what paced the loop. Under async Present is off the UI thread, so a 0 wait
    // free-spins the loop (100k+ fps, pegging a core → thermal/scheduling contention that makes the render thread's
    // presents irregular = judder). This cap replaces the lost vsync throttle; WaitForWork still returns EARLY on input
    // (latency unchanged), and the render coalesces (DropOldest) any over-production on a panel slower than ~142Hz.
    private const int AsyncDisplayPaceMs = 7;

    /// <summary>Render-thread present progress (last presented publish-seq) — 0 when there is no render thread. The delta
    /// over wall-time is the ACTUAL on-screen frame rate under async (which the UI-thread FrameMs cannot report, since
    /// submit/present are off-thread). Diagnostic (FG_FPS_LOG).</summary>
    public ulong RenderPresentSeq => _renderThread?.PresentAck ?? 0;
    /// <summary>Wall-time the render thread most recently BLOCKED on the GPU (frame fence + present latency) inside its
    /// submit — the real render-side cost async hides from FrameMs. High + climbing ⇒ GPU-bound. Diagnostic (FG_FPS_LOG).</summary>
    public double LastGpuFenceWaitMs => _device.LastFenceWaitMs;
    /// <summary>Diagnostic (FG_GPU_TIMING=1): the TRUE on-GPU raster time (ms) of the most recent frame, from a whole-frame
    /// timestamp-query pair (lags one frame). Unlike <see cref="LastGpuFenceWaitMs"/> this excludes the vblank/latency wait,
    /// so it says whether a maximized 60fps lock is GPU-fill-bound (render ≳ refresh budget) or vblank-quantized. 0 when off.</summary>
    public double LastGpuRenderMs => _device.LastGpuRenderMs;
    /// <summary>Diagnostic (FG_GPU_TIMING=1): the scene-raster portion of <see cref="LastGpuRenderMs"/> (excl. uploads/baked-blur)
    /// — when this dominates and exceeds the refresh budget, the maximize lock is content fill/overdraw. 0 when off.</summary>
    public double LastGpuSceneMs => _device.LastGpuSceneMs;

    /// <summary>The message-loop wait timeout (ms) for the NEXT pump: how long to block in <c>WaitForWork</c> before
    /// running another frame. Computes the wake mask ONCE and paces by it:
    /// <list type="bullet">
    /// <item>None ⇒ -1: fully idle, block until an input/paint message arrives (0% CPU).</item>
    /// <item>minimized ⇒ -1 (regardless of the mask): a minimized window paints nothing; only the restore message matters.</item>
    /// <item>DynamicText is the ONLY set bit ⇒ 100: the on-screen fps/draw-count HUD is a READOUT, not an animation —
    ///   a 10 Hz refresh is imperceptible and idles the CPU at ~0% instead of running record+present at the display rate.</item>
    /// <item>otherwise ⇒ 0: real animation/scroll/decode/drag work in flight — pace at the display rate (present-throttled).</item>
    /// </list>
    /// <c>WaitForWork</c> returns EARLY on any input message, so responsiveness is identical at every timeout. One
    /// consequence is honest: when the HUD is the only wake source its own fps line then reads the throttled cadence
    /// (~10), and it reports the real frame rate again the instant anything else animates.</summary>
    public int RecommendedWaitMs()
    {
        int w = ClampWaitToTimers(RecommendedWaitMsCore());
        _lastWaitMs = w;   // remembered so Paint can detect a throttle/idle → display-rate step-up and resync the frame clock
        return w;
    }

    /// <summary>The wait (ms) the loop last chose to pace INTO the current frame (the raw <see cref="RecommendedWaitMs"/>
    /// value, timer-clamped): 0 = display-rate, &gt;0 = ambient/HUD throttle, -1 = blocked idle. Diagnostic (FG_FPS_LOG).</summary>
    public int LastWaitMs => _lastWaitMs;
    /// <summary>Which <see cref="RecommendedWaitMsCore"/> branch produced <see cref="LastWaitMs"/> — the signal that tells a
    /// maximize/60fps investigation whether the loop is <see cref="HostWaitKind.Ambient"/>-throttled (software cap) or running
    /// at display rate (a lock is then downstream in Present/GPU). Diagnostic (FG_FPS_LOG).</summary>
    public HostWaitKind LastWaitKind => _lastWaitKind;

    /// <summary>Shorten an IDLE/throttled wait so the loop wakes when the earliest frame-clock timer is due (a pending
    /// timer keeps the loop from over-sleeping past its fire). A display-rate wait (0 sync / <see cref="AsyncDisplayPaceMs"/>
    /// async — animation/scroll live) is left untouched: it already drains the timer next frame, and shortening it to a
    /// sub-frame value would spuriously trip the frame-clock step-up Resync (the frozen-one-shot-anim bug class). No armed
    /// timer ⇒ the wait is unchanged (a fully idle loop stays -1 → 0% CPU).</summary>
    private int ClampWaitToTimers(int w)
    {
        if (w == 0 || w == AsyncDisplayPaceMs) return w;
        if (!_timers.TryPeekEarliest(out double due)) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(0.0, due - _timers.NowMs));
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    private int RecommendedWaitMsCore()
    {
        // Feed the FG_ADAPTIVE_FPS governor: smooth the render-thread/UI GPU fence-wait so a sustained over-budget stretch
        // (a maximized fill-bound frame) is detected without one-frame jitter flipping the pacing. Cheap; only when armed.
        if (s_adaptiveFps) _gpuBoundEma = _gpuBoundEma * 0.85 + _device.LastFenceWaitMs * 0.15;
        if (IsMinimized) { MaybeTrimOnIdle(); _lastWaitKind = HostWaitKind.Idle; return -1; }   // nothing to paint; only the restore message wakes us (see RunFrame's minimize gate)
        WakeReasons r = ComputeWakeReasons();
        if (r == WakeReasons.None) { MaybeTrimOnIdle(); _lastWaitKind = HostWaitKind.Idle; return -1; }   // fully idle: trim the slab tail once, then block until a message arrives
        if (r == WakeReasons.DynamicText) { _lastWaitKind = HostWaitKind.Hud; return 100; }   // HUD-only: 10 Hz readout, ~0% idle CPU
        if ((r & ~(WakeReasons.BakedBlurPending | WakeReasons.DynamicText)) == 0)
        {
            int bakedWait = _bakedBlurQueue.RecommendedWaitMs;
            _lastWaitKind = (r & WakeReasons.DynamicText) != 0 ? HostWaitKind.Hud : HostWaitKind.Baked;
            return (r & WakeReasons.DynamicText) != 0
                ? (bakedWait < 0 ? 100 : Math.Min(100, bakedWait))
                : bakedWait;
        }
        // A live scroll arms a short display-rate grace so the eased settle + any in-flight art reveal finish at the
        // display rate instead of snapping back to the 30 Hz ambient cadence the instant the fling drops below cutoff.
        long now = Stopwatch.GetTimestamp();
        if ((r & WakeReasons.ScrollAnim) != 0) _scrollGraceUntil = now + ScrollGraceTicks;
        // Ambient-only animation (no latency-sensitive interaction live, and any AnimEngine activity is loop-only — a
        // spinner/shimmer, NOT a one-shot transition mid-flight): pace to AmbientAnimationFps instead of the full
        // display refresh. A real input/post still wakes WaitForWork early, so this paces only the autonomous tick.
        // The cap ALSO defers through the 0.45s post-scroll hold (_mainScrollHoldUntil, refreshed at the phase-7 scroll
        // tick): slow wheel-notch scrolling over an ambient loop (skeleton shimmer) settles between notches, and without
        // the hold each notch stepped 30Hz→display-rate→30Hz — the step-up Resync at ApplyProjections' frame-clock guard
        // then dropped a stale ~34ms delta per notch, felt as a cadence lurch. Holding display rate through the whole
        // interaction keeps the clock monotonic; the cap resumes ~0.45s after the last real user-scroll frame.
        if (AmbientAnimationFps > 0 && (r & LatencySensitiveWake) == 0 && AnimIsAmbient()
            && now >= _scrollGraceUntil && now >= _mainScrollHoldUntil)
        {
            MaybeTrimOnIdle();   // #10: playback/ambient never reaches WakeReasons.None, so trim the slab tail here too (30s-cadence-gated)
            _lastWaitKind = HostWaitKind.Ambient;
            return AmbientFrameWaitMs();
        }
        // FG_ADAPTIVE_FPS governor: the animation is NOT ambient-classified (e.g. a one-shot transition or the smooth
        // playhead), but the GPU can't sustain the panel rate at this size — running full-rate just thrashes into
        // vblank-misses. Pace to the ambient cap for a STEADY sustainable cadence. Same latency-sensitive + scroll-hold
        // guards as the ambient branch (never touches interaction/scroll), and the same Resync-exempt wait.
        if (s_adaptiveFps && AmbientAnimationFps > 0 && (r & GovernorNeverPace) == 0
            && _gpuBoundEma > GpuBoundBudgetMs && now >= _scrollGraceUntil && now >= _mainScrollHoldUntil)
        {
            _lastWaitKind = HostWaitKind.Ambient;
            return AmbientFrameWaitMs();
        }
        // Skip-submit pacing floor: an elided submit skips Present — the sync path's ONLY pacer — so a scroll-armed-
        // but-unchanged stretch (a held/stuck band, a spring tail, the 2s scrollbar idle-hide dwell) would otherwise
        // free-run the loop at CPU speed re-recording a byte-identical scene (measured on-device: ~785 fps, a full
        // core, for the whole armed window). Pace those frames at AsyncDisplayPaceMs — the same constant the async
        // path returns, deliberately: it is exempt from the NextDeltaMs Resync guard, so the animation clock stays
        // monotonic (a novel wait value here would zero-dt every animating frame — the frozen one-shot-anim bug
        // class). Input still ends the wait immediately (WaitForWork is MsgWait-based), so nothing gains latency;
        // the first frame that actually changes pixels submits, and the next wait returns to 0 (present-throttled).
        if (!_asyncActive && _lastFrameSkippedSubmit) { _lastWaitKind = HostWaitKind.PaceSkipSubmit; return AsyncDisplayPaceMs; }
        _lastWaitKind = _asyncActive ? HostWaitKind.PaceAsync : HostWaitKind.DisplayRate;
        return _asyncActive ? AsyncDisplayPaceMs : 0;   // latency-sensitive / one-shot motion: sync = present-throttled (0); async = pace cap (present is off-thread — 0 would free-spin)
    }

    /// <summary>True when capping the frame rate won't dull a one-shot transition: either no AnimEngine track is running,
    /// or every active track is a perpetual LOOP (an indeterminate spinner, skeleton shimmer). A one-shot transition
    /// (page entrance, number pop, reveal) keeps the full display rate so it stays crisp.</summary>
    // A connected-animation fly OR a pending snapshot awaiting its dest is a one-shot transition — NEVER ambient. Without
    // the _connected guard, the AWAIT-DEST phase (snapshot captured, dest not yet laid out: _connected is active but no
    // spring track is seeded yet, and only the skeleton's LOOP shimmer runs) reads as all-loop → throttles to the 30 Hz
    // ambient cap, so the detail page mounts at 30 Hz and the transition stalls before the spring starts — the residual
    // "connected animation is sometimes laggy." Keeping the whole transition at display rate mounts the dest ~4× faster.
    private bool AnimIsAmbient() => !_connected.HasActive && (!_anim.HasActive || (_anim.LoopTrackCount == _anim.TrackCount && !_anim.DisplayRateActive));

    /// <summary>Milliseconds to wait before the next AMBIENT-animation frame so the loop holds ~<see cref="AmbientAnimationFps"/>
    /// instead of free-running at the display refresh. = frame budget minus the time the just-finished frame took (this is
    /// called right after <see cref="RunFrame"/>), clamped to ≥0. Returns the full budget on the first frame.</summary>
    private int AmbientFrameWaitMs()
    {
        double budgetMs = 1000.0 / AmbientAnimationFps;
        if (_lastFrameStartTicks == 0) return (int)budgetMs;
        double elapsedMs = (Stopwatch.GetTimestamp() - _lastFrameStartTicks) * 1000.0 / Stopwatch.Frequency;
        double wait = budgetMs - elapsedMs;
        return wait <= 0 ? 0 : (int)wait;
    }

    // Slow idle-cadence slab tail-trim (mem-02): the SoA columns only grow; when the loop has been fully idle for a
    // while, give the high-water tail back to the GC ONCE per cadence (the realloc is cheap and amortized — only when
    // genuinely idle, never on an active frame). 0 = "never trimmed yet".
    private long _lastTrimTicks;
    private static readonly long TrimIdleCadenceTicks = (long)(30.0 * Stopwatch.Frequency);   // ~30s between attempts
    private void MaybeTrimOnIdle()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastTrimTicks != 0 && now - _lastTrimTicks < TrimIdleCadenceTicks) return;
        _lastTrimTicks = now;
        _scene.TrimExcessCapacity();   // no-op (returns 0) unless the slab is a mostly-empty high-water tail past the floor
        _pixelPool.Trim();             // release the idle CPU pixel-pool retention to the GC on the same idle cadence
    }

    // ── Skip-submit gate state (finding #3a) ─────────────────────────────────────────────────────────────────────────
    private ulong _lastPresentedDrawListHash;   // FNV-1a of the last PRESENTED command stream; a byte-identical frame skips submit+present
    private long _framesSkippedSubmit;          // diagnostic census of elided submits (idle/playback redundant presents avoided)
    private bool _lastFrameSkippedSubmit;       // the previous frame elided Present → RecommendedWaitMs must self-pace (no vsync block happened)
    /// <summary>Frames whose GPU submit+present was elided because the recorded command stream matched the last presented one.</summary>
    public long FramesSkippedSubmit => _framesSkippedSubmit;

    /// <summary>Steady-state guardrail (finding #4): the number of live <c>FrameClock.Tick</c> subscribers (per-frame
    /// pollers — e.g. the playback playhead ticker). It MUST fall back to 0 once playback/animation stops; a soak/CI
    /// check can assert that, catching a leaked poller that would keep the frame loop awake forever.</summary>
    public int FrameClockPollerCount => _frameClockSig.SubscriberCount;

    /// <summary>FNV-1a 64 over the recorded command stream + painter sort keys, length-prefixed so the two spans can't
    /// alias. Record is a pure function of the scene, so an equal hash ⇒ byte-identical pixels ⇒ the front buffer is still
    /// correct. Hashed 8 bytes at a time; only computed on quiet candidate frames (active frames short-circuit before it).</summary>
    private static ulong DrawListHash(ReadOnlySpan<byte> bytes, ReadOnlySpan<ulong> sortKeys)
    {
        const ulong Off = 14695981039346656037UL, Prime = 1099511628211UL;
        ulong h = Off;
        h = (h ^ (uint)bytes.Length) * Prime;
        var words = MemoryMarshal.Cast<byte, ulong>(bytes);
        for (int i = 0; i < words.Length; i++) h = (h ^ words[i]) * Prime;
        for (int i = words.Length * 8; i < bytes.Length; i++) h = (h ^ bytes[i]) * Prime;   // tail (< 8 bytes)
        h = (h ^ (uint)sortKeys.Length) * Prime;
        for (int i = 0; i < sortKeys.Length; i++) h = (h ^ sortKeys[i]) * Prime;
        return h;
    }

    /// <summary>The bitmask form of <see cref="HasActiveWork"/>: one bit per OR-term, semantically identical (the
    /// boolean is just <c>!= None</c>). Every term is an O(1) read (ImageCache.PendingCount/HasActiveCrossfades were
    /// made O(1) so this never scans). Drives FG_WAKE_DIAG attribution; otherwise as cheap as the original chain.</summary>
    private WakeReasons ComputeWakeReasons()
    {
        WakeReasons r = WakeReasons.None;
        if (_frameNeeded) r |= WakeReasons.FrameNeeded;
        // A bound virtual list spreading its initial window across frames (cold-realize stagger) needs frames to keep
        // coming until it finishes — it's neither a re-render nor an animation, so fold it into FrameNeeded.
        if (_reconciler.HasWarmingVirtuals) r |= WakeReasons.FrameNeeded;
        // A viewport whose overscan the steady-scroll row budget (or a nested-rail mount) only partially realized needs
        // frames to keep coming until the halo catches up — the visible band is already fully realized (never a stall).
        if (_reconciler.HasBudgetDeferredVirtuals) r |= WakeReasons.FrameNeeded;
        if (_runtime.HasPending) r |= WakeReasons.RuntimePending;
        if (_scene.HasDynamicText) r |= WakeReasons.DynamicText;
        if (_anim.HasActive || _connected.HasActive) r |= WakeReasons.Anim;   // connected fly / snapshot awaiting dest; hover/press fades are now _anim tracks too
        if (_scrollAnim.HasActive) r |= WakeReasons.ScrollAnim;
        if (_repeat.HasActive) r |= WakeReasons.Repeat;
        if (_caretBlinker.HasActive) r |= WakeReasons.Caret;
        if (_scene.HasBrushAnims) r |= WakeReasons.BrushAnims;
        if (_images.HasReadyCompletions) r |= WakeReasons.ImageReady;
        if (_device.HasPendingUploads) r |= WakeReasons.ImagesPending;
        if (_bakedBlurQueue.HasJobs) r |= WakeReasons.BakedBlurPending;
        if (_images.HasActiveCrossfades) r |= WakeReasons.ImageCrossfades;
        if (_scene.OrphanCount > 0) r |= WakeReasons.Orphans;
        if (_dispatcher.Drag.HasActiveWork || _dispatcher.DragDrop.HasActiveWork) r |= WakeReasons.DragDropWork;   // E5: ghost spring easing / edge auto-scroll
        if (_dispatcher.Drag.IsActive) r |= WakeReasons.DragActive;   // E5 reorder dwell keep-alive: a live drag keeps frames coming so the 200/300ms FrameClock dwell tickers advance even on a motionless pointer (DragController.cs:118)
        if (_dispatcher.HasArmedHold) r |= WakeReasons.GestureHold;   // §7A touch long-press: a STATIONARY held finger emits no input, so keep frames coming until TickGestureArenas fires the ~500ms Hold (then this clears and the loop idles)
        if (_dispatcher.HasPendingTouchPress) r |= WakeReasons.TouchPress;
        // A media player actively presenting a video surface (playing, or ramping to play through the DRM/CDM licensing
        // handshake) must keep the loop ticking at DISPLAY rate — otherwise the loop idles the instant the initial
        // FrameNeeded clears, the MediaPlayerElement pump stops, and the video freezes (advancing only when a seek/pause
        // pokes _frameNeeded). Cleared the moment playback pauses/stops/ends or the surface is released, so a paused,
        // audio-only, or idle player still lets the loop sleep. O(1) counter read (VideoSurfaceRegistry).
        if (_videoSurfaces.HasActivePresentation) r |= WakeReasons.VideoPresenting;
        // A windowed popup's desktop-acrylic open reveal is driven per-frame on Present (CompositionBackdrop.TickAnimation),
        // so it needs the loop to keep presenting until it settles — otherwise (no engine animation active for windowed
        // menus) the loop idle-skips and the reveal freezes at its seed. O(popups) ≈ O(1) (typically 0–1 menus open).
        for (int i = 0; i < _popupWindows.Count; i++)
            if (_popupWindows[i].Swapchain?.PopupAnimating == true) { r |= WakeReasons.PopupAnim; break; }
        // Frame-clock timers: a DUE timer forces exactly the frame that fires it; a pending-but-future timer sets NO bit
        // (the loop still idles — RecommendedWaitMs shapes the wait to reach it). Warm-cadence keeps the loop rendering
        // for a bounded window after the last input. Read the clock once, and only when a timer is armed / a warm hold is
        // live (so an idle host with no timers pays nothing here).
        if (_timers.Count > 0 || (_warmCadenceEnabled && _warmCadenceUntilMs > 0.0))
        {
            double tnow = _timers.NowMs;
            if (_timers.HasDue(tnow)) r |= WakeReasons.Timer;
            if (_warmCadenceEnabled && tnow < _warmCadenceUntilMs) r |= WakeReasons.WarmCadence;
        }
        return r;
    }

    /// <summary>Enable inertial smooth scrolling + auto-hiding scrollbars (the real app turns this on; off = immediate).</summary>
    public bool SmoothScroll { get => _dispatcher.SmoothScroll; set => _dispatcher.SmoothScroll = value; }

    public ImageCache Images => _images;

    // Census accessors (read by MemCensus / CensusSnapshot — same assembly): the subsystems Scene/Animation/Images
    // already expose are reused; these surface the rest. All passive O(1) reads.
    internal StringTable Strings => _strings;
    internal TreeReconciler Reconciler => _reconciler;
    /// <summary>Last <c>FG_RENDER_CENSUS</c> spike dump (empty when census off or no spike this frame).</summary>
    public string LastRenderCensusDump => _reconciler.LastRenderCensusDump;
    internal int InteractionAnimatorCensus => _anim.HoverPressTrackCount;   // hover/press are now engine HoverFade/PressFade tracks (InteractionAnimator deleted)
    internal int ScrollAnimatorCensus => _scrollAnim.ActiveCount;

    /// <summary>Test-only handle to the phase-7 scroll integrator (scroll-feel-rework-v2 §8 headless gates). Headless
    /// leaves <see cref="ScrollIntegrator.FrameQpcSec"/> at 0 (the resampler is then vacuous — deterministic for the
    /// legacy gates), so the §8 gates that must exercise real frame-time resampling set it to a SYNTHETIC frame clock via
    /// this seam before each <c>RunFrame</c> (headless never overwrites it — see the <c>!_isHeadless</c> guard at the
    /// tick). Not exposed publicly; VerticalSlice has InternalsVisibleTo.</summary>
    internal ScrollIntegrator ScrollIntegratorForTest => _scrollAnim;
    internal int DeviceLostRecoveryCountForTest => _deviceLostRecoveryCount;
    /// <summary>Test-only (wake.scrollHoldSuppressesAmbientCap): read/force the 0.45s post-scroll hold so the gate can
    /// pin the hold live/expired deterministically instead of sleeping wall-clock. Stopwatch-tick deadline.</summary>
    internal long MainScrollHoldUntilForTest { get => _mainScrollHoldUntil; set => _mainScrollHoldUntil = value; }
    /// <summary>Test-only companion: force the post-scroll display-rate grace expired so the gate isolates the HOLD term.</summary>
    internal void SetScrollGraceForTest(long until) => _scrollGraceUntil = until;

    /// <summary>Test-only (gate.timer.*): the frame-clock timer queue, its deterministic headless clock, and the
    /// post-input warm-cadence enable (off headless by default so existing idle gates are unaffected; the warm-cadence
    /// gate flips it on). <see cref="FrameClockMsForTest"/> is the headless timer clock (advances by the fixed step per Paint).</summary>
    internal HostTimerQueue TimersForTest => _timers;
    internal double FrameClockMsForTest => _frameClockMs;
    internal bool WarmCadenceEnabledForTest { get => _warmCadenceEnabled; set => _warmCadenceEnabled = value; }
    internal double WarmCadenceUntilForTest => _warmCadenceUntilMs;

    /// <summary>The frame loop's current wake-reason mask — why <see cref="HasActiveWork"/> would keep running this
    /// instant (for tests / census). An O(1) recompute of the same terms.</summary>
    public WakeReasons CurrentWakeReasons => ComputeWakeReasons();

    /// <summary>The focused-editor caret-blink ticker (phase 7). Text-input controls Focus/Blur/ResetBlink it.</summary>
    public CaretBlinker CaretBlinker => _caretBlinker;

    /// <summary>
    /// Whether out-of-bounds popup WINDOWS are available (the engine's <c>CPopup::DoesPlatformSupportWindowedPopup</c>
    /// gate). Defaults to true only on the headless path: the headless device creates independent swapchains, so the
    /// COMPLETE windowed-popup pipeline (PAL window + own swapchain + subtree DrawList) runs and is verifiable.
    /// needs-pixels — D3D12 stays false until the per-target submit lands: <c>IGpuDevice.SubmitDrawList</c> has no
    /// present-target parameter and <c>D3D12Device.CreateSwapchain</c> is a one-shot device init (D3D12Device.cs:95-122),
    /// so a second swapchain cannot be rendered yet. When false, overlays asking for
    /// <c>PopupOptions.ConstrainToRootBounds = false</c> silently fall back to in-window clamped placement (exactly
    /// WinUI on platforms without windowed-popup support).
    /// </summary>
    public bool PopupWindowsEnabled { get; set; }

    /// <summary>Live out-of-bounds popup windows (E4) — for headless checks (decode each slot's DrawList).</summary>
    public IReadOnlyList<PopupWindowSlot> PopupWindows => _popupWindows;

    public AppHost(IPlatformApp app, IPlatformWindow window, IGpuDevice device, IFontSystem fonts,
                   StringTable strings, Component root, ImageCache? images = null, IFrameTimeSource? frameTime = null,
                   ScrollTuning? scrollTuning = null, bool compositeSwapchain = false, bool isDetachedChild = false)
    {
        _app = app;
        _fonts = fonts;
        _isDetachedChild = isDetachedChild;
        _window = window;
        _asyncActive = s_renderAsync && window.Handle.Kind != NativeHandleKind.Headless;   // headless never goes async (see field)
        // Step 3 (async): windowed out-of-bounds popups submit + present on the UI thread (RecordPopupWindows), sharing
        // the one device/queue/fence/command-list with the render thread — a concurrent submit source that would race the
        // async loop and defeat the device-level submit/present confinement assert. Gate them OFF under async: flyouts/menus
        // fall back to in-window clamped placement (the overlay's existing fallback). Removes the last UI-thread GPU submit,
        // making the Step 0 assert unconditionally valid. Default + force-sync keep windowed popups (no async overlap).
        PopupWindowsEnabled = (window.Handle.Kind == NativeHandleKind.Headless || device.SupportsSecondarySwapchains) && !_asyncActive;
        _device = device;
        _root = root;
        _strings = strings;
        // The overlay scrollbar's arrows = the SAME caret glyphs the ScrollBar control template draws (the shared
        // IconGlyphs constants), pre-interned once so record stays 0-alloc. PINNED with a host AddRef: the ids are
        // shared BY CONTENT with any TextEl using the same glyph/family (the ScrollBar page's arrow cells, every
        // icon's font family) — without the ref, that page's unmount Release reclaims the id and the recorder's
        // arrows silently resolve to "" for the rest of the session.
        StringId sbUp = strings.Intern(IconGlyphs.CaretUpSolid8), sbDown = strings.Intern(IconGlyphs.CaretDownSolid8),
                 sbLeft = strings.Intern(IconGlyphs.CaretLeftSolid8), sbRight = strings.Intern(IconGlyphs.CaretRightSolid8),
                 sbFam = strings.Intern(Theme.IconFont);
        strings.AddRef(sbUp); strings.AddRef(sbDown); strings.AddRef(sbLeft); strings.AddRef(sbRight); strings.AddRef(sbFam);
        SceneRecorder.ConfigureScrollbarArrowGlyphs(sbUp, sbDown, sbLeft, sbRight, sbFam);
        _images = images ?? new ImageCache(new FakeImageDecoder());
        _isHeadless = window.Handle.Kind == NativeHandleKind.Headless;
        _frameTime = frameTime ?? (_isHeadless ? new FixedFrameTimeSource() : new StopwatchFrameTimeSource());
        // Timer clock: headless rides the deterministic accumulated frame delta (gates pump frames); a real window uses
        // the monotonic wall clock so a due time survives a fully-blocked WaitForWork (the clamped anim delta would drift).
        _timers = new HostTimerQueue(_isHeadless
            ? () => _frameClockMs
            : static () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        _drainTimers = _timers.Drain;
        _warmCadenceEnabled = !_isHeadless;   // gates opt in via WarmCadenceEnabledForTest
        // A detached child window must be COMPOSITED (its own DComp tree) so its per-window video presenter can hole-punch
        // and composite the protected/clear surface. The primary host passes false and relies on the device-composited
        // default (identical behavior). CreateSwapchain only forces composited for the FIRST swapchain; the child is the
        // second, so it must be requested explicitly here.
        _swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx, Composited: compositeSwapchain));
        _reconciler = new TreeReconciler(_scene, strings, _runtime);
        _reconciler.RegisterPendingEffectContext = RegisterPendingEffectContext;
        _layout = new FlexLayout(_scene, fonts);
        _invalidator = new LayoutInvalidator(_scene, _layout);
        _invalidator.DebugKeyResolver = _reconciler.DebugKeyOf;   // best-effort node→key for the FG_DIAG relayout-escape message (DEBUG-only invocation)
        var scrollProfile = scrollTuning ?? ScrollTuning.WinUiLike;   // WinUI-parity wheel distance + feel (the Win32 app default)
        _dispatcher = new InputDispatcher(_scene) { Tuning = scrollProfile };
        _reconciler.OnSubtreeDeactivated = _dispatcher.DeactivateSubtree;
        _anim = new AnimEngine(_scene);
        _connected = new ConnectedAnimation(_scene, _anim, _images);   // shared-element (connected-animation) Hero flies
        // Scroll is fully engine-owned: the deterministic ScrollIntegrator is the single, portable scroll source (§2.1
        // single writer) on every platform (WheelAnimating chase + touch/touchpad fling + overscroll spring + conscious
        // scrollbar). There is no OS scroll source — touchpad arrives as phase-tagged scroll events, touch as the gesture path.
        _scrollAnim = new ScrollIntegrator(_scene, scrollProfile);
        _repeat = new RepeatTicker(_scene);
        _caretBlinker = new CaretBlinker(_scene);
        _lastSize = window.ClientSizePx;
        _lastScale = window.Scale;

        // A reactive write (anywhere) requests a frame.
        _runtime.FrameRequested = WakeFrame;
        _dispatcher.RequestRerender = WakeFrame;   // virtual list crossing an item boundary on scroll
        _scrollAnim.RequestRerender = WakeFrame;   // re-realize the virtual window on a boundary crossing
        // Hover/press edges drive BOTH the (record-time) InteractionAnimator AND the new declarative While* resolver.
        // The resolver is a no-op for nodes without WhileHover/WhilePressed targets — additive, no regression.
        _dispatcher.OnHoverChanged = (n, on) => { _anim.SetHover(n, on); _anim.ApplyInteractionEdge(n, AnimEngine.InteractKind.Hover, on); };
        _dispatcher.OnPressChanged = (n, on) => { _anim.SetPress(n, on); _anim.ApplyInteractionEdge(n, AnimEngine.InteractKind.Press, on); };
        _dispatcher.OnScrollArmed = _scrollAnim.Arm;
        _dispatcher.OnScrollHover = _scrollAnim.Hover;
        _dispatcher.OnScrollLeave = _scrollAnim.Leave;
        _scrollAnim.ScrollWrite = _dispatcher.WriteScrollOffset;   // Fling integrator writes absolute offsets through the Input chokepoint
        _scrollAnim.OverscrollWrite = _dispatcher.WriteOverscroll; // overscroll spring-back writes the visual band (offset untouched)
        _dispatcher.OnFlingStarted = SeedScrollFling;              // touch-up flick → friction-decay inertia in phase 7
        // scroll-feel-rework-v2 §2.1/§2.3: the phase-driven dispatcher is a pure intent recorder — it records
        // TouchpadTracking contact onto the integrator resampler; phase 7 (ScrollIntegrator.Tick) is the SOLE offset/band
        // writer. CancelFling zeros a coast on every PointerDown / scrollbar grab (R6 fix).
        _dispatcher.OnScrollTrackBegin = _scrollAnim.BeginTracking;
        _dispatcher.OnScrollTrackSample = _scrollAnim.AppendContactSample;
        _dispatcher.OnScrollTrackEnd = _scrollAnim.EndTracking;
        _dispatcher.OnCancelFling = _scrollAnim.CancelFling;
        _dispatcher.OnRepeatArmed = _repeat.Arm;
        _dispatcher.OnRepeatReleased = _repeat.Disarm;
        _dispatcher.OnRepeatPaused = _repeat.Pause;     // held pointer left the repeat node → stop ticking
        _dispatcher.OnRepeatResumed = _repeat.Resume;   // re-entered → fresh initial delay, no immediate re-fire
        _dispatcher.OnKeyPreview = _inputHooks.Preview;   // an open overlay/flyout can intercept Escape (registered via the InputHooks ambient)
        _inputHooks.PointerVelocity = () => _dispatcher.PointerVelocity;        // cross-axis swipe controls snap on real flick speed
        _inputHooks.GetPointerPosition = () => _dispatcher.PointerPosition;     // ToolTip safe-zone poll (bubble stays hit-test-invisible)
        _inputHooks.GetFocus = () => _dispatcher.Focused;                       // an opening overlay captures focus to restore on close
        _inputHooks.RestoreFocus = h => _dispatcher.SetFocus(h, visual: false);
        _inputHooks.FocusNode = (h, visual) => _dispatcher.SetFocus(h, visual);
        _inputHooks.MoveFocusVisual = h => _dispatcher.SetFocus(h, visual: true);   // roving arrow-key focus shows the ring (RadioButtons)
        _inputHooks.PushFocusScope = _dispatcher.PushFocusScope;     // REAL Tab trap for FocusTrap overlays (ContentDialog)
        _inputHooks.PopFocusScope = _dispatcher.RemoveFocusScope;    // order-independent (overlays close out of stack order)
        _inputHooks.FirstFocusableIn = _dispatcher.FirstFocusableIn; // focus-trap initial focus (first tab stop / default button)
        _dispatcher.OnCursorChanged = _window.SetCursor;                        // hover-resolved cursor (hand/I-beam/resize)
        _dispatcher.OnWindowBlur = _inputHooks.NotifyWindowBlur;                // deactivation → light-dismiss overlays close
        _dispatcher.OnPointerDownObserved = _inputHooks.NotifyPointerDown;
        _dispatcher.OnScrollStartedObserved = _inputHooks.NotifyScrollStarted;
        _inputHooks.RedispatchContextAt = _dispatcher.RequestContextAt;         // scrim right-click → close top + reopen the node's menu (one gesture)

        // Custom-titlebar chrome seam (WindowDesc.CustomFrame): pull-state + caption commands to the window, the
        // region push (relayout-only), and an epoch signal bumped on activation/placement changes so the TitleBar
        // control re-renders (dim / max↔restore glyph). All members default-no-op on standard-frame backends.
        _inputHooks.GetWindowState = () => _window.State;
        _inputHooks.IsWindowActive = () => _window.IsActive;
        _inputHooks.WindowMinimize = _window.Minimize;
        _inputHooks.WindowToggleMaximize = _window.ToggleMaximize;
        _inputHooks.IsWindowFullscreen = () => _window.IsFullscreen;
        _inputHooks.WindowSetFullscreen = _window.SetFullscreen;
        _inputHooks.WindowClose = _window.CloseWindow;
        _inputHooks.OpenDetachedWindow = OpenDetachedWindow;   // pop-out video window (guarded: a child host / async / headless returns null)
        _inputHooks.SetTitleBarRegions = (regions, count) => _window.SetTitleBarRegions(regions.AsSpan(0, count));
        _inputHooks.GetNodeRect = _scene.AbsoluteRect;
        var chromeEpoch = new Signal<int>(0);
        _inputHooks.WindowChromeEpoch = chromeEpoch;
        // Mica deactivation parity (WinUI): a Mica window paints a flat SOLID fallback when INACTIVE — DWM stops the live
        // blur, so without this the transparent client lets the desktop wallpaper bleed through, giving a too-light,
        // wallpaper-tinted chrome whenever the window isn't focused. Active → Transparent (the real Mica shows); inactive →
        // SolidBackgroundFillColorBase (theme-aware). Only a Mica window (FluentApp set WindowBackground=Transparent) swaps.
        bool micaWindow = Theme.WindowBackground.A <= 0.004f;
        _dispatcher.OnWindowActivationChanged = () =>
        {
            // Read the base LIVE off Tok.T so it follows a theme toggle: dark #202020 / light warm canvas. A hardcoded dark
            // fallback showed near-black chrome in LIGHT mode the instant the window lost focus (the translucent light
            // chrome composited over #202020 instead of the light canvas).
            if (micaWindow) Theme.WindowBackground = _window.IsActive ? ColorF.Transparent : Tok.T.WindowBackground;
            chromeEpoch.Value = chromeEpoch.Peek() + 1;
        };

        // Live drag state for UseDragState / DragPreviewLayer (cursor-following custom preview). Wired on the host
        // instance AND the channel-default (a DragPreviewLayer mounted by a static factory reaches it via Default).
        _inputHooks.DragEpoch = _dragEpoch;
        _inputHooks.GetDragState = ReadDragState;
        InputHooks.Current.Default.DragEpoch = _dragEpoch;
        InputHooks.Current.Default.GetDragState = ReadDragState;

        // E5 drop-settle: the released drag visual glides from the drop point into its (possibly reordered) slot via
        // the same FLIP pipeline that moves displaced siblings — the seeded spring is retargeted velocity-continuously
        // by ApplyProjections when the OnDragCompleted commit re-lays-out. No Animate transition ⇒ the visual snaps.
        _dispatcher.Drag.OnSettle = (node, fromAbs, toAbs) =>
        {
            if (Motion.ReducedMotion) return;   // reduced motion: snap into the slot (no glide)
            if (_anim.TryGetTransition(node, out var spec)) _anim.AnimateBounds(node, fromAbs, toAbs, spec);
        };

        // Text-editing seams for EditableText (clipboard / IME / caret blink / shared text metrics) — see InputHooks.
        _inputHooks.Clipboard = app.Clipboard;
        _inputHooks.OpenUri = app.OpenUri;
        // Static factories (HyperlinkButton.Create) have no component scope → no UseContext: mirror the seam onto
        // the InputHooks.Current channel-default instance too (last-constructed host wins — matches the
        // single-window v1 host model; headless checks construct hosts sequentially).
        InputHooks.Current.Default.OpenUri = app.OpenUri;
        InputHooks.Current.Default.Clipboard = app.Clipboard;   // mirror the clipboard too (static factories / host-less reads use the default)

        // OS file/folder drop seam (the inbound twin of OpenUri): the platform's file-drop handler (the Windows backend's
        // WM_DROPFILES case) invokes these on the UI thread via the normal message pump; they drive the dispatcher's
        // external DragSession so a BoxEl.DropTarget accepting DropKinds.Files receives the drop. Wired on the host
        // instance AND the channel-default (the backend reaches them via Current.Default — it has no component scope).
        _inputHooks.ExternalDragEnter = _dispatcher.ExternalDragEnter;
        _inputHooks.ExternalDragOver = _dispatcher.ExternalDragOver;
        _inputHooks.ExternalDragLeave = _dispatcher.ExternalDragLeave;
        _inputHooks.ExternalDrop = _dispatcher.ExternalDrop;
        _inputHooks.ExternalDropFiles = _dispatcher.ExternalDropFiles;
        InputHooks.Current.Default.ExternalDragEnter = _dispatcher.ExternalDragEnter;
        InputHooks.Current.Default.ExternalDragOver = _dispatcher.ExternalDragOver;
        InputHooks.Current.Default.ExternalDragLeave = _dispatcher.ExternalDragLeave;
        InputHooks.Current.Default.ExternalDrop = _dispatcher.ExternalDrop;
        InputHooks.Current.Default.ExternalDropFiles = _dispatcher.ExternalDropFiles;

        // Inbound twin of OpenUri: a single-instance second-launch redirect (the PAL's WM_COPYDATA → ActivationRedirected,
        // already on the UI thread). Stash + WakeFrame here; Paint() drains _pendingActivation at the top and re-raises
        // the public AppHost.ActivationRedirected for app code. WakeFrame is UI-thread-only — safe because the PAL
        // delivers this on the UI thread (no PostMessage hop needed, unlike a cross-thread notification activator).
        _onActivationRedirected = uri => { _pendingActivation = uri; WakeFrame(); };
        app.ActivationRedirected += _onActivationRedirected;
        // Inbound OS color-settings change (dark-mode/accent flip): the PAL raises this on the UI thread from
        // WM_SETTINGCHANGE. Stash + WakeFrame; Paint() drains the flag at the top and re-raises the public event so app
        // code (which owns the System/Light/Dark mode decision) re-reads the OS state and triggers a live re-theme.
        _onSystemColorsChanged = () => { _pendingSystemColors = true; WakeFrame(); };
        app.SystemColorsChanged += _onSystemColorsChanged;
        _inputHooks.TextInput = window.TextInput;
        _inputHooks.Fonts = fonts;
        _inputHooks.CaretFocus = (n, blinkMs) => _caretBlinker.Focus(n, blinkMs);
        _inputHooks.CaretBlur = _caretBlinker.Blur;
        _inputHooks.CaretReset = _caretBlinker.ResetBlink;
        _inputHooks.ImeSetCaretRect = dip =>   // controls pass DIP; the host owns the window scale → physical px
        {
            float s = _window.Scale <= 0f ? 1f : _window.Scale;
            _window.TextInput.SetCaretRectPx(new RectF(dip.X * s, dip.Y * s, dip.W * s, dip.H * s));
        };

        // SIP (touch keyboard) trigger seam (input-a11y.md §10): EditableText shows/hides the on-screen keyboard through
        // these on a TOUCH focus-gain / focus-loss; the dispatcher reports the focus-causing pointer's device class.
        _inputHooks.LastPointerWasTouch = () => _dispatcher.LastPointerKind == PointerKind.Touch;
        _inputHooks.ShowTouchKeyboard = _window.TextInput.TryShowTouchKeyboard;
        _inputHooks.HideTouchKeyboard = _window.TextInput.TryHideTouchKeyboard;
        // The panel's Showing/Hiding OccludedRect (CLIENT DIP) reflows the focused editor's caret above it — the WinUI
        // EnsureFocusedElementInView the InputPaneHandler drives. Cached delegate (unsubscribed in Dispose) so a disposed
        // host leaves no callback into it; a WakeFrame schedules the frame that paints the scrolled position.
        _onOccludedRectChanged = dipRect =>
        {
            if (_dispatcher.EnsureFocusedAboveOcclusion(dipRect.Y)) WakeFrame();
        };
        _window.TextInput.OccludedRectChanged += _onOccludedRectChanged;

        // E4 windowed out-of-bounds popups: the OverlayHost asks for monitor work areas + popup-window leases through
        // these hooks; the host owns the DIP↔screen-px conversion (window scale + client origin) and the render side
        // (own swapchain + per-popup DrawList via the recorder root-override).
        _inputHooks.GetWorkArea = GetWorkAreaDip;
        _inputHooks.OpenPopupWindow = OpenPopupWindow;
        _inputHooks.SetPopupWindowBounds = SetPopupWindowBounds;
        _inputHooks.ClosePopupWindow = ClosePopupWindow;
        _inputHooks.AnimatePopupClose = AnimatePopupCloseWindow;

        _reconciler.Anim = _anim;
        _reconciler.Connected = _connected;   // shared-element (connected-animation) participant registry, fed by Element.MorphId
        _reconciler.ArmScroll = _scrollAnim.Arm;   // controls can request a smooth programmatic scroll (set Target + arm → phase 7 eases)
        _reconciler.PeekMainScrollBusy = () => Stopwatch.GetTimestamp() < _mainScrollHoldUntil;
        // KeepAlive park/un-park → quiesce/resume the parked subtree's animation + scroll tickers so a backgrounded tab's
        // looping animation or mid-fling scroll can't keep the frame loop awake (defeating the idle wake-stop). A parked
        // shared-element node also captures its reverse-fly snapshot here (Back returns to it via the like-tagged dest).
        _reconciler.OnNodeParkedChanged = (node, parked) =>
        {
            _anim.SetNodeParked(node, parked); _scrollAnim.SetNodeParked(node, parked);
            _connected.OnNodeParked(node, parked);
        };
        // Symmetric teardown of INDEX-keyed per-node side-tables on slot free (mem-06): a freed node's slot is reused,
        // so the AnimEngine layout-transition spec + the ScrollIntegrator conscious-bar timers (both keyed by node index,
        // not gen-checked handle) must be dropped or the next node reusing that index inherits the stale row.
        _scene.OnFreeIndex = OnSceneSlotFreed;
        _reconciler.Images = _images;
        _reconciler.ImageEpoch = _imageEpoch;
        _images.SetBakedBlurQueue(_bakedBlurQueue);
        _images.SetCompletionWake(_window.Wake);
        _bakedBlurQueue.SetCompletionWake(_window.Wake);
        _device.SetBakedBlurQueue(_bakedBlurQueue);
        if (_asyncActive)
        {
            // ASYNC (Step 1): the UI thread must not touch the device. The pixel sink COPIES the transient decode pixels
            // into a rented ArrayPool buffer and enqueues it (optimistically admitting Ready); the render thread stages it
            // (returning the buffer) and posts back only rejections. The evict sink enqueues too. See ImageUploadQueue.
            _imageQueue = new Threading.ImageUploadQueue { BufferPool = _pixelPool };
            var q = _imageQueue;
            _images.SetPixelAttemptSink((int id, System.ReadOnlySpan<byte> px, int w, int h) =>
            {
                byte[] buf = _pixelPool.Rent(px.Length);   // bounded pixel pool copy (returned render-side via the queue's BufferPool)
                px.CopyTo(buf);
                q.EnqueueUpload(id, buf, w, h, px.Length);
                return FluentGpu.Scene.ImageUploadResult.Accepted;   // optimistic; a real rejection returns via the reject ring next Pump
            });
            _images.SetEvictSink(q.EnqueueEvict);
            _images.SetAsyncUploadQueue(q);
            _device.MarkImageUploadsRenderConfined();
        }
        else
        {
            _images.SetPixelAttemptSink(_device.TryUploadImage);
            _images.SetEvictSink(_device.EvictImage);
        }
        _images.ImageStatusChanged += (id, _, _, _) =>
        {
            _reconciler.MarkImageDirty(id);
            if (_imageEpoch.HasSubscribers) _imageEpoch.Value = _imageEpoch.Peek() + 1;
            WakeFrame();
        };

        // Publish ambient contexts before the first render so UseContext(Viewport.Size)/FrameDiagnostics resolve.
        _lastViewportDip = ClientSizeDip();
        _viewportSig.Value = _lastViewportDip;
        _inputHooksSig = new Signal<object?>(_inputHooks);
        _viewportScaleSig.Value = _window.Scale <= 0f ? 1f : _window.Scale;
        _reconciler.SetAmbient(Viewport.Size, _viewportSig);
        _reconciler.SetAmbient(Viewport.Scale, _viewportScaleSig);
        _reconciler.SetAmbient(FrameDiagnostics.Current, _frameStatsSig);
        _reconciler.SetAmbient(InputHooks.Current, _inputHooksSig);
        _reconciler.SetAmbient(FrameClock.Tick, _frameClockSig);
        _uiPoster = Post;   // ONE delegate instance so HostDispatch.Current can be identity-compared on teardown
        _hostPostSig = new Signal<object?>(_uiPoster);   // ambient UI-thread poster (HostDispatch.Post / UsePost)
        _reconciler.SetAmbient(HostDispatch.Post, _hostPostSig);
        HostDispatch.Current = _uiPoster;   // process-static poster for non-component services (localization, …) — cleared in Dispose
        _reconciler.SetAmbient(SharedTransition.Begin, new Signal<object?>((Action<string>)_connected.Begin));   // connected-anim forward capture-at-click
        _reconciler.SetAmbient(SharedTransition.SetMotion, new Signal<object?>((Action<FluentGpu.Animation.ConnectedMotion>)(m => _connected.FlyMotion = m)));   // live fly-curve switcher (app A/B)
        // Window-visibility ambient: the channel value IS the visibility signal (an IReadSignal<bool>, never re-published),
        // so UseIsActive resolves it once and subscribes to the INNER signal — see Activation.IsActive.
        _reconciler.SetAmbient(Activation.IsActive, new Signal<object?>(_windowVisible));
        _reconciler.SetAmbient(ThemeControl.Request, new Signal<object?>((Action<float>)RequestThemeTransition));   // live re-theme trigger for app code
        _reconciler.SetAmbient(VideoCompositor.Current, new Signal<object?>(_videoSurfaces));   // video-surface intent buffer for UseVideoSurface
        _reconciler.SetAmbient(HostTimers.Current, new Signal<object?>(_timers));   // frame-clock timer queue for the timing hooks (UseTimeout/UseInterval/UseDebouncedValue/UseThrottledValue)

        // Keep-alive repaint: the OS fires this synchronously from inside a modal move/size loop (and on NC
        // hover/press transitions while the frame loop idles). Paint with keepAlive so the device skips its
        // frame-latency throttle wait — otherwise each fires a full vblank-class stall inline on the WndProc thread
        // (the drag-start / live-resize hitch). Live resize still paints synchronously; it just no longer blocks.
        _window.PaintRequested = () => Paint(0, keepAlive: true);

        // Render-thread seam (Step 4, force-sync): spawn the fgpu-render thread that runs submit/present off the UI
        // thread. Default OFF — ships single-thread until the seam.race soak is green. The thread just waits on its wake
        // event until the first Paint drains it, so constructing it here (before the first frame) is safe.
        // Spawn the render thread ONLY for a real (non-headless) backend — headless has no GPU work to offload and its
        // device seam methods are no-ops, so it stays on the deterministic synchronous inline path.
        if ((s_renderThread || s_renderAsync) && window.Handle.Kind != NativeHandleKind.Headless)
        {
            // Step 4: under async, wire the device-lost recovery rendezvous — arm the backend to SIGNAL loss (not throw on
            // the render thread) + bound its fence waits, and give the render loop a recover gate (_device.RecoverDevice
            // under render confinement) + a thread-safe UI wake to nudge the UI out of its clean block on RecoverDone.
            if (_asyncActive) { _deviceLost = new Threading.DeviceLostCoordinator(); _device.EnableAsyncDeviceLostSignaling(); }
            _renderThread = new Threading.RenderThread(_renderSeam, SubmitPresentOnRenderThread, async: _asyncActive,
                deviceLost: _deviceLost, recover: _deviceLost is null ? null : RecoverDeviceAfterDump, windowWake: _deviceLost is null ? null : _window.Wake);
            _device.MarkRenderConfined();
        }

        // Opt-in diagnostics tools (constructed only when their flag is set; the host tick paths short-circuit otherwise).
        if (s_wakeDiag) _wakeDiag = new WakeDiagnostics();
        if (s_memDiag)
        {
            double sec = 5.0;
            string? raw = Environment.GetEnvironmentVariable("FG_MEM_DIAG_SEC");
            if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
                sec = parsed;
            _memCensus = new MemCensus(this, sec);
        }

        // Mount the root component as a reactive render-effect (initial render builds the scene).
        _reconciler.MountRoot(_root);
        // Baseline the re-theme epoch AFTER the root mount — startup theme injection (OS accent / Mica window background,
        // applied before this ctor returns) has already bumped Tok.Epoch, so the FIRST paint must not see a spurious change.
        _lastThemeEpoch = Tok.Epoch;
    }

    private void WakeFrame()
    {
        if (_inPaint) _frameAfterPaint = true;
        else _frameNeeded = true;
    }

    /// <summary>Snapshot the live typed drag for <c>UseDragState</c> — both the in-app <c>DragSource</c> session and the
    /// OS file-drag session live on <c>DragDropContext</c>. Idle ⇒ <see cref="DragState.Active"/> false.</summary>
    private DragState ReadDragState()
    {
        var dd = _dispatcher.DragDrop;
        if (!dd.IsActive) return default;
        var s = dd.Session;
        return new DragState(true, s.Kind, s.Position, s.Payload);
    }

    /// <summary>Run <paramref name="action"/> on the UI thread at the top of the next frame. THREAD-SAFE — callable from
    /// any thread (an OS callback, a worker, an agile-COM apartment), unlike the UI-thread-only <see cref="WakeFrame"/>.
    /// Enqueues the action and posts a thread-safe wake so a fully-idle, blocked loop runs a frame to drain it; the drain
    /// happens inside a reactive <c>Batch</c> (see <see cref="Paint"/>), so every signal the posted actions write
    /// coalesces into a single re-render. This is the engine's UI marshal — surfaced to components as
    /// <c>HostDispatch.Post</c> / <c>UsePost()</c>.</summary>
    public void Post(Action action)
    {
        if (action is null) return;
        _uiPosts.Enqueue(action);
        _window.Wake();   // thread-safe (Win32 PostMessage WM_NULL); breaks a blocked WaitForWork so an idle loop drains promptly
    }

    private void DrainUiPosts()
    {
        // Bounded to a one-frame snapshot of the queue depth: an action that unconditionally re-Posts itself (re-enqueues
        // + Wake()s) must not livelock this drain into a CPU-spin — its re-post lands in _uiPosts and is picked up by a
        // LATER frame (the Wake keeps the loop alive). The migrated cards never self-re-post, but the cap is cheap insurance.
        int budget = _uiPosts.Count;
        while (budget-- > 0 && _uiPosts.TryDequeue(out var a))
            try { a(); } catch { /* a posted action must never take down the frame */ }
    }

    /// <summary>Wired to <see cref="InputDispatcher.OnFlingStarted"/>: a touch pan released with a flick speed hands its
    /// offset-space velocity here. Seed the viewport's <see cref="ScrollState.FlingVelocity"/> (clamped to the §4.3
    /// FlingMaxVelocityPxPerS = 8000 px/s seed cap) + <c>Phase = Fling</c> and arm the <see cref="ScrollIntegrator"/> so
    /// phase 7 coasts it via the exact-integral CoastStep (and <c>WakeReasons.ScrollAnim</c> keeps frames coming until it
    /// settles). 0-alloc: a cached method group, a few field writes on a ref.</summary>
    private void SeedScrollFling(NodeHandle node, float velocityPxPerS)
    {
        if (node.IsNull || !_scene.IsLive(node) || !_scene.HasScroll(node)) return;
        ref ScrollState sc = ref _scene.ScrollRef(node);
        sc.FlingVelocity = Math.Clamp(velocityPxPerS, -ScrollIntegrator.FlingMaxVelocityPxPerS, ScrollIntegrator.FlingMaxVelocityPxPerS);
        sc.Phase = ScrollIntegrator.Fling;
        sc.PhaseFlags = 0;   // a touch/PTP-fallback self-fling (not OS-owned): the exact-integral coast owns it
        // A snap-configured viewport re-solves the velocity on the FIRST fling tick (ScrollIntegrator) so the same decay
        // curve lands EXACTLY on a snap value — capture the launch offset (the impulse "ignored value" anchor) and reset
        // the one-shot retarget latch here. A non-snap viewport ignores both.
        sc.FlingRetargeted = false;
        sc.FlingSnapTarget = float.NaN;
        sc.FlingFromOffset = sc.Orientation == 1 ? sc.OffsetX : sc.OffsetY;
        if (FluentGpu.Foundation.ScrollTrace.On)
            FluentGpu.Foundation.ScrollTrace.AnimEvent((int)node.Raw.Index, 4, velocityPxPerS, sc.FlingFromOffset, 0f);
        _scrollAnim.Arm(node);
    }

    /// <summary>Events pumped into the ring this frame — recorded by the <see cref="FluentGpu.Foundation.ScrollTrace"/>
    /// frame marker (diagnostic only; written every frame, read only when the trace is on).</summary>
    private int _tracePumpedEvents;

    /// <summary>Run one full frame: pump + input, then paint (the reactive flush + layout + record happen in Paint).</summary>
    public FrameStats RunFrame()
    {
        // Seam confinement backstop: the frame pump IS the UI thread. Bind it (idempotent) + assert. Both are
        // [Conditional("FGGUARD")] — live in Debug/CI (proves single-UI-thread ownership), erased from Release/Ship.
        Threading.ThreadGuard.BindCurrent(Threading.ThreadGuard.ThreadRole.Ui);
        Threading.ThreadGuard.AssertUi();
        _lastFrameStartTicks = Stopwatch.GetTimestamp();   // frame-start stamp for RecommendedWaitMs ambient-fps pacing
        long db = 0, dt = 0;
        if (s_allocDiag) { db = GC.GetAllocatedBytesForCurrentThread(); dt = Stopwatch.GetTimestamp(); }
        long diagUiStart = db;

        _ring.Clear();
        _tracePumpedEvents = _window.PumpInto(_ring);              // 1 pump
        if (s_allocDiag) { db = Probe(SegPump, db, dt); dt = Stopwatch.GetTimestamp(); }
        int clicks = _dispatcher.Dispatch(_ring.Drain(), _ring.DrainVelocitySamples());  // 2 input dispatch (handlers write signals → schedule effects)
        if (s_allocDiag) { db = Probe(SegDispatch, db, dt); dt = Stopwatch.GetTimestamp(); }
        // Post-input warm-cadence hold: any input this frame (a pumped event or a handled click) keeps the loop rendering
        // for WarmCadenceHoldMs so a follow-up interaction pays no cold-start ramp (see field). Real window only by default.
        if (_warmCadenceEnabled && WarmCadenceHoldMs > 0f && (clicks > 0 || _tracePumpedEvents > 0))
            _warmCadenceUntilMs = _timers.NowMs + WarmCadenceHoldMs;

        // Step 4 fault injection (FG_FORCE_DEVICE_LOST=<frameN>): force a controlled DEVICE_REMOVED so the next submit
        // fails and the recovery rendezvous below is exercised on real hardware.
        if (s_forceLostFrame > 0 && _asyncActive && ++_frameOrdinal == s_forceLostFrame)
        {
            if (s_dlTrace) System.Console.Error.WriteLine($"[dl] UI: injecting device loss at frame {_frameOrdinal}");
            _device.InjectDeviceLost();
        }

        // Step 4 (async): device-lost recovery handshake. The render thread records a lost reason (a failed submit/present
        // or a bounded fence-wait timeout on a removed device). On the 0→1 edge: dirty the whole tree + relayout, ask the
        // render thread to rebuild (waking it so it reaches the recover gate), then BLOCK (render nothing) until RecoverDone
        // — then re-realize resident images and fall through to a full re-recorded frame against the rebuilt device.
        if (_deviceLost is { } dl && _asyncActive)
        {
            if (dl.RecoverRequest == 0 && _device.PollDeviceLost() != 0)
            {
                if (s_dlTrace) System.Console.Error.WriteLine($"[dl] UI: detected reason=0x{_device.PollDeviceLost():X} at frame {_frameOrdinal} → requesting recover");
                _scene.MarkAllPaintDirty();
                _needFullLayout = true;
                dl.RecoverRequest = 1;
                _renderThread!.WakeAsync();   // CRITICAL: wake the parked render loop so it reaches the recover gate
            }
            if (dl.RecoverRequest != 0)
            {
                if (dl.RecoverDone != 0)
                {
                    if (s_dlTrace) System.Console.Error.WriteLine($"[dl] UI: observed RecoverDone at frame {_frameOrdinal} → re-realizing images + resuming");
                    dl.RecoverDone = 0;
                    dl.RecoverRequest = 0;
                    _images.ReRealizeAllResident();   // re-decode resident art → re-upload to the fresh store (Step-1 handoff)
                    // fall through: the whole-tree-dirty + full-layout frame re-records everything against the rebuilt device
                }
                else
                {
                    LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, FrameMs = _frameMs };
                    return LastStats;   // block cleanly; the render thread's windowWake nudges us when RecoverDone flips
                }
            }
        }

        // Minimize gate: a minimized window paints nothing — but the pump+dispatch above MUST run so the restore
        // message lands (RecommendedWaitMs blocks indefinitely while minimized, so the loop only wakes on a message).
        // Skip Paint entirely (no record/submit/present), BEFORE the image-pump early-out below; the restore EDGE
        // forces a frame so the first visible frame paints immediately. Headless never reports Minimized (its State
        // defaults to Normal and nothing here flips it), so the headless path is unaffected.
        bool minimized = IsMinimized;
        if (_wasMinimized && !minimized) _frameNeeded = true;   // restored: repaint now
        if (_wasMinimized != minimized)
        {
            // Window-visibility EDGE → update the Activation.IsActive signal so every component's UseIsActive flips and
            // UseActivation fires. On the minimize-ENTERING edge the gate below returns BEFORE Paint's reactive flush,
            // so flush ONCE here (one-shot, on the edge only — not per idle frame) so onDeactivated runs while invisible.
            // The restore edge forced _frameNeeded above, so its onActivated rides Paint's normal flush.
            UpdateWindowVisible();
            if (minimized) _runtime.Flush();
        }
        _wasMinimized = minimized;
        if (minimized)
        {
            LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, FrameMs = _frameMs };
            // Awake-but-skipped: counts toward _framesRun + _framesMinimized (rendered:false), the wake-diag's
            // "frames spent minimized" signal. wake is recomputed here since the s_wakeDiag snapshot is below.
            if (_wakeDiag is not null) { _wakeDiag.Record(ComputeWakeReasons(), awake: true, rendered: false, reconciled: false, laidOut: false, minimized: true); _wakeDiag.MaybeReport(); }
            if (_memCensus is not null) _memCensus.MaybeReport();
            if (s_allocTypes) AllocTypeProfiler.MaybeReport();
            if (s_allocDiag)
            {
                _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
                DiagMaybeReport();
            }
            return LastStats;
        }

        // Cross-thread UI posts (HostDispatch.Post / UsePost): drain BEFORE the idle gate below. A worker that posts an
        // action Wake()s the loop (PostMessage WM_NULL), but a pending _uiPosts queue is NOT itself a wake term in
        // ComputeWakeReasons() — so without draining here, an otherwise-idle page (e.g. the migrated WindowsApi cards that
        // dropped FrameClock.Tick) would early-return at `if (!HasActiveWork)` BEFORE Paint, the only other drain (inside
        // Paint) would never run, and the posted signal writes would be stranded forever (a structural freeze, not a
        // deadlock). Running inside _runtime.Batch coalesces the actions' signal writes into one re-render and defers the
        // FrameRequested wake to the batch's end, where it sets _frameNeeded — so HasActiveWork (FrameNeeded || HasPending)
        // is true THIS frame and we fall through to Paint, whose _runtime.Flush() applies the coalesced re-render. When the
        // queue is empty this is a no-op: the loop still idles (RecommendedWaitMs==-1), so render-purity is preserved — a
        // frame is forced ONLY when a post is actually pending. No lost-wakeup: Post enqueues before Wake, so a post that
        // arrives after this drain but before the gate still posted its own WM_NULL that re-wakes the loop next iteration.
        if (!_uiPosts.IsEmpty) _runtime.Batch(DrainUiPosts);

        // Wake attribution: snapshot the mask at the idle decision point (before the image pump can flip _frameNeeded).
        WakeReasons wake = s_wakeDiag ? ComputeWakeReasons() : WakeReasons.None;

        if (!HasActiveWork)
        {
            int completed = _images.Pump();
            if (s_allocDiag) db = Probe(SegImages, db, dt);
            if (completed == 0)
            {
                LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, FrameMs = _frameMs };
                if (_wakeDiag is not null) { _wakeDiag.Record(WakeReasons.None, awake: false, rendered: false, reconciled: false, laidOut: false, minimized: IsMinimized); _wakeDiag.MaybeReport(); }
                if (_memCensus is not null) _memCensus.MaybeReport();
                if (s_allocTypes) AllocTypeProfiler.MaybeReport();
                if (s_allocDiag)
                {
                    _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
                    DiagMaybeReport();
                }
                return LastStats;
            }
            _frameNeeded = true;
            if (s_wakeDiag) wake = ComputeWakeReasons();   // a completed decode forced this paint → re-attribute (now FrameNeeded)
        }

        if (s_allocDiag) _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
        FrameStats painted = Paint(clicks);
        if (_wakeDiag is not null)
        {
            // Awake frame: classify reconciled/layout-only/record-only from FrameStats (Rendered = reconciled||layoutNeeded).
            _wakeDiag.Record(wake, awake: true, rendered: painted.Rendered, reconciled: painted.ComponentsRendered > 0,
                             laidOut: painted.Rendered, minimized: IsMinimized);
            _wakeDiag.MaybeReport();
        }
        if (_memCensus is not null) _memCensus.MaybeReport();
        if (s_allocTypes) AllocTypeProfiler.MaybeReport();
        if (RenderBudget.CompiledIn) { RenderBudget.FrameBoundary(); RenderBudget.MaybeReport(); }   // FG_RENDER_DIAG tripwire (folds away in release)
        return painted;
    }

    /// <summary>True when the host window is minimized (PAL <see cref="Pal.WindowState.Minimized"/>) — frames run
    /// while minimized are wasted work the wake diagnostics surface.</summary>
    private bool IsMinimized => _window.State == FluentGpu.Pal.WindowState.Minimized;

    /// <summary>Recompute and publish the ambient window-visibility (<c>Activation.IsActive</c>): visible IFF not
    /// minimized AND not app-suspended. Value-eq-gated by the signal, so a no-op write notifies nobody. UI-thread.</summary>
    private void UpdateWindowVisible() => _windowVisible.Value = !IsMinimized && _windowActiveApp;

    /// <summary>App-side power suspend/resume hook (opt-in): the app wires <c>PowerSession.Suspending/Resumed</c> into
    /// this via <see cref="Post"/> (power callbacks arrive off-thread) to AND a suspend gate into window visibility, so
    /// <c>UseIsActive</c>/<c>UseActivation</c> see a suspended app as inactive. The engine never references the power
    /// API — this is a documented augmentation. Call on the UI thread (marshal via <see cref="Post"/> if off-thread);
    /// idempotent and value-gated. Forces a frame so the visibility flip flushes promptly.</summary>
    public void SetWindowActive(bool active)
    {
        if (_windowActiveApp == active) return;
        _windowActiveApp = active;
        UpdateWindowVisible();
        WakeFrame();   // ensure the loop runs a frame so the UseActivation effects flush
    }

    /// <summary>Phases 3–12: flush reactive work, (scoped) re-layout, record, submit, present, effects. No pump — safe from WndProc.
    /// <paramref name="keepAlive"/> marks a repaint fired synchronously from inside an OS modal move/size loop: the submit
    /// skips the device's frame-latency throttle so the WndProc thread isn't blocked up to a vblank.</summary>
    public FrameStats Paint(int clicks = 0, bool keepAlive = false)
    {
        // Paint is reached BOTH from RunFrame (already bound) AND synchronously from the WndProc PaintRequested repaint
        // (live-resize, line ~789) which is NOT — so bind the current (message/UI) thread here too. Paint is always the
        // UI thread; the render-thread seam's AssertUi (DrawListArenaRing.WriteFront / SceneFramePublisher.Publish) runs
        // on this path, so both entries must be bound (the seam's AssertUi in SceneFramePublisher.Publish runs here).
        // Idempotent for the same role; erased from Release with ThreadGuard.
        Threading.ThreadGuard.BindCurrent(Threading.ThreadGuard.ThreadRole.Ui);
        if (_inPaint) { _frameAfterPaint = true; return LastStats; }
        _inPaint = true;
        // Publish the effective device scale for scroll content-transform device-pixel rounding (before reconcile/layout).
        _scene.DeviceScale = _window.Scale <= 0f ? 1f : _window.Scale;
        _reconciler.FrameEpoch++;   // one tick per paint — caps a warming virtual list's cold-realize grow to 1 batch/frame
        long diagUiStart = s_allocDiag ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            // Single-instance activation redirect: deliver a pending second-launch payload (set by the UI-thread
            // ActivationRedirected subscription) to app code BEFORE the reactive flush, so any signal writes the handler
            // makes are picked up by _runtime.Flush() and rendered this same frame. UI-thread only — no lock needed.
            if (_pendingActivation is { } activation)
            {
                _pendingActivation = null;
                ActivationRedirected?.Invoke(activation);
            }
            // OS color-settings change: deliver to app code BEFORE the flush (same rationale as activation above) so the
            // handler's Tok.Use/SetAccent + RequestThemeTransition are picked up by THIS frame's theme detection + flush.
            if (_pendingSystemColors)
            {
                _pendingSystemColors = false;
                SystemColorsChanged?.Invoke();
            }

            long frameStart = Stopwatch.GetTimestamp();
            _reconciler.BeginRenderCensus();
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.WindowResize, _window.InModalLoop);
            // Scroll-coincident reconcile → snap, don't FLIP (perf plan W2-P2.2): while a user scroll is actually moving
            // content (an offset REALLY advanced last frame — the latch below the phase-7 scroll tick — AND the 0.45s
            // post-scroll hold is live), a reconcile that lands this frame must not seed FLIP projections: rows/cards
            // flying to their new slots through a scrolling viewport reads as jank and burns structural tracks per frame.
            // Set BEFORE CaptureProjections so ApplyProjections takes its suppressed-snap branch. Gating on the offset
            // latch (not the hold alone) keeps a click-triggered expand right after scrolling FLIPping normally.
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.Scroll,
                _anyOffsetWroteLastFrame && frameStart < _mainScrollHoldUntil);
            // FG_RESIZE_DIAG: per-tick segment timing of the modal-loop keep-alive paint. Captured only when both the flag
            // is on AND this is a keep-alive tick — zero work / zero alloc otherwise (the normal hot path is untouched).
            bool diagTick = keepAlive && s_resizeDiag;
            double ensureMs = 0, layoutMs0 = 0;
            long segStart = diagTick ? Stopwatch.GetTimestamp() : 0;
            bool resized = EnsureSize(keepAlive);
            if (diagTick) { ensureMs = ElapsedMs(segStart); segStart = Stopwatch.GetTimestamp(); }

            // Modal-loop keep-alive idle skip. During a title-bar MOVE or edge RESIZE the OS runs its own modal
            // message loop on THIS (WndProc) thread and drives keep-alive paints — the 8 ms WM_TIMER, WM_SIZE,
            // WM_MOVE — synchronously, with the app's own frame loop suspended. Render a keep-alive tick ONLY when
            // something actually needs it; otherwise skip the whole pipeline (the last presented frame stays on screen).
            //
            // Two bail cases:
            //  (1) Nothing is awake at all (ComputeWakeReasons == None) — the classic pure-move idle skip.
            //  (2) We're INSIDE the modal loop and this tick isn't a real resize, has no pending layout/UI work, AND no
            //      one-shot transition is in flight — bail even though an AMBIENT wake (playback seek-ticker, caret
            //      blink, perpetual brush/spinner loop) is live. Measured: a single edge-resize-while-playing fired 69
            //      real resizes but 564 REDUNDANT present-only paints (~1.8s of wasted WndProc time, present-blocked up
            //      to 62ms each) because the seek-ticker wake kept defeating case (1). Those PERPETUAL animations can't
            //      advance mid-drag anyway (the frame loop is suspended), so painting the unchanged content for them is
            //      pure waste that starves the modal loop → felt as sluggish resizing.
            //      The AnimIsAmbient() guard is the exception that keeps responsive-control motion alive: a ONE-SHOT
            //      layout transition (a PlayerBar button's Enter/Exit pop when it crosses a responsive breakpoint mid-
            //      resize) is a finite track, so AnimIsAmbient() is false and we DON'T bail — the button animates in/out
            //      while only the perpetual playback ticker is dropped. A real resize / band-crossing relayout still
            //      paints; WM_EXITSIZEMOVE flushes any deferred work in one settle frame, so nothing visible is lost.
            //      Warming virtual lists (FrameNeeded from HasWarmingVirtuals) and any other essential wake bit still
            //      paint — OnlyAmbientWakeReasons masks them off so a seek ticker cannot starve mid-drag refill.
            var wakeReasons = ComputeWakeReasons();
            if (keepAlive && !resized && _everLaidOut && !_needFullLayout
                && _uiPosts.IsEmpty && !_scene.AnyLayoutDirty
                && (wakeReasons == WakeReasons.None
                    || (_window.SizedInModalLoop && AnimIsAmbient() && OnlyAmbientWakeReasons(wakeReasons))))
                return LastStats;

            var layoutSize = LayoutSizeForFrame(keepAlive);
            PublishViewport(layoutSize);

            // FLIP "First": capture presented rects of layout-animated nodes BEFORE the reconcile/relayout that moves them.
            // Skip on the very first layout — freshly-mounted nodes are unmeasured (0-size), so FLIPping them would animate
            // a spurious 0→full reveal that clips content. (Nodes mounted on later frames are created during Flush, AFTER
            // this capture, so they're correctly never captured.)
            // Also skip on a window RESIZE: the pre-resize rects are stale, so FLIPping them animates the resize delta —
            // a content slide that, when a NavigationView pane also auto-collapses at the breakpoint, leaves a stale
            // presented translation (content shifted, backdrop revealed). Resizes SNAP; state-driven changes still FLIP.
            bool willReconcile = _runtime.HasPending || _needFullLayout;
            bool capturedProjections = false;
            long db = 0, dt0 = 0;
            if (s_allocDiag) { db = GC.GetAllocatedBytesForCurrentThread(); dt0 = Stopwatch.GetTimestamp(); }
            if (willReconcile && _everLaidOut && !_scene.Root.IsNull && !resized)
            {
                _projectBefore.Clear();
                CaptureProjections();
                capturedProjections = _projectBefore.Count > 0;
            }
            else if (resized && _everLaidOut && !_scene.Root.IsNull)
            {
                // The window actually changed size this frame. Any in-flight FLIP/structural track still holds a
                // PRE-resize translate + presented size: the (re)layout below re-lays each cell to a new slot, but the
                // stale LocalTransform would draw it at newSlot+staleOffset (the overlap) and a SizeMode.Relayout track
                // would keep forcing li.Width/Height to a stale interpolated size every tick (the detached labels + the
                // per-cell subtree relayout that collapses FPS). Cancel them and snap each FLIP node onto the geometry
                // the (re)layout is about to solve — bounds land clean. This is the WindowResize suppression widened past
                // the modal loop: maximize / restore / snap / programmatic resizes arrive as a plain WM_SIZE with no
                // InModalLoop, so gating the cancel on `resized` (not just _window.InModalLoop) covers them too. Capture
                // is already skipped on a resize (above), so no NEW projection starts this frame either.
                _anim.CancelStructuralAll(_scene.BoundsAnimatedNodes);
            }
            if (s_allocDiag) { db = Probe(SegFlip, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            if (s_motionDiag && (willReconcile || capturedProjections))
                System.Console.Error.WriteLine(
                    $"[motion-diag] frame={_frameOrdinal} keepAlive={keepAlive} resized={resized} hasPending={_runtime.HasPending} needFullLayout={_needFullLayout} capture={_projectBefore.Count} suppressed={Motion.LayoutTransitionsSuppressed}");

            long before = GC.GetAllocatedBytesForCurrentThread();

            // Drain cross-thread UI posts so their signal writes land in THIS flush. RunFrame already drained them before
            // its idle gate, so on the normal frame path this is a no-op on an empty queue; it earns its keep on the
            // Paint-ONLY path (the PaintRequested keep-alive fired from inside an OS modal move/size loop, which bypasses
            // RunFrame entirely) — there a post that arrived mid-drag still applies this frame instead of being stranded.
            if (!_uiPosts.IsEmpty) _runtime.Batch(DrainUiPosts);
            // Frame-clock timers (UseTimeout/UseInterval/UseDebouncedValue/UseThrottledValue): fire due callbacks INSIDE
            // the hot-phase window, before the flush, so their signal writes coalesce into THIS frame's re-render (same
            // rationale as the UI-post drain above). Skipped when nothing is armed → 0-alloc on every frame that uses no
            // timer, and 0-alloc on a quiet frame with an armed-but-not-due timer (Drain is one comparison then returns).
            if (_timers.Count > 0) _runtime.Batch(_drainTimers);
            // Drag epoch: while a typed drag is live, bump each frame so a DragPreviewLayer re-renders and follows the
            // cursor; bump once more when it ends so the preview tears down. Only the preview subtree re-renders.
            bool dragActive = _dispatcher.DragDrop.IsActive;
            if (dragActive || _dragWasActive) _dragEpoch.Value = _dragEpoch.Peek() + 1;
            _dragWasActive = dragActive;
            // Live re-theme: a Tok.Use/SetAccent bumped Tok.Epoch (or RequestThemeTransition was called). Re-render every
            // mounted component IN PLACE so each re-reads the new token set, and arm the cross-fade window around EXACTLY
            // the flush that runs those re-renders (and the virtuals re-flush) so the color diffs animate uniformly —
            // then disarm so ordinary logical-state flips keep their per-element timing. No remount: state survives.
            bool themeChanged = Tok.Epoch != _lastThemeEpoch || !float.IsNaN(_pendingThemeMs);
            float themeMs = !float.IsNaN(_pendingThemeMs) ? _pendingThemeMs : 250f;
            _pendingThemeMs = float.NaN;
            if (themeChanged)
            {
                _lastThemeEpoch = Tok.Epoch;
                OnApplyThemeMaterial?.Invoke(Tok.Theme == ThemeKind.Dark);   // instant OS material flip (cannot cross-fade)
                _reconciler.SetThemeTransition(themeMs);
                _reconciler.RethemeAll();
            }
            bool virtualsChanged = false;
            double reactiveFlushMs = 0, virtualRealizeMs = 0;
            try
            {
                long tRx0 = Stopwatch.GetTimestamp();
                _runtime.Flush();                              // 3–5 apply scheduled re-renders (render-effects reconcile) + bindings
                long tRx1 = Stopwatch.GetTimestamp();
                virtualsChanged = _reconciler.ReRealizeVirtuals();   // virtual boundary re-realize (granular)
                long tVr1 = Stopwatch.GetTimestamp();
                if (virtualsChanged && _runtime.HasPending) _runtime.Flush();   // bound-row rebinds (slot signal writes) land THIS frame
                long tRx2 = Stopwatch.GetTimestamp();
                reactiveFlushMs = ToMs(tRx1 - tRx0) + ToMs(tRx2 - tVr1);
                virtualRealizeMs = ToMs(tVr1 - tRx1);
            }
            finally { if (themeChanged) _reconciler.SetThemeTransition(float.NaN); }
            bool reconciled = _reconciler.ConsumeReconciled() || virtualsChanged;
            long tFlush = Stopwatch.GetTimestamp();   // always-on segment timing (FrameStats.*Ms) — see below
            // Spike-gated type roster (FG_RENDER_CENSUS): one line when FlushMs ≥ 12 or comps are high. Peek render
            // count WITHOUT consuming it (ConsumeRenderCount runs later when assembling LastStats).
            int censusComps = _reconciler.PeekRenderCount();
            _reconciler.MaybeDumpRenderCensus(ToMs(tFlush - frameStart), reactiveFlushMs, virtualRealizeMs, censusComps,
                _anyOffsetWroteLastFrame || Stopwatch.GetTimestamp() < _mainScrollHoldUntil);
            if (s_allocDiag) { db = Probe(SegFlush, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            bool layoutNeeded = _needFullLayout || reconciled || _scene.AnyLayoutDirty;
            string layoutPath = "none";
            _layout.ResetFrameDiagCounters();   // frame start for the measure/arrange/text-miss counters read into FrameStats
            _invalidator.BeginFrame(_timers.NowMs);   // reset the per-frame relayout-escape counter (FrameStats.RootRelayoutEscapes)
            if (layoutNeeded && !_scene.Root.IsNull)
            {
                if (_needFullLayout || !_everLaidOut)
                {
                    layoutPath = "full";
                    _layout.Run(_scene.Root, layoutSize);      // 6 full layout: first frame / resize / DPI / root change
                    _needFullLayout = false;
                    _everLaidOut = true;
                }
                else
                {
                    layoutPath = "scoped";
                    _invalidator.RunDirty(layoutSize);         // 6 scoped relayout: only dirty subtrees, firewalled at boundaries
                }
                _scene.ClearLayoutDirty();

                // D1 realize-after-layout (bounded): ArrangeViewport flags viewports whose realized window no longer
                // covers the viewport size it just published (a mount realizes against a hint BEFORE any layout; a
                // relayout can also grow the host). Re-realize + scoped relayout here so the FIRST presented frame
                // already shows the real rows — max 2 passes (a pass realizes the exact computed window, so a
                // further pass only fires on measured-extent drift; any residue is caught by the next frame's
                // pre-layout ReRealizeVirtuals). Cold realize edge only — steady frames never enter the loop.
                for (int realizePass = 0; realizePass < 2 && _reconciler.ReRealizeVirtuals(); realizePass++)
                {
                    if (_runtime.HasPending) _runtime.Flush(); // bound-slot rebinds (RowBind) land THIS frame
                    _reconciler.ConsumeReconciled();           // realize mounts are folded into this frame's layout
                    reconciled = true;
                    _invalidator.RunDirty(layoutSize);
                    _scene.ClearLayoutDirty();
                }
            }

            DrainLayoutEffects();                              // 6.5 layout effects (Bounds valid)
            _connected.ReducedMotion = Motion.ReducedMotion;   // 6.5 connected-animation: remember tag rects, seed flies to arrived dests, expire stale
            _connected.Tick65();
            // Responsive show/hide "make room": nodes that mounted with a SizeMode.Reflow enter now have their natural
            // size — ease the main-axis LAYOUT size 0→that so neighbours reflow as the entrant reveals. Seeded here
            // (post-layout, BEFORE the anim tick) so the first ticked size is ~0 and RunReflowLayout re-solves siblings
            // before record — no 1-frame snap. RunReflowLayout is NOT resize-gated, so this animates even mid window-drag.
            if (_anim.PendingEnterReflow.Count > 0)
            {
                var pend = _anim.PendingEnterReflow;
                for (int i = 0; i < pend.Count; i++)
                {
                    var pn = pend[i];
                    if (!_scene.IsLive(pn)) continue;
                    var par = _scene.Parent(pn);
                    bool horiz = !par.IsNull && _scene.Layout(par).Direction == 0;
                    ref RectF pb = ref _scene.Bounds(pn);
                    _anim.SeedEnterReflow(pn, horiz, pb.W, pb.H);
                }
                pend.Clear();
            }
            // Exit-reflow mirror: a container that lost a SizeMode.Reflow child this frame eases from its with-child size
            // (snapshotted in Remove, pre-layout) → its now-solved without-child size, so the sibling reflows smoothly
            // instead of snapping into the freed space.
            if (_anim.PendingExitReflow.Count > 0)
            {
                var pex = _anim.PendingExitReflow;
                for (int i = 0; i < pex.Count; i++)
                {
                    var (pn, fromW, fromH, spec) = pex[i];
                    if (!_scene.IsLive(pn)) continue;
                    var row = _scene.Parent(pn);
                    bool horiz = !row.IsNull && _scene.Layout(row).Direction == 0;
                    var nb = _scene.Bounds(pn);
                    _anim.SeedReflowResize(pn, horiz, horiz ? fromW : fromH, horiz ? nb.W : nb.H, spec);
                }
                pex.Clear();
            }
            if (reconciled) DumpSceneOnce(layoutSize);
            if (diagTick) { layoutMs0 = ElapsedMs(segStart); }   // flush/reconcile/relayout/layout-effects span (FG_RESIZE_DIAG)
            long tLayout = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegLayout, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            if (capturedProjections) ApplyProjections();       // FLIP "Last+Invert+Play"
            // fps consistency (root fix): if the loop paced INTO this frame from a throttled (ambient 30 Hz) or idle
            // cadence AND this frame now drives interactive or one-shot motion (scroll/hover/drag/repeat, or a
            // connected-animation fly / non-loop transition), the frame clock's pending delta is the stale throttle gap,
            // not a real interval. Drop it so the first active frame advances ~one frame instead of leaping ~34 ms — the
            // root of "scroll/connected animations feel 24 fps then 120 fps." Steady display-rate frames (prev wait 0)
            // and genuine mid-scroll GC hitches are untouched; steady ambient frames never enter (no interactive work).
            // Resync fires ONLY when stepping UP from a genuinely THROTTLED/idle cadence (ambient 30 Hz, HUD 10 Hz, or a
            // blocked idle) to display rate — feeding that stale throttle gap into the animators would make one-shot motion
            // LEAP on frame 1. A frame already AT display rate must NOT resync. Sync display rate waits 0; ASYNC display
            // rate waits AsyncDisplayPaceMs (the free-spin cap, RecommendedWaitMsCore) — so BOTH are "already at display
            // rate." Excluding AsyncDisplayPaceMs is load-bearing: without it EVERY async animating frame resynced →
            // NextDeltaMs()==0 every frame → one-shot enter transitions froze at their initial (invisible) state, so
            // animated content (sidebar sections, home cards) never appeared on-screen while non-animated chrome did.
            if (_lastWaitMs != 0 && _lastWaitMs != AsyncDisplayPaceMs)
            {
                WakeReasons stepUp = ComputeWakeReasons();
                if ((stepUp & LatencySensitiveWake) != 0 || (_anim.HasActive && !AnimIsAmbient()) || _connected.HasActive)
                    _frameTime.Resync();
            }
            float dtMs = _frameTime.NextDeltaMs();
            _frameClockMs += dtMs;                             // frame-clock timer base (headless: the deterministic FixedFrameTimeSource step; ignored by the real-window wall clock)
            _anim.Tick(dtMs);                                  // 7 animation (transform/opacity/presented-size — never relayout)
            _reconciler.FinalizeKeepAliveTransitions();         // 7 park retained outgoing pages after their exit settles
            _inputHooks.RunAfterAnimations();                  // 7.1 tree lifecycle finalizers (overlays) before record/present
            RunIncrementalLayout();                            // 7 scoped subtree relayout for SizeMode.Relayout
            RunReflowLayout(layoutSize);                       // 7 boundary-scoped re-solve for SizeMode.Reflow (smooth reflow)
            // 7.2 video pump: engine-invoked per-binding pump (the video pump / viewport writes LEAVE the control's
            // Render — Render is pure). Each registered media element reads its now-final laid-out area + this scale and
            // writes value-gated video intents; the phase-11.5 Drain flushes them the same frame. Single-writer
            // enforced by the registry (fullscreen ownership transfer). Runs on every backend incl. headless so the
            // pump cadence is FRAME-driven not render-driven; zero-alloc (mount-registered closures over a fixed array).
            _videoSurfaces.PumpAll(_scene.DeviceScale);
            ReclaimSettledOrphans();                           // 7 free settled exit orphans
            _connected.Settle();                               // 7 retire landed shared-element flies (reveal dest, unpin, free overlay)
            _connected.SyncDetached();                         // 7 flag-gated rebuild: mirror the engine-animated fly into its DetachedNode snapshot (RecordDetached draws it)
            // 7 eased hover/press: HoverT/PressT now driven by the engine's HoverFade/PressFade tracks (ticked in _anim.Tick above); InteractionAnimator deleted
            // 7 implicit BrushTransition: the cross-fade T is now driven by the unified engine (AnimChannel.BrushFade,
            // seeded at reconcile); the separate per-frame AdvanceBrushAnims ticker is deleted.
            // (TickTouchpad is gone — scroll phase events apply 1:1 at dispatch; design §6/§12.)
            if (FluentGpu.Foundation.ScrollTrace.On)
                FluentGpu.Foundation.ScrollTrace.Frame(dtMs, _tracePumpedEvents, _scrollAnim.HasActive || _dispatcher.GestureActive);
            // scroll-feel-rework-v2 §4.1: the TouchpadTracking resampler targets frameT − 5ms. Feed the frame's QPC clock
            // (matches the dispatcher's per-packet QpcTicks). Headless leaves it 0 → the resampler uses the latest deposited
            // sample (no synthesis), preserving gate determinism.
            if (!_isHeadless) _scrollAnim.FrameQpcSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            _scrollAnim.Tick(dtMs);                            // 7 smooth scroll + fling + overscroll spring + scrollbar fade (the engine-owned integrator)
            long scrollHoldNow = Stopwatch.GetTimestamp();
            if (_scrollAnim.AnyUserScrollActiveThisFrame)
            {
                _selfBlurHoldUntil = scrollHoldNow + SelfBlurHoldAfterScrollTicks;
                _mainScrollHoldUntil = scrollHoldNow + MainScrollHoldTicks;
            }
            // Latch for NEXT frame's MotionSuppressionSource.Scroll decision (set at the top of Paint, before FLIP
            // capture): did any viewport's offset actually advance THIS frame? Captured here — right at the scroll-apply
            // site — so the next Paint reads last frame's real motion, not the whole hold window.
            _anyOffsetWroteLastFrame = _scrollAnim.AnyOffsetWroteThisFrame;
            bool holdSelfBlurForScroll = scrollHoldNow < _selfBlurHoldUntil;
            bool scrollActive = holdSelfBlurForScroll || _scrollAnim.AnyOffsetWroteThisFrame;
            _images.SuppressReveals = scrollActive;
            _images.ScrollThrottled = scrollActive;   // upload-burst → fence-stall guard (the safe lever; triple-buffer hung the Adreno)
            ScrollBindEval.ApplyPinAndFlagPass(_scene);       // 7 generic scroll-bind pins + the predicate-flag channel (sticky etc.)
            ScrollBindEval.RunObservers(_scene);              // 7 change-only scroll-geometry observers (pull-to-refresh / analytics)
            _repeat.Tick(dtMs);                                // 7 RepeatButton auto-repeat (held → re-fire click)
            _caretBlinker.Tick(dtMs);                          // 7 focused-editor caret blink (toggles TextEditState)
            _dispatcher.DragDrop.Tick(dtMs);                   // 7 E5 edge auto-scroll (drag near an overflowing viewport edge)
            _dispatcher.Drag.Tick(dtMs);                       // 7 E5 ghost: spring-lag easing + re-pin over the scrolled origin
            _dispatcher.TickGestureArenas(dtMs);               // 7 §7A arena timer tick (Hold long-press promotion on idle-held frames)
            long tAnim = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegAnim, db, dt0); dt0 = Stopwatch.GetTimestamp(); }
            _images.Pump();                                    // 7.5 apply finished decodes + evict
            _images.Tick(dtMs);
            long tImagePump = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegImages, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            // Scroll re-realize catch-up (phase 7.6): the fling/smooth scroll animators above advanced the content's
            // -ScrollOffset transform AFTER this frame's pre-layout ReRealizeVirtuals, so a fast fling would record the
            // viewport translated PAST the realized rows — the leading edge draws through (FG_PROBE=scroll-flicker).
            // Re-realize for the just-advanced offset + scoped relayout the newly mounted rows so the recorded frame's
            // realized window matches the offset it draws. No-op on steady frames (ReRealizeVirtuals returns false when
            // nothing is VirtualRangeDirty); bounded to 2 passes like the cold realize edge in the layout block above.
            for (int scrollPass = 0; scrollPass < 2 && _reconciler.ReRealizeVirtuals(); scrollPass++)
            {
                if (_runtime.HasPending) _runtime.Flush();   // bound-slot rebinds (RowBind) for the newly realized rows
                _reconciler.ConsumeReconciled();
                reconciled = true;                           // this frame DID realize+relayout — keep FrameStats.Rendered honest
                _invalidator.RunDirty(layoutSize);
                _scene.ClearLayoutDirty();
            }
            long tRealizeCatchup = Stopwatch.GetTimestamp();   // 7.6 cost was invisibly charged to RecordMs — split it out

            // Stuck-hover fix (input-a11y.md §5.4/§15 — "hover re-resolves when content moves under a stationary pointer,
            // not just layout commits"): a scroll offset write OR a reconcile/relayout this frame moved content under a
            // possibly stationary mouse/pen cursor, and a hit-test only rides real PointerMoves — so a STATIONARY cursor
            // has no other refresh hook. The offset-write case is the fling/smooth-scroll leg; the layoutNeeded case is
            // any commit that TRANSLATES bounds out from under the cursor with no move to re-resolve it (the sidebar
            // collapse snapping its 240→56 rail + the drag-grip overlay it carries is the canonical instance — the grip
            // keeps NodeFlags.Hovered, so its hover-only seam hairline stays lit until the next real move). Re-resolve
            // NOW — AFTER the re-realize catch-up, so the hit-test sees the finalized realized/transformed rows and a
            // rebound virtual slot's Unmark (Reconciler) can't clobber the refreshed hover. Gated like the scroll path —
            // only on frames that actually wrote offsets OR relaid out (`layoutNeeded` = full/scoped layout ran; steady
            // idle/paint-only frames never enter), never per-idle-frame. The dispatcher self-gates mouse/pen + a valid
            // last position + no touch pan/item-drag. One hit-test; zero-alloc scalar walk through the hover chokepoints.
            if (_scrollAnim.AnyOffsetWroteThisFrame) _dispatcher.RefreshHoverAfterScroll();
            // Layout-move stuck-hover (input-a11y.md §5.4/§15): a reconcile/relayout this frame — NOT a scroll write — can
            // TRANSLATE a node out from under a STATIONARY mouse/pen cursor with no PointerMove to re-resolve it (the sidebar
            // collapse snapping its 240→56 rail carries its hover-only resize grip away, leaving the grip's seam hairline lit
            // until the next real move). Gated on a frame that actually relaid out (`layoutNeeded` = full/scoped layout ran;
            // steady idle/paint-only frames never enter) — but NOT when a scroll write already refreshed above. The dispatcher
            // self-gates mouse/pen + a valid position + no touch pan/item-drag, and no-ops unless the hit actually CHANGED.
            else if (layoutNeeded) _dispatcher.RefreshHoverAfterLayoutMove();

            ScrollBindEval.ApplyContinuousPass(_scene);        // 7.7 steady-frame scroll binds (collapsed hero / fade copy)

            var focus = new FocusVisualStyle(Tok.FocusOuter, Tok.FocusInner, Tok.FocusThickness);
            // WinUI text-edit decor brushes: selection = TextControlSelectionHighlightColor (= AccentFillColorSelectedTextBackgroundBrush),
            // selected glyphs = TextOnAccentFillColorSelectedTextBrush, caret = the text foreground.
            var textEdit = new TextEditStyle(Tok.AccentSelectedTextBackground, Tok.TextOnAccentSelectedText, Tok.TextPrimary);
            UpdateDynamicDiagnosticsText();
            if (s_allocDiag) { db = Probe(SegDynText, db, dt0); dt0 = Stopwatch.GetTimestamp(); }   // alloc-05: dyntext interning was untracked
            // Out-of-bounds popup subtrees render into their OWN popup windows — exclude them from the main pass
            // (they stay in the one SceneStore for layout/hit-test; only their pixels move).
            _popupSkipRoots.Clear();
            for (int i = 0; i < _popupWindows.Count; i++)
                if (!_popupWindows[i].Root.IsNull && _scene.IsLive(_popupWindows[i].Root))
                    _popupSkipRoots.Add(_popupWindows[i].Root);
            SpanReuseDisabledReason spanDisable = SpanReuseDisabledReason.None;
            // Per-node record-dirty carries reconcile/layout/image invalidation — no window-global SceneChanged/Layout/ImageContent kills.
            // W5 spatial scoping: PopupWindows (skipRoots) + Detached (connected-anim fly anchors) NO LONGER kill span reuse
            // globally — the recorder blocks only their ancestor chains (skipRoots it already sees; the fly anchors arrive via
            // reuseBlockRoots below). Only whole-canvas events (Resize/ModalPaint) stay global here.
            if (resized) spanDisable |= SpanReuseDisabledReason.Resize;
            if (keepAlive && _window.SizedInModalLoop) spanDisable |= SpanReuseDisabledReason.ModalPaint;
            _connected.CollectReuseBlockRoots(_reuseBlockRoots);
            bool imageFadeActive = _images.HasActiveCrossfades;
            _imageCrossfadeWasActive = imageFadeActive;
            if (++_damageEpoch == 0) _damageEpoch = 1;   // nonzero (0 = "no carve-out info" sentinel for the compositor)
            var recordStats = SceneRecorder.Record(_scene, _drawList, _images, in focus, Tok.ScrollThumb, Tok.AcrylicFlyout.Fallback, in textEdit,
                CollectionsMarshal.AsSpan(_popupSkipRoots), holdSelfBlurForAnyUserScroll: scrollActive || _scrollAnim.AnyOffsetWroteThisFrame,
                spans: _spanTable, spanReuseDisabled: spanDisable,
                // Damage the band any structural-track cancel (drag-suppression snap @ ApplyProjections, resize snap @
                // CancelStructuralAll above) vacated this frame — else the ghost rail persists. AsSpan is alloc-free.
                pendingStructuralDamage: CollectionsMarshal.AsSpan(_anim.PendingStructuralDamage), // 8 record
                damageEpoch: _damageEpoch, // §2.3/E9 own-subtree carve-out epoch
                reuseBlockRoots: CollectionsMarshal.AsSpan(_reuseBlockRoots)); // W5 spatial scoping: connected-anim fly anchor chains to block
            _anim.PendingStructuralDamage.Clear();   // retains capacity → no steady-state alloc
            SceneRecorder.RecordDetached(_scene, _drawList, _images, _connected.Detached, _scene.OverlayClip);   // 8 detached fly snapshots (flag-gated rebuild; no-op when none)
            RecordPopupWindows(in focus, in textEdit);         // 8b record each popup window's subtree DrawList
            bool imageContentChanged = _recordedImageContentEpoch != _images.ContentEpoch;
            _recordedImageContentEpoch = _images.ContentEpoch;
            // 8b′ probe capture (WAVEE_LYRICS_ADVANCE_PROBE): snapshot the designated viewports' scroll state HERE — before
            // the ClearTransformDirty below wipes the content-node TransformDirty bit that drove this frame's DoF defer.
            CaptureProbeScroll(ProbeLyricsViewport, out int probeLyMode, out bool probeLyUser, out bool probeLyDirty);
            CaptureProbeScroll(ProbeMainViewport, out int probeMainMode, out bool _, out bool probeMainDirty);
            // 8c consume the frame's motion bits (the glyph-snap gate read them during record). A motion frame queues ONE
            // settle frame: the last moved frame recorded its text unsnapped, so the trailing static record re-snaps crisp.
            bool transformWrote = _scene.AnyTransformWrote;
            // A bake is already bounded to ONE adaptive, downscaled job per cadence interval. Pause only for work that
            // represents direct manipulation or a structural commit in THIS frame. Image cross-fades, ordinary entrance
            // motion and unrelated texture uploads can remain active for hundreds of milliseconds while a page fills;
            // treating those as a global quiet-frame prerequisite made a visible editorial card stay crisp long after its
            // source was resident. The queue now gets its first chance on the next non-structural, non-input frame (often
            // the source-completion frame itself), while scroll/drag remains protected from the one-shot GPU pass.
            _bakedBlurQueue.Paused = scrollActive || reconciled || layoutNeeded
                || clicks > 0 || _tracePumpedEvents > 0
                || _dispatcher.Drag.IsActive || _dispatcher.DragDrop.IsActive;
            if (transformWrote) { _frameAfterPaint = true; _scene.ClearTransformDirty(); }
            _scene.ClearRecordDirty();
            long tRecord = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegRecord, db, dt0); dt0 = Stopwatch.GetTimestamp(); }
            // Modal-loop repaint (WM_EXITSIZEMOVE settle): present at SyncInterval 0 + skip the latency waitable so the
            // WndProc thread isn't blocked up to a vblank. Mid-drag resize is deferred (no keep-alive paints); this path
            // runs once on mouse-up with the final client size.
            // Skip-submit gate (idle/slow-change power, finding #3a): when this frame mutated nothing the recorder reads
            // (no reconcile, no relayout, no transform write) AND the recorded command stream is byte-identical to the last
            // PRESENTED frame, the already-presented front buffer is still correct — elide the GPU submit + Present (the
            // dominant ~2.5ms/frame cost at rest). The cheap flags short-circuit so ACTIVE frames never hash; the hash
            // confirms byte-identity for paint-channel / image-state changes that set no flag. Conservative: steady main
            // window only (presented before, no resize, not a modal keep-alive, no interleaving popup windows). A playback
            // playhead quantized to whole pixels (SeekBar) lands on the same stream most frames, so this fires during play.
            // Active image reveals resolve at replay time — defeat skip-submit while fades are live.
            bool maybeUnchanged = _everLaidOut && !resized && !keepAlive && _popupWindows.Count == 0
                && !reconciled && !layoutNeeded && !transformWrote
                && !imageContentChanged
                && !_device.HasPendingUploads
                && !_bakedBlurQueue.HasRunnableJob
                && !_images.HasActiveCrossfades;
            ulong dlHash = maybeUnchanged ? DrawListHash(_drawList.Bytes, _drawList.SortKeys) : 0UL;
            bool skipSubmit = maybeUnchanged && dlHash == _lastPresentedDrawListHash;
            RememberDeviceLostFrame(clicks, keepAlive, resized, reconciled, layoutNeeded, transformWrote,
                maybeUnchanged, skipSubmit, in recordStats, frameStart, tFlush, tLayout, tAnim, tRecord);
            long subStart = (keepAlive && s_resizeDiag) ? Stopwatch.GetTimestamp() : 0;
            long tSubmitDone, tSubmit, hotAlloc;
            if (skipSubmit)
            {
                _framesSkippedSubmit++;
                _lastFrameSkippedSubmit = true;   // no Present happened → RecommendedWaitMs applies the pacing floor
                hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
                tSubmitDone = tSubmit = Stopwatch.GetTimestamp();
            }
            else
            {
                // Render-thread seam (Cut A): the UI records into _drawList and PUBLISHes it (copied into a FREE slot's
                // render-readable arena — PickFreeSlot makes the arena reuse safe for every mode). Step 1 (inline,
                // default): the UI submits from the acquired arena — byte-identical to a direct submit. Step 4
                // (FG_RENDER_THREAD): the fgpu-render thread submits/presents; the UI BLOCKS in DrainSync (force-sync).
                // Step 5 (FG_RENDER_ASYNC): the UI WakeAsyncs and PROCEEDS — the render thread presents on its own
                // timeline (the smoothness win: the GPU fence-wait no longer bounds back to the UI thread).
                var submitInfo = new FrameInfo(FrameSizePx(keepAlive), _window.Scale, Clear, recordStats.Damage, _images.ClockMs, _damageEpoch);
                if (resized && keepAlive) _device.HintSettlePresent();
                _renderSeam.Publish(_drawList.Bytes, _drawList.SortKeys, in submitInfo, suppressVsync: keepAlive);
                if (_renderThread is not null)
                {
                    if (_asyncActive) _renderThread.WakeAsync();   // async: UI does NOT wait (present happens later, render-side)
                    else _renderThread.DrainSync();                  // force-sync: block until the render thread presented
                    tSubmitDone = Stopwatch.GetTimestamp();          // async: present is off-thread; force-sync collapses the boundary
                }
                else
                {
                    if (keepAlive) { _device.SuppressVsyncOnce(); _device.SuppressLatencyWaitOnce(); }
                    try
                    {
                        if (_renderSeam.TryAcquire(out var rf))
                            _device.SubmitDrawList(_renderSeam.Bytes(rf), _renderSeam.SortKeys(rf), in rf.Submit); // 10 submit
                        tSubmitDone = Stopwatch.GetTimestamp();     // boundary: SubmitDrawList done, Present not yet called
                        _swapchain.Present();                       // 11 present (UI thread)
                    }
                    catch (Exception ex)
                    {
                        if (!TryRecoverForegroundDeviceLost(ex, clicks)) throw;
                        return LastStats;
                    }
                }
                if (maybeUnchanged) _lastPresentedDrawListHash = dlHash;   // track the stream only across quiet runs (active frames don't hash)
                _lastFrameSkippedSubmit = false;   // a real submit/present paced this frame
                hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
                tSubmit = Stopwatch.GetTimestamp();
            }
            if (s_allocDiag) { db = Probe(SegSubmit, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            // 11.5 — flush queued video-surface intents into the composited presenter (render thread; the hole-punch
            // rides this same frame turn). GUARDED on a non-null presenter, so it is a no-op on the headless seam and on
            // an opaque (non-composited) window — the zero-alloc gates never execute this path. Internally cheap: the
            // registry short-circuits when nothing is dirty. Targets THIS host's OWN swapchain's presenter (not the
            // device primary), so a second AppHost driving a detached video window composites into ITS window's DComp
            // root — for the primary host `_swapchain` IS the primary, so this is behaviorally identical there.
            if (_device.GetVideoPresenter(_swapchain) is { } vp) _videoSurfaces.Drain(vp, _window.Scale);

            DrainPassiveEffects();                             // 12 passive effects
            _strings.Tick();                                   // 12.5 reclaim released text ids (behind the reader quarantine)
            if (s_allocDiag) { db = Probe(SegEffects, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            UpdateFrameTiming(frameStart);
            int componentsRendered = _reconciler.ConsumeRenderCount();
            if (keepAlive && s_resizeDiag)
                ReportResizeTick(frameStart, ensureMs, layoutMs0, subStart, resized, layoutPath,
                    componentsRendered, recordStats.NodesVisited, _drawList.CommandCount, hotAlloc);
            LastStats = new FrameStats(_drawList.CommandCount, clicks, hotAlloc, reconciled || layoutNeeded)
            {
                NodesVisited = recordStats.NodesVisited,
                NodesCulled = recordStats.NodesCulled,
                DrawNodeCount = recordStats.DrawnNodeCount,
                CulledNodeCount = recordStats.CulledNodeCount,
                BlurCandidateCount = recordStats.BlurCandidateCount,
                BlurGroupCount = recordStats.BlurGroupCount,
                BlurSuppressedByScrollCount = recordStats.BlurSuppressedByScrollCount,
                BlurHoldCandidateCount = recordStats.BlurHoldCandidateCount,
                EdgeFadeGroupCount = recordStats.EdgeFadeGroupCount,
                SpansReused = recordStats.SpansReused,
                SpansRebased = recordStats.SpansRebased,
                SpansReRecorded = recordStats.SpansReRecorded,
                SpanBytesCopied = recordStats.SpanBytesCopied,
                SpanReuseDisabledReasons = recordStats.SpanReuseDisabledReasons,
                MeasureCount = _layout.DiagMeasure,
                ArrangeCount = _layout.DiagArrange,
                TextShapeMisses = _layout.DiagTextMiss,
                RootRelayoutEscapes = _invalidator.EscapesThisFrame,
                Fps = _fps,
                FrameMs = _frameMs,
                ComponentsRendered = componentsRendered,
                FlushMs = ToMs(tFlush - frameStart),   // incl. flip/FLIP-capture + reactive flush + reconcile
                ReactiveFlushMs = reactiveFlushMs,
                VirtualRealizeMs = virtualRealizeMs,
                LayoutMs = ToMs(tLayout - tFlush),
                AnimMs = ToMs(tAnim - tLayout),         // phase-7 ticks + projections
                RecordMs = ToMs(tRecord - tAnim),       // image pump + SceneRecorder (+ text shaping) + dyntext
                ImagePumpMs = ToMs(tImagePump - tAnim),            // of which: phase-7.5 decode apply/evict
                RealizeCatchupMs = ToMs(tRealizeCatchup - tImagePump), // of which: phase-7.6 re-realize + scoped relayout
                SubmitMs = ToMs(tSubmit - tRecord),     // command build + GPU submit + present (total; ~0 on a skipped frame)
                FenceWaitMs = skipSubmit ? 0.0 : _device.LastFenceWaitMs,  // of which: UI-thread stall on the frame fence + latency waitable
                PresentMs = ToMs(tSubmit - tSubmitDone),// of which: the Present() call (0 on a skipped frame)
                Presented = !skipSubmit,
                LyricsScrollMode = probeLyMode,
                LyricsUserScrollActive = probeLyUser,
                LyricsContentDirtyAtRecord = probeLyDirty,
                MainScrollMode = probeMainMode,
                MainContentDirtyAtRecord = probeMainDirty,
            };
            PublishFrameStats(LastStats);
            // Hitch attribution into the scroll trace (>12ms frames only): the per-phase split lands in the SAME CSV as
            // the offset writes, so a lurch is directly attributable (GPU fence stall vs realize vs record vs shaping).
            if (FluentGpu.Foundation.ScrollTrace.On && dtMs > 12f)
            {
                float rawDt = _frameTime is StopwatchFrameTimeSource sfts ? sfts.LastRawDeltaMs : dtMs;
                FluentGpu.Foundation.ScrollTrace.FrameTiming(
                    (float)LastStats.FlushMs, (float)LastStats.LayoutMs, (float)LastStats.AnimMs,
                    (float)LastStats.RecordMs, (float)LastStats.SubmitMs, (float)LastStats.FenceWaitMs,
                    (float)LastStats.PresentMs, LastStats.MeasureCount, LastStats.TextShapeMisses, rawDt);
                // Gap discriminator (note 113): most traced scroll hitches have SLACK — raw dt far exceeding the frame's
                // measured work — meaning the loop wasn't running. GC-collection deltas vs the wait the loop last asked
                // for split that into "GC pause" / "wake-model slept" / "externally preempted".
                float slack = rawDt - (float)(LastStats.FlushMs + LastStats.LayoutMs + LastStats.AnimMs + LastStats.RecordMs + LastStats.SubmitMs);
                if (slack > 12f)
                {
                    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                    FluentGpu.Foundation.ScrollTrace.Note(113, slack, g0 - _traceGc0, ((g1 - _traceGc1) << 8) | (g2 - _traceGc2), _lastWaitMs);
                    _traceGc0 = g0; _traceGc1 = g1; _traceGc2 = g2;
                }
            }
            if (_frameClockSig.HasSubscribers) _frameClockSig.Value = ++_frameClock;   // drive per-frame pollers (overlay close) only when watched
            if (s_allocDiag) Probe(SegPublish, db, dt0);   // alloc-05: frame-stat box + frameclock long-box were untracked
            return LastStats;
        }
        finally
        {
            _frameNeeded = false;
            if (_frameAfterPaint) { _frameNeeded = true; _frameAfterPaint = false; }
            _inPaint = false;
            if (s_allocDiag)
            {
                _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
                _diagFrames++;
                DiagMaybeReport();
            }
        }
    }

    // ── E4 windowed out-of-bounds popups ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Window-DIP point → the containing monitor's work area, translated back into window-DIP space (the
    /// container rect the FlyoutPositioner clamps windowed popups against — WinUI FlyoutBase_Partial.cpp:3382-3392
    /// <c>useMonitorBounds</c>). The host owns the scale + client-origin conversion.</summary>
    private RectF GetWorkAreaDip(Point2 dipPoint)
    {
        float s = _window.Scale <= 0f ? 1f : _window.Scale;
        var origin = _window.ClientOriginPx;
        var work = _app.GetWorkArea(new Point2(origin.X + dipPoint.X * s, origin.Y + dipPoint.Y * s));
        return new RectF((work.X - origin.X) / s, (work.Y - origin.Y) / s, work.W / s, work.H / s);
    }

    /// <summary>Lease a popup window for an overlay subtree. Returns -1 when windowed popups are unavailable
    /// (<see cref="PopupWindowsEnabled"/> false, or the PAL declined) — callers fall back to constrained placement.</summary>
    private int OpenPopupWindow(NodeHandle subtreeRoot, PopupWindowMaterial material)
    {
        if (!PopupWindowsEnabled || subtreeRoot.IsNull) return -1;
        var palWindow = _app.CreatePopupWindow(new PopupWindowDesc(_window.Handle, default, material, Tok.Theme == ThemeKind.Dark));
        if (palWindow is null) return -1;
        bool acrylic = material == PopupWindowMaterial.TransientAcrylic;
        // Flat tint over the host-backdrop (blurred desktop): the dark MenuFlyout fallback color at ~0.5 so the desktop
        // reads through as a frosted grey (WinUI DesktopAcrylicBackdrop look). Tunable.
        ColorF tint = acrylic ? Tok.AcrylicFlyout.Fallback with { A = 0.5f } : default;
        // Round the composition acrylic to the flyout corner radius (WinUI OverlayCornerRadius = 8 DIP) so it matches
        // the engine-drawn rounded plate/border in the swapchain content.
        float cornerPx = acrylic ? 8f * (_window.Scale <= 0f ? 1f : _window.Scale) : 0f;
        var slot = new PopupWindowSlot(++_popupTokenSeq, palWindow, subtreeRoot, material)
        {
            Swapchain = _device.CreateSwapchain(new SwapchainDesc(palWindow.Handle, new Size2(1, 1),
                Composited: true, DesktopAcrylic: acrylic, AcrylicTint: tint, CornerRadiusPx: cornerPx)),
        };
        _popupWindows.Add(slot);
        WakeFrame();
        return slot.Token;
    }

    /// <summary>Place a leased popup window: bounds arrive in main-window DIP (the overlay's placement space); the
    /// host converts to physical virtual-screen px (client origin + scale), resizes the popup swapchain, and shows the
    /// window (never activating — focus stays here).</summary>
    private void SetPopupWindowBounds(int token, RectF dipBounds, bool opensUp, float closedRatio)
    {
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            var slot = _popupWindows[i];
            if (slot.Token != token) continue;
            slot.BoundsDip = dipBounds;
            // Inflate the popup WINDOW by the WinUI medium-popup shadow insets (L10 T2 R10 B18 DIP) so the composition drop
            // shadow has margin to render into; the menu plate sits inset at (insL,insT) within the window. RecordPopupWindows
            // records the subtree at WindowBoundsDip's top-left, so the content lands at the inset offset, and the per-frame
            // re-glue + the window px both derive from WindowBoundsDip.
            const float insL = 10f, insT = 2f, insR = 10f, insB = 18f;
            slot.WindowBoundsDip = new RectF(dipBounds.X - insL, dipBounds.Y - insT, dipBounds.W + insL + insR, dipBounds.H + insT + insB);
            float s = _window.Scale <= 0f ? 1f : _window.Scale;
            var origin = _window.ClientOriginPx;
            var wb = slot.WindowBoundsDip;
            var px = new RectF(origin.X + wb.X * s, origin.Y + wb.Y * s, wb.W * s, wb.H * s);
            slot.Window.SetBoundsPx(in px);
            float wpx = MathF.Max(1f, px.W), hpx = MathF.Max(1f, px.H);
            slot.Swapchain?.Resize(new Size2(wpx, hpx));
            // Content rect = the menu plate inset by the shadow margins (window px): the acrylic rounds to it + the shadow
            // is masked to it; the engine draws the plate/border/items there too (recorded at the inset origin).
            var contentPx = new RectF(insL * s, insT * s, dipBounds.W * s, dipBounds.H * s);
            slot.Swapchain?.ConfigurePopupChrome(new PopupChromeMetrics(
                contentPx, opensUp, closedRatio > 0f ? closedRatio : 0.5f, 8f * s, 1f * s));
            bool firstShow = !slot.Window.IsShown;
            if (firstShow) slot.Window.Show();
            if (firstShow) slot.Swapchain?.AnimatePopupOpen();
            WakeFrame();
            return;
        }
    }

    /// <summary>Begin the desktop-acrylic CLOSE fade on a popup window's composition chrome (acrylic + shadow). The engine
    /// fades the content swapchain over the same 83ms; the window itself is disposed at finalize (<see cref="ClosePopupWindow"/>),
    /// by which time the fade has settled — so the acrylic fades out instead of vanishing.</summary>
    private void AnimatePopupCloseWindow(int token)
    {
        for (int i = 0; i < _popupWindows.Count; i++)
            if (_popupWindows[i].Token == token) { _popupWindows[i].Swapchain?.AnimatePopupClose(); WakeFrame(); return; }
    }

    private void ClosePopupWindow(int token)
    {
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            var slot = _popupWindows[i];
            if (slot.Token != token) continue;
            slot.Window.Hide();
            slot.Swapchain?.Dispose();
            slot.Window.Dispose();
            _popupWindows.RemoveAt(i);
            WakeFrame();
            return;
        }
    }

    /// <summary>Phase 8b: re-record each popup window's subtree into its own DrawList (recorder root-override,
    /// re-origined to the popup's placed top-left) and present its swapchain.</summary>
    private void RecordPopupWindows(in FocusVisualStyle focus, in TextEditStyle textEdit)
    {
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            var slot = _popupWindows[i];
            if (slot.Root.IsNull || !_scene.IsLive(slot.Root)) continue;
            var origin = slot.WindowBoundsDip.IsEmpty ? slot.BoundsDip : slot.WindowBoundsDip;
            // Re-glue the popup window to the owner's CURRENT screen position. It's a separate top-level HWND in
            // virtual-screen px; the overlay only re-places it when the anchor's window-DIP moves, so a pure window MOVE
            // (client origin shifts, anchor-DIP unchanged) — or a resize from the top/left edge — strands it at its old
            // screen position. Re-derive screen px from the live client origin + the placed DIP each frame (cheap; only
            // moves the window when it actually drifted >0.5px).
            if (slot.Swapchain is not null && !origin.IsEmpty)
            {
                float os = _window.Scale <= 0f ? 1f : _window.Scale;
                var co = _window.ClientOriginPx;
                float wx = co.X + origin.X * os, wy = co.Y + origin.Y * os;
                var cur = slot.Window.BoundsPx;
                if (MathF.Abs(wx - cur.X) > 0.5f || MathF.Abs(wy - cur.Y) > 0.5f)
                    slot.Window.SetBoundsPx(new RectF(wx, wy, cur.W, cur.H));
            }
            SceneRecorder.RecordSubtree(_scene, slot.DrawList, _images, in focus, Tok.ScrollThumb, Tok.AcrylicFlyout.Fallback, in textEdit,
                slot.Root, new Point2(origin.X, origin.Y));
            if (slot.Swapchain is { } sc)
            {
                try
                {
                    _device.SubmitDrawList(slot.DrawList.Bytes, slot.DrawList.SortKeys,
                        new FrameInfo(sc.SizePx, _window.Scale, ColorF.Transparent), sc);
                    sc.Present();
                }
                catch (Exception ex)
                {
                    // A windowed popup failed to render (e.g. a swapchain fault on a zombie HWND). Tear THIS popup down
                    // and disable the windowed path so menus fall back to in-window engine acrylic — never crash-loop the
                    // frame. (A true device-loss is still fatal at the main present; that's a separate recovery gap.)
                    Console.Error.WriteLine($"[popup] windowed render failed, falling back to in-window: {ex.Message}");
                    Diag.Sink?.Invoke($"[popup] windowed render failed, falling back to in-window: {ex}");
                    PopupWindowsEnabled = false;
                    slot.Window.Hide();
                    slot.Swapchain?.Dispose();
                    slot.Window.Dispose();
                    slot.Swapchain = null;
                    _popupWindows.RemoveAt(i);
                    i--;
                }
            }
        }
    }

    private void PublishViewport(Size2 dip)
    {
        if (dip.Width == _lastViewportDip.Width && dip.Height == _lastViewportDip.Height) return;
        _lastViewportDip = dip;
        _viewportSig.Value = dip;   // schedules consumers (NavigationView display modes) granularly
    }

    private void PublishFrameStats(FrameStats stats)
    {
        if (_frameStatsSig.HasSubscribers) _frameStatsSig.Value = stats;   // box only when a consumer (HUD) reads it
    }

    /// <summary>The node's presented rect in its PARENT's frame: layout origin + its own in-flight LocalTransform.
    /// Because <see cref="SceneStore.AbsoluteRect"/> is a pure translation sum up the chain, this is the absolute rect
    /// minus every ancestor contribution — computable with no ancestor walk.</summary>
    private RectF RelRect(NodeHandle n)
    {
        ref readonly RectF b = ref _scene.Bounds(n);
        ref readonly NodePaint p = ref _scene.Paint(n);
        return new RectF(b.X + p.LocalTransform.Dx, b.Y + p.LocalTransform.Dy, b.W, b.H);
    }

    /// <summary>The node's presented rect relative to an arbitrary FRAME's origin (FLIP relativeTarget). Uses the
    /// absolute translation sum, so the relative rect is UNCHANGED when node + frame move together (coherence). For
    /// frame == the node's parent this equals <see cref="RelRect"/>.</summary>
    private RectF RelRectIn(NodeHandle n, NodeHandle frame)
    {
        RectF a = _scene.AbsoluteRect(n), f = _scene.AbsoluteRect(frame);
        return new RectF(a.X - f.X, a.Y - f.Y, a.W, a.H);
    }

    // FLIP "First" capture — every BoundsAnimated node's presented PARENT-RELATIVE rect, snapshotted BEFORE this commit.
    private void CaptureProjections()
    {
        var nodes = _scene.BoundsAnimatedNodes;
        int w = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            NodeHandle n = nodes[i];
            if (!_scene.IsLive(n) || (_scene.Flags(n) & NodeFlags.BoundsAnimated) == 0) continue;
            nodes[w++] = n;
            // FLIP relativeTarget: capture relative to the resolved shared-layout anchor (if any) instead of the parent,
            // so the node rides the anchor's motion coherently (its anchor-relative rect is unchanged ⇒ no re-FLIP).
            NodeHandle anchor = _reconciler.ResolveRelativeTarget(n);
            _projectBefore[n] = anchor.IsNull
                ? new ProjCapture(RelRect(n), _scene.Parent(n))
                : new ProjCapture(RelRectIn(n, anchor), anchor);
        }
        if (w < nodes.Count) nodes.RemoveRange(w, nodes.Count - w);
    }

    // FG_MOTION_DIAG per-node line (one word of OUTCOME + the captured/live rects). Static → zero capture, and only ever
    // reached under the s_motionDiag guard, so the off-path stays allocation-free.
    private static void LogMotionNode(uint idx, string outcome, in RectF f, in RectF t)
        => System.Console.Error.WriteLine(
            $"[motion-diag]   node={idx} {outcome} from=({f.X:0.0},{f.Y:0.0},{f.W:0.0},{f.H:0.0}) to=({t.X:0.0},{t.Y:0.0},{t.W:0.0},{t.H:0.0})");

    private void ApplyProjections()
    {
        // Deadbands: below these the commit didn't move/resize the node WITHIN ITS PARENT, so it must ride any
        // ancestor reflow rigidly. The skip is required for correctness, not a fast path — AnimateBounds on a
        // zero delta RESTARTS a full-duration tween from the current value (and seeds throwaway spring tracks),
        // which is exactly the "knob lags its own track during a reveal" desync. In-flight tracks keep running.
        const float PosEps = 0.05f;
        const float SizeEps = 0.5f;   // matches RevealSize's no-change deadband (AnimEngine)
        // Two DISTINCT axes, not one "reduced" flag:
        //  • Suppression (an interactive/edge/maximize resize owns geometry) does NOT merely shorten the tween — it must
        //    NOT START a projection AND must cancel any in-flight structural track, snapping the node onto the geometry
        //    just laid out so bounds track the pointer with no stale translate/overlap.
        //  • ReducedMotion is a separate ACCESSIBILITY preference (gate-covered): it keeps its 1ms-tween snap and still
        //    lets opacity/etc. animate — behaviour left exactly as before.
        bool suppressed = Motion.LayoutTransitionsSuppressed;
        bool reduced = Motion.ReducedMotion;

        // Discover changed containers that explicitly own the visual projection for their subtree. A shell/card width
        // commit commonly changes dozens of descendant card/shelf bounds; allowing every descendant's authored
        // CardRefit/CardResize recipe to start here recreates per-frame Relayout/Reflow under the projected root. Keep
        // the semantic final layout, but let the container be the sole geometry animator for this commit.
        _projectionSuppressionRoots.Clear();
        if (!suppressed)
        {
            foreach (var kv in _projectBefore)
            {
                NodeHandle n = kv.Key;
                if (!_scene.IsLive(n) || (_scene.Flags(n) & NodeFlags.BoundsAnimated) == 0) continue;
                if (!_anim.TryGetTransition(n, out var spec) || !spec.SuppressDescendantTransitions) continue;
                if (TryProjectionRects(n, kv.Value, PosEps, SizeEps, out _, out _))
                    _projectionSuppressionRoots.Add(n);
            }
        }

        foreach (var kv in _projectBefore)
        {
            var n = kv.Key;
            // Diag-only best-effort from/to for the pre-TryProjectionRects branches (the real parent-relative pair is only
            // computed by TryProjectionRects; here `to` is the live parent-relative rect, absent for a non-live node).
            RectF fLog = default, tLog = default;
            if (s_motionDiag) { fLog = kv.Value.Rel; tLog = _scene.IsLive(n) ? RelRect(n) : default; }
            if (n == _dispatcher.Drag.ActiveNode) { if (s_motionDiag) LogMotionNode(n.Raw.Index, "drag-skip", fLog, tLog); continue; }   // E5: the pointer owns the dragged node's transform
            if (!_scene.IsLive(n) || (_scene.Flags(n) & NodeFlags.BoundsAnimated) == 0) { if (s_motionDiag) LogMotionNode(n.Raw.Index, "dead-node", fLog, tLog); continue; }
            if (suppressed) { if (s_motionDiag) LogMotionNode(n.Raw.Index, "suppressed-snap", fLog, tLog); _anim.SnapStructuralToLayout(n); continue; }   // skip-start + cancel-in-flight → snap to laid-out bounds
            if (IsBelowProjectionSuppressionRoot(n))
            {
                if (s_motionDiag) LogMotionNode(n.Raw.Index, "below-root-snap", fLog, tLog);
                _anim.SnapStructuralToLayout(n);
                continue;
            }
            if (!TryProjectionRects(n, kv.Value, PosEps, SizeEps, out RectF from, out RectF to))
            {
                if (s_motionDiag)
                {
                    // TryProjectionRects returns false for TWO distinct reasons: (a) the reference frame changed
                    // (frameNow != captured.Parent — a reparent OR a RelativeTo anchor that now resolves elsewhere, so the
                    // relative rects are incomparable and it bails BEFORE the delta check), or (b) a genuine sub-deadband
                    // move. Mirror its exact frame comparison to label each accurately — conflating (a) as "deadband" made a
                    // 240px reference-frame delta read as a no-op. Reads scene state only; no behaviour change.
                    NodeHandle anchorNow = _reconciler.ResolveRelativeTarget(n);
                    NodeHandle frameNow = anchorNow.IsNull ? _scene.Parent(n) : anchorNow;
                    LogMotionNode(n.Raw.Index, frameNow != kv.Value.Parent ? "frame-mismatch" : "deadband", fLog, tLog);
                }
                continue;
            }
            if (!_anim.TryGetTransition(n, out var spec)) { if (s_motionDiag) LogMotionNode(n.Raw.Index, "no-transition", from, to); continue; }
            if (reduced) spec = spec with { Dynamics = TransitionDynamics.Tween(1f, Easing.Linear) };
            if (s_motionDiag) LogMotionNode(n.Raw.Index, "animate", from, to);
            // AnimateBounds consumes only deltas, so parent-relative rects feed it directly; for a purely local
            // move this is bit-identical to the old absolute pair (the ancestor sum cancels).
            _anim.AnimateBounds(n, from, to, spec);
        }
        _projectionSuppressionRoots.Clear();
        _projectBefore.Clear();
    }

    private bool TryProjectionRects(NodeHandle n, in ProjCapture captured, float posEps, float sizeEps,
                                    out RectF from, out RectF to)
    {
        from = captured.Rel;
        NodeHandle anchor = _reconciler.ResolveRelativeTarget(n);
        NodeHandle frameNow = anchor.IsNull ? _scene.Parent(n) : anchor;
        if (frameNow != captured.Parent)
        {
            to = default;
            return false;   // reparented / anchor changed: the relative frames are incomparable
        }
        to = anchor.IsNull ? RelRect(n) : RelRectIn(n, anchor);
        return MathF.Abs(from.X - to.X) >= posEps || MathF.Abs(from.Y - to.Y) >= posEps
            || MathF.Abs(from.W - to.W) >= sizeEps || MathF.Abs(from.H - to.H) >= sizeEps;
    }

    private bool IsBelowProjectionSuppressionRoot(NodeHandle node)
    {
        if (_projectionSuppressionRoots.Count == 0) return false;
        for (NodeHandle p = _scene.Parent(node); !p.IsNull && _scene.IsLive(p); p = _scene.Parent(p))
            for (int i = 0; i < _projectionSuppressionRoots.Count; i++)
                if (p == _projectionSuppressionRoots[i]) return true;
        return false;
    }

    private void RunIncrementalLayout()
    {
        var roots = _anim.IncrementalRoots;
        if (roots.Count == 0) return;
        for (int i = 0; i < roots.Count; i++)
        {
            var r = roots[i];
            if (!_scene.IsLive(r)) continue;
            ref NodePaint p = ref _scene.Paint(r);
            ref LayoutInput li = ref _scene.Layout(r);
            if (!float.IsNaN(p.PresentedW)) li.Width = p.PresentedW;
            if (!float.IsNaN(p.PresentedH)) li.Height = p.PresentedH;
            _layout.RunSubtree(r);
        }
        roots.Clear();
    }

    /// <summary>SizeMode.Reflow (phase 7): a reflow track just wrote its interpolated size into LayoutInput and dirtied
    /// the PARENT — re-solve those scopes through the standard boundary firewall so siblings reflow at the eased size
    /// before record, then refresh each Trailing-anchored node's child-shift from the fresh bounds (the content's end
    /// edge rides the animated edge). Runs only on frames where a reflow track wrote — zero work otherwise.</summary>
    private void RunReflowLayout(Size2 layoutSize)
    {
        var roots = _anim.ReflowRoots;
        if (!_anim.ConsumeReflowWrites()) { roots.Clear(); return; }
        if (_scene.AnyLayoutDirty)
        {
            _invalidator.RunDirty(layoutSize);
            _scene.ClearLayoutDirty();
        }
        for (int i = 0; i < roots.Count; i++)
        {
            var r = roots[i];
            if (!_scene.IsLive(r)) continue;
            if (!_anim.TryGetTransition(r, out var spec) || spec.Anchor != SizeAnchor.Trailing) continue;
            float extent = 0f;
            for (var c = _scene.FirstChild(r); !c.IsNull; c = _scene.NextSibling(c))
            {
                ref RectF cb = ref _scene.Bounds(c);
                extent = MathF.Max(extent, cb.Y + cb.H);
            }
            ref NodePaint p = ref _scene.Paint(r);
            p.ChildShiftY = extent <= 0f ? 0f : MathF.Min(0f, _scene.Bounds(r).H - extent);
            _scene.Mark(r, NodeFlags.PaintDirty);
        }
        roots.Clear();
    }


    /// <summary>Settle timeout: a wedged exit track (one that never reaches its end) would keep its orphan LIVE,
    /// pinning OrphanCount &gt; 0 and so keeping the wake loop running forever. Reclaim every settled orphan (no tracks)
    /// as before, and FORCE-reclaim any orphan older than this even if it still has tracks. Healthy exit animations
    /// settle in &lt;1s, so the backstop never fires in a well-behaved run.</summary>
    private const long OrphanSettleTimeoutMs = 2000;
    private void ReclaimSettledOrphans()
    {
        long nowTicks = _scene.OrphanCount > 0 ? Stopwatch.GetTimestamp() : 0;
        for (int i = _scene.OrphanCount - 1; i >= 0;)
        {
            // Reclaiming an exiting parent may cascade-reclaim its earlier-indexed exiting children. Rebase the cursor
            // after every removal so a shrunken orphan list can never leave i pointing past its new end.
            if (i >= _scene.OrphanCount) { i = _scene.OrphanCount - 1; continue; }
            var o = _scene.OrphanAt(i, out _, out _);
            if (!_anim.HasTracks(o))
            {
                _scene.ReclaimOrphan(o);
                i = Math.Min(i - 1, _scene.OrphanCount - 1);
                continue;
            }
            double ageMs = (nowTicks - _scene.OrphanEnqueuedTicks(i)) * 1000.0 / Stopwatch.Frequency;
            if (ageMs >= OrphanSettleTimeoutMs)
            {
                Diag.Event("scene", $"orphan-backstop force-reclaim age={ageMs:0}ms (wedged exit track)");
                _scene.ReclaimOrphan(o);
                i = Math.Min(i - 1, _scene.OrphanCount - 1);
                continue;
            }
            i--;
        }
    }

    /// <summary>Slot-free fan-out (wired to <see cref="SceneStore.OnFreeIndex"/>): drop every INDEX-keyed per-node row
    /// the engine subsystems hold so a freed slot leaves nothing for the next node reusing that index to inherit. The
    /// gen-checked-handle side-tables (in-flight anim tracks, the interaction/scroll armed sets) self-prune at their next
    /// tick and are deliberately untouched here.</summary>
    private void OnSceneSlotFreed(int index)
    {
        _anim.ClearForIndex(index);
        _scrollAnim.ClearForIndex(index);
    }

    private void UpdateFrameTiming(long frameStart)
    {
        long now = Stopwatch.GetTimestamp();
        _frameMs = (now - frameStart) * 1000.0 / Stopwatch.Frequency;
        _presentTimes[_presentTimeNext] = now;
        _presentTimeNext = (_presentTimeNext + 1) % _presentTimes.Length;
        if (_presentTimeCount < _presentTimes.Length) _presentTimeCount++;
        if (_presentTimeCount < 2) return;

        int newest = (_presentTimeNext - 1 + _presentTimes.Length) % _presentTimes.Length;
        long newestTime = _presentTimes[newest];
        long oldestTime = newestTime;
        int intervals = 0;
        long windowTicks = (long)(FpsWindowSeconds * Stopwatch.Frequency);
        for (int i = 1; i < _presentTimeCount; i++)
        {
            int index = (newest - i + _presentTimes.Length) % _presentTimes.Length;
            long candidate = _presentTimes[index];
            if (newestTime - candidate > windowTicks && intervals > 0) break;
            oldestTime = candidate;
            intervals = i;
        }

        double elapsed = (newestTime - oldestTime) / (double)Stopwatch.Frequency;
        if (elapsed > 0.0001) _fps = intervals / elapsed;
    }

    // Sentinel quant for the "--" (no data yet) display — distinct from any real value so it interns "--" exactly once.
    private const long DynTextNoData = long.MinValue + 1;
    // Cached resolve delegate (one alloc, not new-per-frame): returns the per-kind cached id with NO Intern.
    private Func<DynamicTextKind, StringId>? _dynTextResolve;
    // Last-seen scene dynamic-text registration epoch: a node (un)mounted/swapped since the last rewrite has no
    // resolved id yet, so the per-node pass must run even when no displayed value moved this frame.
    private int _dynTextEpochSeen = -1;

    /// <summary>Refresh the retained HUD text slots (FPS / draw counts / frame ms) WITHOUT re-rendering or relayout —
    /// intern-on-change: each kind is quantized to its DISPLAY granularity and re-stringified+interned only when that
    /// quantized value actually changes (a steady or same-rounding readout costs nothing and burns no ids). When no
    /// kind changed this frame the per-node UpdateDynamicText scan is skipped entirely (the scene already holds the
    /// right ids).</summary>
    private void UpdateDynamicDiagnosticsText()
    {
        if (!_scene.HasDynamicText) return;
        bool registrationChanged = _scene.DynamicTextEpoch != _dynTextEpochSeen;
        _dynTextEpochSeen = _scene.DynamicTextEpoch;
        bool anyChanged = false;
        // Only the kinds the HUD can show have a quant; recompute each and re-intern on change. All read LastStats /
        // _fps / _frameMs at the SAME point the prior code's resolve lambda did (the previous frame's stats — this runs
        // before LastStats is reassigned), so the displayed values are unchanged frame-for-frame.
        anyChanged |= RefreshDynText(DynamicTextKind.FrameFps);
        anyChanged |= RefreshDynText(DynamicTextKind.FrameCommandCount);
        anyChanged |= RefreshDynText(DynamicTextKind.FrameDrawCount);
        anyChanged |= RefreshDynText(DynamicTextKind.FrameCullCount);
        anyChanged |= RefreshDynText(DynamicTextKind.FrameMs);
        if (!anyChanged && !registrationChanged) return;   // nothing moved a display unit and no node (un)mounted → no per-node rewrite, no id churn

        _scene.UpdateDynamicText(_dynTextResolve ??= kind => _dynTextId[(int)kind]);
    }

    /// <summary>Quantize one HUD kind to its display unit; on a change, stringify+intern the new value, hold a host ref
    /// on the new id, drop the host ref on the old, and cache both. Returns true iff the cached id changed.</summary>
    private bool RefreshDynText(DynamicTextKind kind)
    {
        int k = (int)kind;
        long quant = kind switch
        {
            DynamicTextKind.FrameFps => _fps <= 0.0 ? DynTextNoData : (long)Math.Round(_fps, MidpointRounding.AwayFromZero),
            DynamicTextKind.FrameCommandCount => LastStats.DrawCommandCount,
            DynamicTextKind.FrameDrawCount => LastStats.DrawNodeCount,
            DynamicTextKind.FrameCullCount => LastStats.CulledNodeCount,
            DynamicTextKind.FrameMs => _frameMs <= 0.0 ? DynTextNoData : (long)Math.Round(_frameMs * 10.0, MidpointRounding.AwayFromZero),
            _ => DynTextNoData,
        };
        if (quant == _dynTextQuant[k]) return false;   // same display unit → reuse the cached id, no ToString/Intern

        string s = kind switch
        {
            DynamicTextKind.FrameFps => quant == DynTextNoData ? "--" : _fps.ToString("0", CultureInfo.InvariantCulture),
            DynamicTextKind.FrameMs => quant == DynTextNoData ? "--" : _frameMs.ToString("0.0", CultureInfo.InvariantCulture),
            _ => quant.ToString(CultureInfo.InvariantCulture),
        };
        StringId next = _strings.Intern(s);
        _strings.AddRef(next);                 // host-held ref: the cached id stays alive across frames
        _strings.Release(_dynTextId[k]);       // drop the prior cached value's host ref (no-op for id 0 / first frame)
        _dynTextId[k] = next;
        _dynTextQuant[k] = quant;
        return true;
    }

    private void DrainLayoutEffects()
        => DrainPendingEffectContexts(_pendingLayoutEffectContexts, layout: true);

    private void DrainPassiveEffects()
        => DrainPendingEffectContexts(_pendingPassiveEffectContexts, layout: false);

    private void RegisterPendingEffectContext(RenderContext ctx, bool layout)
        => (layout ? _pendingLayoutEffectContexts : _pendingPassiveEffectContexts).Add(ctx);

    private static void DrainPendingEffectContexts(List<RenderContext> contexts, bool layout)
    {
        for (int i = 0; i < contexts.Count; i++)
            Drain(layout ? contexts[i].PendingLayoutEffects : contexts[i].PendingEffects);
        contexts.Clear();
    }

    private static void Drain(List<Action> q)
    {
        if (q.Count == 0) return;
        for (int i = 0; i < q.Count; i++) q[i]();
        q.Clear();
    }

    private bool _dumped;
    private void DumpSceneOnce(Size2 layoutSize)
    {
        string? dumpMode = Environment.GetEnvironmentVariable("FG_DUMP");
        if (string.IsNullOrWhiteSpace(dumpMode) || !Diag.EnvFlag("FG_DUMP")) return;
        bool all = dumpMode.Equals("all", StringComparison.OrdinalIgnoreCase);
        if (_dumped && !all) return;
        _dumped = true;
        Console.Error.WriteLine($"=== SCENE DUMP (post-layout, window {layoutSize.Width:0}x{layoutSize.Height:0} DIP) ===");
        DumpNode(_scene.Root, 0);
        Console.Error.WriteLine("=== END SCENE DUMP ===");
    }

    private void DumpNode(FluentGpu.Foundation.NodeHandle n, int depth)
    {
        if (n.IsNull) return;
        ref RectF b = ref _scene.Bounds(n);
        ref NodePaint p = ref _scene.Paint(n);
        NodeFlags f = _scene.Flags(n);

        string text = "";
        if (p.VisualKind == VisualKind.Text)
        {
            string s = _strings.Resolve(p.Text) ?? "";
            if (s.Length > 24) s = s.Substring(0, 24) + "…";
            text = $" \"{s}\"";
        }

        string vis = (f & NodeFlags.Visible) != 0 ? "" : " HIDDEN";
        string clip = (f & NodeFlags.ClipsToBounds) != 0 ? " clip" : "";
        string scroll = (f & NodeFlags.Scrollable) != 0 ? " scroll" : "";
        Console.Error.WriteLine(
            $"{new string(' ', depth * 2)}{p.VisualKind,-5} b=({b.X,6:0.#},{b.Y,6:0.#} {b.W,6:0.#}x{b.H,5:0.#}) " +
            $"op={p.Opacity:0.00} fillA={p.Fill.A:0.00} bw={p.BorderWidth:0.#}{vis}{clip}{scroll}{text}");

        for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
            DumpNode(c, depth + 1);
    }

    /// <summary>True during a composited modal edge-drag: HWND size advances but GPU resize + relayout wait for mouse-up.</summary>
    private bool DeferModalResize(bool keepAlive)
        => keepAlive && _window.InModalLoop && _window.Composited;

    /// <summary>Layout/submit viewport in DIP while a modal resize is deferred — keep the last presented size until
    /// WM_EXITSIZEMOVE.</summary>
    private Size2 LayoutSizeForFrame(bool keepAlive)
    {
        if (DeferModalResize(keepAlive))
        {
            float scale = _lastScale <= 0f ? 1f : _lastScale;
            return new Size2(_lastSize.Width / scale, _lastSize.Height / scale);
        }
        return ClientSizeDip();
    }

    private Size2 FrameSizePx(bool keepAlive) => DeferModalResize(keepAlive) ? _lastSize : _window.ClientSizePx;

    /// <summary>Resize the swapchain to match the window's client size; force a full re-layout on change.
    /// Returns true if the client size changed this frame (so the caller can SNAP layout — a window resize must not
    /// FLIP-animate content; the pre-resize rects are stale and projecting them shifts the content + reveals the backdrop).</summary>
    private bool EnsureSize(bool keepAlive = false)
    {
        // Scale participates too: a per-monitor DPI change (WM_DPICHANGED) re-scales the window — usually the px
        // size changes with the suggested rect, but even when it doesn't, the DIP viewport (px/scale) did, so the
        // tree must re-lay-out (glyph re-rasterization keys on the per-frame FrameInfo scale by itself).
        var s = _window.ClientSizePx;
        float scale = _window.Scale;
        if (s.Width == _lastSize.Width && s.Height == _lastSize.Height && scale == _lastScale) return false;
        if (DeferModalResize(keepAlive)) return false;   // pending until WM_EXITSIZEMOVE (InModalLoop cleared before Paint)
        _lastSize = s;
        if (scale != _lastScale) _viewportScaleSig.Value = scale <= 0f ? 1f : scale;
        _lastScale = scale;
        // Step 2 (async resize rendezvous): D3D12Swapchain.Resize does a fenced WaitForGpu + releases the back buffers +
        // ResizeBuffers + recreates RTVs — all mutating ComPtrs the render thread reads in submit/present. Under async,
        // PARK the render loop (mutual exclusion) around the unchanged Resize. Default + force-sync take the else branch
        // (no render thread running concurrently — force-sync's UI is the only toucher between publishes), byte-identical.
        if (_renderThread is not null && _asyncActive)
        {
            _renderThread.Quiesce();
            try { _swapchain.Resize(s); }
            finally { _renderThread.Resume(); }
        }
        else _swapchain.Resize(s);
        _needFullLayout = true;
        return true;
    }

    private Size2 ClientSizeDip()
    {
        var s = _window.ClientSizePx;
        float scale = _window.Scale <= 0f ? 1f : _window.Scale;
        return new Size2(s.Width / scale, s.Height / scale);
    }

    public void Dispose()
    {
        _renderThread?.Dispose();   // Step 4: stop + join the fgpu-render thread before tearing down the device it submits to
        if (ReferenceEquals(HostDispatch.Current, _uiPoster))
            HostDispatch.Current = null;   // drop the process-static poster so a disposed host leaks no callback

        // Detach the activation-redirect subscription so a disposed host's IPlatformApp keeps no callback into it.
        if (_onActivationRedirected is { } onAct) { _app.ActivationRedirected -= onAct; _onActivationRedirected = null; }
        if (_onSystemColorsChanged is { } onSys) { _app.SystemColorsChanged -= onSys; _onSystemColorsChanged = null; }
        // Symmetric SIP teardown: drop the OccludedRect subscription so a disposed host's window TextInput keeps no
        // callback into it (the SIP reflow closure captures _dispatcher).
        if (_onOccludedRectChanged is { } onOcc) { _window.TextInput.OccludedRectChanged -= onOcc; _onOccludedRectChanged = null; }

        // mem-05: the ctor mirrored this host's app.OpenUri onto the shared InputHooks.Current.Default channel (static
        // HyperlinkButton factories reach the seam there). Release it so a disposed host's IPlatformApp graph is
        // collectable — but ONLY if this host's delegate is still installed (Target == our _app): a later-constructed
        // host may have overwritten it (last-wins), and clearing that would break the live host's hyperlinks.
        var def = InputHooks.Current.Default;
        if (def.OpenUri is { } cur && ReferenceEquals(cur.Target, _app)) def.OpenUri = null;

        // Same release for the OS-drop seam: the ctor mirrored this host's dispatcher onto the channel-default. Clear it
        // only when our dispatcher is still the installed target (a later host may have overwritten it, last-wins).
        if (def.ExternalDragEnter is { } de && ReferenceEquals(de.Target, _dispatcher))
        {
            def.ExternalDragEnter = null;
            def.ExternalDragOver = null;
            def.ExternalDragLeave = null;
            def.ExternalDrop = null;
            def.ExternalDropFiles = null;
        }
        // Live drag-state seam (GetDragState captures this host): clear when ours is still installed.
        if (def.GetDragState is { } gds && ReferenceEquals(gds.Target, this))
        {
            def.GetDragState = null;
            def.DragEpoch = null;
        }

        // Symmetry for the intern-on-change HUD cache: each cached id holds one host AddRef (RefreshDynText), so a
        // disposed HUD-bearing host must drop them or it pins ≤5 ids on the shared interner per disposed host.
        for (int i = 0; i < _dynTextId.Length; i++)
        {
            if (_dynTextId[i].IsEmpty) continue;
            _strings.Release(_dynTextId[i]);
            _dynTextId[i] = default;
        }

        for (int i = _popupWindows.Count - 1; i >= 0; i--)
        {
            _popupWindows[i].Swapchain?.Dispose();
            _popupWindows[i].Window.Dispose();
        }
        _popupWindows.Clear();
        // Tear down detached child windows (each disposes its own swapchain — which releases its video presenter — and its
        // window, but NOT the shared device). Do this before our own swapchain/device teardown.
        for (int i = _detachedHosts.Count - 1; i >= 0; i--) _detachedHosts[i].Dispose();
        _detachedHosts.Clear();
        _swapchain.Dispose();
        // A detached CHILD host shares the parent's device — it must NOT dispose it (the parent owns the device lifecycle).
        if (!_isDetachedChild) _device.Dispose();
        _window.Dispose();
    }
}
