using System.Runtime.CompilerServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using Gen = FluentGpu.Interop.Generated;

namespace FluentGpu.Rhi.D3D12;

// ── The composite's CLOCK-INDEPENDENT work census (audit PERF r3 COMP) ─────────────────────────────────────────────────
//
// GPU milliseconds are work divided by a clock the governor picks: on the owner's Adreno X1-85 the core clock steps through
// its OPP ladder with load, and the bus clock with the whole platform's power state, so the same frame measures 2x apart
// from one minute to the next. Work is proven in counts instead. While the pass timeline is on (GpuPassTimingEnabled) a
// composite submit brackets its three phases — tile raster, offscreen, back-buffer composite — with one D3D12
// PIPELINE_STATISTICS query each (never nested, each begun and ended outside a render pass), resolved with the timeline's
// timestamps and read back on the same one-submission-later schedule (the ring slot's own fence wait). Beside them, the
// CPU's count of what the submit encoded: render passes, draws, and the composite pass's draws and quad pixels (each quad
// ∩ its scissor — the CPU's estimate of the composite's PS invocations). Published as GpuFrameWork with the timeline.
public sealed unsafe partial class D3D12Device
{
    private const uint WorkPhases = 3;   // tile raster, offscreen, composite
    private volatile bool _workCensusEnabled;
    /// <summary>Pipeline-statistics work census on (with the pass timeline only; any thread). Off by default: the queries cost the
    /// Adreno's driver GPU time of their own, so a timing run leaves them off and a work run turns them on.</summary>
    public bool GpuWorkCensusEnabled { get => _workCensusEnabled; set => _workCensusEnabled = value; }
    private ID3D12QueryHeap* _workHeap;
    private ID3D12Resource* _workReadback;
    private D3D12_QUERY_DATA_PIPELINE_STATISTICS* _workData;
    private bool _workInitTried;
    private int _workOpen = -1;          // the phase whose query is open (−1 = none)
    private int _workIssued;             // bit per phase: begun and ended this submit
    private readonly int[] _workIssuedSlot = new int[SubmissionRing.Depth];
    private readonly GpuFrameWork[] _workSlot = new GpuFrameWork[SubmissionRing.Depth];
    // the composite pass's draws and quad pixels (CompositeDrawCensus), this submit
    private bool _compositeCensusOn;
    private int _frameCompDraws;
    private long _frameCompPx, _frameCompFeatherPx, _frameRepaintPx;

