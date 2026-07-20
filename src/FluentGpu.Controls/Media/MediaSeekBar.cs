using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls.Media;

/// <summary>
/// The media transport's scrub bar — a bespoke, compositor-bound seek control that REPLACES a per-frame
/// <c>Slider.Create</c> in <see cref="MediaPlayerElement"/>. It is an autonomous <see cref="Component"/> (embedded via
/// <c>Embed.Comp</c>) so the element's per-frame video-pump re-render does NOT recreate it (which destroyed any in-flight
/// drag and snapped the thumb back). Three properties make it correct where a controlled slider was not:
/// <list type="bullet">
/// <item><b>Scrub gate.</b> While the user is dragging, the displayed fraction follows the finger and IGNORES the
///   playhead, so the transport's position tick can't yank the thumb out from under the pointer.</item>
/// <item><b>Compositor-bound playhead.</b> The fill/thumb positions are bound <c>Transform</c>s reading ONE
///   <see cref="FloatSignal"/> (<c>_displayFrac</c>) — moving the playhead never re-renders/relayouts this component. A
///   mounted per-frame ticker advances that signal from <see cref="IMediaPlayer.PositionSeconds"/> while playing.</item>
/// <item><b>Live scrub + accurate commit.</b> Dragging issues fast <see cref="SeekMode.Keyframe"/> seeks for preview
///   (throttled); releasing issues one <see cref="SeekMode.Accurate"/> seek — driving the native transport (DRM or
///   clear) to the exact target.</item>
/// </list>
/// The Render body reads only LOW-frequency signals (play-state / duration) so it never re-renders per frame.
/// TerraFX-free: Engine + Controls types only. Modeled on the app's proven player-bar SeekBar.
/// </summary>
public sealed class MediaSeekBar : Component
{
    /// <summary>The player this bar seeks (headless contract).</summary>
    public required IMediaPlayer Player { get; init; }

    private const float HitHeight = 24f;           // the transport's scrub row height
    private const long SeekThrottleMs = 200;       // min gap between live keyframe seeks while dragging

    // While scrubbing the fill follows _scrubFrac and ignores the reported position (no snap-back).
    private readonly Signal<bool> _scrubbing = new(false);
    private readonly FloatSignal _scrubFrac = new(0f);
    // The single value the fill/thumb compositor binds read. Advanced per frame by the ticker while playing; set from
    // the reported position when paused; set to the finger position while scrubbing.
    private readonly FloatSignal _displayFrac = new(0f);
    // Live track width (px) as a signal so the thumb's bound transform re-evaluates when the layout width changes.
    private readonly FloatSignal _width = new(0f);

    private NodeHandle _self;
    private long _lastSeekMs = long.MinValue;       // throttle anchor for live keyframe seeks

    /// <summary>Re-derive <c>_displayFrac</c> from the current model — called by the ticker every frame while playing,
    /// and from an effect so a paused bar still shows the right resting position. Zero alloc; value-gated writes.</summary>
    internal void Recompute()
    {
        if (_scrubbing.Peek()) { _displayFrac.Value = _scrubFrac.Peek(); return; }   // scrub gate
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (durSec <= 0.0) { if (_displayFrac.Peek() != 0f) _displayFrac.Value = 0f; return; }
        float frac = (float)Math.Clamp(Player.PositionSeconds.Peek() / durSec, 0.0, 1.0);
        // Quantize to the live track's whole-pixel granularity: most ticker frames land on the same pixel, so the write
        // is a true no-op (no bind re-run, no redundant GPU submit); a real pixel step still advances smoothly.
        float w = _width.Peek();
        float q = w > 1f ? MathF.Round(frac * w) / w : frac;
        if (q != _displayFrac.Peek()) _displayFrac.Value = q;
    }

    public override Element Render()
    {
        // LOW-frequency subscriptions ONLY (never the position — that would re-render this component every frame).
        var st = Player.State.Value;
        bool playing = Player.IsPlaying.Value;
        bool buffering = Player.IsBuffering.Value;
        double durSec = Player.Duration.Value.TotalSeconds;
        bool enabled = durSec > 0.0 && st is not (PlaybackState.Idle or PlaybackState.Failed);

        // Re-seed the resting display when the enabling inputs change (duration arrives, play/pause edge). The playing
        // ticker covers per-frame advance; a seek-while-paused re-seeds in OnCommit.
        UseEffect(() => Recompute(), HashCode.Combine(enabled, playing, (int)(durSec * 1000)));

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

        // Fill grows from the LEFT edge by a bound ScaleX reading _displayFrac (TransformOriginX=0). No layout, no re-render.
        Func<Affine2D> fillBind = () => Affine2D.Scale(MathF.Max(Math.Clamp(_displayFrac.Value, 0f, 1f), 1e-4f), 1f);
        // Thumb slid to the value by a bound translation reading _displayFrac AND _width (so a width change re-evaluates it).
        Func<Affine2D> thumbBind = () => ThumbTransform(_width.Value, _displayFrac.Value, ringD);

        var fill = new BoxEl
        {
            Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
            Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress,
            Corners = CornerRadius4.All(0f),   // the scaled value segment stays square (scaling a rounded rect frays the cap)
            HitTestVisible = false,
            TransformOriginX = 0f,
            Transform = fillBind,
        };

        var rail = new BoxEl
        {
            Height = s.TrackHeight, Grow = 1f, AlignSelf = FlexAlign.Center,
            Fill = railFill, HoverFill = railFill, PressedFill = railFill,
            Corners = CornerRadius4.All(s.TrackCornerRadius),
            ClipToBounds = true, ZStack = true, HitTestVisible = false,
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
            // Thumb ring fades in on hover/press (the resting bar reads as a clean level line).
            Opacity = 0f, HoverOpacity = enabled ? 1f : 0f, PressedOpacity = enabled ? 1f : 0f,
            HitTestVisible = false,
            Transform = thumbBind,
            Children = [inner],
        };

        var stack = new BoxEl
        {
            ZStack = true, Grow = 1f, Height = HitHeight, AlignItems = FlexAlign.Center,
            HitTestVisible = false,
            Children = [rail, thumb],
        };

        // Per-frame ticker: advances _displayFrac from the playhead while playing; unmounted when paused/stopped so the
        // frame loop can idle. It NEVER re-renders this component (only writes a signal the compositor binds read).
        bool canAdvance = enabled && playing && !buffering;
        Element? ticker = canAdvance ? Embed.Comp(() => new MediaSeekTicker { Owner = this }) : null;

        return new BoxEl
        {
            Grow = 1f, Height = HitHeight, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Slider,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            IsEnabled = enabled,
            OnRealized = OnRealizedCb,           // mount-only; captures the node for width refresh
            OnBoundsChanged = OnBoundsChangedCb,
            OnPointerDown = enabled ? OnDown : null,
            OnDrag = enabled ? OnDragMove : null,
            OnClick = enabled ? OnCommit : null,             // drag-end → accurate commit (one SeekAsync)
            OnDragCanceled = enabled ? OnCanceled : null,
            Children = ticker is null ? [stack] : [stack, ticker],
        };
    }

