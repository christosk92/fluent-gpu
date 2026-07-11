namespace FluentGpu.Render;

/// <summary>
/// Portable scanline polygon fill for icon masks: 4×4-supersampled coverage of a set of NORMALIZED (0..1) flattened
/// contours into a caller-provided R8 buffer. Supports even-odd and nonzero winding. Deterministic and backend-free —
/// the same routine feeds the D3D12 atlas raster (backend-side, lazy on a cache miss) and the VerticalSlice golden
/// gates. Only per-call scratch (the crossing list) allocates; the coverage buffer is the caller's.
///
/// <para>This is deliberately NOT the design/subsystems/gpu-renderer.md §5 GPU tessellation lane — icons are tiny,
/// static, glyph-shaped workloads and ride the DirectWrite R8 glyph atlas exactly like text (same posture as
/// DrawTabShape's SDF variant), so the tessellation-fraction tripwire stays honest.</para>
/// </summary>
public static class IconRaster
{
    /// <summary>Supersample factor per axis (4×4 = 16 samples/pixel box filter).</summary>
    public const int SS = 4;

    private struct Crossing { public float X; public int Dir; }

    /// <summary>Rasterize the contours (normalized 0..1, x/y interleaved in <paramref name="coords"/>; each contour a
    /// <paramref name="starts"/>[k] point offset + <paramref name="counts"/>[k] point run) into <paramref name="dst"/>
    /// (row-major, length ≥ <paramref name="w"/>×<paramref name="h"/>) as 8-bit coverage. <paramref name="evenOdd"/>
    /// selects the fill rule (else nonzero winding). Deterministic; clamps rather than throwing on odd geometry.</summary>
    public static void Rasterize(ReadOnlySpan<float> coords, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        bool evenOdd, int w, int h, Span<byte> dst)
    {
        int need = w * h;
        if (w <= 0 || h <= 0 || dst.Length < need) return;
        dst.Slice(0, need).Clear();
        if (starts.Length == 0) return;

        var acc = new int[w];                 // per-pixel sub-sample hit count for the current output row
        var xs = new List<Crossing>(32);      // crossings at one sub-scanline
        int subW = w * SS;

        for (int r = 0; r < h; r++)
        {
            Array.Clear(acc);
            for (int sj = 0; sj < SS; sj++)
            {
                float sy = (r + (sj + 0.5f) / SS) / h;   // 0..1
                xs.Clear();
                for (int k = 0; k < starts.Length; k++)
                {
                    int p0 = starts[k], cnt = counts[k];
                    for (int e = 0; e < cnt; e++)
                    {
                        int a = (p0 + e) * 2;
                        int bi = (p0 + (e + 1 == cnt ? 0 : e + 1)) * 2;   // wrap last→first (implicit close)
                        float ya = coords[a + 1], yb = coords[bi + 1];
                        if (ya == yb) continue;
                        bool up = ya < yb;
                        float lo = up ? ya : yb, hi = up ? yb : ya;
                        if (sy < lo || sy >= hi) continue;   // half-open to avoid double-counting shared vertices
                        float xa = coords[a], xb = coords[bi];
                        float x = xa + (sy - ya) / (yb - ya) * (xb - xa);
                        xs.Add(new Crossing { X = x, Dir = up ? 1 : -1 });
                    }
                }
                if (xs.Count < 2) continue;
                xs.Sort(static (p, q) => p.X.CompareTo(q.X));

                if (evenOdd)
                {
                    for (int c = 0; c + 1 < xs.Count; c += 2)
                        FillSpan(acc, xs[c].X, xs[c + 1].X, w, subW);
                }
                else
                {
                    int wind = 0;
                    for (int c = 0; c + 1 < xs.Count; c++)
                    {
                        wind += xs[c].Dir;
                        if (wind != 0) FillSpan(acc, xs[c].X, xs[c + 1].X, w, subW);
                    }
                }
            }

            int rowOff = r * w;
            for (int x = 0; x < w; x++)
            {
                int cov = acc[x] * 255 + (SS * SS) / 2;
                dst[rowOff + x] = (byte)Math.Min(255, cov / (SS * SS));
            }
        }
    }

    // Add sub-column coverage for a normalized x-span [xa,xb] into the per-pixel accumulator (one sub-scanline).
    private static void FillSpan(int[] acc, float xa, float xb, int w, int subW)
    {
        if (xb <= xa) return;
        // Sub-column sc has center (sc+0.5)/subW; it is inside iff that center ∈ [xa,xb].
        int scLo = (int)MathF.Ceiling(xa * subW - 0.5f);
        int scHi = (int)MathF.Floor(xb * subW - 0.5f);
        if (scLo < 0) scLo = 0;
        if (scHi > subW - 1) scHi = subW - 1;
        for (int sc = scLo; sc <= scHi; sc++)
            acc[sc / SS]++;
    }
}
