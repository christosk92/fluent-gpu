using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// CPU mirror of the HLSL <c>Inst</c> struct (144 B, float4-aligned). One instanced quad per rounded-rect-family
/// primitive, with a world transform + opacity. <see cref="Kind"/> selects the SDF variant:
/// 0 = rounded rect (fill, or an SDF outline when <see cref="StrokeWidth"/> &gt; 0 — optionally dashed via
/// <see cref="DashOn"/>/<see cref="DashOff"/>, px along the perimeter clockwise from the top edge's left end);
/// 1 = checkerboard fill (<see cref="CellPx"/> square cells alternating color/ColorB — the ColorPicker alpha lane);
/// 2 = the WinUI selected-tab shape (radii.x = top radius, radii.w = bottom flare radius — TabViewItem.cpp:98-123).
/// <see cref="ClipX"/>… carry the tier-2 rounded clip (device-space rounded box; ClipW ≤ 0 = none): the PS multiplies
/// coverage by the clip SDF so an animated clip on a rounded surface (AnimChannel.ClipL/T/R/B + Corners) clips this
/// pipeline's primitives with round corners (other pipelines stay scissor-clipped — documented on ClipCmd).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RectInstance
{
    public float PosX, PosY, W, H;
    public float RTL, RTR, RBR, RBL;
    public float R, G, B, A;
    public float M11, M12, M21, M22, Dx, Dy;   // 2x3 world transform (local→device)
    public float Opacity;
    public float StrokeWidth;   // 0 = filled; >0 = an SDF outline (focus ring / border) of this width.
    public float DashOn, DashOff;   // dashed outline: on/off px along the perimeter; either ≤ 0 ⇒ solid
    public float Kind;              // 0 = rect, 1 = checker fill, 2 = tab shape (see type doc)
    public float CellPx;            // checker cell size (local units)
    public float BR, BG, BB, BA;    // checker second color (ColorB)
    public float ClipX, ClipY, ClipW, ClipH;   // tier-2 rounded clip box (device space); ClipW ≤ 0 = none
    public float ClipR;             // rounded-clip corner radius (device units)
    public float Pad0, Pad1, Pad2;  // pad to a float4 multiple (36 floats = 144 B — must match the HLSL Inst)
}

/// <summary>
/// The SDF rounded-rect pipeline (design/subsystems/gpu-renderer.md): a unit quad drawn instanced; instance data
/// (rect/radii/color) is read in the VS from a root StructuredBuffer; the PS evaluates the analytic rounded-box SDF
/// with single-pass AA. E9 variants live in the same PS: dashed outlines (perimeter arc-length modulation),
/// checkerboard fills, the WinUI selected-tab shape, and the tier-2 rounded-clip coverage clamp.
/// Shaders are compiled at runtime (D3DCompile → DXBC sm5.1; DXC→DXIL offline is the spec's eventual path).
/// </summary>
internal sealed unsafe class RoundRectPipeline : IDisposable
{
    private const int MaxInstances = 4096;
    private const int FrameCount = 2;   // double-buffered per frame-in-flight so frame N's CPU writes never race frame N-1's GPU reads

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;
    private readonly ID3D12Resource*[] _instances = new ID3D12Resource*[FrameCount];   // structured buffer of RectInstance per frame-in-flight (upload heap, persistently mapped)
    private readonly RectInstance*[] _mapped = new RectInstance*[FrameCount];
    private int _cursor;
    private int _active;
    private ulong _activeGva;
    private int _dropped;

    public int DroppedInstances => _dropped;

