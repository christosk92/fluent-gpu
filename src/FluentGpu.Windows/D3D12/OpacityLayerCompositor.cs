using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using FluentGpu.Render;

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
/// LastLayers. Perf: the RT stays canvas-sized (group content can extend past the layer rect), but the two blur passes
/// are SCISSORED to the layer's device rect + the kernel's ±3σ tap halo (<see cref="BlurInPlace"/>) — pixel-identical to
/// a full-canvas blur (the kernel output is zero past that halo) at a fraction of the fill for a small surface (a
/// one-line lyrics glow is a ~44 px band, not the whole window).
/// </summary>
internal sealed unsafe class OpacityLayerCompositor : IDisposable
{
    private const int MaxPool = 32;           // CANVAS-sized transient group slots + small REGION-sized blur-cache PINS (one pool)
    private const int TrimIdleFrames = 600;   // free entries idle this long (~10 s) are retired (fence-gated release)

    private ID3D12Device* _device;
    private uint _w, _h;                      // canvas size (physical px) — transient slots match it; PINS are region-sized

    private struct PoolEntry
    {
        public ID3D12Resource* Res;            // null = empty slot
        public D3D12_RESOURCE_STATES State;    // tracked in record order (single DIRECT queue serializes execution)
        public uint W, H;                      // RT dims — canvas for a transient slot, the blur region for a PIN
        public ulong LastUseFence;
        public int IdleFrames;
        public bool InUse;
        public ulong PinHash;                  // 0 = transient slot; else the blur-cache content hash this slot retains
        public bool BlurReady;                 // (pins) the RT currently holds the FINAL blurred result for PinHash
        public int RegionX, RegionY;           // (pins) physical-px screen origin of the retained blur region (for the composite)
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
    private ID3D12RootSignature* _edgeRoot;   // edge fade: 16 root consts (rect + per-edge band + corner radii + falloff/intensity/alpha) + SRV + point sampler
    private ID3D12PipelineState* _edgePso;    // per-edge feather composite (premultiplied SourceOver, like _pso) — follows the rounded corners (the curve)

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
    // LINEAR-SAMPLED Gaussian: fold each adjacent tap pair (k, k+1) into ONE bilinear fetch at the weight-interpolated
    // offset (a LINEAR sampler returns w0*texel_k + w1*texel_{k+1}), halving the texture fetches for an identical kernel
    // — the standard separable-blur optimization. The LINEAR sampler (BuildBlurPipeline) makes the midpoint fetch exact.
    [loop] for (int k = 1; k <= R; k += 2)
    {
        float w0 = exp(-(float)(k * k) * inv2s2);
        float w1 = (k + 1 <= R) ? exp(-(float)((k + 1) * (k + 1)) * inv2s2) : 0.0;
        float wp = w0 + w1;
        float off = wp > 1e-8 ? (k * w0 + (k + 1) * w1) / wp : (float)k;   // bilinear midpoint → exact pair weight
        acc += gSrc.Sample(gSamp, i.uv + step * off) * wp;
        acc += gSrc.Sample(gSamp, i.uv - step * off) * wp;
        wsum += 2.0 * wp;
    }
    return acc / wsum;
}
""";

    // Edge fade: a 1:1 composite (POINT sampler, premultiplied SourceOver — like the opacity composite) that multiplies
    // the sampled premultiplied layer by a per-edge feather. The inward distance is to the nearest ENABLED edge,
    // normalized by that edge's band; where a rounded corner's TWO adjacent edges both fade, the distance follows the
    // corner ARC (the curve) — so the fade hugs the corner instead of the straight edge. Portable HLSL (smoothstep/lerp/
    // saturate/length only — no D2D1) → a Metal PSMainEdgeFade re-implements this verbatim.
    private const string EdgeFadeHlsl = """
cbuffer C : register(b0) { float4 rect; float4 band; float4 corner; float4 misc; };
// rect = device (minX,minY,maxX,maxY); band = per-edge depth px (L,T,R,B), 0 = disabled; corner = radii px (TL,TR,BR,BL);
// misc = (falloff 0=lin/1=smooth/2=cubic, intensity 0..1, groupAlpha, unused).
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float curveF(float t, float mode) { t = saturate(t); if (mode < 0.5) return t; if (mode < 1.5) return t * t * (3.0 - 2.0 * t); return t * t * t; }
// normalized inward arc distance for one corner — only inside its quadrant + when active (else 1e9 = no effect).
float arcN(float2 p, float2 c, float r, float cb, bool act) { return (act && r > 0.0 && cb > 0.0) ? (r - length(p - c)) / cb : 1e9; }
float4 PSMain(V i) : SV_Target
{
    float4 src = gSrc.Sample(gSamp, i.uv);   // premultiplied
    float2 p = i.pos.xy;                       // device pixel centre
    float bx = band.x, bt = band.y, brr = band.z, bb = band.w;
    float n = 1e9;                             // nearest enabled straight edge, normalized by its own band
    if (bx > 0.0)  n = min(n, (p.x - rect.x) / bx);
    if (bt > 0.0)  n = min(n, (p.y - rect.y) / bt);
    if (brr > 0.0) n = min(n, (rect.z - p.x) / brr);
    if (bb > 0.0)  n = min(n, (rect.w - p.y) / bb);
    // rounded-corner curve: a corner contributes its arc only where BOTH adjacent edges fade.
    float2 tl = float2(rect.x + corner.x, rect.y + corner.x);
    n = min(n, arcN(p, tl, corner.x, min(bx, bt),  bx > 0.0 && bt > 0.0 && p.x < tl.x && p.y < tl.y));
    float2 tr = float2(rect.z - corner.y, rect.y + corner.y);
    n = min(n, arcN(p, tr, corner.y, min(brr, bt), brr > 0.0 && bt > 0.0 && p.x > tr.x && p.y < tr.y));
    float2 br2 = float2(rect.z - corner.z, rect.w - corner.z);
    n = min(n, arcN(p, br2, corner.z, min(brr, bb), brr > 0.0 && bb > 0.0 && p.x > br2.x && p.y > br2.y));
    float2 bl = float2(rect.x + corner.w, rect.w - corner.w);
    n = min(n, arcN(p, bl, corner.w, min(bx, bb), bx > 0.0 && bb > 0.0 && p.x < bl.x && p.y > bl.y));
    float feather = curveF(n, misc.x);        // 0 at the boundary → 1 at band depth
    return src * (lerp(1.0, feather, misc.y) * misc.z);   // premultiplied × feather × groupAlpha
}
""";

    public void Init(ID3D12Device* device)
    {
        _device = device;
        BuildHeaps();
        BuildPipeline();
        BuildBlurPipeline();
        BuildEdgeFadePipeline();
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

    // The edge-fade pipeline: a POINT sampler (1:1 copy, like the composite) + 16 root consts (rect + per-edge band +
    // corner radii + falloff/intensity/groupAlpha) + 1 SRV table, premultiplied SourceOver blend (it composites the
    // feathered layer over the underlying target). Separate from _root/_pso so the plain composite stays a 4-const copy.
    private void BuildEdgeFadePipeline()
    {
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        range.BaseShaderRegister = 0;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = 16;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
        p[1].Anonymous.DescriptorTable.pDescriptorRanges = &range;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC samp = default;
        samp.Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_POINT;   // 1:1 texel copy
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
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "OpacityLayer.SerializeEdgeRootSig");
        ID3D12RootSignature* rs;
        Check(_device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "OpacityLayer.CreateEdgeRootSig");
        _edgeRoot = rs;
        sig->Release();
        if (err != null) err->Release();

        ID3DBlob* vs = CompileSource(EdgeFadeHlsl, "VSMain", "vs_5_1");
        ID3DBlob* ps = CompileSource(EdgeFadeHlsl, "PSMain", "ps_5_1");
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _edgeRoot;
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
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;     // premultiplied SourceOver (like the opacity composite)
        pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        ID3D12PipelineState* pso;
        Check(_device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "OpacityLayer.EdgePso");
        _edgePso = pso;
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
            if (e.Res == null || e.InUse || e.W != _w || e.H != _h || e.PinHash != 0) continue;   // never grab a retained pin as a transient slot
            best = i; break;
        }
        if (best < 0)
        {
            int slot = -1;
            for (int i = 0; i < MaxPool; i++) if (_pool[i].Res == null) { slot = i; break; }
            if (slot < 0)
            {
                // Evict the LRU non-in-use slot, preferring an UNPINNED (transient) victim — a displaced blur-cache pin
                // merely re-blurs the next time it is needed, whereas evicting a transient slot loses nothing.
                for (int i = 0; i < MaxPool; i++)
                {
                    if (_pool[i].InUse) continue;
                    if (slot < 0) { slot = i; continue; }
                    bool iUn = _pool[i].PinHash == 0, sUn = _pool[slot].PinHash == 0;
                    if (iUn != sUn) { if (iUn) slot = i; continue; }
                    if (_pool[i].LastUseFence < _pool[slot].LastUseFence) slot = i;
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

    /// <summary>Composite the leased RT over the currently-bound target while feathering its premultiplied alpha per-edge
    /// (following the rounded corners) per the edge-fade fields of <paramref name="L"/> — the alpha-mask edge fade. The
    /// group RT is canvas-sized + 1:1, so the layer's device rect lives in the same space as <c>SV_Position</c>.</summary>
    public void EdgeFadeComposite(ID3D12GraphicsCommandList* cmd, int slot, in PushLayerCmd L, float scale)
    {
        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_edgeRoot);
        cmd->SetPipelineState(_edgePso);
        // DeviceRect / bands / corners arrive in LOGICAL (DIP) device space; SV_Position is PHYSICAL px — scale by the
        // frame DPI factor (the acrylic composite does the same, AcrylicCompositor.SnapshotRegion).
        float* c = stackalloc float[16]
        {
            L.DeviceRect.X * scale, L.DeviceRect.Y * scale, (L.DeviceRect.X + L.DeviceRect.W) * scale, (L.DeviceRect.Y + L.DeviceRect.H) * scale,
            L.FadeBandL * scale, L.FadeBandT * scale, L.FadeBandR * scale, L.FadeBandB * scale,
            L.Radii.TopLeft * scale, L.Radii.TopRight * scale, L.Radii.BottomRight * scale, L.Radii.BottomLeft * scale,
            L.FadeFalloff, Math.Clamp(L.FadeIntensity, 0f, 1f), Math.Clamp(L.GroupAlpha, 0f, 1f), L.FadeEdges,
        };
        cmd->SetGraphicsRoot32BitConstants(0, 16, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(slot)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        cmd->DrawInstanced(3, 1, 0, 0);
        GroupsThisFrame++;
    }

    /// <summary>Return the slot to the free list (same-queue reuse needs no fence).</summary>
    public void Release(int slot) => _pool[slot].InUse = false;

    // ── Cross-frame BLUR CACHE (capable + WEAK GPUs go idle/cool when STATIONARY instead of re-blurring every submit) ────
    // A self-blur whose subtree draw-bytes + σ are byte-identical to a previous frame's reuses that frame's RETAINED,
    // already-blurred pixels (a "pin"), keyed by a content hash the caller (D3D12Device) folds from the layer's drawlist
    // segment. Stationary lyrics — the dimmed neighbours unchanged — then skip the subtree render + BOTH Gaussian passes
    // and just re-composite the pin. KEY DESIGN: a pin is a small REGION-SIZED RT (the blur's DeviceRect + ±3σ halo, e.g.
    // ~340×120 px for a rail line ≈ 160 KB), NOT a full-canvas RT (33 MB at 4K) — so 16 stationary lines pin in a few MB,
    // and a momentary scroll recurrence can't thrash VRAM (the canvas-sized-pin design did, regressing 4K scroll). The pin
    // is filled by a CopyTextureRegion out of the transient scratch right after the normal miss composite, so the render +
    // blur path is UNCHANGED (no device-viewport translation) and a HIT is provably PIXEL-IDENTICAL to a miss (bit-exact
    // copy; the UV[0,1] composite over the region viewport samples the same texel centres). Pins share the pool with the
    // canvas transient slots (PinHash != 0 ⇒ retained), LRU-evicted under pressure (unpinned victims first) and trimmed
    // when idle (surface closed). Only the blur tiers create pins; the whole path is gated by FG_BLUR_CACHE.

    /// <summary>The physical-px box a self-blur actually writes: DeviceRect (DIP × <paramref name="scale"/>) inflated by
    /// the kernel's ±min(ceil(3σ),32)px tap halo, clamped to the canvas. The region a pin must capture + composite —
    /// identical to <see cref="BlurInPlace"/>'s scissor, so the pinned strip is exactly the blurred strip.</summary>
    private void RegionBox(in PushLayerCmd L, float scale, out int minX, out int minY, out int maxX, out int maxY)
    {
        int rTaps = (int)MathF.Min(MathF.Ceiling(L.BlurSigma * 3f), 32f);
        minX = Math.Max(0, (int)MathF.Floor(L.DeviceRect.X * scale) - rTaps);
        minY = Math.Max(0, (int)MathF.Floor(L.DeviceRect.Y * scale) - rTaps);
        maxX = Math.Min((int)_w, (int)MathF.Ceiling((L.DeviceRect.X + L.DeviceRect.W) * scale) + rTaps);
        maxY = Math.Min((int)_h, (int)MathF.Ceiling((L.DeviceRect.Y + L.DeviceRect.H) * scale) + rTaps);
    }

    /// <summary>A valid (already-blurred) region pin for <paramref name="hash"/>, refreshed into this frame's SRV bank,
    /// or -1. The pin is left PIXEL_SHADER_RESOURCE between frames, so the caller composites it with no barrier.</summary>
    public int FindPin(ulong hash)
    {
        if (hash == 0) return -1;
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || e.InUse || e.PinHash != hash || !e.BlurReady) continue;
            e.IdleFrames = 0;
            CreateSrv(e.Res, SrvCpu(PoolSrvSlot(i)));   // refresh THIS frame's parity bank for the composite
            return i;
        }
        return -1;
    }

    /// <summary>Cache-MISS completion: COPY the just-blurred region out of the transient scratch slot
    /// <paramref name="scratchSlot"/> into a small, REGION-SIZED retained pin keyed by <paramref name="hash"/>, so next
    /// frame's <see cref="FindPin"/> composites it without re-rendering+re-blurring. Reuses this hash's existing pin RT if
    /// the region size still matches; else allocates (evicting an unpinned victim first). The scratch holds the final
    /// blurred subtree (PIXEL_SHADER_RESOURCE after <see cref="BlurInPlace"/> + the miss composite); this leaves it in
    /// COPY_SOURCE (the next <see cref="Acquire"/> transitions it back). A no-op (just re-blurs next frame) if the pool is
    /// momentarily full of in-use scratch.</summary>
    public void RetainPinFromScratch(ID3D12GraphicsCommandList* cmd, int scratchSlot, ulong hash, in PushLayerCmd L, float scale, ulong frameFence)
    {
        RegionBox(in L, scale, out int minX, out int minY, out int maxX, out int maxY);
        uint rw = (uint)Math.Max(1, maxX - minX), rh = (uint)Math.Max(1, maxY - minY);

        int slot = -1;
        for (int i = 0; i < MaxPool; i++)   // reuse this hash's existing same-size pin if it survived
            if (_pool[i].Res != null && !_pool[i].InUse && _pool[i].PinHash == hash && _pool[i].W == rw && _pool[i].H == rh) { slot = i; break; }
        if (slot < 0) for (int i = 0; i < MaxPool; i++) if (_pool[i].Res == null) { slot = i; break; }   // cold growth
        if (slot < 0)
        {
            for (int i = 0; i < MaxPool; i++)   // LRU evict, preferring an UNPINNED (transient) victim
            {
                if (_pool[i].InUse) continue;
                if (slot < 0) { slot = i; continue; }
                bool iUn = _pool[i].PinHash == 0, sUn = _pool[slot].PinHash == 0;
                if (iUn != sUn) { if (iUn) slot = i; continue; }
                if (_pool[i].LastUseFence < _pool[slot].LastUseFence) slot = i;
            }
            if (slot < 0) return;   // pool momentarily all in-use scratch — skip the pin (re-blurs next frame, no correctness loss)
        }
        ref var e = ref _pool[slot];
        if (e.Res != null && (e.W != rw || e.H != rh))   // wrong-size RT (different region) → retire (fence-gated) + reallocate
        {
            _retired.Add(new Retired { Res = e.Res, Fence = e.LastUseFence });
            e = default;
        }
        if (e.Res == null)
        {
            e.Res = CreateTarget(rw, rh, $"OpacityLayer.Pin[{slot}]");
            e.State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            e.W = rw; e.H = rh;
        }

        // COPY the scratch's [minX,minY → maxX,maxY] strip to the pin's (0,0). Bit-exact (same format, no resample).
        Barrier(cmd, _pool[scratchSlot].Res, ref _pool[scratchSlot].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
        Barrier(cmd, e.Res, ref e.State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_TEXTURE_COPY_LOCATION dst = default; dst.pResource = e.Res; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.Anonymous.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION src = default; src.pResource = _pool[scratchSlot].Res; src.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; src.Anonymous.SubresourceIndex = 0;
        D3D12_BOX b = new() { left = (uint)minX, top = (uint)minY, front = 0, right = (uint)maxX, bottom = (uint)maxY, back = 1 };
        cmd->CopyTextureRegion(&dst, 0, 0, 0, &src, &b);
        Barrier(cmd, e.Res, ref e.State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

        e.PinHash = hash; e.BlurReady = true; e.InUse = false; e.IdleFrames = 0; e.LastUseFence = frameFence;
        e.RegionX = minX; e.RegionY = minY;
    }

    /// <summary>Composite a cache-HIT REGION pin over the currently-bound target at its screen position. The pin holds the
    /// blurred DeviceRect+halo strip; set the viewport to that screen rect so the fullscreen-triangle's UV[0,1] maps the
    /// whole pin onto the same pixels the miss composite wrote (PIXEL-IDENTICAL). Restores the full-canvas viewport after,
    /// preserving the device's invariant (it sets the viewport ONCE); the caller re-applies its enclosing clip.</summary>
    public void CompositePinnedBlur(ID3D12GraphicsCommandList* cmd, int slot, float alpha, in PushLayerCmd L, float scale)
    {
        RegionBox(in L, scale, out int minX, out int minY, out int maxX, out int maxY);
        int rw = maxX - minX, rh = maxY - minY;
        if (rw <= 0 || rh <= 0) return;
        D3D12_VIEWPORT vp = new() { TopLeftX = minX, TopLeftY = minY, Width = rw, Height = rh, MaxDepth = 1 };
        RECT box = new() { left = minX, top = minY, right = maxX, bottom = maxY };
        cmd->RSSetViewports(1, &vp);
        cmd->RSSetScissorRects(1, &box);
        Composite(cmd, slot, alpha);
        SetViewport(cmd, _w, _h);   // restore the full-canvas viewport (+ full scissor; the caller re-applies its clip)
    }

    /// <summary>Separable-Gaussian-blur the group RT <paramref name="groupSlot"/> IN PLACE (the Expressive Motion Kit
    /// self-blur). The slot holds the subtree at full alpha; this leases a scratch RT, blurs H (group → scratch) then V
    /// (scratch → group) at dynamic σ <paramref name="sigma"/> px, releases the scratch, and leaves the group readable
    /// (PIXEL_SHADER_RESOURCE) for the caller's <see cref="Composite"/>.
    /// Both passes are SCISSORED to <paramref name="L"/>.DeviceRect × <paramref name="scale"/> inflated by the kernel's
    /// tap radius (±min(ceil(3σ),32) px — the same cap BlurHlsl uses), NOT the whole canvas. The subtree's pixels live
    /// inside DeviceRect and the kernel output is identically zero past that halo, so every non-zero tap the V pass reads
    /// was written by the H pass inside the scissor — the result is pixel-identical to the old full-canvas blur while
    /// touching only the layer's strip. The full viewport is kept so the fullscreen-triangle's SV_Position↔texel mapping
    /// (canvas-sized 1:1 RT) is unchanged; only rasterization is clipped. Leaves the tight scissor set — the composite
    /// that follows only needs the same region (content ⊆ it), and the caller restores the enclosing clip afterward.</summary>
    public void BlurInPlace(ID3D12GraphicsCommandList* cmd, int groupSlot, float sigma, ulong frameFence, in PushLayerCmd L, float scale)
    {
        // The halo-inflated device rect (RegionBox = DeviceRect×scale ± ±min(ceil(3σ),32)px) — MUST match BlurHlsl's R so
        // the scissor keeps every non-zero tap, and MUST match the blur-cache pin region (RetainPinFromScratch copies it).
        RegionBox(in L, scale, out int minX, out int minY, out int maxX, out int maxY);
        if (maxX <= minX || maxY <= minY) { BeginRead(cmd, groupSlot); return; }   // off-screen / degenerate — nothing to blur
        RECT box = new() { left = minX, top = minY, right = maxX, bottom = maxY };

        int scratch = Acquire(cmd, frameFence);   // leased + cleared (WHOLE RT → transparent outside the scissor) + bound
        // pass H: group (SRV) → scratch (RT), clipped to the halo-inflated device rect
        BeginRead(cmd, groupSlot);
        Bind(cmd, scratch);
        SetViewport(cmd, _w, _h);                 // full viewport (1:1 mapping); the scissor below bounds the work
        cmd->RSSetScissorRects(1, &box);
        BlurPass(cmd, groupSlot, sigma, 1f, 0f);
        // pass V: scratch (SRV) → group (RT), same scissor
        BeginRead(cmd, scratch);
        Bind(cmd, groupSlot);
        SetViewport(cmd, _w, _h);
        cmd->RSSetScissorRects(1, &box);
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
        if (_edgePso != null) _edgePso->Release();
        if (_edgeRoot != null) _edgeRoot->Release();
    }
}
