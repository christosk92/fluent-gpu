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
    /// <see cref="Run(Func{Component}, AppOptions?)"/> creates the window (and after it
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

    /// <summary>
    /// Relay of the host's OS color-settings-change event (Windows app dark/light flip or accent change), delivered on the
    /// UI thread at the top of a frame. App-layer relay (not an Engine-seam accessor) so page/app code can react —
    /// typically re-reading <see cref="SystemIsDark"/> while it follows the OS — without holding the <c>AppHost</c>.
    /// </summary>
    public static event Action? SystemColorsChanged;

    /// <summary>True when the OS "app" theme is Light (Settings ▸ Colors). The app-layer facade over the Win32 reader so
    /// composition-root code (e.g. seeding the initial theme from a "System" preference) stays free of PAL imports.
    /// Defaults to FALSE (dark) when unreadable, matching the engine default.</summary>
    public static bool SystemUsesLightTheme() => Win32Theme.SystemUsesLightTheme();

    /// <summary>The current OS accent color (Settings ▸ Colors), preferring the <c>Light2</c> shade WinUI uses for the
    /// dark-theme accent fill, else the base accent; null when unreadable. The app-layer facade over the Win32 reader.</summary>
    public static ColorF? SystemAccent()
        => Win32Theme.AccentLight2() is { } a ? ColorF.FromRgba(a.R, a.G, a.B)
         : Win32Theme.Accent() is { } b ? ColorF.FromRgba(b.R, b.G, b.B)
         : null;

    /// <summary>The FULL OS accent ramp (<c>SystemAccentColor</c> + <c>Light1..3</c> + <c>Dark1..3</c>) read via
    /// <c>IUISettings3.GetColorValue</c>, so accent brushes resolve THEME-AWARE (the WinUI Dark1 shade in light, Light2
    /// in dark) instead of one flat color reused in both themes. Null when unreadable — callers then fall back to
    /// <see cref="SystemAccent"/> + <c>Tok.SetAccent</c> (which derives an approximate ramp). App-layer facade over the
    /// Win32/WinRT reader.</summary>
    public static AccentRamp? SystemAccentRamp() => Win32Theme.ReadAccentRamp();

    /// <summary>
    /// Optional diagnostic-harness hook. When it is set and returns <see langword="true"/>, it has taken over the run
    /// (e.g. a soak / stress longevity probe) and the normal interactive frame loop below is skipped. Kept as a generic
    /// seam so the engine entry point carries no dependency on any app-specific harness: the gallery installs its
    /// <c>SoakProbe</c> here, gated on its <c>FG_SOAK</c> / <c>FG_STRESS_*</c> env flags. Left <see langword="null"/>
    /// for normal apps. UI-thread only.
    /// </summary>
    public static Func<AppHost, IPlatformWindow, IGpuDevice, bool>? DiagnosticRun;

    /// <summary>Run the app: create a DPI-aware window, bring up D3D12 + Mica + the real OS accent, wire the font system
    /// and frame loop, and render <paramref name="root"/>. Pass <paramref name="options"/> to set the window title/size,
    /// Mica variant, custom frame, ambient-fps throttle, and warm-cadence hold; omit it for the defaults.</summary>
    public static void Run(Func<Component> root, AppOptions? options = null)
        => RunCore(root, options ?? new AppOptions(), new HarnessOptions());

    /// <summary><c>FluentApp.Run&lt;MyApp&gt;()</c> — same, for a parameterless root component.</summary>
    public static void Run<T>(AppOptions? options = null) where T : Component, new()
        => Run(() => new T(), options);

    // The single implementation. Public entry points (Run) and the diagnostic harness (FluentAppHarness.Run) both route
    // here; splitting the interactive options (AppOptions) from the test/diagnostic knobs (HarnessOptions: frames,
    // screenshot, frame-wait) keeps the everyday surface a one-liner while the harness owns the deterministic controls.
    internal static void RunCore(Func<Component> root, AppOptions o, HarnessOptions h)
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--audio-host") >= 0)
            throw new InvalidOperationException("FluentApp.Run must not be reached in --audio-host child mode.");

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
        var window = (Win32Window)app.CreateWindow(new WindowDesc(o.Title, new Size2(o.Width, o.Height), 1f, o.Mica, CustomFrame: o.CustomFrame));
        // Publish the real top-level HWND so app-layer callers (the Windows-APIs page: SMTC / pickers / taskbar) can pass
        // it as their explicit nint hwnd — the host accessor, not an Engine-seam invention. Cleared when the run ends.
        WindowHandle = window.Handle.Value;

        // Prefer the exact OS ramp (theme-aware accent fills); fall back to the base accent (Tok.SetAccent derives a ramp).
        if (Win32Theme.ReadAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
        else if (Win32Theme.AccentLight2() is { } a) Theme.Accent = ColorF.FromRgba(a.R, a.G, a.B);
        else if (Win32Theme.Accent() is { } b) Theme.Accent = ColorF.FromRgba(b.R, b.G, b.B);
        Win32Theme.ApplyWindowMaterial(window.Handle.Value, Theme.Dark, o.Mica, o.CustomFrame, o.MicaAlt);
        if (o.Mica) Theme.WindowBackground = ColorF.Transparent;

        // Text measurement runs through DirectWrite (the same design advances + line-break math the D3D12 GlyphRenderer
        // uses to render), so measured wrap/height matches rendered wrap/height exactly. (GDI measure is retired here.)
        var fonts = new DirectWriteFontSystem(strings);
        IGpuDevice device = new D3D12Device(strings, composited: o.Mica);

        // Real image pipeline: WIC constrained decode on a worker pool, behind a disk-cached HTTP/2 fetcher.
        using var imageFetcher = new DefaultImageFetcher(diskCache: new DiskImageCache());
        // ONE bounded CPU pixel pool for the whole pipeline: decode BGRA buffers (workers) + async-upload copies (UI)
        // share the DefaultRetainedCapBytes budget (media-pipeline.md §3 staging blocks, as built).
        var pixelPool = new PixelBufferPool();
        using var imageDecoder = new DecodeScheduler(new WicImageCodec(), imageFetcher,
            new DecodeOptions { PixelPool = pixelPool });
        var images = new ImageCache(imageDecoder, ImageCacheBudgetBytes());

        using var host = new AppHost(app, window, device, fonts, strings, root(), images);
        host.PixelPool = pixelPool;   // before the first RunFrame
        host.SmoothScroll = true;   // inertial wheel scrolling + auto-hiding scrollbars (the real-app default)
        // App-set ambient power throttle (>0): pace perpetual loop animation (spinner/shimmer/equalizer/media-playhead) to
        // this rate so a never-idling app (one with always-on ambient motion) doesn't free-run the whole render+present
        // pipeline at the panel refresh. A live FG_ANIM_FPS env var still wins (the host seeded its default from it), so
        // the diagnostic override (incl. =0 to A/B uncapped) is preserved; 0 here = leave the host default untouched.
        if (o.AmbientFps > 0 && Environment.GetEnvironmentVariable("FG_ANIM_FPS") is null)
            host.AmbientAnimationFps = o.AmbientFps;
        // Post-input warm-cadence hold (G1b): keep rendering ~WarmCadenceMs after the last input so a follow-up
        // interaction pays no cold-start ramp. 0 disables the hold (see AppHost.WarmCadenceHoldMs).
        host.WarmCadenceHoldMs = o.WarmCadenceMs;

        // Relay the host's UI-thread single-instance redirect to the app-layer static event (the Windows-APIs page
        // subscribes there). Forwarding the payload, not the handler chain — handlers attach to FluentApp.ActivationRedirected.
        Action<string> forwardActivation = uri => ActivationRedirected?.Invoke(uri);
        host.ActivationRedirected += forwardActivation;

        // Live re-theme: on every theme change the host re-applies the OS window material so DWM's immersive-dark titlebar
        // and the Mica system backdrop flip to the new theme's variant (instant — the OS can't cross-fade its backdrop;
        // the in-app content cross-fades). Mirrors the one-shot startup ApplyWindowMaterial above.
        host.OnApplyThemeMaterial = dark => Win32Theme.ApplyWindowMaterial(window.Handle.Value, dark, o.Mica, o.CustomFrame, o.MicaAlt);
        // Relay the host's UI-thread OS color-settings-change to the app-layer static event (the app subscribes to follow
        // the OS dark-mode/accent live while its theme mode is "System").
        Action forwardSystemColors = () => SystemColorsChanged?.Invoke();
        host.SystemColorsChanged += forwardSystemColors;

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

        // Optional diagnostic-harness takeover (the gallery's SoakProbe longevity / leak-hunt + targeted-stress modes,
        // gated on FG_SOAK / FG_STRESS_* / FG_WAKE_AUDIT). Installed via FluentApp.DiagnosticRun; when it handles the
        // run it returns true and we skip the interactive loop, returning to the clean shutdown below. Null for normal
        // apps. Pair with FG_D3D_MEM=1 for the per-resource [d3d-mem] create/release trace.
        if (DiagnosticRun is { } diag && diag(host, window, device)) { WindowHandle = 0; return; }

        bool fpsLog = Diag.EnvFlag("FG_FPS_LOG");   // periodic [fps] readout to stderr (frame-rate / frame-ms diagnosis)
        int n = 0;
        // Render-side pacing signal (async): FrameMs times only the UI thread, which under async EXCLUDES submit/present/
        // the GPU fence-wait (off-thread). So also report the render thread's ACTUAL present cadence (PresentAck delta /
        // wall-time = real on-screen fps) + its last GPU fence-wait. present << ui-fps ⇒ GPU-bound (the real bottleneck).
        ulong lastPresentSeq = host.RenderPresentSeq;
        long lastPresentTick = System.Diagnostics.Stopwatch.GetTimestamp();
        // Present-path diagnosis (maximize → 60fps): watch the swapchain size + window state so a resize emits a one-shot
        // [fps resize] marker (WxH, scale, state, panel Hz, wait-kind), and every [fps] line carries the wait-kind/ms the
        // loop paced by (Ambient = software 60 cap; DisplayRate/Pace = panel rate → a lock is downstream in Present/GPU).
        float lastLoggedW = -1f, lastLoggedH = -1f;
        int cachedHz = fpsLog ? window.CurrentRefreshHz() : 0;
        static string WaitTok(FluentGpu.Hosting.HostWaitKind k) => k switch
        {
            FluentGpu.Hosting.HostWaitKind.Idle => "idle",
            FluentGpu.Hosting.HostWaitKind.Hud => "hud",
            FluentGpu.Hosting.HostWaitKind.Baked => "baked",
            FluentGpu.Hosting.HostWaitKind.Ambient => "ambient",
            FluentGpu.Hosting.HostWaitKind.PaceSkipSubmit => "pace-skip",
            FluentGpu.Hosting.HostWaitKind.PaceAsync => "pace-async",
            FluentGpu.Hosting.HostWaitKind.DisplayRate => "display",
            _ => "?",
        };
        while (!window.IsClosed)
        {
            host.RunFrame();
            host.TickDetachedHosts();   // pop-out video windows: one frame each on this same UI+render thread
            n++;
            if (fpsLog)
            {
                var s = host.LastStats;
                double gpuMs = host.LastGpuFenceWaitMs;
                var szpx = window.ClientSizePx;
                if (szpx.Width != lastLoggedW || szpx.Height != lastLoggedH)
                {
                    lastLoggedW = szpx.Width; lastLoggedH = szpx.Height;
                    cachedHz = window.CurrentRefreshHz();   // once per size change, not per frame
                    Console.Error.WriteLine($"[fps resize] {szpx.Width}x{szpx.Height} scale {window.Scale:0.##} state {window.State} panel {cachedHz}Hz wait {WaitTok(host.LastWaitKind)}{host.LastWaitMs} (f{n})");
                }
                bool workSpike = (s.FlushMs + s.LayoutMs + s.RecordMs) > 11.0;
                bool spike = workSpike || gpuMs > 11.0;   // UI work (not bare submit pacing) OR render-thread GPU stall
                if (spike || n % 30 == 0)
                {
                    ulong curSeq = host.RenderPresentSeq;
                    long nowT = System.Diagnostics.Stopwatch.GetTimestamp();
                    double presentFps = curSeq > lastPresentSeq && nowT > lastPresentTick
                        ? (curSeq - lastPresentSeq) * (double)System.Diagnostics.Stopwatch.Frequency / (nowT - lastPresentTick) : 0;
                    lastPresentSeq = curSeq; lastPresentTick = nowT;
                    double gpuRenderMs = host.LastGpuRenderMs;   // FG_GPU_TIMING: true raster ms (0 when off) — disambiguates the fence wait
                    string gpuRenderTok = gpuRenderMs > 0.0 ? $" grender {gpuRenderMs:0.0}ms(scene {host.LastGpuSceneMs:0.0})" : "";
                    Console.Error.WriteLine($"[fps]{(spike ? " SPIKE" : "")} ui {s.Fps:0}fps {s.FrameMs:0.0}ms (flush{s.FlushMs:0.0} rx{s.ReactiveFlushMs:0.0}/vr{s.VirtualRealizeMs:0.0} layout{s.LayoutMs:0.0} anim{s.AnimMs:0.0} record{s.RecordMs:0.0} submit{s.SubmitMs:0.0}) | present {presentFps:0}fps gpu {gpuMs:0.0}ms{gpuRenderTok} | wait {WaitTok(host.LastWaitKind)}{host.LastWaitMs} {szpx.Width}x{szpx.Height}@{cachedHz}Hz (f{n})");
                }
            }
            if (h.Frames > 0 && n >= h.Frames) break;
            if (h.Screenshot != null)
                window.WaitForWork(h.FrameWaitMs);   // deterministic ~8ms/frame so time-driven animations advance (and never block)
            else
                // Low-rate wake pacing: idle/minimized block until a message (0% CPU); a HUD-only readout throttles to
                // ~10 Hz; real animation/scroll/decode paces at the display rate. WaitForWork returns early on input,
                // so responsiveness is identical at every timeout. (See AppHost.RecommendedWaitMs.) Folded across any
                // detached video windows, so a playing pop-out keeps the loop live even while the main window is idle.
                window.WaitForWork(host.WaitMsWithDetached());
        }

        if (allocTypes) AllocTypeProfiler.Stop();   // tear down the EventListener (no leak past the run)

        // --screenshot: read the last-rendered back buffer back to CPU and write a PNG for visual fidelity diffing.
        if (h.Screenshot is { } shotPath && device is D3D12Device d3d)
        {
            host.QuiesceRenderThread();   // async (FG_RENDER_ASYNC): stop the render thread so CaptureBgra (a UI-thread GPU op) is the sole GPU owner
            var px = d3d.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(shotPath, px, cw, ch);
            Console.Error.WriteLine($"screenshot: wrote {shotPath} ({cw}x{ch})");
        }

        WindowHandle = 0;   // the window is gone; don't leave a stale handle for a late SMTC/picker call.
    }

    private static long ImageCacheBudgetBytes()
    {
        const long DefaultBytes = 64L * 1024 * 1024;
        string? raw = Environment.GetEnvironmentVariable("FG_IMAGE_CACHE_MB");
        if (int.TryParse(raw, out int mb) && mb is >= 16 and <= 1024) return (long)mb * 1024 * 1024;
        return DefaultBytes;
    }
}

