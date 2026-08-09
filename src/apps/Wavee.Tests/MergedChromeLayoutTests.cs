using System;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Pins the merged chrome row's SPACE ALLOCATOR: that tabs have priority, the ORDER the row gives things up in as the
/// window narrows, the hysteresis band at every structural boundary, the floors nothing may fall below, and the two
/// invariants the allocator exists to guarantee — it never hands out more DIP than the window has, and the Friends
/// affordance is reachable at EVERY width (in the row XOR in the profile menu; it moves address rather than vanishing).
///
/// <para>Every boundary width here is DERIVED from the constants (<see cref="FirstWidthWhere"/>) rather than written
/// down, so retuning a plate width or a floor retunes the tests with it instead of breaking them.</para>
/// </summary>
public class MergedChromeLayoutTests
{
    const int Tabs = 8;
    const float SweepMax = 2600f;

    static MergedChromeLayout At(float w) => MergedChromeLayout.FromWidth(w, Tabs);

    /// <summary>The narrowest width at which the layout satisfies <paramref name="predicate"/> — the derived boundary
    /// every ordering and hysteresis assertion below is written against. The allocator is monotone in width, so this
    /// really is a boundary and not just the first of many.</summary>
    static float FirstWidthWhere(int tabCount, Func<MergedChromeLayout, bool> predicate)
    {
        for (float w = 0f; w <= 4000f; w += 1f)
            if (predicate(MergedChromeLayout.FromWidth(w, tabCount))) return w;
        return -1f;
    }

    static Func<MergedChromeLayout, bool> PredicateFor(string stage) => stage switch
    {
        "name" => l => l.ShowName,
        "friends" => l => l.ShowFriends,
        "forward" => l => l.ShowForward,
        "field" => l => l.SearchMode == MergedSearchMode.Field,
        _ when stage.StartsWith("keep", StringComparison.Ordinal)
            => l => l.KeepTabs >= int.Parse(stage.AsSpan(4)),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown stage"),
    };

    // ── THE REGRESSION: tabs have priority ───────────────────────────────────────────────────────────────────────────

    /// <summary>The reported defect, verified from a screenshot: at ~1500 device px (≈1000 DIP at 150%) with only TWO
    /// open tabs, one tab was evicted into the "⌄" overflow while the search sat at its full 420 flanked by large empty
    /// drag gutters. The old model asked "is the window ≥ 1160 DIP?" to allow a second tab — blind to the fact that
    /// two tabs at their floor need 220 DIP and there were 500 going spare.</summary>
    [Theory]
    [InlineData(1000f, 2)]      // the screenshot, in DIP
    [InlineData(1500f, 2)]      // the same scenario at 100% scale
    [InlineData(1280f, 3)]
    public void Regression_AGenerousWindowNeverOverflowsTabsItHasRoomFor(float width, int tabCount)
    {
        var l = MergedChromeLayout.FromWidth(width, tabCount);
        Assert.Equal(tabCount, l.KeepTabs);
        Assert.Equal(MergedSearchMode.Field, l.SearchMode);
        // …and the accounting behind it: the whole row still fits.
        Assert.True(l.FootprintFor(tabCount) <= width);
    }

    /// <summary>The general form of the same rule, and the allocator's defining equivalence: EVERY open tab renders
    /// exactly when every open tab fits at its floor with the search in its minimum guaranteed (icon) form.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void KeepTabs_HoldsEveryTabThatFitsAtItsFloor(int tabCount)
    {
        float floor = tabCount * ShellResponsiveLayout.ChromeTabMinW + ShellResponsiveLayout.ChromeSearchIconW;
        for (float w = 0f; w <= SweepMax; w += 7f)
        {
            var l = MergedChromeLayout.FromWidth(w, tabCount);
            if (MergedChromeLayout.FreeSpace(w) >= floor) Assert.Equal(tabCount, l.KeepTabs);
            // …and below the floor a tab really is folded away — except with ONE tab open, which never leaves.
            else if (tabCount > 1) Assert.True(l.KeepTabs < tabCount, $"nothing folded at {w} with {tabCount} tabs");
            else Assert.Equal(1, l.KeepTabs);
        }
    }

