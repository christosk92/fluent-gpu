using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The full behavioral spec of the surface placement state machine (<see cref="PlacementCore"/>) — the pure decision
/// layer every video surface, the detached-window owner and the player-bar affordance derive from. Sections:
/// activation (the parity port of the deleted <c>VideoPlacementLogicTests</c>), the click spec, per-content dismiss,
/// availability, host-close, fullscreen, the owner mount decisions, one NAMED regression per historical bug, and two
/// property tests over arbitrary command sequences.
/// </summary>
public class PlacementCoreTests
{
    static readonly PlacementPolicy Policy = PlacementPolicy.Video;
    const PlacementSet All = PlacementSet.Floating | PlacementSet.Detached;   // a track that HAS a video

    static PlacementState Off(PlacementSet available = All, long contentGen = 0)
        => PlacementState.Initial(Policy) with { Available = available, ContentGen = contentGen };

    static PlacementState At(SurfacePlacement p, PlacementSet available = All, long contentGen = 0)
        => PlacementCore.OpenAt(Off(available, contentGen), p);

    // ── activation (parity port: the old VideoPlacementLogic.VideoActive cases, one for one) ─────────────────────────

    [Fact]
    public void Active_WhenRequestedAndContentHasVideoAndNotDismissed()
        => Assert.True(PlacementCore.IsActive(At(SurfacePlacement.Floating, All, contentGen: 5)));

    [Fact]
    public void Inactive_WhenTurnedOff()   // old: VideoActive_False_WhenNotPreferred
        => Assert.False(PlacementCore.IsActive(Off(All, contentGen: 5)));

    [Fact]
    public void Inactive_WhenContentHasNoVideo()   // old: VideoActive_False_WhenNoVideo → availability, not a flag
        => Assert.False(PlacementCore.IsActive(At(SurfacePlacement.Floating, PlacementSet.None, contentGen: 5)));

    [Fact]
    public void Inactive_WhenDismissedForThisContent()
    {
        var s = PlacementCore.DismissForContent(At(SurfacePlacement.Floating, All, contentGen: 5));
        Assert.False(PlacementCore.IsActive(s));
    }

