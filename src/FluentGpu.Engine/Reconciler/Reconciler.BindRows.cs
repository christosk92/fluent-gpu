using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// Bound channels as TABLE ROWS (PERF r3 MODEL M3, DESIGN 2.5). A bound Prop<T> channel of a node used to be one BindEffect<T>
// object (a Computation: ≈ 180 B plus its source list) for as long as the node lived; it is now a row of this reconciler's bind
// table (node, payload, selector, companions element, fire, flags) plus a row of the runtime's RxTable (reactive state and source
// links). A mounted page adds rows to long-lived arrays instead of objects to the heap; an unmount frees the rows for the next
// mount. The reactive behaviour is the computation's, one for one (RxTable mirrors Computation's three-state machine, queueing
// and source order); the fire bodies are the former ones, reading the channel through a Bind<T> view instead of the effect.
// The node's binding chain keeps one order for binds and boundary effects together (the links below), so RethemeAll and the
// persist replay schedule exactly what they did, in the same order.
public sealed partial class TreeReconciler
{
    internal const int RowKindBind = 1, RowKindComp = 2, RowKindShow = 3, RowKindSkel = 4, RowKindFor = 5;
    private const byte BFWritesLayout = 1, BFPrimed = 2, BFStarted = 4, BFFreeAfterFire = 8;
    private int _firingBind;                           // the bind whose body is running (0 = none): disposed mid-run, it is freed after

    private NodeHandle[] _bNode = new NodeHandle[256];
    private object?[] _bThunk = new object?[256];      // the channel's current Func<T>, or null
    private object?[] _bSignal = new object?[256];     // the channel's current IReadSignal<T>, or null
    private object?[] _bSel = new object?[256];        // Func<Element, Prop<T>>: reads the channel off a re-rendered element (static)
    private BindOps?[] _bOps = new BindOps?[256];      // the typed operations (one shared instance per T)
    private object?[] _bFire = new object?[256];       // Action<BindRef> (static, closure-free) or Action (a closure body)
    private Element?[] _bEl = new Element?[256];       // the element the channel was last reconciled against (companions)
    private byte[] _bFlags = new byte[256];
    private int[] _bRow = new int[256];                // its RxTable row; the free list threads through here while free
    private int _bHigh = 1, _bFree, _bLive;            // bind 0 is "none"

    /// <summary>Live bind rows (diagnostics).</summary>
    internal int BindRowCount => _bLive;

    private void InitRows()
    {
        var rows = Runtime.Rows;
        rows.Runner = RunRow;
        rows.DescribeKind = static k => k switch
        {
            RowKindBind => "BindEffect`1",
            RowKindComp => "CompEntry",
            RowKindShow or RowKindSkel or RowKindFor => "Effect",
            _ => "row:" + k,
        };
        rows.DescribeOwner = DescribeRowOwner;
    }

    private void RunRow(int kind, int payload, int row)
    {
        if (kind == RowKindBind) { FireBind(payload); return; }
        if (kind == RowKindComp) { var entry = _compById[payload]!; RunComponent(entry.Node, entry); return; }
        if (kind == RowKindShow) { RunShow(_rowNode[row]); return; }
        if (kind == RowKindSkel) { RunSkel(_rowNode[row]); return; }
        if (kind == RowKindFor) { RunFor(_rowNode[row]); return; }
        throw new InvalidOperationException("unknown row kind " + kind);
    }

    /// <summary>A row's diagnostic owner (Computation.DiagOwner): the component of a render row; none for a bind.</summary>
    private object? DescribeRowOwner(int kind, int payload) => kind == RowKindComp ? _compById[payload]?.Comp : null;

    // ── Boundary rows (Show, skeleton, For): a row of the runtime plus the boundary node, by row number ──────────────────────
    private NodeHandle[] _rowNode = new NodeHandle[256];

