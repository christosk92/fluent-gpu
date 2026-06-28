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

    public static ImageSourceQuality Quality(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return ImageSourceQuality.None;
        return IsUnresolvedProviderToken(source.Trim()) ? ImageSourceQuality.Unresolved : ImageSourceQuality.Usable;
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
