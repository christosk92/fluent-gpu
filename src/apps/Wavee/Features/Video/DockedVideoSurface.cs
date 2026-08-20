using System;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee.Features.Video;

/// <summary>
/// The DOCKED music-video surface — the third face of the placement ladder, and the whole reason it exists: video
/// that simply LIVES in the app while the user browses, at zero commitment (no OS window, no overlay to dismiss).
/// This is <see cref="InWindowVideoPip"/> reduced BY SUBTRACTION, not a fresh design — every semantic that survives
/// below is the mini player's own, carried over verbatim:
///
/// <list type="bullet">
/// <item>The mount gate — visible IFF <see cref="PlaybackBridge.VideoPlacementNow"/> resolves to
///   <see cref="SurfacePlacement.Docked"/>, the ONE placement value, never a standalone flag.</item>
/// <item>The <c>UseSignalEffect</c> reality report — <see cref="PlaybackBridge.SetVideoSurfaceLive"/> tells the model
///   whether THIS surface is actually mounted, scoped to Docked only (the mirror of the PiP's Floating report).</item>
/// <item><see cref="BuildVideoArea"/>'s three-way branch: a live stage when a player exists, a Loading overlay stacked
///   over it while the resolved source is still null (a player must never stop pumping just because a manifest/DRM
///   round-trip is in flight), and a dimmed-artwork poster + spinner when there is no player at all.</item>
/// <item>The hover-reveal chrome idiom: the top scrim strip is <c>Opacity = 0, HoverOpacity = 1</c> and the card earns
///   HOVER-CONTAINER status with a no-op <c>OnPointerExit</c> (the TrackRow / PiP idiom) — one pointer registration,
///   the engine's hover cascade does the rest, no signal and no re-render. The Cap face ALSO mounts the stock
///   <see cref="MediaPlayerElement"/> transport (seek + More, including Aspect ratio) — the three-glyph strip is
///   placement chrome, not a replacement for that bar. The Art-tile face stays transport-off.</item>
/// </list>
///
/// <para><b>What is gone, and why.</b> A docked card is pinned inline layout, not a free-floating overlay: there is
/// no <c>_x/_y/_w/_h/_placed</c>, no bound <c>Transform</c>, no <c>Clamp*</c>/<c>Default*</c> anchor math, none of the
/// eight resize bands or <see cref="Wavee.Features.Video.InWindowVideoPip"/>'s <c>PipResizeEdge</c>, no 2D drag
/// gesture or the node bookkeeping they needed, no <c>VideoPipRect</c> persistence, no
/// <see cref="PlaybackBridge.FloatingSurfaceReserve"/> (a docked card reserves nothing — it costs real flex space, the
/// rail's own layout already accounts for it), no <see cref="Elevation.Flyout"/> shadow (docked is a content-layer
/// rung, never an elevated card — <c>RightRail.cs</c> states the identical rule for the rail itself), no pass-through
/// overlay wrapper of its own (this card is a normal flex child, not a top-Z layer), and no viewport subscription
/// (nothing here anchors to a corner). The Cap face's HEIGHT is the one exception: <c>RightRail</c> overlays the house
/// <c>Splitter</c> on the card's bottom edge so the user can grow past 16:9. Position and width still move only through
/// the placement ladder.</para>
///
/// <para><b>Never builds a player.</b> <see cref="PlaybackBridge.VideoPlayer"/> is presented, never constructed — the
/// same ownership inversion <see cref="InWindowVideoPip"/> and the pop-out window already rely on. Building our own
/// player here is exactly the mistake that restarts playback from 0 on every placement move.</para>
///
/// <para><b>Park-but-keep-pumping under immersive lyrics (B15).</b> <see cref="MediaPlayerElement"/> exposes no public
/// "are you active" prop — its OWN <c>_isActive</c> field (<c>MediaPlayerElement.cs</c> around the <c>PumpNow</c> park
/// check) is populated by the hooks-level <c>UseIsActive()</c>, which AND-folds the ambient
/// <see cref="Activation.IsActive"/> window-visibility signal with the component's own KeepAlive-parked state — there
/// is no per-instance settable field to assign. <see cref="Activation.IsActive"/> IS a real, overridable
/// <c>Context&lt;T&gt;</c> though (the same <c>Ctx.Provide</c> mechanism as any other), so this is the one lever that
/// actually exists: <see cref="_activeGate"/> re-derives the SAME window-visibility read (via <c>UseContext</c>, so a
/// minimized window still parks this surface exactly as it would anywhere else) AND-ed with "immersive lyrics is not
/// covering the rail", and re-provides it just for the <see cref="MediaPlayerElement"/> subtree below. A parked,
/// non-decorative element still calls <c>PumpVideo</c> (only <c>SetVisible(false)</c> is skipped-early), so MF keeps
/// advancing and the video picks up mid-song instead of restarting when immersive lyrics closes.</para>
///
/// <para><b>No <see cref="LayoutTransition"/>, ever, on this node or any ancestor added here.</b> The video composites
/// as a passive hole a DESCENDANT erases against the real back buffer (<c>DrawOp.DrawVideo</c>, a DestOut punch). An
/// ancestor <c>TransitionChannels.Opacity</c> multiplies straight into <c>DrawVideoCmd.Opacity</c> — a washed-out,
/// see-through video with the page bleeding through. An ancestor blur/edge-fade/opacity-GROUP pushes an offscreen RT —
/// the punch never reaches the real back buffer from inside one, so THE HOLE VANISHES ENTIRELY, silently and totally.
/// That is also exactly why this card must never be scrolled inside <c>NowPlayingPanel</c>'s
/// <c>ScrollView(...) with { AutoEdgeFade = true }</c> — see the docked-video design's §1 for the full three-reason
/// case. The rail's own 300ms <c>TranslateX</c> slide is the only motion this card ever rides, for free, because a
/// translate composes on the <c>AbsoluteRect</c> the punch already reads from — nobody needs to animate the hole for
/// the hole to move correctly.</para>
///
/// <para><b>Two faces, one card (<see cref="Face"/>).</b> <see cref="DockedVideoFace.Cap"/> is a full-bleed rail-width
/// tile whose height <c>RightRail</c> owns (16:9 floor, user-growable) and whose <see cref="MediaPlayerElement"/> mounts
/// the stock transport. <see cref="DockedVideoFace.ArtTile"/> wraps the SAME card (identical
/// <see cref="BuildVideoArea"/>/<see cref="BuildChrome"/> calls, identical stage-key prefix, identical reality report,
/// identical <see cref="_activeGate"/> narrowing) inside a fixed 324x324 square so the Details pinned hero
/// (<c>NowPlayingPanel.NowPlayingHeroTile</c>) never reflows when Art and Video swap — transport stays off on that
/// face. See <see cref="Render"/>'s tail for the geometry split and the plan's §2 "Art-tile face" for why the square,
/// not the card, must be what is fixed.</para>
/// </summary>
sealed class DockedVideoSurface : Component
{
    const float ScrimH = 30f;      // the hover-revealed top strip (three glyphs, right-aligned)
    const float GlyphBox = 24f;    // each glyph's square hit target, the InWindowVideoPip close-button rung
    const float ChromeFadeMs = WaveeMotion.Fast;

