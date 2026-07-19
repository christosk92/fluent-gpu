using System.Diagnostics;
using System.Globalization;

namespace FluentGpu.Hosting;

/// <summary>
/// One bit per term of <see cref="AppHost.HasActiveWork"/>: the reason(s) the frame loop refused to idle this frame.
/// The bit order mirrors the OR-chain in <c>AppHost.ComputeWakeReasons</c> (AppHost.cs) one-to-one.
/// </summary>
[Flags]
public enum WakeReasons
{
    None = 0,
    FrameNeeded = 1 << 0,       // _frameNeeded (input/resize/explicit wake pending)
    RuntimePending = 1 << 1,    // _runtime.HasPending (scheduled render-effects)
    DynamicText = 1 << 2,       // _scene.HasDynamicText (FPS/draw-count HUD strings)
    Anim = 1 << 3,              // _anim.HasActive (transform/opacity/bounds tracks)
    Interact = 1 << 4,          // _interact.HasActive (eased hover/press)
    ScrollAnim = 1 << 5,        // _scrollAnim.HasActive (smooth scroll + scrollbar fade)
    Repeat = 1 << 6,            // _repeat.HasActive (RepeatButton auto-repeat)
    Caret = 1 << 7,             // _caretBlinker.HasActive (focused-editor caret blink)
    BrushAnims = 1 << 8,        // _scene.HasBrushAnims (implicit BrushTransition)
    ImagesPending = 1 << 9,     // _images.PendingCount > 0 (in-flight decodes)
    ImageCrossfades = 1 << 10,  // _images.HasActiveCrossfades (reveal fades in flight)
    Orphans = 1 << 11,          // _scene.OrphanCount > 0 (exit-animating orphans)
    DragDropWork = 1 << 12,     // _dispatcher.Drag.HasActiveWork || _dispatcher.DragDrop.HasActiveWork (E5 easing/edge-scroll)
    DragActive = 1 << 13,       // _dispatcher.Drag.IsActive (E5 reorder dwell keep-alive)
    GestureHold = 1 << 14,      // _dispatcher.HasArmedHold (§7A touch long-press timer keep-alive on a stationary held finger)
    PopupAnim = 1 << 15,        // a windowed-popup desktop-acrylic open reveal (CompositionBackdrop) is mid-animation — keep presenting so its per-frame clip inset advances to settle
    TouchPress = 1 << 16,       // delayed 100ms pressed visual for touch inside a scrollable viewport
    VideoPresenting = 1 << 17,  // a media player is actively presenting a video surface (playing / ramping to play) — keep pumping at DISPLAY rate so frames advance (VideoSurfaceRegistry.HasActivePresentation)
}

/// <summary>
/// FG_WAKE_DIAG=1: once-per-second stderr attribution of WHY the frame loop stays awake — the smoking gun behind a
/// process that never idles. Records the <see cref="WakeReasons"/> mask of every awake frame; reports per-reason
/// kept-awake counts, SOLE-reason counts (frames where exactly one bit was set — the cleanest attribution), the
/// current consecutive-awake streak, seconds since the loop last went fully idle, frames spent minimized, and a
/// reconcile/layout/record-only work split. Zero overhead when the flag is off (the host gates every call on the
/// cached <c>s_wakeDiag</c> bool). Reused buffers; the once-per-second report allocates one StringBuilder line.
/// </summary>
internal sealed class WakeDiagnostics
{
    // Per-reason awake-frame counts this window, indexed by bit position (0..ReasonCount-1).
    private const int ReasonCount = 18;
    private static readonly string[] s_reasonNames =
    [
        "frameNeeded", "runtimePending", "dynamicText", "anim", "interact", "scrollAnim", "repeat", "caret",
        "brushAnims", "imagesPending", "imageCrossfades", "orphans", "dragDropWork", "dragActive", "gestureHold",
        "popupAnim", "touchPress", "videoPresenting",
    ];

