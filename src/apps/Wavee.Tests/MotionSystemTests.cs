using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentGpu.Dsl;
using Xunit;

namespace Wavee.Tests;

/// <summary>Wave 2 of the design-system convergence: the MOTION gates. Hover/press motion everywhere is a deliberate
/// Wavee identity and is kept — but the audit found it authored as 69 call sites across 20 distinct scale values
/// (1.005 → 1.16 hover, 0.625 → 0.997 press), with only about five of them honouring reduced motion. These tests pin
/// the SYSTEM that replaced it:
/// <list type="bullet">
///   <item>exactly three interaction tiers, monotonically ordered, one press value per tier;</item>
///   <item>every tier accessor collapses to 1f under reduced motion — the property no call site can forget;</item>
///   <item>the duration ladder is the WinUI Common_themeresources ladder and nothing else;</item>
///   <item>no source file outside the vocabulary may author a raw hover/press scale again.</item>
/// </list></summary>
public class MotionSystemTests
{
    /// <summary>The three tiers by name, so a new tier cannot be added without landing in every gate below.</summary>
    public static TheoryData<string, float, float> Tiers() => new()
    {
        // name        hover   press
        { "Subtle",    1.02f,  0.98f },
        { "Standard",  1.04f,  0.96f },
        { "Emphatic",  1.07f,  0.92f },
    };

    static ScaleTier Tier(string name) => name switch
    {
        "Subtle" => WaveeMotion.ScaleSubtle,
        "Standard" => WaveeMotion.ScaleStandard,
        "Emphatic" => WaveeMotion.ScaleEmphatic,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown tier"),
    };

    // ── The tiers ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The values themselves. Standard's 1.04 is the value <c>WaveeCta</c>'s media pill contributed (it was
    /// the app's only already-systematic pair); its press deepened from that skin's local 0.97 to the ladder's 0.96 so
    /// there is exactly ONE press value per tier.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void EveryTier_CarriesItsPinnedPair(string name, float hover, float press)
    {
        var t = Tier(name);
        Assert.Equal(hover, t.HoverTarget);
        Assert.Equal(press, t.PressTarget);
    }

    /// <summary>A tier grows on hover and shrinks on press — never the reverse, never a no-op. A tier that resolved to
    /// 1f in either direction is a dead cue, which is exactly the <c>1f : 1f</c> defect the register logs as D4.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void EveryTier_GrowsOnHoverAndShrinksOnPress(string name, float hover, float press)
    {
        _ = hover; _ = press;
        var t = Tier(name);
        Assert.True(t.HoverTarget > 1f, $"{name} hover {t.HoverTarget} does not grow");
        Assert.True(t.PressTarget < 1f, $"{name} press {t.PressTarget} does not shrink");
    }

    /// <summary>The ladder is monotonic in BOTH directions: louder tier ⇒ more hover growth AND more press push. Three
    /// rungs, strictly ordered — so "which tier is louder" is answerable without reading the values.</summary>
    [Fact]
    public void TheThreeTiers_AreAStrictlyOrderedLadder()
    {
        var s = WaveeMotion.ScaleSubtle;
        var m = WaveeMotion.ScaleStandard;
        var e = WaveeMotion.ScaleEmphatic;

        Assert.True(s.HoverTarget < m.HoverTarget, "Subtle must grow less than Standard");
        Assert.True(m.HoverTarget < e.HoverTarget, "Standard must grow less than Emphatic");
        Assert.True(s.PressTarget > m.PressTarget, "Subtle must push less than Standard");
        Assert.True(m.PressTarget > e.PressTarget, "Standard must push less than Emphatic");

        // Sub-perceptual rungs are what the sweep DELETED (the 1.005/0.997 tour banner): every rung must clear the
        // recorder's own cull threshold (SceneRecorder skips the transform below |scale-1| = 0.0008) by a wide margin.
        foreach (var t in new[] { s, m, e })
        {
            Assert.True(MathF.Abs(t.HoverTarget - 1f) > 0.01f, "a tier below 1% is sub-perceptual, not a tier");
            Assert.True(MathF.Abs(t.PressTarget - 1f) > 0.01f, "a tier below 1% is sub-perceptual, not a tier");
        }
    }

