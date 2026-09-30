using FluentGpu.Foundation;

namespace FluentGpu.Scroll.Runtime;

/// <summary>What physically produced a scroll input (design B.2). Byte values mirror
/// <see cref="FluentGpu.Scroll.Diag.ScrollSourceCode"/> one-to-one so a probe call can cast between them.</summary>
public enum ScrollSource : byte
{
    /// <summary>A detented mouse wheel: whole ±120 packets, WinUI cubic per notch.</summary>
    MouseWheel = 0,
    /// <summary>A free-spin / high-resolution wheel (sub-notch packets): FRACTIONAL notches — each packet is a
    /// <see cref="ScrollGesture.Notch"/> of <c>raw/120</c> notches on the same accumulating cubic a detented notch rides
    /// (WinUI InteractionTracker / Chromium / Firefox smooth-wheel). A mouse wheel has no lift, so it never coasts.</summary>
    MouseWheelHiRes = 1,
    /// <summary>A precision touchpad pan (positively identified by the OS, or DirectManipulation's contact stream).</summary>
    Touchpad = 2,
    Touch = 3,
    Pen = 4,
    Keyboard = 5,
    /// <summary>A scrollbar thumb drag.</summary>
    Thumb = 6,
    /// <summary>An app-initiated move (<c>ScrollTo</c>/<c>BringIntoView</c>).</summary>
    Programmatic = 7,
}

/// <summary>Gesture phase of a <see cref="ScrollInputEvent"/> (design B.2). Contact sources run
/// <see cref="Begin"/> → <see cref="Sample"/>* → <see cref="End"/>; a detented wheel emits discrete
/// <see cref="Notch"/> events with no begin/end.</summary>
public enum ScrollGesture : byte
{
    Begin = 0,
    Sample = 1,
    End = 2,
    /// <summary>A discrete wheel notch. Its ordinal (3) is <see cref="FluentGpu.Scroll.Diag.ScrollProbe.PhaseNotch"/>.</summary>
    Notch = 3,
}

/// <summary>The one scroll input event shape (design B.2) that replaces the old Wheel/ScrollBegin/ScrollDelta/ScrollEnd
/// quartet. <see cref="Dx"/>/<see cref="Dy"/> are notch units for <see cref="ScrollGesture.Notch"/> and DIP for a
/// <see cref="ScrollGesture.Sample"/>; sign convention: positive = toward the content end (offset increases).</summary>
public readonly record struct ScrollInputEvent(
    ScrollSource Source,
    ScrollGesture Phase,
    long Qpc,
    Point2 PointerDip,
    float Dx,
    float Dy,
    uint PointerId,
    KeyModifiers Mods)
{
    /// <summary>An <see cref="ScrollGesture.End"/> DETECTED after the lift itself: a packet-silence fallback (the
    /// touchpad wheel stream outside DirectManipulation) only knows the contact ended after its silence window, and
    /// stamps <see cref="Qpc"/> at the last packet — the true lift. The dispatcher authors such a release from what is
    /// shown at the detection instant (<c>ScrollHandle.ContactEnd(t, detectedAt)</c>). False for an End delivered at
    /// the lift (DirectManipulation, touch, pen).</summary>
    public bool LiftDetectedLate { get; init; }

    /// <summary>The stream is COMPOSITION-timed: <see cref="Qpc"/> is a PRESENT time on the frame clock, not when a
    /// device reported it — DirectManipulation's touchpad output (it evaluates the manipulation once per produced frame),
    /// stamped <see cref="ContactStamp.ForFrame"/>: the present of the first render turn guaranteed to see the sample
    /// (the next tick's). Such a contact runs on <see cref="FluentGpu.Scroll.Motion.ContactClock.Present"/>: its stamps
    /// are placed on the plan clock as they are (the frame clock's domain) and its plan never predicts past the newest
    /// sample. False for a device-timed stream (touch, pen, the touchpad wheel fallback). Set on every event of such a
    /// stream (Begin/Sample/End).</summary>
    public bool PresentTimed { get; init; }

    /// <summary>When the producer OBSERVED the event (QPC ticks, <see cref="System.Diagnostics.Stopwatch"/> domain);
    /// 0 ⇒ the same as <see cref="Qpc"/>. A device-timed event's <see cref="Qpc"/> already is that time, so producers
    /// leave this 0. A composition-timed event (<see cref="PresentTimed"/>) carries a PRESENT stamp in <see cref="Qpc"/>
    /// — a future time, one tick past the present the producing frame lands on (<see cref="ContactStamp.ForFrame"/>) — so
    /// input-to-photon latency (the probe's Input row, <c>ScrollMetrics.FirstMotionLatency</c>) is measured from this
    /// instead. Diagnostics only: the plan never reads it.</summary>
    public long ArrivalQpc { get; init; }

    /// <summary>On an <see cref="ScrollGesture.End"/>: the producer's verdict on the fingers at the lift
    /// (<see cref="FluentGpu.Scroll.Motion.ContactRelease"/>). DirectManipulation reports it (RUNNING→INERTIA =
    /// <c>Moving</c>, RUNNING→READY = <c>Stopped</c>); a stream that cannot see it leaves <c>Unknown</c> and the engine's
    /// time rule decides (<c>PlanAuthor.StoppedAfterS</c>). Ignored on every other phase.</summary>
    public FluentGpu.Scroll.Motion.ContactRelease Release { get; init; }
}

