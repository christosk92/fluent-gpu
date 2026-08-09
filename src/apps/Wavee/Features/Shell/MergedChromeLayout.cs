using System;

namespace Wavee;

/// <summary>Which shape the merged row's window-centred search takes at this width.</summary>
public enum MergedSearchMode : byte
{
    /// <summary>A real omnibar field, <c>SearchWidth</c> DIP wide.</summary>
    Field,
    /// <summary>A 32-DIP magnifier that CLICK-expands into a field (LibraryV3Search's affordance).</summary>
    Icon,
}

/// <summary>
/// The PURE space allocator behind Wavee's ONE 48-DIP chrome row. It answers a single question — at this window width,
/// with this many open tabs, what does the merged row still carry? — and it never returns a layout that needs more room
/// than the width it was handed.
///
/// <para><b>Space accounting, not a threshold table.</b> The previous model asked "is the window at least 1500 DIP?"
/// to decide how many tabs survive. That is blind to what the row is actually carrying: with only two tabs open it
/// evicted one into the "⌄" overflow at 1000 DIP while the search sat at its full 420 between two wide empty drag
/// gutters — and it never accounted for the tab strip's permanently reserved 32-DIP "+" slot at all. So the ladder now
/// SUBTRACTS a fixed budget from the width and hands out the remainder:</para>
/// <list type="number">
/// <item><b>Fixed budget</b> — <see cref="FixedBudget"/>: the bar lead, Back (+ Forward when it is in the row), the
/// reserved "+", the identity cluster as the ladder bits leave it, the two gutter floors, the guaranteed drag strip and
/// the caption cluster. Plus the "⌄" chevron, but only when a tab is actually going to be folded away.</item>
/// <item><b>Tabs claim first</b> — <see cref="KeepTabs"/> is the most open tabs that fit at <c>ChromeTabMinW</c> with
/// the search in its MINIMUM GUARANTEED form (the 32-DIP icon). Every tab that fits at the floor renders; the "⌄"
/// appears only when even tab-at-floor + search-as-icon does not fit. The active tab never leaves (KeepTabs ≥ 1).</item>
/// <item><b>Search gets the remainder</b> — leftover after the tabs are at their floor becomes the field width, clamped
/// to [<c>ChromeSearchMinW</c>, <c>ChromeSearchMaxW</c>]; below the minimum it is the icon. So the search yields in this
/// order — 420 → 220, then icon — and only THEN is a tab evicted.</item>
/// <item><b>Tabs widen with the surplus</b> — anything still spare once the search has reached its comfortable
/// <c>ChromeSearchMaxW</c> grows <see cref="TabMaxWidth"/> from 110 toward 200; past that the bar's grow bands absorb
/// the rest as drag space.</item>
/// </list>
///
/// <para><b>The three identity bits stay width-banded</b> (<see cref="ShowName"/> at <c>ChromeNameEnterW</c>,
/// <see cref="ShowFriends"/> at <c>ChromeFriendsEnterW</c>, <see cref="ShowForward"/> at <c>ChromeForwardEnterW</c>):
/// they are cheap fixed-width toggles and their bands are the app's authored comfort points. But their WIDTHS feed the
/// budget, so the accounting is honest across them — see <see cref="FreeSpace"/> for the one subtlety that costs.</para>
///
/// <para><b>What is NOT on this ladder any more.</b> Notifications and the theme toggle are unconditional rows of the
/// PROFILE MENU at every width (the bell merged into the chip's unread badge; the moon/sun became a menu row), so they
/// have no threshold to model. Friends is the one affordance that MOVES rather than drops: <see cref="ShowFriends"/>
/// means "in the row" and <see cref="FriendsInMenu"/> is its exact complement — the affordance is never lost, it just
/// changes address. That XOR is the invariant <c>MergedChromeLayoutTests</c> pins.</para>
///
/// <para><b>Hysteresis.</b> Unchanged in mechanism, applied to the STRUCTURAL decisions: <see cref="Resolve"/> resolves
/// the stage at <c>width</c> and again at <c>width - ChromePromotionHysteresisW</c>; a DEMOTION is taken immediately
/// while a PROMOTION only lands if it also holds at the reserved width, otherwise the previous stage is held. The two
/// CONTINUOUS outputs (<see cref="SearchWidth"/>, <see cref="TabMaxWidth"/>) need no reserve — they cannot oscillate,
/// they only stretch — so they track the live width under whatever stage was held, quantised to
/// <c>ChromeWidthQuantumW</c> so a resize drag does not re-render the bar per device pixel.</para>
///
/// <para><b>The click-expanded search is part of this ladder, not a decoration on top of it.</b> Under pressure the row
/// resolves <see cref="MergedSearchMode.Icon"/>, and the magnifier CLICK-EXPANDS. That expansion is an INPUT here
/// (<c>searchExpanded</c>), not a render-time override: the field claims its width IN PLACE and the row folds its
/// lower-priority chrome to fund it — name → bare avatar, friends → profile menu, then tabs into the "⌄", in exactly
/// the priority order the stages above already encode. Sizing it downstream instead is what shipped a 40-DIP
/// "expanded" field: the measured centre column is ~0 while the row is still collapsed, so the clamp floored at the
/// icon width and the row never learned it was supposed to give way.</para>
///
/// <para>Engine-free by construction (System only) so <c>MergedChromeLayoutTests</c> drives the real allocator. The
/// constants themselves live in <see cref="ShellResponsiveLayout"/>, next to the shell's other breakpoints.</para>
/// </summary>
public readonly record struct MergedChromeLayout(
    bool ShowName,
    bool ShowFriends,
    bool ShowForward,
    MergedSearchMode SearchMode,
    float SearchWidth,
    float TabMaxWidth,
    int KeepTabs,
    bool SearchExpanded = false)
{
    /// <summary>A monotone "how much is this row carrying" score. NO LONGER the promotion comparand (the reserve is now
    /// applied per structural decision — see <see cref="Resolve"/>); it survives as the one-number summary the
    /// narrowing-never-adds test asserts on, and every component of it is non-decreasing in width.</summary>
    internal int Richness =>
        (ShowName ? 1 : 0) + (ShowFriends ? 1 : 0) + (ShowForward ? 1 : 0)
        + (SearchMode == MergedSearchMode.Field ? 1 : 0)
        + (int)(SearchWidth * 0.1f) + (int)(TabMaxWidth * 0.1f) + KeepTabs;

    /// <summary>Friends is a standalone island button at this width. The NAME the row builder reads (an alias of
    /// <see cref="ShowFriends"/>) so the two addresses read as one decision at both call sites.</summary>
    public bool FriendsInRow => ShowFriends;

    /// <summary>Friends has folded into the profile menu — the EXACT complement of <see cref="FriendsInRow"/>. The
    /// profile flyout mounts its Friends row on this, so the affordance is present at every width and duplicated at
    /// none (the XOR the tests pin).</summary>
    public bool FriendsInMenu => !ShowFriends;

    /// <summary>True once the row has shed everything but the avatar — the ladder's last rung. (Notifications and the
    /// theme toggle no longer participate: they are unconditional profile-menu rows.)</summary>
    public bool BareAvatar => !ShowName && !ShowFriends;

    /// <summary>The seed (no previous state, so no promotion reserve) — the value a shell signal is constructed with
    /// before the first viewport effect runs.</summary>
    public static MergedChromeLayout FromWidth(float width, int tabCount)
        => Resolve(width, tabCount, null);

    /// <summary>The live resolve. <paramref name="previous"/> null = seed. Demotion is immediate (nothing can clip
    /// while the window contracts); promotion needs <c>ChromePromotionHysteresisW</c> of reserve.
    /// <para>Pure in its arguments by design: the row feeds no measured spans back in. TitleBar measures exactly ONE
    /// span — the centre column (<c>CenterAvail</c>) — and that measurement is a downstream CLAMP on the field the
    /// ladder already sized, not an input to the decision. Every other span in the row is a fixed constant the budget
    /// below names, so there is nothing live to thread through.</para>
    /// <para><paramref name="searchExpanded"/> is the magnifier's click latch. When it is set and the ladder would
    /// otherwise resolve <see cref="MergedSearchMode.Icon"/>, <see cref="Expand"/> forces a field and FUNDS it out of
    /// the lower-priority chrome (see there for the order and the two targets). It is applied AFTER the hysteresis
    /// hold and is never gated by it: the expansion is user-initiated, so it lands on the frame of the click rather
    /// than one reserve band later. Coming back OUT is the ordinary machinery — the caller simply stops passing the
    /// latch and the held ladder it never touched is published again.</para>
    /// <para><b>The <paramref name="previous"/> contract, and the one way to break this:</b> it must be the
    /// UNEXPANDED ladder — the carrier the hysteresis lives in. Feeding an EXPANDED result back in would record the
    /// expansion's folds (name off, friends off, tabs folded) as HELD demotions, and the promotion reserve would then
    /// keep the row lean after the latch dropped: the transient would have poisoned the steady state. So the shell
    /// keeps the base ladder beside the published one (<c>WaveeShell._chromeBase</c>) and always resolves from THAT;
    /// <see cref="SearchExpanded"/> marks the results that must never be fed back. The <c>FreeSpace</c> infimum itself
    /// is a pure function of width and is untouched by any of this — the expansion spends the space the folds return
    /// (<c>reclaimed</c>) instead of re-deriving a looser base.</para></summary>
    public static MergedChromeLayout Resolve(float width, int tabCount, MergedChromeLayout? previous = null,
                                             bool searchExpanded = false)
    {
        width = MathF.Max(0f, width);
        var stage = StageFor(width, tabCount);
        if (previous is { } old)
        {
            var reserved = StageFor(
                MathF.Max(0f, width - ShellResponsiveLayout.ChromePromotionHysteresisW), tabCount);
            stage = Hold(in stage, Stage.Of(in old, tabCount), in reserved);
        }
        float reclaimed = 0f;
        bool expanded = searchExpanded && Expand(width, tabCount, ref stage, out reclaimed);
        return Compose(width, tabCount, in stage, expanded, reclaimed);
    }

    // ── the FIXED BUDGET ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every DIP of the row that is neither a tab nor the search, for one configuration of the three identity
    /// bits. Public because the tests derive their boundary widths from it rather than from magic numbers.
    /// <para>Note the <paramref name="forward"/> rung is COST-NEUTRAL by construction: when Forward leaves the row it
    /// becomes the only thing in the "⋯" overflow, and that button is the same <c>ChromeNavButtonW</c> plate. (The page
    /// Pin can also put a row in the "⋯", but its presence is a property of the current destination, not of width, so
    /// it cannot be budgeted — the gutters absorb it. That is the one deliberate imprecision in this table.)</para></summary>
    public static float FixedBudget(bool name, bool friends, bool forward)
        => ShellResponsiveLayout.ChromeBarLeadW
         + ShellResponsiveLayout.ChromeNavButtonW                                    // Back — never leaves the row
         + (forward ? ShellResponsiveLayout.ChromeNavButtonW : 0f)                   // Forward, while it is in the row
         + ShellResponsiveLayout.ChromeAddSlotW                                      // the strip's reserved "+"
         + ShellResponsiveLayout.ChromeProfileChipW
         + (name ? ShellResponsiveLayout.ChromeProfileNameW : 0f)
         + (friends ? ShellResponsiveLayout.ChromeNavButtonW : 0f)
         + (forward ? 0f : ShellResponsiveLayout.ChromeNavButtonW)                   // the "⋯" that carries Forward
         + 2f * ShellResponsiveLayout.ChromeGutterMinW
         + ShellResponsiveLayout.ChromeMinDragStripW
         + ShellResponsiveLayout.ChromeCaptionClusterW;

    /// <summary>The fixed budget THIS layout is carrying, including the "⌄" chevron when it will be shown.</summary>
    public float FixedBudgetFor(int tabCount)
        => FixedBudget(ShowName, ShowFriends, ShowForward)
         + (tabCount > KeepTabs ? ShellResponsiveLayout.ChromeTabOverflowW : 0f);

    /// <summary>Every DIP this layout claims at that tab count — the space-honesty comparand. Tabs are counted at their
    /// CAP (a shorter label hugs narrower, which only ever frees more).</summary>
    public float FootprintFor(int tabCount)
        => FixedBudgetFor(tabCount)
         + KeepTabs * TabMaxWidth
         + (SearchMode == MergedSearchMode.Field ? SearchWidth : ShellResponsiveLayout.ChromeSearchIconW);

    /// <summary>The smallest row this allocator can ever emit: the leanest identity cluster, one tab at its floor and
    /// the search as an icon (plus the "⌄" once there is anything to fold away). Below this the FLOORS win over the
    /// arithmetic and the tabs island clips — deliberately, because the active tab never leaves.</summary>
    public static float MinimumFootprintFor(int tabCount)
        => FixedBudget(false, false, false)
         + ShellResponsiveLayout.ChromeTabMinW
         + ShellResponsiveLayout.ChromeSearchIconW
         + (tabCount > 1 ? ShellResponsiveLayout.ChromeTabOverflowW : 0f);

    /// <summary>The narrowest window at which a click-expanded search can still be funded IN PLACE: the same lean floor
    /// as <see cref="MinimumFootprintFor"/> but with the icon replaced by the expansion's hard floor
    /// (<c>ChromeSearchExpandedMinW</c>). At or above this width, <c>Resolve(..., searchExpanded: true)</c> ALWAYS
    /// returns a field — every fold above is available and their sum is enough — and below it the ladder keeps the
    /// magnifier rather than emit a field too narrow to type in.
    /// <para><b>764 DIP with a single tab open, 800 with more</b> (the "⌄" costs its own plate) — which is ABOVE
    /// Wavee's 300-DIP minimum window, so state it plainly: between the app floor and this, clicking the magnifier
    /// still cannot claim a real field and <c>MergedSearchIsland</c> falls back to the measured-column clamp. Lowering
    /// it means letting the expansion count the tab strip below <c>ChromeTabMinW</c> — a layout decision (the strip
    /// clips, the caption cluster must not move), not an arithmetic one, and deliberately not taken here.</para></summary>
    public static float MinimumExpandedWidthFor(int tabCount)
        => FixedBudget(false, false, false)
         + ShellResponsiveLayout.ChromeTabMinW
         + ShellResponsiveLayout.ChromeSearchExpandedMinW
         + (tabCount > 1 ? ShellResponsiveLayout.ChromeTabOverflowW : 0f);

    /// <summary>The space the tabs and the search share at this width — and the one place the model pays for keeping
    /// the identity bits on width bands.
    /// <para>The raw figure is <c>width - FixedBudget(the bits at this width)</c>, but that is NOT monotone: the name
    /// arriving at 1360 costs the budget 90 DIP while the window only grew by one, so a NARROWING window would FREE 90
    /// and could add a tab back. This returns the running infimum from the right instead — at every width it is the
    /// smallest free space any WIDER window will have — which is monotone by construction. The cost is an ~89-DIP dead
    /// band below the name threshold (and 44 below friends) where widening buys nothing; the bar's grow drag bands
    /// absorb it, which is exactly what they are for.</para></summary>
    public static float FreeSpace(float width)
    {
        float w = MathF.Max(0f, width);
        float free = w - FixedBudget(w >= ShellResponsiveLayout.ChromeNameEnterW,
                                     w >= ShellResponsiveLayout.ChromeFriendsEnterW,
                                     w > ShellResponsiveLayout.ChromeForwardEnterW);
        // The three points where a bit turns on as the window widens. Between them the budget is constant and free
        // space rises with slope 1, so the infimum over [w, ∞) is the min of here and each later jump point.
        free = MathF.Min(free, FreeAtJump(w, ShellResponsiveLayout.ChromeForwardEnterW + 1f));
        free = MathF.Min(free, FreeAtJump(w, ShellResponsiveLayout.ChromeFriendsEnterW));
        free = MathF.Min(free, FreeAtJump(w, ShellResponsiveLayout.ChromeNameEnterW));
        return MathF.Max(0f, free);
    }

    static float FreeAtJump(float w, float jump)
        => w >= jump
            ? float.PositiveInfinity
            : jump - FixedBudget(jump >= ShellResponsiveLayout.ChromeNameEnterW,
                                 jump >= ShellResponsiveLayout.ChromeFriendsEnterW,
                                 jump > ShellResponsiveLayout.ChromeForwardEnterW);

    // ── the STAGE: every decision the promotion reserve has to make sticky ───────────────────────────────────────────

    /// <summary>The row's STRUCTURAL state — the things that pop rather than stretch, and therefore the only things
    /// that can oscillate on a boundary. <see cref="Resolve"/> applies the hysteresis reserve to exactly these.</summary>
    readonly record struct Stage(bool Name, bool Friends, bool Forward, bool Field, int Keep)
    {
        /// <summary>The stage a previous layout was holding. KeepTabs is re-clamped to the CURRENT open count, because
        /// a tab that closed while the window was held would otherwise leave a cap above the count (the
        /// widening-spurious-demotion guard, kept from the threshold model).</summary>
        internal static Stage Of(in MergedChromeLayout l, int tabCount) => new(
            l.ShowName, l.ShowFriends, l.ShowForward, l.SearchMode == MergedSearchMode.Field,
            tabCount > 0 ? Math.Clamp(l.KeepTabs, 1, tabCount) : Math.Max(1, l.KeepTabs));
    }

    /// <summary>The pure allocation, before any hysteresis: what this width can carry.</summary>
    static Stage StageFor(float w, int tabCount)
    {
        bool name = w >= ShellResponsiveLayout.ChromeNameEnterW;
        bool friends = w >= ShellResponsiveLayout.ChromeFriendsEnterW;
        // Strictly greater, so the demotion edge lands on the historical ToolbarNarrowEnterW (520) exactly.
        bool forward = w > ShellResponsiveLayout.ChromeForwardEnterW;

        float free = FreeSpace(w);
        int open = tabCount > 0 ? tabCount : 1;

        // (2) TABS CLAIM FIRST, at the floor, against the search in its minimum guaranteed form. Only when even THAT
        //     does not fit does the "⌄" appear — and then it costs the budget its own plate, which is why the two
        //     branches are computed separately rather than always reserving the chevron.
        int keep;
        float chevron;
        float floorForAll = open * ShellResponsiveLayout.ChromeTabMinW + ShellResponsiveLayout.ChromeSearchIconW;
        if (free >= floorForAll)
        {
            keep = open;
            chevron = 0f;
        }
        else
        {
            chevron = ShellResponsiveLayout.ChromeTabOverflowW;
            keep = (int)MathF.Floor(
                (free - ShellResponsiveLayout.ChromeSearchIconW - chevron) / ShellResponsiveLayout.ChromeTabMinW);
            keep = Math.Clamp(keep, 1, open > 1 ? open - 1 : 1);
            if (keep >= open) chevron = 0f;      // a single open tab is never folded away, so there is no chevron
        }

        // (3) The SEARCH takes what is left — a real field only if the leftover clears its minimum.
        float rest = free - chevron - keep * ShellResponsiveLayout.ChromeTabMinW;
        return new Stage(name, friends, forward, rest >= ShellResponsiveLayout.ChromeSearchMinW, keep);
    }

    /// <summary>Demotion immediate, promotion only with the reserve, otherwise HOLD — per structural decision.
    /// <para>For the bools: a candidate false always wins (nothing can clip while the window contracts), and a
    /// candidate true only lands if the previous stage already had it or the reserved width also grants it.
    /// For KeepTabs the same rule in arithmetic form — never above the candidate, never below what was already held
    /// unless the candidate says so, and any promotion capped at what the reserved width can carry.</para></summary>
    static Stage Hold(in Stage candidate, in Stage previous, in Stage reserved) => new(
        candidate.Name && (previous.Name || reserved.Name),
        candidate.Friends && (previous.Friends || reserved.Friends),
        candidate.Forward && (previous.Forward || reserved.Forward),
        candidate.Field && (previous.Field || reserved.Field),
        Math.Min(candidate.Keep, Math.Max(previous.Keep, reserved.Keep)));

    // ── the click-expanded search: the ladder's ONE user-initiated stage ──────────────────────────────────────────────

    /// <summary>Force a field at a width that resolved the magnifier, and FUND it by folding the row's lower-priority
    /// chrome — the same priority order the stages encode, run backwards:
    /// <list type="number">
    /// <item>the profile NAME (90 DIP) → a bare avatar,</item>
    /// <item>FRIENDS (44) → a profile-menu row (the affordance moves address, it is never lost),</item>
    /// <item>each TAB past the active one (110, less the "⌄" plate the first fold buys) → the overflow menu.</item>
    /// </list>
    /// <para>FORWARD is deliberately NOT on that list even though it sits between friends and the tabs on the width
    /// ladder: <see cref="FixedBudget"/> makes its rung cost-neutral (the button it removes and the "⋯" it forces in
    /// are the same plate), so folding it would cost an affordance and free nothing.</para>
    /// <para><b>The folds buy the FLOOR; the field then grows into whatever they bought, capped at the target.</b> Each
    /// rung is taken only while the row is still short of <c>ChromeSearchExpandedMinW</c> — so a squeeze the profile
    /// name alone can pay for leaves the Friends button and the whole strip standing, and only a real squeeze walks
    /// down to <c>KeepTabs = 1</c>. Folding further to chase the comfortable <c>ChromeSearchExpandedW</c> was
    /// considered and rejected: it is arithmetically the same as having no fold ladder here at all (an icon-mode row
    /// has under <c>ChromeSearchMinW</c> of spare BY DEFINITION, so a 380 target folds every identity bit at every
    /// width it can be reached from), and 240 is already wider than the field this ladder is willing to keep
    /// PERMANENTLY. <c>ChromeSearchExpandedW</c> survives as the CAP the field grows to when the folds overshoot.</para>
    /// <para>Returns false — leaving <paramref name="s"/> untouched — when even the last fold cannot reach the floor.
    /// That is only possible below <see cref="MinimumExpandedWidthFor"/>, and then the magnifier stays.</para>
    /// <para><paramref name="reclaimed"/> is the DIP the folded identity bits return to the row. <see cref="Compose"/>
    /// budgets from the same monotone <see cref="FreeSpace"/> as every other resolution — which assumes the identity
    /// bits the WIDTH grants, not the ones this stage kept — so the saving has to be handed forward explicitly. It is
    /// the exact plate width of each bit dropped here, which keeps the allocation conservative: the true fixed cost of
    /// the composed stage is never above what the base plus this bonus budgeted (see <see cref="Compose"/>).</para></summary>
    static bool Expand(float w, int tabCount, ref Stage s, out float reclaimed)
    {
        reclaimed = 0f;
        if (s.Field) return false;                       // already a real omnibar — the latch has nothing to buy

        int open = tabCount > 0 ? tabCount : 1;
        int keep = Math.Clamp(s.Keep, 1, open);
        bool name = s.Name, friends = s.Friends;
        float chevron = keep < open ? ShellResponsiveLayout.ChromeTabOverflowW : 0f;
        float spare = FreeSpace(w) - chevron - keep * ShellResponsiveLayout.ChromeTabMinW;

        if (spare < ShellResponsiveLayout.ChromeSearchExpandedMinW && name)
        {
            name = false;
            reclaimed += ShellResponsiveLayout.ChromeProfileNameW;
            spare += ShellResponsiveLayout.ChromeProfileNameW;
        }
        if (spare < ShellResponsiveLayout.ChromeSearchExpandedMinW && friends)
        {
            friends = false;
            reclaimed += ShellResponsiveLayout.ChromeNavButtonW;
            spare += ShellResponsiveLayout.ChromeNavButtonW;
        }
        while (spare < ShellResponsiveLayout.ChromeSearchExpandedMinW && keep > 1)
        {
            // The FIRST fold also buys the "⌄" plate; every later one is the tab floor outright.
            if (keep == open) spare -= ShellResponsiveLayout.ChromeTabOverflowW;
            keep--;
            spare += ShellResponsiveLayout.ChromeTabMinW;
        }

        if (spare < ShellResponsiveLayout.ChromeSearchExpandedMinW) { reclaimed = 0f; return false; }
        s = new Stage(name, friends, s.Forward, true, keep);
        return true;
    }

    /// <summary>Turn a held stage into the row's widths at the LIVE width. The two continuous outputs are recomputed
    /// here rather than carried through the hold, so widening grows the field immediately instead of trailing the
    /// window by the reserve.
    /// <para><paramref name="expanded"/> swaps the search's two bounds for the click-expansion's pair (floor
    /// <c>ChromeSearchExpandedMinW</c>, cap <c>ChromeSearchExpandedW</c>) and parks the tab cap at its floor: a
    /// transient overlay that also widened the tabs would make its own collapse jarring.</para></summary>
    static MergedChromeLayout Compose(float w, int tabCount, in Stage s, bool expanded = false, float reclaimed = 0f)
    {
        int open = tabCount > 0 ? tabCount : 1;
        int keep = Math.Clamp(s.Keep, 1, open);

        // Deliberately the SAME monotone free space the stage was decided on. A held stage is never RICHER than the
        // candidate (Hold only ever removes), so its true fixed cost is ≤ what this budgeted — the allocation below
        // therefore always fits, and it stays monotone across the identity bands. `reclaimed` is the ONE addition:
        // the exact plate width of every identity bit the expansion folded, which the base budget still assumed.
        float free = FreeSpace(w) + reclaimed;
        if (keep < open) free -= ShellResponsiveLayout.ChromeTabOverflowW;
        float spare = MathF.Max(0f, free - keep * ShellResponsiveLayout.ChromeTabMinW);

        float searchFloor = expanded ? ShellResponsiveLayout.ChromeSearchExpandedMinW
                                     : ShellResponsiveLayout.ChromeSearchMinW;
        float searchCap = expanded ? ShellResponsiveLayout.ChromeSearchExpandedW
                                   : ShellResponsiveLayout.ChromeSearchMaxW;

        MergedSearchMode mode;
        float searchW;
        if (s.Field && spare >= searchFloor)
        {
            mode = MergedSearchMode.Field;
            // Snapped DOWN, so the field never claims a DIP the budget did not have. 220, 240, 380 and 420 are all
            // multiples of the quantum, so both clamps survive the snap.
            searchW = Quantise(MathF.Min(spare, searchCap));
            // A click-expansion parks the tabs at their floor: the surplus past the expanded cap stays drag band, so
            // dropping the latch moves the field and NOTHING else.
            spare = expanded ? 0f : spare - searchW;
        }
        else
        {
            mode = MergedSearchMode.Icon;
            searchW = ShellResponsiveLayout.ChromeSearchIconW;
            // (4) Tabs widen only once the search is COMFORTABLE. In icon mode it never is, so the leftover — bounded
            //     by ChromeSearchMinW, since more than that would have made it a field — stays drag band.
            spare = 0f;
        }

        float tabMax = Quantise(MathF.Min(
            ShellResponsiveLayout.ChromeTabMaxW,
            ShellResponsiveLayout.ChromeTabMinW + spare / keep));

        return new MergedChromeLayout(s.Name, s.Friends, s.Forward, mode, searchW, tabMax, keep,
                                      SearchExpanded: expanded && mode == MergedSearchMode.Field);
    }

    static float Quantise(float v)
        => MathF.Floor(v / ShellResponsiveLayout.ChromeWidthQuantumW) * ShellResponsiveLayout.ChromeWidthQuantumW;
}
