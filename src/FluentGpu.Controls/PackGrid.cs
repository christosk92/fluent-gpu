using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>What a realized item knows about its box. Written when a pack changes it (an edge), never per frame.</summary>
public readonly record struct PackBox(int Cols, float Rows, float Width, float Height, float ToolStrip);

/// <summary>A realized item's state in its grid. <see cref="Provisional"/>: a foreign drag's item.</summary>
public enum PackItemPhase : byte { Rest, Lifted, KeyboardLifted, Provisional }

/// <summary>The per-item handle a <see cref="PackGrid"/> template gets: its key, its packed box and its lift phase as signals.
/// One cell per key while the key is in the list (it survives leaving and re-entering the window).</summary>
public sealed class PackCell
{
    internal readonly Signal<PackBox> BoxSig = new(default);
    internal readonly Signal<PackItemPhase> PhaseSig = new(PackItemPhase.Rest);
    internal NodeHandle Node;
    // the grid's per-cell handlers, built once (stable delegates keep the wrapper's element equal across renders)
    internal Action<NodeHandle>? OnRealized;
    internal Action<KeyEventArgs>? OnKey;
    internal Action<bool>? OnFocus;
    internal DragSource? Drag;
    internal Action<DragEventArgs>? OnDragStarted, OnDragDelta, OnDragCompleted;
    internal Action? OnDragCanceled;
    internal Action<HoldEventArgs>? OnHold;
    internal Action<HoldEventArgs>? OnHoldReleased;
    internal bool Seen;   // still in the list this render (cells of removed keys are dropped)

    internal PackCell(string key) => Key = key;

    public string Key { get; }
    public IReadSignal<PackBox> Box => BoxSig;
    public IReadSignal<PackItemPhase> Phase => PhaseSig;
}

/// <summary>Options of a <see cref="PackGrid"/>. The delegates are read at pack time; the grid re-packs when a signal they read
/// changes.</summary>
public sealed record PackGridOptions<T>
{
    /// <summary>Stable item identity; also the item wrapper's node key.</summary>
    public required Func<T, string> KeyOf { get; init; }
    /// <summary>Read at pack time: reads signals, re-packs when they change.</summary>
    public required Func<T, PackEntry> EntryOf { get; init; }
    /// <summary>From the theme's tier record; read at pack time.</summary>
    public required Func<PackMetrics> Metrics { get; init; }
    public LayoutTransition Reflow { get; init; } = PackGrid.Reflow;
    /// <summary>An item ADDED while mounted; an item the window reaches mounts without it.</summary>
    public EnterExit Enter { get; init; } = PackGrid.FadeIn;
    public EnterExit Exit { get; init; } = PackGrid.FadeOut;
    /// <summary>The ancestor scroller; null: the nearest (<c>UseScroll()</c>).</summary>
    public ScrollHandle? Scroll { get; init; }
    /// <summary>Realized area beyond the viewport on each side, in viewports; widened by the scroll velocity.</summary>
    public float OverscanViewports { get; init; } = 1f;
    public PackGridController? Controller { get; init; }
    public PackReorder<T>? Reorder { get; init; }
    /// <summary>Classic: tiles take focus only in Edit Home. Null: tiles are focusable.</summary>
    public Func<bool>? ItemsFocusable { get; init; }
    /// <summary>Arrows move FOCUS between tiles (<see cref="PackLayout.Nearest"/>).</summary>
    public bool ArrowNavigation { get; init; } = true;
    public Func<T, PackAutomation>? Automation { get; init; }
    /// <summary>[first, last) of the realized entries, on change: the live-budget gate reads it.</summary>
    public Action<int, int>? OnRealizedRange { get; init; }
    /// <summary>A touch hold on a tile released without a drag. Unset (the usual case), the release raises the tile's context
    /// request (<c>OnContextRequested</c> on the wrapper or an ancestor, trigger Hold): the same menu a right-click opens. Set, it
    /// replaces that. Either way a move after the hold lifts the tile instead when reorder is on ("hold, then drag"), and with
    /// this set every tile holds (500 ms on touch) even with reorder off.</summary>
    public Action<T, HoldEventArgs>? OnHoldReleased { get; init; }
    /// <summary>The grid's own node key.</summary>
    public string? Key { get; init; }
    /// <summary>How far an item's clip reaches outside its packed box, per edge (default none: the tile clips at its box). For ink that
    /// overhangs the box on purpose: a full-row band whose heading sits on the page gutter, where a light display glyph ("j") reaches
    /// left of the line's origin. The box, the pack and every neighbour are unchanged; only the paint may reach the outset.</summary>
    public Func<T, Edges4>? InkOutset { get; init; }
}

/// <summary>What a tile announces itself as. The engine has no accessible-name channel on a box yet: the grid applies
/// <see cref="Role"/>; the name, position and set size are for the app's announcements and the inspector.</summary>
public readonly record struct PackAutomation(string Name, AutomationRole Role, int PositionInSet, int SizeOfSet);

/// <summary>
/// A bento grid of keyed items placed at <see cref="PackLayout"/> rects inside the page's own scroller: a natural-height box
/// (not a scroller) that realizes only the bands near the ancestor viewport, and animates every rect change with one FLIP
/// re-flow. Each item sits in a fixed-size, clipped wrapper (a relayout boundary) at <c>Margin = (x, y)</c> in a ZStack, in
/// entry order, so tab and narrator order are reading order. See <c>design/subsystems/pack-grid.md</c>.
/// </summary>
public static class PackGrid
{
    /// <summary>Bounds, a 250 ms tween on cubic-bezier(0.25, 1, 0.5, 1). The size runs as <see cref="SizeMode.Relayout"/>: the tile's own
    /// subtree (a fixed-size boundary) re-solves at the interpolated size each tick, so its content glides with its box. (Reveal laid the
    /// content out at the final size at once and eased a clip over it: every right-aligned part, a card's ⋯ corner, a clock column,
    /// jumped.) Reduced motion: a 120 ms fade.</summary>
    public static LayoutTransition Reflow => Motion.ReducedMotion
        ? new LayoutTransition(TransitionChannels.Opacity, TransitionDynamics.Tween(120f, EasingSpec.Named(Easing.Linear)), SizeMode.Relayout)
        : new LayoutTransition(TransitionChannels.Bounds, TransitionDynamics.Tween(250f, EasingSpec.CubicBezier(0.25f, 1f, 0.5f, 1f)), SizeMode.Relayout);

