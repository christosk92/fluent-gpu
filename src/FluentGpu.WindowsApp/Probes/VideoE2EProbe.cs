using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu;

/// <summary>
/// <c>--video-e2e [outDir] [--seconds N] [--switches N] [--cycles N]</c>: the automated, timed, end-to-end video test of
/// the real gallery host (D3D12 + DirectComposition + Media Foundation). It drives the same shapes Wavee's video pop-out
/// does: the main window scrolls a 100k-row virtualized list every frame (the hidden <c>video-e2e</c> gallery page, via
/// <see cref="GalleryShell.StressNavigate"/> + <see cref="ScrollHandle.ScrollTo"/>) while a detached child
/// (<see cref="AppHost.OpenDetachedWindow"/>) hosts a real <see cref="MediaPlayerElement"/> over a real
/// <see cref="MediaPlayer"/>/<see cref="MfMediaPlayer"/> playing a clear MP4. Scenarios, in order, all timed with QPC:
/// S1 baseline (no pop-out), S2 pop-out with video, S3 UI actions (popup open/close + main-window resize) with the pop-out
/// playing, S4 source switching, S5 placement churn (animated video element in the main window), S6 pop-out
/// close / cold open / warm (parked) reopen. It reads the engine's own evidence rather than re-deriving it: the
/// <see cref="PresentLedger"/> (fresh-present QPC of the main window and of the child), the shared D3D12 device's
/// liveness-timeout counters, <see cref="MediaPlayer.Statistics"/> (the MF rendered/dropped counters), the open-timing
/// split (<see cref="IDetachedVideoWindow.OnRevealed"/> / <see cref="IDetachedVideoWindow.OnFirstVideoBound"/>) and the
/// always-on <c>[video] stream.size</c> / <c>[video.surface] place</c> lines (teed off stderr).
///
/// Writes <c>outDir/video-e2e.json</c> and <c>outDir/video-e2e.md</c> (always, even after a failure or abort). Exit code:
/// 0 = every thresholded metric PASS, 2 = at least one FAIL, 1 = the run could not complete (a scenario timed out or
/// threw). Needs a live composited desktop and a GPU; not a CI gate. Thresholds are the audit's measurement plan and are
/// never tuned here.
/// </summary>
internal static class VideoE2EProbe
{
    public readonly record struct Config(string OutDir, double Seconds, int Switches, int Cycles);

    /// <summary>Set by Program.cs from the CLI; null unless <c>--video-e2e</c> was passed.</summary>
    public static Config? Args;

