using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// The full-page customizer's PURE model (Wave 4a): the progressive three-region tier ladder, the searchable palette table
// + filter, the outline flattening and its flat-index → MoveSection translation, the display-option projection, and the
// opaque extension-config rewriter. All of it lives in Features/Sidebar/Curated/SidebarCustomizerLayout.cs, which is
// engine-free ON PURPOSE and source-included here, so these tests drive PRODUCTION code rather than a copy of it.
//
// Where legality is the REDUCER's business (a group inside a group), the test asserts exactly that: the translation
// produces the command and SidebarLayoutReducer rejects it. Two authorities for one rule is the drift this avoids.
public class SidebarCustomizerLayoutTests
{
    // ── the tier ladder ───────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Bands (R3.2 round 2 LOWERED the top two): >=1320 four regions · 1000-1319 three · 820-999 two · <820 outline only.
    // The width these take is the PAGE CONTENT width, and the docked sidebar spends ~280 DIP before this page measures —
    // at the old 1480/1180 a normal ~1330 window fell into Compact and the reporter never saw Canvas or the rich header.

    [Fact]
    public void NominalTier_MapsTheFourBands()
    {
        Assert.Equal(SidebarCustomizerTier.Canvas, SidebarCustomizerLayout.NominalTier(1320f));
        Assert.Equal(SidebarCustomizerTier.Canvas, SidebarCustomizerLayout.NominalTier(2400f));
        Assert.Equal(SidebarCustomizerTier.Full, SidebarCustomizerLayout.NominalTier(1000f));
        Assert.Equal(SidebarCustomizerTier.Full, SidebarCustomizerLayout.NominalTier(1319f));
        Assert.Equal(SidebarCustomizerTier.Compact, SidebarCustomizerLayout.NominalTier(999f));
        Assert.Equal(SidebarCustomizerTier.Compact, SidebarCustomizerLayout.NominalTier(820f));
        Assert.Equal(SidebarCustomizerTier.Narrow, SidebarCustomizerLayout.NominalTier(819f));
        Assert.Equal(SidebarCustomizerTier.Narrow, SidebarCustomizerLayout.NominalTier(0f));
    }

    /// <summary>The Full band must never be narrower than the three fixed regions it promises to show, or the tier would
    /// squeeze the elastic outline below <c>OutlineMinWidth</c> instead of dropping a region.</summary>
    [Fact]
    public void FullBand_FitsItsThreeRegions()
    {
        const float PagePadding = 32f;    // Spacing.L a side
        const float RegionGaps = 24f;     // two 12-DIP gaps between three regions
        float needed = SidebarCustomizerLayout.PaletteWidth
                     + SidebarCustomizerLayout.InspectorWidth
                     + SidebarCustomizerLayout.OutlineMinWidth
                     + RegionGaps + PagePadding;
        Assert.True(SidebarCustomizerLayout.FullEnterW >= needed,
            $"FullEnterW {SidebarCustomizerLayout.FullEnterW} < {needed} needed");

        float canvasNeeded = needed + SidebarCustomizerLayout.PreviewWidth + 12f;
        Assert.True(SidebarCustomizerLayout.CanvasEnterW >= canvasNeeded,
            $"CanvasEnterW {SidebarCustomizerLayout.CanvasEnterW} < {canvasNeeded} needed");
    }

    [Fact]
    public void Tier_UnmeasuredTakesTheNominalBand()
    {
        Assert.Equal(SidebarCustomizerTier.Canvas, SidebarCustomizerLayout.Tier(1600f, -1));
        Assert.Equal(SidebarCustomizerTier.Full, SidebarCustomizerLayout.Tier(1100f, -1));
        Assert.Equal(SidebarCustomizerTier.Compact, SidebarCustomizerLayout.Tier(900f, -1));
        Assert.Equal(SidebarCustomizerTier.Narrow, SidebarCustomizerLayout.Tier(700f, -1));
    }

