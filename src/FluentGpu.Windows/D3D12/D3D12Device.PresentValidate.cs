using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Gen = FluentGpu.Interop.Generated;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Cumulative <c>--fg present-validate</c> census (render thread writes, diagnostics read): composites checked,
/// back buffers that differed from a whole shadow composite, presents whose dirty rects missed a changed pixel, presents
/// after which the modelled screen differed from the back buffer, and composites not checked.</summary>
public readonly record struct PresentCensus(long Checked, long CompositeMismatches, long UnderReports, long StaleScreens, long Skipped,
    long ShadowDiverged = 0, long ModelChecked = 0);

// PRESENT VALIDATION (`--fg present-validate`, gpu-renderer.md §13.1h). After every primary composite the device
//   1. re-composites the WHOLE frame (CLEAR + every item, no repaint rects) into a window-sized shadow scratch and copies it
//      and the back buffer to readback buffers;
//   2. once the GPU passed the frame, compares them byte for byte — a difference is a back buffer the PRESERVE route got
//      wrong (a repaint set that missed a changed pixel, an item culled out of a rect it reaches);
//   3. keeps a CPU model of what DWM shows: a whole present replaces it, a Present1 replaces it inside the dirty rects only.
//      Every changed pixel (back buffer vs the last presented one) outside the rects is an UNDER-REPORT, and a model that
//      differs from the back buffer after a present is a STALE SCREEN — exactly what a user would see.
// Every composite is checked in order (a full ring waits for its oldest check: the validator slows the frame, it never
// drops one, so the model has no gaps). Debug arm: several window-sized readbacks and a CPU compare per frame.
public sealed unsafe partial class D3D12Device
{
    private struct PresentCheck
    {
        public ID3D12Resource* Back, Shadow;
        public ulong Bytes, Fence;
        public uint Pitch;
        public int W, H, Turn;
        public long Seq;
        public bool Busy, Partial;
        public byte PresentMode;            // 0 = not presented (yet), 1 = whole, 2 = dirty rects
        public RECT[] Rects;
        public int RectCount;
        public int ShadowDraws, Items, ShadowPasses;
        public bool StillOnShadow;
    }

    private readonly PresentCheck[] _pvChecks = new PresentCheck[3];

    // Forensics for a mismatch: per composite (ring by sequence) the route, the dirty and repaint rects and every item's
    // footprint, so a bad pixel names the items over it in the turns that led to it.
    private sealed class PvMeta
    {
        public long Seq = -1;
        public int Turn;
        public bool Partial, DirtyFull;
        public PixelRect[] Dirty = new PixelRect[RepaintDamageRegion.MaxRects], Repaint = new PixelRect[RepaintDamageRegion.MaxRects];
        public int DirtyN, RepaintN, ItemN;
        public PixelRect[] Foot = new PixelRect[64];
        public CompositeKind[] Kind = new CompositeKind[64];
        public int[] Slice = new int[64];
        public float[] Alpha = new float[64];
    }
    private readonly PvMeta[] _pvMeta = [new(), new(), new(), new(), new(), new(), new(), new()];

    private void PvCaptureMeta(in CompositeFrame frame, long seq)
    {
        PvMeta m = _pvMeta[seq % _pvMeta.Length];
        m.Seq = seq; m.Turn = _compositeTurn; m.Partial = _ppPartial; m.DirtyFull = _ppDirtyFull;
        ReadOnlySpan<RectF> dr = _ppDirty.AsSpan();
        m.DirtyN = 0;
        for (int k = 0; k < dr.Length && m.DirtyN < m.Dirty.Length; k++) m.Dirty[m.DirtyN++] = PpPx(dr[k]);
        m.RepaintN = 0;
        for (int k = 0; k < _ppRepaintN && k < m.Repaint.Length; k++) m.Repaint[m.RepaintN++] = _ppRepaint[k];
        int n = frame.Items.Length;
        if (m.Foot.Length < n) { m.Foot = new PixelRect[n]; m.Kind = new CompositeKind[n]; m.Slice = new int[n]; m.Alpha = new float[n]; }
        for (int i = 0; i < n; i++)
        {
            m.Foot[i] = i < _itemFoot.Length ? _itemFoot[i] : default;
            m.Kind[i] = frame.Items[i].Kind; m.Slice[i] = frame.Items[i].SliceId; m.Alpha[i] = frame.Items[i].Alpha;
        }
        m.ItemN = n;
    }

