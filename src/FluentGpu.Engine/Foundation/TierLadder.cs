namespace FluentGpu.Foundation;

/// <summary>
/// One boundary of a <see cref="TierLadder"/>: the value (a width, a height, an aspect-free scalar) at which the tier
/// index steps up, plus a direction-aware hold. The edge is crossed UPWARD (widening) once the value reaches
/// <c>At + WidenHold</c> and DOWNWARD (narrowing) once it drops below <c>At - NarrowHold</c>; between the two the
/// current tier holds. A fresh resolve (no previous tier) uses <see cref="At"/> itself, so a fresh launch at the edge
/// lands above it whichever side of the hold band the value is in. <see cref="None"/> is an absent edge (a structure that
/// draws no band there): it can never be crossed, so the tier above it is simply unreachable.
/// </summary>
/// <param name="At">The edge value. <see cref="float.NaN"/> marks an absent edge.</param>
/// <param name="NarrowHold">Extra distance below <paramref name="At"/> the value must fall before the edge is crossed downward.</param>
/// <param name="WidenHold">Extra distance above <paramref name="At"/> the value must rise before the edge is crossed upward.</param>
public readonly record struct TierEdge(float At, float NarrowHold = 0f, float WidenHold = 0f)
{
    /// <summary>An absent edge (the ladder's tier above it is unreachable).</summary>
    public static readonly TierEdge None = new(float.NaN);

    /// <summary>False for <see cref="None"/>.</summary>
    public bool IsPresent => !float.IsNaN(At);
}

/// <summary>
/// An optional aspect rule of a <see cref="TierLadder"/> (the stage-class pattern: a tall, narrow window may not use the
/// wide layouts even when its width would allow them, and a very wide one may not use the narrow ones). Applied AFTER the
/// edge walk. <see cref="PortraitBelow"/> caps the tier at <see cref="PortraitMaxTier"/> while <c>aspect = width / height</c>
/// is below it; <see cref="LandscapeAbove"/> raises the tier to at least <see cref="LandscapeMinTier"/> while the aspect is
/// above it. <see cref="Hold"/> is the aspect hysteresis: a cap or floor already in effect (the previous tier sits on its
/// side of the rule) lets go only once the aspect is <c>Hold</c> past its edge. A NaN aspect (a source with no second axis)
/// disables the rule.
/// </summary>
public readonly record struct TierAspectRule(
    float PortraitBelow, int PortraitMaxTier,
    float LandscapeAbove = float.PositiveInfinity, int LandscapeMinTier = 0,
    float Hold = 0f)
{
    /// <summary>Apply the rule to the edge-walk result <paramref name="tier"/>; <paramref name="previous"/> is the previous
    /// (post-rule) tier or -1. Pure.</summary>
    public int Apply(int tier, float aspect, int previous)
    {
        if (float.IsNaN(aspect)) return tier;
        if (tier > PortraitMaxTier)
        {
            bool capped = aspect < PortraitBelow
                          || (previous >= 0 && previous <= PortraitMaxTier && aspect < PortraitBelow + Hold);
            if (capped) tier = PortraitMaxTier;
        }
        if (tier < LandscapeMinTier)
        {
            bool lifted = aspect > LandscapeAbove
                          || (previous >= LandscapeMinTier && aspect > LandscapeAbove - Hold);
            if (lifted) tier = LandscapeMinTier;
        }
        return tier;
    }
}

/// <summary>
/// An ordered ladder of size tiers with hysteresis: <c>N</c> ascending <see cref="TierEdge"/>s define <c>N + 1</c> tier
/// indices (0 below the first edge, <c>i + 1</c> at or above edge <c>i</c>). The tier index is the caller's own enum
/// (Compact = 0 … Huge4K = 4); an absent edge (<see cref="TierEdge.None"/>) leaves its upper tier unreachable and an
/// empty ladder is always tier 0.
/// <para><see cref="Resolve(float,int)"/> is pure and allocation-free: it walks the edges from the top, the highest edge
/// that counts as crossed wins, and an edge counts as crossed by the previous tier's side of it — above it already, the
/// value must stay at or over <c>At - NarrowHold</c>; below it, the value must reach <c>At + WidenHold</c>; no previous
/// tier (-1), the value must reach <c>At</c>. The previous tier is the caller's state (a hook cell, a test loop).</para>
/// Construction validates (cold): present edges strictly ascending, holds finite and non-negative.
/// </summary>
public sealed class TierLadder
{
    private readonly TierEdge[] _edges;

