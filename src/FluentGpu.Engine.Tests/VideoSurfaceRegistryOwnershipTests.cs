using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F183 (the registry has ONE owner): <see cref="VideoSurfaceRegistry"/> is UI-thread state. The render thread reads only what a
/// publication carries (a POD snapshot), reads the small content mailbox under a lock, and hands results back through a mailbox the
/// UI thread folds into its signals - it never writes a slot, a pump counter or a <c>Signal</c>.
/// </summary>
public sealed class VideoSurfaceRegistryOwnershipTests
{
    private sealed class RecordingPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        public int Destroys;
        private uint _next = 1;

        public VideoSurfaceId CreateSurface()
        {
            var id = new VideoSurfaceId(_next++);
            Calls.Add($"Create({id.Value})");
            return id;
        }

        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
        {
            Calls.Add($"Bind({id.Value},0x{dcompSurfaceHandle:X})");
            return true;
        }

        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => Calls.Add($"Place({id.Value})");
        public void SetVisible(VideoSurfaceId id, bool visible) { }
        public void Destroy(VideoSurfaceId id) { Destroys++; Calls.Add($"Destroy({id.Value})"); }
        public void Commit() { }

        public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static readonly RectF Rect = new(10f, 20f, 100f, 50f);

    private static void RunOnThread(ThreadGuard.ThreadRole? role, Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (role is { } r) ThreadGuard.BindCurrent(r);
                body();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.Start();
        t.Join();
        if (failure is not null) throw new InvalidOperationException("worker thread failed", failure);
    }

    private static long GenOf(VideoSurfaceRegistry reg)
    {
        var buf = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        int n = reg.SnapshotInto(buf);
        Assert.True(n > 0);
        return buf[0].Gen;
    }

    [Fact]
    public void ASnapshot_IsTheFullStateOfEveryLiveSlot_AndClearsTheUnpublishedFlag()
    {
        var reg = new VideoSurfaceRegistry();
        Assert.False(reg.HasUnpublishedChanges);
        int a = reg.Acquire();
        int b = reg.Acquire();
        reg.Bind(a, 0x10);
        reg.Place(a, Rect, z: 3);
        reg.SetVisible(b, false);
        Assert.True(reg.HasUnpublishedChanges);   // a publication is owed even if nothing else in the frame moved

        var buf = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        int n = reg.SnapshotInto(buf);
        Assert.Equal(2, n);
        Assert.False(reg.HasUnpublishedChanges);
        Assert.Equal(a, buf[0].Token);
        Assert.Equal((nuint)0x10, buf[0].DesiredHandle);
        Assert.Equal(Rect, buf[0].RectDip);
        Assert.Equal(3, buf[0].Z);
        Assert.True(buf[0].HasGeometry && buf[0].Visible);
        Assert.Equal(b, buf[1].Token);
        Assert.False(buf[1].Visible);
        Assert.NotEqual(buf[0].Gen, buf[1].Gen);

        // The snapshot is state: a second one with no change in between carries the same thing (a skipped publication loses nothing).
        var again = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        Assert.Equal(2, reg.SnapshotInto(again));
        Assert.Equal(buf[0].RectDip, again[0].RectDip);
        Assert.False(reg.HasUnpublishedChanges);
    }

    [Fact]
    public void ASnapshot_CarriesTheHoleOrigin_OnlyWhenTheTrackedNodeIsTheRectThatWasPlaced()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new RectF(0f, 0f, 320f, 180f);
        scene.Paint(node).LocalTransform = Affine2D.Translation(40f, 8f);
        RectF hole = scene.AbsoluteTransformedRect(node);

        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();
        reg.SetGeometryNode(token, node);
        reg.RequestGeometryPumps(scene);
        reg.Place(token, hole);
        var buf = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        reg.SnapshotInto(buf);
        Assert.True(buf[0].HasHoleOrigin);   // the owner placed the very rect it follows: the applier may follow the hole's pose
        Assert.Equal(hole.X, buf[0].HoleX);
        Assert.Equal(hole.Y, buf[0].HoleY);
        Assert.Equal(hole.W, buf[0].HoleW);   // the size rides along: it tells this hole from another that shares the token
        Assert.Equal(hole.H, buf[0].HoleH);

