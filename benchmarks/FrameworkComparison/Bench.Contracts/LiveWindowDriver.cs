using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bench.Contracts;

/// <summary>What a host hands the desktop driver: its top-level window and its own UI-thread marshal.</summary>
public sealed class LiveWindowTarget
{
    public required string Framework { get; init; }
    public required string FrameworkVersion { get; init; }
    public required nint Hwnd { get; init; }
    /// <summary>Thread-safe: run the action on the host's UI thread (FluentGpu <c>AppHost.Post</c>, WinUI
    /// <c>DispatcherQueue.TryEnqueue</c>).</summary>
    public required Action<Action> PostToUi { get; init; }
    /// <summary>UI thread: close the window and end the process normally.</summary>
    public required Action CloseOnUi { get; init; }
    /// <summary>UI thread, theme-switch only: paint <c>probeId</c> into the frame-ID probe and switch to the theme in the
    /// framework's own idiomatic way, in the same UI-thread turn.</summary>
    public Action<int, bool>? ApplyThemeOnUi { get; init; }
}

/// <summary>
/// The shared desktop driver of the desktop-driven scenarios. The SAME code runs in both hosts on a dedicated
/// background thread of the host process: it drives the window with plain Win32 (cross-thread <c>SetWindowPos</c>, which
/// the window's UI thread must process, exactly as for any external resize) and observes the result where the user does,
/// on the DWM-composed desktop (DXGI desktop duplication: every composed frame, with its present time). Nothing a framework reports about itself is trusted: every verdict is made from desktop pixels.
/// </summary>
public static class LiveWindowDriver
{
    public static void Start(BenchOptions options, LiveWindowTarget target)
    {
        var thread = new Thread(() => Run(options, target))
        {
            IsBackground = true,
            Name = "bench-desktop-driver",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
    }

    private static void Run(BenchOptions options, LiveWindowTarget target)
    {
        try
        {
            BenchResult result = options.Scenario switch
            {
                BenchScenarios.WindowResize => new ResizeRun(options, target).Execute(),
                BenchScenarios.ThemeSwitch => new ThemeRun(options, target).Execute(),
                _ => throw new InvalidOperationException(options.Scenario),
            };
            result.Write(options.OutputPath);
            target.PostToUi(target.CloseOnUi);
        }
        catch (Exception ex)
        {
            // No result file: the runner records the run as failed with this exit code. Never a silent pass.
            Console.Error.WriteLine("[desktop-driver] " + ex);
            Environment.Exit(3);
        }
    }

    internal static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    internal static BenchResult Finish(BenchOptions options, LiveWindowTarget target, long start, long stop,
        double[] frameMs, int warmup, string notes, Dictionary<string, double> metrics, Dictionary<string, double[]> series)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        var result = BenchResult.Create(target.Framework, target.FrameworkVersion, options with { WarmupFrames = warmup },
            start, stop, frameMs, [], 0, process.WorkingSet64, process.PrivateMemorySize64, notes);
        return result with { Metrics = metrics, Series = series };
    }
}

/// <summary>
/// window-resize. Per composed frame k: take the first frame DWM composed after the previous step took effect, sample it, then
/// <c>SetWindowPos</c> to trajectory step k. A sample judges the frame DWM composed AFTER the previous step's
/// <c>SetWindowPos</c> returned, i.e. whether the app put content of the new size on screen within one refresh. Each
/// sample reads (a) the two size probes, which encode the width and height (DIP) the content was LAID OUT at, and (b) the
/// pixels just inside the window's actual right and bottom client edge, where the layout's edge strips must be.
/// </summary>
internal sealed class ResizeRun
{
    private const int MaxSettleMs = 5000;
    private readonly BenchOptions _options;
    private readonly LiveWindowTarget _target;
    private readonly nint _hwnd;
    private readonly double _scale;

    internal ResizeRun(BenchOptions options, LiveWindowTarget target)
    {
        _options = options;
        _target = target;
        _hwnd = target.Hwnd;
        _scale = Native.GetDpiForWindow(_hwnd) / 96d;
    }

    /// <summary>Strip = the layout's edge strip is on the window edge; Unpainted = pure black or pure white (nothing
    /// drawn there: the DWM/redirection fill of an area the app has not painted yet); Other = anything else (old content).</summary>
    private enum Edge : byte { Strip = 0, Unpainted = 1, Other = 2 }

