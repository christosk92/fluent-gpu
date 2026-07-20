using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

enum MediaScenarioKind : byte { Progressive, Dash, Hls, PlayReadyDash, PlayReadyHls, Negative }

[Flags]
enum MediaScenarioFeature : uint
{
    None = 0, Transport = 1, Seek = 2, Buffering = 4, Aspect = 8, Abr = 16, Live = 32,
    LowLatency = 64, MultiAudio = 128, Captions = 256, Rotation = 512, Hdr = 1024,
    Drm = 2048, MultiKey = 4096, Discontinuity = 8192, Error = 16384,
    AudioOnly = 32768, EmbeddedCaptions = 65536, TrickMode = 131072, CodecSwitch = 262144,
    HighFrameRate = 524288, Fullscreen = 1048576, ResponsiveChrome = 2097152, EarlyPlay = 4194304,
}

sealed record MediaTestScenario(
    string Id, string Title, string Description, string Source, MediaScenarioKind Kind,
    MediaScenarioFeature Features, string Expected, string Reference,
    string? LicenseUrl = null, string? HeaderName = null, string? HeaderValue = null);

/// <summary>Public, reproducible media fixtures. Each entry says what it proves and what success looks like; the lab
/// intentionally includes unsupported-codec/network cases so typed failure behavior is testable too.</summary>
static class MediaTestCatalog
{
    public static readonly MediaTestScenario[] All =
    [
        new("progressive", "Progressive MP4 · transport + seek",
            "Short clear H.264 MP4 for play/pause/resume, accurate seek, volume, resize and every aspect mode.",
            "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4",
            MediaScenarioKind.Progressive, MediaScenarioFeature.Transport | MediaScenarioFeature.Seek | MediaScenarioFeature.Aspect,
            "Starts within a few seconds; clock advances; pause/resume and drag seek acknowledge; no frame escapes the viewport.",
            "https://test-videos.co.uk/bigbuckbunny/mp4-h264"),

        new("chrome-responsive", "Player chrome · compact + overflow",
            "The clear MP4 in a deliberately narrow 520-DIP player verifies the two-row overlay, auto-hide/reveal and ellipsis overflow menu.",
            "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4",
            MediaScenarioKind.Progressive, MediaScenarioFeature.Transport | MediaScenarioFeature.ResponsiveChrome,
            "Seek remains full-width; advanced quality/audio/caption/rate/aspect commands collapse into …; chrome fades while playing and returns on pointer, touch or focus.",
            "https://test-videos.co.uk/bigbuckbunny/mp4-h264"),

        new("chrome-fullscreen", "Player chrome · true fullscreen",
            "The clear MP4 verifies borderless monitor fullscreen, F11/Escape, overlay controls and exact restoration of the prior window placement.",
            "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4",
            MediaScenarioKind.Progressive, MediaScenarioFeature.Transport | MediaScenarioFeature.Fullscreen | MediaScenarioFeature.Aspect,
            "Fullscreen covers the monitor without title chrome; controls auto-hide; Escape or the restore glyph returns to the original window and inline surface.",
            "https://test-videos.co.uk/bigbuckbunny/mp4-h264"),

        new("early-play", "Opening · early Play intent + motion",
            "Press Play immediately after Run to verify that intent is accepted while metadata/network setup is still Opening.",
            "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4",
            MediaScenarioKind.Progressive, MediaScenarioFeature.Transport | MediaScenarioFeature.Buffering | MediaScenarioFeature.EarlyPlay,
            "The button changes to Pause immediately; an animated Starting playback shell remains responsive; playback begins automatically when ready without a second click or a discrete visual snap.",
            "https://test-videos.co.uk/bigbuckbunny/mp4-h264"),

        new("dash-vod", "DASH VOD · ABR ladder",
            "Big Buck Bunny 30fps DASH reference ladder for manifest templates, initialization segments, ABR and end-of-stream.",
            "https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Transport | MediaScenarioFeature.Seek | MediaScenarioFeature.Buffering | MediaScenarioFeature.Abr | MediaScenarioFeature.Aspect,
            "Qualities enumerate; auto quality settles without oscillation; manual pin is acknowledged; seeking resumes without A/V drift.",
            "https://reference.dashif.org/dash.js/latest/samples/getting-started/basic-playback.html"),

        new("dash-live", "DASH live · DVR + go-live",
            "DASH-IF livesim two-second live presentation for moving seek window, live offset and reconnect behavior.",
            "https://livesim2.dashif.org/livesim2/testpic_2s/Manifest.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Transport | MediaScenarioFeature.Buffering | MediaScenarioFeature.Live | MediaScenarioFeature.Abr,
            "LIVE badge appears; seek window moves; seeking behind live works; Go Live returns to the current edge.",
            "https://livesim2.dashif.org/assets"),

        new("dash-ll", "Low-latency DASH · catch-up",
            "DASH-IF low-delay CMAF loop for availability-time-offset, partial availability and live catch-up policy.",
            "https://livesim2.dashif.org/livesim2/testpic_2s_low_delay/Manifest.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Buffering | MediaScenarioFeature.Live | MediaScenarioFeature.LowLatency | MediaScenarioFeature.Abr,
            "Low-latency mode is detected; live offset converges without a seek storm; underrun uses catch-up/rebuffer policy.",
            "https://livesim2.dashif.org/assets"),

        new("dash-multiaudio", "DASH · alternate audio",
            "DASH-IF live asset with two audio adaptations for track enumeration and seamless audio switching.",
            "https://livesim2.dashif.org/livesim2/testpic_6s/multiaudio.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.MultiAudio | MediaScenarioFeature.Buffering,
            "Both audio tracks appear; selecting either is acknowledged; switch buffering is explicit and video clock remains continuous.",
            "https://livesim2.dashif.org/assets"),

        new("dash-mixed", "DASH · mixed segment duration",
            "Two-second video with six-second audio segments catches schedulers that incorrectly assume aligned segment boundaries.",
            "https://livesim2.dashif.org/livesim2/testpic_6s/mixeddur.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.Buffering | MediaScenarioFeature.Discontinuity,
            "Audio and video schedule independently and stay synchronized; no false end or repeated segment request.",
            "https://livesim2.dashif.org/assets"),

        new("dash-captions", "DASH · multiple captions",
            "DASH-IF five-track subtitle/caption presentation for track roles, regions and caption enable/disable.",
            "https://livesim2.dashif.org/livesim2/testpic_2s/multi_subs.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.Captions,
            "All text tracks enumerate with language/role; selected cues render in the safe area and Off removes them.",
            "https://livesim2.dashif.org/assets"),

        new("dash-rotation", "DASH · rotation + aspect",
            "DASH-IF rotating-logo ladder for representation switches, resize, aspect fit/fill and geometry diagnostics.",
            "https://livesim2.dashif.org/livesim2/testrotate_2s/Manifest.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.Abr | MediaScenarioFeature.Rotation | MediaScenarioFeature.Aspect,
            "720p/540p/360p variants enumerate; resize and aspect changes stay centered with no crop leak or distortion.",
            "https://livesim2.dashif.org/assets"),

        new("dash-avc3", "DASH · AVC3 in-band configuration",
            "DASH-IF AVC3 fixture verifies in-band parameter sets and representation continuity across segment boundaries.",
            "https://livesim2.dashif.org/livesim2/testpic_2s/Manifest_avc3.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.CodecSwitch | MediaScenarioFeature.Abr,
            "AVC3 is catalogued distinctly; segment-boundary parameter changes do not produce decoder errors or a black frame.",
            "https://livesim2.dashif.org/assets"),

        new("dash-audio-only", "DASH · audio-only live",
            "DASH-IF audio-only live presentation verifies chrome degradation, live timing and the no-video compositor path.",
            "https://livesim2.dashif.org/livesim2/testpic_2s/audio.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.AudioOnly | MediaScenarioFeature.Buffering,
            "No video hole is created; audio transport, buffering, DVR timing and Go Live remain functional.",
            "https://livesim2.dashif.org/assets"),

        new("dash-cea608", "DASH · embedded CEA-608 captions",
            "DASH-IF video with two embedded CEA-608 services validates caption discovery independently of sidecar subtitles.",
            "https://livesim2.dashif.org/livesim2/testpic_2s/cea608.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.Captions | MediaScenarioFeature.EmbeddedCaptions,
            "Both caption services are discoverable; enabling one renders timed cues and Off removes the overlay.",
            "https://livesim2.dashif.org/assets"),

        new("dash-trickmode", "DASH · trick-mode track",
            "DASH-IF trick-mode adaptation validates role parsing, frame-step capability and separation from normal ABR variants.",
            "https://livesim2.dashif.org/livesim2/testpic_2s/Manifest_trickmode.mpd",
            MediaScenarioKind.Dash, MediaScenarioFeature.Live | MediaScenarioFeature.TrickMode | MediaScenarioFeature.Abr,
            "Trick-mode is not selected for normal playback; frame-step/seek remains responsive and normal quality selection excludes it.",
            "https://livesim2.dashif.org/assets"),

        new("clear-60fps", "Clear MP4 · 1080p60 composition",
            "Microsoft's official clear counterpart to the CBCS fixture stresses 60fps pumping, scaling and pause/seek without DRM.",
            "https://test.playready.microsoft.com/media/dash/APPLEENC_CBCS_BBB_1080p/clear/bbb_sunflower_1080p_60fps_normal.mp4",
            MediaScenarioKind.Progressive, MediaScenarioFeature.Transport | MediaScenarioFeature.Seek | MediaScenarioFeature.Aspect | MediaScenarioFeature.HighFrameRate,
            "Clock and frame delivery sustain 60fps; resize/aspect modes stay centered; pause/resume/seek acknowledge without command loss.",
            "https://learn.microsoft.com/playready/advanced/testcontent/playready-4x-test-content"),

        new("hls-bipbop", "HLS · renditions + WebVTT",
            "Apple Bip Bop master with AVC/HEVC variants, alternate audio and WebVTT subtitles.",
            "https://devstreaming-cdn.apple.com/videos/streaming/examples/bipbop_16x9/bipbop_16x9_variant.m3u8",
            MediaScenarioKind.Hls, MediaScenarioFeature.Transport | MediaScenarioFeature.Seek | MediaScenarioFeature.Abr | MediaScenarioFeature.MultiAudio | MediaScenarioFeature.Captions,
            "Master variants and rendition groups enumerate; alternate audio and English subtitles can be selected.",
            "https://developer.apple.com/streaming/examples/"),

        new("hls-hdr", "HLS · HDR / HEVC / Atmos ladder",
            "Apple advanced stream exercises AVC, HEVC, Dolby Vision/HDR10 signaling, audio renditions and WebVTT.",
            "https://devstreaming-cdn.apple.com/videos/streaming/examples/adv_dv_atmos/main.m3u8",
            MediaScenarioKind.Hls, MediaScenarioFeature.Abr | MediaScenarioFeature.Hdr | MediaScenarioFeature.MultiAudio | MediaScenarioFeature.Captions,
            "Compatible variants are selected for the display; HDR metadata is surfaced; unsupported codecs downgrade or fail with a typed error.",
            "https://developer.apple.com/streaming/examples/"),

        new("playready-axinom", "PlayReady CENC · Axinom",
            "Known-good single-key DASH/CENC path used for native CDM transport, protected composition, seek and license relay.",
            ProtectedVideoDemo.AxinomMpd, MediaScenarioKind.PlayReadyDash,
            MediaScenarioFeature.Transport | MediaScenarioFeature.Seek | MediaScenarioFeature.Buffering | MediaScenarioFeature.Drm | MediaScenarioFeature.Aspect,
            "License becomes USABLE; protected handle is non-zero; capture may be black; play/pause/resume/seek all receive native acknowledgements.",
            "https://docs.axinom.com/services/drm/players/", ProtectedVideoDemo.AxinomLicenseUrl,
            ProtectedVideoDemo.AxinomHeaderName, ProtectedVideoDemo.AxinomToken),

        new("playready-cbcs-dash", "PlayReady CBCS · DASH",
            "Microsoft's official H.264/AAC CBCS presentation tests pattern encryption and protected audio/video.",
            "https://test.playready.microsoft.com/media/dash/APPLEENC_CBCS_BBB_1080p/1080p.mpd",
            MediaScenarioKind.PlayReadyDash, MediaScenarioFeature.Transport | MediaScenarioFeature.Seek | MediaScenarioFeature.Drm | MediaScenarioFeature.MultiKey,
            "License succeeds at SL150; CBCS samples decrypt; protected video and audio advance together.",
            "https://learn.microsoft.com/playready/advanced/testcontent/playready-4x-test-content",
            "http://test.playready.microsoft.com/service/rightsmanager.asmx?cfg=(persist:false,ck:W31bfVt9W31bfVt9W31bfQ==,ckt:aes128bitcbc)"),

        new("playready-cbcs-hls", "PlayReady CBCS · HLS compatibility",
            "Microsoft's official protected HLS alternate playlist. This is also the explicit HLS+PlayReady compatibility probe.",
            "https://test.playready.microsoft.com/media/dash/APPLEENC_CBCS_BBB_1080p/1080p_alternate.m3u8",
            MediaScenarioKind.PlayReadyHls, MediaScenarioFeature.Drm | MediaScenarioFeature.MultiKey,
            "Either protected HLS plays with CBCS or the engine reports a typed Unsupported/DRM error—never silent black or an infinite spinner.",
            "https://learn.microsoft.com/playready/advanced/testcontent/playready-4x-test-content",
            "http://test.playready.microsoft.com/service/rightsmanager.asmx?cfg=(persist:false,ck:W31bfVt9W31bfVt9W31bfQ==,ckt:aes128bitcbc)"),

        new("negative-404", "Negative · network failure",
            "A stable missing URL verifies retry budget, network buffering reason and terminal typed recovery.",
            "https://dash.akamaized.net/akamai/bbb_30fps/does-not-exist.mpd",
            MediaScenarioKind.Negative, MediaScenarioFeature.Buffering | MediaScenarioFeature.Error,
            "Retries are bounded; UI reports Network/NeedsNetwork; controls remain responsive; no orphan test/player process.",
            "https://dash.akamaized.net/akamai/bbb_30fps/"),
    ];

