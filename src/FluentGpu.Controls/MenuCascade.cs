using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// The cascade of ONE menu level: which of its rows has its sub-menu open, and the hover intent that opens, keeps and
/// closes it. Every surface that cascades owns one (a <see cref="MenuFlyoutPresenter"/> level, a
/// <see cref="CommandBarFlyout"/>'s overflow rows), so a menu behaves the same wherever it is drawn.
///
/// <para><b>WinUI CascadingMenuHelper.</b> Hovering a sub-menu row opens its cascade after MenuShowDelay (400 ms,
/// <see cref="MenuFlyout.SubMenuShowDelayMs"/>); leaving that row cancels the pending open. Leaving the row of the OPEN
/// cascade, or hovering a sibling, arms a delay-close of the same length; the pointer reaching the cascade (any depth of
/// it) or coming back to its row disarms it. A click (mouse, touch or pen tap) and Right open at once; Left closes one
/// level.</para>
///
/// <para><b>Submenu aim (the "safe triangle": macOS, Chrome, GTK, Radix).</b> WinUI stops there, so a pointer that
/// travels diagonally from a row to its cascade crosses sibling rows and loses the menu whenever the trip outlasts the
/// delay or a sibling's own open timer wins. Here a move is AIMED when it heads into the open cascade: it lies in the
/// triangle from the point where it left the row (or from the pointer a few samples ago) to the cascade's near edge. An
/// aimed move over a sibling neither switches nor closes: it restarts both timers, so the cascade survives any number of
/// crossings while the pointer keeps moving toward it, and a pointer that STOPS over a sibling still gets that sibling
/// (or the close) one delay later, exactly as WinUI would. Moves over this level's gaps, padding, separators and headers
/// count too (the presenter feeds them through <see cref="Sample"/>), and a close that comes due while the pointer is
/// outside every menu (between a left-flipped cascade and its parent, between two popup windows) re-checks the pointer
/// first: inside the cascade chain it is cancelled, still moving inside the aim triangle it waits one more delay.</para>
///
/// <para>Timers ride the host timer queue directly (no component per countdown, no re-render per arm): a hover move
/// allocates nothing. UI thread only.</para>
/// </summary>
internal sealed class MenuCascade
{
    /// <summary>How far past the cascade's top and bottom corners the aim triangle reaches (DIP): a pointer aimed at the
    /// first or last row's edge still counts.</summary>
    const float AimSlop = 6f;
    /// <summary>The cascade's hit band past its presenter rect (DIP): the plate's 2px presenter padding plus the 4px
    /// overlap onto the owner row (CascadingMenuHelper m_subMenuOverlapPixels) belong to the cascade.</summary>
    const float ChainSlop = 4f;
    const int Samples = 4;

    // ── wiring (set once by the owning surface) ──────────────────────────────────────────────────────────────────
    public IOverlayService? Overlay;
    public InputHooks? Hooks;
    public HostTimerQueue? Timers;
    public Func<SceneStore?> Scene = static () => null;
    /// <summary>The level that opened this one (null at the root menu).</summary>
    public MenuCascade? Parent;
    /// <summary>Row <c>i</c>'s live node (the cascade's anchor and the row-rect probes).</summary>
    public Func<int, NodeHandle> RowNode = static _ => default;
    /// <summary>Row <c>i</c>'s sub-menu items when it is an ENABLED cascade row, else null.</summary>
    public Func<int, IReadOnlyList<MenuFlyoutItem>?> CascadeOf = static _ => null;
    /// <summary>Invoking a row anywhere in the chain closes the whole menu.</summary>
    public Action CloseAll = static () => { };
    /// <summary>This level's own surface (its presenter root): the rect the "pointer left the whole menu" test reads.</summary>
    public Func<NodeHandle> SelfNode = static () => default;
    /// <summary>Part modifiers and row size every cascaded level inherits.</summary>
    public TemplateParts? Parts;
    public ControlSize Size = ControlSize.Medium;

    /// <summary>The row whose cascade is open (-1: none) — the SubMenuOpened fill reads it.</summary>
    public readonly Signal<int> OpenIndex = new(-1);

    OverlayHandle? _child;
    NodeHandle _childRoot;
    MenuCascade? _childCascade;
    int _pendingOpen = -1;
    bool _closeArmed, _leaveArmed;
    long _openGen, _closeGen, _leaveGen;
    readonly Action<long> _openFire, _closeFire, _leaveFire;

