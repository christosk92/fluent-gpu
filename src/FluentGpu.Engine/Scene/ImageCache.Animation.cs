using FluentGpu.Foundation;
using FluentGpu.Media;

namespace FluentGpu.Scene;

/// <summary>
/// Animated images (<c>ImageEl.Animate</c>), the UI half. The entry's ordinary decode lands first (frame 0, the static
/// path, unchanged); from then on, while a node showing the entry is in view, a <see cref="AnimatedFrameSource"/> decodes the
/// following frames on its own worker and this half shows each one when it falls due: the frame's pixels are uploaded under
/// the SAME image id (the texture store takes a fresh placement and retires the old one behind the frame fence, so a frame
/// being sampled is never overwritten) and the id is noted as content-changed, which repaints only the nodes that show it
/// (the E1 per-node band). Nothing else in the page re-records.
/// <para><b>The clock.</b> Due times are the running sum of the frames' own durations on the image clock, so a file at
/// 17/17/16 ms plays at an even 60 fps with no drift. A frame is shown on the first UI frame at or after its due time minus
/// <see cref="AnimSlackMs"/>; the host's wait is shaped to wake for that time (<see cref="AnimationDueMs"/>) and a frame
/// still decoding wakes the host itself when it lands, so a playing image costs one UI turn per animation frame and no
/// polling. Shown later than <see cref="AnimLateToleranceMs"/> after its due time counts as late (the summary lines say how
/// many). After a gap with no ticks (the window hidden or parked) the timeline re-bases instead of hurrying to catch up.</para>
/// <para><b>When it plays.</b> Only while a node showing the id is in view this frame (the reconciler names them through
/// <see cref="NoteAnimationShown"/>: live, shown, inside every clipping ancestor and the window) and the entry is Ready and
/// pinned. Off screen, minimised or not painting, nothing is due and the host is not woken; after
/// <see cref="AnimatedFrameSource.ReleaseAfterMs"/> the source frees its ring, decoder and bytes. Reduced motion
/// (<c>Motion.ReducedMotion</c>) shows the first frame and never plays: a playing image switched to reduced motion rewinds to
/// frame 0 and stops. A file the codec does not play (a still, one frame, a GIF, no WebP decoder) never starts: the
/// static path's frame is all there is.</para>
/// </summary>
public sealed partial class ImageCache
{
    /// <summary>A frame may show this much before its due time (it presents at the next refresh, which is later).</summary>
    public const float AnimSlackMs = 4f;
    /// <summary>A frame shown more than this after its due time missed its refresh: counted late.</summary>
    public const float AnimLateToleranceMs = 8f;
    /// <summary>A tick gap longer than this is a pause (hidden, parked, not painting): the timeline re-bases.</summary>
    private const float AnimGapMs = 250f;

    private sealed class AnimPlayback
    {
        public AnimatedFrameSource? Source;
        public bool Shown;            // a node showing the id is in view THIS frame (NoteAnimationShown)
        public bool Playing;          // the clock is armed
        public bool Static;           // the codec does not play this file (or an upload failed): never retried for this entry
        public bool Rewinding;        // reduced motion while playing: show the reopened source's frame 0, then stop
        public float NextDueMs = float.NaN;
        public float LastTickMs = float.NegativeInfinity;
        public int ShownIndex = -1;   // the frame the texture holds (-1 = the static path's frame 0)
        public int Shown_, OnTime, Late;
        public int LateDecode, LateTick;   // late because the frame was not decoded at its due time / because no UI frame ran in time
        public bool WaitedOnDecode;        // this due frame found the ring empty at least once
        public float MaxLateMs;
        public float PlayStartMs;
    }

    private readonly Dictionary<int, AnimPlayback> _anims = new();
    private readonly List<int> _animRemove = new();

    /// <summary>Animated entries the cache tracks (diagnostics).</summary>
    public int AnimationCount => _anims.Count;

    /// <summary>UI thread, per frame, before <see cref="TickAnimations"/>: a node that asked <paramref name="id"/> to animate is
    /// in view this frame. The first sighting of an id allocates its playback row (once); every later one is a flag write.</summary>
    internal void NoteAnimationShown(int id)
    {
        if (id == 0) return;
        if (!_anims.TryGetValue(id, out var p))
        {
            if (!_byId.TryGetValue(id, out var e) || e.Derived) return;
            p = new AnimPlayback();
            _anims[id] = p;
        }
        p.Shown = true;
    }

