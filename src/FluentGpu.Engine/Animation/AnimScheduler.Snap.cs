using FluentGpu.Foundation;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  SNAP SCOPE (EP17 / E17a) — "land at the END value in the same publication".
//
//  While AppHost's SnapScope is active, `Snapping` is true and every seed of a one-shot Replace row lands at its end
//  state instead of starting a track. This is reduced-motion-as-a-value taken to its limit: ReducedSnap seeds a
//  settled 1 ms track, which still needs a tick and leaves a row for HasTracks; a snap writes the end value into the
//  scene NOW (ApplyImmediate / the side tables) and creates NO row, so the very publication that carries the commit
//  carries the end pose and `HasTracks` is already false (an Exit/overlay settle gate opens at once).
//
//  The seed primitives are the single funnel: Spring, SeedEased, Keyframes (Animate, SeedChannel, SeedTerminal,
//  SnapTo, KeyframesMotion, the brush/hover/press fades and every UseTransition/UseSpring/UseAnimate hook all reach
//  one of the three). Looping and additive rows (authored continuous motion), Driven rows (they follow a source, so
//  they are already "at the end" of that source) and the clip / stroke-trim / reveal-band / flow-reveal channels (their
//  end state has settle semantics the host snap paths own: ApplyProjections, SeedFlowReveal, BeginRevealBand) keep
//  their normal behaviour here.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    /// <summary>True while the host's <c>SnapScope</c> is active (<see cref="FluentGpu.Hosting.SnapScope"/>): seeds land at
    /// their end value with no track. Set by <c>AppHost</c> on the 0-to-1 and 1-to-0 depth edges only.</summary>
    public bool Snapping { get; internal set; }

    /// <summary>Land <paramref name="ch"/> on <paramref name="node"/> at <paramref name="to"/> with no row, cancelling a
    /// running Replace row on the channel first. False = this channel is not handled here (the caller seeds normally).</summary>
    private bool TryLandAtEnd(NodeHandle node, AnimChannel ch, float to)
    {
        switch (ch)
        {
            case AnimChannel.TranslateX:
            case AnimChannel.TranslateY:
            case AnimChannel.ScaleX:
            case AnimChannel.ScaleY:
            case AnimChannel.Rotation:
            case AnimChannel.Opacity:
            case AnimChannel.BlurSigma:
                CancelReplaceRow(node, ch);
                if (_scene.IsLive(node)) ApplyImmediate(node, ch, to);
                return true;
            case AnimChannel.BrushFade:
            case AnimChannel.HoverFade:
            case AnimChannel.PressFade:
                CancelReplaceRow(node, ch);
                if (_scene.IsLive(node)) WriteSideTable(ch, node, to);
                return true;
            case AnimChannel.SizeW:
            case AnimChannel.SizeH:
            case AnimChannel.LayoutW:
            case AnimChannel.LayoutH:
            {
                // The end state of a size / reflow seed IS the laid-out size (PresentedW/H stay NaN, LayoutInput keeps its
                // declared value), so the row is not created; one already in flight settles back to that state.
                int ex = FindReplaceRow(node, ch);
                if (ex >= 0) { SettleRestore(ex); FreeSlot(ex); }
                return true;
            }
            default:
                return false;
        }
    }

    private int FindReplaceRow(NodeHandle node, AnimChannel ch)
    {
        for (int s = _slab.HeadOnNode((int)node.Raw.Index); s >= 0; s = _slab.At(s).NextOnNode)
            if (_slab.At(s).Channel == ch && !_slab.At(s).Has(AnimFlags.Additive)) return s;
        return -1;
    }

    private void CancelReplaceRow(NodeHandle node, AnimChannel ch)
    {
        int ex = FindReplaceRow(node, ch);
        if (ex >= 0) FreeSlot(ex);
    }
}
