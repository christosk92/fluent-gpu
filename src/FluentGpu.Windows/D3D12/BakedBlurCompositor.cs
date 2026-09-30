using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using Gen = FluentGpu.Interop.Generated;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// The image BAKE (a blurred derivative of a decoded image — mosaic / hero backdrops), run OFF the frame on its own
/// COMPUTE queue (docs/plans/scroll-gpu-retained-tiles-implementation.md §C): the source is resampled to the output size,
/// then a dual-Kawase down/up chain (<see cref="AcrylicKawaseMath"/> — the σ→chain mapping the live acrylic uses) blurs
/// it, the last up pass writing straight into the derivative's texture through an unordered-access view. The derivative
/// is published behind the batch's fence (<see cref="ImageTextureStore.CommitDerived"/>): until the compute work lands
/// the frame keeps drawing what it drew before (the unblurred pixels / the placeholder) — never a hole, and no frame
/// ever waits on a bake. One job per turn; one scratch pyramid per batch in flight (<see cref="UploadQueue.Depth"/>
/// banks), each reused only once its batch completed.
/// </summary>
internal sealed unsafe class BakedBlurCompositor : IDisposable
{
    private const int ScratchSize = 512;
    private const int Levels = AcrylicKawaseMath.MaxIterations + 1;   // L0 (the resampled source) + the halving pyramid
    private const int Banks = UploadQueue.Depth;
    private const int SlotsPerBank = 2 + 2 * Levels;                   // source SRV, output UAV, level SRVs, level UAVs

    private ID3D12Device* _device;
    private readonly UploadQueue _compute = new(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COMPUTE, "BakedBlur.ComputeQueue");
    private ID3D12DescriptorHeap* _heap;
    private uint _inc;
    private ID3D12RootSignature* _root;
    private ID3D12PipelineState* _resamplePso, _downPso, _upPso;
    private ID3D12QueryHeap* _timestampHeap;
    private ID3D12Resource* _timestampReadback;
    private ulong* _timestampData;
    private ulong _timestampFrequency;
    private readonly ID3D12Resource*[] _levels = new ID3D12Resource*[Banks * Levels];
    private readonly D3D12_RESOURCE_STATES[] _levelState = new D3D12_RESOURCE_STATES[Banks * Levels];
    private readonly ulong[] _bankFence = new ulong[Banks];
    private readonly bool[] _timestampPending = new bool[Banks];
    private int _nextBank;
    private readonly float[] _k = new float[16];

    /// <summary>The compute queue whose fences gate a derivative's readiness (the image store reads them).</summary>
    public UploadQueue ComputeQueue => _compute;

    /// <summary>Scratch pyramids — one per compute batch that may be in flight.</summary>
    internal int ScratchBankCount => _bankFence.Length;

    /// <summary>Timestamp pairs — one per scratch bank.</summary>
    internal int TimestampBankCount => _timestampPending.Length;

    private const string Hlsl = """
cbuffer C : register(b0) { float4 P0; float4 P1; float4 P2; float4 P3; };
Texture2D gSrc : register(t0);
RWTexture2D<float4> gDst : register(u0);
SamplerState gLin : register(s0);
// P0.xy = destination size (texels written). Resample: P1 = source uv origin.xy / size.zw, P2 = clamp min.xy / max.zw.
// Kawase: P1.xy = source uv per destination texel, P1.zw = max source uv, P2.xy = source texel size, P2.z = offset.
[numthreads(8, 8, 1)]
void CSResample(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)P0.x || id.y >= (uint)P0.y) return;
    float2 t = (float2(id.xy) + 0.5) / P0.xy;
    gDst[id.xy] = gSrc.SampleLevel(gLin, clamp(P1.xy + t * P1.zw, P2.xy, P2.zw), 0);
}
float4 KS(float2 uv) { return gSrc.SampleLevel(gLin, clamp(uv, P2.xy * 0.5, P1.zw), 0); }
[numthreads(8, 8, 1)]
void CSKawaseDown(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)P0.x || id.y >= (uint)P0.y) return;
    float2 uv = (float2(id.xy) + 0.5) * P1.xy;
    float2 h = P2.xy * 0.5 * P2.z;
    float4 s = KS(uv) * 4.0;
    s += KS(uv + float2(h.x, h.y));
    s += KS(uv + float2(-h.x, -h.y));
    s += KS(uv + float2(h.x, -h.y));
    s += KS(uv + float2(-h.x, h.y));
    gDst[id.xy] = s / 8.0;
}
[numthreads(8, 8, 1)]
void CSKawaseUp(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)P0.x || id.y >= (uint)P0.y) return;
    float2 uv = (float2(id.xy) + 0.5) * P1.xy;
    float2 h = P2.xy * 0.5 * P2.z;
    float4 s = KS(uv + float2(-h.x * 2.0, 0.0));
    s += KS(uv + float2(-h.x, h.y)) * 2.0;
    s += KS(uv + float2(0.0, h.y * 2.0));
    s += KS(uv + float2(h.x, h.y)) * 2.0;
    s += KS(uv + float2(h.x * 2.0, 0.0));
    s += KS(uv + float2(h.x, -h.y)) * 2.0;
    s += KS(uv + float2(0.0, -h.y * 2.0));
    s += KS(uv + float2(-h.x, -h.y)) * 2.0;
    gDst[id.xy] = s / 12.0;
}
""";