    [Fact]
    public void Tier_WidensImmediately()
    {
        Assert.Equal(SidebarCustomizerTier.Canvas,
            SidebarCustomizerLayout.Tier(1320f, (int)SidebarCustomizerTier.Narrow));
        Assert.Equal(SidebarCustomizerTier.Full,
            SidebarCustomizerLayout.Tier(1000f, (int)SidebarCustomizerTier.Narrow));
        Assert.Equal(SidebarCustomizerTier.Compact,
            SidebarCustomizerLayout.Tier(820f, (int)SidebarCustomizerTier.Narrow));
    }

    [Fact]
    public void Tier_NarrowsOnlyPastTheHysteresisDip()
    {
        Assert.Equal(SidebarCustomizerTier.Canvas,
            SidebarCustomizerLayout.Tier(1310f, (int)SidebarCustomizerTier.Canvas));
        Assert.Equal(SidebarCustomizerTier.Full,
            SidebarCustomizerLayout.Tier(1290f, (int)SidebarCustomizerTier.Canvas));

        // 990 is below the 1000 threshold but inside the 24-DIP dip: the layout must NOT drop a region yet.
        Assert.Equal(SidebarCustomizerTier.Full,
            SidebarCustomizerLayout.Tier(990f, (int)SidebarCustomizerTier.Full));
        Assert.Equal(SidebarCustomizerTier.Compact,
            SidebarCustomizerLayout.Tier(970f, (int)SidebarCustomizerTier.Full));

        Assert.Equal(SidebarCustomizerTier.Compact,
            SidebarCustomizerLayout.Tier(810f, (int)SidebarCustomizerTier.Compact));
        Assert.Equal(SidebarCustomizerTier.Narrow,
            SidebarCustomizerLayout.Tier(790f, (int)SidebarCustomizerTier.Compact));
    }

    [Fact]
    public void Tier_SkipsTwoBandsAtOnceWhenTheDropIsBigEnough()
        => Assert.Equal(SidebarCustomizerTier.Narrow,
            SidebarCustomizerLayout.Tier(600f, (int)SidebarCustomizerTier.Full));

    [Fact]
    public void SheetHeight_IsClampedBothWays()
    {
        Assert.Equal(320f, SidebarCustomizerLayout.SheetHeight(0f));       // unmeasured page
        Assert.Equal(520f, SidebarCustomizerLayout.SheetHeight(1000f));    // 55% would be 550
        Assert.Equal(240f, SidebarCustomizerLayout.SheetHeight(400f));     // 55% would be 220
        Assert.Equal(100f, SidebarCustomizerLayout.SheetHeight(100f));     // never taller than the page itself
    }

    [Fact]
    public void RegionVisibility_FollowsTheTier()
    {
        Assert.True(SidebarCustomizerLayout.PaletteInline(SidebarCustomizerTier.Canvas));
        Assert.True(SidebarCustomizerLayout.PaletteInline(SidebarCustomizerTier.Full));
        Assert.False(SidebarCustomizerLayout.PaletteInline(SidebarCustomizerTier.Compact));
        Assert.False(SidebarCustomizerLayout.PaletteInline(SidebarCustomizerTier.Narrow));

        Assert.True(SidebarCustomizerLayout.InspectorInline(SidebarCustomizerTier.Canvas));
        Assert.True(SidebarCustomizerLayout.InspectorInline(SidebarCustomizerTier.Full));
        Assert.True(SidebarCustomizerLayout.InspectorInline(SidebarCustomizerTier.Compact));
        Assert.False(SidebarCustomizerLayout.InspectorInline(SidebarCustomizerTier.Narrow));

        Assert.True(SidebarCustomizerLayout.PreviewInline(SidebarCustomizerTier.Canvas));
        Assert.False(SidebarCustomizerLayout.PreviewInline(SidebarCustomizerTier.Full));
        Assert.False(SidebarCustomizerLayout.PreviewInline(SidebarCustomizerTier.Compact));
        Assert.False(SidebarCustomizerLayout.PreviewInline(SidebarCustomizerTier.Narrow));
    }

