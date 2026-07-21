using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Media.Codecs.Wic;
using FluentGpu.Media.Windows;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using FluentGpu.Text.DirectWrite;

namespace FluentGpu;

/// <summary>
/// M3 — the "real unprotected video" milestone of the DRM-free video compositing spine
/// (<c>docs/plans/video-compositing-spine-design.md</c>). Drives <see cref="VideoMediaEngine"/>
/// (<c>IMFMediaEngineEx</c> in windowless swap-chain mode) to decode a CLEAR progressive MP4 by URL, hands its
/// DirectComposition swap-chain HANDLE to the same <see cref="IVideoPresenter"/> M0 proved (bind →
/// <c>CreateSurfaceFromHandle</c> → <c>SetContent</c>), positions the video child z-BELOW the UI in a transparent hole,
/// runs until frames present, and screen-captures the DWM composite to a PNG.
///
/// Same "screen capture, not back buffer" reason as M0: the video is a sibling DComp visual composited by DWM, never in
/// our swapchain back buffer. No PlayReady / no protected content — this is the exact path a CDM will later reuse.
/// </summary>
static class VideoReal
{
    // A stable CLEAR progressive H.264 MP4 IMFMediaEngine can resolve by URL (NOT DASH/Smooth). Override via FG_VIDEO_URL.
    const string DefaultUrl = "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4";

    static readonly ColorF TopBar = ColorF.FromRgba(0x18, 0x1B, 0x22);   // opaque chrome bar → proves UI-over-video composite

    public static int Run(string pngPath, int frames)
    {
        const int W = 1000, H = 700;
        string url = Environment.GetEnvironmentVariable("FG_VIDEO_URL") is { Length: > 0 } e ? e : DefaultUrl;

        var strings = new StringTable();
        using var app = new Win32App();
        var window = (Win32Window)app.CreateWindow(new WindowDesc("FluentGpu — Real Video (M3)", new Size2(W, H), 1f, Composited: true));
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, mica: true, customFrame: false, micaAlt: false);
        Theme.WindowBackground = ColorF.Transparent;

        var fonts = new DirectWriteFontSystem(strings);
        var device = new D3D12Device(strings, composited: true);
        using var imageFetcher = new DefaultImageFetcher(diskCache: new DiskImageCache());
        var pixelPool = new PixelBufferPool();
        using var imageDecoder = new DecodeScheduler(new WicImageCodec(), imageFetcher, new DecodeOptions { PixelPool = pixelPool });
        var images = new ImageCache(imageDecoder, 64L * 1024 * 1024);

        using var host = new AppHost(app, window, device, fonts, strings, new VideoRealScene(), images);
        host.PixelPool = pixelPool;
        window.Show();

        // Warm up: BindDComp binds the (restructured) present tree on the first Present; a few frames settle layout.
        for (int i = 0; i < 6 && !window.IsClosed; i++) { host.RunFrame(); window.WaitForWork(8); }

        var presenter = device.GetVideoPresenter();
        if (presenter is null)
        {
            Console.Error.WriteLine("VideoReal: no composited primary swapchain — cannot present a video child.");
            return 2;
        }

        Console.Error.WriteLine($"VideoReal: playing {url}");
        using var engine = new VideoMediaEngine();
        int initHr = engine.Initialize(url);
        if (initHr < 0)
        {
            Console.Error.WriteLine($"VideoReal: engine init failed hr=0x{(uint)initHr:X8} — aborting.");
            return 3;
        }

        float scale = VideoM0.GetDpiForWindow(window.Handle.Value) / 96f;
        if (scale <= 0f) scale = 1f;
        int barPx = (int)MathF.Round(54f * scale);

        VideoSurfaceId id = default;
        bool bound = false;
        bool sawTick = false;
        int childW = 0, childH = 0, cx = 0, cy = 0;

