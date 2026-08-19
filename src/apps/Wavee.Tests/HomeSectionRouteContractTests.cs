using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// HomeSectionPage must pick its endpoint from the ROUTE PREFIX, never from sniffing the section's own uri.
///
/// <para>THE DEFECT. HomeSectionPage used to decide homeSection vs browseSection with
/// <c>IsHomeSection(uri) =&gt; uri.StartsWith("spotify:section:")</c>. Every Home section AND every hardcoded
/// Browse Charts section (<c>Wavee.Features.Browse.BrowseTaxonomy.ChartSections</c>) carries a
/// <c>spotify:section:</c> uri, so the sniff could not tell them apart — a browse-minted Charts section could
/// reach <c>homeSection</c> instead of <c>browseSection</c> (wrong endpoint, wrong data or a 400), and a
/// "helpful" fallback to the other endpoint on failure would have hidden exactly this mistake.</para>
///
/// <para>THE FIX. The caller's own ROUTE PREFIX (<c>home-section:</c> via <c>HomeSectionRoutes</c> vs
/// <c>browse-section:</c> via <c>BrowseSectionRoutes</c>) selects the endpoint before the section's uri is ever
/// looked at, and there is no fallback in either direction — a route sent to the wrong prefix now fails loudly
/// instead of quietly reading the wrong resource.</para>
///
/// <para>THE TECHNIQUE. This test project carries no FluentGpu reference (see <c>Wavee.Tests.csproj</c>'s own
/// comment) and <c>HomeSectionPage</c> is a FluentGpu <c>Component</c>, so it cannot be mounted here — the
/// checkable invariant is therefore the SOURCE, exactly like <see cref="DetailHeroRowIdentityTests"/> pins keys it
/// cannot exercise through a reconciler. Every assertion below is reformat-resistant on purpose: substring/regex
/// checks never depend on line numbers or exact indentation/wrapping, whitespace is normalised before the
/// wide-span checks, and the "no line mixes both endpoints" check works because
/// <c>"GetSectionAsync"</c> is not a substring of <c>"GetHomeSectionAsync"</c> (the extra "Home" breaks it) — so a
/// reformatted call site still lands the right endpoint name on the right side of the check.</para>
/// </summary>
public class HomeSectionRouteContractTests
{
    [Fact]
    public void HomeSectionPage_NoLongerContainsTheUriSniff()
    {
        string src = HomeSectionPageSource();
        if (src is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        Assert.False(Regex.IsMatch(src, @"\bIsHomeSection\b"), "the deleted uri-sniffing predicate reappeared");
        // The doc-comment's `<c>spotify:section:</c>` mention is prose, not a string literal, and must stay that
        // way — this is exactly the literal the deleted `uri.StartsWith("spotify:section:")` sniff used.
        Assert.DoesNotContain("\"spotify:section:\"", src);
    }

    [Fact]
    public void HomeSectionPage_ReferencesBothRoutePrefixTypes()
    {
        string src = HomeSectionPageSource();
        if (src is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        Assert.Contains("BrowseSectionRoutes", src);
        Assert.Contains("HomeSectionRoutes", src);
    }

    [Fact]
    public void HomeSectionPage_CallsBothEndpoints_AndNoSingleLineMixesThem()
    {
        string src = HomeSectionPageSource();
        if (src is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        Assert.Contains("GetHomeSectionAsync", src);
        Assert.Contains("GetSectionAsync", src);

        // A parser-free way to pin "neither call sits inside the other's branch": no single LINE may mention both
        // endpoint names. Reformatting a call's own argument list keeps the call on its own statement/line, so
        // this survives ordinary re-wrapping.
        foreach (string line in src.Split('\n'))
        {
            bool home = line.Contains("GetHomeSectionAsync");
            bool browse = line.Contains("GetSectionAsync");
            Assert.False(home && browse, $"a single line references both endpoints: {line.Trim()}");
        }
    }

    [Fact]
    public void HomeSectionPage_TheBrowseFlagIsWhatActuallySelectsTheEndpoint()
    {
        string src = HomeSectionPageSource();
        if (src is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        Assert.True(Regex.IsMatch(src, @"\bbool\s+browse\b"), "expected a `bool browse` declaration");
        // The selector shows up BOTH as the uri-resolution ternary (`browse ? … : …`) and as an `if (browse)` /
        // `if (!browse)` guard around the two load paths — whitespace-insensitive so reformatting cannot break it.
        Assert.True(Regex.IsMatch(src, @"browse\s*\?"), "expected a `browse ?` ternary selecting the section uri");
        Assert.True(Regex.IsMatch(src, @"if\s*\(\s*!?\s*browse\s*\)"), "expected an `if (browse)`/`if (!browse)` guard");
    }

    [Fact]
    public void ContentHost_RoutesBothPrefixesToTheSameHomeSectionPageMount()
    {
        string src = ContentHostSource();
        if (src is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        string collapsed = Collapse(src);

        // Both prefixes are tested in ONE condition...
        Assert.Contains("if (HomeSectionRoutes.Is(r.Name) || BrowseSectionRoutes.Is(r.Name))", collapsed);
        // …and that condition's own branch — bounded, non-greedy, so a change elsewhere in the file cannot make
        // this match spuriously — is what mounts HomeSectionPage.
        var m = Regex.Match(collapsed,
            @"if \(HomeSectionRoutes\.Is\(r\.Name\) \|\| BrowseSectionRoutes\.Is\(r\.Name\)\).{0,300}?new HomeSectionPage\(r\)");
        Assert.True(m.Success, "expected the combined-prefix branch to mount HomeSectionPage");
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────────────

    static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    static string HomeSectionPageSource() => Read("Features", "Home", "HomeSectionPage.cs");
    static string ContentHostSource() => Read("Features", "Shell", "ContentHost.cs");

    static string Read(params string[] parts)
    {
        string root = AppSourceRoot();
        return root is null ? null! : File.ReadAllText(Path.Combine(root, Path.Combine(parts)));
    }

    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "Features", "Home", "HomeSectionPage.cs");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "Wavee");
            dir = dir.Parent;
        }
        return null!;
    }
}
