namespace FluentGpu.Foundation;

/// <summary>
/// Easing curves — a FOUNDATIONAL motion primitive shared by every layer that animates: the animation-track engine,
/// declarative transitions, scroll-bind output shaping, and the image placeholder->art cross-fade. Living in Foundation
/// lets any subsystem (Dsl, Scene, Render, Animation) spec a transition with the same vocabulary. The set spans three
/// families: the classic CSS/Penner curves, the real Fluent 2 / WinUI control curves, and the transitions.dev
/// "expressive" overshoot palette.
/// <para>
/// HOW TO READ THE GRAPHS below (a '·' dot grid framed by '│' and '└─' axes): the horizontal axis is normalized time t
/// (0 at the left edge -> 1 at the right); the vertical axis is the eased output (0 at the bottom -> 1 near the top).
/// A flat-looking start means "slow" (little output change per unit time); a steep '#' run means "fast". A dashed '-'
/// line marks the 1.0 target, drawn only for curves that overshoot above it or dip below 0, so you can see by how much.
/// The picture is the curve's PERSONALITY — pick by the shape you want the eye to feel, not by the name.
/// </para>
/// <para>
/// Picking quickly: things ARRIVING want an ease-OUT (fast then settle: <see cref="EaseOut"/>, <see cref="SmoothOut"/>,
/// <see cref="FluentDecelerate"/>); things LEAVING want an ease-IN (<see cref="EaseIn"/>, <see cref="FluentAccelerate"/>);
/// an on-screen MOVE wants an ease-in-out (<see cref="EaseInOut"/>, <see cref="FluentStandard"/>); a value DRIVEN by a
/// continuous input (a scroll-linked fade) usually wants <see cref="Linear"/> so the output mirrors the input instead of
/// surprising the user. The overshoot curves (<see cref="Overshoot"/>, <see cref="Pop"/>, <see cref="OvershootStrong"/>,
/// <see cref="Back"/>, <see cref="Elastic"/>) leave the [0,1] range mid-flight — only apply them to properties that
/// tolerate it (scale/translate), never to opacity or color.
/// </para>
/// </summary>
public enum Easing : byte
{
    // ── classic CSS / Penner curves ───────────────────────────────────────────────────────────────────────
    /// <summary><b>Linear</b> — no easing; output tracks input 1:1 at a constant rate.
    /// <para>Mechanical, uniform motion. The right choice when the value is DRIVEN by a continuous input (scroll-linked fades/parallax, scrubbing, a progress bar) — anything where the output must mirror the input, not editorialize it. y = t.</para>
    /// <code>
    /// │···························#
    /// │·······················#####
    /// │···················#####····
    /// │················####········
    /// │············#####···········
    /// │·········####···············
    /// │·····#####··················
    /// │·#####······················
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    Linear,
    /// <summary><b>EaseIn</b> — accelerate from rest — slow start, fast finish.
    /// <para>Builds speed; the object lingers then takes off. Reads as anticipation / departure. Good for content LEAVING that should pick up speed as it goes. y = t squared.</para>
    /// <code>
    /// │···························#
    /// │·························###
    /// │·······················###··
    /// │·····················###····
    /// │··················####······
    /// │···············####·········
    /// │···········#####············
    /// │·····#######················
    /// │######······················
    /// └────────────────────────────
    /// </code></summary>
    EaseIn,
    /// <summary><b>EaseOut</b> — decelerate to rest — fast start, gentle settle.
    /// <para>The everyday 'feels natural' entrance: covers most of the distance immediately, then glides to a stop. Default for things ARRIVING / settling into place. y = 1-(1-t) squared.</para>
    /// <code>
    /// │·······················#####
    /// │·················#######····
    /// │·············#####··········
    /// │··········####··············
    /// │·······####·················
    /// │·····###····················
    /// │···###······················
    /// │·###························
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    EaseOut,
    /// <summary><b>EaseInOut</b> — symmetric ease — slow start, fast middle, slow end.
    /// <para>Balanced, polite reposition for an on-screen A-to-B move with no emphasis on either end. Piecewise quadratic (the engine's plain ease curve).</para>
    /// <code>
    /// │························####
    /// │····················#####···
    /// │·················####·······
    /// │···············###··········
    /// │·············###············
    /// │···········###··············
    /// │········####················
    /// │····#####···················
    /// │#####·······················
    /// └────────────────────────────
    /// </code></summary>
    EaseInOut,
    /// <summary><b>Sine</b> — the gentlest acceleration — a quarter-sine ramp.
    /// <para>Barely-there ease-in: a very soft, organic speed-up with no abruptness anywhere. Use when even EaseIn feels too strong. y = 1-cos(t*pi/2).</para>
    /// <code>
    /// │···························#
    /// │·························###
    /// │······················####··
    /// │····················###·····
    /// │·················####·······
    /// │··············####··········
    /// │··········#####·············
    /// │·····######·················
    /// │######······················
    /// └────────────────────────────
    /// </code></summary>
    Sine,
    /// <summary><b>Quad</b> — quadratic ease-in (t squared) — mild acceleration.
    /// <para>Identical shape to EaseIn; named for callers who think in polynomial degree. The mildest of the Quad/Cubic/Expo ease-in family.</para>
    /// <code>
    /// │···························#
    /// │·························###
    /// │·······················###··
    /// │·····················###····
    /// │··················####······
    /// │···············####·········
    /// │···········#####············
    /// │·····#######················
    /// │######······················
    /// └────────────────────────────
    /// </code></summary>
    Quad,
    /// <summary><b>Cubic</b> — cubic ease-in (t cubed) — stronger, later acceleration.
    /// <para>More pronounced slow-start than Quad: the value hangs near 0 longer, then ramps harder. Mid-strength ease-in.</para>
    /// <code>
    /// │···························#
    /// │··························##
    /// │·························##·
    /// │·······················###··
    /// │·····················###····
    /// │···················###······
    /// │···············#####········
    /// │·········#######············
    /// │##########··················
    /// └────────────────────────────
    /// </code></summary>
    Cubic,
    /// <summary><b>Expo</b> — exponential ease-in — extreme: nothing, then a snap.
    /// <para>Stays almost flat at 0 for most of the duration, then rockets to 1 at the very end. Dramatic / mechanical-snap. y = 2^(10(t-1)).</para>
    /// <code>
    /// │···························#
    /// │···························#
    /// │··························##
    /// │·························##·
    /// │························##··
    /// │·······················##···
    /// │····················####····
    /// │··············#######·······
    /// │###############·············
    /// └────────────────────────────
    /// </code></summary>
    Expo,
    /// <summary><b>Back</b> — anticipation ease-in — dips BELOW 0 before moving.
    /// <para>Pulls back slightly (undershoots past the start) then springs forward to 1 — a wind-up. Note the values briefly go negative. y = t squared*(2.70158t - 1.70158).</para>
    /// <code>
    /// │---------------------------#
    /// │··························##
    /// │·························##·
    /// │························##··
    /// │·······················##···
    /// │·····················###····
    /// │···················###······
    /// │#########······#####········
    /// │········########············
    /// └────────────────────────────
    /// </code></summary>
    Back,
    /// <summary><b>Elastic</b> — rubber-band ease-in — oscillates, undershooting hard.
    /// <para>A damped spring run as an ease-in: swings well below 0 (dip ~ -0.36) before snapping up to 1. Playful, attention-grabbing; rarely right for subtle UI.</para>
    /// <code>
    /// │---------------------------#
    /// │···························#
    /// │···························#
    /// │··························##
    /// │··························#·
    /// │··················####····#·
    /// │###################··##··##·
    /// │······················####··
    /// │·······················##···
    /// └────────────────────────────
    /// </code></summary>
    Elastic,
    /// <summary><b>Bounce</b> — ease-out bounce — lands, then bounces and resettles.
    /// <para>Models a dropped ball: hits the target, rebounds a few diminishing hops, comes to rest. Toy-like; use sparingly.</para>
    /// <code>
    /// │··········##········##··####
    /// │·········####·····#######···
    /// │·········#··#######·········
    /// │········##··················
    /// │·······##···················
    /// │······##····················
    /// │····###·····················
    /// │··###·······················
    /// │###·························
    /// └────────────────────────────
    /// </code></summary>
    Bounce,

