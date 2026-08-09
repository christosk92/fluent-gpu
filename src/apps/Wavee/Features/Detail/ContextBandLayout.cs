using System;

namespace Wavee.Features.Detail;

/// <summary>What the context band carries at one width: the title's slot, how many pivot items survived, and whether
/// any were dropped. Every field is derived by <see cref="ContextBandLayout.Resolve"/>.</summary>
public readonly record struct ContextBandFit(float TitleWidth, int PivotCount, bool PivotTruncated);

/// <summary>
/// The PURE allocator behind Wavee's ONE sticky page header — the <b>text-chrome context bar</b> (the Zune pivot
/// idiom): a 56-DIP band carrying a title on the left, the page's own sections as text links in the middle, and text
/// actions on the right. No thumbnail, no plates, no shadow — the chrome IS typography, so the only thing that can
/// physically not fit is TEXT, and this file is the arithmetic that decides what yields.
///
/// <para><b>The priority order is fixed and is the whole model.</b> The title never drops (it is what the band is
/// FOR — it answers "what page is this" once the hero has scrolled away) and the actions never drop (they are the
/// page's primary verbs and have nowhere else to live once the hero is gone). Only the PIVOT yields, and it yields
/// from the RIGHT: later sections drop first, because a pivot is an ordered walk down the page and the sections the
/// visitor has not reached yet are the ones they are least likely to be aiming at. There is deliberately NO overflow
/// chevron — a "⌄" holding three section names is a menu pretending to be wayfinding, and the sections it hides are
/// all still reachable by simply scrolling, which is the gesture the pivot is a shortcut FOR.</para>
///
/// <para><b>Widths are ESTIMATED, not measured.</b> A measured-width feedback loop (render → measure → re-resolve →
/// re-render) is what <c>DetailTrackCommandBarLayout</c> pays for a command bar whose items are icon+label controls
/// of genuinely unknowable width. A pivot item is one localized word or two at ONE rung (14/600), so
/// <see cref="EstimateLabelWidth"/> from the character count is within a few DIP, and being a few DIP conservative
/// costs at most one pivot item at one window width — where the alternative costs a render loop on every resize
/// frame. The estimate is deliberately generous (see <see cref="AvgCharW"/>) so the band clips nothing.</para>
///
/// <para><b>The scroll spy</b> (<see cref="ActiveSection"/>) is the other half: given where each section's top
/// currently sits relative to the viewport, which one is "here". It is a pure scan so the live version — which reads
/// committed layout through <c>SceneStore.AbsoluteRect</c>, exactly like <c>ShyMonthPill</c> — has no arithmetic of
/// its own to get wrong.</para>
///
/// <para>Engine-free by construction (System only) so <c>ContextBandLayoutTests</c> drives the real allocator, in the
/// <c>MergedChromeLayout</c> / <c>DetailVerticalLayout</c> tradition.</para>
/// </summary>
public static class ContextBandLayout
{
    /// <summary>The identity row's height. Equal to <c>DetailVerticalLayout.CompactIdentityHeight</c> by construction:
    /// the detail band and the artist band are the SAME band, and the detail collapse ladder already targets 56.</summary>
    public const float Height = 56f;

    /// <summary>The band's one hairline. ONE, under the whole stuck surface — including the tracklist column row when
    /// it has joined the band — so a scrolled page never shows two sticky strata with raw rows between them.</summary>
    public const float HairlineHeight = 1f;

    /// <summary>THE OFFSET MODEL'S OTHER HALF. The band paints no fill (see <c>ContextBand</c>), so the page owes it a
    /// CLIP: nothing may render into these DIP, and what shows there is the page's own ground.
    ///
    /// <para>This is the ARTIST arm's inset, where the band is the identity row and nothing else. The track-detail
    /// pages pin a second stratum under it — the tracklist's column row plus the shared hairline — so they clip at
    /// <c>DetailVerticalLayout.StickyClipInset</c> instead, which sums exactly this height plus that row. Two numbers,
    /// one rule.</para></summary>
    public const float ClipInset = Height;

