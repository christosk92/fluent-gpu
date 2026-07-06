using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Wavee.Backend;

// ── Outbound Connect command envelopes (proto-free, the wire shape the real desktop sends) ────────────────────────────
// The body the controller POSTs to /connect-state/v1/player/command/from/{us}/to/{target}. Built faithfully from the
// decoded desktop captures (the envelope is a fixed wire spec — lifted verbatim): a "play" carries an OPAQUE context uri
// (+ play_origin / prepare_play_options.skip_to / play_options / logging_params), wrapped as {command, connection_type,
// intent_id}. Collections are URI-only (sort/filter rides on context.url); a non-collection with custom-ordered pageTracks
// embeds context.pages with uids; otherwise URI-only. IDs/time are passed in (so the builder stays pure + unit-testable).
public static class OutboundEnvelope
{
    public const string DefaultFeatureVersion = "harmony";

    public static string Play(
        string fromDeviceId, string contextUri, string? contextUrl,
        int? skipToIndex, string? skipToTrackUri, string? skipToTrackUid,
        IReadOnlyList<QueuedRef>? pageTracks, bool shuffle,
        string featureIdentifier, string featureVersion,
        string commandId, string intentId, long initiatedTimeMs)
    {
        bool isCollection = ContextResolve.IsCollection(contextUri);
        var buf = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "play");

