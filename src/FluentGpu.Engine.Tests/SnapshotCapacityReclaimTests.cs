using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Text;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class SnapshotCapacityReclaimTests
{
    [Fact]
    public void PolicyRequiresSustainedSlackAndRetainsCooldownAfterRegrowth()
    {
        SceneCapacityReclaimPolicy policy = default;
        policy.Observe(8192, 20, 100);
        Assert.False(policy.TryTarget(8192, 30_099, out _));
        Assert.True(policy.TryTarget(8192, 30_100, out int target));
        Assert.Equal(256, target);
        policy.NoteAttempt(30_100);
        policy.Observe(8192, 7000, 30_101); // Real re-expansion resets the low-water streak.
        policy.Observe(8192, 20, 30_102);
        Assert.False(policy.TryTarget(8192, 60_102, out _));
        Assert.True(policy.TryTarget(8192, 150_100, out _));
        policy.Observe(8192, 3000, 150_101);
        Assert.False(policy.TryTarget(8192, 900_000, out _));
        Assert.True(SceneCapacityReclaimPolicy.Target(8000) >= 8032);
    }

    [Fact]
    public void FreshCaptureIgnoresParkedHighWaterButExtraRootPreservesHighIndices()
    {
        using var f = new Fixture();
        var high = new SceneRecordingSnapshot();
        var small = new SceneRecordingSnapshot();
        try
        {
            high.Capture(f.Scene);
            int peak = high.Capacity;
            f.Park();
            high.Capture(f.Scene);
            small.Capture(f.Scene);
            Assert.True(small.Capacity * 32 < peak);
            Assert.False(small.IsLive(f.HighLeaf));
            Assert.True(high.EqualsForParity(small, out string mismatch), mismatch);
            // One reachable node at a high sparse index still requires that index, not merely live-count slots.
            small.Capture(f.Scene, [f.HighLeaf]);
            Assert.True(small.IsLive(f.HighLeaf));
            Assert.True(small.IsLive(f.Page)); // Required ancestor, even though its other children remain parked.
            Assert.True(small.Capacity > f.HighLeaf.Raw.Index);
            Assert.Equal((int)f.HighLeaf.Raw.Index + 1, small.RequiredNodeCapacity);
            f.Scene.AppendChild(f.Scene.Root, f.Page);
            f.Scene.NoteBulkMutation();
            small.Capture(f.Scene);
            high.Capture(f.Scene);
            Assert.True(high.EqualsForParity(small, out mismatch), mismatch);
        }
        finally { high.ReleaseResources(); small.ReleaseResources(); }
    }

    [Fact]
    public void ColdReplacementDropsCapacityWithoutTouchingReaderOrPublishingAndNextCaptureIsWarm()
    {
        using var f = new Fixture();
        var (free, held) = f.WarmAndPark();
        var heldScene = f.Publisher.Scene(held).Scene;
        var oldFree = f.Publisher.Scene(free).Scene;
        int heldCapacity = heldScene.Capacity;
        long oldBytes = oldFree.IndexedCapacityBytes;
        ulong sequence = f.Publisher.PublishSeq, consumed = f.Publisher.LastConsumedSeq;
        Assert.NotEqual(long.MaxValue, f.Publisher.NextCapacityMaintenanceMs);
        Assert.True(f.Reclaim());
        Assert.Equal(long.MaxValue, f.Publisher.NextCapacityMaintenanceMs); // Remaining high-water belongs to Reading.
        Assert.Same(heldScene, f.Publisher.Scene(held).Scene);
        Assert.Equal(heldCapacity, heldScene.Capacity);
        Assert.True(heldScene.TryGetTextStyle(f.Text, out var style));
        Assert.Equal(f.Family, style.FontFamily);
        Assert.Equal(sequence, f.Publisher.PublishSeq);
        Assert.Equal(consumed, f.Publisher.LastConsumedSeq);
        Assert.False(f.Publisher.TryAcquire(out _)); // No maintenance-only publication/wake.
        var replacement = f.Publisher.Scene(free).Scene;
        Assert.NotSame(oldFree, replacement);
        Assert.Equal(256, replacement.Capacity);
        Assert.Equal(oldBytes - replacement.IndexedCapacityBytes, f.Publisher.ReclaimedIndexedCapacityBytes);
        Assert.Equal(0UL, oldFree.LastCaptureSeq); // Retired baseline/pins were released.
        Assert.Equal(held.PublishSeq, f.Publisher.OldestSlotCaptureSeq);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong next = f.PublishRaw();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        f.Scene.NotePublished(next);
        Assert.Equal(0, allocated);
        Assert.False(f.Publisher.LastCaptureWasIncremental); // Cold preparation is not an accepted baseline.
        Assert.True(f.Publisher.TryAcquire(out var acquired));
        Assert.Equal(free.ArenaIndex, acquired.ArenaIndex); // Prefer the prepared slot, not an unused third frame.
        var full = new SceneRecordingSnapshot();
        try
        {
            full.Capture(f.Scene);
            Assert.True(replacement.EqualsForParity(full, out string mismatch), mismatch);
        }
        finally { full.ReleaseResources(); }
        Assert.Equal(1, f.Publisher.CapacityReclaims);
    }

    [Fact]
    public void RecorderReuseBlockChainAcceptsUnallocatedParkedTailAsDead()
    {
        using var f = new Fixture();
        f.Park();
        var snapshot = new SceneRecordingSnapshot();
        try
        {
            snapshot.Capture(f.Scene);
            Assert.True(f.HighLeaf.Raw.Index >= snapshot.Capacity);
            Assert.False(snapshot.IsLive(f.HighLeaf));
            Assert.True(snapshot.Parent(f.HighLeaf).IsNull);
            Assert.True(snapshot.FirstChild(f.HighLeaf).IsNull);
            Assert.True(snapshot.NextSibling(f.HighLeaf).IsNull);
            Assert.Equal(default, snapshot.Flags(f.HighLeaf));
            Assert.False(snapshot.TryGetInteract(f.HighLeaf, out _));
            Assert.False(snapshot.TryGetBrushAnim(f.HighLeaf, out _));
            var draw = new DrawList();
            var spans = new SpanTable();
            // BlockSpecials/BlockChain deliberately walk caller-supplied handles. This used to read the dead
            // source-high-water topology row, and must still terminate without touching an unallocated tail.
            snapshot.Recording.Record(snapshot, draw, spans: spans, reuseBlockRoots: [f.HighLeaf]);
            snapshot.Recording.Record(snapshot, draw, spans: spans, reuseBlockRoots: [f.HighLeaf]);
        }
        finally { snapshot.ReleaseResources(); }
    }

    [Fact]
    public void PendingPublicationAndRetainedReaderAreNotReclaimable()
    {
        using var f = new Fixture();
        f.WarmAndPark();
        ulong pending = f.PublishRaw();
        f.Scene.NotePublished(pending); // One Reading, one Published, and one never-created Free slot.
        Assert.True(f.Publisher.HasPendingFrame);
        Assert.False(f.Reclaim());
        Assert.True(f.Publisher.HasPendingFrame);
        Assert.True(f.Publisher.TryAcquire(out var acquired));
        Assert.Equal(pending, acquired.PublishSeq);
        Assert.Equal(0, f.Publisher.CapacityReclaims);
    }

    [Fact]
    public void ReplacementReacquiresFontAndSpanPinsBeforeOldFrameRetires()
    {
        using var f = new Fixture();
        f.WarmAndPark();
        f.ReleaseAuthoredResources(); // Only snapshots now own the identities still present in committed columns.
        Assert.True(f.Reclaim());
        for (int i = 0; i < 100; i++) f.Strings.Tick();
        ChurnSpans();
        Assert.Equal("reclaim fixture font", f.Strings.Resolve(f.Family));
        Assert.NotNull(SpanRunTable.Shared.Resolve(f.SpanId));
        f.PublishAndAcquire();
        // Remove those identities and recapture every initialized slot; pins must not leak after replacement.
        f.Scene.Layout(f.Text).TextStyle = default;
        f.Scene.Paint(f.Text).VisualKind = VisualKind.Box;
        f.Scene.NoteBulkMutation();
        for (int i = 0; i < 4; i++) f.PublishAndAcquire();
        for (int i = 0; i < 100; i++) f.Strings.Tick();
        ChurnSpans();
        Assert.Empty(f.Strings.Resolve(f.Family));
        Assert.Null(SpanRunTable.Shared.Resolve(f.SpanId));
    }

    private static void ChurnSpans()
    {
        for (int i = 0; i < 300; i++)
        {
            int id = SpanRunTable.Shared.Create([]);
            SpanRunTable.Shared.AddRef(id);
            SpanRunTable.Shared.Release(id);
        }
    }

    [Fact]
    public void PreflightDoesNotActivatePopupWithUnpublishedSequence()
    {
        using var f = new Fixture();
        var (free, _) = f.WarmAndPark();
        using var window = new HeadlessPopupWindow(new(default, new(0, 0, 100, 100)));
        var popup = new PopupWindowSlot(1, window, f.Text, PopupWindowMaterial.None);
        PopupWindowSlot[] popups = [popup];
        var replacement = f.Publisher.Scene(free).PrepareCapacityReplacement(f.Scene, f.Images, f.Strings,
            f.Detached, popups, f.Animation, 256);
        try
        {
            Assert.Equal(0UL, popup.FirstSceneSequence);
            ulong actualSequence = f.Scene.PublishSeq + 1;
            replacement.Capture(f.Scene, f.Images, f.Strings, default, default, default, default,
                f.Detached, popups, f.Animation, actualSequence, 0);
            Assert.Equal(actualSequence, popup.FirstSceneSequence);
        }
        finally { replacement.ReleaseResources(); }
    }

    [Fact]
    public void FailedPreflightPreservesOldFramePinsAndReleasesWritingClaim()
    {
        using var f = new Fixture();
        var (free, held) = f.WarmAndPark();
        var previous = f.Publisher.Scene(free);
        ulong baseline = previous.Scene.LastCaptureSeq;
        Assert.Throws<InvalidOperationException>(() => f.Publisher.TryReclaimSceneCapacity(f.Scene, f.Images, f.Strings,
            f.Detached, new UnavailablePopups(), f.Animation, Environment.TickCount64 + 30_001));
        Assert.Same(previous, f.Publisher.Scene(free));
        Assert.Equal(baseline, previous.Scene.LastCaptureSeq);
        Assert.True(previous.Scene.TryGetTextStyle(f.Text, out var style));
        Assert.Equal(f.Family, style.FontFamily);
        Assert.Equal(held.PublishSeq, f.Publisher.LastConsumedSeq);
        Assert.Equal(free.ArenaIndex, f.PublishAndAcquire().ArenaIndex);
        Assert.Equal(0, f.Publisher.CapacityReclaims);
    }

    private sealed class UnavailablePopups : IReadOnlyList<PopupWindowSlot>
    {
        public int Count => 1;
        public PopupWindowSlot this[int index] => throw new InvalidOperationException("Injected preflight failure.");
        public IEnumerator<PopupWindowSlot> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly SceneStore Scene = new();
        internal readonly SceneFramePublisher Publisher = new();
        internal readonly StringTable Strings = new();
        internal readonly ImageCache Images = new(new NoDecoder());
        internal readonly DetachedAnimSlab Detached = new();
        internal readonly PopupWindowSlot[] Popups = [];
        internal readonly AnimEngine Animation;
        internal readonly NodeHandle Text, Page, HighLeaf;
        internal readonly StringId Family;
        internal readonly int SpanId;
        private bool _authored = true;

        internal Fixture()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Scene.Root = Scene.CreateNode(1);
            Text = Scene.CreateNode(2);
            Page = Scene.CreateNode(3);
            Scene.AppendChild(Scene.Root, Text);
            Scene.AppendChild(Scene.Root, Page);
            Family = Strings.Intern("reclaim fixture font");
            Strings.AddRef(Family);
            SpanId = SpanRunTable.Shared.Create([new(0, 4, 400, 12, Family, default, 0)]);
            SpanRunTable.Shared.AddRef(SpanId);
            Scene.Paint(Text).VisualKind = VisualKind.Text;
            Scene.Layout(Text).TextStyle = new TextStyle { FontFamily = Family, SizeDip = 12, Weight = 400, SpanRunId = SpanId };
            for (int i = 0; i < 4096; i++)
            {
                HighLeaf = Scene.CreateNode(4);
                Scene.AppendChild(Page, HighLeaf);
            }
            Animation = new AnimEngine(Scene);
        }

        internal void Park() { Scene.Detach(Page); Scene.NoteBulkMutation(); }
        internal ulong PublishRaw() => Publisher.PublishScene(Scene, Images, Strings, default,
            default, default, default, Detached, Popups, Animation,
            new FrameInfo(new(800, 600), 1, default), false, false);
        internal RenderFrame PublishAndAcquire()
        {
            ulong seq = PublishRaw();
            Scene.NotePublished(seq);
            Assert.True(Publisher.TryAcquire(out var frame));
            return frame;
        }
        internal (RenderFrame Free, RenderFrame Held) WarmAndPark()
        {
            PublishAndAcquire();
            PublishAndAcquire();
            Park();
            var free = PublishAndAcquire();
            var held = PublishAndAcquire();
            return (free, held);
        }
        internal bool Reclaim() => Publisher.TryReclaimSceneCapacity(Scene, Images, Strings, Detached, Popups, Animation,
            Environment.TickCount64 + SceneCapacityReclaimPolicy.LowWaterMs + 1);
        internal void ReleaseAuthoredResources()
        {
            if (!_authored) return;
            _authored = false;
            Strings.Release(Family);
            SpanRunTable.Shared.Release(SpanId);
        }
        public void Dispose() { ReleaseAuthoredResources(); Publisher.ReleaseSceneResources(); }
    }

    private sealed class NoDecoder : IImageDecoder
    {
        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible) => false;
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }
    }
}
