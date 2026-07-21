using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Media.Codecs.Wic;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using FluentGpu.Text.DirectWrite;

namespace FluentGpu;

/// <summary>
/// M0 of the DRM-free video compositing spine (docs/plans/video-phase1-plan.md §4). Stands up the composited
/// (Mica) window, restructures the DirectComposition tree (root → UI on top + one video child below), binds an
/// ENGINE-OWNED test surface through the real handle path (<c>DCompositionCreateSurfaceHandle</c> →
/// <c>CreateSurfaceFromHandle</c> → <c>SetContent</c>), punches a graded hole in the UI back buffer (a poster box at
/// opacity <c>1−VideoReady</c> over the transparent Mica background), and captures the DWM-COMPOSITED result to a PNG.
///
/// Geometry (concentric, so the layering is unmistakable): a centered video child (magenta/cyan split test pattern)
/// with a 70-DIP ring of raw pattern around a centered poster box. The poster region shows the graded blend
/// (<c>poster·(1−w) + testpattern·w</c> at <c>w = VideoReady = 0.5</c>); the ring shows the raw child; outside the
/// child the transparent window reveals Mica.
///
/// Why a SCREEN capture, not the back buffer: the video child is a sibling DComp visual composited by DWM — it is
/// NOT in our swapchain back buffer, so <c>D3D12Device.CaptureBgra</c> (the normal <c>--screenshot</c> path) cannot
/// see it. Only a screen-level GDI BitBlt of the window sees the true composited pixels.
/// </summary>
static class VideoM0
{
    // Poster box in DIPs (the graded layer); a ring of raw pattern around it in the video child.
    const float PosterDipW = 360f, PosterDipH = 260f, RingDip = 70f;
    const float VideoReady = 0.5f;

    // A distinctly non-magenta/non-cyan poster so the blend colour is obviously "both layers": yellow.
    static readonly ColorF Poster = ColorF.FromRgba(0xF2, 0xC5, 0x11);

    public static int Run(string pngPath, int frames)
    {
        if (frames <= 0) frames = 8;
        const int W = 900, H = 640;

        var strings = new StringTable();
        using var app = new Win32App();
        var window = (Win32Window)app.CreateWindow(new WindowDesc("FluentGpu — Video M0", new Size2(W, H), 1f, Composited: true));
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, mica: true, customFrame: false, micaAlt: false);
        Theme.WindowBackground = ColorF.Transparent;

        var fonts = new DirectWriteFontSystem(strings);
        var device = new D3D12Device(strings, composited: true);
        using var imageFetcher = new DefaultImageFetcher(diskCache: new DiskImageCache());
        var pixelPool = new PixelBufferPool();
        using var imageDecoder = new DecodeScheduler(new WicImageCodec(), imageFetcher, new DecodeOptions { PixelPool = pixelPool });
        var images = new ImageCache(imageDecoder, 64L * 1024 * 1024);

        using var host = new AppHost(app, window, device, fonts, strings, new VideoM0Scene(PosterDipW, PosterDipH, Poster, VideoReady), images);
        host.PixelPool = pixelPool;
        window.Show();

        // Warm up: BindDComp binds the (restructured) present tree on the first Present; a few frames settle layout.
        for (int i = 0; i < 6 && !window.IsClosed; i++) { host.RunFrame(); window.WaitForWork(8); }

        // Concentric device-px geometry (single-threaded: this thread is the render thread, quarantine 0).
        var px = device.SizePx;
        int wpx = (int)px.Width, hpx = (int)px.Height;
        float scale = GetDpiForWindow(window.Handle.Value) / 96f;
        if (scale <= 0f) scale = 1f;
        int childW = (int)MathF.Round((PosterDipW + 2 * RingDip) * scale);
        int childH = (int)MathF.Round((PosterDipH + 2 * RingDip) * scale);
        int cx = (wpx - childW) / 2;
        int cy = (hpx - childH) / 2;

        // The video spine end-to-end: engine-owned shareable surface → BindSurfaceHandle → Place under the hole.
        var presenter = device.GetVideoPresenter();
        if (presenter is null)
        {
            Console.Error.WriteLine("VideoM0: no composited primary swapchain — cannot present a video child.");
            return 2;
        }
        nuint handle = device.CreateEngineTestSurfaceHandle((uint)childW, (uint)childH);
        VideoSurfaceId id = presenter.CreateSurface();
        presenter.BindSurfaceHandle(id, handle);
        presenter.Place(id, new RectF(cx, cy, childW, childH), 1f, 0);
        presenter.SetVisible(id, true);
        presenter.Commit();
        Console.Error.WriteLine($"VideoM0: child placed at device rect ({cx},{cy},{childW},{childH}); scale={scale:0.##}; VideoReady={VideoReady}");