    /// <summary>Where does this card live? Cap/Takeover (the rail's full-bleed slot, RightRail's non-Details arm)
    /// vs Art tile (the Details pinned hero — the SAME video, letterboxed into a fixed 324x324 square so switching
    /// Art&lt;-&gt;Video never reflows the credits below it). Default is <see cref="DockedVideoFace.Cap"/> so the
    /// existing mount is unchanged; <c>NowPlayingPanel.NowPlayingHeroTile</c> is the one caller that sets ArtTile.</summary>
    public DockedVideoFace Face { get; init; }

    /// <summary>The Activation.IsActive OVERRIDE for this card's own <see cref="MediaPlayerElement"/> — see the class
    /// doc's "park-but-keep-pumping" section for why this, and not an invented prop, is the real lever. A stable
    /// instance (never reassigned) is load-bearing: <c>Ctx.Provide</c> only re-notifies existing subscribers when THIS
    /// signal's <c>.Value</c> changes, not when a fresh instance replaces it, and <c>UseIsActive()</c> resolves the
    /// provided instance once and subscribes to ITS value stream for the life of the element.</summary>
    readonly Signal<bool> _activeGate = new(true);

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var ui = UseContext(ShellUi.Slot);
        var svc = UseContext(Services.Slot);   // before the null-guard: hook order must not shift when context arrives
        if (b is null || ui is null) return new BoxEl();

