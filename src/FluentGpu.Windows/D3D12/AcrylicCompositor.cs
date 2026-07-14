using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using ColorF = FluentGpu.Foundation.ColorF;
using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Real per-node acrylic (frosted glass) — the in-app acrylic effect runner of
/// design/subsystems/backdrop-effects-animation.md §2.3 (two-pass PushLayer{Acrylic} schedule) with the
/// gpu-renderer.md §7.1 LayerPool, inlined HLSL-side in this leaf. The scene renders into an engine-owned canvas RT;
/// at each <c>PushLayer</c>:
///   pass A  SNAPSHOT the canvas region beneath the layer rect (inflated by the full chain support) into a pooled
///           offscreen RT at FULL region resolution (down = 1; the dual-Kawase chain does its own halving — Wave B),
///   pass B  a dual-Kawase DOWNSAMPLE chain (ARM SIGGRAPH 2015 dual filter): AcrylicKawaseMath.SelectChain maps
///           sigma = BlurSigma*scale (AcrylicBrush.h:64 sc_blurRadius = 30 DIP) into (iterations, per-pass offset);
///           each pass halves resolution with a 5-tap bilinear kernel (center 1/2 + four half-texel corners 1/8, /8)
///           building the 1/2, 1/4, 1/8, 1/16 RT pyramid,
///   pass C  the matching UPSAMPLE chain (8-tap tent, /12) folds the pyramid back to full resolution; radius grows with
///           the iteration count, the offset interpolates smoothly between the coarse 2x steps (KWin D9848 model),
///   pass D  composite the WinUI AcrylicBrush recipe into the canvas clipped to the rounded layer rect
///           (blurred backdrop SourceOver opaque fallback → luminosity blend → tint/color blend → 2% noise —
///           AcrylicBrush.cpp:500-548), then the layer's content draws on top.
/// At frame end the canvas is blitted to the back buffer.
///
/// LayerPool: RTs pooled by power-of-two size bucket and reused across layers AND frames — steady state acquires from
/// the free list (zero resource creation per frame). Reusing an RT on the same DIRECT queue needs no fence (execution
/// is queue-ordered and barriers carry the state), but DESTRUCTION is fence-gated: evicted/idle-trimmed entries are
/// retired and released only once the frame fence passes their last use (the ImageTextureStore deferred-reclaim
/// convention). Shader-visible SRV descriptors are parity-banked per frame so recreating a slot never rewrites a
/// descriptor an in-flight frame still references. All RTs + ComPtrs are owned here on the render thread (per the
/// threading-render-seam contract); device-lost ⇒ the whole device (and this compositor) is torn down and rebuilt.
/// WARP-safe: ps_5_1, inline fixed-tap Kawase kernels (no dynamic indexing), no UAVs, no typed-load requirements.
///
/// needs-pixels: the recipe values + region/bucket/kernel math are headless-checked (VerticalSlice 64m/64n via
/// HeadlessGpuDevice.LastLayers + AcrylicBackdropMath); the composited GPU pixels themselves are verified manually
/// (--shot) — there is no headless framebuffer for the D3D12 leaf.
/// </summary>
internal sealed unsafe class AcrylicCompositor : IDisposable
{
    private const int MaxPool = 12;           // pooled RT slots. A dual-Kawase chain leases iterations+1 (≤5) pyramid
                                              // levels at once; plus retained per-layer cache RTs → 12 gives headroom.
    private const int TrimIdleFrames = 600;   // free entries idle this long (~10 s) are retired (fence-gated release)

    private ID3D12Device* _device;
    private uint _w, _h;   // canvas size (physical px)

    private ID3D12Resource* _canvas; private D3D12_RESOURCE_STATES _canvasState;

    // ── LayerPool (gpu-renderer.md §7.1) ────────────────────────────────────────────────────────────────────────────
    private struct PoolEntry
    {
        public ID3D12Resource* Res;            // null = empty slot
        public D3D12_RESOURCE_STATES State;    // tracked in record order (single DIRECT queue serializes execution)
        public int W, H;                       // bucket dims (power-of-two, AcrylicBackdropMath.BucketDim)
        public ulong LastUseFence;             // frame fence value covering the entry's most recent GPU use
        public int IdleFrames;                 // consecutive layered frames without an acquire (trim heuristic)
        public bool InUse;                     // held by an in-progress BlurAndComposite this frame
        // Retained-backdrop cache (design §2.3): when PinLayer != 0 this entry holds a layer's blurred snapshot RETAINED
        // across frames (not released after the composite); Stamp is the geometry it was blurred for, so a frame at rest
        // reuses it (passes A/B/C skipped) until the geometry changes or the damage region touches its snapshot region.
        public ulong PinLayer;
        public AcrylicBackdropMath.BackdropStamp Stamp;
    }
    private readonly PoolEntry[] _pool = new PoolEntry[MaxPool];

    private struct Retired { public ID3D12Resource* Res; public ulong Fence; }
    private readonly List<Retired> _retired = new();   // fence-gated deferred release (eviction/trim/resize)

    private ID3D12DescriptorHeap* _rtvHeap;   // 1 + MaxPool RTVs: slot 0 = canvas, 1+i = pool entry i
    private ID3D12DescriptorHeap* _srvHeap;   // 1 + 2·MaxPool shader-visible SRVs: slot 0 = canvas; pool SRVs are
                                              // parity-banked (1 + parity·MaxPool + i) — frame N never rewrites a
                                              // descriptor the in-flight frame N−1 references
    private uint _rtvInc, _srvInc;
    private int _parity;                      // this frame's SRV bank (frameIndex & 1), set by BeginCanvas
    private readonly float[] _scratch4 = new float[4];
    private readonly float[] _scratchK = new float[8];      // dual-Kawase pass consts: p0(srcTexel.xy, offset, 0) + p1(usedFrac.xy, maxUv.xy)
    private readonly float[] _scratch28 = new float[28];

    private ID3D12RootSignature* _copyRoot;   // copy/Kawase: root consts + 1 SRV table + static linear-clamp sampler
    private ID3D12PipelineState* _copyPso, _kdownPso, _kupPso;
    private ID3D12RootSignature* _compRoot;   // composite: 28 root consts + 1 SRV table + static sampler
    private ID3D12PipelineState* _compPso;

