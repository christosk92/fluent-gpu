using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using System;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ProgressRing brought to 1:1 with <c>controls/dev/ProgressRing</c>. WinUI renders the ring via a Lottie
/// <c>AnimatedVisualPlayer</c> (a continuous accent ring, NOT dots): a determinate variant whose accent arc's TrimEnd
/// sweeps 0→1 over a (default-transparent) track ring, and an indeterminate variant whose accent arc rotates while its
/// trim breathes. We reproduce both on the engine's SDF arc primitive (<see cref="ArcSpec"/>) — the determinate via a
/// <see cref="ProgressRingTemplateSettings"/>-computed sweep, the indeterminate via a rotation <c>Keyframes</c> loop at
/// the exact WinUI cadence (900° per 2.0s, two cubic-bezier 0.167,0.167→0.833,0.833 half-segments).
///
/// Dimensional canon (ProgressRing.xaml / AnimatedVisuals/ProgressRingIndeterminate.cpp): Width=Height=32 default,
/// MinWidth=MinHeight=16. Stroke is the Lottie weight, NOT the unused <c>ProgressRingStrokeThickness=4</c> themeresource:
/// the indeterminate ellipse uses <c>StrokeThickness(1.5F)</c> inside an 80px viewbox at <c>Scale:5,5</c> (effective
/// 1.5·5 = 7.5 in the viewbox), which maps to the 32px control as 7.5·32/80 = <b>3.0px</b> (= size·0.09375). We scale
/// it proportionally so larger rings keep the same visual weight. Foreground=AccentFillColorDefaultBrush
/// (Tok.AccentDefault), Background=ControlFillColorTransparentBrush (transparent — the default track is invisible,
/// exactly like WinUI). IsActive=false → the LayoutRoot fades to Opacity 0 (the Inactive visual state).
/// </summary>
public static class ProgressRing
{
    // WinUI defaults (ProgressRing.xaml): Width/Height = 32, MinWidth/MinHeight = 16.
    public const float DefaultSize = 32f;
    public const float MinSize = 16f;
    // Lottie stroke weight (AnimatedVisuals/ProgressRingIndeterminate.cpp: StrokeThickness(1.5F) inside an 80px viewbox
    // at Scale:5,5 ⇒ 7.5 viewbox units ⇒ 7.5·32/80 = 3.0px at the 32px default). The ProgressRingStrokeThickness=4
    // themeresource is NOT used by the Lottie player. We scale proportionally so larger rings keep the visual weight.
    const float StrokeAtDefault = 3.0f;   // == DefaultSize · 0.09375f

    /// <summary>
    /// Computed geometry for one ProgressRing (the audit's P3 TemplateSettings convention — mirrors WinUI's
    /// <c>ProgressRingTemplateSettings</c>, recomputed once from the control's size, never inside a per-frame bind).
    /// <paramref name="Sweep"/> is the determinate accent-arc sweep in degrees (value·360). For the indeterminate ring
    /// it is the fixed visible arc length the rotation loop spins.
    /// </summary>
    public readonly record struct ProgressRingTemplateSettings(float Size, float Stroke, float StartDeg, float Sweep)
    {
        public static ProgressRingTemplateSettings ForDeterminate(float size, float value)
        {
            float s = size < MinSize ? MinSize : size;
            float v = value < 0f ? 0f : value > 1f ? 1f : value;
            return new ProgressRingTemplateSettings(s, StrokeAtDefault * s / DefaultSize, 0f, v * 360f);
        }

        public static ProgressRingTemplateSettings ForIndeterminate(float size)
        {
            float s = size < MinSize ? MinSize : size;
            // The WinUI indeterminate arc is a FULL-circle ellipse (sweep 360°) whose visible length is carved by the
            // breathing StrokeTrim channels (0→0.5 ⇒ 0°→180°); the rotation loop spins the whole shape 0→900°.
            return new ProgressRingTemplateSettings(s, StrokeAtDefault * s / DefaultSize, 0f, 360f);
        }
    }

    // ── Indeterminate motion canon (AnimatedVisuals/ProgressRingIndeterminate.cpp) ───────────────────────────────
    // c_durationTicks = 20000000L → 2.0s per loop. Two animations run on the 2.0s timeline:
    //   • RotationAngleInDegrees: 0 → 450 (offset 0.5) → 900 (offset 1.0), each half on the Lottie cubic spline.
    //   • The ellipse's TrimEnd 0→0.5 over the FIRST half (offsets 0→0.5), then TrimStart 0→0.5 over the SECOND half
    //     (offsets 0.5→1.0), both on the spline. Net visible arc = TrimEnd−TrimStart breathes 0→0.5→0 (0°→180°→0°).
    const float IndeterminateDurationMs = 2000f;
    // WinUI Lottie cubic spline {0.167,0.167}{0.833,0.833} — a near-linear ease applied to each segment.
    static readonly EasingSpec IndeterminateSpline = EasingSpec.CubicBezier(0.167f, 0.167f, 0.833f, 0.833f);

