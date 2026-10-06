using FluentGpu.Foundation;
using FluentGpu.Hosting;

namespace FluentGpu.Rhi.D3D12;

// ── Hidden-window memory release (HiddenStage.Shallow) ─────────────────────────────────────────────────────────────────
//
// While the primary window is minimized, hidden to the tray or fully covered for long enough, nothing it could draw is
// visible, so every resource a frame would simply rebuild is released instead of idling resident. The host has already
// evicted the retained tiles (SliceTable.EvictAll + TrimTileSurfaces) and queued evictions for the image textures nothing
// holds; this is the device's half. Everything here is fence-gated exactly like the idle trims it generalizes (a retire
// behind the last-use fence, destroyed once the fence passed), so a frame still in flight finishes first, and everything
// is recreated lazily by the first frame back - nothing a presented frame needs is dropped.
public sealed unsafe partial class D3D12Device
{
    /// <inheritdoc/>
    public void ReleaseHiddenResources(HiddenStage stage)
    {
        if (_device == null || _fence == null) return;
        AssertSubmitThread();
        if (stage == HiddenStage.Visible)
        {
            _imageTextures?.SetNoPooling(false);
            _videoMemorySampleCountdown = 1;   // the first present after the restore re-reads the memory census (it is otherwise sampled every N presents; an idle app has few)
            return;
        }
        if (System.Threading.Volatile.Read(ref _deviceLostReason) != 0) return;   // the recovery rebuilds all of it
        ulong completed = _fence->GetCompletedValue();
        _imageTextures?.SetNoPooling(true);   // an eviction still on its way must not refill the pools this call empties
        // Scratch: groups, self-blurs, acrylic backdrops and every RETAINED derived result (each recomputed from its content key
        // on the first composite back). Each retires behind its own last-use fence.
        _surfaces?.DropScratch();
        // The stencil target of the primary window (a visible pop-out's own swapchain keeps its own: it is presenting).
        if (_primarySwapchain is { Disposed: false } primary)
        {
            var f = primary.Frame;
            if (f.StencilDsv != null && f.LastSubmitFence <= completed) ReleaseStencilDsv(f);
        }
        _bakedBlur?.ReleaseLevels();
        _imageTextures?.ReleaseIdle(completed);
        _surfaces?.DrainRetired(completed);
        _imageTextures?.ReclaimCompleted(completed);
        PublishVideoMemorySnapshot(force: true);   // the census line right after the release reads fresh numbers
    }

    /// <inheritdoc/>
    public bool HasHiddenReleaseBacklog
        => (_surfaces?.RetiredCount ?? 0) > 0
        || (_imageTextures?.HasRetireBacklog ?? false)
        || (_bakedBlur?.HasLevels ?? false)
        || (_primarySwapchain is { Disposed: false } p && p.Frame.StencilDsv != null);
}