    /// <summary>The "⌄" is the LAST resort: it can only appear once the search has already surrendered to its icon.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void Chevron_OnlyAppearsAfterTheSearchHasAlreadyCollapsed(int tabCount)
    {
        for (float w = 0f; w <= SweepMax; w += 5f)
        {
            var l = MergedChromeLayout.FromWidth(w, tabCount);
            if (l.KeepTabs < tabCount)
                Assert.Equal(MergedSearchMode.Icon, l.SearchMode);
        }
    }

    // ── priority ORDER ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Read bottom-up, this is the ladder as authored: the last thing to arrive as the window widens is a
    /// wider TAB; before that the search reaching its comfortable maximum; before that the search becoming a field at
    /// all; and before all of them, every tab in the strip. Read top-down it is the give-way order — 420→220, then
    /// icon, and only then is a tab evicted.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Priority_TheSearchYieldsAllTheWayToAnIconBeforeATabIsEvicted(int tabCount)
    {
        float keepAll = FirstWidthWhere(tabCount, l => l.KeepTabs == tabCount);
        float field = FirstWidthWhere(tabCount, l => l.SearchMode == MergedSearchMode.Field);
        float comfort = FirstWidthWhere(tabCount, l => l.SearchWidth >= ShellResponsiveLayout.ChromeSearchMaxW);
        float widen = FirstWidthWhere(tabCount, l => l.TabMaxWidth > ShellResponsiveLayout.ChromeTabMinW);

        Assert.True(field > 0f && comfort > 0f && widen > 0f, "the ladder never reaches its top rungs");
        Assert.True(keepAll < field, $"a tab was evicted while the search was still a field ({keepAll} vs {field})");
        Assert.True(field < comfort, $"the field appeared at its comfortable width, skipping the 220→420 walk");
        Assert.True(comfort <= widen, $"tabs widened before the search was comfortable ({widen} vs {comfort})");
    }

    /// <summary>The same rule as a per-width invariant rather than an ordering: a tab is only ever wider than its floor
    /// when the search has ALREADY reached its comfortable maximum.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void Priority_TabsOnlyWidenOnceTheSearchIsComfortable(int tabCount)
    {
        for (float w = 0f; w <= SweepMax; w += 3f)
        {
            var l = MergedChromeLayout.FromWidth(w, tabCount);
            if (l.TabMaxWidth <= ShellResponsiveLayout.ChromeTabMinW) continue;
            Assert.Equal(MergedSearchMode.Field, l.SearchMode);
            Assert.Equal(ShellResponsiveLayout.ChromeSearchMaxW, l.SearchWidth);
        }
    }

    [Fact]
    public void Ladder_DropsNameBeforeFriendsLeavesTheRow()
    {
        Assert.True(At(1400f).ShowName);
        // The name is the FIRST thing to go — the Friends button is still standing in the row after it has gone.
        var justBelowName = At(ShellResponsiveLayout.ChromeNameEnterW - 1f);
        Assert.False(justBelowName.ShowName);
        Assert.True(justBelowName.FriendsInRow);
        Assert.False(justBelowName.FriendsInMenu);
    }

    [Fact]
    public void Ladder_MovesFriendsIntoTheMenuAtTheComfortBand()
    {
        // At/above the band Friends is a standalone island button …
        var wide = At(ShellResponsiveLayout.ChromeFriendsEnterW);      // 1000
        Assert.True(wide.FriendsInRow);
        Assert.False(wide.FriendsInMenu);

        // … and one DIP below it the button is gone but the affordance is NOT: it becomes a profile-menu row. That is
        // also the rung at which the identity island is a bare avatar (nothing else is width-gated any more — the bell
        // merged into the chip's badge and the theme toggle is an unconditional menu row).
        var narrow = At(ShellResponsiveLayout.ChromeFriendsEnterW - 1f);   // 999
        Assert.False(narrow.FriendsInRow);
        Assert.True(narrow.FriendsInMenu);
        Assert.True(narrow.BareAvatar);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(519f)]
    [InlineData(999f)]
    [InlineData(1000f)]
    [InlineData(1359f)]
    [InlineData(2400f)]
    public void Friends_AreReachableAtEveryWidth_ButtonXorMenuRow(float w)
        => Assert.True(At(w).FriendsInRow ^ At(w).FriendsInMenu);

