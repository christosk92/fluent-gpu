using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Text.DirectWrite;

/// <summary>
/// The DirectWrite measurement backend (<see cref="IFontSystem"/>) — a thin wrapper over <see cref="TextLayoutEngine"/>.
/// Measurement runs the full itemize → shape → wrap/trim → position pipeline (kerning, ligatures, complex script, UAX #14
/// line breaking, CJK/emoji font fallback), so the measured box matches what the renderer — which uses the SAME engine —
/// actually draws. measure ≡ render: this parity is what keeps shaped glyphs from overlapping (the divergence that read
/// as bold when the renderer was shaped but measurement was per-char). Touched on the UI thread during phase-6 layout.
/// </summary>
public sealed class DirectWriteFontSystem : IFontSystem, IDisposable
{
    private readonly StringTable _strings;
    private readonly TextLayoutEngine _engine = new();

    public DirectWriteFontSystem(StringTable strings) => _strings = strings;

    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
    {
        string family = _strings.Resolve(style.FontFamily);
        string s = _strings.Resolve(text);
        _engine.Layout(s.AsSpan(), family, style.Bold, style.SizeDip, maxWidth, (int)style.Wrap, (int)style.Trim, style.MaxLines);
        return new TextMetrics(new Size2(_engine.Width, _engine.Height), _engine.Baseline);
    }

    public void Dispose() => _engine.Dispose();
}
