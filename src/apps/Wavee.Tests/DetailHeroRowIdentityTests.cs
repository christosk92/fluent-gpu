using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The detail hero must not RELABEL itself when the full model lands.
///
/// <para>THE DEFECT. A detail page renders twice per open: <c>DetailPage</c> mounts <c>DetailShell</c> immediately from
/// the PARTIAL nav preview model (cover · title · artist), then re-renders from the full model a moment later. Both hero
/// builders emit their rows CONDITIONALLY — eyebrow, owner block, artist face pile, meta line, daylist strip and
/// description exist only in the full model — so the second render INSERTS rows into the middle of the column. The
/// reconciler matches UNKEYED siblings by POSITION + TYPE (<c>Reconciler.ReconcileChildrenCore</c>), so the title's
/// <c>TextEl</c> at index 1 was UPDATED INTO the newly-first eyebrow run and a second <c>TextEl</c> mounted below it for
/// the title: the hero visibly changed its own text and style for a frame, and every row under it jumped.</para>
///
/// <para>THE FIX, AND WHY IT IS PINNED BY SOURCE. Every structural row carries a stable, model-text-free <c>Key</c>, so
/// an inserted row MOUNTS and the existing nodes MOVE. This test project deliberately carries no FluentGpu reference
/// (see <c>Wavee.Tests.csproj</c>: "no FluentGpu/TerraFX/GPU"), so there is no headless harness that can build these
/// element trees and diff two reconciles — the checkable invariant is therefore the SOURCE one, the same technique
/// <see cref="DetailSkeletonGeometryTests"/> uses to pin that both band consumers really call the shared function. A
/// dropped key is exactly the kind of edit that reads as harmless in review, which is what this file is for.</para>
/// </summary>
public class DetailHeroRowIdentityTests
{
    // Every row the two-column rail can emit, in stack order. Membership is the point; order is documentation.
    static readonly string[] RailKeys =
    [
        "rail:cover", "rail:eyebrow", "rail:owner", "rail:title", "rail:artists", "rail:meta",
        "rail:daylist", "rail:cta", "rail:prerelease", "rail:release", "rail:desc",
    ];

    // The vertical (narrow) header arm of the same file.
    static readonly string[] HeaderKeys =
    [
        "hdr:cover", "hdr:eyebrow", "hdr:owner", "hdr:title", "hdr:artists", "hdr:meta",
        "hdr:play", "hdr:prerelease", "hdr:release",
    ];

    // The vertical hero's identity column. Keyed since it was written; pinned here so it stays that way.
    static readonly string[] HeroKeys =
    [
        "hero-eyebrow", "hero-title", "hero-rule", "hero-attribution", "hero-meta",
        "hero-pulse", "hero-actions", "hero-description",
    ];

    // The hero rows that exist ONLY in the full model — the ones whose insert used to rewrite a sibling.
    static readonly string[] HeroLateKeys =
    [
        "hero-eyebrow", "hero-attribution", "hero-meta", "hero-pulse", "hero-description",
    ];

