using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The press lean of <see cref="TileMotion.Tilt"/>: the scale while pressed, the largest lean (shear, as a fraction
/// of the box), and the release time.</summary>
public readonly record struct TiltSpec(float Scale = 0.97f, float MaxLean = 0.02f, float ReleaseMs = 120f);

/// <summary>Zune's tile motion in 2-D (the engine's transform is a 2x3 affine; perspective is out of scope).</summary>
public static class TileMotion
{
    /// <summary>Leans the child toward the press point while pressed: a bound Transform (scale, a small shear, 1-2 DIP of
    /// travel). No relayout, no render per move. Reduced motion: none.</summary>
    public static Element Tilt(Element child, TiltSpec? spec = null) => child;

    /// <summary>Shows <paramref name="back"/> instead of <paramref name="front"/> when <paramref name="turned"/> is true: scale X
    /// 1 -> 0 -> 1 with the swap at the half. Reduced motion: a cross-fade. Both faces stay mounted for the turn only.</summary>
    public static Element TurnOver(IReadSignal<bool> turned, Func<Element> front, Func<Element> back,
        float turnMs = 420f, float backMs = 360f)
        => Embed.Comp(new TurnOverView.Props(turned, front, back, turnMs, backMs), () => new TurnOverView());
}

internal sealed class TurnOverView : Component
{
    public sealed record Props(IReadSignal<bool> Turned, Func<Element> Front, Func<Element> Back, float TurnMs, float BackMs);

    public override Element Render()
    {
        var p = UseProps<Props>();
        bool turned = p.Turned.Value;
        return new BoxEl { Direction = 1, Grow = 1f, Children = [turned ? p.Back() : p.Front()] };
    }
}
