using System;
using System.Threading;
using FluentGpu.Hosting.Threading;
using Xunit;

namespace FluentGpu.Engine.Tests;

[Collection(SerialTestCollection.Name)]
public sealed class RenderThreadRecoveryControlTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PendingPark_SurvivesRecoveryAndItsConsumedWake(bool recoveryThrows, bool parkDuringRecovery)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var loss = new DeviceLostCoordinator();
        using var turnEntered = new ManualResetEventSlim();
        using var releaseTurn = new ManualResetEventSlim();
        using var recoveryEntered = new ManualResetEventSlim();
        using var releaseRecovery = new ManualResetEventSlim();
        using var parked = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int turns = 0, submits = 0, recoveries = 0;
        var rt = new RenderThread(seam, _ => Interlocked.Increment(ref submits), deviceLost: loss,
            recover: () =>
            {
                Interlocked.Increment(ref recoveries);
                recoveryEntered.Set();
                if (!releaseRecovery.Wait(10_000)) throw new TimeoutException("test recovery barrier");
                if (recoveryThrows) throw new InvalidOperationException("test recovery failure");
            },
            preTurn: () =>
            {
                if (Interlocked.Increment(ref turns) != 1) return;
                turnEntered.Set();
                if (!releaseTurn.Wait(10_000)) throw new TimeoutException("test turn barrier");
            });
        Exception? uiError = null;
        var ui = new Thread(() =>
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            try
            {
                rt.Quiesce();
                try
                {
                    parked.Set();
                    if (!resume.Wait(10_000)) throw new TimeoutException("test resume barrier");
                }
                finally { rt.Resume(); }
            }
            catch (Exception ex) { uiError = ex; }
        }) { IsBackground = true };
        bool uiStarted = false;
        try
        {
            rt.WakeAsync();
            Assert.True(turnEntered.Wait(5000), "initial turn never reached its barrier");
            loss.RecoverRequest = 1;
            if (parkDuringRecovery)
            {
                rt.WakeAsync();
                releaseTurn.Set();
                Assert.True(recoveryEntered.Wait(5000), "recovery never started");
            }
            ui.Start(); uiStarted = true;
            Assert.True(rt.ParkRequested.WaitOne(5000), "UI never requested the park");
            // ParkRequested is raised just BEFORE _wake.Set. Wait until Quiesce reached its acknowledgement wait
            // as well, so releasing the old turn cannot race a later nudge and accidentally hide the lost-wake bug.
            Assert.True(SpinWait.SpinUntil(() => (ui.ThreadState & ThreadState.WaitSleepJoin) != 0, 5000), "UI did not finish signalling its park request");
            releaseTurn.Set();
            Assert.True(recoveryEntered.Wait(5000), "recovery never started");
            Assert.False(parked.IsSet, "recovery must finish before ownership passes to the UI");
            releaseRecovery.Set();
            Assert.True(parked.Wait(5000), "pending park was lost after recovery consumed its wake");
            Assert.Equal(1, loss.RecoverDone);
            Assert.Equal(1, Volatile.Read(ref recoveries));
            resume.Set();
            Assert.True(ui.Join(5000), "UI did not resume");
            Assert.Null(uiError);
            loss.RecoverRequest = loss.RecoverDone = 0;

            seam.Publish(new byte[] { 1 }, default, default);
            rt.DrainSync();
            Assert.Equal(1, Volatile.Read(ref submits));
            rt.Quiesce();
            try { Assert.Equal(2, rt.QuiesceCount); }
            finally { rt.Resume(); }
            seam.Publish(new byte[] { 2 }, default, default);
            rt.DrainSync();
            Assert.Equal(2, Volatile.Read(ref submits));
        }
        finally
        {
            releaseTurn.Set(); releaseRecovery.Set(); resume.Set();
            // A failed regression must still clean up the old loop rather than leave its blocked UI thread behind.
            rt.WakeForVideo();
            if (uiStarted) Assert.True(ui.Join(5000), "UI cleanup did not finish");
            rt.Dispose();
        }
    }

    [Fact]
    public void StopDuringRecovery_JoinsWithoutAnotherWakeOrASecondRecovery()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var loss = new DeviceLostCoordinator { RecoverRequest = 1 };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stopRequested = new ManualResetEventSlim();
        int recoveries = 0, submits = 0;
        var rt = new RenderThread(new SceneFramePublisher(), _ => Interlocked.Increment(ref submits), deviceLost: loss,
            recover: () =>
            {
                Interlocked.Increment(ref recoveries);
                entered.Set();
                if (!release.Wait(10_000)) throw new TimeoutException("test stop barrier");
            });
        Exception? stopError = null;
        var stopping = new Thread(() =>
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            stopRequested.Set();
            try { rt.Dispose(); }
            catch (Exception ex) { stopError = ex; }
        }) { IsBackground = true };
        bool started = false;
        try
        {
            // An actionable request present before construction runs without requiring any nudge.
            Assert.True(entered.Wait(5000), "pending recovery waited for an unrelated wake");
            stopping.Start(); started = true;
            Assert.True(stopRequested.Wait(5000));
            Assert.True(SpinWait.SpinUntil(() => rt.StopRequested, 5000), "Dispose did not request stop while recovery was blocked");
            release.Set();
            Assert.True(stopping.Join(5000), "stop was lost when recovery returned");
            Assert.Null(stopError);
            Assert.Equal(1, Volatile.Read(ref recoveries));
            Assert.Equal(0, Volatile.Read(ref submits));
        }
        finally
        {
            release.Set();
            if (started) Assert.True(stopping.Join(5000));
            else rt.Dispose();
        }
    }
}
