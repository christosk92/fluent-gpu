using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Wavee.Backend.Audio;

/// <summary>Manifest schema for the audio runtime support pack registry. Version pins live here — not compiled into the client.</summary>
public sealed class RuntimeManifest
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("packs")] public RuntimeManifestPack[] Packs { get; set; } = [];
}

public sealed class RuntimeManifestPack
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("urls")] public string[]? Urls { get; set; }
    [JsonPropertyName("compression")] public string? Compression { get; set; }
    [JsonPropertyName("sha256_hex")] public string Sha256Hex { get; set; } = "";
    [JsonPropertyName("playplay_token_hex")] public string PlayPlayTokenHex { get; set; } = "";
    [JsonPropertyName("spotify_version")] public string SpotifyVersion { get; set; } = "";
    [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("request_version")] public int RequestVersion { get; set; } = 5;
    [JsonPropertyName("arch")] public string Arch { get; set; } = "X64";
    [JsonPropertyName("analysis_base_hex")] public string AnalysisBaseHex { get; set; } = "";
    [JsonPropertyName("vm_runtime_init_va_hex")] public string VmRuntimeInitVaHex { get; set; } = "";
    [JsonPropertyName("vm_object_transform_va_hex")] public string VmObjectTransformVaHex { get; set; } = "";
    [JsonPropertyName("runtime_context_va_hex")] public string RuntimeContextVaHex { get; set; } = "";
    [JsonPropertyName("fill_random_bytes_va_hex")] public string FillRandomBytesVaHex { get; set; } = "";
    [JsonPropertyName("vm_init_value_hex")] public string VmInitValueHex { get; set; } = "";
    [JsonPropertyName("trigger_rip_va_hex")] public string TriggerRipVaHex { get; set; } = "";
    [JsonPropertyName("trigger_rip_reg_offset")] public int TriggerRipRegOffset { get; set; }
    [JsonPropertyName("extraction_strategy")] public string ExtractionStrategy { get; set; } = "trigger_rip";
    [JsonPropertyName("vm_object_size")] public int VmObjectSize { get; set; } = 144;
    [JsonPropertyName("rt_context_size")] public int RtContextSize { get; set; } = 16;
    [JsonPropertyName("derived_key_size")] public int DerivedKeySize { get; set; } = 24;
    [JsonPropertyName("obfuscated_key_size")] public int ObfuscatedKeySize { get; set; } = 16;
    [JsonPropertyName("init_value_size")] public int InitValueSize { get; set; } = 16;
    [JsonPropertyName("content_id_size")] public int ContentIdSize { get; set; } = 16;
    [JsonPropertyName("key_size")] public int KeySize { get; set; } = 16;
}

public static class RuntimeManifestPackExtensions
{
    public static PlayPlayConfig ToConfig(this RuntimeManifestPack p)
    {
        var arch = p.Arch.Equals("Arm64", StringComparison.OrdinalIgnoreCase) ? Architecture.Arm64 : Architecture.X64;
        var extraction = ParseExtraction(p);
        return new PlayPlayConfig(
            Version: p.SpotifyVersion,
            Arch: arch,
            Sha256: HexToBytes(p.Sha256Hex),
            PlayPlayToken: HexToBytes(p.PlayPlayTokenHex),
            VmInitValue: HexToBytes(p.VmInitValueHex),
            AnalysisBase: ParseHexUlong(p.AnalysisBaseHex),
            VmRuntimeInitVa: ParseHexUlong(p.VmRuntimeInitVaHex),
            VmObjectTransformVa: ParseHexUlong(p.VmObjectTransformVaHex),
            RuntimeContextVa: ParseHexUlong(p.RuntimeContextVaHex),
            FillRandomBytesVa: ParseHexUlong(p.FillRandomBytesVaHex),
            AesKey: extraction,
            VmObjectSize: p.VmObjectSize,
            RtContextSize: p.RtContextSize,
            DerivedKeySize: p.DerivedKeySize,
            ObfuscatedKeySize: p.ObfuscatedKeySize,
            InitValueSize: p.InitValueSize,
            ContentIdSize: p.ContentIdSize,
            KeySize: p.KeySize);
    }

    static AesKeyExtraction ParseExtraction(RuntimeManifestPack p) => p.ExtractionStrategy switch
    {
        "buffer_slice" => new AesKeyExtraction.OutputBufferSlice(0, p.KeySize),
        "post_process" => new AesKeyExtraction.PostProcessCall(ParseHexUlong(p.TriggerRipVaHex), 0),
        _ => new AesKeyExtraction.TriggerRipBreakpoint(ParseHexUlong(p.TriggerRipVaHex), p.TriggerRipRegOffset),
    };

    public static SpotifyRuntimeIdentity ToIdentity(this RuntimeManifestPack p)
    {
        var semver = p.SpotifyVersion;
        var appVer = string.IsNullOrEmpty(p.AppVersion) ? DeriveAppVersion(semver) : p.AppVersion;
        return new SpotifyRuntimeIdentity(appVer, semver, p.RequestVersion);
    }

    static string DeriveAppVersion(string semver)
    {
        // 1.2.88.483 → 128800483 (desktop app-version header convention)
        var parts = semver.Split('.');
        if (parts.Length < 4) return semver.Replace(".", "");
        return parts[0] + parts[1] + parts[2] + parts[3];
    }

    static byte[] HexToBytes(string hex) => Convert.FromHexString(hex);

    static ulong ParseHexUlong(string hex)
    {
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan();
        return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

[JsonSerializable(typeof(RuntimeManifest))]
[JsonSerializable(typeof(RuntimeManifestPack))]
public sealed partial class RuntimeManifestJsonContext : JsonSerializerContext;
