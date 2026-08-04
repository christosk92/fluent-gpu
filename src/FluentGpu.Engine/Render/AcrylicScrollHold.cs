namespace FluentGpu.Render;

/// <summary>
/// The scroll-cadence half of the acrylic retained-backdrop cache (design/subsystems/backdrop-effects-animation.md
/// §2.3 / E10) — portable + headless-gated, so the D3D12 leaf never owns the decision.
///
/// <para>The problem: a scrolling page emits damage EVERY frame, and that damage legitimately overlaps a chrome
/// acrylic's tight damage-test region, so <see cref="AcrylicBackdropMath.BackdropReusable"/> misses on every scroll
/// frame and the compositor re-runs its whole pipeline (backdrop snapshot + dual-Kawase chain + composite) at frame
/// rate. Measured on the Wavee scroll path that is the dominant component of a ~5 ms composite pass.</para>
///
/// <para>The fix is the SAME lever the self-blur groups already pull (`SceneRecorder`'s <c>holdBlur</c>, driven by
/// AppHost's ~0.12 s <c>SelfBlurHold</c> window): while a user scroll is active, a layer that ALREADY HAS a retained
/// blurred snapshot stretches it across a few frames instead of re-blurring, refreshing on every
/// <see cref="ScrollRefreshCadence"/>-th frame (30 Hz at 120 Hz, 15 Hz at 60 Hz). A heavily blurred backdrop is
/// low-frequency by construction — the blur destroys exactly the high-frequency detail that would make ≤3 frames of
/// lag legible under fast-moving content — which is why WinUI likewise decouples acrylic refresh from frame rate.</para>
///
/// <para><b>Never shows garbage.</b> The hold only ever extends the life of an EXISTING snapshot of the SAME geometry:
/// no retained snapshot (first frame, post-resize, <c>LayerId == 0</c>) ⇒ blur immediately; a changed stamp
/// (rect / sigma / scale / canvas / backdrop-source / clip) ⇒ blur immediately, because that snapshot was blurred for a
/// different rect and reusing it would MISPLACE the frost rather than merely date it. There is no fallback tint path.</para>
///
/// <para><b>Healing.</b> When the hold releases, the entry's held-frame counter is still nonzero, and the compositor
/// treats a nonzero counter as "this snapshot is known-stale" — so the first non-hold frame runs a full refresh even
/// though nothing moved that frame (the plain damage test would otherwise report a clean HIT and freeze the stale
/// snapshot in place until the next unrelated damage). After that refresh the counter is 0 and ordinary damage-driven
/// behavior resumes.</para>
/// </summary>
public static class AcrylicScrollHold
{
    /// <summary>Refresh every Nth frame while the scroll hold is live: 4 ⇒ 30 Hz at 120 Hz (≤25 ms of backdrop lag),
    /// 15 Hz at 60 Hz (≤50 ms). 1 disables the hold entirely (every frame refreshes).</summary>
    public const int ScrollRefreshCadence = 4;

    /// <summary>
    /// Must this acrylic layer re-run the full snapshot+blur pipeline this frame?
    ///
    /// <para><b>Precondition:</b> the caller asks only when the cheap path is unavailable — i.e. the plain
    /// <see cref="AcrylicBackdropMath.BackdropReusable"/> test MISSED this frame, or the retained entry is already
    /// marked stale by a previous hold (<paramref name="framesHeld"/> &gt; 0). A clean, never-held entry composites its
    /// snapshot without consulting this at all.</para>
    ///
    /// <para>Truth table:
    /// <list type="bullet">
    /// <item>no retained snapshot ⇒ <see langword="true"/> (blur now — the hold can only stretch an existing one)</item>
    /// <item>stamp changed ⇒ <see langword="true"/> (the snapshot belongs to a different rect/sigma/scale/source)</item>
    /// <item>scroll hold released ⇒ <see langword="true"/> (damage-driven again; also the heal of a stale entry)</item>
    /// <item>hold + retained + same stamp ⇒ <see langword="true"/> only on every <paramref name="cadence"/>-th frame</item>
    /// </list></para>
    /// </summary>
    /// <param name="scrollHold">The frame's user-scroll hold (AppHost's SelfBlurHold window: any user scroll this frame,
    /// plus a ~0.12 s tail), carried to the backend on <c>FrameInfo.ScrollHold</c> — a property of the PUBLISHED frame.</param>
    /// <param name="hasRetained">This layer has a live retained (pinned) blurred snapshot.</param>
    /// <param name="stampUnchanged">The retained snapshot's <see cref="AcrylicBackdropMath.BackdropStamp"/> equals this
    /// frame's — same rect (quantized), sigma, scale, canvas, backdrop source and clip.</param>
    /// <param name="framesHeld">Consecutive frames this entry has already been composited stale (0 = fresh).</param>
    /// <param name="cadence">Refresh period in frames; ≤1 means "never hold".</param>
    public static bool ShouldRefresh(bool scrollHold, bool hasRetained, bool stampUnchanged, int framesHeld, int cadence)
    {
        if (!hasRetained) return true;
        if (!stampUnchanged) return true;
        if (!scrollHold) return true;
        if (cadence <= 1) return true;
        return framesHeld + 1 >= cadence;
    }
}
