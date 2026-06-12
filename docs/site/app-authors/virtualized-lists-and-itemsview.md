# Virtualized lists and ItemsView

You have a playlist with 100,000 tracks. WinUI's answer is `ListView` over an `ItemsRepeater` and a virtualizing
panel; FluentGpu's answer is the same idea, decomposed into pieces you pick from cheapest-first:
`Virtual.List` / `Virtual.ListBound` for the bare windowed viewport, `Repeater.ItemsRepeater` for a pluggable layout,
and `ItemsView` for the whole collection control (selection + keyboard + reorder). All four sit over **one** retained
viewport node that realizes only the visible window and recycles row nodes through the slab free-list.

This page is a cookbook. For *why* virtualization is one retained node plus a hook trio (and the recycle-correctness
invariants, the decode→measure loop break, the honest alloc claim), read the subsystem design:
[../../../design/subsystems/virtualization.md](../../../design/subsystems/virtualization.md). For the elements and
the signals mental model the row templates are built from, read
[../../guide/components-elements-layout.md](../../guide/components-elements-layout.md) and
[../../guide/reactivity.md](../../guide/reactivity.md).

> The mental model in one line: a virtualized list is **not a control and not a new frame phase**. It is a single
> node that, on the frames where the visible range changes, calls your `renderItem(i)` for `[first, last) + overscan`
> only, mints stable-keyed `Element`s, and feeds them to the keyed reconciler you already have. In-window scroll is a
> transform-only frame — no realize, no relayout.

---

## When to virtualize (10k+ rows, bounded live nodes)

Virtualize when the list is large enough that realizing every row would blow the node budget or the frame time. The
threshold is not a hard number — it is "would a screen's worth plus a few rows of overscan be far fewer live nodes
than the whole collection?" A 50-row settings list does not need it; a 5,000-row track list does.

The non-virtual escape hatches exist for the small case and are *deliberately* non-virtual:

- `Repeater.ItemsRepeater(..., RepeatLayout.Wrap(gap))` / `RepeatLayout.Inline(...)` build the **whole** child list.
  Use them for a toolbar, a tag cloud, a nav pane — collections small enough that windowing would cost more than it
  saves.
- A plain `ScrollView(VStack(0f, rows))` over a pre-built `Element[]` (the `ScrollPage` gallery shape) is fine for a
  few dozen rows. Scrolling is still layout-free (the viewport clips and offsets by a transform), but every row is a
  live node.

Everything else on this page realizes only the window. Two rules make that windowing correct:

1. **Provide a stable `keyOf`.** A row's identity (a track id, a playlist guid) — *not* its index — is what lets the
   keyed reconciler recognize a row that survived a scroll boundary and keep its node + component state across
   recycling. Without it the diff falls back to positional keys and row state can smear across recycled slots.
2. **Keep the list component from re-rendering every frame.** In-window scroll touches no managed state; it is a
   phase-7 transform write. If you find the whole list re-rendering as you scroll, the cause is almost always state
   that lives on the list component and changes each frame — move it down or bind it
   (see [../../guide/pitfalls.md](../../guide/pitfalls.md)).

> **Album art in rows.** Row `Fill` / `Foreground` must be static theme tokens (`Tok.TextPrimary`, `Tok.FillCardDefault`),
> never a per-track derived brush — that would thrash the brush cache at scroll velocity. A row thumbnail is an
> `ImageEl` (a `DrawImageCmd` + residency handle), which is Media's story, not Theme's. This is a hard invariant; see
> [../../../design/subsystems/virtualization.md](../../../design/subsystems/virtualization.md) §5.3.

---

## `Virtual.List` — the index-rebuild path (a Spotify-style track list)

`Virtual.List` is the uniform 1-D viewport: a fixed row extent, your `renderItem(i)` returning a fresh `Element`
per visible index, and a `keyOf`. This is the WaveeMusic "Liked Songs" shape.

```csharp
public static VirtualListEl List(int itemCount, float itemExtent, Func<int, Element> renderItem,
                                 Func<int, string>? keyOf = null, int overscan = 4)
```

The full demo (`src/FluentGpu.WindowsApp/ScrollDemo.cs`) is 5,000 rows at 56px, mouse-wheel scrolled, flat in memory
at any list size:

