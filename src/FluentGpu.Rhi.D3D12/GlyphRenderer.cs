using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Text;
using FluentGpu.Text.DirectWrite;
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
internal struct FaceMetrics { public ushort Em; public short Asc, Desc; }
/// <summary>Glyph-atlas cache key. <paramref name="Fam"/> is a family id (codepoint path) OR a face id (glyph-id path);
/// <paramref name="Ch"/> is a codepoint OR a glyph id. <paramref name="ByGid"/> keeps those two key spaces disjoint so a
/// codepoint entry and a glyph-id entry can never alias (the bug that smeared shaped + per-char glyphs together). Value type → no alloc.</summary>
internal readonly record struct GlyphKey(int Fam, int Size, int Scale, bool Bold, int Ch, bool ByGid);

/// <summary>One baked glyph quad in LOCAL (DIP) space — color/transform/opacity are applied per-frame at replay, NOT baked
/// here, so the same shaped run is reusable across scroll/theme/fade. The atlas UVs are stable (the shelf packer never
/// repacks), so a cached quad stays valid for the life of the run.</summary>
internal struct ShapedGlyph { public float DstX, DstY, DstW, DstH, U0, V0, U1, V1; }

/// <summary>A fully shaped text run cached by content (see <see cref="RunKey"/>): the local-space quads + an LRU stamp.
/// Allocated only on a cache miss (content change); replayed allocation-free on every steady-state frame.</summary>
internal struct ShapedRun { public ShapedGlyph[] Glyphs; public int Count; public int LastUsedFrame; }

/// <summary>Content key for the shaped-run cache. Keyed on the interned <see cref="StringId"/> handles (stable across frames,
/// no per-frame string hashing) + quantized layout inputs. Excludes color/transform/opacity (replayed). Bounds origin and
/// width are layout-stable for a given element (scroll rides the world transform, not the bounds), so they can key safely.</summary>
internal readonly record struct RunKey(int TextId, int FamId, int SizeQ, int Bold, int Wrap, int Trim, int MaxLines, int WidthQ, int OriginXQ, int OriginYQ, int ScaleQ);

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
    private const string DefaultFamily = "Segoe UI";
    // Per-(family, weight) faces + metrics, resolved on demand. Family = a system name OR "path.ttf#Family Name".
    private readonly Dictionary<(string fam, bool bold), nint> _faces = new();
    private readonly Dictionary<(string fam, bool bold), FaceMetrics> _faceMetrics = new();
    private readonly Dictionary<string, int> _famIds = new();

    private readonly byte[] _cpu = new byte[ATLAS * ATLAS];
    private ID3D12Resource* _tex;
    private ID3D12Resource* _texUpload;
    private ID3D12DescriptorHeap* _srvHeap;
    private D3D12_GPU_DESCRIPTOR_HANDLE _srvGpu;
    private bool _atlasDirty = true;
    private long _atlasNonZero;
    private bool _texInitialized;
    private int _shelfX = 1, _shelfY = 1, _shelfH;

    private readonly Dictionary<GlyphKey, GlyphEntry> _cache = new();
    // Itemize→shape→wrap layout (shared logic with the measure path, so render and measure layout identically).
    private TextLayoutEngine _engine = null!;
    private readonly Dictionary<nint, int> _faceIds = new();   // IDWriteFontFace* → small int for the glyph-cache key

    // Shaped-run cache: unchanged text runs replay their baked local-space quads instead of re-shaping every frame
    // (kills the per-glyph GetGlyph/Dictionary.TryGetValue/DirectWrite storm). Keyed on interned StringId handles, so a
    // hit needs neither a string hash nor a face/family lookup. See LayoutRun.
    private readonly Dictionary<RunKey, ShapedRun> _runCache = new();
    private readonly List<ShapedGlyph> _scratch = new(256);   // reused miss-path shaping buffer
    private readonly List<RunKey> _evictScratch = new();      // reused eviction sweep buffer (off the hot path)
    // Renderer-owned quad-array free-list (bucketed by pow2 size). A virtualization storm shapes thousands of fresh
    // runs whose arrays the cache holds for many frames — the SHARED ArrayPool drains and falls back to allocating;
    // this list retains every returned array, so steady-state churn reuses instead of allocating.
    private readonly Stack<ShapedGlyph[]>[] _quadPool = new Stack<ShapedGlyph[]>[14];   // buckets 1<<0 .. 1<<13
    // Liveness source for the run cache: a reclaimed text id resolves to "" (StringTable), so its runs can never be
    // hit again — evict them promptly instead of waiting out the age backstop (keeps the free-list small under storms).
    private StringTable? _liveness;
    private int _frame;
    private int _runsCached, _runsShaped;                     // per-frame diagnostics
