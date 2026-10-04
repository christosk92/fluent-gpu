using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Instance record for one series chunk — the GPU twin of <see cref="DrawSeriesCmd"/>, 76 floats (304 B),
/// laid out exactly as the HLSL <c>Inst</c> below (<c>float4 s[8]</c> at byte offset 176).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SeriesInstance
{
    public float RectX, RectY, RectW, RectH;
    public float M11, M12, M21, M22;
    public float Dx, Dy, Opacity, Shape;
    public float Count, X0, SampleDx, Baseline;
    public float Amplitude, Thickness, StopCount, Flags;
    public float C0R, C0G, C0B, C0A, C1R, C1G, C1B, C1A, C2R, C2G, C2B, C2A, C3R, C3G, C3B, C3A;
    public float O0, O1, O2, O3;
    public float Prev, Next, Total, Index;
    public Samples32 S;

    public static SeriesInstance From(in DrawSeriesCmd c)
    {
        var i = new SeriesInstance
        {
            RectX = c.Rect.X, RectY = c.Rect.Y, RectW = c.Rect.W, RectH = c.Rect.H,
            M11 = c.Transform.M11, M12 = c.Transform.M12, M21 = c.Transform.M21, M22 = c.Transform.M22,
            Dx = c.Transform.Dx, Dy = c.Transform.Dy, Opacity = c.Opacity, Shape = c.Shape,
            Count = c.Count, X0 = c.X0, SampleDx = c.Dx, Baseline = c.Baseline,
            Amplitude = c.Amplitude, Thickness = c.Thickness, StopCount = c.StopCount, Flags = c.Flags,
            C0R = c.C0.R, C0G = c.C0.G, C0B = c.C0.B, C0A = c.C0.A, C1R = c.C1.R, C1G = c.C1.G, C1B = c.C1.B, C1A = c.C1.A,
            C2R = c.C2.R, C2G = c.C2.G, C2B = c.C2.B, C2A = c.C2.A, C3R = c.C3.R, C3G = c.C3.G, C3B = c.C3.B, C3A = c.C3.A,
            O0 = c.O0, O1 = c.O1, O2 = c.O2, O3 = c.O3,
            Prev = c.Prev, Next = c.Next, Total = c.Total, Index = c.Index,
        };
        for (int k = 0; k < SeriesSpec.ChunkSamples; k++) i.S[k] = c.S[k];
        return i;
    }
}

