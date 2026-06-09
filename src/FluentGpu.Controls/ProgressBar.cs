using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Animation;

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
    // ── WinUI sizes/corners (ProgressBar_themeresources.xaml) ──────────────────────────────────────
    const float MinHeight = 3f;          // ProgressBarMinHeight
    const float TrackHeight = 1f;         // ProgressBarTrackHeight
    const float IndicatorRadius = 1.5f;   // ProgressBarCornerRadius (indicator)
    const float TrackRadius = 0.5f;       // ProgressBarTrackCornerRadius
    const float DefaultWidth = 240f;

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

    // ── Determinate ────────────────────────────────────────────────────────────────────────────────
    /// <summary>Determinate progress; <paramref name="value"/> is clamped to 0..1, indicator width = value * width.
    /// <paramref name="state"/> selects the indicator color (Normal accent / Paused caution / Error critical).</summary>
    public static BoxEl Determinate(float value, float width = DefaultWidth, ProgressBarState state = ProgressBarState.Normal)
    {
        float v = value < 0f ? 0f : value > 1f ? 1f : value;
        return new BoxEl
        {
            ZStack = true,
            Width = width,
            Height = MinHeight,
            Role = AutomationRole.ProgressBar,
            Children =
            [
                // ProgressBarTrack: 1px, ControlStrongStrokeColorDefault, 0.5px corner, vertically centered in the band.
                new BoxEl
                {
                    Width = width,
                    Height = TrackHeight,
                    OffsetY = (MinHeight - TrackHeight) / 2f,
                    Corners = CornerRadius4.All(TrackRadius),
                    Fill = Tok.StrokeControlStrongDefault,
                },
                // DeterminateProgressBarIndicator: foreground fill, 1.5px corner, left-aligned, width = v * track width.
                new BoxEl
                {
                    Width = v * width,
                    Height = MinHeight,
                    Corners = CornerRadius4.All(IndicatorRadius),
                    Fill = ForegroundFor(state),
                },
            ],
        };
    }

    // ── Indeterminate ────────────────────────────────────────────────────────────────────────────
    /// <summary>Indeterminate progress: the two clipped accent indicators sweeping across the track on the WinUI
    /// ProgressBarTemplateSettings translate keyframes. In Paused/Error, the track hides and only indicator2 shows,
    /// recolored to caution/critical (matching WinUI's IndeterminatePaused / IndeterminateError visual states).</summary>
    public static Element Indeterminate(float width = DefaultWidth, ProgressBarState state = ProgressBarState.Normal)
        => Embed.Comp(() => new IndeterminateBar { Width = width, State = state });

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
        public float Width = DefaultWidth;
        public ProgressBarState State = ProgressBarState.Normal;

        public override Element Render()
        {
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
                        }, LoopMs, loop: true);
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
                        }, LoopMs, loop: true);
                    }
                }
            }, Width, State);

            // Indicator2 spans the full track in Paused/Error (SetProgressBarIndicatorWidth: 100%), else 60%.
            float ind2Width = nonNormal ? Width : ts.Indicator2Width;

            return new BoxEl
            {
                ZStack = true,
                Width = Width,
                Height = MinHeight,
                ClipToBounds = true,                 // Border Clip="...ClipRect" — the sweep is clipped to the track bounds
                Role = AutomationRole.ProgressBar,
                Children =
                [
                    // ProgressBarTrack — hidden (Opacity 0) in every indeterminate state in WinUI.
                    new BoxEl
                    {
                        Width = Width,
                        Height = TrackHeight,
                        OffsetY = (MinHeight - TrackHeight) / 2f,
                        Corners = CornerRadius4.All(TrackRadius),
                        Fill = Tok.StrokeControlStrongDefault,
                        Opacity = 0f,
                    },
                    // IndeterminateProgressBarIndicator (40% width) — visible only in the Normal indeterminate state.
                    new BoxEl
                    {
                        Width = ts.Indicator1Width,
                        Height = MinHeight,
                        Corners = CornerRadius4.All(IndicatorRadius),
                        Fill = fg,
                        Opacity = nonNormal ? 0f : 1f,
                        OnRealized = h => ind1Ref.Value = h,
                    },
                    // IndeterminateProgressBarIndicator2 (60% width, or 100% in Paused/Error) — always visible indeterminate.
                    new BoxEl
                    {
                        Width = ind2Width,
                        Height = MinHeight,
                        Corners = CornerRadius4.All(IndicatorRadius),
                        Fill = fg,
                        OnRealized = h => ind2Ref.Value = h,
                    },
                ],
            };
        }
    }
}
