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
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;

namespace FluentGpu.Hosting;

/// <summary>Optional platform-window seam for the input-pacing census the host mirrors onto <see cref="FrameStats"/>
/// (always-on P0 counters, no env switch). Implemented by the Win32 window (its paced <c>WaitForWork</c> keeps the
/// counter); a headless or foreign window simply does not implement it and the host reports 0. Discovered once at
/// construction (<c>window as IInputPacingSource</c>) — no per-frame type test, no allocation.</summary>
public interface IInputPacingSource
{
    /// <summary>Monotonic count of display-paced waits a NON-deferrable message broke early (a frame produced off-phase).
    /// A consumer differences successive frames.</summary>
    long PacedUrgentBreaks { get; }
}

/// <summary>Why a scroll-active frame's raw gap exceeded the work the frame measured (<see cref="FrameStats.SlackMs"/>):
/// a GC landed in the gap, the wake model deliberately slept for it, or the thread was runnable and did not run
/// (OS pre-emption, a blocking call outside the measured phases).</summary>
public enum SlackCause : byte { None, Gc, WakeSlept, Preempted }

public readonly record struct FrameStats(int DrawCommandCount, int ClicksHandled, long HotPhaseAllocBytes, bool Rendered)
{
    public int NodesVisited { get; init; }
    public int DrawNodeCount { get; init; }
    public int CulledNodeCount { get; init; }
    public int BlurCandidateCount { get; init; }
    public int BlurGroupCount { get; init; }
    public int EdgeFadeGroupCount { get; init; }
    public int SpansReused { get; init; }
    public int SpansReRecorded { get; init; }
    /// <summary>The retained-tile slice partition's census for this frame's record pass (the render side's latest imported
    /// feedback under record-on-render): slices walked / kept whole / effect slices / folded / bytes recorded.</summary>
    public FluentGpu.Render.SliceRecordStats Slices { get; init; }
    /// <summary>True when this frame recorded NOTHING — every slice kept whole (only composite parameters moved: a pure
    /// scroll tick, a sticky/parallax/thumb pose). The frame is a composite-only turn.</summary>
    public bool CompositeOnlyTurn { get; init; }
    /// <summary>Why spans were re-recorded instead of reused this frame (always-on reason counts; the render side's latest
    /// imported record feedback under record-on-render).</summary>
    public FluentGpu.Render.SpanReuseMissStats SpanMisses { get; init; }
    public int SpanBytesCopied { get; init; }
    public int NodesCulled { get; init; }
    /// <summary>Subtrees the record walk refused to descend into because the recording thread ran out of stack
    /// (<see cref="SceneRecordStats.DepthAborts"/>). Nonzero ⇒ the presented frame is visibly INCOMPLETE (a page whose
    /// deepest content — track rows, a hero — is missing). Must be 0 in a healthy build; the UI thread's 32 MB stack
    /// (FluentApp.RunCore) is what keeps it there.</summary>
    public int DepthAborts { get; init; }
    public SpanReuseDisabledReason SpanReuseDisabledReasons { get; init; }
    /// <summary>Per-node span-reuse gate refusals, populated only under <c>--fg render</c>.</summary>
    public SpanReuseMissStats SpanReuseMisses { get; init; }
    // Repaint damage (gpu-renderer.md §13.1) — the measure point for §5.1-A: what fraction of the window this frame
    // would have had to repaint, over how many disjoint rects, and (when the region gave up) why. No renderer consumes
    // the region yet, so these are the ONLY way to validate the accumulator against a real workload.
    /// <summary>Fraction of the client area this frame's repaint set covers (0..1; 1 when a full repaint was forced).</summary>
    public float RepaintCoverage { get; init; }
    /// <summary>Disjoint rects in this frame's repaint set (0 when full, and 0 also means "nothing changed").</summary>
    public int RepaintRectCount { get; init; }
    /// <summary><see cref="RepaintFullReason.None"/> unless the frame forced a full repaint.</summary>
    public RepaintFullReason RepaintFullReason { get; init; }
    // Per-frame layout-cost counters (FlexLayout diag). P0 made the underlying FlexLayout fields (_dMeasure/_dArrange/
    // _dTextMiss/etc.) ALWAYS-ON — --fg layout now gates only the Console.Error.WriteLine printout in
    // FlexLayout.Run(), not the counting itself (`gate.diag.counters-always-on`). MeasureCount/ArrangeCount are total
    // node visits across the frame's full + scoped + phase-7 reflow layout passes — MeasureCount counts REAL measures
    // (within-pass memo hits are excluded; FlexLayout.DiagMeasureMemoHits has those); TextShapeMisses is DirectWrite
    // re-shapes (measure-cache misses). A projected (Reveal/FLIP) size animation must keep these ~0 on every anim tick —
    // only the commit frame is large. The reflow-per-tick defect (backdrop-effects-animation §5.8) is exactly a nonzero here.
    public int MeasureCount { get; init; }
    public int ArrangeCount { get; init; }
    public int TextShapeMisses { get; init; }
    /// <summary>P0 always-on counter: true glyph-shape calls this frame (<c>IFontSystem.ShapeCount</c> delta), i.e. real
    /// DirectWrite/headless shaping work — as opposed to <see cref="TextShapeMisses"/> which is the layout measure-cache
    /// miss count (a measure miss does not always re-shape, and a shape can happen outside layout, e.g. span rebind).
    /// An identical second frame must read 0 here (`gate.diag.counters-always-on`).</summary>
    public int TextShapes { get; init; }
    /// <summary>P0 always-on counter: nodes copied by this frame's <c>SceneRecordingSnapshot.Capture</c> (0 on a frame
    /// that did not record, e.g. skip-submit). P8's incremental capture is expected to shrink this on coast frames.</summary>
    public int CapturedNodes { get; init; }
    /// <summary>Always-on submit breakdown (async / record-on-render path; 0 elsewhere): the draw-list hash, the
    /// <c>PublishScene</c> capture, and the tail (ledger clears + render-thread wake) — the three pieces of
    /// <see cref="SubmitMs"/> on the UI thread, so a slow `submit` names its own cause.</summary>
    public double SubmitHashMs { get; init; }
    /// <inheritdoc cref="SubmitHashMs"/>
    public double SubmitCaptureMs { get; init; }
    /// <inheritdoc cref="SubmitHashMs"/>
    public double SubmitTailMs { get; init; }
    /// <summary>Whether this frame's <c>PublishScene</c> capture took the incremental (changed-nodes-only) path.</summary>
    public bool CaptureIncremental { get; init; }
    /// <summary>The capture's own split (ms): scene snapshot, recording config + strings, image-cache snapshot,
    /// compositor animations — which step a slow <see cref="SubmitCaptureMs"/> spent its time in.</summary>
    public double CaptureSceneMs { get; init; }
    /// <summary>The render thread's last present-latency wait on the main window (ms) — the vsync-bound half of
    /// <see cref="FenceWaitMs"/>, read from the swapchain's latest present statistics (cross-thread, last writer).</summary>
    public double LatencyWaitMs { get; init; }
    /// <summary>DWM's own deltas for the main window (frames it dropped, missed and presented late — the OS-attested
    /// counterpart of <see cref="MissedVsyncs"/>), carried ONLY by the first frame that observes a new 1 Hz DWM sample
    /// (<see cref="DwmSampleSeq"/> changed) and 0 on every other frame, so summing them per frame counts each sample
    /// once. 0 when unavailable.</summary>
    public uint DwmDropped { get; init; }
    /// <summary>Identity of the latest DWM timing sample (<c>PresentStats.DwmSampleSeq</c>; 0 = none yet).</summary>
    public uint DwmSampleSeq { get; init; }
    /// <summary>Render-thread pacing evidence, CUMULATIVE since the host started (a reader diffs two frames): fresh
    /// presents, motion re-presents, race hits (a fresh publication deferred to the next compositor tick because a
    /// motion re-present had already taken this one), and slot waits over 4 ms by kind. See RenderThread's counters.</summary>
    public long RenderFreshPresents { get; init; }
    /// <inheritdoc cref="RenderFreshPresents"/>
    public long RenderMotionPresents { get; init; }
    /// <inheritdoc cref="RenderFreshPresents"/>
    public long RenderRaceHits { get; init; }
    /// <summary>Clock-paced render turns whose present slot was still busy past the grace (the previous present missed its
    /// vblank and owned this one) and that presented nothing so the next tick presented on time — CUMULATIVE. Each is
    /// also one <see cref="RenderMissedMotionTicks"/>. See RenderThread.CatchUpSkips / SlotCatchUp.</summary>
    public long RenderCatchUpSkips { get; init; }
    /// <inheritdoc cref="RenderFreshPresents"/>
    public long RenderFreshLongWaits { get; init; }
    /// <inheritdoc cref="RenderFreshPresents"/>
    public long RenderMotionLongWaits { get; init; }
    /// <summary>Compositor ticks that passed with motion live but no present for them (CUMULATIVE) — the pacing cliff's
    /// own counter (RenderThread.MissedMotionTicks); 0 while every tick is presented.</summary>
    public long RenderMissedMotionTicks { get; init; }
    /// <summary>Render-thread turns that presented nothing because the compositor tick had already been presented for
    /// (the pending publication or motion waited for the next tick) — CUMULATIVE. See RenderThread.SkippedTicks.</summary>
    public long RenderSkippedTicks { get; init; }
    /// <summary>OS-attested present statistics for the main swapchain, CUMULATIVE (a reader diffs two frames): presents
    /// that reached a vblank, presents superseded before display, and vblanks that showed a stale frame although its
    /// successor had been submitted in time (<see cref="FluentGpu.Rhi.ISwapchain.PresentsDisplayed"/> and siblings — the
    /// <c>PresentStatisticsLedger</c>, DXGI's paired counters only). Idle vblanks and producer-skipped ticks are not
    /// repeats: the latter are <see cref="RenderMissedMotionTicks"/>.</summary>
    public long PresentsDisplayed { get; init; }
    /// <inheritdoc cref="PresentsDisplayed"/>
    public long PresentsDropped { get; init; }
    /// <inheritdoc cref="PresentsDisplayed"/>
    public long VblanksRepeated { get; init; }
    /// <inheritdoc cref="DwmDropped"/>
    public uint DwmMissed { get; init; }
    /// <inheritdoc cref="DwmDropped"/>
    public uint DwmLate { get; init; }
    /// <inheritdoc cref="CaptureSceneMs"/>
    public double CaptureConfigMs { get; init; }
    /// <inheritdoc cref="CaptureSceneMs"/>
    public double CaptureImagesMs { get; init; }
    /// <inheritdoc cref="CaptureSceneMs"/>
    public double CaptureAnimMs { get; init; }
    /// <summary>P0 always-on counter: sum of <c>Reconciler.NodeBindingFireCount</c> this frame — every bound-channel
    /// effect PROLOGUE (whether or not it wrote a column). Compare against <see cref="BindingWrites"/> to see the
    /// equality-gating hit rate.</summary>
    public int BindingFires { get; init; }
    /// <summary>P0 always-on counter: sum of <c>Reconciler.NodeBindingWriteCount</c> this frame — bound-channel effects
    /// that actually wrote a scene column (an equal republish fires without writing).</summary>
    public int BindingWrites { get; init; }
    /// <summary>P0 counter, valid only when process allocation tracking is on (same probe gate as <see cref="HotPhaseAllocBytes"/>,
    /// <c>-p:EventSourceSupport=true</c>): GC-delta bytes across this frame's <c>FlushRebindsToQuiescence</c> call(s) — the
    /// rebind-only allocation cost, isolated from layout/record/submit.</summary>
    public long RebindFlushAllocBytes { get; init; }
    /// <summary>LayoutDirty marks this frame's scoped relayout consumed (ALWAYS-ON, unlike Measure/ArrangeCount). 0 means
    /// no layout ran at all — the oracle for "a re-render whose tree is unchanged must not dirty layout". Stays 0 on a
    /// full-layout frame (that path does not go through the invalidator).</summary>
    public int ScopedRelayoutMarks { get; init; }
    // Relayout-escape diagnostic (ALWAYS-ON, incl. Release): the number of dirty nodes this frame whose scoped-relayout
    // search (LayoutInvalidator.FindRelayoutRoot) walked a node at depth > 1 ALL the way to the scene root — i.e. found no
    // layout boundary, forcing a full-subtree relayout from the top. A sustained nonzero value during interaction means a
    // hot subtree is missing a fixed-size ClipToBounds boundary (or a `.Boundary()`); set --fg diag to log the offending node.
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
    // Stopwatch reads/frame, zero alloc — so a profiler/probe can attribute a frame-time spike to a phase without --fg alloc.
    public double FlushMs { get; init; }
    /// <summary>Of <see cref="FlushMs"/>: wall time inside <c>_runtime.Flush()</c> (render-effects + bindings), including the
    /// same-frame second flush after pre-layout virtual realize. Always-on Stopwatch; 0 when nothing flushed.</summary>
    public double ReactiveFlushMs { get; init; }
    /// <summary>ALWAYS-ON: reactive units (computations) this frame's flushes ran — every flush runs to quiescence.</summary>
    public int ReactiveUnits { get; init; }
    /// <summary>ALWAYS-ON: the longest single reactive unit this frame (ms) — a component render / effect / binding with
    /// its synchronous memo pulls and reconciliation. A unit longer than the frame period is also reported by the
    /// always-on <c>[signals.slow-unit]</c> line (<see cref="SlowReactiveUnits"/>), naming its owner: fix it at its source.</summary>
    public double ReactiveLongestUnitMs { get; init; }
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
    /// <summary>Renderer-owned recording duration from the latest imported successful presentation (not current UI work).</summary>
    public double RenderRecordMs { get; init; }
    /// <summary>Longest UI-thread wait inside the render thread's park rendezvous (<c>RenderThread.Quiesce</c>: window resize,
    /// pop-out create / close, one-shot GPU work) so far, in ms; 0 without a render thread. The cost the UI pays for
    /// the render thread reaching its gate - above a refresh it is a UI hitch, and the interruptible present-slot wait is
    /// what keeps it out of the 1 s range (a 1 s present-slot wait used to be waited out whole).</summary>
    public double QuiesceWaitMsMax { get; init; }
    /// <summary>Scene publication associated with renderer recording counters; zero before the first feedback sample.</summary>
    public ulong RecordedSceneSequence { get; init; }
    public double SubmitMs { get; init; }
    // RecordMs sub-split (hitch attribution): the phase-7.5 image pump/tick and the phase-7.6 scroll re-realize
    // catch-up both run between tAnim and tRecord, so their cost was invisibly charged to "record" — a realize spike
    // on a fast fling read as SceneRecorder cost. RecordMs still covers the whole segment; these carve it up.
    public double ImagePumpMs { get; init; }
    public int ImageApplyCount { get; init; }
    public int ImageApplyBytes { get; init; }
    /// <summary>P0 always-on counter (cumulative): render-thread image-upload drain turns that hit the device's per-turn
    /// byte budget and carried a job to the next turn (<see cref="IGpuDevice.DeferredImageUploads"/>; 0 on headless).
    /// Difference successive frames: a nonzero step is an applied cover whose texture landed one present later than its
    /// admission, and a sustained climb during a scroll means covers land faster than one turn stages them.</summary>
    public int DeferredImageUploads { get; init; }
    /// <summary>Cumulative pixel bytes of the jobs <see cref="DeferredImageUploads"/> counted
    /// (<see cref="IGpuDevice.DeferredImageUploadBytes"/>).</summary>
    public long DeferredImageUploadBytes { get; init; }
    public double RealizeCatchupMs { get; init; }
    // Submit sub-split (diagnostics for the #1 hotspot; async runs the backend submit on the render thread).
    // FenceWaitMs = submitting-thread wall-time BLOCKED on back-buffer retirement + present-latency waitable
    // INSIDE SubmitDrawList; PresentMs = the Present() call. cmdBuild = SubmitMs − FenceWaitMs − PresentMs is the real CPU
    // command-build cost. Lets a probe attribute a 27 ms "submit" spike to the stall vs the build without an external profiler.
    public double FenceWaitMs { get; init; }
    public double PresentMs { get; init; }
    /// <summary>Most recent true on-GPU whole-frame execution span (always-on timestamp pair; 0 when unsupported or no
    /// sample has retired yet).</summary>
    public double GpuRenderMs { get; init; }
    /// <summary>ALWAYS-ON per-frame render census (plain counters, compiled into every build): repaint damage, span reuse
    /// and misses, the capture's path + full reason + cost, record time, and the device's latest retired per-submit
    /// counters (route actually taken, edge fades + strip px, group RTs, pass breaks, draws, glyph instances, uploads,
    /// baked blurs, back-buffer transitions). The pass-granular GPU timeline is <see cref="AppHost.CopyGpuPassTimeline"/>.</summary>
    public RenderFrameCensus RenderCensus { get; init; }
    // This frame actually submitted + presented (skip-submit did NOT elide it). A probe uses it to see how often a
    // "static" scene is force-presented anyway (a sustained loop animation marking TransformDirty defeats skip-submit).
    // NOTE this is `!skipSubmit`, decided BEFORE the render thread does anything — it means "published, not elided",
    // never "photons reached the panel". Cadence verdicts must come from present stamps, not from this bit.
    public bool Presented { get; init; }
    /// <summary>Any viewport was in user-driven motion this frame or the last (<see cref="AppHost.AnyUserScrollMoving"/>) —
    /// the generic per-frame bit a diagnostic consumer normalises cadence metrics by. Motion detail lives in the
    /// scroll probe (<c>FluentGpu.Scroll.Diag.ScrollProbe</c> / <c>BurstSummary</c>), not here.</summary>
    public bool ScrollActive { get; init; }
    /// <summary>The publish seq this frame's DrawList was handed to the render seam under (0 when the frame elided its
    /// submit). This is the ONLY per-frame identity that survives the UI→render-thread boundary; see
    /// <see cref="AppHost.LastPresentPublishSeq"/> for the ack side and the join contract.</summary>
    public ulong PublishSeq { get; init; }
    // Hitch attribution: GC collection deltas since the previous painted frame (ALWAYS-ON — a slow-frame log line
    // needs "a gen-2 landed here" without a flag).
    public int Gc0Delta { get; init; }
    public int Gc1Delta { get; init; }
    public int Gc2Delta { get; init; }
    /// <summary>Always-on gap discriminator for scroll-active frames whose raw inter-frame gap exceeded 12 ms: the part
    /// of that gap no measured phase (flush/layout/anim/record/submit) accounts for, in ms — the time the loop was not
    /// running. 0 on every other frame.</summary>
    public float SlackMs { get; init; }
    /// <summary>What <see cref="SlackMs"/> was (<c>SlackCause.None</c> when the slack is under 4 ms — the frame was
    /// slow, not absent).</summary>
    public SlackCause SlackCause { get; init; }
    /// <summary>The measured decomposition of this frame's UI gap (previous Paint end → this Paint start) — where the
    /// slack went: the loop's wait vs what it requested, message dispatch, input, posts, cold work, the thread's own
    /// running time from its cycle counter, and the verdict (<see cref="UiGapClassifier"/>). Filled on a slack frame
    /// (<see cref="SlackCause"/> not None), default otherwise.</summary>
    public UiGapReport UiGap { get; init; }
    /// <summary>The per-component census line for this frame when <see cref="AppHost.RenderCensus"/> is on AND the
    /// flush exceeded <see cref="RefreshIntervalMs"/> (or rendered ≥ the census min-comps): "[render-census] flush=…
    /// top=Type×n(r=ms c=ms a=K),… bytes=Type×n=K,…" — WHICH components made the frame slow and WHO allocated. Null
    /// on every in-budget frame and when the census is off.</summary>
    public string? Census { get; init; }
    /// <summary>The panel's refresh interval (ms) as the host paced this frame — 8.33 at 120 Hz, 16.67 at 60 Hz. The
    /// frame budget every "slow frame" judgement is made against; a consumer never hard-codes 16.</summary>
    public double RefreshIntervalMs { get; init; }
    /// <summary>Monotonic count of scene publications the render thread never adopted (a newer publication superseded
    /// them before it woke). Ordinary and cheap — record-dirty bits and the pending-removal ledger accumulate until a
    /// publication is CONSUMED, so a skipped one loses nothing. A sustained climb means the render thread is behind.</summary>
    public long PublicationGaps { get; init; }
    /// <summary>P0 always-on counter (monotonic): display-paced input waits the platform broke early for a non-deferrable
    /// message, i.e. frames produced OFF-phase by urgent input (<see cref="IInputPacingSource.PacedUrgentBreaks"/>; 0 when
    /// the window does not implement the seam — headless). Wheel is URGENT by design (a notch authors its plan during
    /// message dispatch), so a wheel glide legitimately raises this.</summary>
    public long PacedUrgentBreaks { get; init; }
    /// <summary>P0 always-on counter (monotonic): <c>RunFrame</c>s that dispatched input but declined to produce a frame
    /// because one was already produced for the current compositor tick (<see cref="AppHost.ProductionDeclines"/>). Each
    /// one is an input update that reached the screen a tick late; a wheel glide must hold the difference at ~0.</summary>
    public long ProductionDeclines { get; init; }
    /// <summary>Cumulative presents and OS-refresh slots those presents missed (an interval of two vblanks counts one
    /// missed slot). Cumulative so a window can difference them: the displayed cadence is the smoothness number, and
    /// it is invisible to the UI-side phase times when the renderer runs on its own thread.</summary>
    public long PresentedFrames { get; init; }
    public long MissedVsyncs { get; init; }
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
    /// <summary>The popup's swapchain, or null while it does not exist. Under a render thread the swapchain is created (and
    /// later disposed) BY that thread through the host's popup mailbox, so a freshly leased slot reads null until the create
    /// turn lands; the recorder skips a null slot and resolves the swapchain at record time, never at capture time.</summary>
    public ISwapchain? Swapchain { get => Volatile.Read(ref _swapchain); internal set => Volatile.Write(ref _swapchain, value); }
    private ISwapchain? _swapchain;
    /// <summary>The render thread could not create this popup's swapchain (it logged why). The UI drops the slot.</summary>
    internal volatile bool CreateFailed;
    /// <summary>The UI closed this popup: a create still queued for it is skipped.</summary>
    internal volatile bool Retired;
    private int _windowDisposed;
    /// <summary>Dispose the PAL window exactly once, from whichever path gets there first (the UI's close, the post the render
    /// thread sends once the swapchain is gone, the host's teardown sweep). UI thread only: the HWND belongs to it.</summary>
    internal void DisposeWindow()
    {
        if (Interlocked.Exchange(ref _windowDisposed, 1) == 0) Window.Dispose();
    }
    /// <summary>The popup's own command stream, re-recorded each frame via <c>SceneRecorder.RecordSubtree</c>.</summary>
    public DrawList DrawList { get; } = new();
    internal SceneRecordingContext Recording { get; } = new();
    internal ulong FirstSceneSequence;
    /// <summary>This popup's cost, for the always-on <c>[overlay.popup]</c> line written at close.</summary>
    internal PopupLifecycle Lifecycle { get; } = new();
}

/// <summary>Which branch of <see cref="AppHost.RecommendedWaitMs"/> produced the last wait — the diagnostic that
/// distinguishes per-source cadence pacing, measured adaptive-GPU pacing, and display-rate free-run. <c>Cadence</c>
/// means the wait was shaped by the earliest DUE animation/caret row's own declared cadence
/// (<c>AnimEngine.NextDueMs</c> / <c>CaretBlinker.NextDueMs</c>), quantized onto the panel's refresh by
/// <see cref="CadencePacing"/>; <c>AdaptiveGpu</c> means a fresh on-GPU execution sample selected a sustainable
/// cadence instead; <c>DisplayRate</c>/<c>DisplayTick</c> mean the loop ran at panel rate and any lock is downstream.
/// Surfaced via <see cref="AppHost.LastWaitKind"/>.</summary>
public enum HostWaitKind : byte
{
    Idle,            // -1: fully idle / minimized — block until a message
    Hud,             // 100: DynamicText-only readout throttle
    Baked,           // baked-blur queue cadence
    Cadence,         // CadencePacing over the earliest not-yet-due row's own cadence (replaced the inferred ambient fps cap)
    DisplayTick,     // async: production paced on the compositor tick (one frame per vblank); the timeout is a backstop
    SoftwarePace,    // async without a display clock: wall-clock pace at the refresh period (60 Hz floor when unknown)
    DisplayRate,     // 0: sync render path — present-throttled (panel rate)
    AdaptiveGpu,     // CadencePacing at the governor's fixed 30 fps, selected by fresh on-GPU execution samples
    PowerCap,        // CadencePacing at the app's PowerCapFps ceiling (the OS asked for less work); never interaction
}

public sealed partial class AppHost : IDisposable
{
    private readonly IPlatformApp _app;
    private readonly IPlatformWindow _window;
    private readonly IGpuDevice _device;
    // The present-queue depth (DXGI SetMaximumFrameLatency) IN FORCE. Chosen on the render thread from measured GPU
    // margin (PresentQueueDepthPolicy — depth 2 only while GPU execution approaches the refresh) and read by both threads
    // (Volatile): it feeds the frame clock's present prediction — RefreshLattice.Build stamps PresentQpc = FrameQpc +
    // (1 + this)·refresh and RenderPresentSec does the same per render tick — so plans and animations are evaluated at
    // the vblank the frame is actually composited at under the queue depth actually used. Headless keeps 1.
    private int _maxFrameLatency;
    private Threading.PresentQueueDepthPolicy _depthPolicy;   // render thread only
    private readonly ISwapchain _swapchain;
    private readonly Component _root;
    private readonly StringTable _strings;
    private readonly IFontSystem _fonts;   // retained so a detached child host (pop-out video window) can be constructed with the same font system
    private readonly FluentGpu.Media.VideoSurfaceRegistry _videoSurfaces = new();   // UI-thread video-surface intents; a snapshot rides every publication (F070)
    // The render thread's sole consumer of video placement (F070 / F183): applies the PUBLISHED snapshot to the IVideoPresenter in the turn
    // that presents it. Render-thread-only; created lazily by that thread. The single-thread host (no render thread) drains through the
    // registry's same-thread shim instead.
    private FluentGpu.Media.VideoPlacementApplier? _videoApplier;
    private FluentGpu.Media.VideoPlacementApplier VideoApplier => _videoApplier ??= new FluentGpu.Media.VideoPlacementApplier(_videoSurfaces);

    // Detached child hosts (the pop-out video mini-player): each is a full AppHost over its OWN top-level window +
    // composited swapchain + presenter, sharing this device/fonts/strings/images. Ticked by the loop via
    // TickDetachedHosts() on THIS (the parent's) UI+render thread. Empty on child hosts (no recursion).
    private readonly List<AppHost> _detachedHosts = new(1);
    // Render-thread-visible copy of the live detached children (parent host only). The parent's ONE render thread iterates
    // this to drain each child's seam on its own present turn (DrainChildRenderSources). A copy-on-write snapshot: the UI
    // thread (sole writer) publishes a whole new array with Volatile.Write and the render thread takes ONE Volatile.Read per
    // use, so it never sees a torn structural change and an attach never parks the loop. A DETACH still parks (the child's
    // swapchain is released right after, which the render thread may be presenting). Distinct from _detachedHosts (which the
    // UI thread mutates freely for its own reaping).
    private AppHost[] _childRenderSources = [];
    private bool _isDetachedChild;   // true on a child host: it must not dispose the shared device, nor manage its own detached windows
    // On a detached CHILD host: the parent that adopted it (AdoptDetachedChild). The modal-loop peer tick (F093) walks from any
    // window to the root so ONE throttle stamp and one peer list serve the whole process. Null on the primary host.
    private AppHost? _parentHost;
    // Root host only: the last allowed modal-loop peer tick (Environment.TickCount64; ModalPaintThrottle.ShouldSkipPeerTick),
    // and how many peer paints those ticks have run (tests + diagnostics).
    private long _modalPeerLastMs;
    private long _modalPeerPaints;
    // On a detached CHILD host under a threaded parent (async or force-sync): the PARENT's render thread. The child spawns
    // NO render thread of its own (that would be a second submit/present owner racing the shared, render-confined device);
    // instead its RunFrame PUBLISHes to its own seam and WAKES this parent thread, which drains the child's seam + presents
    // the child's swapchain render-confined. Null on the primary host and on a child under a pure single-thread parent.
    private readonly Threading.RenderThread? _parentRenderThread;
    private bool _closedShutdownDone;   // guards the once-only on-close render-thread teardown (RunFrame close gate + Dispose)
    // On a detached CHILD host: the closed-callback the DetachedWindowHandle exposes, fired exactly once by the parent's
    // reaper (TickDetachedHosts) just before Dispose(). _onClosedFired guards against any double-fire.
    internal Action? OnClosed;
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
    // Popups closed while a render thread existed: out of _popupWindows already, but their swapchain is still owned by the
    // render thread until it runs the queued Dispose, and their HWND is destroyed only after (UI thread). UI-thread only.
    private readonly List<PopupWindowSlot> _retiringPopups = new(2);
    private readonly List<NodeHandle> _popupSkipRoots = new(2);
    private readonly List<NodeHandle> _reuseBlockRoots = new(4);   // W5: connected-anim fly anchors whose span-reuse ancestor chains the recorder blocks (spatial scoping)
    private int _popupTokenSeq;

    // Item E: constructed in the ctor body (not here) so it can honour initialSceneCapacity — see the ctor.
    private readonly SceneStore _scene;
    private readonly ReactiveRuntime _runtime = new();
    private readonly TreeReconciler _reconciler;
    private readonly FlexLayout _layout;
    private readonly LayoutInvalidator _invalidator;
    private readonly HashSet<NodeHandle> _followAnchors = new();   // F169 scratch: the follow pass's anchor chains, handed to _anim each frame
    private readonly DrawList _drawList = new();
    private readonly SpanTable _spanTable = new();
    private bool _imageCrossfadeWasActive;
    // Render-thread seam (Cut A, submit-only; docs/plans/render-thread-seam-landing-plan.md · design/subsystems/threading-render-seam.md).
    // STEP 1 — single-thread pass-through: the UI records into _drawList, copies it into a render-readable arena, then
    // PUBLISHes + ACQUIREs it on THIS (UI) thread and submits from the acquired arena — byte-identical to a direct
    // submit, no behaviour/perf change. This only establishes the seam SHAPE so the later (soak-gated) render-thread
    // spawn — which moves submit/present/the GPU fence-wait stall off the UI thread — is an additive change, not a rewrite.
    private readonly Threading.SceneFramePublisher _renderSeam = new();
    private readonly Threading.SceneFramePublisher _recordFeedback = new(
        cmdCap: System.Runtime.CompilerServices.Unsafe.SizeOf<Threading.RecordingFeedback>(), sortCap: 1, reverse: true);
    private readonly DrawList _renderCommands = new();
    private readonly SpanTable _renderSpans = new();
    // Retained tiles (docs/plans/scroll-gpu-retained-tiles-implementation.md, P1): the render thread's slice arenas (paired
    // with _renderSpans) and its tile bookkeeping; the UI-thread (inline) pair below is the same model for SingleThread.
    private readonly SliceRecorder _renderSlices = new();
    // NULL on a detached child: it presents through its OWN swapchain's direct route (SubmitDrawList), never composites, so it
    // owns no retained-tile table - and a stray child composite turn fails fast instead of numbering surfaces into the
    // device-wide tile pool the primary window also uses (F229). Assigned once in the constructor.
    private readonly FluentGpu.Render.Tiles.SliceTable? _renderTiles;
    private readonly SliceRecorder _uiSlices = new();
    private readonly FluentGpu.Render.Tiles.SliceTable _uiTiles = new(SliceTableCap, SliceTileCap, SliceSurfaceCap);
    // P1 sizes: every slice SEGMENT is a SliceTable row (a static root split around each child slice, a scroll slice
    // around a sticky header…), so the table is sized for the segment count a real page reaches, not the design's
    // composited-slice default of 16 — P2 pins the real budget with the surfaces.
    private const int SliceTableCap = 256, SliceTileCap = 64, SliceSurfaceCap = 64;

    /// <summary>The UI-thread (SingleThread / headless) slice recorder and tile table — gates read the partition and the
    /// per-turn invalidation census off these.</summary>
    internal SliceRecorder UiSlices => _uiSlices;
    internal FluentGpu.Render.Tiles.SliceTable UiSliceTable => _uiTiles;
    private ulong _lastRecordedScene;
    // UI-thread: the scene publish seq handed to the seam on the PREVIOUS painted frame. The consume gate for clearing
    // record-dirty bits + the pending-removal ledger (see the publish site) — bits are dropped only once the renderer
    // has adopted the publication that carried them, so a skipped publication is a plain no-op.
    private ulong _lastPublishedSceneSeq;
    // Render-thread: publications the consumer never adopted (superseded before it woke). DIAGNOSTIC ONLY — it no longer
    // disables span reuse or forces root-bounds damage; the accumulate-until-consumed rule above makes a gap ordinary.
    private long _publicationGaps;
    // Render-thread private: FNV-1a of the last stream this thread actually submitted+presented, and the target epoch it
    // presented under. A byte-identical stream with an EMPTY repaint region and no clock-driven work is already on screen.
    // Needs no external invalidation reset: every event that would poison it (resize, DPI, device recovery, clear-color
    // change, image content, crossfades) reaches this thread as a non-empty repaint region or a new target epoch.
    private ulong _lastRenderPresentedHash;
    private Threading.RenderSubmissionContinuity _renderSubmissionContinuity;
    private long _lastRenderedTargetEpoch = -1;
    // UI→render mirror of _popupWindows.Count. The render thread must never present a skip candidate while a popup
    // window rides the same turn (RecordPopups submits its own swapchain from the same publication).
    private int _popupWindowCount;
    private SceneRecordStats _lastRecordStats;
    private int _lastRecordedCommandCount;
    private double _lastRenderRecordMs;
    private ulong _lastRecordedFeedbackScene;
    private readonly RenderCompositorAnimations _renderAnimations = new();
    private Threading.RenderFrame _activeRenderFrame;
    private bool _hasActiveRenderFrame;
    private byte[] _feedbackBytes = [];
    private int _lastSettledPoseCount;
    private int _renderVisible = 1;
    // UI→render mirror of the window's OCCLUSION, written once per RunFrame by PublishRenderMotionPolicy (UI thread):
    // _renderOccluded = the primary swapchain reports occluded (covered by another window - a pop-out over the main window -
    // or cloaked): render motion PARKS exactly like a minimized window (HasOwnRenderMotion), and the UI's occlusion probe
    // still re-presents once per OcclusionProbeIntervalMs so the un-occlusion is heard. Occlusion is the ONE window state that
    // slows render motion: a visible window keeps the display rate whether or not it is the active one (motion policy, 2026-10-03).
    private int _renderOccluded;
    private long _renderPeriodTicks = Stopwatch.Frequency / 60;
    private bool _renderWasPaused;
    private int _renderMotionActive; // renderer-written diagnostic mirror; never inspect render-owned state from UI
    private ulong _wakeCensusPresented;
    private long _wakeCensusNoop, _wakeCensusWarm, _wakeCensusFresh;
    // Render thread writes: fresh publications whose record came out byte-identical to the presented stream (elided submit).
    private long _freshIdentical;

    /// <summary>Whether this host's render motion may run at all: on screen, not latched <see cref="_renderFailed"/>, and with a
    /// retained frame still valid for the current target to re-present. A child that latched RenderFailed has NO motion to run:
    /// a re-present would submit the same failing frame on the shared command queue and throw again every turn, and its live
    /// rows would keep the parent loop at the display tick (F109).</summary>
    internal static bool RenderMotionMayRun(bool paused, bool renderFailed, bool hasCurrentFrame)
        => !paused && !renderFailed && hasCurrentFrame;

    private bool HasOwnRenderMotion()
    {
        // Parked (minimized / hidden / cloaked: _renderVisible) OR occluded (_renderOccluded): nothing of this window is on
        // screen, so its compositor motion is paused in place - the same Pause/Resume the minimize edge makes, which re-anchors
        // every row on resume so a loop continues from where it stopped instead of jumping.
        bool paused = Volatile.Read(ref _renderVisible) == 0 || Volatile.Read(ref _renderOccluded) != 0;
        if (paused != _renderWasPaused)
        {
            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (paused)
            {
                _renderAnimations.Pause(now);
            }
            else
            {
                _renderAnimations.Resume(now);
            }
            _renderWasPaused = paused;
        }
        bool active = false;
        if (RenderMotionMayRun(paused, _renderFailed, _hasActiveRenderFrame && _renderSeam.IsCurrentTarget(_activeRenderFrame)))
        {
            var frame = _renderSeam.Scene(_activeRenderFrame);
            active = _renderAnimations.HasActive || _renderPoser.HasActive || frame.Images.HasCrossfades(RenderImageClock(_activeRenderFrame, frame))
                || _device.HasLiveFeedback;   // a feedback trail (F6) advances with no scene change while it settles
        }
        Volatile.Write(ref _renderMotionActive, active ? 1 : 0);
        return active;
    }

    // F242: why each render-thread submit ran (fresh publication / compositor animation / scroll pose / crossfade) and whether it
    // RECORDED or only re-composed retained slices. Render thread writes (one Note per submit), the UI-thread [wake] census reads.
    private readonly RenderPresentCensus _renderPresentCensus = new();

    private void AppendRenderWakeCensus(System.Text.StringBuilder sb)
    {
        ulong presented = PresentedSequence;
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $" | renderMotion={Volatile.Read(ref _renderMotionActive)} presents={presented - _wakeCensusPresented} feedbackPending={(_recordFeedback.HasPendingFrame ? 1 : 0)}");
        _wakeCensusPresented = presented;
        // The redundant-frame evidence, per window: UI frames that elided a no-op publication (noopSkips), turns the warm
        // cadence alone woke and that ran idle (warmIdle), and publications the render thread still adopted, recorded and
        // found byte-identical to what it had presented (freshIdentical: what the UI-side skip could not prove unchanged).
        long noop = _noopPublications.Elided, warm = _warmCadenceIdleTurns, fresh = Interlocked.Read(ref _freshIdentical);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $" noopSkips={noop - _wakeCensusNoop} warmIdle={warm - _wakeCensusWarm} freshIdentical={fresh - _wakeCensusFresh}");
        _wakeCensusNoop = noop; _wakeCensusWarm = warm; _wakeCensusFresh = fresh;
        _noopPublications.AppendBlockedWindow(sb);
        _renderPresentCensus.AppendWindow(sb);
    }

    private float RenderImageClock(in Threading.RenderFrame frame, Threading.SceneRenderFrame scene)
        => ImagePresentationClock.Extrapolate(frame.Submit.ImageClockMs,
            double.IsNaN(scene.Images.ClockCapturedAtMs) ? scene.Animations.CapturedAtMs : scene.Images.ClockCapturedAtMs,
            Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    /// <summary>F108: whether THIS host drains the <see cref="ImageCache"/> (<c>Pump</c>, the presentation clock, <c>Tick</c>, the
    /// content-changed clear). A cache is pumped by exactly one host per loop iteration:
    /// the primary window pumps the cache it shares with its pop-outs on every <c>RunFrame</c>, so a detached child that
    /// pumped it too applied the decodes the parent had not yet reached (doubling the UI-side apply work on exactly the turns the
    /// main window is busiest) and woke itself for completions that were not its own. A child pumps only when its parent cannot:
    /// parked (minimized / hidden / cloaked / covered by a fullscreen pop-out: <c>RunFrame</c> returns before any pump),
    /// where the pop-out's own artwork would otherwise never decode. A child with a cache of its own (no parent, or a different
    /// cache) pumps it, as does every primary host.</summary>
    private bool PumpsSharedImages
        => !_isDetachedChild || _parentHost is not { } parent || !ReferenceEquals(parent._images, _images) || parent.IsParked;

    private void AdvanceImagePresentationClock()
    {
        if (_frameTime is not StopwatchFrameTimeSource || !PumpsSharedImages) return;
        double nowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        _images.AdvancePresentationClock(nowMs);
    }

    /// <summary>T10: milliseconds until the image cache's pinned canceled-leftover sweep has work
    /// (<see cref="ImageCache.LeftoverRetryDueMs"/>): +∞ when nothing is listed (the common case — one float read, no
    /// clock read), ≤ 0 when due now. The due time is on the IMAGE clock, which only advances when
    /// <see cref="AdvanceImagePresentationClock"/> samples it; an idle loop has not sampled it since its last turn, so the
    /// wall time elapsed since that sample is extrapolated here exactly as the render thread does
    /// (<see cref="ImagePresentationClock.Extrapolate"/>). Fixed/manual headless time is not wall time: there the image
    /// clock only moves by <c>ImageCache.Tick</c>, so it is read as-is. Allocation-free.</summary>
    private double ImageLeftoverDueInMs()
    {
        float due = _images.LeftoverRetryDueMs;
        if (float.IsPositiveInfinity(due)) return double.PositiveInfinity;
        double clock = _images.ClockMs;
        double sampledAt = _images.ClockCapturedAtMs;
        if (_frameTime is StopwatchFrameTimeSource && !double.IsNaN(sampledAt))
            clock += Math.Max(0.0, Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency - sampledAt);
        return due - clock;
    }

    /// <summary>T10: shorten an IDLE/throttled wait so the loop wakes when a pinned canceled leftover falls due — the
    /// <see cref="ClampWaitToTimers"/> contract applied to <see cref="ImageLeftoverDueInMs"/>: a display-rate wait is
    /// left alone (its next frame pumps anyway), a parked host keeps its block (Paint, which pumps, is gated off), and
    /// the clamp floors at 1 ms (an already-due leftover is the <see cref="WakeReasons.ImageLeftoverDue"/> bit's job —
    /// it takes the due-now branch — never a 0 ms poll). Nothing listed ⇒ +∞ ⇒ the wait is unchanged (a fully idle
    /// loop stays -1).</summary>
    private int ClampWaitToImageLeftovers(int w, HostWaitKind kind)
    {
        if (IsDisplayRateWait(kind, w) || IsParked || !PumpsSharedImages) return w;
        double dueInMs = ImageLeftoverDueInMs();
        if (double.IsPositiveInfinity(dueInMs)) return w;
        int dueIn = dueInMs >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(Math.Max(1.0, dueInMs));
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    /// <summary>E1 (design-engine-images.md): describe every currently-revealing image as its owning node(s)' repaint
    /// rect instead of the caller forcing a full frame for <c>HasActiveCrossfades</c>. Returns false — leaving
    /// <paramref name="repaint"/> untouched beyond whatever it already had — the moment the crossfade set cannot be
    /// fully described: <see cref="ImageCache.RevealingOverflow"/> (more than 128 concurrent reveals), a node under a
    /// scaled/rotated ancestor (<see cref="TreeReconciler.AddImageNodeRepaint"/> returns -1), or a live image inside the
    /// detached-fly slab (<c>ConnectedAnimation.DetachedFly</c>) — those nodes are snapshot rows, not scene nodes, so they are never in
    /// <c>_imageNodes</c> and would otherwise silently go undescribed. The caller then keeps its named
    /// <see cref="RepaintFullReason.DetachedContent"/> surrender for this frame.</summary>
    private bool AddCrossfadeRepaint(ref RepaintDamageRegion repaint)
    {
        if (_images.RevealingOverflow) return false;
        if (DetachedSlabHasLiveImages()) return false;
        foreach (int id in _images.RevealingIds)
        {
            if (_reconciler.AddImageNodeRepaint(id, ref repaint) < 0) return false;
        }
        return true;
    }

    /// <summary>True when the detached-fly slab (<see cref="ConnectedAnimation.Detached"/>, <c>ConnectedAnimation.DetachedFly</c>)
    /// currently holds a live snapshot carrying an image. Those rows are not scene nodes — <c>Reconciler._imageNodes</c>
    /// never sees them — so a crossfade landing on one of them cannot be described as a per-node rect; the caller keeps
    /// its named full for the frame instead. The slab holds only the handful of nodes presently exiting/flying
    /// (typically 0-3), so this scan is O(live count), not O(capacity).</summary>
    private bool DetachedSlabHasLiveImages()
    {
        var detached = _connected.Detached;
        if (!detached.HasActive) return false;
        for (int i = 0; i < detached.NodeCount; i++)
        {
            ref readonly var node = ref detached.At(i);
            if (node.InUse && node.ImageId != 0) return true;
        }
        return false;
    }

    private bool HasRenderMotion()
    {
        if (HasOwnRenderMotion()) return true;
        // A child whose present slot was busy this turn left its publication pending (DrainChildRenderSources), and one whose
        // non-blocking present was refused owes that frame: keep the loop ticking so the next turn probes the slot / re-presents
        // instead of sleeping until some unrelated wake.
        foreach (var child in Volatile.Read(ref _childRenderSources)) if (child.HasOwnRenderMotion() || child._slotDeferred || child._presentOwed) return true;
        return false;
    }

    // Render-thread-owned (set/cleared only in DrainChildRenderSources; read by HasRenderMotion on the same thread): this
    // child's own swapchain had no free present slot on the last drain, so its pending publication was left in the mailbox.
    private bool _slotDeferred;
    // Render-thread-owned: when this child's publication was first deferred behind a busy slot (QPC; 0 = not deferred), so its
    // present's lag counts the whole wait; and the count of real Presents this host made on the render thread (a drain tells a
    // present from an elided turn by its change).
    private long _deferredSinceQpc, _renderPresentCount;
    // Render-thread-owned: this child's non-blocking present was REFUSED (DXGI_ERROR_WAS_STILL_DRAWING; see PresentFrame), so the
    // frame it drew into the back buffer is still owed to the glass. _owedFrame names it; DrainChildRenderSources re-presents it
    // on a later turn (or a fresh publication supersedes it). The latency credit was never spent, so it is still held.
    private bool _presentOwed, _owedComposited;
    private Threading.RenderFrame _owedFrame;
    // Per-target pacing evidence of a detached child (the child= section of [render.pace], its PresentLedger rows, its depth
    // log). Set once on the UI thread by AttachChildRenderSource BEFORE the child is published to the render thread's snapshot.
    private Threading.ChildPresentPace? _childPace;
    private static int s_childPaceTargets;   // pace-target ids (1, 2, ...; 0 = the primary window)

    private void RenderMotion()
    {
        // motionRepresent: true — this re-presents the RETAINED _activeRenderFrame on a later vblank than the one it
        // was published for. It is a plain vsync'd refresh, never the original publish's interactive/suppressed-vsync
        // present (item C, §2): ApplyPresentPacing must not re-apply SuppressVsync here.
        if (HasOwnRenderMotion()) SubmitPresentOnRenderThread(_activeRenderFrame, motionRepresent: true);
        else
        {
            // Motion ended during the slot wait: no submit runs, but the render loop still counts this turn as a present, so it
            // must not hand SamplePresentSplit the PREVIOUS turn's phases as this turn's split.
            _presentSplit = default;
            _splitVideoMs = 0;
        }
    }

    private bool _idleTrimDormant;   // the last pass found nothing trimmable; any composite turn clears it (SubmitSlices)

    /// <summary>Render-thread housekeeping between turns, on wall clock (never a turn count: an idle app runs no turns). Evicts
    /// retained tiles nothing has requested for <see cref="FluentGpu.Render.Tiles.SliceTable.StaleTileMs"/> (never the last turn's
    /// own set) and hands their textures back, then lets the device drain its retired queues and drop idle scratch / stencil /
    /// staging resources. Returns the ms until it next has something to do, or -1 when nothing is trimmable (the render
    /// thread then blocks indefinitely until a turn re-arms it): a clean-idle app takes no periodic wake.</summary>
    private int TrimIdleOnRenderThread(long nowMs)
    {
        if (_idleTrimDormant) return -1;
        long next = long.MaxValue;
        if (_renderTiles is { } tiles)
        {
            tiles.EvictStale(nowMs);
            var freed = tiles.TrimFreeSlotsNow();
            if (!freed.IsEmpty) { _device.TrimTileSurfaces(freed); next = 500; }   // the retired textures release once the fence passes
            long due = tiles.NextStaleInMs(nowMs);
            if (due >= 0 && due < next) next = Math.Max(1, due);
        }
        int dev = _device.TrimIdleResources(nowMs);
        if (dev >= 0 && dev < next) next = dev;
        if (next == long.MaxValue) { _idleTrimDormant = true; return -1; }
        return (int)Math.Min(next, int.MaxValue);
    }

    private long RenderPeriodTicks() => Volatile.Read(ref _renderPeriodTicks);

    /// <summary>Every image id something in this host still HOLDS — the proof the image cache needs before reclaiming a
    /// tombstone (<see cref="ImageCache.SetHeldImageSource"/>): each live scene node's paint image (parked nodes included:
    /// they re-pin by id on unpark), its image effects' derived/outgoing ids, its row cells' images, plus the reconciler's
    /// hold-last-good and swap ids. Runs only on the cache's amortized reclaim cadence (UI thread), never per frame.</summary>
    private void CollectHeldImageIds(HashSet<int> held)
    {
        int count = _scene.RecordingNodeCount;
        for (int i = 0; i < count; i++)
        {
            var node = _scene.HandleAt(i);
            if (node.IsNull || !_scene.IsLive(node)) continue;
            ref NodePaint paint = ref _scene.Paint(node);
            if (paint.ImageId != 0) held.Add(paint.ImageId);
            if (_scene.TryGetImageEffects(node, out var fx))
            {
                if (fx.DerivedImageId != 0) held.Add(fx.DerivedImageId);
                if (fx.SwapOutgoingId != 0) held.Add(fx.SwapOutgoingId);
            }
            if (_scene.TryGetRowCells(node, out var cells, out _, out _))
                foreach (ref readonly var cell in cells) if (cell.ImageId != 0) held.Add(cell.ImageId);
        }
        _reconciler.CollectHeldImageIds(held);
    }

    /// <summary>Render thread, after each present: fold the latest retired whole-frame GPU execution into the
    /// present-queue depth policy and apply a changed depth to the device (see <see cref="PresentQueueDepthPolicy"/> for
    /// the latency/throughput trade-off). The present prediction reads the depth in force from the next frame on.
    /// <para>Per target: the sample is THIS host's swapchain's (<see cref="ISwapchain.TryGetGpuRenderSample"/>), the policy is
    /// this host's, and the depth is applied to that same swapchain (<see cref="IGpuDevice.SetPresentQueueDepth(ISwapchain, int)"/>)
    /// - a detached pop-out follows its own GPU margin and can never retarget the main window's present queue (nor the
    /// reverse).</para></summary>
    private void ChoosePresentQueueDepth()
    {
        if (!_swapchain.TryGetGpuRenderSample(out GpuRenderSample sample)) return;
        double refreshMs = RenderPeriodTicks() * 1000.0 / Stopwatch.Frequency;
        if (!_depthPolicy.Observe(sample.ExecutionMs, sample.Sequence, refreshMs)) return;
        int depth = _device.SetPresentQueueDepth(_swapchain, _depthPolicy.Depth);
        Volatile.Write(ref _maxFrameLatency, depth);
        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[render.depth] target={_childPace?.Target ?? 0} depth={depth} gpuEmaMs={_depthPolicy.EmaMs:F2} refreshMs={refreshMs:F2}"));
    }

    /// <summary>Render thread, with the <c>[render.pace]</c> window: open every attached child's window.</summary>
    private void BeginChildPaceWindow()
    {
        _device.ResetNonPrimaryLatencyWindow();   // childWaitMax= is a per-window figure, like every child token
        var list = Volatile.Read(ref _childRenderSources);
        for (int i = 0; i < list.Length; i++) list[i]._childPace?.BeginWindow();
    }

    /// <summary>Render thread, at the <c>[render.pace]</c> report: the <c>child=</c> section (leading space; empty when no child
    /// presented or was deferred in the window) - each pop-out's own presents, slot probe and lag, which the parent's
    /// counters never include.</summary>
    private string DescribeChildPace()
    {
        var list = Volatile.Read(ref _childRenderSources);
        System.Text.StringBuilder? sb = null;
        for (int i = 0; i < list.Length; i++)
        {
            string? token = list[i]._childPace?.Describe();
            if (token is null) continue;
            if (sb is null) sb = new System.Text.StringBuilder(" child=[");
            else sb.Append(' ');
            sb.Append(token);
        }
        return sb is null ? "" : sb.Append(']').ToString();
    }

    /// <summary>The host's half of the render thread's [render.pace] line (sampled on the render thread once a second),
    /// and the retired GPU time the slot catch-up policy reads after every paced present (<c>SlotCatchUp.Observe</c>).
    /// Cross-thread reads of plain fields; allocation-free (a record struct by value).</summary>
    private RenderPaceHostState SamplePaceHostState()
        => new(_gpuBoundEma, _gpuGovernorEngaged, _lastWaitKind,
               _swapchain.TryGetGpuRenderSample(out GpuRenderSample s) ? s.ExecutionMs : 0.0, Volatile.Read(ref _maxFrameLatency),
               _device.SlotLivenessTimeouts, _device.NonPrimaryLatencyTimeouts, _device.NonPrimaryLatencyWaitMaxMs);

    /// <summary>UI-readable pacing snapshot: the render thread's cumulative present counters, the display clock's filter
    /// state, the present-queue depth, the adaptive GPU governor and the latest GPU execution — the data behind the
    /// always-on [render.pace] line. A reader differences two samples. Default (all zero) without a render thread.</summary>
    public RenderPaceSnapshot RenderPace
    {
        get
        {
            var rt = _renderThread;
            var clock = _window.DisplayClock;
            double toMs = 1000.0 / Stopwatch.Frequency;
            return new RenderPaceSnapshot
            {
                FreshPresents = rt?.FreshPresents ?? 0,
                MotionPresents = rt?.MotionPresents ?? 0,
                SkippedTicks = rt?.SkippedTicks ?? 0,
                RaceHits = rt?.RaceHits ?? 0,
                CatchUpSkips = rt?.CatchUpSkips ?? 0,
                MissedMotionTicks = rt?.MissedMotionTicks ?? 0,
                TickSeq = rt?.TickSeq ?? 0,
                SlotWaitCount = rt?.SlotWaitCount ?? 0,
                SlotWaitSumMs = (rt?.SlotWaitTotalQpc ?? 0) * toMs,
                SlotWaitMaxMs = (rt?.SlotWaitMaxQpc ?? 0) * toMs,
                ClockPeriodMs = clock.MeasuredPeriodQpc * toMs,
                ClockIgnoredReturns = clock.IgnoredReturns,
                ClockSlotDrops = clock.SlotDrops,
                ClockDecimating = clock.Decimating,
                PresentQueueDepth = Volatile.Read(ref _maxFrameLatency),
                GovernorEmaMs = _gpuBoundEma,
                GovernorEngaged = _gpuGovernorEngaged,
                LastWaitKind = _lastWaitKind,
                GpuExecutionMs = _swapchain.TryGetGpuRenderSample(out GpuRenderSample s) ? s.ExecutionMs : 0.0,
            };
        }
    }
    // The dedicated render thread, constructed for a real windowed host (mode Async — the default — or ForceSync). null ⇒
    // the SingleThread inline pass-through (headless, and the internal SingleThread override). It runs submit/present off
    // the UI thread; under ForceSync the UI still blocks on it (no async overlap), under Async it presents on its own timeline.
    private Threading.RenderThread? _renderThread;   // set once: the constructor (a windowed host), or InstallRenderThreadForTest before the first frame
    // Step 1 (ASYNC only): the image upload/evict handoff. Non-null ⇒ ImageCache hands GPU work to the render thread
    // through this queue (drained in SubmitPresentOnRenderThread before submit) instead of touching the device on the UI
    // thread. Null in default/force-sync — there the direct device sinks run with no cross-thread overlap.
    private readonly Threading.ImageUploadQueue? _imageQueue;
    private readonly Threading.BakedBlurQueue _bakedBlurQueue = new();
    // Step 4 (ASYNC): device-lost recovery rendezvous. Foreground recovery is synchronous and reuses RecoverDevice
    // directly; async parks the render loop and drives RecoverDevice through this coordinator.
    private readonly Threading.DeviceLostCoordinator? _deviceLost;
    private static int s_forceLostFrame => EngineSwitches.ForceDeviceLostAtFrame;   // `--fg device-lost=N` (recovery test arm)
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
    // ── Detached-child render failure (INCIDENT 2026-09) ────────────────────────────────────────────────────────────
    // A detached child (video pop-out) rides the PARENT's render thread (DrainChildRenderSources → this host's own
    // SubmitPresentOnRenderThread). A non-device-loss exception from a child's own frame used to rethrow out of that
    // catch (":1244", by design — a genuine bug there must not be silently masked for the MAIN host) straight through
    // DrainChildRenderSources (no catch) and the shared RenderThread.Loop, killing the whole process over ONE popup
    // frame. `_renderFailed` latches per-host (only ever set on a host with `_parentRenderThread is not null`, i.e. a
    // detached child) so DrainChildRenderSources can permanently skip a failed child instead of retrying a frame that
    // is expected to fail identically forever; `OnRenderFailed` lets the owner (IDetachedVideoWindow.OnRenderFailed)
    // react — e.g. close the pop-out and fall back inline — without polling. Written only on the render thread inside
    // the catch below; read from both threads, so it is `volatile` (a one-way latch, same shape as HasPresentedContent
    // on D3D12Swapchain).
    private volatile bool _renderFailed;
    /// <summary>True once this host's own render frame threw a non-device-loss exception (detached children only —
    /// see the block above). One-way latch; never cleared. Read by <see cref="DrainChildRenderSources"/> to stop
    /// resubmitting a child that cannot present, and surfaced to the app via <c>IDetachedVideoWindow.RenderFailed</c>.</summary>
    public bool RenderFailed => _renderFailed;
    /// <summary>Fired once, on the render thread, the instant <see cref="_renderFailed"/> latches. The owner (app code
    /// via <c>IDetachedVideoWindow.OnRenderFailed</c>) typically marshals off this thread before touching UI state.</summary>
    public event Action? OnRenderFailed;
    private readonly InputDispatcher _dispatcher;
    private readonly InputEventRing _ring = new();
    private readonly IFrameTimeSource _frameTime;
    private readonly bool _isHeadless;   // headless: FixedFrameTimeSource; the scroll clock's FrameSec is the deterministic _frameClockMs instead of a QPC read
    private readonly AnimEngine _anim;
    private readonly ConnectedAnimation _connected;
    // ── the ONE frame clock — built once at the top of RunFrame, before pump/dispatch, so PumpScroll (Paint, after
    // the display-phase gate) and the scroll frame step (also Paint) share the identical (FrameQpc, PresentQpc) pair
    // with whatever DirectManipulation samples this frame. Named _palFrameClock (not
    // _frameClock) because that identifier is already the FrameClock.Tick poller's frame counter (_frameClockSig).
    private FluentGpu.Pal.FrameClock _palFrameClock;
    private ulong _frameClockSeq;                         // RunFrame-call ordinal — FrameClock.Seq (never resets)
    private long _frameClockFloorQpc;                     // RefreshLattice.Snap's monotonicity floor (replaces the old QuantizedFrameSec's _lastQuantizedFrameQpc)
    private readonly InputEventRing _scrollPumpRing = new();  // dedicated ring for IPlatformWindow.PumpScroll (kept separate from _ring, which the top-of-RunFrame pump already drained this frame)
    private readonly RepeatTicker _repeat;
    private readonly CaretBlinker _caretBlinker;
    private readonly ImageCache _images;
    private ImageStatusHandler? _onSharedImageStatus;   // detached child only: its own nodes' per-id completion route (F108); detached in PrepareDispose
    // M5 (adreno-hang-fixes.md): hysteresis/cooldown/grace for _images.EvictToVramPressure — see VramShedPolicy.cs.
    private VramShedPolicy _vramShed;
    private readonly Dictionary<NodeHandle, ProjCapture> _projectBefore = new();   // captured presented rects of BoundsAnimated nodes (FLIP "First")
    private readonly List<NodeHandle> _projectionSuppressionRoots = new();          // changed projected containers that own descendant motion this commit
    private readonly List<NodeHandle> _liveReflowScratch = new(8);                  // nodes with a live LayoutW/H reflow row this commit (ApplyProjections shove suppression)
    private readonly Dictionary<int, int> _reflowShoveFrames = new(16);             // parent idx → the child idx on a reflowing node's ancestor path ("the reflow-carrying child under this parent")
    private readonly List<RenderContext> _pendingLayoutEffectContexts = new();
    private readonly List<RenderContext> _pendingPassiveEffectContexts = new();

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
        int BlurCandidateCount, int BlurGroupCount,
        int EdgeFadeGroupCount, int RepaintRects, RepaintFullReason RepaintFull, double FlushMs, double LayoutMs, double AnimMs, double RecordMs)
    {
        public readonly bool IsValid => Seq != 0;
    }

    // Ambient context signals (read via UseContext): published by the host, consumers subscribe granularly.
    private readonly Signal<object?> _viewportSig = new(default(Size2));
    private readonly Signal<object?> _viewportScaleSig = new(1f);   // Viewport.Scale ambient (DIP→device px)
    private readonly Signal<object?> _viewportZoomSig = new(1f);    // Viewport.Zoom ambient (app-zoom factor; display-only — Scale already contains it)
    private readonly Signal<object?> _frameStatsSig = new(default(FrameStats));
    private readonly InputHooks _inputHooks = new();
    private readonly Signal<object?> _inputHooksSig;
    private readonly Signal<object?> _frameClockSig = new(0L);
    /// <summary>The <c>FrameClock.PaceableTick</c> ambient: the same frame counter as <see cref="_frameClockSig"/>, but its
    /// subscribers raise <see cref="WakeReasons.FrameClockPaceable"/>, which the GPU governor may pace.</summary>
    private readonly Signal<object?> _frameClockPaceableSig = new(0L);
    private long _frameClock;
    // Drag epoch → UseDragState. EDGE-triggered (session begin/end, OverTarget / Effect / Caption change, settle
    // start+expiry): the chip FOLLOWS through the DragPosX/Y binds below, so bumping this per frame — as it used to —
    // re-rendered the whole preview subtree at pointer rate for a value nothing read.
    private readonly Signal<int> _dragEpoch = new(0);
    // InputHooks.WindowOccluded: PARKED (minimized / hidden) OR the primary swapchain's IsOccluded (cloaked / covered
    // stand-down, or the DXGI occlusion latch where the backend reports one). Written once per RunFrame ABOVE the park and
    // idle gates (PublishWindowOccluded), value-eq-gated.
    private readonly Signal<bool> _windowOccluded = new(false);
    // The occlusion probe (PublishWindowOccluded): the next forced present while the swapchain reports occluded and the
    // window is not parked, on the host timer clock (_timers.NowMs). NaN = disarmed.
    private double _occlusionProbeDueMs = double.NaN;
    private const double OcclusionProbeIntervalMs = 250.0;
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
    // Window-visibility ambient (Activation.IsActive): false while the window is parked (minimized OR hidden) OR while the
    // app has signalled a power suspend (SetWindowActive(false)). UseIsActive AND-folds it with each component's
    // KeepAlive-parked state. Written on the park/un-park EDGE in RunFrame (and by SetWindowActive); value-eq-gated, so a
    // steady frame is a no-op.
    private readonly Signal<bool> _windowVisible = new(true);
    private bool _windowActiveApp = true;                // app-side power suspend/resume gate (AND-ed into _windowVisible)

    // Cross-thread UI dispatch (HostDispatch.Post / UsePost): worker / OS-callback / agile-COM threads enqueue
    // UI-thread actions and Wake() the loop; drained inside a reactive Batch at the top of each frame's flush so the
    // posted signal writes coalesce into one re-render. The engine-owned replacement for hand-rolled post-to-UI plumbing
    // (and for the UseContext(FrameClock.Tick)-to-drain anti-pattern that re-rendered every frame just to poll).
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _uiPosts = new();
    private readonly Signal<object?> _hostPostSig;
    private Action<Action>? _postForward;   // set when a detached child is reaped: Post forwards here (the parent's poster)
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

    /// <summary>Per-component render census for every frame (see <see cref="TreeReconciler.RenderCensusEnabled"/>): when
    /// on, a frame whose flush exceeds the panel's refresh interval carries the top offenders on <see cref="FrameStats.Census"/>.
    /// App-settable via <c>AppOptions.RenderCensus</c>; an app that logs every slow frame turns it on for the session.</summary>
    public bool RenderCensus
    {
        get => _reconciler.RenderCensusEnabled;
        set => _reconciler.RenderCensusEnabled = value;
    }
    private bool _warmCadenceEnabled;
    private double _warmCadenceUntilMs;

    // ── `--fg alloc` (EngineSwitches.AllocDiag): once-per-second allocation/CPU attribution (stderr) ──
    // UI-thread bytes + ticks per frame segment (GetAllocatedBytesForCurrentThread deltas) and the process-wide
    // allocation total, so scroll-time churn can be pinned to a phase (or to a worker thread) without a profiler.
    private static bool s_allocDiag => EngineSwitches.AllocDiag;
    private static bool s_fpsLog => EngineSwitches.FpsLog;
    // `--fg fps` hitch attribution: GC.CollectionCount deltas since the previous painted frame (0 when flag off).
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
    private System.Text.StringBuilder? _diagSb;   // reused across reports (one alloc, not new-per-report) — `--fg alloc` only

    // ── `--fg mem[=N]` / `--fg alloc-types`: opt-in diagnostics tools (each behind its own switch; nothing when off).
    // The wake census is NOT among them any more — it is always on (WakeDiagnostics, the [wake] line). ──
    private static bool s_memDiag => EngineSwitches.MemDiagSeconds > 0;
    // The AllocTypeProfiler listener is constructed by the app layer (FluentApp.Run); the host only drives its
    // once-per-second report on the frame cadence (no extra timer thread). Reads are no-ops when not started.
    private static bool s_allocTypes => EngineSwitches.AllocTypes;

    // ── `--fg resize`: per-tick timing of the keep-alive (modal move/size loop) paint, so smoothness is measurable. ──
    // One line per modal-loop tick to stderr — total/ensureSize/layout/submit+present ms — gated entirely so the normal
    // hot path and the zero-alloc gates are untouched (no work, no allocation, when the flag is off).
    private static bool s_resizeDiag => EngineSwitches.ResizeDiag;
    // ── `--fg motion`: projected-motion (Reveal/FLIP) discrimination trace (why a structural transition snapped vs animated). ──
    // One [motion-diag] line per reconciling frame (capture summary) + one per captured node in ApplyProjections (branch OUTCOME)
    // + AnimEngine seed/snap lines + per-frame structural tick values. Entirely gated — no work, no allocation, when the flag is off.
    private static bool s_motionDiag => EngineSwitches.MotionDiag;
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
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_coldMaintenanceWakeGate)
            {
                _pixelPool.BufferRetained -= OnPixelBufferRetained;
                _pixelPool = value;
                if (!_coldMaintenanceStopped) _pixelPool.BufferRetained += OnPixelBufferRetained;
                if (_imageQueue is not null) _imageQueue.BufferPool = value;
            }
            if (value.RetainedBytes > 0) OnPixelBufferRetained(value);
        }
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
    private bool _pendingThumbClick;                   // thumbnail-toolbar click pending; drained at Paint top
    private int _pendingThumbButtonId;                 // valid when _pendingThumbClick (0 is a legal button id)
    private Action<int>? _onThumbButtonClicked;        // cached subscription (unsubscribed in Dispose)
    // App navigation command (mouse side buttons / Back-Forward keys). Coalesced to the LAST command of the frame: the
    // OS can deliver a burst if the user mashes the button, and replaying every one of them would blow through the
    // history stack in a single frame.
    private bool _pendingAppNavigation;
    private int _pendingAppNavigationWhich;
    private Action<int>? _onAppNavigationCommand;      // cached subscription (unsubscribed in Dispose)
    private bool _pendingTaskbarButtonCreated;         // explorer created/recreated the taskbar button
    private Action? _onTaskbarButtonCreated;           // cached subscription (unsubscribed in Dispose)

    /// <summary>Raised on the UI thread when the OS color settings change (Windows app dark/light flip or accent change),
    /// delivered at the top of the next frame so handlers may freely mutate the theme / write signals. App code reacts by
    /// re-reading the OS state and calling <see cref="RequestThemeTransition"/> (typically only while it follows the OS).
    /// Wired from <see cref="FluentGpu.Pal.IPlatformApp.SystemColorsChanged"/>; never fires under the headless PAL.</summary>
    /// <summary>Raised on the UI thread at the end of every RENDERED frame with that frame's <see cref="FrameStats"/>
    /// (phase times included). A struct argument, so the invoke allocates nothing; handlers must be cheap — they run
    /// inside the frame, after present has been handed off.</summary>
    public event Action<FrameStats>? FrameCompleted;
    public event Action? SystemColorsChanged;

    /// <summary>
    /// Raised on the UI thread when a SECOND launch of a single-instance app is redirected to this running instance,
    /// carrying the new launch's activation payload (the deep-link URI, e.g. <c>wavee://callback?…</c>, or the empty
    /// string for a focus-only relaunch). Wired from <see cref="IPlatformApp.ActivationRedirected"/> and delivered at the
    /// top of the next frame, so handlers may freely mutate signals (a re-render is already scheduled). Set up by
    /// <c>FluentGpu.WindowsApi.Activation.SingleInstanceGate</c> on the sender side; never fires under the headless PAL.
    /// </summary>
    public event Action<string>? ActivationRedirected;

    /// <summary>
    /// Raised on the UI thread when the user clicks a taskbar thumbnail-toolbar button, carrying the button's
    /// application-defined id. Wired from <see cref="IPlatformApp.ThumbButtonClicked"/> and delivered at the top of the
    /// next frame, so handlers may freely mutate signals. Never fires under the headless PAL.
    /// </summary>
    public event Action<int>? ThumbButtonClicked;

    /// <summary>
    /// Raised on the UI thread when the OS reports a browser-style navigation command — a mouse's side buttons
    /// (XButton1/2) or a keyboard Back/Forward key. Payload is <c>0 = Back</c>, <c>1 = Forward</c>. Wired from
    /// <see cref="FluentGpu.Pal.IPlatformApp.AppNavigationCommand"/> and delivered at the top of the next frame, so
    /// handlers may navigate and mutate signals freely. Coalesced to the last command of the frame (a mashed side button
    /// must not walk the whole history stack in one frame). Never fires under the headless PAL.
    /// </summary>
    public event Action<int>? AppNavigationCommand;

    /// <summary>
    /// Raised on the UI thread when explorer creates (or re-creates, after a shell restart) this window's taskbar
    /// button. Wired from <see cref="IPlatformApp.TaskbarButtonCreated"/> and delivered at the top of the next frame.
    /// Thumbnail-toolbar callers re-invoke <c>TaskbarManager.SetThumbButtons</c> here (after
    /// <c>NotifyTaskbarButtonCreated</c> to reset the add-once latch). Never fires under the headless PAL.
    /// </summary>
    public event Action? TaskbarButtonCreated;

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

    // --fg resize: stopwatch ticks since <paramref name="sinceTicks"/> as milliseconds (modal-loop tick segment timing).
    private static double ElapsedMs(long sinceTicks) => (Stopwatch.GetTimestamp() - sinceTicks) * 1000.0 / Stopwatch.Frequency;
    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;   // FrameStats per-segment timing

    // --fg resize: one line per modal move/size-loop keep-alive tick — total paint, ensureSize (swapchain resize),
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
            $"[--fg resize t={Environment.TickCount64}] tick total={totalMs:F2}ms ensureSize={ensureMs:F2}ms layout={layoutMs:F2}ms submit+present={submitMs:F2}ms " +
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

    /// <summary>PARK the fgpu-render thread, run <paramref name="uiGpuWork"/> with the UI thread as the sole GPU owner,
    /// then release it — the REPEATABLE form of <see cref="QuiesceRenderThread"/> (which stops the thread for good and
    /// therefore cannot be used more than once in a live session). Exactly the mutual exclusion the fenced UI-side
    /// swapchain <c>Resize</c> already runs under, so a UI-thread GPU read here is no more novel than that one.
    /// <para>Used by the <c>--repaint-identity</c> harness, which has to interleave frame production with back-buffer
    /// captures; an embedder doing any out-of-band GPU work against the same device needs the same bracket. Re-entrancy
    /// is not supported (the underlying park/resume is not counted), and <paramref name="uiGpuWork"/> must not itself
    /// run a frame.</para></summary>
    public void RunWithRenderThreadParked(Action uiGpuWork)
    {
        ArgumentNullException.ThrowIfNull(uiGpuWork);
        if (_renderThread is { } rt)
        {
            rt.Quiesce();
            try { uiGpuWork(); }
            finally { rt.Resume(); }
        }
        else uiGpuWork();
    }

    /// <summary>
    /// Submit one composited turn (retained tiles §A.8): build the <see cref="CompositeFrame"/> from the slice recorder's
    /// composite plan — the <see cref="FluentGpu.Render.Tiles.SliceTable"/> per-turn flow (open, invalidate, request the
    /// needed tiles, resolve) runs every turn — hand it to the backend's <see cref="IGpuDevice.SubmitComposite"/>, then mark
    /// exactly the tiles the backend reported rastered as valid. The primary window has no other route.
    /// </summary>
    private void SubmitSlices(SliceRecorder slices, FluentGpu.Render.Tiles.SliceTable tiles, SceneRecordingSnapshot scene,
        in FrameInfo submit, int themeEpoch, ulong publishSeq)
    {
        // The composite route draws into the PRIMARY swapchain through one device-wide tile pool. A detached child owns
        // neither, so reaching here is a routing bug (ChooseSubmitRoute) - fail fast rather than draw into the main window.
        if (_isDetachedChild)
            throw new InvalidOperationException("A detached child host must present through its own swapchain (SubmitDrawList), never SubmitComposite.");
        if (!_device.SupportsComposite)
            throw new InvalidOperationException(_device.BackendName + " cannot composite the retained tiles (SubmitComposite).");
        _idleTrimDormant = false;   // a composite turn may leave new stale tiles: the idle trim re-arms
        slices.EvidencePublishSeq = publishSeq;   // the ledger frame names the publication it presents
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var frame = slices.BuildComposite(tiles, scene, in submit, themeEpoch, submit.RepaintDamage, withStreams: true);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        _device.SubmitComposite(in frame, _swapchain);
        slices.EndComposite(tiles, in frame);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        _evBuildTicks = t1 - t0; _evSubmitTicks = t2 - t1;
        NoteTileCensus(slices, tiles);
        _ledgerTurnTiles += slices.LastRasteredTiles;
        FluentGpu.Scroll.Diag.ScrollProbe.RenderCost(FluentGpu.Scroll.Diag.ScrollCostPhase.Composite, t1 - t0,
            frame.Items.Length, slices.LastExposedTileMissing);
        FluentGpu.Scroll.Diag.ScrollProbe.RenderCost(FluentGpu.Scroll.Diag.ScrollCostPhase.TileRaster, t2 - t1,
            slices.LastRasteredTiles, (int)(slices.LastRasteredBytes / 1024));
    }

    private readonly object _tileCensusLock = new();
    private FluentGpu.Render.Tiles.TileCensus _tileCensus;

    /// <summary>Publish the composite turn's tile census (render thread, or the UI thread inline) for the UI-side frame stats.</summary>
    private void NoteTileCensus(SliceRecorder slices, FluentGpu.Render.Tiles.SliceTable tiles)
    {
        var c = FluentGpu.Render.Tiles.TileCensus.Capture(tiles, slices.LastItems.Length, slices.LastRasteredTiles,
            slices.LastExposedTileMissing, slices.LastCoverageClamps, slices.LastBudgetBytes, slices.LastStats, _device.LastCompositeCache,
            _device.LastLostPlacements);   // a placed tile whose slot held no texture composited nothing (must be 0)
        lock (_tileCensusLock) _tileCensus = c;
        NoteStaleTiles(tiles, c.StaleTiles);   // the stale-tile invariant: TileInvariants tally + the edge-gated [tiles.stale] line
    }

    /// <summary>The latest composite turn's retained-tile census (any thread).</summary>
    public FluentGpu.Render.Tiles.TileCensus LastTileCensus { get { lock (_tileCensusLock) return _tileCensus; } }

    /// <summary>Arm this frame's one-shot present pacing (render thread, immediately before the submit that consumes it).
    /// Deliberately NOT armed for a frame the byte-identical skip elides — a suppress-once flag left standing would be
    /// spent by an unrelated later present.</summary>
    /// <param name="motionRepresent">True from <see cref="RenderMotion"/> re-presenting the retained frame on a later
    /// vblank than the one it published for (item C, §2). That is an ordinary vsync'd refresh of already-composed
    /// content, NOT the original suppressed-vsync keep-alive present the frame was published with — applying that stale
    /// flag here would present it at interval 0 with no present-slot credit (credit drift: DWM silently drops one of
    /// two queued presents).</param>
    private void ApplyPresentPacing(in Threading.RenderFrame rf, bool motionRepresent = false)
    {
        if (motionRepresent) return;
        if (rf.SuppressVsync) { _swapchain.SuppressVsyncOnce(); _swapchain.SuppressLatencyWaitOnce(); }
        // F101: the settle hint rides the frame that carries it and is armed here, on the presenting thread, for exactly the
        // submit that presents it. Not re-armed by a motion re-present (returned above): a retained frame is not a settle.
        if (rf.SettlePresent) _swapchain.HintSettlePresent();
    }

    /// <summary>The inline (UI-thread) turn's skip-submit hash: the composite plan's for the primary host, the standalone stream's
    /// for a detached child (which records into <c>_drawList</c> and submits it directly).</summary>
    private ulong InlineStreamHash()
        => _isDetachedChild ? DrawListHash(_drawList.Bytes, _drawList.SortKeys) : _uiSlices.CompositeHash;

    /// <summary>Which device route a host's turn submits through.</summary>
    internal enum SubmitRoute : byte
    {
        /// <summary>The primary window: retained tiles, <see cref="IGpuDevice.SubmitComposite"/> into the primary back buffer.</summary>
        Composite,
        /// <summary>A detached child (video pop-out): the whole frame recorded standalone and submitted with
        /// <see cref="IGpuDevice.SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo, ISwapchain)"/> against the
        /// child's OWN swapchain - never the primary's back buffer, tile pool or frame-latency credit.</summary>
        DirectOwnSwapchain,
    }

    /// <summary>The route a host submits through, as a pure decision (unit-tested). A detached child must never composite: the
    /// composite route is bound to the primary swapchain and the device-wide tile pool.</summary>
    internal static SubmitRoute ChooseSubmitRoute(bool isDetachedChild)
        => isDetachedChild ? SubmitRoute.DirectOwnSwapchain : SubmitRoute.Composite;

    /// <summary>FNV-1a 64 over a standalone-recorded command stream + painter sort keys, length-prefixed so the two spans can't
    /// alias. Record is a pure function of the scene, so an equal hash means byte-identical pixels, i.e. the front buffer is
    /// still correct (the detached child's skip-submit baseline; the composite route hashes its slice plan instead —
    /// <see cref="SliceRecorder.CompositeHash"/>). Hashed 8 bytes at a time.</summary>
    internal static ulong DrawListHash(ReadOnlySpan<byte> bytes, ReadOnlySpan<ulong> sortKeys)
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

    /// <summary>The render thread's byte-identical-frame elision, as a pure decision (unit-tested; see
    /// <c>RenderLifecycleTests</c>). True ⇒ the already-presented front buffer is still correct: the recorded stream
    /// hashes to the last SUBMITTED one, the frame's repaint set is empty (no rects and no forced-full cause), no
    /// clock-driven pixels are live (image crossfades — compositor poses no longer count, because a pose that moved
    /// fails the hash compare and one that did not is not a change), and no popup window rides this turn.
    /// <paramref name="lastPresentedHash"/> 0 means "no baseline yet" and never skips.</summary>
    internal static bool ShouldSkipRenderSubmit(ulong drawListHash, ulong lastPresentedHash, bool repaintPending,
                                                bool clockActive, bool hasPopupWindows)
        => lastPresentedHash != 0UL && drawListHash == lastPresentedHash
           && !repaintPending && !clockActive && !hasPopupWindows;

    // ── popup swapchain work, posted UI → render thread ──────────────────────────────────────────────────────────────
    // The render thread is the sole ComPtr owner, so a popup Resize/ConfigurePopupChrome/AnimatePopupClose used to park
    // it (Quiesce) from the UI. Those calls fire on EVERY pointer move over an open flyout, so the park was a per-frame
    // UI stall of up to one submit+present. They ride this single-producer/single-consumer mailbox instead, drained at
    // the top of SubmitPresentOnRenderThread — i.e. on the same turn that records + presents the popup. Two lists that
    // swap (never re-new) keep it allocation-free after the first flyout; the lock is uncontended and off the hot path.
    // Create and Dispose ride it too (F207): opening a flyout used to park the loop for the PAL window + CreateSwapchain, and
    // closing one for the swapchain release, each costing the UI up to a whole render turn (or the 1 s slot wait behind
    // it). The render thread is the ComPtr owner, so it now runs both itself; the UI only creates/destroys the HWND. Actions
    // name the SLOT, not a swapchain, because a Create may not have run when the UI posts the Resize that follows it - the
    // ops are applied in order, and the swapchain is resolved when each one runs. A detached child's OWN swapchain resize is
    // not in the list at all: it is ONE latest-wins slot (_ownResizePending, F093), because a live resize posts one per WM_SIZE
    // and only the newest size is ever worth a ResizeBuffers.
    private enum PopupRenderOp : byte { ResizeAndChrome, AnimateClose, Create, Dispose }

    private readonly record struct PopupRenderAction(PopupRenderOp Op, PopupWindowSlot? Slot, Size2 Size, PopupChromeMetrics Chrome,
                                                     SwapchainDesc Desc = default);

    private readonly object _popupActionLock = new();
    private List<PopupRenderAction> _popupActionsIn = new(4);
    private List<PopupRenderAction> _popupActionsOut = new(4);
    // A detached child's pending OWN swapchain resize (guarded by _popupActionLock): the newest requested size, applied once at
    // the next drain however many were requested since (at most one ResizeBuffers per render tick, F093).
    private bool _ownResizePending;
    private Size2 _ownResizeSize;
    private int _ownResizesApplied;

    private void PostPopupRenderAction(in PopupRenderAction action)
    {
        lock (_popupActionLock) _popupActionsIn.Add(action);
        // Ops that change what EXISTS (a swapchain built or released) must not wait for a publication that may never come:
        // wake the render loop, whose per-turn callback (DrainChildRenderSources) applies them. A popup's Resize / fade stays
        // unwoken - it is ordered behind the publication that carries the matching placement.
        if (action.Op is PopupRenderOp.Create or PopupRenderOp.Dispose) OwningRenderThread?.WakeAsync();
    }

    /// <summary>UI thread: ask the render thread to resize THIS host's own swapchain (a detached child's). Latest wins: a request
    /// made before the previous one was drained REPLACES it, so a live resize that posts per WM_SIZE costs the shared render
    /// thread one ResizeBuffers per turn, not one per message. Wakes the loop: the resize must not wait for a publication.</summary>
    private void PostOwnResize(Size2 size)
    {
        lock (_popupActionLock) { _ownResizePending = true; _ownResizeSize = size; }
        OwningRenderThread?.WakeAsync();
    }

    /// <summary>Render thread: apply every popup swapchain mutation the UI posted since the last turn, in order.</summary>
    private void DrainPopupRenderActions()
    {
        bool ownResize;
        Size2 ownSize;
        lock (_popupActionLock)
        {
            ownResize = _ownResizePending;
            ownSize = _ownResizeSize;
            _ownResizePending = false;
            if (_popupActionsIn.Count == 0 && !ownResize) return;
            if (_popupActionsIn.Count != 0) (_popupActionsIn, _popupActionsOut) = (_popupActionsOut, _popupActionsIn);
        }
        if (ownResize)
        {
            // This host's OWN swapchain (a detached child's: the primary parks for its resize instead), once, at the newest size.
            _ownResizesApplied++;
            try { _swapchain.Resize(ownSize); }
            catch (Exception) when (_device.NoteIfDeviceLost()) { }
        }
        for (int i = 0; i < _popupActionsOut.Count; i++)
        {
            ref readonly var a = ref CollectionsMarshal.AsSpan(_popupActionsOut)[i];
            switch (a.Op)
            {
                case PopupRenderOp.Create:
                    if (!a.Slot!.Retired) CreatePopupSwapchainOnRender(a.Slot!, a.Desc);   // closed again before this turn: nothing to build
                    break;
                case PopupRenderOp.ResizeAndChrome:
                    if (a.Slot!.Swapchain is { } resized) { resized.Resize(a.Size); resized.ConfigurePopupChrome(a.Chrome); }
                    break;
                case PopupRenderOp.AnimateClose:
                    a.Slot!.Swapchain?.AnimatePopupClose();
                    break;
                case PopupRenderOp.Dispose:
                    RetirePopupSlotOnRender(a.Slot!);
                    break;
            }
        }
        _popupActionsOut.Clear();   // retains capacity
    }

    /// <summary>Render thread: build a leased popup's swapchain. A failure must not escape (this runs outside the submit's
    /// device-lost handling): it is logged and latched on the slot, and the UI drops the popup and stops leasing windowed ones.</summary>
    private void CreatePopupSwapchainOnRender(PopupWindowSlot slot, in SwapchainDesc desc)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            slot.Swapchain = _device.CreateSwapchain(in desc);
            slot.Lifecycle.RenderCreateMs = ToMs(Stopwatch.GetTimestamp() - t0);   // the lease's render-thread half ([overlay.popup])
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[popup] windowed swapchain create failed on the render thread: {ex.Message}");
            Diag.Sink?.Invoke($"[popup] windowed swapchain create failed on the render thread: {ex}");
            slot.CreateFailed = true;
        }
    }

    /// <summary>Render thread: release a closed popup's swapchain, then hand its HWND back to the UI thread to destroy.
    /// The window must outlive its composition swapchain (destroying it first gives DirectComposition errors), and
    /// <c>DestroyWindow</c> belongs to the thread that created it.</summary>
    private void RetirePopupSlotOnRender(PopupWindowSlot slot)
    {
        var sc = slot.Swapchain;
        slot.Swapchain = null;   // first: nothing recorded after this reaches the swapchain being released
        sc?.Dispose();
        // In mailbox order: after this slot's last pass, so its per-pass fields are settled. The one [overlay.popup] line it leaves behind.
        Diag.Line(slot.Lifecycle.Line(slot.Material, Stopwatch.GetTimestamp()));
        Post(() => ReleaseRetiredPopupWindow(slot));
    }

    /// <summary>UI thread: the render thread released this popup's swapchain; destroy its window.</summary>
    private void ReleaseRetiredPopupWindow(PopupWindowSlot slot)
    {
        _retiringPopups.Remove(slot);
        slot.DisposeWindow();
    }

    /// <summary>UI thread, with the render loop PARKED (or absent): drop every queued action. Teardown only: the popups it
    /// named are released directly by <see cref="Dispose"/> (the slots in <c>_popupWindows</c> and <c>_retiringPopups</c>).</summary>
    private void PurgePopupRenderActions()
    {
        lock (_popupActionLock) { _popupActionsIn.Clear(); _ownResizePending = false; }
    }

    /// <summary>Test-only (F093): the own-swapchain resize seam — post, drain, and how many ResizeBuffers actually ran.</summary>
    internal void PostOwnResizeForTest(Size2 size) => PostOwnResize(size);
    internal void DrainPopupRenderActionsForTest() => DrainPopupRenderActions();
    internal int OwnResizesAppliedForTest => _ownResizesApplied;

    // F244: where the latest present turn's wall time went on THIS host (render-thread-owned; the render loop samples it through
    // SamplePresentSplit right after the turn). _splitVideoMs is DrainVideoForPresentTurn's own stamp, reset each turn.
    private Threading.PresentSplit _presentSplit;
    private double _splitVideoMs;

    private Threading.PresentSplit SamplePresentSplit() => _presentSplit;

    /// <summary>The phases of one present turn from the host's QPC stamps (F244): stage = turn start to staged, record = staged to
    /// recorded, submit = recorded to just before the present (net of the two waits the device reports for it, when it submitted),
    /// present = the present call, video = the drain's own stamp. A turn that did not submit passes
    /// <c>submitted: false</c> and reads no device waits.</summary>
    private Threading.PresentSplit BuildPresentSplit(long t0, long staged, long recorded, long beforePresent, long presented, bool submitted)
    {
        double latencyMs = 0, fenceMs = 0;
        if (submitted) _swapchain.GetLastSubmitWaits(out latencyMs, out fenceMs);
        double submitMs = Math.Max(0.0, ToMs(beforePresent - recorded) - latencyMs - fenceMs);
        return new Threading.PresentSplit(ToMs(staged - t0), ToMs(recorded - staged), submitMs, fenceMs, latencyMs,
            ToMs(presented - beforePresent), _splitVideoMs);
    }

    private void SubmitPresentOnRenderThread(Threading.RenderFrame rf, bool motionRepresent = false, bool retryOwedPresent = false)
    {
        Threading.ThreadGuard.AssertRender();
        long splitT0 = Stopwatch.GetTimestamp();
        _presentSplit = default;
        _splitVideoMs = 0;
        // Popup swapchain create/resize/chrome work the UI posted for this thread. FIRST — before the early-out and
        // before RecordPopups reads those swapchains — so a flyout's per-frame re-place lands on the same turn that
        // presents it, without the UI ever parking this loop for it (the old Quiesce per pointer move).
        DrainPopupRenderActions();
        if (!_renderSeam.IsCurrentTarget(rf)) return;
        try
        {
            if (retryOwedPresent)
            {
                // The frame a refused non-blocking present left in the back buffer: present it as it is. Nothing is recorded or
                // submitted (a second submit this turn would wait for the credit the first present has not yet given back),
                // and the feedback of that frame already ran with its first attempt. The video drain did NOT: it rides the
                // turn whose UI frame actually lands, so a hole-punched video never moves ahead of the frame that carries
                // its hole (a refused retry leaves the intents dirty in the registry for the next one).
                if (PresentFrame(in rf, _owedComposited)) DrainVideoForPresentTurn(in rf, placement: !_swapchain.LastPresentStoodDown);
                return;
            }
            int feedbackSize = 0;
            int settledPoses = 0;
            bool presented = true;
            bool composited = false;
            // Step 1 (async): stage uploads / free evictions on the render thread, BEFORE the submit opens its command list —
            // so a texture is resident before the draw that references it, and the store stays single-toucher (no lock).
            // INSIDE the try (deliberately): the staging path touches the device exactly like submit/present does, so a
            // device-removed failure there must land in the SAME recovery gate below. It used to sit one line outside,
            // which is how "Image.CreateUpload failed: 0x887A0005" left the fgpu-render thread as an unobserved
            // background exception and killed the process. The backend also soft-fails staging now (it rejects instead
            // of throwing) — this is the belt to that suspenders.
            if (_imageQueue is { } q)
            {
                if (rf.HasScene) q.SetSceneReader(this, _renderSeam.Scene(rf).Images);
                _device.DrainImageJobs(q);
            }
            long splitStaged = Stopwatch.GetTimestamp();
            long splitRecorded = splitStaged;
            if (rf.HasScene)
            {
                var sceneFrame = _renderSeam.Scene(rf);
                // Present-time clock (scroll rework §0/§5): this turn's pixels land on the vblank RefreshLattice predicts
                // from the tick that woke us — animations and scroll poses are both evaluated THERE, not at "now".
                double presentSec = RenderPresentSec(RenderTurnTickQpc());
                double nowMs = presentSec * 1000.0;
                bool fresh = rf.PublishSeq != _lastRecordedScene;
                if (fresh)
                {
                    _renderAnimations.Adopt(sceneFrame.Animations, sceneFrame.Scene, nowMs);
                    _lastSettledPoseCount = 0;
                }
                else _renderAnimations.Tick(sceneFrame.Scene, nowMs);
                bool scrollPosed = TickRenderScroll(sceneFrame, fresh, presentSec);
                bool scrollNeedsRecord = _renderSink.RecordRequired;
                _activeRenderFrame = rf;
                _hasActiveRenderFrame = true;
                // §13.1: a clock-driven turn whose poses did not move records a byte-identical stream and an empty
                // repaint region, so there is nothing for the rest of this block to do. Elide the RECORD, not just the
                // submit — HasOwnRenderMotion keeps re-entering here for as long as any row is live, so a Cadence.At(60)
                // row on a 120 Hz panel was paying a full scene record on every other turn to produce the same bytes.
                // Safe on the !fresh branch only: _lastRecordedScene already equals rf.PublishSeq there, so skipping
                // leaves no bookkeeping behind, and ChangedThisTick folds in Done transitions precisely because the
                // feedback publish below is what completes UI lifecycles.
                if (!fresh && !_renderAnimations.ChangedThisTick && !scrollPosed
                    && !sceneFrame.Images.HasCrossfades(RenderImageClock(rf, sceneFrame)))
                {
                    Interlocked.Increment(ref _framesSkippedSubmit);
                    _ledgerTurnOutcome = LedgerTurnOutcome.Elided;
                    long splitElided = Stopwatch.GetTimestamp();
                    DrainVideoForPresentTurn(in rf, elided: true);   // owed regardless (content only) — see its remarks
                    _presentSplit = BuildPresentSplit(splitT0, splitStaged, splitElided, splitElided, splitElided, submitted: false);
                    // E5: this turn elides the record AND the submit, so it will never reach the staging pass above
                    // again for a while — fence-only maintenance (retire-backlog release) still owes forward progress,
                    // or an image evicted under VRAM pressure sits un-released for as long as the clock keeps re-
                    // entering here. No command list, no present — just releases resources whose retire fence completed.
                    _device.ReclaimCompletedUploads();
                    return;
                }
                // Publications the consumer never adopted. A DIAGNOSTIC now, on both sides: the store keeps its
                // record-dirty bits and its pending-removal ledger until a publication is CONSUMED, so this capture
                // already carries the union of everything that changed since the last adopted one — a gap neither
                // kills span reuse nor seeds root damage. It is still REPORTED (the FrameStats.PublicationGaps
                // census a frame-stats consumer reads) because a sustained climb means the render thread is behind.
                bool publicationGap = fresh && rf.PublishSeq > _lastRecordedScene + 1;
                if (publicationGap)
                    Interlocked.Add(ref _publicationGaps, (long)(rf.PublishSeq - _lastRecordedScene - 1));
                // THE THREE-WAY RENDER TURN (retained tiles §A.8): nothing changed → elided above; only slice POSES moved
                // (a scroll offset, a sticky/parallax translation, a thumb position — composite parameters) → a
                // composite-only turn that records NOTHING and re-places the retained slices; a fresh publication, a
                // compositor animation, a non-translation scroll-effect channel or a pose the slices cannot honour
                // (a baked pose, offset-dependent chrome) → record.
                long recordStart = Stopwatch.GetTimestamp();
                SceneRecordStats stats;
                // A detached child (pop-out) takes the DIRECT route into its OWN swapchain (F090): it records the whole frame
                // standalone (no slice arenas) every turn, so it never has a composite-only turn, never touches the shared
                // tile pool and never composites into the primary back buffer.
                bool direct = ChooseSubmitRoute(_isDetachedChild) == SubmitRoute.DirectOwnSwapchain;
                bool compositeOnly = !direct && !fresh && !_renderAnimations.ChangedThisTick && !scrollNeedsRecord
                    && _renderSlices.PosesCompatible(sceneFrame.Scene);
                if (compositeOnly)
                {
                    var composed = sceneFrame.Compose(_renderSlices);
                    stats = new SceneRecordStats(0, 0, 0) { RepaintDamage = composed, Slices = _renderSlices.LastStats };
                }
                else
                {
                    stats = sceneFrame.Record(_renderCommands, _renderSpans, direct ? null : _renderSlices, publicationGap);
                    _lastRecordedScene = rf.PublishSeq;
                }
                splitRecorded = Stopwatch.GetTimestamp();
                double recordMs = ToMs(splitRecorded - recordStart);
                // §13.1 repaint set for THIS submit: what the recorder actually dirtied, unioned with what only the UI
                // could see (first frame / resize / DPI / clear-color / image content / live crossfades) — which the
                // publisher also carries forward across skipped publications. Full only for the named causes below.
                var repaint = stats.RepaintDamage;
                repaint.Union(rf.Submit.RepaintDamage);
                // Above the damage decision, because the crossfade arm below needs it.
                float imageClockMs = RenderImageClock(rf, sceneFrame);
                if (rf.TargetEpoch != _lastRenderedTargetEpoch)
                {
                    // First frame on this target, or the UI re-created/resized it: nothing on the target is trustworthy.
                    // The publisher forces the same thing off its own LastConsumedTargetEpoch; ForceFull keeps the FIRST
                    // reason, so agreeing twice is idempotent — and this arm also covers the very first frame.
                    repaint.ForceFull(RepaintFullReason.TargetInvalidated);
                    _lastRenderedTargetEpoch = rf.TargetEpoch;
                    _renderSubmissionContinuity = default;
                }
                // A clock-driven re-record USED to force full here, on the grounds that no scene bit described it.
                // Since §13.1's pose split that is no longer true: a pose stamps the posed node's own overlay-self
                // epoch, so the recorder's damage block describes exactly the nodes whose pixels moved, and forcing
                // full here threw that description away on every animation turn. The remaining undescribed case — an
                // image CROSSFADE, whose pixels advance with ImageClockMs under byte-identical commands and no dirty
                // bit anywhere — is no longer a render-thread ForceFull either (design-engine-images.md E1): the HOST
                // already unioned its own per-node crossfade rects into `rf.Submit.RepaintDamage` before publish
                // (`AddCrossfadeRepaint`), and that region rides in via the `repaint.Union` above on EVERY re-record of
                // the same publication — so this arm would only ever repeat a full the host already downgraded to
                // partial. Kept as a comment, not a case: deleting it silently would read as an oversight.
                ulong dlHash = direct ? DrawListHash(_renderCommands.Bytes, _renderCommands.SortKeys) : _renderSlices.CompositeHash;
                // Skip-submit (idle/slow-change power): a byte-identical stream with an EMPTY repaint region and no
                // clock-driven work is ALREADY on screen — the presented front buffer is still correct, so elide the
                // GPU submit + Present. The feedback publish still rides (compositor poses / video rects / popup
                // reveal gating are UI lifecycle, not pixels).
                // NOT _renderAnimations.HasActive any more: a live row whose value did not move records a byte-identical
                // stream AND an empty repaint region, which is exactly the question ShouldSkipRenderSubmit asks. A row
                // that DID move fails the hash compare on its own. Only image crossfades advance pixels with no bit
                // anywhere to show for it, so they alone keep a frame owed.
                bool clockActive = sceneFrame.Images.HasCrossfades(imageClockMs) || _device.HasLiveFeedback;   // + a settling feedback trail (F6)
                // An armed frame capture must present (evidence-diagnostics §A.6) — no tile is invalidated: the capture shows
                // exactly the retained pixels.
                bool skip = Volatile.Read(ref _evCaptureArmed) == 0 && ShouldSkipRenderSubmit(dlHash, _lastRenderPresentedHash, repaintPending: !repaint.IsEmpty,
                    clockActive: clockActive, hasPopupWindows: Volatile.Read(ref _popupWindowCount) != 0);
                var submit = rf.Submit with
                {
                    ImageClockMs = imageClockMs,
                    RepaintDamage = repaint,
                    CarriedFromSeq = _renderSubmissionContinuity.ExtendCarry(rf.Submit.CarriedFromSeq),
                };
                if (FrameLedger.Enabled) NoteLedgerSubmit(in repaint, in rf.Submit, compositeOnly ? LedgerTurnOutcome.CompositeOnly
                    : skip ? LedgerTurnOutcome.SkipSubmit : LedgerTurnOutcome.Recorded);
                if (skip)
                {
                    _renderSubmissionContinuity.Elided(rf.PublishSeq);
                    presented = false;
                    Interlocked.Increment(ref _framesSkippedSubmit);
                    if (fresh) Interlocked.Increment(ref _freshIdentical);
                    // E5: this turn skips the submit (no command list, no present) but the staging pass at the top of
                    // this method may still have queued retires this frame — a settle frame that never submits again
                    // must not leave them stuck behind a fence the device already signaled.
                    _device.ReclaimCompletedUploads();
                }
                else
                {
                    _renderPresentCensus.Note(
                        RenderPresentCensus.Classify(fresh, _renderAnimations.ChangedThisTick, scrollPosed || scrollNeedsRecord, clockActive),
                        recorded: !compositeOnly);
                    ApplyPresentPacing(in rf, motionRepresent);
                    if (direct)
                        _device.SubmitDrawList(_renderCommands.Bytes, _renderCommands.SortKeys, in submit, _swapchain);
                    else
                    {
                        SubmitSlices(_renderSlices, _renderTiles!, sceneFrame.Scene, in submit, sceneFrame.Options.ThemeEpoch, rf.PublishSeq);
                        composited = true;
                    }
                    sceneFrame.RecordPopups(_device, submit.Scale, submit.ImageClockMs);
                    _lastRenderPresentedHash = dlHash;   // §5.2 Fix A: every SUBMITTED stream becomes the elision baseline
                    _renderSubmissionContinuity.Submitted(rf.PublishSeq);
                }
                if (!direct)   // the per-turn cost row describes a composite turn; a direct child has no slice plan to cost
                    NoteTurnCost(_renderSlices, recordMs, compositeOnly, stats.Slices.KeptAll, skip,
                        capture: composited && Volatile.Read(ref _evCaptureArmed) != 0);
                var poses = _renderAnimations.Feedback;
                Threading.RecordingFeedback feedback = new()
                {
                    SceneSequence = rf.PublishSeq, Stats = stats, PoseCount = poses.Length,
                    CommandCount = direct ? _renderCommands.CommandCount : _renderSlices.TotalCommandCount,
                    RecordMs = recordMs,
                };
                feedback.VideoCount = sceneFrame.Scene.Recording.CopyVideoRects(feedback.VideoRects);
                int headerSize = System.Runtime.CompilerServices.Unsafe.SizeOf<Threading.RecordingFeedback>();
                int size = headerSize + poses.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<CompositorAnimationPose>();
                if (_feedbackBytes.Length < size) Array.Resize(ref _feedbackBytes, Math.Max(size, _feedbackBytes.Length * 2));
                Span<byte> bytes = _feedbackBytes.AsSpan(0, size);
                MemoryMarshal.Write(bytes, in feedback);
                MemoryMarshal.AsBytes(poses).CopyTo(bytes[headerSize..]);
                feedbackSize = size;
                foreach (ref readonly var pose in poses) if (pose.Done) settledPoses++;
            }
            else
            {
                ApplyPresentPacing(in rf, motionRepresent);
                _device.SubmitDrawList(_renderSeam.Bytes(rf), _renderSeam.SortKeys(rf), in rf.Submit, _swapchain);
                _ledgerTurnOutcome = LedgerTurnOutcome.Direct;
            }
            // A REFUSED non-blocking present (false) skips the video drain below: its Place/Destroy/bind commits stay coupled to
            // the UI frame that reaches the glass, so they ride the owed frame's retry (or the superseding publication).
            long splitBeforePresent = Stopwatch.GetTimestamp();
            if (presented) ArmGeometryMotionPresent(in rf);
            bool landed = !presented || PresentFrame(in rf, composited);
            long splitPresented = Stopwatch.GetTimestamp();
            _ledgerTurnPresented = presented && landed;
            if (feedbackSize != 0)
            {
                // Import only successfully presented poses; failed presents must not complete UI lifecycles.
                _recordFeedback.Publish(_feedbackBytes.AsSpan(0, feedbackSize), default, default);
                if (settledPoses > _lastSettledPoseCount) _window.Wake();
                _lastSettledPoseCount = settledPoses;
            }
            // A present that stood down (cloaked / minimized / occluded: nothing visible, nothing queued) must not commit video
            // placement for a hole that never reached the glass (F080): the placements stay dirty for the next real present.
            if (landed) DrainVideoForPresentTurn(in rf, placement: !(presented && _swapchain.LastPresentStoodDown));
            _presentSplit = BuildPresentSplit(splitT0, splitStaged, splitRecorded, splitBeforePresent, splitPresented, submitted: presented);
        }
        catch (System.Exception ex) when (_asyncActive)
        {
            // Step 4: a submit/present threw on the render thread. If the device is lost, record it (the UI recover gate
            // fires next frame) and SWALLOW — an unobserved background exception here would kill the process. A
            // non-device-loss throw is a genuine bug: rethrow so it isn't masked — UNLESS this host is a detached
            // child (`_parentRenderThread is not null`): it rides the PARENT's shared render thread, so rethrowing
            // here would kill the parent (and every OTHER child) over one popup's bad frame (INCIDENT 2026-09: a
            // stencil-DSV size mismatch closing the shared cmdList with E_INVALIDARG did exactly this). Survivable
            // instead: latch RenderFailed once, log ONE always-on line (this is not a debug-only path — a detached
            // child dying silently in the field is unreportable otherwise), fire OnRenderFailed so the owner can react
            // (e.g. close the pop-out), and swallow. DrainChildRenderSources checks RenderFailed and stops resubmitting
            // this child from here on — its frame is expected to keep failing the same way, and PRESENTING a torn/
            // partial submit would be worse than presenting nothing.
            if (!_device.NoteIfDeviceLost())
            {
                if (_parentRenderThread is null) throw;
                NoteChildRenderFailure(ex);
            }
        }
    }

    /// <summary>Render thread, detached child only: latch <see cref="_renderFailed"/> once, log ONE always-on line and fire
    /// <see cref="OnRenderFailed"/> (see the catch in <see cref="SubmitPresentOnRenderThread"/>).</summary>
    private void NoteChildRenderFailure(System.Exception ex)
    {
        if (_renderFailed) return;
        _renderFailed = true;
        string line = $"[detached] child frame failed hwnd={_window.Handle.Value:X}: {ex.GetType().Name}: {ex.Message}";
        Console.Error.WriteLine(line);
        Diag.Sink?.Invoke(line);
        OnRenderFailed?.Invoke();
    }

    /// <summary>Whether this host's presents are non-blocking (F085): only a detached child riding a parent's render thread,
    /// and only behind <see cref="EngineSwitches.NonBlockingSecondaryPresent"/>. The primary window's present is the one the
    /// pacing is built around and keeps its blocking contract; a popup has no retry path for a refused present.</summary>
    internal static bool UsesNonBlockingPresent(bool isDetachedChild, bool hasParentRenderThread, bool switchOn)
        => isDetachedChild && hasParentRenderThread && switchOn;

    /// <summary>Render thread: present this host's swapchain, non-blocking when <see cref="UsesNonBlockingPresent"/>. True when the
    /// frame is on its way to the glass; false when DXGI refused it (<see cref="ISwapchain.PresentNoWait"/>), in which case the
    /// frame is OWED: it stays in the back buffer, the latency credit stays held (nothing was queued for it to be spent on), and
    /// <see cref="DrainChildRenderSources"/> re-presents it on a later turn or a fresh publication supersedes it. A refused
    /// present is neither counted as a present nor acknowledged (<see cref="NotePresented"/>).</summary>
    private bool PresentFrame(in Threading.RenderFrame rf, bool composited)
    {
        if (UsesNonBlockingPresent(_isDetachedChild, _parentRenderThread is not null, EngineSwitches.NonBlockingSecondaryPresent))
        {
            if (!_swapchain.PresentNoWait())
            {
                _presentOwed = true;
                _owedFrame = rf;
                _owedComposited = composited;
                _childPace?.NoteSkipped();
                return false;
            }
        }
        else _swapchain.Present();
        _presentOwed = false;
        _renderPresentCount++;
        NotePresented(rf.PublishSeq);
        // Per target: this host's own GPU sample feeds this host's own depth policy, applied to this host's own
        // swapchain (a pop-out's GPU time never retunes the main window's present queue).
        ChoosePresentQueueDepth();
        if (composited) CompleteFrameCapture(_renderSlices, rf.PublishSeq);
        return true;
    }

    /// <summary>11.5 (threaded) — the video placement rides THIS present turn on the presenting thread, mirroring the sync path's
    /// after-present ordering (AppHost.Paint phase 11.5). Both GetVideoPresenter and every presenter call assert the render/submit
    /// thread when render-confined, so the apply MUST run here, not UI-side; the UI-side call at phase 11.5 is skipped whenever a
    /// render thread exists. F070: it applies the SNAPSHOT the presented frame carries (<see cref="Threading.SceneFramePublisher.VideoIntents"/>),
    /// never the live registry, moved by the hole's posed travel on this turn's composite (<see cref="VideoPosedHoles"/>), with the
    /// FRAME's scale (rf.Submit.Scale) rather than the live _window.Scale — the video lands under the hole of the frame it is
    /// presented with, not under whatever the UI has written since.
    /// <para>Its own method because a turn that elides the record still owes its CONTENT half: <paramref name="elided"/> applies only
    /// create / bind (a handle that arrived, a failed bind's retry); geometry and destroys belong to the turn that presents the
    /// matching hole, and the elided publication's own geometry was applied by the turn that recorded it.</para>
    /// <para>F080: the placement is APPLIED here but not committed. The turn's ONE device-level commit is
    /// <see cref="CommitVideoTurn"/> (<see cref="CommitVideoTurnAfterPresent"/>, the render loop's post-turn), after every child
    /// and then the parent drained, so the shared composition device is flushed once per turn instead of once per window. A turn that MOVES an
    /// already-placed surface commits at once instead (F070 Stage B), right after the present that carries the new hole and with no
    /// flush in between: the hole's flip and the video's new rect then become eligible for the same DWM composition.
    /// <paramref name="placement"/> false is a present that stood down: only releases are applied.</para></summary>
    private void DrainVideoForPresentTurn(in Threading.RenderFrame rf, bool placement = true, bool elided = false)
    {
        Threading.ThreadGuard.AssertRender();
        long videoStart = Stopwatch.GetTimestamp();
        _lastRenderScale = rf.Submit.Scale;   // the early (structural) drain places a new surface with the last presented frame's scale
        // A bound-readiness edge is published from this (render) thread; the UI loop may be blocked with nothing else
        // to wake it, so the poster drop / hole punch would stall. Wake is thread-safe. Deferred commit only while a render loop
        // exists to make it (every caller runs inside one of its turns).
        if (_device.GetVideoPresenter(_swapchain) is { } vp)
        {
            bool deferCommit = OwningRenderThread is not null;
            var scope = !placement ? FluentGpu.Media.VideoApplyScope.ReleasesOnly
                : elided ? FluentGpu.Media.VideoApplyScope.ContentOnly : FluentGpu.Media.VideoApplyScope.Full;
            var applier = VideoApplier;
            bool edge = applier.ApplyTurn(vp, _renderSeam.VideoIntents(rf), VideoPosedHoles(in rf), rf.Submit.Scale, scope, deferCommit);
            if (deferCommit && applier.LastTurnMovedGeometry)
            {
                _device.CommitVideoComposition();   // Stage B: this target's motion lands right after ITS present, not at the end of the turn
                edge |= applier.PublishCommitted();
            }
            if (edge) _window.Wake();
        }
        // F101: a settle hint armed for this turn is consumed WITHOUT blocking, and only after the placement commit above. The
        // render thread is shared with the pop-out, so it never sits in a DWM flush; the next turn's compositor-tick wake is
        // the settle.
        _swapchain.CompleteSettlePresent(blockUntilComposed: false);
        // Advisory, one way engine → PAL: a composited window defers ALL painting during an OS modal edge-resize,
        // which would leave this video child at its pre-resize geometry while the frame moves under it. Telling the
        // window it carries live video lets it keep a throttled keep-alive instead.
        _window.SetHasLiveVideo(_videoSurfaces.HasLiveSurface);
        _splitVideoMs = ToMs(Stopwatch.GetTimestamp() - videoStart);   // F244: the drain's share of this turn's work
    }

    /// <summary>The holes this frame's composite placed, with the pose it applied (F070): what the video placement follows. Empty for a
    /// frame that carries no scene (a finished command stream).</summary>
    private ReadOnlySpan<FluentGpu.Media.VideoPosedHole> VideoPosedHoles(in Threading.RenderFrame rf)
        => rf.HasScene ? _renderSeam.Scene(rf).Scene.Recording.PosedVideoHoles : default;

    /// <summary>Render thread, just before the present (F070 Stage B): adopt the frame's video snapshot and, when the apply that follows
    /// will MOVE a surface that is already on screen, arm the swapchain's geometry-motion present - a bounded wait for this frame's own
    /// GPU work, so the flip carrying the new hole and the Commit that moves the video reach DWM together. Armed only on a turn that moves
    /// video geometry (a drag, a scroll, a resize), never on steady playback, idle or a content-only turn.</summary>
    private void ArmGeometryMotionPresent(in Threading.RenderFrame rf)
    {
        if (_device.GetVideoPresenter(_swapchain) is null) return;   // nothing composited behind this target: no video to skew against
        if (VideoApplier.PrepareGeometry(_renderSeam.VideoIntents(rf), VideoPosedHoles(in rf), rf.Submit.Scale))
            _swapchain.HintGeometryMotionPresent();
    }

    private void RecoverDeviceAfterDump()
    {
        _renderSeam.InvalidateTarget();
        // RecoverDevice rebuilds EVERY swapchain, a detached child's included, so each child's retained pre-loss frame, owed
        // present and skip-submit baseline are as stale as the parent's (F099): a child has no DeviceLostCoordinator of its own,
        // this rendezvous is the only place it can hear about the loss.
        var children = Volatile.Read(ref _childRenderSources);
        for (int i = 0; i < children.Length; i++) children[i].InvalidateRenderStateAfterDeviceLoss();
        _deviceLostRecoveryCount++;
        DumpDeviceLostFrames(null, "async-render");
        _device.DumpDeviceLostDiagnostics(WriteDeviceLostLine);
        _device.RecoverDevice();
        // Popup create / retire work queued before (or during) the loss runs now, in order, against the rebuilt device: a Create
        // attempted on the lost one would have thrown, and nothing else will drain until the next publication.
        DrainPopupRenderActions();
        // Each child's own queued swapchain work likewise, and its UI half of the recovery: the child's UI thread never saw the
        // loss, so it is told to repaint its whole (now empty) rebuilt target.
        for (int i = 0; i < children.Length; i++)
        {
            children[i].DrainPopupRenderActions();
            children[i].Post(children[i].RepaintAfterDeviceRecovery);
        }
        ResetAdaptiveGpuGovernor();
    }

    /// <summary>Render thread, inside the parent's recover gate, detached child only: drop everything this child's render side
    /// remembers about the pre-loss target. The target-epoch bump makes the next publication repaint in full and
    /// <see cref="HasOwnRenderMotion"/> stop re-presenting the pre-loss frame; the elision baseline is cleared so a child with no
    /// fresh damage cannot skip the submit that fills its rebuilt swapchain. (A child records through its swapchain's direct
    /// route and owns no retained-tile table, so there is no tile ledger to reset.)</summary>
    internal void InvalidateRenderStateAfterDeviceLoss()
    {
        _renderSeam.InvalidateTarget();
        _lastRenderPresentedHash = 0;
        _renderSubmissionContinuity = default;
        _presentOwed = false;
        _slotDeferred = false;
        _deferredSinceQpc = 0;
    }

    /// <summary>UI thread, posted by the parent's recovery (<see cref="RecoverDeviceAfterDump"/>): the rebuilt target holds
    /// nothing, so repaint this child in full and wake it - a static pop-out may otherwise never publish again, leaving its
    /// rebuilt swapchain (and the DComp rebind that happens on present) empty. A no-op once the child has been reaped (its
    /// posts are forwarded and its scene is released).</summary>
    internal void RepaintAfterDeviceRecovery()
    {
        if (Volatile.Read(ref _postForward) is not null) return;
        _scene.MarkAllPaintDirty();
        _repaintTargetValid = false;   // the rebuilt target holds nothing — the next frame repaints in full (§13.1)
        _lastPresentedDrawListHash = 0;
        WakeFrame();
    }

    // Render thread only: the DIP->device scale of the last frame this host's present turn drained video for (0 = none yet).
    // What the early (structural) drain places a freshly created surface with, before this turn's own frame is chosen.
    private float _lastRenderScale;

    /// <summary>Render thread, parent host: the EARLY half of the video drain (F208), the render loop's <c>preTurn</c> hook. Runs
    /// at the top of every turn past the resize / device-lost gates (also a bare wake: a video handle arriving wakes the loop),
    /// BEFORE the primary's present-slot wait: for this host and every non-failed child it creates the surface and binds the
    /// handle of an entry that still needs one (<see cref="FluentGpu.Media.VideoPlacementApplier.ApplyStructural"/>, off the registry's content mailbox), so a
    /// pop-out's picture no longer waits behind another window's slot wait or for its own UI frame to be published. Only
    /// create and bind move early: <c>Place</c> moves of an existing surface and <c>Destroy</c> stay coupled to the UI frame's
    /// present (<see cref="DrainVideoForPresentTurn"/>), the two-clock tear lock. A parked video-only post (F098: the UI's wake was
    /// only its video pump, so it published no frame) is applied here too, off the retained frame's glass. O(hosts) when nothing needs it.</summary>
    private void DrainVideoStructuralPreTurn()
    {
        Threading.ThreadGuard.AssertRender();
        var list = Volatile.Read(ref _childRenderSources);
        bool any = HasEarlyVideoWork;
        for (int i = 0; !any && i < list.Length; i++) any = !list[i]._renderFailed && list[i].HasEarlyVideoWork;
        if (!any) return;
        // Popup swapchain work first: its visual edits ride the same device commit as the video's (DrainChildRenderSources runs it
        // again later in the turn, a cheap no-op by then).
        DrainPopupRenderActions();
        DrainVideoStructural();
        for (int i = 0; i < list.Length; i++)
        {
            var child = list[i];
            if (child._renderFailed) continue;
            child.DrainPopupRenderActions();
            child.DrainVideoStructural();
        }
        CommitVideoTurn(list);
    }

    /// <summary>Render thread: what the early video drain owes this host - a handle to create or bind, or a parked video-only post.</summary>
    private bool HasEarlyVideoWork => VideoApplier.HasStructuralWork || _renderSeam.HasVideoOnlyPost;

    // Render-thread scratch for a taken video-only post (F098); allocated on the first one.
    private FluentGpu.Media.VideoPresentIntent[]? _videoOnlyScratch;

    /// <summary>Render thread: this host's early video drain (see <see cref="DrainVideoStructuralPreTurn"/>). Waits for the first
    /// presented frame's scale; a failure follows the present path's own policy (a device loss is the recovery's, a child
    /// latches <see cref="_renderFailed"/>, anything else on the parent is a bug and rethrows).</summary>
    private void DrainVideoStructural()
    {
        bool videoOnly = _renderSeam.HasVideoOnlyPost;
        if (_lastRenderScale <= 0f || !(videoOnly || VideoApplier.HasStructuralWork)) return;
        try
        {
            if (_device.GetVideoPresenter(_swapchain) is { } vp)
            {
                bool edge = videoOnly && ApplyVideoOnlyPost(vp);
                // The content mailbox, not the live registry (F183): the handle, its sequence and any release - never geometry, which only a
                // publication (or the video-only post above) delivers (a surface created before its first publication is simply not placed yet).
                if (VideoApplier.ApplyStructural(vp, _lastRenderScale, deferCommit: true)) edge = true;
                if (edge) _window.Wake();
            }
        }
        catch (System.Exception ex) when (_asyncActive)
        {
            if (_device.NoteIfDeviceLost()) return;
            if (_parentRenderThread is null) throw;
            NoteChildRenderFailure(ex);
        }
    }

    /// <summary>Render thread (F098): apply the video snapshot the UI parked instead of publishing a frame. Nothing in the UI changed
    /// (that is what makes a post legal), so the glass still shows the retained frame's holes: the snapshot is placed against that
    /// frame's posed holes and scale exactly as a re-present of it would be, and a present that stood down applies releases only. An
    /// owed present (<see cref="_presentOwed"/>: the active frame was refused and is not on the glass yet) is held the same way, as a
    /// content-only adopt: its retry drain places the post's geometry when that frame lands. The
    /// device commit is the pre-turn's (<see cref="CommitVideoTurn"/>). Left parked (false, nothing taken) while no frame has been
    /// presented to this target yet - the next publication carries the snapshot. Returns true on a bound-readiness edge.</summary>
    private bool ApplyVideoOnlyPost(IVideoPresenter vp)
    {
        Threading.ThreadGuard.AssertRender();
        if (!_hasActiveRenderFrame || !_renderSeam.IsCurrentTarget(_activeRenderFrame)) return false;
        var scratch = _videoOnlyScratch ??= new FluentGpu.Media.VideoPresentIntent[FluentGpu.Media.VideoSurfaceRegistry.MaxSurfaces];
        if (!_renderSeam.TryTakeVideoOnly(scratch, out int count)) return false;
        // An owed present (non-blocking secondary) is held like a stood-down one: the active frame is the refused one, not yet on the
        // glass, so nothing is placed or destroyed against its holes. ContentOnly still adopts the snapshot (handle, release flag and
        // geometry with its newer Seq); the retry's Full drain places it against that frame's holes when it lands.
        var scope = _presentOwed ? FluentGpu.Media.VideoApplyScope.ContentOnly
            : _swapchain.LastPresentStoodDown ? FluentGpu.Media.VideoApplyScope.ReleasesOnly : FluentGpu.Media.VideoApplyScope.Full;
        bool edge = VideoApplier.ApplyTurn(vp, scratch.AsSpan(0, count), VideoPosedHoles(in _activeRenderFrame),
            _activeRenderFrame.Submit.Scale, scope, deferCommit: true);
        _window.SetHasLiveVideo(_videoSurfaces.HasLiveSurface);
        return edge;
    }

    /// <summary>Render thread, parent host: the turn's ONE device-level composition Commit (F080) and the readiness it makes
    /// true. Every presenter shares the device's one <c>IDCompositionDevice</c>, so a single commit flushes the parent's and every
    /// pop-out's tree in the same DWM frame; each registry then publishes which of its slots are bound AND composed, waking its
    /// UI loop on an edge. O(1) when nothing was applied since the last commit.</summary>
    private void CommitVideoTurn(AppHost[] children)
    {
        _device.CommitVideoComposition();
        if (VideoApplier.PublishCommitted()) _window.Wake();
        for (int i = 0; i < children.Length; i++)
            if (children[i].VideoApplier.PublishCommitted()) children[i]._window.Wake();
    }

    /// <summary>Render thread, failed detached child only (<see cref="_renderFailed"/>): clear the retry state a refused or
    /// deferred present left behind (it would keep <see cref="HasRenderMotion"/> true for a child that will never present
    /// again) and apply any pending video-surface intent for the last frame it drew, without touching the swapchain. Runs from
    /// <see cref="DrainChildRenderSources"/>, which has no catch on the shared render thread, so the drain is best-effort and
    /// NEVER throws: the video drain can itself be the work that latched <see cref="_renderFailed"/>, so a throw here is
    /// swallowed (logged once) and stops further drains for this child.</summary>
    internal void DrainVideoAfterRenderFailure()
    {
        _slotDeferred = false;
        _presentOwed = false;
        _deferredSinceQpc = 0;
        if (_failedVideoDrainStopped) return;
        try
        {
            // The content mailbox (releases, handles) over the geometry of the last frame this child drew: it publishes no more frames.
            if (_hasActiveRenderFrame && _device.GetVideoPresenter(_swapchain) is { } vp)
                VideoApplier.ApplyMailbox(vp, _activeRenderFrame.Submit.Scale, FluentGpu.Media.VideoApplyScope.Full, false,
                    VideoPosedHoles(in _activeRenderFrame));
        }
        catch (Exception ex)
        {
            // A device loss is the parent's recovery to handle (it resets this child); anything else is a drain that cannot
            // succeed, so stop retrying it every turn and say so once.
            if (_device.NoteIfDeviceLost()) return;
            _failedVideoDrainStopped = true;
            string line = $"[detached] failed child video drain stopped hwnd={_window.Handle.Value:X}: {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(line);
            Diag.Sink?.Invoke(line);
        }
    }
    /// <summary>Render thread only: a failed child's video drain threw once and is not retried (see
    /// <see cref="DrainVideoAfterRenderFailure"/>).</summary>
    private bool _failedVideoDrainStopped;

    private void ImportRecordingFeedback()
    {
        if (!_recordFeedback.TryAcquire(out var frame)) return;
        var feedback = MemoryMarshal.Read<Threading.RecordingFeedback>(_recordFeedback.Bytes(frame));
        _lastRecordStats = feedback.Stats;
        _lastRecordedCommandCount = feedback.CommandCount;
        _lastRenderRecordMs = feedback.RecordMs;
        _lastRecordedFeedbackScene = feedback.SceneSequence;
        var poseBytes = _recordFeedback.Bytes(frame)[System.Runtime.CompilerServices.Unsafe.SizeOf<Threading.RecordingFeedback>()..];
        var poses = MemoryMarshal.Cast<byte, CompositorAnimationPose>(poseBytes)[..feedback.PoseCount];
        _anim.ApplyCompositorFeedback(poses);
        foreach (ref readonly var pose in poses) if (pose.Done) { _frameNeeded = true; break; }
        _scene.Recording.ImportVideoRects(((ReadOnlySpan<RectF>)feedback.VideoRects)[..feedback.VideoCount]);
        // A popup stays hidden until the render thread recorded its first coherent scene publication AND that
        // publication's popup pass actually PRESENTED content into the popup's composition surface
        // (ISwapchain.HasPresentedContent). The sequence alone is not evidence of painted pixels: the feedback is
        // published whether or not the popup's own present landed, so revealing on the sequence could show the popup
        // as its frosted composition chrome with an empty content surface — the "empty flyout". The reveal is now
        // driven by the paint, and WakeReasons.PopupAnim keeps a frame owed until that paint happens.
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            var popup = _popupWindows[i];
            if (!popup.Window.IsShown && popup.Swapchain is { HasPresentedContent: true }
                && popup.FirstSceneSequence != 0 && popup.FirstSceneSequence <= feedback.SceneSequence)
                ShowPopupWindow(popup);
        }
    }

    private bool TryRecoverForegroundDeviceLost(Exception ex, int clicks)
    {
        if (!_device.NoteIfDeviceLost()) return false;
        _deviceLostRecoveryCount++;
        DumpDeviceLostFrames(ex, "foreground");
        _device.DumpDeviceLostDiagnostics(WriteDeviceLostLine);
        _device.RecoverDevice();
        ResetAdaptiveGpuGovernor();
        _scene.MarkAllPaintDirty();
        _repaintTargetValid = false;   // the rebuilt target holds nothing — the next frame repaints in full (§13.1)
        _needFullLayout = true;
        _lastPresentedDrawListHash = 0;
        _navThrottleFrames = PostRecoveryThrottleFrames;   // drip the re-realize upload burst so the fresh (weak) device doesn't re-hang (foreground path)
        _images.ReRealizeAllResident();
        _frameAfterPaint = true;
        LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, PresentFps = _presentFps, PresentedSequence = this.PresentedSequence, FrameMs = _frameMs };
        PublishFrameStats(LastStats);
        return true;
    }

    private void ResetAdaptiveGpuGovernor()
    {
        _gpuBoundEma = 0.0;
        _gpuBoundSampleSequence = 0;
        _gpuBoundLastSample = default;
        _gpuGovernorEngaged = false;
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
            _window.Scale, clicks, _pumpedEvents, keepAlive, resized, reconciled, layoutNeeded, transformWrote,
            maybeUnchanged, skipSubmit, _device.HasPendingUploads, _drawList.CommandCount, _drawList.Bytes.Length,
            _drawList.SortKeys.Length, _drawList.OpcodeStats, recordStats.NodesVisited, recordStats.DrawnNodeCount,
            recordStats.CulledNodeCount, recordStats.BlurCandidateCount, recordStats.BlurGroupCount,
            recordStats.EdgeFadeGroupCount, recordStats.RepaintDamage.Count, recordStats.RepaintDamage.FullReason, ToMs(tFlush - frameStart),
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
            WriteDeviceLostLine($"[device-lost]   draw cmds={f.CommandCount} bytes={f.CommandBytes} sort={f.SortKeyCount} nodes={f.NodesVisited}/{f.DrawNodeCount}/{f.CulledNodeCount} blur={f.BlurCandidateCount}/{f.BlurGroupCount} edgeFade={f.EdgeFadeGroupCount} repaint={f.RepaintRects}/{f.RepaintFull}");
            WriteDeviceLostLine($"[device-lost]   ms flush={f.FlushMs:0.###} layout={f.LayoutMs:0.###} anim={f.AnimMs:0.###} record={f.RecordMs:0.###} ops={f.OpcodeStats}");
        }
    }

    private static void WriteDeviceLostLine(string line) => Diag.Line(line);

    private bool _frameNeeded = true;        // a frame is required (reactive work pending, input, resize, …)
    private bool _frameAfterPaint;           // a wake arrived during paint → run another frame
    private bool _needFullLayout = true;     // first frame / resize / DPI / root structural change
    // Nav-burst upload bound (P1b): a full-tree-dirty + full-layout frame (a route/page change) opens a short decaying
    // window during which image uploads are throttled EXACTLY like a live scroll — the tight DecodeScheduler cap
    // (1 apply / 512 KiB per frame). A nav's album-art burst otherwise hits the GPU unthrottled right as it is also
    // doing transition work; this bounds that burst at any buffer depth (same rationale as the scroll throttle).
    private int _navThrottleFrames;          // >0 = window open; decays one per produced frame
    private const int NavThrottleWindowFrames = 10;
    // Post-recovery cooldown: recovery re-records the full frame AND ReRealizeAllResident restarts EVERY resident
    // image's decode → upload. Slamming all of that onto a just-recovered weak GPU in one frame is what re-hangs it
    // (the observed DEVICE_HUNG recovery loop: recover → full re-realize burst → hang again). A longer throttle
    // window than a normal nav makes those re-uploads drip through the DecodeScheduler cap (1 apply/frame) so the
    // fresh device warms up instead of re-hanging. Longer than nav because re-realize touches the whole resident set.
    private const int PostRecoveryThrottleFrames = 45;
    private bool _everLaidOut;               // suppress FLIP capture until the first layout (freshly-mounted nodes have no "before")
    private bool _wasParked;                 // previous frame's parked state (minimized OR hidden) — the un-park EDGE forces a repaint
    // Detached CHILD only: the DWM-cloak state (another virtual desktop, a shell transition), debounced by CloakParkGate and
    // sampled once per RunFrame. A cloaked pop-out presents nothing (Present stands down) yet keeps WS_VISIBLE, so without
    // this it kept painting + RenderMotion-presenting a window nobody can see, on the render thread shared with the main window.
    private CloakParkGate _cloakGate;
    private bool _cloakParked;
    // Any host (F118, UI thread): the window is completely covered, so it parks exactly like a minimized one. Two causes feed it
    // (UpdateCoverPark): a fullscreen, active detached child over the PRIMARY host (WindowCoverPolicy.Covers), and, for any window,
    // the union of the opaque top-level windows above it (WindowCoverPolicy.CoveredByWindows over the backend's win-event-driven
    // IPlatformWindow.CopyOccluderRectsPx). The backend wakes the loop on the events, and a cloak-style poll backs the un-park up.
    private bool _coverParked;
    private long _occlusionEpochSeen;      // the IPlatformWindow.OcclusionEpoch the cached OS verdict was computed for (0 = never / untracked)
    private bool _occludedByWindows;       // the cached OS-level verdict: covered by the union of the windows above it
    private RectF[]? _occluderRects;       // reused buffer for IPlatformWindow.CopyOccluderRectsPx (WindowCoverPolicy.MaxOccluders)
    // A cloak-parked child polls at this period: DWM raises no message when a window is un-cloaked, so unlike a minimized or
    // hidden window (whose restore IS a message) nothing would ever wake a loop that blocked until one.
    private const int CloakPollMs = 250;
    // Detached CHILD only (F115, UI thread): the pop-out window was created HIDDEN and is waiting for its first present
    // (DetachedRevealGate). While pending the host is exempt from the hidden-window park (and the production gate), so its own
    // first frame can land in the hidden window; TryRevealDetached shows the window exactly once. False on every other host.
    private bool _revealPending;
    // Detached CHILD only (F110, UI thread): the pop-out was PARKED warm (IDetachedVideoWindow.Park): hidden by the app's request
    // and kept alive for reuse, not closing. The host parks through the ordinary hidden-window gate (IsOsParked); this flag only
    // says the hide is deliberate, so the handle reports IsParked and Unpark knows the host is reusable. Cleared by Unpark.
    private bool _warmParked;
    private bool _revealTopmost;          // the always-on-top state to apply when the window is shown (kept current by the handle's SetTopmost)
    private long _revealDeadlineQpc;
    private long _openStartQpc;           // the QPC the open began (OpenDetachedWindow entry): FirstPresentMs counts from here
    private double _openFirstFrameMs = -1;   // the child's first RunFrame, timed once (-1 = not run yet)
    // F215: the QPC this host's first successful Present completed (render thread writes once, UI reads; 0 = none yet), and the
    // pop-out's first successful video bind (UI side: _firstVideoBindMs from the open start, -1 = none yet, once reported).
    private long _firstPresentQpc;
    private double _firstVideoBindMs = -1;
    private bool _firstVideoBindReported;
    // F110/F215: a warm-reused pop-out whose applier had already bound a surface keeps that bind (the parked slot stays bound), so no
    // NEW first bind will ever land; its time-to-first-video is the reveal, which is when the held picture becomes visible.
    private bool _bindCarriedOver;
    // The pop-out open cost split (F110): window and host-ctor stages are stamped by OpenDetachedWindow, the frame and present
    // stages when the reveal lands. OnRevealed is the handle's callback (cleared after it fires, like OnClosed).
    internal DetachedOpenTiming OpenTiming;
    internal Action<DetachedOpenTiming>? OnRevealed;
    /// <summary>F215: ms from the open start to the child's first SUCCESSFUL video bind (the presenter accepted a swap-chain handle for
    /// one of its surfaces), or -1 while none has landed. Set by <see cref="PollFirstVideoBind"/> on the UI thread.</summary>
    internal double FirstVideoBindMs => _firstVideoBindMs;
    /// <summary>Fired once, on the UI thread, with <see cref="FirstVideoBindMs"/> (cleared after it fires, like <see cref="OnRevealed"/>).</summary>
    internal Action<double>? OnFirstVideoBound;
    private WindowStateRelay _windowStateRelay;   // placement + visibility samples → AppHost.WindowStateChanged edges
    private bool _inPaint;
    private Size2 _lastSize;
    private float _lastScale;
    private float _lastZoom = 1f;            // last-published Viewport.Zoom (value-gate — EnsureSize compares it every frame, zero-alloc)
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
    private uint _dwmSampleSeqSeen;   // UI-private: the DWM sample whose deltas a FrameStats already carried
    private const double FpsWindowSeconds = 1.0;

    // Pacing → timestep coupling (fps consistency). The wait the loop used to pace INTO the current frame: 0 = display
    // rate; >0 = cadence-paced / HUD; -1 = blocked idle. A non-zero value means the frame clock's pending delta is a
    // STALE throttle/idle gap, not a real render interval — so Paint resyncs the clock before the anim tick when this
    // frame drives interactive or one-shot motion, killing the first-frame lurch on a scroll-start or a connected fly.
    private int _lastWaitMs;
    private HostWaitKind _lastWaitKind;   // which RecommendedWaitMsCore branch produced _lastWaitMs (present/pacing diagnosis)
    /// <summary>A host-wide frame-rate CEILING for motion, in fps; 0 (the default) = none. It is for the OS asking for
    /// less work — Windows Energy Saver; Chromium's Energy Saver caps the same way — and nothing else: the engine never
    /// throttles a visible window for losing focus (the GPUI-style <c>InactiveFrameIntervalMs</c> floor was deleted on
    /// 2026-10-03; no other reference engine throttles on focus) nor for motion it guesses is "ambient".
    /// <para>While set, every frame the loop would produce for motion — cadence rows, one-shot transitions, loops,
    /// <c>FrameClock.Tick</c> subscribers, image reveals, timers — is paced to at most this rate on the vblank lattice,
    /// uniformly, so nothing runs smooth beside something choppy (<see cref="CadencePacing.FlooredWaitMs"/>). Scroll,
    /// drag, touch and auto-repeat are never capped (<see cref="PowerCapNeverPace"/>), and input still DISPATCHES the
    /// moment it arrives — only frame production waits. UI thread only.</para></summary>
    public int PowerCapFps { get; set; }

    /// <summary>The frames <see cref="PowerCapFps"/> never paces: the GPU governor's interaction set
    /// (<see cref="GpuGovernorWake.NeverPace"/>) minus the frame-clock poller bit — a per-frame subscriber is exactly the
    /// continuous motion an energy-saver ceiling is meant to reach.</summary>
    internal const WakeReasons PowerCapNeverPace = GpuGovernorWake.NeverPace & ~WakeReasons.FrameClockPoller;

    /// <summary>Adaptive GPU pacing (default on): when measured whole-frame on-GPU execution proves the panel rate is
    /// unsustainable at this window size, pace continuous motion to a steady <see cref="GpuGovernorFps"/> instead of
    /// free-running into vblank misses. Measurement-driven and self-releasing (engage ≥
    /// <see cref="GpuGovernorEngageMs"/>, release ≤ <see cref="GpuGovernorReleaseMs"/>), so on a GPU that keeps up it
    /// never engages and costs one EMA update per frame. <c>false</c> removes the governor entirely — the escape hatch
    /// for a capture that must see the raw cadence. Wired from <c>AppOptions.AdaptiveGpuPacing</c>.</summary>
    public bool AdaptiveGpuPacing { get; set; } = true;

    /// <summary>Consume one coherent, completed whole-frame GPU execution sample into the adaptive governor. Returns
    /// true only for a new valid sample. Sequence 0 (unsupported), a repeated sequence, NaN/infinity and non-positive
    /// durations never update the EMA. A repeated sample may still retain an already-engaged decision while its target-
    /// local submit age and wall age remain bounded; <see cref="IsAdaptiveGpuSampleUsable"/> owns that separate law.
    /// Invalid NEW samples are consumed and fail open rather than preserving an old engaged decision indefinitely.
    ///
    /// Pure apart from its explicit state refs so VerticalSlice can lock the no-fence-fallback, consume-once and
    /// hysteresis laws without a D3D device.</summary>
    internal static bool TryAdvanceAdaptiveGpuGovernor(double executionMs, ulong sequence,
        ref ulong consumedSequence, ref double emaMs, ref bool engaged)
    {
        if (sequence == 0 || sequence == consumedSequence) return false;
        consumedSequence = sequence;
        if (!double.IsFinite(executionMs) || executionMs <= 0.0)
        {
            emaMs = 0.0;
            engaged = false;
            return false;
        }

        double prior = double.IsFinite(emaMs) && emaMs > 0.0 ? emaMs : 0.0;
        emaMs = prior * (1.0 - GpuGovernorAlpha) + executionMs * GpuGovernorAlpha;
        if (engaged)
        {
            if (emaMs <= GpuGovernorReleaseMs) engaged = false;
        }
        else if (emaMs >= GpuGovernorEngageMs)
        {
            engaged = true;
        }
        return true;
    }

    /// <summary>Can a previously published target-local sample still support the current governor decision? Freshness
    /// controls EMA mutation; age controls decision validity. A repeated sequence remains usable while BOTH its same-
    /// target submit age and wall age are bounded, then fails open. Pure for deterministic policy gates.</summary>
    internal static bool IsAdaptiveGpuSampleUsable(in GpuRenderSample sample, long nowQpc,
        ulong maxSubmitAge, long maxWallAgeTicks)
    {
        if (sample.Sequence == 0 || !double.IsFinite(sample.ExecutionMs) || sample.ExecutionMs <= 0.0
            || sample.SubmitAge > maxSubmitAge || sample.PublishedQpc <= 0 || nowQpc < sample.PublishedQpc
            || maxWallAgeTicks < 0)
            return false;
        return nowQpc - sample.PublishedQpc <= maxWallAgeTicks;
    }

    /// <summary>Apply the complete adaptive-GPU sample policy. A usable repeated sequence does not touch the EMA but
    /// retains the current hysteresis decision. Unsupported/invalid/expired evidence clears the decision and its EMA.
    /// The return value alone authorizes the adaptive wait; callers must still apply the input/scroll/grace gates.</summary>
    internal static bool EvaluateAdaptiveGpuSample(in GpuRenderSample sample, long nowQpc,
        ulong maxSubmitAge, long maxWallAgeTicks, ref ulong consumedSequence, ref double emaMs, ref bool engaged)
    {
        if (!IsAdaptiveGpuSampleUsable(in sample, nowQpc, maxSubmitAge, maxWallAgeTicks))
        {
            emaMs = 0.0;
            engaged = false;
            return false;
        }

        _ = TryAdvanceAdaptiveGpuGovernor(sample.ExecutionMs, sample.Sequence,
            ref consumedSequence, ref emaMs, ref engaged);
        return engaged;
    }

    /// <summary>Evaluate a target-local sample read while tolerating a transient unavailable read during the backend's
    /// seqlock publication. The last coherent sample remains bounded by the exact same submit/wall-age policy; a backend
    /// that has never supplied one therefore stays fail-open.</summary>
    internal static bool EvaluateAdaptiveGpuRead(bool sampleAvailable, in GpuRenderSample sample, long nowQpc,
        ulong maxSubmitAge, long maxWallAgeTicks, ref GpuRenderSample cachedSample,
        ref ulong consumedSequence, ref double emaMs, ref bool engaged)
    {
        if (sampleAvailable) cachedSample = sample;
        return EvaluateAdaptiveGpuSample(in cachedSample, nowQpc, maxSubmitAge, maxWallAgeTicks,
            ref consumedSequence, ref emaMs, ref engaged);
    }

    // Adaptive GPU governor: when on-GPU execution genuinely cannot sustain the panel rate at the current size
    // (a maximized frame whose command list executes in ~14ms), pace CONTINUOUS animation (playhead/shimmer) to a
    // steady GpuGovernorFps instead of free-running the loop into vblank-misses. A steady 60 beats a jittery 60 and
    // halves GPU/power; it NEVER engages for latency-sensitive frames (no added input/scroll latency) and routes
    // through the same Resync-exempt CadencePacing wait as the cadence branch so it can't trip the frozen-anim clock
    // guard. DEFAULT ON (AppHost.AdaptiveGpuPacing / AppOptions.AdaptiveGpuPacing): on a fast GPU the EMA stays under
    // budget so it NEVER engages; it only acts when the GPU is genuinely bound, turning a thrashing 60 into a steady one.
    /// <summary>The sustainable cadence the adaptive-GPU governor paces to once engaged (fps). Not app policy and not
    /// a row's cadence: the rate a GPU-bound frame can actually hold.</summary>
    private const int GpuGovernorFps = 30;

    private double _gpuBoundEma;            // smoothed fresh whole-frame GPU execution samples (never fence waits)
    private ulong _gpuBoundSampleSequence;  // last completed device sample consumed into the EMA
    private GpuRenderSample _gpuBoundLastSample; // last coherent sample from THIS host's swapchain; TTL-bounded on read contention
    private bool _gpuGovernorEngaged;
    internal const double GpuGovernorEngageMs = 10.0;
    internal const double GpuGovernorReleaseMs = 8.0;   // hysteresis: don't chatter around the engage threshold
    private const double GpuGovernorAlpha = 0.15;
    internal const ulong GpuGovernorMaxSubmitAge = 7;   // includes the normal FRAME_COUNT=3 resolve lag + bounded slack
    internal const int GpuGovernorSampleTtlMs = 250;
    private static readonly long GpuGovernorSampleTtlTicks = (long)(GpuGovernorSampleTtlMs * (Stopwatch.Frequency / 1000.0));
    /// <summary>The only wake bits that carry a FUTURE due time: an animation row and the caret both answer
    /// "how long until my next frame?" (<c>AnimEngine.NextDueMs</c> / <c>CaretBlinker.NextDueMs</c>), and both are set
    /// ONLY when that answer is ≤ 0. Every other bit means "due now" — input, scroll, images, timers, video, popups,
    /// virtual refill — so a frame carrying one of them can never be paced into the future, and the cadence branch
    /// tests for exactly their absence. This one mask replaced the whole inferred `LatencySensitiveWake` /
    /// `ImageWake` / scroll-grace / mount-grace classifier: a source that must run at the panel rate now says so with
    /// <c>Cadence.Display</c> instead of the host guessing from a bitmask.</summary>
    private const WakeReasons CadenceWake = WakeReasons.Anim | WakeReasons.Caret;

    // Modal-loop keep-alive paints must still run when any of these wake bits are set — even if autonomous animation is
    // also live (playback seek ticker). Without this mask the modal-loop nothing-due bail swallowed warming virtual
    // lists mid-drag (detail-resize-flicker fix).
    private const WakeReasons ModalLoopEssentialWake =
        WakeReasons.FrameNeeded | WakeReasons.RuntimePending | WakeReasons.ScrollAnim |
        WakeReasons.DragDropWork | WakeReasons.DragActive | WakeReasons.GestureHold | WakeReasons.TouchPress |
        WakeReasons.PopupAnim | WakeReasons.ImagesPending | WakeReasons.ImageReady | WakeReasons.ImageCrossfades | WakeReasons.Orphans |
        // An explicit UI frame-clock poller or a queued native-video hand-off must not be swallowed by a modal loop.
        WakeReasons.FrameClockPoller | WakeReasons.FrameClockPaceable | WakeReasons.VideoPumpPending |
        // A due frame-clock timer (a debounce/timeout/interval) must still fire while the user drags/resizes the window.
        WakeReasons.Timer |
        // …and a due pinned-leftover image restart (T10) is the same kind of deadline.
        WakeReasons.ImageLeftoverDue |
        // A KeepAlive unpark replay still in flight is essential so the modal bail cannot starve it.
        WakeReasons.WarmingVirtuals;
    private static bool NoEssentialModalWakeReasons(WakeReasons reasons) => (reasons & ModalLoopEssentialWake) == 0;
    // Dynamic-text (HUD) intern-on-change cache, indexed by (int)DynamicTextKind (None..FrameMs = 0..5). Each slot
    // holds the last DISPLAYED quantized value (the int fps / int cmd|draw|cull / 0.1-rounded ms — exactly the display
    // granularity) and the StringId it interned to (the host holds ONE ref per cached id). When a kind's quantized
    // value is unchanged we reuse the cached id with no ToString and no Intern — so a jittering readout that rounds to
    // the same number produces zero string churn and burns no new ids; when ALL five are unchanged the per-node scan
    // is skipped entirely. Sentinel _dynTextQuant=long.MinValue ⇒ "not computed yet" (first frame always interns).
    private readonly long[] _dynTextQuant = InitDynTextQuant();
    private readonly StringId[] _dynTextId = new StringId[7];
    private static long[] InitDynTextQuant() { var a = new long[7]; Array.Fill(a, long.MinValue); return a; }
    // The frame's clear colour. A detached child owns its Mica active/inactive swap HOST-LOCALLY (_childWindowBackground):
    // Theme.WindowBackground is process-global, so a child writing it flipped the MAIN window's backdrop on every focus change.
    private ColorF Clear => _childWindowBackground ?? Theme.WindowBackground;
    private ColorF? _childWindowBackground;   // detached child only: Mica-inactive fallback (null => follow the global)
    private bool _micaWindow;                 // FluentApp set WindowBackground=Transparent (a Mica window); a child inherits its parent's

    public SceneStore Scene => _scene;
    public AnimEngine Animation => _anim;
    /// <summary>The host-owned video-surface intent buffer (published on <c>VideoCompositor.Current</c>). A media player
    /// façade writes surface rect/visibility/handle here; the host drains it into the render-thread presenter at phase 11.</summary>
    public FluentGpu.Media.VideoSurfaceRegistry VideoSurfaces => _videoSurfaces;

    // ── detached video window (the pop-out mini-player) ──────────────────────────────────────────────────────────────

    /// <summary>Open a detached, movable/resizable, (by default) always-on-top top-level window hosting
    /// <see cref="DetachedWindowRequest.Content"/> in its OWN composited window + AppHost + swapchain + video presenter.
    /// Reuses the full frame loop (this is a real second AppHost sharing the device/fonts/strings/images), ticked by the
    /// parent loop on the same UI thread via <see cref="TickDetachedHosts"/>, with its frames presented by the parent's single
    /// render thread when there is one (see docs/design/subsystems/threading-render-seam.md §1, "Detached children"). Returns
    /// null when unavailable: a child host (no recursion), headless, or a backend without secondary swapchains. The async
    /// render path is NOT a reason: a child routes its present through the parent's render thread. Host-wired to
    /// <c>InputHooks.OpenDetachedWindow</c>.</summary>
    public IDetachedVideoWindow? OpenDetachedWindow(DetachedWindowRequest request)
    {
        // Async is NO LONGER excluded: a detached child routes its present through THIS (the parent's) single render thread
        // (AttachChildRenderSource + _parentRenderThread), so there is never a second submit/present owner on the shared,
        // render-confined device. Still unavailable on a child host (no recursion), headless, or a backend without secondaries.
        if (_isDetachedChild || _isHeadless || !_device.SupportsSecondarySwapchains || request.Content is null)
            return null;
        long openStartQpc = Stopwatch.GetTimestamp();
        float scale = _window.Scale;
        // WindowDesc takes PIXELS: the request is DIP, so scale it here or a 150% display opens the window at 2/3 size.
        // CustomFrame: true — a detached video pop-out is borderless like every other Wavee window; without it Win32
        // always creates WS_OVERLAPPEDWINDOW and the OS caption (icon/title/min/max/close) shows on top of the video.
        var desc = new WindowDesc(request.Title,
            new Size2(request.InitialSizeDip.Width * scale, request.InitialSizeDip.Height * scale), scale,
            Composited: true, CustomFrame: true, SkipDropAndTouchpad: true);   // a video pop-out takes no file drop (F110)
        var win = _app.CreateWindow(desc);

        // A 16:9-ish client floor so the mini-player can never be dragged down to an unusable sliver (caller-overridable).
        var minDip = request.MinClientSizeDip;
        win.SetMinClientSizePx(minDip.Width > 0f && minDip.Height > 0f
            ? new Size2(minDip.Width * scale, minDip.Height * scale)
            : new Size2(320f * scale, 180f * scale));

        // A RESTORED placement wins (the user put it there last time), clamped into the work area of the monitor nearest
        // to it — so a window remembered on a display that has since been unplugged still opens somewhere visible instead
        // of off-screen. Otherwise open at the bottom-right of the parent's monitor (a picture-in-picture home), fully
        // on-screen, instead of the CW_USEDEFAULT cascade. Falls back to CW_USEDEFAULT when the work area is unavailable
        // (headless / query failure → RectF.Infinite).
        var restored = request.InitialBoundsPx;
        bool haveRestored = restored.W > 1f && restored.H > 1f;
        var work = _app.GetWorkArea(haveRestored
            ? new Point2(restored.X + restored.W * 0.5f, restored.Y + restored.H * 0.5f)
            : OwnerMonitorAnchorPx());
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

        // The window stays HIDDEN (F115): it is composited (no redirection bitmap), so showing it before its swapchain has a
        // presented frame put an empty or see-through topmost rectangle on screen for the whole open. The host reveals it
        // (TryRevealDetached) once its own first present has landed. GetWindowRect / GetClientRect, and with them the
        // chrome measurement below, work on a hidden HWND.
        // SetBoundsPx above sized the OUTER rect to the requested CLIENT size, so the caption + borders ate into the
        // content (a 480×270 request produced a ~470×230 client). Now that the window exists its chrome is measurable:
        // grow the outer rect by that difference, anchored at the bottom-right corner it was placed on. A restored
        // placement is already an outer rect the user chose, so it is left alone.
        if (!haveRestored)
        {
            var outer = win.OuterBoundsPx;
            var client = win.ClientSizePx;
            float dw = outer.W - client.Width, dh = outer.H - client.Height;
            if (outer.W > 0f && client.Width > 0f && (dw > 0f || dh > 0f))
                win.SetBoundsPx(new RectF(MathF.Max(work.IsInfinite ? outer.X - dw : work.X, outer.X - dw),
                                          MathF.Max(work.IsInfinite ? outer.Y - dh : work.Y, outer.Y - dh),
                                          outer.W + dw, outer.H + dh));
        }

        // Create the host ONLY AFTER the window is created + sized, so its swapchain, first layout, and published
        // Viewport.Size all use the FINAL client size. A host constructed before SetBoundsPx reads a 0×0 /
        // stale ClientSizePx → its scene root lays out at 0×0 and the composited swapchain presents nothing (the
        // detached window then renders fully transparent, and the idle loop spins on the broken window).
        long ctorStartQpc = Stopwatch.GetTimestamp();
        var child = new AppHost(_app, win, _device, _fonts, _strings, request.Content, images: _images,
            compositeSwapchain: true, isDetachedChild: true, parentRenderThread: _renderThread);
        long ctorEndQpc = Stopwatch.GetTimestamp();
        // Arm the reveal BEFORE the child can be ticked: from here its host is exempt from the hidden-window park until
        // TryRevealDetached shows the window after its first present (or the timeout).
        child.BeginDetachedReveal(request.AlwaysOnTop, openStartQpc,
            QpcToMs(ctorStartQpc - openStartQpc), QpcToMs(ctorEndQpc - ctorStartQpc));
        AdoptDetachedChild(child);
        // Register the child as a render source for the parent's render loop (a no-op reader when there is no render thread —
        // the pure single-thread parent leaves the child on the inline present path). A copy-on-write publish: no park, and
        // an in-flight DrainChildRenderSources keeps the snapshot it took.
        AttachChildRenderSource(child);
        WakeFrame();
        return CreateDetachedHandle(child, win);
    }

    /// <summary>The live handle the app drives a pop-out through. Internal: the Engine.Tests seam (<c>OpenDetachedWindow</c>
    /// itself refuses a headless host), beside <see cref="AdoptDetachedChild"/>.</summary>
    internal IDetachedVideoWindow CreateDetachedHandle(AppHost child, IPlatformWindow window) => new DetachedWindowHandle(this, child, window);

    private static double QpcToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Detached CHILD, UI thread: arm the reveal of a pop-out window that was created hidden (F115). Records the open
    /// cost stages measured so far (window creation and host construction, F110) and the always-on-top state to apply when the
    /// window is shown. Until <see cref="TryRevealDetached"/> shows it the host is not parked and not production-gated.
    /// Internal: the Engine.Tests seam, beside <see cref="AdoptDetachedChild"/>.</summary>
    internal void BeginDetachedReveal(bool alwaysOnTop, long openStartQpc, double windowCreateMs, double hostCtorMs,
                                      int timeoutMs = DetachedRevealGate.TimeoutMs)
    {
        _revealPending = true;
        _revealTopmost = alwaysOnTop;
        _openStartQpc = openStartQpc;
        _revealDeadlineQpc = DetachedRevealGate.DeadlineQpc(Stopwatch.GetTimestamp(), timeoutMs);
        _openFirstFrameMs = -1;
        OpenTiming = new DetachedOpenTiming(windowCreateMs, hostCtorMs, 0, 0, false);
    }

    /// <summary>Detached CHILD, UI thread, once per tick after its frame: show the hidden pop-out window the first time its own
    /// swapchain reports a presented frame, or when <see cref="DetachedRevealGate.TimeoutMs"/> has passed without one.
    /// Shown exactly once: the pending flag clears first, so a re-entrant tick (a WM_PAINT the show provokes) cannot show
    /// it twice. Returns true on the tick that revealed it.</summary>
    internal bool TryRevealDetached()
    {
        if (!_revealPending) return false;
        long now = Stopwatch.GetTimestamp();
        bool presented = _swapchain.HasPresentedContent;
        if (!DetachedRevealGate.Due(presented, now, _revealDeadlineQpc)) return false;
        _revealPending = false;
        _window.Show();
        if (_revealTopmost) _window.SetTopmost(true);
        // Only the FIRST composited present is exempt from the IsHwndCovered stand-down, so a motion present, or the frame
        // published between that first present and this Show, was dropped while the window was hidden. Re-present now that it
        // is visible, or the occluded state (and a stale frame) lingers until the occlusion probe.
        if (_swapchain.IsOccluded) RequestFullRepaintOnce();
        // The render thread stamped the first successful Present itself (NotePresented); FirstPresentMs is when the UI OBSERVED one, so
        // the two differ by the reveal lag a busy UI or an async render thread adds (-1 here: the reveal came from the timeout).
        long presentedQpc = Volatile.Read(ref _firstPresentQpc);
        double renderPresentMs = presented && presentedQpc != 0 ? Math.Max(0.0, QpcToMs(presentedQpc - _openStartQpc)) : -1.0;
        var timing = new DetachedOpenTiming(OpenTiming.WindowCreateMs, OpenTiming.HostCtorMs,
            Math.Max(0.0, _openFirstFrameMs), QpcToMs(now - _openStartQpc), TimedOut: !presented, RenderPresentMs: renderPresentMs);
        OpenTiming = timing;
        // Always-on, one line per open: the split that the app's own log used to guess at (window vs. host vs. first frame vs.
        // first present), plus whether the reveal was earned by a presented frame or forced by the timeout.
        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[detached] reveal hwnd={_window.Handle.Value:X} windowMs={timing.WindowCreateMs:F1} ctorMs={timing.HostCtorMs:F1} firstFrameMs={timing.FirstFrameMs:F1} firstPresentMs={timing.FirstPresentMs:F1} renderPresentMs={timing.RenderPresentMs:F1} timedOut={(timing.TimedOut ? 1 : 0)}"));
        var cb = OnRevealed;
        OnRevealed = null;
        cb?.Invoke(timing);
        return true;
    }

    /// <summary>Detached CHILD, UI thread: run one frame, timing the FIRST one (the full reconcile, layout and record of the
    /// pop-out's tree - the stage the app's old cost log never saw), then give the reveal its chance.</summary>
    private void RunDetachedChildFrame()
    {
        if (_revealPending && _openFirstFrameMs < 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            RunFrame();
            _openFirstFrameMs = QpcToMs(Stopwatch.GetTimestamp() - t0);
        }
        else RunFrame();
        TryRevealDetached();
        PollFirstVideoBind();
    }

    /// <summary>Detached CHILD, UI thread, once per tick after its frame (F215): the first time this child's video applier reports a
    /// successful bind, stamp the pop-out's time-to-first-video (from the open start), log it as one always-on line and fire
    /// <see cref="OnFirstVideoBound"/>. The reveal only proves the child presented ITS frame; the picture is behind the bind, which
    /// follows the native handle and can land well after the reveal. A cheap flag test once reported (or for a host that never
    /// opened as a pop-out).</summary>
    internal void PollFirstVideoBind()
    {
        if (_firstVideoBindReported || _openStartQpc == 0) return;
        double bindMs;
        if (_bindCarriedOver)
        {
            if (_revealPending) return;   // the held picture is not visible before the reveal
            bindMs = Math.Max(0.0, OpenTiming.FirstPresentMs);
        }
        else
        {
            long bindQpc = Volatile.Read(ref _videoApplier)?.FirstBindQpc ?? 0;
            if (bindQpc == 0) return;
            bindMs = Math.Max(0.0, QpcToMs(bindQpc - _openStartQpc));
        }
        _firstVideoBindReported = true;
        _firstVideoBindMs = bindMs;
        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[detached] first.video.bind hwnd={_window.Handle.Value:X} ms={_firstVideoBindMs:F1} afterRevealMs={(OpenTiming.FirstPresentMs > 0.0 ? _firstVideoBindMs - OpenTiming.FirstPresentMs : -1.0):F1}"));
        var cb = OnFirstVideoBound;
        OnFirstVideoBound = null;
        cb?.Invoke(_firstVideoBindMs);
    }

    /// <summary>The screen point (physical px) that names the monitor THIS window is on, for placing a window it opens: the
    /// centre of its outer rect, so a window straddling two displays resolves to the one holding most of it (the client
    /// origin is its top-left corner, which names the neighbour whenever the window hangs over an edge). A minimized window has
    /// no usable rect (it reads as its parking spot), so it falls back to the client origin, as before.</summary>
    private Point2 OwnerMonitorAnchorPx()
    {
        var outer = _window.OuterBoundsPx;
        if (_window.State != WindowState.Minimized && outer.W > 1f && outer.H > 1f)
            return new Point2(outer.X + outer.W * 0.5f, outer.Y + outer.H * 0.5f);
        return _window.ClientOriginPx;
    }

    /// <summary>Parent, UI thread (F110): park a live, revealed pop-out warm instead of closing it. Leaves fullscreen first (the
    /// platform restores the windowed rect), delivers a pending settled-bounds change, then hides the window: the child host
    /// parks through the ordinary hidden-window gate (no reconcile, layout, record or present) while its tree and swapchain stay
    /// alive, so a reopen skips the window, swapchain and mount cost and the close skips the teardown WaitForGpu. The child stays
    /// in <see cref="_detachedHosts"/> (the parent's Dispose and the reaper still own it). Returns false when it cannot be parked.</summary>
    internal bool ParkDetachedChild(AppHost child)
    {
        if (child._warmParked) return !child._window.IsClosed;
        if (child._window.IsClosed || child._revealPending || child.RenderFailed || !_detachedHosts.Contains(child)) return false;
        var w = child._window;
        if (w.IsFullscreen) w.SetFullscreen(false);
        child.FlushPendingBoundsChange();
        child._warmParked = true;
        w.Hide();
        Diag.Line(string.Create(CultureInfo.InvariantCulture, $"[detached] park hwnd={w.Handle.Value:X}"));
        return true;
    }

    /// <summary>Parent, UI thread (F110): reuse a parked pop-out for a new open. Applies the request's restored bounds (clamped into
    /// the work area of the monitor nearest to it, exactly as a fresh open does) and always-on-top state, retitles the window and
    /// re-arms the reveal gate, so the window is shown on the child's next frame (its swapchain already presented, so the gate
    /// opens at once) and <see cref="OnRevealed"/> fires then. The parked tree is reused as it is. Returns false when the child is
    /// not parked or is no longer reusable (the caller builds a new one).</summary>
    internal bool UnparkDetachedChild(AppHost child, DetachedWindowRequest request)
    {
        if (!child._warmParked || child._window.IsClosed || child.RenderFailed || !_detachedHosts.Contains(child)) return false;
        var w = child._window;
        var restored = request.InitialBoundsPx;
        if (restored.W > 1f && restored.H > 1f)
        {
            var work = _app.GetWorkArea(new Point2(restored.X + restored.W * 0.5f, restored.Y + restored.H * 0.5f));
            if (!work.IsInfinite)
            {
                float rw = MathF.Min(restored.W, work.W), rh = MathF.Min(restored.H, work.H);
                float rx = MathF.Min(MathF.Max(restored.X, work.X), work.X + work.W - rw);
                float ry = MathF.Min(MathF.Max(restored.Y, work.Y), work.Y + work.H - rh);
                w.SetBoundsPx(new RectF(rx, ry, rw, rh));
            }
            else w.SetBoundsPx(restored);
        }
        w.SetTitle(child._strings.Intern(request.Title));
        w.SetTopmost(request.AlwaysOnTop);
        child._warmParked = false;
        // A fresh open re-measures time-to-first-video (and re-fires OnFirstVideoBound, which the app sets again on reuse) from the
        // new open start; a bind the parked applier already holds is not re-stamped, so it counts from the reveal (PollFirstVideoBind).
        child._firstVideoBindReported = false;
        child._firstVideoBindMs = -1;
        child._bindCarriedOver = (Volatile.Read(ref child._videoApplier)?.FirstBindQpc ?? 0) != 0;
        // From here the host is exempt from the hidden-window park (like a fresh open) until TryRevealDetached shows the window
        // after its next frame; the stale last frame of the previous session is all the swapchain holds until that frame presents.
        child.BeginDetachedReveal(request.AlwaysOnTop, Stopwatch.GetTimestamp(), 0.0, 0.0);
        child.RequestFullRepaintOnce();
        Diag.Line(string.Create(CultureInfo.InvariantCulture, $"[detached] unpark hwnd={w.Handle.Value:X}"));
        return true;
    }

    /// <summary>Track a freshly built detached child so <see cref="TickDetachedHosts"/> ticks and reaps it. The pop-out shares
    /// the main window's backdrop material, so it inherits the Mica flag. Internal: the Engine.Tests detach/reap seam
    /// (<c>OpenDetachedWindow</c> itself refuses a headless host).</summary>
    internal void AdoptDetachedChild(AppHost child)
    {
        child._micaWindow = _micaWindow;
        child._parentHost = this;
        _detachedHosts.Add(child);
    }

    /// <summary>The frame clear colour (test seam for the host-local Mica backdrop).</summary>
    internal ColorF ClearForTest => Clear;

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
                ReapDetachedChild(child);
                continue;
            }
            child.RunDetachedChildFrame();
            child.SampleDetachedBounds();
        }
    }

    /// <summary>One beat of the OS modal move/size loop THIS window is in (<see cref="IPlatformWindow.ModalLoopTick"/>, F093). That
    /// loop runs inside a DispatchMessage on the one UI thread, so the frame loop that normally ticks every window is suspended
    /// until mouse-up: the main window used to freeze (progress, lyrics, hover, posts) for the whole of a pop-out drag, and the
    /// pop-out while the main window was dragged. Each beat therefore also paints the OTHER windows of the process, through the
    /// keep-alive <see cref="Paint"/> ONLY: never <see cref="RunFrame"/> (it pumps and dispatches messages, which must not nest
    /// inside another window's dispatch), and Paint's own modal idle skip makes a peer with nothing awake cost nothing. Throttled
    /// to ~30 Hz by ONE stamp on the root host, so the dragged window's timer and its WM_SIZE together cost one round per interval.</summary>
    private void OnModalLoopTick()
    {
        var root = _parentHost ?? this;
        if (ModalPaintThrottle.ShouldSkipPeerTick(Environment.TickCount64, ref root._modalPeerLastMs)) return;
        root.PaintModalPeers(this);
    }

    /// <summary>Root host: paint the root and every live detached child except <paramref name="source"/> (the window in the loop,
    /// which paints itself). Index loop over the live count: a paint may run app code that opens a pop-out.</summary>
    private void PaintModalPeers(AppHost source)
    {
        PaintModalPeer(source);
        for (int i = 0; i < _detachedHosts.Count; i++) _detachedHosts[i].PaintModalPeer(source);
    }

    private void PaintModalPeer(AppHost source)
    {
        if (!ModalPaintThrottle.ShouldPaintPeer(ReferenceEquals(this, source), _window.IsClosed, IsParked, _window.InModalLoop)) return;
        (_parentHost ?? this)._modalPeerPaints++;
        Paint(0, keepAlive: true);
        // An unrevealed pop-out's first frame lands through here too when the main window is the one being dragged: reveal it
        // now rather than after the loop ends. Idempotent and a no-op on every other host.
        if (_revealPending) TryRevealDetached();
    }

    /// <summary>Test-only (F093): peer paints the modal-loop ticks have run so far (root host).</summary>
    internal long ModalPeerPaintsForTest => (_parentHost ?? this)._modalPeerPaints;

    /// <summary>UI thread: tear a closed pop-out down. App-visible work first, OUTSIDE any park (it runs app code); then ONE
    /// render-thread rendezvous (F117/F110) that unregisters the child as a render source and disposes it, instead of one park
    /// for the detach and a second inside <see cref="Dispose"/>.</summary>
    private void ReapDetachedChild(AppHost child)
    {
        // Persist a pending resize/move first: the user closing the window with the OS chrome lands HERE (not in
        // DetachedWindowHandle.Close), and the frame-stillness settle may never have run for a near-frameless
        // video window. Uses the last sampled rect since the OS window is already gone.
        child.FlushPendingBoundsChange();
        // Fire the closed-callback once, on this (the UI+render) thread, before teardown. Programmatic Close()
        // also lands here (WM_CLOSE → IsClosed), so this single reap site gives exactly-once for free.
        // From here a post aimed at the child (a MediaPlayer continuation that captured its poster, a worker's
        // HostDispatch.Post) must reach a live queue: forward to THIS host and hand over what is already queued.
        child.ForwardPostsTo(_uiPoster);
        if (!child._onClosedFired) { child._onClosedFired = true; var cb = child.OnClosed; child.OnClosed = null; cb?.Invoke(); }
        child.ForwardPostsTo(_uiPoster);   // the callback may have posted too
        // The component tree unmounts HERE, before the park: its cleanups are app code (they may touch the render seam
        // themselves, and a nested Quiesce is illegal), and the render thread keeps presenting other windows meanwhile.
        child.PrepareDispose();
        // ONE rendezvous for the whole teardown. Stop the render thread from touching this child's seam/swapchain, then release
        // them, inside the same park: no in-flight DrainChildRenderSources present can be mid-flight against the swapchain we
        // are about to release. A null thread means the pure single-thread inline path (the UI thread is the sole device owner).
        var rt = _renderThread ?? child._parentRenderThread;
        rt?.Quiesce();
        try
        {
            RemoveChildRenderSource(child);
            child.ReleaseRenderResourcesParked();
        }
        finally { rt?.Resume(); }
        child.FinishDispose();   // the window and the process-wide seams: UI work, after the render thread is running again
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

    /// <summary>Report a pending bounds change NOW, without waiting for the frame-stillness settle. Called when a detached
    /// window is closing.
    /// <para>Why this is needed: <see cref="SampleDetachedBounds"/> only advances while frames are being produced, and a
    /// detached VIDEO window is very nearly frameless by design — DirectComposition presents the decoded video
    /// independently, so an idle window with no UI animation can go long stretches without a single host frame. The
    /// settle counter then never reaches 10 and the resize is never persisted at all: resize the pop-out, let the next
    /// track have no video (the window closes), and it reopens at the old size. The close IS the settle signal.</para></summary>
    private void FlushPendingBoundsChange()
    {
        if (!_boundsDirty || BoundsChanged is null) return;
        var now = _window.OuterBoundsPx;
        if (now.W <= 1f || now.H <= 1f) now = _lastBoundsPx;   // backend already tore the window down — use the last sample
        if (now.W <= 1f || now.H <= 1f) return;
        _boundsDirty = false;
        _boundsSettleFrames = 0;
        BoundsChanged.Invoke(now);
    }

    // ── detached-window render routing (parent host; runs THROUGH the parent's single render thread) ─────────────────────

    /// <summary>The render thread that owns THIS host's submit/present: the host's own render thread if it has one, else the
    /// parent's (a detached child), else null (pure single-thread inline). Used to rendezvous swapchain resize/teardown.</summary>
    private Threading.RenderThread? OwningRenderThread => _renderThread ?? _parentRenderThread;

    /// <summary>UI thread (parent host): register a detached child as a render source for the parent's render loop. Publishes a
    /// new copy-on-write snapshot WITHOUT parking the loop: a turn already running keeps the snapshot it took, the next turn
    /// sees the child, and the render thread never observes a torn list. Without a render thread (the pure single-thread
    /// path) the child presents inline and the list is simply unused.</summary>
    private void AttachChildRenderSource(AppHost child)
    {
        Threading.ThreadGuard.AssertUi();
        var old = Volatile.Read(ref _childRenderSources);
        var next = new AppHost[old.Length + 1];
        Array.Copy(old, next, old.Length);
        next[old.Length] = child;
        // Its pace evidence exists BEFORE the render thread can see the child (the Volatile.Write below publishes it).
        child._childPace = new Threading.ChildPresentPace(Interlocked.Increment(ref s_childPaceTargets));
        child._videoSurfaces.HostOrdinal = child._childPace.Target;   // F235: the pump / stream-size lines name WHICH window's registry wrote (0 = main)
        // Once per child, always-on: WHERE this child's frames are submitted. A pop-out that presented through the wrong target
        // (F090) was attributable only indirectly; the line names the route, the swapchain and the backend up front.
        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[detached] attach target={child._childPace.Target} hwnd={child._window.Handle.Value:X} route={ChooseSubmitRoute(child._isDetachedChild)} swapchain={child._swapchain.SizePx.Width:F0}x{child._swapchain.SizePx.Height:F0} secondary={(_device.SupportsSecondarySwapchains ? 1 : 0)} backend={_device.BackendName}"));
        Volatile.Write(ref _childRenderSources, next);
    }

    /// <summary>UI thread (parent host), INSIDE the reaper's render-thread park (<see cref="ReapDetachedChild"/>): unregister a
    /// detached child on close, in the same rendezvous that then disposes it, so an in-flight <see cref="DrainChildRenderSources"/>
    /// can't be presenting the child's swapchain as it is released.</summary>
    private void RemoveChildRenderSource(AppHost child)
    {
        var old = Volatile.Read(ref _childRenderSources);
        int at = Array.IndexOf(old, child);
        if (at < 0) return;
        var next = new AppHost[old.Length - 1];
        Array.Copy(old, 0, next, 0, at);
        Array.Copy(old, at + 1, next, at, old.Length - at - 1);
        Volatile.Write(ref _childRenderSources, next);
    }

    /// <summary>Render thread (parent host): drain each registered child host's seam on this present turn — a fresh child
    /// publish is submitted+presented against the CHILD's own swapchain + video presenter, render-confined (the child reuses
    /// the same per-host <see cref="SubmitPresentOnRenderThread"/>). Runs every turn, BEFORE the parent's own present decision
    /// (<c>RenderThread.Loop</c>): a pop-out's present then never queues behind the main window's present-slot wait, which is
    /// the one place a turn blocks (up to a vblank while paced, the liveness bound when the primary's queue is full) — the
    /// shared thread serves the child's frame first and the parent's pacing is untouched (F241, without throttling anything:
    /// an unfocused main window keeps its display-rate motion). Every take here is non-blocking, so the parent's present is
    /// delayed only by the child's own record + submit (the pace line's childDrain). A child with no new publish is a cheap
    /// <c>TryAcquire</c>-false no-op. One snapshot of the copy-on-write list is taken per turn; a child is only ever removed
    /// under a Quiesce rendezvous, so a child in the snapshot is alive for the whole turn.</summary>
    private void DrainChildRenderSources()
    {
        Threading.ThreadGuard.AssertRender();
        // This host's OWN queued swapchain work (a popup's create / release) is applied on every turn, not only on a turn that
        // has a publication to present: the loop runs this callback even on a bare wake, which is what PostPopupRenderAction sends.
        DrainPopupRenderActions();
        var list = Volatile.Read(ref _childRenderSources);
        for (int i = 0; i < list.Length; i++)
        {
            var child = list[i];
            // A child that already latched RenderFailed (see SubmitPresentOnRenderThread's catch) is expected to
            // fail the SAME way every turn — resubmitting it would just re-log and re-swallow forever. It does nothing
            // device-related from here on: no motion re-present (RenderMotion records, submits and presents), and its
            // HasOwnRenderMotion answers false so it cannot hold the shared loop at the display tick. Only its video
            // placement is still drained, so a pop-out that is still on screen keeps its video where the user left it
            // until the owner (OnRenderFailed) closes it.
            if (child._renderFailed) { child.DrainVideoAfterRenderFailure(); continue; }
            // The child's queued swapchain work (its own resize, its popups' create / dispose) rides this turn even when it has
            // no frame to present: those ops are why the UI no longer parks this thread for them.
            child.DrainPopupRenderActions();
            // An owed present (a refused non-blocking one) whose target was since invalidated (resize / recovery) is stale: the back
            // buffer it described is gone, and the next publication draws a whole new frame.
            if (child._presentOwed && !child._renderSeam.IsCurrentTarget(child._owedFrame)) child._presentOwed = false;
            // Nothing to present (no publication pending, no render motion, no owed present): a cheap no-op that must not touch a slot.
            if (!child._renderSeam.HasPendingFrame && !child.HasOwnRenderMotion() && !child._presentOwed) { child._slotDeferred = false; child._deferredSinceQpc = 0; continue; }
            // Never block the shared render thread on a pop-out's vblank (F090): probe the child's OWN present slot without
            // waiting BEFORE adopting its frame. A busy slot (its previous present has not retired) leaves the publication
            // pending - DropOldest keeps only the newest anyway - and the loop comes back next tick (HasRenderMotion); the
            // main window's presents are never stalled behind a secondary monitor's queue. This probe IS the child's catch-up
            // (a busy slot skips, never waits), so a child needs no SlotCatchUp policy of its own.
            var pace = child._childPace;
            long probeStart = Stopwatch.GetTimestamp();
            bool slotOpen = _device.TryTakePresentSlot(child._swapchain, 0);
            long slotAt = Stopwatch.GetTimestamp();
            pace?.NoteSlotTake(slotAt - probeStart, slotOpen);
            if (!slotOpen)
            {
                if (!child._slotDeferred) child._deferredSinceQpc = probeStart;   // its lag counts from the first deferral
                child._slotDeferred = true;
                continue;
            }
            child._slotDeferred = false;
            long presents0 = child._renderPresentCount;
            bool fresh = child._renderSeam.TryAcquire(out var rf);
            // The publication this turn's present (if any) delivers: the fresh one, or the owed one a retry re-presents.
            ulong deliveredSeq = 0;
            if (fresh)
            {
                deliveredSeq = rf.PublishSeq;
                child.SubmitPresentOnRenderThread(rf);
            }
            else if (child._presentOwed)
            {
                // Only the refused frame goes out: re-recording motion over it would submit again before the credit it still
                // holds has been spent, and a second refusal is just another skip, never a wait.
                deliveredSeq = child._owedFrame.PublishSeq;
                child.SubmitPresentOnRenderThread(child._owedFrame, retryOwedPresent: true);
            }
            else child.RenderMotion();
            // Per-child evidence of a REAL present (an elided turn - byte-identical frame - presented nothing; a refused one
            // is counted by the child's pace as skipped).
            if (child._renderPresentCount != presents0)
            {
                // F080, the corollary of draining children BEFORE the parent's present decision: placement work this present applied
                // with the device commit deferred (a first Place, SetVisible, a viewport / clip / corner-radius / overlay change — a
                // MOVE already committed inside the child's own drain, Stage B) must not wait for the turn's post-present commit.
                // The parent's slot wait sits between here and there and can cross a vblank (the unpaced liveness bound, a paced
                // catch-up falling through to the blocking take), and the child's hole would then flip a DWM frame before its
                // picture followed. Commit right after the child's flip; the post-turn commit is a no-op for this child then.
                if (child.VideoApplier.HasUncommittedApply)
                {
                    _device.CommitVideoComposition();
                    if (child.VideoApplier.PublishCommitted()) child._window.Wake();
                }
                long done = Stopwatch.GetTimestamp();
                pace?.NotePresent(done - (child._deferredSinceQpc != 0 ? child._deferredSinceQpc : probeStart), done - slotAt);
                if (deliveredSeq != 0 && pace is not null)
                    Threading.PresentLedger.RecordChild(pace.Target, deliveredSeq, _renderThread?.TickSeq ?? 0, _renderThread?.DisplayClockTickQpc ?? 0, done);
            }
            child._deferredSinceQpc = 0;
        }
    }

    /// <summary>Render thread, parent host, after the parent's present decision: the turn's composition commit for the PARENT's
    /// deferred placement work (F080) and the readiness it makes true. The children drained BEFORE the parent's slot wait
    /// (<see cref="DrainChildRenderSources"/>, F241) and each child that presented with placement applied already committed right
    /// after its own flip, so for them this is the O(1) no-op; the parent's own drain (inside its present) applied without a
    /// commit and lands here, in the DWM frame of its present.</summary>
    private void CommitVideoTurnAfterPresent() => CommitVideoTurn(Volatile.Read(ref _childRenderSources));

    /// <summary>The render loop with THIS host's real callbacks: the submit, the children's drain (extraDrain, before the present
    /// decision), the motion tick, the slot take, the pace evidence, the structural video pre-turn and the post-present commit
    /// (<see cref="CommitVideoTurnAfterPresent"/>). ONE place builds it — the constructor for a windowed host and
    /// <see cref="InstallRenderThreadForTest"/> for a headless one — so a test can pin which callback runs where (a headless
    /// window never goes async on its own, which is how the wiring used to have no coverage).</summary>
    private Threading.RenderThread BuildRenderThread(bool async, FluentGpu.Pal.IRenderDisplayClock? displayClock)
        => new Threading.RenderThread(_renderSeam, rf => SubmitPresentOnRenderThread(rf), async: async,
            deviceLost: _deviceLost, recover: _deviceLost is null ? null : RecoverDeviceAfterDump, windowWake: _deviceLost is null ? null : _window.Wake,
            extraDrain: DrainChildRenderSources, needsTick: HasRenderMotion, tick: RenderMotion,
            tickPeriod: RenderPeriodTicks, displayClock: displayClock,
            takePresentSlot: _device.TryTakePresentSlot, paceHost: SamplePaceHostState,
            submitAbortHandleSink: _device.SetSubmitAbortHandle,
            ownMotion: HasOwnRenderMotion, childPaceBegin: BeginChildPaceWindow, childPaceReport: DescribeChildPace,
            presentSplit: SamplePresentSplit, preTurn: DrainVideoStructuralPreTurn, postTurn: CommitVideoTurnAfterPresent,
            idleTrim: TrimIdleOnRenderThread)
        { LedgerSink = LedgerRenderTurn };

    /// <summary>Test-only: give a HEADLESS primary host the force-sync render loop a windowed one would have (one
    /// <c>RunFrame</c> = one publish + one <c>DrainSync</c> turn on the fgpu-render thread), wired by the same
    /// <see cref="BuildRenderThread"/> as production. From then on the host publishes to its seam and the render thread
    /// submits, drains its detached children and commits, exactly as under a real window; a detached child built with this
    /// thread as its <c>parentRenderThread</c> rides its turns. Call once, before the first frame.</summary>
    internal Threading.RenderThread InstallRenderThreadForTest()
    {
        Threading.ThreadGuard.AssertUi();
        if (_renderThread is not null) throw new InvalidOperationException("the host already owns a render thread");
        _renderThread = BuildRenderThread(async: false, displayClock: null);
        _videoSurfaces.StructuralWake = _renderThread.WakeForVideo;
        return _renderThread;
    }

    /// <summary>Test-only: register a detached child as one of this host's render sources (what <c>OpenDetachedWindow</c> does
    /// after <see cref="AdoptDetachedChild"/>); the child's publications then ride this host's render turns.</summary>
    internal void AttachChildRenderSourceForTest(AppHost child) => AttachChildRenderSource(child);

    /// <summary>Test-only: this host's video-surface registry (the UI-thread intents the publications snapshot).</summary>
    internal FluentGpu.Media.VideoSurfaceRegistry VideoSurfacesForTest => _videoSurfaces;

    /// <summary>Test-only: does this host's applier owe a composition commit for work applied with the commit deferred?</summary>
    internal bool HasUncommittedVideoApplyForTest => VideoApplier.HasUncommittedApply;

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
    /// the platform to absorb pointer-motion wake storms up to the already-selected deadline; idle, cadence-paced and urgent
    /// paths keep ordinary immediate input wake behavior. The display-clock request is the OR over every host's finite wait
    /// (see <see cref="CombineWait"/>), so a shorter wait of one host never disarms the clock another host's wait wants.</summary>
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
    // deadline the stricter motion-coalescing request wins, since both hosts are due at the same instant. The display
    // clock is OR-ed across both finite requests whichever timeout wins: both hosts' ticks come from the one parent-window
    // compositor clock, so a shorter cadence wait must not DISARM it under a pop-out whose own wait wants the vblank
    // (its production gate would starve on a frozen tick seq) — arming it only adds the vblank as a wake source.
    internal static PlatformWaitRequest CombineWait(PlatformWaitRequest a, PlatformWaitRequest b)
    {
        if (a.TimeoutMs < 0) return b;
        if (b.TimeoutMs < 0) return a;
        bool clock = a.WakeOnDisplayClock || b.WakeOnDisplayClock;
        if (a.TimeoutMs < b.TimeoutMs) return new PlatformWaitRequest(a.TimeoutMs, a.InputWakePolicy, clock);
        if (b.TimeoutMs < a.TimeoutMs) return new PlatformWaitRequest(b.TimeoutMs, b.InputWakePolicy, clock);
        return new PlatformWaitRequest(a.TimeoutMs,
            a.InputWakePolicy is PlatformInputWakePolicy.CoalescePointerMotion
                || b.InputWakePolicy is PlatformInputWakePolicy.CoalescePointerMotion
                    ? PlatformInputWakePolicy.CoalescePointerMotion
                    : PlatformInputWakePolicy.Immediate,
            clock);
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
        // A pop-out still waiting for its reveal (F115) applies the LATEST always-on-top choice when it is shown, so a toggle made
        // while it was hidden is not overwritten by the open request's value.
        public void SetTopmost(bool topmost) { _child._revealTopmost = topmost; _window.SetTopmost(topmost); }
        public DetachedOpenTiming OpenTiming => _child.OpenTiming;
        public Action<DetachedOpenTiming>? OnRevealed { get => _child.OnRevealed; set => _child.OnRevealed = value; }
        public double FirstVideoBindMs => _child.FirstVideoBindMs;
        public Action<double>? OnFirstVideoBound { get => _child.OnFirstVideoBound; set => _child.OnFirstVideoBound = value; }
        public void SetBounds(RectF outerBoundsPx) => _window.SetBoundsPx(outerBoundsPx);
        // Overrides IDetachedVideoWindow.MoveTo's SetBounds(current-size)-based default: the PAL window has its own
        // pure-move primitive (IPlatformWindow.MoveToPx), so route to it directly and skip the BoundsPx read-back.
        public void MoveTo(Point2 outerOriginPx) => _window.MoveToPx(outerOriginPx);
        public void Close()
        {
            // Persist a pending resize/move BEFORE the window goes away — the frame-stillness settle may never have run
            // (see AppHost.FlushPendingBoundsChange). Ordered first so the observer still sees a live window rect.
            _child.FlushPendingBoundsChange();
            _window.CloseWindow();   // WM_CLOSE → IsClosed → reaped by TickDetachedHosts
        }
        // Reads/writes the child host's field so the parent's reaper (which holds the child, not the handle) fires it.
        public Action? OnClosed { get => _child.OnClosed; set => _child.OnClosed = value; }
        public RectF BoundsPx => _window.OuterBoundsPx;
        // Fullscreen goes to the CHILD window's own PAL window, never the parent's: the backend resolves the target
        // monitor from THAT window's handle, which is what keeps a pop-out dragged to a second display fullscreening
        // where it already is instead of hopping back to the app's monitor (see IDetachedVideoWindow.SetFullscreen).
        // Guarded on !IsClosed the same way IsOpen is — the reaper runs a frame after the OS destroyed the window, so a
        // late toggle from a still-mounted owner must be inert rather than a call against a dead handle.
        public void SetFullscreen(bool fullscreen) { if (!_window.IsClosed) _window.SetFullscreen(fullscreen); }
        public bool IsFullscreen => !_window.IsClosed && _window.IsFullscreen;
        public void SetTitle(string title) => _window.SetTitle(_child._strings.Intern(title));
        // Same indirection as OnClosed: the reaper samples the CHILD, so the callback must live on the child host.
        public Action<RectF>? BoundsChanged { get => _child.BoundsChanged; set => _child.BoundsChanged = value; }
        // INCIDENT 2026-09: reads/wires the CHILD host's RenderFailed latch/event (SubmitPresentOnRenderThread's
        // catch + DrainChildRenderSources, both on _child) — same indirection as OnClosed/BoundsChanged above.
        public bool RenderFailed => _child.RenderFailed;
        // F110: warm reuse. The handle only forwards; the parent owns the child list and the platform calls.
        public bool IsParked => _child._warmParked && IsOpen;
        public bool Park() => _parent.ParkDetachedChild(_child);
        public bool Unpark(DetachedWindowRequest request) => _parent.UnparkDetachedChild(_child, request);
        public Action? OnRenderFailed { get => _renderFailedCallback; set { _child.OnRenderFailed -= _renderFailedCallback; _renderFailedCallback = value; if (value != null) _child.OnRenderFailed += value; } }
        private Action? _renderFailedCallback;
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

    /// <summary>Software pace floor (ms) when no display clock exists AND the refresh period cannot be trusted (headless,
    /// a remote session reporting a bogus 2 ms "refresh"): 60 Hz. Remote streams are 60 Hz; spinning faster only burns CPU.</summary>
    public const int SoftwarePaceFloorMs = 15;

    /// <summary>The wall-clock production pace when the platform has no compositor clock: just under the refresh period
    /// when the panel's period is attested (present stats valid and >= 4 ms), else the 60 Hz floor. Pure so the gate can
    /// lock it without a panel.</summary>
    public static int SoftwarePaceMs(double refreshMs, bool refreshTrusted)
        => refreshTrusted && refreshMs >= 4.0 ? DeriveAsyncPaceMs(refreshMs) : SoftwarePaceFloorMs;

    private int SoftwarePaceMs()
    {
        // RefreshPeriodTrusted (not stats.Valid directly) so a per-window refresh source counts as trusted too —
        // reading _swapchain.LastPresentStats here would silently drop back to device-only and miss the window override.
        double refreshMs = RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency;
        return SoftwarePaceMs(refreshMs, RefreshPeriodTrusted());
    }

    // -- Production pacing: one frame per compositor tick -----------------------------------------------------------
    // With Present on the render thread the UI loop has no natural vblank reference; producing on every wake (input,
    // DirectManipulation, timers) makes several frames per refresh of which the render thread shows the newest — the
    // POSITIONS reaching the screen are then sampled at uneven intervals even though presents are metronomic (measured
    // 2026-07-25: 1.5–2.6 productions per present, 61% discarded, motion 6–14 ms apart on an 8.33 ms grid). The display's
    // own clock (IPlatformWindow.DisplayClock — DCompositionWaitForCompositorClock republished as a tick seq + vblank
    // instant) is the phase reference: input is dispatched on every wake, but a FRAME is produced at most once per tick,
    // and the frame is stamped with that tick's vblank instant (FrameClock.FrameQpc). No present-ack handshake, no stall
    // ceiling, no slip/unpaced heuristics: the tick IS the vblank, so there is nothing to infer.
    private long _frameTickSeq;            // the display-clock tick this RunFrame was sampled on (0 = clock unavailable)
    private long _lastProducedTickSeq;     // the tick the last produced frame belongs to
    private long _productionDeclines;      // diagnostic census: RunFrames that dispatched input but produced no frame (already produced for this tick)
    private readonly IInputPacingSource? _pacingSource;   // the window's paced-wait census (FrameStats.PacedUrgentBreaks); null ⇒ 0

    /// <summary>Frames declined for production because a frame was already produced for the current compositor tick
    /// (input was still dispatched). Each one is a frame DropOldest would have discarded. Diagnostic (--fg fps).</summary>
    public long ProductionDeclines => _productionDeclines;

    /// <summary>Backstop timeout (ms) for a tick-paced wait: two refresh periods, clamped 8–34. The tick normally ends the
    /// wait; this only fires if the compositor stalls or the tick is missed, so the loop keeps running input, timers and
    /// recovery. Never a pacer in itself.</summary>
    private int TickBackstopMs()
    {
        double refreshMs = RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency;
        int ms = (int)Math.Round(refreshMs * 2.0);
        return ms < 8 ? 8 : ms > 34 ? 34 : ms;
    }

    /// <summary>True when a frame has already been produced for the current compositor tick — producing another would
    /// only feed DropOldest. Open when async is off (the sync path present-throttles), when the platform has no display
    /// clock (the software pace wait is the pacer), or when the previous wait did not arm the clock (ticks were not
    /// counted while idle/cadence-paced — the first frame after a wake must not wait a vblank). UI thread only.
    /// <para>A detached child is gated too (F106), on the PARENT's tick: it has no compositor clock of its own and rides the
    /// parent's render thread, so before this it produced (and bare-woke that thread) once per UI-loop iteration - input over
    /// either window, a media post, a ticker - several frames per vblank of which the newest was shown. Its tick comes from
    /// <see cref="PacingClock"/>.</para></summary>
    private bool ProductionGateBlocks()
    {
        if (!_asyncActive || (_renderThread is null && _parentRenderThread is null)) return false;
        if (_revealPending) return false;   // an unrevealed pop-out (F115) owes its reveal frame: never declined while the window waits for it
        if (!ProductionGateDeclines(_frameTickSeq, _lastProducedTickSeq, _lastWaitWantsDisplayClock)) return false;
        _productionDeclines++;
        return true;
    }

    /// <summary>The gate's decision, pure: a frame is declined only when a tick is known (<paramref name="frameTickSeq"/> != 0),
    /// the wait that led here armed the display clock (so the seq really counted vblanks while the host was busy) and a frame
    /// was already produced for that very tick.</summary>
    internal static bool ProductionGateDeclines(long frameTickSeq, long lastProducedTickSeq, bool waitWantsDisplayClock)
        => frameTickSeq != 0 && waitWantsDisplayClock && frameTickSeq == lastProducedTickSeq;

    /// <summary>The display clock this host paces production on. A window with a compositor clock of its own (the primary)
    /// uses it. A detached child's window has none, so it samples the PARENT render thread's clock - the same vblank source
    /// that thread presents on - while that tick is current; otherwise (no clock, parked clock) the child's own, unavailable,
    /// sample, which keeps the unpaced behaviour. UI thread.</summary>
    private DisplayClockSample PacingClock()
    {
        var own = _window.DisplayClock;
        if (own.Available || _parentRenderThread is not { } parent) return own;
        return parent.TryGetDisplayTick(Stopwatch.GetTimestamp(), out long seq, out long qpc) ? new DisplayClockSample(true, seq, qpc) : own;
    }

    /// <summary>Render thread: the compositor tick the present-time prediction (<see cref="RenderPresentSec"/>) is anchored to
    /// (0 = none, anchor on now). The host's own render thread names its paced turn's tick; a detached child, drained by the
    /// parent's thread, takes the display clock's CURRENT tick - the parent thread's turn tick is only refreshed by the parent's
    /// own paced presents, so it is stale whenever only the pop-out animates.</summary>
    private long RenderTurnTickQpc()
    {
        if (_renderThread is { } own) return own.DisplayTickQpc;
        return _parentRenderThread is { } parent && parent.TryGetDisplayTick(Stopwatch.GetTimestamp(), out _, out long qpc) ? qpc : 0;
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
    /// <summary>THIS frame's <see cref="FluentGpu.Pal.FrameClock"/> (scroll-v3-plan §5.1) — built once at the top of
    /// <see cref="RunFrame"/>, before the input pump/dispatch. The one target time <c>IPlatformWindow.PumpScroll</c>
    /// and the scroll frame step (<c>RunScrollFrame</c>) both consume this same frame; UI-thread read-only (no setter — RunFrame is the
    /// sole writer).</summary>
    public FluentGpu.Pal.FrameClock FrameClock => _palFrameClock;
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
    /// <summary>Wall-time the render thread most recently blocked on frame/back-buffer retirement plus present latency.
    /// This is the render-side stall async hides from FrameMs, not GPU execution time. Diagnostic (--fg fps).</summary>
    public double LastGpuFenceWaitMs => _swapchain.LastFenceWaitMs;
    /// <summary>The TRUE on-GPU execution span (ms) of the most recently retired frame, from the inexpensive always-on
    /// whole-frame timestamp pair. Unlike <see cref="LastGpuFenceWaitMs"/> this excludes vblank/latency/back-buffer waits.
    /// 0 only when the backend does not support timestamp measurement or no sample has retired yet.</summary>
    public double LastGpuRenderMs => _swapchain.TryGetGpuRenderSample(out GpuRenderSample sample) ? sample.ExecutionMs : 0.0;
    /// <summary>Monotonic publication identity for this HOST'S swapchain sample (0 = unsupported/not yet sampled).</summary>
    public ulong LastGpuRenderSampleSequence
        => _swapchain.TryGetGpuRenderSample(out GpuRenderSample sample) ? sample.Sequence : 0;
    /// <summary>Read the coherent whole-frame execution sample owned by this host's swapchain.</summary>
    public bool TryGetGpuRenderSample(out GpuRenderSample sample) => _swapchain.TryGetGpuRenderSample(out sample);
    /// <summary>Copy the coherent submitted-rect-area diagnostic snapshot owned by this host's swapchain.</summary>
    public bool TryCopyRectSubmittedAreaSample(Span<RectSubmittedAreaItem> blendedTop,
        out RectSubmittedAreaSample sample) => _swapchain.TryCopyRectSubmittedAreaSample(blendedTop, out sample);
    /// <summary>Runtime toggle for the device's PASS-granular GPU timeline (a Diagnostics page / probe switch — never an
    /// environment variable). While on, <see cref="CopyGpuPassTimeline"/> returns the most recently retired frame's
    /// per-pass GPU times.</summary>
    public bool GpuPassTimingEnabled
    {
        get => _device.GpuPassTimingEnabled;
        set => _device.GpuPassTimingEnabled = value;
    }

    /// <summary>Copy the main swapchain's most recently retired pass-granular GPU timeline (see
    /// <see cref="GpuPassTimingEnabled"/>). Returns the passes copied; zero-alloc.</summary>
    public int CopyGpuPassTimeline(Span<GpuPassTiming> dst, out GpuPassFrameSummary summary)
        => _swapchain.CopyGpuPassTimeline(dst, out summary);

    /// <summary>Measurement knockouts for probes / the Diagnostics page (<see cref="Rhi.GpuKnockouts"/>): each removes one
    /// cost class from the device's submit so its GPU share can be measured by difference. Never set by production code.</summary>
    public GpuKnockouts GpuKnockouts
    {
        get => _device.Knockouts;
        set
        {
            _device.Knockouts = value;
            // GroupFades is a PLACEMENT control (the slice recorder decides group vs distributed feather), not a device one.
            bool groupFades = (value & GpuKnockouts.GroupFades) != 0;
            _uiSlices.ForceGroupFades = groupFades;
            _renderSlices.ForceGroupFades = groupFades;
            // StickyClipInPaint is a RECORD control (the scene recorder cuts a sticky clip or bakes it inline).
            bool stickyInPaint = (value & GpuKnockouts.StickyClipInPaint) != 0;
            _uiSlices.ForceInlineStickyClip = stickyInPaint;
            _renderSlices.ForceInlineStickyClip = stickyInPaint;
        }
    }

    /// <summary>The message-loop wait timeout (ms) for the NEXT pump: how long to block in <c>WaitForWork</c> before
    /// running another frame. Computes the wake mask ONCE and paces by it:
    /// <list type="bullet">
    /// <item>None ⇒ -1: fully idle, block until an input/paint message arrives (0% CPU).</item>
    /// <item>parked (minimized or hidden) ⇒ -1 (regardless of the mask): a parked window paints nothing; only the
    ///   restore/show message matters — except an unconsumed park/un-park edge, which runs its edge frame at once (0).</item>
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
        w = ClampWaitToScrollChrome(w, _lastWaitKind);   // a scrollbar dwell (the idle hide) wakes exactly when it expires
        w = ClampWaitToOcclusionProbe(w, _lastWaitKind);   // an occluded (not parked) window still wakes to re-probe its target
        w = ClampWaitToCloakDebounce(w, _lastWaitKind);    // a cloaked detached child wakes when its park debounce ends
        w = ClampWaitToReveal(w, _lastWaitKind);           // an unrevealed pop-out wakes to look for its first present
        w = ClampWaitToImageLeftovers(w, _lastWaitKind);   // T10: an idle page still wakes for a pinned canceled leftover
        w = ClampWaitToColdMaintenance(w);
        // Render-owned exits normally wake us through completion feedback. Keep their wall-clock
        // reclaim backstop reachable even when no UI timer or input will ever arrive.
        if (!IsParked && _anim.RenderOwnsCompositor && _scene.OrphanCount > 0)
        {
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < _scene.OrphanCount; i++)
            {
                double ageMs = (now - _scene.OrphanEnqueuedTicks(i)) * 1000.0 / Stopwatch.Frequency;
                int dueIn = (int)Math.Ceiling(Math.Max(1.0, OrphanSettleTimeoutMs - ageMs));
                w = w < 0 ? dueIn : Math.Min(w, dueIn);
            }
        }
        _lastWaitMs = w;   // remembered so Paint can detect a throttle/idle → display-rate step-up and resync the frame clock
        // Latch the classification NOW, against the branch that produced it. Deriving it later from the timeout VALUE
        // was a real bug: the clamp rewrites the value without touching the kind, and two unrelated branches can return
        // the same integer — at 120 Hz the phase-gate ceiling and a Cadence 60-on-120 wait are both 17 ms.
        _lastWaitWasDisplayRate = IsDisplayRateWait(_lastWaitKind, w);
        return w;
    }

    /// <summary>The wait (ms) the loop last chose to pace INTO the current frame (the raw <see cref="RecommendedWaitMs"/>
    /// value, timer-clamped): 0 = display-rate, &gt;0 = cadence/HUD throttle, -1 = blocked idle. Diagnostic (--fg fps).</summary>
    public int LastWaitMs => _lastWaitMs;
    /// <summary>Which <see cref="RecommendedWaitMsCore"/> branch produced <see cref="LastWaitMs"/> — the signal that tells a
    /// maximize/60fps investigation whether the loop is <see cref="HostWaitKind.Cadence"/>-paced by the sources' own
    /// declared rates, <see cref="HostWaitKind.AdaptiveGpu"/>-throttled by measured execution, or running at display rate
    /// (a lock is then downstream in Present/GPU). Diagnostic (--fg fps).</summary>
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
    /// <summary>Shorten an idle/throttled wait to the scrollbar chrome's earliest dwell expiry (the 2 s idle hide, the
    /// hover dwells): the dwell needs no frames while it counts, only the one where it expires. Same rules as
    /// <see cref="ClampWaitToTimers"/>: a display-rate wait and a parked host are left alone, and never a 0 wait.</summary>
    private int ClampWaitToScrollChrome(int w, HostWaitKind kind)
    {
        if (IsDisplayRateWait(kind, w) || IsParked) return w;
        if (!_scrollChrome.TryPeekDue(out double due)) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(0.0, due - _timers.NowMs));
        if (dueIn < 1) dueIn = 1;
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    private int ClampWaitToTimers(int w, HostWaitKind kind)
    {
        if (IsDisplayRateWait(kind, w)) return w;
        // Paint — the only drain site — is gated off while parked (minimized or hidden), so no wait length can make a
        // timer fire; shortening the idle block converts a 0%-CPU sleep into a spin. A message (restore, show,
        // WM_ACTIVATE, a power broadcast) is what wakes a parked loop, and the un-park edge forces the frame that drains.
        if (IsParked) return w;
        if (!_timers.TryPeekEarliest(out double due)) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(0.0, due - _timers.NowMs));
        // The drain is on the NEXT frame, which may be skipped — never return 0 (that is a spin, not a wait).
        if (dueIn < 1) dueIn = 1;
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    /// <summary>Publish <see cref="InputHooks.WindowOccluded"/> for THIS frame: <paramref name="parked"/> (minimized or
    /// hidden — the host's own sample) OR the primary swapchain's <see cref="ISwapchain.IsOccluded"/>. Called from
    /// <see cref="RunFrame"/> above the park and idle gates, so every frame publishes, painted or not.
    /// <para>
    /// Parked is folded in because a parked host never reaches Paint, so it never presents: a backend whose occlusion
    /// read is a by-product of its last present (D3D12 <c>LastPresentStoodDown</c>) would otherwise keep reporting the
    /// last VISIBLE present for the whole minimize.
    /// </para>
    /// <para>
    /// The probe keeps the un-occlusion observable. A backend clears its flags only inside a real present (D3D12 resets
    /// <c>LastPresentStoodDown</c> at the top of every <c>Present</c> and clears <c>OccludedLatched</c> on a successful
    /// <c>DXGI_PRESENT_TEST</c>), and a consumer that pauses on this hook lets the app go idle — nothing would present
    /// again, so a window that was un-cloaked or uncovered would stay "occluded" until some unrelated change painted. So
    /// while the swapchain reports occluded and the window is NOT parked (a parked window's restore/show is a message, and
    /// its un-park edge forces a frame), force one full repaint every <see cref="OcclusionProbeIntervalMs"/>: the present
    /// it owes re-probes the target, and the next frame publishes the cleared value. Disarmed the moment it reads clear.
    /// Allocation-free: two field reads, one clock read and a value-gated signal write.
    /// </para></summary>
    private void PublishWindowOccluded(bool parked)
    {
        bool targetOccluded = _swapchain.IsOccluded;
        PublishRenderMotionPolicy(targetOccluded);
        _windowOccluded.SetIfChanged(parked || targetOccluded);
        if (parked || !targetOccluded) { _occlusionProbeDueMs = double.NaN; return; }
        double now = _timers.NowMs;
        if (double.IsNaN(_occlusionProbeDueMs)) { _occlusionProbeDueMs = now + OcclusionProbeIntervalMs; return; }
        if (now < _occlusionProbeDueMs) return;
        _occlusionProbeDueMs = now + OcclusionProbeIntervalMs;
        RequestFullRepaintOnce();   // defeats both skip-submit gates (UI hash + render-side empty damage) and asks for the frame
    }

    /// <summary>UI thread, once per frame: mirror the window's OCCLUSION to the render thread (<see cref="_renderOccluded"/>). A
    /// change wakes the render loop so it re-evaluates at once - parking its loops when the window becomes occluded, resuming
    /// them when the occlusion probe hears it clear. Focus is deliberately NOT mirrored: an unfocused but visible window keeps
    /// its display-rate motion. One int read and a value-gated write: free on a steady frame.</summary>
    private void PublishRenderMotionPolicy(bool targetOccluded)
    {
        int occluded = targetOccluded ? 1 : 0;
        if (Volatile.Read(ref _renderOccluded) == occluded) return;
        Volatile.Write(ref _renderOccluded, occluded);
        OwningRenderThread?.WakeAsync();
    }

    /// <summary>Shorten an idle/throttled wait so the loop wakes for the armed occlusion probe
    /// (<see cref="PublishWindowOccluded"/>). Same guards as <see cref="ClampWaitToTimers"/>: a display-rate wait already
    /// runs the next frame, a parked window has no probe, and the clamp floors at 1 ms (never a spin).</summary>
    private int ClampWaitToOcclusionProbe(int w, HostWaitKind kind)
    {
        if (double.IsNaN(_occlusionProbeDueMs) || IsDisplayRateWait(kind, w) || IsParked) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(1.0, _occlusionProbeDueMs - _timers.NowMs));
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    /// <summary>Shorten an idle/throttled wait so a cloaked detached child reaches its park when the
    /// <see cref="CloakParkGate"/> debounce ends. The gate is sampled only inside <see cref="RunFrame"/> and a cloak posts no
    /// message, so a child whose UI is idle (render-thread-only motion does not wake the UI loop) would otherwise keep
    /// presenting into the cloaked window until something unrelated woke it. Same guards as
    /// <see cref="ClampWaitToTimers"/>: only a detached child reads the cloak, a display-rate wait already runs the next frame
    /// (which samples the gate), a parked host has nothing left to debounce, and the clamp floors at 1 ms (never a spin).
    /// An uncloaked or already-parked gate reports infinity, so the wait is unchanged.</summary>
    private int ClampWaitToCloakDebounce(int w, HostWaitKind kind)
    {
        if (!_isDetachedChild || IsDisplayRateWait(kind, w) || IsParked) return w;
        double untilPark = _cloakGate.MsUntilPark(_timers.NowMs);
        if (double.IsPositiveInfinity(untilPark)) return w;
        int dueIn = (int)Math.Ceiling(Math.Max(1.0, untilPark));
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    /// <summary>Shorten an idle wait so an unrevealed pop-out (F115) comes back to check whether its first frame has presented:
    /// the present is acknowledged on the render thread, which does not wake the UI loop by itself, so without this a pop-out
    /// whose UI went idle after its first frame would stay hidden until the reveal timeout. A display-rate wait already runs the
    /// next frame (which checks), and a revealed (or non-child) host reports <paramref name="w"/> unchanged.</summary>
    private int ClampWaitToReveal(int w, HostWaitKind kind)
    {
        if (!_revealPending || IsDisplayRateWait(kind, w)) return w;
        return w < 0 ? DetachedRevealGate.PollMs : Math.Min(w, DetachedRevealGate.PollMs);
    }

    /// <summary>Did the branch that produced the last wait want the DISPLAY clock as a wake source? Latched here, next
    /// to the branch, for the same reason <see cref="_lastWaitWasDisplayRate"/> is: re-deriving it later from the
    /// timeout value cannot distinguish an armed phase-gate wait from an unarmed pace wait — they are the same kind and
    /// can be the same integer, and they want OPPOSITE answers (see the two branches below).</summary>
    private bool _lastWaitWantsDisplayClock;

    private int RecommendedWaitMsCore()
    {
        // Default off: every branch that has a better phase reference, or none at all (idle, HUD, baked, cadence),
        // leaves the vblank waiter parked. Only the two branches below opt in.
        _lastWaitWantsDisplayClock = false;
        long now = Stopwatch.GetTimestamp();
        // Freshness gates EMA mutation; bounded TARGET-LOCAL age gates whether the resulting state may pace. Repeated
        // RecommendedWaitMs calls against one completed sample therefore retain an engaged decision instead of alternating
        // AdaptiveGpu/display, but only for six same-target submits and 250ms. A transient unavailable seqlock read uses
        // the same bounded cached sample; unsupported-never-seen and expired evidence fail open. LastFenceWaitMs is
        // intentionally absent: it includes present/back-buffer waits and fed policy its own output.
        bool adaptiveGpuWaitEligible = false;
        if (AdaptiveGpuPacing)
        {
            bool sampleAvailable = _swapchain.TryGetGpuRenderSample(out GpuRenderSample executionSample);
            // The governor reads THIS window's own GPU execution and nothing else: a live video surface, a pop-out or the GPU
            // tier never move its thresholds (owner decision, 2026-10-05: video must never lower the main window's frame rate).
            adaptiveGpuWaitEligible = EvaluateAdaptiveGpuRead(sampleAvailable, in executionSample, now,
                GpuGovernorMaxSubmitAge, GpuGovernorSampleTtlTicks, ref _gpuBoundLastSample,
                ref _gpuBoundSampleSequence, ref _gpuBoundEma, ref _gpuGovernorEngaged);
        }
        // Parked (minimized or hidden): block until a message — cold maintenance bounds the outer wait, never paints. But
        // an UNCONSUMED park/un-park edge runs one frame now: a window hidden or shown by app code (Hide/Show from a
        // handler, not from a message the pump dispatched) posts nothing that would otherwise wake the loop, and the
        // edge frame is what flips Activation.IsActive, pauses/resumes the render thread and forces the first paint.
        // Not during a device-lost rendezvous: RunFrame returns before the edge block until RecoverDone, so a 0 there would
        // spin instead of letting the render thread's wake nudge the loop (the existing recovery contract).
        bool parked = IsParked;
        bool recovering = _deviceLost is { RecoverRequest: not 0, RecoverDone: 0 } && _asyncActive;
        if (parked != _wasParked && !recovering) { _lastWaitKind = HostWaitKind.Idle; return 0; }
        if (parked) { _lastWaitKind = HostWaitKind.Idle; return (_cloakParked || _coverParked) && !IsOsParked ? CloakPollMs : -1; }
        WakeReasons r = ComputeWakeReasons();
        if (r == WakeReasons.None) { _lastWaitKind = HostWaitKind.Idle; return -1; }
        if (r == WakeReasons.DynamicText) { _lastWaitKind = HostWaitKind.Hud; return 100; }   // HUD-only: 10 Hz readout, ~0% idle CPU
        if ((r & ~(WakeReasons.BakedBlurPending | WakeReasons.DynamicText)) == 0)
        {
            int bakedWait = _bakedBlurQueue.RecommendedWaitMs;
            _lastWaitKind = (r & WakeReasons.DynamicText) != 0 ? HostWaitKind.Hud : HostWaitKind.Baked;
            return (r & WakeReasons.DynamicText) != 0
                ? (bakedWait < 0 ? 100 : Math.Min(100, bakedWait))
                : bakedWait;
        }
        // ── Cadence: the loop waits for the earliest DUE source, at the rate that source itself declared ─────────────
        // This branch replaced the entire ambient CLASSIFIER (AnimIsAmbient / AmbientCapEngaged / LatencySensitiveWake /
        // ImageWake / the scroll-grace + scroll-hold + mount-grace windows). The host no longer infers a frame CLASS
        // from a bitmask and a set of time windows — at least eight recorded regressions came from that inference
        // guessing wrong in one direction or the other. Each animation row now carries its own Cadence
        // (Cadence.Display — every row's default, loops included — or an opt-in Cadence.At(hz)) and the scheduler answers ONE
        // question: how long until the earliest of them is due. The caret answers the same question for its blink.
        //
        // Only Anim|Caret can be paced into the future; every other wake bit means "due now" (input, scroll, images,
        // timers, video, popups, virtual refill), and a frame carrying one falls straight through to the display-tick
        // path — which is why this branch can never add input or scroll latency, and why the scroll/mount grace
        // windows that existed to protect exactly those cases are gone. A row that is due NOW (due == 0) also falls
        // through: due-now is the display-rate path, not a wait.
        double nowMs = _timers.NowMs;   // the same clock domain the scheduler's NextDueMs takes
        float due = MathF.Min(_anim.NextDueMs(nowMs), _caretBlinker.NextDueMs());
        bool onlyCadenceWork = (r & ~CadenceWake) == 0;
        if (onlyCadenceWork && due > 0f && !float.IsPositiveInfinity(due))
        {
            double refreshMs = RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency;
            long lastPresent = Volatile.Read(ref _lastPresentQpc);
            double sincePresentMs = lastPresent == 0 ? -1.0 : (now - lastPresent) * 1000.0 / Stopwatch.Frequency;
            // The power ceiling floors the PERIOD before quantization (an interval between presents, on the vblank
            // lattice), so it can only LENGTHEN a row's own wait. 0 = no ceiling: the row's cadence verbatim.
            int pcap = PowerCapFps;
            int wait = pcap > 0
                ? CadencePacing.FlooredWaitMs(due, 1000.0 / pcap, refreshMs, sincePresentMs)
                : CadencePacing.QuantizedWaitMs(due, refreshMs, sincePresentMs);
            _lastWaitKind = HostWaitKind.Cadence;
            return wait;
        }
        // Power ceiling: POLICY the OS asked for (PowerCapFps — Windows Energy Saver), applied uniformly to every frame
        // the loop would produce for motion, due-now rows and frame-clock pollers included, so nothing runs smooth
        // beside something choppy. Interaction (PowerCapNeverPace: scroll, drag, touch, repeat) is never capped. The
        // same Resync-exempt quantized wait as the governor below; the slower of the two wins when both apply.
        if (PowerCapFps > 0 && (r & PowerCapNeverPace) == 0)
        {
            int fps = PowerCapFps;
            if (AdaptiveGpuPacing && adaptiveGpuWaitEligible && GpuGovernorWake.MayPace(r) && GpuGovernorFps < fps) fps = GpuGovernorFps;
            double refreshMs = RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency;
            long lastPresent = Volatile.Read(ref _lastPresentQpc);
            double sincePresentMs = lastPresent == 0 ? -1.0 : (now - lastPresent) * 1000.0 / Stopwatch.Frequency;
            _lastWaitKind = HostWaitKind.PowerCap;
            return CadencePacing.QuantizedWaitMs(1000.0 / fps, refreshMs, sincePresentMs);
        }
        // Adaptive-GPU governor: MEASUREMENT, not policy. The frame is not cadence-paceable (a due row, a one-shot
        // transition, the smooth playhead, an image reveal), but sampled on-GPU execution says the panel rate is
        // unsustainable at this size — running full-rate just thrashes into vblank misses. Pace to a STEADY
        // GpuGovernorFps through the same Resync-exempt quantized wait. It never touches interaction/scroll
        // (GovernorNeverPace); the scroll-grace/hold guards it used to share with the ambient branch are gone with the
        // classifier — a live scroll already sets a GovernorNeverPace bit, which is the guard that was doing the work.
        if (AdaptiveGpuPacing && adaptiveGpuWaitEligible && GpuGovernorWake.MayPace(r))
        {
            double refreshMs = RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency;
            long lastPresent = Volatile.Read(ref _lastPresentQpc);
            double sincePresentMs = lastPresent == 0 ? -1.0 : (now - lastPresent) * 1000.0 / Stopwatch.Frequency;
            _lastWaitKind = HostWaitKind.AdaptiveGpu;
            return CadencePacing.QuantizedWaitMs(1000.0 / GpuGovernorFps, refreshMs, sincePresentMs);
        }
        // Due-now work (interaction, a due animation row, scroll, a live producer, image work): produce ONE
        // frame per compositor tick. Async: the wait ends on the display clock's tick (or the backstop if the compositor
        // stalls); ProductionGateBlocks declines a second production inside the same tick. Input still ends the wait
        // immediately (WaitForWork is MsgWait-based) and is dispatched on that wake — only the frame waits for its tick.
        // No display clock (headless / a session where the runtime probe ruled the export out): wall-clock software pace
        // at the refresh period, 60 Hz floor when the period is untrusted. Sync render path: 0 — Present throttles.
        if (_asyncActive)
        {
            _lastWaitWantsDisplayClock = true;   // arms (and keeps armed) the display clock; the first wait also creates it
            // A child has no clock of its own: it paces on the parent render thread's (the combined wait arms it, see
            // WaitRequestWithDetached).
            if (_window.DisplayClock.Available || (_parentRenderThread?.DisplayClockAvailable ?? false))
            {
                _lastWaitKind = HostWaitKind.DisplayTick;
                return TickBackstopMs();
            }
            _lastWaitKind = HostWaitKind.SoftwarePace;
            return SoftwarePaceMs();
        }
        _lastWaitKind = HostWaitKind.DisplayRate;
        return 0;
    }

    /// <summary>Was the wait that paced INTO the current frame a display-rate one? Latched in
    /// <see cref="RecommendedWaitMs"/> against the branch that produced it, never re-derived later.</summary>
    private bool _lastWaitWasDisplayRate;

    /// <summary>True when the loop was ALREADY running at display rate, as opposed to throttled/idle.
    ///
    /// Classified by BRANCH (<see cref="HostWaitKind"/>), not by timeout value. The value-based form this replaces was
    /// wrong two ways. It aliased: <see cref="TickBackstopMs"/> is 17 ms on a 120 Hz panel and
    /// <see cref="CadencePacing.QuantizedWaitMs"/> returns integers 1..17 there, so a Cadence-paced frame that happened
    /// to compute 17 was classified display-rate — which skipped both the timer clamp and the step-up Resync, the exact
    /// cadence-lurch this guard exists to prevent. And it was stale: the gate wait was a mutable field read a frame
    /// later than it was written, so a refresh-rate change made a wait that WAS display-rate stop matching.
    ///
    /// The <c>w == 0</c> clause is not redundant, but it is SCOPED to the one kind that legitimately produces a 0:
    /// <c>BakedBlurQueue.RecommendedWaitMs</c> returns 0 for "due now" under <see cref="HostWaitKind.Baked"/>; no gap
    /// elapses, so resyncing there would reintroduce the lurch that kind-only classification is meant to avoid.
    /// Unscoped, the clause aliased in the other direction — the exact hazard the paragraph above describes, just by
    /// value 0 instead of 17: an Idle/Cadence wait that <see cref="ClampWaitToTimers"/> had rewritten down to 0 for a
    /// due timer then read as display-rate, which suppressed the step-up Resync on precisely the frames that HAD
    /// over-slept. The clamp no longer emits 0 (it floors at 1 ms), and this test no longer accepts one from any
    /// branch but Baked — so the code now matches the intent documented here.
    ///
    /// Getting this wrong is a known, non-obvious breakage rather than a style point. The frame-clock step-up guard
    /// resyncs the animation clock whenever the previous wait was a stale throttle gap; if a display-rate wait is not
    /// recognised as one, EVERY animating async frame resyncs, NextDeltaMs() returns 0 every frame, and one-shot enter
    /// transitions freeze at their initial (invisible) state — animated content never appears while static chrome does.</summary>
    private static bool IsDisplayRateWait(HostWaitKind kind, int w) =>
        kind is HostWaitKind.DisplayRate or HostWaitKind.DisplayTick or HostWaitKind.SoftwarePace
            || (kind is HostWaitKind.Baked && w == 0);

    private readonly ColdMaintenanceDeadline _pixelMaintenance = new(), _sceneMaintenance = new();
    private readonly object _coldMaintenanceWakeGate = new();
    private bool _coldMaintenanceStopped;
    private ulong _seenSceneCapacityRevision;
    // Deterministic monotonic-clock seam for behavioral tests; production uses the OS uptime clock, not frame time.
    internal Func<long>? ColdMaintenanceClock { get; set; }
    private long ColdMaintenanceNowMs => ColdMaintenanceClock?.Invoke() ?? Environment.TickCount64;
    internal int ColdMaintenanceRuns { get; private set; }
    internal int ColdMaintenanceWakeCount { get; private set; }

    private void OnPixelBufferRetained(FluentGpu.Media.PixelBufferPool pool)
    {
        // Also serializes disposal against a worker already invoking its copied event delegate.
        lock (_coldMaintenanceWakeGate)
        {
            if (_coldMaintenanceStopped || !ReferenceEquals(pool, _pixelPool)) return;
            if (_pixelMaintenance.Arm(ColdMaintenanceNowMs))
            {
                ColdMaintenanceWakeCount++;
                _window.Wake(); // Bare message, NOT WakeFrame/Post.
            }
        }
    }

    private void ObserveSceneCapacity(long now)
    {
        ulong revision = _scene.CapacityRevision;
        if (revision == _seenSceneCapacityRevision) return;
        _seenSceneCapacityRevision = revision;
        if (_scene.Capacity > 256) _sceneMaintenance.Arm(now);
    }

    private bool CanMaintainSnapshots => _anim.RenderOwnsCompositor && !_runtime.HasPending
        && !_needFullLayout && !_window.InModalLoop;

    private int ClampWaitToColdMaintenance(int wait)
    {
        // Service is intentionally below the recovery rendezvous. Do not turn its excluded drain into a 1 ms poll.
        if (_deviceLost is { RecoverRequest: not 0, RecoverDone: 0 } && _asyncActive) return wait;
        long now = ColdMaintenanceNowMs;
        ObserveSceneCapacity(now);
        wait = _pixelMaintenance.ClampWait(wait, now);
        wait = _sceneMaintenance.ClampWait(wait, now);
        return CanMaintainSnapshots
            ? ColdMaintenanceDeadline.ClampWait(wait, now, _renderSeam.NextCapacityMaintenanceMs) : wait;
    }

    private void RunColdMaintenance()
    {
        long now = ColdMaintenanceNowMs;
        ObserveSceneCapacity(now);
        bool serviced = false;
        // Disarm BEFORE releasing buffers: a Return that races the trim can arm the next finite episode.
        if (_pixelMaintenance.TryConsume(now)) { _pixelPool.Trim(); serviced = true; }
        if (_sceneMaintenance.TryConsume(now))
        {
            _scene.TrimExcessCapacity();
            _seenSceneCapacityRevision = _scene.CapacityRevision; // Our own shrink must not schedule another attempt.
            serviced = true;
        }
        if (CanMaintainSnapshots && _renderSeam.NextCapacityMaintenanceMs <= now)
        {
            // Three slots bound the cold work. Failed attempts consume their policy too; never poll a Reading slot.
            for (int i = 0; i < 3; i++)
                _renderSeam.TryReclaimSceneCapacity(_scene, _images, _strings,
                    _connected.Detached, _popupWindows, _anim, now);
            serviced = true;
        }
        if (serviced) ColdMaintenanceRuns++;
    }

    // ── Repaint-damage host state (gpu-renderer.md §13.1) ────────────────────────────────────────────────────────────
    // The recorder can only see what the SCENE changed. These are the host-level facts that invalidate the target
    // itself — nothing in the scene is dirty, yet every pixel is untrustworthy. Each forces a full repaint with a named
    // reason. No renderer consumes the region yet, so these are pure bookkeeping in Phase A.
    private bool _repaintTargetValid;           // false until the first frame publishes; cleared by resize/DPI/device recovery
    private ColorF _lastPublishedClear;         // theme switch changes the clear color under a byte-identical stream

    /// <summary>Force the NEXT published frame to repaint the WHOLE target
    /// (<see cref="RepaintFullReason.TargetInvalidated"/>), exactly as a resize or a device recovery does. One frame
    /// only — the flag re-arms itself at publish.
    /// <para>Not a diagnostic switch: it is the explicit form of an invalidation the engine already performs
    /// internally, and an embedder that paints into the same target out-of-band (an interop overlay, an external
    /// composition pass) genuinely needs it. The <c>--repaint-identity</c> harness uses it to render the SAME scene
    /// state twice — once partially, once in full — and compare the two captures byte for byte.</para>
    /// <para>It also clears the skip-submit baseline, and that is not an extra: the request is for a REPAINT, and a
    /// frame whose command stream is byte-identical to the last presented one is elided before it ever publishes — so
    /// without this the request would be silently swallowed exactly on the frames where the caller most obviously means
    /// it (nothing in the scene changed; the TARGET is what went stale).</para></summary>
    public void RequestFullRepaintOnce()
    {
        _repaintTargetValid = false;
        _lastPresentedDrawListHash = 0UL;
        // …and ASK for the frame. Nothing in the scene is dirty on the frames this call is for, so without this the
        // host's idle gate returns before Paint and the request waits for an unrelated wake that may never come.
        WakeFrame();
    }

    // ── No-op publication skip (render-thread seam): see NoopPublicationGate ─────────────────────────────────────────
    private readonly NoopPublicationGate _noopPublications = new();

    /// <summary>Scene publications elided because the frame changed nothing a publication carries (cumulative).</summary>
    internal long NoopPublicationsElided => _noopPublications.Elided;

    /// <summary>Test-only: the render seam's publication counter (moves once per published scene).</summary>
    internal ulong ScenePublishSeqForTest => _renderSeam.PublishSeq;

    /// <summary>Test-only: the publication key a frame would compare now (default record options), and whether the gate
    /// would match it against the last publication — the allocation-free comparison the no-op skip makes every frame.</summary>
    internal bool NoopKeyMatchesForTest()
    {
        var key = BuildPublicationKey(default, _window.ClientSizePx);
        return _noopPublications.Matches(in key, _uiCoverage);
    }

    /// <summary>Test-only: the <c>published=</c> census since the last call (why each publication could not be skipped).</summary>
    internal string NoopBlockedCensusForTest()
    {
        var sb = new System.Text.StringBuilder();
        _noopPublications.AppendBlockedWindow(sb);
        return sb.ToString();
    }

    /// <summary>Test-only: invalidate the render target (what a resize / DPI change / device recovery does to the seam).</summary>
    internal void InvalidateRenderTargetForTest() => _renderSeam.InvalidateTarget();

    /// <summary>Test-only: captures that kept their slot's image snapshot (no image input moved since that slot's last one).</summary>
    internal int ImageCapturesReusedForTest => _renderSeam.ImageCapturesReused;

    /// <summary>What a scene publication made now would carry beyond the store's own ledger (see <see cref="PublicationKey"/>).</summary>
    private PublicationKey BuildPublicationKey(in Threading.SceneRecordOptions options, Size2 frameSize) => new(
        _renderSeam.TargetEpoch, _scene.Root, _scene.DeviceScale, _scene.OverlayClip, _scene.SpotlightScrimClip,
        _scene.HasActiveVirtualDisclosures, _scene.PendingRemovalExtents.Length, _scene.PendingRemovalOverflow,
        ImageCache.RecordingInputSerial, _anim.CompositorCaptureFingerprint(), _scene.Recording.ConfigurationVersion,
        options, frameSize, _window.Scale, Clear);

    /// <summary>The hard half of the no-op publication skip: every host-side fact that, when set, means this frame owes the
    /// renderer a publication whatever the key comparison says. Conservative by construction: each clause is cheap, and any
    /// doubt publishes (a skipped real change would be a stale frame; a published no-op only costs time).</summary>
    private NoopPublicationBlock NoopPublicationCandidate(bool resized, bool keepAlive, bool reconciled, bool layoutNeeded,
        bool transformWrote, bool imageContentChanged)
    {
        if (_paintWake == UnknownPaintWake || !NoopPublicationGate.WakeAllowsSkip(_paintWake)) return NoopPublicationBlock.Wake;
        if (_lastPublishedSceneSeq == 0 || !_everLaidOut || !_repaintTargetValid || _revealPending) return NoopPublicationBlock.Target;
        // An armed frame capture (RequestFrameCapture) completes on the render thread's next COMPOSITED present, and only a
        // publication (or render-side motion) reaches that turn: a settled scene must publish once for it.
        if (Volatile.Read(ref _evCaptureArmed) != 0) return NoopPublicationBlock.Capture;
        if (resized || keepAlive || reconciled || layoutNeeded) return NoopPublicationBlock.Structure;
        if (transformWrote) return NoopPublicationBlock.Transform;
        if (imageContentChanged) return NoopPublicationBlock.Images;
        if (_scene.HasUnpublishedChanges) return NoopPublicationBlock.SceneChange;
        if (_scene.HasRecordDirtyLedger || _scene.PendingRemovalExtents.Length != 0 || _scene.PendingRemovalOverflow)
            return NoopPublicationBlock.Retiring;
        if (_popupWindows.Count != 0 || _retiringPopups.Count != 0 || _popupSkipRoots.Count != 0 || _reuseBlockRoots.Count != 0
            || _connected.Detached.NodeCount != 0 || _anim.PendingStructuralDamage.Count != 0
            || _scene.OrphanCount != 0 || _scene.OverlayCount != 0 || !_scene.DragGhost.IsNull || !_scene.DragOverlay.IsNull
            || _scene.DropSpotlightActive) return NoopPublicationBlock.Overlay;
        if (_images.HasActiveCrossfades || _device.HasPendingUploads || _bakedBlurQueue.HasRunnableJob) return NoopPublicationBlock.Images;
        if (_videoSurfaces.HasUnpublishedChanges || _swapchain.TextRepaintPending || _device.HasLiveFeedback) return NoopPublicationBlock.Device;
        if (_anyScrollMovedThisFrame || _scrollUnsettledCount != 0 || AnyUserScrollMoving) return NoopPublicationBlock.Scroll;
        lock (_popupActionLock) { if (_popupActionsIn.Count != 0 || _ownResizePending) return NoopPublicationBlock.Overlay; }
        return NoopPublicationBlock.None;
    }

#if DEBUG || FLUENTGPU_DIAG
    // DEBUG self-check of the elide path: every NoopParityInterval-th elision (and the first) re-captures the store in full into
    // a scratch snapshot and compares it with the snapshot the newest publication carried. Equal is the definition of a sound
    // skip; a mismatch means some store write escaped the ledger HasUnpublishedChanges reads. It is reported, the gate is
    // reset, and this frame publishes, so the pixels are right either way. Compiled out of Release, like the capture parity.
    private const int NoopParityInterval = 16;
    private Scene.SceneRecordingSnapshot? _noopParityScratch;
    private long _noopParityCounter;
    internal int NoopParityVerifications { get; private set; }
    internal int NoopParityFailures { get; private set; }

    private bool VerifyNoopPublication()
    {
        if (_noopParityCounter++ % NoopParityInterval != 0) return true;
        if (_renderSeam.NewestCapturedScene is not { } newest) return true;
        NoopParityVerifications++;
        var scratch = _noopParityScratch ??= new Scene.SceneRecordingSnapshot();
        scratch.Capture(_scene, default);   // no popup roots: a frame with popup windows never reaches the elide
        bool equal = scratch.EqualsForParity(newest.Scene, out string mismatch);
        scratch.ReleaseResources();
        if (equal) return true;
        NoopParityFailures++;
        _noopPublications.Invalidate();
        Console.Error.WriteLine("[fg-noop-parity] a publication the gate would elide differs from the last one published ("
            + mismatch + "): some store write bypassed the capture ledger. Publishing this frame.");
        return false;
    }
#else
    internal int NoopParityVerifications => 0;
    internal int NoopParityFailures => 0;
    private static bool VerifyNoopPublication() => true;
#endif

    // ── Skip-submit gate state (finding #3a) ─────────────────────────────────────────────────────────────────────────
    private ulong _lastPresentedDrawListHash;   // FNV-1a of the last PRESENTED command stream; a byte-identical frame skips submit+present
    private long _framesSkippedSubmit;          // diagnostic census of elided submits (idle/playback redundant presents avoided)
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

    /// <summary>The identities behind <see cref="FrameClockPollerCount"/>, in the same <c>pollers=N:…</c> form the
    /// always-on <c>[wake]</c> census prints (see <c>WakeDiagnostics.AppendPollers</c>) — so a gate, or the app's
    /// diagnostics page, shows exactly what the log would say instead of reimplementing the walk. Appends to the
    /// caller's builder; report cadence only, never per frame.</summary>
    public void DescribeFrameClockPollers(System.Text.StringBuilder sb) => WakeDiagnostics.AppendPollers(sb, _frameClockSig);

    /// <summary>Every <c>FrameClock.Tick</c> subscriber that held a kept-awake frame during the CURRENT [wake] census
    /// window, named, even one that has already unmounted (unlike <see cref="DescribeFrameClockPollers"/>, which only
    /// sees who is still subscribed right now) — the <c>pollersSeen=</c> field of the always-on <c>[wake]</c> line
    /// (<c>WakeDiagnostics.AppendPollersSeen</c>). No-op (appends nothing) before the host's first frame.</summary>
    public void DescribeFrameClockPollersSeen(System.Text.StringBuilder sb) => _wakeDiag?.AppendPollersSeen(sb);


    /// <summary>The bitmask form of <see cref="HasActiveWork"/>: one bit per OR-term, semantically identical (the
    /// boolean is just <c>!= None</c>). Every term is an O(1) read (ImageCache.PendingCount/HasActiveCrossfades were
    /// made O(1) so this never scans). Drives the always-on [wake] census; otherwise as cheap as the original chain.</summary>
    private WakeReasons ComputeWakeReasons()
    {
        WakeReasons r = WakeReasons.None;
        if (_frameNeeded) r |= WakeReasons.FrameNeeded;
        // A pending window geometry/scale change IS active work: EnsureSize only runs inside Paint, so a resize,
        // WM_DPICHANGED hop or app-zoom step landing on an otherwise idle host must wake the frame that will run it
        // (Win32 also raises PaintRequested; headless drives the seam directly and relies on this term). Three field
        // reads + float compares - zero-alloc, and False every frame the window is quiet.
        var wsz = _window.ClientSizePx;
        if (_window.Scale != _lastScale || wsz.Width != _lastSize.Width || wsz.Height != _lastSize.Height)
            r |= WakeReasons.FrameNeeded;
        // A theme mutation (Tok.Use / Tok.SetAccent, from anywhere: a settings click, an async album accent) is work the
        // next Paint re-renders for: it wakes that frame itself instead of waiting on an unrelated wake. One int compare.
        if (Tok.Epoch != _lastThemeEpoch) r |= WakeReasons.FrameNeeded;
        // Own bits (not folded into FrameNeeded) so the [wake] census can name the treadmill: warming vs budget vs latch.
        if (_reconciler.HasWarmingVirtuals) r |= WakeReasons.WarmingVirtuals;
        if (_runtime.HasPending) r |= WakeReasons.RuntimePending;
        if (_scene.HasDynamicText) r |= WakeReasons.DynamicText;
        // Anim wake: any row that is timer-due NOW or LATER sets the bit — the cadence branch in RecommendedWaitMsCore
        // then turns "later" into a wait of exactly that length. Driven-only rows are event-woken (signal write), never
        // timer-due: NextDueMs returns +∞ for them, so a paused Driven playhead still costs zero frames.
        if (_connected.HasActive || (_anim.HasUiWork && !float.IsPositiveInfinity(_anim.NextDueMs(_timers.NowMs))))
            r |= WakeReasons.Anim;   // connected fly / snapshot awaiting dest; hover/press fades are now _anim tracks too
        // An unsettled plan needs UI frames (the virtualizer re-windows on it every frame; the render poser moves the
        // pixels on its own ticks) and a live chrome cycle needs its tick.
        if (_scrollUnsettledCount > 0 || _scrollChrome.NeedsFrame
            || (_scrollChrome.TryPeekDue(out double chromeDue) && chromeDue <= _timers.NowMs)) r |= WakeReasons.ScrollAnim;
        // A frame-aligned producer (DirectManipulation engaged/pending, or a touchpad wheel-fallback gesture live) needs one
        // PumpScroll per refresh regardless of whether a plan is already live.
        if (_window.ScrollProducerLive) r |= WakeReasons.ScrollProducer;
        if (_repeat.HasActive) r |= WakeReasons.Repeat;
        // Caret wake: a focused editor owns a wait of CaretBlinker.NextDueMs (the cadence branch), never a panel-rate
        // spin — the bit says "there is a blink coming", the blinker says when.
        if (_caretBlinker.HasActive) r |= WakeReasons.Caret;
        if (!_anim.RenderOwnsCompositor && _scene.HasBrushAnims) r |= WakeReasons.BrushAnims;
        // The cache-wide drain bits belong to the host that pumps the cache (F108): a pop-out that does not would see them set
        // until the primary's pump clears them and take a Paint that applies nothing, and it must not be woken for a
        // completion that only the primary's pump can drain (per-id completions wake the owning host: ImageStatusChanged).
        if (PumpsSharedImages)
        {
            if (_images.HasReadyCompletions) r |= WakeReasons.ImageReady;
            // T10: a pinned canceled leftover whose retry is DUE forces the frame whose Pump restarts it; a future one sets no
            // bit (ClampWaitToImageLeftovers shapes the idle wait to reach it). Nothing listed = one float read.
            if (ImageLeftoverDueInMs() <= 0.0) r |= WakeReasons.ImageLeftoverDue;
        }
        // E5 (design-engine-images.md): HasPendingUploads is an unsubmitted UPLOAD/activation, not a retire backlog —
        // a queued retire is fence-only maintenance (ReclaimCompletedUploads), never a reason to keep the loop awake on
        // its own, since it clears itself the next time ANY frame (even a skipped/elided one) reaches the device.
        if (_device.HasPendingUploads) r |= WakeReasons.ImagesPending;
        if (_swapchain.TextRepaintPending) r |= WakeReasons.TextRepaintPending;
        if (_bakedBlurQueue.HasJobs) r |= WakeReasons.BakedBlurPending;
        if (!_anim.RenderOwnsCompositor && _images.HasActiveCrossfades) r |= WakeReasons.ImageCrossfades;
        if (_scene.OrphanCount > 0 && (!_anim.RenderOwnsCompositor || HasReclaimableOrphan()))
            r |= WakeReasons.Orphans;
        if (_dispatcher.Drag.HasActiveWork || _dispatcher.DragDrop.HasActiveWork
            || _dragSettlePhase != DragSettlePhase.None) r |= WakeReasons.DragDropWork;   // E5: ghost spring easing / edge auto-scroll / chip settle
        if (_dispatcher.Drag.IsActive) r |= WakeReasons.DragActive;   // E5 reorder dwell keep-alive: a live drag keeps frames coming so the 200/300ms FrameClock dwell tickers advance even on a motionless pointer (DragController.cs:118)
        if (_dispatcher.HasArmedHold) r |= WakeReasons.GestureHold;   // §7A touch long-press: a STATIONARY held finger emits no input, so keep frames coming until TickGestureArenas fires the ~500ms Hold (then this clears and the loop idles)
        if (_dispatcher.HasPendingTouchPress) r |= WakeReasons.TouchPress;
        // A compositor-bound UI clock (not native video presentation) is an explicit request for panel-rate frames.
        // This keeps the seek playhead smooth while a native DirectComposition video presents decoded frames on its own.
        if (_frameClockSig.HasSubscribers) r |= WakeReasons.FrameClockPoller;
        // A paceable per-frame clock (a visualizer) asks for frames too, but the governor may pace it (GpuGovernorWake.NeverPace).
        if (_frameClockPaceableSig.HasSubscribers) r |= WakeReasons.FrameClockPaceable;
        // A feedback trail (visualizer F6) still settling: the backend advances it on frames with no scene change.
        if (_device.HasLiveFeedback) r |= WakeReasons.FeedbackSettle;
        // Native engines / geometry changes request one coalesced post-layout video pump. It is deliberately distinct
        // from playback state: a playing DComp video must not turn every host frame into a repaint.
        if (_videoSurfaces.HasPendingPumps) r |= WakeReasons.VideoPumpPending;
        // A windowed popup's desktop-acrylic open reveal is driven per-frame on Present (CompositionBackdrop.TickAnimation),
        // so it needs the loop to keep presenting until it settles — otherwise (no engine animation active for windowed
        // menus) the loop idle-skips and the reveal freezes at its seed. O(popups) ≈ O(1) (typically 0–1 menus open).
        //
        // A popup that has not yet PRESENTED content owes a frame for the same reason, and it is the stronger claim: its
        // window is still hidden waiting for that paint (see the reveal gate in ImportRecordingFeedback /
        // RecordPopupWindows), so if the loop idled here the popup would never appear at all — and if it were revealed
        // anyway it would be a frosted plate with nothing in it. Clears with the first present, which is the frame after
        // the lease in the normal case.
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            // A leased popup whose swapchain the render thread has not built yet owes frames too (its create rides the next turn),
            // and one whose create FAILED owes the frame that drops it (RecordPopupWindows).
            if (_popupWindows[i].Swapchain is not { } psc)
            {
                r |= WakeReasons.PopupAnim;
                break;
            }
            if (psc.PopupAnimating || !psc.HasPresentedContent) { r |= WakeReasons.PopupAnim; break; }
        }
        if (_inputHooks.HasAfterAnimationWork?.Invoke() == true) r |= WakeReasons.PopupAnim;
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


    public ImageCache Images => _images;

    // Census accessors (read by MemCensus / CensusSnapshot — same assembly): the subsystems Scene/Animation/Images
    // already expose are reused; these surface the rest. All passive O(1) reads.
    internal StringTable Strings => _strings;
    internal TreeReconciler Reconciler => _reconciler;
    internal (int initializedSlots, long indexedBytes, long textStyleBytes, long totalCapacity, long highestRequired)
        SceneCapacityCensus => _renderSeam.SceneCapacityCensus;
    internal int SceneCapacityReclaims => _renderSeam.CapacityReclaims;
    internal long ReclaimedSceneIndexedBytes => _renderSeam.ReclaimedIndexedCapacityBytes;
    /// <summary>Last <see cref="RenderCensus"/> spike dump (empty when census off or no spike this frame).</summary>
    public string LastRenderCensusDump => _reconciler.LastRenderCensusDump;
    internal int InteractionAnimatorCensus => _anim.HoverPressTrackCount;   // hover/press are now engine HoverFade/PressFade tracks (InteractionAnimator deleted)
    internal int DeviceLostRecoveryCountForTest => _deviceLostRecoveryCount;

    /// <summary>Test seam (F109): leave this host's render-retry state as a refused or deferred present would
    /// (<paramref name="owed"/>), run the failed-child drain, and report whether any retry state survives it.</summary>
    internal bool RetryStateAfterFailedDrainForTest(bool owed)
    {
        _presentOwed = owed;
        _slotDeferred = owed;
        DrainVideoAfterRenderFailure();
        return _presentOwed || _slotDeferred;
    }

    /// <summary>Test-only (gate.timer.*): the frame-clock timer queue, its deterministic headless clock, and the
    /// post-input warm-cadence enable (off headless by default so existing idle gates are unaffected; the warm-cadence
    /// gate flips it on). <see cref="FrameClockMsForTest"/> is the headless timer clock (advances by the fixed step per Paint).</summary>
    internal HostTimerQueue TimersForTest => _timers;
    internal double FrameClockMsForTest => _frameClockMs;
    internal bool WarmCadenceEnabledForTest { get => _warmCadenceEnabled; set => _warmCadenceEnabled = value; }
    /// <summary>Test-only (F118): the UI→render occlusion mirror <see cref="PublishRenderMotionPolicy"/> wrote last (the park flag).</summary>
    internal bool RenderOccludedForTest => Volatile.Read(ref _renderOccluded) != 0;
    /// <summary>Test-only (F118): the value <c>InputHooks.WindowOccluded</c> last published (parked OR the swapchain reports occluded).</summary>
    internal bool WindowOccludedForTest => _windowOccluded.Value;
    internal double WarmCadenceUntilForTest => _warmCadenceUntilMs;

    /// <summary>Test-only (gate.wake-present.*): drives <see cref="NotePresented"/>'s missed-vsync accounting
    /// directly, bypassing the real record/submit/present pipeline. Headless never goes async (<see
    /// cref="_asyncActive"/> is false there — see its own field doc), so the actual UI-thread-stamps/render-thread-
    /// reads race this mechanism exists for cannot occur naturally inside this single-threaded harness; these three
    /// members let a gate script the exact QPC ordering of stamps and present calls the race needs instead.
    /// <see cref="NoteNoPresentTurnForTest"/> mirrors a RunFrame/Paint exit that submitted no present (see
    /// <see cref="_lastNoPresentQpc"/>'s doc for the full enumeration); <see cref="NotePresentedForTest"/> mirrors
    /// a present-thread present completing (real or, for the race, deliberately "late"); <see
    /// cref="MissedVsyncsTotalForTest"/> reads the cumulative counter these calls feed, independent of any
    /// FrameStats a real RunFrame call would have produced.</summary>
    internal void NoteNoPresentTurnForTest() => NoteNoPresentTurn();
    /// <summary>Test-only: an explicit wake with nothing behind it (the FrameNeeded bit alone), as a bare WakeFrame makes.</summary>
    internal void WakeFrameForTest() => WakeFrame();
    internal void NotePresentedForTest(ulong publishSeq = 0) => NotePresented(publishSeq);
    internal long MissedVsyncsTotalForTest => Interlocked.Read(ref _missedVsyncsTotal);

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

    /// <param name="initialSceneCapacity">Item E: the <see cref="SceneStore"/>'s (and the render seam's snapshot
    /// slots') starting node capacity, and the floor <see cref="SceneStore.TrimExcessCapacity"/> will never cut below.
    /// 0 (the default) keeps today's behaviour (<see cref="SceneStore"/>'s own default of 64, no floor). A host whose
    /// first real scene is known to be large (Wavee: ~8192) sizes this up front instead of paying the doubling-Grow
    /// GC churn (LOH churn observed 2048→4096→…→8192 on first scroll/nav) every cold session.</param>
    public AppHost(IPlatformApp app, IPlatformWindow window, IGpuDevice device, IFontSystem fonts,
                   StringTable strings, Component root, ImageCache? images = null, IFrameTimeSource? frameTime = null,
                   bool compositeSwapchain = false, bool isDetachedChild = false,
                   Threading.RenderThread? parentRenderThread = null, int initialSceneCapacity = 0)
        : this(app, window, device, fonts, strings, root, images, frameTime, compositeSwapchain,
               isDetachedChild, parentRenderThread, loopModeOverride: null, initialSceneCapacity) { }

    // Internal ctor carrying the render-loop mode override. loopModeOverride is the ONLY way to request ForceSync (or pin
    // SingleThread) — there is no env var; nothing selects ForceSync by default. Reachable from the IVT seam tests/probes
    // (FluentGpu.VerticalSlice / FluentGpu.Windows.Tests) so they can exercise the threaded submit path without the async
    // timeline. A Headless window ignores the override and stays SingleThread (the deterministic gate path). The public
    // ctor above delegates here with null (⇒ Async for a real windowed host — the landed default).
    internal AppHost(IPlatformApp app, IPlatformWindow window, IGpuDevice device, IFontSystem fonts,
                     StringTable strings, Component root, ImageCache? images, IFrameTimeSource? frameTime,
                     bool compositeSwapchain, bool isDetachedChild,
                     Threading.RenderThread? parentRenderThread, RenderLoopMode? loopModeOverride,
                     int initialSceneCapacity = 0)
    {
        // Item E: 0 keeps SceneStore's own built-in default (64) and no trim floor — today's behaviour, byte-for-byte.
        _scene = initialSceneCapacity > 0 ? new SceneStore(initialSceneCapacity) : new SceneStore();
        if (initialSceneCapacity > 0) _scene.CapacityFloor = initialSceneCapacity;
        // The render seam's own snapshot slots (SceneRenderFrame, one per ring slot) are lazily captured — size the
        // FIRST one for each slot to the same capacity so the host's known-large first scene doesn't ALSO pay a
        // doubling-Grow on the render thread's copy the first time each of the three slots captures.
        _renderSeam.InitialSceneCapacity = initialSceneCapacity;
        _app = app;
        _fonts = fonts;
        _isDetachedChild = isDetachedChild;
        if (!isDetachedChild) _renderTiles = new(SliceTableCap, SliceTileCap, SliceSurfaceCap);   // a child never composites (see the field)
        _parentRenderThread = parentRenderThread;   // detached child: route presents through the parent's single render thread
        _window = window;
        _pacingSource = window as IInputPacingSource;   // once: the Win32 window implements it, headless does not (→ 0)
        _pixelPool.BufferRetained += OnPixelBufferRetained;
        // Render-loop mode decision: a Headless window is ALWAYS SingleThread (the deterministic path the slice/gates need);
        // a real windowed host defaults to Async (the landed default). loopModeOverride is the internal-only escape hatch —
        // seam tests/probes pass ForceSync (or SingleThread) explicitly; it never forces a Headless window off SingleThread.
        _loopMode = window.Handle.Kind == NativeHandleKind.Headless
            ? RenderLoopMode.SingleThread
            : (loopModeOverride ?? RenderLoopMode.Async);
        _asyncActive = _loopMode == RenderLoopMode.Async && window.Handle.Kind != NativeHandleKind.Headless;   // headless never goes async (see field)
        // Popup targets are captured into the same scene publication and recorded/presented by the owning render
        // thread. UI target creation/resize/destruction is parked, so async no longer needs an in-window fallback.
        PopupWindowsEnabled = window.Handle.Kind == NativeHandleKind.Headless || device.SupportsSecondarySwapchains;
        _device = device;
        // A detached child's swapchain is created at the backend's initial depth (= the policy's Shallow), not at the primary's current one.
        _maxFrameLatency = isDetachedChild ? _depthPolicy.Depth : device.MaxFrameLatency;
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
        SceneRecorder.ConfigureScrollbarArrowGlyphs(_scene, sbUp, sbDown, sbLeft, sbRight, sbFam);
        _images = images ?? new ImageCache(new FakeImageDecoder());
        _isHeadless = window.Handle.Kind == NativeHandleKind.Headless;
        if (window.Handle.Kind == NativeHandleKind.Hwnd) _videoSurfaces.WindowHandle = (nuint)window.Handle.Value;   // protected video ties output protection to THIS window (main or pop-out)
        _videoSurfaces.DisplayProvider = () => new FluentGpu.Media.VideoDisplay(window.MonitorSizePx, window.IsFullscreen);   // F089: the stream is sized against THIS window's monitor
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
        // INCIDENT 2026-09 (docs/plans/detached-window-render-isolation-implementation.md §1.2): a detached child's
        // swapchain used to be created while the PARENT's render thread could be mid-record on the shared device
        // (CreateSwapchain → InitSwapChain → ResetRepaintLedger + Activate). It now runs under the same park
        // OpenPopupWindow uses for the identical call. A null owner means no render thread exists yet (the primary
        // host's own ctor, or a child under a single-thread parent): the UI thread is the sole device owner.
        parentRenderThread?.Quiesce();
        try
        {
            _swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx, Composited: compositeSwapchain));
        }
        finally { parentRenderThread?.Resume(); }
        _reconciler = new TreeReconciler(_scene, strings, _runtime);
        _reconciler.RegisterPendingEffectContext = RegisterPendingEffectContext;
        _layout = new FlexLayout(_scene, fonts);
        _invalidator = new LayoutInvalidator(_scene, _layout);
        _invalidator.DebugKeyResolver = _reconciler.DebugKeyOf;   // best-effort node→key for the --fg diag relayout-escape message (DEBUG-only invocation)
        _dispatcher = new InputDispatcher(_scene);
        _reconciler.OnSubtreeDeactivated = OnSubtreeDeactivated;
        _reconciler.OnSubtreeRemoved = _dispatcher.NotifySubtreeRemoved;
        _anim =new AnimEngine(_scene);
        _connected = new ConnectedAnimation(_scene, _anim, _images);   // shared-element (connected-animation) Hero flies
        _scrollChrome = new ScrollBarChrome(_scene);
        _uiSink = new UiPoseSink(_scene);
        InitScrollWiring();
        _repeat = new RepeatTicker(_scene);
        _caretBlinker = new CaretBlinker(_scene);
        _lastSize = window.ClientSizePx;
        _lastScale = window.Scale;
        _lastZoom = window.Zoom;

        // A reactive write (anywhere) requests a frame.
        _runtime.FrameRequested = WakeFrame;
        _dispatcher.RequestRerender = WakeFrame;   // virtual list crossing an item boundary on scroll
        // Hover/press edges drive BOTH the (record-time) InteractionAnimator AND the new declarative While* resolver.
        // The resolver is a no-op for nodes without WhileHover/WhilePressed targets — additive, no regression.
        _dispatcher.OnHoverChanged = (n, on) => { _anim.SetHover(n, on); _anim.ApplyInteractionEdge(n, AnimEngine.InteractKind.Hover, on); };
        _dispatcher.OnPressChanged = (n, on) => { _anim.SetPress(n, on); _anim.ApplyInteractionEdge(n, AnimEngine.InteractKind.Press, on); };
        _dispatcher.OnRepeatArmed = _repeat.Arm;
        _dispatcher.OnRepeatReleased = _repeat.Disarm;
        _dispatcher.OnRepeatPaused = _repeat.Pause;     // held pointer left the repeat node → stop ticking
        _dispatcher.OnRepeatResumed = _repeat.Resume;   // re-entered → fresh initial delay, no immediate re-fire
        _dispatcher.OnKeyPreview = _inputHooks.Preview;   // an open overlay/flyout can intercept Escape (registered via the InputHooks ambient)
        // Ctrl+wheel app zoom: bridge the dispatcher's post-element / pre-viewport wheel hook to the tree-registered
        // InputHooks.ZoomWheel (null until an app opts in, so Ctrl+wheel scrolls exactly as before). One lambda, wired
        // once at construction; per-notch invocation allocates nothing.
        _dispatcher.OnZoomWheel = d => _inputHooks.ZoomWheel?.Invoke(d) ?? false;
        _inputHooks.PointerVelocity = () => _dispatcher.PointerVelocity;        // cross-axis swipe controls snap on real flick speed
        _inputHooks.GetPointerPosition = () => _dispatcher.PointerPosition;     // ToolTip safe-zone poll (bubble stays hit-test-invisible)
        _inputHooks.SetCursorOverride = _dispatcher.SetCursorOverride;          // media idle chrome: hide until activity
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
        _dispatcher.OnScrollGestureStarted = _inputHooks.NotifyScrollStarted;   // "a scroll started" (SwipeControl auto-dismiss)
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
        _dispatcher.OnWindowMoveSizeBegan = _inputHooks.NotifyWindowMoveSizeBegan; // …learns when an OS move/size loop begins
        _dispatcher.OnWindowMoveSizeEnded = _inputHooks.NotifyWindowMoveSizeEnded; // …learns when an OS move/size loop is over
        _inputHooks.WindowClose = _window.CloseWindow;
        _inputHooks.OpenDetachedWindow = OpenDetachedWindow;   // pop-out video window (guarded: a child host / async / headless returns null)
        // The same guard, askable in advance, so an affordance can offer or withhold the option instead of dead-clicking.
        _inputHooks.CanOpenDetachedWindow = () => !_isDetachedChild && !_isHeadless && _device.SupportsSecondarySwapchains;
        _inputHooks.SetTitleBarRegions = (regions, count) => _window.SetTitleBarRegions(regions.AsSpan(0, count));
        _inputHooks.GetNodeRect = _scene.AbsoluteRect;
        var chromeEpoch = new Signal<int>(0);
        _inputHooks.WindowChromeEpoch = chromeEpoch;
        _inputHooks.WindowOccluded = _windowOccluded;
        // Mica deactivation parity (WinUI): a Mica window paints a flat SOLID fallback when INACTIVE — DWM stops the live
        // blur, so without this the transparent client lets the desktop wallpaper bleed through, giving a too-light,
        // wallpaper-tinted chrome whenever the window isn't focused. Active → Transparent (the real Mica shows); inactive →
        // SolidBackgroundFillColorBase (theme-aware). Only a Mica window (FluentApp set WindowBackground=Transparent) swaps.
        _micaWindow = Theme.WindowBackground.A <= 0.004f;
        _dispatcher.OnWindowActivationChanged = () =>
        {
            // Read the base LIVE off Tok.T so it follows a theme toggle: dark #202020 / light warm canvas. A hardcoded dark
            // fallback showed near-black chrome in LIGHT mode the instant the window lost focus (the translucent light
            // chrome composited over #202020 instead of the light canvas).
            if (_micaWindow)
            {
                var bg = _window.IsActive ? ColorF.Transparent : Tok.T.WindowBackground;
                if (!_isDetachedChild) Theme.WindowBackground = bg;
                else if (_childWindowBackground != bg)
                {
                    // A child never writes the process-global backdrop. Its clear colour is host-local; zero the presented-
                    // hash latch (the device-lost idiom) so the byte-identical stream still reaches the screen once.
                    _childWindowBackground = bg;
                    _lastPresentedDrawListHash = 0;
                }
            }
            chromeEpoch.Value = chromeEpoch.Peek() + 1;
        };

        // Live drag state for UseDragState / DragPreviewLayer (cursor-following custom preview). Wired on the host
        // instance AND the channel-default (a DragPreviewLayer mounted by a static factory reaches it via Default).
        _inputHooks.DragEpoch = _dragEpoch;
        _inputHooks.GetDragState = ReadDragState;
        _inputHooks.DragPosX = _dragPosX;
        _inputHooks.DragPosY = _dragPosY;
        // The channel-default mirrors below are process-global, last-writer-wins seams owned by the PRIMARY host: a detached
        // child must not publish them (it would hijack the main window's OS drops / hyperlinks / drag preview) nor clear them.
        if (!_isDetachedChild)
        {
            InputHooks.Current.Default.DragEpoch = _dragEpoch;
            InputHooks.Current.Default.GetDragState = ReadDragState;
            InputHooks.Current.Default.DragPosX = _dragPosX;
            InputHooks.Current.Default.DragPosY = _dragPosY;
        }

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
        if (!_isDetachedChild)
        {
            InputHooks.Current.Default.OpenUri = app.OpenUri;
            InputHooks.Current.Default.Clipboard = app.Clipboard;   // mirror the clipboard too (static factories / host-less reads use the default)
        }

        // OS file/folder drop seam (the inbound twin of OpenUri): the platform's file-drop handler (the Windows backend's
        // WM_DROPFILES case) invokes these on the UI thread via the normal message pump; they drive the dispatcher's
        // external DragSession so a BoxEl.DropTarget accepting DropKinds.Files receives the drop. Wired on the host
        // instance AND the channel-default (the backend reaches them via Current.Default — it has no component scope).
        _inputHooks.ExternalDragEnter = _dispatcher.ExternalDragEnter;
        _inputHooks.ExternalDragOver = _dispatcher.ExternalDragOver;
        _inputHooks.ExternalDragLeave = _dispatcher.ExternalDragLeave;
        _inputHooks.ExternalDrop = _dispatcher.ExternalDrop;
        _inputHooks.ExternalDropFiles = _dispatcher.ExternalDropFiles;
        if (!_isDetachedChild)
        {
            InputHooks.Current.Default.ExternalDragEnter = _dispatcher.ExternalDragEnter;
            InputHooks.Current.Default.ExternalDragOver = _dispatcher.ExternalDragOver;
            InputHooks.Current.Default.ExternalDragLeave = _dispatcher.ExternalDragLeave;
            InputHooks.Current.Default.ExternalDrop = _dispatcher.ExternalDrop;
            InputHooks.Current.Default.ExternalDropFiles = _dispatcher.ExternalDropFiles;
        }

        // Inbound twin of OpenUri: a single-instance second-launch redirect (the PAL's WM_COPYDATA → ActivationRedirected,
        // already on the UI thread). Stash + WakeFrame here; Paint() drains _pendingActivation at the top and re-raises
        // the public AppHost.ActivationRedirected for app code. WakeFrame is UI-thread-only — safe because the PAL
        // delivers this on the UI thread (no PostMessage hop needed, unlike a cross-thread notification activator).
        _onActivationRedirected = uri => { _pendingActivation = uri; WakeFrame(); };
        app.ActivationRedirected += _onActivationRedirected;
        // Thumbnail-toolbar click: the PAL raises this on the UI thread from WM_COMMAND/THBN_CLICKED. Stash the id +
        // WakeFrame; Paint() drains at the top and re-raises so app handlers may write signals this same frame.
        _onThumbButtonClicked = id => { _pendingThumbButtonId = id; _pendingThumbClick = true; WakeFrame(); };
        app.ThumbButtonClicked += _onThumbButtonClicked;
        _onAppNavigationCommand = which => { _pendingAppNavigationWhich = which; _pendingAppNavigation = true; WakeFrame(); };
        app.AppNavigationCommand += _onAppNavigationCommand;
        // Explorer created/recreated the taskbar button (registered TaskbarButtonCreated message). Same stash/drain as
        // SystemColorsChanged — app code re-adds the thumbnail toolbar after a shell restart.
        _onTaskbarButtonCreated = () => { _pendingTaskbarButtonCreated = true; WakeFrame(); };
        app.TaskbarButtonCreated += _onTaskbarButtonCreated;
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
        _reconciler.RequestFrame = WakeFrame;      // wake-only seam: mutate retained scene state, wake, DON'T re-render
        // KeepAlive park/un-park → quiesce/resume the parked subtree's animation + scroll tickers so a backgrounded tab's
        // looping animation or mid-fling scroll can't keep the frame loop awake (defeating the idle wake-stop). A parked
        // shared-element node also captures its reverse-fly snapshot here (Back returns to it via the like-tagged dest).
        _reconciler.OnNodeParkedChanged = (node, parked) =>
        {
            _anim.SetNodeParked(node, parked);
            // Only viewports own a chrome row — the reconciler raises this for EVERY node in the parked subtree.
            if (_scene.HasScroll(node)) _scrollChrome.SetNodeParked((int)node.Raw.Index, parked);
            _connected.OnNodeParked(node, parked);
        };
        // Symmetric teardown of INDEX-keyed per-node side-tables on slot free (mem-06): a freed node's slot is reused,
        // so the AnimEngine layout-transition spec (keyed by node index, not gen-checked handle) must be dropped or the
        // next node reusing that index inherits the stale row.
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
            _images.SetHeldImageSource(CollectHeldImageIds);
            _bakedBlurQueue.SetCompletionWake(_window.Wake);
            _device.SetBakedBlurQueue(_bakedBlurQueue);
            if (_loopMode is (RenderLoopMode.ForceSync or RenderLoopMode.Async))
            {
                // ASYNC (Step 1): the UI thread must not touch the device. The pixel sink hands an OWNED pixel buffer to the
                // render thread via the queue (optimistically admitting Ready); the render thread stages it, returns the buffer
                // to the queue's BufferPool, and posts back only rejections. The evict sink enqueues too. See ImageUploadQueue.
                _imageQueue = new Threading.ImageUploadQueue { BufferPool = _pixelPool };
                var q = _imageQueue;
                _images.SetPixelAttemptSink((int id, System.ReadOnlySpan<byte> px, int w, int h) =>
                {
                    // Ownership handoff (scroll-feel 2026-09-16 W2-E1): while DecodeScheduler.Pump is running this sink,
                    // `px` IS the worker's pooled decode buffer, on loan — take it and enqueue it as-is. The old shape
                    // (Rent a second buffer + px.CopyTo) memcpy'd every cover a second time on the UI thread: 256 KB–1 MiB,
                    // LOH-class, per apply during a fling — the hotAllocKB / gen2 signature in the scroll recordings.
                    // The buffer flows back to the SAME pool the scheduler rented it from: D3D12Device.DrainImageJobs →
                    // q.ReturnUploadBuffer → q.BufferPool, which the PixelPool setter points at FluentApp's ONE shared
                    // PixelBufferPool (also the scheduler's DecodeOptions.PixelPool). Any other caller — the blur-hash LQIP
                    // upload (ImageCache scratch), FakeImageDecoder's `new byte[]` scratch, a non-scheduler decoder — offers a
                    // span it still owns, so it is copied into a pool rental exactly as before.
                    if (!FluentGpu.Media.DecodeScheduler.TryTakeDecodeBuffer(px, out byte[]? buf))
                    {
                        buf = _pixelPool.Rent(px.Length);
                        px.CopyTo(buf);
                    }
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

        if (_isDetachedChild)
        {
            _imageQueue = _images.RecordingUploadQueue;
            // The primary's ImageStatusChanged handler (above) only knows ITS nodes, and the pump that raises the event is the
            // primary's (PumpsSharedImages): route each id's completion to this host's own nodes and wake only this host when one
            // is actually painting it, so a pop-out's artwork still appears without the pop-out Painting for every main-window
            // decode (F108). Raised on the UI thread, inside the pump. Detached in PrepareDispose.
            _onSharedImageStatus = (id, _, _, _) => { if (_reconciler.MarkImageDirty(id)) WakeFrame(); };
            _images.ImageStatusChanged += _onSharedImageStatus;
        }

        // Publish ambient contexts before the first render so UseContext(Viewport.Size)/FrameDiagnostics resolve.
        _lastViewportDip = ClientSizeDip();
        _viewportSig.Value = _lastViewportDip;
        _inputHooksSig = new Signal<object?>(_inputHooks);
        _viewportScaleSig.Value = _window.Scale <= 0f ? 1f : _window.Scale;
        _viewportZoomSig.Value = _window.Zoom;   // display-only channel (Scale above already contains the zoom)
        _reconciler.SetAmbient(Viewport.Size, _viewportSig);
        _reconciler.SetAmbient(Viewport.Scale, _viewportScaleSig);
        _reconciler.SetAmbient(Viewport.Zoom, _viewportZoomSig);
        _reconciler.SetAmbient(FrameDiagnostics.Current, _frameStatsSig);
        _reconciler.SetAmbient(InputHooks.Current, _inputHooksSig);
        // Fully qualified: FluentGpu.Pal.FrameClock (the scroll-v3 seam clock, §5.1) is now ALSO in scope via
        // `using FluentGpu.Pal;` — bare `FrameClock` is ambiguous with FluentGpu.Hooks.FrameClock (this ambient key).
        _reconciler.SetAmbient(FluentGpu.Hooks.FrameClock.Tick, _frameClockSig);
        _reconciler.SetAmbient(FluentGpu.Hooks.FrameClock.PaceableTick, _frameClockPaceableSig);
        _uiPoster = Post;   // ONE delegate instance so HostDispatch.Current can be identity-compared on teardown
        _hostPostSig = new Signal<object?>(_uiPoster);   // ambient UI-thread poster (HostDispatch.Post / UsePost)
        _reconciler.SetAmbient(HostDispatch.Post, _hostPostSig);
        if (!_isDetachedChild) HostDispatch.Current = _uiPoster;   // process-static poster for non-component services (localization, …) — cleared in Dispose; a child must not take it over
        _reconciler.SetAmbient(SharedTransition.Begin, new Signal<object?>((Action<string>)_connected.Begin));   // connected-anim forward capture-at-click
        _reconciler.SetAmbient(SharedTransition.BeginConfigured, new Signal<object?>((Action<FluentGpu.Animation.ConnectedTransitionRequest>)_connected.Begin));
        _reconciler.SetAmbient(SharedTransition.SetMotion, new Signal<object?>((Action<FluentGpu.Animation.ConnectedMotion>)(m => _connected.FlyMotion = m)));   // live fly-curve switcher (app A/B)
        // Window-visibility ambient: the channel value IS the visibility signal (an IReadSignal<bool>, never re-published),
        // so UseIsActive resolves it once and subscribes to the INNER signal — see Activation.IsActive.
        _reconciler.SetAmbient(Activation.IsActive, new Signal<object?>(_windowVisible));
        _reconciler.SetAmbient(ThemeControl.Request, new Signal<object?>((Action<float>)RequestThemeTransition));   // live re-theme trigger for app code
        _reconciler.SetAmbient(VideoCompositor.Current, new Signal<object?>(_videoSurfaces));   // video-surface intent buffer for UseVideoSurface
        _videoSurfaces.RequireSlotBinding = !_isHeadless;   // a real host drains into a presenter: elements wait for their OWN slot's bind (the headless seam has no presenter to wait for)
        if (!_isHeadless) FluentGpu.Media.MediaCensus.RegisterRegistry(_videoSurfaces);   // F235: every window's registry joins the process-wide dual-handle scan (unregistered in FinishDispose)
        _reconciler.SetAmbient(HostTimers.Current, new Signal<object?>(_timers));   // frame-clock timer queue for the timing hooks (UseTimeout/UseInterval/UseDebouncedValue/UseThrottledValue)

        // Keep-alive repaint: the OS fires this synchronously from inside a modal move/size loop (and on NC
        // hover/press transitions while the frame loop idles). Paint with keepAlive so the device skips its
        // frame-latency throttle wait — otherwise each fires a full vblank-class stall inline on the WndProc thread
        // (the drag-start / live-resize hitch). Live resize still paints synchronously; it just no longer blocks.
        _window.PaintRequested = () => Paint(0, keepAlive: true);
        // Modal-loop peer tick (F093): the OS loop of THIS window suspends every other window's frame loop (it runs inside a
        // DispatchMessage on the one UI thread), so each beat of it also repaints the peers, paint-only and throttled.
        _window.ModalLoopTick = OnModalLoopTick;

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
            _renderThread = BuildRenderThread(_asyncActive, _window.CreateRenderDisplayClock());
            _device.MarkRenderConfined();
            _videoSurfaces.StructuralWake = _renderThread.WakeForVideo;   // a handle arriving wakes the loop: no UI publication needed (F208)
        }
        else if (_isDetachedChild && parentRenderThread is not null)
            _videoSurfaces.StructuralWake = parentRenderThread.WakeForVideo;   // a pop-out's handle wakes the PARENT's loop, which drains it
        EnableEvidence();   // the evidence ledgers live on the recorder pair that composites (AppHost.Evidence.cs)

        // Opt-in diagnostics tools (constructed only when their flag is set; the host tick paths short-circuit otherwise).
        _wakeDiag = new WakeDiagnostics(_frameClockSig, _anim, _scene, AppendRenderWakeCensus, () => _window.IsActive, _timers);   // always-on: see WakeDiagnostics (the [wake] census)
        if (s_memDiag)
        {
            _memCensus = new MemCensus(this, EngineSwitches.MemDiagSeconds);
        }

        // Mount the root component as a reactive render-effect (initial render builds the scene). The mount is UI work
        // and the constructing thread IS the UI thread: bind it before mounting. A root whose first page mounts a
        // virtualized list binds its scroll handle here, and that plan write wakes the (already running) render thread
        // through a UI-asserted call — unbound, that tripped the Debug confinement guard at startup.
        Threading.ThreadGuard.BindCurrent(Threading.ThreadGuard.ThreadRole.Ui);
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

    void OnSubtreeDeactivated(NodeHandle root)
    {
        _dispatcher.DeactivateSubtree(root);
        _inputHooks.RunSubtreeDeactivated(root);
        // A parked page has just unpinned every cover it was holding, which is the one moment the image LRU gains
        // candidates that are provably off screen. Eviction otherwise runs only after a completed decode, so
        // navigating away from an image-heavy page and then decoding nothing left it resident indefinitely.
        _images.TrimToBudget();
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
        // A reaped detached child hands its queue to the parent: the child's loop will never run again.
        if (Volatile.Read(ref _postForward) is { } fwd) { fwd(action); return; }
        _uiPosts.Enqueue(action);
        // Lost the race with ForwardPostsTo (it set the forward after our check but drained before our enqueue): re-drain.
        if (Volatile.Read(ref _postForward) is { } fwd2) { DrainPostsTo(fwd2); return; }
        _window.Wake();   // thread-safe (Win32 PostMessage WM_NULL); breaks a blocked WaitForWork so an idle loop drains promptly
    }

    /// <summary>Detached child being retired (UI thread, parent's reaper): from now on <see cref="Post"/> forwards to the
    /// parent's poster, and everything already queued here is handed over so no awaiter is stranded on a loop that will
    /// never tick again. Idempotent.</summary>
    private void ForwardPostsTo(Action<Action> parentPost)
    {
        Volatile.Write(ref _postForward, parentPost);
        DrainPostsTo(parentPost);
    }

    private void DrainPostsTo(Action<Action> target)
    {
        while (_uiPosts.TryDequeue(out var a)) target(a);
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
        {
            // A posted action must never take down the frame — but a swallowed exception must never be SILENT either:
            // until 2026-09-25 this catch was empty, and an app-level fault posted to the UI thread vanished without a
            // trace (Wavee's `--crash-probe throw` ran for 90 s "healthy"). Route it to the diagnostic sink the host
            // app wires into its log; the frame continues exactly as before.
            try { a(); }
            catch (Exception ex) { Diag.Sink?.Invoke("[post] posted UI action threw (frame continues): " + ex); }
        }
        // Re-arm on leftovers: _uiPosts is NOT a term in ComputeWakeReasons(), so a ceiling-truncated drain could
        // otherwise idle-gate before its next slice ran. (Every Post also carries its own WM_NULL, so the WAKE is already
        // guaranteed; this makes the FRAME guaranteed too — including after a self-re-post the snapshot deferred.)
        if (!_uiPosts.IsEmpty) _frameNeeded = true;
    }

    private int _pumpedEvents;   // events pumped into the ring this frame (device-lost line + the warm-cadence arm)

    /// <summary>The wake mask <see cref="RunFrame"/> computed for the frame its <see cref="Paint"/> call runs, or
    /// <see cref="UnknownPaintWake"/> for a Paint entered any other way (the WndProc keep-alive repaint), which then
    /// recomputes what it needs and never takes the no-op publication skip.</summary>
    private WakeReasons _paintWake = UnknownPaintWake;
    private const WakeReasons UnknownPaintWake = (WakeReasons)(-1);
    private long _warmCadenceIdleTurns;   // turns woken by the warm-cadence hold alone, run as idle turns (no Paint)

    /// <summary>Turns whose only wake reason was the post-input warm-cadence hold, taken as idle turns instead of a
    /// Paint (cumulative; the [wake] census prints the window's count as <c>warmIdle=</c>).</summary>
    internal long WarmCadenceIdleTurns => _warmCadenceIdleTurns;

    private const uint WarmCadenceInputMask =
        (1u << (int)InputKind.PointerDown) | (1u << (int)InputKind.PointerUp)
        | (1u << (int)InputKind.PointerCancel) | (1u << (int)InputKind.Key)
        | (1u << (int)InputKind.KeyUp) | (1u << (int)InputKind.Char)
        | (1u << (int)InputKind.Scroll);

    /// <summary>The frame proper (<see cref="RunFrame"/> wraps it with the frame ledger, AppHost.Ledger.cs). Every early-out
    /// stamps <see cref="_ledgerExit"/> (a plain store) so a ledgered frame says which gate stopped it.</summary>
    private FrameStats RunFrameCore()
    {
        _ledgerExit = LedgerFrameExit.Painted;
        _ledgerWake = 0;
        ImportRecordingFeedback();
        // Seam confinement backstop: the frame pump IS the UI thread. Bind it (idempotent) + assert. Both are
        // [Conditional("FGGUARD")] — live in Debug/CI (proves single-UI-thread ownership), erased from Release/Ship.
        Threading.ThreadGuard.BindCurrent(Threading.ThreadGuard.ThreadRole.Ui);
        Threading.ThreadGuard.AssertUi();

        // ── ONE frame clock (scroll-v3-plan §5.1) — built HERE, before the pump/dispatch below, so a frame-aligned
        // producer's PumpScroll (Paint, after the production gate) and the scroll frame step (also Paint) both
        // consume the identical (FrameQpc, PresentQpc) pair for this RunFrame call. Cheap: a few field reads + integer
        // arithmetic (RefreshLattice is pure), no allocation.
        unchecked { _frameClockSeq++; }
        if (_isHeadless)
        {
            long headlessFrameQpc = (long)(_frameClockMs * 1e-3 * Stopwatch.Frequency);
            _palFrameClock = RefreshLattice.Headless(headlessFrameQpc, RefreshPeriodQpcOrDefault(), _frameClockSeq);
        }
        else
        {
            // The compositor tick this frame belongs to (§13.2): production is gated to one frame per tick, and the frame
            // is stamped with that tick's vblank instant. Sampled ONCE here so the gate below and the clock agree.
            var display = PacingClock();
            _frameTickSeq = display.Available ? display.TickSeq : 0;
            _palFrameClock = RefreshLattice.Build(display.Available, display.TickQpc, RefreshPeriodQpcOrDefault(),
                Stopwatch.GetTimestamp(), _frameClockFloorQpc, _frameClockSeq, Volatile.Read(ref _maxFrameLatency));
            _frameClockFloorQpc = _palFrameClock.FrameQpc;
        }
        // Publish the SAME target time to app code (Hooks.FrameClock.FrameQpc/PresentQpc) before anything app-visible
        // runs this frame — input handlers, posts, timers, the Tick publish, the flush — so app motion samples the
        // frame's vsync-lattice time instead of a ~15.6 ms-quantized wall clock. Both paths (headless: deterministic).
        // Two static long stores.
        // Primary host only: a detached child (ticked right after the parent, same thread) samples the parent's value
        // instead of overwriting it with its own off-lattice instant.
        if (!_isDetachedChild)
        {
            FluentGpu.Hooks.FrameClock.FrameQpc = _palFrameClock.FrameQpc;
            FluentGpu.Hooks.FrameClock.PresentQpc = _palFrameClock.PresentQpc;
        }

        long db = 0, dt = 0;
        if (s_allocDiag) { db = GC.GetAllocatedBytesForCurrentThread(); dt = Stopwatch.GetTimestamp(); }
        long diagUiStart = db;

        _ring.Clear();
        long gapT = Stopwatch.GetTimestamp();                 // UI-gap segments (AppHost.UiGap.cs)
        _pumpedEvents = _window.PumpInto(_ring);              // 1 pump
        _ledgerPumpQpc = GapSegment(ref _gapMessagesTicks, gapT);
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
            _ledgerExit = LedgerFrameExit.Closed;
            LastStats = new FrameStats(0, 0, 0, Rendered: false) { Fps = _fps, PresentFps = _presentFps, PresentedSequence = this.PresentedSequence, FrameMs = _frameMs };
            NoteNoPresentTurn();   // closing: no present this turn either (see _lastNoPresentQpc's doc)
            return LastStats;
        }

        ReadOnlySpan<InputEvent> inputEvents = _ring.Drain();
        uint inputKindMask = 0;
        for (int i = 0; i < inputEvents.Length; i++) inputKindMask |= 1u << (int)inputEvents[i].Kind;
        gapT = Stopwatch.GetTimestamp();
        int clicks = _dispatcher.Dispatch(inputEvents, _ring.DrainVelocitySamples());  // 2 input dispatch (handlers write signals → schedule effects)
        GapSegment(ref _gapInputTicks, gapT);
        if (s_allocDiag) { db = Probe(SegDispatch, db, dt); dt = Stopwatch.GetTimestamp(); }
        // Passive hover/motion must not pin the host at display rate. Only interaction-semantic input (press/release,
        // keyboard, wheel or a scroll phase) and an actual handled click arm the post-input cadence hold.
        if (_warmCadenceEnabled && WarmCadenceHoldMs > 0f
            && (clicks > 0 || (inputKindMask & WarmCadenceInputMask) != 0))
            _warmCadenceUntilMs = _timers.NowMs + WarmCadenceHoldMs;

        // Step 4 fault injection (--fg device-lost=N=<frameN>): force a controlled DEVICE_REMOVED so the next submit
        // fails and the recovery rendezvous below is exercised on real hardware.
        if (s_forceLostFrame > 0 && _asyncActive && ++_frameOrdinal == s_forceLostFrame)
        {
            Diag.Line($"[dl] UI: injecting device loss at frame {_frameOrdinal}");
            _device.InjectDeviceLost();
        }

        // Step 4 (async): device-lost recovery handshake. The render thread records a lost reason (a failed submit/present
        // or a bounded fence-wait timeout on a removed device). On the 0→1 edge: dirty the whole tree + relayout, ask the
        // render thread to rebuild (waking it so it reaches the recover gate), then BLOCK (render nothing) until RecoverDone
        // — then re-realize resident images and fall through to a full re-recorded frame against the rebuilt device.
        if (_deviceLost is { } dl && _asyncActive)
        {
            // Live GPU-adapter switch (Settings > About picker → GpuAdapterInfo.RequestAdapterSwitch): when no recovery
            // is already in flight, drive the SAME rendezvous a device loss takes. InjectDeviceLost() (as the
            // --fg device-lost=N hook above does — on the UI thread) tears the device down cleanly; setting
            // RecoverRequest + waking reaches the render loop's recover gate, which re-runs InitDevice honoring the new
            // PreferredAdapterLuid (re-logs [d3d12.adapter]). Consuming the flag here (render-owned device seam) keeps
            // the switch on the device-owning thread and avoids a UI-thread RemoveDevice race.
            if (dl.RecoverRequest == 0 && _device.ConsumeAdapterSwitchRequest())
            {
                Diag.Line($"[dl] UI: adapter-switch requested at frame {_frameOrdinal} → injecting device loss + requesting recover");
                _device.InjectDeviceLost();
                _scene.MarkAllPaintDirty();
                _repaintTargetValid = false;   // the rebuilt target holds nothing — the next frame repaints in full (§13.1)
                _needFullLayout = true;
                dl.RecoverRequest = 1;
                _renderThread!.WakeAsync();   // CRITICAL: wake the parked render loop so it reaches the recover gate
            }
            if (dl.RecoverRequest == 0 && _device.PollDeviceLost() != 0)
            {
                Diag.Line($"[dl] UI: detected reason=0x{_device.PollDeviceLost():X} at frame {_frameOrdinal} → requesting recover");
                _scene.MarkAllPaintDirty();
                _repaintTargetValid = false;   // the rebuilt target holds nothing — the next frame repaints in full (§13.1)
                _needFullLayout = true;
                dl.RecoverRequest = 1;
                _renderThread!.WakeAsync();   // CRITICAL: wake the parked render loop so it reaches the recover gate
            }
            if (dl.RecoverRequest != 0)
            {
                if (dl.RecoverDone != 0)
                {
                    Diag.Line($"[dl] UI: observed RecoverDone at frame {_frameOrdinal} → re-realizing images + resuming");
                    dl.RecoverDone = 0;
                    dl.RecoverRequest = 0;
                    _navThrottleFrames = PostRecoveryThrottleFrames;   // drip the re-realize upload burst so the fresh (weak) device doesn't re-hang
                    _images.ReRealizeAllResident();   // re-decode resident art → re-upload to the fresh store (Step-1 handoff)
                    // fall through: the whole-tree-dirty + full-layout frame re-records everything against the rebuilt device
                }
                else
                {
                    _ledgerExit = LedgerFrameExit.Recovering;
                    LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, PresentFps = _presentFps, PresentedSequence = this.PresentedSequence, FrameMs = _frameMs };
                    NoteNoPresentTurn();   // blocked on recovery: no present this turn (see _lastNoPresentQpc's doc)
                    return LastStats;   // block cleanly; the render thread's windowWake nudges us when RecoverDone flips
                }
            }
        }

        // UI-owned cold work is reachable without Paint, including while minimized. The previous committed scene
        // is still intact (posts/reactive flush have not run); this never requests a frame or touches GPU owners.
        gapT = Stopwatch.GetTimestamp();
        RunColdMaintenance();
        GapSegment(ref _gapColdTicks, gapT);

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
        // Window lifecycle relay (AppHost.WindowStateChanged): one placement + visibility sample per frame, parked frames
        // included, raised BEFORE the park gate so a minimize-to-tray handler hears the minimize in the frame it happened.
        // A handler may hide/show/restore the window, so the park decision below reads a FRESH sample after it ran.
        WindowStatus windowStatus = new(_window.State, _window.IsVisible);
        if (_windowStateRelay.TryAdvance(windowStatus, out WindowStateChange windowChange))
        {
            WindowStateChanged?.Invoke(windowChange);
            windowStatus = new(_window.State, _window.IsVisible);
        }
        // A detached child whose window DWM has cloaked for a sustained period parks exactly like a hidden one (the
        // debounce keeps a shell transition from flapping it). Sampled AFTER the relay: the app-facing WindowStateChanged
        // reports the window's own placement/visibility, which a cloak does not change.
        // An unrevealed pop-out (created hidden, F115) is neither hidden-parked nor cloak-parked: its first frame has to paint into
        // the hidden window for the reveal to have anything to show.
        if (_isDetachedChild && !_revealPending) _cloakParked = _cloakGate.Advance(_window.IsCloaked, _timers.NowMs);
        // A window parks while other windows completely cover it (F118): the DXGI occlusion latch does not fire for a flip-model
        // composition swapchain another top-level covers, so without this it kept recording and presenting invisible frames on the
        // render thread it shares with the pop-out (a fullscreen pop-out over the primary window, or any other window over either).
        // Sampled after the relay, like the cloak.
        UpdateCoverPark();
        bool minimized = (windowStatus.Parked && !_revealPending) || _cloakParked || _coverParked;   // "minimized" below means PARKED: minimized, hidden OR cloaked (E2 — identical cost)
        // InputHooks.WindowOccluded — HERE, above the park and idle gates, so it is published on EVERY frame and not only
        // the ones that reach Paint (an occlusion edge on an idle host would otherwise never be heard). A change schedules
        // its readers, which is RuntimePending: the idle gate below falls through to Paint (or the park branch flushes).
        PublishWindowOccluded(minimized);
        bool restoreEdge = _wasParked && !minimized;
        int restorePosts = 0, restoreTimers = 0;
        long restoreDrainT0 = 0;
        if (restoreEdge) { restorePosts = _uiPosts.Count; restoreTimers = _timers.Count; restoreDrainT0 = Stopwatch.GetTimestamp(); }
        bool drainedPosts = !_uiPosts.IsEmpty;
        if (drainedPosts) { gapT = Stopwatch.GetTimestamp(); _runtime.Batch(DrainUiPosts); GapSegment(ref _gapPostsTicks, gapT); }
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
            // Idle branch (keeps the loop AWAKE), it is absent from GovernorNeverPace so it can never defeat the GPU
            // governor, and it self-expires after WarmCadenceHoldMs off the same wall clock. A restore is simply another
            // interaction edge. (It IS outside CadenceWake, so while the hold is live the loop produces at the display
            // rate rather than at a row's cadence — that is the hold's whole purpose: no cold-start ramp.)
            if (_warmCadenceEnabled && WarmCadenceHoldMs > 0f)
                _warmCadenceUntilMs = _timers.NowMs + WarmCadenceHoldMs;
        }
        if (_wasParked != minimized)
        {
            // Window-visibility EDGE → update the Activation.IsActive signal so every component's UseIsActive flips and
            // UseActivation fires. On the minimize-ENTERING edge the gate below returns BEFORE Paint's reactive flush,
            // so flush ONCE here (one-shot, on the edge only — not per idle frame) so onDeactivated runs while invisible.
            // The restore edge forced _frameNeeded above, so its onActivated rides Paint's normal flush.
            UpdateWindowVisible();
            Volatile.Write(ref _renderVisible, minimized ? 0 : 1);
            OwningRenderThread?.WakeAsync();
            if (minimized) FlushToQuiescence();
        }
        _wasParked = minimized;
        if (minimized)
        {
            // OS events stashed for app code (a second-launch redirect, a thumbnail-toolbar click, a Back/Forward command,
            // explorer's taskbar-button re-creation) are normally re-raised at the top of Paint — which never runs while
            // parked. Deliver them here, before the flush below, or a hidden tray app could never be woken by a second
            // launch and a minimized one would sit on a thumbnail click until restored. The OS colour change stays with
            // Paint: its handler feeds this frame's theme detection, which only Paint runs (it lands on the un-park frame).
            DeliverPendingPlatformEvents(includeSystemColors: false);
            // The hoisted drain above ran, but Paint — the only _runtime.Flush() call site on the normal path — does not.
            // Flush here after a NON-EMPTY drain so the reactive pending queue does not simply become the new
            // accumulator: the posted signal writes are applied (memos recompute, effects run, components re-render into
            // the scene) instead of piling up until the restore frame. Same intent as the minimize-EDGE flush above, now
            // per drained minimized frame; a frame with nothing drained costs nothing.
            if (drainedPosts || _runtime.HasPending) FlushToQuiescence();
            _ledgerExit = LedgerFrameExit.Parked;
            LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, PresentFps = _presentFps, PresentedSequence = this.PresentedSequence, FrameMs = _frameMs };
            // Nothing presented this frame (minimized: Paint never runs) — the next present must not be charged for
            // the gap this created (see _lastNoPresentQpc's doc on NotePresented).
            NoteNoPresentTurn();
            // Awake-but-skipped: counts toward _framesRun + _framesMinimized (rendered:false), the wake-diag's
            // "frames spent minimized" signal. wake is recomputed here since the census snapshot is taken below.
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
        // Computed ONCE per frame: the idle gate below, the census, and Paint's own reads (_paintWake) all use this mask.
        WakeReasons wake = ComputeWakeReasons();   // always-on census input; allocation-free field reads
        _ledgerWake = (uint)wake;                  // the frame ledger's wake census (a plain store)

        // Warm cadence ALONE is not work: the hold only keeps the loop waking on the display tick for a second after the
        // last interaction, so the next one pays no cold-start ramp. A frame woken by nothing else (no input this turn, no
        // post, no other wake bit) has nothing to reconcile, lay out, record or present, and used to run the whole Paint
        // and publish a byte-identical scene for every tick of that second (60-165 full frames per click). It now takes the
        // idle turn below: the wait stays the display-tick wait (RecommendedWaitMs still sees the hold), so input latency is
        // unchanged — any real input ends the wait and arrives with its own bits set.
        bool warmCadenceOnly = wake == WakeReasons.WarmCadence && inputKindMask == 0 && clicks == 0 && !drainedPosts;

        if (wake == WakeReasons.None || warmCadenceOnly)   // == !HasActiveWork, without a second ComputeWakeReasons
        {
            AdvanceImagePresentationClock(); // completed hidden/idle decodes must not inherit a stale reveal start
            int completed = PumpsSharedImages ? _images.Pump() : 0;   // a pop-out leaves the shared cache to the primary (F108)
            if (s_allocDiag) db = Probe(SegImages, db, dt);
            if (completed == 0)
            {
                // E5 (design-engine-images.md): this IS the dominant elided turn for a real (non-headless-test) idle
                // app — no reconcile, no layout, no Paint at all, so SubmitPresentOnRenderThread's own reclaim points
                // never run either. A retired image sitting behind an already-signaled fence must not wait for the
                // NEXT active frame (which may be seconds away at rest) to be released. Async gate off only — under
                // Async/ForceSync the render thread owns every device touch and reclaims on its own turns instead.
                if (!_asyncActive) _device.ReclaimCompletedUploads();
                _ledgerExit = LedgerFrameExit.Idle;
                if (warmCadenceOnly)
                {
                    _warmCadenceIdleTurns++;
                    // Headless time IS the frame clock (one fixed step per painted frame), so a warm turn that no longer
                    // paints must still let the hold's time pass, as the wall clock does under a real window, or the hold
                    // would never expire there. Here only: a turn whose decode completed falls through to Paint, which
                    // advances the clock itself.
                    if (_isHeadless) _frameClockMs += _frameTime.NextDeltaMs();
                }
                LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, PresentFps = _presentFps, PresentedSequence = this.PresentedSequence, FrameMs = _frameMs };
                // Genuinely idle — no active work, no completed image (the deep-idle case: streak/idleAgo in the
                // [wake] census). Nothing was owed a present during this stretch; stamp so the NEXT present (the
                // wheel notch that wakes us) rebases instead of charging the whole idle gap as missed vsyncs.
                NoteNoPresentTurn();
                if (_wakeDiag is not null) { _wakeDiag.Record(wake, awake: false, rendered: false, reconciled: false, laidOut: false, minimized: IsParked); _wakeDiag.MaybeReport(); }
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
            wake = ComputeWakeReasons();   // a completed decode forced this paint → re-attribute (now FrameNeeded)
            _ledgerWake = (uint)wake;
        }

        // F098: a wake whose ONLY reason is a coalesced video pump (a native state edge, a transport command, a geometry request)
        // almost never changes a pixel of the UI, and used to cost a reconcile-free but full scene capture + publish here and a
        // record + hash on the render thread to move at most a few DirectComposition properties. Run the pump now and, when it
        // wrote nothing a frame must carry, stop; when it wrote only registry intents, park their snapshot for the render
        // thread's early video drain (a video-only post, no capture / record / present). Anything else it touched (a signal, a
        // dirty layout) takes the ordinary frame below.
        if (wake == WakeReasons.VideoPumpPending && inputKindMask == 0 && clicks == 0 && TryRunVideoOnlyTurn())
        {
            _ledgerExit = LedgerFrameExit.VideoOnly;
            LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, PresentFps = _presentFps, PresentedSequence = this.PresentedSequence, FrameMs = _frameMs };
            NoteNoPresentTurn();   // no present this turn (see _lastNoPresentQpc's doc)
            if (_wakeDiag is not null) { _wakeDiag.Record(wake, awake: true, rendered: false, reconciled: false, laidOut: false, minimized: false); _wakeDiag.MaybeReport(); }
            if (_memCensus is not null) _memCensus.MaybeReport();
            if (s_allocTypes) AllocTypeProfiler.MaybeReport();
            if (s_allocDiag) _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
            return LastStats;
        }

        // Production gate (see ProductionGateBlocks). Deliberately the LAST thing before Paint: the pump, the input
        // dispatch, the close/device-lost/minimize gates and the image pump above have all already run, so nothing that
        // affects correctness or input latency is skipped — only the production of a frame that could not have been
        // shown. Reported Rendered:false, which is already the shape of the five other early-outs in this method.
        if (ProductionGateBlocks())
        {
            _ledgerExit = LedgerFrameExit.Gated;
            LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, PresentFps = _presentFps, PresentedSequence = this.PresentedSequence, FrameMs = _frameMs };
            // Awake (input/wake reasons were live) but production was gated — nothing was submitted, so the next
            // present must not be charged for this turn's slice of the gap (see _lastNoPresentQpc's doc).
            NoteNoPresentTurn();
            if (_wakeDiag is not null) { _wakeDiag.Record(wake, awake: true, rendered: false, reconciled: false, laidOut: false, minimized: false); _wakeDiag.MaybeReport(); }
            if (s_allocDiag) _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
            return LastStats;
        }

        if (s_allocDiag) _diagUiBytes += GC.GetAllocatedBytesForCurrentThread() - diagUiStart;
        _lastProducedTickSeq = _frameTickSeq;   // this frame is the one produced for the current compositor tick
        ulong publishedBefore = _renderSeam.PublishSeq;
        _paintWake = wake;   // Paint reads this frame's mask instead of recomputing it (step-up guard, publication gate)
        FrameStats painted;
        try { painted = Paint(clicks); }
        finally { _paintWake = UnknownPaintWake; }
        if (_wakeDiag is not null)
        {
            // Awake frame: classify reconciled/layout-only/record-only from FrameStats (Rendered = reconciled||layoutNeeded), and
            // whether it handed the renderer a scene (the publication counter moved) so the line can say what the present was for.
            _wakeDiag.Record(wake, awake: true, rendered: painted.Rendered, reconciled: painted.ComponentsRendered > 0,
                             laidOut: painted.Rendered, minimized: IsParked, published: _renderSeam.PublishSeq != publishedBefore);
            _wakeDiag.MaybeReport();
        }
        if (_memCensus is not null) _memCensus.MaybeReport();
        if (s_allocTypes) AllocTypeProfiler.MaybeReport();
        // --fg render tripwire (folds away in release). Both callees already early-out on !Enabled, so hoisting it
        // into the guard is behaviour-identical — and the non-const operand keeps the folded body off CS0162.
        if (RenderBudget.CompiledIn && RenderBudget.Enabled) { RenderBudget.FrameBoundary(); RenderBudget.MaybeReport(); }
        return painted;
    }

    private long _videoOnlyQuietTurns;
    // Test seam (F098): lets a host that never goes async take the video-only turn, so a headless gate can observe the post.
    private bool _videoOnlyTurnInline;

    /// <summary>Video-only turns that found the pump wrote nothing a frame must carry (cumulative): no capture, no post, no wake.</summary>
    internal long VideoOnlyQuietTurns => Volatile.Read(ref _videoOnlyQuietTurns);

    /// <summary>Video-only posts handed to the render thread instead of a publication (cumulative, F098).</summary>
    internal long VideoOnlyPosts => _renderSeam.VideoOnlyPosts;

    /// <summary>Test-only (F098): take the video-only turn on a host without a render thread. The post then stays in the seam
    /// (nothing drains it), so the gate reads it with <c>TryTakeVideoOnlyForTest</c>.</summary>
    internal bool VideoOnlyTurnInlineForTest { get => _videoOnlyTurnInline; set => _videoOnlyTurnInline = value; }

    /// <summary>Test-only (F098): take the parked video-only post, as the render thread's early drain would.</summary>
    internal bool TryTakeVideoOnlyForTest(FluentGpu.Media.VideoPresentIntent[] destination, out int count)
        => _renderSeam.TryTakeVideoOnly(destination, out count);

    /// <summary>UI thread, <see cref="RunFrame"/>, wake reason exactly <see cref="WakeReasons.VideoPumpPending"/> (F098): run the
    /// coalesced video pump and decide whether the frame it would have ridden is owed. Returns true when the turn is complete
    /// (nothing for Paint): the pump wrote no intent, or its intents went to the render thread as a video-only post. Returns
    /// false when a real frame is owed - the pump wrote a signal or dirtied layout, a full publication is still outstanding, the
    /// table holds a pending release, or the host is not one whose render side records (headless / single-thread / force-sync
    /// keep the one-publication-per-pump path, which their skip-submit already elides). The pump has then already run and Paint
    /// carries its intents.
    /// <para>Safe because nothing else asked for this frame: the wake mask is the pump alone (no reconcile, animation, scroll,
    /// image or input work), layout is clean and nothing wrote a transform, so the scene the render thread holds is exactly the
    /// one the UI would capture and every hole in it is where the video will be placed. A destroy and a placement that moves with
    /// a hole stay with the publication that carries the hole: a post is refused while a release is pending or a full publication
    /// is outstanding (see <see cref="Threading.SceneFramePublisher.TryPostVideoOnly"/>).</para></summary>
    private bool TryRunVideoOnlyTurn()
    {
        bool threaded = _asyncActive && OwningRenderThread is not null;
        if (!threaded && !_videoOnlyTurnInline) return false;
        if (!_everLaidOut || _needFullLayout || _revealPending || _scene.Root.IsNull) return false;
        if (_scene.AnyLayoutDirty || _scene.AnyTransformWrote) return false;
        _videoSurfaces.PumpPending(_scene.DeviceScale);
        // The pump wrote a signal (a readiness edge, a state mirror) or dirtied layout: the frame it needs is owed, and Paint carries
        // this pump's intents like any other. The pump consumed the pending-pump wake bit, so the owed frame is named explicitly: if
        // the production gate declines it this tick, the next RunFrame still finds a reason to produce it.
        if (_runtime.HasPending || _frameNeeded || _scene.AnyLayoutDirty)
        {
            _frameNeeded = true;
            return false;
        }
        if (!_videoSurfaces.HasUnpublishedChanges)
        {
            Interlocked.Increment(ref _videoOnlyQuietTurns);
            return true;
        }
        if (_videoSurfaces.HasReleasePending || !_renderSeam.TryPostVideoOnly(_videoSurfaces))
        {
            _frameNeeded = true;   // the intents ride an ordinary publication (see above)
            return false;
        }
        OwningRenderThread?.WakeForVideo();   // the early video drain (preTurn) applies the post: no publication, record or present behind it
        return true;
    }

    /// <summary>True when nothing of the host window is on screen: minimized (PAL <see cref="Pal.WindowState.Minimized"/>)
    /// OR hidden (<see cref="IPlatformWindow.IsVisible"/> false — a close-to-tray app), OR (a detached child only) cloaked by
    /// the OS compositor for a sustained period (<see cref="IPlatformWindow.IsCloaked"/>, debounced by
    /// <see cref="CloakParkGate"/>). A parked host produces no frames: no reconcile, layout, record or present,
    /// <c>UseIsActive</c> reads false, and the loop blocks on messages (a cloak-parked child polls for the un-cloak) — the
    /// minimized behaviour, now shared by the hidden window (<see cref="WindowStatus.Parked"/>). The PRIMARY host also parks
    /// while a fullscreen, active pop-out covers it entirely (<see cref="WindowCoverPolicy"/>, F118) and raises
    /// <c>InputHooks.WindowOccluded</c> like any parked window. Live read of the window (the cloak and cover halves are the
    /// last frame's sample).</summary>
    public bool IsParked => IsOsParked || _cloakParked || _coverParked;

    /// <summary>Minimized or hidden — the states whose restore is a window message (unlike a cloak, which raises none).</summary>
    private bool IsOsParked => !_revealPending && new WindowStatus(_window.State, _window.IsVisible).Parked;

    /// <summary>Any host, UI thread, once per <see cref="RunFrame"/>: re-derive <see cref="_coverParked"/> from the two coverage
    /// causes (F118). (1) The primary host's live detached children: only a FULLSCREEN child is ever measured (a field read), so a
    /// host with no pop-out, or with a windowed or snapped one, pays nothing; the verdict is <see cref="WindowCoverPolicy.Covers"/>:
    /// fullscreen AND active (an alt-tab away un-parks at once) AND on screen AND its rect contains this window's, so a main
    /// window on another monitor, or spanning two, is never parked. (2) The OS (<see cref="OsOcclusionCovers"/>): the union of the
    /// opaque top-level windows above this one, recomputed only when the backend's occlusion epoch moved. Logs the edges only.</summary>
    private void UpdateCoverPark()
    {
        bool popOut = false;
        for (int i = 0; i < _detachedHosts.Count && !popOut; i++)
        {
            var child = _detachedHosts[i];
            var w = child._window;
            if (w.IsClosed || !w.IsFullscreen) continue;
            popOut = WindowCoverPolicy.Covers(childFullscreen: true, childActive: w.IsActive,
                childVisible: !child._revealPending && !child.IsParked, w.OuterBoundsPx, _window.OuterBoundsPx);
        }
        // Always read (not short-circuited by the pop-out cause): it keeps the epoch / cached verdict current across a pop-out cover.
        bool windows = OsOcclusionCovers();
        bool covered = popOut || windows;
        if (covered == _coverParked) return;
        _coverParked = covered;
        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[occlusion] hwnd={_window.Handle.Value:X} {(covered ? "covered: parked" : "uncovered: unparked")} by={(popOut ? "fullscreen-pop-out" : windows ? "windows" : "none")}"));
    }

    /// <summary>UI thread (F118): is this window completely hidden behind the opaque top-level windows above it? The backend's
    /// win-event hooks bump <see cref="IPlatformWindow.OcclusionEpoch"/> (and wake the loop) whenever the set, the Z-order or the
    /// geometry of top-level windows may have changed, so the Z-order walk and the rect test run only then; between events the
    /// cached verdict stands, so an idle covered window costs one counter read per frame. Never covered while it is not on screen
    /// anyway (minimized, hidden, cloaked: their own park gates), while a pop-out waits for its reveal or is parked warm, or when
    /// the backend does not track occlusion (epoch 0). Uncovering is seen on the same frame the event woke the loop.</summary>
    private bool OsOcclusionCovers()
    {
        var w = _window;
        long epoch = w.OcclusionEpoch;
        if (epoch == 0) { _occlusionEpochSeen = 0; _occludedByWindows = false; return false; }
        if (_revealPending || _warmParked) { _occlusionEpochSeen = 0; _occludedByWindows = false; return false; }   // re-evaluated on the first frame it counts
        if (epoch == _occlusionEpochSeen) return _occludedByWindows;
        _occlusionEpochSeen = epoch;
        bool covered = false;
        var target = w.OuterBoundsPx;
        if (!w.IsClosed && w.IsVisible && w.State != WindowState.Minimized && !w.IsCloaked && target.W > 1f && target.H > 1f)
        {
            _occluderRects ??= new RectF[WindowCoverPolicy.MaxOccluders];
            int n = w.CopyOccluderRectsPx(_occluderRects);
            covered = n > 0 && WindowCoverPolicy.CoveredByWindows(target, new ReadOnlySpan<RectF>(_occluderRects, 0, n));
        }
        _occludedByWindows = covered;
        return covered;
    }

    /// <summary>Raised on the UI thread when the window's placement or visibility changed since the previous frame —
    /// minimized, restored, maximized, hidden, shown (<see cref="WindowStateChange"/> carries both samples and the edge
    /// flags). Sampled once per <see cref="RunFrame"/> after the pump, parked frames included, and raised before the
    /// park gate, so a minimize-to-tray handler can <see cref="IPlatformWindow.Hide"/> the window in the frame it was
    /// minimized. The first frame only seeds the relay. A handler may show, hide or restore the window; the host
    /// re-samples before deciding whether this frame is parked.</summary>
    public event Action<WindowStateChange>? WindowStateChanged;

    /// <summary>Recompute and publish the ambient window-visibility (<c>Activation.IsActive</c>): visible IFF not
    /// parked (minimized or hidden) AND not app-suspended. Value-eq-gated by the signal, so a no-op write notifies
    /// nobody. UI-thread.</summary>
    private void UpdateWindowVisible() => _windowVisible.Value = !IsParked && _windowActiveApp;

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

    /// <summary>Re-raise the OS events the PAL stashed for app code. Called at the top of <see cref="Paint"/> (before the
    /// reactive flush, so a handler's signal writes render this same frame) and from <see cref="RunFrame"/>'s parked
    /// branch (before its flush), because Paint never runs while the window is minimized or hidden. UI-thread only.</summary>
    private void DeliverPendingPlatformEvents(bool includeSystemColors)
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
        // Paint-only (includeSystemColors): theme detection is Paint's, so a parked window takes it on the un-park frame.
        if (includeSystemColors && _pendingSystemColors)
        {
            _pendingSystemColors = false;
            SystemColorsChanged?.Invoke();
        }
        if (_pendingThumbClick)
        {
            _pendingThumbClick = false;
            ThumbButtonClicked?.Invoke(_pendingThumbButtonId);
        }
        if (_pendingAppNavigation)
        {
            _pendingAppNavigation = false;
            AppNavigationCommand?.Invoke(_pendingAppNavigationWhich);
        }
        if (_pendingTaskbarButtonCreated)
        {
            _pendingTaskbarButtonCreated = false;
            TaskbarButtonCreated?.Invoke();
        }
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
        if (_inPaint) { _frameAfterPaint = true; NoteNoPresentTurn(); return LastStats; }
        _inPaint = true;
        _anim.RenderOwnsCompositor = OwningRenderThread is not null;
        Volatile.Write(ref _renderPeriodTicks, RefreshPeriodQpcOrDefault());
        // Publish the effective device scale for scroll content-transform device-pixel rounding (before reconcile/layout).
        float deviceScale = _window.Scale <= 0f ? 1f : _window.Scale;
        bool scaleChanged = deviceScale != _scene.DeviceScale;
        _scene.DeviceScale = deviceScale;
        if (scaleChanged) _reconciler.RetargetImagesForScale(deviceScale);   // explicit-extent images follow the display's density
        _reconciler.FrameEpoch++;   // one tick per paint
        long diagUiStart = s_allocDiag ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            DeliverPendingPlatformEvents(includeSystemColors: true);

            long frameStart = Stopwatch.GetTimestamp();
            GapPaintStart(frameStart);                         // close + classify the UI gap since the last Paint (AppHost.UiGap.cs)
            long shapeCountAtFrameStart = _fonts.ShapeCount;   // P0: FrameStats.TextShapes reads the delta across this Paint
            _reconciler.BeginRenderCensus();
            Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.WindowResize, _window.InModalLoop);
            // --fg resize: per-tick segment timing of the modal-loop keep-alive paint. Captured only when both the flag
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
            //      animation row is DUE — bail even though an autonomous wake (playback seek-ticker, caret blink,
            //      perpetual brush/spinner loop) is live. Measured: a single edge-resize-while-playing fired 69
            //      real resizes but 564 REDUNDANT present-only paints (~1.8s of wasted WndProc time, present-blocked up
            //      to 62ms each) because the seek-ticker wake kept defeating case (1). Those PERPETUAL animations can't
            //      advance mid-drag anyway (the frame loop is suspended), so painting the unchanged content for them is
            //      pure waste that starves the modal loop → felt as sluggish resizing.
            //      NextDueMs() > 0 is the exception that keeps responsive-control motion alive, and it is now the
            //      SOURCE's own answer rather than the host's guess: a ONE-SHOT layout transition (a PlayerBar button's
            //      Enter/Exit pop when it crosses a responsive breakpoint mid-resize) is due every frame, so its due
            //      time is 0 and we DON'T bail — the button animates in/out while only the perpetual playback ticker
            //      (paced at its own sub-refresh cadence, so not due on most of these ticks) is dropped. A real resize /
            //      band-crossing relayout still paints; WM_EXITSIZEMOVE flushes any deferred work in one settle frame,
            //      so nothing visible is lost. Warming / budget-deferred virtual lists (own wake bits) and any other
            //      essential wake bit still paint — NoEssentialModalWakeReasons masks them off so a seek ticker cannot
            //      starve mid-drag refill.
            // Only a keep-alive repaint asks: the mask is computed for it alone, never on the ordinary RunFrame path.
            if (keepAlive && !resized && _everLaidOut && !_needFullLayout
                && _uiPosts.IsEmpty && !_scene.AnyLayoutDirty
                && ComputeWakeReasons() is var wakeReasons
                && (wakeReasons == WakeReasons.None
                    || (_window.SizedInModalLoop && _anim.NextDueMs(_timers.NowMs) > 0f
                        && NoEssentialModalWakeReasons(wakeReasons))))
            {
                // Modal-loop keep-alive idle skip: the whole pipeline (record/submit/present) was skipped, not just
                // elided post-record — no present this turn (see _lastNoPresentQpc's doc).
                NoteNoPresentTurn();
                return LastStats;
            }

            var layoutSize = LayoutSizeForFrame(keepAlive);
            PublishViewport(layoutSize);

            // ── Scroll frame step — phase 2.5 (scroll rework §2/§6) ─────────────────────────────────────────────
            // Every viewport's plan is evaluated at THIS frame's predicted present time: the offset is the INPUT to
            // realization (the virtualizer windows on it) and to layout, so it must be current before either runs.
            //
            // fps consistency: if the loop paced INTO this frame from a gap that was NOT chosen for the work this frame
            // is about to tick, that gap is stale — drop it so the first active frame advances ~one frame instead of
            // leaping ~34 ms. "Not chosen for it" is decidable from the branch that produced the wait: Idle / Hud /
            // Baked never paced animation, so their gap is always stale; a Cadence wait IS the earliest row's own period
            // UNLESS it was cut short by work the cadence branch would have refused; an AdaptiveGpu wait is a
            // measurement-chosen period for exactly this frame's motion (and a PowerCap wait a policy-chosen one), so
            // neither is ever stale.
            if (!_lastWaitWasDisplayRate)
            {
                // RunFrame's mask for this frame when it is the caller (computed a few field reads ago); recomputed otherwise.
                WakeReasons stepUp = _paintWake != UnknownPaintWake ? _paintWake : ComputeWakeReasons();
                bool staleGap = _lastWaitKind is HostWaitKind.Idle or HostWaitKind.Hud or HostWaitKind.Baked
                             || (_lastWaitKind == HostWaitKind.Cadence && (stepUp & ~CadenceWake) != 0);
                if (staleGap || _connected.HasActive || _runtime.HasPending)
                    _frameTime.Resync();
            }
            float dtMs = _frameTime.NextDeltaMs();
            AdvanceImagePresentationClock(); // reconcile-time image swaps must also start from current wall time
            _frameClockMs += dtMs;                             // frame-clock timer base (headless: the deterministic FixedFrameTimeSource step; ignored by the real-window wall clock)
            _scene.AnimClockMs = _frameClockMs;                // publish the ANIMATION timebase for orphan deadlines (same clamped delta the animator advances on)

            // PumpScroll: a frame-aligned producer (DirectManipulation) issues its ONE Update for THIS frame's
            // _palFrameClock here — after the display-phase gate, so it never fires against a declined instant — and
            // its contact events dispatch through the ordinary input path (InputKind.Scroll).
            _scrollPumpRing.Clear();
            int scrollPumped = _window.PumpScroll(in _palFrameClock, _scrollPumpRing);
            if (scrollPumped > 0)
                _dispatcher.Dispatch(_scrollPumpRing.Drain(), _scrollPumpRing.DrainVelocitySamples());

            // The frame step: plans → this frame's offsets/motion, realize windows, chrome, the FLIP-suppression latch
            // (a reconcile landing while a user scroll moves content this frame or the last must SNAP, not fly).
            RunScrollFrame(_palFrameClock.PresentQpc / (double)Stopwatch.Frequency);

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

            // Cold UI preflight only: capacity reclamation may allocate while warming a replacement snapshot.
            // Do it before the protected-phase allocation baseline, never during capture/publish or a modal repaint.
            // Pending structural work would immediately invalidate the observed low-water demand, so defer then.
            if (!willReconcile && !resized && !keepAlive && _anim.RenderOwnsCompositor)
                _renderSeam.TryReclaimSceneCapacity(_scene, _images, _strings,
                    _connected.Detached, _popupWindows, _anim);

            long before = GC.GetAllocatedBytesForCurrentThread();
            _rebindFlushAllocBytesThisFrame = 0;   // P0: FrameStats.RebindFlushAllocBytes accumulates across this Paint's FlushRebindsToQuiescence call(s)
            _rxUnitsThisFrame = 0; _rxLongestTicksThisFrame = 0; _rxLongestUnitThisFrame = null;   // FrameStats.Reactive* (every flush of this Paint)

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
            if (_frameClockSig.HasSubscribers || _frameClockPaceableSig.HasSubscribers)
            {
                ++_frameClock;
                if (_frameClockSig.HasSubscribers) _frameClockSig.Value = _frameClock;
                if (_frameClockPaceableSig.HasSubscribers) _frameClockPaceableSig.Value = _frameClock;
            }
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
                FlushToQuiescence();                                            // 3–5 apply scheduled re-renders (render-effects reconcile) + bindings — the whole committed write
                long tRx1 = Stopwatch.GetTimestamp();
                SyncScrollPlansMidFrame();                                      // plans the reconcile just authored (ScrollKey → 0, controller posts) window THIS frame
                virtualsChanged = _reconciler.ReRealizeVirtuals();   // virtual boundary re-realize (granular): EVERY covered row, this frame
                long tVr1 = Stopwatch.GetTimestamp();
                if (virtualsChanged && _runtime.HasPending) FlushRebindsToQuiescence();   // bound-row rebinds (slot signal writes) land THIS frame
                long tRx2 = Stopwatch.GetTimestamp();
                reactiveFlushMs = ToMs(tRx1 - tRx0) + ToMs(tRx2 - tVr1);
                virtualRealizeMs = ToMs(tVr1 - tRx1);
                // Fix C (§5.2): absorb pre-flush mid-paint wakes. Timer drain / UI-post Batch close inside Paint fires
                // FrameRequested → WakeFrame → _frameAfterPaint while _inPaint, but THIS flush already applied those
                // writes. Clearing here leaves post-flush WakeFrame sites (passive effects, popups, theme, …) intact.
                _frameAfterPaint = _runtime.HasPending;
            }
            finally { if (themeChanged) _reconciler.SetThemeTransition(float.NaN); }
            bool reconciled = _reconciler.ConsumeReconciled() || virtualsChanged;
            long tFlush = Stopwatch.GetTimestamp();   // always-on segment timing (FrameStats.*Ms) — see below
            // Spike-gated type roster (RenderCensus): one line when the flush alone exceeded the panel's refresh interval
            // or comps are high; carried on FrameStats.Census. Peek render count WITHOUT consuming it (ConsumeRenderCount
            // runs later when assembling LastStats).
            int censusComps = _reconciler.PeekRenderCount();
            double refreshIntervalMs = RefreshPeriodQpcOrDefault() * 1000.0 / Stopwatch.Frequency;
            string? censusLine = _reconciler.MaybeDumpRenderCensus(refreshIntervalMs, ToMs(tFlush - frameStart), reactiveFlushMs,
                virtualRealizeMs, censusComps, AnyUserScrollMoving);
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
                    _navThrottleFrames = NavThrottleWindowFrames;   // P1b: a full layout is the nav shape → open the upload-throttle window
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
                    if (_runtime.HasPending) FlushRebindsToQuiescence(); // bound-slot rebinds (RowBind) land THIS frame — unbudgeted
                    _reconciler.ConsumeReconciled();           // realize mounts are folded into this frame's layout
                    reconciled = true;
                    _invalidator.RunDirty(layoutSize);
                            _scene.ClearLayoutDirty();
                }
            }
            long tSolve = Stopwatch.GetTimestamp();            // of LayoutMs: the flex solve itself (full or scoped + realize catch-up)

            DrainLayoutEffects();                              // 6.5 layout effects (Bounds valid)
            // A layout effect that scrolled (BringIntoView on the freshly laid-out rows) shows and windows THIS frame:
            // re-evaluate the plans and, when a window moved, realize + scoped-relayout once more (bounded to one pass).
            if (SyncScrollPlansMidFrame() && !_scene.Root.IsNull && _reconciler.ReRealizeVirtuals())
            {
                if (_runtime.HasPending) FlushRebindsToQuiescence();
                _reconciler.ConsumeReconciled();
                reconciled = true;
                _invalidator.RunDirty(layoutSize);
                _scene.ClearLayoutDirty();
            }
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
                    float from = horiz ? fromW : fromH, to = horiz ? nb.W : nb.H;
                    // ItemsView's ItemContainer is a default-Direction row wrapping a column slot. The grandparent
                    // axis then picks WIDTH (unchanged) and this seed no-ops — the measured row snaps closed. If the
                    // chosen axis didn't move, the other one is the disclosure axis.
                    if (MathF.Abs(from - to) < 0.5f)
                    {
                        horiz = !horiz;
                        from = horiz ? fromW : fromH;
                        to = horiz ? nb.W : nb.H;
                    }
                    _anim.SeedReflowResize(pn, horiz, from, to, spec);
                }
                pex.Clear();
            }
            if (reconciled) DumpSceneOnce(layoutSize);
            if (diagTick) { layoutMs0 = ElapsedMs(segStart); }   // flush/reconcile/relayout/layout-effects span (--fg resize)
            long tLayout = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegLayout, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            bool keepAliveSuppressed = _reconciler.ConsumeKeepAliveLayoutSuppressionFrame();
            if (capturedProjections) ApplyProjections(keepAliveSuppressed);       // FLIP "Last+Invert+Play"
            _anim.Tick(dtMs);                                  // 7 animation (transform/opacity/presented-size — never relayout)
            _reconciler.FinalizeKeepAliveTransitions();         // 7 park retained outgoing pages after their exit settles
            _inputHooks.RunAfterAnimations();                  // 7.1 tree lifecycle finalizers (overlays) before record/present
            RunIncrementalLayout();                            // 7 scoped subtree relayout for SizeMode.Relayout
            RunReflowLayout(layoutSize);                       // 7 boundary-scoped re-solve for SizeMode.Reflow (smooth reflow)
            // 7.15 follow-rect (F169): a node that must cover ANOTHER node's rect this frame (the docked video overlay over its
            // hollow reservation) takes size and position from the target's PAINTED rect here: after layout AND the animation
            // tick (so a paint-only slide of the target is already in its transform), before the video geometry scan and
            // record. The size write re-solves only the follower's dirty scope; the position is then a paint translation.
            // A render-owned compositor row would NOT be in that transform (the render thread advances it, the UI sees it only
            // at feedback), so the pass republishes its anchor chains below and the anim engine keeps their translate/scale
            // rows UI-owned while a follower follows (next frame's tick onward; a row already in flight hands over at once).
            bool hasFollowers = _scene.HasFollowers;
            if (hasFollowers)
            {
                if (_scene.SizeFollowRects())
                {
                    _invalidator.RunDirty(layoutSize);
                    _scene.ClearLayoutDirty();
                }
                _scene.PlaceFollowRects();
            }
            if (hasFollowers || _anim.HasFollowAnchors)   // the second term releases the anchors the frame the last follower goes
            {
                _followAnchors.Clear();
                if (hasFollowers) _scene.CollectFollowAnchors(_followAnchors);
                _anim.SetFollowAnchors(_followAnchors);
            }
            // 7.2 video pump: event/geometry/transport requests are coalesced into one post-layout turn per surface.
            // Native DirectComposition video presents decoded frames independently, so a playing video no longer turns
            // every host frame into RepaintCurrentFrame. Render remains pure; registered pumps only write value-gated
            // intents, with fullscreen single-writer ownership enforced by the registry.
            // A surface's on-screen rect can move with NO layout change — a compositor-only Transform translation (a
            // draggable mini-player), a page transition, a FLIP projection. Those move the punched hole but fire no
            // bounds-changed edge, so nothing requested a pump and the DComp child stayed at its last-pumped offset
            // (the video trailing the card during a drag). This compares each tracked surface's absolute rect and
            // requests a pump only when it actually moved — so it observes a frame already being produced and never
            // becomes a wake reason of its own. Must precede PumpPending so the placement drains this same frame turn.
            _videoSurfaces.RequestGeometryPumps(_scene);
            _videoSurfaces.PumpPending(_scene.DeviceScale);
            ReclaimSettledOrphans();                           // 7 free settled exit orphans
            _connected.Settle();                               // 7 retire landed shared-element flies (reveal dest, unpin, free overlay)
            _connected.SyncDetached();                         // 7 flag-gated rebuild: mirror the engine-animated fly into its DetachedNode snapshot (RecordDetached draws it)
            // 7 eased hover/press: HoverT/PressT now driven by the engine's HoverFade/PressFade tracks (ticked in _anim.Tick above); InteractionAnimator deleted
            // 7 implicit BrushTransition: the cross-fade T is now driven by the unified engine (AnimChannel.BrushFade,
            // seeded at reconcile); the separate per-frame AdvanceBrushAnims ticker is deleted.
            bool scrollActive = AnyUserScrollMoving;
            if (_navThrottleFrames > 0) _navThrottleFrames--;   // decay the P1b nav-burst window one frame at a time
            _scrollChrome.Tick(dtMs, _timers.NowMs);           // 7 conscious scrollbar fade/expand (chrome never touches motion; dwells on the timer clock)
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
            // Reveal starts are stamped at the CURRENT active wall time before applying incoming textures. Never
            // drive this clock from dtMs: sparse UI work resyncs that animation delta to zero, but the render thread
            // has already advanced the same image with wall time and a new publication must not rewind its opacity.
            AdvanceImagePresentationClock();
            // 7.5 apply finished decodes + evict (one permanent bytes-per-frame upload cap — never scroll-keyed).
            {
                // The SYNC/inline analogue of the render-thread drain guard (SubmitPresentOnRenderThread): Pump's pixel
                // sink is `_device.TryUploadImage`, a device touch that fails with DXGI_ERROR_DEVICE_REMOVED in exactly
                // the same window as submit/present — and phase 7.5 sits OUTSIDE the submit try below, so the throw used
                // to escape Paint entirely. Route it into the SAME foreground recovery gate; a genuine (non-device-loss)
                // decoder/upload bug still propagates. The backend soft-fails staging first, so this is the net, not the
                // normal path (media-pipeline.md §4.1).
                //
                // A detached child shares the primary's cache and leaves its pump to the primary (PumpsSharedImages, F108).
                if (PumpsSharedImages)
                {
                    try { _images.Pump(long.MaxValue); }
                    catch (Exception ex)
                    {
                        if (!TryRecoverForegroundDeviceLost(ex, clicks)) throw;
                        NoteNoPresentTurn();   // recovered mid-paint, before submit — no present this turn
                        return LastStats;
                    }
                }
            }
            if (_frameTime is not StopwatchFrameTimeSource && PumpsSharedImages) _images.Tick(dtMs); // fixed/manual headless time stays deterministic
            // E1 (design-engine-images.md): every id whose CONTENT changed this pump (LQIP→full, baked-blur replace,
            // a fresh decode landing) marks its owning node(s) paint-dirty, so the recorder's §13.1 block describes the
            // landing as a per-node band instead of the host having to force a full frame for it. Idempotent for ids an
            // ImageStatusChanged event already reached this frame (Mark is itself idempotent) — this sweep only catches
            // the sites that advance ContentEpoch with no status event (RestartDecode/RestartDerived/blur upgrade).
            // The OR'd return feeds `imageContentChanged` below: an id with no owning node (prefetch-only) cannot have
            // changed any SUBMITTED draw-list bytes, so it must not defeat the skip-submit hash shortcut by itself.
            bool anyImageNodeDirtied = false;
            foreach (int id in _images.ContentChangedIds) anyImageNodeDirtied |= _reconciler.MarkImageDirty(id);
            // M5 (adreno-hang-fixes.md): VRAM-pressure eviction, Weak/UMA only. Once per frame, after the apply/tick
            // maintenance, read the device LOCAL-segment usage and evict unpinned LRU when the shed policy says so —
            // armed above 90% of budget, disarmed below 80%, and re-fired only on a genuinely NEW sample after its
            // cooldown (VramShedPolicy.cs): the device only refreshes its usage sample every 10 presents, so reacting
            // on every frame re-shed the same stale overage for up to 10 frames straight, evicting entries a scroll
            // immediately re-requested. Discrete GPUs never read VRAM here (short-circuits on IsWeak); TryGetVramUsage
            // default-returns false so no per-frame alloc. On UMA (the Adreno / iGPU case) DXGI's LOCAL budget is the OS
            // residency budget of the SHARED pool (about 15 GB), not the small dedicated carve-out, so this arms only under
            // real system memory pressure, when the OS shrinks that budget (F251) - which is the right trigger there.
            if (FluentGpu.Foundation.GpuProfile.IsWeak && _device.TryGetVramUsage(out long __vu, out long __vb) && _vramShed.Decide(__vu, __vb))
                _vramShed.NoteShed(_images.EvictToVramPressure(__vb, __vu));
            long tImagePump = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegImages, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            // There is no post-layout scroll re-realize catch-up: the plans are evaluated at phase 2.5 (top of Paint),
            // BEFORE the pre-layout ReRealizeVirtuals and BEFORE ArrangeViewport, so this frame's offset is already the
            // one realize+layout windowed on.
            // A stationary pointer emits no PointerMove while edge auto-scroll moves/recycles the rows beneath it;
            // still re-hit after the frame's realize so the nearest target and its insertion slot follow.
            if (dragEdgeActive) _dispatcher.RefreshDragDropAfterAutoScroll();
            long tRealizeCatchup = Stopwatch.GetTimestamp();

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
            if (_anyScrollMovedThisFrame) _dispatcher.RefreshHoverAfterScroll();
            // Layout-move stuck-hover (input-a11y.md §5.4/§15): a reconcile/relayout this frame — NOT a scroll write — can
            // TRANSLATE a node out from under a STATIONARY mouse/pen cursor with no PointerMove to re-resolve it (the sidebar
            // collapse snapping its 240→56 rail carries its hover-only resize grip away, leaving the grip's seam hairline lit
            // until the next real move). Gated on a frame that actually relaid out (`layoutNeeded` = full/scoped layout ran;
            // steady idle/paint-only frames never enter) — but NOT when a scroll write already refreshed above. The dispatcher
            // self-gates mouse/pen + a valid position + no touch pan/item-drag, and no-ops unless the hit actually CHANGED.
            else if (layoutNeeded) _dispatcher.RefreshHoverAfterLayoutMove();

            PoseScrollUi();                                    // 7.7 UI-side content pose + scroll effects (hit-test/publish truth; the render poser re-poses per tick)
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
            bool recordOnRender = OwningRenderThread is not null;
            var recordStats = _lastRecordStats;
            if (!recordOnRender)
            {
            recordStats = SceneRecorder.Record(_scene, _drawList, _images, in focus, Tok.ScrollThumb, Tok.AcrylicFlyout.Fallback, in textEdit,
                CollectionsMarshal.AsSpan(_popupSkipRoots),
                spans: _spanTable, spanReuseDisabled: spanDisable,
                // Damage the band any structural-track cancel (drag-suppression snap @ ApplyProjections, resize snap @
                // CancelStructuralAll above) vacated this frame — else the ghost rail persists. AsSpan is alloc-free.
                pendingStructuralDamage: CollectionsMarshal.AsSpan(_anim.PendingStructuralDamage), // 8 record
                reuseBlockRoots: CollectionsMarshal.AsSpan(_reuseBlockRoots), // W5 spatial scoping: connected-anim fly anchor chains to block
                collectSpanReuseMisses: RenderBudget.CompiledIn && RenderBudget.Enabled,
                // retained tiles: the UI-thread slice arenas + their composite plan. A detached child under a single-thread
                // parent records STANDALONE (null) instead: it presents through its own swapchain's direct route (F090).
                slices: _isDetachedChild ? null : _uiSlices,
                detached: _connected.Detached);        // 8 detached fly snapshots — the root slice's top-band tail (no-op when none)
            _anim.PendingStructuralDamage.Clear();   // retains capacity → no steady-state alloc
            RecordPopupWindows(in focus, in textEdit);         // 8b record each popup window's subtree DrawList
            }
            else RecordPopupWindows(in focus, in textEdit, prepareOnly: true);
            // E1 (design-engine-images.md): ImageContent is now the NAMED SURRENDER for the case the per-node path
            // above cannot describe (more than 64 ids landed in one pump) — a normal landing is already described by
            // MarkImageDirty's PaintDirty → the recorder's §13.1 band, so it does NOT force a full frame any more.
            // imageContentChanged still has to defeat skip-submit below, but ONLY for a change that could touch the
            // SUBMITTED draw-list bytes: a byte-identical LQIP→full-res swap on a LIVE node changes pixels under
            // UNCHANGED command bytes, which the hash compare cannot see — anyImageNodeDirtied (above) is exactly that
            // set. A changed id with no owning node (prefetch-only) has no draw op to go stale, so it must NOT block
            // the elide — that is what keeps an off-screen landing's otherwise-idle frame skippable.
            bool imageContentOverflow = _images.ContentChangedOverflow;
            bool imageContentChanged = imageContentOverflow || anyImageNodeDirtied;
            if (PumpsSharedImages) _images.ClearContentChanged();   // the ONE clear — read by both the MarkImageDirty sweep above and here first; the pumping host's alone (F108)
            // 8c consume the frame's motion bits. The settle re-snap this used to queue is obsolete: glyph runs are no
            // longer recorded "unsnapped", they carry a 0..255 SOFTNESS that already returns to 0 on its own as the
            // scroll decelerates, so the crisp frame arrives as part of the deceleration rather than as a follow-up.
            // UnsnappedGlyphSpans is consequently always 0 now (SceneRecorder stopped incrementing it) and the branch
            // below is inert — kept so the stat and its consumer stay visibly paired if softness ever gets a hard cutoff.
            // Rect-only transform ticks (EQ bars / seek playhead) must NOT pay a follow-up frame (§5.2 Fix B).
            // transformWrote is snapshotted BEFORE ClearTransformDirty — keep that order.
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
                || clicks > 0 || _pumpedEvents > 0
                || _dispatcher.Drag.IsActive || _dispatcher.DragDrop.IsActive;
            if (transformWrote)
            {
                if (recordStats.UnsnappedGlyphSpans > 0) _frameAfterPaint = true;
                if (!recordOnRender) _scene.ClearTransformDirty();
            }
            if (!recordOnRender) _scene.ClearRecordDirty();
            long tRecord = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegRecord, db, dt0); dt0 = Stopwatch.GetTimestamp(); }
            // Modal-loop repaint (WM_EXITSIZEMOVE settle): present at SyncInterval 0 + skip the latency waitable so the
            // WndProc thread isn't blocked up to a vblank. Mid-drag resize is deferred (no keep-alive paints); this path
            // runs once on mouse-up with the final client size.
            // Skip-submit gate (idle/slow-change power, finding #3a): when this frame mutated nothing the recorder reads
            // (no reconcile, no relayout, no transform write) AND the recorded command stream is byte-identical to the last
            // PRESENTED frame, the already-presented front buffer is still correct — elide the GPU submit + Present (the
            // dominant ~2.5ms/frame cost at rest). Cheap flags short-circuit the hash for skip candidates that already
            // fail a flag; presenting frames ALWAYS hash so the baseline tracks the stream that reached the screen
            // (§5.2 Fix A — without that, an active tick presents S1 while the baseline still holds H(S0) and the next
            // quiet frame re-presents). Conservative: steady main window only (presented before, no resize, not a modal
            // keep-alive, no interleaving popup windows). A playback playhead quantized to whole pixels (SeekBar) lands
            // on the same stream most frames, so this fires during play. Active image reveals resolve at replay time —
            // defeat skip-submit while fades are live.
            bool maybeUnchanged = !recordOnRender && _everLaidOut && !resized && !keepAlive && _popupWindows.Count == 0
                && !reconciled && !layoutNeeded && !transformWrote
                && !imageContentChanged
                && !_device.HasPendingUploads
                && !_swapchain.TextRepaintPending
                && !_bakedBlurQueue.HasRunnableJob
                && !_images.HasActiveCrossfades
                && !_videoSurfaces.HasUnpublishedChanges;   // F070: a video intent the frame has not carried yet is owed a publication
            ulong dlHash = 0UL;
            bool skipSubmit = false;
            if (maybeUnchanged)
            {
                dlHash = InlineStreamHash();
                skipSubmit = dlHash == _lastPresentedDrawListHash;
                if (!skipSubmit) _wakeDiag?.NoteSkipMiss();   // a rate in the [wake] census, not a per-frame stderr line
            }
            // No-op publication skip (render-thread seam; NoopPublicationGate): the inline hash above only ever ran for the
            // single-thread path, so under a render thread EVERY Paint published, and the render side found most of those
            // identical only after a capture, an adoption, a record and a hash. Decided here, on the facts the capture would
            // read: a frame that changed nothing a publication carries publishes nothing and wakes nobody.
            Threading.SceneRecordOptions recordOptions = default;
            PublicationKey publicationKey = default;
            Size2 publishFrameSize = default;
            if (recordOnRender)
            {
                recordOptions = new Threading.SceneRecordOptions(focus, textEdit, Tok.ScrollThumb,
                    Tok.AcrylicFlyout.Fallback, spanDisable,
                    RenderBudget.CompiledIn && RenderBudget.Enabled, Tok.Epoch);
                publishFrameSize = FrameSizePx(keepAlive);
                publicationKey = BuildPublicationKey(in recordOptions, publishFrameSize);
                var block = NoopPublicationCandidate(resized, keepAlive, reconciled, layoutNeeded, transformWrote, imageContentChanged);
                if (block == NoopPublicationBlock.None && !_noopPublications.Matches(in publicationKey, _uiCoverage))
                    block = NoopPublicationBlock.Key;
                if (block == NoopPublicationBlock.None && !VerifyNoopPublication()) block = NoopPublicationBlock.Parity;
                if (block == NoopPublicationBlock.None)
                {
                    skipSubmit = true;
                    _noopPublications.NoteElided();
                }
                else _noopPublications.NoteBlocked(block);
            }
            RememberDeviceLostFrame(clicks, keepAlive, resized, reconciled, layoutNeeded, transformWrote,
                maybeUnchanged, skipSubmit, in recordStats, frameStart, tFlush, tLayout, tAnim, tRecord);
            long subStart = (keepAlive && s_resizeDiag) ? Stopwatch.GetTimestamp() : 0;
            long tSubmitDone, tSubmit, hotAlloc;
            double subHashMs = 0, subCaptureMs = 0, subTailMs = 0;
            long tCap0 = 0;
            // The repaint region this frame actually PUBLISHED (§13.1). An elided frame publishes nothing, so it stays
            // empty — which is the truth: the presented pixels did not change.
            RepaintDamageRegion publishedRepaint = default;
            if (skipSubmit)
            {
                // Terminal `neverPresented`: this frame publishes nothing, so any latency sample tagged with it can
                // never join a present. Zeroing the tag makes that a LABELLED sample class in the trace rather than a
                // silent hole — a hole would make the pacing bucket look clean precisely when pacing is the fault.
                _framePublishSeq = 0;
                _framesSkippedSubmit++;
                // The case _lastNoPresentQpc exists for: Paint ran (reconcile/layout/record all happened) but the
                // recorded stream hashed identical to the last PRESENTED one, so submit + Present were elided — the
                // next real present must not be charged for this turn's slice of the gap.
                NoteNoPresentTurn();
                // E5: fence-only maintenance for the frame this branch just elided. Guarded on the async gate being
                // OFF — under Async/ForceSync the render thread owns every device touch (threading-render-seam.md:
                // "the render thread owns every ComPtr") and reclaims on its OWN skip/elided-record branches instead;
                // calling this from the UI thread while that thread is live would be a cross-thread device touch.
                // A render thread (a no-op publication skip under force-sync) owns the device: no UI-thread touch then.
                if (!_asyncActive && !recordOnRender) _device.ReclaimCompletedUploads();
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
                // RepaintDamage (§13.1) rides the same seam. The recorder filled it from SCENE changes; the host folds in
                // the invalidations only it can see — an untrustworthy target (first frame / resize / DPI / device
                // recovery) and a clear-color change under a byte-identical stream (theme switch) stay named-full
                // surrenders. The two classes whose PIXELS move with no dirty bit at all — an image content landing and
                // a live crossfade — are (design-engine-images.md E1) DESCRIBED as per-node rects instead of forcing
                // full: MarkImageDirty already turned a landing into a recorder band (only an id COUNT overflow, more
                // than 64 in one pump, still surrenders by name), and AddCrossfadeRepaint below turns every revealing id
                // into its own node's band. The region is CONSUMED: it is the submitted FrameInfo.RepaintDamage, and an
                // empty one is what lets a byte-identical frame elide its present.
                // recordOnRender: the recorder runs on the render thread, so `recordStats` here is the PREVIOUS frame's
                // imported feedback — its repaint region describes a frame that already presented and must NOT seed this
                // one (it would pin every frame "dirty" and defeat the render-side elision). Seed only what the HOST can
                // see; the render side unions in the region its own record produces.
                RepaintDamageRegion repaint = recordOnRender ? default : recordStats.RepaintDamage;
                if (!_repaintTargetValid || resized || Clear != _lastPublishedClear)
                    repaint.ForceFull(RepaintFullReason.TargetInvalidated);
                if (imageContentOverflow) repaint.ForceFull(RepaintFullReason.ImageContent);          // the named surrender survives, for >64 landings in one pump
                if (_images.HasActiveCrossfades && !AddCrossfadeRepaint(ref repaint)) repaint.ForceFull(RepaintFullReason.DetachedContent);
                _repaintTargetValid = true;
                _lastPublishedClear = Clear;
                // The STAT (not the submitted region). Under recordOnRender the frame's real repaint set is only known
                // on the render thread, so report the renderer-owned region from the latest imported feedback — the
                // same source as RenderRecordMs / RecordedSceneSequence, and consistent with them.
                publishedRepaint = recordOnRender ? recordStats.RepaintDamage : repaint;
                // The skip-submit baseline (updated after the submit below) — the same single hash, computed once.
                long tHash0 = Stopwatch.GetTimestamp();
                if (dlHash == 0UL) dlHash = InlineStreamHash();
                subHashMs = ElapsedMs(tHash0);
                var submitInfo = new FrameInfo(recordOnRender ? publishFrameSize : FrameSizePx(keepAlive), _window.Scale, Clear, _images.ClockMs, repaint);
                // F101: a settle frame (resized && keepAlive) carries its hint IN the publication (settlePresent below); it
                // used to be a UI-thread poke at the render-owned swapchain state, which a render turn presenting an
                // earlier publication could consume and a stand-down could drop.
                bool settlePresent = resized && keepAlive;
                // Keep the returned seq: it is this frame's identity across the seam, and the ONLY thing that lets a
                // present stamp be attributed back to the offsets this frame baked in (it was previously discarded).
                if (recordOnRender)
                {
                    tCap0 = Stopwatch.GetTimestamp();
                    _framePublishSeq = _renderSeam.PublishScene(_scene, _images, _strings, recordOptions,
                        CollectionsMarshal.AsSpan(_popupSkipRoots), CollectionsMarshal.AsSpan(_reuseBlockRoots),
                        CollectionsMarshal.AsSpan(_anim.PendingStructuralDamage), _connected.Detached, _popupWindows, _anim,
                        submitInfo, suppressVsync: keepAlive, settlePresent: settlePresent, video: _videoSurfaces);
                    subCaptureMs = ElapsedMs(tCap0);
                    tCap0 = Stopwatch.GetTimestamp();
                    _lastPublishedSceneSeq = _framePublishSeq;
                    _noopPublications.Remember(in publicationKey, _uiCoverage);   // what this publication carried (read before the capture)
                    _anim.PendingStructuralDamage.Clear();
                    // NotePublished FIRST (P8): every store write from here on belongs to the NEXT publication, and the
                    // capture ledger stamps by _publishSeq + 1 - including the ledger entries ClearTransformDirty and
                    // ClearRecordDirty below produce for the flag/dirty-bit columns they zero.
                    _scene.NotePublished(_framePublishSeq);
                    _scene.ClearTransformDirty();   // per-frame: a motion HINT for the compositor, not a delta ledger
                    // Record-dirty bits + the pending-removal ledger describe deltas since the last CONSUMED publication,
                    // not since the last published one: a publication the render thread never adopted delivered nothing,
                    // so its deltas must union into the next capture (that union is what keeps clean-span reuse valid
                    // across a publication gap — the gap itself is then just a counter). Each entry is stamped with the
                    // publication it belongs to, so only entries the renderer has adopted are dropped; the ones this
                    // publication just carried stay until it is consumed, and union into the next capture if it is not.
                    // Target invalidation needs no special case: the dropped publication is never consumed, so its
                    // entries stay and the next capture is a full record against the fresh target anyway.
                    // P8 adds a SECOND retention floor: the three publisher slots hold snapshots of three different
                    // publications, and an incremental refresh of the OLDEST one needs every delta since ITS baseline.
                    // So the ledgers are held to min(last CONSUMED publication, oldest SLOT baseline) - the older of
                    // "what the renderer has adopted" and "what the staler snapshots still have to catch up on".
                    ulong consumedSeq = _renderSeam.LastConsumedSeq;
                    ulong oldestSlotSeq = _renderSeam.OldestSlotCaptureSeq;
                    ulong retainSeq = Math.Min(consumedSeq, oldestSlotSeq);
                    _scene.ClearPendingRemovals(retainSeq);
                    _scene.ClearRecordDirty(retainSeq);
                    _scene.ClearCaptureLedger(oldestSlotSeq);
                }
                else _framePublishSeq = _renderSeam.Publish(_drawList.Bytes, _drawList.SortKeys, in submitInfo,
                    suppressVsync: keepAlive, settlePresent: settlePresent, video: _videoSurfaces);
                if (_renderThread is not null)
                {
                    if (_asyncActive) _renderThread.WakeAsync();   // async: UI does NOT wait (present happens later, render-side)
                    else _renderThread.DrainSync();                  // force-sync: block until the render thread presented
                    tSubmitDone = Stopwatch.GetTimestamp();          // async: present is off-thread; force-sync collapses the boundary
                    if (recordOnRender) subTailMs = ToMs(tSubmitDone - tCap0);
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
                        bool composited = false;
                        if (_renderSeam.TryAcquire(out var rf))
                        {
                            if (rf.SuppressVsync) { _swapchain.SuppressVsyncOnce(); _swapchain.SuppressLatencyWaitOnce(); }
                            if (rf.SettlePresent) _swapchain.HintSettlePresent();
                            // 10 submit (own swapchain — primary host: _swapchain IS _primarySwapchain): the composite turn over
                            // the UI-thread slice arenas (the published stream IS their flatten).
                            if (ChooseSubmitRoute(_isDetachedChild) == SubmitRoute.Composite && _scene.Recording.InlineSnapshot is { } inlineScene)
                            {
                                SubmitSlices(_uiSlices, _uiTiles, inlineScene, in rf.Submit, Tok.Epoch, rf.PublishSeq);
                                composited = true;
                            }
                            else _device.SubmitDrawList(_renderSeam.Bytes(rf), _renderSeam.SortKeys(rf), in rf.Submit, _swapchain);
                        }
                        tSubmitDone = Stopwatch.GetTimestamp();     // boundary: SubmitDrawList done, Present not yet called
                        _swapchain.Present();                       // 11 present (UI thread)
                        // rf is definitely-assigned on both TryAcquire outcomes; a false acquire leaves PublishSeq 0,
                        // which NotePresented treats as "no new content" and does not let move the ack.
                        NotePresented(rf.PublishSeq);
                        if (composited) CompleteFrameCapture(_uiSlices, rf.PublishSeq);   // an armed evidence capture (§A.6)
                    }
                    catch (Exception ex)
                    {
                        if (!TryRecoverForegroundDeviceLost(ex, clicks)) throw;
                        // Submit or Present threw and was recovered as a device loss: NotePresented above was never
                        // reached, so this turn presented nothing (see _lastNoPresentQpc's doc).
                        NoteNoPresentTurn();
                        return LastStats;
                    }
                }
                // §5.2 Fix A: every submitted stream becomes the elision baseline — active frames hash too. (Already
                // computed above; this is the same value, not a second walk.)
                _lastPresentedDrawListHash = dlHash;
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
                    if (_swapchain.LastPresentStoodDown) _framesStoodDown++;
                }
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
            if (_renderThread is null && _parentRenderThread is null)
            {
                if (_device.GetVideoPresenter(_swapchain) is { } vp) _videoSurfaces.Drain(vp, _window.Scale);
                // F101: the settle sync runs only now, AFTER the placement commit above — a flush between the present and that
                // commit composed the new hole one frame ahead of the new video geometry. The UI thread is the presenting
                // thread here, so blocking one vblank starves no other target.
                _swapchain.CompleteSettlePresent(blockUntilComposed: true);
            }
            // Advisory, one way engine → PAL: a composited window defers ALL painting during an OS modal
            // edge-resize, which would leave this video child at its pre-resize geometry while the frame moves
            // under it. Telling the window it carries live video lets it keep a throttled keep-alive instead.
            _window.SetHasLiveVideo(_videoSurfaces.HasLiveSurface);

            DrainPassiveEffects();                             // 12 passive effects
            _strings.Tick();                                   // 12.5 reclaim released text ids (behind the reader quarantine)
            if (s_allocDiag) { db = Probe(SegEffects, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            UpdateFrameTiming(frameStart);
            int componentsRendered = _reconciler.ConsumeRenderCount();
            // Always-on: a frame-over-budget log needs "did a GC land inside this frame" without a flag (three
            // CollectionCount reads — no allocation, sub-microsecond).
            int gc0 = 0, gc1 = 0, gc2 = 0;
            {
                int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
                if (_gcSnapInitialized) { gc0 = c0 - _prevGc0; gc1 = c1 - _prevGc1; gc2 = c2 - _prevGc2; }
                _prevGc0 = c0; _prevGc1 = c1; _prevGc2 = c2;
                _gcSnapInitialized = true;
            }
            if (keepAlive && s_resizeDiag)
                ReportResizeTick(frameStart, ensureMs, layoutMs0, subStart, resized, layoutPath,
                    componentsRendered, recordStats.NodesVisited, _uiSlices.TotalCommandCount, hotAlloc);
            // Gap discriminator, always-on (one branch per frame): while scroll is active, a raw inter-frame gap over
            // 12 ms is split into the work this frame measured and the SLACK no phase accounts for. GC deltas landing
            // in the gap vs the wait the loop last asked for tell "GC pause" / "wake model slept" / "pre-empted" apart —
            // a 124 ms hole mid-drag with 2.8 ms of work and no GC otherwise goes unattributed.
            float slackMs = 0f;
            SlackCause slackCause = SlackCause.None;
            float rawDtMs = _frameTime is StopwatchFrameTimeSource rawSrc ? rawSrc.LastRawDeltaMs : dtMs;
            if (scrollActive && rawDtMs > 12f)
            {
                float workMs = (float)(ToMs(tFlush - frameStart) + ToMs(tLayout - tFlush) + ToMs(tAnim - tLayout) + ToMs(tRecord - tAnim) + ToMs(tSubmit - tRecord));
                slackMs = MathF.Max(0f, rawDtMs - workMs);
                if (slackMs >= 4f)
                    slackCause = (gc0 | gc1 | gc2) != 0 ? SlackCause.Gc
                        : _lastWaitMs > 0 && _lastWaitMs >= slackMs * 0.5f ? SlackCause.WakeSlept
                        : SlackCause.Preempted;
            }
            // The main window's present truth, read ONCE (render-written). DWM's 1 Hz deltas reach FrameStats and the
            // probe only on the first frame that observes their sample (DwmSampleSeq changed); every later frame carries 0,
            // so a reader summing per frame counts each DWM sample once.
            var presentStats = _swapchain.LastPresentStats;
            bool dwmFresh = presentStats.Valid && presentStats.DwmSampleSeq != 0 && presentStats.DwmSampleSeq != _dwmSampleSeqSeen;
            if (dwmFresh) _dwmSampleSeqSeen = presentStats.DwmSampleSeq;
            LastStats = new FrameStats(recordOnRender ? _lastRecordedCommandCount : _isDetachedChild ? _drawList.CommandCount : _uiSlices.TotalCommandCount, clicks, hotAlloc, reconciled || layoutNeeded)
            {
                SlackMs = slackMs,
                SlackCause = slackCause,
                UiGap = slackCause != SlackCause.None ? _lastGap : default,
                NodesVisited = recordStats.NodesVisited,
                NodesCulled = recordStats.NodesCulled,
                DrawNodeCount = recordStats.DrawnNodeCount,
                CulledNodeCount = recordStats.CulledNodeCount,
                BlurCandidateCount = recordStats.BlurCandidateCount,
                BlurGroupCount = recordStats.BlurGroupCount,
                EdgeFadeGroupCount = recordStats.EdgeFadeGroupCount,
                SpansReused = recordStats.SpansReused,
                SpansReRecorded = recordStats.SpansReRecorded,
                Slices = recordStats.Slices,
                CompositeOnlyTurn = recordStats.Slices.KeptAll,
                SpanMisses = recordStats.SpanReuseMisses,
                SpanBytesCopied = recordStats.SpanBytesCopied,
                DepthAborts = recordStats.DepthAborts,
                SpanReuseDisabledReasons = recordStats.SpanReuseDisabledReasons,
                SpanReuseMisses = recordStats.SpanReuseMisses,
                RepaintCoverage = publishedRepaint.Coverage(layoutSize.Width, layoutSize.Height),
                RepaintRectCount = publishedRepaint.Count,
                RepaintFullReason = publishedRepaint.FullReason,
                MeasureCount = _layout.DiagMeasure,
                ArrangeCount = _layout.DiagArrange,
                TextShapeMisses = _layout.DiagTextMiss,
                TextShapes = (int)(_fonts.ShapeCount - shapeCountAtFrameStart),
                CapturedNodes = recordOnRender ? _renderSeam.LastCapturedNodeCount : 0,
                SubmitHashMs = subHashMs,
                SubmitCaptureMs = subCaptureMs,
                SubmitTailMs = subTailMs,
                CaptureIncremental = recordOnRender && _renderSeam.LastCaptureWasIncremental,
                CaptureSceneMs = recordOnRender ? _renderSeam.LastCaptureSplit.Scene : 0,
                CaptureConfigMs = recordOnRender ? _renderSeam.LastCaptureSplit.Config : 0,
                CaptureImagesMs = recordOnRender ? _renderSeam.LastCaptureSplit.Images : 0,
                CaptureAnimMs = recordOnRender ? _renderSeam.LastCaptureSplit.Anim : 0,
                BindingFires = _reconciler.NodeBindingFireCount,
                BindingWrites = _reconciler.NodeBindingWriteCount,
                RebindFlushAllocBytes = _rebindFlushAllocBytesThisFrame,
                ScopedRelayoutMarks = _invalidator.DirtyMarksThisFrame,
                RootRelayoutEscapes = _invalidator.EscapesThisFrame,
                Fps = _fps,
                PresentFps = _presentFps,
                PresentedSequence = this.PresentedSequence,
                FrameMs = _frameMs,
                ComponentsRendered = componentsRendered,
                FlushMs = ToMs(tFlush - frameStart),   // incl. flip/FLIP-capture + reactive flush + reconcile
                ReactiveFlushMs = reactiveFlushMs,
                ReactiveUnits = _rxUnitsThisFrame,
                ReactiveLongestUnitMs = ToMs(_rxLongestTicksThisFrame),
                VirtualRealizeMs = virtualRealizeMs,
                LayoutMs = ToMs(tLayout - tFlush),
                LayoutSolveMs = ToMs(tSolve - tFlush),                 // of which: FlexLayout (full/scoped + D1 realize catch-up)
                LayoutEffectsMs = ToMs(tLayoutEffects - tSolve),       // of which: DrainLayoutEffects
                ConnectedTickMs = ToMs(tConnected - tLayoutEffects),   // of which: ConnectedAnimation.Tick65
                ReflowSeedMs = ToMs(tLayout - tConnected),             // of which: enter/exit reflow seeding (+ scene dump)
                LocalRelayoutResolves = _invalidator.LocalResolvesThisFrame,
                AnimMs = ToMs(tAnim - tLayout),         // phase-7 ticks + projections
                RecordMs = ToMs(tRecord - tAnim),       // image pump + SceneRecorder (+ text shaping) + dyntext
                RenderRecordMs = recordOnRender ? _lastRenderRecordMs : 0,
                QuiesceWaitMsMax = OwningRenderThread?.QuiesceWaitMsMax ?? 0,
                RecordedSceneSequence = recordOnRender ? _lastRecordedFeedbackScene : 0,
                ImagePumpMs = ToMs(tImagePump - tAnim),            // of which: phase-7.5 decode apply/evict
                ImageApplyCount = _images.LastPumpAppliedCount,
                ImageApplyBytes = _images.LastPumpAppliedBytes,
                DeferredImageUploads = _device.DeferredImageUploads,           // P0: budget-truncated upload drain turns (cumulative)
                DeferredImageUploadBytes = _device.DeferredImageUploadBytes,
                RealizeCatchupMs = ToMs(tRealizeCatchup - tImagePump), // of which: phase-7.6 re-realize + scoped relayout
                SubmitMs = ToMs(tSubmit - tRecord),     // command build + GPU submit + present (total; ~0 on a skipped frame)
                FenceWaitMs = skipSubmit ? 0.0 : _swapchain.LastFenceWaitMs,
                RenderFreshPresents = _renderThread?.FreshPresents ?? 0,
                RenderMotionPresents = _renderThread?.MotionPresents ?? 0,
                RenderRaceHits = _renderThread?.RaceHits ?? 0,
                RenderCatchUpSkips = _renderThread?.CatchUpSkips ?? 0,
                RenderFreshLongWaits = _renderThread?.FreshLongWaits ?? 0,
                RenderMotionLongWaits = _renderThread?.MotionLongWaits ?? 0,
                RenderMissedMotionTicks = _renderThread?.MissedMotionTicks ?? 0,
                RenderSkippedTicks = _renderThread?.SkippedTicks ?? 0,
                PresentsDisplayed = _swapchain.PresentsDisplayed,
                PresentsDropped = _swapchain.PresentsDropped,
                VblanksRepeated = _swapchain.VblanksRepeated,
                LatencyWaitMs = presentStats.Valid ? presentStats.LatencyWaitMs : 0.0,
                DwmDropped = dwmFresh ? presentStats.DwmFramesDroppedDelta : 0u,
                DwmMissed = dwmFresh ? presentStats.DwmFramesMissedDelta : 0u,
                DwmLate = dwmFresh ? presentStats.DwmFramesLateDelta : 0u,
                DwmSampleSeq = presentStats.Valid ? presentStats.DwmSampleSeq : 0u,
                PresentMs = ToMs(tSubmit - tSubmitDone),// of which: the Present() call (0 on a skipped frame)
                GpuRenderMs = LastGpuRenderMs,
                RenderCensus = new RenderFrameCensus
                {
                    RepaintCoverage = publishedRepaint.Coverage(layoutSize.Width, layoutSize.Height),
                    RepaintRects = publishedRepaint.Count,
                    RepaintFullReason = publishedRepaint.FullReason,
                    EdgeFadeGroups = recordStats.EdgeFadeGroupCount,
                    SpansReused = recordStats.SpansReused,
                    SliceBytesRecorded = recordStats.Slices.BytesRecorded,
                    SpansReRecorded = recordStats.SpansReRecorded,
                    SpanMisses = recordStats.SpanReuseMisses,
                    CaptureIncremental = recordOnRender && _renderSeam.LastCaptureWasIncremental,
                    CaptureFullReason = recordOnRender ? _renderSeam.LastCaptureFullReason : CaptureFullReason.None,
                    CapturedNodes = recordOnRender ? _renderSeam.LastCapturedNodeCount : 0,
                    RecordMs = (float)(recordOnRender ? _lastRenderRecordMs : ToMs(tRecord - tAnim)),
                    CaptureMs = (float)subCaptureMs,
                    Device = _swapchain.TryGetFrameCounters(out GpuFrameCounters deviceCounters) ? deviceCounters : default,
                    Tiles = LastTileCensus,
                },
                Presented = !skipSubmit,
                ScrollActive = scrollActive,
                PublicationGaps = Interlocked.Read(ref _publicationGaps),
                PacedUrgentBreaks = _pacingSource is null ? 0 : _pacingSource.PacedUrgentBreaks,   // P0: off-phase frames from urgent input
                ProductionDeclines = _productionDeclines,                                          // P0: input updates that missed their tick
                PresentedFrames = Interlocked.Read(ref _presentedFramesTotal),
                MissedVsyncs = Interlocked.Read(ref _missedVsyncsTotal),
                PublishSeq = _framePublishSeq,
                Gc0Delta = gc0,
                Gc1Delta = gc1,
                Gc2Delta = gc2,
                Census = censusLine,
                RefreshIntervalMs = refreshIntervalMs,
            };
            GapPaintEnd(frameStart);                           // the next UI gap starts here (AppHost.UiGap.cs)
            if (FrameLedger.Enabled) _ledgerPaint = new LedgerPaintStamps(frameStart, tFlush, tLayout, tAnim, tRecord, tSubmit);
            // scroll-lab E3: the main window's present truth (the same values FrameStats just took) into the probe's UI ring.
            if (!_isDetachedChild && FluentGpu.Scroll.Diag.ScrollProbe.Level != FluentGpu.Scroll.Diag.ProbeLevel.Off)
                FluentGpu.Scroll.Diag.ScrollProbe.Present(Stopwatch.GetTimestamp(), LastStats.PresentsDisplayed, LastStats.PresentsDropped,
                    LastStats.VblanksRepeated, LastStats.DwmDropped, LastStats.DwmMissed, LastStats.DwmLate, LastStats.DwmSampleSeq,
                    LastStats.LatencyWaitMs, refreshIntervalMs, _frameMs, !skipSubmit, presentStats.Valid);
            FrameCompleted?.Invoke(LastStats);
            PublishFrameStats(LastStats);
            // (the frame-clock publish moved to phase 3, just before the flush — see the drain block there)
            if (s_allocDiag) Probe(SegPublish, db, dt0);   // alloc-05: frame-stat box + frameclock long-box were untracked
            return LastStats;
        }
        finally
        {
            _frameNeeded = _runtime.HasPending;
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

    /// <summary>The turn's reactive flush (phase 3): runs to QUIESCENCE — every scheduled re-render, effect and binding,
    /// and everything they schedule. A committed write is applied whole in the frame that flushes it: a wall-clock slice
    /// that yielded under load presented the write half-applied across frames and, with its deadline already spent, ran
    /// nothing at all. A slow unit is not sliced around; it is measured (<see cref="NoteReactiveFlush"/>) and reported so
    /// it gets fixed where it is. Only a unit that threw (its siblings stay queued) or a broken runaway cycle can leave
    /// work pending — that asks for the next frame.</summary>
    private void FlushToQuiescence()
    {
        var r = _runtime.Flush();
        NoteReactiveFlush(in r);
        if (r.HasPending) WakeFrame();
    }

    /// <summary>The post-realize rebind flush: runs to quiescence like every flush, under the bound-transition
    /// suppression scope. A realize pass writes the slot signals that bind the rows it just created, and those rebinds land
    /// THIS frame — a realized row rendered against a stale binding is a visible defect.</summary>
    private void FlushRebindsToQuiescence()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        // P3 (virtualization.md §5.5): this flush is where a recycled slot's rebound channels fire — a bound-channel
        // transition seed or the P1 Visible false→true Enter seed must SNAP here, not animate, or a recycle replays a
        // cross-fade/pop for what the app sees as the same persistent row. RealizeBoundWindow's own slot-creation
        // mounts push the same suppression (see its call sites) so a genuine cold mount inside THIS flush still snaps
        // too — correct, since a cold-realized row has no prior displayed state to transition from anyway.
        ReactiveFlushResult r;
        using (_reconciler.PushSuppressBoundTransitions())
            r = _runtime.Flush();
        _rebindFlushAllocBytesThisFrame += GC.GetAllocatedBytesForCurrentThread() - before;
        NoteReactiveFlush(in r);
        if (r.HasPending) WakeFrame();   // only a throwing unit can leave work here
    }

    // FrameStats.Reactive* accumulators (every flush of the current Paint) and the slow-unit reporter.
    private int _rxUnitsThisFrame;
    private long _rxLongestTicksThisFrame;
    private Computation? _rxLongestUnitThisFrame;
    private readonly SlowReactiveUnits _slowUnits = new();

    /// <summary>Fold one flush into this frame's reactive census, and report a unit longer than the frame period ALWAYS-ON
    /// (<see cref="SlowReactiveUnits"/> rate-limits the line to one a second and counts the rest): the finding to fix at
    /// its source — a render doing too much, not a reason to slice the flush.</summary>
    private void NoteReactiveFlush(in ReactiveFlushResult r)
    {
        _rxUnitsThisFrame += r.UnitsRun;
        if (r.LongestUnitTicks > _rxLongestTicksThisFrame) { _rxLongestTicksThisFrame = r.LongestUnitTicks; _rxLongestUnitThisFrame = r.LongestUnit; }
        long period = RefreshPeriodQpcOrDefault();
        if (_slowUnits.Note(r.LongestUnitTicks, period, Stopwatch.GetTimestamp(), Stopwatch.Frequency, out int more))
        {
            object? owner = r.LongestUnit?.DiagOwner;
            string who = owner is null ? (r.LongestUnit?.GetType().Name ?? "?") : owner.GetType().Name;
            Diag.Line(string.Create(CultureInfo.InvariantCulture,
                $"[signals.slow-unit] one reactive unit ran {r.LongestUnitTicks * 1000.0 / Stopwatch.Frequency:0.00} ms (owner={who}; frame period {period * 1000.0 / Stopwatch.Frequency:0.00} ms){(more > 0 ? $"; {more} more since the last report" : "")}"));
        }
    }

    /// <summary>P0 always-on counter accumulator: GC-delta bytes across this frame's <see cref="FlushRebindsToQuiescence"/>
    /// call(s), reset at the top of each Paint and read into <c>FrameStats.RebindFlushAllocBytes</c>.</summary>
    private long _rebindFlushAllocBytesThisFrame;

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
        // NO render-thread park (F207): the HWND is the UI thread's own, and the swapchain is built by the render thread (below).
        long leaseStart = Stopwatch.GetTimestamp();
        var palWindow = _app.CreatePopupWindow(new PopupWindowDesc(_window.Handle, default, material, Tok.Theme == ThemeKind.Dark));
        if (palWindow is null) return -1;
        long windowDone = Stopwatch.GetTimestamp();
        bool acrylic = material == PopupWindowMaterial.TransientAcrylic;
        // Flat tint over the host-backdrop (blurred desktop): the dark MenuFlyout fallback color at ~0.5 so the desktop
        // reads through as a frosted grey (WinUI DesktopAcrylicBackdrop look). Tunable.
        ColorF tint = acrylic ? Tok.AcrylicFlyout.Fallback with { A = 0.5f } : default;
        // Round the composition acrylic to the flyout corner radius (WinUI OverlayCornerRadius = 8 DIP) so it matches
        // the engine-drawn rounded plate/border in the swapchain content.
        float cornerPx = acrylic ? 8f * (_window.Scale <= 0f ? 1f : _window.Scale) : 0f;
        var slot = new PopupWindowSlot(++_popupTokenSeq, palWindow, subtreeRoot, material);
        var desc = new SwapchainDesc(palWindow.Handle, new Size2(1, 1),
            Composited: true, DesktopAcrylic: acrylic, AcrylicTint: tint, CornerRadiusPx: cornerPx);
        // Under a render thread the CREATE rides the mailbox: that thread is the sole ComPtr owner, so the swapchain is built
        // where the INCIDENT 2026-09 race (a create under a recording in flight) cannot occur, and the UI never waits for a
        // render turn to open a flyout. Until it lands slot.Swapchain is null: the recorder skips the slot, the wake reasons
        // keep frames coming, and the reveal gate waits for HasPresentedContent (so the window stays hidden meanwhile).
        // The thread-affine part of the create (the D3D12 backend's composition compositor for an acrylic popup) stays here.
        if (OwningRenderThread is null) slot.Swapchain = _device.CreateSwapchain(in desc);
        else
        {
            _device.PrepareSwapchainCreate(in desc);
            PostPopupRenderAction(new PopupRenderAction(PopupRenderOp.Create, slot, default, default, desc));
        }
        long leaseDone = Stopwatch.GetTimestamp();
        var life = slot.Lifecycle;
        life.LeaseStartQpc = leaseStart;
        life.ParkMs = 0;                                        // no park: the render thread builds the swapchain in mailbox order
        life.WindowMs = ToMs(windowDone - leaseStart);
        life.SwapchainMs = ToMs(leaseDone - windowDone);        // the UI-side half (inline create, or the thread-affine prepare)
        life.LeaseMs = ToMs(leaseDone - leaseStart);
        _popupWindows.Add(slot);
        Volatile.Write(ref _popupWindowCount, _popupWindows.Count);   // render-thread mirror: never elide a popup turn
        WakeFrame();
        return slot.Token;
    }

    /// <summary>Place a leased popup window: bounds arrive in main-window DIP (the overlay's placement space); the
    /// host converts to physical virtual-screen px (client origin + scale), resizes the popup swapchain, and seeds its
    /// chrome while the window remains hidden. The first successful popup present reveals it without activation.</summary>
    private void SetPopupWindowBounds(int token, RectF dipBounds, bool opensUp, float closedRatio)
    {
        // NO render-thread park here. This fires on every pointer move over an open flyout (and every frame of its open
        // reveal), so parking the loop meant a UI stall of up to one submit+present PER INTERACTION. The only ComPtr
        // touches — Resize + ConfigurePopupChrome — are posted to the render thread and applied at the top of its next
        // turn, before the same turn's RecordPopups reads the swapchain. Everything below is UI-owned state (slot
        // fields, the PAL window rect). The stale-publication drop the park needed is gone with it: the resize is now
        // ORDERED behind this frame's publication instead of racing it.
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
            // Content rect = the menu plate inset by the shadow margins (window px): the acrylic rounds to it + the shadow
            // is masked to it; the engine draws the plate/border/items there too (recorded at the inset origin).
            var contentPx = new RectF(insL * s, insT * s, dipBounds.W * s, dipBounds.H * s);
            var chrome = new PopupChromeMetrics(contentPx, opensUp, MathF.Max(0f, closedRatio), 8f * s, 1f * s);
            if (OwningRenderThread is null)
            {
                if (slot.Swapchain is { } sc) { sc.Resize(new Size2(wpx, hpx)); sc.ConfigurePopupChrome(chrome); }
            }
            else if (!slot.CreateFailed)
                PostPopupRenderAction(new PopupRenderAction(PopupRenderOp.ResizeAndChrome, slot, new Size2(wpx, hpx), chrome));
            WakeFrame();
            return;
        }
    }

    /// <summary>Begin the desktop-acrylic CLOSE fade on a popup window's composition chrome (acrylic + shadow). The engine
    /// fades the content swapchain over the same 83ms; the window itself is disposed at finalize (<see cref="ClosePopupWindow"/>),
    /// by which time the fade has settled — so the acrylic fades out instead of vanishing.</summary>
    private void AnimatePopupCloseWindow(int token)
    {
        // Same reasoning as SetPopupWindowBounds: one ComPtr call, posted rather than parked. The window itself is
        // disposed later at ClosePopupWindow, by which time the posted fade has been applied on the render thread.
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            if (_popupWindows[i].Token != token) continue;
            var slot = _popupWindows[i];
            if (OwningRenderThread is null) slot.Swapchain?.AnimatePopupClose();
            else if (!slot.CreateFailed) PostPopupRenderAction(new PopupRenderAction(PopupRenderOp.AnimateClose, slot, default, default));
            WakeFrame();
            return;
        }
    }

    private void ClosePopupWindow(int token)
    {
        bool inline = OwningRenderThread is null;
        if (inline) _renderSeam.InvalidateTarget();
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            var slot = _popupWindows[i];
            if (slot.Token != token) continue;
            slot.Window.Hide();
            slot.Retired = true;
            _popupWindows.RemoveAt(i);
            Volatile.Write(ref _popupWindowCount, _popupWindows.Count);
            if (inline)
            {
                // No render thread: every pass ran on this thread, so the per-pass fields are settled. The one line this popup leaves behind.
                Diag.Line(slot.Lifecycle.Line(slot.Material, Stopwatch.GetTimestamp()));
                slot.Swapchain?.Dispose();
                slot.Swapchain = null;
                slot.DisposeWindow();
            }
            else
            {
                // NO park (F207): the render thread releases the swapchain in mailbox order - after every Resize / fade already
                // queued for this slot - and posts the HWND back for the UI thread to destroy. A frame published before this
                // call may still name the slot; the recorder resolves slot.Swapchain when it records, finds null and skips it.
                _retiringPopups.Add(slot);
                PostPopupRenderAction(new PopupRenderAction(PopupRenderOp.Dispose, slot, default, default));
            }
            WakeFrame();
            return;
        }
    }

    /// <summary>Reveal a popup window (UI thread) and time the OS call — the reveal half of its <c>[overlay.popup]</c> line.</summary>
    private static void ShowPopupWindow(PopupWindowSlot slot)
    {
        long t0 = Stopwatch.GetTimestamp();
        slot.Window.Show();
        long t1 = Stopwatch.GetTimestamp();
        slot.Lifecycle.ShownQpc = t1;
        slot.Lifecycle.ShowMs = ToMs(t1 - t0);
    }

    /// <summary>Phase 8b: re-record each popup window's subtree into its own DrawList (recorder root-override,
    /// re-origined to the popup's placed top-left) and present its swapchain.</summary>
    private void RecordPopupWindows(in FocusVisualStyle focus, in TextEditStyle textEdit, bool prepareOnly = false)
    {
        for (int i = 0; i < _popupWindows.Count; i++)
        {
            var slot = _popupWindows[i];
            if (slot.CreateFailed)
            {
                // The render thread could not build this popup's swapchain (it logged why). Same fallback as a popup whose
                // render failed: stop leasing windowed popups so menus fall back to in-window, and drop this slot.
                PopupWindowsEnabled = false;
                slot.Window.Hide();
                slot.Retired = true;
                slot.DisposeWindow();
                _popupWindows.RemoveAt(i);
                Volatile.Write(ref _popupWindowCount, _popupWindows.Count);
                i--;
                continue;
            }
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
            if (prepareOnly) continue;
            // An UNPLACED popup (the overlay has not given it a rect yet) has nothing to paint, and presenting it
            // anyway would latch its reveal evidence on the 1×1 creation-size surface — a revealed popup window with
            // nothing in it. It is placed on the very next layout effect.
            if (origin.IsEmpty) continue;
            long passStart = Stopwatch.GetTimestamp();
            SceneRecorder.RecordSubtree(_scene, slot.DrawList, _images, in focus, Tok.ScrollThumb, Tok.AcrylicFlyout.Fallback, in textEdit,
                slot.Root, new Point2(origin.X, origin.Y));
            if (slot.Swapchain is { } sc)
            {
                try
                {
                    _device.SubmitDrawList(slot.DrawList.Bytes, slot.DrawList.SortKeys,
                        new FrameInfo(sc.SizePx, _window.Scale, ColorF.Transparent), sc);
                    sc.Present();
                    slot.Lifecycle.NoteTurn(passStart, Stopwatch.GetTimestamp(), sc.HasPresentedContent);
                    // Atomic creation: the popup HWND stays hidden until its swapchain contains the seeded first frame.
                    // This prevents an uninitialized/full-opacity plate from flashing before the engine/compositor
                    // entrance state exists. The gate is the swapchain's OWN report that it presented content — NOT
                    // the fact that Present() was called and did not throw: a backend stands down for a covered /
                    // hidden / cloaked target and presents NOTHING, which (since the popup HWND is hidden precisely
                    // until this frame) is exactly the case here. Revealing on the call rather than on the paint is
                    // what showed a popup as its frosted chrome with no menu inside it.
                    if (!slot.Window.IsShown && sc.HasPresentedContent)
                    {
                        ShowPopupWindow(slot);
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

    // --fg motion per-node line (one word of OUTCOME + the captured/live rects). Static → zero capture, and only ever
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

        // Reflow-shove suppression: a node whose commit-time position delta is CAUSED by an active reflow (its ancestor
        // hop crosses a reflowing sibling, so the reflow's growing/shrinking main-axis extent is what moved it) must NOT
        // position-FLIP. The reflow's per-tick layout re-solve IS the animation — a FLIP translate on top of it is stale
        // double-compensation, and because every commit during the reflow re-seeds the projection (ReframePosition
        // restarts), the translate stays perpetually a drawer-height behind and the shoved sibling paints over the rows
        // underneath it. Map every live reflow node's ancestor path to "the child index that carries the reflow" so the
        // per-node loop below can tell a shove apart from an independent move.
        // Cleared FIRST (not just at the tail) so an early-out on any path can never leave a stale frame map behind.
        _reflowShoveFrames.Clear();
        _anim.CollectLiveReflowNodes(_liveReflowScratch);   // clears the list itself
        for (int i = 0; i < _liveReflowScratch.Count; i++)
        {
            NodeHandle carrier = _liveReflowScratch[i];
            if (!_scene.IsLive(carrier)) continue;
            for (NodeHandle c = carrier, p = _scene.Parent(c); !p.IsNull && _scene.IsLive(p); c = p, p = _scene.Parent(p))
                _reflowShoveFrames.TryAdd((int)p.Raw.Index, (int)c.Raw.Index);   // first writer wins (nested reflows: the innermost path claims the shared hops)
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
            // Reflow-shoved (see the frame map above): drop any in-flight position row, land the node on the geometry the
            // reflow's own re-solve just wrote, and zero the position delta so nothing re-seeds a translate. Size/opacity
            // still animate — POSITION is the one channel the reflow owns. Accepted edge: a sibling that genuinely made an
            // independent move on the SAME commit gets snapped for that one commit; a rare coincidence, bounded to one
            // frame, and far preferable to the overlap artifact.
            if (_liveReflowScratch.Count > 0 && IsReflowShoved(n))
            {
                _anim.SnapPositionToLayout(n);
                from = @from with { X = to.X, Y = to.Y };
                if (MathF.Abs(from.W - to.W) < SizeEps && MathF.Abs(from.H - to.H) < SizeEps)
                {
                    if (s_motionDiag) LogMotionNode(n.Raw.Index, "reflow-shove-snap", from, to);
                    continue;
                }
            }
            if (!_anim.TryGetTransition(n, out var spec)) { if (s_motionDiag) LogMotionNode(n.Raw.Index, "no-transition", from, to); continue; }
            if (reduced) spec = spec with { Dynamics = TransitionDynamics.Tween(1f, Easing.Linear) };
            if (s_motionDiag) LogMotionNode(n.Raw.Index, "animate", from, to);
            // AnimateBounds consumes only deltas, so parent-relative rects feed it directly; for a purely local
            // move this is bit-identical to the old absolute pair (the ancestor sum cancels).
            _anim.AnimateBounds(n, from, to, spec);
        }
        _projectionSuppressionRoots.Clear();
        _liveReflowScratch.Clear();
        _reflowShoveFrames.Clear();
        _projectBefore.Clear();
    }

    /// <summary>True when <paramref name="n"/>'s commit-time move was CAUSED by an active reflow: some hop of its
    /// ancestor chain has a reflow-carrying child that is NOT the child we came up through — i.e. the reflowing subtree
    /// is a SIBLING that shoved us along the main axis. When the carrier IS our own hop we are INSIDE the reflow subtree
    /// and pass straight through: the reflow's own target/echo guards already own that case.</summary>
    private bool IsReflowShoved(NodeHandle n)
    {
        if (_reflowShoveFrames.Count == 0) return false;
        for (NodeHandle a = n, p = _scene.Parent(a); !p.IsNull && _scene.IsLive(p); a = p, p = _scene.Parent(p))
            if (_reflowShoveFrames.TryGetValue((int)p.Raw.Index, out int carrier) && carrier != (int)a.Raw.Index)
                return true;
        return false;
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
    /// edge rides the animated edge). The re-solve also re-exposes each enter-reflow node's NATURAL child extent, so a row
    /// aimed at "the solved auto size" retargets when async content lands mid-animation instead of snapping at settle.
    /// Runs only on frames where a reflow track wrote — zero work otherwise.</summary>
    private void RunReflowLayout(Size2 layoutSize)
    {
        var roots = _anim.ReflowRoots;
        if (!_anim.ConsumeReflowWrites()) { roots.Clear(); return; }
        if (_scene.AnyLayoutDirty)
        {
            _invalidator.RunDirty(layoutSize);
            _scene.ClearLayoutDirty();
        }
        // Natural-extent retarget: a reflow row PINS LayoutInput.Width/Height to its interp every tick, so layout can
        // never expose a changed natural size — but the node's CHILDREN are still arranged at their natural size inside
        // that pinned box, so the re-solve just above hands us the real main-axis extent for free. Async content that
        // lands mid-animation (an image, a fetched list) therefore retargets the row instead of snapping the moment it
        // settles onto a now-stale target.
        // The gates are deliberately narrow:
        //  • NaturalTarget only — enter-reflow rows whose destination WAS "the solved auto size"; a row aimed at an
        //    explicit number is aimed there on purpose.
        //  • RestoreTo NaN only — a declared-height node clips below its natural content INTENTIONALLY, and growing the
        //    target to the natural extent would fight the author's declaration (and SettleRestore's writeback).
        //  • The 0.5 guard is self-limiting for Grow-filled content: there the child extent IS the interp, so |to - extent|
        //    stays at the current gap and only a genuine content change clears the threshold. RetargetReflow adds its own
        //    target guard on top, so a no-op retarget can never restart the tween.
        for (int i = 0; i < roots.Count; i++)
        {
            var r = roots[i];
            if (!_scene.IsLive(r)) continue;
            if (_anim.TryGetLiveReflow(r, AnimChannel.LayoutH, out float toH, out bool natH, out float restH)
                && natH && float.IsNaN(restH))
            {
                float extentH = 0f;
                for (var c = _scene.FirstChild(r); !c.IsNull; c = _scene.NextSibling(c))
                {
                    if (_scene.IsOrphan(c)) continue;
                    ref readonly RectF cb = ref _scene.Bounds(c);   // parent-relative — the same space the Trailing walk below reads
                    extentH = MathF.Max(extentH, cb.Y + cb.H);
                }
                if (extentH > 0f) extentH += _scene.Layout(r).Padding.Bottom;
                if (extentH > 0.5f && MathF.Abs(toH - extentH) >= 0.5f)
                    _anim.RetargetReflow(r, AnimChannel.LayoutH, extentH);
            }
            // Mirror for the horizontal axis. A node has at most one MAIN-axis reflow row in practice, so the second
            // TryGetLiveReflow is a miss on every real frame — cheap enough not to need a direction lookup.
            if (_anim.TryGetLiveReflow(r, AnimChannel.LayoutW, out float toW, out bool natW, out float restW)
                && natW && float.IsNaN(restW))
            {
                float extentW = 0f;
                for (var c = _scene.FirstChild(r); !c.IsNull; c = _scene.NextSibling(c))
                {
                    if (_scene.IsOrphan(c)) continue;
                    ref readonly RectF cb = ref _scene.Bounds(c);
                    extentW = MathF.Max(extentW, cb.X + cb.W);
                }
                if (extentW > 0f) extentW += _scene.Layout(r).Padding.Right;
                if (extentW > 0.5f && MathF.Abs(toW - extentW) >= 0.5f)
                    _anim.RetargetReflow(r, AnimChannel.LayoutW, extentW);
            }
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
    /// settle in &lt;1s, so the backstop never fires in a well-behaved run.
    /// This global wall-clock value stays the outer guard (it is what stops a never-painting app from pinning the wake
    /// loop). An orphan enqueued with its own <see cref="SceneStore.OrphanMaxAgeMs"/> (the reconciler passes the exit
    /// spec's duration + delay + slack) ALSO gets that tighter deadline, measured on
    /// <see cref="SceneStore.AnimClockMs"/> — the same clamped timebase the exit tracks integrate on, so a healthy exit
    /// can never trip it however badly the wall clock hitches, while a WEDGED page-sized exit (a skeleton shimmer
    /// cross-dissolving under live content) is dropped in ~one exit duration instead of painting half-faded for 2s.</summary>
    private const long OrphanSettleTimeoutMs = 2000;

    private bool OrphanDeadlinePassed(int index, long nowTicks)
    {
        double ageMs = (nowTicks - _scene.OrphanEnqueuedTicks(index)) * 1000.0 / Stopwatch.Frequency;
        float ownMaxAge = _scene.OrphanMaxAgeMs(index);
        return ageMs >= OrphanSettleTimeoutMs
            || (ownMaxAge > 0f && _scene.OrphanAnimAgeMs(index) >= ownMaxAge);
    }

    private bool HasReclaimableOrphan()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        for (int i = 0; i < _scene.OrphanCount; i++)
            if (!_anim.HasTracks(_scene.OrphanAt(i, out _, out _)) || OrphanDeadlinePassed(i, nowTicks))
                return true;
        return false;
    }

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
            float ownMaxAge = _scene.OrphanMaxAgeMs(i);
            if (OrphanDeadlinePassed(i, nowTicks))
            {
                // Diag.Enabled-gated: the interpolated message is an ARGUMENT, so it is built at the call site even when
                // the sink is off — an unconditional call here allocates on a frame that reclaims (a phases 6-13 breach).
                if (Diag.Enabled)
                    Diag.Event("scene", $"orphan-backstop force-reclaim wall={ageMs:0}ms anim={_scene.OrphanAnimAgeMs(i):0}ms own={ownMaxAge:0}ms (wedged exit track)");
                _scene.ReclaimOrphan(o);
                i = Math.Min(i - 1, _scene.OrphanCount - 1);
                continue;
            }
            i--;
        }
    }

    /// <summary>Slot-free fan-out (wired to <see cref="SceneStore.OnFreeIndex"/>): drop every INDEX-keyed per-node row
    /// the engine subsystems hold so a freed slot leaves nothing for the next node reusing that index to inherit. The
    /// animation tracks are retired here too: a render-owned track cannot request a later UI tick to self-prune.
    /// Scroll state needs no index-keyed clear here: a removed viewport's handle is unbound (and its plan slot released)
    /// by <c>OnScrollNodeRemoved</c>, and plan slots are keyed by (index, generation).</summary>
    private void OnSceneSlotFreed(int index)
    {
        _anim.ClearForIndex(index);
    }


    // ── Refresh-period helpers ───────────────────────────────────────────────────────────────────────────────────────
    // Everything here is integer/float arithmetic over values the frame already computed. No allocation, no formatting,
    // no syscalls beyond the two Stopwatch reads the frame took anyway — the phases 6-13 zero-alloc contract still binds.


    /// <summary>Refresh period in QPC ticks, sourced with priority: THIS window's own per-monitor value
    /// (<see cref="IPlatformWindow.DisplayRefreshPeriodQpc"/> — so a drag to a different-rate display, or simply
    /// being on a secondary monitor, re-paces with no app involvement) → the device's swapchain
    /// <see cref="FluentGpu.Rhi.PresentStats.RefreshPeriodQpc"/> (whole-app fallback: the only source before the
    /// first per-window sample, or on a backend with no per-monitor query) → 0 when neither reports anything.
    /// <paramref name="trusted"/> is the bit a caller needs to tell "genuinely measured" from "nothing known yet":
    /// true for any nonzero window value (a per-window API report is never speculative) or for a Valid device
    /// sample; false otherwise. The ONE place that orders window-over-device — <see cref="RefreshPeriodQpcOrDefault"/>
    /// and <see cref="RefreshPeriodTrusted"/> both funnel through this rather than re-deriving it.</summary>
    private long RefreshPeriodQpcSource(out bool trusted)
    {
        long fromWindow = _window.DisplayRefreshPeriodQpc;
        if (fromWindow > 0) { trusted = true; return fromWindow; }
        var stats = _swapchain.LastPresentStats;
        trusted = stats.Valid && stats.RefreshPeriodQpc > 0;
        return stats.RefreshPeriodQpc;
    }

    /// <summary>Refresh period in QPC ticks, MEASURED (<see cref="RefreshPeriodQpcSource"/> — the window's own
    /// per-monitor source when available, else DWM qpcRefreshPeriod via the device's PresentStats) rather than
    /// nominal. Falls back to 60 Hz only when neither source reports anything; a consumer distinguishes "measured"
    /// from "defaulted" via <see cref="RefreshPeriodTrusted"/> rather than the stats' Valid bit directly — this is
    /// the single funnel every pacing/prediction consumer (including <c>RefreshLattice.Build</c>'s staleness gate
    /// and present prediction) must read.</summary>
    private long RefreshPeriodQpcOrDefault()
    {
        long p = RefreshPeriodQpcSource(out _);
        return p > 0 ? p : Stopwatch.Frequency / 60;
    }

    /// <summary>True iff <see cref="RefreshPeriodQpcOrDefault"/>'s value is attested (the window's own period, or a
    /// Valid device PresentStats sample) rather than the 60 Hz default it silently substitutes. Companion helper so
    /// a caller that needs the trust bit (<c>SoftwarePaceMs</c>) reads the one funnel instead of re-deriving it from
    /// <c>_swapchain.LastPresentStats</c> directly and bypassing the window source.</summary>
    private bool RefreshPeriodTrusted()
    {
        RefreshPeriodQpcSource(out bool trusted);
        return trusted;
    }

    // QuantizedFrameSec (the ad-hoc lattice snap this method used to do inline) is now RefreshLattice.Snap, called
    // once per RunFrame at the top (see the _palFrameClock build there) — this file just consumes the result via
    // _palFrameClock.FrameQpc. The monotonicity floor is _frameClockFloorQpc.

    /// <summary>Called on whichever thread just returned from <c>Present()</c> (the render thread under the async
    /// default). Stamps WHEN the present returned and WHICH published frame it carried, then bumps the present count.
    /// The QPC read must stay the first statement: everything downstream of Present is attribution error.
    ///
    /// A present with <paramref name="publishSeq"/> == 0 (nothing newly acquired — the previous frame is still on
    /// screen) does NOT move the ack, so the ack stays monotone and a joiner never sees it go backwards.</summary>
    // Present cadence census (present-thread-owned writers, UI-side volatile readers via FrameStats).
    private long _presentedFramesTotal, _missedVsyncsTotal, _prevPresentedQpc;

    /// <summary>QPC timestamp (UI thread, <see cref="Stopwatch.GetTimestamp"/>), written via <see
    /// cref="NoteNoPresentTurn"/> at EVERY RunFrame/Paint exit that ran this turn but submitted no present for it —
    /// enumerated end-to-end in RunFrame: the window-close exit, the device-lost recovery block, the minimized
    /// park, the deep-idle "no active work" exit, the ProductionGateBlocks exit; and inside Paint: the reentrant-
    /// Paint guard, the modal-loop keep-alive idle skip, an image-pump device-lost recovery bail, the inline
    /// submit/present exception recovery, and — the case this field exists for — the skip-submit elision (Paint
    /// ran, the recorded draw list hashed identical to <see cref="_lastPresentedDrawListHash"/>, so the GPU
    /// submit + Present were elided because the already-presented frame is still correct). <see
    /// cref="NotePresented"/> reads it with `_lastNoPresentQpc > prev` — "did at least one no-present turn happen
    /// AFTER the previous present" — instead of latching+clearing a bool. Multiple UI-thread writers just overwrite
    /// the same long (monotonically, since QPC only increases); no Interlocked needed, and nothing is ever cleared.
    ///
    /// <para>Root cause #1 this exists to fix (measured live, 44 glides / 20 missed-vblank markers, 19 on frame 0):
    /// <see cref="NotePresented"/> used to derive "missed vsyncs" purely from the wall-clock GAP since the
    /// PREVIOUS present, with no notion of whether the loop was continuously trying to render in between. During a
    /// real idle stretch (streak in the thousands, idleAgo in the tens of seconds per the always-on [wake] census)
    /// RunFrame legitimately presents nothing — there is nothing to show, so there is nothing "missed". The first
    /// present after such a gap (a wheel notch waking the loop) then had `qpc - prev` equal to the ENTIRE idle
    /// duration, which the old formula divided by one vsync period and reported as a mid-glide vblank miss on frame
    /// 0 of every single glide — a measurement artifact, not a real dropped frame (mid-glide pacing is unaffected:
    /// dtP95 8.5-9.0ms against 8.3ms/120Hz, liveMissed=0).</para>
    ///
    /// <para>Root cause #2 (why a bool undercounted even after the first fix): a plain latch only covered the two
    /// RunFrame exits that skip Paint entirely (minimized, deep-idle). It missed every turn where Paint RAN but the
    /// present was ELIDED — dominated by the skip-submit path above, but also ProductionGateBlocks and the other
    /// mid-Paint bails — so a loop that stayed awake-but-nothing-to-show between two real presents (a playback
    /// tick, a diagnostics repaint) still charged the next real present for the entire gap. Production scroll
    /// traces kept showing a missed-vblank marker on frame 0 of wheel glides and on idle frames for exactly this
    /// reason. A timestamp fixes it by construction: any no-present turn, whenever in the gap it happened, moves
    /// the stamp past `prev` and excuses the whole gap up to the next present.</para>
    ///
    /// <para>Root cause #3 (the bool's race, async present): a bool set on the UI thread's idle exit could be
    /// consumed+cleared by a render-thread <see cref="NotePresented"/> call that was actually finishing a frame
    /// that was already in flight when the idle exit ran (async present completes off-thread, after WakeAsync
    /// already returned) — that late call's own gap is legitimately excused (the idle stamp predates it), but
    /// CONSUMING the flag there means the loop's next idle turn (same idle stretch — genuinely still nothing to
    /// do) has to re-arm it before the real wake present runs, and if that re-check ever raced the late call's
    /// clear at the memory level, the re-arm could be the write that got lost — dropping the wake present's excuse
    /// for a stretch that was never anything but idle. A stamp has nothing to lose the same way:
    /// <see cref="NoteNoPresentTurn"/> is just an overwrite, so a re-check after the late call always leaves
    /// `_lastNoPresentQpc` newer than that call's own present (which is now `prev` for the wake present), and
    /// `stamp > prev` sees it regardless of which thread wrote which value when — there is no clear step for a
    /// race to land on either side of. (A single stamp older than the late call's own present time does NOT, by
    /// itself, excuse a present that comes after that late call — `stamp > prev` is exactly as literal as it reads;
    /// what makes the whole stretch safe is that the loop keeps re-stamping for as long as it keeps re-entering its
    /// idle branch, which it does on every idle RunFrame call, not just the first.)</para>
    ///
    /// <para>Subtlety: a genuinely missed vblank between two live, back-to-back presents must still be counted —
    /// and it is, because between two consecutive PRESENTING turns no RunFrame/Paint turn can return without either
    /// presenting or hitting one of the enumerated no-present stamps above; there is no third kind of turn. So
    /// `_lastNoPresentQpc <= prev` holds in that case and NotePresented's gap math below runs unmodified, charging
    /// the real miss. NOT covered by this field (out of scope for this fix, flagged rather than silently ignored):
    /// a present-thread-owned elision inside <see cref="SubmitPresentOnRenderThread"/> itself (its own
    /// <c>ShouldSkipRenderSubmit</c> skip, and the clock-driven re-record early-out) — those are render-thread
    /// turns, not UI-thread RunFrame/Paint turns, and can still under-stamp a gap that contains only render-thread-
    /// side elisions with no UI-thread no-present turn in it.</para></summary>
    private long _lastNoPresentQpc;

    /// <summary>One-liner for every RunFrame/Paint exit that ran this turn but did not submit a present — see
    /// <see cref="_lastNoPresentQpc"/>'s doc for the full enumeration and why the comparison in <see
    /// cref="NotePresented"/> is race-free without clearing.</summary>
    private void NoteNoPresentTurn()
    {
        Volatile.Write(ref _lastNoPresentQpc, Stopwatch.GetTimestamp());
    }

    private void NotePresented(ulong publishSeq)
    {
        long qpc = Stopwatch.GetTimestamp();   // first statement: everything downstream of Present is attribution error
        Volatile.Write(ref _lastPresentQpc, qpc);
        // F215: the first present that actually put content on the glass (a stood-down present is not one).
        if (Volatile.Read(ref _firstPresentQpc) == 0 && _swapchain.HasPresentedContent) Volatile.Write(ref _firstPresentQpc, qpc);
        long prev = _prevPresentedQpc;
        _prevPresentedQpc = qpc;
        Interlocked.Increment(ref _presentedFramesTotal);
        // A no-present turn AFTER the previous present rebases the baseline WITHOUT charging the gap it covers as
        // missed vsyncs (see _lastNoPresentQpc's doc above) — the loop owed no frame across that stretch, so it
        // cannot have missed one. No clearing: the comparison against `prev` is what makes this race-free.
        bool wasIdle = Volatile.Read(ref _lastNoPresentQpc) > prev;
        if (!wasIdle && prev != 0 && qpc > prev)
        {
            long vsync = RefreshPeriodQpcOrDefault();
            long missed = (qpc - prev + vsync / 2) / vsync - 1;   // the standard half-interval-biased slot count
            if (missed > 0) Interlocked.Add(ref _missedVsyncsTotal, missed);
        }
        if (publishSeq != 0) Volatile.Write(ref _lastPresentPublishSeq, (long)publishSeq);
        Interlocked.Increment(ref _presentedSequence);
        // The one place the 60 Hz phase-lock is observable (§11.1.4): the interval between consecutive presents. The
        // gate sees only "publish owed a present", and in the locked state every publish IS presented — one refresh
        // late, forever. Present-thread-owned; the UI side only reads two volatile ints.
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
        anyChanged |= RefreshDynText(DynamicTextKind.FramePresentFps);
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
            DynamicTextKind.FramePresentFps => _presentFps <= 0.0 ? DynTextNoData : (long)Math.Round(_presentFps, MidpointRounding.AwayFromZero),
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
            DynamicTextKind.FramePresentFps => quant == DynTextNoData ? "--" : _presentFps.ToString("0", CultureInfo.InvariantCulture),
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
        string? dumpMode = EngineSwitches.SceneDump;   // `--fg dump[=all]`
        if (string.IsNullOrWhiteSpace(dumpMode)) return;
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
        // Viewport.Zoom ambient (display-only — the effective Scale above already contains the zoom). Value-gated
        // float compare so this per-frame path stays zero-alloc; the boxing publish happens only on an actual zoom
        // change (a keystroke / wheel notch), which also changed Scale and therefore got us past the early-out.
        float zoom = _window.Zoom;
        if (zoom != _lastZoom) { _viewportZoomSig.Value = zoom; _lastZoom = zoom; }
        // Resize mutates the same GPU targets the renderer reads. Every threaded mode parks, including force-sync:
        // its retained compositor scene can tick between explicit UI drains. Children park their parent's sole owner.
        //
        // Resize runs out of the WndProc, and every step of D3D12Swapchain.Resize (the fenced WaitForGpu signal,
        // ResizeBuffers, the GetBuffer per RTV) Checks its HRESULT and throws on a removed device — an unhandled throw
        // inside a window message. Consult NoteIfDeviceLost: a recorded loss means SKIP the resize (RecoverDevice
        // rebuilds the swapchain wholesale, and one stale-size frame until the recovery frame lands is invisible next to
        // a crash); anything else is a genuine bug and rethrows. The exception FILTER runs before the finally, so the
        // render loop is still Resumed on both outcomes.
        if (_isDetachedChild && _parentRenderThread is not null)
        {
            // A pop-out's OWN swapchain resize rides its mailbox (F207), applied on the shared render thread at the top of this
            // child's next turn - dragging the pop-out's edge never parks the parent's loop, nor the main window's presents.
            // Latest wins (F093): the several resizes a live drag posts between two turns collapse into one ResizeBuffers.
            // Invalidate FIRST, then post: the turn that applies the op sees the new epoch, so a frame this host published at the
            // old size is dropped there and can never be presented against the resized swapchain.
            _renderSeam.InvalidateTarget();
            PostOwnResize(s);
        }
        else if (OwningRenderThread is { } rt)
        {
            rt.Quiesce();
            try { _renderSeam.InvalidateTarget(); _swapchain.Resize(s); }
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

    // Idempotent. A throwing cleanup must not abort the teardown below (the swapchain / device would leak), so it is
    // routed to the diagnostic sink like a throwing posted action.
    private void UnmountComponentTree()
    {
        try
        {
            _reconciler.UnmountRoot();
            _runtime.Flush();   // once: effects the cleanups scheduled (all owned computations are disposed, so this is cheap)
        }
        catch (Exception ex) { Diag.Sink?.Invoke("[dispose] component-tree unmount threw (teardown continues): " + ex); }
    }

    /// <summary>The UI-only first phase of <see cref="Dispose"/>, idempotent: unmount the component tree so every scope cleanup
    /// (UnregisterPump, `PumpRequested -=`, static-signal unsubscribes, ReportLive) runs while the swapchain / registries /
    /// process seams it may touch still exist, and stop the cold-maintenance wake. Without the unmount a disposed child stayed
    /// reachable from process-lifetime signals and from the shared MediaPlayer's handlers. Split out so the pop-out reaper can
    /// run it BEFORE its single render-thread park (cleanups are app code and must not run inside the rendezvous).</summary>
    private void PrepareDispose()
    {
        UnmountComponentTree();
        if (_onSharedImageStatus is { } onImage) { _images.ImageStatusChanged -= onImage; _onSharedImageStatus = null; }
        lock (_coldMaintenanceWakeGate)
        {
            _coldMaintenanceStopped = true;
            _pixelPool.BufferRetained -= OnPixelBufferRetained;
        }
    }

    public void Dispose()
    {
        FrameLedger.Release(this);
        PrepareDispose();
        _renderThread?.Dispose();   // Step 4: stop + join the fgpu-render thread before tearing down the device it submits to
        // Detached child windows FIRST and OUTSIDE our own park (INCIDENT 2026-09 §2.4): each child's Dispose parks
        // OUR render thread itself (its OwningRenderThread is _parentRenderThread), and Quiesce is not re-entrant.
        // Our thread is already joined here (above), so those parks are no-ops; on a child host this list is empty.
        for (int i = _detachedHosts.Count - 1; i >= 0; i--) { _detachedHosts[i].ForwardPostsTo(_uiPoster); _detachedHosts[i].Dispose(); }
        _detachedHosts.Clear();
        var owner = OwningRenderThread;
        owner?.Quiesce();
        try { ReleaseRenderResourcesParked(); }
        finally { owner?.Resume(); }
        FinishDispose();
    }

    /// <summary>The part of the teardown that needs the render thread parked (or gone): the seam, popup windows and the host's own
    /// swapchain. Called with the park HELD - by <see cref="Dispose"/>, or by the parent's pop-out reaper, which holds ONE rendezvous
    /// for unregistering the child and releasing it (<see cref="ReapDetachedChild"/>); <c>Quiesce</c> is not re-entrant, so this never parks.</summary>
    private void ReleaseRenderResourcesParked()
    {
        _renderSeam.InvalidateTarget();
        PurgePopupRenderActions();   // the loop is parked (or gone): no drain can be in flight
        _imageQueue?.RemoveSceneReader(this);
        _renderSeam.ReleaseSceneResources();
        _scene.Recording.ReleaseInlineResources();
        for (int i = _popupWindows.Count - 1; i >= 0; i--)
        {
            _popupWindows[i].Swapchain?.Dispose();
            _popupWindows[i].DisposeWindow();
        }
        _popupWindows.Clear();
        // Popups closed while a render thread existed: their queued dispose was just purged (or the loop never reached it),
        // and the window release the render thread posts may never run on this host again.
        for (int i = _retiringPopups.Count - 1; i >= 0; i--)
        {
            var retiring = _retiringPopups[i];
            retiring.Swapchain?.Dispose();
            retiring.Swapchain = null;
            retiring.DisposeWindow();
        }
        _retiringPopups.Clear();
        // INSIDE the park (INCIDENT 2026-09 §1.3): for a detached child the owning thread is the parent's LIVE render
        // thread and DisposeSwapchain's fence wait / ReleaseStencilDsv must not race its recorder.
        _swapchain.Dispose();
    }

    /// <summary>The UI-thread tail of the teardown, after the render resources are released and the park (if any) is over:
    /// the host's process-wide seams, string refs and finally the window. Kept OUT of the parked part so a pop-out's window
    /// destruction never runs inside the render-thread rendezvous.</summary>
    private void FinishDispose()
    {
        FluentGpu.Media.MediaCensus.UnregisterRegistry(_videoSurfaces);   // F235: a reaped pop-out leaves the dual-handle scan (its slots were released with it)
        if (!_isDetachedChild && ReferenceEquals(HostDispatch.Current, _uiPoster))
            HostDispatch.Current = null;   // drop the process-static poster so a disposed host leaks no callback

        // Detach the activation-redirect subscription so a disposed host's IPlatformApp keeps no callback into it.
        if (_onActivationRedirected is { } onAct) { _app.ActivationRedirected -= onAct; _onActivationRedirected = null; }
        if (_onSystemColorsChanged is { } onSys) { _app.SystemColorsChanged -= onSys; _onSystemColorsChanged = null; }
        if (_onThumbButtonClicked is { } onThumb) { _app.ThumbButtonClicked -= onThumb; _onThumbButtonClicked = null; }
        if (_onAppNavigationCommand is { } onNav) { _app.AppNavigationCommand -= onNav; _onAppNavigationCommand = null; }
        if (_onTaskbarButtonCreated is { } onTbb) { _app.TaskbarButtonCreated -= onTbb; _onTaskbarButtonCreated = null; }
        // Symmetric SIP teardown: drop the OccludedRect subscription so a disposed host's window TextInput keeps no
        // callback into it (the SIP reflow closure captures _dispatcher).
        if (_onOccludedRectChanged is { } onOcc) { _window.TextInput.OccludedRectChanged -= onOcc; _onOccludedRectChanged = null; }

        // mem-05: the ctor mirrored this host's app.OpenUri onto the shared InputHooks.Current.Default channel (static
        // HyperlinkButton factories reach the seam there). Release it so a disposed host's IPlatformApp graph is
        // collectable — but ONLY if this host's delegate is still installed (Target == our _app): a later-constructed
        // host may have overwritten it (last-wins), and clearing that would break the live host's hyperlinks.
        // A detached child never installed any of these (the ctor skips them), and for OpenUri `cur.Target` is the SHARED
        // _app, so a child's Dispose would clear the PARENT's hyperlink seam: the whole block is the primary host's.
        var def = InputHooks.Current.Default;
        if (!_isDetachedChild && def.OpenUri is { } cur && ReferenceEquals(cur.Target, _app)) def.OpenUri = null;

        // Same release for the OS-drop seam: the ctor mirrored this host's dispatcher onto the channel-default. Clear it
        // only when our dispatcher is still the installed target (a later host may have overwritten it, last-wins).
        if (!_isDetachedChild && def.ExternalDragEnter is { } de && ReferenceEquals(de.Target, _dispatcher))
        {
            def.ExternalDragEnter = null;
            def.ExternalDragOver = null;
            def.ExternalDragLeave = null;
            def.ExternalDrop = null;
            def.ExternalDropFiles = null;
        }
        // Live drag-state seam (GetDragState captures this host): clear when ours is still installed.
        if (!_isDetachedChild && def.GetDragState is { } gds && ReferenceEquals(gds.Target, this))
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

        // A detached CHILD host shares the parent's device — it must NOT dispose it (the parent owns the device lifecycle).
        if (!_isDetachedChild) _device.Dispose();
        _window.Dispose();
    }
}
