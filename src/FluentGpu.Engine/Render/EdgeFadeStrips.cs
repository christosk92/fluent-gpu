using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Pure geometry for the PURE-edge-fade "strip snapshot + write-through lerp restore" path — TerraFX-free so the
/// headless gates can call it (same role <see cref="EdgeFadeLayerClear"/> and <see cref="SelfBlurRegion"/> play for the
/// legacy paths).
///
/// <para><b>Why strips exist.</b> The legacy edge fade leases a FULL-CANVAS group RT, re-renders the subtree into it and
/// composites it back through a fullscreen triangle whose pixel shader is an IDENTITY copy everywhere outside the fade
/// bands. For a fade with no blur and group alpha 1, the only pixels that differ from drawing the subtree STRAIGHT onto
/// the target are the ones inside a band (or under an active corner arc). So the backend can: snapshot those band
/// pixels (<c>D</c>) before the subtree, let the subtree draw direct, snapshot them again (<c>F</c>) after, and write
/// through <c>lerp(D, F, feather)</c>.</para>
///
/// <para><b>Why that is exact.</b> With a premultiplied subtree result <c>(C, a)</c>, the legacy composite is
/// <c>out = C·f + D·(1 − a·f)</c> (SourceOver of <c>src × f</c>); drawing direct gives <c>F = C + D·(1 − a)</c>; and
/// <c>lerp(D, F, f) = D + f·(C − D·a) = C·f + D·(1 − a·f)</c> — identical for ANY backdrop alpha, which a single-snapshot
/// SourceOver restore is not (it is exact only where the backdrop is opaque, and a Mica window's back buffer holds
/// <c>d &lt; 1</c>). The 8-bit UNORM snapshot round-trip makes it ~1/255-accurate rather than bit-exact.</para>
///
/// <para><b>The DISJOINTNESS invariant (load-bearing).</b> The returned strips are pairwise DISJOINT: a pixel restored
/// twice would have the feather applied twice. The scheme is:</para>
/// <list type="bullet">
/// <item>the TOP and BOTTOM strips take the FULL clip width;</item>
/// <item>the LEFT and RIGHT strips take only the rows BETWEEN them (and the right strip starts at or after the left
/// strip's end);</item>
/// <item>the top/bottom band depth is widened to <c>max(band, active corner radius)</c> so all four corner squares —
/// every one of which touches the top or the bottom edge — are subsumed by a full-width strip. A corner arc only
/// contributes where BOTH its adjacent edges fade (<c>arcN</c> in the edge-fade shader) and its influence is confined
/// to that corner's radius square, so this is complete without a fifth strip.</item>
/// </list>
///
/// <para><b>The COVERAGE invariant.</b> Every pixel of the composite box where the analytic feather is &lt; 1 lies in
/// some strip; outside the strips the feather is exactly 1, i.e. <c>lerp(D, F, 1) == F</c> == the pixels already on the
/// target. Strips extend OUTWARD to the composite-clip edge (not just to the layer rect), because a faded edge drives
/// the feather to 0 outside the rect and the direct-drawn subtree must be erased back to <c>D</c> there.</para>
///
/// <para>An edge is enabled iff its band is &gt; 0 — the same predicate the shader uses (<c>if (bx &gt; 0.0)</c>); the
/// recorder already zeroes the band of every edge missing from <c>FadeEdges</c>.</para>
/// </summary>
public static class EdgeFadeStrips
{
    /// <summary>At most one strip per edge.</summary>
    public const int MaxStrips = 4;

    /// <summary>True iff <paramref name="layer"/> is a PURE edge fade — no blur and a flat group alpha of 1 — and so
    /// may take the strip path. Everything else (FadeAndBlur, an alpha-faded group) keeps the legacy full-canvas group
    /// RT: a σ &gt; 0 layer needs the whole canvas cleared (BlurInPlace reads a halo past the composite clip, see
    /// <see cref="EdgeFadeLayerClear"/>), and a group alpha &lt; 1 is exactly the case where drawing the subtree direct
    /// would double-blend overlapping children.</summary>
    public static bool IsPureFade(in PushLayerCmd layer)
        => layer.Kind == (int)LayerKind.EdgeFade && layer.BlurSigma == 0f && layer.GroupAlpha >= 0.999f;

