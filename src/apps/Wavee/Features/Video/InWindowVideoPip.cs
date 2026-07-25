using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.SpotifyLive;

namespace Wavee.Features.Video;

/// <summary>
/// An in-window, draggable + resizable picture-in-picture video surface. It floats over the shell (a top-Z, pass-through
/// overlay layer), anchored bottom-right by default, and hosts the SHARED <see cref="PopOutVideoStage"/>, which PRESENTS the
/// engine <c>MediaPlayer</c> owned by <c>FluentVideoMediaHost</c> (<see cref="PlaybackBridge.VideoPlayer"/>) for the resolved
/// <see cref="PopOutVideoSource"/>. NO surface builds a player — that ownership inversion is what keeps a placement move from
/// restarting the video from 0 and what makes the video's soundtrack the app's ONE current media.
/// Video composites here because the stage's <c>MediaPlayerElement</c> uses the PRIMARY window's AppHost registry (the
/// PiP lives INSIDE the main scene). Mutually exclusive with the detached pop-out window (exactly ONE mounted surface may
/// pump a given player). Visibility is derived: it mounts iff <see cref="PlaybackBridge.VideoPlacementNow"/> resolves
/// to <see cref="SurfacePlacement.Floating"/> — the ONE placement value, never a standalone flag. While it sits at its
/// default anchor it RESERVES layout (<see cref="PlaybackBridge.FloatingSurfaceReserve"/>) so it cannot cover page
/// content; dragging or resizing it releases the reservation, because placing it deliberately is the user opting into
/// a free-floating overlay.
///
/// Drag/resize follow the SidebarResizeGrip idiom: BoxEl.OnDrag uses the engine's eager pointer capture, so the gesture
/// keeps firing as the pointer leaves the thin grip; because the grip MOVES with the surface, the true window-space
/// pointer position is reconstructed each move as <c>local + scene.AbsoluteRect(handle)</c>. The surface is positioned
/// by a compositor-only <c>Transform</c> translation (layout stays put; only the paint/hit-test rect moves).
/// </summary>
sealed class InWindowVideoPip : Component
{
    // Tuning (DIP) — empirical, safe to tweak live.
    const float DefaultW = 360f, DefaultH = 202f;   // ~16:9
    const float MinW = 240f, MinH = 135f;           // sensible floor (16:9-ish)
    const float Margin = 16f;                        // gap from the window edges
    const float ChromeH = 32f;                       // the draggable title/close bar
    const float GripSize = 20f;                      // the bottom-right resize grip hit area

    // Live geometry (signals so the bound Transform / Width / Height re-fire on drag + resize).
    readonly Signal<float> _x = new(0f), _y = new(0f);
    readonly Signal<float> _w = new(DefaultW), _h = new(DefaultH);
    // false = still anchored bottom-right (tracks the viewport) AND reserving layout so it covers nothing; true = the
    // user has dragged it somewhere specific, which is them opting into a free-floating overlay. A signal because the
    // reservation effect must re-run on that edge.
    readonly Signal<bool> _placed = new(false);

    // Captured nodes for window-space pointer reconstruction (the grips move with the surface).
    NodeHandle _dragNode, _gripNode;
    // The ambient viewport signal, captured in Render so the gesture handlers (which run outside Render) can Peek it.
    IReadSignal<Size2>? _vpSig;
    // Drag/resize gesture anchors (window-space pointer at gesture start + the value being dragged).
    float _startX, _startY, _startW, _startH, _startPx, _startPy;

    /// <summary>The settings store (frozen at mount — a stable instance). Remembers where the user dragged/sized the mini
    /// player. Never remembers whether a video was playing.</summary>
    public IAppSettings? Settings { get; init; }
    bool _seeded;

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        if (b is null) return new BoxEl();