    private static bool PvIn(in PixelRect r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    /// <summary>One line per recent composite (oldest first) describing pixel (<paramref name="x"/>, <paramref name="y"/>):
    /// route, whether its dirty / repaint set covered it, the items whose footprint covers it.</summary>
    private void PvForensics(long seq, int x, int y)
    {
        var ci = CultureInfo.InvariantCulture;
        for (long q = seq - _pvMeta.Length + 1; q <= seq; q++)
        {
            if (q < 0) continue;
            PvMeta m = _pvMeta[q % _pvMeta.Length];
            if (m.Seq != q) continue;
            bool inDirty = m.DirtyFull, inRepaint = !m.Partial;
            for (int k = 0; k < m.DirtyN; k++) inDirty |= PvIn(m.Dirty[k], x, y);
            for (int k = 0; k < m.RepaintN; k++) inRepaint |= PvIn(m.Repaint[k], x, y);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < m.ItemN; i++)
                if (PvIn(m.Foot[i], x, y))
                    sb.Append(string.Create(ci, $" #{i}:{m.Kind[i]}/s{m.Slice[i]}/a{m.Alpha[i]:0.###}[{m.Foot[i].Left},{m.Foot[i].Top},{m.Foot[i].Right},{m.Foot[i].Bottom}]"));
            Console.Error.WriteLine(string.Create(ci,
                $"[present-validate]   turn={m.Turn} route={(m.Partial ? "preserve" : "whole")}{(m.DirtyFull ? " dirtyFull" : "")} dirty@px={inDirty} repaint@px={inRepaint} items:{sb}"));
        }
    }

    private long _pvSeq, _pvNextToProcess;
    private int _pvPendingSlot = -1;        // the check queued by the latest primary composite, awaiting its present
    private byte[]? _pvModel;               // what DWM shows (BGRA, W×H), valid after a whole present
    private byte[]? _pvPrev;                // the last presented back buffer
    private int _pvModelW, _pvModelH;
    private bool _pvModelOk;
    private long _pvChecked, _pvCompositeBad, _pvUnder, _pvStale, _pvSkipped;

    /// <summary>The cumulative present-validation census.</summary>
    public PresentCensus LastPresentCensus => new(Volatile.Read(ref _pvChecked), Volatile.Read(ref _pvCompositeBad),
        Volatile.Read(ref _pvUnder), Volatile.Read(ref _pvStale), Volatile.Read(ref _pvSkipped), Volatile.Read(ref _pvShadowDiverged), Volatile.Read(ref _pvModelChecked));

    private long _pvModelChecked;   // Present1 (dirty-rect) presents actually compared against a valid screen model

    private long _pvShadowDiverged;

