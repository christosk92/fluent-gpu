using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Wavee.Tests;

/// <summary>Locks the track-cell-only artwork preference and the setup examples that explain row density.</summary>
public sealed class TrackArtworkPreferenceTests
{
    [Fact]
    public void Preference_IsPersistedAndPublishedThroughTheAppearanceEpoch()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string settings = File.ReadAllText(Path.Combine(root, "Platform", "AppSettings.cs"));
        string prefs = File.ReadAllText(Path.Combine(root, "Design", "AppearancePrefs.cs"));
        string setupWrites = File.ReadAllText(Path.Combine(root, "Features", "Setup", "SetupWrites.cs"));
        string setupPage = File.ReadAllText(Path.Combine(root, "Features", "Setup", "SetupPage.Appearance.cs"));
        string settingsPage = File.ReadAllText(Path.Combine(root, "Features", "Shell", "SettingsPage.General.cs"));

        Assert.Contains("appearance.trackArtwork.hidden", settings);
        Assert.Contains("HideTrackArtwork", prefs);
        Assert.Contains("AppearancePrefs.Bump()", setupWrites);
        Assert.Contains("ArtworkCheckBox", setupPage);
        Assert.Contains("TrackArtworkCheckBox", settingsPage);
    }

    [Fact]
    public void EveryTrackRowFamily_ConsumesTheSamePreference()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string[] consumers =
        [
            Path.Combine("Features", "Detail", "DetailTracks.cs"),
            Path.Combine("Features", "Detail", "ArtistPopular.cs"),
            Path.Combine("Features", "Home", "HomeModules.Artists.cs"),
            Path.Combine("Features", "Library", "LibraryPage.cs"),
            Path.Combine("Features", "Player", "NowPlayingPanel.cs"),
            Path.Combine("Features", "Player", "QueuePanel.cs"),
            Path.Combine("Features", "Player", "StagePanes.cs"),
            Path.Combine("Features", "Player", "VideoRailPanel.cs"),
            Path.Combine("Features", "Recents", "RecentsPage.cs"),
            Path.Combine("Features", "Search", "SearchPage.cs"),
        ];

        foreach (string relative in consumers)
            Assert.Contains("TrackArtworkHidden", File.ReadAllText(Path.Combine(root, relative)));
    }

    [Fact]
    public void DedicatedPlayerIdentityArtwork_RemainsOutsideTheTrackCellPreference()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        Assert.DoesNotContain("TrackArtworkHidden", File.ReadAllText(Path.Combine(root, "Features", "Shell", "PlayerBar.cs")));
        Assert.DoesNotContain("TrackArtworkHidden", File.ReadAllText(Path.Combine(root, "Features", "Player", "StageIdentity.cs")));
    }

    [Fact]
    public void QueueTrackCells_ConsumeClassicStyleAsTheNarrowTableFallback()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string queue = File.ReadAllText(Path.Combine(root, "Features", "Player", "QueuePanel.cs"));
        Assert.Contains("WaveeSettings.TrackRowStyle", queue);
        Assert.Contains("!classic && !artworkHidden", queue);
        Assert.Contains("QueueIdentity(t, go, nowPlaying:", queue);
        Assert.Contains("ClassicHairline()", queue);
    }

    [Fact]
    public void ArtistTopTracks_KeepArtworkInClassicAndExposeMediaBadges()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string popular = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistPopular.cs"));
        string band = File.ReadAllText(Path.Combine(root, "Features", "Detail", "ArtistPage.TopTracks.cs"));
        Assert.Contains("_classic = _svc.Settings.Get(WaveeSettings.TrackRowStyle) == 1", popular);
        Assert.Contains("_showArtwork = !AppearancePrefs.TrackArtworkHidden", popular);
        Assert.Contains("const float ClassicRowH = 48f", popular);
        Assert.Contains("cellGap = _classic ? Spacing.S", popular);
        Assert.Contains("Gap = classic ? Spacing.XS : 1f", popular);
        Assert.Contains("PressScale = WaveeMotion.ScaleSubtle.Press", popular);
        Assert.Contains("VideoPresence.HasVideo(t)", popular);
        Assert.Contains("TrackRow.ClassicExplicitBadge", popular);
        Assert.Contains("ClassicHairline()", popular);
        Assert.Contains(":facts=", popular);
        Assert.Contains("SkeletonShape(popular, popTitle, showTrackArtwork, classic)", band);
    }

    [Fact]
    public void DensityExamples_AreTheFourSuppliedRasterReferences()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string setup = Path.Combine(root, "assets", "setup");
        foreach (string name in new[]
                 {
                     "density-compact.png", "density-default.png", "density-cozy.png", "density-comfortable.png",
                 })
        {
            string path = Path.Combine(setup, name);
            Assert.True(File.Exists(path), $"Missing setup density reference: {name}");
            Assert.True(new FileInfo(path).Length > 0, $"Empty setup density reference: {name}");
        }
    }

    /// <summary><c>src/apps/Wavee</c>, located from this file's compile-time path.</summary>
    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
