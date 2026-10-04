using FluentGpu.Foundation;
using FluentGpu.Scene;
using System.Runtime.InteropServices;

namespace FluentGpu.Animation;

/// <summary>
/// Render-thread-owned generator state. A complete desired set replaces membership; unchanged seed revisions retain
/// their trajectory even when UI publications are skipped. Every sample uses the engine's existing absolute-time law.
/// </summary>
public sealed class RenderCompositorAnimations
{
    private struct State
    {
        public CompositorAnimationSnapshot.Entry Desired;
        public double AnchorNowMs;
        public float AnchorElapsedMs, Value, Velocity, ElapsedMs;
        public bool Parked, Done;
        /// <summary>The render-side twin of the engine's <c>_lastAdvanceMs</c>: when this row was last re-sampled.
        /// 0 = never. Only consulted when the row carries a cadence period (<c>Desired.PeriodMs &gt; 0</c>).</summary>
        public double LastAdvanceMs;
        /// <summary>Pending start (<see cref="AnimFlags.StartPending"/>, a structural enter/exit): the first render frame
        /// that poses the row HOLDS it at t=0 (<see cref="HoldNowMs"/>, with that frame's steady interval in
        /// <see cref="HoldRefMs"/>); the next one resolves the start from that presentation
        /// (<see cref="AnimEngine.PendingStartStep"/>), so a long first frame cannot eat the entrance.</summary>
        public bool StartPending;
        public double HoldNowMs;
        public float HoldRefMs;
        /// <summary>The value this row last POSED onto a scene snapshot, and whether it ever posed one. A row held by
        /// its cadence (<c>PeriodMs</c> not yet elapsed) and a <c>Done</c> row re-pose the IDENTICAL float every tick;
        /// posing that as a CHANGE is what made a 60 Hz marquee damage the window at 120 Hz. Cleared when the row
        /// parks, so an un-park always re-poses as a change.</summary>
        public float PosedValue;
        public bool HasPosed;
        /// <summary>The row's node could not reach a pixel on its last tick (<see cref="Reaches"/>): nothing was posed
        /// for it, so the frame shows whatever it last recorded. The tick it can reach pixels again re-poses it as a
        /// change, whatever its value.</summary>
        public bool Hidden;
    }
    /// <summary>One node's folded pose for this tick, plus whether ANY of its channels actually moved. Folded per node
    /// rather than per row because a node with a TranslateX and an Opacity row must damage once, not twice.</summary>
    private struct NodeAcc { public AnimEngine.Accum Acc; public bool Changed, OpacityMoving, OpacityPosed, Hidden; }
    private State[] _states = [], _nextStates = [];
    // Per row, this tick: evaluated on a live, un-parked node (pass 1), and whether that row moved to a new Done.
    private bool[] _rowLive = [], _rowDoneEdge = [];
    // Render tick bookkeeping for the pending-start hold: the previous tick's instant and the interval before it.
    private double _lastTickMs;
    private float _tickIntervalMs;
    private Dictionary<ulong, int> _indices = new(), _nextIndices = new();
    private CompositorAnimationPose[] _feedback = [];
    private readonly Dictionary<NodeHandle, NodeAcc> _accumulators = new(64);
    /// <summary>Nodes whose row DISAPPEARED or parked since the last tick. They present their AUTHORED pose again —
    /// a real pixel change that no surviving row will report — so the next tick damages each one exactly once.
    /// Collected on the publisher-driven Adopt path, where an allocation is allowed; the steady tick only drains it.</summary>
    private NodeHandle[] _reverted = [];
    private int _revertedCount;
    private int _count;
    private bool _paused;
    private double _pausedAtMs;
    public bool HasActive { get; private set; }
    /// <summary>Did this tick change ANY pixels — a posed value that moved, a row that finished, or a row that
    /// disappeared or parked? False means the compositor produced a byte-identical scene, which is what lets the host
    /// elide the whole record+submit rather than only the present. <c>Done</c> is part of it because the feedback
    /// publish is what completes UI lifecycles, and a skipped record skips that publish too.</summary>
    public bool ChangedThisTick { get; private set; }
    public ReadOnlySpan<CompositorAnimationPose> Feedback => _feedback.AsSpan(0, _count);