        // ── Phase 1: pump until metadata + bind, or error/timeout ──────────────────────────────────────────────────
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var nextTrace = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!window.IsClosed && DateTime.UtcNow < deadline)
        {
            host.RunFrame();
            window.WaitForWork(16);

            if (DateTime.UtcNow >= nextTrace)
            {
                Console.Error.WriteLine($"VideoReal: [t] readyState={engine.ReadyState} events=[{engine.EventTrace}]");
                nextTrace = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }

            if (engine.HasError)
            {
                Console.Error.WriteLine($"VideoReal: MediaEngine ERROR code={engine.ErrorCode} hr=0x{(uint)engine.ErrorHr:X8} (last event {engine.LastEventName}).");
                break;
            }
            if (bound || !engine.MetadataLoaded) continue;

            // Geometry: fit the native video into the window (below the top bar), preserving aspect, device px.
            uint vw = 1280, vh = 720;
            engine.TryGetNativeVideoSize(out vw, out vh);
            if (vw == 0 || vh == 0) { vw = 1280; vh = 720; }
            var px = device.SizePx;
            int wpx = (int)px.Width, hpx = (int)px.Height;
            int margin = (int)MathF.Round(40f * scale);
            int availW = Math.Max(16, wpx - 2 * margin);
            int availH = Math.Max(16, hpx - barPx - 2 * margin);
            float ar = (float)vw / vh;
            childW = availW; childH = (int)MathF.Round(childW / ar);
            if (childH > availH) { childH = availH; childW = (int)MathF.Round(childH * ar); }
            cx = (wpx - childW) / 2;
            cy = barPx + (hpx - barPx - childH) / 2;

            nuint handle = engine.GetSwapchainHandle();
            if (handle == 0) { Console.Error.WriteLine("VideoReal: swapchain handle not ready yet — retrying."); continue; }

            // The video spine end-to-end: MediaEngine windowless swapchain handle → BindSurfaceHandle → Place under the hole.
            id = presenter.CreateSurface();
            presenter.BindSurfaceHandle(id, handle);
            presenter.Place(id, new RectF(cx, cy, childW, childH), 1f, 0);
            presenter.SetVisible(id, true);
            presenter.Commit();
            engine.SetVideoStreamRect(childW, childH);   // UpdateVideoStream(null, {0,0,w,h}, border)
            bound = true;
            Console.Error.WriteLine($"VideoReal: BOUND video {vw}x{vh} -> child rect ({cx},{cy},{childW},{childH}); scale={scale:0.##}");
        }

        if (!bound)
        {
            Console.Error.WriteLine($"VideoReal: never bound a swapchain (metadata={engine.MetadataLoaded}, error={engine.HasError}, last={engine.LastEventName}). No real frame — FAIL.");
        }
        else
        {
            // ── Phase 2: let the engine decode & auto-present; force repaint of the latest frame each turn ─────────
            int settle = frames > 0 ? frames : 120;
            var bindTime = DateTime.UtcNow;
            for (int i = 0; i < settle && !window.IsClosed; i++)
            {
                host.RunFrame();
                window.WaitForWork(16);
                if (engine.OnVideoStreamTick(out _)) sawTick = true;
                engine.RepaintCurrentFrame();
                presenter.Commit();
                // Stop once we're genuinely playing, have seen a decoded frame, and given it ~1.5s to render.
                if (engine.Playing && sawTick && (DateTime.UtcNow - bindTime) > TimeSpan.FromSeconds(1.5) && i > 30)
                    break;
            }
            Console.Error.WriteLine($"VideoReal: state playing={engine.Playing} canplay={engine.CanPlay} readyState={engine.ReadyState} newFrameTick={sawTick} last={engine.LastEventName}");
        }
        Console.Error.WriteLine($"VideoReal: full event trace = [{engine.EventTrace}]");

        // Bring our window to the foreground + topmost so the screen BitBlt captures IT (the DComp video child is
        // DWM-composited on screen; a screen capture of the window rect grabs whatever is on top, so the window must be
        // frontmost). Then capture the DWM composite (includes the DComp video child).
        BringToFront(window.Handle.Value);
        for (int i = 0; i < 8; i++) { host.RunFrame(); window.WaitForWork(16); engine.RepaintCurrentFrame(); presenter.Commit(); }
        bool ok = VideoM0.CaptureWindowToPng(window.Handle.Value, pngPath);
        Console.Error.WriteLine(ok ? $"VideoReal: wrote screen capture -> {pngPath}" : "VideoReal: screen capture FAILED.");

        bool realFrame = bound && sawTick && engine.Playing && !engine.HasError;
        Console.Error.WriteLine(realFrame
            ? "VideoReal: RESULT = a decoded frame presented (playing + video-stream-tick). Inspect the PNG for a recognizable frame."
            : "VideoReal: RESULT = NO confirmed decoded frame (see state above) — treat the PNG as SUSPECT/BLACK, not success.");

        if (bound) { presenter.Destroy(id); presenter.Commit(); }
        return ok && realFrame ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetForegroundWindow(nint hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowWindow(nint hWnd, int nCmdShow);

    /// <summary>Force the window frontmost + topmost so a screen-region capture grabs it (not whatever else is on top).</summary>
    private static void BringToFront(nint hwnd)
    {
        const int SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
        nint HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
        ShowWindow(hwnd, 5 /*SW_SHOW*/);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }
}

/// <summary>The UI back-buffer scene for M3: a transparent full-bleed column with an opaque top chrome bar. The center
/// stays transparent — the natural "hole" the z-below video child shows through (UI-over-video composite proof).</summary>
sealed class VideoRealScene : Component
{
    public override Element Render() => new BoxEl
    {
        Grow = 1,
        Direction = 1,   // column: top bar, then transparent fill
        Children =
        [
            new BoxEl { Height = 54f, Fill = VideoRealBar },   // opaque chrome (composites over Mica + over the video's top edge if any)
            new BoxEl { Grow = 1 },                            // transparent — the video shows through here (z-below)
        ],
    };

    static readonly ColorF VideoRealBar = ColorF.FromRgba(0x18, 0x1B, 0x22);
}