    private const string Hlsl = """
struct Inst
{
    float2 pos; float2 size;
    float4 radii;
    float4 color;
    float4 m; float2 t; float opacity; float stroke;
    float2 dash; float kind; float cellPx;
    float4 colorB;
    float4 clip;            // xy = origin, zw = size (device space); z <= 0 = no rounded clip
    float clipR; float3 pad;
};
StructuredBuffer<Inst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut
{
    float4 pos : SV_Position;
    float2 local : TEXCOORD0;      // SDF coverage space (centred local units — crisp under transform via fwidth)
    float2 halfSize : TEXCOORD1;
    float4 radii : TEXCOORD2;      // x = uniform/top radius, w = tab flare radius
    float4 color : TEXCOORD3;
    float4 colorB : TEXCOORD4;
    float4 clip : TEXCOORD5;       // rounded-clip box (device space)
    float4 misc : TEXCOORD6;       // x = opacity, y = stroke, z = kind, w = cellPx
    float4 misc2 : TEXCOORD7;      // x = dashOn, y = dashOff, z = clipR
    float2 world : TEXCOORD8;      // device-space position (for the rounded clip SDF)
};

VSOut VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    Inst it = gInst[iid];
    // INFLATE the quad outward by a margin so the FULL coverage footprint is rasterized. The PS evaluates an SDF whose
    // edge sits at the rect boundary (fill) or straddles the centreline ±stroke/2 (outline); the antialiasing feather
    // adds ~1px more on each side. The bare rect quad clips that outer half — harmless on straight edges (a hair thin)
    // but it slices the rectangular quad corner through a rounded band, so corners/pill-ends read ROUGH. The margin is in
    // local units; ~2px covers the AA feather at sane DPI, plus stroke/2 to contain the outline's outer half.
    float margin = (it.stroke > 0.0 ? it.stroke * 0.5 : 0.0) + 2.0;
    float2 dir = corner * 2.0 - 1.0;                            // -1 at corner 0, +1 at corner 1 (outward)
    float2 lp = it.pos + corner * it.size + dir * margin;       // inflated local-space point
    float2 world = float2(it.m.x * lp.x + it.m.z * lp.y + it.t.x,  // 2x3 affine: local → device
                          it.m.y * lp.x + it.m.w * lp.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.local = corner * it.size - it.size * 0.5 + dir * margin;
    o.halfSize = it.size * 0.5;
    o.radii = it.radii;
    o.color = it.color;
    o.colorB = it.colorB;
    o.clip = it.clip;
    o.misc = float4(it.opacity, it.stroke, it.kind, it.cellPx);
    o.misc2 = float4(it.dash.x, it.dash.y, it.clipR, 0.0);
    o.world = world;
    return o;
}

float SdRoundBox(float2 p, float2 b, float r)
{
    float2 q = abs(p) - (b - r);
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;   // signed distance to the rounded-box edge
}

// Per-corner rounded box: pick the quadrant's radius (radii = TL,TR,BR,BL; local origin at centre, -y = top).
// The shell content card declares (r, r, 0, 0) — a uniform-radius SDF silently rounded its BOTTOM corners too.
float SdRoundBox4(float2 p, float2 b, float4 radii)
{
    float rr = (p.x < 0.0) ? ((p.y < 0.0) ? radii.x : radii.w) : ((p.y < 0.0) ? radii.y : radii.z);
    rr = min(rr, min(b.x, b.y));
    float2 q = abs(p) - (b - rr);
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - rr;
}

float4 PSMain(VSOut i) : SV_Target
{
    float kind = i.misc.z;
    float stroke = i.misc.y;
    float d;
    if (kind > 1.5)
    {
        // ── tab shape (WinUI TabViewItem::UpdateTabGeometry, TabViewItem.cpp:98-123) ──
        // body: the rect inset by the flare radius per side, TOP corners rounded at radii.x, bottom square;
        // flares: the bottom-corner squares OUTSIDE the body, minus a flare-radius disc centred at the outer
        // edge flare-radius above the bottom (the concave quarter-arc the path's "a 4,4 0 0 0" encodes).
        float fr = i.radii.w;
        float2 bh = float2(max(i.halfSize.x - fr, 0.0), i.halfSize.y);
        float r = (i.local.y < 0.0) ? min(i.radii.x, min(bh.x, bh.y)) : 0.0;   // per-corner radius: top only
        float2 q = abs(i.local) - (bh - r);
        float dBody = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
        d = dBody;
        if (fr > 0.0)
        {
            float2 fp = float2(abs(i.local.x), i.local.y);                  // mirror x: both flares in one test
            float2 sqC = float2(i.halfSize.x - fr * 0.5, i.halfSize.y - fr * 0.5);
            float2 sq = abs(fp - sqC) - fr * 0.5;
            float dSquare = min(max(sq.x, sq.y), 0.0) + length(max(sq, 0.0));
            float2 fc = float2(i.halfSize.x, i.halfSize.y - fr);            // concave-arc centre (outer edge)
            float dFlare = max(dSquare, fr - length(fp - fc));              // square ∖ disc = the inverted fillet
            d = min(dBody, dFlare);
        }
    }
    else
    {
        d = SdRoundBox4(i.local, i.halfSize, i.radii);
    }
    float fw = max(fwidth(d), 1e-4);
    float cov;
    if (stroke > 0.0)
        cov = clamp(0.5 - (abs(d) - stroke * 0.5) / fw, 0.0, 1.0);   // outline: a band of width 'stroke' centred on the edge
    else
        cov = clamp(0.5 - d / fw, 0.0, 1.0);                          // fill: crisp ~1px linear AA

    // ── dashed outline: modulate the band by an arc-length perimeter parameter (clockwise from the top edge's
    // left end; straight edges are exact, corner arcs use angle × radius). Either dash component ≤ 0 ⇒ solid. ──
    if (stroke > 0.0 && i.misc2.x > 0.0 && i.misc2.y > 0.0)
    {
        float r2 = min(i.radii.x, min(i.halfSize.x, i.halfSize.y));
        float2 hp = max(i.halfSize - r2, 0.0);
        float A = 2.0 * hp.x, B = 1.5707963 * r2, C = 2.0 * hp.y;
        float2 p = i.local;
        float per;
        if (abs(p.x) <= hp.x)
            per = (p.y < 0.0) ? (p.x + hp.x) : (A + B + C + B) + (hp.x - p.x);
        else if (abs(p.y) <= hp.y)
            per = (p.x > 0.0) ? (A + B) + (p.y + hp.y) : (A + B + C + B + A + B) + (hp.y - p.y);
        else
        {
            float2 cp = p - sign(p) * hp;
            if (p.x > 0.0 && p.y < 0.0)      per = A + atan2(cp.x, -cp.y) * r2;                      // top-right arc
            else if (p.x > 0.0)              per = A + B + C + atan2(cp.y, cp.x) * r2;               // bottom-right
            else if (p.y > 0.0)              per = A + B + C + B + A + atan2(-cp.x, cp.y) * r2;      // bottom-left
            else                             per = A + B + C + B + A + B + C + atan2(-cp.y, -cp.x) * r2; // top-left
        }
        float m = i.misc2.x + i.misc2.y;
        float ph = fmod(per, m);
        float fwp = max(fwidth(per), 1e-4);
        // soft window over [0, dashOn]: AA'd dash ends (the wrap seam at per=0 keeps WinUI StrokeDashArray's
        // non-normalized behavior — the last dash may truncate).
        cov *= clamp((i.misc2.x - ph) / fwp + 0.5, 0.0, 1.0) * clamp(ph / fwp + 0.5, 0.0, 1.0);
    }

    // ── checkerboard fill (ColorPicker alpha lane): square cells from the rect's top-left, alternating color
    // (even cells — WinUI's blank/transparent cell) and colorB (the checker color). ColorHelpers.cpp:384-404. ──
    float4 baseCol = i.color;
    if (kind > 0.5 && kind < 1.5)
    {
        float2 cell = floor((i.local + i.halfSize) / max(i.misc.w, 1.0));
        baseCol = (fmod(cell.x + cell.y, 2.0) < 0.5) ? i.color : i.colorB;
    }

    // ── tier-2 rounded clip (device space): multiply coverage by the clipping rounded-box SDF. ──
    if (i.clip.z > 0.0)
    {
        float dc = SdRoundBox(i.world - (i.clip.xy + i.clip.zw * 0.5), i.clip.zw * 0.5, min(i.misc2.z, min(i.clip.z, i.clip.w) * 0.5));
        cov *= clamp(0.5 - dc / max(fwidth(dc), 1e-4), 0.0, 1.0);
    }

    float aOut = baseCol.a * cov * i.misc.x;
    return float4(baseCol.rgb * aOut, aOut);   // premultiplied alpha
}
""";

