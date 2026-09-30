using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Scroll.Runtime;

/// <summary>Where the poser writes pixels. The host implements it over the scene's compositor paint table
/// (<c>CompositorPaint(node).Transform = Translate(trans)</c> for content; the matching channel write for effects).
/// Called only from the render thread, inside <see cref="ScrollPoser.Tick"/>, zero-alloc.</summary>
public interface IScrollPoseSink
{
    /// <summary>The viewport's shown (clamped) offset this tick — recorded for chrome (thumb/edge fades) that must not
    /// lag the content. Called before <see cref="PoseContent"/> for the same viewport.</summary>
    void PoseViewport(int vpNode, double shown);

    /// <summary>Content translate along the scroll axis, already device-pixel-snapped, DIP. <paramref name="changed"/> is
    /// false when this is the SAME translate this row posed on the previous tick (a settled viewport re-posed while
    /// something else keeps the compositor ticking): the sink must still write it, but must not report it as a pose
    /// change — only a change may produce repaint damage.</summary>
    void PoseContent(int node, bool horizontal, float trans, bool changed);

    /// <summary>One effect channel value for <paramref name="node"/> (TransY/Opacity/ClipTop/... per
    /// <see cref="EffectChannel"/>). Translations are derived from the same snapped position as the content and are
    /// therefore already on the content's pixel grid — the host must NOT snap them again. <paramref name="changed"/>
    /// has <see cref="PoseContent"/>'s meaning for this effect row.</summary>
    void PoseEffect(int node, EffectChannel channel, float value, bool changed);

    /// <summary>The node's transform-class rows (<see cref="EffectChannel.TransX"/>/<see cref="EffectChannel.TransY"/>/
    /// <see cref="EffectChannel.ScaleXY"/>, stretch included) folded into ONE <see cref="EffectTransform"/> — they share the
    /// node's single <c>LocalTransform</c>, so the sink writes the composed matrix once
    /// (<see cref="EffectTransform.ToLocal"/>) rather than letting the last row win. <paramref name="changed"/> is true
    /// when any folded row's value changed (same meaning as <see cref="PoseEffect"/>).</summary>
    void PoseTransform(int node, in EffectTransform transform, bool changed);
}

/// <summary>What the render thread posed for one viewport on its last tick — read back by the UI thread
/// (<see cref="ScrollHandle.ApplyFeedback"/>) to drive the offset/motion signals.</summary>
/// <param name="Vp">Viewport node index.</param>
/// <param name="Gen">Viewport node generation.</param>
/// <param name="Shown">The position actually shown (after the coverage clamp), content coordinates.</param>
/// <param name="Velocity">The plan's velocity at present time (DIP/s, signed).</param>
/// <param name="Settled">The plan reported itself structurally settled.</param>
/// <param name="Clamped">The coverage clamp moved the shown position (a would-be blank row).</param>
/// <param name="Kind">The plan's <see cref="MotionKind"/>.</param>
/// <param name="PresentSec">The present time the pose was evaluated at.</param>
public readonly record struct ScrollPoseFeedback(
    int Vp, uint Gen, double Shown, double Velocity, bool Settled, bool Clamped, MotionKind Kind, double PresentSec);

/// <summary>
/// The render-thread poser (design §5): every compositor tick, for every covered viewport, evaluates the plan at the
/// PREDICTED PRESENT time, clamps to the realized coverage (so a blank row is structurally impossible — a clamp is
/// recorded, never shown), snaps the translate to the device-pixel grid and writes it through the
/// <see cref="IScrollPoseSink"/>; then evaluates every effect scoped to that viewport against the SAME snapped position.
///
/// <para>SNAP RULE (the one decision effects depend on): the content translate is
/// <c>trans = SnapToDevicePixel((float)(WindowOrigin − p'), scale)</c>. Effects are evaluated NOT at <c>p'</c> but at
/// <c>pSnapped = WindowOrigin − trans</c> — the position the content is actually painted at. A sticky header's
/// <c>TransY = pSnapped + Inset − NodeY</c> then differs from the content's translate by an exact content-space
/// constant, so header and rows sit on the same pixel grid; snapping the header separately (or evaluating at the
/// unsnapped <c>p'</c>) can put it half a device pixel off the rows it pins over, which reads as a seam.</para>
///
/// <para>THREADING: <see cref="Adopt"/> and <see cref="Tick"/> run on the render thread only. Feedback is published
/// per viewport through a seqlock (render thread writes, UI thread reads via <see cref="TryGetFeedback"/>). The
/// coverage table is a private copy (<see cref="Adopt"/> copies) so the publisher may reuse its buffer. Zero
/// allocation per tick.</para>
/// </summary>
public sealed class ScrollPoser
{
    public const int Cap = PlanSlots.Capacity;