    public void Pause(double nowMs)
    {
        if (_paused) return;
        for (int i = 0; i < _count; i++) Evaluate(ref _states[i], nowMs, _tickIntervalMs);
        _pausedAtMs = nowMs;
        _paused = true;
        HasActive = false;
    }

    public void Resume(double nowMs)
    {
        if (!_paused) return;
        double parkedMs = Math.Max(0, nowMs - _pausedAtMs);
        HasActive = false;
        for (int i = 0; i < _count; i++)
        {
            _states[i].AnchorNowMs += parkedMs;
            if (_states[i].StartPending && !double.IsNaN(_states[i].HoldNowMs)) _states[i].HoldNowMs += parkedMs;
            HasActive |= !_states[i].Done && !_states[i].Parked;
        }
        _lastTickMs = 0;   // the pause is not a frame interval
        _paused = false;
    }

    public void Adopt(CompositorAnimationSnapshot desired, SceneRecordingSnapshot scene, double nowMs)
    {
        if (_paused) nowMs = _pausedAtMs;
        double capturedAtMs = _paused ? Math.Min(desired.CapturedAtMs, _pausedAtMs) : desired.CapturedAtMs;
        SceneRecordingSnapshot.Grow(ref _nextStates, desired.Count);
        SceneRecordingSnapshot.Grow(ref _feedback, desired.Count);
        SceneRecordingSnapshot.Grow(ref _rowLive, desired.Count);
        SceneRecordingSnapshot.Grow(ref _rowDoneEdge, desired.Count);
        _accumulators.EnsureCapacity(desired.Count);
        _nextIndices.Clear();
        int count = 0;
        for (int i = 0; i < desired.Count; i++)
        {
            ref readonly var entry = ref desired.At(i);
            if (!scene.IsLive(entry.Row.Node)) continue;
            State state;
            Keyframe[]? ownedKeys = null;
            if (_indices.TryGetValue(entry.Instance, out int oldIndex))
            {
                state = _states[oldIndex];
                ownedKeys = state.Desired.Keys;
                bool parking = !state.Parked && entry.Row.Has(AnimFlags.Parked);
                // The UI visibility edge belongs to its publication timestamp, not to the later instant when
                // a busy renderer happens to acquire it. Analytical sampling can recover that earlier pose.
                if (parking && state.Desired.Revision == entry.Revision)
                {
                    state.Done = state.Desired.Row.Has(AnimFlags.Done);
                    Evaluate(ref state, capturedAtMs, _tickIntervalMs);
                }
                else Evaluate(ref state, nowMs, _tickIntervalMs);
                if (state.Desired.Revision != entry.Revision)
                {
                    float current = state.Value, velocity = state.Velocity;
                    state = Seed(in entry, capturedAtMs);
                    // A retained instance has already been posed on screen: a retarget continues it, it never re-pends.
                    state.StartPending = false;
                    if (entry.Row.Kind == GenKind.Spring && !entry.ExplicitFrom)
                    {
                        state.Desired.Row.Gen = Generators.BakeSpring(entry.Row.Gen.Omega, entry.Row.Gen.Zeta,
                            current - entry.Row.To, velocity);
                        state.Value = current; state.Velocity = velocity;
                        state.AnchorElapsedMs = 0; state.AnchorNowMs = nowMs;
                    }
                }
                else
                {
                    // Adopt fresh key storage before the previous snapshot lease is released, retaining generator
                    // coefficients rebased on a render-side spring retarget rather than the UI's older pose.
                    var generator = state.Desired.Row.Gen;
                    state.Desired = entry;
                    state.Desired.Row.Gen = generator;
                    bool parked = entry.Row.Has(AnimFlags.Parked);
                    if (parked != state.Parked)
                    {
                        state.AnchorElapsedMs = state.ElapsedMs;
                        state.AnchorNowMs = capturedAtMs;
                        state.Parked = parked;
                        // A row that PARKS stops posing, so the node falls back to its authored pose — a pixel change
                        // nothing else reports. Caught here rather than in the post-loop walk below because _states
                        // still holds the OLD Parked value there; the flip is only visible at this line. Clearing
                        // HasPosed makes the eventual un-park re-pose as a change rather than compare against a value
                        // the node has not shown for however long it was parked.
                        if (parked && state.HasPosed) NoteReverted(entry.Row.Node);
                        state.HasPosed = false;
                    }
                }
            }
            else state = Seed(in entry, capturedAtMs);
            // Adoption may release the old publisher slot before sampling the previous trajectory. Keep keyframes
            // owned by this renderer, never a reference to storage that the UI can recapture after lease handover.
            if (ownedKeys is null || ownedKeys.Length != entry.Keys.Length) ownedKeys = new Keyframe[entry.Keys.Length];
            entry.Keys.AsSpan().CopyTo(ownedKeys);
            state.Desired.Keys = ownedKeys;
            _nextStates[count] = state;
            _nextIndices.Add(entry.Instance, count++);
        }
        // Rows that VANISHED from the desired set: their node presents its authored pose again, and no surviving row
        // will report that. Walked BEFORE the Array.Clear below — that clear wipes _states, so a walk placed after it
        // (or after the swap) reads zeroed entries and silently reverts nothing.
        for (int i = 0; i < _count; i++)
        {
            ref readonly var old = ref _states[i];
            if (old.HasPosed && !old.Parked && !_nextIndices.ContainsKey(old.Desired.Instance))
                NoteReverted(old.Desired.Row.Node);
        }
        // Release references into omitted desired snapshots; retained states have copied the new snapshot's keys.
        Array.Clear(_states, 0, _count);
        (_states, _nextStates) = (_nextStates, _states);
        (_indices, _nextIndices) = (_nextIndices, _indices);
        _count = count;
        Tick(scene, nowMs);
    }

