using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// ONE persistently-mapped UPLOAD buffer per frame-in-flight, bump-allocated by every draw pipeline that used to own a
/// private worst-case instance ring (round-rect, shadow, arc, polyline, gradient, image, and the path lane's
/// vertex+index+instance blocks). The sizing/growth RULE lives in the engine-free
/// <see cref="UploadArenaPolicy"/> (headlessly gated); this class only owns the COM objects and the mapped pointers.
///
/// <para><b>Why:</b> nine private rings × <see cref="D3D12Device.FrameBankDepth"/> banks cost the sum of nine
/// independent worst cases (≈1.5 MiB per bank, 4.4 MiB resident) plus the 64 KiB placement granule each committed
/// buffer is padded to — while a real frame records a small fraction of ONE of them. On a UMA adapter every
/// CPU-visible heap is pinned host memory (the Adreno maps UPLOAD heaps PAGE_WRITECOMBINE and keeps the whole segment
/// resident), so those bytes are working set, not "GPU memory somewhere else".</para>
///
/// <para><b>Fence discipline (the invariant to preserve):</b> the device calls <see cref="BeginFrame"/> only after
/// the ring slot's fence value proved the submit that last used that bank has retired, so replacing the bank's
/// buffer there cannot race an in-flight GPU read. Growth happens ONLY there — never mid-frame — because a GPU virtual
/// address handed out by <see cref="TryReserve"/> must stay valid until the frame's LAST flush executes, and this
/// backend flushes several times per submit (one per layer/segment boundary). A frame that outgrows its bank refuses,
/// drops that run (exactly as an over-cap run was dropped before), and the banks pick up the larger size at their next
/// <see cref="BeginFrame"/>; the device turns the refusal into one more full, un-skippable repaint.</para>
/// </summary>
internal sealed unsafe class UploadArena : IDisposable
{
    /// <summary>Per-bank starting size and ceiling — owned by <see cref="UploadArenaPolicy"/> (which also carries the
    /// full-frame worst case they are justified against, and the headless gate that asserts the ceiling still covers
    /// it). 3 × 384 KiB at launch (1.125 MiB) replaces the nine fixed rings' 3 × ≈1.48 MiB (4.45 MiB).</summary>
    internal const uint InitialBytes = UploadArenaPolicy.DefaultInitialBytes;
    internal const uint MaxBytes = UploadArenaPolicy.DefaultMaxBytes;

    private readonly UploadArenaPolicy _policy = new(D3D12Device.FrameBankDepth, InitialBytes, MaxBytes);
    private readonly ID3D12Resource*[] _res = new ID3D12Resource*[D3D12Device.FrameBankDepth];
    private readonly nint[] _cpu = new nint[D3D12Device.FrameBankDepth];   // persistently mapped base
    private readonly ulong[] _gpu = new ulong[D3D12Device.FrameBankDepth];
    private ID3D12Device* _device;   // non-owning; the device outlives every pipeline
    private int _bank;

    /// <summary>The sizing/growth rule (census + diagnostics read through it).</summary>
    public UploadArenaPolicy Policy => _policy;

    /// <summary>Total bytes the arena holds (all banks) — added to the D3D12 resource census.</summary>
    public long LiveBytes => _policy.LiveBytes;

    /// <summary>True when a reservation was refused during the frame just recorded (⇒ content was dropped and the
    /// banks are growing; the device owes one more full repaint).</summary>
    public bool RefusedThisFrame => _policy.RefusedThisFrame;

    public void Init(ID3D12Device* device)
    {
        _device = device;
        for (int b = 0; b < D3D12Device.FrameBankDepth; b++) Allocate(b, _policy.InitialBytes);
    }

    /// <summary>Select this frame's bank (by <see cref="SubmissionRing"/> slot) and reset the bump cursor, growing the
    /// bank first if a previous frame asked for more. The bank is fenced at this point — see the type doc.</summary>
    public void BeginFrame(int slot)
    {
        if (_policy.BeginFrame(slot, out int bank, out uint growTo)) Allocate(bank, growTo);
        _bank = bank;
    }

    /// <summary>
    /// Reserve <paramref name="bytes"/> of this frame's arena. On success <paramref name="cpu"/> is the mapped address
    /// to write and <paramref name="gva"/> the matching GPU virtual address (16-byte aligned — what a root
    /// StructuredBuffer SRV / vertex / index view requires). On failure nothing is consumed and the caller must record
    /// NOTHING (its command-list state stays untouched, like the old over-cap drop path).
    /// </summary>
    public bool TryReserve(int bytes, out byte* cpu, out ulong gva)
    {
        cpu = null;
        gva = 0;
        if (bytes <= 0) return false;
        nint mapped = _cpu[_bank];
        if (mapped == 0) return false;                       // bank could not be (re)allocated — device removed
        if (!_policy.TryReserve((uint)bytes, out uint off)) return false;
        cpu = (byte*)mapped + off;
        gva = _gpu[_bank] + off;
        return true;
    }

    // (Re)create ONE bank at `bytes`. The old buffer is released immediately: BeginFrame is the only caller and the
    // device fenced this bank's last submit before it ran, so nothing in flight can still be reading it. A failure
    // (device removed) leaves the bank null and every reservation on it refuses — the pipelines then drop their runs
    // instead of writing through a stale pointer, which is the same posture the image store takes on a lost device.
    private void Allocate(int bank, uint bytes)
    {
        if (_device == null) return;
        Release(bank);
        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes;
        rd.Height = 1;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE;
        ID3D12Resource* res = null;
        if ((int)_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null,
                __uuidof<ID3D12Resource>(), (void**)&res) < 0 || res == null)
            return;
        void* p;
        if ((int)res->Map(0, null, &p) < 0)
        {
            res->Release();
            return;
        }
        _res[bank] = res;
        _cpu[bank] = (nint)p;
        _gpu[bank] = res->GetGPUVirtualAddress();
        D3D12MemoryDiagnostics.Track(res, $"Upload.Arena[{bank}]", bytes);
        _policy.NoteGrown(bank, bytes);
    }

    private void Release(int bank)
    {
        if (_res[bank] == null) return;
        _res[bank]->Unmap(0, null);
        D3D12MemoryDiagnostics.Release(_res[bank], "Upload.Arena");
        _res[bank]->Release();
        _res[bank] = null;
        _cpu[bank] = 0;
        _gpu[bank] = 0;
        _policy.NoteReleased(bank);
    }

    public void Dispose()
    {
        for (int b = 0; b < D3D12Device.FrameBankDepth; b++) Release(b);
        _device = null;
    }
}
