using System.Diagnostics;
using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Theming;

namespace FluentGpu.Hosting;

/// <summary>What a theme transaction loads BEFORE it freezes, with the UI still live (E5 step 1). The fonts are hard: the
/// freeze waits for them, up to <see cref="MaxMs"/>. The images are soft: their decode starts now and nothing waits for them
/// here (the settle holds pinned images up to its soft wait).</summary>
public sealed record PrewarmSet
{
    /// <summary>Nothing to load.</summary>
    public static PrewarmSet None { get; } = new();

    /// <summary>The faces the new frame shapes with (<see cref="ThemeDefinition.Fonts"/>), resolved on a worker. Null: none.</summary>
    public FontSet? Fonts { get; init; }

    /// <summary>Images the new frame shows, decoded ahead at prefetch priority (never pinned, never waited for).</summary>
    public IReadOnlyList<ImagePrewarm>? Images { get; init; }

    /// <summary>The most milliseconds (host timer clock) the fonts may hold the live UI before the freeze goes ahead anyway; the
    /// settle's own font gate (<see cref="SettleSpec.FontWaitMs"/>) then covers what is left. Default 1500.</summary>
    public int MaxMs { get; init; } = 1500;
}

/// <summary>One image to decode ahead of a theme transaction, at its target decode size.</summary>
public readonly record struct ImagePrewarm(string Source, int Width, int Height);

/// <summary>
/// One theme transaction (E5, <see cref="AppHost.RunThemeTransaction"/>): install <see cref="Next"/> at <see cref="Kind"/> and
/// run <see cref="Apply"/> under a frozen frame, so a change of structure or of a non-colour table reaches the screen as ONE
/// settled frame and a dissolve, never as a cascade of hybrid frames.
/// </summary>
/// <param name="Next">The definition the new frame is built with. <c>Tok.Use(Next, Kind)</c> runs in the apply flush, under the freeze.
/// Its <see cref="ThemeDefinition.Materials"/> picks the window material (null for the kind keeps the current one).</param>
/// <param name="Kind">The theme kind of the new frame.</param>
/// <param name="Apply">The application's change, run once in the apply flush right after the definition is installed: activate the
/// new theme's state, swap the root (a component of another type under the root, which remounts everything below it) or change
/// an option in place. It must not wait on anything.</param>
public sealed record ThemeTransactionRequest(ThemeDefinition Next, ThemeKind Kind, Action Apply)
{
    /// <summary>What to load before the freeze (fonts hard, images soft).</summary>
    public PrewarmSet Prewarm { get; init; } = PrewarmSet.None;

    /// <summary>Undo <see cref="Apply"/> when it, or a template it mounted, throws: the engine puts the previous definition, kind
    /// and window material back and calls this under the same freeze; the frame thaws with a cut. Null: nothing app-side to undo.</summary>
    public Action? Rollback { get; init; }

    /// <summary>Step 2: close the transient popups OTHER than the one hosting the control that started the transaction, run right
    /// before the freeze. The application knows which one that is; null closes nothing.</summary>
    public Action? CloseTransients { get; init; }

    /// <summary>The thaw's cross-dissolve (owner Q10: 250 ms).</summary>
    public DissolveSpec Dissolve { get; init; } = new(250f);

    /// <summary>The settle's bounds. Null: <see cref="SettleSpec.Default"/> with the fonts gate on <see cref="Next"/>'s fonts.</summary>
    public SettleSpec? Settle { get; init; }

    /// <summary>Step 7: the change is on screen (called at thaw start, or when a cut ended the freeze early).</summary>
    public Action? OnCommitted { get; init; }

    /// <summary>The change failed and was rolled back (the frame never showed it). The application logs and tells the user.</summary>
    public Action<Exception>? OnFailed { get; init; }

    /// <summary>A short name for the <c>[theme-tx]</c> log line (the theme id, an option id).</summary>
    public string? Label { get; init; }
}

/// <summary>How a theme transaction ended.</summary>
public enum ThemeTransactionOutcome : byte
{
    /// <summary>Applied under the freeze, settled, thawed with the dissolve.</summary>
    Committed = 0,
    /// <summary>Applied, but a resize, a DPI change or the watchdog cut the freeze: the change showed without the dissolve.</summary>
    Cut = 1,
    /// <summary>The apply or a template it mounted threw: everything was put back under the freeze and the frame thawed with a cut.</summary>
    RolledBack = 2,
    /// <summary>A later request replaced this one before it started (only the latest waiting request runs).</summary>
    Superseded = 3,
    /// <summary>Applied at once without a freeze: the window shows nothing (hidden, minimized, parked) or the host is gone.</summary>
    Immediate = 4,
    /// <summary>The host was disposed before the change was applied: nothing changed.</summary>
    Canceled = 5,
}

