using System.Text.Json.Serialization;
using Wavee.Core;

namespace Wavee.Backend.Audio;

/// <summary>Length-prefixed JSON IPC envelope: [4B BE length][UTF-8 JSON]. id == 0 is host-initiated.</summary>
public sealed class IpcEnvelope
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("payload")] public object? Payload { get; init; }
}

public static class AudioIpcContract
{
    public const int Version = 6;   // v6: explicit recovery-aware host snapshots + typed playback failures
}

public static class IpcMessageTypes
{
    public const string Hello = "hello";
    public const string Ready = "ready";
    public const string PlayTrack = "play_track";
    public const string PrepareNext = "prepare_next";
    public const string SupplyNextBody = "supply_next_body";
    public const string CancelPrepared = "cancel_prepared";
    public const string DerivePlayPlayKey = "derive_playplay_key";
    public const string LoadFastStart = "load_fast_start";
    public const string SupplyBody = "supply_body";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Seek = "seek";
    public const string SetVolume = "set_volume";
    public const string SetOutputDevice = "set_output_device";
    public const string SetMute = "set_mute";
    public const string DeviceEvent = "device_event";
    public const string SessionVolume = "session_volume";
    public const string SetEqualizer = "set_equalizer";
    public const string SetCrossfade = "set_crossfade";
    public const string Shutdown = "shutdown";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string CommandResult = "command_result";
    public const string StateUpdate = "state";
    public const string Position = "position";
    public const string TrackFinished = "ended";
    public const string BufferDepth = "buffer_depth";
    public const string Underrun = "underrun";
    public const string SeekCommitted = "seek_committed";
    public const string SeekCancelled = "seek_cancelled";
    public const string CrossfadeStarted = "crossfade_started";
    public const string CrossfadeMissed = "crossfade_missed";
    public const string CrossfadeCompleted = "crossfade_completed";
    public const string EqualizerApplied = "eq_applied";
    public const string Diagnostic = "diagnostic";
    public const string Error = "error";
    public const string AuthLeaseRequest = "auth_lease_request";
    public const string AuthLeaseReply = "auth_lease_reply";
}

public sealed class RuntimeAssetDescriptor
{
    [JsonPropertyName("packPath")] public required string PackPath { get; init; }
    [JsonPropertyName("packId")] public required string PackId { get; init; }
    [JsonPropertyName("config")] public required PlayPlayConfig Config { get; init; }

    public RuntimeAsset ToAsset() => new(PackPath, Config, PackId);
    public static RuntimeAssetDescriptor FromAsset(RuntimeAsset asset) => new()
    {
        PackPath = asset.PackPath,
        PackId = asset.PackId,
        Config = asset.Config,
    };
}

public sealed class EqualizerSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("preampDb")] public float PreampDb { get; init; }
    [JsonPropertyName("gainsDb")] public float[] GainsDb { get; init; } = new float[10];

    public static EqualizerSettings Flat { get; } = new();
}

public sealed class CrossfadeSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("durationMs")] public int DurationMs { get; init; }

    public static CrossfadeSettings Off { get; } = new();
}

public sealed class HelloCommand
{
    [JsonPropertyName("contractVersion")] public int ContractVersion { get; init; }
    [JsonPropertyName("launchToken")] public required string LaunchToken { get; init; }
    [JsonPropertyName("parentPid")] public int ParentPid { get; init; }
    [JsonPropertyName("pack")] public RuntimeAssetDescriptor? Pack { get; init; }
    [JsonPropertyName("equalizer")] public EqualizerSettings Equalizer { get; init; } = EqualizerSettings.Flat;
    [JsonPropertyName("crossfade")] public CrossfadeSettings Crossfade { get; init; } = CrossfadeSettings.Off;
    [JsonPropertyName("volume")] public double Volume { get; init; } = 1.0;
    [JsonPropertyName("outputDeviceId")] public string? OutputDeviceId { get; init; }
}

public sealed class ReadyMessage
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("contractVersion")] public int ContractVersion { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("pid")] public int Pid { get; init; }
}

public sealed class EmptyPayload
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
}

public sealed class GenerationCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
}

public sealed class SeekCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("positionMs")] public long PositionMs { get; init; }
}

public sealed class VolumeCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("volume")] public double Volume { get; init; }
}

/// <summary>UI -> host: choose the WASAPI output endpoint. Generation is informational only — device routing is global
/// (the HandleVolume precedent); applied unconditionally.</summary>
public sealed class SetOutputDeviceCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
}

