using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Reconciler;
using FluentGpu.Signals;

namespace FluentGpu.Hosting;

/// <summary>
/// The engine's runtime diagnostic toggles — plain static state, set in code (a host, a probe, a Diagnostics page) or
/// from the hosting process's command line through <see cref="Apply(ReadOnlySpan{string})"/>. There are NO environment
/// variable switches in the engine: an ambient variable silently changes a run nobody can see the cause of, while a
/// command-line flag is on the launch line of the process it changes. <c>FluentApp</c> applies its own process's
/// arguments once, before the window exists, so every FluentApp host (the gallery, Wavee, the benches) accepts:
/// <code>--fg name[,name...]</code> (or <c>--fg=name,...</c>), with names
/// <list type="bullet">
/// <item><c>diag</c> — engine <see cref="Diag"/> on (compiled-in builds) with its sink on stderr, plus the boot trace.</item>
/// <item><c>fps</c> — the periodic <c>[fps]</c> line.</item>
/// <item><c>alloc</c> / <c>alloc-types</c> — per-segment allocation probes / the process-global allocation-type listener.</item>
/// <item><c>mem</c> or <c>mem=N</c> — interval memory census every N seconds (default 5).</item>
/// <item><c>resize</c>, <c>motion</c>, <c>layout</c>, <c>layout-overflow</c>, <c>layout-verify</c> — their printouts.</item>
/// <item><c>render</c> — the render-budget tripwire (<see cref="RenderBudget"/>) and the device's submitted-area census.</item>
/// <item><c>img=FILTER</c> — narrow the image-cache trace (a <c>diag</c> run) to sources containing FILTER.</item>
/// <item><c>d3d-mem</c> — per-resource D3D12 allocation lines. <c>nc</c> — the non-client hit-test trace.</item>
/// <item><c>dump=MODE</c> — the one-shot scene dump.</item>
/// <item><c>shelf</c>, <c>morph</c> — the paged-shelf / connected-animation traces.</item>
/// <item><c>no-guards</c> — the default-on DEBUG guards (BindContract, BackwardsWriteGuard, one-surface-per-player)
/// off, for a measurement that must not pay their per-write scans. <c>guards-throw</c> — every guard throws.</item>
/// <item><c>device-lost=N</c> — inject a device loss at frame N (the recovery path's test arm).</item>
/// <item><c>opaque</c> — an opaque HWND swapchain instead of the DWM Mica composition (A/B arm).</item>
/// <item><c>no-precise-wait</c> — the frame wait falls back from the high-resolution waitable timer.</item>
/// <item><c>no-vsync</c> — present at sync-interval 0 (diagnose present cap vs frame cost).</item>
/// <item><c>present-nowait</c> — a detached pop-out presents with DXGI_PRESENT_DO_NOT_WAIT and re-presents a refused frame on
/// a later turn instead of ever blocking the shared render thread in Present (A/B arm, default off).</item>
/// <item><c>gpu-timing</c> — start with the pass-granular GPU timeline on (<c>AppHost.GpuPassTimingEnabled</c>, the
/// same runtime toggle the Wavee Diagnostics "Tiles" card flips).</item>
/// <item><c>video-nv12</c> — both media engines (clear and protected) output NV12 instead of the forced BGRA when the
/// output's overlay probe reports NV12 support (F249, A/B arm, default off).</item>
/// <item><c>playready-sl2000</c> — the protected runtime never probes hardware PlayReady SL3000 (the <c>.3000</c> key system with a
/// "3000" video capability) and asks for the software SL2000 CDM it always did. Without it the runtime probes SL3000 and falls back to
/// SL2000 when the machine cannot grant it, and logs the negotiated level (F022; default off = probe on).</item>
/// <item><c>video-overlay</c> — a video whose rect nothing paints over is promoted ABOVE the UI plane instead of staying
/// a hole-punched underlay, where the output's overlay probe reports support (F087, A/B arm, default off).</item>
/// <item><c>ledger</c> or <c>ledger=PATH</c> — the per-frame <see cref="FrameLedger"/> on from the first frame (CPU, memory and GPU
/// for every frame); with a PATH, FluentApp writes the binary dump there and one CSV per stream beside it when the window closes.</item>
/// <item><c>test-input</c> — a window accepts the private registered message <c>FluentGpu.TestInput</c> (kind + client px in
/// wParam/lParam, see <c>Win32TestInput</c>) and turns it into the pointer events <c>WM_POINTER*</c> would, so an out-of-process
/// e2e driver can hover, click, drag and wheel without the physical mouse (default off).</item>
/// </list>
/// Unknown names are reported once on stderr and ignored.
/// </summary>
public static class EngineSwitches
{
    public static bool DiagConsole;
    public static bool FpsLog;
    public static bool AllocDiag;
    public static bool AllocTypes;
    /// <summary>Interval memory census period in seconds; 0 = off.</summary>
    public static int MemDiagSeconds;
    public static bool ResizeDiag;
    public static bool MotionDiag;
    public static bool LayoutDiag;
    public static bool LayoutOverflow;
    public static bool LayoutVerify;
    public static bool RenderDiag;
    /// <summary>Image-cache trace source filter (null = trace every source while <see cref="Diag"/> is on).</summary>
    public static string? ImageTrace;
    public static bool D3DMemLog;
    public static bool NcDiag;
    /// <summary>Accept the <c>FluentGpu.TestInput</c> window message (<c>--fg test-input</c>); read once when the first window is created.</summary>
    public static bool TestInput;
    /// <summary>One-shot scene dump mode (null = off).</summary>
    public static string? SceneDump;
    public static bool ShelfLog;
    public static bool MorphLog;
    /// <summary>Inject a device loss at this frame ordinal (-1 = never).</summary>
    public static int ForceDeviceLostAtFrame = -1;
    public static bool OpaqueWindow;
    /// <summary>High-resolution waitable-timer frame waits (default on).</summary>
    public static bool PreciseWait = true;
    /// <summary>Present at sync-interval 0 (+ ALLOW_TEARING) instead of 1.</summary>
    public static bool NoVsync;
    /// <summary>Start the host with its pass-granular GPU timeline on.</summary>
    public static bool GpuPassTiming;
    /// <summary>Non-blocking secondary present (F085, <c>--fg present-nowait</c>): a detached pop-out's Present carries
    /// DXGI_PRESENT_DO_NOT_WAIT, and a frame DXGI refuses (DXGI_ERROR_WAS_STILL_DRAWING) stays owed and is re-presented on a
    /// later turn - the shared render thread never waits inside a secondary window's Present. A HYPOTHESIS arm, default off:
    /// the child's present slot is already probed without waiting (F090), so the default is decided by PresentMon
    /// (MsBetweenDisplayChange) on both HWNDs with the pop-out playing, not by this flag's existence.</summary>
    public static bool NonBlockingSecondaryPresent;

