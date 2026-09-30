using System;
using System.Diagnostics;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.ScrollLab.Analysis;
using FluentGpu.ScrollLab.Record;
using FluentGpu.ScrollLab.Surfaces;
using FluentGpu.ScrollLab.Tuning;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace FluentGpu.ScrollLab.Lab;

/// <summary>The lab's three pages (the top SelectorBar's order).</summary>
public enum LabPage : byte
{
    Surface = 0,
    Record = 1,
    Analysis = 2,
}

/// <summary>
/// The lab's whole state as signals (UI thread): navigation, the active surface and its <see cref="ScrollHandle"/>,
/// the edited feel, the recorder, the HUD readouts (written by <see cref="LabHost"/>, throttled) and the current
/// analysis — plus the actions every page and hotkey shares. Components read these through bound props or their own
/// render; nothing here is per-component, so the main window, the tuning pane and the detached tuning window all see
/// one state.
/// </summary>
public static class LabState
{
    public static readonly Signal<int> Page = new((int)LabPage.Surface);
    public static readonly Signal<int> Surface = new((int)ScrollSurfaceKind.FixedList100k);
    public static readonly Signal<ScrollHandle?> ActiveHandle = new(null);
    public static readonly Signal<bool> TuningPaneOpen = new(false);

    /// <summary>The feel being edited — always equal to what <see cref="ScrollTunables.Current"/> was last set to here.</summary>
    public static readonly Signal<MotionFeel> Feel = new(ScrollTunables.Current);
    /// <summary>The preset the ★ marks and the diff list compare against.</summary>
    public static readonly Signal<string> PresetName = new(ScrollTunables.ActiveProfileName == "Custom" ? FeelProfiles.All[0].Name : ScrollTunables.ActiveProfileName);

    public static readonly Signal<bool> Recording = new(false);
    public static readonly Signal<string> SessionName = new("session");
    /// <summary>0 = Summary, 1 = Trace (the recording probe level).</summary>
    public static readonly Signal<int> RecordLevel = new(1);
    public static readonly Signal<int> SessionsVersion = new(0);
    public static readonly Signal<AnalysisResult?> Analysis = new(null);
    public static readonly Signal<string> Status = new("F8 felt wrong · F10 record · Ctrl+T tuning pane");

    // ── HUD (LabHost writes; bound text reads) ──────────────────────────────────────────────────────────────────
    public static readonly Signal<double> HudOffset = new(0.0);
    public static readonly Signal<double> HudVelocity = new(0.0);
    public static readonly Signal<MotionKind> HudKind = new(MotionKind.Idle);
    public static readonly Signal<int> HudClamps = new(0);
    public static readonly Signal<double> HudLagMs = new(double.NaN);
    public static readonly Signal<double> HudDipPerNotch = new(double.NaN);
    public static readonly Signal<int> HudNotches = new(0);
    public static readonly Signal<long> HudLostRows = new(0);
    /// <summary>Whether the pass-granular GPU timeline is on (<see cref="LabHost.SetGpuPasses"/>).</summary>
    public static readonly Signal<bool> GpuPasses = new(false);
    /// <summary>The latest retired frame's pass timeline, summarised (≤ 4 Hz).</summary>
    public static readonly Signal<string> HudGpuPasses = new("off");
    public static readonly Signal<SparkBarsModel> HudPoseStrip = new(new SparkBarsModel(ReadOnlyMemory<SparkBar>.Empty));

    public static readonly SessionRecorder Recorder = new();

    private static IDetachedVideoWindow? s_tuningWindow;
    private static long s_lastTunableMarkQpc;

    public static ScrollSurfaceKind SurfaceKind => (ScrollSurfaceKind)Surface.Peek();

    // ── feel ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Applies an edited feel: live on the next notch/lift (<see cref="ScrollTunables.Apply"/>), fenced in the
    /// trace with a <see cref="ProbeMark.TunableChanged"/> (at most one per 100 ms while a slider drags).</summary>
    public static void ApplyFeel(in MotionFeel feel)
    {
        if (feel == Feel.Peek()) return;
        ScrollTunables.Apply(feel);
        Feel.Value = feel;
        long now = Stopwatch.GetTimestamp();
        if (now - s_lastTunableMarkQpc >= Stopwatch.Frequency / 10)
        {
            s_lastTunableMarkQpc = now;
            Mark(ProbeMark.TunableChanged);
        }
    }

