namespace FluentGpu.Rhi;

/// <summary>Which command-list call was recorded; only the STATE-changing classes that the D3D12 core runtime validates
/// at Close (barriers, render-target binding, clears, viewport/scissor, query resolves, list reset/close). Draw runs
/// are self-evidently well-formed and are not logged per run.</summary>
public enum RecordedOp : byte
{
    None = 0, ListReset, Barrier, SetRenderTarget, SetRenderTargetWithDsv, ClearRtv, ClearDsv, Viewport, Scissor,
    StencilDsvCreated, StencilRef, EndQuery, ResolveQuery, CopyTexture, ListClose,
    RenderPassBegin, RenderPassEnd,
}

/// <summary>
/// Always-on forensic ring (INCIDENT 2026-09, <c>docs/plans/detached-window-render-isolation-implementation.md</c> §2.6):
/// POD, fixed-capacity ring of the last <see cref="Capacity"/> recorded ops. Push is zero-alloc (a struct write);
/// <see cref="Format"/> allocates and runs only on a Close failure. Lives in Engine so it is unit-testable headlessly
/// (the VerticalSlice closure stays TerraFX-free); <c>D3D12Device</c> owns one and writes it from its recording
/// chokepoints, then names the last ops in one <c>[d3d12.forensic]</c> line when <c>ID3D12GraphicsCommandList::Close</c>
/// rejects the list — the only evidence a Release crash leaves (the debug layer never runs in the field).
/// Render-owner-confined like the command list it mirrors: no locking.
/// </summary>
public sealed class RecordedOpRing
{
    public const int Capacity = 64;   // power of two: the head wraps with a mask, never a modulo
    public struct Entry { public RecordedOp Op; public byte Target; public ushort Aux; public uint A; public uint B; }   // 12 B
    private readonly Entry[] _ring = new Entry[Capacity];
    private int _head;
    private ulong _total;

    public ulong Total => _total;
    public int Count => (int)Math.Min(_total, (ulong)Capacity);

    public void Push(RecordedOp op, byte target, uint a = 0, uint b = 0, ushort aux = 0)
    {
        ref Entry e = ref _ring[_head];
        e.Op = op; e.Target = target; e.Aux = aux; e.A = a; e.B = b;
        _head = (_head + 1) & (Capacity - 1);
        _total++;
    }

    /// <summary>For HIGH-RATE ops (scissor, viewport): when the newest entry is the same op on the same target, overwrite
    /// its payload with this call's and bump its <c>Aux</c> as a saturating repeat count, instead of pushing. Draw runs
    /// are not logged, so a frame's dozens of clip changes between two state ops collapse to one entry carrying the
    /// LAST value — without this a single clipped page would flood the ring and evict the one call Close rejected.
    /// A fresh entry starts at <c>Aux = 1</c>. Zero-alloc like <see cref="Push"/>.</summary>
    public void PushCoalesced(RecordedOp op, byte target, uint a = 0, uint b = 0)
    {
        if (_total != 0)
        {
            ref Entry last = ref _ring[(_head - 1) & (Capacity - 1)];
            if (last.Op == op && last.Target == target)
            {
                last.A = a; last.B = b;
                if (last.Aux != ushort.MaxValue) last.Aux++;
                return;
            }
        }
        Push(op, target, a, b, aux: 1);
    }

    /// <summary>Oldest → newest. Index 0 is the oldest retained entry.</summary>
    public Entry At(int i)
    {
        int n = Count;
        if ((uint)i >= (uint)n) throw new ArgumentOutOfRangeException(nameof(i));
        int start = n < Capacity ? 0 : _head;
        return _ring[(start + i) & (Capacity - 1)];
    }

    /// <summary>One line: <c>ops(N/total)=Op#t:A:B:aux|…</c> (<c>#t</c> = target ordinal at record time; A/B hex).
    /// Failure path only (allocates).</summary>
    public void Format(System.Text.StringBuilder sb)
    {
        int n = Count;
        sb.Append("ops(").Append(n).Append('/').Append(_total).Append(")=");
        for (int i = 0; i < n; i++)
        {
            Entry e = At(i);
            if (i != 0) sb.Append('|');
            sb.Append(e.Op).Append('#').Append(e.Target).Append(':').Append(e.A.ToString("X")).Append(':').Append(e.B.ToString("X"));
            if (e.Aux != 0) sb.Append(':').Append(e.Aux);
        }
    }
}