    private struct Sample
    {
        public int Iteration;        // the step whose size the window had while this frame was composed
        public long Qpc;
        public int ContentW, ContentH;   // decoded laid-out size, -1 = undecodable
        public Edge Right, Bottom;
        public int ContentStep;      // most recent step with the decoded size, -1 = none
        public uint Accumulated;     // frames DWM composed since the previous acquire (1 = none missed)
    }

    // Staging layout of one duplicated frame: the probe row at (0,0), the right-edge column beside it, the bottom-edge
    // row below both.
    private int _probesW, _probesH, _colX, _rowY;

    internal BenchResult Execute()
    {
        int warmup = _options.WarmupFrames;
        int measured = _options.Iterations;
        int total = warmup + measured;

        Native.GetWindowRect(_hwnd, out Native.RECT wr);
        Native.GetClientRect(_hwnd, out Native.RECT cr);
        int ncW = (wr.Right - wr.Left) - (cr.Right - cr.Left);
        int ncH = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);

        int maxW = Px(BenchWorkload.ResizeMaxWidth) + 16, maxH = Px(BenchWorkload.ResizeMaxHeight) + 16;
        _probesW = Px(BenchWorkload.ResizeProbe2X + BenchWorkload.ResizeProbeSize + 8);
        _probesH = Px(BenchWorkload.ResizeProbeRowHeight);
        _colX = _probesW + 8;
        _rowY = maxH + 8;

