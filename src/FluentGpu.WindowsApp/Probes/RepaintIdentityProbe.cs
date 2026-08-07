using System;
using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;

namespace FluentGpu;

/// <summary>
/// <c>--repaint-identity</c> — the PIXEL check for damage-scissored partial repaint (gpu-renderer.md §13.1, task-7
/// review §F). Every gate the feature shipped with is pure <c>RepaintPolicy</c>/<c>RepaintCull</c> arithmetic on the
/// Engine side; not one of them touches the device, the clear/scissor/cull agreement, or a single pixel, and
/// <c>--screenshot</c> only ever exercises the FullDirect first frame. So the one thing nothing verified was the thing
/// the whole feature claims: <b>a partially repainted frame is indistinguishable from a fully repainted one.</b>
///
/// The method, per scenario: settle the scene, warm the canvas until the route really is <see cref="RepaintRoute.Partial"/>,
/// apply ONE scripted signal write, let it settle on the partial path, capture the back buffer (<b>P</b>); then
/// <see cref="AppHost.RequestFullRepaintOnce"/> and render the SAME state again through a full repaint, capture (<b>F</b>);
/// assert P == F byte for byte. A mismatch writes P/F/an amplified diff as PNGs and prints the differing region's
/// bounding box + pixel count, which is what identifies WHICH class broke: a 1-px column ⇒ the pixel-grid fold, a
/// missing glyph fragment ⇒ the cull halo, a rectangular block at a stale position ⇒ a missed vacated band.
///
/// A second, independent gate rides the same loop: the <b>route delta</b>. Step 0 of the involution lands back on the
/// exact state a FullDirect frame was captured at, so the two ROUTES can be compared at identical scene state. They must
/// be bit-identical — the canvas and the back buffer are the same size, the same <c>B8G8R8A8_UNORM</c> format and carry
/// the same null-desc view — and §13.1 makes canvas frames the normal case, so any delta is a permanent, visible
/// difference between a scrolling window and an idle one.
///
/// This cannot live in FluentGpu.VerticalSlice — that harness is headless by contract and there are no pixels there.
/// It is a command-line arm, not a behaviour switch: nothing here changes the default path, and the one API it needs
/// (<c>RequestFullRepaintOnce</c>) is the explicit form of an invalidation the engine already performs internally.
/// </summary>
internal static class RepaintIdentityProbe
{
    private const int Width = 900, Height = 640;

    /// <summary>Per-warm-frame route/reason trace. A scenario that reports INCONCLUSIVE is a real failure, and the ONE
    /// question worth asking then is "which disqualifier is firing" — so keep the answer one env flag away rather than
    /// making the next person re-derive this probe's instrumentation.</summary>
    private static readonly bool s_trace = Diag.EnvFlag("FG_REPAINT_IDENTITY_TRACE");

    /// <summary>One scenario. <paramref name="Mutate"/> MUST be an INVOLUTION — applying it twice restores the scene
    /// exactly — because that is what lets the same state be reached once by a full replay and once by two partial ones
    /// (see the baseline/P comparison in <c>Drive</c>). <paramref name="Arrange"/> runs once before the measurement, for
    /// the setup a scenario needs but does not want measured.</summary>
    private readonly record struct Scenario(int Id, string Name, string Targets, Action Mutate, Action? Arrange = null);

    /// <summary>Entry point for <c>Program.Main</c>: returns the process exit code (0 = every scenario byte-identical).</summary>
    public static int Run(string? outDir)
    {
        outDir ??= ".tmp/repaint-identity";
        int exit = 1;
        FluentApp.DiagnosticRun = (host, window, device) =>
        {
            exit = Drive(host, window, device, outDir);
            return true;   // we own the run; skip the interactive loop
        };
        FluentAppHarness.Run(() => new RepaintIdentityScene(),
            new AppOptions
            {
                Title = "FluentGpu — repaint identity",
                Width = Width, Height = Height,
                // Mica OFF: a desktop-sampling acrylic backdrop makes every frame FullDirect by policy (the backdrop
                // snapshot copies target regions INTO the canvas), so the partial route would never be reached at all.
                Mica = false,
                // No ambient throttle and no post-input warm hold: both only matter for a loop we are not running.
                AmbientFps = 0, WarmCadenceMs = 0f,
            });
        return exit;
    }

