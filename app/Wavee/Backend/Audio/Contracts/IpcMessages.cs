using System.Text.Json.Serialization;

namespace Wavee.Backend.Audio;

/// <summary>Length-prefixed JSON IPC envelope: [4B BE length][UTF-8 JSON]. Source-gen STJ for AOT.</summary>
public sealed class IpcEnvelope
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("payload")] public object? Payload { get; init; }
}

public static class IpcMessageTypes
{
    public const string DerivePlayPlayKey = "derive_playplay_key";
    public const string LoadFastStart = "load_fast_start";
    public const string SupplyBody = "supply_body";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Seek = "seek";
    public const string SetVolume = "set_volume";
    public const string Shutdown = "shutdown";
    public const string Ping = "ping";
    public const string Ready = "ready";
    public const string Pong = "pong";
    public const string CommandResult = "command_result";
    public const string StateUpdate = "state_update";
    public const string TrackFinished = "track_finished";
}

/// <summary>UI → host: derive a plaintext AES key from an obfuscated key. Config is already parsed UI-side.</summary>
public sealed class DerivePlayPlayKeyCommand
{
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("obfuscatedKeyHex")] public required string ObfuscatedKeyHex { get; init; }
    [JsonPropertyName("contentIdHex")] public required string ContentIdHex { get; init; }
    [JsonPropertyName("spotifyDllPath")] public required string SpotifyDllPath { get; init; }
    [JsonPropertyName("config")] public required PlayPlayConfig Config { get; init; }
}

public sealed class DerivePlayPlayKeyResult
{
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("aesKeyHex")] public string? AesKeyHex { get; init; }
    [JsonPropertyName("reason")] public AudioKeyFailureReason? Reason { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

/// <summary>UI → host: start decode/output from the clear head bytes before the key exists.</summary>
public sealed class LoadFastStartCommand
{
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("trackUri")] public required string TrackUri { get; init; }
    [JsonPropertyName("fileIdHex")] public required string FileIdHex { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }
    [JsonPropertyName("normalizationGainDb")] public float NormalizationGainDb { get; init; }
    [JsonPropertyName("headBytesBase64")] public required string HeadBytesBase64 { get; init; }
}

/// <summary>UI → host: supply key + CDN endpoints when parallel resolution completes.</summary>
public sealed class SupplyBodyCommand
{
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("fileIdHex")] public required string FileIdHex { get; init; }
    [JsonPropertyName("aesKeyHex")] public required string AesKeyHex { get; init; }
    [JsonPropertyName("cdnUrls")] public required string[] CdnUrls { get; init; }
    [JsonPropertyName("headBoundary")] public int HeadBoundary { get; init; }
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; init; }
}

public sealed class CommandResultMessage
{
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("reason")] public AudioKeyFailureReason? Reason { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed class HostStateUpdate
{
    [JsonPropertyName("isPlaying")] public bool IsPlaying { get; init; }
    [JsonPropertyName("isBuffering")] public bool IsBuffering { get; init; }
    [JsonPropertyName("isPrebuffering")] public bool IsPrebuffering { get; init; }
    [JsonPropertyName("positionMs")] public long PositionMs { get; init; }
}

public sealed class TrackFinishedMessage
{
    [JsonPropertyName("trackUri")] public string? TrackUri { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

// Concrete control payloads — anonymous types can't serialize under source-gen STJ / AOT.
public sealed class ReadyMessage { [JsonPropertyName("ok")] public bool Ok { get; init; } }
public sealed class EmptyPayload { }
public sealed class SeekCommand { [JsonPropertyName("positionMs")] public long PositionMs { get; init; } }
public sealed class VolumeCommand { [JsonPropertyName("volume")] public double Volume { get; init; } }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(DerivePlayPlayKeyCommand))]
[JsonSerializable(typeof(DerivePlayPlayKeyResult))]
[JsonSerializable(typeof(LoadFastStartCommand))]
[JsonSerializable(typeof(SupplyBodyCommand))]
[JsonSerializable(typeof(CommandResultMessage))]
[JsonSerializable(typeof(HostStateUpdate))]
[JsonSerializable(typeof(TrackFinishedMessage))]
[JsonSerializable(typeof(ReadyMessage))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(SeekCommand))]
[JsonSerializable(typeof(VolumeCommand))]
[JsonSerializable(typeof(PlayPlayConfig))]
[JsonSerializable(typeof(AesKeyExtraction.TriggerRipBreakpoint))]
[JsonSerializable(typeof(AesKeyExtraction.OutputBufferSlice))]
[JsonSerializable(typeof(AesKeyExtraction.PostProcessCall))]
public sealed partial class AudioIpcJsonContext : JsonSerializerContext;
