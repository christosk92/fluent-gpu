using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.Pal;

namespace FluentGpu.Media.Windows;

/// <summary>
/// A live Media-Foundation video session (spec §9.1): drives an <see cref="IVideoEngine"/> (the PROVEN
/// <see cref="VideoMediaEngine"/> in production) and maps its worker-thread event state onto the player's
/// <see cref="MediaSignalSink"/>. State translation, transport, position projection and the composited-surface handoff all
/// run on the UI/pump thread (<see cref="PumpVideo"/>) so the sink's sole-writer contract holds; every ComPtr stays behind
/// the engine's MTA thread.
/// <list type="bullet">
/// <item>Transport (<see cref="PlayAsync"/>/<see cref="PauseAsync"/>/<see cref="SeekAsync"/>/rate/volume/mute) is
///   idempotent and accepted SYNCHRONOUSLY — it records intent + forwards to the engine and returns a completed task; it
///   never throws or blocks. The next <see cref="PumpVideo"/> realizes the resulting state transition.</item>
/// <item>Position is projected from the engine's presentation clock (authoritative), pushed each pump.</item>
/// <item><see cref="Video"/> is <see cref="VideoDelivery.None"/> until the swap-chain handle + natural size exist, then a
///   <see cref="VideoDelivery.CompositedSurface"/> — the shipping Path A (spec §9.1).</item>
/// </list>
/// </summary>
public sealed class MfMediaSession : IMediaSession, IVideoSurfaceSession
{
    private readonly IVideoEngine _engine;
    private readonly MediaOpenOptions _opts;
    private readonly AdaptiveManifest? _manifest;

    private MediaSignalSink? _sink;
    private bool _disposed;

    // Intent (UI thread).
    private bool _playRequested;
    private bool _everPlayed;
    private double _rate = 1.0;
    private double _volume = 1.0;
    private bool _muted;

    // Realized/published state (UI thread, via the pump).
    private bool _metaReady;
    private SizeI _naturalSize = SizeI.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private nuint _handle;
    private int _streamW, _streamH;
    private PlaybackState _publishedState = PlaybackState.Opening;
    private bool _errorPublished;
    private bool _seeking;

    // In-band (manifest-declared) subtitle rendering: track-id → its adaptation, the loaded cue timeline, and the
    // last published cue. The cue timeline is built OFF-thread and swapped in as a whole (reference write); the pump
    // (UI thread) only ever reads a fully-built CueTrack.
    private static readonly HttpClient s_subtitleHttp = new();
    private readonly Dictionary<int, AdaptiveTrackGroup> _textGroups = new();
    private volatile CueTrack? _inbandCues;
    private int _cueEpoch;
    private TimedCue? _publishedCue;

    internal MfMediaSession(IVideoEngine engine, MediaOpenOptions opts, AdaptiveManifest? manifest = null)
    {
        _engine = engine;
        _opts = opts;
        _manifest = manifest;
        _playRequested = !opts.StartPaused;
        if (_playRequested) _everPlayed = true;
    }

    /// <inheritdoc/>
    public void ConnectSignals(MediaSignalSink sink)
    {
        _sink = sink;
        // Re-apply cold state the engine already accepted, then announce we are opening (metadata pending).
        _engine.SetPlaybackRate(_rate);
        _engine.SetVolume(_volume);
        _engine.SetMuted(_muted);
        sink.PlayRequested(_playRequested);
        sink.State(PlaybackState.Opening);
        _publishedState = PlaybackState.Opening;
        PublishManifestCatalog(sink);
        // If StartPosition was requested, seek before the first frame (applied once the source resolves too).
        if (_opts.StartPosition > TimeSpan.Zero) _engine.SeekTo(_opts.StartPosition.TotalSeconds);
    }

    /// <inheritdoc/>
    public VideoDelivery Video =>
        _handle != 0 && !_naturalSize.IsEmpty
            ? new VideoDelivery.CompositedSurface(new VideoSurfaceId(1), _naturalSize, IsHdr: false)
            : VideoDelivery.None;