    private void EnsureWorkQueries()
    {
        if (_workInitTried) return;
        _workInitTried = true;
        D3D12_QUERY_HEAP_DESC qd = default;
        qd.Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_PIPELINE_STATISTICS;
        qd.Count = WorkPhases * SubmissionRing.Depth;
        ID3D12QueryHeap* heap;
        if (_device->CreateQueryHeap(&qd, __uuidof<ID3D12QueryHeap>(), (void**)&heap) < 0) return;
        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = (ulong)(WorkPhases * SubmissionRing.Depth) * (ulong)sizeof(D3D12_QUERY_DATA_PIPELINE_STATISTICS);
        rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        if (_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&readback) < 0)
        {
            heap->Release();
            return;
        }
        void* mapped;
        if (readback->Map(0, null, &mapped) < 0) { readback->Release(); heap->Release(); return; }
        _workHeap = heap;
        _workReadback = readback;
        _workData = (D3D12_QUERY_DATA_PIPELINE_STATISTICS*)mapped;
        D3D12MemoryDiagnostics.Track(readback, "GpuWorkCensus.PipelineStatsReadback", rd.Width);
    }

    /// <summary>A composite phase begins: close the open phase's query and open <paramref name="phase"/>'s (0 tile raster, 1
    /// offscreen, 2 composite). Only while the pass timeline is on for this submit; ends any open render pass first (a query
    /// is begun and ended outside passes — every caller sits at a point where the next work opens its own pass anyway).</summary>
    private void WorkPhase(int phase)
    {
        if (!_passOn || !_workCensusEnabled) return;
        EnsureWorkQueries();
        if (_workHeap == null) return;
        EndPassIfOpen();
        uint b = WorkPhases * (uint)_ringSlot;
        if (_workOpen >= 0)
        {
            _cmdList->EndQuery(_workHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_PIPELINE_STATISTICS, b + (uint)_workOpen);
            _workIssued |= 1 << _workOpen;
        }
        _cmdList->BeginQuery(_workHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_PIPELINE_STATISTICS, b + (uint)phase);
        _workOpen = phase;
    }

    /// <summary>The composite pass ended: close the open phase's query.</summary>
    private void WorkPhaseEnd()
    {
        if (_workOpen < 0 || _workHeap == null) return;
        EndPassIfOpen();
        uint b = WorkPhases * (uint)_ringSlot;
        _cmdList->EndQuery(_workHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_PIPELINE_STATISTICS, b + (uint)_workOpen);
        _workIssued |= 1 << _workOpen;
        _workOpen = -1;
    }

    /// <summary>Submit end (PassEnd, while the timeline is on): resolve the phases this submit issued and bank the CPU's counts
    /// for the one-submission-later publish.</summary>
    private void WorkResolve(int slot)
    {
        WorkPhaseEnd();
        int issued = _workIssued;
        _workIssued = 0;
        _workIssuedSlot[slot] = issued;
        _workSlot[slot] = new GpuFrameWork(0, 0, 0, _frameRenderPasses, GpuDrawCount.Frame, _frameCompDraws, _frameCompPx, _frameCompFeatherPx,
            _frameRepaintPx);
        _frameCompDraws = 0; _frameCompPx = 0; _frameCompFeatherPx = 0;
        if (_workHeap == null) return;
        uint b = WorkPhases * (uint)slot;
        ulong stride = (ulong)sizeof(D3D12_QUERY_DATA_PIPELINE_STATISTICS);
        for (int p = 0; p < WorkPhases; p++)
            if ((issued & (1 << p)) != 0)
                _cmdList->ResolveQueryData(_workHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_PIPELINE_STATISTICS, b + (uint)p, 1, _workReadback, (b + (uint)p) * stride);
    }

    /// <summary>The ring slot's retired census (its fence wait proved the resolve landed), with the counts banked at its submit.</summary>
    private GpuFrameWork CollectWork(int slot)
    {
        GpuFrameWork w = _workSlot[slot];
        int issued = _workIssuedSlot[slot];
        _workIssuedSlot[slot] = 0;
        if (_workData == null || issued == 0) return w;
        uint b = WorkPhases * (uint)slot;
        ulong Ps(int p) => (issued & (1 << p)) != 0 ? _workData[b + (uint)p].PSInvocations : 0UL;
        return w with { PsTileRaster = Ps(0), PsOffscreen = Ps(1), PsComposite = Ps(2) };
    }

    /// <summary>One composite-pass quad (target px, already scissored by <paramref name="scissor"/>): counted while the census is on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CompositeDrawCensus(float x0, float y0, float x1, float y1, in PixelRect scissor, bool feathered)
    {
        if (!_compositeCensusOn) return;
        _frameCompDraws++;
        float w = MathF.Min(x1, scissor.Right) - MathF.Max(x0, scissor.Left);
        float h = MathF.Min(y1, scissor.Bottom) - MathF.Max(y0, scissor.Top);
        if (w <= 0f || h <= 0f) return;
        long a = (long)(w * h);
        _frameCompPx += a;
        if (feathered) _frameCompFeatherPx += a;
    }

    private void ReleaseWorkCensus()
    {
        if (_workReadback != null)
        {
            if (_workData != null) _workReadback->Unmap(0, null);
            D3D12MemoryDiagnostics.Release(_workReadback, "GpuWorkCensus.PipelineStatsReadback");
            _workReadback->Release(); _workReadback = null; _workData = null;
        }
        if (_workHeap != null) { _workHeap->Release(); _workHeap = null; }
        _workInitTried = false; _workOpen = -1; _workIssued = 0;
        Array.Clear(_workIssuedSlot);
    }

    // ── per-item fragment census (diagnostics: which composite item shades how many pixels) ───────────────────────────
    // CompositeItemCensusEvery = N > 0 (the frame bench sets it with the pass timeline): one WHOLE-route composite in N gives
    // every top-level item it draws its own PIPELINE_STATISTICS query (sequential inside the composite pass, so the phase
    // query is left out on that frame and its PS count is the items' sum). Read back once its fence passed and kept as the
    // latest snapshot (CopyCompositeItemCensus), with each item's kind, slice, node, footprint and draws.
    public const int ItemCensusMax = 256;
    /// <summary>One top-level composite item of the latest census frame.</summary>
    public readonly record struct CompositeItemStat(int Item, CompositeKind Kind, int SliceId, int NodeIndex, uint Gen, long FootPx,
        long PsInvocations, int Draws, bool Inline, bool Opaque, bool Hidden, PixelRect Foot = default, Foundation.RectF Clip = default,
        Foundation.RectF OpaqueRect = default);
    private volatile int _itemCensusEvery;
    /// <summary>A whole-route composite in this many is a per-item census frame (0 = off). Any thread.</summary>
    public int CompositeItemCensusEvery { get => _itemCensusEvery; set => _itemCensusEvery = value; }
    private ID3D12QueryHeap* _itemHeap;
    private ID3D12Resource* _itemReadback;
    private D3D12_QUERY_DATA_PIPELINE_STATISTICS* _itemData;
    private bool _itemInitTried, _itemCensusFrame;
    private int _itemCensusCountdown;
    private ulong _itemCensusFence;     // the submit whose results the bank holds (0 = none pending)
    private int _itemCensusN;
    private readonly CompositeItemStat[] _itemPending = new CompositeItemStat[ItemCensusMax];
    private readonly CompositeItemStat[] _itemSnapshot = new CompositeItemStat[ItemCensusMax];
    private int _itemSnapshotN;
    private readonly object _itemSnapshotLock = new();
    private int _itemDraws0;
    /// <summary>One painting op of an INLINE census item: its kind and window-px rect (its declared footprint, unclipped).</summary>
    public readonly record struct CompositeItemOp(int Item, Render.DrawOp Op, float X, float Y, float W, float H,
        float Alpha = 1f, int FillKind = 0, int ClipDepth = 0, Foundation.RectF Clip = default);
    public const int ItemOpsMax = 1024;
    private readonly CompositeItemOp[] _opsPending = new CompositeItemOp[ItemOpsMax];
    private readonly CompositeItemOp[] _opsSnapshot = new CompositeItemOp[ItemOpsMax];
    private int _opsPendingN, _opsSnapshotN;
    private readonly Foundation.RectF[] _censusClips = new Foundation.RectF[32];

    /// <summary>Copy the latest census snapshot's inline ops; returns the count. Any thread.</summary>
    public int CopyCompositeItemOps(Span<CompositeItemOp> dst)
    {
        lock (_itemSnapshotLock)
        {
            int n = Math.Min(dst.Length, _opsSnapshotN);
            _opsSnapshot.AsSpan(0, n).CopyTo(dst);
            return n;
        }
    }

    /// <summary>List an inline census item's painting ops (SliceOpBounds: the footprint the recorder and the replay cull use).</summary>
    private void ItemCensusOps(in CompositeFrame frame, int i, in Render.Tiles.SliceRow row, in CompositeItem it)
    {
        float sc = _frameScale;
        float ox = it.Transform.Dx - row.Frame.OriginX, oy = it.Transform.Dy - row.Frame.OriginY;
        Foundation.RectF[] clips = _censusClips;
        int depth = 0;
        Walk(frame.PrefixOf(in row), list: false);
        Walk(frame.StreamOf(in row), list: true);

        void Walk(ReadOnlySpan<byte> stream, bool list)
        {
            int pos = 0;
            while (pos + sizeof(int) <= stream.Length && _opsPendingN < ItemOpsMax)
            {
                var op = (Render.DrawOp)System.Runtime.InteropServices.MemoryMarshal.Read<int>(stream[pos..]);
                int payload = pos + sizeof(int);
                if (!RepaintStreamSafety.TryBodySize(op, out int body) || payload + body > stream.Length) break;
                ReadOnlySpan<byte> pl = stream.Slice(payload, body);
                if (op == Render.DrawOp.PushClip)
                {
                    var c = System.Runtime.InteropServices.MemoryMarshal.Read<Render.ClipCmd>(pl);
                    if (depth < clips.Length) clips[depth] = c.DeviceRect;
                    depth++;
                }
                else if (op == Render.DrawOp.PopClip) depth = Math.Max(0, depth - 1);
                else if (list && Render.Tiles.SliceOpBounds.TryGet(op, pl, out Foundation.RectF b))
                {
                    float a = 1f; int kind = 0;
                    if (op == Render.DrawOp.FillRoundRect)
                    {
                        var f = System.Runtime.InteropServices.MemoryMarshal.Read<Render.FillRoundRectCmd>(pl);
                        a = f.Fill.A * f.Opacity; kind = f.FillKind;
                    }
                    Foundation.RectF clip = depth > 0 && depth <= clips.Length ? clips[depth - 1] : default;
                    _opsPending[_opsPendingN++] = new CompositeItemOp(i, op, ox + b.X * sc, oy + b.Y * sc, b.W * sc, b.H * sc, a, kind, depth, clip);
                }
                pos = payload + body;
            }
        }
    }

    /// <summary>Copy the latest census snapshot; returns the item count (0 = none taken yet). Any thread.</summary>
    public int CopyCompositeItemCensus(Span<CompositeItemStat> dst)
    {
        lock (_itemSnapshotLock)
        {
            int n = Math.Min(dst.Length, _itemSnapshotN);
            _itemSnapshot.AsSpan(0, n).CopyTo(dst);
            return n;
        }
    }

    private void EnsureItemCensus()
    {
        if (_itemInitTried) return;
        _itemInitTried = true;
        D3D12_QUERY_HEAP_DESC qd = default;
        qd.Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_PIPELINE_STATISTICS;
        qd.Count = ItemCensusMax;
        ID3D12QueryHeap* heap;
        if (_device->CreateQueryHeap(&qd, __uuidof<ID3D12QueryHeap>(), (void**)&heap) < 0) return;
        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = (ulong)ItemCensusMax * (ulong)sizeof(D3D12_QUERY_DATA_PIPELINE_STATISTICS);
        rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        if (_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&readback) < 0)
        { heap->Release(); return; }
        void* mapped;
        if (readback->Map(0, null, &mapped) < 0) { readback->Release(); heap->Release(); return; }
        _itemHeap = heap; _itemReadback = readback; _itemData = (D3D12_QUERY_DATA_PIPELINE_STATISTICS*)mapped;
        D3D12MemoryDiagnostics.Track(readback, "GpuWorkCensus.ItemStatsReadback", rd.Width);
    }

    /// <summary>Before the composite pass (after the repaint plan): is this a census frame? Publishes a finished one first.</summary>
    private bool ItemCensusDecide()
    {
        ItemCensusCollect();
        _itemCensusFrame = false;
        int every = _itemCensusEvery;
        if (every <= 0 || !_passOn || !_workCensusEnabled || _ppPartial || _itemCensusFence != 0) return false;
        if (--_itemCensusCountdown > 0) return false;
        EnsureItemCensus();
        if (_itemHeap == null) return false;
        _itemCensusCountdown = every;
        _itemCensusFrame = true;
        _itemCensusN = 0;
        _opsPendingN = 0;
        return true;
    }

    private void ItemCensusCollect()
    {
        if (_itemCensusFence == 0 || Gen.ID3D12FenceVtbl.GetCompletedValue(_fence) < _itemCensusFence) return;
        lock (_itemSnapshotLock)
        {
            for (int k = 0; k < _itemCensusN; k++) _itemSnapshot[k] = _itemPending[k] with { PsInvocations = (long)_itemData[k].PSInvocations };
            _itemSnapshotN = _itemCensusN;
            _opsPending.AsSpan(0, _opsPendingN).CopyTo(_opsSnapshot);
            _opsSnapshotN = _opsPendingN;
        }
        _itemCensusFence = 0;
    }

    /// <summary>A top-level item of a census frame is about to draw: open its query (−1 = not counted).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ItemCensusBegin()
    {
        if (!_itemCensusFrame || _itemCensusN >= ItemCensusMax) return -1;
        _cmdList->BeginQuery(_itemHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_PIPELINE_STATISTICS, (uint)_itemCensusN);
        _itemDraws0 = GpuDrawCount.Frame;
        return _itemCensusN;
    }

    private void ItemCensusEnd(int q, in CompositeFrame frame, int i)
    {
        if (q < 0) return;
        _cmdList->EndQuery(_itemHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_PIPELINE_STATISTICS, (uint)q);
        ref readonly CompositeItem it = ref frame.Items[i];
        int r = RowOf(it.SliceId);
        int node = r >= 0 ? frame.Slices[r].NodeIndex : -1;
        uint gen = r >= 0 ? frame.Slices[r].Gen : 0;
        PixelRect f = (uint)i < (uint)_itemFoot.Length ? _itemFoot[i] : default;
        long foot = f.IsEmpty ? 0 : (long)(f.Right - f.Left) * (f.Bottom - f.Top);
        _itemPending[q] = new CompositeItemStat(i, it.Kind, it.SliceId, node, gen, foot, 0, GpuDrawCount.Frame - _itemDraws0,
            InlineDirect(in it), it.Opaque.W > 0f && it.Opaque.H > 0f, false, f, it.Clip, it.Opaque);
        if (r >= 0 && InlineDirect(in it)) ItemCensusOps(in frame, i, in frame.Slices[r], in it);
        _itemCensusN = q + 1;
    }

    /// <summary>The census frame's composite pass ended: resolve its item queries and remember the submit's fence.</summary>
    private void ItemCensusResolve()
    {
        if (!_itemCensusFrame) return;
        _itemCensusFrame = false;
        if (_itemCensusN == 0) return;
        _cmdList->ResolveQueryData(_itemHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_PIPELINE_STATISTICS, 0, (uint)_itemCensusN, _itemReadback, 0);
        _itemCensusFence = _fenceValue + 1;
    }
}
