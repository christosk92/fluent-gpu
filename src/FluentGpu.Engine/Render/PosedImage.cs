using System.Runtime.InteropServices;
using FluentGpu.Rhi;

namespace FluentGpu.Render;

/// <summary>
/// The stream-side half of a <see cref="CompositeKind.Image"/> item (BoxEl.CompositePose): a posed image layer's slice stream
/// is exactly one plain <see cref="DrawOp.DrawImage"/> (optionally under rectangular clips) — the recorder proved it
/// (<c>SliceRecorder</c>'s eligibility scan); a backend or the headless model reads the op back here to draw the quad.
/// </summary>
public static class PosedImage
{
    /// <summary>The first <see cref="DrawImageCmd"/> in <paramref name="stream"/>; false when it holds none.</summary>
    public static bool Find(ReadOnlySpan<byte> stream, out DrawImageCmd image)
    {
        int pos = 0;
        while (pos + sizeof(int) <= stream.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(stream[pos..]);
            int payload = pos + sizeof(int);
            if (!RepaintStreamSafety.TryBodySize(op, out int body) || payload + body > stream.Length) break;
            if (op == DrawOp.DrawImage)
            {
                image = MemoryMarshal.Read<DrawImageCmd>(stream.Slice(payload, body));
                return true;
            }
            pos = payload + body;
        }
        image = default;
        return false;
    }
}