    // ── Reduced motion ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE gate this wave exists for. The engine seeds hover/press progress through
    /// <c>AnimScheduler.SeedEased</c>, which — unlike <c>SeedMotion</c>/<c>KeyframesMotion</c> — carries no
    /// <c>ReducedMotionPolicy</c>, and <c>SceneRecorder</c> composites <c>1 + (HoverScale-1)·HoverT</c>
    /// unconditionally. So the interaction scale is the one animated channel the engine does NOT suppress, and the
    /// suppression has to be a property of the app's authored VALUE. Reading it in the accessor (never at the call
    /// site) is what makes it unforgettable — and returning exactly 1f is what makes the recorder skip the transform
    /// rather than animate to a visually-identical one.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void EveryTier_CollapsesToIdentity_UnderReducedMotion(string name, float hover, float press)
    {
        _ = hover; _ = press;
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            var t = Tier(name);
            Assert.Equal(t.HoverTarget, t.Hover);
            Assert.Equal(t.PressTarget, t.Press);

            Motion.ReducedMotion = true;
            Assert.Equal(1f, t.Hover);
            Assert.Equal(1f, t.Press);
            Assert.Equal(1f, t.HoverIf(true));
            Assert.Equal(1f, t.PressIf(true));
        }
        finally { Motion.ReducedMotion = saved; }
    }

    /// <summary>The gated overloads: a dead affordance (a disabled transport button, an unavailable filter chip) must
    /// not answer the pointer at all, and must do so without a second reduced-motion read at the call site.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void GatedAccessors_AreIdentityWhenDisabled(string name, float hover, float press)
    {
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            var t = Tier(name);
            Assert.Equal(1f, t.HoverIf(false));
            Assert.Equal(1f, t.PressIf(false));
            Assert.Equal(hover, t.HoverIf(true));
            Assert.Equal(press, t.PressIf(true));
        }
        finally { Motion.ReducedMotion = saved; }
    }

    // ── The duration ladder ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three rungs, the real WinUI Common_themeresources durations, strictly ordered. The sweep snapped
    /// 100/120/140/150/180 ms of hand-picked interaction timing onto them; anything that needs a different number is a
    /// STRUCTURAL transition (a page/pane/flyout tween) and deliberately does not live on this ladder.</summary>
    [Fact]
    public void DurationLadder_IsTheWinUiLadder()
    {
        Assert.Equal(83f, WaveeMotion.Faster);
        Assert.Equal(167f, WaveeMotion.Fast);
        Assert.Equal(250f, WaveeMotion.Standard);
        Assert.True(WaveeMotion.Faster < WaveeMotion.Fast);
        Assert.True(WaveeMotion.Fast < WaveeMotion.Standard);

        // Faster is the engine's own brush-transition duration — the app must not mint a second value for it.
        Assert.Equal(Motion.ControlFaster, WaveeMotion.Faster);
        Assert.Equal(Motion.ControlFast, WaveeMotion.Fast);
        Assert.Equal(Motion.ControlNormal, WaveeMotion.Standard);
    }

    /// <summary>The stagger rung survives the "no unused tokens" rule as a DECLARED forward reference (Wave 5 wires the
    /// list/shelf entrance choreography). Pinned so the value is decided once, here, rather than re-picked there.</summary>
    [Fact]
    public void StaggerRung_IsOneDecidedValue() => Assert.Equal(40f, WaveeMotion.StaggerMs);

    // ── The source gate ──────────────────────────────────────────────────────────────────────────────────────────

    // Sites that may keep a literal, each for a stated reason. Anything else authoring a raw hover/press scale is
    // exactly the drift this wave removed.
    static readonly (string File, string Reason)[] SanctionedScaleLiterals =
    [
        ("DetailTracks.cs", "10f/16f is WinUI's selection-pill geometry ratio (ListViewItem parity), not an interaction tier"),
        ("WaveeMotion.cs",  "the vocabulary itself"),
    ];

    static readonly Regex RawScale = new(@"\b(Hover|Press)Scale\s*=\s*[0-9]", RegexOptions.Compiled);

    /// <summary>No file may author a raw hover/press scale again. This is the gate that makes the convergence durable:
    /// a new surface can only get a scale cue by naming a tier, and naming a tier drags reduced-motion safety along
    /// with it. (Compile-time enforcement is impossible — <c>BoxEl.HoverScale</c> is a plain float on an engine
    /// record — so the enforcement is a source scan, which is also why it names its exceptions out loud.)</summary>
    [Fact]
    public void NoSourceFile_AuthorsARawHoverOrPressScale()
    {
        string root = AppSourceRoot();
        if (root is null)
        {
            Assert.Skip("app sources not present next to the test sources (binary-only run) — source gate inconclusive");
            return;
        }

        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            string name = Path.GetFileName(path);
            bool sanctioned = Array.Exists(SanctionedScaleLiterals, s => s.File == name);

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!RawScale.IsMatch(lines[i])) continue;
                if (sanctioned) continue;
                offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "raw hover/press scale literals must go through a WaveeMotion tier:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The tier vocabulary is actually REACHED: the sweep must have left the app reading tiers, not merely
    /// stopped it writing literals (deleting every cue would also pass the gate above). Pins the identity decision —
    /// the hover motion stays, it is just systematised.</summary>
    [Fact]
    public void TheAppReadsEveryTier()
    {
        string root = AppSourceRoot();
        if (root is null)
        {
            Assert.Skip("app sources not present next to the test sources (binary-only run) — source gate inconclusive");
            return;
        }

        var counts = new Dictionary<string, int> { ["ScaleSubtle"] = 0, ["ScaleStandard"] = 0, ["ScaleEmphatic"] = 0 };
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || Path.GetFileName(path) == "WaveeMotion.cs")
                continue;

            string text = File.ReadAllText(path);
            foreach (string tier in new[] { "ScaleSubtle", "ScaleStandard", "ScaleEmphatic" })
            {
                int at = 0;
                while ((at = text.IndexOf("WaveeMotion." + tier, at, StringComparison.Ordinal)) >= 0)
                {
                    counts[tier]++;
                    at += tier.Length;
                }
            }
        }

        foreach (var (tier, n) in counts)
            Assert.True(n > 0, $"no call site reads WaveeMotion.{tier} — the tier is dead, or the sweep dropped a cue");

        // WaveeCta — the app's one primary-CTA skin — must be ON the ladder, not carrying private constants.
        string cta = File.ReadAllText(Path.Combine(root, "Design", "WaveeCta.cs"));
        Assert.Contains("WaveeMotion.ScaleStandard.Hover", cta);
        Assert.Contains("WaveeMotion.ScaleStandard.Press", cta);
        Assert.DoesNotContain("PillHoverScale", cta);
        Assert.DoesNotContain("PillPressScale", cta);
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
