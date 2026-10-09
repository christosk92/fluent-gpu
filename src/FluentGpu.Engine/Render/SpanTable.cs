using FluentGpu.Foundation;

namespace FluentGpu.Render;

[Flags]
public enum SpanReuseDisabledReason : uint
{
    None = 0,
    FirstRecord = 1u << 0,
    SceneChanged = 1u << 1,
    Layout = 1u << 2,
    Resize = 1u << 3,
    ModalPaint = 1u << 4,
    PopupWindows = 1u << 5,
    DragGhost = 1u << 6,
    Overlays = 1u << 7,
    Orphans = 1u << 8,
    Detached = 1u << 9,
    ImageContent = 1u << 10,
    DragSpotlight = 1u << 11,
    PathSlab = 1u << 12,   // PathRealizationCache compacted since the table last recorded: stored spans index moved slab offsets
}

public readonly record struct DrawSpan(
    int ByteStart,
    int ByteLength,
    int SortStart,
    int SortCount,
    int CommandCount,
    DrawListOpcodeStats OpcodeStats,
    Affine2D World,
    RectF SubtreeBounds,
    bool ClipComplete);

/// <summary>Per-node prior-frame DrawList span metadata. The DrawList owns the byte/sort arenas; this table owns only
/// offsets and validation keys, so a clean subtree can memcpy its previous commands without re-walking descendants.
/// <para>Validity is per ARENA BUFFER, not per frame (retained tiles P1): every span records the <c>bufGen</c> of the
/// arena buffer it was written into (a process-unique number the slice recorder assigns on every arena swap) and the
/// slice slot that arena belongs to. A copy is legal only from the buffer that is the arena's PRIOR one now — so a
/// slice the recorder KEPT untouched for twenty frames still copies its clean rows on the frame it next re-records,
/// and a span can never be replayed out of another slice's arena.</para></summary>
public sealed class SpanTable
{
    private uint[] _gen;
    private uint[] _frame;
    private ulong[] _inputSig;
    private ulong[] _bufGen;
    private int[] _sliceSlot;
    private Affine2D[] _world;
    private int[] _byteStart;
    private int[] _byteLength;
    private int[] _sortStart;
    private int[] _sortCount;
    private int[] _commandCount;
    private DrawListOpcodeStats[] _opcodeStats;
    private RectF[] _subtreeBounds;
    private RectF[] _selfBounds;
    private bool[] _clipComplete;
    private bool[] _culled;
    // Spatial span-reuse scoping (scene-memory.md): per-node BLOCK stamp. stamp == the current record frame ⇒ this node's
    // stored span could go stale (a special-cased visual lives inside its subtree) ⇒ deny reuse AND skip the store. Stale
    // stamps from prior frames read as unblocked, so no per-frame clear is needed (the _frame/_frameId pattern).
    private uint[] _blockStamp;
    private uint _frameId;

    public SpanTable(int capacity = 64)
    {
        if (capacity < 4) capacity = 4;
        _gen = new uint[capacity];
        _frame = new uint[capacity];
        _inputSig = new ulong[capacity];
        _bufGen = new ulong[capacity];
        _sliceSlot = new int[capacity];
        _world = new Affine2D[capacity];
        _byteStart = new int[capacity];
        _byteLength = new int[capacity];
        _sortStart = new int[capacity];
        _sortCount = new int[capacity];
        _commandCount = new int[capacity];
        _opcodeStats = new DrawListOpcodeStats[capacity];
        _subtreeBounds = new RectF[capacity];
        _selfBounds = new RectF[capacity];
        _clipComplete = new bool[capacity];
        _culled = new bool[capacity];
        _blockStamp = new uint[capacity];
    }

    public bool HasPrior => _frameId > 1;

    public uint BeginFrame(int capacity)
    {
        EnsureCapacity(capacity);
        _frameId++;
        if (_frameId == 0)
        {
            Array.Clear(_frame);
            Array.Clear(_blockStamp);   // wrap: a stale stamp must never falsely equal the reset frame id (1)
            _frameId = 1;
        }
        return _frameId;
    }

