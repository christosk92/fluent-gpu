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
/// <see cref="Source"/>/<see cref="Player"/> are FROZEN signals on purpose: app <c>Ctx.Provide</c> chains do NOT cross
/// the AppHost boundary (a detached window builds its own reconciler + ambient map), so the bridge's signals are handed
/// in directly. Reading them inside <c>Render</c> still subscribes normally — this window has its own render loop.
/// <see cref="Bridge"/>/<see cref="Settings"/> are frozen for the same boundary reason, but are stable INSTANCES rather
/// than signals — correct to freeze outright (no subscription needed; only the values reachable FROM them are live).
/// </summary>
sealed class PopOutVideoWindow : Component
{
    /// <summary>The resolved source (null = nothing yet — shows the letterbox background).</summary>
    public required IReadSignal<PopOutVideoSource?> Source { get; init; }

    /// <summary>The live video player owned by the backend media host (see <see cref="PlaybackBridge.VideoPlayer"/>). This
    /// window never builds a player — it binds to this one, so a placement flip re-binds instead of restarting from 0.</summary>
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }

    /// <summary>OPTIONAL — frozen because a detached window builds its own AppHost/reconciler and does NOT inherit the
    /// shell's <c>Ctx.Provide</c> chain, so <c>UseContext(PlaybackBridge.Slot)</c> would resolve to null in here even
    /// though the instance is perfectly stable (same reasoning as freezing <see cref="Player"/>). When present, the
    /// video's own transport More (⋯) menu gains the shared placement rows (<see cref="VideoPlacementMenu"/>) ahead of
    /// its own — see <see cref="PopOutVideoStage.Bridge"/>. When null (the caller has not threaded it through yet),
    /// the element's own More menu still works; it simply carries no placement rows.</summary>
    public PlaybackBridge? Bridge { get; init; }

    /// <summary>OPTIONAL, same freezing rationale as <see cref="Bridge"/> — needed only for the Always-on-top row.</summary>
    public IAppSettings? Settings { get; init; }

    /// <summary>Wrap the content in an overlay host so the transport's flyouts (speed, more, quality, CC, the volume
    /// slider) actually open. A detached window builds its OWN AppHost — its own reconciler and ambient context map —
    /// so the shell's OverlayHost never reaches it and <c>UseContext(Overlay.Service)</c> would resolve to
    /// <c>NullOverlayService</c>, making every one of those buttons a silent no-op.
    ///
    /// <para>The child MUST be a COMPONENT, never an inline element tree. <see cref="OverlayHost.Child"/> is
    /// <c>[MountOnceContent]</c> and <see cref="OverlayHost.Create"/> hands it to <c>Embed.Comp</c>, so it is built
    /// ONCE and frozen (the props-freeze contract — see docs/design/subsystems/component-props-contract.md). Passing
    /// the element tree directly froze it at the first render, when no player existed yet: the window then rendered an
    /// empty root FOREVER, so no <c>MediaPlayerElement</c> was ever mounted, nothing pumped the protected session, and
    /// the managed side sat at <c>Opening</c> until the start watchdog gave up — while the native log showed the video
    /// licensed, playing and feeding samples. A component re-renders itself, so its signal reads stay live.</para></summary>
    public override Element Render() =>
        OverlayHost.Create(Embed.Comp(() => new PopOutVideoContent { Source = Source, Player = Player, Bridge = Bridge, Settings = Settings }));
}

/// <summary>The pop-out's actual content, as a COMPONENT so it re-renders when the source/player signals change (see
/// the note on <see cref="PopOutVideoWindow.Render"/> for why this cannot be an inline element tree). Both props are
/// FROZEN signal instances — freezing a <c>Signal</c> is correct; freezing the values read out of one is not.</summary>
sealed class PopOutVideoContent : Component
{
    /// <inheritdoc cref="PopOutVideoWindow.Source"/>
    public required IReadSignal<PopOutVideoSource?> Source { get; init; }
    /// <inheritdoc cref="PopOutVideoWindow.Player"/>
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }
    /// <inheritdoc cref="PopOutVideoWindow.Bridge"/>
    public PlaybackBridge? Bridge { get; init; }
    /// <inheritdoc cref="PopOutVideoWindow.Settings"/>
    public IAppSettings? Settings { get; init; }

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
        return new BoxEl
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
                ? [new BoxEl { Grow = 1, Children = [Embed.Comp(() => new PopOutVideoStage { Source = src, Player = Player, Bridge = Bridge, Settings = Settings }) with { Key = "stage:" + stageKey }] }]
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
    /// <summary>Resolved source identity (may be briefly null while an override re-resolves — the stage stays mounted
    /// so MF keeps pumping; the parent overlays Loading when this is null).</summary>
    public PopOutVideoSource? Source { get; init; }
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }

    /// <summary>OPTIONAL — when present, wires <see cref="MediaPlayerElement.MoreMenuItems"/> with the shared
    /// <see cref="VideoPlacementMenu"/> rows (Fullscreen omitted: the element already has its own Fullscreen row,
    /// which delegates to the app, so including ours would duplicate it). All Wavee placement hosts thread this
    /// instance through; null remains a safe standalone fallback with only the element's playback rows.</summary>
    public PlaybackBridge? Bridge { get; init; }
    /// <summary>OPTIONAL, paired with <see cref="Bridge"/> — needed only for the Always-on-top row.</summary>
    public IAppSettings? Settings { get; init; }

    public override Element Render()
    {
        var binding = Player.Value;   // subscribe → re-bind when the host rebuilds/clears its player
        // The player vanished (host stopped) — render nothing; the owning surface unmounts this on the same pass.
        if (binding.Player is not { } player) return new BoxEl { Grow = 1f, MinHeight = 0f };
        var bridge = Bridge;
        var settings = Settings;
        return Embed.Comp(() => new MediaPlayerElement
            {
                Player = player, Stretch = MediaStretch.Uniform,
                AspectMode = bridge?.VideoAspectPolicy,
                CustomAspectRatio = bridge?.VideoCustomAspectRatio,
                AspectModeChanged = bridge is null ? null : bridge.SetVideoAspect,
                MoreMenuItems = bridge is null ? null : () => VideoPlacementMenu.Items(bridge, settings, includeFullscreen: false),
            })
            with { Key = "player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }
}
