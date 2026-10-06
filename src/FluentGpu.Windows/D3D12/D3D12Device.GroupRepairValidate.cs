using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

// THE GROUP-REPAIR VALIDATION ARM (--fg group-repair-validate). A repaired group surface (RepairGroup: only the rects whose
// entries changed cleared and redrawn) must equal the whole render of the same group bit for bit. With the switch on, the
// turn that repairs a surface also renders the group whole into a second scratch and copies both into readback buffers; the
// next composite (after the GPU passed them) compares the two and logs. One comparison in flight at a time; every other
// repair in between goes unchecked. Render-thread owned, like the rest of the composite.
public sealed unsafe partial class D3D12Device
{
    private ID3D12Resource* _grvA;
    private ID3D12Resource* _grvB;
    private ulong _grvBytes;
    private D3D12_PLACED_SUBRESOURCE_FOOTPRINT _grvFp;
    private int _grvW, _grvH;
    private bool _grvPending;
    private long _grvChecks, _grvMismatches;
    private int _grvMaxDelta;

    /// <summary>Compare the pair the previous repair queued (the GPU has passed it once this frame's slot is acquired).</summary>
    private void GroupRepairValidateBegin()
    {
        if (!_grvPending) return;
        _grvPending = false;
        WaitForGpu();
        void* a, b;
        D3D12_RANGE rr = default; rr.Begin = 0; rr.End = (nuint)_grvBytes;
        if ((int)_grvA->Map(0, &rr, &a) < 0) return;
        if ((int)_grvB->Map(0, &rr, &b) < 0) { _grvA->Unmap(0, null); return; }
        int diffPx = 0, maxD = 0;
        for (int y = 0; y < _grvH; y++)
        {
            byte* ra = (byte*)a + _grvFp.Offset + (ulong)y * _grvFp.Footprint.RowPitch;
            byte* rb = (byte*)b + _grvFp.Offset + (ulong)y * _grvFp.Footprint.RowPitch;
            for (int x = 0; x < _grvW * 4; x += 4)
            {
                int d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(ra[x + c] - rb[x + c]));
                if (d > 0) { diffPx++; maxD = Math.Max(maxD, d); }
            }
        }
        D3D12_RANGE none = default;
        _grvA->Unmap(0, &none); _grvB->Unmap(0, &none);
        _grvChecks++;
        if (diffPx > 0) _grvMismatches++;
        _grvMaxDelta = Math.Max(_grvMaxDelta, maxD);
        if (diffPx > 0 || _grvChecks % 600 == 1)
            Diag.Line(System.FormattableString.Invariant(
                $"[group-repair] {_grvW}x{_grvH} diffPx={diffPx} maxDelta={maxD} | checks={_grvChecks} mismatches={_grvMismatches} maxDeltaEver={_grvMaxDelta}"));
    }

    /// <summary>After repairing group <paramref name="i"/>'s surface <paramref name="s"/> (render target state, pass closed):
    /// render the group whole into a fresh scratch and queue both for <see cref="GroupRepairValidateBegin"/>.</summary>
    private void GroupRepairValidate(in CompositeFrame frame, int i, in PixelRect region, int s)
    {
        if (!EngineSwitches.GroupRepairValidate || _grvPending) return;
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        int t = _surfaces!.AcquireScratch(w, h, _fenceValue + 1);
        if (t < 0) return;
        ScratchBarrier(t, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        BeginPass(_surfaces.ScratchRtv(t), w, h, PassLoad.Clear);
        BindCompositor(_surfaces.ScratchW(t), _surfaces.ScratchH(t));
        DrawRange(in frame, i + 1, Math.Min(frame.Items.Length, i + 1 + frame.Items[i].GroupCount), region.Left, region.Top, w, h, -1);
        EndPassIfOpen();
        if (!EnsureGroupRepairReadback(w, h)) { _surfaces.ReleaseScratch(t); return; }
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        ScratchBarrier(t, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        CopyToReadback(_surfaces.ScratchResource(s), _grvA, w, h);
        CopyToReadback(_surfaces.ScratchResource(t), _grvB, w, h);
        ScratchBarrier(s, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        ScratchBarrier(t, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        _surfaces.ReleaseScratch(t);
        InvalidateCmdState();
        _grvPending = true;
    }

    private bool EnsureGroupRepairReadback(int w, int h)
    {
        D3D12_RESOURCE_DESC desc = default;
        desc.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = (ulong)w; desc.Height = (uint)h; desc.DepthOrArraySize = 1; desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp; uint rows; ulong rowBytes; ulong total;
        _device->GetCopyableFootprints(&desc, 0, 1, 0, &fp, &rows, &rowBytes, &total);
        _grvFp = fp; _grvW = w; _grvH = h;
        if (_grvA != null && _grvBytes >= total) return true;
        ReleaseGroupRepairValidation();
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC bd = default;
        bd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = total; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
        bd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* a;
        ID3D12Resource* b;
        if ((int)_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &bd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&a) < 0) return false;
        if ((int)_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &bd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&b) < 0) { a->Release(); return false; }
        D3D12MemoryDiagnostics.Track(a, "GroupRepairValidate.Readback", total);
        D3D12MemoryDiagnostics.Track(b, "GroupRepairValidate.Readback", total);
        _grvA = a; _grvB = b; _grvBytes = total;
        return true;
    }

    private void CopyToReadback(ID3D12Resource* src, ID3D12Resource* dst, int w, int h)
    {
        D3D12_BOX box = default; box.right = (uint)w; box.bottom = (uint)h; box.back = 1;
        D3D12_TEXTURE_COPY_LOCATION d = default;
        d.pResource = dst; d.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; d.Anonymous.PlacedFootprint = _grvFp;
        D3D12_TEXTURE_COPY_LOCATION sl = default;
        sl.pResource = src; sl.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; sl.Anonymous.SubresourceIndex = 0;
        _cmdList->CopyTextureRegion(&d, 0, 0, 0, &sl, &box);
    }

    /// <summary>Release the readback pair (device teardown / recovery — the caller has drained the GPU).</summary>
    private void ReleaseGroupRepairValidation()
    {
        if (_grvA != null) { D3D12MemoryDiagnostics.Release(_grvA, "GroupRepairValidate.Readback"); _grvA->Release(); _grvA = null; }
        if (_grvB != null) { D3D12MemoryDiagnostics.Release(_grvB, "GroupRepairValidate.Readback"); _grvB->Release(); _grvB = null; }
        _grvBytes = 0;
        _grvPending = false;
    }
}
