using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ColorPicker: a 2-D saturation/value spectrum (two stacked linear gradients — white→hue over transparent→black,
/// since the engine has no 2-D gradient), a segmented rainbow hue rail (≤4-stop gradients ×6 to span 360°), an optional
/// alpha rail, a preview swatch, and editable Hex / R / G / B channel fields. HSV is the internal editing space (so hue is
/// stable at the grays); the selected color is written to a caller <see cref="Signal{T}"/> so a page can read it.
///
/// CUSTOMIZATION goes through <see cref="Parts"/> (the one generic door — no per-feature knobs): every named template
/// part accepts arbitrary element props; the scrub mechanics and value-driven geometry are re-asserted after any
/// modifier, so customization can restyle everything but break nothing.
/// </summary>
public sealed class ColorPicker : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The 2-D saturation/value box (WinUI ColorSpectrum). Owned: OnPointerDown/OnDrag (the SV scrub maps
    /// pointer position against the declared size), Width, Height, Role, Children (the two value-driven gradient
    /// layers + the positioned thumb).</summary>
    public const string PartSpectrum = "Spectrum";
    /// <summary>The 16×16 spectrum selection ring (WinUI SelectionEllipsePanel). Owned: OffsetX/OffsetY (the value
    /// position).</summary>
    public const string PartSpectrumThumb = "SpectrumThumb";
    /// <summary>The segmented rainbow hue rail (WinUI ThirdDimensionSlider). Owned: OnPointerDown/OnDrag (the hue
    /// scrub maps pointer X against the declared width), Width, Role, Children (the gradient strip + the positioned
    /// thumb).</summary>
    public const string PartHueRail = "HueRail";
    /// <summary>The hue rail thumb. Owned: OffsetX (the value position — the centering math assumes the stock
    /// <see cref="Style.SliderThumbW"/>).</summary>
    public const string PartHueThumb = "HueThumb";
    /// <summary>The alpha rail (WinUI AlphaSlider; mounted only when <see cref="AlphaEnabled"/>). Owned:
    /// OnPointerDown/OnDrag, Width, Role, Children (the underlay + the value-driven alpha gradient + the positioned
    /// thumb).</summary>
    public const string PartAlphaRail = "AlphaRail";
    /// <summary>The alpha rail thumb. Owned: OffsetX (the value position).</summary>
    public const string PartAlphaThumb = "AlphaThumb";
    /// <summary>The preview swatch. Owned: Fill (the live color).</summary>
    public const string PartSwatch = "Swatch";

    /// <summary>WinUI-aligned dimensions and brushes (ColorPicker.xaml / ColorPicker_themeresources.xaml).</summary>
    public sealed record Style
    {
        public float SpectrumMaxW { get; init; } = 336f;            // ColorSpectrum MaxWidth
        public float SpectrumMaxH { get; init; } = 336f;            // ColorSpectrum MaxHeight
        public float SpectrumThumbRadius { get; init; } = 8f;       // SelectionEllipsePanel 16x16
        public float RailH { get; init; } = 12f;                    // ThirdDimension/AlphaSlider Height = 12 (was 18)
        public float RailBoxH { get; init; } = 20f;                 // rail + 8px vertical padding (was 26)
        public float SliderThumbW { get; init; } = 12f;             // SliderHorizontalThumbWidth (was 6)
        public float SliderThumbCornerRadius { get; init; } = 6f;   // ColorPickerSliderCornerRadius (fixed, was railH/2)
        public float SwatchW { get; init; } = 44f;                  // preview swatch (was 64)
        public float SwatchH { get; init; } = 44f;
        public float BorderWidth { get; init; } = 2f;               // swatch StrokeThickness (was 1)
        public ColorF SwatchBorder { get; init; }                   // ColorPickerBorderBrush -> StrokeControlDefault
        public ColorF AlphaRailFill { get; init; }                  // alpha gradient underlay -> FillControlDefault
        public ColorF SliderThumbBg { get; init; }                  // ColorPickerSliderThumbBackground -> TextPrimary
        public ColorF SpectrumThumbStroke { get; init; }            // spectrum selection ring -> TextPrimary
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        SwatchBorder = Tok.StrokeControlDefault,
        AlphaRailFill = Tok.FillControlDefault,
        SliderThumbBg = Tok.TextPrimary,
        SpectrumThumbStroke = Tok.TextPrimary,
    };

    public Signal<ColorF> Color = new(ColorF.FromRgba(0x4C, 0xC2, 0xFF));
    public bool AlphaEnabled;
    public float SpectrumW = 256f;
    public float SpectrumH = 256f;
    public Style? StyleArg;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract. The Hex/R/G/B fields are composed <see cref="EditableText"/>
    /// controls — their chrome is EditableText's own (not forwarded).</summary>
    public TemplateParts? Parts;

    public static Element Create(Signal<ColorF> color, bool alphaEnabled = false, float spectrumW = 256f, float spectrumH = 256f, Style? style = null, TemplateParts? parts = null)
        => Embed.Comp(() => new ColorPicker { Color = color, AlphaEnabled = alphaEnabled, SpectrumW = spectrumW, SpectrumH = spectrumH, StyleArg = style, Parts = parts });

    static readonly ColorF White = ColorF.FromRgba(255, 255, 255);
    static readonly ColorF Black = ColorF.FromRgba(0, 0, 0);
    static readonly ColorF ClearBlack = new(0f, 0f, 0f, 0f);

    public override Element Render()
    {
        var st = StyleArg ?? DefaultStyle;
        var seed = Color.Peek().ToHsv();
        var h = UseSignal(seed.H);
        var sat = UseSignal(seed.S);
        var val = UseSignal(seed.V);
        var alpha = UseSignal(Color.Peek().A);
        var rText = UseSignal(""); var gText = UseSignal(""); var bText = UseSignal(""); var hexText = UseSignal("");

        float H = h.Value, S = sat.Value, V = val.Value, A = alpha.Value;   // subscribe → re-render on edit
        var color = ColorF.FromHsv(H, S, V, AlphaEnabled ? A : 1f);

        void Push() => Color.Value = ColorF.FromHsv(h.Peek(), sat.Peek(), val.Peek(), AlphaEnabled ? alpha.Peek() : 1f);
        void SetColor(ColorF c)
        {
            var hsv = c.ToHsv();
            h.Value = hsv.H; sat.Value = hsv.S; val.Value = hsv.V;
            if (AlphaEnabled) alpha.Value = c.A;
            Push();
        }

        // Keep the channel fields in sync with the live color (after render — writing signals during render is illegal).
        UseEffect(() =>
        {
            rText.Value = ((int)MathF.Round(color.R * 255f)).ToString(CultureInfo.InvariantCulture);
            gText.Value = ((int)MathF.Round(color.G * 255f)).ToString(CultureInfo.InvariantCulture);
            bText.Value = ((int)MathF.Round(color.B * 255f)).ToString(CultureInfo.InvariantCulture);
            hexText.Value = color.ToHex();
        }, DepKey.From(color.R, color.G, color.B, color.A));

        int Cur(float c) => (int)MathF.Round(c * 255f);
        void SetRgb(int r, int g, int b) => SetColor(ColorF.FromRgba((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Cur(A)));

        // ── SV spectrum (white→hue ⊗ transparent→black) with a draggable 2-D thumb ──
        float thumbR = st.SpectrumThumbRadius;
        void SetSV(Point2 p) { sat.Value = Math.Clamp(p.X / SpectrumW, 0f, 1f); val.Value = Math.Clamp(1f - p.Y / SpectrumH, 0f, 1f); Push(); }
        float svX = Math.Clamp(S * SpectrumW, thumbR, SpectrumW - thumbR) - thumbR;
        float svY = Math.Clamp((1f - V) * SpectrumH, thumbR, SpectrumH - thumbR) - thumbR;
        var svThumb = new BoxEl
        {
            Width = thumbR * 2f, Height = thumbR * 2f, ZStack = true,
            OffsetX = svX,
            OffsetY = svY,
            Children =
            [
                new BoxEl { Width = thumbR * 2f, Height = thumbR * 2f, Corners = Radii.Circle(thumbR * 2f), BorderWidth = 2f, BorderColor = White, Fill = ColorF.Transparent },
                new BoxEl { Width = 10f, Height = 10f, OffsetX = 4f, OffsetY = 4f, Corners = Radii.Circle(10f), BorderWidth = 1f, BorderColor = ColorF.FromRgba(0, 0, 0, 0x99), Fill = ColorF.Transparent },
            ],
        };
        var spectrum = new BoxEl
        {
            Width = SpectrumW, Height = SpectrumH, ZStack = true, Corners = Radii.OverlayAll, ClipToBounds = true,
            Role = AutomationRole.Slider, OnPointerDown = SetSV, OnDrag = SetSV,
            Children =
            [
                new BoxEl { Width = SpectrumW, Height = SpectrumH, Gradient = Ui.LinearGradient(0f, new GradientStop(0f, White), new GradientStop(1f, ColorF.FromHsv(H, 1f, 1f))) },
                new BoxEl { Width = SpectrumW, Height = SpectrumH, Gradient = Ui.LinearGradient(90f, new GradientStop(0f, ClearBlack), new GradientStop(1f, Black)) },
                Parts.Apply(PartSpectrumThumb, svThumb) with { OffsetX = svX, OffsetY = svY },
            ],
        };
        // Parts: restyle anything (corners, border, shadow…); the SV scrub mechanics + declared geometry always win.
        spectrum = Parts.Apply(PartSpectrum, spectrum) with
        {
            Width = SpectrumW, Height = SpectrumH,
            OnPointerDown = SetSV, OnDrag = SetSV, Role = AutomationRole.Slider,
            Children = spectrum.Children,
        };

        // ── Hue rail: 6 adjacent ≤4-stop gradient segments span the full 360° (MaxStops = 4) ──
        float railH = st.RailH;
        float railBoxH = st.RailBoxH;
        float railPad = (railBoxH - railH) * 0.5f;
        void SetHue(Point2 p) { h.Value = Math.Clamp(p.X / SpectrumW, 0f, 1f) * 360f; Push(); }
        var segs = new Element[6];
        for (int i = 0; i < 6; i++)
            segs[i] = new BoxEl
            {
                Grow = 1f, Height = railH,
                Gradient = Ui.LinearGradient(0f, new GradientStop(0f, ColorF.FromHsv(i * 60f, 1f, 1f)), new GradientStop(1f, ColorF.FromHsv((i + 1) * 60f, 1f, 1f))),
            };
        var hueRail = new BoxEl
        {
            Width = SpectrumW, Height = railBoxH, ZStack = true,
            Role = AutomationRole.Slider, OnPointerDown = SetHue, OnDrag = SetHue,
            Children =
            [
                new BoxEl { Width = SpectrumW, Height = railH, OffsetY = railPad, Direction = 0, Corners = Radii.Circle(railH), ClipToBounds = true, Children = segs },
                HandleAt(Math.Clamp(H / 360f * SpectrumW, st.SliderThumbW * 0.5f, SpectrumW - st.SliderThumbW * 0.5f), railBoxH, st, Parts, PartHueThumb),
            ],
        };
        hueRail = Parts.Apply(PartHueRail, hueRail) with
        {
            Width = SpectrumW,
            OnPointerDown = SetHue, OnDrag = SetHue, Role = AutomationRole.Slider,
            Children = hueRail.Children,
        };

        var rows = new List<Element> { spectrum, hueRail };

        // ── optional alpha rail (current color, transparent → opaque) ──
        if (AlphaEnabled)
        {
            void SetA(Point2 p) { alpha.Value = Math.Clamp(p.X / SpectrumW, 0f, 1f); Push(); }
            var opaque = ColorF.FromHsv(H, S, V, 1f);
            var alphaRail = new BoxEl
            {
                Width = SpectrumW, Height = railBoxH, ZStack = true,
                Role = AutomationRole.Slider, OnPointerDown = SetA, OnDrag = SetA,
                Children =
                [
                    new BoxEl { Width = SpectrumW, Height = railH, OffsetY = railPad, Corners = Radii.Circle(railH), ClipToBounds = true, Fill = st.AlphaRailFill },
                    new BoxEl { Width = SpectrumW, Height = railH, OffsetY = railPad, Corners = Radii.Circle(railH), ClipToBounds = true, Gradient = Ui.LinearGradient(0f, new GradientStop(0f, opaque with { A = 0f }), new GradientStop(1f, opaque)) },
                    HandleAt(Math.Clamp(A * SpectrumW, st.SliderThumbW * 0.5f, SpectrumW - st.SliderThumbW * 0.5f), railBoxH, st, Parts, PartAlphaThumb),
                ],
            };
            rows.Add(Parts.Apply(PartAlphaRail, alphaRail) with
            {
                Width = SpectrumW,
                OnPointerDown = SetA, OnDrag = SetA, Role = AutomationRole.Slider,
                Children = alphaRail.Children,
            });
        }

        // ── preview swatch + channel fields ──
        var swatch = new BoxEl { Width = st.SwatchW, Height = st.SwatchH, Corners = Radii.ControlAll, BorderWidth = st.BorderWidth, BorderColor = st.SwatchBorder, Fill = color };
        swatch = Parts.Apply(PartSwatch, swatch) with { Fill = color };   // the preview always shows the live color
        Element Field(string label, Signal<string> text, Func<string, string> sanitize, Action<string> commit, float w) => new BoxEl
        {
            Direction = 1, Gap = 3f,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary },
                Embed.Comp(() => new EditableText { Text = text, Width = w, Height = 32f, FontSize = 14f, Sanitize = sanitize, OnCommit = commit }),
            ],
        };
        static string DigitsMax3(string s)
        {
            Span<char> buf = stackalloc char[3];
            int n = 0;
            for (int i = 0; i < s.Length && n < buf.Length; i++)
                if (char.IsDigit(s[i])) buf[n++] = s[i];
            return new string(buf[..n]);
        }
        static string HexMax6(string s)
        {
            Span<char> buf = stackalloc char[6];
            int n = 0;
            for (int i = 0; i < s.Length && n < buf.Length; i++)
                if (Uri.IsHexDigit(s[i])) buf[n++] = s[i];
            return new string(buf[..n]);
        }
        int ParseInt(string s) => int.TryParse(s, out int n) ? n : 0;

        var channels = new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.End,
            Children =
            [
                swatch,
                Field("Hex", hexText, HexMax6, s => { if (ColorF.TryParseHex(s, out var c)) SetColor(c); }, 88f),
                Field("R", rText, DigitsMax3, s => SetRgb(ParseInt(s), Cur(color.G), Cur(color.B)), 64f),
                Field("G", gText, DigitsMax3, s => SetRgb(Cur(color.R), ParseInt(s), Cur(color.B)), 64f),
                Field("B", bText, DigitsMax3, s => SetRgb(Cur(color.R), Cur(color.G), ParseInt(s)), 64f),
            ],
        };
        rows.Add(channels);

        return new BoxEl { Direction = 1, Gap = 12f, Children = rows.ToArray() };
    }

    // The hue/alpha rail thumb: 12px wide, 6px corner (ColorPickerSlider), TextPrimary background + 1px elevation border.
    // Routed through its part name; OffsetX (the value position) is re-asserted — the centering assumes the stock width.
    static Element HandleAt(float x, float railBoxH, Style st, TemplateParts? parts, string part)
    {
        var thumb = new BoxEl
        {
            Width = st.SliderThumbW,
            Height = railBoxH,
            OffsetX = x - st.SliderThumbW * 0.5f,
            Corners = CornerRadius4.All(st.SliderThumbCornerRadius),
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = st.SliderThumbBg,
        };
        return parts.Apply(part, thumb) with { OffsetX = thumb.OffsetX };
    }
}
