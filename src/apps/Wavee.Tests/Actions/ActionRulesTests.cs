using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

// The pure decision core behind the action predicates (Actions/ActionRules.cs) + the device-gated queue verbs
// (DetailQueueActions) the PlayNext/AddToQueue actions ride: no remote device → the call still fires (the player
// surfaces the standard "choose a remote device" prompt) but the verb reports 0 → the caller shows no success toast.
public class ActionRulesTests
{
    // ── ToggleLike checked-state: checked iff ALL saved ────────────────────────────────────────────────────────────
    [Fact]
    public void AllSaved_TrueOnlyWhenEveryTrackSaved()
    {
        var saved = new HashSet<string> { "spotify:track:a" };
        var a = T.Mk("a");
        var b = T.Mk("b");

        Assert.True(ActionRules.AllSaved(new[] { a }, saved.Contains));
        Assert.False(ActionRules.AllSaved(new[] { a, b }, saved.Contains));   // 1 of 2 saved → unchecked

        saved.Add("spotify:track:b");
        Assert.True(ActionRules.AllSaved(new[] { a, b }, saved.Contains));
    }

    [Fact]
    public void AllSaved_EmptySet_OrUrilessTrack_IsFalse()
    {
        Assert.False(ActionRules.AllSaved(Array.Empty<Track>(), _ => true));
        var uriless = T.Mk("x", uriOverride: "");
        Assert.False(ActionRules.AllSaved(new[] { uriless }, _ => true));
    }

    // ── Remove-from-this-playlist gate: editable host + resolved rows ─────────────────────────────────────────────
    [Fact]
    public void CanRemoveFromPlaylist_RequiresEditableCapsAndRows()
    {
        Assert.False(ActionRules.CanRemoveFromPlaylist(null));   // album/artist surface — no host, no Remove row

        var rows = new[] { new PlaylistRowRef(0, "spotify:track:a", "uid-a") };
        var readOnly = new PlaylistHost("spotify:playlist:p",
            new PlaylistCapabilities(CanView: true, CanEditItems: false, CanEditMetadata: false, IsCollaborative: false, IsOwner: false), rows);
        Assert.False(ActionRules.CanRemoveFromPlaylist(readOnly));   // foreign playlist → absent

        var editableNoRows = new PlaylistHost("spotify:playlist:p",
            new PlaylistCapabilities(true, true, true, false, true), Array.Empty<PlaylistRowRef>());
        Assert.False(ActionRules.CanRemoveFromPlaylist(editableNoRows));

        var editable = new PlaylistHost("spotify:playlist:p",
            new PlaylistCapabilities(true, true, true, false, true), rows);
        Assert.True(ActionRules.CanRemoveFromPlaylist(editable));    // owned playlist → present
    }

    // ── Container navigation routes (the app's go(key, name) scheme) ──────────────────────────────────────────────
    [Fact]
    public void RouteFor_MapsContainerKinds()
    {
        var album = ActionTarget.ForAlbum("spotify:album:x", "A");
        var artist = ActionTarget.ForArtist("spotify:artist:y", "B");
        var playlist = ActionTarget.ForPlaylist("spotify:playlist:z", "C");
        var liked = ActionTarget.ForPlaylist("spotify:collection:tracks", "Liked Songs");
        var tracks = ActionTarget.ForTracks(new[] { T.Mk("a") });

        Assert.Equal("album:spotify:album:x", ActionRules.RouteFor(in album));
        Assert.Equal("artist:spotify:artist:y", ActionRules.RouteFor(in artist));
        Assert.Equal("pl:spotify:playlist:z", ActionRules.RouteFor(in playlist));
        Assert.Equal("liked", ActionRules.RouteFor(in liked));
        Assert.Null(ActionRules.RouteFor(in tracks));
    }

    // ── PlayNext / AddToEnd device gate (regression: no device → fire + report 0 → no success toast) ──────────────
    [Fact]
    public void PlayNext_NoActiveDevice_FiresButReportsZero()
    {
        var p = new RecordingPlayer();   // ActiveDeviceId null — no remote device
        int n = DetailQueueActions.PlayNext(p, new[] { T.Mk("a"), T.Mk("b") });

        Assert.Equal(0, n);                          // caller shows NO "added" toast
        Assert.Single(p.PlayNextCalls);              // the intent still fired → the standard device prompt path
        Assert.Equal(2, p.PlayNextCalls[0].Count);
    }

