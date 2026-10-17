using FluentGpu.Hosting.Tiers;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A measured tier source that crosses during a RESIZED frame measured a layout that mixes the new window size with tiers that
/// commit one frame later (a nav pane still at its old width). Its crossing waits one more layout instead of committing on the
/// first eligible frame, so the next layout can confirm it or cross back and cancel it: a page never flashes the other arm.
/// </summary>
public sealed class TierRegistryUnsettledTests
{
    [Fact]
    public void A_measured_crossing_from_a_resized_frame_waits_one_layout_and_a_cross_back_cancels_it()
    {
        int frames = 0;
        var registry = new TierRegistry(() => frames++);
        var slot = registry.Create();

        registry.FrameResized = true;                 // the resized frame's layout measures a stale, narrower width
        registry.Store(slot, 1, measured: true);
        registry.FrameResized = false;                // the first eligible frame
        Assert.Equal(0, registry.CommitPending());
        Assert.Equal(0, slot.Committed.Peek());
        Assert.True(slot.IsPending);
        registry.Cancel(slot);                        // that frame's layout measures the real width: back on the committed tier
        Assert.Equal(0, registry.CommitPending());
        Assert.Equal(0, slot.Committed.Peek());
        Assert.False(registry.HasPending);
    }

    [Fact]
    public void A_measured_crossing_from_a_resized_frame_lands_one_frame_later_when_it_holds()
    {
        var registry = new TierRegistry(() => { });
        var slot = registry.Create();

        registry.FrameResized = true;
        registry.Store(slot, 2, measured: true);
        registry.FrameResized = false;
        Assert.Equal(0, registry.CommitPending());    // waits one layout
        Assert.Equal(1, registry.CommitPending());    // nothing crossed back: it lands
        Assert.Equal(2, slot.Committed.Peek());
        Assert.False(registry.HasPending);
    }

    [Fact]
    public void A_viewport_crossing_and_a_measured_one_from_a_settled_frame_commit_on_the_first_eligible_frame()
    {
        var registry = new TierRegistry(() => { });
        var viewport = registry.Create();
        var measured = registry.Create();

        registry.FrameResized = true;
        registry.Store(viewport, 1);                  // the viewport is right on a resized frame
        registry.FrameResized = false;
        Assert.Equal(1, registry.CommitPending());
        Assert.Equal(1, viewport.Committed.Peek());
        registry.FrameResized = false;                // a later, settled frame with no commit of its own
        registry.Store(measured, 3, measured: true);
        Assert.Equal(1, registry.CommitPending());
        Assert.Equal(3, measured.Committed.Peek());
    }

    [Fact]
    public void A_measured_crossing_found_in_the_frame_that_committed_a_tier_waits_one_layout()
    {
        var registry = new TierRegistry(() => { });
        var window = registry.Create();
        var page = registry.Create();

        registry.Store(window, 1);
        registry.FrameResized = false;                // frame C: the window tier commits ...
        Assert.Equal(1, registry.CommitPending());
        registry.Store(page, 1, measured: true);      // ... and this frame's layout still has the old pane width
        registry.FrameResized = false;                // frame C+1
        Assert.Equal(0, registry.CommitPending());    // the page waits
        registry.Cancel(page);                        // C+1's layout, beside the new pane, crosses back
        Assert.Equal(0, page.Committed.Peek());
        Assert.False(registry.HasPending);
    }

    [Fact]
    public void A_measured_crossing_stored_while_another_crossing_is_pending_waits_one_layout()
    {
        var registry = new TierRegistry(() => { });
        var window = registry.Create();
        var page = registry.Create();

        registry.Store(window, 1);                    // the window tier crossed: the nav pane narrows only when it commits
        registry.Store(page, 1, measured: true);      // the page measured beside the old pane: a stale, narrower width
        Assert.Equal(1, registry.CommitPending());    // the window tier lands; the page waits for a layout beside the new pane
        Assert.Equal(1, window.Committed.Peek());
        Assert.Equal(0, page.Committed.Peek());
        registry.Cancel(page);                        // that layout measures the real width: no crossing
        Assert.Equal(0, registry.CommitPending());
        Assert.Equal(0, page.Committed.Peek());
    }

    [Fact]
    public void A_settled_measure_confirms_an_unsettled_crossing_at_once()
    {
        var registry = new TierRegistry(() => { });
        var slot = registry.Create();

        registry.FrameResized = true;
        registry.Store(slot, 1, measured: true);
        registry.FrameResized = false;
        registry.Store(slot, 1, measured: true);      // the next layout measures the same side again: settled
        Assert.Equal(1, registry.CommitPending());
        Assert.Equal(1, slot.Committed.Peek());
    }
}
