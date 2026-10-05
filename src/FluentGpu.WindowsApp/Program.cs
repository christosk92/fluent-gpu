using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// Shared context channel: the parent provides the live count; nested components read it via UseContext.
static class Demo
{
    public static readonly Context<int> Count = new(0);
}

// A nested, independently-rendered component that reads the count from context (no props threading).
sealed class CountDisplay : Component
{
    public override Element Render() => Heading($"Count: {UseContext(Demo.Count)}");
}

// A nested component with its OWN local state. On toggle, ONLY the icon glyph + its color change (the reconciler
// diffs and patches just that node); the color fades grey→red over 200ms via UseAnimatedValue (re-renders until settled).
sealed class LikeButton : Component
{
    public override Element Render()
    {
        var (liked, setLiked) = UseState(false);
        // bouncy scale pop on toggle — a composited spring on this button's node (no per-frame re-render);
        // the heart colour fades via the value hook. Toggle ⇒ deps change ⇒ the spring retargets (keeps velocity).
        UseSpring(AnimChannel.ScaleX, liked ? 1.12f : 1f, SpringParams.FromResponse(0.28f, 0.45f), liked);
        UseSpring(AnimChannel.ScaleY, liked ? 1.12f : 1f, SpringParams.FromResponse(0.28f, 0.45f), liked);
        float t = UseAnimatedValue(liked ? 1f : 0f, 200f);
        var fg = ColorF.Lerp(ColorF.FromRgba(0xC5, 0xC5, 0xC5), ColorF.FromRgba(0xE8, 0x11, 0x23), t);
        return Button.Standard(liked ? "♥ Liked" : "♡ Like", () => setLiked(!liked),
            Button.StandardStyle with { Foreground = fg });
    }
}

// The demo: flex layout + nested components + UseContext + accent/standard buttons + the system accent + Mica.
sealed class DemoApp : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);
        // entrance: the whole UI fades + slides up on mount (composited transition on the root node).
        UseTransition(AnimChannel.Opacity, 0f, 1f, 350f, Easing.EaseOut, "enter");
        UseTransition(AnimChannel.TranslateY, 14f, 0f, 350f, Easing.EaseOut, "enter");
        return new BoxEl
        {
            Direction = 1,
            Gap = 16,
            Padding = new Edges4(28, 24, 28, 28),
            Children =
            [
                Heading("FluentGpu — Fluent demo"),

                // Nested component reads the count across the component boundary via context.
                Ctx.Provide(Demo.Count, count, Embed.Comp(() => new CountDisplay())),

                // A row of controls: accent +/-, a neutral reset, a custom-styled button, and a self-stateful Like button.
                HStack(8,
                    Button.Accent("-", () => setCount(count - 1)),
                    Button.Accent("+", () => setCount(count + 1)),
                    Button.Standard("Reset", () => setCount(0)),
                    Button.Accent("Save", () => { }, new Button.Style
                    {
                        Background = ColorF.FromRgba(0x10, 0x7C, 0x10),
                        Foreground = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
                        BorderBrush = GradientSpec.Solid(ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x20)),
                        HoverBackground = ColorF.FromRgba(0x16, 0x95, 0x16),
                        PressedBackground = ColorF.FromRgba(0x0B, 0x5A, 0x0B),
                        CornerRadius = 6f,
                    }),
                    Embed.Comp(() => new LikeButton())),

                Text("Accent · standard · custom-styled · nested components · UseContext · hover/press · Mica.")
                    .Foreground(ColorF.FromRgba(0xB0, 0xB0, 0xB0)),
            ],
        };
    }
}

