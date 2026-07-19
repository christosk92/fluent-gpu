using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Media;

/// <summary>
/// The immutable source description (spec §5). One record, many factories; the HARD PARTS (read-coalescing, buffering,
/// eviction, blocking-bridge, thread-firewall, seek emulation) live in the engine, not the app. Sources compose through
/// a small algebra (<see cref="Concat"/>/<see cref="Clip"/>/<see cref="Loop"/>/<see cref="Merge"/>/<see cref="Silence"/>)
/// and carry per-source overrides (<see cref="With(NetworkOptions)"/>/<see cref="With(DrmConfig)"/>/<see cref="WithMetadata"/>/
/// <see cref="WithExternalSubtitle"/>/<see cref="WithKind"/>). Every factory yields ONE player code path.
/// </summary>
public abstract record MediaSource
{
    /// <summary>The routing hint (<see cref="MediaKind.Auto"/> = the router sniffs).</summary>
    public MediaKind Kind { get; init; } = MediaKind.Auto;

    /// <summary>Per-source network options (auth/timeout), or null for the player default.</summary>
    public NetworkOptions? Network { get; init; }

    /// <summary>Per-source DRM config, or null for none.</summary>
    public DrmConfig? Drm { get; init; }

    /// <summary>Seeded now-playing metadata (avoids a round-trip), or null.</summary>
    public MediaMetadata? Metadata { get; init; }

    /// <summary>External subtitle/caption tracks attached to this source (rendered by the engine text stack).</summary>
    public IReadOnlyList<SubtitleSource> ExternalSubtitles { get; init; } = Array.Empty<SubtitleSource>();

    // ── factories: all yield ONE player code path ────────────────────────────────────────────────────────────────────

    /// <summary>A local file.</summary>
    public static MediaSource FromFile(string path) => new FileSource(path);
    /// <summary>A URL (optionally with per-source network options).</summary>
    public static MediaSource FromUri(string url, NetworkOptions? net = null) => new UriSource(url) { Network = net };
    /// <summary>An existing <see cref="Stream"/> (the easy case, verbatim); <paramref name="hint"/> seeds the content type.</summary>
    public static MediaSource FromStream(Stream stream, MediaContentType? hint = null) => new StreamSource(stream, hint);
    /// <summary>An in-memory byte blob; <paramref name="hint"/> seeds the content type.</summary>
    public static MediaSource FromBytes(ReadOnlyMemory<byte> bytes, MediaContentType? hint = null) => new BytesSource(bytes, hint);
    /// <summary>A random-access byte seam (spec §5.1) — PlayPlay's front door (wrap in <see cref="DecryptingSource"/>).</summary>
    public static MediaSource FromPull(IMediaByteSource source) => new PullSource(source);
    /// <summary>An MSE-style push/append feed (spec §5.2).</summary>
    public static MediaSource FromFeed(IMediaFeed feed) => new FeedSource(feed);
    /// <summary>An already-demuxed encoded-sample source (spec §5.3) — the blessed FFmpeg-class path.</summary>
    public static MediaSource FromSamples(IMediaSampleSource source) => new SampleSource(source);

    /// <summary>A lazily-resolved source (STEAL: <c>MediaBinder</c>) — resolves to a concrete source on open.</summary>
    public static MediaSource Deferred(Func<CancellationToken, ValueTask<MediaSource>> bind) => new DeferredSource(bind);

    // ── composable source algebra (STEAL: ExoPlayer decorator tree) ──────────────────────────────────────────────────

    /// <summary>Play <paramref name="parts"/> back-to-back as one logical source.</summary>
    public static MediaSource Concat(params MediaSource[] parts) => new ConcatSource(CopyOf(parts));
    /// <summary>Clip this source to <c>[start, end)</c>.</summary>
    public MediaSource Clip(TimeSpan start, TimeSpan end) => new ClipSource(this, start, end) { Kind = Kind };
    /// <summary>Loop this source <paramref name="count"/> times (<c>-1</c> = infinite).</summary>
    public MediaSource Loop(int count = -1) => new LoopSource(this, count) { Kind = Kind };
    /// <summary>Merge parallel tracks (e.g. video + external audio) into one timeline.</summary>
    public static MediaSource Merge(params MediaSource[] tracks) => new MergeSource(CopyOf(tracks));
    /// <summary>Append <paramref name="duration"/> of silence after this source.</summary>
    public MediaSource Silence(TimeSpan duration) => new SilenceSource(this, duration) { Kind = Kind };

