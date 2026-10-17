using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

// E20 (technical-plan §4, EP20; Classic tuning F10) - the complete live theme cross-fade. A light/dark or accent change on the
// live path (no freeze) re-runs every token reader (RethemeAll) inside the host's armed window (SetThemeTransition), and EVERY
// colour consumer the re-run changes joins one fade on one timeline:
//   • re-rendered static colours (fill, border, text) - the implicit BrushTransition path, overridden to the theme duration;
//   • bound colour channels (Fill/BorderColor/Color = () => Tok.X, an icon layer's tint, an image's placeholder, a list row's
//     fill) - they used to snap, because their effects wrote paint directly;
//   • the static colours the old path skipped: span-text foregrounds, icon tints, image placeholders, list-row fills, and a
//     gradient fill or gradient border (the elevation border every button carries);
//   • a parked page (KeepAlive, a parked Flow.Persist subtree, a pooled bound row) that comes back while the fade runs: its owed
//     re-render and the bound fades its channels seeded while parked join the fade at the point the shared timeline has reached,
//     so it is at the same lerp as everything else instead of snapping (or starting a fresh 250 ms fade) on arrival.
// Nothing else changes: outside the window every channel keeps its own timing (bound channels snap, BrushTransitionMs as authored),
// a snap scope (the theme transaction) lands every seed at its end value, and a recycled bound row (SuppressBoundTransitions) snaps.
public sealed partial class TreeReconciler
{
    // The live fade's shared timeline, on the animation clock (AnimEngine.ClockNowMs): where it started and how long it runs.
    private double _themeFadeStartMs = double.NaN;
    private float _themeFadeMs;
    // 0 for the nodes the theme flush itself re-runs; the progress already reached when a late joiner (an un-parked page) is armed.
    private float _themeFadeFromT;
    // Nodes whose bound colour fade was seeded while they were parked: the parked animation row does not advance, so the un-park
    // re-seeds it at the shared timeline's progress (SyncParkedThemeFade).
    private readonly HashSet<NodeHandle> _parkedThemeFades = new();

    /// <summary>The theme cross-fade window is armed (the host's RethemeAll flush, or a late joiner's catch-up render).</summary>
    private bool ThemeFadeArmed => !float.IsNaN(_themeTransitionMs) && _themeTransitionMs > 0f;

    /// <summary>A bound colour channel re-fired by the armed window cross-fades instead of snapping: never its mount run (nothing
    /// on screen to fade from) and never inside a recycled-row rebind (the row shows another item's colours).</summary>
    private bool BoundThemeFade(BindEffect fx) => fx.Started && ThemeFadeArmed && SuppressBoundTransitions == 0 && Anim is not null;

    /// <summary>The progress a seed made now starts at: the late joiner's, inside the armed window; 0 for an element's own
    /// BrushTransition outside it.</summary>
    private float BrushFadeFromT => ThemeFadeArmed ? _themeFadeFromT : 0f;

    /// <summary>Start the shared timeline (the host arms the window for a theme flush). A fade a still-parked node owes from the
    /// previous timeline lands first (nobody saw it), so a channel this flush fades again starts from the colour it now holds.</summary>
    private void BeginThemeFadeClock(float ms)
    {
        _themeFadeStartMs = Anim?.ClockNowMs ?? double.NaN;
        _themeFadeMs = ms;
        _themeFadeFromT = 0f;
        foreach (var node in _parkedThemeFades)
            if (_scene.IsLive(node)) _scene.SetBrushAnimT((int)node.Raw.Index, 1f);
        _parkedThemeFades.Clear();
    }

    /// <summary>Where the running live theme fade is: its progress 0..1 and the time it has left. False when none runs (or it is over).</summary>
    private bool TryThemeFadeProgress(out float progress, out float remainingMs)
    {
        progress = 1f; remainingMs = 0f;
        if (Anim is not { } anim || double.IsNaN(_themeFadeStartMs) || _themeFadeMs <= 0f) return false;
        double elapsed = anim.ClockNowMs - _themeFadeStartMs;
        if (elapsed >= _themeFadeMs) return false;
        progress = elapsed <= 0 ? 0f : (float)(elapsed / _themeFadeMs);
        remainingMs = (float)(_themeFadeMs - elapsed);
        return true;
    }

