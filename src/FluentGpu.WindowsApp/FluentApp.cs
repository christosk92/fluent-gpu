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
using FluentGpu.Scene;
using FluentGpu.Text.DirectWrite;

namespace FluentGpu;

/// <summary>
/// Batteries-included entry point — the whole SDK in one call. <c>FluentApp.Run(() =&gt; new MyApp())</c> creates a
/// DPI-aware window, brings up D3D12, applies Mica + the real system accent, wires the font system + frame loop, and
/// renders your root component. No PAL/RHI/AppHost plumbing to think about — just write components.
/// </summary>
public static class FluentApp
{
    /// <summary>
    /// The live top-level window HWND of the currently-running app, or <see cref="nint.Zero"/> before
    /// <see cref="Run(Func{Component}, string, int, int, bool, int, string?, bool)"/> creates the window (and after it
    /// closes). This is the real <c>FluentGpu</c> window handle — the app-layer accessor that
    /// <c>FluentGpu.WindowsApi</c> consumers (SMTC / file pickers / taskbar) pass as their explicit <c>nint hwnd</c>
    /// parameter, so a UI page never has to invent a handle on the Engine seam. UI-thread only (the value is set on the
    /// thread that pumps the window). Single-window by design; the gallery runs exactly one top-level window.
    /// </summary>
    public static nint WindowHandle { get; private set; }

    /// <summary>
    /// Relay of the host's single-instance activation-redirect event (a second app launch's deep-link payload forwarded
    /// to this running instance). Forwarded from <c>AppHost.ActivationRedirected</c> while a run is active and delivered
    /// on the UI thread, so handlers may write signals that re-render. App-layer relay (not an Engine-seam accessor) so
    /// page/app code can subscribe without holding the <c>AppHost</c> instance.
    /// </summary>
    public static event Action<string>? ActivationRedirected;

    public static void Run(Func<Component> root, string title = "FluentGpu", int width = 800, int height = 600,
                           bool mica = true, int frames = -1, string? screenshot = null, bool customFrame = false)
    {
        bool consoleDiagnostics = Diag.EnvFlag("FG_DIAG") || Diag.EnvFlag("FG_DIAG_CONSOLE");
        if (consoleDiagnostics)
        {
            Diag.Enabled = true;
            Diag.Sink = Console.Error.WriteLine;   // engine diagnostics -> console (Debug/FLUENTGPU_DIAG only)
        }

        var strings = new StringTable();
        using var app = new Win32App();
        // customFrame: the app draws its own WinUI TitleBar (caption stripped, engine caption buttons, snap layouts) —
        // an explicit opt-in (the gallery): apps without a TitleBar keep the standard OS frame.
        var window = (Win32Window)app.CreateWindow(new WindowDesc(title, new Size2(width, height), 1f, mica, CustomFrame: customFrame));
        // Publish the real top-level HWND so app-layer callers (the Windows-APIs page: SMTC / pickers / taskbar) can pass
        // it as their explicit nint hwnd — the host accessor, not an Engine-seam invention. Cleared when the run ends.
        WindowHandle = window.Handle.Value;

        if (Win32Theme.AccentLight2() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);
        else if (Win32Theme.Accent() is { } b) Theme.Accent = ColorF.FromRgba(b.R, b.G, b.B);
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, mica, customFrame);
        if (mica) Theme.WindowBackground = ColorF.Transparent;

        // Text measurement runs through DirectWrite (the same design advances + line-break math the D3D12 GlyphRenderer
        // uses to render), so measured wrap/height matches rendered wrap/height exactly. (GDI measure is retired here.)
        var fonts = new DirectWriteFontSystem(strings);
        IGpuDevice device = new D3D12Device(strings, composited: mica);

        // Real image pipeline: WIC constrained decode on a worker pool, behind a disk-cached HTTP/2 fetcher.
        using var imageFetcher = new DefaultImageFetcher(diskCache: new DiskImageCache());
        using var imageDecoder = new DecodeScheduler(new WicImageCodec(), imageFetcher);
        var images = new ImageCache(imageDecoder);

