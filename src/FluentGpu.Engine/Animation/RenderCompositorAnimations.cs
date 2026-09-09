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
    }
    private State[] _states = [], _nextStates = [];
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
        for (int i = 0; i < _count; i++) Evaluate(ref _states[i], nowMs);
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
            HasActive |= !_states[i].Done && !_states[i].Parked;
        }
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
                    Evaluate(ref state, capturedAtMs);
                }
                else Evaluate(ref state, nowMs);
                if (state.Desired.Revision != entry.Revision)
                {
                    float current = state.Value, velocity = state.Velocity;
                    state = Seed(in entry, capturedAtMs);
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
        scene.BeginCompositorOverlay();
        _accumulators.Clear();
        HasActive = false;
        for (int i = 0; i < _count; i++)
        {
            ref var state = ref _states[i];
            Evaluate(ref state, nowMs);
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
    };

    private static void Evaluate(ref State state, double nowMs)
    {
        if (state.Parked || state.Done) return;
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