    /// <summary>NV12 media-engine output (F249, <c>--fg video-nv12</c>): both engines drop the forced B8G8R8A8 output format for NV12
    /// when the render output's overlay probe (<see cref="FluentGpu.Media.VideoOverlayCaps"/>) reports NV12 as plane-capable, so the
    /// decoded frame skips the per-frame NV12 to BGRA video-processor pass and a YUV overlay plane can take it. BGRA stays the fallback
    /// whenever the probe says no or has not run. A HYPOTHESIS arm, default off: whether MF honours the format for a windowless swap
    /// chain and whether DWM then promotes it is only settled by PresentMon (the Hardware Composed / MPO plane columns) with the
    /// switch on and off.</summary>
    public static bool Nv12VideoOutput;

    /// <summary>Overlay promotion (F087, <c>--fg video-overlay</c>): a video whose rect nothing paints over (a fullscreen video with its
    /// chrome hidden, an idle pop-out) is inserted ABOVE the UI visual and falls back to the hole-punched underlay the turn something
    /// covers it, with a hold after each demotion; only where the output's overlay probe reports support. The UI hole stays punched in
    /// both modes. A HYPOTHESIS arm, default off, decided by the owner's PresentMon A/B (<c>MsBetweenDisplayChange</c> and the
    /// PresentMode column, switch on vs off), not by this flag's existence.</summary>
    public static bool VideoOverlay;