    Point2 _exitApex;                                  // where the pointer was when it last stood on the open row
    readonly Point2[] _ring = new Point2[Samples];     // the last pointer samples on this level (aim apex = the oldest)
    int _ringCount, _ringHead;
    Point2 _lastCheck;                                 // the pointer when the close was last (re)armed by aim
    Point2 _leaveCheck;                                // the pointer when the leave was last (re)armed by aim
    bool _disposed;

    public MenuCascade()
    {
        _openFire = OnOpenDue;
        _closeFire = OnCloseDue;
        _leaveFire = OnLeaveDue;
    }

    float DelayMs => MenuFlyout.SubMenuShowDelayMs;
    Point2? Pointer => Hooks?.GetPointerPosition?.Invoke();

    // ── opening and closing ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open row <paramref name="i"/>'s cascade now (a click, a tap, Right, or the hover delay). Anchored to the
    /// ROW node, so the overlay host nests it under this level's popup (z-order above it, closed with it). Right of the
    /// row, top edges aligned, overlapping it by 4px (CascadingMenuHelper.h:121, cpp:678); the positioner flips it left
    /// when there is no room. Windowed (ConstrainToRootBounds=false) like every WinUI menu.</summary>
    public void Open(int i, bool focusFirst)
    {
        if (_disposed || Overlay is not { } svc || CascadeOf(i) is not { Count: > 0 } items) return;
        if (OpenIndex.Peek() == i && _child is { IsOpen: true }) return;
        CloseChild();
        if (RowNode(i).IsNull) return;
        if (Pointer is { } p) _exitApex = p;
        int row = i;
        var parent = this;
        var handle = svc.Open(
            () => parent.RowNode(row),
            () => Embed.Comp(() => new MenuFlyoutPresenter
            {
                Items = items,
                Close = parent.CloseAll,          // invoking a sub item closes the WHOLE chain
                MinWidth = MenuFlyout.ThemeMinWidth,
                ParentCascade = parent,
                FocusFirstOnMount = focusFirst,   // keyboard open: cursor + focus on the first sub item
                Parts = parent.Parts,
                Size = parent.Size,
            }),
            FlyoutPlacement.RightEdgeAlignedTop,
            new PopupOptions(Chrome: PopupChrome.Flyout) { ConstrainToRootBounds = false, AnchorOffsetX = -4f });
        handle.ClosedAction = () =>
        {
            if (!ReferenceEquals(_child, handle)) return;
            _child = null;
            _childRoot = default;
            _childCascade = null;
            OpenIndex.Value = -1;
        };
        _child = handle;
        OpenIndex.Value = i;
    }

    /// <summary>Close this level's open cascade (and so every level under it) and drop any pending open.</summary>
    public void CloseChild()
    {
        CancelOpen();
        CloseOpenChild();
    }

    // Close the open cascade but keep a pending open: the delay-close of the old cascade and the delay-open of the
    // sibling the pointer dwells on come due together, and the sibling must still open.
    void CloseOpenChild()
    {
        CancelClose();
        var h = _child;
        _child = null;
        _childRoot = default;
        _childCascade = null;
        if (h is { IsOpen: true }) h.Close();
        if (OpenIndex.Peek() != -1) OpenIndex.Value = -1;
    }

    /// <summary>The cascade this level opened has mounted: its presenter root (the rect the aim and chain tests read).</summary>
    internal void ChildMounted(MenuCascade child, NodeHandle root)
    {
        if (_child is null) return;
        _childCascade = child;
        _childRoot = root;
    }

    /// <summary>The owning surface unmounted: drop the timers (the overlay host closes the nested popups itself).</summary>
    public void Dispose()
    {
        _disposed = true;
        CancelOpen();
        CancelClose();
        CancelLeave();
    }

    // ── pointer input ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pointer moved over row <paramref name="i"/> (its OnHoverMove).</summary>
    public void RowHover(int i)
    {
        if (_disposed || Pointer is not { } p) return;
        InsideFromChild();
        int open = OpenIndex.Peek();
        if (i == open)
        {
            // Home: keep the cascade, and this is the newest launch point for the aim triangle.
            CancelClose();
            CancelOpen();
            _exitApex = p;
            Push(p);
            return;
        }
        bool cascades = CascadeOf(i) is { Count: > 0 };
        if (open >= 0 && Aimed(p))
        {
            // Heading into the open cascade: postpone both the switch and the close while the pointer keeps moving.
            if (cascades) ArmOpen(i); else CancelOpen();
            if (_closeArmed) { ArmClose(); _lastCheck = p; }
            Push(p);
            return;
        }
        if (cascades) { if (_pendingOpen != i) ArmOpen(i); }
        else CancelOpen();
        if (open >= 0 && !_closeArmed) { ArmClose(); _lastCheck = p; }
        Push(p);
    }

