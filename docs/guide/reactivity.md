# Reactivity (signals) — the core model

[← Guide index](./README.md)

This is the most important page. Everything in the framework is a consequence of the model here.

## The model in one paragraph

State lives in **signals**. **Reading** a signal inside a *reactive computation* subscribes that computation to the
signal. **Writing** a signal re-runs (on the next frame's flush) exactly the computations that read it — nothing else.
A component's render is one kind of computation; a property *binding* is a finer-grained one. There is no full-tree
re-render and no global dirty flag. (This is the SolidJS/Preact-Signals model, under a React-style authoring surface.)

```csharp
var n = new Signal<int>(0);
// reads:
int x  = n.Value;   // subscribes the current computation
int y  = n.Peek();  // does NOT subscribe (read-only peek)
// writes:
n.Value = 5;            // notifies subscribers if 5 != current (value-equality gated)
n.Update(v => v + 1);   // functional update off the latest committed value
```

## The three update mechanisms (cheapest first)

### 1. Binding — compositor-only, no render/reconcile/layout
A binding is a thunk on an element that the reconciler turns into an effect at mount; when a signal it reads changes,
that one effect writes one scene-node property. Use for high-frequency scalars.

```csharp
var x = UseFloatSignal(0f);
new BoxEl {
    Width = 200, Height = 8,
    Transform = Prop.Of(() => Affine2D.Translation(x.Value, 0f)),  // reads x.Value => subscribes this binding
};
// elsewhere: x.Value = pointerX;  // → this binding re-runs → node transform updates → recomposite. NO re-render.
```

| Bind prop (on `BoxEl`) | Writes | Cost |
|---|---|---|
| `Transform : Prop<Affine2D>` | `LocalTransform` | compositor-only |
| `Opacity : Prop<float>` | `Opacity` | compositor-only |
| `Fill : Prop<ColorF>` | `Fill` | compositor-only (re-record this node) |
| `Width`/`Height : Prop<float>` | layout size | **scoped relayout** |
| (`TextEl`) `Text : Prop<string>` | text content | **scoped relayout** (metrics may change) |
| (`TextEl`) `Color : Prop<ColorF>` | text color | compositor-only |

Every bindable channel is ONE `Prop<T>` property with **three accepted forms**: a static value (`Opacity = 0.5f` — written at reconcile, granular re-render tier), a derived thunk (`Opacity = Prop.Of(() => f(sig.Value))`, or assign a typed `Func<T>` local — inline lambdas need `Prop.Of` because C# cannot chain a lambda conversion into a user conversion), or a **concrete signal** (`Opacity = sig` — signal-direct, no closure; `Signal<T>`/`FloatSignal`/`Memo<T>`; through an `IReadSignal<T>` parameter use the thunk form). A BOUND channel ignores its static sibling and is wired **once at mount** — a fresh thunk on re-render is ignored (change the signal's value, not the bind). `UseState` values feed the static form (setState → re-render); the hot-scalar upgrade is `UseState` → `UseSignal` and the assignment flips from value to signal with no property-name change. Never use `default(Prop<T>)` to mean "unset", and in a `cond ? value : signal` ternary put the `(Prop<T>)` cast on the value arm. `Prop<T>` is a property type, not a parameter type — factories take `T`/`Func<T>`/`Signal<T>` params. Because a bind wires **once at mount**, keep a channel's bound-vs-static shape **stable across renders** — flipping `Fill = staticColor` ↔ `Fill = signal` on a reused node silently loses (the new form never takes). A DEBUG-only tripwire (`BindContract`; folds out of Release; env kill-switch `FG_BIND_CONTRACT=0`) reports such a flip; a fresh thunk on re-render (bound→bound) is fine and is not flagged.

> **Rule:** a bind thunk must read `.Value` (subscribes), not `.Peek()`. And prefer a transform/opacity/fill bind over
> a width/height/text bind when you can express the change as a transform — it skips layout entirely.

Because the thunk is mount-owned, do not close over a value computed by a component render and expect a later render
to replace it. Move the reactive read inside the thunk:

```csharp
int snapshot = selected.Value;
new TextEl(Prop.Of(() => $"selected {snapshot}"));       // wrong: snapshot freezes at mount (FGRP002)
new TextEl(Prop.Of(() => $"selected {selected.Value}")); // correct: the mounted bind subscribes directly
```

Bound virtual rows have the same rule for both halves of their identity: the slot index **and the collection source**
must be reactive. Prefer `BoundItems.From(...)` / `BoundItems.Project(...)` with the typed
`ItemsView.CreateBound<T>` overload; `BoundItemScope<T>.Item` resolves one current source snapshot and the recycled
slot index together, so a same-count collection replacement cannot leave one cell bound to the mount-time list.

### 2. Granular re-render — re-render one component's subtree
`UseState`/`UseSignal` read in `Render()` subscribe the component's **render-effect**. A write re-renders only that
component's subtree (+ a scoped relayout). This is the familiar React style — now granular by construction.

```csharp
sealed class Counter : Component
{
    public override Element Render()
    {
        var (n, setN) = UseState(0);          // reading `n` subscribes THIS component
        return Button.Accent($"n = {n}", () => setN(n + 1));  // setN re-renders only Counter's subtree
    }
}
```

### 3. Reactive control-flow — restructure without a parent re-render
`Flow.For` (keyed list) and `Flow.Show` (conditional) are boundary effects. When their inputs change they diff/swap
their own children via the keyed reconciler — the enclosing component does **not** re-render.

```csharp
var items = UseSignal<IReadOnlyList<Track>>(new List<Track>());
var open  = UseSignal(false);

VStack(gap: 4,
    Flow.For(items,                                        // the collection signal (or a Func<IReadOnlyList<T>>)
             t => t.Id,                                    // key: a stable, unique per-item id — REQUIRED, never the index
             (t, i) => Row(t)),                            // add/move/remove rows, state preserved by key
    Flow.Show(() => open.Value, detailsPanel, fallback));  // mount/unmount the branch
```

`Flow.For<T>` is **typed and mandatory-keyed**. It snapshots the items source **once** per structural change (no
per-row re-read of the signal), diffs the rows by `key`, and preserves each row's node — and therefore its component
state — across insert/move/remove. The `key` must be a stable, unique per-item id: **never the index** (a reorder would
otherwise reassign state to the wrong row). A duplicate key trips a DEBUG tripwire. Overloads accept a
`Func<IReadOnlyList<T>>` or the collection signal directly, and a `(item)` or `(item, index)` row builder. A parent
re-render that rebuilds the `Flow.For` re-points the row closures in place (they never freeze at first mount).

### 4. Async data — `UseResource` (stale-while-revalidate)
`UseResource` kicks an async `loader` at mount, reloads when its `DepKey deps` change, and returns a `Resource<T>` — a
`Loadable<T>` spine (bind it straight into `Skel.Region`) plus fetch state and imperative controls. Every load is
**epoch-stamped**, so an older/slower fetch can never overwrite a newer one; the in-flight load and any stale timer are
cancelled on unmount.

```csharp
var album = UseResource(ct => svc.GetAlbumAsync(id, ct),
                        seed: Album.Empty,
                        deps: id,                                    // id changes ⇒ reload (component reused, no remount)
                        options: new ResourceOptions { StaleTimeMs = 30_000 });

Skel.Region(album.Loadable, AlbumRow, count: 8,                      // shimmer while Pending, reveal on Ready
    content: ts => Flow.For(() => ts.Tracks, t => t.Id, (t, i) => AlbumRow(t)));

// from an event: album.Refresh();  or  album.Mutate(optimisticValue);
```

| Member | Behaviour |
|---|---|
| `Loadable` | Pending/Ready/Failed value spine — bind into `Skel.Region` / a leaf `.Bind()` |
| `IsFetching` | `IReadSignal<bool>` — true while any load (initial / deps / refresh / mutate-revalidate) is in flight |
| `IsStale` | `IReadSignal<bool>` — flips true once the data is older than `StaleTimeMs` (immediately when 0, the default) |
| `LastError` | the last failure (kept even when a refresh failure leaves the prior `Ready` value visible) |
| `Refresh()` | re-run the loader, **keeping the current `Ready` value visible while it loads**; a refresh failure keeps the old value + sets `LastError` |
| `Mutate(v, refresh = true)` | write `v` as `Ready` **immediately** (optimistic), cancel any in-flight load, then revalidate |

`ResourceOptions`: `StaleTimeMs` (0 = stale on Ready; the `IsStale` flip is driven by the host frame-clock timer queue,
not a poller) and `KeepPreviousData` (a deps change keeps showing the previous `Ready` value while the new identity
loads, instead of resetting to `Pending(seed)`).

## The hooks (state side)

| Hook | Returns | Subscribes on read? | Use for |
|---|---|---|---|
| `UseState<T>(initial)` | `(T value, Action<T> set)` | reading `value` does | ordinary component state |
| `UseSignal<T>(initial)` | `Signal<T>` | only when you read `.Value` | a cell you own; bind it, or read `.Value` to subscribe |
| `UseFloatSignal(initial)` | `FloatSignal` | only when you read `.Value` | hot scalars (slider/scroll/progress) bound to channels |
| `UseComputed<T>(fn)` | `Memo<T>` | reading `.Value` does | derived value (lazy, cached, recomputed when inputs change) |
| `UseReducer<S,A>(reducer, init)` | `(S state, Action<A> dispatch)` | reading `state` does | folded state; `dispatch` applies immediately |
| `UseRef<T>(initial)` | `Ref<T>` | never | mutable box that survives re-renders without triggering them |
| `UseMemo<T>(factory, deps)` | `T` | n/a (deps-gated memo) | expensive value recomputed only when the `DepKey` `deps` changes (`default` = compute once) |

`UseState` is just sugar over a signal: the setter writes the signal; the value read subscribes the render-effect.

### Hook cells are keyed by call site — conditional and looped hooks are legal

Hook cells are stored **per call site**, not by a positional cursor. Each `Use*` captures its `[CallerFilePath]` +
`[CallerLineNumber]` (+ a per-render ordinal that disambiguates the same source line hit N times — a hook in a loop), so
a hook keeps the **same** cell across renders even when it is called **conditionally**:

```csharp
public override Element Render()
{
    var (name, setName) = UseState("");
    if (ShowEmail) { var (email, setEmail) = UseState(""); … }   // ✅ legal: a hook inside `if`
    foreach (var f in fields) { var s = UseSignal(f.Default); … } // ✅ legal: hooks in a loop (keyed per ordinal)
    var (phone, setPhone) = UseState("");                        // keeps its own cell regardless of the branch above
    return …;
}
```

A conditionally-skipped hook **keeps its state** for when the branch is re-entered, and it never shifts its neighbours'
cells (the failure mode of the old positional model). Two caveats: put loop hooks at the **end** so per-iteration state
stays aligned by ordinal when the count changes (append/remove at the end — reordering the middle re-associates state,
same as a keyed list without a `keyOf`); and don't write **two** `Use*` calls on one physical source line behind a
conditional (they share a line and would swap cell types). `FGRP005` remains as a compatibility lint, not a hard rule.

## One component model — run-once is inferred, not a mode

There is a single base: **`Component`** (override `Element Render()`). Every render runs **tracked** — it auto-subscribes
to the signals it reads and re-runs only when one of those changes. **A render that reads no signals subscribes to
nothing and therefore renders exactly once**; run-once is a *consequence* of not reading signals, not a separate class
or a flag. (There is no `ReactiveComponent`/`Setup()` any more — the old duality is deleted.)

So the "signals-native, zero-re-render" style is just a `Component` whose `Render()` reads no signals directly and
drives everything dynamic through **bindings / `For` / `Show`**:

```csharp
// Reads no signal directly → renders ONCE. To show a changing value you MUST bind it.
sealed class Clock : Component
{
    public override Element Render()
    {
        var t = UseSignal("00:00");
        return new TextEl("") { Text = t };                   // ✅ signal-direct: updates when t changes
        // return Ui.Text(t.Value);                            // ❌ reads once here → subscribes THIS render (re-render), not a live bind
    }
}
```

> 🤖 **AGENT:** the `❌` line is the #1 mistake. Reading `signal.Value` **in `Render()`** subscribes the whole render
> (coarse: re-runs `Render` on change). Anything that should change over time should instead go through a **bound prop**
> (`Text`/`Transform`/… set to a Func/signal) or `For`/`Show`, so only the affected node updates and the component
> itself never re-renders. Reach for a `.Value` read in render only when you genuinely want render to branch on it.

## Effects

| Hook | Runs | For |
|---|---|---|
| `UseEffect(fn)` | after present (phase 12); **auto-tracked** | subscriptions, IO, side effects that follow the signals they read |
| `UseEffect(fn, deps)` | after present (phase 12), when the `DepKey` `deps` changes | side effects keyed to explicit values (no tracking) |
| `UseLayoutEffect(fn[, deps])` | after layout, before paint (phase 6.5), `Bounds` valid | measuring, seeding animations on the node |
| `Reactive.OnCleanup(fn)` | when the enclosing computation re-runs / disposes | tear-down inside an effect/binding |
| `Reactive.Untrack(fn)` | runs `fn` without subscribing the current computation | read a signal without creating a dependency |

**Auto-tracking is the default.** `UseEffect(fn)` with no deps runs its body under signal-read tracking: any signal it
reads re-runs it, and tracking is **re-armed on every run** (a branch that reads a different signal next run follows that
one and drops the old — never a stale one-shot capture). An effect that reads no signal runs exactly once. The body still
executes in the passive-effect drain (after paint), never inline during `Flush`.

> **DEBUG tripwire — backwards write.** An effect (or bind thunk) that **writes a signal it also reads** in the same run
> re-marks itself stale → a convergence risk. A DEBUG-only tripwire (`BackwardsWriteGuard`; folds out of Release; env
> kill-switch `FG_BACKWARDS_WRITE=0`) reports it once. Derive the value, or split the read and the write across effects.

**`DepKey` deps are the explicit opt-in.** `UseEffect(fn, deps)` disables tracking and re-runs only when the `DepKey`
changes — the over-scoping escape ("run only when THIS changes"). `deps` is a 16-byte value key, not an array:

- Scalars and short tuples convert implicitly: `UseEffect(fn, count)`, `UseEffect(fn, open)`, `UseEffect(fn, (name, index))`.
- `DepKey.Empty` (the default) = **mount-once** — the body runs once and never re-runs.
- `>4` scalars: fold sub-keys with `DepKey.Combine(a, b)`, or `DepKey.From(HashCode.Combine(...))`.
- Reference deps: `DepKey.FromRef(obj)` re-fires on an **instance swap**, not an in-place mutation (identity, not `Equals`).
  A fresh lambda each render is a new identity ⇒ it re-runs every render — pass a stable delegate or a scalar key instead.
- Strings hash (XxHash64) — a ~2⁻⁶⁴ chance two distinct strings collide and miss a re-run (acceptable for dep gating).

**Cleanup return.** Return an `Action?` from the effect body (`UseEffect(() => { …; return () => dispose(); })`) and it runs
before each re-run and once at unmount — the React `useEffect` cleanup channel. Works on both the auto-tracked and
deps-gated forms. (Overload note: a lambda whose body `return`s an `Action` binds to the cleanup overload; write a block
body `() => { X(); }` for a fire-only effect.)

The reactive **`Effect`** type (in `FluentGpu.Signals`) is the always-eager auto-tracked primitive that powers bindings;
`UseSignalEffect` exposes it for adapter components that need eager (synchronous, inline-`Flush`) timing.

## Timers — debounce, throttle, timeout, interval

Four hooks schedule work on the host's **frame-clock timer queue** (`HostTimerQueue`, an engine-owned min-heap drained
at the top of the frame, before the reactive flush — so a fired timer's signal writes land in the same re-render). Use
these instead of `System.Threading.Timer` / `Task.Delay`: they never wake a background thread, they let the frame loop
**idle-quiesce** (the loop blocks until the earliest timer is due — a pending timer costs zero frames), and they pause
correctly with the component. They are **not** the media clock — playback position stays device-clock-derived.

| Hook | Returns | For |
|---|---|---|
| `UseDebouncedValue(source, ms)` | `IReadSignal<T>` | a signal that follows `source` after `ms` of quiet — **trailing edge** (search-as-you-type) |
| `UseThrottledValue(source, ms)` | `IReadSignal<T>` | follows `source` at most once per `ms` — **leading edge + trailing sample** |
| `UseTimeout(cb, ms[, deps])` | `TimerHandle` | fire `cb` once, `ms` from now; **restarts when `deps` change** (default = once from mount) |
| `UseInterval(tick, ms[, enabled])` | `void` | fire `tick` every `ms` — **auto-pauses while parked / minimized**, resumes cleanly |

`source` is an `IReadSignal<T>` **or** a `Func<T>` thunk (wrapped in a memo that auto-tracks the signals it reads). The
returned debounced/throttled signal updates with **zero re-render** — bind or read it like any signal.

**Debounce control — `Flush()` / `Cancel()`.** Take the handle with the out-overload
`UseDebouncedValue(source, ms, out DebounceHandle h)`:

- `h.Flush()` — commit the source's current value **now** and drop the pending fire (the "search on Enter" path).
- `h.Cancel()` — drop the pending fire without committing (the debounced signal keeps its last value).

**Timeout control — `TimerHandle{ Cancel(), Restart() }`.** `Cancel()` drops the pending fire; `Restart()` re-arms from
now. Both are generation-guarded, so a callback that comes due **after the component unmounts is a no-op** (every timer
cell cancels itself on unmount).

**Interval pausing.** `UseInterval` folds `UseIsActive()`: it stops ticking while the component's page is parked by
`Flow.KeepAlive` **or** the window is minimized/app-suspended, and re-arms when it comes back — so a background tab burns
no CPU. Pass `enabled: false` to pause it explicitly.

**Idle-quiesce + warm cadence.** A pending-but-future timer sets **no** wake reason — the loop still idles (0% CPU), and
its message-loop wait is shortened to reach the earliest due time; only a *due* timer forces exactly the frame that fires
it. Separately, after the last input the loop keeps rendering for a short **warm-cadence** hold (~1 s, real window) before
allowing full quiesce, so a follow-up interaction pays no cold-start ramp.

**Steady-state cost is zero.** The wrapper + watcher are allocated once at mount; a source change re-arms by a lazy heap
re-insert (a generation bump), so a quiet frame with an armed timer adds **0 bytes** to the hot phase.

## Context — `UseContext` + `Ctx.Provide`

Context values are signals under the hood. A provider stores a signal per node; `UseContext` resolves the nearest
provider by walking up the scene tree and **subscribes** — so a value change re-renders exactly the consumers, and a
consumer that re-renders for its own reasons still reads the right value (no context-stack to reconstruct).

```csharp
public static readonly Context<string> ThemeName = new("dark");           // a channel + default

Ctx.Provide(ThemeName, "light", Embed.Comp(() => new Child()));            // provide to a subtree

sealed class Child : Component
{
    public override Element Render() => Ui.Text(UseContext(ThemeName));     // reads + subscribes; re-renders on change
}
```

`UseContext` returns the channel **default** when no provider is in scope. For a dependency the component cannot render
without (a required service/store), use **`UseRequiredContext<T>(channel)`**: same resolve + subscribe path (including
the parked-subtree fallback so a `Flow.KeepAlive`-parked re-render still resolves), but it **throws**
`InvalidOperationException` naming the type when no provider resolves — and a provider carrying `null` for a
`Context<T?>` also throws. A missing provider becomes a loud error at the consumer instead of a silent default.

Built-in ambient contexts published by the host: `Viewport.Size` (`Context<Size2>`, client size in DIP — used for
responsive layout) and `FrameDiagnostics.Current` (`Context<FrameStats>`).

## Composing components — `Embed.Comp`

A component is embedded into another's output as an element:

```csharp
VStack(gap: 8,
    Embed.Comp(() => new Sidebar()),
    Embed.Comp(() => new MainView()));
```

## Props — re-pushed to the child (`Embed.Comp(props, factory)`)

A child component instance is created **once** (by the `Embed.Comp` factory) and **reused** across the parent's
re-renders — the reconciler never re-invokes the factory. So a value captured in the constructor or a plain field is
**frozen at mount**: a parent that later re-renders with a new value never delivers it.

The fix is the **props channel**: pass the data as the first argument to `Embed.Comp`, and the reconciler **re-pushes**
it to the reused child on every parent re-render. The child reads it with `UseProps<T>()`:

```csharp
// ✅ Pass props as a RECORD; the parent re-pushes them live on each re-render.
Embed.Comp(new HeaderProps(title, count), () => new Header());

sealed record HeaderProps(string Title, int Count);
sealed class Header : Component {
    public override Element Render() {
        var p = UseProps<HeaderProps>();     // subscribes THIS component; re-renders when a re-push changes the value
        return Ui.Text($"{p.Title} ({p.Count})");
    }
}
```

Delivery is **equality-gated**, exactly like a context-provider signal: a fresh-but-equal props record (use an immutable
`record` so value equality applies) is coalesced — **no child re-render**. A parent that hands back the *same* props
reference (a memoized/cached object) is short-circuited before the equality walk even runs (O(1)). So re-pushing costs
nothing when the data didn't change, and a delegate field in the record — which defeats record equality — will
re-render the child every parent render (the same trade-off context providers have always had; the **`[Props]`
generator** below is the cure — it makes delegate props latest-write so a fresh lambda never re-renders).

`UseProps<T>()` is **non-positional** (no hook cell): it may be called conditionally or after an early return, and it
throws (naming this component) if the component was mounted without props. For a component usable **both** with and
without props, use `UsePropsOrDefault<T>()` (returns `null` when propless). A changed **`Key`** still forces a full
remount (fresh instance, state reset) — that lives one level above the props channel, in the keyed child diff.

**Other parent→child mechanisms** (choose by shape):
- **A `Signal<T>`** passed once through the factory — a stable reference the child reads (`sig.Value`) and re-renders on;
  best for a *single* live value shared by reference (the controlled-input contract uses this).
- **Context** (`Ctx.Provide` + `UseContext`) — for *ambient* data consumed by many descendants at varying depths
  (theme, services, a store), where prop-drilling would be noise.
- **Re-pushed props** (above) — the default for concrete parent→child data: typed, local, equality-gated.

The takeaway: **a parent re-rendering does not re-render its child components** — each re-renders only for its own
state/context/props. Data reaches a child through the props channel, a signal, or context — never a frozen field. (This
is also why granular re-render is cheap: there's no prop-diffing cascade — only the channel a child actually reads.)

## `[Props]` — generated signal-backed props (the ergonomic form)

Writing the transport record + `UseProps<T>()` + hand-rolled per-field diffing by hand is boilerplate. Mark the
component `[Props] partial` and declare each prop as a **get-only partial property** with `[Prop]`; the source
generator emits all of it — into the same partial — with **zero reflection** (AOT-clean):

```csharp
using FluentGpu.Hooks;

[Props]
sealed partial class Header : Component {
    [Prop] public partial string Title  { get; }   // non-delegate → per-field Signal<T>
    [Prop] public partial int    Count  { get; }
    [Prop] public partial Action? OnTap { get; }    // delegate → stable latest-write forwarder

    public override Element Render()                 // reading Title/Count SUBSCRIBES this render (re-renders on change)
        => new BoxEl { OnClick = () => OnTap?.Invoke(),   // the forwarder invokes the NEWEST OnTap, no re-render needed
                       Children = [Ui.Text($"{Title} ({Count})")] };
}

// Mount it — the generated PropsData transport + Of(...) factory:
Header.Of(title, count, onTap);                      // ≡ Embed.Comp(new Header.PropsData(title, count, onTap), () => new Header())
```

What the generator emits into the partial:

- **Per non-delegate `[Prop]`** — a mount-allocated `Signal<T>`, a **subscribing getter** (`Title => _titleProp.Value`),
  and a **`TitleProp`** `IReadSignal<T>` **bind accessor**. Reading `Title` in `Render` subscribes this component (a
  re-push re-renders it); binding `TitleProp` into a node/child channel (`Opacity = Prop.Bind(alphaProp)`) updates
  **compositor-only** — no re-render, no reconcile, no layout.
- **Per delegate `[Prop]`** (`Action` / `Action<T1..T4>` / `Func`) — a **latest-write slot** behind a **stable
  forwarder**. A parent passing a *fresh but equivalent* lambda does **not** re-render the child (delegates have no
  signal); a handler that captured the forwarder always invokes the **newest** delegate. A delegate with **more than
  four parameters** degrades to a raw latest field (no stable forwarder — diagnostic `FGSG004`, Info; a captured
  reference is then a snapshot, not the newest).
- **`PropsData`** — the immutable transport record (one positional per `[Prop]`, declared order). **Its declared order
  is the `PropsData(...)` / `Of(...)` argument order** — keep the `[Prop]` list in the order callers expect.
- **`void IPropsHost.ApplyProps(object)`** — the delivery sink the reconciler calls at its reuse seam (wrapped in
  `Runtime.Batch`, so a multi-field re-push settles in **one** child re-render, never a torn intermediate). It
  reference-short-circuits an identical re-push (O(1)), then writes each field signal **equality-gated** — only
  *changed* fields notify — and assigns delegate slots without notifying. `MountComponent` seeds it before the first
  render, so the very first `Render` sees the props.
- **`Of(...)`** — the embed factory (defaults on the trailing nullable params). **`CurrentProps()` / `From(source)`** —
  a **snapshot** of the live values (see forwarding below).

**Collection-typed props draw a warning.** A `[Prop]` of `List<>` / `IReadOnlyList<>` / an array / `Dictionary<>` /
`HashSet<>` is backed by a default-comparer signal, so a mutated-in-place collection **never notifies** and a
fresh-but-equal one **always** re-renders. The generator emits **`FGSG005` (Warning)** advising an
immutable/keyed representation (e.g. `ImmutableArray<T>`, which has value semantics and is exempt) or a version stamp.
(Diagnostics: `FGSG001` a `[Prop]` that isn't a get-only partial property; `FGSG002` a non-`partial` `[Props]` class;
`FGSG003` a `[Props]` class not deriving `Component` — all Error; `FGSG004` wide delegate — Info; `FGSG005` collection —
Warning.) A build-time **`PropsManifest`** constant lists, per component, which props became signals vs delegate
forwarders — the skippability report, greppable and zero runtime cost.

**Forwarding a SUBSET of props to a child** (Solid's `splitProps` problem — passing part of a component's live props on
has two shapes, choose deliberately):

- **Reactivity-preserving (prefer this):** bind the typed **`XxxProp`** accessors into the child's props/binds
  (`child: new Panel.PropsData(title: this.Title, alpha: /* bind */ AlphaProp …)` or `Opacity = Prop.Bind(AlphaProp)`).
  The child's channel tracks the live signal, so later parent re-pushes keep flowing. For a **whole-record** edit, use
  record `with`: `parentProps with { Title = "x" }` — that IS the "merge" story (PropsData is a `record`).
- **Snapshot (documented COLLAPSE hazard):** `CurrentProps()` / `From(source)` capture the *current* values into a new
  `PropsData`. Passing that subset to a child **freezes** those fields at snapshot time (their live reactivity is lost) —
  use it only when a point-in-time copy is what you want.

Everything from the plain-record section still holds: delivery is reconcile-phase (outside the paint alloc window),
signals are allocated at **mount**, the forwarder lazily **once**; a changed `Key` still remounts.

## Allocation discipline (why bindings/effects are wired once)

The per-frame paint phases (6–13) must do **zero managed allocation** (the harness asserts it). Bindings and effects
are created **once at mount**; their thunks then run each change without allocating. So:

- Don't allocate inside a bind thunk or a hot effect body (no `new`, no LINQ, no boxing, no closures-per-call).
- Capture the signal/objects the thunk needs **once** (the closure is created at mount, not per update).
- A signal `set` on the hot path is allocation-free (subscribers are a pre-sized list).

Next: **[components-elements-layout.md](./components-elements-layout.md)** for the element zoo, layout, controls, and
theming — or **[rendering-and-performance.md](./rendering-and-performance.md)** for what happens after you return an
`Element`.
