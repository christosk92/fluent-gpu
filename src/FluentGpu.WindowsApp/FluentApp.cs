using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Media.Codecs.Wic;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Rhi.Gdi;
using FluentGpu.Scene;

namespace FluentGpu;

/// <summary>
/// Batteries-included entry point — the whole SDK in one call. <c>FluentApp.Run(() =&gt; new MyApp())</c> creates a
/// DPI-aware window, brings up D3D12, applies Mica + the real system accent, wires the font system + frame loop, and
/// renders your root component. No PAL/RHI/AppHost plumbing to think about — just write components.
/// </summary>
public static class FluentApp
{
    public static void Run(Func<Component> root, string title = "FluentGpu", int width = 800, int height = 600,
                           bool mica = true, int frames = -1, string? screenshot = null)
    {
        bool consoleDiagnostics = Diag.EnvFlag("FG_DIAG") || Diag.EnvFlag("FG_DIAG_CONSOLE");
        if (consoleDiagnostics)
        {
            Diag.Enabled = true;
            Diag.Sink = Console.Error.WriteLine;   // engine diagnostics -> console (Debug/FLUENTGPU_DIAG only)
        }

        var strings = new StringTable();
        using var app = new Win32App();
        var window = (Win32Window)app.CreateWindow(new WindowDesc(title, new Size2(width, height), 1f, mica));

        if (Win32Theme.AccentLight2() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);
        else if (Win32Theme.Accent() is { } b) Theme.Accent = ColorF.FromRgba(b.R, b.G, b.B);
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, mica);
        if (mica) Theme.WindowBackground = ColorF.Transparent;

        var fonts = new GdiFontSystem(strings);
        IGpuDevice device = new D3D12Device(strings, composited: mica);

        // Real image pipeline: WIC constrained decode on a worker pool, behind a disk-cached HTTP/2 fetcher.
        using var imageFetcher = new DefaultImageFetcher(diskCache: new DiskImageCache());
        using var imageDecoder = new DecodeScheduler(new WicImageCodec(), imageFetcher);
        var images = new ImageCache(imageDecoder);

        using var host = new AppHost(app, window, device, fonts, strings, root(), images);
        host.SmoothScroll = true;   // inertial wheel scrolling + auto-hiding scrollbars (the real-app default)

        window.Show();

        int n = 0;
        while (!window.IsClosed)
        {
            host.RunFrame();
            n++;
            if (frames > 0 && n >= frames) break;
            // Active frames are paced by the swapchain present path; an extra timed wait here skews animation and FPS diagnostics.
            window.WaitForWork(host.HasActiveWork ? 0 : -1);
        }

        // --screenshot: read the last-rendered back buffer back to CPU and write a PNG for visual fidelity diffing.
        if (screenshot != null && device is D3D12Device d3d)
        {
            var px = d3d.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(screenshot, px, cw, ch);
            Console.Error.WriteLine($"screenshot: wrote {screenshot} ({cw}x{ch})");
        }
    }

    /// <summary><c>FluentApp.Run&lt;MyApp&gt;()</c> — same, for a parameterless root component.</summary>
    public static void Run<T>(string title = "FluentGpu", int width = 800, int height = 600) where T : Component, new()
        => Run(() => new T(), title, width, height);
}
