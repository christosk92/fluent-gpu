using System.Diagnostics;
using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Hosting;

/// <summary>Options of the main-window reveal gate (<see cref="AppHost.BeginRevealGate"/>, EP8).</summary>
public sealed record RevealGateOptions
{
    /// <summary>True once the first frame can be the FINAL one: fonts the first screen needs are loaded (the application passes
    /// its font registry's readiness), so the window never opens on fallback glyphs. Evaluated on the UI thread at the start of
    /// every frame while the gate is pending; null means ready at once. The frame that is presented for the reveal is
    /// the first one built after this returned true.</summary>
    public Func<bool>? FirstFrameReady { get; init; }

    /// <summary>The watchdog (ms): when the first ready frame has not presented by then, the window is shown anyway (a stuck font
    /// load, a backend that never reports a presented frame) - bounded, logged, and visible rather than invisible.</summary>
    public int TimeoutMs { get; init; } = DetachedRevealGate.TimeoutMs;
}

/// <summary>How a main-window reveal ended (<see cref="AppHost.MainRevealed"/>).</summary>
/// <param name="ReadyMs">Milliseconds from arming until <see cref="RevealGateOptions.FirstFrameReady"/> was first seen true
/// (-1: never).</param>
/// <param name="ShownMs">Milliseconds from arming until the window was shown.</param>
/// <param name="TimedOut">True when the watchdog showed it rather than a presented ready frame.</param>
public readonly record struct RevealTiming(double ReadyMs, double ShownMs, bool TimedOut);

public sealed partial class AppHost
{
    private Func<bool>? _revealFirstFrameReady;
    private bool _revealReadySeen;             // FirstFrameReady was true at the start of a frame
    private ulong _revealReadyPublishSeq;      // PublishSequence when it was first seen true: the reveal needs a LATER publication presented
    private long _revealArmQpc;
    private double _revealReadyMs = -1;

    /// <summary>Raised once on the UI thread, right after the main window was shown by its reveal gate.</summary>
    public event Action<RevealTiming>? MainRevealed;

    /// <summary>True while the main window is hidden and waiting for its reveal (<see cref="BeginRevealGate"/>).</summary>
    public bool IsRevealPending => _revealPending && !_isDetachedChild;

    /// <summary>
    /// Main window, UI thread (EP8): keep the (hidden) window hidden until its first frame has presented with the application's
    /// fonts ready, then show it - so the first thing the user sees is the final frame, never white or black first, never fallback
    /// glyphs. Call while the window is hidden: at cold start instead of showing it, and again before every later show-from-hidden
    /// (tray restore, wake) so the frame is repainted into the hidden window and revealed on its present.
    /// <list type="bullet">
    /// <item>The host is exempt from the hidden-window park while the gate is pending, so the frame the reveal waits for can paint
    /// into the hidden window (the pop-out reveal's rule, now shared); the swapchain's reveal exemption is re-armed
    /// (<see cref="FluentGpu.Rhi.ISwapchain.ArmRevealExemption"/>).</item>
    /// <item>The reveal needs <see cref="RevealGateOptions.FirstFrameReady"/> true at the start of a frame, then that frame's
    /// publication presented with real content (a stood-down present does not count), or the watchdog
    /// (<see cref="RevealGateOptions.TimeoutMs"/>).</item>
    /// <item>Just before <c>Show</c> the host calls <see cref="OnApplyThemeMaterial"/> with the current dark flag, so the OS
    /// material and dark attribute (and anything that changed while hidden) are in place before the window is visible.</item>
    /// </list>
    /// Needs a composited swapchain (a non-composited target stands a hidden window's presents down); the Windows entry point
    /// only arms it for one. A pop-out uses <see cref="BeginDetachedReveal"/>.</summary>
    public void BeginRevealGate(RevealGateOptions? options = null)
    {
        if (_isDetachedChild) throw new InvalidOperationException("BeginRevealGate is for the main window; a pop-out reveals through its own detached-window path.");
        var o = options ?? new RevealGateOptions();
        _revealPending = true;
        _revealFirstFrameReady = o.FirstFrameReady;
        _revealReadySeen = false;
        _revealReadyMs = -1;
        _revealArmQpc = Stopwatch.GetTimestamp();
        _revealDeadlineQpc = DetachedRevealGate.DeadlineQpc(_revealArmQpc, o.TimeoutMs);
        _swapchain.ArmRevealExemption();
        RequestFullRepaintOnce();   // ask for the frame the reveal waits for
    }

    /// <summary>Frame start (UI thread): note the first frame at which the application's fonts are ready. The reveal must present a
    /// publication made after this point, so it is asked for here (a ready edge with nothing dirty would otherwise publish
    /// nothing new).</summary>
    private void NoteRevealReadiness()
    {
        if (_revealReadySeen) return;
        if (_revealFirstFrameReady is { } ready && !ready()) return;
        _revealReadySeen = true;
        _revealReadyPublishSeq = PublishSequence;
        _revealReadyMs = QpcMs(Stopwatch.GetTimestamp() - _revealArmQpc);
        RequestFullRepaintOnce();
    }

    /// <summary>Frame end (UI thread): show the hidden main window when a ready frame has presented, or when the watchdog says it
    /// is not coming. Shown exactly once per arming (the pending flag clears first). Returns true on the tick that revealed it.</summary>
    internal bool TryRevealMain()
    {
        if (!_revealPending || _isDetachedChild) return false;
        long now = Stopwatch.GetTimestamp();
        bool presented = _revealReadySeen && _swapchain.HasPresentedContent && RenderPresentSeq > _revealReadyPublishSeq;
        if (!DetachedRevealGate.Due(presented, now, _revealDeadlineQpc)) return false;
        _revealPending = false;
        _revealFirstFrameReady = null;
        // Material and dark attribute FIRST: the OS cannot cross-fade its backdrop, and a window shown before the right one is
        // applied flashes the wrong ground for a frame. Idempotent when nothing changed since startup.
        OnApplyThemeMaterial?.Invoke(Tok.Theme == ThemeKind.Dark);
        _window.Show();
        // Only the first composited present is exempt from the hidden-window stand-down: a motion present between it and this Show
        // was dropped, so present once more now that the window is visible (see TryRevealDetached).
        if (_swapchain.IsOccluded) RequestFullRepaintOnce();
        var timing = new RevealTiming(_revealReadyMs, QpcMs(now - _revealArmQpc), TimedOut: !presented);
        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[reveal] main shown readyMs={timing.ReadyMs:F1} shownMs={timing.ShownMs:F1} timedOut={(timing.TimedOut ? 1 : 0)}"));
        MainRevealed?.Invoke(timing);
        return true;
    }

    /// <summary>E21, UI thread, before the frame's animation tick: will the frame being built NOT be shown? True while the main
    /// window is hidden behind its reveal gate and its fonts are not ready (the first frame built after they are is the one the
    /// reveal shows), while a freeze holds presents (its capture frame still presents), and while a restore hold keeps the pre-hide
    /// frame on the glass. Entrances seeded under such frames start their clock on the first frame that is shown.</summary>
    private bool EntrancesUnseen()
        => (_revealPending && !_isDetachedChild && !_revealReadySeen)
           || (_fzPhase == FreezePhase.Frozen && !_fzCapturePending)
           || _restoreHoldArmed;

    private static double QpcMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
