using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Runtime;

/// <summary>Result of verifying a candidate PlayPlay runtime directory.</summary>
public sealed record PlayPlayRuntimeVerifyResult(
    bool Ok,
    ProvisioningOutcome Outcome,
    string? Detail,
    string? RuntimeDir,
    string? DllPath,
    PlayPlayRuntimeManifest? Manifest,
    SignatureTrust SignatureTrust = SignatureTrust.Unknown,
    bool NeedsUntrustedConfirmation = false,
    DigitalSignatureInfo? SignatureInfo = null,
    bool TrustedByPinnedFingerprint = false)
{
    public static PlayPlayRuntimeVerifyResult Fail(ProvisioningOutcome outcome, string detail) =>
        new(false, outcome, detail, null, null, null);
}

/// <summary>SHA-256 + PE arch hard gates; Authenticode advisory (user must confirm untrusted DLLs).</summary>
public static class PlayPlayRuntimeVerifier
{
    public static readonly string[] SupportedAlgorithms = ["129300667-native-cdn-v2"];

    public static PlayPlayRuntimeVerifyResult VerifyDirectory(string runtimeDir, bool allowUntrustedSignature, Action<string>? log = null)
    {
        if (!PlayPlayRuntimePaths.IsRuntimeDirectory(runtimeDir))
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, "missing Spotify.dll or playplay-runtime.json");

        var dllPath = Path.Combine(runtimeDir, PlayPlayRuntimePaths.DllFileName);
        var manifestPath = Path.Combine(runtimeDir, PlayPlayRuntimePaths.ManifestFileName);
        return Verify(dllPath, manifestPath, allowUntrustedSignature, log);
    }

    public static PlayPlayRuntimeVerifyResult Verify(string dllPath, string manifestPath, bool allowUntrustedSignature, Action<string>? log = null)
    {
        log?.Invoke($"verify: dll={dllPath}");
        if (!File.Exists(dllPath))
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, "Spotify.dll not found");

        byte[] dllBytes;
        try { dllBytes = File.ReadAllBytes(dllPath); }
        catch (Exception ex) { return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, ex.Message); }

        // Belt-and-suspenders: a real Spotify.dll is tens of MB. Reject an absurdly small file BEFORE trusting any hash
        // or pinned fingerprint — this catches the 70-byte MZ stub (the WriteMinimalPe test fixture) that once got pinned
        // and produced LoadLibraryEx Win32 193 (ERROR_BAD_EXE_FORMAT). A stub can no longer match a real pin, but a future
        // mis-pin must still never bind.
        const int MinRealDllBytes = 10 * 1024 * 1024;
        if (dllBytes.Length < MinRealDllBytes)
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable,
                $"Spotify.dll is only {dllBytes.Length:N0} bytes — not a real DLL (placeholder/stub)");

        byte[] hash;
        try { hash = SHA256.HashData(dllBytes); }
        catch (Exception ex) { return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, ex.Message); }

        if (!File.Exists(manifestPath))
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, "playplay-runtime.json not found");

        PlayPlayRuntimeManifest manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize(json, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest)
                ?? throw new InvalidOperationException("manifest parse returned null");
        }
        catch (Exception ex)
        {
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, "manifest parse failed: " + ex.Message);
        }

        var manifestErr = manifest.Validate();
        if (manifestErr is not null)
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, manifestErr);

        var expectedHash = Convert.FromHexString(manifest.DllSha256);
        if (!hash.AsSpan().SequenceEqual(expectedHash))
        {
            log?.Invoke($"verify: hash mismatch expected={manifest.DllSha256} actual={Convert.ToHexStringLower(hash)}");
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.HashMismatch,
                $"SHA-256 mismatch (expected {manifest.DllSha256[..12]}…, got {Convert.ToHexStringLower(hash)[..12]}…)");
        }
        log?.Invoke("verify: SHA-256 OK");

        if (!PeAndSignature.TryGetPeArchitecture(dllPath, out var peArch, out var peErr))
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable, peErr ?? "PE read failed");

        var processArch = RuntimeInformation.ProcessArchitecture;
        if (peArch != processArch)
        {
            log?.Invoke($"verify: arch mismatch dll={peArch} process={processArch}");
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.ArchUnsupported,
                $"DLL is {peArch} but Wavee is {processArch}");
        }
        log?.Invoke($"verify: PE arch OK ({peArch})");

        if (!SupportedAlgorithms.Contains(manifest.AlgorithmVersion, StringComparer.Ordinal))
            return PlayPlayRuntimeVerifyResult.Fail(ProvisioningOutcome.RuntimeUnavailable,
                $"unsupported algorithm '{manifest.AlgorithmVersion}'");

        // A hash match against our PINNED known-pack table is a STRONGER guarantee than Authenticode: we shipped the
        // exact SHA-256 of this binary, so its publisher signature — which Windows often can't even parse for the
        // Spotify.dll form (0x800B0003 TRUST_E_SUBJECT_FORM_UNKNOWN) — is irrelevant. Trust the fingerprint and skip the
        // untrusted gate, so the automatic provisioning path (allowUntrusted=false) binds it too.
        var hashHex = Convert.ToHexStringLower(hash);
        bool pinnedTrusted = PlayPlayKnownPacks.TryMatch(hashHex, out _);

        var sigTrust = SignatureTrust.Unknown;
        DigitalSignatureInfo? sigInfo = null;
        if (OperatingSystem.IsWindows())
        {
            var (trust, reason) = PeAndSignature.TryVerifyAuthenticode(dllPath);
            sigTrust = pinnedTrusted ? SignatureTrust.Trusted : trust;
            sigInfo = PeAndSignature.TryReadDigitalSignatureInfo(dllPath, trust, reason);
            log?.Invoke($"verify: Authenticode {trust} ({reason})");
            if (sigInfo is { } s)
                log?.Invoke($"verify: signed by {s.Subject} issuer={s.Issuer}");
            if (pinnedTrusted)
                log?.Invoke(sigInfo is { Trust: SignatureTrust.Trusted }
                    ? "verify: trusted by Spotify signature and pinned fingerprint"
                    : "verify: trusted by pinned fingerprint (known pack)");
        }
        else
        {
            sigTrust = pinnedTrusted ? SignatureTrust.Trusted : SignatureTrust.UnsupportedPlatform;
            log?.Invoke("verify: Authenticode skipped (non-Windows)");
            if (pinnedTrusted) log?.Invoke("verify: trusted by pinned fingerprint (known pack)");
        }

        if (!pinnedTrusted && sigTrust == SignatureTrust.Untrusted && !allowUntrustedSignature)
        {
            return new PlayPlayRuntimeVerifyResult(
                false, ProvisioningOutcome.SignatureInvalid,
                "DLL signature is not trusted — explicit confirmation required",
                Path.GetDirectoryName(dllPath), dllPath, manifest, sigTrust, NeedsUntrustedConfirmation: true,
                SignatureInfo: sigInfo);
        }

        return new PlayPlayRuntimeVerifyResult(
            true, ProvisioningOutcome.Ready, null,
            Path.GetDirectoryName(dllPath), dllPath, manifest, sigTrust,
            SignatureInfo: sigInfo,
            TrustedByPinnedFingerprint: pinnedTrusted);
    }

    public static RuntimeAsset ToAsset(PlayPlayRuntimeVerifyResult result)
    {
        if (!result.Ok || result.Manifest is null || result.DllPath is null)
            throw new InvalidOperationException("cannot build asset from failed verification");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(result.DllPath));
        return new RuntimeAsset(result.DllPath, result.Manifest.ToPlayPlayConfig(hash), result.Manifest.PackId);
    }
}
