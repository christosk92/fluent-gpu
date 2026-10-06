using FluentGpu.Hosting;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The packer geometry the <c>--fg img-atlas=gpucopy256</c> experiment relies on (256 px cells on 2048 px pages), the
/// default being unchanged, and the switch parser. Serial: the switches are process-wide statics.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class ImageAtlasPackerGeometryTests : System.IDisposable
{
    public ImageAtlasPackerGeometryTests() { EngineSwitches.ImageAtlas = ImageAtlasExperiment.Default; EngineSwitches.ImagePlaced256Query = false; }
    public void Dispose() { EngineSwitches.ImageAtlas = ImageAtlasExperiment.Default; EngineSwitches.ImagePlaced256Query = false; }

    [Fact]
    public void ADefaultPackerStillRefuses256AndKeeps1024Geometry()
    {
        var p = new ImageAtlasPacker(1024, 4L << 20, ImageAtlasUpload.GpuCopy);
        Assert.Equal(ImageAtlasPacker.MaxPackedBucket, p.MaxBucket);
        Assert.False(p.CanPack(256));
        Assert.True(p.CanPack(128));
        Assert.Equal(15, p.CellsPerAxis(64));    // 225 cells per page
        Assert.Equal(7, p.CellsPerAxis(128));    // 49 cells per page
    }

    [Fact]
    public void A256BucketPacksSevenBySevenCellsOnA2048Page()
    {
        var p = new ImageAtlasPacker(2048, 16L << 20, ImageAtlasUpload.GpuCopy, maxPackedBucket: 256);
        Assert.True(p.CanPack(256));
        Assert.False(p.CanPack(512));
        Assert.Equal(7, p.CellsPerAxis(256));    // (2048 - 1) / 257
        int page = p.TryReservePage(256);
        Assert.True(page >= 0);
        var first = p.CommitPage(page);
        Assert.True(first.IsValid);
        Assert.Equal(256, first.Bucket);
        Assert.Equal(1, first.X);                 // one texel of gutter from the page edge
        for (int i = 1; i < 49; i++) Assert.True(p.TryAcquire(256, out var cell) && cell.IsValid, $"cell {i}");
        Assert.False(p.TryAcquire(256, out _));   // the 50th needs a second page
    }

    [Fact]
    public void TheSwitchesParse()
    {
        EngineSwitches.ApplyList("img-atlas=gpucopy256");
        Assert.Equal(ImageAtlasExperiment.GpuCopy256, EngineSwitches.ImageAtlas);
        EngineSwitches.ApplyList("img-atlas=gpucopy");
        Assert.Equal(ImageAtlasExperiment.GpuCopy, EngineSwitches.ImageAtlas);
        EngineSwitches.ApplyList("img-atlas=rowmajor-probe,img-placed=256");
        Assert.Equal(ImageAtlasExperiment.RowMajorProbe, EngineSwitches.ImageAtlas);
        Assert.True(EngineSwitches.ImagePlaced256Query);
        EngineSwitches.ApplyList("img-atlas=nonsense");
        Assert.Equal(ImageAtlasExperiment.RowMajorProbe, EngineSwitches.ImageAtlas);   // an unknown value changes nothing
    }
}
