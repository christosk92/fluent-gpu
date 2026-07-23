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
/// overlay layer), anchored bottom-right by default, and hosts the SHARED <see cref="PopOutVideoStage"/> for the resolved
/// <see cref="PopOutVideoSource"/> — so the player logic (clear MF / PlayReady CDM) is reused verbatim, not duplicated.
/// Video composites here because the stage's <c>MediaPlayerElement</c> uses the PRIMARY window's AppHost registry (the
/// PiP lives INSIDE the main scene). Mutually exclusive with the detached pop-out window (both read the one resolved
/// source; only one plays at a time). Visibility is driven by <see cref="PlaybackBridge.ShowInWindowPip"/>.
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
    bool _placed;   // false = still anchored bottom-right (tracks the viewport); true = user has dragged it

    // Captured nodes for window-space pointer reconstruction (the grips move with the surface).
    NodeHandle _dragNode, _gripNode;
    // The ambient viewport signal, captured in Render so the gesture handlers (which run outside Render) can Peek it.
    IReadSignal<Size2>? _vpSig;
    // Drag/resize gesture anchors (window-space pointer at gesture start + the value being dragged).
    float _startX, _startY, _startW, _startH, _startPx, _startPy;

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        if (b is null) return new BoxEl();

        // Subscribe → mount/unmount the whole surface when the user toggles the in-window placement.
        if (!b.ShowInWindowPip.Value) return new BoxEl();

        var vp = UseContextSignal(Viewport.Size);
        _vpSig = vp;
        var source = b.PopOutVideoSource;   // the shared resolved source (clear URL / PlayReady descriptor)

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
                float x = _placed ? _x.Value : DefaultX(v, w);
                float y = _placed ? _y.Value : DefaultY(v, h);
                return Affine2D.Translation(ClampX(x, v, w), ClampY(y, v, h));
            }),
            Fill = Tok.MediaLetterbox,
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
                        BuildVideoArea(source),
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
            OnClick = () => { },              // an OnDrag node's click is its release/commit edge (drag-end)
            OnDragCanceled = () => { },
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
            OnClick = () => b.ShowInWindowPip.Value = false,
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
    static Element BuildVideoArea(IReadSignal<PopOutVideoSource?> source)
    {
        var src = source.Value;   // subscribe → remount the stage on a source change (clear ↔ DRM / track change)
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = Tok.MediaLetterbox,
            Children = src is null
                ? Array.Empty<Element>()
                : [ Embed.Comp(() => new PopOutVideoStage { Source = src }) with { Key = "pipstage:" + src.Key } ],
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
            OnClick = () => { },
            OnDragCanceled = () => { },
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
        float x = _placed ? _x.Peek() : DefaultX(vp, w);
        float y = _placed ? _y.Peek() : DefaultY(vp, h);
        _x.Value = ClampX(x, vp, w); _y.Value = ClampY(y, vp, h);
        _placed = true;
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
        _placed = true;
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