    /// <summary>SubmitComposite, after the composite pass (the back buffer is RENDER_TARGET, no pass open): shadow-composite
    /// the frame whole and queue both readbacks.</summary>
    private void QueuePresentCheck(in CompositeFrame frame, ID3D12Resource* backBuffer)
    {
        int w = (int)_w, h = (int)_h;
        _pvPendingSlot = -1;   // the previous composite's present (or stand-down) has happened
        if (w <= 0 || h <= 0) { PvSkip(); return; }
        int slot = PvFreeSlot();
        if (slot < 0)
        {
            // the ring is full: wait for the oldest check's frame and compare it (never drop a frame from the model)
            ulong oldest = ulong.MaxValue;
            for (int i = 0; i < _pvChecks.Length; i++) if (_pvChecks[i].Busy) oldest = Math.Min(oldest, _pvChecks[i].Fence);
            if (oldest != ulong.MaxValue) WaitForFenceValue(oldest);
            PollPresentChecks();
            slot = PvFreeSlot();
        }
        ulong fence = _fenceValue + 1;
        if (slot < 0 || !PvEnsureShadow(w, h)) { PvSkip(); return; }

        // the back buffer's readback first (its pass ended; the copy resolves it), then the shadow is the validator's own
        // target (never a pooled scratch: the check must not change what the frame leases)
        EndPassIfOpen();
        ref PresentCheck c = ref _pvChecks[slot];
        D3D12_RESOURCE_DESC desc = default;
        desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = (ulong)w; desc.Height = (uint)h; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp; uint rows; ulong rowBytes; ulong total;
        _device->GetCopyableFootprints(&desc, 0, 1, 0, &fp, &rows, &rowBytes, &total);
        if (c.Back == null || c.Bytes < total)
        {
            if (c.Back != null) { D3D12MemoryDiagnostics.Release(c.Back, "PresentValidate.Readback"); c.Back->Release(); }
            if (c.Shadow != null) { D3D12MemoryDiagnostics.Release(c.Shadow, "PresentValidate.Readback"); c.Shadow->Release(); }
            c.Back = CreateReadback(total, "PresentValidate.Readback");
            c.Shadow = CreateReadback(total, "PresentValidate.Readback");
            c.Bytes = total;
        }
        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        CopyToReadback(backBuffer, c.Back, fp, w, h);
        Barrier(backBuffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);

        // the shadow pass is the real whole-route pass: a CLEAR load-op with the frame's clear colour, then every item
        BeginPass(_pvShadowRtv, w, h, PassLoad.Clear, frame.Info.Clear);
        BindCompositor(w, h);
        bool clipOn = _frameClipOn;
        _frameClipOn = false;
        int draws0 = GpuDrawCount.Frame, passes0 = _frameRenderPasses;
        if ((_frameKnockouts & GpuKnockouts.ClearOnly) == 0) DrawRange(in frame, 0, frame.Items.Length, 0, 0, w, h, -1);
        int shadowDraws = GpuDrawCount.Frame - draws0;
        int passes = _frameRenderPasses - passes0;
        bool stillOnShadow = _inRenderPass && _passRtv.ptr == _pvShadowRtv.ptr;
        _frameClipOn = clipOn;
        EndPassIfOpen();
        Barrier(_pvShadow, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        CopyToReadback(_pvShadow, c.Shadow, fp, w, h);
        Barrier(_pvShadow, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        c.Pitch = fp.Footprint.RowPitch;
        c.W = w; c.H = h; c.Turn = _compositeTurn; c.Seq = _pvSeq++;
        PvCaptureMeta(in frame, c.Seq);
        c.Fence = fence;
        c.Partial = _ppPartial;
        c.PresentMode = 0;
        c.RectCount = 0;
        c.ShadowDraws = shadowDraws; c.Items = frame.Items.Length; c.ShadowPasses = passes; c.StillOnShadow = stillOnShadow;
        c.Busy = true;
        _pvPendingSlot = slot;
        InvalidateCmdState();
    }

    private ID3D12Resource* _pvShadow;
    private ID3D12DescriptorHeap* _pvShadowRtvHeap;
    private D3D12_CPU_DESCRIPTOR_HANDLE _pvShadowRtv;
    private int _pvShadowW, _pvShadowH;

    /// <summary>The validator's own window-sized shadow target (resting in RENDER_TARGET); re-created on a resize, after
    /// the GPU is idle (a debug arm).</summary>
    private bool PvEnsureShadow(int w, int h)
    {
        if (_pvShadow != null && _pvShadowW == w && _pvShadowH == h) return true;
        if (_pvShadowRtvHeap == null)
        {
            D3D12_DESCRIPTOR_HEAP_DESC rh = default;
            rh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
            rh.NumDescriptors = 1;
            ID3D12DescriptorHeap* heap;
            if (_device->CreateDescriptorHeap(&rh, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap) < 0) return false;
            _pvShadowRtvHeap = heap;
            _pvShadowRtv = heap->GetCPUDescriptorHandleForHeapStart();
        }
        if (_pvShadow != null)
        {
            WaitForFenceValue(_fenceValue);   // nothing in flight may still copy from it
            D3D12MemoryDiagnostics.Release(_pvShadow, "PresentValidate.Shadow");
            _pvShadow->Release(); _pvShadow = null;
        }
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = (ulong)w; rd.Height = (uint)h; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        ID3D12Resource* res;
        if (_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, null, __uuidof<ID3D12Resource>(), (void**)&res) < 0) return false;
        D3D12MemoryDiagnostics.Track(res, "PresentValidate.Shadow", (ulong)w * (ulong)h * 4UL);
        _device->CreateRenderTargetView(res, null, _pvShadowRtv);
        _pvShadow = res; _pvShadowW = w; _pvShadowH = h;
        return true;
    }

    private int PvFreeSlot()
    {
        for (int i = 0; i < _pvChecks.Length; i++) if (!_pvChecks[i].Busy) return i;
        return -1;
    }

    /// <summary>A composite left unchecked: the model cannot follow it (it is rebuilt by the next whole present).</summary>
    private void PvSkip()
    {
        Volatile.Write(ref _pvSkipped, _pvSkipped + 1);
        _pvPendingSlot = -1;
        _pvSeq++;   // a hole in the sequence: the in-order walk steps over it and drops the model there
    }

    /// <summary>Present, for the primary target: what DWM was told about the frame the latest composite drew.</summary>
    private void NotePresentForValidation(D3D12Swapchain target, byte mode)
    {
        if (!TileDamage.PresentValidate || !ReferenceEquals(target, _primarySwapchain) || _pvPendingSlot < 0) return;
        ref PresentCheck c = ref _pvChecks[_pvPendingSlot];
        _pvPendingSlot = -1;
        if (!c.Busy) return;
        c.PresentMode = mode;
        if (mode == 2)
        {
            c.Rects ??= new RECT[target.PpPresentRects.Length];
            if (c.Rects.Length < target.PpPresentCount) c.Rects = new RECT[target.PpPresentCount];
            for (int i = 0; i < target.PpPresentCount; i++) c.Rects[i] = target.PpPresentRects[i];
            c.RectCount = target.PpPresentCount;
        }
    }

    /// <summary>Compare every queued check the GPU has finished, in composite order (start of a composite; render thread).</summary>
    private void PollPresentChecks()
    {
        ulong done = Gen.ID3D12FenceVtbl.GetCompletedValue(_fence);
        while (true)
        {
            int slot = -1;
            for (int i = 0; i < _pvChecks.Length; i++)
                if (_pvChecks[i].Busy && _pvChecks[i].Seq == _pvNextToProcess) { slot = i; break; }
            if (slot < 0)
            {
                // a skipped composite left a hole in the sequence: step over it
                if (_pvNextToProcess < _pvSeq && !PvAnyBusyAt(_pvNextToProcess)) { _pvModelOk = false; _pvNextToProcess++; continue; }
                return;
            }
            ref PresentCheck c = ref _pvChecks[slot];
            if (done < c.Fence) return;
            // a check whose present has not run yet (the pending one) is compared once the present noted its mode
            if (slot == _pvPendingSlot) return;
            PvCompare(ref c);
            c.Busy = false;
            _pvNextToProcess++;
        }
    }

    private bool PvAnyBusyAt(long seq)
    {
        for (int i = 0; i < _pvChecks.Length; i++) if (_pvChecks[i].Busy && _pvChecks[i].Seq == seq) return true;
        return false;
    }

    private void PvCompare(ref PresentCheck c)
    {
        void* pa, pb;
        D3D12_RANGE rr = new() { Begin = 0, End = (nuint)c.Bytes };
        if (c.Back->Map(0, &rr, &pa) < 0) return;
        if (c.Shadow->Map(0, &rr, &pb) < 0) { D3D12_RANGE z0 = default; c.Back->Unmap(0, &z0); return; }
        int w = c.W, h = c.H, rowBytes = w * 4;
        var ci = CultureInfo.InvariantCulture;
        Volatile.Write(ref _pvChecked, _pvChecked + 1);

        // 1. the back buffer against a whole composite of the same frame
        // The REPAINT region of this composite (the whole window on the whole route): there the back buffer was drawn just
        // now by the same code as the shadow, so a difference inside it means the two composites of one frame disagree —
        // the frame cannot be checked (a validator / driver divergence). A damage bug can only show OUTSIDE it, where the
        // back buffer kept pixels of an earlier frame.
        PvMeta meta = _pvMeta[c.Seq % _pvMeta.Length];
        bool metaOk = meta.Seq == c.Seq;
        long bad = 0, badIn = 0; int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, maxDelta = 0;
        for (int y = 0; y < h; y++)
        {
            uint* ra = (uint*)((byte*)pa + (ulong)y * c.Pitch), rb = (uint*)((byte*)pb + (ulong)y * c.Pitch);
            for (int x = 0; x < w; x++)
            {
                uint va = ra[x], vb = rb[x];
                if (va == vb) continue;
                bad++;
                bool inRepaint = !c.Partial || !metaOk;
                if (!inRepaint) for (int k = 0; k < meta.RepaintN; k++) if (PvIn(meta.Repaint[k], x, y)) { inRepaint = true; break; }
                if (inRepaint) badIn++;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                for (int k = 0; k < 32; k += 8) maxDelta = Math.Max(maxDelta, Math.Abs((int)((va >> k) & 0xFF) - (int)((vb >> k) & 0xFF)));
            }
        }
        if (badIn > 0)
        {
            // The two composites of this frame disagree INSIDE its repaint region (all of it on the whole route), where the
            // back buffer was drawn by the very same code: the frame cannot be checked. On the Adreno driver the shadow (the
            // second composite of the frame, into its own target) now and then comes back incomplete — empty or cut off
            // after some item — while the back buffer is whole (the dumps; no under-report or stale screen follows; none
            // on WARP). Counted apart as UNVERIFIED, never as a pass.
            Volatile.Write(ref _pvShadowDiverged, _pvShadowDiverged + 1);
            Console.Error.WriteLine(string.Create(ci,
                $"[present-validate] SHADOW-DIVERGED (unverified frame) turn={c.Turn} route={(c.Partial ? "preserve" : "whole")} px={bad} inRepaint={badIn} bbox=[{x0},{y0} → {x1 + 1},{y1 + 1}) maxDelta={maxDelta} shadowDraws={c.ShadowDraws} passes={c.ShadowPasses} onShadow={c.StillOnShadow} items={c.Items}"));
            if (_pvShadowDiverged <= 40) PvDump(c.Turn, pa, pb, c.Pitch, w, h);   // every one (bounded only to protect the disk)
        }
        else if (bad > 0)
        {
            Volatile.Write(ref _pvCompositeBad, _pvCompositeBad + 1);
            Console.Error.WriteLine(string.Create(ci,
                $"[present-validate] COMPOSITE MISMATCH turn={c.Turn} route={(c.Partial ? "preserve" : "whole")} px={bad} bbox=[{x0},{y0} → {x1 + 1},{y1 + 1}) maxDelta={maxDelta} shadowDraws={c.ShadowDraws} passes={c.ShadowPasses} onShadow={c.StillOnShadow} items={c.Items} fence={c.Fence} done={Gen.ID3D12FenceVtbl.GetCompletedValue(_fence)}"));
            if (_pvCompositeBad <= 20) PvForensics(c.Seq, x0, y0);
            if (_pvCompositeBad <= 4) PvDump(c.Turn, pa, pb, c.Pitch, w, h);
        }

        // 2. the present against the modelled screen
        if (c.PresentMode != 0)
        {
            int n = w * h * 4;
            if (_pvModel is null || _pvModel.Length < n || _pvModelW != w || _pvModelH != h)
            {
                _pvModel = new byte[n]; _pvPrev = new byte[n]; _pvModelW = w; _pvModelH = h; _pvModelOk = false;
            }
            if (c.PresentMode == 1)
            {
                for (int y = 0; y < h; y++) new ReadOnlySpan<byte>((byte*)pa + (ulong)y * c.Pitch, rowBytes).CopyTo(_pvModel.AsSpan(y * rowBytes));
                _pvModelOk = true;
            }
            else if (_pvModelOk)
            {
                Volatile.Write(ref _pvModelChecked, _pvModelChecked + 1);
                long under = 0, stale = 0;
                int ux0 = int.MaxValue, uy0 = int.MaxValue, ux1 = -1, uy1 = -1;
                fixed (byte* model = _pvModel)
                fixed (byte* prev = _pvPrev)
                {
                    for (int y = 0; y < h; y++)
                    {
                        uint* ra = (uint*)((byte*)pa + (ulong)y * c.Pitch);
                        uint* rm = (uint*)(model + (long)y * rowBytes), rp = (uint*)(prev + (long)y * rowBytes);
                        for (int x = 0; x < w; x++)
                        {
                            bool named = false;
                            for (int k = 0; k < c.RectCount; k++)
                            {
                                ref RECT r = ref c.Rects[k];
                                if (x >= r.left && x < r.right && y >= r.top && y < r.bottom) { named = true; break; }
                            }
                            if (named) { rm[x] = ra[x]; continue; }   // DWM takes the new pixel
                            if (ra[x] != rp[x])
                            {
                                under++;
                                ux0 = Math.Min(ux0, x); uy0 = Math.Min(uy0, y); ux1 = Math.Max(ux1, x); uy1 = Math.Max(uy1, y);
                            }
                            if (rm[x] != ra[x]) stale++;
                        }
                    }
                }
                if (under > 0)
                {
                    Volatile.Write(ref _pvUnder, _pvUnder + 1);
                    Console.Error.WriteLine(string.Create(ci,
                        $"[present-validate] UNDER-REPORT turn={c.Turn} route={(c.Partial ? "preserve" : "whole")} rects={c.RectCount} changedOutsideRects={under} bbox=[{ux0},{uy0} → {ux1 + 1},{uy1 + 1})"));
                }
                if (stale > 0)
                {
                    Volatile.Write(ref _pvStale, _pvStale + 1);
                    Console.Error.WriteLine(string.Create(ci, $"[present-validate] STALE SCREEN turn={c.Turn} px={stale}"));
                }
            }
            for (int y = 0; y < h; y++) new ReadOnlySpan<byte>((byte*)pa + (ulong)y * c.Pitch, rowBytes).CopyTo(_pvPrev!.AsSpan(y * rowBytes));
        }
        D3D12_RANGE z = default;
        c.Back->Unmap(0, &z); c.Shadow->Unmap(0, &z);
        if (_pvChecked % 200 == 1)
            Console.Error.WriteLine(string.Create(ci,
                $"[present-validate] ok checked={_pvChecked} compositeMismatches={_pvCompositeBad} underReports={_pvUnder} staleScreens={_pvStale} skipped={_pvSkipped} shadowDiverged={_pvShadowDiverged}"));
    }

    /// <summary>The first mismatches' back buffer and shadow as PNGs in the temp folder (fg-pv-TURN-back/shadow.png).</summary>
    private static void PvDump(int turn, void* pa, void* pb, uint pitch, int w, int h)
    {
        try
        {
            var a = new byte[w * h * 4]; var b = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                new ReadOnlySpan<byte>((byte*)pa + (ulong)y * pitch, w * 4).CopyTo(a.AsSpan(y * w * 4));
                new ReadOnlySpan<byte>((byte*)pb + (ulong)y * pitch, w * 4).CopyTo(b.AsSpan(y * w * 4));
            }
            string dir = System.IO.Path.GetTempPath();
            PngWriter.WriteBgra(System.IO.Path.Combine(dir, $"fg-pv-{turn}-back.png"), a, w, h);
            PngWriter.WriteBgra(System.IO.Path.Combine(dir, $"fg-pv-{turn}-shadow.png"), b, w, h);
            Console.Error.WriteLine($"[present-validate]   dumped {dir}fg-pv-{turn}-back.png / -shadow.png");
        }
        catch (Exception ex) { Console.Error.WriteLine("[present-validate]   dump failed: " + ex.Message); }
    }

    /// <summary>Device teardown (the GPU is drained): release the present-validation readbacks.</summary>
    private void ReleasePresentChecks()
    {
        if (_pvShadow != null) { D3D12MemoryDiagnostics.Release(_pvShadow, "PresentValidate.Shadow"); _pvShadow->Release(); _pvShadow = null; }
        if (_pvShadowRtvHeap != null) { _pvShadowRtvHeap->Release(); _pvShadowRtvHeap = null; }
        for (int i = 0; i < _pvChecks.Length; i++)
        {
            ref PresentCheck c = ref _pvChecks[i];
            if (c.Back != null) { D3D12MemoryDiagnostics.Release(c.Back, "PresentValidate.Readback"); c.Back->Release(); }
            if (c.Shadow != null) { D3D12MemoryDiagnostics.Release(c.Shadow, "PresentValidate.Readback"); c.Shadow->Release(); }
            c = default;
        }
        // the sequence restarts: no in-flight check may wait on a fence value the next device never signals
        _pvSeq = 0; _pvNextToProcess = 0; _pvPendingSlot = -1;
        _pvModelOk = false; _pvShadowW = _pvShadowH = 0;
        foreach (PvMeta m in _pvMeta) m.Seq = -1;
    }
}
