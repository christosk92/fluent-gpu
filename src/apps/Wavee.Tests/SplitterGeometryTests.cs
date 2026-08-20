using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The column splitter is a GRAB TARGET. Every seam (library columns, detail rail, both shell rails) routes through
/// <c>FluentGpu.Controls.Splitter</c> — a 16-DIP invisible-at-rest hit strip with an optional reveal-on-hover
/// indicator. These pins exist because wrapper Widths are plain floats on engine records; there is no compile-time
/// seam to keep the four call sites on the same strip.
/// </summary>
public class SplitterGeometryTests
{
    [Fact]
    public void SplitterStrip_IsTheSixteenDipGrabTarget()
    {
        string controls = ControlsSplitter();
        if (controls is null) { Assert.Skip("Splitter.cs not present next to the test sources"); return; }
        Assert.Contains("public const float StripW = 16f;", controls);
        Assert.DoesNotContain("Width = 16f", controls); // the strip is the named const, never a local literal
    }

    [Fact]
    public void EverySplitterWrapper_UsesTheSharedStripWidth()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string library = File.ReadAllText(Path.Combine(root, "Features", "Library", "LibraryPage.cs"));
        string detail = File.ReadAllText(Path.Combine(root, "Features", "Detail", "DetailShell.cs"));
        string shell = File.ReadAllText(Path.Combine(root, "Features", "Shell", "WaveeShell.cs"));

        Assert.Contains("Width = Splitter.StripW", library);
        Assert.Contains("Splitter.StripW", detail);
        Assert.Contains("Splitter.StripW", shell);

        string rail = File.ReadAllText(Path.Combine(root, "Features", "Player", "RightRail.cs"));
        Assert.Contains("SplitterAxis.Vertical", rail);
        Assert.Contains("ShowIndicator = false", rail);
        Assert.DoesNotContain("GripStripW = 7f", detail);
        Assert.DoesNotContain("sealed class ColumnGrip", library);
        Assert.DoesNotContain("sealed class SidebarResizeGrip", shell);
    }

    [Fact]
    public void SplitterIndicator_IsAnOpacityRevealOnANeutralGrabToken()
    {
        string controls = ControlsSplitter();
        if (controls is null) { Assert.Skip("Splitter.cs not present next to the test sources"); return; }

        Assert.Contains("Opacity = 0f, HoverOpacity = 1f, PressedOpacity = 1f", controls);
        Assert.Contains("IndicatorFill = Tok.FillControlStrong", controls);
        Assert.DoesNotContain("HoverFill = Tok.TextTertiary", controls);
        Assert.DoesNotContain("Fill = Tok.AccentDefault", controls);
    }

    [Fact]
    public void SplitterStrip_UsesSizeNSOnTheVerticalAxis()
    {
        string controls = ControlsSplitter();
        if (controls is null) { Assert.Skip("Splitter.cs not present next to the test sources"); return; }
        Assert.Contains("CursorId.SizeNS", controls);
        Assert.Contains("Height = vertical ? Splitter.StripW : float.NaN", controls);
        Assert.Contains("local.Y + r.Y", controls);
    }

    [Fact]
    public void DockedVideoCap_IsFullBleedAndVerticallySized()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string docked = File.ReadAllText(Path.Combine(root, "Features", "Video", "DockedVideoSurface.cs"));
        Assert.DoesNotContain("Margin = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f)", docked);
        Assert.DoesNotContain("MediaStretch.UniformToFill", docked);
        Assert.Contains("AreTransportControlsEnabled = cap", docked);
        Assert.Contains("VideoPlacementMenu.Items", docked);
        Assert.Contains("Stretch = MediaStretch.Uniform", docked);

        string rail = File.ReadAllText(Path.Combine(root, "Features", "Player", "RightRail.cs"));
        Assert.Contains("Height = ui.DockedVideoHeight", rail);
        Assert.Contains("SplitterAxis.Vertical", rail);
        Assert.DoesNotContain("Prop.Of(() => ShellResponsiveLayout.ClampDockedVideoHeight", rail);
    }

    static string? ControlsSplitter([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null;
        string repo = Path.GetFullPath(Path.Combine(tests, "..", "..", ".."));
        string path = Path.Combine(repo, "src", "FluentGpu.Controls", "Splitter.cs");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
