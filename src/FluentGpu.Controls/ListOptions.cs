using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

public enum RepeatKind : byte { Stack, Grid, Custom, Wrap, Inline }

/// <summary>
/// A pluggable layout for <see cref="ItemsView"/> (the equivalent of WinUI's <c>Layout</c> object). A readonly struct
/// (no per-call allocation). Stack/Grid/Custom virtualize (only the window is realized); Wrap/Inline are non-virtual
/// (build the whole child list — use for SMALL collections like a nav pane or a toolbar). Measured/LinedFlow/SpanGrid/
/// HorizontalGrid ride the Custom slot (every layout object IS an <see cref="IVirtualLayout"/>; variable-extent ones
/// are <see cref="IMeasuredVirtualLayout"/> — E11-L0's one seam).
/// NOTE: the stateful layouts (Measured/LinedFlow/SpanGrid/Grouped) own realize tables — when the OWNING component
/// re-renders, hoist the layout instance (e.g. <c>UseMemo</c>) instead of calling the factory inline each render.
/// </summary>
public readonly struct RepeatLayout
{
    public readonly RepeatKind Kind;
    public readonly float Extent;          // Stack: item extent; Grid: item height (≤0 ⇒ measure rows); Wrap/Inline: gap
    public readonly float Gap;             // Grid: cell gap
    public readonly int Columns;           // Grid: fixed columns (0 when <see cref="MinCellWidth"/> drives responsive cols)
    public readonly float MinCellWidth;    // Grid auto/fit: min cell width → column count from cross size
    public readonly float Estimate;        // Grid auto/fit: initial row-height estimate before cells measure
    public readonly bool Horizontal;
    public readonly IVirtualLayout? CustomLayout;

    private RepeatLayout(RepeatKind kind, float extent, float gap, int columns, bool horizontal, IVirtualLayout? custom,
                         float minCellWidth = 0f, float estimate = 0f)
        => (Kind, Extent, Gap, Columns, Horizontal, CustomLayout, MinCellWidth, Estimate) =
            (kind, extent, gap, columns, horizontal, custom, minCellWidth, estimate);

    /// <summary>Virtualized uniform stack (1-D).</summary>
    public static RepeatLayout Stack(float itemExtent, bool horizontal = false) => new(RepeatKind.Stack, itemExtent, 0, 0, horizontal, null);
    /// <summary>Virtualized uniform card grid (2-D, by row) at a fixed row height.</summary>
    public static RepeatLayout Grid(int columns, float itemHeight, float gap = 0f) => new(RepeatKind.Grid, itemHeight, gap, columns, false, null);
    /// <summary>Virtualized card grid with <b>measured row heights</b> (square art + text cards — no guessed text allowance).
    /// Rows correct to the max measured cell height at arrange time; <paramref name="estimateRowHeight"/> seeds scroll extent.</summary>
    public static RepeatLayout GridAuto(int columns, float gap = 0f, float estimateRowHeight = 120f)
        => new(RepeatKind.Grid, 0f, gap, columns, false, null, estimate: estimateRowHeight);
    /// <summary>Responsive measured grid: column count and cell width derive from the viewport cross size — callers pass only
    /// <paramref name="minCellWidth"/> + gap (the virtualized <c>AutoGrid</c> shape).</summary>
    public static RepeatLayout GridFit(float minCellWidth, float gap = 0f, float estimateRowHeight = 120f)
        => new(RepeatKind.Grid, 0f, gap, 0, false, null, minCellWidth, estimateRowHeight);
    /// <summary>Obsolete — use <see cref="GridAuto"/> or <see cref="GridFit"/> (measured rows) instead of guessing text height.</summary>
    [Obsolete("Use GridAuto or GridFit — measured rows replace aspect+textAllowance guessing.")]
    public static RepeatLayout AspectGrid(int columns, float aspect, float textAllowance, float gap = 0f)
        => new(RepeatKind.Custom, 0, 0, 0, false, new AspectGridVirtualLayout(columns, aspect, textAllowance, gap));
    /// <summary>Virtualized with ANY custom <see cref="IVirtualLayout"/>.</summary>
    public static RepeatLayout Custom(IVirtualLayout layout, bool horizontal = false) => new(RepeatKind.Custom, 0, 0, 0, horizontal, layout);
    /// <summary>Virtualized with a variable-extent <see cref="IMeasuredVirtualLayout"/> (estimate-then-correct + anchoring).</summary>
    public static RepeatLayout Measured(IMeasuredVirtualLayout layout, bool horizontal = false) => new(RepeatKind.Custom, 0, 0, 0, horizontal, layout);
    /// <summary>The WinUI <c>LinedFlowLayout</c> photo-wall (uniform-height lines, aspect-ratio widths). Stateful —
    /// hoist when the owner re-renders (see the struct remarks).</summary>
    public static RepeatLayout LinedFlow(float lineHeight, Func<int, float>? aspectRatio = null, float lineSpacing = 0f, float minItemSpacing = 0f)
        => new(RepeatKind.Custom, 0, 0, 0, false, new LinedFlowLayout(lineHeight, aspectRatio, lineSpacing, minItemSpacing));
    /// <summary>Uniform-row grid with item spanning (hero rows). Stateful — hoist when the owner re-renders.</summary>
    public static RepeatLayout SpanGrid(int columns, float rowHeight, float gap, Func<int, int> spanOf)
        => new(RepeatKind.Custom, 0, 0, 0, false, new SpanningGridVirtualLayout(columns, rowHeight, gap, spanOf));
    /// <summary>Horizontally-scrolling uniform grid (a shelf <paramref name="rows"/> cells tall), by column.</summary>
    public static RepeatLayout HorizontalGrid(int rows, float itemWidth, float gap = 0f)
        => new(RepeatKind.Custom, 0, 0, 0, true, new HorizontalGridVirtualLayout(rows, itemWidth, gap));
    /// <summary>Variable-extent uniform-list: every row seeds at <paramref name="estimatedExtent"/> and corrects to its
    /// measured extent on realize (Fenwick estimate-then-correct + scroll anchoring — the <c>MeasuredStackVirtualLayout</c>).
    /// Stateful — hoist when the owner re-renders (see the struct remarks).</summary>
    public static RepeatLayout VariableList(float estimatedExtent, bool horizontal = false)
        => new(RepeatKind.Custom, 0, 0, 0, horizontal, new MeasuredStackVirtualLayout(estimatedExtent, horizontal));
    /// <summary>ANALYTIC variable-extent list: <paramref name="extentOf"/> reports a row's height straight from the
    /// host's own model, so an unmeasured row seeds at its REAL extent instead of one global estimate — which is what
    /// keeps the content extent and the scroll anchor still when the model inserts or removes rows in the MIDDLE of a
    /// list whose rows are not all the same height. It is a SEED, not a substitute for measurement: measure feedback
    /// still corrects any row the function cannot predict exactly, and <paramref name="estimatedExtent"/> is the
    /// fallback for a non-finite/non-positive answer. Called only on a seed/resize/splice — never per frame — so keep
    /// it allocation-free and cache the delegate (it is part of the layout's identity). Stateful — hoist when the owner
    /// re-renders (see the struct remarks).</summary>
    public static RepeatLayout Extents(Func<int, float> extentOf, float estimatedExtent, bool horizontal = false)
        => new(RepeatKind.Custom, 0, 0, 0, horizontal,
               new MeasuredStackVirtualLayout(estimatedExtent, horizontal, extentOf));
    /// <summary>Grouped flat list with measured rows + a sticky-header hook: <paramref name="headerIndices"/> (sorted
    /// ascending) are group-header flat indices — a header is just a measured item KIND. For <c>StickyHeaderIndexAt</c>
    /// keep your own <c>GroupedListVirtualLayout</c> and pass it via <see cref="Measured"/> instead. Stateful — hoist when
    /// the owner re-renders.</summary>
    public static RepeatLayout GroupedList(int[] headerIndices, float headerExtent, float itemEstimate)
        => new(RepeatKind.Custom, 0, 0, 0, false, new GroupedListVirtualLayout(headerIndices, headerExtent, itemEstimate));
    /// <summary>Non-virtual wrap (flex-wrap) — for small collections.</summary>
    public static RepeatLayout Wrap(float gap = 8f) => new(RepeatKind.Wrap, 0, gap, 0, false, null);
    /// <summary>Non-virtual stack (column/row) — for small collections (nav panes, toolbars).</summary>
    public static RepeatLayout Inline(bool horizontal = false, float gap = 0f) => new(RepeatKind.Inline, 0, gap, 0, horizontal, null);
}

