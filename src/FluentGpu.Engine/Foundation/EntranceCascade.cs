namespace FluentGpu.Foundation;

/// <summary>A staggered navigation entrance: the on-screen units of a page (or of any subtree) enter one after another
/// instead of as one block. WinUI's model, made an engine primitive: a container marked
/// <see cref="FluentGpu.Dsl.Element.EntranceGroup"/> is a panel whose <c>ChildrenTransitions</c> hold an
/// <c>EntranceThemeTransition</c> with <c>IsStaggeringEnabled</c>: each of its children that the viewport shows is a UNIT, and
/// the units enter down the screen (top first, then left to right), each <see cref="Enter"/> → rest on <see cref="Dynamics"/>.
/// <para><b>Who starts one.</b> A <c>Flow.KeepAlive</c> boundary whose <c>KeepAliveOptions.CascadeFor</c> answers a cascade for
/// an activation (a page swap), or a component through <c>RenderContext.EntranceCascade</c> (a pivot that re-presents part of a
/// page). Either way the cascade is resolved AFTER the frame's layout, so it knows what the viewport shows, and seeded before the
/// frame's animation tick, so its first presented frame is every unit at its terminal (or already moving).</para>
/// <para><b>The units.</b> The walk descends from the cascade's root through every node that holds a group; a group's children
/// are units, except a child that is (or holds) a group itself, which dissolves into its own children. A unit is a leaf of the
/// cascade: nothing inside it is staggered again, so no offset compounds. A unit outside the viewport (the clip of every
/// scroll viewport and <c>ClipToBounds</c> box between it and the root) is never animated; nor is one whose pose another track
/// already drives (its own mount Enter, a hover lift). A root that holds no group enters as the one unit.
/// <b>No unit is cut on its way in.</b> A child dissolves only when every unit it yields travels its terminal offset inside the
/// clips between it and the root (as far as the root itself shows it). A clip INSIDE the child that would cut one (a list's
/// viewport under its rows sliding sideways, a shelf's clip under its cards rising) keeps the child whole: the list, the shelf
/// or the section enters as one unit. Without that rule a list's rows slid 200 DIP under the list's own left edge and showed only
/// their right-hand cells until they had nearly landed.</para>
/// <para><b>The delays.</b> Unit <c>i</c> starts at <see cref="DelayMs"/> + the sum of the first <c>i</c> gaps, where the first gap
/// is <see cref="StepMs"/> and each next one is the previous × <see cref="StepDecay"/> (WinUI's stagger <c>DelayReduce</c>), never
/// later than <see cref="DelayMs"/> + <see cref="MaxDelayMs"/> (WinUI's <c>DelayCap</c>). A long page therefore cascades its head
/// and first sections and lands the rest together, and the last unit still starts early.</para>
/// <para>A POD (no closures), hot-path safe. Under reduced motion the engine snaps the transform channels and keeps the fade
/// (the LayoutTransition KeepFade default); a caller that wants no stagger then passes <see cref="StepMs"/> 0.</para></summary>
/// <param name="Enter">Each unit's terminal: where it enters FROM, relative to its authored pose (offset adds, opacity and scale
/// multiply). <c>Active</c> is implied.</param>
/// <param name="Dynamics">Each unit's dynamics (a tween with a WinUI KeySpline, typically).</param>
/// <param name="DelayMs">The first unit's start (the exit leg the entrance waits out).</param>
/// <param name="StepMs">The gap between the first and the second unit.</param>
/// <param name="StepDecay">Each later gap is the previous one × this (1 = an even stagger).</param>
/// <param name="MaxDelayMs">No unit starts later than <see cref="DelayMs"/> + this.</param>
/// <param name="MaxUnits">The units animated at most; past them a unit appears with the page (a bound for a dense viewport).</param>
/// <param name="CompositeUnits">The first this many units are composited while they enter (<c>NodePaint.CompositeLayer</c>: their
/// slide and fade are composite parameters, nothing under them re-records per frame). The rest fold inline.</param>
/// <param name="KeepInRoot">Each unit's travel is shortened to the room between it and the root's edge on the side it enters from, so
/// no unit is ever cut by the root's own edge on its way in (a sideways slide inside a page: the section nearest the page's gutter
/// moves as far as the gutter, one with room beside it the whole <see cref="Enter"/> offset). Off: the root's edge cuts what enters
/// across it (a page rising from below its own bottom edge).</param>
public readonly record struct EntranceCascade(
    EnterExit Enter,
    TransitionDynamics Dynamics,
    float DelayMs = 0f,
    float StepMs = 0f,
    float StepDecay = 1f,
    float MaxDelayMs = float.PositiveInfinity,
    int MaxUnits = 48,
    int CompositeUnits = 8,
    bool KeepInRoot = false)
{
    /// <summary>The start delay of unit <paramref name="ordinal"/> (0-based) under this cascade.</summary>
    public float DelayOf(int ordinal)
    {
        float d = 0f, gap = StepMs;
        for (int i = 0; i < ordinal && gap > 0.01f; i++) { d += gap; gap *= StepDecay; if (d >= MaxDelayMs) break; }
        return DelayMs + MathF.Min(d, MaxDelayMs);
    }
}
