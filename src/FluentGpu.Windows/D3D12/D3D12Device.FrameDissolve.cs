using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using Gen = FluentGpu.Interop.Generated;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// A retained, immutable snapshot of a presented frame (E2, <see cref="IFrameSnapshot"/>): a copy of the target's back buffer taken
/// after the capture frame's submit and before its present - premultiplied BGRA WITH its alpha, exactly the pixels that frame
/// flips (alpha-0 video holes and transparent Mica regions stay alpha 0). Its own one-descriptor shader-visible SRV heap, so the
/// dissolve binds it without touching the frame banks. <see cref="Dispose"/> retires it behind the fence of its last GPU use; a device
/// loss or teardown releases it at once (the snapshot then reads as dead and a dissolve from it presents the frame as is).
/// </summary>
internal sealed unsafe class D3D12FrameSnapshot : IFrameSnapshot
{
    internal readonly D3D12Device Device;
    internal readonly uint W, H;
    internal ID3D12Resource* Texture;
    internal ID3D12DescriptorHeap* SrvHeap;
    internal ulong LastUseFence;   // the fence value of the last queue work that reads it (the capture copy, then each dissolve)
    internal bool Disposed;

    internal D3D12FrameSnapshot(D3D12Device device, uint w, uint h) { Device = device; W = w; H = h; }

    public Size2 SizePx => new(W, H);
    public bool PremultipliedAlpha => true;
    /// <summary>False once the GPU resources are gone (disposed, or released by a device loss / teardown).</summary>
    internal bool IsLive => Texture != null;

    public void Dispose() => Device.RetireFrameSnapshot(this);
}

// FRAME DISSOLVE (E2, threading-render-seam.md §10.1, gpu-renderer.md §13.1h) - the D3D12 half of ISwapchain.CaptureSnapshot /
// DissolveNextPresent.
//   Capture: right after the capture frame's submit (before its present) the back buffer is COPIED into a retained texture, on a
//     small command list of its own, so nothing of the frame's recording state or banks is touched.
//   Dissolve: right before a dissolve frame's flip, the back buffer (which holds N, the frame just submitted, composited over the
//     frame's transparent clear) is lerped IN PLACE: one full-target quad of PSLoad samples S and writes (1 - t)·S, and the blend
//     ONE / BLEND_FACTOR(t) adds t·N - out = (1 - t)·S + t·N on all four premultiplied channels (FrameDissolve.Blend). No copy of N.
//   Presents: every dissolve present is WHOLE (no Present1 dirty rects), the present after it is whole too (PpNeedFullPresent:
//     the frame's dirty rects describe N against the previous N, not what DWM shows), and the lerped back buffer forgets its
//     partial-present age (_ppBufTurn = 0), so the FLIP_SEQUENTIAL chain never keeps a stale lerped pixel: the next frame into
//     that buffer is composited whole.
//   Holds, stand-downs: the armed dissolve is one-shot; a held or stood-down present consumes it without drawing anything.
public sealed unsafe partial class D3D12Device
{
    private readonly ID3D12CommandAllocator*[] _fdAllocs = new ID3D12CommandAllocator*[2];
    private readonly ulong[] _fdAllocFence = new ulong[2];
    private int _fdNext;
    private ID3D12GraphicsCommandList* _fdList;
    private readonly List<D3D12FrameSnapshot> _fdLive = new(4);      // captured, not disposed
    private readonly List<D3D12FrameSnapshot> _fdRetired = new(4);   // disposed, waiting for their last GPU use to retire

    /// <summary>Snapshots holding GPU memory: live ones plus disposed ones still waiting on the fence (the freeze's one extra texture
    /// per frozen window). Render-owner read.</summary>
    internal int FrameSnapshotsHeld => _fdLive.Count + _fdRetired.Count;

    /// <summary>Presents of any target that were dissolves, since the device was created.</summary>
    internal long DissolvedPresentCount { get; private set; }

