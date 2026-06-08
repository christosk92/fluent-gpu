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
/// Real per-node acrylic (frosted glass). The scene renders into an engine-owned canvas RT; at each <c>PushLayer</c> the
/// canvas-so-far (the backdrop) is downsampled and separable-gaussian blurred, then the WinUI AcrylicBrush recipe is
/// composited into the canvas at the node's rounded rect (blurred backdrop → luminosity blend → tint → noise). The node's
/// content then draws on top. At frame end the canvas is blitted to the back buffer. All RTs + ComPtrs are owned here on
/// the render thread (per the threading-render-seam contract).
/// </summary>
internal sealed unsafe class AcrylicCompositor : IDisposable
{
    private ID3D12Device* _device;
    private uint _w, _h, _qw, _qh;   // canvas size + quarter size

    private ID3D12Resource* _canvas; private D3D12_RESOURCE_STATES _canvasState;
    private ID3D12Resource* _q0; private D3D12_RESOURCE_STATES _q0State;   // downsample / blur ping-pong (quarter res)
    private ID3D12Resource* _q1; private D3D12_RESOURCE_STATES _q1State;

    private ID3D12DescriptorHeap* _rtvHeap;   // 3 RTVs: canvas, q0, q1
    private ID3D12DescriptorHeap* _srvHeap;   // 3 shader-visible SRVs: canvas, q0, q1
    private uint _rtvInc, _srvInc;

    private ID3D12RootSignature* _copyRoot;   // copy/blur: root consts + 1 SRV table + static sampler
    private ID3D12PipelineState* _copyPso, _blurPso;
    private ID3D12RootSignature* _compRoot;   // composite: root consts + 1 SRV table + static sampler
    private ID3D12PipelineState* _compPso;

    private const string CopyHlsl = """
cbuffer C : register(b0) { float4 texelDir; };   // xy = source texel size, zw = blur direction (0 for copy)
Texture2D gSrc : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VSMain(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.pos = float4(uv * 2.0 - 1.0, 0, 1); o.pos.y = -o.pos.y; o.uv = uv; return o; }
float4 CopyPS(V i) : SV_Target { return gSrc.Sample(gSamp, i.uv); }   // preserve alpha → transparent pixels reach the backbuffer so DWM Mica composites
float4 BlurPS(V i) : SV_Target
{
    float2 d = texelDir.zw * texelDir.xy;
    float4 c = gSrc.Sample(gSamp, i.uv) * 0.227027;
    c += gSrc.Sample(gSamp, i.uv + d * 1.3846153846) * 0.3162162;
    c += gSrc.Sample(gSamp, i.uv - d * 1.3846153846) * 0.3162162;
    c += gSrc.Sample(gSamp, i.uv + d * 3.2307692308) * 0.0702703;
    c += gSrc.Sample(gSamp, i.uv - d * 3.2307692308) * 0.0702703;
    return c;                                                         // preserve premultiplied alpha; transparent Mica must not blur to black
}
""";