    /// <summary>The feather at the clip edge, so content dissolves into the band instead of being guillotined by it.
    /// The same band the detail pages' item clip already uses, so every surface cuts identically.</summary>
    public const float ClipFadeBand = DetailVerticalLayout.StickyFadeBand;

    /// <summary>Gap between the three clusters (title | pivot | actions).</summary>
    public const float ClusterGap = 24f;      // Spacing.XXL

    /// <summary>Gap between two pivot items.</summary>
    public const float PivotGap = 16f;        // Spacing.L

    /// <summary>Gap between two text actions in the right cluster.</summary>
    public const float ActionGap = 16f;       // Spacing.L

    /// <summary>Horizontal padding INSIDE one pivot item's hit box — also the inset the 2-DIP active underline is
    /// drawn within, so the mark reads as belonging to the word rather than to the slot.</summary>
    public const float PivotPadX = 8f;

    /// <summary>Horizontal padding inside one text action's hit box. Larger than <see cref="PivotPadX"/> because an
    /// action has no underline to widen its perceived target.</summary>
    public const float ActionPadX = 10f;

    /// <summary>The active-section underline: 2 DIP, the tab-strip rung (this is genuine AccentSelection — "you are
    /// here" — not decoration), sitting <see cref="UnderlineGap"/> under the link's line box.</summary>
    public const float UnderlineHeight = 2f;
    public const float UnderlineGap = 4f;     // Spacing.XS

    /// <summary>The smallest slot the title is allowed to claim before the pivot starts yielding. Roughly one short
    /// word plus its ellipsis — below this the title is not identifying anything.</summary>
    public const float TitleFloor = 88f;

    /// <summary>The widest slot the title claims. Past it the surplus goes to the pivot, which is the affordance that
    /// actually does something with more room.</summary>
    public const float TitleCap = 280f;

    /// <summary>Average advance of one character at the band's rung (14 px / weight 600, Segoe UI Variable Text).
    /// Deliberately on the generous side of the real average (~6.9 for mixed-case Latin): a pivot that estimates a
    /// touch WIDE drops one item a few DIP early, which is invisible; one that estimates narrow clips a word, which is
    /// not. Non-Latin scripts run wider per glyph but shorter per word, and the two errors cancel in the band's
    /// favour.</summary>
    public const float AvgCharW = 7.6f;

    /// <summary>The estimated laid-out width of one band label, hit box included.</summary>
    public static float EstimateLabelWidth(int labelLength, float padX)
        => MathF.Max(0f, labelLength) * AvgCharW + 2f * padX;

    /// <inheritdoc cref="EstimateLabelWidth(int,float)"/>
    public static float EstimateLabelWidth(string? label, float padX)
        => EstimateLabelWidth(label?.Length ?? 0, padX);

    /// <summary>The right cluster's total claim: every action at its estimated width plus the gaps between them.
    /// The actions never drop, so this is a HARD subtraction from the pivot's budget.</summary>
    public static float ActionsWidth(ReadOnlySpan<float> actionWidths)
    {
        if (actionWidths.Length == 0) return 0f;
        float w = 0f;
        for (int i = 0; i < actionWidths.Length; i++) w += MathF.Max(0f, actionWidths[i]);
        return w + (actionWidths.Length - 1) * ActionGap;
    }

