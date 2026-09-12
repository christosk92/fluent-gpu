using System;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class SnapshotResourceReferenceTests
{
    static SceneStore Scene()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(1);
        return scene;
    }

    static NodeHandle Child(SceneStore scene, VisualKind kind = VisualKind.Box, int image = 0)
    {
        var node = scene.CreateNode(2);
        scene.AppendChild(scene.Root, node);
        scene.Paint(node).VisualKind = kind;
        scene.Paint(node).ImageId = image;
        return node;
    }

    static void CaptureNext(SceneStore scene, SceneRecordingSnapshot snapshot, ulong previous,
        ReadOnlySpan<NodeHandle> extraRoots = default)
    {
        scene.NotePublished(previous);
        Assert.True(snapshot.CaptureIncremental(scene, extraRoots, previous));
        Assert.Equal(0, snapshot.IncrementalParityFailures);
        var full = new SceneRecordingSnapshot();
        full.Capture(scene, extraRoots);
        Assert.True(snapshot.EqualsForParity(full, out string mismatch), mismatch);
        Assert.Equal(full.ReferencedImageIds.ToArray(), snapshot.ReferencedImageIds.ToArray()); // ordered, not just set-equal
        full.ReleaseResources();
    }

    [Fact]
    public void StableLyricWipesDoNotRescanThousandsOfResourceRows()
    {
        var scene = Scene();
        for (int i = 0; i < 3500; i++) Child(scene);
        Child(scene, VisualKind.Image, 7);
        var lyric = Child(scene, VisualKind.Text);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        Assert.Equal(snapshot.CapturedNodeCount, snapshot.ResourceReferenceRowsScanned);
        for (ulong seq = 1; seq <= 120; seq++)
        {
            scene.NotePublished(seq);
            scene.SetGlyphWipe(lyric, new(default, default, seq / 120f));
            Assert.True(snapshot.CaptureIncremental(scene, [], seq));
            Assert.Equal(0, snapshot.ResourceReferenceRowsScanned);
            Assert.True(snapshot.CopiedNodeCount < 8);
            Assert.Equal(0, snapshot.IncrementalParityFailures);
            Assert.Equal(7, Assert.Single(snapshot.ReferencedImageIds.ToArray()));
        }
        snapshot.ReleaseResources();
    }

    [Fact]
    public void CurrentDerivedAndOutgoingChangesRebuildButFadeScalarChangesDoNot()
    {
        var scene = Scene();
        var image = Child(scene, VisualKind.Image, 1);
        scene.SetImageEffects(image, new ImageVisualEffects(2, default, default) { SwapOutgoingId = 3 });
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        Assert.Equal(new[] { 1, 2, 3 }, snapshot.ReferencedImageIds.ToArray());
        scene.NotePublished(1);
        scene.SetImageEffects(image, new ImageVisualEffects(2, default, default)
            { SwapOutgoingId = 3, SwapStartMs = 123, SwapMs = 220 });
        CaptureNext(scene, snapshot, 1);
        Assert.Equal(0, snapshot.ResourceReferenceRowsScanned);
        scene.NotePublished(2);
        scene.SetImageEffects(image, new ImageVisualEffects(4, default, default) { SwapOutgoingId = 5 });
        CaptureNext(scene, snapshot, 2);
        Assert.Equal(new[] { 1, 4, 5 }, snapshot.ReferencedImageIds.ToArray());
        Assert.Equal(snapshot.CapturedNodeCount, snapshot.ResourceReferenceRowsScanned);
        scene.NotePublished(3);
        scene.Paint(image).ImageId = 9;
        scene.NoteCaptureChanged((int)image.Raw.Index);
        CaptureNext(scene, snapshot, 3);
        Assert.Equal(new[] { 9, 4, 5 }, snapshot.ReferencedImageIds.ToArray());
        scene.NotePublished(4);
        scene.ClearImageEffects(image);
        CaptureNext(scene, snapshot, 4);
        Assert.Equal(new[] { 9 }, snapshot.ReferencedImageIds.ToArray());
    }

    [Fact]
    public void SharedIdsSurvivePartialRemovalAndReorderingKeepsFullCaptureOrder()
    {
        var scene = Scene();
        var a = Child(scene, VisualKind.Image, 1);
        Child(scene, VisualKind.Image, 1);
        var b = Child(scene, VisualKind.Image, 2);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        Assert.Equal(new[] { 2, 1 }, snapshot.ReferencedImageIds.ToArray());
        scene.NotePublished(1);
        scene.Detach(b);
        scene.PrependChild(scene.Root, b);
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index); // fixture bypasses reconciler bulk commit
        CaptureNext(scene, snapshot, 1);
        Assert.Equal(new[] { 1, 2 }, snapshot.ReferencedImageIds.ToArray());
        Assert.True(snapshot.ResourceReferenceRowsScanned > 0);
        scene.NotePublished(2);
        scene.FreeSubtree(a);
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index);
        CaptureNext(scene, snapshot, 2);
        Assert.Equal(new[] { 1, 2 }, snapshot.ReferencedImageIds.ToArray());
    }

    [Fact]
    public void RecycledGenerationAndImageToOtherKindDiscardOldReferences()
    {
        var scene = Scene();
        var old = Child(scene, VisualKind.Image, 1);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        scene.FreeSubtree(old);
        var replacement = Child(scene, VisualKind.IconLayer, 999); // ImageId means geometry, not an image here
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index);
        Assert.Equal(old.Raw.Index, replacement.Raw.Index);
        CaptureNext(scene, snapshot, 1);
        Assert.Empty(snapshot.ReferencedImageIds.ToArray());
        Assert.True(snapshot.ResourceReferenceRowsScanned > 0);
    }

    [Fact]
    public void ExtraRootsAndParkingInvalidateMembershipWithoutSourceContentChanges()
    {
        var scene = Scene();
        var image = Child(scene, VisualKind.Image, 11);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        scene.Detach(image);
        scene.NoteCaptureChanged((int)scene.Root.Raw.Index);
        CaptureNext(scene, snapshot, 1);
        Assert.Empty(snapshot.ReferencedImageIds.ToArray());
        CaptureNext(scene, snapshot, 2, [image]);
        Assert.Equal(new[] { 11 }, snapshot.ReferencedImageIds.ToArray());
        CaptureNext(scene, snapshot, 3);
        Assert.Empty(snapshot.ReferencedImageIds.ToArray());
    }

    [Fact]
    public void ImageReadinessAndClockRefreshEvenWhenIdentityScanIsSkipped()
    {
        var scene = Scene();
        var cache = new ImageCache(new FakeImageDecoder());
        cache.AdvancePresentationClock(1000);
        var handle = cache.Request("reference-metadata", 16, 16);
        Child(scene, VisualKind.Image, handle.Id);
        var snapshot = new SceneRecordingSnapshot();
        var images = new ImageRecordingSnapshot();
        snapshot.Capture(scene);
        images.Capture(cache, snapshot.ReferencedImageIds);
        Assert.NotEqual(ImageState.Ready, images.StateOf(handle));
        cache.AdvancePresentationClock(1100);
        cache.Pump();
        CaptureNext(scene, snapshot, 1);
        Assert.Equal(0, snapshot.ResourceReferenceRowsScanned);
        images.Capture(cache, snapshot.ReferencedImageIds);
        Assert.Equal(ImageState.Ready, images.StateOf(handle));
        Assert.Equal(cache.ClockCapturedAtMs, images.ClockCapturedAtMs);
        Assert.True(images.HasCrossfades(cache.ClockMs));
        cache.AdvancePresentationClock(2000);
        CaptureNext(scene, snapshot, 2);
        images.Capture(cache, snapshot.ReferencedImageIds);
        Assert.False(images.HasCrossfades(cache.ClockMs));
    }

    [Fact]
    public void SameSpanIdStillCopiesNewDecorationArtifactsAndRetainsOwnership()
    {
        var scene = Scene();
        var text = Child(scene, VisualKind.Text);
        int id = SpanRunTable.Shared.Create([new(0, 3, 400, 12, default, default, SpanStyle.UnderlineBit)]);
        SpanRunTable.Shared.AddRef(id); // scene owner; FreeSubtree releases it
        var run = SpanRunTable.Shared.Resolve(id)!;
        run.PublishRects(new(100, [new(new(0, 10, 30, 1), 0, SpanStyle.UnderlineBit)]));
        scene.Layout(text).TextStyle = new(default, 12, 400, SpanRunId: id);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.NotePublished(1);
        run.PublishRects(new(200, [new(new(0, 20, 60, 2), 0, SpanStyle.UnderlineBit)]));
        scene.NoteCaptureChanged((int)text.Raw.Index); // the real layout pass carries this write/bulk notification
        CaptureNext(scene, snapshot, 1);
        Assert.Equal(0, snapshot.ResourceReferenceRowsScanned);
        Assert.True(snapshot.TryGetSpanDecorations(text, out _, out var rects));
        Assert.Equal(new RectF(0, 20, 60, 2), rects[0].Rect);
        scene.FreeSubtree(scene.Root);
        for (int i = 0; i < 300; i++)
        {
            int churn = SpanRunTable.Shared.Create([]);
            SpanRunTable.Shared.AddRef(churn);
            SpanRunTable.Shared.Release(churn);
        }
        Assert.NotNull(SpanRunTable.Shared.Resolve(id)); // held snapshot still owns it
        snapshot.ReleaseResources();
        for (int i = 0; i < 300; i++)
        {
            int churn = SpanRunTable.Shared.Create([]);
            SpanRunTable.Shared.AddRef(churn);
            SpanRunTable.Shared.Release(churn);
        }
        Assert.Null(SpanRunTable.Shared.Resolve(id));
    }

    [Fact]
    public void FullCaptureAndSourceSwitchAlwaysRebuildReferences()
    {
        var a = Scene();
        Child(a, VisualKind.Image, 1);
        var b = Scene();
        Child(b, VisualKind.Image, 2);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(a);
        Assert.False(snapshot.CaptureIncremental(b, [], 1));
        snapshot.Capture(b);
        Assert.Equal(new[] { 2 }, snapshot.ReferencedImageIds.ToArray());
        Assert.Equal(snapshot.CapturedNodeCount, snapshot.ResourceReferenceRowsScanned);
        snapshot.ReleaseResources();
        b.NotePublished(1);
        Assert.False(snapshot.CaptureIncremental(b, [], 1));
        snapshot.Capture(b);
        Assert.Equal(snapshot.CapturedNodeCount, snapshot.ResourceReferenceRowsScanned);
    }
}