    private readonly long[] _reasonFrames = new long[ReasonCount];   // frames where reason i kept the loop awake
    private readonly long[] _soleFrames = new long[ReasonCount];     // frames where reason i was the ONLY bit set
    private long _framesRun;          // frames where RunFrame did real work (awake)
    private long _framesRendered;     // of those, frames that actually rendered (FrameStats.Rendered)
    private long _framesMinimized;    // awake frames observed while the window was minimized
    private long _reconciledFrames;   // awake frames that reconciled
    private long _layoutFrames;       // awake frames that ran layout
    private long _recordOnlyFrames;   // awake frames that neither reconciled nor laid out (compositor-only)

    private int _awakeStreak;         // consecutive awake frames (reset when the loop last saw None)
    private long _lastIdleTicks;      // timestamp of the last fully-idle observation (seconds-since-idle base)
    private long _windowStartTicks;

    /// <summary>Record one frame's wake mask + classification. <paramref name="reasons"/> is the mask the loop
    /// computed; <paramref name="awake"/> is whether the frame actually did work (a frame can run for a completed
    /// image pump with reasons==None — counted toward the streak reset, not toward an awake reason).</summary>
    public void Record(WakeReasons reasons, bool awake, bool rendered, bool reconciled, bool laidOut, bool minimized)
    {
        long now = Stopwatch.GetTimestamp();
        if (_windowStartTicks == 0) { _windowStartTicks = now; _lastIdleTicks = now; }

        if (reasons == WakeReasons.None)
        {
            _awakeStreak = 0;
            _lastIdleTicks = now;
        }
        else
        {
            _awakeStreak++;
        }

        if (!awake) return;   // image-pump-only frame: classified as idle above, no per-reason attribution

        _framesRun++;
        if (rendered) _framesRendered++;
        if (minimized) _framesMinimized++;
        // 3-way split (one bucket per frame): reconciled wins over layout-only wins over compositor-only record.
        if (reconciled) _reconciledFrames++;
        else if (laidOut) _layoutFrames++;
        else _recordOnlyFrames++;

        int bits = (int)reasons;
        int set = System.Numerics.BitOperations.PopCount((uint)bits);
        for (int i = 0; i < ReasonCount; i++)
        {
            if ((bits & (1 << i)) == 0) continue;
            _reasonFrames[i]++;
            if (set == 1) _soleFrames[i]++;
        }
    }

    /// <summary>Emit the once-per-second line if a second has elapsed, then reset the window. Cheap timestamp check
    /// otherwise. Call once per frame from the host (only when the flag is on).</summary>
    public void MaybeReport()
    {
        long now = Stopwatch.GetTimestamp();
        if (_windowStartTicks == 0) { _windowStartTicks = now; _lastIdleTicks = now; return; }
        double sec = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        if (sec < 1.0) return;

        double idleSec = (now - _lastIdleTicks) / (double)Stopwatch.Frequency;
        var sb = new System.Text.StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture,
            $"[wakediag] run {_framesRun} rendered {_framesRendered} | reconciled {_reconciledFrames} layout {_layoutFrames} recordOnly {_recordOnlyFrames}");
        sb.Append(CultureInfo.InvariantCulture, $" | streak {_awakeStreak} idleAgo {idleSec:0.0}s minimized {_framesMinimized}");

        sb.Append(" | kept:");
        for (int i = 0; i < ReasonCount; i++)
            if (_reasonFrames[i] > 0) sb.Append(CultureInfo.InvariantCulture, $" {s_reasonNames[i]}={_reasonFrames[i]}");

        sb.Append(" | sole:");
        bool anySole = false;
        for (int i = 0; i < ReasonCount; i++)
            if (_soleFrames[i] > 0) { sb.Append(CultureInfo.InvariantCulture, $" {s_reasonNames[i]}={_soleFrames[i]}"); anySole = true; }
        if (!anySole) sb.Append(" none");

        Console.Error.WriteLine(sb.ToString());

        Array.Clear(_reasonFrames);
        Array.Clear(_soleFrames);
        _framesRun = 0;
        _framesRendered = 0;
        _framesMinimized = 0;
        _reconciledFrames = 0;
        _layoutFrames = 0;
        _recordOnlyFrames = 0;
        _windowStartTicks = now;
    }
}