/// <summary>UI -> host: mute/unmute the Windows session (Phase B).</summary>
public sealed class MuteCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("muted")] public bool Muted { get; init; }
}

/// <summary>host -> UI: a device-topology notice (loss / fallback / auto-return / output-failed). Global — no generation gate.</summary>
public sealed class DeviceEventMessage
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("kind")] public int Kind { get; init; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; init; }
    [JsonPropertyName("wasExplicit")] public bool WasExplicit { get; init; }
}

/// <summary>host -> UI: an EXTERNAL Windows session-volume change (SndVol / another app) to reflect in the UI (Phase B).</summary>
public sealed class SessionVolumeMessage
{
    [JsonPropertyName("volume01")] public double Volume01 { get; init; }
    [JsonPropertyName("muted")] public bool Muted { get; init; }
}

public sealed class SetEqualizerCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("settings")] public EqualizerSettings Settings { get; init; } = EqualizerSettings.Flat;
}

public sealed class SetCrossfadeCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("settings")] public CrossfadeSettings Settings { get; init; } = CrossfadeSettings.Off;
}

/// <summary>UI -> host: derive a plaintext AES key from an obfuscated key. Config is already parsed UI-side.</summary>
public sealed class DerivePlayPlayKeyCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("obfuscatedKeyHex")] public required string ObfuscatedKeyHex { get; init; }
    [JsonPropertyName("contentIdHex")] public required string ContentIdHex { get; init; }
    [JsonPropertyName("spotifyDllPath")] public required string SpotifyDllPath { get; init; }
    [JsonPropertyName("packId")] public string? PackId { get; init; }
    [JsonPropertyName("config")] public required PlayPlayConfig Config { get; init; }
    [JsonPropertyName("playPlayAuxBase64")] public string? PlayPlayAuxBase64 { get; init; }
    [JsonPropertyName("licenseRawBase64")] public string? LicenseRawBase64 { get; init; }
    [JsonPropertyName("licenseRequestBase64")] public string? LicenseRequestBase64 { get; init; }
}

public sealed class DerivePlayPlayKeyResult
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("aesKeyHex")] public string? AesKeyHex { get; init; }
    [JsonPropertyName("nativeCdnSeedBase64")] public string? NativeCdnSeedBase64 { get; init; }
    [JsonPropertyName("derivedSlabBase64")] public string? DerivedSlabBase64 { get; init; }
    [JsonPropertyName("reason")] public AudioKeyFailureReason? Reason { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

/// <summary>UI -> host: start decode/output from clear head bytes before the key exists.</summary>
public sealed class LoadFastStartCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("trackUri")] public required string TrackUri { get; init; }
    [JsonPropertyName("fileIdHex")] public required string FileIdHex { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }
    [JsonPropertyName("normalizationGainDb")] public float NormalizationGainDb { get; init; }
    [JsonPropertyName("headBytesBase64")] public required string HeadBytesBase64 { get; init; }
}

/// <summary>UI -> host: supply key + CDN endpoints when parallel resolution completes.</summary>
public sealed class SupplyBodyCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("trackUri")] public required string TrackUri { get; init; }
    [JsonPropertyName("fileIdHex")] public required string FileIdHex { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }
    [JsonPropertyName("normalizationGainDb")] public float NormalizationGainDb { get; init; }
    [JsonPropertyName("aesKeyHex")] public required string AesKeyHex { get; init; }
    [JsonPropertyName("nativeCdnSeedBase64")] public string? NativeCdnSeedBase64 { get; init; }
    [JsonPropertyName("cdnUrls")] public required string[] CdnUrls { get; init; }
    [JsonPropertyName("headBoundary")] public int HeadBoundary { get; init; }
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; init; }
    [JsonPropertyName("sourceKind")] public int SourceKind { get; init; }   // 0 = SpotifyEncrypted (default), 1 = ExternalPlain
}

/// <summary>UI -> host: create an independently decoded next stream without changing the active generation.</summary>
public sealed class PrepareNextCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
    [JsonPropertyName("trackUri")] public required string TrackUri { get; init; }
    [JsonPropertyName("fileIdHex")] public required string FileIdHex { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }
    [JsonPropertyName("normalizationGainDb")] public float NormalizationGainDb { get; init; }
    [JsonPropertyName("headBytesBase64")] public required string HeadBytesBase64 { get; init; }
    [JsonPropertyName("allowOverlap")] public bool AllowOverlap { get; init; }
}

/// <summary>UI -> host: attach the resolved body to a tokenized prepared stream.</summary>
public sealed class SupplyNextBodyCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
    [JsonPropertyName("body")] public required SupplyBodyCommand Body { get; init; }
}