    public void Init(ID3D12Device* device)
    {
        _device = device;
        _compute.Init(device);
        InitTimestamps();
        D3D12_DESCRIPTOR_HEAP_DESC hd = default;
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = (uint)(Banks * SlotsPerBank);
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* heap;
        Check(_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "BakedBlur.Heap");
        _heap = heap;
        _inc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12MemoryDiagnostics.Track(_heap, "BakedBlur.Heap", (ulong)(Banks * SlotsPerBank) * _inc);
        BuildRoot();
        _resamplePso = BuildPso("CSResample", "BakedBlur.Resample");
        _downPso = BuildPso("CSKawaseDown", "BakedBlur.KawaseDown");
        _upPso = BuildPso("CSKawaseUp", "BakedBlur.KawaseUp");
        // The scratch pyramids are created LAZILY (EnsureBank): an app that never bakes an image blur never holds them.
    }

    /// <summary>Bytes the scratch pyramids hold (in use once created; nothing before the first bake).</summary>
    public LayerTargetCensus TargetCensus
    {
        get
        {
            LayerTargetCensus c = default;
            for (int b = 0; b < Banks; b++)
                for (int l = 0; l < Levels; l++)
                    if (_levels[b * Levels + l] != null)
                    {
                        int d = LevelSide(l);
                        c = c.WithSlot(LayerTargetBucket.Bytes(d, d), inUse: true, retained: false);
                    }
            return c;
        }
    }

    /// <summary>Run at most one runnable job: record its compute batch, submit it, publish the derivative behind the
    /// batch's fence and post the result. Returns whether a job was consumed.</summary>
    public bool DrainOne(ImageTextureStore images, BakedBlurQueue queue)
    {
        CollectGpuTimes(queue);
        if (!queue.TryDequeueRunnableJob(out var job)) return false;
        long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!queue.IsCurrent(in job) || !images.TryGetBakeSource(job.SourceId, out var source, out var sourceUv))
        {
            Fail(queue, in job, recordStart);
            return true;
        }
        int outW = Math.Clamp(job.OutputW, 1, ScratchSize), outH = Math.Clamp(job.OutputH, 1, ScratchSize);
        if (!images.TryReserveDerived(outW, outH, out var destination, out int token))
        {
            Fail(queue, in job, recordStart);
            return true;
        }

        int bank = _nextBank;
        _nextBank = (bank + 1) % Banks;
        _compute.WaitFor(_bankFence[bank]);   // the bank's previous batch (the one-job-per-turn cadence makes this a no-op)
        CollectBank(queue, bank);
        EnsureBank(bank);
        AcrylicKawaseMath.SelectChain(job.SigmaTexels, 1f, out int iters, out float offset);

        var cmd = _compute.Open();
        void* heap = _heap;
        Gen.ID3D12GraphicsCommandListVtbl.SetDescriptorHeaps(cmd, 1, &heap);
        Gen.ID3D12GraphicsCommandListVtbl.SetComputeRootSignature(cmd, _root);
        int q = bank * 2;
        if (_timestampHeap != null) cmd->EndQuery(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, (uint)q);

