using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
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
    public void Play_OrderedPages_PreserveSuppliedOrder_AndSkipTo_NoMetadataNoOptions()
    {
        // The embedded page order is VERBATIM (no re-sort), skip_to carries the start row's uri/uid/index, and the
        // envelope stays honest: no per-track metadata + no command.options (no decoded play capture confirms either).
        var json = OutboundEnvelope.Play(
            "us", "spotify:playlist:p", null,
            skipToIndex: 1, skipToTrackUri: "spotify:track:a", skipToTrackUid: "ua",
            pageTracks: new[]
            {
                new QueuedRef("spotify:track:c", "uc"),
                new QueuedRef("spotify:track:a", "ua"),
                new QueuedRef("spotify:track:b", "ub"),
            },
            shuffle: false, featureIdentifier: "playlist", featureVersion: "v",
            commandId: "c", intentId: "i", initiatedTimeMs: 1);

        using var doc = JsonDocument.Parse(json);
        var cmd = doc.RootElement.GetProperty("command");
        var tracks = cmd.GetProperty("context").GetProperty("pages")[0].GetProperty("tracks");
        Assert.Equal(3, tracks.GetArrayLength());
        Assert.Equal("spotify:track:c", tracks[0].GetProperty("uri").GetString());   // verbatim, NOT re-sorted
        Assert.Equal("spotify:track:a", tracks[1].GetProperty("uri").GetString());
        Assert.Equal("spotify:track:b", tracks[2].GetProperty("uri").GetString());
        Assert.Equal("uc", tracks[0].GetProperty("uid").GetString());

        var skip = cmd.GetProperty("prepare_play_options").GetProperty("skip_to");
        Assert.Equal("spotify:track:a", skip.GetProperty("track_uri").GetString());
        Assert.Equal("ua", skip.GetProperty("track_uid").GetString());
        Assert.Equal(1, skip.GetProperty("track_index").GetInt32());

        Assert.False(tracks[0].TryGetProperty("metadata", out _));   // honest-shape guard
        Assert.False(cmd.TryGetProperty("options", out _));          // honest-shape guard
    }

    [Fact]
    public void AddToQueue_SingleTrack_MatchesDesktopEnvelopeShape()
    {
        var json = OutboundEnvelope.AddToQueue(
            fromDeviceId: "us", trackUri: "spotify:track:x", trackUid: "",
            overrideRestrictions: false, onlyForLocalDevice: false, systemInitiated: false,
            commandId: "cmd1", intentId: "intent1", initiatedTimeMs: 7);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("intent1", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("add_to_queue", cmd.GetProperty("endpoint").GetString());
        Assert.False(cmd.TryGetProperty("uri", out _));                 // NOT the legacy flat command.uri
        Assert.False(cmd.TryGetProperty("prepare_play_options", out _));

        var track = cmd.GetProperty("track");
        Assert.Equal("spotify:track:x", track.GetProperty("uri").GetString());
        Assert.Equal("", track.GetProperty("uid").GetString());         // present even when empty
        Assert.Equal(JsonValueKind.Object, track.GetProperty("metadata").ValueKind);
        Assert.Empty(track.GetProperty("metadata").EnumerateObject());  // explicit {}

        var opts = cmd.GetProperty("options");
        Assert.False(opts.GetProperty("override_restrictions").GetBoolean());
        Assert.False(opts.GetProperty("only_for_local_device").GetBoolean());
        Assert.False(opts.GetProperty("system_initiated").GetBoolean());

        Assert.Equal("us", cmd.GetProperty("logging_params").GetProperty("device_identifier").GetString());
    }

    [Fact]
    public void AddToQueue_UidPresentWhenProvided()
    {
        var json = OutboundEnvelope.AddToQueue("us", "spotify:track:x", "q1", false, false, false, "c", "i", 1);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("q1", doc.RootElement.GetProperty("command").GetProperty("track").GetProperty("uid").GetString());
    }

    [Fact]
    public void SetQueue_EmitsFullSnapshotShape()
    {
        var json = OutboundEnvelope.SetQueue("us", 2487768622004702740UL,
            prevTracks: Array.Empty<QueueWireEntry>(),
            nextTracks: new[]
            {
                new QueueWireEntry("spotify:track:n1", "q1", true),
                new QueueWireEntry("spotify:track:n2", "", true),
                new QueueWireEntry("spotify:track:cx", "hex", false),
            },
            commandId: "c", intentId: "i", initiatedTimeMs: 5);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wlan", root.GetProperty("connection_type").GetString());
        Assert.Equal("i", root.GetProperty("intent_id").GetString());

        var cmd = root.GetProperty("command");
        Assert.Equal("set_queue", cmd.GetProperty("endpoint").GetString());
        Assert.Equal(JsonValueKind.Number, cmd.GetProperty("queue_revision").ValueKind);   // bare number, not a string
        Assert.Equal(2487768622004702740UL, cmd.GetProperty("queue_revision").GetUInt64());
        Assert.Empty(cmd.GetProperty("prev_tracks").EnumerateArray());                     // no history model → empty

        var next = cmd.GetProperty("next_tracks");
        Assert.Equal(3, next.GetArrayLength());

        // queue rows: provider:"queue" + metadata.is_queued is the STRING "true"
        Assert.Equal("queue", next[0].GetProperty("provider").GetString());
        var md0 = next[0].GetProperty("metadata").GetProperty("is_queued");
        Assert.Equal(JsonValueKind.String, md0.ValueKind);
        Assert.Equal("true", md0.GetString());
        Assert.Equal("q1", next[0].GetProperty("uid").GetString());
        Assert.Equal("", next[1].GetProperty("uid").GetString());                          // uid always written, may be ""

        // context row: provider:"context", no is_queued
        Assert.Equal("context", next[2].GetProperty("provider").GetString());
        Assert.False(next[2].GetProperty("metadata").TryGetProperty("is_queued", out _));

        // every entry: removed/blocked empty arrays + restrictions = the 22-key object, each an empty array
        foreach (var entry in next.EnumerateArray())
        {
            Assert.Empty(entry.GetProperty("removed").EnumerateArray());
            Assert.Empty(entry.GetProperty("blocked").EnumerateArray());
            int rk = 0;
            foreach (var p in entry.GetProperty("restrictions").EnumerateObject()) { rk++; Assert.Equal(JsonValueKind.Array, p.Value.ValueKind); }
            Assert.Equal(22, rk);
        }

        var opts = cmd.GetProperty("options");
        Assert.False(opts.GetProperty("override_restrictions").GetBoolean());
        Assert.False(opts.GetProperty("only_for_local_device").GetBoolean());
        Assert.False(opts.GetProperty("system_initiated").GetBoolean());
        Assert.Equal("us", cmd.GetProperty("logging_params").GetProperty("device_identifier").GetString());
    }

    [Fact]
    public void SetQueue_RevisionExceedingInt64_SerializesAsBareNumber()
    {
        var json = OutboundEnvelope.SetQueue("us", 10355548321371651421UL,
            Array.Empty<QueueWireEntry>(), new[] { new QueueWireEntry("spotify:track:x", "", true) }, "c", "i", 1);
        Assert.Contains("\"queue_revision\":10355548321371651421", json);   // unquoted bare number, > Int64.MaxValue
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(10355548321371651421UL, doc.RootElement.GetProperty("command").GetProperty("queue_revision").GetUInt64());
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
    public void Transfer_MatchesDesktopConnectTransferBodyShape()
    {
        var json = OutboundEnvelope.Transfer("transfer-id", "command-id", "interaction-id", "premium");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var options = root.GetProperty("options");
        Assert.Equal("restore", options.GetProperty("restore_paused").GetString());
        Assert.Equal("extrapolate", options.GetProperty("restore_position").GetString());
        Assert.Equal("only_current", options.GetProperty("restore_track").GetString());
        Assert.Equal("premium", options.GetProperty("license").GetString());
        Assert.Equal("transfer-id", root.GetProperty("transfer_intent_id").GetString());
        Assert.Equal("command-id", root.GetProperty("command_id").GetString());
        Assert.Equal("interaction-id", root.GetProperty("interaction_id").GetString());
    }

    [Fact]
    public async Task LiveOutboundControl_Transfer_PostsToConnectTransferEndpoint()
    {
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us", () => "conn-123").TransferAsync("active", "target");
        Assert.Equal("/connect-state/v1/connect/transfer/from/active/to/target", stub.LastRequestRoute);
        Assert.Equal("POST", stub.LastRequestMethod);
        Assert.Equal("application/x-www-form-urlencoded", stub.LastRequestHeaders!["Content-Type"]);
        Assert.Equal("conn-123", stub.LastRequestHeaders!["X-Spotify-Connection-Id"]);

        using var doc = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.Equal("restore", doc.RootElement.GetProperty("options").GetProperty("restore_paused").GetString());
        Assert.Equal("extrapolate", doc.RootElement.GetProperty("options").GetProperty("restore_position").GetString());
        Assert.Equal("only_current", doc.RootElement.GetProperty("options").GetProperty("restore_track").GetString());
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

    [Fact]
    public async Task LiveOutboundControl_Command_GzipFraming_HeadersRouteAndBody()
    {
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us", () => "conn-123").SendAsync("target", "{\"command\":{}}");

        Assert.Equal("/connect-state/v1/player/command/from/us/to/target", stub.LastRequestRoute);
        Assert.Equal("POST", stub.LastRequestMethod);

        var h = stub.LastRequestHeaders!;
        Assert.NotNull(h);
        Assert.Equal("application/x-www-form-urlencoded", h["Content-Type"]);
        Assert.Equal("gzip", h["X-Transfer-Encoding"]);                       // custom header — NOT Content-Encoding
        Assert.Equal("conn-123", h["X-Spotify-Connection-Id"]);
        Assert.False(h.ContainsKey("Content-Encoding"));                      // would make intermediaries auto-inflate
        Assert.False(h.ContainsKey("client-token"));                         // middleware stamps it downstream, not here

        var body = stub.LastRequestBody!;
        Assert.Equal(0x1F, body[0]); Assert.Equal(0x8B, body[1]);            // gzip magic bytes
        Assert.Equal("{\"command\":{}}", Encoding.UTF8.GetString(HttpCompression.Gunzip(body)));
    }

    [Fact]
    public async Task LiveOutboundControl_Command_OmitsConnectionIdWhenAbsent()
    {
        var noDelegate = new StubTransport();
        await new LiveOutboundControl(noDelegate, "us").SendAsync("t", "{\"command\":{}}");

        var emptyConn = new StubTransport();
        await new LiveOutboundControl(emptyConn, "us", () => "").SendAsync("t", "{\"command\":{}}");

        var nullConn = new StubTransport();
        await new LiveOutboundControl(nullConn, "us", () => null).SendAsync("t", "{\"command\":{}}");

        foreach (var stub in new[] { noDelegate, emptyConn, nullConn })
        {
            var h = stub.LastRequestHeaders!;
            Assert.False(h.ContainsKey("X-Spotify-Connection-Id"));          // omitted when delegate null / returns null|""
            Assert.Equal("application/x-www-form-urlencoded", h["Content-Type"]);   // framing still present
            Assert.Equal("gzip", h["X-Transfer-Encoding"]);
        }
    }

    [Fact]
    public async Task LiveOutboundControl_Volume_SendsNoCommandFraming()
    {
        // The volume PUT must not pick up the player-command gzip/form framing even when a connection-id is available.
        var stub = new StubTransport();
        await new LiveOutboundControl(stub, "us", () => "conn-123").SetVolumeAsync("target", 19496);
        Assert.Equal("PUT", stub.LastRequestMethod);
        Assert.Null(stub.LastRequestHeaders);   // SetVolumeAsync passes no headers → application/protobuf default, no gzip
    }
}