    // ── per-source overrides (player default → per-source, ExoPlayer DI layering) ─────────────────────────────────────

    /// <summary>Override the network options for this source.</summary>
    public MediaSource With(NetworkOptions net) => this with { Network = net };
    /// <summary>Attach DRM config to this source.</summary>
    public MediaSource With(DrmConfig drm) => this with { Drm = drm };
    /// <summary>Seed now-playing metadata (no round-trip).</summary>
    public MediaSource WithMetadata(MediaMetadata meta) => this with { Metadata = meta };
    /// <summary>Attach an external subtitle track (rendered by the engine's GPU text stack).</summary>
    public MediaSource WithExternalSubtitle(SubtitleSource sub) => this with { ExternalSubtitles = Append(ExternalSubtitles, sub) };
    /// <summary>Force the routing kind (e.g. PlayPlay → <see cref="MediaKind.PcmAudio"/>).</summary>
    public MediaSource WithKind(MediaKind kind) => this with { Kind = kind };

    private static IReadOnlyList<SubtitleSource> Append(IReadOnlyList<SubtitleSource> existing, SubtitleSource add)
    {
        var next = new SubtitleSource[existing.Count + 1];
        for (int i = 0; i < existing.Count; i++) next[i] = existing[i];
        next[^1] = add;
        return next;
    }

    private static MediaSource[] CopyOf(MediaSource[] parts)
    {
        var copy = new MediaSource[parts.Length];
        Array.Copy(parts, copy, parts.Length);
        return copy;
    }
}

// ── Concrete leaf/composite sources (records — value equality, immutable) ────────────────────────────────────────────

/// <summary>A local-file source.</summary>
public sealed record FileSource(string Path) : MediaSource;
/// <summary>A URL source.</summary>
public sealed record UriSource(string Url) : MediaSource;
/// <summary>A <see cref="Stream"/> source (the easy case).</summary>
public sealed record StreamSource(Stream Stream, MediaContentType? Hint) : MediaSource;
/// <summary>An in-memory bytes source.</summary>
public sealed record BytesSource(ReadOnlyMemory<byte> Bytes, MediaContentType? Hint) : MediaSource;
/// <summary>A random-access pull-byte source (spec §5.1).</summary>
public sealed record PullSource(IMediaByteSource Source) : MediaSource;
/// <summary>A push/append feed source (spec §5.2).</summary>
public sealed record FeedSource(IMediaFeed Feed) : MediaSource;
/// <summary>An already-demuxed encoded-sample source (spec §5.3).</summary>
public sealed record SampleSource(IMediaSampleSource Source) : MediaSource;
/// <summary>A lazily-resolved source.</summary>
public sealed record DeferredSource(Func<CancellationToken, ValueTask<MediaSource>> Bind) : MediaSource;
/// <summary>Concatenation of parts.</summary>
public sealed record ConcatSource(IReadOnlyList<MediaSource> Parts) : MediaSource;
/// <summary>A clipped view of an inner source.</summary>
public sealed record ClipSource(MediaSource Inner, TimeSpan Start, TimeSpan End) : MediaSource;
/// <summary>A looped inner source (<c>Count == -1</c> = infinite).</summary>
public sealed record LoopSource(MediaSource Inner, int Count) : MediaSource;
/// <summary>Parallel merge of tracks (video + external audio, etc.).</summary>
public sealed record MergeSource(IReadOnlyList<MediaSource> Tracks) : MediaSource;
/// <summary>An inner source with trailing silence.</summary>
public sealed record SilenceSource(MediaSource Inner, TimeSpan Duration) : MediaSource;
