using System.Linq;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Runtime;

/// <summary>The definitive in-code pin table for known Spotify builds — the versioning system's source of truth (NOT a
/// JSON file). Each entry is the exact reverse-engineering for one (build, arch) binary, keyed by the SHA-256 of that
/// binary. Lets the user supply just the bare <c>Spotify.dll</c>: Wavee recognizes a supported build by fingerprint and
/// synthesizes <c>playplay-runtime.json</c> itself. VA offsets are valid ONLY for the exact binary, which is why the key
/// is the content hash — never a version string. Pins per arch differ (image base is shared, everything else is not).</summary>
static class PlayPlayKnownPacks
{
    // The PlayPlay token is PER-ARCH (proven by decoding the live request bodies field 2): ARM64 and x64 send different
    // 16-byte tokens embedded in their own Spotify.dll. Never share it.
    const string TokenHexArm64 = "025614bf92a6c95e922e466523da4f96";
    const string TokenHexX64 = "029e867610e2eb965450e1c7169b7da8";
    const string VmInitZero = "00000000000000000000000000000000";

    /// <summary>One entry per exact (build, arch) binary. Newest first.</summary>
    public static readonly PlayPlayRuntimeManifest[] Packs =
    [
        // ── Spotify 1.2.93.667 (129300667) — ARM64 ──────────────────────────────────────────────────
        // dll: app/audio-runtime/packs/129300667-arm64.bin (~38 MB)
        new(
            SchemaVersion: 1,
            PackId: "129300667-arm64",
            SpotifyVersion: "1.2.93.667",
            ClientVersion: "1.2.93.667.g7b5cc0ce",
            AppVersion: "129300667",
            PlayPlayRequestVersion: 5,
            Arch: "Arm64",
            AlgorithmVersion: "129300667-native-cdn-v2",
            DllSha256: "cf95a21911e932cb3a53ae5732c5f8f416b885d79e8cf793107d79e329e9b387",
            PlayPlayTokenHex: TokenHexArm64,
            Config: new PlayPlayRuntimeManifestConfig(
                AnalysisBaseHex: "180000000",
                VmRuntimeInitVaHex: "1804B6DD8",
                VmObjectTransformVaHex: "1804B9038",
                RuntimeContextVaHex: "18192D398",
                RuntimeContextSecondaryVaHex: "0",
                InitVtableLabsHex: [],
                TransformFourthArgTemplateVaHex: "1822ECD38",
                TransformFourthArgBuildVaHex: "181579FF0",
                FillRandomBytesVaHex: "180434B04",
                VmInitValueHex: VmInitZero,
                AesKey: new AesKeyExtraction.OutputBufferSlice(0, 16),
                VmObjectSize: 144,
                RtContextSize: 16,
                DerivedKeySize: 24,
                ObfuscatedKeySize: 16,
                InitValueSize: 16,
                ContentIdSize: 16,
                KeySize: 16)),

        // ── Spotify 1.2.93.667 (129300667) — x64 ────────────────────────────────────────────────────
        // dll: app/audio-runtime/packs/129300667-x64.bin (~42 MB)
        new(
            SchemaVersion: 1,
            PackId: "129300667-x64",
            SpotifyVersion: "1.2.93.667",
            ClientVersion: "1.2.93.667.g7b5cc0ce",
            AppVersion: "129300667",
            PlayPlayRequestVersion: 5,
            Arch: "X64",
            AlgorithmVersion: "129300667-native-cdn-v2",
            DllSha256: "cef6b4ef24cc7895a3c2323ba3d177480b913cad55ef5b6e9448c81697bab4db",
            PlayPlayTokenHex: TokenHexX64,
            Config: new PlayPlayRuntimeManifestConfig(
                AnalysisBaseHex: "180000000",
                VmRuntimeInitVaHex: "1804B35F8",
                VmObjectTransformVaHex: "1804B5538",
                RuntimeContextVaHex: "1818C4848",
                RuntimeContextSecondaryVaHex: "1818B3730",
                InitVtableLabsHex:
                [
                    "1818B4598", "1818B46B0", "1818B47C8", "1818B48E8",
                    "1818B4A00", "1818C4608", "1818B4C38", "1818C4728",
                ],
                TransformFourthArgTemplateVaHex: "182674ED8",
                TransformFourthArgBuildVaHex: "1815EA8CC",
                FillRandomBytesVaHex: "180434B04",
                VmInitValueHex: VmInitZero,
                AesKey: new AesKeyExtraction.OutputBufferSlice(0, 16),
                VmObjectSize: 144,
                RtContextSize: 16,
                DerivedKeySize: 32,
                ObfuscatedKeySize: 16,
                InitValueSize: 16,
                ContentIdSize: 16,
                KeySize: 16)),
    ];

    /// <summary>Find the known pack for an exact DLL by its hex SHA-256 (case-insensitive).</summary>
    public static bool TryMatch(string dllSha256Hex, out PlayPlayRuntimeManifest manifest)
    {
        foreach (var p in Packs)
        {
            if (string.Equals(p.DllSha256, dllSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                manifest = p;
                return true;
            }
        }
        manifest = null!;
        return false;
    }

    /// <summary>The newest known pack for a given architecture, or null if none — used as the best-effort pin set when a
    /// user supplies an unrecognized DLL of a supported arch and chooses "try anyway".</summary>
    public static PlayPlayRuntimeManifest? NewestForArch(System.Runtime.InteropServices.Architecture arch)
    {
        foreach (var p in Packs)
            if (PlayPlayRuntimeManifest.TryParseArch(p.Arch, out var a) && a == arch)
                return p;
        return null;
    }

    /// <summary>Human list of recognized builds for the setup UI — e.g. "Spotify 1.2.93.667 (Arm64)".</summary>
    public static string Summary =>
        string.Join(", ", Packs.Select(p => $"Spotify {p.SpotifyVersion} ({p.Arch})"));
}
