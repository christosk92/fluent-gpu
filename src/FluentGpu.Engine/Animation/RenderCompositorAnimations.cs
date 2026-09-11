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
    }
    private State[] _states = [], _nextStates = [];
    // Render tick bookkeeping for the pending-start hold: the previous tick's instant and the interval before it.
    private double _lastTickMs;
    private float _tickIntervalMs;
    private Dictionary<ulong, int> _indices = new(), _nextIndices = new();
    private CompositorAnimationPose[] _feedback = [];
    private readonly Dictionary<NodeHandle, AnimEngine.Accum> _accumulators = new(64);
    private int _count;
    private bool _paused;
    private double _pausedAtMs;
    public bool HasActive { get; private set; }
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
        // Release references into omitted desired snapshots; retained states have copied the new snapshot's keys.
        Array.Clear(_states, 0, _count);
        (_states, _nextStates) = (_nextStates, _states);
        (_indices, _nextIndices) = (_nextIndices, _indices);
        _count = count;
        Tick(scene, nowMs);
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
        for (int i = 0; i < _count; i++)
        {
            ref var state = ref _states[i];
            Evaluate(ref state, nowMs, _tickIntervalMs);
            ref readonly var row = ref state.Desired.Row;
            if (!state.Parked && scene.IsLive(row.Node))
            {
                if (row.Channel is AnimChannel.HoverFade or AnimChannel.PressFade)
                    scene.SetCompositorInteraction(row.Node, row.Channel == AnimChannel.PressFade, state.Value);
                else if (row.Channel == AnimChannel.BrushFade) scene.SetCompositorBrush(row.Node, state.Value);
                else
                {
                    ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(_accumulators, row.Node, out bool exists);
                    if (!exists) accumulator = AnimEngine.Accum.FromPaint(in scene.Paint(row.Node));
                    accumulator.Fold(row.Channel, state.Value, replace: true);
                }
                HasActive |= !_paused && !state.Done;
            }
            _feedback[i] = new(row.Node, row.Channel, state.Desired.Instance, state.Desired.Revision,
                state.Value, state.Velocity, MathF.Max(0, state.ElapsedMs), MathF.Max(0, -state.ElapsedMs), state.Done);
        }
        foreach (var entry in _accumulators) Compose(scene, entry.Key, entry.Value);
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

    private static void Compose(SceneRecordingSnapshot scene, NodeHandle node, in AnimEngine.Accum accumulator)
    {
        ref var paint = ref scene.CompositorPaint(node);
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