```csharp
sealed class TrackListDemo : Component
{
    const int N = 5_000;

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            // a normal header band above the scroller
            new BoxEl
            {
                Height = 64, Padding = new Edges4(24, 16, 24, 16), AlignItems = FlexAlign.Center,
                Fill = ColorF.FromRgba(0x18, 0x18, 0x18),
                Children = [Heading("Liked Songs"), Text($"   {N:N0} songs").Foreground(Grey)],
            },
            // the virtualized list fills the rest of the window and scrolls
            Virtual.List(N, 56f, Row, keyOf: i => "t" + i) with { Grow = 1f },
        ],
    };

    static Element Row(int i)
    {
        string title = Titles[i % Titles.Length];
        string artist = Artists[i % Artists.Length];
        return new BoxEl
        {
            Direction = 0, Height = 56, Gap = 14, Padding = new Edges4(24, 8, 24, 8),
            AlignItems = FlexAlign.Center,
            Fill = (i & 1) == 0 ? ColorF.FromRgba(0x1B, 0x1B, 0x1B) : ColorF.FromRgba(0x15, 0x15, 0x15),
            HoverFill = ColorF.FromRgba(0x2C, 0x2C, 0x2C),
            OnClick = () => { },   // hoverable + clickable rows
            Children =
            [
                new BoxEl { Width = 28, Children = [Text($"{i + 1}").Foreground(Grey)] },
                new BoxEl { Width = 40, Height = 40, Corners = CornerRadius4.All(4), Fill = AlbumTint(i) },
                new BoxEl
                {
                    Direction = 1, Grow = 1, Gap = 2,
                    Children = [Text(title), Text(artist).Foreground(Grey).FontSize(12f)],
                },
                Text($"{mins}:{secs:00}").Foreground(Grey),
            ],
        };
    }
}
```

Two things to copy from this:

- `Virtual.List(...) with { Grow = 1f }` makes the viewport **fill** the remaining space. A `Grow` viewport is a
  *hard viewport*: it never measures its content extent, so 5,000 (or 5,000,000) rows stay windowed. Without `Grow`
  it would natural-size to `itemCount × itemExtent`.
- `renderItem` rebuilds the row `Element` each time an index enters the window. That is fine here: only the ~window+overscan
  rows are ever built, and survivors across a scroll boundary keep their node (the keyed diff updates in place). When
  *every* visible cell would otherwise rebuild on a thumb-drag storm — 100k rows flying past — reach for the bound
  path below instead.

