using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using ColorF = FluentGpu.Foundation.ColorF;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// Real per-node acrylic (frosted glass) — the in-app acrylic effect runner of
/// design/subsystems/backdrop-effects-animation.md §2.3 (two-pass PushLayer{Acrylic} schedule) with the
/// gpu-renderer.md §7.1 LayerPool, inlined HLSL-side in this leaf. The scene renders into an engine-owned canvas RT;
/// at each <c>PushLayer</c>:
///   pass A  SNAPSHOT+downsample the canvas region beneath the layer rect (inflated by the full blur support) into a
///           pooled offscreen RT at 1/down resolution (down = AcrylicBackdropMath.DownsampleFactor — /4 at 100% DPI),
///   pass B  blur H (fixed bilinear-tap gaussian, σ = AcrylicBackdropMath.KernelSigma in snapshot texels ⇒
///           effective σ = 30 DIP, matching microsoft-ui-xaml AcrylicBrush.h:64 sc_blurRadius),
///   pass C  blur V (ping-pong between the two pooled RTs),
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
/// WARP-safe: ps_5_1, static-array kernel, no UAVs, no typed-load requirements.
///
/// needs-pixels: the recipe values + region/bucket/kernel math are headless-checked (VerticalSlice 64m/64n via
/// HeadlessGpuDevice.LastLayers + AcrylicBackdropMath); the composited GPU pixels themselves are verified manually
/// (--shot) — there is no headless framebuffer for the D3D12 leaf.
/// </summary>
internal sealed unsafe class AcrylicCompositor : IDisposable
{
    private const int MaxPool = 8;            // pooled RT slots (a frame holds at most 2 at once; steady state uses 2)
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

    private ID3D12RootSignature* _copyRoot;   // copy/blur: 8 root consts + 1 SRV table + static linear-clamp sampler
    private ID3D12PipelineState* _copyPso, _blurPso;
    private ID3D12RootSignature* _compRoot;   // composite: 28 root consts + 1 SRV table + static sampler
    private ID3D12PipelineState* _compPso;

    /// <summary>Acrylic layers composited this frame (diagnostics).</summary>
    public int LayersThisFrame { get; private set; }

    /// <summary>Live pooled RTs (diagnostics; steady state = 2 while any acrylic surface is open).</summary>
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

    // One separable gaussian pass with the FIXED kernel (built at Init from AcrylicBackdropMath so the HLSL constants
    // cannot drift from the headless-checked weights). Sampling is clamped to the used sub-rect of the (possibly
    // larger) bucket RT so taps never read stale texels from a previous lease of the pooled texture.
    private static string BuildBlurHlsl()
    {
        var off = AcrylicBackdropMath.TapOffsets;
        var wgt = AcrylicBackdropMath.TapWeights;
        var sb = new StringBuilder(1024);
        sb.Append("static const float OFF[").Append(AcrylicBackdropMath.TapCount).Append("] = {");
        for (int i = 0; i < off.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(off[i].ToString("G9", CultureInfo.InvariantCulture)); }
        sb.Append("};\nstatic const float WGT[").Append(AcrylicBackdropMath.TapCount).Append("] = {");
        for (int i = 0; i < wgt.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(wgt[i].ToString("G9", CultureInfo.InvariantCulture)); }
        sb.Append("};\nstatic const int TAPS = ").Append(AcrylicBackdropMath.TapCount).Append(";\n");
        return """
cbuffer C : register(b0) { float4 texelDir; float4 scaleClamp; };   // texelDir: xy = source texel size, zw = blur direction; scaleClamp: xy = used-uv scale, zw = max uv (used minus half texel)
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
""" + sb + """
float4 BlurPS(V i) : SV_Target
{
    float2 c = i.uv * scaleClamp.xy;
    float2 lo = texelDir.xy * 0.5;
    float2 d = texelDir.zw * texelDir.xy;
    float4 acc = gSrc.Sample(gSamp, clamp(c, lo, scaleClamp.zw)) * WGT[0];
    [unroll] for (int k = 1; k < TAPS; k++)
    {
        acc += gSrc.Sample(gSamp, clamp(c + d * OFF[k], lo, scaleClamp.zw)) * WGT[k];
        acc += gSrc.Sample(gSamp, clamp(c - d * OFF[k], lo, scaleClamp.zw)) * WGT[k];
    }
    return acc;   // preserve premultiplied alpha; transparent Mica regions must not blur to black (fallback resolves at composite)
}
""";
    }

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