    public static MediaTestScenario? Find(string id)
    {
        foreach (MediaTestScenario s in All) if (s.Id == id) return s;
        return null;
    }
}

/// <summary>The single media area of the gallery: a scenario CATALOG page (grouped tiles) that NAVIGATES to a
/// per-scenario player page (auto-runs on entry, back returns to the catalog). Replaces the former three-page split
/// (Professional Media Lab / Desktop Video / Protected Video) — DRM lives here as its own section, including the
/// free-form DASH+PlayReady source form.</summary>
sealed class MediaLabPage : Component
{
    /// <summary>The non-catalog detail entry: the free-form DASH + PlayReady source form (Axinom prefilled).</summary>
    internal const string CustomDrmId = "custom-drm";

    private static readonly (string Title, string[] Ids)[] Sections =
    [
        ("Playback & player chrome", ["progressive", "clear-60fps", "chrome-responsive", "chrome-fullscreen", "early-play"]),
        ("Adaptive streaming", ["dash-vod", "dash-live", "dash-ll", "dash-avc3", "dash-rotation", "dash-trickmode", "dash-mixed", "hls-bipbop", "hls-hdr"]),
        ("Tracks, captions & audio", ["dash-multiaudio", "dash-captions", "dash-cea608", "dash-audio-only"]),
        ("Protected content (PlayReady)", ["playready-axinom", "playready-cbcs-dash", "playready-cbcs-hls", CustomDrmId]),
        ("Failure handling", ["negative-404"]),
    ];

