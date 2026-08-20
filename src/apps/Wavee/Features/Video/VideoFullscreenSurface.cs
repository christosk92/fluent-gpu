using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee.Features.Video;

/// <summary>
/// The FULLSCREEN VIDEO surface — the full-bleed home for <see cref="SurfacePlacement.Fullscreen"/>. Modeled on
/// <see cref="ImmersiveLyricsSurface"/>, the shipping precedent for a full-bleed shell layer: a <c>Direction = 1</c>,
/// <c>Focusable</c>, <c>HitTestPassThrough</c> root with two live chrome bands (the window caption strip at top, the
/// docked player bar at bottom — see that surface's own remarks for why both stay reachable) and a childless hit
/// shield so a click on the exposed background cannot fall through to the page underneath.
///
/// <para>Content is the SHARED <see cref="PopOutVideoStage"/> (<c>PopOutVideoWindow.cs:98-112</c>) — the same stage
/// <see cref="InWindowVideoPip"/> and <c>PopOutVideoWindow</c> present, bound to <see cref="PlaybackBridge.VideoPlayer"/>
/// and keyed on the binding generation. This surface NEVER builds a <c>MediaPlayer</c>: the engine player is owned by
/// <c>FluentVideoMediaHost</c>, and every placement only BINDS a <see cref="FluentGpu.Controls.Media.MediaPlayerElement"/>
/// to it — which is what lets a placement move re-bind a presenter instead of restarting playback from 0.</para>
///
/// <para><b>NO OPACITY CHANNEL ON ANY ANCESTOR OF THE HOLE.</b> <c>DrawOp.DrawVideo</c> is a DestOut erase against the
/// back buffer; a fade on an ancestor multiplies cumulative opacity into it (a washed-out, translucent video with the
/// page bleeding through), and an <c>OpacityGroup</c>/blur/edge-fade ancestor is worse — it pushes an OFFSCREEN RT,
/// where the erase never reaches the real back buffer and the video vanishes entirely (the docked-video plan's §2
/// ancestor table). So <see cref="EnterTerminal"/>/<see cref="ExitTerminal"/> below carry a SCALE only — no
/// <c>Opacity</c> component — unlike <see cref="ImmersiveLyricsSurface"/>'s own terminals, which fade freely because
/// that stage has no video hole to protect. The scale terminal still carries the ⚠️ the same table names (the hole
/// scales about the node centre, the DirectComposition child does not, so there is a brief visual misalignment for the
/// ~200 ms of the transition) — accepted here because it is only a transient seam, not a total failure like a wash-out
/// or a vanished hole.</para>
/// </summary>
sealed class VideoFullscreenSurface : Component
{
    const SurfacePlacement Owned = SurfacePlacement.Fullscreen;   // the ONE placement this surface is responsible for

    const float TopBandH = 56f;      // the exit affordance's band — no identity/lyrics content here, just the way out
    const float ExitInset = 12f;

    NodeHandle _root;   // the surface root — the node the shield and the focus re-park park focus on

    /// <inheritdoc cref="ImmersiveLyricsSurface.EnterTerminal"/>
    /// <remarks>Scale-only — see the NO-OPACITY remark on the type doc comment. Reduced motion is the DEFAULT terminal
    /// (<c>Active = false</c>): a hard cut, not a same-value no-op animation.</remarks>
    internal static EnterExit EnterTerminal => Motion.ReducedMotion
        ? default
        : new EnterExit(Sx: 1.03f, Sy: 1.03f, Active: true);

    /// <inheritdoc cref="EnterTerminal"/>
    internal static EnterExit ExitTerminal => Motion.ReducedMotion
        ? default
        : new EnterExit(Sx: 1.02f, Sy: 1.02f, Active: true);

    /// <summary>Whether THIS mount was reached by an explicit user action (F11 / the menu's "Full screen" / the card's
    /// own fullscreen glyph) rather than an automatic reappearance — the surface's own analogue of the docked card's B12
    /// ("never re-opened by a track change"). <c>WaveeShell</c> is the one place that can tell the two apart: this
    /// component fully remounts every time <c>Flow.Show</c> toggles it, so it has no memory of its OWN history, while
    /// the shell never unmounts and can compare <c>PlaybackBridge.VideoSurface.Requested</c> across the transition
    /// (see the shell's own remarks beside where this is written). Peeked ONCE, at mount, never subscribed — a live
    /// value here would fight the focus system on every unrelated placement-state change. Null (not wired) defaults to
    /// TRUE, matching <see cref="ImmersiveLyricsSurface"/>'s unconditional take-focus-at-mount behaviour.</summary>
    public IReadSignal<bool>? UserInitiated { get; init; }

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var hooks = UseContext(InputHooks.Current);
        if (b is null) return new BoxEl();

        // Reality report: mirrors VideoPlacementHost's / InWindowVideoPip's own Owned/SetVideoSurfaceLive idiom for
        // the other two owned surfaces (Detached / Floating) — the model's Live field is written ONLY by the owner.
        UseSignalEffect(() => b.SetVideoSurfaceLive(Owned, mounted: b.VideoPlacementNow() == Owned));
        UseEffect(() => () => b.SetVideoSurfaceLive(Owned, mounted: false), DepKey.Empty);

