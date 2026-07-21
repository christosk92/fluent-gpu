using System;
using System.Collections.Generic;

namespace FluentGpu.Media.Adaptive;

public enum AdaptiveTrackType : byte { Video, Audio, Text }

/// <summary>One concrete media request on the presentation timeline.</summary>
public readonly record struct AdaptiveSegment(
    Uri Uri, long Number, TimeSpan Start, TimeSpan Duration, bool IsPartial = false,
    long ByteRangeOffset = -1, long ByteRangeLength = -1,
    int DiscontinuitySequence = 0, DateTimeOffset? ProgramDateTime = null, bool IsGap = false);

/// <summary>A representation's initialization and ordered media segments.</summary>
public sealed record AdaptiveRepresentation(
    QualityVariant Quality, Uri? Initialization, IReadOnlyList<AdaptiveSegment> Segments,
    string? PlaylistUri = null, string? DrmScheme = null, ReadOnlyMemory<byte> InitData = default,
    string? AudioGroup = null, string? SubtitleGroup = null);

/// <summary>Mutually-selectable representations belonging to one audio/video/text adaptation.</summary>
public sealed record AdaptiveTrackGroup(
    string Id, AdaptiveTrackType Type, string? Language, TrackRole Role,
    IReadOnlyList<AdaptiveRepresentation> Representations, bool IsDefault = false, bool IsForced = false);

/// <summary>Normalized DASH/HLS presentation consumed by the shared scheduler.</summary>
public sealed record AdaptiveManifest(
    Uri Source, AdaptiveManifestKind Kind, bool IsLive, bool IsLowLatency,
    TimeSpan? Duration, TimeSpan MinimumUpdatePeriod, TimeSpan TimeShiftBufferDepth,
    TimeSpan SuggestedPresentationDelay, DateTimeOffset? AvailabilityStartTime,
    IReadOnlyList<AdaptiveTrackGroup> TrackGroups);

internal static class AdaptiveUri
{
    public static Uri Resolve(Uri parent, string? relative)
        => string.IsNullOrWhiteSpace(relative) ? parent : new Uri(parent, relative.Trim());

    public static string ExpandTemplate(string template, string representationId, long number, long time, int bandwidth)
    {
        string value = template.Replace("$RepresentationID$", representationId, StringComparison.Ordinal)
            .Replace("$Bandwidth$", bandwidth.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$Time$", time.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        int at = 0;
        while ((at = value.IndexOf("$Number", at, StringComparison.Ordinal)) >= 0)
        {
            int end = value.IndexOf('$', at + 7);
            if (end < 0) break;
            string token = value[at..(end + 1)];
            string format = "0";
            int pct = token.IndexOf('%');
            if (pct >= 0 && token.EndsWith("d$", StringComparison.Ordinal))
            {
                string widthText = token[(pct + 1)..^2].TrimStart('0');
                if (int.TryParse(widthText, out int width) && width is > 0 and < 20) format = new string('0', width);
            }
            string replacement = number.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            value = string.Concat(value.AsSpan(0, at), replacement, value.AsSpan(end + 1));
            at += replacement.Length;
        }
        return value.Replace("$$", "$", StringComparison.Ordinal);
    }
}