/// <summary>The result of <see cref="AppHost.RunThemeTransaction"/>.</summary>
/// <param name="Outcome">How it ended.</param>
public sealed record ThemeTransactionResult(ThemeTransactionOutcome Outcome)
{
    /// <summary>The settle (null when none ran: an immediate apply, a cut before the capture, a supersede).</summary>
    public SettleResult? Settle { get; init; }
    /// <summary>Wall milliseconds from the request to the freeze (the prewarm with live UI).</summary>
    public double PrewarmMs { get; init; }
    /// <summary>Wall milliseconds from the request to thaw start (the transaction latency the plan records; 0 when it never thawed).</summary>
    public double LatencyMs { get; init; }
    /// <summary>What the apply or a template threw (<see cref="ThemeTransactionOutcome.RolledBack"/>).</summary>
    public Exception? Error { get; init; }
}

/// <summary>
/// E5 - the theme transaction: a theme switch, or a change to a definition's non-colour tables, applied as ONE settled frame
/// under a frozen snapshot. Light/dark and colour-only changes never come here (they keep the live cross-fade path).
/// <list type="number">
/// <item><b>Prewarm</b>, with live UI: the fonts resolve on a worker (the freeze waits up to <see cref="PrewarmSet.MaxMs"/>), the
/// images start decoding. <see cref="ThemeTransactionBusy"/> turns true after 100 ms so the originating control can show a busy ring.</item>
/// <item><b>Close transients</b> (<see cref="ThemeTransactionRequest.CloseTransients"/>) other than the one hosting the originating
/// control. Gestures are not cancelled: the gesture that issued the request has ended.</item>
/// <item><b>Freeze</b> this window and every detached pop-out (<see cref="Freeze"/>; nothing of the page is captured, owner Q26).</item>
/// <item><b>Apply</b>, in the first frame after the capture, under the freeze's snap scope: <c>Tok.Use(Next, Kind)</c>, the window
/// material's ground, then <see cref="ThemeTransactionRequest.Apply"/> (a root swap or an in-place change). The same frame
/// flushes, re-renders every component in place for the new definition and is the first settle pass.</item>
/// <item><b>Settle</b> (<see cref="Settle"/>) until the frame is quiet, bounded (6 passes / 250 ms after fonts).</item>
/// <item><b>Thaw</b> with the dissolve; the DWM change is ordered with the dissolve by the material pair (opaque → translucent and
/// translucent → translucent at thaw start, translucent → opaque at thaw end, nothing when equal). Pop-outs thaw in the same call.</item>
/// <item><b>Committed</b>: <see cref="ThemeTransactionRequest.OnCommitted"/>, one <c>[theme-tx]</c> log line.</item>
/// </list>
/// A throw in the apply or in a template it mounted (a frame of the apply or the settle) rolls back under the same freeze: the
/// previous definition, kind and material, then <see cref="ThemeTransactionRequest.Rollback"/>, one flush, and a thaw with a cut,
/// so nothing of the failed frame is ever shown. A resize, DPI change or the watchdog cuts the freeze as it always does: the change
/// stays applied and shows at once (<see cref="ThemeTransactionOutcome.Cut"/>). While one transaction runs, a new request waits;
/// only the latest waiting request is kept (the others complete <see cref="ThemeTransactionOutcome.Superseded"/>). A window that
/// shows nothing applies at once. UI thread only; the task completes asynchronously.
/// </summary>
public sealed partial class AppHost
{
    private enum TxPhase : byte { Prewarm, Capturing, Settling, RollingBack, RolledBackFlush }