    // ── the real Fluent 2 / WinUI motion curves (cubic-bezier; P0=(0,0), P3=(1,1)) ─────────────────────────
    /// <summary><b>FluentStandard</b> — WinUI 'move / reposition' — strong ease-in-out.
    /// <para>The Fluent 2 workhorse for an element changing position on screen: slow ends, quick middle. cubic-bezier(0.8, 0.0, 0.2, 1.0). P0=(0,0), P3=(1,1).</para>
    /// <code>
    /// │······················######
    /// │·················######·····
    /// │···············###··········
    /// │··············##············
    /// │··············#·············
    /// │·············##·············
    /// │···········###··············
    /// │······######················
    /// │#######·····················
    /// └────────────────────────────
    /// </code></summary>
    FluentStandard,
    /// <summary><b>FluentDecelerate</b> — WinUI 'entrance / show' — heavy ease-out.
    /// <para>For something APPEARING: enters quickly and decelerates into place. The Fluent counterpart to EaseOut. cubic-bezier(0.1, 0.9, 0.2, 1.0).</para>
    /// <code>
    /// │···············#############
    /// │·······#########············
    /// │····####····················
    /// │···##·······················
    /// │··##························
    /// │··#·························
    /// │·##·························
    /// │·#··························
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    FluentDecelerate,
    /// <summary><b>FluentAccelerate</b> — WinUI 'exit / hide' — heavy ease-in, snaps out late.
    /// <para>For something LEAVING: holds near the start, then accelerates off fast. WARNING — because it sits near 0 for most of t and lunges only at the end, it is wrong for a value fade where you want steady, perceptible change (it reads as '1, 1, 1, then suddenly gone'). Use Linear/EaseOut for scroll-driven fades. cubic-bezier(0.9, 0.1, 1.0, 0.2).</para>
    /// <code>
    /// │···························#
    /// │···························#
    /// │···························#
    /// │···························#
    /// │··························##
    /// │························###·
    /// │····················#####···
    /// │·······##############·······
    /// │########····················
    /// └────────────────────────────
    /// </code></summary>
    FluentAccelerate,
    /// <summary><b>FluentPopOpen</b> — WinUI flyout/menu open — instant takeoff, long settle.
    /// <para>Snappy reveal: leaves the start immediately then decelerates the whole way. The MenuPopupThemeTransition curve. cubic-bezier(0, 0, 0, 1).</para>
    /// <code>
    /// │····················########
    /// │············#########·······
    /// │········#####···············
    /// │·····####···················
    /// │···###······················
    /// │··##························
    /// │·##·························
    /// │·#··························
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    FluentPopOpen,