    /// <summary>Image-clock time the next animation frame is due (minus <see cref="AnimSlackMs"/>), +∞ when nothing is playing
    /// or every playing image waits on its decode (whose landing wakes the host itself). The host turns it into a wake.</summary>
    internal float AnimationDueMs
    {
        get
        {
            if (_anims.Count == 0) return float.PositiveInfinity;
            float min = float.PositiveInfinity;
            foreach (var p in _anims.Values)
            {
                if (!p.Playing || p.Source is not { } s || s.Buffered == 0) continue;
                float due = float.IsNaN(p.NextDueMs) ? _clockMs : p.NextDueMs - AnimSlackMs;
                if (due < min) min = due;
            }
            return min;
        }
    }

    /// <summary>UI thread, once per painted frame after <see cref="Pump()"/>: arm, run or pause every animated entry from the
    /// sightings noted this frame (see the class remarks). Steady playback is allocation-free: the ring hands a frame over,
    /// the upload path takes the buffer as a loan and the id is noted for the per-node repaint.</summary>
    internal void TickAnimations(bool reducedMotion)
    {
        if (_anims.Count == 0) return;
        foreach (var pair in _anims)
        {
            int id = pair.Key;
            var p = pair.Value;
            bool shown = p.Shown;
            p.Shown = false;
            if (!_byId.TryGetValue(id, out var e) || e.Refs <= 0)
            {
                // Nothing holds the id any more (unmounted, evicted): the row goes with it.
                PauseAnimation(id, p, "released");
                p.Source?.Dispose();
                p.Source = null;
                _animRemove.Add(id);
                continue;
            }
            if (!shown || e.State != ImageState.Ready || p.Static || _decoder is not DecodeScheduler scheduler)
            {
                PauseAnimation(id, p, !shown ? "hidden" : "not-ready");
                continue;
            }
            if (reducedMotion && !p.Rewinding)
            {
                if (p.ShownIndex <= 0) { PauseAnimation(id, p, "reduced-motion"); continue; }
                // Playing when reduced motion came on: reopen and show frame 0, then stop (the first frame is the image).
                PauseAnimation(id, p, "reduced-motion");
                p.Source?.Dispose();
                p.Source = null;
                p.Rewinding = true;
            }

            if (p.Source is null || p.Source.State == AnimatedFrameSource.SourceState.Released)
            {
                p.Source?.Dispose();
                p.Source = SourceOf(new ImageHandle(id)) is { Length: > 0 } src ? scheduler.OpenAnimation(src) : null;
                if (p.Source is null) { p.Static = true; continue; }
                // A reopened source starts at frame 0 again: the timeline restarts with it.
                p.NextDueMs = float.NaN;
                p.ShownIndex = -1;
            }
            var source = p.Source;
            switch (source.State)
            {
                case AnimatedFrameSource.SourceState.NotAnimated:
                case AnimatedFrameSource.SourceState.Failed:
                    // A still, a one-frame file, a GIF, a WebP the machine cannot decode: the static frame is the image.
                    Diag.Line($"[image.anim] static id={id} src={AnimatedFrameSource.TailOf(source.Source)} reason={source.State}");
                    source.Dispose();
                    p.Source = null;
                    p.Static = true;
                    p.Rewinding = false;
                    continue;
            }
            source.Touch();
            if (!p.Playing)
            {
                p.Playing = true;
                p.PlayStartMs = _clockMs;
                // Resuming: the next frame is due now, never "overdue since we left".
                if (!float.IsNaN(p.NextDueMs)) p.NextDueMs = _clockMs;
            }
            else if (_clockMs - p.LastTickMs > AnimGapMs && !float.IsNaN(p.NextDueMs) && p.NextDueMs < _clockMs)
                p.NextDueMs = _clockMs;   // no ticks for a while (the window stopped painting): re-base, do not count it late
            p.LastTickMs = _clockMs;

            float due = float.IsNaN(p.NextDueMs) ? _clockMs : p.NextDueMs;
            if (_clockMs + AnimSlackMs < due) continue;                 // not due yet
            if (!source.TryTake(out var frame)) { p.WaitedOnDecode = true; continue; }   // still decoding: its landing wakes the host

            if (!ShowAnimationFrame(id, e, scheduler, in frame))
            {
                Diag.Line($"[image.anim] upload refused id={id}: playback stops on the frame shown");
                PauseAnimation(id, p, "upload-refused");
                source.Dispose();
                p.Source = null;
                p.Static = true;
                continue;
            }
            float lateBy = _clockMs - due;
            if (!float.IsNaN(p.NextDueMs))
            {
                if (lateBy > AnimLateToleranceMs)
                {
                    p.Late++;
                    if (p.WaitedOnDecode) p.LateDecode++; else p.LateTick++;
                }
                else p.OnTime++;
                if (lateBy > p.MaxLateMs) p.MaxLateMs = lateBy;
            }
            p.WaitedOnDecode = false;
            p.Shown_++;
            p.ShownIndex = frame.Index;
            // The running sum keeps an even cadence; only a frame later than its own duration re-bases (no burst to catch up).
            p.NextDueMs = lateBy > frame.DurationMs ? _clockMs + frame.DurationMs : due + frame.DurationMs;
            source.UiShown = p.Shown_; source.UiOnTime = p.OnTime; source.UiLate = p.Late; source.UiLateDecode = p.LateDecode; source.UiLateTick = p.LateTick; source.UiMaxLateCs = (int)(p.MaxLateMs * 100f);

            if (p.Rewinding)
            {
                // Reduced motion: frame 0 is up; stop here until motion is allowed again.
                p.Rewinding = false;
                PauseAnimation(id, p, "reduced-motion");
                source.Dispose();
                p.Source = null;
                p.ShownIndex = 0;
            }
        }
        if (_animRemove.Count > 0)
        {
            foreach (int id in _animRemove) _anims.Remove(id);
            _animRemove.Clear();
        }
    }