/// <summary>
/// WinUI <c>ItemCollectionTransition</c> mapped onto the engine's FLIP/<see cref="LayoutTransition"/> pipeline
/// (E11-L2): Moves = position FLIP (a reorder/removal slides the survivors), Adds = enter fade-in, Removes = exit
/// fade-out, all over WinUI's <c>ControlFastAnimationDuration</c> 167ms / decelerate spline (0,0,0,1) — the timing the
/// ItemContainer/Repeater storyboards use (ItemContainer.xaml:54-56 KeySpline="0,0,0,1" at ControlFastAnimationDuration).
/// Virtualized caveat (documented, deliberate): enter transitions also play when an item SCROLLS into the realized
/// window as a fresh mount — WinUI's LinedFlow behaves the same way for newly realized elements.
/// </summary>
public readonly record struct ItemCollectionTransition(
    bool AnimateAdds = true,
    bool AnimateRemoves = true,
    bool AnimateMoves = true,
    float DurationMs = 167f)
{
    // NOTE: must spell the arguments out — on a record struct the parameterless `new()` ZERO-initializes
    // (primary-ctor parameter defaults do not apply), which silently made Default an all-off 0ms transition.
    public static ItemCollectionTransition Default => new(AnimateAdds: true, AnimateRemoves: true, AnimateMoves: true, DurationMs: 167f);

    /// <summary>The engine-level spec each realized item root is stamped with (<see cref="BoxEl.Animate"/>).</summary>
    internal LayoutTransition ToSpec()
        => new(
            (AnimateMoves ? TransitionChannels.Position : TransitionChannels.None)
            | (AnimateAdds || AnimateRemoves ? TransitionChannels.Opacity : TransitionChannels.None),
            TransitionDynamics.Tween(DurationMs, Easing.FluentDecelerate),
            SizeMode.Auto,
            Enter: AnimateAdds ? new EnterExit(Opacity: 0f, Active: true) : default,
            Exit: AnimateRemoves ? new EnterExit(Opacity: 0f, Active: true) : default);
}

