namespace FluentGpu.Media;

/// <summary>
/// The animated half of the decode seam (<c>ImageEl.Animate</c>): an <see cref="IImageCodec"/> leaf that can also play a
/// multi-frame file implements this. <see cref="OpenAnimated"/> returns a SEQUENTIAL frame reader, or null when the bytes are
/// not an animation it composites (a still, a one-frame file, an unsupported container): the caller then keeps the static
/// path. The reader and everything native behind it belong to the ONE thread that opened it (the animation's worker).
/// </summary>
public interface IAnimatedImageCodec
{
    /// <summary>Open <paramref name="length"/> encoded bytes at <paramref name="encoded"/>. The memory must stay valid and
    /// unmoved until the reader is disposed (the caller owns it: a native block, never a managed array). Null = not an
    /// animation this codec plays (fewer than two frames, or a container whose frames need compositing the codec does not
    /// do).</summary>
    unsafe IAnimatedImageReader? OpenAnimated(byte* encoded, int length);
}

/// <summary>A sequential reader over one animated image's FULL, COMPOSITED frames (each frame the whole canvas, as it is
/// shown). Frames decode at the file's own size: a codec-side scaler pass costs several frame budgets at video sizes
/// (measured: WIC Fant 1440x810 to 1200 wide, 40-60 ms a frame on a Snapdragon X Elite against 10-12 ms for the decode
/// itself), so an animation is authored at the size it shows and the GPU samples it down. Thread-confined to its opener.</summary>
public interface IAnimatedImageReader : System.IDisposable
{
    int Width { get; }
    int Height { get; }
    int FrameCount { get; }
    /// <summary>How many times the file asks to be played; 0 = forever.</summary>
    int LoopCount { get; }
    /// <summary>Decode frame <paramref name="index"/> (0..FrameCount-1) as premultiplied BGRA8, stride Width*4, into
    /// <paramref name="dst"/> (at least Width*Height*4 bytes). Cheap only in order (frame i after i-1, or 0 after the last):
    /// a jump re-composites from the start. <paramref name="durationMs"/> is the frame's own display time (a 0 or missing
    /// duration reads 100 ms, the browsers' rule for a zero delay).</summary>
    bool TryDecode(int index, System.Span<byte> dst, out int durationMs);
}
