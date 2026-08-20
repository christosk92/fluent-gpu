using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// THE pane splitter — WinUI GridSplitter / Community Toolkit PropertySizer as one control. A 16-DIP
/// invisible hit strip with optional hover thumb; <c>BoxEl.OnDrag</c> eager-captures so the gesture survives leaving
/// the thin strip. Size writes are 1:1 to the pointer (layout transitions suppressed for the gesture). Default axis
/// is horizontal (width). Opt-in detent: pass a <c>collapsed</c> signal to arm resist + fade below min, collapse past
/// <see cref="SplitterOptions.ForcePush"/>, and re-open past <see cref="SplitterOptions.ReExpand"/>.
/// </summary>
public static partial class Splitter
{
    /// <summary>THE grab target (DIP). Every seam in the kit is this thick — shell rails, library columns, detail
    /// rails, a docked-video height seam. Toolkit GridSplitter's default grip band; never narrower. Horizontal axis:
    /// strip width. Vertical axis: strip height.</summary>
    public const float StripW = 16f;

    /// <summary>The 16-DIP hit strip. Owned: OnPointerDown / OnDrag / OnClick / OnDragCanceled, Cursor, Width / Height
    /// (per axis), Children (the indicator mount).</summary>
    public const string PartRoot = "Root";
    /// <summary>The reveal-on-hover 2-DIP thumb. Owned: HitTestVisible (never a target), Opacity / HoverOpacity /
    /// PressedOpacity (the reveal). Omitted when <see cref="SplitterOptions.ShowIndicator"/> is false.</summary>
    public const string PartIndicator = "Indicator";

    public sealed record Style
    {
        public float IndicatorW { get; init; } = 2f;
        public float IndicatorInset { get; init; } = 4f;
        /// <summary>Reveal duration — WinUI ControlFasterAnimationDuration.</summary>
        public float HoverDurationMs { get; init; } = Motion.ControlFast;
        public ColorF IndicatorFill { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        IndicatorFill = Tok.FillControlStrong,
    };

    /// <summary>Frozen seam knobs. Range / polarity / detent live here; the live width and collapse/fade/dragging
    /// signals are <see cref="Create"/> arguments (the kit's controlled-input contract). Null on Create ⇒ clamp-only
    /// with <see cref="ShowIndicator"/> on.</summary>
    public sealed record SplitterOptions
    {
        public float Min { get; init; }
        public float Max { get; init; }
        public SplitterPolarity Polarity { get; init; } = SplitterPolarity.Trailing;
        /// <summary>Which dimension <see cref="Create"/> writes. Horizontal (default) = width / SizeWE / window-X.
        /// Vertical = height / SizeNS / window-Y.</summary>
        public SplitterAxis Axis { get; init; }
        /// <summary>Reveal-on-hover 2-DIP thumb (GridSplitter). False = paint-free (shell rails; discovery is SizeWE / SizeNS).</summary>
        public bool ShowIndicator { get; init; } = true;
        /// <summary>When true, the <c>collapsed</c> Create argument is an OPEN signal (true = expanded) —
        /// <c>ShellUi.RailOpen</c>. The detent writes false to close.</summary>
        public bool InvertCollapsed { get; init; }
        /// <summary>Width the gesture treats as the collapsed origin. Sidebar compact rail = 56; a remnant-less close
        /// (right rail) = 0. Detail compact is a sibling strip, so it also uses 0 as the drag origin.</summary>
        public float CompactWidth { get; init; }
        /// <summary>Raw width below this begins resist + fade. NaN (default) = <see cref="Min"/>.</summary>
        public float FadeStart { get; init; } = float.NaN;
        /// <summary>DIP of travel from <see cref="FadeStart"/> to the fade floor / collapse.</summary>
        public float FadeDistance { get; init; } = 44f;
        public float MinFade { get; init; } = 0.35f;
        public float Resist { get; init; } = 0.28f;
        /// <summary>DIP past <see cref="FadeStart"/> that collapses. 0 = <see cref="FadeDistance"/>.</summary>
        public float ForcePush { get; init; }
        public float ReExpand { get; init; } = 210f;
    }

    static readonly SplitterOptions DefaultOptions = new();

    /// <summary>Controlled size is a caller <see cref="Signal{T}"/> (width or height per <see cref="SplitterOptions.Axis"/>).
    /// A drag WRITES it; <paramref name="onCommit"/> fires once on a real drag-end (a bare click on a collapsed remnant
    /// re-opens without requiring a drag). <paramref name="collapsed"/> null ⇒ clamp-only (library columns). Non-null
    /// arms the detent. Fade / dragging are optional live companions the host binds (content opacity, layout-transition
    /// snap).</summary>
    public static Element Create(
        Signal<float> width,
        Action? onCommit = null,
        SplitterOptions? options = null,
        Signal<bool>? collapsed = null,
        Signal<float>? fade = null,
        Signal<bool>? dragging = null,
        Style? style = null,
        TemplateParts? parts = null)
        => Embed.Comp(new Props(new WidthCell(width), onCommit, options ?? DefaultOptions,
                                collapsed, fade, dragging, style ?? DefaultStyle, parts),
                      () => new SplitterCore());

