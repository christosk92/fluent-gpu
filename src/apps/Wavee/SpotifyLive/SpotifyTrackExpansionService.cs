using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Af = Wavee.Protocol.Audiofiles;
using Md = Wavee.Protocol.Metadata;
using Wf = Wavee.Waveforms;
using Va = Wavee.Protocol.ExtendedMetadata;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>Resolves a track's alternate versions and playable audio formats for the expanded row drawer.
///
/// Everything here is fetched ON EXPAND. That is the whole performance argument: a 10k-row playlist realizes rows
/// constantly, and the waveform/association planes are far too heavy to ride that path — kind 237 alone is ~38 KB per
/// track. Tempo and tint DO ride the row bundle (see <see cref="SpotifyTrackAdornmentService"/>) because they are a
/// few bytes and appear in the row itself.
///
/// The version LABEL problem, stated plainly: kinds 98/99 return a <c>target_uri</c> and artwork and nothing else — no
/// name, no "Live"/"Remix" tag. So the associations are resolved to real tracks in a second batch (TrackV4 + the same
/// adornments the rows use), and the drawer shows the resolved TRACK NAME. Any type wording beyond video-vs-audio
/// would be invented, so it is not shown.</summary>
public sealed class SpotifyTrackExpansionService : ITrackExpansionService
{
    const int ResolveCap = 300;   // the measured server-side per-POST entity ceiling

    static readonly MessageParser<Va.VideoAssociations> AssocParser =
        Va.VideoAssociations.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Af.AudioFilesExtensionResponse> AudioFilesParser =
        Af.AudioFilesExtensionResponse.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Md.Track> TrackParser = Md.Track.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Wf.ThreeBandWaveforms> WaveformParser =
        Wf.ThreeBandWaveforms.Parser.WithDiscardUnknownFields(true);

    readonly ExtendedMetadataSource _metadata;
    readonly IStore _store;
    readonly WaveeLogger _log;

    // Per-item format override. Session-scoped by design: "play this ONE track as FLAC" is a momentary choice, and
    // persisting it would silently diverge from the user's global quality setting forever.
    readonly ConcurrentDictionary<string, int> _formatOverrides = new(StringComparer.Ordinal);

    // Drawer contents are immutable per track, so one in-flight/completed task per uri is shared by every re-open.
    readonly ConcurrentDictionary<string, Task<TrackExpansion>> _cache = new(StringComparer.Ordinal);

    public SpotifyTrackExpansionService(ExtendedMetadataSource metadata, IStore store, WaveeLogger log = default)
    {
        _metadata = metadata;
        _store = store;
        _log = log;
    }

