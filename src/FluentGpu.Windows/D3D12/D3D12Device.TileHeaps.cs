using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Compaction of the pooled tile heaps (<see cref="TileHeapPool"/>, <see cref="FluentGpu.Render.Tiles.TileMemory"/>). A freed
/// surface returns its run to its heap, and only an EMPTY heap is ever released, so the live surfaces of a session end up
/// scattered over heaps that each hold a little. A compaction copies every surface of the sparsest heap into free runs of the
/// others (one CopyResource each, the same bytes: the surface keeps its serial and write record) so that heap empties:
/// <list type="bullet">
/// <item><b>in frame</b> — while the app composites (it may never go idle: playback animates every turn), at the start of a
/// turn, before any raster, when the free runs inside partly used heaps exceed
/// <see cref="FluentGpu.Render.Tiles.TileMemory.FragmentCapHeaps"/> heaps' worth (at most once per
/// <see cref="FluentGpu.Render.Tiles.TileMemory.FrameCompactSpacingMs"/>), in the turn's own command list;</item>
/// <item><b>idle</b> — once nothing has composited for <see cref="FluentGpu.Render.Tiles.TileMemory.HeapIdleReleaseMs"/>, on a
/// list of its own (the GPU is idle), one heap per idle tick until the live surfaces no longer fit in fewer heaps.</item>
/// </list>
/// </summary>
public sealed unsafe partial class D3D12Device
{
    private D3D12_RESOURCE_BARRIER[] _moveBarriers = new D3D12_RESOURCE_BARRIER[64];

    /// <summary>The in-frame compaction (<see cref="SurfacePool.PlanFrameCompaction"/>): at the start of a composite turn, before
    /// any raster, copy the sparsest heap's surfaces into free runs of the others inside THIS turn's command list; the old
    /// textures retire behind this turn's fence (and any earlier frame still sampling them).</summary>
    private void CompactTileHeapsInFrame(ulong frameFence)
    {
        ReadOnlySpan<SurfacePool.HeapMove> moves = _surfaces!.PlanFrameCompaction(Environment.TickCount64);
        if (moves.IsEmpty) return;
        EndPassIfOpen();
        RecordMoves(moves);
        _surfaces.CommitMoves(moves, frameFence);
        FrameCompactions++;
        if (FluentGpu.Render.Tiles.TileMemory.HeapLog) Console.Error.WriteLine($"[tile-heaps] frame-compact moved={moves.Length} fence={frameFence} t={Environment.TickCount64}");
    }

    /// <summary>Surfaces moved by in-frame compactions (diagnostics).</summary>
    internal long FrameCompactions { get; private set; }

    private void RecordMoves(ReadOnlySpan<SurfacePool.HeapMove> moves)
    {
        if (_moveBarriers.Length < moves.Length * 2) _moveBarriers = new D3D12_RESOURCE_BARRIER[moves.Length * 2];
        int nb = 0;
        for (int i = 0; i < moves.Length; i++)
        {
            // the new texture is placed on memory another surface used: alias it in (it was created in COPY_DEST; a full copy is
            // its initialization); the old one becomes the copy source
            D3D12_RESOURCE_BARRIER ab = default;
            ab.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_ALIASING;
            ab.Anonymous.Aliasing.pResourceAfter = moves[i].New;
            _moveBarriers[nb++] = ab;
            if (moves[i].OldState != D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE)
                _moveBarriers[nb++] = Transition(moves[i].Old, moves[i].OldState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        }
        fixed (D3D12_RESOURCE_BARRIER* p = _moveBarriers) _cmdList->ResourceBarrier((uint)nb, p);
        for (int i = 0; i < moves.Length; i++) _cmdList->CopyResource(moves[i].New, moves[i].Old);
        nb = 0;
        for (int i = 0; i < moves.Length; i++)
            _moveBarriers[nb++] = Transition(moves[i].New, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        fixed (D3D12_RESOURCE_BARRIER* p = _moveBarriers) _cmdList->ResourceBarrier((uint)nb, p);
    }

    /// <summary>Render thread, between turns (<see cref="TrimIdleResources"/>). Returns the ms until the idle path should look
    /// again (a heap was emptied: release it next tick), or −1 when nothing moved.</summary>
    private int CompactTileHeapsIdle(long nowMs)
    {
        if (_surfaces is not { } sp || _primarySwapchain is not { Disposed: false } primary) return -1;
        ReadOnlySpan<SurfacePool.HeapMove> moves = sp.PlanCompaction(nowMs);
        if (moves.IsEmpty) return -1;
        BeginTargetFrame(primary.Frame);
        WaitForGpu();   // idle: nothing is in flight, and the ring slot's allocator is free to reset (the capture path's rule)
        int slot = _ring.NextSlot;
        Check(_ring.Allocators[slot]->Reset(), "compact.alloc.Reset");
        Check(_cmdList->Reset(_ring.Allocators[slot], null), "compact.cmd.Reset");
        InvalidateCmdState();
        Rec(RecordedOp.ListReset, (uint)slot, _w << 16 | (_h & 0xFFFF), aux: 2);   // aux=2: the heap compaction list
        RecordMoves(moves);
        Check(_cmdList->Close(), "compact.cmd.Close");
        ID3D12CommandList* list = (ID3D12CommandList*)_cmdList;
        _queue->ExecuteCommandLists(1, &list);
        WaitForGpu();
        ulong done = _fenceValue;
        EndTargetFrame();
        sp.CommitMoves(moves, done);
        sp.DrainRetired(done);
        if (FluentGpu.Render.Tiles.TileMemory.HeapLog) Console.Error.WriteLine($"[tile-heaps] idle-compact moved={moves.Length} t={Environment.TickCount64}");   // the copies are done: the old textures release, their runs free, the heap is empty
        return (int)FluentGpu.Render.Tiles.TileMemory.HeapReleaseSpacingMs;
    }
}
