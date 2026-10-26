using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;

using ColorF = FluentGpu.Foundation.ColorF;

namespace FluentGpu.Rhi.D3D12;

// ── CLEAR FOLD: a window-covering opaque ground fill becomes the composite pass's clear colour (audit PERF r3 COMP, item 3) ──
//
// On the whole-frame route the composite pass opens with a CLEAR load — on a tiler a per-bin fast clear that shades no
// fragment — and the shell then paints its ground: a solid, opaque, square fill over the whole window as the first op of
// the first item drawn (Wavee: `zune.material.ground`, 5.6 M fragments a frame at 3444x1620). That fill overwrites every
// pixel the clear wrote, so the clear's colour is never seen: clearing to the FILL's colour instead and not drawing the fill
// leaves every pixel identical. Exactness, step by step:
//   * coverage: such a fill (kind 0, radius 0, alpha 1, opacity 1, no rounded clip, no stencil scope) draws through the rect
//     pipe's opaque fast path — a plain quad, no SDF, no AA ramp, no blend — which writes the colour at every pixel whose centre
//     lies inside it; the fill's quad contains every pixel centre of the window (a window-exact rect does, a quarter pixel to
//     spare). Without that PSO the blended SDF path is used, and its ramp needs the edge FoldMarginPx outside the window;
//   * colour: alpha 1 and opacity 1, so the shader writes (r·1, g·1, b·1, 1) — premultiplied equals straight — and a
//     render-target clear converts the same four floats to UNORM8 by the same float->UNORM rule;
//   * order: it is the first op of the first item drawn (every earlier item is hidden under an opaque cover, so draws nothing),
//     with no scope open before it (no clip, layer, stencil or blend bracket in the segment's prefix) and its item composites
//     plain (InlineDirect: alpha 1, source-over, no feather / rounded clip) under a scissor covering the window.
// Anything else keeps the frame's own clear and draws the fill. The partial route is untouched (its repaint rects already
// start at their opaque cover, D3D12Device.PartialPresent.PpCompositeRects). present-validate's shadow composite draws the
// unfolded frame and compares it with the back buffer: the proof. `--fg no-clear-fold` is the A/B arm.
public sealed unsafe partial class D3D12Device
{
    /// <summary>The fill must reach this far past every window edge (device px): beyond the SDF's half-pixel ramp with room
    /// for a slice's sub-pixel residual.</summary>
    private const float FoldMarginPx = 2f;

    private int _foldItem = -1;          // the item whose first op is folded this composite (whole route only)
    private bool _foldPending;           // the next FillRoundRect the decoder sees is the folded fill: skip it
    private FillRoundRectCmd _foldCmd;
    /// <summary>Whole-route composites whose ground fill was folded into the clear (diagnostics, render thread).</summary>
    public long ClearFoldFrames { get; private set; }

    /// <summary>The whole-route composite's clear colour: the ground fill's when it folds (see the file header), else the frame's.</summary>
    private ColorF ClearFoldDecide(in CompositeFrame frame)
    {
        _foldItem = -1;
        ColorF clear = frame.Info.Clear;
        if (!TileDamage.ClearFold || (_frameKnockouts & (GpuKnockouts.ClearOnly | GpuKnockouts.ForceFullDirect)) != 0) return clear;
        ReadOnlySpan<CompositeItem> items = frame.Items;
        int k = 0;
        while (k < items.Length && Hidden(k, items.Length))
            k = (items[k].Kind == CompositeKind.Group ? Math.Min(items.Length, k + 1 + items[k].GroupCount) : k + 1);
        if (k >= items.Length) return clear;
        ref readonly CompositeItem it = ref items[k];
        if (!InlineDirect(in it)) return clear;
        if (!IsUnbounded(it.Clip) && (it.Clip.X > 0f || it.Clip.Y > 0f || it.Clip.Right < _w || it.Clip.Bottom < _h)) return clear;
        int r = RowOf(it.SliceId);
        if (r < 0) return clear;
        ref readonly SliceRow row = ref frame.Slices[r];
        if (HasScopeOps(frame.PrefixOf(in row))) return clear;
        ReadOnlySpan<byte> s = frame.StreamOf(in row);
        if (s.Length < sizeof(int) || (DrawOp)MemoryMarshal.Read<int>(s) != DrawOp.FillRoundRect
            || s.Length < sizeof(int) + Unsafe.SizeOf<FillRoundRectCmd>()) return clear;
        var c = MemoryMarshal.Read<FillRoundRectCmd>(s[sizeof(int)..]);
        if (c.FillKind != 0 || c.Fill.A != 1f || c.Opacity != 1f
            || c.Radii.TopLeft != 0f || c.Radii.TopRight != 0f || c.Radii.BottomRight != 0f || c.Radii.BottomLeft != 0f
            || c.Transform.M12 != 0f || c.Transform.M21 != 0f || !(c.Transform.M11 > 0f) || !(c.Transform.M22 > 0f)) return clear;
        // the replay maps stream DIP through the op's transform, the window scale, the slice's sub-pixel phase and the item's
        // placement (DrawInline / ReplaySegment)
        float sc = _frameScale;
        float ox = it.Transform.Dx - row.Frame.OriginX + row.Frame.PhaseX, oy = it.Transform.Dy - row.Frame.OriginY + row.Frame.PhaseY;
        float x0 = ox + (c.Transform.M11 * c.Rect.X + c.Transform.Dx) * sc, x1 = ox + (c.Transform.M11 * (c.Rect.X + c.Rect.W) + c.Transform.Dx) * sc;
        float y0 = oy + (c.Transform.M22 * c.Rect.Y + c.Transform.Dy) * sc, y1 = oy + (c.Transform.M22 * (c.Rect.Y + c.Rect.H) + c.Transform.Dy) * sc;
        // Coverage 1 at every pixel: through the opaque fast path (IsOpaquePlainRect: no SDF, no AA ramp, a plain quad) every pixel
        // whose centre lies inside the quad gets the colour, so the quad need only contain every centre (k + 0.5) — a window-exact
        // rect does, with a quarter pixel to spare. Without that PSO the SDF ramp needs the edge FoldMarginPx outside.
        float m = _rectPipe!.HasOpaquePso ? -0.25f : FoldMarginPx;
        if (!(x0 <= -m && y0 <= -m && x1 >= _w + m && y1 >= _h + m)) return clear;
        _foldItem = k;
        _foldCmd = c;
        ClearFoldFrames++;
        return new ColorF(c.Fill.R, c.Fill.G, c.Fill.B, 1f);
    }

    /// <summary>Any scope op (clip, stencil clip, layer, blend bracket) in a segment prefix: the first op would not be drawn plain.</summary>
    private static bool HasScopeOps(ReadOnlySpan<byte> prefix)
    {
        int pos = 0;
        while (pos + sizeof(int) <= prefix.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(prefix[pos..]);
            if (op is DrawOp.PushClip or DrawOp.PushStencilClip or DrawOp.PushLayer or DrawOp.SetBlend) return true;
            if (!RepaintStreamSafety.TryBodySize(op, out int body)) return true;
            pos += sizeof(int) + body;
        }
        return false;
    }

    /// <summary>The decoder's question for a FillRoundRect: is this the folded ground fill (the first one decoded while the
    /// folded item replays)? One-shot: any first fill consumes the arm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool FoldSkip(in FillRoundRectCmd c)
    {
        if (!_foldPending) return false;
        _foldPending = false;
        return c == _foldCmd;
    }
}