        D3D12_DESCRIPTOR_HEAP_DESC sh = default;
        sh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        sh.NumDescriptors = 1 + 2 * MaxPool;   // canvas + two parity banks of pool SRVs (see _srvHeap comment)
        sh.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* shp; Check(_device->CreateDescriptorHeap(&sh, __uuidof<ID3D12DescriptorHeap>(), (void**)&shp), "Acrylic.SrvHeap");
        _srvHeap = shp;
        _srvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
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
        _copyRoot = SampleRootSig(8);   // copy uses 4 floats, blur uses 8 — shared sig sized for the larger
        ID3DBlob* vs = Compile(CopyHlsl, "VSMain", "vs_5_1");
        ID3DBlob* copyPs = Compile(CopyHlsl, "CopyPS", "ps_5_1");
        string blurHlsl = BuildBlurHlsl();   // kernel constants injected from AcrylicBackdropMath (cold path, Init only)
        ID3DBlob* blurVs = Compile(blurHlsl, "VSMain", "vs_5_1");
        ID3DBlob* blurPs = Compile(blurHlsl, "BlurPS", "ps_5_1");
        _copyPso = MakePso(_copyRoot, vs, copyPs, blend: false);
        _blurPso = MakePso(_copyRoot, blurVs, blurPs, blend: false);
        vs->Release(); copyPs->Release(); blurVs->Release(); blurPs->Release();
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
        LayersThisFrame = 0;
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
            if (e.Res == null || e.InUse || e.W < bw || e.H < bh) continue;
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
                    if (_pool[i].InUse) continue;   // a frame leases at most 2 of 8 slots → a free victim always exists
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