    // ── the header's command-pressure fit ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Undo/Redo stay inline at EVERY tier while the width allows it (round-3 defect 4). They used to be banned
    /// outright below Compact, so a narrow window collapsed to "… Done" with no history affordance even though the two
    /// buttons fit with room to spare. Width — never tier — is what may demote them.</summary>
    [Fact]
    public void CommandFit_KeepsHistoryInlineAtEveryTierWhenItFits()
    {
        var w = SidebarCustomizerCommandWidths.Default;
        foreach (SidebarCustomizerTier tier in Enum.GetValues<SidebarCustomizerTier>())
        {
            var fit = SidebarCustomizerCommandLayout.Resolve(600f, in w, tier);
            Assert.True(fit.Has(SidebarCustomizerInlineCommand.Undo), $"{tier}: Undo demoted at 600 DIP");
            Assert.True(fit.Has(SidebarCustomizerInlineCommand.Redo), $"{tier}: Redo demoted at 600 DIP");
        }
    }

    /// <summary>...and under REAL pressure they are the LAST inline commands standing: history outranks creation.</summary>
    [Fact]
    public void CommandFit_DemotesAddBeforeHistory()
    {
        var w = SidebarCustomizerCommandWidths.Default;
        // More(48) + Gap(8) + Done(76) = 132 mandatory; +Undo +Redo = 228; +Add would need 276.
        var fit = SidebarCustomizerCommandLayout.Resolve(240f, in w, SidebarCustomizerTier.Full);
        Assert.True(fit.Has(SidebarCustomizerInlineCommand.Undo));
        Assert.True(fit.Has(SidebarCustomizerInlineCommand.Redo));
        Assert.False(fit.Has(SidebarCustomizerInlineCommand.Add));

        // Squeezed to the mandatory pair only: everything optional is in overflow, nothing is dropped outright.
        var none = SidebarCustomizerCommandLayout.Resolve(140f, in w, SidebarCustomizerTier.Full);
        Assert.Equal(SidebarCustomizerInlineCommand.None, none.Inline);
    }

    // ── the palette table ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Palette_IdsAreUniqueAndEveryGroupIsPopulated()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in SidebarPalette.All)
            Assert.True(ids.Add(entry.Id), "duplicate palette id: " + entry.Id);