/// <summary>
/// Scroll-surface knobs for an <see cref="ItemsView"/> (grouped out of <see cref="ListOptions"/> so the common factory
/// call stays short). All forwarded onto the built <c>VirtualListEl</c> viewport.
/// </summary>
public sealed record ScrollOptions
{
    /// <summary>Identity-stable two-way vertical scroll controller. The viewport pushes exact range values and serves
    /// controller-originated absolute/relative requests through the engine's programmatic scroll seam.</summary>
    public IScrollController? VerticalScrollController { get; init; }
    /// <summary>The ONE authoring handle (scroll-v3-plan §7.2) over this viewport's live scroll — attached on
    /// realize, detached on unmount/re-bake, the <c>ItemsView</c> parity of <c>ScrollEl.Controller</c>. Distinct
    /// from <see cref="VerticalScrollController"/> (the annotated-rail seam, which stays).</summary>
    public FluentGpu.Scroll.ScrollController? Controller { get; init; }
    /// <summary>Scroll-position restoration key (see <c>VirtualListEl.ScrollKey</c>): a stable per-content identity so a
    /// revisit lands at the saved row on the first realized window. Null ⇒ no restoration.</summary>
    public string? ScrollKey { get; init; }
    /// <summary>Never draw the conscious scrollbar for the virtualized viewport (a paged surface navigates by its pager).</summary>
    public bool SuppressScrollBar { get; init; }
    /// <summary>Scroll-edge cues for the virtualized viewport (controls.md §8.3) — the surface-colour fade at an
    /// overflowing edge. <see cref="ScrollEdgeCues.Auto"/> (default) resolves to the app default;
    /// <see cref="ScrollEdgeCues.None"/> opts out. Forwarded onto the built viewport exactly like the
    /// <see cref="AutoEdgeFade"/> pair beside it — <c>ItemsView</c> already exposed this knob on its own builder
    /// surface, and this is the <c>CreateBound</c>/<c>Create</c> path's spelling of the same one.
    /// <para>A surface whose opaque ground is a ZStack SIBLING rather than an ANCESTOR must opt out: the cue resolves
    /// its colour by walking ancestors (<c>SceneRecorder.TryResolveCueSurface</c>), so it would sail past that ground,
    /// fade toward the wrong plate, and paint an opaque band the surface never asked for.</para></summary>
    public ScrollEdgeCues EdgeCues { get; init; } = ScrollEdgeCues.Auto;
    /// <summary>Premium alpha-mask edge fade: feather the content's own alpha at the overflowing edges (one offscreen RT).</summary>
    public bool AutoEdgeFade { get; init; }
    /// <summary>Feather WIDTH in DIP for <see cref="AutoEdgeFade"/>; 0 (default) = the engine's standard band. Forwarded
    /// onto the built viewport — see <c>ScrollEl.AutoEdgeFadeBand</c>.</summary>
    public float AutoEdgeFadeBand { get; init; }
    /// <summary>Scroll-geometry observer (project a scalar, get the change) forwarded onto the viewport.</summary>
    public (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? OnScrollGeometryChanged { get; init; }
    /// <summary>Declarative scroll-snap points for the virtualized viewport (see <c>ScrollEl.Snap</c>): flings land exactly
    /// on a snap value; wheel/keyboard/programmatic stay hard-clamped. Null (default) ⇒ the reconciler never touches the
    /// snap fields, so a post-mount scene write survives.
    /// <para>This record is UNPACKED at factory time and FROZEN at mount (the component-props contract), so declare only a
    /// CONSTANT interval here — a row height, a fixed page stride. An interval that re-fits with the viewport width (a
    /// size-reactive pager's page stride) cannot be expressed declaratively; that owner writes
    /// <c>ScrollState.SnapInterval</c> onto its realized viewport instead (see <c>ItemsViewController.Viewport</c>).</para></summary>
    public FluentGpu.Scene.SnapSpec? Snap { get; init; }
    /// <summary>Optional viewport-space TOP inset that clips only recyclable items after
    /// <see cref="ListOptions.PersistentPrefixCount"/>. Persistent prefix items (for example a hero + sticky chrome)
    /// remain unclipped. The recorder applies one shared band clip without per-row paint writes; NaN disables it.</summary>
    public float ItemClipTopInset { get; init; } = float.NaN;
    /// <summary>Optional top alpha-feather, in DIP, applied to the same recyclable suffix as
    /// <see cref="ItemClipTopInset"/>. The hard inset remains authoritative for paint and input; this only softens the
    /// visible edge immediately below pinned prefix chrome. Zero (default) disables it.</summary>
    public float ItemClipTopFadeBand { get; init; }
}

/// <summary>
/// Drag-reorder displacement channel for an <see cref="ItemsView"/> — the WinUI "siblings part to make room" over the
/// positional recycler. Supplied by the owning reorder substrate (the List/Grid/TreeView preset). See the field docs on
/// <see cref="ItemsView"/> for the ownership contract (the dragged ghost is skipped via its <c>DragGhost</c> scene flag).
/// </summary>
public sealed record ReorderOptions
{
    /// <summary>Resting-index → target displacement (DIP) at the current dwell-committed reorder target.</summary>
    public Func<int, (float dx, float dy)>? ItemDisplacement { get; init; }
    /// <summary>Bumped by the owner on every drag-delta / dwell-commit; the view subscribes so the displacement re-seeds.</summary>
    public IReadSignal<int>? DisplacementVersion { get; init; }
    /// <summary>Optional redundant hint: the resting index currently pointer-dragged (the seed already skips it via the flag).</summary>
    public IReadSignal<int>? DraggedSlot { get; init; }
}

/// <summary>
/// THE declarative sortable/insertable-list surface (design ruling (f), level 4 — SwiftUI <c>dropDestination</c> /
/// dnd-kit <c>useSortable</c> / Framer <c>Reorder</c>): an app declares INTENT — which payloads this list takes, which
/// of its rows the payload came from, what to do on deposit — and <see cref="ItemsView"/> owns every coordinate.
///
/// <para>The view already knows its viewport rect, its live scroll offset, its virtual layout's MEASURED item bands,
/// its persistent prefix and its item count: the exact values apps used to shovel in by hand (a hard-coded leading
/// estimate against a measured list was one of the four documented "cannot drop in this mode" causes). Setting this
/// option makes the view mount its own <see cref="DropTargetSpec"/>, resolve the insertion slot (<see cref="SortableMath"/>,
/// centre-crossing trigger), open an exact live gap with virtual-removal accounting, draw the 2px accent line + terminal
/// dot, host the in-gap preview, hide same-list source rows and run the whole teardown/optimistic-handoff lifecycle.</para>
///
/// <code>
/// Insertion = new InsertionOptions {
///     AcceptKinds   = [MyKinds.Resource],
///     CanAccept     = p =&gt; …,                       // capability gate (kind is matched first, for free)
///     IsSameList    = p =&gt; …,                       // move vs copy semantics
///     SourceIndices = p =&gt; …,                       // dragged DISPLAY rows → virtual removal + hide
///     OnDeposit     = (p, slot) =&gt; CommitAsync(p, slot),
///     GapPreview    = (p, slot) =&gt; PreviewCards(p),
/// }
/// </code>
///
/// <para>Like every other option this record is UNPACKED and FROZEN at mount (the component-props contract), so its
/// delegates must read LIVE app state (a signal, a field on the owning component) rather than close over a snapshot.</para>
/// </summary>
public sealed record InsertionOptions
{
    /// <summary>Drag kinds this list accepts (the cheap ordinal first gate — see <see cref="DropTargetSpec"/>).</summary>
    public string[] AcceptKinds { get; init; } = [];
    /// <summary>Capability gate over the payload. False ⇒ the target is TRANSPARENT (discovery continues to a
    /// compatible ancestor) rather than accepting and silently no-op'ing the drop.</summary>
    public Func<object?, bool>? CanAccept { get; init; }
    /// <summary>True ⇒ the payload's rows came from THIS list: move semantics (sources hide, the gap is exactly
    /// N·extent, the effect is <see cref="DropEffect.Move"/>). False ⇒ a copy with a capped gap.</summary>
    public Func<object?, bool>? IsSameList { get; init; }
    /// <summary>The dragged rows as DISPLAY indices relative to the insertable range (0 = the first insertable item,
    /// NOT the first item). Drives virtual removal + the source-row hide; may be non-contiguous. Only consulted when
    /// <see cref="IsSameList"/> holds.</summary>
    public Func<object?, IReadOnlyList<int>?>? SourceIndices { get; init; }
    /// <summary>How many rows the payload carries (the gap/preview size for a CROSS-list copy; a same-list move counts
    /// <see cref="SourceIndices"/> instead). Null ⇒ 1.</summary>
    public Func<object?, int>? DraggedCount { get; init; }
    /// <summary>Commit the drop at <c>slot</c> — the RAW insertion slot in display space (0..count) the user aimed at.
    /// It is deliberately NOT pre-corrected for rows removed above it: a backend move convention that inserts "before
    /// the row currently at this index" already discounts them, and correcting twice moves the block twice.
    /// The result is "a mutation was issued": only <c>true</c> promises the membership snapshot that
    /// <see cref="ItemsViewController.ObserveInsertionMembership"/> hands the gap over to.</summary>
    public Func<object?, int, Task<bool>>? OnDeposit { get; init; }
    /// <summary>Optional in-gap preview content (the app owns the CARDS; the view owns their position and the gap).
    /// Null ⇒ the line alone marks the insertion point.</summary>
    public Func<object?, int, Element>? GapPreview { get; init; }
    /// <summary>Optional drop caption (<see cref="DragSession.Caption"/>) — "Move 3 tracks". Refreshed per move, and
    /// also once per frame while an edge auto-scroll is running (the destination re-projects under a still pointer,
    /// inside the 0-alloc frame region), so prefer a cached/precomputed string over interpolating one per call.</summary>
    public Func<object?, int, string?>? Caption { get; init; }
    /// <summary>Why a payload this list COULD have taken (its kind matched) was turned away by <see cref="CanAccept"/> —
    /// "Clear sorting to reorder", "Can't edit this playlist". A refusing target is transparent by design, so without
    /// this the user gets no signal at all and reads the whole feature as broken; the chip renders it beside the
    /// not-allowed glyph. See <see cref="DropTargetSpec.RefusalCaption"/>. Null ⇒ the glyph alone.</summary>
    public Func<object?, string?>? RefusalCaption { get; init; }
    /// <summary>Fired once after a deposit LANDS (the membership handoff, else the commit's success edge) with the
    /// landed <c>(slot, count)</c> — the seam an app renders its own post-drop flash from. The framework deliberately
    /// ships no app visual here beyond the line and the gap.</summary>
    public Action<int, int>? OnLanded { get; init; }
    /// <summary>The INSERTABLE sub-range of the item model, <c>(firstItem, count)</c>. Null ⇒
    /// <c>(PersistentPrefixCount, itemCount − PersistentPrefixCount)</c>. Lists that append rows the insertion does not
    /// address (a "Recommended" header + its rows) must bound it, or those rows ride the gap down.</summary>
    public Func<(int First, int Count)>? Range { get; init; }
    /// <summary>Preview cards, and the cross-list gap cap (default 3 — an exact-N gap for a 500-track copy would blow
    /// the viewport).</summary>
    public int PreviewCap { get; init; } = SortableMath.DefaultPreviewCap;
    /// <summary>Drag-dim participation (default <see cref="DropTargetVisualPolicy.Spotlight"/>).</summary>
    public DropTargetVisualPolicy VisualPolicy { get; init; } = DropTargetVisualPolicy.Spotlight;
    /// <summary>Per-session spotlight policy — return false for a same-list reorder so the app never dims for it.</summary>
    public Func<DragSession, bool>? SpotlightWhen { get; init; }
    /// <summary>Sit this gesture out entirely (<see cref="DropTargetSpec.Transparent"/>): no acceptance, no refusal
    /// cue, no spotlight — discovery walks past to the ancestors. Use it where the list could never take the payload
    /// ON THIS SURFACE, so <see cref="RefusalCaption"/> would be an accusation rather than an explanation (an album
    /// page's track table saying "Can't edit this playlist"). A list that CAN take drops and is turning this one away
    /// wants <see cref="CanAccept"/> + <see cref="RefusalCaption"/> instead.</summary>
    public Func<object?, bool>? Transparent { get; init; }
}

/// <summary>
/// Entrance / cold-realize choreography for a BOUND <see cref="ItemsView"/> (<c>CreateBound</c>). Bound rows recycle
/// (mount-keyed Enter can't express a per-row add/glide), so these ride the same displacement bump that lands the order.
/// </summary>
public sealed record EntranceOptions
{
    /// <summary>Opt-in cold-mount stagger: a heavy list realizes its initial window a few rows/frame (kills the mount spike).</summary>
    public bool StaggerColdRealize { get; init; }
    /// <summary>Optional FLIP start override for the displacement seed (glide surviving rows old→new in the same bump).</summary>
    public Func<int, (float dx, float dy)?>? ItemFlipFrom { get; init; }
    /// <summary>Optional per-row opacity seed (from→1 after a stagger delay) — an added-row ease-in without a slot remount.</summary>
    public Func<int, (float from, float delayMs)?>? ItemFadeFrom { get; init; }
}

/// <summary>Removal choreography for a bound <see cref="ItemsView"/>. The controller first detaches any realized
/// matching slots into the engine's exit-orphan layer, then invokes the caller's data mutation exactly once. Unseen
/// items disappear structurally without being realized just to animate.</summary>
public sealed record RemovalOptions
{
    /// <summary>Presented-space terminal for each realized removed row.</summary>
    public EnterExit Exit { get; init; } = new(Dy: -Spacing.XS, Opacity: 0f, Active: true);
    /// <summary>Named exit recipe; defaults to the engine's standard structural exit.</summary>
    public MotionTokenId Motion { get; init; } = MotionTokenId.StandardExit;
    /// <summary>Optional per-row deal delay. The composing control chooses the value from its motion vocabulary.</summary>
    public float StaggerMs { get; init; }
}

/// <summary>Reactive one-shot provider for an inserted contiguous range. The owner records its logical key on the user
/// event; once its new plan exists, ItemsView consumes the exact range before the first expanded paint.</summary>
public sealed record DisclosureOptions
{
    public IReadSignal<int>? Version { get; init; }
    public Func<ItemDisclosureRange?>? PendingExpand { get; init; }
    public Action<ItemDisclosureRange>? OnExpandStarted { get; init; }
    public Action<ItemDisclosureRange>? OnExpandSettled { get; init; }
    /// <summary>Optional cold-path lifecycle trace. Invoked from controller/layout/effect work, never paint or input.</summary>
    public Action<ItemDisclosureDiagnostic>? Diagnostic { get; init; }
}

/// <summary>
/// The consolidated options record for the <see cref="ItemsView"/> creation trio (<c>Create</c> / <c>CreateBound</c>).
/// The ~20 named factory arguments collapse into this one record + the grouped sub-records
/// (<see cref="Scroll"/>/<see cref="Reorder"/>/<see cref="Entrance"/>); it is UNPACKED to the component's fields at
/// factory time — the recycling hot path NEVER reads the record. Callers construct it with an object initializer:
/// <c>new ListOptions { SelectionMode = ItemsSelectionMode.Multiple, OnInvoked = i => … }</c>.
/// </summary>
public record ListOptions
{
    /// <summary>Selection semantics — None/Single/Multiple/Extended (default Single, WinUI <c>ItemsView.h</c>).</summary>
    public ItemsSelectionMode SelectionMode { get; init; } = ItemsSelectionMode.Single;
    /// <summary>External selection model (shared / multi-view); null ⇒ the view owns one.</summary>
    public SelectionModel? Selection { get; init; }
    /// <summary>WinUI <c>IsItemInvokedEnabled</c> (default false) — gates whether Enter/DoubleTap raise <see cref="OnInvoked"/>.</summary>
    public bool IsItemInvokedEnabled { get; init; }
    /// <summary>The invoke callback (WinUI <c>ItemInvoked</c>): the item index, gated by the invoke matrix.</summary>
    public Action<int>? OnInvoked { get; init; }
    /// <summary>Selection-changed callback (WinUI <c>SelectionChanged</c>).</summary>
    public Action? OnChange { get; init; }
    /// <summary>Typeahead text per item (defaults to the string items when they back the view).</summary>
    public Func<int, string>? ItemText { get; init; }
    /// <summary>Per-item enabled gate (disabled items dim + don't interact / take focus).</summary>
    public Func<int, bool>? IsItemEnabled { get; init; }
    /// <summary>Imperative handle (CurrentItemIndex / StartBringItemIntoView / ScrollBy / Selection).</summary>
    public ItemsViewController? Controller { get; init; }
    /// <summary>Row overscan (rows realized beyond the viewport, per edge). Overridden by <see cref="CacheExtentPx"/> when set.</summary>
    public int Overscan { get; init; } = 4;
    /// <summary>Flex participation of the view: 1 (default) = fill the parent (hard viewport); 0 = natural (measures to ContentExtent).</summary>
    public float Grow { get; init; } = 1f;
    /// <summary>Built-in selector-visual preset (AccentPill / Check / FullRow / Border / None). RenderItem path only.</summary>
    public SelectorVisual Selector { get; init; } = SelectorVisual.Border;
    /// <summary>L4 skin seam: replaces the default <c>ItemContainer</c> chrome. RenderItem path only.</summary>
    public ItemContainerFactory? ContainerFactory { get; init; }
    /// <summary>Stable per-item keys for the keyed diff (reorder projections need item-identity keys). RenderItem path only.</summary>
    public Func<int, string>? KeyOf { get; init; }
    /// <summary>WinUI <c>ItemTransitionProvider</c> — Adds fade, Removes fade, Moves FLIP. RenderItem path only.</summary>
    public ItemCollectionTransition? Transition { get; init; }
    /// <summary>Per-item VARIATION (fill/fg/opacity/corner/padding/glyph as values) baked into the chrome. RenderItem path only.</summary>
    public Func<int, ItemChromeState, PartDelta>? PartDelta { get; init; }
    /// <summary>Reactive item count (crosses the frozen-ComponentEl boundary so a set change re-windows without a remount).</summary>
    public IReadSignal<int>? CountSignal { get; init; }

