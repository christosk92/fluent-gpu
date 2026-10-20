namespace FluentGpu.Foundation;

/// <summary>Declarative window-drag role of a box (<c>BoxEl.WindowDrag</c>, sugar <c>el.WindowDragArea()</c> /
/// <c>el.NoWindowDrag()</c>). The host turns every reachable box with a role into a non-client region each frame
/// (phase 7.8) and pushes the merged list to the window only when it changed — no handle capture, no layout effect,
/// no stale rect after a scroll or a self-resizing island.
/// <para><see cref="Drag"/>: the box's VISIBLE rect (every ancestor clip and the scroll/transform pose folded in) is a
/// caption band — pressing it starts the OS move loop (snap, double-click maximize when resizable). A right-click on it
/// raises the nearest <c>OnContextRequested</c> under the point; with no handler the system menu opens.
/// <see cref="NoDrag"/>: an interactive island inside a drag area stays client (buttons, text boxes) — islands always
/// beat drag bands. Needs a <c>WindowDesc.CustomFrame</c> window; a standard frame ignores the regions.</para></summary>
public enum WindowDragRole : byte
{
    /// <summary>No role (the default): the box neither drags nor carves an island.</summary>
    None = 0,
    /// <summary>The box's visible rect moves the window (HTCAPTION).</summary>
    Drag = 1,
    /// <summary>The box's visible rect stays client inside an enclosing drag area.</summary>
    NoDrag = 2,
}

/// <summary>One collected window-drag entry (<c>SceneStore.CollectWindowDragRects</c>): the declaring node, its role and
/// its clip-aware visible rect in window DIP (never empty).</summary>
public readonly record struct WindowDragRect(NodeHandle Node, WindowDragRole Role, RectF RectDip);