    /// <summary>The pointer left row <paramref name="i"/> (its OnPointerExit).</summary>
    public void RowExit(int i)
    {
        if (_disposed) return;
        LevelExit();
        if (_pendingOpen == i) CancelOpen();
        if (i != OpenIndex.Peek() || _closeArmed) return;
        var p = Pointer;
        if (p is { } q && ChainContains(q)) return;   // straight into the cascade (the 4px overlap, its padding)
        ArmClose();
        _lastCheck = p ?? default;
    }

    /// <summary>The pointer moved anywhere over this level (its rows, gaps, padding, separators, headers): the
    /// presenter's OnPointerMoveWithin. A move a row already handled is a no-op.</summary>
    public void Sample()
    {
        if (_disposed || Pointer is not { } p) return;
        if (_ringCount > 0 && _ring[(_ringHead + Samples - 1) % Samples] == p) return;
        InsideFromChild();
        if (OpenIndex.Peek() >= 0 && _closeArmed && Aimed(p)) { ArmClose(); _lastCheck = p; }
        Push(p);
    }

    /// <summary>The pointer left a row or the gaps of this level (their OnPointerExit): from now until it reaches a row
    /// again, the root level watches whether it is still over the menu. Once it stands outside every level, the open
    /// cascades close one delay later (WinUI CascadingMenuHelper's delay-close when the pointer leaves the sub-menu); the
    /// root menu itself stays, it light-dismisses on a press. A pointer that comes back into any row before then, or keeps
    /// travelling on aim toward an open cascade, keeps them.</summary>
    public void LevelExit()
    {
        if (_disposed) return;
        var top = this;
        while (top.Parent is { } up) top = up;
        if (top.OpenIndex.Peek() < 0 || top._leaveArmed) return;
        top.ArmLeave();
        top._leaveCheck = Pointer ?? default;
    }

    // The pointer is inside this level, i.e. inside the cascade of every ancestor: none of them may close or switch.
    void InsideFromChild()
    {
        for (var a = Parent; a is not null; a = a.Parent)
        {
            a.CancelClose();
            a.CancelOpen();
            a.CancelLeave();
        }
        CancelLeave();
    }

    // ── geometry ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is <paramref name="p"/> inside the open cascade or any cascade under it?</summary>
    bool ChainContains(Point2 p)
    {
        var scene = Scene();
        if (scene is null) return false;
        for (var c = this; c is not null; c = c._childCascade)
        {
            var root = c._childRoot;
            if (root.IsNull || !scene.IsLive(root)) return false;
            var r = scene.AbsoluteRect(root);
            if (p.X >= r.X - ChainSlop && p.X < r.Right + ChainSlop && p.Y >= r.Y - ChainSlop && p.Y < r.Bottom + ChainSlop) return true;
        }
        return false;
    }

    /// <summary>Is <paramref name="p"/> over this level's surface or any open cascade under it?</summary>
    bool InsideMenu(Point2 p)
    {
        var scene = Scene();
        var self = SelfNode();
        if (scene is not null && !self.IsNull && scene.IsLive(self))
        {
            var r = scene.AbsoluteRect(self);
            if (p.X >= r.X - ChainSlop && p.X < r.Right + ChainSlop && p.Y >= r.Y - ChainSlop && p.Y < r.Bottom + ChainSlop) return true;
        }
        return ChainContains(p);
    }

    bool OverRow(int i, Point2 p)
    {
        var scene = Scene();
        var n = i >= 0 ? RowNode(i) : default;
        return scene is not null && !n.IsNull && scene.IsLive(n) && scene.AbsoluteRect(n).Contains(p);
    }

