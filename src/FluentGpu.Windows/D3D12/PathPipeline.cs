using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Rhi.D3D12;

/// <summary>Per-draw uniform record for one <c>FillPath</c>/<c>StrokePath</c> (gpu-renderer.md §5), riding the SAME
/// shared root-signature SRV slot (t0) every other SDF pipeline uses (see <see cref="SdfSharedResources"/>). Layout
/// mirrors the HLSL <c>PathInst</c> struct field-for-field (natural StructuredBuffer packing — the exact convention
/// already proven by <see cref="PolylineStrokeInstance"/>/its HLSL <c>Inst</c>): color(4) + m(4) + t(2) + opacity(1)
/// + arcLenPx(1) + trimStart/trimEnd/dashOn/dashOff(4) = 16 floats, 64 bytes. Fills carry TrimStart=0, TrimEnd=1,
/// DashOn=0 — the full-cover window — so ONE shader/PSO serves both opcodes (see <see cref="Hlsl"/>); trim/dash are
/// PER-DRAW UNIFORMS here, never geometry, which is what lets a 60 Hz stroke-trim/dash animation stay a cache hit
/// against <see cref="PathRealizationCache"/> with zero re-tessellation.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PathInstance
{
    public float R, G, B, A;
    public float M11, M12, M21, M22;
    public float Dx, Dy, Opacity, ArcLenPx;
    public float TrimStart, TrimEnd, DashOn, DashOff;
}

/// <summary>One decoded <c>FillPath</c>/<c>StrokePath</c> draw: the <see cref="PathRealizationCache.Shared"/> slab
/// range to resolve/upload (deduped per frame — see <see cref="PathPipeline"/>) plus this draw's uniform record.</summary>
internal struct PathDrawItem
{
    public int VtxStart, VtxCount, IdxStart, IdxCount;
    public PathInstance Inst;
}

/// <summary>
/// The D3D12 TRIANGLELIST lane for tessellated path fills/strokes (gpu-renderer.md §5, BUILD-ROADMAP §1.5) — the
/// first real vertex+index buffer this backend has ever bound (every sibling SDF pipeline instances one shared unit
/// quad as a TRIANGLESTRIP; every image draws the same shared quad through a heap-bound SRV). Geometry comes from
/// <see cref="PathRealizationCache.Shared"/>'s retained slab (tessellated once, off the frame hot path); this
/// pipeline's only per-frame job is (a) copy each DISTINCT realization actually drawn into this frame's own
/// GPU-visible VB/IB exactly once (deduped by a fixed-capacity, open-addressed map reset every <see cref="BeginFrame"/>
/// — zero managed allocation), and (b) issue one <c>DrawIndexedInstanced</c> per path, offsetting the shared root
/// SRV (t0) to that draw's <see cref="PathInstance"/> record (the same GVA-offset mechanism every SDF pipeline uses
/// for its instance bank).
///
/// <para><b>Deliberately v1-simple:</b> two <see cref="FrameCount"/>-deep, persistently-mapped UPLOAD buffers (VB +
/// IB) sized for a fixed worst case, plus the SRV instance bank, all following
/// <see cref="PolylineStrokePipeline.BuildBuffers"/>'s double-buffer-by-frame-index + <c>_dropped</c>-on-overflow
/// discipline verbatim — never a resize mid-frame. The GPU-resident-slab + per-vertex-PathIdx follow-up (one
/// <c>DrawIndexed</c> per RUN instead of per path) is intentionally descoped; <see cref="DrawsThisFrame"/> /
/// <see cref="UploadBytesThisFrame"/> exist so that decision is data-driven.</para>
///
/// <para><b>The one non-obvious trap (read before touching the <c>RecordAll</c> call site):</b> this pipeline
/// REBINDS the vertex buffer, the index buffer, AND the primitive topology (TRIANGLELIST, not the shared
/// TRIANGLESTRIP). It must therefore be entered/exited via an explicit <see cref="Begin"/> — the SAME pattern
/// <c>D3D12Device.RecordAll</c> uses for <c>PrimKind.Image</c> — and the call site MUST clear
/// <c>_sharedSdfStateBound</c> after binding, never route through <c>NoteSdfPipeBind</c>. If that flag is left
/// <c>true</c>, the NEXT Rect/Arc/Polyline/Gradient run sees "shared SDF state already bound" and skips rebinding
/// the shared quad VB + TRIANGLESTRIP topology — it then draws with THIS pipeline's VB/IB/topology still bound.
/// That is silent, intermittent visual corruption, and no headless gate can catch it (there are no GPU pixels in
/// the VerticalSlice harness) — only a real device with real pixels will ever show it.</para>
/// </summary>
internal sealed unsafe class PathPipeline : IDisposable
{
    private const int FrameCount = 2;      // double-buffered per frame-in-flight, same rationale as every sibling pipe
    private const int MaxVertices = 16384; // 16 B/vtx ⇒ 256 KiB/frame worst case (a "few thousand vertices" per hero × headroom)
    private const int MaxIndices = 32768;  // 4 B/idx ⇒ 128 KiB/frame worst case
    private const int MaxDraws = 512;      // instance records per frame (~40 draws/dialog per the design brief × headroom)
    private const int MapCapacity = 1024;  // power-of-two > MaxDraws so the fixed open-addressed dedupe map never fills at max distinct draws

