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
    public double Fps { get; init; }
    public double FrameMs { get; init; }
    public int ComponentsRendered { get; init; }
    // Always-on per-segment timing of the last Paint (ms): flush=reconcile/component-render, layout=FlexLayout,
    // anim=phase-7 ticks, record=SceneRecorder (+ text shaping), submit=command build + GPU submit + present. ~5
    // Stopwatch reads/frame, zero alloc — so a profiler/probe can attribute a frame-time spike to a phase without FG_ALLOC_DIAG.
    public double FlushMs { get; init; }
    public double LayoutMs { get; init; }
    public double AnimMs { get; init; }
    public double RecordMs { get; init; }
    public double SubmitMs { get; init; }
    // Submit sub-split (diagnostics for the #1 hotspot — GPU fence/present pacing is charged to "submit" on the UI thread
    // until the render-thread seam lands). FenceWaitMs = wall-time BLOCKED on the frame fence + present-latency waitable
    // INSIDE SubmitDrawList; PresentMs = the Present() call. cmdBuild = SubmitMs − FenceWaitMs − PresentMs is the real CPU
    // command-build cost. Lets a probe attribute a 27 ms "submit" spike to the stall vs the build without an external profiler.
    public double FenceWaitMs { get; init; }
    public double PresentMs { get; init; }
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

public sealed class AppHost : IDisposable
{
    private readonly IPlatformApp _app;
    private readonly IPlatformWindow _window;
    private readonly IGpuDevice _device;
    private readonly ISwapchain _swapchain;
    private readonly Component _root;
    private readonly StringTable _strings;

    // E4 windowed out-of-bounds popups: one slot per leased popup window (see PopupWindowSlot).
    private readonly List<PopupWindowSlot> _popupWindows = new(2);
    private readonly List<NodeHandle> _popupSkipRoots = new(2);
    private int _popupTokenSeq;

    private readonly SceneStore _scene = new();
    private readonly ReactiveRuntime _runtime = new();
    private readonly TreeReconciler _reconciler;
    private readonly FlexLayout _layout;
    private readonly LayoutInvalidator _invalidator;
    private readonly DrawList _drawList = new();
    private readonly InputDispatcher _dispatcher;
    private readonly InputEventRing _ring = new();
    private readonly IFrameTimeSource _frameTime;
    private readonly AnimEngine _anim;
    private readonly ConnectedAnimation _connected;
    private readonly ScrollAnimator _scrollAnim;   // the deterministic, engine-owned scroll integrator (wheel/touchpad/touch/spring) — the ONLY scroll source
    private readonly RepeatTicker _repeat;
    private readonly CaretBlinker _caretBlinker;
    private readonly ImageCache _images;
    private readonly Dictionary<NodeHandle, ProjCapture> _projectBefore = new();   // captured presented rects of BoundsAnimated nodes (FLIP "First")

    /// <summary>FLIP "First" snapshot of a BoundsAnimated node, in PARENT-RELATIVE presented space (its own layout
    /// origin + in-flight LocalTransform). Parent-relative is what makes projections respond only to LOCAL movement:
    /// an ancestor reflow (an Expander reveal, a pane resize) shifts parent and child equally, the relative rect is
    /// unchanged, and the node rides the reflow RIGIDLY instead of re-FLIPping every frame. The parent handle is kept
    /// purely as a reparent guard — across different parents the relative frames are incomparable, so we snap.</summary>
    private readonly record struct ProjCapture(RectF Rel, NodeHandle Parent);

    // Ambient context signals (read via UseContext): published by the host, consumers subscribe granularly.
    private readonly Signal<object?> _viewportSig = new(default(Size2));
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
    private readonly WakeDiagnostics? _wakeDiag;
    private readonly MemCensus? _memCensus;

    /// <summary>MemCensus GPU-residency hook (FluentApp wires <c>D3D12Device.DiagResourceTotals</c>); headless leaves null.</summary>
    public Func<(long bytes, int count)>? GpuResources { get; set; }
    /// <summary>MemCensus GPU one-line detail hook (glyph/texture-store summary); headless leaves null.</summary>
    public Func<string>? GpuDetail { get; set; }

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

