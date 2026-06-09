using System.Diagnostics;
using System.Threading;

namespace FluentGpu.Foundation;

/// <summary>4-byte interned string id. Id 0 = empty. No <see cref="string"/> reaches the paint path.</summary>
public readonly record struct StringId(int Value)
{
    public bool IsEmpty => Value == 0;
    public static StringId Empty => default;
}

/// <summary>String interner with REF-COUNTED reclamation. Lives in Foundation so Render can resolve without depending on Scene.
///
/// <para><b>Seam-safe (threading-render-seam.md §9):</b> the UI thread interns (<see cref="Intern"/>, the single writer) while
/// the render thread resolves (<see cref="Resolve"/>) the previous frame's ids concurrently. The backing store is therefore
/// <i>chunked</i>: chunks are allocated once and never resized or moved, so a concurrent reader can never observe a torn or
/// reallocated array (a plain <c>List&lt;string&gt;</c> would race on its geometric Add-realloc). A release-store of the count
/// publishes each new slot; the reader acquire-loads the count before indexing, so it only ever reads fully-written slots.</para>
///
/// <para><b>Reclamation (virtualization workloads):</b> a 100k-row virtual list streams unique row text through the table —
/// append-only interning is an unbounded leak. Owners (the scene's text columns) pair <see cref="AddRef"/>/<see cref="Release"/>;
/// when an id's count hits 0 its map entry is removed (a re-intern of the same content gets a NEW id) and the slot is cleared
/// after a reader quarantine (<see cref="Tick"/> per frame, far beyond the in-flight frame depth). <b>Ids are never reused</b> —
/// reclaimed ids stay burned, so a stale id can never alias different content (the shaped-run cache keys on ids and ages out in
/// well under the id space). Fully-dead chunks free their backing array. Strings never AddRef'd are simply permanent.</para></summary>
public sealed class StringTable
{
    private const int ChunkBits = 12;
    private const int ChunkSize = 1 << ChunkBits;   // 4096 strings/chunk
    private const int ChunkMask = ChunkSize - 1;
    // Slots are cleared this many Ticks (frames) after their release — far beyond the queued-frame depth, so a draw
    // list recorded before the release can still Resolve the id on the render thread.
    private const int QuarantineTicks = 16;

    private readonly Dictionary<string, int> _map = new(StringComparer.Ordinal);   // writer-only (UI thread); reader never touches it
    private string[]?[] _chunks;                     // outer array swapped (grown) under release ordering; chunks themselves immutable
    private int[]?[] _refs;                          // per-id refcounts (writer-only, parallel to _chunks)
    private int[] _chunkDead;                        // per-chunk cleared-slot count (chunk freed at ChunkSize)
    private int _count;                              // published via Volatile (release on write / acquire on read)
    private long _tick;
    private readonly Queue<(int Id, long Tick)> _pendingClear = new();

    public StringTable()
    {
        _chunks = new string[4][];
        _chunks[0] = new string[ChunkSize];
        _chunks[0]![0] = "";   // id 0 = empty
        _refs = new int[4][];
        _refs[0] = new int[ChunkSize];
        _chunkDead = new int[4];
        Volatile.Write(ref _count, 1);
    }

    /// <summary>Live map entries (interned strings currently resolvable by content) — diagnostics/leak checks.</summary>
    public int MapCount => _map.Count;
    /// <summary>Releases whose slots await the reader quarantine — diagnostics.</summary>
    public int PendingReclaim => _pendingClear.Count;

