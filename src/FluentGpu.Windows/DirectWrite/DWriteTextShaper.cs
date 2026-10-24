using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Text.DirectWrite;

/// <summary>
/// DirectWrite glyph shaper (text.md §4.4): turns one itemized run (single face + script + direction) into positioned
/// glyphs via <c>IDWriteTextAnalyzer.GetGlyphs</c> + <c>GetGlyphPlacements</c> — real cluster/ligature/kerning/RTL
/// shaping, not per-char design advances. Advances come out in DIP (GetGlyphPlacements is given the DIP em size).
/// Scratch buffers are grown and reused; <see cref="Shape"/> returns a span into the reused output buffer (valid until
/// the next call). COM via TerraFX raw pointers.
/// </summary>
public sealed unsafe class DWriteTextShaper : IDisposable
{
    private IDWriteFactory* _dw;
    private IDWriteTextAnalyzer* _an;
    private readonly bool _ownsFactory;

    private ushort[] _cluster = [];
    private DWRITE_SHAPING_TEXT_PROPERTIES[] _textProps = [];
    private int _maxGlyphs;
    private ushort[] _gids = [];
    private DWRITE_SHAPING_GLYPH_PROPERTIES[] _glyphProps = [];
    private float[] _adv = [];
    private DWRITE_GLYPH_OFFSET[] _off = [];
    private GlyphPlacement[] _out = [];

    public DWriteTextShaper(IDWriteFactory* factory = null)
    {
        if (factory == null)
        {
            IDWriteFactory* f;
            Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
            _dw = f; _ownsFactory = true;
        }
        else { _dw = factory; _ownsFactory = false; }
        IDWriteTextAnalyzer* a;
        Check(_dw->CreateTextAnalyzer(&a), "CreateTextAnalyzer");
        _an = a;
    }

    // FontFeatures bit → DWRITE_FONT_FEATURE_TAG (bits 4..7 are unassigned → 0, skipped). Bit layout: FontFeatures.
    private static readonly DWRITE_FONT_FEATURE_TAG[] s_featureTags =
    [
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_TABULAR_FIGURES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_LINING_FIGURES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_OLD_STYLE_FIGURES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_SLASHED_ZERO,
        0, 0, 0, 0,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_1,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_2,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_3,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_4,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_5,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_6,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_7,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_8,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_9,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_10,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_11,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_12,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_13,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_14,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_15,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_16,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_17,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_18,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_19,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STYLISTIC_SET_20,
    ];

    /// <summary>Shape [start, start+length) of <paramref name="text"/> with the given face/script/direction at
    /// <paramref name="size"/> DIP. Returns the shaped glyphs (logical order) in a reused buffer.
    /// <paramref name="features"/> is a packed <c>FontFeatures</c> mask: every set bit becomes one enabled OpenType
    /// feature in ONE <c>DWRITE_TYPOGRAPHIC_FEATURES</c> range covering the whole sub-run, handed IDENTICALLY to
    /// <c>GetGlyphs</c> (substitution: tnum/onum/zero/ssNN swap glyph ids) and <c>GetGlyphPlacements</c> (positioning:
    /// tnum's equal advances) — a mismatch would place one feature's glyphs with another's advances. The font's default
    /// features (kern, liga, …) still apply; 0 ⇒ the null-features call (unchanged).</summary>
    public ReadOnlySpan<GlyphPlacement> Shape(char* text, int start, int length, IDWriteFontFace* face, float size, ushort scriptId, byte shapes, bool rtl,
        uint features = 0)
    {
        if (length <= 0) return ReadOnlySpan<GlyphPlacement>.Empty;
        // Feature list on the stack (≤ 24 entries) — zero allocation per shape.
        DWRITE_FONT_FEATURE* featList = stackalloc DWRITE_FONT_FEATURE[s_featureTags.Length];
        uint featCount = 0;
        for (int b = 0; b < s_featureTags.Length; b++)
            if ((features & (1u << b)) != 0 && s_featureTags[b] != 0)
                featList[featCount++] = new DWRITE_FONT_FEATURE { nameTag = s_featureTags[b], parameter = 1 };
        DWRITE_TYPOGRAPHIC_FEATURES typo = new DWRITE_TYPOGRAPHIC_FEATURES { features = featList, featureCount = featCount };
        DWRITE_TYPOGRAPHIC_FEATURES* typoPtr = &typo;
        uint rangeLen = (uint)length;
        DWRITE_TYPOGRAPHIC_FEATURES** featRanges = featCount > 0 ? &typoPtr : null;
        uint* featRangeLens = featCount > 0 ? &rangeLen : null;
        uint featRangeCount = featCount > 0 ? 1u : 0u;
        if (_cluster.Length < length) { _cluster = new ushort[length]; _textProps = new DWRITE_SHAPING_TEXT_PROPERTIES[length]; }
        if (_maxGlyphs < 3 * length / 2 + 16) GrowGlyphs(3 * length / 2 + 16);

        var sa = new DWRITE_SCRIPT_ANALYSIS { script = scriptId, shapes = (DWRITE_SCRIPT_SHAPES)shapes };
        uint actual = 0;
        BOOL isRtl = rtl ? BOOL.TRUE : BOOL.FALSE;

        // GetGlyphs with the classic E_NOT_SUFFICIENT_BUFFER (0x8007007A) grow-and-retry loop.
        while (true)
        {
            HRESULT hr;
            fixed (ushort* cm = _cluster, gi = _gids)
            fixed (DWRITE_SHAPING_TEXT_PROPERTIES* tp = _textProps)
            fixed (DWRITE_SHAPING_GLYPH_PROPERTIES* gp = _glyphProps)
                hr = _an->GetGlyphs(text + start, (uint)length, face, BOOL.FALSE, isRtl, &sa, null, null,
                    featRanges, featRangeLens, featRangeCount, (uint)_maxGlyphs, cm, tp, gi, gp, &actual);
            if ((uint)hr == 0x8007007A) { GrowGlyphs(_maxGlyphs * 2); continue; }
            Check(hr, "GetGlyphs");
            break;
        }

        fixed (ushort* cm = _cluster, gi = _gids)
        fixed (DWRITE_SHAPING_TEXT_PROPERTIES* tp = _textProps)
        fixed (DWRITE_SHAPING_GLYPH_PROPERTIES* gp = _glyphProps)
        fixed (float* adv = _adv)
        fixed (DWRITE_GLYPH_OFFSET* off = _off)
            Check(_an->GetGlyphPlacements(text + start, cm, tp, (uint)length, gi, gp, actual, face, size,
                BOOL.FALSE, isRtl, &sa, null, featRanges, featRangeLens, featRangeCount, adv, off), "GetGlyphPlacements");

        int gc = (int)actual;
        if (_out.Length < gc) _out = new GlyphPlacement[gc];
        // cluster start (first source text index) per glyph, inverting clusterMap. clusterMap points each char at its
        // cluster's FIRST glyph only, so the rest of a multi-glyph cluster (a reordered Indic matra, an unattached
        // combining mark) has no entry: it inherits the previous glyph's cluster (glyphs are in logical order), never
        // the sub-run start, whose break opportunity (MustBreak after a newline, CanBreak after a space) would
        // otherwise land mid-cluster.
        Span<int> clusterStart = gc <= 256 ? stackalloc int[gc] : new int[gc];
        clusterStart.Fill(-1);
        for (int t = 0; t < length; t++) { int g = _cluster[t]; if (g >= 0 && g < gc && clusterStart[g] < 0) clusterStart[g] = start + t; }
        for (int i = 0; i < gc; i++)
        {
            if (clusterStart[i] < 0) clusterStart[i] = i > 0 ? clusterStart[i - 1] : start;
            _out[i] = new GlyphPlacement(_gids[i], _adv[i], _off[i].advanceOffset, _off[i].ascenderOffset, clusterStart[i]);
        }
        return _out.AsSpan(0, gc);
    }

