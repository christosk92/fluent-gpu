using System;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Media.PlayReady;
using Wavee.SpotifyLive;

namespace Wavee.Features.Video;

/// <summary>
/// Root content of the detached, always-on-top pop-out video window (its own composited AppHost + swapchain + video
/// presenter). Reads the resolved <see cref="PopOutVideoSource"/> and mounts a keyed <see cref="PopOutVideoStage"/> so
/// the player rebuilds cleanly when the source changes (clear ↔ DRM / track change). The OS window frame handles
/// move/resize/close; the host sets always-on-top.
/// </summary>
sealed class PopOutVideoWindow : Component
{
    /// <summary>The resolved source (null = nothing yet — shows the letterbox background).</summary>
    public required IReadSignal<PopOutVideoSource?> Source { get; init; }

    public override Element Render()
    {
        // Size the root to THIS window's viewport. The AppHost does NOT auto-stretch a scene root (a bare Grow=1 hugs to
        // 0×0 and the composited swapchain then presents transparent — the pop-out looked see-through); WaveeShell fills
        // the same way. Fill paints an opaque letterbox even before a source resolves, so the window is never transparent.
        var vp = UseContextSignal(Viewport.Size);
        var src = Source.Value;   // subscribe → remount the stage on a source change
        return new BoxEl
        {
            Direction = 1,
            Width = Prop.Of(() => vp.Value.Width),
            Height = Prop.Of(() => vp.Value.Height),
            Fill = Tok.MediaLetterbox,
            Children = src is null
                ? Array.Empty<Element>()
                : [new BoxEl { Grow = 1, Children = [Embed.Comp(() => new PopOutVideoStage { Source = src }) with { Key = "stage:" + src.Key }] }],
        };
    }
}

/// <summary>One player+surface for a FROZEN source (props freeze at mount; the parent remounts this on a source change).
/// Builds the clear MF backend, or the clear+DRM MF backend (routing a DrmConfig source to the native PlayReady CDM),
/// and opens the source once. The MediaPlayerElement uses THIS window's AppHost registry, so video composites here.</summary>
sealed class PopOutVideoStage : Component
{
    public required PopOutVideoSource Source { get; init; }

    public override Element Render()
    {
        var src = Source;
        var player = UseMediaPlayer(b =>
        {
            if (src.IsDrm)
                // MfMediaPlayer routes a DrmConfig-carrying source to the injected DRM backend (native in-process CDM);
                // clear frames still use the proven engine path. ProtectedMediaBackend carries the parsed Spotify
                // descriptor (init/segment/stride/PSSH); the relay POSTs the license challenge.
                b.WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer(new ProtectedMediaBackend(src.LicenseRelay, src.DrmDescriptor)))
                 .WithDrm(src.LicenseRelay!);
            else
                b.WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer());
        });

        var opened = UseRef(false);
        if (!opened.Value)
        {
            opened.Value = true;
            if (src.IsDrm)
                // The descriptor drives the native open; the URI is advisory. DrmConfig routes to the DRM backend.
                _ = player.Play(MediaSource.FromUri(src.DrmDescriptor!.InitUrl).With(new DrmConfig(DrmSystem.PlayReady, src.LicenseServerUri)));
            else if (!string.IsNullOrEmpty(src.ClearUrl))
                _ = player.Play(MediaSource.FromUri(src.ClearUrl));
        }

        return Embed.Comp(() => new MediaPlayerElement { Player = player, Stretch = MediaStretch.Uniform });
    }
}