    private sealed class TxRun
    {
        public required ThemeTransactionRequest Request;
        public readonly TaskCompletionSource<ThemeTransactionResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TxPhase Phase;
        public long StartQpc;
        public double StartMs;           // host timer clock (the busy flag and the prewarm cap)
        public double PrewarmMs;
        public Task? Fonts;
        public FreezeToken? Token;
        public Task<SettleResult>? Settle;
        public SettleResult? SettleResult;
        public ThemeDefinition? PrevDefinition;
        public ThemeKind PrevKind;
        public WindowMaterial PrevMaterial;
        public WindowMaterial NextMaterial;
        public bool DwmPending;          // a DWM write is owed (material or dark flag changed) and has not run
        public Exception? Fault;
        public int RollbackFaults;       // throws absorbed while the restored tree flushes (the failed subtree's queued work)
    }

    private TxRun? _tx;
    private Action<Exception>? _txFaultSink;
    private TxRun? _txPending;
    private bool _txOwnsDwm;             // the apply's Tok.Use must not write DWM on the live path (the transaction orders it)
    private bool _txDisposed;
    private readonly Signal<bool> _txBusy = new(false);

    /// <summary>True while a transaction's prewarm has held the live UI for 100 ms or more: the originating control shows its
    /// busy ring. False again at the freeze.</summary>
    public IReadSignal<bool> ThemeTransactionBusy => _txBusy;

    /// <summary>True while a theme transaction runs or waits on this host (from the request to its thaw start).</summary>
    public bool IsThemeTransactionRunning => _tx is not null || _txPending is not null;

    /// <summary>Run a theme transaction (see the type summary). Main window, UI thread. Never throws for the application's own
    /// failures: a throwing apply ends as <see cref="ThemeTransactionOutcome.RolledBack"/>.</summary>
    public Task<ThemeTransactionResult> RunThemeTransaction(ThemeTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Next);
        ArgumentNullException.ThrowIfNull(request.Apply);
        Threading.ThreadGuard.AssertUi();
        if (_isDetachedChild) throw new InvalidOperationException("A theme transaction runs on the main window; its pop-outs freeze with it.");

