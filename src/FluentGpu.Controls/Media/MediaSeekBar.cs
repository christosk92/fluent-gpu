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
/// <c>Embed.Comp</c>) so source/geometry video pumps never recreate it (which would destroy an in-flight drag and snap
/// the thumb back). What makes it correct where a controlled slider was not:
/// <list type="bullet">
/// <item><b>Scrub gate, held until the PLAYER confirms.</b> While the user is dragging — and afterwards, until the
///   reported position actually reaches the committed target (or a bounded timeout expires) — the displayed fraction
///   follows the finger and IGNORES the playhead. Releasing the gate at pointer-up is wrong on the DRM path:
///   <c>ProtectedMediaSession</c> only publishes after the native ack (seconds), and every pump in between republishes
///   the STALE position, which is exactly the "thumb snaps back, then jumps seconds later" report.</item>
/// <item><b>Compositor-bound playhead.</b> The fill/thumb/buffered positions are bound <c>Transform</c>s reading
///   <see cref="FloatSignal"/>s — moving the playhead never re-renders or relayouts this component. A mounted
///   pixel-dwell ticker advances the display signal between coarse reported positions.</item>
/// <item><b>No per-frame re-render.</b> Render subscribes to NOTHING that ticks with the video: the hot position signal
///   is read inside a <c>UseSignalEffect</c> (which re-runs the effect, not the render), and buffered/seekable ranges
///   drive bound transforms through their own signals. Render's dependency set is duration + play/enable state only.</item>
/// <item><b>Live keyframe preview + one accurate commit.</b> Dragging issues fast <see cref="SeekMode.Keyframe"/>
///   seeks (wall-clock throttled, at most one per posted turn) on BOTH the standalone and host-owned paths; releasing
///   issues one <see cref="SeekMode.Accurate"/> seek to the exact target.</item>
/// <item><b>The stall is explained, not guessed at.</b> Buffered and seekable ranges are shaded in the rail, and a
///   seek that outlives <see cref="SeekSpinnerDelayMs"/> grows a small INLINE spinner at the playhead — never a
///   full-surface overlay, and never a blanked frame.</item>
/// </list>
/// TerraFX-free: Engine + Controls types only.
/// </summary>
public sealed class MediaSeekBar : Component
{
    /// <summary>The player this bar seeks (headless contract).</summary>
    public required IMediaPlayer Player { get; init; }
    /// <summary>Optional host-owned seek command. Null seeks <see cref="Player"/> directly.</summary>
    public Action<TimeSpan, SeekMode>? SeekRequested { get; init; }
    /// <summary>The owning chrome's visibility. A SIGNAL, not a bool: init props freeze at mount and this bar outlives
    /// every hide/reveal cycle by design (unmounting it is what made a fresh bar flash an empty rail at fraction 0 on
    /// every reveal). While the chrome is down the bar leaves the hit-test, focus and accessibility surfaces — hidden
    /// chrome that still answers a click is worse than no chrome. Null = always interactive.</summary>
    public IReadSignal<bool>? ChromeVisible { get; init; }

    /// <summary>The scrub row's hit height (DIP). ≥ 20 px is the pointer-target floor for a 4 px rail.</summary>
    public const float HitHeight = 24f;
    /// <summary>Minimum WALL-CLOCK gap between live keyframe previews while dragging. This is a real throttle: the
    /// value it guards is <see cref="Environment.TickCount64"/>, NOT two media positions — comparing media positions
    /// (the previous shape) meant a 3-hour video issued a seek on every single pointer move while a 30-second clip
    /// issued almost none, because the same pixel of travel is worth a different number of milliseconds.</summary>
    public const long SeekThrottleMs = 100;
    /// <summary>How long a seek may run before the inline spinner appears. Below this a seek reads as instant and a
    /// spinner would only flash.</summary>
    public const float SeekSpinnerDelayMs = 500f;
    /// <summary>How close the reported position must come to the committed target before the scrub gate reopens.</summary>
    private const float ConfirmToleranceSec = 0.75f;
    /// <summary>Upper bound on holding the gate closed waiting for a confirm — a backend that never reports lands the
    /// thumb on the target rather than freezing the control.</summary>
    private const long ConfirmTimeoutMs = 5000;

