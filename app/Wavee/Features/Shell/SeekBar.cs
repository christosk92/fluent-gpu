using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The center scrub bar — a bespoke media-seek control that REPLACES the old Slider.Bind seek in the player bar. Three
// reasons it isn't a Slider:
//   1. THE SCRUB GATE (the bug fix). While the user is dragging, the displayed fraction must IGNORE PositionFrac so the
//      1 Hz position tick can't yank the thumb back under the finger. Slider.Bind reads one signal; we need a derived
//      fraction (scrubbing ? scrubFrac : playing ? interpolated : positionFrac).
//   2. SMOOTH PLAYHEAD. The transport only reports position ~1 Hz, so a raw bind steps once a second (jerky). While
//      playing we interpolate between ticks off the per-frame FrameClock, anchored to the wall-clock at the last tick.
//   3. CLICK-ANYWHERE SCRUB modeled on ScrollBar's thumb-drag (grab on down, normalized 0..1 on drag), committing on
//      the drag-end (OnClick) so we issue ONE SeekAsync, not one per move.
//
// Cost discipline (signals-first): this is a leaf sub-component. The fill/thumb position is a compositor Transform BIND
// reading ONE signal (_displayFrac) — it NEVER re-renders this component. While playing, a mounted SeekTicker advances
// _displayFrac per frame (the ScrollBar conscious-ticker idiom: a FrameClock consumer that writes a signal the binds
// read); when paused/stopped the ticker is unmounted and the frame loop idles. The component re-renders only on the
// LOW-frequency state it reads in Render (playing/enabled), never per move or per frame.
sealed class SeekBar : Component
{
    const float HitHeight = 32f;     // WinUI SliderHorizontalHeight
    static readonly bool DiagEnabled = Diag.EnvFlag("WAVEE_PLAYERBAR_DIAG");
    static int s_renderCount;
    static int s_boundsCount;

    readonly PlaybackBridge _b;

    // While scrubbing the fill follows _scrubFrac and ignores PositionFrac (no 1 Hz snap-back).
    readonly Signal<bool> _scrubbing = new(false);
    readonly FloatSignal _scrubFrac = new(0f);
    // The single value the fill/thumb compositor binds read. Advanced per frame by SeekTicker while playing; set
    // directly from PositionFrac when paused; set to the finger position while scrubbing.
    readonly FloatSignal _displayFrac = new(0f);

    NodeHandle _self;
    NodeHandle _thumb;
    float _width;            // live track width (px), refreshed from arranged bounds and each pointer-down
    long _tickWallMs;        // Environment.TickCount64 at the last position tick — the interpolation anchor
    long _tickPosMs;         // PositionMs at the last tick

    public SeekBar(PlaybackBridge b)
    {
        _b = b;
    }

    // Re-derive _displayFrac from the current model — called by the ticker every frame while playing, and once from a
    // mount effect so a paused/stopped bar still shows the right resting position. Zero alloc.
    internal void Recompute()
    {
        if (_scrubbing.Peek()) { _displayFrac.Value = _scrubFrac.Peek(); return; }   // scrub gate: ignore PositionFrac
        if (_b.IsPlaying.Peek() && !_b.IsBuffering.Peek())
        {
            long dur = _b.DurationMs.Peek();
            if (dur <= 0L) { _displayFrac.Value = 0f; return; }
            long est = _tickPosMs + (Environment.TickCount64 - _tickWallMs);   // interpolate between 1 Hz ticks
            float frac = Math.Clamp(est / (float)dur, 0f, 1f);
            // Quantize to whole-pixel granularity of the live track: a multi-minute track's playhead moves a few px/s, so
            // most ticker frames land on the SAME pixel. Snapping _displayFrac to that pixel makes those frames write no
            // transform → a byte-identical DrawList → the host's skip-submit gate elides the redundant GPU submit+present
            // (the dominant at-rest cost), while a real pixel step still advances smoothly. Raw fraction when width unknown.
            float w = _width;
            float q = w > 1f ? MathF.Round(frac * w) / w : frac;
            if (q != _displayFrac.Peek()) _displayFrac.Value = q;   // value-gate: an unmoved pixel is a true no-op (no bind re-run)
            return;
        }
        _displayFrac.Value = _b.PositionFrac.Peek();   // paused/stopped: static, the reported position
    }