        reg.Place(token, new RectF(hole.X + 24f, hole.Y, hole.W, hole.H));   // a refit / clamp: the placed rect is not the node's
        reg.SnapshotInto(buf);
        Assert.False(buf[0].HasHoleOrigin);   // no pose correction rather than a constant offset
    }

    [Fact]
    public void RenderResults_AreFoldedIntoTheSignalsByTheUiThreadOnly()
    {
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        int token = reg.Acquire();
        long gen = GenOf(reg);

        RunOnThread(ThreadGuard.ThreadRole.Render, () => reg.PostResult(token - 1, gen, surfaceId: 7, bound: true, releaseDone: false));

        Assert.True(reg.Surface(token).Peek().IsNone);   // the render thread's post wrote no signal
        Assert.False(reg.Bound(token).Peek());
        Assert.True(reg.HasPendingPumps);                // it published an edge the UI thread folds in
        reg.PumpPending(1f);
        Assert.Equal(7u, reg.Surface(token).Peek().Value);
        Assert.True(reg.Bound(token).Peek());
        Assert.False(reg.HasPendingPumps);
    }

    [Fact]
    public void AReleaseCompletedOnTheRenderThread_FreesTheSlotOnTheUiThread_AndALateResultOfThePreviousIncarnationIsDropped()
    {
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        int token = reg.Acquire();
        long gen1 = GenOf(reg);
        reg.Release(token);

        RunOnThread(ThreadGuard.ThreadRole.Render, () => reg.PostResult(token - 1, gen1, 0, false, releaseDone: true));
        Assert.Equal(token, reg.Acquire());              // freed by the UI thread's import, then re-acquired as a NEW incarnation
        Assert.NotEqual(gen1, GenOf(reg));

        RunOnThread(ThreadGuard.ThreadRole.Render, () => reg.PostResult(token - 1, gen1, surfaceId: 9, bound: true, releaseDone: false));
        reg.PumpPending(1f);
        Assert.True(reg.Surface(token).Peek().IsNone);   // the old incarnation's late word never touches the new one
        Assert.False(reg.Bound(token).Peek());
    }

    [Fact]
    public void ASnapshotOfAFreedSlot_NeverResurrectsIt()
    {
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new RecordingPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, Rect);
        var stale = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        int staleCount = reg.SnapshotInto(stale);
        applier.ApplyTurn(p, stale.AsSpan(0, staleCount), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Count("Create("));

        reg.Release(token);
        var release = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        int releaseCount = reg.SnapshotInto(release);
        applier.ApplyTurn(p, release.AsSpan(0, releaseCount), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Destroys);
        Assert.Equal(token, reg.Acquire());              // a new incarnation with no handle yet

        // An OLDER publication (the elided turn re-reading its frame, a stale mailbox copy) still describes the previous incarnation.
        applier.ApplyTurn(p, stale.AsSpan(0, staleCount), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Count("Create("));
        Assert.Equal(1, p.Destroys);
    }

    [Fact]
    public void AHandleOlderThanTheOneAlreadyAdopted_IsNeverBoundAgain()
    {
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new RecordingPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, Rect);
        var older = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        int olderCount = reg.SnapshotInto(older);   // a publication that predates the next Bind...

        reg.Bind(token, 0x20);
        applier.ApplyStructural(p, 1f, deferCommit: false);   // ...which the early drain adopts first
        Assert.Contains("Bind(1,0x20)", p.Calls);

        applier.ApplyTurn(p, older.AsSpan(0, olderCount), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Count("Bind("));               // the surface is not flipped back to 0x10 and forward again
        Assert.Equal(1, p.Count("Place("));              // the older publication's geometry still lands
    }

    [Fact]
    public void ARebindUnderANewSequence_WrapsTheSameHandleAgain_OnlyOnce()
    {
        var reg = new VideoSurfaceRegistry();
        var applier = new VideoPlacementApplier(reg);
        var p = new RecordingPresenter();
        int token = reg.Acquire();
        reg.Bind(token, 0x10);
        reg.Place(token, Rect);
        var first = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        applier.ApplyTurn(p, first.AsSpan(0, reg.SnapshotInto(first)), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Count("Bind("));

        reg.Bind(token, 0x10);                           // value-gated: no new sequence
        var same = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        applier.ApplyTurn(p, same.AsSpan(0, reg.SnapshotInto(same)), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(1, p.Count("Bind("));

        reg.Rebind(token);                               // the producer rebuilt its surface behind the same value
        var forced = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        int forcedCount = reg.SnapshotInto(forced);
        applier.ApplyTurn(p, forced.AsSpan(0, forcedCount), default, 1f, VideoApplyScope.Full, deferCommit: false);
        Assert.Equal(2, p.Count("Bind("));
        applier.ApplyTurn(p, forced.AsSpan(0, forcedCount), default, 1f, VideoApplyScope.Full, deferCommit: false);   // the elided re-read
        Assert.Equal(2, p.Count("Bind("));
    }

    [Fact]
    public void TheRegistrysUiMethods_RejectTheRenderThread_WhenThreadGuardsAreCompiledIn()
    {
        var reg = new VideoSurfaceRegistry();
        int token = reg.Acquire();

        // Whether the engine's guards are armed is the ENGINE's build flag (the Conditional is decided per assembly): probe it through
        // an engine method that asserts, on a thread bound to no role.
        bool guarded = false;
        RunOnThread(null, () =>
        {
            try { new SceneFramePublisher().Publish(new byte[] { 1 }, default, default); }
            catch (ThreadConfinementViolation) { guarded = true; }
        });

        Exception? thrown = null;
        RunOnThread(ThreadGuard.ThreadRole.Render, () =>
        {
            try { reg.Place(token, Rect); }
            catch (ThreadConfinementViolation ex) { thrown = ex; }
        });
        if (guarded) Assert.NotNull(thrown);
        else Assert.Null(thrown);
    }

    [Fact]
    public void TheMailboxAndTheResultChannel_SurviveAConcurrentUiAndRenderHammer()
    {
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        int token = reg.Acquire();
        long gen = GenOf(reg);
        const int Iterations = 20000;
        int maxMail = 0;
        int renderPosts = 0;

        Exception? failure = null;
        var render = new Thread(() =>
        {
            try
            {
                ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Render);
                var mail = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
                for (int i = 0; i < Iterations; i++)
                {
                    reg.CopyMail(mail, out int count);
                    maxMail = Math.Max(maxMail, count);
                    reg.PostResult(token - 1, gen, (uint)(i & 0xFF) + 1, (i & 1) == 0, releaseDone: false);
                    renderPosts++;
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        render.Start();
        var buf = new VideoPresentIntent[VideoSurfaceRegistry.MaxSurfaces];
        for (int i = 0; i < Iterations; i++)
        {
            reg.Bind(token, (nuint)(0x10 + (i & 7)));
            reg.Place(token, new RectF(i & 63, 0f, 100f, 50f));
            reg.SnapshotInto(buf);
            reg.PumpPending(1f);
        }
        render.Join();

        Assert.Null(failure);
        Assert.Equal(Iterations, renderPosts);
        Assert.True(maxMail <= VideoSurfaceRegistry.MaxSurfaces);
        reg.PumpPending(1f);
        Assert.False(reg.HasPendingPumps);   // every post was folded in: nothing is left to drift
        Assert.False(reg.Surface(token).Peek().IsNone);
    }
}
