using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Protocol.Player;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public class ConnectStateBuilderTests
{
    [Fact]
    public void BuildPutState_PreservesVideoMetadataAndSynthesizesAudioContextMetadata()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var videoMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["track_player"] = "video",
            ["media.type"] = "video",
            ["media.manifest_id"] = "manifest",
            ["save_track.uri"] = "spotify:track:audio",
            ["context_uri"] = "spotify:playlist:p",
        };
        var autoplayMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["track_player"] = "audio",
        };
        var contextMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format_list_type"] = "liked-songs",
            ["context_description"] = "Liked Songs",
            ["mixer_enabled"] = "true",
        };
        var snap = new LocalPlaybackSnapshot(
            Track: new SnapshotTrack("spotify:track:video", "uv", "context", "Video", "Album",
                "spotify:artist:a", "Artist", "spotify:album:al", "", true, 5, videoMetadata),
            ContextUri: "spotify:playlist:p",
            PositionMs: 123,
            DurationMs: 456,
            IsPlaying: true,
            IsPaused: false,
            Shuffle: false,
            Repeat: RepeatMode.Off,
            PrevTracks: Array.Empty<SnapshotTrack>(),
            NextTracks: new[]
            {
                new SnapshotTrack("spotify:track:audio-next", "un", "context", "Audio", "Album",
                    "spotify:artist:a", "Artist", "spotify:album:al", "https://i.scdn.co/image/abc",
                    false, 6, new Dictionary<string, string>()),
                new SnapshotTrack("spotify:track:auto", "ua", "autoplay", "Auto", "",
                    "", "", "", "", false, 7, autoplayMetadata),
            },
            ContextMetadata: contextMetadata,
            ContextIndex: 5,
            InteractionId: "interaction",
            PageInstanceId: "page",
            QueueRevision: "42",
            SessionId: "session",
            PlaybackId: "playback",
            HasBeenPlayingForMs: 1000,
            StartedPlayingAtMs: 9000,
            Volume01: 1);

        var req = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000));

        var ps = req.Device.PlayerState;
        Assert.Equal("spotify:playlist:p", ps.ContextUri);
        Assert.Equal("context://spotify:playlist:p", ps.ContextUrl);
        Assert.Equal("42", ps.QueueRevision);
        Assert.Equal((uint)5, ps.Index.Track);
        Assert.Equal("your_library", ps.PlayOrigin.FeatureIdentifier);
        Assert.Equal("your_library", ps.PlayOrigin.ReferrerIdentifier);
        Assert.Equal(BitrateLevel.High, ps.PlaybackQuality.BitrateLevel);
        Assert.Equal(BitrateStrategy.CachedFile, ps.PlaybackQuality.Strategy);
        Assert.Equal(HiFiStatus.Off, ps.PlaybackQuality.HifiStatus);
        Assert.Equal("2", ps.ContextMetadata["player.arch"]);
        Assert.Equal("true", ps.ContextMetadata["mixer_enabled"]);

        var video = ps.Track.Metadata;
        Assert.Equal("video", video["track_player"]);
        Assert.Equal("manifest", video["media.manifest_id"]);
        Assert.Equal("spotify:track:audio", video["save_track.uri"]);
        Assert.Equal("interaction", video["interaction_id"]);
        Assert.Equal("page", video["page_instance_id"]);
        Assert.False(video.ContainsKey("entity_uri"));
        Assert.False(video.ContainsKey("view_index"));
        Assert.False(video.ContainsKey("iteration"));

        var audio = ps.NextTracks[0].Metadata;
        Assert.Equal("audio", audio["track_player"]);
        Assert.Equal("spotify:playlist:p", audio["context_uri"]);
        Assert.Equal("spotify:playlist:p", audio["entity_uri"]);
        Assert.Equal("6", audio["view_index"]);
        Assert.Equal("0", audio["iteration"]);
        Assert.Equal("spotify:image:abc", audio["image_url"]);

        var autoplay = ps.NextTracks[1];
        Assert.Equal("autoplay", autoplay.Provider);
        Assert.Equal("true", autoplay.Metadata["autoplay.is_autoplay"]);
        Assert.False(autoplay.Metadata.ContainsKey("context_uri"));
        Assert.False(autoplay.Metadata.ContainsKey("entity_uri"));
    }

    // ── M0: `track_player` is the controller's LIVE media kind, not a bare has-video flag ────────────────────────────
    // The current track's track_player names the HOST that is actually decoding. A video-capable track played as AUDIO must
    // report "audio" (remote controllers render the wrong player otherwise) — and a track playing as VIDEO must report
    // "video" even if the wire snapshot's own metadata said otherwise. So: video IFF the current kind is Video.
    static LocalPlaybackSnapshot Snap(bool hasVideo, Dictionary<string, string>? trackMetadata = null) =>
        new(
            Track: new SnapshotTrack("spotify:track:t", "uid", "context", "Title", "Album",
                "spotify:artist:a", "Artist", "spotify:album:al", "", hasVideo, 3,
                trackMetadata ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            ContextUri: "spotify:playlist:p",
            PositionMs: 0,
            DurationMs: 1000,
            IsPlaying: true,
            IsPaused: false,
            Shuffle: false,
            Repeat: RepeatMode.Off,
            PrevTracks: Array.Empty<SnapshotTrack>(),
            NextTracks: new[]
            {
                new SnapshotTrack("spotify:track:next", "un", "context", "Next", "Album",
                    "spotify:artist:a", "Artist", "spotify:album:al", "", false, 4,
                    new Dictionary<string, string>(StringComparer.Ordinal)),
            },
            ContextMetadata: new Dictionary<string, string>(StringComparer.Ordinal),
            ContextIndex: 3,
            InteractionId: "interaction",
            PageInstanceId: "page",
            QueueRevision: "1",
            SessionId: "session",
            PlaybackId: "playback",
            HasBeenPlayingForMs: 0,
            StartedPlayingAtMs: 9000);

    static string TrackPlayerOf(LocalPlaybackSnapshot snap, PlayableKind? kind)
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000, currentKind: kind)).Device.PlayerState;
        return ps.Track.Metadata["track_player"];
    }

    [Theory]
    [InlineData(PlayableKind.Video, "video")]
    [InlineData(PlayableKind.Audio, "audio")]
    [InlineData(PlayableKind.LocalFile, "audio")]
    public void BuildPutState_TrackPlayer_FollowsTheCurrentMediaKind_ForAPlainTrack(PlayableKind kind, string expected)
        => Assert.Equal(expected, TrackPlayerOf(Snap(hasVideo: false), kind));

    [Theory]
    [InlineData(PlayableKind.Video, "video")]
    [InlineData(PlayableKind.Audio, "audio")]
    [InlineData(PlayableKind.LocalFile, "audio")]
    public void BuildPutState_TrackPlayer_FollowsTheCurrentMediaKind_ForAVideoCapableTrack(PlayableKind kind, string expected)
    {
        // has-video AND a stale metadata claim of "video" — the live kind still wins (this is the M0 defect: the app used to
        // advertise track_player="video" while the audio host was the one playing).
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["track_player"] = "video",
            ["media.type"] = "video",
        };
        Assert.Equal(expected, TrackPlayerOf(Snap(hasVideo: true, meta), kind));
    }

    [Fact]
    public void BuildPutState_TrackPlayer_KeepsTheWireHeuristic_WhenNoKindIsSupplied()
    {
        // The empty NewConnection announce (and any caller that genuinely doesn't know the kind) keeps the pre-M0 behavior.
        Assert.Equal("audio", TrackPlayerOf(Snap(hasVideo: false), null));
        Assert.Equal("video", TrackPlayerOf(Snap(hasVideo: true), null));
    }

    [Fact]
    public void BuildPutState_CurrentMediaKind_DoesNotLeakOntoNextTracks()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, Snap(hasVideo: false), 1, true,
            nowMs: 10_000, currentKind: PlayableKind.Video)).Device.PlayerState;

        Assert.Equal("video", ps.Track.Metadata["track_player"]);
        Assert.Equal("audio", ps.NextTracks[0].Metadata["track_player"]);   // next-up is not the current media
    }

    [Fact]
    public void BuildPutState_Paused_KeepsIsPlayingTrueAndAddsPauseRestrictions()
    {
        var builder = new ConnectStateBuilder("device", "Wavee");
        var snap = new LocalPlaybackSnapshot(
            Track: new SnapshotTrack("spotify:track:t", "uid", "context", "Title", "Album",
                "spotify:artist:a", "Artist", "spotify:album:al", "", false, 0,
                new Dictionary<string, string>()),
            ContextUri: "spotify:playlist:p",
            PositionMs: 131_090,
            DurationMs: 227_866,
            IsPlaying: true,
            IsPaused: true,
            Shuffle: false,
            Repeat: RepeatMode.Off,
            PrevTracks: Array.Empty<SnapshotTrack>(),
            NextTracks: Array.Empty<SnapshotTrack>(),
            ContextMetadata: new Dictionary<string, string>(),
            ContextIndex: 0,
            InteractionId: "interaction",
            PageInstanceId: "page",
            QueueRevision: "1",
            SessionId: "session",
            PlaybackId: "playback",
            HasBeenPlayingForMs: 0,
            StartedPlayingAtMs: 9000);

        var ps = PutStateRequest.Parser.ParseFrom(builder.BuildPutState(
            PutStateReasonKind.PlayerStateChanged, snap, 1, true, nowMs: 10_000)).Device.PlayerState;

        Assert.True(ps.IsPlaying);
        Assert.True(ps.IsPaused);
        Assert.Equal(0.0, ps.PlaybackSpeed);
        Assert.Contains("already_paused", ps.Restrictions.DisallowPausingReasons);
        Assert.Contains("no_prev_track", ps.Restrictions.DisallowSkippingPrevReasons);
        Assert.Empty(ps.Restrictions.DisallowResumingReasons);
    }
}
