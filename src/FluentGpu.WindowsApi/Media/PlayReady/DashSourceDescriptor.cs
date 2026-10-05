using System;
using System.Collections.Generic;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>One addressable CENC representation. The identity is stable for the lifetime of the manifest; switching
/// representations never changes the PlayReady session or presentation clock.</summary>
public sealed record ProtectedRepresentationDescriptor
{
    public required string Id { get; init; }
    public required FluentGpu.Media.QualityVariant Quality { get; init; }
    public required string InitUrl { get; init; }
    public required string SegmentBaseUrl { get; init; }
    public required string SegmentPrefix { get; init; }
    public required string SegmentSuffix { get; init; }
    public int StartNumber { get; init; } = 1;
    public int SegmentCount { get; init; } = 1;
    public int SegmentStride { get; init; } = 1;
    public string? DefaultKid { get; init; }
}

/// <summary>One selectable protected media track and all of its compatible representations.</summary>
public sealed record ProtectedTrackDescriptor
{
    public required int Id { get; init; }
    public required FluentGpu.Media.TrackKind Kind { get; init; }
    public string? Language { get; init; }
    public required string Label { get; init; }
    public FluentGpu.Media.TrackRole Role { get; init; } = FluentGpu.Media.TrackRole.Main;
    public bool IsDefault { get; init; }
    public required IReadOnlyList<ProtectedRepresentationDescriptor> Representations { get; init; }
}

/// <summary>The complete protected-media catalog discovered in a manifest.</summary>
public sealed record ProtectedAdaptiveCatalog
{
    public required IReadOnlyList<ProtectedTrackDescriptor> Tracks { get; init; }
}

/// <summary>
/// The parsed source descriptor an MPD yields for the native PlayReady open path (<c>FgPrOpenDesc</c>): an explicit
/// init-segment URL plus the <c>base + prefix + number + suffix</c> media-segment template the native demuxer walks, the
/// segment range, and the PlayReady init data (<c>cenc:pssh</c> + <c>default_KID</c>). Produced by
/// <see cref="DashDescriptorMapper"/> (the engine's <c>AdaptiveManifest</c> mapped onto this shape); consumed by <see cref="ProtectedMediaBackend"/> (mapped onto a
/// <see cref="ProtectedVideoRequest"/>). The license server URL is a SEPARATE concern (entered by the app, carried on
/// <see cref="FluentGpu.Media.DrmConfig"/> + the <c>WithDrm</c> relay) — a manifest does not carry it.
/// </summary>
public sealed record DashSourceDescriptor
{
    /// <summary>All selectable tracks and quality representations. Null preserves the legacy single-representation ABI.</summary>
    public ProtectedAdaptiveCatalog? Catalog { get; init; }
    /// <summary>Absolute URL of the H.264 initialization segment.</summary>
    public required string InitUrl { get; init; }
    /// <summary>Base URL for the numbered media segments (the directory the segment names resolve against).</summary>
    public required string SegmentBaseUrl { get; init; }
    /// <summary>Media-segment name PREFIX (everything before the <c>$Number$</c> token, after the base).</summary>
    public required string SegmentPrefix { get; init; }
    /// <summary>Media-segment name SUFFIX (everything after the <c>$Number$</c> token, e.g. <c>.m4s</c>).</summary>
    public required string SegmentSuffix { get; init; }
    /// <summary>First segment number (<c>SegmentTemplate@startNumber</c>, default 1).</summary>
    public int StartNumber { get; init; } = 1;
    /// <summary>Number of media segments to fetch (from the <c>SegmentTimeline</c>, or <c>@duration</c>/<c>@timescale</c>
    /// against the presentation duration).</summary>
    public int SegmentCount { get; init; } = 1;
    /// <summary>Segment-number step: 1 for numbered <c>$Number$</c> content; N for time-addressed segments (Spotify names
    /// segments by absolute time — segment i = <see cref="StartNumber"/> + i*stride, stride = segment length in seconds).</summary>
    public int SegmentStride { get; init; } = 1;