    private int NewNodeRow(int kind, NodeHandle node)
    {
        int row = Runtime.Rows.Create(kind, 0);
        if (row >= _rowNode.Length) Array.Resize(ref _rowNode, Math.Max(row + 1, _rowNode.Length * 2));
        _rowNode[row] = node;
        return row;
    }

    private void DisposeNodeRow(int row)
    {
        Runtime.Rows.Dispose(row);
        _rowNode[row] = default;
    }

    // ── Mounted components by id (a render row's payload) ─────────────────────────────────────────────────────────────────
    private CompEntry?[] _compById = new CompEntry?[256];
    private int[] _compFreeNext = new int[256];
    private int _compHigh = 1, _compFree;

    private int RegisterComp(CompEntry entry)
    {
        int id;
        if (_compFree != 0) { id = _compFree; _compFree = _compFreeNext[id]; }
        else
        {
            id = _compHigh++;
            if (id >= _compById.Length) { Array.Resize(ref _compById, id * 2); Array.Resize(ref _compFreeNext, id * 2); }
        }
        _compById[id] = entry;
        return id;
    }

    /// <summary>Unmount: dispose the render row (unsubscribed; a queued render is dropped) and free the id.</summary>
    private void ReleaseComp(CompEntry entry)
    {
        Runtime.Rows.Dispose(entry.Row);
        _compById[entry.Id] = null;
        _compFreeNext[entry.Id] = _compFree;
        _compFree = entry.Id;
    }

    private void FireBind(int b)
    {
        FluentGpu.Hosting.MountProf.Enter(FluentGpu.Hosting.MountProf.BindFire);
        int outer = _firingBind;
        _firingBind = b;
        try
        {
            var f = _bFire[b];
            if (f is Action body) body();
            else Unsafe.As<Action<BindRef>>(f!)(new BindRef(this, b));
        }
        finally
        {
            _firingBind = outer;
            // A body that disposed its own bind (its node unmounted under it) read a live row to the end, as an effect object
            // would have; the row is released now.
            if ((_bFlags[b] & BFFreeAfterFire) != 0) ReleaseBind(b);
            FluentGpu.Hosting.MountProf.Exit();
        }
    }

    /// <summary>A new bind for <paramref name="el"/>'s channel read by <paramref name="channel"/> (not yet running: <see cref="Bind{T}.Start(NodeHandle, Action{BindRef})"/>).</summary>
    private Bind<T> NewBind<T>(Element el, Func<Element, Prop<T>> channel, bool writesLayout = false)
    {
        int b;
        if (_bFree != 0) { b = _bFree; _bFree = _bRow[b]; }
        else
        {
            b = _bHigh++;
            if (b >= _bNode.Length) GrowBinds();
        }
        _bLive++;
        Prop<T> p = channel(el);
        _bThunk[b] = p.Thunk;
        _bSignal[b] = p.Signal;
        _bSel[b] = channel;
        _bOps[b] = BindOps<T>.Instance;
        _bEl[b] = el;
        _bFlags[b] = writesLayout ? BFWritesLayout : (byte)0;
        _bNode[b] = default;
        _bFire[b] = null;
        _bRow[b] = 0;
        return new Bind<T>(this, b);
    }

    /// <summary>The mount run: create the bind's reactive row and run it now (Computation.RunStale), then mark it started.</summary>
    internal void StartBind(int b)
    {
        int row = Runtime.Rows.Create(RowKindBind, b);
        _bRow[b] = row;
        Runtime.Rows.RunNow(row);
        _bFlags[b] |= BFStarted;
    }

    /// <summary>Dispose a bind: unsubscribe its row (a queued run is dropped by the flush) and free both rows.</summary>
    private void DisposeBind(int b)
    {
        Runtime.Rows.Dispose(_bRow[b]);
        if (b == _firingBind) { _bFlags[b] |= BFFreeAfterFire; return; }
        ReleaseBind(b);
    }

