using System.Diagnostics;
using System.Globalization;
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
}

/// <summary>
/// Composition root + the single-UI-thread frame loop. Signals-first: a setState writes a signal that schedules ONLY
/// the owning component's render-effect (granular), and a bound high-frequency scalar (slider/scroll) writes a node
/// channel directly — a compositor-only frame with no render/reconcile/layout. The host drains the reactive runtime
/// once per frame (phase 3), runs (scoped) layout only when a reconcile/layout-bind changed something, then records.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly IPlatformWindow _window;
    private readonly IGpuDevice _device;
    private readonly ISwapchain _swapchain;
    private readonly Component _root;
    private readonly StringTable _strings;

    private readonly SceneStore _scene = new();
    private readonly ReactiveRuntime _runtime = new();
    private readonly TreeReconciler _reconciler;
    private readonly FlexLayout _layout;
    private readonly LayoutInvalidator _invalidator;
    private readonly DrawList _drawList = new();
    private readonly InputDispatcher _dispatcher;
    private readonly InputEventRing _ring = new();
    private readonly AnimEngine _anim;
    private readonly InteractionAnimator _interact;
    private readonly ScrollAnimator _scrollAnim;
    private readonly RepeatTicker _repeat;
    private readonly ImageCache _images;
    private readonly Dictionary<NodeHandle, RectF> _projectBefore = new();   // captured presented rects of BoundsAnimated nodes (FLIP "First")

    // Ambient context signals (read via UseContext): published by the host, consumers subscribe granularly.
    private readonly Signal<object?> _viewportSig = new(default(Size2));
    private readonly Signal<object?> _frameStatsSig = new(default(FrameStats));
    private readonly InputHooks _inputHooks = new();
    private readonly Signal<object?> _inputHooksSig;
    private readonly Signal<int> _imageEpoch = new(0);   // bumped on any image status change → re-renders UseImage consumers
    private Size2 _lastViewportDip;

    private bool _frameNeeded = true;        // a frame is required (reactive work pending, input, resize, …)
    private bool _frameAfterPaint;           // a wake arrived during paint → run another frame
    private bool _needFullLayout = true;     // first frame / resize / DPI / root structural change
    private bool _everLaidOut;               // suppress FLIP capture until the first layout (freshly-mounted nodes have no "before")
    private bool _inPaint;
    private Size2 _lastSize;
    private readonly long[] _presentTimes = new long[240];
    private int _presentTimeNext;
    private int _presentTimeCount;
    private double _fps;
    private double _frameMs;
    private const double FpsWindowSeconds = 1.0;
    private static ColorF Clear => Theme.WindowBackground;

    public SceneStore Scene => _scene;
    public AnimEngine Animation => _anim;
    public FrameStats LastStats { get; private set; }
    public bool HasActiveWork => _frameNeeded || _runtime.HasPending || _scene.HasDynamicText || _anim.HasActive
        || _interact.HasActive || _scrollAnim.HasActive || _repeat.HasActive || _images.PendingCount > 0 || _images.HasActiveCrossfades || _scene.OrphanCount > 0;

    /// <summary>Enable inertial smooth scrolling + auto-hiding scrollbars (the real app turns this on; off = immediate).</summary>
    public bool SmoothScroll { get => _dispatcher.SmoothScroll; set => _dispatcher.SmoothScroll = value; }

    public ImageCache Images => _images;

    public AppHost(IPlatformApp app, IPlatformWindow window, IGpuDevice device, IFontSystem fonts,
                   StringTable strings, Component root, ImageCache? images = null)
    {
        _window = window;
        _device = device;
        _root = root;
        _strings = strings;
        _images = images ?? new ImageCache(new FakeImageDecoder());
        _swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx));
        _reconciler = new TreeReconciler(_scene, strings, _runtime);
        _layout = new FlexLayout(_scene, fonts);
        _invalidator = new LayoutInvalidator(_scene, _layout);
        _dispatcher = new InputDispatcher(_scene);
        _anim = new AnimEngine(_scene);
        _interact = new InteractionAnimator(_scene);
        _scrollAnim = new ScrollAnimator(_scene);
        _repeat = new RepeatTicker(_scene);
        _lastSize = window.ClientSizePx;

        // A reactive write (anywhere) requests a frame.
        _runtime.FrameRequested = WakeFrame;
        _dispatcher.RequestRerender = WakeFrame;   // virtual list crossing an item boundary on scroll
        _scrollAnim.RequestRerender = WakeFrame;   // re-realize the virtual window on a boundary crossing
        _dispatcher.OnHoverChanged = _interact.SetHover;
        _dispatcher.OnPressChanged = _interact.SetPress;
        _dispatcher.OnScrollArmed = _scrollAnim.Arm;
        _dispatcher.OnScrollHover = _scrollAnim.Hover;
        _dispatcher.OnScrollLeave = _scrollAnim.Leave;
        _dispatcher.OnRepeatArmed = _repeat.Arm;
        _dispatcher.OnRepeatReleased = _repeat.Disarm;
        _dispatcher.OnKeyPreview = _inputHooks.Preview;   // an open overlay/flyout can intercept Escape (registered via the InputHooks ambient)

        _reconciler.Anim = _anim;
        _reconciler.Images = _images;
        _reconciler.ImageEpoch = _imageEpoch;
        _images.SetPixelSink(_device.UploadImage);
        _images.SetEvictSink(_device.EvictImage);
        _images.ImageStatusChanged += (_, _, _, _) => { if (_imageEpoch.HasSubscribers) _imageEpoch.Value = _imageEpoch.Peek() + 1; WakeFrame(); };

        // Publish ambient contexts before the first render so UseContext(Viewport.Size)/FrameDiagnostics resolve.
        _lastViewportDip = ClientSizeDip();
        _viewportSig.Value = _lastViewportDip;
        _inputHooksSig = new Signal<object?>(_inputHooks);
        _reconciler.SetAmbient(Viewport.Size, _viewportSig);
        _reconciler.SetAmbient(FrameDiagnostics.Current, _frameStatsSig);
        _reconciler.SetAmbient(InputHooks.Current, _inputHooksSig);

        _window.PaintRequested = () => Paint(0);

        // Mount the root component as a reactive render-effect (initial render builds the scene).
        _reconciler.MountRoot(_root);
    }

    private void WakeFrame()
    {
        if (_inPaint) _frameAfterPaint = true;
        else _frameNeeded = true;
    }

    /// <summary>Run one full frame: pump + input, then paint (the reactive flush + layout + record happen in Paint).</summary>
    public FrameStats RunFrame()
    {
        _ring.Clear();
        _window.PumpInto(_ring);              // 1 pump
        int clicks = _dispatcher.Dispatch(_ring.Drain());  // 2 input dispatch (handlers write signals → schedule effects)

        if (!HasActiveWork)
        {
            int completed = _images.Pump();
            if (completed == 0)
            {
                LastStats = new FrameStats(0, clicks, 0, Rendered: false) { Fps = _fps, FrameMs = _frameMs };
                return LastStats;
            }
            _frameNeeded = true;
        }

        return Paint(clicks);
    }

    /// <summary>Phases 3–12: flush reactive work, (scoped) re-layout, record, submit, present, effects. No pump — safe from WndProc.</summary>
    public FrameStats Paint(int clicks = 0)
    {
        if (_inPaint) { _frameAfterPaint = true; return LastStats; }
        _inPaint = true;
        try
        {
            long frameStart = Stopwatch.GetTimestamp();
            EnsureSize();
            var layoutSize = ClientSizeDip();
            PublishViewport(layoutSize);

            // FLIP "First": capture presented rects of layout-animated nodes BEFORE the reconcile/relayout that moves them.
            // Skip on the very first layout — freshly-mounted nodes are unmeasured (0-size), so FLIPping them would animate
            // a spurious 0→full reveal that clips content. (Nodes mounted on later frames are created during Flush, AFTER
            // this capture, so they're correctly never captured.)
            bool willReconcile = _runtime.HasPending || _needFullLayout;
            bool capturedProjections = false;
            if (willReconcile && _everLaidOut && !_scene.Root.IsNull)
            {
                _projectBefore.Clear();
                CaptureProjections(_scene.Root);
                capturedProjections = _projectBefore.Count > 0;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();

            _runtime.Flush();                                  // 3–5 apply scheduled re-renders (render-effects reconcile) + bindings
            bool virtualsChanged = _reconciler.ReRealizeVirtuals();   // virtual boundary re-realize (granular)
            bool reconciled = _reconciler.ConsumeReconciled() || virtualsChanged;

            bool layoutNeeded = _needFullLayout || reconciled || _scene.AnyLayoutDirty;
            if (layoutNeeded && !_scene.Root.IsNull)
            {
                if (_needFullLayout || !_everLaidOut)
                {
                    _layout.Run(_scene.Root, layoutSize);      // 6 full layout: first frame / resize / DPI / root change
                    _needFullLayout = false;
                    _everLaidOut = true;
                }
                else
                {
                    _invalidator.RunDirty(layoutSize);         // 6 scoped relayout: only dirty subtrees, firewalled at boundaries
                }
                _scene.ClearLayoutDirty();
            }

            DrainLayoutEffects();                              // 6.5 layout effects (Bounds valid)
            if (reconciled) DumpSceneOnce(layoutSize);

            if (capturedProjections) ApplyProjections();       // FLIP "Last+Invert+Play"
            _anim.Tick(16f);                                   // 7 animation (transform/opacity/presented-size — never relayout)
            RunIncrementalLayout();                            // 7 scoped subtree relayout for SizeMode.Relayout
            ReclaimSettledOrphans();                           // 7 free settled exit orphans
            _interact.Tick(16f);                               // 7 eased hover/press
            _scrollAnim.Tick(16f);                             // 7 smooth scroll + scrollbar fade
            _repeat.Tick(16f);                                 // 7 RepeatButton auto-repeat (held → re-fire click)
            _images.Pump();                                    // 7.5 apply finished decodes + evict
            _images.Tick(16f);

            var focus = new FocusVisualStyle(Tok.FocusOuter, Tok.FocusInner, Tok.FocusThickness);
            UpdateDynamicDiagnosticsText();
            var recordStats = SceneRecorder.Record(_scene, _drawList, _images, in focus, Tok.ScrollThumb, Tok.AcrylicBase); // 8 record
            _device.SubmitDrawList(_drawList.Bytes, _drawList.SortKeys,
                new FrameInfo(_window.ClientSizePx, _window.Scale, Clear)); // 10 submit
            _swapchain.Present();                              // 11 present
            long hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

            DrainPassiveEffects();                             // 12 passive effects

            UpdateFrameTiming(frameStart);
            LastStats = new FrameStats(_drawList.CommandCount, clicks, hotAlloc, reconciled || layoutNeeded)
            {
                NodesVisited = recordStats.NodesVisited,
                DrawNodeCount = recordStats.DrawnNodeCount,
                CulledNodeCount = recordStats.CulledNodeCount,
                Fps = _fps,
                FrameMs = _frameMs,
                ComponentsRendered = _reconciler.ConsumeRenderCount(),
            };
            PublishFrameStats(LastStats);
            return LastStats;
        }
        finally
        {
            _frameNeeded = false;
            if (_frameAfterPaint) { _frameNeeded = true; _frameAfterPaint = false; }
            _inPaint = false;
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

    // FLIP "First" capture — every BoundsAnimated node's presented absolute rect, snapshotted BEFORE this commit.
    private void CaptureProjections(NodeHandle n)
    {
        if (n.IsNull) return;
        if ((_scene.Flags(n) & NodeFlags.BoundsAnimated) != 0)
            _projectBefore[n] = _scene.AbsoluteRect(n);
        for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
            CaptureProjections(c);
    }

    private void ApplyProjections()
    {
        bool reduced = Motion.ReducedMotion;
        foreach (var kv in _projectBefore)
        {
            var n = kv.Key;
            if (!_scene.IsLive(n) || (_scene.Flags(n) & NodeFlags.BoundsAnimated) == 0) continue;
            if (!_anim.TryGetTransition(n, out var spec)) continue;
            if (reduced) spec = spec with { Dynamics = TransitionDynamics.Tween(1f, Easing.Linear) };
            _anim.AnimateBounds(n, kv.Value, _scene.AbsoluteRect(n), spec);
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

    private void ReclaimSettledOrphans()
    {
        for (int i = _scene.OrphanCount - 1; i >= 0; i--)
        {
            var o = _scene.OrphanAt(i, out _, out _);
            if (!_anim.HasTracks(o)) _scene.ReclaimOrphan(o);
        }
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

    private void UpdateDynamicDiagnosticsText()
    {
        if (!_scene.HasDynamicText) return;
        _scene.UpdateDynamicText(kind => _strings.Intern(kind switch
        {
            DynamicTextKind.FrameFps => _fps <= 0.0 ? "--" : _fps.ToString("0", CultureInfo.InvariantCulture),
            DynamicTextKind.FrameCommandCount => LastStats.DrawCommandCount.ToString(CultureInfo.InvariantCulture),
            DynamicTextKind.FrameDrawCount => LastStats.DrawNodeCount.ToString(CultureInfo.InvariantCulture),
            DynamicTextKind.FrameCullCount => LastStats.CulledNodeCount.ToString(CultureInfo.InvariantCulture),
            DynamicTextKind.FrameMs => _frameMs <= 0.0 ? "--" : _frameMs.ToString("0.0", CultureInfo.InvariantCulture),
            _ => "",
        }));
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

    /// <summary>Resize the swapchain to match the window's client size; force a full re-layout on change.</summary>
    private void EnsureSize()
    {
        var s = _window.ClientSizePx;
        if (s.Width == _lastSize.Width && s.Height == _lastSize.Height) return;
        _lastSize = s;
        _swapchain.Resize(s);
        _needFullLayout = true;
    }

    private Size2 ClientSizeDip()
    {
        var s = _window.ClientSizePx;
        float scale = _window.Scale <= 0f ? 1f : _window.Scale;
        return new Size2(s.Width / scale, s.Height / scale);
    }

    public void Dispose()
    {
        _swapchain.Dispose();
        _device.Dispose();
        _window.Dispose();
    }
}
