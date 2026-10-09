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
/// <summary>One cached local-space glyph quad. <see cref="V0"/>/<see cref="V1"/> address sub-pixel phase 0; a replay at
/// phase <c>p</c> adds <c>p * VStride</c> to both, which is why the phase stack is packed in ONE atlas slot
/// (<c>SubPixelPhases</c> variants stacked vertically, one transparent gutter row between them — <c>PhaseStride</c>). That keeps the shaped-run cache phase-AGNOSTIC — the alternative,
/// keying runs by phase, would multiply the cache by the phase count and re-shape on every sub-pixel crossing.</summary>
internal struct ShapedGlyph { public float DstX, DstY, DstW, DstH, U0, V0, U1, V1, VStride; }

/// <summary>One COLR v0 layer of a colour glyph (emoji), as <c>TranslateColorGlyphRun</c> reports it: the layer's own
/// glyph id in the SAME face (a plain outline, rasterized through the ordinary alpha atlas), its offset from the base
/// glyph's pen position in EM (size-independent, so one cache entry serves every size), and its fill — the CPAL palette
/// colour, or <see cref="Foreground"/> (palette index 0xFFFF) meaning "the run colour" (baked as A==0 = inherit).</summary>
internal readonly record struct ColorLayer(ushort Gid, float DxEm, float DyEm, ColorF Color, bool Foreground);

/// <summary>A fully shaped text run cached by content (see <see cref="RunKey"/>): the local-space quads + an LRU stamp.
/// Allocated only on a cache miss (content change); replayed allocation-free on every steady-state frame.
/// <see cref="Colors"/> (span runs and colour-emoji runs, parallel to <see cref="Glyphs"/>): the per-quad color
/// override — A==0 entries inherit the replayed command color; null = a plain uniform run (the overwhelming case).
/// A colour emoji contributes one quad PER PALETTE LAYER, each carrying its CPAL colour here.</summary>
internal struct ShapedRun { public ShapedGlyph[] Glyphs; public ColorF[]? Colors; public int Count; public int LastUsedFrame; }