    /// <summary>Intern a string to a stable id. UI-thread only (the single writer). A live id is never moved; reclaimed
    /// ids are never reused (re-interning previously-released content yields a fresh id).</summary>
    public StringId Intern(string? s)
    {
        if (string.IsNullOrEmpty(s)) return StringId.Empty;
        if (_map.TryGetValue(s, out int id)) return new StringId(id);

        id = _count;
        int ci = id >> ChunkBits, off = id & ChunkMask;
        if (ci >= _chunks.Length) GrowChunkArray(ci);
        if (_chunks[ci] is null)
        {
            Volatile.Write(ref _chunks[ci], new string[ChunkSize]);   // publish a fully-allocated chunk
            _refs[ci] = new int[ChunkSize];
        }
        _chunks[ci]![off] = s;          // write the slot...
        _refs[ci]![off] = 0;
        _map[s] = id;
        Volatile.Write(ref _count, id + 1);   // ...then release the new count so a reader that sees it also sees the slot
        return new StringId(id);
    }

    /// <summary>Take an ownership reference on an id (UI thread). Owners are the scene's text columns; pair with <see cref="Release"/>.</summary>
    public void AddRef(StringId id)
    {
        int v = id.Value;
        if (v <= 0 || v >= _count) return;
        _refs[v >> ChunkBits]![v & ChunkMask]++;
    }

    /// <summary>Drop an ownership reference (UI thread). The LAST release removes the map entry and schedules the slot
    /// clear behind the reader quarantine. Releasing a never-AddRef'd (permanent) id is a no-op.</summary>
    public void Release(StringId id)
    {
        int v = id.Value;
        if (v <= 0 || v >= _count) return;
        int ci = v >> ChunkBits, off = v & ChunkMask;
        int[]? refs = _refs[ci];
        if (refs is null || refs[off] == 0) return;   // permanent (never AddRef'd) or already reclaimed
        if (--refs[off] > 0) return;

        string? s = _chunks[ci]?[off];
        if (s is not null) _map.Remove(s);
        _pendingClear.Enqueue((v, _tick));
    }

    /// <summary>Advance the reclamation clock one frame (host, once per painted frame): clears slots whose quarantine
    /// elapsed and frees fully-dead chunks. UI thread.</summary>
    public void Tick()
    {
        _tick++;
        while (_pendingClear.Count > 0)
        {
            var (id, tick) = _pendingClear.Peek();
            if (_tick - tick <= QuarantineTicks) break;
            _pendingClear.Dequeue();

            int ci = id >> ChunkBits, off = id & ChunkMask;
            var chunk = _chunks[ci];
            if (chunk is null) continue;
            if (_refs[ci]![off] != 0) { Debug.Fail("StringTable: id resurrected after release"); continue; }
            chunk[off] = null!;   // atomic ref store — a concurrent Resolve sees null → ""
            if (++_chunkDead[ci] == ChunkSize)
            {
                Volatile.Write(ref _chunks[ci], null);   // whole chunk dead (and fully written) → free the backing array
                _refs[ci] = null;
            }
        }
    }

    /// <summary>Resolve an id to its string. Safe to call from the render thread concurrently with <see cref="Intern"/>.</summary>
    public string Resolve(StringId id)
    {
        int v = id.Value;
        if ((uint)v >= (uint)Volatile.Read(ref _count)) return "";   // acquire — pairs with Intern's release; bounds the read
        var chunks = Volatile.Read(ref _chunks);                     // see the (possibly-grown) outer array published before count
        var chunk = Volatile.Read(ref chunks[v >> ChunkBits]);
        return chunk?[v & ChunkMask] ?? "";
    }

    private void GrowChunkArray(int neededChunkIndex)
    {
        // Rare. Copy chunk refs into a larger outer array and publish it; existing chunks are reused in place (never moved).
        int newLen = Math.Max(_chunks.Length * 2, neededChunkIndex + 1);
        var bigger = new string[newLen][];
        Array.Copy(_chunks, bigger, _chunks.Length);
        var biggerRefs = new int[newLen][];
        Array.Copy(_refs, biggerRefs, _refs.Length);
        var biggerDead = new int[newLen];
        Array.Copy(_chunkDead, biggerDead, _chunkDead.Length);
        _refs = biggerRefs;
        _chunkDead = biggerDead;
        Volatile.Write(ref _chunks, bigger);
    }
}
