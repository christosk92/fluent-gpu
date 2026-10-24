using FluentGpu.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Text.DirectWrite;

/// <summary>
/// The DirectWrite <see cref="IFontFaceSource"/> behind <see cref="FontRegistry.Shared"/> (E9): the ONLY place on Windows
/// that turns a family name or a font file into an <c>IDWriteFontFace*</c>. The registry owns every policy decision (chains,
/// <c>#Family</c> selection, caching, counters); this class is the thin, thread-safe COM edge. The shared DirectWrite
/// factory is process-wide and free-threaded, so the UI thread (measure) and the render thread (raster) can both create
/// faces through it; the faces it returns are the ones both consumers use.
/// </summary>
public sealed unsafe class DWriteFontFaceSource : IFontFaceSource, IDisposable
{
    private IDWriteFactory* _dw;

    public DWriteFontFaceSource()
    {
        IDWriteFactory* f;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f), "DWriteCreateFactory");
        _dw = f;
    }

    /// <summary>Install this source on <see cref="FontRegistry.Shared"/> if no backend has yet (the engines' constructors call it).</summary>
    public static void EnsureInstalled() => FontRegistry.Shared.UseSourceIfNone(static () => new DWriteFontFaceSource());

    public bool HasSystemFamily(string family)
    {
        IDWriteFontCollection* coll;
        if ((int)_dw->GetSystemFontCollection(&coll, BOOL.FALSE) < 0) return false;
        uint idx; BOOL exists;
        int hr;
        fixed (char* pn = family) hr = (int)coll->FindFamilyName(pn, &idx, &exists);
        coll->Release();
        return hr >= 0 && exists;
    }

    public string[]? ReadFileFamilies(string path)
    {
        IDWriteFontFile* file;
        int hr;
        fixed (char* p = path) hr = (int)_dw->CreateFontFileReference(p, null, &file);
        if (hr < 0) return null;
        try
        {
            BOOL supported; DWRITE_FONT_FILE_TYPE fileType; DWRITE_FONT_FACE_TYPE faceType; uint numFaces;
            if ((int)file->Analyze(&supported, &fileType, &faceType, &numFaces) < 0 || !supported || numFaces == 0) return null;
            var names = new string[numFaces];
            IDWriteFontFile** files = &file;
            for (uint i = 0; i < numFaces; i++)
            {
                IDWriteFontFace* face;
                if ((int)_dw->CreateFontFace(faceType, 1, files, i, DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE, &face) < 0) { names[i] = ""; continue; }
                names[i] = FamilyNameOf(face);
                face->Release();
            }
            return names;
        }
        finally { file->Release(); }
    }

    // The family name of a face (its first locale's entry). IDWriteFontFace3 exposes it directly.
    private static string FamilyNameOf(IDWriteFontFace* face)
    {
        IDWriteFontFace3* f3;
        if ((int)face->QueryInterface(__uuidof<IDWriteFontFace3>(), (void**)&f3) < 0 || f3 == null) return "";
        try
        {
            IDWriteLocalizedStrings* names;
            if ((int)f3->GetFamilyNames(&names) < 0 || names == null) return "";
            try
            {
                uint len;
                if ((int)names->GetStringLength(0, &len) < 0) return "";
                var buf = new char[len + 1];
                fixed (char* b = buf) names->GetString(0, b, len + 1);
                return new string(buf, 0, (int)len);
            }
            finally { names->Release(); }
        }
        finally { f3->Release(); }
    }

    public nint CreateSystemFace(string family, int weight, out FontFaceMetrics metrics)
    {
        metrics = default;
        IDWriteFontCollection* coll;
        if ((int)_dw->GetSystemFontCollection(&coll, BOOL.FALSE) < 0) return 0;
        IDWriteFontFace* face = null;
        try
        {
            uint idx; BOOL exists;
            fixed (char* pn = family) Check(coll->FindFamilyName(pn, &idx, &exists), "FindFamilyName");
            if (!exists) return 0;
            IDWriteFontFamily* fam; Check(coll->GetFontFamily(idx, &fam), "GetFontFamily");
            // The numeric weight IS the DWRITE_FONT_WEIGHT - GetFirstMatchingFont picks the nearest face / variable-font
            // named instance (e.g. SemiBold 600 of "Segoe UI Variable Text").
            IDWriteFont* font;
            Check(fam->GetFirstMatchingFont((DWRITE_FONT_WEIGHT)weight, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font), "GetFirstMatchingFont");
            fam->Release();
            int hr = (int)font->CreateFontFace(&face);
            font->Release();
            if (hr < 0 || face == null) return 0;
        }
        finally { coll->Release(); }
        metrics = MetricsOf(face);
        return (nint)face;
    }

    public nint CreateFileFace(string path, int faceIndex, int weight, out FontFaceMetrics metrics)
    {
        metrics = default;
        IDWriteFontFile* file;
        int hr;
        fixed (char* p = path) hr = (int)_dw->CreateFontFileReference(p, null, &file);
        if (hr < 0) return 0;
        IDWriteFontFace* face;
        try
        {
            BOOL supported; DWRITE_FONT_FILE_TYPE fileType; DWRITE_FONT_FACE_TYPE faceType; uint numFaces;
            if ((int)file->Analyze(&supported, &fileType, &faceType, &numFaces) < 0 || !supported || (uint)faceIndex >= numFaces) return 0;
            IDWriteFontFile** files = &file;
            // A raw file has no weight family to pick from - synthesize bold for the heavy half (>= 600).
            var sim = weight >= 600 ? DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD : DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE;
            if ((int)_dw->CreateFontFace(faceType, 1, files, (uint)faceIndex, sim, &face) < 0 || face == null) return 0;
        }
        finally { file->Release(); }
        metrics = MetricsOf(face);
        return (nint)face;
    }

    public void ReleaseFace(nint face)
    {
        if (face != 0) ((IDWriteFontFace*)face)->Release();
    }

    private static FontFaceMetrics MetricsOf(IDWriteFontFace* face)
    {
        DWRITE_FONT_METRICS fm; face->GetMetrics(&fm);
        return new FontFaceMetrics
        {
            Em = fm.designUnitsPerEm, Asc = (short)fm.ascent, Desc = (short)fm.descent,
            UnderPos = fm.underlinePosition, UnderThk = fm.underlineThickness, StrikePos = fm.strikethroughPosition,
            // OS/2 cap height for TextLineBounds.Tight; a font that reports none gets the ~0.7 em convention
            // (the headless model's capHeight ~ 0.7 x size).
            Cap = fm.capHeight != 0 ? fm.capHeight : (ushort)(fm.designUnitsPerEm * 7 / 10),
        };
    }

    private static void Check(HRESULT hr, string what)
    {
        if ((int)hr < 0) throw new InvalidOperationException($"{what} failed: 0x{(uint)hr:X8}");
    }

    public void Dispose()
    {
        if (_dw != null) { _dw->Release(); _dw = null; }
    }
}
