using FluentGpu.Animation;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Scroll-surface knobs for an <see cref="ItemsView"/> (grouped out of <see cref="ListOptions"/> so the common factory
/// call stays short). All forwarded onto the built <c>VirtualListEl</c> viewport.
/// </summary>
public sealed record ScrollOptions
{
    /// <summary>Scroll-position restoration key (see <c>VirtualListEl.ScrollKey</c>): a stable per-content identity so a
    /// revisit lands at the saved row on the first realized window. Null ⇒ no restoration.</summary>
    public string? ScrollKey { get; init; }
    /// <summary>Never draw the conscious scrollbar for the virtualized viewport (a paged surface navigates by its pager).</summary>
    public bool SuppressScrollBar { get; init; }
    /// <summary>Premium alpha-mask edge fade: feather the content's own alpha at the overflowing edges (one offscreen RT).</summary>
    public bool AutoEdgeFade { get; init; }
    /// <summary>Scroll-geometry observer (project a scalar, get the change) forwarded onto the viewport.</summary>
    public (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? OnScrollGeometryChanged { get; init; }
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
    /// <summary>Entrance / cold-realize choreography (bound path).</summary>
    public EntranceOptions? Entrance { get; init; }

    // ── research adjustment #16 — virtualization knobs (opt-in; unset ⇒ byte-identical to the pre-knob path) ──
    /// <summary>Recycle-pool discriminator: <c>index → contentType</c>. Heterogeneous rows only recycle/rebind within
    /// their own content-type pool — a cross-type reuse forces a full element rebuild instead of a cheap rebind. Null ⇒
    /// one homogeneous pool (today's behavior). BOUND path (<c>CreateBound</c>) only.</summary>
    public Func<int, int>? ContentType { get; init; }
    /// <summary>Pre-realize margin BEYOND the viewport, in PIXELS. Overscan is row-based (a row count); this is a pixel
    /// band the engine converts to rows against the average row extent. <see cref="float.NaN"/> (default) ⇒ row-based
    /// <see cref="Overscan"/> stays authoritative; a finite value overrides it.</summary>
    public float CacheExtentPx { get; init; } = float.NaN;
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
