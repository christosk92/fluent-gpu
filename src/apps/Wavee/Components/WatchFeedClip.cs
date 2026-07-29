using System;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The artist's watch-feed loop, playing inside a circle.
///
/// Deliberately thin: it owns nothing but one <see cref="MediaPlayer"/> and leans on pieces that already exist —
/// <see cref="MediaPlayerElement"/> for the surface and its pump, <c>MediaSource.Loop</c> for the repeat. It is NOT
/// wired to <c>FluentVideoMediaHost</c>: that host is the single owner of the now-playing video, and an artist
/// portrait must never take that slot from a music video the user is actually watching.
///
/// The clip is a Canvas mp4: for <c>videoType: "URL"</c> the wire puts a plain <c>https://canvaz.scdn.co/…cnvs.mp4</c>
/// straight into <c>watchFeedEntrypoint.video.fileId</c>, which the mapper surfaces as
/// <c>ArtistWatchFeed.CanvasUrl</c> — so there is no manifest hop and no DRM path here. The caller only mounts this
/// when that URL exists; an absent or non-URL video node keeps the still.
///
/// Round corners come from the COMPOSITOR (<c>MediaPlayerElement.CornerRadius</c> → <c>VideoBinding.SetCornerRadius</c>
/// → the DComp rounded rectangle clip), not from a parent: the frame composites in its own DirectComposition visual
/// outside the UI back buffer, where a parent's <c>ClipToBounds</c> has no reach.
/// </summary>
sealed class WatchFeedClip : Component
{
    /// <summary>The Canvas mp4 URL. Frozen at mount — the caller keys this component on it.</summary>
    public required string Url { get; init; }
    /// <summary>Circle diameter in DIP.</summary>
    public required float Size { get; init; }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);

        // Pages are Flow.KeepAlive-PARKED (MaxEntries 8), not unmounted, so navigating away never runs an unmount
        // cleanup. Parking therefore has to RELEASE, not merely pause: a paused MF engine still owns its MTA thread,
        // its decoder and its DXGI swapchain, so eight parked artist pages would sit on eight of each until the LRU
        // finally evicted them. And because the frame composites in its own DirectComposition visual below the UI
        // swapchain, a parked page's video is not covered by whatever the shell draws next — which is how a
        // navigated-away portrait ended up still playing on top of the navigation rail.
        //
        // Unmounting the whole subtree while parked is what makes the release complete: it tears down the element (the
        // surface is destroyed and the pump unregistered) and disposes the player. Re-opening on return is cheap and
        // invisible — the caller stacks the still underneath, so there is never a hole to look at.
        var active = UseIsActive();
        bool live = active.Value;   // subscribe: park/unpark re-renders this clip

        // Keyed on the activation epoch so a return builds a FRESH player rather than reviving a disposed one.
        var epoch = UseRef(0);
        if (live && _wasParked) { epoch.Value++; _wasParked = false; }
        else if (!live) _wasParked = true;

        var player = UseMemo(() =>
        {
            // The plain clear MF backend — the same construction FluentVideoMediaHost uses for a Canvas/local source.
            var p = MediaPlayer.Build().WithBackend(MediaKind.MfVideoOrFile, new MfMediaPlayer()).Build();
            p.SetVolume(0);   // a portrait must never make noise; the clip is decoration, not playback
            return p;
        }, DepKey.From(epoch.Value));

        UseEffect(() =>
        {
            if (live) _ = PlayLoopedAsync(player, Url, svc);
            return () =>
            {
                // Fire-and-forget teardown: DisposeAsync joins the MF engine's own thread, which must not block the
                // reconciler. Parking or navigating away stops the decoder without stalling the frame.
                _ = player.DisposeAsync();
            };
        }, DepKey.From(epoch.Value, live ? 1 : 0));

        if (!live) return new BoxEl();

        return Embed.Comp(() => new MediaPlayerElement
        {
            Player = player,
            AreTransportControlsEnabled = false,   // decoration: no transport, no chrome
            IsDecorative = true,                   // no 160-DIP floor, no frame border, no spinner over the still
            Stretch = MediaStretch.UniformToFill,  // a 9:16 canvas in a circle must fill it, not letterbox
            CornerRadius = Size / 2f,
        }) with { Key = "watchfeed:" + Url + ":" + epoch.Value };
    }

    bool _wasParked;

    static async Task PlayLoopedAsync(MediaPlayer player, string url, Services? svc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await player.Play(MediaSource.FromUri(url).Loop()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A dead CDN / unplayable canvas is not an error STATE for a portrait — the still simply stays — but it
            // must not be invisible: "the artist circle never animates" is otherwise undiagnosable.
            Log(svc, WaveeLogLevel.Warning, "watchfeed.clip.fail", "artist watch-feed clip failed to start", url, sw, ex);
            return;
        }

        // Play() returning is NOT proof of playback: MediaPlayer.OpenAsync swallows open failures into Error/State
        // instead of throwing, so a dead URL used to be logged as a successful start — leaving "the circle never
        // animates" with no matching failure line anywhere in the log.
        if (player.State.Peek() == PlaybackState.Failed)
            Log(svc, WaveeLogLevel.Warning, "watchfeed.clip.fail",
                "artist watch-feed clip failed to open: " + (player.Error.Peek()?.Message ?? "unknown"), url, sw, null);
        else
            Log(svc, WaveeLogLevel.Debug, "watchfeed.clip.play", "artist watch-feed clip started", url, sw, null);
    }

    static void Log(Services? svc, WaveeLogLevel level, string id, string message, string url,
                    System.Diagnostics.Stopwatch sw, Exception? ex)
    {
        if (svc?.Log is not { } log) return;
        log.Event(level, "watchfeed", id, message, url, sw.ElapsedMilliseconds, ex);
    }
}