    /// <summary>Queue a node whose pose reverted to its authored value. Grows on the Adopt path only (like
    /// <c>_nextStates</c>/<c>_feedback</c>), so the steady tick stays allocation-free — gate.compositor-alloc.</summary>
    private void NoteReverted(NodeHandle node)
    {
        SceneRecordingSnapshot.Grow(ref _reverted, _revertedCount + 1);
        _reverted[_revertedCount++] = node;
    }

    public void Tick(SceneRecordingSnapshot scene, double nowMs)
    {
        if (_paused) nowMs = _pausedAtMs;
        // The steady render interval going INTO this tick — the reference a pending-start row held on this tick caps its
        // first advance against. 0 = unknown (the first tick, or a gap longer than any frame — an idle render thread).
        if (!_paused && nowMs > _lastTickMs)
        {
            double interval = _lastTickMs > 0 ? nowMs - _lastTickMs : 0;
            _tickIntervalMs = interval > 0 && interval <= AnimClock.MaxDeltaMs ? (float)interval : 0f;
            _lastTickMs = nowMs;
        }
        scene.BeginCompositorOverlay();
        _accumulators.Clear();
        HasActive = false;
        // A revert queued by Adopt is a real change even if no row moves this tick.
        ChangedThisTick = _revertedCount > 0;
        // PASS 1: sample every row and fold the transform/opacity channels per node — nothing is written to the scene yet,
        // because whether a row may pose at all depends on its ancestors' opacity THIS tick (pass 2).
        for (int i = 0; i < _count; i++)
        {
            ref var state = ref _states[i];
            bool wasDone = state.Done;
            Evaluate(ref state, nowMs, _tickIntervalMs);
            ref readonly var row = ref state.Desired.Row;
            _rowLive[i] = !state.Parked && scene.IsLive(row.Node);
            _rowDoneEdge[i] = state.Done && !wasDone;
            if (_rowLive[i] && row.Channel is not (AnimChannel.HoverFade or AnimChannel.PressFade or AnimChannel.BrushFade))
            {
                ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(_accumulators, row.Node, out bool exists);
                if (!exists) accumulator.Acc = AnimEngine.Accum.FromPaint(in scene.Paint(row.Node));
                accumulator.Acc.Fold(row.Channel, state.Value, replace: true);
                accumulator.OpacityMoving |= row.Channel == AnimChannel.Opacity && !state.Done;
                accumulator.OpacityPosed |= row.Channel == AnimChannel.Opacity;
            }
            _feedback[i] = new(row.Node, row.Channel, state.Desired.Instance, state.Desired.Revision,
                state.Value, state.Velocity, MathF.Max(0, state.ElapsedMs), MathF.Max(0, -state.ElapsedMs), state.Done);
        }
        // PASS 2: a row poses only when its node can reach a pixel. One that cannot (an ancestor at opacity 0 or not
        // Visible — an always-mounted busy bar parked at opacity 0, looping) changes nothing on screen: it writes no pose,
        // damages nothing and, when it loops forever, does not keep the render loop awake. Its value is a pure function
        // of time, so the tick it can reach pixels again poses it exactly where it belongs.
        for (int i = 0; i < _count; i++)
        {
            if (!_rowLive[i]) continue;
            ref var state = ref _states[i];
            ref readonly var row = ref state.Desired.Row;
            // A row reaching Done completes a UI lifecycle through the feedback publish — it counts as "this tick did
            // something" even when the value held, and even when nobody can see it.
            ChangedThisTick |= _rowDoneEdge[i];
            if (!Reaches(scene, row.Node))
            {
                state.Hidden = true;
                // flagged, not removed: a descendant's Reaches still reads this node's opacity from its accumulator
                if (row.Channel is not (AnimChannel.HoverFade or AnimChannel.PressFade or AnimChannel.BrushFade))
                    CollectionsMarshal.GetValueRefOrNullRef(_accumulators, row.Node).Hidden = true;
                // A finite row still ticks to its Done (its lifecycle needs the edge); a loop never ends, so it waits.
                HasActive |= !_paused && !state.Done && !row.Has(AnimFlags.Loop);
                continue;
            }
            // Bitwise compare, deliberately no tolerance: a held or Done row re-poses the IDENTICAL float, and a live
            // row's next analytic sample differs in at least one ulp. (WebRender's approx_eq guards a property binding
            // that can be re-sent unchanged; our Value is recomputed, not re-sent.) A row that was hidden posed nothing
            // since: it re-poses as a change.
            bool changed = !state.HasPosed || state.Value != state.PosedValue || state.Hidden;
            state.PosedValue = state.Value;
            state.HasPosed = true;
            state.Hidden = false;
            ChangedThisTick |= changed;
            if (row.Channel is AnimChannel.HoverFade or AnimChannel.PressFade)
                scene.SetCompositorInteraction(row.Node, row.Channel == AnimChannel.PressFade, state.Value, changed);
            else if (row.Channel == AnimChannel.BrushFade) scene.SetCompositorBrush(row.Node, state.Value, changed);
            else CollectionsMarshal.GetValueRefOrNullRef(_accumulators, row.Node).Changed |= changed;   // any channel moving damages the node once
            HasActive |= !_paused && !state.Done;
        }
        foreach (var entry in _accumulators) if (!entry.Value.Hidden) Compose(scene, entry.Key, entry.Value.Acc, entry.Value.Changed);
        // Drain the reverts LAST, so a node that both lost a row and kept another is damaged by whichever ran first
        // and not twice — MarkCompositorSelfChanged is idempotent within an epoch.
        for (int i = 0; i < _revertedCount; i++)
            if (scene.IsLive(_reverted[i])) scene.MarkCompositorSelfChanged(_reverted[i]);
        _revertedCount = 0;
    }

