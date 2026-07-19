using System;
using System.Threading.Tasks;
using FluentGpu.Media;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>Tuning for an in-process <see cref="DesktopProtectedVideoPlayer"/> session.</summary>
public sealed class ProtectedVideoOptions
{
    /// <summary>The playback mode: the proven protected path by default; <c>"clear"</c> selects the clear-video backend.</summary>
    public string Mode { get; init; } = "protected-custom";
}

/// <summary>
/// The M5 generalized open request for a protected session: a SOURCE descriptor (an explicit DASH init+segment URI
/// template and/or an explicit PSSH + HTTP headers) plus the app-supplied license relay (spec §9.2 <c>WithDrm</c>).
/// License acquisition lives in this managed relay, not native. When no source template is supplied the native backend
/// falls back to its baked Axinom singlekey test vector (so a plain <see cref="Drm"/>-only request still plays on-box).
/// </summary>
public sealed record ProtectedVideoRequest
{
    /// <summary>The originating <see cref="MediaSource"/> (advisory — carries the URI + metadata), or null.</summary>
    public MediaSource? Source { get; init; }
    /// <summary>The protection configuration (drives the <see cref="DrmSystem"/> reported to the relay).</summary>
    public DrmConfig? Drm { get; init; }
    /// <summary>The app license relay: a CDM challenge → a license blob (runs on a worker; bounded by <see cref="LicenseTimeout"/>).</summary>
    public Func<LicenseRequest, ValueTask<LicenseResponse>>? LicenseRelay { get; init; }

    /// <summary>Explicit init-segment URL (M6/testing). Null ⇒ native uses its baked Axinom source.</summary>
    public string? InitUrl { get; init; }
    /// <summary>Base URL for numbered media segments.</summary>
    public string? SegmentBaseUrl { get; init; }
    /// <summary>Media-segment name prefix.</summary>
    public string? SegmentPrefix { get; init; }
    /// <summary>Media-segment name suffix (e.g. <c>.m4s</c>).</summary>
    public string? SegmentSuffix { get; init; }
    /// <summary>First segment number.</summary>
    public int StartNumber { get; init; } = 1;
    /// <summary>Segment count to fetch.</summary>
    public int SegmentCount { get; init; } = 6;
    /// <summary>Optional explicit PlayReady PSSH init data (else parsed natively from the init segment).</summary>
    public ReadOnlyMemory<byte> Pssh { get; init; }
    /// <summary>Optional <c>"Name: Value\n"</c> HTTP headers applied to segment fetches (auth for a real CDN).</summary>
    public string? HttpHeaders { get; init; }
    /// <summary>The bounded license-acquisition timeout (a shortfall becomes a <see cref="MediaErrorCategory.Drm"/> error).</summary>
    public TimeSpan LicenseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The playback mode (<c>"clear"</c> selects the native clear diagnostic; else the protected CENC path).</summary>
    public string? Mode { get; init; }
}

/// <summary>The lifecycle state of a protected-video session, surfaced as a signal on the player façade.</summary>
public enum ProtectedVideoState
{
    /// <summary>No session — created but not started.</summary>
    Idle = 0,
    /// <summary>The backend process is being registered/activated.</summary>
    Launching,
    /// <summary>Process is up; waiting for the handshake.</summary>
    Connecting,
    /// <summary>A source is loading (demux/CDM/license in progress).</summary>
    Loading,
    /// <summary>The DRM license reached USABLE.</summary>
    Licensed,
    /// <summary>Rebuffering (transient, mid-playback).</summary>
    Buffering,
    /// <summary>Decoding and presenting frames.</summary>
    Playing,
    /// <summary>Paused by request.</summary>
    Paused,
    /// <summary>Playback finished (end of stream).</summary>
    Ended,
    /// <summary>Source unloaded / session stopped; the process may still be alive for reuse.</summary>
    Stopped,
    /// <summary>A terminal error (see the error signal for detail).</summary>
    Error,
}