    /// <summary>A ladder with no edges: always tier 0.</summary>
    public static readonly TierLadder Empty = new(ReadOnlySpan<TierEdge>.Empty);

    public TierLadder(ReadOnlySpan<TierEdge> edges, TierAspectRule? aspect = null)
    {
        _edges = edges.ToArray();
        float last = float.NegativeInfinity;
        for (int i = 0; i < _edges.Length; i++)
        {
            var e = _edges[i];
            if (!e.IsPresent) continue;
            if (!float.IsFinite(e.At)) throw new ArgumentException($"Tier edge {i} must be finite or TierEdge.None.", nameof(edges));
            if (!(e.At > last)) throw new ArgumentException($"Tier edges must be strictly ascending (edge {i} = {e.At} after {last}).", nameof(edges));
            if (!float.IsFinite(e.NarrowHold) || e.NarrowHold < 0f || !float.IsFinite(e.WidenHold) || e.WidenHold < 0f)
                throw new ArgumentException($"Tier edge {i} holds must be finite and non-negative.", nameof(edges));
            last = e.At;
        }
        if (aspect is { } rule && (rule.PortraitMaxTier < 0 || rule.PortraitMaxTier > _edges.Length || rule.LandscapeMinTier < 0 || rule.LandscapeMinTier > _edges.Length))
            throw new ArgumentException("The aspect rule's tiers must be within the ladder.", nameof(aspect));
        Aspect = aspect;
    }

    /// <summary>A ladder over <paramref name="edges"/> (ascending, <see cref="TierEdge.None"/> allowed).</summary>
    public static TierLadder Of(params TierEdge[] edges) => new(edges);

    /// <summary>As <see cref="Of(TierEdge[])"/> plus an aspect rule.</summary>
    public static TierLadder Of(TierAspectRule aspect, params TierEdge[] edges) => new(edges, aspect);

    /// <summary>The optional aspect rule.</summary>
    public TierAspectRule? Aspect { get; }

    /// <summary>The number of edges (including absent ones).</summary>
    public int EdgeCount => _edges.Length;

    /// <summary>The number of tier indices, <c>EdgeCount + 1</c> (some may be unreachable behind an absent edge).</summary>
    public int TierCount => _edges.Length + 1;

    /// <summary>Edge <paramref name="index"/> (0-based).</summary>
    public TierEdge EdgeAt(int index) => _edges[index];

    /// <summary>Resolve <paramref name="value"/> with hysteresis against the <paramref name="previous"/> tier (-1 = none).
    /// No aspect input (a rule, if any, is not applied). Pure; no allocation.</summary>
    public int Resolve(float value, int previous) => Resolve(value, float.NaN, previous);

    /// <summary>Resolve <paramref name="value"/> with hysteresis against the <paramref name="previous"/> tier (-1 = none),
    /// applying the aspect rule (if the ladder has one) to <paramref name="aspect"/> = width / height (NaN = unknown).
    /// A NaN value keeps the previous tier. Pure; no allocation.</summary>
    public int Resolve(float value, float aspect, int previous)
    {
        int n = _edges.Length;
        int p = previous < 0 ? -1 : previous > n ? n : previous;
        if (float.IsNaN(value)) return p < 0 ? 0 : p;
        int tier = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            ref readonly TierEdge e = ref _edges[i];
            if (float.IsNaN(e.At)) continue;
            bool above = p < 0 ? value >= e.At
                       : p > i ? value >= e.At - e.NarrowHold
                       : value >= e.At + e.WidenHold;
            if (above) { tier = i + 1; break; }
        }
        return Aspect is { } rule ? rule.Apply(tier, aspect, p) : tier;
    }
}
