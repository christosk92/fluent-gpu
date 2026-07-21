using System;
using System.Collections.Generic;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>The operation currently preventing continuous playback. This is orthogonal to <see cref="PlaybackState"/>
/// so chrome can distinguish initial load, an accurate seek, a quality switch, and a recoverable network rebuffer.</summary>
public enum BufferingReason : byte
{
    None, Initial, Seeking, TrackSwitch, QualitySwitch, Rebuffering, LiveCatchUp, NetworkRecovery
}

/// <summary>Professional buffering snapshot. Percent is 0..1 when known and -1 for an indeterminate spinner.</summary>
public readonly record struct BufferingInfo(
    BufferingReason Reason, double Percent, TimeSpan BufferedAhead, TimeSpan TargetAhead, bool CanResume)
{
    public static BufferingInfo None => new(BufferingReason.None, 1, TimeSpan.Zero, TimeSpan.Zero, true);
    public bool IsBuffering => Reason != BufferingReason.None;
}

/// <summary>Manifest family selected for adaptive playback.</summary>
public enum AdaptiveManifestKind : byte { Auto, Dash, Hls }

/// <summary>Latency target for adaptive live playback.</summary>
public enum LiveLatencyMode : byte { Standard, LowLatency, Custom }

/// <summary>Adaptive-source policy. The portable scheduler consumes this; platform decoders never parse policy.</summary>
public sealed record AdaptiveSourceOptions
{
    public AdaptiveManifestKind ManifestKind { get; init; } = AdaptiveManifestKind.Auto;
    public LiveLatencyMode LatencyMode { get; init; } = LiveLatencyMode.Standard;
    public TimeSpan? TargetLiveOffset { get; init; }
    public bool AllowCodecSwitch { get; init; } = true;
    public bool PreferHdr { get; init; } = true;
}

/// <summary>One chapter marker in the authoritative media timeline.</summary>
public readonly record struct MediaChapter(string Id, string Title, TimeSpan Start, TimeSpan? End = null);

/// <summary>Seekable/live-window state. Unknown non-live media uses <see cref="Empty"/>.</summary>
public readonly record struct TimelineInfo(
    bool IsLive, TimeSpan SeekableStart, TimeSpan SeekableEnd, TimeSpan LiveEdge,
    TimeSpan LiveOffset, bool IsAtLiveEdge, IReadOnlyList<MediaChapter> Chapters)
{
    public static TimelineInfo Empty { get; } = new(false, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
        TimeSpan.Zero, false, Array.Empty<MediaChapter>());
    public TimeSpan DvrWindow => SeekableEnd > SeekableStart ? SeekableEnd - SeekableStart : TimeSpan.Zero;
}

/// <summary>Rational pixel/sample aspect ratio. <c>1/1</c> means square pixels.</summary>
public readonly record struct PixelAspectRatio(int Numerator, int Denominator)
{
    public static PixelAspectRatio Square => new(1, 1);
    public double Value => Numerator > 0 && Denominator > 0 ? (double)Numerator / Denominator : 1.0;
}

/// <summary>Integer clean-aperture rectangle inside the coded frame.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Complete display geometry, including non-square pixels and rotation. DisplaySize is the post-aperture,
/// post-SAR, post-rotation size the UI should aspect-fit.</summary>
public readonly record struct VideoGeometry(
    SizeI CodedSize, PixelRect CleanAperture, PixelAspectRatio SampleAspectRatio, ushort RotationDegrees,
    SizeI DisplaySize)
{
    public static VideoGeometry Empty => new(SizeI.Zero, default, PixelAspectRatio.Square, 0, SizeI.Zero);
    public bool HasVideo => !DisplaySize.IsEmpty || !CodedSize.IsEmpty;
}

public enum VideoColorPrimaries : byte { Unknown, Bt601, Bt709, Bt2020, DisplayP3 }
public enum VideoTransfer : byte { Unknown, Srgb, Bt1886, Pq, Hlg }
public enum VideoMatrix : byte { Unknown, Rgb, Bt601, Bt709, Bt2020NonConstant }
public enum VideoRange : byte { Unknown, Limited, Full }
public enum HdrFormat : byte { Sdr, Hdr10, Hlg }

/// <summary>Static HDR mastering metadata. Values use the stream's normalized chromaticity/luminance units.</summary>
public readonly record struct HdrMasteringMetadata(
    float Rx, float Ry, float Gx, float Gy, float Bx, float By, float Wx, float Wy,
    float MinLuminance, float MaxLuminance, ushort MaxContentLightLevel, ushort MaxFrameAverageLightLevel);

/// <summary>Video colorimetry and HDR signaling surfaced before presentation policy is selected.</summary>
public readonly record struct VideoColorInfo(
    VideoColorPrimaries Primaries, VideoTransfer Transfer, VideoMatrix Matrix, VideoRange Range,
    HdrFormat Hdr, HdrMasteringMetadata? Mastering)
{
    public static VideoColorInfo Sdr => new(VideoColorPrimaries.Bt709, VideoTransfer.Bt1886,
        VideoMatrix.Bt709, VideoRange.Limited, HdrFormat.Sdr, null);
}

/// <summary>How video content maps into its layout rectangle.</summary>
public enum VideoAspectMode : byte { Uniform, UniformToFill, Fill, Native, Custom }

/// <summary>One selectable adaptive representation.</summary>
public sealed record QualityVariant(
    string Id, int Bitrate, SizeI Resolution, double FrameRate, MediaContentType Codec,
    HdrFormat Hdr = HdrFormat.Sdr, string? Label = null);

/// <summary>Automatic or manually-pinned quality selection.</summary>
public readonly record struct QualitySelection(bool IsAuto, string? VariantId)
{
    public static QualitySelection Auto => new(true, null);
    public static QualitySelection Pin(string variantId) => new(false, variantId);
}

/// <summary>Observable quality collection and acknowledged selection.</summary>
public sealed class QualitySet
{
    private readonly Signal<QualitySelection> _selected = new(QualitySelection.Auto);
    private readonly Signal<QualityVariant?> _active = new(null);
    public ObservableList<QualityVariant> Variants { get; } = new();
    public IReadSignal<QualitySelection> Selected => _selected;
    public IReadSignal<QualityVariant?> Active => _active;
    public void PublishSelection(QualitySelection selection) => _selected.Value = selection;
    public void PublishActive(QualityVariant? variant) => _active.Value = variant;
}

/// <summary>Low-frequency diagnostic snapshot. Backends publish it at a bounded cadence, never per decoded sample.</summary>
public readonly record struct PlaybackStatistics(
    long BytesDownloaded, long FramesDecoded, long FramesDropped, long AudioUnderruns,
    double EstimatedThroughputKbps, double VideoBitrateKbps, double AudioBitrateKbps,
    TimeSpan StartupTime, TimeSpan RebufferTime, int RebufferCount)
{
    public static PlaybackStatistics Empty => default;
}
