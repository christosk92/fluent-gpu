using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>One-shot render-thread image blur. A fixed 512² scratch PAIR per frame-in-flight bank (depth =
/// <see cref="D3D12Device.FrameBankDepth"/>) implements the same downsample/separable-Gaussian/upsample schedule as
/// self blur; only the final output is persistent.</summary>
internal sealed unsafe class BakedBlurCompositor : IDisposable
{
    private const int ScratchSize = 512;
    private const int Banks = D3D12Device.FrameBankDepth;   // one scratch pair + one timestamp pair per frame-in-flight
    private ID3D12Device* _device;
    private ID3D12DescriptorHeap* _srvHeap;
    private ID3D12DescriptorHeap* _rtvHeap;
    private uint _srvInc, _rtvInc;
    private ID3D12RootSignature* _root;
    private ID3D12PipelineState* _copyPso;
    private ID3D12PipelineState* _blurPso;
    private ID3D12QueryHeap* _timestampHeap;
    private ID3D12Resource* _timestampReadback;
    private ulong* _timestampData;
    private ulong _timestampFrequency;
    private readonly bool[] _timestampPending = new bool[Banks];
    // Per-bank ping-pong scratch pair. Bank b owns SRV slots {b*3 = the bake source, b*3+1 = A, b*3+2 = B} and RTV
    // slots {b*3 = A, b*3+1 = B} — the third RTV slot per bank is deliberately unused so DrainOne's Rtv(slot) /
    // Rtv(slot+1) arithmetic stays identical to the SRV stride. Banking (not double-buffering) is what keeps frame N's
    // recorded copy/blur passes off the scratch the frames still in flight are reading.
    private readonly ID3D12Resource*[] _scratchA = new ID3D12Resource*[Banks];
    private readonly ID3D12Resource*[] _scratchB = new ID3D12Resource*[Banks];
    private readonly D3D12_RESOURCE_STATES[] _stateA = new D3D12_RESOURCE_STATES[Banks];
    private readonly D3D12_RESOURCE_STATES[] _stateB = new D3D12_RESOURCE_STATES[Banks];
    private readonly float[] _constants = new float[24];
    private BakedBlurQueue.Result _recordedResult;
    private bool _hasRecordedResult;

    /// <summary>How many per-frame-in-flight scratch banks exist — tracks <see cref="D3D12Device.FrameBankDepth"/> by
    /// construction (FrameBankingTests asserts the depths cannot drift apart).</summary>
    internal int ScratchBankCount => _scratchA.Length;

    /// <summary>How many frame-in-flight timestamp banks exist — same depth, same guard.</summary>
    internal int TimestampBankCount => _timestampPending.Length;

    private const string CopyHlsl = """
cbuffer C : register(b0) { float4 p0; float4 p1; float4 o0; float4 o1; float4 w0; float4 w1; };
Texture2D gSrc : register(t0); SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv=float2((id<<1)&2,id&2); o.pos=float4(uv*2.0-1.0,0,1); o.pos.y=-o.pos.y; o.uv=uv; return o; }
float4 CopyPS(V i) : SV_Target { return gSrc.Sample(gSamp, clamp(p0.xy+i.uv*p0.zw,p1.xy,p1.zw)); }
""";

    private const string BlurHlsl = """
cbuffer C : register(b0) { float4 p0; float4 p1; float4 o0; float4 o1; float4 w0; float4 w1; };
Texture2D gSrc : register(t0); SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv=float2((id<<1)&2,id&2); o.pos=float4(uv*2.0-1.0,0,1); o.pos.y=-o.pos.y; o.uv=uv; return o; }
float4 T(float2 uv,float2 lo,float2 hi){return gSrc.Sample(gSamp,clamp(uv,lo,hi));}
float4 BlurPS(V i) : SV_Target {
 float2 lo=p0.xy*0.5, mx=p1.zw, base=i.uv*p1.xy, stp=p0.zw*p0.xy;
 float4 a=T(base,lo,mx)*w0.x;
 a+=(T(base+stp*o0.y,lo,mx)+T(base-stp*o0.y,lo,mx))*w0.y;
 a+=(T(base+stp*o0.z,lo,mx)+T(base-stp*o0.z,lo,mx))*w0.z;
 a+=(T(base+stp*o0.w,lo,mx)+T(base-stp*o0.w,lo,mx))*w0.w;
 a+=(T(base+stp*o1.x,lo,mx)+T(base-stp*o1.x,lo,mx))*w1.x;
 a+=(T(base+stp*o1.y,lo,mx)+T(base-stp*o1.y,lo,mx))*w1.y;
 a+=(T(base+stp*o1.z,lo,mx)+T(base-stp*o1.z,lo,mx))*w1.z; return a; }
""";

    public void Init(ID3D12Device* device, ID3D12CommandQueue* queue)
    {
        _device = device;
        InitTimestamps(queue);
        BuildHeaps();
        BuildRoot();
        _copyPso = BuildPso(CopyHlsl, "CopyPS", "BakedBlur.Copy");
        _blurPso = BuildPso(BlurHlsl, "BlurPS", "BakedBlur.Gaussian");
        // The scratch PAIR per frame-in-flight is created LAZILY, on the first job (EnsureBanks) — not here. It is a
        // fixed 6 x 512x512x4 = 6 MiB, every byte of it resident from device init onward on a UMA adapter, and an app
        // that never bakes an image blur (no mosaic/hero backdrop on screen yet — which includes every launch until the
        // first such surface mounts) never needed a texel of it. Creating it on demand costs one allocation on the
        // first bake, on the render thread, ahead of a job that is already a multi-pass GPU operation.
    }

    /// <summary>Create the per-bank scratch pair + its views on first use. Idempotent and cheap after the first call
    /// (one null check). Called from <see cref="DrainOne"/> once a runnable job exists — never from a frame that has
    /// nothing to bake, which is what keeps the 6 MiB off the launch working set.</summary>
    private void EnsureBanks()
    {
        if (_scratchA[0] != null) return;
        for (int b = 0; b < Banks; b++)
        {
            _scratchA[b] = CreateTarget(ScratchSize, ScratchSize, $"BakedBlur.ScratchA{b}");
            _scratchB[b] = CreateTarget(ScratchSize, ScratchSize, $"BakedBlur.ScratchB{b}");
            _stateA[b] = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;   // CreateTarget's initial state
            _stateB[b] = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            CreateSrv(_scratchA[b], SrvCpu(b * 3 + 1)); CreateSrv(_scratchB[b], SrvCpu(b * 3 + 2));
            _device->CreateRenderTargetView(_scratchA[b], null, Rtv(b * 3));
            _device->CreateRenderTargetView(_scratchB[b], null, Rtv(b * 3 + 1));
        }
    }

    /// <summary>Bytes this compositor holds, reported as IN-USE: the scratch pair is banked per frame-in-flight and
    /// permanently live once created, so there is no free/pooled half to report. Zero before the first bake — which is
    /// the point of <see cref="EnsureBanks"/>, and the honest way for the census to show it.</summary>
    public LayerTargetCensus TargetCensus
    {
        get
        {
            LayerTargetCensus c = default;
            for (int b = 0; b < Banks; b++)
            {
                if (_scratchA[b] != null) c = c.WithSlot(LayerTargetBucket.Bytes(ScratchSize, ScratchSize), inUse: true, isPin: false);
                if (_scratchB[b] != null) c = c.WithSlot(LayerTargetBucket.Bytes(ScratchSize, ScratchSize), inUse: true, isPin: false);
            }
            return c;
        }
    }

    public bool DrainOne(ID3D12GraphicsCommandList* cmd, ImageTextureStore images, BakedBlurQueue queue, int frameIndex)
    {
        CollectGpuTime(queue, frameIndex);
        if (!queue.TryDequeueRunnableJob(out var job)) return false;
        EnsureBanks();
        long recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!images.TryGetBakeSource(job.SourceId, out var source, out var sourceUv))
        {
            queue.Post(new BakedBlurQueue.Result(job.Id, job.Generation, false, 0, 0, job.Quality, job.IsUpgrade));
            queue.ReportRecordTime(System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds);
            return true;
        }

        int outW = Math.Clamp(job.OutputW, 1, ScratchSize), outH = Math.Clamp(job.OutputH, 1, ScratchSize);
        int down = AcrylicBackdropMath.DownsampleFactor(job.SigmaTexels, 1f);
        int blurW = Math.Max(1, (outW + down - 1) / down), blurH = Math.Max(1, (outH + down - 1) / down);
        float texelSigma = AcrylicBackdropMath.EffectiveTexelSigma(job.SigmaTexels, 1f, down);
        int bank = frameIndex % Banks, slot = bank * 3;
        ID3D12Resource* scratchA = _scratchA[bank];
        ID3D12Resource* scratchB = _scratchB[bank];
        ref D3D12_RESOURCE_STATES stateA = ref _stateA[bank];
        ref D3D12_RESOURCE_STATES stateB = ref _stateB[bank];
        int query = bank * 2;
        if (_timestampHeap != null) cmd->EndQuery(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, (uint)query);

        CreateSrv(source, SrvCpu(slot));
        ID3D12DescriptorHeap* heap = _srvHeap;
        cmd->SetDescriptorHeaps(1, &heap);
        cmd->SetGraphicsRootSignature(_root);
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

        Barrier(cmd, scratchA, ref stateA, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        RecordCopy(cmd, slot, Rtv(slot), blurW, blurH, sourceUv.X, sourceUv.Y, sourceUv.W, sourceUv.H,
            sourceUv.X, sourceUv.Y, sourceUv.X + sourceUv.W, sourceUv.Y + sourceUv.H);
        Barrier(cmd, scratchA, ref stateA, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

        Barrier(cmd, scratchB, ref stateB, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        RecordBlur(cmd, slot + 1, Rtv(slot + 1), blurW, blurH, texelSigma, 1f, 0f);
        Barrier(cmd, scratchB, ref stateB, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

        Barrier(cmd, scratchA, ref stateA, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        RecordBlur(cmd, slot + 2, Rtv(slot), blurW, blurH, texelSigma, 0f, 1f);
        Barrier(cmd, scratchA, ref stateA, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

        Barrier(cmd, scratchB, ref stateB, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        float uw = (float)blurW / ScratchSize, uh = (float)blurH / ScratchSize;
        float tx = 0.5f / ScratchSize, ty = 0.5f / ScratchSize;
        RecordCopy(cmd, slot + 1, Rtv(slot + 1), outW, outH, 0f, 0f, uw, uh, tx, ty, uw - tx, uh - ty);
        Barrier(cmd, scratchB, ref stateB, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

        if (!queue.IsCurrent(in job) || !images.TryAdoptBakedFrom(cmd, job.Id, scratchB, outW, outH))
        {
            queue.Post(new BakedBlurQueue.Result(job.Id, job.Generation, false, 0, 0, job.Quality, job.IsUpgrade));
            queue.ReportRecordTime(System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds);
            return true;
        }

        if (_timestampHeap != null && _timestampReadback != null)
        {
            cmd->EndQuery(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, (uint)(query + 1));
            cmd->ResolveQueryData(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP,
                (uint)query, 2, _timestampReadback, (ulong)query * sizeof(ulong));
            _timestampPending[bank] = true;
        }

        // Register the texture now so later commands in this list can resolve its SRV, but do not expose Ready to the
        // UI/cache until ExecuteCommandLists has accepted the producer work.
        _recordedResult = new BakedBlurQueue.Result(job.Id, job.Generation, true, outW, outH, job.Quality, job.IsUpgrade);
        _hasRecordedResult = true;
        queue.ReportRecordTime(System.Diagnostics.Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds);
        return true;
    }

    private void InitTimestamps(ID3D12CommandQueue* queue)
    {
        ulong frequency;
        if (queue == null || queue->GetTimestampFrequency(&frequency) < 0 || frequency == 0) return;
        _timestampFrequency = frequency;
        D3D12_QUERY_HEAP_DESC qd = default;
        qd.Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        qd.Count = 2u * (uint)Banks;   // begin+end timestamp per frame-in-flight bank
        ID3D12QueryHeap* heap;
        if (_device->CreateQueryHeap(&qd, __uuidof<ID3D12QueryHeap>(), (void**)&heap) < 0) return;
        _timestampHeap = heap;

        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = 2UL * Banks * sizeof(ulong); rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback;
        if (_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null,
            __uuidof<ID3D12Resource>(), (void**)&readback) < 0)
        {
            _timestampHeap->Release(); _timestampHeap = null; return;
        }
        _timestampReadback = readback;
        D3D12MemoryDiagnostics.Track(readback, "BakedBlur.TimestampReadback", 2UL * Banks * sizeof(ulong));
        void* mapped;
        if (readback->Map(0, null, &mapped) >= 0) _timestampData = (ulong*)mapped;
    }

    private void CollectGpuTime(BakedBlurQueue queue, int frameIndex)
    {
        int bank = frameIndex % Banks;
        if (!_timestampPending[bank] || _timestampData == null || _timestampFrequency == 0) return;
        int query = bank * 2;
        ulong begin = _timestampData[query], end = _timestampData[query + 1];
        _timestampPending[bank] = false;
        if (end >= begin) queue.ReportGpuTime((end - begin) * 1000.0 / _timestampFrequency);
    }

    public void PublishRecorded(BakedBlurQueue? queue)
    {
        if (!_hasRecordedResult || queue is null) return;
        queue.Post(in _recordedResult);
        _hasRecordedResult = false;
    }

    private void RecordCopy(ID3D12GraphicsCommandList* cmd, int srvSlot, D3D12_CPU_DESCRIPTOR_HANDLE rtv,
        int w, int h, float ux, float uy, float uw, float uh, float minX, float minY, float maxX, float maxY)
    {
        Array.Clear(_constants);
        _constants[0]=ux; _constants[1]=uy; _constants[2]=uw; _constants[3]=uh;
        _constants[4]=minX; _constants[5]=minY; _constants[6]=maxX; _constants[7]=maxY;
        BindPass(cmd, _copyPso, srvSlot, rtv, w, h);
    }

    private void RecordBlur(ID3D12GraphicsCommandList* cmd, int srvSlot, D3D12_CPU_DESCRIPTOR_HANDLE rtv,
        int w, int h, float sigma, float dirX, float dirY)
    {
        Array.Clear(_constants);
        _constants[0]=1f/ScratchSize; _constants[1]=1f/ScratchSize; _constants[2]=dirX; _constants[3]=dirY;
        _constants[4]=(float)w/ScratchSize; _constants[5]=(float)h/ScratchSize;
        _constants[6]=_constants[4]-0.5f/ScratchSize; _constants[7]=_constants[5]-0.5f/ScratchSize;
        Span<float> offsets = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        Span<float> weights = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        int count = AcrylicBackdropMath.BuildKernel(sigma, offsets, weights);
        for (int i=0;i<count;i++){_constants[8+i]=offsets[i];_constants[16+i]=weights[i];}
        BindPass(cmd, _blurPso, srvSlot, rtv, w, h);
    }

    private void BindPass(ID3D12GraphicsCommandList* cmd, ID3D12PipelineState* pso, int srvSlot,
        D3D12_CPU_DESCRIPTOR_HANDLE rtv, int w, int h)
    {
        cmd->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        D3D12_VIEWPORT vp = new() { Width=w, Height=h, MaxDepth=1f };
        RECT sc = new() { right=w, bottom=h };
        cmd->RSSetViewports(1, &vp); cmd->RSSetScissorRects(1, &sc);
        cmd->SetPipelineState(pso);
        fixed(float* c=_constants) cmd->SetGraphicsRoot32BitConstants(0,24,c,0);
        cmd->SetGraphicsRootDescriptorTable(1,SrvGpu(srvSlot));
        cmd->DrawInstanced(3,1,0,0);
    }

    private void BuildHeaps()
    {
        D3D12_DESCRIPTOR_HEAP_DESC sh = default; sh.Type=D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        sh.NumDescriptors=(uint)(3*Banks); sh.Flags=D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;   // source+A+B per bank
        ID3D12DescriptorHeap* s; Check(_device->CreateDescriptorHeap(&sh,__uuidof<ID3D12DescriptorHeap>(),(void**)&s),"BakedBlur.SrvHeap"); _srvHeap=s;
        D3D12_DESCRIPTOR_HEAP_DESC rh = default; rh.Type=D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV; rh.NumDescriptors=(uint)(3*Banks);   // A+B per bank, stride 3 to mirror the SRV slots
        ID3D12DescriptorHeap* r; Check(_device->CreateDescriptorHeap(&rh,__uuidof<ID3D12DescriptorHeap>(),(void**)&r),"BakedBlur.RtvHeap"); _rtvHeap=r;
        _srvInc=_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        _rtvInc=_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        D3D12MemoryDiagnostics.Track(_srvHeap,"BakedBlur.SrvHeap",(ulong)(3*Banks)*_srvInc);
        D3D12MemoryDiagnostics.Track(_rtvHeap,"BakedBlur.RtvHeap",(ulong)(3*Banks)*_rtvInc);
    }

    private void BuildRoot()
    {
        D3D12_DESCRIPTOR_RANGE range=default; range.RangeType=D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors=1; range.BaseShaderRegister=0; range.OffsetInDescriptorsFromTableStart=0xFFFFFFFF;
        D3D12_ROOT_PARAMETER* p=stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; p[0].Anonymous.Constants.Num32BitValues=24;
        p[0].ShaderVisibility=D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
        p[1].ParameterType=D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges=1; p[1].Anonymous.DescriptorTable.pDescriptorRanges=&range;
        p[1].ShaderVisibility=D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
        D3D12_STATIC_SAMPLER_DESC samp=default; samp.Filter=D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        samp.AddressU=samp.AddressV=samp.AddressW=D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.ShaderVisibility=D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL; samp.MaxLOD=float.MaxValue;
        D3D12_ROOT_SIGNATURE_DESC d=default; d.NumParameters=2; d.pParameters=p; d.NumStaticSamplers=1; d.pStaticSamplers=&samp;
        ID3DBlob* sig=null; ID3DBlob* err=null;
        Check(D3D12SerializeRootSignature(&d,D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,&sig,&err),"BakedBlur.RootSerialize");
        ID3D12RootSignature* root; Check(_device->CreateRootSignature(0,sig->GetBufferPointer(),sig->GetBufferSize(),__uuidof<ID3D12RootSignature>(),(void**)&root),"BakedBlur.Root");
        _root=root; sig->Release(); if(err!=null)err->Release();
    }

    private ID3D12PipelineState* BuildPso(string hlsl, string psEntry, string what)
    {
        ID3DBlob* vs=ShaderCompiler.Compile(hlsl,"VSMain","vs_5_1");
        ID3DBlob* ps=ShaderCompiler.Compile(hlsl,psEntry,"ps_5_1");
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd=default; pd.pRootSignature=_root;
        pd.VS=new D3D12_SHADER_BYTECODE{pShaderBytecode=vs->GetBufferPointer(),BytecodeLength=vs->GetBufferSize()};
        pd.PS=new D3D12_SHADER_BYTECODE{pShaderBytecode=ps->GetBufferPointer(),BytecodeLength=ps->GetBufferSize()};
        pd.PrimitiveTopologyType=D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pd.NumRenderTargets=1; pd.RTVFormats[0]=DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; pd.SampleDesc.Count=1; pd.SampleMask=uint.MaxValue;
        pd.RasterizerState.FillMode=D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID; pd.RasterizerState.CullMode=D3D12_CULL_MODE.D3D12_CULL_MODE_NONE; pd.RasterizerState.DepthClipEnable=BOOL.TRUE;
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask=(byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        pd.DepthStencilState.DepthEnable=BOOL.FALSE; pd.DepthStencilState.StencilEnable=BOOL.FALSE;
        ID3D12PipelineState* pso; Check(_device->CreateGraphicsPipelineState(&pd,__uuidof<ID3D12PipelineState>(),(void**)&pso),what);
        vs->Release(); ps->Release(); return pso;
    }

    private ID3D12Resource* CreateTarget(int w,int h,string name)
    {
        D3D12_HEAP_PROPERTIES hp=default; hp.Type=D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC rd=default; rd.Dimension=D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width=(uint)w; rd.Height=(uint)h; rd.DepthOrArraySize=1; rd.MipLevels=1; rd.Format=DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        rd.SampleDesc.Count=1; rd.Layout=D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN; rd.Flags=D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        // Query BEFORE CreateCommittedResource, on the SAME desc (audit gpu mem-02): the device-reported allocation
        // requirement, not an inferred w*h*4 pixel estimate — see D3D12MemoryDiagnostics.AllocationBytes.
        ulong bytes=D3D12MemoryDiagnostics.AllocationBytes(_device,&rd);
        D3D12_CLEAR_VALUE cv=default; cv.Format=rd.Format; ID3D12Resource* res;
        Check(_device->CreateCommittedResource(&hp,D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,&rd,D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,&cv,__uuidof<ID3D12Resource>(),(void**)&res),"BakedBlur.Target");
        string label=$"{name} {w}x{h}";
        D3D12MemoryDiagnostics.Track(res,D3D12MemoryDiagnostics.NameOrUnknown(label,bytes),bytes!=0?bytes:(ulong)w*(uint)h*4UL); return res;
    }

    private void CreateSrv(ID3D12Resource* res,D3D12_CPU_DESCRIPTOR_HANDLE cpu)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC sd=default; sd.Format=DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.ViewDimension=D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D; sd.Shader4ComponentMapping=0x1688; sd.Anonymous.Texture2D.MipLevels=1;
        _device->CreateShaderResourceView(res,&sd,cpu);
    }
    private D3D12_CPU_DESCRIPTOR_HANDLE SrvCpu(int i){var h=_srvHeap->GetCPUDescriptorHandleForHeapStart();h.ptr+=(nuint)((uint)i*_srvInc);return h;}
    private D3D12_GPU_DESCRIPTOR_HANDLE SrvGpu(int i){var h=_srvHeap->GetGPUDescriptorHandleForHeapStart();h.ptr+=(ulong)((uint)i*_srvInc);return h;}
    private D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int i){var h=_rtvHeap->GetCPUDescriptorHandleForHeapStart();h.ptr+=(nuint)((uint)i*_rtvInc);return h;}
    private static void Barrier(ID3D12GraphicsCommandList* cmd,ID3D12Resource* res,ref D3D12_RESOURCE_STATES state,D3D12_RESOURCE_STATES to)
    { if(state==to)return; D3D12_RESOURCE_BARRIER b=default;b.Type=D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;b.Anonymous.Transition.pResource=res;b.Anonymous.Transition.StateBefore=state;b.Anonymous.Transition.StateAfter=to;b.Anonymous.Transition.Subresource=0xFFFFFFFF;cmd->ResourceBarrier(1,&b);state=to; }
    private static void Check(HRESULT hr,string what){if((int)hr<0)throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");}

    public void Dispose()
    {
        if(_timestampReadback!=null){if(_timestampData!=null)_timestampReadback->Unmap(0,null);_timestampData=null;D3D12MemoryDiagnostics.Release(_timestampReadback,"BakedBlur.TimestampReadback");_timestampReadback->Release();_timestampReadback=null;}
        if(_timestampHeap!=null){_timestampHeap->Release();_timestampHeap=null;}
        for(int b=0;b<Banks;b++)
        {
            if(_scratchA[b]!=null){D3D12MemoryDiagnostics.Release(_scratchA[b],$"BakedBlur.ScratchA{b}");_scratchA[b]->Release();_scratchA[b]=null;}
            if(_scratchB[b]!=null){D3D12MemoryDiagnostics.Release(_scratchB[b],$"BakedBlur.ScratchB{b}");_scratchB[b]->Release();_scratchB[b]=null;}
        }
        if(_copyPso!=null){_copyPso->Release();_copyPso=null;} if(_blurPso!=null){_blurPso->Release();_blurPso=null;}
        if(_root!=null){_root->Release();_root=null;}
        if(_srvHeap!=null){D3D12MemoryDiagnostics.Release(_srvHeap,"BakedBlur.SrvHeap");_srvHeap->Release();_srvHeap=null;}
        if(_rtvHeap!=null){D3D12MemoryDiagnostics.Release(_rtvHeap,"BakedBlur.RtvHeap");_rtvHeap->Release();_rtvHeap=null;}
    }
}
