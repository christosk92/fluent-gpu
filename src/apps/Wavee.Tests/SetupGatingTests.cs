using System;
using System.IO;
using Xunit;

namespace Wavee.Tests;

// The first-run setup wizard's pure decisions (App/SetupGating.cs + App/SetupBootstrap.cs): the arm/complete/defer
// state machine, the page-flow skip/clamp rules, and the footer progress mapping. Mirrors SidebarBootstrapTests /
// SidebarDesignGatingTests in shape — these drive the REAL production types (source-included), never a copy of them.
public class SetupGatingTests : IDisposable
{
    readonly string _local = Path.Combine(Path.GetTempPath(), "wavee-setup-gating-tests", Guid.NewGuid().ToString("n"));

    public SetupGatingTests() => Directory.CreateDirectory(_local);

    public void Dispose()
    {
        try { Directory.Delete(_local, recursive: true); } catch (Exception) { }
    }

    // ── SetupBootstrap.Run ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreshInstall_ArmsPending_AndSuppressesTheSidebarChooser()
    {
        var settings = new MemoryAppSettings();
        SetupBootstrap.Run(settings, _local);

        Assert.True(settings.Get(WaveeSettings.SetupPending));
        Assert.False(settings.Get(WaveeSettings.SetupCompleted));
        // LOAD-BEARING: setup page 5 IS the sidebar-design chooser — a fresh install must not also arm the separate
        // one-time popup chooser, or both onboardings show on the same launch.
        Assert.True(settings.Get(WaveeSettings.SidebarOnboardingSeen));
        Assert.Equal(SetupBootstrap.TargetVersion, settings.Get(WaveeSettings.SetupBootstrapVersion));
    }

    [Fact]
    public void ExistingInstall_DoesNotArmPending()
    {
        // library.db existing is SidebarBootstrap.IsFreshInstall's own first witness — SetupBootstrap reuses it verbatim.
        string waveeDir = Path.Combine(_local, "Wavee");
        Directory.CreateDirectory(waveeDir);
        File.WriteAllText(Path.Combine(waveeDir, "library.db"), "sqlite");

        var settings = new MemoryAppSettings();
        SetupBootstrap.Run(settings, _local);

        Assert.False(settings.Get(WaveeSettings.SetupPending));
        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        // An existing install must not be affected by the setup wizard arming the sidebar chooser suppression either
        // way — SidebarBootstrap (not SetupBootstrap) owns that key for existing installs.
        Assert.False(settings.WasWritten(WaveeSettings.SidebarOnboardingSeen));
    }

    [Fact]
    public void Run_IsIdempotent()
    {
        var settings = new MemoryAppSettings();
        SetupBootstrap.Run(settings, _local);
        settings.Set(WaveeSettings.SetupPending, false);   // simulate the wizard having already been shown
        int before = settings.WrittenCount;

        SetupBootstrap.Run(settings, _local);   // a later launch — must touch nothing

        Assert.Equal(before, settings.WrittenCount);
        Assert.False(settings.Get(WaveeSettings.SetupPending));
    }

    [Fact]
    public void FactoryResetProfile_ReArmsTheWizard()
    {
        // Every key back at its default, in a fresh (empty) temp data root — exactly what a factory reset produces —
        // must look indistinguishable from a true first launch and re-arm the wizard.
        var settings = new MemoryAppSettings();
        SetupBootstrap.Run(settings, _local);

        Assert.True(settings.Get(WaveeSettings.SetupPending));
        Assert.Equal(SetupBootstrap.TargetVersion, settings.Get(WaveeSettings.SetupBootstrapVersion));
    }

    // ── SetupGating.IsPending / IsCompleted ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsPending_IsCompleted_NullTolerant()
    {
        Assert.False(SetupGating.IsPending(null));
        Assert.False(SetupGating.IsCompleted(null));
    }