    // ── the seek index hints ─────────────────────────────────────────────────────────────────────────────────────────
    // A template-addressed DASH source needs no `sidx`: segment i starts at i·SegmentLengthMs and (with
    // SegmentsStartWithKeyframe) every segment start is a keyframe, so a seek maps to ONE segment GET per stream with no
    // index fetch at all — the arithmetic IS the index. The keyframes INSIDE a segment are learnt by the native demuxer
    // as segments are parsed (the session's keyframe table); these fields are what the planner knows before any byte.
    /// <summary>The presentation length of one media segment in ms (<c>SegmentTemplate@duration / @timescale</c>, or the
    /// first <c>SegmentTimeline/S@d</c>); 0 when the MPD does not say (the native side measures it from the first
    /// parsed segment). Spotify: <c>segment_length</c> × 1000.</summary>
    public int SegmentLengthMs { get; init; }
    /// <summary>The presentation duration in ms (<c>MPD@mediaPresentationDuration</c>), or 0 when unknown.</summary>
    public long DurationMs { get; init; }
    /// <summary>Whether every media segment begins with a stream access point of type 1 or 2 (an IDR), per
    /// <c>@startWithSAP</c> on the Representation or its AdaptationSet. An MPD that does not declare it is assumed to
    /// (the DASH-IF interoperability profiles require it for segment-template content); only an explicit 0 or ≥ 3 says
    /// otherwise, and then the seek planner may not treat a segment start as a keyframe.</summary>
    public bool SegmentsStartWithKeyframe { get; init; } = true;

    /// <summary>The PlayReady <c>cenc:pssh</c> init data (decoded from base64), or empty when the native parses it from the
    /// init segment.</summary>
    public ReadOnlyMemory<byte> Pssh { get; init; }
    /// <summary>The content key id (<c>@cenc:default_KID</c>), hex, dashless — advisory (the native derives the KID from the
    /// init segment).</summary>
    public string? DefaultKid { get; init; }
    /// <summary>The chosen video representation's <c>@id</c> (advisory / diagnostics).</summary>
    public string? RepresentationId { get; init; }
    /// <summary>The chosen representation's <c>@codecs</c> (e.g. <c>avc1.640028</c>).</summary>
    public string? Codecs { get; init; }

    // ── the paired AUDIO representation (optional) ───────────────────────────────────────────────────────────────────
    // A music video carries its own soundtrack on a second, separately-addressed representation under the same content
    // key. All four URL parts are null when the source has no usable audio, in which case the native side plays video
    // only — the absence must degrade, never fail. <see cref="SegmentCount"/>/<see cref="StartNumber"/>/
    // <see cref="SegmentStride"/> are shared: both representations are cut on the same segment grid.
    /// <summary>Absolute URL of the audio initialization segment, or null when there is no audio representation.</summary>
    public string? AudioInitUrl { get; init; }
    /// <summary>Base URL for the audio media segments.</summary>
    public string? AudioSegmentBaseUrl { get; init; }
    /// <summary>Audio media-segment name prefix.</summary>
    public string? AudioSegmentPrefix { get; init; }
    /// <summary>Audio media-segment name suffix.</summary>
    public string? AudioSegmentSuffix { get; init; }
    /// <summary>The audio representation's <c>@codecs</c> (e.g. <c>mp4a.40.2</c>).</summary>
    public string? AudioCodecs { get; init; }
}

/// <summary>Thrown when an MPD cannot be mapped into a playable protected descriptor (no H.264 video representation, no
/// segment URL pattern the native template can express, malformed XML, …). Surfaced by the app as an inline / typed error — never a silent black frame.</summary>
public sealed class DashManifestException : Exception
{
    public DashManifestException(string message, Exception? inner = null) : base(message, inner) { }
}
