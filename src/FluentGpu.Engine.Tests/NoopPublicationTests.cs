using System;
using System.Reflection;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The no-op publication skip (<see cref="NoopPublicationGate"/>): under a render thread the UI publishes a scene only when
/// the frame produced something a publication carries. Pinned through the REAL host with the force-sync render loop a
/// windowed host would have (<c>AppHost.InstallRenderThreadForTest</c>): a wake that changed nothing publishes nothing, and
/// every real change (and every input the publication key carries) still publishes on the frame that made it. Also the
/// warm-cadence idle turn. Serial: it constructs hosts and touches process-static theme state.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class NoopPublicationTests
{
    /// <summary>The most publications a settled scene may still make after one change, while the change's record-dirty
    /// and removal ledgers retire across consumed publications.</summary>
    private const int MaxTrailingPublications = 3;

    private sealed class Root : Component
    {
        public readonly Signal<float> W = new(200f);
        public readonly Signal<ColorF> Fill = new(ColorF.FromRgba(20, 60, 120));
        public override Element Render() => new BoxEl
        {
            Width = W.Value, Height = 100f, Fill = Fill.Value,
            HoverFill = ColorF.FromRgba(60, 120, 200), OnClick = static () => { },
        };
    }

    private sealed class ScrollRoot : Component
    {
        public override Element Render() => Ui.ScrollView(new BoxEl { Height = 20_000f, Direction = 1 });
    }

    /// <summary>A decoder whose completions land only once <see cref="Open"/> is set, so a decode can complete on a frame of
    /// the test's choosing (the stock fake completes on the very next pump).</summary>
    private sealed class GatedDecoder : IImageDecoder
    {
        private readonly FakeImageDecoder _inner = new();
        public bool Open;
        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
            => _inner.Begin(id, source, targetW, targetH, priority);
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            if (Open) _inner.Pump(onComplete, onPixels);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessGpuDevice Device = new();
        public readonly HeadlessWindow Window;
        public readonly AppHost Host;

        public Rig(Component? root = null, ImageCache? images = null, bool warmCadence = false)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(Strings), Strings, root ?? new Root(), images: images);
            Host.InstallRenderThreadForTest();
            Host.WarmCadenceEnabledForTest = warmCadence;
            Host.RunFrame();
            Assert.True(Host.ScenePublishSeqForTest >= 1, "the first frame publishes");
        }

        public ulong Seq => Host.ScenePublishSeqForTest;

        public ulong WakeAndRun()
        {
            Host.WakeFrameForTest();
            Host.RunFrame();
            return Host.ScenePublishSeqForTest;
        }

        /// <summary>Bare wakes until one publishes nothing; returns how many of them still published.</summary>
        public int Settle(int max = 16)
        {
            for (int published = 0; published <= max; published++)
            {
                ulong before = Seq;
                if (WakeAndRun() == before) return published;
            }
            throw new Xunit.Sdk.XunitException("the host never stopped publishing on bare wakes:" + Host.NoopBlockedCensusForTest()
                + " wake=" + Host.CurrentWakeReasons);
        }

        /// <summary>Settle, do <paramref name="change"/>, run the frame it wakes, and assert that frame published.</summary>
        public void AssertPublishes(Action change, string what, int settleMax = 16)
        {
            Settle(settleMax);
            ulong before = Seq;
            change();
            Assert.True(WakeAndRun() > before, what + " publishes");
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    // ── the skip itself ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABareWake_AfterTheSceneSettled_PublishesNothing()
    {
        using var rig = new Rig();
        Assert.InRange(rig.Settle(), 0, MaxTrailingPublications);

        ulong seq = rig.Seq;
        long elided = rig.Host.NoopPublicationsElided;
        int presents = rig.Device.PrimarySwapchain!.PresentCount;
        for (int i = 0; i < 5; i++) Assert.Equal(seq, rig.WakeAndRun());
        Assert.Equal(elided + 5, rig.Host.NoopPublicationsElided);
        Assert.Equal(presents, rig.Device.PrimarySwapchain!.PresentCount);   // nothing reached the render thread either
        // DEBUG builds re-derive an elided frame in full and compare it with the last publication; it must agree.
        Assert.Equal(0, rig.Host.NoopParityFailures);
        if (SceneRecordingSnapshot.ParityVerifyCompiledIn) Assert.True(rig.Host.NoopParityVerifications > 0);
    }

    [Fact]
    public void AChange_LeavesAtMostAFewTrailingPublications()
    {
        var root = new Root();
        using var rig = new Rig(root);
        rig.Settle();
        root.Fill.Value = ColorF.FromRgba(200, 30, 30);
        rig.Host.RunFrame();
        Assert.InRange(rig.Settle(), 0, MaxTrailingPublications);
        root.W.Value = 260f;
        rig.Host.RunFrame();
        Assert.InRange(rig.Settle(), 0, MaxTrailingPublications);
    }

    [Fact]
    public void ALayoutChange_And_APaintOnlyChange_PublishOnTheFrameThatMadeThem()
    {
        var root = new Root();
        using var rig = new Rig(root);
        rig.AssertPublishes(() => root.W.Value = 260f, "a re-render + relayout");
        rig.AssertPublishes(() => root.Fill.Value = ColorF.FromRgba(200, 30, 30), "a fill change");
        rig.Settle();
        ulong settled = rig.Seq;
        Assert.Equal(settled, rig.WakeAndRun());   // …and the skip resumes once the change's ledgers retired
    }

    [Fact]
    public void AFullRepaintRequest_Publishes()
    {
        using var rig = new Rig();
        rig.AssertPublishes(rig.Host.RequestFullRepaintOnce, "a repaint request");
    }

    [Fact]
    public void AFrameCaptureRequest_OnASettledScene_PublishesAndLands()
    {
        using var rig = new Rig();
        rig.Settle();
        rig.Host.RequestFrameCapture();
        rig.WakeAndRun();
        Assert.True(rig.Host.TryTakeFrameCapture(out var capture), "the armed capture completed on the publication it forced");
        Assert.NotNull(capture);
        Assert.False(rig.Host.FrameCapturePending);
    }

    // ── every member of the publication key ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResize_And_AScaleChange_Publish()
    {
        using var rig = new Rig();
        rig.AssertPublishes(() => rig.Window.ClientSizePx = new Size2(400, 300), "a resize");
        rig.AssertPublishes(() => rig.Window.Scale = 1.25f, "a scale change");
    }

    [Fact]
    public void ATargetInvalidation_Publishes()
    {
        using var rig = new Rig();
        rig.AssertPublishes(rig.Host.InvalidateRenderTargetForTest, "an invalidated render target (resize / DPI / device recovery)");
    }

    [Fact]
    public void AWindowBackgroundFlip_Publishes()
    {
        using var rig = new Rig();
        var field = typeof(Tok).GetField("_windowBg", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previous = field.GetValue(null);
        try
        {
            rig.AssertPublishes(() => Tok.SetWindowBackground(ColorF.FromRgba(1, 2, 3)), "a window-background (clear colour) flip");
        }
        finally { field.SetValue(null, previous); }
    }

    [Fact]
    public void AThemeSwitch_Publishes()
    {
        using var rig = new Rig();
        ThemeKind previous = Tok.Theme;
        try
        {
            rig.AssertPublishes(() => Tok.Use(previous == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark), "a theme switch");
        }
        finally { Tok.Use(previous); }
    }

    [Fact]
    public void ARecordingConfigurationChange_And_AnUnledgeredSceneField_Publish()
    {
        using var rig = new Rig();
        rig.AssertPublishes(() => SceneRecorder.ConfigureScrollbarArrowGlyphs(rig.Host.Scene, default, default, default, default, default),
            "a scrollbar-glyph configuration change");
        // A scene-level field the snapshot copies wholesale, with no ledger behind it: only the key sees it. (OverlayClip is
        // the same kind of field, but the connected-animation tick re-derives it every frame, and it is finite only while a
        // fly is in flight, which already publishes through the overlay / detached-node gates.)
        rig.AssertPublishes(() => rig.Host.Scene.SpotlightScrimClip = new RectF(0, 0, 100, 100), "a spotlight scrim clip change");
    }

    [Fact]
    public void ACompositorRow_Start_Retarget_And_Park_EachPublish()
    {
        using var rig = new Rig();
        var node = rig.Host.Scene.Root;
        var anim = rig.Host.Animation;

        rig.Settle();
        ulong fp = anim.CompositorCaptureFingerprint();
        ulong seq = rig.Seq;
        anim.Animate(node, AnimChannel.Opacity, 1f, 0.5f, 10_000f);
        ulong started = anim.CompositorCaptureFingerprint();
        Assert.NotEqual(fp, started);
        Assert.True(rig.WakeAndRun() > seq, "a compositor row's start publishes");

        seq = rig.Seq;
        anim.Animate(node, AnimChannel.Opacity, 0.5f, 0.2f, 10_000f);   // a retarget of the same row
        ulong retargeted = anim.CompositorCaptureFingerprint();
        Assert.NotEqual(started, retargeted);
        Assert.True(rig.WakeAndRun() > seq, "a compositor row's retarget publishes");

        seq = rig.Seq;
        anim.SetNodeParked(node, true);
        Assert.NotEqual(retargeted, anim.CompositorCaptureFingerprint());
        Assert.True(rig.WakeAndRun() > seq, "a compositor row's park publishes");
    }

    [Fact]
    public void ACaretBlink_Publishes_ButTheTicksBetweenBlinksDoNot()
    {
        using var rig = new Rig();
        rig.Settle();
        rig.Host.CaretBlinker.Focus(rig.Host.Scene.Root);
        ulong start = rig.Seq;
        // 100 frames of the fixed 16 ms headless step span three 500 ms blink edges: each edge (and the focus itself)
        // publishes with its short trail; the ~30 frames between edges do not.
        for (int i = 0; i < 100; i++) rig.WakeAndRun();
        long published = (long)(rig.Seq - start);
        Assert.InRange(published, 3, 4 * (1 + MaxTrailingPublications));
    }

    [Fact]
    public void AScrollTo_Publishes()
    {
        using var rig = new Rig(new ScrollRoot());
        var scene = rig.Host.Scene;
        NodeHandle vp = default;
        for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
        {
            var h = scene.HandleAt(i);
            if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
        }
        Assert.False(vp.IsNull);
        var handle = rig.Host.TryGetScrollHandle(vp)!;
        // The scrollbar chrome's own reveal/hide timeline runs ~2.5 s after mount (ScrollAnim, which always publishes).
        rig.AssertPublishes(() => handle.ScrollTo(4_000.0), "a scroll", settleMax: 400);
    }

    [Fact]
    public void AnImageRequest_ItsDecodeCompletion_AndItsEviction_EachPublish()
    {
        var decoder = new GatedDecoder();
        var images = new ImageCache(decoder);
        using var rig = new Rig(images: images);
        ImageHandle h = default;
        rig.AssertPublishes(() => h = images.Request("test://noop-publication.png", 16, 16), "an image request");
        Assert.Equal(ImageState.Pending, images.StateOf(h));

        rig.AssertPublishes(() => decoder.Open = true, "a decode completion");
        Assert.Equal(ImageState.Ready, images.StateOf(h));

        for (int i = 0; i < 160; i++) rig.WakeAndRun();   // past the just-landed grace window (headless image clock)
        rig.AssertPublishes(() => images.EvictToVramPressure(budgetBytes: 1_000, usedBytes: 10_000_000), "an eviction");
        Assert.NotEqual(ImageState.Ready, images.StateOf(h));
    }

    [Fact]
    public void APublicationsImageSnapshot_IsKept_UntilAnImageInputMoves()
    {
        var root = new Root();
        using var rig = new Rig(root);
        rig.Settle();
        // A re-render's own capture is full (a reconcile is a bulk mutation), but the publications that follow it while its
        // record-dirty ledger retires are incremental: their slots already hold an image snapshot of the same (empty) id
        // set at the same cache-wide input serial, so it is kept rather than rebuilt.
        int reused = rig.Host.ImageCapturesReusedForTest;
        root.Fill.Value = ColorF.FromRgba(200, 30, 30);
        rig.Host.RunFrame();
        rig.Settle();
        int afterSettle = rig.Host.ImageCapturesReusedForTest;
        Assert.True(afterSettle > reused, $"the incremental publications reuse the image snapshot ({reused} -> {afterSettle})");

        rig.AssertPublishes(() => rig.Host.Images.Request("test://noop-publication-reuse.png", 16, 16), "an image input change");
        Assert.Equal(afterSettle, rig.Host.ImageCapturesReusedForTest);   // …and that capture rebuilt its image snapshot
    }

    // ── warm cadence ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWarmHold_RunsIdleTurns_AdvancingTheClockOncePerTurn_AndHoverStillPublishes()
    {
        using var rig = new Rig(warmCadence: true);
        rig.Settle();
        // A press and release outside the box: an interaction edge that arms the hold and changes nothing on screen.
        rig.Window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(300f, 200f), 0, 0));
        rig.Window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(300f, 200f), 0, 0));
        rig.Host.RunFrame();
        Assert.True((rig.Host.CurrentWakeReasons & WakeReasons.WarmCadence) != 0, "the hold is armed");
        for (int i = 0; i < 8 && rig.Host.CurrentWakeReasons != WakeReasons.WarmCadence; i++) rig.Host.RunFrame();
        Assert.Equal(WakeReasons.WarmCadence, rig.Host.CurrentWakeReasons);

        ulong seq = rig.Seq;
        for (int i = 0; i < 5; i++)
        {
            long idle = rig.Host.WarmCadenceIdleTurns;
            double clock = rig.Host.FrameClockMsForTest;
            rig.Host.RunFrame();                                          // no wake of its own: the hold alone
            Assert.Equal(idle + 1, rig.Host.WarmCadenceIdleTurns);
            Assert.Equal(16.0, rig.Host.FrameClockMsForTest - clock, 3);  // exactly one fixed step per turn
        }
        Assert.Equal(seq, rig.Seq);                                       // and nothing was published

        rig.Window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(10f, 10f), 0, 0));
        rig.Host.RunFrame();
        Assert.True(rig.Seq > seq, "hovering the box during the hold publishes");
    }

    // ── cost ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheKeyComparison_AndTheCoverageCompare_AllocateNothing()
    {
        using var rig = new Rig();
        rig.Settle();
        var a = new ScrollCoverageTable();
        var b = new ScrollCoverageTable();
        a.AddRow(new ScrollCoverageRow(1, 1, 2, 0, 0, 100, 50, 400, false, 0, 0, 0));
        b.CopyFrom(a);
        int hits = 0;
        for (int i = 0; i < 4; i++) { rig.Host.NoopKeyMatchesForTest(); a.ContentEquals(b); }   // warm (JIT tiering)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            if (a.ContentEquals(b)) hits++;
            rig.Host.NoopKeyMatchesForTest();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1000, hits);
        Assert.Equal(0, allocated);
    }

    // ── pure pieces ────────────────────────────────────────────────────────────────────────────────────────────────

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
