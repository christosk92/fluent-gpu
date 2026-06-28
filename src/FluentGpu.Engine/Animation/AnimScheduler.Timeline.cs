using System;
using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — switch-over Step A (cont'd): multi-keyframe (@keyframes) + driven-timeline parity.
//
//  Keyframes/Drive need per-row value-keyframes; the lean 64B AnimValue doesn't carry them, so for PARITY they live
//  in a slot-keyed side store (the clean shared keyframe arena + index-based SignalSource are the follow-up — this
//  preserves behavior without regressing the gallery/controls). Driven rows stash the [domainMin, domainMax] domain in
//  the Generator's eased union (FromV = min, DurationMs = span) and read progress from the existing DrivenClockTable
//  by index. Sampling + the eased/driven progress math port from AnimEngine.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    private readonly Dictionary<int, Keyframe[]> _keysBySlot = new();   // slot → value keyframes (Keyframes/Drive rows)

    /// <summary>Driven value sources (scroll offset, playback ms, …) by index — the same table AnimEngine exposed, so
    /// `Clocks.Register(...)` + Drive callers are a drop-in. (The index-based SignalSource that retires the
    /// `List&lt;Func&lt;float&gt;&gt;` closure leak is the clean follow-up.)</summary>
    public DrivenClockTable Clocks { get; } = new();

    internal void ClearKeys(int slot) => _keysBySlot.Remove(slot);

    /// <summary>Multi-keyframe eased track (@keyframes). Offsets ascending in 0..1; per-segment easing.</summary>
    public void Keyframes(NodeHandle node, AnimChannel channel, Keyframe[] keys, float durationMs,
                          bool loop = false, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f,
                          bool displayRate = false)
    {
        int s = Get(node, channel, composite != CompositeOp.Replace);
        ref AnimValue r = ref _slab.At(s);
        r.Kind = GenKind.Keyframes;
        r.Gen = default;
        r.Gen.FromV = keys.Length > 0 ? keys[0].Value : 0f;
        r.Gen.DurationMs = durationMs;
        r.To = keys.Length > 0 ? keys[^1].Value : 0f;
        r.Position = r.Gen.FromV; r.Velocity = 0f; r.ElapsedMs = 0f;
        r.DelayRemainingMs = MathF.Max(0f, delayMs);
        r.Flags &= ~(AnimFlags.Done | AnimFlags.Driven);
        if (loop) r.Flags |= AnimFlags.Loop; else r.Flags &= ~AnimFlags.Loop;
        if (displayRate) r.Flags |= AnimFlags.DisplayRate; else r.Flags &= ~AnimFlags.DisplayRate;   // transient loop → display rate
        r.Flags |= AnimFlags.JustSeeded;   // seed frame holds the initial value (advance begins next frame)
        r.DrivenSrc = AnimValue.WallClock;
        _keysBySlot[s] = keys;
    }

    /// <summary>Scroll/value-driven track: progress = clamp01((source − domainMin)/(domainMax − domainMin)), source
    /// sampled from the DrivenClock <paramref name="drivenRef"/>; value = Sample(keys, progress).</summary>
    public void Drive(NodeHandle node, AnimChannel channel, Keyframe[] keys, int drivenRef, float domainMin, float domainMax,
                      CompositeOp composite = CompositeOp.Replace)
    {
        int s = Get(node, channel, composite != CompositeOp.Replace);
        ref AnimValue r = ref _slab.At(s);
        r.Kind = GenKind.Keyframes;
        r.Gen = default;
        r.Gen.FromV = domainMin;
        r.Gen.DurationMs = domainMax - domainMin;   // domain span
        r.To = keys.Length > 0 ? keys[^1].Value : 0f;
        r.ElapsedMs = 0f; r.DelayRemainingMs = 0f;
        r.Flags &= ~(AnimFlags.Done | AnimFlags.Loop);
        r.Flags |= AnimFlags.Driven;
        r.DrivenSrc = (ushort)drivenRef;
        _keysBySlot[s] = keys;
    }

    /// <summary>Advance a non-spring row (Eased two-point / Keyframes / Driven) to its value this tick; sets Done.
    /// Called from the Tick advance loop.</summary>
    private float AdvanceTimeline(int slot, ref AnimValue r)
    {
        float u;
        bool done = false;
        if (r.Has(AnimFlags.Driven))
        {
            float src = Clocks.Sample(r.DrivenSrc == AnimValue.WallClock ? -1 : r.DrivenSrc);
            float span = r.Gen.DurationMs;
            u = span <= 1e-6f ? 0f : (src - r.Gen.FromV) / span;
            u = u < 0f ? 0f : (u > 1f ? 1f : u);
        }
        else
        {
            float dur = r.Gen.DurationMs <= 0f ? 1f : r.Gen.DurationMs;
            u = r.ElapsedMs / dur;
            if (r.Has(AnimFlags.Loop)) u -= MathF.Floor(u);
            else if (u >= 1f) { u = 1f; done = true; }
            else if (u < 0f) u = 0f;
        }

        float val = _keysBySlot.TryGetValue(slot, out Keyframe[]? keys) && keys.Length >= 2
            ? Sample(keys, u)
            : r.Gen.FromV + (r.To - r.Gen.FromV) * Easings.Ease((Easing)(byte)r.Gen.EaseId, u);   // Animate two-point

        if (done) r.Flags |= AnimFlags.Done;
        return val;
    }

    // sample a multi-keyframe track at progress u (0..1), per-segment easing (ported from AnimEngine.Sample)
    private static float Sample(Keyframe[] keys, float u)
    {
        if (keys.Length == 0) return 0f;
        if (keys.Length == 1 || u <= keys[0].Offset) return keys[0].Value;
        if (u >= keys[^1].Offset) return keys[^1].Value;
        int i = 0;
        while (i < keys.Length - 1 && keys[i + 1].Offset < u) i++;
        Keyframe a = keys[i], b = keys[i + 1];
        float span = b.Offset - a.Offset;
        float local = span <= 0f ? 1f : (u - a.Offset) / span;
        return a.Value + (b.Value - a.Value) * Easings.Ease(b.Easing, local);
    }
}
