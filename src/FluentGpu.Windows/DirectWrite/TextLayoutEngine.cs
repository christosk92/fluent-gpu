using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Text.DirectWrite;

/// <summary>One laid-out glyph: post-shaping glyph id, the DWrite face it belongs to (as <c>nint</c>), and its draw origin
/// in DIP — X within the paragraph (line-relative origin folded in), Y = its line baseline from the top, both shifted by
/// the shaper's GPOS glyph offset (combining marks attach to their base; zero for ordinary glyphs). Pen positions for
/// hit-testing are in <see cref="LaidCluster"/>.
/// <see cref="Size"/> is the DIP em size the glyph was shaped at (spans may override the base size) and
/// <see cref="Span"/> the index of the <see cref="SpanStyle"/> it belongs to (−1 = the base style) — the renderer
/// rasterizes at <see cref="Size"/> and tints per span (rtb-01 inline runs).</summary>
public readonly struct LaidGlyph
{
    public readonly ushort Gid;
    public readonly nint Face;
    public readonly float X, Y;
    public readonly float Size;
    public readonly short Span;
    public LaidGlyph(ushort gid, nint face, float x, float y, float size, short span)
    { Gid = gid; Face = face; X = x; Y = y; Size = size; Span = span; }
}

/// <summary>Source mapping for one laid glyph — the hit-test companion to <see cref="LaidGlyph"/> (parallel to
/// <see cref="TextLayoutEngine.Glyphs"/> minus the synthetic trim ellipsis): the UTF-16 index of the cluster's first
/// source char (taken from the shaper's cluster map, never re-derived from char counts), the glyph's leading pen X and
/// advance on its line, and its line index. Entries are stored in the VISUAL order glyphs were placed (after BiDi L2
/// reordering), so an in-order scan walks the line left→right.</summary>
public readonly struct LaidCluster
{
    public readonly int Cluster;
    public readonly float X;
    public readonly float Advance;
    public readonly int Line;
    public LaidCluster(int cluster, float x, float advance, int line) { Cluster = cluster; X = x; Advance = advance; Line = line; }
}

/// <summary>One laid-out line: the UTF-16 source range it covers — [StartChar, EndChar), where EndChar is the next
/// line's StartChar (the text length on the last line) — its vertical band (Top/Height), its rendered advance width
/// (including a trim ellipsis), and its window [FirstGlyph, FirstGlyph + GlyphCount) into
/// <see cref="TextLayoutEngine.Clusters"/>. Chars dropped by trimming have no cluster entry; queries clamp them to the
/// trimmed edge.</summary>
public struct LaidLine
{
    public int StartChar, EndChar;
    /// <summary>For an overflow-suffix line, the visible body ends here and resumes at <see cref="SuffixStart"/>.
    /// Both values are -1 for an ordinary contiguous line.</summary>
    public int VisibleBodyEnd, SuffixStart;
    public float Top, Height, Width;
    public int FirstGlyph, GlyphCount;
    /// <summary>The line's window into <see cref="TextLayoutEngine.Glyphs"/> (the laid glyphs, including a trim
    /// ellipsis — so <see cref="LaidCount"/> &gt; <see cref="GlyphCount"/> marks an ellipsized line).</summary>
    public int FirstLaid, LaidCount;
    /// <summary>The alignment offset applied to the whole line (TextAlign Center/End; 0 for Start and Justify) — the
    /// caret x of an EMPTY line (a line with no clusters to read the offset from).</summary>
    public float X;
}

/// <summary>
/// The DirectWrite layout engine (text.md §8): itemize → shape (per run) → wrap (UAX #14 break opportunities) → position,
/// with BiDi L2 reordering per line. Drives BOTH measurement (metrics) and rendering (positioned glyphs) so they layout
/// identically — and the editor queries (<see cref="HitTest"/>/<see cref="CaretAt"/>/<see cref="RangeRects"/>) read the
/// retained per-glyph cluster + per-line tables of the SAME layout, so hit-testing matches rendering exactly. Owns its
/// itemizer + shaper + face cache; reusable, single-thread-confined. Results live in reused grow-only buffers
/// (valid until the next <see cref="Layout"/>; queries fire per keystroke/pointer-move with 0 steady-state allocation).
/// v1: design-advance shaping with kerning/ligatures/complex-script; font fallback + color glyphs are layered on in
/// later phases; query edge mapping is leading=left (LTR — BiDi-correct edges arrive with the RTL workstream, but the
/// scan is already cluster-correct and walks the visual order).
/// </summary>
public sealed unsafe partial class TextLayoutEngine : IDisposable
{
    // Faces come from the one shared registry (E9), never from a private cache.
    private readonly FontRegistry _registry;
    private readonly TextCaseScratch _case = new();
    private IDWriteFactory* _dw;
    private readonly DWriteItemizer _itemizer;
    private readonly DWriteTextShaper _shaper;

    /// <summary>Cached per-face design metrics (design units; positive-up from the baseline as DWrite reports them).
    /// <c>Cap</c> is the OS/2 cap height (TextLineBounds.Tight trims the line box to cap..baseline); fonts that report
    /// none get the ~0.7 em convention, matching the headless model (HeadlessFontSystem.cs:13).</summary>
    private struct FaceMetrics { public ushort Em; public short Asc, Desc, UnderPos, StrikePos; public ushort UnderThk; public ushort Cap; }
    // Font fallback (Phase 6): split a run by glyph coverage so CJK/emoji/symbols resolve to a covering face.
    private IDWriteFontFallback* _fallback;
    private IDWriteFontCollection* _sysColl;
    private TextAnalysisSourceCcw* _fsrc;

    private readonly List<ItemRun> _runs = new();
    private readonly List<BreakOpp> _breaks = new();
    private RunGlyph[] _glyphs = new RunGlyph[64];
    private int _glyphCount;
    private LaidGlyph[] _laid = new LaidGlyph[64];
    private int _laidCount;
    // Editor-query retention (grow-only, reused across Layout calls): per-laid-glyph cluster map + per-line table.
    private LaidCluster[] _clusters = new LaidCluster[64];
    private int _clusterCount;
    private LaidLine[] _lines = new LaidLine[4];
    private int _lineRecCount;
    private int _textLen;
    private ushort _ellGid; private float _ellAdv; private nint _ellFace; private float _ellSize;
    // Per-span trim ellipsis (spanned paragraphs only; grow-only, parallel, indexed by span): the "…" glyph in each
    // span's own face at its own size, so a line cut inside a span ends in THAT span's ellipsis (EmitLine) — never the
    // paragraph base size/weight. _spanEllCount == 0 for plain text (the single-style trim path, unchanged).
    private ushort[] _spanEllGid = new ushort[4]; private nint[] _spanEllFace = new nint[4];
    private float[] _spanEllAdv = new float[4]; private float[] _spanEllSize = new float[4];
    private int _spanEllCount;

    public float Width { get; private set; }
    public float Height { get; private set; }
    public float Baseline { get; private set; }
    public int LineCount { get; private set; }
    /// <summary>Underline bar position for the laid family/size, measured DOWN from the line top (top-down DIP;
    /// DWrite's positive-up design value is flipped over <see cref="Baseline"/>). Per-line: add the line's Top.</summary>
    public float UnderlineY { get; private set; }
    public float UnderlineThickness { get; private set; }
    /// <summary>Strikethrough bar position, measured DOWN from the line top (same frame as <see cref="UnderlineY"/>).</summary>
    public float StrikeY { get; private set; }
    public ReadOnlySpan<LaidGlyph> Glyphs => _laid.AsSpan(0, _laidCount);
    /// <summary>Cluster map of the current layout, in placement (visual) order — parallel to <see cref="Glyphs"/> minus
    /// the synthetic trim ellipsis. Valid until the next <see cref="Layout"/>.</summary>
    public ReadOnlySpan<LaidCluster> Clusters => _clusters.AsSpan(0, _clusterCount);
    /// <summary>Per-line table of the current layout (always ≥ 1 entry, even for empty text). Valid until the next
    /// <see cref="Layout"/>.</summary>
    public ReadOnlySpan<LaidLine> Lines => _lines.AsSpan(0, _lineRecCount);

    // OffX/OffY: the shaper's GPOS glyph offset (mark-to-base / mark-to-mark attachment) in the VISUAL top-down frame — ink only; Advance (pen, wrap, hit-test) is unaffected.
    private struct RunGlyph { public ushort Gid; public nint Face; public float Advance; public int Cluster; public byte Level; public float Size; public short Span; public float OffX, OffY; }

    // ── Shape cache ─────────────────────────────────────────────────────────────────────────────────
    // Shaping (itemize → shape) is width-INDEPENDENT — only WrapAndPosition consumes maxWidth. A drag-resize
    // re-measures the SAME text at a new width every frame; without this each call re-runs the COM-heavy itemize +
    // per-glyph fallback + shape phase. The shaped glyphs + break opportunities + line metrics are kept, keyed on a
    // verified 64-bit hash of the SHAPE inputs (content + family + weight + size + spacing + lineHeight + stacking +
    // lineBounds + features); a (text,style) match restores them and replays ONLY WrapAndPosition at the new width. Plain
    // (no-span) text only — spanned inline runs (rtb-01) re-shape. The store is the PROCESS's (ShapeStore.Shared, see
    // TextLayoutEngine.ShapeStore.cs): the UI measure engine and the render thread's glyph engine shape a string once
    // between them (PERF r3 UI, C4). Null (a validator's engine) = no cache: every layout shapes.
    // 2048, not 256: a 1.5k-row track list keeps ~40 rows x ~8 strings on screen, so a 256-entry cache could not even
    // hold the visible page — every scroll-back, every width re-wrap and every recycle onto a string seen a moment
    // ago re-ran DirectWrite itemize+shape. Eviction is batched so a full store costs one pass per ~cap/4 misses.
    private const int ShapeCacheCap = 2048;
    private readonly ShapeStore? _store;