    private struct FeedbackSlot
    {
        public int Seq;
        public bool Live;
        public ScrollPoseFeedback Value;
    }

    private readonly ScrollCoverageTable _cov = new();
    private readonly FeedbackSlot[] _feedback = new FeedbackSlot[Cap];

    // "changed vs previous tick" memory, indexed like the adopted coverage table.
    private readonly float[] _lastTrans = new float[ScrollCoverageTable.RowCapacity];
    private readonly bool[] _lastPosed = new bool[ScrollCoverageTable.RowCapacity];
    private readonly float[] _lastEffect = new float[ScrollCoverageTable.EffectCapacity];
    private bool _freshAdopt;
    private bool _hasActive;
    private readonly bool _recordsProbePoses;

    /// <param name="recordsProbePoses">True for THE render-thread poser only: it is the sole producer of the probe's
    /// render ring (<see cref="ScrollProbe.Pose"/>). The UI-thread poser (hit-testing, the headless recorder) evaluates
    /// the same plans with the same arithmetic but records nothing — a second writer would make the single-producer
    /// ring a data race and record every frame's pose twice.</param>
    public ScrollPoser(bool recordsProbePoses = false) => _recordsProbePoses = recordsProbePoses;

    /// <summary>True when any covered viewport's plan was not settled on the last tick — the host's
    /// <c>MotionDue</c> term (<c>poser.HasActive || animations.HasActive</c>).</summary>
    public bool HasActive => _hasActive;

    /// <summary>Number of coverage rows currently adopted.</summary>
    public int RowCount => _cov.RowCount;

    /// <summary>Takes a private copy of the published coverage. The next <see cref="Tick"/> always reports a change
    /// (the scene it poses over is new).</summary>
    public void Adopt(ScrollCoverageTable cov)
    {
        _cov.CopyFrom(cov);
        _freshAdopt = true;
    }

