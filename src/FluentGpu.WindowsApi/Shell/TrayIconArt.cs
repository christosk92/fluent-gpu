using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;
using FluentGpu.Foundation;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;
using ColorF = FluentGpu.Foundation.ColorF;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>What a <see cref="NotifyIcon.SetIconSource"/> callback is asked to draw: the square frame size the taskbar's
/// DPI selects (<see cref="NotifyIcon.SmallIconSizeForDpi"/>) and whether the taskbar is light.</summary>
public readonly record struct TrayIconRequest(int SizePx, bool LightTaskbar);

/// <summary>A tray icon image: tightly packed, top-down, PREMULTIPLIED BGRA8 (<c>Width × Height × 4</c> bytes; an alpha-0
/// pixel is colour 0).</summary>
public readonly record struct TrayIconBitmap(byte[] PremultipliedBgra, int Width, int Height);

/// <summary>
/// Tray icon art drawn on the CPU — a font glyph (Segoe Fluent Icons by default) tinted for the taskbar, with an optional
/// badge (a rounded pill of 1–3 characters in the bottom-right corner, cut out of the glyph). Feed the result to
/// <see cref="NotifyIcon.SetIconSource"/> so it is redrawn when the taskbar's DPI or theme changes:
/// <code>tray.SetIconSource(r =&gt; TrayIconArt.Glyph(r, "", badge: "42"));</code>
/// </summary>
/// <remarks>Rasterized with DirectWrite (<c>CreateGlyphRunAnalysis</c> → alpha texture, the engine's glyph-atlas recipe)
/// into coverage, then tinted premultiplied. The glyph is centred on its ink at whole-pixel offsets, so the font's own
/// hinting grid is kept. A shared DirectWrite factory and the resolved faces are cached for the process. Any thread; calls
/// are serialized.</remarks>
[SupportedOSPlatform("windows6.1")]
public static unsafe class TrayIconArt
{
    private const string FluentIcons = "Segoe Fluent Icons", Mdl2Assets = "Segoe MDL2 Assets";
    private const string BadgeFamily = "Bahnschrift", BadgeFallbackFamily = "Segoe UI";
    private const int MaxRunGlyphs = 16;
    private static readonly string[] s_iconFamilies = [FluentIcons, Mdl2Assets];   // Windows 11, then Windows 10

    private static readonly object s_gate = new();
    private static IDWriteFactory* s_dw;
    private static bool s_dwFailed;
    private static readonly Dictionary<string, nint> s_faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Draw <paramref name="glyph"/> (one or a few characters — typically a Segoe Fluent Icons code point) at the request's
    /// size, with an optional <paramref name="badge"/>.
    /// </summary>
    /// <param name="request">The size and taskbar theme to draw for (size clamped to 16–48 px).</param>
    /// <param name="glyph">The glyph text; must not be empty.</param>
    /// <param name="ink">The glyph colour (straight-alpha sRGB); default white on a dark taskbar, #1B1B1B on a light one.</param>
    /// <param name="badge">Badge text — 1 to 3 characters (longer is cut; null/blank = no badge), drawn in condensed digits.</param>
    /// <param name="badgeFill">The pill's colour; default Fluent "critical" red. The badge text picks white or near-black
    /// for contrast.</param>
    /// <param name="fontPath">A <c>.ttf</c>/<c>.otf</c> to draw the glyph from, tried first; then Segoe Fluent Icons, then
    /// Segoe MDL2 Assets (Windows 10) — the first face that has every character wins.</param>
    /// <exception cref="ArgumentException"><paramref name="glyph"/> is null or empty.</exception>
    public static TrayIconBitmap Glyph(TrayIconRequest request, string glyph, ColorF? ink = null,
        string? badge = null, ColorF? badgeFill = null, string? fontPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(glyph);
        int size = TrayIconPixels.ClampArtSize(request.SizePx);
        var px = new byte[size * size * 4];
        ColorF inkColor = ink ?? TrayIconPixels.DefaultInk(request.LightTaskbar);
        string? text = TrayIconPixels.TrimBadge(badge);

        lock (s_gate)
        {
            IDWriteFactory* dw = Factory();
            if (dw == null) return new TrayIconBitmap(px, size, size);

            IDWriteFontFace* face = ResolveFace(dw, glyph, fontPath);
            if (face != null && Rasterize(dw, face, glyph, size, out byte[] cov, out RECT b))
            {
                int w = b.right - b.left, h = b.bottom - b.top;
                int x = b.left + TrayIconPixels.CenterOffset(b.left, b.right, 0, size);
                int y = b.top + TrayIconPixels.CenterOffset(b.top, b.bottom, 0, size);
                TrayIconPixels.SourceOver(px, size, size, cov, w, h, x, y, inkColor);
            }
            else if (face == null)
                Diag.Line($"[notifyicon] TrayIconArt: no font has glyph U+{(int)glyph[0]:X4} ({fontPath ?? FluentIcons})");

            if (text is not null)
            {
                int badgeH = TrayIconPixels.BadgeHeight(size);
                // Condensed digits at an em of the pill's height: the figures' cap height (~0.7 em) leaves a 1–2 px rim.
                IDWriteFontFace* bface = BadgeFace(dw, text);
                byte[] tcov = [];
                RECT tb = default;
                bool haveText = bface != null && Rasterize(dw, bface, text, badgeH, out tcov, out tb);
                TrayBadgeRect pill = TrayIconPixels.BadgeRect(size, haveText ? tb.right - tb.left : 0);
                TrayIconPixels.KnockOut(px, size, size, pill, ring: size >= 32 ? 2f : 1f);
                ColorF fill = badgeFill ?? TrayIconPixels.DefaultBadgeFill;
                TrayIconPixels.FillPill(px, size, size, pill, fill);
                if (haveText)
                {
                    int x = tb.left + TrayIconPixels.CenterOffset(tb.left, tb.right, pill.X, pill.Width);
                    int y = tb.top + TrayIconPixels.CenterOffset(tb.top, tb.bottom, pill.Y, pill.Height);
                    TrayIconPixels.SourceOver(px, size, size, tcov, tb.right - tb.left, tb.bottom - tb.top, x, y,
                        TrayIconPixels.ContrastInk(fill));
                }
            }
        }
        return new TrayIconBitmap(px, size, size);
    }

