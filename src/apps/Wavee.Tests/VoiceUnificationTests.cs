using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>Wave 3 of the design-system convergence: the VOICE gates. Waves 1 and 2 put the app on one type ramp and
/// one motion ladder; what was still speaking with several voices was the app's shared IDIOMS — the same thing said
/// four ways on four surfaces. These tests pin the three that are checkable as values or as source facts:
/// <list type="bullet">
///   <item><b>the eyebrow</b> — one rung, one weight, ONE tracking, and no caps transform anywhere;</item>
///   <item><b>the empty state</b> — a display-face headline and a QUIET action, never an accent one;</item>
///   <item><b>zebra striping</b> — a DERIVED token off the subtle-fill ladder, quieter than hover by construction, and
///   confined to long tracklists.</item>
/// </list></summary>
public class VoiceUnificationTests
{
    // ── The eyebrow ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE tracking, owned by the alias. Before this wave the eyebrow role carried NINE different letterspacing
    /// values across 58 call sites (10, 20, 30, 32, 40, 50, 60, 70, 80, 120) — a ladder nobody designed, which is why
    /// two eyebrows stacked on one page never looked like the same label.</summary>
    [Fact]
    public void EyebrowAlias_OwnsTheOneTracking()
    {
        Assert.Equal(30f, WaveeType.EyebrowTracking);
        var el = WaveeType.Eyebrow("x");
        Assert.Equal(WaveeType.EyebrowTracking, el.CharSpacing);

        // …and it is still the Caption rung at Semibold: the tracking rides ON the ramp, it does not replace it.
        Assert.Equal(12f, el.Size);
        Assert.Equal(16f, el.LineHeight);
        Assert.Equal((ushort)600, el.ResolvedWeight);
    }

    /// <summary>The alias carries no COLOUR of its own: an accent reason, a tertiary kind tag and an on-accent badge are
    /// the same type at three jobs, and the accent arm is deliberate identity (see <c>WaveeAccent</c>). Metrics and
    /// tracking belong to the alias; colour belongs to the call site.</summary>
    [Fact]
    public void EyebrowAlias_LeavesColourToTheCallSite()
        => Assert.Equal(Ui.Caption("x").Color, WaveeType.Eyebrow("x").Color);

    /// <summary>No call site may re-track an eyebrow — or letterspace anything else positively. Positive tracking in
    /// this app means exactly one thing (the eyebrow role), so it lives in exactly one place; the NEGATIVE tracking on
    /// the display-face aliases is a different job and is untouched.</summary>
    [Fact]
    public void NoSourceFile_AuthorsAPositiveTrackingLiteral()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        var offenders = new List<string>();
        foreach (string path in AppSources(root))
        {
            string name = Path.GetFileName(path);
            if (name == "WaveeType.cs") continue;                       // the aliases themselves
            if (Array.IndexOf(SanctionedTracking, name) >= 0) continue; // named exceptions, below

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = PositiveTracking.Match(lines[i]);
                if (m.Success) offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "positive CharSpacing belongs to WaveeType.Eyebrow and nowhere else:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The ONE sanctioned positive-tracking site: the login screen's device-pairing CODE. It is not a label at
    /// all — it is a string of characters a human reads back one glyph at a time, and letterspacing is what makes that
    /// possible. Named out loud so it stays a decision.</summary>
    static readonly string[] SanctionedTracking = ["LoginView.cs"];

    static readonly Regex PositiveTracking = new(@"CharSpacing\s*=\s*[0-9]+(\.[0-9]+)?f", RegexOptions.Compiled);

    /// <summary>Case is not part of the voice — and a <c>.ToUpper()</c> over a LOCALIZED string is not a style choice,
    /// it is a bug: it mangles Turkish dotted i, expands German ß, and (Home's greeting, the site that started this)
    /// shouts the user's own display name back at them. No eyebrow may caps-transform its text.</summary>
    [Fact]
    public void NoEyebrow_CapsTransformsItsText()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        var offenders = new List<string>();
        foreach (string path in AppSources(root))
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("WaveeType.Eyebrow(", StringComparison.Ordinal)
                    && lines[i].Contains("ToUpper", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "an eyebrow takes its string's OWN casing:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The alias is actually REACHED. Deleting every eyebrow would also satisfy the two gates above, so this
    /// pins the other half of the decision: the role stays, it is just spoken one way.</summary>
    [Fact]
    public void TheAppSpeaksTheEyebrow()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        int sites = 0, files = 0;
        foreach (string path in AppSources(root))
        {
            if (Path.GetFileName(path) == "WaveeType.cs") continue;
            string text = File.ReadAllText(path);
            int at = 0, here = 0;
            while ((at = text.IndexOf("WaveeType.Eyebrow(", at, StringComparison.Ordinal)) >= 0) { here++; at += 18; }
            if (here > 0) { files++; sites += here; }
        }

        Assert.True(sites >= 50, $"only {sites} eyebrow call sites — the conversion regressed");
        Assert.True(files >= 25, $"only {files} files speak the eyebrow — the conversion regressed");
    }

    // ── The empty state ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE empty-state grammar: a display-face headline (Title at page scale, Subtitle in a rail), an optional
    /// caption, and at most ONE QUIET action. The action must be <c>Button.Standard</c>: the accent budget's action
    /// rung belongs to the page's real primary, and an empty library must not shout louder than a full one.
    /// <para>A SOURCE pin rather than an element-tree pin because <c>EmptyState</c> depends on
    /// <c>FluentGpu.Controls.Button</c>, which this deliberately engine-light test assembly does not reference.</para></summary>
    [Fact]
    public void EmptyState_IsBigTypeAndAQuietAction()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string src = File.ReadAllText(Path.Combine(root, "Components", "EmptyState.cs"));

        // The two scales, and only two.
        Assert.Contains("WaveeType.PageHero(title)", src);   // page scale — 28 / 36 / 600
        Assert.Contains("Ui.Subtitle(title)", src);          // rail scale — 20 / 28 / 600
        Assert.Contains("public static Element Compact(", src);

        // The quiet action.
        Assert.Contains("Button.Standard(", src);
        Assert.DoesNotContain("Button.Accent(", src);

        // NO decorative glyph, and no `glyph:` parameter left for a call site to pass one through.
        Assert.DoesNotContain("Icon(glyph", src);
        Assert.DoesNotContain("string glyph", src);
    }