static class Program
{
    // [STAThread]: the gallery UI thread must be a single-threaded apartment, the conventional GUI-app apartment
    // (WinForms/WPF are STA for exactly this reason). The shell common-item dialog (IFileOpenDialog/IFileSaveDialog →
    // IModalWindow.Show, FilePicker.cs) is an STA-only (ThreadingModel=Apartment) coclass: its Show() does OLE init /
    // RegisterDragDrop and runs a nested modal loop with STA pump semantics, so invoking it from this thread (which owns
    // the composited window and runs the message pump in FluentApp.Run) requires STA. Without this attribute the thread
    // defaults to MTA and Show() wedges (the Dialogs-pillar crash). STA is also what the Shell pillar already assumes —
    // TaskbarManager/JumpList CoInitializeEx(APARTMENTTHREADED) on this thread and merely tolerate the MTA-degraded path
    // today. The Media (SMTC RoInitialize SINGLETHREADED), Notifications (toast activator has an E_INVALIDARG→non-AGILE
    // CoRegisterClassObject fallback that works in any apartment), and Network pillars are apartment-agnostic or run on a
    // separate MTA reader thread (NetworkStatus.MtaReader), so the MTA→STA flip is safe across the whole WindowsApi surface.
    [STAThread]
    static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--driver-memory-floor") >= 0)
        {
            Environment.Exit(DriverMemoryFloorProbe.Run(args));
            return;
        }
        if (Array.IndexOf(args, "--flip-idle-probe") >= 0
            || Array.IndexOf(args, "--flip-idle-probe-backstop") >= 0
            || Array.IndexOf(args, "--flip-idle-probe-stress") >= 0)
        {
            Environment.Exit(FlipIdleProbe.Run(
                backstop: Array.IndexOf(args, "--flip-idle-probe-backstop") >= 0,
                stress: Array.IndexOf(args, "--flip-idle-probe-stress") >= 0));
            return;
        }
        // ── FluentGpu.WindowsApi validation harness (runs BEFORE the window/GPU stack spins up). ──────────────────────
        // The spawned single-instance child mode MUST be first: it is a second process that should never touch the
        // window path — it acquires the gate, forwards its activation payload via WM_COPYDATA, and exits with a code the
        // parent asserts on.
        int childIdx = Array.IndexOf(args, "--windowsapi-smoke-child");
        if (childIdx >= 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            string payload = childIdx + 1 < args.Length ? args[childIdx + 1] : string.Empty;
            Environment.Exit(WindowsApiSmoke.RunChild(payload));
            return;
        }
        // The parent smoke: headless console-style [PASS]/[FAIL] checks over all four pillars; exit code = failure count.
        if (Array.IndexOf(args, "--windowsapi-smoke") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            Environment.Exit(WindowsApiSmoke.Run());
            return;
        }
        // Headless NLM-off-thread deadlock probe (regression gate for the "Windows APIs" page hang). Runs the exact
        // NetworkCard pattern — Task.Run(() => { _ = NetworkStatus.IsOnline; _ = NetworkStatus.GetConnectivity(); }) — on
        // pool threads with a hard per-read timeout, NO window/GPU. exit 0 = no hang (fixed); exit 2 = hung (reproduced).
        if (Array.IndexOf(args, "--nlm-deadlock-probe") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(6, 0))
        {
            Environment.Exit(NlmDeadlockProbe.Run(args));
            return;
        }
        // Headless dispatcher freeze probe (regression gate for the UsePost() stranding bug): drives a real headless
        // AppHost, posts from a worker thread, asserts the post applies. exit 0 = applied (fixed); exit 2 = stranded.
        if (Array.IndexOf(args, "--post-freeze-probe") >= 0)
        {
            Environment.Exit(PostFreezeProbe.Run(args));
            return;
        }
        // File-picker crash probe (regression gate for the Dialogs-pillar crash): opens Open/Save/PickFolder against a
        // real owner window and auto-dismisses each via a watchdog (no human). exit 0 = opened+dismissed, no crash
        // (fixed); a process fail-fast at IModalWindow.Show on an MTA thread (the bug) is seen as a non-zero crash exit
        // by the parent launcher. Add --sta to run the pickers on STA workers (the post-fix shape).
        if (Array.IndexOf(args, "--filepicker-probe") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
        {
            Environment.Exit(FilePickerProbe.Run(args));
            return;
        }
        // FAITHFUL variant: drive the REAL gallery window and run the picker on the UI thread mid-loop (the exact
        // DialogsCard reentrancy condition). exit 0 = ran + returned, no crash; a Show() fail-fast is a crash exit.
        if (Array.IndexOf(args, "--filepicker-auto") >= 0 && OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
        {
            Environment.Exit(FilePickerProbe.RunAuto(args));
            return;
        }
        // Packaging identity readback probe (the MSIX/package-identity prototype gate): reads PackageIdentity for the
        // CURRENT process and writes {IsPackaged, PackageFullName, PackageFamilyName, ApplicationUserModelId, Version,
        // InstalledLocation} to a fixed non-virtualized file (+ %TEMP% + Console), then exits — BEFORE any window/GPU
        // spins up. Launched under package identity via the AppExecutionAlias (fluentgpu-gallery.exe --packaging-probe),
        // IsPackaged reports True and a real PackageFullName/AUMID is read back from outside the process.
        if (Array.IndexOf(args, "--packaging-probe") >= 0)
        {
            Environment.Exit(PackagingProbe.Run(args));
            return;
        }
        // Localization (i18n) engine probe: loads the sample JSON resources and runs headless [PASS]/[FAIL] checks over
        // every feature (dotted-key load, named {name} interp, ICU plural en/pl, select, parent fallback, missing-key/arg
        // visible forms, pseudo-loc, live SetCulture re-resolution, OS-detected culture). exit code = failure count.
        if (Array.IndexOf(args, "--loc-probe") >= 0)
        {
            Environment.Exit(LocProbe.Run(args));
            return;
        }
        // WS7 gallery integrity gate: mount every [GalleryPage] registry entry headlessly (3 frames), assert no throw +
        // key uniqueness + Related-key resolution. Exit code = failure count. Runs BEFORE any window/GPU spins up.
        if (Array.IndexOf(args, "--gallery-audit") >= 0)
        {
            Environment.Exit(GalleryAudit.Run(args));
            return;
        }
        // WS7 shot-sweep contract: print every registry shot id (page:<key>) with ShotMode != Skip + the shell id.
        if (Array.IndexOf(args, "--shot-list") >= 0)
        {
            Environment.Exit(GalleryAudit.ShotList());
            return;
        }

        // Wire the gallery's soak / stress diagnostic harness into the engine's batteries-included entry point. The hook
        // only fires when a --soak / --stress-resize / --stress-nav / --wake-audit argument selected a mode; normal runs
        // ignore it. SoakProbe lives here (in the gallery) because it drives GalleryShell's nav hook, so it cannot move
        // into the engine library.
        SoakProbe.Parse(args);
        FluentApp.DiagnosticRun = SoakProbe.TryRun;
        // Gallery-only CLI arms (never environment variables): the diagnostics HUD, a forced localization culture, the
        // real-video source, and the D3D12 debug layer / DRED host options handed to FluentApp through AppOptions.
        GalleryShell.ShowDiagnosticsHud = Array.IndexOf(args, "--hud") >= 0;
        bool d3d12Debug = Array.IndexOf(args, "--d3d12-debug") >= 0;
        bool d3d12Dred = Array.IndexOf(args, "--d3d12-dred") >= 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--loc-culture") LocalizationPage.ForcedCulture = args[i + 1];
            if (args[i] == "--video-url") VideoReal.Url = args[i + 1];
        }

        int frames = -1;   // optional --frames N for headless/CI; omit for a normal interactive window
        string demo = "default";
        string? screenshot = null;   // --screenshot <path> renders a deterministic scene and writes a PNG (visual diff loop)
        string shot = "menu";        // --shot <id> selects the ShotScene
        string page = "welcome";     // --page <id> deep-links a gallery page (perf/diagnosis automation)
        bool micaShot = false;       // --mica reproduces the composited path the live app uses
        for (int i = 0; i < args.Length; i++)
        {
            if (i < args.Length - 1 && args[i] == "--frames" && int.TryParse(args[i + 1], out int f)) frames = f;
            if (i < args.Length - 1 && args[i] == "--demo") demo = args[i + 1];
            if (i < args.Length - 1 && args[i] == "--screenshot") screenshot = args[i + 1];
            if (i < args.Length - 1 && args[i] == "--shot") shot = args[i + 1];
            if (i < args.Length - 1 && args[i] == "--page") page = args[i + 1];
            if (args[i] == "--mica") micaShot = true;
        }

        // Scroll performance probes (engine track 1): `--scroll-bench [page] --dipPerSec N --seconds S` and
        // `--scroll-soak [page] [--scale x]`, plus the knockout switches (--edge-fades-off / --freeze-uploads /
        // --force-full-direct / --clear-only) and `--out dir`. CLI arms (never env switches); see ScrollPerfProbes.
        if (ScrollPerfProbes.Parse(args))
        {
            FluentApp.DiagnosticRun = ScrollPerfProbes.TryRun;
            FluentAppHarness.Run(() => new GalleryShell { InitialPage = ScrollPerfProbes.Page },
                new AppOptions { Title = "FluentGpu — Scroll probe", Width = 1240, Height = 820, CustomFrame = true,
                                 D3D12DebugLayer = d3d12Debug, D3D12Dred = d3d12Dred },
                new HarnessOptions { Frames = frames });
            Environment.Exit(ScrollPerfProbes.ExitCode);
            return;
        }

        // Detached-window render-isolation live stress probe (docs/plans/detached-window-render-isolation-
        // implementation.md §7.3): drives the REAL gallery — main window scrolling a long list at full rate while a
        // detached child window (the same AppHost.OpenDetachedWindow path Wavee's video pop-out rides) opens/closes
        // at randomized intervals, alternating monitors. `--detached-stress [outDir] [--cycles N] [--seed N]`.
        // The D3D12 debug layer is armed for the whole run (AppOptions.D3D12DebugLayer) so its validation output is
        // captured; DetachedStressProbe tees stderr to <outDir>/detached-stress.log itself. GPU + a live composited
        // desktop required; not a headless/CI gate.
        int dstress = Array.IndexOf(args, "--detached-stress");
        if (dstress >= 0)
        {
            string dstressOut = dstress + 1 < args.Length && !args[dstress + 1].StartsWith("--")
                ? args[dstress + 1] : ".tmp/detached-stress";
            int cycles = 300, seed = 1;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--cycles" && int.TryParse(args[i + 1], out int c)) cycles = c;
                if (args[i] == "--seed" && int.TryParse(args[i + 1], out int s)) seed = s;
            }
            DetachedStressProbe.Args = new DetachedStressProbe.Config(dstressOut, cycles, seed);
            FluentApp.DiagnosticRun = (h, w, d) => DetachedStressProbe.TryRun(h, w, d) || SoakProbe.TryRun(h, w, d);
            // Runs through the SAME default gallery composition the interactive launch uses (the probe navigates it
            // itself via GalleryShell.StressNavigate) — DiagnosticRun intercepts before the interactive loop starts.
            FluentAppHarness.Run(() => new GalleryShell { InitialPage = page },
                new AppOptions { Title = "FluentGpu — Capability Gallery", Width = 1240, Height = 820, CustomFrame = true,
                                 D3D12DebugLayer = true, D3D12Dred = d3d12Dred },
                new HarnessOptions { Frames = frames });
            Environment.Exit(DetachedStressProbe.ExitCode);
            return;
        }