    public void Init(ID3D12Device* device, SdfSharedResources shared)
    {
        _shared = shared;
        BuildPipeline(device);
        BuildBuffers(device);
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
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
                throw new InvalidOperationException($"shader {entry} ({target}) failed: {msg}");
            }
        }
        return code;
    }

    private void BuildPipeline(ID3D12Device* device)
    {
        ID3DBlob* vs = Compile("VSMain", "vs_5_1");
        ID3DBlob* ps = Compile("PSMain", "ps_5_1");

        byte[] semantic = Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        {
            D3D12_INPUT_ELEMENT_DESC elem = default;
            elem.SemanticName = (sbyte*)sem;
            elem.SemanticIndex = 0;
            elem.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
            elem.InputSlot = 0;
            elem.AlignedByteOffset = 0;
            elem.InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;
            elem.InstanceDataStepRate = 0;

            D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
            pd.pRootSignature = _shared.RootSignature;
            pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
            pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
            pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = &elem, NumElements = 1 };
            pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            pd.NumRenderTargets = 1;
            pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            pd.SampleDesc.Count = 1;
            pd.SampleMask = uint.MaxValue;

            // rasterizer: solid, no cull
            pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
            pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
            pd.RasterizerState.DepthClipEnable = BOOL.TRUE;

            // alpha blend on RT0
            pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
            pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;   // premultiplied
            pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
            pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;

            // depth/stencil off
            pd.DepthStencilState.DepthEnable = BOOL.FALSE;
            pd.DepthStencilState.StencilEnable = BOOL.FALSE;

            ID3D12PipelineState* pso;
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "CreateGraphicsPipelineState");
            _pso = pso;
        }
        vs->Release();
        ps->Release();
    }

    private void BuildBuffers(ID3D12Device* device)
    {
        for (int f = 0; f < FrameCount; f++)
        {
            _instances[f] = CreateUpload(device, (uint)(sizeof(RectInstance) * MaxInstances), "RoundRect.InstanceUpload");
            void* ip; _instances[f]->Map(0, null, &ip);
            _mapped[f] = (RectInstance*)ip;   // persistently mapped
        }
    }

    private static ID3D12Resource* CreateUpload(ID3D12Device* device, uint bytes, string name)
    {
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
        ID3D12Resource* res;
        Check(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "CreateCommittedResource");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    /// <summary>Select this frame's instance buffer (by back-buffer index) and reset the cursor. The chosen buffer was
    /// last written FrameCount frames ago, whose GPU work the device has already fenced — so no CPU↔GPU race.</summary>
    public void BeginFrame(int frameIndex) { _active = ((frameIndex % FrameCount) + FrameCount) % FrameCount; _activeGva = _instances[_active]->GetGPUVirtualAddress(); _cursor = 0; _dropped = 0; }

    /// <summary>Record one run. <paramref name="bindSharedState"/> binds the shared SDF root signature, viewport
    /// constants, topology, and quad VB; <paramref name="bindPipelineState"/> binds this pipeline's PSO. When both are
    /// false, only the per-run instance SRV offset + draw are recorded. Returns false when the instance buffer is full
    /// and nothing was recorded (the command-list state is then untouched).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, ReadOnlySpan<RectInstance> instances, float vpW, float vpH,
                       bool bindSharedState = true, bool bindPipelineState = true)
    {
        int start = _cursor;
        int count = Math.Min(instances.Length, MaxInstances - start);
        if (count <= 0) { _dropped += instances.Length; return false; }
        _dropped += instances.Length - count;
        for (int i = 0; i < count; i++) _mapped[_active][start + i] = instances[i];
        _cursor += count;

        if (bindSharedState)
        {
            cmd->SetGraphicsRootSignature(_shared.RootSignature);
            _shared.SetViewportConstants(cmd, vpW, vpH);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            var qv = _shared.QuadView;
            cmd->IASetVertexBuffers(0, 1, &qv);
        }
        if (bindPipelineState)
            cmd->SetPipelineState(_pso);
        cmd->SetGraphicsRootShaderResourceView(1, _activeGva + (ulong)(start * sizeof(RectInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return true;
    }

    public void Dispose()
    {
        for (int f = 0; f < FrameCount; f++)
            if (_instances[f] != null) { _instances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances[f], "RoundRect.InstanceUpload"); _instances[f]->Release(); _instances[f] = null; }
        if (_pso != null) _pso->Release();
    }
}
