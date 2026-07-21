using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.GalleryKit;

/// <summary>
/// The interactive property surface for an <see cref="ExampleCard"/>. A sample body reads live knob values through the
/// signals this returns; the card renders <see cref="BuildPanel"/> beside the live example so a visitor can drive it.
///
/// <para><b>Registration-by-label, NOT hook-positional.</b> A <c>[Sample]</c> factory is a plain <c>static</c> method —
/// it cannot call the protected <c>Component.Use*</c> hooks. So <see cref="Knobs"/> owns all sample state as label-keyed
/// cached signals: the first call for a label creates the signal (and its panel row); every later call returns the same
/// instance. That makes registration stable across re-renders and safe to call from anywhere in an arbitrary sample
/// body (conditionals, loops) — the exact property hook cells cannot provide. The card holds one <see cref="Knobs"/> per
/// example (via <c>UseRef</c>), so the signals persist for the card's lifetime.</para>
/// </summary>
public sealed class Knobs
{
    private readonly Dictionary<string, object> _cells = new(StringComparer.Ordinal);
    private readonly List<Func<Element>> _rows = new();

    private T GetOrAdd<T>(string label, Func<T> create, Func<T, Element> row) where T : class
    {
        if (_cells.TryGetValue(label, out var existing)) return (T)existing;
        T cell = create();
        _cells[label] = cell;
        _rows.Add(() => row(cell));
        return cell;
    }

    /// <summary>A boolean knob rendered as a labeled <c>ToggleSwitch</c>.</summary>
    public Signal<bool> Toggle(string label, bool initial = false)
        => GetOrAdd(label, () => new Signal<bool>(initial),
            sig => ToggleSwitch.Create(sig, header: label));

    /// <summary>A scalar knob rendered as a labeled <c>Slider</c> with a live value readout.</summary>
    public FloatSignal Slider(string label, float initial, float min = 0f, float max = 1f, float step = 0f)
        => GetOrAdd(label, () => new FloatSignal(initial),
            sig => new BoxEl
            {
                Direction = 1, Gap = 2f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
                        Children =
                        [
                            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary },
                            new BoxEl { Grow = 1f },
                            new TextEl("") { Text = Prop.Of(() => sig.Value.ToString("0.##", CultureInfo.InvariantCulture)), Size = 12f, Bold = true, Color = Tok.TextPrimary, FontFamily = "Cascadia Code" },
                        ],
                    },
                    FluentGpu.Controls.Slider.Create(sig, options: new FluentGpu.Controls.Slider.SliderOptions { Min = min, Max = max, Step = step }),
                ],
            });

    /// <summary>A choice knob rendered as a labeled <c>ComboBox</c>; the signal holds the selected index.</summary>
    public Signal<int> Choice(string label, string[] options, int initial = 0)
        => GetOrAdd(label, () => new Signal<int>(initial),
            sig => new BoxEl
            {
                Direction = 1, Gap = 2f,
                Children = [new TextEl(label) { Size = 12f, Color = Tok.TextSecondary }, ComboBox.Create(options, sig)],
            });

    /// <summary>A text knob rendered as a labeled <c>TextBox</c>.</summary>
    public Signal<string> Text(string label, string initial = "")
        => GetOrAdd(label, () => new Signal<string>(initial),
            sig => TextBox.Create(sig, options: new TextBox.TextBoxOptions { Header = label }));

    /// <summary>A color knob rendered as a labeled compact <c>ColorPicker</c>.</summary>
    public Signal<ColorF> Color(string label, ColorF initial)
        => GetOrAdd(label, () => new Signal<ColorF>(initial),
            sig => new BoxEl
            {
                Direction = 1, Gap = 4f,
                Children = [new TextEl(label) { Size = 12f, Color = Tok.TextSecondary }, ColorPicker.Create(sig, spectrumW: 180f, spectrumH: 120f)],
            });

    /// <summary>True once any knob has been registered (the card shows the options panel only then).</summary>
    public bool HasAny => _rows.Count > 0;

    /// <summary>The auto-generated options panel — one framework-control row per registered knob, in registration order.
    /// The knob wiring appears verbatim in the displayed sample code, so the panel documents the API for free.</summary>
    public Element BuildPanel()
    {
        if (_rows.Count == 0) return new BoxEl();
        var kids = new Element[_rows.Count];
        for (int i = 0; i < _rows.Count; i++) kids[i] = _rows[i]();
        return new BoxEl { Direction = 1, Gap = 12f, Children = kids };
    }
}