    /// <summary>Acrylic layers composited this frame (diagnostics).</summary>
    public int LayersThisFrame { get; private set; }

    /// <summary>Of <see cref="LayersThisFrame"/>, how many REUSED a retained blurred backdrop (cache hit — passes A/B/C
    /// skipped) instead of re-blurring. A stationary acrylic surface being scrolled should read all-hits (diagnostics).</summary>
    public int CacheHitsThisFrame { get; private set; }

    /// <summary>Live pooled RTs (diagnostics; a re-blur holds iterations+1 pyramid levels, ≤5, plus retained cache RTs).</summary>
    public int PooledRtCount
    {
        get { int n = 0; for (int i = 0; i < MaxPool; i++) if (_pool[i].Res != null) n++; return n; }
    }

    // Fullscreen-triangle copy/downsample. srcOffScale maps the target viewport's [0,1] uv onto the source sub-rect
    // (snapshot pass: the layer's backdrop region within the canvas; blit pass: offset 0, scale 1 = whole canvas).
    private const string CopyHlsl = """
cbuffer C : register(b0) { float4 srcOffScale; };   // xy = source uv offset, zw = source uv scale
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 CopyPS(V i) : SV_Target { return gSrc.Sample(gSamp, srcOffScale.xy + i.uv * srcOffScale.zw); }   // preserve premultiplied alpha → transparent pixels reach the backbuffer so DWM Mica composites
""";

    // Dual-Kawase downsample/upsample chain (ARM SIGGRAPH 2015 dual filter — the LIVE-BACKDROP blur, Wave B). Both passes
    // take one cbuffer: p0.xy = the SOURCE texel size (1/bucketW, 1/bucketH), p0.z = the per-pass offset (source texels);
    // p1.xy = the source used-uv fraction (usedW/bucketW), p1.zw = the max sample uv (usedFrac − half texel). All tap
    // offsets are computed INLINE from p0 (half-texel × offset) — no arrays, so no dynamic float4 component indexing
    // (fxc X3504, the lesson from the separable kernel). Every sample is clamped to the used sub-rect so a tap never
    // reads stale texels outside the level's valid region in a (possibly larger) pooled bucket RT. Premultiplied alpha
    // is preserved through the chain; the opaque-fallback SourceOver resolves at composite (pass D), never here.
    private const string KawaseHlsl = """
cbuffer C : register(b0) { float4 p0; float4 p1; };
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 S(float2 uv, float2 lo, float2 hi) { return gSrc.Sample(gSamp, clamp(uv, lo, hi)); }
// 5-tap downsample: center (weight 4) + four half-texel diagonal corners (weight 1), sum/8 → center 1/2, corners 1/8.
float4 KawaseDownPS(V i) : SV_Target
{
    float2 lo = p0.xy * 0.5;
    float2 uv = i.uv * p1.xy;              // dst uv (0..1 over the used dst viewport) → source used sub-rect
    float2 h  = p0.xy * 0.5 * p0.z;        // half source texel × offset
    float4 s = S(uv, lo, p1.zw) * 4.0;
    s += S(uv + float2( h.x,  h.y), lo, p1.zw);
    s += S(uv + float2(-h.x, -h.y), lo, p1.zw);
    s += S(uv + float2( h.x, -h.y), lo, p1.zw);
    s += S(uv + float2(-h.x,  h.y), lo, p1.zw);
    return s / 8.0;
}
// 8-tap upsample tent: four axis taps at 2× distance (weight 1) + four diagonal taps at 1× (weight 2), sum/12.
float4 KawaseUpPS(V i) : SV_Target
{
    float2 lo = p0.xy * 0.5;
    float2 uv = i.uv * p1.xy;
    float2 h  = p0.xy * 0.5 * p0.z;
    float4 s = S(uv + float2(-h.x * 2.0, 0.0), lo, p1.zw);
    s += S(uv + float2(-h.x,  h.y), lo, p1.zw) * 2.0;
    s += S(uv + float2(0.0,  h.y * 2.0), lo, p1.zw);
    s += S(uv + float2( h.x,  h.y), lo, p1.zw) * 2.0;
    s += S(uv + float2( h.x * 2.0, 0.0), lo, p1.zw);
    s += S(uv + float2( h.x, -h.y), lo, p1.zw) * 2.0;
    s += S(uv + float2(0.0, -h.y * 2.0), lo, p1.zw);
    s += S(uv + float2(-h.x, -h.y), lo, p1.zw) * 2.0;
    return s / 12.0;
}
""";

