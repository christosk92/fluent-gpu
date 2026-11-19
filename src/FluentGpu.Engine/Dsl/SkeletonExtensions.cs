namespace FluentGpu.Dsl;

/// <summary>Author sugar for tuning how a node is skeletonized (read by <c>SkeletonDeriver</c> during a region's
/// pending→loaded derivation). Pure record <c>with</c>-copies — construction-time metadata only, no scene cost.</summary>
public static class SkeletonExtensions
{
    /// <summary>Opt this node out of (or back into) shimmer derivation. <c>Skeletonized(false)</c> ⇒ the deriver emits a
    /// same-sized empty spacer (keeps the layout slot, no shimmer bar) — e.g. a column you want blank while loading.</summary>
    public static T Skeletonized<T>(this T e, bool on) where T : Element
        => (T)((Element)e with { SkeletonMode = on ? SkeletonMode.Auto : SkeletonMode.Off });

    /// <summary>Substitute a bespoke shimmer subtree for this node during derivation (overrides the auto-map) — for a
    /// node whose default-derived bar isn't the right placeholder.</summary>
    public static T Skel<T>(this T e, Element customShimmer) where T : Element
        => (T)((Element)e with { SkeletonOverride = customShimmer });

    /// <summary>Stand this node in the shimmer as <paramref name="live"/>, a part the page already knows (a cover a card staged), and keep it in
    /// place through the region's reveal: everything around it reveals, it does not. A connected flight lands on it on the click frame and hands
    /// off to the real node (the one carrying the same MorphId) without a second arrival.</summary>
    public static T SkelHeld<T>(this T e, Element live) where T : Element
        => (T)((Element)e with { SkeletonOverride = live with { SkeletonMode = SkeletonMode.Held } });
}
