using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// PushLayer{Opacity} runner (E9): flat subtree alpha — WinUI Composition LayerVisual semantics. The subtree between
/// PushLayer(Kind=Opacity, GroupAlpha)…PopLayer renders at FULL alpha into a leased CANVAS-SIZED offscreen RT
/// (cleared transparent), then <see cref="Composite"/> draws that RT ONCE over the underlying target with every
/// channel scaled by the group alpha (premultiplied ⇒ a uniform ×α is the correct flat fade) — so overlapping
/// children never double-blend, unlike the default per-node multiplied opacity (which matches WinUI's plain
/// Visual.Opacity). Full-canvas RTs (not DeviceRect buckets) because group content legitimately extends past the
/// layer rect (shadow halos, focus rings outside bounds); nesting leases one slot per open group.
///
/// Pooling follows the SAME conventions the AcrylicCompositor's LayerPool established (gpu-renderer.md §7.1) —
/// duplicated here rather than shared because that pool is bucket-quantized + private to the acrylic leaf and this
/// unit does not own AcrylicCompositor.cs: same-queue REUSE needs no fence (DIRECT-queue execution is ordered and
/// barriers carry the state); DESTRUCTION is fence-gated (retired entries release only once the frame fence passes
/// their last use); shader-visible SRV descriptors are parity-banked per frame so a slot recreation never rewrites a
/// descriptor the in-flight frame still references; free entries idle past the trim window are retired so a closed
/// fading surface stops pinning VRAM. All ComPtrs live here on the render thread (threading-render-seam contract).
///
/// Known limitation (documented honestly): an Acrylic layer NESTED INSIDE an open opacity group composites into the
/// engine canvas, not into the group RT (the acrylic leaf owns its canvas binding) — so it won't fade with the group.
/// Put the acrylic and the opacity group on the same node instead of nesting them.
///
/// This unit ALSO runs the Expressive Motion Kit's per-node SELF-blur (DrawList LayerKind.Blur, NodePaint.BlurSigma):
/// a blur group is just an opacity group whose RT is separable-Gaussian-blurred (<see cref="BlurInPlace"/>) BEFORE the
/// flat composite — so a node's own pixels blur + fade together (the transitions.dev recipes). The kernel σ is dynamic
/// (it animates), so the blur shader computes the gaussian weights per-frame from a σ uniform (unlike the acrylic
/// compositor's fixed 30-DIP kernel, which is tuned for a static backdrop blur). needs-pixels: the blurred GPU pixels
/// are a --shot manual check; the headless harness asserts the LayerKind.Blur opcode + BlurSigma on HeadlessGpuDevice.
/// LastLayers. Perf note (honest): the blur runs over the full canvas-sized group RT; a per-layer-rect bucket (like the
/// acrylic LayerPool) is a deferred optimization.
/// </summary>
internal sealed unsafe class OpacityLayerCompositor : IDisposable
{
    private const int MaxPool = 4;            // ≥ max opacity-group nesting depth per frame
    private const int TrimIdleFrames = 600;   // free entries idle this long (~10 s) are retired (fence-gated release)

    private ID3D12Device* _device;
    private uint _w, _h;                      // canvas size (physical px) — every pooled RT matches it

    private struct PoolEntry
    {
        public ID3D12Resource* Res;            // null = empty slot
        public D3D12_RESOURCE_STATES State;    // tracked in record order (single DIRECT queue serializes execution)
        public uint W, H;
        public ulong LastUseFence;
        public int IdleFrames;
        public bool InUse;
    }
    private readonly PoolEntry[] _pool = new PoolEntry[MaxPool];

    private struct Retired { public ID3D12Resource* Res; public ulong Fence; }
    private readonly List<Retired> _retired = new();

    private ID3D12DescriptorHeap* _rtvHeap;   // MaxPool RTVs (slot i)
    private ID3D12DescriptorHeap* _srvHeap;   // 2·MaxPool shader-visible SRVs, parity-banked (parity·MaxPool + i)
    private uint _rtvInc, _srvInc;
    private int _parity;