        // The ambient window-visibility signal, read the SAME way UseIsActive reads it, so folding it back into
        // _activeGate below does not regress "a minimized window stops pumping" for this one surface — only the
        // "immersive lyrics is up" term is new.
        var windowVisible = UseContext(Activation.IsActive);
        UseSignalEffect(() =>
            _activeGate.Value = (windowVisible is null || windowVisible.Value) && !ui.ImmersiveLyrics.Value);

        // Reality + reports, scoped to Docked only (the mirror of InWindowVideoPip's Floating report) — no layout
        // reservation to publish: a docked card is inline flex, not a free-floating overlay reserving space nobody
        // else can see coming.
        UseSignalEffect(() => b.SetVideoSurfaceLive(SurfacePlacement.Docked, b.VideoPlacementNow() == SurfacePlacement.Docked));
        // Unmount discipline: if this whole surface goes away (logout / shell swap) while still reporting live, take
        // the report back — the model must not believe a card is mounted that no longer exists.
        UseEffect(() => () => b.SetVideoSurfaceLive(SurfacePlacement.Docked, false), DepKey.Empty);

        // Subscribe → mount/unmount the card as the ONE resolved placement changes. RightRail embeds this
        // unconditionally in both the Cap (Lyrics/Queue/Friends) and Takeover (Video) arms; THIS gate is what makes it
        // invisible (and Shrink=0f collapsed, so nothing reflows) the moment the video is anywhere else.
        if (b.VideoPlacementNow() != SurfacePlacement.Docked) return new BoxEl();

        void EnterFullscreen()
        {
            Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
            b.ShowVideoAt(SurfacePlacement.Fullscreen);
        }

        // The interactive video card ITSELF — video area + hover chrome, ZStack-overlaid — is shared VERBATIM between
        // both faces (the class doc's "two faces, one card" paragraph): only what wraps it, and at what aspect ratio,
        // differs below. Declared as BoxEl (not Element) so the `with` expressions below can reach BoxEl-only members
        // (Corners, ZStack, ...) — Element itself carries none of them.
        BoxEl card = new BoxEl
        {
            ZStack = true, ClipToBounds = true,
            Corners = Face == DockedVideoFace.ArtTile ? CornerRadius4.All(Radii.Card) : default,
            BorderWidth = Face == DockedVideoFace.ArtTile ? 1f : 0f,
            BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
            // NO Shadow: see the class doc — docked is a content-layer rung, never an elevated card.
            // NO Layout/Enter/Exit transition of any kind — see the class doc's motion paragraph. This is not an
            // oversight to "fix" later; adding one here is exactly the mistake that erases or washes out the hole.
            OnPointerExit = static () => { },   // hover-container registration only, the TrackRow/PiP idiom
            OnKeyDown = e =>
            {
                // Space = play/pause, mirroring MediaPlayerElement.HandleKey. Escape is deliberately NOT mirrored:
                // HandleKey's Escape case only fires `when IsFullscreenPresentation`, which this face never is — the
                // fullscreen surface (a separate, later phase) owns Escape for real.
                if (e.KeyCode != Keys.Space) return;
                e.Handled = true;
                if (b.VideoPlayer.Peek().Player is not { } p) return;
                if (p.IsPlayRequested.Peek()) _ = p.PauseAsync(); else _ = p.PlayAsync();
            },
            Focusable = true,
            Children = [ BuildVideoArea(b, EnterFullscreen, svc?.Settings), BuildChrome(b, EnterFullscreen, artTile: Face == DockedVideoFace.ArtTile) ],
        };

