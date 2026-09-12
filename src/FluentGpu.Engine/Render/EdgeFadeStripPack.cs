namespace FluentGpu.Render;

/// <summary>
/// The ONE placement contract for the pure-edge-fade strip snapshot scratch (<see cref="EdgeFadeStrips"/>): where each
/// strip's pixels live inside the pooled surface that holds both the PRE-subtree snapshot (<c>D</c>) and the
/// POST-subtree one (<c>F</c>). TerraFX-free so the headless gates and the unit tests own the truth the D3D12 backend
/// (<c>OpacityLayerCompositor.AcquireStripScratch</c> / <c>CopyStripSnapshot</c> / <c>EdgeFadeStripRestore</c>) obeys —
/// every consumer of a pack offset calls <see cref="Place"/>, so the copy destination and the restore shader's source
/// UV cannot disagree.
///
/// <para><b>Why it is not a plain vertical stack.</b> The strips of a four-edge fade are two FULL-WIDTH horizontal bands
/// and two FULL-HEIGHT vertical ones. Stacking all four on one column made the scratch
/// <c>max(width) × Σ(height)</c> — for a full-window 1770×1140 fade with 24-px bands that is 1770×2328 per half, ~7×
/// a full-window render target, of which over 90% is never written. The strips' actual area is under 140 kpx.</para>
///
/// <para><b>The SHELF layout.</b> Two rows of placement, chosen per strip by its own aspect — deterministic, O(strips),
/// allocation-free (strip counts are bounded by <see cref="EdgeFadeStrips.MaxStrips"/>):</para>
/// <list type="bullet">
/// <item><b>Column strips</b> (<c>Width &gt;= Height</c> — the top/bottom bands) stack VERTICALLY at <c>x = 0</c>, in
/// input order, from <c>y = 0</c>.</item>
/// <item><b>Shelf strips</b> (<c>Height &gt; Width</c> — the left/right bands) sit SIDE BY SIDE on one shelf whose top
/// is the total column height, in input order, from <c>x = 0</c>.</item>
/// </list>
/// <para>So <c>packW = max(max column width, Σ shelf widths)</c> and <c>packH = Σ column heights + max shelf height</c>
/// — 1770×1140 per half for the full-window fade above: 15.4 MiB tight for both halves, 28 MiB after the pool's
/// power-of-two bucket ladder above 2048 rows, instead of 56.</para>
///
/// <para><b>The invariants the backend relies on</b> (unit-gated): placements are pairwise NON-OVERLAPPING (column
/// strips own disjoint row bands at <c>x = 0</c>; shelf strips own disjoint column bands on one shelf strictly below
/// every column strip); every placed strip lies inside <c>[0,packW) × [0,packH)</c>, so the <c>F</c> half at
/// <c>y + packH</c> never collides with the <c>D</c> half; and <c>D</c> and <c>F</c> placements of the SAME strip differ
/// only by that half offset.</para>
/// </summary>
public static class EdgeFadeStripPack
{
    /// <summary>True iff this strip stacks on the vertical column (a wide band) rather than the side-by-side shelf.
    /// Degenerate/empty strips are column strips and contribute nothing to either extent.</summary>
    public static bool IsColumnStrip(in SelfBlurPixelBox s) => s.Width >= s.Height;

    /// <summary>Size of ONE half (the <c>D</c> or the <c>F</c> snapshot). The full scratch is
    /// <c>packW × packH * 2</c>.</summary>
    public static void Measure(ReadOnlySpan<SelfBlurPixelBox> strips, int count, out int packW, out int packH)
    {
        int columnW = 0, columnH = 0, shelfW = 0, shelfH = 0;
        int n = Math.Min(count, strips.Length);
        for (int i = 0; i < n; i++)
        {
            SelfBlurPixelBox s = strips[i];
            int w = s.Width, h = s.Height;   // SelfBlurPixelBox clamps both to >= 0
            if (IsColumnStrip(in s)) { if (w > columnW) columnW = w; columnH += h; }
            else { shelfW += w; if (h > shelfH) shelfH = h; }
        }
        packW = Math.Max(columnW, shelfW);
        packH = columnH + shelfH;
    }

    /// <summary>Top-left corner of <paramref name="index"/>'s pixels inside a half (add <c>packH</c> for the <c>F</c>
    /// half). O(count) with count ≤ <see cref="EdgeFadeStrips.MaxStrips"/>, no allocation.</summary>
    public static void Place(ReadOnlySpan<SelfBlurPixelBox> strips, int count, int index, out int x, out int y)
    {
        x = 0; y = 0;
        int n = Math.Min(count, strips.Length);
        if ((uint)index >= (uint)n) return;

        if (IsColumnStrip(in strips[index]))
        {
            for (int i = 0; i < index; i++)
                if (IsColumnStrip(in strips[i])) y += strips[i].Height;
            return;
        }

        for (int i = 0; i < n; i++)
            if (IsColumnStrip(in strips[i])) y += strips[i].Height;   // the shelf starts under the whole column
        for (int i = 0; i < index; i++)
            if (!IsColumnStrip(in strips[i])) x += strips[i].Width;
    }
}