    /// <summary>Upload one frame under <paramref name="id"/>: the frame's pool block is LOANED to the sink (the async render
    /// thread takes it and returns it to the pool after staging; a synchronous sink copies it and the block goes back here).</summary>
    private bool ShowAnimationFrame(int id, Entry e, DecodeScheduler scheduler, in AnimatedFrameSource.Frame frame)
    {
        ImageUploadResult result;
        bool taken;
        DecodeScheduler.BeginLoan(frame.Block, frame.ByteLen);
        try { result = UploadPixels(id, frame.Block.AsSpan(0, frame.ByteLen), frame.W, frame.H); }
        finally { taken = DecodeScheduler.EndLoan(); }
        if (!taken) scheduler.PixelPool.Return(frame.Block);
        if (result != ImageUploadResult.Accepted) return false;
        // New pixels under the same id leave the node's draw bytes unchanged, and the retained tiles re-raster only what the
        // bytes (or a live cross-fade window) say changed. So each shown frame restamps the entry's reveal to a zero-length fade
        // at this frame's clock: the image's DrawImage carries a new FadeStartMs (its alpha still resolves to 1: a zero
        // duration is no fade), the slice diff re-rasters exactly that op's footprint, and nothing else in the page moves.
        // An authored reveal still running (the first frame fading in over its placeholder) is left to finish: its live fade
        // window already invalidates the rect every frame.
        if (float.IsNaN(e.TextureMs) || _clockMs >= e.TextureMs + e.RevealMs)
        {
            e.TextureMs = _clockMs;
            e.RevealMs = 0f;
        }
        NoteContentChanged(id);
        return true;
    }

    private void PauseAnimation(int id, AnimPlayback p, string reason)
    {
        if (!p.Playing) return;
        p.Playing = false;
        var s = p.Source;
        long ring = s is null ? 0 : (long)AnimatedFrameSource.Depth * s.Width * s.Height * 4;
        float avgDecode = s is null || s.Decoded == 0 ? 0f : s.DecodeSumCs / 100f / s.Decoded;
        Diag.Line($"[image.anim] pause id={id} reason={reason} playedMs={_clockMs - p.PlayStartMs:F0} shown={p.Shown_} onTime={p.OnTime} " +
            $"late={p.Late} (decode={p.LateDecode} tick={p.LateTick}) maxLateMs={p.MaxLateMs:F1} decodeAvgMs={avgDecode:F2} decodeMaxMs={(s?.DecodeMaxCs ?? 0) / 100f:F2} " +
            $"decodeOver16ms={s?.DecodeOverBudget ?? 0} ringBytes={ring} texBytes={(s is null ? 0 : CommittedBytesFor(s.Width, s.Height))}");
    }
}
