using System;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Signals;
using FluentGpu.Windows.Media;

namespace Wavee.Features.Video;

/// <summary>
/// Root content of the detached, always-on-top pop-out video window (opened via <c>InputHooks.OpenDetachedWindow</c> —
/// the window is its own composited <c>AppHost</c> + swapchain + video presenter). A full-bleed
/// <see cref="MediaPlayerElement"/> on the clear Media-Foundation backend that (re)loads whenever the resolved
/// video/Canvas URL changes. The OS window frame handles move/resize/close; the host sets always-on-top. The Spotify
/// video-resolution layer (Canvas today; the gated PlayReady DRM path later) feeds <see cref="Url"/>.
/// </summary>
sealed class PopOutVideoWindow : Component
{
    /// <summary>The resolved, ready-to-play video/Canvas URL (null = nothing yet — shows the letterbox background).</summary>
    public required IReadSignal<string?> Url { get; init; }

    public override Element Render()
    {
        // Clear MF backend registered once at mount; DRM (ProtectedMediaBackend + WithDrm) is added by the Spotify DRM
        // lane once its runtime gate (the PlayReady/Widevine probe) is confirmed.
        var player = UseMediaPlayer(b => b.WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer()));
        var url = Url.Value;                        // subscribe → re-render + reload on a URL change
        var last = UseRef<string?>(null);
        if (!string.Equals(url, last.Value, StringComparison.Ordinal))
        {
            last.Value = url;
            if (!string.IsNullOrEmpty(url)) _ = player.Play(MediaSource.FromUri(url));
        }

        return new BoxEl
        {
            Grow = 1,
            Fill = Tok.MediaLetterbox,   // black surround behind the letterboxed frame (and before the first frame)
            Children = [Embed.Comp(() => new MediaPlayerElement { Player = player, Stretch = MediaStretch.Uniform })],
        };
    }
}