        // Render more frames so the composited result stabilises on screen before the capture.
        for (int i = 0; i < frames && !window.IsClosed; i++) { host.RunFrame(); window.WaitForWork(16); }

        bool ok = CaptureWindowToPng(window.Handle.Value, pngPath);
        Console.Error.WriteLine(ok
            ? $"VideoM0: wrote composited screen capture -> {pngPath}"
            : "VideoM0: screen capture FAILED (window not visible / non-composited session?).");

        // Also dump the back buffer (proves the hole/poster layer; the child is expectedly absent from it).
        try
        {
            var bb = device.CaptureBgra(out int bw, out int bh);
            string bbPath = System.IO.Path.ChangeExtension(pngPath, null) + "-backbuffer.png";
            FluentGpu.Foundation.PngWriter.WriteBgra(bbPath, bb, bw, bh);
            Console.Error.WriteLine($"VideoM0: wrote UI back-buffer (child absent by design) -> {bbPath}");
        }
        catch (Exception e) { Console.Error.WriteLine($"VideoM0: back-buffer dump skipped: {e.Message}"); }

        presenter.Destroy(id);
        presenter.Commit();
        return ok ? 0 : 1;
    }

    // ── GDI screen capture of the window's client area (captures the true DWM composite, incl. the DComp child) ──
    internal static bool CaptureWindowToPng(nint hwnd, string path)
    {
        if (!GetClientRect(hwnd, out RECT rc)) return false;
        int w = rc.right - rc.left, h = rc.bottom - rc.top;
        if (w <= 0 || h <= 0) return false;
        POINT tl = default;
        if (!ClientToScreen(hwnd, ref tl)) return false;

        nint screen = GetDC(0);
        if (screen == 0) return false;
        nint mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,   // top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,   // BI_RGB
        };
        nint dib = CreateDIBSection(screen, ref bmi, 0 /*DIB_RGB_COLORS*/, out nint bits, 0, 0);
        bool ok = false;
        if (dib != 0 && bits != 0)
        {
            nint old = SelectObject(mem, dib);
            const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
            ok = BitBlt(mem, 0, 0, w, h, screen, tl.x, tl.y, SRCCOPY | CAPTUREBLT);
            if (ok)
            {
                var buf = new byte[w * h * 4];
                Marshal.Copy(bits, buf, 0, buf.Length);
                for (int i = 3; i < buf.Length; i += 4) buf[i] = 0xFF;   // force opaque alpha (BI_RGB leaves it undefined)
                FluentGpu.Foundation.PngWriter.WriteBgra(path, buf, w, h);
            }
            SelectObject(mem, old);
            DeleteObject(dib);
        }
        DeleteDC(mem);
        ReleaseDC(0, screen);
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; public uint rgb0; }

    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetClientRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ClientToScreen(nint hwnd, ref POINT p);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BITMAPINFO bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int rop);
}

/// <summary>The UI back-buffer scene for M0: a transparent full-bleed page with a centered poster box drawn at group
/// opacity <c>1−VideoReady</c> — the graded hole layer over the (transparent) Mica background.</summary>
sealed class VideoM0Scene : Component
{
    private readonly float _posterW, _posterH;
    private readonly ColorF _poster;
    private readonly float _videoReady;

    public VideoM0Scene(float posterW, float posterH, ColorF poster, float videoReady)
    { _posterW = posterW; _posterH = posterH; _poster = poster; _videoReady = videoReady; }

    public override Element Render() => new BoxEl
    {
        Grow = 1,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Children =
        [
            // Poster at opacity (1 − VideoReady), a real premultiplied group ⇒ back-buffer pixel = poster·(1−w), α=(1−w).
            // DWM over the video child ⇒ poster·(1−w) + child·w — the graded reveal, no shader, no black frame.
            new BoxEl
            {
                Width = _posterW,
                Height = _posterH,
                Fill = _poster,
                Opacity = 1f - _videoReady,
                OpacityGroup = true,
            },
        ],
    };
}
