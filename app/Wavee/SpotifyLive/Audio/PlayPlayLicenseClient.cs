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

    public PlayPlayLicenseClient(ITransport transport, Action<string>? log = null)
    {
        _transport = transport;
        _log = log;
    }

    public async Task<(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason)> FetchObfuscatedKeyAsync(
        string fileIdHex, PlayPlayConfig config, CancellationToken ct)
    {
        var body = BuildRequestBody(config);
        var route = "/playplay/v1/key/" + fileIdHex;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var resp = await _transport.Request(Channel.Spclient, route, body, ct,
                method: "POST", headers: SpotifyHeaders.PlayPlayKey()).ConfigureAwait(false);
            if (resp.Ok)
            {
                var parsed = PlayPlayLicenseResponse.Parser.ParseFrom(resp.Body);
                if (parsed.ObfuscatedKey.Length == 16)
                    return (parsed.ObfuscatedKey.ToByteArray(), AudioKeyFailureReason.None);
                return (default, AudioKeyFailureReason.RotationDrift);
            }
            if (resp.Status == 403)
            {
                _log?.Invoke($"PlayPlay license 403 for {fileIdHex} (attempt {attempt + 1})");
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
                else return (default, AudioKeyFailureReason.License403);
            }
            else if (resp.Status >= 500 && attempt < 2)
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            else
                return (default, AudioKeyFailureReason.Network);
        }
        return (default, AudioKeyFailureReason.Network);
    }

    // Desktop sends Unix seconds (10-digit), not milliseconds — wire matches a ~30-byte body.
    internal static byte[] BuildRequestBody(PlayPlayConfig config, long? timestampSeconds = null)
    {
        var req = new PlayPlayLicenseRequest
        {
            Version = SpotifyRuntimeIdentityHost.Current.PlayPlayRequestVersion,
            Token = ByteString.CopyFrom(SpotifyRuntimeIdentity.DefaultPlayPlayToken),
            Interactivity = Interactivity.Interactive,
            ContentType = ContentType.AudioTrack,
            Timestamp = timestampSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        return req.ToByteArray();
    }
}
