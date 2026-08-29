using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Per-hop peak magnitudes (0..1) of a track and how many bars to draw them as. The peaks are RESAMPLED
/// across the whole set, so a 64-bar strip of a 12,000-hop track still shows the whole track, not its first seconds.</summary>
public sealed record WaveformModel(ReadOnlyMemory<float> Peaks, int Bars = 64);

/// <summary>
/// A track's shape as mirrored bars about a centre line — what makes a waveform read as a waveform rather than as a
/// bar chart (lifted from the Wavee track drawer). Silence keeps a <see cref="Style.MinBar"/> baseline so the strip
/// never has holes. An optional <c>progress</c> signal paints the played portion in <see cref="Style.PlayedInk"/>
/// through a bound clip width (the <see cref="ProgressBar"/> idiom): the playhead advances with no re-render.
/// Stateless: one canonical <see cref="Create"/>.
/// </summary>
public static class Waveform
{
    public sealed record Style
    {
        public float Height { get; init; } = 24f;
        public float BarWidth { get; init; } = 2f;
        public float Gap { get; init; } = 1f;
        public float MinBar { get; init; } = 2f;
        public ColorF Ink { get; init; }
        public ColorF PlayedInk { get; init; }
        /// <summary>Mirror the bars about the vertical centre (true) or anchor them to the baseline (false).</summary>
        public bool Mirrored { get; init; } = true;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Ink = Tok.TextTertiary,
        PlayedInk = Tok.AccentDefault,
    };

    /// <summary>Total width of a strip: <c>Bars · BarWidth + (Bars − 1) · Gap</c>.</summary>
    public static float WidthOf(WaveformModel model, Style? style = null)
    {
        var st = style ?? DefaultStyle;
        int n = Math.Max(0, Math.Min(model.Bars, model.Peaks.Length));
        return n == 0 ? 0f : n * st.BarWidth + (n - 1) * st.Gap;
    }

    /// <summary>The ONE canonical factory. <paramref name="progress"/> = a caller-owned 0..1 signal (the played
    /// fraction), or null for a plain shape.</summary>
    public static Element Create(WaveformModel model, FloatSignal? progress = null, Style? style = null, string? key = null)
    {
        var st = style ?? DefaultStyle;
        var peaks = model.Peaks.Span;
        int n = Math.Max(0, Math.Min(model.Bars, peaks.Length));
        if (n == 0) return new BoxEl { Key = key };
        float width = n * st.BarWidth + (n - 1) * st.Gap;

        var rest = Strip(peaks, n, st, st.Ink);
        if (progress is null) return rest with { Key = key, Role = AutomationRole.None };

        var played = Strip(peaks, n, st, st.PlayedInk);
        // The played overlay is the same strip clipped to progress·width: a bound Width (compositor/relayout, no
        // component re-render) — bars left of the playhead swap ink, the rest stay resting.
        var clip = new BoxEl
        {
            Height = st.Height, ClipToBounds = true, HitTestVisible = false,
            Width = (Prop<float>)(Func<float>)(() =>
            {
                float p = progress.Value;
                return (p < 0f ? 0f : p > 1f ? 1f : p) * width;
            }),
            Children = [played with { Width = width, Shrink = 0f }],
        };
        return new BoxEl
        {
            Key = key, ZStack = true, Width = width, Height = st.Height, Shrink = 0f,
            Role = AutomationRole.None, HitTestPassThrough = true,
            Children = [rest, clip],
        };
    }

    static BoxEl Strip(ReadOnlySpan<float> peaks, int n, Style st, ColorF ink)
    {
        var bars = new Element[n];
        for (int i = 0; i < n; i++)
        {
            // Sample across the whole set so a shorter strip still shows the WHOLE track.
            float p = peaks[(int)((long)i * peaks.Length / n)];
            if (float.IsNaN(p) || p < 0f) p = 0f; else if (p > 1f) p = 1f;
            bars[i] = new BoxEl
            {
                Width = st.BarWidth, Shrink = 0f,
                Height = MathF.Max(st.MinBar, p * st.Height),
                Corners = CornerRadius4.All(st.BarWidth / 2f),
                Fill = ink,
                AlignSelf = st.Mirrored ? FlexAlign.Center : FlexAlign.End,
                HitTestVisible = false,
            };
        }
        return new BoxEl
        {
            Direction = 0, Gap = st.Gap, Height = st.Height, Shrink = 0f,
            AlignItems = st.Mirrored ? FlexAlign.Center : FlexAlign.End, HitTestPassThrough = true,
            Children = bars,
        };
    }
}