    public Task<TrackExpansion> GetAsync(string trackUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.Ordinal))
            return Task.FromResult(TrackExpansion.Empty);
        // GetOrAdd, so re-opening a drawer (or two surfaces opening the same track) shares ONE fetch.
        return _cache.GetOrAdd(trackUri, uri => LoadAsync(uri, ct));
    }

    public void SetFormatOverride(string uri, int? formatId)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (formatId is { } id) _formatOverrides[uri] = id;
        else _formatOverrides.TryRemove(uri, out _);
    }

    public int? FormatOverrideFor(string uri)
        => !string.IsNullOrEmpty(uri) && _formatOverrides.TryGetValue(uri, out var id) ? id : null;

    async Task<TrackExpansion> LoadAsync(string trackUri, CancellationToken ct)
    {
        try
        {
            // One POST for both planes: GzipExtensionRequest groups kinds under a single EntityRequest, so asking for
            // associations AND audio files costs one round trip.
            var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(4)
            {
                (trackUri, Xm.ExtensionKind.VideoAssociations, null),
                (trackUri, Xm.ExtensionKind.AudioAssociations, null),
                (trackUri, Xm.ExtensionKind.AudioFiles, null),
                // The waveform (~38 KB) rides this SAME request — GzipExtensionRequest groups kinds under one
                // EntityRequest, so it costs zero extra round trips and only ever loads for a row the user expanded.
                // That is precisely why it is here and not in the row bundle, where 300 realized rows would pull ~11 MB.
                (trackUri, Xm.ExtensionKind.MixThreeBandWaveforms, null),
            };

            var results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false);

            var targets = new List<(string Uri, TrackVersionKind Kind)>(4);
            results.TryGetValue((trackUri, Xm.ExtensionKind.VideoAssociations), out var video);
            results.TryGetValue((trackUri, Xm.ExtensionKind.AudioAssociations), out var audio);
            // Teach the plane what this fetch just learned — the SAME fold the detect batch uses, so the row's
            // has-video indicator and this drawer can never disagree about a payload one of them has in hand.
            // This is also the heal path: a row whose association was a stale negative lights up on expand.
            // (A missing result is Status 0 → the fold's default branch → no write; unconditional is safe.)
            SpotifyVideoService.Fold(_store, trackUri, video, DateTimeOffset.UtcNow);
            CollectTargets(video.Payload, TrackVersionKind.Video, targets);
            CollectTargets(audio.Payload, TrackVersionKind.Audio, targets);

            results.TryGetValue((trackUri, Xm.ExtensionKind.AudioFiles), out var files);
            var formats = MapFormats(files.Payload);

            results.TryGetValue((trackUri, Xm.ExtensionKind.MixThreeBandWaveforms), out var wave);
            var waveform = MapWaveform(wave.Payload);

            var versions = targets.Count == 0
                ? (IReadOnlyList<TrackVersion>)Array.Empty<TrackVersion>()
                : await ResolveAsync(targets, ct).ConfigureAwait(false);

            return new TrackExpansion(versions, formats, waveform);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Do not cache a cancellation — the next open must retry rather than inherit an empty drawer.
            _cache.TryRemove(trackUri, out _);
            throw;
        }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "expansion.fail", "track expansion failed", trackUri, ex: ex);
            _cache.TryRemove(trackUri, out _);
            return TrackExpansion.Empty;
        }
    }

    /// <summary>Both association kinds share ONE message shape (<c>associations[].target_uri</c> + artwork); only the
    /// image aspect differs (16:9 video stills vs square covers). So one collector serves both, and the KIND comes
    /// from which extension carried it.</summary>
    void CollectTargets(ByteString? payload, TrackVersionKind kind, List<(string, TrackVersionKind)> into)
    {
        if (payload is null || payload.IsEmpty) return;
        try
        {
            var assoc = AssocParser.ParseFrom(payload);
            // The existing proto models field 1 as a SINGLE Association (matching the wire: one counterpart per
            // extension), with the quality variants nested under it. `associated_uri` is that counterpart.
            if (assoc.Association?.AssociatedUri is { Length: > 0 } uri
                && uri.StartsWith("spotify:track:", StringComparison.Ordinal))
                into.Add((uri, kind));
        }
        catch (InvalidProtocolBufferException ex)
        {
            // One malformed association must not lose the other plane's versions — but a silent drop here looked
            // identical to "this track has no versions", which is the one thing the drawer must not lie about.
            _log.Event(WaveeLogLevel.Warning, "expansion.assoc.parse", "video-associations parse failed", ex: ex);
        }
    }

    /// <summary>Columns the waveform is reduced to. Wide enough to read as a shape at any drawer width, small enough
    /// that the whole thing is a few hundred floats instead of ~37 000 bytes held per expanded track.</summary>
    const int WaveformColumns = 220;

    /// <summary>Kind 237 → one 0..1 magnitude per column.
    ///
    /// Reduced HERE, once, rather than in the renderer: the payload is three ~12 KB arrays at 50 Hz and the drawer
    /// draws a strip a few hundred pixels wide, so keeping the raw bytes around would be ~37 KB per expanded row for
    /// resolution nothing can show.
    ///
    /// Each band is walked across its OWN length. The wire ships band_low LONGER than the other two (a confirmed
    /// oddity — 12886 vs 12466 on the reference track), so indexing all three off one cursor drifts ~8 s by the end of
    /// the track. Mapping each band by fraction-of-itself keeps them aligned in TIME.</summary>
    TrackWaveform? MapWaveform(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return null;
        try
        {
            var w = WaveformParser.ParseFrom(payload);
            ReadOnlySpan<byte> low = w.BandLow.Span, mid = w.BandMid.Span, high = w.BandHigh.Span;
            if (low.IsEmpty && mid.IsEmpty && high.IsEmpty) return null;

            var peaks = new float[WaveformColumns];
            float peak = 0f;
            for (int i = 0; i < WaveformColumns; i++)
            {
                // The column's span as a FRACTION of the track, resolved per band against that band's own length.
                float a = (float)i / WaveformColumns, b = (float)(i + 1) / WaveformColumns;
                float sum = BandMax(low, a, b) + BandMax(mid, a, b) + BandMax(high, a, b);
                peaks[i] = sum;
                if (sum > peak) peak = sum;
            }
            if (peak <= 0f) return null;
            for (int i = 0; i < peaks.Length; i++) peaks[i] /= peak;   // normalise to the track's own loudest column
            return new TrackWaveform(peaks);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _log.Event(WaveeLogLevel.Debug, "expansion.waveform.parse", "waveform parse failed", ex: ex);
            return null;
        }
    }

    /// <summary>The loudest sample in [<paramref name="a"/>, <paramref name="b"/>) of a band, as fractions of its own
    /// length. MAX, not mean: averaging flattens transients and every track ends up the same soft blob.</summary>
    static float BandMax(ReadOnlySpan<byte> band, float a, float b)
    {
        if (band.IsEmpty) return 0f;
        int from = (int)(a * band.Length);
        int to = Math.Max(from + 1, Math.Min(band.Length, (int)(b * band.Length)));
        byte max = 0;
        for (int i = from; i < to; i++) if (band[i] > max) max = band[i];
        return max;
    }

    /// <summary>Resolve association targets to real tracks (name + duration) plus their tempo/key, so the drawer can
    /// show what actually differs between versions rather than a list of ids.</summary>
    async Task<IReadOnlyList<TrackVersion>> ResolveAsync(List<(string Uri, TrackVersionKind Kind)> targets, CancellationToken ct)
    {
        var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(targets.Count * 2);
        foreach (var (uri, _) in targets)
        {
            reqs.Add((uri, Xm.ExtensionKind.TrackV4, null));
            reqs.Add((uri, Xm.ExtensionKind.AudioAttributesV2, null));
            if (reqs.Count >= ResolveCap * 2) break;
        }

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> resolved;
        try { resolved = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "expansion.resolve.fail", "version title resolve failed", ex: ex);
            // Fall back to id-titled entries rather than dropping the versions entirely — the user still learns the
            // track HAS alternates and can open them.
            return Fallback(targets);
        }

        var list = new List<TrackVersion>(targets.Count);
        foreach (var (uri, kind) in targets)
        {
            string title = IdOf(uri);
            long duration = 0;
            Image? art = null;

            if (resolved.TryGetValue((uri, Xm.ExtensionKind.TrackV4), out var tv) && tv.Payload is { IsEmpty: false } tp)
            {
                try
                {
                    var track = TrackParser.ParseFrom(tp);
                    if (track.Name is { Length: > 0 }) title = track.Name;
                    if (track.Duration > 0) duration = track.Duration;
                    // The cover was here all along. A music video is an entity the playlist never contained, so the
                    // store lookup below always misses and the thumbnail rendered as an empty tile — but the SAME
                    // TrackV4 payload we already parse for the title carries album.cover_group. Kinds 98/99 only ever
                    // ship DASH file ids, so this parse is the only place a video's art can come from.
                    art ??= ExtendedMetadataSource.PickImage(track.Album?.CoverGroup);
                }
                catch (InvalidProtocolBufferException ex)
                {
                    // Keep the id as the title, but say so: a drawer full of raw ids is otherwise unexplainable.
                    _log.Event(WaveeLogLevel.Debug, "expansion.title.parse", "version title parse failed", ex: ex);
                }
            }

            // Prefer whatever the store already knows (the row bundle may have adorned it) over a second parse.
            var stored = _store.GetTrack(uri);
            art ??= stored?.Image;

            list.Add(new TrackVersion(uri, kind, title, art, duration,
                TempoBpm: stored?.TempoBpm, MusicalKey: stored?.MusicalKey,
                CamelotCode: stored?.CamelotCode, CamelotColor: stored?.CamelotColor));
        }
        return list;
    }

    static IReadOnlyList<TrackVersion> Fallback(List<(string Uri, TrackVersionKind Kind)> targets)
    {
        var list = new List<TrackVersion>(targets.Count);
        foreach (var (uri, kind) in targets) list.Add(new TrackVersion(uri, kind, IdOf(uri), null));
        return list;
    }

    static string IdOf(string uri)
    {
        int i = uri.LastIndexOf(':');
        return i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
    }

    /// <summary>The formats this track actually has, best first. Ordered by bitrate DESC so the quality ladder reads
    /// top-down; a format with no bitrate sorts last rather than pretending to be lossless.</summary>
    IReadOnlyList<AudioFormatOption> MapFormats(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return Array.Empty<AudioFormatOption>();
        try
        {
            var response = AudioFilesParser.ParseFrom(payload);
            var list = new List<AudioFormatOption>(response.Files.Count);
            foreach (var f in response.Files)
            {
                if (f.File is null) continue;
                int id = (int)f.File.Format;
                list.Add(new AudioFormatOption(id, FormatLabel(f.File.Format), f.AverageBitrate));
            }
            list.Sort(static (a, b) => b.AverageBitrate.CompareTo(a.AverageBitrate));
            return list;
        }
        catch (InvalidProtocolBufferException ex)
        {
            _log.Event(WaveeLogLevel.Warning, "expansion.formats.parse", "audio-format parse failed", ex: ex);
            return Array.Empty<AudioFormatOption>();
        }
    }

    /// <summary>A short human label for a Spotify audio format. Unknown values render their raw enum id rather than
    /// being hidden — a format we cannot name is still one the user can select, and hiding it would silently shrink
    /// the ladder.</summary>
    internal static string FormatLabel(Md.AudioFile.Types.Format format) => format switch
    {
        Md.AudioFile.Types.Format.OggVorbis96 => "OGG 96",
        Md.AudioFile.Types.Format.OggVorbis160 => "OGG 160",
        Md.AudioFile.Types.Format.OggVorbis320 => "OGG 320",
        Md.AudioFile.Types.Format.Mp396 => "MP3 96",
        Md.AudioFile.Types.Format.Mp3160 => "MP3 160",
        Md.AudioFile.Types.Format.Mp3256 => "MP3 256",
        Md.AudioFile.Types.Format.Mp3320 => "MP3 320",
        Md.AudioFile.Types.Format.Aac24 => "AAC 24",
        Md.AudioFile.Types.Format.Aac48 => "AAC 48",
        Md.AudioFile.Types.Format.FlacFlac => "FLAC",
        _ => "Format " + ((int)format).ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Switchable expansion seam — the drawer holds this for the session; go-live/GoOffline swap the inner
/// implementation so a page never caches a stale service across a login change.</summary>
public sealed class SwitchableTrackExpansionService : ITrackExpansionService
{
    volatile ITrackExpansionService _inner = NullTrackExpansionService.Instance;

    public void SetInner(ITrackExpansionService inner) => _inner = inner ?? NullTrackExpansionService.Instance;
    public void Reset() => _inner = NullTrackExpansionService.Instance;

    public Task<TrackExpansion> GetAsync(string trackUri, CancellationToken ct = default) => _inner.GetAsync(trackUri, ct);
    public void SetFormatOverride(string uri, int? formatId) => _inner.SetFormatOverride(uri, formatId);
    public int? FormatOverrideFor(string uri) => _inner.FormatOverrideFor(uri);
}
