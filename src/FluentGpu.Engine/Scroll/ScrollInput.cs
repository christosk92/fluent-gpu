namespace FluentGpu.Scroll;

/// <summary>The verb of a <see cref="ScrollInput"/> command. Device-blind: the router (WP-C) is the ONLY thing that
/// knows whether a body's motion started on a finger, a touchpad, a wheel, or a call to <c>ScrollController</c> — by
/// the time a command reaches the kernel it is one of these 17 shapes. See plan §2.1/§2.2.</summary>
public enum ScrollInputKind : byte
{
    Bind, Unbind, Park, SetFrame, SetZoom, Chain, Cancel,
    ContactBegin, ContactMove, ContactEnd,   // real-time samples (touch/pen): T = qpc sec, A = axis pos DIP
    FrameDelta,                               // frame-aligned delta (DM RUNNING / hi-res fallback): A = delta DIP — no resampling
    WheelNotch,                               // A = DIP delta (router already scaled notch→DIP)
    ScrollTo, ScrollBy,                       // A = offset/delta, B = halflifeMs (0 = distance-derived), C = zeta, D = omega, E = settleVel; Flags bit Immediate
    SetVelocity,                              // A = DIP/s (0 = stop) — edge autoscroll (time-true, replaces per-frame pokes)
    ThumbSet,                                 // A = absolute offset, immediate, Activity stays Idle
    Restore,                                  // A = x, B = y (latched until geometry can hold it)
    AnchorShift,                              // A = delta (main axis) — coordinate-frame shift; rebases every intent
}

[System.Flags]
public enum ScrollInputFlags : byte
{
    None = 0,
    /// <summary>ScrollTo/ScrollBy: apply verbatim this tick, no spring (scrollbar thumb / pinch focal / edge
    /// auto-scroll intents). Also makes the command STRUCTURAL — drained by <see cref="ScrollKernel.Reclamp"/> as
    /// well as <see cref="ScrollKernel.Tick"/> (plan §3.3 point 3's "an Immediate ScrollTo from a layout effect
    /// lands this frame" case). Park: bit0 doubles as the "parked" boolean (Park has no other flag use).</summary>
    Immediate = 1,
}

/// <summary>A viewport's geometry + snap/zoom configuration, copied verbatim into the owning <see cref="ScrollBody"/>
/// on <see cref="ScrollInputKind.SetFrame"/>. "Main"/"Cross" are already orientation-resolved (main = the scroll
/// axis), so kernel physics never branches on <see cref="Orientation"/> except to pick OffsetX vs OffsetY.</summary>
public readonly record struct ScrollFrameSpec(byte Orientation, float ExtentMain, float ExtentCross, float ViewportMain, float ViewportCross,
    float Zoom, bool ContentSized, float SnapInterval, float SnapStart, float SnapEnd, float[]? SnapPoints);