    /// <summary><see cref="ISwapchain.CaptureSnapshot"/>: copy the back buffer the last submit of <paramref name="sc"/> drew into a
    /// retained texture. Null when the target or device cannot (disposed, lost, out of memory) - the host then thaws as a cut.</summary>
    internal IFrameSnapshot? CaptureFrameSnapshot(D3D12Swapchain sc)
    {
        AssertSubmitThread();
        if (sc.Disposed || _device == null || sc.SwapChain == null || sc.W == 0 || sc.H == 0) return null;
        DrainRetiredFrameSnapshots();
        TargetFrameState f = sc.Frame;
        uint idx = f.FrameIndex;
        ID3D12Resource* back = idx < FRAME_COUNT ? sc.BackBuffers[idx] : null;
        if (back == null) return null;

        var snap = new D3D12FrameSnapshot(this, sc.W, sc.H);
        try
        {
            D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
            D3D12_RESOURCE_DESC desc = default;
            desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            desc.Width = sc.W; desc.Height = sc.H; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
            desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
            ID3D12Resource* tex;
            Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &desc,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&tex), "FreezeSnapshot.Texture");
            snap.Texture = tex;
            D3D12MemoryDiagnostics.Track(tex, "Freeze.Snapshot", (ulong)sc.W * sc.H * 4);

            D3D12_DESCRIPTOR_HEAP_DESC hd = default;
            hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
            hd.NumDescriptors = 1;
            hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
            ID3D12DescriptorHeap* heap;
            Check(_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "FreezeSnapshot.SrvHeap");
            snap.SrvHeap = heap;
            D3D12MemoryDiagnostics.Track(heap, "Freeze.SnapshotSrv", 64);
            _device->CreateShaderResourceView(tex, null, heap->GetCPUDescriptorHandleForHeapStart());