    private ID3D12RootSignature* _root;       // 1 root const (alpha) + 1 SRV table + static linear-clamp sampler
    private ID3D12PipelineState* _pso;        // fullscreen triangle, src = RT × alpha, blend ONE/INV_SRC_ALPHA
    private ID3D12RootSignature* _blurRoot;   // self-blur: 8 root consts (texel size, axis dir, σ) + SRV table + linear sampler
    private ID3D12PipelineState* _blurPso;    // separable dynamic-σ gaussian, one axis per invocation (no blend — full overwrite)

    /// <summary>Opacity groups composited this frame (diagnostics).</summary>
    public int GroupsThisFrame { get; private set; }

    /// <summary>Live pooled RTs (diagnostics; steady state = nesting depth while any group is animating).</summary>
    public int PooledRtCount
    {
        get { int n = 0; for (int i = 0; i < MaxPool; i++) if (_pool[i].Res != null) n++; return n; }
    }

    // Fullscreen-triangle composite: sample the group RT and scale ALL channels by the group alpha (premultiplied).
    private const string Hlsl = """
cbuffer C : register(b0) { float4 prm; };   // x = group alpha
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 PSMain(V i) : SV_Target { return gSrc.Sample(gSamp, i.uv) * prm.x; }   // premultiplied × α = flat group fade
""";

    // The Expressive Motion Kit self-blur: a separable gaussian with a DYNAMIC σ (it animates) — weights are computed
    // per-frame from a σ uniform (px), unlike the acrylic compositor's fixed kernel. One axis per invocation; the host
    // ping-pongs the group RT ↔ a scratch RT. Premultiplied throughout, so transparent regions stay transparent.
    private const string BlurHlsl = """
cbuffer C : register(b0) { float4 texelDir; float4 sig; };   // texelDir.xy = 1/size, .zw = axis (1,0)|(0,1); sig.x = σ (px)
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 BlurPS(V i) : SV_Target
{
    float sigma = max(sig.x, 1e-3);
    float inv2s2 = 1.0 / (2.0 * sigma * sigma);
    float2 step = texelDir.zw * texelDir.xy;     // one-texel step along the blur axis
    int R = (int)min(ceil(sigma * 3.0), 32.0);   // ±3σ support, capped
    float4 acc = gSrc.Sample(gSamp, i.uv);        // centre tap, weight 1
    float wsum = 1.0;
    [loop] for (int k = 1; k <= R; k++)
    {
        float w = exp(-(float)(k * k) * inv2s2);
        acc += gSrc.Sample(gSamp, i.uv + step * k) * w;
        acc += gSrc.Sample(gSamp, i.uv - step * k) * w;
        wsum += 2.0 * w;
    }
    return acc / wsum;
}
""";

