using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The DEBUG incremental-capture self-check (<c>SceneRecordingSnapshot.VerifyIncrementalParity</c>) re-derives a FULL
/// capture into a scratch snapshot and compares every column. Run on EVERY incremental publish it cost ~1.8 ms and
/// ~2.2 MB per publish even on the gallery's 412-node list page (67 gen-0 GCs a second in a Debug soak) — the margin
/// every Debug measurement is taken against. Its cadence is now bounded and deterministic: the first incremental capture
/// of a streak is always verified, then every Nth, N = max(8, ceil(capturedNodes / 256)). A missing NoteCaptureChanged
/// leaves a divergence that persists until the node is captured again, so it is still caught within N publishes.
/// </summary>
public sealed class CaptureParityBudgetTests
{
    // Read through a field, not the const, so neither build arm sees unreachable code.
    private static readonly bool VerifierCompiledIn = SceneRecordingSnapshot.ParityVerifyCompiledIn;

    private static (SceneStore Scene, NodeHandle Last) Scene(int boxes)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(1);
        NodeHandle last = default;
        for (int i = 0; i < boxes; i++)
        {
            last = scene.CreateNode(2);
            scene.AppendChild(scene.Root, last);
            scene.Bounds(last) = new RectF(0, i * 10, 100, 10);
        }
        return (scene, last);
    }

    [Fact]
    public void OnALargeSceneTheSelfCheckRunsAtMostOncePerWindowOfPublishes()
    {
        if (!VerifierCompiledIn) return;
        var (scene, last) = Scene(20_000);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        ulong seq = 1;
        for (int i = 0; i < 16; i++)
        {
            scene.NotePublished(seq);
            scene.Paint(last).Opacity = i / 16f;
            scene.NoteCaptureChanged((int)last.Raw.Index);
            Assert.True(snapshot.CaptureIncremental(scene, [], seq));
            seq++;
        }
        // N = max(8, ceil(20_001 / 256)) = 79 ⇒ only the streak's first of 16 incremental publishes is verified (was 16).
        Assert.InRange(snapshot.ParityVerifications, 1, 2);
        Assert.Equal(0, snapshot.IncrementalParityFailures);
    }

    [Fact]
    public void TheFirstIncrementalPublishOfAStreakIsAlwaysVerified_ThenEveryEighthOnASmallScene()
    {
        if (!VerifierCompiledIn) return;
        var (scene, last) = Scene(200);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        for (ulong seq = 1; seq <= 17; seq++)
        {
            scene.NotePublished(seq);
            scene.Paint(last).Opacity = seq / 32f;
            scene.NoteCaptureChanged((int)last.Raw.Index);
            Assert.True(snapshot.CaptureIncremental(scene, [], seq));
            if (seq == 1) Assert.Equal(1, snapshot.ParityVerifications);   // a fixture's first incremental is checked
        }
        Assert.Equal(3, snapshot.ParityVerifications);                     // publishes 1, 8 and 16 of the streak
    }

    [Fact]
    public void AMissingCaptureNoteIsStillCaughtWithinTheWindow_AndTheFrameHeals()
    {
        if (!VerifierCompiledIn) return;
        var (scene, last) = Scene(20_000);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        scene.Bounds(last) = new RectF(7, 7, 7, 7);   // a column write WITHOUT NoteCaptureChanged — the bug class
        for (ulong seq = 1; seq <= 10; seq++)
        {
            scene.NotePublished(seq);
            Assert.True(snapshot.CaptureIncremental(scene, [], seq));
        }
        Assert.Equal(1, snapshot.IncrementalParityFailures);
        Assert.Equal(new RectF(7, 7, 7, 7), snapshot.Bounds(last));   // redone as a full capture: correct again
    }
}
