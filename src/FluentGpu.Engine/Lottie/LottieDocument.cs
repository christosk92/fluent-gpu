using FluentGpu.Foundation;

namespace FluentGpu.Lottie;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  The Bodymovin/Lottie document model — the exact shape LottieParser.cs builds and LottieCompiler.cs consumes.
//  Deliberately NOT the full Lottie spec: it covers what a Bodymovin 5.6.5 export of layer-transform + opacity +
//  trim + 0-4 morphing bezier shapes actually uses (docs/plans/wavee onboarding assets) — no images, text,
//  expressions, layer masks, repeaters, or animated-color tracks. See docs/plans (wavee) lottie-heroes plan §2.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One Lottie easing pair (<c>o</c> out / <c>i</c> in bezier handles) OR a hold (<c>h:1</c>) for the segment
/// leading INTO the keyframe that carries it — mirrors <see cref="FluentGpu.Animation.Keyframe.Easing"/>'s "easing of
/// the segment leading into this key" convention, so compiling straight across needs no re-interpretation.</summary>
public readonly record struct LottieEase(float OutX, float OutY, float InX, float InY, bool Hold)
{
    public static LottieEase Default => new(0.167f, 0.167f, 0.833f, 0.833f, false);
    public EasingSpec ToSpec() => Hold ? EasingSpec.Named(Easing.Hold) : EasingSpec.CubicBezier(OutX, OutY, InX, InY);
}

/// <summary>One keyframe of an <see cref="AnimScalar"/> track: layer-local frame <see cref="T"/>, the value AT this
/// frame, and the easing of the segment leading into it (<see cref="LottieEase.Default"/> until a following key's
/// <c>i</c>/<c>o</c> overwrite it — the parser fills this in from the segment's own <c>i</c>/<c>o</c>).</summary>
public sealed class ScalarKey
{
    public float T;
    public float Value;
    public LottieEase Ease = LottieEase.Default;
}

/// <summary>One keyframe of an <see cref="AnimVec2"/> track. <see cref="To"/>/<see cref="Ti"/> are the Lottie spatial
/// tangents (<c>to</c>/<c>ti</c>) relative to <see cref="Value"/> — non-zero only for a position track whose segment is
/// a true spatial bezier, not a straight line. <see cref="EaseX"/>/<see cref="EaseY"/> are the (possibly per-axis)
/// temporal easing of the segment leading into this key.</summary>
public sealed class Vec2Key
{
    public float T;
    public Point2 Value;
    public Point2 To;
    public Point2 Ti;
    public LottieEase EaseX = LottieEase.Default;
    public LottieEase EaseY = LottieEase.Default;
}

/// <summary>One keyframe of an <see cref="AnimPath"/> (morphing bezier shape) track.</summary>
public sealed class PathKey
{
    public float T;
    public LottieBezier Shape = new();
    public LottieEase Ease = LottieEase.Default;
}

/// <summary>A Lottie vector-shape vertex set: on-curve vertices <see cref="V"/> with per-vertex OUT (<see cref="O"/>)
/// and IN (<see cref="I"/>) control-point OFFSETS (relative to the matching <see cref="V"/> entry — Lottie's own
/// convention, unlike the engine's absolute <c>PathBuilder</c> control points). <see cref="Closed"/> mirrors the
/// shape's own <c>c</c> flag.</summary>
public sealed class LottieBezier
{
    public Point2[] V = [];
    public Point2[] I = [];
    public Point2[] O = [];
    public bool Closed;

    /// <summary>A shallow value-copy (new arrays, same points) — used when baking a synthetic morph sample so the
    /// original keyframe shapes are never mutated.</summary>
    public LottieBezier Clone() => new() { V = (Point2[])V.Clone(), I = (Point2[])I.Clone(), O = (Point2[])O.Clone(), Closed = Closed };
}

/// <summary>An animatable scalar (Lottie <c>{a,k}</c> where <c>k</c> is a number or a keyframe array).</summary>
public sealed class AnimScalar
{
    public float Static;
    public ScalarKey[]? Keys;
    public bool IsAnimated => Keys is { Length: > 1 };

    public static AnimScalar Of(float v) => new() { Static = v };
}

/// <summary>An animatable 2D vector (Lottie <c>{a,k}</c> where <c>k</c> is <c>[x,y(,z)]</c> or a keyframe array).</summary>
public sealed class AnimVec2
{
    public Point2 Static;
    public Vec2Key[]? Keys;
    public bool IsAnimated => Keys is { Length: > 1 };

    public static AnimVec2 Of(Point2 v) => new() { Static = v };
}

/// <summary>An animatable vector shape (Lottie <c>ks</c> on a <c>sh</c> shape item).</summary>
public sealed class AnimPath
{
    public LottieBezier Static = new();
    public PathKey[]? Keys;
    public bool IsAnimated => Keys is { Length: > 1 };
}

/// <summary>A layer/group transform (Lottie <c>ks</c>). <see cref="PositionX"/>/<see cref="PositionY"/> are set
/// instead of <see cref="Position"/> when the layer split its position into independent per-axis tracks
/// (<c>ks.p.s == true</c>).</summary>
public sealed class LottieTransform
{
    public AnimVec2 Anchor = AnimVec2.Of(default);
    public AnimVec2 Position = AnimVec2.Of(default);
    public AnimScalar? PositionX;
    public AnimScalar? PositionY;
    public AnimVec2 Scale = AnimVec2.Of(new Point2(100f, 100f));
    public AnimScalar Rotation = AnimScalar.Of(0f);
    public AnimScalar Opacity = AnimScalar.Of(100f);
    /// <summary>True when any of skew/skew-axis (<c>sk</c>/<c>sa</c>) or non-zero 3D rotation axes were present and
    /// non-zero — the parser sets this so the compiler can count an <c>Approximations</c> hit once per transform
    /// instead of re-deriving it.</summary>
    public bool HasUnsupportedSkewOrAxis;