    /// <summary>Scroll-surface knobs (scrollKey / suppress-bar / auto-edge-fade / geometry observer).</summary>
    public ScrollOptions? Scroll { get; init; }
    /// <summary>Drag-reorder displacement channel.</summary>
    public ReorderOptions? Reorder { get; init; }
    /// <summary>Declarative insertion/sortable destination — the view owns ALL the geometry (see
    /// <see cref="InsertionOptions"/>). Supersedes hand-wired drop lanes; coexists with <see cref="Reorder"/> (an
    /// external displacement provider still applies when no insertion gap is open).</summary>
    public InsertionOptions? Insertion { get; init; }
    /// <summary>Entrance / cold-realize choreography (bound path).</summary>
    public EntranceOptions? Entrance { get; init; }
    /// <summary>Removal choreography invoked through <see cref="ItemsViewController.BeginRemoval"/> (bound path).</summary>
    public RemovalOptions? Removal { get; init; }
    /// <summary>Optional virtualized contiguous disclosure source.</summary>
    public DisclosureOptions? Disclosure { get; init; }

    // ── research adjustment #16 — virtualization knobs (opt-in; unset ⇒ byte-identical to the pre-knob path) ──
    /// <summary>Recycle-pool discriminator: <c>index → contentType</c>. Heterogeneous rows only recycle/rebind within
    /// their own content-type pool — a cross-type reuse forces a full element rebuild instead of a cheap rebind. Null ⇒
    /// one homogeneous pool (today's behavior). BOUND path (<c>CreateBound</c>) only.</summary>
    public Func<int, int>? ContentType { get; init; }
    /// <summary>Pre-realize margin BEYOND the viewport, in PIXELS. Overscan is row-based (a row count); this is a pixel
    /// band the engine converts to rows against the average row extent. <see cref="float.NaN"/> (default) ⇒ row-based
    /// <see cref="Overscan"/> stays authoritative; a finite value overrides it.</summary>
    public float CacheExtentPx { get; init; } = float.NaN;
    /// <summary>BOUND path only: keep the first N logical items mounted as normal leading content children while all
    /// later items continue to recycle. Use this for native sticky/scroll-linked prefix composition; default 0.</summary>
    public int PersistentPrefixCount { get; init; }
    /// <summary>Per-item paint isolation: wrap each realized item container as a layout/paint boundary
    /// (<c>IsolateLayout</c> + <c>ClipToBounds</c>) so an item's internal invalidation can't escape to relayout the list.
    /// Off by default.</summary>
    public bool RepaintBoundary { get; init; }

