using Xunit;

namespace Wavee.Tests;

public class VideoPlacementLogicTests
{
    // ── VideoActive ─────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void VideoActive_True_WhenPreferAndHasVideoAndNotDismissed()
        => Assert.True(VideoPlacementLogic.VideoActive(preferVideo: true, hasVideo: true, trackGen: 5, dismissedForTrackGen: -1));

    [Fact]
    public void VideoActive_False_WhenNotPreferred()
        => Assert.False(VideoPlacementLogic.VideoActive(preferVideo: false, hasVideo: true, trackGen: 5, dismissedForTrackGen: -1));

    [Fact]
    public void VideoActive_False_WhenNoVideo()
        => Assert.False(VideoPlacementLogic.VideoActive(preferVideo: true, hasVideo: false, trackGen: 5, dismissedForTrackGen: -1));

    [Fact]
    public void VideoActive_False_WhenDismissedForThisTrack()
        => Assert.False(VideoPlacementLogic.VideoActive(preferVideo: true, hasVideo: true, trackGen: 5, dismissedForTrackGen: 5));

    [Fact]
    public void VideoActive_TrueAgain_AfterTrackGenBump_WhileDismissStaysOld()
    {
        // Dismissed track 5, then a track change bumps the gen to 6. The per-track dismiss expires (PreferVideo is
        // sticky), so the video is active again for the new track even though dismissedForTrackGen still reads 5.
        Assert.False(VideoPlacementLogic.VideoActive(preferVideo: true, hasVideo: true, trackGen: 5, dismissedForTrackGen: 5));
        Assert.True(VideoPlacementLogic.VideoActive(preferVideo: true, hasVideo: true, trackGen: 6, dismissedForTrackGen: 5));
    }

    // ── ShouldPublishResolve ────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ShouldPublishResolve_True_WhenCapturedEqualsCurrent()
        => Assert.True(VideoPlacementLogic.ShouldPublishResolve(capturedGen: 3, currentGen: 3));

    [Fact]
    public void ShouldPublishResolve_False_WhenStale()
        => Assert.False(VideoPlacementLogic.ShouldPublishResolve(capturedGen: 3, currentGen: 4));

    // ── DecideDetached ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void DecideDetached_Open_WhenActiveDetachedNotAlive()
        => Assert.Equal(VideoPlacementLogic.DetachedAction.Open,
            VideoPlacementLogic.DecideDetached(videoActive: true, VideoPlacement.Detached, windowAlive: false));

    [Fact]
    public void DecideDetached_None_WhenActiveDetachedAlive()
        => Assert.Equal(VideoPlacementLogic.DetachedAction.None,
            VideoPlacementLogic.DecideDetached(videoActive: true, VideoPlacement.Detached, windowAlive: true));

    [Theory]
    [InlineData(VideoPlacement.Detached)]
    [InlineData(VideoPlacement.InWindowPip)]
    public void DecideDetached_Close_WhenNotActiveButAlive(VideoPlacement placement)
        => Assert.Equal(VideoPlacementLogic.DetachedAction.Close,
            VideoPlacementLogic.DecideDetached(videoActive: false, placement, windowAlive: true));

    [Fact]
    public void DecideDetached_Close_WhenActivePipButAlive()
        => Assert.Equal(VideoPlacementLogic.DetachedAction.Close,
            VideoPlacementLogic.DecideDetached(videoActive: true, VideoPlacement.InWindowPip, windowAlive: true));

    [Theory]
    [InlineData(VideoPlacement.Detached)]
    [InlineData(VideoPlacement.InWindowPip)]
    public void DecideDetached_None_WhenNotActiveAndNotAlive(VideoPlacement placement)
        => Assert.Equal(VideoPlacementLogic.DetachedAction.None,
            VideoPlacementLogic.DecideDetached(videoActive: false, placement, windowAlive: false));

    // ── FallbackOnUserClose ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void FallbackOnUserClose_PiP_WhenDetachedAndActive()
        => Assert.Equal(VideoPlacement.InWindowPip,
            VideoPlacementLogic.FallbackOnUserClose(VideoPlacement.Detached, videoActive: true));

    [Fact]
    public void FallbackOnUserClose_Null_WhenAlreadyPiP()
        => Assert.Null(VideoPlacementLogic.FallbackOnUserClose(VideoPlacement.InWindowPip, videoActive: true));

    [Fact]
    public void FallbackOnUserClose_Null_WhenDetachedButNotActive()
        => Assert.Null(VideoPlacementLogic.FallbackOnUserClose(VideoPlacement.Detached, videoActive: false));
}
