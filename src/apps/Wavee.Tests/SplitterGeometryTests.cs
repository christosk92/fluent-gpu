using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The column splitter is a GRAB TARGET, and the app has four of them (three library seams + the detail rail). They ship
/// the stock GridSplitter model — a wide, invisible-at-rest hit strip with a reveal-on-hover indicator — which the
/// sidebar's own grip already used at 16 DIP while the column grips sat at 7 (and 12 while collapsed), i.e. under half
/// the pointer target and with no touch story at all.
///
/// <para>The engine ships no splitter control of its own (FluentGpu.Controls has ScrollBar / Slider / SplitView, but no
/// Splitter or Sizer), so <c>ColumnGrip</c> IS the app's splitter and its <c>StripW</c> is the one width. These pins
/// exist because the width lives in a wrapper at each call site — the component cannot enforce its own hit area.</para>
/// </summary>
public class SplitterGeometryTests
{
    /// <summary>16 DIP: the sidebar grip's width, the Toolkit GridSplitter's default grip band, and the smallest strip
    /// an edge drag can be aimed at without care. Never narrower. (A source pin, not a value pin: <c>ColumnGrip</c>
    /// lives inside the LibraryPage UI component, which this test project deliberately does not compile — only the pure
    /// layout/model files are linked in.)</summary>
    [Fact]
    public void SplitterStrip_IsTheSixteenDipGrabTarget()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string library = File.ReadAllText(Path.Combine(root, "Features", "Library", "LibraryPage.cs"));
        Assert.Contains("public const float StripW = 16f;", library);
        // The sidebar's own grip is the precedent this width comes from — it must not drift below it either.
        string sidebar = File.ReadAllText(Path.Combine(root, "Features", "Sidebar", "SidebarResize.cs"));
        Assert.Contains("Width = 16f", sidebar);
    }

    /// <summary>Every splitter wrapper routes through <see cref="ColumnGrip.StripW"/> rather than re-authoring a width,
    /// so the four seams cannot drift apart again. (A wrapper's Width is a plain float on an engine record — there is
    /// no compile-time seam to enforce it, hence the source scan.)</summary>
    [Fact]
    public void EverySplitterWrapper_UsesTheSharedStripWidth()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string library = File.ReadAllText(Path.Combine(root, "Features", "Library", "LibraryPage.cs"));
        string detail = File.ReadAllText(Path.Combine(root, "Features", "Detail", "DetailShell.cs"));

        // The library's three seams share ONE wrapper factory, which must take the shared width.
        Assert.Contains("Width = ColumnGrip.StripW", library);
        // The detail rail's strip resolves the expanded width from the same constant.
        Assert.Contains("ColumnGrip.StripW", detail);
        // The superseded 7-DIP strip is gone from both.
        Assert.DoesNotContain("GripStripW = 7f", detail);
    }

    /// <summary>The indicator is a REVEAL, not a permanently painted hairline: it must be opacity-driven, because a
    /// fill-only child is not driven by its container's hover (AnimScheduler.SetHoverDescendants cascades only to
    /// HoverOpacity / Hover-PressScale nodes) — the old <c>HoverFill = Tok.TextTertiary</c> hairline therefore only lit
    /// when the pointer was over the 1-DIP line itself, leaving most of the strip dead to the cue. It also must not be
    /// the accent: a splitter is structure, and WaveeAccent's rule (b) says accent is never structure.</summary>
    [Fact]
    public void SplitterIndicator_IsAnOpacityRevealOnANeutralGrabToken()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string library = File.ReadAllText(Path.Combine(root, "Features", "Library", "LibraryPage.cs"));
        int grip = library.IndexOf("sealed class ColumnGrip", StringComparison.Ordinal);
        Assert.True(grip >= 0, "ColumnGrip moved — re-point this gate");
        string body = library[grip..];

        Assert.Contains("Opacity = 0f, HoverOpacity = 1f, PressedOpacity = 1f", body);
        Assert.Contains("Fill = Tok.FillControlStrong", body);
        Assert.DoesNotContain("HoverFill = Tok.TextTertiary", body);
        Assert.DoesNotContain("Fill = Tok.AccentDefault", body);
    }

    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
