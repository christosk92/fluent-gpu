using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using TerraFX.Interop.DirectX;

namespace FluentGpu.Rhi.D3D12;

/// <summary>
/// INLINE segments: a plain <see cref="CompositeKind.Direct"/> item (source-over, alpha 1, no blur, feather, rounded clip,
/// low-res surface or feedback) is not rastered anywhere first — its commands are replayed straight into the composite
/// target (the back buffer, or a backdrop / group scratch) at its painter position, under its clip.
/// <para>The recorder sends cheap static segments here (<c>SliceRecorder.InlineSegment</c>) instead of holding their pixels
/// in tiles; a segment the tile budget degrades lands here too when its parameters are plain. Before this, a Direct item
/// was replayed into transient tile-sized SCRATCH chunks every turn and those composited — the scratch pool kept the
/// textures, so a degraded segment cost its tiles' memory anyway plus a raster and a read-back per frame.</para>
/// <para>Everything that reasons about what an item paints sees the same footprint (<see cref="InlineFoot"/>: the item clip
/// ∩ the segment's painted bounds): the backdrop range test, partial present, occlusion both ways.</para>
/// </summary>
public sealed unsafe partial class D3D12Device
{
    /// <summary>Items drawn inline this frame, and the commands they replayed.</summary>
    private int _frameInlineItems, _frameInlineReopens, _frameResumes;

    /// <summary>A Direct item whose composite is a plain source-over copy of its commands' output.</summary>
    private static bool InlineDirect(in CompositeItem it)
        => it.Kind == CompositeKind.Direct && it.LowResDown <= 1 && it.BlurSigma <= 0f && !it.IsFeedback
           && it.Alpha >= 1f && it.Feather.IsNone && it.Feather2.IsNone && it.RoundClip.W <= 0f
           && it.BlendCopy == 0 && it.HasLayer == 0;

    /// <summary>Window px an inline item can write: its clip ∩ its segment's painted bounds (placed), rounded out by a pixel
    /// for antialiasing. Empty when its row is gone.</summary>
    private PixelRect InlineFoot(in CompositeFrame frame, in CompositeItem it)
    {
        int r = RowOf(it.SliceId);
        if (r < 0) return default;
        ref readonly SliceRow row = ref frame.Slices[r];
        PixelRect win = new(0, 0, (int)_w, (int)_h);
        PixelRect foot = IsUnbounded(it.Clip)
            ? win
            : new PixelRect((int)MathF.Floor(it.Clip.X), (int)MathF.Floor(it.Clip.Y), (int)MathF.Ceiling(it.Clip.Right), (int)MathF.Ceiling(it.Clip.Bottom));
        if (!row.ContentBounds.IsEmpty)
        {
            RectF c = row.ContentBounds;
            float tx = it.Transform.Dx, ty = it.Transform.Dy;
            foot = new PixelRect(
                Math.Max(foot.Left, (int)MathF.Floor(tx + c.X) - 1), Math.Max(foot.Top, (int)MathF.Floor(ty + c.Y) - 1),
                Math.Min(foot.Right, (int)MathF.Ceiling(tx + c.X + c.W) + 1), Math.Min(foot.Bottom, (int)MathF.Ceiling(ty + c.Y + c.H) + 1));
        }
        foot = new PixelRect(Math.Max(foot.Left, win.Left), Math.Max(foot.Top, win.Top), Math.Min(foot.Right, win.Right), Math.Min(foot.Bottom, win.Bottom));
        return foot.IsEmpty ? default : foot;
    }

    /// <summary>Replay item <paramref name="i"/>'s segment into the target of the open pass (its origin at window px
    /// (<paramref name="ox"/>, <paramref name="oy"/>), <paramref name="tw"/>×<paramref name="th"/>), cut to the item's
    /// scissor (clip ∩ the repaint / repair rect), then hand the pass back to the compositor.</summary>
    private void DrawInline(in CompositeFrame frame, in CompositeItem it, int ox, int oy, int tw, int th)
    {
        int r = RowOf(it.SliceId);
        if (r < 0) return;
        ref readonly SliceRow row = ref frame.Slices[r];
        ItemScissor(in it, ox, oy);
        PixelRect foot = InlineFoot(in frame, in it);
        PixelRect cut = new(
            Math.Max(_itemScissor.Left, foot.Left - ox), Math.Max(_itemScissor.Top, foot.Top - oy),
            Math.Min(_itemScissor.Right, foot.Right - ox), Math.Min(_itemScissor.Bottom, foot.Bottom - oy));
        if (cut.IsEmpty) return;
        D3D12_CPU_DESCRIPTOR_HANDLE target = _passRtv;
        int pw = _passRtvW, ph = _passRtvH;
        // target px = slice px + (Transform − OriginX − ox): the chunk route's arithmetic (PrepareDirectBlur) with the
        // target in place of the chunk. The span cull wants the target in slice px relative to the origin.
        int tx = (int)it.Transform.Dx, ty = (int)it.Transform.Dy;
        int dx = tx - row.Frame.OriginX - ox, dy = ty - row.Frame.OriginY - oy;
        var cull = new RectF(cut.Left - (tx - ox), cut.Top - (ty - oy), cut.Right - cut.Left, cut.Bottom - cut.Top);
        ReplaySegment(in frame, in row, dx, dy, tw, th, target, cull, clamp: true, damage: cut);
        // the replay leaves the pass open or suspended (an inline group layer it closed resumed its own target): put the
        // composite target back and the compositor's bindings with it
        if (!_inRenderPass || _passRtv.ptr != target.ptr) { _frameInlineReopens++; BeginPass(target, pw, ph, PassLoad.Preserve); }
        BindCompositor(tw, th);
        _frameInlineItems++;
    }
}