public sealed class CancelPreparedCommand
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
}

/// <summary>Host -> UI: the prepared token started, completed, or missed its natural boundary.</summary>
public sealed class PreparedTransitionMessage
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("kind")] public int Kind { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
    [JsonPropertyName("trackUri")] public required string TrackUri { get; init; }
    [JsonPropertyName("positionMs")] public long PositionMs { get; init; }
    [JsonPropertyName("effectiveFadeMs")] public int EffectiveFadeMs { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed class CommandResultMessage
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("reason")] public AudioKeyFailureReason? Reason { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed class HostStateUpdate
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("kind")] public int Kind { get; init; }
    [JsonPropertyName("isPlaying")] public bool IsPlaying { get; init; }
    [JsonPropertyName("isBuffering")] public bool IsBuffering { get; init; }
    [JsonPropertyName("isPrebuffering")] public bool IsPrebuffering { get; init; }
    [JsonPropertyName("recoveryKind")] public PlaybackRecoveryKind RecoveryKind { get; init; }
    [JsonPropertyName("positionMs")] public long PositionMs { get; init; }
}

public sealed class PlaybackFailureMessage
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("positionMs")] public long PositionMs { get; init; }
    [JsonPropertyName("reason")] public AudioKeyFailureReason Reason { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed class TrackFinishedMessage
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("trackUri")] public string? TrackUri { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed class DiagnosticMessage
{
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed class PingMessage
{
    [JsonPropertyName("sentUnixMs")] public long SentUnixMs { get; init; }
}

public sealed class PongMessage
{
    [JsonPropertyName("sentUnixMs")] public long SentUnixMs { get; init; }
    [JsonPropertyName("hostUnixMs")] public long HostUnixMs { get; init; }
}

public sealed class AuthLeaseRequest
{
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("minTtlMs")] public int MinTtlMs { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("generation")] public long Generation { get; init; }
    [JsonPropertyName("forceRefresh")] public bool ForceRefresh { get; init; }
}

public sealed class AuthLeaseReply
{
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; init; }
    [JsonPropertyName("accessToken")] public string? AccessToken { get; init; }
    [JsonPropertyName("clientToken")] public string? ClientToken { get; init; }
    [JsonPropertyName("expiresAtUnixMs")] public long ExpiresAtUnixMs { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(HelloCommand))]
[JsonSerializable(typeof(ReadyMessage))]
[JsonSerializable(typeof(RuntimeAssetDescriptor))]
[JsonSerializable(typeof(EqualizerSettings))]
[JsonSerializable(typeof(CrossfadeSettings))]
[JsonSerializable(typeof(DerivePlayPlayKeyCommand))]
[JsonSerializable(typeof(DerivePlayPlayKeyResult))]
[JsonSerializable(typeof(LoadFastStartCommand))]
[JsonSerializable(typeof(SupplyBodyCommand))]
[JsonSerializable(typeof(PrepareNextCommand))]
[JsonSerializable(typeof(SupplyNextBodyCommand))]
[JsonSerializable(typeof(CancelPreparedCommand))]
[JsonSerializable(typeof(PreparedTransitionMessage))]
[JsonSerializable(typeof(CommandResultMessage))]
[JsonSerializable(typeof(HostStateUpdate))]
[JsonSerializable(typeof(PlaybackFailureMessage))]
[JsonSerializable(typeof(TrackFinishedMessage))]
[JsonSerializable(typeof(DiagnosticMessage))]
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(PongMessage))]
[JsonSerializable(typeof(AuthLeaseRequest))]
[JsonSerializable(typeof(AuthLeaseReply))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(GenerationCommand))]
[JsonSerializable(typeof(SeekCommand))]
[JsonSerializable(typeof(VolumeCommand))]
[JsonSerializable(typeof(SetEqualizerCommand))]
[JsonSerializable(typeof(SetCrossfadeCommand))]
[JsonSerializable(typeof(SetOutputDeviceCommand))]
[JsonSerializable(typeof(MuteCommand))]
[JsonSerializable(typeof(DeviceEventMessage))]
[JsonSerializable(typeof(SessionVolumeMessage))]
[JsonSerializable(typeof(PlayPlayConfig))]
[JsonSerializable(typeof(AesKeyExtraction.TriggerRipBreakpoint))]
[JsonSerializable(typeof(AesKeyExtraction.OutputBufferSlice))]
[JsonSerializable(typeof(AesKeyExtraction.PostProcessCall))]
public sealed partial class AudioIpcJsonContext : JsonSerializerContext;