    public void Init(ID3D12Device* device)
    {
        _device = device;
        BuildHeaps();
        BuildPipeline();
        BuildBlurPipeline();
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    private void BuildHeaps()
    {
        D3D12_DESCRIPTOR_HEAP_DESC rh = default;
        rh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rh.NumDescriptors = MaxPool;
        ID3D12DescriptorHeap* rhp; Check(_device->CreateDescriptorHeap(&rh, __uuidof<ID3D12DescriptorHeap>(), (void**)&rhp), "OpacityLayer.RtvHeap");
        _rtvHeap = rhp;
        _rtvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        D3D12MemoryDiagnostics.Track(_rtvHeap, "OpacityLayer.RtvHeap", (ulong)rh.NumDescriptors * _rtvInc);

        D3D12_DESCRIPTOR_HEAP_DESC sh = default;
        sh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        sh.NumDescriptors = 2 * MaxPool;   // two parity banks (see _srvHeap comment)
        sh.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* shp; Check(_device->CreateDescriptorHeap(&sh, __uuidof<ID3D12DescriptorHeap>(), (void**)&shp), "OpacityLayer.SrvHeap");
        _srvHeap = shp;
        _srvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12MemoryDiagnostics.Track(_srvHeap, "OpacityLayer.SrvHeap", (ulong)sh.NumDescriptors * _srvInc);
    }

    private void BuildPipeline()
    {
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        range.BaseShaderRegister = 0;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;   // D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = 4;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
        p[1].Anonymous.DescriptorTable.pDescriptorRanges = &range;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC samp = default;
        samp.Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_POINT;   // 1:1 texel copy — no filtering wanted
        samp.AddressU = samp.AddressV = samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.ShaderRegister = 0;
        samp.ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
        samp.MaxLOD = float.MaxValue;

        D3D12_ROOT_SIGNATURE_DESC desc = default;
        desc.NumParameters = 2;
        desc.pParameters = p;
        desc.NumStaticSamplers = 1;
        desc.pStaticSamplers = &samp;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "OpacityLayer.SerializeRootSig");
        ID3D12RootSignature* rs;
        Check(_device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "OpacityLayer.CreateRootSig");
        _root = rs;
        sig->Release();
        if (err != null) err->Release();

        ID3DBlob* vs = Compile("VSMain", "vs_5_1");
        ID3DBlob* ps = Compile("PSMain", "ps_5_1");
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _root;
        pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
        pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
        pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pd.NumRenderTargets = 1;
        pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        pd.SampleDesc.Count = 1;
        pd.SampleMask = uint.MaxValue;
        pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
        pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
        pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
        pd.DepthStencilState.DepthEnable = BOOL.FALSE;
        pd.DepthStencilState.StencilEnable = BOOL.FALSE;
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;     // premultiplied SourceOver
        pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        ID3D12PipelineState* pso;
        Check(_device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "OpacityLayer.Pso");
        _pso = pso;
        vs->Release(); ps->Release();
    }

    // The self-blur pipeline: a LINEAR-clamp sampler (the composite PSO uses POINT) + 8 root consts (texel size, axis
    // dir, σ) + 1 SRV table, no blend (each pass fully overwrites its target). Separate from _root/_pso so the blur and
    // composite stay independent (the composite remains a 1:1 premultiplied × α copy).
    private void BuildBlurPipeline()
    {
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        range.BaseShaderRegister = 0;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;   // D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = 8;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
        p[1].Anonymous.DescriptorTable.pDescriptorRanges = &range;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC samp = default;
        samp.Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR;   // gaussian taps want bilinear
        samp.AddressU = samp.AddressV = samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.ShaderRegister = 0;
        samp.ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
        samp.MaxLOD = float.MaxValue;

        D3D12_ROOT_SIGNATURE_DESC desc = default;
        desc.NumParameters = 2;
        desc.pParameters = p;
        desc.NumStaticSamplers = 1;
        desc.pStaticSamplers = &samp;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "OpacityLayer.SerializeBlurRootSig");
        ID3D12RootSignature* rs;
        Check(_device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "OpacityLayer.CreateBlurRootSig");
        _blurRoot = rs;
        sig->Release();
        if (err != null) err->Release();

