using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
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

/// <summary>Protected (PlayReady) playback through the UNIFIED Media Playback API (M5): a <c>MediaPlayer</c> routed to an
/// <c>MfMediaPlayer</c> + a <c>ProtectedMediaBackend</c> (the native CDM), opened on a <c>MediaSource</c> carrying a
/// <c>DrmConfig</c>, and presented by the real <c>MediaPlayerElement</c>. The Axinom test license is acquired by a MANAGED
/// <c>WithDrm</c> relay (the license POST + X-AxDRM-Message live here, not in native).</summary>
sealed class ProtectedVideoDemo : Component
{
    // Axinom public single-key PlayReady v10 test vector (the license POST now lives in managed app code).
    const string AxinomMpd = "https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/manifest.mpd";
    const string AxinomLicenseUrl = "https://drm-playready-licensing.axprod.net/AcquireLicense";
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

    private static readonly System.Net.Http.HttpClient s_http = new();

    /// <summary>The managed WithDrm relay: POST the CDM challenge to the Axinom license server with the v10 entitlement
    /// token. The engine never sees the key; a shortfall becomes MediaError{Category.Drm} (never a silent drop).</summary>
    private static async System.Threading.Tasks.ValueTask<FluentGpu.Media.LicenseResponse> AcquireAxinomLicense(
        FluentGpu.Media.LicenseRequest request)
    {
        using var content = new System.Net.Http.ByteArrayContent(request.Challenge.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
        using var msg = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, AxinomLicenseUrl) { Content = content };
        msg.Headers.TryAddWithoutValidation("SOAPAction", "\"http://schemas.microsoft.com/DRM/2007/03/protocols/AcquireLicense\"");
        msg.Headers.TryAddWithoutValidation("X-AxDRM-Message", AxinomToken);
        using var resp = await s_http.SendAsync(msg).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        byte[] license = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return new FluentGpu.Media.LicenseResponse(license);
    }

    public override Element Render()
    {
        var player = UseMediaPlayer(b => b
            .WithBackend(FluentGpu.Media.MediaKind.MfVideoOrFile,
                new FluentGpu.Media.Windows.MfMediaPlayer(new ProtectedMediaBackend(AcquireAxinomLicense)))
            .WithDrm(AcquireAxinomLicense));

        UseEffect(() =>
        {
            var source = FluentGpu.Media.MediaSource.FromUri(AxinomMpd)
                .With(new FluentGpu.Media.DrmConfig(FluentGpu.Media.DrmSystem.PlayReady, AxinomLicenseUrl));
            _ = player.OpenAsync(source);
        }, System.Array.Empty<object>());

        return new BoxEl
        {
            Height = 460f,
            Children = [Embed.Comp(() => new FluentGpu.Controls.Media.MediaPlayerElement { Player = player })],
        };
    }
}
