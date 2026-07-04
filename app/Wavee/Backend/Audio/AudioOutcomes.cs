namespace Wavee.Backend.Audio;

public enum AudioKeyFailureReason
{
    None = 0,
    NeverProvisioned,
    ProvisioningUnavailable,
    ArchUnsupported,
    SecurityBlocked,
    License403,
    RotationDrift,
    EmulationFault,
    Network,
    KeyPending,
    Prebuffering,
    ApPermanent,
    ApTimeout,
    Restricted,
}

public enum ProvisioningOutcome
{
    Ready,
    NeverAttempted,
    ManifestUnavailable,
    PackDownloadFailed,
    HashMismatch,
    SignatureInvalid,
    ArchUnsupported,
}

public readonly record struct AudioKeyResult(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason, string? Detail = null)
{
    public bool Ok => Reason == AudioKeyFailureReason.None && !Key.IsEmpty;
    public static AudioKeyResult Success(ReadOnlyMemory<byte> key) => new(key, AudioKeyFailureReason.None);
    public static AudioKeyResult Fail(AudioKeyFailureReason reason, string? detail = null) => new(default, reason, detail);
}

public readonly record struct PlayPlayDeriveResult(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason, string? Detail = null)
{
    public bool Ok => Reason == AudioKeyFailureReason.None && Key.Length == 16;
}

/// <summary>A surfaced local-playback failure: the typed reason + a technical detail (for the log) + the user-facing
/// message (for the toast). Lets the UX show a friendly line while the log/diagnostics keep the exact cause.</summary>
public readonly record struct PlaybackErrorInfo(AudioKeyFailureReason Reason, string UserMessage, string? Detail);

/// <summary>Thrown when a track can't be resolved/played locally. Carries the typed reason so the controller can surface
/// a specific, user-facing message instead of failing silently.</summary>
public sealed class AudioPlaybackException : Exception
{
    public AudioKeyFailureReason Reason { get; }
    public AudioPlaybackException(AudioKeyFailureReason reason, string? detail = null)
        : base(detail ?? reason.ToString()) => Reason = reason;
}

/// <summary>Maps typed audio failures to short, honest, user-facing messages (no jargon, no silent nulls).</summary>
public static class AudioFailureText
{
    public static string ToUserMessage(this AudioKeyFailureReason r) => r switch
    {
        AudioKeyFailureReason.NeverProvisioned or AudioKeyFailureReason.ProvisioningUnavailable
            => "Playback support is still setting up — try again in a moment.",
        AudioKeyFailureReason.License403 => "Spotify declined this track for your account.",
        AudioKeyFailureReason.RotationDrift => "Playback needs an update to keep working.",
        AudioKeyFailureReason.ArchUnsupported => "This track can't be played on this device.",
        AudioKeyFailureReason.SecurityBlocked or AudioKeyFailureReason.EmulationFault
            => "Couldn't start secure playback for this track.",
        AudioKeyFailureReason.Network => "Network problem — couldn't reach Spotify.",
        AudioKeyFailureReason.ApTimeout => "Timed out reaching Spotify — try again.",
        AudioKeyFailureReason.ApPermanent or AudioKeyFailureReason.Restricted => "This track isn't available to play right now.",
        _ => "Couldn't play this track.",
    };

    public static string ToUserMessage(this ProvisioningOutcome o) => o switch
    {
        ProvisioningOutcome.ManifestUnavailable => "Couldn't reach the playback-support service.",
        ProvisioningOutcome.PackDownloadFailed => "Couldn't download playback support.",
        ProvisioningOutcome.HashMismatch or ProvisioningOutcome.SignatureInvalid => "Playback support failed verification.",
        ProvisioningOutcome.ArchUnsupported => "Playback support isn't available for this device.",
        _ => "Playback support isn't ready.",
    };
}
