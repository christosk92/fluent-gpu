using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Animation;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ProgressBar: a thin 1px track with a 3px accent indicator on top (centered vertically in the 3px band).
/// <see cref="Determinate"/> fills the indicator to a 0..1 value; <see cref="Indeterminate"/> sweeps a short accent
/// segment across the track on a looping translate animation.
/// </summary>
public static class ProgressBar
{
    private const float IndicatorHeight = 3f;
    private const float TrackHeight = 1f;
    private const float IndicatorRadius = 1.5f;
    private const float TrackRadius = 0.5f;

    /// <summary>Determinate progress; <paramref name="value"/> is clamped to 0..1, indicator width = value * width.</summary>
    public static BoxEl Determinate(float value, float width = 240f)
    {
        float v = value < 0f ? 0f : value > 1f ? 1f : value;
        return new BoxEl
        {
            ZStack = true,
            Width = width,
            Height = IndicatorHeight,
            Role = AutomationRole.ProgressBar,
            Children =
            [
                new BoxEl
                {
                    Width = width,
                    Height = TrackHeight,
                    OffsetY = (IndicatorHeight - TrackHeight) / 2f,
                    Corners = CornerRadius4.All(TrackRadius),
                    Fill = Tok.FillControlStrong,
                },
                new BoxEl
                {
                    Width = v * width,
                    Height = IndicatorHeight,
                    Corners = CornerRadius4.All(IndicatorRadius),
                    Fill = Tok.AccentDefault,
                },
            ],
        };
    }

    /// <summary>Indeterminate progress: a looping accent segment sweeping across the track.</summary>
    public static Element Indeterminate(float width = 240f)
        => Embed.Comp(() => new IndeterminateBar { Width = width });

    private sealed class IndeterminateBar : Component
    {
        public float Width = 240f;

        public override Element Render() => new BoxEl
        {
            ZStack = true,
            Width = Width,
            Height = IndicatorHeight,
            ClipToBounds = true,
            Role = AutomationRole.ProgressBar,
            Children =
            [
                new BoxEl
                {
                    Width = Width,
                    Height = TrackHeight,
                    OffsetY = (IndicatorHeight - TrackHeight) / 2f,
                    Corners = CornerRadius4.All(TrackRadius),
                    Fill = Tok.FillControlStrong,
                },
                Embed.Comp(() => new SweepSegment { Width = Width }),
            ],
        };
    }

    private sealed class SweepSegment : Component
    {
        public float Width = 240f;

        public override Element Render()
        {
            UseKeyframes(AnimChannel.TranslateX, new Keyframe[]
            {
                new(0f, -Width * 0.4f),
                new(1f, Width),
            }, 1600f, loop: true);

            return new BoxEl
            {
                Width = Width * 0.4f,
                Height = IndicatorHeight,
                Corners = CornerRadius4.All(IndicatorRadius),
                Fill = Tok.AccentDefault,
            };
        }
    }
}