    [Fact]
    public void PlayNext_WithActiveDevice_ReportsCount()
    {
        var p = new RecordingPlayer { ActiveDeviceId = "dev-1" };
        int n = DetailQueueActions.PlayNext(p, new[] { T.Mk("a"), T.Mk("b"), T.Mk("c") });

        Assert.Equal(3, n);
        Assert.Single(p.PlayNextCalls);
        Assert.Equal("spotify:track:a", p.PlayNextCalls[0][0].Uri);
        Assert.Equal("uid-a", p.PlayNextCalls[0][0].Uid);   // ContextUid rides the wire row
    }

    [Fact]
    public void AddToEnd_NoActiveDevice_EnqueuesNothing()
    {
        var p = new RecordingPlayer();
        int n = DetailQueueActions.AddToEnd(p, new[] { T.Mk("a") });

        Assert.Equal(0, n);
        Assert.Empty(p.Enqueued);   // remote-only verb: nothing silently queued locally
    }

    [Fact]
    public void AddToEnd_WithActiveDevice_EnqueuesEach()
    {
        var p = new RecordingPlayer { ActiveDeviceId = "dev-1" };
        int n = DetailQueueActions.AddToEnd(p, new[] { T.Mk("a"), T.Mk("b") });

        Assert.Equal(2, n);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, p.Enqueued);
    }

    [Fact]
    public void PlayNext_NullPlayer_ReportsZero()
        => Assert.Equal(0, DetailQueueActions.PlayNext(null, new[] { T.Mk("a") }));

    // ── View-credits gate: a single track carrying a primary artist uri (the NPV fetch keys off artistUri+trackUri) ──
    [Fact]
    public void CanViewCredits_SingleTrackWithPrimaryArtist_IsTrue()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a") });   // T.Mk seeds spotify:artist:ar0
        Assert.True(ActionRules.CanViewCredits(in target));
    }

    [Fact]
    public void CanViewCredits_MultiSelect_IsFalse()   // single-track only
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a"), T.Mk("b") });
        Assert.False(ActionRules.CanViewCredits(in target));
    }

    [Fact]
    public void CanViewCredits_TrackWithNoArtists_IsFalse()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a", artists: 0) });
        Assert.False(ActionRules.CanViewCredits(in target));
    }

    // ── Radio gates: song radio = single spotify:track; artist radio = an Artist target with a spotify:artist uri ─────
    [Fact]
    public void CanStartTrackRadio_SingleSpotifyTrack_IsTrue()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a") });   // spotify:track:a
        Assert.True(ActionRules.CanStartTrackRadio(in target));
    }

    [Fact]
    public void CanStartTrackRadio_MultiSelect_OrNonSpotifyUri_IsFalse()
    {
        var multi = ActionTarget.ForTracks(new[] { T.Mk("a"), T.Mk("b") });
        Assert.False(ActionRules.CanStartTrackRadio(in multi));            // radio seeds a single track

        var local = ActionTarget.ForTracks(new[] { T.Mk("x", uriOverride: "local:file:1") });
        Assert.False(ActionRules.CanStartTrackRadio(in local));           // non-spotify:track uri disabled
    }

    [Fact]
    public void CanStartArtistRadio_ArtistTarget_IsTrue_OthersFalse()
    {
        Assert.True(ActionRules.CanStartArtistRadio(ActionTarget.ForArtist("spotify:artist:y", "B")));
        Assert.False(ActionRules.CanStartArtistRadio(ActionTarget.ForAlbum("spotify:album:x", "A")));   // not an artist
        Assert.False(ActionRules.CanStartArtistRadio(ActionTarget.ForArtist("local:artist:z", "C")));   // non-spotify uri
    }

    [Fact]
    public void CanViewCredits_ArtistWithoutUri_IsFalse()
    {
        var track = new Track("z", "spotify:track:z", "Track z",
            new[] { new ArtistRef("", "", "Unknown") },
            new AlbumRef("al1", "spotify:album:al1", "Album One"), 180_000, false, null);
        var target = ActionTarget.ForTracks(new[] { track });
        Assert.False(ActionRules.CanViewCredits(in target));
    }
}
