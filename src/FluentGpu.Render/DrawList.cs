using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

public enum DrawOp : int { FillRoundRect = 1, DrawGlyphRun = 2 }

// POD payloads (unmanaged). Encoded as [int op][payload] in the byte stream.
public readonly record struct FillRoundRectCmd(RectF Rect, CornerRadius4 Radii, ColorF Fill);
public readonly record struct DrawGlyphRunCmd(RectF Bounds, ColorF Color, StringId Text, float FontSize, int Bold);

/// <summary>
/// Flat POD command stream consumed by the RHI (<c>SubmitDrawList</c>). The slice grows a single contiguous buffer;
/// the full engine uses double-buffered arenas + clean-span memcpy. SortKeys are recorded in parallel.
/// </summary>
public sealed class DrawList
{
    private byte[] _buf;
    private int _len;
    private ulong[] _sort;
    private int _sortLen;

    public DrawList(int capacity = 4096)
    {
        _buf = GC.AllocateUninitializedArray<byte>(capacity, pinned: false);
        _sort = new ulong[256];
    }

    public ReadOnlySpan<byte> Bytes => _buf.AsSpan(0, _len);
    public ReadOnlySpan<ulong> SortKeys => _sort.AsSpan(0, _sortLen);
    public int CommandCount { get; private set; }

    public void Reset() { _len = 0; _sortLen = 0; CommandCount = 0; }

    public void FillRoundRect(in RectF rect, in CornerRadius4 radii, in ColorF fill, ulong sortKey = 0)
    {
        WriteOp(DrawOp.FillRoundRect);
        WritePayload(new FillRoundRectCmd(rect, radii, fill));
        PushSort(sortKey);
    }

    public void DrawGlyphRun(in RectF bounds, in ColorF color, StringId text, float fontSize, int bold, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawGlyphRun);
        WritePayload(new DrawGlyphRunCmd(bounds, color, text, fontSize, bold));
        PushSort(sortKey);
    }

    private void WriteOp(DrawOp op)
    {
        Ensure(sizeof(int));
        int v = (int)op;
        MemoryMarshal.Write(_buf.AsSpan(_len), in v);
        _len += sizeof(int);
        CommandCount++;
    }

    private void WritePayload<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        Ensure(size);
        MemoryMarshal.Write(_buf.AsSpan(_len), in value);
        _len += size;
    }

    private void PushSort(ulong key)
    {
        if (_sortLen == _sort.Length) Array.Resize(ref _sort, _sort.Length * 2);
        _sort[_sortLen++] = key;
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int n = _buf.Length * 2;
        while (n < _len + extra) n *= 2;
        var nb = GC.AllocateUninitializedArray<byte>(n, pinned: false);
        Array.Copy(_buf, nb, _len);
        _buf = nb;
    }
}
