using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The no-op publication skip (<see cref="NoopPublicationGate"/>): under a render thread the UI publishes a scene only when
/// the frame produced something a publication carries. Pinned through the REAL host with the force-sync render loop a
/// windowed host would have (<c>AppHost.InstallRenderThreadForTest</c>): a wake that changed nothing publishes nothing, and
/// any real change still publishes on the frame that made it. Serial: it constructs hosts (process-static seams).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class NoopPublicationTests
{
    private sealed class Root : Component
    {
        public readonly Signal<float> W = new(200f);
        public readonly Signal<ColorF> Fill = new(ColorF.FromRgba(20, 60, 120));
        public override Element Render() => new BoxEl { Width = W.Value, Height = 100f, Fill = Fill.Value };
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessGpuDevice Device = new();
        public readonly HeadlessWindow Window;
        public readonly Root Root = new();
        public readonly AppHost Host;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(Strings), Strings, Root);
            Host.InstallRenderThreadForTest();
        }

        /// <summary>Run frames until a bare wake stops publishing: the first publication and the record-dirty / removal
        /// ledgers it leaves behind retire over a few consumed publications, exactly as after any real change.</summary>
        public void Settle()
        {
            for (int i = 0; i < 16; i++)
            {
                ulong before = Host.ScenePublishSeqForTest;
                Host.WakeFrameForTest();
                Host.RunFrame();
                if (i > 0 && Host.ScenePublishSeqForTest == before) return;
            }
            throw new Xunit.Sdk.XunitException("the host never stopped publishing on bare wakes");
        }

        public ulong WakeAndRun()
        {
            Host.WakeFrameForTest();
            Host.RunFrame();
            return Host.ScenePublishSeqForTest;
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    [Fact]
    public void ABareWake_AfterTheSceneSettled_PublishesNothing()
    {
        using var rig = new Rig();
        rig.Host.RunFrame();
        Assert.True(rig.Host.ScenePublishSeqForTest >= 1, "the first frame publishes");
        rig.Settle();

        ulong seq = rig.Host.ScenePublishSeqForTest;
        long elided = rig.Host.NoopPublicationsElided;
        int presents = rig.Device.PrimarySwapchain!.PresentCount;
        for (int i = 0; i < 5; i++) Assert.Equal(seq, rig.WakeAndRun());
        Assert.Equal(elided + 5, rig.Host.NoopPublicationsElided);
        Assert.Equal(presents, rig.Device.PrimarySwapchain!.PresentCount);   // nothing reached the render thread either
    }

    [Fact]
    public void ALayoutChange_Publishes_OnTheFrameThatMadeIt()
    {
        using var rig = new Rig();
        rig.Host.RunFrame();
        rig.Settle();
        ulong seq = rig.Host.ScenePublishSeqForTest;

        rig.Root.W.Value = 260f;
        rig.Host.RunFrame();
        Assert.True(rig.Host.ScenePublishSeqForTest > seq, "a re-render + relayout publishes");
    }

    [Fact]
    public void APaintOnlyChange_Publishes_OnTheFrameThatMadeIt()
    {
        using var rig = new Rig();
        rig.Host.RunFrame();
        rig.Settle();
        ulong seq = rig.Host.ScenePublishSeqForTest;

        rig.Root.Fill.Value = ColorF.FromRgba(200, 30, 30);
        rig.Host.RunFrame();
        Assert.True(rig.Host.ScenePublishSeqForTest > seq, "a fill change publishes");
        // …and the scene settles again afterwards: the skip resumes once the change's ledgers retired.
        rig.Settle();
        ulong settled = rig.Host.ScenePublishSeqForTest;
        Assert.Equal(settled, rig.WakeAndRun());
    }

    [Fact]
    public void AFullRepaintRequest_Publishes()
    {
        using var rig = new Rig();
        rig.Host.RunFrame();
        rig.Settle();
        ulong seq = rig.Host.ScenePublishSeqForTest;

        rig.Host.RequestFullRepaintOnce();
        rig.Host.RunFrame();
        Assert.True(rig.Host.ScenePublishSeqForTest > seq, "a repaint request is never elided");
    }

    [Fact]
    public void AnUnledgeredImageInputChange_Publishes()
    {
        using var rig = new Rig();
        rig.Host.RunFrame();
        rig.Settle();
        ulong seq = rig.Host.ScenePublishSeqForTest;

        // Any image cache entry changing its recording inputs moves the cache-wide serial the publication key carries.
        rig.Host.Images.Request("test://noop-publication.png", 16, 16);
        Assert.True(rig.WakeAndRun() > seq, "an image recording-input change publishes");
    }

    [Fact]
    public void APublication_KeepsItsSlotsImageSnapshot_UntilAnImageInputMoves()
    {
        using var rig = new Rig();
        rig.Host.RunFrame();
        rig.Settle();

        // A re-render's own capture is full (a reconcile is a bulk mutation), but the publications that follow it while its
        // record-dirty ledger retires are incremental: their slots already hold an image snapshot of the same (empty) id
        // set at the same cache-wide input serial, so it is kept rather than rebuilt.
        int reused = rig.Host.ImageCapturesReusedForTest;
        rig.Root.Fill.Value = ColorF.FromRgba(200, 30, 30);
        rig.Host.RunFrame();
        rig.Settle();
        int afterSettle = rig.Host.ImageCapturesReusedForTest;
        Assert.True(afterSettle > reused, $"the incremental publications reuse the image snapshot ({reused} -> {afterSettle})");

        // An image input moving (a new cache entry) publishes by itself, and that capture rebuilds its image snapshot.
        ulong seq = rig.Host.ScenePublishSeqForTest;
        rig.Host.Images.Request("test://noop-publication-reuse.png", 16, 16);
        Assert.True(rig.WakeAndRun() > seq, "an image recording-input change publishes");
        Assert.Equal(afterSettle, rig.Host.ImageCapturesReusedForTest);
    }

    [Theory]
    [InlineData(WakeReasons.FrameNeeded, true)]
    [InlineData(WakeReasons.RuntimePending | WakeReasons.Timer, true)]
    [InlineData(WakeReasons.WarmCadence | WakeReasons.FrameClockPoller | WakeReasons.FrameClockPaceable, true)]
    [InlineData(WakeReasons.Caret | WakeReasons.DynamicText, true)]
    [InlineData(WakeReasons.Anim, false)]
    [InlineData(WakeReasons.ScrollAnim, false)]
    [InlineData(WakeReasons.ScrollProducer | WakeReasons.FrameNeeded, false)]
    [InlineData(WakeReasons.ImageReady, false)]
    [InlineData(WakeReasons.PopupAnim, false)]
    [InlineData(WakeReasons.Orphans, false)]
    [InlineData(WakeReasons.VideoPumpPending, false)]
    [InlineData(WakeReasons.FeedbackSettle, false)]
    public void OnlyLedgeredWakeReasons_MaySkip(WakeReasons wake, bool skippable)
        => Assert.Equal(skippable, NoopPublicationGate.WakeAllowsSkip(wake));

    [Fact]
    public void TheStoreReportsUnpublishedChanges_UntilThePublicationThatCarriesThem()
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var child = scene.CreateNode(2);
        scene.AppendChild(root, child);
        Assert.True(scene.HasUnpublishedChanges);
        scene.NotePublished(1);
        Assert.False(scene.HasUnpublishedChanges);

        scene.Mark(child, NodeFlags.PaintDirty);
        Assert.True(scene.HasUnpublishedChanges);
        scene.NotePublished(2);
        Assert.False(scene.HasUnpublishedChanges);

        scene.NoteBulkMutation();
        Assert.True(scene.HasUnpublishedChanges);
        scene.NotePublished(3);
        Assert.False(scene.HasUnpublishedChanges);
    }
}
