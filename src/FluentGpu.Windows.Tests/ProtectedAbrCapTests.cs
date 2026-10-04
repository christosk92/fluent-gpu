using System;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The ABR cap plumbing of <see cref="ProtectedMediaSession"/> over the shared <see cref="AdaptiveBitrateController"/>:
/// the viewport cap is floored at 720, quantised UP to a ladder height, raised at once and lowered only after it has
/// held for 1.5 s (never while a representation switch is pending); the policy cap and the viewport cap are separate
/// controller inputs; and a new session on a shared controller starts from a clean ladder/viewport without losing the
/// throughput history or the policy. Time is passed in, so nothing waits.
/// </summary>
public sealed class ProtectedAbrCapTests
{
    private static readonly int[] Heights = [240, 480, 720, 1080, 2160];

    private static ProtectedRepresentationDescriptor Rep(int height) => new()
    {
        Id = "r" + height,
        Quality = new QualityVariant("r" + height, height * 2_000, new SizeI(height * 16 / 9, height), 30,
            new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None)),
        InitUrl = "https://media/r" + height + "/init.mp4",
        SegmentBaseUrl = "https://media/r" + height + "/",
        SegmentPrefix = "seg-",
        SegmentSuffix = ".m4s",
        SegmentCount = 10,
    };

    private static ProtectedAdaptiveCatalog Catalog()
    {
        var reps = new ProtectedRepresentationDescriptor[Heights.Length];
        for (int i = 0; i < reps.Length; i++) reps[i] = Rep(Heights[i]);
        return new ProtectedAdaptiveCatalog
        {
            Tracks = [new ProtectedTrackDescriptor
            {
                Id = 1, Kind = FluentGpu.Media.TrackKind.Video, Label = "Video", IsDefault = true,
                Representations = reps,
            }],
        };
    }

    private static ProtectedMediaSession NewSession(AdaptiveBitrateController abr, int initialRung = 0)
    {
        var request = new ProtectedVideoRequest { InitUrl = Rep(Heights[initialRung]).InitUrl, Catalog = Catalog() };
        return new ProtectedMediaSession(new FakeProtectedVideoPlayer(), request, new MediaOpenOptions { Abr = abr });
    }

    [Fact]
    public async Task ViewportCap_IsFlooredAt720_AndQuantisedUpToALadderHeight()
    {
        var abr = new AdaptiveBitrateController();
        await using var session = NewSession(abr);
        Assert.Equal(int.MaxValue, abr.ViewportMaxHeight);   // a new source starts with no viewport cap

        session.ApplyViewportHeight(191, 10_000);            // a docked rail: floored at 720
        Assert.Equal(720, abr.ViewportMaxHeight);

        session.ApplyViewportHeight(1012, 10_000);           // 1012 px and 1080 px fullscreen are the same cap
        Assert.Equal(1080, abr.ViewportMaxHeight);
        session.ApplyViewportHeight(1080, 10_000);
        Assert.Equal(1080, abr.ViewportMaxHeight);

        session.ApplyViewportHeight(3000, 10_000);           // taller than every rung: the tallest rung
        Assert.Equal(2160, abr.ViewportMaxHeight);
    }

    [Fact]
    public async Task ViewportCap_FirstValueAndRaisesApplyAtOnce_LowerCapsOnlyAfterTheySettle()
    {
        var abr = new AdaptiveBitrateController();
        await using var session = NewSession(abr);

        session.ApplyViewportHeight(1080, 10_000);           // the first real viewport: immediate
        Assert.Equal(1080, abr.ViewportMaxHeight);

        session.ApplyViewportHeight(700, 20_000);            // a lower cap starts its timer ...
        Assert.Equal(1080, abr.ViewportMaxHeight);
        session.ApplyViewportHeight(700, 21_000);
        Assert.Equal(1080, abr.ViewportMaxHeight);
        session.ApplyViewportHeight(700, 21_499);
        Assert.Equal(1080, abr.ViewportMaxHeight);
        session.ApplyViewportHeight(700, 21_500);            // ... and commits after 1.5 s of the same request
        Assert.Equal(720, abr.ViewportMaxHeight);

        session.ApplyViewportHeight(2000, 21_600);           // a raise never waits
        Assert.Equal(2160, abr.ViewportMaxHeight);
    }

    [Fact]
    public async Task ViewportCap_ALowerCapThatIsInterruptedRestartsItsTimer()
    {
        var abr = new AdaptiveBitrateController();
        await using var session = NewSession(abr);
        session.ApplyViewportHeight(1080, 10_000);

        session.ApplyViewportHeight(700, 20_000);            // shrinking ...
        session.ApplyViewportHeight(1080, 21_000);           // ... then back to the committed cap (an animation wobble)
        session.ApplyViewportHeight(700, 21_100);            // the timer restarts here
        session.ApplyViewportHeight(700, 22_000);
        Assert.Equal(1080, abr.ViewportMaxHeight);
        session.ApplyViewportHeight(700, 22_600);
        Assert.Equal(720, abr.ViewportMaxHeight);
    }

    [Fact]
    public async Task ViewportCap_IsIgnoredWhileARepresentationSwitchIsPending()
    {
        var abr = new AdaptiveBitrateController();
        await using var session = NewSession(abr);
        session.ApplyViewportHeight(1080, 10_000);

        await session.SelectQualityAsync(QualitySelection.Pin("r2160"));   // pending until the pump observes it active
        session.ApplyViewportHeight(2000, 20_000);
        session.ApplyViewportHeight(700, 20_000);
        session.ApplyViewportHeight(700, 30_000);
        Assert.Equal(1080, abr.ViewportMaxHeight);
    }

    [Fact]
    public async Task PolicyCap_IsAControllerInput_NeverFoldedWithTheViewport()
    {
        var abr = new AdaptiveBitrateController();
        await using var session = NewSession(abr);

        session.SetAdaptiveMaxHeight(480);
        Assert.Equal(480, abr.PolicyMaxHeight);
        Assert.Equal(int.MaxValue, abr.ViewportMaxHeight);

        session.ApplyViewportHeight(2000, 10_000);
        Assert.Equal(480, abr.PolicyMaxHeight);              // the viewport write left the policy alone
        Assert.Equal(2160, abr.ViewportMaxHeight);
        Assert.Equal(480, abr.MaxHeight);                    // effective = min(policy, viewport)

        session.SetAdaptiveMaxHeight(0);                     // zero = unlimited
        Assert.Equal(int.MaxValue, abr.PolicyMaxHeight);
        Assert.Equal(2160, abr.MaxHeight);
    }

    [Fact]
    public async Task NewSessionOnASharedController_DoesNotInheritThePreviousViewportAsACeiling()
    {
        // F149: a video last watched docked (viewport 720) used to leave the next in-place-switched video with a 720
        // policy cap, so going fullscreen never climbed above 720p.
        var abr = new AdaptiveBitrateController { MaxHeight = 1080 };   // the app's policy
        await using (var docked = NewSession(abr))
        {
            docked.ApplyViewportHeight(300, 10_000);
            Assert.Equal(720, abr.MaxHeight);
        }

        await using var next = NewSession(abr);
        Assert.Equal(1080, abr.PolicyMaxHeight);             // the policy survives
        Assert.Equal(int.MaxValue, abr.ViewportMaxHeight);   // the previous surface's cap does not
        next.ApplyViewportHeight(2160, 20_000);
        Assert.Equal(1080, abr.MaxHeight);                   // fullscreen is bounded by the POLICY, not by the old 720
    }

    [Fact]
    public async Task NewSessionOnASharedController_KeepsTheThroughputButStartsAFreshClimb()
    {
        var abr = new AdaptiveBitrateController();
        int[] bitrates = [300_000, 1_000_000, 3_000_000];
        abr.RecordDownload(2_000_000, TimeSpan.FromSeconds(1));
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));   // the first climb arms the two-vote gate
        Assert.Equal(0, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 500));

        await using var session = NewSession(abr);

        Assert.False(abr.EstimateIsPrior);                                         // the link measurement survives
        Assert.Equal(2, abr.Choose(bitrates, TimeSpan.FromSeconds(20), 5_000));   // one vote again, as on a fresh controller
    }
}
