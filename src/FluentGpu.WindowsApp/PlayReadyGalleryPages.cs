using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.WindowsApi.Media.PlayReady;
using static FluentGpu.Dsl.Ui;

// ── Protected-video (PlayReady) playback page ──────────────────────────────────────────────────────────────────────

/// <summary>Plays a protected (PlayReady) video through the engine's video-compositing spine. Native PlayReady runs
/// in-process (the <see cref="DesktopProtectedVideoPlayer"/> DLL backend); when that backend isn't present in the build
/// the page shows a graceful "not available" note instead of failing.</summary>
sealed class ProtectedVideoPage : Component
{
    public override Element Render()
    {
        Element body = DesktopProtectedVideoPlayer.IsAvailable
            ? Embed.Comp(() => new ProtectedVideoDemo())
            : NotAvailable();

        return GalleryPage.Shell("Protected Video (PlayReady - Desktop)",
            "Native desktop PlayReady through the unified Media Playback API: a MediaPlayer routed to MfMediaPlayer + a " +
            "protected backend (native CDM), a MediaSource carrying DrmConfig, and the Axinom test license supplied by a " +
            "managed WithDrm relay. Windows owns the mfpmp.exe boundary; capture is black by design (output protection).",
            body);
    }

    static Element NotAvailable()
        => ControlExample.Build("PlayReady backend unavailable",
            new BoxEl { Direction = 1, Gap = 10, Children =
                [Body("The in-process PlayReady native backend (FluentGpu.PlayReady.Native.dll) isn't present in this build.").Secondary()] });
}

/// <summary>Clear-video control through the unified Media Playback API: a real <c>MfMediaPlayer</c> (Media Foundation
/// windowless-swapchain decode) bound to the real <c>MediaPlayerElement</c> — the M1 on-box proof of the same-process
/// video surface + gallery composition, independent of DRM.</summary>
sealed class DesktopVideoPage : Component
{
    public override Element Render()
        => GalleryPage.Shell("Desktop Video (In-process)",
            "A real clear MP4 decoded by Media Foundation inside the normal FluentGpu desktop process via the unified " +
            "Media Playback API (MfMediaPlayer → MediaPlayerElement). This proves the same-process video surface and " +
            "gallery composition independently of DRM.",
            Embed.Comp(() => new ClearVideoDemo()));
}

/// <summary>Owns a <c>MediaPlayer</c> routed to the MF video backend, opens a clear MP4 once at mount, and presents it
/// through the real <c>MediaPlayerElement</c> (its own transport, its own hole-punched video surface).</summary>
sealed class ClearVideoDemo : Component
{
    // A stable clear progressive H.264 MP4 IMFMediaEngine resolves by URL. Override via FG_VIDEO_URL (e.g. a local path).
    const string DefaultUrl = "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4";

    public override Element Render()
    {
        var player = UseMediaPlayer(b => b.WithBackend(FluentGpu.Media.MediaKind.MfVideoOrFile, new FluentGpu.Media.Windows.MfMediaPlayer()));
        UseEffect(() =>
        {
            string url = Environment.GetEnvironmentVariable("FG_VIDEO_URL") is { Length: > 0 } e ? e : DefaultUrl;
            _ = player.OpenAsync(FluentGpu.Media.MediaSource.FromUri(url));
        }, System.Array.Empty<object>());

        return new BoxEl
        {
            Height = 460f,
            Children = [Embed.Comp(() => new FluentGpu.Controls.Media.MediaPlayerElement { Player = player })],
        };
    }
}

/// <summary>A prepared, parsed protected source ready to play: the DASH descriptor (from <see cref="DashManifestParser"/>)
/// + the built <c>WithDrm</c> license relay + the entered MPD/license URLs. Handed to a keyed <see cref="ProtectedPlayerView"/>
/// so each Play remounts a fresh player.</summary>
sealed record PreparedProtectedSource(int Id, string MpdUrl, string LicenseUrl, DashSourceDescriptor Descriptor,
    Func<LicenseRequest, ValueTask<LicenseResponse>> Relay);

