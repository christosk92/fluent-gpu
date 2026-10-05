using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;

namespace FluentGpu;

/// <summary>
/// <c>--detached-stress [outDir] [--cycles N] [--seed N]</c> (implementation plan
/// <c>docs/plans/detached-window-render-isolation-implementation.md</c> §7.3): the LIVE two-monitor repro for the
/// detached-child render-isolation incident (INCIDENT 2026-09 — a popup's <c>Close</c> path faulting the shared
/// <c>ID3D12GraphicsCommandList</c> used to kill the whole process). Keeps the REAL gallery's main window scrolling a
/// 100k-row virtualized list at full rate (<see cref="GalleryShell.StressNavigate"/> to <c>"virtualization"</c> +
/// posting synthetic <c>ScrollInput.ScrollTo</c> every frame — the same command port a wheel drives) while a detached
/// child window — the exact <see cref="AppHost.OpenDetachedWindow"/> path Wavee's video pop-out rides — opens and
/// closes <c>--cycles</c> times (default 300) at randomized 50-1500ms intervals, alternating monitors when more than
/// one is attached (<see cref="EnumerateMonitorRects"/>, a local <c>EnumDisplayMonitors</c> P/Invoke — this is a live
/// diagnostic, not part of the NativeAOT publish closure).
///
/// A CLI arm (the <c>--dialog-scroll-probe</c>/<c>--repaint-identity</c> precedent): <c>Program.cs</c> launches the
/// gallery with <c>AppOptions.D3D12DebugLayer</c> set when <c>--detached-stress</c> is present, so the D3D12 debug
/// layer's validation output is captured for the whole run. This class installs a stderr tee
/// (<see cref="Console.SetError"/>) so every <c>[d3d12.debug]</c> / <c>[d3d12.forensic]</c> / <c>[d3d12.stall]</c> /
/// <c>[detached] child frame failed</c> line — all of which route to <c>Console.Error</c> today, always-on, no
/// Diag.Enabled gate — lands in <c>&lt;outDir&gt;/detached-stress.log</c> as well as the console.
///
/// <b>Exit code:</b> 0 iff zero D3D12 debug-layer ERROR/CORRUPTION lines, zero <c>[d3d12.forensic]</c> lines (the
/// always-on Close-failure forensic ring — §2.6), zero <c>Close()</c> exceptions/reap-timeouts, zero
/// <c>[detached] child frame failed</c> lines / <see cref="IDetachedVideoWindow.RenderFailed"/> latches, zero
/// device-lost recoveries, and the main window's presented fps (<see cref="FrameStats.PresentFps"/>) stayed
/// &gt;= 0.9x the PRIMARY monitor's refresh across every window that had a child open — the plan's Phase 2 criterion — and
/// the shared render thread's present-slot wait (<see cref="RenderPaceSnapshot.SlotWaitMaxMs"/>) never ran past 100 ms while the
/// per-frame-repainting child was open (a pop-out that composites into, or waits on, the MAIN swapchain's frame-latency
/// credit pins it at the 1000 ms liveness bound — F090);
/// a Phase 0/1 run still reports the number without failing the process on it, since the render-thread-per-target
/// allocation counter (§7.3 point 4) is a later-wave RenderThread addition — this probe uses
/// <see cref="FrameStats.HotPhaseAllocBytes"/> (today's zero-alloc-phase gauge, sampled through the same
/// <c>host.RunFrame()</c> call) as a same-shape stand-in until that counter lands, and says so in the summary line.
/// Nonzero otherwise. Needs a live composited desktop + GPU; not a headless/CI gate — the owner runs it after each
/// wave on the two-monitor rig (120 Hz primary + a different-refresh secondary), Debug + the debug layer.
/// </summary>
internal static class DetachedStressProbe
{
    public readonly record struct Config(string OutDir, int Cycles, int Seed);

    /// <summary>Set by Program.cs from the CLI args before FluentApp.Run/FluentAppHarness.Run; null unless
    /// <c>--detached-stress</c> was passed. <see cref="TryRun"/> takes over the interactive frame loop when set,
    /// exactly like <see cref="SoakProbe.TryRun"/> — chain both on <see cref="FluentApp.DiagnosticRun"/>.</summary>
    public static Config? Args;

