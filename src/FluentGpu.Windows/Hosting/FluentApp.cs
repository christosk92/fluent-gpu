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

#if DEBUG || FLUENTGPU_DIAG
        // Diagnostic A/B only: remove the DWM-Mica / premultiplied-composition path as ONE variable. This creates the
        // ordinary opaque HWND flip-model swapchain, letting PresentMon tell us whether DWM composition is the cadence
        // bottleneck on the current build. The entire override (including the environment lookup) is absent from a normal
        // Release build; it is not a product backdrop decision.
        if (Diag.EnvFlag("FG_OPAQUE_WINDOW"))
        {
            o = o with { Mica = false };
            Console.Error.WriteLine("[window] FG_OPAQUE_WINDOW=1 — DWM Mica disabled; using opaque HWND swapchain");
        }
#endif

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
        // Also the [fps] line's latW/opgrp source below: both counters live on the device and are not carried in FrameStats.
        D3D12Device? gpuDev = device as D3D12Device;
        if (gpuDev is { } gpu)
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
        bool scrollPerf = Diag.EnvFlag("FG_SCROLL_PERF");
        // Publish the shared time axis before the first diagnostic line: [fps]/[scrollperf]/[wakediag]/[render-census]
        // carry no timestamp of their own, so without an anchor none of them can be joined to each other, to the CSV, or
        // to the launcher's wall-clock phase markers. No-op when the scroll trace already anchored at its own t0.
        if (fpsLog || scrollPerf) FluentGpu.Foundation.ScrollTrace.EnsureAnchor();
        // ops/diag phase protocol: the launcher writes one line ("<phase> <repetition> <abVariant> <cold>") into the file
        // named by FG_SCROLL_PHASE_FILE as each phase begins. Polled HERE, in the host loop — deliberately OUTSIDE
        // host.RunFrame() — because a filesystem touch inside phases 6-13 would breach the zero-alloc contract those
        // phases are gated on. Rate-limited to a mtime check every PhasePollFrames frames; the contents are read only
        // when the mtime actually moved.
        string? phaseFile = Environment.GetEnvironmentVariable("FG_SCROLL_PHASE_FILE");
        long phaseFileStamp = 0;
        const int PhasePollFrames = 15;
        int n = 0;
        // FrameMs/Fps time the UI loop. PresentFps comes from the host's successful-swapchain-present counter in every
        // submit mode, so async coalescing and inline mode are both represented truthfully.
        // Present-path diagnosis (maximize → 60fps): watch the swapchain size + window state so a resize emits a one-shot
        // [fps resize] marker (WxH, scale, state, panel Hz, wait-kind), and every [fps] line carries the wait-kind/ms the
        // loop paced by (Ambient = software 60 cap; DisplayRate/Pace = panel rate → a lock is downstream in Present/GPU).
        float lastLoggedW = -1f, lastLoggedH = -1f;
        int cachedHz = fpsLog ? window.CurrentRefreshHz() : 0;
        int spikeCluster = 0;
        bool prevSpike = false;
        // Deltas, not levels: PresentedSequence/FramesSkippedSubmit/PublishSequence are monotonic counters, and the
        // question a cadence investigation asks is always "how many since the last line".
        ulong prevPresentSeq = 0, prevPublishSeq = 0;
        long prevSkipped = 0, prevGated = 0;
        long scrollPerfWindowStart = scrollPerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        int spFrames = 0, spClipE = 0, spClipD = 0, spFullHide = 0, spPinD = 0, spMorphD = 0, spContD = 0, spBindsMax = 0;
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
            if (phaseFile is not null && n % PhasePollFrames == 0) PollPhaseMarker(phaseFile, ref phaseFileStamp);
            if (fpsLog || scrollPerf)
            {
                var s = host.LastStats;
                double gpuMs = host.LastGpuFenceWaitMs;
                var szpx = window.ClientSizePx;
                if (fpsLog && (szpx.Width != lastLoggedW || szpx.Height != lastLoggedH))
                {
                    lastLoggedW = szpx.Width; lastLoggedH = szpx.Height;
                    cachedHz = window.CurrentRefreshHz();   // once per size change, not per frame
                    Console.Error.WriteLine($"[fps resize] {szpx.Width}x{szpx.Height} scale {window.Scale:0.##} state {window.State} panel {cachedHz}Hz wait {WaitTok(host.LastWaitKind)}{host.LastWaitMs} (f{n})");
                }
                bool workSpike = (s.FlushMs + s.LayoutMs + s.RecordMs) > 11.0;
                // gpuMs (LastGpuFenceWaitMs) goes stale when submits are elided (skip-submit / pace-skip), so gate the
                // render-side spike on the frame actually presenting; scale the threshold with refresh so ordinary
                // vsync-pacing waits at 120Hz (~8.33ms → 12.5ms trip) aren't flagged, staying 11ms at 60Hz.
                double vsyncMs = cachedHz > 0 ? 1000.0 / cachedHz : 8.33;
                double gpuThreshold = Math.Max(11.0, vsyncMs * 1.5);
                bool spike = workSpike || (s.Presented && gpuMs > gpuThreshold);   // UI work OR a real render-thread GPU stall on a presented frame
                if (spike)
                {
                    spikeCluster = prevSpike ? spikeCluster + 1 : 1;
                    prevSpike = true;
                }
                else
                {
                    spikeCluster = 0;
                    prevSpike = false;
                }
                if (scrollPerf && (s.StickyClipEvals | s.StickyClipDirties | s.PinDirties | s.MorphDirties | s.ContinuousDirties) != 0)
                {
                    spFrames++;
                    spClipE += s.StickyClipEvals;
                    spClipD += s.StickyClipDirties;
                    spFullHide += s.StickyClipFullyHidden;
                    spPinD += s.PinDirties;
                    spMorphD += s.MorphDirties;
                    spContD += s.ContinuousDirties;
                    if (s.ScrollBindCount > spBindsMax) spBindsMax = s.ScrollBindCount;
                }
                if (scrollPerf)
                {
                    double spSec = (System.Diagnostics.Stopwatch.GetTimestamp() - scrollPerfWindowStart)
                        / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (spSec >= 1.0)
                    {
                        if (spFrames > 0)
                        {
                            Console.Error.WriteLine(
                                $"[scrollperf] frames={spFrames} clipE={spClipE} clipD={spClipD} fullHide={spFullHide} " +
                                $"pinD={spPinD} morphD={spMorphD} contD={spContD} bindsMax={spBindsMax}");
                        }
                        spFrames = spClipE = spClipD = spFullHide = spPinD = spMorphD = spContD = spBindsMax = 0;
                        scrollPerfWindowStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }
                // Emit on every SCROLL-ACTIVE frame, not just every 30th: a fixed frame stride samples a 10-second
                // gesture about 20 times, which cannot support a percentile and will miss a one-frame stall entirely.
                // The line is built and written outside RunFrame, so its allocation never touches the hot phases.
                if (fpsLog && (spike || s.ScrollActive || n % 30 == 0))
                {
                    double gpuRenderMs = s.GpuRenderMs;   // FG_GPU_TIMING: true raster ms (0 when off) — disambiguates the fence wait
                    // latW splits the always-printed `gpu` number (LastGpuFenceWaitMs conflates the frame fence with the
                    // swapchain latency waitable), so it is ungated exactly like `gpu`; opgrp counts the full-window layer
                    // composites the `comp` bucket is paid for, so it rides the FG_GPU_TIMING-gated grender group.
                    double latWaitMs = gpuDev?.LastLatencyWaitMs ?? 0.0;
                    int opGroups = gpuDev?.LastOpacityGroups ?? 0;
                    // …split by kind (they sum to opGroups) plus how many blended the FULL canvas — a bare count cannot
                    // tell a dozen cheap scissored row-fades from a handful of full-window reads, which is the real cost.
                    int opPlain = gpuDev?.LastPlainOpacityGroups ?? 0;
                    int opBounded = gpuDev?.LastBoundedOpacityGroups ?? 0;
                    int opBlur = gpuDev?.LastBlurGroups ?? 0;
                    int opEdge = gpuDev?.LastEdgeFadeGroups ?? 0;
                    int opFull = gpuDev?.LastFullTargetGroups ?? 0;
                    // grender X(scene Y: rect R img I glyph G comp C) opgrpN(o/bo/bl/ef,full) — all 0 unless FG_GPU_TIMING.
                    string gpuRenderTok = gpuRenderMs > 0.0
                        ? $" grender {gpuRenderMs:0.0}ms(scene {s.GpuSceneMs:0.0}: rect {s.GpuFillMs:0.0} img {s.GpuImageMs:0.0} glyph {s.GpuGlyphMs:0.0} comp {s.GpuCompositeMs:0.0}) opgrp{opGroups}(o{opPlain}/bo{opBounded}/bl{opBlur}/ef{opEdge},full{opFull})"
                        : "";
                    string clusterTok = spike && spikeCluster > 0 ? $" cluster={spikeCluster}" : "";
                    // layout X.X(fx A eff B conn C rf D) — the four passengers of the layout bucket (they sum to it):
                    // fx = the flex solve, eff = DrainLayoutEffects, conn = ConnectedAnimation.Tick65, rf = enter/exit
                    // reflow seeding. Printed only when the bucket is worth splitting (≥0.1 ms), so quiet frames stay short.
                    string layoutSplitTok = s.LayoutMs >= 0.1
                        ? $"(fx{s.LayoutSolveMs:0.0} eff{s.LayoutEffectsMs:0.0} conn{s.ConnectedTickMs:0.0} rf{s.ReflowSeedMs:0.0})"
                        : "";
                    string hitchTok =
                        $" | hitch comps={s.ComponentsRendered} nodes={s.NodesVisited}/{s.DrawNodeCount} " +
                        $"pump={s.ImagePumpMs:0.0}ms apply={s.ImageApplyCount}/{s.ImageApplyBytes / 1024}KB realize={s.RealizeCatchupMs:0.0}ms " +
                        $"escapes={s.RootRelayoutEscapes} escLoc={s.LocalRelayoutResolves} " +
                        $"spans={s.SpansReused}/{s.SpansRebased}/{s.SpansReRecorded} " +
                        $"reasons=0x{((uint)s.SpanReuseDisabledReasons):X} gc0=+{s.Gc0Delta} gc1=+{s.Gc1Delta} gc2=+{s.Gc2Delta}";
                    string scrollTok = scrollPerf
                        ? $" | scroll clipE={s.StickyClipEvals} clipD={s.StickyClipDirties} fullHide={s.StickyClipFullyHidden} " +
                          $"pinD={s.PinDirties} morphD={s.MorphDirties} contD={s.ContinuousDirties} binds={s.ScrollBindCount}"
                        : "";
                    // Read the LIVE host properties, not the FrameStats copies: five early-out paths in RunFrame
                    // construct `new FrameStats(0, ..., Rendered: false)` and leave both of these at 0, which is the
                    // mechanical reason idle/minimized stretches have always printed "present 0fps seq=0" — a
                    // construction artifact that reads exactly like a total present stall.
                    ulong presentSeq = host.PresentedSequence, publishSeq = host.PublishSequence;
                    ulong consumedSeq = host.ConsumedSequence;
                    long skipped = host.FramesSkippedSubmit;
                    // Saturating, because these are UNSIGNED counters that do not track each other exactly: a present
                    // can happen with nothing newly acquired (the previous frame stays on screen), so presentSeq may
                    // legitimately run ahead of publishSeq. An unguarded subtraction would wrap to ~1.8e19 and read as
                    // a catastrophic backlog.
                    static ulong Behind(ulong ahead, ulong behind) => ahead > behind ? ahead - behind : 0UL;
                    // gateD = frames the display-phase gate declined since the last line. In steady scrolling this
                    // should be SMALL and coal should sit at ~0: the gate's whole purpose is to stop producing the
                    // frames that coal was counting as discarded. A large gateD with a large coal means the gate is
                    // being bypassed (ceiling hit, or a non-async path).
                    long gated = host.PhaseGatedFrames;
                    string seamTok =
                        $" presentD={Behind(presentSeq, prevPresentSeq)} pubD={Behind(publishSeq, prevPublishSeq)} " +
                        $"coal={Behind(publishSeq, presentSeq)} lag={Behind(publishSeq, consumedSeq)} " +
                        $"ack={host.RenderPresentSeq} skipD={skipped - prevSkipped} gateD={gated - prevGated}";
                    prevPresentSeq = presentSeq; prevPublishSeq = publishSeq; prevSkipped = skipped; prevGated = gated;
                    Console.Error.WriteLine(
                        $"[fps] tMs={FluentGpu.Foundation.ScrollTrace.NowMs:0.000}{(spike ? " SPIKE" : "")}{clusterTok}" +
                        $"{(s.ScrollActive ? " scroll" : "")} loop {s.Fps:0}fps {s.FrameMs:0.0}ms " +
                        $"(flush{s.FlushMs:0.0} rx{s.ReactiveFlushMs:0.0}/vr{s.VirtualRealizeMs:0.0} layout{s.LayoutMs:0.0}{layoutSplitTok} " +
                        $"anim{s.AnimMs:0.0} record{s.RecordMs:0.0} submit{s.SubmitMs:0.0}) | present {host.PresentFps:0}fps seq={presentSeq}{seamTok} " +
                        $"gpu {gpuMs:0.0}ms latW{latWaitMs:0.0}{gpuRenderTok} | wait {WaitTok(host.LastWaitKind)}{host.LastWaitMs} " +
                        $"{szpx.Width}x{szpx.Height}@{cachedHz}Hz (f{n}){hitchTok}{scrollTok}");
                }
            }
            if (h.Frames > 0 && n >= h.Frames) break;
            if (h.Screenshot != null)
                window.WaitForWork(h.FrameWaitMs);   // deterministic ~8ms/frame so time-driven animations advance (and never block)
            else
            {
                // Low-rate wake pacing: idle/minimized block until a message (0% CPU); a HUD-only readout throttles to
                // ~10 Hz; real animation/scroll/decode paces at the display rate. WaitForWork returns early on input,
                // so responsiveness is identical at every timeout. (See AppHost.RecommendedWaitMs.) Folded across any
                // detached video windows, so a playing pop-out keeps the loop live even while the main window is idle.
                int waitMs = host.WaitMsWithDetached();
                // About to sleep: persist any buffered scroll-trace records first. ScrollTrace's own idle flush counts
                // IDLE FRAMES, but a loop that has nothing to do stops running frames at all — so an app that simply
                // goes quiet after a gesture never reaches the threshold, and everything since the last flush is lost
                // if the process is later killed rather than closed (ProcessExit is the only other flush). Measured:
                // a 50 s synthetic capture kept 32 of ~20,000 records. Gated on a genuinely long wait so the lock is
                // never taken on a display-rate frame, and a no-op (one uncontended lock, then an early return) both
                // when nothing is pending and in any build without FLUENTGPU_DIAG.
                if (waitMs < 0 || waitMs >= 100) FluentGpu.Foundation.ScrollTrace.Flush();
                window.WaitForWork(waitMs);
            }
        }

        if (allocTypes) AllocTypeProfiler.Stop();   // tear down the EventListener (no leak past the run)

        // --screenshot: read the last-rendered back buffer back to CPU and write a PNG for visual fidelity diffing.
        if (h.Screenshot is { } shotPath && device is D3D12Device d3d)
        {
            host.QuiesceRenderThread();   // async (the default): stop the render thread so CaptureBgra (a UI-thread GPU op) is the sole GPU owner
            var px = d3d.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(shotPath, px, cw, ch);
            Console.Error.WriteLine($"screenshot: wrote {shotPath} ({cw}x{ch})");
        }

        WindowHandle = 0;   // the window is gone; don't leave a stale handle for a late SMTC/picker call.
    }

    /// <summary>ops/diag capture protocol: read the launcher's phase marker and stamp it into every subsequent scroll-trace
    /// record, so a capture can be sliced by phase / repetition / A-B arm offline without any per-frame filesystem work
    /// on the scroll path. Format is one whitespace-separated line: <c>phaseOrdinal repetition abVariant coldPass</c>.
    ///
    /// Called from the host LOOP, never from inside <c>RunFrame</c> — a <c>File.Exists</c>/read there would sit inside
    /// the phases 6-13 window that <c>gate.alloc.steady-zero</c> and <c>gate.scroll.alloc-zero</c> hold at zero managed
    /// allocations, and nothing in the engine touches the filesystem per frame today. The mtime check is the cheap
    /// guard; the contents are parsed only when it actually moved.
    ///
    /// The human-named phase list lives in the launcher's phases.jsonl. Only the ORDINALS travel in-band, because the
    /// state word is packed into a POD ring record. What a human marker structurally cannot record is the drag →
    /// inertia → settle split within a phase; the integrator stamps that separately, per tick.</summary>
    private static void PollPhaseMarker(string path, ref long stamp)
    {
        try
        {
            long t = File.GetLastWriteTimeUtc(path).ToFileTimeUtc();
            if (t == stamp) return;
            stamp = t;
            // FileShare.ReadWrite is REQUIRED, not defensive: File.ReadAllText opens with FileShare.Read, which
            // EXCLUDES writers, so this poll would intermittently lock the launcher out of the very file it owns and
            // abort the capture mid-session. The reader must never be able to block the writer — a diagnostic that can
            // kill the run it is instrumenting is worse than no diagnostic.
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();
            string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[0], out int phase))
                FluentGpu.Foundation.ScrollTrace.SetState(FluentGpu.Foundation.ScrollTraceState.Phase, phase);
            if (parts.Length > 1 && int.TryParse(parts[1], out int rep))
                FluentGpu.Foundation.ScrollTrace.SetState(FluentGpu.Foundation.ScrollTraceState.Repetition, rep);
            if (parts.Length > 2 && int.TryParse(parts[2], out int ab))
                FluentGpu.Foundation.ScrollTrace.SetState(FluentGpu.Foundation.ScrollTraceState.AbVariant, ab);
            if (parts.Length > 3 && int.TryParse(parts[3], out int cold))
                FluentGpu.Foundation.ScrollTrace.SetState(FluentGpu.Foundation.ScrollTraceState.ColdPass, cold);
            // Note 210/211 mark the boundary IN the CSV itself, so a phase transition is visible in the row stream even
            // if phases.jsonl is lost — the 210-block is deliberately far from the engine's 100-block note codes.
            FluentGpu.Foundation.ScrollTrace.Note(210, 0f, FluentGpu.Foundation.ScrollTrace.StateWord, 0, 0f);
        }
        catch { /* best-effort diagnostic: a half-written marker is skipped, never fatal to the run */ }
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
