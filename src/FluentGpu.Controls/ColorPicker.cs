using System.Globalization;
using System.Linq;
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
/// </summary>
public sealed class ColorPicker : Component
{
    public Signal<ColorF> Color = new(ColorF.FromRgba(0x4C, 0xC2, 0xFF));
    public bool AlphaEnabled;
    public float SpectrumW = 260f;
    public float SpectrumH = 168f;

    public static Element Create(Signal<ColorF> color, bool alphaEnabled = false, float spectrumW = 260f, float spectrumH = 168f)
        => Embed.Comp(() => new ColorPicker { Color = color, AlphaEnabled = alphaEnabled, SpectrumW = spectrumW, SpectrumH = spectrumH });

    static readonly ColorF White = ColorF.FromRgba(255, 255, 255);
    static readonly ColorF Black = ColorF.FromRgba(0, 0, 0);
    static readonly ColorF ClearBlack = new(0f, 0f, 0f, 0f);

    public override Element Render()
    {
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
        }, color);

        int Cur(float c) => (int)MathF.Round(c * 255f);
        void SetRgb(int r, int g, int b) => SetColor(ColorF.FromRgba((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Cur(A)));

        // ── SV spectrum (white→hue ⊗ transparent→black) with a draggable 2-D thumb ──
        float thumbR = 9f;
        void SetSV(Point2 p) { sat.Value = Math.Clamp(p.X / SpectrumW, 0f, 1f); val.Value = Math.Clamp(1f - p.Y / SpectrumH, 0f, 1f); Push(); }
        var spectrum = new BoxEl
        {
            Width = SpectrumW, Height = SpectrumH, ZStack = true, Corners = Radii.OverlayAll, ClipToBounds = true,
            Role = AutomationRole.Slider, OnPointerDown = SetSV, OnDrag = SetSV,
            Children =
            [
                new BoxEl { Width = SpectrumW, Height = SpectrumH, Gradient = Ui.LinearGradient(0f, new GradientStop(0f, White), new GradientStop(1f, ColorF.FromHsv(H, 1f, 1f))) },
                new BoxEl { Width = SpectrumW, Height = SpectrumH, Gradient = Ui.LinearGradient(90f, new GradientStop(0f, ClearBlack), new GradientStop(1f, Black)) },
                new BoxEl
                {
                    Width = thumbR * 2f, Height = thumbR * 2f, Corners = Radii.Circle(thumbR * 2f),
                    BorderWidth = 2f, BorderColor = White, Fill = ColorF.Transparent,
                    OffsetX = Math.Clamp(S * SpectrumW, thumbR, SpectrumW - thumbR) - thumbR,
                    OffsetY = Math.Clamp((1f - V) * SpectrumH, thumbR, SpectrumH - thumbR) - thumbR,
                },
            ],
        };

        // ── Hue rail: 6 adjacent ≤4-stop gradient segments span the full 360° (MaxStops = 4) ──
        float railH = 16f;
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
            Width = SpectrumW, Height = railH, ZStack = true, Corners = Radii.Circle(railH), ClipToBounds = true,
            Role = AutomationRole.Slider, OnPointerDown = SetHue, OnDrag = SetHue,
            Children =
            [
                new BoxEl { Width = SpectrumW, Height = railH, Direction = 0, Children = segs },
                HandleAt(Math.Clamp(H / 360f * SpectrumW, 4f, SpectrumW - 4f), railH),
            ],
        };

        var rows = new List<Element> { spectrum, hueRail };

        // ── optional alpha rail (current color, transparent → opaque) ──
        if (AlphaEnabled)
        {
            void SetA(Point2 p) { alpha.Value = Math.Clamp(p.X / SpectrumW, 0f, 1f); Push(); }
            var opaque = ColorF.FromHsv(H, S, V, 1f);
            rows.Add(new BoxEl
            {
                Width = SpectrumW, Height = railH, ZStack = true, Corners = Radii.Circle(railH), ClipToBounds = true,
                Role = AutomationRole.Slider, OnPointerDown = SetA, OnDrag = SetA,
                Children =
                [
                    new BoxEl { Width = SpectrumW, Height = railH, Gradient = Ui.LinearGradient(0f, new GradientStop(0f, opaque with { A = 0f }), new GradientStop(1f, opaque)) },
                    HandleAt(Math.Clamp(A * SpectrumW, 4f, SpectrumW - 4f), railH),
                ],
            });
        }

        // ── preview swatch + channel fields ──
        var swatch = new BoxEl { Width = 48f, Height = 48f, Corners = Radii.ControlAll, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, Fill = color };
        Element Field(string label, Signal<string> text, Func<string, string> sanitize, Action<string> commit, float w) => new BoxEl
        {
            Direction = 1, Gap = 2f,
            Children =
            [
                new TextEl(label) { Size = 11f, Color = Tok.TextSecondary },
                Embed.Comp(() => new EditableText { Text = text, Width = w, Height = 28f, FontSize = 13f, Sanitize = sanitize, OnCommit = commit }),
            ],
        };
        static string DigitsMax3(string s) => new string(s.Where(char.IsDigit).Take(3).ToArray());
        static string HexMax6(string s) => new string(s.Where(Uri.IsHexDigit).Take(6).ToArray());
        int ParseInt(string s) => int.TryParse(s, out int n) ? n : 0;

        var channels = new BoxEl
        {
            Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center,
            Children =
            [
                swatch,
                Field("Hex", hexText, HexMax6, s => { if (ColorF.TryParseHex(s, out var c)) SetColor(c); }, 84f),
                Field("R", rText, DigitsMax3, s => SetRgb(ParseInt(s), Cur(color.G), Cur(color.B)), 48f),
                Field("G", gText, DigitsMax3, s => SetRgb(Cur(color.R), ParseInt(s), Cur(color.B)), 48f),
                Field("B", bText, DigitsMax3, s => SetRgb(Cur(color.R), Cur(color.G), ParseInt(s)), 48f),
            ],
        };
        rows.Add(channels);

        return new BoxEl { Direction = 1, Gap = 12f, Children = rows.ToArray() };
    }

    // A small handle for the hue/alpha rails: a thin vertical bar at the given x.
    static Element HandleAt(float x, float railH) => new BoxEl
    {
        Direction = 0,
        Children =
        [
            new BoxEl { Width = MathF.Max(0f, x - 1.5f) },
            new BoxEl { Width = 3f, Height = railH, Corners = CornerRadius4.All(1.5f), BorderWidth = 1f, BorderColor = ColorF.FromRgba(0, 0, 0, 0x66), Fill = ColorF.FromRgba(255, 255, 255) },
        ],
    };
}