    /// <summary>The probe's verdict, read by Program.cs AFTER <c>FluentAppHarness.Run</c> returns (the diagnostic
    /// delegate runs INSIDE that call, before the window/device teardown it still needs — see <see cref="TryRun"/> —
    /// so it cannot itself <c>Environment.Exit</c> without skipping that teardown, unlike a probe that runs before any
    /// window opens).</summary>
    public static int ExitCode;

    /// <summary>Adapter for <see cref="FluentApp.DiagnosticRun"/>: run the stress drive when <see cref="Args"/> is
    /// set, else decline (so it chains harmlessly ahead of <see cref="SoakProbe.TryRun"/>).</summary>
    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (Args is not { } cfg) return false;
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("[detached-stress] needs the Win32 + D3D12 backend (GPU required).");
            ExitCode = 2;
            return true;
        }
        ExitCode = Run(cfg, host, w, gpu);
        return true;
    }

    private static int Run(Config cfg, AppHost host, Win32Window window, D3D12Device gpu)
    {
        Directory.CreateDirectory(cfg.OutDir);
        string logPath = Path.Combine(cfg.OutDir, "detached-stress.log");
        var counters = new Counters();
        var originalErr = Console.Error;
        using var fileWriter = new StreamWriter(File.Create(logPath), Encoding.UTF8) { AutoFlush = true };
        var tee = new TeeTextWriter(originalErr, fileWriter, counters);
        Console.SetError(tee);
        try
        {
            return Drive(cfg, host, window, gpu, counters);
        }
        finally
        {
            Console.Error.Flush();
            Console.SetError(originalErr);
        }
    }

    private static int Drive(Config cfg, AppHost host, Win32Window window, D3D12Device gpu, Counters counters)
    {
        var monitors = EnumerateMonitorRects();
        var primary = DisplayInfo.ForPrimary();
        double primaryHz = primary.Valid && primary.RefreshDenominator != 0
            ? primary.RefreshNumerator / (double)primary.RefreshDenominator
            : 60.0;
        Console.Error.WriteLine(
            $"[detached-stress] start cycles={cfg.Cycles} seed={cfg.Seed} outDir={Path.GetFullPath(cfg.OutDir)} " +
            $"monitors={monitors.Count} primaryHz={primaryHz:0.00}" +
            (monitors.Count > 1 ? " (alternating)" : " (single monitor attached — alternation degrades to same monitor)"));

        for (int i = 0; i < 30 && !window.IsClosed; i++) host.RunFrame();   // warm up + mount the gallery shell

        var nav = GalleryShell.StressNavigate;
        if (nav is null)
        {
            Console.Error.WriteLine("[detached-stress] nav hook unavailable — is the root GalleryShell?");
            return 2;
        }
        nav("virtualization");   // the 100k-row bound-recycler list — the app's own 10k+ library-list stand-in
        for (int i = 0; i < 12 && !window.IsClosed; i++) host.RunFrame();
        double slotWaitMaxBaselineMs = host.RenderPace.SlotWaitMaxMs;   // the render thread's running max before any child opened

        NodeHandle scrollNode = FindScrollable(host.Scene, host.Scene.Root);
        bool canScroll = !scrollNode.IsNull;
        if (!canScroll) Console.Error.WriteLine("[detached-stress] no scroll viewport found on the virtualization page — main window still runs frames, just without the scroll drive.");

        var rng = new Random(cfg.Seed);
        var mainFrameMs = new List<double>(cfg.Cycles * 32);
        var mainFenceWaitMs = new List<double>(cfg.Cycles * 32);
        var childOpenFps = new List<double>(cfg.Cycles * 16);
        var childOpenAllocBytes = new List<long>(cfg.Cycles * 16);

        float scrollOffset = 0f, scrollDir = 1f;
        const float ScrollStepPx = 16f;
        var sw = Stopwatch.StartNew();

        int opens = 0, closeCalls = 0, closeFailures = 0, closeTimeouts = 0, deviceLostRecoveries = 0, childRenderFailedLatches = 0;

        for (int cycle = 0; cycle < cfg.Cycles && !window.IsClosed; cycle++)
        {
            // Background load between cycles too — the isolation claim is that the MAIN window never slows down
            // for a child opening/closing, not just while one happens to be open.
            int prewaitFrames = rng.Next(4, 16);
            for (int f = 0; f < prewaitFrames && !window.IsClosed; f++)
                Step(host, canScroll, scrollNode, ref scrollOffset, ref scrollDir, ScrollStepPx, mainFrameMs, mainFenceWaitMs, null, null);

            var monitor = monitors[opens % monitors.Count];
            var size = new Size2(480, 270);
            RectF bounds = PlaceOnMonitor(monitor, size, opens);

            IDetachedVideoWindow? child = null;
            try { child = host.OpenDetachedWindow(new DetachedWindowRequest("stress", size, new StressChild(), InitialBoundsPx: bounds)); }
            catch (Exception ex) { Console.Error.WriteLine($"[detached-stress] OpenDetachedWindow threw on cycle {cycle}: {ex}"); }
            if (child is null)
            {
                Console.Error.WriteLine($"[detached-stress] OpenDetachedWindow returned null on cycle {cycle} (unsupported backend/state) — aborting early.");
                return 2;
            }
            opens++;
            bool renderFailedThisChild = false;
            child.OnRenderFailed = () => renderFailedThisChild = true;

            int holdMs = rng.Next(50, 1501);
            long holdStart = sw.ElapsedMilliseconds;
            while (sw.ElapsedMilliseconds - holdStart < holdMs && !window.IsClosed)
            {
                StressChild.Tick.Value++;   // the child repaints EVERY frame (a ticking seek bar): the F090 shape
                Step(host, canScroll, scrollNode, ref scrollOffset, ref scrollDir, ScrollStepPx, mainFrameMs, mainFenceWaitMs, childOpenFps, childOpenAllocBytes);
                host.TickDetachedHosts();
                if (child.RenderFailed) renderFailedThisChild = true;
            }
            if (renderFailedThisChild) childRenderFailedLatches++;

            try { child.Close(); }
            catch (Exception ex) { closeFailures++; Console.Error.WriteLine($"[detached-stress] Close() threw on cycle {cycle}: {ex}"); }
            closeCalls++;

            int reapBudget = 120;   // ~2s worth of frames at a slow 60fps floor
            while (child.IsOpen && reapBudget-- > 0 && !window.IsClosed)
            {
                host.RunFrame();
                host.TickDetachedHosts();
            }
            if (child.IsOpen)
            {
                closeTimeouts++;
                Console.Error.WriteLine($"[detached-stress] child #{opens} did not reap within budget after Close() on cycle {cycle}.");
            }

            if (gpu.NoteIfDeviceLost()) deviceLostRecoveries++;

            if ((cycle + 1) % 25 == 0 || cycle == cfg.Cycles - 1)
            {
                Console.Error.WriteLine(
                    $"[detached-stress] cycle={cycle + 1,4}/{cfg.Cycles} opens={opens} closeFailures={closeFailures} closeTimeouts={closeTimeouts} " +
                    $"childRenderFailed={childRenderFailedLatches} debugErrors={counters.DebugLayerErrors} forensicLines={counters.ForensicLines} " +
                    $"stallLines={counters.StallLines} childFrameFailedLines={counters.ChildFrameFailedLines} deviceLostRecoveries={deviceLostRecoveries}");
            }
        }

        double fpsMainP50 = Pct(mainFrameMs, 50) is { } ms50 && ms50 > 0 ? 1000.0 / ms50 : 0;
        double fenceP95 = Pct(mainFenceWaitMs, 95) ?? 0;
        double fpsChildOpenP50 = Pct(childOpenFps.ConvertAll(f => f), 50) ?? 0;   // already fps samples, not ms
        long allocMax = 0; foreach (var b in childOpenAllocBytes) if (b > allocMax) allocMax = b;

        bool passFps = fpsChildOpenP50 <= 0 || fpsChildOpenP50 >= 0.9 * primaryHz;   // 0 samples (no child-open window measured) never fails this leg
        // The render thread's present-slot wait is a running max (RenderPace): only growth past the pre-child baseline counts,
        // so a startup hiccup cannot fail (or hide) a pop-out stall. A repainting pop-out must never hold the thread > 100 ms.
        double slotWaitMaxMs = host.RenderPace.SlotWaitMaxMs;
        const double SlotWaitCeilingMs = 100.0;
        bool passSlotWait = slotWaitMaxMs <= SlotWaitCeilingMs || slotWaitMaxMs <= slotWaitMaxBaselineMs + 1.0;
        bool pass = counters.DebugLayerErrors == 0
            && counters.ForensicLines == 0
            && closeFailures == 0
            && closeTimeouts == 0
            && counters.ChildFrameFailedLines == 0
            && childRenderFailedLatches == 0
            && deviceLostRecoveries == 0
            && passFps
            && passSlotWait;

        string verdict = pass ? "PASS" : "FAIL";
        Console.Error.WriteLine(
            $"[detached-stress] cycles={cfg.Cycles} opens={opens} closes={closeCalls} closeFailures={closeFailures} closeTimeouts={closeTimeouts} " +
            $"childRenderFailed={childRenderFailedLatches} debugErrors={counters.DebugLayerErrors} forensicLines={counters.ForensicLines} " +
            $"stallLines={counters.StallLines} deviceLostRecoveries={deviceLostRecoveries} " +
            $"fps.main.p50={fpsMainP50:0.0} fps.childOpen.p50={fpsChildOpenP50:0.0} fenceWait.p95={fenceP95:0.00}ms " +
            $"slotWaitMax={slotWaitMaxMs:0.0}ms (baseline {slotWaitMaxBaselineMs:0.0}ms, ceiling {SlotWaitCeilingMs:0}ms) " +
            $"hotPhaseAllocBytes.max={allocMax} (stand-in for the render-thread-per-turn counter, §7.3 point 4 — not yet landed) " +
            $"verdict={verdict}");
        return pass ? 0 : 1;
    }

    /// <summary>One synthetic scroll tick + <see cref="AppHost.RunFrame"/>, folding the frame's stats into the
    /// running samples. When <paramref name="childOpenFps"/>/<paramref name="childOpenAllocBytes"/> are non-null
    /// (a child is currently held open), the sample also lands there — the "does the main window slow down while a
    /// child is open" measurement window from §7.3 point 4.</summary>
    private static void Step(
        AppHost host, bool canScroll, NodeHandle scrollNode, ref float offset, ref float dir, float step,
        List<double> frameMs, List<double> fenceWaitMs, List<double>? childOpenFps, List<long>? childOpenAllocBytes)
    {
        if (canScroll)
        {
            offset += dir * step;
            if (offset > 400_000f) { offset = 400_000f; dir = -1f; }   // a 100k-row list is far taller than any viewport; bounce well short of the true extent
            else if (offset < 0f) { offset = 0f; dir = 1f; }
            host.TryGetScrollHandle(scrollNode)?.ScrollTo(offset, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
        }
        var s = host.RunFrame();
        frameMs.Add(s.FrameMs > 0 ? s.FrameMs : 0);
        fenceWaitMs.Add(s.FenceWaitMs);
        if (childOpenFps is not null && s.PresentFps > 0) childOpenFps.Add(s.PresentFps);
        childOpenAllocBytes?.Add(s.HotPhaseAllocBytes);
    }

    private static NodeHandle FindScrollable(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n)) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindScrollable(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    private static RectF PlaceOnMonitor(RectF monitor, Size2 sizeDip, int index)
    {
        // Best-effort DIP≈px placement (the host clamps InitialBoundsPx to a visible monitor regardless — see
        // DetachedWindowRequest's doc comment — so precision here only affects where on the monitor it lands, not
        // whether it lands somewhere visible). Walks the window down-right a little per cycle so repeated opens are
        // visually distinguishable on a manual run without ever leaving the monitor's bounds.
        float pad = 32f;
        float jitter = (index % 5) * 24f;
        float w = sizeDip.Width, h = sizeDip.Height;
        float maxX = MathF.Max(monitor.X + pad, monitor.X + monitor.W - w - pad);
        float maxY = MathF.Max(monitor.Y + pad, monitor.Y + monitor.H - h - pad);
        float x = MathF.Min(monitor.X + pad + jitter, maxX);
        float y = MathF.Min(monitor.Y + pad + jitter, maxY);
        return new RectF(x, y, w, h);
    }

    /// <summary>p-th percentile (0..100), or null when there are no samples. Sorts a copy — this is a live probe run
    /// once at the end over a few hundred cycles' worth of samples, not a hot path.</summary>
    private static double? Pct(List<double> data, int p)
    {
        if (data.Count == 0) return null;
        var copy = data.ToArray();
        Array.Sort(copy);
        int idx = (int)Math.Round((p / 100.0) * (copy.Length - 1));
        return copy[Math.Clamp(idx, 0, copy.Length - 1)];
    }

    // ── stderr tee + line classification ────────────────────────────────────────────────────────────────────────────
    // The lines this probe must count are ALL always-on (never behind Diag.Enabled): "[d3d12.debug]" is written
    // directly to Console.Error by D3D12Device once the debug-layer option armed the info queue; "[d3d12.forensic]"
    // (RecordedOpRing, §2.6) and "[d3d12.stall]" go through Diag.Line, which routes to Console.Error when no sink is
    // installed (true here — the gallery never installs one); "[detached] child frame failed" is a direct
    // Console.Error.WriteLine in AppHost's child-submit catch. A single TextWriter tee over Console.Error therefore
    // sees every one of them without touching Diag/AppHost/D3D12Device.
    private sealed class Counters
    {
        public long DebugLayerErrors;
        public long ForensicLines;
        public long StallLines;
        public long ChildFrameFailedLines;

        public void Observe(string line)
        {
            if (line.StartsWith("[d3d12.debug]", StringComparison.Ordinal)
                && (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("CORRUPTION", StringComparison.Ordinal)))
                DebugLayerErrors++;
            else if (line.StartsWith("[d3d12.forensic]", StringComparison.Ordinal)) ForensicLines++;
            else if (line.StartsWith("[d3d12.stall]", StringComparison.Ordinal)) StallLines++;
            else if (line.StartsWith("[detached] child frame failed", StringComparison.Ordinal)) ChildFrameFailedLines++;
        }
    }

    private sealed class TeeTextWriter(TextWriter a, TextWriter b, Counters counters) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { if (value is null) return; a.Write(value); b.Write(value); }
        public override void WriteLine(string? value)
        {
            value ??= string.Empty;
            counters.Observe(value);
            a.WriteLine(value);
            b.WriteLine(value);
        }
        public override void Flush() { a.Flush(); b.Flush(); }
    }

    // ── monitor enumeration (local, self-contained — never a per-frame call: once per Drive()) ─────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref Win32Rect lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    private static List<RectF> EnumerateMonitorRects()
    {
        var rects = new List<RectF>(2);
        MonitorEnumProc callback = (nint _, nint _, ref Win32Rect r, nint _) =>
        {
            rects.Add(new RectF(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        if (rects.Count == 0) rects.Add(new RectF(0, 0, 1920, 1080));   // never expected on a real desktop session
        GC.KeepAlive(callback);
        return rects;
    }
}

/// <summary>The detached child's content: a rounded, clipped plate — the exact "rounded/path clip supplies
/// PushStencilClip in the main stream after the swap" shape (§1.3 of the implementation plan) that rode the
/// stencil-DSV incident, so the stress cycle keeps exercising that path on every open, not just a plain rect.</summary>
sealed class StressChild : Component
{
    /// <summary>Bumped by the probe every frame a child is open: the child's content changes every turn, so it submits and
    /// presents every turn like a pop-out with a ticking seek bar (the F090 stall needed exactly that).</summary>
    public static readonly Signal<int> Tick = new(0);

    public override Element Render() => new BoxEl
    {
        Grow = 1f,
        Direction = 1,
        Gap = 6f,
        Padding = Edges4.All(16f),
        ClipToBounds = true,
        Corners = CornerRadius4.All(16f),
        Fill = ColorF.FromRgba(30, 30, 38, 255),
        Children =
        [
            new TextEl("Detached stress child") { Size = 16f },
            new TextEl("stencil-clip plate — opened/closed on a timer") { Size = 12f },
            new BoxEl { Height = 4f, Width = 40f + Tick.Value % 200, Fill = ColorF.FromRgba(90, 160, 255, 255) },
        ],
    };
}