        if (Face == DockedVideoFace.ArtTile)
        {
            // Art-tile face (plan §2 "Art-tile face"): the SAME card, letterboxed inside a FIXED 324x324 square so
            // switching Art<->Video carries ZERO reflow of whatever scrolls beneath it. The outer square's own
            // AspectRatio never changes between states — only what paints in the fixed ~182-DIP middle band does —
            // so THIS node must never gain a height animation or a LayoutTransition of its own: a BoundsAnimated
            // outer here, or a SizeMode.Reveal/Reflow ancestor above it, is exactly the trap the class doc's motion
            // paragraph and the plan's motion table warn about. `Justify = Center` on a Direction=1 column with one
            // AspectRatio(16:9) child is what produces the two equal ~71-DIP bars for free, with no explicit spacer
            // boxes to keep in sync if Radii.Card or the tile width ever changes.
            return new BoxEl
            {
                Shrink = 0f, AspectRatio = 1f,
                Direction = 1, Justify = FlexJustify.Center,
                ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
                Fill = Tok.MediaLetterbox,   // the letterbox bars above/below the centred 16:9 card
                Children = [ card with { AspectRatio = 16f / 9f, Shrink = 0f } ],
            };
        }

        // Cap/Takeover face: full-bleed in the rail (the parent clips the top-left radius). Height is the SAME
        // FloatSignal RightRail's wrapper and the vertical splitter write — a declared size, not Grow=1 inside a
        // NaN-height ZStack (that measured as 0 once AspectRatio came off). Stretch defaults to Uniform (Fit); Crop
        // is a More-menu click when the user has grown the tile taller than 16:9.
        return card with
        {
            Shrink = 0f, MinWidth = 0f,
            Height = ui.DockedVideoHeight,
            Fill = Tok.MediaLetterbox,
            Corners = default, BorderWidth = 0f,
        };
    }

    // ── the video area — mirrors InWindowVideoPip.BuildVideoArea's three-way branch, built directly against
    // MediaPlayerElement (not the shared PopOutVideoStage: Cap uses the stock transport + a custom poster + the
    // fullscreen delegate; ArtTile stays transport-off inside the 324 square). ─────────────────────────────────────
    Element BuildVideoArea(PlaybackBridge b, Action enterFullscreen, IAppSettings? settings)
    {
        var src = b.PopOutVideoSource.Value;                          // subscribe → remount the stage on a source change
        var binding = b.VideoPlayer.Value;                            // subscribe → poster ↔ hole
        var track = b.CurrentTrack.Value;
        bool mount = VideoSurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
        if (mount && binding.Player is { } player)
        {
            bool cap = Face != DockedVideoFace.ArtTile;
            string stageKey = src?.Key ?? ("gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Element element = Embed.Comp(() => new MediaPlayerElement
            {
                Player = player,
                Stretch = MediaStretch.Uniform,            // house Fit; Crop/Stretch live in the transport More menu
                AspectMode = b.VideoAspectPolicy,
                CustomAspectRatio = b.VideoCustomAspectRatio,
                AspectModeChanged = b.SetVideoAspect,
                CornerRadius = Face == DockedVideoFace.ArtTile ? Radii.Card : 0f, // Cap is clipped by the rail; ArtTile keeps the inner round
                AreTransportControlsEnabled = cap,         // Art-tile hero stays transport-off (too small, must not reflow)
                ShowLetterboxBars = true,
                IsDecorative = false,                      // MUST stay false: decorative skips the pump while parked
                PosterContent = Poster(track),
                // ArtTile has no transport, but right-click/Menu-key still opens this same complete menu.
                MoreMenuItems = () => VideoPlacementMenu.Items(b, settings, includeFullscreen: false),
                // E3: F11 and the transport fullscreen button delegate to us instead of opening a second overlay.
                FullscreenRequested = enterFullscreen,
            }) with { Key = "dockstage:" + stageKey };
            // The Activation.IsActive override lives HERE, tight around the element that actually reads it — see the
            // class doc's park-but-keep-pumping paragraph for why this Ctx.Provide, and not a settable prop, is real.
            Element stage = Ctx.Provide<IReadSignal<bool>?>(FluentGpu.Hooks.Activation.IsActive, _activeGate, element);
            if (src is not null)
                return new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent, Children = [ stage ] };
            // Player present, source still resolving (a manifest/DRM round-trip in flight) — keep pumping under Loading.
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = ColorF.Transparent,
                Children = [ stage, LoadingOverlay() ],
            };
        }

        return Poster(track);
    }

    // The shared "no player yet" composition — the track's own artwork, dimmed, with a spinner. Used both as the
    // outer fallback (no player at all) and as MediaPlayerElement.PosterContent (shown until the element's own first
    // frame): a resolving manifest/DRM licence takes real time on every track change, and a black rectangle for those
    // seconds reads as broken rather than as loading.
    static Element Poster(Track? track) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
        Children =
        [
            new BoxEl { Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [ Surfaces.ArtworkFill(track?.Image, 0f) ] },
            LoadingOverlay(),
        ],
    };

    static Element LoadingOverlay() => new BoxEl
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

    // ── chrome — the hover-revealed top strip: pop out · fullscreen · close, right-aligned, 30 DIP tall. ────────────
    static Element BuildChrome(PlaybackBridge b, Action enterFullscreen, bool artTile) => new BoxEl
    {
        Grow = 1f, Direction = 1, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Height = ScrimH, Shrink = 0f, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XXS,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Gradient = Tok.ScrimTop,
                Corners = artTile ? new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f) : default,
                Opacity = 0f, HoverOpacity = 1f,
                HoverDurationMs = ChromeFadeMs, HoverEasing = Easing.FluentDecelerate,
                Children =
                [
                    Glyph(Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), () =>
                    {
                        Announcer.Say(Loc.Get(Strings.Player.VideoMiniPlayer));
                        b.ShowVideoAt(SurfacePlacement.Floating);
                    }),
                    Glyph(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), enterFullscreen),
                    Glyph(Icons.Cancel, Loc.Get(Strings.Player.TurnOffVideo), () =>
                    {
                        Announcer.Say(Loc.Get(Strings.Player.TurnOffVideo));
                        // Sticky off, via the model — never TurnVideoOff directly: NotifyVideoSurfaceClosed carries the
                        // stale-close identity guard PlacementCore.HostClosed needs to make an in-app close stick.
                        b.NotifyVideoSurfaceClosed(SurfacePlacement.Docked);
                    }),
                ],
            },
        ],
    };

    static Element Glyph(string glyph, string tip, Action onClick) => ToolTip.Wrap(new BoxEl
    {
        Width = GlyphBox, Height = GlyphBox, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Fill = ColorF.Transparent,
        HoverFill = Tok.OnMediaPrimary with { A = 0.14f },
        PressedFill = Tok.OnMediaPrimary with { A = 0.22f },
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = 11f, FontFamily = Theme.IconFont,
                Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary,
            },
        ],
    }, tip);
}

/// <summary>The two places a docked video can render (see <see cref="DockedVideoSurface.Face"/>). Both are the SAME
/// placement value (<see cref="SurfacePlacement.Docked"/>) and the SAME mounted surface — this enum only picks which
/// envelope wraps it, never a second gate on top of <see cref="PlaybackBridge.VideoPlacementNow"/>.</summary>
enum DockedVideoFace
{
    /// <summary>RightRail's non-Details arm: a full-bleed cap, pinned above the header. The default — every
    /// existing mount that does not set <see cref="DockedVideoSurface.Face"/> keeps this slot.</summary>
    Cap,

    /// <summary>The Details pinned hero (<c>NowPlayingPanel.NowPlayingHeroTile</c>): the same card letterboxed into
    /// a FIXED 324x324 square — 182 DIP of video framed by ~71-DIP <see cref="Tok.MediaLetterbox"/> bars top and
    /// bottom — so toggling Art&lt;-&gt;Video never changes the tile's own size and so never reflows the credits
    /// scrolling beneath it.</summary>
    ArtTile,
}
