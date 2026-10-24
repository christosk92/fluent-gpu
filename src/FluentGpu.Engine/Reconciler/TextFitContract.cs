using System;
using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Reconciler;

/// <summary>
/// A DEBUG-only tripwire for a <c>TextEl.MinSize</c> auto-fit floor that can never act (report-only, like
/// <see cref="BindContract"/>; erased outside <c>DEBUG</c>/<c>FLUENTGPU_DIAG</c>). The layout shrinks a run toward its
/// floor only while there is something to fit: a NoWrap run fits ONE line into its column, a wrapping run fits
/// <c>MaxLines</c>. So a floor is dead on a wrapping run with no line cap (<c>MaxLines == 0</c>: it grows, never
/// overflows) and a floor ABOVE the size has nothing to shrink to; a floor equal to the size is a harmless no-op.
/// Checked where the reconciler writes the text style, and only when the floor's inputs change, so a re-render repeats
/// nothing.
/// </summary>
public static class TextFitContract
{
    /// <summary>Count of dead floors reported since the last <see cref="Reset"/> (gate accessor).</summary>
    public static int Violations { get; private set; }

    /// <summary>The most recent report (gate accessor).</summary>
    public static string? LastViolation { get; private set; }

    /// <summary>Reset the accumulators (between gate scenarios).</summary>
    public static void Reset() { Violations = 0; LastViolation = null; }

    /// <summary>Report <paramref name="next"/>'s floor when it cannot act and its fit inputs differ from
    /// <paramref name="prev"/>'s (a fresh node, or a re-render that changed them).</summary>
    [Conditional("DEBUG"), Conditional("FLUENTGPU_DIAG")]
    public static void Check(in TextStyle prev, in TextStyle next, string text)
    {
        if (!(next.MinSizeDip > 0f)) return;
        if (prev.MinSizeDip == next.MinSizeDip && prev.SizeDip == next.SizeDip
            && prev.Wrap == next.Wrap && prev.MaxLines == next.MaxLines) return;
        string? why = next.MinSizeDip > next.SizeDip
            ? $"MinSize {next.MinSizeDip:0.##} is above Size {next.SizeDip:0.##}: there is nothing to shrink to"
            : next.Wrap != TextWrap.NoWrap && next.MaxLines <= 0
                ? $"a wrapping run with MaxLines = 0 grows instead of overflowing, so MinSize {next.MinSizeDip:0.##} never acts (cap MaxLines, or use NoWrap)"
                : null;
        if (why is null) return;
        Violations++;
        string shown = text.Length > 40 ? string.Concat(text.AsSpan(0, 40), "...") : text;
        string msg = $"[textfit] TextEl \"{shown}\": {why}.";
        LastViolation = msg;
        if (Diag.Sink is { } sink) sink(msg);
        else Console.Error.WriteLine(msg);
    }
}
