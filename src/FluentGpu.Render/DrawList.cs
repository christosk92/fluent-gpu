using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

public enum DrawOp : int
{
    FillRoundRect = 1, DrawGlyphRun = 2, PushClip = 3, PopClip = 4, DrawImage = 5,
    DrawRoundRectStroke = 6,   // SDF outline (focus ring) — same pipeline as FillRoundRect, with a stroke width
    DrawShadow = 7,            // soft drop shadow (analytic rounded-box gaussian)
    DrawGradientRect = 8,      // linear/radial gradient fill (≤4 stops)
    PushLayer = 9,             // begin a backdrop-effect layer (acrylic): blur the canvas behind DeviceRect
    PopLayer = 10,             // end the layer: composite tint + noise, then the subtree drew on top
    DrawGradientStroke = 11,   // gradient-tinted SDF outline (WinUI elevation border) — gradient PS + a stroke band
}

// POD payloads (unmanaged). Encoded as [int op][payload] in the byte stream.
// Transform is the composited world transform (local→device); Opacity is the cumulative subtree opacity.
public readonly record struct FillRoundRectCmd(RectF Rect, CornerRadius4 Radii, ColorF Fill, Affine2D Transform, float Opacity);
// Wrap/Trim are TextWrap/TextTrim enum values; MaxLines caps the line count (0 = unlimited). Bounds.W is the wrap width.
public readonly record struct DrawGlyphRunCmd(RectF Bounds, ColorF Color, StringId Text, StringId Family, float FontSize, int Bold, int Wrap, int Trim, int MaxLines, Affine2D Transform, float Opacity);
// Tier-1 (scissor) clip: an axis-aligned DEVICE-space rect already intersected with the enclosing clip by the recorder.
// The RHI sets the scissor to <see cref="DeviceRect"/> on PushClip and restores the previous on PopClip.
public readonly record struct ClipCmd(RectF DeviceRect);
// An image quad. <see cref="ImageId"/> is the ImageCache handle; <see cref="Ready"/>==0 ⇒ draw <see cref="Placeholder"/>
// (decode in flight). The GPU leaf samples the uploaded texture for the handle when ready (needs-pixels).
public readonly record struct DrawImageCmd(RectF Rect, CornerRadius4 Radii, int ImageId, int Ready, ColorF Placeholder, Affine2D Transform, float Opacity);
// An SDF outline (focus visual). Same SDF rounded-box as FillRoundRect, but the PS draws a <see cref="StrokeWidth"/>-wide
// band centered on the edge instead of filling. Drawn over the control; works on any fill/background.
public readonly record struct DrawRoundRectStrokeCmd(RectF Rect, CornerRadius4 Radii, ColorF Color, float StrokeWidth, Affine2D Transform, float Opacity);
// A soft drop shadow: a rounded-box SDF with a gaussian falloff (offset + blur + spread), drawn beneath the fill.
public readonly record struct DrawShadowCmd(RectF Rect, CornerRadius4 Radii, ColorF Color, float OffsetX, float OffsetY, float Blur, float Spread, Affine2D Transform, float Opacity);
// A gradient-filled rounded rect. Start/End are in local 0..1 axis coords; up to 4 stops (C0..C3 / O0..O3, StopCount used).
public readonly record struct DrawGradientRectCmd(RectF Rect, CornerRadius4 Radii, Point2 Start, Point2 End, int Shape, int StopCount,
    ColorF C0, ColorF C1, ColorF C2, ColorF C3, float O0, float O1, float O2, float O3, Affine2D Transform, float Opacity);
// A gradient-tinted SDF outline (elevation border): the gradient PS sampled along the local axis, drawn as a band of
// <see cref="StrokeWidth"/> centered on the rounded-box edge (instead of a fill). Same payload as the gradient fill + a width.
public readonly record struct DrawGradientStrokeCmd(RectF Rect, CornerRadius4 Radii, Point2 Start, Point2 End, int Shape, int StopCount,
    ColorF C0, ColorF C1, ColorF C2, ColorF C3, float O0, float O1, float O2, float O3, float StrokeWidth, Affine2D Transform, float Opacity);
// Begin/end a backdrop-effect layer (acrylic): the engine snapshots the canvas under DeviceRect, gaussian-blurs it,
// then on PopLayer tints + adds noise + a luminosity wash; the subtree between the two draws on top.
public readonly record struct PushLayerCmd(RectF DeviceRect, CornerRadius4 Radii, ColorF Tint, float TintOpacity, float BlurSigma, float NoiseOpacity, float LuminosityOpacity);
public readonly record struct PopLayerCmd(RectF DeviceRect);