/// <summary>What the OS could tell us about the device behind a wheel packet (the PAL's
/// <c>WheelSourceEvidenceOf</c>/<c>PointerKindOf</c> distilled to three states).</summary>
public enum WheelDeviceEvidence : byte
{
    /// <summary>The OS could not positively resolve the source.</summary>
    Unknown = 0,
    /// <summary>PT_TOUCHPAD / DM touchpad evidence — authoritative.</summary>
    PrecisionTouchpad = 1,
    /// <summary>A positively identified physical mouse.</summary>
    Mouse = 2,
}

/// <summary>
/// Pure wheel-packet classifier (design B.2): decides whether a packet belongs to a detented mouse wheel, a free-spin
/// hi-res wheel or a precision touchpad, and LATCHES that verdict for the rest of the gesture so a stray packet
/// mid-stream cannot flip the motion model under the user. Carries the three priority rules of the PAL's
/// <c>HandlePointerWheel</c> verbatim: (1) authoritative touchpad evidence ⇒ <see cref="ScrollSource.Touchpad"/>;
/// (2) a sub-notch delta (not an exact multiple of <see cref="DeltaPerNotch"/>) ⇒ <see cref="ScrollSource.MouseWheelHiRes"/>;
/// (3) otherwise ⇒ <see cref="ScrollSource.MouseWheel"/>. Rules 1/2 only ever escalate within a gesture (never
/// de-escalate); a packet gap above <see cref="GestureGapMs"/> ends the gesture and re-evaluates from scratch.
/// A value type the producer keeps per window — no allocation, no clock of its own (the caller passes the gap).
/// </summary>
public struct WheelClassifier
{
    /// <summary>Packet silence (ms) that ends a wheel gesture and clears the latch — the PAL's <c>WheelGestureGapMs</c>.</summary>
    public const double GestureGapMs = 200.0;

    /// <summary>Raw wheel units per detent (Win32 <c>WHEEL_DELTA</c>).</summary>
    public const int DeltaPerNotch = 120;

    private byte _latch;   // 0 = none, 1 = hi-res, 2 = touchpad
    private bool _mouseSeen;

    /// <summary>The verdict latched for the current gesture, or <see cref="ScrollSource.MouseWheel"/> when none.</summary>
    public ScrollSource Latched => _latch switch { 2 => ScrollSource.Touchpad, 1 => ScrollSource.MouseWheelHiRes, _ => ScrollSource.MouseWheel };

    /// <summary>True while a gesture is latched hi-res or touchpad.</summary>
    public bool IsLatched => _latch != 0;

    /// <summary>Clears the gesture latch (what a gap above <see cref="GestureGapMs"/> does implicitly).</summary>
    public void Reset()
    {
        _latch = 0;
        _mouseSeen = false;
    }

    /// <summary>Classifies one packet. <paramref name="rawDelta"/> is the signed raw wheel delta (±120 per detent);
    /// <paramref name="gapMs"/> is the time since the previous packet (any value above <see cref="GestureGapMs"/>
    /// starts a fresh gesture — pass <c>double.PositiveInfinity</c> for the first packet ever).</summary>
    public ScrollSource Classify(int rawDelta, WheelDeviceEvidence evidence, double gapMs)
    {
        if (gapMs > GestureGapMs) Reset();

        if (evidence == WheelDeviceEvidence.PrecisionTouchpad)
            _latch = 2;                                        // rule 1: authoritative, wins over everything
        else if (evidence == WheelDeviceEvidence.Mouse)
            _mouseSeen = true;

        bool subNotch = rawDelta != 0 && (rawDelta % DeltaPerNotch) != 0;
        if (subNotch && _latch == 0)
            _latch = 1;                                        // rule 2: sub-notch granularity ⇒ hi-res (never back)

        // A positively identified mouse never reads as a touchpad even if a later packet in the same gesture claims
        // touchpad evidence — the OS-resolved device is authoritative once seen (the PAL's TryStopForPhysicalWheel rule).
        if (_latch == 2 && _mouseSeen && evidence != WheelDeviceEvidence.PrecisionTouchpad)
            _latch = 1;

        return Latched;                                        // rule 3: MouseWheel when nothing latched
    }