    // ── transport (idempotent; accepted synchronously; the pump realizes state) ──────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask PlayAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = true;
        _everPlayed = true;
        _engine.Play();
        _sink?.PlayRequested(true);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask PauseAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _playRequested = false;
        _engine.Pause();
        _sink?.PlayRequested(false);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask SeekAsync(TimeSpan to, SeekMode mode)
    {
        if (_disposed) return ValueTask.CompletedTask;
        double hi = _duration > TimeSpan.Zero ? _duration.TotalSeconds : double.MaxValue;
        double t = Math.Clamp(to.TotalSeconds, 0.0, hi);
        _seeking = true;
        _sink?.Buffering(new BufferingInfo(BufferingReason.Seeking, -1, TimeSpan.Zero,
            (_opts.Buffering ?? BufferPolicy.Vod).ResumePlayback, false));
        _engine.SeekTo(t);
        // Reflect the seek target immediately so a bound seekbar doesn't snap back for a frame.
        _sink?.Position(TimeSpan.FromSeconds(t));
        _sink?.SettleTransport();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void SetRate(double rate) { if (_disposed) return; _rate = rate; _engine.SetPlaybackRate(rate); }
    /// <inheritdoc/>
    public void SetVolume(double volume) { if (_disposed) return; _volume = Math.Clamp(volume, 0, 1); _engine.SetVolume(_volume); }
    /// <inheritdoc/>
    public void SetMuted(bool muted) { if (_disposed) return; _muted = muted; _engine.SetMuted(muted); _sink?.Muted(muted); }

    /// <summary>Realize a TEXT selection by loading the manifest-declared rendition's WebVTT into a cue timeline the
    /// pump renders (in-band captions). Returns immediately; the fetch runs in the background and is superseded by the
    /// next selection (epoch). Audio/video selection is MF-engine-internal for natively-played adaptive URLs — a no-op
    /// here so the catalog selection still updates.</summary>
    public ValueTask SelectTrackAsync(MediaTrack? track)
    {
        if (_disposed) return ValueTask.CompletedTask;
        if (track is null || track.Kind == TrackKind.Text)
        {
            int epoch = ++_cueEpoch;
            _inbandCues = null;
            if (track is not null && _textGroups.TryGetValue(track.Id, out AdaptiveTrackGroup? group))
                _ = LoadInbandCuesAsync(group, epoch);
        }
        return ValueTask.CompletedTask;
    }

    private async Task LoadInbandCuesAsync(AdaptiveTrackGroup group, int epoch)
    {
        try
        {
            // A live rendition is an ever-sliding window — whole-track prefetch does not apply (deferred).
            if (_manifest is not { } manifest || manifest.IsLive || group.Representations.Count == 0) return;
            AdaptiveRepresentation rep = group.Representations[0];
            IReadOnlyList<AdaptiveSegment> segments = rep.Segments;
            if (segments.Count == 0 && rep.PlaylistUri is { } playlist)
            {
                // HLS: the master only names the rendition; its media playlist carries the segment URIs.
                var mediaUri = new Uri(playlist);
                string text = await s_subtitleHttp.GetStringAsync(mediaUri).ConfigureAwait(false);
                AdaptiveManifest media = HlsManifestParser.ParseMedia(text, mediaUri, rep.Quality);
                if (media.TrackGroups.Count > 0 && media.TrackGroups[0].Representations.Count > 0)
                    segments = media.TrackGroups[0].Representations[0].Segments;
            }
            if (segments.Count == 0) return;

            var merged = new List<TimedCue>();
            var seen = new HashSet<(long, string)>();   // segments repeat boundary-spanning cues — dedupe on (start, text)
            int cap = Math.Min(segments.Count, 512);    // bound the fetch storm on very long presentations
            for (int i = 0; i < cap; i++)
            {
                if (epoch != _cueEpoch || _disposed) return;   // selection superseded / torn down
                string vtt;
                try { vtt = await s_subtitleHttp.GetStringAsync(segments[i].Uri).ConfigureAwait(false); }
                catch (HttpRequestException) { continue; }      // one missing caption segment is not fatal
                if (i == 0 && !vtt.AsSpan().TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal))
                    return;                                     // not WebVTT (e.g. TTML) — unsupported for now
                CueTrack part = SubtitleLoader.ParseWebVtt(vtt);
                bool added = false;
                for (int c = 0; c < part.Count; c++)
                {
                    TimedCue cue = part[c];
                    if (seen.Add((cue.Start.Ticks, cue.Text))) { merged.Add(cue); added = true; }
                }
                // Progressive availability: swap in a freshly-built snapshot (the pump only ever sees a complete,
                // immutable-from-its-view CueTrack — never a list another thread is still mutating).
                if (added && (i % 8 == 7 || i == cap - 1) && epoch == _cueEpoch)
                    _inbandCues = Snapshot(merged);
            }
            if (epoch == _cueEpoch && merged.Count > 0) _inbandCues = Snapshot(merged);
        }
        catch
        {
            // Cue loading is best-effort presentation sugar — a failure must never disturb playback.
        }

        static CueTrack Snapshot(List<TimedCue> cues)
        {
            var track = new CueTrack();
            for (int i = 0; i < cues.Count; i++) track.Add(cues[i]);
            return track;
        }
    }