    /// <summary>Can <paramref name="node"/> put a pixel on screen this tick? Not when it or an ancestor is not Visible, or
    /// an ANCESTOR sits at opacity 0 — the value THIS tick poses when an animation folds it, else the scene's (a hover or
    /// pressed opacity counts as possibly visible). The node's OWN opacity hides it only when no row of its own poses it:
    /// a posed 0 is exactly what hides a faded-out node, so that pose must be written. An opacity that is itself in motion
    /// counts as visible: a fade-in from 0 is what must keep posing. Conservative elsewhere: a broken chain reads as visible.</summary>
    private bool Reaches(SceneRecordingSnapshot scene, NodeHandle node)
    {
        for (NodeHandle n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
        {
            if ((scene.Flags(n) & NodeFlags.Visible) == 0) return false;
            ref readonly NodePaint p = ref scene.Paint(n);
            float op = p.Opacity;
            if (_accumulators.TryGetValue(n, out NodeAcc acc))
            {
                if (acc.OpacityMoving || (n == node && acc.OpacityPosed)) continue;
                op = acc.Acc.Op;
            }
            if (!float.IsNaN(p.HoverOpacity)) op = MathF.Max(op, p.HoverOpacity);
            if (!float.IsNaN(p.PressedOpacity)) op = MathF.Max(op, p.PressedOpacity);
            if (op <= 0f) return false;
        }
        return true;
    }

    private static State Seed(in CompositorAnimationSnapshot.Entry entry, double capturedAtMs) => new()
    {
        Desired = entry,
        AnchorNowMs = capturedAtMs,
        AnchorElapsedMs = entry.Row.ElapsedMs - entry.Row.DelayRemainingMs,
        ElapsedMs = entry.Row.ElapsedMs - entry.Row.DelayRemainingMs,
        Value = entry.Row.Position,
        Velocity = entry.Row.Velocity,
        Parked = entry.Row.Has(AnimFlags.Parked),
        Done = entry.Row.Has(AnimFlags.Done),
        StartPending = entry.Row.Has(AnimFlags.StartPending) && entry.PeriodMs == 0,
        HoldNowMs = double.NaN,
    };

    private static void Evaluate(ref State state, double nowMs, float refIntervalMs)
    {
        if (state.Parked || state.Done) return;
        // PENDING START (AnimFlags.StartPending — a structural enter/exit the UI just seeded): the first render frame to
        // pose it holds t=0, and that presentation is where its start time resolves (the UI's seed-frame hold, render
        // side). The next frame then advances by the time since it — capped to one steady interval when the held frame
        // itself ran long (a first frame of a new page recording/uploading for 40-60 ms), exactly AnimEngine's PASS1 rule.
        if (state.StartPending)
        {
            if (double.IsNaN(state.HoldNowMs))
            {
                state.HoldNowMs = nowMs;
                state.HoldRefMs = refIntervalMs > 0f ? refIntervalMs : AnimClock.DefaultDeltaMs;
                return;
            }
            if (nowMs <= state.HoldNowMs) return;
            float first = AnimEngine.PendingStartStep((float)(nowMs - state.HoldNowMs), state.HoldRefMs);
            state.AnchorNowMs = nowMs - first;
            state.StartPending = false;
        }
        // CADENCE (the render-thread half of AnimEngine's PASS1 due-check): a row that states its own frame rate is
        // re-sampled only when its period has elapsed; in between its Value/ElapsedMs are HELD, so a 30Hz shimmer
        // steps at 30Hz even though the compositor is posing at panel rate for something else. Sampling stays
        // analytical/absolute, so holding costs nothing and skipping never accumulates drift.
        ushort periodMs = state.Desired.PeriodMs;
        if (periodMs > 0)
        {
            if (state.LastAdvanceMs > 0d && nowMs - state.LastAdvanceMs < periodMs - AnimEngine.CadenceSlackMs) return;
            state.LastAdvanceMs = nowMs;
        }
        state.ElapsedMs = state.AnchorElapsedMs + (float)Math.Max(0, nowMs - state.AnchorNowMs);
        if (state.ElapsedMs < 0) return;
        ref readonly var row = ref state.Desired.Row;
        if (row.Kind == GenKind.Spring)
        {
            float restDelta = row.Channel is AnimChannel.SizeW or AnimChannel.SizeH ? .5f : Generators.RestDelta;
            var value = Generators.EvalSpring(in row.Gen, row.To, state.ElapsedMs, restDelta, Generators.RestSpeed, out var velocity);
            state.Value = value.Value; state.Velocity = velocity; state.Done = value.Done;
            return;
        }
        float duration = row.Gen.DurationMs <= 0 ? 1 : row.Gen.DurationMs;
        float progress = state.ElapsedMs / duration;
        if (row.Has(AnimFlags.Loop)) progress -= MathF.Floor(progress);
        else if (progress >= 1) { progress = 1; state.Done = true; }
        else progress = MathF.Max(0, progress);
        state.Value = state.Desired.Keys.Length >= 2
            ? AnimEngine.Sample(state.Desired.Keys, progress)
            : row.Gen.FromV + (row.To - row.Gen.FromV) * Easings.Ease((Easing)(byte)row.Gen.EaseId, progress);
        state.Velocity = 0; // The existing eased/keyframe engine carries velocity only for analytical springs.
    }

    private static void Compose(SceneRecordingSnapshot scene, NodeHandle node, in AnimEngine.Accum accumulator, bool changed)
    {
        ref var paint = ref scene.CompositorPaint(node, changed);
        var transform = Affine2D.Translation(accumulator.Tx, accumulator.Ty);
        if (accumulator.Rot != 0) transform = transform.Multiply(Affine2D.Rotation(accumulator.Rot * (MathF.PI / 180)));
        if (accumulator.Sx != 1 || accumulator.Sy != 1) transform = transform.Multiply(Affine2D.Scale(accumulator.Sx, accumulator.Sy));
        if ((scene.Flags(node) & NodeFlags.DragGhost) == 0)
        {
            paint.LocalTransform = transform;
            paint.Opacity = accumulator.Op;
        }
        paint.BlurSigma = MathF.Max(0, accumulator.Blur);
        if (!float.IsNaN(accumulator.Sw)) paint.PresentedW = accumulator.Sw;
        if (!float.IsNaN(accumulator.Sh)) paint.PresentedH = accumulator.Sh;
        if (!float.IsNaN(accumulator.TrimStart)) paint.StrokeTrimStart = accumulator.TrimStart;
        if (!float.IsNaN(accumulator.TrimEnd)) paint.StrokeTrimEnd = accumulator.TrimEnd;
        if (!float.IsNaN(accumulator.ClipL) || !float.IsNaN(accumulator.ClipT)
            || !float.IsNaN(accumulator.ClipR) || !float.IsNaN(accumulator.ClipB))
        {
            ref readonly var bounds = ref scene.Bounds(node);
            paint.ClipRect = RectF.FromLTRB(float.IsNaN(accumulator.ClipL) ? 0 : accumulator.ClipL,
                float.IsNaN(accumulator.ClipT) ? 0 : accumulator.ClipT,
                float.IsNaN(accumulator.ClipR) ? bounds.W : accumulator.ClipR,
                float.IsNaN(accumulator.ClipB) ? bounds.H : accumulator.ClipB);
        }
    }
}