        var run = new TxRun { Request = request, StartQpc = Stopwatch.GetTimestamp(), StartMs = _timers.NowMs };
        if (_tx is not null)
        {
            // Coalesce: one waits, the latest wins.
            if (_txPending is { } replaced) Finish(replaced, new ThemeTransactionResult(ThemeTransactionOutcome.Superseded));
            _txPending = run;
            return run.Done.Task;
        }
        if (!CanFreezeForTransaction())
        {
            ApplyImmediate(run);
            return run.Done.Task;
        }
        StartPrewarm(run);
        return run.Done.Task;
    }

    /// <summary>A window that is on screen can be frozen; a hidden, minimized or parked one (or a disposed host) has nothing to hold.</summary>
    private bool CanFreezeForTransaction() => !_txDisposed && _window.IsVisible && !_revealPending && !IsParked && !_window.IsClosed;

    private void StartPrewarm(TxRun run)
    {
        _tx = run;
        run.Phase = TxPhase.Prewarm;
        run.StartMs = _timers.NowMs;
        FrameTrace.Theme("tx.begin", run.Request.Label);
        var prewarm = run.Request.Prewarm;
        if (prewarm.Fonts is { } fonts && FontRegistry.Shared.Source is not null && !Fonts.IsReady(fonts))
            run.Fonts = Fonts.Preload(fonts);
        if (prewarm.Images is { } images)
            for (int i = 0; i < images.Count; i++) _images.Prefetch(images[i].Source, images[i].Width, images[i].Height);
        WakeFrame();
    }

    /// <summary>Paint, right after the freeze's frame start and before the settle's pass start: advance the transaction. The apply
    /// runs HERE, so the frame that follows flushes it and is the first settle pass.</summary>
    private void TxFrameStart()
    {
        if (_tx is null)
        {
            if (_txPending is not { } next) return;
            _txPending = null;
            if (CanFreezeForTransaction()) StartPrewarm(next);
            else { ApplyImmediate(next); return; }
        }
        var run = _tx!;
        switch (run.Phase)
        {
            case TxPhase.Prewarm:
            {
                double waited = _timers.NowMs - run.StartMs;
                bool fontsDone = run.Fonts is null || run.Fonts.IsCompleted;
                if (!fontsDone && waited < run.Request.Prewarm.MaxMs)
                {
                    if (waited >= BusyAfterMs) _txBusy.Value = true;
                    return;
                }
                if (FreezeState != FreezePhase.None) return;   // a freeze (or the previous dissolve) still runs: start after it
                if (!CanFreezeForTransaction()) { _tx = null; ApplyImmediate(run); return; }
                run.PrewarmMs = ElapsedWallMs(run.StartQpc);
                _txBusy.Value = false;
                run.Request.CloseTransients?.Invoke();
                run.Token = Freeze();
                run.Phase = TxPhase.Capturing;   // this frame is the capture frame
                FrameTrace.Theme("tx.freeze", run.Request.Label, (float)run.PrewarmMs);
                return;
            }
            case TxPhase.Capturing:
            {
                var token = run.Token!;
                if (token.WasCut)
                {
                    // A resize or the watchdog ended the freeze before the outgoing frame was captured: apply without one.
                    _tx = null;
                    ApplyUnfrozen(run, ThemeTransactionOutcome.Cut);
                    return;
                }
                if (!token.IsCaptured) return;
                ApplyUnderFreeze(run);
                return;
            }
            case TxPhase.RollingBack:
                RestorePrevious(run);
                SafeRollback(run);
                run.Phase = TxPhase.RolledBackFlush;   // this frame flushes the restored tree under the freeze
                return;
        }
    }

    private const double BusyAfterMs = 100.0;
    private const int MaxRollbackFaults = 4;

    /// <summary>The apply flush: install the definition and the material's ground, run the application's change, start the settle.</summary>
    private void ApplyUnderFreeze(TxRun run)
    {
        var r = run.Request;
        CapturePrevious(run);
        // A template of the new theme that throws is contained to its component (it renders empty) and recorded: the frame is held,
        // so nothing of it is shown, and the pass ends in a rollback (TxAfterPass).
        _reconciler.RenderFaultSink = _txFaultSink ??= ex => { if (_tx is { } t) t.Fault ??= ex; };
        try
        {
            InstallNext(run);
            r.Apply();
        }
        catch (Exception ex)
        {
            // The application's own apply threw: put everything back now; this frame flushes the restored tree.
            run.Fault = ex;
            RestorePrevious(run);
            SafeRollback(run);
            run.Phase = TxPhase.RolledBackFlush;
            return;
        }
        run.Phase = TxPhase.Settling;
        FrameTrace.Theme("tx.apply", r.Label);
        var spec = r.Settle ?? SettleSpec.Default with { FontsReady = FontsReadyFor(r.Next.Fonts) };
        run.Settle = Settle(spec);
    }

    private static Func<bool> FontsReadyFor(FontSet fonts) => () => FontRegistry.Shared.Source is null || Fonts.IsReady(fonts);

    private void CapturePrevious(TxRun run)
    {
        run.PrevDefinition = Tok.Definition;
        run.PrevKind = Tok.Theme;
        run.PrevMaterial = WindowMaterial;
    }

    /// <summary>Install the next definition and kind, and the next material's ground. The DWM write is owed, not made.</summary>
    private void InstallNext(TxRun run)
    {
        var r = run.Request;
        run.NextMaterial = r.Next.Materials.For(r.Kind) ?? WindowMaterial;
        run.DwmPending = run.NextMaterial != run.PrevMaterial || r.Kind != run.PrevKind;
        _txOwnsDwm = true;
        Tok.Use(r.Next, r.Kind);
        if (run.NextMaterial != run.PrevMaterial) ApplyMaterialGround(run.NextMaterial);
    }

    private void RestorePrevious(TxRun run)
    {
        if (run.PrevDefinition is null) return;
        Tok.Use(run.PrevDefinition, run.PrevKind);
        if (WindowMaterial != run.PrevMaterial) ApplyMaterialGround(run.PrevMaterial);
        run.DwmPending = false;   // nothing was written: DWM still shows the previous material and flag
    }

    private static void SafeRollback(TxRun run)
    {
        try { run.Request.Rollback?.Invoke(); }
        catch (Exception ex) { Diag.Line("[theme-tx] rollback threw: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>Paint, right after the settle judged its pass: thaw when the settle ended, or finish a rollback.</summary>
    private void TxAfterPass()
    {
        if (_tx is not { } run) return;
        if (run.Phase == TxPhase.RolledBackFlush)
        {
            if (run.Token is { IsActive: true } t) Thaw(t, DissolveSpec.Cut, null);
            _tx = null;
            var error = run.Fault ?? new InvalidOperationException("theme transaction rolled back");
            InvokeFailed(run, error);
            Finish(run, new ThemeTransactionResult(ThemeTransactionOutcome.RolledBack) { PrewarmMs = run.PrewarmMs, Error = error });
            return;
        }
        if (run.Phase == TxPhase.Settling && run.Fault is not null)
        {
            // A template of the new theme threw in this pass: end the settle and roll back at the next frame, under the same freeze.
            CompleteSettle(timedOut: false, cut: true, images: 0);
            run.Phase = TxPhase.RollingBack;
            WakeFrame();
            return;
        }
        if (run.Phase != TxPhase.Settling || run.Settle is not { IsCompleted: true } settle) return;
        run.SettleResult = settle.Result;
        var token = run.Token!;
        _tx = null;
        if (!token.IsActive || run.SettleResult.Cut)
        {
            // A resize or the watchdog cut the freeze under the settle: the change is already applied and shows now.
            if (run.DwmPending) WriteMaterialNow(run);
            Commit(run, ThemeTransactionOutcome.Cut, latencyMs: 0);
            return;
        }
        FrameTrace.Theme("tx.thaw", run.Request.Label, run.Request.Dissolve.Ms, (float)ElapsedWallMs(run.StartQpc));
        Thaw(token, run.Request.Dissolve, DwmPlanFor(run));
        Commit(run, ThemeTransactionOutcome.Committed, latencyMs: ElapsedWallMs(run.StartQpc));
    }

    /// <summary>The DWM rows of E2: same material and flag → no change; translucent → opaque at thaw end (the new frame covers the
    /// window); otherwise at thaw start (the snapshot still covers or tints it).</summary>
    private DwmPlan? DwmPlanFor(TxRun run)
    {
        if (!run.DwmPending) return null;
        WindowMaterial m = run.NextMaterial;
        bool dark = run.Request.Kind == ThemeKind.Dark;
        Action write = () => { run.DwmPending = false; WriteMaterialOrdered(m, dark); };
        bool toOpaque = run.PrevMaterial != WindowMaterial.None && m == WindowMaterial.None;
        return toOpaque ? new DwmPlan(AtThawEnd: write) : new DwmPlan(AtThawStart: write);
    }

    private void WriteMaterialNow(TxRun run)
    {
        run.DwmPending = false;
        bool dark = run.Request.Kind == ThemeKind.Dark;
        if (WindowMaterialSink is { } sink) sink(run.NextMaterial, dark);
        else OnApplyThemeMaterial?.Invoke(dark);
    }

    /// <summary>Apply without a freeze: the window shows nothing, so there is no frame to hold.</summary>
    private void ApplyImmediate(TxRun run) => ApplyUnfrozen(run, ThemeTransactionOutcome.Immediate);

    private void ApplyUnfrozen(TxRun run, ThemeTransactionOutcome outcome)
    {
        _txBusy.Value = false;
        CapturePrevious(run);
        try
        {
            InstallNext(run);
            run.Request.Apply();
        }
        catch (Exception ex)
        {
            run.Fault = ex;
            RestorePrevious(run);
            SafeRollback(run);
            _txOwnsDwm = false;
            InvokeFailed(run, ex);
            Finish(run, new ThemeTransactionResult(ThemeTransactionOutcome.RolledBack) { Error = ex });
            return;
        }
        if (run.DwmPending) WriteMaterialNow(run);
        Commit(run, outcome, latencyMs: 0);
    }

    private void Commit(TxRun run, ThemeTransactionOutcome outcome, double latencyMs)
    {
        try { run.Request.OnCommitted?.Invoke(); }
        catch (Exception ex) { Diag.Line("[theme-tx] OnCommitted threw: " + ex.GetType().Name + ": " + ex.Message); }
        Finish(run, new ThemeTransactionResult(outcome) { Settle = run.SettleResult, PrewarmMs = run.PrewarmMs, LatencyMs = latencyMs });
    }

    private static void InvokeFailed(TxRun run, Exception error)
    {
        try { run.Request.OnFailed?.Invoke(error); }
        catch (Exception ex) { Diag.Line("[theme-tx] OnFailed threw: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private void Finish(TxRun run, ThemeTransactionResult result)
    {
        if (ReferenceEquals(_tx, run)) _tx = null;
        if (_tx is null) { _txOwnsDwm = false; _txBusy.Value = false; _reconciler.RenderFaultSink = null; }
        var s = result.Settle;
        string line = string.Create(CultureInfo.InvariantCulture,
            $"[theme-tx] {run.Request.Label ?? run.Request.Next.Id} outcome={result.Outcome} prewarmMs={result.PrewarmMs:F1} latencyMs={result.LatencyMs:F1} settlePasses={s?.Passes ?? 0} settleTimedOut={(s?.TimedOut == true ? 1 : 0)}");
        Diag.Line(result.Error is { } e ? line + " error=" + e.GetType().Name + ": " + e.Message : line);
        run.Done.TrySetResult(result);
        if (_tx is null && _txPending is not null) WakeFrame();   // the waiting request starts at the next frame
    }

    /// <summary>Paint threw. Inside a transaction's apply or settle frames the throw is the new frame's (a template of the new
    /// theme): record it, end the settle and roll back under the same freeze at the next frame. Returns false when the throw is
    /// not the transaction's to absorb (it propagates as before); a throw while the rollback itself flushes ends the transaction first.</summary>
    private bool TxAbsorbFault(Exception ex)
    {
        if (_tx is not { } run) return false;
        if (run.Phase == TxPhase.Settling && run.Fault is null)
        {
            run.Fault = ex;
            CompleteSettle(timedOut: false, cut: true, images: 0);
            run.Phase = TxPhase.RollingBack;
            WakeFrame();
            return true;
        }
        if (run.Phase == TxPhase.RolledBackFlush && ++run.RollbackFaults <= MaxRollbackFaults)
        {
            // Work the failed subtree had queued (a re-render the definition change scheduled for the half-mounted component) can
            // throw once more before the restored parent's re-render unmounts it: its siblings stay queued, so the next frame
            // carries on. Bounded.
            WakeFrame();
            return true;
        }
        if (run.Phase == TxPhase.RolledBackFlush)
        {
            // The restored tree keeps throwing: there is nothing left to roll back to. End the freeze and let the throw propagate.
            if (run.Token is { IsActive: true } t) Thaw(t, DissolveSpec.Cut, null);
            _tx = null;
            InvokeFailed(run, run.Fault ?? ex);
            Finish(run, new ThemeTransactionResult(ThemeTransactionOutcome.RolledBack) { PrewarmMs = run.PrewarmMs, Error = run.Fault ?? ex });
        }
        return false;
    }

    /// <summary>The frame loop owes the transaction a frame: the prewarm polls its fonts (every turn on a headless host, whose
    /// clock moves only on painted frames), the capture landed and the apply is due, a rollback is due, or a waiting request can start.</summary>
    private bool TxNeedsFrame()
    {
        if (_tx is not { } run) return _txPending is not null;
        if (run.Phase is TxPhase.RollingBack or TxPhase.RolledBackFlush) return true;
        // Captured (or cut before the capture): the apply flush is owed now. Settling and dissolving drive their own frames.
        if (run.Phase == TxPhase.Capturing) return run.Token is { } token && (token.IsCaptured || token.WasCut);
        if (run.Phase != TxPhase.Prewarm) return false;
        if (_isHeadless || run.Fonts is null || run.Fonts.IsCompleted) return true;
        double waited = _timers.NowMs - run.StartMs;
        return waited >= run.Request.Prewarm.MaxMs || (!_txBusy.Peek() && waited >= BusyAfterMs);
    }

    /// <summary>Shorten an idle wait while the prewarm polls its fonts (every 16 ms).</summary>
    private int ClampWaitToTransaction(int w)
    {
        if (_tx is not { Phase: TxPhase.Prewarm }) return w;
        return w < 0 ? 16 : Math.Min(w, 16);
    }

    /// <summary>Dispose: a running or waiting transaction completes (applied ones as a cut, the rest canceled), so no awaiter is stranded.</summary>
    private void CancelThemeTransactionOnDispose()
    {
        _txDisposed = true;
        if (_txPending is { } waiting) { _txPending = null; Finish(waiting, new ThemeTransactionResult(ThemeTransactionOutcome.Canceled)); }
        if (_tx is not { } run) return;
        _tx = null;
        bool applied = run.Phase is TxPhase.Settling;
        Finish(run, new ThemeTransactionResult(applied ? ThemeTransactionOutcome.Cut : ThemeTransactionOutcome.Canceled)
        {
            PrewarmMs = run.PrewarmMs, Settle = run.SettleResult,
        });
    }

    private static double ElapsedWallMs(long startQpc) => (Stopwatch.GetTimestamp() - startQpc) * 1000.0 / Stopwatch.Frequency;
}
