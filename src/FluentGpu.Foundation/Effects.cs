namespace FluentGpu.Foundation;

/// <summary>
/// A drop shadow / elevation descriptor (CSS box-shadow shape). Lives in Foundation so Scene/Render/Reconciler can
/// read it without referencing Dsl. Carried by an elevated node; the recorder emits a <c>DrawShadow</c> beneath the fill.
/// </summary>
public readonly record struct ShadowSpec(float Blur, float OffsetY, float OffsetX, ColorF Color, float Spread = 0f)
{
    public bool IsNone => Color.A <= 0f || (Blur <= 0f && Spread <= 0f && OffsetX == 0f && OffsetY == 0f);
}

/// <summary>
/// A circular-arc stroke (WinUI ProgressRing — a trimmed, round-capped ring). The arc is centred in the node's box with
/// radius (min(W,H)-Thickness)/2, a <see cref="Thickness"/>-wide stroke, swept from <see cref="StartDeg"/> for
/// <see cref="SweepDeg"/> degrees (0° = 12 o'clock, clockwise). The recorder emits a <c>DrawArc</c>; the GPU draws the SDF
/// arc with round caps. A full ring is SweepDeg = 360.
/// </summary>
public readonly record struct ArcSpec(ColorF Color, float Thickness, float StartDeg, float SweepDeg, bool RoundCaps = true)
{
    public bool IsNone => Color.A <= 0f || Thickness <= 0f || SweepDeg <= 0f;
}

/// <summary>One gradient stop: a normalized position (0..1 along the gradient axis) and its color.</summary>
public readonly record struct GradientStop(float Offset, ColorF Color);

public enum GradientShape : byte { Linear = 0, Radial = 1 }

/// <summary>
/// A gradient fill. <see cref="AngleDeg"/> is the linear axis (0 = left-to-right, 90 = top-to-bottom). Up to 4 stops are
/// carried into the POD draw command; extras are ignored. When set on a node it supersedes the solid fill at record time.
/// </summary>
public readonly record struct GradientSpec(GradientShape Shape, float AngleDeg, GradientStop[] Stops)
{
    public const int MaxStops = 4;

    /// <summary>A solid "gradient" (one stop) - so a flat border and a gradient border are the SAME knob (BorderBrush).</summary>
    public static GradientSpec Solid(ColorF color) => new(GradientShape.Linear, 0f, [new GradientStop(0f, color)]);

    /// <summary>A vertical 2-stop linear gradient (top to bottom).</summary>
    public static GradientSpec Vertical(ColorF top, ColorF bottom) => new(GradientShape.Linear, 90f, [new GradientStop(0f, top), new GradientStop(1f, bottom)]);
}

/// <summary>
/// A per-node acrylic (frosted glass): the engine samples the canvas behind the node, resolves transparent backdrop
/// through <see cref="Fallback"/>, blurs it (<see cref="BlurSigma"/>), then applies WinUI's luminosity/tint recipe.
/// Realized by the <c>PushLayer</c>/<c>PopLayer</c> backdrop subsystem.
/// </summary>
public readonly record struct AcrylicSpec(ColorF Tint, float TintOpacity, float BlurSigma, float NoiseOpacity, float LuminosityOpacity, ColorF Fallback)
{
    public AcrylicSpec(ColorF tint, float tintOpacity, float blurSigma, float noiseOpacity, float luminosityOpacity)
        : this(tint, tintOpacity, blurSigma, noiseOpacity, luminosityOpacity, tint) { }

    // WinUI 3 AcrylicBrush_themeresources.xaml source values. The renderer follows AcrylicBrush.cpp:
    // backdrop SourceOver opaque FallbackColor -> GaussianBlur(30) -> luminosity blend -> tint/color blend -> noise.
    public static AcrylicSpec InAppDefault => new(ColorF.FromRgba(0x2C, 0x2C, 0x2C), 0.15f, 30f, 0.02f, 0.96f, ColorF.FromRgba(0x2C, 0x2C, 0x2C));
    public static AcrylicSpec InAppBase => new(ColorF.FromRgba(0x20, 0x20, 0x20), 0.50f, 30f, 0.02f, 0.96f, ColorF.FromRgba(0x1C, 0x1C, 0x1C));

    // MenuFlyoutPresenter and CommandBarFlyoutPresenter use DesktopAcrylicTransparentBrush plus
    // AcrylicBackgroundFillColorDefaultBackdrop. In-canvas reproduction uses that same acrylic recipe.
    public static AcrylicSpec Flyout => InAppDefault;
    public static AcrylicSpec FlyoutLight => new(ColorF.FromRgba(0xFC, 0xFC, 0xFC), 0.0f, 30f, 0.02f, 0.85f, ColorF.FromRgba(0xF9, 0xF9, 0xF9));
}
