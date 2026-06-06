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

    private readonly SceneStore _scene = new();
    private readonly TreeReconciler _reconciler;
    private readonly FlexLayout _layout;
    private readonly DrawList _drawList = new();
    private readonly InputDispatcher _dispatcher;
    private readonly InputEventRing _ring = new();

    private Element? _oldRoot;
    private bool _dirty = true;
    private readonly ColorF _clear = ColorF.FromRgba(0x1E, 0x1E, 0x1E);

    public SceneStore Scene => _scene;
    public FrameStats LastStats { get; private set; }

    public AppHost(IPlatformApp app, IPlatformWindow window, IGpuDevice device, IFontSystem fonts,
                   StringTable strings, Component root)
    {
        _window = window;
        _device = device;
        _root = root;
        _swapchain = device.CreateSwapchain(new SwapchainDesc(window.Handle, window.ClientSizePx));
        _reconciler = new TreeReconciler(_scene, strings);
        _layout = new FlexLayout(_scene, fonts);
        _dispatcher = new InputDispatcher(_scene);
        _root.Context.RequestRerender = () => _dirty = true;
    }

    /// <summary>Run exactly one frame. Returns the per-frame stats (incl. allocations across the paint half).</summary>
    public FrameStats RunFrame()
    {
        // 1 pump
        _ring.Clear();
        _window.PumpInto(_ring);
        var events = _ring.Drain();

        // 2 input dispatch (handlers' setState marks dirty via RequestRerender)
        int clicks = _dispatcher.Dispatch(events);

        // 3 hook-state flush
        if (_root.Context.FlushPending()) _dirty = true;

        bool rendered = false;
        if (_dirty)
        {
            // 4 render
            var newRoot = _root.RenderWithHooks();
            // 5 reconcile
            _reconciler.ReconcileRoot(newRoot, _oldRoot);
            _oldRoot = newRoot;
            _dirty = false;
            rendered = true;
        }

        // ── paint half (phases 6–11): target 0 managed allocations when nothing changed ──
        long before = GC.GetAllocatedBytesForCurrentThread();

        if (rendered) _layout.Run(_scene.Root);                 // 6 layout (dirty-scoped in the slice = whole tree)
        SceneRecorder.Record(_scene, _drawList);                 // 8 record
        _device.SubmitDrawList(_drawList.Bytes, _drawList.SortKeys,
            new FrameInfo(_window.ClientSizePx, _window.Scale, _clear)); // 10 submit
        _swapchain.Present();                                    // 11 present

        long hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

        // 12 passive effects (after present)
        var effects = _root.Context.PendingEffects;
        if (effects.Count > 0)
        {
            foreach (var e in effects) e();
            effects.Clear();
        }

        LastStats = new FrameStats(_drawList.CommandCount, clicks, hotAlloc, rendered);
        return LastStats;
    }

    public void Dispose()
    {
        _swapchain.Dispose();
        _device.Dispose();
        _window.Dispose();
    }
}