        using var host = new AppHost(app, window, device, fonts, strings, root(), images);
        host.SmoothScroll = true;   // inertial wheel scrolling + auto-hiding scrollbars (the real-app default)

        // Relay the host's UI-thread single-instance redirect to the app-layer static event (the Windows-APIs page
        // subscribes there). Forwarding the payload, not the handler chain — handlers attach to FluentApp.ActivationRedirected.
        Action<string> forwardActivation = uri => ActivationRedirected?.Invoke(uri);
        host.ActivationRedirected += forwardActivation;

        // FG_ALLOC_TYPES=1: bring up the per-type allocation profiler (process-global EventListener; the host drives
        // its once-per-second report on the frame cadence). Stopped in the finally so headless/short runs don't leak it.
        bool allocTypes = Diag.EnvFlag("FG_ALLOC_TYPES");
        if (allocTypes) AllocTypeProfiler.Start();

        // FG_MEM_DIAG=1 GPU residency hooks: surface tracked D3D12 resource totals + a glyph/texture-store summary
        // (no-op unless the census is also enabled; headless devices leave these null).
        if (device is D3D12Device gpu)
        {
            host.GpuResources = () => gpu.DiagResourceTotals;
            host.GpuDetail = () => gpu.DiagGpuDetail;
        }

        window.Show();

        // Longevity / leak-hunt harness (SoakProbe). Each mode runs instead of the interactive loop and returns to the
        // clean shutdown below. Pair with FG_D3D_MEM=1 for the per-resource [d3d-mem] create/release trace.
        if (device is D3D12Device probeGpu)
        {
            if (Diag.EnvFlag("FG_SOAK"))          { SoakProbe.RunSoak(host, window, probeGpu);  WindowHandle = 0; return; }
            if (Diag.EnvFlag("FG_STRESS_RESIZE")) { SoakProbe.RunResize(host, window, probeGpu); WindowHandle = 0; return; }
            if (Diag.EnvFlag("FG_STRESS_NAV"))    { SoakProbe.RunNav(host, window, probeGpu);    WindowHandle = 0; return; }
            if (Diag.EnvFlag("FG_WAKE_AUDIT"))    { SoakProbe.RunWakeAudit(host, window, probeGpu); WindowHandle = 0; return; }
        }

        int n = 0;
        while (!window.IsClosed)
        {
            host.RunFrame();
            n++;
            if (frames > 0 && n >= frames) break;
            if (screenshot != null)
                window.WaitForWork(8);   // deterministic ~8ms/frame so time-driven animations advance (and never block)
            else
                // Low-rate wake pacing: idle/minimized block until a message (0% CPU); a HUD-only readout throttles to
                // ~10 Hz; real animation/scroll/decode paces at the display rate. WaitForWork returns early on input,
                // so responsiveness is identical at every timeout. (See AppHost.RecommendedWaitMs.)
                window.WaitForWork(host.RecommendedWaitMs());
        }

        if (allocTypes) AllocTypeProfiler.Stop();   // tear down the EventListener (no leak past the run)

        // --screenshot: read the last-rendered back buffer back to CPU and write a PNG for visual fidelity diffing.
        if (screenshot != null && device is D3D12Device d3d)
        {
            var px = d3d.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(screenshot, px, cw, ch);
            Console.Error.WriteLine($"screenshot: wrote {screenshot} ({cw}x{ch})");
        }

        WindowHandle = 0;   // the window is gone; don't leave a stale handle for a late SMTC/picker call.
    }

    /// <summary><c>FluentApp.Run&lt;MyApp&gt;()</c> — same, for a parameterless root component.</summary>
    public static void Run<T>(string title = "FluentGpu", int width = 800, int height = 600) where T : Component, new()
        => Run(() => new T(), title, width, height);
}
