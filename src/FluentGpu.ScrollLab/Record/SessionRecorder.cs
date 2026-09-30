using System;
using System.Diagnostics;
using FluentGpu.ScrollLab.Surfaces;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Diag.Analysis;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.ScrollLab.Record;

/// <summary>One finished recording: every probe row drained between start and stop (UI ring: input, plan, extent,
/// cost, present and the session's markers; render ring: poses and the render thread's per-present Turn rows), plus
/// what was being tested.</summary>
public sealed class SessionData
{
    public required string Name { get; init; }
    public required ScrollSurfaceKind Surface { get; init; }
    public required ProbeLevel Level { get; init; }
    public required long StartQpc { get; init; }
    public required long EndQpc { get; init; }
    public required DateTime StartedLocal { get; init; }
    public required ProbeRow[] Ui { get; init; }
    public required ProbeRow[] Render { get; init; }
    public long LostUi { get; init; }
    public long LostRender { get; init; }
    public required MotionFeel Feel { get; init; }
    public required string PresetBase { get; init; }
    /// <summary>The surface's viewport node at start (−1 = pick the most-moving one).</summary>
    public int Viewport { get; init; } = -1;
    public double QpcFrequency { get; init; } = Stopwatch.Frequency;
    /// <summary>One sample per UI frame (render census + pacing + GPU pass summary) — see <see cref="FrameSample"/>.</summary>
    public FrameSample[] Frames { get; init; } = Array.Empty<FrameSample>();
    /// <summary><see cref="FrameLog.KindCount"/> per-pass-kind ms per frame, parallel to <see cref="Frames"/>.</summary>
    public float[] FramePassMs { get; init; } = Array.Empty<float>();

    public double DurationS => (EndQpc - StartQpc) / QpcFrequency;

    /// <summary>The analysis series: the start viewport when it posed during the session (a surface that re-bound its
    /// handle — the edge cases — falls back to the most-moving viewport).</summary>
    public SessionSeries ToSeries()
    {
        int vp = Viewport;
        if (vp >= 0)
        {
            bool posed = false;
            for (int i = 0; i < Render.Length && !posed; i++) posed = Render[i].Kind == ProbeRowKind.Pose && Render[i].Vp == vp;
            if (!posed) vp = -1;
        }
        // The frame log's missed-motion-tick counter is the per-UI-frame fallback for recordings without Turn rows.
        var pacing = new PacingSample[Frames.Length];
        for (int i = 0; i < Frames.Length; i++)
            pacing[i] = new PacingSample((Frames[i].Qpc - StartQpc) / QpcFrequency, Frames[i].MissedMotionTicks);
        return SessionSeries.FromRows(Ui, Render, StartQpc, QpcFrequency, vp, lostRows: LostUi + LostRender, pacing: pacing);
    }
}

/// <summary>
/// The lab's session recorder (scroll-lab E3 consumer): <see cref="LabHost"/> drains both probe rings every frame and,
/// while a session runs, appends the rows here into growable buffers (doubling — a 10 s Trace session at 240 Hz is a few
/// thousand rows). Markers have ONE source, the probe ring (<c>ScrollProbe.Mark</c> records at Summary and Trace): the
/// session's own fences (<see cref="ProbeMark.SessionStart"/>/<see cref="ProbeMark.SessionEnd"/>) are ring marks too, and
/// the caller drains after each so they land in the rows like every other record. UI thread only.
/// </summary>
public sealed class SessionRecorder
{
    private ProbeRow[] _ui = new ProbeRow[1 << 14];
    private int _uiCount;
    private ProbeRow[] _render = new ProbeRow[1 << 15];
    private int _renderCount;
    private long _lostUi, _lostRender;
    private readonly FrameLog _frames = new();

    private string _name = "";
    private ScrollSurfaceKind _surface;
    private ProbeLevel _level, _prevLevel;
    private DateTime _started;
    private MotionFeel _feel;
    private string _preset = "";
    private int _viewport = -1;

    public bool IsRecording { get; private set; }
    public long StartQpc { get; private set; }

    public double ElapsedS => IsRecording ? (Stopwatch.GetTimestamp() - StartQpc) / (double)Stopwatch.Frequency : 0.0;

    public void Start(string name, ScrollSurfaceKind surface, ProbeLevel level, in MotionFeel feel, string presetBase, int viewport)
    {
        if (IsRecording) return;
        _uiCount = 0;
        _renderCount = 0;
        _frames.Clear();
        _lostUi = 0;
        _lostRender = 0;
        _name = name;
        _surface = surface;
        _level = level;
        _feel = feel;
        _preset = presetBase;
        _viewport = viewport;
        _started = DateTime.Now;
        _prevLevel = ScrollProbe.Level;
        ScrollProbe.Level = level;
        StartQpc = Stopwatch.GetTimestamp();
        IsRecording = true;
        ScrollProbe.Mark(StartQpc, ProbeMark.SessionStart);   // the next drain appends it
    }

    /// <summary>Appends drained UI-ring rows (markers included — the ring is their one source).</summary>
    public void AppendUi(ReadOnlySpan<ProbeRow> rows)
    {
        if (!IsRecording) return;
        for (int i = 0; i < rows.Length; i++) Push(ref _ui, ref _uiCount, in rows[i]);
    }

    public void AppendRender(ReadOnlySpan<ProbeRow> rows)
    {
        if (!IsRecording) return;
        for (int i = 0; i < rows.Length; i++) Push(ref _render, ref _renderCount, in rows[i]);
    }

    /// <summary>Appends one UI frame's telemetry (<see cref="FrameLog.Append"/>).</summary>
    public void AppendFrame(long qpc, in Hosting.FrameStats stats, in Hosting.RenderPaceSnapshot pace,
                            in Rhi.GpuPassFrameSummary passes, ReadOnlySpan<float> kindMs)
    {
        if (IsRecording) _frames.Append(qpc, in stats, in pace, in passes, kindMs);
    }

    /// <summary>Records overwritten before the drain reached them (the ring outran the frame loop).</summary>
    public void NoteLost(long ui, long render)
    {
        _lostUi += ui;
        _lostRender += render;
    }

    /// <summary>Stamps the end fence into the ring. The caller then drains (so the fence and everything before it are
    /// appended) and calls <see cref="Stop"/> with the returned stamp.</summary>
    public long MarkEnd()
    {
        long end = Stopwatch.GetTimestamp();
        ScrollProbe.Mark(end, ProbeMark.SessionEnd);
        return end;
    }

    /// <summary>Ends the session (after <see cref="MarkEnd"/> and a final drain) and returns its data; restores the
    /// probe level.</summary>
    public SessionData Stop(long end)
    {
        IsRecording = false;
        ScrollProbe.Level = _prevLevel;
        return new SessionData
        {
            Name = _name,
            Surface = _surface,
            Level = _level,
            StartQpc = StartQpc,
            EndQpc = end,
            StartedLocal = _started,
            Ui = _ui.AsSpan(0, _uiCount).ToArray(),
            Render = _render.AsSpan(0, _renderCount).ToArray(),
            LostUi = _lostUi,
            LostRender = _lostRender,
            Feel = _feel,
            PresetBase = _preset,
            Viewport = _viewport,
            Frames = _frames.Samples(),
            FramePassMs = _frames.PassMs(),
        };
    }

    private static void Push(ref ProbeRow[] buffer, ref int count, in ProbeRow row)
    {
        if (count == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
        buffer[count++] = row;
    }
}
