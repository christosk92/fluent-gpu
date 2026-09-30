using System;
using System.Diagnostics;
using System.Reflection;
using FluentGpu.Controls;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Rhi;
using FluentGpu.ScrollLab.Record;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.ScrollLab.Lab;

/// <summary>
/// The lab's attachment to the live engine: <see cref="Attach"/> (from <c>FluentApp.DiagnosticRun</c>, returning
/// false so the interactive loop keeps running) keeps the host/window/device for the session metadata and arms the
/// probe; <see cref="OnFrame"/> (the <c>FluentApp.FrameCompleted</c> tap, UI thread, inside the frame) drains BOTH probe
/// rings incrementally into a fixed scratch span — feeding the HUD tracker every frame and the
/// <see cref="SessionRecorder"/> while a session runs — then publishes the HUD signals, throttled to 20 Hz while the
/// content moves so the lab does not re-render (and perturb) every frame it is measuring.
/// </summary>
public static class LabHost
{
    private static AppHost? s_host;
    private static IPlatformWindow? s_window;
    private static IGpuDevice? s_device;

    private static readonly ProbeRow[] s_scratch = new ProbeRow[4096];
    private static long s_uiCursor = -1, s_renderCursor = -1;
    private static readonly HudTracker s_hud = new();
    private static long s_lastHudQpc, s_lastStripQpc;
    private static ulong s_lastPlanSeq = ulong.MaxValue;
    private static double s_lastPlanDest;
    private static long s_lost;
    private static readonly GpuPassTiming[] s_passes = new GpuPassTiming[GpuPassTimeline.MaxPasses];
    private static readonly float[] s_kindMs = new float[FrameLog.KindCount];
    private static ulong s_lastPassSeq;
    private static long s_lastPassHudQpc;

    public static AppHost? Host => s_host;

    public static void Attach(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        s_host = host;
        s_window = window;
        s_device = device;
        // The HUD needs notches + poses (Summary keeps both); a session raises it to its own level while it runs.
        if (ScrollProbe.Level == ProbeLevel.Off) ScrollProbe.Level = ProbeLevel.Summary;
        SyncCursors();
    }

    /// <summary>The per-frame tap.</summary>
    public static void OnFrame(FrameStats stats)
    {
        Drain();
        SampleFrame(in stats);
        PublishHud();
    }

    /// <summary>Turns the pass-granular GPU timeline on/off (<see cref="AppHost.GpuPassTimingEnabled"/> — a runtime
    /// toggle, never an environment variable).</summary>
    public static void SetGpuPasses(bool on)
    {
        if (s_host is { } host) host.GpuPassTimingEnabled = on;
        LabState.GpuPasses.Value = on;
        if (!on) LabState.HudGpuPasses.Value = "off";
    }

    /// <summary>Per frame: the freshest retired GPU pass timeline (while enabled; summed per <see cref="GpuPassKind"/>),
    /// and — while a session runs — the frame's census + pacing + pass summary into the session's frame log.</summary>
    private static void SampleFrame(in FrameStats stats)
    {
        var host = s_host;
        if (host is null) return;
        long now = Stopwatch.GetTimestamp();
        GpuPassFrameSummary summary = default;
        bool fresh = false;
        if (LabState.GpuPasses.Peek())
        {
            int n = host.CopyGpuPassTimeline(s_passes, out summary);
            if (n > 0 && summary.Sequence != s_lastPassSeq)
            {
                fresh = true;
                s_lastPassSeq = summary.Sequence;
                Array.Clear(s_kindMs);
                for (int i = 0; i < n; i++) s_kindMs[(int)s_passes[i].Kind] += s_passes[i].Ms;
                if (now - s_lastPassHudQpc >= Stopwatch.Frequency / 4)
                {
                    s_lastPassHudQpc = now;
                    LabState.HudGpuPasses.Value = FormatPasses(in summary, s_kindMs);
                }
            }
        }
        var rec = LabState.Recorder;
        if (rec.IsRecording)
            rec.AppendFrame(now, in stats, host.RenderPace, in summary, fresh ? s_kindMs : ReadOnlySpan<float>.Empty);
    }

