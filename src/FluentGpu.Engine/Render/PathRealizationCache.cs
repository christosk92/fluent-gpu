using System.Runtime.CompilerServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Geometry-realization cache key (gpu-renderer.md §5.1): <c>(GeometryId, ContentEpoch, DeviceScaleQ, StrokeWidthQ,
/// RuleByte, JoinCapByte, Kind)</c>. <see cref="ContentEpoch"/> folds in the content-version stamp so a geometry edit
/// (a fresh <see cref="PathContentEpoch"/>, even over byte-identical points) is a compile-time-guaranteed cache MISS,
/// never a stale replay (<see cref="PathContentEpoch"/>'s own doc). <see cref="DeviceScaleQ"/>/<see cref="StrokeWidthQ"/>
/// are quantized (×64, rounded) so a sub-quantum scale/width wobble still HITS.
///
/// <para><b><see cref="JoinCapByte"/> is additive beyond canon</b> (gpu-renderer.md §5.1 prints the 4-field key
/// without it): without folding join/cap/miter-limit into the key, two stroke nodes sharing one geometry at one
/// width but DIFFERENT joins would collide on the same cache slot and one would silently render with the other's
/// tessellation. Packed as <c>(join &lt;&lt; 4) | (cap &lt;&lt; 2) | quantizedMiterLimit</c> (2 bits each for join/cap,
/// 4 bits for a coarsely-quantized miter limit — plenty to discriminate the three joins × three caps × a handful of
/// limit buckets that ever actually differ in practice).</para>
/// </summary>
public readonly record struct PathRealizationKey(
    int GeometryId, ulong ContentEpoch, ushort DeviceScaleQ, ushort StrokeWidthQ, byte RuleByte, byte JoinCapByte, byte Kind)
{
    /// <summary>0 = fill, 1 = stroke (<see cref="Kind"/>'s documented values).</summary>
    public const byte KindFill = 0, KindStroke = 1;

    public static ushort QuantizeScale(float deviceScale) => (ushort)Math.Clamp((int)MathF.Round(deviceScale * 64f), 0, ushort.MaxValue);
    public static ushort QuantizeWidth(float width) => (ushort)Math.Clamp((int)MathF.Round(width * 64f), 0, ushort.MaxValue);

    /// <summary>Pack join/cap/miter-limit into the additive discriminator byte (see type doc).</summary>
    public static byte PackJoinCap(LineJoin join, LineCap cap, float miterLimit)
    {
        byte j = (byte)((byte)join & 0x3);
        byte c = (byte)((byte)cap & 0x3);
        byte m = (byte)(Math.Clamp((int)MathF.Round(miterLimit), 0, 15) & 0xF);
        return (byte)((j << 6) | (c << 4) | m);
    }
}

/// <summary>
/// Retained vertex/index slab + LRU realization cache for <see cref="PathTessellator"/> results (gpu-renderer.md
/// §5.1) — the "a static SVG/icon costs nothing per frame" guarantee. Tessellate once on first paint or on a
/// scale/content/style change (a genuine <see cref="PathRealizationKey"/> miss); every subsequent frame the recorder
/// emits <c>FillPathCmd</c>/<c>StrokePathCmd</c> referencing the SAME cached <see cref="PathRef"/> — zero pending
/// tessellation work, zero managed allocation, on the steady-state path.
///
/// <para><b>Backing store:</b> one retained pair of managed arrays (<see cref="PathVertex"/>[] + <c>uint</c>[]) with a
/// bump cursor — deliberately NOT the per-frame <see cref="ArenaAllocator"/> (its spans die on <c>Reset()</c>, the
/// opposite of retained) and NOT <c>SlabAllocator&lt;T&gt;</c> (that's a fixed-size-per-handle slot allocator; a
/// tessellated path's vertex/index run varies wildly in size). Grows by DOUBLING, and only on an actual cache MISS
/// that needs more room than is currently allocated — never speculatively.</para>
///
/// <para><b>LRU / eviction posture mirrors <c>ImageTextureStore.Free</c>'s deferred-behind-the-frame-fence discipline</b>
/// (read it: <c>src/FluentGpu.Windows/D3D12/ImageTextureStore.cs</c>) generalized from "2 frames" to
/// <see cref="QuarantineFrames"/>: an entry's <c>LastUsedFrame</c> must be strictly older than
/// <c>currentFrame - QuarantineFrames</c> before it is even ELIGIBLE for eviction, and compaction itself only ever
/// runs at a <see cref="BeginFrame"/> boundary (never mid-frame, never on the read/hit path) — so this stays correct
/// unchanged when the render seam later moves to a real ≥2-frame in-flight quarantine.</para>
///
/// <para><b>Key→<see cref="PathRef"/> map</b> is a pre-sized OPEN-ADDRESSED struct array (linear probing +
/// tombstones), never <c>Dictionary&lt;K,V&gt;</c> — this is read on the record-time cache-lookup path, and a
/// <c>Dictionary</c> resize allocates exactly where this repo's zero-alloc discipline says it must not.</para>
/// </summary>
public sealed class PathRealizationCache
{
    public static readonly PathRealizationCache Shared = new();

    /// <summary>An entry younger than this many frames is NEVER evicted, regardless of budget pressure — the
    /// generalized form of <c>ImageTextureStore</c>'s 2-frame fence (<c>RenderInFlightDepth + 1</c>, foundations.md).</summary>
    public const int QuarantineFrames = 2;

    private enum SlotState : byte { Empty = 0, Occupied = 1, Tombstone = 2 }
    private struct Slot
    {
        public SlotState State;
        public PathRealizationKey Key;
        public PathRef Value;
        public ulong LastUsedFrame;
    }

    private Slot[] _slots = new Slot[256];
    private int _occupied, _tombstones;

    private PathVertex[] _vtxSlab = new PathVertex[4096];
    private uint[] _idxSlab = new uint[8192];
    private int _vtxCursor, _idxCursor;

    private ulong _currentFrame;

    /// <summary>Total <see cref="PathTessellator"/> invocations that actually ran (cache MISSES only — a hit never
    /// re-tessellates). Always-on plain counter (NOT <c>[Conditional]</c>) — a Release build's zero-re-tessellation
    /// proof (<c>gate.path.tess.alloc-zero</c>) reads it directly.</summary>
    public int TessellationCount { get; private set; }
    /// <summary>Total successful <c>TryRealize*</c> calls (hits + misses).</summary>
    public int RealizationCount { get; private set; }
    /// <summary>Live bytes currently held across both slabs.</summary>
    public long SlabBytes => (long)_vtxCursor * Unsafe.SizeOf<PathVertex>() + (long)_idxCursor * sizeof(uint);
    /// <summary>Total entries dropped by LRU compaction across the process lifetime.</summary>
    public int EvictionCount { get; private set; }

    public ReadOnlySpan<PathVertex> Vertices => _vtxSlab.AsSpan(0, _vtxCursor);
    public ReadOnlySpan<uint> Indices => _idxSlab.AsSpan(0, _idxCursor);

    /// <summary>Frame-boundary hook: advances the current frame index and, ONLY if the slab is currently over
    /// <see cref="GpuProfile.PathSlabBudgetBytes"/>, compacts out every entry outside the quarantine window (see type
    /// doc). Never runs mid-frame; the read path (<see cref="TryRealizeFill"/>/<see cref="TryRealizeStroke"/>) never
    /// triggers it.</summary>
    public void BeginFrame(ulong frameIndex)
    {
        _currentFrame = frameIndex;
        if (SlabBytes > GpuProfile.PathSlabBudgetBytes) CompactEvictingStale();
    }

    public bool TryRealizeFill(PathData path, FillRule rule, float deviceScale, out PathRef pathRef)
    {
        if (path is null || path.VerbCount == 0) { pathRef = default; return false; }
        var key = new PathRealizationKey(path.GeometryId, path.Epoch.Value,
            PathRealizationKey.QuantizeScale(deviceScale), 0, (byte)rule, 0, PathRealizationKey.KindFill);
        return Realize(key, path, rule, default, deviceScale, PathTrimSpace.PerContour, isStroke: false, out pathRef);
    }

    public bool TryRealizeStroke(PathData path, in StrokeStyle style, float deviceScale, out PathRef pathRef,
        PathTrimSpace trimSpace = PathTrimSpace.PerContour)
    {
        if (path is null || path.VerbCount == 0 || style.IsNone) { pathRef = default; return false; }
        var key = new PathRealizationKey(path.GeometryId, path.Epoch.Value,
            PathRealizationKey.QuantizeScale(deviceScale), PathRealizationKey.QuantizeWidth(style.Width),
            (byte)path.Rule, PathRealizationKey.PackJoinCap(style.Join, style.Cap, style.MiterLimit), PathRealizationKey.KindStroke);
        return Realize(key, path, path.Rule, style, deviceScale, trimSpace, isStroke: true, out pathRef);
    }

    private bool Realize(in PathRealizationKey key, PathData path, FillRule rule, in StrokeStyle style,
        float deviceScale, PathTrimSpace trimSpace, bool isStroke, out PathRef pathRef)
    {
        int slot = Find(key);
        if (slot >= 0)
        {
            _slots[slot].LastUsedFrame = _currentFrame;
            pathRef = _slots[slot].Value;
            RealizationCount++;
            return true;
        }

        // Miss: tessellate into the tail of the current slab; grow (double) on an undersized attempt and retry.
        while (true)
        {
            var vtxDst = _vtxSlab.AsSpan(_vtxCursor);
            var idxDst = _idxSlab.AsSpan(_idxCursor);
            var tess = new PathTessellator(vtxDst, idxDst, deviceScale);
            bool ok;
            PathRef r;
            if (isStroke) ok = tess.TryTessellateStroke(path, in style, out r, trimSpace);
            else ok = tess.TryTessellateFill(path, rule, out r);

            if (ok)
            {
                TessellationCount++;
                var final = new PathRef(_vtxCursor, r.VtxCount, _idxCursor, r.IdxCount, r.Bounds, r.ArcLenPx);
                _vtxCursor += r.VtxCount;
                _idxCursor += r.IdxCount;
                Insert(key, final);
                pathRef = final;
                RealizationCount++;
                return true;
            }

            GrowVtx(_vtxCursor + tess.NeededVtx);
            GrowIdx(_idxCursor + tess.NeededIdx);
        }
    }

    private void GrowVtx(int need)
    {
        int n = Math.Max(_vtxSlab.Length * 2, need);
        Array.Resize(ref _vtxSlab, n);
    }

    private void GrowIdx(int need)
    {
        int n = Math.Max(_idxSlab.Length * 2, need);
        Array.Resize(ref _idxSlab, n);
    }

    // ── open-addressed key→PathRef map (linear probe + tombstones) ─────────────
    private int Find(in PathRealizationKey key)
    {
        int mask = _slots.Length - 1;
        int i = key.GetHashCode() & mask;
        int start = i;
        while (true)
        {
            var st = _slots[i].State;
            if (st == SlotState.Empty) return -1;
            if (st == SlotState.Occupied && _slots[i].Key.Equals(key)) return i;
            i = (i + 1) & mask;
            if (i == start) return -1;
        }
    }

    private void Insert(in PathRealizationKey key, in PathRef value)
    {
        if ((_occupied + _tombstones + 1) * 4 >= _slots.Length * 3) Rehash(_slots.Length * 2);
        InsertRaw(key, value, _currentFrame);
    }

    private void InsertRaw(in PathRealizationKey key, in PathRef value, ulong lastUsed)
    {
        int mask = _slots.Length - 1;
        int i = key.GetHashCode() & mask;
        while (_slots[i].State == SlotState.Occupied) i = (i + 1) & mask;
        if (_slots[i].State == SlotState.Tombstone) _tombstones--;
        _slots[i] = new Slot { State = SlotState.Occupied, Key = key, Value = value, LastUsedFrame = lastUsed };
        _occupied++;
    }

    private void Rehash(int newCapacity)
    {
        var old = _slots;
        _slots = new Slot[newCapacity];
        _occupied = 0; _tombstones = 0;
        for (int i = 0; i < old.Length; i++)
            if (old[i].State == SlotState.Occupied) InsertRaw(old[i].Key, old[i].Value, old[i].LastUsedFrame);
    }

    /// <summary>Drop every entry whose <c>LastUsedFrame</c> is older than <see cref="QuarantineFrames"/> frames ago,
    /// then physically compact the vertex/index slabs so the reclaimed space is contiguous again (a v1 simplification:
    /// eviction is all-eligible-entries-or-nothing rather than a byte-precise "evict exactly enough" sizing — it never
    /// evicts a quarantined entry, which is the one invariant that actually matters; see type doc). Only ever called
    /// from <see cref="BeginFrame"/>.</summary>
    private void CompactEvictingStale()
    {
        ulong protectFrom = _currentFrame > QuarantineFrames ? _currentFrame - QuarantineFrames : 0;

        var keep = new List<(PathRealizationKey Key, PathRef Value, ulong LastUsed)>(_occupied);
        int evicted = 0;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].State != SlotState.Occupied) continue;
            if (_slots[i].LastUsedFrame >= protectFrom) keep.Add((_slots[i].Key, _slots[i].Value, _slots[i].LastUsedFrame));
            else evicted++;
        }
        if (evicted == 0) return;

        // Preserve relative slab order (stable sort by old VtxStart) so a re-realized neighbor set stays contiguous
        // rather than shuffled — purely cosmetic for correctness, but keeps the slab's locality sane.
        keep.Sort((a, b) => a.Value.VtxStart.CompareTo(b.Value.VtxStart));

        var newVtx = new PathVertex[_vtxSlab.Length];
        var newIdx = new uint[_idxSlab.Length];
        int vc = 0, ic = 0;
        var rebuilt = new (PathRealizationKey Key, PathRef Value, ulong LastUsed)[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            var (k, v, last) = keep[i];
            _vtxSlab.AsSpan(v.VtxStart, v.VtxCount).CopyTo(newVtx.AsSpan(vc));
            _idxSlab.AsSpan(v.IdxStart, v.IdxCount).CopyTo(newIdx.AsSpan(ic));
            rebuilt[i] = (k, new PathRef(vc, v.VtxCount, ic, v.IdxCount, v.Bounds, v.ArcLenPx), last);
            vc += v.VtxCount;
            ic += v.IdxCount;
        }

        _vtxSlab = newVtx; _idxSlab = newIdx; _vtxCursor = vc; _idxCursor = ic;

        _slots = new Slot[_slots.Length];
        _occupied = 0; _tombstones = 0;
        for (int i = 0; i < rebuilt.Length; i++) InsertRaw(rebuilt[i].Key, rebuilt[i].Value, rebuilt[i].LastUsed);

        EvictionCount += evicted;
    }
}
