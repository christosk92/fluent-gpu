using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  Shared animation types — the channel vocabulary + dynamics POD used across the engine, controls, and DSL.
//  Extracted from the former AnimEngine.cs when the engine was reworked onto the AnimValue slab (the AnimEngine
//  class is now the slab-based partials AnimScheduler.*.cs, renamed to AnimEngine). These types are unchanged.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Animatable channels. Transform channels compose into LocalTransform (TransformDirty); Opacity + the presented
/// SizeW/SizeH → PaintDirty. LayoutW/LayoutH are the one deliberate exception to "animation never relays out": a
/// SizeMode.Reflow track writes the interpolated size into LayoutInput each tick and the host re-solves the nearest
/// layout boundary, so neighbours reflow smoothly.</summary>
public enum AnimChannel : byte { TranslateX, TranslateY, ScaleX, ScaleY, Rotation, Opacity, SizeW, SizeH, StrokeTrimStart, StrokeTrimEnd, ClipL, ClipT, ClipR, ClipB, LayoutW, LayoutH, BlurSigma, BrushFade, HoverFade, PressFade }

public enum IntegrationMode : byte { Eased, Spring }

/// <summary>How a track combines with other tracks on the same channel (CSS animation-composition).</summary>
public enum CompositeOp : byte { Replace, Add, Accumulate }

/// <summary>A spring (stateful ODE) — no duration; integrates toward Target, carrying velocity across retargets.</summary>
public readonly struct SpringParams
{
    public readonly float Stiffness, Damping, Mass, RestEps;
    public SpringParams(float stiffness, float damping, float mass = 1f, float restEps = 0.001f)
        => (Stiffness, Damping, Mass, RestEps) = (stiffness, damping, mass, restEps);

    /// <summary>response = approx settle time (s); dampingRatio 1 = critical (no overshoot), &lt;1 = bouncy.</summary>
    public static SpringParams FromResponse(float responseSec, float dampingRatio = 1f, float mass = 1f)
    {
        float w = (2f * MathF.PI) / MathF.Max(responseSec, 1e-3f);   // natural frequency
        return new SpringParams(w * w * mass, 2f * dampingRatio * w * mass, mass);
    }
    public static SpringParams Default => FromResponse(0.35f, 0.75f);
}

/// <summary>One keyframe: a normalized offset (0..1), its value, and the easing of the segment leading INTO it.</summary>
public readonly record struct Keyframe
{
    public float Offset { get; init; }
    public float Value { get; init; }
    public EasingSpec Easing { get; init; }

    public Keyframe(float offset, float value) : this(offset, value, EasingSpec.Default) { }
    public Keyframe(float offset, float value, Easing easing) : this(offset, value, (EasingSpec)easing) { }
    public Keyframe(float offset, float value, EasingSpec easing)
    {
        Offset = offset;
        Value = value;
        Easing = easing;
    }
}

/// <summary>A value source (scroll offset, playback ms, a custom MotionValue) that can drive a timeline instead of wall-time.
/// (Retained for parity; the index-based SignalSource that retires this List&lt;Func&lt;float&gt;&gt; closure model is a follow-up.)</summary>
public sealed class DrivenClockTable
{
    private readonly List<Func<float>> _sources = new();
    public int Register(Func<float> source) { _sources.Add(source); return _sources.Count - 1; }
    public float Sample(int i) => (uint)i < (uint)_sources.Count ? _sources[i]() : 0f;
}
