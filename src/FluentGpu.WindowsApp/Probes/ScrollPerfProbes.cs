using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu;

/// <summary>
/// The two scroll performance probes (engine track 1, measurement). CLI arms, never environment switches; both drive
/// the REAL gallery window (launched straight onto the page) through <see cref="FluentApp.DiagnosticRun"/> (the same takeover <see cref="SoakProbe"/> and
/// <see cref="DetachedStressProbe"/> use), with the production frame loop shape — <c>RunFrame</c> then
/// <c>WaitForWork(WaitRequestWithDetached())</c> — so pacing is the app's own, not a busy loop.
/// <list type="bullet">
/// <item><c>--scroll-bench [page] --dipPerSec N --seconds S [--out dir]</c>: opens <c>page</c> (default
/// <c>virtualization</c>, the ListBound 100k list), writes ONE constant-velocity plan into the list's plan slot
/// (<see cref="ScrollHandle.AutoScroll"/> — a single closed-form segment, position linear in time), turns the device's
/// pass-granular GPU timeline on (<see cref="AppHost.GpuPassTimingEnabled"/>) and logs PER FRAME: UI frame / record /
/// capture ms, the repaint route + full reasons + capture path, and the retired GPU timeline (total + every pass). Ends
/// with a per-pass summary (mean / p50 / p95 by pass kind and target size) and route / reason histograms.</item>
/// <item><c>--scroll-soak [page] [--scale x] [--out dir]</c>: the sustained-touchpad soak. DirectManipulation-shaped
/// touchpad contacts (<see cref="ScrollSource.Touchpad"/> Begin / Sample at 120 Hz / End) are injected through
/// <see cref="Win32Window.EnqueueExternal"/> — the exact queue DM's producer feeds — on the timeline
/// 120 s scroll / 20 s idle / 20 s scroll / 60 s idle / 20 s scroll (each span × scale), logging one line per second:
/// UI frame p50/p95, capture ms + full/incremental, publishes, render presents by kind + skipped + race + MISSED motion
/// ticks, tick delta, the display clock's period / ignored / decimating / slot drops, present-slot wait, present-queue
/// depth, GPU execution ms, the GPU governor, GC counts / pause / allocated bytes, scene store live / capacity, snapshot
/// capacity, image-cache entries, and a CPU canary (a fixed workload timed on the UI thread and on a worker — its time
/// rising under a constant workload is the thermal clock-drop signal).</item>
/// </list>
/// <c>--vertical</c> (bench): scroll the page's largest VERTICAL viewport instead of the one with the most items (a page
/// whose shelves are the virtual lists, e.g. <c>artist-bench</c>).
/// Knockout switches (both probes): <c>--edge-fades-off</c>, <c>--freeze-uploads</c>, <c>--force-full-direct</c>,
/// <c>--clear-only</c>, <c>--group-fades</c> (every fade over child slices composites as a group — the identity control of
/// the distributed feather) → <see cref="AppHost.GpuKnockouts"/>. Output lands in <c>--out</c> (default
/// <c>.tmp/scroll-bench</c> / <c>.tmp/scroll-soak</c>); the resolved path is printed at start.
/// </summary>
internal static class ScrollPerfProbes
{
    public sealed record BenchConfig(string Page, double DipPerSec, double Seconds, string OutDir, GpuKnockouts Knockouts, bool Vertical = false);
    public sealed record SoakConfig(string Page, double Scale, string OutDir, GpuKnockouts Knockouts);

    public static BenchConfig? Bench;
    /// <summary>The gallery page the probe runs on (Program.cs launches the shell straight onto it).</summary>
    public static string Page => Bench?.Page ?? Soak?.Page ?? "virtualization";
    public static SoakConfig? Soak;
    /// <summary>Read by Program.cs after the run returns (the delegate runs inside FluentApp.Run, before teardown).</summary>
    public static int ExitCode;