        // Restore the remembered geometry once, before the first layout, so it opens where the user left it rather than
        // at the default corner and then jumping. A remembered rect also means "deliberately placed", so it keeps its
        // free-floating behavior (no layout reservation) exactly as it had when the user put it there.
        if (!_seeded)
        {
            _seeded = true;
            if (Settings is { } st &&
                PlacementPersistence.TryLoadRect(st.Get(WaveeSettings.VideoPipRect),
                    out float px, out float py, out float pw, out float ph))
            {
                _x.Value = px; _y.Value = py;
                _w.Value = Math.Max(MinW, pw); _h.Value = Math.Max(MinH, ph);
                _placed.Value = true;
            }
        }

        var vp = UseContextSignal(Viewport.Size);
        _vpSig = vp;

        // Reality + layout reservation, in one place and OUTSIDE render (an effect, never a write during render):
        // report which placement is actually mounted, and publish how much bottom space the page must keep clear.
        // Anchored ⇒ reserve exactly our height + the gap, so the surface cannot cover content; dragged or gone ⇒ 0.
        UseSignalEffect(() =>
        {
            bool live = b.VideoPlacementNow() == SurfacePlacement.Floating;
            b.SetVideoSurfaceLive(SurfacePlacement.Floating, live);   // reports only its OWN placement
            b.FloatingSurfaceReserve.Value = live && !_placed.Value ? _h.Value + Margin : 0f;
        });
        // Give the reservation back on unmount (logout / shell swap), or the page would keep a hole for a surface that
        // no longer exists.
        UseEffect(() => () => b.FloatingSurfaceReserve.Value = 0f, DepKey.Empty);

        // Subscribe → mount/unmount the whole surface as the ONE resolved placement changes.
        if (b.VideoPlacementNow() != SurfacePlacement.Floating) return new BoxEl();

        // The floating surface. Compositor-only Transform positions it; layout keeps it at the layer origin.
        var surface = new BoxEl
        {
            Direction = 1, ClipToBounds = true, ZStack = true,
            Width = Prop.Of(() => _w.Value),
            Height = Prop.Of(() => _h.Value),
            Transform = Prop.Of(() =>
            {
                var v = vp.Value;                       // subscribe → re-clamp on window resize
                float w = _w.Value, h = _h.Value;       // subscribe → re-place on resize
                bool placed = _placed.Value;            // subscribe → re-anchor when the user first drags it
                float x = placed ? _x.Value : DefaultX(v, w);
                float y = placed ? _y.Value : DefaultY(v, h);
                return Affine2D.Translation(ClampX(x, v, w), ClampY(y, v, h));
            }),
            // Transparent: the video composites as a passive z-below hole, so nothing opaque may sit behind the video
            // rect (an opaque surface fill paints OVER it → black video). The chrome bar + MediaPlayerElement's
            // letterbox bars + the border/shadow give the PiP its solid framed look; the video area punches the hole.
            Fill = ColorF.Transparent,
            Corners = CornerRadius4.All(Radii.Card),
            BorderWidth = 1f,
            BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
            Shadow = Elevation.Flyout,
            Children =
            [
                // Layer 0 — the content column (chrome bar + video area).
                new BoxEl
                {
                    Direction = 1, Grow = 1f, MinHeight = 0f,
                    Children =
                    [
                        BuildChrome(b),
                        BuildVideoArea(b),
                    ],
                },
                // Layer 1 — the bottom-right resize grip, anchored via a pass-through filler layer.
                new BoxEl
                {
                    Grow = 1f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                    HitTestPassThrough = true,
                    Children = [ BuildGrip() ],
                },
            ],
        };