    /// <summary>Poses every covered viewport at <paramref name="presentSec"/>. Returns true when any content
    /// translate or effect value differs from the previous tick (or the coverage was just adopted), so the host can
    /// skip a present when nothing moved.</summary>
    public bool Tick(PlanSlots slots, double presentSec, float dpiScale, IScrollPoseSink sink)
    {
        bool adopted = _freshAdopt;
        bool changed = adopted;
        _freshAdopt = false;
        bool anyActive = false;
        long presentQpc = (long)(presentSec * Stopwatch.Frequency);
        int rows = _cov.RowCount;

        for (int r = 0; r < rows; r++)
        {
            ref readonly ScrollCoverageRow row = ref _cov.RowAt(r);
            if (!slots.TryRead(row.Id, out ScrollPlan plan, out double frameShift))
            {
                _lastPosed[r] = false;
                continue;
            }

            // FRAME RULE: pose in the coverage's own coordinate frame. A measured-extent correction shifts the plan's
            // frame the moment layout discovers it (Virtualizer.ApplyMeasured -> PlanSlots.Shift), but the content this
            // coverage describes was arranged before that shift and stays on screen until the next publication is
            // adopted; posing the shifted plan against it would move the content by the correction for those ticks and
            // back on the adopt. `drift` is every shift newer than this coverage, which makes (shift, coverage) atomic
            // from the poser's view: the plan in the coverage's frame is exactly `p - drift` (ScrollPlan.Shifted).
            double drift = frameShift - row.FrameShift;
            double p = plan.Eval(presentSec, out double v, out bool settled) - drift;

            // Coverage clamp: never show content the UI thread did not realize. The clamp relaxes at the content's
            // true ends (coverage starting at 0 / ending at ExtentTotal) so a rubber-band overpan — which shows empty
            // space, not un-realized rows — is not counted as a clamp.
            double lo = row.Start <= 0.0 ? double.NegativeInfinity : row.Start;
            double hi = row.End >= row.ExtentTotal ? double.PositiveInfinity : row.End - row.Viewport;
            if (hi < lo) hi = lo;
            double shown = p < lo ? lo : (p > hi ? hi : p);
            bool clamped = shown != p;

            float trans = ScrollEffectEval.SnapToDevicePixel((float)(row.WindowOrigin - shown), dpiScale);
            // A row's pose is a CHANGE when this poser never posed it, the coverage was just adopted (the snapshot it
            // poses over is new — its overlay starts from the authored columns), or the value moved.
            bool fresh = !_lastPosed[r] || adopted;
            bool transChanged = fresh || _lastTrans[r] != trans;
            sink.PoseViewport(row.Vp, shown);
            sink.PoseContent(row.ContentNodeIndex, row.Horizontal, trans, transChanged);
            if (transChanged) changed = true;
            _lastTrans[r] = trans;
            _lastPosed[r] = true;

            // Effects ride the SNAPPED position (see the class remarks) so they share the content's pixel grid.
            double pSnapped = row.WindowOrigin - (double)trans;
            int eEnd = row.EffectStart + row.EffectCount;
            // Transform-class rows of one node are contiguous (the host appends a node's rows together): fold them into
            // one EffectTransform and hand it over when the run ends, so rows sharing the LocalTransform compose.
            int tfNode = -1;
            bool tfChanged = false;
            EffectTransform tf = EffectTransform.Identity;
            for (int e = row.EffectStart; e < eEnd; e++)
            {
                ScrollEffectRow er = _cov.EffectAt(e);
                ScrollEffect effect = er.Effect;
                EffectGeometry geometry = er.Geometry;
                float value = ScrollEffectEval.Evaluate(in effect, pSnapped, in geometry);
                bool effectChanged = fresh || _lastEffect[e] != value;
                if (effectChanged) changed = true;
                _lastEffect[e] = value;
                // Engaged-edge evidence (EngagedCrossings): THE render poser notes the tick at which a sticky / sticky-clip
                // row crosses its engage threshold at the posed offset — what the UI's `engaged:` flip is measured against.
                if (_recordsProbePoses && effect.Kind is EffectKind.Sticky or EffectKind.StickyClip)
                    EngagedCrossings.Observe(er.NodeIndex, ScrollEffectEval.StickyEngaged(in effect, pSnapped, in geometry),
                        presentSec, pSnapped);
                if (!ScrollEffectEval.IsTransformChannel(effect.Channel))
                {
                    sink.PoseEffect(er.NodeIndex, effect.Channel, value, effectChanged);
                    continue;
                }
                if (er.NodeIndex != tfNode)
                {
                    if (tfNode >= 0) sink.PoseTransform(tfNode, in tf, tfChanged);
                    tfNode = er.NodeIndex;
                    tf = EffectTransform.Identity;
                    tfChanged = false;
                }
                tf = tf.Add(effect.Channel, effect.Kind, value);
                tfChanged |= effectChanged;
            }
            if (tfNode >= 0) sink.PoseTransform(tfNode, in tf, tfChanged);

            // Feedback and the probe speak the PLAN's (current) frame, the one every UI-side reader works in.
            double shownPlan = shown + drift;
            if (_recordsProbePoses) ScrollProbe.Pose(row.Vp, presentQpc, shownPlan, v, clamped, trans, p + drift);
            if (!settled) anyActive = true;

            PublishFeedback(r, new ScrollPoseFeedback(row.Vp, row.Gen, shownPlan, v, settled, clamped, plan.Kind, presentSec));
        }

        _hasActive = anyActive;
        return changed;
    }

    /// <summary>UI-thread read of the last pose for <paramref name="vp"/>. False when the viewport was not covered
    /// on the last tick. Never torn.</summary>
    public bool TryGetFeedback(ScrollViewportId vp, out ScrollPoseFeedback feedback)
    {
        for (int i = 0; i < Cap; i++)
        {
            ref FeedbackSlot s = ref _feedback[i];
            int s1, s2;
            bool live;
            ScrollPoseFeedback value;
            do
            {
                s1 = Volatile.Read(ref s.Seq);
                while ((s1 & 1) != 0) { Thread.SpinWait(1); s1 = Volatile.Read(ref s.Seq); }
                Thread.MemoryBarrier();
                live = s.Live;
                value = s.Value;
                Thread.MemoryBarrier();
                s2 = Volatile.Read(ref s.Seq);
            } while (s1 != s2);
            if (live && value.Vp == vp.Node && value.Gen == vp.Gen)
            {
                feedback = value;
                return true;
            }
        }
        feedback = default;
        return false;
    }

    private void PublishFeedback(int slot, in ScrollPoseFeedback f)
    {
        ref FeedbackSlot s = ref _feedback[slot];
        Volatile.Write(ref s.Seq, s.Seq + 1);
        Thread.MemoryBarrier();
        s.Live = true;
        s.Value = f;
        Thread.MemoryBarrier();
        Volatile.Write(ref s.Seq, s.Seq + 1);
    }
}
