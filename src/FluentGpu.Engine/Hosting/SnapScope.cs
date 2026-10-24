using System;

namespace FluentGpu.Hosting;

/// <summary>
/// Transaction snap scope (EP17, E17a/b): while one is active on an <see cref="AppHost"/> the UI changes WITHOUT motion, so a
/// whole-app change (a theme transaction) is published as one coherent frame instead of a cross-fade of hybrids.
/// <list type="bullet">
/// <item>Element brush transitions (<c>BrushTransitionMs</c>), bound-channel transitions and every slab channel started by
/// <c>Transition</c> / <c>Enter</c> / <c>Exit</c> / <c>Stagger</c> / <c>Layout</c> (and the <c>UseTransition</c>-style hooks) land at
/// their END value in the publication that carries the commit: no old-to-new fade, no spring, and no animation row left behind
/// (<c>AnimEngine.HasTracks</c> is false at once). An Exit frees the node immediately (no orphan). LayoutTransition FLIPs,
/// FlowReveal and reveal bands snap to the laid-out geometry. Connected (shared-element) flights are skipped.</item>
/// <item>An image that decodes inside the scope reveals instantly (<c>ImageCache.BeginReveal</c>), and an image swap is a hard cut.</item>
/// <item><c>FrameClock.Tick</c> / <c>PaceableTick</c> do not advance, and the host timer queue is held: <c>FrameTicker</c> and
/// <c>UseInterval</c> / <c>UseTimeout</c> / debounce sources schedule nothing. On exit every armed timer is slid by the time it
/// was held, so intervals resume a full period after the exit and never burst-fire the periods they missed.</item>
/// </list>
/// Only a transaction enters a scope; ordinary navigation, live options and light/dark never do, and with no scope active
/// every code path here is the existing one (a depth check, nothing more).
/// <para>The scope must be open while the frame(s) that publish the change run: seeds and FLIP capture happen at reconcile
/// time, which is the host's frame. Tracks that were already running when the scope opened keep running (only seeds made
/// inside the scope land at their end). UI thread only; nests (a depth counter on the host, the outermost exit restores).
/// Dispose exactly once per <see cref="Enter"/>. Entering and leaving allocate nothing.</para>
/// </summary>
public readonly struct SnapScope : IDisposable
{
    private readonly AppHost? _host;

    private SnapScope(AppHost host) => _host = host;

    /// <summary>Open a snap scope on <paramref name="host"/>; dispose the result to close it (use <c>using</c>).</summary>
    public static SnapScope Enter(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.PushSnap();
        return new SnapScope(host);
    }

    /// <summary>Close the scope. The outermost close restores the animation engine, the image reveals, the frame clock and the
    /// timer queue. A <c>default</c> scope is a no-op.</summary>
    public void Dispose() => _host?.PopSnap();
}

public sealed partial class AppHost
{
    private int _snapDepth;   // SnapScope nesting depth (UI thread)

    /// <summary>True while at least one <see cref="SnapScope"/> is open on this host.</summary>
    public bool IsSnapping => _snapDepth > 0;

    internal void PushSnap()
    {
        if (_snapDepth++ != 0) return;
        _anim.Snapping = true;
        _images.PushSnap();
        _timers.Hold();
    }

    internal void PopSnap()
    {
        if (_snapDepth == 0) return;
        if (--_snapDepth != 0) return;
        _anim.Snapping = false;
        _images.PopSnap();
        _timers.Release();
    }
}
