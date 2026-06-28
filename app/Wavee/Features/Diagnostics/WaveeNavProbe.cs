using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace Wavee;

// WAVEE_NAV_PROBE=1: a scripted navigation / scroll / theme / back-forward stress harness. Installed via
// FluentApp.DiagnosticRun, it takes over the run loop and drives REAL app navigation through WaveeShell.Probe* hooks,
// recording the per-frame PRODUCTION time of every painted frame. To measure the engine's true throughput each frame
// suppresses the swapchain frame-latency wait AND vsync (WAVEE_PROBE_VSYNC=1 keeps them — proves a present artifact).
//
// "Butter smooth" target on a 120 Hz panel = every frame's WORK under one vblank (8.33 ms). A frame over budget misses
// a vblank in real (vsync-paced) use → a visible stutter. So the headline metric is "frames over 8.33 ms" — and each is
// split into WORK spikes vs GC spikes (a Gen0+ collection fired on that frame), because the two need different fixes.
internal static class WaveeNavProbe
{
    const double BudgetMs = 1000.0 / 120.0;   // one 120 Hz vblank — the smoothness bar

    // Heavy pages: HomePage (image cards), Liked (big track list), real playlists (40 tracks + cover). Cheap: placeholders.
    static readonly (string Key, string? Arg)[] HeavyRoutes =
    [
        ("home", null),
        ("liked", null),
        ("pl:spotify:playlist:pl0", "Playlist 0"),
        ("pl:spotify:playlist:pl1", "Playlist 1"),
        ("pl:spotify:playlist:pl2", "Playlist 2"),
        ("pl:spotify:playlist:pl3", "Playlist 3"),
        ("pl:spotify:playlist:pl4", "Playlist 4"),
        ("pl:spotify:playlist:pl5", "Playlist 5"),
    ];
    static readonly (string Key, string? Arg)[] CheapRoutes =
    [
        ("albums", null), ("artists", null), ("podcasts", null), ("local", null), ("search", null),
    ];

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        bool connStress = Diag.EnvFlag("WAVEE_CONN_STRESS");
        bool trackShot = Diag.EnvFlag("WAVEE_TRACKLIST_SHOT");
        bool heroShot = Diag.EnvFlag("WAVEE_HERO_SHOT");
        bool homeScroll = Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE");
        if (!Diag.EnvFlag("WAVEE_NAV_PROBE") && !connStress && !trackShot && !heroShot && !homeScroll) return false;
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("[wavee-nav-probe] unavailable: requires Win32Window + D3D12Device");
            return true;
        }
        // DiagnosticRun fires straight after window.Show(), BEFORE the first frame — so WaveeShell hasn't mounted yet
        // and ProbeNav is still null. Pump warmup frames until the shell wires its nav hook (fixes the mount race that
        // made earlier shot runs flakily report "nav hook not wired").
        for (int i = 0; i < 240 && WaveeShell.ProbeNav is null && !w.IsClosed; i++)
        { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        if (WaveeShell.ProbeNav is null)
        {
            Console.Error.WriteLine("[wavee-nav-probe] nav hook not wired (WaveeShell not mounted?)");
            return true;
        }
        if (heroShot) RunHeroCollapseShot(host, w, gpu);
        else if (trackShot) RunTrackListShot(host, w, gpu);
        else if (connStress) RunConnStress(host, w, gpu);
        else if (homeScroll) RunHomeScrollProbe(host, w, gpu);
        else Run(host, w, gpu);
        return true;
    }

    // WAVEE_TRACKLIST_SHOT=1: navigate to each surface that shows tracks and capture a PNG, so the unified track row (the
    // shared TrackRow cell) can be eyeballed for 1:1 parity — a detail playlist, the Library albums pane, an artist page
    // ("Popular"), and search results ("Songs"). Output → C:\tmp\tl_*.png.
    static void RunTrackListShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(@"C:\tmp"); } catch { }
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Nav(string k, string? a) => WaveeShell.ProbeNav!(k, a);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra($@"C:\tmp\tl_{name}.png", px, cw, ch);
            Console.Error.WriteLine($"[tracklist-shot] wrote tl_{name}.png ({cw}x{ch})");
        }

        Nav("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(20);   // home live + card covers decode

        // 1) Detail playlist (the canonical full list — must be unchanged by the cell extraction).
        Nav("pl:spotify:playlist:pl0", "Playlist 0"); Settle(40); System.Threading.Thread.Sleep(700); Settle(60); Shot("detail");
        // 2) Library albums → the pane auto-selects the first album → its tracks render via the embedded TrackList.
        Nav("albums", null); Settle(50); System.Threading.Thread.Sleep(800); Settle(80); Shot("library");
        // 3) Artist page → "Popular" top-tracks (synthesized for any artist uri).
        Nav("artist:spotify:artist:ar0", "Artist"); Settle(50); System.Threading.Thread.Sleep(700); Settle(60); Shot("artist");
        Nav("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5"); Settle(60); System.Threading.Thread.Sleep(2500); Settle(120); Shot("artist_maroon5");
        // 4) Search → the "Songs" rows in the All view.
        Nav("search", "the"); Settle(50); System.Threading.Thread.Sleep(700); Settle(60); Shot("search");

        Console.Error.WriteLine("[tracklist-shot] done");
    }

    // WAVEE_HERO_SHOT=1: navigate to an artist page and capture the collapsing hero at several scroll offsets, dumping the
    // collapse geometry (pin shift / presented height / child shift / parent height) so it can be verified numerically.
    // Output → C:\tmp\hero_{offset}.png + a stderr [hero-geom] dump.
    static void RunHeroCollapseShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(@"C:\tmp"); } catch { }
        window.SetClientSize(1280, 900);
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(int off)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra($@"C:\tmp\hero_{off:D3}.png", px, cw, ch);
            Console.Error.WriteLine($"[hero-shot] wrote hero_{off:D3}.png ({cw}x{ch})");
        }

        WaveeShell.ProbeNav!("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(20);
        WaveeShell.ProbeNav!("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5");
        Settle(50); System.Threading.Thread.Sleep(2500); Settle(120);

        // List every scroller so a too-strict pick is obvious, then take the most-scrollable by content extent (lenient).
        NodeHandle vp = NodeHandle.Null; float bestContent = 0f;
        {
            var st = new Stack<NodeHandle>(); if (!host.Scene.Root.IsNull) st.Push(host.Scene.Root);
            while (st.Count > 0)
            {
                var n = st.Pop();
                if (n.IsNull || !host.Scene.IsLive(n)) continue;
                if (host.Scene.HasScroll(n) && host.Scene.TryGetScroll(n, out var s))
                {
                    var r = host.Scene.AbsoluteRect(n);
                    Console.Error.WriteLine($"[hero-shot]   scroller rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0}) viewH={s.ViewportH:0} contentH={s.ContentH:0}");
                    if (s.ContentH > s.ViewportH + 1f && s.ContentH > bestContent) { bestContent = s.ContentH; vp = n; }
                }
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) st.Push(c);
            }
        }
        if (vp.IsNull) { Console.Error.WriteLine("[hero-shot] no scroll viewport — aborting"); return; }
        var vr = host.Scene.AbsoluteRect(vp);
        host.Scene.TryGetScroll(vp, out var s0);
        float vh = s0.ViewportH > 20f ? s0.ViewportH : vr.H;
        var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
        Settle(2);
        Console.Error.WriteLine($"[hero-shot] viewport content {s0.ContentH:0} viewH {s0.ViewportH:0} @ ({pos.X:0},{pos.Y:0})");

        foreach (int target in new[] { 0, 90, 150, 210, 300, 410 })
        {
            // Approach gently: one small notch, let it settle (velocity ~0) before the next, so fling momentum doesn't
            // overshoot the low sample points.
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                host.Scene.TryGetScroll(vp, out var st);
                if (st.OffsetY >= target - 2f) break;
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, MathF.Min(30f, target - st.OffsetY)));
                for (int k = 0; k < 5 && !window.IsClosed; k++) Frame();
            }
            Settle(6);
            host.Scene.TryGetScroll(vp, out var stNow);
            DumpCollapse(host.Scene, vp, stNow.OffsetY);
            Shot((int)stNow.OffsetY);
        }
        Console.Error.WriteLine("[hero-shot] done");
    }

    // Walk the scene under the scroll content and print any node carrying collapse state (a PresentedH override or a
    // ChildShiftY), with its pin shift (LocalTransform.Dy), its laid-out height, and its PARENT's height — the parent
    // height is the sticky containing-block clamp, so a parent that tightly equals the node's height means the pin can't
    // engage. Also prints the node's siblings so the gap between the collapsed hero and the following content is visible.
    static void DumpCollapse(FluentGpu.Scene.SceneStore scene, NodeHandle vp, float offset)
    {
        scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        NodeHandle collapse = NodeHandle.Null;
        var stack = new Stack<NodeHandle>();
        if (!content.IsNull) stack.Push(content);
        while (stack.Count > 0 && collapse.IsNull)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            ref var p = ref scene.Paint(n);
            if (!float.IsNaN(p.PresentedH) || MathF.Abs(p.ChildShiftY) > 0.01f) { collapse = n; break; }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        if (collapse.IsNull) { Console.Error.WriteLine($"[hero-geom] off={offset:0}: no collapse node (scroll-away mode)"); return; }
        ref var cp = ref scene.Paint(collapse);
        var cr = scene.AbsoluteRect(collapse);
        var par = scene.Parent(collapse);
        float parH = par.IsNull ? -1f : scene.Bounds(par).H;
        Console.Error.WriteLine(
            $"[hero-geom] off={offset:0}: collapse node rectY={cr.Y:0} boundsH={scene.Bounds(collapse).H:0} " +
            $"presentedH={cp.PresentedH:0} childShiftY={cp.ChildShiftY:0} pinShift(Dy)={cp.LocalTransform.Dy:0} " +
            $"| parentH={parH:0} (==boundsH ⇒ pin clamp=0)");
        if (!par.IsNull)
            for (var c = scene.FirstChild(par); !c.IsNull; c = scene.NextSibling(c))
            {
                var rc = scene.AbsoluteRect(c);
                Console.Error.WriteLine($"           sibling rectY={rc.Y:0} H={scene.Bounds(c).H:0} bottom={rc.Bottom:0}");
            }
    }

    // WAVEE_CONN_STRESS=1 (+ optional WAVEE_STRESS_N=100, WAVEE_STRESS_NOPACE=1): the user's scenario — SLOWLY click a Home
    // card (connected-animation fly to detail), WAIT for the nav to settle, go BACK (reverse fly), wait, repeat N times. For
    // every frame it captures BOTH the loop's RecommendedWaitMs (0 = display-rate, >0 = throttled to the 30Hz ambient cap)
    // AND the pure per-frame WORK cost (vsync suppressed). Headline metric = "await-dest throttle": with the
    // AnimIsAmbient(_connected) fix, EVERY in-flight (fly/mount) frame must be display-rate (wait==0) — any wait>0 there is
    // the stall that made connected animations "sometimes laggy". Cycles 1..K (K = distinct home cards) are COLD/uncached.
    sealed class Seg
    {
        public int Frames, Active, Over8, Over16, Gen0, MaxAwaitWait;
        public double MaxWorkMs;
        public long Alloc;
        public readonly List<int> FirstWaits = new(24);
        public readonly List<double> ActiveWork = new(64);
    }

    static void RunConnStress(AppHost host, Win32Window window, D3D12Device gpu)
    {
        int N = int.TryParse(Environment.GetEnvironmentVariable("WAVEE_STRESS_N"), out var nn) && nn > 0 ? nn : 100;
        bool realPace = !Diag.EnvFlag("WAVEE_STRESS_NOPACE");
        if (WaveeShell.ProbeCardNav is null) { Console.Error.WriteLine("[conn-stress] ProbeCardNav not wired — aborting"); return; }
        Console.Error.WriteLine($"[conn-stress] {N} cycles (click->settle->back->settle); realistic pacing={realPace}");

        void Spin(int frames) { for (int f = 0; f < frames && !window.IsClosed; f++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }

        // Warmup: home live + card covers decoded (the fly needs an image source), then a clean GC baseline.
        WaveeShell.ProbeNav!("home", null); Spin(60);
        System.Threading.Thread.Sleep(900);
        Spin(20);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var keys = new List<string>(); host.CollectMorphKeys(keys);
        if (keys.Count == 0) { Console.Error.WriteLine("[conn-stress] no home-card morph keys — aborting"); return; }
        Console.Error.WriteLine($"[conn-stress] {keys.Count} distinct home-card keys (cycles 1..{keys.Count} are COLD/uncached)");

        // Drive one navigation to rest: capture per-frame RecommendedWaitMs (the perceived cadence) + pure WORK (vsync
        // removed). "Settled" = the transient fly+mount stops driving display-rate frames (4 consecutive non-display frames).
        Seg Drive()
        {
            var seg = new Seg();
            long a0 = GC.GetAllocatedBytesForCurrentThread(); int g0 = GC.CollectionCount(0);
            int consecIdle = 0;
            for (int f = 0; f < 90 && !window.IsClosed; f++)
            {
                int wait = host.RecommendedWaitMs();      // the pace the real loop would apply now (also feeds Fix 1's resync)
                if (seg.FirstWaits.Count < 24) seg.FirstWaits.Add(wait);
                if (f < 10 && wait > seg.MaxAwaitWait) seg.MaxAwaitWait = wait;   // throttle during the await-dest/early-fly window
                bool display = wait == 0;                 // a live fly/mount frame runs at display rate
                if (display) consecIdle = 0; else consecIdle++;
                if (realPace && wait > 0) System.Threading.Thread.Sleep(Math.Min(wait, 40));
                gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
                var s = host.RunFrame();
                seg.Frames++;
                if (display)
                {
                    seg.Active++;
                    seg.ActiveWork.Add(s.FrameMs);
                    if (s.FrameMs > seg.MaxWorkMs) seg.MaxWorkMs = s.FrameMs;
                    if (s.FrameMs > 8.33) seg.Over8++;
                    if (s.FrameMs > 16.7) seg.Over16++;
                }
                if (consecIdle >= 4) break;               // transient fly+mount done — only ambient/idle remains
            }
            seg.Alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            seg.Gen0 = GC.CollectionCount(0) - g0;
            return seg;
        }

        var fwd = new List<Seg>(N); var back = new List<Seg>(N);
        for (int i = 0; i < N && !window.IsClosed; i++)
        {
            WaveeShell.ProbeNav!("home", null); Spin(realPace ? 18 : 12);          // home = the fly's source card; let it settle
            WaveeShell.ProbeCardNav!(keys[i % keys.Count], null, true);            // CLICK -> connected-animation fly to detail
            fwd.Add(Drive());
            WaveeShell.ProbeBack?.Invoke();                                        // BACK -> reverse fly
            back.Add(Drive());
            if ((i + 1) % 10 == 0) Console.Error.WriteLine($"[conn-stress] {i + 1}/{N}");
        }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine($"=== WAVEE CONNECTED-ANIM STRESS — {N} cycles (click->settle->back->settle) ===");
        sb.AppendLine("await-throttle = an in-flight (fly/mount) frame the loop would THROTTLE to the 30Hz ambient cap — MUST be 0");
        sb.AppendLine("(that 30Hz stall was the 'connected anim sometimes laggy'); work = pure per-frame cost, vsync removed.");
        ReportSeg(sb, "FORWARD click->detail", fwd, keys.Count);
        ReportSeg(sb, "BACK    ->home", back, keys.Count);
        string rep = sb.ToString();
        Console.Error.Write(rep);
        try { Directory.CreateDirectory(@"C:\tmp"); File.WriteAllText(@"C:\tmp\wavee-conn-stress.txt", rep); Console.Error.WriteLine("[conn-stress] wrote C:\\tmp\\wavee-conn-stress.txt"); } catch { }
    }

    static void ReportSeg(StringBuilder sb, string title, List<Seg> segs, int coldCount)
    {
        sb.AppendLine();
        sb.AppendLine($"-- {title} — {segs.Count} cycles --");
        if (segs.Count == 0) { sb.AppendLine("  (none)"); return; }
        int throttledCycles = 0, maxAwait = 0;
        foreach (var s in segs) { if (s.MaxAwaitWait > 0) throttledCycles++; if (s.MaxAwaitWait > maxAwait) maxAwait = s.MaxAwaitWait; }
        sb.AppendLine($"  >> await-dest THROTTLE: {throttledCycles}/{segs.Count} cycles had a throttled in-flight frame (max wait {maxAwait}ms) — want 0/{segs.Count}");
        var work = new List<double>(8192); foreach (var s in segs) work.AddRange(s.ActiveWork);
        var wa = work.ToArray(); Array.Sort(wa);
        int over8 = 0, over16 = 0; foreach (var s in segs) { over8 += s.Over8; over16 += s.Over16; }
        sb.AppendLine($"  in-flight frames: {wa.Length} | WORK ms p50 {Pct(wa,50):0.00} p90 {Pct(wa,90):0.00} p99 {Pct(wa,99):0.00} max {(wa.Length>0?wa[^1]:0):0.00} | >8.33ms={over8} >16.7ms={over16}");
        var maxw = new double[segs.Count]; var settle = new double[segs.Count]; var alloc = new double[segs.Count];
        long totAlloc = 0; int totGc = 0;
        for (int i = 0; i < segs.Count; i++) { maxw[i] = segs[i].MaxWorkMs; settle[i] = segs[i].Frames; alloc[i] = segs[i].Alloc / 1024.0; totAlloc += segs[i].Alloc; totGc += segs[i].Gen0; }
        Array.Sort(maxw); Array.Sort(settle); Array.Sort(alloc);
        sb.AppendLine($"  per-cycle worst-frame ms: p50 {Pct(maxw,50):0.00} p90 {Pct(maxw,90):0.00} max {maxw[^1]:0.00}");
        sb.AppendLine($"  per-cycle frames-to-settle: p50 {Pct(settle,50):0} p90 {Pct(settle,90):0} max {settle[^1]:0}");
        sb.AppendLine($"  alloc/cycle KB: p50 {Pct(alloc,50):0} p90 {Pct(alloc,90):0} max {alloc[^1]:0} | total {totAlloc/1024/1024}MB Gen0={totGc}");
        if (segs.Count > coldCount && coldCount > 0)
        {
            double coldMax = 0, warmMax = 0; int cOver16 = 0, wOver16 = 0;
            for (int i = 0; i < segs.Count; i++)
            {
                bool cold = i < coldCount;
                foreach (var v in segs[i].ActiveWork) if (v > 16.7) { if (cold) cOver16++; else wOver16++; }
                if (cold) coldMax = Math.Max(coldMax, segs[i].MaxWorkMs); else warmMax = Math.Max(warmMax, segs[i].MaxWorkMs);
            }
            sb.AppendLine($"  COLD (1..{coldCount}) worst {coldMax:0.00}ms >16.7ms={cOver16}  |  WARM worst {warmMax:0.00}ms >16.7ms={wOver16}");
        }
        var w0 = segs[0].FirstWaits; var hd = new StringBuilder("  cycle#1 first waits(ms, 0=display rate): ");
        foreach (var x in w0) hd.Append(x).Append(' ');
        sb.AppendLine(hd.ToString());
    }

    sealed class Phase(string name)
    {
        public readonly string Name = name;
        public readonly List<double> Ms = new(4096);
        public int OverBudget;       // frames whose WORK exceeded one vblank (would drop a frame in vsync-paced use)
        public int OverBudgetGc;     // ...of those, frames on which a Gen0+ GC fired (a GC spike, not a work spike)
    }

    // WAVEE_HOME_SCROLL_PROBE=1: the screenshot repro path, isolated from the full nav stress suite. It warms Home,
    // toggles compact/expanded sidebar state, simulates a drag-resize sequence through the same shell signals, then
    // scrolls the Home viewport with real wheel input. Output defaults to .wavee-diagnostics under the current dir; override
    // with WAVEE_PROBE_OUT.
    static void RunHomeScrollProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string? outDir = ProbeOutputDir();
        string? csvPath = outDir is null ? null : Path.Combine(outDir, "wavee-home-scroll-probe.csv");
        string? summaryPath = outDir is null ? null : Path.Combine(outDir, "wavee-home-scroll-probe-summary.txt");
        var csv = new StringBuilder(1 << 15);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,gen0,gen1,comps,nodes,draws,overBudget");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");

        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(dg0).Append(',').Append(dg1).Append(',')
                   .Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }

        Console.Error.WriteLine("[home-scroll-probe] warmup");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var sidebar = new Phase("sidebar-home");
        var scroll = new Phase("home-scroll");

        Nav("home", null);
        Settle(40);
        System.Threading.Thread.Sleep(700);     // let the Home feed and visible art settle before the sidebar scenario
        Settle(30);

        if (WaveeShell.ProbeSidebarCompact is null || WaveeShell.ProbeSidebarDragBegin is null ||
            WaveeShell.ProbeSidebarDragWidth is null || WaveeShell.ProbeSidebarDragEnd is null)
        {
            Console.Error.WriteLine("[home-scroll-probe] sidebar hooks unavailable; skipping sidebar state phase");
        }
        else
        {
            Console.Error.WriteLine("[home-scroll-probe] sidebar compact -> expanded -> drag-resize");
            WaveeShell.ProbeSidebarCompact(true);
            for (int f = 0; f < 18 && !window.IsClosed; f++) Measure(sidebar, "compact");
            WaveeShell.ProbeSidebarCompact(false);
            for (int f = 0; f < 26 && !window.IsClosed; f++) Measure(sidebar, "expand");

            WaveeShell.ProbeSidebarDragBegin();
            foreach (float w in new[] { 300f, 340f, 380f, 420f, 360f, 300f, 260f, 330f })
            {
                WaveeShell.ProbeSidebarDragWidth(w);
                Measure(sidebar, "drag");
            }
            WaveeShell.ProbeSidebarDragEnd();
            for (int f = 0; f < 12 && !window.IsClosed; f++) Measure(sidebar, "drag-end");
        }

        var viewport = FindLargestScrollViewport(host.Scene);
        if (viewport.IsNull)
        {
            Console.Error.WriteLine("[home-scroll-probe] no Home scroll viewport found");
        }
        else
        {
            var vr = host.Scene.AbsoluteRect(viewport);
            host.Scene.TryGetScroll(viewport, out var s0);
            float vh = s0.ViewportH > 20f ? s0.ViewportH : (vr.H > 20f ? vr.H : 400f);
            var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
            Settle(2);
            Console.Error.WriteLine($"[home-scroll-probe] wheel @ ({pos.X:0},{pos.Y:0}) viewport x={vr.X:0} w={vr.W:0} content {s0.ContentH:0} viewH {s0.ViewportH:0}");

            for (int i = 0; i < 180 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 30) & 1) == 0 ? +60f : -60f));
                Measure(scroll, "wheel");
            }
            for (int rep = 0; rep < 4 && !window.IsClosed; rep++)
            {
                for (int k = 0; k < 6 && !window.IsClosed; k++) { window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, +60f)); Measure(scroll, "flick"); }
                for (int st = 0; st < 16 && !window.IsClosed; st++) Measure(scroll, "coast");
            }
            host.Scene.TryGetScroll(viewport, out var s1);
            bool moved = MathF.Abs(s1.OffsetY - s0.OffsetY) > 1f;
            Console.Error.WriteLine($"[home-scroll-probe] endOff {s1.OffsetY:0} - wheel input {(moved ? "took effect" : "did not move")}");

            // Real app-loop cadence: present at vsync and ask RecommendedWaitMs before each frame so accidental ambient
            // throttling during scroll shows up as wait>0.
            var iv = new List<double>(220);
            long prev = Stopwatch.GetTimestamp();
            int throttled = 0, maxWait = 0;
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 20) & 1) == 0 ? +60f : -60f));
                int wait = host.RecommendedWaitMs();
                if (wait > 0) { throttled++; if (wait > maxWait) maxWait = wait; System.Threading.Thread.Sleep(Math.Min(wait, 40)); }
                host.RunFrame();
                long now = Stopwatch.GetTimestamp();
                iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency);
                prev = now;
            }
            var a = iv.ToArray();
            double tot = 0, worst = 0;
            foreach (var v in a) { tot += v; if (v > worst) worst = v; }
            double mean = a.Length > 0 ? tot / a.Length : 0;
            int o16 = 0;
            foreach (var v in a) if (v > 16.7) o16++;
            Console.Error.WriteLine($"[home-scroll-fps] REAL app-loop wheel scroll: {a.Length} frames mean {mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0} fps) worst {worst:0.0}ms ({(worst > 0 ? 1000.0 / worst : 0):0} fps) >16.7ms(<60fps)={o16} throttledWaitFrames={throttled} maxWait={maxWait}ms");
        }

        var phases = new[] { sidebar, scroll };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(4096);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE HOME SCROLL PROBE - per-frame production time (ms); target < 8.33 ms at 120 Hz ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced)" : "(vsync/latency throttle removed -> pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 96));
        sb.AppendLine(Format(all));

        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 15 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            int.TryParse(c[9], out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(5), D(6), D(7), D(8), g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 12 frames - {"total",7} = {"flush",6} + {"layout",6} + {"anim",5} + {"record",6} + {"submit",6}  (gc=Gen0)  transition");
        for (int i = 0; i < Math.Min(12, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,6:0.00} + {r.Layout,6:0.00} + {r.Anim,5:0.00} + {r.Record,6:0.00} + {r.Submit,6:0.00}  gc={r.G0}  {r.Tag}");
        }

        string report = sb.ToString();
        Console.Error.Write(report);
        if (csvPath is not null) WriteProbeFile(csvPath, csv.ToString(), "home-scroll-probe");
        if (summaryPath is not null) WriteProbeFile(summaryPath, report, "home-scroll-probe");
    }

    static string? ProbeOutputDir()
    {
        string? dir = Environment.GetEnvironmentVariable("WAVEE_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.CurrentDirectory, ".wavee-diagnostics");
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[home-scroll-probe] file output disabled: cannot create {dir}: {ex.Message}");
            return null;
        }
    }

    static void WriteProbeFile(string path, string text, string tag)
    {
        try
        {
            File.WriteAllText(path, text);
            Console.Error.WriteLine($"[{tag}] wrote {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{tag}] failed to write {path}: {ex.Message}");
        }
    }

    static void Run(AppHost host, Win32Window window, D3D12Device gpu)
    {
        Directory.CreateDirectory(@"C:\tmp");
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,gen0,gen1,comps,nodes,draws,overBudget");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");

        // One measured frame under `phase`, tagged `label`. Strips the throttle (unless WAVEE_PROBE_VSYNC) so FrameMs is
        // pure production cost; captures the Gen0/Gen1 collection deltas so a GC spike is distinguishable from a work spike.
        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(dg0).Append(',').Append(dg1).Append(',')
                   .Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }

        // ── Warmup (not measured); collect once so the baseline starts from a known GC state. ──
        Console.Error.WriteLine("[wavee-nav-probe] warmup");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ── Phase 0: HOME ↔ DETAIL (the named case). Bounce home → a fresh playlist → home → next playlist …, holding a
        //    few settle frames each so the cold-mount transition frame AND its settle are captured + labelled by route. ──
        var homeDetail = new Phase("home<->detail");
        Console.Error.WriteLine("[wavee-nav-probe] home<->detail (the named case)");
        bool capture = Diag.EnvFlag("WAVEE_PB_CAPTURE");   // dump the first home→detail transition frame-by-frame to PNG

        // Player-bar-glitch repro: the EXACT Home-card click path (preview → DetailShell preview-path mount + Hero fly),
        // which the sidebar-style ProbeNav never exercises. Home must be mounted first (the fly's source card).
        if (capture && WaveeShell.ProbeCardNav is not null)
        {
            Console.Error.WriteLine("[wavee-nav-probe] card-click capture (home→detail via preview + Hero fly)");
            Nav("home", null);
            for (int f = 0; f < 40 && !window.IsClosed; f++) host.RunFrame();
            System.Threading.Thread.Sleep(700);                         // let card covers decode (async) so the fly has an image source
            for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
            string? flyKey = host.FirstMorphKey;                        // a REAL home-card key → the morph actually FLIES
            Console.Error.WriteLine($"[wavee-nav-probe] fly key = {flyKey ?? "(none found)"}");
            WaveeShell.ProbeCardNav(flyKey ?? "pl:spotify:playlist:pl0", null, true);
            for (int f = 0; f < 12 && !window.IsClosed; f++)
            {
                gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
                host.RunFrame();
                var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra($@"C:\tmp\card_{f:D2}.png", px, cw, ch);
            }
        }
        for (int rep = 0; rep < 6 && !window.IsClosed; rep++)
        {
            Nav("home", null);
            for (int f = 0; f < 8 && !window.IsClosed; f++) Measure(homeDetail, "home");
            var (k, a) = HeavyRoutes[2 + rep % 6];   // pl0..pl5 (fresh each bounce → real cold mount)
            Nav(k, a);
            for (int f = 0; f < 8 && !window.IsClosed; f++)
            {
                Measure(homeDetail, k);
                if (capture && rep == 0) { var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra($@"C:\tmp\pb_{f:D2}.png", px, cw, ch); }
            }
        }

        // ── Phase 1: NAV-SETTLE across all route types (2 sweeps). ──
        var navSettle = new Phase("nav-settle");
        const int SettleFrames = 14;
        var sweep = new List<(string, string?)>();
        for (int s = 0; s < HeavyRoutes.Length; s++) { sweep.Add(HeavyRoutes[s]); if (s < CheapRoutes.Length) sweep.Add(CheapRoutes[s]); }
        Console.Error.WriteLine($"[wavee-nav-probe] nav-settle ({sweep.Count} routes x 2 sweeps)");
        for (int rep = 0; rep < 2 && !window.IsClosed; rep++)
            foreach (var (key, arg) in sweep)
            {
                if (window.IsClosed) break;
                Nav(key, arg);
                for (int f = 0; f < SettleFrames && !window.IsClosed; f++) Measure(navSettle, key);
            }

        // ── Phase 2: NAV-HAMMER (route swap every frame — worst-case churn). ──
        var hammer = new Phase("nav-hammer");
        Console.Error.WriteLine("[wavee-nav-probe] nav-hammer (400 frames)");
        for (int i = 0; i < 400 && !window.IsClosed; i++)
        {
            var (key, arg) = HeavyRoutes[i % HeavyRoutes.Length];
            Nav(key, arg);
            Measure(hammer, key);
        }

        // ── Phase 3: BACK / FORWARD (KeepAlive swap + connected-animation cover fly). ──
        var backfwd = new Phase("back-forward");
        for (int i = 0; i < HeavyRoutes.Length && !window.IsClosed; i++) { var (k, a) = HeavyRoutes[i]; Nav(k, a); Settle(3); }
        Console.Error.WriteLine("[wavee-nav-probe] back-forward (240 frames)");
        for (int i = 0; i < 240 && !window.IsClosed; i++)
        {
            if ((i / 8) % 2 == 0) WaveeShell.ProbeBack?.Invoke(); else WaveeShell.ProbeForward?.Invoke();
            Measure(backfwd, "nav");
            // Capture the FIRST back/forward transition (the connected-animation Hero-fly path) frame-by-frame.
            if (capture && i < 16) { var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra($@"C:\tmp\bf_{i:D2}.png", px, cw, ch); }
        }

        // ── Phase 4: SCROLL via REAL mouse-wheel INPUT. NOT WriteScrollOffset (that bypasses input): here every notch is an
        //    InputKind.Wheel event injected through window.QueueInput → PumpInto → the dispatcher ring → DispatchWheel/ScrollAt
        //    (hit-test the cursor pos, route to the nearest VERTICAL scroller), and SmoothScroll eases each +60 DIP notch via
        //    the ScrollAnimator (inertia). So this measures the full real path a mouse drives: hit-test + wheel routing + the
        //    eased per-frame re-layout / virtualization re-realize / re-record, including the inertial COAST after a flick. ──
        var scroll = new Phase("scroll (real wheel)");
        if (Diag.EnvFlag("WAVEE_LIKED_SHOT")) { window.SetClientSize(1456, 820); Settle(12); Nav("liked", null); Settle(50); var px0 = gpu.CaptureBgra(out int cw0, out int ch0); PngWriter.WriteBgra(@"C:\tmp\liked_header.png", px0, cw0, ch0); }
        // Navigate to a TWO-COLUMN playlist: its track list cross-stretches to the rail height → a real non-zero viewport that
        // renders rows the wheel can hit-test. (The single-column Liked list measures a 0-height viewport — a pre-existing bug
        // that leaves it empty + un-hit-testable; not what we want to audit scroll feel on.)
        Nav("home", null); Settle(20);
        System.Threading.Thread.Sleep(600);
        for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
        var skeys = new List<string>(); host.CollectMorphKeys(skeys);
        Nav(skeys.Count > 0 ? skeys[0] : "pl:local:1", null); Settle(40);
        var viewport = FindLargestScrollViewport(host.Scene);
        if (viewport.IsNull) Console.Error.WriteLine("[wavee-nav-probe] scroll: no scroll viewport — skipped");
        else
        {
            var vr = host.Scene.AbsoluteRect(viewport);
            host.Scene.TryGetScroll(viewport, out var s0);
            // Cursor over the viewport centre (layout-DIP, the hit-test space). The scroll node's own box height can be 0, so
            // use the ScrollState ViewportH to place the point over a REAL row (so HitTestAny descends to it and routes the wheel).
            float vh = s0.ViewportH > 20f ? s0.ViewportH : (vr.H > 20f ? vr.H : 400f);
            var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));   // a real mouse hovers first → routes the wheel here + reveals the auto-hide scrollbar
            Settle(2);
            Console.Error.WriteLine($"[wavee-nav-probe] scroll: REAL wheel @ ({pos.X:0},{pos.Y:0}) viewport content {s0.ContentH:0} viewH {s0.ViewportH:0}");

            // Brisk continuous spin — ~1 notch/frame DOWN then UP. The offset crosses item boundaries continuously, so the
            // virtualized list recycles/re-realizes rows every frame (the heaviest steady-scroll case). Inject + measure each frame.
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, i < 100 ? +60f : -60f));
                Measure(scroll, i < 100 ? "wheel-down" : "wheel-up");
            }
            // Flick-and-coast: a 6-notch burst, then STOP — the ScrollAnimator eases the residual to rest. These COAST frames
            // are exactly what a programmatic offset-set can never see (it lands instantly); a real flick animates for ~15 frames.
            for (int rep = 0; rep < 6 && !window.IsClosed; rep++)
            {
                for (int k = 0; k < 6 && !window.IsClosed; k++) { window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, +60f)); Measure(scroll, "flick"); }
                for (int st = 0; st < 16 && !window.IsClosed; st++) Measure(scroll, "coast");
            }
            host.Scene.TryGetScroll(viewport, out var s1);
            bool moved = Math.Abs(s1.OffsetY - s0.OffsetY) > 1f;
            Console.Error.WriteLine($"[wavee-nav-probe] scroll: endOff {s1.OffsetY:0} — wheel input {(moved ? "TOOK EFFECT (offset moved by dispatch+ease)" : "NO-OP — routing FAILED")}");

            // REAL-vsync wheel-scroll FPS: drive notches at TRUE present cadence (no vsync suppression) and time the wall-clock
            // frame interval — the FPS a user actually perceives while spinning the wheel. Oscillate so the list never sits clamped.
            var iv = new List<double>(220);
            long prev = Stopwatch.GetTimestamp();
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 20) % 2 == 0) ? +60f : -60f));   // 20 down / 20 up → always moving
                host.RunFrame();
                long now = Stopwatch.GetTimestamp(); iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency); prev = now;
            }
            var a = iv.ToArray(); double tot = 0, worst = 0; foreach (var v in a) { tot += v; if (v > worst) worst = v; }
            double mean = a.Length > 0 ? tot / a.Length : 0; int o16 = 0; foreach (var v in a) if (v > 16.7) o16++;
            Console.Error.WriteLine($"[scroll-fps] REAL wheel scroll at vsync: {a.Length} frames mean {mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0} fps) worst {worst:0.0}ms ({(worst > 0 ? 1000.0 / worst : 0):0} fps) >16.7ms(<60fps)={o16}");
        }

        // ── Phase 5: THEME TOGGLE (RethemeAll re-renders every component + arms the cross-fade). ──
        var theme = new Phase("theme-toggle");
        Nav("home", null); Settle(6);
        Console.Error.WriteLine("[wavee-nav-probe] theme-toggle (240 frames)");
        for (int i = 0; i < 240 && !window.IsClosed; i++)
        {
            if (i % 20 == 0) WaveeShell.ProbeTheme?.Invoke();
            Measure(theme, i % 20 == 0 ? "flip" : "settle");
        }

        // ── Phase 6: IDLE (steady-state floor). ──
        var idle = new Phase("idle");
        Nav("home", null); Settle(10);
        Console.Error.WriteLine("[wavee-nav-probe] idle (180 frames)");
        for (int i = 0; i < 180 && !window.IsClosed; i++) Measure(idle, "idle");

        // ── Phase: CONNECTED ANIMATION (Hero cover fly) cost. Trigger REAL flies (home→detail→back) and measure the
        //    per-frame production cost WHILE a fly is in flight, vs the SAME navigation on the SAME path WITHOUT the fly
        //    (doMorph=false) — so the delta is purely the connected-animation overhead. Needs card covers loaded first. ──
        var connFly = new Phase("conn-anim FLY");
        var connNo = new Phase("conn-anim no-fly");
        if (WaveeShell.ProbeCardNav is not null)
        {
            Nav("home", null);
            for (int f = 0; f < 40 && !window.IsClosed; f++) host.RunFrame();
            System.Threading.Thread.Sleep(800);                        // let card covers decode (the fly needs an image source)
            for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
            string? flyKey = host.FirstMorphKey;
            Console.Error.WriteLine($"[wavee-nav-probe] conn-anim fly key = {flyKey ?? "(none)"}");
            if (flyKey is not null)
            {
                var keys = new List<string>(); host.CollectMorphKeys(keys);
                // FRESH flies (DISTINCT cards → cold mount + load + fly) WITH per-segment capture, to pin where the
                // ~9-10ms/frame that drops the fly to 60fps actually goes (flush=realize, record=image crossfades, …).
                for (int ki = 0; ki < keys.Count && ki < 12 && !window.IsClosed; ki++)
                {
                    Nav("home", null);
                    for (int f = 0; f < 16 && !window.IsClosed; f++) host.RunFrame();
                    WaveeShell.ProbeCardNav(keys[ki], null, true);
                    for (int f = 0; f < 16 && !window.IsClosed; f++) Measure(connFly, "fresh");
                }
                for (int rep = 0; rep < 14 && !window.IsClosed; rep++)   // SAME nav, NO fly (preview path, no morph)
                {
                    WaveeShell.ProbeCardNav(flyKey, null, false);
                    for (int f = 0; f < 14 && !window.IsClosed; f++) Measure(connNo, "fwd");
                    WaveeShell.ProbeBack?.Invoke();
                    for (int f = 0; f < 14 && !window.IsClosed; f++) Measure(connNo, "back");
                }

                // ── REAL FPS of a COMPLETE Hero fly: present at TRUE vsync cadence (do NOT suppress vsync/latency) and time
                //    the WALL-CLOCK frame interval click→settle, so this is the FPS actually perceived during the fly. Fly to
                //    DISTINCT home-card pages so each is a FRESH (uncached) cold mount + fly — the realistic worst case. ──
                host.CollectMorphKeys(keys);   // reuse the list collected above
                Console.Error.WriteLine($"[fly-fps] complete connected animations at REAL vsync cadence — {keys.Count} fresh home-card keys (interval ms / fps):");
                for (int fly = 0; fly < keys.Count && fly < 6 && !window.IsClosed; fly++)
                {
                    Nav("home", null);
                    for (int f = 0; f < 18 && !window.IsClosed; f++) host.RunFrame();   // home live (the fly's source card) — not measured
                    var iv = new List<double>(96);
                    double sFlush = 0, sLayout = 0, sAnim = 0, sRecord = 0, sSubmit = 0; int sN = 0;
                    WaveeShell.ProbeCardNav(keys[fly], null, true);                       // CLICK a FRESH card — fly + cold mount start
                    long prev = Stopwatch.GetTimestamp();
                    for (int f = 0; f < 60 && !window.IsClosed; f++)
                    {
                        var s = host.RunFrame();                                         // vsync NOT suppressed → real present pacing
                        long now = Stopwatch.GetTimestamp();
                        double dms = (now - prev) * 1000.0 / Stopwatch.Frequency;
                        iv.Add(dms); prev = now;
                        if (f < 20) { sFlush += s.FlushMs; sLayout += s.LayoutMs; sAnim += s.AnimMs; sRecord += s.RecordMs; sSubmit += s.SubmitMs; sN++; }
                    }
                    var arr = iv.ToArray();
                    double tot = 0, worst = 0; foreach (var v in arr) { tot += v; if (v > worst) worst = v; }
                    double mean = tot / arr.Length;
                    int over16 = 0, over33 = 0; foreach (var v in arr) { if (v > 16.7) over16++; if (v > 33.3) over33++; }
                    Console.Error.WriteLine($"[fly-fps] fresh fly#{fly}: {arr.Length} frames {tot:0}ms | mean {mean:0.0}ms ({1000.0/mean:0} fps) | worst {worst:0.0}ms ({1000.0/worst:0} fps) | >16.7ms(<60fps)={over16} >33ms(<30fps)={over33}");
                    if (sN > 0) Console.Error.WriteLine($"           REAL-vsync segs(first {sN}): flush {sFlush/sN:0.0} layout {sLayout/sN:0.0} anim {sAnim/sN:0.0} record {sRecord/sN:0.0} submit {sSubmit/sN:0.0}");
                    var head = new StringBuilder("           first 18 intervals(ms):");
                    for (int i = 0; i < Math.Min(18, arr.Length); i++) head.Append(' ').Append(arr[i].ToString("0", CultureInfo.InvariantCulture));
                    Console.Error.WriteLine(head.ToString());
                }
            }
        }

        // ── Report ──
        var phases = new[] { idle, scroll, backfwd, connNo, connFly, homeDetail, navSettle, hammer, theme };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE STRESS — per-frame PRODUCTION time (ms); target = every frame < 8.33 ms (one 120 Hz vblank) ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced — FrameMs includes the present wait)" : "(vsync/latency throttle removed → pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 96));
        sb.AppendLine(Format(all));
        sb.AppendLine();
        sb.AppendLine("over8.3ms = frames whose WORK would miss a vblank (the stutters). (ofwhich GC) = those coinciding with a Gen0+ collection.");

        // Worst 15 frames overall, broken down by segment — the exact spikes to remedy.
        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 15 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            int.TryParse(c[9], out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(5), D(6), D(7), D(8), g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 15 frames — {"total",7} = {"flush",6} + {"layout",6} + {"anim",5} + {"record",6} + {"submit",6}  (gc=Gen0)  transition");
        for (int i = 0; i < Math.Min(15, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,6:0.00} + {r.Layout,6:0.00} + {r.Anim,5:0.00} + {r.Record,6:0.00} + {r.Submit,6:0.00}  gc={r.G0}  {r.Tag}");
        }

        string report = sb.ToString();
        Console.Error.Write(report);
        File.WriteAllText(@"C:\tmp\wavee-nav-probe.csv", csv.ToString());
        File.WriteAllText(@"C:\tmp\wavee-nav-probe-summary.txt", report);
        Console.Error.WriteLine("[wavee-nav-probe] wrote C:\\tmp\\wavee-nav-probe.csv + -summary.txt");
    }

    static string Format(Phase p)
    {
        if (p.Ms.Count == 0) return $"{p.Name,-14} {0,5}   (no frames)";
        var a = p.Ms.ToArray();
        Array.Sort(a);
        return $"{p.Name,-14} {a.Length,5} {Pct(a, 50),6:0.00} {Pct(a, 90),6:0.00} {Pct(a, 99),7:0.00} {Pct(a, 99.9),7:0.00} {a[^1],8:0.00}   {p.OverBudget,10} {p.OverBudgetGc,12}";
    }

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] * (1 - (rank - lo)) + sorted[hi] * (rank - lo);
    }

    // The most-scrollable viewport by VERTICAL CONTENT extent. Ranking by box area fails here: a virtualized list's scroll
    // node reports a laid-out box HEIGHT of 0 (its extent lives in ScrollState.ContentH, not the rect), so a tall-but-narrow
    // sidebar would win on area. ContentH picks the real content list and ignores horizontal-only scrollers.
    static NodeHandle FindLargestScrollViewport(FluentGpu.Scene.SceneStore scene)
    {
        var best = default(NodeHandle);
        float bestContent = 0f;
        var stack = new Stack<NodeHandle>();
        if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc))
            {
                var r = scene.AbsoluteRect(n);
                // Real, hit-testable CONTENT scroller: a non-zero viewport (a 0-height viewport stops hit-test descent),
                // genuinely scrollable, and past the sidebar (x>100) so we never pick the nav rail. Ranked by content extent.
                if (sc.ViewportH > 50f && sc.ContentH > sc.ViewportH + 1f && r.X > 100f && sc.ContentH > bestContent)
                { bestContent = sc.ContentH; best = n; }
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return best;
    }
}
