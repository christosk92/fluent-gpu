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
/// offsets and validation keys, so a clean subtree can memcpy its previous commands without re-walking descendants.</summary>
public sealed class SpanTable
{
    private uint[] _gen;
    private uint[] _frame;
    private ulong[] _inputSig;
    private ulong[] _moveSig;
    private Affine2D[] _world;
    private int[] _byteStart;
    private int[] _byteLength;
    private int[] _sortStart;
    private int[] _sortCount;
    private int[] _commandCount;
    private DrawListOpcodeStats[] _opcodeStats;
    private RectF[] _subtreeBounds;
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
        _moveSig = new ulong[capacity];
        _world = new Affine2D[capacity];
        _byteStart = new int[capacity];
        _byteLength = new int[capacity];
        _sortStart = new int[capacity];
        _sortCount = new int[capacity];
        _commandCount = new int[capacity];
        _opcodeStats = new DrawListOpcodeStats[capacity];
        _subtreeBounds = new RectF[capacity];
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

    public bool TryGet(int nodeIndex, uint gen, uint frameId, ulong inputSig, out DrawSpan span)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || frameId <= 1)
        {
            span = default;
            return false;
        }

        if (_culled[nodeIndex] || _gen[nodeIndex] != gen || _frame[nodeIndex] != frameId - 1 || _inputSig[nodeIndex] != inputSig)
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

    public bool TryGetTranslated(int nodeIndex, uint gen, uint frameId, ulong moveSig, out DrawSpan span)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || frameId <= 1)
        {
            span = default;
            return false;
        }

        if (_culled[nodeIndex] || _gen[nodeIndex] != gen || _frame[nodeIndex] != frameId - 1 || _moveSig[nodeIndex] != moveSig)
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

    public void Store(int nodeIndex, uint gen, uint frameId, ulong inputSig, ulong moveSig, in DrawSpan span)
    {
        EnsureCapacity(nodeIndex + 1);
        _gen[nodeIndex] = gen;
        _frame[nodeIndex] = frameId;
        _inputSig[nodeIndex] = inputSig;
        _moveSig[nodeIndex] = moveSig;
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

    public void StoreCulled(int nodeIndex, uint gen, uint frameId, in Affine2D world, in RectF subtreeBounds)
    {
        EnsureCapacity(nodeIndex + 1);
        _gen[nodeIndex] = gen;
        _frame[nodeIndex] = frameId;
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
        Array.Resize(ref _moveSig, n);
        Array.Resize(ref _world, n);
        Array.Resize(ref _byteStart, n);
        Array.Resize(ref _byteLength, n);
        Array.Resize(ref _sortStart, n);
        Array.Resize(ref _sortCount, n);
        Array.Resize(ref _commandCount, n);
        Array.Resize(ref _opcodeStats, n);
        Array.Resize(ref _subtreeBounds, n);
        Array.Resize(ref _clipComplete, n);
        Array.Resize(ref _culled, n);
        Array.Resize(ref _blockStamp, n);
    }
}