/// <summary>Content key for the shaped-run cache. Keyed on the interned <see cref="StringId"/> handles (stable across frames,
/// no per-frame string hashing) + quantized layout inputs — including EVERY shaping input of the glyph op (numeric Weight,
/// CharacterSpacing ×10, LineHeight ×10, packed LineStacking|LineBounds, the SpanRunId inline-run overlay — a span style
/// change mints a fresh id upstream, so the key self-invalidates), so two runs differing only in weight or tracking
/// can never alias. Excludes color/transform/opacity (replayed). Bounds origin and width are layout-stable for a given
/// element (scroll rides the world transform, not the bounds), so they can key safely — and they key EXACTLY (their float bits), never rounded: see MakeRunKey.</summary>
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
    // Start with 2048² R8 (4 MiB each CPU/GPU); grow once to the former 4096² capacity during preflight,
    // before any instance or upload is recorded. Four subpixel phases are unchanged. No automatic shrink:
    // multilingual/zoom navigation must not repeatedly discard useful coverage. Uploads remain dirty-row bands.
    private const int InitialAtlasEdge = 2048;
    private const int MaximumAtlasEdge = 4096;
    private int ATLAS => _atlas.Size;

    /// <summary>Vertical sub-pixel positions each glyph is rasterized at. Text can then be placed on a 1/N device-row
    /// grid and stay CRISP, instead of choosing between a whole-pixel snap (which quantizes all scrolling to the device
    /// grid — at DPI scale 1.0 that is a full 1 DIP, and 26% of slow-scroll frames displayed a literal zero step) and
    /// drawing unsnapped (which samples an integer-baseline bitmap at a fractional device Y through a LINEAR sampler and
    /// smears every glyph across two rows — the literal "motion blur while scrolling, sharp when it stops").
    ///
    /// <para>4 phases ⇒ worst-case placement error 1/8 device pixel, below the visual threshold, and the same count
    /// Skia has long used for sub-pixel text positioning. The cost is per-glyph atlas AREA, not per-glyph count: each
    /// entry is <c>SubPixelPhases</c>× taller, which is why <see cref="ATLAS"/> went 2048 → 4096 in the same change
    /// (4× the area exactly absorbs it; R8, so 16 MB). Raising this without raising the atlas trades crispness for
    /// eviction thrash.</para></summary>
    internal const int SubPixelPhases = 4;

    /// <summary>Baseline Y offset in DEVICE pixels for phase <paramref name="p"/> (positive = down).</summary>
    private static float PhaseOffset(int p) => p / (float)SubPixelPhases;

    /// <summary>Rows from one phase's first row to the next phase's: the ink height plus ONE transparent gutter row. The
    /// phases used to be stacked flush, so under LINEAR sampling any quad that is not texel-exact (scaled or animated
    /// text — the lyrics) blended the bottom row of phase p with the TOP row of phase p+1: the top strokes of the next
    /// variant showed up as short "underlines" beneath v, n, m… (and its bottom row above the glyph, from p−1).</summary>
    private static int PhaseStride(int h) => h + 1;

    /// <summary>The packed slot's height: <see cref="SubPixelPhases"/> phases, a gutter row between each pair. The atlas
    /// packer's own 1-texel apron covers the outer edges.</summary>
    private static int PhaseStackHeight(int h) => h * SubPixelPhases + (SubPixelPhases - 1);

    private IDWriteFactory* _dw;
    private const string DefaultFamily = "Segoe UI";
    // Per-(family, numeric weight) faces + metrics, resolved on demand. Family = a system name OR "path.ttf#Family Name".
    private readonly Dictionary<(string fam, int weight), nint> _faces = new();
    private readonly Dictionary<(string fam, int weight), FaceMetrics> _faceMetrics = new();
    private readonly Dictionary<string, int> _famIds = new();

    // The R8 mirror + the append-only shelf packer + the DIRTY-ROW REGION that bounds every upload, all in one
    // backend-agnostic, COM-free class (FluentGpu.Text.GlyphAtlasStore). Its class doc carries the three-clause
    // region-tracking invariant this renderer's UploadIfDirty depends on; read it before touching the upload path.
    private GlyphAtlasStore _atlas = new(InitialAtlasEdge);
    private Action? _prepareGrowthFence;
    private bool _preparing;
    private bool _growthFailed;
    private ID3D12Resource* _tex;
    // Atlas staging, BANKED per frame-in-flight (FrameCount deep). WaitForFrame only proves frame
    // N−FrameCount retired, never N−1, so consecutive dirty-atlas frames raced a single shared staging buffer —
    // latent at frame latency 1, live at 2. Each bank holds a CONTIGUOUS full-width row band of the atlas (never the
    // whole atlas), so a bank of R rows costs R·ATLAS bytes and grows only when a frame actually needs more.
    private readonly ID3D12Resource*[] _texUpload = new ID3D12Resource*[FrameCount];
    // Rows each bank can stage. 1024 rows = 4 MiB: a COLD first frame of an app shell rasterizes ~400–700 rows of
    // shelves (one shelf per (size, weight) ≈ SubPixelPhases × the ink height), so this lands a first paint whole
    // without a growth round-trip. The cap is 2048 rows = 8 MiB per bank: a frame that dirties more than that (a
    // generational reset re-rasterizing a huge working set) drains across two frames, arming a repaint — bounded
    // memory beats a bank permanently ratcheted to the full 16 MiB atlas.
    // After the existing clean-frame window, the warm floor is 256 rows (1 MiB/bank): 9 MiB less pinned upload
    // memory across three banks. New demands grow again through the unchanged clamped-upload healing path.
    private const int InitialStagingRows = GlyphStagingPolicy.InitialRows;
    private const int MaxStagingRows = GlyphStagingPolicy.MaxRows;
    private readonly int[] _stagingRows = new int[FrameCount];
    private int _wantStagingRows = InitialStagingRows;
    /// <summary>Submitted frames since the atlas was last dirty. Drives the staging reserve back down — the same
    /// tier-gated idle-window shape LayerTargetPool uses for its render targets, and generous for the same reason.</summary>
    private int _atlasIdleFrames;
    /// <summary>Idle frames before a grown staging reserve is handed back. ~1 s at 120 Hz on a weak (UMA) adapter
    /// where the memory matters most, five times that elsewhere — mirroring LayerTargetPool's trim windows.</summary>
    private static int StagingIdleFrames => GpuProfile.IsWeak ? 120 : 600;
    private bool _uploadBacklog;              // a flush left rows dirty (staging short) — this frame's text is not faithful
    private ID3D12DescriptorHeap* _srvHeap;
    private D3D12_GPU_DESCRIPTOR_HANDLE _srvGpu;
    private bool _texInitialized;

    private readonly Dictionary<GlyphKey, GlyphEntry> _cache = new();
    // ThemedIcon coverage masks packed into the SAME R8 atlas as glyphs, keyed by (interned PathId, device px). A
    // SEPARATE dictionary from _cache so an icon entry can never alias a GlyphKey (structurally disjoint — the design's
    // "reserved key space", realized as its own map). Dropped on a generational atlas reset like _cache/_runCache.
    private readonly Dictionary<(int PathId, int W, int H), GlyphEntry> _iconCache = new();
    // Itemize→shape→wrap layout (shared logic with the measure path, so render and measure layout identically).
    private TextLayoutEngine _engine = null!;
    private readonly Dictionary<nint, int> _faceIds = new();   // IDWriteFontFace* → small int for the glyph-cache key

    // ── Colour glyphs (COLR/CPAL emoji) — run-cache MISS path only, never touched at replay ─────────────────────
    // The layout engine already isolates emoji into a Segoe UI Emoji sub-run (TextLayoutEngine.ResolveRunFace); what
    // was missing is the raster side: CreateGlyphRunAnalysis of the BASE glyph yields its monochrome fallback outline,
    // so emoji drew as run-coloured silhouettes. TranslateColorGlyphRun decomposes the base glyph into its COLR v0
    // layers — each a plain outline glyph in the same face plus a CPAL colour — and ShapeInto bakes one quad per layer
    // through the SAME R8 atlas / GetGlyphByGid path, pushing the layer colour into the run's per-quad Colors (the
    // span-run mechanism Replay already honours). Layout, measure, GlyphKey, the atlas and RunKey are all colour-blind
    // and unchanged. COLR v1 paint trees (gradients, transforms) are OUT OF SCOPE: DWrite reports them only through
    // the DWRITE_GLYPH_IMAGE_FORMATS_COLR_PAINT_TREE format, which has no layer decomposition — the flat v0 look is
    // the target, and a glyph with only a v1 description falls back to today's monochrome outline.
    private IDWriteFactory2* _dw2;   // TranslateColorGlyphRun (COLR v0 layers); null on a pre-8.1 DirectWrite
    private IDWriteFactory4* _dw4;   // the image-format-aware variant — the fallback when Factory2 reports NOCOLOR
    private readonly Dictionary<nint, bool> _faceIsColor = new();          // IDWriteFontFace* → IDWriteFontFace2.IsColorFont
    private readonly Dictionary<long, ColorLayer[]> _colorLayers = new();   // (faceId << 16 | gid) → layers (empty = no colour)
    private readonly List<ColorLayer> _layerScratch = new(16);              // reused enumerator output buffer (miss path)
    private static readonly ColorLayer[] NoLayers = [];
    /// <summary>DWrite's "this glyph run has no colour information" — the expected answer for every non-emoji glyph
    /// of a colour face (letters in Segoe UI Emoji's Latin range) and for a font with no COLR table at all.</summary>
    private const int DWRITE_E_NOCOLOR = unchecked((int)0x8898500C);

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
    private const long MaxPooledQuadBytes = 1024 * 1024;
    private long _pooledQuadBytes;
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
    private const int FrameCount = D3D12Device.FrameBankDepth;   // banked per frame-in-flight (depth = D3D12Device.FrameBankDepth) so frame N's CPU writes never race the GPU reads of the frames still in flight
    private readonly ID3D12Resource*[] _instances = new ID3D12Resource*[FrameCount];
    private readonly GlyphInstance*[] _mapped = new GlyphInstance*[FrameCount];
    // The per-frame glyph instance bank STARTS at 8192 quads and GROWS on demand: a frame that overflows it records
    // the tail as dropped (Record), remembers the size it needed (_wantCapacity), and each bank re-allocates itself the
    // next time BeginFrame lands on it — that bank is fenced at BeginFrame, so no in-flight reader can see the swap.
    // The device turns a dropped frame into an un-skippable full repaint (TextRepaintPending), so an overflow costs one
    // frame with a blank tail and then heals. It used to be a hard cap: a 600-line log preview scrolled into a dialog
    // silently blanked every glyph recorded after it (the checkbox + command-row labels) for as long as the stream
    // stayed byte-identical. Capped at MaxGlyphCapacity (80 B × 262144 = 20 MB per bank).
    private const int InitialGlyphCapacity = 8192;
    private const int MaxGlyphCapacity = 262144;
    private readonly int[] _capacity = new int[FrameCount];
    private int _wantCapacity = InitialGlyphCapacity;
    /// <summary>Quads the active bank can hold this frame (grows across frames after an overflow).</summary>
    public int GlyphCapacity => _capacity[_active];
    private int _cursor;
    private int _active;
    private ulong _activeGva;
    private int _dropped;

    // Sub-glyph gradient-wipe path: a second PSO (per-pixel before→after fill) with its own instance buffers. Only the
    // active lyric line + its glow feed it (~tens of glyphs), so a small cap; normal text never touches it.
    private ID3D12PipelineState* _psoGrad;
    // Tier-3 stencil path clip (gpu-renderer.md S6): EQUAL-tested clones of BOTH glyph passes, built lazily on the
    // first stencil scope. Glyph coverage also covers DrawIconMask, which rides this atlas/PSO.
    private ID3D12PipelineState* _psoStencilTest, _psoGradStencilTest;
    private bool _stencilTried;
    private ID3D12Device* _device;   // non-owning; the device outlives every pipeline
    private const int MaxGradGlyphs = 1024;
    private readonly ID3D12Resource*[] _gradInstances = new ID3D12Resource*[FrameCount];
    private readonly GradGlyphInstance*[] _mappedGrad = new GradGlyphInstance*[FrameCount];
    private int _gradCursor;
    private ulong _activeGradGva;
    // Gradient-budget visibility. Overflow used to be SILENT: RecordGradient clamped to MaxGradGlyphs and folded the
    // remainder into the SHARED _dropped total, so a truncated wipe (the tail of a lyric line simply not painted) was
    // indistinguishable from every other pipeline's drop. These two are cheap (a few int ops per RECORD, not per glyph)
    // so they stay compiled in like _dropped; the loud reporting on top of them is [Conditional]-erased on Release.
    private int _gradDropped;    // gradient instances dropped THIS frame (0 = healthy)
    private int _gradPeak;       // session high-water of gradient instances used in ONE frame, vs MaxGradGlyphs

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
    public long AtlasNonZero => _atlas.NonZeroTexels;
    /// <summary>Sub-glyph WIPE instances dropped this frame because the <c>MaxGradGlyphs</c> budget was exhausted.
    /// Non-zero = a karaoke line rendered TRUNCATED; the budget (or the settled-split fast path) needs attention.</summary>
    public int GradInstancesDropped => _gradDropped;
    /// <summary>Session high-water mark of sub-glyph WIPE instances used in one frame, against the fixed
    /// <c>MaxGradGlyphs</c> budget — the headroom probe for the lyrics surfaces.</summary>
    public int GradInstancePeak => _gradPeak;
    /// <summary>The fixed per-frame sub-glyph WIPE instance budget (the denominator for <see cref="GradInstancePeak"/>).</summary>
    public static int GradInstanceBudget => MaxGradGlyphs;

    // ── MemCensus accessors (O(1), or a tiny fixed-bucket sum) ────────────────────────────────────
    /// <summary>Cached rasterized glyph entries (the glyph atlas cache) — O(1) census.</summary>
    internal int CachedGlyphCount => _cache.Count;
    /// <summary>Cached shaped text runs (the run cache) — O(1) census.</summary>
    internal int CachedRunCount => _runCache.Count;
    /// <summary>Atlas generation-reset count (the epoch counter; bumped when the atlas fills and flushes) — O(1).</summary>
    internal int AtlasResetCount => _atlas.Epoch;
    internal int AtlasEdge => ATLAS;
    internal int AtlasOccupiedRows => _atlas.OccupiedRowCount;
    internal long AtlasCpuBytes => (long)ATLAS * ATLAS;
    internal long QuadPoolBytes => _pooledQuadBytes;
    /// <summary>Bytes each per-frame atlas staging bank currently holds (a dirty-row band, NOT the whole atlas) —
    /// the census figure that used to read a flat 3 × ATLAS².</summary>
    internal long AtlasStagingBytes
    {
        get { long n = 0; for (int i = 0; i < _stagingRows.Length; i++) n += (long)_stagingRows[i] * ATLAS; return n; }
    }
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
        _device = device;
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
        // Colour-glyph translation (best effort: null ⇒ emoji keep rendering as monochrome outlines, nothing else changes).
        IDWriteFactory2* f2;
        if ((int)_dw->QueryInterface(__uuidof<IDWriteFactory2>(), (void**)&f2) >= 0 && f2 != null) _dw2 = f2;
        IDWriteFactory4* f4;
        if ((int)_dw->QueryInterface(__uuidof<IDWriteFactory4>(), (void**)&f4) >= 0 && f4 != null) _dw4 = f4;
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

    /// <summary>The glyph realization's size key: the DEVICE em in 10.6 fixed point (canon text.md §3.1 "SizePx: size
    /// AFTER DPI scale, quantized"). The bitmap is rasterized from THIS value, so "same key ⇒ same pixels" holds by
    /// construction. It used to be <c>Round(size)</c> — a whole DIP, on the wrong side of the DPI multiply — while the
    /// bitmap was rasterized at the exact <c>size * dpiScale</c>: 13.5 and 14 DIP (20.25 vs 21 px at 150 %) shared one
    /// key, so whichever run rasterized a glyph id FIRST decided the bitmap, bearings and cell height every other size
    /// in the bucket got. A word then mixed two em sizes — the late-claimed letters (g, p, y…) 4–10 % larger, one device
    /// row higher and one row taller than their neighbours ("aesPa", "leaGue").</summary>
    private static int DeviceEmQ(float size, float dpiScale) => (int)MathF.Round(size * dpiScale * 64f);

    // Rasterize at the PHYSICAL size (size * dpiScale) so glyphs are crisp when drawn into a DIP-sized quad at high DPI.
    private GlyphEntry GetGlyph(IDWriteFontFace* face, ushort em, int famId, char ch, float size, int weight, float dpiScale)
    {
        int sizeQ = DeviceEmQ(size, dpiScale);
        int scaleQ = (int)MathF.Round(dpiScale * 100f);
        var key = new GlyphKey(famId, sizeQ, scaleQ, weight, ch, ByGid: false);
        if (_cache.TryGetValue(key, out var e)) return e;

        uint cp = ch;
        ushort gi;
        face->GetGlyphIndices(&cp, 1, &gi);

        DWRITE_GLYPH_METRICS gm;
        face->GetDesignGlyphMetrics(&gi, 1, &gm, BOOL.FALSE);
        float advance = gm.advanceWidth * (size / em);            // advance in DIP (scale-independent)
        float physEm = sizeQ * (1f / 64f);                         // rasterize FROM THE KEY (see DeviceEmQ)

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

        // NATURAL_SYMMETRIC (not NATURAL): symmetric AA in BOTH axes. Plain NATURAL anti-aliases only horizontally, which
        // samples out fine horizontal stroke features — the documented blur on CJK faces (e.g. MS Mincho) and large text.
        // SYMMETRIC is the modern default (UWP/WinUI/WPF) and Microsoft's recommendation above ~16 ppem. (dwrite.h docs.)
        // NATURAL (antialiased) rendering mode → must query the CLEARTYPE_3x1 texture (ALIASED_1x1 returns empty bounds here).
        // H is the PER-PHASE height; the packed slot is SubPixelPhases× that.
        bool hasInk = TryRasterizePhaseStack(&run, out RECT bounds, out int w, out int h, out byte[] stack);
        if (!hasInk) { bounds = default; w = 0; h = 0; }

        e = new GlyphEntry { Advance = advance, BearingX = bounds.left, BearingY = bounds.top, W = w, H = h };
        Diag.Set("text.glyph", "last", $"ch='{ch}' gi={gi} {w}x{h}x{SubPixelPhases} adv={advance:0.0}");
        if (w > 0 && h > 0) PackOrReset(ref e, stack, w, PhaseStackHeight(h));   // a successful pack marks its rows dirty
        Diag.Count("text.glyph", "rasterized");
        _cache[key] = e;
        return e;
    }

    /// <summary>Rasterize <see cref="SubPixelPhases"/> vertically-offset variants of one glyph run and stack them into a
    /// single R8 image of <c>w × (h · SubPixelPhases)</c>, phase p occupying rows <c>[p·h, (p+1)·h)</c>.
    ///
    /// <para>All phases share ONE box — the union of their individual alpha-texture bounds — so every variant is aligned
    /// to the same origin and differs only by the sub-pixel shift. That is what lets the replay select a phase with a
    /// single V offset instead of per-phase bearings. The union is taken rather than "phase 0 plus a row" because a
    /// sub-pixel shift can move the reported bounds on either edge, and <c>CreateAlphaTexture</c> is asked for each
    /// phase's OWN rect (passing a wider rect than the analysis reported is not contractually safe) and blitted into
    /// place.</para>
    ///
    /// <para>Returns false for a glyph with no coverage at any phase (space, control) — the caller caches a zero-size
    /// entry as before. Cache-miss path only: 2N analyses per glyph, never per frame.</para></summary>
    private bool TryRasterizePhaseStack(DWRITE_GLYPH_RUN* run, out RECT box, out int w, out int h, out byte[] stack)
    {
        box = default; w = 0; h = 0; stack = [];
        RECT union = default;
        bool any = false;
        for (int p = 0; p < SubPixelPhases; p++)
        {
            IDWriteGlyphRunAnalysis* a;
            Check(_dw->CreateGlyphRunAnalysis(run, 1.0f, null, DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL, 0f, PhaseOffset(p), &a), "CreateGlyphRunAnalysis");
            RECT b;
            int hr = (int)a->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &b);
            a->Release();
            Check(hr, "GetAlphaTextureBounds");
            if (b.right <= b.left || b.bottom <= b.top) continue;
            if (!any) { union = b; any = true; }
            else
            {
                if (b.left < union.left) union.left = b.left;
                if (b.top < union.top) union.top = b.top;
                if (b.right > union.right) union.right = b.right;
                if (b.bottom > union.bottom) union.bottom = b.bottom;
            }
        }
        if (!any) return false;

        box = union;
        w = union.right - union.left;
        h = union.bottom - union.top;
        stack = new byte[w * PhaseStackHeight(h)];                 // zero-filled: the gutter rows stay transparent

        for (int p = 0; p < SubPixelPhases; p++)
        {
            IDWriteGlyphRunAnalysis* a;
            Check(_dw->CreateGlyphRunAnalysis(run, 1.0f, null, DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL, 0f, PhaseOffset(p), &a), "CreateGlyphRunAnalysis");
            RECT b;
            int hrB = (int)a->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &b);
            if (hrB < 0) { a->Release(); Check(hrB, "GetAlphaTextureBounds"); }
            int bw = b.right - b.left, bh = b.bottom - b.top;
            if (bw <= 0 || bh <= 0) { a->Release(); continue; }
            byte[] rgb = ArrayPool<byte>.Shared.Rent(bw * bh * 3);
            int hrT;
            fixed (byte* pr = rgb)
                hrT = (int)a->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &b, pr, (uint)(bw * bh * 3));
            a->Release();
            if (hrT >= 0)
            {
                int dstBase = p * w * PhaseStride(h) + (b.top - union.top) * w + (b.left - union.left);
                for (int row = 0; row < bh; row++)
                {
                    int src = row * bw * 3, dst = dstBase + row * w;
                    for (int x = 0; x < bw; x++)
                        stack[dst + x] = (byte)((rgb[src + 3 * x] + rgb[src + 3 * x + 1] + rgb[src + 3 * x + 2]) / 3);
                }
            }
            ArrayPool<byte>.Shared.Return(rgb);
            Check(hrT, "CreateAlphaTexture");
        }
        return true;
    }

    /// <summary>Shelf-pack one rasterized glyph into the atlas mirror (and mark its rows dirty for the next upload
    /// flush). False = atlas full — the caller must NOT cache the entry as-is (an unpacked entry keeps X=Y=0 and would
    /// sample the atlas origin); it resets the atlas generation and retries.</summary>
    private bool TryPack(ref GlyphEntry e, ReadOnlySpan<byte> src, int w, int h)
    {
        if (!_atlas.TryPack(src, w, h, out int x, out int y)) return false;   // atlas full → generational reset (ResetAtlas)
        e.X = x; e.Y = y;
        return true;
    }

    /// <summary>Pack with overflow recovery. A full atlas DEFERS the generational flush to the next <see cref="BeginFrame"/>
    /// (text.md §5.3: eviction only at frame START) — resetting mid-record would re-assign cells underneath UVs already
    /// baked/emitted this frame, so quads (and cached runs, and retained tiles) would sample the WRONG glyph. Instead
    /// the failed glyph renders as NOTHING for this one frame (no quad); the boundary reset in BeginFrame clears
    /// _cache/_iconCache/_runCache so everything re-rasterizes into the fresh generation next frame, and
    /// <see cref="AtlasResetPending"/> tells the device to force that next frame to actually happen (not be skip-submitted
    /// or kept as a faithful retained tile).</summary>
    private void PackOrReset(ref GlyphEntry e, ReadOnlySpan<byte> src, int w, int h)
    {
        if (_preparing && !_growthFailed && ATLAS < MaximumAtlasEdge && !_atlas.CanPack(w, h)
            && w <= MaximumAtlasEdge - 2 && h <= MaximumAtlasEdge - 2)
            TryGrowAtlas();
        // A single glyph larger than the whole atlas can never pack — blank it without arming a reset, or an
        // oversized glyph would trigger a full cache flush every single frame it is on screen.
        if (w + 2 > ATLAS || h + 2 > ATLAS)
        {
            e.X = 0; e.Y = 0; e.W = 0; e.H = 0;
            Diag.Set("text.atlas", "oversized-glyph", $"{w}x{h} > {ATLAS}");
            return;
        }
        if (TryPack(ref e, src, w, h)) return;
        if (!_resetPending)
        {
            _resetPending = true;
            Diag.Count("text.atlas", "overflow-deferred");
            Diag.Event("text.atlas", $"atlas full at {ATLAS}x{ATLAS} — generation reset deferred to next frame");
        }
        e.X = 0; e.Y = 0; e.W = 0; e.H = 0;   // render nothing this frame rather than a stale/wrong glyph
    }

    /// <summary>The frame currently being recorded does NOT faithfully render its text, for one of two reasons, and
    /// either way the device must not skip-submit it and must not treat its tiles as faithful retained content:
    /// <list type="bullet">
    /// <item>the atlas OVERFLOWED while this frame was shaped — some newly-requested glyphs were emitted blank and the
    /// generational flush runs at the next <see cref="BeginFrame"/> (the original meaning of the name); or</item>
    /// <item>an upload flush was STAGING-SHORT — the tail of this frame's dirty row band did not fit the staging bank,
    /// so glyphs packed into those rows sample not-yet-uploaded texels. The bank grows at the next
    /// <see cref="BeginFrame"/> and the band drains; the forced repaint is what makes it invisible.</item>
    /// </list></summary>
    internal bool AtlasResetPending => _resetPending || _uploadBacklog;
    private bool _resetPending;

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
        // A successful pack marks its rows dirty; may trigger a generational reset (which clears _iconCache) — we add AFTER.
        if (w > 0 && h > 0) PackOrReset(ref e, src, w, h);
        _iconCache[(pathId, w, h)] = e;
        IconUv(in e, out u0, out v0, out u1, out v1);
    }

    private void IconUv(in GlyphEntry e, out float u0, out float v0, out float u1, out float v1)
    {
        u0 = e.X / (float)ATLAS; v0 = e.Y / (float)ATLAS;
        u1 = (e.X + e.W) / (float)ATLAS; v1 = (e.Y + e.H) / (float)ATLAS;
    }

    /// <summary>Generational flush. The GPU texture is deliberately NOT cleared: the reset drops every cached UV, and
    /// the fresh generation re-uploads (full width) every row it lands on, so the previous generation's ink is
    /// unreachable — see the GlyphAtlasStore region invariant, clause 1.</summary>
    private void ResetAtlas()
    {
        _atlas.Reset();
        _cache.Clear();
        _iconCache.Clear();
        foreach (var kv in _runCache) ReturnQuads(kv.Value.Glyphs);
        _runCache.Clear();
        Diag.Count("text.atlas", "generation-reset");
        Diag.Event("text.atlas", $"generation reset #{_atlas.Epoch} (atlas full at {ATLAS}x{ATLAS})");
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
        int spanRunId = 0, bool forceColor = false, float motionSoft = 0f)
    {
        var key = MakeRunKey(textId, familyId, size, weight, maxWidth, wrap, trim, maxLines, originX, topY, dpiScale, charSpacing, lineHeight, lineStacking, lineBounds, spanRunId);

        ref var hit = ref CollectionsMarshal.GetValueRefOrNullRef(_runCache, key);
        if (!Unsafe.IsNullRef(ref hit))
        {
            hit.LastUsedFrame = _frame;
            if (_preparing) return;
            _runsCached++;
#if DEBUG
            if (VerifyCache) VerifyAgainstReshape(in hit, text, family, size, weight, originX, topY, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, dpiScale, spanRunId);
#endif
            var quads = hit.Glyphs.AsSpan(0, hit.Count);
            Replay(quads, hit.Colors, forceColor, color, world, opacity, dpiScale, motionSoft, outList);
            return;
        }

        // Miss (new/changed run): shape once into the scratch buffer, cache the baked local-space quads, then replay.
        // The quad array is POOLED (returned on eviction) so scroll-storms of fresh text don't churn Gen0 per run.
        // Span runs (rtb-01): the SpanRunTable overlay restyles ranges of the SAME flow; per-quad span colors bake
        // into a parallel Colors array (allocated on the miss only — a span style change minted a fresh id anyway).
        // The colour scratch is passed for EVERY run (not only span runs) because a colour emoji's palette layers
        // recolour quads of a plain run too; ColorGlyphBake.RetainColors keeps the plain all-inherit case at
        // Colors = null, so the uniform run still pays nothing at replay.
        var spanRun = spanRunId != 0 ? SpanRunTable.Shared.Resolve(spanRunId) : null;
        _scratch.Clear();
        _colorScratch.Clear();
        bool consistent = ShapeInto(text, family, size, weight, originX, topY, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, dpiScale, _scratch,
            spanRun is not null ? spanRun.Spans : default, _colorScratch,
            spanRun?.OverflowSuffixStart ?? -1);
        int n = _scratch.Count;
        var arr = RentQuads(n);
        for (int i = 0; i < n; i++) arr[i] = _scratch[i];
        ColorF[]? colors = null;
        if (_colorScratch.Count == n && ColorGlyphBake.RetainColors(CollectionsMarshal.AsSpan(_colorScratch)))
        {
            colors = new ColorF[n];   // only retain when some span or palette layer actually recolors
            _colorScratch.CopyTo(colors);
        }
        // A mixed-generation shape (some quads hold stale atlas UVs) must NEVER be cached — it would replay wrong
        // forever instead of just this one frame. Unreachable in the frame-boundary-only reset model; kept as the
        // correctness backstop (Diag canary — should read 0 in production).
        if (consistent)
        {
            _runCache[key] = new ShapedRun { Glyphs = arr, Colors = colors, Count = n, LastUsedFrame = _frame };
            _runsShaped++;
        }
        else
        {
            Diag.Count("text.run", "uncachedMixedEpoch");
        }
        if (_preparing) { if (!consistent) ReturnQuads(arr); return; }
        var baked = arr.AsSpan(0, n);
        Replay(baked, colors, forceColor, color, world, opacity, dpiScale, motionSoft, outList);
        if (!consistent) ReturnQuads(arr);   // Replay already copied into outList; the rented array is not retained
    }

    private float[] _gradDy = Array.Empty<float>();
    private float[] _gradRo0 = Array.Empty<float>();     // per-glyph reading-order LEFT  (continuous across wrapped visual lines)
    private float[] _gradRo1 = Array.Empty<float>();     // per-glyph reading-order RIGHT

    /// <summary>A SETTLED wipe whose effective fill alpha (color.A × opacity) lands below this contributes nothing
    /// through the premultiplied blend (SRC=ONE, DEST=INV_SRC_ALPHA: dest scales by 1−α and gains α·rgb), i.e. under
    /// 0.03/255 of an 8-bit channel — far below one quantization step. Skipping the draw is therefore pixel-identical,
    /// and it drops the lyrics GLOW layer's fully-unsung lines (its <c>After</c> is A==0) entirely instead of pushing
    /// invisible instances through the shaper and the instance budget.</summary>
    private const float SettledAlphaEpsilon = 1e-4f;

    /// <summary>Glyph-WIPE variant of <see cref="LayoutRun"/> (the <c>GlyphWipe</c> primitive): shapes/caches the run
    /// under the SAME <see cref="RunKey"/> (so re-using it costs no reshape), then computes a PER-GLYPH Y offset from the
    /// wipe <paramref name="split"/> (0..1 along the run's READING-ORDER extent) and feeds every glyph its own
    /// reading-order extent, so the gradient PS mixes <paramref name="before"/> (sung) → <paramref name="after"/>
    /// (unsung) PER PIXEL across a <paramref name="softness"/>-wide feather band — the boundary cuts THROUGH a glyph,
    /// half sung / half unsung. Unsung glyphs sit <paramref name="lift"/> DIP BELOW the baseline and rise smoothly to it
    /// as the band sweeps them; sung glyphs never move. Advancing the split per frame only changes the computed per-glyph
    /// values, never the cache key, so there is no per-frame reshape.
    /// <para>SETTLED-SPLIT FAST PATH: a run whose wipe is fully sung (split ≥ 1) or fully unsung (split ≤ 0) has a
    /// CONSTANT fill and a constant per-glyph dy, so it is emitted into <paramref name="plainList"/> — the lean
    /// single-color glyph batch — instead of the gradient batch (proof of pixel-equivalence in the body). In the steady
    /// state every visible lyric line except the one actually being sung is settled, so this is what keeps the gradient
    /// path at ~one line per frame.</para></summary>
    public void LayoutRunGradient(StringId textId, StringId familyId, string text, string family, float size, int weight, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, ColorF before, ColorF after, float split, float softness, float lift, float dpiScale, Affine2D world, float opacity,
        List<GradGlyphInstance> outList, List<GlyphInstance> plainList,
        int spanRunId = 0, float motionSoft = 0f)
    {
        var key = MakeRunKey(textId, familyId, size, weight, maxWidth, wrap, trim, maxLines, originX, topY, dpiScale, charSpacing, lineHeight, lineStacking, lineBounds, spanRunId);

        ShapedGlyph[] quadsArr; int count;
        ref var hit = ref CollectionsMarshal.GetValueRefOrNullRef(_runCache, key);
        if (!Unsafe.IsNullRef(ref hit))
        {
            if (_preparing) { hit.LastUsedFrame = _frame; return; }
            hit.LastUsedFrame = _frame; _runsCached++;
            quadsArr = hit.Glyphs; count = hit.Count;
        }
        else
        {
            _scratch.Clear();
            // colorsOut stays null here: the wipe paints every quad from its own before/after pair, so colour emoji
            // inside a gradient/lyrics wipe render MONOCHROME (each COLR layer takes the wipe colour). Geometry is
            // identical to LayoutRun's shape of the same key (ShapeInto emits layer quads regardless), so the cached
            // run is shared; a LayoutRun hit on a run first shaped HERE replays emoji flat (Colors = null) — accepted.
            bool consistent = ShapeInto(text, family, size, weight, originX, topY, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, lineStacking, lineBounds, dpiScale, _scratch);
            count = _scratch.Count;
            var arr = RentQuads(count);
            for (int i = 0; i < count; i++) arr[i] = _scratch[i];
            // See LayoutRun: a mixed-generation shape must never be cached.
            if (consistent)
            {
                _runCache[key] = new ShapedRun { Glyphs = arr, Colors = null, Count = count, LastUsedFrame = _frame };
                _runsShaped++;
            }
            else
            {
                Diag.Count("text.run", "uncachedMixedEpoch");
            }
            quadsArr = arr;
        }

        if (_preparing) return;
        var quads = quadsArr.AsSpan(0, count);
        if (count == 0) return;

        // ── SETTLED-SPLIT FAST PATH ────────────────────────────────────────────────────────────────────────────────
        // A settled wipe is a plain single-color run, so pay the plain path's cost, not the gradient path's. Steady
        // state: every visible lyric line is settled except the ~one being sung, and the gradient batch's budget
        // (MaxGradGlyphs) plus its second PSO only has to cover that one line. PIXEL-EQUIVALENCE (both endpoints, from
        // the shader's own band `a = saturate((splitShader - gt)/fade + 0.5)` and the endpoint remap
        // `splitShader = split*(1+fade) - 0.5*fade`; every glyph's reading-order extent gt lies in [0,1] by
        // construction, since ro0 >= 0 and ro1 <= total):
        //   split >= 1 ⇒ splitShader = 1 + fade/2 ⇒ (splitShader - gt)/fade + 0.5 >= 1 for ALL gt <= 1 ⇒ a == 1
        //                everywhere ⇒ colour == `before` at every pixel AND per-glyph dy == lift*(1-a) == 0. So the
        //                plain path with force-colour `before` and the normal snap dy is EXACT.
        //   split <= 0 ⇒ splitShader = -fade/2 ⇒ (splitShader - gt)/fade + 0.5 <= 0 for ALL gt >= 0 ⇒ a == 0
        //                everywhere ⇒ colour == `after` AND per-glyph dy == lift UNIFORMLY. So the plain path with
        //                force-colour `after` and a per-glyph dy span filled with `lift` is EXACT — the span (not a
        //                folded `snapDy + lift`) keeps the float addition in the SAME order as the gradient path, so
        //                the emitted DstY is bit-identical at the boundary frame.
        // Colours: the gradient path never consults a run's per-span colours, so the fast path force-colours to match.
        // Z-ORDER: the plain batch records before the gradient batch, so a settled run now draws UNDER a same-segment
        // mid-wipe run (they are different lyric lines — disjoint boxes) and, unlike before, in stream order relative
        // to ordinary text (strictly closer to painter order than routing everything through the gradient batch).
        if (split >= 1f || split <= 0f)
        {
            bool sung = split >= 1f;
            ColorF settled = sung ? before : after;
            if (settled.A * opacity <= SettledAlphaEpsilon) return;   // invisible (e.g. the glow layer's unsung pass)
            if (sung)
            {
                Replay(quads, null, forceColor: true, settled, world, opacity, dpiScale, motionSoft, plainList);
            }
            else
            {
                if (_gradDy.Length < count) _gradDy = new float[count];
                var dy = _gradDy.AsSpan(0, count);
                dy.Fill(lift);   // a == 0 everywhere ⇒ lift*(1-a) == lift; Span.Fill is intrinsic, no allocation
                Replay(quads, null, forceColor: true, settled, world, opacity, dpiScale, motionSoft, plainList, dy);
            }
            return;
        }

        if (_gradDy.Length < count) _gradDy = new float[count];
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
        // Per-glyph motion at the karaoke front, VERIFIED frame-by-frame against real Apple Music footage (2026-08-03):
        //   • an UNSUNG glyph sits `lift` DIP BELOW its baseline and rises smoothly to it (dy = lift·(1−a)) as the
        //     feather band sweeps across it — `a` is the band's own coverage at the glyph centre, so the rise happens
        //     over exactly the softness window and settles to 0 the moment the glyph is fully sung;
        //   • a SUNG glyph is rock-solid — it never moves again;
        //   • there is NO scale change. Glyphs do not magnify at the front. (Removed 2026-08-03: the code here used to
        //     couple `lift > 0` to a ≈1.12× per-glyph "char pop" borrowed from BetterLyrics' LyricsAnimator; pixel
        //     evidence shows no glyph magnification anywhere in Apple's animation, so the pop is refuted and gone.)
        // The colour wipe itself is per-PIXEL in the gradient PS (ReplayGradient feeds each glyph its reading-order
        // extent), so the boundary cuts THROUGH a glyph — half sung / half unsung — not glyph-by-glyph.
        for (int i = 0; i < count; i++)
        {
            float gtc = (_gradRo0[i] + _gradRo1[i]) * 0.5f / total;   // glyph-centre reading-order position (drives the rise)
            float a = Math.Clamp((splitShader - gtc) / fade + 0.5f, 0f, 1f);
            _gradDy[i] = lift * (1f - a);   // unsung sunk by `lift`, rising to the baseline (0) as it is swept (settles to 0 at split==1)
        }
        ReplayGradient(quads, world, opacity, dpiScale, motionSoft, before, after, splitShader, fade, total,
            _gradRo0.AsSpan(0, count), _gradRo1.AsSpan(0, count), _gradDy.AsSpan(0, count), outList);
    }

    /// <summary>Wire the interner so the run cache can drop runs whose text id was reclaimed (resolves empty).</summary>
    public void SetLivenessSource(StringTable strings) => _liveness = strings;

    internal ShapedGlyph[] RentQuads(int count)
    {
        if (count == 0) return Array.Empty<ShapedGlyph>();
        int bucket = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(count - 1));
        if (bucket >= _quadPool.Length) return new ShapedGlyph[count];   // pathological run — don't pool
        var stack = _quadPool[bucket];
        if (stack is not { Count: > 0 }) return new ShapedGlyph[1 << bucket];
        var array = stack.Pop();
        _pooledQuadBytes -= (long)array.Length * Unsafe.SizeOf<ShapedGlyph>();
        return array;
    }

    internal void ReturnQuads(ShapedGlyph[] arr)
    {
        if (arr.Length == 0) return;
        int bucket = System.Numerics.BitOperations.Log2((uint)arr.Length);
        if (bucket >= _quadPool.Length || arr.Length != 1 << bucket) return;
        long bytes = (long)arr.Length * Unsafe.SizeOf<ShapedGlyph>();
        if (bytes > MaxPooledQuadBytes - _pooledQuadBytes) return;
        var stack = _quadPool[bucket] ??= new Stack<ShapedGlyph[]>();
        if (stack.Count >= MaxPooledQuadArraysPerBucket) return;   // cap reached → drop the reference (GC reclaims)
        stack.Push(arr);
        _pooledQuadBytes += bytes;
    }

    // Colour-blind by design (like GlyphKey and the atlas): a colour emoji's palette layers bake into the run's Colors
    // array, not the key, so the same run replays under any run colour; forceColor (selection/disabled) still wins.
    // Width and origin key on their EXACT float bits, never a whole-DIP bucket: the shape bakes the origin into every
    // quad and makes every wrap/trim decision at the exact width (TextLayoutEngine.WrapAndPosition's
    // `pen + adv > maxWidth + WrapSlack`, the same test the UI measure pass re-runs at the exact width). A rounded key
    // let a box resizing through 199.6 and settling at 200.4 replay the 199.6 shape — a line more than measure sized the
    // box for, or an ellipsis cut at the wrong width — for as long as the text stayed on screen. A box at rest keeps its
    // exact width, so steady-state hits are unchanged.
    internal static RunKey MakeRunKey(StringId textId, StringId familyId, float size, int weight, float maxWidth, int wrap, int trim, int maxLines, float originX, float topY, float dpiScale,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, int spanRunId)
    {
        int widthQ = float.IsInfinity(maxWidth) || maxWidth > 1e9f ? int.MaxValue : ExactKey(maxWidth);
        int lineHQ = float.IsNaN(lineHeight) || lineHeight <= 0f ? 0 : (int)MathF.Round(lineHeight * 10f);   // 0 = font-natural
        // The size at the glyph cache's own resolution (DeviceEmQ): a whole-DIP bucket let a 13.5-DIP and a 14-DIP run
        // of the same text share ONE baked quad set.
        return new RunKey(textId.Value, familyId.Value, DeviceEmQ(size, dpiScale), weight, wrap, trim, maxLines,
            widthQ, ExactKey(originX), ExactKey(topY), (int)MathF.Round(dpiScale * 100f),
            (int)MathF.Round(charSpacing * 10f), lineHQ, lineStacking | (lineBounds << 8), spanRunId);
    }

    /// <summary>A layout float as an exact key: its IEEE bits, with -0 folded onto +0 (they lay out identically).</summary>
    private static int ExactKey(float v) => v == 0f ? 0 : BitConverter.SingleToInt32Bits(v);

    /// <summary>Per-run vertical placement, applied at replay: snap the run's baseline to the nearest <b>1/N device
    /// row</b> and report which of the <see cref="SubPixelPhases"/> baked variants realises that fraction. Returns the
    /// correction in local DIP; <paramref name="phase"/> selects the atlas rows.
    ///
    /// <para>Glyph bitmaps are rasterized for an integer device baseline PLUS a known sub-pixel offset, and every
    /// bearing/height is an integer device row, so all quads of one BASELINE share the first quad's fractional device-Y
    /// phase (Replay snaps each baseline group, see <see cref="BaselineGroupEnd"/>). The correction moves the baseline onto the integer row <c>i</c>; the bitmap itself carries the remaining
    /// <c>phase/N</c>. Worst-case residual is 1/(2N) device pixels.</para>
    ///
    /// <para>This replaces a whole-pixel snap that was <b>motion-gated off</b> for the entire duration of every scroll,
    /// fling and drag — which meant a fractional device Y sampled through the LINEAR/CLAMP atlas sampler, attenuating
    /// the bottom coverage row by (1−frac) and smearing every glyph across two rows for as long as the gesture lasted,
    /// then popping sharp on the settle frame. There is no longer anything to gate: the phase stack is crisp at any
    /// fraction, so text stays sharp in motion AND the scene is free to move sub-pixel.</para>
    ///
    /// <para>Snapping happens HERE, not at bake, so cached quads stay phase-agnostic and one run cached at one position
    /// replays correctly at any other. One scalar PER BASELINE, not per run: a wrapped run's pitch is fractional in device px
    /// (Segoe UI 14 → 18.62 DIP), so a run-wide snap left lines 2..n at a fractional device row, bilinearly smeared.
    /// Snapping each line on its own moves its leading by at most 1/N px. Y only — X keeps DirectWrite's sub-pixel advances. Skewed/rotated/flipped worlds
    /// (M12 ≠ 0 or M22 ≤ 0) draw unsnapped at phase 0 — there is no meaningful pixel grid for them.</para></summary>
    private static float SnapDy(ReadOnlySpan<ShapedGlyph> glyphs, in Affine2D world, float dpiScale, float motionSoft, out int phase)
    {
        phase = 0;
        if (glyphs.Length == 0 || world.M12 != 0f || world.M22 <= 0f) return 0f;
        float devY = (world.M22 * glyphs[0].DstY + world.Dy) * dpiScale;
        float m = MathF.Round(devY * SubPixelPhases);          // nearest 1/N device row, in 1/N units
        float rowF = MathF.Floor(m / SubPixelPhases);          // the integer device row the quad is positioned on
        phase = (int)(m - rowF * SubPixelPhases);              // floor remainder ⇒ always 0..N-1, incl. negative devY
        if (phase < 0) phase = 0; else if (phase >= SubPixelPhases) phase = SubPixelPhases - 1;
        // Relax the snap in proportion to how fast the content is moving. At 0 the run lands exactly on its 1/N phase
        // (crisp); at 1 no correction is applied at all, the glyph sits at its natural fractional device Y, and the
        // LINEAR/CLAMP atlas sampler reproduces the soft look WinUI gets for free by resampling a composited surface.
        // In between the run drifts partway off the phase grid, which is a real blend rather than a switch.
        float relax = motionSoft > 0f ? (motionSoft >= 1f ? 0f : 1f - motionSoft) : 1f;   // NaN ⇒ crisp
        return (rowF - devY) / (world.M22 * dpiScale) * relax;
    }

    /// <summary>Two quads whose LOCAL device-Y fractions agree within this many device px sit on the same baseline grid
    /// (see <see cref="BaselineGroupEnd"/>). Far above float error at any realistic local Y, far below anything visible.</summary>
    private const float BaselineGroupSlack = 1f / 64f;

    /// <summary>The end (exclusive) of the baseline group that starts at quad <paramref name="start"/>: the following quads
    /// that share its fractional LOCAL device-Y. ShapeInto bakes every quad at <c>topY + lineBaseline + bearing</c> with an
    /// integer device-row bearing, so one visual line is one group whatever its glyph sizes or spans. A wrapped line whose
    /// pitch is fractional in device px (Segoe UI 14 → 18.62 DIP) starts a new group and gets its own <see cref="SnapDy"/>.</summary>
    private static int BaselineGroupEnd(ReadOnlySpan<ShapedGlyph> glyphs, int start, float dpiScale)
    {
        float anchor = glyphs[start].DstY * dpiScale;
        int i = start + 1;
        for (; i < glyphs.Length; i++)
        {
            float d = glyphs[i].DstY * dpiScale - anchor;
            if (MathF.Abs(d - MathF.Round(d)) > BaselineGroupSlack) break;
        }
        return i;
    }

    /// <summary>Emit cached local-space quads into <paramref name="outList"/>, applying the per-frame color/transform/opacity
    /// and each baseline's <see cref="SnapDy"/> correction. Allocation-free (appends into the reused glyph-instance list) —
    /// the steady-state path for unchanged text.
    /// <paramref name="colors"/> (span runs and colour-emoji runs): the per-quad tint — A==0 inherits <paramref name="color"/>;
    /// <paramref name="forceColor"/> repaints every quad in <paramref name="color"/> regardless (the recorder's
    /// selected-text recolor re-emit, which must override span colors like WinUI's selection repaint) — so a selected
    /// or disabled emoji goes FLAT in the forced colour, exactly like WinUI's selection repaint of a colour glyph.
    /// <paramref name="perGlyphDy"/> (optional, parallel to <paramref name="glyphs"/>): an extra per-glyph local-DIP Y
    /// offset added on top of the baseline's snap — how <see cref="LayoutRunGradient"/>'s settled-split fast path
    /// reproduces the wipe's uniform unsung `Lift` through this lean single-color path.</summary>
    internal static void Replay(ReadOnlySpan<ShapedGlyph> glyphs, ColorF[]? colors, bool forceColor, ColorF color, Affine2D world, float opacity, float dpiScale, float motionSoft, List<GlyphInstance> outList, ReadOnlySpan<float> perGlyphDy = default)
    {
        float snapDy = 0f; int phase = 0, groupEnd = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (i == groupEnd)   // a new baseline: snap it onto its OWN 1/N phase (see BaselineGroupEnd)
            {
                groupEnd = BaselineGroupEnd(glyphs, i, dpiScale);
                snapDy = SnapDy(glyphs[i..], world, dpiScale, motionSoft, out phase);
            }
            ref readonly var s = ref glyphs[i];
            ColorF c = colors is not null && !forceColor && colors[i].A > 0f ? colors[i] : color;
            float dx = s.DstX, dy = s.DstY + snapDy + (i < perGlyphDy.Length ? perGlyphDy[i] : 0f), dw = s.DstW, dh = s.DstH;
            float v = phase * s.VStride;   // select the baked sub-pixel variant (contiguous phase stack)
            outList.Add(new GlyphInstance
            {
                DstX = dx, DstY = dy, DstW = dw, DstH = dh,
                U0 = s.U0, V0 = s.V0 + v, U1 = s.U1, V1 = s.V1 + v,
                R = c.R, G = c.G, B = c.B, A = c.A,
                M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22, Dx = world.Dx, Dy = world.Dy, Opacity = opacity,
            });
        }
    }

    /// <summary>Emit gradient-glyph instances for the sub-glyph wipe: per glyph, apply the per-glyph rise (Dy) to the quad
    /// and record its run-local-x extent [gt0,gt1] plus before/after/split/fade — the PS does the per-PIXEL colour mix, so
    /// the wipe boundary cuts THROUGH glyphs (half sung / half unsung), not glyph-by-glyph. The quad's SIZE is never
    /// touched: glyphs do not magnify at the front (the refuted char pop — see LayoutRunGradient).</summary>
    internal static void ReplayGradient(ReadOnlySpan<ShapedGlyph> glyphs, Affine2D world, float opacity, float dpiScale, float motionSoft,
        ColorF before, ColorF after, float split, float fade, float total,
        ReadOnlySpan<float> ro0, ReadOnlySpan<float> ro1,
        ReadOnlySpan<float> perGlyphDy, List<GradGlyphInstance> outList)
    {
        float inv = 1f / total;
        float snapDy = 0f; int phase = 0, groupEnd = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (i == groupEnd)   // a new baseline: snap it onto its OWN 1/N phase (see BaselineGroupEnd)
            {
                groupEnd = BaselineGroupEnd(glyphs, i, dpiScale);
                snapDy = SnapDy(glyphs[i..], world, dpiScale, motionSoft, out phase);
            }
            ref readonly var s = ref glyphs[i];
            float dx = s.DstX, dy = s.DstY + snapDy + (i < perGlyphDy.Length ? perGlyphDy[i] : 0f), dw = s.DstW, dh = s.DstH;
            float v = phase * s.VStride;   // select the baked sub-pixel variant (contiguous phase stack)
            outList.Add(new GradGlyphInstance
            {
                DstX = dx, DstY = dy, DstW = dw, DstH = dh,
                U0 = s.U0, V0 = s.V0 + v, U1 = s.U1, V1 = s.V1 + v,
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
    /// glyph rasterizes at ITS shaped size (LaidGlyph.Size) and <paramref name="colorsOut"/> (when non-null) receives
    /// the per-quad color (A==0 = inherit the run colour), parallel to <paramref name="outList"/>.
    /// <para>Colour emoji: a glyph of a colour face (<see cref="IsColorFace"/>) with COLR v0 layers
    /// (<see cref="ColorLayersFor"/>) bakes one quad PER LAYER — the layer's own outline glyph through the same alpha
    /// atlas, offset by the layer's EM delta — and pushes <c>ColorGlyphBake.LayerColor</c> (the CPAL colour, or the
    /// span colour for a foreground layer) into <paramref name="colorsOut"/>. The GEOMETRY is emitted whether or not a
    /// colour list is requested, so the same <see cref="RunKey"/> shapes identically for <see cref="LayoutRun"/> and
    /// <see cref="LayoutRunGradient"/> (which keeps <c>colorsOut</c> null: emoji inside a gradient/lyrics wipe stay
    /// monochrome — every layer takes the wipe colour). A glyph without colour layers takes the single-quad path
    /// exactly as before, so non-emoji quads are byte-identical to the pre-colour bake.</para></summary>
    /// <summary>Returns whether the shape stayed within ONE atlas generation throughout — false means the atlas
    /// overflowed mid-shape (now impossible in the frame-boundary-only reset model — see PackOrReset/BeginFrame —
    /// but kept as an unconditional correctness invariant against any future mid-frame flush path): a caller MUST
    /// NOT cache a run for which this returns false, since some of its quads may hold stale-generation UVs.</summary>
    private bool ShapeInto(string text, string family, float size, int weight, float originX, float topY, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, float dpiScale, List<ShapedGlyph> outList,
        ReadOnlySpan<SpanStyle> spans = default, List<ColorF>? colorsOut = null, int overflowSuffixStart = -1)
    {
        _engine.Layout(text.AsSpan(), family ?? "", weight, size, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight,
            lineStacking, lineBounds, spans, _liveness, overflowSuffixStart);
        float inv = 1f / dpiScale;
        // If the atlas generation resets mid-run (PackOrReset), quads already baked this pass hold stale UVs —
        // re-shape the whole run into the fresh generation so a cached run is always generation-consistent.
        // Bounded: a restarted pass packs into an empty ATLAS² atlas; one run can't fill it (guard at 3 just in case).
        int epoch, restarts = 0;
        do
        {
            epoch = _atlas.Epoch;
            outList.Clear();
            colorsOut?.Clear();
            foreach (var lg in _engine.Glyphs)
            {
                if (lg.Face == 0) continue;
                float gsize = lg.Size > 0f ? lg.Size : size;
                ColorF spanColor = lg.Span >= 0 && lg.Span < spans.Length ? spans[lg.Span].Color : default;
                // Colour emoji: one quad per COLR v0 layer (see the ColorLayer doc). Cache-miss path only — the
                // face/layer lookups are dictionary probes after the first shape of a given (face, gid).
                if (IsColorFace(lg.Face))
                {
                    var layers = ColorLayersFor((IDWriteFontFace*)lg.Face, lg.Gid, gsize);
                    if (layers.Length > 0)
                    {
                        for (int li = 0; li < layers.Length; li++)
                        {
                            ref readonly var layer = ref layers[li];
                            var le = GetGlyphByGid((IDWriteFontFace*)lg.Face, layer.Gid, gsize, dpiScale);
                            if (le.W <= 0 || le.H <= 0) continue;   // an ink-less layer emits no quad AND no colour (lists stay parallel)
                            outList.Add(Quad(in le, originX + lg.X + layer.DxEm * gsize + le.BearingX * inv, topY + lg.Y + layer.DyEm * gsize + le.BearingY * inv, inv));
                            colorsOut?.Add(ColorGlyphBake.LayerColor(layer.Foreground, layer.Color, spanColor));
                        }
                        continue;
                    }
                    // A colour face's glyph without colour layers (a letter in Segoe UI Emoji) is an ordinary outline.
                }
                var ge = GetGlyphByGid((IDWriteFontFace*)lg.Face, lg.Gid, gsize, dpiScale);
                if (ge.W > 0 && ge.H > 0)
                {
                    outList.Add(Quad(in ge, originX + lg.X + ge.BearingX * inv, topY + lg.Y + ge.BearingY * inv, inv));
                    colorsOut?.Add(spanColor);
                }
            }
        } while (epoch != _atlas.Epoch && ++restarts < 3);
        return epoch == _atlas.Epoch;
    }

    /// <summary>The local-space quad for one packed atlas entry at a resolved top-left (bearing already applied by the
    /// caller, in the same expression order as before so the non-emoji bake stays bit-identical).</summary>
    private ShapedGlyph Quad(in GlyphEntry ge, float dstX, float dstY, float inv) => new()
    {
        DstX = dstX, DstY = dstY,
        DstW = ge.W * inv, DstH = ge.H * inv,
        U0 = ge.X / (float)ATLAS, V0 = ge.Y / (float)ATLAS, U1 = (ge.X + ge.W) / (float)ATLAS, V1 = (ge.Y + ge.H) / (float)ATLAS,
        VStride = PhaseStride(ge.H) / (float)ATLAS,
    };

    private int FaceId(nint face) { if (_faceIds.TryGetValue(face, out int id)) return id; id = _faceIds.Count + 1; _faceIds[face] = id; return id; }

    /// <summary>Whether <paramref name="face"/> carries colour tables (<c>IDWriteFontFace2.IsColorFont</c> — COLR/CPAL,
    /// SVG or bitmap). Cached per face pointer; false when DirectWrite predates FontFace2. A true answer only gates
    /// the <see cref="ColorLayersFor"/> probe — every glyph of a colour face is still asked individually.</summary>
    private bool IsColorFace(nint face)
    {
        if (_faceIsColor.TryGetValue(face, out bool isColor)) return isColor;
        isColor = false;
        if (_dw2 != null)   // FontFace2 shipped with Factory2 (DirectWrite 8.1); no point probing on an older runtime
        {
            IDWriteFontFace2* f2;
            if ((int)((IDWriteFontFace*)face)->QueryInterface(__uuidof<IDWriteFontFace2>(), (void**)&f2) >= 0 && f2 != null)
            {
                isColor = f2->IsColorFont();
                f2->Release();
            }
        }
        _faceIsColor[face] = isColor;
        return isColor;
    }

    /// <summary>The COLR v0 layers of glyph <paramref name="gid"/> in <paramref name="face"/>, cached on
    /// <c>(faceId &lt;&lt; 16 | gid)</c> — offsets are stored in EM so the entry is size-independent (translated at
    /// <paramref name="size"/> and divided back out). Empty for a glyph with no colour description
    /// (<c>DWRITE_E_NOCOLOR</c>), which is also the cached answer, so the DirectWrite call runs once per (face, gid).
    /// <para>Primary path: <c>IDWriteFactory2.TranslateColorGlyphRun</c> (COLR v0 only). Fallback when that reports
    /// NOCOLOR: <c>IDWriteFactory4.TranslateColorGlyphRun</c> asking for <c>COLR | TRUETYPE | CFF</c>, whose
    /// <c>IDWriteColorGlyphRunEnumerator1</c> yields the same base-struct layout (<c>DWRITE_COLOR_GLYPH_RUN1</c>
    /// derives from <c>DWRITE_COLOR_GLYPH_RUN</c>, so the base pointer cast is exact). Both decompose only v0 layer
    /// lists; a glyph that exists solely as a COLR v1 paint tree yields NOCOLOR from both and stays monochrome.</para>
    /// <para>Miss path only: called from <see cref="ShapeInto"/>, never at replay. The one allocation is the cached
    /// array itself (like a span run's <c>Colors</c> array), on the first shape of a given glyph.</para></summary>
    private ColorLayer[] ColorLayersFor(IDWriteFontFace* face, ushort gid, float size)
    {
        long key = ((long)FaceId((nint)face) << 16) | gid;
        if (_colorLayers.TryGetValue(key, out var cached)) return cached;

        _layerScratch.Clear();
        float zeroAdvance = 0f;
        ushort gi = gid;
        DWRITE_GLYPH_RUN run = default;
        run.fontFace = face; run.fontEmSize = size; run.glyphCount = 1;
        run.glyphIndices = &gi; run.glyphAdvances = &zeroAdvance; run.glyphOffsets = null;
        run.isSideways = BOOL.FALSE; run.bidiLevel = 0;

        bool translated = false;
        if (_dw2 != null)
        {
            IDWriteColorGlyphRunEnumerator* en = null;
            int hr = (int)_dw2->TranslateColorGlyphRun(0f, 0f, &run, null, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL, null, 0, &en);
            if (hr >= 0 && en != null)
            {
                translated = true;
                BOOL more;
                while ((int)en->MoveNext(&more) >= 0 && more)
                {
                    DWRITE_COLOR_GLYPH_RUN* cr;
                    if ((int)en->GetCurrentRun(&cr) < 0 || cr == null) break;
                    AppendLayers(cr, size);
                }
                en->Release();
            }
            else if (hr != DWRITE_E_NOCOLOR)
            {
                Diag.Event("text.color", $"TranslateColorGlyphRun(Factory2) failed for gid {gid}: 0x{(uint)hr:X8}");
            }
        }
        if (!translated && _dw4 != null)
        {
            // Factory2 answered NOCOLOR (or is unavailable): ask the image-format-aware variant for the same v0 layer
            // list. TRUETYPE|CFF are included so a mixed run's plain outline members come back as foreground layers
            // (paletteIndex 0xFFFF) rather than making the whole call fail; COLR_PAINT_TREE is deliberately NOT
            // requested (v1 out of scope — see the field block).
            IDWriteColorGlyphRunEnumerator1* en1 = null;
            var origin = new D2D_POINT_2F { x = 0f, y = 0f };
            int hr = (int)_dw4->TranslateColorGlyphRun(origin, &run, null,
                DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_COLR | DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_TRUETYPE | DWRITE_GLYPH_IMAGE_FORMATS.DWRITE_GLYPH_IMAGE_FORMATS_CFF,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL, null, 0, &en1);
            if (hr >= 0 && en1 != null)
            {
                BOOL more;
                while ((int)en1->MoveNext(&more) >= 0 && more)
                {
                    DWRITE_COLOR_GLYPH_RUN1* cr1;
                    if ((int)en1->GetCurrentRun(&cr1) < 0 || cr1 == null) break;
                    AppendLayers((DWRITE_COLOR_GLYPH_RUN*)cr1, size);   // DWRITE_COLOR_GLYPH_RUN1 : DWRITE_COLOR_GLYPH_RUN — base at offset 0
                }
                en1->Release();
            }
            else if (hr != DWRITE_E_NOCOLOR)
            {
                Diag.Event("text.color", $"TranslateColorGlyphRun(Factory4) failed for gid {gid}: 0x{(uint)hr:X8}");
            }
        }

        var layers = _layerScratch.Count == 0 ? NoLayers : _layerScratch.ToArray();
        _layerScratch.Clear();
        if (layers.Length > 0) Diag.Count("text.color", "glyphsTranslated");
        _colorLayers[key] = layers;
        return layers;
    }

    /// <summary>Append one colour run's glyphs as <see cref="ColorLayer"/>s (EM units). A layer run normally holds
    /// exactly one glyph, but the loop honours the general shape: X = baseline origin + advances before k + the glyph's
    /// advanceOffset; Y = baseline origin − ascenderOffset (DirectWrite's ascender offset is positive UP, our Y is
    /// positive DOWN). Palette index 0xFFFF marks a FOREGROUND layer whose colour is the run's, decided at bake by
    /// <c>ColorGlyphBake.LayerColor</c>.</summary>
    private void AppendLayers(DWRITE_COLOR_GLYPH_RUN* cr, float size)
    {
        var gr = cr->glyphRun;
        if (gr.glyphIndices == null) return;
        bool foreground = cr->paletteIndex == 0xFFFF;
        var rc = cr->runColor;
        ColorF palette = foreground ? default : new ColorF(rc.r, rc.g, rc.b, rc.a);
        float pen = cr->baselineOriginX;
        for (uint k = 0; k < gr.glyphCount; k++)
        {
            float dx = pen, dy = cr->baselineOriginY;
            if (gr.glyphOffsets != null) { dx += gr.glyphOffsets[k].advanceOffset; dy -= gr.glyphOffsets[k].ascenderOffset; }
            _layerScratch.Add(new ColorLayer(gr.glyphIndices[k], dx / size, dy / size, palette, foreground));
            if (gr.glyphAdvances != null) pen += gr.glyphAdvances[k];
        }
    }

    // Rasterize one POST-shaping glyph (by glyph id, not codepoint) at the physical size into the atlas. The face comes
    // from the layout engine; DWrite factories are process-shared, so _dw and the engine's factory are the same object.
    // Coverage only (R8): a colour emoji never comes through here as its base glyph — ShapeInto rasterizes each COLR
    // LAYER glyph instead (plain outlines in the same face), so the GlyphKey/atlas stay colour-blind and unchanged.
    private GlyphEntry GetGlyphByGid(IDWriteFontFace* face, ushort gid, float size, float dpiScale)
    {
        int faceId = FaceId((nint)face);
        int sizeQ = DeviceEmQ(size, dpiScale);
        int scaleQ = (int)MathF.Round(dpiScale * 100f);
        // Weight 0 here is correct: the face id already encodes the (family, numeric weight) the engine resolved.
        var key = new GlyphKey(faceId, sizeQ, scaleQ, 0, gid, ByGid: true);
        if (_cache.TryGetValue(key, out var e)) return e;

        float physEm = sizeQ * (1f / 64f);                         // rasterize FROM THE KEY (see DeviceEmQ)
        float zeroAdvance = 0f;
        ushort gi = gid;
        DWRITE_GLYPH_RUN run = default;
        run.fontFace = face; run.fontEmSize = physEm; run.glyphCount = 1;
        run.glyphIndices = &gi; run.glyphAdvances = &zeroAdvance; run.glyphOffsets = null;
        run.isSideways = BOOL.FALSE; run.bidiLevel = 0;

        // NATURAL_SYMMETRIC + the SubPixelPhases vertical phase stack — see TryRasterizePhaseStack. H is PER PHASE.
        bool hasInk = TryRasterizePhaseStack(&run, out RECT bounds, out int w, out int h, out byte[] stack);
        if (!hasInk) { bounds = default; w = 0; h = 0; }
        e = new GlyphEntry { Advance = 0f, BearingX = bounds.left, BearingY = bounds.top, W = w, H = h };
        if (w > 0 && h > 0) PackOrReset(ref e, stack, w, PhaseStackHeight(h));   // a successful pack marks its rows dirty
        Diag.Count("text.glyph", "rasterized");
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
                VStride = PhaseStride(g.H) / (float)ATLAS,
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
            spanRun is not null ? spanRun.Spans : default, overflowSuffixStart: spanRun?.OverflowSuffixStart ?? -1);
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
    /// <summary>Cold glyph preparation is before command-list reset, image-upload fence stamps, and all instance
    /// emission. The callback drains the whole device only for the single 2048-to-4096 growth transition.</summary>
    internal void BeginPreparation(Action growthFence)
    {
        if (_preparing || _cursor != 0 || _gradCursor != 0)
            throw new InvalidOperationException("Glyph preparation must precede instance recording.");
        _prepareGrowthFence = growthFence;
        _preparing = true;
    }

    internal void EndPreparation()
    {
        _preparing = false;
        _prepareGrowthFence = null;
        if (!_atlas.IsDirty) return;
        int bandBase = Math.Min(_atlas.DirtyRowStart, Math.Max(0, _atlas.ShelfRow - 1));
        int needed = _atlas.DirtyRowStart + _atlas.DirtyRowCount - bandBase;
        if (_stagingRows[_active] >= needed) return;
        // No copy has been recorded yet. Allocate BEFORE releasing the valid bank; all dirty rows must fit
        // this first paint even if a previous idle window reduced the warm reserve.
        int rows = Math.Min(ATLAS, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)needed));
        var replacement = CreateUpload(_device, (uint)(ATLAS * rows), $"Glyph.AtlasUpload[{_active}]");
        D3D12MemoryDiagnostics.Release(_texUpload[_active], $"Glyph.AtlasUpload[{_active}]");
        _texUpload[_active]->Release();
        _texUpload[_active] = replacement;
        _stagingRows[_active] = rows;
        _wantStagingRows = Math.Max(_wantStagingRows, Math.Min(rows, MaxStagingRows));
    }

    private void TryGrowAtlas()
    {
        Debug.Assert(_preparing && _prepareGrowthFence is not null);
        ID3D12Resource* texture = null;
        ID3D12DescriptorHeap* heap = null;
        var uploads = new ID3D12Resource*[FrameCount];
        GlyphAtlasStore candidate;
        try
        {
            try
            {
                candidate = _atlas.CreateExpanded(MaximumAtlasEdge);
                D3D12_HEAP_PROPERTIES hp = default; hp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
                D3D12_RESOURCE_DESC td = default;
                td.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
                td.Width = MaximumAtlasEdge; td.Height = MaximumAtlasEdge;
                td.DepthOrArraySize = 1; td.MipLevels = 1; td.SampleDesc.Count = 1;
                td.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM;
                // Query BEFORE CreateCommittedResource, on the SAME desc (audit gpu mem-02): the device-reported
                // allocation requirement, not an inferred edge*edge*1 pixel estimate.
                ulong atlasBytes = D3D12MemoryDiagnostics.AllocationBytes(_device, &td);
                Check(_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null,
                    __uuidof<ID3D12Resource>(), (void**)&texture), "Grow glyph atlas");
                D3D12MemoryDiagnostics.Track(texture,
                    D3D12MemoryDiagnostics.NameOrUnknown("Glyph.AtlasTexture 4096x4096 R8", atlasBytes),
                    atlasBytes != 0 ? atlasBytes : (ulong)MaximumAtlasEdge * MaximumAtlasEdge);
                D3D12_DESCRIPTOR_HEAP_DESC hd = default;
                hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
                hd.NumDescriptors = 1; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
                Check(_device->CreateDescriptorHeap(&hd, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "Grow glyph SRV");
                D3D12MemoryDiagnostics.Track(heap, "Glyph.SrvHeap", _device->GetDescriptorHandleIncrementSize(hd.Type));
                D3D12_SHADER_RESOURCE_VIEW_DESC sd = default;
                sd.Format = td.Format; sd.ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D;
                sd.Shader4ComponentMapping = 0x1688; sd.Anonymous.Texture2D.MipLevels = 1;
                _device->CreateShaderResourceView(texture, &sd, heap->GetCPUDescriptorHandleForHeapStart());
                for (int f = 0; f < FrameCount; f++)
                    uploads[f] = CreateUpload(_device, (uint)(MaximumAtlasEdge *
                        (f == _active ? MaximumAtlasEdge : GlyphStagingPolicy.WarmRows)), $"Glyph.AtlasUpload[{f}]");
            }
            catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException)
            {
                // Admission failure never publishes half a realization or destroys the last-good atlas.
                _growthFailed = true;
                Diag.Event("text.atlas", $"growth rejected; retaining {ATLAS} atlas: {ex.Message}");
                return;
            }
            // Outside the admission catch: device-loss/fence failure follows the device recovery path.
            _prepareGrowthFence!();
            D3D12MemoryDiagnostics.Release(_tex, "Glyph.AtlasTexture"); _tex->Release();
            D3D12MemoryDiagnostics.Release(_srvHeap, "Glyph.SrvHeap"); _srvHeap->Release();
            _tex = texture; texture = null;
            _srvHeap = heap; heap = null;
            _srvGpu = _srvHeap->GetGPUDescriptorHandleForHeapStart();
            for (int f = 0; f < FrameCount; f++)
            {
                D3D12MemoryDiagnostics.Release(_texUpload[f], $"Glyph.AtlasUpload[{f}]");
                _texUpload[f]->Release();
                _texUpload[f] = uploads[f]; uploads[f] = null;
                _stagingRows[f] = f == _active ? MaximumAtlasEdge : GlyphStagingPolicy.WarmRows;
            }
            _atlas = candidate;
            _texInitialized = false;
            foreach (var kv in _runCache) ReturnQuads(kv.Value.Glyphs);
            _runCache.Clear(); // coordinates survive; every normalized UV and phase stride must be rebuilt
            Diag.Count("text.atlas", "capacityGrow");
        }
        finally
        {
            if (texture != null) { D3D12MemoryDiagnostics.Release(texture, "Glyph.AtlasTexture"); texture->Release(); }
            if (heap != null) { D3D12MemoryDiagnostics.Release(heap, "Glyph.SrvHeap"); heap->Release(); }
            for (int f = 0; f < FrameCount; f++)
                if (uploads[f] != null) { D3D12MemoryDiagnostics.Release(uploads[f], $"Glyph.AtlasUpload[{f}]"); uploads[f]->Release(); }
        }
    }

    private void InitAtlasTexture(ID3D12Device* device)
    {
        D3D12_HEAP_PROPERTIES dp = default; dp.Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC td = default;
        td.Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = (ulong)ATLAS; td.Height = (uint)ATLAS; td.DepthOrArraySize = 1; td.MipLevels = 1;
        td.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM; td.SampleDesc.Count = 1;
        td.Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
        // Query BEFORE CreateCommittedResource, on the SAME desc (audit gpu mem-02): the device-reported allocation
        // requirement, not an inferred ATLAS*ATLAS*1 pixel estimate.
        ulong atlasBytes = D3D12MemoryDiagnostics.AllocationBytes(device, &td);
        ID3D12Resource* tex;
        Check(device->CreateCommittedResource(&dp, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, null, __uuidof<ID3D12Resource>(), (void**)&tex), "CreateTexture");
        _tex = tex;
        D3D12MemoryDiagnostics.Track(_tex,
            D3D12MemoryDiagnostics.NameOrUnknown($"Glyph.AtlasTexture {ATLAS}x{ATLAS} R8", atlasBytes),
            atlasBytes != 0 ? atlasBytes : (ulong)(ATLAS * ATLAS));

        // A staging bank is a dirty-ROW BAND, not a mirror of the atlas: InitialStagingRows × ATLAS bytes, grown at a
        // frame boundary if a frame ever needs more. (Was ATLAS × ATLAS per bank — 3 × 16 MiB that a full-atlas
        // memcpy + full-atlas CopyTextureRegion re-uploaded on EVERY dirty frame.)
        for (int f = 0; f < FrameCount; f++)
        {
            _stagingRows[f] = InitialStagingRows;
            _texUpload[f] = CreateUpload(device, (uint)(ATLAS * InitialStagingRows), $"Glyph.AtlasUpload[{f}]");
        }

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

    // ── Atlas upload: DIRTY-ROW STAGING ───────────────────────────────────────────────────────────────────────────
    // The region-tracking invariant lives on FluentGpu.Text.GlyphAtlasStore (three clauses: rows-plus-one-row-apron /
    // one fixed band mapping per frame / append-only within a frame). What it means HERE, and why this is not the old
    // full-atlas re-copy:
    //
    //   • A flush stages and copies only the atlas ROWS written since the previous flush, ± one apron row. Rows go up
    //     FULL WIDTH (RowPitch = ATLAS, which is 256-aligned for the pitch rule and makes every band offset a whole
    //     number of rows, hence 512-aligned for the placed-footprint rule) — so the packer's 1-texel gutter, which a
    //     LINEAR sampler reads half a texel into at a cell edge, is uploaded with the cell instead of being left as
    //     whatever the previous generation wrote there.
    //   • Multiple UploadIfDirty calls within ONE frame (one per dirty segment) still Map the SAME per-frame staging
    //     bank, and each recorded CopyTextureRegion still reads it at EXECUTION time (one Close+ExecuteCommandLists
    //     per submit). That stays correct because the band mapping is FIXED for the frame — arena byte
    //     (r − BandBase)·ATLAS is always atlas row r — so a copy recorded at flush 1 reads the FINAL bytes of ITS
    //     rows, and the atlas is strictly APPEND-ONLY within a frame (generational resets are deferred to the next
    //     BeginFrame — see PackOrReset), so those final bytes are a superset of what it was recorded for. Hence every
    //     flush RE-STAGES the rows written since the last flush but records a copy only for rows no earlier copy of
    //     this frame covers: within a frame each dirty row is copied exactly once, and the ranges are disjoint.
    //   • A band longer than the staging bank is CLAMPED: the tail stays dirty, drains next frame, grows the bank at
    //     the next BeginFrame, and arms AtlasResetPending so the device repaints instead of keeping the unfaithful
    //     frame as a partial-repaint base. Never silent (Diag "text.atlas"/"stagingShort").
    /// <summary>Glyph-atlas band bytes copied by the most recent <see cref="UploadIfDirty"/> (R8 rows × atlas width).
    /// Always-on plain counter (render thread) — the device folds it into its per-submit <c>GpuFrameCounters</c>.</summary>
    internal long LastUploadBytes { get; private set; }

    /// <summary>The atlas texture has had its first upload (or its first-frame barrier): it is samplable. Until then a
    /// skipped upload would leave it in COPY_DEST under a draw that samples it.</summary>
    internal bool TextureInitialized => _texInitialized;

    /// <summary>True when <see cref="UploadIfDirty"/> would record a copy or a barrier (a dirty band, or the atlas's
    /// first transition) — the caller must not be inside a render pass then.</summary>
    public bool NeedsUpload => _atlas.IsDirty || !_texInitialized;

    public void UploadIfDirty(ID3D12GraphicsCommandList* cmd)
    {
        LastUploadBytes = 0;
        if (!_atlas.IsDirty && _texInitialized) return;
        // Successful uploads clear IsDirty before the next BeginFrame. Reset here as well, or a scrolling
        // stream of new glyphs looks "clean" at every frame boundary and shrinks/regrows every idle window.
        if (_atlas.IsDirty) _atlasIdleFrames = 0;

        int rows = _stagingRows[_active];
        if (!_atlas.TryTakeUpload(rows, out var flush))
        {
            // Nothing to land. The texture is created in COPY_DEST and a committed D3D12 resource starts ZEROED, which
            // is exactly the empty mirror — so a first frame with no glyphs owes only the barrier that makes it
            // samplable (the full-atlas upload of zeros this used to do was pure cost).
            if (!_texInitialized)
            {
                Transition(cmd, _tex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
                _texInitialized = true;
            }
            NoteStagingNeed();
            return;
        }

        ID3D12Resource* up = _texUpload[_active];   // THIS frame's staging bank (BeginFrame set _active)
        if (flush.HasStage)
        {
            void* p; Check(up->Map(0, null, &p), "Map glyph upload");
            _atlas.StageInto(in flush, new Span<byte>(p, rows * ATLAS));
            up->Unmap(0, null);
        }

        if (flush.HasCopy)
        {
            if (_texInitialized) Transition(cmd, _tex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);

            D3D12_TEXTURE_COPY_LOCATION dst = default;
            dst.pResource = _tex; dst.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.Anonymous.SubresourceIndex = 0;
            D3D12_TEXTURE_COPY_LOCATION srcLoc = default;
            srcLoc.pResource = up; srcLoc.Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            srcLoc.Anonymous.PlacedFootprint.Offset = (ulong)flush.CopyOffset;   // a whole number of ATLAS-byte rows ⇒ 512-aligned
            srcLoc.Anonymous.PlacedFootprint.Footprint.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Width = (uint)ATLAS;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Height = (uint)flush.CopyRowCount;
            srcLoc.Anonymous.PlacedFootprint.Footprint.Depth = 1;
            srcLoc.Anonymous.PlacedFootprint.Footprint.RowPitch = (uint)ATLAS;   // R8; both supported edges are 256-aligned
            cmd->CopyTextureRegion(&dst, 0, (uint)flush.CopyRowStart, 0, &srcLoc, null);
            LastUploadBytes += (long)flush.CopyRowCount * ATLAS;

            Transition(cmd, _tex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            Diag.Count("text.atlas", "uploadRows", flush.CopyRowCount);
        }
        else if (!_texInitialized)
        {
            // Refresh-only flush (every dirty row is already covered by an earlier copy this frame) on the very first
            // frame: the barrier is still owed.
            Transition(cmd, _tex, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        }
        _texInitialized = true;
        Diag.Count("text.atlas", "uploadFlush");
        NoteStagingNeed();
    }

    /// <summary>Fold the store's post-flush verdict back into the bank sizing: grow target (next power of two of the
    /// widest band any flush has needed, capped) and the staging-short backlog that arms
    /// <see cref="AtlasResetPending"/>. Cheap enough to run per flush — two int compares in the healthy case.</summary>
    private void NoteStagingNeed()
    {
        int need = _atlas.WantedStagingRows;
        if (need > _wantStagingRows && _wantStagingRows < MaxStagingRows)
        {
            _wantStagingRows = GlyphStagingPolicy.ForDemand(_wantStagingRows, need);
            Diag.Count("text.atlas", "stagingGrowArmed");
        }
        if (_atlas.ShortfallRows > 0 && !_uploadBacklog)
        {
            _uploadBacklog = true;
            Diag.Count("text.atlas", "stagingShort");
            Diag.Event("text.atlas", $"atlas staging short by {_atlas.ShortfallRows} row(s) of {_stagingRows[_active]} — the tail lands next frame (bank grows to {_wantStagingRows} rows)");
        }
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
            _capacity[f] = InitialGlyphCapacity;
            _instances[f] = CreateUpload(device, (uint)(sizeof(GlyphInstance) * InitialGlyphCapacity), "Glyph.InstanceUpload");
            void* ip; _instances[f]->Map(0, null, &ip); _mapped[f] = (GlyphInstance*)ip;   // persistently mapped
            _gradInstances[f] = CreateUpload(device, (uint)(sizeof(GradGlyphInstance) * MaxGradGlyphs), "Glyph.GradInstanceUpload");
            void* gp; _gradInstances[f]->Map(0, null, &gp); _mappedGrad[f] = (GradGlyphInstance*)gp;
        }
    }

    public void BeginFrame(int slot)
    {
        _active = ((slot % FrameCount) + FrameCount) % FrameCount;   // this frame's instance buffer — already fenced, so no CPU↔GPU race
        if (_capacity[_active] < _wantCapacity) GrowBank(_active);          // fenced here ⇒ the only safe moment to swap it
        // The atlas has been clean for a long stretch ⇒ hand the grown reserve back. Counted in SUBMITTED frames and
        // deliberately generous: a shrink immediately followed by a big reset costs one stagingShort frame, which the
        // existing AtlasResetPending → forced-repaint machinery already handles and gate.atlas.upload.clamp/.drain
        // already pin. Never released to null — UploadIfDirty dereferences the bank unconditionally.
        int warmRows = GlyphStagingPolicy.AfterIdle(_wantStagingRows, _atlasIdleFrames, StagingIdleFrames);
        if (warmRows != _wantStagingRows)
        {
            _wantStagingRows = warmRows;
            _atlasIdleFrames = 0;
        }
        if (_stagingRows[_active] != _wantStagingRows) ResizeStagingBank(_active);   // a fenced bank is the ONLY safe swap point
        _activeGva = _instances[_active]->GetGPUVirtualAddress();
        _activeGradGva = _gradInstances[_active]->GetGPUVirtualAddress();
        _cursor = 0;
        _gradCursor = 0;
        _dropped = 0;
        _gradDropped = 0;
        _frame++;
        // Deferred generational flush (text.md §5.3): the atlas overflowed while the PREVIOUS frame recorded.
        // Nothing is mid-shape here and no quads reference the atlas yet this frame, so the repack can never
        // strand a stale UV. ResetAtlas clears _cache/_iconCache/_runCache and bumps the epoch.
        if (_resetPending) { _resetPending = false; ResetAtlas(); }
        // Open the atlas upload frame AFTER the deferred reset: the band mapping (arena offset 0 ↔ one atlas row) is
        // per-frame, and the reset it may follow rewinds the packer to the top of a now-empty mirror.
        _uploadBacklog = false;
        _atlasIdleFrames = _atlas.IsDirty ? 0 : _atlasIdleFrames + 1;
        _atlas.BeginFrame();
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
    public bool Record(ID3D12GraphicsCommandList* cmd, List<GlyphInstance> instances, float vpW, float vpH, bool rebind = true, bool stencilTest = false)
    {
        int start = _cursor;
        int count = Math.Min(instances.Count, _capacity[_active] - start);
        if (count < instances.Count) NoteOverflow(start + instances.Count);
        if (count <= 0) { _dropped += instances.Count; return false; }
        _dropped += instances.Count - count;
        for (int i = 0; i < count; i++) _mapped[_active][start + i] = instances[i];
        _cursor += count;

        // A tier-3 stencil scope needs the EQUAL-tested clone, and it must be bound whether or not `rebind` says the
        // glyph pipe is already current (that flag tracks WHICH pipe is bound, not which VARIANT) — so bind the whole
        // static block when the variant is in play. Null clone ⇒ the scope degrades to its plain scissor.
        ID3D12PipelineState* stencil = stencilTest ? StencilTestPso(grad: false) : null;
        if (rebind || stencil != null)
        {
            ID3D12DescriptorHeap* heap = _srvHeap;
            cmd->SetDescriptorHeaps(1, &heap);
            cmd->SetGraphicsRootSignature(_rootSig);
            cmd->SetPipelineState(stencil != null ? stencil : _pso);
            _vpConstants[0] = vpW;
            _vpConstants[1] = vpH;
            fixed (float* vp = _vpConstants)
                cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
            cmd->SetGraphicsRootDescriptorTable(1, _srvGpu);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView) cmd->IASetVertexBuffers(0, 1, qv);
        }
        cmd->SetGraphicsRootShaderResourceView(2, _activeGva + (ulong)(start * sizeof(GlyphInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0); GpuDrawCount.Frame++;
        return true;
    }

    /// <summary>Draw the sub-glyph gradient-wipe instances (active lyric line + glow) with the gradient PSO — same atlas,
    /// viewport, quad and per-frame banking as <see cref="Record"/>, into whatever RT is bound (so a blur layer captures the
    /// glow's gradient glyphs exactly like normal glyphs).</summary>
    public bool RecordGradient(ID3D12GraphicsCommandList* cmd, List<GradGlyphInstance> instances, float vpW, float vpH, bool rebind = true, bool stencilTest = false)
    {
        int start = _gradCursor;
        int count = Math.Min(instances.Count, MaxGradGlyphs - start);
        if (count <= 0) { _dropped += instances.Count; NoteGradBudget(instances.Count, start); return false; }
        _dropped += instances.Count - count;
        for (int i = 0; i < count; i++) _mappedGrad[_active][start + i] = instances[i];
        _gradCursor += count;
        NoteGradBudget(instances.Count - count, _gradCursor);

        ID3D12PipelineState* stencilGrad = stencilTest ? StencilTestPso(grad: true) : null;
        if (rebind || stencilGrad != null)
        {
            ID3D12DescriptorHeap* heap = _srvHeap;
            cmd->SetDescriptorHeaps(1, &heap);
            cmd->SetGraphicsRootSignature(_rootSig);
            cmd->SetPipelineState(stencilGrad != null ? stencilGrad : _psoGrad);
            _vpConstants[0] = vpW;
            _vpConstants[1] = vpH;
            fixed (float* vp = _vpConstants)
                cmd->SetGraphicsRoot32BitConstants(0, 2, vp, 0);
            cmd->SetGraphicsRootDescriptorTable(1, _srvGpu);
            cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            fixed (D3D12_VERTEX_BUFFER_VIEW* qv = &_quadView) cmd->IASetVertexBuffers(0, 1, qv);
        }
        cmd->SetGraphicsRootShaderResourceView(2, _activeGradGva + (ulong)(start * sizeof(GradGlyphInstance)));
        cmd->DrawInstanced(4, (uint)count, 0, 0); GpuDrawCount.Frame++;
        return true;
    }

    /// <summary>Account the sub-glyph WIPE instance budget for one <see cref="RecordGradient"/> call: the drop count and
    /// the session high-water mark (both cheap enough to stay compiled in — once per record, not per glyph), plus the
    /// LOUD report, which is <c>[Conditional]</c>-erased on Release along with its argument formatting. Before this, a
    /// budget overflow only nudged the shared <see cref="DroppedInstances"/> total and the karaoke line silently lost
    /// its tail; now it names itself. The settled-split fast path in <see cref="LayoutRunGradient"/> is what keeps the
    /// peak at ~one line, so this counter is also the headroom evidence for the lyrics surfaces.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteGradBudget(int dropped, int used)
    {
        if (used > _gradPeak) _gradPeak = used;
        if (dropped <= 0) return;
        _gradDropped += dropped;
        Diag.Count("text.grad", "dropped", dropped);
        Diag.Event("text.grad", $"gradient-glyph budget exhausted: dropped {dropped} instance(s) (budget {MaxGradGlyphs}) — the karaoke wipe is TRUNCATED this frame");
    }

    // An overflow this frame: remember the capacity the frame needed (next power of two, capped), so every bank grows
    // to it as BeginFrame reaches it. Beyond the cap the tail stays dropped and counted — visible, never silent.
    private void NoteOverflow(int needed)
    {
        int want = _capacity[_active];
        while (want < needed && want < MaxGlyphCapacity) want <<= 1;
        if (want > _wantCapacity) { _wantCapacity = want; Diag.Count("text.glyphs", "bankOverflow"); }
    }

    /// <summary>Re-allocate one atlas staging bank at the new row target. Called ONLY from <see cref="BeginFrame"/>,
    /// on the bank that frame is about to use: that bank's fence has retired there, so no in-flight copy can still be
    /// reading the buffer being released. (The banks are Map/Unmap-per-flush, never persistently mapped, so there is
    /// nothing to unmap first.)</summary>
    /// <summary>Re-create one staging bank at <see cref="_wantStagingRows"/>. Both directions: the bank used to only
    /// ever grow, so one generational reset with a big working set took all three to MaxStagingRows — 24 MiB of pinned
    /// host memory — and held them there for the process lifetime, on a UMA laptop, for an atlas that is dirty on
    /// roughly one frame in ten thousand. Called only from <see cref="BeginFrame"/>, which is the one point where this
    /// bank is known fenced.</summary>
    private void ResizeStagingBank(int f)
    {
        bool shrinking = _stagingRows[f] > _wantStagingRows;
        var replacement = CreateUpload(_device, (uint)(ATLAS * _wantStagingRows), $"Glyph.AtlasUpload[{f}]");
        D3D12MemoryDiagnostics.Release(_texUpload[f], $"Glyph.AtlasUpload[{f}]");
        _texUpload[f]->Release();
        _texUpload[f] = replacement;
        _stagingRows[f] = _wantStagingRows;
        Diag.Count("text.atlas", shrinking ? "stagingShrink" : "stagingGrow");
        Diag.Set("text.atlas", "stagingBytes", AtlasStagingBytes);   // all banks together — the memory story, not a per-frame value
    }

    private void GrowBank(int f)
    {
        _instances[f]->Unmap(0, null);
        D3D12MemoryDiagnostics.Release(_instances[f], "Glyph.InstanceUpload");
        _instances[f]->Release();
        _instances[f] = CreateUpload(_device, (uint)(sizeof(GlyphInstance) * _wantCapacity), "Glyph.InstanceUpload");
        void* ip; _instances[f]->Map(0, null, &ip); _mapped[f] = (GlyphInstance*)ip;   // persistently mapped, like the ctor
        _capacity[f] = _wantCapacity;
        Diag.Count("text.glyphs", "bankGrow");
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

    /// <summary>The lazily-built EQUAL-tested clone of the plain (or gradient-wipe) glyph PSO. Both are built on the
    /// first stencil scope of the process; null ⇒ the build failed and glyph runs inside the scope fall back to the
    /// scissor (counted on <c>Diag "d3d12"/"stencilFallback"</c> by the device).</summary>
    private ID3D12PipelineState* StencilTestPso(bool grad)
    {
        if (!_stencilTried)
        {
            _stencilTried = true;
            _psoStencilTest = StencilPso.TryBuildQuadEqualTest(_device, _rootSig, Hlsl, "VSMain", "PSMain", "glyph", depthClip: false);
            _psoGradStencilTest = StencilPso.TryBuildQuadEqualTest(_device, _rootSig, HlslGrad, "VSMain", "PSMain", "glyphGrad", depthClip: false);
        }
        return grad ? _psoGradStencilTest : _psoStencilTest;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        for (int f = 0; f < FrameCount; f++)
        {
            if (_instances[f] != null) { _instances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_instances[f], "Glyph.InstanceUpload"); _instances[f]->Release(); _instances[f] = null; }
            if (_gradInstances[f] != null) { _gradInstances[f]->Unmap(0, null); D3D12MemoryDiagnostics.Release(_gradInstances[f], "Glyph.GradInstanceUpload"); _gradInstances[f]->Release(); _gradInstances[f] = null; }
            if (_texUpload[f] != null) { D3D12MemoryDiagnostics.Release(_texUpload[f], $"Glyph.AtlasUpload[{f}]"); _texUpload[f]->Release(); _texUpload[f] = null; }
        }
        if (_quad != null) { D3D12MemoryDiagnostics.Release(_quad, "Glyph.QuadUpload"); _quad->Release(); _quad = null; }
        if (_psoGrad != null) { _psoGrad->Release(); _psoGrad = null; }
        if (_psoStencilTest != null) { _psoStencilTest->Release(); _psoStencilTest = null; }
        if (_psoGradStencilTest != null) { _psoGradStencilTest->Release(); _psoGradStencilTest = null; }
        if (_pso != null) _pso->Release();
        if (_rootSig != null) _rootSig->Release();
        if (_srvHeap != null) { D3D12MemoryDiagnostics.Release(_srvHeap, "Glyph.SrvHeap"); _srvHeap->Release(); }
        if (_tex != null) { D3D12MemoryDiagnostics.Release(_tex, "Glyph.AtlasTexture"); _tex->Release(); _tex = null; }
        foreach (var f in _faces.Values) if (f != 0) ((IDWriteFontFace*)f)->Release();
        _faces.Clear();
        if (_dw4 != null) { _dw4->Release(); _dw4 = null; }
        if (_dw2 != null) { _dw2->Release(); _dw2 = null; }
        if (_dw != null) _dw->Release();
    }
}