    /// <summary>"3.42 ms · 12 passes — Scene 2.10 · Clear 0.41 · Uploads 0.20 · …" (the four largest kinds).</summary>
    public static string FormatPasses(in GpuPassFrameSummary summary, ReadOnlySpan<float> kindMs)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(96);
        sb.Append(summary.WholeMs.ToString("0.00", ci)).Append(" ms · ").Append(summary.PassCount.ToString(ci)).Append(" passes");
        Span<bool> used = stackalloc bool[FrameLog.KindCount];
        for (int shown = 0; shown < 4; shown++)
        {
            int best = -1;
            for (int k = 0; k < kindMs.Length && k < used.Length; k++)
                if (!used[k] && kindMs[k] > 0f && (best < 0 || kindMs[k] > kindMs[best])) best = k;
            if (best < 0) break;
            used[best] = true;
            sb.Append(shown == 0 ? " — " : " · ").Append(((GpuPassKind)best).ToString()).Append(' ').Append(kindMs[best].ToString("0.00", ci));
        }
        return sb.ToString();
    }

    /// <summary>Drains everything written since the last drain (also called by record start/stop so a session's edges
    /// are exact).</summary>
    public static void Drain()
    {
        SyncCursors();
        var rec = LabState.Recorder;
        var handle = LabState.ActiveHandle.Peek();
        int vp = handle is { IsBound: true } ? handle.Vp.Node : -1;

        while (true)
        {
            long from = s_uiCursor;
            int n = ScrollProbe.ReadUi(from, s_scratch, out long next);
            long lost = next - n - from;
            s_uiCursor = next;
            ReadOnlySpan<ProbeRow> rows = s_scratch.AsSpan(0, n);
            s_hud.OnUi(rows);
            if (rec.IsRecording)
            {
                rec.AppendUi(rows);
                if (lost > 0) rec.NoteLost(lost, 0);
            }
            if (lost > 0) s_lost += lost;
            if (n < s_scratch.Length) break;
        }
        while (true)
        {
            long from = s_renderCursor;
            int n = ScrollProbe.ReadRender(from, s_scratch, out long next);
            long lost = next - n - from;
            s_renderCursor = next;
            ReadOnlySpan<ProbeRow> rows = s_scratch.AsSpan(0, n);
            s_hud.OnRender(rows, vp);
            if (rec.IsRecording)
            {
                rec.AppendRender(rows);
                if (lost > 0) rec.NoteLost(0, lost);
            }
            if (lost > 0) s_lost += lost;
            if (n < s_scratch.Length) break;
        }
    }

    /// <summary>The host facts a session folder records.</summary>
    public static LabMeta CaptureMeta()
    {
        string engine = typeof(ScrollProbe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return new LabMeta(
            DpiScale: s_window?.Scale ?? 1f,
            MaxFrameLatency: s_device?.MaxFrameLatency ?? 1,
            EngineVersion: engine,
            OsBuild: Environment.OSVersion.Version.ToString(),
            Monitor: "");
    }

    private static void SyncCursors()
    {
        if (s_uiCursor >= 0) return;
        s_uiCursor = ScrollProbe.UiWriteCount;
        s_renderCursor = ScrollProbe.RenderWriteCount;
    }

    private static void PublishHud()
    {
        var handle = LabState.ActiveHandle.Peek();
        long now = Stopwatch.GetTimestamp();
        bool moving = false;
        if (handle is { IsBound: true })
        {
            // DIP per notch: the destination step of each new wheel plan over the notches that authored it.
            var plan = handle.Plan;
            if (plan.Seq != s_lastPlanSeq)
            {
                if (plan.Kind == MotionKind.Wheel && s_hud.NotchesSincePlan > 0.0)
                {
                    LabState.HudDipPerNotch.Value = Math.Abs(plan.Dest - s_lastPlanDest) / s_hud.NotchesSincePlan;
                    s_hud.NotchesSincePlan = 0.0;
                }
                s_lastPlanSeq = plan.Seq;
                s_lastPlanDest = plan.Dest;
            }
            var motion = handle.Motion.Peek();
            moving = motion.IsMoving;
            // 20 Hz while moving; the final (idle) values land at once.
            if (moving && now - s_lastHudQpc < Stopwatch.Frequency / 20) return;
            s_lastHudQpc = now;
            LabState.HudOffset.Value = Math.Round(handle.Offset.Peek(), 1);
            LabState.HudVelocity.Value = Math.Round(motion.SpeedDipPerS);
            LabState.HudKind.Value = motion.Kind;
        }
        LabState.HudClamps.Value = s_hud.Clamps;
        LabState.HudLagMs.Value = s_hud.LagMs;
        LabState.HudNotches.Value = s_hud.Notches;
        LabState.HudLostRows.Value = s_lost;
        if (s_hud.StripDirty && (!moving || now - s_lastStripQpc >= Stopwatch.Frequency / 5))
        {
            s_lastStripQpc = now;
            LabState.HudPoseStrip.Value = s_hud.TakeStrip();
        }
    }

    /// <summary>The HUD's incremental probe readers: last notch→pose latency, coverage clamps, and the recent
    /// per-frame displacement strip (a fixed ring; a strip model is allocated only when published, ≤ 5 Hz).</summary>
    private sealed class HudTracker
    {
        private const int StripLength = 48;
        private readonly float[] _strip = new float[StripLength];
        private int _stripHead, _stripCount;
        private long _lastNotchQpc;
        private bool _awaitingPose;
        private double _lastPos;
        private bool _hasPose;

        public double LagMs = double.NaN;
        public int Clamps;
        public int Notches;
        public double NotchesSincePlan;
        public bool StripDirty;

        public void OnUi(ReadOnlySpan<ProbeRow> rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                ref readonly ProbeRow r = ref rows[i];
                if (!r.IsNotch) continue;
                _lastNotchQpc = r.Qpc != 0 ? r.Qpc : Stopwatch.GetTimestamp();
                _awaitingPose = true;
                Notches++;
                NotchesSincePlan += Math.Abs(r.Dy != 0f ? r.Dy : r.Dx);
            }
        }

        public void OnRender(ReadOnlySpan<ProbeRow> rows, int vp)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                ref readonly ProbeRow r = ref rows[i];
                if (r.Kind != ProbeRowKind.Pose || (vp >= 0 && r.Vp != vp)) continue;
                if (_awaitingPose && r.Qpc >= _lastNotchQpc)
                {
                    LagMs = Math.Round((r.Qpc - _lastNotchQpc) * 1000.0 / Stopwatch.Frequency, 1);
                    _awaitingPose = false;
                }
                if (r.Clamped) Clamps++;
                if (_hasPose)   // one Pose row per present (the render poser is the only recorder)
                {
                    _strip[_stripHead] = (float)Math.Abs(r.Pos - _lastPos);
                    _stripHead = (_stripHead + 1) % StripLength;
                    if (_stripCount < StripLength) _stripCount++;
                    StripDirty = true;
                }
                _lastPos = r.Pos;
                _hasPose = true;
            }
        }

        public SparkBarsModel TakeStrip()
        {
            StripDirty = false;
            var bars = new SparkBar[_stripCount];
            int start = (_stripHead - _stripCount + StripLength) % StripLength;
            for (int i = 0; i < _stripCount; i++)
            {
                float v = _strip[(start + i) % StripLength];
                bars[i] = new SparkBar(v, "", Accent: i == _stripCount - 1);
            }
            return new SparkBarsModel(bars);
        }
    }
}