    /// <summary>The verdict, read by Program.cs after the harness returns (see <see cref="DetachedStressProbe.ExitCode"/>).</summary>
    public static int ExitCode;

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (Args is not { } cfg) return false;
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("[video-e2e] needs the Win32 + D3D12 backend (GPU required).");
            ExitCode = 1;
            return true;
        }
        ExitCode = Run(cfg, host, w, gpu);
        return true;
    }

    private static int Run(Config cfg, AppHost host, Win32Window window, D3D12Device gpu)
    {
        Directory.CreateDirectory(cfg.OutDir);
        string logPath = Path.Combine(cfg.OutDir, "video-e2e.log");
        var originalErr = Console.Error;
        using var fileWriter = new StreamWriter(File.Create(logPath), Encoding.UTF8) { AutoFlush = true };
        var e2e = new E2E(cfg, host, window, gpu);
        var tee = new TeeTextWriter(originalErr, fileWriter, e2e.ObserveLine);
        Console.SetError(tee);
        try { return e2e.Execute(); }
        finally
        {
            Console.Error.Flush();
            Console.SetError(originalErr);
        }
    }

    // ── stderr tee ───────────────────────────────────────────────────────────────────────────────────────────────────
    private sealed class TeeTextWriter(TextWriter a, TextWriter b, Action<string> observe) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { if (value is null) return; a.Write(value); b.Write(value); }
        public override void WriteLine(string? value)
        {
            value ??= string.Empty;
            observe(value);
            a.WriteLine(value);
            b.WriteLine(value);
        }
        public override void Flush() { a.Flush(); b.Flush(); }
    }

    // ── result model ─────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly record struct Dist(int N, double P50, double P95, double P99, double Max, int Over15, int Over20);

    private sealed record Metric(string Scenario, string Name, string Value, string Threshold, string Status);

    private sealed class ScenarioResult(string id, string name)
    {
        public string Id = id, Name = name;
        public bool Completed;
        public double DurationMs;
        public string Error = "";
        public readonly List<(string Key, string Json)> Raw = new();
        public readonly List<string> Notes = new();
    }

    /// <summary>Accumulates MF rendered/dropped frames for one player across source switches by DELTA (a new session's counters
    /// restart low; a stale unchanged read adds nothing).</summary>
    private sealed class FrameAcc
    {
        private long _lastR = -1, _lastD = -1;
        public long Rendered, Dropped;
        public void Poll(IMediaPlayer? p)
        {
            if (p is null) return;
            PlaybackStatistics s = p.Statistics.Peek();
            long r = s.FramesRendered, d = s.FramesDropped;
            if (_lastR < 0) { _lastR = r; _lastD = d; return; }
            if (r != _lastR) { Rendered += r >= _lastR ? r - _lastR : r; _lastR = r; }
            if (d != _lastD) { Dropped += d >= _lastD ? d - _lastD : d; _lastD = d; }
        }
        public double DropPct => Rendered + Dropped == 0 ? 0 : 100.0 * Dropped / (Rendered + Dropped);
    }

    private sealed class ChildRun
    {
        public IDetachedVideoWindow Win = null!;
        public long OpenQpc;
        public bool Revealed;
        public DetachedOpenTiming Timing;
        public long RevealedObservedQpc;
        public double BindMs = -1;
        public long BoundObservedQpc;
    }

    // ── the run ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private sealed class E2E
    {
        private const double VsyncOverDouble = 2.0, VsyncOverOneAndHalf = 1.5;
        private const double SlotTimeoutTargetCount = 0, UiBlockTargetMs = 50.0, UiBlockStretchMs = 5.0;
        private const double OpenToPresentTargetMs = 150.0, SwitchTargetMs = 700.0, DropTargetPct = 1.0;
        private const int StreamUpdateTarget = 5;

        private readonly Config _cfg;
        private readonly AppHost _host;
        private readonly Win32Window _window;
        private readonly D3D12Device _gpu;
        private readonly double _freq = Stopwatch.Frequency;

        private readonly List<ScenarioResult> _scenarios = new();
        private readonly List<Metric> _metrics = new();
        private ScenarioResult _cur = null!;
        private bool _incomplete;
        private bool _aborted;
        private long _scenarioDeadlineQpc;

        private double _hz, _vsyncMs;
        private string _fixtureA = "", _fixtureB = "";

        private MediaPlayer? _popPlayer, _mainPlayer;
        private readonly FrameAcc _popFrames = new(), _mainFrames = new();
        private ChildRun? _child;

        // stderr counters (tee thread: any thread)
        private long _streamSizeLines, _placeLines, _attachLines;

        // UI-loop measurement (resettable)
        private long _prevStart;
        private double _gapMax, _busyMax;
        private int _powerCapIterations, _iterations;
        private readonly int[] _waitKinds = new int[16];

        // present tracing
        private List<double>? _mainSink, _childSink;
        private ulong _mainCursor, _childCursor;
        private long _mainLastDone, _childLastDone, _childFloorQpc, _childFirstPresentQpc;

        // scrolling
        private ScrollHandle? _scroll;
        private double _off, _dir = 1.0;

        public E2E(Config cfg, AppHost host, Win32Window window, D3D12Device gpu)
        { _cfg = cfg; _host = host; _window = window; _gpu = gpu; }

        public void ObserveLine(string line)
        {
            if (line.StartsWith("[video] stream.size", StringComparison.Ordinal)) System.Threading.Interlocked.Increment(ref _streamSizeLines);
            else if (line.StartsWith("[video.surface] place", StringComparison.Ordinal)) System.Threading.Interlocked.Increment(ref _placeLines);
            else if (line.StartsWith("[detached] attach", StringComparison.Ordinal)) System.Threading.Interlocked.Increment(ref _attachLines);
        }

        private long Now() => Stopwatch.GetTimestamp();
        private double Ms(long ticks) => ticks * 1000.0 / _freq;
        private static string F(double v, string fmt = "0.00") => v.ToString(fmt, CultureInfo.InvariantCulture);
        private long Timeouts() => _gpu.SlotLivenessTimeouts + _gpu.NonPrimaryLatencyTimeouts;

        // ── driver ───────────────────────────────────────────────────────────────────────────────────────────────────
        public int Execute()
        {
            RectF origBounds = _window.OuterBoundsPx;
            long runStart = Now();
            try
            {
                if (!ResolveFixtures(out string why))
                {
                    Console.Error.WriteLine($"[video-e2e] {why}");
                    _incomplete = true;
                    Fail("setup", why);
                    return Finish(runStart);
                }
                BringToFront();
                Scenario("S1", "Baseline: main window only, list scrolling", 40_000, S1);
                Scenario("S2", "Pop-out with video", 90_000, S2);
                Scenario("S3", "UI actions while the pop-out plays", 90_000, S3);
                Scenario("S4", "Source switching", 120_000, S4);
                Scenario("S5", "Placement churn (animated video element, main window)", 60_000, S5);
                Scenario("S6", "Pop-out close, cold open and warm reopen", 60_000, S6);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[video-e2e] run aborted: {ex}");
                _incomplete = true;
                Fail("run", ex.Message);
            }
            finally
            {
                Cleanup(origBounds);
            }
            return Finish(runStart);
        }

        private void Fail(string id, string msg)
        {
            var r = new ScenarioResult(id, id) { Error = msg };
            _scenarios.Add(r);
        }

        private void Scenario(string id, string name, int budgetMs, Action body)
        {
            _cur = new ScenarioResult(id, name);
            _scenarios.Add(_cur);
            _aborted = false;
            long start = Now();
            _scenarioDeadlineQpc = start + (long)(budgetMs / 1000.0 * _freq);
            Console.Error.WriteLine($"[video-e2e] {id} begin: {name}");
            try { body(); _cur.Completed = !_aborted; }
            catch (Exception ex)
            {
                _cur.Error = ex.GetType().Name + ": " + ex.Message;
                Console.Error.WriteLine($"[video-e2e] {id} threw: {ex}");
            }
            _cur.DurationMs = Ms(Now() - start);
            if (!_cur.Completed)
            {
                _incomplete = true;
                if (_cur.Error.Length == 0) _cur.Error = _window.IsClosed ? "window closed" : "scenario time budget exceeded";
            }
            _mainSink = null; _childSink = null;
            Console.Error.WriteLine($"[video-e2e] {id} end completed={_cur.Completed} ms={F(_cur.DurationMs, "0")}");
        }

        private bool ResolveFixtures(out string why)
        {
            why = "";
            const string rel = "src/FluentGpu.Windows.Tests/Fixtures/video/bear-1280x720-av_frag.mp4";
            string? found = null;
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                string? d = Path.GetFullPath(start);
                for (int i = 0; i < 10 && d is not null; i++, d = Path.GetDirectoryName(d))
                {
                    string cand = Path.Combine(d, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(cand)) { found = cand; break; }
                }
                if (found is not null) break;
            }
            if (found is null) { why = "clear test fixture not found (" + rel + ") above " + AppContext.BaseDirectory; return false; }
            _fixtureA = found;
            // A second, distinct URL for the same clip, so a switch is never a same-URL no-op.
            _fixtureB = Path.Combine(Path.GetFullPath(_cfg.OutDir), "bear-b.mp4");
            File.Copy(_fixtureA, _fixtureB, overwrite: true);
            return true;
        }

        [DllImport("user32.dll")] private static extern int ShowWindow(nint hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hWnd);
        private void BringToFront()
        {
            try { ShowWindow(_window.Handle.Value, 5); SetForegroundWindow(_window.Handle.Value); } catch { }
        }

        // ── frame loop ───────────────────────────────────────────────────────────────────────────────────────────────
        private void ResetUi() { Array.Clear(_waitKinds); _gapMax = 0; _busyMax = 0; _powerCapIterations = 0; _iterations = 0; _prevStart = 0; }

        /// <summary>One turn of the gallery's real loop (RunFrame, TickDetachedHosts, the typed wait), timed. The wait is
        /// capped at 16 ms so an idle/slow wait never starves the probe's own timers.</summary>
        private void Step(bool scroll)
        {
            if (_window.IsClosed) { _aborted = true; return; }
            if (Now() > _scenarioDeadlineQpc) { _aborted = true; return; }
            long t0 = Now();
            if (_prevStart != 0) { double gap = Ms(t0 - _prevStart); if (gap > _gapMax) _gapMax = gap; }
            if (scroll) ScrollTick();
            _host.RunFrame();
            _host.TickDetachedHosts();
            PollPresents();
            _popFrames.Poll(_popPlayer);
            _mainFrames.Poll(_mainPlayer);
            long t1 = Now();
            double busy = Ms(t1 - t0);
            if (busy > _busyMax) _busyMax = busy;
            _lastBusy = busy;
            _iterations++;
            if (_host.LastWaitKind == HostWaitKind.PowerCap) _powerCapIterations++;   // the one policy ceiling (energy saver); there is no focus throttle
            _waitKinds[(int)_host.LastWaitKind & 15]++;
            if (_child is { } ch && ch.Win.IsOpen && !ch.Win.IsParked) VideoE2EState.ChildTick.Value++;
            var wait = _host.WaitRequestWithDetached();
            // The probe's scroll drive is a synthetic input the host does not see as scroll motion, so it recommends Idle (-1) here.
            // Wake on the compositor tick (capped at 16 ms) so the loop turns once per vblank like a real scroll, not once per timer.
            if (wait.TimeoutMs < 0 || wait.TimeoutMs > 16) _window.WaitForWork(new PlatformWaitRequest(16, PlatformInputWakePolicy.Immediate, true));
            else _window.WaitForWork(in wait);
            _prevStart = t0;
        }
        private double _lastBusy;

        private bool PumpUntil(Func<bool> cond, double timeoutMs, bool scroll = true)
        {
            long end = Now() + (long)(timeoutMs / 1000.0 * _freq);
            while (!_aborted && Now() < end)
            {
                Step(scroll);
                if (cond()) return true;
            }
            return !_aborted && cond();
        }

        private void PumpFor(double ms, bool scroll = true)
        {
            long end = Now() + (long)(ms / 1000.0 * _freq);
            while (!_aborted && Now() < end) Step(scroll);
        }

        private void PumpFrames(int n, bool scroll = true)
        {
            for (int i = 0; i < n && !_aborted; i++) Step(scroll);
        }

        // ── scrolling ────────────────────────────────────────────────────────────────────────────────────────────────
        private void FindScroll()
        {
            ScrollHandle? best = null;
            double bestExtent = 0;
            void Walk(NodeHandle n)
            {
                if (n.IsNull) return;
                if (_host.Scene.HasScroll(n) && _host.TryGetScrollHandle(n) is { } h && h.Extent > bestExtent) { best = h; bestExtent = h.Extent; }
                for (var c = _host.Scene.FirstChild(n); !c.IsNull; c = _host.Scene.NextSibling(c)) Walk(c);
            }
            Walk(_host.Scene.Root);
            _scroll = best;
        }

        private void ScrollTick()
        {
            if (_scroll is null) return;
            double max = Math.Min(400_000.0, _scroll.MaxOffset);
            if (max <= 0) max = 400_000.0;
            _off += _dir * 16.0;
            if (_off > max) { _off = max; _dir = -1.0; }
            else if (_off < 0) { _off = 0; _dir = 1.0; }
            _scroll.ScrollTo(_off, ScrollMove.Immediate);
        }

        private void NavigateToProbePage()
        {
            // The probe page is a HIDDEN gallery page: Program.cs deep-links it through GalleryShell.InitialPage (a hidden key
            // is not in the nav tree, so the StressNavigate hook is only the fallback).
            PumpFrames(40, scroll: false);
            for (int i = 0; i < 5 && ScrollExtent() < 1_000_000.0; i++)
            {
                FindScroll();
                if (ScrollExtent() >= 1_000_000.0) break;
                if (i == 2) GalleryShell.StressNavigate?.Invoke("video-e2e");
                PumpFrames(20, scroll: false);
            }
            Raw("scrollExtentDip", ScrollExtent());
            if (ScrollExtent() < 1_000_000.0)
            {
                _cur.Notes.Add("the 100k-row list was not found: the main window ran frames without the intended scroll workload");
                _incomplete = true;
            }
        }

        private double ScrollExtent() => _scroll?.Extent ?? 0.0;

        // ── present tracing (PresentLedger) ──────────────────────────────────────────────────────────────────────────
        private double _mainGapSinceMark;

        private void PollPresents()
        {
            // The cursor is a publication seq; if the seq space restarts under us (a re-seated seam), nothing at/after the cursor
            // ever appears again. After 300 ms without an accepted present, re-scan from 0 (the DoneQpc dedupe skips old rows).
            long now = Now();
            bool mainGot = false, childGot = false;
            for (int guard = 0; guard < 80; guard++)
            {
                if (!PresentLedger.TryFindFirstAtOrAfter(_mainCursor, out PresentRecord r)) break;
                _mainCursor = r.PublishSeq + 1;
                if (r.DoneQpc <= _mainLastDone) continue;
                if (_mainLastDone != 0)
                {
                    double iv = Ms(r.DoneQpc - _mainLastDone);
                    _mainSink?.Add(iv);
                    if (iv > _mainGapSinceMark) _mainGapSinceMark = iv;
                }
                _mainLastDone = r.DoneQpc;
                mainGot = true;
            }
            if (!mainGot && _mainLastDone != 0 && Ms(now - _mainLastDone) > 300.0 && _mainCursor != 0) _mainCursor = 0;
            for (int guard = 0; guard < 80; guard++)
            {
                if (!PresentLedger.TryFindChildFirstAtOrAfter(-1, _childCursor, out PresentRecord r)) break;
                _childCursor = r.PublishSeq + 1;
                if (r.DoneQpc <= _childFloorQpc || r.DoneQpc <= _childLastDone) continue;
                if (_childFirstPresentQpc == 0) _childFirstPresentQpc = r.DoneQpc;
                if (_childLastDone != 0) _childSink?.Add(Ms(r.DoneQpc - _childLastDone));
                _childLastDone = r.DoneQpc;
                childGot = true;
            }
            if (!childGot && _childLastDone != 0 && Ms(now - _childLastDone) > 300.0 && _childCursor != 0) _childCursor = 0;
        }

        private Dist Summarize(List<double> v)
        {
            if (v.Count == 0) return default;
            var a = v.ToArray();
            Array.Sort(a);
            double P(int p) => a[Math.Clamp((int)Math.Round(p / 100.0 * (a.Length - 1)), 0, a.Length - 1)];
            int o15 = 0, o20 = 0;
            foreach (double x in a) { if (x > _vsyncMs * VsyncOverOneAndHalf) o15++; if (x > _vsyncMs * VsyncOverDouble) o20++; }
            return new Dist(a.Length, P(50), P(95), P(99), a[^1], o15, o20);
        }

        // ── reporting helpers ────────────────────────────────────────────────────────────────────────────────────────
        private void Raw(string key, double v) => _cur.Raw.Add((key, Num(v)));
        private void RawS(string key, string v) => _cur.Raw.Add((key, "\"" + Esc(v) + "\""));
        private void RawDist(string key, Dist d) => _cur.Raw.Add((key,
            $"{{\"n\":{d.N},\"p50\":{Num(d.P50)},\"p95\":{Num(d.P95)},\"p99\":{Num(d.P99)},\"max\":{Num(d.Max)},\"over1_5x\":{d.Over15},\"over2x\":{d.Over20}}}"));
        private void RawWaitKinds()
        {
            var sb = new StringBuilder("{");
            bool first = true;
            for (int i = 0; i < _waitKinds.Length; i++)
            {
                if (_waitKinds[i] == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(((HostWaitKind)i).ToString()).Append("\":").Append(_waitKinds[i]);
            }
            _cur.Raw.Add(("hostWaitKindTurns", sb.Append('}').ToString()));
        }
        private void RawList(string key, List<double> v)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < v.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Num(v[i])); }
            _cur.Raw.Add((key, sb.Append(']').ToString()));
        }
        private static string Num(double v) => double.IsFinite(v) ? v.ToString("0.####", CultureInfo.InvariantCulture) : "null";
        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append(' '); else sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private void Info(string name, string value) => _metrics.Add(new Metric(_cur.Id, name, value, "-", "INFO"));
        private void Check(string name, string value, string threshold, bool pass) => _metrics.Add(new Metric(_cur.Id, name, value, threshold, pass ? "PASS" : "FAIL"));
        private void NoMeasure(string name, string why) { _metrics.Add(new Metric(_cur.Id, name, "not measured: " + why, "-", "N/A")); _incomplete = true; }

        private void IntervalInfo(string label, Dist d, string rawKey)
        {
            RawDist(rawKey, d);
            if (d.N == 0) { NoMeasure(label + " present interval", "no presents recorded"); return; }
            Info(label + " present interval p50/p95/p99/max (ms)", $"{F(d.P50)} / {F(d.P95)} / {F(d.P99)} / {F(d.Max)}  (n={d.N})");
            Info(label + " intervals > 1.5x / > 2x vsync", $"{d.Over15} / {d.Over20}  (vsync {F(_vsyncMs, "0.000")} ms)");
        }

        // ── players + media ──────────────────────────────────────────────────────────────────────────────────────────
        private static MediaPlayer NewPlayer() => MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer()).Build();
        private MediaSource SourceA() => MediaSource.FromFile(_fixtureA).Loop();
        private MediaSource SourceB() => MediaSource.FromFile(_fixtureB).Loop();

        private Task OpenAndPlay(MediaPlayer p, MediaSource s, TimeSpan start) => Task.Run(async () =>
        {
            try
            {
                await p.OpenAsync(s, new MediaOpenOptions { StartPaused = false, StartPosition = start }).ConfigureAwait(false);
                await p.PlayAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[video-e2e] open/play threw: {ex.Message}"); }
        });

        private static bool IsPlaying(IMediaPlayer p) => p.State.Peek() == PlaybackState.Playing;

        // ── pop-out helpers ──────────────────────────────────────────────────────────────────────────────────────────
        private ChildRun? OpenChild()
        {
            var run = new ChildRun { OpenQpc = Now() };
            _childFloorQpc = run.OpenQpc; _childFirstPresentQpc = 0; _childLastDone = 0; _childCursor = 0;
            IDetachedVideoWindow? w = _host.OpenDetachedWindow(new DetachedWindowRequest("Video E2E", new Size2(640f, 360f), new VideoE2EChild()));
            if (w is null) return null;
            run.Win = w;
            Wire(run);
            return run;
        }

        private void Wire(ChildRun run)
        {
            run.Win.OnRevealed = t => { run.Revealed = true; run.Timing = t; run.RevealedObservedQpc = Now(); };
            run.Win.OnFirstVideoBound = ms => { run.BindMs = ms; run.BoundObservedQpc = Now(); };
        }

        private bool CloseChild(ChildRun run, out double ms)
        {
            long t0 = Now();
            try { run.Win.Close(); } catch (Exception ex) { Console.Error.WriteLine($"[video-e2e] Close() threw: {ex.Message}"); }
            int budget = 400;
            while (run.Win.IsOpen && budget-- > 0 && !_window.IsClosed)
            {
                _host.RunFrame();
                _host.TickDetachedHosts();
                PollPresents();
                _window.WaitForWork(4);
            }
            ms = Ms(Now() - t0);
            return !run.Win.IsOpen;
        }

        // ── S1 ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private void S1()
        {
            var primary = DisplayInfo.ForPrimary();
            _hz = primary.Valid && primary.RefreshDenominator != 0 ? primary.RefreshNumerator / (double)primary.RefreshDenominator : _window.CurrentRefreshHz();
            if (_hz <= 1.0) _hz = 60.0;
            _vsyncMs = 1000.0 / _hz;
            Raw("refreshHz", _hz); Raw("windowRefreshHz", _window.CurrentRefreshHz()); Raw("vsyncMs", _vsyncMs);

            NavigateToProbePage();
            PumpFor(1500);   // warm: layout, atlas, scroll plan
            var sink = new List<double>(2048);
            _mainSink = sink;
            ResetUi();
            PumpFor(10_000);
            _mainSink = null;
            double clockPeriod = _host.RenderPace.ClockPeriodMs;
            Raw("clockPeriodMs", clockPeriod);
            Raw("powerCapIterations", _powerCapIterations); RawWaitKinds();
            Raw("uiGapMaxMs", _gapMax); Raw("uiBusyMaxMs", _busyMax);

            var d = Summarize(sink);
            Info("Display refresh rate (Hz) / vsync period (ms)", $"{F(_hz)} / {F(_vsyncMs, "0.000")}  (compositor beat {F(clockPeriod, "0.000")} ms)");
            IntervalInfo("S1 main", d, "mainInterval");
            Check("S1 baseline presents recorded", d.N.ToString(CultureInfo.InvariantCulture), ">= 300 intervals (valid baseline)", d.N >= 300);
            if (_powerCapIterations > _iterations / 4)
                _cur.Notes.Add($"frames were paced by the power cap (energy saver) in {_powerCapIterations}/{_iterations} loop turns: the baseline may under-report");
            S1Dist = d;
        }
        private Dist S1Dist;

        // ── S2 ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private void S2()
        {
            // Both players are built NOW, while the main host's poster is HostDispatch.Current: a player marshals through the poster
            // it captured at construction, and a pop-out host overwrites (then clears) that static once it opens and closes.
            _popPlayer = NewPlayer();
            _mainPlayer = NewPlayer();
            VideoE2EState.PopPlayer = _popPlayer;
            VideoE2EState.MainPlayer = _mainPlayer;
            _ = OpenAndPlay(_popPlayer, SourceA(), TimeSpan.Zero);
            bool playing = PumpUntil(() => IsPlaying(_popPlayer) && _popPlayer.Position.Peek() > TimeSpan.FromMilliseconds(300), 10_000);
            if (!playing) _cur.Notes.Add("pop-out player did not reach Playing before the gesture (continuing)");
            PumpFor(500);

            // The gesture: open the detached window while the player already plays (the hand-off Wavee does).
            long r0 = _popFrames.Rendered;
            _child = OpenChild();
            if (_child is null) { _cur.Error = "OpenDetachedWindow returned null"; _aborted = true; return; }
            var run = _child;
            long childOpenQpc = run.OpenQpc;
            double firstVisibleMs = -1;
            PumpUntil(() =>
            {
                if (firstVisibleMs < 0 && run.BindMs >= 0 && _popFrames.Rendered > r0 && !_popPlayer.VideoSurface.Peek().IsNone)
                    firstVisibleMs = Ms(Now() - childOpenQpc);
                return run.Revealed && firstVisibleMs >= 0;
            }, 8000);
            // let the ledger see the first present
            PumpFrames(6);
            double presentMs = _childFirstPresentQpc != 0 ? Ms(_childFirstPresentQpc - childOpenQpc) : (run.Revealed ? Ms(run.RevealedObservedQpc - childOpenQpc) : -1);
            RawS("firstPresentSource", _childFirstPresentQpc != 0 ? "PresentLedger child ring (render-thread DoneQpc)" : "reveal observed on UI thread");
            Raw("firstChildPresentMs", presentMs);
            Raw("revealFirstPresentMs", run.Timing.FirstPresentMs); Raw("revealRenderPresentMs", run.Timing.RenderPresentMs);
            Raw("windowCreateMs", run.Timing.WindowCreateMs); Raw("hostCtorMs", run.Timing.HostCtorMs); Raw("firstFrameMs", run.Timing.FirstFrameMs);
            Raw("revealTimedOut", run.Timing.TimedOut ? 1 : 0);
            Raw("firstVideoBindMs", run.BindMs); Raw("firstVideoFrameVisibleMs", firstVisibleMs);

            if (presentMs < 0) NoMeasure("Pop-out open -> first child present (ms)", "child never presented");
            else Check("Pop-out open -> first child present (ms)", F(presentMs, "0.0"), $"< {OpenToPresentTargetMs:0} ms", presentMs < OpenToPresentTargetMs);
            Info("  split: window create / host ctor / first frame / reveal (ms)",
                $"{F(run.Timing.WindowCreateMs, "0.0")} / {F(run.Timing.HostCtorMs, "0.0")} / {F(run.Timing.FirstFrameMs, "0.0")} / {F(run.Timing.FirstPresentMs, "0.0")}{(run.Timing.TimedOut ? " (reveal TIMEOUT)" : "")}");
            if (run.BindMs < 0) NoMeasure("Pop-out open -> first video bound (ms)", "no bind observed");
            else Check("Pop-out open -> first video bound (ms)", F(run.BindMs, "0.0"), $"< {SwitchTargetMs:0} ms (switch target; no separate audit target)", run.BindMs < SwitchTargetMs);
            if (firstVisibleMs < 0) NoMeasure("Pop-out open -> first video frame visible (ms)", "no rendered frame after bind");
            else Check("Pop-out open -> first video frame visible (ms)", F(firstVisibleMs, "0.0"), $"< {SwitchTargetMs:0} ms (switch target; no separate audit target)", firstVisibleMs < SwitchTargetMs);

            // Steady state: the pop-out plays, the main list scrolls.
            PumpFor(1000);
            var mainSink = new List<double>(4096); var childSink = new List<double>(4096);
            _mainSink = mainSink; _childSink = childSink;
            long to0 = Timeouts();
            long fr0 = _popFrames.Rendered, fd0 = _popFrames.Dropped;
            ResetUi();
            long streams0 = System.Threading.Interlocked.Read(ref _streamSizeLines);
            PumpFor(_cfg.Seconds * 1000.0);
            _mainSink = null; _childSink = null;
            long timeouts = Timeouts() - to0;
            long fr = _popFrames.Rendered - fr0, fd = _popFrames.Dropped - fd0;
            double dropPct = fr + fd == 0 ? 0 : 100.0 * fd / (fr + fd);
            var dm = Summarize(mainSink); var dc = Summarize(childSink);
            Raw("slotTimeouts", timeouts); Raw("slotLivenessTimeouts", _gpu.SlotLivenessTimeouts); Raw("nonPrimaryLatencyTimeouts", _gpu.NonPrimaryLatencyTimeouts);
            Raw("framesRendered", fr); Raw("framesDropped", fd); Raw("dropPct", dropPct);
            Raw("uiGapMaxMs", _gapMax); Raw("uiBusyMaxMs", _busyMax); Raw("powerCapIterations", _powerCapIterations); RawWaitKinds();
            Raw("streamSizeUpdates", System.Threading.Interlocked.Read(ref _streamSizeLines) - streams0);

            IntervalInfo("S2 main (pop-out open)", dm, "mainInterval");
            RawDist("childInterval", dc);
            if (dc.N > 0) Info("S2 child present interval p50/p95/p99/max (ms)", $"{F(dc.P50)} / {F(dc.P95)} / {F(dc.P99)} / {F(dc.Max)}  (n={dc.N}, > 1.5x {dc.Over15}, > 2x {dc.Over20})");
            else NoMeasure("S2 child present interval", "no child presents recorded");
            if (dm.N > 0 && S1Dist.N > 0)
            {
                double limit = S1Dist.P99 + _vsyncMs;
                Check("S2 main p99 interval vs baseline p99 + 1 vsync (ms)", $"{F(dm.P99)} vs {F(S1Dist.P99)} + {F(_vsyncMs)} = {F(limit)}", "p99 <= baseline p99 + 1 vsync", dm.P99 <= limit);
                Info("S2 main p99 delta vs S1 (ms)", F(dm.P99 - S1Dist.P99));
            }
            else NoMeasure("S2 main p99 interval vs baseline", "missing S1 or S2 samples");
            Check("S2 1000 ms latency/slot timeouts", timeouts.ToString(CultureInfo.InvariantCulture), "== 0", timeouts == SlotTimeoutTargetCount);
            Check("S2 worst UI-thread stall (max loop gap, ms)", F(_gapMax, "0.0"), $"< {UiBlockTargetMs:0} ms", _gapMax < UiBlockTargetMs);
            Info("S2 worst single RunFrame+tick busy (ms)", F(_busyMax, "0.0"));
            Check("S2 pop-out video really played (frames rendered)", $"{fr} rendered in {_cfg.Seconds:0.#} s", ">= 12 frames/s of window", fr >= 12 * _cfg.Seconds);
            Check("S2 dropped frames (MF GetStatistics)", $"{F(dropPct)}% ({fd} of {fr + fd})", $"< {DropTargetPct:0}%", dropPct < DropTargetPct);
            S2Dist = dm;
        }
        private Dist S2Dist;

        // ── S3 ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private double TriggerMs;

        private double Timed(Action trigger, int frames, List<double> presentGaps)
        {
            double b = Timed(trigger, frames);
            presentGaps.Add(_mainGapSinceMark);
            return b;
        }

        private double Timed(Action trigger, int frames)
        {
            _mainGapSinceMark = 0;
            long t0 = Now();
            trigger();
            TriggerMs = Ms(Now() - t0);
            return ActionBlockTail(frames);
        }

        private double ActionBlockTail(int frames)
        {
            Step(true);
            double worst = TriggerMs + _lastBusy;
            for (int i = 1; i < frames && !_aborted; i++) { Step(true); if (_lastBusy > worst) worst = _lastBusy; }
            return worst;
        }

        private void S3()
        {
            if (_child is null || !_child.Win.IsOpen) { NoMeasure("S3 UI actions", "pop-out not open (S2 incomplete)"); _aborted = true; return; }
            var open = new List<double>(); var close = new List<double>(); var resize = new List<double>();
            var openGap = new List<double>(); var closeGap = new List<double>(); var resizeGap = new List<double>();
            RectF orig = _window.OuterBoundsPx;
            var mainSink = new List<double>(4096); _mainSink = mainSink;
            long to0 = Timeouts();
            ResetUi();
            for (int i = 0; i < _cfg.Cycles && !_aborted; i++)
            {
                open.Add(Timed(() => VideoE2EState.PopupOpen.Value = true, 20, openGap));
                PumpFrames(10);
                close.Add(Timed(() => VideoE2EState.PopupOpen.Value = false, 20, closeGap));
                PumpFrames(10);
                float dw = (i % 2 == 0) ? 80f : -80f;
                resize.Add(Timed(() => _window.SetBoundsPx(new RectF(orig.X, orig.Y, Math.Max(640f, orig.W + dw), orig.H)), 20, resizeGap));
                PumpFrames(10);
            }
            _window.SetBoundsPx(orig);
            PumpFrames(20);
            _mainSink = null;
            long timeouts = Timeouts() - to0;
            RawList("popupOpenBlockMs", open); RawList("popupCloseBlockMs", close); RawList("resizeBlockMs", resize);
            RawList("popupOpenMainPresentGapMs", openGap); RawList("popupCloseMainPresentGapMs", closeGap); RawList("resizeMainPresentGapMs", resizeGap);
            RawWaitKinds();
            Raw("slotTimeouts", timeouts); Raw("uiGapMaxMs", _gapMax);
            var dm = Summarize(mainSink); IntervalInfo("S3 main (actions)", dm, "mainInterval");
            void Rep(string name, List<double> v)
            {
                if (v.Count == 0) { NoMeasure("S3 " + name + " UI-thread block", "no samples"); return; }
                var a = v.ToArray(); Array.Sort(a);
                double p95 = a[Math.Clamp((int)Math.Round(0.95 * (a.Length - 1)), 0, a.Length - 1)];
                double max = a[^1];
                Check($"S3 {name} UI-thread block max / p95 (ms)", $"{F(max, "0.0")} / {F(p95, "0.0")}  (n={a.Length})", $"max < {UiBlockTargetMs:0} ms (stretch {UiBlockStretchMs:0} ms: {(max < UiBlockStretchMs ? "met" : "not met")})", max < UiBlockTargetMs);
            }
            Rep("popup open", open); Rep("popup close", close); Rep("main-window resize", resize);
            void Gap(string name, List<double> v) { if (v.Count > 0) { var a = v.ToArray(); Array.Sort(a); Info($"S3 worst main present gap after {name} max / p50 (ms)", $"{F(a[^1], "0.0")} / {F(a[a.Length / 2], "0.0")}"); } }
            Gap("popup open", openGap); Gap("popup close", closeGap); Gap("main-window resize", resizeGap);
            Check("S3 1000 ms latency/slot timeouts", timeouts.ToString(CultureInfo.InvariantCulture), "== 0", timeouts == SlotTimeoutTargetCount);
        }

        // ── S4 ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private void S4()
        {
            if (_popPlayer is null) { NoMeasure("S4 switching", "no player (S2 incomplete)"); _aborted = true; return; }
            var times = new List<double>();
            int timeouts = 0;
            long fr0 = _popFrames.Rendered, fd0 = _popFrames.Dropped;
            long streams0 = System.Threading.Interlocked.Read(ref _streamSizeLines);
            long slot0 = Timeouts();
            ResetUi();
            for (int i = 0; i < _cfg.Switches && !_aborted; i++)
            {
                bool useB = (i % 2) == 0;
                var prevSession = _popPlayer.Session;
                MediaSource src = useB ? SourceB() : SourceA();
                var start = TimeSpan.FromMilliseconds(useB ? 1200 : 0);
                long t0 = Now();
                _ = OpenAndPlay(_popPlayer, src, start);
                double ms = -1;
                bool ok = PumpUntil(() =>
                {
                    if (_popPlayer.Session is MfMediaSession s && !ReferenceEquals(s, prevSession) && s.FirstFrameEpoch > 0 && !_popPlayer.VideoSurface.Peek().IsNone)
                    { ms = Ms(Now() - t0); return true; }
                    return false;
                }, 5000);
                if (!ok) { timeouts++; ms = 5000; }
                times.Add(ms);
                PumpFor(1700);   // dwell: the MF counters reach the player through the ~1 Hz state pump, so a shorter dwell undercounts
            }
            PumpFor(1200);
            long fr = _popFrames.Rendered - fr0, fd = _popFrames.Dropped - fd0;
            double dropPct = fr + fd == 0 ? 0 : 100.0 * fd / (fr + fd);
            RawWaitKinds();
            RawList("switchToFirstFrameMs", times);
            Raw("switchTimeouts", timeouts); Raw("framesRendered", fr); Raw("framesDropped", fd); Raw("dropPct", dropPct);
            Raw("streamSizeUpdates", System.Threading.Interlocked.Read(ref _streamSizeLines) - streams0);
            Raw("slotTimeouts", Timeouts() - slot0); Raw("uiGapMaxMs", _gapMax);
            if (times.Count == 0) { NoMeasure("S4 OpenAsync -> first frame", "no switches completed"); return; }
            var a = times.ToArray(); Array.Sort(a);
            double p50 = a[Math.Clamp((int)Math.Round(0.5 * (a.Length - 1)), 0, a.Length - 1)];
            double p95 = a[Math.Clamp((int)Math.Round(0.95 * (a.Length - 1)), 0, a.Length - 1)];
            Info("S4 switches completed / timed out (5 s cap)", $"{times.Count - timeouts} / {timeouts}");
            Check("S4 switch OpenAsync -> first frame p95 (ms)", $"p95 {F(p95, "0.0")}, p50 {F(p50, "0.0")}, max {F(a[^1], "0.0")}  (n={a.Length})", $"p95 < {SwitchTargetMs:0} ms", p95 < SwitchTargetMs && timeouts == 0);
            Check("S4 dropped frames over the run (MF GetStatistics)", $"{F(dropPct)}% ({fd} of {fr + fd})", $"< {DropTargetPct:0}%", dropPct < DropTargetPct);
            Info("S4 UI-thread worst loop gap (ms)", F(_gapMax, "0.0"));
        }

        // ── S5 ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private void S5()
        {
            // The pop-out is closed first (timed): S5 is the main-window placement workload on its own.
            if (_child is not null && _child.Win.IsOpen)
            {
                bool closed = CloseChild(_child, out double closeMs);
                Raw("popoutHardCloseMs", closeMs); Raw("popoutHardCloseReaped", closed ? 1 : 0);
                S6HardCloseMs = closeMs;
                _child = null;
            }
            _ = OpenAndPlay(_mainPlayer!, SourceA(), TimeSpan.Zero);
            VideoE2EState.StageOn.Value = true;
            bool bound = PumpUntil(() => IsPlaying(_mainPlayer!) && !_mainPlayer!.VideoSurface.Peek().IsNone, 10_000);
            if (!bound) { _cur.Notes.Add("main video never became visible: placement churn measured without a bound surface"); }
            PumpFor(800);

            var sink = new List<double>(2048); _mainSink = sink;
            long streams0 = System.Threading.Interlocked.Read(ref _streamSizeLines);
            long place0 = System.Threading.Interlocked.Read(ref _placeLines);
            long fr0 = _mainFrames.Rendered, fd0 = _mainFrames.Dropped;
            long slot0 = Timeouts();
            ResetUi();
            long start = Now();
            const double MotionMs = 5000.0;
            VideoSurfaceGeometry lastGeo = _mainPlayer!.SurfaceGeometry.Peek();
            int geoChanges = 0;
            float minPlaceW = float.MaxValue, maxPlaceW = 0f;
            while (!_aborted && Ms(Now() - start) < MotionMs)
            {
                double t = Ms(Now() - start) / 1000.0;
                double s = 0.5 + 0.5 * Math.Sin(t * 2.0 * Math.PI / 2.5);
                float w = (float)(320.0 + 360.0 * s);
                VideoE2EState.StageW.Value = w;
                VideoE2EState.StageH.Value = w * 9f / 16f;
                VideoE2EState.StageX.Value = (float)(120.0 * Math.Sin(t * 2.0 * Math.PI / 1.7));
                Step(true);
                VideoSurfaceGeometry geo = _mainPlayer.SurfaceGeometry.Peek();
                if (geo != lastGeo) { geoChanges++; lastGeo = geo; }
                if (geo.IsPlaced) { if (geo.Place.W < minPlaceW) minPlaceW = geo.Place.W; if (geo.Place.W > maxPlaceW) maxPlaceW = geo.Place.W; }
            }
            _mainSink = null;
            long streams = System.Threading.Interlocked.Read(ref _streamSizeLines) - streams0;
            long places = System.Threading.Interlocked.Read(ref _placeLines) - place0;
            long fr = _mainFrames.Rendered - fr0, fd = _mainFrames.Dropped - fd0;
            var d = Summarize(sink);
            Raw("streamSizeUpdates", streams); Raw("placeLines", places); Raw("placementChanges", geoChanges);
            Raw("placeWidthMinPx", minPlaceW == float.MaxValue ? 0 : minPlaceW); Raw("placeWidthMaxPx", maxPlaceW); Raw("framesRendered", fr); Raw("framesDropped", fd);
            Raw("slotTimeouts", Timeouts() - slot0); Raw("uiGapMaxMs", _gapMax); Raw("motionMs", MotionMs);
            Check("S5 decoder stream-size updates during 5 s motion", streams.ToString(CultureInfo.InvariantCulture), $"<= {StreamUpdateTarget} (settled sizing, not per frame)", streams <= StreamUpdateTarget);
            IntervalInfo("S5 main (motion)", d, "mainInterval");
            Check("S5 motion reached the video placement (placement changes / place width range px)",
                $"{geoChanges} changes, {F(minPlaceW == float.MaxValue ? 0 : minPlaceW, "0")} .. {F(maxPlaceW, "0")} px", ">= 30 changes and range > 150 px (workload validity)",
                geoChanges >= 30 && maxPlaceW - (minPlaceW == float.MaxValue ? 0 : minPlaceW) > 150f);
            Info("S5 hole-vs-placement delta telemetry", "not exposed by the engine in this build ([video.surface] place is logged once per surface, not per move); not measured");
            Info("S5 frames rendered / dropped (MF)", $"{fr} / {fd}");
            Info("S5 worst UI-thread loop gap (ms)", F(_gapMax, "0.0"));

            VideoE2EState.StageOn.Value = false;
            PumpFrames(10);
            _mainPlayer!.Stop();
        }
        private double S6HardCloseMs = -1;

        // ── S6 ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private void S6()
        {
            if (_popPlayer is null) { NoMeasure("S6 reopen", "no player"); _aborted = true; return; }
            // The pop-out player keeps playing; the closed window left it unbound.
            var req = new DetachedWindowRequest("Video E2E", new Size2(640f, 360f), new VideoE2EChild());

            // Cold open: a brand-new window after a hard close.
            var run = OpenChild();
            if (run is null) { _cur.Error = "OpenDetachedWindow returned null"; _aborted = true; return; }
            _child = run;
            PumpUntil(() => run.Revealed, 5000);
            PumpFrames(6);
            double coldMs = _childFirstPresentQpc != 0 ? Ms(_childFirstPresentQpc - run.OpenQpc) : (run.Revealed ? Ms(run.RevealedObservedQpc - run.OpenQpc) : -1);
            PumpUntil(() => run.BindMs >= 0, 4000);
            double coldBind = run.BindMs;
            Raw("coldOpenFirstPresentMs", coldMs); Raw("coldOpenBindMs", coldBind); Raw("coldOpenRevealMs", run.Timing.FirstPresentMs);

            var warm = new List<double>(); var warmBind = new List<double>(); var park = new List<double>();
            int parkFailures = 0;
            for (int i = 0; i < 5 && !_aborted; i++)
            {
                long p0 = Now();
                bool parked = false;
                try { parked = run.Win.Park(); } catch (Exception ex) { Console.Error.WriteLine($"[video-e2e] Park() threw: {ex.Message}"); }
                double parkMs = Ms(Now() - p0);
                park.Add(parkMs);
                PumpFor(300, scroll: false);
                if (!parked) { parkFailures++; CloseChild(run, out _); run = OpenChild(); if (run is null) { _aborted = true; return; } _child = run; PumpUntil(() => run.Revealed, 5000); continue; }
                run.Revealed = false; run.BindMs = -1; run.BoundObservedQpc = 0; run.RevealedObservedQpc = 0;
                _childFloorQpc = Now(); _childFirstPresentQpc = 0; run.OpenQpc = _childFloorQpc;
                bool un = run.Win.Unpark(req);
                if (!un) { parkFailures++; _cur.Notes.Add("Unpark() returned false"); CloseChild(run, out _); run = OpenChild(); if (run is null) { _aborted = true; return; } _child = run; PumpUntil(() => run.Revealed, 5000); continue; }
                Wire(run);
                PumpUntil(() => run.Revealed, 5000);
                PumpFrames(6);
                double ms = _childFirstPresentQpc != 0 ? Ms(_childFirstPresentQpc - run.OpenQpc) : (run.Revealed ? Ms(run.RevealedObservedQpc - run.OpenQpc) : -1);
                warm.Add(ms);
                PumpUntil(() => run.BindMs >= 0, 2000);
                warmBind.Add(run.BindMs);
                PumpFor(400, scroll: false);
            }
            bool closed = CloseChild(run, out double closeMs);
            _child = null;
            RawList("warmReopenFirstPresentMs", warm); RawList("warmReopenBindMs", warmBind); RawList("parkMs", park);
            Raw("finalHardCloseMs", closeMs); Raw("parkFailures", parkFailures);

            if (coldMs < 0) NoMeasure("S6 cold open -> first present (ms)", "no present");
            else Check("S6 cold open -> first present (ms)", F(coldMs, "0.0"), $"< {OpenToPresentTargetMs:0} ms", coldMs < OpenToPresentTargetMs);
            Info("S6 cold open -> first video bound (ms)", F(coldBind, "0.0"));
            if (warm.Count == 0 || warm.Exists(x => x < 0)) NoMeasure("S6 warm reopen -> first present (ms)", warm.Count == 0 ? "no warm reopen completed" : "a warm reopen never presented");
            else
            {
                var a = warm.ToArray(); Array.Sort(a);
                Check("S6 warm reopen -> first present max / p50 (ms)", $"{F(a[^1], "0.0")} / {F(a[a.Length / 2], "0.0")}  (n={a.Length})", $"max < {OpenToPresentTargetMs:0} ms", a[^1] < OpenToPresentTargetMs);
                if (coldMs > 0) Info("S6 warm vs cold (p50 / cold)", $"{F(a[a.Length / 2], "0.0")} ms vs {F(coldMs, "0.0")} ms");
            }
            Check("S6 park/unpark failures (fell back to cold open)", parkFailures.ToString(CultureInfo.InvariantCulture), "== 0", parkFailures == 0);
            if (park.Count > 0) { var a = park.ToArray(); Array.Sort(a); Info("S6 park (hide) call max (ms)", F(a[^1], "0.0")); }
            Info("S6 hard close Close() -> reaped (ms), end of S5 / end of S6", $"{F(S6HardCloseMs, "0.0")} / {F(closeMs, "0.0")}{(closed ? "" : " (NOT reaped)")}");
        }

        // ── cleanup + report ─────────────────────────────────────────────────────────────────────────────────────────
        private void Cleanup(RectF origBounds)
        {
            try
            {
                _aborted = false;
                _scenarioDeadlineQpc = long.MaxValue;
                VideoE2EState.PopupOpen.Value = false;
                VideoE2EState.StageOn.Value = false;
                if (_child is not null && _child.Win.IsOpen) CloseChild(_child, out _);
                _child = null;
                for (int i = 0; i < 6 && !_window.IsClosed; i++) { _host.RunFrame(); _host.TickDetachedHosts(); }
                // Close ANY detached window the probe opened and lost track of (a scenario that threw mid-reopen).
                _popPlayer?.Stop(); _mainPlayer?.Stop();
                for (int i = 0; i < 6 && !_window.IsClosed; i++) { _host.RunFrame(); _host.TickDetachedHosts(); _window.WaitForWork(4); }
                if (_popPlayer is { } pp) _ = pp.DisposeAsync().AsTask();
                if (_mainPlayer is { } mp) _ = mp.DisposeAsync().AsTask();
                VideoE2EState.PopPlayer = null; VideoE2EState.MainPlayer = null;
                if (origBounds.W > 1f) _window.SetBoundsPx(origBounds);
                for (int i = 0; i < 6 && !_window.IsClosed; i++) { _host.RunFrame(); _host.TickDetachedHosts(); }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[video-e2e] cleanup threw: {ex.Message}"); }
        }

        private int Finish(long runStart)
        {
            int fails = 0, infos = 0, passes = 0, na = 0;
            foreach (var m in _metrics)
            {
                if (m.Status == "FAIL") fails++; else if (m.Status == "PASS") passes++; else if (m.Status == "N/A") na++; else infos++;
            }
            int exit = _incomplete ? 1 : fails > 0 ? 2 : 0;
            string verdict = exit == 0 ? "PASS" : exit == 2 ? "FAIL" : "INCOMPLETE";
            double totalMs = Ms(Now() - runStart);
            try { File.WriteAllText(Path.Combine(_cfg.OutDir, "video-e2e.json"), BuildJson(verdict, exit, totalMs, passes, fails, na), new UTF8Encoding(false)); }
            catch (Exception ex) { Console.Error.WriteLine($"[video-e2e] json write failed: {ex.Message}"); exit = 1; }
            string md = BuildMarkdown(verdict, exit, totalMs, passes, fails, na);
            try { File.WriteAllText(Path.Combine(_cfg.OutDir, "video-e2e.md"), md, new UTF8Encoding(false)); }
            catch (Exception ex) { Console.Error.WriteLine($"[video-e2e] md write failed: {ex.Message}"); exit = 1; }
            Console.Error.WriteLine($"[video-e2e] verdict={verdict} pass={passes} fail={fails} notMeasured={na} info={infos} exit={exit} out={Path.GetFullPath(_cfg.OutDir)}");
            return exit;
        }

        private string BuildJson(string verdict, int exit, double totalMs, int passes, int fails, int na)
        {
            var sb = new StringBuilder(8192);
            sb.Append("{\n");
            sb.Append($"  \"verdict\": \"{verdict}\", \"exitCode\": {exit}, \"totalMs\": {Num(totalMs)}, \"pass\": {passes}, \"fail\": {fails}, \"notMeasured\": {na},\n");
            sb.Append($"  \"config\": {{\"seconds\": {Num(_cfg.Seconds)}, \"switches\": {_cfg.Switches}, \"cycles\": {_cfg.Cycles}, \"fixtureA\": \"{Esc(_fixtureA)}\", \"fixtureB\": \"{Esc(_fixtureB)}\"}},\n");
            sb.Append($"  \"display\": {{\"refreshHz\": {Num(_hz)}, \"vsyncMs\": {Num(_vsyncMs)}}},\n");
            sb.Append("  \"scenarios\": [\n");
            for (int i = 0; i < _scenarios.Count; i++)
            {
                var s = _scenarios[i];
                sb.Append($"    {{\"id\": \"{Esc(s.Id)}\", \"name\": \"{Esc(s.Name)}\", \"completed\": {(s.Completed ? "true" : "false")}, \"durationMs\": {Num(s.DurationMs)}, \"error\": \"{Esc(s.Error)}\",\n");
                sb.Append("      \"notes\": [");
                for (int n = 0; n < s.Notes.Count; n++) { if (n > 0) sb.Append(", "); sb.Append('"').Append(Esc(s.Notes[n])).Append('"'); }
                sb.Append("],\n      \"raw\": {");
                for (int r = 0; r < s.Raw.Count; r++) { if (r > 0) sb.Append(", "); sb.Append('"').Append(Esc(s.Raw[r].Key)).Append("\": ").Append(s.Raw[r].Json); }
                sb.Append("}}").Append(i + 1 < _scenarios.Count ? ",\n" : "\n");
            }
            sb.Append("  ],\n  \"metrics\": [\n");
            for (int i = 0; i < _metrics.Count; i++)
            {
                var m = _metrics[i];
                sb.Append($"    {{\"scenario\": \"{Esc(m.Scenario)}\", \"metric\": \"{Esc(m.Name)}\", \"value\": \"{Esc(m.Value)}\", \"threshold\": \"{Esc(m.Threshold)}\", \"status\": \"{m.Status}\"}}");
                sb.Append(i + 1 < _metrics.Count ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        private string BuildMarkdown(string verdict, int exit, double totalMs, int passes, int fails, int na)
        {
            var sb = new StringBuilder(4096);
            sb.Append("# FluentGpu video end-to-end results\n\n");
            sb.Append($"**Verdict: {verdict}** (exit {exit}) - {passes} PASS, {fails} FAIL, {na} not measured. Total {F(totalMs / 1000.0, "0.0")} s.\n\n");
            sb.Append($"Display {F(_hz)} Hz (vsync {F(_vsyncMs, "0.000")} ms). Clip: `{Path.GetFileName(_fixtureA)}` (looped); switch target is a copy of the same clip. Options: seconds={_cfg.Seconds:0.#}, switches={_cfg.Switches}, cycles={_cfg.Cycles}.\n\n");
            sb.Append("| Scenario | Metric | Value | Threshold | Status |\n|---|---|---|---|---|\n");
            foreach (var m in _metrics)
                sb.Append($"| {m.Scenario} | {m.Name.Replace("|", "/")} | {m.Value.Replace("|", "/")} | {m.Threshold.Replace("|", "/")} | {m.Status} |\n");
            sb.Append("\n## Scenarios\n\n");
            foreach (var s in _scenarios)
            {
                sb.Append($"- **{s.Id}** {s.Name}: {(s.Completed ? "completed" : "NOT completed")} in {F(s.DurationMs / 1000.0, "0.0")} s");
                if (s.Error.Length > 0) sb.Append($" - error: {s.Error}");
                sb.Append('\n');
                foreach (var n in s.Notes) sb.Append($"  - note: {n}\n");
            }
            return sb.ToString();
        }
    }
}