        // Same placement on both hosts: top-left at (32, 32) px, step-0 size, above other windows (an occluded pixel
        // would be judged as the framework's).
        (int w0, int h0) = BenchWorkload.ResizeSizeDip(0);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 32, 32, Px(w0) + ncW, Px(h0) + ncH, Native.SWP_NOACTIVATE);
        string occlusion = Native.OcclusionNote(_hwnd, Px(32), Px(32));
        var origin0 = new Native.POINT();
        Native.ClientToScreen(_hwnd, ref origin0);
        using var dup = new DesktopDuplication(origin0.X, origin0.Y, Math.Max(maxW, _colX + 16), _rowY + 16);

        // Settle on step 0: proves the probes decode and the strips sit on the edge before anything is timed.
        Sample settle = default;
        long settleDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * MaxSettleMs / 1000;
        do
        {
            settle = Take(dup, 0, w0, h0, 0);
        } while (!InSync(settle, w0, h0) && Stopwatch.GetTimestamp() < settleDeadline);
        if (!InSync(settle, w0, h0))
            throw new InvalidOperationException(
                $"resize: step-0 never in sync (content {settle.ContentW}x{settle.ContentH}, edges {settle.Right}/{settle.Bottom}); {occlusion}");
        SaveShot("start");

        var samples = new Sample[total + 1];
        var setStart = new long[total + 1];
        var setMs = new double[total + 1];
        var stepW = new int[total + 1];
        var stepH = new int[total + 1];
        stepW[0] = w0; stepH[0] = h0;
        long measureStart = 0;
        int shotAt = warmup + measured / 2;

        var setEnd = new long[total + 1];
        setEnd[0] = Stopwatch.GetTimestamp();
        for (int k = 1; k <= total; k++)
        {
            // The first frame DWM composed after the previous SetWindowPos returned: the frame that shows the new size.
            Sample s = Take(dup, k - 1, stepW[k - 1], stepH[k - 1], setEnd[k - 1]);
            s.ContentStep = ResolveStep(s, k - 1, stepW, stepH);
            samples[k] = s;
            if (k == warmup + 1) measureStart = s.Qpc;
            if (k == shotAt) SaveShot("inflight");

            (int w, int h) = BenchWorkload.ResizeSizeDip(k);
            stepW[k] = w; stepH[k] = h;
            long t0 = Stopwatch.GetTimestamp();
            setStart[k] = t0;
            Native.SetWindowPos(_hwnd, 0, 0, 0, Px(w) + ncW, Px(h) + ncH,
                Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            setEnd[k] = Stopwatch.GetTimestamp();
            setMs[k] = LiveWindowDriver.Ms(setEnd[k] - t0);
        }

        // Trailing samples so the last steps can resolve their latency, then settle at the final size.
        var tail = new List<Sample>();
        long tailDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;   // 1 s
        Sample last;
        long notBefore = setEnd[total];
        do
        {
            last = Take(dup, total, stepW[total], stepH[total], notBefore);
            notBefore = last.Qpc;
            last.ContentStep = ResolveStep(last, total, stepW, stepH);
            tail.Add(last);
        } while (!InSync(last, stepW[total], stepH[total]) && Stopwatch.GetTimestamp() < tailDeadline);
        long stop = Stopwatch.GetTimestamp();
        bool finalInSync = InSync(last, stepW[total], stepH[total]);
        SaveShot("end");

        // ── Reduce (measured steps only) ─────────────────────────────────────────────────────────────────────
        var all = new List<Sample>(samples.Length + tail.Count);
        for (int k = 1; k <= total; k++) all.Add(samples[k]);
        all.AddRange(tail);

        int nSamples = 0, inSync = 0, black = 0, stretched = 0, stale = 0, undecodable = 0, contentUpdates = 0;
        var lag = new List<double>();
        int prevW = int.MinValue, prevH = int.MinValue;
        for (int k = warmup + 1; k <= total; k++)
        {
            Sample s = samples[k];
            nSamples++;
            bool contentOk = s.ContentW == stepW[k - 1] && s.ContentH == stepH[k - 1];
            bool edgesOk = s.Right == Edge.Strip && s.Bottom == Edge.Strip;
            if (contentOk && edgesOk) inSync++;
            else if (s.Right == Edge.Unpainted || s.Bottom == Edge.Unpainted) black++;
            else if (edgesOk) stretched++;
            else stale++;
            if (s.ContentW < 0) undecodable++;
            if (s.ContentW != prevW || s.ContentH != prevH) { if (prevW != int.MinValue) contentUpdates++; prevW = s.ContentW; prevH = s.ContentH; }
            if (s.ContentStep >= 0) lag.Add(k - 1 - s.ContentStep);
        }

        // Latency of step j: SetWindowPos(j) call to the first composed frame showing content laid out at step j or later.
        var latency = new List<double>(measured);
        var setBlocked = new List<double>(measured);
        int unresolved = 0;
        int cursor = 0;
        for (int j = warmup + 1; j <= total; j++)
        {
            setBlocked.Add(setMs[j]);
            while (cursor < all.Count && (all[cursor].Qpc <= setStart[j])) cursor++;
            int found = -1;
            for (int i = cursor; i < all.Count; i++)
                if (all[i].ContentStep >= j) { found = i; break; }
            if (found < 0) { unresolved++; continue; }
            latency.Add(LiveWindowDriver.Ms(all[found].Qpc - setStart[j]));
        }

        double seconds = LiveWindowDriver.Ms(samples[total].Qpc - measureStart) / 1000d;
        var metrics = new Dictionary<string, double>
        {
            ["frames"] = nSamples,
            ["framesInSync"] = inSync,
            ["framesUnpaintedEdge"] = black,
            ["framesStretched"] = stretched,
            ["framesStale"] = stale,
            ["framesUndecodable"] = undecodable,
            ["inSyncPercent"] = 100d * inSync / Math.Max(1, nSamples),
            ["mismatchPercent"] = 100d * (nSamples - inSync) / Math.Max(1, nSamples),
            ["contentUpdatesPerSecond"] = contentUpdates / Math.Max(1e-9, seconds),
            ["resizeStepsPerSecond"] = (nSamples - 1) / Math.Max(1e-9, seconds),
            ["unresolvedSteps"] = unresolved,
            ["finalInSync"] = finalInSync ? 1 : 0,
            ["scale"] = _scale,
        };
        var series = new Dictionary<string, double[]>
        {
            ["setWindowPosMs"] = setBlocked.ToArray(),
            ["contentLagFrames"] = lag.ToArray(),
        };
        WriteSampleLog(samples, stepW, stepH, total, tail);
        string notes = string.Create(CultureInfo.InvariantCulture,
            $"Desktop-observed live resize: {measured} SetWindowPos steps (+{warmup} warmup), one per composed frame (DXGI desktop duplication), " +
            $"trajectory {BenchWorkload.ResizeMinWidth}-{BenchWorkload.ResizeMaxWidth} x {BenchWorkload.ResizeMinHeight}-{BenchWorkload.ResizeMaxHeight} DIP " +
            $"at {BenchWorkload.ResizeStepWidth}x{BenchWorkload.ResizeStepHeight} DIP/step, scale {_scale:0.###}. frameMs = SetWindowPos call to the present time of the first " +
            $"composed frame whose content was laid out at that step or later (desktop-duplication read of the size probes). {occlusion}");
        return LiveWindowDriver.Finish(_options, _target, measureStart, stop, latency.ToArray(), warmup, notes, metrics, series);
    }

    private static bool InSync(in Sample s, int w, int h)
        => s.ContentW == w && s.ContentH == h && s.Right == Edge.Strip && s.Bottom == Edge.Strip;

    /// <summary>The most recent step (at or before <paramref name="upTo"/>) whose size equals the decoded content size.
    /// Within one half-cycle every size is unique, so a lag shorter than a half-cycle resolves exactly.</summary>
    private static int ResolveStep(in Sample s, int upTo, int[] w, int[] h)
    {
        if (s.ContentW < 0) return -1;
        int floor = Math.Max(0, upTo - BenchWorkload.ResizeHalfCycleSteps);
        for (int i = upTo; i >= floor; i--)
            if (w[i] == s.ContentW && h[i] == s.ContentH) return i;
        return -1;
    }

    /// <summary>Judge the next composed frame presented after <paramref name="notBefore"/> (QPC) against the size
    /// (<paramref name="wDip"/> x <paramref name="hDip"/>) the window had at that point.</summary>
    private Sample Take(DesktopDuplication dup, int iteration, int wDip, int hDip, long notBefore)
    {
        var s = new Sample { Iteration = iteration, ContentStep = -1, ContentW = -1, ContentH = -1, Right = Edge.Other, Bottom = Edge.Other };
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;   // nothing composed for 1 s: judge as stale
        long present = 0;
        uint accumulated = 0;
        while (true)
        {
            if (Stopwatch.GetTimestamp() > deadline) { s.Qpc = Stopwatch.GetTimestamp(); return s; }
            if (!dup.Acquire(100, out present, out accumulated)) continue;
            if (present > notBefore) break;
            dup.EndFrame();   // composed before the step took effect: not this step's frame
            dup.Unmap();
        }
        var origin = new Native.POINT();
        Native.ClientToScreen(_hwnd, ref origin);
        int wPx = Px(wDip), hPx = Px(hDip);
        dup.Copy(origin.X, origin.Y, _probesW, _probesH, 0, 0);
        dup.Copy(origin.X + wPx - 8, origin.Y, 8, hPx, _colX, 0);
        dup.Copy(origin.X, origin.Y + hPx - 8, wPx, 8, 0, _rowY);
        dup.EndFrame();
        try
        {
            s.Qpc = present;
            s.Accumulated = accumulated;
            int cy = Px(BenchWorkload.ResizeProbeMargin + BenchWorkload.ResizeProbeSize / 2);
            s.ContentW = DecodeAt(dup, Px(BenchWorkload.ResizeProbeMargin + BenchWorkload.ResizeProbeSize / 2), cy);
            s.ContentH = DecodeAt(dup, Px(BenchWorkload.ResizeProbe2X + BenchWorkload.ResizeProbeSize / 2), cy);
            if (s.ContentW < 0 || s.ContentH < 0) { s.ContentW = -1; s.ContentH = -1; }

            // Right edge: client columns W-6 and W-3 px (inside the 9 px strip, clear of the 1 px DWM window border),
            // at five heights inside the middle row. Bottom edge: rows H-6 and H-3 at five widths.
            Edge right = Edge.Strip, bottom = Edge.Strip;
            foreach (double f in EdgeFractions)
            {
                int y = (int)(hPx * f);
                right = Worse(right, Classify(dup, _colX + 2, y));
                right = Worse(right, Classify(dup, _colX + 5, y));
                int x = (int)(wPx * f);
                bottom = Worse(bottom, Classify(dup, x, _rowY + 2));
                bottom = Worse(bottom, Classify(dup, x, _rowY + 5));
            }
            s.Right = right;
            s.Bottom = bottom;
        }
        finally { dup.Unmap(); }
        return s;
    }

    private static readonly double[] EdgeFractions = [0.15, 0.3, 0.5, 0.7, 0.85];

    private static Edge Worse(Edge a, Edge b) => a == Edge.Unpainted || b == Edge.Unpainted ? Edge.Unpainted
        : a == Edge.Other || b == Edge.Other ? Edge.Other : Edge.Strip;

    private static Edge Classify(DesktopDuplication c, int x, int y)
    {
        (byte r, byte g, byte b) = c.Pixel(x, y);
        if ((r <= 8 && g <= 8 && b <= 8) || (r >= 247 && g >= 247 && b >= 247)) return Edge.Unpainted;
        return Near(r, BenchWorkload.ResizeEdgeR) && Near(g, BenchWorkload.ResizeEdgeG) && Near(b, BenchWorkload.ResizeEdgeB)
            ? Edge.Strip : Edge.Other;
        static bool Near(byte a, byte e) => Math.Abs(a - e) <= 14;
    }

    /// <summary>Decode a probe, requiring a uniform 3 x 3 patch (a half-composed or blended pixel is not a size).</summary>
    private static int DecodeAt(DesktopDuplication c, int x, int y)
    {
        (byte r, byte g, byte b) = c.Pixel(x, y);
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (c.Pixel(x + dx, y + dy) != (r, g, b)) return -1;
        return FrameIdProbe.TryDecode(r, g, b, out int v) ? v : -1;
    }

    private int Px(int dip) => (int)Math.Round(dip * _scale);

    private void SaveShot(string tag)
    {
        string path = Path.ChangeExtension(_options.OutputPath, null) + ".resize-" + tag + ".bmp";
        DesktopCapture.SaveClient(_hwnd, path);
    }

    private void WriteSampleLog(Sample[] samples, int[] w, int[] h, int total, List<Sample> tail)
    {
        var sb = new StringBuilder(64 * (total + tail.Count));
        void Line(in Sample s, int expectW, int expectH, double setMs)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{{\"k\":{s.Iteration},\"qpc\":{s.Qpc},\"acc\":{s.Accumulated},\"expectW\":{expectW},\"expectH\":{expectH},\"contentW\":{s.ContentW},\"contentH\":{s.ContentH},\"right\":{(int)s.Right},\"bottom\":{(int)s.Bottom},\"contentStep\":{s.ContentStep}}}")
              .Append('\n');
        }
        for (int k = 1; k <= total; k++) Line(samples[k], w[k - 1], h[k - 1], 0);
        foreach (Sample s in tail) Line(s, w[total], h[total], 0);
        File.WriteAllText(Path.ChangeExtension(_options.OutputPath, ".resize.jsonl"), sb.ToString());
    }
}