    /// <summary>An error is an empty state with a reason, so it is the SAME grammar — it must route THROUGH the
    /// component rather than re-authoring a parallel one (which is how it grew its own 32-DIP critical glyph and its
    /// own accent Retry).</summary>
    [Fact]
    public void ErrorState_RoutesThroughTheSameGrammar()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string src = File.ReadAllText(Path.Combine(root, "Components", "ErrorState.cs"));
        Assert.Contains("EmptyState.Build(", src);
        Assert.DoesNotContain("Button.Accent(", src);
        Assert.DoesNotContain("Icons.Cancel", src);
    }

    // ── Zebra ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stripe is DERIVED, not three hand-picked alphas per theme: it is the engine's subtle-fill ink at its
    /// quietest rung. That drops the light/dark branch (the subtle ladder already flips black ink for white) and makes
    /// the ordering below structural instead of coincidental.</summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void RowZebra_IsTheSubtleFillLadder(ThemeKind theme)
    {
        WithTheme(theme, () => Assert.Equal(Tok.FillSubtleTertiary, WaveeColors.RowZebra));
    }

    /// <summary>The invariant the old literals BROKE in dark, where the stripe (0x0F) was exactly the hover fill: a
    /// stripe must be quieter than hover, or a striped row has no hover at all.</summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void RowZebra_IsQuieterThanHover(ThemeKind theme)
    {
        WithTheme(theme, () =>
            Assert.True(WaveeColors.RowZebra.A < WaveeColors.RowHover.A,
                $"{theme}: zebra α {WaveeColors.RowZebra.A} is not below hover α {WaveeColors.RowHover.A}"));
    }

    /// <summary>Hover/press ON a stripe is the row state SOURCE-OVER the stripe, collapsed to ONE translucent fill —
    /// a row paints a single <c>Fill</c>, never two stacked plates. <c>ColorContrast.Over</c> is associative, so the
    /// merged rung composites pixel-identically to painting the two rungs in sequence.</summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void ZebraStates_AreTheRowStateOverTheStripe(ThemeKind theme)
    {
        WithTheme(theme, () =>
        {
            Assert.Equal(ColorContrast.Over(WaveeColors.RowHover, WaveeColors.RowZebra), WaveeColors.RowHoverZebra);
            Assert.Equal(ColorContrast.Over(WaveeColors.RowPressed, WaveeColors.RowZebra), WaveeColors.RowPressedZebra);

            // Compositing can only ADD coverage: an interacted stripe is never lighter than the stripe alone.
            Assert.True(WaveeColors.RowHoverZebra.A > WaveeColors.RowZebra.A);
            Assert.True(WaveeColors.RowPressedZebra.A > WaveeColors.RowZebra.A);
        });
    }

    /// <summary>Zebra is a scanning aid for lists long enough to lose your place in — the detail tracklist and the
    /// shared track row, and nothing else. The queue and the friends rail show a handful of rows in a 340-DIP panel,
    /// where the stripe was texture rather than navigation (and halved the contrast hover had left to move against).</summary>
    [Fact]
    public void Zebra_IsConfinedToTheLongTracklists()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        var offenders = new List<string>();
        foreach (string path in AppSources(root))
        {
            string name = Path.GetFileName(path);
            if (Array.IndexOf(ZebraOwners, name) >= 0) continue;
            string text = File.ReadAllText(path);
            if (text.Contains("WaveeColors.RowZebra", StringComparison.Ordinal)
                || text.Contains("WaveeColors.RowHoverZebra", StringComparison.Ordinal)
                || text.Contains("WaveeColors.RowPressedZebra", StringComparison.Ordinal))
                offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "zebra striping belongs to the long tracklists only:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The long tracklists (plus the token layer that defines the rungs).</summary>
    static readonly string[] ZebraOwners = ["WaveeTokens.cs", "TrackRow.cs", "DetailTracks.cs"];

    // ── The accent budget's two hard rules ───────────────────────────────────────────────────────────────────────

    /// <summary>Rule (a): a decorative ornament may not wear SELECTION geometry. The artist section header's 3 × 22
    /// r1.5 accent capsule was pixel-for-pixel the selection-indicator shape (a short accent bar flush against a
    /// control's edge) doing a decorative job, eight times down one page. It is now a 20 × 2 rule UNDER the header.</summary>
    [Fact]
    public void SectionOrnament_IsARuleNotASelectionBar()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string surfaces = File.ReadAllText(Path.Combine(root, "Design", "Surfaces.cs"));
        Assert.Contains("AccentRuleWidth = 20f", surfaces);
        Assert.Contains("AccentRuleHeight = 2f", surfaces);
        Assert.Contains("public static BoxEl AccentRule(", surfaces);
        Assert.DoesNotContain("Radii.Circle(3f)", surfaces);

        // The artist page's counted header shares the ONE ornament instead of re-authoring the capsule.
        string sections = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistPage.Sections.cs"));
        Assert.Contains("Surfaces.AccentRule(", sections);
        Assert.DoesNotContain("CornerRadius4.All(1.5f)", sections);
    }

    /// <summary>Rule (b): accent is never STRUCTURE. A border, a divider, a chevron or a disclosure glyph is chrome and
    /// takes a stroke/secondary token — the concert surfaces' dashed accent border and accent chevrons were the app's
    /// single largest source of ambient accent.</summary>
    [Fact]
    public void AccentIsNeverStructure_OnTheConcertSurfaces()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present next to the test sources — source gate inconclusive"); return; }

        string src = File.ReadAllText(Path.Combine(root, "Components", "ConcertUi.cs"));
        Assert.DoesNotContain("BorderColor = Tok.AccentTextPrimary", src);
        Assert.DoesNotContain("Icons.ChevronRight, 14f, Tok.AccentTextPrimary", src);
        Assert.DoesNotContain("Icons.ChevronDown, 10f, Tok.AccentTextPrimary", src);
    }

    /// <summary>The three accent ROLES exist as named values, so a paint site can declare which one it is playing
    /// rather than reaching for <c>Tok.AccentDefault</c> and meaning any of the three.</summary>
    [Fact]
    public void AccentRoles_AreNamed()
    {
        Assert.Equal(Tok.AccentDefault, WaveeAccent.Action);
        Assert.Equal(Tok.AccentDefault, WaveeAccent.Selection);
        Assert.Equal(Tok.AccentTextPrimary, WaveeAccent.Decor);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    static void WithTheme(ThemeKind theme, Action body)
    {
        var was = Tok.Theme;
        try { Tok.Use(theme); body(); }
        finally { Tok.Use(was); }
    }

    static IEnumerable<string> AppSources(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            yield return path;
        }
    }

    /// <summary>src/apps/Wavee, located from THIS file's compile-time path — the test sources and the app sources are
    /// siblings in the repo. Null when the sources are not on disk (a binary-only run).</summary>
    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