            w.WriteStartObject("context");
            w.WriteString("entity_uri", contextUri);
            w.WriteString("uri", contextUri);
            w.WriteString("url", contextUrl ?? ("context://" + contextUri));
            w.WriteStartObject("metadata"); w.WriteEndObject();
            if (!isCollection && pageTracks is { Count: > 0 })   // a custom-ordered (sorted/filtered) page → embed with uids
            {
                w.WriteStartArray("pages");
                w.WriteStartObject();
                w.WriteStartArray("tracks");
                foreach (var t in pageTracks)
                {
                    w.WriteStartObject();
                    w.WriteString("uri", t.Uri);
                    if (!string.IsNullOrEmpty(t.Uid)) w.WriteString("uid", t.Uid);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndArray();
            }
            w.WriteEndObject();   // context

            w.WriteStartObject("play_origin");
            w.WriteString("feature_identifier", featureIdentifier);
            w.WriteString("feature_version", featureVersion);
            w.WriteString("referrer_identifier", "harmony");
            w.WriteEndObject();

            w.WriteStartObject("prepare_play_options");
            w.WriteBoolean("always_play_something", false);
            if (skipToTrackUri is not null || skipToTrackUid is not null || skipToIndex is >= 0)
            {
                w.WriteStartObject("skip_to");
                if (skipToTrackUri is not null) w.WriteString("track_uri", skipToTrackUri);
                if (skipToTrackUid is not null) w.WriteString("track_uid", skipToTrackUid);
                if (skipToIndex is int i && i >= 0) w.WriteNumber("track_index", i);
                w.WriteEndObject();
            }
            w.WriteString("license", "premium");
            w.WriteStartObject("player_options_override");
            w.WriteBoolean("shuffling_context", shuffle);
            w.WriteEndObject();
            w.WriteEndObject();   // prepare_play_options

            w.WriteStartObject("play_options");
            w.WriteString("reason", "interactive");
            w.WriteString("operation", "replace");
            w.WriteString("trigger", "immediately");
            w.WriteEndObject();

            WriteLoggingParams(w, fromDeviceId, commandId, initiatedTimeMs);
            w.WriteEndObject();   // command
            WriteTail(w, intentId);
            w.WriteEndObject();   // root
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // A non-play command (pause/resume/seek_to/skip_*/set_*/transfer): the same envelope with the verb's args + logging_params.
    public static string Command(string fromDeviceId, string endpoint, (string Key, object Value)[] args,
                                 string commandId, string intentId, long initiatedTimeMs)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", endpoint);
            foreach (var (k, v) in args)
            {
                switch (v)
                {
                    case bool b: w.WriteBoolean(k, b); break;
                    case long l: w.WriteNumber(k, l); break;
                    case int i: w.WriteNumber(k, i); break;
                    default: w.WriteString(k, v?.ToString() ?? ""); break;
                }
            }
            WriteLoggingParams(w, fromDeviceId, commandId, initiatedTimeMs);
            w.WriteEndObject();   // command
            WriteTail(w, intentId);
            w.WriteEndObject();   // root
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // add_to_queue: a SINGLE track as command.track {uri,uid,metadata} + command.options (the desktop shape). uid and
    // metadata are ALWAYS written, even when empty (NOT the play builder's omit-if-empty uid rule) — the real desktop
    // sends uid:"" + metadata:{} for a fresh add. Replaces the legacy flat command.uri form.
    public static string AddToQueue(string fromDeviceId, string trackUri, string trackUid,
        bool overrideRestrictions, bool onlyForLocalDevice, bool systemInitiated,
        string commandId, string intentId, long initiatedTimeMs)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "add_to_queue");
            w.WriteStartObject("track");
            w.WriteString("uri", trackUri);
            w.WriteString("uid", trackUid ?? "");          // ALWAYS written, even ""
            w.WriteStartObject("metadata"); w.WriteEndObject();   // explicit {}
            w.WriteEndObject();   // track
            w.WriteStartObject("options");
            w.WriteBoolean("override_restrictions", overrideRestrictions);
            w.WriteBoolean("only_for_local_device", onlyForLocalDevice);
            w.WriteBoolean("system_initiated", systemInitiated);
            w.WriteEndObject();   // options
            WriteLoggingParams(w, fromDeviceId, commandId, initiatedTimeMs);
            w.WriteEndObject();   // command
            WriteTail(w, intentId);
            w.WriteEndObject();   // root
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // set_queue: a full queue snapshot — used to insert tracks as "play next" WITHOUT replacing the context. next_tracks is
    // the user-queue block (provider:"queue") followed by the context continuation (provider:"context"); the current track
    // is implicit (in neither array). queue_revision is a bare UNSIGNED number that can EXCEED Int64 (ulong, never long).
    public static string SetQueue(string fromDeviceId, ulong queueRevision,
        IReadOnlyList<QueueWireEntry> prevTracks, IReadOnlyList<QueueWireEntry> nextTracks,
        string commandId, string intentId, long initiatedTimeMs)
    {
        var buf = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "set_queue");
            w.WriteNumber("queue_revision", queueRevision);   // ulong overload → bare number, handles > Int64
            w.WriteStartArray("prev_tracks");
            foreach (var e in prevTracks) WriteQueueEntry(w, e);
            w.WriteEndArray();
            w.WriteStartArray("next_tracks");
            foreach (var e in nextTracks) WriteQueueEntry(w, e);
            w.WriteEndArray();
            w.WriteStartObject("options");
            w.WriteBoolean("override_restrictions", false);
            w.WriteBoolean("only_for_local_device", false);
            w.WriteBoolean("system_initiated", false);
            w.WriteEndObject();
            WriteLoggingParams(w, fromDeviceId, commandId, initiatedTimeMs);
            w.WriteEndObject();   // command
            WriteTail(w, intentId);
            w.WriteEndObject();   // root
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    static void WriteQueueEntry(Utf8JsonWriter w, in QueueWireEntry e)
    {
        w.WriteStartObject();
        w.WriteString("uri", e.Uri);
        w.WriteString("uid", e.Uid ?? "");                       // always written, may be ""
        w.WriteStartObject("metadata");
        if (e.IsQueued) w.WriteString("is_queued", "true");      // STRING "true" — NOT a boolean (matches the capture)
        w.WriteEndObject();
        w.WriteString("provider", e.IsQueued ? "queue" : "context");
        w.WriteStartArray("removed"); w.WriteEndArray();
        w.WriteStartArray("blocked"); w.WriteEndArray();
        w.WriteStartObject("restrictions"); WriteEmptyRestrictions(w); w.WriteEndObject();
        w.WriteEndObject();
    }