    /// <summary>Parses the probe arms out of <paramref name="args"/>. Returns true when one was requested.</summary>
    public static bool Parse(string[] args)
    {
        int bench = Array.IndexOf(args, "--scroll-bench"), soak = Array.IndexOf(args, "--scroll-soak");
        if (bench < 0 && soak < 0) return false;
        GpuKnockouts k = GpuKnockouts.None;
        if (Array.IndexOf(args, "--edge-fades-off") >= 0) k |= GpuKnockouts.EdgeFadesOff;
        if (Array.IndexOf(args, "--freeze-uploads") >= 0) k |= GpuKnockouts.FreezeUploads;
        if (Array.IndexOf(args, "--force-full-direct") >= 0) k |= GpuKnockouts.ForceFullDirect;
        if (Array.IndexOf(args, "--clear-only") >= 0) k |= GpuKnockouts.ClearOnly;
        if (Array.IndexOf(args, "--group-fades") >= 0) k |= GpuKnockouts.GroupFades;
        string? outDir = Value(args, "--out");
        int at = bench >= 0 ? bench : soak;
        string page = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : "virtualization";
        if (bench >= 0)
        {
            double v = double.TryParse(Value(args, "--dipPerSec"), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? dv : 3000.0;
            double s = double.TryParse(Value(args, "--seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ds) ? ds : 10.0;
            Bench = new BenchConfig(page, v, s, outDir ?? ".tmp/scroll-bench", k, Vertical: Array.IndexOf(args, "--vertical") >= 0);
        }
        else
        {
            double scale = double.TryParse(Value(args, "--scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sc) ? sc : 1.0;
            Soak = new SoakConfig(page, scale, outDir ?? ".tmp/scroll-soak", k);
        }
        return true;
    }

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary><see cref="FluentApp.DiagnosticRun"/> adapter: run the requested probe, else decline.</summary>
    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (Bench is null && Soak is null) return false;
        try
        {
            ExitCode = Bench is { } b ? RunBench(b, host, window, device) : RunSoak(Soak!, host, window);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scroll-probe] failed: {ex}");
            ExitCode = 3;
        }
        return true;
    }

    // ── shared ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One production-shaped loop turn: a frame, the detached hosts, then the host's own wait.</summary>
    private static FrameStats Turn(AppHost host, IPlatformWindow window)
    {
        var s = host.RunFrame();
        host.TickDetachedHosts();
        window.WaitForWork(host.WaitRequestWithDetached());
        return s;
    }

    private static void TurnFor(AppHost host, IPlatformWindow window, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds && !window.IsClosed) Turn(host, window);
    }

    /// <summary>Navigates the gallery shell to <paramref name="page"/> and returns the scroll viewport with the most
    /// items (the virtualized list), or Null.</summary>
    private static NodeHandle OpenListPage(AppHost host, IPlatformWindow window, string page, bool vertical = false)
    {
        // The gallery was launched straight onto the page (Program.cs: GalleryShell.InitialPage = Page) — no nav hook.
        TurnFor(host, window, 2.0);
        var scene = host.Scene;
        NodeHandle best = NodeHandle.Null;
        int bestItems = -1;
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (h.IsNull || !scene.IsLive(h) || !scene.TryGetScroll(h, out var sc)) continue;
            if ((scene.Flags(h) & NodeFlags.Parked) != 0) continue;
            if (vertical)
            {
                // --vertical: the page scroller (the vertical viewport with the largest content), not a shelf.
                if (sc.Orientation == 1) continue;
                int extent = (int)sc.ContentH;
                if (extent > bestItems) { bestItems = extent; best = h; }
                continue;
            }
            if (sc.ItemCount > bestItems) { bestItems = sc.ItemCount; best = h; }
        }
        Console.Error.WriteLine($"[scroll-probe] page={page} listItems={bestItems}");
        return best;
    }

    private static StreamWriter OpenOut(string dir, string file)
    {
        Directory.CreateDirectory(dir);
        string path = Path.GetFullPath(Path.Combine(dir, file));
        Console.Error.WriteLine($"[scroll-probe] output={path}");
        return new StreamWriter(File.Create(path), new UTF8Encoding(false)) { AutoFlush = false };
    }

    private static double Pct(List<double> data, double p)
    {
        if (data.Count == 0) return 0;
        var copy = data.ToArray();
        Array.Sort(copy);
        int idx = (int)Math.Round(p / 100.0 * (copy.Length - 1));
        return copy[Math.Clamp(idx, 0, copy.Length - 1)];
    }

    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    // ── bench ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static int RunBench(BenchConfig cfg, AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        using var csv = OpenOut(cfg.OutDir, "scroll-bench.csv");
        var list = OpenListPage(host, window, cfg.Page, cfg.Vertical);
        if (list.IsNull) return 2;
        var handle = host.TryGetScrollHandle(list);
        if (handle is null) return 2;
        host.GpuKnockouts = cfg.Knockouts;
        host.GpuPassTimingEnabled = true;
        handle.ScrollTo(cfg.DipPerSec >= 0 ? 1000.0 : handle.MaxOffset - 1000.0, ScrollMove.Immediate);
        TurnFor(host, window, 1.0);   // settle at the start position; the timeline warms up

        csv.WriteLine("frame,tMs,uiFrameMs,recordMs,captureMs,captureIncremental,captureFullReason,uiRepaintFullReason,route,deviceFullReason,coveragePct,featherItems,offscreenPx,offscreenSurfaces,passBreaks,draws,glyphs,uploadBytes,gpuSeq,gpuWholeMs,passes");
        var passes = new GpuPassTiming[GpuPassTimeline.MaxPasses];
        var byPass = new Dictionary<string, List<double>>();
        var whole = new List<double>();
        var uiMs = new List<double>();
        var routes = new Dictionary<string, int>();
        var reasons = new Dictionary<string, int>();
        var captureReasons = new Dictionary<string, int>();
        int captures = 0, incremental = 0;
        ulong lastSeq = 0;
        // retained-tile census, per composite turn (dedup by turn)
        int lastTurn = int.MinValue, turns = 0, tilesMax = 0, exposedMissing = 0, staleTurns = 0, degradedMax = 0, slicesMax = 0;
        long tilesSum = 0, residentMax = 0, budget = 0;
        var tileReasons = new long[10];
        long offSurfSum = 0, offPxSum = 0, featherSum = 0, featherPxSum = 0, directSum = 0, inlineSum = 0; int offFrames = 0;
        // gate 0 (composite-fade-groups §4): the per-kind offscreen split + the slice partition + the group cache
        long spGroupN = 0, spGroupPx = 0, spGroupHits = 0, spBlurN = 0, spBlurPx = 0, spBlurHits = 0, spBdN = 0, spBdPx = 0, spBdHits = 0,
             spDirN = 0, spDirPx = 0, spInlN = 0, spInlPx = 0;
        int effMax = 0, foldMax = 0, freeFadeMax = 0, acrMax = 0, acrFallbackMax = 0;
        long visibleNeedMax = 0, retainedMax = 0, groupSurfSum = 0, groupHitSum = 0;
        var d3d = device as FluentGpu.Rhi.D3D12.D3D12Device;
        long presented0 = (long)host.PresentedSequence;
        var pace0 = host.RenderPace;

        handle.AutoScroll(cfg.DipPerSec);
        var sw = Stopwatch.StartNew();
        int frame = 0;
        var line = new StringBuilder(512);
        while (sw.Elapsed.TotalSeconds < cfg.Seconds && !window.IsClosed)
        {
            var s = Turn(host, window);
            frame++;
            var c = s.RenderCensus;
            int n = host.CopyGpuPassTimeline(passes, out GpuPassFrameSummary sum);
            bool freshGpu = n > 0 && sum.Sequence != lastSeq;
            line.Clear();
            line.Append(frame).Append(',').Append(F(sw.Elapsed.TotalMilliseconds)).Append(',').Append(F(s.FrameMs)).Append(',')
                .Append(F(c.RecordMs)).Append(',').Append(F(c.CaptureMs)).Append(',').Append(c.CaptureIncremental ? 1 : 0).Append(',')
                .Append(c.CaptureFullReason).Append(',').Append(c.RepaintFullReason).Append(',').Append(c.Device.Route).Append(',')
                .Append(c.Device.FullReason).Append(',').Append(F(c.Device.CoveragePct)).Append(',').Append(c.Device.FeatherItems).Append(',')
                .Append(c.Device.OffscreenPx).Append(',').Append(c.Device.OffscreenSurfaces).Append(',').Append(c.Device.PassBreaks).Append(',')
                .Append(c.Device.Draws).Append(',').Append(c.Device.GlyphInstances).Append(',').Append(c.Device.UploadBytes).Append(',')
                .Append(freshGpu ? sum.Sequence : 0).Append(',').Append(freshGpu ? F(sum.WholeMs) : "").Append(',');
            if (freshGpu)
            {
                lastSeq = sum.Sequence;
                whole.Add(sum.WholeMs);
                for (int i = 0; i < n; i++)
                {
                    ref readonly var p = ref passes[i];
                    string key = $"{p.Kind}@{p.TargetWidthPx}x{p.TargetHeightPx}";
                    if (!byPass.TryGetValue(key, out var l)) byPass[key] = l = new List<double>();
                    l.Add(p.Ms);
                    if (i > 0) line.Append(' ');
                    line.Append(key).Append(':').Append(F(p.Ms));
                }
            }
            csv.WriteLine(line.ToString());
            if (c.Device.Route == RepaintRoute.Composite)
            {
                directSum += d3d?.LastDirectRegions ?? 0; inlineSum += d3d?.LastInlineGroups ?? 0;
                offFrames++; offSurfSum += c.Device.OffscreenSurfaces; offPxSum += c.Device.OffscreenPx; featherSum += c.Device.FeatherItems; featherPxSum += c.Device.FeatherPx;
                if (d3d is not null)
                {
                    var sp = d3d.LastOffscreenSplit;
                    spGroupN += sp.GroupSurfaces; spGroupPx += sp.GroupPx; spGroupHits += sp.GroupCacheHits;
                    spBlurN += sp.LeafBlurSurfaces; spBlurPx += sp.LeafBlurPx; spBlurHits += sp.LeafBlurHits;
                    spBdN += sp.BackdropSurfaces; spBdPx += sp.BackdropPx; spBdHits += sp.BackdropHits;
                    spDirN += sp.DirectSurfaces; spDirPx += sp.DirectPx; spInlN += sp.InlineSurfaces; spInlPx += sp.InlinePx;
                }
            }
            var tc = host.LastTileCensus;
            if (tc.Turn != lastTurn && tc.Turn != 0)
            {
                lastTurn = tc.Turn; turns++;
                tilesSum += tc.Rastered; tilesMax = Math.Max(tilesMax, tc.Rastered);
                exposedMissing += tc.ExposedMissing; staleTurns += tc.StaleTiles > 0 ? 1 : 0; degradedMax = Math.Max(degradedMax, tc.DegradedSlices);
                slicesMax = Math.Max(slicesMax, tc.Slices); residentMax = Math.Max(residentMax, tc.ResidentBytes); budget = tc.BudgetBytes;
                tileReasons[1] += tc.NoTexture; tileReasons[2] += tc.Content; tileReasons[3] += tc.PrimCount; tileReasons[4] += tc.ValidRectChanged;
                tileReasons[5] += tc.ScaleChanged; tileReasons[6] += tc.SliceGeometry; tileReasons[7] += tc.BackgroundOrTheme;
                tileReasons[8] += tc.EvictedReason; tileReasons[9] += tc.Degraded;
                effMax = Math.Max(effMax, tc.EffectSlices); foldMax = Math.Max(foldMax, tc.Folded); freeFadeMax = Math.Max(freeFadeMax, tc.FreeFades);
                acrMax = Math.Max(acrMax, tc.AcrylicSlices); acrFallbackMax = Math.Max(acrFallbackMax, tc.AcrylicFallbacks);
                visibleNeedMax = Math.Max(visibleNeedMax, tc.VisibleNeedBytes); retainedMax = Math.Max(retainedMax, tc.RetainedBytes);
                groupSurfSum += tc.GroupSurfaces; groupHitSum += tc.GroupCacheHits;
            }
            if (s.FrameMs > 0) uiMs.Add(s.FrameMs);
            Count(routes, c.Device.Route.ToString());
            if (c.Device.FullReason != RepaintFullReason.None) Count(reasons, "device:" + c.Device.FullReason);
            if (c.RepaintFullReason != RepaintFullReason.None) Count(reasons, "ui:" + c.RepaintFullReason);
            if (s.PublishSeq != 0)
            {
                captures++;
                if (c.CaptureIncremental) incremental++; else Count(captureReasons, c.CaptureFullReason.ToString());
            }
        }
        handle.ScrollTo(handle.LastShown, ScrollMove.Immediate);
        var pace1 = host.RenderPace;
        TurnFor(host, window, 0.5);
        host.GpuPassTimingEnabled = false;
        host.GpuKnockouts = GpuKnockouts.None;
        csv.Flush();

        var summary = new StringBuilder();
        double secs = sw.Elapsed.TotalSeconds;
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] page={cfg.Page} dipPerSec={cfg.DipPerSec} seconds={secs:0.0} knockouts={cfg.Knockouts} frames={frame} presents/s={((long)host.PresentedSequence - presented0) / secs:0.0} ui.p50={Pct(uiMs, 50):0.00} ui.p95={Pct(uiMs, 95):0.00}"));
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] render fresh={pace1.FreshPresents - pace0.FreshPresents} motion={pace1.MotionPresents - pace0.MotionPresents} skipped={pace1.SkippedTicks - pace0.SkippedTicks} race={pace1.RaceHits - pace0.RaceHits} missedTicks={pace1.MissedMotionTicks - pace0.MissedMotionTicks} ticks={pace1.TickSeq - pace0.TickSeq} depth={pace1.PresentQueueDepth} clockPeriodMs={pace1.ClockPeriodMs:0.000}"));
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] gpu frames={whole.Count} whole.mean={Mean(whole):0.000} whole.p50={Pct(whole, 50):0.000} whole.p95={Pct(whole, 95):0.000}"));
        var keys = new List<string>(byPass.Keys);
        keys.Sort((a, b) => Sum(byPass[b]).CompareTo(Sum(byPass[a])));   // biggest per-frame share first
        foreach (var key in keys)
        {
            var l = byPass[key];
            summary.AppendLine(FormattableString.Invariant(
                $"[scroll-bench] pass {key,-32} n={l.Count,5} perFrame={(whole.Count == 0 ? 0 : Sum(l) / whole.Count):0.000} mean={Mean(l):0.000} p50={Pct(l, 50):0.000} p95={Pct(l, 95):0.000}"));
        }
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] tiles turns={turns} rastered/turn mean={(turns == 0 ? 0 : (double)tilesSum / turns):0.00} max={tilesMax} exposedMissing={exposedMissing} staleTurns={staleTurns} degradedSlicesMax={degradedMax} slicesMax={slicesMax} residentMaxMiB={residentMax / 1048576.0:0.0} budgetMiB={budget / 1048576.0:0.0}"));
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] tile reasons noTexture={tileReasons[1]} content={tileReasons[2]} primCount={tileReasons[3]} validRect={tileReasons[4]} scale={tileReasons[5]} geometry={tileReasons[6]} theme={tileReasons[7]} evicted={tileReasons[8]} degraded={tileReasons[9]}"));
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] offscreen perFrame surfaces={(offFrames == 0 ? 0 : (double)offSurfSum / offFrames):0.00} px={(offFrames == 0 ? 0 : (double)offPxSum / offFrames):0} featherItems={(offFrames == 0 ? 0 : (double)featherSum / offFrames):0.00} featherPx={(offFrames == 0 ? 0 : (double)featherPxSum / offFrames):0} directRegions={(offFrames == 0 ? 0 : (double)directSum / offFrames):0.00} inlineLayers={(offFrames == 0 ? 0 : (double)inlineSum / offFrames):0.00}"));
        double of = offFrames == 0 ? 1 : offFrames;
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] offscreen split perFrame groups={spGroupN / of:0.00} ({spGroupPx / of / 1e6:0.00} Mpx, hits={spGroupHits / of:0.00}) leafBlur={spBlurN / of:0.00} ({spBlurPx / of / 1e6:0.00} Mpx, hits={spBlurHits / of:0.00}) backdrop={spBdN / of:0.00} ({spBdPx / of / 1e6:0.00} Mpx, hits={spBdHits / of:0.00}) direct={spDirN / of:0.00} ({spDirPx / of / 1e6:0.00} Mpx) inline={spInlN / of:0.00} ({spInlPx / of / 1e6:0.00} Mpx)"));
        summary.AppendLine(FormattableString.Invariant(
            $"[scroll-bench] slices effectSlicesMax={effMax} foldedMax={foldMax} freeFadesMax={freeFadeMax} acrylicSlicesMax={acrMax} acrylicFallbacksMax={acrFallbackMax} visibleNeedMaxMiB={visibleNeedMax / 1048576.0:0.0} budgetMiB={budget / 1048576.0:0.0} groupSurfaces/turn={(turns == 0 ? 0 : (double)groupSurfSum / turns):0.00} groupCacheHits/turn={(turns == 0 ? 0 : (double)groupHitSum / turns):0.00} retainedMaxMiB={retainedMax / 1048576.0:0.0}"));
        summary.AppendLine("[scroll-bench] routes " + Hist(routes));
        summary.AppendLine("[scroll-bench] fullReasons " + Hist(reasons));
        summary.AppendLine(FormattableString.Invariant($"[scroll-bench] captures={captures} incremental={incremental} fullReasons ") + Hist(captureReasons));
        string text = summary.ToString();
        Console.Error.Write(text);
        File.WriteAllText(Path.Combine(cfg.OutDir, "scroll-bench-summary.txt"), text);
        return 0;
    }

    private static double Mean(List<double> l) { double s = 0; foreach (var v in l) s += v; return l.Count == 0 ? 0 : s / l.Count; }
    private static double Sum(List<double> l) { double s = 0; foreach (var v in l) s += v; return s; }
    private static void Count(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1;
    private static string Hist(Dictionary<string, int> d)
    {
        var sb = new StringBuilder();
        foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
        return sb.Length == 0 ? "(none)" : sb.ToString().TrimEnd();
    }

    // ── soak ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fixed CPU canary workload: a deterministic integer hash loop (no allocation, no memory traffic). Its
    /// wall time rises only when the core runs slower — thermal / power clock drops, or contention.</summary>
    private static ulong CanaryWork()
    {
        ulong x = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < 400_000; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; }
        return x;
    }

    private static double TimeCanary()
    {
        long t0 = Stopwatch.GetTimestamp();
        ulong r = CanaryWork();
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return r == 42 ? ms + 1e-9 : ms;   // keep the result live
    }

    private static int RunSoak(SoakConfig cfg, AppHost host, IPlatformWindow window)
    {
        if (window is not Win32Window w32) { Console.Error.WriteLine("[scroll-soak] needs the Win32 window (DM-shaped injection)."); return 2; }
        using var log = OpenOut(cfg.OutDir, "scroll-soak.log");
        var list = OpenListPage(host, window, cfg.Page);
        if (list.IsNull) return 2;
        var handle = host.TryGetScrollHandle(list);
        if (handle is null) return 2;
        host.GpuKnockouts = cfg.Knockouts;
        handle.ScrollTo(handle.MaxOffset / 2, ScrollMove.Immediate);   // start mid-list: alternating gestures never pin at an edge
        TurnFor(host, window, 1.0);

        // The contact point: the list viewport's center, in DIP.
        var r = host.Scene.AbsoluteRect(list);
        var pointer = new Point2(r.X + r.W * 0.5f, r.Y + r.H * 0.5f);

        // Worker canary.
        double workerCanaryMs = 0;
        bool stop = false;
        var worker = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                Volatile.Write(ref workerCanaryMs, TimeCanary());
                Thread.Sleep(1000);
            }
        }) { IsBackground = true, Name = "scroll-soak-canary" };
        worker.Start();

        (bool Scroll, double Seconds)[] timeline = [(true, 120), (false, 20), (true, 20), (false, 60), (true, 20)];
        log.WriteLine("t,phase,uiP50,uiP95,frames,publishes,captureMsMean,captureMsMax,captureFull,captureIncr,fresh,motion,skipped,race,missed,ticks,clockPeriodMs,clockIgnored,clockDecimating,clockSlotDrops,slotWaitAvgMs,slotWaitMaxMs,depth,gpuExecMs,governorEma,governorEngaged,waitKind,gc0,gc1,gc2,gcPauseMs,allocMB,sceneLive,sceneCapacity,snapshotCapacity,imageEntries,imageReady,canaryUiMs,canaryWorkerMs");

        var uiMs = new List<double>(256);
        double capSum = 0, capMax = 0;
        int frames = 0, publishes = 0, capFull = 0, capIncr = 0;
        var pace0 = host.RenderPace;
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        long alloc0 = GC.GetTotalAllocatedBytes(false);
        var total = Stopwatch.StartNew();
        double nextReport = 1.0;
        int second = 0;
        long qpcFreq = Stopwatch.Frequency;
        uint contactId = 0x5C0;

        foreach (var (scroll, spanSeconds) in timeline)
        {
            double span = spanSeconds * cfg.Scale;
            var seg = Stopwatch.StartNew();
            // Gesture state: contacts of ~1.2 s, alternating direction, samples at 120 Hz on the contact's own lattice.
            bool contact = false;
            double contactStart = 0;
            long samplesSent = 0;
            float dir = 1f;
            long contactT0 = 0;
            while (seg.Elapsed.TotalSeconds < span && !window.IsClosed)
            {
                if (scroll)
                {
                    double t = seg.Elapsed.TotalSeconds;
                    if (!contact && t - contactStart >= 0.0)
                    {
                        contact = true; contactStart = t; samplesSent = 0; contactT0 = Stopwatch.GetTimestamp();
                        w32.EnqueueExternal(InputEvent.ForScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.Begin,
                            contactT0, pointer, 0f, 0f, contactId, KeyModifiers.None), PointerKind.Touchpad, unchecked((uint)Environment.TickCount64)));
                    }
                    double ct = t - contactStart;
                    const double ContactS = 1.2, SampleHz = 120.0;
                    long due = (long)(Math.Min(ct, ContactS) * SampleHz);
                    while (samplesSent < due)
                    {
                        samplesSent++;
                        double st = samplesSent / SampleHz;
                        // A smooth finger: a raised-cosine speed profile peaking at ~2400 DIP/s mid-contact.
                        double speed = 2400.0 * 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * st / ContactS));
                        float dy = (float)(dir * speed / SampleHz);
                        long qpc = contactT0 + (long)(st * qpcFreq);
                        w32.EnqueueExternal(InputEvent.ForScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.Sample,
                            qpc, pointer, 0f, dy, contactId, KeyModifiers.None), PointerKind.Touchpad, unchecked((uint)Environment.TickCount64)));
                    }
                    if (ct >= ContactS)
                    {
                        w32.EnqueueExternal(InputEvent.ForScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.End,
                            Stopwatch.GetTimestamp(), pointer, 0f, 0f, contactId, KeyModifiers.None), PointerKind.Touchpad, unchecked((uint)Environment.TickCount64)));
                        contact = false;
                        contactStart = t + 0.25;   // a quarter-second between swipes (the fling coasts meanwhile)
                        dir = -dir;
                        contactId++;
                    }
                }
                var s = Turn(host, window);
                frames++;
                if (s.FrameMs > 0) uiMs.Add(s.FrameMs);
                if (s.PublishSeq != 0)
                {
                    publishes++;
                    capSum += s.SubmitCaptureMs;
                    if (s.SubmitCaptureMs > capMax) capMax = s.SubmitCaptureMs;
                    if (s.RenderCensus.CaptureIncremental) capIncr++; else capFull++;
                }
                if (total.Elapsed.TotalSeconds >= nextReport)
                {
                    nextReport += 1.0;
                    second++;
                    var pace = host.RenderPace;
                    var census = CensusSnapshot.Capture(host);
                    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                    TimeSpan pause = GC.GetTotalPauseDuration();
                    long alloc = GC.GetTotalAllocatedBytes(false);
                    long slotN = pace.SlotWaitCount - pace0.SlotWaitCount;
                    double slotAvg = slotN == 0 ? 0 : (pace.SlotWaitSumMs - pace0.SlotWaitSumMs) / slotN;
                    string lineOut = string.Join(',',
                        second.ToString(CultureInfo.InvariantCulture), scroll ? "scroll" : "idle",
                        F(Pct(uiMs, 50)), F(Pct(uiMs, 95)), frames.ToString(CultureInfo.InvariantCulture), publishes.ToString(CultureInfo.InvariantCulture),
                        F(publishes == 0 ? 0 : capSum / publishes), F(capMax), capFull.ToString(CultureInfo.InvariantCulture), capIncr.ToString(CultureInfo.InvariantCulture),
                        (pace.FreshPresents - pace0.FreshPresents).ToString(CultureInfo.InvariantCulture),
                        (pace.MotionPresents - pace0.MotionPresents).ToString(CultureInfo.InvariantCulture),
                        (pace.SkippedTicks - pace0.SkippedTicks).ToString(CultureInfo.InvariantCulture),
                        (pace.RaceHits - pace0.RaceHits).ToString(CultureInfo.InvariantCulture),
                        (pace.MissedMotionTicks - pace0.MissedMotionTicks).ToString(CultureInfo.InvariantCulture),
                        (pace.TickSeq - pace0.TickSeq).ToString(CultureInfo.InvariantCulture),
                        F(pace.ClockPeriodMs), (pace.ClockIgnoredReturns - pace0.ClockIgnoredReturns).ToString(CultureInfo.InvariantCulture),
                        pace.ClockDecimating ? "1" : "0", (pace.ClockSlotDrops - pace0.ClockSlotDrops).ToString(CultureInfo.InvariantCulture),
                        F(slotAvg), F(pace.SlotWaitMaxMs), pace.PresentQueueDepth.ToString(CultureInfo.InvariantCulture),
                        F(pace.GpuExecutionMs), F(pace.GovernorEmaMs), pace.GovernorEngaged ? "1" : "0", pace.LastWaitKind.ToString(),
                        (g0 - gc0).ToString(CultureInfo.InvariantCulture), (g1 - gc1).ToString(CultureInfo.InvariantCulture), (g2 - gc2).ToString(CultureInfo.InvariantCulture),
                        F((pause - pause0).TotalMilliseconds), F((alloc - alloc0) / (1024.0 * 1024.0)),
                        census.SceneLive.ToString(CultureInfo.InvariantCulture), census.SceneCapacity.ToString(CultureInfo.InvariantCulture),
                        census.SnapshotCapacity.ToString(CultureInfo.InvariantCulture), census.ImageCount.ToString(CultureInfo.InvariantCulture),
                        census.ImageReady.ToString(CultureInfo.InvariantCulture),
                        F(TimeCanary()), F(Volatile.Read(ref workerCanaryMs)));
                    log.WriteLine(lineOut);
                    log.Flush();
                    Console.Error.WriteLine("[scroll-soak] " + lineOut);
                    uiMs.Clear(); capSum = 0; capMax = 0; frames = 0; publishes = 0; capFull = 0; capIncr = 0;
                    pace0 = pace; gc0 = g0; gc1 = g1; gc2 = g2; pause0 = pause; alloc0 = alloc;
                }
            }
            if (contact)
                w32.EnqueueExternal(InputEvent.ForScroll(new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.End,
                    Stopwatch.GetTimestamp(), pointer, 0f, 0f, contactId, KeyModifiers.None), PointerKind.Touchpad, unchecked((uint)Environment.TickCount64)));
        }
        Volatile.Write(ref stop, true);
        host.GpuKnockouts = GpuKnockouts.None;
        Console.Error.WriteLine($"[scroll-soak] done seconds={total.Elapsed.TotalSeconds:0.0}");
        return 0;
    }
}
