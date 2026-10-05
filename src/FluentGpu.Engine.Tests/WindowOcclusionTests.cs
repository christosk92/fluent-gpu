using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F118, the general case: any window parks while the UNION of the opaque top-level windows above it covers it completely (the
/// Win32 backend feeds the rects from <c>SetWinEventHook</c>-driven Z-order walks; the verdict is a pure rect test). The pure
/// <see cref="WindowCoverPolicy.CoveredByWindows"/> is pinned case by case; the host wiring (park, <c>InputHooks.WindowOccluded</c>,
/// the epoch-gated recompute, the un-park the moment the cover goes) is driven through real headless windows.
/// Serial: it constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class WindowOcclusionTests
{
    private static readonly RectF Window = new(100, 100, 1200, 800);
    private static readonly RectF Monitor = new(0, 0, 1920, 1080);

    private static bool Covered(RectF target, params RectF[] occluders) => WindowCoverPolicy.CoveredByWindows(target, occluders);

    // ── pure verdict ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneWindowContainingTheTarget_Covers()
        => Assert.True(Covered(Window, Monitor));

    [Fact]
    public void NoOccluders_OrAnEmptyTarget_NeverCover()
    {
        Assert.False(Covered(Window));
        Assert.False(Covered(default, Monitor));
        Assert.False(Covered(new RectF(0, 0, 1, 1), Monitor));
    }

    [Fact]
    public void TwoWindowsTiling_TheTargetBetweenThem_CoverItTogether()
    {
        var left = new RectF(0, 0, 700, 1080);
        var right = new RectF(700, 0, 1220, 1080);
        Assert.False(Covered(Window, left));
        Assert.False(Covered(Window, right));
        Assert.True(Covered(Window, left, right));
        Assert.True(Covered(Window, right, left));   // order does not matter
    }

    [Fact]
    public void ASeamWiderThanTheToleranceBetweenTwoWindows_LeavesTheTargetVisible()
    {
        var left = new RectF(0, 0, 690, 1080);
        var right = new RectF(760, 0, 1160, 1080);   // 70 px of desktop shows through between them
        Assert.False(Covered(Window, left, right));
    }

    [Fact]
    public void AnLShapedCover_LeavesTheUncoveredCornerVisible()
    {
        var top = new RectF(0, 0, 1920, 500);
        var leftStrip = new RectF(0, 500, 500, 580);   // the lower right of the target is still open
        Assert.False(Covered(Window, top, leftStrip));
        Assert.True(Covered(Window, top, new RectF(0, 500, 1920, 580)));
    }

    [Fact]
    public void OverlappingCoverers_AreAUnion_NotDoubleCounted()
    {
        var a = new RectF(0, 0, 800, 1080);
        var b = new RectF(600, 0, 800, 1080);
        var c = new RectF(1300, 0, 620, 1080);
        Assert.True(Covered(Window, a, b, c));
        Assert.False(Covered(Window, a, c));   // 800..1300 is open without the middle one
    }

    [Fact]
    public void AnOccluderThatMissesOrOnlyTouchesTheTarget_CoversNothing()
    {
        Assert.False(Covered(Window, new RectF(1400, 0, 400, 1080)));      // beside it
        Assert.False(Covered(Window, new RectF(1300, 100, 500, 800)));     // touches the right edge only
        Assert.False(Covered(Window, new RectF(0, 0, 0, 0)));              // empty
    }

    [Fact]
    public void ATargetOverhangingItsVisibleFrame_StillCountsAsCoveredWithinTheTolerance()
    {
        // A maximized window's outer rect overhangs its monitor by the invisible resize border (about 8 px; 16 px at 200%).
        Assert.True(Covered(new RectF(-8, -8, 1936, 1096), Monitor));
        Assert.True(Covered(new RectF(-16, -16, 1952, 1112), Monitor));
        // Strict (no tolerance): the overhang is uncovered.
        Assert.False(WindowCoverPolicy.CoveredByWindows(new RectF(-8, -8, 1936, 1096), new[] { Monitor }, tolerancePx: 0f));
        // Far past the tolerance: a window really sticking out onto another monitor is not covered.
        Assert.False(Covered(new RectF(1500, 100, 1200, 800), Monitor));
    }

    [Fact]
    public void OnlyTheFirstMaxOccludersAreUsed_SoAFlurryOfTinyWindowsNeverClaimsMoreThanItShouldAndABigOneFirstStillWins()
    {
        var all = new RectF[WindowCoverPolicy.MaxOccluders + 1];
        for (int i = 0; i < WindowCoverPolicy.MaxOccluders; i++) all[i] = new RectF(100 + i * 10, 100, 8, 8);
        all[WindowCoverPolicy.MaxOccluders] = Monitor;   // past the cap: ignored (under-reporting is the safe direction)
        Assert.False(WindowCoverPolicy.CoveredByWindows(Window, all));

        var bigFirst = new RectF[WindowCoverPolicy.MaxOccluders + 1];
        bigFirst[0] = Monitor;
        for (int i = 1; i < bigFirst.Length; i++) bigFirst[i] = new RectF(100 + i * 10, 100, 8, 8);
        Assert.True(WindowCoverPolicy.CoveredByWindows(Window, bigFirst));
    }

    // ── host wiring ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow ParentWindow { get; }
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }

        public Rig()
        {
            var strings = new StringTable();
            var device = new HeadlessGpuDevice();
            ParentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            ParentWindow.Show();
            ParentWindow.OuterBoundsPx = Window;
            Parent = new AppHost(App, ParentWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.Show();
            ChildWindow.OuterBoundsPx = new RectF(1500, 700, 320, 180);
            Child = new AppHost(App, ChildWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Parent.AdoptDetachedChild(Child);
            Child.RunFrame();
            Parent.RunFrame();
        }

        public void Dispose() { Parent.Dispose(); App.Dispose(); }
    }

    [Fact]
    public void AnUntrackedWindow_IsNeverParkedForACover()
    {
        using var rig = new Rig();   // headless reports epoch 0 until a test feeds occluders: the backend does not track
        for (int i = 0; i < 3; i++) rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
        Assert.Equal(0, rig.ParentWindow.OccluderQueries);
    }

    [Fact]
    public void AnotherWindowOverTheMainWindow_ParksIt_AndTheUncoverResumesAtOnce()
    {
        using var rig = new Rig();

        rig.ParentWindow.SetOccluders(Monitor);   // a maximized browser comes up over it
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);               // parks exactly like a minimized window
        Assert.True(rig.Parent.WindowOccludedForTest);  // and the visualizers / lyrics hear InputHooks.WindowOccluded
        Assert.True(rig.ParentWindow.IsVisible);        // the window itself is untouched
        Assert.InRange(rig.Parent.RecommendedWaitMs(), 1, 250);   // a backstop poll: the event normally wakes the loop first

        rig.ParentWindow.SetOccluders();                // it is closed / minimized / moved away: an event bumped the epoch
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);              // the very next frame
        Assert.False(rig.Parent.WindowOccludedForTest);
    }

    [Fact]
    public void ATilingOfWindows_ParksItJustLikeOneBigWindow_AndAGapDoesNot()
    {
        using var rig = new Rig();

        rig.ParentWindow.SetOccluders(new RectF(0, 0, 700, 1080), new RectF(700, 0, 1220, 1080));
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);

        rig.ParentWindow.SetOccluders(new RectF(0, 0, 690, 1080), new RectF(760, 0, 1160, 1080));
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
    }

    [Fact]
    public void ThePopOutItselfCountsAsAWindowAboveTheMainWindow()
    {
        using var rig = new Rig();
        // A windowed (not fullscreen) pop-out dragged over the whole main window: the fullscreen special case does not apply, the
        // general occlusion does, because the pop-out's rect is just another top-level window above it.
        rig.ParentWindow.SetOccluders(new RectF(50, 50, 1400, 900));
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);
    }

    [Fact]
    public void TheVerdictIsCachedPerEpoch_AQuietWindowDoesNotWalkTheZOrderEveryFrame()
    {
        using var rig = new Rig();
        rig.ParentWindow.SetOccluders(new RectF(0, 0, 100, 100));   // a small window: no cover
        for (int i = 0; i < 6; i++) rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
        Assert.Equal(1, rig.ParentWindow.OccluderQueries);          // one Z-order walk for the one epoch

        rig.ParentWindow.SetOccluders(Monitor);
        for (int i = 0; i < 6; i++) rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);
        Assert.Equal(2, rig.ParentWindow.OccluderQueries);
    }

    [Fact]
    public void AMinimizedOrHiddenWindow_IsNotWalked_ItsOwnGatesParkIt()
    {
        using var rig = new Rig();
        rig.ParentWindow.State = WindowState.Minimized;
        rig.ParentWindow.SetOccluders(Monitor);
        rig.Parent.RunFrame();
        Assert.Equal(0, rig.ParentWindow.OccluderQueries);   // not asked: nothing of it is on screen anyway
        Assert.True(rig.Parent.IsParked);                    // parked by the minimize gate, not by the cover
    }

    [Fact]
    public void AFullscreenPopOutCover_StillParks_AndAnOsCoverHeldBeside_ItDoesNotUnparkEarly()
    {
        using var rig = new Rig();
        rig.ChildWindow.SetFullscreen(true);
        rig.ChildWindow.OuterBoundsPx = Monitor;
        rig.ParentWindow.SetOccluders(Monitor);   // the OS also reports it (the pop-out is the topmost window above)
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);

        rig.ChildWindow.SetFullscreen(false);
        rig.ChildWindow.OuterBoundsPx = new RectF(960, 0, 960, 1080);
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);          // the OS verdict (cached for its epoch) still says covered

        rig.ParentWindow.SetOccluders(new RectF(960, 0, 960, 1080));   // the pop-out snapped: the epoch moved, half the window shows
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
    }

    [Fact]
    public void ADetachedChildParksWhenCovered_AndAnUnrevealedOneNeverDoes()
    {
        using var rig = new Rig();

        rig.ChildWindow.SetOccluders(new RectF(1000, 500, 1000, 600));   // another window over the pop-out
        rig.Child.RunFrame();
        Assert.True(rig.Child.IsParked);

        rig.ChildWindow.SetOccluders();
        rig.Child.RunFrame();
        Assert.False(rig.Child.IsParked);

        rig.Child.BeginDetachedReveal(alwaysOnTop: true, System.Diagnostics.Stopwatch.GetTimestamp(), windowCreateMs: 1, hostCtorMs: 1);
        rig.ChildWindow.SetOccluders(new RectF(1000, 500, 1000, 600));
        rig.Child.RunFrame();
        Assert.False(rig.Child.IsParked);   // nothing of a still-hidden pop-out is on screen, and its first frame has to paint
    }
}
