using System.Threading;

namespace FluentGpu.Foundation;

/// <summary>4-byte interned string id. Id 0 = empty. No <see cref="string"/> reaches the paint path.</summary>
public readonly record struct StringId(int Value)
{
    public bool IsEmpty => Value == 0;
    public static StringId Empty => default;
}

/// <summary>Process/window-lifetime string interner. Lives in Foundation so Render can resolve without depending on Scene.
///
/// <para><b>Seam-safe (threading-render-seam.md §9):</b> the UI thread interns (<see cref="Intern"/>, the single writer) while
/// the render thread resolves (<see cref="Resolve"/>) the previous frame's ids concurrently. The backing store is therefore
/// <i>chunked</i>: chunks are allocated once and never resized or moved, so a concurrent reader can never observe a torn or
/// reallocated array (a plain <c>List&lt;string&gt;</c> would race on its geometric Add-realloc). A release-store of the count
/// publishes each new slot; the reader acquire-loads the count before indexing, so it only ever reads fully-written slots.</para></summary>
public sealed class StringTable
{
    private const int ChunkBits = 12;
    private const int ChunkSize = 1 << ChunkBits;   // 4096 strings/chunk
    private const int ChunkMask = ChunkSize - 1;

    private readonly Dictionary<string, int> _map = new(StringComparer.Ordinal);   // writer-only (UI thread); reader never touches it
    private string[]?[] _chunks;                     // outer array swapped (grown) under release ordering; chunks themselves immutable
    private int _count;                              // published via Volatile (release on write / acquire on read)

    public StringTable()
    {
        _chunks = new string[4][];
        _chunks[0] = new string[ChunkSize];
        _chunks[0]![0] = "";   // id 0 = empty
        Volatile.Write(ref _count, 1);
    }

    /// <summary>Intern a string to a stable id. UI-thread only (the single writer). Existing ids are never reused or moved.</summary>
    public StringId Intern(string? s)
    {
        if (string.IsNullOrEmpty(s)) return StringId.Empty;
        if (_map.TryGetValue(s, out int id)) return new StringId(id);

        id = _count;
        int ci = id >> ChunkBits, off = id & ChunkMask;
        if (ci >= _chunks.Length) GrowChunkArray(ci);
        if (_chunks[ci] is null) Volatile.Write(ref _chunks[ci], new string[ChunkSize]);   // publish a fully-allocated chunk
        _chunks[ci]![off] = s;          // write the slot...
        _map[s] = id;
        Volatile.Write(ref _count, id + 1);   // ...then release the new count so a reader that sees it also sees the slot
        return new StringId(id);
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
        var bigger = new string[Math.Max(_chunks.Length * 2, neededChunkIndex + 1)][];
        Array.Copy(_chunks, bigger, _chunks.Length);
        Volatile.Write(ref _chunks, bigger);
    }
}
