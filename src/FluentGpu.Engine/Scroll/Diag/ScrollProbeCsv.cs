using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FluentGpu.Scroll.Diag;

public static partial class ScrollProbe
{
    /// <summary>The CSV schema version written as <c># scroll_probe_schema=</c>. 2 = the <c>vp</c> column, pose
    /// plan/clamp notes, <c>coverage</c> rows, extent causes, plan destinations and the time-aligned window. 3 = the
    /// <c>turn_cost</c> row after each <c>turn</c> (the render turn's record / build / submit / GPU split, tiles, walks —
    /// evidence-diagnostics §A.4). 4 = a touchpad <c>raw_wheel</c> row names its contact phase and, on the End, the
    /// producer's release verdict in the note (<c>phase=begin|sample|end[;release=unknown|moving|stopped]</c>), so a
    /// zero-delta Begin/End row is never mistaken for a sample and a lift reads with what the producer said about it.</summary>
    public const int CsvSchema = 4;

    /// <summary>
    /// Export every still-retained ring record (the live rings — Wavee's Diagnostics ▸ Scroll ▸ Export CSV). Drains both
    /// rings and writes exactly the file the row-based overload writes, with no scenario. Off the hot path — allocates.
    /// </summary>
    public static void ExportCsv(TextWriter w, double qpcFrequency, double refreshHz, double dpiScale)
    {
        var ui = new ProbeRow[RingCapacity];
        var render = new ProbeRow[RingCapacity];
        int nUi = ReadUi(0, ui, out _);
        int nRender = ReadRender(0, render, out _);
        // A ring that has written more than its capacity lost its oldest history: it only speaks from its first retained
        // row on, so the other ring's older rows are clipped to that instant (see the window rule below).
        WriteCsv(w, ui.AsSpan(0, nUi), render.AsSpan(0, nRender), qpcFrequency, refreshHz, dpiScale, scenario: null,
            viewport: -1, uiWrapped: UiWriteCount > RingCapacity, renderWrapped: RenderWriteCount > RingCapacity);
    }

