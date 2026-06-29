using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// An animated "fluid" aurora backdrop (BetterLyrics-style): a few large, soft, palette-colored radial blobs that slowly
// drift and overlap into a shifting color wash. Built entirely from the engine's EXISTING radial-gradient fill + looping
// keyframe animation — no new engine primitive. (The true domain-warped value-noise shader is the higher-fidelity
// follow-up; this captures the "soft shifting color" feel safely.) Mounted only on the fullscreen surface while open, so
// the perpetual motion is gated and idles when the now-playing view closes.
sealed class FluidBackdrop : Component
{
    readonly Palette? _palette;
    public FluidBackdrop(Palette? palette) => _palette = palette;

    public override Element Render()
    {
        ColorF accent = _palette is { } p ? WaveePalette.Accent(p) : new ColorF(0.42f, 0.52f, 0.92f, 1f);
        ColorF c1 = accent;
        ColorF c2 = ColorF.Lerp(accent, new ColorF(0.93f, 0.40f, 0.62f, 1f), 0.55f);   // drift toward magenta
        ColorF c3 = ColorF.Lerp(accent, new ColorF(0.30f, 0.82f, 0.70f, 1f), 0.55f);   // drift toward teal

        return new BoxEl
        {
            Grow = 1f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
            Children =
            [
                Embed.Comp(() => new FluidBlob(c1, new Point2(0.30f, 0.32f),  44f,  30f, 17000f, 21000f)),
                Embed.Comp(() => new FluidBlob(c2, new Point2(0.74f, 0.58f), -38f,  40f, 23000f, 19000f)),
                Embed.Comp(() => new FluidBlob(c3, new Point2(0.52f, 0.84f),  30f, -34f, 26000f, 24000f)),
            ],
        };
    }
}

// One soft palette blob: a full-bleed radial gradient (color center → transparent edge) that drifts on a slow looping
// translate. Overlapping blobs blend into the aurora. Decorative (no hit-testing); animation rides the compositor.
sealed class FluidBlob : Component
{
    readonly ColorF _color;
    readonly Point2 _center;
    readonly float _dx, _dy, _durX, _durY;

    public FluidBlob(ColorF color, Point2 center, float dx, float dy, float durX, float durY)
    {
        _color = color; _center = center; _dx = dx; _dy = dy; _durX = durX; _durY = durY;
    }

    public override Element Render()
    {
        // Slow looping drift (seeded once at mount; rides the compositor — no re-render).
        UseKeyframes(AnimChannel.TranslateX, [new(0f, 0f), new(0.5f, _dx), new(1f, 0f)], _durX, loop: true);
        UseKeyframes(AnimChannel.TranslateY, [new(0f, 0f), new(0.5f, _dy), new(1f, 0f)], _durY, loop: true);

        return new BoxEl
        {
            Grow = 1f, Opacity = 0.6f, HitTestVisible = false,
            Gradient = RadialGradient(_center, new Point2(0.62f, 0.62f),
                new GradientStop(0f, _color with { A = 0.85f }),
                new GradientStop(1f, _color with { A = 0f })),
        };
    }
}
