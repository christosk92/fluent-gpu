using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Pb = Wavee.Protocol.PreRelease;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

// ── Upcoming-release identity (extended-metadata kind 138) ───────────────────────────────────────────────────────────
// One read that answers two questions at once. The wire serves the SAME payload under both a `spotify:album:` and a
// `spotify:prerelease:` entity_uri (E2E-DIFF.md §5.4.1), and the payload names both uris — so a single round trip with
// whichever uri the caller happens to hold resolves the pair in both directions. Their ids differ (see PreReleaseUris),
// so this kind is the ONLY mapping; nothing may synthesise one uri from the other.
//
// Kind 138 404s for almost every entity (3 of the 5 captured entities 404'd), which is why a miss is a cached NULL and
// never an error: "no upcoming release" is the correct answer for every album that is already out, and the announce
// surfaces simply do not render.
sealed class SpotifyPreReleaseService : IPreReleaseService
{
    // Unknown fields are discarded rather than retained: nothing here round-trips a payload back to the server, and the
    // corpus already shows Spotify adding fields to these trait payloads over time.
    static readonly MessageParser<Pb.Prerelease> PayloadParser = Pb.Prerelease.Parser.WithDiscardUnknownFields(true);

    readonly ExtendedMetadataSource _metadata;
    readonly ExtensionEtagCache? _extensions;
    readonly WaveeLogger _log;

    /// <summary>Resolved answers keyed by EVERY uri that resolves to them — see <see cref="Cache"/>. Holds negatives
    /// (a null value) as well as links: without that, every album open of an ordinary released album would re-ask the
    /// wire for a kind it is never going to have. The negative is process-lifetime by design — the underlying
    /// <see cref="ExtensionEtagCache"/> already owns the durable 24 h missing-TTL, and this table only stops the
    /// re-render storm above it.</summary>
    readonly ConcurrentDictionary<string, PreReleaseLink?> _byUri = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Task<PreReleaseLink?>> _inFlight = new(StringComparer.Ordinal);

    public SpotifyPreReleaseService(ExtendedMetadataSource metadata, WaveeLogger log = default, ExtensionEtagCache? extensions = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _extensions = extensions;
        _log = log;
    }