#if DEBUG
    /// <summary>When set, every cache hit re-shapes and asserts the geometry matches — verifies the cache is output-identical
    /// to the uncached path. Off by default (it defeats the perf win); flip on (e.g. via the debugger) for a verification run.</summary>
#pragma warning disable CS0649 // assigned at runtime via the debugger for a verification pass
    internal static bool VerifyCache;
#pragma warning restore CS0649
#endif

    private ID3D12RootSignature* _rootSig;
    private ID3D12PipelineState* _pso;
    private ID3D12Resource* _quad;
    private D3D12_VERTEX_BUFFER_VIEW _quadView;
    private ID3D12Resource* _instances;
    private GlyphInstance* _mapped;
    private const int MaxGlyphs = 8192;
    private int _cursor;

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
    float a = gAtlas.Sample(gSamp, i.uv).r;   // grayscale coverage, used directly (no gamma boost — that thickened all text)
    float aOut = i.color.a * a * i.opacity;
    return float4(i.color.rgb * aOut, aOut);   // premultiplied alpha
}
""";

    // diagnostics (surfaced through the standardized Diag facility; stripped on release)
    public int CachedGlyphs => _cache.Count;
    public int CachedRuns => _runCache.Count;
    public int RunsCached => _runsCached;
    public int RunsShaped => _runsShaped;
    public long AtlasNonZero => _atlasNonZero;

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    public void Init(ID3D12Device* device)
    {
        InitDWrite();
        _engine = new TextLayoutEngine();
        InitAtlasTexture(device);
        InitPipeline(device);
    }

    private void InitDWrite()
    {
        IDWriteFactory* f;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
        _dw = f;
        // warm the default face so the atlas has metrics from frame 1
        ResolveFace(DefaultFamily, false, out _, out _, out _);
    }

    // A small per-family integer used in the glyph-cache key (so the same codepoint in two fonts caches separately).
    private int FamilyId(string family)
    {
        if (string.IsNullOrEmpty(family)) family = DefaultFamily;
        if (_famIds.TryGetValue(family, out int id)) return id;
        id = _famIds.Count + 1; _famIds[family] = id; return id;
    }

    /// <summary>Resolve (and cache) a font face for a family + weight. Family is a system name ("Segoe UI",
    /// "Segoe Fluent Icons") or a custom file "path.ttf#Family Name" (the WinUI syntax). Falls back to the default on error.</summary>
    private IDWriteFontFace* ResolveFace(string family, bool bold, out ushort em, out short asc, out short desc)
    {
        if (string.IsNullOrEmpty(family)) family = DefaultFamily;
        var key = (family, bold);
        if (_faces.TryGetValue(key, out var cached))
        {
            var m0 = _faceMetrics[key]; em = m0.Em; asc = m0.Asc; desc = m0.Desc;
            return (IDWriteFontFace*)cached;
        }

        IDWriteFontFace* face;
        try { face = CreateFaceFor(family, bold); }
        catch { face = family == DefaultFamily ? null : CreateFaceFor(DefaultFamily, bold); }
        if (face == null) { em = 2048; asc = 1500; desc = 500; return null; }

        DWRITE_FONT_METRICS m; face->GetMetrics(&m);
        var fm = new FaceMetrics { Em = m.designUnitsPerEm, Asc = (short)m.ascent, Desc = (short)m.descent };
        _faces[key] = (nint)face; _faceMetrics[key] = fm;
        em = fm.Em; asc = fm.Asc; desc = fm.Desc;
        return face;
    }

    private IDWriteFontFace* CreateFaceFor(string family, bool bold)
    {
        int hash = family.IndexOf('#');
        string path = hash >= 0 ? family.Substring(0, hash) : family;
        bool isFile = path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
        return isFile ? CreateFaceFromFile(path, bold) : CreateSystemFace(family, bold);
    }

    private IDWriteFontFace* CreateSystemFace(string familyName, bool bold)
    {
        IDWriteFontCollection* coll;
        Check(_dw->GetSystemFontCollection(&coll, BOOL.FALSE), "GetSystemFontCollection");
        uint index; BOOL exists;
        fixed (char* pn = familyName) Check(coll->FindFamilyName(pn, &index, &exists), "FindFamilyName");
        if (!exists)   // unknown family → fall back to the default
        {
            coll->Release();
            return familyName == DefaultFamily ? null : CreateSystemFace(DefaultFamily, bold);
        }
        IDWriteFontFamily* family;
        Check(coll->GetFontFamily(index, &family), "GetFontFamily");
        IDWriteFont* font;
        var weight = bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL;
        Check(family->GetFirstMatchingFont(weight, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font), "GetFirstMatchingFont");
        IDWriteFontFace* face;
        Check(font->CreateFontFace(&face), "CreateFontFace");
        font->Release(); family->Release(); coll->Release();
        return face;
    }

    // Load a face directly from a .ttf/.otf file (custom icon fonts). Bold is synthesized when requested.
    private IDWriteFontFace* CreateFaceFromFile(string path, bool bold)
    {
        IDWriteFontFile* file;
        fixed (char* p = path) Check(_dw->CreateFontFileReference(p, null, &file), "CreateFontFileReference");
        BOOL supported; DWRITE_FONT_FILE_TYPE ft; DWRITE_FONT_FACE_TYPE faceType; uint numFaces;
        Check(file->Analyze(&supported, &ft, &faceType, &numFaces), "Analyze(font)");
        IDWriteFontFile** files = &file;
        var sim = bold ? DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD : DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE;
        IDWriteFontFace* face;
        Check(_dw->CreateFontFace(faceType, 1, files, 0, sim, &face), "CreateFontFace(file)");
        file->Release();
        return face;
    }

    // Rasterize at the PHYSICAL size (size * dpiScale) so glyphs are crisp when drawn into a DIP-sized quad at high DPI.
    private GlyphEntry GetGlyph(IDWriteFontFace* face, ushort em, int famId, char ch, float size, bool bold, float dpiScale)
    {
        int sizeQ = (int)MathF.Round(size);
        int scaleQ = (int)MathF.Round(dpiScale * 100f);
        var key = new GlyphKey(famId, sizeQ, scaleQ, bold, ch, ByGid: false);
        if (_cache.TryGetValue(key, out var e)) return e;

        uint cp = ch;
        ushort gi;
        face->GetGlyphIndices(&cp, 1, &gi);

        DWRITE_GLYPH_METRICS gm;
        face->GetDesignGlyphMetrics(&gi, 1, &gm, BOOL.FALSE);
        float advance = gm.advanceWidth * (size / em);            // advance in DIP (scale-independent)
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
        long nonZero = _atlasNonZero;
        for (int row = 0; row < h; row++)
        {
            int srcOff = row * w;
            int dstOff = (e.Y + row) * ATLAS + e.X;
            for (int x = 0; x < w; x++)
            {
                byte v = src[srcOff + x];
                _cpu[dstOff + x] = v;
                if (v != 0) nonZero++;
            }
        }
        _atlasNonZero = nonZero;
        _shelfX += w + 1;
        if (h > _shelfH) _shelfH = h;
    }

    /// <summary>Lay out one run (LTR, design advances) into glyph quads in DIP space, then emit them tinted/transformed into
    /// <paramref name="outList"/>. The shaping (the expensive per-glyph GetGlyph/DirectWrite work) is cached by content under
    /// <see cref="RunKey"/>: an unchanged run replays its baked local-space quads with the CURRENT color/transform/opacity and
    /// touches neither DirectWrite nor the glyph dictionary. <paramref name="textId"/>/<paramref name="familyId"/> are the
    /// interned handles (the cache key); <paramref name="text"/>/<paramref name="family"/> are needed only to shape on a miss.
    /// <paramref name="family"/> selects the face (system name or "path.ttf#Family"); empty = the default body font.</summary>
    public void LayoutRun(StringId textId, StringId familyId, string text, string family, float size, bool bold, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines, ColorF color, float dpiScale, Affine2D world, float opacity, List<GlyphInstance> outList)
    {
        var key = MakeRunKey(textId, familyId, size, bold, maxWidth, wrap, trim, maxLines, originX, topY, dpiScale);

        ref var hit = ref CollectionsMarshal.GetValueRefOrNullRef(_runCache, key);
        if (!Unsafe.IsNullRef(ref hit))
        {
            hit.LastUsedFrame = _frame;
            _runsCached++;
#if DEBUG
            if (VerifyCache) VerifyAgainstReshape(in hit, text, family, size, bold, originX, topY, maxWidth, wrap, trim, maxLines, dpiScale);
#endif
            Replay(hit.Glyphs.AsSpan(0, hit.Count), color, world, opacity, outList);
            return;
        }

        // Miss (new/changed run): shape once into the scratch buffer, cache the baked local-space quads, then replay.
        // The quad array is POOLED (returned on eviction) so scroll-storms of fresh text don't churn Gen0 per run.
        _scratch.Clear();
        ShapeInto(text, family, size, bold, originX, topY, maxWidth, wrap, trim, maxLines, dpiScale, _scratch);
        int n = _scratch.Count;
        var arr = RentQuads(n);
        for (int i = 0; i < n; i++) arr[i] = _scratch[i];
        _runCache[key] = new ShapedRun { Glyphs = arr, Count = n, LastUsedFrame = _frame };
        _runsShaped++;
        Replay(arr.AsSpan(0, n), color, world, opacity, outList);
    }

    /// <summary>Wire the interner so the run cache can drop runs whose text id was reclaimed (resolves empty).</summary>
    public void SetLivenessSource(StringTable strings) => _liveness = strings;

    private ShapedGlyph[] RentQuads(int count)
    {
        if (count == 0) return Array.Empty<ShapedGlyph>();
        int bucket = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(count - 1));
        if (bucket >= _quadPool.Length) return new ShapedGlyph[count];   // pathological run — don't pool
        var stack = _quadPool[bucket];
        return stack is { Count: > 0 } ? stack.Pop() : new ShapedGlyph[1 << bucket];
    }

    private void ReturnQuads(ShapedGlyph[] arr)
    {
        if (arr.Length == 0) return;
        int bucket = System.Numerics.BitOperations.Log2((uint)arr.Length);
        if (bucket >= _quadPool.Length || arr.Length != 1 << bucket) return;
        (_quadPool[bucket] ??= new Stack<ShapedGlyph[]>()).Push(arr);
    }

    private static RunKey MakeRunKey(StringId textId, StringId familyId, float size, bool bold, float maxWidth, int wrap, int trim, int maxLines, float originX, float topY, float dpiScale)
    {
        int widthQ = float.IsInfinity(maxWidth) || maxWidth > 1e9f ? int.MaxValue : (int)MathF.Round(maxWidth);
        return new RunKey(textId.Value, familyId.Value, (int)MathF.Round(size), bold ? 1 : 0, wrap, trim, maxLines,
            widthQ, (int)MathF.Round(originX), (int)MathF.Round(topY), (int)MathF.Round(dpiScale * 100f));
    }

    /// <summary>Emit cached local-space quads into <paramref name="outList"/>, applying the per-frame color/transform/opacity.
    /// Allocation-free (appends into the reused glyph-instance list) — the steady-state path for unchanged text.</summary>
    private static void Replay(ReadOnlySpan<ShapedGlyph> glyphs, ColorF color, Affine2D world, float opacity, List<GlyphInstance> outList)
    {
        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly var s = ref glyphs[i];
            outList.Add(new GlyphInstance
            {
                DstX = s.DstX, DstY = s.DstY, DstW = s.DstW, DstH = s.DstH,
                U0 = s.U0, V0 = s.V0, U1 = s.U1, V1 = s.V1,
                R = color.R, G = color.G, B = color.B, A = color.A,
                M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22, Dx = world.Dx, Dy = world.Dy, Opacity = opacity,
            });
        }
    }

    /// <summary>Shape + lay out a run via the DirectWrite layout engine (itemize → shape → wrap/trim → position, with
    /// kerning/ligatures/complex-script/BiDi), then rasterize each POST-shaping glyph (by glyph id) into the atlas and
    /// bake its local-space quad. The engine drives BOTH this render path and the measure path, so they layout identically.</summary>
    private void ShapeInto(string text, string family, float size, bool bold, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines, float dpiScale, List<ShapedGlyph> outList)
    {
        _engine.Layout(text.AsSpan(), family ?? "", bold, size, maxWidth, wrap, trim, maxLines);
        float inv = 1f / dpiScale;
        foreach (var lg in _engine.Glyphs)
        {
            if (lg.Face == 0) continue;
            var ge = GetGlyphByGid((IDWriteFontFace*)lg.Face, lg.Gid, size, dpiScale);
            if (ge.W > 0 && ge.H > 0)
                outList.Add(new ShapedGlyph
                {
                    DstX = originX + lg.X + ge.BearingX * inv, DstY = topY + lg.Y + ge.BearingY * inv,
                    DstW = ge.W * inv, DstH = ge.H * inv,
                    U0 = ge.X / (float)ATLAS, V0 = ge.Y / (float)ATLAS, U1 = (ge.X + ge.W) / (float)ATLAS, V1 = (ge.Y + ge.H) / (float)ATLAS,
                });
        }
    }

    private int FaceId(nint face) { if (_faceIds.TryGetValue(face, out int id)) return id; id = _faceIds.Count + 1; _faceIds[face] = id; return id; }

    // Rasterize one POST-shaping glyph (by glyph id, not codepoint) at the physical size into the atlas. The face comes
    // from the layout engine; DWrite factories are process-shared, so _dw and the engine's factory are the same object.
    private GlyphEntry GetGlyphByGid(IDWriteFontFace* face, ushort gid, float size, float dpiScale)
    {
        int faceId = FaceId((nint)face);
        int sizeQ = (int)MathF.Round(size);
        int scaleQ = (int)MathF.Round(dpiScale * 100f);
        var key = new GlyphKey(faceId, sizeQ, scaleQ, false, gid, ByGid: true);
        if (_cache.TryGetValue(key, out var e)) return e;

        float physEm = size * dpiScale;
        float zeroAdvance = 0f;
        ushort gi = gid;
        DWRITE_GLYPH_RUN run = default;
        run.fontFace = face; run.fontEmSize = physEm; run.glyphCount = 1;
        run.glyphIndices = &gi; run.glyphAdvances = &zeroAdvance; run.glyphOffsets = null;
        run.isSideways = BOOL.FALSE; run.bidiLevel = 0;

        IDWriteGlyphRunAnalysis* analysis;
        Check(_dw->CreateGlyphRunAnalysis(&run, 1.0f, null, DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL,
            DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL, 0f, 0f, &analysis), "CreateGlyphRunAnalysis");
        RECT bounds;
        Check(analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds), "GetAlphaTextureBounds");
        int w = bounds.right - bounds.left, h = bounds.bottom - bounds.top;
        e = new GlyphEntry { Advance = 0f, BearingX = bounds.left, BearingY = bounds.top, W = w, H = h };
        if (w > 0 && h > 0)
        {
            byte[] rgb = ArrayPool<byte>.Shared.Rent(w * h * 3);
            fixed (byte* pr = rgb)
                Check(analysis->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds, pr, (uint)(w * h * 3)), "CreateAlphaTexture");
            byte[] gray = ArrayPool<byte>.Shared.Rent(w * h);
            for (int i = 0; i < w * h; i++) gray[i] = (byte)((rgb[3 * i] + rgb[3 * i + 1] + rgb[3 * i + 2]) / 3);
            Pack(ref e, gray, w, h);
            ArrayPool<byte>.Shared.Return(gray);
            ArrayPool<byte>.Shared.Return(rgb);
            _atlasDirty = true;
        }
        Diag.Count("text.glyph", "rasterized");
        analysis->Release();
        _cache[key] = e;
        return e;
    }

    private float Emit(IDWriteFontFace* face, ushort em, int famId, char ch, float size, bool bold, float dpiScale, float pen, float baseline, float inv, List<ShapedGlyph> outList)
    {
        var g = GetGlyph(face, em, famId, ch, size, bold, dpiScale);
        if (g.W > 0 && g.H > 0)
            outList.Add(new ShapedGlyph
            {
                DstX = pen + g.BearingX * inv, DstY = baseline + g.BearingY * inv, DstW = g.W * inv, DstH = g.H * inv,
                U0 = g.X / (float)ATLAS, V0 = g.Y / (float)ATLAS, U1 = (g.X + g.W) / (float)ATLAS, V1 = (g.Y + g.H) / (float)ATLAS,
            });
        return pen + g.Advance;
    }

    // Drop runs not referenced for a while so the cache tracks the live (e.g. virtualized) working set. Off the hot path —
    // called from BeginFrame on a stride. The age threshold tightens when the cache grows large (a bounded backstop),
    // and a run whose text id was RECLAIMED by the interner (resolves empty — no live node shows it, the draw list
    // can't reference it) is dropped after a short grace, so storm-shaped runs recycle their quad arrays promptly.
    private void EvictStaleRuns()
    {
        int maxAge = _runCache.Count > 4096 ? 60 : 240;
        foreach (var kv in _runCache)
        {
            int idle = _frame - kv.Value.LastUsedFrame;
            if (idle > maxAge
                || (idle > 2 && kv.Key.TextId != 0 && _liveness is { } st && st.Resolve(new StringId(kv.Key.TextId)).Length == 0))
                _evictScratch.Add(kv.Key);
        }
        foreach (var k in _evictScratch)
            if (_runCache.Remove(k, out var dead))
                ReturnQuads(dead.Glyphs);
        _evictScratch.Clear();
    }

#if DEBUG
    // Re-shape and assert the cached run is byte-identical to a fresh shape — proves the cache is output-preserving.
    private void VerifyAgainstReshape(in ShapedRun cached, string text, string family, float size, bool bold, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines, float dpiScale)
    {
        _scratch.Clear();
        ShapeInto(text, family, size, bold, originX, topY, maxWidth, wrap, trim, maxLines, dpiScale, _scratch);
        Debug.Assert(_scratch.Count == cached.Count, $"shaped-run cache count mismatch for \"{text}\": {_scratch.Count} vs {cached.Count}");
        for (int i = 0; i < _scratch.Count && i < cached.Count; i++)
        {
            var a = _scratch[i]; var b = cached.Glyphs[i];
            Debug.Assert(a.DstX == b.DstX && a.DstY == b.DstY && a.DstW == b.DstW && a.DstH == b.DstH
                && a.U0 == b.U0 && a.V0 == b.V0 && a.U1 == b.U1 && a.V1 == b.V1, $"shaped-run cache geometry mismatch at glyph {i} of \"{text}\"");
        }
    }
#endif

    // The wrap/fit/measure math is shared with the layout MEASURE path (FluentGpu.Text.LineBreaker) so render and measure
    // break lines identically — the advance source is the same DirectWrite design advance via GetGlyph.
    private readonly struct GlyphAdvanceSource : IAdvanceSource
    {
        private readonly GlyphRenderer _r;
        private readonly nint _face;
        private readonly ushort _em; private readonly int _famId; private readonly float _size; private readonly bool _bold; private readonly float _dpi;
        public GlyphAdvanceSource(GlyphRenderer r, IDWriteFontFace* face, ushort em, int famId, float size, bool bold, float dpi)
        { _r = r; _face = (nint)face; _em = em; _famId = famId; _size = size; _bold = bold; _dpi = dpi; }
        public float Advance(char ch) => _r.GetGlyph((IDWriteFontFace*)_face, _em, _famId, ch, _size, _bold, _dpi).Advance;
        public float EllipsisAdvance => _r.GetGlyph((IDWriteFontFace*)_face, _em, _famId, '…', _size, _bold, _dpi).Advance;
    }

    private float MeasureRange(IDWriteFontFace* face, ushort em, int famId, string text, int s, int e, float size, bool bold, float dpiScale)
        => LineBreaker.MeasureRange(text.AsSpan(), s, e, new GlyphAdvanceSource(this, face, em, famId, size, bold, dpiScale));

    private int FitEllipsis(IDWriteFontFace* face, ushort em, int famId, string text, int s, int e, float size, bool bold, float dpiScale, float maxWidth, float ellipsisW)
        => LineBreaker.FitEllipsis(text.AsSpan(), s, e, maxWidth, ellipsisW, new GlyphAdvanceSource(this, face, em, famId, size, bold, dpiScale));

    private static int SkipSpaces(string text, int i) { while (i < text.Length && text[i] == ' ') i++; return i; }

    /// <summary>Greedy line break (shared with the measure path): the exclusive end index of the line starting at
    /// <paramref name="start"/> that fits within <paramref name="maxWidth"/>.</summary>
    private int WrapEnd(IDWriteFontFace* face, ushort em, int famId, string text, int start, int n, float size, bool bold, float dpiScale, float maxWidth, int wrap)
        => LineBreaker.WrapEnd(text.AsSpan(), start, n, maxWidth, wrap, new GlyphAdvanceSource(this, face, em, famId, size, bold, dpiScale));

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
        D3D12MemoryDiagnostics.Track(_tex, $"Glyph.AtlasTexture {ATLAS}x{ATLAS} R8", (ulong)ATLAS * ATLAS);

        _texUpload = CreateUpload(device, ATLAS * ATLAS, "Glyph.AtlasUpload");

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
        _quad = CreateUpload(device, sizeof(float) * 8, "Glyph.QuadUpload");
        void* qp; _quad->Map(0, null, &qp); Buffer.MemoryCopy(quad, qp, 32, 32); _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW { BufferLocation = _quad->GetGPUVirtualAddress(), SizeInBytes = 32, StrideInBytes = 8 };

        _instances = CreateUpload(device, (uint)(sizeof(GlyphInstance) * MaxGlyphs), "Glyph.InstanceUpload");
        void* ip; _instances->Map(0, null, &ip); _mapped = (GlyphInstance*)ip;
    }

    public void BeginFrame()
    {
        _cursor = 0;
        _frame++;
        _runsCached = 0;
        _runsShaped = 0;
        // Sweep stale shaped runs: every 64 frames at rest, every 8 under churn (a scroll storm fills the cache with
        // dead-id runs — sweeping sooner keeps their pooled quad arrays cycling instead of piling up).
        int stride = _runCache.Count > 2048 ? 7 : 63;
        if ((_frame & stride) == 0) EvictStaleRuns();
    }

    public void Record(ID3D12GraphicsCommandList* cmd, List<GlyphInstance> instances, float vpW, float vpH)
    {
        int start = _cursor;
        int count = Math.Min(instances.Count, MaxGlyphs - start);
        if (count == 0) return;
        for (int i = 0; i < count; i++) _mapped[start + i] = instances[i];
        _cursor += count;

        ID3D12DescriptorHeap* heap = _srvHeap;
        cmd->SetDescriptorHeaps(1, &heap);
        cmd->SetGraphicsRootSignature(_rootSig);
        cmd->SetPipelineState(_pso);
        float* vp = stackalloc float[2] { vpW, vpH };
        cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
        cmd->SetGraphicsRootDescriptorTable(1, _srvGpu);
        cmd->SetGraphicsRootShaderResourceView(2, _instances->GetGPUVirtualAddress() + (ulong)(start * sizeof(GlyphInstance)));
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView) cmd->IASetVertexBuffers(0, 1, qv);
        cmd->DrawInstanced(4, (uint)count, 0, 0);
    }

    private static ID3D12Resource* CreateUpload(ID3D12Device* device, uint bytes, string name)
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
        D3D12MemoryDiagnostics.Track(res, name, bytes);
        return res;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        if (_instances != null) { _instances->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances, "Glyph.InstanceUpload"); _instances->Release(); _instances = null; }
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "Glyph.QuadUpload"); _quad->Release(); _quad = null; }
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
        if (_srvHeap != null) _srvHeap->Release();
        if (_texUpload != null) { D3D12MemoryDiagnostics.Release(_texUpload, "Glyph.AtlasUpload"); _texUpload->Release(); _texUpload = null; }
        if (_tex != null) { D3D12MemoryDiagnostics.Release(_tex, "Glyph.AtlasTexture"); _tex->Release(); _tex = null; }
        foreach (var f in _faces.Values) if (f != 0) ((IDWriteFontFace*)f)->Release();
        _faces.Clear();
        if (_dw != null) _dw->Release();
    }
}
