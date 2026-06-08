using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Text.DirectWrite;

/// <summary>One laid-out glyph: post-shaping glyph id, the DWrite face it belongs to (as <c>nint</c>), and its pen
/// position in DIP — X within the paragraph (line-relative origin folded in), Y = its line baseline from the top.</summary>
public readonly struct LaidGlyph
{
    public readonly ushort Gid;
    public readonly nint Face;
    public readonly float X, Y;
    public LaidGlyph(ushort gid, nint face, float x, float y) { Gid = gid; Face = face; X = x; Y = y; }
}

/// <summary>
/// The DirectWrite layout engine (text.md §8): itemize → shape (per run) → wrap (UAX #14 break opportunities) → position,
/// with BiDi L2 reordering per line. Drives BOTH measurement (metrics) and rendering (positioned glyphs) so they layout
/// identically. Owns its itemizer + shaper + face cache; reusable, single-thread-confined. Results live in reused buffers
/// (valid until the next <see cref="Layout"/>). v1: design-advance shaping with kerning/ligatures/complex-script; font
/// fallback + color glyphs are layered on in later phases.
/// </summary>
public sealed unsafe class TextLayoutEngine : IDisposable
{
    private const string DefaultFamily = "Segoe UI";
    private IDWriteFactory* _dw;
    private readonly DWriteItemizer _itemizer;
    private readonly DWriteTextShaper _shaper;
    private readonly Dictionary<(string fam, bool bold), nint> _faces = new();
    private readonly Dictionary<(string fam, bool bold), (ushort em, short asc, short desc)> _metrics = new();
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
    private ushort _ellGid; private float _ellAdv; private nint _ellFace;

    public float Width { get; private set; }
    public float Height { get; private set; }
    public float Baseline { get; private set; }
    public int LineCount { get; private set; }
    public ReadOnlySpan<LaidGlyph> Glyphs => _laid.AsSpan(0, _laidCount);

    private struct RunGlyph { public ushort Gid; public nint Face; public float Advance; public int Cluster; public byte Level; }