        foreach (var group in SidebarPalette.Groups)
        {
            bool any = false;
            foreach (var entry in SidebarPalette.All)
                if (entry.Group == group) { any = true; break; }
            Assert.True(any, "empty palette group: " + group);
        }
    }

    [Fact]
    public void Palette_EveryEntryAddsAKnownKindAndNamesItsLocKeys()
    {
        foreach (var entry in SidebarPalette.All)
        {
            Assert.True(SidebarSectionKinds.IsKnown(entry.Kind));
            Assert.False(string.IsNullOrEmpty(entry.NameLocKey));
            Assert.False(string.IsNullOrEmpty(entry.DescriptionLocKey));
            Assert.False(string.IsNullOrEmpty(entry.IconName));
        }
    }

    [Fact]
    public void Palette_ContributionEntriesNameARegisteredFirstPartySource()
    {
        int contributions = 0;
        foreach (var entry in SidebarPalette.All)
        {
            if (entry.Add != SidebarPaletteAdd.Contribution) continue;
            contributions++;
            Assert.Equal(SidebarSectionKind.Extension, entry.Kind);
            Assert.False(string.IsNullOrEmpty(entry.ContributionId));
            Assert.True(SidebarContributions.IsFirstParty(entry.ContributionId),
                "not a first-party contribution id: " + entry.ContributionId);
        }
        // Artist top tracks + Queue + Now Playing: the three built-in dynamic feeds that ship as Extension sections.
        Assert.Equal(3, contributions);
    }

    [Fact]
    public void Palette_TheGenericExtensionEntryDefersToTheContributionPicker()
    {
        SidebarPaletteEntry? generic = null;
        foreach (var entry in SidebarPalette.All)
            if (entry.Add == SidebarPaletteAdd.AnyContribution) generic = entry;

        Assert.NotNull(generic);
        Assert.Null(generic!.ContributionId);          // the user picks it; the table cannot know
        Assert.Equal(SidebarSectionKind.Extension, generic.Kind);
    }

    [Fact]
    public void Palette_LikedSongsIsOnePreseededShortcutGesture()
    {
        SidebarPaletteEntry? liked = null;
        foreach (var entry in SidebarPalette.All)
            if (entry.Add == SidebarPaletteAdd.LikedSongsShortcut) liked = entry;

        Assert.NotNull(liked);
        Assert.Equal("likedSongs", liked!.Id);
        Assert.Equal(SidebarPaletteGroup.Navigation, liked.Group);
        Assert.Equal(SidebarSectionKind.StaticLinks, liked.Kind);
        Assert.Equal("nav.likedSongs", liked.NameLocKey);
        Assert.Equal("Heart", liked.IconName);
    }

    [Fact]
    public void Palette_FilterIsTokenWiseAndCaseInsensitive()
    {
        var into = new List<SidebarPaletteEntry>();

        Assert.Equal(SidebarPalette.All.Length,
            SidebarPalette.Filter("", Label, Description, into));
        Assert.Equal(SidebarPalette.All.Length,
            SidebarPalette.Filter("   ", Label, Description, into));

        SidebarPalette.Filter("QUEUE", Label, Description, into);
        Assert.Single(into);
        Assert.Equal("queue", into[0].Id);

        // Two tokens, out of order, matching the label of one entry.
        SidebarPalette.Filter("tracks top", Label, Description, into);
        Assert.Single(into);
        Assert.Equal("artistTopTracks", into[0].Id);

        Assert.Equal(0, SidebarPalette.Filter("zzzznothing", Label, Description, into));

        // A token that only appears in the DESCRIPTION still matches.
        SidebarPalette.Filter("divider", Label, Description, into);
        Assert.Single(into);

        static string Label(SidebarPaletteEntry e) => e.Id;                  // stand-in for the localized name
        static string? Description(SidebarPaletteEntry e) => e.NameLocKey;   // …and its description
    }

    [Fact]
    public void Palette_MatchesEmptyQueryAlwaysWins()
    {
        Assert.True(SidebarPalette.Matches("", null, null));
        Assert.False(SidebarPalette.Matches("x", null, null));
        Assert.True(SidebarPalette.Matches(SidebarPalette.NormalizeQuery("  Pin  "), "Pinned", null));
    }

    // ── the outline ───────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionSpec Section(string id, SidebarSectionKind kind,
        IReadOnlyList<SidebarSectionSpec>? children = null, bool hidden = false)
        => new(id, kind, Title: null, TitleLocKey: null, Hidden: hidden, Collapsed: false, Display: null,
               Items: null, Query: null, Children: children, Extension: null);

    /// <summary>pinned · group[child] · divider — the shape every outline rule needs (a top-level row, a group, a nested
    /// row and a trailing row).</summary>
    static SidebarCustomLayout Doc()
    {
        var child = Section("sec_c1", SidebarSectionKind.StaticLinks);
        var group = Section("sec_g", SidebarSectionKind.CustomGroup, new[] { child });
        return new SidebarCustomLayout(SidebarTemplates.Blank,
        [
            Section("sec_p", SidebarSectionKind.Pinned),
            group,
            Section("sec_d", SidebarSectionKind.Divider),
        ]);
    }

    static List<SidebarOutlineRow> Rows(SidebarCustomLayout doc)
    {
        var rows = new List<SidebarOutlineRow>();
        SidebarOutlineRows.Build(doc, rows);
        return rows;
    }

    [Fact]
    public void OutlineRows_FlattenTopLevelAndGroupChildrenInOrder()
    {
        var rows = Rows(Doc());

        Assert.Equal(4, rows.Count);
        Assert.Equal("sec_p", rows[0].SectionId);
        Assert.Equal("sec_g", rows[1].SectionId);
        Assert.Equal("sec_c1", rows[2].SectionId);
        Assert.Equal("sec_d", rows[3].SectionId);

        Assert.Null(rows[0].ParentId);
        Assert.Equal("sec_g", rows[2].ParentId);
        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(1, rows[2].Depth);
        Assert.Equal(0, rows[2].IndexInParent);
        Assert.Equal(2, rows[3].IndexInParent);      // top-level index, not the flat one

        Assert.True(rows[1].IsGroup);
        Assert.Equal(1, rows[1].ChildCount);
        Assert.False(rows[0].IsGroup);

        // R3.2 item 2: an outline row is a CARD (24-DIP kind chip + title + kind subtitle), so top-level grew 44 -> 52 and
        // the one-line depth-1 child 36 -> 44. These two numbers are also Reorderable's drop pitch.
        Assert.Equal(52f, rows[0].Height);
        Assert.Equal(44f, rows[2].Height);
    }

    [Fact]
    public void OutlineRows_BuildIsIdempotentAndClearsTheCallerList()
    {
        var rows = new List<SidebarOutlineRow> { default, default };
        Assert.Equal(4, SidebarOutlineRows.Build(Doc(), rows));
        Assert.Equal(4, rows.Count);
        Assert.Equal(0, SidebarOutlineRows.Build(null, rows));
        Assert.Empty(rows);
    }

    [Fact]
    public void OutlineRows_IndexOfFindsBySectionId()
    {
        var rows = Rows(Doc());
        Assert.Equal(2, SidebarOutlineRows.IndexOf(rows, "sec_c1"));
        Assert.Equal(-1, SidebarOutlineRows.IndexOf(rows, "nope"));
        Assert.Equal(-1, SidebarOutlineRows.IndexOf(rows, null));
    }

    // ── the drag translation ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drag_NoOpAndOutOfRangeProduceNothing()
    {
        var rows = Rows(Doc());
        Assert.Null(SidebarOutlineDrag.ToMove(rows, 1, 1));
        Assert.Null(SidebarOutlineDrag.ToMove(rows, 9, 0));
        Assert.Null(SidebarOutlineDrag.ToMove(new List<SidebarOutlineRow>(), 0, 1));
    }

    [Fact]
    public void Drag_TopLevelRowTakesTheTargetsTopLevelSlot()
    {
        var rows = Rows(Doc());
        var move = SidebarOutlineDrag.ToMove(rows, 3, 0);      // the divider onto the pinned row

        Assert.NotNull(move);
        Assert.Equal("sec_d", move!.SectionId);
        Assert.Null(move.NewParentId);
        Assert.Equal(0, move.NewIndex);

        var result = SidebarLayoutReducer.Apply(Doc(), move);
        Assert.True(result.Changed);
        Assert.Equal("sec_d", result.Layout.Sections[0].Id);
    }

    [Fact]
    public void Drag_OntoAGroupHeaderLandsInsideTheGroup()
    {
        var rows = Rows(Doc());
        var move = SidebarOutlineDrag.ToMove(rows, 0, 1);      // pinned onto the group's own row

        Assert.NotNull(move);
        Assert.Equal("sec_p", move!.SectionId);
        Assert.Equal("sec_g", move.NewParentId);
        Assert.Equal(0, move.NewIndex);

        var result = SidebarLayoutReducer.Apply(Doc(), move);
        Assert.True(result.Changed);
        var group = result.Layout.Find("sec_g");
        Assert.NotNull(group);
        Assert.Equal("sec_p", group!.ChildList[0].Id);
        Assert.Equal(2, result.Layout.Sections.Count);          // it left the top level
    }

    [Fact]
    public void Drag_OntoAGroupChildTakesThatChildsSlot()
    {
        var rows = Rows(Doc());
        var move = SidebarOutlineDrag.ToMove(rows, 3, 2);      // the divider onto the group's child

        Assert.NotNull(move);
        Assert.Equal("sec_g", move!.NewParentId);
        Assert.Equal(0, move.NewIndex);

        var result = SidebarLayoutReducer.Apply(Doc(), move);
        Assert.True(result.Changed);
        Assert.Equal("sec_d", result.Layout.Find("sec_g")!.ChildList[0].Id);
    }

    [Fact]
    public void Drag_PastTheEndAppendsAfterTheLastRow()
    {
        var rows = Rows(Doc());
        var move = SidebarOutlineDrag.ToMove(rows, 0, rows.Count);

        Assert.NotNull(move);
        Assert.Equal("sec_p", move!.SectionId);
        Assert.Null(move.NewParentId);
        Assert.Equal(3, move.NewIndex);

        var result = SidebarLayoutReducer.Apply(Doc(), move);
        Assert.True(result.Changed);
        Assert.Equal("sec_p", result.Layout.Sections[^1].Id);
    }

    [Fact]
    public void Drag_AGroupIntoAGroupIsBuiltAndRejectedByTheReducer()
    {
        // LEGALITY IS THE REDUCER'S: the translation does not filter, so the customizer shows ONE rejection message from
        // ONE authority instead of two subtly different rules.
        var rows = Rows(Doc());
        var move = SidebarOutlineDrag.ToMove(rows, 1, 2);      // the group onto its own child

        Assert.NotNull(move);
        Assert.Equal("sec_g", move!.SectionId);
        Assert.Equal("sec_g", move.NewParentId);

        var result = SidebarLayoutReducer.Apply(Doc(), move);
        Assert.False(result.Changed);
        Assert.Equal(SidebarRejectReason.NestingTooDeep, result.Reason);
    }

    // ── display options: the panel's projection is the reducer's inverse ──────────────────────────────────────────────

    /// <summary>THE ROUND TRIP, for EVERY field and EVERY choice the panel can offer: the exact index the panel hands
    /// <c>SetDisplayOption</c> must come back out of <c>SidebarDisplayValues.Read</c> unchanged.
    /// <para>This is the guard the round-2 "some things don't change" report demanded. A silent OFF-BY-ONE between the
    /// panel's choice index and the reducer's enum cast is invisible in the UI — the control moves, the document changes,
    /// and the WRONG value lands — so it can only be caught here. The enum arms are the prime suspects (a
    /// <c>ChoiceLocKeys</c> list reordered independently of its enum), which is why every index is probed rather than
    /// one representative value per field.</para></summary>
    [Fact]
    public void DisplayValues_EveryFieldRoundTripsEveryChoiceThePanelCanOffer()
    {
        foreach (var field in SidebarDisplayValues.Order)
        {
            foreach (int chosen in PanelChoices(field))
            {
                var opts = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, chosen);
                int read = SidebarDisplayValues.Read(opts, field);
                Assert.True(chosen == read,
                    $"{field}: panel offered {chosen}, reducer+Read gave back {read}");
            }
        }
    }

    [Fact]
    public void PlaylistTreeQueryPanel_HidesKindsAndAlwaysShowsQualifier()
    {
        var tree = SidebarQueryPanelShape.For(SidebarSectionKind.PlaylistTree, qualifiersAvailable: false);
        Assert.False(tree.ShowKinds);
        Assert.True(tree.ShowQualifier);

        var entityWithoutCapability = SidebarQueryPanelShape.For(
            SidebarSectionKind.EntityList, qualifiersAvailable: false);
        Assert.True(entityWithoutCapability.ShowKinds);
        Assert.False(entityWithoutCapability.ShowQualifier);
    }

    [Fact]
    public void NumberEdit_RoundsAndClampsBothDirections()
    {
        Assert.Equal(3, SidebarNumberEdit.Normalize(3, 2, 4));
        Assert.Equal(2, SidebarNumberEdit.Normalize(2, 2, 4));
        Assert.Equal(2, SidebarNumberEdit.Normalize(1.2, 2, 4));
        Assert.Equal(4, SidebarNumberEdit.Normalize(9, 2, 4));
        Assert.Equal(3, SidebarNumberEdit.Normalize(2.6, 2, 4));
    }

    /// <summary>Exactly the values the property panel can send for one field: 0/1 for a flag, every choice index for an
    /// enum, and the range endpoints plus an interior point for a number. Nothing outside this set is reachable from the
    /// UI, and everything inside it must survive the trip.</summary>
    static IEnumerable<int> PanelChoices(SidebarDisplayField field)
    {
        if (SidebarDisplayValues.IsFlag(field)) return [0, 1];
        return field switch
        {
            // 0 = uncapped; the panel's slider spans 0..MaxItemsPerSection inclusive.
            SidebarDisplayField.MaxItems => [0, 1, 25, SidebarLayoutReducer.MaxItemsPerSection],
            // The reducer clamps to [2,4] and the panel's spinner offers exactly that.
            SidebarDisplayField.GridColumns => [2, 3, 4],
            _ => Indices(SidebarDisplayValues.ChoiceLocKeys(field).Length),
        };

        static IEnumerable<int> Indices(int count)
        {
            for (int i = 0; i < count; i++) yield return i;
        }
    }

    /// <summary>Every enum field's choice-label list must be exactly as long as the value range the reducer accepts. A
    /// list SHORTER than the range hides a legal value from the user; a list LONGER than it offers a choice the reducer
    /// silently clamps away — which is the "I picked it and nothing happened" shape of the round-2 report.</summary>
    [Fact]
    public void DisplayValues_EnumChoiceListsMatchTheReducersAcceptedRange()
    {
        foreach (var field in SidebarDisplayValues.Order)
        {
            if (SidebarDisplayValues.IsFlag(field)) continue;
            if (field is SidebarDisplayField.MaxItems or SidebarDisplayField.GridColumns) continue;

            int count = SidebarDisplayValues.ChoiceLocKeys(field).Length;
            Assert.True(count > 0, $"{field} has no choice labels");

            // One past the last offered choice must CLAMP back onto the last one. If it does not, the reducer accepts a
            // value the panel never offers and the choice list is short.
            var beyond = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, count);
            Assert.Equal(count - 1, SidebarDisplayValues.Read(beyond, field));

            // ...and the last offered choice must survive unclamped (the list is not LONGER than the range).
            var last = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, count - 1);
            Assert.Equal(count - 1, SidebarDisplayValues.Read(last, field));
        }
    }

    [Fact]
    public void DisplayValues_ReadIsTheInverseOfTheReducersWithField()
    {
        foreach (var field in SidebarDisplayValues.Order)
        {
            int wrote = field switch
            {
                SidebarDisplayField.Density => 2,
                SidebarDisplayField.Presentation => 1,
                SidebarDisplayField.MaxItems => 25,
                SidebarDisplayField.GridColumns => 3,
                SidebarDisplayField.RecentsSource => 1,
                _ => 1,
            };
            var opts = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, wrote);
            Assert.Equal(wrote, SidebarDisplayValues.Read(opts, field));
        }
    }

    [Fact]
    public void DisplayValues_OrderCoversEveryFieldTheOptionTableCanAllow()
    {
        // Every field some kind allows must have a row; otherwise the panel silently cannot edit it.
        foreach (SidebarDisplayField field in Enum.GetValues<SidebarDisplayField>())
        {
            bool allowedSomewhere = false;
            foreach (SidebarSectionKind kind in Enum.GetValues<SidebarSectionKind>())
                if (SidebarSectionKinds.AllowsDisplayField(kind, field)) { allowedSomewhere = true; break; }
            if (!allowedSomewhere) continue;

            Assert.Contains(field, SidebarDisplayValues.Order);
            Assert.False(string.IsNullOrEmpty(SidebarDisplayValues.LabelLocKey(field)));
            if (!SidebarDisplayValues.IsFlag(field)
                && field is not (SidebarDisplayField.MaxItems or SidebarDisplayField.GridColumns))
                Assert.NotEmpty(SidebarDisplayValues.ChoiceLocKeys(field));
        }
    }

    [Fact]
    public void DisplayValues_ReadOfANullOptionsBagIsTheDefault()
        => Assert.Equal((int)SidebarDisplayOptions.Default.Density,
            SidebarDisplayValues.Read(null, SidebarDisplayField.Density));

    // ── the opaque extension-config rewriter ──────────────────────────────────────────────────────────────────────────

    static SidebarConfigSchema Schema() => new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "k.max", DefaultJson: "4", Min: 1, Max: 20),
        new SidebarConfigField("artistUri", SidebarConfigFieldKind.EntityUri, "k.artist", Required: true),
        new SidebarConfigField("kinds", SidebarConfigFieldKind.Enum, "k.kinds", DefaultJson: "\"all\"",
            EnumValues: ["all", "playlists"]),
    ]);

    [Fact]
    public void ConfigDefaults_SeedOnlyTheFieldsThatDeclareOne()
    {
        var config = new SidebarSourceConfig(SidebarConfigJson.Defaults(Schema()));
        Assert.True(config.IsObject);
        Assert.Equal(4, config.Int("maxItems", -1));
        Assert.Equal("all", config.Str("kinds"));
        Assert.Null(config.Str("artistUri"));
    }

    [Fact]
    public void ConfigDefaults_OfNoSchemaIsAnEmptyObject()
    {
        Assert.True(new SidebarSourceConfig(SidebarConfigJson.Defaults(null)).IsObject);
        Assert.True(new SidebarSourceConfig(SidebarConfigJson.Defaults(SidebarConfigSchema.None)).IsObject);
    }

    [Fact]
    public void ConfigWrite_PreservesMembersThisBuildDoesNotKnow()
    {
        var start = SidebarJson.Detach("{\"maxItems\":3,\"futureFlag\":true,\"nested\":{\"a\":1}}");

        var next = new SidebarSourceConfig(SidebarConfigJson.WithInt(start, "maxItems", 9));
        Assert.Equal(9, next.Int("maxItems"));
        Assert.True(next.Bool("futureFlag"));

        var again = new SidebarSourceConfig(SidebarConfigJson.WithBool(
            SidebarConfigJson.WithString(next.Value, "artistUri", "spotify:artist:1"), "descending", true));
        Assert.Equal("spotify:artist:1", again.Str("artistUri"));
        Assert.True(again.Bool("descending"));
        Assert.Equal(9, again.Int("maxItems"));
        Assert.True(again.Bool("futureFlag"));
    }

    [Fact]
    public void ConfigWrite_UriListNormalizesAndAnEmptyListRemovesTheMember()
    {
        var withList = SidebarConfigJson.WithStrings(SidebarJson.EmptyObject, "includeUris",
            [" spotify:artist:a ", "", "spotify:artist:b"]);
        var buffer = new List<string>();
        Assert.Equal(2, new SidebarSourceConfig(withList).Strings("includeUris", buffer));
        Assert.Equal("spotify:artist:a", buffer[0]);
        Assert.Equal("spotify:artist:b", buffer[1]);

        buffer.Clear();
        var cleared = SidebarConfigJson.WithStrings(withList, "includeUris", Array.Empty<string>());
        Assert.Equal(0, new SidebarSourceConfig(cleared).Strings("includeUris", buffer));
        Assert.True(new SidebarSourceConfig(cleared).IsObject);
    }

    [Fact]
    public void ConfigWrite_ANonObjectConfigDegradesToJustTheEditedMember()
    {
        var next = new SidebarSourceConfig(SidebarConfigJson.WithInt(SidebarJson.Detach("42"), "maxItems", 7));
        Assert.True(next.IsObject);
        Assert.Equal(7, next.Int("maxItems"));
    }

    [Fact]
    public void ConfigWrite_RoundTripsThroughTheReducersSetExtensionConfig()
    {
        var xref = new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, SidebarContributions.Queue, 1,
            SidebarConfigJson.Defaults(Schema()));
        var section = new SidebarSectionSpec("sec_x", SidebarSectionKind.Extension, Title: null, TitleLocKey: null,
            Hidden: false, Collapsed: false, Display: null, Items: null, Query: null, Children: null, Extension: xref);
        var doc = new SidebarCustomLayout(SidebarTemplates.Blank, [section]);

        var edited = SidebarConfigJson.WithInt(xref.Config, "maxItems", 11);
        var result = SidebarLayoutReducer.Apply(doc, new SetExtensionConfig("sec_x", edited));

        Assert.True(result.Changed);
        var stored = result.Layout.Find("sec_x")!.Extension!;
        Assert.Equal(11, new SidebarSourceConfig(stored.Config).Int("maxItems"));
        Assert.Equal("all", new SidebarSourceConfig(stored.Config).Str("kinds"));

        // Re-applying the same value is a NoChange (the reducer compares canonical JSON) — so a mirror effect that writes
        // the control's own value back can never burn an undo slot.
        var again = SidebarLayoutReducer.Apply(result.Layout, new SetExtensionConfig("sec_x", edited));
        Assert.False(again.Changed);
        Assert.Equal(SidebarRejectReason.NoChange, again.Reason);
    }
}