    public async Task<PreReleaseLink?> ResolveAsync(string uri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        // TryGetValue (not `?? fetch`) so a cached NEGATIVE is distinguishable from a cache miss.
        if (_byUri.TryGetValue(uri, out var hit)) return hit;

        var task = _inFlight.GetOrAdd(uri, static (u, self) => self.LoadAsync(u), this);
        // WaitAsync so navigating away cancels the AWAIT, not the shared load a second surface may be joined to
        // (the artist masthead and the album page routinely ask for the same release at the same moment).
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    async Task<PreReleaseLink?> LoadAsync(string uri)
    {
        try
        {
            ByteString? payload;
            try
            {
                // Etag-cache-preferred, raw source as the fallback — the same shape every other extension reader uses
                // (SpotifyAlbumEnrichmentService, SpotifyVideoService). CancellationToken.None: this task is SHARED, so
                // one caller's nav-away must not cancel it out from under the others (they hold it via WaitAsync).
                payload = _extensions is not null
                    ? await _extensions.GetPayloadAsync(uri, Xm.ExtensionKind.Prerelease, CancellationToken.None).ConfigureAwait(false)
                    : await _metadata.GetExtensionAsync(uri, Xm.ExtensionKind.Prerelease, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort by contract: a network blip must never break an album open or an artist page.
                _log.Info("PRERELEASE fetch: " + ex.Message);
                return Cache(uri, null);
            }

            if (payload is null) return Cache(uri, null);   // 404 — the ordinary answer for an already-released entity

            Pb.Prerelease msg;
            try { msg = PayloadParser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException ex)
            {
                _log.Info("PRERELEASE parse: " + ex.Message);
                return Cache(uri, null);
            }

            var link = Link(msg);
            if (link is null)
            {
                _log.Info("PRERELEASE half-link discarded for " + uri);
                return Cache(uri, null);
            }
            _log.Debug($"prerelease resolved {uri} -> {link.PreReleaseUri} / {link.AlbumUri} at {link.ReleaseAt}");
            return Cache(uri, link);
        }
        finally
        {
            // Success or failure: the coalescing slot is released so a later resolve can retry a transient failure
            // (a cached negative short-circuits before we ever get here, so this is not a retry storm).
            _inFlight.TryRemove(uri, out _);
        }
    }

    /// <summary>Publishes one answer under THREE keys: the uri the caller asked with, plus — for a positive — the
    /// payload's own prerelease and album uris. That is what makes the single round trip serve BOTH directions: the
    /// artist masthead resolves an album uri and the pre-save heart later asks with the prerelease uri (or the reverse,
    /// from a `spotify:prerelease:` link), and neither pays a second request.</summary>
    PreReleaseLink? Cache(string queryUri, PreReleaseLink? link)
    {
        _byUri[queryUri] = link;
        if (link is not null)
        {
            _byUri[link.PreReleaseUri] = link;
            _byUri[link.AlbumUri] = link;
        }
        return link;
    }

    /// <summary>Projects the wire payload onto <see cref="PreReleaseLink"/>, or null when it is not a usable link.</summary>
    static PreReleaseLink? Link(Pb.Prerelease msg)
    {
        string prereleaseUri = msg.PrereleaseUri;
        string albumUri = msg.Release?.AlbumUri ?? "";
        // HALF-LINKS ARE REJECTED. The two uris cannot be derived from one another, so a payload carrying only one of
        // them is not a link: with no album uri nothing can be navigated to, and with no prerelease uri nothing can be
        // pre-saved. Returning null here (rather than a link with an empty field) keeps that impossible state out of
        // the record entirely — every consumer downstream may assume both uris are real.
        if (prereleaseUri.Length == 0 || albumUri.Length == 0) return null;

        var release = msg.Release!;
        // seconds == 0 is "absent", not 1970: an announced-but-undated release is a real shape, and a null ReleaseAt is
        // what PreReleaseLink.IsUpcoming reads as "upcoming, date unknown".
        DateTimeOffset? releaseAt = msg.ReleaseAt is { Seconds: > 0 } t ? DateTimeOffset.FromUnixTimeSeconds(t.Seconds) : null;

        ArtistRef? artist = release.Artist is { Uri.Length: > 0 } a
            ? new ArtistRef(SpotifyExportMapper.IdFromUri(a.Uri), a.Uri, a.Name)
            : null;

        return new PreReleaseLink(
            prereleaseUri,
            albumUri,
            releaseAt,
            Name: release.Name is { Length: > 0 } name ? name : null,
            Type: release.Type is { Length: > 0 } type ? type : null,
            Artist: artist,
            Cover: Cover(release.Images));
    }

    /// <summary>The cover to show. Prefer the "DEFAULT" rendition (600 px in the captured payload) — it is the one
    /// Spotify's own surfaces use — and fall back to the largest by area when the size names change or are absent.
    /// The LARGE rendition rides along as <see cref="Image.LargestUrl"/> so an immersive surface is not stuck with the
    /// card-sized URL. Sizes are STRINGS in this kind, unlike the integer enums of kinds 179/98.</summary>
    static Image? Cover(RepeatedField<Pb.Prerelease.Types.Image>? images)
    {
        if (images is null || images.Count == 0) return null;

        Pb.Prerelease.Types.Image? best = null, largest = null;
        foreach (var img in images)
        {
            if (img.Url.Length == 0) continue;
            if (largest is null || (long)img.Width * img.Height > (long)largest.Width * largest.Height) largest = img;
            if (best is null && string.Equals(img.Size, "DEFAULT", StringComparison.OrdinalIgnoreCase)) best = img;
        }
        best ??= largest;
        if (best is null) return null;

        string? largestUrl = largest is not null && !ReferenceEquals(largest, best) ? largest.Url : null;
        return new Image(best.Url,
            best.Width > 0 ? (int?)best.Width : null,
            best.Height > 0 ? (int?)best.Height : null,
            LargestUrl: largestUrl);
    }
}