    private const string CompHlsl = """
cbuffer C : register(b0) { float4 rect; float4 vps; float4 tint; float4 fallback; float4 prm; };   // vps = logicalW,logicalH,physW,physH ; prm = radius,tintOp,lumOp,noiseOp
Texture2D gBlur : register(t0);
SamplerState gSamp : register(s0);
struct V { float4 pos : SV_Position; float2 local : TEXCOORD0; float2 half : TEXCOORD1; };
V VSMain(uint id : SV_VertexID)
{
    float2 corner = float2(id & 1, (id >> 1) & 1);
    float2 world = rect.xy + corner * rect.zw;                    // DeviceRect is already in logical device space (identity)
    V o;
    o.pos = float4(world.x / vps.x * 2.0 - 1.0, 1.0 - world.y / vps.y * 2.0, 0, 1);
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
    float2 uv = i.pos.xy / vps.zw;                                // SV_Position is physical px → uv into the physical-size blurred texture
    float4 src = gBlur.Sample(gSamp, uv);                         // premultiplied blurred backdrop

    // WinUI resolves transparent backdrop over opaque FallbackColor before blur. We preserve alpha through the blur
    // passes and complete that SourceOver resolve here; transparent/Mica regions therefore acrylic against fallback,
    // not transparent black.
    float3 B = src.rgb + fallback.rgb * saturate(1.0 - src.a);

    // WinUI 3 AcrylicBrush.cpp: luminosity blend first, then tint/color blend. The luminosity pass keeps backdrop
    // hue/saturation but takes lightness from the luminosity/tint color; the color pass applies tint hue/saturation
    // while preserving that luminosity result.
    float3 lumBlend = lerp(B, SetLum(B, Lum(tint.rgb)), prm.z);
    float3 colorBlend = SetLum(tint.rgb, Lum(lumBlend));
    float3 res = lerp(lumBlend, colorBlend, prm.y);
    float n = frac(sin(dot(i.pos.xy, float2(12.9898, 78.233))) * 43758.5453);
    res += (n - 0.5) * prm.w;                                    // noise
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
        rh.NumDescriptors = 3;
        ID3D12DescriptorHeap* rhp; Check(_device->CreateDescriptorHeap(&rh, __uuidof<ID3D12DescriptorHeap>(), (void**)&rhp), "Acrylic.RtvHeap");
        _rtvHeap = rhp;
        _rtvInc = _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

        D3D12_DESCRIPTOR_HEAP_DESC sh = default;
        sh.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        sh.NumDescriptors = 3;
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
        _copyRoot = SampleRootSig(4);
        ID3DBlob* vs = Compile(CopyHlsl, "VSMain", "vs_5_1");
        ID3DBlob* copyPs = Compile(CopyHlsl, "CopyPS", "ps_5_1");
        ID3DBlob* blurPs = Compile(CopyHlsl, "BlurPS", "ps_5_1");
        _copyPso = MakePso(_copyRoot, vs, copyPs, blend: false);
        _blurPso = MakePso(_copyRoot, vs, blurPs, blend: false);
        vs->Release(); copyPs->Release(); blurPs->Release();
    }

    private void BuildCompositePipeline()
    {
        _compRoot = SampleRootSig(20);
        ID3DBlob* vs = Compile(CompHlsl, "VSMain", "vs_5_1");
        ID3DBlob* ps = Compile(CompHlsl, "PSMain", "ps_5_1");
        _compPso = MakePso(_compRoot, vs, ps, blend: true);
        vs->Release(); ps->Release();
    }

    public void EnsureSize(uint w, uint h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == _w && h == _h && _canvas != null) return;
        D3D12MemoryDiagnostics.Resize("Acrylic.Targets", w, h);
        ReleaseTextures();
        _w = w; _h = h; _qw = Math.Max(1, w / 4); _qh = Math.Max(1, h / 4);

        _canvas = CreateTarget(_w, _h, "Acrylic.Canvas"); _canvasState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        _q0 = CreateTarget(_qw, _qh, "Acrylic.Q0"); _q0State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        _q1 = CreateTarget(_qw, _qh, "Acrylic.Q1"); _q1State = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;

        var rtv = _rtvHeap->GetCPUDescriptorHandleForHeapStart();
        _device->CreateRenderTargetView(_canvas, null, rtv); rtv.ptr += _rtvInc;
        _device->CreateRenderTargetView(_q0, null, rtv); rtv.ptr += _rtvInc;
        _device->CreateRenderTargetView(_q1, null, rtv);

        var srv = _srvHeap->GetCPUDescriptorHandleForHeapStart();
        CreateSrv(_canvas, srv); srv.ptr += _srvInc;
        CreateSrv(_q0, srv); srv.ptr += _srvInc;
        CreateSrv(_q1, srv);
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
        D3D12_CLEAR_VALUE cv = default; cv.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        ID3D12Resource* res;
        Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, &cv, __uuidof<ID3D12Resource>(), (void**)&res), "Acrylic.CreateTarget");
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

    private D3D12_CPU_DESCRIPTOR_HANDLE Rtv(int i) { var h = _rtvHeap->GetCPUDescriptorHandleForHeapStart(); h.ptr += (nuint)i * _rtvInc; return h; }
    private D3D12_GPU_DESCRIPTOR_HANDLE SrvGpu(int i) { var h = _srvHeap->GetGPUDescriptorHandleForHeapStart(); h.ptr += (ulong)i * _srvInc; return h; }

    private void SetViewport(ID3D12GraphicsCommandList* cmd, uint w, uint h)
    {
        D3D12_VIEWPORT vp = new() { Width = w, Height = h, MaxDepth = 1 };
        RECT sc = new() { right = (int)w, bottom = (int)h };
        cmd->RSSetViewports(1, &vp);
        cmd->RSSetScissorRects(1, &sc);
    }

    /// <summary>Bind the canvas as the render target, clear it, and set the full viewport. Call once before drawing the scene.</summary>
    public void BeginCanvas(ID3D12GraphicsCommandList* cmd, in ColorF clear)
    {
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

    /// <summary>Downsample + separable-blur the canvas, then composite the acrylic recipe into the canvas at the layer rect.</summary>
    public void BlurAndComposite(ID3D12GraphicsCommandList* cmd, in PushLayerCmd L, float lw, float lh)
    {
        SetHeap(cmd);
        // 1) downsample canvas → q0 (quarter res)
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _q0, ref _q0State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rt = Rtv(1); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, _qw, _qh);
        FullScreen(cmd, _copyPso, 0, stackalloc float[4] { 1f / _w, 1f / _h, 0, 0 });

        // 2) blur H: q0 → q1
        Barrier(cmd, _q0, ref _q0State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _q1, ref _q1State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        rt = Rtv(2); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, _qw, _qh);
        FullScreen(cmd, _blurPso, 1, stackalloc float[4] { 1f / _qw, 1f / _qh, 1f, 0f });

        // 3) blur V: q1 → q0
        Barrier(cmd, _q1, ref _q1State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _q0, ref _q0State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        rt = Rtv(1); cmd->OMSetRenderTargets(1, &rt, BOOL.FALSE, null); SetViewport(cmd, _qw, _qh);
        FullScreen(cmd, _blurPso, 2, stackalloc float[4] { 1f / _qw, 1f / _qh, 0f, 1f });

        // 4) composite acrylic into the canvas at the layer rect (samples blurred q0 at screen UV)
        Barrier(cmd, _q0, ref _q0State, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var crt = Rtv(0); cmd->OMSetRenderTargets(1, &crt, BOOL.FALSE, null); SetViewport(cmd, _w, _h);
        cmd->SetGraphicsRootSignature(_compRoot);
        cmd->SetPipelineState(_compPso);
        Span<float> cc = stackalloc float[20]
        {
            L.DeviceRect.X, L.DeviceRect.Y, L.DeviceRect.W, L.DeviceRect.H,
            lw, lh, _w, _h,             // logical viewport (VS NDC) + physical size (PS uv)
            L.Tint.R, L.Tint.G, L.Tint.B, L.Tint.A,
            L.Fallback.R, L.Fallback.G, L.Fallback.B, L.Fallback.A,
            L.Radii.TopLeft, L.TintOpacity, L.LuminosityOpacity, L.NoiseOpacity,
        };
        fixed (float* c = cc) cmd->SetGraphicsRoot32BitConstants(0, 20, c, 0);
        cmd->SetGraphicsRootDescriptorTable(1, SrvGpu(1));   // q0 holds the final blurred image
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        cmd->DrawInstanced(4, 1, 0, 0);

        BindCanvas(cmd);   // leave canvas bound + full viewport for continued scene drawing
    }

    /// <summary>Blit the finished canvas to the back buffer (already bound as the render target by the caller).</summary>
    public void BlitToBackBuffer(ID3D12GraphicsCommandList* cmd, D3D12_CPU_DESCRIPTOR_HANDLE backRtv)
    {
        SetHeap(cmd);
        Barrier(cmd, _canvas, ref _canvasState, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        cmd->OMSetRenderTargets(1, &backRtv, BOOL.FALSE, null);
        SetViewport(cmd, _w, _h);
        FullScreen(cmd, _copyPso, 0, stackalloc float[4] { 1f / _w, 1f / _h, 0, 0 });
    }

    private void ReleaseTextures()
    {
        if (_canvas != null) { D3D12MemoryDiagnostics.Release(_canvas, "Acrylic.Canvas"); _canvas->Release(); _canvas = null; }
        if (_q0 != null) { D3D12MemoryDiagnostics.Release(_q0, "Acrylic.Q0"); _q0->Release(); _q0 = null; }
        if (_q1 != null) { D3D12MemoryDiagnostics.Release(_q1, "Acrylic.Q1"); _q1->Release(); _q1 = null; }
    }

    public void Dispose()
    {
        ReleaseTextures();
        if (_rtvHeap != null) _rtvHeap->Release();
        if (_srvHeap != null) _srvHeap->Release();
        if (_copyPso != null) _copyPso->Release();
        if (_blurPso != null) _blurPso->Release();
        if (_compPso != null) _compPso->Release();
        if (_copyRoot != null) _copyRoot->Release();
        if (_compRoot != null) _compRoot->Release();
    }
}
