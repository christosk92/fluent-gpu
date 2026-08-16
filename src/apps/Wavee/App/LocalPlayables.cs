using System;
using System.Collections.Generic;
using System.IO;
using Wavee.Backend.MediaSources;
using Wavee.Core;

namespace Wavee;

// ── SYNTHETIC PLAYABLES — how a file becomes something the queue can carry ────────────────────────────────────────────
// `Track` stays THE playable identity (the seam decision): episodes already ride the queue/controller/projection as
// Track records, and so do these. Nothing here is Spotify-shaped — no id, no album, no artist — because nothing between
// play-intent and the media host inspects those; the uri routes, the provider resolves, the projection displays.
//
// The entry point is PlaybackController.PlayTrackAsync(Track): the no-context, no-hydration verb. That is the whole
// reason these Tracks must be COMPLETE at construction — there is no resolver behind them to fill anything in later.
//
// Engine-free (Wavee.Core + BCL) so it is unit-testable headlessly, exactly like VideoOverrideUx.

/// <summary>Builds the synthetic <see cref="Track"/> records for the two non-catalog playable namespaces.</summary>
public static class LocalPlayables
{
    /// <summary>A local audio file (.mp3/.ogg/.flac) as a playable. <see cref="TrackOrigin.Local"/> is what makes the
    /// controller classify it <c>PlayableKind.LocalFile</c> — the audio host, <c>track_player:"audio"</c>, no crossfade
    /// across its boundaries — all through rules that already existed and are already tested.</summary>
    /// <param name="absolutePath">The file's absolute path (it is stored verbatim inside the uri).</param>
    /// <param name="probeDurationMs">Optional header duration probe. Passed in rather than called directly so this file
    /// stays codec-free; the live wiring supplies <c>LocalAudioDurationProbe.Probe</c>.</param>
    public static Track ForLocalFile(string absolutePath, Func<string, long>? probeDurationMs = null)
    {
        string uri = PlayableUri.ForLocalFile(absolutePath);
        return Build(uri, absolutePath, TrackOrigin.Local, "local", probeDurationMs);
    }

    /// <summary>A generic "play this" playable: a path or an http(s) URL. Origin stays <see cref="TrackOrigin.Streamed"/>
    /// — deliberately NOT Local — so it classifies as plain Audio and plays through the very same host branch external
    /// podcast episodes do. (A generic playable is not necessarily a file, and lying about that would put a URL through
    /// the local-file kind for no gain.)</summary>
    public static Track ForMedia(string pathOrUrl, Func<string, long>? probeDurationMs = null)
    {
        string uri = PlayableUri.ForMedia(pathOrUrl);
        return Build(uri, pathOrUrl, TrackOrigin.Streamed, "media", probeDurationMs);
    }

    static Track Build(string uri, string pathOrUrl, TrackOrigin origin, string source, Func<string, long>? probe)
    {
        long durationMs = 0;
        if (probe is not null && !PlayableUri.IsHttpUrl(pathOrUrl))
        {
            // Fail-soft: an unreadable header means "unknown length", never "cannot play". The projection folds a
            // duration only when it is > 0, so 0 simply leaves the seek bar without a total.
            try { durationMs = Math.Max(0, probe(pathOrUrl)); }
            catch { durationMs = 0; }
        }
        return new Track(
            // Row identity comparisons (TrackRow.Invoke's "is this already playing?") compare ids, so the id must be 1:1
            // with the uri — the encoded payload EntityUri.IdOf returns is exactly that, and it never collides with a
            // Spotify base-62 id.
            Id: EntityUri.IdOf(uri),
            Uri: uri,
            Title: TitleOf(pathOrUrl),
            Artists: Array.Empty<ArtistRef>(),
            Album: new AlbumRef("", "", ""),
            DurationMs: durationMs,
            IsExplicit: false,
            Image: null,
            Origin: origin,
            // Stated, not defaulted. Availability is nullable ("nobody has told us") because only getAlbum/getTrack
            // carry a server verdict — but a file the user just handed us needs no server to be playable, and leaving
            // it unknown would let a "playable only" filter hide the very file they dropped in.
            Availability: Availability.Playable,
            Source: source);
    }

    /// <summary>The display title: the file name without its extension (tag reading is explicitly out of scope — a
    /// file name is what the user named the thing, and it is honest). A URL falls back to its last path segment.</summary>
    public static string TitleOf(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return "";
        try
        {
            if (PlayableUri.IsHttpUrl(pathOrUrl))
            {
                int q = pathOrUrl.IndexOfAny(['?', '#']);
                string trimmed = q >= 0 ? pathOrUrl[..q] : pathOrUrl;
                int slash = trimmed.LastIndexOf('/');
                string tail = slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
                return tail.Length > 0 ? tail : pathOrUrl;
            }
            var name = Path.GetFileNameWithoutExtension(pathOrUrl);
            return name is { Length: > 0 } ? name : pathOrUrl;
        }
        catch { return pathOrUrl; }
    }

    // ── drop classification ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a shell-level file drop should do. Pure, so the whole drop policy is testable without an engine.</summary>
    public enum DropAction : byte
    {
        /// <summary>Nothing in the drop is playable — the drop is refused with a cue; nothing is persisted.</summary>
        None,
        /// <summary>An audio file (.mp3/.ogg/.flac): play it.</summary>
        PlayAudio,
        /// <summary>An .mp4: attach it to its OWN generic playable as that playable's video override, then play it —
        /// which is how a dropped video plays with its embedded audio through the existing override machinery.</summary>
        PlayVideo,
    }

    /// <summary>Classify a shell drop. Audio wins over video when the drop carries both, because a plain audio drop is
    /// the unambiguous "play this song" gesture; a mixed drop is not an error, it just takes the first playable thing.</summary>
    public static DropAction ClassifyDrop(IReadOnlyList<string>? paths, out string picked)
    {
        picked = "";
        if (paths is null) return DropAction.None;
        for (int i = 0; i < paths.Count; i++)
            if (LocalFileMediaProvider.IsSupportedAudioFile(paths[i])) { picked = paths[i]; return DropAction.PlayAudio; }
        for (int i = 0; i < paths.Count; i++)
            if (VideoOverrideUx.IsMp4(paths[i])) { picked = paths[i]; return DropAction.PlayVideo; }
        return DropAction.None;
    }
}
