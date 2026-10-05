using System.Runtime.CompilerServices;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;

namespace FluentGpu.Hosting.Threading;

internal readonly record struct SceneRecordOptions(FocusVisualStyle Focus, TextEditStyle TextEdit,
    ColorF ScrollThumb, ColorF ScrollTrack, SpanReuseDisabledReason SpanDisable,
    bool CollectSpanMisses, int ThemeEpoch = 0);

/// <summary>All recording inputs for one publication. The publisher slot owns every mutable buffer.</summary>
internal sealed class SceneRenderFrame
{
    internal SceneRecordingSnapshot Scene { get; } = new();
    internal ImageRecordingSnapshot Images { get; } = new();
    internal CompositorAnimationSnapshot Animations { get; } = new();
    internal SceneRecordOptions Options;
    private NodeHandle[] _skip = [], _reuseBlock = [];
    private RectF[] _damage = [];
    private DetachedNode[] _detached = [];
    private PopupRecordingTarget[] _popups = [];
    private NodeHandle[] _popupRoots = [];
    private int _skipCount, _reuseBlockCount, _damageCount, _detachedCount, _popupCount;

    internal void Capture(SceneStore source, ImageCache images, StringTable strings, in SceneRecordOptions options,
        ReadOnlySpan<NodeHandle> skip, ReadOnlySpan<NodeHandle> reuseBlock, ReadOnlySpan<RectF> damage,
        DetachedAnimSlab detached, IReadOnlyList<PopupWindowSlot> popups, AnimEngine animation, ulong sequence,
        ulong lastCapturedSeq, bool preflightOnly = false)
    {
        _popupCount = popups.Count;
        Grow(ref _popupRoots, _popupCount);
        for (int i = 0; i < _popupCount; i++) _popupRoots[i] = popups[i].Root;
        // P8 (threading-render-seam.md 3): refresh this slot's snapshot incrementally when the store can prove what
        // changed since the publication this slot last captured; otherwise copy the whole reachable tree. The
        // incremental path returns false rather than guessing, so the fallback is not an error path - it is the normal
        // answer on any frame that reconciled or laid out.
        var popupRoots = _popupRoots.AsSpan(0, _popupCount);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Scene.CaptureIncremental(source, popupRoots, lastCapturedSeq)) Scene.Capture(source, popupRoots);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Scene.Recording.CopyConfigurationFrom(source.Recording);
        Scene.Recording.RetainConfigurationStrings(Scene, strings);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        // Only images the recorder can draw: the captured nodes' references plus the detached slab's (folded in below).
        Images.Capture(images, Scene.ReferencedImageIds);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        animation.CaptureCompositorAnimations(Animations,
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        DiagSceneMs = (t1 - t0) * tick; DiagConfigMs = (t2 - t1) * tick; DiagImagesMs = (t3 - t2) * tick; DiagAnimMs = (t4 - t3) * tick;
        // The compositor overlay is a bounded ROW POOL, not a per-node column, and it is sized HERE - on the publisher
        // side, while this slot is exclusively owned, and only once the animation descriptions exist. The renderer
        // adopts this slot's Animations against this slot's Scene, so the distinct nodes named above are exactly the
        // rows a tick can ask for: the render thread never grows the pool and never allocates.
        // See SceneRecordingSnapshot.Animation.cs for the fallback if a caller ever drives a tick past the reserve.
        Scene.ReserveCompositorRows(Animations.DistinctNodeCount);
        // Scroll coverage (scroll rework §5): one row per viewport the UI realized content for + its effect rows, copied
        // from the host's UI-side table so the render poser adopts exactly what this publication's rows cover.
        Scene.ScrollCoverage.Clear();
        source.CaptureScrollCoverage?.Invoke(Scene.ScrollCoverage);
        Options = options;
        Copy(skip, ref _skip, out _skipCount);
        Copy(reuseBlock, ref _reuseBlock, out _reuseBlockCount);
        Copy(damage, ref _damage, out _damageCount);
        _detachedCount = detached.NodeCount;
        Grow(ref _detached, _detachedCount);
        for (int i = 0; i < _detachedCount; i++)
        {
            _detached[i] = detached.At(i);
            if (_detached[i].InUse && (VisualKind)_detached[i].Kind == VisualKind.Image && _detached[i].ImageId != 0)
                Images.AddReferenced(images, [_detached[i].ImageId]);
        }
        Grow(ref _popups, _popupCount);
        for (int i = 0; i < _popupCount; i++)
        {
            var target = popups[i];
            if (!preflightOnly && target.FirstSceneSequence == 0) target.FirstSceneSequence = sequence;
            var origin = target.WindowBoundsDip.IsEmpty ? target.BoundsDip : target.WindowBoundsDip;
            // Placed = the overlay has given this popup a real rect. An UNPLACED popup has nothing to paint, and
            // presenting it anyway would latch its "content has presented" reveal evidence on a 1×1 (creation-size)
            // surface — a revealed popup window with nothing in it.
            bool placed = !target.WindowBoundsDip.IsEmpty || !target.BoundsDip.IsEmpty;
            // The SLOT travels, not its swapchain: under a render thread the swapchain is created (and released) by that thread
            // through the popup mailbox, so it may not exist yet when this publication is captured. RecordPopups resolves it
            // (and its size) on the render thread when it records; the slot's Lifecycle takes the per-pass timing there too.
            _popups[i] = new(target.Root, new(origin.X, origin.Y), target, target.Window,
                target.DrawList, target.Recording, placed);
        }
        _popups.AsSpan(_popupCount).Clear();
    }

