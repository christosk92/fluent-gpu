using System.Diagnostics;
using System.Globalization;
using FluentGpu.Pal;

namespace FluentGpu.Hosting;

/// <summary>
/// What one windowed popup (an OS popup window leased for an overlay that may leave the root bounds) cost the host,
/// written as ONE always-on <c>[overlay.popup]</c> line when the popup closes. A menu that opens slowly, or a window that
/// stutters while a menu is up, is then attributable from the log alone: how long the lease held the UI thread and
/// parked the render thread (and which step: the park, the window, the swapchain + composition chrome), how long until
/// the popup's first frame reached its surface and until its window was shown, and what each popup pass cost the render
/// thread while it was up.
/// <para><b>Threading.</b> The UI thread writes the lease and reveal fields; the render thread writes the per-pass fields
/// (<see cref="NoteTurn"/>). <see cref="Line"/> runs on the UI thread at close, with the render loop PARKED
/// (<c>AppHost.ClosePopupWindow</c> quiesces it first), which is what makes reading the render-thread fields safe.</para>
/// </summary>
internal sealed class PopupLifecycle
{
    // ── UI thread ──
    internal long LeaseStartQpc;
    internal double ParkMs, WindowMs, SwapchainMs, LeaseMs;
    internal long ShownQpc;
    internal double ShowMs;

    // ── render thread (read at close, loop parked) ──
    internal long FirstPresentQpc;
    internal double FirstTurnMs;
    internal int Turns;
    internal double TurnMsSum, TurnMsMax;

    /// <summary>One popup pass on the render thread: record + submit + present of the popup swapchain.</summary>
    internal void NoteTurn(long startQpc, long endQpc, bool presentedContent)
    {
        double ms = (endQpc - startQpc) * 1000.0 / Stopwatch.Frequency;
        Turns++;
        TurnMsSum += ms;
        if (ms > TurnMsMax) TurnMsMax = ms;
        if (FirstPresentQpc == 0 && presentedContent)
        {
            FirstPresentQpc = endQpc;
            FirstTurnMs = ms;
        }
    }

    internal string Line(PopupWindowMaterial material, long closeQpc)
    {
        static double Since(long from, long to) => from == 0 || to == 0 ? double.NaN : (to - from) * 1000.0 / Stopwatch.Frequency;
        var ci = CultureInfo.InvariantCulture;
        return string.Create(ci,
            $"[overlay.popup] material={material} lease={LeaseMs:F1}ms (park={ParkMs:F1} window={WindowMs:F1} swapchain={SwapchainMs:F1})"
            + $" firstPresent=+{Since(LeaseStartQpc, FirstPresentQpc):F1}ms (pass={FirstTurnMs:F2}ms)"
            + $" shown=+{Since(LeaseStartQpc, ShownQpc):F1}ms (show={ShowMs:F1}ms)"
            + $" open={Since(LeaseStartQpc, closeQpc):F0}ms passes={Turns} pass={(Turns == 0 ? 0 : TurnMsSum / Turns):F2}/{TurnMsMax:F2}ms");
    }
}
