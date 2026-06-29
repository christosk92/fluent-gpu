using System;
using System.Text.Json;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// Phase D: the outbound player-command envelope matches the decoded desktop captures (golden shape), and honors the
// collection-vs-pages rule. Pure (Backend) → unit-tested directly with fixed ids/time.
public class OutboundEnvelopeTests
{
    [Fact]
    public void Play_Album_MatchesDesktopEnvelopeShape()
    {
        var json = OutboundEnvelope.Play(
            fromDeviceId: "dev1", contextUri: "spotify:album:abc", contextUrl: null,
            skipToIndex: 0, skipToTrackUri: "spotify:track:t", skipToTrackUid: "uid1",
            pageTracks: null, shuffle: false,
            featureIdentifier: "album", featureVersion: "xpui-x",
            commandId: "cmd1", intentId: "intent1", initiatedTimeMs: 123);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("intent1", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("play", cmd.GetProperty("endpoint").GetString());

        var ctx = cmd.GetProperty("context");
        Assert.Equal("spotify:album:abc", ctx.GetProperty("uri").GetString());
        Assert.Equal("spotify:album:abc", ctx.GetProperty("entity_uri").GetString());
        Assert.Equal("context://spotify:album:abc", ctx.GetProperty("url").GetString());
        Assert.False(ctx.TryGetProperty("pages", out _));   // plain album → URI-only

        var po = cmd.GetProperty("play_origin");
        Assert.Equal("album", po.GetProperty("feature_identifier").GetString());
        Assert.Equal("xpui-x", po.GetProperty("feature_version").GetString());

        var skip = cmd.GetProperty("prepare_play_options").GetProperty("skip_to");
        Assert.Equal("spotify:track:t", skip.GetProperty("track_uri").GetString());
        Assert.Equal("uid1", skip.GetProperty("track_uid").GetString());
        Assert.Equal(0, skip.GetProperty("track_index").GetInt32());

        var play = cmd.GetProperty("play_options");
        Assert.Equal("interactive", play.GetProperty("reason").GetString());
        Assert.Equal("replace", play.GetProperty("operation").GetString());
        Assert.Equal("immediately", play.GetProperty("trigger").GetString());

        var log = cmd.GetProperty("logging_params");
        Assert.Equal("cmd1", log.GetProperty("command_id").GetString());
        Assert.Equal("dev1", log.GetProperty("device_identifier").GetString());
        Assert.Equal(123, log.GetProperty("command_initiated_time").GetInt64());
    }

    [Fact]
    public void Play_Collection_IsUriOnly_EvenWithPageTracks()
    {
        var json = OutboundEnvelope.Play(
            "dev1", "spotify:user:x:collection", "context://spotify:user:x:collection?sort=added", null, null, null,
            new[] { new QueuedRef("spotify:track:a", "u1") }, false, "collection", "v", "c", "i", 1);

        using var doc = JsonDocument.Parse(json);
        var ctx = doc.RootElement.GetProperty("command").GetProperty("context");
        Assert.False(ctx.TryGetProperty("pages", out _));   // collection → URI-only even when pageTracks supplied
        Assert.Equal("context://spotify:user:x:collection?sort=added", ctx.GetProperty("url").GetString());   // sort rides on url
    }

    [Fact]
    public void Play_NonCollection_WithPageTracks_EmbedsPagesWithUids()
    {
        var json = OutboundEnvelope.Play(
            "dev1", "spotify:playlist:p", null, null, null, null,
            new[] { new QueuedRef("spotify:track:a", "u1"), new QueuedRef("spotify:track:b", "u2") },
            false, "playlist", "v", "c", "i", 1);

        using var doc = JsonDocument.Parse(json);
        var tracks = doc.RootElement.GetProperty("command").GetProperty("context").GetProperty("pages")[0].GetProperty("tracks");
        Assert.Equal(2, tracks.GetArrayLength());
        Assert.Equal("spotify:track:a", tracks[0].GetProperty("uri").GetString());
        Assert.Equal("u1", tracks[0].GetProperty("uid").GetString());
    }

    [Fact]
    public void Command_WrapsEnvelope_WithLoggingParams()
    {
        var json = OutboundEnvelope.Command("dev1", "pause", Array.Empty<(string, object)>(), "c", "i", 5);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("pause", root.GetProperty("command").GetProperty("endpoint").GetString());
        Assert.Equal("dev1", root.GetProperty("command").GetProperty("logging_params").GetProperty("device_identifier").GetString());
    }

    [Fact]
    public void ConnectVolumeBody_RoundTripsToTheCapturedBytes()
    {
        // SetVolumeCommand{ volume=19496, logging_params={}, connection_type="wlan" } == the decoded desktop capture.
        Assert.Equal("CKiYARoAIgR3bGFu", Convert.ToBase64String(OutboundEnvelope.ConnectVolumeBody(19496)));
    }

    [Fact]
    public async Task LiveOutboundControl_SetVolume_PutsToConnectVolumeEndpoint()
    {
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us").SetVolumeAsync("target", 19496);
        Assert.Equal("/connect-state/v1/connect/volume/from/us/to/target", stub.LastRequestRoute);
        Assert.Equal("PUT", stub.LastRequestMethod);
        Assert.Equal("CKiYARoAIgR3bGFu", Convert.ToBase64String(stub.LastRequestBody!));
    }
}
