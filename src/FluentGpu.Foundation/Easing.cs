namespace FluentGpu.Foundation;

/// <summary>
/// Easing curves — a FOUNDATIONAL motion primitive shared by every layer that animates: the animation-track engine,
/// declarative transitions, and the image placeholder→art cross-fade. Living in Foundation lets any subsystem (Dsl,
/// Scene, Render, Animation) spec a transition with the same vocabulary. Includes the real Fluent 2 / WinUI curves.
/// </summary>
public enum Easing : byte
{
    Linear, EaseIn, EaseOut, EaseInOut, Sine, Quad, Cubic, Expo, Back, Elastic, Bounce,
    // The real Fluent 2 / WinUI motion curves (cubic-bezier control points, P0=(0,0) P3=(1,1)):
    FluentStandard,    // move / reposition — cubic-bezier(0.8, 0.0, 0.2, 1.0)
    FluentDecelerate,  // entrance / show   — cubic-bezier(0.1, 0.9, 0.2, 1.0)
    FluentAccelerate,  // exit / hide       — cubic-bezier(0.9, 0.1, 1.0, 0.2)
    FluentPopOpen,     // flyout/menu open  — cubic-bezier(0, 0, 0, 1)  (the WinUI MenuPopupThemeTransition curve)
}

public readonly record struct EasingSpec
{
    private readonly byte _hasValue;
    private readonly byte _kind;
    private readonly Easing _named;
    private readonly float _x1, _y1, _x2, _y2;

    private EasingSpec(Easing named)
    {
        _hasValue = 1;
        _kind = 0;
        _named = named;
        _x1 = _y1 = _x2 = _y2 = 0f;
    }

    private EasingSpec(float x1, float y1, float x2, float y2)
    {
        _hasValue = 1;
        _kind = 1;
        _named = Easing.Linear;
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    public static EasingSpec Default => default;
    public static EasingSpec Named(Easing easing) => new(easing);
    public static EasingSpec CubicBezier(float x1, float y1, float x2, float y2) => new(x1, y1, x2, y2);
    public static implicit operator EasingSpec(Easing easing) => new(easing);

    internal float Evaluate(float t)
        => _hasValue == 0 ? Easings.Ease(Easing.EaseInOut, t)
         : _kind == 1 ? Easings.CubicBezier(t, _x1, _y1, _x2, _y2)
         : Easings.Ease(_named, t);
}

/// <summary>Evaluates an <see cref="Easing"/> curve at normalized time t (0..1).</summary>
public static class Easings
{
    public static float Ease(EasingSpec e, float t) => e.Evaluate(t);

    public static float Ease(Easing e, float t) => e switch
    {
        Easing.EaseIn => t * t,
        Easing.EaseOut => 1f - (1f - t) * (1f - t),
        Easing.EaseInOut => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
        Easing.Sine => 1f - MathF.Cos(t * MathF.PI * 0.5f),
        Easing.Quad => t * t,
        Easing.Cubic => t * t * t,
        Easing.Expo => t <= 0f ? 0f : MathF.Pow(2f, 10f * (t - 1f)),
        Easing.Back => t * t * (2.70158f * t - 1.70158f),
        Easing.Elastic => t == 0f || t == 1f ? t : -MathF.Pow(2f, 10f * (t - 1f)) * MathF.Sin((t - 1.075f) * (2f * MathF.PI) / 0.3f),
        Easing.Bounce => Bounce(t),
        Easing.FluentStandard => CubicBezier(t, 0.8f, 0.0f, 0.2f, 1.0f),
        Easing.FluentDecelerate => CubicBezier(t, 0.1f, 0.9f, 0.2f, 1.0f),
        Easing.FluentAccelerate => CubicBezier(t, 0.9f, 0.1f, 1.0f, 0.2f),
        Easing.FluentPopOpen => CubicBezier(t, 0.0f, 0.0f, 0.0f, 1.0f),
        _ => t,   // Linear
    };

    /// <summary>Evaluate a CSS/WinUI cubic-bezier easing y(t): find the curve parameter s where x(s)==t (Newton +
    /// bisection fallback), then return y(s). P0=(0,0), P3=(1,1), control points (x1,y1),(x2,y2).</summary>
    internal static float CubicBezier(float t, float x1, float y1, float x2, float y2)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        static float Cx(float s, float x1, float x2) { float u = 1f - s; return 3f * u * u * s * x1 + 3f * u * s * s * x2 + s * s * s; }
        static float Cy(float s, float y1, float y2) { float u = 1f - s; return 3f * u * u * s * y1 + 3f * u * s * s * y2 + s * s * s; }
        static float Dx(float s, float x1, float x2) { float u = 1f - s; return 3f * u * u * x1 + 6f * u * s * (x2 - x1) + 3f * s * s * (1f - x2); }

        float guess = t;
        for (int i = 0; i < 6; i++)   // Newton-Raphson
        {
            float x = Cx(guess, x1, x2) - t;
            if (MathF.Abs(x) < 1e-4f) return Cy(guess, y1, y2);
            float d = Dx(guess, x1, x2);
            if (MathF.Abs(d) < 1e-6f) break;
            guess -= x / d;
        }
        float lo = 0f, hi = 1f; guess = t;   // bisection fallback
        for (int i = 0; i < 20; i++)
        {
            float x = Cx(guess, x1, x2);
            if (MathF.Abs(x - t) < 1e-4f) break;
            if (x < t) lo = guess; else hi = guess;
            guess = (lo + hi) * 0.5f;
        }
        return Cy(guess, y1, y2);
    }

    private static float Bounce(float t)
    {
        const float n1 = 7.5625f, d1 = 2.75f;
        if (t < 1f / d1) return n1 * t * t;
        if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1; return n1 * t * t + 0.984375f;
    }
}
