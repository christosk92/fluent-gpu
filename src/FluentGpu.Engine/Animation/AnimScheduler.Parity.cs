using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — switch-over Step A: AnimEngine API parity (so AppHost/Reconciler/controls can swap the field).
//
//  This increment covers the parity surface that needs NO new infra: the per-node LayoutTransition side-table, the
//  O(1) census, HasTracks/CancelToRest, and the dt-driven Tick(dt) compat entry. The heavier parity (Keyframes +
//  the keyframe store, Drive + the index-based SignalSource clocks, the SizeMode.Reflow machinery + the host
//  worklists ReflowRoots/PendingEnterReflow/…) lands in the next Step-A increments. Build-verified each step.
//
//  ALSO HERE: the CADENCE side tables + the wake answer. Every row carries its own frame rate as DATA (`Cadence`,
//  AnimClock.cs) in `_cadencePeriodMs`, and `NextDueMs(nowMs)` reports when the earliest row is due. That pair
//  REPLACED the host's deleted `AnimIsAmbient()` inference (all-rows-are-loops ∧ none-is-DisplayRate ∧ three grace
//  windows ⇒ throttle the WHOLE loop to AmbientAnimationFps) — eight recorded regressions came from it guessing
//  wrong. Zed GPUI's `Animation::with_max_fps` is the same move. Design: animation-engine-rework-design.md §3.4/§6.2.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    // Per-node layout-transition spec, keyed by node INDEX (slot reuse self-cleans; bounded by the slab size). The
    // reconciler Set/Clears it from BoxEl.Animate/Layout each reconcile; FLIP capture/apply read it.
    private readonly Dictionary<int, LayoutTransition> _transitions = new();

    // ── census (read by the MemCensus sampler / wake diagnostics) ─────────────────────────────────
    /// <summary>Active rows, all channels — O(1).</summary>
    public int TrackCount => _slab.Count;

    // ── CADENCE: per-row frame rate as data (Cadence, AnimClock.cs) ───────────────────────────────
    // Two side arrays indexed by SLOT, deliberately NOT fields on the 64-byte AnimValue row (it is full — see
    // AnimValue.cs). They grow ONLY in Get(), the same seed-time path that already grows the slab's own _rows, never
    // in a frame phase. Every seed writes both (Get resets them to "display rate, never advanced"), so a recycled
    // slot can never inherit the previous tenant's cadence and Free/ClearNode need not touch them.
    //   _cadencePeriodMs[s] : 0 = display rate (present every frame while alive)
    //                         ushort.MaxValue = Cadence.Default → resolve 1000/DefaultLoopHz at USE time, so an app
    //                         power-policy write to DefaultLoopHz retunes every idle loop with no re-seed
    //                         anything else = the literal period in ms (an explicit Cadence.At(hz))
    //   _lastAdvanceMs[s]   : AnimClock.NowMs of the row's last advance; 0 = never advanced (⇒ due now)
    private ushort[] _cadencePeriodMs = new ushort[64];
    private double[] _lastAdvanceMs = new double[64];

    private const ushort CadenceDefaultSentinel = ushort.MaxValue;
    private const ushort CadenceMaxPeriodMs = ushort.MaxValue - 1;   // keeps a literal period distinct from the sentinel

    private float _defaultLoopHz = 30f;

    /// <summary>The rate a <see cref="Cadence.Default"/> row (every perpetual <c>loop: true</c> keyframe track seeded
    /// without an explicit cadence — shimmers, skeleton pulses, spinners) actually runs at. Resolved LATE, on every
    /// advance and every wake computation, so the app's power policy can write it live (battery ⇒ 15, AC ⇒ 30) and
    /// the change applies to already-running rows. <c>0</c> or less = uncapped (display rate). Read through
    /// <see cref="Volatile"/> — the render thread reads it while the UI thread writes it.</summary>
    public float DefaultLoopHz
    {
        get => Volatile.Read(ref _defaultLoopHz);
        set
        {
            Volatile.Write(ref _defaultLoopHz, value > 0f ? value : 0f);
            _censusVersion = -1;   // every Default row's due time just moved: recompute on the next NextDueMs
        }
    }

    /// <summary>Grow the cadence side arrays to cover <paramref name="slot"/> and reset it to the default cadence
    /// (display rate, never advanced). Called from <see cref="Get"/> ONLY — a seed-time path, never a frame phase.</summary>
    private void ResetCadence(int slot)
    {
        if (slot >= _cadencePeriodMs.Length)
        {
            int cap = _cadencePeriodMs.Length == 0 ? 64 : _cadencePeriodMs.Length;
            while (cap <= slot) cap *= 2;
            Array.Resize(ref _cadencePeriodMs, cap);
            Array.Resize(ref _lastAdvanceMs, cap);
        }
        _cadencePeriodMs[slot] = 0;
        _lastAdvanceMs[slot] = 0d;
    }

    /// <summary>Store a seed's cadence on <paramref name="slot"/>. A <c>null</c> <paramref name="cadence"/> keeps the
    /// display-rate default <see cref="ResetCadence"/> just wrote.</summary>
    private void SetCadence(int slot, Cadence? cadence)
    {
        if (cadence is not { } c) return;
        _cadencePeriodMs[slot] = PeriodFor(in c);
        _lastAdvanceMs[slot] = 0d;
    }

    private static ushort PeriodFor(in Cadence c)
    {
        if (c.IsDefault) return CadenceDefaultSentinel;
        float ms = c.PeriodMs;
        if (ms <= 0f) return 0;                                   // DisplayRate
        if (float.IsPositiveInfinity(ms)) return 0;               // Driven/OneShot/Paused: the row's FLAGS decide, not a period
        int r = (int)MathF.Round(ms);
        return r <= 0 ? (ushort)0 : (r >= CadenceMaxPeriodMs ? CadenceMaxPeriodMs : (ushort)r);
    }

    /// <summary>The row's period in ms with <see cref="Cadence.Default"/> resolved against the LIVE
    /// <see cref="DefaultLoopHz"/>. <c>0</c> = display rate.</summary>
    private int ResolvedPeriodMs(int slot)
    {
        ushort p = _cadencePeriodMs[slot];
        if (p != CadenceDefaultSentinel) return p;
        float hz = DefaultLoopHz;
        if (hz <= 0f) return 0;                                   // uncapped policy ⇒ default loops run at display rate
        int ms = (int)MathF.Round(1000f / hz);
        return ms <= 0 ? 0 : (ms >= CadenceMaxPeriodMs ? CadenceMaxPeriodMs : ms);
    }

    // ── the wake answer: min(next-due) over the live rows ─────────────────────────────────────────
    // Recomputed at the END of every Tick (that walk is already paid for) and lazily whenever the slab's mutation
    // Version moves (a seed/free between ticks). -1 forces the first compute (Version starts at 0).
    private int _censusVersion = -1;
    private double _minDueAtMs = double.PositiveInfinity;   // AnimClock.NowMs domain; +∞ = nothing timer-due
    private bool _anyDueNow;
    private int _displayRateLoopCount;
    private int _loopCount;

    // NextDueMs takes the HOST's clock (AppHost passes _timers.NowMs — wall ms since start), while rows advance on
    // AnimClock.NowMs (a sum of clamped/injected quanta: a different origin, and it deliberately drifts from wall
    // time across a GC stall or a dt-injected Tick). Both are milliseconds, so only the OFFSET has to be reconciled —
    // and it is, once per census: the first NextDueMs call after a (re)compute anchors the host clock to it, and
    // every later call subtracts the host-observed elapsed since that anchor. No host change, works for RunFrame
    // (wall) and Tick(dt) alike, and the anchoring error is bounded by one frame.
    private double _extAnchorMs;
    private bool _extAnchorValid;

    private void RefreshCensus()
    {
        if (_censusVersion == _slab.Version) return;
        RecomputeCensus();
    }

    /// <summary>Walk the live rows once and recompute the wake census: the earliest due time, the "a row wants THIS
    /// frame" bit, and the display-rate-loop tripwire count. Zero alloc, no closures. Called at the end of the
    /// per-frame <c>Tick</c> and lazily from <see cref="RefreshCensus"/> on a slab-Version change.</summary>
    private void RecomputeCensus()
    {
        _censusVersion = _slab.Version;
        double now = _clock.NowMs;
        double minDue = double.PositiveInfinity;
        bool dueNow = false;
        int drLoops = 0, loops = 0;
        for (int nodeIndex = _slab.FirstActiveNode; nodeIndex >= 0; nodeIndex = _slab.NextActiveNode(nodeIndex))
            for (int s = _slab.HeadOnNode(nodeIndex); s >= 0; s = _slab.At(s).NextOnNode)
            {
                AnimFlags f = _slab.At(s).Flags;
                bool loop = (f & AnimFlags.Loop) != 0;
                if (loop) loops++;
                // Parked/Done/Driven rows are never TIMER-due: parked is quiesced, done retires this tick, driven is
                // event-woken by its signal write (that was the whole point — a paused playhead costs zero frames).
                if ((f & (AnimFlags.Parked | AnimFlags.Done | AnimFlags.Driven)) != 0) continue;
                int period = ResolvedPeriodMs(s);
                if (period <= 0)
                {
                    dueNow = true;
                    if (loop) drLoops++;
                    continue;
                }
                double last = _lastAdvanceMs[s];
                if (last <= 0d) { dueNow = true; continue; }      // never advanced ⇒ owed its first frame
                double due = last + period;
                if (due <= now) dueNow = true;
                else if (due < minDue) minDue = due;
            }
        _minDueAtMs = minDue;
        _anyDueNow = dueNow;
        _displayRateLoopCount = drLoops;
        _loopCount = loops;
        _extAnchorValid = false;
    }

    /// <summary>Milliseconds until the earliest live row needs a frame. <c>0</c> = a frame is due NOW; <c>+∞</c> =
    /// nothing is timer-due (idle, or only Driven/Parked rows — those are woken by their signal write, never by the
    /// clock); otherwise the ms until the soonest <see cref="CadenceKind.Hz"/> row's next advance. THE host's wait
    /// authority — it calls this several times per frame, so it is O(1): the scan is memoized per tick and per slab
    /// mutation. <paramref name="nowMs"/> is the caller's own monotonic ms clock (see the domain note above
    /// <c>_extAnchorMs</c>).</summary>
    public float NextDueMs(double nowMs)
    {
        RefreshCensus();
        if (_anyDueNow) return 0f;
        if (double.IsPositiveInfinity(_minDueAtMs)) return float.PositiveInfinity;
        if (!_extAnchorValid) { _extAnchorMs = nowMs; _extAnchorValid = true; }
        double sinceCensus = nowMs - _extAnchorMs;
        if (sinceCensus < 0d) sinceCensus = 0d;
        double remaining = _minDueAtMs - _clock.NowMs - sinceCensus;
        return remaining <= 0d ? 0f : (float)remaining;
    }

    /// <summary>Census tripwire: live <c>loop: true</c> rows running at <see cref="CadenceKind.DisplayRate"/> — a
    /// perpetual loop that pins the whole frame loop to the panel refresh. The host samples it once per 30s and
    /// complains; a legitimate one (an indeterminate ProgressBar) is transient, a permanent one is a bug. Memoized
    /// with <see cref="NextDueMs"/>.</summary>
    public int DisplayRateLoopCount { get { RefreshCensus(); return _displayRateLoopCount; } }

    /// <summary>Live looping rows (the <see cref="AnimFlags.Loop"/> bit) — census/diagnostics ONLY; nothing infers a
    /// frame class from it any more (that was the deleted <c>AnimIsAmbient</c>). Memoized with
    /// <see cref="NextDueMs"/>.</summary>
    public int LoopCount { get { RefreshCensus(); return _loopCount; } }

    /// <summary>Live per-node layout-transition specs — O(1).</summary>
    public int TransitionCount => _transitions.Count;

    // ── layout-transition side-table (node index → spec) ──────────────────────────────────────────
    public void SetTransition(NodeHandle node, in LayoutTransition t) => _transitions[(int)node.Raw.Index] = t;
    public bool TryGetTransition(NodeHandle node, out LayoutTransition t) => _transitions.TryGetValue((int)node.Raw.Index, out t);
    public void ClearTransition(NodeHandle node) => _transitions.Remove((int)node.Raw.Index);
    /// <summary>Symmetric teardown when a scene slot is FREED (wired to SceneStore.OnFreeIndex): drop the index-keyed
    /// spec so a freed node leaves no dormant spec the next node reusing the slot inherits. In-flight rows are
    /// gen-checked and self-prune at the next tick's IsLive guard.</summary>
    public void ClearForIndex(int index) { _transitions.Remove(index); ClearInteractTargets(index); }   // #12: also drop the index's interact-target row (was leaked — ClearInteractTargets had no caller)

    /// <summary>True while any row targets this node (the host detects a settled exit orphan when this goes false).</summary>
    public bool HasTracks(NodeHandle node) => _slab.HeadOnNode((int)node.Raw.Index) >= 0;

    /// <summary>Cancel + reset the channel's paint to its settle-time resting sentinel (StrokeTrim/PresentedW/H → NaN,
    /// Clip → Infinite) — symmetric with the settle path. Channels without a rest sentinel behave exactly like
    /// <see cref="Cancel"/>. Ported from AnimEngine.CancelToRest.</summary>
    public void CancelToRest(NodeHandle node, AnimChannel channel)
    {
        Cancel(node, channel);
        if (!_scene.IsLive(node)) return;
        ref NodePaint p = ref _scene.Paint(node);
        switch (channel)
        {
            case AnimChannel.SizeW: p.PresentedW = float.NaN; _scene.Unmark(node, NodeFlags.Relayouting); break;
            case AnimChannel.SizeH: p.PresentedH = float.NaN; break;
            case AnimChannel.StrokeTrimStart: p.StrokeTrimStart = float.NaN; break;
            case AnimChannel.StrokeTrimEnd: p.StrokeTrimEnd = float.NaN; break;
            case AnimChannel.ClipL or AnimChannel.ClipT or AnimChannel.ClipR or AnimChannel.ClipB: p.ClipRect = RectF.Infinite; break;
            default: return;   // no rest sentinel — identical to Cancel
        }
        _scene.Mark(node, NodeFlags.PaintDirty);
    }

    /// <summary>dt-driven tick entry (tests + the AppHost compat path during the switch): advance the owned clock by the
    /// clamped dt and run one tick. RunFrame is the wall-time path; this is the dt-injected one (also the determinism-gate
    /// entry — inject dt ∈ {8.33,16.67,33.3}).</summary>
    public void Tick(float dtMs)
    {
        // Explicit dt-injection (the AppHost compat path + tests/fast-forward + the determinism gate): use the RAW
        // step — NO 1..40ms clamp. The clamp is the WALL-TIME GC-spike defense and lives in AnimClock.Advance/RunFrame;
        // the analytical spring is stable at any dt (no sub-stepping), so the raw step here matches the old Tick(dt).
        _clock.DeltaMs = dtMs;
        _clock.NowMs += dtMs;
        _clock.FrameId++;
        Tick(in _clock);
    }
}
