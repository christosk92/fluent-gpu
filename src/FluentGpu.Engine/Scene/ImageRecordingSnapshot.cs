using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>Value-only image readiness, dimensions and reveal inputs for one claimed scene publication.</summary>
public sealed class ImageRecordingSnapshot
{
    private readonly Dictionary<int, Entry> _entries = new();
    private float _fadeDeadline;
    private readonly record struct Entry(ImageState State, int Width, int Height, float Start, float Duration, int Easing);

    /// <summary>UI producer only; the owning scene slot must not be leased by the renderer. Full copy of every cache
    /// entry — kept for callers with no narrower referenced-id set (see the <c>ReadOnlySpan&lt;int&gt;</c> overload,
    /// perf plan item 1). SceneRenderFrame.cs is the one production caller left on this overload; the async render
    /// thread should switch to <c>Capture(images, Scene.ReferencedImageIds)</c> plus any detached-slab ids.</summary>
    public void Capture(ImageCache? source)
    {
        _entries.Clear();
        _fadeDeadline = float.NegativeInfinity;
        source?.CopyRecordingInputs(this);
    }

    /// <summary>Narrowed capture (perf plan item 1): copies only the entries the recorder can actually reach —
    /// <paramref name="referencedIds"/> (a captured scene's <c>ReferencedImageIds</c>, deduped by the caller) — instead
    /// of every entry ever seen this session. An id absent from <paramref name="referencedIds"/> that source still
    /// tracks is simply not copied; <see cref="StateOf"/>/<see cref="Retains"/> read as unknown/not-retained for it,
    /// which is correct — nothing captured this frame can draw it.</summary>
    public void Capture(ImageCache? source, ReadOnlySpan<int> referencedIds)
    {
        _entries.Clear();
        _fadeDeadline = float.NegativeInfinity;
        source?.CopyRecordingInputs(this, referencedIds);
    }

    /// <summary>Merge extra entries into an already-captured snapshot without clearing it (perf plan item 5: a
    /// detached-fly slab's own image ids, folded in by the caller of a reused inline capture instead of re-walking the
    /// whole scene). No-op for a null source or an empty id set.</summary>
    public void AddReferenced(ImageCache? source, ReadOnlySpan<int> ids)
    {
        if (source is null || ids.IsEmpty) return;
        source.CopyRecordingInputs(this, ids);
    }

    internal void Add(int id, ImageState state, int width, int height, float start, float duration, int easing,
                      float swapHoldUntilMs = float.NegativeInfinity)
    {
        // Idempotent by design: a re-add (AddReferenced folding in an id the main capture already carried, or a
        // caller re-adding the same id) simply refreshes the entry rather than throwing on a duplicate key.
        _entries[id] = new(state, width, height, start, duration, easing);
        if (state == ImageState.Ready && !float.IsNaN(start) && duration > 0)
            _fadeDeadline = MathF.Max(_fadeDeadline, start + duration);
        // A node's swap crossfade (ImageCache.BeginSwap) draws this entry as the OUTGOING texture until the deadline:
        // clock-driven work the render thread must keep presenting even though no reveal is running.
        if (state == ImageState.Ready && swapHoldUntilMs > _fadeDeadline) _fadeDeadline = swapHoldUntilMs;
    }

    internal bool HasCrossfades(float clockMs) => clockMs < _fadeDeadline;
    /// <summary>Entries currently held — diagnostics/gates only (perf plan item 1: proves a narrowed capture holds
    /// exactly the referenced set, not every image the source cache has ever seen).</summary>
    internal int Count => _entries.Count;

    public ImageState StateOf(ImageHandle image) => _entries.TryGetValue(image.Id, out var entry) ? entry.State : ImageState.None;
    internal bool Retains(int id) => _entries.TryGetValue(id, out var entry) && entry.State == ImageState.Ready;
    public (int W, int H) SizeOf(ImageHandle image) => _entries.TryGetValue(image.Id, out var entry) ? (entry.Width, entry.Height) : default;
    public bool FadeParamsOf(ImageHandle image, out float start, out float duration, out int easing)
    {
        if (_entries.TryGetValue(image.Id, out var entry) && !float.IsNaN(entry.Start))
        {
            start = entry.Start; duration = entry.Duration; easing = entry.Easing;
            return true;
        }
        start = float.NaN; duration = 0; easing = 0;
        return false;
    }
}
