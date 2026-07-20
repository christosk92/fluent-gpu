# Skeleton loading — one UI, derived shimmer, blur-reveal swap

> **✅ Animation engine — signals-first rework landed + verified.** The reveal/swap and shimmer here ride the unified slab: the blur-reveal is an `Enter` `EnterExit.Blur` terminal and the shimmer pulse an ambient `Hz` cadence row (so a backgrounded tab's shimmer stops for free); `SkeletonDeriver`/`Skel.Region` are unchanged. Design, now implemented: [`../plans/animation-engine-rework-design.md`](../plans/animation-engine-rework-design.md).

Showing a shimmer while a UI loads usually means building **two UIs** — the skeleton and the real one — and hand-keeping
them in sync. FluentGpu makes it one source: you author the real UI, wrap the async region in `Skel.Region(...)`, and the
framework **derives the shimmer from that same UI**, keeps the parts you already have real, and swaps to the real content
with the [blur reveal](./motion-recipes.md) on load.

Live demo: the **Async & skeletons** gallery page (`src/FluentGpu.WindowsApp/AsyncSkeletonPage.cs`).

## The data spine — `Loadable<T>`

A `Loadable<T>` is a per-field async value: a `State` signal (`Pending | Ready | Failed`) + a `Value` signal. It is the
unit that survives *incremental* arrival — a field can be Ready while a sibling is still Pending.

```csharp
public sealed class Loadable<T> {
    public readonly Signal<byte> State;     // LoadState: Pending | Ready | Failed
    public readonly Signal<T> Value;
    public void SetReady(T v);              // the "data loaded" trigger
    public void SetFailed(Exception e);
    public Prop<T> Bind();                  // Text = field.Bind()  → re-resolves when the field flips Ready
}
```

Get one from a hook:
- `UseResource(loader, seed)` — kicks `loader(CancellationToken)` at mount, marshals the result to the UI thread (via
  `UsePost`), reloads on a `deps` change, and **cancels on unmount** (back-nav mid-load). Returns a `Resource<T>`; bind
  its `.Loadable` (the Pending/Ready/Failed spine) here. Also exposes `IsFetching`/`IsStale`/`Refresh()`/`Mutate()` —
  see [reactivity.md](reactivity.md#4-async-data--useresource-stale-while-revalidate).
- `UseLoadable(initial)` — a persistent loadable you drive yourself (`SetReady`/`SetFailed`).

## The boundary — `Skel.Region`

```csharp
var tracks = UseResource(LoadTracks, seed: Array.Empty<Track>());        // Resource<Track[]>

return new BoxEl { Direction = 1, Gap = 16f, Children = [
    Header(Seed.Cover, Seed.Title),                          // REAL on frame 1 — known on click, NOT in a region

    Skel.Region(tracks.Loadable,                             // the Loadable spine drives the shimmer/reveal
        rowTemplate: AlbumRow,                               // Track? -> Element : your ONE row shape
        count: Seed.TrackCount,                              // placeholder rows before data lands
        reveal: SkelReveal.StaggerRows,
        onFailed: () => ErrorCard("Couldn't load — Retry"),
        content: ts => Flow.For(() => ts, t => t.Id, (t, i) => AlbumRow(t))),
]};
```

While `tracks` is **Pending**, the region derives `count` shimmer rows from `rowTemplate(null)` and pulses them. On
**Ready** it mounts `content(value)` and blur-reveals the rows (staggered). On **Failed** it shows `onFailed`. The
`group:` token coordinates sibling regions' reveals into one settle window.

## Three grains, one mechanism

| Grain | How | Example |
| --- | --- | --- |
| **Sibling** (partial-known) | anything *outside* a region is real on frame 1 | the album cover + title |
| **Region** | `Skel.Region(loadable, …)` shimmers only its subtree | the track list |
| **Leaf** (incremental field) | `leaf.Pending(field)` shimmers one cell in place | a row's duration streaming in |

The leaf form lets a row be **real** (title visible) while one descendant cell still shimmers:

```csharp
new TextEl("") { Text = t.Duration.Bind(), Width = 48f }.Pending(t.Duration)
```

When `t.Duration.SetReady("3:14")` fires, just that cell reveals — the row keeps its identity, the header and title never
re-render (signals-first granularity).

## How the shimmer is derived

`SkeletonDeriver` walks your real element subtree once (a reconcile-edge event, never a paint phase): containers copy
their layout + chrome and recurse; text/image/box leaves become shimmer bars sized from their **declared** statics (a
bound dimension reads via `Prop.ValueOr`, never the inert 0; `Grow` is preserved so a Grow title bar fills like the
title). So the shimmer sits in the **identical layout slot** — no content-jump on swap. Override per node with
`el.Skeletonized(false)` (blank spacer) or `el.Skel(customShimmer)`.

Lists take an explicit `rowTemplate(Track?)` because `Flow.For` yields zero rows while `Data` is null — the row template
(your real row, factored to accept null) keeps lists single-source.

## The swap

On the Pending→Ready edge the region reconciles shimmer→real (the proven `Flow.Show` swap — no new scene column). The
shimmer **orphan-exits** with an opacity+blur fade (`EnterExit.Blur`) while the real rows blur-rise in
(`SoftReveal`/`SoftRevealStaggered`) — the two-layer cross-blur, in the same slot. The looping pulse is cancelled on the
exit so it can't pin the orphan (the engine still quiesces). Under `Motion.ReducedMotion` the swap is an instant snap.

Honest note: the swap frame is a **reconcile edge** (it allocates the new subtree's elements), so 0-managed-alloc is
asserted on the steady frames *after* the swap settles, not on the swap frame — the same discipline as any
mount/`Flow.Show` branch change (bounded Gen0 at the reconcile edge).

## Control-kit shapes

`Skeletons.ListItemRow(...)` and `Skeletons.CardBody(...)` are ready-made row/card shapes to pass as a `rowTemplate` or
drop in standalone.

## See also
- `src/FluentGpu.Engine/Hooks/{SkeletonRegion,SkeletonDeriver}.cs`, `Foundation/Signals/Loadable.cs` — the source.
- `docs/guide/motion-recipes.md` — the blur reveal the swap reuses.