    private static int Drive(AppHost host, IPlatformWindow window, IGpuDevice device, string outDir)
    {
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("repaint-identity: needs the Win32 + D3D12 backend (GPU required).");
            return 2;
        }

        Scenario[] matrix =
        [
            new(0, "twin-animators-subpixel-gap", "C1 — the 1-px double-blend column",
                () => { RepaintIdentityScene.Tick.Value++; RepaintIdentityScene.TickB.Value++; }),
            new(1, "glyph-straddle", "I4 — glyph cull-halo under-coverage",
                () => RepaintIdentityScene.Tick.Value++),
            new(2, "stale-prior-extent", "I3 — ghost at a pre-translation position",
                () => RepaintIdentityScene.RowX.Value = RepaintIdentityScene.RowX.Value == 0f ? 150f : 0f,
                // The ancestor rebase happens BEFORE the measured mutation and is allowed to settle, so the row's own
                // move later reads a prior extent that a translated-span copy shifted out from under it.
                Arrange: () => RepaintIdentityScene.ScrollY.Value = 600f),
            new(3, "opacity-group-straddle", "the LAYERED partial route (single union rect)",
                () => { RepaintIdentityScene.Tick.Value++; RepaintIdentityScene.TickB.Value++; }),
            new(4, "video-hole-overlap", "E — the DrawVideo hole's damage inflation",
                () => RepaintIdentityScene.Tick.Value++),
            new(5, "three-animators", "union blow-up + instance-bank pressure (3 rects)",
                () => { RepaintIdentityScene.Tick.Value++; RepaintIdentityScene.TickB.Value++; RepaintIdentityScene.TickC.Value++; }),
        ];

        int passed = 0, failed = 0, inconclusive = 0;
        long worstReplayRects = 0, worstDropped = 0;
        Console.Error.WriteLine($"[repaint-identity] {Width}x{Height} scale={w.Scale:0.###}  ({matrix.Length} scenarios)");

