namespace FluentGpu.Signals;

/// <summary>
/// The engine's own reactive computations as ROWS of a table instead of one object each (PERF r3 MODEL, DESIGN 2.5): a node
/// bind, a component render, a control-flow boundary is a row index here, with its state, its queue flag, its source links and
/// what to run kept in arrays. A row runs the same three-state push-pull algorithm as <see cref="Computation"/> (CLEAN, CHECK,
/// DIRTY; a CHECK resolves by polling its sources in read order), is scheduled into the same runtime queues in the same order,
/// and subscribes to signals and memos through the same <see cref="SubList"/>, as a <see cref="Sub"/> with a row.
/// <para>What a row does when it runs is the host's: <see cref="Runner"/> gets the row's kind and payload (an index into the
/// host's own tables). Rows are recycled through a free list; a row disposed while it waits in a queue is freed when the
/// queue drops it, so a queued entry can never run a recycled row.</para>
/// <para>UI-thread confined, like the rest of the reactive core. One per <see cref="ReactiveRuntime"/>.</para>
/// </summary>
public sealed class RxTable
{
    private const byte FLive = 1, FQueued = 2, FStructural = 4, FFreeAfterQueue = 8;

    private readonly ReactiveRuntime _rt;
    private byte[] _state = new byte[64];
    private byte[] _flags = new byte[64];
    private int[] _kind = new int[64];
    private int[] _payload = new int[64];
    private int[] _srcHead = new int[64];   // first source link (read order), 0 = none
    private int[] _srcTail = new int[64];
    private ISignalSource?[] _cause = new ISignalSource?[64];
    private int _high = 1;                  // row 0 is "no row"
    private int _freeRow;                   // free list threaded through _payload
    private int _live;

    // Source links (a row's sources in read order): a pool with a free list threaded through _linkNext.
    private ISignalSource?[] _linkSrc = new ISignalSource?[256];
    private int[] _linkNext = new int[256];
    private int _linkHigh = 1;
    private int _freeLink;

    /// <summary>Runs a row: (kind, payload, row). Called with tracking set to the row, after its old source links were dropped.</summary>
    public delegate void RowRunner(int kind, int payload, int row);

    /// <summary>The host's runner for every row kind. Must be set before the first row runs.</summary>
    public RowRunner? Runner { get; set; }

    /// <summary>DIAGNOSTIC: the object a row exists on behalf of (kind, payload) → owner, for the slow-unit report and the wake
    /// census. Report time only.</summary>
    public Func<int, int, object?>? DescribeOwner { get; set; }

    /// <summary>DIAGNOSTIC: a row kind's name. Report time only.</summary>
    public Func<int, string>? DescribeKind { get; set; }

    internal RxTable(ReactiveRuntime rt) => _rt = rt;

    /// <summary>Live rows (a census figure).</summary>
    public int LiveCount => _live;

    /// <summary>A new row, DIRTY (it has never run). <paramref name="structural"/> rows flush before normal ones, as
    /// <see cref="Computation.Structural"/> computations do.</summary>
    public int Create(int kind, int payload, bool structural = false)
    {
        int row;
        if (_freeRow != 0) { row = _freeRow; _freeRow = _payload[row]; }
        else
        {
            row = _high++;
            if (row >= _state.Length) Grow(row + 1);
        }
        _state[row] = Computation.Dirty;
        _flags[row] = (byte)(FLive | (structural ? FStructural : 0));
        _kind[row] = kind;
        _payload[row] = payload;
        _srcHead[row] = _srcTail[row] = 0;
        _cause[row] = null;
        _live++;
        return row;
    }

    /// <summary>The payload a row was created with (the host may re-point it, e.g. after its own table compacts).</summary>
    public int PayloadOf(int row) => _payload[row];
    public void SetPayload(int row, int payload) => _payload[row] = payload;
    public int KindOf(int row) => _kind[row];
    public bool IsLive(int row) => row > 0 && row < _high && (_flags[row] & FLive) != 0;

    /// <summary>What last took the row out of CLEAN (a signal, a memo, or null for an imperative schedule); cleared on read.</summary>
    public object? TakeStaleCause(int row) { var c = _cause[row]; _cause[row] = null; return c; }

    /// <summary>Dispose a row: drop its source links, and free it now, or once the queue that holds it drops it.</summary>
    public void Dispose(int row)
    {
        if (!IsLive(row)) return;
        UnlinkSources(row);
        _live--;
        byte f = _flags[row];
        if ((f & FQueued) != 0) { _flags[row] = (byte)((f & ~FLive) | FFreeAfterQueue); return; }
        Free(row);
    }

    private void Free(int row)
    {
        _flags[row] = 0;
        _state[row] = Computation.Clean;
        _cause[row] = null;
        _payload[row] = _freeRow;
        _freeRow = row;
    }

    /// <summary>Imperatively mark a row DIRTY and schedule it (a forced re-run).</summary>
    public void Schedule(int row) => MarkDirty(row, null);

