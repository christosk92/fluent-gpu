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

/// <summary>A gradient-glyph instance for the sub-glyph karaoke wipe: like <see cref="GlyphInstance"/> but the per-PIXEL
/// fill is a linear gradient along the run axis (before→after over a soft band at <see cref="Split"/>), so a single glyph
/// straddling the split renders half sung / half unsung (BetterLyrics LyricsLineRendererBase). <see cref="Gt0"/>/<see
/// cref="Gt1"/> are this glyph's run-local-x extent (0..1); the VS interpolates them across the quad → per-pixel run
/// position. A SEPARATE path from <see cref="GlyphInstance"/> so normal text keeps its lean 80-byte single-color instance.
/// Field ORDER keeps every float4 (m/before/after) at a 16-byte-aligned offset (structured-buffer rule), matching the HLSL.</summary>
internal struct GradGlyphInstance
{
    public float DstX, DstY, DstW, DstH;       // dst(2) + size(2)
    public float U0, V0, U1, V1;               // uv0(2) + uv1(2)
    public float M11, M12, M21, M22;           // m (float4) — 2x3 world, rotation/scale part
    public float BR, BG, BB, BA;               // before (sung) color
    public float AR, AG, AB, AA;               // after (unsung) color
    public float Dx, Dy;                       // t (float2) — world translation
    public float Gt0, Gt1;                     // run-local-x extent of this glyph (0..1 along the run)
    public float Split, Fade;                  // wipe split + soft fade band (run fractions)
    public float Opacity;
    public float Pad;                          // → 28 floats / 112 bytes
}

internal struct GlyphEntry { public int X, Y, W, H; public float BearingX, BearingY, Advance; }
internal struct FaceMetrics { public ushort Em; public short Asc, Desc; }
/// <summary>Glyph-atlas cache key. <paramref name="Fam"/> is a family id (codepoint path) OR a face id (glyph-id path);
/// <paramref name="Ch"/> is a codepoint OR a glyph id. <paramref name="ByGid"/> keeps those two key spaces disjoint so a
/// codepoint entry and a glyph-id entry can never alias (the bug that smeared shaped + per-char glyphs together).
/// <paramref name="Weight"/> is the NUMERIC font weight (codepoint path; the glyph-id path keys weight via the face id,
/// since faces are resolved per (family, weight)). Value type → no alloc.</summary>
internal readonly record struct GlyphKey(int Fam, int Size, int Scale, int Weight, int Ch, bool ByGid);

/// <summary>One baked glyph quad in LOCAL (DIP) space — color/transform/opacity are applied per-frame at replay, NOT baked
/// here, so the same shaped run is reusable across scroll/theme/fade. The atlas UVs are stable (the shelf packer never
/// repacks), so a cached quad stays valid for the life of the run.</summary>
internal struct ShapedGlyph { public float DstX, DstY, DstW, DstH, U0, V0, U1, V1; }

/// <summary>A fully shaped text run cached by content (see <see cref="RunKey"/>): the local-space quads + an LRU stamp.
/// Allocated only on a cache miss (content change); replayed allocation-free on every steady-state frame.
/// <see cref="Colors"/> (span runs only, parallel to <see cref="Glyphs"/>): the per-quad span color override —
/// A==0 entries inherit the replayed command color; null = a plain uniform run (the overwhelming case).</summary>
internal struct ShapedRun { public ShapedGlyph[] Glyphs; public ColorF[]? Colors; public int Count; public int LastUsedFrame; }