    private SdfSharedResources _shared = null!;
    private ID3D12PipelineState* _pso;

    private readonly ID3D12Resource*[] _vb = new ID3D12Resource*[FrameCount];
    private readonly PathVertex*[] _vbMapped = new PathVertex*[FrameCount];
    private readonly D3D12_VERTEX_BUFFER_VIEW[] _vbView = new D3D12_VERTEX_BUFFER_VIEW[FrameCount];

    private readonly ID3D12Resource*[] _ib = new ID3D12Resource*[FrameCount];
    private readonly uint*[] _ibMapped = new uint*[FrameCount];
    private readonly D3D12_INDEX_BUFFER_VIEW[] _ibView = new D3D12_INDEX_BUFFER_VIEW[FrameCount];

    private readonly ID3D12Resource*[] _inst = new ID3D12Resource*[FrameCount];
    private readonly PathInstance*[] _instMapped = new PathInstance*[FrameCount];

    private int _active;
    private ulong _instGva;
    private int _vtxCursor, _idxCursor, _drawCursor;
    private int _dropped;
    private long _uploadBytes;

    // Fixed-capacity, open-addressed, per-frame dedupe map: (VtxStart,VtxCount,IdxStart,IdxCount) — the realization's
    // identity in PathRealizationCache.Shared's slab (RealizationId is reserved/always-0 today; see PathPipeline's use
    // of this tuple as the dedupe key) -> (VtxBase,IdxBase) already uploaded into THIS frame's VB/IB. Reset in place
    // every BeginFrame (Array.Clear over the same backing array) — never reallocated, so this stays zero-alloc.
    private struct MapSlot { public bool Occupied; public int VtxStart, VtxCount, IdxStart, IdxCount, VtxBase, IdxBase; }
    private readonly MapSlot[] _map = new MapSlot[MapCapacity];
    private int _mapCount;

    public int DroppedInstances => _dropped;
    /// <summary>Paths actually drawn this frame (one DrawIndexedInstanced each) — feeds <c>Diag.Set("path","draws",…)</c>.</summary>
    public int DrawsThisFrame => _drawCursor;
    /// <summary>Bytes memcpy'd from <see cref="PathRealizationCache.Shared"/> into this frame's VB/IB (post-dedupe) —
    /// feeds <c>Diag.Set("path","uploadBytes",…)</c>, the data the GPU-resident-slab follow-up would be sized against.</summary>
    public long UploadBytesThisFrame => _uploadBytes;

