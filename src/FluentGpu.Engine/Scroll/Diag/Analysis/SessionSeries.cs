using System;
using System.Collections.Generic;

namespace FluentGpu.Scroll.Diag.Analysis;

/// <summary>One scroll input as the analysis sees it: <see cref="T"/> is session seconds (QPC, relative to the
/// session origin); <see cref="Phase"/> is the probe's gesture byte (<see cref="ScrollProbe.PhaseNotch"/> = a detented
/// notch, 0/1/2 = Begin/Sample/End of a contact stream); <see cref="Dx"/>/<see cref="Dy"/> are notch units for a notch
/// and DIP for a sample, positive toward the content end.</summary>
public readonly record struct InputSample(double T, ScrollSourceCode Source, byte Phase, float Dx, float Dy)
{
    /// <summary>The contact-stream End phase byte (mirrors <c>ScrollGesture.End</c>).</summary>
    public const byte PhaseEnd = 2;

    public bool IsNotch => Phase == ScrollProbe.PhaseNotch;
    public bool IsEnd => Phase == PhaseEnd;
    /// <summary>The signed amount along the dominant axis (Dy unless only Dx is set).</summary>
    public float Amount => Dy != 0f ? Dy : Dx;
}

/// <summary>One posed frame: <see cref="T"/> is the pose's PRESENT time (session seconds), <see cref="Pos"/> the
/// position actually shown (after the coverage clamp), <see cref="Vel"/> the plan's velocity (DIP/s).</summary>
public readonly record struct PoseSample(double T, double Pos, double Vel = 0.0, bool Clamped = false);

/// <summary>One frame's present-truth sample (<see cref="ScrollProbe.Present"/>): the ledger counters are CUMULATIVE,
/// the DWM counts are the deltas of DWM sample <see cref="DwmSeq"/> (fresh on the first row carrying a new value; 0 =
/// no sample identity). <see cref="PairedLedger"/> false marks a recording made before the paired-counter ledger, whose
/// <see cref="PresentsDropped"/> is the lagging-sample artefact and whose <see cref="VblanksRepeated"/> includes idle and
/// producer-skipped vblanks — neither a display attestation.</summary>
public readonly record struct PresentSample(double T, long PresentsDisplayed, long PresentsDropped, long VblanksRepeated,
    uint DwmDropped = 0, uint DwmMissed = 0, uint DwmLate = 0, double LatencyWaitMs = 0.0, double RefreshIntervalMs = 0.0,
    bool Presented = true, byte DwmSeq = 0, bool PairedLedger = true);

/// <summary>One render-thread present (<see cref="ScrollProbe.Turn"/>): <see cref="T"/> is the compositor tick it was
/// decided for (session seconds), <see cref="MissedTicks"/> the ticks it charged to the missed-motion-tick counter, and
/// where the turn's time went — <see cref="WakeLagMs"/> (tick → turn start), <see cref="SlotWaitMs"/> (present-slot
/// wait), <see cref="WorkMs"/> (record + submit + back-buffer fence + Present). <see cref="Fresh"/> = a fresh UI
/// publication (false = a motion re-present); <see cref="Paced"/> = decided on the display clock.</summary>
public readonly record struct TurnSample(double T, long TickSeq, int MissedTicks, double WakeLagMs, double SlotWaitMs,
    double WorkMs, bool Fresh, bool Paced = true)
{
    /// <summary>Tick → present returned (ms): how long the turn held the render loop.</summary>
    public double SpanMs => WakeLagMs + SlotWaitMs + WorkMs;
}

/// <summary>The render thread's CUMULATIVE missed-motion-tick counter as one UI frame read it (the lab's frame log) —
/// the per-frame fallback of <see cref="TurnSample.MissedTicks"/> for recordings without Turn rows.</summary>
public readonly record struct PacingSample(double T, long MissedMotionTicks);

/// <summary>One authored plan (<see cref="ScrollProbe.Plan"/>): <see cref="T"/> is the plan's ANCHOR time — the start
/// of its first segment (for a wheel notch: <c>max(notch stamp, pose floor)</c>) — in session seconds;
/// <see cref="Kind"/> the <c>MotionKind</c> byte; <see cref="Start"/>/<see cref="Dest"/> its start position and
/// destination.</summary>
public readonly record struct PlanSample(double T, byte Kind, double Start, double Dest)
{
    /// <summary><c>MotionKind.Wheel</c>'s byte.</summary>
    public const byte KindWheel = 1;
    public bool IsWheel => Kind == KindWheel;
}

/// <summary>A marker (<see cref="ProbeMark"/>) at session second <see cref="T"/>.</summary>
public readonly record struct MarkerSample(double T, ProbeMark Code);