    /// <summary>Seed (or retarget) the colour cross-fade of the channels in <paramref name="bits"/>, BEFORE the caller writes their
    /// new values: each starts from the colour it displays now (mid-flight fades continue from where they are). A channel of a
    /// running fade that is not in <paramref name="bits"/> carries on from its displayed colour, so two channels of one node changed
    /// by separate writers (a re-render and a bound effect in the same flush) fade together instead of the later one dropping the
    /// other. <paramref name="gradientFrom"/>/<paramref name="borderBrushFrom"/> are the specs a gradient fill / gradient border fades
    /// from. Zero allocation (sparse columns, the unified engine's two-point track).</summary>
    private void SeedColorFade(NodeHandle node, byte bits, float ms, float fromT,
                               in GradientSpec gradientFrom = default, in GradientSpec borderBrushFrom = default)
    {
        ref NodePaint paint = ref _scene.Paint(node);
        var ba = new BrushAnim { DurationMs = ms, T = fromT };
        if (_scene.TryGetBrushAnim(node, out var prev))
        {
            if ((prev.Channels & BrushAnim.FillBit) != 0) { ba.FillFrom = ColorF.LerpLinear(prev.FillFrom, paint.Fill, prev.T); ba.Channels |= BrushAnim.FillBit; }
            if ((prev.Channels & BrushAnim.BorderBit) != 0) { ba.BorderFrom = ColorF.LerpLinear(prev.BorderFrom, paint.BorderColor, prev.T); ba.Channels |= BrushAnim.BorderBit; }
            if ((prev.Channels & BrushAnim.TextBit) != 0) { ba.TextFrom = ColorF.LerpLinear(prev.TextFrom, paint.TextColor, prev.T); ba.Channels |= BrushAnim.TextBit; }
            // A running gradient fade keeps its origin spec (a spec cannot be re-based without allocating one).
            ba.Channels |= (byte)(prev.Channels & (BrushAnim.GradientBit | BrushAnim.BorderBrushBit) & ~bits);
        }
        if ((bits & BrushAnim.FillBit) != 0 && (ba.Channels & BrushAnim.FillBit) == 0) { ba.FillFrom = paint.Fill; ba.Channels |= BrushAnim.FillBit; }
        if ((bits & BrushAnim.BorderBit) != 0 && (ba.Channels & BrushAnim.BorderBit) == 0) { ba.BorderFrom = paint.BorderColor; ba.Channels |= BrushAnim.BorderBit; }
        if ((bits & BrushAnim.TextBit) != 0 && (ba.Channels & BrushAnim.TextBit) == 0) { ba.TextFrom = paint.TextColor; ba.Channels |= BrushAnim.TextBit; }
        if ((bits & BrushAnim.GradientBit) != 0) { _scene.SetGradientFadeFrom(node, gradientFrom); ba.Channels |= BrushAnim.GradientBit; }
        if ((bits & BrushAnim.BorderBrushBit) != 0) { _scene.SetBorderBrushFadeFrom(node, borderBrushFrom); ba.Channels |= BrushAnim.BorderBrushBit; }
        _scene.SetBrushAnim(node, ba);
        _scene.Mark(node, NodeFlags.PaintDirty);
        Anim?.SeedBrushFade(node, ms, fromT);   // drive T via the unified engine (no separate ticker)
        // A parked node's row does not advance: its un-park puts it back on the shared timeline.
        if (ThemeFadeArmed && (_scene.Flags(node) & NodeFlags.Parked) != 0) _parkedThemeFades.Add(node);
    }

    /// <summary>A bound colour channel is about to write <paramref name="bit"/>: inside the armed theme window it cross-fades.</summary>
    private void BoundColorFade(BindEffect fx, NodeHandle node, byte bit)
    {
        if (BoundThemeFade(fx)) SeedColorFade(node, bit, _themeTransitionMs, _themeFadeFromT);
    }

    /// <summary>A static colour the implicit BrushTransition never covered (span text, icon tint, image placeholder, list-row fill)
    /// is about to change on a live node: inside the armed theme window it cross-fades.</summary>
    private void StaticThemeFade(NodeHandle node, bool isMount, byte bit)
    {
        if (!isMount && ThemeFadeArmed && Anim is not null) SeedColorFade(node, bit, _themeTransitionMs, _themeFadeFromT);
    }

    /// <summary>Un-park walk, per node: a bound fade seeded while parked re-joins the shared timeline at the progress it has reached
    /// (or lands, if the fade is over).</summary>
    private void SyncParkedThemeFade(NodeHandle node)
    {
        if (_parkedThemeFades.Count == 0 || !_parkedThemeFades.Remove(node)) return;
        if (!_scene.TryGetBrushAnim(node, out var ba) || Anim is not { } anim) return;
        if (TryThemeFadeProgress(out float progress, out float remainingMs))
        {
            ba.T = progress;
            _scene.SetBrushAnim(node, ba);
            anim.SeedBrushFade(node, remainingMs, progress);
        }
        else anim.SeedBrushFade(node, 1f, 1f);   // the fade ended while it was away: land on the new colours
        _scene.Mark(node, NodeFlags.PaintDirty);
    }

    /// <summary>A component that comes back from a park while the live theme fade runs, owing the re-render that carries the new
    /// theme: that render is a late joiner and fades from the progress the shared timeline has reached.</summary>
    private bool ThemeCatchUpDue(int parkEpoch) => parkEpoch != Tok.Epoch && TryThemeFadeProgress(out _, out _);

    /// <summary>Arm the window for a late joiner's render (<see cref="CompEntry.ThemeCatchUp"/>). Returns false (nothing armed)
    /// when the window is already armed (the theme flush itself) or the fade is over.</summary>
    private bool BeginThemeCatchUp(out float savedMs, out float savedFromT)
    {
        savedMs = _themeTransitionMs; savedFromT = _themeFadeFromT;
        if (ThemeFadeArmed || !TryThemeFadeProgress(out float progress, out float remainingMs)) return false;
        _themeTransitionMs = remainingMs;
        _themeFadeFromT = progress;
        return true;
    }

    private void EndThemeCatchUp(float savedMs, float savedFromT)
    {
        _themeTransitionMs = savedMs;
        _themeFadeFromT = savedFromT;
    }

    /// <summary>Two gradient specs paint the same thing (shape, axis and every stop) - a re-render that rebuilt an equal spec is no
    /// colour change.</summary>
    private static bool SameGradient(in GradientSpec a, in GradientSpec b)
    {
        if (ReferenceEquals(a.Stops, b.Stops)) return a.Equals(b);
        if (a.Shape != b.Shape || a.AngleDeg != b.AngleDeg || a.AxisLengthPx != b.AxisLengthPx || a.AnchorEnd != b.AnchorEnd
            || !a.RadialCenter.Equals(b.RadialCenter) || !a.RadialRadius.Equals(b.RadialRadius)) return false;
        var sa = a.Stops; var sb = b.Stops;
        if (sa is null || sb is null) return sa is null && sb is null;
        if (sa.Length != sb.Length) return false;
        for (int i = 0; i < sa.Length; i++)
            if (sa[i].Offset != sb[i].Offset || sa[i].Color != sb[i].Color) return false;
        return true;
    }
}
