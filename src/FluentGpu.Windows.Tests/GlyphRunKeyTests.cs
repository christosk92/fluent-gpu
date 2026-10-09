using FluentGpu.Foundation;
using FluentGpu.Rhi.D3D12;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>The shaped-run cache replays baked quads without re-wrapping, so its key must separate every width and
/// origin the shape was baked at: a whole-DIP bucket replayed a 199.6-DIP wrap (two lines) into a box measured at
/// 200.4 DIP (one line).</summary>
public sealed class GlyphRunKeyTests
{
    private static readonly StringId Text = new(7), Family = new(3);

    private static RunKey Key(float maxWidth, int wrap = 1, int trim = 0, float originX = 0f, float topY = 0f)
        => GlyphRenderer.MakeRunKey(Text, Family, 14f, 400, maxWidth, wrap, trim, 0, originX, topY, 1.25f, 0f, float.NaN, 0, 0, 0);

    [Fact]
    public void WrapWidthsInOneWholeDipBucketKeyDistinctRuns()
        => Assert.NotEqual(Key(199.6f), Key(200.4f));

    [Fact]
    public void TrimWidthsInOneWholeDipBucketKeyDistinctRuns()
        => Assert.NotEqual(Key(199.6f, wrap: 0, trim: 1), Key(200.4f, wrap: 0, trim: 1));

    [Fact]
    public void OriginsInOneWholeDipBucketKeyDistinctRuns()
    {
        Assert.NotEqual(Key(200f, originX: 3.6f), Key(200f, originX: 4.4f));
        Assert.NotEqual(Key(200f, topY: 3.6f), Key(200f, topY: 4.4f));
    }

    [Fact]
    public void IdenticalLayoutInputsShareOneRun()
    {
        Assert.Equal(Key(200.4f), Key(200.4f));
        Assert.Equal(Key(float.PositiveInfinity), Key(2e9f));        // unbounded widths share the int.MaxValue bucket
        Assert.Equal(Key(200f, originX: 0f), Key(200f, originX: -0f)); // -0 folds onto +0
    }
}