/// <summary>
/// theme-switch. Calibrates each host's own Dark and Light rendering of a fixed set of sample regions (window
/// background, card, thumbnail, accent hero, hero title text, side-list text), then alternates the theme. A switch is
/// complete at the first composed frame in which the frame-ID probe carries the switch's id AND every region matches the
/// new theme's reference; frames in between that show some regions re-themed and others not are counted as mixed.
/// </summary>
internal sealed class ThemeRun
{
    private readonly BenchOptions _options;
    private readonly LiveWindowTarget _target;
    private readonly nint _hwnd;
    private readonly double _scale;
    private long[] _stamp = [];
    private long[] _stampEnd = [];

    /// <summary>Sample regions in client DIP on the detail page (x, y, w, h). Small ones are solid surfaces; the two
    /// large ones average a block of text, so they track text colour rather than one glyph's anti-aliasing.</summary>
    private static readonly (string Name, int X, int Y, int W, int H)[] Regions =
    [
        ("window-bg", 800, 380, 6, 40),
        ("card", 116, 200, 16, 60),
        ("thumb", 30, 200, 60, 60),
        ("accent-hero", 60, 40, 200, 100),
        ("hero-text", 330, 60, 260, 64),
        ("list-text", 814, 14, 150, 660),
    ];

    internal ThemeRun(BenchOptions options, LiveWindowTarget target)
    {
        _options = options;
        _target = target;
        _hwnd = target.Hwnd;
        _scale = Native.GetDpiForWindow(_hwnd) / 96d;
    }