    // Ambient-animation frame-rate cap (FG_ANIM_FPS env, default 0 = UNCAPPED/display-rate). 0 lets the vsync-locked
    // present pace the loop to the panel's refresh — a clean 120 on a 120 Hz panel, a clean 60 on a 60 Hz panel — with
    // no extra software wait. A POSITIVE cap is an OPT-IN power throttle for perpetual loops (a spinner, skeleton
    // shimmer, reveal fade, implicit brush transition, caret blink) where a sub-refresh rate is imperceptible and idles
    // the CPU; an app/battery policy sets it via this property. WARNING: a positive cap BELOW the panel's refresh BEATS
    // against the vsync-locked present (the software wait stacks onto the vblank quantization), so e.g. a 60 cap on a
    // 120 Hz panel reads ~40–60, not a clean 60 — the default is 0 precisely to avoid that. Latency-SENSITIVE motion
    // (scroll/hover/press/drag/repeat — motion the user actively drives) is exempt and always runs at display rate; and
    // input/worker-posts wake the loop instantly regardless of the wait, so the cap NEVER adds input latency.
    private long _lastFrameStartTicks;
    // Pacing → timestep coupling (fps consistency). The wait the loop used to pace INTO the current frame: 0 = display
    // rate; >0 = ambient-throttled / HUD; -1 = blocked idle. A non-zero value means the frame clock's pending delta is a
    // STALE throttle/idle gap, not a real render interval — so Paint resyncs the clock before the anim tick when this
    // frame drives interactive or one-shot motion, killing the first-frame lurch on a scroll-start or a connected fly.
    private int _lastWaitMs;
    // Post-scroll grace window: keep display-rate pacing for a short tail after the last scroll-active frame so the eased
    // settle + any in-flight art reveal finish smoothly instead of snapping to the 30 Hz ambient cadence mid-motion.
    private long _scrollGraceUntil;
    private static readonly long ScrollGraceTicks = (long)(0.15 * Stopwatch.Frequency);
    public int AmbientAnimationFps { get; set; } = s_ambientFpsDefault;
    private static readonly int s_ambientFpsDefault = ReadAmbientFps();
    private static int ReadAmbientFps() => int.TryParse(Environment.GetEnvironmentVariable("FG_ANIM_FPS"), out var v) && v >= 0 ? v : 0;
    private const WakeReasons LatencySensitiveWake =
        WakeReasons.Interact | WakeReasons.ScrollAnim | WakeReasons.Repeat |
        WakeReasons.DragActive | WakeReasons.DragDropWork | WakeReasons.GestureHold |
        // Album-art reveals (decode → crossfade) fire DURING and right after a homepage scroll, and they are transient,
        // user-visible motion — keep them at the display rate instead of letting the ambient cap drop the reveal to 30 Hz
        // the instant the fling settles (a driver of the "scroll feels 24 fps then 120 fps" inconsistency). Both bits
        // clear the moment decode/reveal finishes, so this never holds the loop awake the way a perpetual loop would.
        WakeReasons.ImageCrossfades | WakeReasons.ImagesPending;
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
        int w = RecommendedWaitMsCore();
        _lastWaitMs = w;   // remembered so Paint can detect a throttle/idle → display-rate step-up and resync the frame clock
        return w;
    }

    private int RecommendedWaitMsCore()
    {
        if (IsMinimized) { MaybeTrimOnIdle(); return -1; }   // nothing to paint; only the restore message wakes us (see RunFrame's minimize gate)
        WakeReasons r = ComputeWakeReasons();
        if (r == WakeReasons.None) { MaybeTrimOnIdle(); return -1; }   // fully idle: trim the slab tail once, then block until a message arrives
        if (r == WakeReasons.DynamicText) return 100;   // HUD-only: 10 Hz readout, ~0% idle CPU
        // A live scroll arms a short display-rate grace so the eased settle + any in-flight art reveal finish at the
        // display rate instead of snapping back to the 30 Hz ambient cadence the instant the fling drops below cutoff.
        if ((r & WakeReasons.ScrollAnim) != 0) _scrollGraceUntil = Stopwatch.GetTimestamp() + ScrollGraceTicks;
        // Ambient-only animation (no latency-sensitive interaction live, and any AnimEngine activity is loop-only — a
        // spinner/shimmer, NOT a one-shot transition mid-flight): pace to AmbientAnimationFps instead of the full
        // display refresh. A real input/post still wakes WaitForWork early, so this paces only the autonomous tick.
        if (AmbientAnimationFps > 0 && (r & LatencySensitiveWake) == 0 && AnimIsAmbient()
            && Stopwatch.GetTimestamp() >= _scrollGraceUntil)
        {
            MaybeTrimOnIdle();   // #10: playback/ambient never reaches WakeReasons.None, so trim the slab tail here too (30s-cadence-gated)
            return AmbientFrameWaitMs();
        }
        return 0;                                   // latency-sensitive / one-shot motion: display-rate pacing
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
    }

    // ── Skip-submit gate state (finding #3a) ─────────────────────────────────────────────────────────────────────────
    private ulong _lastPresentedDrawListHash;   // FNV-1a of the last PRESENTED command stream; a byte-identical frame skips submit+present
    private long _framesSkippedSubmit;          // diagnostic census of elided submits (idle/playback redundant presents avoided)
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
        if (_runtime.HasPending) r |= WakeReasons.RuntimePending;
        if (_scene.HasDynamicText) r |= WakeReasons.DynamicText;
        if (_anim.HasActive || _connected.HasActive) r |= WakeReasons.Anim;   // connected fly / snapshot awaiting dest; hover/press fades are now _anim tracks too
        if (_scrollAnim.HasActive) r |= WakeReasons.ScrollAnim;
        if (_repeat.HasActive) r |= WakeReasons.Repeat;
        if (_caretBlinker.HasActive) r |= WakeReasons.Caret;
        if (_scene.HasBrushAnims) r |= WakeReasons.BrushAnims;
        if (_images.PendingCount > 0) r |= WakeReasons.ImagesPending;
        if (_device.HasPendingUploads) r |= WakeReasons.ImagesPending;
        if (_images.HasActiveCrossfades) r |= WakeReasons.ImageCrossfades;
        if (_scene.OrphanCount > 0) r |= WakeReasons.Orphans;
        if (_dispatcher.Drag.HasActiveWork || _dispatcher.DragDrop.HasActiveWork) r |= WakeReasons.DragDropWork;   // E5: ghost spring easing / edge auto-scroll
        if (_dispatcher.Drag.IsActive) r |= WakeReasons.DragActive;   // E5 reorder dwell keep-alive: a live drag keeps frames coming so the 200/300ms FrameClock dwell tickers advance even on a motionless pointer (DragController.cs:118)
        if (_dispatcher.HasArmedHold) r |= WakeReasons.GestureHold;   // §7A touch long-press: a STATIONARY held finger emits no input, so keep frames coming until TickGestureArenas fires the ~500ms Hold (then this clears and the loop idles)
        // A windowed popup's desktop-acrylic open reveal is driven per-frame on Present (CompositionBackdrop.TickAnimation),
        // so it needs the loop to keep presenting until it settles — otherwise (no engine animation active for windowed
        // menus) the loop idle-skips and the reveal freezes at its seed. O(popups) ≈ O(1) (typically 0–1 menus open).
        for (int i = 0; i < _popupWindows.Count; i++)
            if (_popupWindows[i].Swapchain?.PopupAnimating == true) { r |= WakeReasons.PopupAnim; break; }
        return r;
    }

    /// <summary>Enable inertial smooth scrolling + auto-hiding scrollbars (the real app turns this on; off = immediate).</summary>
    public bool SmoothScroll { get => _dispatcher.SmoothScroll; set => _dispatcher.SmoothScroll = value; }

    public ImageCache Images => _images;

    // Census accessors (read by MemCensus / CensusSnapshot — same assembly): the subsystems Scene/Animation/Images
    // already expose are reused; these surface the rest. All passive O(1) reads.
    internal StringTable Strings => _strings;
    internal TreeReconciler Reconciler => _reconciler;
    internal int InteractionAnimatorCensus => _anim.HoverPressTrackCount;   // hover/press are now engine HoverFade/PressFade tracks (InteractionAnimator deleted)
    internal int ScrollAnimatorCensus => _scrollAnim.ActiveCount;

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
                   ScrollTuning? scrollTuning = null)
    {
        _app = app;
        _window = window;
        PopupWindowsEnabled = window.Handle.Kind == NativeHandleKind.Headless || device.SupportsSecondarySwapchains;
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
        _frameTime = frameTime ?? (window.Handle.Kind == NativeHandleKind.Headless ? new FixedFrameTimeSource() : new StopwatchFrameTimeSource());
        _swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx));
        _reconciler = new TreeReconciler(_scene, strings, _runtime);
        _layout = new FlexLayout(_scene, fonts);
        _invalidator = new LayoutInvalidator(_scene, _layout);
        var scrollProfile = scrollTuning ?? ScrollTuning.WinUiLike;   // WinUI-parity wheel distance + feel (the Win32 app default)
        _dispatcher = new InputDispatcher(_scene) { Tuning = scrollProfile };
        _reconciler.OnSubtreeDeactivated = _dispatcher.DeactivateSubtree;
        _anim = new AnimEngine(_scene);
        _connected = new ConnectedAnimation(_scene, _anim, _images);   // shared-element (connected-animation) Hero flies
        // Scroll is fully engine-owned: the deterministic ScrollAnimator is the single, portable scroll source on every
        // platform (wheel target-chase + touch/touchpad fling + overscroll spring + conscious scrollbar). There is no OS
        // scroll source — touchpad arrives as hi-res WM_POINTERWHEEL → PanTouchpad, touch as the dispatcher's gesture path.
        _scrollAnim = new ScrollAnimator(_scene, scrollProfile);
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
        _dispatcher.OnRepeatArmed = _repeat.Arm;
        _dispatcher.OnRepeatReleased = _repeat.Disarm;
        _dispatcher.OnRepeatPaused = _repeat.Pause;     // held pointer left the repeat node → stop ticking
        _dispatcher.OnRepeatResumed = _repeat.Resume;   // re-entered → fresh initial delay, no immediate re-fire
        _dispatcher.OnKeyPreview = _inputHooks.Preview;   // an open overlay/flyout can intercept Escape (registered via the InputHooks ambient)
        _inputHooks.PointerVelocity = () => _dispatcher.PointerVelocity;        // cross-axis swipe controls snap on real flick speed
        _inputHooks.GetFocus = () => _dispatcher.Focused;                       // an opening overlay captures focus to restore on close
        _inputHooks.RestoreFocus = h => _dispatcher.SetFocus(h, visual: false);
        _inputHooks.FocusNode = (h, visual) => _dispatcher.SetFocus(h, visual);
        _inputHooks.MoveFocusVisual = h => _dispatcher.SetFocus(h, visual: true);   // roving arrow-key focus shows the ring (RadioButtons)
        _inputHooks.PushFocusScope = _dispatcher.PushFocusScope;     // REAL Tab trap for FocusTrap overlays (ContentDialog)
        _inputHooks.PopFocusScope = _dispatcher.RemoveFocusScope;    // order-independent (overlays close out of stack order)
        _inputHooks.FirstFocusableIn = _dispatcher.FirstFocusableIn; // focus-trap initial focus (first tab stop / default button)
        _dispatcher.OnCursorChanged = _window.SetCursor;                        // hover-resolved cursor (hand/I-beam/resize)
        _dispatcher.OnWindowBlur = _inputHooks.NotifyWindowBlur;                // deactivation → light-dismiss overlays close

        // Custom-titlebar chrome seam (WindowDesc.CustomFrame): pull-state + caption commands to the window, the
        // region push (relayout-only), and an epoch signal bumped on activation/placement changes so the TitleBar
        // control re-renders (dim / max↔restore glyph). All members default-no-op on standard-frame backends.
        _inputHooks.GetWindowState = () => _window.State;
        _inputHooks.IsWindowActive = () => _window.IsActive;
        _inputHooks.WindowMinimize = _window.Minimize;
        _inputHooks.WindowToggleMaximize = _window.ToggleMaximize;
        _inputHooks.WindowClose = _window.CloseWindow;
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
        // KeepAlive park/un-park → quiesce/resume the parked subtree's animation + scroll tickers so a backgrounded tab's
        // looping animation or mid-fling scroll can't keep the frame loop awake (defeating the idle wake-stop). A parked
        // shared-element node also captures its reverse-fly snapshot here (Back returns to it via the like-tagged dest).
        _reconciler.OnNodeParkedChanged = (node, parked) =>
        {
            _anim.SetNodeParked(node, parked); _scrollAnim.SetNodeParked(node, parked);
            _connected.OnNodeParked(node, parked);
        };
        // Symmetric teardown of INDEX-keyed per-node side-tables on slot free (mem-06): a freed node's slot is reused,
        // so the AnimEngine layout-transition spec + the ScrollAnimator conscious-bar timers (both keyed by node index,
        // not gen-checked handle) must be dropped or the next node reusing that index inherits the stale row.
        _scene.OnFreeIndex = OnSceneSlotFreed;
        _reconciler.Images = _images;
        _reconciler.ImageEpoch = _imageEpoch;
        _images.SetPixelAttemptSink(_device.TryUploadImage);
        _images.SetEvictSink(_device.EvictImage);
        _images.ImageStatusChanged += (_, _, _, _) => { if (_imageEpoch.HasSubscribers) _imageEpoch.Value = _imageEpoch.Peek() + 1; WakeFrame(); };

        // Publish ambient contexts before the first render so UseContext(Viewport.Size)/FrameDiagnostics resolve.
        _lastViewportDip = ClientSizeDip();
        _viewportSig.Value = _lastViewportDip;
        _inputHooksSig = new Signal<object?>(_inputHooks);
        _reconciler.SetAmbient(Viewport.Size, _viewportSig);
        _reconciler.SetAmbient(FrameDiagnostics.Current, _frameStatsSig);
        _reconciler.SetAmbient(InputHooks.Current, _inputHooksSig);
        _reconciler.SetAmbient(FrameClock.Tick, _frameClockSig);
        _hostPostSig = new Signal<object?>((Action<Action>)Post);   // ambient UI-thread poster (HostDispatch.Post / UsePost)
        _reconciler.SetAmbient(HostDispatch.Post, _hostPostSig);
        _reconciler.SetAmbient(SharedTransition.Begin, new Signal<object?>((Action<string>)_connected.Begin));   // connected-anim forward capture-at-click
        _reconciler.SetAmbient(SharedTransition.SetMotion, new Signal<object?>((Action<FluentGpu.Animation.ConnectedMotion>)(m => _connected.FlyMotion = m)));   // live fly-curve switcher (app A/B)
        // Window-visibility ambient: the channel value IS the visibility signal (an IReadSignal<bool>, never re-published),
        // so UseIsActive resolves it once and subscribes to the INNER signal — see Activation.IsActive.
        _reconciler.SetAmbient(Activation.IsActive, new Signal<object?>(_windowVisible));
        _reconciler.SetAmbient(ThemeControl.Request, new Signal<object?>((Action<float>)RequestThemeTransition));   // live re-theme trigger for app code

        // Keep-alive repaint: the OS fires this synchronously from inside a modal move/size loop (and on NC
        // hover/press transitions while the frame loop idles). Paint with keepAlive so the device skips its
        // frame-latency throttle wait — otherwise each fires a full vblank-class stall inline on the WndProc thread
        // (the drag-start / live-resize hitch). Live resize still paints synchronously; it just no longer blocks.
        _window.PaintRequested = () => Paint(0, keepAlive: true);

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
    /// offset-space velocity here. Seed the viewport's <see cref="ScrollState.FlingVelocity"/> + <c>ScrollMode=Fling</c>
    /// and arm the <see cref="ScrollAnimator"/> so phase 7 friction-decays it (and <c>WakeReasons.ScrollAnim</c> keeps
    /// frames coming until it settles). 0-alloc: a cached method group, two field writes on a ref.</summary>
    private void SeedScrollFling(NodeHandle node, float velocityPxPerS)
    {
        if (node.IsNull || !_scene.IsLive(node) || !_scene.HasScroll(node)) return;
        ref ScrollState sc = ref _scene.ScrollRef(node);
        sc.FlingVelocity = velocityPxPerS;
        sc.ScrollMode = 1;   // ScrollAnimator Fling mode
        // A snap-configured viewport re-solves the velocity on the FIRST fling tick (ScrollAnimator) so the same decay
        // curve lands EXACTLY on a snap value — capture the launch offset (the impulse "ignored value" anchor) and reset
        // the one-shot retarget latch here. A non-snap viewport ignores both.
        sc.FlingRetargeted = false;
        sc.FlingSnapTarget = float.NaN;
        sc.FlingFromOffset = sc.Orientation == 1 ? sc.OffsetX : sc.OffsetY;
        _scrollAnim.Arm(node);
    }

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
        _window.PumpInto(_ring);              // 1 pump
        if (s_allocDiag) { db = Probe(SegPump, db, dt); dt = Stopwatch.GetTimestamp(); }
        int clicks = _dispatcher.Dispatch(_ring.Drain());  // 2 input dispatch (handlers write signals → schedule effects)
        if (s_allocDiag) { db = Probe(SegDispatch, db, dt); dt = Stopwatch.GetTimestamp(); }

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
        if (_inPaint) { _frameAfterPaint = true; return LastStats; }
        _inPaint = true;
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
            // FG_RESIZE_DIAG: per-tick segment timing of the modal-loop keep-alive paint. Captured only when both the flag
            // is on AND this is a keep-alive tick — zero work / zero alloc otherwise (the normal hot path is untouched).
            bool diagTick = keepAlive && s_resizeDiag;
            double ensureMs = 0, layoutMs0 = 0;
            long segStart = diagTick ? Stopwatch.GetTimestamp() : 0;
            bool resized = EnsureSize();
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
            if (keepAlive && !resized && _everLaidOut && !_needFullLayout
                && _uiPosts.IsEmpty && !_scene.AnyLayoutDirty
                && (ComputeWakeReasons() == WakeReasons.None || (_window.InModalLoop && AnimIsAmbient())))
                return LastStats;

            var layoutSize = ClientSizeDip();
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
                CaptureProjections(_scene.Root);
                capturedProjections = _projectBefore.Count > 0;
            }
            if (s_allocDiag) { db = Probe(SegFlip, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            long before = GC.GetAllocatedBytesForCurrentThread();

            // Drain cross-thread UI posts so their signal writes land in THIS flush. RunFrame already drained them before
            // its idle gate, so on the normal frame path this is a no-op on an empty queue; it earns its keep on the
            // Paint-ONLY path (the PaintRequested keep-alive fired from inside an OS modal move/size loop, which bypasses
            // RunFrame entirely) — there a post that arrived mid-drag still applies this frame instead of being stranded.
            if (!_uiPosts.IsEmpty) _runtime.Batch(DrainUiPosts);
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
            try
            {
                _runtime.Flush();                              // 3–5 apply scheduled re-renders (render-effects reconcile) + bindings
                virtualsChanged = _reconciler.ReRealizeVirtuals();   // virtual boundary re-realize (granular)
                if (virtualsChanged && _runtime.HasPending) _runtime.Flush();   // bound-row rebinds (slot signal writes) land THIS frame
            }
            finally { if (themeChanged) _reconciler.SetThemeTransition(float.NaN); }
            bool reconciled = _reconciler.ConsumeReconciled() || virtualsChanged;
            long tFlush = Stopwatch.GetTimestamp();   // always-on segment timing (FrameStats.*Ms) — see below
            if (s_allocDiag) { db = Probe(SegFlush, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

            bool layoutNeeded = _needFullLayout || reconciled || _scene.AnyLayoutDirty;
            string layoutPath = "none";
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
            if (_lastWaitMs != 0)
            {
                WakeReasons stepUp = ComputeWakeReasons();
                if ((stepUp & LatencySensitiveWake) != 0 || (_anim.HasActive && !AnimIsAmbient()) || _connected.HasActive)
                    _frameTime.Resync();
            }
            float dtMs = _frameTime.NextDeltaMs();
            _anim.Tick(dtMs);                                  // 7 animation (transform/opacity/presented-size — never relayout)
            _inputHooks.RunAfterAnimations();                  // 7.1 tree lifecycle finalizers (overlays) before record/present
            RunIncrementalLayout();                            // 7 scoped subtree relayout for SizeMode.Relayout
            RunReflowLayout(layoutSize);                       // 7 boundary-scoped re-solve for SizeMode.Reflow (smooth reflow)
            ReclaimSettledOrphans();                           // 7 free settled exit orphans
            _connected.Settle();                               // 7 retire landed shared-element flies (reveal dest, unpin, free overlay)
            _connected.SyncDetached();                         // 7 flag-gated rebuild: mirror the engine-animated fly into its DetachedNode snapshot (RecordDetached draws it)
            // 7 eased hover/press: HoverT/PressT now driven by the engine's HoverFade/PressFade tracks (ticked in _anim.Tick above); InteractionAnimator deleted
            // 7 implicit BrushTransition: the cross-fade T is now driven by the unified engine (AnimChannel.BrushFade,
            // seeded at reconcile); the separate per-frame AdvanceBrushAnims ticker is deleted.
            _dispatcher.TickTouchpad(dtMs);                    // 7 precision-touchpad pan: ease applied offset + lift→inertia (before the pump so the fling it seeds runs this frame)
            _scrollAnim.Tick(dtMs);                            // 7 smooth scroll + fling + overscroll spring + scrollbar fade (the engine-owned integrator)
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
            var recordStats = SceneRecorder.Record(_scene, _drawList, _images, in focus, Tok.ScrollThumb, Tok.AcrylicFlyout.Fallback, in textEdit,
                CollectionsMarshal.AsSpan(_popupSkipRoots)); // 8 record
            SceneRecorder.RecordDetached(_scene, _drawList, _images, _connected.Detached, _scene.OverlayClip);   // 8 detached fly snapshots (flag-gated rebuild; no-op when none)
            RecordPopupWindows(in focus, in textEdit);         // 8b record each popup window's subtree DrawList
            // 8c consume the frame's motion bits (the glyph-snap gate read them during record). A motion frame queues ONE
            // settle frame: the last moved frame recorded its text unsnapped, so the trailing static record re-snaps crisp.
            bool transformWrote = _scene.AnyTransformWrote;
            if (transformWrote) { _frameAfterPaint = true; _scene.ClearTransformDirty(); }
            long tRecord = Stopwatch.GetTimestamp();
            if (s_allocDiag) { db = Probe(SegRecord, db, dt0); dt0 = Stopwatch.GetTimestamp(); }
            // Modal-loop repaint: present at SyncInterval 0 so Present is a cheap, tear-free hand-off (the composited DComp
            // flip surface is still composited at vblank by DWM) instead of blocking the WndProc thread up to a vblank — the
            // live-resize/move hitch. We KEEP the frame-latency waitable wait (do NOT SuppressLatencyWaitOnce here) as the
            // pacing gate: with SetMaximumFrameLatency=1 it self-throttles the producer to one in-flight frame so interval-0
            // presents can't back up the present queue and re-block in Present/back-buffer acquire (DXGI pacing review).
            // Skip-submit gate (idle/slow-change power, finding #3a): when this frame mutated nothing the recorder reads
            // (no reconcile, no relayout, no transform write) AND the recorded command stream is byte-identical to the last
            // PRESENTED frame, the already-presented front buffer is still correct — elide the GPU submit + Present (the
            // dominant ~2.5ms/frame cost at rest). The cheap flags short-circuit so ACTIVE frames never hash; the hash
            // confirms byte-identity for paint-channel / image-state changes that set no flag. Conservative: steady main
            // window only (presented before, no resize, not a modal keep-alive, no interleaving popup windows). A playback
            // playhead quantized to whole pixels (SeekBar) lands on the same stream most frames, so this fires during play.
            bool maybeUnchanged = _everLaidOut && !resized && !keepAlive && _popupWindows.Count == 0
                && !reconciled && !layoutNeeded && !transformWrote
                && !_device.HasPendingUploads;   // a staged texture upload is flushed INSIDE submit — skipping would leave it white
            ulong dlHash = maybeUnchanged ? DrawListHash(_drawList.Bytes, _drawList.SortKeys) : 0UL;
            bool skipSubmit = maybeUnchanged && dlHash == _lastPresentedDrawListHash;
            long subStart = (keepAlive && s_resizeDiag) ? Stopwatch.GetTimestamp() : 0;
            long tSubmitDone, tSubmit, hotAlloc;
            if (skipSubmit)
            {
                _framesSkippedSubmit++;
                hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
                tSubmitDone = tSubmit = Stopwatch.GetTimestamp();
            }
            else
            {
                if (keepAlive) _device.SuppressVsyncOnce();
                _device.SubmitDrawList(_drawList.Bytes, _drawList.SortKeys,
                    new FrameInfo(_window.ClientSizePx, _window.Scale, Clear, recordStats.Damage)); // 10 submit
                tSubmitDone = Stopwatch.GetTimestamp();        // boundary: SubmitDrawList done, Present not yet called
                _swapchain.Present();                          // 11 present
                if (maybeUnchanged) _lastPresentedDrawListHash = dlHash;   // track the stream only across quiet runs (active frames don't hash)
                hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
                tSubmit = Stopwatch.GetTimestamp();
            }
            if (s_allocDiag) { db = Probe(SegSubmit, db, dt0); dt0 = Stopwatch.GetTimestamp(); }

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
                DrawNodeCount = recordStats.DrawnNodeCount,
                CulledNodeCount = recordStats.CulledNodeCount,
                Fps = _fps,
                FrameMs = _frameMs,
                ComponentsRendered = componentsRendered,
                FlushMs = ToMs(tFlush - frameStart),   // incl. flip/FLIP-capture + reactive flush + reconcile
                LayoutMs = ToMs(tLayout - tFlush),
                AnimMs = ToMs(tAnim - tLayout),         // phase-7 ticks + projections
                RecordMs = ToMs(tRecord - tAnim),       // image pump + SceneRecorder (+ text shaping) + dyntext
                SubmitMs = ToMs(tSubmit - tRecord),     // command build + GPU submit + present (total; ~0 on a skipped frame)
                FenceWaitMs = skipSubmit ? 0.0 : _device.LastFenceWaitMs,  // of which: UI-thread stall on the frame fence + latency waitable
                PresentMs = ToMs(tSubmit - tSubmitDone),// of which: the Present() call (0 on a skipped frame)
            };
            PublishFrameStats(LastStats);
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
    private void CaptureProjections(NodeHandle n)
    {
        if (n.IsNull) return;
        if ((_scene.Flags(n) & NodeFlags.BoundsAnimated) != 0)
        {
            // FLIP relativeTarget: capture relative to the resolved shared-layout anchor (if any) instead of the parent,
            // so the node rides the anchor's motion coherently (its anchor-relative rect is unchanged ⇒ no re-FLIP).
            NodeHandle anchor = _reconciler.ResolveRelativeTarget(n);
            _projectBefore[n] = anchor.IsNull
                ? new ProjCapture(RelRect(n), _scene.Parent(n))
                : new ProjCapture(RelRectIn(n, anchor), anchor);
        }
        for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
            CaptureProjections(c);
    }

    private void ApplyProjections()
    {
        // Deadbands: below these the commit didn't move/resize the node WITHIN ITS PARENT, so it must ride any
        // ancestor reflow rigidly. The skip is required for correctness, not a fast path — AnimateBounds on a
        // zero delta RESTARTS a full-duration tween from the current value (and seeds throwaway spring tracks),
        // which is exactly the "knob lags its own track during a reveal" desync. In-flight tracks keep running.
        const float PosEps = 0.05f;
        const float SizeEps = 0.5f;   // matches RevealSize's no-change deadband (AnimEngine)
        bool reduced = Motion.ReducedMotion;
        foreach (var kv in _projectBefore)
        {
            var n = kv.Key;
            if (n == _dispatcher.Drag.ActiveNode) continue;   // E5: the pointer owns the dragged node's transform
            if (!_scene.IsLive(n) || (_scene.Flags(n) & NodeFlags.BoundsAnimated) == 0) continue;
            // The captured "frame" is the parent OR (for a relativeTarget) the shared-layout anchor; re-resolve it now.
            NodeHandle anchor = _reconciler.ResolveRelativeTarget(n);
            NodeHandle frameNow = anchor.IsNull ? _scene.Parent(n) : anchor;
            if (frameNow != kv.Value.Parent) continue;   // reparented / anchor changed: rel frames incomparable — snap
            RectF from = kv.Value.Rel, to = anchor.IsNull ? RelRect(n) : RelRectIn(n, anchor);
            if (MathF.Abs(from.X - to.X) < PosEps && MathF.Abs(from.Y - to.Y) < PosEps
                && MathF.Abs(from.W - to.W) < SizeEps && MathF.Abs(from.H - to.H) < SizeEps)
                continue;                                     // no LOCAL change ⇒ ancestor-driven move only
            if (!_anim.TryGetTransition(n, out var spec)) continue;
            if (reduced) spec = spec with { Dynamics = TransitionDynamics.Tween(1f, Easing.Linear) };
            // AnimateBounds consumes only deltas, so parent-relative rects feed it directly; for a purely local
            // move this is bit-identical to the old absolute pair (the ancestor sum cancels).
            _anim.AnimateBounds(n, from, to, spec);
        }
        _projectBefore.Clear();
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
        for (int i = _scene.OrphanCount - 1; i >= 0; i--)
        {
            var o = _scene.OrphanAt(i, out _, out _);
            if (!_anim.HasTracks(o)) { _scene.ReclaimOrphan(o); continue; }
            double ageMs = (nowTicks - _scene.OrphanEnqueuedTicks(i)) * 1000.0 / Stopwatch.Frequency;
            if (ageMs >= OrphanSettleTimeoutMs)
            {
                Diag.Event("scene", $"orphan-backstop force-reclaim age={ageMs:0}ms (wedged exit track)");
                _scene.ReclaimOrphan(o);
            }
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
    {
        Drain(_root.Context.PendingLayoutEffects);
        foreach (var c in _reconciler.LiveComponents) Drain(c.Context.PendingLayoutEffects);
    }

    private void DrainPassiveEffects()
    {
        Drain(_root.Context.PendingEffects);
        foreach (var c in _reconciler.LiveComponents) Drain(c.Context.PendingEffects);
    }

    private static void Drain(List<Action> q)
    {
        if (q.Count == 0) return;
        foreach (var e in q) e();
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

    /// <summary>Resize the swapchain to match the window's client size; force a full re-layout on change.
    /// Returns true if the client size changed this frame (so the caller can SNAP layout — a window resize must not
    /// FLIP-animate content; the pre-resize rects are stale and projecting them shifts the content + reveals the backdrop).</summary>
    private bool EnsureSize()
    {
        // Scale participates too: a per-monitor DPI change (WM_DPICHANGED) re-scales the window — usually the px
        // size changes with the suggested rect, but even when it doesn't, the DIP viewport (px/scale) did, so the
        // tree must re-lay-out (glyph re-rasterization keys on the per-frame FrameInfo scale by itself).
        var s = _window.ClientSizePx;
        float scale = _window.Scale;
        if (s.Width == _lastSize.Width && s.Height == _lastSize.Height && scale == _lastScale) return false;
        _lastSize = s;
        _lastScale = scale;
        _swapchain.Resize(s);
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
        _swapchain.Dispose();
        _device.Dispose();
        _window.Dispose();
    }
}