        // Escape routes to the FOCUSED node and bubbles up its ancestors, so the surface takes focus once at mount —
        // but ONLY on a user-initiated open (see the UserInitiated doc comment). The root stays focusable (and does
        // NOT set AllowFocusOnInteraction=false) so a click on the surface's own background lands focus back here
        // rather than clearing it — Escape keeps working after any interaction.
        UseLayoutEffect(() =>
        {
            if (Context.HostNode.IsNull) return;
            if (UserInitiated?.Peek() ?? true) hooks.FocusNode?.Invoke(Context.HostNode, false);
        }, DepKey.Empty);

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            // Full-bleed POSITIONER: only the body band below is hittable, so the window caption strip and the docked
            // player bar keep receiving input through this layer — the same courtesy ImmersiveLyricsSurface pays.
            HitTestPassThrough = true,
            Focusable = true,
            OnRealized = h => _root = h,
            OnKeyDown = e =>
            {
                if (e.Handled || e.KeyCode != Keys.Escape) return;
                e.Handled = true;
                b.ExitVideoFullscreen();
            },
            // NEVER LEAVE FOCUS NULL WHILE THE SURFACE IS UP — see ImmersiveLyricsSurface's identical remark. Runs
            // regardless of UserInitiated: once mounted, an automatic reappearance still owns keyboard Escape and
            // must not strand focus at null the first time something else (the player bar) steals it.
            OnFocusChanged = got =>
            {
                if (got || _root.IsNull) return;
                if ((hooks.GetFocus?.Invoke() ?? default).IsNull) hooks.FocusNode?.Invoke(_root, false);
            },
            Children =
            [
                new BoxEl { Height = TitleBar.ExpandedHeight, Shrink = 0f, HitTestPassThrough = true },
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                    // Opaque floor: same token PopOutVideoContent/InWindowVideoPip's no-player fallback use. A STATIC
                    // fill, never an animated/transitioning one — see the NO-OPACITY-ANCESTOR remark on the type doc.
                    Fill = Tok.MediaLetterbox,
                    Children =
                    [
                        VideoArea(b),
                        Shield(hooks),
                        ExitChrome(b),
                    ],
                },
                new BoxEl { Height = WaveeSize.PlayerBarH, Shrink = 0f, HitTestPassThrough = true },
            ],
        };
    }

    /// <summary>The surface's HIT SHIELD — childless, full-bleed, input-only. Same contract as
    /// <see cref="ImmersiveLyricsSurface.Shield"/>: the video area and the exit chrome are the only real hit targets in
    /// this ZStack, so anywhere else (the letterbox bars, empty space around the video) must still take the click
    /// itself rather than let it fall through to whatever page the surface covers.</summary>
    Element Shield(InputHooks hooks) => new BoxEl
    {
        Key = "fs:shield",
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
        OnClick = () => { if (!_root.IsNull) hooks.FocusNode?.Invoke(_root, false); },
    };

    /// <summary>The video area — hosts the SHARED <see cref="PopOutVideoStage"/>, keyed on the source identity so it
    /// remounts cleanly on a track/source change. Poster + spinner cover the brief no-player window a placement MOVE
    /// leaves (close-then-open — B22), exactly like <see cref="InWindowVideoPip.BuildVideoArea"/>.</summary>
    static Element VideoArea(PlaybackBridge b)
    {
        var src = b.PopOutVideoSource.Value;      // subscribe → remount the stage on a source change
        var binding = b.VideoPlayer.Value;        // subscribe → poster ↔ hole
        bool mount = VideoSurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
        if (mount)
        {
            string stageKey = src?.Key ?? ("gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Bridge puts the placement ladder on the ⋯ menu so fullscreen is not a dead end — you can leave it FOR a
            // placement, not only back to wherever ReturnTo happens to point. Settings is deliberately null: the only
            // row that reads it is "Always on top", which is Detached-only and therefore unreachable from fullscreen.
            var stage = Embed.Comp(() => new PopOutVideoStage
            {
                Source = src, Player = b.VideoPlayer, Bridge = b,
            }) with { Key = "fsstage:" + stageKey };
            if (src is not null)
                return new BoxEl
                {
                    Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent,
                    Children = [stage],
                };
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = ColorF.Transparent,
                Children = [stage, LoadingOverlay(b.CurrentTrack.Value)],
            };
        }

        var track = b.CurrentTrack.Value;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
            Children =
            [
                new BoxEl { Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [Surfaces.ArtworkFill(track?.Image, 0f)] },
                LoadingOverlay(track),
            ],
        };
    }

    static Element LoadingOverlay(Track? _) => new BoxEl
    {
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.S,
        HitTestPassThrough = true,
        Children =
        [
            ProgressRing.Indeterminate(size: 20f, foreground: Tok.TextOnAccentPrimary),
            new TextEl(Loc.Get(Strings.Player.Loading))
            {
                Size = 12f, Weight = 600, Color = Tok.TextOnAccentPrimary,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The ONE way out — a top-right <see cref="StageChrome.ExitFab"/> (the "way out" shape: an ink-made
    /// ground with a card shadow, the one separation channel that survives an inverted ink ladder — see that shape's
    /// own remarks for why it, not <see cref="StageChrome.ScrimFab"/>, is the correct plate over undimmed video).
    /// The tooltip names the DESTINATION, not the current state — the label discipline the placement spec §3.2 sets
    /// out ("every control is named for its destination, never a bare verb"). Reusing the menu's "Full screen" label
    /// here would name the state the user is already in, which reads as a no-op control.</summary>
    static Element ExitChrome(PlaybackBridge b) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.End, Shrink = 0f,
        Height = TopBandH, Padding = new Edges4(0f, ExitInset, ExitInset, 0f),
        HitTestPassThrough = true,
        Children =
        [
            ToolTip.Wrap(
                StageChrome.ExitFab(Icons.BackToWindow, () => b.ExitVideoFullscreen()),
                Loc.Get(Strings.Player.VideoExitFullScreen)),
        ],
    };
}