        ID3DBlob* vs = CompileSource(BlurHlsl, "VSMain", "vs_5_1");
        ID3DBlob* ps = CompileSource(BlurHlsl, "BlurPS", "ps_5_1");
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _blurRoot;
        pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
        pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
        pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pd.NumRenderTargets = 1;
        pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        pd.SampleDesc.Count = 1;
        pd.SampleMask = uint.MaxValue;
        pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
        pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
        pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
        pd.DepthStencilState.DepthEnable = BOOL.FALSE;
        pd.DepthStencilState.StencilEnable = BOOL.FALSE;
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.FALSE;   // full overwrite (the pass IS the new RT contents)
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        ID3D12PipelineState* pso;
        Check(_device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "OpacityLayer.BlurPso");
        _blurPso = pso;
        vs->Release(); ps->Release();
    }

    private static ID3DBlob* CompileSource(string hlsl, string entry, string target)
    {
        byte[] src = Encoding.ASCII.GetBytes(hlsl);
        byte[] ent = Encoding.ASCII.GetBytes(entry + "\0");
        byte[] tgt = Encoding.ASCII.GetBytes(target + "\0");
        ID3DBlob* code = null; ID3DBlob* err = null;
        fixed (byte* ps = src) fixed (byte* pe = ent) fixed (byte* pt = tgt)
        {
            HRESULT hr = D3DCompile(ps, (nuint)src.Length, null, null, null, (sbyte*)pe, (sbyte*)pt, 0, 0, &code, &err);
            if ((int)hr < 0)
            {
                string msg = err != null ? Marshal.PtrToStringAnsi((nint)err->GetBufferPointer()) ?? "" : "";
                throw new InvalidOperationException($"opacity-layer blur shader {entry} failed: {msg}");
            }
        }
        return code;
    }

    private static ID3DBlob* Compile(string entry, string target)
    {
        byte[] src = Encoding.ASCII.GetBytes(Hlsl);
        byte[] ent = Encoding.ASCII.GetBytes(entry + "\0");
        byte[] tgt = Encoding.ASCII.GetBytes(target + "\0");
        ID3DBlob* code = null; ID3DBlob* err = null;
        fixed (byte* ps = src) fixed (byte* pe = ent) fixed (byte* pt = tgt)
        {
            HRESULT hr = D3DCompile(ps, (nuint)src.Length, null, null, null, (sbyte*)pe, (sbyte*)pt, 0, 0, &code, &err);
            if ((int)hr < 0)
            {
                string msg = err != null ? Marshal.PtrToStringAnsi((nint)err->GetBufferPointer()) ?? "" : "";
                throw new InvalidOperationException($"opacity-layer shader {entry} failed: {msg}");
            }
        }
        return code;
    }

    /// <summary>Adopt the canvas size; size-mismatched pool entries are retired (fence-gated). Allocation itself is
    /// lazy — nothing is created until the first <see cref="Acquire"/>.</summary>
    public void EnsureSize(uint w, uint h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == _w && h == _h) return;
        _w = w; _h = h;
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || (e.W == w && e.H == h)) continue;
            _retired.Add(new Retired { Res = e.Res, Fence = e.LastUseFence });
            e = default;
        }
    }

    private D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int slot) { var h = _rtvHeap->GetCPUDescriptorHandleForHeapStart(); h.ptr += (nuint)slot * _rtvInc; return h; }
    private D3D12_CPU_DESCRIPTOR_HANDLE SrvCpu(int slot) { var h = _srvHeap->GetCPUDescriptorHandleForHeapStart(); h.ptr += (nuint)slot * _srvInc; return h; }
    private D3D12_GPU_DESCRIPTOR_HANDLE SrvGpu(int slot) { var h = _srvHeap->GetGPUDescriptorHandleForHeapStart(); h.ptr += (ulong)slot * _srvInc; return h; }
    private int PoolSrvSlot(int i) => _parity * MaxPool + i;

    private void DrainRetired(ulong completedFence)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Fence > completedFence) continue;
            D3D12MemoryDiagnostics.Release(_retired[i].Res, "OpacityLayer.Pool");
            _retired[i].Res->Release();
            _retired.RemoveAt(i);
        }
    }

    private void TickPool(ulong completedFence)
    {
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || e.InUse) continue;
            if (++e.IdleFrames <= TrimIdleFrames) continue;
            _retired.Add(new Retired { Res = e.Res, Fence = e.LastUseFence });
            e = default;
        }
        DrainRetired(completedFence);
    }

    /// <summary>Per-frame upkeep on the layered (canvas) path. <paramref name="parity"/> = frameIndex &amp; 1.</summary>
    public void BeginFrame(ulong completedFence, int parity)
    {
        _parity = parity & 1;
        GroupsThisFrame = 0;
        TickPool(completedFence);
    }

    /// <summary>Idle upkeep for frames with no layers at all (mirrors AcrylicCompositor.TickIdle).</summary>
    public void TickIdle(ulong completedFence)
    {
        if (_retired.Count > 0 || PooledRtCount > 0) TickPool(completedFence);
        GroupsThisFrame = 0;
    }

    /// <summary>Lease a canvas-sized RT, clear it transparent, and bind it as the current render target (the caller's
    /// scissor/viewport state is untouched — the subtree keeps clipping identically). Returns the slot index for the
    /// matching <see cref="BeginRead"/>/<see cref="Composite"/>/<see cref="Release"/>. Steady state reuses the free
    /// list with zero resource creation.</summary>
    public int Acquire(ID3D12GraphicsCommandList* cmd, ulong frameFence)
    {
        int best = -1;
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || e.InUse || e.W != _w || e.H != _h) continue;
            best = i; break;
        }
        if (best < 0)
        {
            int slot = -1;
            for (int i = 0; i < MaxPool; i++) if (_pool[i].Res == null) { slot = i; break; }
            if (slot < 0)
            {
                for (int i = 0; i < MaxPool; i++)
                {
                    if (_pool[i].InUse) continue;
                    if (slot < 0 || _pool[i].LastUseFence < _pool[slot].LastUseFence) slot = i;
                }
                if (slot < 0) throw new InvalidOperationException("opacity-layer pool exhausted (group nesting deeper than MaxPool)");
                _retired.Add(new Retired { Res = _pool[slot].Res, Fence = _pool[slot].LastUseFence });
                _pool[slot] = default;
            }
            _pool[slot].Res = CreateTarget(_w, _h, $"OpacityLayer.Pool[{slot}]");
            _pool[slot].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _pool[slot].W = _w; _pool[slot].H = _h;
            _device->CreateRenderTargetView(_pool[slot].Res, null, Rtv(slot));   // RTVs are CPU-read at record time — safe to rewrite
            best = slot;
        }

        ref var entry = ref _pool[best];
        entry.InUse = true;
        entry.IdleFrames = 0;
        entry.LastUseFence = frameFence;
        CreateSrv(entry.Res, SrvCpu(PoolSrvSlot(best)));   // refresh THIS frame's parity bank

        Barrier(cmd, entry.Res, ref entry.State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = Rtv(best);
        cmd->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        float* clear = stackalloc float[4] { 0f, 0f, 0f, 0f };   // transparent — only the subtree's pixels composite
        cmd->ClearRenderTargetView(rtv, clear, 0, null);          // clears the whole RT regardless of scissor
        return best;
    }

    /// <summary>Re-bind a leased slot as the render target (after an intervening pass switched targets).</summary>
    public void Bind(ID3D12GraphicsCommandList* cmd, int slot)
    {
        ref var entry = ref _pool[slot];
        Barrier(cmd, entry.Res, ref entry.State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = Rtv(slot);
        cmd->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
    }

    /// <summary>Transition the slot to shader-readable. Call BEFORE the caller binds the underlying target for
    /// <see cref="Composite"/> (split out so the caller controls which target the composite lands on).</summary>
    public void BeginRead(ID3D12GraphicsCommandList* cmd, int slot)
        => Barrier(cmd, _pool[slot].Res, ref _pool[slot].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

    /// <summary>Composite the leased RT over the CURRENTLY-BOUND render target at <paramref name="alpha"/> (a
    /// fullscreen pass; the group RT is canvas-sized and 1:1). The current scissor applies — it is the group node's
    /// enclosing clip, which already bounded the subtree's content.</summary>
    public void Composite(ID3D12GraphicsCommandList* cmd, int slot, float alpha)
    {
        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_root);
        cmd->SetPipelineState(_pso);
        float* c = stackalloc float[4] { Math.Clamp(alpha, 0f, 1f), 0f, 0f, 0f };
        cmd->SetGraphicsRoot32BitConstants(0, 4, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(slot)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        cmd->DrawInstanced(3, 1, 0, 0);
        GroupsThisFrame++;
    }

    /// <summary>Return the slot to the free list (same-queue reuse needs no fence).</summary>
    public void Release(int slot) => _pool[slot].InUse = false;

    /// <summary>Separable-Gaussian-blur the group RT <paramref name="groupSlot"/> IN PLACE (the Expressive Motion Kit
    /// self-blur). The slot holds the subtree at full alpha; this leases a scratch RT, blurs H (group → scratch) then V
    /// (scratch → group) at dynamic σ <paramref name="sigma"/> px, releases the scratch, and leaves the group readable
    /// (PIXEL_SHADER_RESOURCE) for the caller's <see cref="Composite"/>. Leaves the FULL viewport set (the two passes
    /// cover the whole canvas-sized RT) — the caller composites fullscreen afterward.</summary>
    public void BlurInPlace(ID3D12GraphicsCommandList* cmd, int groupSlot, float sigma, ulong frameFence)
    {
        int scratch = Acquire(cmd, frameFence);   // leased + cleared + bound as RT (the pass-H output)
        // pass H: group (SRV) → scratch (RT)
        BeginRead(cmd, groupSlot);
        Bind(cmd, scratch);
        SetViewport(cmd, _w, _h);
        BlurPass(cmd, groupSlot, sigma, 1f, 0f);
        // pass V: scratch (SRV) → group (RT)
        BeginRead(cmd, scratch);
        Bind(cmd, groupSlot);
        SetViewport(cmd, _w, _h);
        BlurPass(cmd, scratch, sigma, 0f, 1f);
        // leave the group readable for the composite; the scratch returns to the free list (same-queue reuse, no fence)
        BeginRead(cmd, groupSlot);
        Release(scratch);
    }

    private void BlurPass(ID3D12GraphicsCommandList* cmd, int srcSlot, float sigma, float dirX, float dirY)
    {
        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_blurRoot);
        cmd->SetPipelineState(_blurPso);
        float* c = stackalloc float[8] { 1f / _w, 1f / _h, dirX, dirY, sigma, 0f, 0f, 0f };
        cmd->SetGraphicsRoot32BitConstants(0, 8, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(srcSlot)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        cmd->DrawInstanced(3, 1, 0, 0);
    }

    private void SetViewport(ID3D12GraphicsCommandList* cmd, uint w, uint h)
    {
        D3D12_VIEWPORT vp = new() { Width = w, Height = h, MaxDepth = 1 };
        RECT sc = new() { right = (int)w, bottom = (int)h };
        cmd->RSSetViewports(1, &vp);
        cmd->RSSetScissorRects(1, &sc);
    }

    private ID3D12Resource* CreateTarget(uint w, uint h, string name)
    {
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = w; rd.Height = h; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        D3D12_CLEAR_VALUE cv = default; cv.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;   // transparent clear
        ID3D12Resource* res;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, &cv,
            __uuidof<ID3D12Resource>(), (void**)&res), "OpacityLayer.CreateTarget");
        D3D12MemoryDiagnostics.Track(res, $"{name} {w}x{h}", (ulong)w * h * 4UL);
        return res;
    }

    private void CreateSrv(ID3D12Resource* tex, D3D12_CPU_DESCRIPTOR_HANDLE h)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
        sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = 0x1688;   // identity RGBA
        sd.Anonymous.Texture2D.MipLevels = 1;
        _device->CreateShaderResourceView(tex, &sd, h);
    }

    private static void Barrier(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, ref D3D12_RESOURCE_STATES state, D3D12_RESOURCE_STATES to)
    {
        if (state == to) return;
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = state;
        b.Anonymous.Transition.StateAfter = to;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        cmd->ResourceBarrier(1, &b);
        state = to;
    }

    public void Dispose()
    {
        // The device fenced the GPU idle before disposing (D3D12Device.Dispose → WaitForGpu) — immediate release is safe.
        for (int i = 0; i < MaxPool; i++)
        {
            if (_pool[i].Res == null) continue;
            D3D12MemoryDiagnostics.Release(_pool[i].Res, "OpacityLayer.Pool");
            _pool[i].Res->Release();
            _pool[i] = default;
        }
        for (int i = 0; i < _retired.Count; i++)
        {
            D3D12MemoryDiagnostics.Release(_retired[i].Res, "OpacityLayer.Pool");
            _retired[i].Res->Release();
        }
        _retired.Clear();
        if (_rtvHeap != null) { D3D12MemoryDiagnostics.Release(_rtvHeap, "OpacityLayer.RtvHeap"); _rtvHeap->Release(); }
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "OpacityLayer.SrvHeap"); _srvHeap->Release(); }
        if (_pso != null) _pso->Release();
        if (_root != null) _root->Release();
        if (_blurPso != null) _blurPso->Release();
        if (_blurRoot != null) _blurRoot->Release();
    }
}