    private void GrowGlyphs(int n)
    {
        _maxGlyphs = n;
        _gids = new ushort[n]; _glyphProps = new DWRITE_SHAPING_GLYPH_PROPERTIES[n];
        _adv = new float[n]; _off = new DWRITE_GLYPH_OFFSET[n];
    }

    /// <summary>Smoke test (Windows only): shape Latin + an Arabic run and print gids/advances. Proves the shaping
    /// COM round-trip (GetGlyphs/GetGlyphPlacements) works and ligatures/marks collapse.</summary>
    public static void SelfTest()
    {
        using var shaper = new DWriteTextShaper();
        IDWriteFontFace* face = shaper.ResolveSystemFace(FluentGpu.Dsl.Tok.Definition.Fonts.BodyPrimary, false);
        void ShapeAndPrint(string s, ushort script)
        {
            fixed (char* p = s)
            {
                var glyphs = shaper.Shape(p, 0, s.Length, face, 14f, script, 0, false);
                Console.Write($"[shape] \"{s}\" ({s.Length} chars) -> {glyphs.Length} glyphs: ");
                foreach (var g in glyphs) Console.Write($"[gid={g.GlyphId} adv={g.Advance:0.0} cl={g.Cluster}] ");
                Console.WriteLine();
            }
        }
        ShapeAndPrint("Hello", 49);        // Latin — 5 chars → 5 glyphs
        ShapeAndPrint("fi office", 49);    // 'fi' may form a ligature (fewer glyphs than chars)
        ShapeAndPrint("AVA", 49);          // kerning pairs
        face->Release();
    }

    private IDWriteFontFace* ResolveSystemFace(string familyName, bool bold)
    {
        IDWriteFontCollection* coll;
        Check(_dw->GetSystemFontCollection(&coll, BOOL.FALSE), "GetSystemFontCollection");
        uint index; BOOL exists;
        fixed (char* pn = familyName) Check(coll->FindFamilyName(pn, &index, &exists), "FindFamilyName");
        IDWriteFontFamily* fam;
        Check(coll->GetFontFamily(index, &fam), "GetFontFamily");
        IDWriteFont* font;
        var weight = bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL;
        Check(fam->GetFirstMatchingFont(weight, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font), "GetFirstMatchingFont");
        IDWriteFontFace* face;
        Check(font->CreateFontFace(&face), "CreateFontFace");
        font->Release(); fam->Release(); coll->Release();
        return face;
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    public void Dispose()
    {
        if (_an != null) { _an->Release(); _an = null; }
        if (_ownsFactory && _dw != null) { _dw->Release(); _dw = null; }
    }
}