/// <summary>
/// The everyday window/app options for <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/>: window title + size,
/// Mica material, custom frame, the ambient-fps power throttle, and the post-input warm-cadence hold. Every field has a
/// flagship default, so <c>new AppOptions { Title = "…" }</c> overrides only what it names.
/// </summary>
public sealed record AppOptions
{
    /// <summary>Window title (caption / taskbar).</summary>
    public string Title { get; init; } = "FluentGpu";
    /// <summary>Initial client width (DIP).</summary>
    public int Width { get; init; } = 800;
    /// <summary>Initial client height (DIP).</summary>
    public int Height { get; init; } = 600;
    /// <summary>Apply the DWM Mica system backdrop (window becomes transparent to it). False = an opaque window.</summary>
    public bool Mica { get; init; } = true;
    /// <summary>Use Mica BaseAlt (the flatter File-Explorer tint) instead of the default Mica Base.</summary>
    public bool MicaAlt { get; init; }
    /// <summary>The app draws its own title bar (OS caption stripped; engine caption buttons + snap layouts).</summary>
    public bool CustomFrame { get; init; }
    /// <summary>Power throttle for PERPETUAL ambient motion (looping spinner/shimmer/equalizer, smooth playhead): the
    /// frame loop paces autonomous-animation frames to this rate instead of free-running at the panel refresh. 0 (the
    /// default) keeps the engine default (uncapped / display-rate). Latency-sensitive motion the user drives (scroll,
    /// hover, press, drag) is exempt and always runs at the display rate. Maps to <see cref="AppHost.AmbientAnimationFps"/>
    /// (a live <c>FG_ANIM_FPS</c> env var still wins).</summary>
    public int AmbientFps { get; init; }
    /// <summary>Post-input warm-cadence hold (ms): after the last input, keep rendering this long before allowing full
    /// quiesce so a follow-up interaction pays no cold-start ramp (G1b / research #10). 0 disables the hold. Maps to
    /// <see cref="AppHost.WarmCadenceHoldMs"/>.</summary>
    public float WarmCadenceMs { get; init; } = 1000f;
}

