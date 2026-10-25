using System.Globalization;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;

using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// GLYPH INK PUBLISHING (audit PERF r3 RASTER, item 8). Every replayed glyph run publishes the extent of its shaped quads to
// GlyphInk (relative to its layout origin, keyed by its shaping inputs), where the slice recorder's fade distribution test
// reads it in place of the run's 1.5-em halo box. `--fg ink-validate` checks the premise on every replay: the quads this
// replay emitted lie inside the extent the table held for the run BEFORE it (the one a distribution decision could have
// read), margin included.
public sealed unsafe partial class D3D12Device
{
    private long _inkRuns, _inkQuads, _inkBreaches;
    private float _inkMaxOver;

    /// <summary>DecodeOne, after a DrawGlyphRun's LayoutRun: publish the run's extent (and check the previous one).</summary>
    private void PublishGlyphInk(in DrawGlyphRunCmd g, int firstInstance)
    {
        ulong key = GlyphInk.Key(in g, _frameScale);
        if (TileDamage.InkValidate && GlyphInk.TryGet(key, out RectF prior)) CheckInk(in g, in prior, firstInstance);
        // Only a COMPLETE shape publishes: once the atlas overflowed this frame, a glyph that failed to pack has no quad in
        // the shapes made from then on (the reset at the next BeginFrame drops them and reshapes), so their extent could
        // be short of the run's.
        if (_glyphs!.LastRunInkValid && !_glyphs.AtlasResetPending) GlyphInk.Store(key, in _glyphs.LastRunInk);
    }

    private void CheckInk(in DrawGlyphRunCmd g, in RectF prior, int first)
    {
        RectF box = GlyphInk.LocalBox(in g, in prior);
        _inkRuns++;
        for (int i = first; i < _glyphInsts.Count; i++)
        {
            GlyphInstance q = _glyphInsts[i];
            if (!(q.DstW > 0f) || !(q.DstH > 0f)) continue;
            _inkQuads++;
            float over = box.IsEmpty ? float.PositiveInfinity
                : MathF.Max(MathF.Max(box.X - q.DstX, q.DstX + q.DstW - box.Right), MathF.Max(box.Y - q.DstY, q.DstY + q.DstH - box.Bottom));
            if (over <= 0f) continue;
            _inkBreaches++;
            if (over > _inkMaxOver) _inkMaxOver = over;
            if (_inkBreaches <= 10)
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[ink-validate] BREACH text={g.Text.Value} size={g.FontSize} box=({box.X:0.##},{box.Y:0.##} {box.W:0.##}x{box.H:0.##}) quad=({q.DstX:0.##},{q.DstY:0.##} {q.DstW:0.##}x{q.DstH:0.##}) over={over:0.###}"));
        }
        if (_inkRuns % 500 == 1)
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[ink-validate] runs={_inkRuns} quads={_inkQuads} breaches={_inkBreaches} maxOver={_inkMaxOver:0.###}"));
    }
}
