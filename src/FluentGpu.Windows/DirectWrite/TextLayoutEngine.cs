using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Text.DirectWrite;

/// <summary>One laid-out glyph: post-shaping glyph id, the DWrite face it belongs to (as <c>nint</c>), and its pen
/// position in DIP — X within the paragraph (line-relative origin folded in), Y = its line baseline from the top.
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
    public float Top, Height, Width;
    public int FirstGlyph, GlyphCount;
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
public sealed unsafe class TextLayoutEngine : IDisposable
{
    private const string DefaultFamily = "Segoe UI";
    private IDWriteFactory* _dw;
    private readonly DWriteItemizer _itemizer;
    private readonly DWriteTextShaper _shaper;
    private readonly Dictionary<(string fam, int weight), nint> _faces = new();
    private readonly Dictionary<(string fam, int weight), FaceMetrics> _metrics = new();

    /// <summary>Cached per-face design metrics (design units; positive-up from the baseline as DWrite reports them).
    /// <c>Cap</c> is the OS/2 cap height (TextLineBounds.Tight trims the line box to cap..baseline); fonts that report
    /// none get the ~0.7 em convention, matching the headless model (HeadlessFontSystem.cs:13).</summary>
    private struct FaceMetrics { public ushort Em; public short Asc, Desc, UnderPos, StrikePos; public ushort UnderThk; public ushort Cap; }
    // Font fallback (Phase 6): split a run by glyph coverage so CJK/emoji/symbols resolve to a covering face.
    private IDWriteFontFallback* _fallback;
    private IDWriteFontCollection* _sysColl;
    private TextAnalysisSourceCcw* _fsrc;
    private readonly Dictionary<string, nint> _fallbackFaces = new();

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

    private struct RunGlyph { public ushort Gid; public nint Face; public float Advance; public int Cluster; public byte Level; public float Size; public short Span; }

    // ── Shape cache ─────────────────────────────────────────────────────────────────────────────────
    // Shaping (itemize → shape) is width-INDEPENDENT — only WrapAndPosition consumes maxWidth. A drag-resize
    // re-measures the SAME text at a new width every frame; without this each call re-runs the COM-heavy itemize +
    // per-glyph fallback + shape phase. Cache the shaped glyphs + break opportunities + line metrics, keyed on a
    // verified 64-bit hash of the SHAPE inputs (content + family + weight + size + spacing + lineHeight + stacking +
    // lineBounds); a (text,style) match restores them and replays ONLY WrapAndPosition at the new width. Plain
    // (no-span) text only — spanned inline runs (rtb-01) re-shape (never a resize hot path). Bounded LRU, instance
    // reused on eviction. Single-thread-confined: the measure engine (UI thread) and the render engine (render thread)
    // are SEPARATE instances (DirectWriteFontSystem.cs:13), so each has its own cache — no cross-thread sharing.
    private sealed class ShapeEntry
    {
        public char[] Text = Array.Empty<char>(); public int TextLen;
        public string Family = ""; public int Weight; public float Size, CharSpacing, LineHeightArg; public int Stacking, LineBounds;
        public RunGlyph[] Glyphs = new RunGlyph[16]; public int GlyphCount;
        public BreakOpp[] Breaks = new BreakOpp[16]; public int BreakCount;
        public float Baseline, UnderlineY, UnderlineThickness, StrikeY, LineH;
        public ushort EllGid; public float EllAdv; public nint EllFace; public float EllSize;
        public long Tick;
    }
    private readonly Dictionary<long, ShapeEntry> _shapeCache = new();
    private long _shapeTick;
    private const int ShapeCacheCap = 256;

    /// <summary>Diagnostics/regression counter: the number of ACTUAL itemize+shape passes. A width-only re-wrap of
    /// already-shaped text does NOT bump it, so a resize that re-wraps cached text leaves this flat.</summary>
    public long ShapeCount { get; private set; }