            int slot = BeginDissolveList();
            if (slot < 0) { ReleaseSnapshotResources(snap); return null; }
            FdBarrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
            _fdList->CopyResource(tex, back);
            FdBarrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
            FdBarrier(tex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            ulong v = EndDissolveList(slot);
            snap.LastUseFence = v;
            NoteTargetQueueWork(f, idx, v);
        }
        catch (InvalidOperationException e)
        {
            Diag.Line("[d3d12.freeze] snapshot capture failed (" + e.Message + "): the thaw is a cut");
            ReleaseSnapshotResources(snap);
            return null;
        }
        _fdLive.Add(snap);
        Diag.Count("d3d12", "freezeSnapshots");
        return snap;
    }

    /// <summary>The armed dissolve of this Present, applied right before the flip: lerp the back buffer toward the snapshot in place
    /// (see the file header). True when the back buffer now holds the dissolve (including a re-present of an already lerped buffer);
    /// false when nothing was drawn (a dead or wrongly sized snapshot, a lost device) - the frame then presents as it is.</summary>
    internal bool ApplyFrameDissolve(D3D12Swapchain target, D3D12FrameSnapshot snap, float t)
    {
        if (!snap.IsLive || !ReferenceEquals(snap.Device, this) || snap.W != target.W || snap.H != target.H || _device == null) return false;
        TargetFrameState f = target.Frame;
        uint idx = f.FrameIndex;
        ID3D12Resource* back = idx < FRAME_COUNT ? target.BackBuffers[idx] : null;
        if (back == null || target.RtvHeap == null) return false;
        ulong submit = target.SubmitSequence;
        if (f.DissolvedSubmit == submit) return true;   // a refused present re-presented: this buffer already holds the dissolve
        t = t < 0f ? 0f : (t > 1f ? 1f : t);
        if (_compositor is null)
        {
            // A device whose targets all replay directly (no composite turn yet) builds the composite pipelines on first need.
            _compositor = new SliceCompositor();
            _compositor.Init(_device);
        }
        int slot = BeginDissolveList();
        if (slot < 0) return false;
        var list = _fdList;
        FdBarrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = target.RtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += idx * _rtvSize;
        list->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        _compositor.Bind(list, snap.SrvHeap, (int)target.W, (int)target.H);
        _compositor.Scissor(list, 0, 0, (int)target.W, (int)target.H);
        _compositor.Begin(0f, 0f, target.W, target.H);
        _compositor.Alpha(1f - t);                         // the quad writes (1 - t)·S ...
        float* factor = stackalloc float[4] { t, t, t, t };
        list->OMSetBlendFactor(factor);                    // ... and the blend adds t·N (the back buffer)
        _compositor.Draw(list, SliceCompositor.Pso.Dissolve, snap.SrvHeap->GetGPUDescriptorHandleForHeapStart());
        FdBarrier(back, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
        ulong v = EndDissolveList(slot);
        snap.LastUseFence = v;
        NoteTargetQueueWork(f, idx, v);
        f.DissolvedSubmit = submit;
        // The lerped buffer no longer holds the frame its partial-present age says it does: the next frame into it is composited whole.
        if (target.SequentialFlip && ReferenceEquals(target, _primarySwapchain) && idx < (uint)_ppBufTurn.Length) _ppBufTurn[idx] = 0;
        target.PpPresentCount = 0;   // a whole present
        return true;
    }

    /// <summary>Queue work this file put on a target's back buffer <paramref name="idx"/>: the next submit into that buffer, a resize and
    /// a teardown wait for it (the target's fence ledger), and so does a geometry-motion present (LastSubmitFence).</summary>
    private static void NoteTargetQueueWork(TargetFrameState f, uint idx, ulong v)
    {
        if (idx < (uint)f.FenceValues.Length && f.FenceValues[idx] < v) f.FenceValues[idx] = v;
        if (f.LastSubmitFence < v) f.LastSubmitFence = v;
    }

    /// <summary><see cref="IFrameSnapshot.Dispose"/>: release now when its last GPU use retired, else after it does
    /// (<see cref="DrainRetiredFrameSnapshots"/>, from the next present or capture). Render owner.</summary>
    internal void RetireFrameSnapshot(D3D12FrameSnapshot snap)
    {
        if (snap.Disposed) return;
        snap.Disposed = true;
        _fdLive.Remove(snap);
        if (!snap.IsLive) return;
        AssertDeviceOwner();
        if (_fence == null || Gen.ID3D12FenceVtbl.GetCompletedValue(_fence) >= snap.LastUseFence) ReleaseSnapshotResources(snap);
        else _fdRetired.Add(snap);
    }

    private void DrainRetiredFrameSnapshots()
    {
        if (_fdRetired.Count == 0 || _fence == null) return;
        ulong done = Gen.ID3D12FenceVtbl.GetCompletedValue(_fence);
        for (int i = _fdRetired.Count - 1; i >= 0; i--)
        {
            D3D12FrameSnapshot s = _fdRetired[i];
            if (done < s.LastUseFence) continue;
            ReleaseSnapshotResources(s);
            _fdRetired.RemoveAt(i);
        }
    }

    private static void ReleaseSnapshotResources(D3D12FrameSnapshot s)
    {
        if (s.Texture != null) { D3D12MemoryDiagnostics.Release(s.Texture, "Freeze.Snapshot"); s.Texture->Release(); s.Texture = null; }
        if (s.SrvHeap != null) { D3D12MemoryDiagnostics.Release(s.SrvHeap, "Freeze.SnapshotSrv"); s.SrvHeap->Release(); s.SrvHeap = null; }
    }

    /// <summary>Device loss / teardown: every snapshot's texture goes now (a later dissolve from one presents the frame as it is, and
    /// its Dispose is a no-op), and so do the dissolve's command list and allocators (a recovery rebuilds them on first use).</summary>
    private void ReleaseFrameDissolveResources()
    {
        for (int i = 0; i < _fdLive.Count; i++) ReleaseSnapshotResources(_fdLive[i]);
        for (int i = 0; i < _fdRetired.Count; i++) ReleaseSnapshotResources(_fdRetired[i]);
        _fdLive.Clear();
        _fdRetired.Clear();
        for (int i = 0; i < _swapchains.Count; i++) _swapchains[i].Frame.DissolveFrom = null;
        if (_fdList != null) { _fdList->Release(); _fdList = null; }
        for (int i = 0; i < _fdAllocs.Length; i++)
        {
            if (_fdAllocs[i] != null) { _fdAllocs[i]->Release(); _fdAllocs[i] = null; }
            _fdAllocFence[i] = 0;
        }
        _fdNext = 0;
    }

    /// <summary>Open the dissolve's own command list on the next of its two allocators (waiting for that allocator's last use, normally
    /// long retired). -1 when the fence cannot be reached (a lost device).</summary>
    private int BeginDissolveList()
    {
        if (_fdList == null)
        {
            for (int i = 0; i < _fdAllocs.Length; i++)
            {
                ID3D12CommandAllocator* a;
                Check(_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof<ID3D12CommandAllocator>(), (void**)&a),
                    "FrameDissolve.CreateCommandAllocator");
                _fdAllocs[i] = a;
            }
            ID3D12GraphicsCommandList* l;
            Check(_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, _fdAllocs[0], null,
                __uuidof<ID3D12GraphicsCommandList>(), (void**)&l), "FrameDissolve.CreateCommandList");
            Check(l->Close(), "FrameDissolve.Close");
            _fdList = l;
        }
        int slot = _fdNext;
        if (!WaitForFenceValue(_fdAllocFence[slot])) return -1;
        _fdNext ^= 1;
        Check(_fdAllocs[slot]->Reset(), "FrameDissolve.alloc.Reset");
        Check(_fdList->Reset(_fdAllocs[slot], null), "FrameDissolve.list.Reset");
        return slot;
    }

    private ulong EndDissolveList(int slot)
    {
        Check(_fdList->Close(), "FrameDissolve.list.Close");
        ID3D12CommandList* exec = (ID3D12CommandList*)_fdList;
        Gen.ID3D12CommandQueueVtbl.ExecuteCommandLists(_queue, 1, (void**)&exec);   // GEN-COM (wired)
        ulong v = SignalNext();
        _fdAllocFence[slot] = v;
        return v;
    }

    private void FdBarrier(ID3D12Resource* res, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = before;
        b.Anonymous.Transition.StateAfter = after;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;   // all subresources
        _fdList->ResourceBarrier(1, &b);
    }

    /// <summary>Test seam: read a snapshot's texture back as tightly packed, top-down BGRA8 (drains the GPU; allocates).</summary>
    internal byte[] ReadFrameSnapshotBgra(IFrameSnapshot snapshot)
    {
        AssertDeviceOwner();
        var s = (D3D12FrameSnapshot)snapshot;
        if (!s.IsLive) throw new InvalidOperationException("The snapshot holds no texture.");
        D3D12_RESOURCE_DESC desc = default;
        desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = s.W; desc.Height = s.H; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp; uint rows; ulong rowBytes; ulong total;
        _device->GetCopyableFootprints(&desc, 0, 1, 0, &fp, &rows, &rowBytes, &total);
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC bd = default;
        bd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = total; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1; bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &bd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&readback), "FreezeSnapshot.Readback");
        try
        {
            int slot = BeginDissolveList();
            if (slot < 0) throw new InvalidOperationException("The device is lost.");
            FdBarrier(s.Texture, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
            D3D12_TEXTURE_COPY_LOCATION dst = default;
            dst.pResource = readback;
            dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            dst.Anonymous.PlacedFootprint = fp;
            D3D12_TEXTURE_COPY_LOCATION src = default;
            src.pResource = s.Texture;
            src.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            _fdList->CopyTextureRegion(&dst, 0, 0, 0, &src, null);
            FdBarrier(s.Texture, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            s.LastUseFence = EndDissolveList(slot);
            WaitForGpu();
            int w = (int)s.W, h = (int)s.H;
            byte[] outp = new byte[(long)w * h * 4];
            void* mapped;
            D3D12_RANGE rr = default; rr.End = (nuint)total;
            Check(readback->Map(0, &rr, &mapped), "FreezeSnapshot.Map");
            for (int y = 0; y < h; y++)
                new ReadOnlySpan<byte>((byte*)mapped + (ulong)y * fp.Footprint.RowPitch, w * 4).CopyTo(outp.AsSpan(y * w * 4));
            D3D12_RANGE none = default;
            readback->Unmap(0, &none);
            return outp;
        }
        finally { readback->Release(); }
    }
}
