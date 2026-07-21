namespace FluentGpu.Media;

/// <summary>
/// Portable decode seam (media-pipeline.md §3): turn encoded bytes (JPEG/PNG/WebP/…) into PREMULTIPLIED BGRA8 at a
/// target size, constrained so a huge source never materializes full-res in CPU memory (WIC <c>IWICBitmapScaler</c> on
/// Windows, <c>CGImageSource</c> on macOS). Implemented entirely on a worker thread; the leaf's COM/objc objects never
/// cross the scheduler seam — only POD pixels do.
/// </summary>
public interface IImageCodec
{
    /// <summary>Decode <paramref name="encoded"/> into <paramref name="dstBgra8"/> (premultiplied, row-major, stride
    /// = w*4) at <paramref name="targetW"/>×<paramref name="targetH"/>. <paramref name="dstBgra8"/> is at least
    /// targetW*targetH*4 bytes. Returns false (no throw) if the bytes are not a decodable image; the scheduler maps that
    /// to <c>ImageFailureKind.Decode</c>. Outputs the actual decoded dims — the source is fit WITHIN the target box
    /// preserving its aspect ratio, so the result is ≤ target on each axis (the scheduler honours the returned dims).</summary>
    bool DecodeConstrained(ReadOnlySpan<byte> encoded, int targetW, int targetH, Span<byte> dstBgra8, out int w, out int h);
}
