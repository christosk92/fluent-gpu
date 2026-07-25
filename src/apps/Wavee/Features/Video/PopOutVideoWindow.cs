using System;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.SpotifyLive;

namespace Wavee.Features.Video;

/// <summary>
/// Root content of the detached, always-on-top pop-out video window (its own composited AppHost + swapchain + video
/// presenter). Reads the resolved <see cref="PopOutVideoSource"/> for the CONTENT IDENTITY and mounts a keyed
/// <see cref="PopOutVideoStage"/>, which PRESENTS the player owned by <c>FluentVideoMediaHost</c>. The OS window frame
/// handles move/resize/close; the host sets always-on-top.
///
/// Both props are FROZEN signals on purpose: app <c>Ctx.Provide</c> chains do NOT cross the AppHost boundary (a detached
/// window builds its own reconciler + ambient map), so the bridge's signals are handed in directly. Reading them inside
/// <c>Render</c> still subscribes normally — this window has its own render loop.
/// </summary>
sealed class PopOutVideoWindow : Component
{
    /// <summary>The resolved source (null = nothing yet — shows the letterbox background).</summary>
    public required IReadSignal<PopOutVideoSource?> Source { get; init; }

    /// <summary>The live video player owned by the backend media host (see <see cref="PlaybackBridge.VideoPlayer"/>). This
    /// window never builds a player — it binds to this one, so a placement flip re-binds instead of restarting from 0.</summary>
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }

    public override Element Render()
    {
        // Size the root to THIS window's viewport (the AppHost does NOT auto-stretch a scene root — a bare Grow=1 hugs to
        // 0×0; WaveeShell fills the same way).
        var vp = UseContextSignal(Viewport.Size);
        var src = Source.Value;                 // subscribe → remount the stage on a source change
        var player = Player;
        bool live = src is not null && Player.Value.Player is not null;   // subscribe → repaint the plate when the player arrives
        return new BoxEl
        {
            Direction = 1,
            Width = Prop.Of(() => vp.Value.Width),
            Height = Prop.Of(() => vp.Value.Height),
            // The video composites as a PASSIVE HOLE: the DComp video sits z-BELOW the UI swapchain, so the video rect
            // must stay TRANSPARENT (premul-0) for it to show through. An opaque fill here paints OVER the video (the
            // black-video bug). So fill opaque until the stage is actually presenting a player (keeps the window from
            // being see-through while the host resolves/opens); once it is, MediaPlayerElement paints the opaque
            // letterbox bars AROUND the video rect and leaves the rect itself the transparent hole.
            Fill = live ? ColorF.Transparent : Tok.MediaLetterbox,
            Children = live
                ? [new BoxEl { Grow = 1, Children = [Embed.Comp(() => new PopOutVideoStage { Source = src!, Player = player }) with { Key = "stage:" + src!.Key }] }]
                : Array.Empty<Element>(),
        };
    }
}

/// <summary>One video SURFACE for a FROZEN source identity (props freeze at mount; the parent remounts this on a source
/// change). It does NOT own a player: the engine <c>MediaPlayer</c> — clear MF backend or clear+DRM (native PlayReady CDM) —
/// is built and owned by <c>FluentVideoMediaHost</c>, and this surface only binds a <see cref="MediaPlayerElement"/> to it.
/// That inversion is what fixes both M0 defects: the video's soundtrack is the ONE current media (so the song stops), and a
/// placement move re-binds a presenter instead of rebuilding a player (so playback does not restart from 0).
///
/// <c>MediaPlayerElement.Player</c> is a frozen-at-mount prop, so the element is KEYED on the binding generation: when the
/// host rebuilds its player the element remounts against the new instance. Exactly ONE mounted surface may pump a given
/// player (the MF session only advances while a mounted element pumps it); the single-placement state guarantees that.</summary>
sealed class PopOutVideoStage : Component
{
    public required PopOutVideoSource Source { get; init; }
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }

    public override Element Render()
    {
        var binding = Player.Value;   // subscribe → re-bind when the host rebuilds/clears its player
        // The player vanished (host stopped) — render nothing; the owning surface unmounts this on the same pass.
        if (binding.Player is not { } player) return new BoxEl { Grow = 1f, MinHeight = 0f };
        return Embed.Comp(() => new MediaPlayerElement { Player = player, Stretch = MediaStretch.Uniform })
            with { Key = "player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }
}