    /// <summary>Applies a preset as the new base (★/diff reference) and the live feel.</summary>
    public static void ApplyPreset(string name, in MotionFeel feel)
    {
        bool builtIn = false;
        foreach (var (n, _) in FeelProfiles.All) if (n == name) { builtIn = true; break; }
        if (builtIn) ScrollTunables.ApplyProfile(name);
        else ScrollTunables.Apply(feel);
        Feel.Value = feel;
        PresetName.Value = name;
        Mark(ProbeMark.ProfileApplied);
    }

    // ── markers / recording ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A marker into the probe ring — the one source: a running session picks it up on the next drain.</summary>
    public static void Mark(ProbeMark code) => ScrollProbe.Mark(Stopwatch.GetTimestamp(), code);

    /// <summary>F8 — "that felt wrong".</summary>
    public static void MarkFelt()
    {
        Mark(ProbeMark.UserFelt);
        Status.Value = Recorder.IsRecording ? "F8 marked (" + Recorder.ElapsedS.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s)" : "F8 marked (not recording — start a session with F10 to keep it)";
    }

    /// <summary>F10.</summary>
    public static void ToggleRecording()
    {
        if (Recorder.IsRecording) StopRecording();
        else StartRecording();
    }

    public static void StartRecording()
    {
        if (Recorder.IsRecording) return;
        LabHost.Drain();   // everything before the start belongs to the HUD only
        var handle = ActiveHandle.Peek();
        int vp = handle is { IsBound: true } ? handle.Vp.Node : -1;
        var level = RecordLevel.Peek() == 0 ? ProbeLevel.Summary : ProbeLevel.Trace;
        string name = SessionName.Peek();
        Recorder.Start(string.IsNullOrWhiteSpace(name) ? "session" : name.Trim(), SurfaceKind, level, Feel.Peek(), PresetName.Peek(), vp);
        Recording.Value = true;
        Status.Value = "● Recording " + ScrollSurfaces.NameOf(SurfaceKind) + " at " + level + " — F10 to stop";
    }

    public static void StopRecording()
    {
        if (!Recorder.IsRecording) return;
        long end = Recorder.MarkEnd();
        LabHost.Drain();   // appends the end fence and everything before it
        var data = Recorder.Stop(end);
        Recording.Value = false;
        try
        {
            string dir = SessionWriter.Write(data, LabHost.CaptureMeta());
            var result = AnalysisResult.From(data, dir);
            SessionWriter.WriteMetrics(dir, result.Metrics);
            Analysis.Value = result;
            SessionsVersion.Value = SessionsVersion.Peek() + 1;
            Status.Value = "Saved " + System.IO.Path.GetFileName(dir) + " (" + (data.Ui.Length + data.Render.Length) + " rows) — see Analysis";
        }
        catch (Exception ex)
        {
            Status.Value = "Session write failed: " + ex.Message;
        }
    }

    /// <summary>Loads a saved session folder and makes it the current analysis.</summary>
    public static void AnalyzeFolder(string dir)
    {
        try
        {
            var data = SessionWriter.Load(dir);
            Analysis.Value = AnalysisResult.From(data, dir);
            Page.Value = (int)LabPage.Analysis;
            Status.Value = "Analyzed " + System.IO.Path.GetFileName(dir);
        }
        catch (Exception ex)
        {
            Status.Value = "Could not load " + System.IO.Path.GetFileName(dir) + ": " + ex.Message;
        }
    }

    // ── tuning window ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens the tuning panel in its own (detached) window; falls back to the in-window pane when the host
    /// cannot open one.</summary>
    public static void OpenTuningWindow(InputHooks hooks)
    {
        if (s_tuningWindow is { IsOpen: true }) return;
        var request = new DetachedWindowRequest("Scroll Lab — Tuning", new Size2(640f, 860f), new TuningWindowRoot(),
            AlwaysOnTop: false, MinClientSizeDip: new Size2(520f, 400f));
        // The host the lab attached to is the authority (DiagnosticRun); the context seam is the fallback.
        s_tuningWindow = LabHost.Host is { } host ? host.OpenDetachedWindow(request)
                       : hooks.OpenDetachedWindow is { } open && (hooks.CanOpenDetachedWindow?.Invoke() ?? true) ? open(request) : null;
        if (s_tuningWindow is { } w)
        {
            w.OnClosed = static () => s_tuningWindow = null;
            Console.Error.WriteLine("[scroll-lab] tuning window opened");
        }
        else
        {
            TuningPaneOpen.Value = true;
            Status.Value = "Detached windows are unavailable here — opened the tuning pane instead";
            Console.Error.WriteLine("[scroll-lab] tuning window unavailable — opened the in-window pane");
        }
    }
}