/// <summary>One POD scroll command — 40-ish bytes, no managed references except the shared immutable
/// <see cref="SnapPoints"/> array (owned by the caller, never mutated by the kernel). Posted into a
/// <see cref="ScrollCommandPort"/>; producers use the static factories below rather than the raw constructor so the
/// per-<see cref="ScrollInputKind"/> field layout stays documented in one place.</summary>
public readonly record struct ScrollInput(ScrollInputKind Kind, int Node, double T,
    float A = 0f, float B = 0f, float C = 0f, float D = 0f, float E = 0f, int I = -1, byte Flags = 0, float[]? SnapPoints = null)
{
    public static ScrollInput Bind(int node) => new(ScrollInputKind.Bind, node, 0.0);
    public static ScrollInput Unbind(int node) => new(ScrollInputKind.Unbind, node, 0.0);
    public static ScrollInput Park(int node, bool parked) => new(ScrollInputKind.Park, node, 0.0, Flags: parked ? (byte)ScrollInputFlags.Immediate : (byte)0);

    public static ScrollInput ContactBegin(int node, double tSec, float axisPos) => new(ScrollInputKind.ContactBegin, node, tSec, A: axisPos);
    public static ScrollInput ContactMove(int node, double tSec, float axisPos) => new(ScrollInputKind.ContactMove, node, tSec, A: axisPos);
    public static ScrollInput ContactEnd(int node, double tSec, float axisPos) => new(ScrollInputKind.ContactEnd, node, tSec, A: axisPos);

    public static ScrollInput FrameDelta(int node, double tSec, float delta) => new(ScrollInputKind.FrameDelta, node, tSec, A: delta);

    public static ScrollInput WheelNotch(int node, double tSec, float dipDelta) => new(ScrollInputKind.WheelNotch, node, tSec, A: dipDelta);

    public static ScrollInput ScrollTo(int node, float offset, bool immediate = false, float halflifeMs = 0f, float zeta = 0f, float omega = 0f, float settleVel = 0f)
        => new(ScrollInputKind.ScrollTo, node, 0.0, A: offset, B: halflifeMs, C: zeta, D: omega, E: settleVel, Flags: immediate ? (byte)ScrollInputFlags.Immediate : (byte)0);

    public static ScrollInput ScrollBy(int node, float delta, bool immediate = false, float halflifeMs = 0f, float zeta = 0f, float omega = 0f, float settleVel = 0f)
        => new(ScrollInputKind.ScrollBy, node, 0.0, A: delta, B: halflifeMs, C: zeta, D: omega, E: settleVel, Flags: immediate ? (byte)ScrollInputFlags.Immediate : (byte)0);

    public static ScrollInput SetVelocity(int node, float dipPerS) => new(ScrollInputKind.SetVelocity, node, 0.0, A: dipPerS);

    public static ScrollInput ThumbSet(int node, float absoluteOffset) => new(ScrollInputKind.ThumbSet, node, 0.0, A: absoluteOffset);

    public static ScrollInput Restore(int node, float x, float y) => new(ScrollInputKind.Restore, node, 0.0, A: x, B: y);

    public static ScrollInput AnchorShift(int node, float delta) => new(ScrollInputKind.AnchorShift, node, 0.0, A: delta);

    public static ScrollInput SetZoom(int node, float zoom, float focalOffset) => new(ScrollInputKind.SetZoom, node, 0.0, A: zoom, B: focalOffset);

    public static ScrollInput Chain(int node, int parent) => new(ScrollInputKind.Chain, node, 0.0, I: parent);

    public static ScrollInput Cancel(int node) => new(ScrollInputKind.Cancel, node, 0.0);

    /// <summary>Pack a <see cref="ScrollFrameSpec"/> into one <see cref="ScrollInput"/>. Internal wire format (NOT
    /// part of the pinned §2.1 surface — only the factory's shape and <see cref="ScrollFrameSpec"/> itself are
    /// pinned): <c>A..E</c> carry <c>ExtentMain, ExtentCross, ViewportMain, ViewportCross, Zoom</c> exactly (full
    /// float precision — these need it most, they gate every clamp); <c>Flags</c> bit0 = Orientation, bit1 =
    /// ContentSized; <c>SnapPoints</c> carries the array reference as-is (already a POD reference, no packing
    /// needed); <c>SnapInterval</c>/<c>SnapStart</c> are bit-packed losslessly into the otherwise-unused <c>T</c>
    /// double (hi32/lo32 via <see cref="System.BitConverter.SingleToInt32Bits(float)"/> — SetFrame carries no real
    /// gesture timestamp, so T is free), and <c>SnapEnd</c> into <c>I</c> the same way (int and float are both 4
    /// bytes). <see cref="ScrollKernel"/> reverses this exactly in <c>UnpackFrame</c> — no precision is lost.</summary>
    public static ScrollInput SetFrame(int node, in ScrollFrameSpec spec)
    {
        byte flags = 0;
        if (spec.Orientation != 0) flags |= 1;
        if (spec.ContentSized) flags |= 2;
        long hi = (long)(uint)System.BitConverter.SingleToInt32Bits(spec.SnapInterval) << 32;
        long lo = (uint)System.BitConverter.SingleToInt32Bits(spec.SnapStart);
        double packedT = System.BitConverter.Int64BitsToDouble(hi | lo);
        int packedI = System.BitConverter.SingleToInt32Bits(spec.SnapEnd);
        return new ScrollInput(ScrollInputKind.SetFrame, node, packedT,
            A: spec.ExtentMain, B: spec.ExtentCross, C: spec.ViewportMain, D: spec.ViewportCross, E: spec.Zoom,
            I: packedI, Flags: flags, SnapPoints: spec.SnapPoints);
    }

    /// <summary>The exact inverse of <see cref="SetFrame(int, in ScrollFrameSpec)"/>'s packing.</summary>
    public static ScrollFrameSpec UnpackFrame(in ScrollInput input)
    {
        long bits = System.BitConverter.DoubleToInt64Bits(input.T);
        float snapInterval = System.BitConverter.Int32BitsToSingle((int)(bits >> 32));
        float snapStart = System.BitConverter.Int32BitsToSingle((int)(bits & 0xFFFFFFFFL));
        float snapEnd = System.BitConverter.Int32BitsToSingle(input.I);
        byte orientation = (byte)(input.Flags & 1);
        bool contentSized = (input.Flags & 2) != 0;
        return new ScrollFrameSpec(orientation, input.A, input.B, input.C, input.D, input.E, contentSized, snapInterval, snapStart, snapEnd, input.SnapPoints);
    }
}