        foreach (Scenario s in matrix)
        {
            if (w.IsClosed) break;
            RepaintIdentityScene.ResetAll();
            RepaintIdentityScene.Scenario.Value = s.Id;
            Settle(host, w, 10);

            s.Arrange?.Invoke();
            if (s.Arrange is not null) Settle(host, w, 10);

            // ── F: the FULL replay baseline. RequestFullRepaintOnce takes one FullDirect frame, which leaves the canvas
            //    invalid; the next small-damage frame is therefore FullIntoCanvas — one full replay INTO the canvas,
            //    then the blit. That is deliberately the baseline rather than the FullDirect frame itself: comparing a
            //    canvas blit against a direct-to-back-buffer render would conflate the question §5.1 actually asks
            //    ("does damage-scissoring lose anything?") with a separate, pre-existing property of the canvas route
            //    (it blends in the canvas's own stored space, so its AA fringes are not bit-identical to the direct
            //    path's). Both captures below are canvas blits, so the ONLY variable left is full vs partial replay.
            host.RequestFullRepaintOnce();
            bool directDone = PumpOne(host, w);
            var directRoute = gpu.LastRepaintRoute;
            Settle(host, w, 3);
            // Keep the DIRECT render of this state: the P-loop below passes back through it on the canvas route, so the
            // two are comparable and the canvas-vs-direct delta becomes a measured number instead of an assumption.
            byte[] direct = Capture(host, gpu, out int dw, out int dh);
            s.Mutate();
            bool baselineDone = PumpOne(host, w);
            var baselineRoute = gpu.LastRepaintRoute;
            Settle(host, w, 3);
            if (s_trace) Console.Error.WriteLine($"[repaint-identity]   baseline: direct={directDone}/{directRoute} " +
                                                 $"full={baselineDone}/{baselineRoute} cov={gpu.LastRepaintCoverage:0.000}");
            if (!directDone || directRoute != RepaintRoute.FullDirect || !baselineDone || baselineRoute != RepaintRoute.FullIntoCanvas)
            {
                Console.Error.WriteLine($"[repaint-identity] {s.Id} {s.Name}: INCONCLUSIVE — no FullIntoCanvas baseline " +
                                        $"(direct={directDone}/{directRoute}, full={baselineDone}/{baselineRoute}); {s.Targets}");
                inconclusive++;
                continue;
            }
            byte[] f = Capture(host, gpu, out int fw, out int fh);

            // ── P: the SAME state, reached by PARTIAL replays. Every scenario's mutation is an involution — applying it
            //    twice restores the scene exactly — so two more mutations land back on the baseline state having gone
            //    through the partial route twice, never repainting anything the damage region did not name.
            int partialSubmits = 0, otherSubmits = 0, rects = 0, routeDelta = -1, routeDeltaMax = 0;
            byte[]? viaCanvasKeep = null;
            for (int step = 0; step < 2; step++)
            {
                s.Mutate();
                for (int i = 0; i < 4; i++)
                {
                    if (!PumpOne(host, w)) continue;     // elided (byte-identical stream): nothing new on screen
                    if (gpu.LastRepaintRoute == RepaintRoute.Partial) { partialSubmits++; rects = Math.Max(rects, gpu.LastReplayRectCount); }
                    else otherSubmits++;
                }
                // ── ROUTE-DELTA GATE. Step 0 lands back on the state the FullDirect capture holds — but reached through
                //    the canvas. This is the ONLY place the two routes can be compared at identical scene state, and
                //    since §13.1 makes canvas frames the NORMAL case (a window alternating scroll → idle crosses the
                //    boundary constantly), the two routes must agree BIT-FOR-BIT — canvas and back buffer are the same
                //    size, the same B8G8R8A8_UNORM format, and carry the same null-desc view, so there is no legitimate
                //    reason for a texel to differ. It is asserted, not merely reported: the first measurement here found
                //    a real defect (the canvas→back-buffer blit was a filtered Sample, ±1 LSB at high-contrast edges —
                //    80 px across one text block), which nothing else in the repo could have caught.
                if (step == 0 && dw == fw && dh == fh)
                {
                    byte[] viaCanvas = Capture(host, gpu, out int cw2, out int ch2);
                    if (cw2 == dw && ch2 == dh)
                    {
                        Compare(direct, viaCanvas, dw, dh, out routeDelta, out _, out _, out _, out _);
                        routeDeltaMax = MaxChannelDelta(direct, viaCanvas);
                        viaCanvasKeep = viaCanvas;
                    }
                }
            }
            long dropped = gpu.LastDroppedInstanceCount;
            worstReplayRects = Math.Max(worstReplayRects, rects);
            worstDropped = Math.Max(worstDropped, dropped);

            if (partialSubmits < 2 || otherSubmits != 0 || rects < 1)
            {
                Console.Error.WriteLine($"[repaint-identity] {s.Id} {s.Name}: INCONCLUSIVE — partial={partialSubmits} " +
                                        $"other={otherSubmits} rects={rects} route={gpu.LastRepaintRoute}/{gpu.LastRepaintFullReason}; {s.Targets}");
                inconclusive++;
                continue;
            }

            byte[] p = Capture(host, gpu, out int pw, out int ph);
            if (pw != fw || ph != fh)
            {
                Console.Error.WriteLine($"[repaint-identity] {s.Id} {s.Name}: INCONCLUSIVE — capture size changed " +
                                        $"({pw}x{ph} vs {fw}x{fh}); {s.Targets}");
                inconclusive++;
                continue;
            }

            if (!Compare(p, f, pw, ph, out int diffPixels, out int dl, out int dt, out int dr, out int db))
            {
                Console.Error.WriteLine($"[repaint-identity] {s.Id} {s.Name}: FAIL  {diffPixels} px differ, bbox " +
                                        $"[{dl},{dt} → {dr},{db}] ({dr - dl}x{db - dt})  rects={rects}  [{s.Targets}]");
                WriteEvidence(outDir, s, p, f, pw, ph);
                failed++;
            }
            else if (routeDelta != 0)
            {
                Console.Error.WriteLine($"[repaint-identity] {s.Id} {s.Name}: FAIL (route delta)  canvasVsDirect=" +
                                        (routeDelta < 0 ? "not measured" : $"{routeDelta}px max{routeDeltaMax}/255") +
                                        $" — a canvas frame must be bit-identical to the FullDirect render of the same state  [{s.Targets}]");
                if (viaCanvasKeep is not null) WriteEvidence(outDir, s, viaCanvasKeep, direct, pw, ph, "route");
                failed++;
            }
            else
            {
                Console.Error.WriteLine($"[repaint-identity] {s.Id} {s.Name}: PASS  (rects={rects} partialFrames={partialSubmits} " +
                                        $"dropped={dropped} canvasVsDirect={routeDelta}px)  [{s.Targets}]");
                passed++;
            }
        }