    /// <summary>An added item: fades in from 96 % (reduced motion: the fade only).</summary>
    public static EnterExit FadeIn => new(Opacity: 0f, Sx: Motion.ReducedMotion ? 1f : 0.96f, Sy: Motion.ReducedMotion ? 1f : 0.96f, Active: true);
    /// <summary>A removed item: fades out to 96 % (reduced motion: the fade only).</summary>
    public static EnterExit FadeOut => new(Opacity: 0f, Sx: Motion.ReducedMotion ? 1f : 0.96f, Sy: Motion.ReducedMotion ? 1f : 0.96f, Active: true);

    public static Element Create<T>(IReadSignal<IReadOnlyList<T>> items, Func<T, PackCell, Element> template,
        PackGridOptions<T> options)
        => Embed.Comp(new PackGridView<T>.Props(items, template, options), () => new PackGridView<T>()) with { Key = options.Key };
}

/// <summary>The app's handle on a mounted <see cref="PackGrid"/>: the live pack, rect and neighbour queries, focus and
/// bring-into-view. Create one, pass it in <see cref="PackGridOptions{T}.Controller"/>.</summary>
public sealed class PackGridController
{
    internal readonly PackResult Live = new();
    internal readonly Signal<int> Version = new(0);
    internal IPackGridHost? Host;

    /// <summary>The live pack, read-only (entry indices are the shown order: the projected one while an item is lifted).</summary>
    public PackResult Layout => Live;
    /// <summary>Bumped after every re-pack.</summary>
    public IReadSignal<int> LayoutVersion => Version;

    /// <summary>The packed rect of <paramref name="key"/> in grid space.</summary>
    public bool TryRect(string key, out RectF rect)
    {
        rect = default;
        if (Host is null) return false;
        int i = Host.IndexOf(key);
        if (i < 0 || i >= Live.Count || Live.BandOf[i] < 0) return false;
        rect = Live.Rects[i];
        return true;
    }

    /// <summary>The key of the entry at a grid-space point, or null.</summary>
    public string? KeyAt(float x, float y)
    {
        if (Host is null) return null;
        int i = PackLayout.HitTest(Live, x, y);
        return i < 0 ? null : Host.KeyAt(i);
    }

    /// <summary>The entry one step away from <paramref name="key"/> (<see cref="PackLayout.Nearest"/>), or null.</summary>
    public string? Neighbour(string key, int dx, int dy, bool tilesOnly = true)
    {
        if (Host is null) return null;
        int i = Host.IndexOf(key);
        if (i < 0) return null;
        int j = PackLayout.Nearest(Live, Host.Entries, i, dx, dy, tilesOnly);
        return j < 0 ? null : Host.KeyAt(j);
    }

    /// <summary>The item has a node right now (inside the window).</summary>
    public bool IsRealized(string key) => Host?.IsRealized(key) ?? false;

    /// <summary>Scrolls the page so the item's packed rect is in view (it is realized on the way).</summary>
    public void BringIntoView(string key, ScrollMove move = ScrollMove.Glide, float margin = 0f) => Host?.BringIntoView(key, move, margin);

    /// <summary>Moves keyboard focus to the item (with the focus visual), bringing it into view first when it is not realized.</summary>
    public void Focus(string key) => Host?.Focus(key);

    /// <summary>The key of the item that holds keyboard focus, or null. A theme handling Alt+Shift+arrows on the board reads it.</summary>
    public string? FocusedKey => Host?.FocusedKey;

    /// <summary><see cref="PackLayout.Verify"/> over the live pack.</summary>
    public int Verify(Span<PackFault> faults) => Host is null ? 0 : PackLayout.Verify(Host.Entries, Host.Metrics, Live, faults);

    /// <summary>The mounted grid's metrics (the inspector's lint snapshot reads them with <see cref="Entries"/>).</summary>
    public PackMetrics Metrics => Host?.Metrics ?? default;
    /// <summary>The entries of the live pack, in shown order.</summary>
    public ReadOnlySpan<PackEntry> Entries => Host is null ? default : Host.Entries;
    /// <summary>The item key of entry <paramref name="index"/>, or null.</summary>
    public string? KeyOf(int index) => Host?.KeyAt(index);
    /// <summary>The presented window-space rect of a realized item's wrapper (the inspector's "realized rect equals packed rect"
    /// check, read when settled); false when it is not realized.</summary>
    public bool TryNodeRect(string key, out RectF rect)
    {
        rect = default;
        return Host is not null && Host.TryNodeRect(key, out rect);
    }
    /// <summary>The grid's own window-space origin (a packed rect plus this is window space, at rest).</summary>
    public bool TryOrigin(out Point2 origin)
    {
        origin = default;
        return Host is not null && Host.TryOrigin(out origin);
    }
}

/// <summary>What a <see cref="PackGridController"/> asks of the mounted grid.</summary>
internal interface IPackGridHost
{
    int IndexOf(string key);
    string? KeyAt(int index);
    ReadOnlySpan<PackEntry> Entries { get; }
    PackMetrics Metrics { get; }
    bool IsRealized(string key);
    void BringIntoView(string key, ScrollMove move, float margin);
    void Focus(string key);
    string? FocusedKey { get; }
    bool TryNodeRect(string key, out RectF rect);
    bool TryOrigin(out Point2 origin);
}

/// <summary>The grid component (see <see cref="PackGrid"/>).</summary>
internal sealed class PackGridView<T> : Component, IPackGridHost, IPackReorderHost
{
    public sealed record Props(IReadSignal<IReadOnlyList<T>> Items, Func<T, PackCell, Element> Template, PackGridOptions<T> Options);

    const string SlotKey = "\u0001pack-slot";