    // ── the UI-thread pump (state mapping + the composited-surface handoff) ───────────────────────────────────────────

    /// <inheritdoc/>
    public void PumpVideo(VideoBinding binding, RectF videoRect, float scale)
    {
        if (_disposed || _sink is null) return;
        var sink = _sink;

        // 1. Terminal error — map the MF media-engine error code to a typed MediaError (published once). Never a silent drop.
        if (_engine.HasError)
        {
            if (!_errorPublished)
            {
                _errorPublished = true;
                sink.Error(MapError(_engine.ErrorCode, _engine.ErrorHr));
                Publish(sink, PlaybackState.Failed);
            }
            return;
        }

        // 2. First metadata → publish natural size / duration / commands and become Ready (or Playing on intent).
        if (_engine.MetadataLoaded && !_metaReady)
        {
            _metaReady = true;
            if (_engine.TryGetNativeVideoSize(out uint cx, out uint cy) && cx > 0 && cy > 0)
                _naturalSize = new SizeI((int)cx, (int)cy);
            sink.NaturalSize(_naturalSize);

            double dur = _engine.DurationSeconds;
            _duration = dur > 0 ? TimeSpan.FromSeconds(dur) : TimeSpan.Zero;
            sink.Duration(_duration);

            sink.Commands(_naturalSize.IsEmpty
                ? MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate
                : MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate | MediaCommandFlags.StepFrame);

            if (_manifest is { } catalog)
            {
                MediaCommandFlags adaptive = MediaCommandFlags.SelectVideoQuality;
                for (int g = 0; g < catalog.TrackGroups.Count; g++)
                    adaptive |= catalog.TrackGroups[g].Type switch
                    {
                        AdaptiveTrackType.Audio => MediaCommandFlags.SelectAudioTrack,
                        AdaptiveTrackType.Text => MediaCommandFlags.SelectTextTrack,
                        _ => MediaCommandFlags.None,
                    };
                if (catalog.IsLive) adaptive |= MediaCommandFlags.GoLive;
                sink.Commands(sinkCoreCommands(_naturalSize) | adaptive);
            }

            // Honor the accepted play/pause intent now that the source has resolved.
            if (_playRequested) _engine.Play(); else _engine.Pause();
        }

        // 3. Composited-surface handoff (Path A) — the single (DRM-free here) bind point. Value-gated all the way down.
        if (binding.IsValid && _metaReady)
        {
            if (_handle == 0) _handle = _engine.GetSwapchainHandle();
            if (_handle != 0)
            {
                binding.Bind(_handle);

                int dw = Math.Max(1, (int)MathF.Round(videoRect.W * (scale <= 0 ? 1f : scale)));
                int dh = Math.Max(1, (int)MathF.Round(videoRect.H * (scale <= 0 ? 1f : scale)));
                if (dw != _streamW || dh != _streamH)
                {
                    _engine.SetVideoStreamRect(dw, dh);   // swap-chain-local dst; the presenter clips (does not scale)
                    _streamW = dw; _streamH = dh;
                }
                binding.SetContentSize(new SizeI(dw, dh));
                binding.Place(new RectF(videoRect.X, videoRect.Y, videoRect.W, videoRect.H));
                binding.SetVisible(true);
                _engine.RepaintCurrentFrame();
            }
        }

        // 4. State + position from the engine (the presentation clock is authoritative for position).
        PlaybackState state = DeriveState();
        Publish(sink, state);
        PublishBuffering(sink, state);
        if (_metaReady)
        {
            TimeSpan pos = TimeSpan.FromSeconds(_engine.CurrentTimeSeconds);
            sink.Position(pos);
            TimedCue? cue = null;
            if (_inbandCues is { } cues) { cues.Advance(pos); cue = cues.ActiveCue.Peek(); }
            if (!EqualityComparer<TimedCue?>.Default.Equals(cue, _publishedCue))
            {
                _publishedCue = cue;
                sink.ActiveCue(cue);
            }
        }
        if (_manifest is { IsLive: true }) PublishLiveTimeline(sink, TimeSpan.FromSeconds(_engine.CurrentTimeSeconds));
    }

