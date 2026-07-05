using System.Runtime.InteropServices;

namespace Wavee.Backend.Audio;

/// <summary>Advisory Authenticode trust for a PlayPlay runtime DLL (v1: surfaced, not a hard gate).</summary>
public enum SignatureTrust
{
    Unknown = 0,
    Trusted,
    Untrusted,
    UnsupportedPlatform,
}

public readonly record struct DigitalSignatureInfo(
    string FilePath,
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    SignatureTrust Trust,
    string Reason);

/// <summary>UI-facing projection of runtime provisioning — derives copy from <see cref="ProvisioningOutcome"/> via <see cref="AudioFailureText"/>.</summary>
public readonly record struct PlaybackRuntimeStatus(
    ProvisioningOutcome Outcome,
    string? PackId = null,
    string? SpotifyVersion = null,
    Architecture? Arch = null,
    string? RuntimePath = null,
    SignatureTrust SignatureTrust = SignatureTrust.Unknown,
    bool NeedsUntrustedConfirmation = false,
    DigitalSignatureInfo? SignatureInfo = null,
    bool TrustedByPinnedFingerprint = false)
{
    public static PlaybackRuntimeStatus NotApplicable { get; } = new(ProvisioningOutcome.NeverAttempted);

    public bool IsReady => Outcome == ProvisioningOutcome.Ready;
    public bool ShowBanner => Outcome is not (ProvisioningOutcome.Ready or ProvisioningOutcome.NeverAttempted);
}
