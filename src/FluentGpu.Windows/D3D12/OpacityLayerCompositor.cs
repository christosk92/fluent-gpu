using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;
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
/// LastLayers. Perf: the RT stays canvas-sized (group content can extend past the layer rect), but the blur is confined
/// to the layer's device rect + the kernel's tap halo (<see cref="BlurInPlace"/>) — a one-line lyrics glow is a ~44 px
/// band, not the whole window — and for σ &gt; 4 it runs the Skia/Impeller downsample-then-separable-Gaussian schedule
/// (blur at 1/down resolution with an ≤7-tap kernel), so a big editorial-card blur pays a fraction of the full-res fill.
/// </summary>
internal sealed unsafe class OpacityLayerCompositor : IDisposable
{
    private const int MaxPool = 32;              // CANVAS-sized transient group slots + small REGION-sized blur-cache PINS (one pool)
    private const int PinBudget = 24;            // max retained region pins; guarantees ≥ MaxPool-PinBudget = 8 slots for transient scratch
    private const int TrimIdleFrames = 600;      // transient (canvas) free entries idle this long (~10 s) are retired (fence-gated)
    private const int PinTrimIdleFrames = 120;   // pins idle this long (~2 s of SUBMITTED frames) are retired — a stationary pin is FindPin-hit every submit, so only ORPHANS (a rect/σ a row left) climb to this

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
        // What resource each parity bank's SRV descriptor for this slot currently DESCRIBES (null = never written).
        // Lets Acquire/FindPin skip the per-frame CreateShaderResourceView when the slot's resource is unchanged —
        // the descriptor write was pure per-acquire overhead. Every retire/recreate path clears the whole entry
        // (`= default`), so a recreated resource can never alias stale tracking.
        public ID3D12Resource* SrvResParity0;
        public ID3D12Resource* SrvResParity1;
    }
    private readonly PoolEntry[] _pool = new PoolEntry[MaxPool];

    /// <summary>A region-local transient self-blur target. Scene commands keep absolute coordinates; the device uses a
    /// shifted full-canvas viewport so those pixels land at <c>absolute - Origin</c> in this small surface.</summary>
    public readonly record struct LocalBlurSurface(
        int Slot, int OriginX, int OriginY, int UsedW, int UsedH,
        SelfBlurPixelBox VisibleOutput);

    private struct Retired { public ID3D12Resource* Res; public ulong Fence; }
    private readonly List<Retired> _retired = new();

    private ID3D12DescriptorHeap* _rtvHeap;   // MaxPool RTVs (slot i)
    private ID3D12DescriptorHeap* _srvHeap;   // 2·MaxPool shader-visible SRVs, parity-banked (parity·MaxPool + i)
    private ID3D12QueryHeap* _timestampHeap;
    private ID3D12Resource* _timestampReadback;
    private ulong* _timestampData;
    private ulong _timestampFrequency;
    private readonly bool[] _timestampPending = new bool[2];
    private bool _blurTimingOpen;
    private uint _rtvInc, _srvInc;
    private int _parity;
    // Heap-backed constant scratch — safe when NativeAOT inlines these callees into SubmitWithLayers' draw-list loop.
    private readonly float[] _scratch4 = new float[4];
    private readonly float[] _scratch8 = new float[8];
    private readonly float[] _scratch16 = new float[16];

    private ID3D12RootSignature* _root;       // 1 root const (alpha) + 1 SRV table + static linear-clamp sampler
    private ID3D12PipelineState* _pso;        // fullscreen triangle, src = RT × alpha, blend ONE/INV_SRC_ALPHA
    private ID3D12RootSignature* _edgeRoot;   // edge fade: 16 root consts (rect + per-edge band + corner radii + falloff/intensity/alpha) + SRV + point sampler
    private ID3D12PipelineState* _edgePso;    // per-edge feather composite (premultiplied SourceOver, like _pso) — follows the rounded corners (the curve)
    private ID3D12RootSignature* _copyRoot;   // down/up sample: 8 root consts (src uv off+scale + clamp bounds) + SRV table + LINEAR sampler
    private ID3D12PipelineState* _copyPso;    // bilinear stretch copy (the downsample prefilter + the upsample; no blend — full overwrite)
    private ID3D12RootSignature* _dsBlurRoot; // downsampled separable blur: 24 root consts (texel size, axis, usedFrac/maxUv, BuildKernel taps) + SRV + LINEAR
    private ID3D12PipelineState* _dsBlurPso;  // separable gaussian at texelSigma over the DOWNSAMPLED scratch, unrolled 7 folded bilinear taps
    private readonly float[] _blurTaps = new float[24];   // ds-blur consts: p0(texel.xy,dir.xy) + p1(usedFrac.xy,maxUv.xy) + o0/o1(offsets) + w0/w1(weights)

    /// <summary>Opacity groups composited this frame (diagnostics).</summary>
    public int GroupsThisFrame { get; private set; }

    /// <summary>Self-blur layers filtered this frame (cache hits are excluded).</summary>
    public int BlurLayersThisFrame { get; private set; }

    /// <summary>Physical pixels in the self-blur output regions this frame.</summary>
    public long BlurRegionPixelsThisFrame { get; private set; }

    /// <summary>Physical pixels touched by self-blur filter/copy passes this frame.</summary>
    public long BlurWorkPixelsThisFrame { get; private set; }

    /// <summary>Physical pixels covered by self-blur composites this frame.</summary>
    public long BlurCompositePixelsThisFrame { get; private set; }

    /// <summary>Physical pixels cleared for region-local self-blur targets this frame.</summary>
    public long BlurClearPixelsThisFrame { get; private set; }

    /// <summary>Render-target creations caused by the opacity/self-blur pool this frame.</summary>
    public int RtCreatesThisFrame { get; private set; }

    /// <summary>Most recently completed GPU span from the first self-blur pass through the last one in a frame.</summary>
    public double LastBlurGpuMs { get; private set; }

    /// <summary>Live pooled RTs (diagnostics; steady state = nesting depth while any group is animating).</summary>
    public int PooledRtCount
    {
        get { int n = 0; for (int i = 0; i < MaxPool; i++) if (_pool[i].Res != null) n++; return n; }
    }

    // Fullscreen-triangle composite: sample the group RT and scale ALL channels by the group alpha (premultiplied).
    private const string Hlsl = """
cbuffer C : register(b0) { float4 prm; float4 uvRect; };   // prm.x = alpha; uvRect = offset.xy,size.xy
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 PSMain(V i) : SV_Target { return gSrc.Sample(gSamp, uvRect.xy + i.uv * uvRect.zw) * prm.x; }
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

    // Downsample/upsample bilinear stretch (the Flutter-Impeller / Skia prefilter — a plain LINEAR sub-rect copy, no
    // blend). os.xy = source uv offset, os.zw = source uv scale; the sample uv is clamped to [cl.xy, cl.zw] (the used
    // sub-rect minus a half-texel guard, so an upsample from a pooled bucket RT never bilinear-bleeds the stale texels
    // past its used region). Downsample passes a full-region clamp (identity). Premultiplied throughout.
    private const string CopyDsHlsl = """