    public TextLayoutEngine()
    {
        IDWriteFactory* f;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
        _dw = f;
        _itemizer = new DWriteItemizer();
        _shaper = new DWriteTextShaper(_dw);
        ResolveFace(DefaultFamily, false, out _, out _, out _);

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
    /// are the TextWrap/TextTrim enum ints; <paramref name="size"/> is DIP.</summary>
    public void Layout(ReadOnlySpan<char> text, string family, bool bold, float size, float maxWidth, int wrap, int trim, int maxLines)
    {
        _glyphCount = 0; _laidCount = 0;
        var face = ResolveFace(family, bold, out ushort em, out short asc, out short desc);
        float scale = em > 0 ? size / em : size / 2048f;
        float lineHeight = (asc + desc) * scale;
        if (lineHeight <= 0f) lineHeight = size * 1.3f;
        Baseline = asc * scale;
        int n = text.Length;
        if (n == 0) { Width = 0; Height = lineHeight; LineCount = 1; return; }

        // Ellipsis glyph for this face (for trim) — a single glyph, no shaping needed.
        _ellFace = (nint)face; _ellGid = 0; _ellAdv = 0f;
        if (face != null)
        {
            uint ec = '…'; ushort eg; face->GetGlyphIndices(&ec, 1, &eg);
            DWRITE_GLYPH_METRICS egm; face->GetDesignGlyphMetrics(&eg, 1, &egm, BOOL.FALSE);
            _ellGid = eg; _ellAdv = (float)egm.advanceWidth * scale;
        }

        _itemizer.Itemize(text, _runs, _breaks);

        // Shape each run, splitting by font-fallback coverage so CJK/emoji/symbols resolve to a covering face.
        fixed (char* p = text)
        {
            _fsrc->Text = p; _fsrc->Len = (uint)n;
            foreach (var run in _runs)
            {
                int pos = run.Start, remaining = run.Length;
                while (remaining > 0)
                {
                    ResolveRunFace(family, bold, face, pos, remaining, out IDWriteFontFace* subFace, out int subLen);
                    if (subLen <= 0) subLen = remaining;
                    var shaped = _shaper.Shape(p, pos, subLen, subFace, size, run.ScriptId, run.ScriptShapes, run.IsRightToLeft);
                    EnsureGlyphs(_glyphCount + shaped.Length);
                    foreach (var g in shaped)
                        _glyphs[_glyphCount++] = new RunGlyph { Gid = g.GlyphId, Face = (nint)subFace, Advance = g.Advance, Cluster = g.Cluster, Level = run.BidiLevel };
                    pos += subLen; remaining -= subLen;
                }
            }
        }

        WrapAndPosition(size, scale, lineHeight, maxWidth, wrap, trim, maxLines, (nint)face);
    }

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

                if (mustBreak && i > lineStart) { EmitLine(lineStart, i, line, lineHeight, doTrim, maxWidth, ref maxLineW); lineStart = i; line++; pen = 0f; lastBreak = -1; }
                if (canBreak) lastBreak = i;

                float adv = _glyphs[i].Advance;
                if (doWrap && pen + adv > maxWidth && i > lineStart && line + 1 < maxL)
                {
                    int br = lastBreak > lineStart ? lastBreak : i;
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
    }

    // Position glyphs [start,end) on one line with BiDi L2 reordering; trim+ellipsize if the line overflows; append to _laid.
    private void EmitLine(int start, int end, int lineIndex, float lineHeight, bool doTrim, float maxWidth, ref float maxLineW)
    {
        if (end <= start) return;
        // Reorder visual order: reverse maximal sequences of level >= L, for L from highest down to 1 (UAX #9 L2).
        int len = end - start;
        Span<int> order = len <= 256 ? stackalloc int[len] : new int[len];
        for (int i = 0; i < len; i++) order[i] = start + i;
        byte maxLevel = 0, minOdd = 255;
        for (int i = start; i < end; i++) { byte l = _glyphs[i].Level; if (l > maxLevel) maxLevel = l; if ((l & 1) != 0 && l < minOdd) minOdd = l; }
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
        for (int k = 0; k < useLen; k++)
        {
            ref readonly var g = ref _glyphs[order[k]];
            _laid[_laidCount++] = new LaidGlyph(g.Gid, g.Face, x, baselineY);
            x += g.Advance;
        }
        if (ellipsize && _ellFace != 0)
        {
            _laid[_laidCount++] = new LaidGlyph(_ellGid, _ellFace, x, baselineY);
            x += _ellAdv;
        }
        if (x > maxLineW) maxLineW = x;
    }

    private void EnsureGlyphs(int n) { if (_glyphs.Length < n) Array.Resize(ref _glyphs, Math.Max(n, _glyphs.Length * 2)); }
    private void EnsureLaid(int n) { if (_laid.Length < n) Array.Resize(ref _laid, Math.Max(n, _laid.Length * 2)); }

    // Pick the face covering [pos, pos+subLen) via system fallback; subLen is the coverage-run length DWrite reports.
    private void ResolveRunFace(string family, bool bold, IDWriteFontFace* baseFace, int pos, int remaining, out IDWriteFontFace* subFace, out int subLen)
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
        var weight = bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL;
        int hr;
        fixed (char* fn = fam)
            hr = (int)_fallback->MapCharacters((IDWriteTextAnalysisSource*)_fsrc, (uint)pos, (uint)remaining, _sysColl, fn,
                weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, &mappedLen, &mappedFont, &sc);
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

    private IDWriteFontFace* ResolveFace(string family, bool bold, out ushort em, out short asc, out short desc)
    {
        if (string.IsNullOrEmpty(family)) family = DefaultFamily;
        var key = (family, bold);
        if (_faces.TryGetValue(key, out var cached)) { var m = _metrics[key]; em = m.em; asc = m.asc; desc = m.desc; return (IDWriteFontFace*)cached; }
        IDWriteFontFace* face;
        try { face = CreateFaceFor(family, bold); } catch { face = family == DefaultFamily ? null : CreateFaceFor(DefaultFamily, bold); }
        if (face == null) { em = 2048; asc = 1500; desc = 500; return null; }
        DWRITE_FONT_METRICS fm; face->GetMetrics(&fm);
        em = fm.designUnitsPerEm; asc = (short)fm.ascent; desc = (short)fm.descent;
        _faces[key] = (nint)face; _metrics[key] = (em, asc, desc);
        return face;
    }

    private IDWriteFontFace* CreateFaceFor(string family, bool bold)
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
            var sim = bold ? DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD : DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE;
            IDWriteFontFace* ff; Check(_dw->CreateFontFace(fct, 1, files, 0, sim, &ff), "CreateFontFace(file)");
            file->Release(); return ff;
        }
        IDWriteFontCollection* coll;
        Check(_dw->GetSystemFontCollection(&coll, BOOL.FALSE), "GetSystemFontCollection");
        uint idx; BOOL exists; fixed (char* pn = family) Check(coll->FindFamilyName(pn, &idx, &exists), "FindFamilyName");
        if (!exists) { coll->Release(); return family == DefaultFamily ? null : CreateFaceFor(DefaultFamily, bold); }
        IDWriteFontFamily* fam; Check(coll->GetFontFamily(idx, &fam), "GetFontFamily");
        IDWriteFont* font; var w = bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL;
        Check(fam->GetFirstMatchingFont(w, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font), "GetFirstMatchingFont");
        IDWriteFontFace* face2; Check(font->CreateFontFace(&face2), "CreateFontFace");
        font->Release(); fam->Release(); coll->Release();
        return face2;
    }

    public static void SelfTest()
    {
        using var e = new TextLayoutEngine();
        void T(string s, float maxW)
        {
            e.Layout(s.AsSpan(), "Segoe UI", false, 14f, maxW, 1, 0, 0);
            Console.WriteLine($"[layout] \"{s}\" maxW={(float.IsInfinity(maxW) ? "inf" : maxW.ToString("0"))} -> {e.LineCount} lines, W={e.Width:0.0} H={e.Height:0.0} glyphs={e.Glyphs.Length}");
        }
        T("Hello world", float.PositiveInfinity);
        T("Hidden content, revealed when the Expander is expanded.", 200f);
        T("Hidden content, revealed when the Expander is expanded.", float.PositiveInfinity);
        T("AVAWAV", float.PositiveInfinity);   // kerned advances
        // Fallback: Latin + CJK + emoji should resolve to multiple covering faces with non-.notdef gids.
        e.Layout("Hi 你好 😀".AsSpan(), "Segoe UI", false, 14f, float.PositiveInfinity, 1, 0, 0);
        var faces = new HashSet<nint>(); int zero = 0;
        foreach (var g in e.Glyphs) { faces.Add(g.Face); if (g.Gid == 0) zero++; }
        Console.WriteLine($"[fallback] \"Hi 你好 😀\" -> {e.Glyphs.Length} glyphs across {faces.Count} face(s), {zero} .notdef");
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
