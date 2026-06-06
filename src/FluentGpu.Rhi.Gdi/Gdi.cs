using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Text;

namespace FluentGpu.Rhi.Gdi;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE { public int Cx, Cy; }

internal static partial class Gdi32
{
    private const string U = "user32.dll";
    private const string G = "gdi32.dll";
    public const int TRANSPARENT = 1;
    public const uint SRCCOPY = 0x00CC0020;
    public const int NULL_PEN = 8;
    public const uint DT_LEFT = 0, DT_TOP = 0, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_NOCLIP = 0x100;
    public const int FW_NORMAL = 400, FW_BOLD = 700;
    public const uint DEFAULT_CHARSET = 1, CLEARTYPE_QUALITY = 5, DEFAULT_PITCH = 0, FF_DONTCARE = 0;

    [LibraryImport(U)] public static partial nint GetDC(nint hWnd);
    [LibraryImport(U)] public static partial int ReleaseDC(nint hWnd, nint hDC);
    [LibraryImport(U)] public static partial int FillRect(nint hDC, in RECT lprc, nint hbr);
    [LibraryImport(U, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(nint hdc, string text, int count, ref RECT lprc, uint format);

    [LibraryImport(G)] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport(G)] public static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport(G)] public static partial nint SelectObject(nint hdc, nint h);
    [LibraryImport(G)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint h);
    [LibraryImport(G)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport(G)] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport(G)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RoundRect(nint hdc, int l, int t, int r, int b, int ew, int eh);
    [LibraryImport(G)] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport(G)] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport(G)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport(G)] public static partial nint GetStockObject(int i);
    [LibraryImport(G, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline,
        uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string face);
    [LibraryImport(G, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextExtentPoint32W(nint hdc, string str, int c, out SIZE size);

    public static uint Bgr(ColorF c)
    {
        int r = (int)(Math.Clamp(c.R, 0, 1) * 255), g = (int)(Math.Clamp(c.G, 0, 1) * 255), b = (int)(Math.Clamp(c.B, 0, 1) * 255);
        return (uint)((b << 16) | (g << 8) | r);
    }
}

/// <summary>
/// Bring-up Windows renderer: rasterizes the DrawList to a real HWND with GDI (double-buffered), so the Counter
/// actually appears on screen and is interactive. The GPU path (Rhi.D3D12: SDF batcher + glyph atlas + DComp) supersedes
/// this — but GDI is the low-risk way to get correct rounded rects + crisp text into a window today.
/// </summary>
public sealed class GdiGpuDevice : IGpuDevice
{
    private readonly StringTable _strings;
    private GdiSwapchain? _sc;

    public GdiGpuDevice(StringTable strings) => _strings = strings;
    public string BackendName => "GDI";

    public ISwapchain CreateSwapchain(in SwapchainDesc desc)
    {
        _sc = new GdiSwapchain(desc.PresentTarget.Value, desc.SizePx);
        return _sc;
    }

    public void SubmitDrawList(ReadOnlySpan<byte> drawList, ReadOnlySpan<ulong> sortKeys, in FrameInfo ctx)
        => _sc?.Render(drawList, ctx, _strings);

    public void Dispose() => _sc?.Dispose();
}

public sealed class GdiSwapchain : ISwapchain
{
    private readonly nint _hwnd;
    private nint _memDc;
    private nint _bmp;
    private int _w, _h;

    public GdiSwapchain(nint hwnd, Size2 size)
    {
        _hwnd = hwnd;
        Ensure((int)size.Width, (int)size.Height);
    }

    public Size2 SizePx => new(_w, _h);

    private void Ensure(int w, int h)
    {
        if (w < 1) w = 1; if (h < 1) h = 1;
        if (w == _w && h == _h && _memDc != 0) return;
        // Order matters: a bitmap selected into a DC cannot be deleted. Destroy the DC first (deselects _bmp), then free it.
        if (_memDc != 0) Gdi32.DeleteDC(_memDc);
        if (_bmp != 0) Gdi32.DeleteObject(_bmp);
        nint wdc = Gdi32.GetDC(_hwnd);
        _memDc = Gdi32.CreateCompatibleDC(wdc);
        _bmp = Gdi32.CreateCompatibleBitmap(wdc, w, h);
        Gdi32.SelectObject(_memDc, _bmp);
        Gdi32.ReleaseDC(_hwnd, wdc);
        _w = w; _h = h;
    }

    public void Resize(Size2 px) => Ensure((int)px.Width, (int)px.Height);

    internal unsafe void Render(ReadOnlySpan<byte> cmds, in FrameInfo ctx, StringTable strings)
    {
        Ensure((int)ctx.SizePx.Width, (int)ctx.SizePx.Height);

        // clear
        var full = new RECT { Left = 0, Top = 0, Right = _w, Bottom = _h };
        nint clearBrush = Gdi32.CreateSolidBrush(Gdi32.Bgr(ctx.Clear));
        Gdi32.FillRect(_memDc, in full, clearBrush);
        Gdi32.DeleteObject(clearBrush);

        Gdi32.SetBkMode(_memDc, Gdi32.TRANSPARENT);
        nint nullPen = Gdi32.GetStockObject(Gdi32.NULL_PEN);
        nint oldPen = Gdi32.SelectObject(_memDc, nullPen);

        int pos = 0;
        while (pos + sizeof(int) <= cmds.Length)
        {
            int op = MemoryMarshal.Read<int>(cmds.Slice(pos));
            pos += sizeof(int);
            switch ((DrawOp)op)
            {
                case DrawOp.FillRoundRect:
                {
                    var c = MemoryMarshal.Read<FillRoundRectCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<FillRoundRectCmd>();
                    nint brush = Gdi32.CreateSolidBrush(Gdi32.Bgr(c.Fill));
                    nint old = Gdi32.SelectObject(_memDc, brush);
                    int rad = (int)MathF.Max(c.Radii.TopLeft, 0) * 2;
                    Gdi32.RoundRect(_memDc, (int)c.Rect.X, (int)c.Rect.Y, (int)c.Rect.Right, (int)c.Rect.Bottom, rad, rad);
                    Gdi32.SelectObject(_memDc, old);
                    Gdi32.DeleteObject(brush);
                    break;
                }
                case DrawOp.DrawGlyphRun:
                {
                    var c = MemoryMarshal.Read<DrawGlyphRunCmd>(cmds.Slice(pos));
                    pos += Unsafe.SizeOf<DrawGlyphRunCmd>();
                    string text = strings.Resolve(c.Text);
                    if (text.Length == 0) break;
                    int height = -(int)MathF.Round(c.FontSize);
                    nint font = Gdi32.CreateFontW(height, 0, 0, 0, c.Bold != 0 ? Gdi32.FW_BOLD : Gdi32.FW_NORMAL,
                        0, 0, 0, Gdi32.DEFAULT_CHARSET, 0, 0, Gdi32.CLEARTYPE_QUALITY, Gdi32.DEFAULT_PITCH, "Segoe UI");
                    nint oldF = Gdi32.SelectObject(_memDc, font);
                    Gdi32.SetTextColor(_memDc, Gdi32.Bgr(c.Color));
                    var tr = new RECT { Left = (int)c.Bounds.X, Top = (int)c.Bounds.Y, Right = (int)c.Bounds.Right + 2, Bottom = (int)c.Bounds.Bottom + 2 };
                    Gdi32.DrawTextW(_memDc, text, text.Length, ref tr, Gdi32.DT_LEFT | Gdi32.DT_TOP | Gdi32.DT_SINGLELINE | Gdi32.DT_NOPREFIX | Gdi32.DT_NOCLIP);
                    Gdi32.SelectObject(_memDc, oldF);
                    Gdi32.DeleteObject(font);
                    break;
                }
                default: pos = cmds.Length; break;
            }
        }

        Gdi32.SelectObject(_memDc, oldPen);
    }

    public void Present()
    {
        nint wdc = Gdi32.GetDC(_hwnd);
        Gdi32.BitBlt(wdc, 0, 0, _w, _h, _memDc, 0, 0, Gdi32.SRCCOPY);
        Gdi32.ReleaseDC(_hwnd, wdc);
    }

    public void Dispose()
    {
        if (_bmp != 0) Gdi32.DeleteObject(_bmp);
        if (_memDc != 0) Gdi32.DeleteDC(_memDc);
    }
}

/// <summary>GDI-backed text measurement so the Windows app's layout matches what GDI draws.</summary>
public sealed class GdiFontSystem : IFontSystem
{
    private readonly StringTable _strings;
    private readonly nint _dc;

    public GdiFontSystem(StringTable strings)
    {
        _strings = strings;
        _dc = Gdi32.CreateCompatibleDC(0);
    }

    public TextMetrics Measure(StringId text, in TextStyle style)
    {
        string s = _strings.Resolve(text);
        int height = -(int)MathF.Round(style.SizeDip);
        nint font = Gdi32.CreateFontW(height, 0, 0, 0, style.Bold ? Gdi32.FW_BOLD : Gdi32.FW_NORMAL,
            0, 0, 0, Gdi32.DEFAULT_CHARSET, 0, 0, Gdi32.CLEARTYPE_QUALITY, Gdi32.DEFAULT_PITCH, "Segoe UI");
        nint oldF = Gdi32.SelectObject(_dc, font);
        Gdi32.GetTextExtentPoint32W(_dc, s, s.Length, out SIZE size);
        Gdi32.SelectObject(_dc, oldF);
        Gdi32.DeleteObject(font);
        float w = size.Cx <= 0 ? s.Length * style.SizeDip * 0.55f : size.Cx;
        float h = size.Cy <= 0 ? style.SizeDip * 1.4f : size.Cy;
        return new TextMetrics(new Size2(w, h), h * 0.8f);
    }
}
