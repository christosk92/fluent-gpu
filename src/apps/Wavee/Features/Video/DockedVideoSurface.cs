using System;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Media;
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
///   the engine's hover cascade does the rest, no signal and no re-render. The three-glyph strip is placement chrome;
///   the TRANSPORT is the global 72-DIP player bar, which owns it for every in-window placement
///   (<see cref="PlacementCore.TransportOwnerFor"/>) — so both faces pass
///   <see cref="MediaPlayerElement.SuppressTransport"/> and the card never stacks a second scrub row above the bar.
///   The Cap face still mounts the element's More (⋯) affordances (Aspect ratio + the placement ladder).</item>
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
/// (nothing here anchors to a corner). The Cap face's HEIGHT is the one exception: at rest it FOLLOWS THE CONTENT —
/// <c>ShellResponsiveLayout.FitDockedVideoHeight</c> of the player's <c>NaturalSize</c> at the rail's width, so a video
/// fills the card edge to edge rather than sitting in letterbox bars (the Details arm mounts the ArtTile face instead,
/// where the square is fixed by design and the ELEMENT'S own aspect fit decides what shows inside it) — and <c>RightRail</c> overlays the house
/// <c>Splitter</c> on the card's bottom edge so the user can override that fit for the source that is playing. Position
/// and width still move only through the placement ladder.</para>
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
/// tile whose height follows the playing content's own aspect (splitter-overridable, clamped to the rail's floor/ceiling).
/// <see cref="DockedVideoFace.ArtTile"/> wraps the SAME card (identical
/// <see cref="BuildVideoArea"/>/<see cref="BuildChrome"/> calls, identical stage-key prefix, identical reality report,
/// identical <see cref="_activeGate"/> narrowing) inside a fixed SQUARE so the Details pinned hero
/// (<c>NowPlayingPanel.NowPlayingHeroTile</c>) never reflows when Art and Video swap. The card FILLS that square: the
/// letterbox is the element's own (<see cref="MediaPlayerElement.ShowLetterboxBars"/>, computed from the real natural
/// size), never a wrapper the element cannot see — which is what makes the Aspect-ratio menu real on this face too.
/// BOTH faces mount the stock transport; the ONE gate is <c>PlaybackBridge.TransportOwnerNow</c>, and the two faces are
/// mutually exclusive mounts (RightRail's Details arm vs every other arm). See <see cref="Render"/>'s tail for the
/// geometry split and the plan's §2 "Art-tile face" for why the square, not the card, must be what is fixed.</para>
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

    /// <summary>The <c>PopOutVideoSource.Key</c> the cap height was last fitted for (Cap face only). A change means a
    /// NEW source, which is what ends the previous splitter drag's override — see the height-fit effect in
    /// <see cref="Render"/>.</summary>
    string? _fittedFor;

    /// <summary>The ALWAYS-ON fit report (no env switch) — the four numbers that decide whether the Cap card is the
    /// content's own shape or a letterboxing box. Value-gated on the whole tuple so the effect can run freely.</summary>
    static readonly WaveeLogger FitLog = new(WaveeLog.Instance, "video");
    (string Key, float RailW, int Nw, int Nh, float H, bool Pinned) _loggedFit;
    (VideoAspectMode Mode, double Custom, TransportOwner Owner) _loggedPolicy = ((VideoAspectMode)255, -1, (TransportOwner)255);

    void LogFit(string key, float railW, SizeI natural, float height, bool pinned)
    {
        var now = (key, railW, natural.Width, natural.Height, height, pinned);
        if (now == _loggedFit) return;
        _loggedFit = now;
        FitLog.Info($"docked cap fit face={Face} rail={railW:0.#} natural={natural.Width}x{natural.Height} " +
                    $"height={height:0.##} pinned={pinned} key={(key.Length == 0 ? "(none)" : key)}");
    }

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

        // ── the card follows the CONTENT'S aspect ────────────────────────────────────────────────────────────────
        // The Cap face is full-bleed at the rail's width, so its HEIGHT is the only thing that decides whether a video
        // fills it or sits in Tok.MediaLetterbox bars. Sizing it purely from the rail's persisted/dragged height made
        // letterboxing the DEFAULT for anything that was not exactly as tall as whatever was left there — a 16:9
        // YouTube stream in a taller card showed bars above and below. So at rest the height IS the content fit
        // (16:9 until the player reports a natural size, so nothing flashes at the wrong shape), and an explicit
        // splitter drag pins it — for THIS source only, because a decision about one video is not a standing one.
        // Written from an effect, never during render; the ArtTile face is a fixed square and never participates.
        UseSignalEffect(() =>
        {
            string sourceKey = b.PopOutVideoSource.Value?.Key ?? "";      // subscribe → a new source re-fits
            var natural = b.VideoPlayer.Value.Player?.NaturalSize.Value ?? default;   // subscribe → fit when MF reports
            float railW = ui.RailWidth.Value;                             // subscribe → re-fit as the rail resizes
            if (Face != DockedVideoFace.Cap) return;
            if (!string.Equals(sourceKey, _fittedFor, StringComparison.Ordinal))
            {
                _fittedFor = sourceKey;
                if (ui.DockedVideoHeightPinned.Peek()) ui.DockedVideoHeightPinned.Value = false;
            }
            if (ui.DockedVideoHeightPinned.Peek()) { LogFit(sourceKey, railW, natural, ui.DockedVideoHeight.Peek(), true); return; }
            float fitted = ShellResponsiveLayout.FitDockedVideoHeight(railW, natural.Width, natural.Height);
            ui.DockedVideoHeight.Value = fitted;
            LogFit(sourceKey, railW, natural, fitted, false);
        });

        // ALWAYS-ON aspect/transport report (no env switch): the two policy values whose effect on this card is
        // otherwise only visible as pixels — the aspect mode the element is driven with, and who owns the transport
        // (which is what decides whether the element mounts its hover chrome at all).
        UseSignalEffect(() =>
        {
            var mode = b.VideoAspectPolicy.Value;
            double custom = b.VideoCustomAspectRatio.Value;
            var owner = b.TransportOwnerNow.Value;
            var now = (mode, custom, owner);
            if (now == _loggedPolicy) return;
            _loggedPolicy = now;
            FitLog.Info($"docked policy face={Face} aspect={mode} custom={custom:0.###} transportOwner={owner} " +
                        $"transportSuppressed={owner != TransportOwner.Docked}");
        });

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
                if (p.IsPlayRequested.Peek()) _ = b.Player.PauseAsync(); else _ = b.Player.ResumeAsync();
            },
            Focusable = true,
            Children = [ BuildVideoArea(b, EnterFullscreen, svc?.Settings), BuildChrome(b, EnterFullscreen, artTile: Face == DockedVideoFace.ArtTile) ],
        };

        if (Face == DockedVideoFace.ArtTile)
        {
            // Art-tile face (plan §2 "Art-tile face"): the SAME card inside a FIXED square (the rail's content width)
            // so switching Art<->Video carries ZERO reflow of whatever scrolls beneath it. The outer square's own
            // AspectRatio never changes between states — only what paints inside it does — so THIS node must never
            // gain a height animation or a LayoutTransition of its own: a BoundsAnimated outer here, or a
            // SizeMode.Reveal/Reflow ancestor above it, is exactly the trap the class doc's motion paragraph and the
            // plan's motion table warn about.
            //
            // THE CARD FILLS THE SQUARE, and the letterbox is the ELEMENT'S. This used to centre a `Shrink=0f` card
            // with a hard-coded `AspectRatio = 16f/9f` inside a `Justify = Center` column and call the surrounding
            // Tok.MediaLetterbox fill "the bars". Two defects came out of that, both observed on a 1920x1080 live
            // stream in a 326-DIP rail:
            //   (i)  the bars were OUTSIDE the MediaPlayerElement, so its Aspect-ratio menu could not reach them —
            //        Stretch and Crop visibly did NOTHING because they only re-fitted the frame inside a box that was
            //        already the frame's own shape;
            //   (ii) the inner card's AspectRatio was not what sized it at all. Measured inside this ZStack it fell
            //        back to its CONTENT, i.e. MediaPlayerElement's own `MinHeight = 160f` video-area floor, so the
            //        card came out 326x160 (aspect 2.04, logged as `place=...,326,160`) — a frame stretched into the
            //        wrong shape between 83-DIP bars, not the designed ~71.
            // Handing the whole square to the element fixes both at once: at Fit it paints its own LetterboxColor
            // (the same Tok.MediaLetterbox token) bars computed from the REAL natural size — so a 4:3 or a vertical
            // video is framed correctly instead of against a hard-coded 16:9 — and Stretch/Crop fill the square edge
            // to edge, which is what the menu has always promised.
            return new BoxEl
            {
                Shrink = 0f, AspectRatio = 1f,
                Direction = 1,
                ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
                Fill = Tok.MediaLetterbox,   // the ground under the element (its own letterbox paints the same token)
                Children = [ card with { Grow = 1f, MinHeight = 0f } ],
            };
        }

        // Cap/Takeover face: full-bleed in the rail (the parent clips the top-left radius). Height is the SAME
        // FloatSignal RightRail's wrapper, the vertical splitter and the content-fit effect above all write — a
        // declared size, not Grow=1 inside a NaN-height ZStack (that measured as 0 once AspectRatio came off).
        // Stretch stays Uniform (Fit): with the height fitted to the content there are no bars to fit INSIDE, and Crop
        // remains a More-menu click for when the user has deliberately grown the tile past the content's own shape.
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
            // ONE transport per session. The docked card shares the window with the global 72-DIP player bar, and
            // PlacementCore.TransportOwnerFor(Docked) hands the transport to the BAR — full-width, always visible,
            // never scrolled away — so the card SUPPRESSES its own rather than stacking a second scrub row 30 DIP above
            // it. Read through the ONE derived signal, never a local bool: two independent visibility flags is exactly
            // how the fullscreen surface and the bar ended up rendering both at once.
            bool suppress = b.TransportOwnerNow.Value != TransportOwner.Docked;
            string stageKey = src?.Key ?? ("gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Element element = Embed.Comp(() => new MediaPlayerElement
            {
                Player = player,
                PlayRequested = () => _ = b.Player.ResumeAsync(),
                PauseRequested = () => _ = b.Player.PauseAsync(),
                // The seek MODE travels with the target (scrub-in-flight = Keyframe, commit = Accurate); dropping it
                // forced every seek down the accurate path.
                SeekRequested = (target, mode) => _ = b.Player.SeekAsync((long)target.TotalMilliseconds, mode),
                Stretch = MediaStretch.Uniform,            // house Fit; Crop/Stretch live in the transport More menu
                AspectMode = b.VideoAspectPolicy,
                CustomAspectRatio = b.VideoCustomAspectRatio,
                AspectModeChanged = b.SetVideoAspect,
                CornerRadius = Face == DockedVideoFace.ArtTile ? Radii.Card : 0f, // Cap is clipped by the rail; ArtTile keeps the inner round
                // ONE gate, and it is the derived owner signal below. This was `Face != ArtTile`, a SECOND independent
                // flag — and it is what made the Details hero a picture with no controls: hovering it revealed nothing
                // because MediaPlayerElement only adds its chrome layer when
                // `AreTransportControlsEnabled && !SuppressTransport`, and the first term was hard-false here. The
                // chrome is an auto-hiding ZStack OVERLAY pinned to the card's bottom edge, so it reflows nothing (the
                // reason the flag was introduced does not apply), and the two faces are mutually exclusive mounts —
                // RightRail's Details arm mounts THIS face, its every other arm mounts the Cap — so the single
                // transport per window still follows from PlacementCore.TransportOwnerFor alone.
                AreTransportControlsEnabled = true,
                SuppressTransport = suppress,
                ShowLetterboxBars = true,
                IsDecorative = false,                      // MUST stay false: decorative skips the pump while parked
                PosterContent = Poster(track),
                // The transport's More button, right-click and the Menu key all open this same complete menu.
                MoreMenuItems = () => VideoPlacementMenu.Items(b, settings, includeFullscreen: false),
                // E3: F11 / F / the transport fullscreen button / the ⋯ Fullscreen row delegate to us instead of
                // opening MediaPlayerElement's own modal overlay fullscreen.
                FullscreenRequested = enterFullscreen,
            }) with { Key = "dockstage:" + stageKey + (suppress ? ":t0" : ":t1") };
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

    /// <summary>The Details pinned hero (<c>NowPlayingPanel.NowPlayingHeroTile</c>): the same card FILLING a fixed
    /// square (the rail's content width), so toggling Art&lt;-&gt;Video never changes the tile's own size and so never
    /// reflows the credits scrolling beneath it. The bars are the element's own
    /// <see cref="Tok.MediaLetterbox"/> fit — Fit frames a 16:9 stream with ~71-DIP bars top and bottom, and
    /// Stretch/Crop fill the square edge to edge.</summary>
    ArtTile,
}