/// <summary>GENERIC protected (PlayReady) playback through the unified Media Playback API: a FORM (MPD URL, license server
/// URL, an optional custom license header) that, on Play, parses ANY DASH/PlayReady MPD (<see cref="DashManifestParser"/>),
/// builds a <c>MediaPlayer</c> routed to <c>MfMediaPlayer</c> + a <c>ProtectedMediaBackend</c> carrying the parsed
/// descriptor, and a MANAGED <c>WithDrm</c> relay that POSTs the CDM challenge to the entered license server with the
/// entered header. Prefilled with the Axinom single-key v10 test vector as the default preset. Parse/license failures
/// show inline (never a silent black frame).</summary>
sealed class ProtectedVideoDemo : Component
{
    // ── Axinom public single-key PlayReady v10 test vector — the default preset (edit the fields to play any source) ──
    const string AxinomMpd = "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/manifest.mpd";
    const string AxinomLicenseUrl = "https://drm-playready-licensing.axprod.net/AcquireLicense";
    const string AxinomHeaderName = "X-AxDRM-Message";
    const string AxinomToken =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.ewogICJ2ZXJzaW9uIjogMSwKICAiY29tX2tleV9pZCI6ICI2OWU1NDA4OC1lOWUw" +
        "LTQ1MzAtOGMxYS0xZWI2ZGNkMGQxNGUiLAogICJtZXNzYWdlIjogewogICAgInR5cGUiOiAiZW50aXRsZW1lbnRfbWVzc2FnZSIs" +
        "CiAgICAidmVyc2lvbiI6IDIsCiAgICAibGljZW5zZSI6IHsKICAgICAgImFsbG93X3BlcnNpc3RlbmNlIjogdHJ1ZQogICAgfSwK" +
        "ICAgICJjb250ZW50X2tleXNfc291cmNlIjogewogICAgICAiaW5saW5lIjogWwogICAgICAgIHsKICAgICAgICAgICJpZCI6ICI0" +
        "MDYwYTg2NS04ODc4LTQyNjctOWNiZi05MWFlNWJhZTFlNzIiLAogICAgICAgICAgImVuY3J5cHRlZF9rZXkiOiAid3QzRW51dVI1" +
        "UkFybjZBRGYxNkNCQT09IiwKICAgICAgICAgICJ1c2FnZV9wb2xpY3kiOiAiUG9saWN5IEEiCiAgICAgICAgfQogICAgICBdCiAg" +
        "ICB9LAogICAgImNvbnRlbnRfa2V5X3VzYWdlX3BvbGljaWVzIjogWwogICAgICB7CiAgICAgICAgIm5hbWUiOiAiUG9saWN5IEEi" +
        "LAogICAgICAgICJwbGF5cmVhZHkiOiB7CiAgICAgICAgICAibWluX2RldmljZV9zZWN1cml0eV9sZXZlbCI6IDE1MCwKICAgICAg" +
        "ICAgICJwbGF5X2VuYWJsZXJzIjogWwogICAgICAgICAgICAiNzg2NjI3RDgtQzJBNi00NEJFLThGODgtMDhBRTI1NUIwMUE3Igog" +
        "ICAgICAgICAgXQogICAgICAgIH0KICAgICAgfQogICAgXQogIH0KfQ.l8PnZznspJ6lnNmfAE9UQV532Ypzt1JXQkvrk8gFSRw";

    private static readonly HttpClient s_http = new();