    private int Px(int dip) => (int)Math.Round(dip * _scale);

    internal BenchResult Execute()
    {
        if (_target.ApplyThemeOnUi is null) throw new InvalidOperationException("theme-switch needs ApplyThemeOnUi.");
        int switches = Math.Min(_options.Iterations, BenchWorkload.ThemeMaxSwitches);
        int warmup = Math.Min(_options.WarmupFrames, BenchWorkload.ThemeWarmupSwitches);
        int total = warmup + switches;
        _stamp = new long[total];
        _stampEnd = new long[total];

        Native.GetWindowRect(_hwnd, out Native.RECT wr);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 32, 32, wr.Right - wr.Left, wr.Bottom - wr.Top, Native.SWP_NOACTIVATE);
        string occlusion = Native.OcclusionNote(_hwnd, Px(400), Px(300));

        var origin0 = new Native.POINT();
        Native.ClientToScreen(_hwnd, ref origin0);
        // Staging layout: the frame-ID probe, then every region, stacked vertically at x = 0.
        _slotY = new int[Regions.Length];
        int y = Px(FrameIdProbe.SizePx) + 4, width = Px(FrameIdProbe.SizePx);
        for (int i = 0; i < Regions.Length; i++)
        {
            _slotY[i] = y;
            y += Px(Regions[i].H) + 4;
            width = Math.Max(width, Px(Regions[i].W));
        }
        using var dup = new DesktopDuplication(origin0.X, origin0.Y, width, y);