    private const string CompHlsl = """
cbuffer C : register(b0) { float4 rect; float4 vpro; float4 rsuf; float4 tint; float4 fallback; float4 prm; float4 blurTexel; };
// rect = layer rect (logical DIP); vpro = logical viewport W,H + snapshot-region origin (phys px);
// rsuf = snapshot-region size (phys px) + blurred-RT used-uv fraction; prm = radius,tintOp,lumOp,noiseOp;
// blurTexel.xy = blurred-RT texel size (for the half-texel edge clamp).
Texture2D gBlur : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 local : TEXCOORD0; float2 half : TEXCOORD1; };
V VSMain(uint id : SV_VertexID)
{
    float2 corner = float2(id & 1, (id >> 1) & 1);
    float2 world = rect.xy + corner * rect.zw;                    // DeviceRect is already in logical device space (identity)
    V o;
    o.pos = float4(world.x / vpro.x * 2.0 - 1.0, 1.0 - world.y / vpro.y * 2.0, 0, 1);
    o.local = corner * rect.zw - rect.zw * 0.5;
    o.half = rect.zw * 0.5;
    return o;
}
float Lum(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
float3 ClipColor(float3 c)
{
    float l = Lum(c);
    float n = min(c.r, min(c.g, c.b));
    float x = max(c.r, max(c.g, c.b));
    if (n < 0.0) c = l + (c - l) * l / max(l - n, 1e-5);
    if (x > 1.0) c = l + (c - l) * (1.0 - l) / max(x - l, 1e-5);
    return saturate(c);
}
float3 SetLum(float3 c, float l) { return ClipColor(c + (l - Lum(c))); }
float4 PSMain(V i) : SV_Target
{
    float r = min(prm.x, min(i.half.x, i.half.y));
    float2 q = abs(i.local) - (i.half - r);
    float d = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
    float fw = max(fwidth(d), 1e-4);
    float cov = saturate(0.5 - d / fw);
    // Top feather (blurTexel.z = fraction of the layer height, 0 = off): ramp coverage 0→1 from the top edge downward so
    // the frosted band dissolves continuously into the crisp backdrop (Apple editorial card) instead of a hard blur line.
    if (blurTexel.z > 0.0)
    {
        float distFromTop = (i.local.y + i.half.y) / max(2.0 * i.half.y, 1e-4);   // 0 at top edge → 1 at bottom
        cov *= smoothstep(0.0, blurTexel.z, distFromTop);
    }
    // SV_Position is physical px → uv into the blurred snapshot: region-relative position × used fraction of the
    // bucket RT, clamped half a texel inside the used sub-rect (pooled RTs can be larger than the snapshot).
    float2 uv = (i.pos.xy - vpro.zw) / rsuf.xy * rsuf.zw;
    float4 src = gBlur.Sample(gSamp, clamp(uv, blurTexel.xy * 0.5, rsuf.zw - blurTexel.xy * 0.5));   // premultiplied blurred backdrop

    // WinUI resolves the (possibly transparent) backdrop over the OPAQUE FallbackColor before blurring
    // (AcrylicBrush.cpp:500-517 "Blend the backdrop on top of the opaque FallbackColor"). We preserve alpha through
    // the blur passes and complete that SourceOver resolve here; transparent/Mica regions therefore acrylic against
    // fallback, not transparent black.
    float3 B = src.rgb + fallback.rgb * saturate(1.0 - src.a);

    // WinUI AcrylicBrush.cpp:446-452 + CombineNoiseWithTintEffect_Luminosity: luminosity blend first (keeps backdrop
    // hue/saturation, takes lightness from the luminosity color at LuminosityOpacity), then the tint/color blend
    // (tint hue/saturation, preserving that luminosity result) at TintOpacity.
    float3 lumBlend = lerp(B, SetLum(B, Lum(tint.rgb)), prm.z);
    float3 colorBlend = SetLum(tint.rgb, Lum(lumBlend));
    float3 res = lerp(lumBlend, colorBlend, prm.y);
    float n = frac(sin(dot(i.pos.xy, float2(12.9898, 78.233))) * 43758.5453);
    res += (n - 0.5) * prm.w;                                    // 2% noise (AcrylicBrush.h:65 sc_noiseOpacity = 0.02)
    return float4(res * cov, cov);                              // premultiplied
}
""";