    // While scrubbing (and until the commit is confirmed) the fill follows _scrubFrac and ignores the reported position.
    private readonly Signal<bool> _scrubbing = new(false);
    private readonly FloatSignal _scrubFrac = new(0f);
    // The single value the fill/thumb compositor binds read. Advanced by the ticker while playing; set from the reported
    // position when paused; set to the finger position while scrubbing.
    private readonly FloatSignal _displayFrac = new(0f);
    // Live track width (px) as a signal so the thumb's bound transform re-evaluates when the layout width changes.
    private readonly FloatSignal _width = new(0f);
    // Buffered head (fraction of duration) and the seekable window — bound, never re-rendered.
    private readonly FloatSignal _bufferedFrac = new(0f);
    private readonly FloatSignal _seekableStartFrac = new(0f);
    private readonly FloatSignal _seekableSpanFrac = new(1f);
    // The target the user is scrubbing to, in seconds; < 0 when not scrubbing. The transport's time label reads this so
    // it shows where the user is GOING, not the decoded position that has not moved yet.
    private readonly FloatSignal _scrubTargetSec = new(-1f);
    // Low-frequency, render-visible: an in-flight seek that outlived SeekSpinnerDelayMs.
    private readonly Signal<bool> _slowSeek = new(false);

    private NodeHandle _self;
    private long _lastSeekWallMs = long.MinValue;   // WALL-CLOCK throttle anchor for live keyframe previews
    private bool _seekPostQueued;                   // one live seek per posted turn (≈ one per frame)
    private float _queuedFrac;
    private Action<Action>? _post;
    private readonly Action _drainSeek;
    // Commit confirmation: the gate stays closed until the reported position reaches this, or the timeout expires.
    private bool _awaitingConfirm;
    private float _confirmTargetSec;
    private long _confirmSinceWallMs;
    // The live slow-seek timer handle (a readonly struct wrapping the hook cell — stored by VALUE so arming it from an
    // event handler costs no delegate allocation, which a `_arm = handle.Restart` method group would).
    private TimerHandle _spinnerTimer;
    // Bounded release: the confirm check rides the position signal, so a backend that stops publishing entirely would
    // otherwise hold the gate forever. This fires it open.
    private TimerHandle _confirmTimer;
    // A low-cadence native position report seeds this anchor. The mounted ticker advances from it without re-rendering
    // the player element or requiring the native video session to pump every display frame.
    private long _positionAnchorWallMs;
    private float _positionAnchorSeconds;

    /// <summary>Create the stable post-drain delegate once; every live seek reuses it (0 alloc per drag move).</summary>
    public MediaSeekBar() => _drainSeek = DrainSeek;

    /// <summary>TRUE while the user owns the playhead — from pointer-down until the committed seek is confirmed by the
    /// player (or times out). This is the media chrome's canonical scrub suppressor: an auto-hide state machine must not
    /// hide the transport out from under a drag, and a position readout must not fight the finger. Read-only by
    /// contract: only this control opens and closes the gate.</summary>
    public IReadSignal<bool> Scrubbing => _scrubbing;

    /// <summary>The position the user is scrubbing TO, in seconds — or a negative value when no scrub is in flight.
    /// The transport's elapsed/total label binds this so that, within 100 ms of pointer-down and regardless of decode,
    /// the number under the finger is the TARGET time and not the decoded time (which may not move for seconds).</summary>
    public IReadSignal<float> ScrubTargetSeconds => _scrubTargetSec;