    /// <summary>The whole allocation at one width. <paramref name="innerWidth"/> is the band's content width — the
    /// full band minus its two gutters.
    /// <para>Order of operations, and the reason for it: the title takes its (clamped) estimate FIRST because it is
    /// the band's reason to exist; the actions take their full claim SECOND because they cannot shrink or move; the
    /// pivot spends whatever is left. When the leftover cannot hold even the first pivot item the pivot is absent
    /// entirely rather than showing a lone truncated word — a one-item pivot is not a pivot, it is a mislabel.</para>
    /// <para>Monotone in <paramref name="innerWidth"/> by construction (both earlier claims are width-independent
    /// once clamped), which is the property that keeps a resize drag from ever REMOVING a pivot item as the window
    /// grows — the invariant the tests walk a width ladder to pin.</para></summary>
    public static ContextBandFit Resolve(float innerWidth, float titleEstimate, float actionsWidth,
                                         ReadOnlySpan<float> pivotWidths)
    {
        float inner = MathF.Max(0f, innerWidth);
        float title = Math.Clamp(MathF.Max(0f, titleEstimate), TitleFloor, TitleCap);
        // A band too narrow even for the floor gives the title everything it can and drops the pivot.
        if (title > inner) return new ContextBandFit(inner, 0, pivotWidths.Length > 0);

        float actions = MathF.Max(0f, actionsWidth);
        int count = FitPivots(inner - title - actions - 2f * ClusterGap, pivotWidths);
        return new ContextBandFit(title, count, count < pivotWidths.Length);
    }

    /// <summary>How many pivot items fit in <paramref name="free"/> DIP, walking left to right — so the items that
    /// drop are always the trailing ones. Never returns more than the pivot has.</summary>
    public static int FitPivots(float free, ReadOnlySpan<float> pivotWidths)
    {
        if (pivotWidths.Length == 0 || free <= 0f) return 0;
        float used = 0f;
        int n = 0;
        for (int i = 0; i < pivotWidths.Length; i++)
        {
            float next = used + (n > 0 ? PivotGap : 0f) + MathF.Max(1f, pivotWidths[i]);
            if (next > free) break;
            used = next;
            n++;
        }
        return n;
    }

    // ── the scroll spy ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How far PAST the band's lower edge a section top may still be and count as "arrived". Without it the
    /// active mark flickers between two neighbours while a section top sits exactly on the boundary during a slow
    /// scroll — one sub-pixel layout jitter is enough. 8 DIP is below the smallest deliberate scroll step and above
    /// any layout noise.</summary>
    public const float SpyProbe = 8f;

    /// <summary>Which pivot item is "here". <paramref name="viewportRelativeTops"/> is each section's top edge
    /// measured DOWN from the viewport's own top (so negative = already scrolled past), in pivot order.
    /// <paramref name="bandBottom"/> is the lower edge of the sticky chrome — a section is "arrived" once its top has
    /// passed under the band, not under the window, or the mark would advance while the section was still hidden
    /// behind the very bar that names it.
    ///
    /// <para>Returns 0 while the page is at the top (the first section is the answer before anything has crossed:
    /// a pivot with no mark reads as broken, and the visitor IS looking at the first section), and −1 only for an
    /// empty pivot. A <see cref="float.NaN"/> top means "not realized yet" and STOPS the scan — an unmeasured section
    /// cannot be behind the band, and treating NaN as arrived would jump the mark to the end of a page whose lower
    /// sections have not laid out.</para></summary>
    public static int ActiveSection(ReadOnlySpan<float> viewportRelativeTops, float bandBottom)
    {
        if (viewportRelativeTops.Length == 0) return -1;
        int active = 0;
        for (int i = 0; i < viewportRelativeTops.Length; i++)
        {
            float top = viewportRelativeTops[i];
            if (float.IsNaN(top)) break;
            if (top <= bandBottom + SpyProbe) active = i; else break;
        }
        return active;
    }

    /// <summary>The content-space offset that parks section <paramref name="index"/>'s top just under the band. The
    /// live path hands the node to <c>ScrollIntoView.BringInto</c> with this as its margin; this overload exists for
    /// the model-driven case (and for the tests) where the destination is arithmetic rather than a realized node.</summary>
    public static float ScrollTargetFor(float currentOffset, float viewportRelativeTop, float bandBottom)
        => MathF.Max(0f, currentOffset + viewportRelativeTop - bandBottom);
}
