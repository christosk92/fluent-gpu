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