    /// <summary>A determinate ring: the accent arc's sweep is <c>value·360°</c> from 12 o'clock clockwise, over a track
    /// ring. WinUI's default Background is <c>ControlFillColorTransparentBrush</c>, so the track is invisible unless a
    /// <paramref name="track"/> color is supplied. <paramref name="foreground"/> defaults to AccentFillColorDefault.</summary>
    public static BoxEl Determinate(float value /*0..1*/, float size = DefaultSize, bool isActive = true,
                                    ColorF? foreground = null, ColorF? track = null)
    {
        var ts = ProgressRingTemplateSettings.ForDeterminate(size, value);
        ColorF fg = foreground ?? Tok.AccentDefault;
        ColorF tk = track ?? ColorF.Transparent;   // WinUI default Background = ControlFillColorTransparentBrush
        return new BoxEl
        {
            ZStack = true, Width = ts.Size, Height = ts.Size,
            Opacity = isActive ? 1f : 0f,           // Inactive visual state: LayoutRoot.Opacity = 0
            Children = new Element[]
            {
                new BoxEl { Width = ts.Size, Height = ts.Size, Arc = new ArcSpec(tk, ts.Stroke, 0f, 360f, RoundCaps: false) },             // track ring
                new BoxEl { Width = ts.Size, Height = ts.Size, Arc = new ArcSpec(fg, ts.Stroke, ts.StartDeg, ts.Sweep, RoundCaps: true) }, // accent sweep (round caps, like the Lottie)
            },
        };
    }

    /// <summary>An indeterminate spinner: a round-capped accent arc rotating continuously at the WinUI cadence
    /// (900° per 2.0s with the Lottie cubic-bezier spline). <paramref name="isActive"/>=false fades it out (Inactive).</summary>
    public static Element Indeterminate(float size = DefaultSize, bool isActive = true, ColorF? foreground = null)
        => Embed.Comp(() => new SpinnerRing { Size = size, IsActive = isActive, Foreground = foreground });

    internal sealed class SpinnerRing : Component
    {
        public float Size = DefaultSize;
        public bool IsActive = true;
        public ColorF? Foreground;

        public override Element Render()
        {
            var ts = ProgressRingTemplateSettings.ForIndeterminate(Size);
            ColorF fg = Foreground ?? Tok.AccentDefault;

            // WinUI ProgressRingIndeterminate: 2.0s loop. RotationAngleInDegrees 0 → 450 (0.5) → 900 (1.0), each half on
            // the Lottie spline — this loop track spins the whole host (and the arc child with it).
            UseKeyframes(AnimChannel.Rotation, new Keyframe[]
            {
                new(0f, 0f, Easing.Linear),
                new(0.5f, 450f, IndeterminateSpline),
                new(1f, 900f, IndeterminateSpline),
            }, IndeterminateDurationMs, loop: true);

            // The breathing arc-length lives on the ARC child, not the host (the host has no stroke). Capture the child
            // handle and drive the two StrokeTrim channels directly (UseKeyframes only targets HostNode). Visible arc =
            // TrimEnd−TrimStart: TrimEnd 0→0.5 over the first half (grows 0°→180°), then TrimStart 0→0.5 over the second
            // (shrinks 180°→0°) — exactly the Lottie TrimStart/TrimEnd 0→0.5 cadence over the 2.0s loop.
            var arcRef = UseRef<NodeHandle>(default);
            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || arcRef.Value.IsNull || !scene.IsLive(arcRef.Value)) return;

                anim.Keyframes(arcRef.Value, AnimChannel.StrokeTrimEnd, new Keyframe[]
                {
                    new(0f, 0.0001f, Easing.Linear),                 // Lottie TrimEnd seed 9.99999975E-05F
                    new(0.5f, 0.5f, IndeterminateSpline),            // grows to 0.5 (180°) at the half
                    new(1f, 0.5f, Easing.Linear),                    // hold 0.5 through the second half
                }, IndeterminateDurationMs, loop: true);

                anim.Keyframes(arcRef.Value, AnimChannel.StrokeTrimStart, new Keyframe[]
                {
                    new(0f, 0f, Easing.Linear),
                    new(0.5f, 0f, Easing.Linear),                    // held 0 through the first half
                    new(1f, 0.5f, IndeterminateSpline),              // catches up to 0.5 (collapses the visible arc)
                }, IndeterminateDurationMs, loop: true);
            }, Size);

            return new BoxEl
            {
                ZStack = true, Width = ts.Size, Height = ts.Size,
                Opacity = IsActive ? 1f : 0f,   // Inactive visual state: LayoutRoot.Opacity = 0
                Children = new Element[]
                {
                    new BoxEl
                    {
                        Width = ts.Size, Height = ts.Size,
                        Arc = new ArcSpec(fg, ts.Stroke, ts.StartDeg, ts.Sweep, RoundCaps: true),
                        OnRealized = h => arcRef.Value = h,
                    },
                },
            };
        }
    }
}
