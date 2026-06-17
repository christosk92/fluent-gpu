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

/// <summary>
/// A retained stroked polyline with normalized trim controls. Points are local DIP coordinates inside the node's box;
/// trim values are path-distance fractions (0..1), so animation can reveal the stroke in draw order.
/// </summary>
public readonly record struct PolylineStrokeSpec(
    Point2 P0, Point2 P1, Point2 P2, Point2 P3, int PointCount,
    ColorF Color, float Thickness, float TrimStart = 0f, float TrimEnd = 1f, bool RoundCaps = true)
{
    public bool IsNone => Color.A <= 0f || Thickness <= 0f || PointCount < 2 || TrimEnd <= TrimStart;
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

    /// <summary>WinUI <c>MappingMode="Absolute"</c> (StartPoint 0,0 EndPoint 0,N): the stop ramp occupies this many
    /// PHYSICAL px along the axis — outside the band the edge stop's color holds — instead of stretching over the
    /// node's full extent. The ControlElevationBorder confines its blend to a 3px band
    /// (Common_themeresources_any.xaml:186). 0 (default) = relative-to-bounds. Applied as a record-time stop-offset
    /// remap, so the POD command and the gradient shader are unchanged.</summary>
    public float AxisLengthPx { get; init; }

    /// <summary>Anchor the absolute band at the END of the axis instead of the start — the engine encoding of WinUI's
    /// <c>RelativeTransform ScaleTransform ScaleY="-1"</c> mirror on elevation brushes (light ControlElevationBorder,
    /// Common_themeresources_any.xaml:382-390; AccentControlElevationBorder both themes :198-205/:397-404): the
    /// darker secondary edge sits at the BOTTOM. Only meaningful with <see cref="AxisLengthPx"/> &gt; 0.</summary>
    public bool AnchorEnd { get; init; }

    /// <summary>Radial-only: the gradient origin in node-relative 0..1 space (0,0 = top-left, 0.5,0.5 = centre). The
    /// default centres the radial (the historical behaviour). Ignored for <see cref="GradientShape.Linear"/>.</summary>
    public Point2 RadialCenter { get; init; } = new(0.5f, 0.5f);

    /// <summary>Radial-only: the gradient radius in node-relative 0..1 units per axis (a stop offset of 1.0 lands this
    /// far from <see cref="RadialCenter"/>). Default 0.5,0.5 reproduces the old centre-to-edge ramp. An ellipse in
    /// pixels when the node isn't square (matches WinUI <c>RadiusX/RadiusY</c> + <c>RelativeToBoundingBox</c>).</summary>
    public Point2 RadialRadius { get; init; } = new(0.5f, 0.5f);

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
    // NOTE: Flyout/FlyoutLight are the per-theme RAW recipes — controls should read the theme-aware
    // `Tok.AcrylicFlyout` (FluentGpu.Dsl Tokens.cs) instead of hard-binding the dark constant.
    public static AcrylicSpec Flyout => InAppDefault;
    public static AcrylicSpec FlyoutLight => new(ColorF.FromRgba(0xFC, 0xFC, 0xFC), 0.0f, 30f, 0.02f, 0.85f, ColorF.FromRgba(0xF9, 0xF9, 0xF9));
}

/// <summary>Which edges an <see cref="EdgeFadeSpec"/> feathers (a bit mask).</summary>
[System.Flags]
public enum EdgeMask : byte { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Horizontal = Left | Right, Vertical = Top | Bottom, All = 15 }

/// <summary>The alpha falloff curve across an edge-fade band. Smoothstep (default) avoids the visible linear shoulder.</summary>
public enum FadeFalloff : byte { Linear = 0, Smoothstep = 1, Cubic = 2 }

/// <summary>Edge-fade effect: feather the content alpha (Fade), blur it (Blur), or both near the edge band.</summary>
public enum EdgeFadeMode : byte { Fade = 0, Blur = 1, FadeAndBlur = 2 }

/// <summary>
/// A per-element EDGE FADE (gpu-renderer.md / controls.md §8.3): feather the element's content alpha to transparent — and
/// optionally blur it — over a band near the chosen edges, so it dissolves into whatever is behind (horizontal scrollers,
/// cards leaving the viewport, panels). Realized as a <c>PushLayer{EdgeFade}</c> offscreen layer (the subtree renders at
/// full alpha into a pooled RT, then composites once while the per-edge feather attenuates the premultiplied alpha) — one
/// RT per faded element, so fade the VIEWPORT, never each row.
///
/// <para>The feather follows the BOUNDARY, not just straight edges: where a rounded corner's two adjacent edges both
/// fade, the band hugs the corner ARC (the curve) instead of the straight edge — so a rounded card dissolves cleanly
/// around its corners. (The corner radii come from the element's <c>Corners</c>.)</para>
/// </summary>
public readonly record struct EdgeFadeSpec(
    EdgeMask Edges, float BandLeft, float BandTop, float BandRight, float BandBottom,
    FadeFalloff Falloff = FadeFalloff.Smoothstep, float Intensity = 1f,
    EdgeFadeMode Mode = EdgeFadeMode.Fade, float BlurSigma = 0f)
{
    /// <summary>One uniform band depth for every enabled edge (the common case).</summary>
    public EdgeFadeSpec(EdgeMask edges, float band, FadeFalloff falloff = FadeFalloff.Smoothstep, float intensity = 1f)
        : this(edges, band, band, band, band, falloff, intensity) { }

    /// <summary>No effect (nothing enabled / fully transparent intensity / zero band with no blur).</summary>
    public bool IsNone => Edges == EdgeMask.None || Intensity <= 0f
        || (BandLeft <= 0f && BandTop <= 0f && BandRight <= 0f && BandBottom <= 0f && Mode == EdgeFadeMode.Fade);

    /// <summary>Per-edge band depth (DIP) for an edge bit, 0 when that edge is not enabled.</summary>
    public readonly float Band(EdgeMask edge) => (Edges & edge) == 0 ? 0f
        : edge switch { EdgeMask.Left => BandLeft, EdgeMask.Top => BandTop, EdgeMask.Right => BandRight, _ => BandBottom };

    public static EdgeFadeSpec Horizontal(float band = 24f) => new(EdgeMask.Horizontal, band);
    public static EdgeFadeSpec Vertical(float band = 24f) => new(EdgeMask.Vertical, band);
    public static EdgeFadeSpec Perimeter(float band = 24f) => new(EdgeMask.All, band);
}