    public override Element Render()
    {
        var (openId, setOpenId) = UseState("");

        if (openId.Length > 0)
            return Embed.Comp(() => new MediaScenarioPage { ScenarioId = openId, Back = () => setOpenId("") })
                with { Key = "media-scenario:" + openId };

        var body = new List<Element>(Sections.Length * 2);
        foreach (var (title, ids) in Sections)
        {
            var tiles = new List<Element>(ids.Length);
            foreach (string id in ids)
            {
                string scenarioId = id;
                if (id == CustomDrmId)
                {
                    tiles.Add(GalleryPage.Tile("Custom source · DRM form", null, Icons.Movie, () => setOpenId(scenarioId),
                        "Enter any DASH manifest + PlayReady license server. Prefilled with the Axinom single-key test vector."));
                    continue;
                }
                MediaTestScenario? s = MediaTestCatalog.Find(id);
                if (s is null) continue;
                tiles.Add(GalleryPage.Tile(s.Title, null, Icons.Movie, () => setOpenId(scenarioId), s.Description));
            }
            body.Add(Subtitle(title));
            body.Add(AutoGrid(300f, 12f, 104f, tiles.ToArray()));
        }

        return GalleryPage.ShellKeyed("media-lab", "Media Lab",
            "Every media capability as a runnable scenario over public DASH, HLS, PlayReady and negative fixtures. " +
            "Open a scenario to play it and compare live diagnostics with its expected result.",
            body.ToArray());
    }
}