    [Fact]
    public void ActiveAgain_AfterContentChange_WhileDismissStaysOld()
    {
        // Dismissed content 5; the next track bumps the generation, so the per-content dismiss expires by itself and the
        // sticky intent brings the surface straight back — without any code clearing the dismiss.
        var dismissed = PlacementCore.DismissForContent(At(SurfacePlacement.Floating, All, contentGen: 5));
        Assert.False(PlacementCore.IsActive(dismissed));

        var next = PlacementCore.ContentChanged(dismissed, 6);
        Assert.True(PlacementCore.IsActive(next));
        Assert.Equal(5, next.DismissedGen);                       // the stale mark is still there, and inert
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(next));
    }

    // ── the click spec ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PrimaryClick_WhenUnlit_OpensAtPreferred_WhichDefaultsToTheMiniPlayer()
    {
        var s = PlacementCore.TogglePrimary(Off());
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));   // NOT a new OS window
    }

    [Theory]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.Detached)]
    public void PrimaryClick_WhenLit_TurnsOff_FromAnyPlacement(SurfacePlacement from)
    {
        var s = PlacementCore.TogglePrimary(At(from));
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);
    }

    [Fact]
    public void PrimaryClick_IsSymmetric_OffOnOffOn()
    {
        var s = Off();
        for (int i = 0; i < 4; i++)
        {
            s = PlacementCore.TogglePrimary(s);
            Assert.Equal(i % 2 == 0, PlacementCore.IsActive(s));
        }
    }

    [Fact]
    public void OpenAt_MakesTheTargetTheNewPreferredHome()
    {
        var s = PlacementCore.OpenAt(Off(), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, s.Preferred);

        // …so turning it off and clicking the primary again returns to the placement the user last chose.
        var reopened = PlacementCore.TogglePrimary(PlacementCore.TurnOff(s));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(reopened));
    }

    [Fact]
    public void TurnOff_KeepsPreferred_SoTheNextOpenGoesHome()
    {
        var s = PlacementCore.TurnOff(At(SurfacePlacement.Detached));
        Assert.Equal(SurfacePlacement.Detached, s.Preferred);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(s));
    }

    [Fact]
    public void OpenAt_ClearsAnEarlierDismiss()   // an explicit "show it" beats an earlier "hide it for this song"
    {
        var dismissed = PlacementCore.DismissForContent(At(SurfacePlacement.Floating));
        var shown = PlacementCore.OpenAt(dismissed, SurfacePlacement.Floating);
        Assert.True(PlacementCore.IsActive(shown));
    }

    [Fact]
    public void MovingPlacement_KeepsItActive_AndDoesNotStack()
    {
        var s = PlacementCore.OpenAt(At(SurfacePlacement.Floating), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(s));   // exactly one placement — an enum, not flags
    }

    // ── per-content dismiss (the surface's own ✕) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dismiss_HidesTheSurface_ButKeepsTheStickyIntent()
    {
        var s = PlacementCore.DismissForContent(At(SurfacePlacement.Floating, All, contentGen: 3));
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.Floating, s.Requested);   // still "on" — it is hidden for this song only
    }

    [Fact]
    public void Restore_UndoesADismiss_WithoutChangingPlacement()
    {
        var s = PlacementCore.Restore(PlacementCore.DismissForContent(At(SurfacePlacement.Detached)));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(s));
    }

    // ── availability (content ∧ host caps) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LosingAvailability_HidesTheSurface_ButPreservesIntent_AndSnapsBack()
    {
        // An audio-only track makes every placement unavailable; the surface goes away but the intent is remembered, so
        // the next track that HAS a video returns to exactly where the user had it (rather than to a fallback).
        var watching = At(SurfacePlacement.Detached);
        var audioOnly = PlacementCore.WithAvailability(watching, PlacementSet.None);
        Assert.False(PlacementCore.IsActive(audioOnly));
        Assert.Equal(SurfacePlacement.Detached, audioOnly.Requested);

        var videoAgain = PlacementCore.WithAvailability(audioOnly, All);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(videoAgain));
    }

    [Fact]
    public void UnavailablePlacement_FallsDownTheCommitmentLadder_WithoutRewritingIntent()
    {
        // The host cannot open a second window (no detached), so a detached request resolves to the mini player — and
        // when a second window becomes possible again it snaps back, because Requested was never overwritten.
        var s = PlacementCore.WithAvailability(At(SurfacePlacement.Detached), PlacementSet.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
        Assert.Equal(SurfacePlacement.Detached, s.Requested);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(PlacementCore.WithAvailability(s, All)));
    }

    [Fact]
    public void ResolveWith_AnswersPerContent_WithoutMutatingState()
    {
        var s = At(SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.ResolveWith(s, All));
        Assert.Equal(SurfacePlacement.None, PlacementCore.ResolveWith(s, PlacementSet.None));   // that track has no video
        Assert.Equal(All, s.Available);                                                          // untouched
    }

    // ── host close ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClosingTheDetachedWindow_FallsBackToTheMiniPlayer_AndAdoptsItAsPreferred()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
        Assert.Equal(SurfacePlacement.Floating, s.Preferred);   // "I don't want a separate window" is remembered
    }

    [Fact]
    public void ClosingTheDetachedWindow_TurnsOff_WhenNothingLessCommittingIsAvailable()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached, PlacementSet.Detached), SurfacePlacement.Detached);
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.None, s.Requested);
    }

    [Fact]
    public void ClosingAnInAppSurface_IsHideForThisSong_NotOff()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Floating), SurfacePlacement.Floating);
        Assert.False(PlacementCore.IsActive(s));
        Assert.Equal(SurfacePlacement.Floating, s.Requested);                  // still on…
        Assert.True(PlacementCore.IsActive(PlacementCore.ContentChanged(s, s.ContentGen + 1)));   // …and back next track
    }

    [Fact]
    public void AStaleClose_IsIgnored()
    {
        // The detached window died, but by the time its close arrived the user had already moved to the mini player.
        // Acting on it would clobber the newer placement; it must be inert.
        var moved = PlacementCore.OpenAt(At(SurfacePlacement.Detached), SurfacePlacement.Floating);
        var after = PlacementCore.HostClosed(moved, SurfacePlacement.Detached);
        Assert.Equal(moved, after);
    }

    // ── fullscreen (reserved surface; the rules are live) ────────────────────────────────────────────────────────────

    [Fact]
    public void Fullscreen_RemembersWhereToGoBack_AndIsNeverThePreferredHome()
    {
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Detached));
        Assert.Equal(SurfacePlacement.Fullscreen, fs.Requested);
        Assert.Equal(SurfacePlacement.Detached, fs.ReturnTo);
        Assert.Equal(SurfacePlacement.Detached, fs.Preferred);   // NOT Fullscreen — it must not survive a restart

        var back = PlacementCore.ExitFullscreen(fs);
        Assert.Equal(SurfacePlacement.Detached, back.Requested);
        Assert.Equal(SurfacePlacement.None, back.ReturnTo);
    }

    [Fact]
    public void Fullscreen_EnteredFromOff_ExitsToThePreferredHome()
    {
        var back = PlacementCore.ExitFullscreen(PlacementCore.EnterFullscreen(Off()));
        Assert.Equal(SurfacePlacement.Floating, back.Requested);
    }

    [Fact]
    public void ExitFullscreen_IsANoOp_WhenNotInFullscreen()
    {
        var s = At(SurfacePlacement.Floating);
        Assert.Equal(s, PlacementCore.ExitFullscreen(s));
    }

    [Fact]
    public void Fullscreen_WhenUnavailable_ResolvesDownTheLadder()
    {
        // Fullscreen is not in the video policy yet, so requesting it still shows the video somewhere real.
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Detached));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(fs));
    }

    // ── owner mount decisions (parity port: the old DecideDetached cases) ────────────────────────────────────────────

    [Fact]
    public void Owner_Opens_WhenItsPlacementIsResolvedAndNothingIsAlive()
        => Assert.Equal(MountAction.Open,
            PlacementCore.DecideOwned(SurfacePlacement.Detached, SurfacePlacement.Detached, alive: false));

    [Fact]
    public void Owner_DoesNothing_WhenAlreadyMatching()
        => Assert.Equal(MountAction.None,
            PlacementCore.DecideOwned(SurfacePlacement.Detached, SurfacePlacement.Detached, alive: true));

    [Theory]
    [InlineData(SurfacePlacement.None)]        // turned off
    [InlineData(SurfacePlacement.Floating)]    // a DIFFERENT placement won
    public void Owner_Closes_WhenItsPlacementIsNoLongerResolved(SurfacePlacement resolved)
        => Assert.Equal(MountAction.Close,
            PlacementCore.DecideOwned(resolved, SurfacePlacement.Detached, alive: true));

    [Theory]
    [InlineData(SurfacePlacement.None)]
    [InlineData(SurfacePlacement.Floating)]
    public void Owner_DoesNothing_WhenNotResolvedAndNotAlive(SurfacePlacement resolved)
        => Assert.Equal(MountAction.None,
            PlacementCore.DecideOwned(resolved, SurfacePlacement.Detached, alive: false));

    [Fact]
    public void DecideMount_Move_WhenMountedInTheWrongPlacement()
        => Assert.Equal(MountAction.Move,
            PlacementCore.DecideMount(SurfacePlacement.Detached, SurfacePlacement.Floating));

    // ── reality reporting is scoped per surface (two independent surfaces must not clobber each other) ───────────────

    [Fact]
    public void MountedSurface_ClaimsReality()
        => Assert.Equal(SurfacePlacement.Detached,
            PlacementCore.LiveAfterReport(SurfacePlacement.None, SurfacePlacement.Detached, mounted: true));

    [Fact]
    public void UnmountedSurface_ReleasesOnlyItsOwnClaim()
        => Assert.Equal(SurfacePlacement.None,
            PlacementCore.LiveAfterReport(SurfacePlacement.Detached, SurfacePlacement.Detached, mounted: false));

    [Fact]
    public void UnmountedSurface_NeverErasesAnotherSurfacesClaim()
    {
        // Both surfaces watch the same state, so the mini player reports "not mounted" on the very same change that the
        // pop-out reports "mounted". Unscoped, that report would erase the pop-out's claim and reality would read None
        // while a window was plainly open.
        var live = PlacementCore.LiveAfterReport(SurfacePlacement.None, SurfacePlacement.Detached, mounted: true);
        live = PlacementCore.LiveAfterReport(live, SurfacePlacement.Floating, mounted: false);
        Assert.Equal(SurfacePlacement.Detached, live);
    }

    [Fact]
    public void RealityReporting_ConvergesRegardlessOfSurfaceOrder()
    {
        // A hand-off (floating → detached): whichever surface reports first, reality settles on the one that is mounted.
        var a = PlacementCore.LiveAfterReport(SurfacePlacement.Floating, SurfacePlacement.Floating, mounted: false);
        a = PlacementCore.LiveAfterReport(a, SurfacePlacement.Detached, mounted: true);

        var b = PlacementCore.LiveAfterReport(SurfacePlacement.Floating, SurfacePlacement.Detached, mounted: true);
        b = PlacementCore.LiveAfterReport(b, SurfacePlacement.Floating, mounted: false);

        Assert.Equal(SurfacePlacement.Detached, a);
        Assert.Equal(SurfacePlacement.Detached, b);
    }

    // ── the async-resolve fence (parity port: ShouldPublishResolve) ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_Publishes_WhenTheCapturedGenerationIsStillCurrent()
        => Assert.True(PlacementCore.IsCurrentGeneration(capturedGen: 3, currentGen: 3));

    [Fact]
    public void Resolve_IsDropped_WhenSuperseded()
        => Assert.False(PlacementCore.IsCurrentGeneration(capturedGen: 3, currentGen: 4));

    // ── named regressions (one per historical bug — these must never come back) ──────────────────────────────────────

    /// <summary>Bug 3: closing the pop-out left the player-bar toggle lit with no surface behind it, because "close" had
    /// no transition at all and the intent flag stayed true.</summary>
    [Fact]
    public void Regression_StuckToggle_ClosingThePopOutLandsInTheMiniPlayerInsteadOfLitNothing()
    {
        var s = PlacementCore.HostClosed(At(SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.True(PlacementCore.IsActive(s));                              // still watching…
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));   // …in a surface that actually exists
    }

    /// <summary>Bug 4: a late video resolve for the PREVIOUS track republished itself over the current one ("changing
    /// track and clicking video again opens the same old video").</summary>
    [Fact]
    public void Regression_StaleVideo_ASupersededResolveNeverPublishes()
    {
        long gen = 7;
        long captured = gen;
        gen++;                                    // the track changed while the resolve was in flight
        Assert.False(PlacementCore.IsCurrentGeneration(captured, gen));
    }

    /// <summary>Bug 5: placement lived in three owners at once (bridge signals, a view-local window handle, the engine's
    /// host table), so they could disagree. One enum makes "mounted in two places" unrepresentable.</summary>
    [Fact]
    public void Regression_PlacementSplit_AtMostOnePlacementIsEverResolved()
    {
        var s = At(SurfacePlacement.Floating);
        foreach (var move in new[] { SurfacePlacement.Detached, SurfacePlacement.Floating, SurfacePlacement.Detached })
        {
            s = PlacementCore.OpenAt(s, move);
            int mounted = 0;
            foreach (var p in new[] { SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Detached, SurfacePlacement.Fullscreen })
                if (PlacementCore.Resolve(s) == p) mounted++;
            Assert.Equal(1, mounted);
        }
    }

    /// <summary>The UX complaint: the primary click spawned an always-on-top OS window — the MOST committing placement —
    /// as its first response. It must open the lowest-commitment surface instead.</summary>
    [Fact]
    public void Regression_FirstClick_OpensTheMiniPlayer_NotAnAlwaysOnTopWindow()
    {
        var s = PlacementCore.TogglePrimary(PlacementState.Initial(Policy) with { Available = All });
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(s));
        Assert.NotEqual(SurfacePlacement.Detached, PlacementCore.Resolve(s));
    }

    // ── persistence: "persist where you like to work; never persist whether it is running" ──────────────────────────

    [Theory]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.Detached)]
    public void PreferredPlacement_RoundTrips(SurfacePlacement p)
        => Assert.Equal(p, PlacementPersistence.LoadPlacement(PlacementPersistence.SavePlacement(p), Policy));

    [Theory]
    [InlineData(SurfacePlacement.None)]          // "off" — restoring it would mean nothing
    [InlineData(SurfacePlacement.Fullscreen)]    // a MODE — restoring it would trap the user in it on next launch
    public void OffAndFullscreen_AreNeverPersisted(SurfacePlacement p)
    {
        Assert.Equal("", PlacementPersistence.SavePlacement(p));
        Assert.Equal(Policy.Default, PlacementPersistence.LoadPlacement(PlacementPersistence.SavePlacement(p), Policy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("docked")]   // a real placement, but this surface does not allow it
    public void UnusablePreference_FallsBackToTheSurfaceDefault(string? raw)
        => Assert.Equal(Policy.Default, PlacementPersistence.LoadPlacement(raw, Policy));

    [Fact]
    public void StoredPreference_IsANameNotAnEnumNumber()
    {
        // Numeric values encode the commitment ladder; persisting them would silently reinterpret saved preferences if
        // the ladder is ever reordered.
        Assert.Equal("detached", PlacementPersistence.SavePlacement(SurfacePlacement.Detached));
        Assert.Equal("floating", PlacementPersistence.SavePlacement(SurfacePlacement.Floating));
    }

    [Fact]
    public void Geometry_RoundTrips_AndRoundsToWholeUnits()
    {
        Assert.True(PlacementPersistence.TryLoadRect(PlacementPersistence.SaveRect(1720.4f, 880.6f, 360f, 202f),
            out float x, out float y, out float w, out float h));
        Assert.Equal(1720f, x);
        Assert.Equal(881f, y);
        Assert.Equal(360f, w);
        Assert.Equal(202f, h);
    }

    [Fact]
    public void Geometry_NegativePositionsSurvive()   // a window on a monitor left of the primary has a negative X
    {
        Assert.True(PlacementPersistence.TryLoadRect(PlacementPersistence.SaveRect(-1920f, -140f, 480f, 270f),
            out float x, out float y, out float w, out float h));
        Assert.Equal(-1920f, x);
        Assert.Equal(-140f, y);
        Assert.Equal(480f, w);
        Assert.Equal(270f, h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]           // truncated
    [InlineData("1,2,3,4,5")]       // over-long
    [InlineData("a,b,c,d")]         // non-numeric
    [InlineData("10,10,0,0")]       // degenerate — would open a 0x0 window
    [InlineData("10,10,-5,-5")]     // negative size
    public void MalformedGeometry_IsRejected(string? raw)
        => Assert.False(PlacementPersistence.TryLoadRect(raw, out _, out _, out _, out _));

    [Fact]
    public void DegenerateGeometry_IsNotEvenWritten()
        => Assert.Equal("", PlacementPersistence.SaveRect(10f, 10f, 0f, 0f));

    // ── property tests ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Property 1: the invariants hold after EVERY command in EVERY order — a resolved placement is always
    /// actually available, Preferred is always a real home, nothing resolves while off, and a dismiss made for other
    /// content is inert. Deterministic pseudo-random sequences (fixed seed) so a failure always reproduces.</summary>
    [Fact]
    public void Property_InvariantsHoldForArbitraryCommandSequences()
    {
        var placements = new[]
        {
            SurfacePlacement.None, SurfacePlacement.Docked, SurfacePlacement.Floating,
            SurfacePlacement.Detached, SurfacePlacement.Fullscreen,
        };
        var sets = new[]
        {
            PlacementSet.None, PlacementSet.Floating, PlacementSet.Detached, All,
            PlacementSet.Fullscreen, PlacementSet.Docked | PlacementSet.Floating,
        };
        var kinds = (PlacementCommandKind[])Enum.GetValues(typeof(PlacementCommandKind));

        uint rng = 0x5EED_1234;
        uint Next() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }

        for (int seq = 0; seq < 2000; seq++)
        {
            var s = PlacementState.Initial(Policy);
            var trail = new List<PlacementCommand>(24);
            long gen = 0;
            for (int step = 0; step < 24; step++)
            {
                var kind = kinds[Next() % (uint)kinds.Length];
                if (kind == PlacementCommandKind.ContentChanged) gen++;
                var cmd = new PlacementCommand(
                    kind,
                    placements[Next() % (uint)placements.Length],
                    sets[Next() % (uint)sets.Length],
                    gen);
                trail.Add(cmd);
                s = PlacementCore.Apply(s, cmd);
                Assert.True(PlacementCore.Invariant(s),
                    $"invariant broken after {string.Join(" → ", trail)}; state = {s}");
            }
        }
    }

    /// <summary>Property 2: the primary affordance is total and symmetric — from ANY reachable state, one click makes an
    /// active surface inactive, and makes an inactive surface active whenever any placement is available at all. That is
    /// the whole "the toggle can never get stuck" guarantee, checked exhaustively rather than by example.</summary>
    [Fact]
    public void Property_PrimaryToggleIsTotalAndSymmetric()
    {
        var placements = new[] { SurfacePlacement.None, SurfacePlacement.Floating, SurfacePlacement.Detached, SurfacePlacement.Fullscreen };
        var sets = new[] { PlacementSet.None, PlacementSet.Floating, PlacementSet.Detached, All };

        foreach (var requested in placements)
        foreach (var preferred in new[] { SurfacePlacement.Floating, SurfacePlacement.Detached })
        foreach (var available in sets)
        foreach (var dismissed in new[] { PlacementState.NotDismissed, 0L, 1L })
        {
            var s = new PlacementState(requested, preferred, SurfacePlacement.None, SurfacePlacement.None, available, 0L, dismissed);
            bool wasActive = PlacementCore.IsActive(s);
            var next = PlacementCore.TogglePrimary(s);

            if (wasActive)
                Assert.False(PlacementCore.IsActive(next));   // lit → always off
            else if (PlacementCore.FirstAvailable(preferred, available) != SurfacePlacement.None)
                Assert.True(PlacementCore.IsActive(next));    // unlit + the home resolves somewhere → always on
            Assert.True(PlacementCore.Invariant(next));
        }
    }
}