    /// <summary>DIAGNOSTIC ONLY: the consumer skipped at least one publication before the one this frame carries.
    /// Deliberately NOT an input to recording. Reuse validity is decided by the snapshot's own record-dirty bits, its
    /// removal ledger and the recorder's gates; the host holds all three open until the renderer has consumed a
    /// publication, so a snapshot published across a gap carries their UNION and is exactly as trustworthy as one
    /// published after a consume. The span table is likewise safe: a span is copyable only out of the arena buffer
    /// generation it was written into (<c>SpanTable.TryGet</c>) — advanced by the render thread's own record passes — and
    /// a publication that was never recorded never advanced it. Treating a gap as a reuse killer meant a full re-record plus whole-root damage on
    /// exactly the frames where the renderer was already behind, which is the steady state under scroll.</summary>
    internal bool PublicationGap { get; private set; }

    /// <summary>Always-on split of the last <see cref="Capture"/> (UI thread, ms): the scene snapshot, the recording
    /// configuration + retained strings, the image-cache snapshot, the compositor-animation capture. Surfaced through
    /// <c>FrameStats</c> so a slow `capture` names its own step.</summary>
    internal double DiagSceneMs, DiagConfigMs, DiagImagesMs, DiagAnimMs;

    /// <summary>Record this publication into the render thread's slice arenas and lay out its composite plan (the scene
    /// turn of the three-way render turn). <paramref name="commands"/> is left empty — the slices ARE the frame.
    /// <para><paramref name="slices"/> null = a STANDALONE record (a detached child, which presents through its own swapchain's
    /// direct route): no slice is cut, every pose is baked, and the whole frame lands in <paramref name="commands"/> as ONE
    /// stream for <c>SubmitDrawList</c>.</para></summary>
    internal SceneRecordStats Record(DrawList commands, SpanTable spans, SliceRecorder? slices, bool publicationGap)
    {
        PublicationGap = publicationGap;
        var options = Options;
        return Scene.Recording.Record(Scene, commands, Images, options.Focus, options.ScrollThumb, options.ScrollTrack,
            options.TextEdit, _skip.AsSpan(0, _skipCount), spans, options.SpanDisable,
            _damage.AsSpan(0, _damageCount),
            _reuseBlock.AsSpan(0, _reuseBlockCount), options.CollectSpanMisses,
            slices, _detached.AsSpan(0, _detachedCount));
    }

    /// <summary>The composite-only turn of the three-way render turn: only slice poses moved, so the retained slices are
    /// re-placed at this snapshot's current poses without recording a byte.</summary>
    internal RepaintDamageRegion Compose(SliceRecorder slices)
        => Scene.Recording.Compose(Scene, slices);

