using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The drawing state of a <see cref="ProgressBar"/> — mirrors WinUI's CommonStates (Determinate / Paused /
/// Error and their Indeterminate counterparts), selected from IsIndeterminate + ShowPaused + ShowError.</summary>
public enum ProgressBarState : byte { Normal = 0, Paused = 1, Error = 2 }

/// <summary>
/// A WinUI ProgressBar (1:1). A 3px-min band (<c>ProgressBarMinHeight</c>) holding a 1px track
/// (<c>ProgressBarTrackHeight</c>, <c>ControlStrongStrokeColorDefault</c>) under an accent indicator
/// (<c>AccentFillColorDefaultBrush</c>) with a 1.5px corner. <see cref="Determinate"/> fills the indicator to a 0..1
/// value; <see cref="Indeterminate"/> sweeps the two clipped accent indicators across the track on the looping
/// translate keyframes WinUI binds from <c>ProgressBarTemplateSettings</c> (Container/Container2 positions). Paused/Error
/// stop the sweep and settle a full-width bar at <c>ContainerAnimationMidPosition</c>, recolored to
/// <c>SystemFillColorCaution</c> / <c>SystemFillColorCritical</c> (instant recolor — no fill-color anim channel).
/// </summary>
public static class ProgressBar
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The 1px under-track (WinUI ProgressBarTrack). Owned: none — its Opacity-0 in the indeterminate
    /// states is stock per-render styling a modifier may override.</summary>
    public const string PartTrack = "Track";
    /// <summary>The foreground indicator (WinUI DeterminateProgressBarIndicator, and BOTH
    /// IndeterminateProgressBarIndicator/2 — the modifier runs on each sweeping indicator). The value-driven Width
    /// and the Paused/Error recolor are stock per-render styling (override-able). Owned: OnRealized (the
    /// indeterminate sweep refs, chained with any modifier-supplied handler).</summary>
    public const string PartFill = "Fill";

    // ── WinUI sizes/corners (ProgressBar_themeresources.xaml) ──────────────────────────────────────
    const float MinHeight = 3f;          // ProgressBarMinHeight
    const float TrackHeight = 1f;         // ProgressBarTrackHeight
    const float IndicatorRadius = 1.5f;   // ProgressBarCornerRadius (indicator)
    const float TrackRadius = 0.5f;       // ProgressBarTrackCornerRadius
    const float DefaultWidth = 240f;

    /// <summary>The measured-width quantum for a stretched (<c>width: float.NaN</c>) indeterminate bar — see
    /// <see cref="Indeterminate"/>. Sub-quantum layout wobble (e.g. a settling flex pass) is absorbed at the
    /// measured-width signal, so it never re-renders the bar or re-arms the sweep.</summary>
    const float StretchWidthQuantum = 4f;

    // Indeterminate indicator widths (ProgressBar.cpp SetProgressBarIndicatorWidth): 40% / 60% of the track width.
    const float Indicator1Frac = 0.40f;
    const float Indicator2Frac = 0.60f;

    // Indeterminate loop = 2.0s (ProgressBar.xaml Indeterminate storyboard, RepeatBehavior="Forever").
    const float LoopMs = 2000f;
    // The shared indeterminate translate easing = KeySpline 0.4,0.0,0.6,1.0 (FastOutSlowIn-ish ease).
    static readonly EasingSpec IndetEase = EasingSpec.CubicBezier(0.4f, 0.0f, 0.6f, 1.0f);

    // WinUI's TemplateSettings.ContainerAnimationMidPosition (ProgressBar.cpp: always 0). IndeterminatePaused/Error
    // settle indicator2's TranslateX here so the full-width caution/critical bar sits STATIC over the track.
    const float ContainerAnimationMidPosition = 0f;

    /// <summary>The indicator foreground for a state: Normal = accent, Paused = caution, Error = critical
    /// (ProgressBar_themeresources: ProgressBarForeground / ProgressBarPausedForegroundColor / ...ErrorForegroundColor).</summary>
    static ColorF ForegroundFor(ProgressBarState state) => state switch
    {
        ProgressBarState.Paused => Tok.SystemFillCaution,    // ProgressBarPausedForegroundColor = SystemFillColorCaution
        ProgressBarState.Error => Tok.SystemFillCritical,    // ProgressBarErrorForegroundColor  = SystemFillColorCritical
        _ => Tok.AccentDefault,                              // ProgressBarForeground            = AccentFillColorDefaultBrush
    };

    // ── Create (the one canonical factory) ───────────────────────────────────────────────────────────
    /// <summary>The ONE canonical ProgressBar factory. <paramref name="value"/> = a caller-owned 0..1
    /// <see cref="FloatSignal"/> tracked compositor-live (the determinate indicator's width binds to it, so a scrub
    /// updates the bar with no re-render); <c>null</c> = the indeterminate sweep. <paramref name="state"/> selects the
    /// indicator color (Normal accent / Paused caution / Error critical). <see cref="Determinate"/> (static value) and
    /// <see cref="Indeterminate"/> are one-line forwarders onto this.</summary>
    public static Element Create(FloatSignal? value = null, float width = DefaultWidth,
                                 ProgressBarState state = ProgressBarState.Normal, TemplateParts? parts = null)
        => value is null
            ? (float.IsNaN(width)
                // width: float.NaN ⇒ stretch to the parent-offered width (see StretchIndeterminateBar) — a
                // separate component so the fixed-width path below stays byte-identical (no measured-width hook,
                // no extra wrapper node) for every caller that doesn't ask for stretch.
                ? Embed.Comp(new Props(width, state, parts), () => new StretchIndeterminateBar())
                : Embed.Comp(new Props(width, state, parts), () => new IndeterminateBar()))
            // Determinate tracking the signal: the indicator width is a bound Func (compositor/relayout, no re-render).
            : DeterminateView((Prop<float>)(Func<float>)(() =>
              {
                  float v = value.Value;   // subscribe the width bind → scrub the bar live
                  return (v < 0f ? 0f : v > 1f ? 1f : v) * width;
              }), width, state, parts);

    // ── Determinate ────────────────────────────────────────────────────────────────────────────────
    /// <summary>Determinate progress with a STATIC value; <paramref name="value"/> is clamped to 0..1, indicator width
    /// = value * width. A one-line forwarder onto <see cref="Create"/>'s shared builder. <paramref name="state"/>
    /// selects the indicator color (Normal accent / Paused caution / Error critical). <paramref name="parts"/> =
    /// per-part styling keyed by <see cref="PartTrack"/>/<see cref="PartFill"/>. For a LIVE value use <see cref="Create"/>
    /// with a <see cref="FloatSignal"/>.</summary>
    public static BoxEl Determinate(float value, float width = DefaultWidth, ProgressBarState state = ProgressBarState.Normal,
                                    TemplateParts? parts = null)
    {
        float v = value < 0f ? 0f : value > 1f ? 1f : value;
        return DeterminateView((Prop<float>)(v * width), width, state, parts);
    }

    /// <summary>Shared determinate builder: the ZStack track + indicator, with the indicator's width supplied as either
    /// a static value (<see cref="Determinate"/>) or a signal-bound <c>Func</c> (<see cref="Create"/>). The value-driven
    /// Width + state recolor are stock (a PartFill modifier sees and may override them).</summary>
    static BoxEl DeterminateView(Prop<float> indicatorWidth, float width, ProgressBarState state, TemplateParts? parts)
        => new BoxEl
        {
            ZStack = true,
            Width = width,
            Height = MinHeight,
            Role = AutomationRole.ProgressBar,
            Children =
            [
                // ProgressBarTrack: 1px, ControlStrongStrokeColorDefault, 0.5px corner, vertically centered in the band.
                parts.Apply(PartTrack, new BoxEl
                {
                    Width = width,
                    Height = TrackHeight,
                    OffsetY = (MinHeight - TrackHeight) / 2f,
                    Corners = CornerRadius4.All(TrackRadius),
                    Fill = Tok.StrokeControlStrongDefault,
                }),
                // DeterminateProgressBarIndicator: foreground fill, 1.5px corner, left-aligned, width = value * track width.
                parts.Apply(PartFill, new BoxEl
                {
                    Width = indicatorWidth,
                    Height = MinHeight,
                    Corners = CornerRadius4.All(IndicatorRadius),
                    Fill = ForegroundFor(state),
                }),
            ],
        };

    // ── Indeterminate ────────────────────────────────────────────────────────────────────────────
    /// <summary>Indeterminate progress: the two clipped accent indicators sweeping across the track on the WinUI
    /// ProgressBarTemplateSettings translate keyframes. In Paused/Error, the track hides and only indicator2 shows,
    /// recolored to caution/critical (matching WinUI's IndeterminatePaused / IndeterminateError visual states).
    /// <paramref name="width"/> = <c>float.NaN</c> STRETCHES the bar to the parent-offered width instead of a fixed
    /// DIP value (the standard "facet-switch busy bar" pattern: full-content-width, pinned under a dimmed page) —
    /// the resolved width is read back via <c>UseMeasuredWidth</c> (quantum 4) and the sweep re-arms at the new
    /// extent whenever the parent resizes. A finite width is unaffected: no measurement, no extra wrapper node.
    /// <paramref name="parts"/> = per-part styling keyed by <see cref="PartTrack"/>/<see cref="PartFill"/> (the
    /// PartFill modifier runs on BOTH sweeping indicators; PartTrack is opacity-0 by default in every indeterminate
    /// state — override it, e.g. <c>b => b with { Opacity = 1f }</c>, for a visible 1px track under the sweep).</summary>
    public static Element Indeterminate(float width = DefaultWidth, ProgressBarState state = ProgressBarState.Normal,
                                        TemplateParts? parts = null)
        => Create(null, width, state, parts);

    /// <summary>Controlled props RE-PUSHED to the stateful core (<c>Embed.Comp(props, …)</c>): a reused ComponentEl
    /// never re-runs its factory, so runtime-changeable props are delivered live (equality-gated); the core reads them
    /// with <c>UseProps</c>.</summary>
    internal sealed record Props(float Width, ProgressBarState State, TemplateParts? Parts);

    /// <summary>The computed translate positions WinUI binds from ProgressBarTemplateSettings into the indeterminate
    /// storyboards (ProgressBar.cpp UpdateWidthBasedTemplateSettings). Indicator widths follow SetProgressBarIndicatorWidth.</summary>
    public readonly record struct ProgressBarTemplateSettings(
        float Indicator1Width, float Indicator2Width,
        float ContainerAnimationStartPosition, float ContainerAnimationEndPosition,
        float Container2AnimationStartPosition, float Container2AnimationEndPosition)
    {
        public static ProgressBarTemplateSettings For(float width)
        {
            // SetProgressBarIndicatorWidth: indicator1 = 40% width, indicator2 = 60% width.
            float w1 = width * Indicator1Frac;
            float w2 = width * Indicator2Frac;
            return new ProgressBarTemplateSettings(
                Indicator1Width: w1,
                Indicator2Width: w2,
                // UpdateWidthBasedTemplateSettings (operates on the 40%/60% indicator widths):
                ContainerAnimationStartPosition: w1 * -1.0f,    // -100% of indicator1  (= -40% width)
                ContainerAnimationEndPosition: w1 * 3.0f,       // +300% of indicator1  (= +120% width)
                Container2AnimationStartPosition: w2 * -1.5f,   // -150% of indicator2  (= -90% width)
                Container2AnimationEndPosition: w2 * 1.66f);    // +166% of indicator2  (= +99.6% width)
        }
    }

    private sealed class IndeterminateBar : Component
    {
        public override Element Render()
        {
            var props = UseProps<Props>();
            float Width = props.Width;
            var State = props.State;
            var Parts = props.Parts;
            var ts = ProgressBarTemplateSettings.For(Width);
            bool nonNormal = State != ProgressBarState.Normal;   // Paused / Error: hide track + indicator1, recolor indicator2
            ColorF fg = ForegroundFor(State);

            // Capture the two indicators so the looping translate tracks drive each independently (UseKeyframes targets the
            // component's HostNode only; per-child translate needs direct AnimEngine.Keyframes on each captured handle).
            var ind1Ref = UseRef<NodeHandle>(default);
            var ind2Ref = UseRef<NodeHandle>(default);

            // IndeterminateProgressBarIndicator storyboard (2s loop):
            //   KeyTime 0    → ContainerAnimationStartPosition          (discrete)
            //   KeyTime 1.5s → ContainerAnimationEndPosition            (spline 0.4,0,0.6,1)
            //   KeyTime 2.0s → ContainerAnimationEndPosition            (discrete hold)
            // In Paused/Error this indicator is hidden (Opacity 0), so we only drive it in the Normal state.
            // IndeterminateProgressBarIndicator2 storyboard (2s loop):
            //   KeyTime 0    → Container2AnimationStartPosition         (discrete)
            //   KeyTime 0.75s→ Container2AnimationStartPosition         (discrete hold)
            //   KeyTime 2.0s → Container2AnimationEndPosition           (spline 0.4,0,0.6,1)
            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null) return;

                if (!ind1Ref.Value.IsNull && scene.IsLive(ind1Ref.Value))
                {
                    if (nonNormal)
                        anim.Cancel(ind1Ref.Value, AnimChannel.TranslateX);   // hidden indicator1: no sweep in Paused/Error
                    else
                        anim.Keyframes(ind1Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ts.ContainerAnimationStartPosition, Easing.Linear),
                            new(0.75f, ts.ContainerAnimationEndPosition, IndetEase),   // 1.5s of 2.0s
                            new(1.00f, ts.ContainerAnimationEndPosition, Easing.Linear),
                        }, LoopMs, loop: true, cadence: Cadence.Display);   // TRANSIENT loop: it must sweep smoothly, so it opts out of DefaultLoopHz
                }

                if (!ind2Ref.Value.IsNull && scene.IsLive(ind2Ref.Value))
                {
                    if (nonNormal)
                    {
                        // IndeterminatePaused / IndeterminateError: NOT a sweep — settle the full-width bar STATIC over the
                        // track at ContainerAnimationMidPosition (=0). Cancel the loop, then hold TranslateX at 0 so a prior
                        // Normal-state sweep can't leave it parked mid-track.
                        anim.Cancel(ind2Ref.Value, AnimChannel.TranslateX);
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ContainerAnimationMidPosition, Easing.Linear),
                            new(1.00f, ContainerAnimationMidPosition, Easing.Linear),
                        }, LoopMs, loop: true);
                    }
                    else
                    {
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.000f, ts.Container2AnimationStartPosition, Easing.Linear),
                            new(0.375f, ts.Container2AnimationStartPosition, Easing.Linear),   // 0.75s hold
                            new(1.000f, ts.Container2AnimationEndPosition, IndetEase),         // → 2.0s
                        }, LoopMs, loop: true, cadence: Cadence.Display);   // TRANSIENT loop: it must sweep smoothly, so it opts out of DefaultLoopHz
                    }
                }
            }, DepKey.From(HashCode.Combine(Width, State)));

            // Indicator2 spans the full track in Paused/Error (SetProgressBarIndicatorWidth: 100%), else 60%.
            float ind2Width = nonNormal ? Width : ts.Indicator2Width;

            Action<NodeHandle> ind1Capture = h => ind1Ref.Value = h;
            Action<NodeHandle> ind2Capture = h => ind2Ref.Value = h;

            // ProgressBarTrack — hidden (Opacity 0) in every indeterminate state in WinUI (stock state styling a
            // PartTrack modifier sees and may override).
            var track = Parts.Apply(PartTrack, new BoxEl
            {
                Width = Width,
                Height = TrackHeight,
                OffsetY = (MinHeight - TrackHeight) / 2f,
                Corners = CornerRadius4.All(TrackRadius),
                Fill = Tok.StrokeControlStrongDefault,
                Opacity = 0f,
            });

            // IndeterminateProgressBarIndicator (40% width) — visible only in the Normal indeterminate state.
            var ind1 = new BoxEl
            {
                Width = ts.Indicator1Width,
                Height = MinHeight,
                Corners = CornerRadius4.All(IndicatorRadius),
                Fill = fg,
                Opacity = nonNormal ? 0f : 1f,
                OnRealized = ind1Capture,
            };
            // IndeterminateProgressBarIndicator2 (60% width, or 100% in Paused/Error) — always visible indeterminate.
            var ind2 = new BoxEl
            {
                Width = ind2Width,
                Height = MinHeight,
                Corners = CornerRadius4.All(IndicatorRadius),
                Fill = fg,
                OnRealized = ind2Capture,
            };
            if (Parts is { } p)   // the PartFill modifier runs on BOTH indicators; the sweep refs always win (chained)
            {
                var m1 = p.Apply(PartFill, ind1);
                ind1 = m1 with { OnRealized = TemplateParts.Chain(ind1Capture, m1.OnRealized) };
                var m2 = p.Apply(PartFill, ind2);
                ind2 = m2 with { OnRealized = TemplateParts.Chain(ind2Capture, m2.OnRealized) };
            }

            return new BoxEl
            {
                ZStack = true,
                Width = Width,
                Height = MinHeight,
                ClipToBounds = true,                 // Border Clip="...ClipRect" — the sweep is clipped to the track bounds
                Role = AutomationRole.ProgressBar,
                Children = [track, ind1, ind2],
            };
        }
    }

    /// <summary>The <c>width: float.NaN</c> path of <see cref="Indeterminate"/>: same visual/motion contract as
    /// <see cref="IndeterminateBar"/>, but the bar's own width is read back from layout instead of taken as a
    /// fixed DIP value. A SEPARATE component (not a branch inside <see cref="IndeterminateBar"/>) so the fixed-width
    /// path stays byte-identical — no measured-width hook installed, no extra wrapper node — for every caller that
    /// doesn't opt into stretch.
    ///
    /// Shape: an outer wrapper (<c>Grow=1, Basis=0, AlignSelf=Stretch</c> — the DensityPlot "stretch to the offered
    /// width" idiom, <c>DensityPlot.cs</c>) is this component's HostNode, so <c>UseMeasuredWidth</c> reports what the
    /// PARENT actually offered; the real ZStack bar (track + two sweeping indicators, <see cref="AutomationRole.ProgressBar"/>)
    /// is its single child, drawn at that resolved width. The first frame has no measured width yet (0, sweep parked);
    /// the layout effect seeds it next frame and the bar (re-render → scoped relayout, NOT a per-frame cost) settles at
    /// the real extent. A later parent resize repeats exactly that: the quantized width signal changes, the component
    /// re-renders, and the same <c>UseEffect</c> below (keyed on Width+State, like <see cref="IndeterminateBar"/>)
    /// re-plans the translate keyframes for the new extent — the "re-arm on resize" contract.</summary>
    private sealed class StretchIndeterminateBar : Component
    {
        public override Element Render()
        {
            var props = UseProps<Props>();
            var State = props.State;
            var Parts = props.Parts;

            // The arranged width of THIS component's rendered root (the stretch wrapper below) — quantum 4 absorbs
            // sub-pixel layout jitter so a settling flex pass doesn't re-render this component every frame.
            float Width = MathF.Max(0f, UseMeasuredWidth(StretchWidthQuantum).Value);

            var ts = ProgressBarTemplateSettings.For(Width);
            bool nonNormal = State != ProgressBarState.Normal;
            ColorF fg = ForegroundFor(State);

            var ind1Ref = UseRef<NodeHandle>(default);
            var ind2Ref = UseRef<NodeHandle>(default);

            // Same storyboard as IndeterminateBar's effect (see its comments for the WinUI KeyTime breakdown) — the
            // only difference is Width comes from measurement instead of a prop, so a resize re-runs this exactly
            // like a width-prop change would. Width == 0 (not yet measured, or a collapsed parent) skips arming: no
            // point sweeping a zero-extent track, and it avoids seeding a degenerate 0-length translate keyframe.
            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || Width <= 0f) return;

                if (!ind1Ref.Value.IsNull && scene.IsLive(ind1Ref.Value))
                {
                    if (nonNormal)
                        anim.Cancel(ind1Ref.Value, AnimChannel.TranslateX);
                    else
                        anim.Keyframes(ind1Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ts.ContainerAnimationStartPosition, Easing.Linear),
                            new(0.75f, ts.ContainerAnimationEndPosition, IndetEase),
                            new(1.00f, ts.ContainerAnimationEndPosition, Easing.Linear),
                        }, LoopMs, loop: true, cadence: Cadence.Display);
                }

                if (!ind2Ref.Value.IsNull && scene.IsLive(ind2Ref.Value))
                {
                    if (nonNormal)
                    {
                        anim.Cancel(ind2Ref.Value, AnimChannel.TranslateX);
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.00f, ContainerAnimationMidPosition, Easing.Linear),
                            new(1.00f, ContainerAnimationMidPosition, Easing.Linear),
                        }, LoopMs, loop: true);
                    }
                    else
                    {
                        anim.Keyframes(ind2Ref.Value, AnimChannel.TranslateX, new Keyframe[]
                        {
                            new(0.000f, ts.Container2AnimationStartPosition, Easing.Linear),
                            new(0.375f, ts.Container2AnimationStartPosition, Easing.Linear),
                            new(1.000f, ts.Container2AnimationEndPosition, IndetEase),
                        }, LoopMs, loop: true, cadence: Cadence.Display);
                    }
                }
            }, DepKey.From(HashCode.Combine(Width, State)));

            float ind2Width = nonNormal ? Width : ts.Indicator2Width;

            Action<NodeHandle> ind1Capture = h => ind1Ref.Value = h;
            Action<NodeHandle> ind2Capture = h => ind2Ref.Value = h;

            var track = Parts.Apply(PartTrack, new BoxEl
            {
                Width = Width,
                Height = TrackHeight,
                OffsetY = (MinHeight - TrackHeight) / 2f,
                Corners = CornerRadius4.All(TrackRadius),
                Fill = Tok.StrokeControlStrongDefault,
                Opacity = 0f,
            });

            var ind1 = new BoxEl
            {
                Width = ts.Indicator1Width,
                Height = MinHeight,
                Corners = CornerRadius4.All(IndicatorRadius),
                Fill = fg,
                Opacity = nonNormal ? 0f : 1f,
                OnRealized = ind1Capture,
            };
            var ind2 = new BoxEl
            {
                Width = ind2Width,
                Height = MinHeight,
                Corners = CornerRadius4.All(IndicatorRadius),
                Fill = fg,
                OnRealized = ind2Capture,
            };
            if (Parts is { } p)
            {
                var m1 = p.Apply(PartFill, ind1);
                ind1 = m1 with { OnRealized = TemplateParts.Chain(ind1Capture, m1.OnRealized) };
                var m2 = p.Apply(PartFill, ind2);
                ind2 = m2 with { OnRealized = TemplateParts.Chain(ind2Capture, m2.OnRealized) };
            }

            var bar = new BoxEl
            {
                ZStack = true,
                Width = Width,
                Height = MinHeight,
                ClipToBounds = true,
                Role = AutomationRole.ProgressBar,
                Children = [track, ind1, ind2],
            };

            // Stretch wrapper (this component's HostNode — what UseMeasuredWidth reports): Grow=1/Basis=0 claims the
            // full main-axis span if the parent is a row, AlignSelf=Stretch claims the full cross-axis span if the
            // parent is a column — the same two-axis idiom DensityPlot uses for "stretch to the width the parent
            // offers" regardless of which kind of parent it's dropped into.
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Grow = 1f, Basis = 0f, AlignSelf = FlexAlign.Stretch,
                Role = AutomationRole.None,
                Children = [bar],
            };
        }
    }
}
