using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>One page fetch's result: the collection <paramref name="Total"/> (so a consumer can reserve virtualization
/// extent for the WHOLE collection from the very first page) and the page's <paramref name="Items"/>. A readonly record
/// struct over a <see cref="ReadOnlyMemory{T}"/> — no allocation, and a source can hand back a slice of its own buffer.</summary>
public readonly record struct PageResult<T>(int Total, ReadOnlyMemory<T> Items);

/// <summary>A source that yields a large collection in pages — the OO seam (a class can implement it). Most callers use the
/// delegate form of <see cref="VirtualCollection{T}"/> instead; this exists for sources that prefer a contract.</summary>
public interface IPagedSource<T>
{
    ValueTask<PageResult<T>> FetchPageAsync(int offset, int count, CancellationToken ct);
}

/// <summary>
/// A source-agnostic, DATA-virtualized collection: a remote/large list of KNOWN total size whose items arrive in PAGES on
/// demand (the artist-discography shape — <c>totalCount</c> is known up front, items stream in as you scroll). Pairs with a
/// virtualized view: the view reserves extent from <see cref="CountOr0"/>, renders a placeholder for any index where
/// <see cref="IsLoaded"/> is false, and calls <see cref="EnsureRange"/> with its visible window to pull the covering pages.
///
/// Design (modern, fast, near-zero-alloc):
/// <list type="bullet">
/// <item>CHUNKED storage (one array per page, jagged) — O(1) indexing, never a single giant array (no LOH cliff); only
/// fetched pages cost memory.</item>
/// <item>Indexing and an already-satisfied <see cref="EnsureRange"/> allocate ZERO — just bounds checks + array reads.</item>
/// <item><see cref="ValueTask{T}"/> fetch: a cached/synchronous source completes with no continuation/state-machine box.</item>
/// <item>ONE <see cref="Version"/> signal (signals-first) bumps when a page lands — the view re-windows; no per-frame work.</item>
/// <item>Pages are all-or-nothing, so a non-null chunk IS the "loaded" flag — no per-slot bitset.</item>
/// </list>
/// Threading: indexing + <see cref="EnsureRange"/> run on the UI thread; the async fetch completes off-thread and is
/// marshalled back through <c>post</c> (the component's <c>UsePost</c>) so the fill + signal bump are UI-thread — no locks.
/// The fetch is a plain delegate, so ANY source plugs in (Spotify GraphQL, a local DB, an in-memory list, a future source).
/// </summary>
public sealed class VirtualCollection<T>
{
    /// <summary>Fetch the page <c>[offset, offset+count)</c> and report the collection total. Generic over the source.</summary>
    public delegate ValueTask<PageResult<T>> Fetch(int offset, int count, CancellationToken ct);

    private readonly Fetch _fetch;
    private readonly int _pageSize;
    private readonly Action<Action>? _post;     // marshal off-thread completion to the UI thread; null = inline (sync/tests)
    private readonly CancellationToken _ct;
    private readonly Signal<int> _version = new(0);

    private T[]?[] _chunks = Array.Empty<T[]>();   // _chunks[p] == null ⇒ page p not loaded (pages are all-or-nothing)
    private bool[] _requested = Array.Empty<bool>(); // page p loaded OR in-flight ⇒ true (request dedup)
    private int _count = -1;                          // total; -1 until the first page lands
    private bool _provisional;                        // _count is a seeded estimate a real page may still correct (see Seed/Fill)