    /// <summary>Re-derive <c>_displayFrac</c> from the current model — called by the ticker every pixel-dwell while
    /// playing, and from the position effect so a paused bar still shows the right resting position. Zero alloc;
    /// value-gated writes.</summary>
    internal void Recompute()
    {
        if (_scrubbing.Peek()) { _displayFrac.Value = _scrubFrac.Peek(); return; }   // scrub gate
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (durSec <= 0.0) { if (_displayFrac.Peek() != 0f) _displayFrac.Value = 0f; return; }
        float position = Player.PositionSeconds.Peek();
        if (Player.IsPlaying.Peek() && !Player.IsBuffering.Peek())
        {
            long elapsedMs = Math.Max(0, Environment.TickCount64 - _positionAnchorWallMs);
            float rate = Math.Max(0f, Player.Rate.Peek());
            position = _positionAnchorSeconds + elapsedMs * 0.001f * rate;
        }
        float frac = (float)Math.Clamp(position / durSec, 0.0, 1.0);
        // Quantize to the live track's whole-pixel granularity: most ticker frames land on the same pixel, so the write
        // is a true no-op (no bind re-run, no redundant GPU submit); a real pixel step still advances smoothly.
        float w = _width.Peek();
        float q = w > 1f ? MathF.Round(frac * w) / w : frac;
        if (q != _displayFrac.Peek()) _displayFrac.Value = q;
    }