    [Fact]
    public void Friends_AreNeverLostNorDuplicated_AcrossTheWholeSweep()
    {
        // The invariant the ProfileMenu's conditional row is built on: ShowFriends means "in the row", FriendsInMenu is
        // its exact complement, so at no width is the affordance absent from both — or present in both.
        for (float w = 0f; w <= SweepMax; w += 11f)
        {
            var l = MergedChromeLayout.FromWidth(w, Tabs);
            Assert.True(l.FriendsInRow ^ l.FriendsInMenu, $"friends were lost or duplicated at {w}");
            Assert.Equal(l.ShowFriends, l.FriendsInRow);
        }
    }

    [Fact]
    public void Ladder_OverflowsTabsBeforeTheAvatarGoesBare()
    {
        // With eight tabs open, the strip is already folding into the "⌄" at widths where identity is still complete.
        var wide = At(1250f);
        Assert.True(wide.KeepTabs < Tabs);
        Assert.True(wide.FriendsInRow);
        Assert.False(wide.BareAvatar);
        // The bare avatar is the LAST rung.
        Assert.True(At(600f).BareAvatar);
    }

    // ── the FIXED BUDGET ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Forward's rung costs the budget nothing: the button it removes from the row and the "⋯" it forces into
    /// the row are the same plate. The allocator relies on this — it is why the forward band cannot move a tab.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FixedBudget_TheForwardRungIsCostNeutral(bool name, bool friends)
        => Assert.Equal(MergedChromeLayout.FixedBudget(name, friends, forward: true),
                        MergedChromeLayout.FixedBudget(name, friends, forward: false));

    /// <summary>Each identity bit only ever ADDS to the budget, and the leanest configuration is the floor every other
    /// one is measured from.</summary>
    [Fact]
    public void FixedBudget_GrowsWithEveryIdentityBitItCarries()
    {
        float bare = MergedChromeLayout.FixedBudget(false, false, true);
        Assert.True(MergedChromeLayout.FixedBudget(true, false, true) > bare);
        Assert.True(MergedChromeLayout.FixedBudget(false, true, true) > bare);
        Assert.True(MergedChromeLayout.FixedBudget(true, true, true)
                    > MergedChromeLayout.FixedBudget(true, false, true));
    }

    /// <summary>The free space the tabs and the search share must be NON-DECREASING in width — otherwise a narrowing
    /// window would free budget (the name's 90 DIP arriving at 1360 is the real case) and could ADD a tab back. This is
    /// the property the running-infimum construction in <c>FreeSpace</c> exists to guarantee.</summary>
    [Fact]
    public void FreeSpace_NeverShrinksAsTheWindowWidens()
    {
        float previous = -1f;
        for (float w = 0f; w <= 3000f; w += 1f)
        {
            float free = MergedChromeLayout.FreeSpace(w);
            Assert.True(free >= previous, $"free space fell from {previous} to {free} at width {w}");
            previous = free;
        }
    }

    /// <summary>Space honesty: the row never claims more DIP than the window has. The one exemption is the documented
    /// FLOOR — one tab at its minimum plus an icon search plus the leanest fixed budget — which the allocator emits
    /// even into a window too small for it, because the active tab never leaves.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    public void Space_IsNeverOverAllocated(int tabCount)
    {
        float floor = MergedChromeLayout.MinimumFootprintFor(tabCount);
        for (float w = 0f; w <= SweepMax; w += 7f)
        {
            var l = MergedChromeLayout.FromWidth(w, tabCount);
            float used = l.FootprintFor(tabCount);
            Assert.True(used <= MathF.Max(w, floor) + 0.5f,
                        $"allocated {used} into a {w}-DIP row ({tabCount} tabs): {l}");
        }
    }

