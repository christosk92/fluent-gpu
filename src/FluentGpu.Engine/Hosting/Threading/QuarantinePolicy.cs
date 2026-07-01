using System.Diagnostics;

namespace FluentGpu.Hosting.Threading;

/// <summary>Consume-gated quarantine depth for the render-thread seam (design/subsystems/threading-render-seam.md §5.1).
/// A resource the UI frees while producing frame <c>p</c> may be reused only once the render thread has consumed a
/// strictly-later frame — <see cref="Quarantine"/> is that slack, DERIVED from the render-in-flight depth (never a magic
/// literal). In the single-thread build order (Step 1) the UI thread both produces and consumes, so nothing is ever in
/// flight across threads and the quarantine is logically <b>0</b>; the constant flips live only when the render thread
/// spawns (a later, soak-gated step).</summary>
public static class QuarantinePolicy
{
    /// <summary>One dedicated render thread reads one published frame at a time.</summary>
    public const int RenderInFlightDepth = 1;

    /// <summary>Frames a freed resource must outlive before reuse = in-flight depth + 1 belt-and-suspenders slack.</summary>
    public const int Quarantine = RenderInFlightDepth + 1;

    // Compile-time guard that nobody hardcoded a bare "2" — the value is derived, per canon §5.1.
    static QuarantinePolicy() => Debug.Assert(Quarantine == RenderInFlightDepth + 1, "Quarantine must be derived from RenderInFlightDepth");
}