        // Calibration: Dark (the start theme on both hosts), then Light. References are the host's own pixels.
        double[][] refDark = Calibrate(dup, dark: true);
        DesktopCapture.SaveClient(_hwnd, Path.ChangeExtension(_options.OutputPath, null) + ".theme-dark.bmp");
        double[][] refLight = Calibrate(dup, dark: false);
        DesktopCapture.SaveClient(_hwnd, Path.ChangeExtension(_options.OutputPath, null) + ".theme-light.bmp");

        var used = new List<int>();
        var dropped = new List<string>();
        for (int i = 0; i < Regions.Length; i++)
        {
            if (L1(refDark[i], refLight[i]) >= 24) used.Add(i);
            else dropped.Add(Regions[i].Name);
        }
        if (used.Count < 4)
            throw new InvalidOperationException($"theme: only {used.Count} regions differ between themes ({string.Join(",", dropped)} do not).");

        var latency = new List<double>(switches);
        var mixed = new List<double>(switches);
        var callMs = new List<double>(switches);
        var hopMs = new List<double>(switches);
        int timeouts = 0, regressed = 0, mixedFramesTotal = 0, framesBeforeId = 0;
        long measureStart = 0;
        var log = new StringBuilder();

        for (int i = 0; i < total; i++)
        {
            bool dark = (i & 1) == 0;   // calibration ended in Light, so switch 0 goes to Dark
            double[][] want = dark ? refDark : refLight;
            double[][] old = dark ? refLight : refDark;
            int id = i + 1;
            long posted = Stopwatch.GetTimestamp();
            if (i == warmup) measureStart = posted;
            Post(i, id, dark);

            long deadline = posted + Stopwatch.Frequency * BenchWorkload.ThemeTimeoutMs / 1000;
            int mixedFrames = 0, beforeId = 0;
            bool done = false;
            double lat = double.NaN;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (!Snap(dup, 50, out long present, out int seenId, out double[][] colors)) continue;
                long stamp = Interlocked.Read(ref _stamp[i]);
                if (stamp == 0 || present <= stamp) continue;   // composed before the UI thread ran the switch
                int nNew = 0;
                foreach (int r in used) if (Matches(colors[r], want[r], old[r])) nNew++;
                if (nNew == used.Count && seenId == id)
                {
                    lat = LiveWindowDriver.Ms(present - stamp);
                    done = true;
                    break;
                }
                if (seenId != id && nNew == 0) beforeId++;
                else mixedFrames++;   // part of the window is in the new theme, part is not
            }