/// <summary>
/// The analysis view of one recorded scroll session (scroll-lab E4): time-sorted input, pose, present and marker
/// series on ONE clock (QPC seconds relative to the session origin), for one viewport. Built from drained probe rows
/// (<see cref="FromRows"/>) or directly from sample arrays (synthetic series in tests). Pure data + lookups; every metric
/// lives in <see cref="ScrollMetrics"/>.
/// </summary>
public sealed class SessionSeries
{
    public SessionSeries(InputSample[] inputs, PoseSample[] poses, PresentSample[]? presents = null,
                         MarkerSample[]? markers = null, double refreshPeriodS = 0.0, PlanSample[]? plans = null,
                         TurnSample[]? turns = null, PacingSample[]? pacing = null)
    {
        Turns = turns is null ? Array.Empty<TurnSample>() : (TurnSample[])turns.Clone();
        Array.Sort(Turns, static (a, b) => a.T.CompareTo(b.T));
        Pacing = pacing is null ? Array.Empty<PacingSample>() : (PacingSample[])pacing.Clone();
        Array.Sort(Pacing, static (a, b) => a.T.CompareTo(b.T));
        Plans = plans is null ? Array.Empty<PlanSample>() : (PlanSample[])plans.Clone();
        Array.Sort(Plans, static (a, b) => a.T.CompareTo(b.T));
        Inputs = inputs is null ? Array.Empty<InputSample>() : (InputSample[])inputs.Clone();
        Array.Sort(Inputs, static (a, b) => a.T.CompareTo(b.T));
        // One Pose row per present (only the render poser records them), so the series is just the sorted rows.
        Poses = poses is null ? Array.Empty<PoseSample>() : (PoseSample[])poses.Clone();
        Array.Sort(Poses, static (a, b) => a.T.CompareTo(b.T));
        Presents = presents is null ? Array.Empty<PresentSample>() : (PresentSample[])presents.Clone();
        Array.Sort(Presents, static (a, b) => a.T.CompareTo(b.T));
        Markers = markers is null ? Array.Empty<MarkerSample>() : (MarkerSample[])markers.Clone();
        Array.Sort(Markers, static (a, b) => a.T.CompareTo(b.T));
        RefreshPeriodS = refreshPeriodS > 0.0 ? refreshPeriodS : DeriveRefreshPeriod(Presents, Poses);
    }

    public InputSample[] Inputs { get; }
    public PoseSample[] Poses { get; }
    public PresentSample[] Presents { get; }
    public MarkerSample[] Markers { get; }
    /// <summary>The viewport's authored plans (Trace-level recordings only; empty otherwise), sorted by anchor time.</summary>
    public PlanSample[] Plans { get; }
    /// <summary>Every render-thread present (empty for recordings made before Turn rows, or without a render thread).</summary>
    public TurnSample[] Turns { get; }
    /// <summary>The missed-motion-tick counter per UI frame (the lab's frame log; empty when none was recorded).</summary>
    public PacingSample[] Pacing { get; }

    /// <summary>The display refresh period (s): the median recorded refresh interval, else the median pose spacing,
    /// else 1/60.</summary>
    public double RefreshPeriodS { get; }

    /// <summary>The viewport node the poses belong to (−1 when built from sample arrays).</summary>
    public int Viewport { get; init; } = -1;

    /// <summary>Records the drain lost to ring overwrite (0 = a complete trace).</summary>
    public long LostRows { get; init; }

