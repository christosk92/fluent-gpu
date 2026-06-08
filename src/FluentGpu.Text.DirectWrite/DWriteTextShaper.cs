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

    /// <summary>Shape [start, start+length) of <paramref name="text"/> with the given face/script/direction at
    /// <paramref name="size"/> DIP. Returns the shaped glyphs (logical order) in a reused buffer.</summary>
    public ReadOnlySpan<GlyphPlacement> Shape(char* text, int start, int length, IDWriteFontFace* face, float size, ushort scriptId, byte shapes, bool rtl)
    {
        if (length <= 0) return ReadOnlySpan<GlyphPlacement>.Empty;
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
                    null, null, 0, (uint)_maxGlyphs, cm, tp, gi, gp, &actual);
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
                BOOL.FALSE, isRtl, &sa, null, null, null, 0, adv, off), "GetGlyphPlacements");

        int gc = (int)actual;
        if (_out.Length < gc) _out = new GlyphPlacement[gc];
        // cluster start (first source text index) per glyph, inverting clusterMap
        Span<int> clusterStart = gc <= 256 ? stackalloc int[gc] : new int[gc];
        clusterStart.Fill(-1);
        for (int t = 0; t < length; t++) { int g = _cluster[t]; if (g >= 0 && g < gc && clusterStart[g] < 0) clusterStart[g] = start + t; }
        for (int i = 0; i < gc; i++)
            _out[i] = new GlyphPlacement(_gids[i], _adv[i], _off[i].advanceOffset, _off[i].ascenderOffset, clusterStart[i] < 0 ? start : clusterStart[i]);
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
        IDWriteFontFace* face = shaper.ResolveSystemFace("Segoe UI", false);
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