    // ── DirectWrite (cache-miss path only: a handful of calls per DPI/theme change) ─────────────────────────────────────

    private static IDWriteFactory* Factory()
    {
        if (s_dw != null || s_dwFailed) return s_dw;
        IDWriteFactory* f;
        if ((int)DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory>(), (IUnknown**)&f) < 0 || f == null)
        {
            s_dwFailed = true;
            Diag.Line("[notifyicon] TrayIconArt: DWriteCreateFactory failed — glyph icons draw blank");
            return null;
        }
        s_dw = f;
        return f;
    }

    private static IDWriteFontFace* ResolveFace(IDWriteFactory* dw, string glyph, string? fontPath)
    {
        IDWriteFontFace* any = null;
        if (!string.IsNullOrEmpty(fontPath))
        {
            IDWriteFontFace* file = CachedFace(dw, "file:" + fontPath, fontPath, isFile: true, 400, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL);
            if (file != null && HasGlyphs(file, glyph)) return file;
            any = file;
        }
        foreach (string family in s_iconFamilies)
        {
            IDWriteFontFace* f = CachedFace(dw, family, family, isFile: false, 400, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL);
            if (f == null) continue;
            if (HasGlyphs(f, glyph)) return f;
            if (any == null) any = f;
        }
        return any;   // nothing has every character: draw what the first face has (a missing glyph's .notdef box)
    }

    private static IDWriteFontFace* BadgeFace(IDWriteFactory* dw, string text)
    {
        IDWriteFontFace* f = CachedFace(dw, "badge:" + BadgeFamily, BadgeFamily, isFile: false, 600, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_CONDENSED);
        if (f != null && HasGlyphs(f, text)) return f;
        IDWriteFontFace* g = CachedFace(dw, "badge:" + BadgeFallbackFamily, BadgeFallbackFamily, isFile: false, 600, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL);
        return g != null ? g : f;
    }

    private static IDWriteFontFace* CachedFace(IDWriteFactory* dw, string key, string source, bool isFile, int weight, DWRITE_FONT_STRETCH stretch)
    {
        if (s_faces.TryGetValue(key, out nint cached)) return (IDWriteFontFace*)cached;
        IDWriteFontFace* face = isFile ? CreateFileFace(dw, source) : CreateSystemFace(dw, source, weight, stretch);
        s_faces[key] = (nint)face;   // a miss is cached too (null): an absent family is not re-probed per redraw
        return face;
    }

    private static IDWriteFontFace* CreateSystemFace(IDWriteFactory* dw, string familyName, int weight, DWRITE_FONT_STRETCH stretch)
    {
        IDWriteFontCollection* coll;
        if ((int)dw->GetSystemFontCollection(&coll, BOOL.FALSE) < 0 || coll == null) return null;
        uint index; BOOL exists;
        int hr;
        fixed (char* pn = familyName) hr = (int)coll->FindFamilyName(pn, &index, &exists);
        if (hr < 0 || !exists) { coll->Release(); return null; }
        IDWriteFontFamily* family;
        if ((int)coll->GetFontFamily(index, &family) < 0) { coll->Release(); return null; }
        IDWriteFont* font;
        hr = (int)family->GetFirstMatchingFont((DWRITE_FONT_WEIGHT)weight, stretch, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &font);
        IDWriteFontFace* face = null;
        if (hr >= 0)
        {
            if ((int)font->CreateFontFace(&face) < 0) face = null;
            font->Release();
        }
        family->Release();
        coll->Release();
        return face;
    }

    private static IDWriteFontFace* CreateFileFace(IDWriteFactory* dw, string path)
    {
        IDWriteFontFile* file;
        int hr;
        fixed (char* p = path) hr = (int)dw->CreateFontFileReference(p, null, &file);
        if (hr < 0 || file == null) return null;
        BOOL supported; DWRITE_FONT_FILE_TYPE fileType; DWRITE_FONT_FACE_TYPE faceType; uint faces;
        IDWriteFontFace* face = null;
        if ((int)file->Analyze(&supported, &fileType, &faceType, &faces) >= 0 && supported)
        {
            IDWriteFontFile** files = &file;
            if ((int)dw->CreateFontFace(faceType, 1, files, 0, DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE, &face) < 0) face = null;
        }
        file->Release();
        return face;
    }

    /// <summary>Code points of <paramref name="text"/> (at most <see cref="MaxRunGlyphs"/>).</summary>
    private static int CodePoints(string text, Span<uint> into)
    {
        int n = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (n == into.Length) break;
            into[n++] = (uint)r.Value;
        }
        return n;
    }

    private static bool HasGlyphs(IDWriteFontFace* face, string text)
    {
        Span<uint> cps = stackalloc uint[MaxRunGlyphs];
        int n = CodePoints(text, cps);
        if (n == 0) return false;
        ushort* gi = stackalloc ushort[MaxRunGlyphs];
        fixed (uint* pc = cps)
            if ((int)face->GetGlyphIndices(pc, (uint)n, gi) < 0) return false;
        for (int i = 0; i < n; i++) if (gi[i] == 0) return false;
        return true;
    }

    /// <summary>Rasterize <paramref name="text"/> at <paramref name="emPx"/> into an 8-bit coverage mask; the bounds are
    /// relative to a baseline origin at (0, 0). False when nothing has ink.</summary>
    private static bool Rasterize(IDWriteFactory* dw, IDWriteFontFace* face, string text, float emPx, out byte[] coverage, out RECT bounds)
    {
        coverage = [];
        bounds = default;
        Span<uint> cps = stackalloc uint[MaxRunGlyphs];
        int n = CodePoints(text, cps);
        if (n == 0) return false;
        ushort* gi = stackalloc ushort[MaxRunGlyphs];
        float* adv = stackalloc float[MaxRunGlyphs];
        DWRITE_GLYPH_METRICS* gm = stackalloc DWRITE_GLYPH_METRICS[MaxRunGlyphs];
        fixed (uint* pc = cps)
            if ((int)face->GetGlyphIndices(pc, (uint)n, gi) < 0) return false;
        DWRITE_FONT_METRICS fm;
        face->GetMetrics(&fm);
        if ((int)face->GetDesignGlyphMetrics(gi, (uint)n, gm, BOOL.FALSE) < 0 || fm.designUnitsPerEm == 0) return false;
        for (int i = 0; i < n; i++) adv[i] = gm[i].advanceWidth * emPx / fm.designUnitsPerEm;

        DWRITE_GLYPH_RUN run = default;
        run.fontFace = face;
        run.fontEmSize = emPx;
        run.glyphCount = (uint)n;
        run.glyphIndices = gi;
        run.glyphAdvances = adv;
        run.glyphOffsets = null;
        run.isSideways = BOOL.FALSE;
        run.bidiLevel = 0;

        // NATURAL_SYMMETRIC antialiases both axes (GlyphRenderer's choice); that mode answers only the CLEARTYPE_3x1
        // texture, whose three sub-samples are averaged back to one grey coverage value.
        IDWriteGlyphRunAnalysis* a;
        if ((int)dw->CreateGlyphRunAnalysis(&run, 1.0f, null, DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL, 0f, 0f, &a) < 0 || a == null)
            return false;
        try
        {
            RECT b;
            if ((int)a->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &b) < 0) return false;
            int w = b.right - b.left, h = b.bottom - b.top;
            if (w <= 0 || h <= 0) return false;
            byte[] rgb = new byte[w * h * 3];
            int hr;
            fixed (byte* pr = rgb)
                hr = (int)a->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &b, pr, (uint)rgb.Length);
            if (hr < 0) return false;
            var cov = new byte[w * h];
            for (int i = 0; i < cov.Length; i++) cov[i] = (byte)((rgb[3 * i] + rgb[3 * i + 1] + rgb[3 * i + 2]) / 3);
            coverage = cov;
            bounds = b;
            return true;
        }
        finally
        {
            a->Release();
        }
    }
}