/// <summary>The series lane (gpu-renderer.md §3.1 DrawSeriesCmd): one <c>DrawInstanced(64, chunks)</c> triangle-strip
/// pass whose vertex shader expands <c>SV_VertexID</c> into (sample, side) pairs — no vertex buffer, no tessellation,
/// no realization cache. Rides the shared SDF root signature (viewport constants b0, instance SRV t0) and the shared
/// TRIANGLESTRIP topology so it participates in the five-pipe shared-state dedup like Polyline; its PSO declares NO
/// input layout, so the shared quad VB being bound is inert. Colour = the ≤ 4-stop ramp by amplitude evaluated PER
/// PIXEL (the VS passes a signed amplitude coordinate that interpolates through 0 at the baseline, so interior stops
/// survive and a Mirrored column is not flat), premultiplied, SrcOver. Mirrored amplitude is measured against HALF the
/// height (a sample of 1.0 reaches the top/bottom edge from a 0.5 baseline) and every vertex is clamped to the chunk
/// rect, so the geometry never escapes the Rect that Cull, SliceOpBounds and damage are computed from. No AA fringe in
/// v1 (documented in the canon row).
/// <para><b>The pixel shader never reads the instance buffer.</b> The shared SDF root signature's SRV parameter is
/// <c>D3D12_SHADER_VISIBILITY_VERTEX</c> only (<c>SdfSharedResources.BuildRootSignature</c>), so a PS that touched
/// <c>gInst</c> would fail PSO creation against it. Every PS input — the ramp colours + offsets, the stop count, the
/// opacity — therefore rides <c>nointerpolation</c> VS outputs, exactly like <c>GradientPipeline</c>.</para></summary>
internal sealed unsafe class SeriesPipeline : IDisposable
{
    private const int MaxInstances = 2048;   // per-FRAME policy cap (a 512-sample series is 17 chunks)

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;
    private ID3D12PipelineState* _psoAdd;   // DrawOp.SetBlend Additive
    // Instance storage is the device's SHARED per-frame UploadArena (see PolylineStrokePipeline): MaxInstances is this
    // pipeline's per-frame POLICY cap, not a memory reservation.
    private UploadArena _arena = null!;
    private int _cursor;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst {
    float4 rect;
    float4 m;
    float2 t; float opacity; float shape;
    float count; float x0; float dx; float baseline;
    float amplitude; float thickness; float stopCount; float flags;
    float4 c0; float4 c1; float4 c2; float4 c3;
    float4 offsets;
    float4 ext;                                     // x = prev sample, y = next sample, z = total samples, w = this chunk's first index
    float4 s[8];
};
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut
{
    float4 pos : SV_Position;
    float amp : TEXCOORD0;                          // signed amplitude coordinate: +s at the top edge, -s at the bottom (Mirrored), 0 at the baseline
    nointerpolation float4 c0 : TEXCOORD1;          // the ramp rides the VS outputs: the shared root SRV is VS-visible only
    nointerpolation float4 c1 : TEXCOORD2;
    nointerpolation float4 c2 : TEXCOORD3;
    nointerpolation float4 c3 : TEXCOORD4;
    nointerpolation float4 offsets : TEXCOORD5;
    nointerpolation float2 misc : TEXCOORD6;        // x = stopCount, y = opacity
    float2 edge : TEXCOORD7;                        // x = signed distance across a ribbon (DIP), y = half the ribbon width (0 = no AA)
};

float sampleAt(uint iid, int i, int n)
{
    i = clamp(i, 0, n - 1);
    // fxc refuses a runtime index into the struct's array and into a float4's lanes (X3512): pick both with literal
    // indices — eight loads, one survives the selects; the compiler folds them into one indexed fetch where it can.
    int k = i >> 2, lane = i & 3;
    Inst it = gInst[iid];
    float4 q = it.s[0];
    q = k == 1 ? it.s[1] : q; q = k == 2 ? it.s[2] : q; q = k == 3 ? it.s[3] : q;
    q = k == 4 ? it.s[4] : q; q = k == 5 ? it.s[5] : q; q = k == 6 ? it.s[6] : q; q = k == 7 ? it.s[7] : q;
    float v = lane == 0 ? q.x : lane == 1 ? q.y : lane == 2 ? q.z : q.w;
    return saturate(v);
}

// A sample by GLOBAL neighbour: inside the chunk from the instance, just outside it from ext (prev/next), so tangents and the
// Polar seam are continuous across chunk edges.
float neighbourAt(uint iid, int i, int n, float4 ext)
{
    if (i < 0) return saturate(ext.x);
    if (i >= n) return saturate(ext.y);
    return sampleAt(iid, i, n);
}

float4 ramp(float a, float stopCount, float4 c0, float4 c1, float4 c2, float4 c3, float4 o)
{
    int n = (int)stopCount;
    if (n <= 1 || a <= o.x) return c0;
    if (a <= o.y || n == 2) return lerp(c0, c1, saturate((a - o.x) / max(o.y - o.x, 1e-4)));
    if (a <= o.z || n == 3) return lerp(c1, c2, saturate((a - o.y) / max(o.z - o.y, 1e-4)));
    return lerp(c2, c3, saturate((a - o.z) / max(o.w - o.z, 1e-4)));
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    int n = (int)it.count;
    int i = min((int)(vid >> 1), n - 1);
    int side = (int)(vid & 1);
    float s = sampleAt(iid, i, n);
    float H = it.rect.w, top = it.rect.y, bottom = it.rect.y + H;
    float baseY = top + it.baseline * H;
    // Mirrored measures its amplitude against HALF the height, so a sample of 1.0 reaches the edge from a 0.5 baseline
    float amp = it.amplitude * (it.shape < 1.5 && it.shape >= 0.5 ? 0.5 * H : H);
    float x = it.x0 + i * it.dx;
    bool aa = fmod(it.flags, 2.0) >= 1.0;
    bool along = fmod(floor(it.flags * 0.5), 2.0) >= 1.0;
    float halfT = it.thickness * 0.5;
    float fringe = aa ? 1.0 : 0.0;                 // the quad grows by one DIP each side; the PS feathers coverage into it
    float2 p; float a; float2 edge = float2(0.0, 0.0);
    if (it.shape < 0.5)       { p = float2(x, side == 0 ? baseY - amp * s : baseY); a = side == 0 ? s : 0.0; }
    else if (it.shape < 1.5)  { p = float2(x, side == 0 ? baseY - amp * s : baseY + amp * s); a = side == 0 ? s : -s; }
    else if (it.shape < 2.5)
    {
        float sPrev = neighbourAt(iid, i - 1, n, it.ext), sNext = neighbourAt(iid, i + 1, n, it.ext);
        float2 tangent = normalize(float2(2.0 * it.dx, -(sNext - sPrev) * amp));
        float2 nrm = float2(-tangent.y, tangent.x);
        float2 c = float2(x, baseY - amp * s);
        float reach = halfT + fringe;
        p = side == 0 ? c + nrm * reach : c - nrm * reach; a = s;
        if (aa) edge = float2(side == 0 ? reach : -reach, halfT);
    }
    else
    {
        // Polar: a closed loop about the box centre; the angle is the GLOBAL sample index over the whole series.
        float total = max(it.ext.z - 1.0, 1.0);
        float2 ctr = float2(it.rect.x + it.rect.z * 0.5, it.rect.y + H * 0.5);
        float rMax = 0.5 * min(it.rect.z, H) * it.amplitude;
        float g = it.ext.w + i;
        float ang = 6.28318530718 * g / total;
        float2 dir = float2(cos(ang), sin(ang));
        float2 c = ctr + dir * (rMax * s);
        a = s;
        if (it.thickness > 0.0)
        {
            float sPrev = neighbourAt(iid, i - 1, n, it.ext), sNext = neighbourAt(iid, i + 1, n, it.ext);
            float step = 6.28318530718 / total;
            float2 pp = ctr + float2(cos(ang - step), sin(ang - step)) * (rMax * sPrev);
            float2 pn = ctr + float2(cos(ang + step), sin(ang + step)) * (rMax * sNext);
            float2 tangent = normalize(pn - pp + 1e-5);
            float2 nrm = float2(-tangent.y, tangent.x);
            float reach = halfT + fringe;
            p = side == 0 ? c + nrm * reach : c - nrm * reach;
            if (aa) edge = float2(side == 0 ? reach : -reach, halfT);
        }
        else p = side == 0 ? c : ctr;
    }
    if (along) a = (it.ext.w + i) / max(it.ext.z - 1.0, 1.0);   // the ramp by position along the series, not by height
    if (it.shape < 2.5) p.y = clamp(p.y, top - fringe, bottom + fringe);   // never escape the chunk rect (+ the AA fringe, inside StrokeHalo)
    float2 world = float2(it.m.x * p.x + it.m.z * p.y + it.t.x, it.m.y * p.x + it.m.w * p.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.amp = a;
    o.c0 = it.c0; o.c1 = it.c1; o.c2 = it.c2; o.c3 = it.c3;
    o.offsets = it.offsets;
    o.misc = float2(it.stopCount, it.opacity);
    o.edge = edge;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float4 col = ramp(abs(i.amp), i.misc.x, i.c0, i.c1, i.c2, i.c3, i.offsets);   // per pixel: |amp| runs baseline -> peak on BOTH sides of a Mirrored column
    col.a *= i.misc.y;
    if (i.edge.y > 0.0) col.a *= saturate(i.edge.y + 0.5 - abs(i.edge.x));   // the 1-DIP analytic fringe on a ribbon edge
    return float4(col.rgb * col.a, col.a);
}
""";

    public void Init(ID3D12Device* device, SdfSharedResources shared, UploadArena arena)
    {
        _shared = shared;
        _arena = arena;
        BuildPipeline(device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    private void BuildPipeline(ID3D12Device* device)
    {
        ID3DBlob* vs = ShaderCompiler.Compile(Hlsl, "VSMain", "vs_5_1", "series");
        ID3DBlob* ps = ShaderCompiler.Compile(Hlsl, "PSMain", "ps_5_1", "series");
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
        pd.pRootSignature = _shared.RootSignature;
        pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
        pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
        pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = null, NumElements = 0 };   // SV_VertexID only
        pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pd.NumRenderTargets = 1;
        pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        pd.SampleDesc.Count = 1;
        pd.SampleMask = uint.MaxValue;
        pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
        pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
        pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
        pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
        pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
        pd.DepthStencilState.DepthEnable = BOOL.FALSE;
        pd.DepthStencilState.StencilEnable = BOOL.FALSE;
        ID3D12PipelineState* pso;
        Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Series.CreateGraphicsPipelineState");
        _pso = pso;
        // The ADDITIVE variant (visualizer F4): colour ONE/ONE adds the premultiplied source; alpha ZERO/ONE leaves the
        // target's alpha untouched, so inside a transparent tile the result composites Over the page as page + glow.
        pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
        pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ZERO;
        pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
        pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
        ID3D12PipelineState* psoAdd;
        Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&psoAdd), "Series.CreateGraphicsPipelineState(Additive)");
        _psoAdd = psoAdd;
        vs->Release();
        ps->Release();
    }

    /// <summary>Reset this frame's policy cap + drop counter. Bank selection belongs to the shared <see cref="UploadArena"/>,
    /// begun once per frame by the device.</summary>
    public void BeginFrame(int slot) { _ = slot; _cursor = 0; _dropped = 0; }

    /// <summary>Record one run (the Polyline contract: shared state and the PSO rebind independently; false when full,
    /// state untouched).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<SeriesInstance> instances, float vpW, float vpH,
                       bool bindSharedState = true, bool bindPipelineState = true, bool additive = false)
    {
        int count = Math.Min(instances.Length, MaxInstances - _cursor);
        if (count <= 0) { _dropped += instances.Length; return false; }
        if (!_arena.TryReserve(count * sizeof(SeriesInstance), out byte* dst, out ulong gva))
        { _dropped += instances.Length; return false; }
        _dropped += instances.Length - count;
        SeriesInstance* slot = (SeriesInstance*)dst;
        for (int i = 0; i < count; i++) slot[i] = instances[i];
        _cursor += count;
        if (bindSharedState)
        {
            cmd->SetGraphicsRootSignature(_shared.RootSignature);
            _shared.SetViewportConstants(cmd, vpW, vpH);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            var qv = _shared.QuadView;
            cmd->IASetVertexBuffers(0, 1, &qv);
        }
        if (bindPipelineState) cmd->SetPipelineState(additive ? _psoAdd : _pso);
        cmd->SetGraphicsRootShaderResourceView(1, gva);
        cmd->DrawInstanced((uint)(2 * SeriesSpec.ChunkSamples), (uint)count, 0, 0); GpuDrawCount.Frame++;
        return true;
    }

    public void Dispose()
    {
        // No instance buffers to release: the shared UploadArena owns them (disposed by the device).
        if (_pso != null) { _pso->Release(); _pso = null; }
        if (_psoAdd != null) { _psoAdd->Release(); _psoAdd = null; }
    }
}
