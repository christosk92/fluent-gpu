using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>A wrapped run's line pitch is fractional in device px (Segoe UI 14 → 18.62 DIP), so each line's baseline sits
/// at its own sub-pixel phase. Snapping the whole run from line 0 left lines 2..n at a fractional device row, smeared
/// across two rows by the LINEAR atlas sampler.</summary>
public sealed class GlyphLineSnapTests
{
    private const int N = GlyphRenderer.SubPixelPhases;
    private const float Top = 3.3f, Ascent = 14.9f, Pitch = 18.62f;   // Segoe UI 14: (2210 + 514) / 2048 × 14
    private static readonly Affine2D World = Affine2D.Translation(0f, 7.37f);

    // Three lines of three quads, baked like ShapeInto: line baseline + a whole-device-row bearing. V0 = 0 with a unit
    // VStride makes each emitted V0 read back the phase Replay chose.
    private static ShapedGlyph[] Wrapped(float dpi)
    {
        var glyphs = new ShapedGlyph[9];
        for (int line = 0, k = 0; line < 3; line++)
            for (int g = 0; g < 3; g++, k++)
                glyphs[k] = new ShapedGlyph
                {
                    DstX = g * 9f, DstY = Top + Ascent + line * Pitch + (-10 + g) / dpi,
                    DstW = 8f / dpi, DstH = 10f / dpi, U1 = 1f, V1 = 1f, VStride = 1f,
                };
        return glyphs;
    }

    private static void AssertOnPhaseGrid(float bakedY, float dstY, float v0, float dpi)
    {
        float devTop = (dstY + World.Dy) * dpi;
        Assert.InRange(MathF.Abs(devTop - MathF.Round(devTop)), 0f, 1e-3f);   // texel-exact: no bilinear smear
        float baked = (bakedY + World.Dy) * dpi;
        Assert.InRange(MathF.Abs(devTop + v0 / N - baked), 0f, 0.5f / N + 1e-3f);   // the phase carries the rest
    }

    [Theory]
    [InlineData(1.00f)]
    [InlineData(1.25f)]
    [InlineData(1.50f)]
    public void EveryWrappedLineLandsOnItsOwnPhase(float dpi)
    {
        var glyphs = Wrapped(dpi);
        var outList = new List<GlyphInstance>();
        GlyphRenderer.Replay(glyphs, null, false, new ColorF(1, 1, 1, 1), World, 1f, dpi, 0f, outList);
        Assert.Equal(glyphs.Length, outList.Count);
        for (int i = 0; i < glyphs.Length; i++) AssertOnPhaseGrid(glyphs[i].DstY, outList[i].DstY, outList[i].V0, dpi);
    }

    [Theory]
    [InlineData(1.00f)]
    [InlineData(1.25f)]
    public void WrappedWipeLinesLandOnTheirOwnPhase(float dpi)
    {
        var glyphs = Wrapped(dpi);
        var ro = new float[glyphs.Length];
        var outList = new List<GradGlyphInstance>();
        GlyphRenderer.ReplayGradient(glyphs, World, 1f, dpi, 0f, new ColorF(1, 1, 1, 1), new ColorF(1, 1, 1, 0.5f),
            0.5f, 0.1f, 1f, ro, ro, ReadOnlySpan<float>.Empty, outList);
        Assert.Equal(glyphs.Length, outList.Count);
        for (int i = 0; i < glyphs.Length; i++) AssertOnPhaseGrid(glyphs[i].DstY, outList[i].DstY, outList[i].V0, dpi);
    }
}