    internal void RecordPopups(IGpuDevice device, float scale, float imageClockMs)
    {
        for (int i = 0; i < _popupCount; i++)
        {
            ref readonly var popup = ref _popups[i];
            if (popup.Slot.Swapchain is not { } swapchain || popup.Root.IsNull || !Scene.IsLive(popup.Root)) continue;   // null: not created yet / already released
            if (!popup.Placed) continue;   // no placement yet ⇒ nothing to paint (and nothing to reveal on)
            long passStart = System.Diagnostics.Stopwatch.GetTimestamp();
            popup.Recording.CopyConfigurationFrom(Scene.Recording);
            popup.Recording.RecordSubtree(Scene, popup.Commands, Images, Options.Focus, Options.ScrollThumb,
                Options.ScrollTrack, Options.TextEdit, popup.Root, popup.Origin);
            device.SubmitDrawList(popup.Commands.Bytes, popup.Commands.SortKeys,
                new FrameInfo(swapchain.SizePx, scale, ColorF.Transparent) { ImageClockMs = imageClockMs }, swapchain);
            swapchain.Present();
            popup.Slot.Lifecycle.NoteTurn(passStart, System.Diagnostics.Stopwatch.GetTimestamp(), swapchain.HasPresentedContent);
            // The open motion starts on the frame the popup's content is actually ON its composition surface — the
            // swapchain's own report, not the fact that Present() was called: a backend stands down for a covered /
            // hidden target and presents nothing, and a popup HWND is hidden precisely until this first frame lands.
            // Latching this on the CALL played the reveal over an empty surface (the "empty flyout") and left the real
            // content to whatever later frame happened to arrive.
            if (!popup.Recording.PopupPresented && swapchain.HasPresentedContent)
            {
                swapchain.AnimatePopupOpen();
                popup.Recording.PopupPresented = true;
            }
            // Window show/visibility belongs to the PAL message thread and is handled from UI feedback.
        }
    }

    internal void ReleaseResources() => Scene.ReleaseResources();

    /// <summary>
    /// Cold UI preparation, only under the owning publisher's Writing claim. Two full captures warm both alternating
    /// captured/string buffers and all current sparse/image/animation reserves. They are NOT publications and must
    /// not activate a popup or advance any mailbox/store sequence. Old pins remain held until this succeeds.
    /// </summary>
    internal SceneRenderFrame PrepareCapacityReplacement(SceneStore source, ImageCache images, StringTable strings,
        DetachedAnimSlab detached, IReadOnlyList<PopupWindowSlot> popups, AnimEngine animation, int nodeCapacity)
    {
        var replacement = new SceneRenderFrame();
        try
        {
            replacement.Scene.ReserveNodeCapacity(nodeCapacity);
            for (int pass = 0; pass < 2; pass++)
                replacement.Capture(source, images, strings, Options,
                    _skip.AsSpan(0, _skipCount), _reuseBlock.AsSpan(0, _reuseBlockCount), _damage.AsSpan(0, _damageCount),
                    detached, popups, animation, source.PublishSeq + 1, lastCapturedSeq: 0, preflightOnly: true);
            return replacement;
        }
        catch
        {
            replacement.ReleaseResources();
            throw;
        }
    }

    private readonly record struct PopupRecordingTarget(NodeHandle Root, Point2 Origin, PopupWindowSlot Slot,
        IPlatformPopupWindow Window, DrawList Commands, SceneRecordingContext Recording, bool Placed);

    private static void Copy<T>(ReadOnlySpan<T> source, ref T[] destination, out int count)
    {
        count = source.Length; Grow(ref destination, count); source.CopyTo(destination);
    }

    private static void Grow<T>(ref T[] buffer, int count)
    {
        if (buffer.Length < count) Array.Resize(ref buffer, Math.Max(count, Math.Max(4, buffer.Length * 2)));
    }
}

[InlineArray(8)]
internal struct RecordingVideoRects { private RectF _element0; }

/// <summary>Value-only reverse-mailbox payload. Never aliases render-owned arrays.</summary>
internal struct RecordingFeedback
{
    internal ulong SceneSequence;
    internal SceneRecordStats Stats;
    internal int VideoCount;
    internal int PoseCount;
    internal int CommandCount;
    internal double RecordMs;
    internal RecordingVideoRects VideoRects;
}
