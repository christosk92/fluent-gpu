using System.Runtime.CompilerServices;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;

namespace FluentGpu.Hosting.Threading;

internal readonly record struct SceneRecordOptions(FocusVisualStyle Focus, TextEditStyle TextEdit,
    ColorF ScrollThumb, ColorF ScrollTrack, bool HoldSelfBlur, SpanReuseDisabledReason SpanDisable,
    ulong DamageEpoch, bool CollectSpanMisses);

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
        ulong lastCapturedSeq)
    {
        _popupCount = popups.Count;
        Grow(ref _popupRoots, _popupCount);
        for (int i = 0; i < _popupCount; i++) _popupRoots[i] = popups[i].Root;
        // P8 (threading-render-seam.md 3): refresh this slot's snapshot incrementally when the store can prove what
        // changed since the publication this slot last captured; otherwise copy the whole reachable tree. The
        // incremental path returns false rather than guessing, so the fallback is not an error path - it is the normal
        // answer on any frame that reconciled or laid out.
        var popupRoots = _popupRoots.AsSpan(0, _popupCount);
        if (!Scene.CaptureIncremental(source, popupRoots, lastCapturedSeq)) Scene.Capture(source, popupRoots);
        Scene.Recording.CopyConfigurationFrom(source.Recording);
        Scene.Recording.RetainConfigurationStrings(Scene, strings);
        // Only images the recorder can draw: the captured nodes' references plus the detached slab's (folded in below).
        Images.Capture(images, Scene.ReferencedImageIds);
        animation.CaptureCompositorAnimations(Animations,
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        // The compositor overlay is a bounded ROW POOL, not a per-node column, and it is sized HERE - on the publisher
        // side, while this slot is exclusively owned, and only once the animation descriptions exist. The renderer
        // adopts this slot's Animations against this slot's Scene, so the distinct nodes named above are exactly the
        // rows a tick can ask for: the render thread never grows the pool and never allocates.
        // See SceneRecordingSnapshot.Animation.cs for the fallback if a caller ever drives a tick past the reserve.
        Scene.ReserveCompositorRows(Animations.DistinctNodeCount);
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
            if (target.FirstSceneSequence == 0) target.FirstSceneSequence = sequence;
            var origin = target.WindowBoundsDip.IsEmpty ? target.BoundsDip : target.WindowBoundsDip;
            // Placed = the overlay has given this popup a real rect. An UNPLACED popup has nothing to paint, and
            // presenting it anyway would latch its "content has presented" reveal evidence on a 1×1 (creation-size)
            // surface — a revealed popup window with nothing in it.
            bool placed = !target.WindowBoundsDip.IsEmpty || !target.BoundsDip.IsEmpty;
            _popups[i] = new(target.Root, new(origin.X, origin.Y), target.Swapchain, target.Window,
                target.DrawList, target.Recording, target.Swapchain?.SizePx ?? default, placed);
        }
        _popups.AsSpan(_popupCount).Clear();
    }

    /// <summary>DIAGNOSTIC ONLY: the consumer skipped at least one publication before the one this frame carries.
    /// Deliberately NOT an input to recording. Reuse validity is decided by the snapshot's own record-dirty bits, its
    /// removal ledger and the recorder's gates; the host holds all three open until the renderer has consumed a
    /// publication, so a snapshot published across a gap carries their UNION and is exactly as trustworthy as one
    /// published after a consume. The span table is likewise safe: its freshness test (<c>_frame[i] == frameId - 1</c>)
    /// counts RECORD frames — advanced once per <see cref="Record"/>, on this thread — and a publication that was never
    /// recorded never advanced it. Treating a gap as a reuse killer meant a full re-record plus whole-root damage on
    /// exactly the frames where the renderer was already behind, which is the steady state under scroll.</summary>
    internal bool PublicationGap { get; private set; }

    internal SceneRecordStats Record(DrawList commands, SpanTable spans, bool publicationGap)
    {
        PublicationGap = publicationGap;
        var options = Options;
        var stats = Scene.Recording.Record(Scene, commands, Images, options.Focus, options.ScrollThumb, options.ScrollTrack,
            options.TextEdit, _skip.AsSpan(0, _skipCount), options.HoldSelfBlur, spans, options.SpanDisable,
            _damage.AsSpan(0, _damageCount), options.DamageEpoch,
            _reuseBlock.AsSpan(0, _reuseBlockCount), options.CollectSpanMisses);
        Scene.Recording.RecordDetachedNodes(commands, Images, _detached.AsSpan(0, _detachedCount), Scene.OverlayClip);
        return stats;
    }

    internal void RecordPopups(IGpuDevice device, float scale, float imageClockMs)
    {
        for (int i = 0; i < _popupCount; i++)
        {
            ref readonly var popup = ref _popups[i];
            if (popup.Swapchain is not { } swapchain || popup.Root.IsNull || !Scene.IsLive(popup.Root)) continue;
            if (!popup.Placed) continue;   // no placement yet ⇒ nothing to paint (and nothing to reveal on)
            popup.Recording.CopyConfigurationFrom(Scene.Recording);
            popup.Recording.RecordSubtree(Scene, popup.Commands, Images, Options.Focus, Options.ScrollThumb,
                Options.ScrollTrack, Options.TextEdit, popup.Root, popup.Origin);
            device.SubmitDrawList(popup.Commands.Bytes, popup.Commands.SortKeys,
                new FrameInfo(popup.Size, scale, ColorF.Transparent) { ImageClockMs = imageClockMs }, swapchain);
            swapchain.Present();
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

    private readonly record struct PopupRecordingTarget(NodeHandle Root, Point2 Origin, ISwapchain? Swapchain,
        IPlatformPopupWindow Window, DrawList Commands, SceneRecordingContext Recording, Size2 Size, bool Placed);

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