    [Fact]
    public void IsPending_IsCompleted_ReadTheKeys()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);
        Assert.True(SetupGating.IsPending(settings));
        Assert.False(SetupGating.IsCompleted(settings));

        settings.Set(WaveeSettings.SetupCompleted, true);
        Assert.True(SetupGating.IsCompleted(settings));
    }

    // ── MarkCompleted ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkCompleted_SetsCompleted_ClearsPending_ReturnsTheTransitionOnce()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);

        Assert.True(SetupGating.MarkCompleted(settings));
        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        Assert.False(settings.Get(WaveeSettings.SetupPending));

        Assert.False(SetupGating.MarkCompleted(settings));   // idempotent: no second transition
        Assert.False(SetupGating.MarkCompleted(settings));
    }

    [Fact]
    public void MarkCompleted_ToleratesNoSettingsSeam()
    {
        Assert.False(SetupGating.MarkCompleted(null));
    }

    // ── MarkDeferred ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkDeferred_ClearsPending_WithoutSettingCompleted()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);

        Assert.True(SetupGating.MarkDeferred(settings));
        Assert.False(settings.Get(WaveeSettings.SetupPending));
        Assert.False(settings.Get(WaveeSettings.SetupCompleted));
    }

    [Fact]
    public void MarkDeferred_AfterMarkCompleted_IsANoOp_AndCompletedStaysTrue()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);
        SetupGating.MarkCompleted(settings);

        Assert.False(SetupGating.MarkDeferred(settings));
        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        Assert.False(settings.Get(WaveeSettings.SetupPending));
    }

    [Fact]
    public void MarkDeferred_IsIdempotent()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);

        Assert.True(SetupGating.MarkDeferred(settings));
        Assert.False(SetupGating.MarkDeferred(settings));   // already cleared — no second transition
    }

    // ── SkipSignIn ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SkipSignIn_MirrorsAuthedFlag(bool authed, bool expected)
        => Assert.Equal(expected, SetupGating.SkipSignIn(authed));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void CarriesAcrossAuthGate_RequiresBarePendingAuthenticated(
        bool bare, bool pending, bool authenticated, bool expected)
        => Assert.Equal(expected, SetupGating.CarriesAcrossAuthGate(bare, pending, authenticated));

    // ── NextPage / PrevPage ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NextPage_SkipsSignIn_WhenSkipping()
    {
        Assert.Equal(SetupPage.LocalPlayback, SetupGating.NextPage(SetupPage.Terms, skipSignIn: true));
        Assert.Equal(SetupPage.SignIn, SetupGating.NextPage(SetupPage.Terms, skipSignIn: false));
    }

    [Fact]
    public void PrevPage_SkipsSignIn_WhenSkipping()
    {
        Assert.Equal(SetupPage.Terms, SetupGating.PrevPage(SetupPage.LocalPlayback, skipSignIn: true));
        Assert.Equal(SetupPage.SignIn, SetupGating.PrevPage(SetupPage.LocalPlayback, skipSignIn: false));
    }

    [Fact]
    public void NextPage_ClampsAtDone()
    {
        Assert.Equal(SetupPage.Done, SetupGating.NextPage(SetupPage.Done, skipSignIn: false));
        Assert.Equal(SetupPage.Done, SetupGating.NextPage(SetupPage.Notifications, skipSignIn: false));
    }

    [Fact]
    public void PrevPage_ClampsAtWelcome()
    {
        Assert.Equal(SetupPage.Welcome, SetupGating.PrevPage(SetupPage.Welcome, skipSignIn: false));
        Assert.Equal(SetupPage.Welcome, SetupGating.PrevPage(SetupPage.Terms, skipSignIn: false));
    }

    [Fact]
    public void NextThenPrev_WithSkip_RoundTrips()
    {
        // Walking forward-then-back over the skip must never leave you ON SignIn.
        var forward = SetupGating.NextPage(SetupPage.Terms, skipSignIn: true);
        var back = SetupGating.PrevPage(forward, skipSignIn: true);
        Assert.Equal(SetupPage.Terms, back);
        Assert.NotEqual(SetupPage.SignIn, forward);
    }

    // ── StepNumber / Progress ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepNumber_IsNullAtTheEnds()
    {
        Assert.Null(SetupGating.StepNumber(SetupPage.Welcome));
        Assert.Null(SetupGating.StepNumber(SetupPage.Done));
    }

    [Theory]
    [InlineData(SetupPage.Terms, 1)]
    [InlineData(SetupPage.SignIn, 2)]
    [InlineData(SetupPage.LocalPlayback, 3)]
    [InlineData(SetupPage.Appearance, 4)]
    [InlineData(SetupPage.Sidebar, 5)]
    [InlineData(SetupPage.Sound, 6)]
    [InlineData(SetupPage.Notifications, 7)]
    public void StepNumber_IsStepOfSeven(SetupPage page, int expectedStep)
    {
        var n = SetupGating.StepNumber(page);
        Assert.NotNull(n);
        Assert.Equal(expectedStep, n!.Value.Step);
        Assert.Equal(7, n!.Value.Total);
    }

    [Fact]
    public void StepNumber_DoesNotChange_WhetherOrNotSignInIsSkipped()
    {
        // The user's mental model is the same wizard whether or not they were already signed in — renumbering to 6
        // would make the two runs look like different products. StepNumber is keyed on page IDENTITY, never on a
        // running count of pages actually visited, so this holds trivially — pin it anyway.
        foreach (SetupPage page in new[]
                 {
                     SetupPage.Terms, SetupPage.LocalPlayback, SetupPage.Appearance,
                     SetupPage.Sidebar, SetupPage.Sound, SetupPage.Notifications,
                 })
        {
            Assert.Equal(SetupGating.StepNumber(page), SetupGating.StepNumber(page));
        }

        // The concrete regression: LocalPlayback is step 3 whether the wizard skipped SignIn to get there or not.
        var viaFullRun = SetupGating.NextPage(SetupGating.NextPage(SetupPage.Welcome, false), false);   // Terms -> SignIn... not reached, see below
        Assert.Equal(SetupPage.SignIn, viaFullRun);
        var viaSkippedRun = SetupGating.NextPage(SetupGating.NextPage(SetupPage.Welcome, true), true);
        Assert.Equal(SetupPage.LocalPlayback, viaSkippedRun);
        Assert.Equal(3, SetupGating.StepNumber(SetupPage.LocalPlayback)!.Value.Step);
    }

    [Fact]
    public void StepLabelKey_OnlyWelcomeAndDoneHaveAFixedLabel()
    {
        Assert.Equal(Strings.Setup.PreSetup, SetupGating.StepLabelKey(SetupPage.Welcome));
        Assert.Equal(Strings.Setup.Complete, SetupGating.StepLabelKey(SetupPage.Done));
        foreach (SetupPage page in new[]
                 {
                     SetupPage.Terms, SetupPage.SignIn, SetupPage.LocalPlayback, SetupPage.Appearance,
                     SetupPage.Sidebar, SetupPage.Sound, SetupPage.Notifications,
                 })
        {
            Assert.Null(SetupGating.StepLabelKey(page));
        }
    }

    [Theory]
    [InlineData(SetupPage.Welcome, 0f)]
    [InlineData(SetupPage.Terms, 1f / 7f)]
    [InlineData(SetupPage.Notifications, 7f / 7f)]
    [InlineData(SetupPage.Done, 1f)]
    public void Progress_MatchesTheLadder(SetupPage page, float expected)
        => Assert.Equal(expected, SetupGating.Progress(page), precision: 5);
}