    // ── transitions.dev "expressive" palette (opt-in app-author curves; the overshoot ones exceed 1.0 by design)
    /// <summary><b>SmoothOut</b> — expressive signature ease-out — quick then long glide.
    /// <para>A strong but smooth deceleration with NO overshoot — feels premium/calm. The transitions.dev signature curve; good for open/close/reposition. cubic-bezier(0.22, 1, 0.36, 1).</para>
    /// <code>
    /// │···············#############
    /// │·········#######············
    /// │······####··················
    /// │·····##·····················
    /// │···###······················
    /// │··##························
    /// │··#·························
    /// │·##·························
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    SmoothOut,
    /// <summary><b>Overshoot</b> — ease-out with a gentle overshoot past 1 (~1.04).
    /// <para>Eases out, nudges just past the target, then settles back — a subtle 'pop'. Intentionally exceeds 1.0 mid-flight. cubic-bezier(0.34, 1.36, 0.64, 1).</para>
    /// <code>
    /// │··············###########···
    /// │----------#####---------####
    /// │·······####·················
    /// │······##····················
    /// │····###·····················
    /// │···##·······················
    /// │··##························
    /// │·##·························
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    Overshoot,
    /// <summary><b>OvershootStrong</b> — pronounced springy overshoot — peaks near 2.0!
    /// <para>Shoots to roughly DOUBLE the target then bounces back to rest. Very bouncy; for playful emphasis (e.g. avatar hover-out). Make sure the animated property tolerates 2x (scale/translate, not opacity). cubic-bezier(0.34, 3.85, 0.64, 1).</para>
    /// <code>
    /// │·········#####··············
    /// │·······###···#####··········
    /// │·····###·········####·······
    /// │····##··············####····
    /// │---##------------------#####
    /// │··##························
    /// │·##·························
    /// │·#··························
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    OvershootStrong,
    /// <summary><b>Pop</b> — pop-in with moderate overshoot (~1.07).
    /// <para>Eases out and overshoots a touch before settling — the go-to for a number or element appearing with emphasis. cubic-bezier(0.34, 1.45, 0.64, 1).</para>
    /// <code>
    /// │·············##########·····
    /// │---------#####--------######
    /// │·······###··················
    /// │·····###····················
    /// │····##······················
    /// │···##·······················
    /// │··##························
    /// │·##·························
    /// │##··························
    /// └────────────────────────────
    /// </code></summary>
    Pop,
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

    /// <summary>True for a default-constructed spec ("no curve chosen") — consumers substitute their own default.</summary>
    public bool IsDefault => _hasValue == 0;

    internal float Evaluate(float t)
        => _hasValue == 0 ? Easings.Ease(Easing.EaseInOut, t)
         : _kind == 1 ? Easings.CubicBezier(t, _x1, _y1, _x2, _y2)
         : Easings.Ease(_named, t);

    /// <summary>The named curve if this is a NAMED spec, else <paramref name="fallback"/> (a default-constructed or
    /// cubic-bezier spec has no enum form). For consumers that need a named <see cref="Easing"/> — e.g. the engine's
    /// 0-alloc two-point generator, which stores only an EaseId byte (a custom bezier falls back to the named default).</summary>
    public Easing NamedOr(Easing fallback) => _hasValue != 0 && _kind == 0 ? _named : fallback;
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
        Easing.SmoothOut => CubicBezier(t, 0.22f, 1.0f, 0.36f, 1.0f),
        Easing.Overshoot => CubicBezier(t, 0.34f, 1.36f, 0.64f, 1.0f),
        Easing.OvershootStrong => CubicBezier(t, 0.34f, 3.85f, 0.64f, 1.0f),
        Easing.Pop => CubicBezier(t, 0.34f, 1.45f, 0.64f, 1.0f),
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