    public override Element Render()
    {
        var b = _b;

        // Derive `enabled` REACTIVELY from the bridge signals (NOT a ctor-frozen field). The reconciler reuses a mounted
        // ComponentEl across re-renders without re-invoking the factory (Reconciler.Update: same ComponentType → early
        // return), so a ctor-frozen flag would stick at its first-mount value (false, before the track resolves) forever.
        // Reading the signals here re-renders the bar on the enabling transition, which re-installs the interaction
        // handlers (OnClick/OnPointerDown/OnDrag run on every reconcile). Mirrors PlayerBar's `active`.
        bool enabled = b.CurrentTrack.Value != null && b.Error.Value == null && !b.IsLoading.Value && b.CanSeek.Value;

        // Subscribe to the LOW-frequency signals that change the bar's STRUCTURE (mount/unmount the ticker) only.
        bool playing = b.IsPlaying.Value;
        bool buffering = b.IsBuffering.Value;
        long posTick = b.PositionMs.Value;   // subscribe → re-anchor the interpolation each ~1 Hz tick
        if (DiagEnabled)
            WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "seekbar.render", "Seek bar rendered",
                fields:
                [
                    WaveeLogField.Of("render", ++s_renderCount),
                    WaveeLogField.Of("enabled", enabled),
                    WaveeLogField.Of("playing", playing),
                ]);

        // Anchor the smooth-playhead interpolation: snapshot wall + position whenever PositionMs changes, then refresh
        // the resting display (covers the paused/seek-while-paused case — the ticker isn't mounted then).
        UseEffect(() =>
        {
            _tickWallMs = Environment.TickCount64;
            _tickPosMs = b.PositionMs.Peek();
            Recompute();
        }, posTick);

        // Fill: a full-width accent bar scaled from the LEFT edge by _displayFrac. TransformOriginX=0 makes the bound
        // Scale pivot on the left (SceneRecorder: world ∘ T(ox,oy) ∘ Local ∘ T(-ox,-oy), ox = W·OriginX = 0), so the
        // fill grows rightward from 0. No layout, no re-render — a pure compositor transform reading ONE signal.
        var s = Slider.DefaultStyle;
        float ringD = s.ThumbRingDiameter;
        ColorF railFill = enabled ? s.RailFill : s.RailFillDisabled;
        ColorF valueFill = enabled ? s.ValueFill : s.ValueFillDisabled;
        ColorF valueHover = enabled ? s.ValueFillPointerOver : s.ValueFillDisabled;
        ColorF valuePress = enabled ? s.ValueFillPressed : s.ValueFillDisabled;
        ColorF dot = enabled ? s.ThumbFill : s.ThumbFillDisabled;
        ColorF dotHover = enabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled;
        ColorF dotPress = enabled ? s.ThumbFillPressed : s.ThumbFillDisabled;
        float rest = enabled ? s.InnerRestScale : s.InnerDisabledScale;
        float hoverScale = enabled ? s.InnerHoverScale / s.InnerRestScale : 1f;
        float pressScale = enabled ? s.InnerPressScale / s.InnerRestScale : 1f;

        Func<Affine2D> fillBind = () => Affine2D.Scale(MathF.Max(Math.Clamp(_displayFrac.Value, 0f, 1f), 1e-4f), 1f);
        Func<Affine2D> thumbBind = () => ThumbTransform(_width, _displayFrac.Value, ringD);
        // Rail thickens while scrubbing (a compositor scale on the cross axis is overkill; a bound Height would relayout,
        // so we keep a static thicker rail when enabled — the visible cue is the thumb fade-in on hover).