    /// <summary>Diagnostics/regression counter: the number of ACTUAL itemize+shape passes. A width-only re-wrap of
    /// already-shaped text does NOT bump it, so a resize that re-wraps cached text leaves this flat.</summary>
    public long ShapeCount { get; private set; }

    private static long ShapeHash(ReadOnlySpan<char> text, string? family, int weight, float size, float charSpacing, float lineHeight, int stacking, int lineBounds, uint features)
    {
        ulong h = 14695981039346656037UL; const ulong P = 1099511628211UL;
        for (int i = 0; i < text.Length; i++) { h ^= text[i]; h *= P; }
        if (family != null) for (int i = 0; i < family.Length; i++) { h ^= family[i]; h *= P; }
        h ^= (uint)weight; h *= P;
        h ^= (uint)BitConverter.SingleToInt32Bits(size); h *= P;
        h ^= (uint)BitConverter.SingleToInt32Bits(charSpacing); h *= P;
        h ^= (uint)BitConverter.SingleToInt32Bits(lineHeight); h *= P;
        h ^= (uint)stacking; h *= P;
        h ^= (uint)lineBounds; h *= P;
        h ^= features; h *= P;   // OpenType features change glyph ids/advances — a shaping input
        return (long)h;
    }

    private static bool NanSafeEq(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || a == b;

    public TextLayoutEngine(FontRegistry? registry = null) : this(registry, ShapeStore.Shared) { }

    /// <summary>An engine over <paramref name="store"/>: the process's (the public constructor), a private one (a self-test that
    /// counts its own shapes), or none (a validator: every layout shapes).</summary>
    internal TextLayoutEngine(FontRegistry? registry, ShapeStore? store)
    {
        _store = store;
        if (registry is null) DWriteFontFaceSource.EnsureInstalled();
        _registry = registry ?? FontRegistry.Shared;
        IDWriteFactory* f;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
        _dw = f;
        _itemizer = new DWriteItemizer();
        _shaper = new DWriteTextShaper(_dw);
        ResolveFace("", 400, out _);   // warm the body chain's Regular face so metrics exist from the first measure

        // Best-effort system font fallback (null ⇒ base-face only, no coverage splitting).
        IDWriteFactory2* f2;
        if ((int)_dw->QueryInterface(__uuidof<IDWriteFactory2>(), (void**)&f2) >= 0 && f2 != null)
        {
            IDWriteFontFallback* fb;
            if ((int)f2->GetSystemFontFallback(&fb) >= 0) _fallback = fb;
            f2->Release();
        }
        IDWriteFontCollection* coll;
        if ((int)_dw->GetSystemFontCollection(&coll, BOOL.FALSE) >= 0) _sysColl = coll;
        _fsrc = TextAnalysisSourceCcw.Create();
    }

    /// <summary>Lay out <paramref name="text"/>; fills <see cref="Glyphs"/> + metrics. <paramref name="wrap"/>/<paramref name="trim"/>
    /// are the TextWrap/TextTrim enum ints; <paramref name="size"/> is DIP. <paramref name="weight"/> is the NUMERIC
    /// font weight — the int IS the DWRITE_FONT_WEIGHT (clamped 1..999; ≤0 → 400). <paramref name="charSpacing"/> is
    /// WinUI CharacterSpacing in 1/1000 em; <paramref name="lineHeight"/> (DIP; NaN/≤0 = font-natural) resolves per
    /// <paramref name="stacking"/> (LineStacking enum int) and <paramref name="lineBounds"/> (TextLineBounds enum int).
    /// <para><paramref name="spans"/> (rtb-01 inline runs): per-char-range style overlays applied over the base style —
    /// the text still shapes as ONE flow (one itemize + one wrap pass, like a WinUI paragraph's inline collection);
    /// each span resolves its own face/weight/size for its range. <paramref name="names"/> resolves span family ids
    /// (null ⇒ span families fall back to the base family). Line box: the MAX ascent/descent across base + spans —
    /// one uniform line height per paragraph (the per-line LineStackingStrategy=MaxHeight refinement for mixed-size
    /// lines is future work; uniform-size spans, the overwhelming case, are exact).</para>
    /// <para><paramref name="typography"/> is <c>TextTypography.Pack(align, features, case)</c>: the TextCase bits are applied
    /// to the text before shaping (E19, into a reused buffer); the FontFeatures bits are a
    /// SHAPING input (they enter the shape cache key and reach both DWrite shaping calls); the TextAlign bits are a
    /// POSITIONING input applied after wrap against <paramref name="alignWidth"/> — the box width the run is drawn in
    /// (+Inf ⇒ the widest line). Alignment never changes <see cref="Width"/>/<see cref="Height"/>.</para></summary>
    public void Layout(ReadOnlySpan<char> text, string family, int weight, float size, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing = 0f, float lineHeight = float.NaN, int stacking = 0, int lineBounds = 0,
        ReadOnlySpan<SpanStyle> spans = default, StringTable? names = null, int overflowSuffixStart = -1,
        int typography = 0, float alignWidth = float.PositiveInfinity)
    {
        long censusT0 = TextDiagnostics.ShapeCensus && !_isValidator ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;   // --fg shape-census
        ReadOnlySpan<char> argText = text; string argFamily = family; int argWeight = weight;   // --fg shape-validate re-lays the call as made
        if (weight <= 0) weight = 400; else if (weight > 999) weight = 999;   // DWRITE_FONT_WEIGHT range
        // E9: a run with no family of its own shapes in the theme's body chain. Key every cache on the CHAIN, not on "", so a
        // body-font change misses the shape cache instead of replaying glyphs of the previous font.
        _registry.SyncBody();
        if (string.IsNullOrEmpty(family)) family = _registry.BodyChain;
        // E19: the case transform is applied HERE, at shaping, into a reused buffer - the written text is never rewritten,
        // nothing is allocated per render, and (length preserving) every cluster/caret index is the same.
        TextCase textCase = TextTypography.CaseOf(typography);
        if (textCase != TextCase.AsWritten) text = _case.Get(text, textCase);
        _glyphCount = 0; _laidCount = 0; _clusterCount = 0; _lineRecCount = 0; _spanEllCount = 0;
        uint features = (uint)TextTypography.FeaturesOf(typography);
        _align = TextTypography.AlignOf(typography);
        _alignWidth = alignWidth;

        // Shape cache: a width-only change replays only WrapAndPosition (shaping is width-independent). Plain text only
        // (spanned inline runs re-shape — never the resize hot path). On a hit, restore the shaped state and re-wrap.
        bool cacheable = spans.IsEmpty && text.Length > 0 && _store is not null;
        long shapeKey = 0;
        if (cacheable)
        {
            shapeKey = ShapeHash(text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds, features);
            if (_store!.TryRestore(shapeKey, text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds, features, this, out float hitLineH))
            {
                WrapAndPosition(size, 1f, hitLineH, maxWidth, wrap, trim, maxLines, _ellFace, overflowSuffixStart);
                if (TextDiagnostics.ShapeValidate && !_isValidator)
                    ValidateHit(argText, argFamily, argWeight, size, maxWidth, wrap, trim, maxLines, charSpacing, lineHeight, stacking, lineBounds,
                        overflowSuffixStart, typography, alignWidth);
                if (censusT0 != 0) CensusLayout(hit: true, cacheable, shapeKey, censusT0, 0);
                return;
            }
        }

        var face = ResolveFace(family, weight, out FaceMetrics fm);
        float scale = fm.Em > 0 ? size / fm.Em : size / 2048f;
        // TextLineBounds.Tight (WinUI TextBlock.TextLineBounds; default Full, TextBlock_themeresources.xaml:17):
        // trim the line box to cap-height..baseline — the box top sits at the cap top, the bottom at the baseline —
        // so vertical centering is optical (e.g. PersonPicture initials, PersonPicture.xaml:66). Affects the measured
        // height AND the in-box baseline; glyph descenders/underline simply extend below the reported box.
        float ascUnits = lineBounds == (int)TextLineBounds.Tight ? fm.Cap : fm.Asc;
        float descUnits = lineBounds == (int)TextLineBounds.Tight ? 0f : fm.Desc;
        float ascDip = ascUnits * scale, descDip = descUnits * scale;
        // Span runs: the line box is the max ascent/descent across the base AND every span face/size, so a larger
        // span lifts the whole paragraph's lines instead of overlapping its neighbors.
        for (int si = 0; si < spans.Length; si++)
        {
            ref readonly var sp = ref spans[si];
            string fam2 = family;
            if (!sp.FontFamily.IsEmpty && names is not null) { var f2 = names.Resolve(sp.FontFamily); if (f2.Length > 0) fam2 = f2; }
            int w2 = sp.Weight != 0 ? sp.Weight : weight;
            float sz2 = sp.SizeDip > 0f ? sp.SizeDip : size;
            ResolveFace(fam2, w2, out FaceMetrics fm2);
            float scale2 = fm2.Em > 0 ? sz2 / fm2.Em : sz2 / 2048f;
            float asc2 = (lineBounds == (int)TextLineBounds.Tight ? fm2.Cap : fm2.Asc) * scale2;
            float desc2 = (lineBounds == (int)TextLineBounds.Tight ? 0f : (float)fm2.Desc) * scale2;
            if (asc2 > ascDip) ascDip = asc2;
            if (desc2 > descDip) descDip = desc2;
        }
        float lineH = ascDip + descDip;
        if (lineH <= 0f) lineH = size * 1.3f;
        Baseline = ascDip;
        // WinUI TextBlock.LineHeight + LineStackingStrategy (BaseTextBlockStyle default MaxHeight,
        // TextBlock_themeresources.xaml:16): MaxHeight = line advance max(font-natural, LineHeight);
        // BlockLineHeight = the advance IS LineHeight. The baseline scales proportionally with the resolved height
        // (resolved × naturalBaseline/naturalHeight — the IDWriteTextLayout SetLineSpacing UNIFORM convention),
        // which is exact-identity when the resolved height equals the natural one.
        if (!float.IsNaN(lineHeight) && lineHeight > 0f)
        {
            float resolved = stacking == (int)LineStacking.BlockLineHeight ? lineHeight : MathF.Max(lineH, lineHeight);
            Baseline = resolved * (Baseline / lineH);
            lineH = resolved;
        }
        // Decoration bars: DWrite reports positions positive-UP from the baseline (underline usually negative = below);
        // engine Y runs top-down, so flip over the baseline. A face with no underline thickness gets a 1-DIP-ish bar.
        UnderlineY = Baseline - fm.UnderPos * scale;
        UnderlineThickness = fm.UnderThk > 0 ? fm.UnderThk * scale : MathF.Max(1f, size / 14f);
        StrikeY = Baseline - fm.StrikePos * scale;
        int n = text.Length;
        _textLen = n;
        if (n == 0)
        {
            Width = 0; Height = lineH; LineCount = 1;
            EnsureLines(1);
            _lines[_lineRecCount++] = new LaidLine
            {
                StartChar = 0, EndChar = 0, VisibleBodyEnd = -1, SuffixStart = -1,
                Top = 0f, Height = lineH, Width = 0f, FirstGlyph = 0, GlyphCount = 0,
            };
            ApplyAlignment();   // an empty Center/End paragraph still places its caret at the aligned x
            return;
        }

        // Ellipsis glyph for this face (for trim) — a single glyph, no shaping needed.
        _ellFace = (nint)face; _ellGid = 0; _ellAdv = 0f; _ellSize = size;
        if (face != null)
        {
            uint ec = '…'; ushort eg; face->GetGlyphIndices(&ec, 1, &eg);
            DWRITE_GLYPH_METRICS egm; face->GetDesignGlyphMetrics(&eg, 1, &egm, BOOL.FALSE);
            _ellGid = eg; _ellAdv = (float)egm.advanceWidth * scale;
        }
        // …and one per span, in the span's resolved face (family + weight) at its size — the same resolution the shaping
        // loop below uses for the span's text. A face without a "…" glyph falls back to the base ellipsis (still tinted
        // by the span). Spanned text never enters the shape cache, so nothing here needs caching.
        if (!spans.IsEmpty) ResolveSpanEllipses(spans, family, weight, size, names);

        _itemizer.Itemize(text, _runs, _breaks);

        // WinUI TextElement.CharacterSpacing: tracking in 1/1000 em → a per-glyph TRAILING advance adjustment applied
        // AFTER shaping (the IDWriteTextLayout1::SetCharacterSpacing model). This engine is a custom itemize→shape
        // pipeline (there is no IDWriteTextLayout object to call SetCharacterSpacing on), so the post-shaping advance
        // adjustment IS the implementation, not a fallback approximation — wrap/trim/queries all see the spaced advances.
        // Em-relative, so a span that overrides the size scales its own tracking with it.

        // Shape each run, splitting at SPAN boundaries (per-segment face/weight/size — the rtb-01 inline-run overlay)
        // and then by font-fallback coverage so CJK/emoji/symbols resolve to a covering face. One itemize + one wrap
        // pass over the whole paragraph: spans restyle ranges, they never re-flow independently.
        fixed (char* p = text)
        {
            _fsrc->Text = p; _fsrc->Len = (uint)n;
            foreach (var run in _runs)
            {
                int pos = run.Start, runEnd = run.Start + run.Length;
                while (pos < runEnd)
                {
                    int spanIdx = -1;
                    int segEnd = spans.IsEmpty ? runEnd : SpanSegmentEnd(spans, pos, runEnd, out spanIdx);
                    string segFamily = family; int segWeight = weight; float segSize = size; short segSpan = -1;
                    IDWriteFontFace* segFace = face;
                    if (spanIdx >= 0)
                    {
                        ref readonly var sp = ref spans[spanIdx];
                        segSpan = (short)spanIdx;
                        if (sp.Weight != 0) segWeight = sp.Weight;
                        if (sp.SizeDip > 0f) segSize = sp.SizeDip;
                        if (!sp.FontFamily.IsEmpty && names is not null) { var f2 = names.Resolve(sp.FontFamily); if (f2.Length > 0) segFamily = f2; }
                        if (segWeight != weight || segFamily != family) segFace = ResolveFace(segFamily, segWeight, out _);
                    }
                    float segSpacing = charSpacing != 0f ? segSize * charSpacing / 1000f : 0f;
                    int remaining = segEnd - pos;
                    while (remaining > 0)
                    {
                        ResolveRunFace(segFamily, segWeight, segFace, pos, remaining, out IDWriteFontFace* subFace, out int subLen);
                        if (subLen <= 0) subLen = remaining;
                        var shaped = _shaper.Shape(p, pos, subLen, subFace, segSize, run.ScriptId, run.ScriptShapes, run.IsRightToLeft, features);
                        EnsureGlyphs(_glyphCount + shaped.Length);
                        foreach (var g in shaped)
                        {
                            // A hard line-break char (LF/CR) carries a break opportunity but no ink; the shaper still returns a
                            // .notdef glyph that would paint as a [] tofu box at every break. Keep the cluster (the editor's
                            // caret/terminator math relies on it) but make it NON-DRAWING (Face 0 ⇒ GlyphRenderer skips it,
                            // GlyphRenderer.cs) and zero-advance.
                            bool brk = (uint)g.Cluster < (uint)n && FluentGpu.Text.LineBreaker.IsHardBreak(p[g.Cluster]);
                            // GPOS offset → visual top-down frame: advanceOffset runs along the reading direction (leftward in an
                            // RTL run, whose glyphs EmitLine reverses into left-edge slots), ascenderOffset is positive-up.
                            float offX = brk ? 0f : (run.IsRightToLeft ? -g.OffsetX : g.OffsetX);
                            float offY = brk ? 0f : -g.OffsetY;
                            _glyphs[_glyphCount++] = new RunGlyph { Gid = g.GlyphId, Face = brk ? 0 : (nint)subFace, Advance = brk ? 0f : g.Advance + segSpacing, Cluster = g.Cluster, Level = run.BidiLevel, Size = segSize, Span = segSpan, OffX = offX, OffY = offY };
                        }
                        pos += subLen; remaining -= subLen;
                    }
                }
            }
        }

        ShapeCount++;   // an actual itemize+shape pass (this was a cache miss); a re-wrap returns above without bumping it
        if (cacheable) _store!.Store(shapeKey, text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds, features, lineH, this);
        long censusT1 = censusT0 != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        WrapAndPosition(size, scale, lineH, maxWidth, wrap, trim, maxLines, (nint)face, overflowSuffixStart);
        if (censusT0 != 0) CensusLayout(hit: false, cacheable, shapeKey, censusT0, censusT1);
    }

    // A word wraps to the next line only when it overruns the box by MORE than this sub-pixel slack. The box width
    // handed to layout can land a hair UNDER the run's own natural width: ancestor flex chains accumulate fractional
    // rounding, and a shrink-to-fit container sizes to ~the run's natural width but its child box ends a few hundredths
    // of a pixel short. A strict `> maxWidth` test then wraps the trailing word — reserving ONE line's height (the
    // measure pass saw it fit) while the run actually rasterizes TWO, so the following sibling overlaps it (the Expander
    // "body overlaps the action button after collapse→re-expand" defect). One DIP of slack keeps measure ≡ render at
    // that boundary — the run clips at most ~1px on the right, imperceptible — without affecting genuinely-narrower boxes.
    private const float WrapSlack = 1f;

    private void WrapAndPosition(float size, float scale, float lineHeight, float maxWidth, int wrap, int trim, int maxLines, nint face,
        int overflowSuffixStart)
    {
        bool doWrap = wrap != 0 && maxWidth > 1f && !float.IsInfinity(maxWidth);
        // Trimming must survive a COLLAPSED box (0 <= maxWidth <= 1), which wrapping deliberately does not: a grid whose
        // Star track resolves to 0 (FlexLayout.ResolveColumns' overflow guard) hands its cell a 0-width budget, and the
        // old `> 1f` gate silently turned the authored ellipsis OFF — the run then rasterized at its NATURAL width while
        // Width below still reported min(natural, maxWidth) ~= 0, so layout stayed "clean" and the glyphs painted
        // straight across the neighbouring columns (the rail-open column pile-up). EmitLine's budget math already floors
        // at 0, so a collapsed cell degrades to one glyph + the ellipsis instead of the whole string. Wrapping stays
        // gated: breaking at a ~0 budget would emit one glyph per line, which is worse than a single trimmed line.
        bool doTrim = trim != 0 && maxWidth >= 0f && !float.IsInfinity(maxWidth);
        int maxL = maxLines > 0 ? maxLines : int.MaxValue;
        int allGc = _glyphCount;
        int suffixGlyph = allGc;
        bool hasOverflowSuffixData = overflowSuffixStart >= 0 && overflowSuffixStart < _textLen;
        if (hasOverflowSuffixData)
        {
            suffixGlyph = 0;
            while (suffixGlyph < allGc && _glyphs[suffixGlyph].Cluster < overflowSuffixStart) suffixGlyph++;
            if (suffixGlyph >= allGc) hasOverflowSuffixData = false;
        }
        bool canEmitOverflowSuffix = hasOverflowSuffixData && doWrap && maxLines > 0;
        // The appended suffix is never ordinary paragraph content. It is either emitted atomically on finite-line
        // wrapping overflow, or omitted altogether (including NoWrap and unlimited-line layouts).
        int gc = hasOverflowSuffixData ? suffixGlyph : allGc;
        int visibleTextEnd = hasOverflowSuffixData ? overflowSuffixStart : _textLen;
        bool emittedOverflowSuffix = false;

        float maxLineW = 0f;
        int line = 0;
        int lineStart = 0;
        float pen = 0f;
        int lastBreak = -1;

        for (int i = 0; i <= gc; i++)
        {
            bool end = i == gc;
            if (!end)
            {
                int c = _glyphs[i].Cluster;
                // A break opportunity belongs to the cluster's first glyph: a multi-glyph cluster (base + combining
                // mark, Devanagari consonant + matra) must never open a line or set lastBreak mid-cluster.
                bool clusterHead = i == 0 || _glyphs[i - 1].Cluster != c;
                byte bb = clusterHead && c >= 0 && c < _breaks.Count ? _breaks[c].BreakBefore : BreakOpp.Neutral;
                bool mustBreak = bb == BreakOpp.MustBreak;
                bool canBreak = bb == BreakOpp.CanBreak || mustBreak;

                if (mustBreak && i > lineStart)
                {
                    if (line + 1 >= maxL)
                    {
                        // Line budget reached at a hard break: emit this line as-is and DROP the rest — WinUI
                        // MaxLines clips whole lines past the cap; it never runs the next paragraph on.
                        if (canEmitOverflowSuffix)
                        {
                            EmitOverflowSuffixLine(lineStart, gc, suffixGlyph, allGc, overflowSuffixStart,
                                line, lineHeight, maxWidth, ref maxLineW);
                            emittedOverflowSuffix = true;
                        }
                        else EmitLine(lineStart, i, line, lineHeight, doTrim, maxWidth, ref maxLineW);
                        line++; lineStart = gc;
                        break;
                    }
                    EmitLine(lineStart, i, line, lineHeight, doTrim, maxWidth, ref maxLineW); lineStart = i; line++; pen = 0f; lastBreak = -1;
                }
                if (canBreak) lastBreak = i;

                float adv = _glyphs[i].Advance;
                // Trailing whitespace HANGS past the box edge (DirectWrite/WinUI/CSS): a space never triggers the wrap.
                // UAX #14 puts the break opportunity on the letter AFTER the space, so wrapping on the space itself
                // either pushed a word that fits to the next line (br = start of the current word) or, with no earlier
                // break, started the next line with the space (br = i): an indented line, or a line of only spaces.
                // The wrap now fires on the next letter, whose CanBreak keeps the spaces on this line.
                bool hang = c >= 0 && c < _breaks.Count && _breaks[c].IsWhitespace;
                if (doWrap && !hang && pen + adv > maxWidth + WrapSlack && i > lineStart)
                {
                    int br = lastBreak > lineStart ? lastBreak : i;
                    if (line + 1 >= maxL)
                    {
                        // Overflow ON the last allowed line: it still wraps at the break point — the remainder is
                        // dropped, never dumped unwrapped past maxWidth. With trimming, EmitLine ellipsizes the
                        // remainder down to the width budget instead (WinUI CharacterEllipsis + MaxLines).
                        if (canEmitOverflowSuffix)
                        {
                            EmitOverflowSuffixLine(lineStart, gc, suffixGlyph, allGc, overflowSuffixStart,
                                line, lineHeight, maxWidth, ref maxLineW);
                            emittedOverflowSuffix = true;
                        }
                        else EmitLine(lineStart, doTrim ? gc : br, line, lineHeight, doTrim, maxWidth, ref maxLineW);
                        line++; lineStart = gc;
                        break;
                    }
                    EmitLine(lineStart, br, line, lineHeight, false, maxWidth, ref maxLineW);   // wrapped lines fit by construction
                    lineStart = br; line++;
                    pen = 0f; for (int k = br; k < i; k++) pen += _glyphs[k].Advance;
                    lastBreak = -1;
                }
                pen += adv;
            }
            else if (gc > lineStart || line == 0)
            {
                // The final line: it may overflow (no-wrap, or the maxLines-truncated remainder) → trim if requested.
                EmitLine(lineStart, gc, line, lineHeight, doTrim, maxWidth, ref maxLineW); line++;
            }
        }

        // A paragraph that ENDS in a hard break owns an empty last line after the terminator (IDWriteTextLayout reports it
        // too). UAX #14 puts the MustBreak BEFORE the char that follows the terminator, and there is none, so the loop above
        // never opens that line: Enter at the end of a multi-line TextBox left the caret on the old line and the box did not
        // grow. DWrite reports MustBreak AFTER the final char only for a hard break (a plain final char gets CanBreak). The
        // line is skipped when MaxLines already cut the paragraph or the overflow suffix ended it.
        if (!emittedOverflowSuffix && line < maxL && _lineRecCount > 0
            && visibleTextEnd > 0 && visibleTextEnd <= _breaks.Count
            && _breaks[visibleTextEnd - 1].BreakAfter == BreakOpp.MustBreak)
        {
            EnsureLines(_lineRecCount + 1);
            _lines[_lineRecCount++] = new LaidLine
            {
                StartChar = visibleTextEnd, EndChar = visibleTextEnd, VisibleBodyEnd = -1, SuffixStart = -1,
                Top = line * lineHeight, Height = lineHeight, Width = 0f, FirstGlyph = _clusterCount, GlyphCount = 0,
                FirstLaid = _laidCount, LaidCount = 0,
            };
            line++;
        }

        LineCount = Math.Max(1, line);
        Width = float.IsInfinity(maxWidth) ? maxLineW : MathF.Min(maxLineW, maxWidth);
        Height = LineCount * lineHeight;

        // Seal the retained line table: each line ends where the next begins; the last line runs to the text length.
        // (Degenerate shaping that produced zero glyphs still gets one empty line so queries always have a band.)
        if (_lineRecCount == 0)
        {
            EnsureLines(1);
            _lines[_lineRecCount++] = new LaidLine
            {
                StartChar = 0, EndChar = visibleTextEnd, VisibleBodyEnd = -1, SuffixStart = -1,
                Top = 0f, Height = lineHeight, Width = 0f, FirstGlyph = 0, GlyphCount = 0,
                FirstLaid = _laidCount, LaidCount = 0,
            };
        }
        for (int li = 0; li + 1 < _lineRecCount; li++) _lines[li].EndChar = _lines[li + 1].StartChar;
        if (!emittedOverflowSuffix) _lines[_lineRecCount - 1].EndChar = visibleTextEnd;
        ApplyAlignment();
    }

    // ── Alignment (TextAlign) ─────────────────────────────────────────────────────────────────────────────────────
    // Positioning only, applied once every line is laid and sealed: a Center/End line shifts BOTH its laid glyphs and
    // its cluster table by one offset, and Justify widens the line's interior whitespace clusters — so hit-test, caret
    // and range rects (which read the cluster table) agree with the drawn glyphs by construction.
    private TextAlign _align;
    private float _alignWidth = float.PositiveInfinity;

    private void ApplyAlignment()
    {
        if (_align == TextAlign.Start) return;
        float boxW = float.IsFinite(_alignWidth) ? _alignWidth : Width;   // +Inf ⇒ the widest line (measure unchanged)
        for (int li = 0; li < _lineRecCount; li++)
        {
            ref LaidLine line = ref _lines[li];
            int visEnd = VisibleClusterEnd(in line);
            float w = VisibleWidth(in line, visEnd);
            if (_align == TextAlign.Justify)
            {
                // Every line except the last and hard-broken ones; never an ellipsized or overflow-suffix line.
                if (li + 1 < _lineRecCount && !IsHardBrokenLine(li) && line.SuffixStart < 0 && line.LaidCount == line.GlyphCount)
                    JustifyLine(in line, visEnd, boxW - w);
                continue;
            }
            float dx = TextTypography.LineOffset(_align, boxW, w);
            if (dx != 0f) ShiftLine(ref line, dx);
        }
    }

    /// <summary>The exclusive end (cluster-table index) of the line's VISIBLE clusters: trailing whitespace hangs past
    /// the box edge (it never triggered the wrap), so it neither counts toward the aligned width nor receives
    /// justification space; zero-advance terminators/marks at the end are skipped the same way. An ellipsized line ends
    /// in the "…" — nothing after it trails, so the whole line is visible.</summary>
    private int VisibleClusterEnd(in LaidLine line)
    {
        int end = line.FirstGlyph + line.GlyphCount;
        if (line.LaidCount > line.GlyphCount) return end;
        while (end > line.FirstGlyph)
        {
            ref readonly var c = ref _clusters[end - 1];
            bool ws = (uint)c.Cluster < (uint)_breaks.Count && _breaks[c.Cluster].IsWhitespace;
            if (!ws && c.Advance != 0f) break;
            end--;
        }
        return end;
    }

    /// <summary>Visible width of a line in its UNALIGNED frame (lines lay from x = 0; an already-shifted line's offset is
    /// removed via <see cref="LaidLine.X"/>).</summary>
    private float VisibleWidth(in LaidLine line, int visEnd)
    {
        if (line.LaidCount > line.GlyphCount) return line.Width;   // ellipsized: the trimmed width, "…" included
        if (visEnd <= line.FirstGlyph) return 0f;
        ref readonly var last = ref _clusters[visEnd - 1];
        return last.X + last.Advance - line.X;
    }

    /// <summary>The visible width (trailing hanging whitespace excluded) of line <paramref name="li"/> of the CURRENT
    /// layout — the width <c>TextTypography.LineOffset</c> aligns. 0 for an out-of-range line.</summary>
    public float VisibleLineWidth(int li)
    {
        if ((uint)li >= (uint)_lineRecCount) return 0f;
        ref readonly var line = ref _lines[li];
        return VisibleWidth(in line, VisibleClusterEnd(in line));
    }

    /// <summary>Justify-at-use geometry (span artifacts are laid unjustified at measure; consumers justify at the drawn
    /// box): the number of justifiable whitespace clusters <see cref="JustifyLine"/> would widen on line
    /// <paramref name="li"/> — 0 when the line is never justified (last, hard-broken, overflow-suffix, ellipsized).</summary>
    public int JustifyGapCount(int li)
    {
        if ((uint)li >= (uint)_lineRecCount || li + 1 >= _lineRecCount || IsHardBrokenLine(li)) return 0;
        ref readonly var line = ref _lines[li];
        if (line.SuffixStart >= 0 || line.LaidCount != line.GlyphCount) return 0;
        return JustifyGapsBefore(li, int.MaxValue);
    }

    /// <summary>The justifiable gaps left of the caret edge <see cref="CaretXOnLine"/> returns for
    /// <paramref name="charIndex"/> on line <paramref name="li"/> (same scan: the first cluster at-or-after the index,
    /// else the line's trailing edge) — the count of widened gaps a justified layout shifts that edge by.</summary>
    public int JustifyGapsBefore(int li, int charIndex)
    {
        if ((uint)li >= (uint)_lineRecCount) return 0;
        ref readonly var line = ref _lines[li];
        if (line.GlyphCount == 0) return 0;
        int gEnd = line.FirstGlyph + line.GlyphCount, k = gEnd;
        for (int i = line.FirstGlyph; i < gEnd; i++)
            if (_clusters[i].Cluster >= charIndex) { k = i; break; }
        int stop = Math.Min(k, VisibleClusterEnd(in line)), count = 0;
        for (int i = line.FirstGlyph; i < stop; i++)
            if (IsJustifiable(i)) count++;
        return count;
    }

    /// <summary>The line whose band contains <paramref name="top"/> (a range rect's Y is its line's Top); clamps to the
    /// last line.</summary>
    public int LineIndexAtTop(float top)
    {
        for (int i = 0; i < _lineRecCount; i++)
            if (top < _lines[i].Top + _lines[i].Height) return i;
        return _lineRecCount - 1;
    }

    private bool IsHardBrokenLine(int li)
    {
        int term = _lines[li].EndChar - 1;
        return term >= 0 && term < _breaks.Count && _breaks[term].BreakAfter == BreakOpp.MustBreak;
    }

    private void ShiftLine(ref LaidLine line, float dx)
    {
        line.X = dx;
        for (int k = line.FirstGlyph, e = line.FirstGlyph + line.GlyphCount; k < e; k++)
        {
            ref readonly var c = ref _clusters[k];
            _clusters[k] = new LaidCluster(c.Cluster, c.X + dx, c.Advance, c.Line);
        }
        for (int k = line.FirstLaid, e = line.FirstLaid + line.LaidCount; k < e; k++)
        {
            ref readonly var g = ref _laid[k];
            _laid[k] = new LaidGlyph(g.Gid, g.Face, g.X + dx, g.Y, g.Size, g.Span);
        }
    }

    /// <summary>Distribute <paramref name="slack"/> equally over the line's interior whitespace clusters (those before
    /// <paramref name="visEnd"/>): each widens by the share — its advance, so hit-test/caret/range rects see the wider
    /// gap — and every later glyph moves right by the accumulated shift. Laid glyphs are 1:1 with clusters on a
    /// non-ellipsized line, in the same order.</summary>
    private void JustifyLine(in LaidLine line, int visEnd, float slack)
    {
        if (!(slack > 0f) || !float.IsFinite(slack)) return;
        int count = 0;
        for (int k = line.FirstGlyph; k < visEnd; k++)
            if (IsJustifiable(k)) count++;
        if (count == 0) return;
        float per = slack / count, shift = 0f;
        int laidEnd = line.FirstLaid + line.LaidCount;
        for (int k = line.FirstGlyph, e = line.FirstGlyph + line.GlyphCount; k < e; k++)
        {
            ref readonly var c = ref _clusters[k];
            bool grow = k < visEnd && IsJustifiable(k);
            _clusters[k] = new LaidCluster(c.Cluster, c.X + shift, grow ? c.Advance + per : c.Advance, c.Line);
            int gi = line.FirstLaid + (k - line.FirstGlyph);
            if (gi < laidEnd)
            {
                ref readonly var g = ref _laid[gi];
                _laid[gi] = new LaidGlyph(g.Gid, g.Face, g.X + shift, g.Y, g.Size, g.Span);
            }
            if (grow) shift += per;
        }
    }

    private bool IsJustifiable(int k)
    {
        ref readonly var c = ref _clusters[k];
        return c.Advance > 0f && (uint)c.Cluster < (uint)_breaks.Count && _breaks[c.Cluster].IsWhitespace;
    }

    // Emit the final visible body fragment plus an atomic overflow-only suffix. The suffix is shaped as part of the
    // paragraph, but omitted entirely when the body fits. On overflow its advance is reserved before the body is cut.
    private void EmitOverflowSuffixLine(int bodyStart, int bodyEnd, int suffixStartGlyph, int suffixEndGlyph,
        int suffixStartChar, int lineIndex, float lineHeight, float maxWidth, ref float maxLineW)
    {
        float suffixW = 0f;
        for (int i = suffixStartGlyph; i < suffixEndGlyph; i++) suffixW += _glyphs[i].Advance;

        int suffixUseEnd = suffixEndGlyph;
        if (suffixW > maxWidth)
        {
            suffixW = 0f;
            suffixUseEnd = suffixStartGlyph;
            while (suffixUseEnd < suffixEndGlyph
                && suffixW + _glyphs[suffixUseEnd].Advance <= maxWidth + WrapSlack)
                suffixW += _glyphs[suffixUseEnd++].Advance;
        }

        float bodyBudget = MathF.Max(0f, maxWidth - suffixW);
        int bodyUseEnd = FitOverflowBody(bodyStart, bodyEnd, bodyBudget);
        int visibleBodyEndChar = bodyUseEnd < bodyEnd ? _glyphs[bodyUseEnd].Cluster : suffixStartChar;
        int firstGlyph = _clusterCount;
        int firstLaid = _laidCount;
        float x = 0f;
        float baselineY = Baseline + lineIndex * lineHeight;
        AppendLogicalRange(bodyStart, bodyUseEnd, lineIndex, baselineY, ref x);
        AppendLogicalRange(suffixStartGlyph, suffixUseEnd, lineIndex, baselineY, ref x);
        if (x > maxLineW) maxLineW = x;

        int startChar = bodyStart < bodyEnd ? _glyphs[bodyStart].Cluster
            : suffixUseEnd > suffixStartGlyph ? _glyphs[suffixStartGlyph].Cluster
            : suffixStartChar;
        EnsureLines(_lineRecCount + 1);
        _lines[_lineRecCount++] = new LaidLine
        {
            StartChar = startChar,
            EndChar = _textLen,
            VisibleBodyEnd = visibleBodyEndChar,
            SuffixStart = suffixStartChar,
            Top = lineIndex * lineHeight,
            Height = lineHeight,
            Width = x,
            FirstGlyph = firstGlyph,
            GlyphCount = _clusterCount - firstGlyph,
            FirstLaid = firstLaid,
            LaidCount = _laidCount - firstLaid,
        };
    }

    private int FitOverflowBody(int start, int end, float budget)
    {
        if (budget <= 0f || start >= end) return start;
        float used = 0f;
        int lastBreak = -1;
        for (int i = start; i < end; i++)
        {
            int c = _glyphs[i].Cluster;
            bool clusterHead = i == 0 || _glyphs[i - 1].Cluster != c;
            if (clusterHead && c >= 0 && c < _breaks.Count)
            {
                byte before = _breaks[c].BreakBefore;
                if (before == BreakOpp.CanBreak || before == BreakOpp.MustBreak) lastBreak = i;
            }
            float next = used + _glyphs[i].Advance;
            if (next > budget + WrapSlack)
                return lastBreak > start ? lastBreak : i;
            used = next;
        }
        return end;
    }

    private void AppendLogicalRange(int start, int end, int lineIndex, float baselineY, ref float x)
    {
        if (end <= start) return;
        EnsureLaid(_laidCount + end - start);
        EnsureClusters(_clusterCount + end - start);
        for (int i = start; i < end; i++)
        {
            ref readonly var g = ref _glyphs[i];
            _laid[_laidCount++] = new LaidGlyph(g.Gid, g.Face, x + g.OffX, baselineY + g.OffY, g.Size, g.Span);
            _clusters[_clusterCount++] = new LaidCluster(g.Cluster, x, g.Advance, lineIndex);
            x += g.Advance;
        }
    }

    // Position glyphs [start,end) on one line with BiDi L2 reordering; trim+ellipsize if the line overflows; append to _laid.
    private void EmitLine(int start, int end, int lineIndex, float lineHeight, bool doTrim, float maxWidth, ref float maxLineW)
    {
        if (end <= start) return;
        int len = end - start;
        int minCluster = int.MaxValue;
        for (int i = start; i < end; i++) { int c = _glyphs[i].Cluster; if (c < minCluster) minCluster = c; }

        // Trim in LOGICAL order, before the reorder: the cut drops the line's logical END and keeps its logical start, as
        // DirectWrite/WinUI do. Fitting the visual prefix instead kept an RTL run's logical TAIL (L2 puts its last glyph
        // first), so a trimmed Hebrew/Arabic title showed its closing words and lost its opening ones.
        int useLen = len; bool ellipsize = false; int ellSpan = -1;
        if (doTrim)
        {
            float total = 0f; for (int i = start; i < end; i++) total += _glyphs[i].Advance;
            if (total > maxWidth)
            {
                if (_spanEllCount == 0)
                {
                    // Single-style paragraph: the base "…" (unchanged path).
                    float budget = MathF.Max(0f, maxWidth - _ellAdv); float acc = 0f; useLen = 0;
                    for (int k = 0; k < len; k++) { float a = _glyphs[start + k].Advance; if (acc + a > budget && useLen > 0) break; acc += a; useLen++; }
                }
                else
                {
                    // Spanned paragraph: the "…" takes the style of the span the cut lands in (the last kept glyph's),
                    // and each candidate cut reserves THAT ellipsis's advance — the pure fit is LineBreaker's.
                    Span<float> advs = len <= 256 ? stackalloc float[len] : new float[len];
                    Span<short> spanOf = len <= 256 ? stackalloc short[len] : new short[len];
                    for (int k = 0; k < len; k++) { ref readonly var g = ref _glyphs[start + k]; advs[k] = g.Advance; spanOf[k] = g.Span; }
                    useLen = FluentGpu.Text.LineBreaker.FitEllipsisBySpan(advs, spanOf, maxWidth, _ellAdv,
                        _spanEllAdv.AsSpan(0, _spanEllCount), out ellSpan);
                }
                ellipsize = true;
            }
        }

        // Reorder the KEPT glyphs into visual order: reverse maximal sequences of level >= L, for L from highest down to 1 (UAX #9 L2).
        Span<int> order = useLen <= 256 ? stackalloc int[useLen] : new int[useLen];
        for (int i = 0; i < useLen; i++) order[i] = start + i;
        byte maxLevel = 0, minOdd = 255;
        for (int i = start; i < start + useLen; i++)
        {
            byte l = _glyphs[i].Level; if (l > maxLevel) maxLevel = l; if ((l & 1) != 0 && l < minOdd) minOdd = l;
        }
        for (int lvl = maxLevel; lvl >= (minOdd == 255 ? maxLevel + 1 : minOdd); lvl--)
        {
            int i = 0;
            while (i < useLen)
            {
                if (_glyphs[order[i]].Level >= lvl)
                {
                    int j = i; while (j < useLen && _glyphs[order[j]].Level >= lvl) j++;
                    order.Slice(i, j - i).Reverse();
                    i = j;
                }
                else i++;
            }
        }

        float baselineY = Baseline + lineIndex * lineHeight;
        float x = 0f;
        EnsureLaid(_laidCount + useLen + 1);
        EnsureClusters(_clusterCount + useLen);
        int firstGlyph = _clusterCount;
        int firstLaid = _laidCount;
        for (int k = 0; k < useLen; k++)
        {
            ref readonly var g = ref _glyphs[order[k]];
            _laid[_laidCount++] = new LaidGlyph(g.Gid, g.Face, x + g.OffX, baselineY + g.OffY, g.Size, g.Span);
            _clusters[_clusterCount++] = new LaidCluster(g.Cluster, x, g.Advance, lineIndex);
            x += g.Advance;
        }
        if (ellipsize && (uint)ellSpan < (uint)_spanEllCount)
        {
            // Span-styled "…": its face (weight/family), size and span index (→ the renderer tints it with the span
            // colour) — the advance added here is the one FitEllipsisBySpan reserved for this cut.
            if (_spanEllFace[ellSpan] != 0)
            {
                _laid[_laidCount++] = new LaidGlyph(_spanEllGid[ellSpan], _spanEllFace[ellSpan], x, baselineY, _spanEllSize[ellSpan], (short)ellSpan);
                x += _spanEllAdv[ellSpan];
            }
        }
        else if (ellipsize && _ellFace != 0)
        {
            _laid[_laidCount++] = new LaidGlyph(_ellGid, _ellFace, x, baselineY, _ellSize, -1);
            x += _ellAdv;
        }
        if (x > maxLineW) maxLineW = x;

        // Retain the line record. lineIndex == _lineRecCount by construction (every recorded line is emitted in order);
        // EndChar is provisional — WrapAndPosition seals it to the next line's StartChar once that line is known.
        EnsureLines(_lineRecCount + 1);
        _lines[_lineRecCount++] = new LaidLine
        {
            StartChar = minCluster, EndChar = _textLen, VisibleBodyEnd = -1, SuffixStart = -1,
            Top = lineIndex * lineHeight, Height = lineHeight, Width = x,
            FirstGlyph = firstGlyph, GlyphCount = useLen,
            FirstLaid = firstLaid, LaidCount = _laidCount - firstLaid,
        };
    }

    // ── Editor queries (against the CURRENT layout — call Layout first; 0 alloc, retained tables only) ──

    /// <summary>Point → UTF-16 insertion index. The index is SNAPPED to the nearest edge of the hit glyph cluster — its
    /// leading edge when the point falls in the left half, its trailing edge (= the next cluster's start, or the line
    /// end) in the right half — and <paramref name="trailing"/> reports which. Out-of-bounds points clamp: above → first
    /// line, below → last line, left → line start, right → line end; a hard-broken line clamps to the position BEFORE
    /// its terminator cluster, so a click past the text never jumps the caret to the next line. Multi-glyph clusters
    /// (base + marks) are walked as one box; an index is never produced inside a cluster.</summary>
    public int HitTest(Point2 point, out bool trailing)
    {
        trailing = false;
        if (_lineRecCount == 0 || _textLen == 0) return 0;
        int li = _lineRecCount - 1;
        for (int i = 0; i < _lineRecCount; i++)
            if (point.Y < _lines[i].Top + _lines[i].Height) { li = i; break; }
        ref readonly var line = ref _lines[li];
        if (line.GlyphCount == 0) return line.StartChar;
        int g = line.FirstGlyph, gEnd = line.FirstGlyph + line.GlyphCount;
        if (point.X <= _clusters[g].X) return _clusters[g].Cluster;   // left of the line → leading edge of the first cluster
        int k = g;
        while (k < gEnd)
        {
            int c = _clusters[k].Cluster;
            float cx = _clusters[k].X, adv = _clusters[k].Advance;
            int j = k + 1;
            while (j < gEnd && _clusters[j].Cluster == c) { adv += _clusters[j].Advance; j++; }   // one box per cluster
            if (point.X < cx + adv)
            {
                if (point.X < cx + adv * 0.5f) return c;                                  // leading half
                trailing = true;
                return j < gEnd ? _clusters[j].Cluster : LineEndInsertion(li);            // trailing half → after the cluster
            }
            k = j;
        }
        trailing = true;
        return LineEndInsertion(li);   // past the right edge
    }

    /// <summary>Caret geometry for a UTF-16 index. Returns the LEADING position of <paramref name="charIndex"/>: at a
    /// soft-wrap boundary that is the START of the continuation line — trailing affinity (pinning the caret to the END
    /// of the wrapped line) is the caller's job; <paramref name="charIndex"/> == text length → the trailing edge of the
    /// last line. An index inside a multi-char cluster (ligature/surrogate pair) snaps forward to the cluster's
    /// trailing edge. Out-of-range indices clamp.</summary>
    public void CaretAt(int charIndex, out float x, out float lineTop, out float lineHeight, out int lineIndex)
    {
        if (_lineRecCount == 0) { x = 0f; lineTop = 0f; lineHeight = Height; lineIndex = 0; return; }
        if (charIndex < 0) charIndex = 0; else if (charIndex > _textLen) charIndex = _textLen;
        int li = _lineRecCount - 1;
        for (int i = 0; i < _lineRecCount; i++)
            if (charIndex < _lines[i].EndChar) { li = i; break; }     // boundary index belongs to the NEXT line (leading)
        ref readonly var line = ref _lines[li];
        x = CaretXOnLine(li, charIndex);
        lineTop = line.Top; lineHeight = line.Height; lineIndex = li;
    }

    /// <summary>One rect per line fragment covered by [<paramref name="start"/>, <paramref name="end"/>): per line,
    /// [max(start, lineStart), min(end, lineEnd)) → x-from/x-to via cluster edges. Returns the count written
    /// (≤ <paramref name="rects"/>.Length; excess fragments are dropped — callers size generously). Zero-width ranges —
    /// and fragments whose clusters have no advance (e.g. a bare line terminator) — produce nothing.</summary>
    public int RangeRects(int start, int end, Span<RectF> rects)
    {
        if (start < 0) start = 0;
        if (end > _textLen) end = _textLen;
        if (end <= start) return 0;
        int written = 0;
        for (int li = 0; li < _lineRecCount && written < rects.Length; li++)
        {
            ref readonly var line = ref _lines[li];
            if (line.SuffixStart >= 0)
            {
                int bs = start > line.StartChar ? start : line.StartChar;
                int be = end < line.VisibleBodyEnd ? end : line.VisibleBodyEnd;
                if (bs < be)
                {
                    float bx0 = CaretXOnLine(li, bs);
                    float bx1 = CaretXOnLine(li, be);
                    if (bx1 > bx0) rects[written++] = new RectF(bx0, line.Top, bx1 - bx0, line.Height);
                }
                if (written >= rects.Length) continue;
                int ss = start > line.SuffixStart ? start : line.SuffixStart;
                int se = end < line.EndChar ? end : line.EndChar;
                if (ss < se)
                {
                    float sx0 = CaretXOnLine(li, ss);
                    float sx1 = CaretXOnLine(li, se);
                    if (sx1 > sx0) rects[written++] = new RectF(sx0, line.Top, sx1 - sx0, line.Height);
                }
                continue;
            }
            int s = start > line.StartChar ? start : line.StartChar;
            int e = end < line.EndChar ? end : line.EndChar;
            if (s >= e) continue;
            float x0 = CaretXOnLine(li, s);
            float x1 = CaretXOnLine(li, e);
            if (x1 <= x0) continue;
            rects[written++] = new RectF(x0, line.Top, x1 - x0, line.Height);
        }
        return written;
    }

    /// <summary>Caret X for <paramref name="charIndex"/> on line <paramref name="li"/>: the leading edge of the first
    /// cluster at-or-after the index (a mid-cluster index thus snaps to the containing cluster's trailing edge); past
    /// every retained cluster — including a trim-dropped tail — the trailing edge of the line's last cluster.</summary>
    private float CaretXOnLine(int li, int charIndex)
    {
        ref readonly var line = ref _lines[li];
        if (line.GlyphCount == 0) return line.X;   // an empty line's caret sits at its alignment offset
        int gEnd = line.FirstGlyph + line.GlyphCount;
        for (int i = line.FirstGlyph; i < gEnd; i++)
            if (_clusters[i].Cluster >= charIndex) return _clusters[i].X;
        ref readonly var last = ref _clusters[gEnd - 1];
        return last.X + last.Advance;
    }

    /// <summary>The insertion index for a hit past a line's right edge. Normally the line's EndChar (== the next line's
    /// start at a soft wrap — affinity is the caller's job); a HARD-broken line (UAX #14 MustBreak after its terminator,
    /// including the empty line after a trailing hard break) returns the terminator cluster's start instead, keeping the
    /// caret on the clicked line. (TextEditCore feeds single-char '\r' hard breaks, so the terminator is one cluster.)</summary>
    private int LineEndInsertion(int li)
    {
        ref readonly var line = ref _lines[li];
        if (li + 1 < _lineRecCount && line.GlyphCount > 0)
        {
            // The char before the next line's start is this line's terminator; MustBreak AFTER it also covers the empty
            // line that follows a trailing hard break (whose start == text length has no BreakBefore entry).
            int term = _lines[li + 1].StartChar - 1;
            if (term >= 0 && term < _breaks.Count && _breaks[term].BreakAfter == BreakOpp.MustBreak)
                return _clusters[line.FirstGlyph + line.GlyphCount - 1].Cluster;
        }
        return line.EndChar;
    }

    /// <summary>The exclusive end of the homogeneous span segment starting at <paramref name="pos"/> (clamped to
    /// <paramref name="limit"/>): inside a span → that span's end; in a gap → the next span's start (base style).
    /// Spans are reconciler-built sorted + non-overlapping; a linear scan is fine at span counts (≤ tens).</summary>
    private static int SpanSegmentEnd(ReadOnlySpan<SpanStyle> spans, int pos, int limit, out int spanIdx)
    {
        spanIdx = -1;
        int segEnd = limit;
        for (int i = 0; i < spans.Length; i++)
        {
            int s = spans[i].Start, e = spans[i].End;
            if (pos >= s && pos < e) { spanIdx = i; return Math.Min(segEnd, e); }
            if (s > pos && s < segEnd) segEnd = s;
        }
        return segEnd;
    }

    /// <summary>Fill the per-span ellipsis table (<c>_spanEll*</c>, indexed by span) for a spanned paragraph: each span's
    /// "…" glyph id + advance in its own resolved face at its own size. Call after the base ellipsis is known (a span
    /// whose face is missing or lacks the glyph takes the base one). Grow-only buffers — 0 alloc in steady state.</summary>
    private void ResolveSpanEllipses(ReadOnlySpan<SpanStyle> spans, string family, int weight, float size, StringTable? names)
    {
        int count = spans.Length;
        if (_spanEllGid.Length < count)
        {
            int cap = Math.Max(count, _spanEllGid.Length * 2);
            Array.Resize(ref _spanEllGid, cap); Array.Resize(ref _spanEllFace, cap);
            Array.Resize(ref _spanEllAdv, cap); Array.Resize(ref _spanEllSize, cap);
        }
        for (int si = 0; si < count; si++)
        {
            ref readonly var sp = ref spans[si];
            string fam = family;
            if (!sp.FontFamily.IsEmpty && names is not null) { var f2 = names.Resolve(sp.FontFamily); if (f2.Length > 0) fam = f2; }
            int w = sp.Weight != 0 ? sp.Weight : weight;
            float sz = sp.SizeDip > 0f ? sp.SizeDip : size;
            var f = ResolveFace(fam, w, out FaceMetrics fm);
            ushort eg = 0;
            if (f != null) { uint ec = '…'; f->GetGlyphIndices(&ec, 1, &eg); }
            if (eg != 0)
            {
                DWRITE_GLYPH_METRICS egm; f->GetDesignGlyphMetrics(&eg, 1, &egm, BOOL.FALSE);
                float sc = fm.Em > 0 ? sz / fm.Em : sz / 2048f;
                _spanEllGid[si] = eg; _spanEllFace[si] = (nint)f; _spanEllAdv[si] = (float)egm.advanceWidth * sc; _spanEllSize[si] = sz;
            }
            else
            {
                _spanEllGid[si] = _ellGid; _spanEllFace[si] = _ellFace; _spanEllAdv[si] = _ellAdv; _spanEllSize[si] = _ellSize;
            }
        }
        _spanEllCount = count;
    }

    private void EnsureGlyphs(int n) { if (_glyphs.Length < n) Array.Resize(ref _glyphs, Math.Max(n, _glyphs.Length * 2)); }
    private void EnsureLaid(int n) { if (_laid.Length < n) Array.Resize(ref _laid, Math.Max(n, _laid.Length * 2)); }
    private void EnsureClusters(int n) { if (_clusters.Length < n) Array.Resize(ref _clusters, Math.Max(n, _clusters.Length * 2)); }
    private void EnsureLines(int n) { if (_lines.Length < n) Array.Resize(ref _lines, Math.Max(n, _lines.Length * 2)); }

    // Pick the face covering [pos, pos+subLen) via system fallback; subLen is the coverage-run length DWrite reports.
    private void ResolveRunFace(string family, int weight, IDWriteFontFace* baseFace, int pos, int remaining, out IDWriteFontFace* subFace, out int subLen)
    {
        subFace = baseFace; subLen = remaining;
        if (baseFace == null) return;

        // Prefer the base face for any prefix it can already render. The system fallback (MapCharacters) remaps even a
        // FULLY-COVERED Latin run to a different "Segoe UI" instance (Win11's heavier Segoe UI Variable) — same glyph ids,
        // but a wider/heavier face, so shaped text rendered bolder than the per-char path (which uses GetFirstMatchingFont).
        // Only invoke the fallback for characters the base face genuinely lacks (CJK / emoji / symbols → .notdef).
        char* txt = _fsrc->Text;
        if (txt != null)
        {
            uint cp0 = txt[pos]; ushort gi0; baseFace->GetGlyphIndices(&cp0, 1, &gi0);
            if (gi0 != 0)
            {
                int len = 1;
                while (len < remaining)
                {
                    // A variation selector belongs to the char before it. The base face maps none (Segoe UI has no
                    // FE00–FE0F), but shaped WITH its base the shaper folds it into the base cluster (♥️ → the heart,
                    // 1️⃣ → Segoe UI's keycap ligature). Breaking here handed the bare selector to MapCharacters, which
                    // maps it to no font, so it shaped alone in the base face as a drawn .notdef box after ©️ ™️ ♥️ ‼️.
                    int vs = VariationSelectorLength(txt, pos + len, pos + remaining);
                    if (vs > 0) { len += vs; continue; }
                    uint cp = txt[pos + len]; ushort gi; baseFace->GetGlyphIndices(&cp, 1, &gi);
                    if (gi == 0) break;
                    len++;
                }
                subLen = len; return;   // base face covers [pos, pos+len) — the same face the per-char path uses
            }
        }

        if (_fallback == null || _sysColl == null) return;
        // A custom font FILE ("Assets/Fonts/Inter.ttf#Inter") names no system family, but its uncovered characters still
        // need a system face: returning here shaped CJK / emoji / other scripts in the file face as drawn .notdef boxes.
        // MapCharacters only takes ONE family as a preference, so the registry hands it the first SYSTEM entry of the
        // style's chain that exists (the body primary when the chain names none); the covered-prefix scan above already
        // kept every character the file can render in the file face.
        string fam = _registry.PreferredSystemFamily(family);
        uint mappedLen; IDWriteFont* mappedFont = null; float sc;
        int hr;
        fixed (char* fn = fam)
            hr = (int)_fallback->MapCharacters((IDWriteTextAnalysisSource*)_fsrc, (uint)pos, (uint)remaining, _sysColl, fn,
                (DWRITE_FONT_WEIGHT)weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, &mappedLen, &mappedFont, &sc);
        if (hr < 0 || mappedLen == 0) { if (mappedFont != null) mappedFont->Release(); return; }
        subLen = (int)mappedLen;
        if (mappedFont != null)
        {
            string famName = GetFamilyName(mappedFont);
            if (!string.IsNullOrEmpty(famName)) subFace = ResolveFallbackFace(famName, mappedFont, baseFace);
            mappedFont->Release();
        }
    }

    // UTF-16 length of the variation selector at t[i] — VS1–VS16 (U+FE00–FE0F, one unit) or VS17–VS256 (U+E0100–E01EF,
    // the pair DB40 DD00–DDEF) — or 0 when t[i] starts none. `end` bounds the low-surrogate read.
    private static int VariationSelectorLength(char* t, int i, int end)
    {
        char c = t[i];
        if (c >= '︀' && c <= '️') return 1;
        if (c == '\uDB40' && i + 1 < end && t[i + 1] >= '\uDD00' && t[i + 1] <= '\uDDEF') return 2;
        return 0;
    }

    private string GetFamilyName(IDWriteFont* font)
    {
        IDWriteFontFamily* fam; if ((int)font->GetFontFamily(&fam) < 0) return "";
        IDWriteLocalizedStrings* names; if ((int)fam->GetFamilyNames(&names) < 0) { fam->Release(); return ""; }
        uint len; names->GetStringLength(0, &len);
        var buf = new char[len + 1];
        fixed (char* b = buf) names->GetString(0, b, len + 1);
        names->Release(); fam->Release();
        return new string(buf, 0, (int)len);
    }

    // The system font-fallback map's faces live in the SHARED registry (one cache with the glyph renderer), keyed by
    // (family, the matched face's REAL weight): a CJK family (Malgun Gothic) has separate Regular/Bold faces, so caching by
    // family alone would pin every weight to whichever resolved first, and 900 and 800 both map to Bold 700.
    private IDWriteFontFace* ResolveFallbackFace(string family, IDWriteFont* font, IDWriteFontFace* baseFace)
    {
        int w = (int)font->GetWeight();
        nint cached = _registry.TryGetFallbackFace(family, w);
        if (cached != 0) return (IDWriteFontFace*)cached;
        IDWriteFontFace* face;
        if ((int)font->CreateFontFace(&face) < 0) return baseFace;
        return (IDWriteFontFace*)_registry.AddFallbackFace(family, w, (nint)face, static f => ((IDWriteFontFace*)f)->Release());
    }

    // Every face - chain entry, #Family file selection, creation, caching, the off-chain error - is the registry's; this
    // engine only converts the registry's metrics to its own layout of them.
    private IDWriteFontFace* ResolveFace(string family, int weight, out FaceMetrics m)
    {
        nint face = _registry.ResolveFace(family, weight, out FontFaceMetrics rm);
        m = new FaceMetrics { Em = rm.Em, Asc = rm.Asc, Desc = rm.Desc, UnderPos = rm.UnderPos, UnderThk = rm.UnderThk, StrikePos = rm.StrikePos, Cap = rm.Cap };
        return (IDWriteFontFace*)face;
    }

    public static void SelfTest()
    {
        using var e = new TextLayoutEngine();
        void T(string s, float maxW)
        {
            e.Layout(s.AsSpan(), "Segoe UI", 400, 14f, maxW, 1, 0, 0);
            Console.WriteLine($"[layout] \"{s}\" maxW={(float.IsInfinity(maxW) ? "inf" : maxW.ToString("0"))} -> {e.LineCount} lines, W={e.Width:0.0} H={e.Height:0.0} glyphs={e.Glyphs.Length}");
        }
        T("Hello world", float.PositiveInfinity);
        T("Hidden content, revealed when the Expander is expanded.", 200f);
        T("Hidden content, revealed when the Expander is expanded.", float.PositiveInfinity);
        T("AVAWAV", float.PositiveInfinity);   // kerned advances

        // WrapSlack guard: a run whose natural width sits a hair OVER its box (sub-pixel ancestor-layout rounding) must
        // NOT wrap a whole word — otherwise the reserved height (1 line) disagrees with what rasterizes (2 lines) and the
        // following sibling overlaps it (the Expander body↔action-button defect after collapse→re-expand). See WrapSlack.
        const string slackProbe = "Hidden content, revealed when the Expander is expanded.";
        e.Layout(slackProbe.AsSpan(), "Segoe UI", 400, 14f, float.PositiveInfinity, 1, 0, 0);
        float slackNatural = e.Width;
        e.Layout(slackProbe.AsSpan(), "Segoe UI", 400, 14f, slackNatural - 0.5f, 1, 0, 0);
        Console.WriteLine($"[wrapslack] natural={slackNatural:0.00}, @ natural-0.5 -> {e.LineCount} line(s)  {(e.LineCount == 1 ? "PASS" : "FAIL (reserves 1 line, rasterizes 2)")}");

        // Fallback: Latin + CJK + emoji should resolve to multiple covering faces with non-.notdef gids.
        e.Layout("Hi 你好 😀".AsSpan(), "Segoe UI", 400, 14f, float.PositiveInfinity, 1, 0, 0);
        var faces = new HashSet<nint>(); int zero = 0;
        foreach (var g in e.Glyphs) { faces.Add(g.Face); if (g.Gid == 0) zero++; }
        Console.WriteLine($"[fallback] \"Hi 你好 😀\" -> {e.Glyphs.Length} glyphs across {faces.Count} face(s), {zero} .notdef");

        // Shape cache: a width-only change must RE-WRAP, not re-shape. On a FRESH engine (clean ShapeCount), shape the
        // paragraph once at one width, then re-measure at several widths — each must match a FRESH (uncached) engine's
        // box EXACTLY, while ShapeCount stays at 1 (shaped once, re-wrapped the rest). The resize-perf regression guard.
        {
            using var e2 = new TextLayoutEngine(null, new ShapeStore(64));   // its own store: the shared one may hold the paragraph already
            using var fresh = new TextLayoutEngine(null, store: null);       // no store: a real shape every time
            const string para = "Hidden content, revealed when the Expander is expanded.";
            e2.Layout(para.AsSpan(), "Segoe UI", 400, 14f, 320f, 1, 0, 0);   // first measure → 1 shape (cache miss)
            bool identical = true;
            foreach (float w in new[] { 220f, 160f, 320f, 480f, 90f })
            {
                e2.Layout(para.AsSpan(), "Segoe UI", 400, 14f, w, 1, 0, 0);        // re-wrap (cache hit)
                fresh.Layout(para.AsSpan(), "Segoe UI", 400, 14f, w, 1, 0, 0);    // fresh shape + wrap
                if (e2.LineCount != fresh.LineCount || MathF.Abs(e2.Width - fresh.Width) > 0.01f || MathF.Abs(e2.Height - fresh.Height) > 0.01f)
                { identical = false; Console.WriteLine($"  [shapecache] MISMATCH @ {w:0}: cached {e2.LineCount}L {e2.Width:0.0}x{e2.Height:0.0} vs fresh {fresh.LineCount}L {fresh.Width:0.0}x{fresh.Height:0.0}"); }
            }
            bool noReshape = e2.ShapeCount == 1;
            Console.WriteLine($"[shapecache] re-wrap identity {(identical ? "PASS" : "FAIL")}; no-re-shape {(noReshape ? "PASS" : "FAIL")} (ShapeCount={e2.ShapeCount}, expected 1)");
        }
    }

    private static void Check(HRESULT hr, string what) { if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}"); }

    /// <summary>The union of the current layout's glyph black boxes (each glyph's design metrics at its own face and size,
    /// placed at its laid origin: <see cref="LaidGlyph.Y"/> is its line's baseline), in the layout's top-down DIP frame. This
    /// is the run's INK: the caps sit well below the line top (the ascent includes the face's internal leading) and a run
    /// without descenders draws nothing under its baseline. False when no laid glyph has ink (empty text, only spaces).
    /// Cold path (one design-metrics call per glyph): inspection and lint, never per frame.</summary>
    public bool TryInkBounds(out RectF ink)
    {
        float left = float.PositiveInfinity, top = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.NegativeInfinity;
        nint lastFace = 0;
        float em = 0f;
        foreach (LaidGlyph g in Glyphs)
        {
            if (g.Face == 0) continue;
            var face = (IDWriteFontFace*)g.Face;
            if (g.Face != lastFace)
            {
                DWRITE_FONT_METRICS fm;
                face->GetMetrics(&fm);
                em = fm.designUnitsPerEm;
                lastFace = g.Face;
            }
            if (em <= 0f) continue;
            ushort gid = g.Gid;
            DWRITE_GLYPH_METRICS gm;
            if ((int)face->GetDesignGlyphMetrics(&gid, 1, &gm, BOOL.FALSE) < 0) continue;
            float s = g.Size / em;
            // Design space is y-up from the baseline: the black box top is verticalOriginY - topSideBearing above it, its bottom
            // advanceHeight - bottomSideBearing - verticalOriginY below it (dwrite.h DWRITE_GLYPH_METRICS).
            float above = (gm.verticalOriginY - gm.topSideBearing) * s;
            float below = ((float)gm.advanceHeight - gm.bottomSideBearing - gm.verticalOriginY) * s;
            float l = gm.leftSideBearing * s, r = ((float)gm.advanceWidth - gm.rightSideBearing) * s;
            if (r - l <= 0f || above + below <= 0f) continue;   // a space or an empty glyph draws no ink
            left = MathF.Min(left, g.X + l);
            right = MathF.Max(right, g.X + r);
            top = MathF.Min(top, g.Y - above);
            bottom = MathF.Max(bottom, g.Y + below);
        }
        if (!(right > left && bottom > top)) { ink = default; return false; }
        ink = new RectF(left, top, right - left, bottom - top);
        return true;
    }

    public void Dispose()
    {
        // Faces belong to the shared FontRegistry (one cache for every consumer); an engine never releases them.
        if (_fsrc != null) { TextAnalysisSourceCcw.Destroy(_fsrc); _fsrc = null; }
        if (_fallback != null) { _fallback->Release(); _fallback = null; }
        if (_sysColl != null) { _sysColl->Release(); _sysColl = null; }
        _shaper.Dispose(); _itemizer.Dispose();
        if (_dw != null) { _dw->Release(); _dw = null; }
    }
}
