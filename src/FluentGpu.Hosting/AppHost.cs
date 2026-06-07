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
using FluentGpu.Text;

namespace FluentGpu.Hosting;

public readonly record struct FrameStats(int DrawCommandCount, int ClicksHandled, long HotPhaseAllocBytes, bool Rendered);

/// <summary>
/// Composition root + the single-UI-thread frame loop (the 13 phases, slice subset). Drives one root Component:
/// pump → input → hook-flush → render → reconcile → layout → record → submit → present → effects.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly IPlatformWindow _window;
    private readonly IGpuDevice _device;
    private readonly ISwapchain _swapchain;
    private readonly Component _root;
    private readonly StringTable _strings;

    private readonly SceneStore _scene = new();
    private readonly TreeReconciler _reconciler;
    private readonly FlexLayout _layout;
    private readonly DrawList _drawList = new();
    private readonly InputDispatcher _dispatcher;
    private readonly InputEventRing _ring = new();
    private readonly AnimEngine _anim;
    private readonly InteractionAnimator _interact;
    private readonly ScrollAnimator _scrollAnim;
    private readonly ImageCache _images;

    private Element? _oldRoot;
    private bool _dirty = true;
    private bool _inPaint;
    private Size2 _lastSize;
    private static ColorF Clear => Theme.WindowBackground;   // theme-driven (transparent later for Mica)

    public SceneStore Scene => _scene;
    public AnimEngine Animation => _anim;
    public FrameStats LastStats { get; private set; }
    public bool HasActiveWork => _dirty || _anim.HasActive || _interact.HasActive || _scrollAnim.HasActive || _images.PendingCount > 0;

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
        _reconciler = new TreeReconciler(_scene, strings);
        _layout = new FlexLayout(_scene, fonts);
        _dispatcher = new InputDispatcher(_scene);
        _anim = new AnimEngine(_scene);
        _interact = new InteractionAnimator(_scene);
        _scrollAnim = new ScrollAnimator(_scene);
        _lastSize = window.ClientSizePx;
        _root.Context.RequestRerender = () => _dirty = true;
        _reconciler.RequestRerender = () => _dirty = true;   // a nested component's setState requests the next frame
        _dispatcher.RequestRerender = () => _dirty = true;   // a virtual list crossing an item boundary on scroll
        _dispatcher.OnHoverChanged = _interact.SetHover;     // ease the brush transition on pointer enter/leave
        _dispatcher.OnPressChanged = _interact.SetPress;
        _dispatcher.OnScrollArmed = _scrollAnim.Arm;         // smooth scroll: arm the viewport for easing
        _dispatcher.OnScrollHover = _scrollAnim.Hover;       // reveal/collapse the WinUI-style scrollbar indicator
        _dispatcher.OnScrollLeave = _scrollAnim.Leave;
        _scrollAnim.RequestRerender = () => _dirty = true;   // re-realize the virtual window on a boundary crossing
        _reconciler.Anim = _anim;          // animation hooks in nested components seed tracks on their nodes
        _reconciler.Images = _images;      // image nodes request decodes + pin residency through the cache
        _root.Context.Anim = _anim;
        // Keep the window live during the OS modal move/size loop (which otherwise blocks RunFrame until mouse-up).
        _window.PaintRequested = () => Paint(0);
    }

    /// <summary>Run one full frame: pump + input + hook-flush, then paint.</summary>
    public FrameStats RunFrame()
    {
        _ring.Clear();
        _window.PumpInto(_ring);              // 1 pump
        int clicks = _dispatcher.Dispatch(_ring.Drain());  // 2 input dispatch

        bool changed = _root.Context.FlushPending();        // 3 hook-state flush (root + all nested components)
        foreach (var c in _reconciler.LiveComponents) changed |= c.Context.FlushPending();
        if (changed) _dirty = true;

        if (!HasActiveWork)
        {
            int completed = _images.Pump();
            if (completed == 0)
            {
                LastStats = new FrameStats(0, clicks, 0, Rendered: false);
                return LastStats;
            }

            _dirty = true;
        }

        return Paint(clicks);
    }

    /// <summary>Phases 6–12 (+ resize): re-layout if dirty, record, submit, present, effects. No pump — safe to call from WndProc.</summary>
    public FrameStats Paint(int clicks = 0)
    {
        if (_inPaint) return LastStats;       // re-entrancy guard (WM_SIZE during the pump)
        _inPaint = true;
        try
        {
            EnsureSize();
            var layoutSize = ClientSizeDip();

            bool rendered = false;
            if (_dirty)
            {
                // Publish the client size as ambient context (responsive layout / NavigationView display modes) for the
                // whole render+reconcile, without adding a tree node (so scene.Root stays the app's own root).
                ContextStack.Push(FluentGpu.Hooks.Viewport.Size, layoutSize);
                try
                {
                    var newRoot = _root.RenderWithHooks();      // 4 render
                    _reconciler.ReconcileRoot(newRoot, _oldRoot); // 5 reconcile
                    _oldRoot = newRoot;
                    _root.Context.HostNode = _scene.Root;         // root component animates itself via the scene root
                }
                finally { ContextStack.Pop(); }
                _dirty = false;
                rendered = true;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            if (rendered) _layout.Run(_scene.Root, layoutSize);             // 6 layout (root fills the window in DIPs)

            DrainLayoutEffects();                             // 6.5 layout effects (root + nested; Bounds valid)

            if (rendered) DumpSceneOnce(layoutSize);          // DIAG: one-shot post-layout scene tree dump

            _anim.Tick(16f);                                  // 7 animation (writes Opacity/transform; never LayoutDirty)
            _interact.Tick(16f);                              // 7 eased hover/press brush transitions
            _scrollAnim.Tick(16f);                            // 7 smooth scroll + scrollbar fade
            _images.Pump();                                   // 7.5 apply finished decodes (+1-frame latency) + evict

            var focus = new FocusVisualStyle(Tok.FocusOuter, Tok.FocusInner, Tok.FocusThickness);
            SceneRecorder.Record(_scene, _drawList, _images, in focus, Tok.ScrollThumb, Tok.AcrylicBase); // 8 record
            _device.SubmitDrawList(_drawList.Bytes, _drawList.SortKeys,
                new FrameInfo(_window.ClientSizePx, _window.Scale, Clear)); // 10 submit
            _swapchain.Present();                             // 11 present
            long hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

            DrainPassiveEffects();                            // 12 passive effects (root + nested)

            LastStats = new FrameStats(_drawList.CommandCount, clicks, hotAlloc, rendered);
            return LastStats;
        }
        finally { _inPaint = false; }
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

    // ── DIAG: dump the retained scene tree with post-layout bounds, so we can see WHERE a node went (missing pane etc.).
    // One-shot by default; set FG_DUMP=all to dump every rendered frame.
    private bool _dumped;
    private void DumpSceneOnce(Size2 layoutSize)
    {
        bool all = Environment.GetEnvironmentVariable("FG_DUMP") == "all";
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

    /// <summary>Resize the swapchain to match the window's client size; force a re-layout on change.</summary>
    private void EnsureSize()
    {
        var s = _window.ClientSizePx;
        if (s.Width == _lastSize.Width && s.Height == _lastSize.Height) return;
        _lastSize = s;
        _swapchain.Resize(s);
        _dirty = true;
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