    /// <summary>Skip the PlayReady SL3000 probe (F022, <c>--fg playready-sl2000</c>): the protected runtime's CDM is the software SL2000 request
    /// it has always made. The default (false) probes the hardware key system first - SL3000 is used only where the machine grants it,
    /// otherwise the runtime falls back to SL2000 - and the negotiated level is logged once per runtime. Read once, when the native runtime
    /// is created: set it (the command line) before the first protected open.</summary>
    public static bool ForcePlayReadySl2000;

    /// <summary>The frame ledger from the first frame (<c>--fg ledger[=PATH]</c>); the path, when given, is <see cref="FrameLedger.DumpPath"/>.</summary>
    public static bool Ledger;

    /// <summary>Apply every <c>--fg</c> flag in <paramref name="args"/>.</summary>
    public static void Apply(ReadOnlySpan<string> args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--fg" && i + 1 < args.Length) ApplyList(args[++i]);
            else if (a.StartsWith("--fg=", StringComparison.Ordinal)) ApplyList(a.Substring(5));
        }
    }

    /// <summary>Apply one comma-separated switch list (<c>fps,layout,mem=10</c>).</summary>
    public static void ApplyList(string list)
    {
        foreach (string raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.IndexOf('=');
            string name = eq < 0 ? raw : raw.Substring(0, eq);
            string? value = eq < 0 ? null : raw.Substring(eq + 1);
            if (!ApplyOne(name, value)) Console.Error.WriteLine($"[fg] unknown engine switch '{raw}' (ignored)");
        }
    }

    private static bool ApplyOne(string name, string? value)
    {
        switch (name)
        {
            case "diag":
                DiagConsole = true;
                Diag.Enabled = Diag.CompiledIn;
                return true;
            case "fps": FpsLog = true; return true;
            case "alloc": AllocDiag = true; return true;
            case "alloc-types": AllocTypes = true; return true;
            case "mem":
                MemDiagSeconds = value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec) && sec > 0 ? sec : 5;
                return true;
            case "resize": ResizeDiag = true; return true;
            case "motion": MotionDiag = true; return true;
            case "layout": LayoutDiag = true; return true;
            case "layout-overflow": LayoutOverflow = true; return true;
            case "layout-verify": LayoutVerify = true; return true;
            case "render":
                RenderDiag = true;
                RenderBudget.Enabled = RenderBudget.CompiledIn;
                return true;
            case "img": ImageTrace = string.IsNullOrEmpty(value) ? null : value; return ImageTrace is not null;
            case "d3d-mem": D3DMemLog = true; return true;
            case "nc": NcDiag = true; return true;
            case "test-input": TestInput = true; return true;
            case "dump": SceneDump = string.IsNullOrEmpty(value) ? "1" : value; return true;
            case "shelf": ShelfLog = true; return true;
            case "morph": MorphLog = true; return true;
            case "no-guards":
                BindContract.Enabled = false;
                BackwardsWriteGuard.Enabled = false;
                OneSurfacePerPlayerGuard.Enabled = false;
                return true;
            case "guards-throw":
                BindContract.ThrowOnViolation = BindContract.CompiledIn;
                BackwardsWriteGuard.ThrowOnViolation = BackwardsWriteGuard.CompiledIn;
                FluentGpu.Hooks.ReuseGuard.ThrowOnViolation = FluentGpu.Hooks.ReuseGuard.CompiledIn;
                OneSurfacePerPlayerGuard.ThrowOnViolation = OneSurfacePerPlayerGuard.CompiledIn;
                return true;
            case "device-lost":
                ForceDeviceLostAtFrame = value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f > 0 ? f : -1;
                return ForceDeviceLostAtFrame > 0;
            case "opaque": OpaqueWindow = true; return true;
            case "no-precise-wait": PreciseWait = false; return true;
            case "no-vsync": NoVsync = true; return true;
            case "present-nowait": NonBlockingSecondaryPresent = true; return true;
            case "gpu-timing": GpuPassTiming = true; return true;
            case "video-nv12": Nv12VideoOutput = true; return true;
            case "video-overlay": VideoOverlay = true; return true;
            case "playready-sl2000": ForcePlayReadySl2000 = true; return true;
            case "ledger":
                Ledger = true;
                if (!string.IsNullOrEmpty(value)) FrameLedger.DumpPath = value;
                return true;
            default: return false;
        }
    }
}
