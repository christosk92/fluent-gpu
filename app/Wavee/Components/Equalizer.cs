using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The now-playing equalizer — three bottom-anchored bars, looping + phase-staggered while PLAYING, settled at a low
// static height when paused. Shared by the track rows (#-cell) AND the content cards' now-playing overlay. Keyed by
// `animate` so a play↔pause flip remounts the bars with the right looping state.
public static class WaveeEqualizer
{
    public static Element Of(bool animate, ColorF color, float height = 13f) => Of(animate, () => color, height);

    public static Element Of(bool animate, Func<ColorF> color, float height = 13f) => new BoxEl
    {
        Key = animate ? "eq-play" : "eq-pause",
        Direction = 0, AlignItems = FlexAlign.End, Justify = FlexJustify.Center, Gap = 2f, Height = height,
        Children =
        [
            Embed.Comp(() => new EqBar(0, animate, color, height)),
            Embed.Comp(() => new EqBar(1, animate, color, height)),
            Embed.Comp(() => new EqBar(2, animate, color, height)),
        ],
    };
}

// One equalizer bar: a bottom-anchored bar whose ScaleY loops (phase-staggered per index) while PLAYING; a single
// static keyframe holds it low when paused. Its parent is keyed by `animate`, so a play↔pause flip remounts it with
// the right looping state. The color provider is fixed for the bar's lifetime but resolves its semantic token live.
sealed class EqBar : Component
{
    static readonly float[][] Patterns =
    [
        [0.35f, 0.95f, 0.45f, 1.00f, 0.35f],
        [0.85f, 0.40f, 1.00f, 0.55f, 0.85f],
        [0.50f, 1.00f, 0.35f, 0.80f, 0.50f],
    ];
    readonly int _i;
    readonly bool _animate;
    readonly Func<ColorF> _color;
    readonly float _h;
    public EqBar(int i, bool animate, Func<ColorF> color, float h) { _i = i; _animate = animate; _color = color; _h = h; }

    public override Element Render()
    {
        var p = Patterns[_i % Patterns.Length];
        Keyframe[] keys = _animate
            ? [new(0f, p[0]), new(0.25f, p[1]), new(0.5f, p[2]), new(0.75f, p[3]), new(1f, p[4])]
            : [new(0f, 0.4f), new(1f, 0.4f)];
        UseKeyframes(AnimChannel.ScaleY, keys, _animate ? 850f : 1f, _animate, DepKey.Empty);
        return new BoxEl
        {
            Width = 2.5f, Height = _h, Corners = CornerRadius4.All(1.25f), Fill = _color(),
            AlignSelf = FlexAlign.End, TransformOriginY = 1f,   // scale up from the bottom edge
        };
    }
}