/// <summary>
/// The deterministic diagnostic knobs for <see cref="FluentAppHarness.Run"/> (test / screenshot / visual-diff loops):
/// a fixed frame count, a screenshot output path, and the per-frame wait. Separate from <see cref="AppOptions"/> so the
/// everyday <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/> surface never sees them.
/// </summary>
public sealed record HarnessOptions
{
    /// <summary>Stop after this many frames (&gt; 0); -1 (the default) runs interactively until the window closes.</summary>
    public int Frames { get; init; } = -1;
    /// <summary>When set, read the last-rendered back buffer to a PNG at this path after the run (visual-diff). The frame
    /// loop then paces at <see cref="FrameWaitMs"/> so time-driven animations advance deterministically.</summary>
    public string? Screenshot { get; init; }
    /// <summary>Per-frame wait (ms) used while a <see cref="Screenshot"/> is pending — deterministic settle pacing.</summary>
    public int FrameWaitMs { get; init; } = 8;
}

/// <summary>
/// The diagnostic / test entry point: <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/> with the deterministic
/// controls (frame count, screenshot, frame-wait) exposed via <see cref="HarnessOptions"/>. The gallery's
/// <c>--frames</c> / <c>--screenshot</c> arms and the screenshot visual-diff loop route through here; everyday apps use
/// <see cref="FluentApp.Run(Func{Component}, AppOptions?)"/>.
/// </summary>
public static class FluentAppHarness
{
    /// <summary>Run <paramref name="root"/> with the given window <paramref name="options"/> and diagnostic
    /// <paramref name="harness"/> controls.</summary>
    public static void Run(Func<Component> root, AppOptions? options = null, HarnessOptions? harness = null)
        => FluentApp.RunCore(root, options ?? new AppOptions(), harness ?? new HarnessOptions());
}
