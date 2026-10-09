using System.Collections.Generic;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A fling through a grid of covers authored with a blur hash: every mount uploads the blur-hash preview under the new id,
/// and every recycle before the full decode lands cancels it, leaving a Canceled tombstone that still owns that texture.
/// Once nothing holds the tombstone the reclaim sweep drops it, and it must free the texture with it: ids are never reused,
/// so nothing else ever would.
/// </summary>
public sealed class TombstoneTextureReclaimTests
{
    private const string Hash = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";

    /// <summary>Never completes a decode on its own; a cancel completes as Canceled on the next pump, like DecodeScheduler.</summary>
    private sealed class CancelOnlyDecoder : IImageDecoder
    {
        private readonly Queue<int> _canceled = new();
        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible) => true;
        public void Cancel(int id) => _canceled.Enqueue(id);
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            while (_canceled.Count > 0) onComplete(_canceled.Dequeue(), false, 0, 0, ImageFailureKind.Canceled, 0);
        }
    }

    private static (ImageCache cache, HashSet<int> resident, HashSet<int> held) Make()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var cache = new ImageCache(new CancelOnlyDecoder(), budgetBytes: 1L << 30);
        var held = new HashSet<int>();
        cache.SetHeldImageSource(set => set.UnionWith(held));
        var resident = new HashSet<int>();   // the store: an upload places the id, an eviction frees it
        cache.SetPixelAttemptSink((id, _, _, _) => { resident.Add(id); return ImageUploadResult.Accepted; });
        cache.SetEvictSink(id => resident.Remove(id));
        return (cache, resident, held);
    }

    /// <summary>Mount and recycle one cover before its decode lands: request (blur hash uploaded), pin, unpin, cancel.</summary>
    private static ImageHandle MountAndRecycle(ImageCache cache, int i)
    {
        var h = cache.Request("https://covers.example/" + i, 64, 64, blurHash: Hash);
        cache.Pin(h);
        cache.Unpin(h);
        cache.Cancel(h);
        return h;
    }

    [Fact]
    public void ReclaimedCanceledTombstonesFreeTheirBlurHashTextures()
    {
        var (cache, resident, _) = Make();
        int n = ImageCache.ReclaimFloor + 76;
        for (int i = 0; i < n; i++) MountAndRecycle(cache, i);
        Assert.Equal(n, resident.Count);   // the previews outlive the cancel (a re-pin would show them again)

        // Pump is what runs the reclaim sweep (ImageCache.Pump -> ReclaimTombstonesIfDue), so the previews are freed there.
        cache.Pump();
        Assert.Empty(resident);

        cache.TrimToBudget();

        Assert.True(cache.EntryCount < n, $"the reclaim sweep did not run: entries={cache.EntryCount}");
        Assert.Empty(resident);
    }

    [Fact]
    public void AHeldCanceledTombstoneKeepsItsBlurHashTexture()
    {
        var (cache, resident, held) = Make();
        var kept = MountAndRecycle(cache, 0);
        held.Add(kept.Id);
        for (int i = 1; i < ImageCache.ReclaimFloor + 76; i++) MountAndRecycle(cache, i);
        cache.Pump();

        cache.TrimToBudget();

        Assert.Equal(new[] { kept.Id }, resident);
    }
}