    private void ReleaseBind(int b)
    {
        _bThunk[b] = null; _bSignal[b] = null; _bSel[b] = null; _bOps[b] = null; _bFire[b] = null; _bEl[b] = null;
        _bNode[b] = default; _bFlags[b] = 0;
        _bRow[b] = _bFree;
        _bFree = b;
        _bLive--;
    }

    private void ScheduleBind(int b) => Runtime.Rows.Schedule(_bRow[b]);

    private void GrowBinds()
    {
        int n = _bNode.Length * 2;
        Array.Resize(ref _bNode, n); Array.Resize(ref _bThunk, n); Array.Resize(ref _bSignal, n); Array.Resize(ref _bSel, n);
        Array.Resize(ref _bOps, n); Array.Resize(ref _bFire, n); Array.Resize(ref _bEl, n); Array.Resize(ref _bFlags, n);
        Array.Resize(ref _bRow, n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T ReadBind<T>(int b)
    {
        var thunk = _bThunk[b];
        return thunk is not null ? Unsafe.As<Func<T>>(thunk)() : Unsafe.As<IReadSignal<T>>(_bSignal[b]!).Value;
    }

    /// <summary><see cref="BindEffect{T}.Rewire"/> for a row: point the bind at <paramref name="next"/>'s channel.</summary>
    internal BindRewire RewireBind<T>(int b, Element next)
    {
        Prop<T> p = Unsafe.As<Func<Element, Prop<T>>>(_bSel[b]!)(next);
        if (!p.IsBound) return BindRewire.NowStatic;
        _bEl[b] = next;
        Func<T>? thunk = p.Thunk;
        IReadSignal<T>? signal = p.Signal;
        if (object.Equals(thunk, _bThunk[b]) && object.Equals(signal, _bSignal[b])) return BindRewire.Unchanged;
        _bThunk[b] = thunk;
        _bSignal[b] = signal;
        Runtime.Rows.RunNow(_bRow[b]);
        return BindRewire.Reran;
    }

    internal NodeHandle BindNodeOf(int b) => _bNode[b];
    internal Element BindElOf(int b) => _bEl[b]!;
    internal bool BindFlag(int b, byte f) => (_bFlags[b] & f) != 0;
    internal void SetBindFlag(int b, byte f, bool on) => _bFlags[b] = on ? (byte)(_bFlags[b] | f) : (byte)(_bFlags[b] & ~f);
    internal void SetBindFire(int b, NodeHandle node, object fire) { _bNode[b] = node; _bFire[b] = fire; }

    // ── The node binding chain: one ordered list per node of boundary computations and bind rows ─────────────────────────────
    private Computation?[] _nbComp = new Computation?[256];
    private int[] _nbBind = new int[256];              // a bind id, or 0
    private int[] _nbRow = new int[256];               // a boundary row (Show/Skel/For), or 0
    private int[] _nbNext = new int[256];
    private int _nbHigh = 1, _nbFree;   // link 0 is "none"; the free list threads through _nbNext

    private int NewLink(Computation? c, int bind, int row = 0)
    {
        int l;
        if (_nbFree != 0) { l = _nbFree; _nbFree = _nbNext[l]; }
        else
        {
            l = _nbHigh++;
            if (l >= _nbComp.Length)
            {
                int n = _nbComp.Length * 2;
                Array.Resize(ref _nbComp, n); Array.Resize(ref _nbBind, n); Array.Resize(ref _nbRow, n); Array.Resize(ref _nbNext, n);
            }
        }
        _nbComp[l] = c; _nbBind[l] = bind; _nbRow[l] = row; _nbNext[l] = 0;
        return l;
    }

    private void FreeLink(int l)
    {
        _nbComp[l] = null; _nbBind[l] = 0; _nbRow[l] = 0;
        _nbNext[l] = _nbFree;
        _nbFree = l;
    }

    private void AppendLink(NodeHandle node, int link)
    {
        int idx = (int)node.Raw.Index;
        if (!_nodeBindings.TryGetValue(idx, out int head)) { _nodeBindings[idx] = link; return; }
        while (_nbNext[head] != 0) head = _nbNext[head];   // wiring order (a node binds a handful)
        _nbNext[head] = link;
    }

    private void AddBinding(NodeHandle node, Computation c) => AppendLink(node, NewLink(c, 0));
    private void AddBinding(NodeHandle node, BindRef bind) => AppendLink(node, NewLink(null, bind.Id));
    private void AddRowBinding(NodeHandle node, int row) => AppendLink(node, NewLink(null, 0, row));

    /// <summary>Schedule every binding of the chain starting at <paramref name="head"/> (RethemeAll, the persist replay).</summary>
    private void ScheduleChain(int head)
    {
        for (int l = head; l != 0; l = _nbNext[l])
        {
            if (_nbComp[l] is { } c) c.Schedule();
            else if (_nbBind[l] != 0) ScheduleBind(_nbBind[l]);
            else Runtime.Rows.Schedule(_nbRow[l]);
        }
    }
}

/// <summary>The typed operations of a bind row (one shared instance per value type: no per-bind object).</summary>
internal abstract class BindOps
{
    internal abstract BindRewire Rewire(TreeReconciler rec, int b, Element next);
}

internal sealed class BindOps<T> : BindOps
{
    internal static readonly BindOps<T> Instance = new();
    internal override BindRewire Rewire(TreeReconciler rec, int b, Element next) => rec.RewireBind<T>(b, next);
}

/// <summary>A bind row seen by a closure-free fire body (the former <c>BindEffect</c>): its node, companions element, flags.</summary>
internal readonly struct BindRef
{
    internal readonly TreeReconciler Rec;
    internal readonly int Id;
    internal BindRef(TreeReconciler rec, int id) { Rec = rec; Id = id; }

    internal NodeHandle Node => Rec.BindNodeOf(Id);
    /// <summary>The element this channel was last reconciled against: bodies read static companions here.</summary>
    internal Element El => Rec.BindElOf(Id);
    /// <summary>The mount run is over (E20: a later run re-writes a channel that already shows a value).</summary>
    internal bool Started => Rec.BindFlag(Id, 4);
    internal bool WritesLayout => Rec.BindFlag(Id, 1);
    /// <summary>A channel's own first-write latch (the bound Width/Height "primed" state).</summary>
    internal bool Primed { get => Rec.BindFlag(Id, 2); set => Rec.SetBindFlag(Id, 2, value); }
}

/// <summary>A typed bind row (the former <c>BindEffect&lt;T&gt;</c>): <see cref="Read"/> is the channel's current value, read
/// INSIDE the body, which is what subscribes the row to the current source.</summary>
internal readonly struct Bind<T>
{
    internal readonly TreeReconciler Rec;
    internal readonly int Id;
    internal Bind(TreeReconciler rec, int id) { Rec = rec; Id = id; }
    internal Bind(BindRef r) { Rec = r.Rec; Id = r.Id; }

    internal T Read() => Rec.ReadBind<T>(Id);
    internal NodeHandle Node => Rec.BindNodeOf(Id);
    internal Element El => Rec.BindElOf(Id);
    internal bool Started => Rec.BindFlag(Id, 4);
    internal bool WritesLayout => Rec.BindFlag(Id, 1);
    internal bool Primed { get => Rec.BindFlag(Id, 2); set => Rec.SetBindFlag(Id, 2, value); }

    /// <summary>Arm the closure-free body (a static lambda calling the channel's Fire method) and run it once.</summary>
    internal BindRef Start(NodeHandle node, Action<BindRef> fire) { Rec.SetBindFire(Id, node, fire); Rec.StartBind(Id); return this; }
    /// <summary>Arm a closure body and run it once (the rare channels).</summary>
    internal BindRef Start(Action body) { Rec.SetBindFire(Id, default, body); Rec.StartBind(Id); return this; }

    public static implicit operator BindRef(Bind<T> b) => new(b.Rec, b.Id);
    public static explicit operator Bind<T>(BindRef r) => new(r);
}
