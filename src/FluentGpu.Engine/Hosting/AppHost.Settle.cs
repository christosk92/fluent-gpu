using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Hosting;

/// <summary>
/// The bounds of one settle (E17c; <see cref="AppHost.Settle"/>). <paramref name="MaxPasses"/> caps the passes that still changed
/// something (a pass that only waited for fonts or images does not count); <paramref name="MaxMs"/> caps the time after the fonts
/// are ready. Whichever comes first ends the settle as a timeout.
/// </summary>
/// <param name="MaxPasses">The most passes (held frames) that may end with work still pending: a measured value that changed, a
/// scheduled render or effect, dirty layout, a pending tier, a virtual list still warming. Default 6.</param>
/// <param name="MaxMs">The most milliseconds (host timer clock) the settle runs once the fonts are ready. Default 250.</param>
public sealed record SettleSpec(int MaxPasses = 6, int MaxMs = 250)
{
    /// <summary>The default bounds: 6 passes, 250 ms, a 150 ms image soft wait, fonts taken as ready.</summary>
    public static SettleSpec Default { get; } = new();

    /// <summary>How long (ms from the settle's start) a PINNED image that is still decoding holds the settle. Past it the settle
    /// completes and the image shows its final-size placeholder, revealing after the thaw exactly as in production. Default 150.</summary>
    public int ImageSoftWaitMs { get; init; } = 150;

    /// <summary>True when the fonts the new frame needs are loaded (the application passes its registry's readiness, e.g.
    /// <c>() =&gt; Fonts.IsReady(definition.Fonts)</c>). Null means ready. Polled while the settle waits for it; the
    /// <see cref="SettleSpec.MaxMs"/> clock starts when it first returns true.</summary>
    public Func<bool>? FontsReady { get; init; }

    /// <summary>The most milliseconds the settle waits for <see cref="FontsReady"/> before giving up (a timeout naming
    /// <c>fonts</c>). Default 1000 — below the freeze watchdog (2 s), so a stuck font load ends as a logged settle timeout and
    /// a dissolve, not as a watchdog cut.</summary>
    public int FontWaitMs { get; init; } = 1000;
}

/// <summary>How a settle ended (<see cref="AppHost.Settle"/>).</summary>
/// <param name="Passes">The held frames the settle ran (every pass, including those that only waited).</param>
/// <param name="TimedOut">True when a cap (<see cref="SettleSpec.MaxPasses"/>, <see cref="SettleSpec.MaxMs"/> or
/// <see cref="SettleSpec.FontWaitMs"/>) ended it before the frame was quiet. The transaction dissolves anyway: the snap scope
/// guarantees nothing is wrong-themed; at worst one region shows a non-final size for a frame after the thaw.</param>
/// <param name="UnsettledKeys">What was still moving when it ended: the keys of the measurements that changed in the last pass
/// (an unkeyed hook is named by its call site), plus <c>render-pending</c>, <c>layout-dirty</c>, <c>tier-pending</c>,
/// <c>virtuals-warming</c>, <c>pop-out</c>, <c>fonts</c>, and one <c>image:&lt;source&gt;</c> per pinned image still decoding
/// (also listed on a successful settle whose soft wait expired). Empty when everything settled.</param>
public sealed record SettleResult(int Passes, bool TimedOut, IReadOnlyList<string> UnsettledKeys)
{
    /// <summary>The passes that ended with work still pending (the ones <see cref="SettleSpec.MaxPasses"/> counts).</summary>
    public int WorkPasses { get; init; }
    /// <summary>Milliseconds (host timer clock) from <see cref="AppHost.Settle"/> to the end.</summary>
    public double ElapsedMs { get; init; }
    /// <summary>Milliseconds spent waiting for the fonts (0 when they were ready at once).</summary>
    public double FontWaitMs { get; init; }
    /// <summary>Pinned images still decoding when the settle ended (past their soft wait, or at a timeout).</summary>
    public int ImagesUndecoded { get; init; }
    /// <summary>True when the snap scope closed under the settle (a resize or the watchdog cut the freeze, or the owner closed
    /// it): the settle stopped where it was; there is nothing to thaw.</summary>
    public bool Cut { get; init; }
}