    /// <summary>
    /// The scroll probe CSV over drained rows (<see cref="ReadUi"/>/<see cref="ReadRender"/> — the live rings or a
    /// recorded lab session), in the <c>wheel-curve-probe/analyze.py</c> format: header comments
    /// (<c>display_refresh_hz</c>/<c>qpc_frequency</c>/<c>dpi_scale</c>/<c>mode=wavee</c>/<c>scroll_probe_schema</c> + the
    /// window comments below), then <c>qpc_ms,pane,kind,offset,delta,rendering_time_ms,device,note,vp</c> — analyze.py's
    /// eight columns first (<c>pane="Wavee"</c>), the viewport node id LAST (<c>vp</c>; empty on rows that belong to no
    /// viewport: input — recorded before routing; the plan row that follows names the viewport — presents, turns,
    /// markers). Rows of both rings are merged in time order.
    /// <list type="bullet">
    /// <item>Pose → <c>frame</c>: offset = shown position, delta = shown − the viewport's previous shown, rendering_time_ms =
    /// the present time, note = <c>vel=…;trans=…;plan=…;clamped=0|1</c> (<c>plan</c> = the plan before the coverage clamp).
    /// A clamped pose also writes <c>state_changed</c> with note <c>coverage_clamp;plan=…;shown=…</c>.</item>
    /// <item>Plan → <c>state_changed</c>: offset = start, delta = dest − start, note =
    /// <c>plan_kind=&lt;byte&gt;;kind=&lt;name&gt;;seq=…;start=…;dest=…;dur_s=…</c>.</item>
    /// <item>Coverage → <c>coverage</c> (the published row the posers clamp against, recorded when it changes): offset =
    /// start, delta = end − start, note = <c>start=…;end=…;origin=…;first=…;last=…</c>.</item>
    /// <item>Extent → <c>view_changed</c>: delta = the frame shift, note =
    /// <c>extent;anchored=0|1;cause=measured|frame|structural;index=…</c> (tag <c>extent</c>, per analyze.py's
    /// <c>tag_of</c>).</item>
    /// <item>Input → <c>notch</c> (a notch phase) and/or <c>raw_wheel</c> (hi-res/touchpad), delta = dy, device = source; a
    /// touchpad row's note is <c>phase=begin|sample|end</c>, the End's with <c>;release=unknown|moving|stopped</c>
    /// (schema 4).</item>
    /// <item><c>present</c>, <c>turn</c>, <c>marker</c> (+ <c>scenario_start</c> on a session start when
    /// <paramref name="scenario"/> is given) as documented on the row kinds.</item>
    /// </list>
    /// <para>TIME-ALIGNED WINDOW: the two live rings wrap independently (the render ring fills with one pose per viewport
    /// per tick, the UI ring with inputs/plans), so a wrapped ring's retained history starts later than the other's. The
    /// live export drops every row older than the LATEST first row of a ring that WRAPPED — before that instant the
    /// wrapped ring's rows are gone and a reader would see notches with no frames (or frames with no plans). A ring that
    /// never wrapped is complete and clips nothing; drained session rows are complete (the lab drains continuously).
    /// Both rings run up to the export, so the window ends at the last row either holds. <c># ui_span_ms</c>,
    /// <c># render_span_ms</c> (each marked wrapped/complete) and <c># window_ms</c> state the spans (<c>first..last</c> on
    /// the <c>qpc_ms</c> clock); <c># window_dropped</c> counts the rows the clip removed.</para>
    /// <paramref name="uiWrapped"/>/<paramref name="renderWrapped"/> say which rows come from a ring that lost older
    /// history (the live export passes the rings' own state). <paramref name="viewport"/> ≥ 0 keeps only that viewport's
    /// viewport-bound rows (vp-less rows stay). Numbers are
    /// invariant-culture. Off the hot path — allocates freely.
    /// </summary>
    public static void ExportCsv(TextWriter w, ReadOnlySpan<ProbeRow> ui, ReadOnlySpan<ProbeRow> render,
        double qpcFrequency, double refreshHz, double dpiScale, string? scenario = "lab", int viewport = -1,
        bool uiWrapped = false, bool renderWrapped = false)
        => WriteCsv(w, ui, render, qpcFrequency, refreshHz, dpiScale, scenario, viewport, uiWrapped, renderWrapped);