            int heldBad = 0;
            if (done)
            {
                long holdEnd = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;   // up to 6 frames or 100 ms
                for (int h = 0; h < BenchWorkload.ThemeHoldFrames && Stopwatch.GetTimestamp() < holdEnd;)
                {
                    if (!Snap(dup, 20, out _, out _, out double[][] colors)) continue;   // nothing changed: still themed
                    h++;
                    foreach (int r in used)
                        if (!Matches(colors[r], want[r], old[r])) { heldBad++; break; }
                }
            }
            string latText = done ? lat.ToString("0.###", CultureInfo.InvariantCulture) : "null";
            log.Append(CultureInfo.InvariantCulture,
                $"{{\"i\":{i},\"dark\":{(dark ? "true" : "false")},\"latencyMs\":{latText},\"mixedFrames\":{mixedFrames},\"framesBeforeSwitchVisible\":{beforeId},\"heldBad\":{heldBad},\"callMs\":{LiveWindowDriver.Ms(_stampEnd[i] - _stamp[i]):0.###},\"hopMs\":{LiveWindowDriver.Ms(_stamp[i] - posted):0.###}}}").Append('\n');
            if (i < warmup) continue;
            if (!done) { timeouts++; continue; }
            latency.Add(lat);
            mixed.Add(mixedFrames);
            mixedFramesTotal += mixedFrames;
            framesBeforeId += beforeId;
            if (heldBad > 0) regressed++;
            callMs.Add(LiveWindowDriver.Ms(_stampEnd[i] - _stamp[i]));
            hopMs.Add(LiveWindowDriver.Ms(_stamp[i] - posted));
        }
        long stop = Stopwatch.GetTimestamp();
        File.WriteAllText(Path.ChangeExtension(_options.OutputPath, ".theme.jsonl"), log.ToString());

        var metrics = new Dictionary<string, double>
        {
            ["switches"] = switches,
            ["timeouts"] = timeouts,
            ["switchesWithMixedFrames"] = mixed.Count(static m => m > 0),
            ["mixedFramesTotal"] = mixedFramesTotal,
            ["framesBeforeSwitchVisible"] = framesBeforeId,
            ["regressedAfterComplete"] = regressed,
            ["regionsUsed"] = used.Count,
            ["scale"] = _scale,
        };
        var series = new Dictionary<string, double[]>
        {
            ["mixedFrames"] = mixed.ToArray(),
            ["uiSwitchCallMs"] = callMs.ToArray(),
            ["uiHopMs"] = hopMs.ToArray(),
        };
        string regionList = string.Join(",", used.Select(static r => Regions[r].Name)) +
            (dropped.Count > 0 ? "; identical in both themes, not used: " + string.Join(",", dropped) : "");
        string notes = string.Create(CultureInfo.InvariantCulture,
            $"Desktop-observed theme switch (DXGI desktop duplication, every composed frame): {switches} alternating switches (+{warmup} warmup), each judged on {used.Count} calibrated regions ({regionList}) plus the frame-ID probe. frameMs = UI-thread switch call to the present time of the first composed frame fully in the new theme. {occlusion}");
        return LiveWindowDriver.Finish(_options, _target, measureStart, stop, latency.ToArray(), warmup, notes, metrics, series);
    }

    private int[] _slotY = [];

    private void Post(int index, int id, bool dark)
    {
        int i = index;
        _target.PostToUi(() =>
        {
            if (i >= 0) Interlocked.Exchange(ref _stamp[i], Stopwatch.GetTimestamp());
            _target.ApplyThemeOnUi!(id, dark);
            if (i >= 0) Interlocked.Exchange(ref _stampEnd[i], Stopwatch.GetTimestamp());
        });
    }

    /// <summary>The next composed frame: its present time, the decoded frame-ID probe and every region's mean colour.</summary>
    private bool Snap(DesktopDuplication dup, int timeoutMs, out long present, out int probeId, out double[][] colors)
    {
        probeId = -1;
        colors = [];
        if (!dup.Acquire(timeoutMs, out present, out _)) return false;
        var origin = new Native.POINT();
        Native.ClientToScreen(_hwnd, ref origin);
        int probe = Px(FrameIdProbe.SizePx);
        dup.Copy(origin.X + Px(FrameIdProbe.ClientX), origin.Y + Px(FrameIdProbe.ClientY), probe, probe, 0, 0);
        for (int r = 0; r < Regions.Length; r++)
            dup.Copy(origin.X + Px(Regions[r].X), origin.Y + Px(Regions[r].Y), Px(Regions[r].W), Px(Regions[r].H), 0, _slotY[r]);
        dup.EndFrame();
        try
        {
            (byte pr, byte pg, byte pb) = dup.Pixel(probe / 2, probe / 2);
            probeId = FrameIdProbe.TryDecode(pr, pg, pb, out int id) ? id : -1;
            colors = new double[Regions.Length][];
            for (int r = 0; r < Regions.Length; r++)
                colors[r] = dup.Average(0, _slotY[r], Px(Regions[r].W), Px(Regions[r].H));
        }
        finally { dup.Unmap(); }
        return true;
    }

    /// <summary>Switch to <paramref name="dark"/> (unmeasured), wait for its probe id, let any animation finish (500 ms),
    /// then wiggle nothing and average the next frames' region colours: the host's own reference rendering of that
    /// theme. Duplication only delivers changed frames, so the probe frame itself is used if the desktop goes still.</summary>
    private double[][] Calibrate(DesktopDuplication dup, bool dark)
    {
        int id = 16000 + (dark ? 1 : 2);
        Post(-1, id, dark);
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        int seen = -1;
        double[][] last = [];
        while (seen != id)
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"theme: calibration id {id} never appeared.");
            Snap(dup, 50, out _, out seen, out last);
        }
        // Let any animation finish, keeping the latest frame: if the screen is still, the last frame IS the reference.
        long settle = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        while (Stopwatch.GetTimestamp() < settle)
            if (Snap(dup, 20, out _, out int pid, out double[][] colors) && pid == id) last = colors;
        return last;
    }

    /// <summary>Closer to <paramref name="target"/> than a quarter of the way from it to <paramref name="other"/>.</summary>
    private static bool Matches(double[] c, double[] target, double[] other)
        => L1(c, target) <= Math.Max(4d, 0.25 * L1(target, other));

    private static double L1(double[] a, double[] b) => Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);
}