        // The full-bleed, pass-through overlay layer: only the surface itself takes input; everything else falls through
        // to the shell below (the runtime-banner / rail-overlay pattern).
        return new BoxEl
        {
            Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Children = [ surface ],
        };
    }

    // ── chrome (draggable title bar + close) ─────────────────────────────────────────────────────────
    Element BuildChrome(PlaybackBridge b)
    {
        var dragSurface = new BoxEl
        {
            Grow = 1f, Height = ChromeH, Direction = 0, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Cursor = CursorId.SizeAll,
            OnRealized = h => _dragNode = h,
            OnPointerDown = OnDragDown,
            OnDrag = OnDragMove,
            OnClick = PersistGeometry,        // an OnDrag node's click is its release/commit edge (drag-end)
            OnDragCanceled = PersistGeometry,
            Children =
            [
                new TextEl(Loc.Get(Strings.Player.NowPlaying))
                {
                    Size = 12f, Weight = 600, Color = Tok.TextSecondary,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };

        var close = new BoxEl
        {
            Width = ChromeH, Height = ChromeH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            Cursor = CursorId.Hand,
            // ✕ dismisses the video for THIS track (audio keeps playing, surface hides) — it does NOT clear the sticky
            // PreferVideo. The next track (or a "watch video" re-click → RestoreVideo) brings it back.
            OnClick = () => b.DismissVideoForCurrentTrack(),
            Children =
            [
                new TextEl(Icons.Cancel) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary },
            ],
        };

        return new BoxEl
        {
            Height = ChromeH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Fill = Tok.FillLayerAlt,
            Children = [ dragSurface, close ],
        };
    }

    // ── the video area — hosts the SHARED PopOutVideoStage, keyed on the source identity so it remounts cleanly ──
    // The stage PRESENTS the player owned by FluentVideoMediaHost (via PlaybackBridge.VideoPlayer); neither this surface nor
    // the stage builds one, which is why moving between the PiP and the pop-out no longer restarts the video from 0.
    //
    // There is exactly ONE state in which this box is transparent — a player is actually presenting, and the video needs
    // the transparent hole to composite through (it sits z-BELOW the UI swapchain, so anything opaque here paints OVER
    // it). In every OTHER state it paints a POSTER: the track's own artwork, dimmed, with a spinner. Resolving a manifest
    // and acquiring a DRM licence takes real time on every track change, and a surface that is a black rectangle for
    // those seconds reads as broken rather than as loading.
    static Element BuildVideoArea(PlaybackBridge b)
    {
        var src = b.PopOutVideoSource.Value;                          // subscribe → remount the stage on a source change
        bool live = src is not null && b.VideoPlayer.Value.Player is not null;   // subscribe → poster ↔ hole
        if (live)
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = ColorF.Transparent,
                Children = [ Embed.Comp(() => new PopOutVideoStage { Source = src!, Player = b.VideoPlayer }) with { Key = "pipstage:" + src!.Key } ],
            };

        var track = b.CurrentTrack.Value;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
            Children =
            [
                // The artwork fills the frame behind the spinner, dimmed enough to read as a placeholder rather than as
                // content. A track with no art degrades to the letterbox fill — still never an empty rect.
                new BoxEl { Grow = 1f, Opacity = 0.4f, ClipToBounds = true, Children = [ Surfaces.ArtworkFill(track?.Image, 0f) ] },
                new BoxEl
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
                },
            ],
        };
    }

    // ── the bottom-right resize grip ─────────────────────────────────────────────────────────────────
    Element BuildGrip()
        => new BoxEl
        {
            Width = GripSize, Height = GripSize, Direction = 0, AlignItems = FlexAlign.End, Justify = FlexJustify.End,
            Cursor = CursorId.SizeNWSE,
            OnRealized = h => _gripNode = h,
            OnPointerDown = OnResizeDown,
            OnDrag = OnResizeMove,
            OnClick = PersistGeometry,
            OnDragCanceled = PersistGeometry,
            Children =
            [
                // A small visual nub in the corner (paint-free hit area otherwise).
                new BoxEl
                {
                    Width = 10f, Height = 10f, Margin = new Edges4(0f, 0f, 3f, 3f), HitTestVisible = false,
                    Corners = new CornerRadius4(0f, 0f, Radii.Control, 0f),
                    Fill = Tok.TextTertiary,
                },
            ],
        };

    // ── drag (move) ──────────────────────────────────────────────────────────────────────────────────
    void OnDragDown(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
        var vp = ViewportPeek();
        // Commit the current effective position so the drag continues from where it is drawn (not the unplaced default).
        float w = _w.Peek(), h = _h.Peek();
        float x = _placed.Peek() ? _x.Peek() : DefaultX(vp, w);
        float y = _placed.Peek() ? _y.Peek() : DefaultY(vp, h);
        _x.Value = ClampX(x, vp, w); _y.Value = ClampY(y, vp, h);
        _placed.Value = true;   // → the layout reservation is released (the user placed it deliberately)
        _startX = _x.Peek(); _startY = _y.Peek();
        var abs = scene.AbsoluteRect(_dragNode);
        _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
    }

    void OnDragMove(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _dragNode.IsNull || !scene.IsLive(_dragNode)) return;
        var abs = scene.AbsoluteRect(_dragNode);       // the drag surface moves WITH the surface → reconstruct window-X/Y
        float px = local.X + abs.X, py = local.Y + abs.Y;
        var vp = ViewportPeek();
        _x.Value = ClampX(_startX + (px - _startPx), vp, _w.Peek());
        _y.Value = ClampY(_startY + (py - _startPy), vp, _h.Peek());
    }

    // ── resize (bottom-right corner) ─────────────────────────────────────────────────────────────────
    void OnResizeDown(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _gripNode.IsNull || !scene.IsLive(_gripNode)) return;
        _placed.Value = true;   // resizing is also deliberate placement → release the layout reservation
        _startW = _w.Peek(); _startH = _h.Peek();
        var abs = scene.AbsoluteRect(_gripNode);
        _startPx = local.X + abs.X; _startPy = local.Y + abs.Y;
    }

    void OnResizeMove(Point2 local)
    {
        var scene = Context.Scene;
        if (scene is null || _gripNode.IsNull || !scene.IsLive(_gripNode)) return;
        var vp = ViewportPeek();
        var abs = scene.AbsoluteRect(_gripNode);
        float px = local.X + abs.X, py = local.Y + abs.Y;
        float x = _x.Peek(), y = _y.Peek();
        // Clamp so the surface stays within the window (its top-left is fixed while the corner is dragged).
        float maxW = Math.Max(MinW, vp.Width - Margin - x);
        float maxH = Math.Max(MinH, vp.Height - Margin - WaveeSize.PlayerBarH - y);
        _w.Value = Math.Clamp(_startW + (px - _startPx), MinW, maxW);
        _h.Value = Math.Clamp(_startH + (py - _startPy), MinH, maxH);
    }

    // Written at the END of a move/resize gesture (an OnDrag node's click IS its release edge), so one write per gesture
    // rather than one per pointer sample. Only a DELIBERATELY placed surface is persisted: while it still sits at its
    // anchored home there is nothing to remember, and writing the computed anchor would freeze it against a later window
    // resize (it would stop tracking the corner).
    void PersistGeometry()
    {
        if (Settings is not { } settings || !_placed.Peek()) return;
        settings.Set(WaveeSettings.VideoPipRect,
            PlacementPersistence.SaveRect(_x.Peek(), _y.Peek(), _w.Peek(), _h.Peek()));
    }

    // ── placement helpers ────────────────────────────────────────────────────────────────────────────
    static float DefaultX(Size2 vp, float w) => vp.Width - w - Margin;
    static float DefaultY(Size2 vp, float h) => vp.Height - h - WaveeSize.PlayerBarH - Margin;

    static float ClampX(float x, Size2 vp, float w)
        => Math.Clamp(x, Margin, Math.Max(Margin, vp.Width - w - Margin));
    static float ClampY(float y, Size2 vp, float h)
        => Math.Clamp(y, Margin, Math.Max(Margin, vp.Height - h - WaveeSize.PlayerBarH - Margin));

    // Viewport size without a subscription — used inside the gesture handlers (they run outside Render).
    Size2 ViewportPeek() => _vpSig?.Peek() ?? new Size2(1280f, 720f);
}