    private static void WriteCsv(TextWriter w, ReadOnlySpan<ProbeRow> ui, ReadOnlySpan<ProbeRow> render,
        double qpcFrequency, double refreshHz, double dpiScale, string? scenario, int viewport, bool uiWrapped, bool renderWrapped)
    {
        var ci = CultureInfo.InvariantCulture;
        double ToMs(long qpc) => qpcFrequency > 0 ? qpc / qpcFrequency * 1000.0 : 0.0;

        var uiSpan = TimeSpanOf(ui);
        var renderSpan = TimeSpanOf(render);
        long windowStart = long.MinValue;
        if (uiWrapped && uiSpan.N > 0) windowStart = Math.Max(windowStart, uiSpan.First);
        if (renderWrapped && renderSpan.N > 0) windowStart = Math.Max(windowStart, renderSpan.First);
        bool clip = windowStart != long.MinValue;
        long windowEnd = Math.Max(uiSpan.N > 0 ? uiSpan.Last : long.MinValue, renderSpan.N > 0 ? renderSpan.Last : long.MinValue);

        // Merge both rings into one time-ordered list (stable: ring order breaks ties), clipped to the common window.
        var rows = new List<(long Qpc, int Seq, ProbeRow Row)>(ui.Length + render.Length);
        int dropped = 0;
        Collect(render, rows, windowStart, viewport, ref dropped);
        Collect(ui, rows, windowStart, viewport, ref dropped);
        rows.Sort(static (a, b) => a.Qpc != b.Qpc ? a.Qpc.CompareTo(b.Qpc) : a.Seq.CompareTo(b.Seq));

        string SpanText((long First, long Last, int N) s, bool wrapped)
            => s.N == 0 ? "none" : ToMs(s.First).ToString("0.000", ci) + ".." + ToMs(s.Last).ToString("0.000", ci)
                                   + (wrapped ? " (wrapped: older history lost)" : " (complete)");

        w.WriteLine("# display_refresh_hz=" + refreshHz.ToString(ci));
        w.WriteLine("# qpc_frequency=" + qpcFrequency.ToString(ci));
        w.WriteLine("# dpi_scale=" + dpiScale.ToString(ci));
        w.WriteLine("# mode=wavee");
        w.WriteLine("# scroll_probe_schema=" + CsvSchema.ToString(ci));
        if (scenario is not null) w.WriteLine("# scroll_lab_schema=1");
        w.WriteLine("# ui_span_ms=" + SpanText(uiSpan, uiWrapped));
        w.WriteLine("# render_span_ms=" + SpanText(renderSpan, renderWrapped));
        w.WriteLine("# window_ms=" + (clip
            ? ToMs(windowStart).ToString("0.000", ci) + ".." + ToMs(windowEnd).ToString("0.000", ci) + " (common: clipped to the later wrapped ring start)"
            : "all (neither ring lost history)"));
        w.WriteLine("# window_dropped=" + dropped.ToString(ci));
        w.WriteLine("qpc_ms,pane,kind,offset,delta,rendering_time_ms,device,note,vp");

        string safeScenario = (scenario ?? "").Replace(',', '_');
        var prevPosByVp = new Dictionary<int, double>();
        foreach (var (_, _, r) in rows)
        {
            double ms = ToMs(r.Qpc);
            switch (r.Kind)
            {
                case ProbeRowKind.Pose:
                {
                    double delta = prevPosByVp.TryGetValue(r.Vp, out double prev) ? r.Pos - prev : 0.0;
                    prevPosByVp[r.Vp] = r.Pos;
                    WriteRow(w, ci, ms, "frame", r.Pos, delta, ms, "",
                        "vel=" + Num(r.Vel, ci) + ";trans=" + Num(r.SnappedTrans, ci) + ";plan=" + Num(r.PlanPos, ci)
                        + ";clamped=" + (r.Clamped ? "1" : "0"), r.Vp);
                    if (r.Clamped)
                        WriteRow(w, ci, ms, "state_changed", null, null, null, "",
                            "coverage_clamp;plan=" + Num(r.PlanPos, ci) + ";shown=" + Num(r.Pos, ci), r.Vp);
                    break;
                }
                case ProbeRowKind.Turn:
                    WriteRow(w, ci, ms, "turn", null, null, r.WorkMs, "",
                        "tick=" + r.TickSeq.ToString(ci)
                        + ";missed=" + r.MissedTicks.ToString(ci)
                        + ";wake_lag_ms=" + r.WakeLagMs.ToString("0.###", ci)
                        + ";slot_wait_ms=" + r.SlotWaitMs.ToString("0.###", ci)
                        + ";work_ms=" + r.WorkMs.ToString("0.###", ci)
                        + ";fresh=" + (r.TurnFresh ? "1" : "0"), -1);
                    break;
                case ProbeRowKind.TurnCost:
                    WriteRow(w, ci, ms, "turn_cost", null, null, r.TurnRecordMs + r.TurnBuildMs + r.TurnSubmitMs, "",
                        "tick=" + r.TickSeq.ToString(ci)
                        + ";record_ms=" + r.TurnRecordMs.ToString("0.###", ci)
                        + ";build_ms=" + r.TurnBuildMs.ToString("0.###", ci)
                        + ";submit_ms=" + r.TurnSubmitMs.ToString("0.###", ci)
                        + ";gpu_ms=" + r.TurnGpuMs.ToString("0.###", ci)
                        + ";pass=" + r.TurnPassFrame.ToString(ci)
                        + ";tiles=" + r.TurnTilesRastered.ToString(ci)
                        + ";kib=" + r.TurnKiBRastered.ToString(ci)
                        + ";walked=" + r.TurnSlicesWalked.ToString(ci)
                        + ";items=" + r.TurnItems.ToString(ci)
                        + ";composite_only=" + (r.TurnCompositeOnly ? "1" : "0")
                        + ";kept_all=" + (r.TurnKeptAll ? "1" : "0")
                        + ";skip=" + (r.TurnSkipSubmit ? "1" : "0")
                        + ";capture=" + (r.TurnCapture ? "1" : "0"), -1);
                    break;
                case ProbeRowKind.Input:
                {
                    string device = r.Source.ToString();
                    if (r.Phase == PhaseNotch)
                        WriteRow(w, ci, ms, "notch", null, r.Dy, null, device, "", -1);
                    if (r.Source == ScrollSourceCode.MouseWheelHiRes)
                        WriteRow(w, ci, ms, "raw_wheel", null, r.Dy, null, device, "", -1);
                    else if (r.Source == ScrollSourceCode.Touchpad)
                        WriteRow(w, ci, ms, "raw_wheel", null, r.Dy, null, device, ContactNote(r.Phase, r.Release), -1);
                    break;
                }
                case ProbeRowKind.Plan:
                    WriteRow(w, ci, ms, "state_changed", r.PlanStart, r.PlanDest - r.PlanStart, null, "",
                        "plan_kind=" + r.PlanKind.ToString(ci)
                        + ";kind=" + MotionKindName(r.PlanKind)
                        + ";seq=" + r.Seq.ToString(ci)
                        + ";start=" + Num(r.PlanStart, ci)
                        + ";dest=" + Num(r.PlanDest, ci)
                        + ";dur_s=" + Num(r.PlanDurationS, ci), r.Vp);
                    break;
                case ProbeRowKind.Coverage:
                    WriteRow(w, ci, ms, "coverage", r.CoverStart, r.CoverEnd - r.CoverStart, null, "",
                        "start=" + Num(r.CoverStart, ci)
                        + ";end=" + Num(r.CoverEnd, ci)
                        + ";origin=" + Num(r.CoverOrigin, ci)
                        + ";first=" + r.CoverFirst.ToString(ci)
                        + ";last=" + r.CoverLast.ToString(ci), r.Vp);
                    break;
                case ProbeRowKind.Extent:
                    WriteRow(w, ci, ms, "view_changed", null, r.ExtentDelta, null, "",
                        "extent;anchored=" + (r.ExtentAnchored ? "1" : "0")
                        + ";cause=" + CauseName(r.ExtentCause)
                        + ";index=" + r.ExtentIndex.ToString(ci), r.Vp);
                    break;
                case ProbeRowKind.Mark:
                    if (r.MarkCode == ProbeMark.SessionStart && scenario is not null)
                        WriteRow(w, ci, ms, "scenario_start", null, null, null, "", safeScenario, -1);
                    WriteRow(w, ci, ms, "marker", null, null, null, "", r.MarkCode.ToString(), -1);
                    break;
                case ProbeRowKind.Present:
                    WriteRow(w, ci, ms, "present", null, null, r.FrameMs, "",
                        "displayed=" + r.PresentsDisplayed.ToString(ci)
                        + ";dropped=" + r.PresentsDropped.ToString(ci)
                        + ";repeated=" + r.VblanksRepeated.ToString(ci)
                        + ";dwm_dropped=" + r.DwmDropped.ToString(ci)
                        + ";dwm_missed=" + r.DwmMissed.ToString(ci)
                        + ";dwm_late=" + r.DwmLate.ToString(ci)
                        + ";latency_wait_ms=" + r.LatencyWaitMs.ToString("0.###", ci)
                        + ";dwm_seq=" + r.DwmSeq.ToString(ci)
                        + ";refresh_ms=" + r.RefreshIntervalMs.ToString("0.###", ci)
                        + ";presented=" + (r.Presented ? "1" : "0"), -1);
                    break;
            }
        }
    }