/// <summary>One scenario as its own page: back header, the auto-running player, the expected-result card and a
/// restart action. The player's own chrome carries aspect/quality/track/caption/rate/fullscreen controls.</summary>
sealed class MediaScenarioPage : Component
{
    public required string ScenarioId { get; init; }
    public required Action Back { get; init; }

    public override Element Render()
    {
        var (runEpoch, setRunEpoch) = UseState(0);
        var aspect = UseSignal(VideoAspectMode.Uniform);
        var customAspect = UseSignal(2.39);

        bool custom = ScenarioId == MediaLabPage.CustomDrmId;
        MediaTestScenario? scenario = custom ? null : MediaTestCatalog.Find(ScenarioId);
        string title = custom ? "Custom source · DASH + PlayReady" : scenario?.Title ?? ScenarioId;

        Element runner = custom
            ? FluentGpu.WindowsApi.Media.PlayReady.DesktopProtectedVideoPlayer.IsAvailable
                ? Embed.Comp(() => new ProtectedVideoDemo()) with { Key = "custom-drm#" + runEpoch }
                : Body("The in-process PlayReady native backend (FluentGpu.PlayReady.Native.dll) isn't present in this build.").Secondary()
            : scenario is null
                ? Body("Unknown scenario.").Secondary()
                : Embed.Comp(() => new MediaScenarioView(scenario, aspect, customAspect)) with { Key = scenario.Id + "#" + runEpoch };

        var kids = new List<Element>(6)
        {
            new BoxEl
            {
                Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                Children =
                [
                    BackButton(Back),
                    new BoxEl
                    {
                        Direction = 1, Gap = 2f, Shrink = 1f, MinWidth = 0f,
                        Children = custom || scenario is null
                            ? [Title(title)]
                            : [Title(title), Caption(scenario.Kind + " · " + scenario.Features).Secondary()],
                    },
                    new BoxEl { Grow = 1f },
                    Button.Standard("Restart", () => setRunEpoch(runEpoch + 1)),
                ],
            },
            runner,
        };
        if (scenario is not null)
            kids.Add(new BoxEl
            {
                Direction = 1, Gap = 8f, Padding = Edges4.All(16f), Fill = Tok.FillCardDefault,
                BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Corners = Radii.OverlayAll,
                Children =
                [
                    Subtitle("What this scenario proves"),
                    Body(scenario.Description).Secondary(),
                    Body("Expected: " + scenario.Expected).Secondary(),
                    Caption("Source: " + scenario.Source).Tertiary(),
                    Caption("Reference: " + scenario.Reference).Tertiary(),
                ],
            });

        return ScrollView(new BoxEl { Direction = 1, Gap = 14f, Padding = Edges4.All(28f), Children = kids.ToArray() });
    }

