namespace FluentGpu.Dsl;

/// <summary>Author sugar for tuning how a node is skeletonized (read by <c>SkeletonDeriver</c> during a region's
/// pending→loaded derivation). Pure record <c>with</c>-copies — construction-time metadata only, no scene cost.</summary>
public static class SkeletonExtensions
{
    /// <summary>Opt this node out of (or back into) shimmer derivation. <c>Skeletonized(false)</c> ⇒ the deriver emits a
    /// same-sized empty spacer (keeps the layout slot, no shimmer bar) — e.g. a column you want blank while loading.</summary>
    public static Element Skeletonized(this Element e, bool on)
        => e with { SkeletonMode = on ? SkeletonMode.Auto : SkeletonMode.Off };

    /// <summary>Substitute a bespoke shimmer subtree for this node during derivation (overrides the auto-map) — for a
    /// node whose default-derived bar isn't the right placeholder.</summary>
    public static Element Skel(this Element e, Element customShimmer)
        => e with { SkeletonOverride = customShimmer };
}