    /// <summary>Whether the strip path may run while a pooled group RT is already open — the ENCLOSING-TARGET half of
    /// the eligibility decision (<see cref="IsPureFade"/> is the payload half). Pure decision, no GPU state, so the
    /// headless gates own the truth table the backend then obeys.
    ///
    /// <para>Only the INNERMOST open group matters: the strip snapshot reads, and the restore writes, exactly the one
    /// surface the subtree is drawing into. Two properties of that surface are what the strip algebra needs, and both
    /// are decided by the innermost group alone:</para>
    /// <list type="bullet">
    /// <item><b>every texel defined.</b> A <see cref="LayerKind.Blur"/> lease always takes a FULL clear (the backend
    /// computes a partial <c>clearRect</c> only for EdgeFade and for a recorder-patched plain Opacity group), so a strip
    /// can never snapshot an UNCLEARED pooled texel. A plain Opacity group is cleared only over its patched extent and
    /// an EdgeFade group only over its box — hence neither is admitted.</item>
    /// <item><b>1:1 canvas space.</b> A full-canvas Blur lease binds the canvas-sized pool RT under the FULL viewport,
    /// so <c>SV_Position</c> is still the canvas-space device pixel the restore shader's geometry assumes. A
    /// REGION-LOCAL blur (<paramref name="innermostLocalUsedW"/> &gt; 0) runs a SHIFTED viewport into a bucketed
    /// scratch — that one must keep the legacy lease.</item>
    /// </list>
    /// <paramref name="innermostLocalUsedW"/> is the innermost group's <c>LocalBlurSurface.UsedW</c> (0 ⇒ the
    /// full-canvas lease). With no group open at all the strip path was always eligible.</summary>
    public static bool GroupAllowsStrip(int openGroupCount, int innermostKind, int innermostLocalUsedW)
        => openGroupCount <= 0
        || (innermostKind == (int)LayerKind.Blur && innermostLocalUsedW == 0);

    /// <summary>Compute the (≤ 4, pairwise DISJOINT) physical-pixel strips this fade must snapshot and restore.
    /// <paramref name="strips"/> must have room for <see cref="MaxStrips"/>; <paramref name="count"/> is 0 when the
    /// fade is a no-op (no enabled edge, empty composite clip, degenerate canvas) — the subtree then simply draws
    /// straight through with nothing to restore.</summary>
    public static void Compute(in PushLayerCmd layer, float scale, int canvasW, int canvasH,
        Span<SelfBlurPixelBox> strips, out int count)
    {
        count = 0;
        if (strips.Length < MaxStrips || !(scale > 0f) || canvasW <= 0 || canvasH <= 0) return;

        float bandL = layer.FadeBandL, bandT = layer.FadeBandT, bandR = layer.FadeBandR, bandB = layer.FadeBandB;
        bool hasL = bandL > 0f, hasT = bandT > 0f, hasR = bandR > 0f, hasB = bandB > 0f;
        if (!hasL && !hasT && !hasR && !hasB) return;   // no enabled edge ⇒ feather is 1 everywhere ⇒ identity

        // The composite box: the same floor/ceil clamp of CompositeClip the legacy composite scissors to.
        RectF cr = layer.CompositeClip;
        if (cr.W <= 0f || cr.H <= 0f) return;
        int boxL = Math.Clamp((int)MathF.Floor(cr.X * scale), 0, canvasW);
        int boxT = Math.Clamp((int)MathF.Floor(cr.Y * scale), 0, canvasH);
        int boxR = Math.Clamp((int)MathF.Ceiling((cr.X + cr.W) * scale), boxL, canvasW);
        int boxB = Math.Clamp((int)MathF.Ceiling((cr.Y + cr.H) * scale), boxT, canvasH);
        if (boxR <= boxL || boxB <= boxT) return;

        RectF r = layer.DeviceRect;
        float rectL = r.X, rectT = r.Y, rectR = r.X + r.W, rectB = r.Y + r.H;

        // Fold every ACTIVE corner arc into the top/bottom depth (see the disjointness note on the type). A corner is
        // active only when both of its adjacent edges fade, and its arc can only pull the feather below 1 inside its
        // own radius square — which always touches the top or the bottom edge.
        float topDepth = hasT
            ? MathF.Max(bandT, MathF.Max(hasL ? layer.Radii.TopLeft : 0f, hasR ? layer.Radii.TopRight : 0f))
            : 0f;
        float botDepth = hasB
            ? MathF.Max(bandB, MathF.Max(hasL ? layer.Radii.BottomLeft : 0f, hasR ? layer.Radii.BottomRight : 0f))
            : 0f;

        // Rows [midTop, midBottom) are what is left for the left/right strips once the full-width top/bottom strips
        // have taken theirs — this is what makes the four strips disjoint.
        int midTop = boxT, midBottom = boxB;

        if (hasT)
        {
            int stripBottom = Math.Clamp((int)MathF.Ceiling((rectT + topDepth) * scale), boxT, boxB);
            if (stripBottom > boxT)
            {
                strips[count++] = new SelfBlurPixelBox(boxL, boxT, boxR, stripBottom);
                midTop = stripBottom;
            }
        }
        if (hasB)
        {
            int stripTop = Math.Clamp((int)MathF.Floor((rectB - botDepth) * scale), midTop, boxB);
            if (stripTop < boxB)
            {
                strips[count++] = new SelfBlurPixelBox(boxL, stripTop, boxR, boxB);
                midBottom = stripTop;
            }
        }
        if (midBottom < midTop) midBottom = midTop;
        if (midBottom <= midTop) return;   // top+bottom already cover every row of the box

        int midLeft = boxL;
        if (hasL)
        {
            int stripRight = Math.Clamp((int)MathF.Ceiling((rectL + bandL) * scale), boxL, boxR);
            if (stripRight > boxL)
            {
                strips[count++] = new SelfBlurPixelBox(boxL, midTop, stripRight, midBottom);
                midLeft = stripRight;
            }
        }
        if (hasR)
        {
            int stripLeft = Math.Clamp((int)MathF.Floor((rectR - bandR) * scale), midLeft, boxR);
            if (stripLeft < boxR)
                strips[count++] = new SelfBlurPixelBox(stripLeft, midTop, boxR, midBottom);
        }
    }