    static Element BackButton(Action back) => new BoxEl
    {
        Width = 36f, Height = 36f, Corners = Radii.ControlAll,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, OnClick = back,
        Children = [new TextEl(Icons.Back) { Size = 14f, Color = Tok.TextPrimary, FontFamily = Theme.IconFont }],
    };
}

sealed class MediaScenarioView : Component
{
    private readonly MediaTestScenario _scenario;
    private readonly IReadSignal<VideoAspectMode> _aspect;
    private readonly IReadSignal<double> _customAspect;
    public MediaScenarioView(MediaTestScenario scenario, IReadSignal<VideoAspectMode> aspect, IReadSignal<double> customAspect)
    { _scenario = scenario; _aspect = aspect; _customAspect = customAspect; }

    public override Element Render()
    {
        MediaTestScenario scenario = _scenario;
        if (scenario.Kind == MediaScenarioKind.PlayReadyDash)
            return Embed.Comp(() => new ProtectedVideoDemo(scenario));
        if (scenario.Kind == MediaScenarioKind.PlayReadyHls)
            return Info("Protected HLS is retained as an explicit compatibility probe. Run its URL/license pair through the \"Custom source · DRM form\" scenario; success or a typed unsupported error is valid, silent black is not.");

        var player = UseMediaPlayer(b => b.WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer())
            .WithBuffering(scenario.Features.HasFlag(MediaScenarioFeature.LowLatency) ? BufferPolicy.LowLatencyLive :
                scenario.Features.HasFlag(MediaScenarioFeature.Live) ? BufferPolicy.Live : BufferPolicy.Vod));
        UseEffect(() =>
        {
            MediaSource source = scenario.Kind is MediaScenarioKind.Dash or MediaScenarioKind.Hls or MediaScenarioKind.Negative
                ? MediaSource.FromAdaptive(scenario.Source, new AdaptiveSourceOptions
                {
                    ManifestKind = scenario.Kind == MediaScenarioKind.Hls ? AdaptiveManifestKind.Hls : AdaptiveManifestKind.Dash,
                    LatencyMode = scenario.Features.HasFlag(MediaScenarioFeature.LowLatency) ? LiveLatencyMode.LowLatency : LiveLatencyMode.Standard,
                })
                : MediaSource.FromUri(scenario.Source);
            _ = OpenAndPlayAsync();

            async Task OpenAndPlayAsync()
            {
                // Declare play intent explicitly: without it the session pump pauses the engine right after metadata
                // (open ≠ play), which reads as "the scenario silently fails" ~40ms in.
                try { await player.OpenAsync(source); await player.PlayAsync(); }
                catch { /* surfaced as the player's typed Error signal */ }
            }
        }, DepKey.Empty);   // mount-once (open+play the scenario)