/// <summary>
/// E17c — the settle loop. A theme transaction (E5) applies its change under a freeze (<see cref="Freeze"/>, which owns the snap
/// scope) and then calls <see cref="Settle"/>: the host keeps running HELD frames (recorded and submitted, never presented) until
/// one pass changes nothing, so the frame the thaw dissolves to is final.
/// <list type="bullet">
/// <item><b>A pass</b> is one ordinary frame: flush the scheduled renders and effects, lay out, run the layout effects (where the
/// measured hooks write), run the passive effects. Nothing is special-cased: the settle only watches.</item>
/// <item><b>Quiet</b> means, after a pass: no measured value changed during it (<see cref="MeasureMemory"/>'s change serial), no
/// scheduled render or effect, layout clean, no pending tier crossing, no virtual list still warming, and the same for every
/// detached pop-out in the snap. Tickers and intervals cannot keep it busy: the snap scope stands the frame clock and the
/// timers still (E17b). UI posts (async data arriving) are not waited for: a page whose data is not there yet settles on its
/// production skeleton.</item>
/// <item><b>Then</b> it waits, without frames, for the fonts (<see cref="SettleSpec.FontsReady"/>) and for the pinned images
/// still decoding, up to their soft wait (<see cref="SettleSpec.ImageSoftWaitMs"/>); an image landing wakes the next pass.</item>
/// <item><b>Bounded</b>: <see cref="SettleSpec.MaxPasses"/> passes that still changed something, or <see cref="SettleSpec.MaxMs"/>
/// after the fonts, whichever first. A timeout completes the task normally with <see cref="SettleResult.TimedOut"/> and logs
/// <c>[settle-timeout]</c> with the unsettled keys. Every settle logs one <c>[settle]</c> line (passes, time, images).</item>
/// <item><b>Keys</b>: keyed measurement (<c>UseMeasuredWidth(key)</c>, <c>UseMeasuredBounds(key)</c>, keyed <c>Responsive.Of</c>)
/// starts a remounted region from its remembered size, so the first pass is already at the final size and the settle is
/// typically one or two passes. Unkeyed hooks still settle, one pass later each.</item>
/// </list>
/// UI thread. One settle per host at a time; it must run inside a snap scope (<see cref="IsSnapping"/>). The task completes on a
/// later turn (continuations run asynchronously). Disposal or a closed snap scope completes it as <see cref="SettleResult.Cut"/>.
/// </summary>
public sealed partial class AppHost
{
    private TaskCompletionSource<SettleResult>? _stTcs;
    private SettleSpec _stSpec = SettleSpec.Default;
    private double _stStartMs, _stFontsReadyMs;
    private int _stPasses, _stWorkPasses;
    private long _stSerialAtPassStart;
    private bool _stQuiet;          // the last pass was quiet: only fonts/images are awaited (no frames needed until something is due)
    private bool _stFontsReady;

    /// <summary>Raised on the UI thread when a settle ends (with the same result its task completes with).</summary>
    public event Action<SettleResult>? Settled;

    /// <summary>True while a <see cref="Settle"/> is running on this host.</summary>
    public bool IsSettling => _stTcs is not null;

    /// <summary>Run held passes until the frame is quiet (see the type summary), bounded by <paramref name="spec"/>. Call inside a
    /// snap scope, after the change was applied; the first pass is the next frame. UI thread.</summary>
    /// <exception cref="InvalidOperationException">No snap scope is open on this host, or a settle is already running.</exception>
    public Task<SettleResult> Settle(SettleSpec? spec = null)
    {
        Threading.ThreadGuard.AssertUi();
        if (_stTcs is not null) throw new InvalidOperationException("A settle is already running on this host.");
        if (_snapDepth == 0) throw new InvalidOperationException("Settle runs inside a snap scope (a frozen host owns one): without it tickers, timers and fades keep the frame moving.");
        spec ??= SettleSpec.Default;
        if (spec.MaxPasses < 1) throw new ArgumentOutOfRangeException(nameof(spec), "MaxPasses must be at least 1.");
        _stSpec = spec;
        _stTcs = new TaskCompletionSource<SettleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stStartMs = _timers.NowMs;
        _stFontsReady = spec.FontsReady is null || spec.FontsReady();   // ready at once: the time cap starts now
        _stFontsReadyMs = _stFontsReady ? _stStartMs : double.NaN;
        _stPasses = _stWorkPasses = 0;
        _stQuiet = false;
        _stSerialAtPassStart = MeasureMemory.ChangeSerial;
        MeasureMemory.BeginJournal();
        WakeFrame();   // the first pass is owed
        return _stTcs.Task;
    }

    /// <summary>Paint, right after the freeze's frame start: open this frame as a settle pass.</summary>
    private void SettlePassStart()
    {
        _stSerialAtPassStart = MeasureMemory.ChangeSerial;
        MeasureMemory.ResetJournal();   // the journal names what changed in the LAST pass
    }

    /// <summary>Paint, after the passive effects: judge the pass and end the settle when it is quiet or a cap is reached.</summary>
    private void SettleAfterPass()
    {
        var spec = _stSpec;
        double now = _timers.NowMs;
        _stPasses++;
        if (_snapDepth == 0) { CompleteSettle(timedOut: false, cut: true, images: 0); return; }

        bool quiet = MeasureMemory.ChangeSerial == _stSerialAtPassStart && !HasSettleWork() && !DetachedHaveSettleWork();
        if (!quiet) _stWorkPasses++;
        if (!_stFontsReady && (spec.FontsReady is null || spec.FontsReady()))
        {
            _stFontsReady = true;
            _stFontsReadyMs = now;
        }

        if (quiet && _stFontsReady)
        {
            int images = _images.CountPinnedUndecoded();
            if (images == 0 || now - _stStartMs >= spec.ImageSoftWaitMs) { CompleteSettle(timedOut: false, cut: false, images); return; }
        }
        if (!quiet && _stWorkPasses >= spec.MaxPasses) { CompleteSettle(timedOut: true, cut: false, _images.CountPinnedUndecoded()); return; }
        bool overTime = _stFontsReady ? now - _stFontsReadyMs >= spec.MaxMs : now - _stStartMs >= spec.FontWaitMs;
        if (overTime) { CompleteSettle(timedOut: true, cut: false, _images.CountPinnedUndecoded()); return; }
        _stQuiet = quiet;
    }

