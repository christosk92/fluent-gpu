using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using ColorF = FluentGpu.Foundation.ColorF;

namespace FluentGpu.Rhi.D3D12;

[StructLayout(LayoutKind.Sequential)]
internal struct GlyphInstance
{
    public float DstX, DstY, DstW, DstH;
    public float U0, V0, U1, V1;
    public float R, G, B, A;
    public float M11, M12, M21, M22, Dx, Dy;   // 2x3 world transform (local→device)
    public float Opacity;
    public float Pad;   // pad to 80 bytes: HLSL rounds structured-buffer stride up to 16 (float4 alignment)
}

internal struct GlyphEntry { public int X, Y, W, H; public float BearingX, BearingY, Advance; }

/// <summary>
/// DirectWrite glyph atlas + textured-quad pipeline (design/subsystems/text.md, gpu-renderer.md DrawGlyphRun). Glyphs are
/// rasterized to a CPU R8 coverage atlas via <c>CreateGlyphRunAnalysis</c>/<c>CreateAlphaTexture</c>, uploaded to a GPU
/// texture, and drawn as quads sampling the atlas (tinted by the run color, gamma-correct grayscale AA). Slice shaping is
/// LTR design-advance positioning over the system "Segoe UI" face; full itemize→BiDi→shape is the spec's follow-up.
/// </summary>
internal sealed unsafe class GlyphRenderer : IDisposable
{
    private const int ATLAS = 1024;

    private IDWriteFactory* _dw;
    private IDWriteFontFace* _faceRegular;
    private IDWriteFontFace* _faceBold;
    private ushort _unitsPerEm;
    private short _ascent, _descent;

    private readonly byte[] _cpu = new byte[ATLAS * ATLAS];
    private ID3D12Resource* _tex;
    private ID3D12Resource* _texUpload;
    private ID3D12DescriptorHeap* _srvHeap;
    private D3D12_GPU_DESCRIPTOR_HANDLE _srvGpu;
    private bool _atlasDirty = true;
    private bool _texInitialized;
    private int _shelfX = 1, _shelfY = 1, _shelfH;

    private readonly Dictionary<long, GlyphEntry> _cache = new();

    private ID3D12RootSignature* _rootSig;
    private ID3D12PipelineState* _pso;
    private ID3D12Resource* _quad;
    private D3D12_VERTEX_BUFFER_VIEW _quadView;
    private ID3D12Resource* _instances;
    private GlyphInstance* _mapped;
    private const int MaxGlyphs = 8192;

    private const string Hlsl = """
struct G { float2 dst; float2 size; float2 uv0; float2 uv1; float4 color; float4 m; float2 t; float opacity; float pad; };
StructuredBuffer<G> gInst : register(t1);
Texture2D gAtlas : register(t0);
SamplerState gSamp : register(s0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 color : TEXCOORD1; float opacity : TEXCOORD2; };

VSOut VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    G g = gInst[iid];
    float2 lp = g.dst + corner * g.size;                        // local-space point
    float2 world = float2(g.m.x * lp.x + g.m.z * lp.y + g.t.x,  // 2x3 affine: local → device
                          g.m.y * lp.x + g.m.w * lp.y + g.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.uv = lerp(g.uv0, g.uv1, corner);
    o.color = g.color;
    o.opacity = g.opacity;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float a = gAtlas.Sample(gSamp, i.uv).r;
    float aOut = i.color.a * a * i.opacity;
    return float4(i.color.rgb * aOut, aOut);   // premultiplied alpha
}
""";

    // diagnostics (surfaced through the standardized Diag facility; stripped on release)
    public int CachedGlyphs => _cache.Count;
    public long AtlasNonZero { get { long n = 0; var c = _cpu; for (int i = 0; i < c.Length; i++) if (c[i] != 0) n++; return n; } }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    public void Init(ID3D12Device* device)
    {
        InitDWrite();
        InitAtlasTexture(device);
        InitPipeline(device);
    }