    private void OnRealizedCb(NodeHandle h)
    {
        _self = h;
        RefreshWidth();
        Recompute();
    }

    private void OnBoundsChangedCb(RectF bounds)
    {
        if (bounds.W > 0f && MathF.Abs(bounds.W - _width.Peek()) > 0.5f)
        {
            _width.Value = bounds.W;
            Recompute();
        }
    }

    private void RefreshWidth()
    {
        var scene = Context.Scene;
        if (scene is null || _self.IsNull || !scene.IsLive(_self)) return;
        float w = scene.AbsoluteRect(_self).W;
        if (w > 0f && MathF.Abs(w - _width.Peek()) > 0.5f) _width.Value = w;
    }

    private static Affine2D ThumbTransform(float width, float frac, float ringD)
    {
        float half = ringD * 0.5f;
        float x = Math.Clamp(Math.Clamp(frac, 0f, 1f) * width - half, 0f, MathF.Max(0f, width - ringD));
        return Affine2D.Translation(x, 0f);
    }

    private bool Enabled()
    {
        var st = Player.State.Peek();
        return Player.Duration.Peek().TotalSeconds > 0.0 && st is not (PlaybackState.Idle or PlaybackState.Failed);
    }

    private void OnDown(Point2 local)
    {
        if (!Enabled()) return;
        RefreshWidth();
        _scrubbing.Value = true;
        _scrubFrac.Value = Frac(local.X);
        _displayFrac.Value = _scrubFrac.Peek();   // paint the jump immediately
        LiveSeek(force: true);
    }

    private void OnDragMove(Point2 local)
    {
        if (!Enabled()) return;
        _scrubFrac.Value = Frac(local.X);
        _displayFrac.Value = _scrubFrac.Peek();
        LiveSeek(force: false);                    // fast keyframe preview (throttled)
    }

    private void OnCommit()
    {
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (!Enabled() || durSec <= 0.0) { OnCanceled(); return; }
        float f = _scrubFrac.Peek();
        var target = TimeSpan.FromSeconds(Math.Clamp(f * durSec, 0.0, durSec));
        _displayFrac.Value = f;                    // hold the committed position; the ticker/effect converges as the playhead catches up
        _scrubbing.Value = false;                  // release the gate; SeekAsync publishes the target position, so no snap-back
        _lastSeekMs = (long)target.TotalMilliseconds;
        _ = Player.SeekAsync(target, SeekMode.Accurate);
    }

    private void OnCanceled()
    {
        _scrubbing.Value = false;
        Recompute();
    }

    // Live scrub preview: a fast keyframe seek to the current finger position, throttled so a pixel-per-move drag can't
    // flood the native transport. `force` bypasses the throttle (the initial press jump).
    private void LiveSeek(bool force)
    {
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (durSec <= 0.0) return;
        long ms = (long)Math.Clamp(_scrubFrac.Peek() * durSec * 1000.0, 0.0, durSec * 1000.0);
        if (!force && _lastSeekMs != long.MinValue && Math.Abs(ms - _lastSeekMs) < SeekThrottleMs) return;
        _lastSeekMs = ms;
        _ = Player.SeekAsync(TimeSpan.FromMilliseconds(ms), SeekMode.Keyframe);
    }

    private float Frac(float x)
    {
        float w = _width.Peek() > 0f ? _width.Peek() : 1f;
        return Math.Clamp(x / w, 0f, 1f);
    }
}

/// <summary>Per-frame stepper for <see cref="MediaSeekBar"/> (the conscious-ticker idiom): mounted only while playing,
/// it subscribes to the host frame clock and advances the owner's <c>_displayFrac</c> signal each frame so the playhead
/// interpolates smoothly between coarse position reports. Unmounted on pause/stop, idling the frame loop. It NEVER
/// re-renders the owner.</summary>
public sealed class MediaSeekTicker : ReactiveComponent
{
    /// <summary>The seek bar this ticker advances.</summary>
    public required MediaSeekBar Owner;

    public override Element Setup()
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