    /// <summary>Whole notches carried by <paramref name="rawDelta"/> for a detented wheel (truncates toward zero;
    /// the caller keeps the signed remainder in <paramref name="accum"/> across packets — the PAL's carryover).</summary>
    public static int Carryover(ref int accum, int rawDelta)
    {
        accum += rawDelta;
        int whole = accum / DeltaPerNotch;
        accum -= whole * DeltaPerNotch;
        return whole;
    }

    /// <summary>Ctrl + a hi-res/touchpad stream is the OS's legacy pinch synthesis — consumed, never scrolled.</summary>
    public static bool IsPinchSynthesis(ScrollSource source, KeyModifiers mods)
        => (mods & KeyModifiers.Ctrl) != 0 && source is ScrollSource.MouseWheelHiRes or ScrollSource.Touchpad;

    /// <summary>One hi-res (free-spin) wheel packet as the fractional notch it is: <c>raw/120</c> notches, a
    /// <see cref="ScrollGesture.Notch"/> of <see cref="ScrollSource.MouseWheelHiRes"/> (vertical: +raw = away from the
    /// user = toward the content START, so negated; horizontal: +raw = right = toward the content end).</summary>
    public static ScrollInputEvent HiResNotch(int rawDelta, bool horizontal, long qpc, Point2 pointerDip, uint pointerId, KeyModifiers mods)
    {
        float units = rawDelta / (float)DeltaPerNotch;
        return new ScrollInputEvent(ScrollSource.MouseWheelHiRes, ScrollGesture.Notch, qpc, pointerDip,
            horizontal ? units : 0f, horizontal ? 0f : -units, pointerId, mods);
    }

    /// <summary>A detented wheel's whole notches as one <see cref="ScrollGesture.Notch"/> of
    /// <see cref="ScrollSource.MouseWheel"/>. <paramref name="wholeV"/>/<paramref name="wholeH"/> are the whole device
    /// notches crossed (<see cref="Carryover"/>, WHEEL_DELTA convention: vertical +1 = rotated AWAY from the user, horizontal
    /// +1 = right); <paramref name="axisMul"/> scales them by the system wheel-lines setting. The event's sign convention is
    /// "positive = toward the content end", so a vertical notch is negated (away = toward the content start) and a
    /// horizontal one is not.</summary>
    public static ScrollInputEvent DetentNotch(int wholeV, int wholeH, float axisMul, long qpc, Point2 pointerDip, uint pointerId, KeyModifiers mods)
        => new(ScrollSource.MouseWheel, ScrollGesture.Notch, qpc, pointerDip, wholeH * axisMul, -wholeV * axisMul, pointerId, mods);

    /// <summary>The app-zoom notch count a Ctrl+wheel event carries in the <c>InputHooks.ZoomWheel</c> convention —
    /// <c>&gt;0 = the wheel rotated AWAY from the user = zoom in</c> (the browser Ctrl+wheel direction). Only a VERTICAL
    /// wheel zooms (a tilt has no away/toward); 0 for a horizontal-only event. The event's vertical axis is "positive =
    /// toward the content end" (<see cref="DetentNotch"/> negated the device sign), so the zoom count is its negation.</summary>
    public static float ZoomNotches(in ScrollInputEvent e) => e.Dy == 0f ? 0f : -e.Dy;

    /// <summary>One touchpad-evidenced wheel packet (a precision touchpad outside DirectManipulation — e.g. over a
    /// popup) as a contact <see cref="ScrollGesture.Sample"/> in DIP: <c>raw/120 × <paramref name="notchDip"/></c> (the feel's
    /// <c>TouchpadWheelDip</c> — a finger's distance, deliberately not the mouse notch). Shift turns a vertical pan sideways (the wheel convention: the vertical sign
    /// carries over — wheel-down/away-from-content-start scrolls right).</summary>
    public static ScrollInputEvent TouchpadWheelSample(int rawDelta, bool horizontal, double notchDip, long qpc, Point2 pointerDip,
                                                       uint pointerId, KeyModifiers mods)
    {
        float dip = rawDelta / (float)DeltaPerNotch * (float)notchDip;
        float dx, dy;
        if (horizontal) { dx = dip; dy = 0f; }
        else if ((mods & KeyModifiers.Shift) != 0) { dx = -dip; dy = 0f; }
        else { dx = 0f; dy = -dip; }
        return new ScrollInputEvent(ScrollSource.Touchpad, ScrollGesture.Sample, qpc, pointerDip, dx, dy, pointerId, mods);
    }
}
