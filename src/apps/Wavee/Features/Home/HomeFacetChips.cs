using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The home facet row (Spotify <c>home.homeChips[]</c>): Music / Podcasts / Audiobooks, each optionally
/// carrying a second level ("Following").
///
/// Uses the SAME grammar as the Concerts filter bar rather than inventing one: a loose chip, and once it carries a
/// value it FUSES into a two-segment pill (<c>ConcertUi.SegmentedPill</c>) whose trailing chevron reopens the facet.
/// Clicking a fused pill clears back to the parent facet — one step, not all the way to unfiltered.
/// Two filter surfaces in the app, one vocabulary.
///
/// Selection writes <see cref="Services.HomeFacet"/> — an OPAQUE server token — and asks the page to refetch. The
/// facet variable was always in the home request; it was simply hardcoded to "".</summary>
sealed class HomeFacetChips : Component
{
    internal sealed record Model(IReadOnlyList<HomeChip> Chips, Action OnFacetChanged);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var model = UseContext(Props);
        var svc = UseContext(Services.Slot);
        if (model is null || svc is null || model.Chips.Count == 0) return new BoxEl();

        string? selected = svc.HomeFacet.Value;    // subscribe → the row re-renders on selection

        // Which top-level chip owns the current selection: either it IS the selection, or one of its sub-chips is.
        HomeChip? activeParent = null;
        HomeChip? activeSub = null;
        foreach (var chip in model.Chips)
        {
            if (string.Equals(chip.Id, selected, StringComparison.Ordinal)) { activeParent = chip; break; }
            foreach (var sub in chip.SubChips)
                if (string.Equals(sub.Id, selected, StringComparison.Ordinal)) { activeParent = chip; activeSub = sub; break; }
            if (activeParent is not null) break;
        }

        var children = new List<Element>(model.Chips.Count * 2);
        bool prevSpilledSubs = false;
        for (int i = 0; i < model.Chips.Count; i++)
        {
            var chip = model.Chips[i];
            // No divider before a chip whose PREDECESSOR spilled sub-chips: the subs belong to that parent, and a
            // divider there made them read as peers of the top-level facets instead of children of one.
            if (i > 0 && !prevSpilledSubs) children.Add(Divider());

            bool isActive = ReferenceEquals(chip, activeParent);
            bool fused = isActive && activeSub is not null;

            // ONE key for both states of this facet's pill. That is the entire fusion mechanism, and it is why
            // Concerts morphs while this row used to pop: the reconciler reuses the SAME node across the loose-token
            // → fused-pill swap, so the width-reflow recipe both shapes carry has a previous width to animate FROM.
            // With distinct keys the token unmounts, a new pill mounts, and there is nothing to reflow.
            string pillKey = "facet-pill:" + chip.Id;
            Element pill = fused
                // Fused: the sub-chip has flown into the pill and become its second segment. Clicking clears back to
                // the parent facet (one step), not all the way to unfiltered.
                ? ConcertUi.SegmentedPill(chip.Label, activeSub!.Label, () => Select(svc, model, chip.Id))
                    with { Key = pillKey }
                : ConcertUi.FilterToken(chip.Label, isActive,
                    () => Select(svc, model, isActive ? null : chip.Id)) with { Key = pillKey };

            // The parent's sub-chips appear only while it is selected, and fly INTO the pill when picked.
            var spilled = isActive && !fused ? chip.SubChips : Array.Empty<HomeChip>();
            prevSpilledSubs = spilled.Count > 0;

            // The group is ALWAYS present, even with no sub-chips. It has to be: the pill can only be reused across
            // the token → fused-pill swap if its PARENT is the same node in both renders, and a group that appears
            // only when subs spill would re-parent the pill on the very transition the fusion depends on.
            //
            // Parent + its options share this one tight group (a half gap, no divider) so the row reads as
            // "Music ▸ its options" rather than as four peer facets — which is what makes the fuse motion legible.
            var group = new List<Element>(spilled.Count + 1) { pill };
            foreach (var child in spilled)
            {
                var subChip = child;
                group.Add(ConcertUi.FilterToken(subChip.Label, false, () => Select(svc, model, subChip.Id)) with
                {
                    Key = "facet-sub:" + subChip.Id,
                    Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                        TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
                        Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true)),
                });
            }
            children.Add(new BoxEl
            {
                Key = "facet-group:" + chip.Id,
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
                Children = group.ToArray(),
            });
        }

        // No eyebrow. The chips ARE self-evidently filters, and a "FILTER BY" label above a single row of pills is
        // pure chrome — it pushed the shelves down and competed with the greeting for the eye.
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
            Children = children.ToArray(),
        };
    }

    // Writing the signal is the whole mutation; the page owns refetching, so this component never knows about
    // Pathfinder or caching. Peek-compare first so re-picking the current chip does not fire a redundant refresh.
    static void Select(Services svc, Model model, string? facetId)
    {
        if (string.Equals(svc.HomeFacet.Peek(), facetId, StringComparison.Ordinal)) return;
        svc.HomeFacet.Value = facetId;
        model.OnFacetChanged();
    }

    static Element Divider() => new BoxEl
    {
        Width = 1f, Height = 22f, Shrink = 0f, Fill = Tok.StrokeDividerDefault,
    };
}