    /// <summary>Is a pointer at <paramref name="p"/> heading into the open cascade? Inside the triangle from the exit
    /// point to the cascade's near edge, or inside the one from the pointer a few samples ago (so a path that bends
    /// toward the cascade after leaving the row counts too).</summary>
    bool Aimed(Point2 p)
    {
        var scene = Scene();
        var root = _childRoot;
        if (scene is null || root.IsNull || !scene.IsLive(root)) return false;
        var r = scene.AbsoluteRect(root);
        if (r.W <= 0f || r.H <= 0f) return false;
        var row = RowNode(OpenIndex.Peek());
        bool toRight = true;
        if (!row.IsNull && scene.IsLive(row))
        {
            var rr = scene.AbsoluteRect(row);
            toRight = r.X + r.W * 0.5f >= rr.X + rr.W * 0.5f;
        }
        float nearX = toRight ? r.X : r.Right;
        var top = new Point2(nearX, r.Y - AimSlop);
        var bottom = new Point2(nearX, r.Bottom + AimSlop);
        if (InTriangle(_exitApex, top, bottom, p)) return true;
        if (_ringCount == 0) return false;
        var oldest = _ring[(_ringHead + Samples - _ringCount) % Samples];
        return oldest != p && InTriangle(oldest, top, bottom, p);
    }

    /// <summary>True when <paramref name="p"/> lies inside (or on) triangle <paramref name="a"/>/<paramref name="b"/>/<paramref name="c"/>.</summary>
    internal static bool InTriangle(Point2 a, Point2 b, Point2 c, Point2 p)
    {
        float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }

    static float Cross(Point2 p, Point2 a, Point2 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

    void Push(Point2 p)
    {
        _ring[_ringHead] = p;
        _ringHead = (_ringHead + 1) % Samples;
        if (_ringCount < Samples) _ringCount++;
    }

    // ── timers ───────────────────────────────────────────────────────────────────────────────────────────────────

    void ArmOpen(int i)
    {
        CancelOpen();
        if (Timers is not { } q) return;
        _pendingOpen = i;
        q.Schedule(q.NowMs + DelayMs, ++_openGen, _openFire, typeof(MenuCascade));
    }

    void CancelOpen()
    {
        if (_pendingOpen < 0) return;
        _pendingOpen = -1;
        _openGen++;
        Timers?.Cancel(_openFire);
    }

    void ArmClose()
    {
        CancelClose();
        if (Timers is not { } q) return;
        _closeArmed = true;
        q.Schedule(q.NowMs + DelayMs, ++_closeGen, _closeFire, typeof(MenuCascade));
    }

    void CancelClose()
    {
        if (!_closeArmed) return;
        _closeArmed = false;
        _closeGen++;
        Timers?.Cancel(_closeFire);
    }

    void ArmLeave()
    {
        CancelLeave();
        if (Timers is not { } q) return;
        _leaveArmed = true;
        q.Schedule(q.NowMs + DelayMs, ++_leaveGen, _leaveFire, typeof(MenuCascade));
    }

    void CancelLeave()
    {
        if (!_leaveArmed) return;
        _leaveArmed = false;
        _leaveGen++;
        Timers?.Cancel(_leaveFire);
    }

    // The root level's leave check: still outside every level → the cascades close (the root stays open).
    void OnLeaveDue(long gen)
    {
        if (gen != _leaveGen || _disposed) return;
        _leaveArmed = false;
        if (Pointer is { } p)
        {
            // Still over the menu but on no row (a level's padding, a gap): keep watching. No event reports the pointer
            // leaving a popup through its padding, and a row it reaches cancels this watch anyway.
            if (InsideMenu(p)) { ArmLeave(); return; }
            // Travelling between two levels outside both (a left-flipped cascade, two popup windows apart): wait while
            // some level's open cascade is the target of a pointer that is still moving.
            if (p != _leaveCheck)
                for (var c = this; c is not null; c = c._childCascade)
                    if (c.OpenIndex.Peek() >= 0 && c.Aimed(p)) { _leaveCheck = p; ArmLeave(); return; }
        }
        CloseChild();
    }

    void OnOpenDue(long gen)
    {
        if (gen != _openGen || _disposed) return;
        int i = _pendingOpen;
        _pendingOpen = -1;
        if (i >= 0) Open(i, focusFirst: false);
    }

    void OnCloseDue(long gen)
    {
        if (gen != _closeGen || _disposed) return;
        _closeArmed = false;
        if (Pointer is { } p)
        {
            if (ChainContains(p) || OverRow(OpenIndex.Peek(), p)) return;
            // Still travelling toward the cascade outside every menu (no move reached any level since the last check):
            // one more delay, re-checked then. A pointer that stopped closes it now.
            if (p != _lastCheck && Aimed(p)) { _lastCheck = p; ArmClose(); return; }
        }
        CloseOpenChild();
    }
}
