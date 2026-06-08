using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using System;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ProgressRing: a smooth, round-capped stroked arc (WinUI renders it via a Lottie AnimatedVisual — a continuous
/// accent ring, NOT dots). Built on the engine's SDF arc primitive (<see cref="ArcSpec"/>). Default 32×32 with a 4px
/// stroke (ProgressRingStrokeThickness), matching WinUI. <see cref="Determinate"/> sweeps the accent arc to <c>value</c>
/// over a faint track ring; <see cref="Indeterminate"/> rotates a fixed accent arc continuously.
/// </summary>
public static class ProgressRing
{
    // WinUI ProgressRingStrokeThickness = 4 at the 32px default → stroke = size/8.
    static float Stroke(float size) => MathF.Max(2f, size / 8f);

    /// <summary>A determinate ring: the accent arc sweeps from 12 o'clock clockwise to <paramref name="value"/> (0..1)
    /// over a faint full track ring.</summary>
    public static BoxEl Determinate(float value /*0..1*/, float size = 32f)
    {
        float v = value < 0f ? 0f : value > 1f ? 1f : value;
        float st = Stroke(size);
        return new BoxEl
        {
            ZStack = true, Width = size, Height = size,
            Children = new Element[]
            {
                new BoxEl { Width = size, Height = size, Arc = new ArcSpec(Tok.FillControlStrong, st, 0f, 360f, RoundCaps: false) },  // track
                new BoxEl { Width = size, Height = size, Arc = new ArcSpec(Tok.AccentDefault, st, 0f, v * 360f) },                    // progress
            },
        };
    }

    /// <summary>An indeterminate spinner: a fixed accent arc (~one third of the ring) rotating continuously.</summary>
    public static Element Indeterminate(float size = 32f) => Embed.Comp(() => new SpinnerRing { Size = size });

    internal sealed class SpinnerRing : Component
    {
        public float Size = 32f;

        public override Element Render()
        {
            UseKeyframes(AnimChannel.Rotation, new Keyframe[] { new(0f, 0f), new(1f, 360f) }, 1100f, loop: true);
            float st = Stroke(Size);
            return new BoxEl
            {
                ZStack = true, Width = Size, Height = Size,
                Children = new Element[]
                {
                    new BoxEl { Width = Size, Height = Size, Arc = new ArcSpec(Tok.AccentDefault, st, 0f, 120f) },
                },
            };
        }
    }
}
