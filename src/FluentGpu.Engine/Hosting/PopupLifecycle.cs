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
/// (<see cref="NoteTurn"/>) and, under a render thread, the swapchain build (<see cref="RenderCreateMs"/>: the create rides the
/// popup mailbox, F207, so the UI never parks the loop for it). <see cref="Line"/> runs where the swapchain is released — on
/// the render thread when it retires the slot in mailbox order (after the last pass), or on the UI thread for a host with no
/// render thread — which is what makes reading the per-pass fields safe without a park.</para>
/// </summary>
internal sealed class PopupLifecycle
{
    // ── UI thread ──
    internal long LeaseStartQpc;
    internal double ParkMs, WindowMs, SwapchainMs, LeaseMs;
    internal long ShownQpc;
    internal double ShowMs;

    // ── render thread (read where the slot is retired, after the last pass) ──
    internal double RenderCreateMs;   // the mailbox create's CreateSwapchain on the render thread; 0 when built inline on the UI thread
    internal long FirstPresentQpc;
    internal double FirstTurnMs;
    internal int Turns;
    internal double TurnMsSum, TurnMsMax;

    // ── the popup pool (AppHost) ──
    internal bool Reused;      // UI: the lease took a pooled window + parked swapchain (render: false again when that swapchain could not serve)
    internal bool Parked;      // where the swapchain is released: the close parked it for the next popup instead
    internal double RetireMs;  // the close's park / release call on its thread (render thread under a render loop)
    internal PopupOrigin Origin;   // UI: where the lease's window + swapchain came from (built, the prewarm's pair, a closed popup's)

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
            $"[overlay.popup] material={material} lease={LeaseMs:F1}ms (park={ParkMs:F1} window={WindowMs:F1} swapchain={SwapchainMs:F1} renderCreate={RenderCreateMs:F1})"
            + $" firstPresent=+{Since(LeaseStartQpc, FirstPresentQpc):F1}ms (pass={FirstTurnMs:F2}ms)"
            + $" shown=+{Since(LeaseStartQpc, ShownQpc):F1}ms (show={ShowMs:F1}ms)"
            + $" open={Since(LeaseStartQpc, closeQpc):F0}ms passes={Turns} pass={(Turns == 0 ? 0 : TurnMsSum / Turns):F2}/{TurnMsMax:F2}ms"
            + $" reused={(Reused ? 1 : 0)} parked={(Parked ? 1 : 0)} retire={RetireMs:F2}ms origin={Origin}");
    }
}