        Console.Error.WriteLine($"[repaint-identity] {passed}/{matrix.Length} identical, {failed} mismatched, " +
                                $"{inconclusive} inconclusive; peak replay rects {worstReplayRects}, peak instancesDropped {worstDropped}");
        // Inconclusive is a FAILURE, not a pass: a scenario that stopped exercising the partial route has stopped
        // testing anything, and silently green is exactly how this feature got here with no pixel coverage at all.
        return failed == 0 && inconclusive == 0 && passed == matrix.Length ? 0 : 1;
    }

    // ── frame driving ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Produce ONE presented frame, or report that there was nothing to produce.
    /// <para>This must PACE the loop, not hammer it. Under the default async seam the display-phase gate declines any
    /// frame published before the previous one was presented — that is its whole job — so a bare <c>RunFrame</c> spin
    /// produces exactly one frame and then silently nothing, which is how this probe's first draft managed to report
    /// six inconclusive scenarios against a perfectly working renderer. Waiting on the host's own recommended timeout
    /// between attempts is what the real host loop does, and it is the only way the gate ever opens.</para>
    /// <para>Returns false when the frame was ELIDED (the skip-submit gate found a byte-identical stream) or when the
    /// host simply had no work: either way nothing new reached the screen and there is nothing to judge.</para>
    /// </summary>
    private static bool PumpOne(AppHost host, Win32Window w, int budgetMs = 400)
    {
        ulong before = host.PublishSequence;
        long skippedBefore = host.FramesSkippedSubmit;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs && !w.IsClosed)
        {
            host.RunFrame();
            if (host.PublishSequence != before) break;                          // produced
            if (host.FramesSkippedSubmit != skippedBefore) return false;        // produced, then elided as identical
            w.WaitForWork(Math.Clamp(host.RecommendedWaitMs(), 1, 16));           // gated / idle: pace and retry
        }
        ulong target = host.PublishSequence;
        if (target == before) return false;
        while (host.RenderPresentSeq < target && sw.ElapsedMilliseconds < budgetMs + 2000 && !w.IsClosed)
            w.WaitForWork(1);
        return true;
    }

    /// <summary>Run until the scene stops producing: <paramref name="quietFrames"/> consecutive attempts that neither
    /// published nor elided anything. Bounded, because a scene that never goes quiet is a bug in the scene and hanging
    /// on it teaches nobody anything.</summary>
    private static void Settle(AppHost host, Win32Window w, int quietFrames)
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        while (quiet < quietFrames && sw.ElapsedMilliseconds < 4000 && !w.IsClosed)
        {
            ulong before = host.PublishSequence;
            long skippedBefore = host.FramesSkippedSubmit;
            host.RunFrame();
            if (host.PublishSequence != before || host.FramesSkippedSubmit != skippedBefore) quiet = 0;
            else quiet++;
            w.WaitForWork(Math.Clamp(host.RecommendedWaitMs(), 1, 16));
        }
        // Everything published must also have been PRESENTED before a capture reads the back buffer.
        ulong target = host.PublishSequence;
        while (host.RenderPresentSeq < target && sw.ElapsedMilliseconds < 6000 && !w.IsClosed) w.WaitForWork(1);
    }

    /// <summary>Read the presented back buffer. The render thread is PARKED for the duration: CaptureBgra resets the
    /// command allocator + fence the render thread otherwise owns, which is the same exclusion the fenced UI-side
    /// swapchain Resize already runs under.</summary>
    private static byte[] Capture(AppHost host, D3D12Device gpu, out int width, out int height)
    {
        byte[]? px = null;
        int cw = 0, ch = 0;
        host.RunWithRenderThreadParked(() => { px = gpu.CaptureBgra(out cw, out ch); });
        width = cw; height = ch;
        return px ?? [];
    }

    // ── comparison + evidence ───────────────────────────────────────────────────────────────────────────────────────

    private static bool Compare(byte[] a, byte[] b, int w, int h,
        out int diffPixels, out int left, out int top, out int right, out int bottom)
    {
        diffPixels = 0; left = int.MaxValue; top = int.MaxValue; right = int.MinValue; bottom = int.MinValue;
        if (a.Length != b.Length) { left = top = 0; right = w; bottom = h; diffPixels = w * h; return false; }
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3]) continue;
                diffPixels++;
                if (x < left) left = x;
                if (x + 1 > right) right = x + 1;
                if (y < top) top = y;
                if (y + 1 > bottom) bottom = y + 1;
            }
        }
        return diffPixels == 0;
    }

    /// <summary>Largest single-channel difference between two captures (0 = identical). The MAGNITUDE is what separates
    /// the two failure shapes the route-delta gate can see: 1 means a rounding/resample artefact at high-contrast edges,
    /// while a colour-space or blend-space mismatch moves whole ramps by tens of levels.</summary>
    private static int MaxChannelDelta(byte[] a, byte[] b)
    {
        int max = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { int d = Math.Abs(a[i] - b[i]); if (d > max) max = d; }
        return max;
    }

    private static void WriteEvidence(string outDir, in Scenario s, byte[] p, byte[] f, int w, int h, string tag = "")
    {
        try
        {
            System.IO.Directory.CreateDirectory(outDir);
            string stem = System.IO.Path.Combine(outDir, tag.Length == 0 ? $"{s.Id}-{s.Name}" : $"{s.Id}-{s.Name}-{tag}");
            PngWriter.WriteBgra($"{stem}-partial.png", p, w, h);
            PngWriter.WriteBgra($"{stem}-full.png", f, w, h);
            // Amplified difference: any channel delta becomes a saturated magenta pixel, so a one-column hairline is
            // visible at a glance instead of needing a pixel-peeper.
            byte[] diff = new byte[p.Length];
            for (int i = 0; i < p.Length; i += 4)
            {
                bool differs = p[i] != f[i] || p[i + 1] != f[i + 1] || p[i + 2] != f[i + 2] || p[i + 3] != f[i + 3];
                diff[i] = differs ? (byte)0xFF : (byte)0x10;       // B
                diff[i + 1] = differs ? (byte)0x00 : (byte)0x10;   // G
                diff[i + 2] = differs ? (byte)0xFF : (byte)0x10;   // R
                diff[i + 3] = 0xFF;
            }
            PngWriter.WriteBgra($"{stem}-diff.png", diff, w, h);
            Console.Error.WriteLine($"[repaint-identity]   evidence: {stem}-{{partial,full,diff}}.png");
        }
        catch (Exception e) { Console.Error.WriteLine($"[repaint-identity]   (evidence write failed: {e.Message})"); }
    }
}
