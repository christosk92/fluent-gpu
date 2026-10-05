using System.Buffers.Binary;
using System.IO;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The two engine pieces the Store screenshot export stands on: <see cref="PngWriter"/>'s straight-alpha output (a Mica
/// window's see-through regions must survive into the PNG) and <see cref="SceneStore.IsShown"/> (a keyed-rect dump must
/// not name parked pages or collapsed panes).
/// </summary>
public sealed class CaptureExportTests
{
    [Fact]
    public void Unpremultiply_RestoresStraightColour()
    {
        Span<byte> dst = stackalloc byte[4];
        // 50% white over nothing: premultiplied (128,128,128,128) is straight (255,255,255,128).
        PngWriter.Unpremultiply([128, 128, 128, 128], dst);
        Assert.Equal(new byte[] { 255, 255, 255, 128 }, dst.ToArray());
        // Opaque passes through untouched; fully transparent is transparent black.
        PngWriter.Unpremultiply([10, 20, 30, 255], dst);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, dst.ToArray());
        PngWriter.Unpremultiply([9, 9, 9, 0], dst);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, dst.ToArray());
        // An overshooting blend (colour above alpha) clamps instead of wrapping.
        PngWriter.Unpremultiply([200, 0, 0, 100], dst);
        Assert.Equal(255, dst[0]);
    }

    [Fact]
    public void Unpremultiply_RoundTripsWithinOneStep()
    {
        Span<byte> dst = stackalloc byte[4];
        for (int a = 1; a < 255; a += 7)
            for (int c = 0; c < 256; c += 5)
            {
                byte p = (byte)((c * a + 127) / 255);
                PngWriter.Unpremultiply([p, p, p, (byte)a], dst);
                int back = (dst[0] * a + 127) / 255;
                Assert.True(Math.Abs(back - p) <= 1, $"a={a} c={c} p={p} back={back}");
            }
    }

    [Fact]
    public void WriteBgra_KeepAlpha_WritesAnRgbaHeader_AndTheOpaqueFormStaysRgb()
    {
        byte[] px = [0, 0, 255, 255, 0, 0, 0, 0];   // one opaque red, one transparent pixel
        string dir = Path.Combine(Path.GetTempPath(), "fgpu-png-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string rgba = Path.Combine(dir, "a.png"), rgb = Path.Combine(dir, "b.png");
            PngWriter.WriteBgra(rgba, px, 2, 1, keepAlpha: true);
            PngWriter.WriteBgra(rgb, px, 2, 1);
            byte[] a = File.ReadAllBytes(rgba), b = File.ReadAllBytes(rgb);
            // signature (8) + IHDR length (4) + "IHDR" (4) + width (4) + height (4) + depth (1) -> colour type at 25
            Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(a.AsSpan(16)));
            Assert.Equal(6, a[25]);
            Assert.Equal(2, b[25]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static NodeHandle Node(SceneStore scene, RectF bounds, NodeHandle parent = default)
    {
        NodeHandle n = scene.CreateNode(1);
        scene.Bounds(n) = bounds;
        if (!parent.IsNull) scene.AppendChild(parent, n);
        return n;
    }

    [Fact]
    public void IsShown_NeedsTheRootChain_Visibility_Opacity_AndASize()
    {
        var scene = new SceneStore();
        var root = Node(scene, new RectF(0, 0, 800, 600));
        scene.Root = root;
        var page = Node(scene, new RectF(0, 0, 800, 600), root);
        var badge = Node(scene, new RectF(10, 10, 40, 16), page);
        Assert.True(scene.IsShown(badge));

        // a zero-size box paints nothing
        var empty = Node(scene, new RectF(10, 10, 0, 16), page);
        Assert.False(scene.IsShown(empty));

        // a transparent ancestor hides the subtree
        scene.Paint(page).Opacity = 0f;
        Assert.False(scene.IsShown(badge));
        scene.Paint(page).Opacity = 1f;

        // a collapsed ancestor hides it
        scene.SetCollapsed(page, true);
        Assert.False(scene.IsShown(badge));
        scene.SetCollapsed(page, false);
        scene.Bounds(page) = new RectF(0, 0, 800, 600);
        Assert.True(scene.IsShown(badge));

        // a subtree that is live but not linked to the root (a parked tab) is not shown
        var parked = Node(scene, new RectF(0, 0, 800, 600));
        var parkedBadge = Node(scene, new RectF(10, 10, 40, 16), parked);
        Assert.False(scene.IsShown(parkedBadge));
        Assert.False(scene.IsShown(NodeHandle.Null));
    }
}
