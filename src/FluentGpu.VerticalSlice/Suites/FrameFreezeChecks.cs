using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// EP10 / E2 (<c>AppHost.Freeze</c> / <c>Thaw</c>): the frame freeze on the headless RHI. A transaction freezes the window, rebuilds
/// the UI underneath and thaws into a premultiplied dissolve; the gates pin that no presented frame in between shows the new tree,
/// that the dissolve is made of exactly the snapshot and the new frame, that input is swallowed, that a resize or the watchdog cuts
/// the freeze (and its snap scope), that a backend without dissolve cuts, and that none of it allocates on the hot phases.
/// </summary>
static class FrameFreezeChecks
{
    static readonly ColorF OldFill = ColorF.FromRgba(200, 30, 30);
    static readonly ColorF NewFill = ColorF.FromRgba(30, 30, 200);

    sealed class FreezeRoot : Component
    {
        public readonly Signal<bool> Alt = new(false);
        public int Clicks;

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 300f, Height = 200f,
            Children =
            [
                new BoxEl { Width = 120f, Height = 40f, Fill = Alt.Value ? NewFill : OldFill },
                new BoxEl { Width = 120f, Height = 40f, Fill = ColorF.FromRgba(90, 90, 90), OnClick = () => Clicks++ },
            ],
        };
    }

    sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings;
        public readonly HeadlessGpuDevice Device = new();
        public readonly HeadlessWindow Window;
        public readonly FreezeRoot Root = new();
        public readonly AppHost Host;
        public readonly HeadlessSwapchain Sc;

        public Rig(StringTable strings, bool dissolveSupported = true)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Strings = strings;
            Window = new HeadlessWindow(new WindowDesc("frame-freeze", new Size2(300, 200), 1f));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(strings), strings, Root);
            Sc = Device.PrimarySwapchain!;
            Sc.DissolveUnsupported = !dissolveSupported;
            Paint(3);   // mount + settle
        }

        public void Paint(int n = 1) { for (int i = 0; i < n; i++) Host.Paint(0); }
        public HeadlessFrameSummary Current() => Device.SummarizeLastFrame();
        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    public static void Run(StringTable strings)
    {
        FormulaCheck();
        CoreSequenceChecks(strings);
        InputChecks(strings);
        CutChecks(strings);
        NoDissolveBackendCheck(strings);
        AllocCheck(strings);
    }

    // ══ the dissolve formula ════════════════════════════════════════════════════════════════════════════════════════════

    static void FormulaCheck()
    {
        var s = new ColorF(0.2f, 0.1f, 0.0f, 0.5f);    // premultiplied: a half-transparent snapshot pixel
        var n = new ColorF(0.0f, 0.4f, 0.8f, 1.0f);
        var hole = new ColorF(0f, 0f, 0f, 0f);
        var q = FrameDissolve.Blend(in s, in n, 0.25f);
        bool lerp = Near(q.R, 0.75f * 0.2f) && Near(q.G, 0.75f * 0.1f + 0.25f * 0.4f) && Near(q.B, 0.25f * 0.8f) && Near(q.A, 0.75f * 0.5f + 0.25f * 1f);
        bool ends = FrameDissolve.Blend(in s, in n, 0f) == s && FrameDissolve.Blend(in s, in n, 1f) == n
                 && FrameDissolve.Blend(in s, in n, -3f) == s && FrameDissolve.Blend(in s, in n, 9f) == n;
        var h = FrameDissolve.Blend(in hole, in hole, 0.5f);
        bool holes = h.R == 0f && h.G == 0f && h.B == 0f && h.A == 0f;                       // a hole in both frames stays a hole at every t
        // Not src-over: src-over of a half-transparent N over S would keep S's alpha contribution at (1 - Na); the dissolve takes (1-t).
        var overHalf = FrameDissolve.Blend(in s, in hole, 0.5f);
        bool notSrcOver = Near(overHalf.A, 0.25f) && Near(overHalf.R, 0.1f);
        Check("gate.freeze.formula the dissolve is out = (1-t)·S + t·N on the four premultiplied channels, alpha included (not src-over); t clamps to 0..1, the ends are exactly S and N, and a pixel that is alpha 0 in both frames (a video hole) stays alpha 0 at every t",
            lerp && ends && holes && notSrcOver, $"lerp={lerp} ends={ends} holes={holes} notSrcOver={notSrcOver}");
    }

    static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-5f;

    static bool HasFill(HeadlessGpuDevice d, ColorF fill)
    {
        for (int i = 0; i < d.LastRects.Count; i++) if (d.LastRects[i].Fill == fill) return true;
        return false;
    }

    // ══ freeze → hold → thaw/dissolve ═══════════════════════════════════════════════════════════════════════════════════

    static void CoreSequenceChecks(StringTable strings)
    {
        using var rig = new Rig(strings);
        var sc = rig.Sc;
        sc.RecordPresents = true;
        var host = rig.Host;
        var oldSummary = rig.Current();

        var token = host.Freeze();
        bool capturedBefore = token.IsCaptured;
        rig.Paint();                                               // the CAPTURE frame: the outgoing tree, presented normally, snapshotted
        bool captured = token.IsCaptured && token.WhenCaptured.IsCompleted;
        int logAtCapture = sc.PresentLog.Count;
        int liveSnapshots = sc.LiveSnapshots;
        bool frozenState = host.IsFrozen && host.FreezeState == FreezePhase.Frozen && host.IsSnapping && host.Animation.Snapping;
        bool captureShowsOld = sc.PresentLog.Count > 0 && sc.PresentLog[^1].Kind == HeadlessPresentKind.Normal && sc.PresentLog[^1].Shown.Hash == oldSummary.Hash;

        rig.Root.Alt.Value = true;                                 // the transaction changes the UI underneath
        int presents0 = sc.PresentCount, held0 = sc.HeldPresentCount;
        rig.Paint(5);
        var rebuilt = rig.Current();                               // the device DID record the new tree ...
        bool recordedNew = rebuilt.Hash != oldSummary.Hash && HasFill(rig.Device, NewFill) && !HasFill(rig.Device, OldFill);
        bool noPresentWhileHeld = sc.PresentCount == presents0 && sc.HeldPresentCount == held0 + 5;   // ... and presented none of it
        bool onlyHeldBetween = true;
        for (int i = logAtCapture; i < sc.PresentLog.Count; i++) onlyHeldBetween &= sc.PresentLog[i].Kind == HeadlessPresentKind.Held;

        // The thaw: a 96 ms dissolve (six 16 ms frames) with a DWM plan whose edges are stamped against the present count.
        var edges = new List<string>();
        var plan = new DwmPlan(
            AtThawStart: () => edges.Add($"start@dissolves={sc.DissolvedPresentCount}"),
            AtThawEnd: () => edges.Add($"end@dissolves={sc.DissolvedPresentCount}"));
        FreezeEnd? ended = null;
        int endedCalls = 0;
        host.FreezeEnded += (t, how) => { endedCalls++; ended = how; };
        int logAtThaw = sc.PresentLog.Count;
        host.Thaw(token, new DissolveSpec(96f), plan);
        bool snapEndedAtThaw = !host.IsSnapping && !host.Animation.Snapping;
        int guard = 0;
        while (host.FreezeState != FreezePhase.None && guard++ < 60) rig.Paint();

        var dissolves = new List<HeadlessPresentRecord>();
        HeadlessPresentRecord last = default;
        for (int i = logAtThaw; i < sc.PresentLog.Count; i++)
        {
            if (sc.PresentLog[i].Kind == HeadlessPresentKind.Dissolve) dissolves.Add(sc.PresentLog[i]);
            last = sc.PresentLog[i];
        }
        bool firstIsDissolve = sc.PresentLog.Count > logAtThaw && sc.PresentLog[logAtThaw].Kind == HeadlessPresentKind.Dissolve;
        bool made = dissolves.Count >= 3;
        bool sources = true, rising = true;
        float prev = 0f;
        foreach (var d in dissolves)
        {
            sources &= d.From.Hash == oldSummary.Hash && d.Shown.Hash == rebuilt.Hash && d.SnapshotId == dissolves[0].SnapshotId;
            rising &= d.T > prev && d.T > 0f && d.T < 1f;
            prev = d.T;
        }
        bool finalIsNormalNew = last.Kind == HeadlessPresentKind.Normal && last.Shown.Hash == rebuilt.Hash;
        bool released = sc.LiveSnapshots == 0 && !host.HasFrozenSnapshotForTest;   // the extra texture is back
        bool dwmOrder = edges.Count == 2 && edges[0] == "start@dissolves=0" && edges[1] == $"end@dissolves={dissolves.Count}";
        bool tokenDone = token.End == FreezeEnd.Thawed && !token.IsActive && ended == FreezeEnd.Thawed && endedCalls == 1;

        Check("gate.freeze.no-hybrid-present Freeze captures the OUTGOING tree (presented once, snapshotted, token captured) and from then on EVERY present until the thaw is held: the new tree is recorded and submitted for its uploads but no presented frame shows it",
            !capturedBefore && captured && liveSnapshots == 1 && frozenState && captureShowsOld && recordedNew && noPresentWhileHeld && onlyHeldBetween,
            $"capturedBefore={capturedBefore} captured={captured} live={liveSnapshots} state={frozenState} captureShowsOld={captureShowsOld} recordedNew={recordedNew} noPresent={noPresentWhileHeld} onlyHeld={onlyHeldBetween}");
        Check("gate.freeze.dissolve Thaw presents a dissolve whose two sources are exactly the snapshot (the outgoing tree) and the new frame, t rising strictly inside (0,1) over >= 3 full presents from the very first post-thaw present, ends in a normal present of the new tree, and releases the snapshot",
            firstIsDissolve && made && sources && rising && finalIsNormalNew && released && snapEndedAtThaw,
            $"firstIsDissolve={firstIsDissolve} dissolves={dissolves.Count} sources={sources} rising={rising} final={last.Kind} released={released} snapEndedAtThaw={snapEndedAtThaw}");
        Check("gate.freeze.dwm-order the DWM plan's start edge runs before the first dissolve present and its end edge after the last one, once each; FreezeEnded fires once with Thawed",
            dwmOrder && tokenDone, $"edges=[{string.Join(", ", edges)}] dissolves={dissolves.Count} tokenDone={tokenDone}");
    }

    // ══ input is swallowed ══════════════════════════════════════════════════════════════════════════════════════════════

    static void InputChecks(StringTable strings)
    {
        using var rig = new Rig(strings);
        var host = rig.Host;
        var p = new Point2(60f, 60f);   // the middle of the clickable box

        void Click()
        {
            rig.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            rig.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
            host.RunFrame();
        }

        Click();
        int live = rig.Root.Clicks;                                // control: the box takes a click when not frozen

        var token = host.Freeze();
        rig.Paint();                                               // capture
        Click();
        Click();
        rig.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0));
        host.RunFrame();
        int frozen = rig.Root.Clicks;

        host.Thaw(token, DissolveSpec.Cut);
        rig.Paint(2);
        Click();
        int after = rig.Root.Clicks;

        Check("gate.freeze.input-swallowed pointer input queued while frozen never reaches the dispatcher (the box under the pointer gets no click), and the same click lands again once the freeze has ended",
            live == 1 && frozen == 1 && after == 2, $"live={live} frozen={frozen} after={after}");
    }

    // ══ cuts: resize, DPI, watchdog ═════════════════════════════════════════════════════════════════════════════════════

    static void CutChecks(StringTable strings)
    {
        // A resize cuts the freeze at once: the snap scope ends, the cut frame presents normally, the snapshot goes back.
        using (var rig = new Rig(strings))
        {
            var host = rig.Host;
            var sc = rig.Sc;
            sc.RecordPresents = true;
            FreezeEnd? ended = null;
            host.FreezeEnded += (t, how) => ended = how;
            var token = host.Freeze();
            rig.Paint();
            rig.Root.Alt.Value = true;
            rig.Paint(2);
            bool heldBefore = sc.HeldPresentCount >= 2 && host.IsSnapping;
            int logBefore = sc.PresentLog.Count;
            rig.Window.ClientSizePx = new Size2(360, 200);
            rig.Paint();
            bool cut = token.End == FreezeEnd.CutByResize && ended == FreezeEnd.CutByResize && host.FreezeState == FreezePhase.None;
            bool scopeEnded = !host.IsSnapping && !host.Animation.Snapping;
            bool presentedNormally = sc.PresentLog.Count == logBefore + 1 && sc.PresentLog[^1].Kind == HeadlessPresentKind.Normal
                && sc.PresentLog[^1].Shown.Hash == rig.Current().Hash;
            bool released = sc.LiveSnapshots == 0 && sc.DissolvedPresentCount == 0;
            Check("gate.freeze.resize-cuts a resize during a freeze cuts it (FreezeEnd.CutByResize): the snap scope ends BEFORE the frame flushes, that frame presents the new tree normally (no dissolve, no hold), and the snapshot is released",
                heldBefore && cut && scopeEnded && presentedNormally && released,
                $"heldBefore={heldBefore} cut={cut} (end={token.End}) scopeEnded={scopeEnded} presentedNormally={presentedNormally} released={released}");
        }

        // A DPI change is the same cut.
        using (var rig = new Rig(strings))
        {
            var token = rig.Host.Freeze();
            rig.Paint();
            rig.Window.Scale = 1.5f;
            rig.Paint();
            Check("gate.freeze.dpi-cuts a DPI change during a freeze cuts it exactly as a resize does",
                token.End == FreezeEnd.CutByResize && !rig.Host.IsSnapping && rig.Sc.LiveSnapshots == 0,
                $"end={token.End} snapping={rig.Host.IsSnapping} live={rig.Sc.LiveSnapshots}");
        }

        // The watchdog: a freeze nobody thaws is cut after FreezeWatchdogMs.
        using (var rig = new Rig(strings))
        {
            var host = rig.Host;
            FreezeEnd? ended = null;
            host.FreezeEnded += (t, how) => ended = how;
            var token = host.Freeze();
            rig.Paint();
            rig.Paint(10);                                          // ~160 ms: still frozen, well inside the watchdog
            bool stillFrozen = host.IsFrozen && token.IsActive;
            bool wakes = host.RecommendedWaitMs() is >= 0 and <= 2000;   // the idle wait is clamped to the watchdog, never unbounded
            host.AdvanceFrameClockForTest(2100);
            rig.Paint();
            bool cut = token.End == FreezeEnd.CutByWatchdog && ended == FreezeEnd.CutByWatchdog && host.FreezeState == FreezePhase.None;
            bool resumed = !host.IsSnapping && rig.Sc.LiveSnapshots == 0;
            // And a Thaw of a token the watchdog already cut is a harmless no-op.
            host.Thaw(token, new DissolveSpec(250f));
            rig.Paint();
            bool noop = host.FreezeState == FreezePhase.None && rig.Sc.DissolvedPresentCount == 0;
            Check("gate.freeze.watchdog a freeze that nobody thaws is cut by the 2 s watchdog (FreezeEnd.CutByWatchdog) even when the loop would idle; the snap scope ends, the snapshot is released, and a late Thaw of the cut token is a no-op",
                stillFrozen && wakes && cut && resumed && noop, $"stillFrozen={stillFrozen} wakes={wakes} cut={cut} (end={token.End}) resumed={resumed} noop={noop}");
        }
    }

    // ══ a backend without dissolve ══════════════════════════════════════════════════════════════════════════════════════

    static void NoDissolveBackendCheck(StringTable strings)
    {
        using var rig = new Rig(strings, dissolveSupported: false);
        var sc = rig.Sc;
        sc.RecordPresents = true;
        var host = rig.Host;
        var token = host.Freeze();
        rig.Paint();
        rig.Root.Alt.Value = true;
        rig.Paint(3);
        bool held = sc.HeldPresentCount == 3 && sc.LiveSnapshots == 0;       // the freeze itself still holds
        host.Thaw(token, new DissolveSpec(250f));
        rig.Paint();
        bool cutThaw = host.FreezeState == FreezePhase.None && sc.DissolvedPresentCount == 0
                    && sc.PresentLog[^1].Kind == HeadlessPresentKind.Normal && sc.PresentLog[^1].Shown.Hash == rig.Current().Hash
                    && token.End == FreezeEnd.Thawed;
        Check("gate.freeze.no-dissolve-backend a backend that cannot snapshot or dissolve still freezes (presents held, nothing captured) and its thaw is a CUT: the new frame is presented at once, no dissolve",
            held && cutThaw, $"held={held} cutThaw={cutThaw}");
    }

    // ══ allocation ══════════════════════════════════════════════════════════════════════════════════════════════════════

    static void AllocCheck(StringTable strings)
    {
        using var rig = new Rig(strings);
        var host = rig.Host;
        var token = host.Freeze();
        rig.Paint(3);
        rig.Root.Alt.Value = true;
        rig.Paint(3);
        long worstHeld = 0;
        for (int i = 0; i < 6; i++) { long b = host.Paint(0).HotPhaseAllocBytes; if (b > worstHeld) worstHeld = b; }
        host.Thaw(token, new DissolveSpec(160f));
        long worstDissolve = 0;
        int frames = 0;
        while (host.FreezeState != FreezePhase.None && frames++ < 40)
        {
            long b = host.Paint(0).HotPhaseAllocBytes;
            if (frames > 1 && b > worstDissolve) worstDissolve = b;
        }
        Check("gate.freeze.alloc-zero the hot phases (6-13) of held frames and of dissolve frames allocate 0 bytes",
            worstHeld == 0 && worstDissolve == 0 && frames > 3, $"held={worstHeld}B dissolve={worstDissolve}B frames={frames}");
    }
}
