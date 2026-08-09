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
    /// <summary>Translated-copy attempts the per-payload walk refused (acrylic layer / unknown opcode) ⇒ re-recorded.</summary>
    public int SpansRebaseRejected { get; init; }
    public int SpansReRecorded { get; init; }
    public int SpanBytesCopied { get; init; }
    public int NodesCulled { get; init; }
    public SpanReuseDisabledReason SpanReuseDisabledReasons { get; init; }
    // Per-frame layout-cost counters (FlexLayout diag; valid only when FG_LAYOUT_DIAG=1, else 0). MeasureCount/ArrangeCount
    // are total node visits across the frame's full + scoped + phase-7 reflow layout passes — MeasureCount counts REAL
    // measures (within-pass memo hits are excluded; FlexLayout.DiagMeasureMemoHits has those); TextShapeMisses is DirectWrite
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
    /// <summary>UI/frame-loop cadence over the trailing one-second window. Kept as <c>Fps</c> for HUD compatibility;
    /// this is not necessarily on-screen cadence when submit/present runs asynchronously or frames coalesce.</summary>
    public double Fps { get; init; }
    /// <summary>Actual successful main-swapchain presents per second over the trailing one-second window.</summary>
    public double PresentFps { get; init; }
    /// <summary>Monotonic count of successful main-swapchain presents in every submit mode.</summary>
    public ulong PresentedSequence { get; init; }
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
    // LayoutMs sub-split (hitch attribution): the "layout" bucket is the whole phase-6/6.5 span, and three passengers ride
    // it that are NOT the flex solve — layout effects, the connected-animation Tick65 (a per-tagged-node AbsoluteRect
    // parent-chain walk, every frame), and the enter/exit reflow seeding loops. A 13→200 ms layout tail on IDENTICAL
    // measure counts is one of those, not the solver, and could not be told apart until this split. The four sum to LayoutMs.
    public double LayoutSolveMs { get; init; }
    public double LayoutEffectsMs { get; init; }
    public double ConnectedTickMs { get; init; }
    public double ReflowSeedMs { get; init; }
    /// <summary>Of <see cref="RootRelayoutEscapes"/>: escapes proven size-stable and re-solved in place — full-window
    /// solves avoided. Equal to RootRelayoutEscapes ⇒ every escape was absorbed and no root solve ran.</summary>
    public int LocalRelayoutResolves { get; init; }
    public double AnimMs { get; init; }
    public double RecordMs { get; init; }
    public double SubmitMs { get; init; }
    // RecordMs sub-split (hitch attribution): the phase-7.5 image pump/tick and the phase-7.6 scroll re-realize
    // catch-up both run between tAnim and tRecord, so their cost was invisibly charged to "record" — a realize spike
    // on a fast fling read as SceneRecorder cost. RecordMs still covers the whole segment; these carve it up.
    public double ImagePumpMs { get; init; }
    public int ImageApplyCount { get; init; }
    public int ImageApplyBytes { get; init; }
    public double RealizeCatchupMs { get; init; }
    // Submit sub-split (diagnostics for the #1 hotspot — GPU fence/present pacing is charged to "submit" on the UI thread
    // until the render-thread seam lands). FenceWaitMs = wall-time BLOCKED on the frame fence + present-latency waitable
    // INSIDE SubmitDrawList; PresentMs = the Present() call. cmdBuild = SubmitMs − FenceWaitMs − PresentMs is the real CPU
    // command-build cost. Lets a probe attribute a 27 ms "submit" spike to the stall vs the build without an external profiler.
    public double FenceWaitMs { get; init; }
    public double PresentMs { get; init; }
    /// <summary>Most recent true on-GPU whole-frame raster time (timestamp queries; 0 when disabled).</summary>
    public double GpuRenderMs { get; init; }
    /// <summary>Scene-raster portion of <see cref="GpuRenderMs"/> (0 when timestamp queries are disabled).</summary>
    public double GpuSceneMs { get; init; }
    /// <summary>Rect/solid-fill portion of <see cref="GpuSceneMs"/> (0 when timestamp queries are disabled).</summary>
    public double GpuFillMs { get; init; }
    /// <summary>Drop-shadow portion of <see cref="GpuSceneMs"/>, split out of <see cref="GpuFillMs"/> (0 when disabled).</summary>
    public double GpuShadowMs { get; init; }
    /// <summary>Image-draw portion of <see cref="GpuSceneMs"/> (0 when timestamp queries are disabled).</summary>
    public double GpuImageMs { get; init; }
    /// <summary>Glyph/text portion of <see cref="GpuSceneMs"/> (0 when timestamp queries are disabled).</summary>
    public double GpuGlyphMs { get; init; }
    /// <summary>Layer/acrylic composite portion of <see cref="GpuSceneMs"/> (0 when timestamp queries are disabled).</summary>
    public double GpuCompositeMs { get; init; }
    // This frame actually submitted + presented (skip-submit did NOT elide it). A probe uses it to see how often a
    // "static" scene is force-presented anyway (a sustained loop animation marking TransformDirty defeats skip-submit).
    // NOTE this is `!skipSubmit`, decided BEFORE the render thread does anything — it means "published, not elided",
    // never "photons reached the panel". Cadence verdicts must come from present stamps, not from this bit.
    public bool Presented { get; init; }
    /// <summary>Did this frame drive scroll (a viewport offset advanced, or we are inside the post-scroll hold)? The
    /// authoritative per-frame bit the engine already computes for its own throttles — surfaced so a diagnostic
    /// consumer can normalise cadence metrics by SCROLL-ACTIVE time rather than wall time (an idle stretch otherwise
    /// dilutes every per-second figure) and can gate its emission on the gesture instead of on a fixed frame counter.</summary>
    public bool ScrollActive { get; init; }
    /// <summary>The publish seq this frame's DrawList was handed to the render seam under (0 when the frame elided its
    /// submit). This is the ONLY per-frame identity that survives the UI→render-thread boundary; see
    /// <see cref="AppHost.LastPresentPublishSeq"/> for the ack side and the join contract.</summary>
    public ulong PublishSeq { get; init; }
    // Probe-only record-time scroll capture (all default 0/false; populated only when AppHost.ProbeLyricsViewport /
    // ProbeMainViewport are set). Captured INSIDE RunFrame right after record, BEFORE ClearTransformDirty wipes the
    // content-node TransformDirty bit — so a probe can read the exact state that drove SceneRecorder's DoF-defer decision
    // (the post-RunFrame read always shows content-dirty == 0, which is why the previous probe couldn't attribute it).
    public int LyricsScrollMode { get; init; }
    public bool LyricsUserScrollActive { get; init; }
    public bool LyricsContentDirtyAtRecord { get; init; }
    public int MainScrollMode { get; init; }
    public bool MainContentDirtyAtRecord { get; init; }
    // Hitch attribution (populated when FG_FPS_LOG / FG_SCROLL_PERF): GC collection deltas since the previous painted
    // frame, plus the opt-in scroll-bind dirty census from ScrollBindEval (0 when FG_SCROLL_PERF is off).
    public int Gc0Delta { get; init; }
    public int Gc1Delta { get; init; }
    public int Gc2Delta { get; init; }
    public int StickyClipEvals { get; init; }
    public int StickyClipDirties { get; init; }
    public int StickyClipFullyHidden { get; init; }
    public int PinDirties { get; init; }
    public int MorphDirties { get; init; }
    public int ContinuousDirties { get; init; }
    public int ScrollBindCount { get; init; }
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
    PaceSkipSubmit,  // AppHost.DeriveAsyncPaceMs after an elided submit (sync path)
    PaceAsync,       // AppHost.DeriveAsyncPaceMs — async present pace cap (or the phase-gate ceiling while armed)
    DisplayRate,     // 0: latency-sensitive / one-shot motion — sync present-throttled (panel rate)
}

/// <summary>How the ambient-animation pacing RATE is selected (the cap's rate, not whether it engages — that stays
/// <see cref="AppHost.AnimIsAmbient"/> + the latency-sensitive/scroll-grace guards). Set via
/// <see cref="AppHost.AmbientRate"/>; see that property for the precedence rules.</summary>
public enum AmbientRateMode : byte
{
    /// <summary>Pace to the literal <see cref="AppHost.AmbientAnimationFps"/> value. A cap BELOW the panel rate that is
    /// not an integer divisor of it beats against the vsync-locked present (see <see cref="AppHost.AmbientFrameWaitMs"/>),
    /// so prefer <see cref="HalfRefresh"/> unless a specific number is the point (a diagnostic A/B).</summary>
    ExplicitFps,
    /// <summary>Pace to HALF the panel's CURRENT refresh — 120 Hz ⇒ 60, 90 Hz ⇒ 45, 60 Hz ⇒ 30 — re-derived every wait
    /// from the measured refresh period, so a display change (or a drag to a different-rate monitor) is picked up with
    /// no app involvement. Always an exact whole-vblank divisor, so it never beats against the present.</summary>
    HalfRefresh,
    /// <summary>No software cap: ambient loops run at the display rate (the old <c>AmbientAnimationFps = 0</c>).</summary>
    Uncapped,
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
    // Render-thread-visible copy of the live detached children (parent host only). The parent's ONE render thread iterates
    // this to drain each child's seam on its own present turn (DrainChildRenderSources). Mutated ONLY under a render-thread
    // rendezvous (AttachChildRenderSource/DetachChildRenderSource park the loop via Quiesce), so the render thread never
    // races a structural List mutation. Distinct from _detachedHosts (which the UI thread mutates freely for its own reaping).
    private readonly List<AppHost> _childRenderSources = new(1);
    private bool _isDetachedChild;   // true on a child host: it must not dispose the shared device, nor manage its own detached windows
    // On a detached CHILD host under a threaded parent (async or force-sync): the PARENT's render thread. The child spawns
    // NO render thread of its own (that would be a second submit/present owner racing the shared, render-confined device);
    // instead its RunFrame PUBLISHes to its own seam and WAKES this parent thread, which drains the child's seam + presents
    // the child's swapchain render-confined. Null on the primary host and on a child under a pure single-thread parent.
    private readonly Threading.RenderThread? _parentRenderThread;
    private bool _closedShutdownDone;   // guards the once-only on-close render-thread teardown (RunFrame close gate + Dispose)
    // On a detached CHILD host: the closed-callback the DetachedWindowHandle exposes, fired exactly once by the parent's
    // reaper (TickDetachedHosts) just before Dispose(). _onClosedFired guards against any double-fire.
    private Action? OnClosed;
    private bool _onClosedFired;
    // On a detached CHILD host: the SETTLED move/resize callback. The parent's reaper samples the window rect each frame
    // and fires this only once the rect has stopped changing, so an owner that persists geometry writes once per gesture
    // instead of once per pixel of a drag.
    private Action<RectF>? BoundsChanged;
    private RectF _lastBoundsPx;
    private int _boundsSettleFrames;
    private bool _boundsDirty;

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
    // The dedicated render thread, constructed for a real windowed host (mode Async — the default — or ForceSync). null ⇒
    // the SingleThread inline pass-through (headless, and the internal SingleThread override). It runs submit/present off
    // the UI thread; under ForceSync the UI still blocks on it (no async overlap), under Async it presents on its own timeline.
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
    // The resolved render-loop mode for THIS host (see RenderLoopMode). Real windowed hosts default to Async; a Headless
    // window is forced to SingleThread so the VerticalSlice gates stay deterministic; ForceSync is reachable only via the
    // internal constructor override (seam tests/probes). Detached children keep this mode but never spawn their OWN thread.
    private readonly RenderLoopMode _loopMode;
    // The effective async gate: mode == Async AND a REAL (non-headless) GPU backend. The render thread offloads real GPU
    // submit/present; a headless (test) backend has none, and its device seam methods (DrainImageJobs/RecoverDevice/…) are
    // no-ops — so headless always stays on the deterministic synchronous inline path. Every async branch keys off THIS, not
    // the raw mode, so the VerticalSlice headless gates are unperturbed. Exposed via LoopMode for host-actual-mode probes.
    private readonly bool _asyncActive;
    /// <summary>The resolved render-loop mode this host is running (Async is the default for real windowed hosts). Used by
    /// in-assembly / IVT diagnostics that need the host's ACTUAL mode rather than a removed env flag.</summary>
    internal RenderLoopMode LoopMode => _loopMode;
    /// <summary>True when this host runs the async render loop (the default for a real windowed host; never headless). The
    /// public read of the host's actual mode for app-side diagnostics/probes (e.g. WaveeResizeProbe) — replaces the removed
    /// FG_RENDER_ASYNC env flag, so probe behavior keys off the host's real state, not an env var.</summary>
    public bool IsAsyncRenderActive => _asyncActive;
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
    // Drag epoch → UseDragState. EDGE-triggered (session begin/end, OverTarget / Effect / Caption change, settle
    // start+expiry): the chip FOLLOWS through the DragPosX/Y binds below, so bumping this per frame — as it used to —
    // re-rendered the whole preview subtree at pointer rate for a value nothing read.
    private readonly Signal<int> _dragEpoch = new(0);
    private readonly FloatSignal _dragPosX = new(0f);    // live drag pointer, window DIP — bound, never re-rendered
    private readonly FloatSignal _dragPosY = new(0f);
    private bool _dragWasActive;
    private NodeHandle _dragOverPrev;                    // edge-detection state (scalar compares per frame, 0 alloc)
    private NodeHandle _dragRefusedPrev;                 // refusal is its own edge: over-nothing and over-a-refuser
                                                         // share OverTarget=Null + Effect=None + a null caption
    private DropEffect _dragEffectPrev;
    private string? _dragCaptionPrev;
    // Last live-session snapshot, retained across the settle window so the chip can animate out with its own content.
    private string _dragLastKind = "";
    private object? _dragLastPayload;
    private Point2 _dragLastPos;
    private DropEffect _dragLastEffect;
    // Drop-settle window published through DragState (Stationary lift only; Ghost keeps the OnSettle FLIP).
    private DragSettlePhase _dragSettlePhase;
    private RectF _dragSettleTarget;
    private float _dragSettleLeftMs;
    private DragSettlePhase _dragSettlePending;
    private RectF _dragSettlePendingTarget;
    private bool _dragSettleRequested;
    /// <summary>The chip-settle window a Stationary drag publishes on release (research target: ~250ms ease into the
    /// slot). The layer animates within it; the host tears the preview down when it expires.</summary>
    public const float DragSettleMs = 250f;
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
    private static readonly bool s_fpsLog = Diag.EnvFlag("FG_FPS_LOG");
    // FG_FPS_LOG hitch attribution: GC.CollectionCount deltas since the previous painted frame (0 when flag off).
    private int _prevGc0, _prevGc1, _prevGc2;
    private bool _gcSnapInitialized;
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
    // Render-thread seam — LANDED, async is the DEFAULT for real windowed hosts (RenderLoopMode.Async; headless stays
    // SingleThread). There is no env flag: FG_RENDER_THREAD and FG_RENDER_ASYNC were removed on 2026-07-23. ForceSync
    // survives only as an internal constructor override (seam tests/probes); nothing selects it by default.
    //
    // Historical note (the 2026-07-03 defect that once held async OFF): presenting from the render thread to the DComp-
    // composited swapchain produced a DIM/wrong ON-SCREEN composite while the back-buffer CaptureBgra passed (the blind
    // spot that hid it). ROOT CAUSE + FIX: BindDComp must run on the PRESENTING thread — deferring the DComp bind to the
    // render thread's first present (D3D12Device.cs:626-679) fixes the dim composite. Re-verified 2026-07-23 with on-screen
    // desktop captures + a 4-minute resize/scroll soak (zero device-lost). Windowed out-of-bounds popups use the in-window
    // clamped fallback under async (see PopupWindowsEnabled below); detached child hosts ride the PARENT's render thread
    // (they never spawn their own — the shared device is render-confined). (Aside: the lyrics choppiness async was once
    // meant to fix is GPU-bound — the DoF blur exceeds the vblank — a DoF cost reduction, not a threading change.)
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
    // The Mica backdrop override rides its OWN counter (Tok.WindowBackgroundEpoch): a window activation flip changes
    // only the frame's clear color, which is read live at submit, so it repaints WITHOUT a re-render or a cross-fade.
    private int _lastWindowBgEpoch;              // last Tok.WindowBackgroundEpoch the host forced a submit for

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

    // Runs ON the fgpu-render thread (bound Render) whenever one exists (mode Async — the default — or ForceSync) — the sole
    // toucher of the device/swapchain ComPtrs for submit+present in that mode. Reads the frame's bytes from the publisher's
    // per-slot arena (PickFreeSlot guarantees the UI is not writing that slot). ForceSync blocks the UI in DrainSync; Async
    // presents on its own timeline. Device/swapchain CREATION + UploadImage staging + resize/device-lost are still UI-side —
    // the documented async residuals (landing plan §9); ForceSync makes those splits safe meanwhile.
    /// <summary>Stop + join the fgpu-render thread so the UI thread becomes the SOLE GPU-ComPtr owner again — required
    /// before a one-shot UI-thread GPU op like <c>CaptureBgra</c> (--screenshot), which resets the command allocator +
    /// fence the render thread is otherwise using (the async capture race). No-op when no render thread; the host must
    /// not paint after this (Dispose's join is idempotent). This is the screenshot-path stand-in for the full async
    /// capture coordination (landing plan §9); it does not make windowed async safe (UploadImage/resize still race).</summary>
    public void QuiesceRenderThread() => _renderThread?.Dispose();

