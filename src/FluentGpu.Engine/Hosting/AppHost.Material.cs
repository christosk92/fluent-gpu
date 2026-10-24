using System.Collections.Concurrent;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Hosting;

public sealed partial class AppHost
{
    // Work the UI thread queued to run on the PRESENTING thread (the render thread, or this thread inline) immediately before the
    // first shown present of a publication newer than the one that was current when it was queued. FIFO: MinPublishSeq never
    // decreases down the queue.
    private readonly record struct PresentWork(ulong MinPublishSeq, long QueuedQpc, Action Work);
    private readonly ConcurrentQueue<PresentWork> _presentWork = new();
    private int _presentWorkCount;
    // A queued DWM write that no present has picked up for this long is run on the UI thread instead (a window that stopped
    // presenting - covered, parked - must not keep a stale backdrop forever).
    private const long PresentWorkStaleMs = 500;

    /// <summary>The window material the host last applied (<see cref="SetWindowMaterial"/> / <see cref="SeedWindowMaterial"/>).</summary>
    public WindowMaterial WindowMaterial { get; private set; } = WindowMaterial.Mica;

    /// <summary>The backend's material writer (Win32: <c>DWMWA_SYSTEMBACKDROP_TYPE</c> + dark attribute + frame extension), called with
    /// the material and dark flag. Runs on the UI thread for an immediate apply, or on the presenting thread for an ordered one.
    /// Headless leaves it null (a test installs a recorder).</summary>
    public Action<WindowMaterial, bool>? WindowMaterialSink { get; set; }

    /// <summary>Record the material the window was created with (the startup DWM write already happened, before the window was
    /// shown), without touching the ground or the sink.</summary>
    public void SeedWindowMaterial(WindowMaterial material) => WindowMaterial = material;

    /// <summary>
    /// Main window (EP9): change the window material at runtime. The ground follows the material in the same publication - a
    /// composited window with a backdrop clears transparent (and keeps the inactive-window solid fallback), a window without one
    /// clears to the theme's opaque ground (<c>Tok.ClearWindowBackground</c>) - and a full repaint is requested.
    /// <para><paramref name="ordered"/> (default): the backend write is queued to the presenting thread
    /// (<see cref="QueueBeforePresent"/>), so DWM changes the backdrop immediately before the present that first shows the new
    /// ground: never a frame before, never a frame after. Not ordered: written now, on the UI thread (the live theme path's order).</para></summary>
    public void SetWindowMaterial(WindowMaterial material, bool dark, bool ordered = true)
    {
        if (_isDetachedChild) throw new InvalidOperationException("SetWindowMaterial is for the main window.");
        ApplyMaterialGround(material);
        var sink = WindowMaterialSink;
        if (sink is not null)
        {
            if (ordered) QueueBeforePresent(() => sink(material, dark));
            else sink(material, dark);
        }
    }

    /// <summary>The host half of a material change: the recorded material and the ground the next publication clears to (a
    /// composited window with a backdrop clears transparent, keeping the inactive solid fallback; a window without one clears to
    /// the theme's opaque ground), with a full repaint so it is published. No DWM write: the caller orders it (the theme
    /// transaction writes it at the edge of its dissolve the DWM rules ask for).</summary>
    private void ApplyMaterialGround(WindowMaterial material)
    {
        WindowMaterial = material;
        bool transparent = _window.Composited && material != WindowMaterial.None;
        _micaWindow = transparent;
        if (transparent) Theme.WindowBackground = _window.IsActive ? ColorF.Transparent : Tok.T.WindowBackground;
        else Tok.ClearWindowBackground();
        RequestFullRepaintOnce();   // the ground changed with the material: publish it (also covers a hidden window and a missing sink)
    }

    /// <summary>Write <paramref name="material"/> and the dark flag to DWM immediately before the next shown present (the sink,
    /// queued to the presenting thread), or, without a sink, through the live path's writer (<see cref="OnApplyThemeMaterial"/>,
    /// which reads the backend's current material) on this thread.</summary>
    private void WriteMaterialOrdered(WindowMaterial material, bool dark)
    {
        if (WindowMaterialSink is { } sink) QueueBeforePresent(() => sink(material, dark));
        else OnApplyThemeMaterial?.Invoke(dark);
    }

    /// <summary>
    /// Main window, any thread: run <paramref name="work"/> on the presenting thread immediately before the first shown present whose
    /// publication is NEWER than the one current now, and request a full repaint so one is guaranteed. A held present is not a shown
    /// one. The window material's DWM writes use it (a DWM attribute applied here lands in the same DWM composition as the pixels
    /// the present carries); a backend may flush DWM inside <paramref name="work"/>. When the window is not visible there is no
    /// present to order against and the work runs at once; a queued item no present picks up within 500 ms runs on the UI thread.
    /// </summary>
    public void QueueBeforePresent(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_isDetachedChild) throw new InvalidOperationException("QueueBeforePresent is for the main window.");
        if (!_window.IsVisible && !_revealPending) { work(); return; }
        _presentWork.Enqueue(new PresentWork(PublishSequence + 1, Stopwatch.GetTimestamp(), work));
        Interlocked.Increment(ref _presentWorkCount);
        RequestFullRepaintOnce();
    }

    /// <summary>Presenting thread: run every queued work item due for the present that carries <paramref name="publishSeq"/>.</summary>
    private void RunBeforePresentWork(ulong publishSeq)
    {
        if (Volatile.Read(ref _presentWorkCount) == 0) return;
        while (_presentWork.TryPeek(out var next) && next.MinPublishSeq <= publishSeq && _presentWork.TryDequeue(out var w))
        {
            Interlocked.Decrement(ref _presentWorkCount);
            w.Work();
        }
    }

    /// <summary>UI thread, frame start: run work no present has picked up for too long.</summary>
    private void DrainStalePresentWork()
    {
        long now = Stopwatch.GetTimestamp();
        while (_presentWork.TryPeek(out var next) && (now - next.QueuedQpc) * 1000 / Stopwatch.Frequency >= PresentWorkStaleMs
               && _presentWork.TryDequeue(out var w))
        {
            Interlocked.Decrement(ref _presentWorkCount);
            w.Work();
        }
    }
}
