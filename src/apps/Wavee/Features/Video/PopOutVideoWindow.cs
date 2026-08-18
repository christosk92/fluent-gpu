using System;
using FluentGpu.Controls;
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
        var binding = Player.Value;             // subscribe → repaint the plate when the player arrives
        // Mount whenever a player exists — a brief source null must not unmount the only MF pump.
        bool live = VideoSurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
        string stageKey = src?.Key ?? ("gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // WRAPPED IN AN OVERLAY HOST. A detached window builds its OWN AppHost — its own reconciler and its own ambient
        // context map — so nothing from the shell's tree reaches it, including the shell's OverlayHost. Without one here
        // `UseContext(Overlay.Service)` resolves to NullOverlayService, and every flyout the transport owns silently does
        // nothing: the speed (1×) and more (…) buttons were dead, and the volume button fell through to a bare mute
        // because its slider flyout could never open. The host must wrap the CONTENT (it renders a top-level ZStack over
        // it), so it is the outermost element here.
        return OverlayHost.Create(new BoxEl
        {
            Direction = 1,
            Width = Prop.Of(() => vp.Value.Width),
            Height = Prop.Of(() => vp.Value.Height),
            // ALWAYS opaque — including while live. The video composites as a passive hole punched by a DESCENDANT
            // (MediaPlayerElement's VideoHole node), and that punch is a DestOut erase: it zeroes the UI back buffer
            // over the video rect, which removes this fill there just as it removes the element's own letterbox fill.
            // So an opaque root does NOT cause the black-video bug — only something painting AFTER the hole, i.e. a
            // later sibling or higher z, can do that.
            // It used to be transparent while live, on the assumption the element would cover everything around the
            // video. It does not: the element draws a ROUNDED, BORDERED frame, so its corners and any slack between
            // frame and window were never painted by anyone — and in a composited window "not painted" means the
            // DESKTOP shows through. That was the wallpaper-coloured strip under the titlebar.
            Fill = Tok.MediaLetterbox,
            Children = live
                ? [new BoxEl { Grow = 1, Children = [Embed.Comp(() => new PopOutVideoStage { Source = src, Player = Player }) with { Key = "stage:" + stageKey }] }]
                : Array.Empty<Element>(),
        });
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
    /// <summary>Resolved source identity (may be briefly null while an override re-resolves — the stage stays mounted
    /// so MF keeps pumping; the parent overlays Loading when this is null).</summary>
    public PopOutVideoSource? Source { get; init; }
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
