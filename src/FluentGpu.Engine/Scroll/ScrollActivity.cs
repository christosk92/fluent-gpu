namespace FluentGpu.Scroll;

/// <summary>
/// Scroll v3 kernel — WP-A (<c>docs/plans/scroll-v3-plan-2026-08-17.md</c> §2). This is the pinned public surface
/// every other work package (host wiring, the input router, the render lease) compiles against. Everything in
/// <c>FluentGpu.Scroll</c> is portable (no <c>FluentGpu.Scene</c>, no <c>NodeHandle</c>, no delegate that touches the
/// scene) so the kernel is tickable from a non-UI thread — that is the whole point of the render-thread fling lease
/// (§6): a POD slab driven by one command port and one output sink.
/// </summary>
/// <remarks>Flutter's activity names (Idle/Drag/Ballistic/Driven); overscroll is a property (Band ≠ 0), not a state —
/// a body can be Ballistic AND banding (a fling that has hit the edge, coasting into the rubber-band) in the same
/// tick, which is exactly why <see cref="ScrollActivityFlags.Banding"/> is a flag, not a fifth enum value.</remarks>
public enum ScrollActivity : byte
{
    Idle = 0,
    Drag = 1,
    Ballistic = 2,
    Driven = 3,
}

[System.Flags]
public enum ScrollActivityFlags : byte
{
    None = 0,
    /// <summary>Driven by ScrollTo/ScrollBy glide — excluded from "user active".</summary>
    Programmatic = 2,
    /// <summary>Driven by a WheelNotch — hard stop at extents, never bands.</summary>
    Wheel = 4,
    /// <summary>Moved by a child's excess THIS frame (drag-time chaining, §2.2).</summary>
    Chained = 8,
    /// <summary>Band ≠ 0 while a contact is live (rubber-band pull, as opposed to a released spring).</summary>
    Banding = 16,
    /// <summary>Band spring settling with no live contact (Bounce activity).</summary>
    Bouncing = 32,
    /// <summary>Driven by SetVelocity — drag-edge autoscroll / marquee (off += v·dt, no chase target).</summary>
    Autoscroll = 64,
    // NOTE: no OsOwned flag and no Momentum* inputs — precision-touchpad inertia is engine-owned by user decision;
    // DM only ever lifts RUNNING→READY into a ContactEnd. See plan §2.1 note under ScrollActivityFlags.
}

/// <summary>Which axes/fields a <see cref="ScrollWrite"/> actually changed this tick — lets a sink skip unaffected
/// work (e.g. a bind evaluator that only cares about the main axis, or a chrome table that ignores Zoom).</summary>
[System.Flags]
public enum ScrollWriteMask : byte
{
    None = 0,
    OffsetX = 1,
    OffsetY = 2,
    BandX = 4,
    BandY = 8,
    Zoom = 16,
}

/// <summary>Who produced a <see cref="ScrollWrite"/> this frame (§2.3's diagnostics contract) — Tick (the phase-2.5
/// per-frame integration), Reclamp (a structural command applied after layout, §3.3), or Lease (the render-thread
/// fling lease, §6; reserved here, written by WP-L in Phase 6).</summary>
/// <remarks><b>Naming note (deviation from the literal §2.1 pin):</b> <c>FluentGpu.Foundation.ScrollTrace</c>
/// (<c>src/FluentGpu.Engine/Foundation/ScrollTrace.cs</c> ~:76-80) ALREADY declares a public <c>ScrollWriter</c> enum
/// (<c>Direct=0, Integrator=1</c>) for the v2 single-writer gate. WP-F owns that file and retires it as part of the
/// Scroll v3 cut-over (§2.3), but until that lands, a same-named enum in this namespace would collide the moment any
/// file <c>using</c>s both <c>FluentGpu.Scroll</c> and <c>FluentGpu.Foundation</c> (ambiguous-reference, not just a
/// style nit). Per the WP-A task brief, this kernel's writer-tag enum is named <see cref="ScrollWriteSource"/>
/// instead — same three values (<c>Tick=1, Reclamp=2, Lease=3</c>), same field name (<c>ScrollWrite.Writer</c>) — so
/// every OTHER pinned name/shape in §2.1 is unchanged. WP-F should delete the Foundation enum and this remark once
/// the rename is no longer needed for disambiguation.</remarks>
public enum ScrollWriteSource : byte
{
    Tick = 1,
    Reclamp = 2,
    Lease = 3,
}
