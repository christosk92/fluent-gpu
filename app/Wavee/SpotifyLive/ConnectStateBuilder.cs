using System;
using Google.Protobuf;
using Wavee.Protocol.Media;
using Wavee.Protocol.Player;
// The UI defines an internal `enum PlayerState` in the parent Wavee namespace (PlayerBar.cs), which shadows the proto type
// here — alias the proto PlayerState explicitly so this stays unambiguous.
using ProtoPlayerState = Wavee.Protocol.Player.PlayerState;

namespace Wavee.SpotifyLive;

// Builds the connect-state PutStateRequest (DeviceInfo + Capabilities + PrivateDeviceInfo + PlayerState) → protobuf bytes.
// The DeviceInfo/Capabilities/supported-types are desktop-parity / anti-fraud values ported VERBATIM from the reference
// (decision #11): "spotify"/"PC laptop", license=premium (Recently-Played eligibility), NeedsFullPlayerState=true, 64 volume
// steps, the 14 supported_types, AudioQuality.VeryHigh. Changing them silently breaks Recently Played / can get us throttled.
// Proto-building lives in SpotifyLive (the wire boundary); the proto-free ConnectService orchestrates the PUT.
public sealed class ConnectStateBuilder
{
    const string KeymasterClientId = "65b708073fc0480ea92a077233ca87bd";
    public const int MaxVolume = 65535;
    public const int DefaultVolumeSteps = 64;

    static readonly string[] DesktopSupportedTypes =
    {
        "audio/ad", "audio/episode", "audio/episode+track", "audio/interruption", "audio/local", "audio/media",
        "audio/podcast-chapter", "audio/track", "audio/user-highlight", "video/ad", "video/episode",
        "video/podcast-chapter", "video/track", "video/user-highlight",
    };

    readonly string _deviceId;
    readonly string _deviceName;
    readonly string _clientId;
    int _volume;

    public ConnectStateBuilder(string deviceId, string deviceName, string? clientId = null, int volume = MaxVolume / 2)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _clientId = clientId ?? KeymasterClientId;
        _volume = Math.Clamp(volume, 0, MaxVolume);
    }

    public string DeviceId => _deviceId;
    public void SetVolume(int spotifyVolume) => _volume = Math.Clamp(spotifyVolume, 0, MaxVolume);

    public DeviceInfo BuildDeviceInfo()
    {
        var info = new DeviceInfo
        {
            CanPlay = true,
            Volume = (uint)_volume,
            Name = _deviceName,
            DeviceId = _deviceId,
            DeviceType = DeviceType.Computer,
            DeviceSoftwareVersion = SpotifyClientIdentity.DeviceSoftwareVersion,
            ClientId = _clientId,
            SpircVersion = SpotifyClientIdentity.SpircVersion,
            Capabilities = BuildCapabilities(),
            Brand = "spotify",
            Model = "PC laptop",
            License = "premium",          // Recently-Played / play-count eligibility — load-bearing
            IsPrivateSession = false,
        };
        info.MetadataMap["debug_level"] = "1";
        info.MetadataMap["tier1_port"] = "0";
        return info;
    }

    static Capabilities BuildCapabilities(int volumeSteps = DefaultVolumeSteps)
    {
        var c = new Capabilities
        {
            CanBePlayer = true,
            GaiaEqConnectId = true,
            SupportsLogout = true,
            IsObservable = true,
            CommandAcks = true,
            SupportsRename = false,
            SupportsPlaylistV2 = true,
            IsControllable = true,
            SupportsExternalEpisodes = true,
            SupportsSetBackendMetadata = true,
            SupportsTransferCommand = true,
            SupportsCommandRequest = true,
            VolumeSteps = volumeSteps,
            SupportsGzipPushes = true,
            NeedsFullPlayerState = true,   // pull full cluster snapshots, not deltas — the projection reconciles from full
            SupportsSetOptionsCommand = true,
            SupportsHifi = new CapabilitySupportDetails { FullySupported = true, UserEligible = true, DeviceSupported = true },
            SupportsDj = true,
            SupportedAudioQuality = AudioQuality.VeryHigh,   // 320 kbps OGG (claiming HIFI would lie about FLAC)
        };
        foreach (var t in DesktopSupportedTypes) c.SupportedTypes.Add(t);
        return c;
    }

    /// <summary>Serialize a PutStateRequest. <paramref name="nowMs"/> overridable for deterministic tests.</summary>
    public byte[] BuildPutState(uint messageId, bool isActive = false,
        PutStateReason reason = PutStateReason.NewConnection, ProtoPlayerState? playerState = null, long? nowMs = null)
    {
        long ts = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var req = new PutStateRequest
        {
            MemberType = MemberType.ConnectState,
            Device = new Device
            {
                DeviceInfo = BuildDeviceInfo(),
                PlayerState = playerState ?? new ProtoPlayerState { Timestamp = ts },
                PrivateDeviceInfo = new PrivateDeviceInfo { Platform = SpotifyClientIdentity.GetPrivateDevicePlatform() },
            },
            IsActive = isActive,
            PutStateReason = reason,
            MessageId = messageId,
            ClientSideTimestamp = (ulong)ts,
        };
        return req.ToByteArray();
    }
}