    public VirtualCollection(Fetch fetch, int pageSize = 50, Action<Action>? post = null, CancellationToken ct = default)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _pageSize = Math.Max(1, pageSize);
        _post = post;
        _ct = ct;
    }

    public VirtualCollection(IPagedSource<T> source, int pageSize = 50, Action<Action>? post = null, CancellationToken ct = default)
        : this((source ?? throw new ArgumentNullException(nameof(source))).FetchPageAsync, pageSize, post, ct) { }

    /// <summary>Total item count, or -1 until the first page lands.</summary>
    public int Count => _count;
    /// <summary>Total clamped to ≥ 0 — reserve virtualization extent from this.</summary>
    public int CountOr0 => _count < 0 ? 0 : _count;
    /// <summary>Bumps whenever a page fills (and when the total is first learned). A view reads it to re-window.</summary>
    public IReadSignal<int> Version => _version;
    public int PageSize => _pageSize;

    /// <summary>O(1), allocation-free. The item, or <c>default</c> when its page is not loaded yet — pair with
    /// <see cref="IsLoaded"/> to tell an unloaded slot from a genuine default value (value-type T).</summary>
    public T? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) return default;
            int p = index / _pageSize;
            var chunk = (uint)p < (uint)_chunks.Length ? _chunks[p] : null;
            return chunk is null ? default : chunk[index - p * _pageSize];
        }
    }

    /// <summary>True iff <paramref name="index"/>'s page is loaded. O(1), allocation-free.</summary>
    public bool IsLoaded(int index)
    {
        if ((uint)index >= (uint)_count) return false;
        int p = index / _pageSize;
        return (uint)p < (uint)_chunks.Length && _chunks[p] is not null;
    }

    /// <summary>Seed an already-known prefix — the artist overview returns the first window + totals, so page 0 (and any
    /// further already-held items) is free, no refetch. Call once before any <see cref="EnsureRange"/>. When
    /// <paramref name="provisional"/> is true, <paramref name="total"/> is an ESTIMATE: it renders identically to a real
    /// count (<see cref="CountOr0"/>/indexer/<see cref="EnsureRange"/> can't tell), but the first real page that reports a
    /// different total wins — <see cref="Fill"/> re-sizes to the authoritative value. Only takes effect when the total is
    /// still unknown; a total already learned from a real page is never downgraded to provisional.</summary>
    public void Seed(int total, ReadOnlySpan<T> items, bool provisional = false)
    {
        if (_count < 0) { SetCount(total); _provisional = provisional; }
        for (int i = 0; i < items.Length && i < _count; i += _pageSize)
        {
            int p = i / _pageSize;
            int cap = Math.Min(_pageSize, _count - i);
            var chunk = new T[cap];
            items.Slice(i, Math.Min(cap, items.Length - i)).CopyTo(chunk);
            _chunks[p] = chunk;
            _requested[p] = true;
        }
        Bump();
    }

    /// <summary>Ensure the pages covering <c>[first, last]</c> are fetched — loaded/in-flight pages are skipped, so this is
    /// a no-op (alloc-free) once the window is present. The view calls it with its visible range whenever the window moves.</summary>
    public void EnsureRange(int first, int last)
    {
        if (_count == 0) return;
        if (_count < 0) { RequestPage(0); return; }      // learn the total from page 0 first
        if (last < first) (first, last) = (last, first);
        first = Math.Clamp(first, 0, _count - 1);
        last = Math.Clamp(last, 0, _count - 1);
        for (int p = first / _pageSize, pe = last / _pageSize; p <= pe; p++) RequestPage(p);
    }

    private void RequestPage(int p)
    {
        if (p < _requested.Length && _requested[p]) return;   // loaded or already in-flight
        EnsureCapacity(p);
        _requested[p] = true;
        var task = _fetch(p * _pageSize, _pageSize, _ct);
        if (task.IsCompletedSuccessfully) Fill(p, task.Result);   // cached/synchronous → no continuation, no box
        else Await(p, task);
    }

    private async void Await(int p, ValueTask<PageResult<T>> task)
    {
        try { var r = await task.ConfigureAwait(false); Run(p, r, fill: true); }
        catch { Run(p, default, fill: false); }   // failed → drop the in-flight guard so a later scroll retries
    }

    // Marshal to the UI thread WITHOUT a per-call closure on the common path: a tiny fixed continuation struct carries the
    // page + payload, so the off-thread completion path allocates only the (rare) state machine, never a capture per fill.
    private void Run(int page, PageResult<T> r, bool fill)
    {
        if (_post is { } post) post(new Continuation(this, page, r, fill).Invoke);
        else Apply(page, r, fill);
    }

    private void Apply(int page, in PageResult<T> r, bool fill)
    {
        if (fill) Fill(page, r);
        else if (page < _requested.Length) _requested[page] = false;
    }

    private sealed class Continuation(VirtualCollection<T> owner, int page, PageResult<T> result, bool fill)
    {
        public void Invoke() => owner.Apply(page, result, fill);
    }

    private void Fill(int page, in PageResult<T> r)
    {
        bool learnedTotal = _count < 0;
        bool corrected = false;
        if (learnedTotal) SetCount(r.Total);
        else if (_provisional && r.Total != _count) { SetCount(r.Total); corrected = true; }  // authoritative page corrects the seeded estimate
        _provisional = false;                                           // a real page has now spoken (whether it agreed or not)
        int cap = (uint)page < (uint)_chunks.Length ? Math.Min(_pageSize, _count - page * _pageSize) : 0;
        if (cap > 0)
        {
            var span = r.Items.Span;
            var chunk = new T[cap];
            span[..Math.Min(span.Length, cap)].CopyTo(chunk);
            _chunks[page] = chunk;
            Bump();
        }
        // An empty facet (Total 0) stores no chunk but MUST still signal that the total is now known — or that a
        // provisional estimate was corrected to a smaller total that leaves this page out of range (e.g. seeded N > 0,
        // live facet empty) — so a consumer watching Version re-windows instead of showing stale shimmer slots forever.
        else if (learnedTotal || corrected) Bump();
    }

    // Sizes _chunks/_requested to EXACTLY the page count for the new total. Grow or shrink: surviving pages are preserved,
    // pages that fall out of range are dropped (so a later grow can't resurrect a stale chunk, and an in-flight result for a
    // now-out-of-range page is a no-op via the cap==0 guard in Fill). A reused array of the same length is left in place.
    private void SetCount(int total)
    {
        _count = Math.Max(0, total);
        int pages = (_count + _pageSize - 1) / _pageSize;
        if (pages == _chunks.Length) return;
        int keep = Math.Min(pages, _chunks.Length);
        var c = new T[pages][]; Array.Copy(_chunks, c, keep); _chunks = c;
        var rq = new bool[pages]; Array.Copy(_requested, rq, keep); _requested = rq;
    }

    private void EnsureCapacity(int page)
    {
        if (page < _chunks.Length) return;
        int n = Math.Max(page + 1, _chunks.Length == 0 ? 4 : _chunks.Length * 2);
        var c = new T[n][]; _chunks.CopyTo(c, 0); _chunks = c;
        var rq = new bool[n]; _requested.CopyTo(rq, 0); _requested = rq;
    }

    private void Bump() => _version.Value = _version.Peek() + 1;
}