        // Automated end-to-end video test with timings (video-engine-design.html measurement plan): the real gallery host
        // scrolling a 100k-row list while a detached pop-out plays a clear MP4; baseline / pop-out / UI actions / source
        // switching / placement churn / pop-out reopen, each with a threshold. `--video-e2e [outDir] [--seconds N]
        // [--switches N] [--cycles N]`; writes video-e2e.json + video-e2e.md. Exit 0 all PASS, 2 any FAIL, 1 incomplete.
        // The D3D12 debug layer stays OFF: it would distort the timings being measured.
        int ve2e = Array.IndexOf(args, "--video-e2e");
        if (ve2e >= 0)
        {
            string ve2eOut = ve2e + 1 < args.Length && !args[ve2e + 1].StartsWith("--")
                ? args[ve2e + 1] : ".tmp/video-e2e";
            double ve2eSeconds = 15; int ve2eSwitches = 10, ve2eCycles = 20;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--seconds" && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec) && sec > 0) ve2eSeconds = sec;
                if (args[i] == "--switches" && int.TryParse(args[i + 1], out int sw) && sw > 0) ve2eSwitches = sw;
                if (args[i] == "--cycles" && int.TryParse(args[i + 1], out int cy) && cy > 0) ve2eCycles = cy;
            }
            VideoE2EProbe.Args = new VideoE2EProbe.Config(ve2eOut, ve2eSeconds, ve2eSwitches, ve2eCycles);
            FluentApp.DiagnosticRun = VideoE2EProbe.TryRun;
            FluentAppHarness.Run(() => new GalleryShell { InitialPage = "video-e2e" },
                new AppOptions { Title = "FluentGpu — Capability Gallery", Width = 1240, Height = 820, CustomFrame = true },
                new HarnessOptions { Frames = frames });
            Environment.Exit(VideoE2EProbe.ExitCode);
            return;
        }

        // M0 of the DRM-free video compositing spine: restructured DComp present tree + IVideoPresenter + an
        // engine-owned test surface through the real CreateSurfaceFromHandle path + a graded hole, captured to a PNG
        // (docs/plans/video-phase1-plan.md §4). `--video-m0 <png>` [--frames N]. Screen capture (the DComp child is
        // composited by DWM, not in our back buffer), so it needs a live composited desktop session.
        int vm0 = Array.IndexOf(args, "--video-m0");
        if (vm0 >= 0)
        {
            string outPng = vm0 + 1 < args.Length ? args[vm0 + 1] : ".tmp/m0-graded-hole.png";
            int vf = -1;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--frames" && int.TryParse(args[i + 1], out int f)) vf = f;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPng))!);
            Environment.Exit(VideoM0.Run(outPng, vf));
            return;
        }

        // M3 of the DRM-free video compositing spine: IMFMediaEngineEx windowless-swapchain decode of a CLEAR MP4 →
        // GetVideoSwapchainHandle → the SAME IVideoPresenter M0 proved, composited z-below the UI. `--video-real <png>`
        // [--frames N]. Screen capture (the DComp child is composited by DWM), needs a live composited desktop session.
        int vreal = Array.IndexOf(args, "--video-real");
        if (vreal >= 0)
        {
            string outPng = vreal + 1 < args.Length && !args[vreal + 1].StartsWith("--") ? args[vreal + 1] : ".tmp/m3-real-video.png";
            int vf = -1;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--frames" && int.TryParse(args[i + 1], out int f)) vf = f;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPng))!);
            Environment.Exit(VideoReal.Run(outPng, vf));
            return;
        }

        // Standalone MF Media Engine probe (NO D3D12 device / window): create VideoMediaEngine + poll to characterize
        // exactly how far the engine gets on this machine (isolates MF/DXGI from our renderer). `--video-probe [url]`.
        int vprobe = Array.IndexOf(args, "--video-probe");
        if (vprobe >= 0)
        {
            string purl = VideoReal.Url
                ?? (vprobe + 1 < args.Length && !args[vprobe + 1].StartsWith("--") ? args[vprobe + 1] : "https://media.w3.org/2010/05/sintel/trailer.mp4");
            using var eng = new FluentGpu.Media.Windows.VideoMediaEngine();
            eng.Start();
            eng.PostSetSource(purl);
            eng.Commands.Post(FluentGpu.Media.VideoCommandKind.Transport, a: 1);
            Console.Error.WriteLine($"video-probe: started url={purl}");
            var dl = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            var ps = eng.Snapshot;
            while (DateTime.UtcNow < dl
                && (ps.Flags & (FluentGpu.Media.VideoEngineFlags.Error | FluentGpu.Media.VideoEngineFlags.Faulted | FluentGpu.Media.VideoEngineFlags.Playing)) == 0)
            {
                System.Threading.Thread.Sleep(500);
                ps = eng.Snapshot;
                Console.Error.WriteLine($"video-probe: readyState={ps.ReadyState} flags={ps.Flags} pos={ps.PositionSeconds:0.00}");
            }
            ps = eng.Snapshot;
            Console.Error.WriteLine($"video-probe: FINAL flags={ps.Flags} readyState={ps.ReadyState} err=0x{(uint)ps.ErrorHr:X8} swapchainHandle=0x{(ulong)ps.SwapchainHandle:X}");
            return;
        }

        // DirectWrite itemizer smoke test (BiDi/script/line-break via the callee CCWs) then exit.
        if (Array.IndexOf(args, "--itemtest") >= 0)
        {
            FluentGpu.Text.DirectWrite.DWriteItemizer.SelfTest();
            return;
        }
        if (Array.IndexOf(args, "--shapetest") >= 0)
        {
            FluentGpu.Text.DirectWrite.DWriteTextShaper.SelfTest();
            return;
        }
        if (Array.IndexOf(args, "--layouttest") >= 0)
        {
            FluentGpu.Text.DirectWrite.TextLayoutEngine.SelfTest();
            return;
        }

        // Repaint-identity mode: the PIXEL gates of the retained tiled composite (gpu-renderer.md §13; see
        // RepaintIdentityProbe). Nonzero exit on any mismatch. GPU required. `--repaint-identity [outDir]`; mismatches
        // write A/B/diff PNGs there.
        // Dialog-scroll probe: the real-path repro for "ContentDialog labels vanish after the body scrolls" — drives the
        // dialog's own scroller through its ScrollHandle and captures the presented back buffer at top/mid/end with the
        // D3D12 route/dropped/glyph-instance/segment counters. `--dialog-scroll-probe [outDir]`. GPU required.
        int dsp = Array.IndexOf(args, "--dialog-scroll-probe");
        if (dsp >= 0)
        {
            string? dspOut = dsp + 1 < args.Length && !args[dsp + 1].StartsWith("--") ? args[dsp + 1] : null;
            Environment.Exit(DialogScrollProbe.Run(dspOut));
            return;
        }

        int smallImagePool = Array.IndexOf(args, "--small-image-pool");
        if (smallImagePool >= 0)
        {
            string? output = smallImagePool + 1 < args.Length && !args[smallImagePool + 1].StartsWith("--")
                ? args[smallImagePool + 1] : null;
            Environment.Exit(SmallTexturePlacementProbe.Run(output, productionPool: true));
            return;
        }

        int smallTextures = Array.IndexOf(args, "--small-texture-placement");
        if (smallTextures >= 0)
        {
            string? output = smallTextures + 1 < args.Length && !args[smallTextures + 1].StartsWith("--")
                ? args[smallTextures + 1] : null;
            Environment.Exit(SmallTexturePlacementProbe.Run(output));
            return;
        }

        int glyphCapacity = Array.IndexOf(args, "--glyph-capacity");
        if (glyphCapacity >= 0)
        {
            string? output = glyphCapacity + 1 < args.Length && !args[glyphCapacity + 1].StartsWith("--")
                ? args[glyphCapacity + 1] : null;
            Environment.Exit(GlyphCapacityProbe.Run(output));
            return;
        }

        int ident = Array.IndexOf(args, "--repaint-identity");
        if (ident >= 0)
        {
            string? identOut = ident + 1 < args.Length && !args[ident + 1].StartsWith("--") ? args[ident + 1] : null;
            int onlyAt = Array.IndexOf(args, "--only");
            string? only = onlyAt >= 0 && onlyAt + 1 < args.Length ? args[onlyAt + 1] : null;
            Environment.Exit(RepaintIdentityProbe.Run(identOut, only));
            return;
        }

        // Screenshot mode: render a single deterministic scene then exit. Opaque by default; --mica for the composited path.
        if (screenshot != null)
        {
            int sf = frames > 0 ? frames : 6;   // a few frames to settle layout + glyph upload
            int sw = 900, sh = 640;             // --w/--h: reproduce a reported window geometry (wrap/clip bugs are width-dependent)
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--w" && int.TryParse(args[i + 1], out int w)) sw = w;
                if (args[i] == "--h" && int.TryParse(args[i + 1], out int h)) sh = h;
            }
            FluentAppHarness.Run(() => new ShotScene(shot),
                new AppOptions { Title = "FluentGpu — Shot", Width = sw, Height = sh, Mica = micaShot },
                new HarnessOptions { Frames = sf, Screenshot = screenshot });
            return;
        }

        // Default = the capability gallery (everything). `--demo wavee` = the Wavee skeleton; `--demo list` = the
        // virtualized track list; `--demo basic` = the original minimal demo. All route through the harness so `--frames`
        // stays a diagnostic knob (AppOptions carries only the everyday window options).
        if (demo == "wavee")
            FluentAppHarness.Run(() => new WaveeShell(),
                new AppOptions { Title = "FluentGpu — Wavee skeleton", Width = 1180, Height = 760 },
                new HarnessOptions { Frames = frames });
        else if (demo == "list")
            FluentAppHarness.Run(() => new TrackListDemo(),
                new AppOptions { Title = "FluentGpu — Virtualized List", Width = 520, Height = 640 },
                new HarnessOptions { Frames = frames });
        else if (demo == "basic")
            FluentAppHarness.Run(() => new DemoApp(),
                new AppOptions { Title = "FluentGpu — Demo", Width = 560, Height = 360 },
                new HarnessOptions { Frames = frames });
        else
            // WS7: the registry-driven GalleryShell (the sole shell — the legacy GalleryApp was deleted in G8b).
            // The gallery draws the WinUI TitleBar (engine caption buttons).
            FluentAppHarness.Run(() => new GalleryShell { InitialPage = page },
                new AppOptions { Title = "FluentGpu — Capability Gallery", Width = 1240, Height = 820, CustomFrame = true,
                                 D3D12DebugLayer = d3d12Debug, D3D12Dred = d3d12Dred },
                new HarnessOptions { Frames = frames });
    }
}