/// <summary>
/// Flat POD command stream consumed by the RHI (<c>SubmitDrawList</c>). The slice grows a single contiguous buffer;
/// the full engine uses double-buffered arenas + clean-span memcpy. SortKeys are recorded in parallel.
/// </summary>
public sealed class DrawList
{
    private byte[] _buf;
    private int _len;
    private ulong[] _sort;
    private int _sortLen;

    public DrawList(int capacity = 4096)
    {
        _buf = GC.AllocateUninitializedArray<byte>(capacity, pinned: false);
        _sort = new ulong[256];
    }

    public ReadOnlySpan<byte> Bytes => _buf.AsSpan(0, _len);
    public ReadOnlySpan<ulong> SortKeys => _sort.AsSpan(0, _sortLen);
    public int CommandCount { get; private set; }

    public void Reset() { _len = 0; _sortLen = 0; CommandCount = 0; }

    public void FillRoundRect(in RectF rect, in CornerRadius4 radii, in ColorF fill, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.FillRoundRect);
        WritePayload(new FillRoundRectCmd(rect, radii, fill, transform, opacity));
        PushSort(sortKey);
    }

    public void DrawGlyphRun(in RectF bounds, in ColorF color, StringId text, StringId family, float fontSize, int bold, int wrap, int trim, int maxLines, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawGlyphRun);
        WritePayload(new DrawGlyphRunCmd(bounds, color, text, family, fontSize, bold, wrap, trim, maxLines, transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>Push a tier-1 scissor clip (device-space, pre-intersected). Pair with <see cref="PopClip"/>.</summary>
    public void PushClip(in RectF deviceRect, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushClip);
        WritePayload(new ClipCmd(deviceRect));
        PushSort(sortKey);
    }

    public void PopClip(ulong sortKey = 0)
    {
        WriteOp(DrawOp.PopClip);
        PushSort(sortKey);
    }

    public void DrawImage(in RectF rect, in CornerRadius4 radii, int imageId, bool ready, in ColorF placeholder, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawImage);
        WritePayload(new DrawImageCmd(rect, radii, imageId, ready ? 1 : 0, placeholder, transform, opacity));
        PushSort(sortKey);
    }

    public void StrokeRoundRect(in RectF rect, in CornerRadius4 radii, in ColorF color, float strokeWidth, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawRoundRectStroke);
        WritePayload(new DrawRoundRectStrokeCmd(rect, radii, color, strokeWidth, transform, opacity));
        PushSort(sortKey);
    }

    public void Shadow(in RectF rect, in CornerRadius4 radii, in ColorF color, float offsetX, float offsetY, float blur, float spread, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawShadow);
        WritePayload(new DrawShadowCmd(rect, radii, color, offsetX, offsetY, blur, spread, transform, opacity));
        PushSort(sortKey);
    }

    public void GradientRect(in DrawGradientRectCmd cmd, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawGradientRect);
        WritePayload(cmd);
        PushSort(sortKey);
    }

    public void GradientStroke(in DrawGradientStrokeCmd cmd, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawGradientStroke);
        WritePayload(cmd);
        PushSort(sortKey);
    }

    public void PushLayer(in RectF deviceRect, in CornerRadius4 radii, in ColorF tint, float tintOpacity, float blurSigma, float noiseOpacity, float luminosityOpacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(new PushLayerCmd(deviceRect, radii, tint, tintOpacity, blurSigma, noiseOpacity, luminosityOpacity));
        PushSort(sortKey);
    }

    public void PopLayer(in RectF deviceRect, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PopLayer);
        WritePayload(new PopLayerCmd(deviceRect));
        PushSort(sortKey);
    }

    private void WriteOp(DrawOp op)
    {
        Ensure(sizeof(int));
        int v = (int)op;
        MemoryMarshal.Write(_buf.AsSpan(_len), in v);
        _len += sizeof(int);
        CommandCount++;
    }

    private void WritePayload<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        Ensure(size);
        MemoryMarshal.Write(_buf.AsSpan(_len), in value);
        _len += size;
    }

    private void PushSort(ulong key)
    {
        if (_sortLen == _sort.Length) Array.Resize(ref _sort, _sort.Length * 2);
        _sort[_sortLen++] = key;
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int n = _buf.Length * 2;
        while (n < _len + extra) n *= 2;
        var nb = GC.AllocateUninitializedArray<byte>(n, pinned: false);
        Array.Copy(_buf, nb, _len);
        _buf = nb;
    }
}