    private static long ShapeHash(ReadOnlySpan<char> text, string? family, int weight, float size, float charSpacing, float lineHeight, int stacking, int lineBounds)
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
        return (long)h;
    }

    private static bool NanSafeEq(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || a == b;

    private static bool ShapeMatches(ShapeEntry e, ReadOnlySpan<char> text, string? family, int weight, float size, float charSpacing, float lineHeight, int stacking, int lineBounds)
    {
        if (e.TextLen != text.Length || e.Weight != weight || e.Size != size || e.CharSpacing != charSpacing
            || e.Stacking != stacking || e.LineBounds != lineBounds || !NanSafeEq(e.LineHeightArg, lineHeight)
            || !string.Equals(e.Family, family ?? "", StringComparison.Ordinal))
            return false;
        for (int i = 0; i < text.Length; i++) if (e.Text[i] != text[i]) return false;
        return true;
    }

    // Restore a cached shape into the engine's working buffers so WrapAndPosition runs unchanged. Zero-alloc steady
    // state (copies into the existing _glyphs buffer + the _breaks list at its retained capacity).
    private void RestoreShape(ShapeEntry e)
    {
        EnsureGlyphs(e.GlyphCount);
        Array.Copy(e.Glyphs, _glyphs, e.GlyphCount); _glyphCount = e.GlyphCount;
        _breaks.Clear();
        if (_breaks.Capacity < e.BreakCount) _breaks.Capacity = e.BreakCount;
        for (int i = 0; i < e.BreakCount; i++) _breaks.Add(e.Breaks[i]);
        Baseline = e.Baseline; UnderlineY = e.UnderlineY; UnderlineThickness = e.UnderlineThickness; StrikeY = e.StrikeY;
        _textLen = e.TextLen; _ellGid = e.EllGid; _ellAdv = e.EllAdv; _ellFace = e.EllFace; _ellSize = e.EllSize;
    }

    private void StoreShape(long key, ReadOnlySpan<char> text, string? family, int weight, float size, float charSpacing, float lineHeight, int stacking, int lineBounds, float lineH)
    {
        if (!_shapeCache.TryGetValue(key, out var e))   // miss (new key) or collision-overwrite (key present but content differed)
        {
            e = _shapeCache.Count >= ShapeCacheCap ? EvictLru() : new ShapeEntry();
            _shapeCache[key] = e;
        }
        if (e.Text.Length < text.Length) e.Text = new char[Math.Max(text.Length, e.Text.Length * 2)];
        text.CopyTo(e.Text);
        e.TextLen = text.Length; e.Family = family ?? ""; e.Weight = weight; e.Size = size;
        e.CharSpacing = charSpacing; e.LineHeightArg = lineHeight; e.Stacking = stacking; e.LineBounds = lineBounds;
        if (e.Glyphs.Length < _glyphCount) e.Glyphs = new RunGlyph[Math.Max(_glyphCount, e.Glyphs.Length * 2)];
        Array.Copy(_glyphs, e.Glyphs, _glyphCount); e.GlyphCount = _glyphCount;
        int bc = _breaks.Count;
        if (e.Breaks.Length < bc) e.Breaks = new BreakOpp[Math.Max(bc, e.Breaks.Length * 2)];
        for (int i = 0; i < bc; i++) e.Breaks[i] = _breaks[i];
        e.BreakCount = bc;
        e.Baseline = Baseline; e.UnderlineY = UnderlineY; e.UnderlineThickness = UnderlineThickness; e.StrikeY = StrikeY;
        e.LineH = lineH; e.EllGid = _ellGid; e.EllAdv = _ellAdv; e.EllFace = _ellFace; e.EllSize = _ellSize;
        e.Tick = ++_shapeTick;
    }

    private ShapeEntry EvictLru()
    {
        long oldestKey = 0, oldestTick = long.MaxValue; ShapeEntry? oldest = null;
        foreach (var kv in _shapeCache) if (kv.Value.Tick < oldestTick) { oldestTick = kv.Value.Tick; oldestKey = kv.Key; oldest = kv.Value; }
        if (oldest != null) _shapeCache.Remove(oldestKey);
        return oldest ?? new ShapeEntry();
    }

    public TextLayoutEngine()
    {
        IDWriteFactory* f;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
        _dw = f;
        _itemizer = new DWriteItemizer();
        _shaper = new DWriteTextShaper(_dw);
        ResolveFace(DefaultFamily, 400, out _);

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
    /// lines is future work; uniform-size spans, the overwhelming case, are exact).</para></summary>
    public void Layout(ReadOnlySpan<char> text, string family, int weight, float size, float maxWidth, int wrap, int trim, int maxLines,
        float charSpacing = 0f, float lineHeight = float.NaN, int stacking = 0, int lineBounds = 0,
        ReadOnlySpan<SpanStyle> spans = default, StringTable? names = null)
    {
        if (weight <= 0) weight = 400; else if (weight > 999) weight = 999;   // DWRITE_FONT_WEIGHT range
        _glyphCount = 0; _laidCount = 0; _clusterCount = 0; _lineRecCount = 0;

        // Shape cache: a width-only change replays only WrapAndPosition (shaping is width-independent). Plain text only
        // (spanned inline runs re-shape — never the resize hot path). On a hit, restore the shaped state and re-wrap.
        bool cacheable = spans.IsEmpty && text.Length > 0;
        long shapeKey = 0;
        if (cacheable)
        {
            shapeKey = ShapeHash(text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds);
            if (_shapeCache.TryGetValue(shapeKey, out var hit)
                && ShapeMatches(hit, text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds))
            {
                RestoreShape(hit);
                hit.Tick = ++_shapeTick;
                WrapAndPosition(size, 1f, hit.LineH, maxWidth, wrap, trim, maxLines, _ellFace);
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
            _lines[_lineRecCount++] = new LaidLine { StartChar = 0, EndChar = 0, Top = 0f, Height = lineH, Width = 0f, FirstGlyph = 0, GlyphCount = 0 };
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
                        var shaped = _shaper.Shape(p, pos, subLen, subFace, segSize, run.ScriptId, run.ScriptShapes, run.IsRightToLeft);
                        EnsureGlyphs(_glyphCount + shaped.Length);
                        foreach (var g in shaped)
                            _glyphs[_glyphCount++] = new RunGlyph { Gid = g.GlyphId, Face = (nint)subFace, Advance = g.Advance + segSpacing, Cluster = g.Cluster, Level = run.BidiLevel, Size = segSize, Span = segSpan };
                        pos += subLen; remaining -= subLen;
                    }
                }
            }
        }

        ShapeCount++;   // an actual itemize+shape pass (this was a cache miss); a re-wrap returns above without bumping it
        if (cacheable) StoreShape(shapeKey, text, family, weight, size, charSpacing, lineHeight, stacking, lineBounds, lineH);
        WrapAndPosition(size, scale, lineH, maxWidth, wrap, trim, maxLines, (nint)face);
    }

    // A word wraps to the next line only when it overruns the box by MORE than this sub-pixel slack. The box width
    // handed to layout can land a hair UNDER the run's own natural width: ancestor flex chains accumulate fractional
    // rounding, and a shrink-to-fit container sizes to ~the run's natural width but its child box ends a few hundredths
    // of a pixel short. A strict `> maxWidth` test then wraps the trailing word — reserving ONE line's height (the
    // measure pass saw it fit) while the run actually rasterizes TWO, so the following sibling overlaps it (the Expander
    // "body overlaps the action button after collapse→re-expand" defect). One DIP of slack keeps measure ≡ render at
    // that boundary — the run clips at most ~1px on the right, imperceptible — without affecting genuinely-narrower boxes.
    private const float WrapSlack = 1f;

    private void WrapAndPosition(float size, float scale, float lineHeight, float maxWidth, int wrap, int trim, int maxLines, nint face)
    {
        bool doWrap = wrap != 0 && maxWidth > 1f && !float.IsInfinity(maxWidth);
        bool doTrim = trim != 0 && maxWidth > 1f && !float.IsInfinity(maxWidth);
        int maxL = maxLines > 0 ? maxLines : int.MaxValue;
        int gc = _glyphCount;

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
                byte bb = c >= 0 && c < _breaks.Count ? _breaks[c].BreakBefore : BreakOpp.Neutral;
                bool mustBreak = bb == BreakOpp.MustBreak;
                bool canBreak = bb == BreakOpp.CanBreak || mustBreak;

                if (mustBreak && i > lineStart)
                {
                    if (line + 1 >= maxL)
                    {
                        // Line budget reached at a hard break: emit this line as-is and DROP the rest — WinUI
                        // MaxLines clips whole lines past the cap; it never runs the next paragraph on.
                        EmitLine(lineStart, i, line, lineHeight, doTrim, maxWidth, ref maxLineW);
                        line++; lineStart = gc;
                        break;
                    }
                    EmitLine(lineStart, i, line, lineHeight, doTrim, maxWidth, ref maxLineW); lineStart = i; line++; pen = 0f; lastBreak = -1;
                }
                if (canBreak) lastBreak = i;

                float adv = _glyphs[i].Advance;
                if (doWrap && pen + adv > maxWidth + WrapSlack && i > lineStart)
                {
                    int br = lastBreak > lineStart ? lastBreak : i;
                    if (line + 1 >= maxL)
                    {
                        // Overflow ON the last allowed line: it still wraps at the break point — the remainder is
                        // dropped, never dumped unwrapped past maxWidth. With trimming, EmitLine ellipsizes the
                        // remainder down to the width budget instead (WinUI CharacterEllipsis + MaxLines).
                        EmitLine(lineStart, doTrim ? gc : br, line, lineHeight, doTrim, maxWidth, ref maxLineW);
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

        LineCount = Math.Max(1, line);
        Width = float.IsInfinity(maxWidth) ? maxLineW : MathF.Min(maxLineW, maxWidth);
        Height = LineCount * lineHeight;

        // Seal the retained line table: each line ends where the next begins; the last line runs to the text length.
        // (Degenerate shaping that produced zero glyphs still gets one empty line so queries always have a band.)
        if (_lineRecCount == 0)
        {
            EnsureLines(1);
            _lines[_lineRecCount++] = new LaidLine { StartChar = 0, EndChar = _textLen, Top = 0f, Height = lineHeight, Width = 0f, FirstGlyph = 0, GlyphCount = 0 };
        }
        for (int li = 0; li + 1 < _lineRecCount; li++) _lines[li].EndChar = _lines[li + 1].StartChar;
    }

    // Position glyphs [start,end) on one line with BiDi L2 reordering; trim+ellipsize if the line overflows; append to _laid.
    private void EmitLine(int start, int end, int lineIndex, float lineHeight, bool doTrim, float maxWidth, ref float maxLineW)
    {
        if (end <= start) return;
        // Reorder visual order: reverse maximal sequences of level >= L, for L from highest down to 1 (UAX #9 L2).
        int len = end - start;
        Span<int> order = len <= 256 ? stackalloc int[len] : new int[len];
        for (int i = 0; i < len; i++) order[i] = start + i;
        byte maxLevel = 0, minOdd = 255; int minCluster = int.MaxValue;
        for (int i = start; i < end; i++)
        {
            byte l = _glyphs[i].Level; if (l > maxLevel) maxLevel = l; if ((l & 1) != 0 && l < minOdd) minOdd = l;
            int c = _glyphs[i].Cluster; if (c < minCluster) minCluster = c;
        }
        for (int lvl = maxLevel; lvl >= (minOdd == 255 ? maxLevel + 1 : minOdd); lvl--)
        {
            int i = 0;
            while (i < len)
            {
                if (_glyphs[order[i]].Level >= lvl)
                {
                    int j = i; while (j < len && _glyphs[order[j]].Level >= lvl) j++;
                    order.Slice(i, j - i).Reverse();
                    i = j;
                }
                else i++;
            }
        }

        float baselineY = Baseline + lineIndex * lineHeight;
        int useLen = len; bool ellipsize = false;
        if (doTrim)
        {
            float total = 0f; for (int k = 0; k < len; k++) total += _glyphs[order[k]].Advance;
            if (total > maxWidth)
            {
                float budget = MathF.Max(0f, maxWidth - _ellAdv); float acc = 0f; useLen = 0;
                for (int k = 0; k < len; k++) { float a = _glyphs[order[k]].Advance; if (acc + a > budget && useLen > 0) break; acc += a; useLen++; }
                ellipsize = true;
            }
        }
        float x = 0f;
        EnsureLaid(_laidCount + useLen + 1);
        EnsureClusters(_clusterCount + useLen);
        int firstGlyph = _clusterCount;
        for (int k = 0; k < useLen; k++)
        {
            ref readonly var g = ref _glyphs[order[k]];
            _laid[_laidCount++] = new LaidGlyph(g.Gid, g.Face, x, baselineY, g.Size, g.Span);
            _clusters[_clusterCount++] = new LaidCluster(g.Cluster, x, g.Advance, lineIndex);
            x += g.Advance;
        }
        if (ellipsize && _ellFace != 0)
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
            StartChar = minCluster, EndChar = _textLen,
            Top = lineIndex * lineHeight, Height = lineHeight, Width = x,
            FirstGlyph = firstGlyph, GlyphCount = useLen,
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
        if (line.GlyphCount == 0) return 0f;
        int gEnd = line.FirstGlyph + line.GlyphCount;
        for (int i = line.FirstGlyph; i < gEnd; i++)
            if (_clusters[i].Cluster >= charIndex) return _clusters[i].X;
        ref readonly var last = ref _clusters[gEnd - 1];
        return last.X + last.Advance;
    }

    /// <summary>The insertion index for a hit past a line's right edge. Normally the line's EndChar (== the next line's
    /// start at a soft wrap — affinity is the caller's job); a HARD-broken line (UAX #14 MustBreak before the next line)
    /// returns the terminator cluster's start instead, keeping the caret on the clicked line. (TextEditCore feeds
    /// single-char '\r' hard breaks, so the terminator is one cluster.)</summary>
    private int LineEndInsertion(int li)
    {
        ref readonly var line = ref _lines[li];
        if (li + 1 < _lineRecCount && line.GlyphCount > 0)
        {
            int nextStart = _lines[li + 1].StartChar;
            if (nextStart >= 0 && nextStart < _breaks.Count && _breaks[nextStart].BreakBefore == BreakOpp.MustBreak)
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
                    uint cp = txt[pos + len]; ushort gi; baseFace->GetGlyphIndices(&cp, 1, &gi);
                    if (gi == 0) break;
                    len++;
                }
                subLen = len; return;   // base face covers [pos, pos+len) — the same face the per-char path uses
            }
        }

        if (_fallback == null || _sysColl == null) return;
        string fam = string.IsNullOrEmpty(family) ? DefaultFamily : family;
        if (fam.IndexOf('#') >= 0 || fam.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || fam.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) return;
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

    private IDWriteFontFace* ResolveFallbackFace(string family, IDWriteFont* font, IDWriteFontFace* baseFace)
    {
        if (_fallbackFaces.TryGetValue(family, out var c)) return (IDWriteFontFace*)c;
        IDWriteFontFace* face;
        if ((int)font->CreateFontFace(&face) < 0) return baseFace;
        _fallbackFaces[family] = (nint)face;
        return face;
    }

    private IDWriteFontFace* ResolveFace(string family, int weight, out FaceMetrics m)
    {
        if (string.IsNullOrEmpty(family)) family = DefaultFamily;
        var key = (family, weight);
        if (_faces.TryGetValue(key, out var cached)) { m = _metrics[key]; return (IDWriteFontFace*)cached; }
        IDWriteFontFace* face;
        try { face = CreateFaceFor(family, weight); } catch { face = family == DefaultFamily ? null : CreateFaceFor(DefaultFamily, weight); }
        if (face == null)
        {
            // Faceless fallback: Segoe-ish design ratios (per 2048 em) so metrics-dependent callers stay sane.
            m = new FaceMetrics { Em = 2048, Asc = 1500, Desc = 500, UnderPos = -200, UnderThk = 140, StrikePos = 600, Cap = 1434 };
            return null;
        }
        DWRITE_FONT_METRICS fm; face->GetMetrics(&fm);
        m = new FaceMetrics
        {
            Em = fm.designUnitsPerEm, Asc = (short)fm.ascent, Desc = (short)fm.descent,
            UnderPos = fm.underlinePosition, UnderThk = fm.underlineThickness, StrikePos = fm.strikethroughPosition,
            // OS/2 cap height for TextLineBounds.Tight; a font that reports none gets the ~0.7 em convention
            // (the headless model's capHeight ≈ 0.7 × size, HeadlessFontSystem.cs:13).
            Cap = fm.capHeight != 0 ? fm.capHeight : (ushort)(fm.designUnitsPerEm * 7 / 10),
        };
        _faces[key] = (nint)face; _metrics[key] = m;
        return face;
    }

    private IDWriteFontFace* CreateFaceFor(string family, int weight)
    {
        int hash = family.IndexOf('#');
        string path = hash >= 0 ? family.Substring(0, hash) : family;
        bool isFile = path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
        if (isFile)
        {
            IDWriteFontFile* file;
            fixed (char* p = path) Check(_dw->CreateFontFileReference(p, null, &file), "CreateFontFileReference");
            BOOL sup; DWRITE_FONT_FILE_TYPE ft; DWRITE_FONT_FACE_TYPE fct; uint nf;
            Check(file->Analyze(&sup, &ft, &fct, &nf), "Analyze");
            IDWriteFontFile** files = &file;
            // A raw file face has no weight family to pick from — synthesize bold for the heavy half (≥ 600).
            var sim = weight >= 600 ? DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD : DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE;
            IDWriteFontFace* ff; Check(_dw->CreateFontFace(fct, 1, files, 0, sim, &ff), "CreateFontFace(file)");
            file->Release(); return ff;
        }
        IDWriteFontCollection* coll;
        Check(_dw->GetSystemFontCollection(&coll, BOOL.FALSE), "GetSystemFontCollection");
        uint idx; BOOL exists; fixed (char* pn = family) Check(coll->FindFamilyName(pn, &idx, &exists), "FindFamilyName");
        if (!exists) { coll->Release(); return family == DefaultFamily ? null : CreateFaceFor(DefaultFamily, weight); }
        IDWriteFontFamily* fam; Check(coll->GetFontFamily(idx, &fam), "GetFontFamily");
        // The numeric weight IS the DWRITE_FONT_WEIGHT — GetFirstMatchingFont picks the nearest face/named instance
        // (variable families like "Segoe UI Variable Text" expose their weight axis as named instances here).
        IDWriteFont* font;
        Check(fam->GetFirstMatchingFont((DWRITE_FONT_WEIGHT)weight, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font), "GetFirstMatchingFont");
        IDWriteFontFace* face2; Check(font->CreateFontFace(&face2), "CreateFontFace");
        font->Release(); fam->Release(); coll->Release();
        return face2;
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
            using var e2 = new TextLayoutEngine();
            using var fresh = new TextLayoutEngine();
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

    public void Dispose()
    {
        foreach (var kv in _faces) ((IDWriteFontFace*)kv.Value)->Release();
        foreach (var kv in _fallbackFaces) ((IDWriteFontFace*)kv.Value)->Release();
        _faces.Clear(); _fallbackFaces.Clear();
        if (_fsrc != null) { TextAnalysisSourceCcw.Destroy(_fsrc); _fsrc = null; }
        if (_fallback != null) { _fallback->Release(); _fallback = null; }
        if (_sysColl != null) { _sysColl->Release(); _sysColl = null; }
        _shaper.Dispose(); _itemizer.Dispose();
        if (_dw != null) { _dw->Release(); _dw = null; }
    }
}