    private void SubmitPresentOnRenderThread(Threading.RenderFrame rf)
    {
        Threading.ThreadGuard.AssertRender();
        try
        {
            // Step 1 (async): stage uploads / free evictions on the render thread, BEFORE the submit opens its command list —
            // so a texture is resident before the draw that references it, and the store stays single-toucher (no lock).
            // INSIDE the try (deliberately): the staging path touches the device exactly like submit/present does, so a
            // device-removed failure there must land in the SAME recovery gate below. It used to sit one line outside,
            // which is how "Image.CreateUpload failed: 0x887A0005" left the fgpu-render thread as an unobserved
            // background exception and killed the process. The backend also soft-fails staging now (it rejects instead
            // of throwing) — this is the belt to that suspenders.
            if (_imageQueue is { } q) _device.DrainImageJobs(q);
            if (rf.SuppressVsync) { _device.SuppressVsyncOnce(); _device.SuppressLatencyWaitOnce(); }
            else if (rf.InteractivePresent) _device.SuppressVsyncOnce();
            _device.SubmitDrawList(_renderSeam.Bytes(rf), _renderSeam.SortKeys(rf), in rf.Submit, _swapchain);
            _swapchain.Present();
            NotePresented(rf.PublishSeq);
            // 11.5 (threaded) — the video hole-punch drain rides THIS present turn on the presenting thread, mirroring
            // the sync path's after-present ordering (AppHost.Paint phase 11.5). Both GetVideoPresenter and every
            // presenter call assert the render/submit thread when render-confined, so the drain MUST run here, not
            // UI-side; the UI-side call at phase 11.5 is skipped whenever a render thread exists. Uses the FRAME's scale
            // (rf.Submit.Scale) rather than the live _window.Scale — the drain must place video for the frame it presents.
            if (_device.GetVideoPresenter(_swapchain) is { } vp) _videoSurfaces.Drain(vp, rf.Submit.Scale);
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
        int mode = (int)_loopMode;   // 0/1/2 = single/force-sync/async (RenderLoopMode values are load-bearing here)
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
    private readonly long[] _actualPresentTimes = new long[240];
    private readonly long[] _actualPresentCounts = new long[240];
    private int _actualPresentTimeNext;
    private int _actualPresentTimeCount;
    private long _lastSampledPresentedSequence;
    private long _presentedSequence;
    // Present stamp + frame identity (the ONE pair that makes input→present correlation possible). Written on whichever
    // thread actually called Present — the render thread under the async default — and read UI-side, hence volatile.
    // _lastPresentQpc is sampled IMMEDIATELY after Present() returns: that is submit-confirmed, NOT vblank-confirmed, and
    // every consumer must carry that error bar (the vblank-attested form is DXGI GetFrameStatistics; see IPresentStats).
    private long _lastPresentQpc;
    private long _lastPresentPublishSeq;
    private ulong _framePublishSeq;   // UI-private: the seq THIS frame published under (0 when the submit was elided)
    private double _presentFps;
    private double _frameMs;
    private const double FpsWindowSeconds = 1.0;

    // Ambient-animation frame-rate cap (FG_ANIM_FPS env, default 30 Hz). 0 is the explicit diagnostic/app override for
    // UNCAPPED/display-rate ambient motion; a positive cap paces perpetual loops (a spinner, skeleton shimmer,
    // equalizer/media playhead, reveal fade, implicit brush transition, caret blink) where a sub-refresh rate is
    // imperceptible and idles the CPU. WARNING: a positive FIXED cap BELOW the panel's refresh BEATS against the
    // vsync-locked present (the software wait stacks onto the vblank quantization), so e.g. a 60 cap on a 120 Hz panel
    // reads ~40–60, not a clean 60 — which is exactly why AmbientRateMode.HalfRefresh (a panel-DERIVED rate, always a
    // whole-vblank divisor) exists and is what an app policy should prefer over a hard-coded number.
    // Latency-SENSITIVE motion (scroll/hover/press/drag/repeat — motion the user actively drives) is
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
    // Page-fill grace (same time base as the two scroll holds): a CLICK navigation arms no scroll hold, so without this
    // the ambient cap engaged for the whole page-enter image reveal (measured: ~1.1s pinned at exactly 60fps after an
    // album→artist nav) — the one case EffectiveLatencySensitiveWake's Image* demotion was never meant to catch. Armed
    // off the flush's component-render count rather than `reconciled`: a mounted per-frame poller (the seek ticker) makes
    // `reconciled` true on ordinary frames, which would hold the grace open forever and delete the demotion entirely.
    private long _mountGraceUntil;
    private static readonly long MountGraceTicks = (long)(0.5 * Stopwatch.Frequency);
    // A structural page-level reconcile renders a whole subtree; a hover/ticker/single-row re-render renders a handful.
    // 25 is the same "comps are high" line the render census already draws (Reconciler.MaybeDumpRenderCensus).
    private const int MountGraceCompThreshold = 25;
    /// <summary>The ambient cap's rate when <see cref="AmbientRate"/> is <see cref="AmbientRateMode.ExplicitFps"/>;
    /// 0 means uncapped (kept as the historical spelling — assigning 0 also flips <see cref="AmbientRate"/> to
    /// <see cref="AmbientRateMode.Uncapped"/>, and assigning a positive value flips it to
    /// <see cref="AmbientRateMode.ExplicitFps"/>, so pre-mode app code keeps its exact old meaning).
    /// <para><b>Precedence (highest first):</b> (1) the <c>FG_ANIM_FPS</c> env var — the diagnostic A/B knob, an explicit
    /// fps including <c>0</c> = uncapped; when it is set, app writes to this property and to <see cref="AmbientRate"/>
    /// are IGNORED so a capture can't be silently re-capped by app policy. (2) the app's own assignment (Wavee's
    /// power/attention policy). (3) the engine default, <see cref="AmbientRateMode.ExplicitFps"/> at 30.</para>
    /// Readable/writable from any thread (volatile scalar); apps set it from the UI thread.</summary>
    public int AmbientAnimationFps
    {
        get => Volatile.Read(ref _ambientFps);
        set => SetAmbientRate(value > 0 ? AmbientRateMode.ExplicitFps : AmbientRateMode.Uncapped, value);
    }

    /// <summary>How the ambient pacing rate is selected. Assigning <see cref="AmbientRateMode.ExplicitFps"/> keeps the
    /// current <see cref="AmbientAnimationFps"/> value; <see cref="AmbientRateMode.HalfRefresh"/> derives the rate from
    /// the live panel refresh every wait. Same precedence rules (and same env lock) as
    /// <see cref="AmbientAnimationFps"/>; safe to flip at runtime from the UI thread.</summary>
    public AmbientRateMode AmbientRate
    {
        get => (AmbientRateMode)Volatile.Read(ref _ambientRateMode);
        set => SetAmbientRate(value, Volatile.Read(ref _ambientFps));
    }

    private int _ambientFps = s_ambientFpsDefault;
    private int _ambientRateMode = (int)(s_ambientFpsDefault > 0 ? AmbientRateMode.ExplicitFps : AmbientRateMode.Uncapped);

    /// <summary>The one write path for both ambient-rate properties: honours the FG_ANIM_FPS lock and publishes the pair
    /// mode-first-consistent (each field is a volatile scalar; a reader that catches the pair mid-write still sees two
    /// individually valid values, and the next wait — ≤ one frame later — sees the settled pair).</summary>
    private void SetAmbientRate(AmbientRateMode mode, int fps)
    {
        if (s_ambientFpsFromEnv) return;   // the diagnostic override wins over app policy (documented on AmbientAnimationFps)
        Volatile.Write(ref _ambientFps, fps < 0 ? 0 : fps);
        Volatile.Write(ref _ambientRateMode, (int)mode);
    }

    private static readonly bool s_ambientFpsFromEnv =
        int.TryParse(Environment.GetEnvironmentVariable("FG_ANIM_FPS"), out var e) && e >= 0;
    private static readonly int s_ambientFpsDefault = ReadAmbientFps();
    private static int ReadAmbientFps() => int.TryParse(Environment.GetEnvironmentVariable("FG_ANIM_FPS"), out var v) && v >= 0 ? v : 30;

    /// <summary>Half-refresh's answer when the panel's refresh period is not known yet (no present has completed, or a
    /// headless device): the 60 Hz panel's answer, which is also the engine's historical default cap.</summary>
    private const int HalfRefreshFallbackFps = 30;

    /// <summary>Resolve the ambient pacing rate (fps) from the mode, the explicit-fps setting and the panel's CURRENT
    /// refresh. Pure and static so the pacing policy is testable without a host (VerticalSlice
    /// <c>gate.host.ambient-rate</c>): <see cref="AmbientRateMode.HalfRefresh"/> ⇒ <c>round(refreshHz / 2)</c>
    /// (120 ⇒ 60, 90 ⇒ 45, 60 ⇒ 30), clamped to ≥1 and falling back to <see cref="HalfRefreshFallbackFps"/> when the
    /// refresh is unknown (<paramref name="refreshHz"/> ≤ 0); <see cref="AmbientRateMode.ExplicitFps"/> ⇒ the explicit
    /// value verbatim (refresh-independent); <see cref="AmbientRateMode.Uncapped"/> ⇒ 0.</summary>
    /// <returns>The cap in fps, or 0 for "no cap".</returns>
    public static int DeriveAmbientFps(AmbientRateMode mode, int explicitFps, double refreshHz) => mode switch
    {
        AmbientRateMode.Uncapped => 0,
        AmbientRateMode.HalfRefresh => refreshHz > 0.0 ? Math.Max(1, (int)Math.Round(refreshHz / 2.0)) : HalfRefreshFallbackFps,
        _ => explicitFps > 0 ? explicitFps : 0,
    };

    /// <summary>Is a software ambient cap in effect AT ALL? The mode-aware replacement for the old
    /// <c>AmbientAnimationFps &gt; 0</c> test that gated both pacing branches: HalfRefresh is always engaged (its rate is
    /// only known once the refresh is read, inside <see cref="AmbientFrameWaitMs"/>), ExplicitFps only for a positive
    /// value, Uncapped never. Getting this wrong in either direction is a visible defect — false ⇒ the cap silently
    /// disappears, true under Uncapped ⇒ ambient motion is throttled after the app explicitly asked for display rate.</summary>
    private bool AmbientCapEngaged => AmbientRate switch
    {
        AmbientRateMode.Uncapped => false,
        AmbientRateMode.HalfRefresh => true,
        _ => AmbientAnimationFps > 0,
    };
    // FG_ADAPTIVE_FPS governor (default off): when the GPU genuinely cannot sustain the panel rate at the current size
    // (smoothed fence-wait over the ~120Hz budget — e.g. a maximized frame that rasters in ~14ms), pace CONTINUOUS
    // animation (playhead/shimmer) to the ambient cap instead of free-running the loop into vblank-misses. A steady 60
    // beats a jittery 60 and halves GPU/power; it NEVER engages for latency-sensitive frames (no added input/scroll
    // latency) and routes through the Resync-exempt AmbientFrameWaitMs so it can't trip the frozen-anim clock guard.
    // DEFAULT ON (opt out with FG_ADAPTIVE_FPS=0): on a fast GPU the EMA stays under budget so it NEVER engages — a no-op;
    // it only acts when the GPU is genuinely bound, turning a thrashing 60 into a steady one. Escape hatch keeps it safe.
    private static readonly bool s_adaptiveFps = Environment.GetEnvironmentVariable("FG_ADAPTIVE_FPS") is not ("0" or "false" or "FALSE" or "off");
    // A/B-only strict-120 path. It activates exclusively on a compositor-owned swapchain after timestamp queries prove
    // the actual GPU render is inside the 8ms budget. Set FG_SCROLL_PRESENT_INTERVAL0=1 together with FG_GPU_TIMING=1;
    // otherwise ordinary vsync remains untouched.
    private static readonly bool s_scrollPresentIntervalZero = Diag.EnvFlag("FG_SCROLL_PRESENT_INTERVAL0");

    // ── FG_BISECT_NO_IMAGE_PUMP: the image-pump bisection arm (ops/diag) ─────────────────────────────────────────
    // A BEHAVIOUR FORK, and the only kind of evidence that can settle the imageDecodeDuringScroll question. That
    // bucket's predicate is a correlation — "the phase-7.5 decode-apply cost is high while scroll is active" — and a
    // correlation cannot distinguish the pump CAUSING the dropped presents from the pump merely being busy during
    // them. Its refuter is therefore defined as a bisection: run the identical gesture with the pump suppressed
    // during scroll and see whether the present cadence changes.
    //
    // Suppresses ONLY the pump, ONLY while scroll is active: decodes keep completing on their workers and are applied
    // the moment the gesture settles. That is deliberately the shape a real fix would take (defer the apply past the
    // gesture), so a positive result names an intervention rather than just an accusation. Compile-fenced exactly like
    // FG_OPAQUE_WINDOW - it must be absent from a shipping build, because it visibly delays image reveals.
#if DEBUG || FLUENTGPU_DIAG
    private static readonly bool s_bisectNoImagePump = Diag.EnvFlag("FG_BISECT_NO_IMAGE_PUMP");
#else
    private const bool s_bisectNoImagePump = false;
#endif
    private long _bisectPumpsSuppressed;
    /// <summary>Diagnostic census: phase-7.5 image pumps skipped by the <c>FG_BISECT_NO_IMAGE_PUMP</c> arm. Non-zero
    /// PROVES the arm was live for this capture — a bisection whose suppression never actually engaged would
    /// otherwise read as "disabling the pump changed nothing", which is the opposite of what happened.</summary>
    public long BisectImagePumpsSuppressed => Volatile.Read(ref _bisectPumpsSuppressed);
    private const double ScrollPresentGpuBudgetMs = 8.0;
    private double _gpuBoundEma;   // smoothed recent GPU fence-wait (ms); governor input
    private const double GpuBoundBudgetMs = 10.0;   // sustained fence-wait above this ⇒ can't hold 120 (8.3ms) → pace to ambient
    // The governor NEVER paces these: genuine interactions (would add input/scroll latency) + an explicit UI frame-clock
    // poller (for example the compositor-bound playback playhead). It DOES pace art-reveal crossfades / one-shot transitions / ambient loops when GPU-bound (a 60Hz crossfade is
    // imperceptible, and the GPU can't do better than ~60 at that size anyway). Narrower than LatencySensitiveWake — which
    // includes the Image* bits — so the governor reliably engages during maximized playback where those bits stay set.
    private const WakeReasons GovernorNeverPace =
        WakeReasons.Interact | WakeReasons.ScrollAnim | WakeReasons.Repeat |
        WakeReasons.DragActive | WakeReasons.DragDropWork | WakeReasons.GestureHold | WakeReasons.TouchPress |
        WakeReasons.FrameClockPoller;
    private const WakeReasons LatencySensitiveWake =
        WakeReasons.Interact | WakeReasons.ScrollAnim | WakeReasons.Repeat |
        WakeReasons.DragActive | WakeReasons.DragDropWork | WakeReasons.GestureHold | WakeReasons.TouchPress |
        // Album-art reveals (decode → crossfade) fire DURING and right after a homepage scroll, and they are transient,
        // user-visible motion — keep them at the display rate instead of letting the ambient cap drop the reveal to 30 Hz
        // the instant the fling settles (a driver of the "scroll feels 24 fps then 120 fps" inconsistency).
        // DEMOTED OUTSIDE INTERACTION (see EffectiveLatencySensitiveWake): the original claim that "both bits clear the
        // moment decode/reveal finishes" does not hold for a PAGE FILL. ImageCrossfades is a global high-water deadline,
        // so a trickle of arrivals holds it true continuously for seconds after a nav — with these bits in the mask the
        // ambient cap could never engage and the loop ran at display rate through the whole fill. The intent above is
        // preserved exactly where it was argued (during and right after a scroll); past the scroll holds the reveals
        // keep running, at the 30 Hz ambient cadence, which is imperceptible for a crossfade.
        WakeReasons.ImageCrossfades | WakeReasons.ImagesPending | WakeReasons.ImageReady |
        // A mounted FrameClock consumer explicitly requested panel-rate UI work (the seek playhead uses this); native
        // DirectComposition video advances independently and instead posts a one-shot VideoPumpPending when needed.
        WakeReasons.FrameClockPoller | WakeReasons.VideoPumpPending;
    // The image bits of LatencySensitiveWake — display-rate ONLY while an interaction is live or just ended.
    private const WakeReasons ImageWake =
        WakeReasons.ImageCrossfades | WakeReasons.ImagesPending | WakeReasons.ImageReady;

    /// <summary>The latency-sensitive mask to test THIS frame: the full <see cref="LatencySensitiveWake"/> while the
    /// post-scroll holds OR the post-navigation page-fill grace are live (a reveal that fires during or right after a
    /// fling — or during the page-enter fill a click navigation just started — stays at display rate: the original
    /// intent), and the mask MINUS <see cref="ImageWake"/> once all three have expired (a background page fill that is
    /// no longer an entrance is not an interaction, so its reveals pace at the ambient cap like any other autonomous
    /// motion). Same time base as the holds the ambient branch already gates on — no allocation, three compares.</summary>
    private WakeReasons EffectiveLatencySensitiveWake(long nowTicks)
        => nowTicks < _scrollGraceUntil || nowTicks < _mainScrollHoldUntil || nowTicks < _mountGraceUntil
             ? LatencySensitiveWake
             : LatencySensitiveWake & ~ImageWake;

    // Modal-loop keep-alive paints must still run when any of these wake bits are set — even if ambient animation is
    // also live (playback seek ticker). Without this mask the InModalLoop+AnimIsAmbient bail swallowed warming virtual
    // lists mid-drag (detail-resize-flicker fix).
    private const WakeReasons ModalLoopEssentialWake =
        WakeReasons.FrameNeeded | WakeReasons.RuntimePending | WakeReasons.ScrollAnim |
        WakeReasons.DragDropWork | WakeReasons.DragActive | WakeReasons.GestureHold | WakeReasons.TouchPress |
        WakeReasons.PopupAnim | WakeReasons.ImagesPending | WakeReasons.ImageReady | WakeReasons.ImageCrossfades | WakeReasons.Orphans |
        // An explicit UI frame-clock poller or a queued native-video hand-off must not be swallowed by a modal loop.
        WakeReasons.FrameClockPoller | WakeReasons.VideoPumpPending |
        // A due frame-clock timer (a debounce/timeout/interval) must still fire while the user drags/resizes the window.
        WakeReasons.Timer |
        // Virtual-list catch-up used to ride FrameNeeded; keep them essential so modal ambient bail cannot starve refill.
        WakeReasons.WarmingVirtuals | WakeReasons.BudgetDeferredVirtuals;
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
        // Async is NO LONGER excluded: a detached child routes its present through THIS (the parent's) single render thread
        // (AttachChildRenderSource + _parentRenderThread), so there is never a second submit/present owner on the shared,
        // render-confined device. Still unavailable on a child host (no recursion), headless, or a backend without secondaries.
        if (_isDetachedChild || _isHeadless || !_device.SupportsSecondarySwapchains || request.Content is null)
            return null;
        float scale = _window.Scale;
        var desc = new WindowDesc(request.Title, request.InitialSizeDip, scale, Composited: true);
        var win = _app.CreateWindow(desc);

        // A 16:9-ish client floor so the mini-player can never be dragged down to an unusable sliver (caller-overridable).
        var minDip = request.MinClientSizeDip;
        win.SetMinClientSizePx(minDip.Width > 0f && minDip.Height > 0f
            ? new Size2(minDip.Width * scale, minDip.Height * scale)
            : new Size2(320f, 180f));

        // A RESTORED placement wins (the user put it there last time), clamped into the work area of the monitor nearest
        // to it — so a window remembered on a display that has since been unplugged still opens somewhere visible instead
        // of off-screen. Otherwise open at the bottom-right of the parent's monitor (a picture-in-picture home), fully
        // on-screen, instead of the CW_USEDEFAULT cascade. Falls back to CW_USEDEFAULT when the work area is unavailable
        // (headless / query failure → RectF.Infinite).
        var restored = request.InitialBoundsPx;
        bool haveRestored = restored.W > 1f && restored.H > 1f;
        var work = _app.GetWorkArea(haveRestored
            ? new Point2(restored.X + restored.W * 0.5f, restored.Y + restored.H * 0.5f)
            : _window.ClientOriginPx);
        if (haveRestored)
        {
            if (!work.IsInfinite)
            {
                float w = MathF.Min(restored.W, work.W), h = MathF.Min(restored.H, work.H);
                float x = MathF.Min(MathF.Max(restored.X, work.X), work.X + work.W - w);
                float y = MathF.Min(MathF.Max(restored.Y, work.Y), work.Y + work.H - h);
                win.SetBoundsPx(new RectF(x, y, w, h));
            }
            else win.SetBoundsPx(restored);
        }
        else if (!work.IsInfinite)
        {
            float wPx = request.InitialSizeDip.Width * scale;
            float hPx = request.InitialSizeDip.Height * scale;
            float margin = 24f * scale;
            float x = work.X + work.W - wPx - margin;
            float y = work.Y + work.H - hPx - margin;
            if (x < work.X) x = work.X;   // keep the left/top edges on-screen for an over-large request
            if (y < work.Y) y = work.Y;
            win.SetBoundsPx(new RectF(x, y, wPx, hPx));
        }

        win.Show();
        if (request.AlwaysOnTop) win.SetTopmost(true);

        // Create the host ONLY AFTER the window is sized + shown, so its swapchain, first layout, and published
        // Viewport.Size all use the FINAL client size. A host constructed before Show()/SetBoundsPx reads a 0×0 /
        // stale ClientSizePx → its scene root lays out at 0×0 and the composited swapchain presents nothing (the
        // detached window then renders fully transparent, and the idle loop spins on the broken window).
        var child = new AppHost(_app, win, _device, _fonts, _strings, request.Content, images: _images,
            compositeSwapchain: true, isDetachedChild: true, parentRenderThread: _renderThread);
        _detachedHosts.Add(child);
        // Register the child as a render source for the parent's render loop (a no-op reader when there is no render thread —
        // the pure single-thread parent leaves the child on the inline present path). The mutation rendezvouses with the
        // render thread so it never races an in-flight DrainChildRenderSources.
        AttachChildRenderSource(child);
        WakeFrame();
        return new DetachedWindowHandle(this, child, win);
    }

    /// <summary>Tick every live detached child host one frame (called by the loop right after the parent's own
    /// <c>RunFrame</c>, same thread). Reaps a window the user closed (dispose + remove). No-op with no detached windows.</summary>
    public void TickDetachedHosts()
    {
        // Parent closing: the render thread this frame's RunFrame just tore down (the window-close gate) still owns nothing,
        // so DO NOT tick children — a child.RunFrame would wake the now-disposed parent render thread. The children are
        // reaped by the parent's Dispose (which disposed the render thread first). The loop exits on the next !IsClosed check.
        if (_window.IsClosed) return;
        for (int i = _detachedHosts.Count - 1; i >= 0; i--)
        {
            var child = _detachedHosts[i];
            if (child._window.IsClosed)
            {
                _detachedHosts.RemoveAt(i);
                // Stop the render thread from touching this child's seam/swapchain BEFORE we dispose it. The rendezvous
                // (Quiesce) guarantees no in-flight DrainChildRenderSources present is mid-flight against the swapchain
                // we are about to release. No-op when the parent has no render thread (pure single-thread inline path).
                DetachChildRenderSource(child);
                // Fire the closed-callback once, on this (the UI+render) thread, before teardown. Programmatic Close()
                // also lands here (WM_CLOSE → IsClosed), so this single reap site gives exactly-once for free.
                if (!child._onClosedFired) { child._onClosedFired = true; var cb = child.OnClosed; child.OnClosed = null; cb?.Invoke(); }
                child.Dispose();
                continue;
            }
            child.RunFrame();
            child.SampleDetachedBounds();
        }
    }

    // Detached CHILD host, UI thread: sample the window rect and raise BoundsChanged once it has SETTLED. Only runs while
    // a detached window is open, and only when someone is listening — one cheap GetWindowRect per frame in that case.
    private void SampleDetachedBounds()
    {
        if (BoundsChanged is null) return;
        var now = _window.OuterBoundsPx;
        if (now.W <= 1f || now.H <= 1f) return;   // backend cannot report bounds → nothing to settle
        if (now != _lastBoundsPx)
        {
            _lastBoundsPx = now;
            _boundsSettleFrames = 0;
            _boundsDirty = true;
            return;
        }
        if (!_boundsDirty) return;
        // ~10 frames of stillness = the gesture is over. Anything shorter fires mid-drag; anything much longer loses the
        // last position if the app exits immediately after a move.
        if (++_boundsSettleFrames < 10) return;
        _boundsDirty = false;
        _boundsSettleFrames = 0;
        BoundsChanged?.Invoke(now);
    }

    // ── detached-window render routing (parent host; runs THROUGH the parent's single render thread) ─────────────────────

    /// <summary>The render thread that owns THIS host's submit/present: the host's own render thread if it has one, else the
    /// parent's (a detached child), else null (pure single-thread inline). Used to rendezvous swapchain resize/teardown.</summary>
    private Threading.RenderThread? OwningRenderThread => _renderThread ?? _parentRenderThread;

    /// <summary>UI thread (parent host): register a detached child as a render source for the parent's render loop. When a
    /// render thread exists it PARKS the loop (Quiesce) around the list mutation so the render thread never sees a torn
    /// structural change; without one it is a plain add (the child then presents inline on the pure single-thread path).</summary>
    private void AttachChildRenderSource(AppHost child)
    {
        Threading.ThreadGuard.AssertUi();
        if (_renderThread is { } rt) { rt.Quiesce(); try { _childRenderSources.Add(child); } finally { rt.Resume(); } }
        else _childRenderSources.Add(child);
    }

    /// <summary>UI thread (parent host): unregister a detached child (on close, BEFORE disposing it). Rendezvous-guarded so
    /// an in-flight <see cref="DrainChildRenderSources"/> can't be presenting the child's swapchain as it is released.</summary>
    private void DetachChildRenderSource(AppHost child)
    {
        Threading.ThreadGuard.AssertUi();
        if (_renderThread is { } rt) { rt.Quiesce(); try { _childRenderSources.Remove(child); } finally { rt.Resume(); } }
        else _childRenderSources.Remove(child);
    }

    /// <summary>Render thread (parent host): drain each registered child host's seam on this present turn — a fresh child
    /// publish is submitted+presented against the CHILD's own swapchain + video presenter, render-confined (the child reuses
    /// the same per-host <see cref="SubmitPresentOnRenderThread"/>). Runs every turn; a child with no new publish is a cheap
    /// <c>TryAcquire</c>-false no-op. The list is mutated only under a Quiesce rendezvous, so it is stable during a turn.</summary>
    private void DrainChildRenderSources()
    {
        Threading.ThreadGuard.AssertRender();
        var list = _childRenderSources;
        for (int i = 0; i < list.Count; i++)
        {
            var child = list[i];
            if (child._renderSeam.TryAcquire(out var rf))
                child.SubmitPresentOnRenderThread(rf);
        }
    }

    /// <summary>UI thread: stop + join this host's render thread on window close (idempotent with Dispose). Ordered BEFORE
    /// any swapchain/device teardown so the render thread — the sole ComPtr owner — is gone first.</summary>
    private void ShutdownRenderThreadOnClose()
    {
        if (_closedShutdownDone) return;
        _closedShutdownDone = true;
        _renderThread?.Dispose();   // stop + join (idempotent); a still-armed WakeAsync can no longer submit after this
    }

    /// <summary>The loop's wait, folded across this host and every detached child (so a playing pop-out keeps the loop at
    /// display rate even while the main window is idle/minimized). Calls <see cref="RecommendedWaitMs"/> (preserving its
    /// LastWaitKind/Ms side effects for logging), then combines each child's recommended wait.</summary>
    public int WaitMsWithDetached() => WaitRequestWithDetached().TimeoutMs;

    /// <summary>The loop's typed wait folded across this host and every detached child. Display-paced finite waits ask
    /// the platform to absorb pointer-motion wake storms up to the already-selected deadline; idle, ambient and urgent
    /// paths keep ordinary immediate input wake behavior.</summary>
    public PlatformWaitRequest WaitRequestWithDetached()
    {
        int w = RecommendedWaitMs();
        var request = WaitRequest(w, _lastWaitKind, _lastWaitWantsDisplayClock);
        for (int i = 0; i < _detachedHosts.Count; i++)
        {
            var child = _detachedHosts[i];
            int childWait = child.RecommendedWaitMs();
            request = CombineWait(request, WaitRequest(childWait, child._lastWaitKind, child._lastWaitWantsDisplayClock));
        }
        return request;
    }

    private static PlatformWaitRequest WaitRequest(int timeoutMs, HostWaitKind kind, bool wakeOnDisplayClock) => new(
        timeoutMs,
        timeoutMs > 0 && IsDisplayRateWait(kind, timeoutMs)
            ? PlatformInputWakePolicy.CoalescePointerMotion
            : PlatformInputWakePolicy.Immediate,
        wakeOnDisplayClock && timeoutMs > 0);

    // -1 = "block until a message" (no preference); any finite wait wins; min of two finite waits. At an equal
    // deadline the stricter motion-coalescing request wins, since both hosts are due at the same instant — and either
    // host asking for the display clock arms it, since both are due on the same vblank.
    internal static PlatformWaitRequest CombineWait(PlatformWaitRequest a, PlatformWaitRequest b)
    {
        if (a.TimeoutMs < 0) return b;
        if (b.TimeoutMs < 0) return a;
        if (a.TimeoutMs < b.TimeoutMs) return a;
        if (b.TimeoutMs < a.TimeoutMs) return b;
        return new PlatformWaitRequest(a.TimeoutMs,
            a.InputWakePolicy is PlatformInputWakePolicy.CoalescePointerMotion
                || b.InputWakePolicy is PlatformInputWakePolicy.CoalescePointerMotion
                    ? PlatformInputWakePolicy.CoalescePointerMotion
                    : PlatformInputWakePolicy.Immediate,
            a.WakeOnDisplayClock || b.WakeOnDisplayClock);
    }

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
        // Reads/writes the child host's field so the parent's reaper (which holds the child, not the handle) fires it.
        public Action? OnClosed { get => _child.OnClosed; set => _child.OnClosed = value; }
        public RectF BoundsPx => _window.OuterBoundsPx;
        public void SetTitle(string title) => _window.SetTitle(_child._strings.Intern(title));
        // Same indirection as OnClosed: the reaper samples the CHILD, so the callback must live on the child host.
        public Action<RectF>? BoundsChanged { get => _child.BoundsChanged; set => _child.BoundsChanged = value; }
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

    /// <summary>The async UI-loop pace cap, DERIVED from the panel's refresh period: just under one refresh, so the loop
    /// is ready before each vblank without free-spinning between them. <c>floor(refreshMs) − 1</c> = 7 ms at 120 Hz,
    /// 15 at 60, 5 at 144, 3 at 240.
    ///
    /// <b>Why a cap at all.</b> In the SYNC path, latency-sensitive frames returned a 0 wait and <c>Present</c> blocked
    /// the UI thread at vsync — THAT is what paced the loop. Under async, Present is off the UI thread, so a 0 wait
    /// free-spins (100k+ fps, pegging a core → thermal/scheduling contention that makes the render thread's presents
    /// irregular = judder). <c>WaitForWork</c> still returns EARLY on input, so latency is unchanged.
    ///
    /// <b>Why derived.</b> It was a hardcoded 7, which is the 120 Hz answer and the wrong number everywhere else: at
    /// 60 Hz it wakes the loop twice per refresh, and at 240 Hz it is a whole refresh late. The cap is SUPERSEDED as the
    /// primary pacer by the display-phase gate and the compositor clock, and survives as the backstop for when neither
    /// is available (no render thread, a stalled/occluded swapchain, a remote session with no compositor clock) — a
    /// wall-clock cap can bound HOW OFTEN the loop produces but never WHEN, and "when" is the whole problem.
    ///
    /// Pure and public so the gate can lock the arithmetic without a host. The clamp bounds a bogus or missing refresh
    /// period: 3 ms floors it short of a spin at any plausible panel rate, 32 ms is a two-refresh 60 Hz ceiling.
    ///
    /// A VARYING value here is safe, and that is not accidental: <see cref="IsDisplayRateWait"/> classifies by BRANCH,
    /// not by timeout value, so a refresh change cannot make a display-rate wait stop being recognised as one and
    /// spuriously trip the frame-clock step-up Resync (the frozen one-shot-anim bug class).</summary>
    public static int DeriveAsyncPaceMs(double refreshMs) => Math.Clamp((int)Math.Floor(refreshMs) - 1, 3, 32);

    /// <summary>The live pace cap for THIS host: <see cref="DeriveAsyncPaceMs"/> over the measured refresh period (which
    /// falls back to 60 Hz when the backend reports none — headless always does).</summary>
    private int AsyncDisplayPaceMs() => DeriveAsyncPaceMs(RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency);

    // ── Display-phase gate ───────────────────────────────────────────────────────────────────────────────────────────
    // Measured 2026-07-25 (ops/diag bundle 20260725-080953, 29,867 latency rows, operator-scored): the engine missed
    // ZERO deadlines — frameOverrun p50 −16 ms against a 16.7 ms budget, present interval p05 7.98 / p50 8.33 / p95
    // 8.69 ms, and DXGI PresentRefreshCount attested ~no dropped slots. Yet STEADY scored 1–3 while GLUED scored 3–4.
    //
    // The cause was phase, not throughput. The UI loop woke on input, on the DirectManipulation pump deadline, and on
    // the 7 ms cap above — a union that produced a frame every ~5.6 ms (p05 0.41 ms) against 8.33 ms presents, so 1.5–2.6
    // frames were produced per present and DropOldest discarded the surplus. Which publish happened to be newest at each
    // vblank then varied, so the POSITIONS reaching the screen were sampled 6.0–14.0 ms apart (p05→p95) even though the
    // FRAMES arrived a metronomic 8.33 ms apart. At the measured p50 gesture velocity that is a 13.5→31.6 DIP spread in
    // per-frame motion where smooth would be a constant ~18.7. Punctual pixels, jittering positions: "120 fps, not
    // buttery". Discarding 61% of rendered frames was the same symptom seen from the power side.
    //
    // The gate restores the phase reference the async flip removed: never produce a frame while a published one is still
    // unpresented. Because the render thread self-paces inside the swapchain's frame-latency waitable, its acks land one
    // refresh apart, so production inherits the display's phase and every frame's clock sample sits a CONSTANT offset
    // from the vblank that shows it. Constant offset is invisible; a varying one is the stutter.
    //
    // This is backpressure, not throttling. It cannot add visible latency: the frames it declines to produce are exactly
    // the ones DropOldest was about to discard, and input is dispatched BEFORE the gate, so contact samples keep landing
    // in the resampler's history and the next produced frame consumes all of them (the resampler is built to fold
    // several packets into one frame — that is what ResampleLatencyMs = 12 exists for).
    // The arm-then-recheck handshake and its memory ordering live in the primitive, not here — inlining them cost a
    // lost wake on ~16% of frames in the first implementation. See Threading/DisplayPhaseGate.cs.
    private Threading.DisplayPhaseGate? _phaseGate;

    // The gate's one blind spot, and its escape. Once a single vblank slips, the render thread's acks land one refresh
    // apart (16.67 ms on a 120 Hz panel) — UNDER the 17 ms ceiling, so the ceiling never fires — and ack-paced
    // production follows at 60 Hz indefinitely. The gate cannot see it: every publish IS presented, just one refresh
    // late, forever. Only the present site can (the interval between presents), so the detector lives there and the UI
    // side reads two volatile ints. See Threading/PresentSlipDetector.cs and threading-render-seam.md §11.1.4.
    private readonly Threading.PresentSlipDetector _slipDetector = new();
    private int _rephaseEpisodeSeen;        // the engage ordinal the escape budget below is currently spending
    private int _rephaseEscapesInEpisode;   // escapes taken inside that episode
    private long _rephaseEscapes;           // lifetime census (diagnostic)
    /// <summary>Escapes allowed per lock episode. Bounds the cost when a scene is genuinely too slow to hold the panel
    /// rate and therefore sustains the 2R cadence on its own: the escape re-anchors the chain a handful of times, finds
    /// the cadence unchanged, and stops. Eight is ~65 ms of re-phasing at 120 Hz — long enough to break a real lock,
    /// short enough that a hopeless one is not paid for every frame.</summary>
    private const int RephaseEscapeBudget = 8;

    /// <summary>Frames the display-phase gate declined to produce (diagnostic). Each one is a frame that would have
    /// been discarded by DropOldest before reaching the screen.</summary>
    public long PhaseGatedFrames => _phaseGate?.GatedFrames ?? 0;

    /// <summary>Times the display-phase gate's two-refresh liveness ceiling fired. Retained for liveness; every escape
    /// must be reported in pacing traces rather than treated as a smoothness win.</summary>
    public long PhaseGateCeilingEscapes => _phaseGate?.CeilingEscapes ?? 0;

    /// <summary>Times the armed gate was opened by the slip re-phase escape (§11.1.4) rather than by an ack or the
    /// ceiling. Its own counter, deliberately: a re-phase escape PRODUCES a frame, so it must not inflate
    /// <see cref="PhaseGatedFrames"/> (the census of declines) nor <see cref="PhaseGateCeilingEscapes"/> (the liveness
    /// backstop) — those two mean what the pacing argument uses them for only if this stays separate.</summary>
    public long PhaseGateRephaseEscapes => Volatile.Read(ref _rephaseEscapes);

    /// <summary>Stall ceiling for the gate, in ms: never wait more than two refresh periods for a present-ack. The gate
    /// must be an optimization, never a liveness dependency — an occluded, stalled, or device-lost render thread stops
    /// acking, and the loop has to keep running (input, timers, recovery) regardless. Clamped so a bogus or missing
    /// refresh period cannot produce either a spin (too low) or a visible freeze (too high).</summary>
    private int PhaseGateCeilingMs()
    {
        double refreshMs = RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency;
        int ms = (int)Math.Round(refreshMs * 2.0);
        return ms < 8 ? 8 : ms > 34 ? 34 : ms;
    }

    /// <summary>Stall ceiling in Stopwatch ticks (the gate's own clock domain).</summary>
    private long PhaseGateCeilingTicks() => (long)(PhaseGateCeilingMs() * (Stopwatch.Frequency / 1000.0));

    /// <summary>Should this frame take the slip re-phase escape — open the armed gate and PRODUCE, breaking the 60 Hz
    /// ack lock (§11.1.4)? Pure and internal so the gate can lock the truth table without a live panel, exactly as
    /// <see cref="DeriveAsyncPaceMs"/> is.
    ///
    /// The three conjuncts past <paramref name="rephaseWanted"/> are the ping-pong guards, and each closes a distinct
    /// way this could make the loop worse:
    /// <list type="number">
    /// <item><b>Kind must be <see cref="HostWaitKind.PaceAsync"/>.</b> The ambient and adaptive-governor branches sit
    /// BEFORE the armed branch in <see cref="RecommendedWaitMsCore"/> and produce <see cref="HostWaitKind.Ambient"/>.
    /// A loop that is deliberately capped — 30 Hz shimmer, a governor that measured the GPU cannot hold panel rate —
    /// presents ~2R by construction and would otherwise be forced back to panel rate by its own throttle's signature.</item>
    /// <item><b>Under budget.</b> A scene that genuinely cannot sustain the rate keeps producing the 2R cadence no
    /// matter how often the chain is re-anchored; the per-episode budget makes that cost bounded instead of permanent.</item>
    /// <item><b>Armed.</b> Armed means a publish is actually owed a present. Unarmed there is nothing to release, and
    /// the unarmed branch already asks for the display clock.</item>
    /// </list></summary>
    internal static bool ShouldRephaseEscape(bool rephaseWanted, bool gateArmed, HostWaitKind lastWaitKind, int escapesInEpisode, int budget)
        => rephaseWanted && gateArmed && lastWaitKind == HostWaitKind.PaceAsync && escapesInEpisode < budget;

    /// <summary>True when a published frame has not yet been presented, so producing another would only feed DropOldest.
    /// Delegates the arm/recheck handshake to <see cref="Threading.DisplayPhaseGate"/>. Open when async is off, when no
    /// render thread owns the present, or past the stall ceiling. UI thread only.</summary>
    private bool PhaseGateBlocks()
    {
        if (!_asyncActive || _renderThread is null || _phaseGate is null) return false;
        // A NEW lock episode gets a fresh budget: the count below bounds one episode's re-phasing, not the process's.
        int episode = _slipDetector.Episode;
        if (episode != _rephaseEpisodeSeen) { _rephaseEpisodeSeen = episode; _rephaseEscapesInEpisode = 0; }
        // The re-phase escape. While the present thread attests a sustained one-vblank slip, the wake that got us here
        // (the compositor tick the armed branch now asks for — see RecommendedWaitMsCore) must PRODUCE, not re-gate.
        // Re-gating on the tick is precisely the busywork the armed branch's rationale objects to; producing is what
        // re-anchors the present chain to the vblank the tick came from, and DropOldest makes the over-production safe.
        // _lastWaitKind is the kind latched by the wait that paced INTO this frame (RecommendedWaitMs), not a prediction.
        if (ShouldRephaseEscape(_slipDetector.RephaseWanted, _phaseGate.IsArmed, _lastWaitKind, _rephaseEscapesInEpisode, RephaseEscapeBudget))
        {
            _phaseGate.Open();           // idempotent, and never counts — this is a produced frame, not a decline
            _rephaseEscapesInEpisode++;
            _rephaseEscapes++;
            return false;
        }
        return _phaseGate.Blocks(_renderSeam.PublishSeq, Stopwatch.GetTimestamp(), PhaseGateCeilingTicks());
    }

    /// <summary>Render thread: nudge the UI out of its wait after a present, but only while it is actually parked on the
    /// gate. Elided otherwise so a 120 Hz present cadence does not post 120 wakes/s at a loop that is idle or already
    /// running (video playback presents continuously with nothing waiting on it).
    ///
    /// The barrier is required, not defensive: RenderThread publishes the ack with a release write, then this reads the
    /// armed flag — a StoreLoad pair that neither x86 nor ARM orders for free. Without it this can observe a stale
    /// "not armed" for a UI thread that has already armed and gone to sleep, which is precisely the lost wake the
    /// handshake exists to close. <see cref="IPlatformWindow.Wake"/> signals a waitable the UI
    /// <see cref="IPlatformWindow.WaitForWork"/> waits on atomically with input (and the HR timer), not message-only.
    /// <see cref="IPlatformWindow.WakePresent"/> rather than <see cref="IPlatformWindow.Wake"/>: this is the
    /// phase-critical signal, and it gets its own waitable so no other producer can consume the auto-reset wake this
    /// loop is parked on (and so it posts no message — at panel rate that would be 120 WM_NULLs a second).
    /// </summary>
    private void OnRenderPresentAck()
    {
        Thread.MemoryBarrier();
        if (_phaseGate is { IsArmed: true }) _window.WakePresent();
    }

    /// <summary>Monotonic successful main-swapchain present count in inline, force-sync, and async modes. Unlike a
    /// publish sequence, coalesced/dropped async frames do not inflate it.</summary>
    public ulong PresentedSequence => (ulong)Volatile.Read(ref _presentedSequence);
    /// <summary>The publish seq of the last frame that reached <c>Present()</c> — the real render-thread acknowledgement,
    /// not the present COUNT. (This used to alias <see cref="PresentedSequence"/>, which discarded the identity: a
    /// count cannot say WHICH frame's content is on screen, and every input→present join needs exactly that.)</summary>
    public ulong RenderPresentSeq => (ulong)Volatile.Read(ref _lastPresentPublishSeq);
    /// <inheritdoc cref="RenderPresentSeq"/>
    public ulong LastPresentPublishSeq => RenderPresentSeq;
    /// <summary>Stopwatch/QPC stamp taken immediately after the last successful <c>Present()</c> returned. SUBMIT-confirmed,
    /// not vblank-confirmed — the panel had not scanned out yet. 0 before the first present.</summary>
    public long LastPresentQpc => Volatile.Read(ref _lastPresentQpc);
    /// <summary>Frames handed to the render seam so far (UI side). <c>PublishSequence - PresentedSequence</c> is the only
    /// measure of DropOldest coalescing — publishes the render thread never presented because a newer frame replaced
    /// them. Nothing else in the engine counts those.</summary>
    public ulong PublishSequence => _renderSeam.PublishSeq;
    /// <summary>Frames the consumer has acquired. <c>PublishSequence - ConsumedSequence</c> is how far behind render is.</summary>
    public ulong ConsumedSequence => _renderSeam.LastConsumedSeq;
    /// <summary>The render thread's own present acknowledgement (falls back to the consumed seq in inline/force-sync
    /// modes, where there is no separate render thread to acknowledge).</summary>
    public ulong RenderPresentAck => _renderThread?.PresentAck ?? _renderSeam.LastConsumedSeq;
    /// <summary>Actual successful-present cadence over the trailing one-second window.</summary>
    public double PresentFps => _presentFps;
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
    /// <summary>Diagnostic (FG_GPU_TIMING=1): the rect/solid-fill, shadow, image, glyph and composite splits of <see cref="LastGpuSceneMs"/> (0 when off).</summary>
    public double LastGpuFillMs => _device.LastGpuFillMs;
    /// <inheritdoc cref="LastGpuFillMs"/>
    public double LastGpuShadowMs => _device.LastGpuShadowMs;
    /// <inheritdoc cref="LastGpuFillMs"/>
    public double LastGpuImageMs => _device.LastGpuImageMs;
    /// <inheritdoc cref="LastGpuFillMs"/>
    public double LastGpuGlyphMs => _device.LastGpuGlyphMs;
    /// <inheritdoc cref="LastGpuFillMs"/>
    public double LastGpuCompositeMs => _device.LastGpuCompositeMs;

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
        int raw = RecommendedWaitMsCore();          // sets _lastWaitKind
        int w = ClampWaitToTimers(raw, _lastWaitKind);
        _lastWaitMs = w;   // remembered so Paint can detect a throttle/idle → display-rate step-up and resync the frame clock
        // Latch the classification NOW, against the branch that produced it. Deriving it later from the timeout VALUE
        // was a real bug: the clamp rewrites the value without touching the kind, and two unrelated branches can return
        // the same integer — at 120 Hz the phase-gate ceiling and an Ambient 60-on-120 wait are both 17 ms.
        _lastWaitWasDisplayRate = IsDisplayRateWait(_lastWaitKind, w);
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
    /// timer keeps the loop from over-sleeping past its fire). A display-rate wait is left untouched: it already drains
    /// the timer next frame, and shortening it to a sub-frame value would spuriously trip the frame-clock step-up
    /// Resync (the frozen-one-shot-anim bug class). No armed timer ⇒ the wait is unchanged (a fully idle loop stays
    /// -1 → 0% CPU). Classified by BRANCH, not by timeout value — see <see cref="IsDisplayRateWait"/>.
    /// <para>
    /// The shortened wait is a REQUEST for the next frame to drain, never a guarantee that one will: <c>Paint</c> is the
    /// only <c>HostTimerQueue.Drain</c> call site and three <see cref="RunFrame"/> early-outs skip it (device-lost
    /// recovery, the minimize gate, a display-phase-gate decline). So an already-due timer must never shorten the wait
    /// to 0 — that turns the loop into a pure poll for as long as the drain stays out of reach. Hence the two guards
    /// below: minimized returns untouched, and every other clamp floors at 1 ms.
    /// </para></summary>
    private int ClampWaitToTimers(int w, HostWaitKind kind)
    {
        if (IsDisplayRateWait(kind, w)) return w;
        // Paint — the only drain site — is gated off while minimized, so no wait length can make a timer fire;
        // shortening the idle block converts a 0%-CPU sleep into a spin. A message (restore, WM_ACTIVATE, a power
        // broadcast) is what wakes a minimized loop, and the restore edge forces the frame that drains.
        if (IsMinimized) return w;
        if (!_timers.TryPeekEarliest(out double due)) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(0.0, due - _timers.NowMs));
        // The drain is on the NEXT frame, which may be skipped — never return 0 (that is a spin, not a wait).
        if (dueIn < 1) dueIn = 1;
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    /// <summary>Did the branch that produced the last wait want the DISPLAY clock as a wake source? Latched here, next
    /// to the branch, for the same reason <see cref="_lastWaitWasDisplayRate"/> is: re-deriving it later from the
    /// timeout value cannot distinguish an armed phase-gate wait from an unarmed pace wait — they are the same kind and
    /// can be the same integer, and they want OPPOSITE answers (see the two branches below).</summary>
    private bool _lastWaitWantsDisplayClock;

    private int RecommendedWaitMsCore()
    {
        // Default off: every branch that has a better phase reference, or none at all (idle, HUD, baked, ambient),
        // leaves the vblank waiter parked. Only the two branches below opt in.
        _lastWaitWantsDisplayClock = false;
        // Feed the FG_ADAPTIVE_FPS governor: smooth the true on-GPU raster time so a sustained over-budget stretch (a
        // maximized fill-bound frame) is detected without one-frame jitter flipping the pacing. Cheap; only when armed.
        // NOT the fence wait: that conflates raster with PACING — vblank/latency-waitable and buffer-release
        // serialization land in it, so the moment the cap paces to 60 the wait inflates to a full 13-16ms refresh
        // interval and the governor keeps itself engaged on its own output (a measured feedback loop). LastGpuRenderMs
        // is the DXGI-timestamp raster time and carries none of that, but it is 0 unless FG_GPU_TIMING=1 — so fall back
        // to the fence wait when the query heap is off, which is the pre-existing (contaminated) behavior, never worse.
        if (s_adaptiveFps)
        {
            double renderMs = _device.LastGpuRenderMs;
            _gpuBoundEma = _gpuBoundEma * 0.85 + (renderMs > 0 ? renderMs : _device.LastFenceWaitMs) * 0.15;
        }
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
        // Gated on REAL MOTION (an offset actually advanced on the last ticked frame), NOT on the ScrollAnim wake bit:
        // that bit is set by merely-ARMED viewports too (ScrollIntegrator.HasActive counts armed, not moving — a
        // scrollbar fade timer with zero motion sets it). Re-arming off the bit made the loop free-run at the display
        // rate for ~2s after EVERY scroll with `rendered 0` (the wakediag `sole: scrollAnim=N` bursts), defeating both
        // the ambient cap and the adaptive governor. The wake bit itself is untouched — an armed viewport still gets
        // frames for its fade, but a fade is ambient-class motion and now paces like one.
        long now = Stopwatch.GetTimestamp();
        if (_scrollAnim.AnyOffsetWroteThisFrame) _scrollGraceUntil = now + ScrollGraceTicks;
        // Ambient-only animation (no latency-sensitive interaction live, and any AnimEngine activity is loop-only — a
        // spinner/shimmer, NOT a one-shot transition mid-flight): pace to AmbientAnimationFps instead of the full
        // display refresh. A real input/post still wakes WaitForWork early, so this paces only the autonomous tick.
        // The cap ALSO defers through the 0.45s post-scroll hold (_mainScrollHoldUntil, refreshed at the phase-7 scroll
        // tick): slow wheel-notch scrolling over an ambient loop (skeleton shimmer) settles between notches, and without
        // the hold each notch stepped 30Hz→display-rate→30Hz — the step-up Resync at ApplyProjections' frame-clock guard
        // then dropped a stale ~34ms delta per notch, felt as a cadence lurch. Holding display rate through the whole
        // interaction keeps the clock monotonic; the cap resumes ~0.45s after the last real user-scroll frame.
        if (AmbientCapEngaged && (r & EffectiveLatencySensitiveWake(now)) == 0 && AnimIsAmbient()
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
        if (s_adaptiveFps && AmbientCapEngaged && (r & GovernorNeverPace) == 0
            && _gpuBoundEma > GpuBoundBudgetMs && now >= _scrollGraceUntil && now >= _mainScrollHoldUntil)
        {
            _lastWaitKind = HostWaitKind.Ambient;
            return AmbientFrameWaitMs();
        }
        // Skip-submit pacing floor: an elided submit skips Present — the sync path's ONLY pacer — so a scroll-armed-
        // but-unchanged stretch (a held/stuck band, a spring tail, the 2s scrollbar idle-hide dwell) would otherwise
        // free-run the loop at CPU speed re-recording a byte-identical scene (measured on-device: ~785 fps, a full
        // core, for the whole armed window). Pace those frames at DeriveAsyncPaceMs — the same value the async path
        // returns, deliberately: BOTH branches are display-rate KINDS, so both are exempt from the NextDeltaMs Resync
        // guard and the animation clock stays monotonic across a switch between them (a wait that read as a throttle
        // gap here would zero-dt every animating frame — the frozen one-shot-anim bug class). Input still ends the wait
        // immediately (WaitForWork is MsgWait-based), so nothing gains latency; the first frame that actually changes
        // pixels submits, and the next wait returns to 0 (present-throttled).
        // The display clock IS this branch's pacer, not a supplement: an elided submit produces no present, so there is
        // no ack to wake on and the wall-clock value below is a pure fallback for when the compositor clock is
        // unavailable. Waking on the tick puts even these frames on the panel's phase.
        if (!_asyncActive && _lastFrameSkippedSubmit)
        {
            _lastWaitKind = HostWaitKind.PaceSkipSubmit;
            _lastWaitWantsDisplayClock = true;
            return AsyncDisplayPaceMs();
        }
        // Parked on the display-phase gate: sleep until the render thread's present-ack wake, with the stall ceiling as
        // the backstop. Returning the pace cap here instead would wake the loop mid-flight only to gate again — busywork
        // that also re-samples the DirectManipulation pump deadline and drags production off the display's phase, which
        // is the behaviour being fixed. Input still ends this wait immediately (WaitForWork is MsgWait-based).
        // For the same reason this branch does NOT ask for the display clock: the ack IS its phase reference, and a
        // per-tick wake would re-enter the loop while the gate is still armed and gate again — the exact busywork above.
        // EXCEPT while the present thread attests a sustained one-vblank slip. Then the ack is not a phase reference at
        // all, it IS the 60 Hz attractor: acks land 16.67 ms apart on a 120 Hz panel, under the 17 ms ceiling, so the
        // ceiling never fires, production follows the acks, and the next present is late for the same reason — a stable
        // fixed point that held for minutes live (ops/diag/sessions/live-20260804-073148). Sleeping on the ack is then
        // sleeping on the lock, and the compositor tick is the only wake source outside it. The busywork objection still
        // stands, which is why the tick is not merely a supplementary wake here: PhaseGateBlocks OPENS the gate on it
        // instead of re-gating, so the woken frame produces and re-anchors the chain to the vblank. Bounded twice over —
        // the engaged episode ends on the first healthy present, and the escape budget bounds it inside the episode.
        if (_asyncActive && _phaseGate is { IsArmed: true })
        {
            _lastWaitKind = HostWaitKind.PaceAsync;
            _lastWaitWantsDisplayClock = _slipDetector.RephaseWanted;
            return PhaseGateCeilingMs();
        }
        // Unarmed async: nothing is owed a present, so there is no ack to sleep on and the wall-clock cap is the only
        // pacer — precisely the case the display clock exists to replace. Sync (DisplayRate) returns 0 and needs no wake
        // source at all.
        _lastWaitKind = _asyncActive ? HostWaitKind.PaceAsync : HostWaitKind.DisplayRate;
        _lastWaitWantsDisplayClock = _asyncActive;
        return _asyncActive ? AsyncDisplayPaceMs() : 0;   // latency-sensitive / one-shot motion: sync = present-throttled (0); async = pace cap (present is off-thread — 0 would free-spin)
    }

    /// <summary>Was the wait that paced INTO the current frame a display-rate one? Latched in
    /// <see cref="RecommendedWaitMs"/> against the branch that produced it, never re-derived later.</summary>
    private bool _lastWaitWasDisplayRate;

    /// <summary>True when the loop was ALREADY running at display rate, as opposed to throttled/idle.
    ///
    /// Classified by BRANCH (<see cref="HostWaitKind"/>), not by timeout value. The value-based form this replaces was
    /// wrong two ways. It aliased: <see cref="PhaseGateCeilingMs"/> is 17 ms on a 120 Hz panel and
    /// <see cref="AmbientFrameWaitMs"/> returns integers 1..17 there, so an Ambient-throttled frame that happened to
    /// compute 17 was classified display-rate — which skipped both the timer clamp and the step-up Resync, the exact
    /// cadence-lurch this guard exists to prevent. And it was stale: the gate wait was a mutable field read a frame
    /// later than it was written, so a refresh-rate change made a wait that WAS display-rate stop matching.
    ///
    /// The <c>w == 0</c> clause is not redundant, but it is SCOPED to the one kind that legitimately produces a 0:
    /// <c>BakedBlurQueue.RecommendedWaitMs</c> returns 0 for "due now" under <see cref="HostWaitKind.Baked"/>; no gap
    /// elapses, so resyncing there would reintroduce the lurch that kind-only classification is meant to avoid.
    /// Unscoped, the clause aliased in the other direction — the exact hazard the paragraph above describes, just by
    /// value 0 instead of 17: an Idle/Ambient wait that <see cref="ClampWaitToTimers"/> had rewritten down to 0 for a
    /// due timer then read as display-rate, which suppressed the step-up Resync on precisely the frames that HAD
    /// over-slept. The clamp no longer emits 0 (it floors at 1 ms), and this test no longer accepts one from any
    /// branch but Baked — so the code now matches the intent documented here.
    ///
    /// Getting this wrong is a known, non-obvious breakage rather than a style point. The frame-clock step-up guard
    /// resyncs the animation clock whenever the previous wait was a stale throttle gap; if a display-rate wait is not
    /// recognised as one, EVERY animating async frame resyncs, NextDeltaMs() returns 0 every frame, and one-shot enter
    /// transitions freeze at their initial (invisible) state — animated content never appears while static chrome does.</summary>
    private static bool IsDisplayRateWait(HostWaitKind kind, int w) =>
        kind is HostWaitKind.DisplayRate or HostWaitKind.PaceAsync or HostWaitKind.PaceSkipSubmit
            || (kind is HostWaitKind.Baked && w == 0);

    /// <summary>True when capping the frame rate won't dull a one-shot transition: either no AnimEngine track is running,
    /// or every active track is a perpetual LOOP (an indeterminate spinner, skeleton shimmer). A one-shot transition
    /// (page entrance, number pop, reveal) keeps the full display rate so it stays crisp.</summary>
    // A connected-animation fly OR a pending snapshot awaiting its dest is a one-shot transition — NEVER ambient. Without
    // the _connected guard, the AWAIT-DEST phase (snapshot captured, dest not yet laid out: _connected is active but no
    // spring track is seeded yet, and only the skeleton's LOOP shimmer runs) reads as all-loop → throttles to the 30 Hz
    // ambient cap, so the detail page mounts at 30 Hz and the transition stalls before the spring starts — the residual
    // "connected animation is sometimes laggy." Keeping the whole transition at display rate mounts the dest ~4× faster.
    private bool AnimIsAmbient() => !_connected.HasActive && (!_anim.HasActive || (_anim.LoopTrackCount == _anim.TrackCount && !_anim.DisplayRateActive));

    /// <summary>Milliseconds to wait before the next AMBIENT-animation frame so the loop holds the rate
    /// <see cref="DeriveAmbientFps"/> resolves from <see cref="AmbientRate"/> (an explicit
    /// <see cref="AmbientAnimationFps"/>, or half the live panel refresh) instead of free-running at the display refresh.
    /// = frame budget minus the time the just-finished frame took (this is called right after <see cref="RunFrame"/>),
    /// clamped to ≥0. Returns the full budget on the first frame.</summary>
    private int AmbientFrameWaitMs()
    {
        // The panel's refresh, MEASURED (DWM qpcRefreshPeriod) and re-read every wait — so HalfRefresh follows a display
        // change / a drag to a different-rate monitor with no app involvement, and no cached rate can go stale.
        long refreshTicks = _device.LastPresentStats.RefreshPeriodQpc;
        double refreshHz = refreshTicks > 0 ? Stopwatch.Frequency / (double)refreshTicks : 0.0;
        int ambientFps = DeriveAmbientFps(AmbientRate, AmbientAnimationFps, refreshHz);
        if (ambientFps <= 0) return 0;   // uncapped: display rate (AmbientCapEngaged already gates the branch; defensive)
        double budgetMs = 1000.0 / ambientFps;
        // Vblank-ANCHORED pacing whenever the panel's refresh period is known. A wall-clock budget is a timer that
        // free-runs against the vblank: at a 60 cap on a 120 Hz panel the 16.67 ms wait drifts through the 8.33 ms
        // refresh window, so the frame actually shown alternates between one produced just before a vblank and one
        // produced just after — a slow beat that reads as uneven shimmer/playhead motion even though the fps number is
        // exactly right. (The field comment above has warned since it was written that "a 60 cap on a 120 Hz panel
        // reads ~40-60"; this is that defect, and it is the same class as the display-phase gate's.)
        //
        // Anchoring the deadline to the last PRESENT — which is vblank-locked — and quantizing the period to a whole
        // number of refresh periods turns the cap into what it always meant: "show every Nth vblank". The modulo keeps
        // the result inside (0, period] no matter how stale the anchor is, so a stretch of skip-submitted (byte-
        // identical) ambient frames can never drive this to 0 and free-spin the loop.
        long lastPresent = Volatile.Read(ref _lastPresentQpc);
        if (refreshTicks > 0 && lastPresent != 0)
        {
            double refreshMs = refreshTicks * 1000.0 / Stopwatch.Frequency;
            int n = (int)Math.Round(budgetMs / refreshMs);
            if (n < 1) n = 1;                       // never pace FASTER than the panel
            double periodMs = n * refreshMs;
            double sinceMs = (Stopwatch.GetTimestamp() - lastPresent) * 1000.0 / Stopwatch.Frequency;
            if (sinceMs >= 0.0)
            {
                double dueMs = periodMs - sinceMs % periodMs;
                return (int)Math.Ceiling(dueMs);
            }
        }
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
    private long _framesStoodDown;              // census of covered/cloaked Present stand-downs — NOT skip-submit elisions
    /// <summary>Frames whose GPU submit+present was elided because the recorded command stream matched the last presented one.</summary>
    public long FramesSkippedSubmit => _framesSkippedSubmit;
    /// <summary>Frames whose Present was skipped because the window was covered/cloaked/iconic (see the device's covered
    /// stand-down). Deliberately NOT folded into <see cref="FramesSkippedSubmit"/>: that counter is the redundant-frame
    /// elision metric a perf capture is judged by, and a hidden window must not be able to inflate it.</summary>
    public long FramesStoodDown => _framesStoodDown;

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
        // Own bits (not folded into FrameNeeded) so FG_WAKE_DIAG can name the treadmill: warming vs budget vs latch.
        if (_reconciler.HasWarmingVirtuals) r |= WakeReasons.WarmingVirtuals;
        if (_reconciler.HasBudgetDeferredVirtuals) r |= WakeReasons.BudgetDeferredVirtuals;
        if (_runtime.HasPending) r |= WakeReasons.RuntimePending;
        if (_scene.HasDynamicText) r |= WakeReasons.DynamicText;
        // Anim wake: Cadence/NextDueMs — Driven-only rows are event-woken (signal write), not timer-due. HasActive alone
        // used to pin the host at panel rate for a paused Driven playhead; NextDueMs returns +∞ for that case.
        if (_connected.HasActive || (_anim.HasActive && _anim.NextDueMs(_timers.NowMs) <= 0f))
            r |= WakeReasons.Anim;   // connected fly / snapshot awaiting dest; hover/press fades are now _anim tracks too
        if (_scrollAnim.HasActive) r |= WakeReasons.ScrollAnim;
        if (_repeat.HasActive) r |= WakeReasons.Repeat;
        if (_caretBlinker.HasActive) r |= WakeReasons.Caret;
        if (_scene.HasBrushAnims) r |= WakeReasons.BrushAnims;
        if (_images.HasReadyCompletions) r |= WakeReasons.ImageReady;
        if (_device.HasPendingUploads) r |= WakeReasons.ImagesPending;
        if (_bakedBlurQueue.HasJobs) r |= WakeReasons.BakedBlurPending;
        if (_images.HasActiveCrossfades) r |= WakeReasons.ImageCrossfades;
        if (_scene.OrphanCount > 0) r |= WakeReasons.Orphans;
        if (_dispatcher.Drag.HasActiveWork || _dispatcher.DragDrop.HasActiveWork
            || _dragSettlePhase != DragSettlePhase.None) r |= WakeReasons.DragDropWork;   // E5: ghost spring easing / edge auto-scroll / chip settle
        if (_dispatcher.Drag.IsActive) r |= WakeReasons.DragActive;   // E5 reorder dwell keep-alive: a live drag keeps frames coming so the 200/300ms FrameClock dwell tickers advance even on a motionless pointer (DragController.cs:118)
        if (_dispatcher.HasArmedHold) r |= WakeReasons.GestureHold;   // §7A touch long-press: a STATIONARY held finger emits no input, so keep frames coming until TickGestureArenas fires the ~500ms Hold (then this clears and the loop idles)
        if (_dispatcher.HasPendingTouchPress) r |= WakeReasons.TouchPress;
        // A compositor-bound UI clock (not native video presentation) is an explicit request for panel-rate frames.
        // This keeps the seek playhead smooth while a native DirectComposition video presents decoded frames on its own.
        if (_frameClockSig.HasSubscribers) r |= WakeReasons.FrameClockPoller;
        // Native engines / geometry changes request one coalesced post-layout video pump. It is deliberately distinct
        // from playback state: a playing DComp video must not turn every host frame into a repaint.
        if (_videoSurfaces.HasPendingPumps) r |= WakeReasons.VideoPumpPending;
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
    /// <summary>Test-only companion read (gate.wake.scrollGraceNeedsMotion): the post-scroll display-rate grace deadline
    /// as RecommendedWaitMs last left it — the gate asserts an armed-but-motionless frame does not extend it.</summary>
    internal long ScrollGraceUntilForTest => _scrollGraceUntil;

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
                   ScrollTuning? scrollTuning = null, bool compositeSwapchain = false, bool isDetachedChild = false,
                   Threading.RenderThread? parentRenderThread = null)
        : this(app, window, device, fonts, strings, root, images, frameTime, scrollTuning, compositeSwapchain,
               isDetachedChild, parentRenderThread, loopModeOverride: null) { }

    // Internal ctor carrying the render-loop mode override. loopModeOverride is the ONLY way to request ForceSync (or pin
    // SingleThread) — there is no env var; nothing selects ForceSync by default. Reachable from the IVT seam tests/probes
    // (FluentGpu.VerticalSlice / FluentGpu.Windows.Tests) so they can exercise the threaded submit path without the async
    // timeline. A Headless window ignores the override and stays SingleThread (the deterministic gate path). The public
    // ctor above delegates here with null (⇒ Async for a real windowed host — the landed default).
    internal AppHost(IPlatformApp app, IPlatformWindow window, IGpuDevice device, IFontSystem fonts,
                     StringTable strings, Component root, ImageCache? images, IFrameTimeSource? frameTime,
                     ScrollTuning? scrollTuning, bool compositeSwapchain, bool isDetachedChild,
                     Threading.RenderThread? parentRenderThread, RenderLoopMode? loopModeOverride)
    {
        _app = app;
        _fonts = fonts;
        _isDetachedChild = isDetachedChild;
        _parentRenderThread = parentRenderThread;   // detached child: route presents through the parent's single render thread
        _window = window;
        // Render-loop mode decision: a Headless window is ALWAYS SingleThread (the deterministic path the slice/gates need);
        // a real windowed host defaults to Async (the landed default). loopModeOverride is the internal-only escape hatch —
        // seam tests/probes pass ForceSync (or SingleThread) explicitly; it never forces a Headless window off SingleThread.
        _loopMode = window.Handle.Kind == NativeHandleKind.Headless
            ? RenderLoopMode.SingleThread
            : (loopModeOverride ?? RenderLoopMode.Async);
        _asyncActive = _loopMode == RenderLoopMode.Async && window.Handle.Kind != NativeHandleKind.Headless;   // headless never goes async (see field)
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
        // The same guard, askable in advance, so an affordance can offer or withhold the option instead of dead-clicking.
        _inputHooks.CanOpenDetachedWindow = () => !_isDetachedChild && !_isHeadless && _device.SupportsSecondarySwapchains;
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
        _inputHooks.DragPosX = _dragPosX;
        _inputHooks.DragPosY = _dragPosY;
        InputHooks.Current.Default.DragEpoch = _dragEpoch;
        InputHooks.Current.Default.GetDragState = ReadDragState;
        InputHooks.Current.Default.DragPosX = _dragPosX;
        InputHooks.Current.Default.DragPosY = _dragPosY;

        // E5 chip settle: a Stationary gesture has no lifted node to FLIP home, so the controller reports the settle
        // WINDOW instead and the host publishes it in DragState for the preview layer to animate through. Latched here
        // (the gesture ends during input dispatch, before Paint's drag block drains it).
        _dispatcher.Drag.OnStationarySettle = (phase, target) =>
        {
            _dragSettlePending = phase;
            _dragSettlePendingTarget = target;
            _dragSettleRequested = true;
        };

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
        _reconciler.RequestFrame = WakeFrame;      // wake-only seam: mutate retained scene state, wake, DON'T re-render
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
        // A detached CHILD host shares the parent's device + ImageCache. The parent (always constructed FIRST) has already
        // installed the image-upload sinks, the baked-blur queue, the completion wake, and — under async — the render-confined
        // upload path on those shared objects. The child must NOT re-install any of them: doing so would CLOBBER the parent's
        // async sinks and, worse, hand the shared (render-confined) device a UI-thread upload path — a confinement violation.
        // The child's frames reference textures the parent's shared pipeline already made resident, plus its own video
        // presenter; shared-texture uploads/evicts continue to ride the parent's queue on the one render thread.
        if (!_isDetachedChild)
        {
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
                WakeFrame();
            };
        }

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
        _reconciler.SetAmbient(SharedTransition.BeginConfigured, new Signal<object?>((Action<FluentGpu.Animation.ConnectedTransitionRequest>)_connected.Begin));
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

        // Render-thread seam: spawn the fgpu-render thread that runs submit/present off the UI thread. This is the DEFAULT
        // for a real windowed host (mode Async — present on its own timeline; or the internal ForceSync — the UI blocks in
        // DrainSync). The thread just waits on its wake event until the first Paint drains it, so constructing it here
        // (before the first frame) is safe. A Headless window stays SingleThread (no render thread) — no GPU work to offload
        // and its device seam methods are no-ops — so the deterministic synchronous inline path is preserved for the gates.
        // A detached CHILD host NEVER spawns its own render thread: the shared device is render-confined (ONE submit/present
        // owner), so a second thread would race the single _cmdList/_queue/_fence undetected. The child instead routes its
        // present through the parent's thread (_parentRenderThread), which drains the child seam via DrainChildRenderSources.
        if (_loopMode is (RenderLoopMode.ForceSync or RenderLoopMode.Async) && window.Handle.Kind != NativeHandleKind.Headless && !_isDetachedChild)
        {
            // Step 4: under async, wire the device-lost recovery rendezvous — arm the backend to SIGNAL loss (not throw on
            // the render thread) + bound its fence waits, and give the render loop a recover gate (_device.RecoverDevice
            // under render confinement) + a thread-safe UI wake to nudge the UI out of its clean block on RecoverDone.
            if (_asyncActive) { _deviceLost = new Threading.DeviceLostCoordinator(); _device.EnableAsyncDeviceLostSignaling(); }
            // Constructed BEFORE the render thread: the thread can present (and call back) the moment it exists, and a
            // null gate on that path would drop the very first wake. The ack closure is allocated once here, never per
            // frame — `_renderThread` is captured by reference so the not-yet-assigned field resolves at call time.
            if (_asyncActive) _phaseGate = new Threading.DisplayPhaseGate(() => _renderThread?.PresentAck ?? 0UL);
            _renderThread = new Threading.RenderThread(_renderSeam, SubmitPresentOnRenderThread, async: _asyncActive,
                deviceLost: _deviceLost, recover: _deviceLost is null ? null : RecoverDeviceAfterDump, windowWake: _deviceLost is null ? null : _window.Wake,
                extraDrain: DrainChildRenderSources,
                // The display-phase gate's clock. Async only: force-sync already blocks the UI in DrainSync (its phase
                // reference was never lost), and a wake there would be pure overhead.
                presentWake: _asyncActive ? OnRenderPresentAck : null);
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
        _lastWindowBgEpoch = Tok.WindowBackgroundEpoch;
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
        if (dd.IsActive)
        {
            var s = dd.Session;
            return new DragState(true, s.Kind, s.Position, s.Payload, s.Effect, s.Caption,
                                 Refused: !s.RefusedTarget.IsNull);
        }
        // The gesture is over but its chip is still settling: keep reporting Active with the LAST live snapshot (plus
        // the settle phase/target) so the preview can glide out instead of vanishing at the release frame.
        if (_dragSettlePhase != DragSettlePhase.None)
            return new DragState(true, _dragLastKind, _dragLastPos, _dragLastPayload, _dragLastEffect,
                                 null, _dragSettlePhase, _dragSettleTarget);
        return default;
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

    /// <summary>Absolute per-drain ceiling on cross-thread UI posts. A backlog deeper than this is spread across frames
    /// (<see cref="DrainUiPosts"/> re-arms <c>_frameNeeded</c> while the queue is non-empty) instead of being paid in one
    /// synchronous frame — so ANY future accumulator bug degrades to a brief catch-up rather than a multi-second hang
    /// inside <c>DispatchMessageW</c> ("Not Responding"). 256 is far above any real frame's post count (Wavee's busiest
    /// bursts are single digits) and far below the thousands a long minimize used to pile up. Internal: the Engine.Tests
    /// ceiling gate pins the exact number.</summary>
    internal const int MaxUiPostsPerDrain = 256;

    /// <summary>Cross-thread UI posts still queued (test seam — the Engine.Tests drain gates; also read by the
    /// restore-edge diagnostic).</summary>
    internal int PendingUiPostCount => _uiPosts.Count;

    private void DrainUiPosts()
    {
        // TWO bounds, both load-bearing; FIFO is preserved either way (ConcurrentQueue dequeues in enqueue order).
        //  • The one-frame SNAPSHOT (`Count`) is the anti-LIVELOCK bound: an action that unconditionally re-Posts itself
        //    (re-enqueues + Wake()s) must not spin this drain — its re-post lands in _uiPosts and is picked up by a LATER
        //    frame (the Wake keeps the loop alive). The migrated cards never self-re-post, but the cap is cheap insurance.
        //  • MaxUiPostsPerDrain is the anti-BURST bound, which the snapshot alone never was: a queue that accumulated for
        //    minutes was still drained WHOLE in one frame. Capping the slice turns a pathological backlog into a bounded
        //    per-frame cost; the remainder re-arms _frameNeeded below so the loop keeps producing frames until it drains.
        int budget = Math.Min(_uiPosts.Count, MaxUiPostsPerDrain);
        while (budget-- > 0 && _uiPosts.TryDequeue(out var a))
            try { a(); } catch { /* a posted action must never take down the frame */ }
        // Re-arm on leftovers: _uiPosts is NOT a term in ComputeWakeReasons(), so a ceiling-truncated drain could
        // otherwise idle-gate before its next slice ran. (Every Post also carries its own WM_NULL, so the WAKE is already
        // guaranteed; this makes the FRAME guaranteed too — including after a self-re-post the snapshot deferred.)
        if (!_uiPosts.IsEmpty) _frameNeeded = true;
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
        if (FluentGpu.Foundation.ScrollTrace.CompiledIn && FluentGpu.Foundation.ScrollTrace.Enabled)
            FluentGpu.Foundation.ScrollTrace.AnimEvent((int)node.Raw.Index, 4, velocityPxPerS, sc.FlingFromOffset, 0f);
        _scrollAnim.Arm(node);
    }

    /// <summary>Events pumped into the ring this frame — recorded by the <see cref="FluentGpu.Foundation.ScrollTrace"/>
    /// frame marker (diagnostic only; written every frame, read only when the trace is on).</summary>
    private int _tracePumpedEvents;
    private uint _traceInputKindMask;

    private const uint WarmCadenceInputMask =
        (1u << (int)InputKind.PointerDown) | (1u << (int)InputKind.PointerUp)
        | (1u << (int)InputKind.PointerCancel) | (1u << (int)InputKind.Key)
        | (1u << (int)InputKind.KeyUp) | (1u << (int)InputKind.Char)
        | (1u << (int)InputKind.Wheel) | (1u << (int)InputKind.ScrollBegin)
        | (1u << (int)InputKind.ScrollUpdate) | (1u << (int)InputKind.ScrollEnd)
        | (1u << (int)InputKind.MomentumBegin) | (1u << (int)InputKind.MomentumUpdate)
        | (1u << (int)InputKind.MomentumEnd);

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

        // Window-close gate: the pump above dispatches WM_CLOSE (→ _closed = true, HWND destroyed). Once closed, STOP driving
        // the render thread NOW, on the UI thread, and paint nothing more. Without this a still-armed async WakeAsync (the
        // last frame published before the close) — or any subsequent frame — would submit/present against a swapchain whose
        // HWND was just torn down, and under async that runs on the render thread where the throw is swallowed but the join
        // ordering is fragile. Deterministically quiescing + joining the render thread here (idempotent with Dispose) means
        // the loop's next `!IsClosed` check exits cleanly and the process dies promptly (the IsBackground thread is only the
        // backstop). Cheap no-op after the first closed frame. Detached children (no own render thread) skip straight through.
        if (_window.IsClosed)
        {
            ShutdownRenderThreadOnClose();
            LastStats = new FrameStats(0, 0, 0, Rendered: false) { Fps = _fps, FrameMs = _frameMs };
            return LastStats;
        }

        ReadOnlySpan<InputEvent> inputEvents = _ring.Drain();
        uint inputKindMask = 0;
        for (int i = 0; i < inputEvents.Length; i++) inputKindMask |= 1u << (int)inputEvents[i].Kind;
        _traceInputKindMask = inputKindMask;
        int clicks = _dispatcher.Dispatch(inputEvents, _ring.DrainVelocitySamples());  // 2 input dispatch (handlers write signals → schedule effects)
        if (s_allocDiag) { db = Probe(SegDispatch, db, dt); dt = Stopwatch.GetTimestamp(); }
        // Passive hover/motion must not pin the host at display rate. Only interaction-semantic input (press/release,
        // keyboard, wheel or a scroll phase) and an actual handled click arm the post-input cadence hold.
        if (_warmCadenceEnabled && WarmCadenceHoldMs > 0f
            && (clicks > 0 || (inputKindMask & WarmCadenceInputMask) != 0))
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

        // ── Cross-thread UI posts (HostDispatch.Post / UsePost) ──────────────────────────────────────────────────────
        // Drained HERE: before the minimize gate AND before the idle gate further down. Both gates return early and
        // _uiPosts is NOT itself a term in ComputeWakeReasons(), so a drain placed after either one is structurally
        // unreachable for as long as that gate holds — and the queue is an UNBOUNDED ConcurrentQueue. Two hazards, one
        // drain:
        //   • IDLE gate — an otherwise-idle page (e.g. the migrated WindowsApi cards that dropped FrameClock.Tick) would
        //     early-return at `if (!HasActiveWork)` BEFORE Paint, the only other drain (inside Paint) would never run,
        //     and the posted signal writes would be stranded forever (a structural freeze, not a deadlock).
        //   • MINIMIZE gate — a minimized app keeps posting (Wavee folds ~1-2/s of playback position/state while
        //     minimized), and with the drain below the gate those posts accumulated for the ENTIRE minimize, then all
        //     landed in ONE drain on the restore frame — which runs synchronously inside the WndProc's WM_SIZE. Thousands
        //     of queued actions, each paying a cross-process SMTC RPC, is a multi-second "Not Responding" hang whose
        //     length is proportional to how long the window was minimized.
        // COST: zero extra wakeups. Post() enqueues and THEN Wake()s (PostMessage WM_NULL), so the loop is already
        // running one iteration per post; this only processes what already woke it. An empty queue is a no-op and
        // RecommendedWaitMsCore still returns -1 while minimized/idle, so a quiet loop stays blocked at 0% CPU.
        // ORDERING: unchanged relative to the pump + input dispatch above (a post still never jumps ahead of the same
        // frame's input). Relative to Paint it moved EARLIER within the same frame, which is unobservable to consumers:
        // posts are cross-thread MARSHALS whose only ordering contract is FIFO against each other (preserved), they were
        // already free to run either side of the idle gate depending on queue state, and Paint's own drain still runs
        // afterwards for anything posted in between. Running inside _runtime.Batch coalesces the actions' signal writes
        // into one re-render and defers the FrameRequested wake to the batch's end, where it sets _frameNeeded — so
        // HasActiveWork (FrameNeeded || HasPending) is true THIS frame and we fall through to Paint, whose
        // _runtime.Flush() applies the coalesced re-render. No lost-wakeup: Post enqueues before Wake, so a post that
        // arrives after this drain but before the gate still posted its own WM_NULL that re-wakes the loop next iteration.
        bool minimized = IsMinimized;
        bool restoreEdge = _wasMinimized && !minimized;
        int restorePosts = 0, restoreTimers = 0;
        long restoreDrainT0 = 0;
        if (restoreEdge) { restorePosts = _uiPosts.Count; restoreTimers = _timers.Count; restoreDrainT0 = Stopwatch.GetTimestamp(); }
        bool drainedPosts = !_uiPosts.IsEmpty;
        if (drainedPosts) _runtime.Batch(DrainUiPosts);
        if (restoreEdge)
            // Permanent + unconditional (no env knob): one line per restore is a human-rate event, and this IS the
            // standing evidence that the minimize accumulator stays dead. Same one-shot Console.Error shape as the
            // `[fps resize]` marker. Expect uiPosts≈0 and drainMs≈0 now that the drain runs while minimized; a large
            // backlog with a multi-ms drain is this bug regressing, and `left=` shows the ceiling spreading it.
            Console.Error.WriteLine(
                $"[restore] uiPosts={restorePosts} timers={restoreTimers} drainMs={(Stopwatch.GetTimestamp() - restoreDrainT0) * 1000.0 / Stopwatch.Frequency:0.00} left={_uiPosts.Count}");

        // Minimize gate: a minimized window paints nothing — but the pump+dispatch above MUST run so the restore
        // message lands (RecommendedWaitMs blocks indefinitely while minimized, so the loop only wakes on a message).
        // Skip Paint entirely (no record/submit/present), BEFORE the image-pump early-out below; the restore EDGE
        // forces a frame so the first visible frame paints immediately. Headless never reports Minimized (its State
        // defaults to Normal and only a test seam flips it), so the headless path is unaffected.
        if (restoreEdge)
        {
            _frameNeeded = true;   // restored: repaint now
            // Restore is a cold-start interaction edge exactly like the post-input arm above (search WarmCadenceInputMask):
            // the render thread and DM were parked through the minimize, so DM re-establishes its surfacing rhythm in
            // BURSTS. Without a hold, ComputeWakeReasons reads None in the gaps between bursts, RecommendedWaitMsCore takes
            // the Idle branch and ClampWaitToTimers stretches the wait to the next armed timer (the observed wait=idle703
            // mid-scroll → burst → idle stutter for ~2s). Arm the SAME warm-cadence hold input arms: it only prevents the
            // Idle branch (keeps the loop AWAKE), it is absent from LatencySensitiveWake/GovernorNeverPace so it can NEVER
            // force display rate or defeat the ambient cap (the _scrollGraceUntil-on-wake-bit free-run class), and it
            // self-expires after WarmCadenceHoldMs off the same wall clock. A restore is simply another interaction edge.
            if (_warmCadenceEnabled && WarmCadenceHoldMs > 0f)
                _warmCadenceUntilMs = _timers.NowMs + WarmCadenceHoldMs;
        }
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
            // The hoisted drain above ran, but Paint — the only _runtime.Flush() call site on the normal path — does not.
            // Flush here after a NON-EMPTY drain so the reactive pending queue does not simply become the new
            // accumulator: the posted signal writes are applied (memos recompute, effects run, components re-render into
            // the scene) instead of piling up until the restore frame. Same intent as the minimize-EDGE flush above, now
            // per drained minimized frame; a frame with nothing drained costs nothing.
            if (drainedPosts) _runtime.Flush();
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

        // (The cross-thread UI-post drain used to sit HERE, below the minimize gate. It is hoisted above that gate — see
        // the block before it — so a minimized app drains instead of accumulating. Render purity is unchanged: an empty
        // queue is a no-op and the loop still idles at RecommendedWaitMs == -1.)

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

        // Display-phase gate (see PhaseGateBlocks). Deliberately the LAST thing before Paint: the pump, the input
        // dispatch, the close/device-lost/minimize gates and the image pump above have all already run, so nothing that
        // affects correctness or input latency is skipped — only the production of a frame that could not have been
        // shown. Reported Rendered:false, which is already the shape of the five other early-outs in this method.
        if (PhaseGateBlocks())
        {
            LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, FrameMs = _frameMs };
            if (_wakeDiag is not null) { _wakeDiag.Record(wake, awake: true, rendered: false, reconciled: false, laidOut: false, minimized: false); _wakeDiag.MaybeReport(); }
            if (s_allocDiag) _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
            return LastStats;
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
        // FG_RENDER_DIAG tripwire (folds away in release). Both callees already early-out on !Enabled, so hoisting it
        // into the guard is behaviour-identical — and the non-const operand keeps the folded body off CS0162.
        if (RenderBudget.CompiledIn && RenderBudget.Enabled) { RenderBudget.FrameBoundary(); RenderBudget.MaybeReport(); }
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
            if (ScrollBindEval.PerfEnabled) ScrollBindEval.BeginPerfFrame(_scene);
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
            //      Warming / budget-deferred virtual lists (own wake bits) and any other essential wake bit still
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

            // Drain cross-thread UI posts so their signal writes land in THIS flush. RunFrame already drained them above
            // its minimize/idle gates, so on the normal frame path this is a no-op on an empty queue; it earns its keep on
            // the Paint-ONLY path (the PaintRequested keep-alive fired from inside an OS modal move/size loop, which
            // bypasses RunFrame entirely) — there a post that arrived mid-drag still applies this frame instead of being
            // stranded. It also picks up the second slice when RunFrame's drain hit MaxUiPostsPerDrain, which is exactly
            // the intended spread-across-frames behaviour (each slice is itself bounded by the same ceiling).
            if (!_uiPosts.IsEmpty) _runtime.Batch(DrainUiPosts);
            // Frame-clock timers (UseTimeout/UseInterval/UseDebouncedValue/UseThrottledValue): fire due callbacks INSIDE
            // the hot-phase window, before the flush, so their signal writes coalesce into THIS frame's re-render (same
            // rationale as the UI-post drain above). Skipped when nothing is armed → 0-alloc on every frame that uses no
            // timer, and 0-alloc on a quiet frame with an armed-but-not-due timer (Drain is one comparison then returns).
            if (_timers.Count > 0) _runtime.Batch(_drainTimers);
            // Frame clock: publish BEFORE the flush so per-frame pollers (FrameClock.Tick subscribers — the seek ticker,
            // overlay-close watchers) drain in THIS frame's flush and the runtime queue is EMPTY at frame end. Published
            // last it left one queued computation every single frame, so the RuntimePending wake reason fired on every
            // frame and the loop could never fall out of display rate. Only when watched — 0-alloc when nothing polls.
            if (_frameClockSig.HasSubscribers) _frameClockSig.Value = ++_frameClock;
            // ── Live drag publication (see the _dragEpoch field comment) ────────────────────────────────────────────
            // POSITION goes out as two float SIGNALS every frame: a bound preview transform is a compositor write, so a
            // drag move costs no render/reconcile/layout and no allocation. The EPOCH — which does re-render the preview
            // subtree — bumps only on the edges a preview's CONTENT depends on: session begin/end, the target under the
            // pointer, the advisory effect, and the target's caption. All scalar/reference compares; 0 alloc.
            bool dragActive = _dispatcher.DragDrop.IsActive;
            if (dragActive)
            {
                var ds = _dispatcher.DragDrop.Session;
                _dragPosX.SetIfChanged(ds.Position.X);
                _dragPosY.SetIfChanged(ds.Position.Y);
                bool dragEdge = !_dragWasActive || ds.OverTarget != _dragOverPrev || ds.Effect != _dragEffectPrev
                                || ds.RefusedTarget != _dragRefusedPrev
                                || !string.Equals(ds.Caption, _dragCaptionPrev, StringComparison.Ordinal);
                if (dragEdge)
                {
                    _dragOverPrev = ds.OverTarget;
                    _dragRefusedPrev = ds.RefusedTarget;
                    _dragEffectPrev = ds.Effect;
                    _dragCaptionPrev = ds.Caption;
                    _dragEpoch.Value = _dragEpoch.Peek() + 1;
                }
                // Retained for the settle window (the session is cleared the instant the gesture ends).
                _dragLastKind = ds.Kind;
                _dragLastPayload = ds.Payload;
                _dragLastPos = ds.Position;
                _dragLastEffect = ds.Effect;
                // A new gesture cancels a stale settle — INCLUDING an undrained latch. One coalesced dispatch batch can
                // carry the release of a Stationary drag and the promotion of the next one, so the publication frame
                // below never runs; leaving the latch armed would fire a phantom settle (with a stale rect) at the end
                // of THIS gesture, even a Ghost one that must never settle.
                if (_dragSettlePhase != DragSettlePhase.None) _dragSettlePhase = DragSettlePhase.None;
                if (_dragSettleRequested) { _dragSettleRequested = false; _dragSettlePending = DragSettlePhase.None; }
            }
            else if (_dragWasActive)
            {
                // The gesture ended since the last frame. A Stationary lift asked for a settle window (its chip glides
                // to the drop point / back home); everything else tears the preview down on this same bump.
                _dragSettlePhase = _dragSettleRequested ? _dragSettlePending : DragSettlePhase.None;
                _dragSettleTarget = _dragSettlePendingTarget;
                _dragSettleLeftMs = _dragSettlePhase != DragSettlePhase.None ? DragSettleMs : 0f;
                _dragSettleRequested = false;
                _dragSettlePending = DragSettlePhase.None;
                _dragOverPrev = NodeHandle.Null;
                _dragRefusedPrev = NodeHandle.Null;
                _dragEffectPrev = DropEffect.None;
                _dragCaptionPrev = null;
                _dragEpoch.Value = _dragEpoch.Peek() + 1;
            }
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
            // Backdrop-only flip (Mica activate/deactivate sets Theme.WindowBackground): the frame's CLEAR COLOR changed
            // and nothing else did — no token a component reads moved, so this must NOT retheme or cross-fade. It must
            // still reach the screen: the recorded command stream is byte-identical, so the skip-submit hash would elide
            // the present and the inactive fallback would never appear. Zero the presented-hash latch — the device-lost
            // idiom — to force exactly ONE real submit, which reads Clear live.
            if (Tok.WindowBackgroundEpoch != _lastWindowBgEpoch)
            {
                _lastWindowBgEpoch = Tok.WindowBackgroundEpoch;
                _lastPresentedDrawListHash = 0;
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
            // Page-fill grace: this flush rendered a page's worth of components (a nav / structural mount), so the image
            // reveal it just kicked off is an ENTRANCE, not background churn — hold display-rate pacing across it. Reuses
            // the census count already peeked above (no new state read) and the frame's own timestamp.
            if (censusComps >= MountGraceCompThreshold) _mountGraceUntil = frameStart + MountGraceTicks;
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
            long tSolve = Stopwatch.GetTimestamp();            // of LayoutMs: the flex solve itself (full or scoped + realize catch-up)

            DrainLayoutEffects();                              // 6.5 layout effects (Bounds valid)
            long tLayoutEffects = Stopwatch.GetTimestamp();
            _connected.ReducedMotion = Motion.ReducedMotion;   // 6.5 connected-animation: remember tag rects, seed flies to arrived dests, expire stale
            _connected.Tick65();
            long tConnected = Stopwatch.GetTimestamp();
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

            bool keepAliveSuppressed = _reconciler.ConsumeKeepAliveLayoutSuppressionFrame();
            if (capturedProjections) ApplyProjections(keepAliveSuppressed);       // FLIP "Last+Invert+Play"
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
            // Same EFFECTIVE mask as the pacing decision that produced the throttled wait we are stepping up from: once
            // the post-scroll holds expire, image reveals are ambient-paced, so a 33 ms gap between two of their frames is
            // a REAL interval and resyncing it away would stall the crossfade instead of un-lurching it.
            if (!_lastWaitWasDisplayRate)
            {
                WakeReasons stepUp = ComputeWakeReasons();
                if ((stepUp & EffectiveLatencySensitiveWake(frameStart)) != 0
                    || (_anim.HasActive && !AnimIsAmbient()) || _connected.HasActive)
                    _frameTime.Resync();
            }
            float dtMs = _frameTime.NextDeltaMs();
            _frameClockMs += dtMs;                             // frame-clock timer base (headless: the deterministic FixedFrameTimeSource step; ignored by the real-window wall clock)
            _anim.Tick(dtMs);                                  // 7 animation (transform/opacity/presented-size — never relayout)
            _reconciler.FinalizeKeepAliveTransitions();         // 7 park retained outgoing pages after their exit settles
            _inputHooks.RunAfterAnimations();                  // 7.1 tree lifecycle finalizers (overlays) before record/present
            RunIncrementalLayout();                            // 7 scoped subtree relayout for SizeMode.Relayout
            RunReflowLayout(layoutSize);                       // 7 boundary-scoped re-solve for SizeMode.Reflow (smooth reflow)
            // 7.2 video pump: event/geometry/transport requests are coalesced into one post-layout turn per surface.
            // Native DirectComposition video presents decoded frames independently, so a playing video no longer turns
            // every host frame into RepaintCurrentFrame. Render remains pure; registered pumps only write value-gated
            // intents, with fullscreen single-writer ownership enforced by the registry.
            _videoSurfaces.PumpPending(_scene.DeviceScale);
            ReclaimSettledOrphans();                           // 7 free settled exit orphans
            _connected.Settle();                               // 7 retire landed shared-element flies (reveal dest, unpin, free overlay)
            _connected.SyncDetached();                         // 7 flag-gated rebuild: mirror the engine-animated fly into its DetachedNode snapshot (RecordDetached draws it)
            // 7 eased hover/press: HoverT/PressT now driven by the engine's HoverFade/PressFade tracks (ticked in _anim.Tick above); InteractionAnimator deleted
            // 7 implicit BrushTransition: the cross-fade T is now driven by the unified engine (AnimChannel.BrushFade,
            // seeded at reconcile); the separate per-frame AdvanceBrushAnims ticker is deleted.
            // (TickTouchpad is gone — scroll phase events apply 1:1 at dispatch; design §6/§12.)
            if (FluentGpu.Foundation.ScrollTrace.CompiledIn && FluentGpu.Foundation.ScrollTrace.Enabled)
                FluentGpu.Foundation.ScrollTrace.Frame(dtMs, _tracePumpedEvents, _traceInputKindMask,
                    _scrollAnim.HasActive || _dispatcher.GestureActive);
            // scroll-feel-rework-v2 §4.1: the TouchpadTracking resampler targets frameT − ScrollTuning.ResampleLatencyMs
            // (12ms as shipped, NOT the 5ms of the original design — four comments drifted on that and are now fixed).
            // NOTE the sampled instant is thereby BEHIND frame start, not ahead to the frame's expected PRESENT time; the
            // signed gap is emitted as clockSampleSkewMs on the latency row so it is measured rather than assumed.
            // Feed the frame's QPC clock
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
            // 7 E5 edge auto-scroll (drag near an overflowing viewport edge).
            bool dragEdgeActive = _dispatcher.DragDrop.Tick(dtMs);
            // 7 E5: a reconcile ran THIS frame, so ApplyBox restored the dragged node's authored opacity/shadow/hit-test.
            // Re-assert the ghost before Tick — a settled/snap gesture early-outs and would otherwise record one
            // un-lifted frame. Guarded on IsActive so an ordinary reconcile frame pays nothing.
            if (reconciled && _dispatcher.Drag.IsActive) _dispatcher.Drag.ReassertPresented();
            _dispatcher.Drag.Tick(dtMs);                       // 7 E5 ghost: spring-lag easing + re-pin over the scrolled origin
            // 7 E5 chip settle: run the ~250ms post-gesture window down, then bump the epoch once so the preview layer
            // re-renders with Active=false and unmounts the chip. Bumping here schedules NEXT frame's re-render, which
            // is exactly right — the settle's last frame still has to paint.
            if (_dragSettlePhase != DragSettlePhase.None && !_dispatcher.DragDrop.IsActive)
            {
                _dragSettleLeftMs -= dtMs;
                if (_dragSettleLeftMs <= 0f)
                {
                    _dragSettlePhase = DragSettlePhase.None;
                    _dragSettleTarget = default;
                    _dragLastPayload = null;                   // release the payload's GC edge with the preview
                    _dragLastKind = "";
                    _dragEpoch.Value = _dragEpoch.Peek() + 1;
                }
            }
            _dispatcher.TickGestureArenas(dtMs);               // 7 §7A arena timer tick (Hold long-press promotion on idle-held frames)
            long tAnim = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegAnim, db, dt0); dt0 = Stopwatch.GetTimestamp(); }
            // 7.5 apply finished decodes + evict. The bisection arm skips ONLY the apply, and ONLY while scrolling —
            // Tick still runs, so cross-fades and eviction bookkeeping stay live and the arm changes one variable.
            if (s_bisectNoImagePump && scrollActive) _bisectPumpsSuppressed++;
            else
            {
                // The SYNC/inline analogue of the render-thread drain guard (SubmitPresentOnRenderThread): Pump's pixel
                // sink is `_device.TryUploadImage`, a device touch that fails with DXGI_ERROR_DEVICE_REMOVED in exactly
                // the same window as submit/present — and phase 7.5 sits OUTSIDE the submit try below, so the throw used
                // to escape Paint entirely. Route it into the SAME foreground recovery gate; a genuine (non-device-loss)
                // decoder/upload bug still propagates. The backend soft-fails staging first, so this is the net, not the
                // normal path (media-pipeline.md §4.1).
                try { _images.Pump(); }
                catch (Exception ex)
                {
                    if (!TryRecoverForegroundDeviceLost(ex, clicks)) throw;
                    return LastStats;
                }
            }
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
            // A stationary pointer emits no PointerMove while edge auto-scroll moves/recycles the rows beneath it.
            // Re-hit after catch-up so the nearest target and its insertion slot follow the newly visible content.
            if (dragEdgeActive) _dispatcher.RefreshDragDropAfterAutoScroll();
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
            // 7.8 drop-spotlight re-collect. AFTER reconcile/layout/realize + the scroll writes above and BEFORE record,
            // so the scrim's cutouts describe the bindings and the geometry THIS frame paints. A recycling virtual list
            // rebinds a realized row's logical item without ever rewriting its drop-target spec, so the per-move version
            // gate alone left the set stale in place (see DragDropContext.SyncSpotlightBeforeRecord). No-op when idle.
            _dispatcher.DragDrop.SyncSpotlightBeforeRecord();

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
                CollectionsMarshal.AsSpan(_popupSkipRoots), holdSelfBlurForAnyUserScroll: holdSelfBlurForScroll,
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
            // A bake is already bounded to ONE adaptive, downscaled job per cadence interval (BakedBlurQueue: the 33ms
            // throttle + adaptive quality + backlog downscale), so its per-frame cost is bounded by construction. Pause
            // only for DIRECT MANIPULATION — scroll, click, pumped input, drag. Reconcile/layout churn deliberately does
            // NOT pause it: a page that re-renders tens of times a second (the measured homepage does) never produces the
            // "quiet frame" the old `reconciled || layoutNeeded` predicate demanded, so the queue starved outright —
            // bakedBlurPending sat pinned at 96 for whole seconds (live-20260804-095007) while every acrylic/editorial
            // backdrop stayed at Minimal (0.25x) quality, which is the visible "blurred art stays blocky" complaint.
            // Image cross-fades, entrance motion and unrelated uploads were already excluded for the same reason.
            _bakedBlurQueue.Paused = scrollActive
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
                // Terminal `neverPresented`: this frame publishes nothing, so any latency sample tagged with it can
                // never join a present. Zeroing the tag makes that a LABELLED sample class in the trace rather than a
                // silent hole — a hole would make the pacing bucket look clean precisely when pacing is the fault.
                _framePublishSeq = 0;
                _framesSkippedSubmit++;
                _lastFrameSkippedSubmit = true;   // no Present happened → RecommendedWaitMs applies the pacing floor
                hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
                tSubmitDone = tSubmit = Stopwatch.GetTimestamp();
            }
            else
            {
                // Render-thread seam (Cut A): the UI records into _drawList and PUBLISHes it (copied into a FREE slot's
                // render-readable arena — PickFreeSlot makes the arena reuse safe for every mode). SingleThread (inline,
                // headless / internal override): the UI submits from the acquired arena — byte-identical to a direct submit.
                // ForceSync: the fgpu-render thread submits/presents; the UI BLOCKS in DrainSync. Async (the default):
                // the UI WakeAsyncs and PROCEEDS — the render thread presents on its own
                // timeline (the smoothness win: the GPU fence-wait no longer bounds back to the UI thread).
                // holdSelfBlurForScroll rides the seam as FrameInfo.ScrollHold: the acrylic retained-backdrop cache
                // rate-limits its re-blur to every Nth frame while it is set (§2.3/E10) — the same hold window the
                // self-blur groups already use, decided here so the flag describes the frame being published.
                var submitInfo = new FrameInfo(FrameSizePx(keepAlive), _window.Scale, Clear, recordStats.Damage, _images.ClockMs, _damageEpoch, holdSelfBlurForScroll);
                if (resized && keepAlive) _device.HintSettlePresent();
                double gpuRenderMs = _device.LastGpuRenderMs;
                bool interactivePresent = !keepAlive && s_scrollPresentIntervalZero && scrollActive
                    && _swapchain.SupportsCompositedIntervalZero
                    && gpuRenderMs > 0.0 && gpuRenderMs <= ScrollPresentGpuBudgetMs;
                // Keep the returned seq: it is this frame's identity across the seam, and the ONLY thing that lets a
                // present stamp be attributed back to the offsets this frame baked in (it was previously discarded).
                _framePublishSeq = _renderSeam.Publish(_drawList.Bytes, _drawList.SortKeys, in submitInfo,
                    suppressVsync: keepAlive, interactivePresent: interactivePresent);
                // Arm the display-phase gate for the frame we JUST handed over, before waking the render thread. The
                // gate is polled at the top of a frame, so a producing cycle used to run its whole record/layout/publish
                // with the gate disarmed — and OnRenderPresentAck elides its wake when unarmed, so the ack for this very
                // frame delivered nothing and the loop fell back to the wall-clock pace. Arming here (and BEFORE the
                // wake, so the ack cannot land in the gap) is what puts the producing frames back on the display's
                // phase. Never counts a gated frame: this one was produced, not declined.
                if (_asyncActive) _phaseGate?.ArmAtPublish(_framePublishSeq, Stopwatch.GetTimestamp());
                if (_renderThread is not null)
                {
                    if (_asyncActive) _renderThread.WakeAsync();   // async: UI does NOT wait (present happens later, render-side)
                    else _renderThread.DrainSync();                  // force-sync: block until the render thread presented
                    tSubmitDone = Stopwatch.GetTimestamp();          // async: present is off-thread; force-sync collapses the boundary
                }
                else if (_parentRenderThread is not null)
                {
                    // Detached CHILD host under a threaded parent: we own NO render thread (the shared device is render-
                    // confined — one submit/present owner). We published our frame to our OWN seam above; now wake the
                    // parent's render thread, which drains our seam (DrainChildRenderSources) and presents OUR swapchain
                    // render-confined. An inline UI-thread submit here would violate that confinement + race the parent's
                    // presents. Async: fire-and-return (BindDComp + video drain ride our first present on the render thread).
                    // Force-sync: block until the parent thread's turn drained us (mirrors the primary DrainSync contract).
                    if (_asyncActive) _parentRenderThread.WakeAsync();
                    else _parentRenderThread.DrainSync();
                    tSubmitDone = Stopwatch.GetTimestamp();
                }
                else
                {
                    try
                    {
                        if (_renderSeam.TryAcquire(out var rf))
                        {
                            if (rf.SuppressVsync) { _device.SuppressVsyncOnce(); _device.SuppressLatencyWaitOnce(); }
                            else if (rf.InteractivePresent) _device.SuppressVsyncOnce();
                            _device.SubmitDrawList(_renderSeam.Bytes(rf), _renderSeam.SortKeys(rf), in rf.Submit, _swapchain); // 10 submit (own swapchain — primary host: _swapchain IS _primarySwapchain)
                        }
                        tSubmitDone = Stopwatch.GetTimestamp();     // boundary: SubmitDrawList done, Present not yet called
                        _swapchain.Present();                       // 11 present (UI thread)
                        // rf is definitely-assigned on both TryAcquire outcomes; a false acquire leaves PublishSeq 0,
                        // which NotePresented treats as "no new content" and does not let move the ack.
                        NotePresented(rf.PublishSeq);
                    }
                    catch (Exception ex)
                    {
                        if (!TryRecoverForegroundDeviceLost(ex, clicks)) throw;
                        return LastStats;
                    }
                }
                if (maybeUnchanged) _lastPresentedDrawListHash = dlHash;   // track the stream only across quiet runs (active frames don't hash)
                // Covered Present stand-down skips the sync path's only pacer — reuse the skip-submit pacing floor.
                // Only when Present was awaited on this turn (inline / force-sync). Async Present completes later on the
                // render thread; reading LastPresentStoodDown here would sample the previous present.
                bool presentAwaited = (_renderThread is null && _parentRenderThread is null)
                    || (_renderThread is not null && !_asyncActive)
                    || (_parentRenderThread is not null && !_asyncActive);
                if (presentAwaited)
                {
                    // Counted SEPARATELY from skip-submit. `skipD` is the elided-redundant-frame metric a perf capture
                    // is judged by; folding stand-downs into it would let a covered/cloaked window manufacture a pass.
                    _lastFrameSkippedSubmit = _device.LastPresentStoodDown;
                    if (_lastFrameSkippedSubmit) _framesStoodDown++;
                }
                // A real submit+present paced this frame. MUST clear unconditionally: on the async path presentAwaited
                // is false (the default since the render-async flip), so without this the flag latches true for the rest
                // of the session after the first skip-submit frame, and the pacing floor then fires on a frame that
                // actually presented the moment _asyncActive drops (modal loop, resize, render-thread teardown).
                else _lastFrameSkippedSubmit = false;
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
            // Only on the pure single-thread path: the UI thread IS the presenting thread here. In threaded modes
            // (force-sync + async, `_renderThread is not null`) both GetVideoPresenter and the presenter are
            // render-thread-confined, so the drain rides SubmitPresentOnRenderThread instead (same after-present turn).
            // A detached CHILD routed through the parent's thread (`_parentRenderThread is not null`) is ALSO confined —
            // its video drain rides the parent thread's DrainChildRenderSources → child.SubmitPresentOnRenderThread — so
            // this UI-side drain must skip it too, or GetVideoPresenter's AssertSubmitThread trips on the UI thread.
            if (_renderThread is null && _parentRenderThread is null && _device.GetVideoPresenter(_swapchain) is { } vp) _videoSurfaces.Drain(vp, _window.Scale);

            DrainPassiveEffects();                             // 12 passive effects
            _strings.Tick();                                   // 12.5 reclaim released text ids (behind the reader quarantine)
            if (s_allocDiag) { db = Probe(SegEffects, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            UpdateFrameTiming(frameStart);
            int componentsRendered = _reconciler.ConsumeRenderCount();
            int gc0 = 0, gc1 = 0, gc2 = 0;
            if (s_fpsLog)
            {
                int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
                if (_gcSnapInitialized) { gc0 = c0 - _prevGc0; gc1 = c1 - _prevGc1; gc2 = c2 - _prevGc2; }
                _prevGc0 = c0; _prevGc1 = c1; _prevGc2 = c2;
                _gcSnapInitialized = true;
            }
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
                SpansRebaseRejected = recordStats.SpansRebaseRejected,
                SpansReRecorded = recordStats.SpansReRecorded,
                SpanBytesCopied = recordStats.SpanBytesCopied,
                SpanReuseDisabledReasons = recordStats.SpanReuseDisabledReasons,
                MeasureCount = _layout.DiagMeasure,
                ArrangeCount = _layout.DiagArrange,
                TextShapeMisses = _layout.DiagTextMiss,
                RootRelayoutEscapes = _invalidator.EscapesThisFrame,
                Fps = _fps,
                PresentFps = _presentFps,
                PresentedSequence = this.PresentedSequence,
                FrameMs = _frameMs,
                ComponentsRendered = componentsRendered,
                FlushMs = ToMs(tFlush - frameStart),   // incl. flip/FLIP-capture + reactive flush + reconcile
                ReactiveFlushMs = reactiveFlushMs,
                VirtualRealizeMs = virtualRealizeMs,
                LayoutMs = ToMs(tLayout - tFlush),
                LayoutSolveMs = ToMs(tSolve - tFlush),                 // of which: FlexLayout (full/scoped + D1 realize catch-up)
                LayoutEffectsMs = ToMs(tLayoutEffects - tSolve),       // of which: DrainLayoutEffects
                ConnectedTickMs = ToMs(tConnected - tLayoutEffects),   // of which: ConnectedAnimation.Tick65
                ReflowSeedMs = ToMs(tLayout - tConnected),             // of which: enter/exit reflow seeding (+ scene dump)
                LocalRelayoutResolves = _invalidator.LocalResolvesThisFrame,
                AnimMs = ToMs(tAnim - tLayout),         // phase-7 ticks + projections
                RecordMs = ToMs(tRecord - tAnim),       // image pump + SceneRecorder (+ text shaping) + dyntext
                ImagePumpMs = ToMs(tImagePump - tAnim),            // of which: phase-7.5 decode apply/evict
                ImageApplyCount = _images.LastPumpAppliedCount,
                ImageApplyBytes = _images.LastPumpAppliedBytes,
                RealizeCatchupMs = ToMs(tRealizeCatchup - tImagePump), // of which: phase-7.6 re-realize + scoped relayout
                SubmitMs = ToMs(tSubmit - tRecord),     // command build + GPU submit + present (total; ~0 on a skipped frame)
                FenceWaitMs = skipSubmit ? 0.0 : _device.LastFenceWaitMs,  // of which: UI-thread stall on the frame fence + latency waitable
                PresentMs = ToMs(tSubmit - tSubmitDone),// of which: the Present() call (0 on a skipped frame)
                GpuRenderMs = _device.LastGpuRenderMs,
                GpuSceneMs = _device.LastGpuSceneMs,
                GpuFillMs = _device.LastGpuFillMs,
                GpuShadowMs = _device.LastGpuShadowMs,
                GpuImageMs = _device.LastGpuImageMs,
                GpuGlyphMs = _device.LastGpuGlyphMs,
                GpuCompositeMs = _device.LastGpuCompositeMs,
                Presented = !skipSubmit,
                ScrollActive = scrollActive,
                PublishSeq = _framePublishSeq,
                LyricsScrollMode = probeLyMode,
                LyricsUserScrollActive = probeLyUser,
                LyricsContentDirtyAtRecord = probeLyDirty,
                MainScrollMode = probeMainMode,
                MainContentDirtyAtRecord = probeMainDirty,
                Gc0Delta = gc0,
                Gc1Delta = gc1,
                Gc2Delta = gc2,
                StickyClipEvals = ScrollBindEval.PerfEnabled ? ScrollBindEval.StickyClipEvals : 0,
                StickyClipDirties = ScrollBindEval.PerfEnabled ? ScrollBindEval.StickyClipDirties : 0,
                StickyClipFullyHidden = ScrollBindEval.PerfEnabled ? ScrollBindEval.StickyClipFullyHidden : 0,
                PinDirties = ScrollBindEval.PerfEnabled ? ScrollBindEval.PinDirties : 0,
                MorphDirties = ScrollBindEval.PerfEnabled ? ScrollBindEval.MorphDirties : 0,
                ContinuousDirties = ScrollBindEval.PerfEnabled ? ScrollBindEval.ContinuousDirties : 0,
                ScrollBindCount = ScrollBindEval.PerfEnabled ? ScrollBindEval.ScrollBindCount : 0,
            };
            PublishFrameStats(LastStats);
            // Hitch attribution into the scroll trace (>12ms frames only): the per-phase split lands in the SAME CSV as
            // the offset writes, so a lurch is directly attributable (GPU fence stall vs realize vs record vs shaping).
            if (FluentGpu.Foundation.ScrollTrace.CompiledIn && FluentGpu.Foundation.ScrollTrace.Enabled && dtMs > 12f)
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
            // Latency row — ONE per scroll-active frame, and deliberately NOT gated on a hitch threshold the way the
            // FrameTiming row above is. The case this whole facility exists for ("cadence looks perfect, feel is bad")
            // produces no hitch rows at all, so a dt-gated sensor would go silent exactly when it is needed.
            if (FluentGpu.Foundation.ScrollTrace.CompiledIn && FluentGpu.Foundation.ScrollTrace.Enabled && scrollActive) EmitLatencyRow(dtMs);
            // (the frame-clock publish moved to phase 3, just before the flush — see the drain block there)
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
    /// host converts to physical virtual-screen px (client origin + scale), resizes the popup swapchain, and seeds its
    /// chrome while the window remains hidden. The first successful popup present reveals it without activation.</summary>
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
                contentPx, opensUp, MathF.Max(0f, closedRatio), 8f * s, 1f * s));
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
                    // Atomic creation: the popup HWND stays hidden until its swapchain contains the seeded first frame.
                    // This prevents an uninitialized/full-opacity plate from flashing before the engine/compositor
                    // entrance state exists.
                    if (!slot.Window.IsShown)
                    {
                        slot.Window.Show();
                        sc.AnimatePopupOpen();
                    }
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

    private void ApplyProjections(bool keepAliveSuppressed = false)
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
        bool suppressed = Motion.LayoutTransitionsSuppressed || keepAliveSuppressed;
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

    // ── Latency-row assembly (diagnostics; only reached when the scroll trace is armed AND scroll is active) ─────────
    // Everything here is integer/float arithmetic over values the frame already computed. No allocation, no formatting,
    // no syscalls beyond the two Stopwatch reads the frame took anyway — the phases 6-13 zero-alloc contract still binds.

    private long _prevLatencyPresentQpc;   // the present stamp this row's predecessor saw (for the present interval)
    private uint _prevPresentRefreshCount;  // DXGI vblank ordinal of the previous attested present (0 = none yet)

    /// <summary>Refresh period in QPC ticks, MEASURED (DWM qpcRefreshPeriod) rather than nominal. Falls back to 60 Hz
    /// only when the backend reports nothing; a consumer distinguishes the two via the stats' Valid bit.</summary>
    private long RefreshPeriodQpcOrDefault()
    {
        long p = _device.LastPresentStats.RefreshPeriodQpc;
        return p > 0 ? p : Stopwatch.Frequency / 60;
    }

    private void EmitLatencyRow(float dtMs)
    {
        var ps = _device.LastPresentStats;
        long vsyncTicks = RefreshPeriodQpcOrDefault();
        double msPerTick = 1000.0 / Stopwatch.Frequency;
        long presentQpc = Volatile.Read(ref _lastPresentQpc);

        // Present interval + missed refresh slots. The half-interval bias before the integer divide is part of the
        // standard definition, not a rounding convenience. Both are 0/-1-suppressed on the first row of a session,
        // which is structurally never late.
        float presentIntervalMs = 0f;
        int missedVsyncs = 0;
        if (_prevLatencyPresentQpc != 0 && presentQpc > _prevLatencyPresentQpc)
        {
            long interval = presentQpc - _prevLatencyPresentQpc;
            presentIntervalMs = (float)(interval * msPerTick);
            missedVsyncs = (int)((interval + vsyncTicks / 2) / vsyncTicks) - 1;
            if (missedVsyncs < 0) missedVsyncs = 0;
        }
        if (presentQpc != 0) _prevLatencyPresentQpc = presentQpc;

        // OS-ATTESTED missed slots (supersedes the stamp-derived count above where present): the difference between
        // consecutive vblank ordinals at which the display pipeline actually showed our frames, minus the one slot a
        // healthy frame is entitled to. Carried BIASED BY +1 so that "not attested" (0) stays distinguishable from
        // "attested zero missed" — those are opposite conclusions, and a bare 0 would silently merge them.
        int attestedPlus1 = 0;
        if (ps.Valid && ps.PresentRefreshCount != 0)
        {
            if (_prevPresentRefreshCount != 0 && ps.PresentRefreshCount > _prevPresentRefreshCount)
            {
                long slots = (long)ps.PresentRefreshCount - _prevPresentRefreshCount - 1;
                if (slots < 0) slots = 0;
                if (slots > 0xFFFE) slots = 0xFFFE;
                attestedPlus1 = (int)slots + 1;
            }
            _prevPresentRefreshCount = ps.PresentRefreshCount;
        }
        int missedPacked = (missedVsyncs & 0xFFFF) | (attestedPlus1 << 16);

        // "We woke up late" vs "we were slow" — two different bugs with two different fixes. Slack is the part of the
        // raw frame gap that no measured phase accounts for; subtracting the wait the loop DELIBERATELY asked for
        // leaves only the unrequested absence. Note 113 is the coarse, hitch-gated form of the same discriminator and
        // stays as the cross-check.
        float rawDt = _frameTime is StopwatchFrameTimeSource s ? s.LastRawDeltaMs : dtMs;
        float workMs = (float)(LastStats.FlushMs + LastStats.LayoutMs + LastStats.AnimMs + LastStats.RecordMs + LastStats.SubmitMs);
        float requestedWaitMs = _lastWaitMs > 0 ? _lastWaitMs : 0f;
        float wakeOverheadMs = rawDt - workMs - requestedWaitMs;
        if (wakeOverheadMs < 0f) wakeOverheadMs = 0f;

        // Deadline model: the real deadline is bufferCount x refresh, NOT one 16.7ms frame — with the render-thread seam
        // plus the consume-gated quarantine the pipeline is several frames deep, and measuring against a single frame
        // over-reports hitches for this architecture. SIGNED: negative is headroom, and the distribution's negative tail
        // is as diagnostic as its positive one.
        const int SwapchainBufferCount = 2;
        float frameOverrunMs = (float)((workMs) - (vsyncTicks * SwapchainBufferCount * msPerTick));

        // clockSampleSkewMs: the offset baked into this frame represents frameStart − ResampleLatencyMs, but the frame
        // will be SEEN at roughly the next refresh boundary. A consistently non-zero mean means the engine is animating
        // from the wrong instant — perfect FPS, zero missed vsyncs, and still the wrong amount of motion per frame.
        // Only meaningful when this frame actually RESAMPLED a contact: the quantity is "the instant the scroll
        // position was sampled from, versus when it will be shown", and a frame with no tracking sample (a wheel
        // chase, an idle tick) sampled nothing. Emitting it there produced a plausible-looking number derived from
        // frame-start alone, which is not the same measurement and would be averaged in as if it were.
        float clockSampleSkewMs = 0f;
        double frameQpcSec = _scrollAnim.FrameQpcSec;
        if (frameQpcSec > 0.0 && presentQpc != 0 && _scrollAnim.TrackingLagSampled)
        {
            double offsetSampleQpc = frameQpcSec * Stopwatch.Frequency - FluentGpu.Animation.ScrollTuning.ResampleLatencyMs / msPerTick;
            double expectedPresentQpc = presentQpc + vsyncTicks;
            clockSampleSkewMs = (float)((offsetSampleQpc - expectedPresentQpc) * msPerTick);
        }

        // Multi-label stage set: EVERY stage over one refresh period, never a single winner. One frame legitimately
        // carries several tags, and collapsing to a winner is how a secondary cause gets a fix it did not need.
        double vsyncMs = vsyncTicks * msPerTick;
        int stageMask = 0;
        if (wakeOverheadMs > vsyncMs) stageMask |= 1 << 0;
        if (LastStats.FlushMs > vsyncMs) stageMask |= 1 << 1;
        if (LastStats.LayoutMs > vsyncMs) stageMask |= 1 << 2;
        if (LastStats.AnimMs > vsyncMs) stageMask |= 1 << 3;
        if (LastStats.RecordMs > vsyncMs) stageMask |= 1 << 4;
        if (LastStats.ImagePumpMs > vsyncMs) stageMask |= 1 << 5;
        if (LastStats.RealizeCatchupMs > vsyncMs) stageMask |= 1 << 6;
        if (LastStats.SubmitMs > vsyncMs) stageMask |= 1 << 7;
        if (LastStats.FenceWaitMs > vsyncMs) stageMask |= 1 << 8;

        double genSec = _scrollAnim.LastContactSampleSec;
        long genQpc = genSec > 0.0 ? (long)(genSec * Stopwatch.Frequency) : 0L;
        var quality = genQpc == 0 ? Foundation.GenStampQuality.Tick : Foundation.ScrollTrace.ContactStampQuality;

        FluentGpu.Foundation.ScrollTrace.Latency(
            _framePublishSeq, quality, stageMask, missedPacked,
            _scrollAnim.TrackingLagSampled ? _scrollAnim.TrackingLagDip : 0f,
            wakeOverheadMs, frameOverrunMs, clockSampleSkewMs, presentIntervalMs,
            _scrollAnim.TrackingLagSampled ? _scrollAnim.TrackingVelocityDipPerMs : 0f, genQpc,
            // The exact present join: WHICH published frame the seam had acknowledged when this row was written.
            // Without it a consumer can only infer the winner from row adjacency.
            ackedPublishSeq: LastPresentPublishSeq);
        _scrollAnim.TrackingLagSampled = false;   // one row per sample; a stale value must not be re-reported as fresh
    }

    /// <summary>Called on whichever thread just returned from <c>Present()</c> (the render thread under the async
    /// default). Stamps WHEN the present returned and WHICH published frame it carried, then bumps the present count.
    /// The QPC read must stay the first statement: everything downstream of Present is attribution error.
    ///
    /// A present with <paramref name="publishSeq"/> == 0 (nothing newly acquired — the previous frame is still on
    /// screen) does NOT move the ack, so the ack stays monotone and a joiner never sees it go backwards.</summary>
    private void NotePresented(ulong publishSeq)
    {
        long qpc = Stopwatch.GetTimestamp();   // first statement: everything downstream of Present is attribution error
        Volatile.Write(ref _lastPresentQpc, qpc);
        if (publishSeq != 0) Volatile.Write(ref _lastPresentPublishSeq, (long)publishSeq);
        Interlocked.Increment(ref _presentedSequence);
        // The one place the 60 Hz phase-lock is observable (§11.1.4): the interval between consecutive presents. The
        // gate sees only "publish owed a present", and in the locked state every publish IS presented — one refresh
        // late, forever. Present-thread-owned; the UI side only reads two volatile ints.
        _slipDetector.OnPresent(qpc, RefreshPeriodQpcOrDefault());
    }

    private void UpdateFrameTiming(long frameStart)
    {
        long now = Stopwatch.GetTimestamp();
        _frameMs = (now - frameStart) * 1000.0 / Stopwatch.Frequency;
        UpdateActualPresentTiming(now);
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

    private void UpdateActualPresentTiming(long now)
    {
        long sequence = Volatile.Read(ref _presentedSequence);
        long windowTicks = (long)(FpsWindowSeconds * Stopwatch.Frequency);
        if (sequence == _lastSampledPresentedSequence)
        {
            if (_actualPresentTimeCount > 0)
            {
                int newest = (_actualPresentTimeNext - 1 + _actualPresentTimes.Length) % _actualPresentTimes.Length;
                if (now - _actualPresentTimes[newest] > windowTicks) _presentFps = 0.0;
            }
            return;
        }

        _lastSampledPresentedSequence = sequence;
        _actualPresentTimes[_actualPresentTimeNext] = now;
        _actualPresentCounts[_actualPresentTimeNext] = sequence;
        _actualPresentTimeNext = (_actualPresentTimeNext + 1) % _actualPresentTimes.Length;
        if (_actualPresentTimeCount < _actualPresentTimes.Length) _actualPresentTimeCount++;
        if (_actualPresentTimeCount < 2) return;

        int newestIndex = (_actualPresentTimeNext - 1 + _actualPresentTimes.Length) % _actualPresentTimes.Length;
        long newestTime = _actualPresentTimes[newestIndex];
        long newestCount = _actualPresentCounts[newestIndex];
        long oldestTime = newestTime;
        long oldestCount = newestCount;
        for (int i = 1; i < _actualPresentTimeCount; i++)
        {
            int index = (newestIndex - i + _actualPresentTimes.Length) % _actualPresentTimes.Length;
            long candidateTime = _actualPresentTimes[index];
            if (newestTime - candidateTime > windowTicks && newestCount > oldestCount) break;
            oldestTime = candidateTime;
            oldestCount = _actualPresentCounts[index];
        }

        double elapsed = (newestTime - oldestTime) / (double)Stopwatch.Frequency;
        if (elapsed > 0.0001 && newestCount > oldestCount)
            _presentFps = (newestCount - oldestCount) / elapsed;
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
        // A detached child's swapchain is presented by the PARENT's render thread, so its resize must park THAT thread too —
        // OwningRenderThread resolves to _renderThread (primary) or _parentRenderThread (child). Force-sync + single-thread
        // take the else (the render thread is idle-parked between publishes, never mid-present concurrently with a resize).
        //
        // Resize runs out of the WndProc, and every step of D3D12Swapchain.Resize (the fenced WaitForGpu signal,
        // ResizeBuffers, the GetBuffer per RTV) Checks its HRESULT and throws on a removed device — an unhandled throw
        // inside a window message. Consult NoteIfDeviceLost: a recorded loss means SKIP the resize (RecoverDevice
        // rebuilds the swapchain wholesale, and one stale-size frame until the recovery frame lands is invisible next to
        // a crash); anything else is a genuine bug and rethrows. The exception FILTER runs before the finally, so the
        // render loop is still Resumed on both outcomes.
        if (OwningRenderThread is { } rt && _asyncActive)
        {
            rt.Quiesce();
            try { _swapchain.Resize(s); }
            catch (Exception) when (_device.NoteIfDeviceLost()) { }
            finally { rt.Resume(); }
        }
        else
        {
            try { _swapchain.Resize(s); }
            catch (Exception) when (_device.NoteIfDeviceLost()) { }
        }
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
            // The position signals are OURS too (the same seam, installed in the same place): leaving them installed
            // points every later reader at a disposed host's signals.
            if (ReferenceEquals(def.DragPosX, _dragPosX)) def.DragPosX = null;
            if (ReferenceEquals(def.DragPosY, _dragPosY)) def.DragPosY = null;
        }
        // A host disposed mid-settle would otherwise pin the last drag's payload for its own lifetime.
        _dragLastPayload = null;
        _dragLastKind = "";

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
