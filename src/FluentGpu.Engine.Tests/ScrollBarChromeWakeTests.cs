using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Idle-power gate for the scroll-v3 wake mechanism (scroll-v3-plan §4), covering BOTH directions of the
/// bug: (1) a parked cursor resting over a scrollable, already-revealed scrollbar must not, by itself, keep telling
/// <c>AppHost.ComputeWakeReasons</c> that there is chrome animation work every single frame; (2) a genuinely FRESH
/// reveal (a real hover-enter, or a real scroll) must be visible to <see cref="ScrollBarChrome.NeedsFrame"/> on the
/// SAME call that arms it — BEFORE any <see cref="ScrollBarChrome.Tick"/> has run — since <c>NeedsFrame</c> is what
/// the host reads to decide whether to run a <c>Tick</c> at all; a one-frame-late signal would mean the loop is
/// never woken for the very first frame of a reveal (VerticalSlice gates 38a / chrome-fade-expand /
/// nonScrollableBarRetires all regressed this way when <c>NeedsFrame</c> was a Tick-end-only snapshot).
/// <see cref="ScrollBarChrome.Active"/> is raw membership (can be true purely because something re-armed the row,
/// changed or not); <see cref="ScrollBarChrome.NeedsFrame"/> is the change-gated wake-purposed signal — see the
/// property's own remarks.</summary>
public sealed class ScrollBarChromeWakeTests
{
    private static NodeHandle MakeScrollableNode(SceneStore scene, out int index)
    {
        scene.Root = scene.CreateNode(1);
        NodeHandle node = scene.Root;
        ref var sc = ref scene.ScrollRef(node);
        sc.Orientation = 0;          // vertical
        sc.ContentH = 1000f;
        sc.ViewportH = 200f;         // overflow 800 dip >> MinBarOverflowPx
        index = (int)node.Raw.Index;
        return node;
    }

    [Fact]
    public void FreshHoverEnter_NeedsFrame_IsTrueBeforeAnyTickRuns()
    {
        var scene = new SceneStore();
        MakeScrollableNode(scene, out int node);
        var chrome = new ScrollBarChrome(scene);
        chrome.FrameIndex = 1;

        // A genuine hover-enter (PointerOver flips false → true) must be visible to NeedsFrame IMMEDIATELY — before
        // Tick ever runs — because NeedsFrame is what the host reads to decide whether to run a Tick at all.
        Assert.False(chrome.NeedsFrame);
        chrome.SetPointerOver(node, over: true, overLane: false);
        Assert.True(chrome.NeedsFrame, "a real PointerOver flip must wake the loop on the same call that armed it");
    }

    [Fact]
    public void FreshScrollMotion_NeedsFrame_IsTrueBeforeAnyTickRuns()
    {
        var scene = new SceneStore();
        MakeScrollableNode(scene, out int node);
        var chrome = new ScrollBarChrome(scene);
        chrome.FrameIndex = 1;

        Assert.False(chrome.NeedsFrame);
        chrome.NotifyMoved(node, moved: true);
        Assert.True(chrome.NeedsFrame, "a real scroll/wheel/touch delta must wake the loop on the same call that reported it");
    }

    [Fact]
    public void HoveredIdleBar_StaysActive_ButDoesNotNeedAFrame_AfterASettlingTick()
    {
        var scene = new SceneStore();
        MakeScrollableNode(scene, out int node);
        var chrome = new ScrollBarChrome(scene);
        chrome.FrameIndex = 1;

        // Hover reveals the bar; one big-dtMs Tick is enough for the 83ms fade to fully settle (ExpandTarget stays
        // 0 — this hover never touches the lane), so the row already retires out of _active this same call and
        // consumes the "just armed" latch.
        chrome.SetPointerOver(node, over: true, overLane: false);
        chrome.Tick(dtMs: 500f);
        Assert.False(chrome.Active);
        Assert.False(chrome.NeedsFrame);

        // Simulate a hover-hit-test poll that re-reports the SAME (unchanged) over=true every frame, the way a
        // continuous PointerOver refresh would, WITHOUT an intervening Tick(). This re-arms membership...
        chrome.SetPointerOver(node, over: true, overLane: false);
        Assert.True(chrome.Active, "a freshly re-armed row is Active by definition (it is a member again)");

        // ...but NeedsFrame must still read false: the over/overLane VALUES did not actually change, and nothing
        // else has happened since the last Tick() settled it, so there is no real animation/dwell work pending.
        // This is exactly the bug scroll-v3's idle-power finding describes — Active alone would tell AppHost to
        // wake for this row forever.
        Assert.False(chrome.NeedsFrame, "a re-armed-but-unchanged hovered idle bar must not need a frame");
    }

    [Fact]
    public void MidFade_NeedsFrame_IsTrue()
    {
        var scene = new SceneStore();
        MakeScrollableNode(scene, out int node);
        var chrome = new ScrollBarChrome(scene);
        chrome.FrameIndex = 1;

        chrome.SetPointerOver(node, over: true, overLane: false);
        // dtMs well under FadeMs (83ms): FadeT advances toward 1 but does not reach it this call, so the row must
        // remain armed for another frame.
        chrome.Tick(dtMs: 10f);

        Assert.True(chrome.Active);
        Assert.True(chrome.NeedsFrame, "a bar mid-fade genuinely needs another frame to finish animating");
    }

    [Fact]
    public void UnhoveredNonScrollableBar_SettlesToNoWork()
    {
        var scene = new SceneStore();
        scene.Root = scene.CreateNode(1);
        NodeHandle handle = scene.Root;
        ref var sc = ref scene.ScrollRef(handle);
        sc.ContentH = 100f;
        sc.ViewportH = 100f;   // no overflow — never scrollable
        int node = (int)handle.Raw.Index;

        var chrome = new ScrollBarChrome(scene);
        chrome.FrameIndex = 1;
        chrome.SetPointerOver(node, over: true, overLane: false);
        chrome.Tick(dtMs: 500f);

        Assert.False(chrome.Active);
        Assert.False(chrome.NeedsFrame);
    }
}
