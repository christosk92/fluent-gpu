namespace FluentGpu.Scroll.Runtime;

/// <summary>
/// The compact-band hysteresis rule (Wavee Home's compact facet band, docs/plans/wavee/home-redesign-implementation.md
/// §E6): a two-threshold flip so a value crossing near the boundary does not chatter — shown WHILE offset stays at or
/// above <paramref name="exitAt"/>, and shows only once offset moves strictly past <paramref name="enterAt"/>. Pure and
/// stateless (the caller owns <paramref name="prev"/>); see <c>RenderContext.UseScrollThreshold</c> for the hook that
/// drives this off a scroller's offset signal.
/// </summary>
public static class ScrollThreshold
{
    /// <summary>Next shown/hidden state. Hidden at rest: <c>prev=false</c> flips to <c>true</c> only when
    /// <c>offset &gt; enterAt</c> (strict — exactly AT the enter threshold stays hidden). Shown at rest: <c>prev=true</c>
    /// stays shown while <c>offset &gt;= exitAt</c> (inclusive — exactly AT the exit threshold stays shown) and only
    /// flips to hidden once offset drops strictly below it. <paramref name="exitAt"/> should be ≤ <paramref name="enterAt"/>
    /// for a real dead band (e.g. enter 64 / exit 56); equal thresholds degrade to a single non-hysteretic flip.</summary>
    public static bool Next(bool prev, double offset, double enterAt, double exitAt)
        => prev ? offset >= exitAt : offset > enterAt;
}
