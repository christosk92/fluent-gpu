using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Protocol.Playplay;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Step A: fetch the obfuscated PlayPlay key over HTTPS (POST /playplay/v1/key/{fileId}).</summary>
public sealed class PlayPlayLicenseClient : ILicenseClient
{
    readonly ITransport _transport;
    readonly Action<string>? _log;
    readonly IWaveeLog? _structuredLog;

    public PlayPlayLicenseClient(ITransport transport, Action<string>? log = null, IWaveeLog? structuredLog = null)
    {
        _transport = transport;
        _log = log;
        _structuredLog = structuredLog;
    }

    public async Task<(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason)> FetchObfuscatedKeyAsync(
        string fileIdHex, PlayPlayConfig config, CancellationToken ct)
    {
        var result = await FetchLicenseAsync(fileIdHex, config, ct).ConfigureAwait(false);
        return (result.Key, result.Reason);
    }

    public async Task<PlayPlayLicenseResult> FetchLicenseAsync(
        string fileIdHex, PlayPlayConfig config, CancellationToken ct)
    {
        var body = BuildRequestBody(config);
        var route = "/playplay/v1/key/" + fileIdHex;
        var identity = SpotifyRuntimeIdentityHost.Current;
        Event(WaveeLogLevel.Info, "license.request.start", "PlayPlay license request starting",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)),
            WaveeLogField.Of("route", "/playplay/v1/key/{file}"),
            WaveeLogField.Of("requestBytes", body.Length),
            WaveeLogField.Of("requestVersion", identity.PlayPlayRequestVersion),
            WaveeLogField.Of("appVersion", identity.AppVersion),
            WaveeLogField.Of("runtimeVersion", config.Version));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Event(WaveeLogLevel.Debug, "license.request.attempt", "PlayPlay license request attempt",
                WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)),
                WaveeLogField.Of("attempt", attempt + 1));
            var resp = await _transport.Request(Channel.Spclient, route, body, ct,
                method: "POST", headers: SpotifyHeaders.PlayPlayKey()).ConfigureAwait(false);
            if (resp.Ok)
            {
                var parsed = ParseResponse(resp.Body);
                if (parsed.Key.Length == 16)
                {
                    _log?.Invoke($"obf {fileIdHex}: key={parsed.Key.Length}B redacted aux={parsed.Auxiliary.Length}B token=redacted body={resp.Body.Length}B fields={parsed.FieldSummary}");
                    Event(WaveeLogLevel.Info, "license.request.ok", "PlayPlay license returned an obfuscated key",
                        WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)),
                        WaveeLogField.Of("attempt", attempt + 1),
                        WaveeLogField.Of("keyBytes", parsed.Key.Length),
                        WaveeLogField.Of("auxBytes", parsed.Auxiliary.Length),
                        WaveeLogField.Of("bodyBytes", resp.Body.Length),
                        WaveeLogField.Of("fields", parsed.FieldSummary));
                    return parsed with { Reason = AudioKeyFailureReason.None, RawBody = resp.Body, RequestBody = body };
                }
                _log?.Invoke($"PlayPlay response missing 16-byte obfuscated key: body={resp.Body.Length}B fields={parsed.FieldSummary}");
                Event(WaveeLogLevel.Error, "license.request.bad_body", "PlayPlay license response did not contain a 16-byte key",
                    WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)),
                    WaveeLogField.Of("bodyBytes", resp.Body.Length),
                    WaveeLogField.Of("fields", parsed.FieldSummary));
                return parsed with { Reason = AudioKeyFailureReason.RotationDrift };
            }
            if (resp.Status == 403)
            {
                _log?.Invoke($"PlayPlay license 403 for {fileIdHex} (attempt {attempt + 1})");
                Event(WaveeLogLevel.Warning, "license.request.403", "PlayPlay license returned 403",
                    WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)),
                    WaveeLogField.Of("attempt", attempt + 1));
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
                else return PlayPlayLicenseResult.Fail(AudioKeyFailureReason.License403);
            }
            else if (resp.Status >= 500 && attempt < 2)
            {
                Event(WaveeLogLevel.Warning, "license.request.retry", "PlayPlay license returned retryable HTTP status",
                    WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)),
                    WaveeLogField.Of("attempt", attempt + 1),
                    WaveeLogField.Of("status", resp.Status));
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            else
            {
                Event(WaveeLogLevel.Error, "license.request.failed", "PlayPlay license request failed",
                    WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)),
                    WaveeLogField.Of("attempt", attempt + 1),
                    WaveeLogField.Of("status", resp.Status));
                return PlayPlayLicenseResult.Fail(AudioKeyFailureReason.Network);
            }
        }
        Event(WaveeLogLevel.Error, "license.request.exhausted", "PlayPlay license request exhausted retries",
            WaveeLogField.Of("file", WaveeLogRedaction.HashLike(fileIdHex)));
        return PlayPlayLicenseResult.Fail(AudioKeyFailureReason.Network);
    }

    // Desktop sends Unix seconds (10-digit), not milliseconds — wire matches a ~30-byte body.
    internal static byte[] BuildRequestBody(PlayPlayConfig config, long? timestampSeconds = null)
    {
        var req = new PlayPlayLicenseRequest
        {
            Version = SpotifyRuntimeIdentityHost.Current.PlayPlayRequestVersion,
            Token = ByteString.CopyFrom(ResolvePlayPlayToken(config)),
            Interactivity = Interactivity.Interactive,
            ContentType = ContentType.AudioTrack,
            Timestamp = timestampSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        return req.ToByteArray();
    }

    internal static byte[] ResolvePlayPlayToken(PlayPlayConfig config)
    {
        var tokenHex = Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_TOKEN_HEX");
        if (string.IsNullOrWhiteSpace(tokenHex))
            return config.PlayPlayToken;

        return Convert.FromHexString(tokenHex.Trim());
    }

    static PlayPlayLicenseResult ParseResponse(ReadOnlySpan<byte> body)
    {
        byte[] key = [];
        byte[] aux = [];
        var fields = new List<string>();

        int pos = 0;
        while (pos < body.Length)
        {
            if (!TryReadVarint(body, ref pos, out var tag)) break;
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    if (!TryReadVarint(body, ref pos, out var v)) return new PlayPlayLicenseResult(key, aux, AudioKeyFailureReason.RotationDrift, string.Join(", ", fields));
                    fields.Add($"#{field}:varint");
                    break;
                case 1:
                    if (pos + 8 > body.Length) return new PlayPlayLicenseResult(key, aux, AudioKeyFailureReason.RotationDrift, string.Join(", ", fields));
                    fields.Add($"#{field}:fixed64[8]");
                    pos += 8;
                    break;
                case 2:
                    if (!TryReadVarint(body, ref pos, out var len64)) return new PlayPlayLicenseResult(key, aux, AudioKeyFailureReason.RotationDrift, string.Join(", ", fields));
                    var len = checked((int)len64);
                    if (len < 0 || pos + len > body.Length) return new PlayPlayLicenseResult(key, aux, AudioKeyFailureReason.RotationDrift, string.Join(", ", fields));
                    var value = body.Slice(pos, len).ToArray();
                    fields.Add($"#{field}:bytes[{len}]");
                    if (field == 1 && len == 16) key = value;
                    else if (len == 4 && aux.Length == 0) aux = value;
                    pos += len;
                    break;
                case 5:
                    if (pos + 4 > body.Length) return new PlayPlayLicenseResult(key, aux, AudioKeyFailureReason.RotationDrift, string.Join(", ", fields));
                    fields.Add($"#{field}:fixed32[4]");
                    pos += 4;
                    break;
                default:
                    return new PlayPlayLicenseResult(key, aux, AudioKeyFailureReason.RotationDrift, string.Join(", ", fields));
            }
        }

        return new PlayPlayLicenseResult(key, aux, AudioKeyFailureReason.None, string.Join(", ", fields), body.ToArray());
    }

    static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < bytes.Length && shift < 64)
        {
            var b = bytes[pos++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    void Event(WaveeLogLevel level, string eventId, string message, params WaveeLogField[] fields) =>
        _structuredLog?.Event(level, "audio", eventId, message, fields: fields);
}

public readonly record struct PlayPlayLicenseResult(
    ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Auxiliary,
    AudioKeyFailureReason Reason,
    string FieldSummary,
    ReadOnlyMemory<byte> RawBody = default,
    ReadOnlyMemory<byte> RequestBody = default)
{
    public static PlayPlayLicenseResult Fail(AudioKeyFailureReason reason) => new(default, default, reason, "");
}