    // VS bakes the Affine2D exactly like PolylineStrokePipeline; PS applies arc-length TRIM then DASH as per-frame
    // UNIFORMS (never geometry — see PathInstance's doc), then the tessellated AA fringe (Cov) is the only
    // anti-aliasing (MSAA is off everywhere in this renderer). Fills pass trimStart=0/trimEnd=1/dashOn=0 so this one
    // shader/PSO serves DrawOp.FillPath and DrawOp.StrokePath both.
    private const string Hlsl = """
struct PathInst {
    float4 color;
    float4 m;
    float2 t; float opacity; float arcLenPx;
    float trimStart; float trimEnd; float dashOn; float dashOff;
};
StructuredBuffer<PathInst> gInst : register(t0);
cbuffer Root : register(b0) { float2 gViewport; };

struct VSOut {
    float4 pos : SV_Position;
    float4 color : TEXCOORD0;
    float2 covS : TEXCOORD1;
    float4 trimDash : TEXCOORD2;    // trimStart, trimEnd, dashOn, dashOff
    float2 opacityArc : TEXCOORD3;  // opacity, arcLenPx
};

VSOut VSMain(float2 posLocal : POSITION, float2 covS : TEXCOORD0, uint iid : SV_InstanceID)
{
    PathInst it = gInst[iid];
    float2 world = float2(it.m.x * posLocal.x + it.m.z * posLocal.y + it.t.x,
                           it.m.y * posLocal.x + it.m.w * posLocal.y + it.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.color = it.color;
    o.covS = covS;
    o.trimDash = float4(it.trimStart, it.trimEnd, it.dashOn, it.dashOff);
    o.opacityArc = float2(it.opacity, it.arcLenPx);
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float s = i.covS.y;
    // Arc-length trim: a per-frame UNIFORM window over the baked normalized contour position — never re-tessellated.
    if (s < i.trimDash.x || s > i.trimDash.y) discard;
    float dashOn = i.trimDash.z;
    if (dashOn > 0.0)
    {
        float per = dashOn + i.trimDash.w;
        if (frac(s * i.opacityArc.y / per) * per > dashOn) discard;
    }
    // The tessellated AA fringe (Cov, 0 outer -> 1 inside) IS the anti-aliasing — no fwidth, no MSAA (SampleDesc.Count=1).
    float a = i.color.a * i.opacityArc.x * saturate(i.covS.x);
    return float4(i.color.rgb * a, a);
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
        => ShaderCompiler.Compile(Hlsl, entry, target, "path");

    private void BuildPipeline(ID3D12Device* device)
    {
        ID3DBlob* vs = Compile("VSMain", "vs_5_1");
        ID3DBlob* ps = Compile("PSMain", "ps_5_1");
        byte[] posSem = Encoding.ASCII.GetBytes("POSITION\0");
        byte[] texSem = Encoding.ASCII.GetBytes("TEXCOORD\0");
        fixed (byte* pos = posSem)
        fixed (byte* tex = texSem)
        {
            D3D12_INPUT_ELEMENT_DESC* elems = stackalloc D3D12_INPUT_ELEMENT_DESC[2];
            elems[0] = default;
            elems[0].SemanticName = (sbyte*)pos;
            elems[0].SemanticIndex = 0;
            elems[0].Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
            elems[0].InputSlot = 0;
            elems[0].AlignedByteOffset = 0;
            elems[0].InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;
            elems[1] = default;
            elems[1].SemanticName = (sbyte*)tex;
            elems[1].SemanticIndex = 0;
            elems[1].Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
            elems[1].InputSlot = 0;
            elems[1].AlignedByteOffset = 8;   // PathVertex { X, Y, Cov, S } — Cov/S start after the 8-byte XY
            elems[1].InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;

            D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
            pd.pRootSignature = _shared.RootSignature;
            pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
            pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
            pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = elems, NumElements = 2 };
            pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            pd.NumRenderTargets = 1;
            pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            pd.SampleDesc.Count = 1;   // MSAA off — this renderer is single-sample everywhere; the tessellated Cov fringe is the AA
            pd.SampleMask = uint.MaxValue;
            pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
            pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;   // winding is arbitrary (PathSweep/PathStroker make no front-face guarantee)
            pd.RasterizerState.DepthClipEnable = BOOL.TRUE;
            pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
            pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;   // premultiplied SrcOver, like every sibling pipeline
            pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
            pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
            pd.DepthStencilState.DepthEnable = BOOL.FALSE;
            pd.DepthStencilState.StencilEnable = BOOL.FALSE;

            ID3D12PipelineState* pso;
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "Path.CreateGraphicsPipelineState");
            _pso = pso;
        }
        vs->Release();
        ps->Release();
    }

    private void BuildBuffers(ID3D12Device* device)
    {
        for (int f = 0; f < FrameCount; f++)
        {
            uint vbBytes = (uint)(sizeof(PathVertex) * MaxVertices);
            _vb[f] = CreateUpload(device, vbBytes, "Path.VertexUpload");
            void* vp; _vb[f]->Map(0, null, &vp);
            _vbMapped[f] = (PathVertex*)vp;   // persistently mapped
            _vbView[f] = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = _vb[f]->GetGPUVirtualAddress(),
                SizeInBytes = vbBytes,
                StrideInBytes = (uint)sizeof(PathVertex),
            };

            uint ibBytes = (uint)(sizeof(uint) * MaxIndices);
            _ib[f] = CreateUpload(device, ibBytes, "Path.IndexUpload");
            void* ip; _ib[f]->Map(0, null, &ip);
            _ibMapped[f] = (uint*)ip;
            _ibView[f] = new D3D12_INDEX_BUFFER_VIEW
            {
                BufferLocation = _ib[f]->GetGPUVirtualAddress(),
                SizeInBytes = ibBytes,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
            };

            uint instBytes = (uint)(sizeof(PathInstance) * MaxDraws);
            _inst[f] = CreateUpload(device, instBytes, "Path.InstanceUpload");
            void* instp; _inst[f]->Map(0, null, &instp);
            _instMapped[f] = (PathInstance*)instp;
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
        ID3D12Resource* res;
        Check(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "Path.CreateCommittedResource");
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    /// <summary>Select this frame's VB/IB/instance bank (by back-buffer index — the same FrameCount-deep rotation
    /// every sibling pipe uses, so writing this bank can never race a still-in-flight GPU read of frame N-1) and
    /// reset every per-frame cursor, INCLUDING the dedupe map (cleared in place — no reallocation, no growth).</summary>
    public void BeginFrame(int frameIndex)
    {
        _active = ((frameIndex % FrameCount) + FrameCount) % FrameCount;
        _instGva = _inst[_active]->GetGPUVirtualAddress();
        _vtxCursor = 0; _idxCursor = 0; _drawCursor = 0; _dropped = 0; _uploadBytes = 0;
        Array.Clear(_map, 0, MapCapacity);
        _mapCount = 0;
    }

    /// <summary>Enter the Path lane: bind the shared root signature (verbatim — same b0/t0 layout every SDF pipe
    /// uses), this pipeline's own PSO, TRIANGLELIST topology, and THIS FRAME's real vertex+index buffers. Callers
    /// MUST treat this exactly like <c>ImagePipeline.Begin</c> — an explicit, un-deduped rebind whenever the bound
    /// pipe transitions into Path — and must clear <c>_sharedSdfStateBound</c> afterward (see this type's doc for
    /// why <c>NoteSdfPipeBind</c> would be wrong here).</summary>
    public void Begin(ID3D12GraphicsCommandList* cmd, float vpW, float vpH)
    {
        cmd->SetGraphicsRootSignature(_shared.RootSignature);
        _shared.SetViewportConstants(cmd, vpW, vpH);
        cmd->SetPipelineState(_pso);
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        var vbv = _vbView[_active];
        cmd->IASetVertexBuffers(0, 1, &vbv);
        var ibv = _ibView[_active];
        cmd->IASetIndexBuffer(&ibv);
    }

    /// <summary>Record one path draw: resolve (or upload, on a per-frame dedupe MISS) its geometry range from
    /// <see cref="PathRealizationCache.Shared"/> into this frame's VB/IB, append its uniform record, and issue one
    /// <c>DrawIndexedInstanced</c>. Returns false (nothing recorded, <see cref="DroppedInstances"/>++) when the
    /// fixed-capacity instance bank, vertex bank, index bank, or dedupe map is full — never resizes mid-frame.
    /// An empty realization (VtxCount/IdxCount == 0, e.g. a not-yet-tessellated or degenerate path) is a legitimate
    /// no-op, not a drop.</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, in PathDrawItem item)
    {
        if (item.VtxCount <= 0 || item.IdxCount <= 0) return true;

        if (_drawCursor >= MaxDraws) { _dropped++; return false; }
        if (!TryResolveOrUpload(item.VtxStart, item.VtxCount, item.IdxStart, item.IdxCount, out int vtxBase, out int idxBase))
        {
            _dropped++;
            return false;
        }

        int slot = _drawCursor++;
        _instMapped[_active][slot] = item.Inst;
        cmd->SetGraphicsRootShaderResourceView(1, _instGva + (ulong)(slot * sizeof(PathInstance)));
        // BaseVertexLocation (vtxBase) is added by the GPU to every index it fetches from the IB — the tessellator's
        // index values are LOCAL/0-based into their own vertex range (see PathSweep/PathStroker), so the SAME index
        // bytes copied verbatim from PathRealizationCache.Shared.Indices resolve correctly once offset by vtxBase.
        cmd->DrawIndexedInstanced((uint)item.IdxCount, 1, (uint)idxBase, vtxBase, 0);
        return true;
    }

    // Resolve (VtxStart,VtxCount,IdxStart,IdxCount) — the realization's identity in the retained slab — against this
    // frame's dedupe map; upload once into the tail of this frame's VB/IB on a genuine miss. Fixed capacity: a full
    // map/VB/IB never grows mid-frame, it just fails (caller drops the draw and counts it).
    private bool TryResolveOrUpload(int vtxStart, int vtxCount, int idxStart, int idxCount, out int vtxBase, out int idxBase)
    {
        int mask = MapCapacity - 1;
        int hash = HashKey(vtxStart, vtxCount, idxStart, idxCount) & mask;
        int i = hash, start = i;
        while (_map[i].Occupied)
        {
            if (_map[i].VtxStart == vtxStart && _map[i].VtxCount == vtxCount &&
                _map[i].IdxStart == idxStart && _map[i].IdxCount == idxCount)
            {
                vtxBase = _map[i].VtxBase; idxBase = _map[i].IdxBase;
                return true;
            }
            i = (i + 1) & mask;
            if (i == start) { vtxBase = 0; idxBase = 0; return false; }   // map full (linear-probe wrapped) — fixed capacity, never grown
        }

        // MISS. Keep the map's load factor <= ~75% (mirrors PathRealizationCache.Insert's own rule) and require both
        // the vertex and index banks to have room — never a partial upload.
        if ((_mapCount + 1) * 4 >= MapCapacity * 3 ||
            vtxCount > MaxVertices - _vtxCursor || idxCount > MaxIndices - _idxCursor)
        {
            vtxBase = 0; idxBase = 0;
            return false;
        }

        var srcVtx = PathRealizationCache.Shared.Vertices.Slice(vtxStart, vtxCount);
        var srcIdx = PathRealizationCache.Shared.Indices.Slice(idxStart, idxCount);
        srcVtx.CopyTo(new Span<PathVertex>(_vbMapped[_active] + _vtxCursor, vtxCount));
        srcIdx.CopyTo(new Span<uint>(_ibMapped[_active] + _idxCursor, idxCount));

        vtxBase = _vtxCursor; idxBase = _idxCursor;
        _uploadBytes += (long)vtxCount * sizeof(PathVertex) + (long)idxCount * sizeof(uint);
        _vtxCursor += vtxCount; _idxCursor += idxCount;

        _map[i] = new MapSlot { Occupied = true, VtxStart = vtxStart, VtxCount = vtxCount, IdxStart = idxStart, IdxCount = idxCount, VtxBase = vtxBase, IdxBase = idxBase };
        _mapCount++;
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int HashKey(int a, int b, int c, int d)
    {
        unchecked
        {
            int h = a;
            h = h * 397 ^ b;
            h = h * 397 ^ c;
            h = h * 397 ^ d;
            return h & 0x7FFFFFFF;
        }
    }

    public void Dispose()
    {
        for (int f = 0; f < FrameCount; f++)
        {
            if (_vb[f] != null) { _vb[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_vb[f], "Path.VertexUpload"); _vb[f]->Release(); _vb[f] = null; }
            if (_ib[f] != null) { _ib[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_ib[f], "Path.IndexUpload"); _ib[f]->Release(); _ib[f] = null; }
            if (_inst[f] != null) { _inst[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_inst[f], "Path.InstanceUpload"); _inst[f]->Release(); _inst[f] = null; }
        }
        if (_pso != null) _pso->Release();
    }
}
