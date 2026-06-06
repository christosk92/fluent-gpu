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
    private bool _inPaint;
    private Size2 _lastSize;
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
        _lastSize = window.ClientSizePx;
        _root.Context.RequestRerender = () => _dirty = true;
        // Keep the window live during the OS modal move/size loop (which otherwise blocks RunFrame until mouse-up).
        _window.PaintRequested = () => Paint(0);
    }

    /// <summary>Run one full frame: pump + input + hook-flush, then paint.</summary>
    public FrameStats RunFrame()
    {
        _ring.Clear();
        _window.PumpInto(_ring);              // 1 pump
        int clicks = _dispatcher.Dispatch(_ring.Drain());  // 2 input dispatch
        if (_root.Context.FlushPending()) _dirty = true;    // 3 hook-state flush
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

            bool rendered = false;
            if (_dirty)
            {
                var newRoot = _root.RenderWithHooks();      // 4 render
                _reconciler.ReconcileRoot(newRoot, _oldRoot); // 5 reconcile
                _oldRoot = newRoot;
                _dirty = false;
                rendered = true;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            if (rendered) _layout.Run(_scene.Root);          // 6 layout
            SceneRecorder.Record(_scene, _drawList);          // 8 record
            _device.SubmitDrawList(_drawList.Bytes, _drawList.SortKeys,
                new FrameInfo(_window.ClientSizePx, _window.Scale, _clear)); // 10 submit
            _swapchain.Present();                             // 11 present
            long hotAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

            var effects = _root.Context.PendingEffects;       // 12 passive effects
            if (effects.Count > 0) { foreach (var e in effects) e(); effects.Clear(); }

            LastStats = new FrameStats(_drawList.CommandCount, clicks, hotAlloc, rendered);
            return LastStats;
        }
        finally { _inPaint = false; }
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

    public void Dispose()
    {
        _swapchain.Dispose();
        _device.Dispose();
        _window.Dispose();
    }
}