    public override Element Render()
    {
        // ── the render's dependency set: LOW-frequency only. The hot position signal is deliberately NOT read here —
        //    it is read inside the effect below, which re-runs the EFFECT, not this render. Reading it here is what made
        //    the bar re-render (and reallocate its bind closures, and relayout the transport row) on every video pump.
        var st = Player.State.Value;
        bool playing = Player.IsPlaying.Value;
        bool buffering = Player.IsBuffering.Value;
        double durSec = Player.Duration.Value.TotalSeconds;
        bool slowSeek = _slowSeek.Value;
        bool chromeUp = ChromeVisible?.Value ?? true;
        bool enabled = chromeUp && durSec > 0.0 && st is not (PlaybackState.Idle or PlaybackState.Failed);

        _post = UsePost();

        // The position anchor is MODEL state, not paint state: writing it from Render (the previous shape) made Render
        // impure and re-seeded interpolation on any unrelated re-render. It belongs to an effect that tracks the hot
        // signal without dragging the render along.
        UseSignalEffect(() =>
        {
            float reported = Player.PositionSeconds.Value;    // subscribes the EFFECT (never the render)
            _positionAnchorSeconds = reported;
            _positionAnchorWallMs = Environment.TickCount64;
            if (_awaitingConfirm)
            {
                bool reached = MathF.Abs(reported - _confirmTargetSec) <= ConfirmToleranceSec;
                bool timedOut = Environment.TickCount64 - _confirmSinceWallMs > ConfirmTimeoutMs;
                if (reached || timedOut) ReleaseGate();
            }
            Recompute();
        });

        // Buffered head + seekable window → bound signals. A stall is then EXPLAINED by the bar (the fill has run past
        // the buffered shading) instead of being guessed at from a frozen frame.
        UseSignalEffect(() =>
        {
            BufferHealth health = Player.Buffer.Value;
            double dur = Player.Duration.Value.TotalSeconds;
            float end = 0f;
            if (dur > 0.0)
            {
                var ranges = health.Ranges;
                for (int i = 0; i < ranges.Count; i++)
                {
                    float e = (float)Math.Clamp(ranges[i].End.TotalSeconds / dur, 0.0, 1.0);
                    if (e > end) end = e;
                }
            }
            _bufferedFrac.SetIfChanged(end);
        });
        UseSignalEffect(() =>
        {
            TimelineInfo timeline = Player.Timeline.Value;
            double dur = Player.Duration.Value.TotalSeconds;
            float start = 0f, span = 1f;
            if (dur > 0.0 && timeline.SeekableEnd > timeline.SeekableStart)
            {
                start = (float)Math.Clamp(timeline.SeekableStart.TotalSeconds / dur, 0.0, 1.0);
                float e = (float)Math.Clamp(timeline.SeekableEnd.TotalSeconds / dur, 0.0, 1.0);
                span = MathF.Max(e - start, 0f);
            }
            _seekableStartFrac.SetIfChanged(start);
            _seekableSpanFrac.SetIfChanged(span);
        });

        // The slow-seek spinner arm/disarm pair. UseTimeout arms at mount, so disarm it once on the mount edge.
        _spinnerTimer = UseTimeout(() => _slowSeek.SetIfChanged(true), SeekSpinnerDelayMs, DepKey.Empty);
        _confirmTimer = UseTimeout(ReleaseGate, ConfirmTimeoutMs, DepKey.Empty);
        UseEffect(DisarmSpinnerOnMount, DepKey.Empty);   // both UseTimeouts arm at mount; nothing is seeking yet

        // Re-seed the resting display when the enabling inputs change (duration arrives, play/pause edge). Deliberately
        // NOT keyed on the reported position — that quantised-to-milliseconds key re-ran this effect on every publish.
        int modelKey = HashCode.Combine(enabled, playing, buffering, (int)durSec);
        UseEffect(() => Recompute(), modelKey);

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
        // Buffered shading: same origin-0 scale, one signal behind the fill.
        Func<Affine2D> bufferedBind = () => Affine2D.Scale(MathF.Max(Math.Clamp(_bufferedFrac.Value, 0f, 1f), 1e-4f), 1f);
        // Seekable window (live/DVR): scale AND translate in one matrix — outside it, the rail reads as unreachable.
        Func<Affine2D> seekableBind = () => SeekableTransform(_width.Value, _seekableStartFrac.Value, _seekableSpanFrac.Value);
        // Thumb slid to the value by a bound translation reading _displayFrac AND _width (so a width change re-evaluates it).
        Func<Affine2D> thumbBind = () => ThumbTransform(_width.Value, _displayFrac.Value, ringD);

        // Painter order inside the rail: seekable window (dimmest) → buffered head → played fill.
        var seekableShade = new BoxEl
        {
            Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
            Fill = Tok.OnMediaPrimary with { A = 0.10f },
            Corners = CornerRadius4.All(0f),
            HitTestVisible = false,
            TransformOriginX = 0f,
            Transform = seekableBind,
        };

        var buffered = new BoxEl
        {
            Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
            Fill = Tok.OnMediaPrimary with { A = 0.24f },
            Corners = CornerRadius4.All(0f),
            HitTestVisible = false,
            TransformOriginX = 0f,
            Transform = bufferedBind,
        };

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
            Children = [seekableShade, buffered, fill],
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

        // The slow-seek cue rides the THUMB's own translation, so it sits exactly at the playhead. It is small, inline
        // and additive: the frame under it keeps playing/holding, which is the whole point — a full-surface overlay
        // that blanks the picture turns a 600 ms seek into a visible outage.
        Element? spinner = slowSeek && enabled
            ? new BoxEl
            {
                Width = ringD, Height = ringD,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                Transform = thumbBind,
                Children = [ProgressRing.Indeterminate(14f)],
            }
            : null;

        var stack = new BoxEl
        {
            ZStack = true, Grow = 1f, Height = HitHeight, AlignItems = FlexAlign.Center,
            HitTestVisible = false,
            Children = spinner is null ? [rail, thumb] : [rail, thumb, spinner],
        };

        // Pixel-due ticker (UseInterval — not FrameClock.Tick, which pins the host at panel rate via FrameClockPoller).
        // Unmounted when paused/stopped so the frame loop can idle. NEVER re-renders this component.
        bool canAdvance = enabled && playing && !buffering;
        Element? ticker = canAdvance ? Embed.Comp(() => new MediaSeekTicker { Owner = this }) : null;

        return new BoxEl
        {
            Grow = 1f, Height = HitHeight, Direction = 0, AlignItems = FlexAlign.Center,
            Role = enabled ? AutomationRole.Slider : default,
            TabStop = enabled ? null : false,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            IsEnabled = enabled,
            OnRealized = OnRealizedCb,           // mount-only; captures the node for width refresh
            OnBoundsChanged = OnBoundsChangedCb,
            OnPointerDown = enabled ? OnDown : null,     // click-to-seek anywhere on the rail: the press IS the seek
            OnDrag = enabled ? OnDragMove : null,
            OnClick = enabled ? OnCommit : null,             // drag-end → accurate commit (one seek)
            OnDragCanceled = enabled ? OnCanceled : null,
            OnPointerWheel = enabled ? OnWheel : null,       // wheel over the seek bar = ±5 s
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

    /// <summary>Scale + translate in ONE matrix: the seekable window starts at <paramref name="startFrac"/> of the rail
    /// and spans <paramref name="spanFrac"/> of it (a live/DVR stream's reachable range).</summary>
    private static Affine2D SeekableTransform(float width, float startFrac, float spanFrac)
        => new(MathF.Max(Math.Clamp(spanFrac, 0f, 1f), 1e-4f), 0f, 0f, 1f, Math.Clamp(startFrac, 0f, 1f) * width, 0f);

    private bool Enabled()
    {
        var st = Player.State.Peek();
        return Player.Duration.Peek().TotalSeconds > 0.0 && st is not (PlaybackState.Idle or PlaybackState.Failed);
    }

    private void OnDown(Point2 local)
    {
        if (!Enabled()) return;
        RefreshWidth();
        // A new grab supersedes any confirm we were still waiting on — including its timers, which would otherwise fire
        // mid-drag and reopen the gate under the finger.
        _awaitingConfirm = false;
        _confirmTimer.Cancel();
        _spinnerTimer.Cancel();
        _slowSeek.SetIfChanged(false);
        _scrubbing.Value = true;
        SetScrub(Frac(local.X));
        _displayFrac.Value = _scrubFrac.Peek();   // paint the jump immediately (< 100 ms, regardless of decode)
        QueueLiveSeek(force: true);
    }

    private void OnDragMove(Point2 local)
    {
        if (!Enabled()) return;
        SetScrub(Frac(local.X));
        _displayFrac.Value = _scrubFrac.Peek();
        QueueLiveSeek(force: false);              // fast keyframe preview (wall-clock throttled, ≤1 per posted turn)
    }

    private void OnCommit()
    {
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (!Enabled() || durSec <= 0.0) { OnCanceled(); return; }
        float f = _scrubFrac.Peek();
        var target = TimeSpan.FromSeconds(Math.Clamp(f * durSec, 0.0, durSec));
        _displayFrac.Value = f;                    // hold the committed position
        _positionAnchorSeconds = (float)target.TotalSeconds;
        _positionAnchorWallMs = Environment.TickCount64;
        // HOLD the gate. The old shape released it here on the theory that "SeekAsync publishes the target position, so
        // no snap-back" — false on the DRM path, where ProtectedMediaSession publishes only after the native ack (up to
        // seconds) and every pump in between republishes the stale position. The gate now reopens on CONFIRMATION.
        _awaitingConfirm = true;
        _confirmTargetSec = (float)target.TotalSeconds;
        _confirmSinceWallMs = Environment.TickCount64;
        _spinnerTimer.Restart();
        _confirmTimer.Restart();
        _lastSeekWallMs = Environment.TickCount64;
        RequestSeek(target, SeekMode.Accurate);
    }

    private void OnCanceled()
    {
        _awaitingConfirm = false;
        ReleaseGate();
    }

    private void OnWheel(WheelEventArgs e)
    {
        if (!Enabled()) return;
        float step = e.Delta > 0f ? 5f : -5f;
        SeekBy(step);
        e.Handled = true;
    }

    /// <summary>Seek relative to the current position (the wheel-over-the-rail gesture and the element's key map share
    /// this, so both land on the same accurate commit + confirm-gated hold).</summary>
    internal void SeekBy(float seconds)
    {
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (durSec <= 0.0) return;
        double from = _awaitingConfirm ? _confirmTargetSec : Player.PositionSeconds.Peek();
        var target = TimeSpan.FromSeconds(Math.Clamp(from + seconds, 0.0, durSec));
        SeekTo(target);
    }

    /// <summary>Seek to an absolute position with the same confirm-gated hold the drag commit uses — so a keyboard or
    /// wheel seek also paints its target immediately and does not let a stale report drag the playhead backwards.</summary>
    internal void SeekTo(TimeSpan target)
    {
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (durSec <= 0.0) return;
        float f = (float)Math.Clamp(target.TotalSeconds / durSec, 0.0, 1.0);
        SetScrub(f);
        _scrubbing.Value = true;
        _displayFrac.Value = f;
        _positionAnchorSeconds = (float)target.TotalSeconds;
        _positionAnchorWallMs = Environment.TickCount64;
        _awaitingConfirm = true;
        _confirmTargetSec = (float)target.TotalSeconds;
        _confirmSinceWallMs = Environment.TickCount64;
        _spinnerTimer.Restart();
        _confirmTimer.Restart();
        _lastSeekWallMs = Environment.TickCount64;
        RequestSeek(target, SeekMode.Accurate);
    }

    private void SetScrub(float frac)
    {
        _scrubFrac.Value = frac;
        double durSec = Player.Duration.Peek().TotalSeconds;
        _scrubTargetSec.SetIfChanged(durSec > 0.0 ? (float)(frac * durSec) : -1f);
    }

    private void DisarmSpinnerOnMount()
    {
        _spinnerTimer.Cancel();
        _confirmTimer.Cancel();
        _slowSeek.SetIfChanged(false);
    }

    private void ReleaseGate()
    {
        _awaitingConfirm = false;
        _spinnerTimer.Cancel();
        _confirmTimer.Cancel();
        _slowSeek.SetIfChanged(false);
        _scrubTargetSec.SetIfChanged(-1f);
        if (_scrubbing.Peek()) _scrubbing.Value = false;
        Recompute();
    }

    // Live scrub preview: a fast keyframe seek to the current finger position. `force` bypasses the wall-clock throttle
    // (the initial press jump). The request itself is posted, so a burst of pointer moves inside one frame collapses to
    // ONE seek — the "coalesce moves to one per frame" rule, enforced at the expensive end.
    private void QueueLiveSeek(bool force)
    {
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (durSec <= 0.0) return;
        _queuedFrac = _scrubFrac.Peek();
        if (_seekPostQueued) return;
        long now = Environment.TickCount64;
        if (!force && _lastSeekWallMs != long.MinValue && now - _lastSeekWallMs < SeekThrottleMs) return;
        _seekPostQueued = true;
        if (_post is { } post) post(_drainSeek); else DrainSeek();
    }

    private void DrainSeek()
    {
        _seekPostQueued = false;
        double durSec = Player.Duration.Peek().TotalSeconds;
        if (durSec <= 0.0 || !_scrubbing.Peek()) return;
        _lastSeekWallMs = Environment.TickCount64;
        // Keyframe previews go to the HOST path too. Suppressing them whenever SeekRequested was set (the previous
        // shape) is what left the picture frozen on the last decoded frame for the whole drag: the host is exactly the
        // path that can serve a cheap keyframe. The mode is carried, so a host that distinguishes fast previews from
        // accurate commits can act on it.
        RequestSeek(TimeSpan.FromSeconds(Math.Clamp(_queuedFrac * durSec, 0.0, durSec)), SeekMode.Keyframe);
    }

    private void RequestSeek(TimeSpan target, SeekMode mode)
    {
        if (SeekRequested is { } request) request(target, mode);
        else _ = Player.SeekAsync(target, mode);
    }

    private float Frac(float x)
    {
        float w = _width.Peek() > 0f ? _width.Peek() : 1f;
        return Math.Clamp(x / w, 0f, 1f);
    }

    /// <summary>Pixel dwell of the playhead: <c>durationMs / trackWidthPx</c>, clamped ~[33, 250] ms.</summary>
    internal float TickIntervalMs()
    {
        double durSec = Player.Duration.Peek().TotalSeconds;
        float w = _width.Peek();
        if (durSec <= 0.0 || w <= 1f) return 100f;
        return Math.Clamp((float)(durSec * 1000.0 / w), 33f, 250f);
    }
}

/// <summary>Pixel-due stepper for <see cref="MediaSeekBar"/>: mounted only while playing, advances <c>_displayFrac</c>
/// on a <see cref="Component.UseInterval"/> at the playhead's pixel dwell (not <c>FrameClock.Tick</c>). Unmounted on
/// pause/stop. NEVER re-renders the owner.</summary>
public sealed class MediaSeekTicker : Component
{
    /// <summary>The seek bar this ticker advances.</summary>
    public required MediaSeekBar Owner;

    public override Element Render()
    {
        UseInterval(() => Owner.Recompute(), Owner.TickIntervalMs());
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