    /// <summary>Run a row now (its first run, or a forced one), tracking what it reads.</summary>
    public void RunNow(int row) { if (IsLive(row)) Run(row); }

    internal void MarkDirty(int row, ISignalSource? cause)
    {
        if ((_flags[row] & FLive) == 0 || _state[row] == Computation.Dirty) return;
        bool wasClean = _state[row] == Computation.Clean;
        _state[row] = Computation.Dirty;
        if (wasClean) { _cause[row] = cause; Enqueue(row); }
    }

    internal void MarkCheck(int row, ISignalSource? cause)
    {
        if ((_flags[row] & FLive) == 0 || _state[row] != Computation.Clean) return;
        _state[row] = Computation.Check;
        _cause[row] = cause;
        Enqueue(row);
    }

    private void Enqueue(int row)
    {
        byte f = _flags[row];
        if ((f & FQueued) != 0) return;
        _flags[row] = (byte)(f | FQueued);
        _rt.ScheduleRow(this, row, (f & FStructural) != 0);
    }

    /// <summary>The runtime popped the row off a queue: clear its queue flag. False when the row was disposed meanwhile (it is
    /// freed here and must not run).</summary>
    internal bool Dequeue(int row)
    {
        byte f = _flags[row];
        _flags[row] = (byte)(f & ~FQueued);
        if ((f & FFreeAfterQueue) != 0) { Free(row); return false; }
        return (f & FLive) != 0;
    }

    /// <summary>The runaway guard dropped the row from a queue: back to CLEAN, unqueued (freed if it was disposed).</summary>
    internal void Drop(int row)
    {
        if (!Dequeue(row)) return;
        _state[row] = Computation.Clean;
    }

    internal bool IsStructural(int row) => (_flags[row] & FStructural) != 0;

    /// <summary>Run the row only if it is really out of date: CLEAN does nothing, CHECK polls its sources first (the equality
    /// cut-off), DIRTY runs. The same resolution <see cref="Computation.RunIfNecessary"/> does for an object.</summary>
    internal void RunIfNecessary(int row)
    {
        if (_state[row] == Computation.Clean) return;
        if (_state[row] == Computation.Check)
        {
            for (int l = _srcHead[row]; l != 0 && _state[row] == Computation.Check; l = _linkNext[l]) _linkSrc[l]!.EnsureFresh();
            if (_state[row] == Computation.Check) { _state[row] = Computation.Clean; return; }
            if (_state[row] != Computation.Dirty) return;
        }
        Run(row);
    }

    private void Run(int row)
    {
        UnlinkSources(row);
        var prevC = Tracking.Current; var prevT = Tracking.Table; int prevR = Tracking.Row;
        Tracking.Current = null; Tracking.Table = this; Tracking.Row = row;
        try { Runner!(_kind[row], _payload[row], row); }
        finally
        {
            Tracking.Current = prevC; Tracking.Table = prevT; Tracking.Row = prevR;
            if ((_flags[row] & FLive) != 0) _state[row] = Computation.Clean;
        }
    }

    /// <summary>A source the running row just read (the caller deduped it against its own subscriber list).</summary>
    internal void AddSource(int row, ISignalSource s)
    {
        int l;
        if (_freeLink != 0) { l = _freeLink; _freeLink = _linkNext[l]; }
        else
        {
            l = _linkHigh++;
            if (l >= _linkSrc.Length)
            {
                Array.Resize(ref _linkSrc, _linkSrc.Length * 2);
                Array.Resize(ref _linkNext, _linkNext.Length * 2);
            }
        }
        _linkSrc[l] = s;
        _linkNext[l] = 0;
        if (_srcTail[row] == 0) _srcHead[row] = l; else _linkNext[_srcTail[row]] = l;
        _srcTail[row] = l;
    }

    private void UnlinkSources(int row)
    {
        int l = _srcHead[row];
        if (l == 0) return;
        var self = new Sub(this, row);
        while (l != 0)
        {
            int next = _linkNext[l];
            _linkSrc[l]!.Unsubscribe(self);
            _linkSrc[l] = null;
            _linkNext[l] = _freeLink;
            _freeLink = l;
            l = next;
        }
        _srcHead[row] = _srcTail[row] = 0;
    }

    private void Grow(int need)
    {
        int n = Math.Max(need, _state.Length * 2);
        Array.Resize(ref _state, n); Array.Resize(ref _flags, n); Array.Resize(ref _kind, n); Array.Resize(ref _payload, n);
        Array.Resize(ref _srcHead, n); Array.Resize(ref _srcTail, n); Array.Resize(ref _cause, n);
    }

    internal object? DiagOwnerOf(int row) => DescribeOwner?.Invoke(_kind[row], _payload[row]);
    internal string DiagKindOf(int row) => DescribeKind?.Invoke(_kind[row]) ?? "row:" + _kind[row];
}