    private static MediaCommandFlags sinkCoreCommands(SizeI size)
        => size.IsEmpty
            ? MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate
            : MediaCommandFlags.Play | MediaCommandFlags.Pause | MediaCommandFlags.Seek | MediaCommandFlags.Rate | MediaCommandFlags.StepFrame;

    private void PublishManifestCatalog(MediaSignalSink sink)
    {
        if (_manifest is not { } manifest) return;
        sink.ResetTracks();
        var qualities = new System.Collections.Generic.List<QualityVariant>();
        int id = 1;
        for (int g = 0; g < manifest.TrackGroups.Count; g++)
        {
            AdaptiveTrackGroup group = manifest.TrackGroups[g];
            if (group.Representations.Count == 0) continue;
            TrackKind kind = group.Type switch
            {
                AdaptiveTrackType.Audio => TrackKind.Audio,
                AdaptiveTrackType.Text => TrackKind.Text,
                _ => TrackKind.Video,
            };
            // Captions default OFF (WinUI/web behavior): only a FORCED text track auto-selects. Audio/video keep the
            // manifest default / first-of-kind rule.
            bool selected = kind == TrackKind.Text
                ? group.IsForced
                : group.IsDefault || !HasSelectedKind(manifest, g, group.Type);
            sink.Track(id, kind, group.Language, LabelOf(group), group.Role,
                group.Representations[0].Quality.Codec, selected);
            if (kind == TrackKind.Text) _textGroups[id] = group;
            id++;
            if (group.Type == AdaptiveTrackType.Video)
                for (int r = 0; r < group.Representations.Count; r++) qualities.Add(group.Representations[r].Quality);
        }
        sink.QualityVariants(qualities);
        sink.QualitySelection(QualitySelection.Auto, qualities.Count > 0 ? qualities[0] : null);
        if (qualities.Count > 0)
        {
            QualityVariant first = qualities[0];
            if (!first.Resolution.IsEmpty)
                sink.VideoGeometry(new VideoGeometry(first.Resolution,
                    new PixelRect(0, 0, first.Resolution.Width, first.Resolution.Height),
                    PixelAspectRatio.Square, 0, first.Resolution));
            sink.VideoColor(first.Hdr switch
            {
                HdrFormat.Hdr10 => new VideoColorInfo(VideoColorPrimaries.Bt2020, VideoTransfer.Pq,
                    VideoMatrix.Bt2020NonConstant, VideoRange.Limited, HdrFormat.Hdr10, null),
                HdrFormat.Hlg => new VideoColorInfo(VideoColorPrimaries.Bt2020, VideoTransfer.Hlg,
                    VideoMatrix.Bt2020NonConstant, VideoRange.Limited, HdrFormat.Hlg, null),
                _ => VideoColorInfo.Sdr,
            });
        }
        PublishLiveTimeline(sink, TimeSpan.Zero);
    }

    private static bool HasSelectedKind(AdaptiveManifest manifest, int before, AdaptiveTrackType type)
    {
        for (int i = 0; i < before; i++)
            if (manifest.TrackGroups[i].Type == type && manifest.TrackGroups[i].Representations.Count > 0) return true;
        return false;
    }

    private static string LabelOf(AdaptiveTrackGroup group)
        => string.IsNullOrWhiteSpace(group.Language) ? group.Id : $"{group.Language} · {group.Id}";