    /// <summary>This host's own settle work after a pass (all O(1) reads).</summary>
    private bool HasSettleWork()
        => _runtime.HasPending || _scene.AnyLayoutDirty || _needFullLayout || _tiers.HasPending || _reconciler.HasWarmingVirtuals;

    private bool DetachedHaveSettleWork()
    {
        for (int i = 0; i < _detachedHosts.Count; i++)
        {
            var child = _detachedHosts[i];
            if (child._snapDepth > 0 && !child._window.IsClosed && child.HasSettleWork()) return true;
        }
        return false;
    }

    /// <summary>The frame loop owes the settle a pass: the last one was not quiet, the snap scope closed under it, or (while it
    /// waits) the fonts became ready or a deadline is due. An image landing wakes the loop on its own. A headless host paints every
    /// turn while it settles (its clock only moves on painted frames).</summary>
    private bool SettleNeedsFrame()
    {
        if (_stTcs is null) return false;
        // Headless time IS the frame clock (one fixed step per painted frame): a waiting settle whose deadlines are on that clock
        // must paint to let them pass, so there it asks for every turn.
        if (!_stQuiet || _snapDepth == 0 || _isHeadless) return true;
        double now = _timers.NowMs;
        if (!_stFontsReady)
            return _stSpec.FontsReady is null || _stSpec.FontsReady() || now - _stStartMs >= _stSpec.FontWaitMs;
        return now - _stStartMs >= _stSpec.ImageSoftWaitMs || now - _stFontsReadyMs >= _stSpec.MaxMs;
    }

    /// <summary>Shorten an idle wait so a waiting settle looks again on time: at its next deadline, or every 16 ms while it polls
    /// the fonts.</summary>
    private int ClampWaitToSettle(int w)
    {
        if (_stTcs is null || !_stQuiet) return w;
        double now = _timers.NowMs;
        double due = _stFontsReady
            ? Math.Min(_stStartMs + _stSpec.ImageSoftWaitMs, _stFontsReadyMs + _stSpec.MaxMs)
            : Math.Min(now + 16.0, _stStartMs + _stSpec.FontWaitMs);
        int dueIn = (int)Math.Ceiling(Math.Max(1.0, due - now));
        return w < 0 ? dueIn : Math.Min(w, dueIn);
    }

    private void CompleteSettle(bool timedOut, bool cut, int images)
    {
        var tcs = _stTcs;
        if (tcs is null) return;
        double now = _timers.NowMs;
        var keys = new List<string>();
        if (timedOut || cut)
        {
            MeasureMemory.CopyJournal(keys);
            if (_runtime.HasPending) keys.Add("render-pending");
            if (_scene.AnyLayoutDirty || _needFullLayout) keys.Add("layout-dirty");
            if (_tiers.HasPending) keys.Add("tier-pending");
            if (_reconciler.HasWarmingVirtuals) keys.Add("virtuals-warming");
            if (DetachedHaveSettleWork()) keys.Add("pop-out");
            if (!_stFontsReady) keys.Add("fonts");
        }
        if (images > 0)
        {
            var sources = new List<string>();
            _images.CountPinnedUndecoded(sources);
            foreach (var s in sources) keys.Add("image:" + FluentGpu.Scene.ImageCache.DiagSourceTail(s));
        }
        double fontWait = _stFontsReady ? _stFontsReadyMs - _stStartMs : now - _stStartMs;
        var result = new SettleResult(_stPasses, timedOut, keys)
        {
            WorkPasses = _stWorkPasses,
            ElapsedMs = now - _stStartMs,
            FontWaitMs = fontWait,
            ImagesUndecoded = images,
            Cut = cut,
        };
        _stTcs = null;
        _stQuiet = false;
        MeasureMemory.EndJournal();

        Diag.Line(string.Create(CultureInfo.InvariantCulture,
            $"[settle] passes={result.Passes} work={result.WorkPasses} ms={result.ElapsedMs:F1} fontsMs={result.FontWaitMs:F1} images={images} timedOut={(timedOut ? 1 : 0)} cut={(cut ? 1 : 0)}"));
        if (timedOut)
            Diag.Line(string.Create(CultureInfo.InvariantCulture,
                $"[settle-timeout] passes={result.Passes} ms={result.ElapsedMs:F1} unsettled={string.Join(",", keys)}"));
        Settled?.Invoke(result);
        tcs.TrySetResult(result);
    }

    /// <summary>Dispose: a settle still running ends as a cut, so its awaiter is never stranded.</summary>
    private void CancelSettleOnDispose() => CompleteSettle(timedOut: false, cut: true, images: 0);
}