        var fill = new BoxEl
        {
            Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
            Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress,
            // Keep the rail WinUI-rounded, but make the transformed value segment square.
            // Scaling a rounded rect exposes tiny vertical cap slivers at the value edge.
            Corners = CornerRadius4.All(0f),
            HitTestVisible = false,
            TransformOriginX = 0f,
            // Always bound: _displayFrac is 0 when disabled (no track) so the fill scales to empty — a disabled rail reads
            // as a flat EMPTY track, not a full-width grey bar (which an identity transform would have shown).
            Transform = fillBind,
        };

        var rail = new BoxEl
        {
            Height = s.TrackHeight, Grow = 1f, AlignSelf = FlexAlign.Center,
            // A subtle track at low alpha (WinUI ControlStrong rail, dimmed for a media line).
            Fill = railFill, HoverFill = railFill, PressedFill = railFill,
            Corners = CornerRadius4.All(s.TrackCornerRadius),
            ClipToBounds = true,
            ZStack = true,
            HitTestVisible = false,
            Children = [fill],
        };

        var inner = new BoxEl
        {
            Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
            Corners = CornerRadius4.All(s.InnerThumbDiameter * 0.5f),
            Fill = dot, HoverFill = dotHover, PressedFill = dotPress,
            ScaleX = rest, ScaleY = rest,
            HoverScale = hoverScale, PressScale = pressScale,
            HoverDurationMs = 250f, PressDurationMs = 250f,
            HitTestVisible = false,
        };