    // ── identity ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryRailRow_CarriesItsStableKey()
    {
        string rail = RailSource();
        if (rail is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        foreach (string key in RailKeys.Concat(HeaderKeys))
            Assert.Contains("\"" + key + "\"", rail);
    }

    [Fact]
    public void EveryVerticalHeroRow_CarriesItsStableKey()
    {
        string hero = HeroSource();
        if (hero is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
        foreach (string key in HeroKeys)
            Assert.Contains("Add(\"" + key + "\"", hero);
    }

    /// <summary>No row list may append an UNKEYED child. The reconciler falls back to position+type the moment one does,
    /// and one unkeyed row is enough to reintroduce the relabel for every row after it.</summary>
    [Fact]
    public void NoRailRowIsAppendedUnkeyed()
    {
        string rail = RailSource();
        if (rail is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        var appends = Regex.Matches(rail, @"\b(kids|info|headerKids)\.Add\(");
        Assert.True(appends.Count >= 15, $"expected the rail/header row appends, found {appends.Count}");
        foreach (Match m in appends)
        {
            string tail = rail.Substring(m.Index, Math.Min(420, rail.Length - m.Index));
            Assert.True(tail.Contains("Row(\"") || tail.Contains("Key = \""),
                $"unkeyed row append at offset {m.Index}: {Collapse(tail)}");
        }
    }

    /// <summary>A row key must be CONSTANT across preview→full. A key built from model text (a title, a uri, a
    /// timestamp) remounts the row the instant that text changes, which is the defect wearing a different hat. The
    /// deliberately identity-encoding keys in these files (<c>save:</c>, <c>daylist:</c>, <c>prerelease:</c>,
    /// <c>vhero-save:</c>, <c>vhero-more:</c>) name components that MUST remount on identity change — they are not row
    /// slots, and they are excluded by prefix.</summary>
    [Fact]
    public void RowKeys_AreConstantNotModelDerived()
    {
        foreach (string src in new[] { RailSource(), HeroSource() })
        {
            if (src is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
            foreach (string line in src.Split('\n'))
            {
                if (line.TrimStart().StartsWith("//")) continue;   // both files DISCUSS the identity keys in prose
                var m = Regex.Match(line, @"\bKey = ([^,\r\n]+)");
                if (!m.Success) continue;
                string value = m.Groups[1].Value.Trim().TrimEnd('}', ')', ';', ' ');   // `… with { Key = "k" });`
                if (value.StartsWith("\"save:") || value.StartsWith("\"daylist:") || value.StartsWith("\"prerelease:")
                    || value.StartsWith("$\"vhero-save:") || value.StartsWith("$\"vhero-more:")
                    || value == "key")   // the hero/rail wrapper's own parameter — checked at its call sites instead
                    continue;
                Assert.True(Regex.IsMatch(value, "^\"[A-Za-z0-9:_-]+\"$"),
                    $"row key is not a constant literal: {value}");
            }
        }
    }

    [Fact]
    public void RowKeys_AreUniqueWithinTheirSibling()
    {
        foreach (string[] set in new[] { RailKeys, HeaderKeys, HeroKeys })
            Assert.Equal(set.Length, set.Distinct(StringComparer.Ordinal).Count());
    }

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The calm-the-shove half: a late row fades up, a pushed row FLIPs, and BOTH are authored on a
    /// <c>BoxEl</c> wrapper — the reconciler bakes Enter/Exit/Layout inside <c>case BoxEl</c> only, so the same fields
    /// on a bare <c>TextEl</c>/<c>ComponentEl</c> are silently dropped and the row would snap.</summary>
    [Fact]
    public void LateRowsFadeUp_PushedRowsFlip_OnABoxWrapper()
    {
        string rail = RailSource();
        if (rail is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        Assert.Contains("EnterExit FadeUp = new(Opacity: 0f, Active: true)", rail);
        Assert.Contains("LayoutTransition Shove = new(", rail);
        Assert.Contains("TransitionChannels.Position, TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut)", rail);

        // Both wrappers are boxes, both take the FLIP, and only LateRow seeds the entrance.
        Assert.Contains("static Element Row(string key, Element child) => new BoxEl", rail);
        Assert.Contains("static Element LateRow(string key, Element child) => new BoxEl", rail);
        Assert.Contains("Key = key, Direction = 1, Layout = Shove, Children = [child],", rail);
        Assert.Contains("Key = key, Direction = 1, Layout = Shove, Enter = FadeUp, Children = [child],", rail);
    }

    /// <summary>Reduced motion is a VALUE handled centrally by the token's KeepFade policy — never an
    /// <c>if (Motion.ReducedMotion)</c> branch in a builder (that changes what is authored between renders, and a hook
    /// or key count that moves with a flag is how the reconciler gets torn). Neither builder may grow one.</summary>
    [Fact]
    public void NeitherBuilderBranchesOnReducedMotion()
    {
        foreach (string src in new[] { RailSource(), HeroSource() })
        {
            if (src is null) { Assert.Skip("app sources not present (binary-only run)"); return; }
            foreach (string line in src.Split('\n'))
            {
                string t = line.TrimStart();
                if (t.StartsWith("//") || t.StartsWith("///")) continue;   // the canon rule is DOCUMENTED in both files
                Assert.DoesNotContain("Motion.ReducedMotion", line);
            }
        }
    }

    /// <summary>The vertical hero shares the rail's two specs (one gesture across the layout cross) and marks exactly
    /// the full-model-only rows late.</summary>
    [Fact]
    public void TheVerticalHeroSharesTheRailsMotionAndMarksItsLateRows()
    {
        string hero = HeroSource();
        if (hero is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        Assert.Contains("Layout = DetailRail.Shove,", hero);
        Assert.Contains("Enter = late ? DetailRail.FadeUp : null,", hero);
        Assert.Contains("void Add(string key, Element? e, bool late = false)", hero);

        foreach (string key in HeroKeys)
        {
            var call = Regex.Match(hero, @"Add\(""" + Regex.Escape(key) + @"""(?<args>.*?)\);", RegexOptions.Singleline);
            Assert.True(call.Success, $"no Add() call for {key}");
            bool late = call.Groups["args"].Value.Contains("late: true");
            Assert.Equal(HeroLateKeys.Contains(key), late);
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────────────

    static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // ── the "About this release" panel ───────────────────────────────────────────────────────────────────────────

    /// <summary>The release facts arrive in WAVES (tracks → full model → publishing hydration), so the panel, every fact
    /// tile, every note and the other-versions dropdown are keyed and own their entrance: the panel fades up once
    /// (its callers wrap it in the FLIP-only <c>Row</c>, never <c>LateRow</c> — no double fade), tiles fade up staggered
    /// and take a Position+Size FLIP so a late Label tile reflows the wrap-grow row instead of snapping it, a value that
    /// re-labels ("2025" → "May 2, 2025") goes through the house text-swap, and reduced motion is a VALUE at the stagger.</summary>
    [Fact]
    public void ReleasePanel_KeysAndAnimatesItsArrivingFacts()
    {
        string trailing = Read("DetailTrailing.cs");
        string rail = RailSource();
        if (trailing is null || rail is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        // the panel owns its entrance; callers wrap it FLIP-only
        Assert.Contains("Key = \"release-panel\", Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,", trailing);
        Assert.Contains("Row(\"rail:release\", AlbumTrailing.ReleasePanel(m, h, outerPadding: false))", rail);
        Assert.Contains("Row(\"hdr:release\", AlbumTrailing.ReleasePanel(m, h, outerPadding: false))", rail);
        Assert.DoesNotContain("LateRow(\"rail:release\"", rail);
        Assert.DoesNotContain("LateRow(\"hdr:release\"", rail);

        // tiles: keyed by fact, fade up, reflow FLIP, staggered by the row, value swap
        foreach (string k in new[] { "\"songs\"", "\"length\"", "\"released\"", "\"label\"" })
            Assert.Contains(k + "));", trailing);
        Assert.Contains("Key = \"fact:\" + key, Enter = DetailRail.FadeUp, Layout = TileReflow,", trailing);
        Assert.Contains("TransitionChannels.Position | TransitionChannels.Size,", trailing);
        Assert.Contains("SizeMode.Reveal);", trailing);
        Assert.Contains("Key = \"v:\" + value,", trailing);
        Assert.Contains("Animate = MotionRecipes.TextSwap,", trailing);
        Assert.Contains("Stagger = stagger, Children = tiles", trailing);
        Assert.Contains("float stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs;", trailing);

        // notes + other versions
        Assert.Contains("NoteText(\"note:courtesy\", courtesy)", trailing);
        Assert.Contains("NoteText(\"note:copyright\", cp)", trailing);
        Assert.Contains("Key = key, Direction = 1, Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,", trailing);
        Assert.Contains("Key = \"release-versions\", Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,", trailing);
    }

    static string RailSource() => Read("DetailRail.cs");
    static string HeroSource() => Read("DetailVerticalHero.cs");

    static string Read(string file)
    {
        string root = AppSourceRoot();
        return root is null ? null! : File.ReadAllText(Path.Combine(root, "Features", "Detail", file));
    }

    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "Features", "Detail", "DetailRail.cs");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "Wavee");
            dir = dir.Parent;
        }
        return null!;
    }
}
