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

Every bindable channel is ONE `Prop<T>` property with **three accepted forms**: a static value (`Opacity = 0.5f` — written at reconcile, granular re-render tier), a derived thunk (`Opacity = Prop.Of(() => f(sig.Value))`, or assign a typed `Func<T>` local — inline lambdas need `Prop.Of` because C# cannot chain a lambda conversion into a user conversion), or a **concrete signal** (`Opacity = sig` — signal-direct, no closure; `Signal<T>`/`FloatSignal`/`Memo<T>`; through an `IReadSignal<T>` parameter use the thunk form). A BOUND channel ignores its static sibling and is wired **once at mount** — a fresh thunk on re-render is ignored (change the signal's value, not the bind). `UseState` values feed the static form (setState → re-render); the hot-scalar upgrade is `UseState` → `UseSignal` and the assignment flips from value to signal with no property-name change. Never use `default(Prop<T>)` to mean "unset", and in a `cond ? value : signal` ternary put the `(Prop<T>)` cast on the value arm. `Prop<T>` is a property type, not a parameter type — factories take `T`/`Func<T>`/`Signal<T>` params.

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

## `Component` vs `ReactiveComponent`

| | `Component` (classic) | `ReactiveComponent` (signals-native) |
|---|---|---|
| Authoring method | `Element Render()` | `Element Setup()` |
| Runs | re-runs on its own state/context change | **runs once** at mount |
| Reactivity | re-render the subtree (granular) | **bindings / `For` / `Show` only** |
| Use when | normal UI; familiar React style | hottest paths; you want zero re-renders |

```csharp
// Signals-native: Setup() runs ONCE. To show a changing value you MUST bind it.
sealed class Clock : ReactiveComponent
{
    public override Element Setup()
    {
        var t = UseSignal("00:00");
        return new TextEl("") { Text = t };                   // ✅ signal-direct: updates when t changes
        // return Ui.Text(t.Value);                            // ❌ reads once, never updates (Setup runs once!)
    }
}
```

> 🤖 **AGENT:** the `❌` line above is the #1 signals-native mistake. In `Setup()`, `signal.Value` is a one-time read.
> Anything that should change over time must go through a **bound prop** (`Text`/`Transform`/… set to a Func/signal) or `For`/`Show`.

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

Built-in ambient contexts published by the host: `Viewport.Size` (`Context<Size2>`, client size in DIP — used for
responsive layout) and `FrameDiagnostics.Current` (`Context<FrameStats>`).

## Composing components — `Embed.Comp`

A component is embedded into another's output as an element:

```csharp
VStack(gap: 8,
    Embed.Comp(() => new Sidebar()),
    Embed.Comp(() => new MainView()));
```

## Props don't flow through constructors

This is the one place the model differs from React, and it trips people up. A child component instance is created
**once** (by the `Embed.Comp` factory) and **reused** across the parent's re-renders — the reconciler never re-invokes
the factory. So values captured in the constructor are **frozen at mount**:

```csharp
// ❌ WRONG: `title` is captured once; if the parent re-renders with a new title, Header never sees it.
Embed.Comp(() => new Header(title));

// ✅ RIGHT: pass a SIGNAL (a stable reference). The child reads sig.Value and re-renders when it changes.
Embed.Comp(() => new Header(titleSignal));
sealed class Header : Component {
    private readonly Signal<string> _title;
    public Header(Signal<string> title) => _title = title;
    public override Element Render() => Ui.Text(_title.Value);   // subscribes; updates when _title changes
}
```

Equivalently, pass data down via **context**. The takeaway: **parent→child data flows through signals or context, not
through constructor arguments.** A parent re-rendering does not re-render its child components — each component
re-renders only for its own state/context. (This is also why granular re-render is cheap: there's no prop-diffing
cascade.)

## Allocation discipline (why bindings/effects are wired once)

The per-frame paint phases (6–13) must do **zero managed allocation** (the harness asserts it). Bindings and effects
are created **once at mount**; their thunks then run each change without allocating. So:

- Don't allocate inside a bind thunk or a hot effect body (no `new`, no LINQ, no boxing, no closures-per-call).
- Capture the signal/objects the thunk needs **once** (the closure is created at mount, not per update).
- A signal `set` on the hot path is allocation-free (subscribers are a pre-sized list).

Next: **[components-elements-layout.md](./components-elements-layout.md)** for the element zoo, layout, controls, and
theming — or **[rendering-and-performance.md](./rendering-and-performance.md)** for what happens after you return an
`Element`.
