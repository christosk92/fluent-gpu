using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Stage D — the cluster → IPlaybackState projection, reconciliation, and the device roster (proto-free, hand-built deltas).
public class ConnectProjectionTests
{
    static RemoteTrack Trk(string uri, string title, long dur) =>
        new(uri, title, "Artist", "spotify:artist:a", "Album", "spotify:album:al", "https://img/x", dur);

    static ClusterDelta Cluster(string active, bool playing, RemoteTrack? track = null, long pos = 0,
        IReadOnlyList<ConnectDeviceRow>? devices = null) =>
        new(active, track is not null, track ?? default, "spotify:playlist:ctx",
            playing, !playing, false, pos, 0, 0, track?.DurationMs ?? 0, false, RepeatMode.Off,
            devices ?? Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    [Fact]
    public void OnCluster_ViewerMode_FoldsTrackPlayStateContext_AndAnchorsPosition()
    {
        long now = 1000;
        var p = new NowPlayingProjection("us", () => now);
        int changes = 0;
        using var s = p.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(_ => changes++));

        p.OnCluster(Cluster("other-device", playing: true, Trk("spotify:track:t1", "Song", 200000), pos: 5000));

        Assert.Equal("spotify:track:t1", p.CurrentTrack!.Uri);
        Assert.Equal("Song", p.CurrentTrack.Title);
        Assert.Equal("Artist", p.CurrentTrack.Artists[0].Name);
        Assert.True(p.IsPlaying);
        Assert.Equal("spotify:playlist:ctx", p.ContextUri);
        Assert.False(p.WeAreActive);
        Assert.True(changes >= 1);

        now = 3000;
        Assert.Equal(7000, p.PositionMs);   // 5000 + (3000-1000), anchored at receipt
    }

    [Fact]
    public void OnCluster_NoActiveDevice_ClampsToPaused()
    {
        var p = new NowPlayingProjection("us");
        p.OnCluster(Cluster("", playing: true, Trk("spotify:track:x", "X", 1000)));
        Assert.False(p.IsPlaying);   // nobody active → we are not playing
    }

    [Fact]
    public void Reconciliation_StaleClusterDoesNotRevertOptimisticLocalCommand()
    {
        long now = 0;
        var p = new NowPlayingProjection("us", () => now);
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000)));
        Assert.True(p.IsPlaying);

        // local pause (optimistic) + the controller flags the in-flight command
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Paused, 1234));
        p.NoteLocalCommand();
        Assert.False(p.IsPlaying);

        now = 1000;   // a STALE cluster (still shows playing) arrives within the window → must NOT revert
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000)));
        Assert.False(p.IsPlaying);

        now = 5000;   // past the window → the cluster is authoritative again
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000)));
        Assert.True(p.IsPlaying);
    }

    [Fact]
    public void DeviceRoster_MapsRows_WithVolumePercent_AndActiveFlag()
    {
        var d = new LiveConnectDevices();
        IReadOnlyList<PlaybackDevice>? got = null;
        using var s = d.DevicesChanged.Subscribe(ConnectHarness.Obs<IReadOnlyList<PlaybackDevice>>(x => got = x));
        d.Update(new[]
        {
            new ConnectDeviceRow("d1", "Phone", DeviceKind.Phone, true, 32768),
            new ConnectDeviceRow("d2", "Speaker", DeviceKind.Speaker, false, 0),
        });
        Assert.NotNull(got);
        Assert.Equal(2, got!.Count);
        Assert.Equal("Phone", got[0].Name);
        Assert.True(got[0].IsActive);
        Assert.Equal(50, got[0].VolumePercent);   // 32768 / 655.35 ≈ 50%
        Assert.Equal(0, got[1].VolumePercent);
    }

    [Fact]
    public void LocalEvent_DrivesSlab_AndFiresChanges()
    {
        var p = new NowPlayingProjection("us", () => 0);
        var track = new Track("t", "spotify:track:t", "Local",
            new[] { new ArtistRef("a", "spotify:artist:a", "A") }, new AlbumRef("al", "spotify:album:al", "Al"),
            60000, false, null);
        bool fired = false;
        using var s = p.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(_ => fired = true));
        p.OnEvent(new PlaybackEvent(EvKind.Started, track, 0));
        Assert.True(p.IsPlaying);
        Assert.Equal("Local", p.CurrentTrack!.Title);
        Assert.True(fired);
    }

    [Fact]
    public void OnCluster_Restrictions_GateSkipAndSeek_AndVolumeFollowsActiveDevice()
    {
        var p = new NowPlayingProjection("us", () => 0);
        p.OnCluster(new ClusterDelta("other", true, Trk("spotify:ad:x", "Ad", 30000), "ctx",
            true, false, false, 0, 0, 0, 30000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>(),
            DisallowSkipPrev: true, DisallowSkipNext: true, DisallowSeeking: true, OurVolume0_65535: 16384));
        Assert.False(p.CanSkipNext);    // ad → skip disabled
        Assert.False(p.CanSkipPrev);
        Assert.False(p.CanSeek);
        Assert.Equal(0.25, p.Volume, 2);   // 16384/65535 ≈ 0.25 (the active device's volume)
    }
}