    public bool IsAnyAnimated => Anchor.IsAnimated || Position.IsAnimated || (PositionX?.IsAnimated ?? false)
        || (PositionY?.IsAnimated ?? false) || Scale.IsAnimated || Rotation.IsAnimated || Opacity.IsAnimated;
}

public enum LottieLayerType : byte { Precomp = 0, Solid = 1, Image = 2, Null = 3, Shape = 4, Text = 5, Unknown = 255 }

/// <summary>One Lottie layer (root-level or inside a precomp asset). <see cref="Parent"/> is the parent's <c>ind</c>
/// (-1 = none, root-parented). <see cref="StartTime"/> mirrors <c>st</c> (the layer's own local time origin — layers
/// DO carry non-zero <c>st</c> in the shipped assets, verified against Eula/Patch).</summary>
public sealed class LottieLayer
{
    public int Index;
    public int Parent = -1;
    public string Name = "";
    public LottieLayerType Type;
    public float InPoint;
    public float OutPoint;
    public float StartTime;
    public float Stretch = 1f;
    public bool Hidden;
    /// <summary>Lottie <c>td</c> — this layer is a TRACK MATTE SOURCE (i.e. it is USED as another layer's matte, and
    /// is itself never painted directly).</summary>
    public bool IsMatteSource;
    /// <summary>Lottie <c>tt</c> — this layer USES a track matte (any non-null value; the compiler doesn't need which
    /// mode since the whole layer is dropped either way per the plan's matte-as-decoration rule).</summary>
    public bool HasTrackMatte;
    public bool HasDropEffect;
    public LottieTransform Transform = new();
    public LottieShape[] Shapes = [];
    public string? RefId;
    public float Width;
    public float Height;
}

public enum LottieShapeType : byte { Group, Path, Rect, Ellipse, Fill, GradientFill, Stroke, GradientStroke, Trim, Transform, Merge, Repeater, Unknown }

/// <summary>A parsed gradient (Lottie <c>g</c> on <c>gf</c>/<c>gs</c>): color stops plus the 50%-offset "mid stop"
/// used for the v1 solid-fill approximation (§4 Gradient fills). <see cref="MidStop"/> already carries alpha from
/// the shape's own <c>o</c> opacity at the sample time the compiler resolves it (see <c>LottieGeometry</c>).</summary>
public sealed class LottieGradient
{
    public bool Radial;
    public Point2 Start;
    public Point2 End;
    public (float Offset, ColorF Color)[] Stops = [];
    public ColorF MidStop;
}

/// <summary>One Lottie shape-list item — Group/Path/Rect/Ellipse/Fill/GradientFill/Stroke/GradientStroke/Trim all
/// share this record (the plan's deliberate one-class-many-fields shape, mirroring the source format's own "shape
/// item" union). Unused fields for a given <see cref="Type"/> stay at their defaults.</summary>
public sealed class LottieShape
{
    public LottieShapeType Type;
    public string? Name;
    public bool Hidden;

    // Group (ty:"gr")
    public LottieShape[] Items = [];
    public LottieTransform? GroupTransform;

    // Path (ty:"sh")
    public AnimPath? Path;

    // Rect (ty:"rc") / Ellipse (ty:"el")
    public AnimVec2? Size;
    public AnimVec2? Center;
    public AnimScalar? Radius;   // Rect corner radius only
    public int Direction = 1;    // Lottie "d": 3 = reversed winding

    // Fill/Stroke/GradientFill/GradientStroke common
    public ColorF Color;
    public AnimScalar Opacity = AnimScalar.Of(100f);
    public FillRule Rule = FillRule.NonZero;

    // Stroke/GradientStroke only
    public AnimScalar? Width;
    public LineCap Cap = LineCap.Round;
    public LineJoin Join = LineJoin.Round;

    // GradientFill/GradientStroke only
    public LottieGradient? Gradient;

    // Trim (ty:"tm")
    public AnimScalar? TrimStart;
    public AnimScalar? TrimEnd;
    public AnimScalar? TrimOffset;
    public byte TrimMode;   // Lottie "m": 1 = simultaneous (per-contour), 2 = individually (whole-path)
}

/// <summary>The parsed Bodymovin document: the root layer list plus every precomp asset's own layer list, keyed by
/// asset id (Lottie <c>refId</c>). <see cref="UnsupportedFeatures"/> is a parse-time-only diagnostic counter (image
/// assets, masks, expressions) — the compiler derives its own <c>Approximations</c>/<c>DroppedLayers</c> separately
/// since those need compile-time context (e.g. a masked layer that also gets dropped for another reason).</summary>
public sealed class LottieDocument
{
    public float FrameRate;
    public float InPoint;
    public float OutPoint;
    public float Width;
    public float Height;
    public string Version = "";
    public LottieLayer[] Layers = [];
    public System.Collections.Generic.Dictionary<string, LottieLayer[]> Precomps = new(System.StringComparer.Ordinal);
    public int UnsupportedFeatures;
    /// <summary>Approximations discovered DURING PARSE (currently: an animated-color track, which the parser can
    /// only ever collapse to its first key) — folded into <see cref="LottiePlan.Approximations"/> by the compiler
    /// alongside every compile-time approximation it finds on its own.</summary>
    public int ParseApproximations;
}