cbuffer C : register(b0) { float4 os; float4 cl; };   // os.xy=off os.zw=scale; cl.xy=minUv cl.zw=maxUv
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 CopyPS(V i) : SV_Target { float2 uv = clamp(os.xy + i.uv * os.zw, cl.xy, cl.zw); return gSrc.Sample(gSamp, uv); }
""";

    // Separable Gaussian over the DOWNSAMPLED scratch (down > 1): the CPU builds ≤ 7 bilinear-folded taps for
    // texelSigma = σ/down ≤ 4 (AcrylicBackdropMath.BuildKernel — the ONE headless-checked kernel source) and uploads
    // them as constants; the shader is fully UNROLLED (center + 6 symmetric pairs, unused pairs carry weight 0), so
    // there is NO dynamic float4 component indexing (fxc X3504) — every tap is a static swizzle. Each tap is a LINEAR
    // fetch at a fractional-texel offset (the fold); the weights already sum to 1, so no renormalize. Every sample is
    // clamped to the used sub-rect of the (bucket-quantized, possibly larger) pooled RT, like the acrylic Kawase passes.
    private const string DsBlurHlsl = """
cbuffer C : register(b0) { float4 p0; float4 p1; float4 o0; float4 o1; float4 w0; float4 w1; };
// p0.xy = 1/bucketSize, p0.zw = axis dir (1,0)|(0,1); p1.xy = usedFrac (used/bucket), p1.zw = maxUv (usedFrac - half texel);
// o0 = offsets[0..3] (o0.x = 0 center), o1 = offsets[4..6],pad; w0 = weights[0..3], w1 = weights[4..6],pad.
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 T(float2 uv, float2 lo, float2 hi) { return gSrc.Sample(gSamp, clamp(uv, lo, hi)); }
float4 BlurPS(V i) : SV_Target
{
    float2 lo = p0.xy * 0.5;                  // half-texel clamp floor
    float2 mx = p1.zw;                        // usedFrac - half texel clamp ceil
    float2 base = i.uv * p1.xy;               // viewport [0,1] → the used sub-rect
    float2 stp = p0.zw * p0.xy;               // one texel along the blur axis
    float4 acc = T(base, lo, mx) * w0.x;                                       // center (o0.x == 0)
    acc += (T(base + stp * o0.y, lo, mx) + T(base - stp * o0.y, lo, mx)) * w0.y;
    acc += (T(base + stp * o0.z, lo, mx) + T(base - stp * o0.z, lo, mx)) * w0.z;
    acc += (T(base + stp * o0.w, lo, mx) + T(base - stp * o0.w, lo, mx)) * w0.w;
    acc += (T(base + stp * o1.x, lo, mx) + T(base - stp * o1.x, lo, mx)) * w1.x;
    acc += (T(base + stp * o1.y, lo, mx) + T(base - stp * o1.y, lo, mx)) * w1.y;
    acc += (T(base + stp * o1.z, lo, mx) + T(base - stp * o1.z, lo, mx)) * w1.z;
    return acc;                              // BuildKernel weights sum to 1 → premultiplied result, no renormalize
}
""";

    public void Init(ID3D12Device* device, ID3D12CommandQueue* queue)
    {
        _device = device;
        InitTimestamps(queue);
        BuildHeaps();
        BuildPipeline();
        BuildEdgeFadePipeline();
        BuildDownsamplePipelines();
    }

    // Root signature shared by the two downsample-schedule pipelines: N 32-bit root consts + 1 SRV table + a static
    // LINEAR-clamp sampler (both the bilinear stretch and the folded-tap gaussian want bilinear filtering).
    private ID3D12RootSignature* BuildLinearSampleRootSig(int numConsts, string what)
    {
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1;
        range.BaseShaderRegister = 0;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[2];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.Num32BitValues = (uint)numConsts;
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

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), what);
        ID3D12RootSignature* rs;
        Check(_device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&rs), what);
        sig->Release();
        if (err != null) err->Release();
        return rs;
    }

    private ID3D12PipelineState* BuildOverwritePso(ID3D12RootSignature* root, string hlsl, string psEntry, string what)
    {
        ID3DBlob* vs = CompileSource(hlsl, "VSMain", "vs_5_1");
        ID3DBlob* ps = CompileSource(hlsl, psEntry, "ps_5_1");
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
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.FALSE;   // full overwrite (each pass IS the new RT contents)
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        ID3D12PipelineState* pso;
        Check(_device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), what);
        vs->Release(); ps->Release();
        return pso;
    }

    // The downsample-then-separable-Gaussian schedule (BlurInPlace, down > 1): a LINEAR bilinear-stretch copy PSO for
    // the down/up sample, and a separable-blur PSO whose taps come from AcrylicBackdropMath.BuildKernel.
    private void BuildDownsamplePipelines()
    {
        _copyRoot = BuildLinearSampleRootSig(8, "OpacityLayer.CopyDsRootSig");
        _copyPso = BuildOverwritePso(_copyRoot, CopyDsHlsl, "CopyPS", "OpacityLayer.CopyDsPso");
        _dsBlurRoot = BuildLinearSampleRootSig(24, "OpacityLayer.DsBlurRootSig");
        _dsBlurPso = BuildOverwritePso(_dsBlurRoot, DsBlurHlsl, "BlurPS", "OpacityLayer.DsBlurPso");
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    private void InitTimestamps(ID3D12CommandQueue* queue)
    {
        ulong frequency;
        if (queue == null || queue->GetTimestampFrequency(&frequency) < 0 || frequency == 0) return;
        _timestampFrequency = frequency;
        D3D12_QUERY_HEAP_DESC qd = default;
        qd.Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        qd.Count = 4;
        ID3D12QueryHeap* heap;
        if (_device->CreateQueryHeap(&qd, __uuidof<ID3D12QueryHeap>(), (void**)&heap) < 0) return;
        _timestampHeap = heap;

        D3D12_HEAP_PROPERTIES hp = default;
        hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = 4 * sizeof(ulong); rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
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
        D3D12MemoryDiagnostics.Track(readback, "OpacityLayer.TimestampReadback", 4 * sizeof(ulong));
        void* mapped;
        if (readback->Map(0, null, &mapped) >= 0) _timestampData = (ulong*)mapped;
    }

    private void CollectGpuTime(int parity)
    {
        if (!_timestampPending[parity] || _timestampData == null || _timestampFrequency == 0) return;
        int query = parity * 2;
        ulong begin = _timestampData[query], end = _timestampData[query + 1];
        _timestampPending[parity] = false;
        if (end >= begin) LastBlurGpuMs = (end - begin) * 1000.0 / _timestampFrequency;
    }

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
        p[0].Anonymous.Constants.Num32BitValues = 8;
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
#if false // Superseded by the CPU-folded, unrolled DsBlur pipeline retained below.
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

#endif

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

    /// <summary>Write the CURRENT parity bank's SRV for this slot only if that descriptor doesn't already describe the
    /// slot's resource. Acquire/FindPin run per group per frame — for a stable slot the descriptor write is redundant
    /// after the first two frames (one per parity bank), and on layer-heavy frames those writes were measurable.</summary>
    private void EnsureParitySrv(ref PoolEntry e, int slot)
    {
        ref ID3D12Resource* described = ref (_parity == 0 ? ref e.SrvResParity0 : ref e.SrvResParity1);
        if (described == e.Res) return;
        CreateSrv(e.Res, SrvCpu(PoolSrvSlot(slot)));
        described = e.Res;
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
            int trimAt = e.PinHash != 0 ? PinTrimIdleFrames : TrimIdleFrames;   // pins reclaim fast (orphans), transient keeps the 10 s window
            if (++e.IdleFrames <= trimAt) continue;
            _retired.Add(new Retired { Res = e.Res, Fence = e.LastUseFence });
            e = default;
        }
        DrainRetired(completedFence);
    }

    /// <summary>Per-frame upkeep on the layered (canvas) path. <paramref name="parity"/> = frameIndex &amp; 1.</summary>
    public void BeginFrame(ulong completedFence, int parity)
    {
        _parity = parity & 1;
        CollectGpuTime(_parity);
        _blurTimingOpen = false;
        GroupsThisFrame = 0;
        BlurLayersThisFrame = 0;
        BlurRegionPixelsThisFrame = 0;
        BlurWorkPixelsThisFrame = 0;
        BlurCompositePixelsThisFrame = 0;
        BlurClearPixelsThisFrame = 0;
        RtCreatesThisFrame = 0;
        TickPool(completedFence);
    }

    private void BeginBlurTiming(ID3D12GraphicsCommandList* cmd)
    {
        if (_blurTimingOpen || _timestampHeap == null) return;
        cmd->EndQuery(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, (uint)(_parity * 2));
        _blurTimingOpen = true;
    }

    /// <summary>Resolve this frame's blur span into a parity-banked readback buffer. The value is consumed only after
    /// that back-buffer bank is fenced and reused, so diagnostics never introduce a GPU wait.</summary>
    public void EndFrame(ID3D12GraphicsCommandList* cmd)
    {
        if (!_blurTimingOpen || _timestampHeap == null || _timestampReadback == null) return;
        uint query = (uint)(_parity * 2);
        cmd->EndQuery(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, query + 1);
        cmd->ResolveQueryData(_timestampHeap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP,
            query, 2, _timestampReadback, (ulong)query * sizeof(ulong));
        _timestampPending[_parity] = true;
        _blurTimingOpen = false;
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
    public int Acquire(ID3D12GraphicsCommandList* cmd, ulong frameFence, RECT* clearRect = null)
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
            RtCreatesThisFrame++;
            _pool[slot].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _pool[slot].W = _w; _pool[slot].H = _h;
            _device->CreateRenderTargetView(_pool[slot].Res, null, Rtv(slot));   // RTVs are CPU-read at record time — safe to rewrite
            best = slot;
        }

        ref var entry = ref _pool[best];
        entry.InUse = true;
        entry.IdleFrames = 0;
        entry.LastUseFence = frameFence;
        EnsureParitySrv(ref entry, best);   // refresh THIS frame's parity bank (skipped when it already describes this resource)

        Barrier(cmd, entry.Res, ref entry.State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = Rtv(best);
        cmd->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        _scratch4[0] = _scratch4[1] = _scratch4[2] = _scratch4[3] = 0f;   // transparent — only the subtree's pixels composite
        fixed (float* clear = _scratch4)
            cmd->ClearRenderTargetView(rtv, clear, clearRect is null ? 0u : 1u, clearRect);
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

    /// <summary>The canvas-sized render target currently owned by an open group. Acrylic nested inside that group must
    /// sample and composite against this target—not the main back buffer—or the group's later composite overwrites it.</summary>
    public ID3D12Resource* TargetResource(int slot) => _pool[slot].Res;

    /// <summary>RTV paired with <see cref="TargetResource"/> for an open group slot.</summary>
    public D3D12_CPU_DESCRIPTOR_HANDLE TargetRtv(int slot) => Rtv(slot);

    /// <summary>Transition the slot to shader-readable. Call BEFORE the caller binds the underlying target for
    /// <see cref="Composite"/> (split out so the caller controls which target the composite lands on).</summary>
    public void BeginRead(ID3D12GraphicsCommandList* cmd, int slot)
        => Barrier(cmd, _pool[slot].Res, ref _pool[slot].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);

    /// <summary>Composite the leased RT over the CURRENTLY-BOUND render target at <paramref name="alpha"/> (a
    /// fullscreen pass; the group RT is canvas-sized and 1:1). The current scissor applies — it is the group node's
    /// enclosing clip, which already bounded the subtree's content.</summary>
    public void Composite(ID3D12GraphicsCommandList* cmd, int slot, float alpha)
        => CompositeUv(cmd, slot, alpha, 0f, 0f, 1f, 1f);

    private void CompositeUv(ID3D12GraphicsCommandList* cmd, int slot, float alpha,
        float uvX, float uvY, float uvW, float uvH)
    {
        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_root);
        cmd->SetPipelineState(_pso);
        _scratch8[0] = Math.Clamp(alpha, 0f, 1f);
        _scratch8[1] = _scratch8[2] = _scratch8[3] = 0f;
        _scratch8[4] = uvX; _scratch8[5] = uvY; _scratch8[6] = uvW; _scratch8[7] = uvH;
        fixed (float* c = _scratch8)
        {
            cmd->SetGraphicsRoot32BitConstants(0, 8, c, 0);
            cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(slot)));
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            cmd->DrawInstanced(3, 1, 0, 0);
        }
        GroupsThisFrame++;
    }

    /// <summary>Composite a canvas-backed self-blur only over its finite Gaussian support. Generic opacity groups remain
    /// full-target because their descendants may intentionally paint outside the node bounds.</summary>
    public void CompositeBlur(ID3D12GraphicsCommandList* cmd, int slot, float alpha, in PushLayerCmd L, float scale, RECT clip)
    {
        RegionBox(in L, scale, out int minX, out int minY, out int maxX, out int maxY);
        RECT box = new()
        {
            left = Math.Max(minX, clip.left),
            top = Math.Max(minY, clip.top),
            right = Math.Min(maxX, clip.right),
            bottom = Math.Min(maxY, clip.bottom),
        };
        if (box.right <= box.left || box.bottom <= box.top) return;
        cmd->RSSetScissorRects(1, &box);
        BlurCompositePixelsThisFrame += (long)(box.right - box.left) * (box.bottom - box.top);
        Composite(cmd, slot, alpha);
    }

    /// <summary>Composite the leased RT over the currently-bound target while feathering its premultiplied alpha per-edge
    /// (following the rounded corners) per the edge-fade fields of <paramref name="L"/> — the alpha-mask edge fade. The
    /// group RT is canvas-sized + 1:1, so the layer's device rect lives in the same space as <c>SV_Position</c>.</summary>
    public void EdgeFadeComposite(ID3D12GraphicsCommandList* cmd, int slot, in PushLayerCmd L, float scale, RECT clip)
    {
        RectF cr = L.CompositeClip;
        if (cr.W <= 0f || cr.H <= 0f) return;
        int left = Math.Clamp((int)MathF.Floor(cr.X * scale), 0, (int)_w);
        int top = Math.Clamp((int)MathF.Floor(cr.Y * scale), 0, (int)_h);
        int right = Math.Clamp((int)MathF.Ceiling((cr.X + cr.W) * scale), left, (int)_w);
        int bottom = Math.Clamp((int)MathF.Ceiling((cr.Y + cr.H) * scale), top, (int)_h);
        RECT box = new()
        {
            left = Math.Max(left, clip.left),
            top = Math.Max(top, clip.top),
            right = Math.Min(right, clip.right),
            bottom = Math.Min(bottom, clip.bottom),
        };
        if (box.right <= box.left || box.bottom <= box.top) return;
        cmd->RSSetScissorRects(1, &box);

        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_edgeRoot);
        cmd->SetPipelineState(_edgePso);
        // DeviceRect / bands / corners arrive in LOGICAL (DIP) device space; SV_Position is PHYSICAL px — scale by the
        // frame DPI factor (the acrylic composite does the same, AcrylicCompositor.SnapshotRegion).
        _scratch16[0] = L.DeviceRect.X * scale;
        _scratch16[1] = L.DeviceRect.Y * scale;
        _scratch16[2] = (L.DeviceRect.X + L.DeviceRect.W) * scale;
        _scratch16[3] = (L.DeviceRect.Y + L.DeviceRect.H) * scale;
        _scratch16[4] = L.FadeBandL * scale;
        _scratch16[5] = L.FadeBandT * scale;
        _scratch16[6] = L.FadeBandR * scale;
        _scratch16[7] = L.FadeBandB * scale;
        _scratch16[8] = L.Radii.TopLeft * scale;
        _scratch16[9] = L.Radii.TopRight * scale;
        _scratch16[10] = L.Radii.BottomRight * scale;
        _scratch16[11] = L.Radii.BottomLeft * scale;
        _scratch16[12] = L.FadeFalloff;
        _scratch16[13] = Math.Clamp(L.FadeIntensity, 0f, 1f);
        _scratch16[14] = Math.Clamp(L.GroupAlpha, 0f, 1f);
        _scratch16[15] = L.FadeEdges;
        fixed (float* c = _scratch16)
        {
            cmd->SetGraphicsRoot32BitConstants(0, 16, c, 0);
            cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(slot)));
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            cmd->DrawInstanced(3, 1, 0, 0);
        }
        GroupsThisFrame++;
    }

    /// <summary>Return the slot to the free list (same-queue reuse needs no fence).</summary>
    public void Release(int slot) => _pool[slot].InUse = false;

    /// <summary>Acquire the small pooled target used while an animated, non-nested self blur is changing sigma. A full
    /// kernel-radius transparent guard surrounds the clip-aware work box, so clamp sampling at the pooled texture edge
    /// is indistinguishable from sampling transparent pixels beyond the blur source.</summary>
    public bool TryAcquireLocalBlur(ID3D12GraphicsCommandList* cmd, ulong frameFence, in PushLayerCmd layer, float scale,
        out LocalBlurSurface surface)
    {
        surface = default;
        if (AcrylicBackdropMath.DownsampleFactor(layer.BlurSigma, 1f) != 1) return false;
        SelfBlurWorkGeometry geometry = SelfBlurRegion.ComputeWork(in layer, scale, (int)_w, (int)_h);
        if (geometry.Work.IsEmpty || geometry.VisibleOutput.IsEmpty) return false;

        int guard = SelfBlurRegion.TapRadius(layer.BlurSigma);
        int usedW = geometry.Work.Width + guard * 2;
        int usedH = geometry.Work.Height + guard * 2;
        if (usedW <= 0 || usedH <= 0) return false;
        // If both axes are effectively canvas-sized the legacy target is cheaper and preserves nested/overspill behavior.
        if (usedW >= (int)_w && usedH >= (int)_h) return false;

        int slot = AcquireScratch(cmd, usedW, usedH, frameFence);
        Bind(cmd, slot);
        var rtv = Rtv(slot);
        _scratch4[0] = _scratch4[1] = _scratch4[2] = _scratch4[3] = 0f;
        fixed (float* clear = _scratch4)
            cmd->ClearRenderTargetView(rtv, clear, 0, null);
        BlurClearPixelsThisFrame += (long)_pool[slot].W * _pool[slot].H;

        surface = new LocalBlurSurface(
            slot,
            geometry.Work.MinX - guard,
            geometry.Work.MinY - guard,
            usedW,
            usedH,
            geometry.VisibleOutput);
        return true;
    }

    /// <summary>Run the exact CPU-kernel separable Gaussian over a region-local animated-blur target.</summary>
    public void BlurLocalInPlace(ID3D12GraphicsCommandList* cmd, in LocalBlurSurface surface, float sigma, ulong frameFence)
    {
        BeginBlurTiming(cmd);
        int groupSlot = surface.Slot;
        int scratch = AcquireScratch(cmd, surface.UsedW, surface.UsedH, frameFence);
        Span<float> offsets = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        Span<float> weights = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        int count = AcrylicBackdropMath.BuildKernel(sigma, offsets, weights);

        BeginRead(cmd, groupSlot);
        Bind(cmd, scratch);
        SetViewport(cmd, (uint)surface.UsedW, (uint)surface.UsedH);
        DsBlurPass(cmd, groupSlot, (int)_pool[groupSlot].W, (int)_pool[groupSlot].H,
            surface.UsedW, surface.UsedH, 1f, 0f, offsets, weights, count);

        BeginRead(cmd, scratch);
        Bind(cmd, groupSlot);
        SetViewport(cmd, (uint)surface.UsedW, (uint)surface.UsedH);
        DsBlurPass(cmd, scratch, (int)_pool[scratch].W, (int)_pool[scratch].H,
            surface.UsedW, surface.UsedH, 0f, 1f, offsets, weights, count);

        BeginRead(cmd, groupSlot);
        Release(scratch);
        long pixels = (long)surface.UsedW * surface.UsedH;
        BlurLayersThisFrame++;
        BlurRegionPixelsThisFrame += surface.VisibleOutput.AreaPx;
        BlurWorkPixelsThisFrame += pixels * 2L;
    }

    /// <summary>Composite the visible portion of a region-local blur at its screen position, sampling the matching
    /// sub-rectangle of the bucketed source without stretching.</summary>
    public void CompositeLocalBlur(ID3D12GraphicsCommandList* cmd, in LocalBlurSurface surface, float alpha, RECT clip)
    {
        SelfBlurPixelBox output = surface.VisibleOutput;
        RECT box = new()
        {
            left = Math.Max(output.MinX, clip.left),
            top = Math.Max(output.MinY, clip.top),
            right = Math.Min(output.MaxX, clip.right),
            bottom = Math.Min(output.MaxY, clip.bottom),
        };
        if (box.right <= box.left || box.bottom <= box.top) return;

        int outW = output.Width, outH = output.Height;
        D3D12_VIEWPORT vp = new()
        {
            TopLeftX = output.MinX,
            TopLeftY = output.MinY,
            Width = outW,
            Height = outH,
            MaxDepth = 1f,
        };
        cmd->RSSetViewports(1, &vp);
        cmd->RSSetScissorRects(1, &box);
        float bw = _pool[surface.Slot].W, bh = _pool[surface.Slot].H;
        CompositeUv(cmd, surface.Slot, alpha,
            (output.MinX - surface.OriginX) / bw,
            (output.MinY - surface.OriginY) / bh,
            outW / bw,
            outH / bh);
        BlurCompositePixelsThisFrame += (long)(box.right - box.left) * (box.bottom - box.top);
        SetViewport(cmd, _w, _h);
    }

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
        => SelfBlurRegion.RegionBox(in L, scale, (int)_w, (int)_h, out minX, out minY, out maxX, out maxY);

    /// <summary>A valid (already-blurred) region pin for <paramref name="hash"/> whose retained region size EQUALS this
    /// frame's <see cref="RegionBox"/> (W==rw &amp;&amp; H==rh), refreshed into this frame's SRV bank, or -1. The pin is left
    /// PIXEL_SHADER_RESOURCE between frames, so the caller composites it with no barrier. The size-exact match is
    /// load-bearing: <see cref="RegionBox"/> depends on the sub-pixel <c>frac(pos·scale)</c> floor/ceil, so a mid-ease
    /// pin can differ by 1px from the rest region — matching hash-only would composite that wrong-sized pin STRETCHED
    /// onto the current viewport (the pin's UV[0,1] mapped onto a taller/shorter box). A physical-size mismatch is a MISS
    /// (the miss path renders + blurs the exact region), never a stretch. Paired with <see cref="RetainPinFromScratch"/>'s
    /// one-pin-per-hash retire so at most one BlurReady pin ever carries a given hash. G1: a hit also stamps
    /// <paramref name="frameFence"/> into LastUseFence so a hot (repeatedly-hit) pin stays MRU and is NEVER the preferred
    /// LRU victim — otherwise a stationary pin keeps its creation fence forever = permanently the oldest entry = the first
    /// thing evicted once the pool fills (the rolling-eviction fps cliff this fixes).</summary>
    public int FindPin(ulong hash, ulong frameFence, in PushLayerCmd L, float scale)
    {
        if (hash == 0) return -1;
        RegionBox(in L, scale, out int minX, out int minY, out int maxX, out int maxY);
        uint rw = (uint)Math.Max(1, maxX - minX), rh = (uint)Math.Max(1, maxY - minY);   // == RetainPinFromScratch's stored W/H
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || e.InUse || e.PinHash != hash || !e.BlurReady || e.W != rw || e.H != rh) continue;
            e.IdleFrames = 0;
            e.LastUseFence = frameFence;                // G1: a hit makes the pin MRU
            EnsureParitySrv(ref e, i);                  // refresh THIS frame's parity bank for the composite (deduped)
            return i;
        }
        return -1;
    }

    /// <summary>Count of retained region pins currently held in the pool (PinHash != 0).</summary>
    private int CountPins() { int n = 0; for (int i = 0; i < MaxPool; i++) if (_pool[i].Res != null && _pool[i].PinHash != 0) n++; return n; }

    /// <summary>Retire the COLDEST non-in-use region pin (fence-gated). With G1 keeping hot pins' fences fresh, the
    /// coldest pin is a genuine orphan (a rect/σ a row has left) — so the budget cap never evicts a live pin.</summary>
    private void EvictLruPin()
    {
        int v = -1;
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || e.InUse || e.PinHash == 0) continue;
            if (v < 0 || e.LastUseFence < _pool[v].LastUseFence) v = i;
        }
        if (v >= 0) { _retired.Add(new Retired { Res = _pool[v].Res, Fence = _pool[v].LastUseFence }); _pool[v] = default; }
    }

    /// <summary>True iff this self-blur's halo-inflated region is clamped by a CANVAS edge this frame — i.e. the
    /// UNCLAMPED <see cref="RegionBox"/> would poke outside [0,_w]×[0,_h] (delegates to the portable
    /// <see cref="SelfBlurRegion.IsClamped"/>). A clamped region captures/needs only a PARTIAL strip. It can STILL be
    /// cached: because <see cref="RegionBox"/> clamps and <see cref="FindPin"/> matches SIZE-exactly, a clamped pin is
    /// distinct from the full on-canvas pin — no squish is possible (a mismatched size is a miss, never a stretch), so a
    /// STATIONARY edge-clamped row HITS its own clamped pin every frame instead of re-blurring. This predicate is now
    /// used only to gate MINTING while the layer is IN MOTION: an actively-scrolling clamped strip changes its clamp
    /// size ~1px/frame, so minting there would churn a fresh region-sized RT every frame — the caller skips the mint in
    /// that case (miss-render only, unchanged cost) and mints at rest.</summary>
    public bool RegionIsClamped(in PushLayerCmd L, float scale) => SelfBlurRegion.IsClamped(in L, scale, (int)_w, (int)_h);

    /// <summary>True iff pin <paramref name="slot"/>'s captured integer region origin differs from this frame's region
    /// origin — i.e. the content moved since the pin was minted. Used only on a SETTLED frame (PushLayerCmd.InMotion==0)
    /// to force one exact re-mint at rest, so the final displayed pin is rasterized at the true rest position.</summary>
    public bool PinOriginDiffers(int slot, in PushLayerCmd L, float scale)
    {
        RegionBox(in L, scale, out int minX, out int minY, out _, out _);
        return _pool[slot].RegionX != minX || _pool[slot].RegionY != minY;
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

        // One pin per hash: a same-hash pin at a DIFFERENT region size (minted mid-ease, before the height settled —
        // RegionBox depends on the sub-pixel frac(pos·scale) floor/ceil) must never coexist with this one. FindPin now
        // matches size-exactly, so a wrong-size survivor is invisible to it, but it would still occupy a slot + budget and
        // (were the match ever hash-only again) be returned then rejected every frame by PinOriginDiffers ⇒ a permanent
        // per-row re-Gaussian. Retire it (fence-gated) before (re)minting at the current size.
        for (int i = 0; i < MaxPool; i++)
        {
            ref var st = ref _pool[i];
            if (st.Res == null || st.InUse || st.PinHash != hash || (st.W == rw && st.H == rh)) continue;
            _retired.Add(new Retired { Res = st.Res, Fence = st.LastUseFence });
            st = default;
        }

        int slot = -1;
        for (int i = 0; i < MaxPool; i++)   // reuse this hash's existing same-size pin if it survived (no pin-count change)
            if (_pool[i].Res != null && !_pool[i].InUse && _pool[i].PinHash == hash && _pool[i].W == rw && _pool[i].H == rh) { slot = i; break; }
        if (slot < 0)   // no live same-size pin for this hash ⇒ will MINT a new one — enforce the pin budget first
        {
            if (CountPins() >= PinBudget) EvictLruPin();   // G2: cap region pins so they can never starve canvas scratch (≥ 8 slots stay for transient)
            for (int i = 0; i < MaxPool; i++) if (_pool[i].Res == null) { slot = i; break; }   // cold growth
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
    public void CompositePinnedBlur(ID3D12GraphicsCommandList* cmd, int slot, float alpha, in PushLayerCmd L, float scale, RECT clip)
    {
        RegionBox(in L, scale, out int minX, out int minY, out int maxX, out int maxY);
        int rw = maxX - minX, rh = maxY - minY;
        if (rw <= 0 || rh <= 0) return;
        RECT box = new()
        {
            left = Math.Max(minX, clip.left),
            top = Math.Max(minY, clip.top),
            right = Math.Min(maxX, clip.right),
            bottom = Math.Min(maxY, clip.bottom),
        };
        if (box.right <= box.left || box.bottom <= box.top) return;
        D3D12_VIEWPORT vp = new() { TopLeftX = minX, TopLeftY = minY, Width = rw, Height = rh, MaxDepth = 1 };
        cmd->RSSetViewports(1, &vp);
        cmd->RSSetScissorRects(1, &box);
        Composite(cmd, slot, alpha);
        SetViewport(cmd, _w, _h);   // restore the full-canvas viewport (+ full scissor; the caller re-applies its clip)
    }

    /// <summary>Gaussian-blur the group RT <paramref name="groupSlot"/> IN PLACE (the Expressive Motion Kit self-blur).
    /// The slot holds the subtree at full alpha; this blurs it at dynamic σ <paramref name="sigma"/> px and leaves the
    /// group readable (PIXEL_SHADER_RESOURCE) for the caller's <see cref="Composite"/>. Two schedules by the Skia/Impeller
    /// downsample factor (<see cref="AcrylicBackdropMath.DownsampleFactor"/>, σ read as sigmaPhys):
    ///  • <b>down == 1</b> (σ ≤ 4 — lyrics DoF, the skeleton cross-blur): the EXACT full-res separable path (leases a
    ///    canvas scratch, H group→scratch then V scratch→group), scissored to the halo-inflated device rect. Pixel-exact.
    ///  • <b>down &gt; 1</b> (σ &gt; 4): DOWNSAMPLE the region into a bucketed scratch at 1/down, run H+V at
    ///    <c>texelSigma = σ/down ≤ 4</c> (≤ 7 <see cref="AcrylicBackdropMath.BuildKernel"/> taps) on that small RT, then
    ///    UPSAMPLE back into the group RT's region — ~30× fewer texture fetches at σ26 than the full-res Gaussian.
    /// Both schedules write the SAME halo-inflated region (<see cref="RegionBox"/>) into the group RT, so the following
    /// <see cref="Composite"/> and the pin copy (<see cref="RetainPinFromScratch"/>) are unchanged and a HIT stays
    /// pixel-identical to a MISS. Leaves the full-canvas viewport + the region scissor set; the caller restores the clip.</summary>
    public void BlurInPlace(ID3D12GraphicsCommandList* cmd, int groupSlot, float sigma, ulong frameFence, in PushLayerCmd L, float scale)
    {
        BeginBlurTiming(cmd);
        // The halo-inflated device rect (RegionBox = DeviceRect×scale ± the kernel's actual tap support) — MUST match the
        // blur passes' effective reach so the scissor/region keeps every non-zero tap, and MUST match the blur-cache pin
        // region (RetainPinFromScratch copies it). SelfBlurRegion derives the halo from the SAME downsample schedule below.
        RegionBox(in L, scale, out int minX, out int minY, out int maxX, out int maxY);
        if (maxX <= minX || maxY <= minY) { BeginRead(cmd, groupSlot); return; }   // off-screen / degenerate — nothing to blur
        RECT box = new() { left = minX, top = minY, right = maxX, bottom = maxY };

        // Downsample factor (Skia/Impeller): σ ≤ 4 ⇒ down 1 ⇒ the EXACT full-res separable path (small blurs — lyrics DoF,
        // the FA-3 skeleton cross-blur — must stay pixel-exact). σ > 4 ⇒ down > 1 ⇒ downsample → blur at texelSigma ≤ 4 →
        // upsample: the same portable schedule the acrylic path documents (AcrylicBackdropMath owns down/texelSigma/kernel).
        // The self-blur σ is already physical px, so DownsampleFactor reads it as sigmaPhys directly (scale 1).
        int down = AcrylicBackdropMath.DownsampleFactor(sigma, 1f);
        int regionW = maxX - minX, regionH = maxY - minY;
        long regionPixels = (long)regionW * regionH;
        BlurLayersThisFrame++;
        BlurRegionPixelsThisFrame += regionPixels;
        if (down <= 1)
        {
            int scratch = Acquire(cmd, frameFence);   // leased + cleared (WHOLE RT → transparent outside the scissor) + bound
            Span<float> exactOffsets = stackalloc float[AcrylicBackdropMath.MaxTapCount];
            Span<float> exactWeights = stackalloc float[AcrylicBackdropMath.MaxTapCount];
            int exactCount = AcrylicBackdropMath.BuildKernel(sigma, exactOffsets, exactWeights);
            // pass H: group (SRV) → scratch (RT), clipped to the halo-inflated device rect
            BeginRead(cmd, groupSlot);
            Bind(cmd, scratch);
            SetViewport(cmd, _w, _h);                 // full viewport (1:1 mapping); the scissor below bounds the work
            cmd->RSSetScissorRects(1, &box);
            DsBlurPass(cmd, groupSlot, (int)_w, (int)_h, (int)_w, (int)_h, 1f, 0f, exactOffsets, exactWeights, exactCount);
            // pass V: scratch (SRV) → group (RT), same scissor
            BeginRead(cmd, scratch);
            Bind(cmd, groupSlot);
            SetViewport(cmd, _w, _h);
            cmd->RSSetScissorRects(1, &box);
            DsBlurPass(cmd, scratch, (int)_w, (int)_h, (int)_w, (int)_h, 0f, 1f, exactOffsets, exactWeights, exactCount);
            BlurWorkPixelsThisFrame += regionPixels * 2L;
            // leave the group readable for the composite; the scratch returns to the free list (same-queue reuse, no fence)
            BeginRead(cmd, groupSlot);
            Release(scratch);
            return;
        }

        // ── down > 1: downsample-then-separable-Gaussian-then-upsample ───────────────────────────────────────────────
        int dw = Math.Max(1, (regionW + down - 1) / down);          // ceil(region / down) — the intermediate resolution
        int dh = Math.Max(1, (regionH + down - 1) / down);
        float texelSigma = AcrylicBackdropMath.EffectiveTexelSigma(sigma, 1f, down);   // = σ/down ≤ 4 (exact when < 4)
        Span<float> koff = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        Span<float> kwgt = stackalloc float[AcrylicBackdropMath.MaxTapCount];
        int nt = AcrylicBackdropMath.BuildKernel(texelSigma, koff, kwgt);              // ≤ 7 bilinear-folded taps

        int a = AcquireScratch(cmd, dw, dh, frameFence);           // bucketed small RT (region/down) — reused by size bucket
        int b = AcquireScratch(cmd, dw, dh, frameFence);
        BlurWorkPixelsThisFrame += (long)dw * dh * 3L + regionPixels;
        int bwA = (int)_pool[a].W, bhA = (int)_pool[a].H;          // A/B share the same bucket (same dw,dh)

        // pass 0: DOWNSAMPLE — group RT region [minX..maxX] → A [0..dw] (bilinear stretch, the prefilter)
        BeginRead(cmd, groupSlot);
        Bind(cmd, a);
        SetViewport(cmd, (uint)dw, (uint)dh);
        CopyDsPass(cmd, groupSlot, (float)minX / _w, (float)minY / _h, (float)regionW / _w, (float)regionH / _h,
            (float)minX / _w, (float)minY / _h, (float)maxX / _w, (float)maxY / _h);
        // pass H: A → B at texelSigma over the downsampled scratch
        BeginRead(cmd, a);
        Bind(cmd, b);
        SetViewport(cmd, (uint)dw, (uint)dh);
        DsBlurPass(cmd, a, bwA, bhA, dw, dh, 1f, 0f, koff, kwgt, nt);
        // pass V: B → A
        BeginRead(cmd, b);
        Bind(cmd, a);
        SetViewport(cmd, (uint)dw, (uint)dh);
        DsBlurPass(cmd, b, bwA, bhA, dw, dh, 0f, 1f, koff, kwgt, nt);
        // pass U: UPSAMPLE — A [0..dw] → group RT region [minX..maxX] (bilinear stretch back, overwrite)
        BeginRead(cmd, a);
        Bind(cmd, groupSlot);
        float tx = 1f / bwA, ty = 1f / bhA;
        float ux = (float)dw / bwA, uy = (float)dh / bhA;         // A's used-uv fraction
        D3D12_VIEWPORT rvp = new() { TopLeftX = minX, TopLeftY = minY, Width = regionW, Height = regionH, MaxDepth = 1 };
        cmd->RSSetViewports(1, &rvp);
        cmd->RSSetScissorRects(1, &box);
        CopyDsPass(cmd, a, 0f, 0f, ux, uy, tx * 0.5f, ty * 0.5f, ux - tx * 0.5f, uy - ty * 0.5f);
        // restore the full-canvas viewport (device sets it once) + the region scissor, and leave the group readable —
        // matching the full-res tail so the caller's Composite (a canvas-sized 1:1 pass) maps correctly.
        SetViewport(cmd, _w, _h);
        cmd->RSSetScissorRects(1, &box);
        BeginRead(cmd, groupSlot);
        Release(a); Release(b);
    }

    /// <summary>Lease a SMALL bucket-quantized (<see cref="AcrylicBackdropMath.BucketDim"/>) transient RT ≥ (w,h) for the
    /// downsample-blur scratch — distinct from the canvas-sized <see cref="Acquire"/> (which the down==1 blur binds) and
    /// from region PINS. Kept in the shared pool as a transient entry (PinHash 0) with a NON-canvas size, so the canvas
    /// <see cref="Acquire"/>'s <c>W==_w &amp;&amp; H==_h</c> reuse guard never grabs it and this never grabs a canvas slot; a
    /// downsampling burst (a height tween) reuses the same bucket frame-to-frame. NO managed allocation.</summary>
    private int AcquireScratch(ID3D12GraphicsCommandList* cmd, int w, int h, ulong frameFence)
    {
        int bw = AcrylicBackdropMath.BucketDim(w), bh = AcrylicBackdropMath.BucketDim(h);
        int best = -1;
        for (int i = 0; i < MaxPool; i++)
        {
            ref var e = ref _pool[i];
            if (e.Res == null || e.InUse || e.PinHash != 0) continue;   // pins are never scratch
            if (e.W == _w && e.H == _h) continue;                        // canvas transient — leave it for Acquire
            if ((int)e.W < bw || (int)e.H < bh) continue;
            if (best < 0 || (long)e.W * e.H < (long)_pool[best].W * _pool[best].H) best = i;
        }
        if (best < 0)
        {
            int slot = -1;
            for (int i = 0; i < MaxPool; i++) if (_pool[i].Res == null) { slot = i; break; }
            if (slot < 0)
            {
                for (int i = 0; i < MaxPool; i++)   // LRU evict, preferring an UNPINNED (transient/scratch) victim
                {
                    if (_pool[i].InUse) continue;
                    if (slot < 0) { slot = i; continue; }
                    bool iUn = _pool[i].PinHash == 0, sUn = _pool[slot].PinHash == 0;
                    if (iUn != sUn) { if (iUn) slot = i; continue; }
                    if (_pool[i].LastUseFence < _pool[slot].LastUseFence) slot = i;
                }
                if (slot < 0) throw new InvalidOperationException("opacity-layer scratch pool exhausted");
                _retired.Add(new Retired { Res = _pool[slot].Res, Fence = _pool[slot].LastUseFence });
                _pool[slot] = default;
            }
            _pool[slot].Res = CreateTarget((uint)bw, (uint)bh, $"OpacityLayer.Scratch[{slot}]");
            RtCreatesThisFrame++;
            _pool[slot].State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            _pool[slot].W = (uint)bw; _pool[slot].H = (uint)bh;
            _device->CreateRenderTargetView(_pool[slot].Res, null, Rtv(slot));
            best = slot;
        }
        ref var entry = ref _pool[best];
        entry.InUse = true;
        entry.IdleFrames = 0;
        entry.LastUseFence = frameFence;
        EnsureParitySrv(ref entry, best);
        return best;
    }

    // Bilinear stretch copy (downsample prefilter / upsample). src uv = clamp(off + uv·scale, [clMin, clMax]).
    private void CopyDsPass(ID3D12GraphicsCommandList* cmd, int srcSlot, float offX, float offY, float sclX, float sclY,
        float clMinX, float clMinY, float clMaxX, float clMaxY)
    {
        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_copyRoot);
        cmd->SetPipelineState(_copyPso);
        _scratch8[0] = offX; _scratch8[1] = offY; _scratch8[2] = sclX; _scratch8[3] = sclY;
        _scratch8[4] = clMinX; _scratch8[5] = clMinY; _scratch8[6] = clMaxX; _scratch8[7] = clMaxY;
        fixed (float* c = _scratch8)
        {
            cmd->SetGraphicsRoot32BitConstants(0, 8, c, 0);
            cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(srcSlot)));
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            cmd->DrawInstanced(3, 1, 0, 0);
        }
    }

    // Separable gaussian at texelSigma over the downsampled scratch, one axis per invocation. The taps come from
    // AcrylicBackdropMath.BuildKernel (≤ 7); the shader unrolls 7 pairs with unused weights zeroed, so no dynamic indexing.
    private void DsBlurPass(ID3D12GraphicsCommandList* cmd, int srcSlot, int bucketW, int bucketH, int usedW, int usedH,
        float dirX, float dirY, ReadOnlySpan<float> off, ReadOnlySpan<float> wgt, int nt)
    {
        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_dsBlurRoot);
        cmd->SetPipelineState(_dsBlurPso);
        float tx = 1f / bucketW, ty = 1f / bucketH;
        float ux = (float)usedW / bucketW, uy = (float)usedH / bucketH;
        var s = _blurTaps;
        s[0] = tx; s[1] = ty; s[2] = dirX; s[3] = dirY;                       // p0
        s[4] = ux; s[5] = uy; s[6] = ux - tx * 0.5f; s[7] = uy - ty * 0.5f;   // p1 (usedFrac, maxUv)
        for (int k = 0; k < 8; k++) { s[8 + k] = 0f; s[16 + k] = 0f; }        // o0/o1 + w0/w1 (unused taps → 0)
        for (int k = 0; k < nt; k++) { s[8 + k] = off[k]; s[16 + k] = wgt[k]; }
        fixed (float* c = s)
        {
            cmd->SetGraphicsRoot32BitConstants(0, 24, c, 0);
            cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(srcSlot)));
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            cmd->DrawInstanced(3, 1, 0, 0);
        }
    }

#if false // Superseded by DsBlurPass, including the full-resolution down==1 path.
    private void BlurPass(ID3D12GraphicsCommandList* cmd, int srcSlot, float sigma, float dirX, float dirY)
    {
        ID3D12DescriptorHeap* h = _srvHeap;
        cmd->SetDescriptorHeaps(1, &h);
        cmd->SetGraphicsRootSignature(_blurRoot);
        cmd->SetPipelineState(_blurPso);
        _scratch8[0] = 1f / _w;
        _scratch8[1] = 1f / _h;
        _scratch8[2] = dirX;
        _scratch8[3] = dirY;
        _scratch8[4] = sigma;
        _scratch8[5] = _scratch8[6] = _scratch8[7] = 0f;
        fixed (float* c = _scratch8)
        {
            cmd->SetGraphicsRoot32BitConstants(0, 8, c, 0);
            cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(srcSlot)));
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            cmd->DrawInstanced(3, 1, 0, 0);
        }
    }

#endif

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
        if (_timestampReadback != null)
        {
            if (_timestampData != null) _timestampReadback->Unmap(0, null);
            _timestampData = null;
            D3D12MemoryDiagnostics.Release(_timestampReadback, "OpacityLayer.TimestampReadback");
            _timestampReadback->Release(); _timestampReadback = null;
        }
        if (_timestampHeap != null) { _timestampHeap->Release(); _timestampHeap = null; }
        if (_rtvHeap != null) { D3D12MemoryDiagnostics.Release(_rtvHeap, "OpacityLayer.RtvHeap"); _rtvHeap->Release(); }
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "OpacityLayer.SrvHeap"); _srvHeap->Release(); }
        if (_pso != null) _pso->Release();
        if (_root != null) _root->Release();
        if (_edgePso != null) _edgePso->Release();
        if (_edgeRoot != null) _edgeRoot->Release();
        if (_copyPso != null) _copyPso->Release();
        if (_copyRoot != null) _copyRoot->Release();
        if (_dsBlurPso != null) _dsBlurPso->Release();
        if (_dsBlurRoot != null) _dsBlurRoot->Release();
    }
}
