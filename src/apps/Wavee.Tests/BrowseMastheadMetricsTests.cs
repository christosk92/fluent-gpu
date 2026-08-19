using System.IO;
using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Features.Browse;
using Xunit;

namespace Wavee.Tests;

public class BrowseMastheadMetricsTests
{
    [Fact]
    public void Reserve_IsFrameTopPlusTitleLargeLine()
    {
        Assert.Equal(52f, Ui.TitleLarge("x").LineHeight);
        Assert.Equal(BrowseMastheadMetrics.TitleLine, Ui.TitleLarge("x").LineHeight);
        Assert.Equal(BrowseMastheadMetrics.Reserve, Spacing.XXXL + Ui.TitleLarge("x").LineHeight);
    }

    [Fact]
    public void FamilyBodyPad_ClearsTheOverlayThenTheOldBandGap()
    {
        Assert.Equal(BrowseMastheadMetrics.Reserve + Spacing.L, BrowseMastheadMetrics.BodyTop);
        var pad = BrowseMastheadMetrics.FamilyBodyPad(Spacing.L);
        Assert.Equal(Spacing.PageWide, pad.Left);
        Assert.Equal(BrowseMastheadMetrics.BodyTop, pad.Top);
        Assert.Equal(Spacing.PageWide, pad.Right);
        Assert.Equal(Spacing.L, pad.Bottom);
    }

    [Fact]
    public void FamilyPages_UseFamilyBodyPad()
    {
        Assert.Contains("FamilyBodyPad(", ReadAppSource(@"Features\Browse\BrowsePage.cs"));
        Assert.Contains("FamilyBodyPad(", ReadAppSource(@"Features\Browse\BrowseDirectoryPage.cs"));
        Assert.Contains("FamilyBodyPad(", ReadAppSource(@"Features\Home\HomeSectionPage.cs"));
    }

    [Fact]
    public void ContentHost_OverlaysTheMasthead_InsteadOfInFlowHeight()
    {
        string host = ReadAppSource(@"Features\Shell\ContentHost.cs");
        Assert.Contains("ZStack = true", host);
        Assert.Contains("HitTestPassThrough = true", host);
        Assert.Contains("new ShellMastheadBand(_route)", host);
        Assert.DoesNotContain("collapses to Height 0", host);
    }

    [Fact]
    public void MastheadBand_FadesOpacity_InsteadOfSnappingHeight()
    {
        string band = ReadAppSource(@"Features\Shell\ShellMastheadBand.cs");
        Assert.Contains("Opacity = live ? 1f : 0f", band);
        Assert.DoesNotContain("Height = 0f", band);
        Assert.DoesNotContain("static Element Collapsed()", band);
    }

    static string ReadAppSource(string relativePath, [CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        Assert.False(string.IsNullOrEmpty(tests));
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        if (!Directory.Exists(app)) { Assert.Skip("app sources not present (binary-only run)"); return null!; }
        return File.ReadAllText(Path.Combine(app, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
    }
}