    /// <summary>The span of <paramref name="nodeIndex"/> when it may be copied out of the buffer generation
    /// <paramref name="bufGen"/> (the arena's PRIOR buffer for a re-record, its CURRENT one for a slice kept whole) under
    /// input signature <paramref name="inputSig"/>. A culled entry has no bytes and never answers.</summary>
    public bool TryGet(int nodeIndex, uint gen, ulong bufGen, ulong inputSig, out DrawSpan span)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || bufGen == 0
            || _culled[nodeIndex] || _gen[nodeIndex] != gen || _bufGen[nodeIndex] != bufGen || _inputSig[nodeIndex] != inputSig)
        {
            span = default;
            return false;
        }

        span = new DrawSpan(
            _byteStart[nodeIndex],
            _byteLength[nodeIndex],
            _sortStart[nodeIndex],
            _sortCount[nodeIndex],
            _commandCount[nodeIndex],
            _opcodeStats[nodeIndex],
            _world[nodeIndex],
            _subtreeBounds[nodeIndex],
            _clipComplete[nodeIndex]);
        return true;
    }

    /// <summary>Why <see cref="TryGet"/> would miss for (<paramref name="nodeIndex"/>, <paramref name="gen"/>) under
    /// <paramref name="bufGen"/> / <paramref name="inputSig"/> — a READ-ONLY probe for the walk ledger
    /// (docs/plans/evidence-diagnostics-implementation.md §A.4), never a reuse decision: 0 = it would HIT; 1 = no span
    /// stored for this node in that buffer (none at all, another generation, culled, or stored into another buffer);
    /// 2 = a span IS stored for this node in that buffer but under a different input signature (the subtree is clean and
    /// its bytes are there — an input such as its inherited opacity changed).</summary>
    public byte ClassifyMiss(int nodeIndex, uint gen, ulong bufGen, ulong inputSig)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || bufGen == 0 || _culled[nodeIndex] || _gen[nodeIndex] != gen
            || _bufGen[nodeIndex] != bufGen) return 1;
        return _inputSig[nodeIndex] != inputSig ? (byte)2 : (byte)0;
    }

    public bool TryGetSubtree(int nodeIndex, uint gen, uint frameId, out Affine2D world, out RectF subtreeBounds)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || frameId <= 1
            || _gen[nodeIndex] != gen || _frame[nodeIndex] != frameId - 1)
        {
            world = default;
            subtreeBounds = default;
            return false;
        }

        world = _world[nodeIndex];
        subtreeBounds = _subtreeBounds[nodeIndex];
        return true;
    }

    /// <summary>Repaint damage (gpu-renderer.md §13.1): the extent this node was LAST RECORDED at — its stored
    /// <see cref="DrawSpan.SubtreeBounds"/>, in the space of the slice it was recorded into (<see cref="SliceSlotOf"/>),
    /// with every shadow/self-blur halo and focus ring already folded in. Paired with the node's CURRENT bounds this gives
    /// the old∪new repaint band.
    /// <para>Deliberately NOT gated on frame recency, unlike <see cref="TryGet"/>/<see cref="TryGetSubtree"/>. Those ask
    /// "may I replay these bytes?"; this asks "where are this node's pixels right now?". Since the translated-copy branch
    /// was deleted (retained tiles P1) nothing moves a node's recorded pixels inside its slice without re-recording it —
    /// an ancestor's exact copy lands them where they were, and a slice's own motion is a composite parameter the caller
    /// maps separately — so a carried-over extent is EXACT, however many frames old.</para>
    /// <para>The slot is overwritten by THIS frame's <see cref="Store"/>, so the recorder must read it BEFORE it
    /// re-records the node.</para>
    /// <paramref name="fresh"/> is true when the extent was refreshed on the previous frame (diagnostic only). A
    /// <c>false</c> RETURN means the node has never stored a span under this generation: brand new, nothing was presented
    /// behind it, its current bounds are the whole truth.</summary>
    public bool TryGetPriorExtent(int nodeIndex, uint gen, uint frameId, out RectF prior, out bool fresh)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || _gen[nodeIndex] != gen || _frame[nodeIndex] == 0)
        {
            prior = default;
            fresh = false;
            return false;
        }
        fresh = frameId > 1 && _frame[nodeIndex] == frameId - 1;
        prior = _subtreeBounds[nodeIndex];
        return true;
    }

    /// <summary>Record the node's OWN visual extent (its box + self-blur halo, in its slice's space) alongside the span a
    /// re-record just stored — the geometry-damage baseline (<see cref="TryGetPriorGeometry"/>).</summary>
    public void StoreSelf(int nodeIndex, in RectF selfBounds)
    {
        if ((uint)nodeIndex < (uint)_selfBounds.Length) _selfBounds[nodeIndex] = selfBounds;
    }

    /// <summary>Where <paramref name="nodeIndex"/> last presented, in the space of <paramref name="sliceSlot"/>: its world
    /// transform, its own visual extent and its subtree's. The recorder compares them with this frame's to damage exactly
    /// what a layout pass moved or resized (retained tiles — a re-window must not damage rows that stayed put).</summary>
    public bool TryGetPriorGeometry(int nodeIndex, uint gen, out Affine2D world, out RectF self, out RectF subtree, out int sliceSlot)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || _gen[nodeIndex] != gen || _frame[nodeIndex] == 0)
        {
            world = default; self = default; subtree = default; sliceSlot = -1;
            return false;
        }
        world = _world[nodeIndex];
        self = _selfBounds[nodeIndex];
        subtree = _subtreeBounds[nodeIndex];
        sliceSlot = _sliceSlot[nodeIndex];
        return true;
    }

    /// <summary>The slice slot whose arena this node's span was last stored in (−1 = never, under this generation) —
    /// the space its <see cref="TryGetPriorExtent"/> rect is expressed in.</summary>
    public int SliceSlotOf(int nodeIndex, uint gen)
        => (uint)nodeIndex < (uint)_gen.Length && _gen[nodeIndex] == gen && _frame[nodeIndex] != 0 ? _sliceSlot[nodeIndex] : -1;

    public void Store(int nodeIndex, uint gen, uint frameId, ulong inputSig, in DrawSpan span, ulong bufGen, int sliceSlot)
    {
        EnsureCapacity(nodeIndex + 1);
        _gen[nodeIndex] = gen;
        _frame[nodeIndex] = frameId;
        _inputSig[nodeIndex] = inputSig;
        _bufGen[nodeIndex] = bufGen;
        _sliceSlot[nodeIndex] = sliceSlot;
        _world[nodeIndex] = span.World;
        _byteStart[nodeIndex] = span.ByteStart;
        _byteLength[nodeIndex] = span.ByteLength;
        _sortStart[nodeIndex] = span.SortStart;
        _sortCount[nodeIndex] = span.SortCount;
        _commandCount[nodeIndex] = span.CommandCount;
        _opcodeStats[nodeIndex] = span.OpcodeStats;
        _subtreeBounds[nodeIndex] = span.SubtreeBounds;
        _clipComplete[nodeIndex] = span.ClipComplete;
        _culled[nodeIndex] = false;
    }

    public void StoreCulled(int nodeIndex, uint gen, uint frameId, in Affine2D world, in RectF subtreeBounds, int sliceSlot)
    {
        EnsureCapacity(nodeIndex + 1);
        // the node's own extent rides the same translation as its subtree
        RectF self = _selfBounds[nodeIndex];
        if (_gen[nodeIndex] == gen && !self.IsEmpty)
            _selfBounds[nodeIndex] = new RectF(self.X + world.Dx - _world[nodeIndex].Dx, self.Y + world.Dy - _world[nodeIndex].Dy, self.W, self.H);
        _gen[nodeIndex] = gen;
        _frame[nodeIndex] = frameId;
        _bufGen[nodeIndex] = 0;
        _sliceSlot[nodeIndex] = sliceSlot;
        _world[nodeIndex] = world;
        _subtreeBounds[nodeIndex] = subtreeBounds;
        _culled[nodeIndex] = true;
    }

    /// <summary>Spatial span-reuse scoping (scene-memory.md): stamp <paramref name="nodeIndex"/> as blocked for
    /// <paramref name="frame"/> — an ancestor of a special-cased visual (popup skipRoot, overlay, exit orphan's visual
    /// parent, connected-anim fly anchor) whose stored bytes could go stale. A blocked node is denied span REUSE and,
    /// crucially, never STORES a span this frame (the not-store-while-blocked safety property): after the special dies
    /// its ancestors simply re-record once — no stale-span resurrection, so a wrong block costs one extra re-record,
    /// never a stale frame.</summary>
    public void MarkBlocked(int nodeIndex, uint frame)
    {
        if ((uint)nodeIndex < (uint)_blockStamp.Length) _blockStamp[nodeIndex] = frame;
    }

    /// <summary>True iff <paramref name="nodeIndex"/> was stamped blocked for <paramref name="frame"/> (== the current
    /// record frame). Stale stamps from prior frames read as unblocked, so no per-frame clear is needed.</summary>
    public bool IsBlocked(int nodeIndex, uint frame)
        => (uint)nodeIndex < (uint)_blockStamp.Length && _blockStamp[nodeIndex] == frame;

    /// <summary>The just-recorded frame id (diagnostics/tests). Pairs with <see cref="StoredAtFrame"/>.</summary>
    public uint CurrentFrameId => _frameId;

    /// <summary>The <see cref="PathRealizationCache.Generation"/> this table's spans were recorded under.</summary>
    public ulong PathSlabGeneration { get; private set; }

    /// <summary>When the path slab compacted since this table last recorded, forget every copyable span. A stored span's
    /// path commands carry raw slab offsets the compaction moved, and a slice arena's keep is the same TryGet. Clearing bufGen
    /// makes TryGet miss for every node until it is re-stored, and keeps the extents that removal damage reads.
    /// True when it forgot.</summary>
    public bool SyncPathSlab(ulong generation)
    {
        if (generation == PathSlabGeneration) return false;
        PathSlabGeneration = generation;
        Array.Clear(_bufGen);
        return true;
    }

    /// <summary>Diagnostics/tests: did <paramref name="nodeIndex"/> get a span STORED (reused, re-recorded, or culled) on
    /// frame <paramref name="frameId"/>? A span-reuse-blocked node stores nothing, so this returns false for it — the
    /// harness assertion behind the not-store-while-blocked property.</summary>
    public bool StoredAtFrame(int nodeIndex, uint frameId)
        => (uint)nodeIndex < (uint)_frame.Length && _frame[nodeIndex] == frameId;

    private void EnsureCapacity(int capacity)
    {
        if (capacity <= _gen.Length) return;
        int n = _gen.Length * 2;
        while (n < capacity) n *= 2;
        Array.Resize(ref _gen, n);
        Array.Resize(ref _frame, n);
        Array.Resize(ref _inputSig, n);
        Array.Resize(ref _bufGen, n);
        Array.Resize(ref _sliceSlot, n);
        Array.Resize(ref _world, n);
        Array.Resize(ref _byteStart, n);
        Array.Resize(ref _byteLength, n);
        Array.Resize(ref _sortStart, n);
        Array.Resize(ref _sortCount, n);
        Array.Resize(ref _commandCount, n);
        Array.Resize(ref _opcodeStats, n);
        Array.Resize(ref _subtreeBounds, n);
        Array.Resize(ref _selfBounds, n);
        Array.Resize(ref _clipComplete, n);
        Array.Resize(ref _culled, n);
        Array.Resize(ref _blockStamp, n);
    }
}