/// <summary>Content key for the shaped-run cache. Keyed on the interned <see cref="StringId"/> handles (stable across frames,
/// no per-frame string hashing) + quantized layout inputs — including EVERY shaping input of the glyph op (numeric Weight,
/// CharacterSpacing ×10, LineHeight ×10, packed LineStacking|LineBounds, the SpanRunId inline-run overlay — a span style
/// change mints a fresh id upstream, so the key self-invalidates), so two runs differing only in weight or tracking
/// can never alias. Excludes color/transform/opacity (replayed). Bounds origin and width are layout-stable for a given
/// element (scroll rides the world transform, not the bounds), so they can key safely.</summary>
internal readonly struct RunKey : IEquatable<RunKey>
{
    private readonly int _textId, _famId, _sizeQ, _weight, _wrap, _trim, _maxLines, _widthQ, _originXQ, _originYQ, _scaleQ;
    private readonly int _spacingQ, _lineHQ, _lineFlags, _spanId, _hash;

    public int TextId => _textId;

    public RunKey(int textId, int famId, int sizeQ, int weight, int wrap, int trim, int maxLines, int widthQ, int originXQ, int originYQ, int scaleQ,
        int spacingQ, int lineHQ, int lineFlags, int spanId)
    {
        _textId = textId; _famId = famId; _sizeQ = sizeQ; _weight = weight; _wrap = wrap; _trim = trim; _maxLines = maxLines;
        _widthQ = widthQ; _originXQ = originXQ; _originYQ = originYQ; _scaleQ = scaleQ; _spacingQ = spacingQ; _lineHQ = lineHQ;
        _lineFlags = lineFlags; _spanId = spanId;
        _hash = Hash(textId, famId, sizeQ, weight, wrap, trim, maxLines, widthQ, originXQ, originYQ, scaleQ, spacingQ, lineHQ, lineFlags, spanId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(RunKey other)
        => _hash == other._hash
           && _textId == other._textId && _famId == other._famId && _sizeQ == other._sizeQ && _weight == other._weight
           && _wrap == other._wrap && _trim == other._trim && _maxLines == other._maxLines && _widthQ == other._widthQ
           && _originXQ == other._originXQ && _originYQ == other._originYQ && _scaleQ == other._scaleQ
           && _spacingQ == other._spacingQ && _lineHQ == other._lineHQ && _lineFlags == other._lineFlags && _spanId == other._spanId;

    public override bool Equals(object? obj) => obj is RunKey other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _hash;

    private static int Hash(int textId, int famId, int sizeQ, int weight, int wrap, int trim, int maxLines, int widthQ, int originXQ, int originYQ, int scaleQ,
        int spacingQ, int lineHQ, int lineFlags, int spanId)
    {
        unchecked
        {
            int h = textId;
            h = (h * 397) ^ famId;
            h = (h * 397) ^ sizeQ;
            h = (h * 397) ^ weight;
            h = (h * 397) ^ wrap;
            h = (h * 397) ^ trim;
            h = (h * 397) ^ maxLines;
            h = (h * 397) ^ widthQ;
            h = (h * 397) ^ originXQ;
            h = (h * 397) ^ originYQ;
            h = (h * 397) ^ scaleQ;
            h = (h * 397) ^ spacingQ;
            h = (h * 397) ^ lineHQ;
            h = (h * 397) ^ lineFlags;
            return (h * 397) ^ spanId;
        }
    }
}

/// <summary>
/// DirectWrite glyph atlas + textured-quad pipeline (design/subsystems/text.md, gpu-renderer.md DrawGlyphRun). Glyphs are
/// rasterized to a CPU R8 coverage atlas via <c>CreateGlyphRunAnalysis</c>/<c>CreateAlphaTexture</c>, uploaded to a GPU
/// texture, and drawn as quads sampling the atlas (tinted by the run color, gamma-correct grayscale AA). Slice shaping is
/// LTR design-advance positioning over the system "Segoe UI" face; full itemize→BiDi→shape is the spec's follow-up.
/// </summary>
internal sealed unsafe class GlyphRenderer : IDisposable
{
    // 2048² R8 (4 MB CPU mirror + 4 MB GPU): sized so even the Iconography page's full Segoe Fluent catalog
    // (~1,500 distinct glyphs at two sizes) plus the app's text fits one generation. Overflow is still HANDLED
    // (generational reset below) — before that, a full atlas silently cached entries at X=Y=0, so every later
    // glyph sampled the atlas origin and corrupted all text for the rest of the session.
    private const int ATLAS = 2048;

    private IDWriteFactory* _dw;
    private const string DefaultFamily = "Segoe UI";
    // Per-(family, numeric weight) faces + metrics, resolved on demand. Family = a system name OR "path.ttf#Family Name".
    private readonly Dictionary<(string fam, int weight), nint> _faces = new();
    private readonly Dictionary<(string fam, int weight), FaceMetrics> _faceMetrics = new();
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
    // ThemedIcon coverage masks packed into the SAME R8 atlas as glyphs, keyed by (interned PathId, device px). A
    // SEPARATE dictionary from _cache so an icon entry can never alias a GlyphKey (structurally disjoint — the design's
    // "reserved key space", realized as its own map). Dropped on a generational atlas reset like _cache/_runCache.
    private readonly Dictionary<(int PathId, int W, int H), GlyphEntry> _iconCache = new();
    // Itemize→shape→wrap layout (shared logic with the measure path, so render and measure layout identically).
    private TextLayoutEngine _engine = null!;
    private readonly Dictionary<nint, int> _faceIds = new();   // IDWriteFontFace* → small int for the glyph-cache key

    // Shaped-run cache: unchanged text runs replay their baked local-space quads instead of re-shaping every frame
    // (kills the per-glyph GetGlyph/Dictionary.TryGetValue/DirectWrite storm). Keyed on interned StringId handles, so a
    // hit needs neither a string hash nor a face/family lookup. See LayoutRun.
    private readonly Dictionary<RunKey, ShapedRun> _runCache = new();
    private readonly List<ShapedGlyph> _scratch = new(256);   // reused miss-path shaping buffer
    private readonly List<ColorF> _colorScratch = new(256);   // reused span-run per-quad color buffer (miss path)
    private readonly List<RunKey> _evictScratch = new();      // reused eviction sweep buffer (off the hot path)
    private readonly float[] _vpConstants = new float[2];
    // Renderer-owned quad-array free-list (bucketed by pow2 size). A virtualization storm shapes thousands of fresh
    // runs whose arrays the cache holds for many frames — the SHARED ArrayPool drains and falls back to allocating;
    // this list retains returned arrays (up to a per-bucket cap), so steady-state churn reuses instead of allocating.
    private readonly Stack<ShapedGlyph[]>[] _quadPool = new Stack<ShapedGlyph[]>[14];   // buckets 1<<0 .. 1<<13
    // Per-bucket retained-depth cap (audit mem-02): without it the free-list ratchets to the session-peak run count and
    // never gives memory back. A single frame can rent at most one array per cache-MISS run; an eviction sweep can
    // return many same-size arrays at once. 8 per bucket absorbs that transient (an eviction sweep landing while a few
    // new same-size runs are mid-shape) without holding peak. Returns beyond the cap drop the reference (managed arrays
    // — the GC reclaims them); the only cost of dropping is that the NEXT miss at that size re-allocates one array.
    private const int MaxPooledQuadArraysPerBucket = 8;
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
    private const int FrameCount = 2;   // double-buffered per frame-in-flight so frame N's CPU writes never race frame N-1's GPU reads
    private readonly ID3D12Resource*[] _instances = new ID3D12Resource*[FrameCount];
    private readonly GlyphInstance*[] _mapped = new GlyphInstance*[FrameCount];
    private const int MaxGlyphs = 8192;
    private int _cursor;
    private int _active;
    private ulong _activeGva;
    private int _dropped;

    // Sub-glyph gradient-wipe path: a second PSO (per-pixel before→after fill) with its own instance buffers. Only the
    // active lyric line + its glow feed it (~tens of glyphs), so a small cap; normal text never touches it.
    private ID3D12PipelineState* _psoGrad;
    private const int MaxGradGlyphs = 1024;
    private readonly ID3D12Resource*[] _gradInstances = new ID3D12Resource*[FrameCount];
    private readonly GradGlyphInstance*[] _mappedGrad = new GradGlyphInstance*[FrameCount];
    private int _gradCursor;
    private ulong _activeGradGva;

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

    // Sub-glyph karaoke wipe: same atlas/sampler/viewport bindings as the glyph shader, but the fill is a per-pixel linear
    // gradient along the run axis. `gt` interpolates this glyph's run-local-x extent [gt0,gt1] across the quad, and the PS
    // mixes before→after over a soft band at `split` — so a glyph straddling the split renders half sung / half unsung.
    private const string HlslGrad = """
struct GG { float2 dst; float2 size; float2 uv0; float2 uv1; float4 m; float4 before; float4 after; float2 t; float2 gt; float2 splitFade; float opacity; float pad; };
StructuredBuffer<GG> gInstG : register(t1);
Texture2D gAtlas : register(t0);
SamplerState gSamp : register(s0);
cbuffer Root : register(b0) { float2 gViewport; };
struct VSOutG { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 before : TEXCOORD1; float4 after : TEXCOORD2; float opacity : TEXCOORD3; float gt : TEXCOORD4; float2 splitFade : TEXCOORD5; };

VSOutG VSMain(float2 corner : POSITION, uint iid : SV_InstanceID)
{
    GG g = gInstG[iid];
    float2 lp = g.dst + corner * g.size;
    float2 world = float2(g.m.x * lp.x + g.m.z * lp.y + g.t.x, g.m.y * lp.x + g.m.w * lp.y + g.t.y);
    float2 ndc = float2(world.x / gViewport.x * 2.0 - 1.0, 1.0 - world.y / gViewport.y * 2.0);
    VSOutG o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.uv = lerp(g.uv0, g.uv1, corner);
    o.before = g.before; o.after = g.after; o.opacity = g.opacity;
    o.gt = lerp(g.gt.x, g.gt.y, corner.x);   // run-local-x at this pixel column (0..1)
    o.splitFade = g.splitFade;
    return o;
}

float4 PSMain(VSOutG i) : SV_Target
{
    float cov = gAtlas.Sample(gSamp, i.uv).r;                                     // glyph coverage
    float a = saturate((i.splitFade.x - i.gt) / max(i.splitFade.y, 1e-4) + 0.5);  // 1 = sung (before), 0 = unsung (after)
    float4 col = lerp(i.after, i.before, a);
    float aOut = col.a * cov * i.opacity;
    return float4(col.rgb * aOut, aOut);   // premultiplied
}
""";

    // diagnostics (surfaced through the standardized Diag facility; stripped on release)
    public int CachedGlyphs => _cache.Count;
    public int CachedRuns => _runCache.Count;
    public int RunsCached => _runsCached;
    public int RunsShaped => _runsShaped;
    public int DroppedInstances => _dropped;
    public long AtlasNonZero => _atlasNonZero;

    // ── MemCensus accessors (O(1), or a tiny fixed-bucket sum) ────────────────────────────────────
    /// <summary>Cached rasterized glyph entries (the glyph atlas cache) — O(1) census.</summary>
    internal int CachedGlyphCount => _cache.Count;
    /// <summary>Cached shaped text runs (the run cache) — O(1) census.</summary>
    internal int CachedRunCount => _runCache.Count;
    /// <summary>Atlas generation-reset count (the epoch counter; bumped when the atlas fills and flushes) — O(1).</summary>
    internal int AtlasResetCount => _atlasEpoch;
    /// <summary>Total retained quad arrays across the pow2 free-list buckets — a fixed 14-bucket sum (census cadence,
    /// never per-frame): how many shaped-run arrays the renderer is holding for reuse.</summary>
    internal int QuadPoolRetained
    {
        get { int n = 0; for (int i = 0; i < _quadPool.Length; i++) { var s = _quadPool[i]; if (s is not null) n += s.Count; } return n; }
    }

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
        ResolveFace(DefaultFamily, 400, out _, out _, out _);
    }

    // A small per-family integer used in the glyph-cache key (so the same codepoint in two fonts caches separately).
    private int FamilyId(string family)
    {
        if (string.IsNullOrEmpty(family)) family = DefaultFamily;
        if (_famIds.TryGetValue(family, out int id)) return id;
        id = _famIds.Count + 1; _famIds[family] = id; return id;
    }

    /// <summary>Resolve (and cache) a font face for a family + NUMERIC weight (the int IS the DWRITE_FONT_WEIGHT;
    /// ≤0 → 400, clamped to 999). Family is a system name ("Segoe UI", "Segoe Fluent Icons") or a custom file
    /// "path.ttf#Family Name" (the WinUI syntax). Falls back to the default on error.</summary>
    private IDWriteFontFace* ResolveFace(string family, int weight, out ushort em, out short asc, out short desc)
    {
        if (string.IsNullOrEmpty(family)) family = DefaultFamily;
        if (weight <= 0) weight = 400; else if (weight > 999) weight = 999;   // DWRITE_FONT_WEIGHT range
        var key = (family, weight);
        if (_faces.TryGetValue(key, out var cached))
        {
            var m0 = _faceMetrics[key]; em = m0.Em; asc = m0.Asc; desc = m0.Desc;
            return (IDWriteFontFace*)cached;
        }

        IDWriteFontFace* face;
        try { face = CreateFaceFor(family, weight); }
        catch { face = family == DefaultFamily ? null : CreateFaceFor(DefaultFamily, weight); }
        if (face == null) { em = 2048; asc = 1500; desc = 500; return null; }

        DWRITE_FONT_METRICS m; face->GetMetrics(&m);
        var fm = new FaceMetrics { Em = m.designUnitsPerEm, Asc = (short)m.ascent, Desc = (short)m.descent };
        _faces[key] = (nint)face; _faceMetrics[key] = fm;
        em = fm.Em; asc = fm.Asc; desc = fm.Desc;
        return face;
    }

    private IDWriteFontFace* CreateFaceFor(string family, int weight)
    {
        int hash = family.IndexOf('#');
        string path = hash >= 0 ? family.Substring(0, hash) : family;
        bool isFile = path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
        return isFile ? CreateFaceFromFile(path, weight) : CreateSystemFace(family, weight);
    }

    private IDWriteFontFace* CreateSystemFace(string familyName, int weight)
    {
        IDWriteFontCollection* coll;
        Check(_dw->GetSystemFontCollection(&coll, BOOL.FALSE), "GetSystemFontCollection");
        uint index; BOOL exists;
        fixed (char* pn = familyName) Check(coll->FindFamilyName(pn, &index, &exists), "FindFamilyName");
        if (!exists)   // unknown family → fall back to the default
        {
            coll->Release();
            return familyName == DefaultFamily ? null : CreateSystemFace(DefaultFamily, weight);
        }
        IDWriteFontFamily* family;
        Check(coll->GetFontFamily(index, &family), "GetFontFamily");
        // The numeric weight passes straight through (WinUI FontWeight ≡ DWRITE_FONT_WEIGHT) — GetFirstMatchingFont
        // picks the nearest face / variable-font named instance (e.g. SemiBold 600 of "Segoe UI Variable Text").
        IDWriteFont* font;
        Check(family->GetFirstMatchingFont((DWRITE_FONT_WEIGHT)weight, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font), "GetFirstMatchingFont");
        IDWriteFontFace* face;
        Check(font->CreateFontFace(&face), "CreateFontFace");
        font->Release(); family->Release(); coll->Release();
        return face;
    }

    // Load a face directly from a .ttf/.otf file (custom icon fonts). A raw file has no weight family to pick from,
    // so the heavy half (>= 600) synthesizes bold.
    private IDWriteFontFace* CreateFaceFromFile(string path, int weight)
    {
        IDWriteFontFile* file;
        fixed (char* p = path) Check(_dw->CreateFontFileReference(p, null, &file), "CreateFontFileReference");
        BOOL supported; DWRITE_FONT_FILE_TYPE ft; DWRITE_FONT_FACE_TYPE faceType; uint numFaces;
        Check(file->Analyze(&supported, &ft, &faceType, &numFaces), "Analyze(font)");
        IDWriteFontFile** files = &file;
        var sim = weight >= 600 ? DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD : DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE;
        IDWriteFontFace* face;
        Check(_dw->CreateFontFace(faceType, 1, files, 0, sim, &face), "CreateFontFace(file)");
        file->Release();
        return face;
    }

    // Rasterize at the PHYSICAL size (size * dpiScale) so glyphs are crisp when drawn into a DIP-sized quad at high DPI.
    private GlyphEntry GetGlyph(IDWriteFontFace* face, ushort em, int famId, char ch, float size, int weight, float dpiScale)
    {
        int sizeQ = (int)MathF.Round(size);
        int scaleQ = (int)MathF.Round(dpiScale * 100f);
        var key = new GlyphKey(famId, sizeQ, scaleQ, weight, ch, ByGid: false);
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
        // NATURAL_SYMMETRIC (not NATURAL): symmetric AA in BOTH axes. Plain NATURAL anti-aliases only horizontally, which
        // samples out fine horizontal stroke features — the documented blur on CJK faces (e.g. MS Mincho) and large text.
        // SYMMETRIC is the modern default (UWP/WinUI/WPF) and Microsoft's recommendation above ~16 ppem. (dwrite.h docs.)
        Check(_dw->CreateGlyphRunAnalysis(&run, 1.0f, null, DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
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
            PackOrReset(ref e, gray, w, h);
            _atlasDirty = true;
        }
        Diag.Count("text.glyph", "rasterized");
        analysis->Release();
        _cache[key] = e;
        return e;
    }

    /// <summary>Shelf-pack one rasterized glyph. False = atlas full — the caller must NOT cache the entry as-is
    /// (an unpacked entry keeps X=Y=0 and would sample the atlas origin); it resets the atlas generation and retries.</summary>
    private bool TryPack(ref GlyphEntry e, byte[] src, int w, int h)
    {
        if (_shelfX + w + 1 > ATLAS) { _shelfX = 1; _shelfY += _shelfH + 1; _shelfH = 0; }
        if (_shelfY + h + 1 > ATLAS) return false;   // atlas full → generational reset (ResetAtlas)
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
        return true;
    }

    /// <summary>Pack with overflow recovery: a full atlas flushes the GENERATION (pixels + glyph entries + every cached
    /// run — their quads embed atlas UVs) and packs into the fresh one. Quads already emitted THIS frame briefly sample
    /// repacked texels (one-frame artifact on the overflow frame); next frame re-shapes from the empty run cache and is
    /// clean. The epoch counter lets an in-flight <see cref="ShapeInto"/> detect the flush and re-shape its own run so a
    /// single cached run never mixes generations.</summary>
    private void PackOrReset(ref GlyphEntry e, byte[] src, int w, int h)
    {
        if (TryPack(ref e, src, w, h)) return;
        ResetAtlas();
        if (!TryPack(ref e, src, w, h))
        {
            // A single glyph larger than the whole atlas — render nothing rather than garbage.
            e.W = 0; e.H = 0;
            Diag.Set("text.atlas", "oversized-glyph", $"{w}x{h} > {ATLAS}");
        }
    }

    private int _atlasEpoch;

    /// <summary>Look up a packed icon mask's atlas UVs. Returns true on a HIT (a present entry, even an empty 0×0 mask —
    /// so the caller doesn't re-rasterize nothing); <paramref name="u1"/>&gt;<paramref name="u0"/> means it has pixels.</summary>
    internal bool TryGetIconUv(int pathId, int w, int h, out float u0, out float v0, out float u1, out float v1)
    {
        if (_iconCache.TryGetValue((pathId, w, h), out var e)) { IconUv(in e, out u0, out v0, out u1, out v1); return true; }
        u0 = v0 = u1 = v1 = 0f;
        return false;
    }

    /// <summary>Shelf-pack an icon's R8 coverage mask (<paramref name="src"/>, row-major, ≥ w×h) into the glyph atlas
    /// (generational-reset-safe via <see cref="PackOrReset"/>) and cache it under (pathId, w, h); returns its atlas UVs.
    /// Backend-side, on an atlas miss — off the frame hot phases (the accepted rasterize-at-replay posture).</summary>
    internal void PackIconMask(int pathId, int w, int h, byte[] src, out float u0, out float v0, out float u1, out float v1)
    {
        var e = new GlyphEntry { W = w, H = h };
        if (w > 0 && h > 0)
        {
            PackOrReset(ref e, src, w, h);   // may trigger a generational reset (which clears _iconCache) — we add AFTER
            _atlasDirty = true;
        }
        _iconCache[(pathId, w, h)] = e;
        IconUv(in e, out u0, out v0, out u1, out v1);
    }

    private static void IconUv(in GlyphEntry e, out float u0, out float v0, out float u1, out float v1)
    {
        u0 = e.X / (float)ATLAS; v0 = e.Y / (float)ATLAS;
        u1 = (e.X + e.W) / (float)ATLAS; v1 = (e.Y + e.H) / (float)ATLAS;
    }

    private void ResetAtlas()
    {
        Array.Clear(_cpu);
        _shelfX = 1; _shelfY = 1; _shelfH = 0;
        _atlasNonZero = 0;
        _cache.Clear();
        _iconCache.Clear();
        foreach (var kv in _runCache) ReturnQuads(kv.Value.Glyphs);
        _runCache.Clear();
        _atlasEpoch++;
        _atlasDirty = true;
        Diag.Count("text.atlas", "generation-reset");
        Diag.Event("text.atlas", $"generation reset #{_atlasEpoch} (atlas full at {ATLAS}x{ATLAS})");
    }

    /// <summary>Lay out one run (LTR, design advances) into glyph quads in DIP space, then emit them tinted/transformed into
    /// <paramref name="outList"/>. The shaping (the expensive per-glyph GetGlyph/DirectWrite work) is cached by content under
    /// <see cref="RunKey"/>: an unchanged run replays its baked local-space quads with the CURRENT color/transform/opacity and
    /// touches neither DirectWrite nor the glyph dictionary. <paramref name="textId"/>/<paramref name="familyId"/> are the
    /// interned handles (the cache key); <paramref name="text"/>/<paramref name="family"/> are needed only to shape on a miss.
    /// <paramref name="family"/> selects the face (system name or "path.ttf#Family"); empty = the default body font.
    /// <paramref name="weight"/> is the NUMERIC font weight; <paramref name="charSpacing"/>/<paramref name="lineHeight"/>/
    /// <paramref name="lineStacking"/>/<paramref name="lineBounds"/> mirror the glyph op (see DrawGlyphRunCmd) and feed
    /// the layout engine, so the GPU path lays out exactly like the measure path.</summary>
    public void LayoutRun(StringId textId, StringId familyId, string text, string family, float size, int weight, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, ColorF color, float dpiScale, Affine2D world, float opacity, List<GlyphInstance> outList,
        int spanRunId = 0, bool forceColor = false, bool inMotion = false)
    {
        var key = MakeRunKey(textId, familyId, size, weight, maxWidth, wrap, trim, maxLines, originX, topY, dpiScale, charSpacing, lineHeight, lineStacking, lineBounds, spanRunId);

        ref var hit = ref CollectionsMarshal.GetValueRefOrNullRef(_runCache, key);
        if (!Unsafe.IsNullRef(ref hit))
        {
            hit.LastUsedFrame = _frame;
            _runsCached++;
#if DEBUG
            if (VerifyCache) VerifyAgainstReshape(in hit, text, family, size, weight, originX, topY, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, dpiScale, spanRunId);
#endif
            var quads = hit.Glyphs.AsSpan(0, hit.Count);
            Replay(quads, hit.Colors, forceColor, color, world, opacity, inMotion ? 0f : SnapDy(quads, world, dpiScale), outList);
            return;
        }

        // Miss (new/changed run): shape once into the scratch buffer, cache the baked local-space quads, then replay.
        // The quad array is POOLED (returned on eviction) so scroll-storms of fresh text don't churn Gen0 per run.
        // Span runs (rtb-01): the SpanRunTable overlay restyles ranges of the SAME flow; per-quad span colors bake
        // into a parallel Colors array (allocated on the miss only — a span style change minted a fresh id anyway).
        var spanRun = spanRunId != 0 ? SpanRunTable.Shared.Resolve(spanRunId) : null;
        _scratch.Clear();
        _colorScratch.Clear();
        ShapeInto(text, family, size, weight, originX, topY, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, dpiScale, _scratch,
            spanRun is not null ? spanRun.Spans : default, spanRun is not null ? _colorScratch : null);
        int n = _scratch.Count;
        var arr = RentQuads(n);
        for (int i = 0; i < n; i++) arr[i] = _scratch[i];
        ColorF[]? colors = null;
        if (spanRun is not null && _colorScratch.Count == n)
        {
            for (int i = 0; i < n && colors is null; i++)
                if (_colorScratch[i].A > 0f) colors = new ColorF[n];   // only retain when some span actually recolors
            if (colors is not null)
                for (int i = 0; i < n; i++) colors[i] = _colorScratch[i];
        }
        _runCache[key] = new ShapedRun { Glyphs = arr, Colors = colors, Count = n, LastUsedFrame = _frame };
        _runsShaped++;
        var baked = arr.AsSpan(0, n);
        Replay(baked, colors, forceColor, color, world, opacity, inMotion ? 0f : SnapDy(baked, world, dpiScale), outList);
    }

    private float[] _gradDy = Array.Empty<float>();
    private float[] _gradScale = Array.Empty<float>();   // per-glyph scale pop at the wipe front (BetterLyrics char pop)
    private float[] _gradRo0 = Array.Empty<float>();     // per-glyph reading-order LEFT  (continuous across wrapped visual lines)
    private float[] _gradRo1 = Array.Empty<float>();     // per-glyph reading-order RIGHT

    /// <summary>Glyph-WIPE variant of <see cref="LayoutRun"/> (the <c>GlyphWipe</c> primitive): shapes/caches the run
    /// under the SAME <see cref="RunKey"/> (so re-using it costs no reshape), then computes a PER-GLYPH color + Y offset
    /// from the wipe <paramref name="split"/> (0..1 along the run's x-extent) — left of the split is
    /// <paramref name="before"/>, right is <paramref name="after"/>, with a <paramref name="softness"/>-wide soft
    /// boundary and a <paramref name="lift"/>-DIP per-glyph float trailing it — and replays through the EXISTING
    /// per-instance color/transform path (no new shader/PSO). The split advancing per frame only changes the computed
    /// per-glyph values, never the cache key, so there is no per-frame reshape.</summary>
    public void LayoutRunGradient(StringId textId, StringId familyId, string text, string family, float size, int weight, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, ColorF before, ColorF after, float split, float softness, float lift, float dpiScale, Affine2D world, float opacity, List<GradGlyphInstance> outList,
        int spanRunId = 0, bool inMotion = false)
    {
        var key = MakeRunKey(textId, familyId, size, weight, maxWidth, wrap, trim, maxLines, originX, topY, dpiScale, charSpacing, lineHeight, lineStacking, lineBounds, spanRunId);

        ShapedGlyph[] quadsArr; int count;
        ref var hit = ref CollectionsMarshal.GetValueRefOrNullRef(_runCache, key);
        if (!Unsafe.IsNullRef(ref hit))
        {
            hit.LastUsedFrame = _frame; _runsCached++;
            quadsArr = hit.Glyphs; count = hit.Count;
        }
        else
        {
            _scratch.Clear();
            ShapeInto(text, family, size, weight, originX, topY, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, dpiScale, _scratch);
            count = _scratch.Count;
            var arr = RentQuads(count);
            for (int i = 0; i < count; i++) arr[i] = _scratch[i];
            _runCache[key] = new ShapedRun { Glyphs = arr, Colors = null, Count = count, LastUsedFrame = _frame };
            _runsShaped++;
            quadsArr = arr;
        }

        var quads = quadsArr.AsSpan(0, count);
        if (count == 0) return;
        if (_gradDy.Length < count) _gradDy = new float[count];
        if (_gradScale.Length < count) _gradScale = new float[count];
        if (_gradRo0.Length < count) _gradRo0 = new float[count];
        if (_gradRo1.Length < count) _gradRo1 = new float[count];

        // READING-ORDER wipe axis. The wipe `split` is a 0..1 fraction of the run's CONTENT IN READING ORDER, not of
        // its raw x-extent. A wrapped run lays its 2nd+ visual lines back at x≈0 (EmitLine restarts the pen per line —
        // TextLayoutEngine.EmitLine: `float x = 0f` per line), so a pure-x boundary (each glyph's DstX vs split·width)
        // paints the LEFT glyphs of line 2 as "already sung" while the still-grey tail of line 1 sits at a LARGER x —
        // i.e. out of reading order (the wrapped-2nd-line glow bug). Instead we exploit that the quads ARE in reading
        // order (EmitLine appends each visual line in turn, left→right within the line; non-inking break glyphs are
        // dropped upstream) and lay the lines END-TO-END into one monotonic coordinate: each glyph's position is
        // (sum of all prior visual-line widths) + its offset from its OWN line's left edge. A visual-line break is where
        // x jumps backward past a full em (within a line x only grows; a wrap resets it to the line origin — a jump of
        // the whole preceding line's width). For a single (non-wrapped) line this collapses to a plain left-edge-to-
        // right-edge normalization, so the boundary reaches the run's true right edge (the last glyph fully fills at
        // split==1). `split`/`fade`/`lift` stay fractions of the total reading-order length.
        float lineBase = 0f;                    // cumulative width of completed visual lines (reading-order origin of this line)
        float lineMinX = quads[0].DstX;         // x-origin of the current visual line
        float lineRight = quads[0].DstX;        // running right edge within the current visual line (x space)
        float lineTol = MathF.Max(size, 1f);    // a wrap jumps back ≫ one em; within-line bearing/marks stay within it
        for (int i = 0; i < count; i++)
        {
            float gx = quads[i].DstX;
            if (i > 0 && gx < lineRight - lineTol)   // x jumped backward past a full line → a new visual line
            {
                lineBase += lineRight - lineMinX;    // bank the completed line's width
                lineMinX = gx;
                lineRight = gx;
            }
            float gr = gx + quads[i].DstW;
            if (gr > lineRight) lineRight = gr;
            _gradRo0[i] = lineBase + (gx - lineMinX);
            _gradRo1[i] = lineBase + (gr - lineMinX);
        }
        float total = MathF.Max(lineBase + (lineRight - lineMinX), 1e-3f);
        float fade = MathF.Max(softness, 1e-4f);
        // Remap split onto the shader's boundary so the `fade`-wide soft band — which the gradient PS CENTRES on the
        // boundary (a = saturate((split - gt)/fade + 0.5)) — fully clears the run at BOTH extremes: split==1 places the
        // boundary half a band PAST the trailing edge (gt = 1 + fade/2) so the last glyph is 100% `before` (white), and
        // split==0 half a band BEFORE the leading edge (gt = -fade/2) so nothing is sung. Without this the trailing
        // fade/2 never fills — the "final syllable never reaches white" defect. The reading-order axis above already puts
        // the last glyph's right edge at gt==1, so this remap is what COMPLETES it. s = split*(1+fade) - fade/2 is the
        // exact closed-form inverse of the PS band: monotonic, pivoting at 0.5 (mid-line wipe timing unchanged), endpoint-exact.
        float splitShader = split * (1f + fade) - 0.5f * fade;
        // BetterLyrics per-char motion at the karaoke front (LyricsAnimator.cs): the SUNG run sits at the baseline while the
        // unsung run is sunk by `lift` (≈10% line-height), each glyph rising into place as the wipe sweeps it; and the glyph
        // AT the split magnifies (scale pop ≈1.12), settling to 1.0 within `popWindow` of the front. The colour wipe itself
        // is per-PIXEL in the gradient PS (ReplayGradient feeds each glyph its reading-order extent), so the boundary cuts
        // THROUGH a glyph — half sung / half unsung — not glyph-by-glyph.
        const float popWindow = 0.12f;
        float popPeak = lift > 0f ? 0.12f : 0f;
        for (int i = 0; i < count; i++)
        {
            float gtc = (_gradRo0[i] + _gradRo1[i]) * 0.5f / total;   // glyph-centre reading-order position (drives float/scale)
            float a = Math.Clamp((splitShader - gtc) / fade + 0.5f, 0f, 1f);
            _gradDy[i] = lift * (1f - a);   // unsung sunk by `lift`, rising to the baseline (0) as it is swept (settles to 0 at split==1)
            float d = MathF.Abs(splitShader - gtc);
            _gradScale[i] = popPeak > 0f && d < popWindow ? 1f + popPeak * (1f - d / popWindow) : 1f;
        }
        ReplayGradient(quads, world, opacity, inMotion ? 0f : SnapDy(quads, world, dpiScale), before, after, splitShader, fade, total,
            _gradRo0.AsSpan(0, count), _gradRo1.AsSpan(0, count), _gradDy.AsSpan(0, count), _gradScale.AsSpan(0, count), outList);
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
        var stack = _quadPool[bucket] ??= new Stack<ShapedGlyph[]>();
        if (stack.Count >= MaxPooledQuadArraysPerBucket) return;   // cap reached → drop the reference (GC reclaims)
        stack.Push(arr);
    }

    private static RunKey MakeRunKey(StringId textId, StringId familyId, float size, int weight, float maxWidth, int wrap, int trim, int maxLines, float originX, float topY, float dpiScale,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, int spanRunId)
    {
        int widthQ = float.IsInfinity(maxWidth) || maxWidth > 1e9f ? int.MaxValue : (int)MathF.Round(maxWidth);
        int lineHQ = float.IsNaN(lineHeight) || lineHeight <= 0f ? 0 : (int)MathF.Round(lineHeight * 10f);   // 0 = font-natural
        return new RunKey(textId.Value, familyId.Value, (int)MathF.Round(size), weight, wrap, trim, maxLines,
            widthQ, (int)MathF.Round(originX), (int)MathF.Round(topY), (int)MathF.Round(dpiScale * 100f),
            (int)MathF.Round(charSpacing * 10f), lineHQ, lineStacking | (lineBounds << 8), spanRunId);
    }

    /// <summary>Per-run vertical device-grid correction (local DIP), applied at replay. Glyph bitmaps are rasterized
    /// for an INTEGER device baseline (CreateGlyphRunAnalysis at baselineOrigin 0,0) and every bearing/height is an
    /// integer device row, so all of a run's quads share the first quad's fractional device-Y phase. Drawn at a
    /// fractional device Y, the LINEAR/CLAMP atlas sampler attenuates the bottom coverage row by (1−frac) — the
    /// intermittent "label shaved 1-2px at the bottom" defect (layout never snaps: a centered row yields fractional
    /// DIP Y, and the DPI scale is fractional at 125/150/175%). Snapping happens HERE, not at bake, so cached quads
    /// stay phase-agnostic and one run cached at one position replays correctly at any other. One scalar for the
    /// whole run: multi-line leading stays uniform (lines snap as a group from the first baseline). Y only — X keeps
    /// DirectWrite's sub-pixel advances. Skewed/rotated/flipped worlds (M12 ≠ 0 or M22 ≤ 0) draw unsnapped — there
    /// is no meaningful pixel grid for them. MOTION-GATED at the call sites: a run whose world was written this frame
    /// (DrawGlyphRunCmd.InMotion — scroll/fling/drag/FLIP) draws unsnapped so it rides sub-pixel with its plate instead
    /// of hopping a device row at every half-pixel crossing; the host's settle frame re-snaps it crisp at rest.</summary>
    private static float SnapDy(ReadOnlySpan<ShapedGlyph> glyphs, in Affine2D world, float dpiScale)
    {
        if (glyphs.Length == 0 || world.M12 != 0f || world.M22 <= 0f) return 0f;
        float devY = (world.M22 * glyphs[0].DstY + world.Dy) * dpiScale;
        return (MathF.Round(devY) - devY) / (world.M22 * dpiScale);
    }

    /// <summary>Emit cached local-space quads into <paramref name="outList"/>, applying the per-frame color/transform/opacity
    /// and the run's <see cref="SnapDy"/> correction. Allocation-free (appends into the reused glyph-instance list) —
    /// the steady-state path for unchanged text.
    /// <paramref name="colors"/> (span runs): the per-quad span tint — A==0 inherits <paramref name="color"/>;
    /// <paramref name="forceColor"/> repaints every quad in <paramref name="color"/> regardless (the recorder's
    /// selected-text recolor re-emit, which must override span colors like WinUI's selection repaint).</summary>
    private static void Replay(ReadOnlySpan<ShapedGlyph> glyphs, ColorF[]? colors, bool forceColor, ColorF color, Affine2D world, float opacity, float snapDy, List<GlyphInstance> outList, ReadOnlySpan<float> perGlyphDy = default, ReadOnlySpan<float> perGlyphScale = default)
    {
        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly var s = ref glyphs[i];
            ColorF c = colors is not null && !forceColor && colors[i].A > 0f ? colors[i] : color;
            float dx = s.DstX, dy = s.DstY + snapDy + (i < perGlyphDy.Length ? perGlyphDy[i] : 0f), dw = s.DstW, dh = s.DstH;
            float sc = i < perGlyphScale.Length ? perGlyphScale[i] : 1f;
            if (sc != 1f)   // scale the glyph quad about its own centre (UV unchanged → the cached bitmap magnifies)
            {
                dx += dw * (1f - sc) * 0.5f; dy += dh * (1f - sc) * 0.5f; dw *= sc; dh *= sc;
            }
            outList.Add(new GlyphInstance
            {
                DstX = dx, DstY = dy, DstW = dw, DstH = dh,
                U0 = s.U0, V0 = s.V0, U1 = s.U1, V1 = s.V1,
                R = c.R, G = c.G, B = c.B, A = c.A,
                M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22, Dx = world.Dx, Dy = world.Dy, Opacity = opacity,
            });
        }
    }

    /// <summary>Emit gradient-glyph instances for the sub-glyph wipe: per glyph, apply the per-glyph float (Dy) + scale pop
    /// to the quad and record its run-local-x extent [gt0,gt1] plus before/after/split/fade — the PS does the per-PIXEL
    /// colour mix, so the wipe boundary cuts THROUGH glyphs (half sung / half unsung), not glyph-by-glyph.</summary>
    private static void ReplayGradient(ReadOnlySpan<ShapedGlyph> glyphs, Affine2D world, float opacity, float snapDy,
        ColorF before, ColorF after, float split, float fade, float total,
        ReadOnlySpan<float> ro0, ReadOnlySpan<float> ro1,
        ReadOnlySpan<float> perGlyphDy, ReadOnlySpan<float> perGlyphScale, List<GradGlyphInstance> outList)
    {
        float inv = 1f / total;
        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly var s = ref glyphs[i];
            float dx = s.DstX, dy = s.DstY + snapDy + (i < perGlyphDy.Length ? perGlyphDy[i] : 0f), dw = s.DstW, dh = s.DstH;
            float sc = i < perGlyphScale.Length ? perGlyphScale[i] : 1f;
            if (sc != 1f) { dx += dw * (1f - sc) * 0.5f; dy += dh * (1f - sc) * 0.5f; dw *= sc; dh *= sc; }
            outList.Add(new GradGlyphInstance
            {
                DstX = dx, DstY = dy, DstW = dw, DstH = dh,
                U0 = s.U0, V0 = s.V0, U1 = s.U1, V1 = s.V1,
                M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22, Dx = world.Dx, Dy = world.Dy,
                BR = before.R, BG = before.G, BB = before.B, BA = before.A,
                AR = after.R, AG = after.G, AB = after.B, AA = after.A,
                // Reading-order extent of this glyph (continuous across wrapped visual lines), 0..1 along the run. The VS
                // lerps gt0→gt1 across the quad's x — within a single glyph reading order is monotonic with x, so the
                // per-pixel wipe through the glyph stays correct.
                Gt0 = ro0[i] * inv, Gt1 = ro1[i] * inv,
                Split = split, Fade = fade, Opacity = opacity,
            });
        }
    }

    /// <summary>Shape + lay out a run via the DirectWrite layout engine (itemize → shape → wrap/trim → position, with
    /// kerning/ligatures/complex-script/BiDi), then rasterize each POST-shaping glyph (by glyph id) into the atlas and
    /// bake its local-space quad. The engine drives BOTH this render path and the measure path, so they layout identically.
    /// <paramref name="spans"/> (rtb-01 inline runs): the same one-flow layout with per-range face/weight/size; each
    /// glyph rasterizes at ITS shaped size (LaidGlyph.Size) and <paramref name="colorsOut"/> (non-null for span runs)
    /// receives the per-quad span color (A==0 = inherit), parallel to <paramref name="outList"/>.</summary>
    private void ShapeInto(string text, string family, float size, int weight, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, float dpiScale, List<ShapedGlyph> outList,
        ReadOnlySpan<SpanStyle> spans = default, List<ColorF>? colorsOut = null)
    {
        _engine.Layout(text.AsSpan(), family ?? "", weight, size, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, spans, _liveness);
        float inv = 1f / dpiScale;
        // If the atlas generation resets mid-run (PackOrReset), quads already baked this pass hold stale UVs —
        // re-shape the whole run into the fresh generation so a cached run is always generation-consistent.
        // Bounded: a restarted pass packs into an empty 2048² atlas; one run can't fill it (guard at 3 just in case).
        int epoch, restarts = 0;
        do
        {
            epoch = _atlasEpoch;
            outList.Clear();
            colorsOut?.Clear();
            foreach (var lg in _engine.Glyphs)
            {
                if (lg.Face == 0) continue;
                var ge = GetGlyphByGid((IDWriteFontFace*)lg.Face, lg.Gid, lg.Size > 0f ? lg.Size : size, dpiScale);
                if (ge.W > 0 && ge.H > 0)
                {
                    outList.Add(new ShapedGlyph
                    {
                        DstX = originX + lg.X + ge.BearingX * inv, DstY = topY + lg.Y + ge.BearingY * inv,
                        DstW = ge.W * inv, DstH = ge.H * inv,
                        U0 = ge.X / (float)ATLAS, V0 = ge.Y / (float)ATLAS, U1 = (ge.X + ge.W) / (float)ATLAS, V1 = (ge.Y + ge.H) / (float)ATLAS,
                    });
                    colorsOut?.Add(lg.Span >= 0 && lg.Span < spans.Length ? spans[lg.Span].Color : default);
                }
            }
        } while (epoch != _atlasEpoch && ++restarts < 3);
    }

    private int FaceId(nint face) { if (_faceIds.TryGetValue(face, out int id)) return id; id = _faceIds.Count + 1; _faceIds[face] = id; return id; }

    // Rasterize one POST-shaping glyph (by glyph id, not codepoint) at the physical size into the atlas. The face comes
    // from the layout engine; DWrite factories are process-shared, so _dw and the engine's factory are the same object.
    private GlyphEntry GetGlyphByGid(IDWriteFontFace* face, ushort gid, float size, float dpiScale)
    {
        int faceId = FaceId((nint)face);
        int sizeQ = (int)MathF.Round(size);
        int scaleQ = (int)MathF.Round(dpiScale * 100f);
        // Weight 0 here is correct: the face id already encodes the (family, numeric weight) the engine resolved.
        var key = new GlyphKey(faceId, sizeQ, scaleQ, 0, gid, ByGid: true);
        if (_cache.TryGetValue(key, out var e)) return e;

        float physEm = size * dpiScale;
        float zeroAdvance = 0f;
        ushort gi = gid;
        DWRITE_GLYPH_RUN run = default;
        run.fontFace = face; run.fontEmSize = physEm; run.glyphCount = 1;
        run.glyphIndices = &gi; run.glyphAdvances = &zeroAdvance; run.glyphOffsets = null;
        run.isSideways = BOOL.FALSE; run.bidiLevel = 0;

        IDWriteGlyphRunAnalysis* analysis;
        // NATURAL_SYMMETRIC (not NATURAL): symmetric AA in BOTH axes. Plain NATURAL anti-aliases only horizontally, which
        // samples out fine horizontal stroke features — the documented blur on CJK faces (e.g. MS Mincho) and large text.
        // SYMMETRIC is the modern default (UWP/WinUI/WPF) and Microsoft's recommendation above ~16 ppem. (dwrite.h docs.)
        Check(_dw->CreateGlyphRunAnalysis(&run, 1.0f, null, DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
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
            PackOrReset(ref e, gray, w, h);
            ArrayPool<byte>.Shared.Return(gray);
            ArrayPool<byte>.Shared.Return(rgb);
            _atlasDirty = true;
        }
        Diag.Count("text.glyph", "rasterized");
        analysis->Release();
        _cache[key] = e;
        return e;
    }

    private float Emit(IDWriteFontFace* face, ushort em, int famId, char ch, float size, int weight, float dpiScale, float pen, float baseline, float inv, List<ShapedGlyph> outList)
    {
        var g = GetGlyph(face, em, famId, ch, size, weight, dpiScale);
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
    private void VerifyAgainstReshape(in ShapedRun cached, string text, string family, float size, int weight, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, float dpiScale, int spanRunId = 0)
    {
        _scratch.Clear();
        var spanRun = spanRunId != 0 ? SpanRunTable.Shared.Resolve(spanRunId) : null;
        ShapeInto(text, family, size, weight, originX, topY, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, dpiScale, _scratch,
            spanRun is not null ? spanRun.Spans : default);
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
        private readonly ushort _em; private readonly int _famId; private readonly float _size; private readonly int _weight; private readonly float _dpi;
        public GlyphAdvanceSource(GlyphRenderer r, IDWriteFontFace* face, ushort em, int famId, float size, int weight, float dpi)
        { _r = r; _face = (nint)face; _em = em; _famId = famId; _size = size; _weight = weight; _dpi = dpi; }
        public float Advance(char ch) => _r.GetGlyph((IDWriteFontFace*)_face, _em, _famId, ch, _size, _weight, _dpi).Advance;
        public float EllipsisAdvance => _r.GetGlyph((IDWriteFontFace*)_face, _em, _famId, '…', _size, _weight, _dpi).Advance;
    }

    private float MeasureRange(IDWriteFontFace* face, ushort em, int famId, string text, int s, int e, float size, int weight, float dpiScale)
        => LineBreaker.MeasureRange(text.AsSpan(), s, e, new GlyphAdvanceSource(this, face, em, famId, size, weight, dpiScale));

    private int FitEllipsis(IDWriteFontFace* face, ushort em, int famId, string text, int s, int e, float size, int weight, float dpiScale, float maxWidth, float ellipsisW)
        => LineBreaker.FitEllipsis(text.AsSpan(), s, e, maxWidth, ellipsisW, new GlyphAdvanceSource(this, face, em, famId, size, weight, dpiScale));

    private static int SkipSpaces(string text, int i) { while (i < text.Length && text[i] == ' ') i++; return i; }

    /// <summary>Greedy line break (shared with the measure path): the exclusive end index of the line starting at
    /// <paramref name="start"/> that fits within <paramref name="maxWidth"/>.</summary>
    private int WrapEnd(IDWriteFontFace* face, ushort em, int famId, string text, int start, int n, float size, int weight, float dpiScale, float maxWidth, int wrap)
        => LineBreaker.WrapEnd(text.AsSpan(), start, n, maxWidth, wrap, new GlyphAdvanceSource(this, face, em, famId, size, weight, dpiScale));

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
        D3D12MemoryDiagnostics.Track(_srvHeap, "Glyph.SrvHeap",
            (ulong)hd.NumDescriptors * device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV));

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

            // Sub-glyph gradient-wipe PSO: identical root sig / input layout / blend, swap in the gradient shaders.
            ID3DBlob* vsg = ShaderCompiler.Compile(HlslGrad, "VSMain", "vs_5_1");
            ID3DBlob* psg = ShaderCompiler.Compile(HlslGrad, "PSMain", "ps_5_1");
            pd.VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vsg->GetBufferPointer(), BytecodeLength = vsg->GetBufferSize() };
            pd.PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = psg->GetBufferPointer(), BytecodeLength = psg->GetBufferSize() };
            ID3D12PipelineState* psog;
            Check(device->CreateGraphicsPipelineState(&pd, __uuidof<ID3D12PipelineState>(), (void**)&psog), "CreateGraphicsPipelineState(glyphGrad)");
            _psoGrad = psog;
            vsg->Release(); psg->Release();
        }
        vs->Release(); ps->Release();

        float* quad = stackalloc float[8] { 0, 0, 1, 0, 0, 1, 1, 1 };
        _quad = CreateUpload(device, sizeof(float) * 8, "Glyph.QuadUpload");
        void* qp; _quad->Map(0, null, &qp); Buffer.MemoryCopy(quad, qp, 32, 32); _quad->Unmap(0, null);
        _quadView = new D3D12_VERTEX_BUFFER_VIEW { BufferLocation = _quad->GetGPUVirtualAddress(), SizeInBytes = 32, StrideInBytes = 8 };

        for (int f = 0; f < FrameCount; f++)
        {
            _instances[f] = CreateUpload(device, (uint)(sizeof(GlyphInstance) * MaxGlyphs), "Glyph.InstanceUpload");
            void* ip; _instances[f]->Map(0, null, &ip); _mapped[f] = (GlyphInstance*)ip;   // persistently mapped
            _gradInstances[f] = CreateUpload(device, (uint)(sizeof(GradGlyphInstance) * MaxGradGlyphs), "Glyph.GradInstanceUpload");
            void* gp; _gradInstances[f]->Map(0, null, &gp); _mappedGrad[f] = (GradGlyphInstance*)gp;
        }
    }

    public void BeginFrame(int frameIndex)
    {
        _active = ((frameIndex % FrameCount) + FrameCount) % FrameCount;   // this frame's instance buffer — already fenced, so no CPU↔GPU race
        _activeGva = _instances[_active]->GetGPUVirtualAddress();
        _activeGradGva = _gradInstances[_active]->GetGPUVirtualAddress();
        _cursor = 0;
        _gradCursor = 0;
        _dropped = 0;
        _frame++;
        _runsCached = 0;
        _runsShaped = 0;
        // Sweep stale shaped runs: every 64 frames at rest, every 8 under churn (a scroll storm fills the cache with
        // dead-id runs — sweeping sooner keeps their pooled quad arrays cycling instead of piling up).
        int stride = _runCache.Count > 2048 ? 7 : 63;
        if ((_frame & stride) == 0) EvictStaleRuns();
    }

    /// <summary>Record one glyph run; <paramref name="rebind"/> false skips the static state (heap, root signature,
    /// PSO, viewport constants, atlas table, topology, quad VB — still bound from a previous glyph run this frame;
    /// see RoundRectPipeline.Record). Returns false when full (state untouched).</summary>
    public bool Record(ID3D12GraphicsCommandList* cmd, List<GlyphInstance> instances, float vpW, float vpH, bool rebind = true)
    {
        int start = _cursor;
        int count = Math.Min(instances.Count, MaxGlyphs - start);
        if (count <= 0) { _dropped += instances.Count; return false; }
        _dropped += instances.Count - count;
        for (int i = 0; i < count; i++) _mapped[_active][start + i] = instances[i];
        _cursor += count;

        if (rebind)
        {
            ID3D12DescriptorHeap* heap = _srvHeap;
            cmd->SetDescriptorHeaps(1, &heap);
            cmd->SetGraphicsRootSignature(_rootSig);
            cmd->SetPipelineState(_pso);
            _vpConstants[0] = vpW;
            _vpConstants[1] = vpH;
            fixed (float* vp = _vpConstants)
                cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
            cmd->SetGraphicsRootDescriptorTable(1, _srvGpu);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView) cmd->IASetVertexBuffers(0, 1, qv);
        }
        cmd->SetGraphicsRootShaderResourceView(2, _activeGva + (ulong)(start * sizeof(GlyphInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return true;
    }

    /// <summary>Draw the sub-glyph gradient-wipe instances (active lyric line + glow) with the gradient PSO — same atlas,
    /// viewport, quad and double-buffering as <see cref="Record"/>, into whatever RT is bound (so a blur layer captures the
    /// glow's gradient glyphs exactly like normal glyphs).</summary>
    public bool RecordGradient(ID3D12GraphicsCommandList* cmd, List<GradGlyphInstance> instances, float vpW, float vpH, bool rebind = true)
    {
        int start = _gradCursor;
        int count = Math.Min(instances.Count, MaxGradGlyphs - start);
        if (count <= 0) { _dropped += instances.Count; return false; }
        _dropped += instances.Count - count;
        for (int i = 0; i < count; i++) _mappedGrad[_active][start + i] = instances[i];
        _gradCursor += count;

        if (rebind)
        {
            ID3D12DescriptorHeap* heap = _srvHeap;
            cmd->SetDescriptorHeaps(1, &heap);
            cmd->SetGraphicsRootSignature(_rootSig);
            cmd->SetPipelineState(_psoGrad);
            _vpConstants[0] = vpW;
            _vpConstants[1] = vpH;
            fixed (float* vp = _vpConstants)
                cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
            cmd->SetGraphicsRootDescriptorTable(1, _srvGpu);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView) cmd->IASetVertexBuffers(0, 1, qv);
        }
        cmd->SetGraphicsRootShaderResourceView(2, _activeGradGva + (ulong)(start * sizeof(GradGlyphInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0);
        return true;
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
        for (int f = 0; f < FrameCount; f++)
            if (_instances[f] != null) { _instances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances[f], "Glyph.InstanceUpload"); _instances[f]->Release(); _instances[f] = null; }
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "Glyph.QuadUpload"); _quad->Release(); _quad = null; }
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "Glyph.SrvHeap"); _srvHeap->Release(); }
        if (_texUpload != null) { D3D12MemoryDiagnostics.Release(_texUpload, "Glyph.AtlasUpload"); _texUpload->Release(); _texUpload = null; }
        if (_tex != null) { D3D12MemoryDiagnostics.Release(_tex, "Glyph.AtlasTexture"); _tex->Release(); _tex = null; }
        foreach (var f in _faces.Values) if (f != 0) ((IDWriteFontFace*)f)->Release();
        _faces.Clear();
        if (_dw != null) _dw->Release();
    }
}