    // ── research adjustment #5 — keep-alive-but-hidden third slot state (opt-in; null ⇒ no keep-alive bucket) ──
    /// <summary>Keep-alive predicate: <c>index → true</c> marks an item whose BOUND slot must NOT be index-rebound when it
    /// scrolls off-window. Its subtree (a mid-edit TextBox, an in-flight <c>UseResource</c>) parks HIDDEN — detached, no
    /// layout/paint cost, render-effects/animations quiesced (the same <c>Flow.KeepAlive</c> parking mechanics) — and its
    /// slot is excluded from the recycle pool until the item re-enters the window or the bucket evicts it (LRU). BOUND
    /// path (<c>CreateBound</c>) only. Null ⇒ no bucket (recycled slots lose live state, today's behavior).</summary>
    public Func<int, bool>? KeepAlive { get; init; }
    /// <summary>Bounded keep-alive bucket cap (documented default 8): the most parked keep-alive slots retained at once.
    /// When the bucket exceeds this, the least-recently-used parked slot is evicted (its subtree unmounted). Prevents a
    /// long scroll over many keep-alive rows from leaking retained subtrees.</summary>
    public int KeepAliveCap { get; init; } = 8;

    // ── viewport-driven hydration (opt-in; null ⇒ byte-identical to the pre-hook path) ──
    /// <summary>Prefetch / hydration hook forwarded onto the built <c>VirtualListEl.OnVisibleRange</c>: the realized
    /// window moved → <c>(first, last)</c>, <c>last</c> EXCLUSIVE. This is the seam a page batches "hydrate the rows the
    /// user is at" from (extended metadata over a long list) WITHOUT dropping to the raw <c>Virtual.Measured</c> factory
    /// and losing selection, keyboard nav, the recycle pools and the entrance/insertion choreography this record owns.
    /// Both factory paths forward it (<c>CreateBound</c>'s bound slots and <c>Create</c>'s templated items).
    /// <para>Two semantics that otherwise bite the caller. (1) It fires ONLY when the realized window actually CHANGED —
    /// a steady transform-only scroll frame never reaches it — so treat it as a change EDGE, never a per-frame tick, and
    /// never rely on it to re-run for a window that did not move. (2) The pair is the REALIZED window INCLUDING the
    /// overscan / cache halo (<see cref="Overscan"/>, <see cref="CacheExtentPx"/>), NOT the strictly-visible rows: it
    /// deliberately runs ahead of the viewport so a prefetch lands before the row is on screen. A caller that needs the
    /// exactly-visible band must narrow the range itself.</para>
    /// <para>Called from the reconciler's realize path (cold edge, never paint or input), so keep the body allocation-free
    /// and cheap — batch the URIs and hand them to an async pump rather than doing work inline.</para></summary>
    public Action<int, int>? OnVisibleRange { get; init; }

    /// <summary>The shared default (Single selection, overscan 4, grow 1, Border selector).</summary>
    public static ListOptions Default { get; } = new();
}

/// <summary>
/// The typed options record for <see cref="ItemsView.CreateBound{T}"/>. Adds the typed callbacks that resolve the current
/// item at invocation time (so a row callback never captures a mount-time list instance); the untyped members of the base
/// record still apply. The typed callbacks WIN over the untyped ones when both are set.
/// </summary>
public sealed record ListOptions<T> : ListOptions
{
    /// <summary>Typed invoke callback: <c>(index, item)</c>.</summary>
    public Action<int, T>? OnInvokedTyped { get; init; }
    /// <summary>Typed typeahead text: <c>(index, item) → string</c>.</summary>
    public Func<int, T, string>? ItemTextTyped { get; init; }
    /// <summary>Typed per-item enabled gate: <c>(index, item) → bool</c>.</summary>
    public Func<int, T, bool>? IsItemEnabledTyped { get; init; }
}