    public override Element Render()
    {
        var mpd = UseSignal(AxinomMpd);
        var licenseUrl = UseSignal(AxinomLicenseUrl);
        var headerName = UseSignal(AxinomHeaderName);
        var headerValue = UseSignal(AxinomToken);
        var (request, setRequest) = UseState<PreparedProtectedSource?>(null);
        var (error, setError) = UseState<string?>(null);
        var (busy, setBusy) = UseState(false);
        var nextId = UseRef(0);
        var post = UsePost();   // marshal the async parse result back onto the UI thread

        async void Play()
        {
            setBusy(true);
            setError(null);
            string mpdUrl = mpd.Peek().Trim();
            string licUrl = licenseUrl.Peek().Trim();
            string? hName = string.IsNullOrWhiteSpace(headerName.Peek()) ? null : headerName.Peek().Trim();
            string hValue = headerValue.Peek();
            try
            {
                var desc = await DashManifestParser.ParseAsync(mpdUrl, s_http).ConfigureAwait(false);
                var relay = PlayReadyLicense.HttpRelay(licUrl, hName, hValue);
                post(() =>
                {
                    nextId.Value++;
                    setRequest(new PreparedProtectedSource(nextId.Value, mpdUrl, licUrl, desc, relay));
                    setBusy(false);
                });
            }
            catch (Exception ex)
            {
                string msg = ex is DashManifestException ? ex.Message : "Could not prepare the source: " + ex.Message;
                post(() => { setError(msg); setBusy(false); });
            }
        }

        var form = new BoxEl
        {
            Direction = 1, Gap = 10f,
            Children =
            [
                TextBox.Create(header: "MPD manifest URL", placeholder: "https://…/manifest.mpd", width: 640f, text: mpd),
                TextBox.Create(header: "License server URL", placeholder: "https://…/AcquireLicense", width: 640f, text: licenseUrl),
                new BoxEl
                {
                    Direction = 0, Gap = 10f, AlignItems = FlexAlign.End,
                    Children =
                    [
                        TextBox.Create(header: "License header name (optional)", placeholder: "X-AxDRM-Message", width: 220f, text: headerName),
                        TextBox.Create(header: "License header value (optional)", placeholder: "token…", width: 410f, text: headerValue),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Accent(busy ? "Preparing…" : "Play", Play, isEnabled: !busy),
                        Body("Prefilled with the Axinom single-key test vector. Enter any DASH + PlayReady source.").Secondary(),
                    ],
                },
                error is not null ? ErrorBanner(error) : new BoxEl { Height = 0f },
            ],
        };

        var kids = new List<Element>(2) { form };
        if (request is { } req)
            kids.Add(Embed.Comp(() => new ProtectedPlayerView(req)) with { Key = "player#" + req.Id });

        return new BoxEl { Direction = 1, Gap = 14f, Children = kids.ToArray() };
    }

    static Element ErrorBanner(string message) => new BoxEl
    {
        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(12f, 10f, 12f, 10f),
        Corners = Radii.ControlAll,
        Fill = new ColorF(0.28f, 0.10f, 0.11f, 1f),
        BorderColor = new ColorF(0.62f, 0.24f, 0.26f, 1f), BorderWidth = 1f,
        Children =
        [
            new TextEl(message) { Size = 13f, Color = new ColorF(0.98f, 0.80f, 0.80f, 1f), Wrap = TextWrap.Wrap },
        ],
    };
}

/// <summary>Owns the <c>MediaPlayer</c> for one prepared protected source: routed to <c>MfMediaPlayer</c> + a
/// <c>ProtectedMediaBackend</c> carrying the parsed descriptor, opened once at mount, presented by the real
/// <c>MediaPlayerElement</c>. A typed DRM/playback error is shown inline (never a silent black frame). Keyed per request,
/// so a new Play remounts a fresh player.</summary>
sealed class ProtectedPlayerView : Component
{
    private readonly PreparedProtectedSource _req;
    public ProtectedPlayerView(PreparedProtectedSource req) => _req = req;

    public override Element Render()
    {
        var req = _req;
        var player = UseMediaPlayer(b => b
            .WithBackend(MediaKind.MfVideoOrFile,
                new MfMediaPlayer(new ProtectedMediaBackend(req.Relay, req.Descriptor)))
            .WithDrm(req.Relay));

        UseEffect(() =>
        {
            var source = MediaSource.FromUri(req.MpdUrl).With(new DrmConfig(DrmSystem.PlayReady, req.LicenseUrl));
            _ = player.OpenAsync(source);
        }, Array.Empty<object>());

        var err = player.Error.Value;   // subscribe → surface a typed CDM/DRM/source error inline
        var kids = new List<Element>(2)
        {
            new BoxEl { Height = 420f, Children = [Embed.Comp(() => new MediaPlayerElement { Player = player })] },
        };
        if (err is not null)
            kids.Add(new BoxEl
            {
                Padding = new Edges4(12f, 10f, 12f, 10f), Corners = Radii.ControlAll,
                Fill = new ColorF(0.28f, 0.10f, 0.11f, 1f),
                BorderColor = new ColorF(0.62f, 0.24f, 0.26f, 1f), BorderWidth = 1f,
                Children = [new TextEl($"{err.Category}: {err.Message}") { Size = 13f, Color = new ColorF(0.98f, 0.80f, 0.80f, 1f), Wrap = TextWrap.Wrap }],
            });
        return new BoxEl { Direction = 1, Gap = 8f, Children = kids.ToArray() };
    }
}
