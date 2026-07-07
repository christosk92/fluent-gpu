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
    Affine2D World);

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
    }

    public bool HasPrior => _frameId > 1;

    public uint BeginFrame(int capacity)
    {
        EnsureCapacity(capacity);
        _frameId++;
        if (_frameId == 0)
        {
            Array.Clear(_frame);
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

        if (_gen[nodeIndex] != gen || _frame[nodeIndex] != frameId - 1 || _inputSig[nodeIndex] != inputSig)
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
            _world[nodeIndex]);
        return true;
    }

    public bool TryGetTranslated(int nodeIndex, uint gen, uint frameId, ulong moveSig, out DrawSpan span)
    {
        if ((uint)nodeIndex >= (uint)_gen.Length || frameId <= 1)
        {
            span = default;
            return false;
        }

        if (_gen[nodeIndex] != gen || _frame[nodeIndex] != frameId - 1 || _moveSig[nodeIndex] != moveSig)
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
            _world[nodeIndex]);
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
    }

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
    }
}