        int b0 = bank * SlotsPerBank;
        CreateSrv(source, b0, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
        CreateUav(destination, b0 + 1);

        // L0 = the source's image rect resampled to the output size.
        LevelBarrier(cmd, bank, 0, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        Array.Clear(_k);
        _k[0] = outW; _k[1] = outH;
        _k[4] = sourceUv.X; _k[5] = sourceUv.Y; _k[6] = sourceUv.W; _k[7] = sourceUv.H;
        _k[8] = sourceUv.X; _k[9] = sourceUv.Y; _k[10] = sourceUv.X + sourceUv.W; _k[11] = sourceUv.Y + sourceUv.H;
        Dispatch(cmd, _resamplePso, b0, LevelUav(bank, 0), outW, outH);
        LevelBarrier(cmd, bank, 0, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);

        // down: L(k−1) → Lk
        for (int k = 1; k <= iters; k++)
        {
            LevelBarrier(cmd, bank, k, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            Kawase(cmd, _downPso, LevelSrv(bank, k - 1), LevelUav(bank, k), k - 1, k, outW, outH, offset);
            LevelBarrier(cmd, bank, k, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        }
        // up: Lk → L(k−1); the last pass (L1 → the output size) writes the derivative itself
        for (int k = iters; k >= 2; k--)
        {
            LevelBarrier(cmd, bank, k - 1, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            Kawase(cmd, _upPso, LevelSrv(bank, k), LevelUav(bank, k - 1), k, k - 1, outW, outH, offset);
            LevelBarrier(cmd, bank, k - 1, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        }
        Transition(cmd, destination, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        Kawase(cmd, _upPso, LevelSrv(bank, 1), b0 + 1, 1, 0, outW, outH, offset);
        Transition(cmd, destination, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);

        if (_timestampHeap != null && _timestampReadback != null)
        {
            cmd->EndQuery(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, (uint)(q + 1));
            cmd->ResolveQueryData(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, (uint)q, 2, _timestampReadback, (ulong)q * sizeof(ulong));
            _timestampPending[bank] = true;
        }
        ulong fence = _compute.Submit();
        _bankFence[bank] = fence;
        images.NoteComputeRead(job.SourceId, fence);
        images.CommitDerived(token, job.Id, fence);
        // ExecuteCommandLists has accepted the work: the derivative is Ready to the UI; the frame draws it once the fence
        // passes (until then it keeps drawing the pixels it had).
        queue.Post(new BakedBlurQueue.Result(job.Id, job.Generation, true, outW, outH, job.Quality, job.IsUpgrade));
        queue.ReportRecordTime(System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds);
        return true;
    }

    private static void Fail(BakedBlurQueue queue, in BakedBlurQueue.Job job, long recordStart)
    {
        queue.Post(new BakedBlurQueue.Result(job.Id, job.Generation, false, 0, 0, job.Quality, job.IsUpgrade));
        queue.ReportRecordTime(System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds);
    }

    /// <summary>One Kawase pass from pyramid level <paramref name="srcLevel"/> (SRV <paramref name="srvSlot"/>) into level
    /// <paramref name="dstLevel"/>'s used extent (UAV <paramref name="uavSlot"/>).</summary>
    private void Kawase(ID3D12GraphicsCommandList* cmd, ID3D12PipelineState* pso, int srvSlot, int uavSlot,
        int srcLevel, int dstLevel, int outW, int outH, float offset)
    {
        int suw = AcrylicKawaseMath.LevelDim(outW, srcLevel), suh = AcrylicKawaseMath.LevelDim(outH, srcLevel);
        int duw = AcrylicKawaseMath.LevelDim(outW, dstLevel), duh = AcrylicKawaseMath.LevelDim(outH, dstLevel);
        float st = 1f / LevelSide(srcLevel);
        Array.Clear(_k);
        _k[0] = duw; _k[1] = duh;
        _k[4] = st * suw / duw; _k[5] = st * suh / duh; _k[6] = (suw - 0.5f) * st; _k[7] = (suh - 0.5f) * st;
        _k[8] = st; _k[9] = st; _k[10] = offset;
        Dispatch(cmd, pso, srvSlot, uavSlot, duw, duh);
    }

    private void Dispatch(ID3D12GraphicsCommandList* cmd, ID3D12PipelineState* pso, int srvSlot, int uavSlot, int w, int h)
    {
        Gen.ID3D12GraphicsCommandListVtbl.SetPipelineState(cmd, pso);
        fixed (float* k = _k) Gen.ID3D12GraphicsCommandListVtbl.SetComputeRoot32BitConstants(cmd, 0, 16, k, 0);
        Gen.ID3D12GraphicsCommandListVtbl.SetComputeRootDescriptorTable(cmd, 1, Gpu(srvSlot));
        Gen.ID3D12GraphicsCommandListVtbl.SetComputeRootDescriptorTable(cmd, 2, Gpu(uavSlot));
        Gen.ID3D12GraphicsCommandListVtbl.Dispatch(cmd, (uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        GpuDrawCount.Frame++;
    }

    private static int LevelSide(int level) => ScratchSize >> level;
    private static int LevelSrv(int bank, int level) => bank * SlotsPerBank + 2 + level;
    private static int LevelUav(int bank, int level) => bank * SlotsPerBank + 2 + Levels + level;

    private void LevelBarrier(ID3D12GraphicsCommandList* cmd, int bank, int level, D3D12_RESOURCE_STATES to)
    {
        ref D3D12_RESOURCE_STATES state = ref _levelState[bank * Levels + level];
        if (state == to) return;
        Transition(cmd, _levels[bank * Levels + level], state, to);
        state = to;
    }

    private static void Transition(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = before;
        b.Anonymous.Transition.StateAfter = after;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        Gen.ID3D12GraphicsCommandListVtbl.ResourceBarrier(cmd, 1, &b);
    }

    /// <summary>Create bank <paramref name="bank"/>'s pyramid (RGBA8 unordered-access textures 512², 256², …) and its
    /// views on first use.</summary>
    private void EnsureBank(int bank)
    {
        if (_levels[bank * Levels] != null) return;
        for (int l = 0; l < Levels; l++)
        {
            int d = LevelSide(l);
            D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
            D3D12_RESOURCE_DESC rd = default;
            rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            rd.Width = (uint)d; rd.Height = (uint)d; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
            rd.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM; rd.SampleDesc.Count = 1;
            rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
            rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
            ulong bytes = D3D12MemoryDiagnostics.AllocationBytes(_device, &rd);
            ID3D12Resource* res;
            Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, null, __uuidof<ID3D12Resource>(), (void**)&res), "BakedBlur.Level");
            string label = $"BakedBlur.Level{l} {d}x{d}";
            D3D12MemoryDiagnostics.Track(res, D3D12MemoryDiagnostics.NameOrUnknown(label, bytes), bytes != 0 ? bytes : (ulong)d * (uint)d * 4UL);
            _levels[bank * Levels + l] = res;
            _levelState[bank * Levels + l] = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
            CreateSrv(res, LevelSrv(bank, l), DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM);
            CreateUav(res, LevelUav(bank, l));
        }
    }

    private void CreateSrv(ID3D12Resource* res, int slot, DXGI_FORMAT format)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
        sd.Format = format;
        sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = 0x1688;
        sd.Anonymous.Texture2D.MipLevels = 1;
        _device->CreateShaderResourceView(res, &sd, Cpu(slot));
    }

    private void CreateUav(ID3D12Resource* res, int slot)
    {
        D3D12_UNORDERED_ACCESS_VIEW_DESC ud = default;
        ud.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        ud.ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_TEXTURE2D;
        _device->CreateUnorderedAccessView(res, null, &ud, Cpu(slot));
    }

    private D3D12_CPU_DESCRIPTOR_HANDLE Cpu(int i) { var h = _heap->GetCPUDescriptorHandleForHeapStart(); h.ptr += (nuint)((uint)i * _inc); return h; }
    private D3D12_GPU_DESCRIPTOR_HANDLE Gpu(int i) { var h = _heap->GetGPUDescriptorHandleForHeapStart(); h.ptr += (ulong)((uint)i * _inc); return h; }

    private void BuildRoot()
    {
        D3D12_DESCRIPTOR_RANGE srv = default;
        srv.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srv.NumDescriptors = 1; srv.BaseShaderRegister = 0; srv.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;
        D3D12_DESCRIPTOR_RANGE uav = default;
        uav.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
        uav.NumDescriptors = 1; uav.BaseShaderRegister = 0; uav.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;
        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[3];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = 16;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1; p[1].Anonymous.DescriptorTable.pDescriptorRanges = &srv;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[2].Anonymous.DescriptorTable.NumDescriptorRanges = 1; p[2].Anonymous.DescriptorTable.pDescriptorRanges = &uav;
        p[2].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        D3D12_STATIC_SAMPLER_DESC samp = default;
        samp.Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        samp.AddressU = samp.AddressV = samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        samp.MaxLOD = float.MaxValue;
        D3D12_ROOT_SIGNATURE_DESC d = default;
        d.NumParameters = 3; d.pParameters = p; d.NumStaticSamplers = 1; d.pStaticSamplers = &samp;
        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&d, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "BakedBlur.RootSerialize");
        ID3D12RootSignature* root;
        Check(_device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&root), "BakedBlur.Root");
        _root = root;
        sig->Release();
        if (err != null) err->Release();
    }

    private ID3D12PipelineState* BuildPso(string entry, string what)
    {
        ID3DBlob* cs = ShaderCompiler.Compile(Hlsl, entry, "cs_5_1", what);
        D3D12_COMPUTE_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _root;
        pd.CS = new D3D12_SHADER_BYTECODE { pShaderBytecode = cs->GetBufferPointer(), BytecodeLength = cs->GetBufferSize() };
        ID3D12PipelineState* pso;
        Check(_device->CreateComputePipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), what);
        cs->Release();
        return pso;
    }

    private void InitTimestamps()
    {
        ulong frequency;
        if (_compute.Queue == null || _compute.Queue->GetTimestampFrequency(&frequency) < 0 || frequency == 0) return;
        _timestampFrequency = frequency;
        D3D12_QUERY_HEAP_DESC qd = default;
        qd.Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        qd.Count = 2u * Banks;
        ID3D12QueryHeap* heap;
        if (_device->CreateQueryHeap(&qd, __uuidof<ID3D12QueryHeap>(), (void**)&heap) < 0) return;
        _timestampHeap = heap;
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = 2UL * Banks * sizeof(ulong); rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        if (_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&readback) < 0)
        {
            _timestampHeap->Release(); _timestampHeap = null; return;
        }
        _timestampReadback = readback;
        D3D12MemoryDiagnostics.Track(readback, "BakedBlur.TimestampReadback", 2UL * Banks * sizeof(ulong));
        void* mapped;
        if (readback->Map(0, null, &mapped) >= 0) _timestampData = (ulong*)mapped;
    }

    /// <summary>Report the GPU time of every bank whose batch completed (a fence compare — never a wait).</summary>
    private void CollectGpuTimes(BakedBlurQueue queue)
    {
        for (int b = 0; b < Banks; b++)
            if (_timestampPending[b] && _compute.IsComplete(_bankFence[b])) CollectBank(queue, b);
    }

    private void CollectBank(BakedBlurQueue queue, int bank)
    {
        if (!_timestampPending[bank] || _timestampData == null || _timestampFrequency == 0) return;
        int q = bank * 2;
        ulong begin = _timestampData[q], end = _timestampData[q + 1];
        _timestampPending[bank] = false;
        if (end >= begin) queue.ReportGpuTime((end - begin) * 1000.0 / _timestampFrequency);
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    public void Dispose()
    {
        _compute.Dispose();   // waits for every bake first
        for (int i = 0; i < _levels.Length; i++)
            if (_levels[i] != null) { D3D12MemoryDiagnostics.Release(_levels[i], "BakedBlur.Level"); _levels[i]->Release(); _levels[i] = null; }
        if (_resamplePso != null) { _resamplePso->Release(); _resamplePso = null; }
        if (_downPso != null) { _downPso->Release(); _downPso = null; }
        if (_upPso != null) { _upPso->Release(); _upPso = null; }
        if (_root != null) { _root->Release(); _root = null; }
        if (_heap != null) { D3D12MemoryDiagnostics.Release(_heap, "BakedBlur.Heap"); _heap->Release(); _heap = null; }
        if (_timestampReadback != null)
        {
            if (_timestampData != null) { _timestampReadback->Unmap(0, null); _timestampData = null; }
            D3D12MemoryDiagnostics.Release(_timestampReadback, "BakedBlur.TimestampReadback");
            _timestampReadback->Release(); _timestampReadback = null;
        }
        if (_timestampHeap != null) { _timestampHeap->Release(); _timestampHeap = null; }
    }
}