    /// <summary>Total physical-pixel area of a computed strip set (the copy + restore cost, for diagnostics).</summary>
    public static long AreaPx(ReadOnlySpan<SelfBlurPixelBox> strips, int count)
    {
        long n = 0;
        for (int i = 0; i < count && i < strips.Length; i++) n += strips[i].AreaPx;
        return n;
    }

    /// <summary>The analytic feather the edge-fade shader applies at a device pixel centre — the CPU mirror of
    /// <c>featherAt</c> in the backend's shared HLSL body. Used by the headless gates (and by anyone reasoning about
    /// the restore) so the coverage invariant above can be asserted without a GPU.</summary>
    public static float FeatherAt(in PushLayerCmd layer, float scale, float px, float py)
    {
        float rx = layer.DeviceRect.X * scale, ry = layer.DeviceRect.Y * scale;
        float rz = (layer.DeviceRect.X + layer.DeviceRect.W) * scale, rw = (layer.DeviceRect.Y + layer.DeviceRect.H) * scale;
        float bx = layer.FadeBandL * scale, bt = layer.FadeBandT * scale;
        float br = layer.FadeBandR * scale, bb = layer.FadeBandB * scale;
        float n = 1e9f;
        if (bx > 0f) n = MathF.Min(n, (px - rx) / bx);
        if (bt > 0f) n = MathF.Min(n, (py - ry) / bt);
        if (br > 0f) n = MathF.Min(n, (rz - px) / br);
        if (bb > 0f) n = MathF.Min(n, (rw - py) / bb);

        float cTL = layer.Radii.TopLeft * scale, cTR = layer.Radii.TopRight * scale;
        float cBR = layer.Radii.BottomRight * scale, cBL = layer.Radii.BottomLeft * scale;
        n = MathF.Min(n, Arc(px, py, rx + cTL, ry + cTL, cTL, MathF.Min(bx, bt), bx > 0f && bt > 0f && px < rx + cTL && py < ry + cTL));
        n = MathF.Min(n, Arc(px, py, rz - cTR, ry + cTR, cTR, MathF.Min(br, bt), br > 0f && bt > 0f && px > rz - cTR && py < ry + cTR));
        n = MathF.Min(n, Arc(px, py, rz - cBR, rw - cBR, cBR, MathF.Min(br, bb), br > 0f && bb > 0f && px > rz - cBR && py > rw - cBR));
        n = MathF.Min(n, Arc(px, py, rx + cBL, rw - cBL, cBL, MathF.Min(bx, bb), bx > 0f && bb > 0f && px < rx + cBL && py > rw - cBL));

        float t = Math.Clamp(n, 0f, 1f);
        float mode = layer.FadeFalloff;
        float feather = mode < 0.5f ? t : mode < 1.5f ? t * t * (3f - 2f * t) : t * t * t;
        float intensity = Math.Clamp(layer.FadeIntensity, 0f, 1f);
        return (1f + (feather - 1f) * intensity) * Math.Clamp(layer.GroupAlpha, 0f, 1f);
    }

    private static float Arc(float px, float py, float cx, float cy, float r, float cb, bool active)
    {
        if (!active || r <= 0f || cb <= 0f) return 1e9f;
        float dx = px - cx, dy = py - cy;
        return (r - MathF.Sqrt(dx * dx + dy * dy)) / cb;
    }
}
