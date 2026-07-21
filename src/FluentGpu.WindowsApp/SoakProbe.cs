using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace FluentGpu;

/// <summary>
/// Longevity / soak + targeted-stress harness for native-memory & responsiveness regression hunting. Drives the REAL
/// app (gallery) through a long, mixed, deterministic workload — page navigation churn (mount/unmount + WIC decode +
/// cache residency) interleaved with window resizes — while sampling, per interval: frame-time percentiles
/// (responsiveness), GC collection deltas + allocation rate (churn), and working-set / private / managed / tracked-GPU
/// memory (leak trend). At the end it linear-fits the second-half memory samples and EXTRAPOLATES the slope to an
/// hour/day human session, so "what happens over 1–2 hrs / days" is answered from a few minutes of compressed load
/// without waiting. Modes (one per env flag, evaluated by FluentApp):
///   FG_SOAK=1            mixed longevity soak (nav + resize)         — FG_SOAK_ACTIONS, FG_SOAK_FPA, FG_SOAK_REPORT
///   FG_STRESS_RESIZE=1   resize-only stress                          — FG_STRESS_ITERS
///   FG_STRESS_NAV=1      navigation-only churn                       — FG_STRESS_CYCLES
/// All write a "[soak]"/"[stress]" trace to stderr; pair with FG_D3D_MEM=1 for the per-resource create/release trace.
/// </summary>
internal static class SoakProbe
{
    /// <summary>
    /// Adapter for <see cref="FluentGpu.FluentApp.DiagnosticRun"/>: dispatch to the env-selected soak / stress mode and
    /// report whether it took over the run (the interactive frame loop is then skipped). Wired in <c>Program.Main</c>.
    /// Lives in the gallery (not the engine) because the modes drive <c>GalleryShell</c>'s nav hook.
    /// </summary>
    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (window is not Win32Window w || device is not D3D12Device gpu) return false;
        if (Diag.EnvFlag("FG_SOAK"))          { RunSoak(host, w, gpu);      return true; }
        if (Diag.EnvFlag("FG_STRESS_RESIZE")) { RunResize(host, w, gpu);    return true; }
        if (Diag.EnvFlag("FG_STRESS_NAV"))    { RunNav(host, w, gpu);       return true; }
        if (Diag.EnvFlag("FG_WAKE_AUDIT"))    { RunWakeAudit(host, w, gpu); return true; }
        return false;
    }
    // ── mixed longevity soak ────────────────────────────────────────────────────────────────────────────────────────
    public static void RunSoak(AppHost host, Win32Window window, D3D12Device gpu)
    {
        int actions = EnvInt("FG_SOAK_ACTIONS", 4000);
        int fpa = EnvInt("FG_SOAK_FPA", 4);          // frames rendered per simulated user action
        int report = EnvInt("FG_SOAK_REPORT", 200);  // emit a sample row every N actions
        var proc = Process.GetCurrentProcess();

        for (int i = 0; i < 30 && !window.IsClosed; i++) host.RunFrame();   // warm up + let the gallery mount (sets the nav hook)
        var nav = GalleryShell.StressNavigate;
        var keys = GalleryShell.StressNavKeys;
        bool canNav = nav is not null && keys.Length > 0;

        var frameMs = new double[report * fpa + 16];
        int fm = 0, navIdx = 0, totalFrames = 0;
        long gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        long alloc0 = GC.GetTotalAllocatedBytes(false);
        var sampleAction = new List<double>();
        var sampleWs = new List<double>();
        double firstQp99 = 0, lastQp99 = 0;

        Console.Error.WriteLine($"[soak] start: {actions} actions x {fpa} frames/action  (nav={(canNav ? keys.Length + " pages" : "OFF")}, +resize every 7th)");
        Console.Error.WriteLine("[soak]   action |   ws   priv  mng   gpu(MB xN)  | frame p50/p95/p99/max (ms) | gc d0/d1/d2  alloc KB/frame");
        long tStart = Stopwatch.GetTimestamp();

        for (int a = 0; a < actions && !window.IsClosed; a++)
        {
            if (canNav && a % 7 != 0) { nav!(keys[navIdx++ % keys.Length]); }
            else { bool big = (a & 1) == 0; window.SetClientSize(big ? 1400 : 980, big ? 980 : 680); }

            for (int f = 0; f < fpa && !window.IsClosed; f++)
            {
                long fs = Stopwatch.GetTimestamp();
                host.RunFrame();
                totalFrames++;
                double ms = (Stopwatch.GetTimestamp() - fs) * 1000.0 / Stopwatch.Frequency;
                if (fm < frameMs.Length) frameMs[fm++] = ms;
            }

            if ((a + 1) % report == 0)
            {
                proc.Refresh();
                double p50 = Pct(frameMs, fm, 50), p95 = Pct(frameMs, fm, 95), p99 = Pct(frameMs, fm, 99), pmax = Pct(frameMs, fm, 100);
                long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                long alloc = GC.GetTotalAllocatedBytes(false);
                double kbPerFrame = (alloc - alloc0) / 1024.0 / Math.Max(1, fm);
                double wsMb = proc.WorkingSet64 / 1048576.0;
                var (gb, gc) = gpu.DiagResourceTotals;

                sampleAction.Add(a + 1); sampleWs.Add(wsMb);
                if (a + 1 <= actions / 4) firstQp99 = Math.Max(firstQp99, p99);
                if (a + 1 > actions * 3 / 4) lastQp99 = Math.Max(lastQp99, p99);

                Console.Error.WriteLine(
                    $"[soak] {a + 1,8} | {wsMb,5:0.0} {proc.PrivateMemorySize64 / 1048576.0,5:0.0} {GC.GetTotalMemory(false) / 1048576.0,4:0.0} {gb / 1048576.0,5:0.0}x{gc,-4}| " +
                    $"{p50,5:0.00}/{p95,5:0.00}/{p99,6:0.00}/{pmax,6:0.0} | {g0 - gc0,2}/{g1 - gc1,2}/{g2 - gc2,2}  {kbPerFrame,6:0.0}");

                gc0 = g0; gc1 = g1; gc2 = g2; alloc0 = alloc; fm = 0;
            }
        }

        for (int i = 0; i < 240 && !window.IsClosed; i++) host.RunFrame();   // settle: let deferred releases drain
        proc.Refresh();
        double sec = (Stopwatch.GetTimestamp() - tStart) / (double)Stopwatch.Frequency;
        double slopePerK = Slope(sampleAction, sampleWs, 0.5) * 1000.0;   // MB per 1000 actions, 2nd-half fit
        double perHour = slopePerK * 1.8;     // a human does ~1 action / 2s ⇒ ~1800 actions/hr
        string memVerdict = Math.Abs(slopePerK) < 0.5 ? "STABLE (flat — no leak)"
                          : slopePerK < 2.0 ? "MILD CREEP (watch)"
                          : "LEAK (growing)";
        string respVerdict = firstQp99 <= 0 ? "n/a"
                          : lastQp99 > firstQp99 * 1.3 ? $"DEGRADED ({firstQp99:0.0}→{lastQp99:0.0}ms p99)"
                          : $"STEADY ({firstQp99:0.0}→{lastQp99:0.0}ms p99)";

        Console.Error.WriteLine("[soak] --------------------------- VERDICT ---------------------------");
        Console.Error.WriteLine($"[soak]  load      : {actions} actions, {totalFrames} frames in {sec:0.0}s  (~{totalFrames / sec:0.0} fps, {actions / sec:0.0} act/s)");
        Console.Error.WriteLine($"[soak]  memory    : slope = {slopePerK:+0.000;-0.000} MB / 1000 actions  ->  {memVerdict}");
        Console.Error.WriteLine($"[soak]  extrapol. : ~ {perHour:+0.0;-0.0} MB/hour, {perHour * 24:+0.0;-0.0} MB/24h at ~1 action/2s (linear est.)");
        Console.Error.WriteLine($"[soak]  responsiv.: {respVerdict}");
        Console.Error.WriteLine($"[soak]  final mem : ws={proc.WorkingSet64 / 1048576.0:0.0}MB priv={proc.PrivateMemorySize64 / 1048576.0:0.0}MB managed={GC.GetTotalMemory(true) / 1048576.0:0.0}MB (post-collect)");
        gpu.DiagDumpLive("soak-final");
        Console.Error.WriteLine("[soak] done.");
    }

    // ── resize-only stress ──────────────────────────────────────────────────────────────────────────────────────────
    public static void RunResize(AppHost host, Win32Window window, D3D12Device gpu)
    {
        int iters = EnvInt("FG_STRESS_ITERS", 240);
        var proc = Process.GetCurrentProcess();
        Console.Error.WriteLine($"[stress] resize: {iters} iterations (two client sizes, 2 frames each)");
        Mem(proc, "baseline"); gpu.DiagDumpLive("baseline");

        for (int it = 0; it < iters && !window.IsClosed; it++)
        {
            bool big = (it & 1) == 0;
            window.SetClientSize(big ? 1500 : 920, big ? 1040 : 660);
            host.RunFrame(); host.RunFrame();
            if (it % 30 == 0)
            {
                var t = gpu.DiagResourceTotals; proc.Refresh();
                Console.Error.WriteLine($"[stress] it={it,4}  ws={proc.WorkingSet64 / 1048576.0,6:0.0}MB priv={proc.PrivateMemorySize64 / 1048576.0,6:0.0}MB gpu={t.bytes / 1048576.0,5:0.0}MBx{t.count} | {gpu.DiagGpuDetail}");
            }
        }
        for (int i = 0; i < 360 && !window.IsClosed; i++) host.RunFrame();
        Mem(proc, "post-idle"); gpu.DiagDumpLive("post-idle");
        Console.Error.WriteLine("[stress] done.");
    }

    // ── navigation-only churn ───────────────────────────────────────────────────────────────────────────────────────
    public static void RunNav(AppHost host, Win32Window window, D3D12Device gpu)
    {
        int cycles = EnvInt("FG_STRESS_CYCLES", 12);
        var proc = Process.GetCurrentProcess();
        for (int i = 0; i < 30 && !window.IsClosed; i++) host.RunFrame();
        var nav = GalleryShell.StressNavigate; var keys = GalleryShell.StressNavKeys;
        if (nav is null || keys.Length == 0) { Console.Error.WriteLine("[stress] nav hook unavailable — is the root GalleryShell?"); return; }
        Console.Error.WriteLine($"[stress] nav: {cycles} cycles x {keys.Length} pages");
        Mem(proc, "nav-base"); gpu.DiagDumpLive("nav-base");

        for (int cycle = 0; cycle < cycles && !window.IsClosed; cycle++)
        {
            for (int k = 0; k < keys.Length && !window.IsClosed; k++)
            {
                nav(keys[k]);
                for (int f = 0; f < 6 && !window.IsClosed; f++) host.RunFrame();
            }
            proc.Refresh(); var t = gpu.DiagResourceTotals;
            Console.Error.WriteLine($"[stress] cycle={cycle,3}  ws={proc.WorkingSet64 / 1048576.0,6:0.0}MB priv={proc.PrivateMemorySize64 / 1048576.0,6:0.0}MB gpu={t.bytes / 1048576.0,6:0.0}MBx{t.count} | {gpu.DiagGpuDetail}");
        }
        for (int i = 0; i < 360 && !window.IsClosed; i++) host.RunFrame();
        Mem(proc, "nav-idle"); gpu.DiagDumpLive("nav-idle");
        Console.Error.WriteLine("[stress] done.");
    }

    // ── idle-CPU / wake audit ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>FG_WAKE_AUDIT=1: navigate to every page, let it settle, and read AppHost.CurrentWakeReasons. A page that
    /// never settles to None keeps the frame loop spinning at display rate (RecommendedWaitMs==0) => idle CPU burn. The
    /// reason names which subsystem; the table separates legitimately-animating pages from stuck-bit bugs.</summary>
    public static void RunWakeAudit(AppHost host, Win32Window window, D3D12Device gpu)
    {
        const int SettleCap = 240;   // up to ~4s of frames to let entrance transitions / decodes finish
        for (int i = 0; i < 30 && !window.IsClosed; i++) host.RunFrame();
        var nav = GalleryShell.StressNavigate; var keys = GalleryShell.StressNavKeys;
        if (nav is null || keys.Length == 0) { Console.Error.WriteLine("[wake] nav hook unavailable"); return; }
        Console.Error.WriteLine($"[wake] auditing {keys.Length} pages (settle cap {SettleCap} frames)");

        int idleOk = 0, neverIdle = 0;
        var byReason = new Dictionary<string, int>();
        foreach (var key in keys)
        {
            if (window.IsClosed) break;
            nav(key);
            int settledAt = -1, frames = 0;
            for (; frames < SettleCap && !window.IsClosed; frames++)
            {
                host.RunFrame();
                if (host.CurrentWakeReasons == WakeReasons.None) { settledAt = frames; break; }
            }
            var reasons = host.CurrentWakeReasons;
            if (reasons == WakeReasons.None) { idleOk++; continue; }
            neverIdle++;
            string r = reasons.ToString();
            byReason[r] = byReason.TryGetValue(r, out var c) ? c + 1 : 1;

            // Profile the per-frame cost of the spin: ComponentsRendered>0 == re-rendering the tree each frame
            // (wasteful — should be a compositor-only bound transform); ==0 with Rendered == compositor/anim-only (ideal).
            long comps = 0, maxAlloc = 0; int rendered = 0; const int N = 60;
            for (int f = 0; f < N && !window.IsClosed; f++)
            {
                var s = host.RunFrame();
                comps += s.ComponentsRendered;
                if (s.HotPhaseAllocBytes > maxAlloc) maxAlloc = s.HotPhaseAllocBytes;
                if (s.Rendered) rendered++;
            }
            string cost = comps == 0 ? "compositor-only (cheap)" : $"RE-RENDER {comps / (double)N:0.0} comps/frame";
            Console.Error.WriteLine($"[wake] NEVER-IDLE  {key,-28} -> {r,-26} | rendered {rendered}/{N} | {cost} | hotAlloc {maxAlloc}B");
        }

        Console.Error.WriteLine($"[wake] ------- summary: {idleOk}/{keys.Length} pages idle to 0% CPU; {neverIdle} keep spinning -------");
        foreach (var kv in byReason) Console.Error.WriteLine($"[wake]   reason {kv.Key} : {kv.Value} page(s)");
        Console.Error.WriteLine("[wake] done.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private static void Mem(Process proc, string label)
    {
        proc.Refresh();
        Console.Error.WriteLine($"[stress] MEM {label,-9} ws={proc.WorkingSet64 / 1048576.0:0.0}MB priv={proc.PrivateMemorySize64 / 1048576.0:0.0}MB managed={GC.GetTotalMemory(false) / 1048576.0:0.0}MB gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
    }

    /// <summary>p-th percentile (0..100) of the first <paramref name="n"/> samples; 100 ⇒ max. Sorts a copy.</summary>
    private static double Pct(double[] data, int n, int p)
    {
        if (n <= 0) return 0;
        var copy = new double[n];
        Array.Copy(data, copy, n);
        Array.Sort(copy);
        int idx = (int)Math.Round((p / 100.0) * (n - 1));
        return copy[Math.Clamp(idx, 0, n - 1)];
    }

    /// <summary>Least-squares slope (Δy per Δx) over the samples from <paramref name="fromFrac"/>..end — the "is the
    /// back half still climbing" leak signal (ignores warm-up). Returns MB-per-action; callers scale to per-1000.</summary>
    private static double Slope(List<double> xs, List<double> ys, double fromFrac)
    {
        int start = (int)(xs.Count * fromFrac);
        int n = xs.Count - start;
        if (n < 2) return 0;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = start; i < xs.Count; i++) { double x = xs[i], y = ys[i]; sx += x; sy += y; sxx += x * x; sxy += x * y; }
        double denom = n * sxx - sx * sx;
        return Math.Abs(denom) < 1e-9 ? 0 : (n * sxy - sx * sy) / denom;
    }

    private static int EnvInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;
}