    /// <summary>Session length (s) spanned by every series.</summary>
    public double DurationS
    {
        get
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            void Widen(double t) { if (t < lo) lo = t; if (t > hi) hi = t; }
            if (Inputs.Length > 0) { Widen(Inputs[0].T); Widen(Inputs[^1].T); }
            if (Poses.Length > 0) { Widen(Poses[0].T); Widen(Poses[^1].T); }
            if (Presents.Length > 0) { Widen(Presents[0].T); Widen(Presents[^1].T); }
            return hi > lo ? hi - lo : 0.0;
        }
    }

    /// <summary>Builds the series from drained probe rows. <paramref name="originQpc"/> is the session's t = 0;
    /// input rows with a zero stamp (no device time) are dropped; <paramref name="viewport"/> −1 picks the viewport with
    /// the most MOVING poses. Markers come from the UI rows' Mark records only — the probe ring is their one source.</summary>
    /// <remarks><paramref name="pacing"/> is the per-UI-frame missed-motion-tick counter (already in session seconds) —
    /// the lab's frame log; Turn rows come from <paramref name="render"/>.</remarks>
    public static SessionSeries FromRows(ReadOnlySpan<ProbeRow> ui, ReadOnlySpan<ProbeRow> render, long originQpc,
                                         double qpcFrequency, int viewport = -1, long lostRows = 0, PacingSample[]? pacing = null)
    {
        if (!(qpcFrequency > 0.0)) qpcFrequency = System.Diagnostics.Stopwatch.Frequency;
        double Sec(long qpc) => (qpc - originQpc) / qpcFrequency;

        if (viewport < 0) viewport = PickViewport(render);

        var inputs = new List<InputSample>();
        var presents = new List<PresentSample>();
        var markers = new List<MarkerSample>();
        for (int i = 0; i < ui.Length; i++)
        {
            ref readonly ProbeRow r = ref ui[i];
            switch (r.Kind)
            {
                case ProbeRowKind.Input:
                    if (r.Qpc != 0) inputs.Add(new InputSample(Sec(r.Qpc), r.Source, r.Phase, r.Dx, r.Dy));
                    break;
                case ProbeRowKind.Present:
                    presents.Add(new PresentSample(Sec(r.Qpc), r.PresentsDisplayed, r.PresentsDropped, r.VblanksRepeated,
                        r.DwmDropped, r.DwmMissed, r.DwmLate, r.LatencyWaitMs, r.RefreshIntervalMs, r.Presented,
                        r.DwmSeq, r.PairedLedger));
                    break;
                case ProbeRowKind.Mark:
                    markers.Add(new MarkerSample(Sec(r.Qpc), r.MarkCode));
                    break;
            }
        }

        var poses = new List<PoseSample>();
        var turns = new List<TurnSample>();
        for (int i = 0; i < render.Length; i++)
        {
            ref readonly ProbeRow r = ref render[i];
            if (r.Kind == ProbeRowKind.Turn)
            {
                turns.Add(new TurnSample(Sec(r.Qpc), r.TickSeq, r.MissedTicks, r.WakeLagMs, r.SlotWaitMs, r.WorkMs, r.TurnFresh, r.TurnPaced));
                continue;
            }
            if (r.Kind != ProbeRowKind.Pose || r.Vp != viewport) continue;
            poses.Add(new PoseSample(Sec(r.Qpc), r.Pos, r.Vel, r.Clamped));
        }

        var plans = new List<PlanSample>();
        for (int i = 0; i < ui.Length; i++)
        {
            ref readonly ProbeRow r = ref ui[i];
            if (r.Kind != ProbeRowKind.Plan || r.Vp != viewport) continue;
            plans.Add(new PlanSample(Sec(r.Qpc), r.PlanKind, r.D0, r.D1));
        }

        return new SessionSeries(inputs.ToArray(), poses.ToArray(), presents.ToArray(), markers.ToArray(), plans: plans.ToArray(),
                                 turns: turns.ToArray(), pacing: pacing)
        {
            Viewport = viewport,
            LostRows = lostRows,
        };
    }

    // ── lookups ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Index of the first pose with <c>T ≥ t</c>, or <c>Poses.Length</c>.</summary>
    public int FirstPoseAtOrAfter(double t)
    {
        int lo = 0, hi = Poses.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Poses[mid].T < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Index of the last pose with <c>T ≤ t</c>, or −1.</summary>
    public int LastPoseAtOrBefore(double t)
    {
        int lo = 0, hi = Poses.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Poses[mid].T <= t) lo = mid + 1; else hi = mid;
        }
        return lo - 1;
    }

    /// <summary>The shown position at <paramref name="t"/>, linearly interpolated between the bracketing poses and held
    /// flat beyond either end. NaN when there are no poses.</summary>
    public double PosAt(double t)
    {
        if (Poses.Length == 0) return double.NaN;
        int k = FirstPoseAtOrAfter(t);
        if (k <= 0) return Poses[0].Pos;
        if (k >= Poses.Length) return Poses[^1].Pos;
        var a = Poses[k - 1];
        var b = Poses[k];
        double span = b.T - a.T;
        if (!(span > 0.0)) return b.Pos;
        return a.Pos + (b.Pos - a.Pos) * ((t - a.T) / span);
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────

    private static int PickViewport(ReadOnlySpan<ProbeRow> render)
    {
        var moving = new Dictionary<int, int>();
        var total = new Dictionary<int, int>();
        var last = new Dictionary<int, double>();
        for (int i = 0; i < render.Length; i++)
        {
            ref readonly ProbeRow r = ref render[i];
            if (r.Kind != ProbeRowKind.Pose) continue;
            total[r.Vp] = total.TryGetValue(r.Vp, out int n) ? n + 1 : 1;
            if (last.TryGetValue(r.Vp, out double prev) && Math.Abs(r.Pos - prev) > ScrollMetrics.MotionEpsilonDip)
                moving[r.Vp] = moving.TryGetValue(r.Vp, out int m) ? m + 1 : 1;
            last[r.Vp] = r.Pos;
        }
        int best = -1, bestMoving = -1, bestTotal = -1;
        foreach (var kv in total)
        {
            int mv = moving.TryGetValue(kv.Key, out int m) ? m : 0;
            if (mv > bestMoving || (mv == bestMoving && kv.Value > bestTotal))
            {
                best = kv.Key; bestMoving = mv; bestTotal = kv.Value;
            }
        }
        return best;
    }

    private static double DeriveRefreshPeriod(PresentSample[] presents, PoseSample[] poses)
    {
        var xs = new List<double>();
        for (int i = 0; i < presents.Length; i++)
            if (presents[i].RefreshIntervalMs > 0.5 && presents[i].RefreshIntervalMs < 100.0) xs.Add(presents[i].RefreshIntervalMs * 1e-3);
        if (xs.Count > 0) return ScrollMetrics.Median(xs);
        for (int i = 1; i < poses.Length; i++)
        {
            double dt = poses[i].T - poses[i - 1].T;
            if (dt > 0.001 && dt < 0.1) xs.Add(dt);
        }
        return xs.Count > 0 ? ScrollMetrics.Median(xs) : 1.0 / 60.0;
    }
}