    private void InitDWrite()
    {
        IDWriteFactory* f;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
        _dw = f;
        _faceRegular = CreateFace(DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _faceBold = CreateFace(DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD);

        DWRITE_FONT_METRICS m;
        _faceRegular->GetMetrics(&m);
        _unitsPerEm = m.designUnitsPerEm;
        _ascent = (short)m.ascent;
        _descent = (short)m.descent;
    }

    private IDWriteFontFace* CreateFace(DWRITE_FONT_WEIGHT weight)
    {
        IDWriteFontCollection* coll;
        Check(_dw->GetSystemFontCollection(&coll, BOOL.FALSE), "GetSystemFontCollection");
        uint index; BOOL exists;
        fixed (char* pn = "Segoe UI")
            Check(coll->FindFamilyName(pn, &index, &exists), "FindFamilyName");
        IDWriteFontFamily* family;
        Check(coll->GetFontFamily(index, &family), "GetFontFamily");
        IDWriteFont* font;
        Check(family->GetFirstMatchingFont(weight, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font), "GetFirstMatchingFont");
        IDWriteFontFace* face;
        Check(font->CreateFontFace(&face), "CreateFontFace");
        font->Release(); family->Release(); coll->Release();
        return face;
    }

    // Rasterize at the PHYSICAL size (size * dpiScale) so glyphs are crisp when drawn into a DIP-sized quad at high DPI.
    private GlyphEntry GetGlyph(char ch, float size, bool bold, float dpiScale)
    {
        IDWriteFontFace* face = bold ? _faceBold : _faceRegular;
        int sizeQ = (int)MathF.Round(size);
        int scaleQ = (int)MathF.Round(dpiScale * 100f);
        long key = ((long)scaleQ << 44) | ((long)(bold ? 1 : 0) << 43) | ((long)sizeQ << 24) | ch;
        if (_cache.TryGetValue(key, out var e)) return e;

        uint cp = ch;
        ushort gi;
        face->GetGlyphIndices(&cp, 1, &gi);

        DWRITE_GLYPH_METRICS gm;
        face->GetDesignGlyphMetrics(&gi, 1, &gm, BOOL.FALSE);
        float advance = gm.advanceWidth * (size / _unitsPerEm);   // advance in DIP (scale-independent)
        float physEm = size * dpiScale;                            // rasterize at physical pixels

        float zeroAdvance = 0f;
        DWRITE_GLYPH_RUN run = default;
        run.fontFace = face;
        run.fontEmSize = physEm;
        run.glyphCount = 1;
        run.glyphIndices = &gi;
        run.glyphAdvances = &zeroAdvance;
        run.glyphOffsets = null;
        run.isSideways = BOOL.FALSE;
        run.bidiLevel = 0;

        IDWriteGlyphRunAnalysis* analysis;
        Check(_dw->CreateGlyphRunAnalysis(&run, 1.0f, null, DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL,
            DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL, 0f, 0f, &analysis), "CreateGlyphRunAnalysis");

        // NATURAL (antialiased) rendering mode → must query the CLEARTYPE_3x1 texture (ALIASED_1x1 returns empty bounds here).
        RECT bounds;
        Check(analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds), "GetAlphaTextureBounds");
        int w = bounds.right - bounds.left, h = bounds.bottom - bounds.top;

        e = new GlyphEntry { Advance = advance, BearingX = bounds.left, BearingY = bounds.top, W = w, H = h };
        Diag.Set("text.glyph", "last", $"ch='{ch}' gi={gi} {w}x{h} adv={advance:0.0}");
        if (w > 0 && h > 0)
        {
            byte[] rgb = new byte[w * h * 3];   // 3 subpixel coverage bytes per pixel
            fixed (byte* pr = rgb)
                Check(analysis->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds, pr, (uint)rgb.Length), "CreateAlphaTexture");
            byte[] gray = new byte[w * h];      // average to grayscale coverage for the R8 atlas
            for (int i = 0; i < w * h; i++) gray[i] = (byte)((rgb[3 * i] + rgb[3 * i + 1] + rgb[3 * i + 2]) / 3);
            Pack(ref e, gray, w, h);
            _atlasDirty = true;
        }
        Diag.Count("text.glyph", "rasterized");
        analysis->Release();
        _cache[key] = e;
        return e;
    }

    private void Pack(ref GlyphEntry e, byte[] src, int w, int h)
    {
        if (_shelfX + w + 1 > ATLAS) { _shelfX = 1; _shelfY += _shelfH + 1; _shelfH = 0; }
        if (_shelfY + h + 1 > ATLAS) return;   // atlas full (slice never hits this)
        e.X = _shelfX; e.Y = _shelfY;
        for (int row = 0; row < h; row++)
            Array.Copy(src, row * w, _cpu, (e.Y + row) * ATLAS + e.X, w);
        _shelfX += w + 1;
        if (h > _shelfH) _shelfH = h;
    }

    /// <summary>Lay out one run (LTR, design advances) into glyph quads in DIP space; rasterizes missing glyphs at physical px.</summary>
    public void LayoutRun(string text, float size, bool bold, float originX, float topY, ColorF color, float dpiScale, Affine2D world, float opacity, List<GlyphInstance> outList)
    {
        float baseline = topY + _ascent * (size / _unitsPerEm);   // top of layout box → baseline (DIP)
        float pen = originX;                                       // DIP
        float inv = 1f / dpiScale;                                 // physical atlas px → DIP
        foreach (char ch in text)
        {
            var g = GetGlyph(ch, size, bold, dpiScale);
            if (g.W > 0 && g.H > 0)
            {
                outList.Add(new GlyphInstance
                {
                    DstX = pen + g.BearingX * inv, DstY = baseline + g.BearingY * inv, DstW = g.W * inv, DstH = g.H * inv,
                    U0 = g.X / (float)ATLAS, V0 = g.Y / (float)ATLAS,
                    U1 = (g.X + g.W) / (float)ATLAS, V1 = (g.Y + g.H) / (float)ATLAS,
                    R = color.R, G = color.G, B = color.B, A = color.A,
                    M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22, Dx = world.Dx, Dy = world.Dy,
                    Opacity = opacity,
                });
            }
            pen += g.Advance;
        }
    }

    // ── GPU resources ─────────────────────────────────────────────────────────
    private void InitAtlasTexture(ID3D12Device* device)
    {
        D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC td = default;
        td.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = ATLAS; td.Height = ATLAS; td.DepthOrArraySize = 1; td.MipLevels = 1;
        td.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM; td.SampleDesc.Count = 1;
        td.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        ID3D12Resource* tex;
        Check(device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&tex), "CreateTexture");
        _tex = tex;

        _texUpload = CreateUpload(device, ATLAS * ATLAS);

        D3D12_DESCRIPTOR_HEAP_DESC hd = default;
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = 1;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        ID3D12DescriptorHeap* heap;
        Check(device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "CreateDescriptorHeap(SRV)");
        _srvHeap = heap;

        D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
        sd.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM;
        sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
        sd.Shader4ComponentMapping = 0x1688;   // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
        sd.Anonymous.Texture2D.MipLevels = 1;
        device->CreateShaderResourceView(_tex, &sd, _srvHeap->GetCPUDescriptorHandleForHeapStart());
        _srvGpu = _srvHeap->GetGPUDescriptorHandleForHeapStart();
    }

    public void UploadIfDirty(ID3D12GraphicsCommandList* cmd)
    {
        if (!_atlasDirty && _texInitialized) return;

        void* p; _texUpload->Map(0, null, &p);
        fixed (byte* src = _cpu) Buffer.MemoryCopy(src, p, ATLAS * ATLAS, ATLAS * ATLAS);
        _texUpload->Unmap(0, null);

        if (_texInitialized) Transition(cmd, _tex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);

        D3D12_TEXTURE_COPY_LOCATION dst = default;
        dst.pResource = _tex; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.Anonymous.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION srcLoc = default;
        srcLoc.pResource = _texUpload; srcLoc.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        srcLoc.Anonymous.PlacedFootprint.Offset = 0;
        srcLoc.Anonymous.PlacedFootprint.Footprint.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM;
        srcLoc.Anonymous.PlacedFootprint.Footprint.Width = ATLAS;
        srcLoc.Anonymous.PlacedFootprint.Footprint.Height = ATLAS;
        srcLoc.Anonymous.PlacedFootprint.Footprint.Depth = 1;
        srcLoc.Anonymous.PlacedFootprint.Footprint.RowPitch = ATLAS;   // R8, 1024 — already 256-aligned
        cmd->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, null);

        Transition(cmd, _tex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        _texInitialized = true;
        _atlasDirty = false;
    }

    private static void Transition(ID3D12GraphicsCommandList* cmd, ID3D12Resource* res, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b = default;
        b.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Anonymous.Transition.pResource = res;
        b.Anonymous.Transition.StateBefore = before;
        b.Anonymous.Transition.StateAfter = after;
        b.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        cmd->ResourceBarrier(1, &b);
    }

    private void InitPipeline(ID3D12Device* device)
    {
        // root: [0] constants b0, [1] table (SRV t0 = atlas), [2] root SRV t1 = instances; static sampler s0
        D3D12_DESCRIPTOR_RANGE range = default;
        range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        range.NumDescriptors = 1; range.BaseShaderRegister = 0; range.RegisterSpace = 0;
        range.OffsetInDescriptorsFromTableStart = 0xFFFFFFFF;

        D3D12_ROOT_PARAMETER* p = stackalloc D3D12_ROOT_PARAMETER[3];
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        p[0].Anonymous.Constants.ShaderRegister = 0; p[0].Anonymous.Constants.Num32BitValues = 2;
        p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        p[1].Anonymous.DescriptorTable.NumDescriptorRanges = 1;
        p[1].Anonymous.DescriptorTable.pDescriptorRanges = &range;
        p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;
        p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV;
        p[2].Anonymous.Descriptor.ShaderRegister = 1;
        p[2].ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX;

        D3D12_STATIC_SAMPLER_DESC samp = default;
        samp.Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        samp.AddressU = samp.AddressV = samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.ShaderRegister = 0; samp.RegisterSpace = 0;
        samp.ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_ROOT_SIGNATURE_DESC rs = default;
        rs.NumParameters = 3; rs.pParameters = p;
        rs.NumStaticSamplers = 1; rs.pStaticSamplers = &samp;
        rs.Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

        ID3DBlob* sig = null; ID3DBlob* err = null;
        Check(D3D12SerializeRootSignature(&rs, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err), "SerializeRootSignature(glyph)");
        ID3D12RootSignature* root;
        Check(device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), __uuidof<ID3D12RootSignature>(), (void**)&root), "CreateRootSignature(glyph)");
        _rootSig = root; sig->Release(); if (err != null) err->Release();

        ID3DBlob* vs = ShaderCompiler.Compile(Hlsl, "VSMain", "vs_5_1");
        ID3DBlob* ps = ShaderCompiler.Compile(Hlsl, "PSMain", "ps_5_1");
        byte[] semantic = Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        {
            D3D12_INPUT_ELEMENT_DESC elem = default;
            elem.SemanticName = (sbyte*)sem; elem.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT;
            elem.InputSlotClass = D3D12_INPUT_CLASSIFICATION.D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA;

            D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = default;
            pd.pRootSignature = _rootSig;
            pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() };
            pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() };
            pd.InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = &elem, NumElements = 1 };
            pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            pd.NumRenderTargets = 1; pd.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            pd.SampleDesc.Count = 1; pd.SampleMask = uint.MaxValue;
            pd.RasterizerState.FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID;
            pd.RasterizerState.CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE;
            pd.BlendState.RenderTarget[0].BlendEnable = BOOL.TRUE;
            pd.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE;   // premultiplied
            pd.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE;
            pd.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            pd.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD;
            pd.BlendState.RenderTarget[0].RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL;
            pd.DepthStencilState.DepthEnable = BOOL.FALSE;
            ID3D12PipelineState* pso;
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&pso), "CreateGraphicsPipelineState(glyph)");
            _pso = pso;
        }
        vs->Release(); ps->Release();

        float* quad = stackalloc float[8] { 0, 0, 1, 0, 0, 1, 1, 1 };
        _quad = CreateUpload(device, sizeof(float) * 8);
        void* qp; _quad->Map(0, null, &qp); Buffer.MemoryCopy(quad, qp, 32, 32); _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW { BufferLocation = _quad->GetGPUVirtualAddress(), SizeInBytes = 32, StrideInBytes = 8 };

        _instances = CreateUpload(device, (uint)(sizeof(GlyphInstance) * MaxGlyphs));
        void* ip; _instances->Map(0, null, &ip); _mapped = (GlyphInstance*)ip;
    }

    public void Record(ID3D12GraphicsCommandList* cmd, List<GlyphInstance> instances, float vpW, float vpH)
    {
        int count = Math.Min(instances.Count, MaxGlyphs);
        if (count == 0) return;
        for (int i = 0; i < count; i++) _mapped[i] = instances[i];

        ID3D12DescriptorHeap* heap = _srvHeap;
        cmd->SetDescriptorHeaps(1, &heap);
        cmd->SetGraphicsRootSignature(_rootSig);
        cmd->SetPipelineState(_pso);
        float* vp = stackalloc float[2] { vpW, vpH };
        cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
        cmd->SetGraphicsRootDescriptorTable(1, _srvGpu);
        cmd->SetGraphicsRootShaderResourceView(2, _instances->GetGPUVirtualAddress());
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView) cmd->IASetVertexBuffers(0, 1, qv);
        cmd->DrawInstanced(4, (uint)count, 0, 0);
    }

    private static ID3D12Resource* CreateUpload(ID3D12Device* device, uint bytes)
    {
        D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd = default;
        rd.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes; rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN; rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* res;
        Check(device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ, null, __uuidof<ID3D12Resource>(), (void**)&res), "CreateCommittedResource");
        return res;
    }

    public void Dispose()
    {
        if (_instances != null) { _instances->Unmap(0, null); _instances->Release(); }
        if (_quad != null) _quad->Release();
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
        if (_srvHeap != null) _srvHeap->Release();
        if (_texUpload != null) _texUpload->Release();
        if (_tex != null) _tex->Release();
        if (_faceBold != null) _faceBold->Release();
        if (_faceRegular != null) _faceRegular->Release();
        if (_dw != null) _dw->Release();
    }
}