    readonly Signal<float> _width = new(0f);
    readonly Signal<int> _edge = Signal.AlwaysNotify(0);   // a window, lift or projection change: re-render
    readonly Action<RectF> _onBounds;
    readonly Action<NodeHandle> _onRootRealized;
    readonly Action _windowEffect;
    readonly Action _afterCommit;
    readonly Action _dragTick;
    readonly Action _revealDue;
    readonly PackResult _own = new();
    readonly PackResult _prev = new();
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);       // key -> shown index
    readonly Dictionary<string, int> _prevIndex = new(StringComparer.Ordinal);   // the previous pack's
    readonly Dictionary<string, PackCell> _cells = new(StringComparer.Ordinal);
    HashSet<string> _realized = new(StringComparer.Ordinal);
    HashSet<string> _realizedNext = new(StringComparer.Ordinal);
    readonly HashSet<string> _docKeys = new(StringComparer.Ordinal);             // every key of the last list
    readonly List<Element> _children = new();
    readonly List<string> _dropCells = new();
    PackEntry[] _entries = [];
    string[] _keys = [];
    int[] _show = [];          // shown index -> source index (items[i]; == items.Count: the provisional item)
    int[] _proj = [];          // the projected order while lifted (source indices)
    int _count;
    PackMetrics _metrics;
    Props? _p;
    IReadOnlyList<T> _items = Array.Empty<T>();
    ulong _packStamp;
    ulong _structStamp;
    ScrollObservation _scroll;
    ScrollHandle? _scrollHandle;
    InputHooks? _hooks;
    NodeHandle _root;
    int _first, _last;         // the rendered window (shown indices)
    double _lastOffset;
    TimerHandle _revealTimer;
    string? _revealKey;
    string? _pendingFocus;
    string? _focusedKey;
    int _rangeFirst = -1, _rangeLast = -1;

    // lift
    string? _liftKey;
    int _liftSrc = -1;
    bool _liftKb, _liftForeign;
    int _projCount;
    int _lastTargetSrc = -1;
    double _lastTriggerMs;
    Point2 _triggerAt;          // grid-space pointer at the last trigger (scroll moves it; a re-flow does not)
    bool _triggered;
    Point2 _pointerAbs;
    object? _foreignPayload;
    T? _provisional;
    DropTargetSpec? _drop;

    public PackGridView()
    {
        _onBounds = r => { if (r.W > 0f && MathF.Abs(r.W - _width.Peek()) > 0.25f) _width.Value = r.W; };
        _onRootRealized = h => _root = h;
        _windowEffect = WindowEffect;
        _afterCommit = AfterCommit;
        _dragTick = DragTick;
        _revealDue = RevealDue;
    }

    PackResult Result => _p?.Options.Controller?.Live ?? _own;
    PackReorder<T>? Reorder => _p?.Options.Reorder;

    public override Element Render()
    {
        var p = UseProps<Props>();
        _p = p;
        var o = p.Options;
        if (o.Controller is { } c) c.Host = this;
        if (o.Reorder is { } ro) ro.Host = this;
        _scroll = UseScroll(o.Scroll);
        _scrollHandle = o.Scroll ?? UseContext(ScrollCtx.Nearest);
        _hooks = UseContext(InputHooks.Current);
        UseEffect(_windowEffect);
        UseLayoutEffect(_afterCommit);
        _revealTimer = UseTimeout(_revealDue, 270f);
        UseInterval(_dragTick, 40f, enabled: _liftKey is not null && !_liftKb);

        var items = p.Items.Value;
        float width = _width.Value;
        _ = _edge.Value;
        var metrics = o.Metrics();
        bool focusable = o.ItemsFocusable?.Invoke() ?? true;
        bool reorderOn = o.Reorder is { } rr && (rr.Enabled?.Invoke() ?? true);

        // the list changed under a lift (the app republished its model mid-gesture, a sync added or removed a row): carry the
        // projection over by key; only a lifted item that left the list ends the lift
        if (_liftKey is not null && !ReferenceEquals(items, _items) && !RebaseProjection(items)) CancelLift(announce: true, rerender: false);
        _items = items;
        int n = items.Count;
        BuildShown(n);
        int shown = _count;

        // entries, keys and the stamp of what decides the pack
        _index.Clear();
        ulong stamp = 1469598103934665603UL;
        for (int i = 0; i < shown; i++)
        {
            T item = ItemAt(_show[i]);
            var e = o.EntryOf(item);
            string key = o.KeyOf(item);
            _entries[i] = e;
            _keys[i] = key;
            _index[key] = i;
            stamp = Mix(stamp, (ulong)(uint)key.GetHashCode());
            stamp = Mix(stamp, (ulong)e.Kind | (ulong)e.Cols << 8 | (ulong)e.Rows << 16 | (ulong)e.Flags << 24 | (ulong)e.OpenMinCols << 32);
            stamp = Mix(stamp, BitConverter.SingleToUInt32Bits(e.Extent));
        }
        stamp = Mix(stamp, (ulong)(uint)metrics.GetHashCode());
        ulong structure = stamp;                    // the order, the entries and the metrics: what a re-flow animates
        stamp = Mix(stamp, BitConverter.SingleToUInt32Bits(width));
        var r = Result;
        bool repacked = stamp != _packStamp || shown != r.Count;
        // A plain width change (the same structure at another width: a window resize inside one tier) is not a re-flow: the tiles
        // follow the width at once, with their content, as the page around them does. Animating the box while its content lays out
        // at the new width at once would make every right-aligned part jump inside a box still easing.
        bool widthOnly = repacked && structure == _structStamp && shown == r.Count && r.Width > 0f && width > 0f;
        _packStamp = stamp;
        _structStamp = structure;
        _metrics = metrics;
        if (repacked)
        {
            _prev.CopyFrom(r);
            PackLayout.Pack(_entries.AsSpan(0, shown), metrics, width, r);
#if DEBUG
            if (width > 0f) VerifyDebug(r);   // the pack before the first measure (width 0) has no geometry to prove
#endif
        }

        // the window: the bands near the scroller's viewport (everything when there is no scroller)
        ComputeWindow(r, o.OverscanViewports, out int first, out int last);
        if (width <= 0f) { first = 0; last = 0; }
        _first = first; _last = last;

        // children, in shown order (reading order); the slot first so it paints under the tiles
        _children.Clear();
        _realizedNext.Clear();
        var rects = r.Rects;
        bool lifting = _liftKey is not null;
        var reflow = lifting ? o.Reflow with { AnimateDuringScroll = true } : o.Reflow;
        if (lifting && Reorder?.Slot is { } slot && _index.TryGetValue(_liftKey!, out int li) && r.BandOf[li] >= 0)
        {
            var q = rects[li];
            _children.Add(new BoxEl
            {
                Key = SlotKey, Width = q.W, Height = q.H, Margin = new Edges4(q.X, q.Y, 0f, 0f),
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Grow = 0f, Shrink = 0f, Direction = 1,
                HitTestVisible = false, Animate = reflow,
                Children = [slot(q)],
            });
        }
        foreach (var cell in _cells.Values) cell.Seen = false;
        for (int i = 0; i < shown; i++)
        {
            string key = _keys[i];
            var cell = CellFor(key);
            cell.Seen = true;
            if ((_entries[i].Flags & PackFlags.Hidden) != 0 || r.BandOf[i] < 0) continue;
            // inside the window; or, on a re-pack, an item that was realized keeps its node so it slides to its new place
            if (!((i >= first && i < last) || (repacked && _realized.Contains(key)))) continue;
            var q = rects[i];
            var box = new PackBox(r.SpanCols[i], r.SpanRows[i], q.W, q.H, r.ToolStripOf[i]);
            if (cell.BoxSig.Peek() != box) cell.BoxSig.Value = box;
            var phase = key == _liftKey ? (_liftForeign ? PackItemPhase.Provisional : _liftKb ? PackItemPhase.KeyboardLifted : PackItemPhase.Lifted) : PackItemPhase.Rest;
            if (cell.PhaseSig.Peek() != phase) cell.PhaseSig.Value = phase;
            _realizedNext.Add(key);

            // enter: a key new to the list fades in; a known key the window reaches mounts in place; a known key a re-pack
            // brought into the window slides in from its previous rect
            var anim = reflow;
            if (!_realized.Contains(key))
            {
                if (_docKeys.Count > 0 && !_docKeys.Contains(key)) anim = anim with { Enter = o.Enter };
                else if (repacked && _prev.Width > 0f && _prevIndex.TryGetValue(key, out int pi) && pi < _prev.Count && _prev.BandOf[pi] >= 0)
                {
                    var pq = _prev.Rects[pi];
                    if (MathF.Abs(pq.X - q.X) > 0.5f || MathF.Abs(pq.Y - q.Y) > 0.5f)
                        anim = anim with { Enter = new EnterExit(Dx: pq.X - q.X, Dy: pq.Y - q.Y, Active: true) };
                }
            }
            anim = anim with { Exit = o.Exit };
            LayoutTransition? animate = widthOnly ? null : anim;
            T item = ItemAt(_show[i]);
            bool tileLike = _entries[i].Kind is PackEntryKind.Tile or PackEntryKind.Band;
            bool canLift = reorderOn && tileLike && phase != PackItemPhase.Provisional && (Reorder!.CanLift?.Invoke(item) ?? true);
            bool kbLifted = phase == PackItemPhase.KeyboardLifted;
            Edges4 outset = o.InkOutset is { } ink ? ink(item) : default;
            bool hasOutset = outset.Left > 0f || outset.Top > 0f || outset.Right > 0f || outset.Bottom > 0f;
            bool holds = (canLift || o.OnHoldReleased is not null) && phase != PackItemPhase.Provisional;
            bool isFocusable = focusable && tileLike;
            float liftScale = kbLifted ? Reorder!.LiftStyle.Scale : 1f;
            _children.Add(new BoxEl
            {
                Key = key,
                Width = q.W, Height = q.H,
                Margin = new Edges4(q.X, q.Y, 0f, 0f),
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Grow = 0f, Shrink = 0f, Direction = 1,
                // clipped at the box: a fixed-size relayout boundary; with an ink outset the clip moves to the layer below and the
                // wrapper stays a boundary by IsolateLayout
                ClipToBounds = !hasOutset, IsolateLayout = hasOutset, ZStack = hasOutset,
                Animate = animate,
                Focusable = isFocusable,
                OnKeyDown = isFocusable ? cell.OnKey : null,
                OnFocusChanged = isFocusable ? cell.OnFocus : null,
                OnRealized = cell.OnRealized,
                Role = o.Automation is { } au ? au(item).Role : AutomationRole.None,
                Draggable = canLift ? cell.Drag : null,
                // reorder context: a drag that starts on a draggable inside the tile (a track row in a card) moves the TILE;
                // outside it (reorder off) the wrapper is not draggable and inner sources drag as themselves
                ClaimsDescendantDrag = canLift,
                OnDragStarted = canLift ? cell.OnDragStarted : null,
                OnDragDelta = canLift ? cell.OnDragDelta : null,
                OnDragCompleted = canLift ? cell.OnDragCompleted : null,
                OnDragCanceled = canLift ? cell.OnDragCanceled : null,
                // touch lifts only after a hold: the hold on the wrapper keeps an axis drag from taking the page's pan
                Hold = holds ? new HoldSpec(TouchMs: 500f, MouseMs: 0f) : null,
                OnHold = holds ? cell.OnHold : null,
                OnHoldReleased = holds && o.OnHoldReleased is not null ? cell.OnHoldReleased : null,   // unset: the engine raises the context request
                Shadow = kbLifted ? Elevation.Flyout : null,
                ScaleX = liftScale, ScaleY = liftScale,
                HitTestVisible = phase != PackItemPhase.Provisional,
                Children = [hasOutset ? InkLayer(p.Template(item, cell), outset) : p.Template(item, cell)],
            });
        }
        // forget cells of keys that left the list
        if (_cells.Count > shown)
        {
            _dropCells.Clear();
            foreach (var kv in _cells) if (!kv.Value.Seen) _dropCells.Add(kv.Key);
            foreach (var k in _dropCells) _cells.Remove(k);
        }
        (_realized, _realizedNext) = (_realizedNext, _realized);
        if (repacked)
        {
            _prevIndex.Clear();
            foreach (var kv in _index) _prevIndex[kv.Key] = kv.Value;
            if (!lifting)
            {
                _docKeys.Clear();
                for (int i = 0; i < n; i++) _docKeys.Add(o.KeyOf(items[i]));
            }
            if (o.Controller is { } ctl) ctl.Version.Value = ctl.Version.Peek() + 1;
        }
        if (first != _rangeFirst || last != _rangeLast)
        {
            _rangeFirst = first; _rangeLast = last;
            o.OnRealizedRange?.Invoke(first, last);
        }

        if (Reorder is { } reo && reo.AcceptKinds.Length > 0)
            _drop ??= new DropTargetSpec(reo.AcceptKinds, OnForeignEnter, OnForeignOver, OnForeignLeave, OnForeignDrop)
            {
                CanAccept = s => Reorder?.CanAccept?.Invoke(s.Payload) ?? true,
                SettleOnDrop = true,
            };
        return new BoxEl
        {
            ZStack = true,
            Height = r.Height,
            IsolateLayout = true,
            OnBoundsChanged = _onBounds,
            OnRealized = _onRootRealized,
            DropTarget = _drop,
            Children = _children.ToArray(),
        };
    }

    static ulong Mix(ulong h, ulong v) => (h ^ v) * 1099511628211UL;

    /// <summary>The clip layer of an item with an ink outset: it fills the wrapper and reaches <paramref name="o"/> beyond it (negative
    /// margins), clips there, and pads its content back by the same amount, so the content lays out in exactly the packed box.</summary>
    static BoxEl InkLayer(Element content, Edges4 o) => new()
    {
        Direction = 1, ClipToBounds = true, HitTestVisible = true,
        Margin = new Edges4(-o.Left, -o.Top, -o.Right, -o.Bottom),
        Padding = o,
        Children = [content],
    };


    // ── shown order ──

    T ItemAt(int src) => src < _items.Count ? _items[src] : _provisional!;

    void BuildShown(int n)
    {
        if (_liftKey is not null && _projCount > 0)
        {
            EnsureArrays(_projCount);
            Array.Copy(_proj, _show, _projCount);
            _count = _projCount;
            return;
        }
        EnsureArrays(n);
        for (int i = 0; i < n; i++) _show[i] = i;
        _count = n;
    }

    void EnsureArrays(int n)
    {
        if (_entries.Length >= n + 1 && _show.Length >= n + 1 && _proj.Length >= n + 1) return;
        int c = Math.Max(n + 1, Math.Max(16, _entries.Length * 2));
        Array.Resize(ref _entries, c); Array.Resize(ref _keys, c); Array.Resize(ref _show, c); Array.Resize(ref _proj, c);
    }

    PackCell CellFor(string key)
    {
        if (_cells.TryGetValue(key, out var cell)) return cell;
        cell = new PackCell(key);
        var cl = cell;
        cell.OnRealized = h => cl.Node = h;
        cell.OnKey = e => OnItemKey(cl.Key, e);
        cell.OnFocus = got =>
        {
            if (got) _focusedKey = cl.Key;
            else
            {
                if (_focusedKey == cl.Key) _focusedKey = null;
                if (_liftKb && _liftKey == cl.Key) Drop();   // focus leaving the lifted tile drops it (the concept's focusout)
            }
        };
        cell.Drag = new DragSource(Reorder?.Kind ?? "pack", () => cl.Key) { Style = DragStyleOf() };
        cell.OnDragStarted = e => BeginPointerLift(cl.Key, e.Absolute);
        cell.OnDragDelta = e => { _pointerAbs = e.Absolute; DragTick(); };
        cell.OnDragCompleted = e => { _pointerAbs = e.Absolute; DragTick(); if (_liftKey == cl.Key && !_liftKb) CommitLift(); };
        cell.OnDragCanceled = () => { if (_liftKey == cl.Key && !_liftKb) CancelLift(announce: true, rerender: true); };
        cell.OnHold = static _ => { };   // the hold gates the touch lift; the drag that follows lifts the tile
        cell.OnHoldReleased = e => HoldReleased(cl.Key, e);
        _cells[key] = cell;
        return cell;
    }

    DragVisualStyle DragStyleOf()
    {
        var ro = Reorder;
        if (ro is null) return DragVisualStyle.Default;
        return ro.LiftStyle with { ThresholdMultiplier = MathF.Max(1f, ro.MouseSlop / 4f) };
    }

    void HoldReleased(string key, HoldEventArgs e)
    {
        if (_p?.Options.OnHoldReleased is not { } released || !_index.TryGetValue(key, out int i) || i >= _count) return;
        released(ItemAt(_show[i]), e);
    }

    // ── window ──

    /// <summary>The grid's top inside its scroller's content: the layout offsets from the grid up to the nearest scrollable
    /// node (the scroll itself is a paint shift, so it is not in these). O(depth), no allocation.</summary>
    float GridTopInContent()
    {
        var scene = Context.Scene;
        if (scene is null || _root.IsNull || !scene.IsLive(_root)) return 0f;
        float y = 0f;
        for (var n = _root; !n.IsNull; )
        {
            var parent = scene.Parent(n);
            if (parent.IsNull) break;
            y += scene.Bounds(n).Y;
            if ((scene.Flags(parent) & NodeFlags.Scrollable) != 0) break;
            n = parent;
        }
        return y;
    }

    void ComputeWindow(PackResult r, float overscanViewports, out int first, out int last)
    {
        double vp = _scroll.Viewport is { } vs ? vs.Peek() : 0.0;
        if (vp <= 0.0 || r.Count == 0) { first = 0; last = r.Count; return; }
        double off = _scroll.Offset.Peek();
        float top = (float)(off - GridTopInContent());
        float over = (float)(vp * overscanViewports);
        float vel = (float)(off - _lastOffset);
        float lead = MathF.Min((float)vp * 2f, MathF.Abs(vel) * 8f);   // widened toward the motion
        float a = top - over - (vel < 0f ? lead : 0f);
        float b = top + (float)vp + over + (vel > 0f ? lead : 0f);
        PackLayout.Window(r, a, b, out first, out last);
    }

    /// <summary>Runs on scroll frames: re-renders only when the realized range no longer covers half the overscan, or reaches
    /// past two and a half of it. Arithmetic and two binary searches; no allocation.</summary>
    void WindowEffect()
    {
        if (_scroll.Viewport is null || _scroll.Offset is null) return;
        _ = _scroll.Offset.Value;
        _ = _scroll.Viewport.Value;
        var r = Result;
        if (r.Count == 0 || _p is null) return;
        float over = _p.Options.OverscanViewports;
        ComputeWindow(r, over * 0.5f, out int needFirst, out int needLast);
        ComputeWindow(r, over * 2.5f, out int keepFirst, out int keepLast);
        _lastOffset = _scroll.Offset.Peek();
        bool uncovered = needFirst < _first || needLast > _last;
        bool tooWide = _first < keepFirst || _last > keepLast;
        if (uncovered || tooWide) _edge.Value = 0;
    }

    void AfterCommit()
    {
        if (_pendingFocus is { } k && _cells.TryGetValue(k, out var cell) && !cell.Node.IsNull && Context.Scene is { } s && s.IsLive(cell.Node))
        {
            _pendingFocus = null;
            _hooks?.FocusNode?.Invoke(cell.Node, true);
        }
    }

    // ── IPackGridHost ──

    public int IndexOf(string key) => _index.TryGetValue(key, out int i) ? i : -1;
    public string? KeyAt(int index) => (uint)index < (uint)_count ? _keys[index] : null;
    public ReadOnlySpan<PackEntry> Entries => _entries.AsSpan(0, _count);
    public PackMetrics Metrics => _metrics;
    public bool IsRealized(string key) => _realized.Contains(key);
    public string? FocusedKey => _focusedKey;

    public void BringIntoView(string key, ScrollMove move, float margin)
    {
        int i = IndexOf(key);
        var r = Result;
        if (i < 0 || i >= r.Count || r.BandOf[i] < 0 || _scrollHandle is null) return;
        var q = r.Rects[i];
        _scrollHandle.BringIntoView(GridTopInContent() + q.Y, q.H, move: move, margin: margin);
    }

    public void Focus(string key)
    {
        BringIntoView(key, ScrollMove.Glide, 0f);
        if (_cells.TryGetValue(key, out var cell) && !cell.Node.IsNull && Context.Scene is { } s && s.IsLive(cell.Node))
        {
            _hooks?.FocusNode?.Invoke(cell.Node, true);
            return;
        }
        _pendingFocus = key;   // realized by the window the bring-into-view opens; focused after that commit
        _edge.Value = 0;
    }

    public bool TryNodeRect(string key, out RectF rect)
    {
        rect = default;
        if (!_cells.TryGetValue(key, out var cell) || cell.Node.IsNull || Context.Scene is not { } s || !s.IsLive(cell.Node)) return false;
        rect = s.PresentedAbsoluteRect(cell.Node);
        return true;
    }

    public bool TryOrigin(out Point2 origin)
    {
        origin = default;
        if (_root.IsNull || Context.Scene is not { } s || !s.IsLive(_root)) return false;
        var a = s.AbsoluteRect(_root);
        origin = new Point2(a.X, a.Y);
        return true;
    }

    // ── keyboard ──

    void OnItemKey(string key, KeyEventArgs e)
    {
        var ro = Reorder;
        int dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
        int dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
        if (_liftKb && _liftKey == key)
        {
            if (dx != 0 || dy != 0) { MoveToward(key, dx, dy); e.Handled = true; }
            else if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && !e.IsRepeat) { Drop(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { Cancel(); e.Handled = true; }
            else if (e.KeyCode == Keys.Tab) Drop();
            return;
        }
        if (ro is not null && ro.Keyboard == PackKeyboard.LiftMode && e.KeyCode == Keys.Space && !e.IsRepeat && e.Mods == KeyModifiers.None)
        {
            if (Lift(key)) e.Handled = true;
            return;
        }
        // plain arrows move focus; modified arrows bubble to the board (a theme's Alt+Shift+arrows reads FocusedKey)
        if ((dx != 0 || dy != 0) && e.Mods == KeyModifiers.None && _p?.Options.ArrowNavigation == true)
        {
            int i = IndexOf(key);
            if (i < 0) return;
            int j = PackLayout.Nearest(Result, Entries, i, dx, dy, tilesOnly: true);
            if (j >= 0) { Focus(_keys[j]); e.Handled = true; }
        }
    }

    // ── IPackReorderHost: lifts, the projection, commits ──

    public string? LiftedKey => _liftKey;

    bool CanReorder(string key, out int shownIndex)
    {
        shownIndex = IndexOf(key);
        var ro = Reorder;
        if (ro is null || shownIndex < 0 || !(ro.Enabled?.Invoke() ?? true)) return false;
        if (_entries[shownIndex].Kind is not (PackEntryKind.Tile or PackEntryKind.Band)) return false;
        return ro.CanLift?.Invoke(ItemAt(_show[shownIndex])) ?? true;
    }

    void StartProjection()
    {
        EnsureArrays(_count + 1);
        Array.Copy(_show, _proj, _count);
        _projCount = _count;
    }

    readonly Dictionary<string, int> _rebase = new(StringComparer.Ordinal);
    readonly List<int> _rebased = new();

    /// <summary>Maps the projection (source indices of the old list) onto <paramref name="next"/> by key: kept items keep their
    /// projected order, items new to the list go in at their own index, the provisional item stays the last source index.
    /// False when the lifted item is not in the new list.</summary>
    bool RebaseProjection(IReadOnlyList<T> next)
    {
        var o = _p!.Options;
        _rebase.Clear();
        for (int i = 0; i < next.Count; i++) _rebase[o.KeyOf(next[i])] = i;
        _rebased.Clear();
        int liftSrc = -1;
        for (int k = 0; k < _projCount; k++)
        {
            int src = _proj[k];
            if (src >= _items.Count)   // the provisional item
            {
                if (_liftForeign && src == _liftSrc) { liftSrc = next.Count; _rebased.Add(next.Count); }
                continue;
            }
            if (!_rebase.TryGetValue(o.KeyOf(_items[src]), out int ni)) continue;
            _rebase.Remove(o.KeyOf(_items[src]));
            if (src == _liftSrc) liftSrc = ni;
            _rebased.Add(ni);
        }
        if (liftSrc < 0) return false;
        foreach (var kv in _rebase) _rebased.Insert(Math.Min(kv.Value, _rebased.Count), kv.Value);
        EnsureArrays(_rebased.Count + 1);
        for (int k = 0; k < _rebased.Count; k++) _proj[k] = _rebased[k];
        _projCount = _rebased.Count;
        _liftSrc = liftSrc;
        _lastTargetSrc = -1;
        return true;
    }

    int ProjIndexOf(int src)
    {
        for (int k = 0; k < _projCount; k++) if (_proj[k] == src) return k;
        return -1;
    }

    public bool Lift(string key)
    {
        if (_liftKey is not null || !CanReorder(key, out int i)) return false;
        StartProjection();
        _liftKey = key; _liftSrc = _show[i]; _liftKb = true; _liftForeign = false; _lastTargetSrc = -1;
        AnnounceAt(PackAnnounceKind.Grab, key, _proj, _projCount);
        _edge.Value = 0;
        return true;
    }

    void BeginPointerLift(string key, Point2 abs)
    {
        if (_liftKey is not null || !CanReorder(key, out int i)) return;
        StartProjection();
        _liftKey = key; _liftSrc = _show[i]; _liftKb = false; _liftForeign = false; _lastTargetSrc = -1; _lastTriggerMs = double.NegativeInfinity; _triggered = false;
        _pointerAbs = abs;
        AnnounceAt(PackAnnounceKind.Grab, key, _proj, _projCount);
        _edge.Value = 0;
    }

    public bool Drop()
    {
        if (_liftKey is null || !_liftKb) return false;
        CommitLift();
        return true;
    }

    public bool Cancel()
    {
        if (_liftKey is null) return false;
        CancelLift(announce: true, rerender: true);
        return true;
    }

    void CommitLift()
    {
        if (_liftKey is not { } key || _liftForeign) return;
        int from = _liftSrc, to = ProjIndexOf(_liftSrc);
        AnnounceAt(PackAnnounceKind.Drop, key, _proj, _projCount);
        ClearLift();
        if (from >= 0 && to >= 0 && from != to) Reorder?.OnCommit?.Invoke(new PackMove(key, from, to));
        _revealKey = key; _revealTimer.Restart();
        _edge.Value = 0;
    }

    void CancelLift(bool announce, bool rerender)
    {
        if (_liftKey is not { } key) return;
        bool foreign = _liftForeign;
        if (announce) AnnounceAt(PackAnnounceKind.Cancel, key, _show, _count);
        ClearLift();
        if (!foreign) Reorder?.OnCancel?.Invoke(key);
        if (rerender) _edge.Value = 0;
    }

    void ClearLift()
    {
        _liftKey = null; _liftSrc = -1; _liftKb = false; _liftForeign = false; _projCount = 0; _lastTargetSrc = -1;
        _provisional = default; _foreignPayload = null;
    }

    /// <summary>The concept's <c>moveRel</c> on the projection: onto a later entry the item lands after it, onto an earlier one
    /// before it, onto a group head after it.</summary>
    bool MoveRel(int src, int targetSrc, bool targetIsHead)
    {
        int i = ProjIndexOf(src), j = ProjIndexOf(targetSrc);
        if (i < 0 || j < 0 || i == j) return false;
        int k = j - (i < j ? 1 : 0) + (i < j || targetIsHead ? 1 : 0);
        return MoveTo(i, k);
    }

    /// <summary>Removes the projection's entry at <paramref name="i"/> and puts it back so it ends at <paramref name="k"/>.</summary>
    bool MoveTo(int i, int k)
    {
        if (k < 0 || k >= _projCount || k == i) return false;
        int src = _proj[i];
        if (Reorder?.CanLandAt is { } land && !land(ItemAt(src), k)) return false;
        if (k > i) Array.Copy(_proj, i + 1, _proj, i, k - i);
        else Array.Copy(_proj, k, _proj, k + 1, i - k);
        _proj[k] = src;
        return true;
    }

    public bool MoveBy(string key, int delta)
    {
        if (delta == 0 || !CanReorder(key, out int si)) return false;
        bool own = _liftKey is null;
        if (own) StartProjection();
        else if (_liftKey != key) return false;
        int i = ProjIndexOf(_show[si]);
        bool moved = i >= 0 && MoveTo(i, i + delta);
        return FinishStep(key, _show[si], own, moved, delta < 0);
    }

    public bool MoveToward(string key, int dx, int dy)
    {
        if ((dx == 0 && dy == 0) || !CanReorder(key, out int si)) return false;
        bool own = _liftKey is null;
        if (own) StartProjection();
        else if (_liftKey != key) return false;
        int src = _show[si];
        bool moved;
        if (dy == 0)
        {
            int i = ProjIndexOf(src);
            moved = i >= 0 && MoveTo(i, i + Math.Sign(dx));
        }
        else
        {
            // onto the nearest entry above / below in the shown pack (the projection as last drawn)
            int t = PackLayout.Nearest(Result, Entries, si, 0, dy, tilesOnly: false);
            moved = t >= 0 && MoveRel(src, _show[t], _entries[t].Kind == PackEntryKind.GroupHead);
        }
        return FinishStep(key, src, own, moved, dx < 0 || dy < 0);
    }

    /// <summary>A step either moved the keyboard lift's projection (re-render, announce the position) or, with no lift, is
    /// committed at once as one move.</summary>
    bool FinishStep(string key, int src, bool own, bool moved, bool towardStart)
    {
        if (!moved)
        {
            if (own) _projCount = 0;
            AnnounceAt(PackAnnounceKind.Edge, key, _show, _count, towardStart);
            return false;
        }
        AnnounceAt(PackAnnounceKind.Move, key, _proj, _projCount);
        if (own)
        {
            int to = ProjIndexOf(src);
            _projCount = 0;
            if (to >= 0 && to != src) Reorder?.OnCommit?.Invoke(new PackMove(key, src, to));
        }
        else _edge.Value = 0;
        _revealKey = key; _revealTimer.Restart();
        return true;
    }

    void RevealDue()
    {
        if (_revealKey is { } k) { _revealKey = null; BringIntoView(k, ScrollMove.Glide, 0f); }
    }

    /// <summary>The pointer lift's step: hit-test the shown (projected) pack under the pointer; a target triggers once, cannot
    /// trigger again until the pointer has left it, never sooner than the dwell after the last (on the frame clock), and never
    /// without new travel: after a trigger the pointer must have moved, in GRID space, half the mouse slop. A re-flow slides
    /// other tiles under a resting pointer and must not walk the order; an auto-scroll carrying the content under it moves the
    /// grid-space point and does. Runs on drag moves and on a 40 ms interval while lifted (the auto-scroll case). No allocation.</summary>
    void DragTick()
    {
        if (_liftKey is null || _liftKb || Context.Scene is not { } s || _root.IsNull || !s.IsLive(_root)) return;
        var g = s.AbsoluteRect(_root);
        float x = _pointerAbs.X - g.X, y = _pointerAbs.Y - g.Y;
        var r = Result;
        int shownCount = Math.Min(_count, r.Count);
        if (_lastTargetSrc >= 0)
        {
            int lt = -1;
            for (int k = 0; k < shownCount; k++) if (_show[k] == _lastTargetSrc) { lt = k; break; }
            if (lt < 0 || !r.Rects[lt].Contains(new Point2(x, y))) _lastTargetSrc = -1;
        }
        if (_triggered)
        {
            float tx = x - _triggerAt.X, ty = y - _triggerAt.Y, slop = MathF.Max(1f, (Reorder?.MouseSlop ?? 6f) * 0.5f);
            if (tx * tx + ty * ty < slop * slop) return;   // a resting pointer never retriggers
        }
        double now = _revealTimer.NowMs;
        if (now - _lastTriggerMs < (Reorder?.DwellMs ?? 140f)) return;
        int hit = PackLayout.HitTest(r, x, y);
        if (hit < 0 || hit >= shownCount) return;
        int targetSrc = _show[hit];
        if (targetSrc == _liftSrc || targetSrc == _lastTargetSrc) return;
        if (!MoveRel(_liftSrc, targetSrc, _entries[hit].Kind == PackEntryKind.GroupHead)) return;
        _lastTargetSrc = targetSrc; _lastTriggerMs = now;
        _triggerAt = new Point2(x, y); _triggered = true;
        AnnounceAt(PackAnnounceKind.Move, _liftKey, _proj, _projCount);
        _edge.Value = 0;
    }

    // ── drag-in of a foreign payload ──

    void OnForeignEnter(DragSession s)
    {
        var ro = Reorder;
        if (ro is null || _liftKey is not null || _p is null) return;
        if (ro.Materialize is not { } materialize) return;
        T? item = materialize(s.Payload);
        if (item is null) return;
        _provisional = item; _foreignPayload = s.Payload;
        StartProjection();
        _liftSrc = _items.Count;
        _proj[_projCount++] = _liftSrc;   // the provisional item, at the end until the pointer places it
        _liftKey = _p.Options.KeyOf(item); _liftKb = false; _liftForeign = true; _lastTargetSrc = -1; _lastTriggerMs = double.NegativeInfinity; _triggered = false;
        _pointerAbs = s.Position;
        _edge.Value = 0;
    }

    void OnForeignOver(DragSession s)
    {
        if (!_liftForeign) return;
        _pointerAbs = s.Position;
        DragTick();
    }

    void OnForeignLeave(DragSession s)
    {
        if (!_liftForeign) return;
        ClearLift();
        _edge.Value = 0;
    }

    void OnForeignDrop(DragSession s)
    {
        if (!_liftForeign) return;
        int at = ProjIndexOf(_liftSrc);
        object? payload = _foreignPayload;
        ClearLift();
        if (at >= 0) Reorder?.OnForeignCommit?.Invoke(payload, at);
        _edge.Value = 0;
    }

    // ── announcements ──

    void AnnounceAt(PackAnnounceKind kind, string key, int[] order, int count, bool edgeAtStart = false)
    {
        var ro = Reorder;
        if (ro?.AnnounceText is not { } compose || _p is null) return;
        var o = _p.Options;
        // the key's 1-based position among the tiles of its group, in `order`
        int group = 0, pos = 0, size = 0, g = -1;
        bool found = false;
        for (int k = 0; k < count; k++)
        {
            T item = ItemAt(order[k]);
            var e = o.EntryOf(item);
            if (e.Kind == PackEntryKind.GroupHead) { if (found) break; g++; size = 0; pos = 0; continue; }
            if (e.Kind == PackEntryKind.Row || (e.Flags & PackFlags.Hidden) != 0) continue;
            size++;
            if (!found)
            {
                pos++;
                if (o.KeyOf(item) == key) { found = true; group = Math.Max(0, g); }
            }
        }
        if (kind == PackAnnounceKind.Edge) pos = edgeAtStart ? 1 : size;
        string? text = compose(new PackAnnounce(kind, key, found || kind == PackAnnounceKind.Edge ? pos : 0, size, group));
        if (text is null) return;
        if (kind == PackAnnounceKind.Move) Announcer.SayThrottled(text, ro.AnnounceAssertive, ro.AnnounceThrottleMs);
        else Announcer.Say(text, ro.AnnounceAssertive);
    }

#if DEBUG
    void VerifyDebug(PackResult r)
    {
        Span<PackFault> faults = stackalloc PackFault[8];
        int nf = PackLayout.Verify(_entries.AsSpan(0, _count), _metrics, r, faults);
        for (int i = 0; i < Math.Min(nf, faults.Length); i++)
        {
            var f = faults[i];
            Console.Error.WriteLine($"[pack] fault {f.Kind} grid={_p?.Options.Key} group={f.Group} band={f.Band} entry={(f.Entry >= 0 && f.Entry < _count ? _keys[f.Entry] : "-")} at=({f.Where.X:0.#},{f.Where.Y:0.#},{f.Where.W:0.#},{f.Where.H:0.#}) cols={r.Columns} width={r.Width:0.#}");
        }
    }
#endif
}
