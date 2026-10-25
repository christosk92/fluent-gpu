using System;

namespace FluentGpu.Hosting;

/// <summary><c>msg wm=</c>: which OS input message a <see cref="TraceKind.Msg"/> row is.</summary>
public enum TraceMsg : byte
{
    PointerDown, PointerUpdate, PointerUp, PointerWheel, PointerHWheel, DmHitTest,
    /// <summary>DirectManipulation's viewport status changed (<c>status=</c>).</summary>
    DmStatus,
    /// <summary>DirectManipulation raised a content update (its transform's x/y translation).</summary>
    DmContent,
}

// Lane SCROLL's rows of the one recorder (format: scratch/perf3/TRACE-FORMAT.md, "SCROLL kinds"): the OS input messages, the engine's
// scroll input events, the plans authored and the poses the render thread shows. Every producer is one static bool check while off.
public static partial class FrameTrace
{
    /// <summary>One OS input message (<c>msg</c>), before any engine interpretation. <paramref name="devQpc"/> = when the OS got the
    /// hardware report (POINTER_INFO.PerformanceCount; 0 = unknown), <paramref name="raw"/> = the wheel delta (×120 units) or the DM
    /// status, <paramref name="fx"/>/<paramref name="fy"/> = the contact on the DEVICE (himetric, touchpad/touch), <paramref name="frame"/>
    /// = the pointer frame id.</summary>
    public static void Msg(TraceMsg wm, uint pointerId, byte pointerType, long devQpc, int raw, int fx, int fy, uint frame,
        float a = float.NaN, float b = float.NaN)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.Msg, B0 = (byte)wm, B1 = pointerType, I0 = unchecked((int)pointerId), I1 = raw, I2 = fx, I3 = fy,
            L0 = devQpc, L1 = frame, F0 = a, F1 = b,
        };
        Push(ref e);
    }

    /// <summary>One engine scroll input event at dispatch (<c>in</c>): <paramref name="stampQpc"/> = the plan-clock time the producer
    /// gave it (DirectManipulation: the next tick's present), <paramref name="arrivalQpc"/> = when the producer observed it.</summary>
    public static void In(byte source, byte phase, long stampQpc, long arrivalQpc, float dx, float dy, bool presentTimed, bool late,
        byte release, uint pointerId)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.In, B0 = source, B1 = phase, B2 = (byte)((presentTimed ? 1 : 0) | (late ? 2 : 0)), I0 = release,
            I1 = unchecked((int)pointerId), L0 = stampQpc, L1 = arrivalQpc, F0 = dx, F1 = dy,
        };
        Push(ref e);
    }

    /// <summary>A plan authored for viewport <paramref name="vp"/> (<c>plan</c>): anchored at <paramref name="anchorQpc"/>, starting
    /// at <paramref name="start"/>, heading to <paramref name="dest"/>.</summary>
    public static void Plan(int vp, ulong seq, byte kind, long anchorQpc, double start, double dest, double durationS, double v0)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.Plan, B0 = kind, I0 = vp, L0 = (long)seq, L1 = anchorQpc,
            L2 = BitConverter.DoubleToInt64Bits(start), L3 = BitConverter.DoubleToInt64Bits(dest), F0 = (float)durationS, F1 = (float)v0,
        };
        Push(ref e);
    }

    /// <summary>Viewport <paramref name="vp"/> posed for the turn of compositor tick <paramref name="tick"/>, for the predicted present
    /// <paramref name="presentQpc"/> (<c>pose</c>): <paramref name="shown"/> = the offset shown, <paramref name="plan"/> = the
    /// pre-clamp plan position.</summary>
    public static void Pose(int vp, long tick, long presentQpc, double shown, double v, float trans, double plan, bool clamped)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.Pose, B0 = clamped ? (byte)1 : (byte)0, I0 = vp, L0 = tick, L1 = presentQpc,
            L2 = BitConverter.DoubleToInt64Bits(shown), L3 = BitConverter.DoubleToInt64Bits(plan), F0 = (float)v, F1 = trans,
        };
        Push(ref e);
    }

    /// <summary>A viewport's identity (<c>vp</c>), once and on change: its scroll key, rect (DIP), extent and viewport size.</summary>
    public static void Vp(int vp, string? key, float x, float y, float w, float h, double extent, double viewport)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.Vp, I0 = vp, Ref0 = key, F0 = x, F1 = y, F2 = w, F3 = h,
            L0 = BitConverter.DoubleToInt64Bits(extent), L1 = BitConverter.DoubleToInt64Bits(viewport),
        };
        Push(ref e);
    }

    /// <summary>DXGI's frame statistics right after a Present of the primary swapchain (<c>dxgi</c>): <paramref name="presentId"/>
    /// = this present's id (GetLastPresentCount), <paramref name="shownId"/> / <paramref name="shownVblank"/> = the last present the
    /// panel showed and its vblank ordinal (PresentCount / PresentRefreshCount), <paramref name="syncVblank"/> /
    /// <paramref name="syncQpc"/> = a vblank ordinal and its QPC (SyncRefreshCount / SyncQPCTime): the panel's own vblank lattice.</summary>
    public static void Dxgi(uint presentId, uint shownId, uint shownVblank, uint syncVblank, long syncQpc)
    {
        if (!s_enabled) return;
        var e = new TraceEvent
        {
            Kind = TraceKind.Dxgi, L0 = presentId, L1 = shownId, L2 = shownVblank, L3 = syncQpc, I0 = unchecked((int)syncVblank),
        };
        Push(ref e);
    }

    private static void WriteScroll(ref TraceLine L, in TraceEvent e)
    {
        switch (e.Kind)
        {
            case TraceKind.Dxgi:
                L.Begin(Ms(e.Qpc), "dxgi"u8);
                L.Num("id"u8, e.L0);
                L.Num("shown_id"u8, e.L1);
                L.Num("shown_vb"u8, e.L2);
                L.Num("sync_vb"u8, (uint)e.I0);
                L.At("sync"u8, e.L3);
                break;
            case TraceKind.Msg:
                L.Begin(Ms(e.Qpc), "msg"u8);
                L.Raw(" wm="u8);
                L.Raw((TraceMsg)e.B0 switch
                {
                    TraceMsg.PointerDown => "pointerdown"u8, TraceMsg.PointerUpdate => "pointerupdate"u8, TraceMsg.PointerUp => "pointerup"u8,
                    TraceMsg.PointerWheel => "pointerwheel"u8, TraceMsg.PointerHWheel => "pointerhwheel"u8, TraceMsg.DmHitTest => "dmhittest"u8,
                    TraceMsg.DmStatus => "dmstatus"u8, _ => "dmcontent"u8,
                });
                if ((TraceMsg)e.B0 is not (TraceMsg.DmStatus or TraceMsg.DmContent))
                {
                    L.Num("ptr"u8, (uint)e.I0);
                    L.Raw(" ptype="u8);
                    L.Raw(e.B1 switch { 2 => "touch"u8, 3 => "pen"u8, 4 => "mouse"u8, 5 => "touchpad"u8, _ => "other"u8 });   // POINTER_INPUT_TYPE
                    if (e.L0 != 0) L.At("dev"u8, e.L0);
                    if (e.L1 != 0) L.Num("frame"u8, e.L1);
                    if (e.I2 != 0 || e.I3 != 0) { L.Num("fx"u8, e.I2); L.Num("fy"u8, e.I3); }
                }
                if ((TraceMsg)e.B0 is TraceMsg.PointerWheel or TraceMsg.PointerHWheel) L.Num("raw"u8, e.I1);
                if ((TraceMsg)e.B0 == TraceMsg.DmStatus) L.Num("status"u8, e.I1);
                if ((TraceMsg)e.B0 == TraceMsg.DmContent) { L.Ms("x"u8, e.F0); L.Ms("y"u8, e.F1); }
                break;
            case TraceKind.In:
                L.Begin(Ms(e.Qpc), "in"u8);
                L.Raw(" src="u8);
                L.Raw(e.B0 switch
                {
                    0 => "mouse"u8, 1 => "hires"u8, 2 => "touchpad"u8, 3 => "touch"u8, 4 => "pen"u8, 5 => "key"u8, 6 => "thumb"u8, _ => "prog"u8,
                });
                L.Raw(" ph="u8);
                L.Raw(e.B1 switch { 0 => "begin"u8, 1 => "sample"u8, 2 => "end"u8, _ => "notch"u8 });
                L.Ms("dx"u8, e.F0);
                L.Ms("dy"u8, e.F1);
                L.At("stamp"u8, e.L0);
                L.At("arr"u8, e.L1);
                L.Num("pt"u8, e.B2 & 1);
                if ((e.B2 & 2) != 0) L.Num("late"u8, 1);
                if (e.B1 == 2) { L.Raw(" rel="u8); L.Raw(e.I0 switch { 1 => "moving"u8, 2 => "stopped"u8, _ => "unknown"u8 }); }
                if (e.I1 != 0) L.Num("ptr"u8, (uint)e.I1);
                break;
            case TraceKind.Plan:
                L.Begin(Ms(e.Qpc), "plan"u8);
                L.Num("vp"u8, e.I0);
                L.Num("seq"u8, e.L0);
                L.Raw(" kind="u8);
                L.Raw(e.B0 switch { 0 => "idle"u8, 1 => "wheel"u8, 2 => "drag"u8, 3 => "fling"u8, 4 => "prog"u8, 5 => "thumb"u8, _ => "other"u8 });
                L.Ms("start"u8, BitConverter.Int64BitsToDouble(e.L2));
                L.Ms("dest"u8, BitConverter.Int64BitsToDouble(e.L3));
                L.Ms("dur_ms"u8, e.F0 * 1000.0);
                L.Ms("v0"u8, e.F1);
                L.At("anchor"u8, e.L1);
                break;
            case TraceKind.Pose:
                L.Begin(Ms(e.Qpc), "pose"u8);
                L.Num("vp"u8, e.I0);
                L.Num("tick"u8, e.L0);
                L.At("present"u8, e.L1);
                L.Ms("shown"u8, BitConverter.Int64BitsToDouble(e.L2));
                L.Ms("v"u8, e.F0);
                L.Ms("trans"u8, e.F1);
                if (e.B0 != 0) { L.Ms("plan"u8, BitConverter.Int64BitsToDouble(e.L3)); L.Num("clamped"u8, 1); }
                break;
            default:   // Vp
                L.Begin(Ms(e.Qpc), "vp"u8);
                L.Num("vp"u8, e.I0);
                L.Str("key"u8, (e.Ref0 as string) ?? "-");
                L.Ms("x"u8, e.F0); L.Ms("y"u8, e.F1); L.Ms("w"u8, e.F2); L.Ms("h"u8, e.F3);
                L.Ms("extent"u8, BitConverter.Int64BitsToDouble(e.L0));
                L.Ms("viewport"u8, BitConverter.Int64BitsToDouble(e.L1));
                break;
        }
    }
}