        var thumb = new BoxEl
        {
            Width = ringD, Height = ringD,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.ThumbCornerRadius),
            Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,
            BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
            Opacity = 0f, HoverOpacity = enabled ? 1f : 0f, PressedOpacity = enabled ? 1f : 0f,
            HitTestVisible = false,
            Transform = thumbBind,
            OnRealized = OnThumbRealized,
            Children = [inner],
        };

        var stack = new BoxEl
        {
            ZStack = true, Grow = 1f, Height = HitHeight, AlignItems = FlexAlign.Center,
            HitTestVisible = false,
            Children = [rail, thumb],
        };

        // While playing, mount the per-frame ticker (a FrameClock consumer that advances _displayFrac each frame); it
        // unmounts when paused/stopped so the frame loop idles. The ticker NEVER re-renders this component.
        bool canAdvance = b.CurrentTrack.Peek() is not null && b.Error.Peek() is null && !b.IsLoading.Peek()
            && playing && !buffering;
        Element? ticker = canAdvance ? Embed.Comp(() => new SeekTicker { Owner = this }) : null;

        // The interactive row. Click-anywhere + drag scrub; OnClick is the drag-END commit edge (single SeekAsync).
        return new BoxEl
        {
            Grow = 1f, Height = HitHeight, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Slider,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            IsEnabled = enabled,
            // OnRealized is MOUNT-ONLY (BindNode wires it once and ignores it on re-render). Set it UNCONDITIONALLY so
            // `_self` is captured at mount regardless of the (then-unknown) enabled state — capturing a node handle while
            // disabled is harmless, and it MUST exist once the bar becomes enabled or every RefreshWidth early-returns.
            OnRealized = OnRealizedCb,
            OnBoundsChanged = OnArrangedBoundsChanged,
            OnPointerDown = enabled ? OnDown : null,
            OnDrag = enabled ? OnDrag : null,
            OnClick = enabled ? OnCommit : null,            // drag-end → commit
            OnDragCanceled = enabled ? OnCanceled : null,
            Children = ticker is null ? [stack] : [stack, ticker],
        };
    }

    void OnRealizedCb(NodeHandle h)
    {
        _self = h;
        RefreshWidth();
        Recompute();   // seed the resting position before the first paint
    }

    void OnThumbRealized(NodeHandle h)
    {
        _thumb = h;
        UpdateThumbTransform();
    }

    void OnArrangedBoundsChanged(RectF bounds)
    {
        if (!SetWidth(bounds.W)) return;
        if (DiagEnabled)
            WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "seekbar.bounds", "Seek bar bounds changed",
                fields:
                [
                    WaveeLogField.Of("count", ++s_boundsCount),
                    WaveeLogField.Of("width", bounds.W),
                ]);
        Recompute();
    }

    void RefreshWidth()
    {
        var scene = Context.Scene;
        if (scene is null || _self.IsNull || !scene.IsLive(_self)) return;
        SetWidth(scene.AbsoluteRect(_self).W);
    }

    bool SetWidth(float w)
    {
        if (w <= 0f || MathF.Abs(w - _width) <= 0.5f) return false;
        _width = w;
        UpdateThumbTransform();
        return true;
    }

    void UpdateThumbTransform()
    {
        var scene = Context.Scene;
        if (scene is null || _thumb.IsNull || !scene.IsLive(_thumb) || _width <= 0f) return;
        var next = ThumbTransform(_width, _displayFrac.Peek(), Slider.DefaultStyle.ThumbRingDiameter);
        ref var paint = ref scene.Paint(_thumb);
        if (paint.LocalTransform == next) return;
        paint.LocalTransform = next;
        scene.Mark(_thumb, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    static Affine2D ThumbTransform(float width, float frac, float ringD)
    {
        float half = ringD * 0.5f;
        float x = Math.Clamp(Math.Clamp(frac, 0f, 1f) * width - half, 0f, MathF.Max(0f, width - ringD));
        return Affine2D.Translation(x, 0f);
    }

    // Defensive re-derive for the pointer handlers (Peek — no render subscription). Handlers are only WIRED when enabled,
    // but this guards a stale fire during the disabling transition. Mirrors the Render derivation.
    bool Enabled() => _b.CurrentTrack.Peek() != null && _b.Error.Peek() == null && !_b.IsLoading.Peek() && _b.CanSeek.Peek();

    void OnDown(Point2 local)
    {
        if (!Enabled()) return;
        RefreshWidth();                                     // grip moves with the layout — re-read each gesture
        _scrubbing.Value = true;
        _scrubFrac.Value = Frac(local.X);                  // click-anywhere: jump to the press point
        _displayFrac.Value = _scrubFrac.Peek();            // paint the jump immediately
    }

    void OnDrag(Point2 local)
    {
        if (!Enabled()) return;
        // OnDrag now delivers UNCLAMPED local coords — clamp the fraction ourselves.
        _scrubFrac.Value = Frac(local.X);
        _displayFrac.Value = _scrubFrac.Peek();
    }

    void OnCommit()
    {
        // Always release the scrub gate — bailing out with _scrubbing still true would freeze
        // _displayFrac at the abandoned finger position until the next successful commit.
        long dur;
        if (!Enabled() || (dur = _b.DurationMs.Peek()) <= 0) { OnCanceled(); return; }
        float f = _scrubFrac.Peek();
        long targetMs = Math.Clamp((long)(f * dur), 0, dur);
        _b.PositionFrac.Value = f;                         // optimistic: paint the new position immediately
        _b.PositionMs.Value = targetMs;                    // keep time labels + interpolation anchor in the same place
        _tickWallMs = Environment.TickCount64;
        _tickPosMs = targetMs;
        _ = _b.Player.SeekAsync(targetMs);
        _scrubbing.Value = false;                          // release the scrub gate (PositionFrac/interp resume)
        Recompute();
    }

    void OnCanceled()
    {
        _scrubbing.Value = false;
        Recompute();
    }

    float Frac(float x)
    {
        float w = _width > 0f ? _width : 1f;
        return Math.Clamp(x / w, 0f, 1f);
    }
}

/// <summary>Per-frame stepper for <see cref="SeekBar"/> (the ScrollBar conscious-ticker idiom): mounted only while the
/// track is playing, it subscribes to the host frame clock and advances the owner's <c>_displayFrac</c> signal each
/// frame so the playhead interpolates smoothly between the ~1 Hz position ticks. The owner unmounts it on pause/stop,
/// idling the frame loop. It NEVER re-renders the owner (it only writes a signal the compositor binds read).</summary>
sealed class SeekTicker : Component
{
    public required SeekBar Owner;

    public override Element Render()
    {
        var tick = UseContextSignal(FrameClock.Tick);
        UseSignalEffect(() =>
        {
            _ = tick.Value;
            Owner.Recompute();
        });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
