using Xunit;

namespace Wavee.Tests;

public class MediaSwitchLogicTests
{
    // ── KindOf ──────────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void KindOf_Video_WhenVideoFlagSet()
        => Assert.Equal(PlayableKind.Video, MediaSwitchLogic.KindOf(isVideoTrack: true, isLocalFile: false));

    [Fact]
    public void KindOf_LocalFile_WhenOnlyLocal()
        => Assert.Equal(PlayableKind.LocalFile, MediaSwitchLogic.KindOf(isVideoTrack: false, isLocalFile: true));

    [Fact]
    public void KindOf_Audio_WhenNeither()
        => Assert.Equal(PlayableKind.Audio, MediaSwitchLogic.KindOf(isVideoTrack: false, isLocalFile: false));

    [Fact]
    public void KindOf_Video_WinsOverLocal_WhenBothSet()
        => Assert.Equal(PlayableKind.Video, MediaSwitchLogic.KindOf(isVideoTrack: true, isLocalFile: true));

    // ── Decide ──────────────────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(PlayableKind.Audio)]
    [InlineData(PlayableKind.Video)]
    [InlineData(PlayableKind.LocalFile)]
    public void Decide_LoadOnCurrent_WhenSameKind(PlayableKind kind)
        => Assert.Equal(MediaSwitchLogic.SwitchAction.LoadOnCurrent, MediaSwitchLogic.Decide(kind, kind));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void Decide_SwapThenLoad_WhenDifferentKind(PlayableKind current, PlayableKind next)
        => Assert.Equal(MediaSwitchLogic.SwitchAction.SwapThenLoad, MediaSwitchLogic.Decide(current, next));

    // ── AllowCrossfade ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AllowCrossfade_True_OnlyForAudioToAudio()
        => Assert.True(MediaSwitchLogic.AllowCrossfade(PlayableKind.Audio, PlayableKind.Audio));

    [Fact]
    public void AllowCrossfade_False_ForVideoToVideo()
        => Assert.False(MediaSwitchLogic.AllowCrossfade(PlayableKind.Video, PlayableKind.Video));

    [Fact]
    public void AllowCrossfade_False_ForLocalToLocal()
        => Assert.False(MediaSwitchLogic.AllowCrossfade(PlayableKind.LocalFile, PlayableKind.LocalFile));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void AllowCrossfade_False_ForEveryCrossKindPair(PlayableKind from, PlayableKind to)
        => Assert.False(MediaSwitchLogic.AllowCrossfade(from, to));

    // ── TrackPlayer ─────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TrackPlayer_Video_ForVideo()
        => Assert.Equal("video", MediaSwitchLogic.TrackPlayer(PlayableKind.Video));

    [Fact]
    public void TrackPlayer_Audio_ForAudio()
        => Assert.Equal("audio", MediaSwitchLogic.TrackPlayer(PlayableKind.Audio));

    [Fact]
    public void TrackPlayer_Audio_ForLocalFile()
        => Assert.Equal("audio", MediaSwitchLogic.TrackPlayer(PlayableKind.LocalFile));

    // ── ShouldStopOutgoingHost ──────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(PlayableKind.Audio)]
    [InlineData(PlayableKind.Video)]
    [InlineData(PlayableKind.LocalFile)]
    public void ShouldStopOutgoingHost_False_WhenSameKind(PlayableKind kind)
        => Assert.False(MediaSwitchLogic.ShouldStopOutgoingHost(kind, kind));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void ShouldStopOutgoingHost_True_WhenKindChanges(PlayableKind current, PlayableKind next)
        => Assert.True(MediaSwitchLogic.ShouldStopOutgoingHost(current, next));

    // ── HostChanges (the controller's real swap trigger — Audio↔LocalFile share the audio host) ──────────────────────
    [Theory]
    [InlineData(PlayableKind.Audio)]
    [InlineData(PlayableKind.Video)]
    [InlineData(PlayableKind.LocalFile)]
    public void HostChanges_False_WhenSameKind(PlayableKind kind)
        => Assert.False(MediaSwitchLogic.HostChanges(kind, kind));

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Audio)]
    public void HostChanges_False_ForAudioLocalFileBoundary(PlayableKind current, PlayableKind next)
        => Assert.False(MediaSwitchLogic.HostChanges(current, next));   // both play through the audio host — no swap

    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void HostChanges_True_OnEveryVideoBoundary(PlayableKind current, PlayableKind next)
        => Assert.True(MediaSwitchLogic.HostChanges(current, next));

    // Every host boundary is also a kind change, so the stop-first rule agrees at a real swap (no two decoders at once).
    [Theory]
    [InlineData(PlayableKind.Audio, PlayableKind.Video)]
    [InlineData(PlayableKind.Video, PlayableKind.Audio)]
    [InlineData(PlayableKind.Video, PlayableKind.LocalFile)]
    [InlineData(PlayableKind.LocalFile, PlayableKind.Video)]
    public void HostChanges_ImpliesShouldStopOutgoingHost(PlayableKind current, PlayableKind next)
    {
        Assert.True(MediaSwitchLogic.HostChanges(current, next));
        Assert.True(MediaSwitchLogic.ShouldStopOutgoingHost(current, next));
    }
}
