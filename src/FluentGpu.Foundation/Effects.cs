namespace FluentGpu.Foundation;

/// <summary>
/// A drop shadow / elevation descriptor (CSS box-shadow shape). Lives in Foundation so Scene/Render/Reconciler can
/// read it without referencing Dsl. Carried by an elevated node; the recorder emits a <c>DrawShadow</c> beneath the fill.
/// </summary>
public readonly record struct ShadowSpec(float Blur, float OffsetY, float OffsetX, ColorF Color, float Spread = 0f)
{
    public bool IsNone => Color.A <= 0f || (Blur <= 0f && Spread <= 0f && OffsetX == 0f && OffsetY == 0f);
}

/// <summary>One gradient stop: a normalized position (0..1 along the gradient axis) and its color.</summary>
public readonly record struct GradientStop(float Offset, ColorF Color);

public enum GradientShape : byte { Linear = 0, Radial = 1 }

/// <summary>
/// A gradient fill. <see cref="AngleDeg"/> is the linear axis (0 = left→right, 90 = top→bottom). Up to 4 stops are
/// carried into the POD draw command; extras are ignored. When set on a node it supersedes the solid fill at record time.
/// </summary>
public readonly record struct GradientSpec(GradientShape Shape, float AngleDeg, GradientStop[] Stops)
{
    public const int MaxStops = 4;

    /// <summary>A solid "gradient" (one stop) — so a flat border and a gradient border are the SAME knob (BorderBrush).</summary>
    public static GradientSpec Solid(ColorF color) => new(GradientShape.Linear, 0f, [new GradientStop(0f, color)]);

    /// <summary>A vertical 2-stop linear gradient (top → bottom).</summary>
    public static GradientSpec Vertical(ColorF top, ColorF bottom) => new(GradientShape.Linear, 90f, [new GradientStop(0f, top), new GradientStop(1f, bottom)]);
}

/// <summary>
/// A per-node acrylic (frosted glass): the engine samples the canvas behind the node, blurs it (<see cref="BlurSigma"/>),
/// then tints + adds noise + a luminosity wash. Realized by the <c>PushLayer</c>/<c>PopLayer</c> backdrop subsystem.
/// </summary>
public readonly record struct AcrylicSpec(ColorF Tint, float TintOpacity, float BlurSigma, float NoiseOpacity, float LuminosityOpacity)
{
    // The real WinUI AcrylicBrush "Default" recipe parameters (dark): backdrop gaussian blur (radius ≈ 30 →
    // sigma below), TintColor, TintOpacity, TintLuminosityOpacity (the luminosity-blend layer), and the noise overlay.
    public static AcrylicSpec InAppDefault => new(ColorF.FromRgba(0x2C, 0x2C, 0x2C), 0.8f, 30f, 0.02f, 0.10f);
    public static AcrylicSpec InAppBase => new(ColorF.FromRgba(0x20, 0x20, 0x20), 0.85f, 30f, 0.02f, 0.10f);
}