    // ── frame passes ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bind the canvas as the render target, clear it, set the full viewport, and run pool upkeep.
    /// Call once before drawing the scene. <paramref name="parity"/> = frameIndex &amp; 1 selects this frame's SRV bank.</summary>
    public void BeginCanvas(ID3D12GraphicsCommandList* cmd, in ColorF clear, ulong completedFence, int parity)
    {
        _parity = parity & 1;
        LayersThisFrame = 0;
        TickPool(completedFence);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = Rtv(0);
        cmd->OMSetRenderTargets(1, &rtv, BOOL.FALSE, null);
        float* c = stackalloc float[4] { clear.R, clear.G, clear.B, clear.A };
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

    private void FullScreen(ID3D12GraphicsCommandList* cmd, ID3D12PipelineState* pso, int srvSlot, ReadOnlySpan<float> consts)
    {
        cmd->SetGraphicsRootSignature(_copyRoot);
        cmd->SetPipelineState(pso);
        fixed (float* c = consts) cmd->SetGraphicsRoot32BitConstants(0, (uint)consts.Length, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(srvSlot));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        cmd->DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>The PushLayer{Acrylic} schedule: snapshot+downsample the canvas region beneath the layer rect into a
    /// pooled RT, two-pass separable gaussian (fixed kernel), then composite the WinUI acrylic recipe into the canvas
    /// clipped to the rounded layer rect. Leaves the canvas bound for continued scene drawing.</summary>
    public void BlurAndComposite(ID3D12GraphicsCommandList* cmd, in PushLayerCmd L, float lw, float lh, float scale, ulong frameFence)
    {
        if (scale <= 0f) scale = 1f;
        int down = AcrylicBackdropMath.DownsampleFactor(L.BlurSigma, scale);
        AcrylicBackdropMath.SnapshotRegion(L.DeviceRect, scale, down, (int)_w, (int)_h, out int rx, out int ry, out int rw, out int rh);
        int dw = Math.Max(1, (rw + down - 1) / down), dh = Math.Max(1, (rh + down - 1) / down);

        int ia = Acquire(dw, dh, frameFence);
        int ib = Acquire(dw, dh, frameFence);

        SetHeap(cmd);

        // pass A: snapshot + downsample the backdrop region (canvas sub-rect) → pool A at 1/down resolution
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _pool[ia].Res, ref _pool[ia].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rt = Rtv(1 + ia); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, (uint)dw, (uint)dh);
        FullScreen(cmd, _copyPso, 0, stackalloc float[4] { (float)rx / _w, (float)ry / _h, (float)rw / _w, (float)rh / _h });

        // pass B: blur H, A → B (fixed σ=KernelSigma kernel in snapshot texels ⇒ effective σ = BlurSigma DIP full-res)
        float aw = _pool[ia].W, ah = _pool[ia].H, bw = _pool[ib].W, bh = _pool[ib].H;
        Barrier(cmd, _pool[ia].Res, ref _pool[ia].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _pool[ib].Res, ref _pool[ib].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        rt = Rtv(1 + ib); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, (uint)dw, (uint)dh);
        FullScreen(cmd, _blurPso, PoolSrvSlot(ia), stackalloc float[8]
            { 1f / aw, 1f / ah, 1f, 0f, dw / aw, dh / ah, (dw - 0.5f) / aw, (dh - 0.5f) / ah });

        // pass C: blur V, B → A (the final blurred snapshot lands back in A)
        Barrier(cmd, _pool[ib].Res, ref _pool[ib].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _pool[ia].Res, ref _pool[ia].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        rt = Rtv(1 + ia); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, (uint)dw, (uint)dh);
        FullScreen(cmd, _blurPso, PoolSrvSlot(ib), stackalloc float[8]
            { 1f / bw, 1f / bh, 0f, 1f, dw / bw, dh / bh, (dw - 0.5f) / bw, (dh - 0.5f) / bh });

        // pass D: composite the acrylic recipe into the canvas at the rounded layer rect (samples blurred A)
        Barrier(cmd, _pool[ia].Res, ref _pool[ia].State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var crt = Rtv(0); cmd->OMSetRenderTargets(1, &crt, BOOL.FALSE, null); SetViewport(cmd, _w, _h);
        cmd->SetGraphicsRootSignature(_compRoot);
        cmd->SetPipelineState(_compPso);
        Span<float> cc = stackalloc float[28]
        {
            L.DeviceRect.X, L.DeviceRect.Y, L.DeviceRect.W, L.DeviceRect.H,
            lw, lh, rx, ry,                                  // logical viewport (VS NDC) + region origin (PS phys px)
            rw, rh, dw / aw, dh / ah,                        // region size (phys px) + used-uv fraction of pool A
            L.Tint.R, L.Tint.G, L.Tint.B, L.Tint.A,
            L.Fallback.R, L.Fallback.G, L.Fallback.B, L.Fallback.A,
            L.Radii.TopLeft, L.TintOpacity, L.LuminosityOpacity, L.NoiseOpacity,
            1f / aw, 1f / ah, 0f, 0f,                        // pool-A texel size (half-texel edge clamp)
        };
        fixed (float* c = cc) cmd->SetGraphicsRoot32BitConstants(0, 28, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(PoolSrvSlot(ia)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        cmd->DrawInstanced(4, 1, 0, 0);

        Release(ia); Release(ib);   // back to the free list — the NEXT layer this frame reuses the same two RTs
        LayersThisFrame++;

        BindCanvas(cmd);   // leave canvas bound + full viewport for continued scene drawing
    }

    /// <summary>Blit the finished canvas to the back buffer (already bound as the render target by the caller).</summary>
    public void BlitToBackBuffer(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE backRtv)
    {
        SetHeap(cmd);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        cmd->OMSetRenderTargets(1, &backRtv, BOOL.FALSE, null);
        SetViewport(cmd, _w, _h);
        FullScreen(cmd, _copyPso, 0, stackalloc float[4] { 0f, 0f, 1f, 1f });
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
        if (_rtvHeap != null) _rtvHeap->Release();
        if (_srvHeap != null) _srvHeap->Release();
        if (_copyPso != null) _copyPso->Release();
        if (_blurPso != null) _blurPso->Release();
        if (_compPso != null) _compPso->Release();
        if (_copyRoot != null) _copyRoot->Release();
        if (_compRoot != null) _compRoot->Release();
    }
}
