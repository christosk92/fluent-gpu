using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Wavee.Backend;

public enum ConnectCmd
{
    Unknown, Play, Pause, Resume, SeekTo, SkipNext, SkipPrev,
    SetShufflingContext, SetRepeatingContext, SetRepeatingTrack,
    Transfer, AddToQueue, SetQueue, UpdateContext, SetOptions,
}

public enum ConnectCommandOutcome { Applied, NoOp, Superseded, Failed }

/// <summary>
/// Parsed Dealer REQUEST envelope. The complete payload remains available for typed command-specific parsing; the
/// frequently needed routing and correlation values are extracted once at the boundary.
/// </summary>
public readonly record struct ConnectCommand(
    ConnectCmd Kind, string Endpoint, string Key, int MessageId, string SenderDeviceId,
    long SeekToMs, bool BoolArg, byte[] Payload,
    string TrackUri = "", string TrackUid = "",
    string SessionId = "", string CommandId = "")
{
    public static bool TryParse(in WireRequest req, out ConnectCommand cmd)
    {
        cmd = default;
        try
        {
            var parts = req.MessageIdent.Split('/');
            if (parts.Length < 5 || req.Command is null || req.Command.Length == 0) return false;

            using var doc = JsonDocument.Parse(req.Command);
            var root = doc.RootElement;
            int messageId = root.TryGetProperty("message_id", out var mid) ? IntLoose(mid) : 0;
            string sender = root.TryGetProperty("sent_by_device_id", out var sd) ? sd.GetString() ?? "" : "";

            JsonElement inner = root;
            string urlEndpoint = parts[^1];
            string endpoint;
            if (urlEndpoint == "command" && parts.Length >= 6 && parts[^2] == "player")
            {
                if (!root.TryGetProperty("command", out inner) || !inner.TryGetProperty("endpoint", out var ep)) return false;
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
                    if (inner.TryGetProperty("value", out var bv) &&
                        bv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        boolArg = bv.GetBoolean();
                    break;
                case ConnectCmd.SkipNext:
                    if (inner.TryGetProperty("track", out var trk) && trk.ValueKind == JsonValueKind.Object)
                    {
                        if (trk.TryGetProperty("uri", out var tu)) trackUri = tu.GetString() ?? "";
                        if (trk.TryGetProperty("uid", out var td)) trackUid = td.GetString() ?? "";
                    }
                    break;
            }

            string sessionId = "";
            if (inner.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                sessionId = sid.GetString() ?? "";
            else if (inner.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
                     && options.TryGetProperty("session_id", out sid) && sid.ValueKind == JsonValueKind.String)
                sessionId = sid.GetString() ?? "";

            string commandId = "";
            if (inner.TryGetProperty("logging_params", out var logging) && logging.ValueKind == JsonValueKind.Object
                && logging.TryGetProperty("command_id", out var cid) && cid.ValueKind == JsonValueKind.String)
                commandId = cid.GetString() ?? "";

            cmd = new ConnectCommand(kind, endpoint, req.RequestId, messageId, sender, seekMs, boolArg, req.Command,
                trackUri, trackUid, sessionId, commandId);
            return kind != ConnectCmd.Unknown;
        }
        catch { return false; }
    }

    static ConnectCmd Map(string endpoint) => endpoint switch
    {
        "play" => ConnectCmd.Play,
        "pause" => ConnectCmd.Pause,
        "resume" => ConnectCmd.Resume,
        "seek_to" => ConnectCmd.SeekTo,
        "skip_next" or "next_track" => ConnectCmd.SkipNext,
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

    static int IntLoose(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetInt32()
        : int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    static long LongLoose(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetInt64()
        : long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>
/// Owns the inbound Connect command queue. A Dealer ACK confirms validation and queue admission, not audible completion.
/// The single worker preserves command order and observes every handler task.
/// </summary>
public sealed class ConnectCommandRouter : IDisposable
{
    const int DedupeCapacity = 1024;
    static readonly long DedupeTicks = (long)(Stopwatch.Frequency * TimeSpan.FromMinutes(10).TotalSeconds);

    readonly ITransport _transport;
    readonly Func<ConnectCommand, CancellationToken, Task<ConnectCommandOutcome>> _dispatch;
    readonly Func<int, CancellationToken, Task<ConnectCommandOutcome>>? _volumeDispatch;
    readonly WaveeLogger _log;
    readonly IDisposable _requestSub;
    readonly IDisposable _volumeSub;
    readonly System.Threading.Channels.Channel<ConnectWork> _queue;
    readonly CancellationTokenSource _cts = new();
    readonly Task _worker;
    readonly object _dedupeGate = new();
    readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    readonly Queue<(string Key, long At)> _seenOrder = new();

    public ConnectCommandRouter(ITransport transport, Action<ConnectCommand> dispatch, WaveeLogger log = default)
        : this(transport, (command, _) =>
        {
            dispatch(command);
            return Task.FromResult(ConnectCommandOutcome.Applied);
        }, null, log)
    {
    }

    public ConnectCommandRouter(
        ITransport transport,
        Func<ConnectCommand, CancellationToken, Task<ConnectCommandOutcome>> dispatch,
        Func<int, CancellationToken, Task<ConnectCommandOutcome>>? volumeDispatch = null,
        WaveeLogger log = default,
        int capacity = 256)
    {
        _transport = transport;
        _dispatch = dispatch;
        _volumeDispatch = volumeDispatch;
        _log = log;
        _queue = System.Threading.Channels.Channel.CreateBounded<ConnectWork>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(WorkerAsync);
        _requestSub = transport.Requests("hm://connect-state/v1/")
            .Subscribe(Observers.From<WireRequest>(OnRequest));
        _volumeSub = transport.Events("hm://connect-state/v1/connect/volume")
            .Subscribe(Observers.From<WireEvent>(OnVolume));
    }

    void OnRequest(WireRequest request)
    {
        RequestResult result;
        if (!ConnectCommand.TryParse(request, out var command))
        {
            result = RequestResult.DeviceDoesNotSupportCommand;
            _log.Info("connect command unsupported: " + request.MessageIdent);
        }
        else
        {
            string dedupeKey = DedupeKey(command);
            if (dedupeKey.Length > 0 && IsDuplicate(dedupeKey))
            {
                result = RequestResult.Success;
                _log.Event(WaveeLogLevel.Debug, "connect.command.duplicate", "exact Connect command replay ignored",
                    fields:
                    [
                        WaveeLogField.Of("endpoint", command.Endpoint),
                        WaveeLogField.Of("messageId", command.MessageId),
                        WaveeLogField.Of("sender", Fingerprint(command.SenderDeviceId)),
                    ]);
            }
            else if (_queue.Writer.TryWrite(ConnectWork.ForCommand(command, Stopwatch.GetTimestamp())))
            {
                if (dedupeKey.Length > 0) Remember(dedupeKey);
                result = RequestResult.Success;
                _log.Event(WaveeLogLevel.Info, "connect.command.received", "Connect command accepted",
                    fields:
                    [
                        WaveeLogField.Of("endpoint", command.Endpoint),
                        WaveeLogField.Of("messageId", command.MessageId),
                        WaveeLogField.Of("sender", Fingerprint(command.SenderDeviceId)),
                        WaveeLogField.Of("commandId", Fingerprint(command.CommandId)),
                        WaveeLogField.Of("session", Fingerprint(command.SessionId)),
                        WaveeLogField.Of("payloadBytes", command.Payload?.Length ?? 0),
                    ]);
            }
            else
            {
                result = RequestResult.ContextPlayerError;
                _log.Warn($"connect command queue full: endpoint={command.Endpoint}");
            }
        }
        _ = _transport.Reply(request.RequestId, result);
    }

    void OnVolume(WireEvent wire)
    {
        if (_volumeDispatch is null) return;
        if (!TryParseSetVolume(wire.Payload, out int volume))
        {
            _log.Warn("connect volume MESSAGE had an invalid SetVolumeCommand body");
            return;
        }
        if (!_queue.Writer.TryWrite(ConnectWork.ForVolume(volume, Stopwatch.GetTimestamp())))
            _log.Warn("connect command queue full: inbound volume dropped");
    }

    async Task WorkerAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var outcome = ConnectCommandOutcome.NoOp;
                Exception? error = null;
                try
                {
                    outcome = work.IsVolume
                        ? (_volumeDispatch is null ? ConnectCommandOutcome.NoOp
                            : await _volumeDispatch(work.Volume, _cts.Token).ConfigureAwait(false))
                        : await _dispatch(work.Command, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception ex) { outcome = ConnectCommandOutcome.Failed; error = ex; }

                long durationMs = (long)Stopwatch.GetElapsedTime(work.ReceivedAt).TotalMilliseconds;
                if (work.IsVolume)
                {
                    _log.Event(error is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning,
                        "connect.volume.completed", "inbound Connect volume completed", elapsedMs: durationMs, ex: error,
                        fields:
                        [
                            WaveeLogField.Of("volume", work.Volume),
                            WaveeLogField.Of("outcome", outcome.ToString()),
                        ]);
                }
                else
                {
                    _log.Event(error is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning,
                        "connect.command.completed", "Connect command completed", elapsedMs: durationMs, ex: error,
                        fields:
                        [
                            WaveeLogField.Of("endpoint", work.Command.Endpoint),
                            WaveeLogField.Of("messageId", work.Command.MessageId),
                            WaveeLogField.Of("sender", Fingerprint(work.Command.SenderDeviceId)),
                            WaveeLogField.Of("commandId", Fingerprint(work.Command.CommandId)),
                            WaveeLogField.Of("outcome", outcome.ToString()),
                        ]);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Warn("connect command worker fault: " + ex.Message, ex); }
    }

    bool IsDuplicate(string key)
    {
        lock (_dedupeGate)
        {
            PruneSeen(Stopwatch.GetTimestamp());
            return _seen.ContainsKey(key);
        }
    }

    void Remember(string key)
    {
        lock (_dedupeGate)
        {
            long now = Stopwatch.GetTimestamp();
            PruneSeen(now);
            if (_seen.ContainsKey(key)) return;
            _seen[key] = now;
            _seenOrder.Enqueue((key, now));
            while (_seenOrder.Count > DedupeCapacity)
            {
                var old = _seenOrder.Dequeue();
                if (_seen.TryGetValue(old.Key, out long at) && at == old.At) _seen.Remove(old.Key);
            }
        }
    }

    void PruneSeen(long now)
    {
        while (_seenOrder.TryPeek(out var first) && now - first.At > DedupeTicks)
        {
            _seenOrder.Dequeue();
            if (_seen.TryGetValue(first.Key, out long at) && at == first.At) _seen.Remove(first.Key);
        }
    }

    static string DedupeKey(in ConnectCommand command) =>
        command.MessageId == 0 || string.IsNullOrEmpty(command.SenderDeviceId)
            ? ""
            : command.SenderDeviceId + "\n" + command.MessageId.ToString(CultureInfo.InvariantCulture);

    static string Fingerprint(string value) =>
        string.IsNullOrEmpty(value) ? "-" : WaveeLogRedaction.HashLike(value);

    internal static bool TryParseSetVolume(ReadOnlySpan<byte> payload, out int volume)
    {
        volume = 0;
        int offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadVarint(payload, ref offset, out ulong key)) return false;
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            if (field == 1 && wire == 0)
            {
                if (!TryReadVarint(payload, ref offset, out ulong raw) || raw > int.MaxValue) return false;
                volume = Math.Clamp((int)raw, 0, 65535);
                return true;
            }
            if (!SkipField(payload, ref offset, wire)) return false;
        }
        return false;
    }

    static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && offset < bytes.Length; shift += 7)
        {
            byte b = bytes[offset++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    static bool SkipField(ReadOnlySpan<byte> bytes, ref int offset, int wire)
    {
        switch (wire)
        {
            case 0: return TryReadVarint(bytes, ref offset, out _);
            case 1: offset += 8; return offset <= bytes.Length;
            case 2:
                if (!TryReadVarint(bytes, ref offset, out ulong length) || length > int.MaxValue) return false;
                offset += (int)length;
                return offset <= bytes.Length;
            case 5: offset += 4; return offset <= bytes.Length;
            default: return false;
        }
    }

    public void Dispose()
    {
        _requestSub.Dispose();
        _volumeSub.Dispose();
        _queue.Writer.TryComplete();
        try
        {
            if (!_worker.Wait(TimeSpan.FromSeconds(2)))
            {
                _cts.Cancel();
                _worker.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch { }
        _cts.Dispose();
    }

    readonly record struct ConnectWork(ConnectCommand Command, int Volume, bool IsVolume, long ReceivedAt)
    {
        public static ConnectWork ForCommand(ConnectCommand command, long at) => new(command, 0, false, at);
        public static ConnectWork ForVolume(int volume, long at) => new(default, volume, true, at);
    }
}
