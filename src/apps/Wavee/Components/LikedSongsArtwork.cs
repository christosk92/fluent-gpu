using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

static class LikedSongsArtwork
{
    public const string Uri = "spotify:collection:tracks";
    static readonly string CoverPath = System.IO.Path.Combine(
        System.AppContext.BaseDirectory, "assets", "covers", "liked-songs-300.png");

    public static bool IsLikedUri(string? uri) => string.Equals(uri, Uri, System.StringComparison.Ordinal);

    public static Element Cover(float size, float radius, string? morphKey = null)
        // Spotify's stock collection cover, bundled so Liked Songs is correct offline and never depends on a render-time
        // network request. Keep the existing caller-owned size/corners/shared-element tag.
        => Image(CoverPath, size, size, radius, Surfaces.ArtworkPlaceholder,
                 transition: ImageTransition.None) with { MorphId = morphKey };
}