    /// <summary>The same honesty while a real resize DRAG walks the hysteresis: a HELD stage must also fit, in both
    /// directions. (A held stage is never richer than the candidate, which is what makes this true by construction —
    /// this pins it anyway, because that is the exact property a future edit to <c>Hold</c> would break.)</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(9)]
    public void Space_IsNeverOverAllocatedWhileHysteresisHolds(int tabCount)
    {
        float floor = MergedChromeLayout.MinimumFootprintFor(tabCount);
        MergedChromeLayout? held = null;
        for (float w = SweepMax; w >= 0f; w -= 9f) held = Step(w);
        for (float w = 0f; w <= SweepMax; w += 9f) held = Step(w);

        MergedChromeLayout Step(float w)
        {
            var l = MergedChromeLayout.Resolve(w, tabCount, held);
            Assert.True(l.FootprintFor(tabCount) <= MathF.Max(w, floor) + 0.5f,
                        $"held layout {l} overflows a {w}-DIP row");
            return l;
        }
    }

    // ── floors and ranges ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(200f)]
    [InlineData(400f)]
    [InlineData(719f)]
    public void Floors_AreNeverBreached(float w)
    {
        var l = At(w);
        Assert.True(l.SearchWidth >= ShellResponsiveLayout.ChromeSearchIconW);
        Assert.True(l.TabMaxWidth >= ShellResponsiveLayout.ChromeTabMinW);
        Assert.True(l.KeepTabs >= 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(40)]
    public void KeepTabs_IsAlwaysAtLeastOneAndNeverExceedsTheOpenCount(int tabCount)
    {
        for (float w = 0f; w <= SweepMax; w += 37f)
        {
            var l = MergedChromeLayout.FromWidth(w, tabCount);
            Assert.True(l.KeepTabs >= 1);
            if (tabCount > 0) Assert.True(l.KeepTabs <= tabCount);
        }
    }

    [Fact]
    public void SearchWidth_StaysInsideItsAuthoredRange()
    {
        for (float w = 0f; w <= 3000f; w += 13f)
        {
            var l = MergedChromeLayout.FromWidth(w, Tabs);
            if (l.SearchMode == MergedSearchMode.Field)
            {
                Assert.True(l.SearchWidth >= ShellResponsiveLayout.ChromeSearchMinW);
                Assert.True(l.SearchWidth <= ShellResponsiveLayout.ChromeSearchMaxW);
            }
            else
            {
                Assert.Equal(ShellResponsiveLayout.ChromeSearchIconW, l.SearchWidth);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public void TabMaxWidth_StaysInsideItsAuthoredRange(int tabCount)
    {
        for (float w = 0f; w <= 4000f; w += 13f)
        {
            var l = MergedChromeLayout.FromWidth(w, tabCount);
            Assert.True(l.TabMaxWidth >= ShellResponsiveLayout.ChromeTabMinW);
            Assert.True(l.TabMaxWidth <= ShellResponsiveLayout.ChromeTabMaxW);
        }
    }

    /// <summary>Both continuous outputs are snapped to <c>ChromeWidthQuantumW</c>. The ladder is a signal the shell
    /// publishes with SetIfChanged; without the snap a resize drag would re-render the title bar (and re-push its
    /// non-client regions) on every device pixel.</summary>
    [Fact]
    public void ContinuousWidths_AreQuantisedSoAResizeDragDoesNotRepublishPerPixel()
    {
        float q = ShellResponsiveLayout.ChromeWidthQuantumW;
        for (float w = 0f; w <= 3000f; w += 1f)
        {
            var l = MergedChromeLayout.FromWidth(w, Tabs);
            Assert.Equal(0f, l.TabMaxWidth % q);
            if (l.SearchMode == MergedSearchMode.Field) Assert.Equal(0f, l.SearchWidth % q);
            else Assert.Equal(ShellResponsiveLayout.ChromeSearchIconW, l.SearchWidth);
        }
    }

    // ── monotonicity: narrowing never adds anything ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(14)]
    public void Narrowing_NeverAddsAnElementBack(int tabCount)
    {
        MergedChromeLayout? previous = null;
        for (float w = SweepMax; w >= 0f; w -= 7f)
        {
            var l = MergedChromeLayout.FromWidth(w, tabCount);
            if (previous is { } p)
            {
                Assert.True(l.Richness <= p.Richness, $"richness grew while narrowing at {w}");
                Assert.True(!l.ShowName || p.ShowName);
                Assert.True(!l.ShowFriends || p.ShowFriends);
                Assert.True(!l.ShowForward || p.ShowForward);
                Assert.True(l.SearchWidth <= p.SearchWidth || l.SearchMode != p.SearchMode);
                Assert.True(l.TabMaxWidth <= p.TabMaxWidth, $"the tab cap grew while narrowing at {w}");
                Assert.True(l.KeepTabs <= p.KeepTabs, $"a tab came back while narrowing at {w}");
                if (l.SearchMode == MergedSearchMode.Field) Assert.Equal(MergedSearchMode.Field, p.SearchMode);
            }
            previous = l;
        }
    }

    // ── hysteresis: one mechanism, every structural boundary ─────────────────────────────────────────────────────────

    /// <summary>Every decision the promotion reserve has to make sticky — the three identity bits, the search's
    /// field↔icon shape, and each rung of KeepTabs. The boundary width is DERIVED per scenario rather than written
    /// down, because the space model has no authored thresholds left to name.</summary>
    [Theory]
    [InlineData("name", 8)]
    [InlineData("friends", 8)]
    [InlineData("forward", 8)]
    [InlineData("field", 8)]
    [InlineData("field", 2)]
    [InlineData("field", 1)]
    [InlineData("keep2", 8)]
    [InlineData("keep3", 8)]
    [InlineData("keep4", 8)]
    [InlineData("keep5", 8)]
    [InlineData("keep6", 8)]
    [InlineData("keep7", 8)]
    [InlineData("keep8", 8)]
    [InlineData("keep2", 2)]
    [InlineData("keep3", 3)]
    [InlineData("keep5", 5)]
    public void EveryStructuralBoundary_DemotesImmediatelyButPromotesOnlyWithReserve(string stage, int tabCount)
    {
        var has = PredicateFor(stage);
        float band = ShellResponsiveLayout.ChromePromotionHysteresisW;
        float boundary = FirstWidthWhere(tabCount, has);
        Assert.True(boundary > 0f, $"no width in range grants '{stage}' with {tabCount} tabs");

        var rich = MergedChromeLayout.FromWidth(boundary + band + 10f, tabCount);
        Assert.True(has(rich));

        // Coming DOWN through the boundary the richer stage is given up at once.
        var demoted = MergedChromeLayout.Resolve(boundary - 1f, tabCount, rich);
        Assert.False(has(demoted));
        Assert.Equal(MergedChromeLayout.FromWidth(boundary - 1f, tabCount), demoted);

        // Coming back UP, re-crossing the boundary is NOT enough: the promotion needs the full reserve.
        Assert.False(has(MergedChromeLayout.Resolve(boundary, tabCount, demoted)));
        Assert.False(has(MergedChromeLayout.Resolve(boundary + band - 1f, tabCount, demoted)));
        Assert.True(has(MergedChromeLayout.Resolve(boundary + band, tabCount, demoted)));
    }

    /// <summary>The reserve is a PROMOTION gate only — it must never make a widening window give something up. (The
    /// spurious-demotion guard: a reserve resolve can land below a lower boundary than the one being held.)</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void Widening_NeverDemotesAnythingTheRowWasAlreadyHolding(int tabCount)
    {
        var held = MergedChromeLayout.FromWidth(0f, tabCount);
        for (float w = 0f; w <= SweepMax; w += 3f)
        {
            var next = MergedChromeLayout.Resolve(w, tabCount, held);
            Assert.True(next.KeepTabs >= held.KeepTabs, $"a tab was dropped while widening at {w}");
            Assert.True(!held.ShowName || next.ShowName);
            Assert.True(!held.ShowFriends || next.ShowFriends);
            Assert.True(!held.ShowForward || next.ShowForward);
            if (held.SearchMode == MergedSearchMode.Field)
                Assert.Equal(MergedSearchMode.Field, next.SearchMode);
            held = next;
        }
    }

    /// <summary>The search's shape specifically: Field→Icon is immediate (nothing may clip while contracting) but
    /// Icon→Field waits out the full reserve.</summary>
    [Fact]
    public void Search_CollapsesImmediatelyButReopensOnlyWithTheReserve()
    {
        const int tabCount = 2;
        float band = ShellResponsiveLayout.ChromePromotionHysteresisW;
        float boundary = FirstWidthWhere(tabCount, l => l.SearchMode == MergedSearchMode.Field);

        var field = MergedChromeLayout.FromWidth(boundary + 200f, tabCount);
        Assert.Equal(MergedSearchMode.Field, field.SearchMode);

        var collapsed = MergedChromeLayout.Resolve(boundary - 1f, tabCount, field);
        Assert.Equal(MergedSearchMode.Icon, collapsed.SearchMode);
        Assert.Equal(ShellResponsiveLayout.ChromeSearchIconW, collapsed.SearchWidth);

        Assert.Equal(MergedSearchMode.Icon,
                     MergedChromeLayout.Resolve(boundary + band - 1f, tabCount, collapsed).SearchMode);
        var reopened = MergedChromeLayout.Resolve(boundary + band, tabCount, collapsed);
        Assert.Equal(MergedSearchMode.Field, reopened.SearchMode);
        Assert.True(reopened.SearchWidth >= ShellResponsiveLayout.ChromeSearchMinW);
    }

    /// <summary>The two CONTINUOUS outputs carry no reserve on purpose — they stretch rather than pop, so they track
    /// the live width under whatever stage is held instead of trailing a widening window by the band.</summary>
    [Fact]
    public void ContinuousWidths_TrackTheLiveWidthRatherThanLaggingByTheReserve()
    {
        const int tabCount = 2;
        var held = MergedChromeLayout.FromWidth(1100f, tabCount);
        for (float w = 1100f; w <= 1400f; w += 10f)
        {
            var live = MergedChromeLayout.Resolve(w, tabCount, held);
            var seed = MergedChromeLayout.FromWidth(w, tabCount);
            if (live.SearchMode == seed.SearchMode && live.KeepTabs == seed.KeepTabs)
                Assert.Equal(seed.SearchWidth, live.SearchWidth);
            held = live;
        }
    }

    [Fact]
    public void Forward_FoldsIntoOverflowOnTheHistoricalToolbarBand()
    {
        // The primary-nav band the two-row toolbar used (ToolbarNarrowEnterW/LeaveW = 520/560) is preserved: the
        // forward button drops at 520 and only comes back once the 40-DIP reserve is cleared.
        Assert.True(At(521f).ShowForward);
        Assert.False(At(ShellResponsiveLayout.ToolbarNarrowEnterW).ShowForward);

        var narrow = MergedChromeLayout.Resolve(500f, Tabs);
        Assert.False(narrow.ShowForward);
        Assert.False(MergedChromeLayout.Resolve(ShellResponsiveLayout.ToolbarNarrowLeaveW - 1f, Tabs, narrow).ShowForward);
        Assert.True(MergedChromeLayout.Resolve(ShellResponsiveLayout.ToolbarNarrowLeaveW + 1f, Tabs, narrow).ShowForward);
    }

    // ── seeds, clamps and the tab-count guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_WithoutAPreviousStateIsTheSeed()
    {
        for (float w = 0f; w <= 2200f; w += 53f)
            Assert.Equal(MergedChromeLayout.FromWidth(w, Tabs), MergedChromeLayout.Resolve(w, Tabs));
    }

    [Fact]
    public void Resolve_ClampsANegativeWidthRatherThanExtrapolating()
        => Assert.Equal(MergedChromeLayout.FromWidth(0f, Tabs), MergedChromeLayout.Resolve(-500f, Tabs));

    /// <summary>A tab CLOSING while the window is held must re-clamp the cap, or the strip would be told to show more
    /// tabs than are open. (The guard the threshold model grew; the space model keeps it.)</summary>
    [Fact]
    public void Resolve_ReClampsAHeldKeepWhenATabCloses()
    {
        var wide = MergedChromeLayout.FromWidth(2000f, 8);
        Assert.Equal(8, wide.KeepTabs);
        Assert.Equal(3, MergedChromeLayout.Resolve(2000f, 3, wide).KeepTabs);
        Assert.Equal(1, MergedChromeLayout.Resolve(2000f, 1, wide).KeepTabs);
    }
}