    /// <inheritdoc cref="Create(Signal{float}, Action?, SplitterOptions?, Signal{bool}?, Signal{float}?, Signal{bool}?, Style?, TemplateParts?)"/>
    public static Element Create(
        FloatSignal width,
        Action? onCommit = null,
        SplitterOptions? options = null,
        Signal<bool>? collapsed = null,
        Signal<float>? fade = null,
        Signal<bool>? dragging = null,
        Style? style = null,
        TemplateParts? parts = null)
        => Embed.Comp(new Props(new WidthCell(width), onCommit, options ?? DefaultOptions,
                                collapsed, fade, dragging, style ?? DefaultStyle, parts),
                      () => new SplitterCore());

    internal sealed record Props(
        WidthCell Width, Action? OnCommit, SplitterOptions Options,
        Signal<bool>? Collapsed, Signal<float>? Fade, Signal<bool>? Dragging,
        Style Style, TemplateParts? Parts);
}

/// <summary>The stateful body: pointer capture, 1:1 size writes, and the opt-in detent. Non-value props ride
/// <c>UseProps</c> (re-pushed; theme-fresh <see cref="Splitter.DefaultStyle"/>). Gesture scratch stays on the
/// instance. The size / collapsed / fade / dragging signal INSTANCES freeze at mount (re-key to swap).</summary>
internal sealed class SplitterCore : Component
{
    Splitter.Props _p = null!;
    readonly Action _onReleased;
    readonly Action _onCanceled;
    NodeHandle _self;
    float _startW, _startPx, _min, _max, _fadeStart, _forcePush;
    bool _startedCollapsed, _moved;

    public SplitterCore()
    {
        _onReleased = OnReleased;
        _onCanceled = OnCanceled;
    }

    bool Detent => _p.Collapsed is not null && _forcePush > 0f;

    public override Element Render()
    {
        var p = UseProps<Splitter.Props>();
        _p = p;
        var o = p.Options;
        var s = p.Style;
        _min = o.Min;
        _max = o.Max;
        _fadeStart = float.IsNaN(o.FadeStart) ? o.Min : o.FadeStart;
        _forcePush = o.ForcePush > 0f ? o.ForcePush : o.FadeDistance;

        bool vertical = o.Axis == SplitterAxis.Vertical;
        Element[] kids;
        if (o.ShowIndicator)
        {
            var indicator = vertical
                ? new BoxEl
                {
                    Height = s.IndicatorW, Grow = 1f, Shrink = 0f,
                    Margin = new Edges4(s.IndicatorInset, 0f, s.IndicatorInset, 0f),
                    Corners = CornerRadius4.All(s.IndicatorW * 0.5f),
                    Fill = s.IndicatorFill,
                    Opacity = 0f, HoverOpacity = 1f, PressedOpacity = 1f,
                    HoverDurationMs = s.HoverDurationMs, HoverEasing = Easing.FluentDecelerate,
                    HitTestVisible = false,
                }
                : new BoxEl
                {
                    Width = s.IndicatorW, Grow = 1f, Shrink = 0f,
                    Margin = new Edges4(0f, s.IndicatorInset, 0f, s.IndicatorInset),
                    Corners = CornerRadius4.All(s.IndicatorW * 0.5f),
                    Fill = s.IndicatorFill,
                    Opacity = 0f, HoverOpacity = 1f, PressedOpacity = 1f,
                    HoverDurationMs = s.HoverDurationMs, HoverEasing = Easing.FluentDecelerate,
                    HitTestVisible = false,
                };
            kids = [p.Parts.Apply(Splitter.PartIndicator, indicator) with { HitTestVisible = false }];
        }
        else kids = [];

        Action<NodeHandle> capture = h => _self = h;
        var cursor = vertical ? CursorId.SizeNS : CursorId.SizeWE;
        var root = new BoxEl
        {
            Width = vertical ? float.NaN : Splitter.StripW,
            Height = vertical ? Splitter.StripW : float.NaN,
            Grow = vertical ? 0f : 1f, Shrink = 0f,
            Direction = vertical ? (byte)0 : (byte)1,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = vertical ? FlexAlign.Stretch : FlexAlign.Auto,
            Cursor = cursor,
            OnRealized = capture,
            OnPointerDown = OnDown,
            OnDrag = OnMove,
            OnClick = _onReleased,
            OnDragCanceled = _onCanceled,
            Children = kids,
        };
        var applied = p.Parts.Apply(Splitter.PartRoot, root);
        return applied with
        {
            Width = vertical ? float.NaN : Splitter.StripW,
            Height = vertical ? Splitter.StripW : float.NaN,
            Grow = vertical ? 0f : 1f,
            AlignSelf = vertical ? FlexAlign.Stretch : FlexAlign.Auto,
            Cursor = cursor,
            OnRealized = TemplateParts.Chain(capture, applied.OnRealized),
            OnPointerDown = OnDown, OnDrag = OnMove, OnClick = _onReleased, OnDragCanceled = _onCanceled,
            Children = kids,
        };
    }