    public void Init(ID3D12Device* device)
    {
        _device = device;
        BuildHeaps();
        BuildCopyPipeline();
        BuildCompositePipeline();
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    private void BuildHeaps()
    {
        D3D12_DESCRIPTOR_HEAP_DESC rh = default;
        rh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rh.NumDescriptors = 1 + MaxPool;
        ID3D12DescriptorHeap* rhp; Check(_device->CreateDescriptorHeap(&rh, __uuidof<ID3D12DescriptorHeap>(), (void**)&rhp), "Acrylic.RtvHeap");
        _rtvHeap = rhp;
        _rtvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        D3D12MemoryDiagnostics.Track(_rtvHeap, "Acrylic.RtvHeap", (ulong)rh.NumDescriptors * _rtvInc);

        D3D12_DESCRIPTOR_HEAP_DESC sh = default;
        sh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        sh.NumDescriptors = 1 + 2 * MaxPool;   // canvas + two parity banks of pool SRVs (see _srvHeap comment)
        sh.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* shp; Check(_device->CreateDescriptorHeap(&sh, __uuidof<ID3D12DescriptorHeap>(), (void**)&shp), "Acrylic.SrvHeap");
        _srvHeap = shp;
        _srvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12MemoryDiagnostics.Track(_srvHeap, "Acrylic.SrvHeap", (ulong)sh.NumDescriptors * _srvInc);
    }

    private ID3D12RootSignature* SampleRootSig(int numConstants)
    {
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        range.BaseShaderRegister = 0;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;   // D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = (uint)numConstants;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
        p[1].Anonymous.DescriptorTable.pDescriptorRanges = &range;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC samp = default;
        samp.Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        samp.AddressU = samp.AddressV = samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.ShaderRegister = 0;
        samp.ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
        samp.MaxLOD = float.MaxValue;

        D3D12_ROOT_SIGNATURE_DESC desc = default;
        desc.NumParameters = 2;
        desc.pParameters = p;
        desc.NumStaticSamplers = 1;
        desc.pStaticSamplers = &samp;
        desc.Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "Acrylic.SerializeRootSig");
        ID3D12RootSignature* rs;
        Check(_device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), "Acrylic.CreateRootSig");
        sig->Release();
        if (err != null) err->Release();
        return rs;
    }

    private static ID3DBlob* Compile(string hlsl, string entry, string target)
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
                throw new InvalidOperationException($"acrylic shader {entry} failed: {msg}");
            }
        }
        return code;
    }

    private ID3D12PipelineState* MakePso(ID3D12RootSignature* root, ID3DBlob* vs, ID3DBlob* ps, bool blend)
    {
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = root;
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
        pd.BlendState.RenderTarget[0].BlendEnable = blend ? BOOL.TRUE : BOOL.FALSE;
        pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        ID3D12PipelineState* pso;
        Check(_device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Acrylic.Pso");
        return pso;
    }

    private void BuildCopyPipeline()
    {
        _copyRoot = SampleRootSig(8);   // copy uses 4 floats, each Kawase pass uses 8 (p0 + p1) — shared sig sized for the larger
        ID3DBlob* vs = Compile(CopyHlsl, "VSMain", "vs_5_1");
        ID3DBlob* copyPs = Compile(CopyHlsl, "CopyPS", "ps_5_1");
        ID3DBlob* kVs = Compile(KawaseHlsl, "VSMain", "vs_5_1");
        ID3DBlob* kDown = Compile(KawaseHlsl, "KawaseDownPS", "ps_5_1");
        ID3DBlob* kUp = Compile(KawaseHlsl, "KawaseUpPS", "ps_5_1");
        _copyPso = MakePso(_copyRoot, vs, copyPs, blend: false);
        _kdownPso = MakePso(_copyRoot, kVs, kDown, blend: false);
        _kupPso = MakePso(_copyRoot, kVs, kUp, blend: false);
        vs->Release(); copyPs->Release(); kVs->Release(); kDown->Release(); kUp->Release();
    }

    private void BuildCompositePipeline()
    {
        _compRoot = SampleRootSig(28);
        ID3DBlob* vs = Compile(CompHlsl, "VSMain", "vs_5_1");
        ID3DBlob* ps = Compile(CompHlsl, "PSMain", "ps_5_1");
        _compPso = MakePso(_compRoot, vs, ps, blend: true);
        vs->Release(); ps->Release();
    }

    /// <summary>(Re)create the full-window canvas RT. Size changes only follow a swapchain Resize, which fenced the
    /// GPU idle first — so releasing the old canvas and rewriting descriptor slot 0 here is in-flight-safe.</summary>
    public void EnsureSize(uint w, uint h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == _w && h == _h && _canvas != null) return;
        D3D12MemoryDiagnostics.Resize("Acrylic.Targets", w, h);
        if (_canvas != null) { D3D12MemoryDiagnostics.Release(_canvas, "Acrylic.Canvas"); _canvas->Release(); _canvas = null; }
        _w = w; _h = h;

        _canvas = CreateTarget(_w, _h, "Acrylic.Canvas", optimizedClear: true);
        _canvasState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        _device->CreateRenderTargetView(_canvas, null, Rtv(0));
        CreateSrv(_canvas, SrvCpu(0));
    }

    private ID3D12Resource* CreateTarget(uint w, uint h, string name, bool optimizedClear)
    {
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = w; rd.Height = h; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        rd.Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        D3D12_CLEAR_VALUE cv = default; cv.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        ID3D12Resource* res;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, optimizedClear ? &cv : null,
            __uuidof<ID3D12Resource>(), (void**)&res), "Acrylic.CreateTarget");
        D3D12MemoryDiagnostics.Track(res, $"{name} {w}x{h}", (ulong)w * h * 4UL);
        return res;
    }

    private void CreateSrv(ID3D12Resource* tex, D3D12_CPU_DESCRIPTOR_HANDLE h)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
        sd.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = 0x1688;   // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING (identity RGBA)
        sd.Anonymous.Texture2D.MipLevels = 1;
        _device->CreateShaderResourceView(tex, &sd, h);
    }

    private void Barrier(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, ref D3D12_RESOURCE_STATES state, D3D12_RESOURCE_STATES to)
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

    // Descriptor slot maps — RTV: 0 = canvas, 1+i = pool entry i. SRV: 0 = canvas, 1 + parity·MaxPool + i = pool
    // entry i in THIS frame's bank (rewritten on every acquire; the other bank belongs to the in-flight frame).
    private D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int slot) { var h = _rtvHeap->GetCPUDescriptorHandleForHeapStart(); h.ptr += (nuint)slot * _rtvInc; return h; }
    private D3D12_CPU_DESCRIPTOR_HANDLE SrvCpu(int slot) { var h = _srvHeap->GetCPUDescriptorHandleForHeapStart(); h.ptr += (nuint)slot * _srvInc; return h; }
    private D3D12_GPU_DESCRIPTOR_HANDLE SrvGpu(int slot) { var h = _srvHeap->GetGPUDescriptorHandleForHeapStart(); h.ptr += (ulong)slot * _srvInc; return h; }
    private int PoolSrvSlot(int i) => 1 + _parity * MaxPool + i;

    private void SetViewport(ID3D12GraphicsCommandList* cmd, uint w, uint h)
    {
        D3D12_VIEWPORT vp = new() { Width = w, Height = h, MaxDepth = 1 };
        RECT sc = new() { right = (int)w, bottom = (int)h };
        cmd->RSSetViewports(1, &vp);
        cmd->RSSetScissorRects(1, &sc);
    }

    // ── LayerPool acquire/release/trim ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Release retired resources whose last GPU use the fence has passed (deferred-delete convention).</summary>
    private void DrainRetired(ulong completedFence)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Fence > completedFence) continue;
            D3D12MemoryDiagnostics.Release(_retired[i].Res, "Acrylic.Pool");
            _retired[i].Res->Release();
            _retired.RemoveAt(i);
        }
    }

    /// <summary>Age free entries; retire (fence-gated) any idle past the trim window so a closed flyout's RTs are
    /// eventually returned to the OS instead of pinning VRAM forever.</summary>
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

    /// <summary>Idle upkeep for frames with no acrylic layers (the canvas path isn't taken): age + trim the pool.</summary>
    public void TickIdle(ulong completedFence)
    {
        if (_retired.Count > 0 || PooledRtCount > 0) TickPool(completedFence);
        LayersThisFrame = 0; CacheHitsThisFrame = 0;
    }

    /// <summary>Direct-to-back-buffer frame setup: set THIS frame's SRV parity bank + run pool upkeep, WITHOUT binding or
    /// clearing the full-window canvas (the directBB path renders the scene straight to the back buffer). The canvas is
    /// retained only as the region-copy scratch for <see cref="SnapshotTargetRegion"/>. Replaces <see cref="TickIdle"/>
    /// on the directBB path so the retained-backdrop cache's parity-banked SRVs stay correct when an acrylic is present.</summary>
    public void BeginFrameDirect(ulong completedFence, int parity)
    {
        _parity = parity & 1;
        if (_retired.Count > 0 || PooledRtCount > 0) TickPool(completedFence);
        LayersThisFrame = 0; CacheHitsThisFrame = 0;
    }

    /// <summary>Lease a pooled RT at least (w,h) texels (bucket-quantized). Steady state hits the free-list "fits"
    /// path — CreateCommittedResource only runs on cold growth or a bucket-size change (eviction is fence-deferred).</summary>
    private int Acquire(int w, int h, ulong frameFence)
    {
        int bw = AcrylicBackdropMath.BucketDim(w), bh = AcrylicBackdropMath.BucketDim(h);

        // 1) smallest free entry that fits (same-queue reuse needs no fence — execution order serializes RT access).
        int best = -1;
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || e.InUse || e.PinLayer != 0 || e.W < bw || e.H < bh) continue;   // never reuse a retained cache RT as scratch
            if (best < 0 || (long)e.W * e.H < (long)_pool[best].W * _pool[best].H) best = i;
        }
        if (best < 0)
        {
            // 2) empty slot → cold growth; 3) no slot → evict the LRU FREE entry (resource release deferred behind
            // its fence; its SRV descriptor is parity-banked so the rewrite can't race the in-flight frame).
            int slot = -1;
            for (int i = 0; i < MaxPool; i++) if (_pool[i].Res == null) { slot = i; break; }
            if (slot < 0)
            {
                for (int i = 0; i < MaxPool; i++)
                {
                    if (_pool[i].InUse) continue;   // a chain leases ≤ iterations+1 (≤5) of 12 slots → a free victim always exists
                    if (slot < 0 || _pool[i].LastUseFence < _pool[slot].LastUseFence) slot = i;
                }
                if (slot < 0) throw new InvalidOperationException("acrylic LayerPool exhausted (more concurrent leases than slots)");
                _retired.Add(new Retired { Res = _pool[slot].Res, Fence = _pool[slot].LastUseFence });
                _pool[slot] = default;
            }
            _pool[slot].Res = CreateTarget((uint)bw, (uint)bh, $"Acrylic.Pool[{slot}]", optimizedClear: false);
            _pool[slot].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _pool[slot].W = bw; _pool[slot].H = bh;
            // RTV descriptors are read at RECORD time (CPU), so rewriting the slot's RTV here cannot race the
            // in-flight frame — its command list already consumed the old descriptor contents when it was recorded.
            _device->CreateRenderTargetView(_pool[slot].Res, null, Rtv(1 + slot));
            best = slot;
        }

        ref var entry = ref _pool[best];
        entry.InUse = true;
        entry.IdleFrames = 0;
        entry.LastUseFence = frameFence;
        CreateSrv(entry.Res, SrvCpu(PoolSrvSlot(best)));   // refresh THIS frame's bank (cheap CPU descriptor write)
        return best;
    }

    private void Release(int idx) => _pool[idx].InUse = false;

    /// <summary>Find the pooled slot holding <paramref name="layerId"/>'s retained blurred backdrop, or -1.</summary>
    private int FindPinned(ulong layerId)
    {
        for (int i = 0; i < MaxPool; i++) if (_pool[i].Res != null && _pool[i].PinLayer == layerId) return i;
        return -1;
    }

    /// <summary>Lease the RETAINED cache slot for <paramref name="layerId"/> at ≥ (w,h): reuse its existing RT if it
    /// still fits, else (re)allocate. Unlike <see cref="Acquire"/> the slot is NOT released after the composite — it
    /// survives across frames so a stationary acrylic surface skips passes A/B/C. Its SRV is refreshed in this frame's
    /// parity bank; eviction of any displaced RT is fence-gated (deferred-reclaim), same as the transient pool.</summary>
    private int AcquirePinned(ulong layerId, int w, int h, ulong frameFence)
    {
        int bw = AcrylicBackdropMath.BucketDim(w), bh = AcrylicBackdropMath.BucketDim(h);
        int slot = FindPinned(layerId);
        if (slot >= 0 && (_pool[slot].W < bw || _pool[slot].H < bh))   // existing cache RT too small → retire + reallocate
        {
            _retired.Add(new Retired { Res = _pool[slot].Res, Fence = _pool[slot].LastUseFence });
            _pool[slot] = default;
            slot = -1;
        }
        if (slot < 0)
        {
            for (int i = 0; i < MaxPool; i++) if (_pool[i].Res == null) { slot = i; break; }   // empty slot → cold growth
            if (slot < 0)
            {
                // No empty slot → evict the LRU NON-leased entry, preferring an unpinned (transient) victim over a
                // pinned cache entry (a displaced cache entry merely re-blurs next time it is needed).
                for (int i = 0; i < MaxPool; i++)
                {
                    if (_pool[i].InUse) continue;
                    if (slot < 0) { slot = i; continue; }
                    bool iUn = _pool[i].PinLayer == 0, sUn = _pool[slot].PinLayer == 0;
                    if (iUn != sUn) { if (iUn) slot = i; continue; }
                    if (_pool[i].LastUseFence < _pool[slot].LastUseFence) slot = i;
                }
                if (slot < 0) throw new InvalidOperationException("acrylic LayerPool exhausted (more concurrent leases than slots)");
                _retired.Add(new Retired { Res = _pool[slot].Res, Fence = _pool[slot].LastUseFence });
                _pool[slot] = default;
            }
            _pool[slot].Res = CreateTarget((uint)bw, (uint)bh, $"Acrylic.Cache[{slot}]", optimizedClear: false);
            _pool[slot].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _pool[slot].W = bw; _pool[slot].H = bh;
            _device->CreateRenderTargetView(_pool[slot].Res, null, Rtv(1 + slot));
        }
        ref var e = ref _pool[slot];
        e.PinLayer = layerId;
        e.InUse = true;
        e.IdleFrames = 0;
        e.LastUseFence = frameFence;
        CreateSrv(e.Res, SrvCpu(PoolSrvSlot(slot)));   // refresh THIS frame's SRV bank
        return slot;
    }

    /// <summary>Pass D: composite the WinUI acrylic recipe into the canvas at the rounded layer rect, sampling the
    /// blurred snapshot in pool entry <paramref name="ia"/> — shared by the re-blur and the cache-hit paths.</summary>
    private void Composite(ID3D12GraphicsCommandList* cmd, in PushLayerCmd L, float lw, float lh, int rx, int ry, int rw, int rh, int ia, int dw, int dh,
        D3D12_CPU_DESCRIPTOR_HANDLE targetRtv, bool targetIsCanvas, RECT compositeScissor)
    {
        float aw = _pool[ia].W, ah = _pool[ia].H;
        Barrier(cmd, _pool[ia].Res, ref _pool[ia].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        if (targetIsCanvas) Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        cmd->OMSetRenderTargets(1, &targetRtv, BOOL.FALSE, null); SetViewport(cmd, _w, _h);
        // Clip the frosted composite to the active clip stack (scroll viewport / ClipToBounds ancestors). SetViewport reset
        // the scissor to the whole window; without this override the acrylic band alone paints outside its scrolling
        // container, bleeding over pinned chrome (sticky nav / player bar) — every other layer kind already honors this.
        RECT sc = compositeScissor; cmd->RSSetScissorRects(1, &sc);
        cmd->SetGraphicsRootSignature(_compRoot);
        cmd->SetPipelineState(_compPso);
        Span<float> cc = _scratch28;
        cc[0] = L.DeviceRect.X; cc[1] = L.DeviceRect.Y; cc[2] = L.DeviceRect.W; cc[3] = L.DeviceRect.H;
        cc[4] = lw; cc[5] = lh; cc[6] = rx; cc[7] = ry;
        cc[8] = rw; cc[9] = rh; cc[10] = dw / aw; cc[11] = dh / ah;
        cc[12] = L.Tint.R; cc[13] = L.Tint.G; cc[14] = L.Tint.B; cc[15] = L.Tint.A;
        cc[16] = L.Fallback.R; cc[17] = L.Fallback.G; cc[18] = L.Fallback.B; cc[19] = L.Fallback.A;
        cc[20] = L.Radii.TopLeft; cc[21] = L.TintOpacity; cc[22] = L.LuminosityOpacity; cc[23] = L.NoiseOpacity;
        cc[24] = 1f / aw; cc[25] = 1f / ah; cc[26] = Math.Clamp(L.FeatherFrac, 0f, 1f); cc[27] = 0f;
        fixed (float* c = cc) cmd->SetGraphicsRoot32BitConstants(0, 28, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(ia)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        cmd->DrawInstanced(4, 1, 0, 0);
    }

    // ── frame passes ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bind the canvas as the render target, clear it, set the full viewport, and run pool upkeep.
    /// Call once before drawing the scene. <paramref name="parity"/> = frameIndex &amp; 1 selects this frame's SRV bank.</summary>
    public void BeginCanvas(ID3D12GraphicsCommandList* cmd, in ColorF clear, ulong completedFence, int parity)
    {
        _parity = parity & 1;
        LayersThisFrame = 0; CacheHitsThisFrame = 0;
        TickPool(completedFence);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = Rtv(0);
        cmd->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        _scratch4[0] = clear.R; _scratch4[1] = clear.G; _scratch4[2] = clear.B; _scratch4[3] = clear.A;
        fixed (float* c = _scratch4)
            cmd->ClearRenderTargetView(rtv, c, 0, null);
        SetViewport(cmd, _w, _h);
    }

    private void SetHeap(ID3D12GraphicsCommandList* cmd) { ID3D12DescriptorHeap* h = _srvHeap; cmd->SetDescriptorHeaps(1, &h); }

    /// <summary>Re-bind the canvas RTV + full viewport (after the blur passes switched targets), to continue scene drawing.</summary>
    public void BindCanvas(ID3D12GraphicsCommandList* cmd)
    {
        var rtv = Rtv(0);
        cmd->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        SetViewport(cmd, _w, _h);
    }

    // Fill the 8-float dual-Kawase pass constant block from the SOURCE level's bucket + used dims and the per-pass offset.
    // p0 = (srcTexel.x, srcTexel.y, offset, 0); p1 = (usedFrac.x, usedFrac.y, maxU, maxV) where maxUv = usedFrac − half
    // texel. The shader maps its 0..1 dst uv onto usedFrac and clamps every tap to [half-texel, maxUv] (the used sub-rect
    // of a possibly larger pooled bucket RT). Same layout for both the down and up passes; only the source dims differ.
    private void WriteKawaseConsts(int srcBucketW, int srcBucketH, int srcUsedW, int srcUsedH, float offset)
    {
        float tx = 1f / srcBucketW, ty = 1f / srcBucketH;
        float ux = (float)srcUsedW / srcBucketW, uy = (float)srcUsedH / srcBucketH;
        _scratchK[0] = tx; _scratchK[1] = ty; _scratchK[2] = offset; _scratchK[3] = 0f;
        _scratchK[4] = ux; _scratchK[5] = uy; _scratchK[6] = ux - tx * 0.5f; _scratchK[7] = uy - ty * 0.5f;
    }

    private void FullScreen(ID3D12GraphicsCommandList* cmd, ID3D12PipelineState* pso, int srvSlot, ReadOnlySpan<float> consts)
    {
        cmd->SetGraphicsRootSignature(_copyRoot);
        cmd->SetPipelineState(pso);
        fixed (float* c = consts) cmd->SetGraphicsRoot32BitConstants(0, (uint)consts.Length, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(srvSlot));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        cmd->DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>The PushLayer{Acrylic} schedule WITH the retained-backdrop cache (design §2.3): on a cache HIT (the
    /// layer is stationary and nothing behind it moved this frame) skip the snapshot+blur and composite the retained
    /// snapshot; else snapshot the canvas region, run the dual-Kawase down/up chain, and composite — RETAINING the
    /// result for a keyed (<see cref="PushLayerCmd.LayerId"/> != 0) layer. Leaves the canvas bound for continued drawing.
    /// The damage rect (physical px) is this frame's union of moved-node bounds (SceneRecorder) — empty ⇒ reuse.</summary>
    public void BlurAndComposite(ID3D12GraphicsCommandList* cmd, in PushLayerCmd L, float lw, float lh, float scale, ulong frameFence,
        float dmgX, float dmgY, float dmgW, float dmgH, RECT compositeScissor, ulong backdropSourceId,
        ID3D12Resource* backdropTarget = null, D3D12_CPU_DESCRIPTOR_HANDLE targetRtv = default)
    {
        if (scale <= 0f) scale = 1f;
        // directBB: when D3D12Device renders straight to the back buffer, snapshot ONLY the small region under this acrylic
        // from the back buffer (into the canvas — the existing region-copy scratch) and composite back onto the back
        // buffer, so a frosted in-app pill/bar no longer forces the whole-window scene→canvas render + blit.
        bool external = backdropTarget != null;
        var compTarget = external ? targetRtv : Rtv(0);
        // Dual-Kawase chain (AcrylicKawaseMath, Wave B): map σ = BlurSigma·scale → (iterations, per-pass offset), snapshot
        // at SnapshotDown (1 = full region; the chain does its own halving — see the down=2 pivot note on the constant),
        // and inflate the region by the chain's blur support (kernelRadiusTexels = pad ⇒ pad = radius·down).
        AcrylicKawaseMath.SelectChain(L.BlurSigma, scale, out int iters, out float offset);
        int pad = AcrylicKawaseMath.PadPx(iters, offset);
        AcrylicBackdropMath.SnapshotRegion(L.DeviceRect, scale, AcrylicKawaseMath.SnapshotDown, pad, (int)_w, (int)_h, out int rx, out int ry, out int rw, out int rh);
        int dw = rw, dh = rh;   // level 0 = the full-resolution snapshot

        SetHeap(cmd);

        // ── retained-backdrop cache: a stationary acrylic surface reuses its blurred snapshot across frames — re-blur
        // ONLY when its geometry changed OR this frame's damage region touches its snapshot region. So scrolling INSIDE
        // a popup (the backdrop behind it is static) composites the cached blur with passes A/B/C skipped. ──
        var nowStamp = AcrylicBackdropMath.Stamp(L.DeviceRect, L.BlurSigma, scale, (int)_w, (int)_h,
            backdropSourceId, compositeScissor.left, compositeScissor.top, compositeScissor.right, compositeScissor.bottom);
        // Damage-test against the TIGHT (un-inflated) rect+margin, NOT the kernel-inflated snapshot region (§2.3/E8): a
        // node animating in the ±KernelRadius·down halo but outside rect+8 no longer forces a re-blur every frame. A MISS
        // still snapshots/blurs the inflated region [rx,ry,rw,rh] below, so edge fidelity is unchanged.
        AcrylicBackdropMath.SnapshotRegionTight(L.DeviceRect, scale, (int)_w, (int)_h, out int tx, out int ty, out int tw, out int th);
        var tightPhys = new RectF(tx, ty, tw, th);
        var damagePhys = new RectF(dmgX, dmgY, dmgW, dmgH);
        if (L.LayerId != 0)
        {
            int pin = FindPinned(L.LayerId);
            if (pin >= 0 && AcrylicBackdropMath.BackdropReusable(_pool[pin].Stamp, nowStamp, tightPhys, damagePhys))
            {
                ref var hit = ref _pool[pin];
                hit.InUse = true; hit.IdleFrames = 0; hit.LastUseFence = frameFence;
                CreateSrv(hit.Res, SrvCpu(PoolSrvSlot(pin)));     // refresh THIS frame's parity bank for the composite sample
                Composite(cmd, in L, lw, lh, rx, ry, rw, rh, pin, dw, dh, compTarget, !external, compositeScissor);
                hit.InUse = false;
                LayersThisFrame++; CacheHitsThisFrame++;
                if (!external) BindCanvas(cmd);
                return;
            }
        }

        // ── (re)blur: pass A snapshot + the dual-Kawase down/up chain. Level 0 is the composite/cache buffer (PINNED for a
        // keyed layer, retained for reuse; transient + released for LayerId == 0); levels 1..iters are transient pyramid
        // RTs (½, ¼, ⅛, 1/16 of the region), leased once and released after the composite. ──
        bool cache = L.LayerId != 0;
        Span<int> lv = stackalloc int[AcrylicKawaseMath.MaxIterations + 1];
        lv[0] = cache ? AcquirePinned(L.LayerId, dw, dh, frameFence) : Acquire(dw, dh, frameFence);
        for (int k = 1; k <= iters; k++)
            lv[k] = Acquire(AcrylicKawaseMath.LevelDim(dw, k), AcrylicKawaseMath.LevelDim(dh, k), frameFence);

        // directBB MISS: copy the back-buffer region into the canvas (same coords) so pass A reads the real backdrop.
        if (external) SnapshotTargetRegion(cmd, backdropTarget, rx, ry, rw, rh, compositeScissor);
        // pass A: snapshot the backdrop region (canvas sub-rect) → level 0 at full resolution (down = 1)
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _pool[lv[0]].Res, ref _pool[lv[0]].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rt = Rtv(1 + lv[0]); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, (uint)dw, (uint)dh);
        _scratch4[0] = (float)rx / _w;
        _scratch4[1] = (float)ry / _h;
        _scratch4[2] = (float)rw / _w;
        _scratch4[3] = (float)rh / _h;
        FullScreen(cmd, _copyPso, 0, _scratch4);

        // pass B: DOWNSAMPLE chain — level k−1 → level k (each halves), 5-tap bilinear kernel, building the RT pyramid.
        for (int k = 1; k <= iters; k++)
        {
            int src = lv[k - 1], dst = lv[k];
            int duw = AcrylicKawaseMath.LevelDim(dw, k), duh = AcrylicKawaseMath.LevelDim(dh, k);
            int suw = AcrylicKawaseMath.LevelDim(dw, k - 1), suh = AcrylicKawaseMath.LevelDim(dh, k - 1);
            Barrier(cmd, _pool[src].Res, ref _pool[src].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            Barrier(cmd, _pool[dst].Res, ref _pool[dst].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
            rt = Rtv(1 + dst); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, (uint)duw, (uint)duh);
            WriteKawaseConsts(_pool[src].W, _pool[src].H, suw, suh, offset);
            FullScreen(cmd, _kdownPso, PoolSrvSlot(src), _scratchK);
        }

        // pass C: UPSAMPLE chain — level k → level k−1 (each doubles), 8-tap tent, folding back to level 0 (the result).
        for (int k = iters; k >= 1; k--)
        {
            int src = lv[k], dst = lv[k - 1];
            int duw = AcrylicKawaseMath.LevelDim(dw, k - 1), duh = AcrylicKawaseMath.LevelDim(dh, k - 1);
            int suw = AcrylicKawaseMath.LevelDim(dw, k), suh = AcrylicKawaseMath.LevelDim(dh, k);
            Barrier(cmd, _pool[src].Res, ref _pool[src].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            Barrier(cmd, _pool[dst].Res, ref _pool[dst].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
            rt = Rtv(1 + dst); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, (uint)duw, (uint)duh);
            WriteKawaseConsts(_pool[src].W, _pool[src].H, suw, suh, offset);
            FullScreen(cmd, _kupPso, PoolSrvSlot(src), _scratchK);
        }

        // pass D: composite the acrylic recipe onto the target — the canvas, or the back buffer on directBB (samples the
        // fully-folded blur in level 0, at dw×dh — down = 1 so the composite's used fraction is unchanged).
        int ia = lv[0];
        Composite(cmd, in L, lw, lh, rx, ry, rw, rh, ia, dw, dh, compTarget, !external, compositeScissor);

        if (cache) { _pool[ia].Stamp = nowStamp; _pool[ia].InUse = false; }   // KEEP level 0 pinned; record what it blurred
        else Release(ia);
        for (int k = 1; k <= iters; k++) Release(lv[k]);                      // pyramid levels → free list
        LayersThisFrame++;

        if (!external) BindCanvas(cmd);
    }

    /// <summary>Blit the finished canvas to the back buffer (already bound as the render target by the caller).</summary>
    public void BlitToBackBuffer(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE backRtv)
    {
        SetHeap(cmd);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        cmd->OMSetRenderTargets(1, &backRtv, BOOL.FALSE, null);
        SetViewport(cmd, _w, _h);
        _scratch4[0] = 0f;
        _scratch4[1] = 0f;
        _scratch4[2] = 1f;
        _scratch4[3] = 1f;
        FullScreen(cmd, _copyPso, 0, _scratch4);
    }

    /// <summary>directBB acrylic: copy the back-buffer region [rx,ry → +rw,+rh] into the canvas at the SAME coords so the
    /// existing snapshot pass reads the real backdrop from the canvas SRV. The back buffer is briefly transitioned to
    /// COPY_SOURCE and back to RENDER_TARGET via RAW (untracked) barriers that NET to no change, so D3D12Device's
    /// explicit-state back-buffer tracking (RENDER_TARGET across the submit, then RT→PRESENT) stays consistent. Canvas and
    /// back buffer are both B8G8R8A8_UNORM with a null-desc RTV, so the copy is bit-exact and the colour space matches.</summary>
    public void SnapshotTargetRegion(ID3D12GraphicsCommandList* cmd, ID3D12Resource* target,
        int rx, int ry, int rw, int rh, RECT sourceClip)
    {
        if (rw <= 0 || rh <= 0 || _canvas == null) return;

        // A nested group's pooled RT is valid only inside its active clip. Clear the padded scratch capture first, then
        // copy the valid intersection so pixels left by an earlier pool lease can never bleed into this blur.
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var canvasRtv = Rtv(0);
        cmd->OMSetRenderTargets(1, &canvasRtv, BOOL.FALSE, null);
        _scratch4[0] = _scratch4[1] = _scratch4[2] = _scratch4[3] = 0f;
        RECT clearRect = new() { left = rx, top = ry, right = rx + rw, bottom = ry + rh };
        fixed (float* clear = _scratch4) cmd->ClearRenderTargetView(canvasRtv, clear, 1, &clearRect);

        int left = Math.Max(rx, sourceClip.left), top = Math.Max(ry, sourceClip.top);
        int right = Math.Min(rx + rw, sourceClip.right), bottom = Math.Min(ry + rh, sourceClip.bottom);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
        if (right > left && bottom > top)
        {
            RawTransition(cmd, target, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
            D3D12_TEXTURE_COPY_LOCATION dst = default; dst.pResource = _canvas; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.Anonymous.SubresourceIndex = 0;
            D3D12_TEXTURE_COPY_LOCATION src = default; src.pResource = target; src.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; src.Anonymous.SubresourceIndex = 0;
            D3D12_BOX box = new() { left = (uint)left, top = (uint)top, front = 0, right = (uint)right, bottom = (uint)bottom, back = 1 };
            cmd->CopyTextureRegion(&dst, (uint)left, (uint)top, 0, &src, &box);
            RawTransition(cmd, target, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        }
        // _canvas left COPY_DEST; the snapshot pass transitions it to PIXEL_SHADER_RESOURCE.
    }

    private static void RawTransition(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = before;
        b.Anonymous.Transition.StateAfter = after;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        cmd->ResourceBarrier(1, &b);
    }

    public void Dispose()
    {
        // The device fenced the GPU idle before disposing (D3D12Device.Dispose → WaitForGpu) — immediate release is safe.
        if (_canvas != null) { D3D12MemoryDiagnostics.Release(_canvas, "Acrylic.Canvas"); _canvas->Release(); _canvas = null; }
        for (int i = 0; i < MaxPool; i++)
        {
            if (_pool[i].Res == null) continue;
            D3D12MemoryDiagnostics.Release(_pool[i].Res, "Acrylic.Pool");
            _pool[i].Res->Release();
            _pool[i] = default;
        }
        for (int i = 0; i < _retired.Count; i++)
        {
            D3D12MemoryDiagnostics.Release(_retired[i].Res, "Acrylic.Pool");
            _retired[i].Res->Release();
        }
        _retired.Clear();
        if (_rtvHeap != null) { D3D12MemoryDiagnostics.Release(_rtvHeap, "Acrylic.RtvHeap"); _rtvHeap->Release(); }
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "Acrylic.SrvHeap"); _srvHeap->Release(); }
        if (_copyPso != null) _copyPso->Release();
        if (_kdownPso != null) _kdownPso->Release();
        if (_kupPso != null) _kupPso->Release();
        if (_compPso != null) _compPso->Release();
        if (_copyRoot != null) _copyRoot->Release();
        if (_compRoot != null) _compRoot->Release();
    }
}
