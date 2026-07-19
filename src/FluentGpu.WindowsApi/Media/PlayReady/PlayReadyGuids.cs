using System;
using System.Runtime.Versioning;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The load-bearing GUIDs, runtime-class names, and sentinel HRESULTs of the native PlayReady playback path. Every IID
/// here was copied from the Windows SDK WinRT headers (<c>windows.media.protection.playready.h</c> /
/// <c>windows.media.protection.h</c> in <c>...\Windows Kits\10\Include\10.0.26100.0\winrt</c>) and is annotated with its
/// source; the two <c>// VERIFY</c>-flagged constants are DERIVED (a computed WinRT parameterized IID and the current
/// public Axinom test endpoint) rather than copied, so they are the first suspects if a live run misbehaves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Protection-system GUIDs.</b> <see cref="PlayReadyProtectionSystemId"/> (<c>{F4637010-…}</c>) is PlayReady's
/// protection-system id; <see cref="PlayReadyContainerGuid"/> (<c>{9A04F079-…}</c>) is the DASH/CENC PlayReady system id
/// (the <c>urn:uuid:9a04f079-…</c> in an MPD's <c>&lt;ContentProtection&gt;</c>). Both are stable, public, well-known
/// PlayReady constants and match WaveeMusic's proven recipe (see <c>docs/plans/video-drm-layer-design.md §3.2</c>).
/// </para>
/// <para>
/// <b>Cold-COM interface IIDs.</b> The four PlayReady service-request interfaces are NOT projected by
/// <c>TerraFX.Interop.WinRT</c>, so they are hand-authored here (see <see cref="PlayReadyServiceRequestInterop"/>). Each
/// IID was read verbatim from the SDK header's <c>MIDL_INTERFACE("…")</c> declaration.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240.0")]
internal static class PlayReadyGuids
{
    // ── Protection-system GUIDs (public, well-known PlayReady constants) ─────────────────────────────────────────────

    /// <summary>PlayReady protection-system id (<c>windows.media.protection.playready.h</c>; WaveeMusic recipe).</summary>
    public const string PlayReadyProtectionSystemId = "{F4637010-03C3-42CD-B932-B48ADF3A6A54}";

    /// <summary>DASH/CENC PlayReady container/system GUID (<c>urn:uuid:9a04f079-…</c>).</summary>
    public const string PlayReadyContainerGuid = "{9A04F079-9840-4286-AB92-E65BE0885F95}";

    // ── MediaProtectionManager property keys (exact strings — WaveeMusic proven recipe) ─────────────────────────────

    public const string KeyMediaProtectionSystemIdMapping = "Windows.Media.Protection.MediaProtectionSystemIdMapping";
    public const string KeyMediaProtectionSystemId = "Windows.Media.Protection.MediaProtectionSystemId";
    public const string KeyMediaProtectionContainerGuid = "Windows.Media.Protection.MediaProtectionContainerGuid";
    public const string KeyUseSoftwareProtectionLayer = "Windows.Media.Protection.UseSoftwareProtectionLayer";

    /// <summary>The activatable class the system-id mapping points at (referenced by runtime-class STRING only).</summary>
    public const string PlayReadyWinRTTrustedInput = "Windows.Media.Protection.PlayReady.PlayReadyWinRTTrustedInput";

    // ── Runtime class names (RoActivateInstance / RoGetActivationFactory) ───────────────────────────────────────────

    public const string RuntimeClass_MediaProtectionManager = "Windows.Media.Protection.MediaProtectionManager";
    public const string RuntimeClass_MediaPlayer = "Windows.Media.Playback.MediaPlayer";
    public const string RuntimeClass_PropertySet = "Windows.Foundation.Collections.PropertySet";
    public const string RuntimeClass_PropertyValue = "Windows.Foundation.PropertyValue";
    public const string RuntimeClass_Uri = "Windows.Foundation.Uri";
    public const string RuntimeClass_AdaptiveMediaSource = "Windows.Media.Streaming.Adaptive.AdaptiveMediaSource";
    public const string RuntimeClass_MediaSource = "Windows.Media.Core.MediaSource";
    public const string RuntimeClass_MediaPlaybackItem = "Windows.Media.Playback.MediaPlaybackItem";

    /// <summary>The default-constructible individualization (provisioning) request runtime class — activated directly
    /// (no <c>MediaProtectionManager</c>/<c>MediaPlayer</c>) to provision PlayReady out-of-band before playback. Verbatim
    /// from <c>windows.media.protection.playready.h</c> line ~5920
    /// (<c>RuntimeClass_Windows_Media_Protection_PlayReady_PlayReadyIndividualizationServiceRequest</c>).</summary>
    public const string RuntimeClass_PlayReadyIndividualizationServiceRequest =
        "Windows.Media.Protection.PlayReady.PlayReadyIndividualizationServiceRequest";

    // ── Hand-authored PlayReady cold-COM interface IIDs (copied from windows.media.protection.playready.h) ───────────

    /// <summary>IID of <c>IPlayReadyServiceRequest</c> (SDK header line ~5254, <c>MIDL_INTERFACE(...)</c>).</summary>
    public static readonly Guid IID_IPlayReadyServiceRequest = new("8bad2836-a703-45a6-a180-76f3565aa725");

    /// <summary>IID of <c>IPlayReadyLicenseAcquisitionServiceRequest</c> (SDK header line ~4693).</summary>
    public static readonly Guid IID_IPlayReadyLicenseAcquisitionServiceRequest = new("5d85ff45-3e9f-4f48-93e1-9530c8d58c3e");

    /// <summary>IID of <c>IPlayReadyIndividualizationServiceRequest</c> (SDK header line ~4551).</summary>
    public static readonly Guid IID_IPlayReadyIndividualizationServiceRequest = new("21f5a86b-008c-4611-ab2f-aaa6c69f0e24");

    /// <summary>IID of <c>IPlayReadySoapMessage</c> (SDK header line ~5317).</summary>
    public static readonly Guid IID_IPlayReadySoapMessage = new("b659fcb5-ce41-41ba-8a0d-61df5fffa139");

    // ── WinRT parameterized / collection IIDs ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IID of <c>Windows.Foundation.Collections.IMap&lt;String, Object&gt;</c> (= <c>IMap&lt;HSTRING, IInspectable&gt;</c>),
    /// the interface a <c>PropertySet</c> is QI'd to for <c>Insert</c>. This value is DERIVED via the WinRT parameterized
    /// generic-instance IID algorithm (RFC-4122 v5 over the pinterface namespace) and independently matches the
    /// universally-published constant <c>1b0d3570-…</c>, so it is treated as verified rather than <c>// VERIFY</c>.
    /// </summary>
    public static readonly Guid IID_IMap_String_Object = new("1b0d3570-0877-5ec2-8a2c-3b9539506aca");

    // ── Sentinel HRESULTs ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED</c> — the individualization "chase the NextServiceRequest"
    /// sentinel (<c>Windows.Media.Protection.PlayReadyErrors.h</c>; WaveeMusic recipe).</summary>
    public const int MSPR_E_CONTENT_ENABLING_ACTION_REQUIRED = unchecked((int)0x8004B895);

    /// <summary><c>MF_E_TOPOLOGY_VERIFICATION_FAILED</c> — the ITA-verification failure this whole spike exists to avoid
    /// (the WinAppSDK <c>SourceNotSupported</c> failure the user reported).</summary>
    public const int MF_E_TOPOLOGY_VERIFICATION_FAILED = unchecked((int)0xC00D715B);
}
