namespace FluentGpu.Scroll.Motion;

/// <summary>What a contact's PRODUCER knows about the fingers at the lift — carried on the
/// <c>ScrollInputEvent.Release</c> of an End and handed to <see cref="PlanAuthor.FollowEnd"/>. A producer that can see
/// the release decision (DirectManipulation: RUNNING→INERTIA is a moving release, RUNNING→READY a stopped one) says so;
/// every other stream says <see cref="Unknown"/> and the engine judges the lift by time
/// (<see cref="PlanAuthor.StoppedAfterS"/>).</summary>
public enum ContactRelease : byte
{
    /// <summary>No verdict: the stopped-finger TIME rule decides (a lift more than
    /// <see cref="PlanAuthor.StoppedAfterS"/> after the newest sample carries no momentum). Touch, pen, the touchpad
    /// wheel fallback, and a DirectManipulation lift that ended in neither INERTIA nor READY.</summary>
    Unknown = 0,

    /// <summary>Released MOVING (DirectManipulation went RUNNING→INERTIA): the release velocity is the contact ring's
    /// least-squares velocity at its newest sample, decayed over the time from that sample to the End.</summary>
    Moving = 1,

    /// <summary>Released at REST (DirectManipulation went RUNNING→READY): no momentum — the plan holds where the contact
    /// shows (a lift past an edge still springs back).</summary>
    Stopped = 2,
}