        float playerWidth = scenario.Features.HasFlag(MediaScenarioFeature.ResponsiveChrome) ? 520f : float.NaN;
        return new BoxEl
        {
            Direction = 1, Gap = 8f,
            Children =
            [
                new BoxEl { Width = playerWidth, MaxWidth = float.NaN, Height = 440f, Children = [Embed.Comp(() => new MediaPlayerElement { Player = player, AspectMode = _aspect, CustomAspectRatio = _customAspect })] },
                Embed.Comp(() => new MediaScenarioDiagnostics { Player = player }),
            ],
        };
    }

    private static Element Info(string text) => new BoxEl
    {
        Padding = Edges4.All(14f), Fill = Tok.FillCardDefault, Corners = Radii.ControlAll,
        Children = [Body(text).Secondary()],
    };
}

sealed class MediaScenarioDiagnostics : Component
{
    public required IMediaPlayer Player { get; init; }

    public override Element Render()
    {
        PlaybackState state = Player.State.Value;
        BufferingInfo buffer = Player.Buffering.Value;
        TimelineInfo timeline = Player.Timeline.Value;
        VideoGeometry geometry = Player.VideoGeometry.Value;
        VideoColorInfo color = Player.VideoColor.Value;
        PlaybackStatistics stats = Player.Statistics.Value;
        _ = Player.Tracks.Audio.Version.Value;
        _ = Player.Tracks.Video.Version.Value;
        _ = Player.Tracks.Text.Version.Value;
        _ = Player.Qualities.Variants.Version.Value;
        QualityVariant? activeQuality = Player.Qualities.Active.Value;
        MediaError? error = Player.Error.Value;
        string text = $"state={state} · buffer={buffer.Reason} {(buffer.Percent >= 0 ? buffer.Percent.ToString("P0") : "indeterminate")} · " +
            $"ahead={buffer.BufferedAhead.TotalSeconds:0.0}s · live={timeline.IsLive} offset={timeline.LiveOffset.TotalSeconds:0.0}s · " +
            $"geometry={geometry.DisplaySize.Width}×{geometry.DisplaySize.Height} · HDR={color.Hdr} · dropped={stats.FramesDropped}";
        string catalog = $"tracks video={Player.Tracks.Video.Count} audio={Player.Tracks.Audio.Count} text={Player.Tracks.Text.Count} · " +
            $"qualities={Player.Qualities.Variants.Count} active={activeQuality?.Label ?? activeQuality?.Id ?? "auto/pending"} · " +
            $"throughput={stats.EstimatedThroughputKbps:0}kbps rebuffer={stats.RebufferCount}";
        return new BoxEl
        {
            Direction = 1, Gap = 4f, Padding = Edges4.All(10f), Fill = Tok.FillLayerDefault, Corners = Radii.ControlAll,
            Children = error is null
                ? [Caption(text).Secondary(), Caption(catalog).Tertiary()]
                : [Caption(text).Secondary(), Caption(catalog).Tertiary(), Caption($"ERROR {error.Category}: {error.Message}").Foreground(Tok.SystemFillCritical)],
        };
    }
}
