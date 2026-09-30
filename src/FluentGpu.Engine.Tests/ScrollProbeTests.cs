using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Scroll-rework diagnostics layer (design B.9): <see cref="ScrollProbe"/>'s level gating, ring behavior,
/// <see cref="BurstSummary"/> classification, CSV export, and <see cref="ScrollTunables"/>' seqlock + JSON
/// round-trip. <see cref="ScrollProbe"/> is process-wide static state, so every test first calls
/// <see cref="ScrollProbe.EndBurst"/> once to draw a fresh boundary before acting, then reads the NEXT
/// <see cref="BurstSummary"/> — that boundary mechanism is what gives each test isolation without needing a
/// internal reset method (tests within one class run sequentially by default). The rings have producers outside this
/// class too — every render-thread present records a Turn row in the render ring — so the class runs in the serial
/// collection, where no other class's render thread is presenting.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ScrollProbeTests
{
    private static long Qpc(long n) => 1_000_000_000L + n;   // an arbitrary, clearly-non-zero base

    private static void FreshBurst()
    {
        ScrollProbe.EndBurst(Qpc(0));   // discard everything recorded before this test
    }

    [Fact]
    public void Off_RecordsNothing()
    {
        ScrollProbe.Level = ProbeLevel.Off;
        FreshBurst();

        ScrollProbe.Input(ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, Qpc(1), 0f, 120f, vp: 1);
        ScrollProbe.Plan(1, 1, 0, Qpc(1), 0.0, 32.0, 0.257);
        ScrollProbe.Pose(1, Qpc(1), pos: 0.0, vel: 0.0, clamped: true, snappedTrans: 0f);
        ScrollProbe.Coverage(1, Qpc(1), start: 0.0, end: 100.0, origin: 0.0, first: 0, last: 10);
        ScrollProbe.Extent(1, Qpc(1), index: 3, delta: 10.0, anchoredSameFrame: false);
        ScrollProbe.Cost(ScrollCostPhase.Layout, 1, qpcTicks: 100_000, rows: 5, nodes: 5);
        ScrollProbe.Mark(Qpc(1), ProbeMark.TunableChanged);

        var summary = ScrollProbe.EndBurst(Qpc(2));

        Assert.Equal(0, summary.Notches);
        Assert.Equal(0, summary.Presents);
        Assert.Equal(0, summary.CoverageClamps);
        Assert.Equal(0, summary.ExtentJumps);
        Assert.Equal(0, summary.LatePresents);
        Assert.Equal(0.0, summary.MaxCostMs);
        Assert.Equal(ScrollVerdict.Smooth, summary.Verdict);
    }

    [Fact]
    public void RenderCostRows_FeedTheBurstTileCensus()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();

        ScrollProbe.RenderCost(ScrollCostPhase.Composite, qpcTicks: 10_000, rows: 5, nodes: 0);
        ScrollProbe.RenderCost(ScrollCostPhase.TileRaster, qpcTicks: 40_000, rows: 2, nodes: 4096);
        ScrollProbe.RenderCost(ScrollCostPhase.Composite, qpcTicks: 12_000, rows: 5, nodes: 1);
        ScrollProbe.RenderCost(ScrollCostPhase.TileRaster, qpcTicks: 90_000, rows: 6, nodes: 12288);
        ScrollProbe.RenderCost(ScrollCostPhase.Composite, qpcTicks: 11_000, rows: 5, nodes: 0);
        ScrollProbe.RenderCost(ScrollCostPhase.TileRaster, qpcTicks: 20_000, rows: 0, nodes: 0);

        var summary = ScrollProbe.EndBurst(Qpc(50));

        Assert.Equal(6, summary.TilesPerFrameMax);
        Assert.Equal(8.0 / 3.0, summary.TilesPerFrameAvg, 6);
        Assert.Equal(1, summary.ExposedTileMissing);
        Assert.True(summary.MaxCostMsByPhase[(int)ScrollCostPhase.TileRaster] > summary.MaxCostMsByPhase[(int)ScrollCostPhase.Composite]);
        Assert.Contains("tiles(avg/max)=2.67/6", summary.FormatLine());
        Assert.Contains("exposedMissing=1", summary.FormatLine());
        ScrollProbe.Level = ProbeLevel.Off;
    }

    [Fact]
    public void Trace_RecordsEverything()
    {
        ScrollProbe.Level = ProbeLevel.Trace;
        FreshBurst();
        long ui0 = ScrollProbe.UiWriteCount, render0 = ScrollProbe.RenderWriteCount;

        ScrollProbe.Input(ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, Qpc(10), dx: 0f, dy: 120f, vp: 7);
        ScrollProbe.Input(ScrollSourceCode.Touchpad, 1, Qpc(11), 0f, 4f, 7);   // non-notch sample
        ScrollProbe.Plan(7, 5, 2, Qpc(12), 10.0, 42.0, 0.257);
        ScrollProbe.Coverage(7, Qpc(13), start: 0.0, end: 500.0, origin: 0.0, first: 0, last: 20);
        ScrollProbe.Extent(7, Qpc(14), index: 4, delta: -8.0, anchoredSameFrame: false);
        ScrollProbe.Cost(ScrollCostPhase.Virtualize, 7, qpcTicks: 50_000, rows: 12, nodes: 12);
        ScrollProbe.Mark(Qpc(15), ProbeMark.BurstBoundary);
        ScrollProbe.Pose(7, Qpc(16), pos: 42.0, vel: 0.0, clamped: false, snappedTrans: 42f);

        var summary = ScrollProbe.EndBurst(Qpc(20));

        Assert.Equal(1, summary.Notches);      // the notch-phase Input, not the sample-phase one
        Assert.Equal(1, summary.Presents);
        Assert.Equal(1, summary.ExtentJumps);

        // This test's own rows (the rings are process-wide: an older test's rows would ride along in a live export).
        var ui = new ProbeRow[64];
        var render = new ProbeRow[64];
        int nUi = ScrollProbe.ReadUi(ui0, ui, out _);
        int nRender = ScrollProbe.ReadRender(render0, render, out _);
        var sw = new StringWriter();
        ScrollProbe.ExportCsv(sw, ui.AsSpan(0, nUi), render.AsSpan(0, nRender), qpcFrequency: 1.0, refreshHz: 60.0, dpiScale: 1.0,
                              scenario: null);
        string csv = sw.ToString();

        Assert.Contains("# display_refresh_hz=60", csv);
        Assert.Contains("# qpc_frequency=1", csv);
        Assert.Contains("# dpi_scale=1", csv);
        Assert.Contains("# mode=wavee", csv);
        Assert.Contains("qpc_ms,pane,kind,offset,delta,rendering_time_ms,device,note,vp", csv);
        Assert.Contains("# scroll_probe_schema=4", csv);

        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains(lines, l => l.Contains(",Wavee,frame,") && l.Contains("42"));
        Assert.Contains(lines, l => l.Contains(",Wavee,notch,") && l.Contains("120"));
        Assert.Contains(lines, l => l.Contains(",Wavee,raw_wheel,") && l.Contains("Touchpad"));
        Assert.Contains(lines, l => l.Contains(",Wavee,state_changed,") && l.Contains("plan_kind=2"));
        Assert.Contains(lines, l => l.Contains(",Wavee,view_changed,") && l.Contains("extent;anchored=0"));

        // Every data row (skip the 4 '#' header comments + the column header) must carry pane "Wavee".
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("qpc_ms,")) continue;
            var cols = line.Split(',');
            Assert.Equal("Wavee", cols[1]);
        }
    }

    /// <summary>Schema 2: the viewport on every viewport-bound row (the LAST column, so analyze.py's eight columns are
    /// unchanged), the pose's pre-clamp plan position and a <c>coverage_clamp</c> row naming both, the published coverage
    /// (start, end, window origin, first, last), extent rows with their cause, and plan rows with start + dest.</summary>
    [Fact]
    public void Csv_CarriesTheViewportAndEveryStageOfTheClampChain()
    {
        var ui = new[]
        {
            ProbeRow.ForInput(Qpc(10), ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, 0f, 1f),
            ProbeRow.ForPlan(Qpc(11), vp: 12, seq: 3, kind: 1, start: 0.0, dest: 32.0, durationS: 0.257),
            ProbeRow.ForCoverage(Qpc(12), vp: 12, start: 408.0, end: 608.0, origin: 408.0, first: 2, last: 7),
            ProbeRow.ForExtent(Qpc(13), vp: 12, index: 4, delta: 24.0, anchored: true, ProbeExtentCause.FrameShift),
            ProbeRow.ForExtent(Qpc(14), vp: 30, index: -1, delta: -48.0, anchored: true, ProbeExtentCause.Structural),
        };
        var render = new[]
        {
            ProbeRow.ForPose(Qpc(15), vp: 12, pos: 408.0, vel: 0.0, clamped: true, snappedTrans: 0f, planPos: 0.0),
            ProbeRow.ForPose(Qpc(15), vp: 30, pos: 5.0, vel: 0.0),
        };
        var sw = new StringWriter();
        ScrollProbe.ExportCsv(sw, ui, render, qpcFrequency: 1.0, refreshHz: 120.0, dpiScale: 1.0, scenario: null);
        var rows = sw.ToString().Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("qpc_ms,")).Select(l => l.Split(',')).ToArray();

        Assert.All(rows, c => Assert.Equal(9, c.Length));
        string[] Row(string kind, string vp, string noteHas)
            => Assert.Single(rows, c => c[2] == kind && c[8] == vp && c[7].Contains(noteHas));

        Assert.Equal("", Assert.Single(rows, c => c[2] == "notch")[8]);                     // input: recorded before routing
        var plan = Row("state_changed", "12", "plan_kind=1");
        Assert.Contains("kind=Wheel", plan[7]);
        Assert.Contains("dest=32", plan[7]);
        Assert.Equal("32", plan[4]);                                                         // delta = dest - start
        var cov = Row("coverage", "12", "start=408");
        Assert.Contains("end=608;origin=408;first=2;last=7", cov[7]);
        Assert.Contains("cause=frame", Row("view_changed", "12", "extent;anchored=1")[7]);
        Assert.Contains("cause=structural", Row("view_changed", "30", "extent;anchored=1")[7]);
        var frame = Row("frame", "12", "clamped=1");
        Assert.Equal("408", frame[3]);
        Assert.Contains("plan=0", frame[7]);
        Assert.Contains("shown=408", Row("state_changed", "12", "coverage_clamp;plan=0")[7]);
        Assert.Contains("clamped=0", Row("frame", "30", "plan=5")[7]);
    }

    /// <summary>The live rings wrap independently: a wrapped ring's retained rows start later than the other ring's. The
    /// export clips every row older than the latest first row of a WRAPPED ring (the common window) and says so in the
    /// header; a complete ring clips nothing.</summary>
    [Fact]
    public void Csv_AWrappedRing_ClipsTheOtherRingToTheCommonWindow()
    {
        var ui = new[]
        {
            ProbeRow.ForInput(Qpc(100), ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, 0f, 1f),    // before the window
            ProbeRow.ForPlan(Qpc(101), vp: 5, seq: 1, kind: 1, start: 0.0, dest: 32.0),                  // before the window
            ProbeRow.ForInput(Qpc(500), ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, 0f, 1f),
            ProbeRow.ForPlan(Qpc(501), vp: 5, seq: 2, kind: 1, start: 32.0, dest: 64.0),
        };
        var render = new[]
        {
            ProbeRow.ForPose(Qpc(400), vp: 5, pos: 30.0, vel: 0.0),
            ProbeRow.ForPose(Qpc(600), vp: 5, pos: 40.0, vel: 0.0),
        };

        var clipped = new StringWriter();
        ScrollProbe.ExportCsv(clipped, ui, render, 1.0, 120.0, 1.0, scenario: null, renderWrapped: true);
        string c = clipped.ToString();
        Assert.Contains("# window_dropped=2", c);
        Assert.Contains("(common", c);
        Assert.Contains("# render_span_ms=", c);
        Assert.DoesNotContain("seq=1;", c);
        Assert.Contains("seq=2;", c);
        var times = c.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("qpc_ms,"))
            .Select(l => double.Parse(l.Split(',')[0], CultureInfo.InvariantCulture)).ToArray();
        Assert.True(times.Min() >= Qpc(400));
        Assert.Equal(times.OrderBy(t => t).ToArray(), times);                               // one merged, time-ordered stream

        var complete = new StringWriter();
        ScrollProbe.ExportCsv(complete, ui, render, 1.0, 120.0, 1.0, scenario: null);
        Assert.Contains("# window_dropped=0", complete.ToString());
        Assert.Contains("seq=1;", complete.ToString());
    }

    // ── TurnCost (evidence-diagnostics §A.4, schema 3) ────────────────────────────────────────────────────────────

    /// <summary>The composite turn notes its cost split; the present that ends it writes the Turn row and, right after it,
    /// the TurnCost row with the SAME tick and the recorder pass frame it names — at the default Summary level.</summary>
    [Fact]
    public void TurnCost_FollowsItsTurnRow_WithTheSameTickAndThePassFrame()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();
        long r0 = ScrollProbe.RenderWriteCount;

        ScrollProbe.NoteTurnCost(recordMs: 1.5, buildMs: 0.4, submitMs: 0.8, gpuMs: 2.1, tilesRastered: 3, kibRastered: 6144,
            slicesWalked: 2, items: 300, flags: ProbeRow.TurnCostCompositeOnly | ProbeRow.TurnCostCapture, passFrame: 4321);
        ScrollProbe.Turn(Qpc(5), tickSeq: 77, missedTicks: 0, wakeLagMs: 0.1, slotWaitMs: 0.25, workMs: 3.0, fresh: false);

        var rows = new ProbeRow[8];
        int n = ScrollProbe.ReadRender(r0, rows, out _);
        ScrollProbe.Level = ProbeLevel.Off;

        Assert.Equal(2, n);
        Assert.Equal(ProbeRowKind.Turn, rows[0].Kind);
        ProbeRow c = rows[1];
        Assert.Equal(ProbeRowKind.TurnCost, c.Kind);
        Assert.Equal(77, c.TickSeq);
        Assert.Equal(Qpc(5), c.Qpc);
        Assert.Equal(1.5f, c.TurnRecordMs);
        Assert.Equal(0.4f, c.TurnBuildMs);
        Assert.Equal(0.8, c.TurnSubmitMs, 9);
        Assert.Equal(2.1, c.TurnGpuMs, 9);
        Assert.Equal(4321u, c.TurnPassFrame);
        Assert.Equal(3, c.TurnTilesRastered);
        Assert.Equal(6144, c.TurnKiBRastered);
        Assert.Equal(2, c.TurnSlicesWalked);
        Assert.Equal(255, c.TurnItems);   // clamped
        Assert.True(c.TurnCompositeOnly);
        Assert.True(c.TurnCapture);
        Assert.False(c.TurnKeptAll);
        Assert.False(c.TurnSkipSubmit);
    }

    /// <summary>A noted cost is written ONCE: the next present without a new note carries a Turn row only.</summary>
    [Fact]
    public void TurnCost_IsWrittenOnce_APresentWithoutANewNoteCarriesNone()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();
        long r0 = ScrollProbe.RenderWriteCount;
        ScrollProbe.NoteTurnCost(1.0, 0.1, 0.1, 0.0, 0, 0, 0, 10, 0);
        ScrollProbe.Turn(Qpc(1), 1, 0, 0.0, 0.0, 1.0, true);
        ScrollProbe.Turn(Qpc(2), 2, 0, 0.0, 0.0, 1.0, false);

        var rows = new ProbeRow[8];
        int n = ScrollProbe.ReadRender(r0, rows, out _);
        ScrollProbe.Level = ProbeLevel.Off;

        Assert.Equal(3, n);
        Assert.Equal(new[] { ProbeRowKind.Turn, ProbeRowKind.TurnCost, ProbeRowKind.Turn },
            rows.Take(n).Select(r => r.Kind).ToArray());
    }

    /// <summary>Off records nothing, and a cost noted while Off never leaks into a later present.</summary>
    [Fact]
    public void TurnCost_Off_RecordsNothing()
    {
        ScrollProbe.Level = ProbeLevel.Off;
        FreshBurst();
        long r0 = ScrollProbe.RenderWriteCount;
        ScrollProbe.NoteTurnCost(1.0, 0.1, 0.1, 0.0, 0, 0, 0, 10, 0);
        ScrollProbe.Turn(Qpc(1), 1, 0, 0.0, 0.0, 1.0, true);
        ScrollProbe.Level = ProbeLevel.Summary;
        ScrollProbe.Turn(Qpc(2), 2, 0, 0.0, 0.0, 1.0, true);

        var rows = new ProbeRow[8];
        int n = ScrollProbe.ReadRender(r0, rows, out _);
        ScrollProbe.Level = ProbeLevel.Off;

        Assert.Equal(1, n);
        Assert.Equal(ProbeRowKind.Turn, rows[0].Kind);
    }

    /// <summary>Since schema 3: a TurnCost row exports as <c>turn_cost</c> with its tick and the whole split in the note.</summary>
    [Fact]
    public void Csv_WritesTheTurnCostRow()
    {
        var render = new[]
        {
            ProbeRow.ForTurn(Qpc(20), tickSeq: 9, missedTicks: 0, wakeLagMs: 0.1, slotWaitMs: 0.5, workMs: 2.0, fresh: true),
            ProbeRow.ForTurnCost(Qpc(20), tickSeq: 9, recordMs: 1.25f, buildMs: 0.5f, submitMs: 0.75, gpuMs: 3.0, passFrame: 77,
                tilesRastered: 4, kibRastered: 8192, slicesWalked: 3, items: 42, flags: ProbeRow.TurnCostKeptAll),
        };
        var sw = new StringWriter();
        ScrollProbe.ExportCsv(sw, ReadOnlySpan<ProbeRow>.Empty, render, 1.0, 60.0, 1.0, scenario: null);
        string csv = sw.ToString();

        Assert.Contains("# scroll_probe_schema=4", csv);
        string line = csv.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.Contains(",Wavee,turn_cost,"));
        Assert.Contains("tick=9;", line);
        Assert.Contains("record_ms=1.25;build_ms=0.5;submit_ms=0.75;gpu_ms=3;pass=77", line);
        Assert.Contains("tiles=4;kib=8192;walked=3;items=42;composite_only=0;kept_all=1;skip=0;capture=0", line);
    }

    /// <summary>"Blocked" is answerable from a default-level export: every wheel NOTCH row — detented and hi-res — is kept at
    /// Summary (evidence-diagnostics §A.4 asked for it; the Summary filter already keeps notch-phase rows of any source).</summary>
    [Fact]
    public void Summary_KeepsDetentedAndHiResNotchRows()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();
        long u0 = ScrollProbe.UiWriteCount;
        ScrollProbe.Input(ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, Qpc(1), 0f, 1f, -1);
        ScrollProbe.Input(ScrollSourceCode.MouseWheelHiRes, ScrollProbe.PhaseNotch, Qpc(2), 0f, 0.125f, -1);

        var rows = new ProbeRow[8];
        int n = ScrollProbe.ReadUi(u0, rows, out _);
        ScrollProbe.Level = ProbeLevel.Off;

        Assert.Equal(2, n);
        Assert.True(rows[0].IsNotch && rows[1].IsNotch);
        Assert.Equal(ScrollSourceCode.MouseWheelHiRes, rows[1].Source);
    }

    [Fact]
    public void Summary_SkipsAnchoredExtentAndNonNotchInput()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();

        ScrollProbe.Input(ScrollSourceCode.MouseWheel, 1, Qpc(1), 0f, 4f, 1);          // sample: dropped
        ScrollProbe.Input(ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, Qpc(2), 0f, 120f, vp: 1);   // notch: kept
        ScrollProbe.Extent(1, Qpc(3), index: 0, delta: 1.0, anchoredSameFrame: true);              // anchored: dropped
        ScrollProbe.Extent(1, Qpc(4), index: 1, delta: 2.0, anchoredSameFrame: false);              // jump: kept
        ScrollProbe.Coverage(1, Qpc(5), 0.0, 100.0, 0.0, 0, 5);                                     // Trace-only: dropped
        ScrollProbe.Plan(1, 1, 0, Qpc(6), 0.0, 1.0, 0.1);                                // Trace-only: dropped

        var summary = ScrollProbe.EndBurst(Qpc(10));

        Assert.Equal(1, summary.Notches);
        Assert.Equal(1, summary.ExtentJumps);
    }

    /// <summary>Schema 4: a touchpad <c>raw_wheel</c> row names its contact phase, and the End its producer's release
    /// verdict, so a zero-delta Begin/End row is never read as a sample and a lift reads with what DirectManipulation said
    /// about it. A hi-res wheel row keeps its empty note.</summary>
    [Fact]
    public void Csv_Schema4_TouchpadRowsCarryPhaseAndRelease()
    {
        var ui = new[]
        {
            ProbeRow.ForInput(Qpc(1), ScrollSourceCode.Touchpad, 0, 0f, 0f),                 // Begin
            ProbeRow.ForInput(Qpc(2), ScrollSourceCode.Touchpad, 1, 0f, 94.0318f),           // Sample
            ProbeRow.ForInput(Qpc(3), ScrollSourceCode.Touchpad, 2, 0f, 0f, release: 1),     // End, released moving
            ProbeRow.ForInput(Qpc(4), ScrollSourceCode.Touchpad, 0, 0f, 0f),
            ProbeRow.ForInput(Qpc(5), ScrollSourceCode.Touchpad, 2, 0f, 0f, release: 2),     // End, released at rest
            ProbeRow.ForInput(Qpc(6), ScrollSourceCode.Touchpad, 2, 0f, 0f),                 // End, no verdict
            ProbeRow.ForInput(Qpc(7), ScrollSourceCode.MouseWheelHiRes, 1, 0f, 0.25f),
        };
        Assert.Equal((byte)1, ui[2].Release);
        var sw = new StringWriter();
        ScrollProbe.ExportCsv(sw, ui, ReadOnlySpan<ProbeRow>.Empty, 1.0, 60.0, 1.0, scenario: null);
        string csv = sw.ToString();

        Assert.Contains("# scroll_probe_schema=4", csv);
        var rows = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains(",Wavee,raw_wheel,")).ToArray();
        Assert.Equal(7, rows.Length);
        Assert.EndsWith(",Touchpad,phase=begin,", rows[0]);
        Assert.EndsWith(",Touchpad,phase=sample,", rows[1]);
        Assert.EndsWith(",Touchpad,phase=end;release=moving,", rows[2]);
        Assert.EndsWith(",Touchpad,phase=begin,", rows[3]);
        Assert.EndsWith(",Touchpad,phase=end;release=stopped,", rows[4]);
        Assert.EndsWith(",Touchpad,phase=end;release=unknown,", rows[5]);
        Assert.EndsWith(",MouseWheelHiRes,,", rows[6]);
    }

    /// <summary>The probe's contact Input row keeps the End's release byte (drained rows carry it).</summary>
    [Fact]
    public void Input_KeepsTheReleaseVerdictOfAnEnd()
    {
        ScrollProbe.Level = ProbeLevel.Trace;
        FreshBurst();
        long from = ScrollProbe.UiWriteCount;
        ScrollProbe.Input(ScrollSourceCode.Touchpad, 2, Qpc(1), 0f, 0f, -1, release: 1);
        var rows = new ProbeRow[4];
        int n = ScrollProbe.ReadUi(from, rows, out _);
        ScrollProbe.EndBurst(Qpc(10));
        ScrollProbe.Level = ProbeLevel.Off;
        Assert.Equal(1, n);
        Assert.Equal((byte)2, rows[0].Phase);
        Assert.Equal((byte)1, rows[0].Release);
    }

    [Fact]
    public void Summary_KeepsContactStreamInput_ForTheTrackingMetrics()
    {
        // A Scroll Lab session records at Summary; its touchpad tracking metrics are defined against the finger's own
        // stream, so every contact report (Begin/Sample/End of touchpad, touch, pen) must survive — a wheel's non-notch
        // row still does not.
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();
        long from = ScrollProbe.UiWriteCount;
        ScrollProbe.Input(ScrollSourceCode.Touchpad, 0, Qpc(1), 0f, 0f, -1);   // Begin
        ScrollProbe.Input(ScrollSourceCode.Touchpad, 1, Qpc(2), 0f, 6f, -1);   // Sample
        ScrollProbe.Input(ScrollSourceCode.Touch, 1, Qpc(3), 0f, 5f, -1);
        ScrollProbe.Input(ScrollSourceCode.Pen, 1, Qpc(4), 0f, 4f, -1);
        ScrollProbe.Input(ScrollSourceCode.Touchpad, 2, Qpc(5), 0f, 0f, -1);   // End
        ScrollProbe.Input(ScrollSourceCode.MouseWheelHiRes, 1, Qpc(6), 0f, 1f, -1);   // not a contact, not a notch
        Assert.Equal(5, ScrollProbe.UiWriteCount - from);
        ScrollProbe.EndBurst(Qpc(10));
        ScrollProbe.Level = ProbeLevel.Off;
    }

    [Fact]
    public void RingWraparound_KeepsOnlyTheLatestEntries()
    {
        const int RingCapacity = 8192;   // matches ScrollProbe's documented render-ring capacity (design B.9)
        const int Pushed = RingCapacity + 808;
        const int Vp = 42;

        ScrollProbe.Level = ProbeLevel.Trace;
        FreshBurst();

        for (int i = 0; i < Pushed; i++)
            ScrollProbe.Pose(Vp, Qpc(i), pos: i, vel: 1.0, clamped: false, snappedTrans: 0f);

        var summary = ScrollProbe.EndBurst(Qpc(Pushed + 1));

        Assert.Equal(RingCapacity, summary.Presents);   // exactly capacity survived, never more

        var sw = new StringWriter();
        ScrollProbe.ExportCsv(sw, qpcFrequency: 1.0, refreshHz: 60.0, dpiScale: 1.0);
        var framePositions = sw.ToString().Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains(",Wavee,frame,"))
            .Select(l => double.Parse(l.Split(',')[3], CultureInfo.InvariantCulture))
            .ToArray();

        // The surviving window is the LATEST pushes: the export drains the ring through ReadRender, whose torn-read guard
        // gives up the oldest retained slot of a full ring (the slot the next write would overwrite mid-copy) — so the
        // file holds indices [Pushed-RingCapacity+1, Pushed), never anything older.
        Assert.Equal(Pushed - RingCapacity + 1, (int)framePositions.Min());
        Assert.Equal(Pushed - 1, (int)framePositions.Max());
    }

    [Fact]
    public void EndBurst_UniformPoseStream_IsSmoothWithNearZeroJitter()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();

        for (int i = 0; i < 40; i++)
            ScrollProbe.Pose(1, Qpc(i * 1000), pos: i * 10.0, vel: 10000.0, clamped: false, snappedTrans: (float)(i * 10));

        var summary = ScrollProbe.EndBurst(Qpc(100_000));

        Assert.Equal(ScrollVerdict.Smooth, summary.Verdict);
        Assert.True(summary.MaxJitter < 1e-9, $"expected ~0 jitter for a uniform stream, got {summary.MaxJitter}");
        Assert.Equal(0, summary.CoverageClamps);
        Assert.Equal(0, summary.ExtentJumps);
        Assert.Equal(0, summary.LatePresents);
    }

    [Fact]
    public void EndBurst_ClampedPose_IsClamped()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();

        for (int i = 0; i < 40; i++)
            ScrollProbe.Pose(2, Qpc(i * 1000), pos: i * 10.0, vel: 10000.0, clamped: i == 20, snappedTrans: 0f);

        var summary = ScrollProbe.EndBurst(Qpc(100_000));

        Assert.Equal(ScrollVerdict.Clamped, summary.Verdict);
        Assert.Equal(1, summary.CoverageClamps);
    }

    [Fact]
    public void EndBurst_NonAnchoredExtentDelta_IsJumped()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();

        for (int i = 0; i < 40; i++)
            ScrollProbe.Pose(3, Qpc(i * 1000), pos: i * 10.0, vel: 10000.0, clamped: false, snappedTrans: 0f);
        ScrollProbe.Extent(3, Qpc(20_500), index: 7, delta: 48.0, anchoredSameFrame: false);

        var summary = ScrollProbe.EndBurst(Qpc(100_000));

        Assert.Equal(ScrollVerdict.Jumped, summary.Verdict);
        Assert.Equal(1, summary.ExtentJumps);
        Assert.Equal(0, summary.CoverageClamps);
    }

    [Fact]
    public void EndBurst_DoubledPresentGap_IsLate()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();

        long t = 0;
        const long Step = 1000;
        for (int i = 0; i < 40; i++)
        {
            // One doubled gap in the middle; position still advances uniformly per SAMPLE (not per unit time), so
            // jitter (which is computed on position deltas only) stays ~0 and only the Late condition fires.
            if (i == 20) t += Step * 2; else t += Step;
            ScrollProbe.Pose(4, Qpc(t), pos: i * 10.0, vel: 10000.0, clamped: false, snappedTrans: 0f);
        }

        var summary = ScrollProbe.EndBurst(Qpc(200_000));

        Assert.Equal(ScrollVerdict.Late, summary.Verdict);
        Assert.True(summary.LatePresents >= 1);
        Assert.Equal(0, summary.CoverageClamps);
        Assert.Equal(0, summary.ExtentJumps);
    }

    [Fact]
    public void BurstSummary_FormatLine_IsNonEmptyAndCarriesVerdict()
    {
        ScrollProbe.Level = ProbeLevel.Summary;
        FreshBurst();
        ScrollProbe.Pose(5, Qpc(1), pos: 0.0, vel: 0.0, clamped: false, snappedTrans: 0f);
        var summary = ScrollProbe.EndBurst(Qpc(2));

        string line = summary.FormatLine();
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.Contains("verdict=" + summary.Verdict, line);
    }

    // ── ScrollTunables ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tunables_JsonRoundTrip_PreservesEveryField()
    {
        var custom = FeelProfiles.Standard with
        {
            WheelNotchDip = 56.0,
            WheelDurationS = 0.4,
            AccelMax = 3.5,
            SpringOmega = 24.0,
            SpringZeta = 0.9,
            SettleVelocity = 2.0,
            WheelRiseS = 0.02,
            AccelUnityGapS = 0.07,
            TouchpadWheelDip = 40.0,
            TouchpadReleaseCapDipPerS = 9000.0,
        };
        ScrollTunables.Apply(custom);

        string json = ScrollTunables.ToJson();
        Assert.True(ScrollTunables.TryFromJson(json, out var roundTripped));
        Assert.Equal(custom, roundTripped);
        Assert.Equal(ScrollTunables.Current, roundTripped);
    }

    [Fact]
    public void Tunables_TryFromJson_MalformedInput_ReturnsFalseAndTheDefault()
    {
        bool ok = ScrollTunables.TryFromJson("{not json", out var feel);
        Assert.False(ok);
        Assert.Equal(FeelProfiles.Standard, feel);
    }

    [Fact]
    public void Tunables_ApplyProfile_SwitchesTheLiveFeelAndName()
    {
        ScrollTunables.ApplyProfile("Glide");
        Assert.Equal(FeelProfiles.Glide, ScrollTunables.Current);
        Assert.Equal("Glide", ScrollTunables.ActiveProfileName);

        ScrollTunables.ApplyProfile("standard");   // case-insensitive
        Assert.Equal(FeelProfiles.Standard, ScrollTunables.Current);
        Assert.Equal("Standard", ScrollTunables.ActiveProfileName);

        // Unknown profile name (a retired one included): silent no-op, previous state unchanged.
        ScrollTunables.ApplyProfile("WinUiExact");
        Assert.Equal(FeelProfiles.Standard, ScrollTunables.Current);
        Assert.Equal("Standard", ScrollTunables.ActiveProfileName);

        ScrollTunables.ApplyProfile("Glide");
        Assert.Equal(FeelProfiles.Glide, ScrollTunables.Current);
    }

    [Fact]
    public void Tunables_Apply_SetsActiveProfileNameToCustom()
    {
        ScrollTunables.ApplyProfile("Glide");
        ScrollTunables.Apply(FeelProfiles.Glide with { WheelNotchDip = 99.0 });
        Assert.Equal("Custom", ScrollTunables.ActiveProfileName);
    }

    [Fact]
    public void Tunables_All_CoversEveryDoubleFieldWithItsDefault()
    {
        var all = ScrollTunables.All;
        Assert.Equal(26, all.Count);   // one row per MotionFeel field
        foreach (var t in all)
        {
            Assert.True(t.Min < t.Max);
            double got = t.Get(FeelProfiles.Standard);
            Assert.Equal(t.Default, got, 6);
            var modified = t.With(FeelProfiles.Standard, t.Default + 1.0);
            Assert.Equal(t.Default + 1.0, t.Get(modified), 6);
        }
    }

    [Fact]
    public void Tunables_Version_IncreasesOnEveryApply()
    {
        uint before = ScrollTunables.Version;
        ScrollTunables.ApplyProfile("Glide");
        uint afterOne = ScrollTunables.Version;
        ScrollTunables.ApplyProfile("Standard");
        uint afterTwo = ScrollTunables.Version;

        Assert.True(afterOne > before);
        Assert.True(afterTwo > afterOne);
    }

    [Fact]
    public async Task Tunables_Seqlock_ConcurrentWritesNeverProduceATornRead()
    {
        var a = FeelProfiles.Standard with { WheelNotchDip = 32.0, SpringOmega = 24.0 };
        var b = FeelProfiles.Glide with { WheelNotchDip = 56.0, SpringOmega = 40.0 };
        ScrollTunables.Apply(a);

        const int Iterations = 20_000;
        using var stop = new CancellationTokenSource();
        var readerException = default(Exception);

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < Iterations; i++)
                ScrollTunables.Apply((i & 1) == 0 ? a : b);
            stop.Cancel();
        }, TestContext.Current.CancellationToken);

        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    MotionFeel f = ScrollTunables.Current;
                    // The read must exactly equal ONE of the two applied values in every field — never a mix
                    // (e.g. `a`'s WheelNotchDip with `b`'s SpringOmega), which is exactly what a torn read looks like.
                    bool matchesA = f.WheelNotchDip == a.WheelNotchDip && f.SpringOmega == a.SpringOmega;
                    bool matchesB = f.WheelNotchDip == b.WheelNotchDip && f.SpringOmega == b.SpringOmega;
                    if (!matchesA && !matchesB)
                        throw new InvalidOperationException($"torn read: WheelNotchDip={f.WheelNotchDip} SpringOmega={f.SpringOmega}");
                }
            }
            catch (Exception ex)
            {
                readerException = ex;
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writer, reader);
        Assert.Null(readerException);
    }
    // The render ring has exactly ONE producer: the render-thread poser. The UI-thread poser (AppHost.PoseScrollUi —
    // hit-testing / the headless recorder) evaluates the same plans with the same arithmetic, but it must never write
    // Pose rows: that made the render ring a two-writer ring (a data race on s_renderCount) and recorded every frame's
    // pose twice. A default-constructed poser is the UI poser.
    [Fact]
    public void AUiPoserTickRecordsNoPoseRow()
    {
        ScrollProbe.Level = ProbeLevel.Trace;
        FreshBurst();
        const int VpNode = 424_242;   // unique to this test, so rows from any other writer cannot be mistaken for ours
        var vp = new FluentGpu.Scroll.Runtime.ScrollViewportId(VpNode, 1);
        var slots = new FluentGpu.Scroll.Runtime.PlanSlots();
        slots.Allocate(vp, ScrollPlan.Idle(VpNode, 100.0, 0.0, 1000.0));
        var cov = new FluentGpu.Scroll.Runtime.ScrollCoverageTable();
        cov.AddRow(new FluentGpu.Scroll.Runtime.ScrollCoverageRow(VpNode, 1, 1, 0.0, 0.0, 2000.0, 400.0, 2000.0, false, 0, 0, 0.0),
            ReadOnlySpan<FluentGpu.Scroll.Runtime.ScrollEffectRow>.Empty);
        var ui = new FluentGpu.Scroll.Runtime.ScrollPoser();
        ui.Adopt(cov);

        long from = ScrollProbe.RenderWriteCount;
        ui.Tick(slots, 1.0, 1f, NullSink.Instance);
        ui.Tick(slots, 1.1, 1f, NullSink.Instance);
        var rows = new ProbeRow[64];
        int n = ScrollProbe.ReadRender(from, rows, out _);
        int ours = 0;
        for (int i = 0; i < n; i++) if (rows[i].Kind == ProbeRowKind.Pose && rows[i].Vp == VpNode) ours++;
        ScrollProbe.Level = ProbeLevel.Off;
        Assert.Equal(0, ours);
    }

    [Fact]
    public void TheRenderPoserIsTheRenderRingsOneProducer_OnePoseRowPerViewportTick()
    {
        ScrollProbe.Level = ProbeLevel.Trace;
        FreshBurst();
        const int VpNode = 434_343;
        var vp = new FluentGpu.Scroll.Runtime.ScrollViewportId(VpNode, 1);
        var slots = new FluentGpu.Scroll.Runtime.PlanSlots();
        slots.Allocate(vp, ScrollPlan.Idle(VpNode, 100.0, 0.0, 1000.0));
        var cov = new FluentGpu.Scroll.Runtime.ScrollCoverageTable();
        cov.AddRow(new FluentGpu.Scroll.Runtime.ScrollCoverageRow(VpNode, 1, 1, 0.0, 0.0, 2000.0, 400.0, 2000.0, false, 0, 0, 0.0),
            ReadOnlySpan<FluentGpu.Scroll.Runtime.ScrollEffectRow>.Empty);
        var render = new FluentGpu.Scroll.Runtime.ScrollPoser(recordsProbePoses: true);
        render.Adopt(cov);
        long from = ScrollProbe.RenderWriteCount;
        render.Tick(slots, 1.0, 1f, NullSink.Instance);
        render.Tick(slots, 1.1, 1f, NullSink.Instance);
        var rows = new ProbeRow[64];
        int n = ScrollProbe.ReadRender(from, rows, out _);
        int ours = 0;
        for (int i = 0; i < n; i++) if (rows[i].Kind == ProbeRowKind.Pose && rows[i].Vp == VpNode) ours++;
        ScrollProbe.Level = ProbeLevel.Off;
        Assert.Equal(2, ours);
    }

    private sealed class NullSink : FluentGpu.Scroll.Runtime.IScrollPoseSink
    {
        public static readonly NullSink Instance = new();
        public void PoseViewport(int vpNode, double shown) { }
        public void PoseContent(int node, bool horizontal, float trans, bool changed) { }
        public void PoseEffect(int node, FluentGpu.Scroll.Effects.EffectChannel channel, float value, bool changed) { }
        public void PoseTransform(int node, in FluentGpu.Scroll.Effects.EffectTransform transform, bool changed) { }
    }
}
