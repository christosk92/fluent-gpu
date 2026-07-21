using System;
using System.Collections.Generic;

namespace Wavee.Core;

public enum ImageSourceQuality
{
    None = 0,
    Unresolved = 1,
    Usable = 2,
}

public static class ImageSource
{
    const string SpotifyImagePrefix = "spotify:image:";
    const string SpotifyImageCdnPrefix = "https://i.scdn.co/image/";

    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith(SpotifyImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = trimmed[SpotifyImagePrefix.Length..].Trim();
            return id.Length == 0 ? "" : SpotifyImageCdnPrefix + id;
        }

        return trimmed;
    }

    public static IReadOnlyList<string>? NormalizeAll(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return values;

        string[]? normalized = null;
        for (int i = 0; i < values.Count; i++)
        {
            var current = values[i];
            var next = Normalize(current) ?? "";
            if (normalized is not null) normalized[i] = next;
            else if (next != current)
            {
                normalized = new string[values.Count];
                for (int j = 0; j < i; j++) normalized[j] = values[j];
                normalized[i] = next;
            }
        }

        return normalized ?? values;
    }

    public static ImageSourceQuality Quality(Image? image)
    {
        if (image is null) return ImageSourceQuality.None;
        var url = Quality(image.Url);
        if (url == ImageSourceQuality.Usable) return url;

        var tiles = Quality(image.MosaicTiles);
        return tiles > url ? tiles : url;
    }

    public static bool IsUsable(Image? image) => Quality(image) == ImageSourceQuality.Usable;

    public static Image? ChooseBetter(Image? primary, Image? fallback)
    {
        if (primary is null) return fallback;
        if (fallback is null) return primary;
        return Quality(primary) >= Quality(fallback) ? primary : fallback;
    }

    /// <summary>True when both images resolve to the same cover identity (normalized URL, or matching mosaic tiles
    /// when neither has a URL). Null/empty pairs are not "the same source".</summary>
    public static bool SameSource(Image? a, Image? b)
    {
        if (a is null || b is null) return false;
        var ua = Normalize(a.Url) ?? "";
        var ub = Normalize(b.Url) ?? "";
        if (ua.Length > 0 && ub.Length > 0)
            return string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase);
        if (ua.Length > 0 || ub.Length > 0) return false;
        return MosaicEquals(a.MosaicTiles, b.MosaicTiles);
    }

    /// <summary>Detail-page handoff: keep the already-visible cover when the loaded model brings a different usable CDN
    /// URL (typical: card-sized hash → largest-size hash for the same art). Swapping would blank the hero (new
    /// <c>ImageCache</c> key → Pending → 220ms crossfade over the placeholder). Prefer <paramref name="incoming"/> only
    /// when nothing usable is on screen yet, or when both share the same source (then merge richer metadata onto the
    /// visible identity). Intentional cover changes after load must not pass the mount-time preview as
    /// <paramref name="visible"/> — latch the chosen cover per route instead.</summary>
    public static Image? PreferVisible(Image? incoming, Image? visible)
    {
        bool inOk = IsUsable(incoming);
        bool visOk = IsUsable(visible);
        if (!inOk) return visOk ? visible : incoming ?? visible;
        if (!visOk) return incoming;
        if (SameSource(incoming, visible)) return EnrichVisible(visible!, incoming!);
        return visible;
    }

    public static ImageSourceQuality Quality(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return ImageSourceQuality.None;
        return IsUnresolvedProviderToken(source.Trim()) ? ImageSourceQuality.Unresolved : ImageSourceQuality.Usable;
    }

    static Image EnrichVisible(Image visible, Image incoming)
    {
        bool needBlur = string.IsNullOrEmpty(visible.BlurHash) && !string.IsNullOrEmpty(incoming.BlurHash);
        bool needW = visible.Width is null && incoming.Width is not null;
        bool needH = visible.Height is null && incoming.Height is not null;
        if (!needBlur && !needW && !needH) return visible;
        return visible with
        {
            BlurHash = needBlur ? incoming.BlurHash : visible.BlurHash,
            Width = needW ? incoming.Width : visible.Width,
            Height = needH ? incoming.Height : visible.Height,
        };
    }

    static bool MosaicEquals(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var ua = Normalize(a[i]) ?? "";
            var ub = Normalize(b[i]) ?? "";
            if (!string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return a.Count > 0;
    }

    static ImageSourceQuality Quality(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return ImageSourceQuality.None;
        var best = ImageSourceQuality.None;
        for (int i = 0; i < values.Count && best != ImageSourceQuality.Usable; i++)
        {
            var q = Quality(values[i]);
            if (q > best) best = q;
        }

        return best;
    }

    static bool IsUnresolvedProviderToken(string source)
        => source.StartsWith(SpotifyImagePrefix, StringComparison.OrdinalIgnoreCase);
}