    bool Vertical => _p.Options.Axis == SplitterAxis.Vertical;

    bool PeekCollapsed()
    {
        bool v = _p.Collapsed!.Peek();
        return _p.Options.InvertCollapsed ? !v : v;
    }

    void SetCollapsed(bool collapsed)
    {
        _p.Collapsed!.Value = _p.Options.InvertCollapsed ? !collapsed : collapsed;
    }

    void SetFade(float v)
    {
        if (_p.Fade is { } fade) fade.Value = v;
    }

    void SetDragging(bool v)
    {
        if (_p.Dragging is { } d) d.Value = v;
    }

    void OnDown(Point2 local)
    {
        var s = Context.Scene;
        if (s is null || _self.IsNull || !s.IsLive(_self)) return;
        Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, true);
        SetDragging(true);
        _moved = false;
        _startedCollapsed = Detent && PeekCollapsed();
        _startW = _startedCollapsed ? _p.Options.CompactWidth : _p.Width.Peek();
        var r = s.AbsoluteRect(_self);
        _startPx = Vertical ? local.Y + r.Y : local.X + r.X;
    }

    void OnMove(Point2 local)
    {
        var s = Context.Scene;
        if (s is null || _self.IsNull || !s.IsLive(_self)) return;
        var r = s.AbsoluteRect(_self);
        float px = Vertical ? local.Y + r.Y : local.X + r.X;
        float rawW = SplitterMath.RawWidth(_startW, _startPx, px, _p.Options.Polarity);
        if (!Detent)
        {
            _moved = true;
            _p.Width.Set(SplitterMath.ClampWidth(rawW, _min, _max));
            return;
        }

        _moved = true;
        if (_startedCollapsed)
        {
            if (rawW >= _p.Options.ReExpand)
            {
                _startedCollapsed = false;
                SetCollapsed(false);
                _p.Width.Set(SplitterMath.ClampWidth(rawW, _min, _max));
                SetFade(1f);
            }
            return;
        }

        if (rawW >= _fadeStart)
        {
            SetCollapsed(false);
            _p.Width.Set(SplitterMath.ClampWidth(rawW, _min, _max));
            SetFade(1f);
            return;
        }

        float into = SplitterMath.Into(_fadeStart, rawW);
        _p.Width.Set(SplitterMath.ResistWidth(_fadeStart, into, _p.Options.Resist));
        SetFade(SplitterMath.Fade(into, _p.Options.FadeDistance, _p.Options.MinFade));
        if (SplitterMath.ShouldCollapse(into, _forcePush))
        {
            SetCollapsed(true);
            _startedCollapsed = true;
            SetFade(1f);
        }
    }

    void OnReleased()
    {
        Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, false);
        bool collapsed = Detent && PeekCollapsed();
        // Sidebar keeps its expanded-width signal while compact (the 56-DIP rail is a different presented width).
        // A remnant-less collapse (CompactWidth = 0) must not persist the sticky sub-min value.
        if (!collapsed || _p.Options.CompactWidth <= 0f)
            _p.Width.Set(SplitterMath.ClampWidth(_p.Width.Peek(), _min, _max));
        SetFade(1f);
        if (_moved) { _p.OnCommit?.Invoke(); SetDragging(false); return; }
        if (Detent && collapsed)
        {
            SetCollapsed(false);
            _p.OnCommit?.Invoke();
        }
        SetDragging(false);
    }

    void OnCanceled()
    {
        Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, false);
        if (!Detent || _p.Options.CompactWidth <= 0f)
            _p.Width.Set(SplitterMath.ClampWidth(_p.Width.Peek(), _min, _max));
        SetFade(1f);
        SetDragging(false);
    }
}

/// <summary>Writable float cell covering both <see cref="Signal{T}"/> and <see cref="FloatSignal"/> so a splitter
/// can drive either (sidebar widths are <c>Signal&lt;float&gt;</c>; <c>ShellUi.RailWidth</c> is a
/// <see cref="FloatSignal"/>).</summary>
readonly struct WidthCell
{
    readonly Signal<float>? _s;
    readonly FloatSignal? _f;
    public WidthCell(Signal<float> s) { _s = s; _f = null; }
    public WidthCell(FloatSignal f) { _s = null; _f = f; }
    public float Peek() => _s is not null ? _s.Peek() : _f!.Peek();
    public void Set(float v)
    {
        if (_s is not null) _s.Value = v;
        else _f!.Value = v;
    }
}
