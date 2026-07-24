using System;
using FluentGpu.Hosting.Threading;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Focused coverage for the render-thread seam's shutdown + detached-window routing (the async-render-flip work). The
/// full AppHost render thread is NOT constructible headlessly — AppHost gates the spawn on a non-headless window kind
/// (AppHost.cs), so a HeadlessWindow is always RenderLoopMode.SingleThread and never spawns one (neither ForceSync nor Async).
/// These tests therefore exercise the seam primitives directly (RenderThread + SceneFramePublisher), which is exactly
/// the machinery Change 1 (deterministic stop+join on close) and Change 2 (parent-thread child drain) are built on.
/// Live verification of an actual pop-out under async is separate (needs a real GPU/DRM runtime).
/// </summary>
public sealed class RenderThreadLifecycleTests
{
    // Change 1 (shutdown) + Change 2 (child drain via extraDrain) + the TryAcquire dedup the child-wake routing needs.
    [Fact]
    public void ForceSync_DrainSync_SubmitsOncePerPublish_AlwaysRunsExtraDrain_DisposeJoins_AndPostDisposeIsSafe()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        int submits = 0, drains = 0;
        // async:false = force-sync, so DrainSync blocks until the render thread's turn completes (deterministic, no sleeps).
        // submitPresent + extraDrain both run ON the render thread; DrainSync's _done wait publishes their writes to us.
        var rt = new RenderThread(seam, _ => submits++, async: false, extraDrain: () => drains++);
        try
        {
            // A published frame is submitted exactly once, and the child-drain callback runs on the same turn.
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(1, submits);
            Assert.True(drains >= 1, $"extraDrain should run on the turn (drains={drains})");

            // A bare wake with NO new publish must NOT re-submit the last frame (TryAcquire dedup — the invariant that lets
            // a detached child wake the parent thread without re-presenting the parent's stale frame), yet extraDrain STILL
            // runs (a child publish rides its own seam, drained by extraDrain even when the parent seam has nothing new).
            int submitsBefore = submits, drainsBefore = drains;
            rt.DrainSync();
            Assert.Equal(submitsBefore, submits);
            Assert.True(drains > drainsBefore, "extraDrain must run every turn, even with no new parent publish");

            // A fresh publish submits again (dedup only suppresses the ALREADY-consumed seq).
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(submitsBefore + 1, submits);
        }
        finally
        {
            // Change 1: Dispose stops + JOINS the render thread deterministically (bounded), and is idempotent.
            rt.Dispose();
            rt.Dispose();
            // Teardown-race safety: a still-armed wake / drain after the thread joined is a no-op, never an
            // ObjectDisposedException (a detached child's last publish can land after the parent thread was disposed).
            rt.WakeAsync();
            rt.DrainSync();
            rt.Quiesce();
            rt.Resume();
        }
    }

    // The dedup contract in isolation: the consumer is idempotent across bare acquires (no intervening publish).
    [Fact]
    public void SceneFramePublisher_TryAcquire_DedupsAnAlreadyConsumedFrame()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();

        Assert.False(seam.TryAcquire(out _), "nothing published yet");

        Span<byte> one = stackalloc byte[] { 7 };
        seam.Publish(one, default, default);
        Assert.True(seam.TryAcquire(out var f1), "first acquire after publish");
        Assert.Equal(1UL, f1.PublishSeq);

        // No new publish → the latest published frame is the one we already consumed → dedup returns false.
        Assert.False(seam.TryAcquire(out _), "bare re-acquire with no new publish must dedup");

        // A new publish is acquirable again.
        seam.Publish(one, default, default);
        Assert.True(seam.TryAcquire(out var f2));
        Assert.Equal(2UL, f2.PublishSeq);
        Assert.False(seam.TryAcquire(out _), "and dedups again");
    }
}