/// <summary>A GDI capture of a desktop rectangle into a top-down 32-bpp DIB (one allocation, reused).</summary>
internal sealed unsafe class DesktopCapture : IDisposable
{
    private readonly nint _screenDc, _memDc, _bitmap, _previous;
    private readonly byte* _bits;
    private readonly int _width, _height;
    private int _grabW, _grabH;

    internal DesktopCapture(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _screenDc = Native.GetDC(0);
        _memDc = Native.CreateCompatibleDC(_screenDc);
        var info = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER),
            biWidth = _width,
            biHeight = -_height,
            biPlanes = 1,
            biBitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_memDc, ref info, 0, out nint bits, 0, 0);
        if (_bitmap == 0) throw new InvalidOperationException("CreateDIBSection failed.");
        _bits = (byte*)bits;
        _previous = Native.SelectObject(_memDc, _bitmap);
        _grabW = _width; _grabH = _height;
    }

    internal void Grab(int screenX, int screenY) => Grab(screenX, screenY, _width, _height);

    internal void Grab(int screenX, int screenY, int w, int h)
    {
        _grabW = Math.Min(w, _width);
        _grabH = Math.Min(h, _height);
        Native.BitBlt(_memDc, 0, 0, _grabW, _grabH, _screenDc, screenX, screenY, Native.SRCCOPY);
        Native.GdiFlush();
    }

    internal (byte R, byte G, byte B) Pixel(int x, int y)
    {
        x = Math.Clamp(x, 0, _grabW - 1);
        y = Math.Clamp(y, 0, _grabH - 1);
        byte* p = _bits + ((long)y * _width + x) * 4;
        return (p[2], p[1], p[0]);
    }

    internal double[] Average()
    {
        double r = 0, g = 0, b = 0;
        for (int y = 0; y < _grabH; y++)
        {
            byte* row = _bits + (long)y * _width * 4;
            for (int x = 0; x < _grabW; x++)
            {
                b += row[x * 4]; g += row[x * 4 + 1]; r += row[x * 4 + 2];
            }
        }
        double n = Math.Max(1, _grabW * _grabH);
        return [r / n, g / n, b / n];
    }

    /// <summary>Screenshot of a window's client area as a 32-bpp BMP (parity evidence).</summary>
    internal static void SaveClient(nint hwnd, string path)
    {
        Native.GetClientRect(hwnd, out Native.RECT rc);
        var origin = new Native.POINT();
        Native.ClientToScreen(hwnd, ref origin);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        using var cap = new DesktopCapture(w, h);
        cap.Grab(origin.X, origin.Y);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);
        int size = w * h * 4;
        writer.Write((byte)'B'); writer.Write((byte)'M');
        writer.Write(54 + size); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(w); writer.Write(-h); writer.Write((short)1); writer.Write((short)32);
        writer.Write(0); writer.Write(size); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(new ReadOnlySpan<byte>(cap._bits, size));
    }

    public void Dispose()
    {
        Native.SelectObject(_memDc, _previous);
        Native.DeleteObject(_bitmap);
        Native.DeleteDC(_memDc);
        Native.ReleaseDC(0, _screenDc);
    }
}

internal static partial class Native
{
    internal static readonly nint HWND_TOPMOST = -1;
    internal const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    internal const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [LibraryImport("user32.dll")] internal static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetClientRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ClientToScreen(nint hwnd, ref POINT point);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] internal static partial nint WindowFromPoint(POINT point);
    [LibraryImport("user32.dll")] internal static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] internal static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll")] internal static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] internal static partial nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] internal static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BitBlt(nint dst, int x, int y, int cx, int cy, nint src, int x1, int y1, uint rop);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GdiFlush();
    [LibraryImport("dwmapi.dll")] internal static partial int DwmFlush();

    /// <summary>Which top-level window owns the screen point at client (x, y) px; the run is only valid if it is ours.
    /// WinUI hosts its content in a child HWND, so the root ancestor is compared.</summary>
    internal static string OcclusionNote(nint hwnd, int clientX, int clientY)
    {
        var p = new POINT { X = clientX, Y = clientY };
        ClientToScreen(hwnd, ref p);
        nint hit = WindowFromPoint(p);
        nint root = hit == 0 ? 0 : GetAncestor(hit, 2 /* GA_ROOT */);
        if (root != hwnd) throw new InvalidOperationException($"window is occluded at ({p.X},{p.Y}) by 0x{root:X}.");
        return "occlusion=none.";
    }
}