    private static void Collect(ReadOnlySpan<ProbeRow> src, List<(long Qpc, int Seq, ProbeRow Row)> into, long windowStart,
        int viewport, ref int dropped)
    {
        for (int i = 0; i < src.Length; i++)
        {
            ref readonly ProbeRow r = ref src[i];
            if (!Exported(in r)) continue;
            if (viewport >= 0 && HasViewport(in r) && r.Vp != viewport) continue;
            if (r.Qpc < windowStart) { dropped++; continue; }
            into.Add((r.Qpc, into.Count, r));
        }
    }

    /// <summary>Row kinds that carry a real timestamp and are exported. <see cref="ProbeRowKind.Cost"/> rows carry an
    /// elapsed duration in their QPC slot (not a time), so they are neither exported nor used for the window.</summary>
    private static bool Exported(in ProbeRow r) => r.Kind != ProbeRowKind.Cost;

    /// <summary>Row kinds whose <see cref="ProbeRow.Vp"/> names a viewport (a Present row reuses the slot for a counter).</summary>
    private static bool HasViewport(in ProbeRow r)
        => r.Kind is ProbeRowKind.Pose or ProbeRowKind.Plan or ProbeRowKind.Coverage or ProbeRowKind.Extent;

    /// <summary>First/last timestamp over a ring's exported rows, and how many there are.</summary>
    private static (long First, long Last, int N) TimeSpanOf(ReadOnlySpan<ProbeRow> rows)
    {
        long first = long.MaxValue, last = long.MinValue;
        int n = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!Exported(in rows[i])) continue;
            long q = rows[i].Qpc;
            if (q < first) first = q;
            if (q > last) last = q;
            n++;
        }
        return (first, last, n);
    }

    private static string Num(double v, CultureInfo ci) => double.IsFinite(v) ? v.ToString("0.####", ci) : "";

    /// <summary>The <c>MotionKind</c> name for a plan row's kind byte (by ordinal — the diagnostics layer takes no type
    /// dependency on the motion enum).</summary>
    private static string MotionKindName(byte kind) => kind switch
    {
        0 => "Idle",
        1 => "Wheel",
        2 => "Drag",
        3 => "Fling",
        4 => "Programmatic",
        5 => "Thumb",
        _ => "K" + kind.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A touchpad contact row's note (schema 4): the gesture phase byte (<c>ScrollGesture</c>: 0 Begin, 1 Sample,
    /// 2 End — kept generic, like the probe's phase) and, on the End, the release verdict byte (<c>ContactRelease</c>).</summary>
    private static string ContactNote(byte phase, byte release) => phase switch
    {
        0 => "phase=begin",
        1 => "phase=sample",
        2 => release switch
        {
            1 => "phase=end;release=moving",
            2 => "phase=end;release=stopped",
            _ => "phase=end;release=unknown",
        },
        _ => "phase=p" + phase.ToString(CultureInfo.InvariantCulture),
    };

    private static string CauseName(ProbeExtentCause c) => c switch
    {
        ProbeExtentCause.Measured => "measured",
        ProbeExtentCause.FrameShift => "frame",
        ProbeExtentCause.Structural => "structural",
        _ => "unknown",
    };

    private static void WriteRow(TextWriter w, CultureInfo ci, double qpcMs, string kind,
        double? offset, double? delta, double? renderingTimeMs, string device, string note, int vp)
    {
        w.Write(qpcMs.ToString("0.000", ci));
        w.Write(',');
        w.Write("Wavee");
        w.Write(',');
        w.Write(kind);
        w.Write(',');
        w.Write(offset.HasValue ? offset.Value.ToString("0.####", ci) : "");
        w.Write(',');
        w.Write(delta.HasValue ? delta.Value.ToString("0.####", ci) : "");
        w.Write(',');
        w.Write(renderingTimeMs.HasValue ? renderingTimeMs.Value.ToString("0.###", ci) : "");
        w.Write(',');
        w.Write(device);
        w.Write(',');
        w.Write(note);
        w.Write(',');
        w.WriteLine(vp >= 0 ? vp.ToString(ci) : "");
    }
}