`Virtual.VariableList`, `Virtual.Grid`, `Virtual.Custom`, and the measured/grouped/lined-flow/spanning factories all
return the same `VirtualListEl` record and take the same `keyOf` / `overscan` arguments. They differ only in the
`IVirtualLayout` they carry — see [the layout catalog](#a-quick-tour-of-the-built-in-layouts) below.

---

## `Virtual.ListBound` — the recycler fast path (100k rows, bound row template with an index signal)

`Virtual.ListBound` is the **signals-first** recycler. Instead of a `Func<int, Element>` that rebuilds the row per
index, you pass a `Func<IReadSignal<int>, Element>` that builds the row template **once per visible slot** with an
*index signal*. Scrolling rebinds a slot by writing its signal — so only the bound thunks inside the row re-run. No
element rebuild, no reconcile, no node churn, no keys.

```csharp
public static VirtualListEl ListBound(int itemCount, float itemExtent, Func<IReadSignal<int>, Element> row,
                                       int overscan = 4)
```

The rule that makes this work: **everything in the row that varies by index must be a reactive bind that reads
`idx.Value`** — a `Text`/`Fill`/`Source`/`Placeholder` set to a `Prop.Of(() => …)` thunk — never a value captured at
build time. `Prop.Of(() => expr)` wraps an inline lambda as a `Prop<T>`; reading `idx.Value` inside it subscribes
that one property to the slot's index signal, so a rebind re-runs *only* that thunk and updates *only* that node
property (compositor-only for transforms/fills).

The 100k-row demo (`src/FluentGpu.WindowsApp/GalleryPages.cs`, `VirtualizationPage`):

```csharp
// BOUND row template (Virtual.ListBound): built ONCE per visible slot with an index SIGNAL — scrolling rebinds the
// slot by writing the signal, so only these bound Text/Fill/Source thunks re-run.
static Element Row(IReadSignal<int> idx)
{
    return new BoxEl
    {
        Direction = 0, Height = 48f, Gap = 12f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(16, 0, 16, 0),
        Fill = Prop.Of(() => (idx.Value % 2 == 0) ? RowEven : RowOdd),
        HoverFill = RowHover,
        Children =
        [
            new BoxEl
            {
                Width = 64f,
                Children = [new TextEl("") { Size = 13f, Color = IndexGrey, Text = Prop.Of(() => $"{idx.Value + 1}") }],
            },
            // real thumbnail; tint shows until it decodes, then cross-fades in
            new ImageEl
            {
                Width = 32f, Height = 32f, Corners = CornerRadius4.All(8f),
                Source = Prop.Of(() => Cover(idx.Value)), Placeholder = Prop.Of(() => TileTint(idx.Value)),
            },
            new BoxEl
            {
                Direction = 1, Gap = 2f, Grow = 1f, Justify = FlexJustify.Center,
                Children =
                [
                    new TextEl("") { Size = 14f, Color = Theme.WindowText, Text = Prop.Of(() => $"Item {idx.Value}") },
                    new TextEl("subtitle") { Size = 12f, Color = SubGrey },
                ],
            },
        ],
    };
}

public override Element Render() => new BoxEl
{
    Direction = 1, Grow = 1f, Gap = 16f, Padding = Edges4.All(24f),
    Children =
    [
        Heading("List virtualization"),
        Text("100,000 rows with real CDN thumbnails — visible slots are built once and REBOUND via index signals "
           + "as you scroll (no element rebuild); images decode off-thread, pack into the atlas, and evict "
           + "off-screen, so memory stays flat.") with { Wrap = TextWrap.Wrap },
        Virtual.ListBound(100000, 48f, Row) with { Grow = 1f },
    ],
};
```

Notice there is **no `keyOf`** — bound slots recycle positionally, so a key would move no node. Notice the static
subtitle (`"subtitle"`) is a plain value, not a thunk: it does not vary by index, so it costs nothing to leave
constant. The thumbnail `Source`/`Placeholder` are bound, so the same `ImageEl` slot re-requests a new picture as you
fling, and off-screen decodes evict — memory stays flat across the whole 100k range.

When to choose which:

| | `Virtual.List` | `Virtual.ListBound` |
|---|---|---|
| Row callback | `Func<int, Element>` rebuilt per index | `Func<IReadSignal<int>, Element>` built once per slot |
| Scroll-into-window cost | re-render the entered rows (bounded Gen0) | rebind signals (thunks re-run; no rebuild) |
| Keys | provide a stable `keyOf` | none (positional recycle) |
| Per-row component state / hooks | yes (survives recycle via the key) | no — the slot is one stable template |
| Best for | normal lists, rows with their own state | huge uniform lists, thumb-drag storms |

`Virtual.GridBound` is the same recycler over a card grid.

---

## `Virtual.Grid` and adaptive column counts (`Viewport.Size`)

`Virtual.Grid` virtualizes a uniform card grid **by row** — it realizes visible-rows × columns and recycles
identically:

```csharp
public static VirtualListEl Grid(int itemCount, int columns, float itemHeight, float gap,
                                 Func<int, Element> renderItem, Func<int, string>? keyOf = null, int overscan = 2)
```

To make the column count responsive, read the client size from the `Viewport.Size` context (a `Size2` in DIP) and
derive `columns` in `Render()`. The Iconography page (`src/FluentGpu.WindowsApp/IconsPage.cs`) does exactly this over
the 1,500-glyph Segoe Fluent Icons catalog:

```csharp
public override Element Render()
{
    var (query, setQuery) = UseState("");
    var (selectedCode, setSelected) = UseState("E700");
    var viewport = UseContext(Viewport.Size);

    var filtered = Filter(query);

    // Adapt the column count to the window (nav pane ≈ 320 + detail pane 320 + paddings).
    int columns = Math.Clamp((int)((viewport.Width - 720f) / 104f), 4, 12);

    var grid = Virtual.Grid(filtered.Length, columns, 92f, 8f,
        i => Tile(filtered[i], filtered[i].Code == selectedCode, setSelected),
        keyOf: i => filtered[i].Code);

    return new BoxEl
    {
        Direction = 1, Gap = 12f, Padding = Edges4.All(28), Grow = 1f,
        Children =
        [
            /* header + search box … */
            new BoxEl
            {
                Direction = 0, Gap = 16f, Grow = 1f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Grow = 1f, ClipToBounds = true, Children = [grid] },
                    DetailPane(selected),
                ],
            },
        ],
    };
}
```

The key moves: `columns` is recomputed each render from `viewport.Width`, the grid is given a stable `keyOf` (the icon
codepoint), and the grid is wrapped in a `ClipToBounds` `Grow` box so it fills the space left of the detail pane. When
the user resizes the window, the `Viewport.Size` context change re-renders this component, `columns` updates, and the
grid relays out — but it is still only realizing the visible rows.

For a *fully* automatic responsive grid that picks its own column count from a minimum column width, there is also
`AutoGrid(minColWidth, gap, rowHeight, …)` — but that is the non-virtual CSS-grid helper, for small responsive
galleries, not 100k cards.

---

## `Virtual.VariableList` — measured heights + anchoring

When rows are not a uniform height — a feed where each card is as tall as its content, group headers that measure
taller than items — use `Virtual.VariableList`. You give an *estimate* extent; each row realizes at the estimate,
gets corrected to its measured height on arrange, and the engine re-pins the scroll anchor so a correction *above* the
viewport never jumps the visible top.

```csharp
public static VirtualListEl VariableList(int itemCount, float estimatedExtent, Func<int, Element> renderItem,
                                         Func<int, string>? keyOf = null, int overscan = 4)
```

```csharp
Virtual.VariableList(feed.Count, estimatedExtent: 120f,
    renderItem: i => FeedCard(feed[i]),     // each card measures to its own content height
    keyOf: i => feed[i].Id) with { Grow = 1f }
```

Behind it is a Fenwick (binary-indexed) extent table that gives O(log n) offset↔index, estimated-then-corrected. The
same machinery is reachable through the *seam* form `Virtual.Measured(itemCount, IMeasuredVirtualLayout, …)` with a
`MeasuredStackVirtualLayout` — and through `Virtual.GroupedList(...)`, where group headers occupy flat indices of their
own and `StickyHeaderIndexAt` tells a presenter which header to pin. These stateful layouts own a measured-extent
table, so if the owning component re-renders you must **hoist the layout instance** (`UseMemo`) rather than construct
it inline each render. The factory remarks on each call out which ones are stateful.

> One hazard to avoid: **never bind a virtualized row's height to a late-arriving image `NaturalSize`.** That creates
> a decode → measure → relayout → re-realize → decode loop. Size the image slot to a fixed bucket and let the picture
> fill it (object-fit cover). The full reasoning is in
> [../../../design/subsystems/virtualization.md](../../../design/subsystems/virtualization.md) §6.3.

---

## `Repeater.ItemsRepeater` with `RepeatLayout` (Wrap / Grid / Custom)

`Repeater.ItemsRepeater` is the data-driven items panel (WinUI's `ItemsRepeater`): a `count`, a `template`, and a
pluggable `RepeatLayout`. It is one control where you swap the layout to get a stack, a card grid, a non-virtual wrap,
or your own geometry. The virtualizing layouts (Stack / Grid / Custom / Measured / LinedFlow / SpanGrid) realize only
the window; Wrap / Inline build the whole child list.

```csharp
public static Element ItemsRepeater(int count, Func<int, Element> template, in RepeatLayout layout,
                                    Func<int, string>? keyOf = null, int overscan = 4, /* lifecycle hooks … */)
```

`RepeatLayout` is a readonly struct (no per-call allocation) with one factory per shape:

```csharp
RepeatLayout.Stack(itemExtent, horizontal: false)   // virtualized uniform 1-D
RepeatLayout.Grid(columns, itemHeight, gap)          // virtualized uniform card grid (by row)
RepeatLayout.Custom(IVirtualLayout)                  // virtualized with ANY layout you implement
RepeatLayout.Measured(IMeasuredVirtualLayout)        // variable-extent estimate-then-correct + anchoring
RepeatLayout.LinedFlow(lineHeight, aspectRatio, …)   // the ItemsView photo-wall
RepeatLayout.SpanGrid(columns, rowHeight, gap, spanOf)  // uniform-row grid with item spanning (hero rows)
RepeatLayout.HorizontalGrid(rows, itemWidth, gap)    // horizontally-scrolling shelf
RepeatLayout.Wrap(gap)                               // NON-virtual flex-wrap (small collections)
RepeatLayout.Inline(horizontal, gap)                 // NON-virtual stack (nav panes, toolbars)
```

The repeater gallery (`src/FluentGpu.WindowsApp/RepeaterPage.cs`) shows three at once — note that the virtualizing ones
are wrapped in a fixed-height `BoxEl` so they get a concrete viewport to window against, and the non-virtual Wrap is
not:

```csharp
public override Element Render() => ScrollView(new BoxEl
{
    Direction = 1, Gap = 18, Padding = Edges4.All(24),
    Children =
    [
        Heading("ItemsRepeater & custom layouts"),

        Label("RepeatLayout.Wrap — non-virtual chips"),
        Repeater.ItemsRepeater(14, Chip, RepeatLayout.Wrap(8f)),

        Label("RepeatLayout.Grid — virtualized 4-column card grid (1,000 items)"),
        new BoxEl { Height = 260, Children =
            [Repeater.ItemsRepeater(1000, Card, RepeatLayout.Grid(4, 110f, 12f), keyOf: i => "c" + i)] },

        Label("RepeatLayout.Custom(StaggerLayout) — your own geometry (5,000 items)"),
        new BoxEl { Height = 260, Children =
            [Repeater.ItemsRepeater(5000, Row, RepeatLayout.Custom(new StaggerLayout(44f, 16f)), keyOf: i => "s" + i)] },
    ],
});
```

There is also a typed overload, `ItemsRepeater<T>(IReadOnlyList<T> items, Func<int, T, Element> template, …)` — the
WinUI `ItemsSource` + `ItemTemplate` pair — so the template receives `(index, item)` with no cast and no per-item
boxing.

> **Don't reach for per-item `TemplateParts` here.** Repeater's per-item customization goes through the item-template
> itself (and, on `ItemsView`, the `PartDelta` value seam below). A per-item `TemplateParts` modifier in a recycled
> scroll path is an allocation/recycling hazard — see [../../guide/control-fidelity.md](../../guide/control-fidelity.md)
> §6.

### Lifecycle and reorder hooks

`ItemsRepeater` exposes WinUI's lifecycle events as optional callbacks fired with item indices at realize time:
`elementPrepared`, `elementClearing`, `elementIndexChanged`, and `visibleRange(first, last)` (the prefetch hook). The
`transition: ItemCollectionTransition.Default` argument stamps an enter-fade / exit-fade / move-FLIP onto each item
root (167ms decelerate) — adds fade in, removes fade out, and a reorder slides the survivors. (Documented caveat: an
enter transition also plays when an item scrolls into the realized window as a fresh mount — WinUI's LinedFlow behaves
the same way.)

---

## Implementing a custom `IVirtualLayout` (allocation-free geometry)

Any deterministic, allocation-free geometry plugs into the virtualizer with **no engine changes**. Implement
`IVirtualLayout` (`src/FluentGpu.Scene/VirtualLayout.cs`) — three pure methods that the engine calls *only* on
realize/arrange frames (a steady in-window scroll never touches them), returning structs / via out-params so a custom
layout costs zero per-frame managed allocation:

```csharp
public interface IVirtualLayout
{
    // Total scroll-axis extent (the published ContentSize) for itemCount items.
    float ContentExtent(int itemCount, float crossSize);

    // The [first,last) item range to realize for scrollOffset (incl. overscan).
    void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan,
                out int first, out int last);

    // Content-space rect of item `index` (LOCAL to the scroll content origin).
    RectF ItemRect(int index, float crossSize);
}
```

The gallery's `StaggerLayout` (`src/FluentGpu.WindowsApp/RepeaterPage.cs`) is the complete proof — a single column
where each row is indented by a repeating step:

```csharp
sealed class StaggerLayout : IVirtualLayout
{
    readonly float _extent, _step;
    public StaggerLayout(float extent, float step) { _extent = extent; _step = step; }

    public float ContentExtent(int n, float cross) => n * _extent;

    public void Window(int n, float cross, float vp, float off, int over, out int first, out int last)
    {
        first = Math.Max(0, (int)MathF.Floor(off / _extent) - over);
        last = Math.Min(n, (int)MathF.Ceiling((off + vp) / _extent) + over);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        float indent = (i % 6) * _step;
        return new RectF(indent, i * _extent, MathF.Max(40f, cross - indent - 16f), _extent - 6f);
    }
}
```

Hand it to `Virtual.Custom(count, new StaggerLayout(44f, 16f), renderItem, keyOf: …)` or
`RepeatLayout.Custom(new StaggerLayout(44f, 16f))`. Rules for a correct layout:

- **Stay allocation-free in all three methods.** They are on the realize/arrange path; no `new`, no LINQ, no boxing.
  Built-ins that need a precomputed table (LinedFlow, SpanGrid) build it lazily and only when `(itemCount, crossSize)`
  change — but `StackVirtualLayout`/`GridVirtualLayout` are pure O(1) arithmetic with no table at all.
- **Keep `Window` cheap.** For a uniform layout it is `floor`/`ceil` arithmetic; for a non-uniform one, binary-search a
  monotonic column (see `SpanningGridVirtualLayout.LowerBoundRow`).
- **If your layout owns mutable state** (a built table), it is **stateful** — hoist the instance in a `UseMemo` so it
  survives re-renders instead of being reconstructed.
- For variable extents that must measure rows, implement `IMeasuredVirtualLayout` (the widened seam:
  `SetMeasured` / `OffsetOf` / `IndexAt`) instead and pair it with `Virtual.Measured` / `RepeatLayout.Measured`.

The built-ins (`StackVirtualLayout`, `GridVirtualLayout`, `HorizontalGridVirtualLayout`, `LinedFlowLayout`,
`SpanningGridVirtualLayout`, `MeasuredStackVirtualLayout`, `GroupedListVirtualLayout`) all live in that one file and
are worth reading as worked examples.

---

## `ItemsView` and its presets (List / Grid / Create; SelectorVisual chrome; SelectionModel)

`ItemsView` is the premiere collection control — a deliberate **superset** of WinUI's `ItemsView` that folds the old
`ListView` / `GridView` onto one substrate. Three pluggable axes, each available with every other (no WinUI-style
capability cliffs):

- **Layout** — any `RepeatLayout` (Stack, Grid, HorizontalStrip, LinedFlow, Measured, SpanGrid, or a custom seam
  layout), over one virtualized viewport.
- **Selection** — `ItemsSelectionMode.None / Single / Multiple / Extended`, backed by a range-based `SelectionModel`
  that is decoupled from realization (select-all over 50k stores one range and realizes nothing).
- **Selector visual** — `SelectorVisual.AccentPill` (the ListView accent bar), `Check` (the GridView corner check),
  `FullRow`, `Border` (the default `ItemContainer` ring), `None` (app-drawn), or a custom `ContainerFactory`.

Plus keyboard navigation (arrows / Home / End / PageUp-Down / Space / Enter / Ctrl+A / typeahead), focus rings,
`StartBringItemIntoView`, and built-in drag-reorder.

### The simple presets

The folded `ListView` / `GridView` surfaces are static presets (the control types no longer exist):

```csharp
// Vertical single-selectable list, AccentPill selector, controlled single-selection signal.
ItemsView.List(items, selectedIndex)

// Grid of labeled tiles, Check selector.
ItemsView.Grid(items, columns: 4, tileSize: 96f)
```

`ItemsView.List(items, selectedIndex)` takes a `Signal<int>` it keeps two-way-synced with the selection — write the
signal and the row re-skins; click a row and the signal updates. The full preset overloads
(`ItemsView.List(count, itemTemplate, selectionMode: …, canReorderItems: …, onReorder: …, …)` and the Grid equivalent)
add templated rows, the selection mode, and built-in reorder.

### The full surface — `ItemsView.Create`

```csharp
ItemsView.Create(
    itemCount, itemTemplate, layout,
    selectionMode: ItemsSelectionMode.Single,
    selection: model,                                  // SelectionModel, or null to let it own one
    selector: SelectorVisual.AccentPill,               // AccentPill | Check | FullRow | Border | None
    isItemInvokedEnabled: true,
    itemInvoked: i => Open(i),
    keyOf: i => items[i].Id,
    partDelta: (i, state) => /* per-item variation, see below */,
    overscan: 4)
```

Drive selection through the shared `SelectionModel` (`src/FluentGpu.Controls/SelectionModel.cs`): it is signals-first,
so any control reading `model.Version.Value` re-renders (and re-skins only its realized window) when selection
changes. Its programmatic API is `Select` / `Deselect` / `Toggle` / `SelectRange` / `SelectAll` / `DeselectAll` /
`InvertSelection`, all range-based and allocation-free. Hand the *same* model to two `ItemsView`s and they share a
selection.

`isItemInvokedEnabled` + `itemInvoked` follow WinUI's invoke matrix: with a selection mode active, Tap and Space
*select* without invoking, while Enter and DoubleTap *invoke*; with `SelectionMode.None`, DoubleTap never invokes.

For the imperative methods (`CurrentItemIndex`, `StartBringItemIntoView`, programmatic selection), pass an
`ItemsViewController` and the component wires it at mount:

```csharp
var controller = new ItemsViewController();
// … ItemsView.Create(..., controller: controller) …
controller.StartBringItemIntoView(index, alignmentRatio: float.NaN);   // minimal scroll; 0 = align start, 1 = align end
controller.Selection?.Select(index);
```

### Reorder

Reorder rides a displacement channel (`ItemDisplacement` / `DisplacementVersion`): displaced siblings glide aside via
an animated translate FLIP (the WinUI "part to make room") — a capability WinUI's own ItemsView lacks. The simplest way
to get it is the preset overloads with `canReorderItems: true` + `onReorder: (from, to) => Move(list, from, to)`; the
List preset wires the 200ms reorder dwell and the Grid preset the 300ms 2-D dwell for you.

---

## Per-item variation with `PartDelta` (zero-alloc, shape-stable) — and the banned per-item `TemplateParts` hazard

You often want rows that look *slightly* different — a highlighted "now playing" row, a dimmed unavailable track, a
tinted tag. The legal seam for that is `PartDelta`, **not** a per-item `TemplateParts` modifier.

`PartDelta` (`src/FluentGpu.Controls/SelectorVisuals.cs`) is a readonly record struct of nullable overrides applied as
a plain `with`-swap into the chrome's already-allocated `BoxEl`/`TextEl` during construction:

```csharp
public readonly record struct PartDelta(
    ColorF? Fill = null,
    ColorF? Foreground = null,
    float? Opacity = null,
    CornerRadius4? Corners = null,
    Edges4? Padding = null,
    ColorF? Border = null,
    string? Glyph = null)
{
    public static readonly PartDelta None = default;
}
```

You supply it as `partDelta: (i, state) => …` on `ItemsView.Create`. Every field is a nullable override — `null` keeps
the preset default exactly (so a `null` delta is byte-for-byte the prior chrome). It is resolved **once per realized
item** and passed by value into the selector builder:

```csharp
ItemsView.Create(tracks.Count, RowContent, RepeatLayout.Stack(56f),
    selector: SelectorVisual.AccentPill,
    keyOf: i => tracks[i].Id,
    partDelta: (i, state) => tracks[i].IsNowPlaying
        ? new PartDelta(Foreground: Tok.AccentDefault)
        : tracks[i].IsUnavailable
            ? new PartDelta(Opacity: 0.4f)
            : PartDelta.None);
```

Two constraints, both CI-enforced:

- The producing `Func` must be **pure-value** — no `new`/box/LINQ/`Animate` per call. It runs on the realize edge
  and feeds the shape-stable chrome; allocating per item would defeat the recycle guarantee.
- It carries **values only** (fill / foreground / opacity / corner / padding / border / glyph). It cannot restructure
  the chrome — that is what `ContainerFactory` / `SelectorVisual` are for.

**Why not per-item `TemplateParts`?** `TemplateParts` is the door for styling a *control's* internals
([../../guide/components-elements-layout.md](../../guide/components-elements-layout.md) → "Template parts"). But a
per-item `TemplateParts` modifier *in a recycled scroll path* allocates and reshapes per realized item, which breaks
recyclability and the steady-scroll zero-alloc contract. That is the banned hazard. `PartDelta` is the sanctioned
replacement: same expressive reach for per-item *appearance*, zero extra allocation, provably shape-stable. See
[../../guide/control-fidelity.md](../../guide/control-fidelity.md) §6.

---

## Stable `keyOf` and why in-window scroll is transform-only

Two threads run through this whole page; here they are stated plainly.

**Stable `keyOf` is what makes recycling correct.** A row's key is its *identity* — a track id, a playlist guid, the
icon codepoint — not its position. When you scroll one row's worth, 39 of 40 rows survive; the keyed reconciler
recognizes them by key as a contiguous run and emits a 1-enter / 1-exit diff with zero moves, so the survivors keep
their nodes (and any per-row component state). Get the key wrong — return the index, or a value that isn't unique — and
the diff can't tell a recycled slot from a new row, so state smears across slots and you pay full rebuilds. The one
path that *doesn't* take a key is `Virtual.ListBound`: its slots recycle positionally and rebind by signal, so a key
would move no node.

**In-window scroll is transform-only — by design.** When the viewport scrolls but no row crosses an item boundary,
nothing realizes and nothing relays out. The engine writes a single `-ScrollOffset` transform on the viewport's content
node in phase 7 (`TransformDirty` / `PaintDirty` only, never `LayoutDirty`); the recorder re-applies that transform to
the cached row quads and records nothing new. Your `IVirtualLayout` methods are not even called. Only when the offset
*crosses* an item edge does the window dirty, the entered rows realize (bounded Gen0 — `Element` records for the rows
that entered, plus a pooled `Element[]` window chunk), and the keyed diff runs. The honest claim, stated in the
subsystem doc: paint machinery (phases 6–13) is zero managed allocation; the realize delta at a boundary cross is
bounded Gen0. It is *near*-zero-allocation, not zero — and it is CI-gated.

If you ever see a scroll re-realizing or relaying out every frame, the list isn't the problem — something is
re-rendering the list *component* each frame. Move that state down or bind it. The full mechanism and the recycle
invariants are in [../../../design/subsystems/virtualization.md](../../../design/subsystems/virtualization.md); the
quick-fix table is in [../../guide/pitfalls.md](../../guide/pitfalls.md).

---

## A quick tour of the built-in layouts

Every factory below returns the same `VirtualListEl` and takes `keyOf` / `overscan`. They differ only in geometry; the
stateful ones (noted) must be hoisted in a `UseMemo` when the owner re-renders.

| Factory | Shape | Stateful? |
|---|---|---|
| `Virtual.List(n, itemExtent, …)` | uniform vertical stack | no |
| `Virtual.ListBound(n, itemExtent, row)` | uniform stack, signal-bound recycler | no |
| `Virtual.Grid(n, columns, itemHeight, gap, …)` | uniform card grid, by row | no |
| `Virtual.GridBound(n, columns, itemHeight, gap, row)` | card grid, signal-bound recycler | no |
| `Virtual.HorizontalGrid(n, rows, itemWidth, gap, …)` | horizontally-scrolling shelf | no |
| `Virtual.VariableList(n, estimatedExtent, …)` | measured heights + anchoring | (built-in path) |
| `Virtual.Measured(n, IMeasuredVirtualLayout, …)` | variable-extent over the seam | yes (hoist the layout) |
| `Virtual.GroupedList(n, headerIndices, …, out layout)` | grouped flat list + sticky-header hook | yes (hoist the layout) |
| `Virtual.LinedFlow(n, lineHeight, aspectRatio, …)` | the photo-wall (aspect-ratio widths) | yes (hoist the layout) |
| `Virtual.SpanGrid(n, columns, rowHeight, gap, spanOf, …)` | uniform-row grid with item spanning (hero rows) | yes (hoist the layout) |
| `Virtual.Custom(n, IVirtualLayout, …)` | your own geometry | depends on your layout |

---

## See also

- [../../guide/components-elements-layout.md](../../guide/components-elements-layout.md) — the element zoo, layout
  props, controls, and the `Virtualization` + `ItemsView` quick reference.
- [../../guide/reactivity.md](../../guide/reactivity.md) — signals, `Prop.Of`, and the three update mechanisms the
  bound row path rides.
- [../../guide/pitfalls.md](../../guide/pitfalls.md) — the "list is slow / leaks nodes" and "scroll re-realizes every
  frame" fixes.
- [../../guide/control-fidelity.md](../../guide/control-fidelity.md) §6 — the `PartDelta` value seam vs the banned
  per-item `TemplateParts` modifier.
- [../../../design/subsystems/virtualization.md](../../../design/subsystems/virtualization.md) — the authoritative
  subsystem design: the node model, the hook trio, recycle-correctness invariants, anchoring, grouping, incremental
  load, and the honest alloc story.
