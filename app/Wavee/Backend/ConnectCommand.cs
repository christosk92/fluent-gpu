using System;
using System.Text.Json;

namespace Wavee.Backend;

// ── Connect remote commands (proto-free; the REQUEST → dispatch → ack path) ───────────────────────────────────────────
// A REQUEST frame's command is JSON: { message_id, sent_by_device_id, command: { endpoint, ...args } } for the real
// hm://connect-state/v1/player/command ident (a legacy form puts the endpoint in the ident tail). The dealer frame `key`
// IS the reply key. Parsed into a flat POD; the full payload bytes ride along so the controller can deep-parse play/transfer.

public enum ConnectCmd
{
    Unknown, Play, Pause, Resume, SeekTo, SkipNext, SkipPrev,
    SetShufflingContext, SetRepeatingContext, SetRepeatingTrack,
    Transfer, AddToQueue, SetQueue, UpdateContext, SetOptions,
}

public readonly record struct ConnectCommand(
    ConnectCmd Kind, string Endpoint, string Key, int MessageId, string SenderDeviceId,
    long SeekToMs, bool BoolArg, byte[] Payload,
    // The optional command.track payload a next_track / skip_next carries (F7): the row the sender is jumping to, uid-first
    // identity + uri fallback. Empty for a bare skip_next (advance one). Trailing defaults so other constructions are unaffected.
    string TrackUri = "", string TrackUid = "")
{
    public static bool TryParse(in WireRequest req, out ConnectCommand cmd)
    {
        cmd = default;
        try
        {
            var parts = req.MessageIdent.Split('/');
            if (parts.Length < 5) return false;   // need hm://connect-state/v1/{endpoint}
            if (req.Command is null || req.Command.Length == 0) return false;

            using var doc = JsonDocument.Parse(req.Command);
            var root = doc.RootElement;
            int messageId = root.TryGetProperty("message_id", out var mid) ? IntLoose(mid) : 0;
            string sender = root.TryGetProperty("sent_by_device_id", out var sd) ? (sd.GetString() ?? "") : "";

            string endpoint;
            JsonElement inner = root;
            string urlEndpoint = parts[^1];
            if (urlEndpoint == "command" && parts.Length >= 6 && parts[^2] == "player")
            {
                if (!root.TryGetProperty("command", out inner)) return false;
                if (!inner.TryGetProperty("endpoint", out var ep)) return false;
                endpoint = ep.GetString()?.ToLowerInvariant() ?? "";
            }
            else endpoint = urlEndpoint.ToLowerInvariant();

            var kind = Map(endpoint);
            long seekMs = 0;
            bool boolArg = false;
            string trackUri = "", trackUid = "";
            switch (kind)
            {
                case ConnectCmd.SeekTo:
                    if (inner.TryGetProperty("position", out var pos)) seekMs = LongLoose(pos);
                    else if (inner.TryGetProperty("value", out var val)) seekMs = LongLoose(val);
                    break;
                case ConnectCmd.SetShufflingContext:
                case ConnectCmd.SetRepeatingContext:
                case ConnectCmd.SetRepeatingTrack:
                    if (inner.TryGetProperty("value", out var bv) && bv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        boolArg = bv.GetBoolean();
                    break;
                case ConnectCmd.SkipNext:
                    // next_track (and, rarely, skip_next) carries command.track {uri,uid} — the row being jumped to (F7).
                    if (inner.TryGetProperty("track", out var trk) && trk.ValueKind == JsonValueKind.Object)
                    {
                        if (trk.TryGetProperty("uri", out var tu)) trackUri = tu.GetString() ?? "";
                        if (trk.TryGetProperty("uid", out var td)) trackUid = td.GetString() ?? "";
                    }
                    break;
            }

            cmd = new ConnectCommand(kind, endpoint, req.RequestId, messageId, sender, seekMs, boolArg, req.Command, trackUri, trackUid);
            return kind != ConnectCmd.Unknown;
        }
        catch { return false; }
    }

    static ConnectCmd Map(string e) => e switch
    {
        "play" => ConnectCmd.Play,
        "pause" => ConnectCmd.Pause,
        "resume" => ConnectCmd.Resume,
        "seek_to" => ConnectCmd.SeekTo,
        "skip_next" => ConnectCmd.SkipNext,
        "next_track" => ConnectCmd.SkipNext,   // official desktop sends next_track (+ the target row) for a queue-row jump (F7)
        "skip_prev" => ConnectCmd.SkipPrev,
        "set_shuffling_context" => ConnectCmd.SetShufflingContext,
        "set_repeating_context" => ConnectCmd.SetRepeatingContext,
        "set_repeating_track" => ConnectCmd.SetRepeatingTrack,
        "transfer" => ConnectCmd.Transfer,
        "add_to_queue" => ConnectCmd.AddToQueue,
        "set_queue" => ConnectCmd.SetQueue,
        "update_context" => ConnectCmd.UpdateContext,
        "set_options" => ConnectCmd.SetOptions,
        _ => ConnectCmd.Unknown,
    };

    // The wire sends numbers sometimes as JSON strings ("12345"); accept both.
    static int IntLoose(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : (int.TryParse(e.GetString(), out var v) ? v : 0);
    static long LongLoose(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : (long.TryParse(e.GetString(), out var v) ? v : 0);
}

// Subscribes the REQUEST firehose, parses each command, dispatches to the controller, and ACKS within the 10 s SLA.
// Policy: ack-on-dispatch (locked decision) — the dealer ack means "received + dispatched", so we ALWAYS reply promptly;
// a dispatch failure surfaces later via the cluster/PutState, never by withholding the ack (that would mark us unhealthy).
public sealed class ConnectCommandRouter : IDisposable
{
    readonly ITransport _transport;
    readonly Action<ConnectCommand> _dispatch;
    readonly WaveeLogger _log;
    readonly IDisposable _sub;

    public ConnectCommandRouter(ITransport transport, Action<ConnectCommand> dispatch, WaveeLogger log = default)
    {
        _transport = transport;
        _dispatch = dispatch;
        _log = log;
        _sub = transport.Requests("hm://connect-state/v1/").Subscribe(Observers.From<WireRequest>(OnRequest));
    }

    void OnRequest(WireRequest req)
    {
        RequestResult result;
        if (ConnectCommand.TryParse(req, out var cmd))
        {
            try { _dispatch(cmd); result = RequestResult.Success; _log.Info("connect command: " + cmd.Kind); }
            catch (Exception ex) { result = RequestResult.ContextPlayerError; _log.Info("connect command dispatch error: " + ex.Message); }
        }
        else
        {
            result = RequestResult.DeviceDoesNotSupportCommand;
            _log.Info("connect command unsupported: " + req.MessageIdent);
        }
        // Always reply (sync ack-on-dispatch trivially meets the 10 s SLA; a parse/dispatch failure still acks a code).
        _ = _transport.Reply(req.RequestId, result);
    }

    public void Dispose() => _sub.Dispose();
}