    private void PublishLiveTimeline(MediaSignalSink sink, TimeSpan position)
    {
        if (_manifest is not { } manifest) return;
        TimeSpan start = TimeSpan.MaxValue, end = TimeSpan.Zero;
        for (int g = 0; g < manifest.TrackGroups.Count; g++)
        {
            var reps = manifest.TrackGroups[g].Representations;
            if (reps.Count == 0) continue;
            var segments = reps[0].Segments;
            for (int s = 0; s < segments.Count; s++)
            {
                if (segments[s].Start < start) start = segments[s].Start;
                TimeSpan segmentEnd = segments[s].Start + segments[s].Duration;
                if (segmentEnd > end) end = segmentEnd;
            }
        }
        if (start == TimeSpan.MaxValue) start = TimeSpan.Zero;
        if (!manifest.IsLive && manifest.Duration is { } duration) end = duration;
        TimeSpan liveOffset = manifest.IsLive && end > position ? end - position : TimeSpan.Zero;
        TimeSpan tolerance = manifest.IsLowLatency ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(6);
        sink.Timeline(new TimelineInfo(manifest.IsLive, start, end, end, liveOffset,
            manifest.IsLive && liveOffset <= tolerance, Array.Empty<MediaChapter>()));
    }

    private void PublishBuffering(MediaSignalSink sink, PlaybackState state)
    {
        bool buffering = state is PlaybackState.Opening or PlaybackState.Buffering or PlaybackState.Stalled;
        if (!buffering)
        {
            _seeking = false;
            sink.Buffering(BufferingInfo.None);
            return;
        }
        BufferPolicy policy = _opts.Buffering ?? BufferPolicy.Vod;
        BufferingReason reason = _seeking || _engine.Seeking ? BufferingReason.Seeking
            : _metaReady ? BufferingReason.Rebuffering : BufferingReason.Initial;
        uint ready = Math.Min(_engine.ReadyState, 4u);
        double percent = ready / 4.0;
        TimeSpan target = reason == BufferingReason.Initial ? policy.InitialPlayback : policy.ResumePlayback;
        sink.Buffering(new BufferingInfo(reason, percent, TimeSpan.FromTicks((long)(target.Ticks * percent)), target,
            ready >= 3));
    }

    private PlaybackState DeriveState()
    {
        if (_engine.HasError) return PlaybackState.Failed;
        if (!_metaReady) return PlaybackState.Opening;
        if (_engine.Ended) return PlaybackState.Ended;
        // A seek in flight is surfaced as Buffering(Seeking) — MF keeps `Playing` true across a seek, so without this
        // the transport halts with zero feedback until SEEKED lands.
        if (_engine.Seeking) return PlaybackState.Buffering;
        if (_engine.Playing) return PlaybackState.Playing;
        if (_playRequested) return PlaybackState.Buffering;   // intent to play, engine not yet advancing (re-buffering)
        return _everPlayed ? PlaybackState.Paused : PlaybackState.Ready;
    }

    private void Publish(MediaSignalSink sink, PlaybackState state)
    {
        if (state == _publishedState) return;
        _publishedState = state;
        sink.State(state);
    }

    /// <summary>Map an <c>MF_MEDIA_ENGINE_ERR</c> code (+ the raw HRESULT) to a typed <see cref="MediaError"/> — a DRM
    /// shortfall (ENCRYPTED) is a Drm error with <see cref="MediaRecovery.NeedsLicense"/>, never a quiet black frame.</summary>
    internal static MediaError MapError(uint mfErr, int hr) => mfErr switch
    {
        // MF_MEDIA_ENGINE_ERR: 1 ABORTED, 2 NETWORK, 3 DECODE, 4 SRC_NOT_SUPPORTED, 5 ENCRYPTED.
        2 => new MediaError(MediaErrorCategory.Network, "The media download failed.", hr, null, MediaRecovery.NeedsNetwork),
        3 => new MediaError(MediaErrorCategory.Decode, "The media could not be decoded.", hr, null, MediaRecovery.Retryable),
        4 => new MediaError(MediaErrorCategory.UnsupportedCodec, "The media format is not supported.", hr, null, MediaRecovery.PickLowerQuality),
        5 => new MediaError(MediaErrorCategory.Drm, "The media is encrypted and no license is available.", hr, null, MediaRecovery.NeedsLicense),
        1 => new MediaError(MediaErrorCategory.Source, "Media loading was aborted.", hr, null, MediaRecovery.Retryable),
        _ => new MediaError(MediaErrorCategory.Source, "The media source failed.", hr, null, MediaRecovery.Retryable),
    };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _sink = null;
        var engine = _engine;
        // The engine tears down its MTA thread + COM on ITS thread (a blocking join); do it off the UI thread.
        await Task.Run(engine.Dispose).ConfigureAwait(false);
    }
}
