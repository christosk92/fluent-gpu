using System;
using System.Buffers.Binary;
using System.Threading;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Text;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class SceneFrameOwnershipTests
{
    private sealed class NoDecoder : IImageDecoder
    {
        public bool Begin(int id, string source, int targetW, int targetH,
                          ImagePriority priority = ImagePriority.Visible) => false;
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels) { }
    }

    private static bool CoveredBy(RepaintDamageRegion region, in RectF r)
    {
        foreach (ref readonly var m in region.AsSpan())
            if (m.X <= r.X && m.Y <= r.Y && m.X + m.W >= r.X + r.W && m.Y + m.H >= r.Y + r.H) return true;
        return false;
    }

    /// <summary>
    /// A scene publication the consumer never adopted still names pixels that must be repainted. Publication is
    /// last-writer-wins, so those rects have to ride forward on the seam or they are lost with the recycled slot — and
    /// the frame that IS adopted must still be a PARTIAL repaint. Forcing full on every scene adoption (the old
    /// behaviour) made a skipped publication cost a whole-target redraw, which is the steady state under scroll.
    /// </summary>
    [Fact]
    public void SkippedScenePublicationsCarryRepaintDamageWithoutForcingFull()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var publisher = new SceneFramePublisher(1, 1);
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(1);
        scene.Bounds(scene.Root) = new RectF(0f, 0f, 400f, 300f);
        var images = new ImageCache(new NoDecoder());
        var strings = new StringTable();
        var animation = new AnimEngine(scene);
        var detached = new DetachedAnimSlab();
        var popups = Array.Empty<PopupWindowSlot>();

        void PublishDamaged(in RectF damaged)
        {
            var region = default(RepaintDamageRegion);
            region.Add(in damaged);
            var submit = new FrameInfo(new Size2(400, 300), 1f, default, RepaintDamage: region);
            publisher.PublishScene(scene, images, strings, default, default, default, default, detached, popups,
                animation, in submit, suppressVsync: false);
        }

        var first = new RectF(0f, 0f, 10f, 10f);
        var second = new RectF(200f, 100f, 10f, 10f);
        var third = new RectF(380f, 280f, 10f, 10f);
        PublishDamaged(in first);
        PublishDamaged(in second);
        PublishDamaged(in third);

        Assert.True(publisher.TryAcquire(out var adopted));
        Assert.Equal(3UL, adopted.PublishSeq);
        Assert.True(adopted.HasScene);
        var damage = adopted.Submit.RepaintDamage;
        Assert.False(damage.IsFull);
        Assert.Equal(RepaintFullReason.None, damage.FullReason);
        Assert.True(CoveredBy(damage, in first));
        Assert.True(CoveredBy(damage, in second));
        Assert.True(CoveredBy(damage, in third));
        // The carry names the OLDEST publication it speaks for, so a consumer can tell "two frames' damage rode
        // forward" from "two frames vanished".
        Assert.Equal(1UL, adopted.Submit.CarriedFromSeq);

        // Caught up: the carry is DISCHARGED and the next publication damages only its own band. A carry that never
        // discharged would ratchet every later frame toward a full repaint.
        var fourth = new RectF(100f, 50f, 10f, 10f);
        PublishDamaged(in fourth);
        Assert.True(publisher.TryAcquire(out var next));
        Assert.Equal(1, next.Submit.RepaintDamage.Count);
        Assert.True(CoveredBy(next.Submit.RepaintDamage, in fourth));
        Assert.Equal(4UL, next.Submit.CarriedFromSeq);

        // A target invalidation while the consumer is parked is the ONE cause the seam itself forces full for: the
        // pixels the consumer last worked from belong to a target that no longer exists.
        publisher.InvalidateTarget();
        PublishDamaged(in fourth);
        Assert.True(publisher.TryAcquire(out var afterInvalidate));
        Assert.True(afterInvalidate.Submit.RepaintDamage.IsFull);
        Assert.Equal(RepaintFullReason.TargetInvalidated, afterInvalidate.Submit.RepaintDamage.FullReason);
        publisher.ReleaseSceneResources();
    }

    [Fact]
    public void AcquiredArenaStaysPinnedAcrossOverwritesAndBareWakes()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var publisher = new SceneFramePublisher(1, 1);
        publisher.Publish([7, 7, 7], [11, 11], default);
        Assert.True(publisher.TryAcquire(out var retained));
        Assert.False(publisher.TryAcquire(out _));
        for (int i = 0; i < 1000; i++)
            publisher.Publish([1, 2, 3, 4, 5], [3, 4, 5], default);
        Assert.Equal(new byte[] { 7, 7, 7 }, publisher.Bytes(retained).ToArray());
        Assert.Equal(new ulong[] { 11, 11 }, publisher.SortKeys(retained).ToArray());
        Assert.Equal(1UL, publisher.LastConsumedSeq);
        Assert.True(publisher.TryAcquire(out var newest));
        Assert.Equal(1001UL, newest.PublishSeq);
    }

    [Fact]
    public void ConcurrentPublicationNeverTearsHeaderOrArena()
    {
        const int frames = 20000;
        var publisher = new SceneFramePublisher(8, 1);
        int finished = 0;
        Exception? failure = null;
        var producer = new Thread(() =>
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            try
            {
                var bytes = new byte[512];
                var keys = new ulong[64];
                for (ulong seq = 1; seq <= frames; seq++)
                {
                    for (int i = 0; i < keys.Length; i++)
                    {
                        keys[i] = seq;
                        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8, 8), seq);
                    }
                    int count = (int)(seq % 64) + 1;
                    publisher.Publish(bytes.AsSpan(0, count * 8), keys.AsSpan(0, count), default);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref finished, 1); }
        });
        producer.Start();
        ulong previous = 0;
        RenderFrame retained = default;
        try
        {
            while (Volatile.Read(ref finished) == 0 || publisher.LastConsumedSeq < (ulong)frames)
            {
                if (failure is not null) break;
                if (!publisher.TryAcquire(out var frame))
                {
                    // Includes the producer's withdrawn-announcement interval. No new frame must never
                    // release the previous lease or let a write invalidate its command/key storage.
                    if (previous != 0)
                    {
                        Assert.Equal(previous, publisher.LastConsumedSeq);
                        Assert.Equal(previous, publisher.SortKeys(retained)[0]);
                        Assert.Equal(previous, BinaryPrimitives.ReadUInt64LittleEndian(publisher.Bytes(retained)[..8]));
                    }
                    Thread.Yield();
                    continue;
                }
                Assert.True(frame.PublishSeq > previous);
                previous = frame.PublishSeq;
                retained = frame;
                Assert.Equal(frame.PublishSeq, frame.Submit.PublishSequence);
                Assert.Equal(frame.SortLen * 8, frame.ByteLen);
                var bytes = publisher.Bytes(frame);
                var keys = publisher.SortKeys(frame);
                for (int i = 0; i < keys.Length; i++)
                {
                    Assert.Equal(frame.PublishSeq, keys[i]);
                    Assert.Equal(frame.PublishSeq, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i * 8, 8)));
                }
            }
        }
        finally { producer.Join(); }
        Assert.Null(failure);
        Assert.Equal((ulong)frames, previous);
    }
}