    // The 22 disallow_*_reasons keys the captured set_queue carries on every entry, each an empty array.
    static void WriteEmptyRestrictions(Utf8JsonWriter w)
    {
        foreach (var k in RestrictionKeys) { w.WriteStartArray(k); w.WriteEndArray(); }
    }

    static readonly string[] RestrictionKeys =
    {
        "disallow_peeking_prev_reasons", "disallow_peeking_next_reasons", "disallow_skipping_prev_reasons",
        "disallow_skipping_next_reasons", "disallow_pausing_reasons", "disallow_resuming_reasons",
        "disallow_toggling_repeat_context_reasons", "disallow_toggling_repeat_track_reasons",
        "disallow_toggling_shuffle_reasons", "disallow_set_queue_reasons", "disallow_add_to_queue_reasons",
        "disallow_seeking_reasons", "disallow_interrupting_playback_reasons", "disallow_transferring_playback_reasons",
        "disallow_remote_control_reasons", "disallow_inserting_into_next_tracks_reasons",
        "disallow_inserting_into_context_tracks_reasons", "disallow_reordering_in_next_tracks_reasons",
        "disallow_reordering_in_context_tracks_reasons", "disallow_removing_from_next_tracks_reasons",
        "disallow_removing_from_context_tracks_reasons", "disallow_updating_context_reasons",
    };

    static void WriteLoggingParams(Utf8JsonWriter w, string deviceId, string commandId, long initiatedTimeMs)
    {
        w.WriteStartObject("logging_params");
        w.WriteNumber("command_initiated_time", initiatedTimeMs);
        w.WriteStartArray("page_instance_ids"); w.WriteEndArray();
        w.WriteStartArray("interaction_ids"); w.WriteEndArray();
        w.WriteString("device_identifier", deviceId);
        w.WriteString("command_id", commandId);
        w.WriteEndObject();
    }

    static void WriteTail(Utf8JsonWriter w, string intentId)
    {
        w.WriteString("connection_type", "wlan");
        w.WriteString("intent_id", intentId);
    }

    // Volume is NOT a player/command verb — it's a dedicated PUT /connect-state/v1/connect/volume/from/{us}/to/{target}
    // with a protobuf SetVolumeCommand body. That message is a fixed 3-field wire spec (volume / empty logging_params /
    // connection_type), hand-encoded here so the outbound path stays proto-free. Round-trips to the captured bytes
    // (volume 19496 → 08 a8 98 01 1a 00 22 04 'wlan').
    public static string Transfer(string transferIntentId, string commandId, string interactionId, string license)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteStartObject("options");
            w.WriteString("restore_paused", "restore");
            w.WriteString("restore_position", "extrapolate");
            w.WriteString("restore_track", "only_current");
            w.WriteString("license", license);
            w.WriteEndObject();
            w.WriteString("transfer_intent_id", transferIntentId);
            w.WriteString("command_id", commandId);
            w.WriteString("interaction_id", interactionId);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    public static byte[] ConnectVolumeBody(int volume0_65535)
    {
        int v = Math.Clamp(volume0_65535, 0, 65535);
        var b = new List<byte>(12) { 0x08 };          // field 1 (volume), varint
        for (uint u = (uint)v; ; )
        {
            if (u >= 0x80) { b.Add((byte)(u | 0x80)); u >>= 7; }
            else { b.Add((byte)u); break; }
        }
        b.Add(0x1a); b.Add(0x00);                      // field 3 (logging_params): empty message
        b.Add(0x22); b.Add(0x04);                      // field 4 (connection_type): length-4 string
        b.Add((byte)'w'); b.Add((byte)'l'); b.Add((byte)'a'); b.Add((byte)'n');
        return b.ToArray();
    }
}
