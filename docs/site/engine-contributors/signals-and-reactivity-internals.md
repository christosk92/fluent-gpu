# Signals and reactivity internals

This page is for people **changing the reactive runtime**, not using it. If you are building an app, read
[Signals, components, and bindings](../app-authors/signals-components-and-bindings.md) and the guide's
[reactivity page](../../guide/reactivity.md) first — they own the mental model and the authoring surface. This page
covers how the machine works underneath, where each piece lives in `src/`, and how to prove a change is correct with the
headless harness.

The whole engine rests on one idea: **a change reaches pixels through a signal.** Reading a signal inside a reactive
computation subscribes that computation; writing the signal re-runs exactly the computations that read it. A component
render is a computation; a property binding is a finer-grained one; a `Flow.For`/`Flow.Show` boundary is a third. There
is no full-tree re-render and no global dirty flag. This is the SolidJS/Preact-Signals model, sitting under a
React-style authoring surface — and it is the *as-built* runtime, documented as the authority in
[`reconciler-hooks.md` §0bis](../../../design/subsystems/reconciler-hooks.md).

> Honesty note: "re-runs on the next flush" means *coalesced to the next frame*, not asynchronous in a way you can
> race. The flush is single-threaded (phase 3). And "alloc-free" means the **set→notify path** allocates nothing —
> subscriptions are wired once at mount; it does not mean signals are free to create per frame.

**Where the code is** (the engine where-to-change map):

| If you're changing… | Edit |
|---|---|
| The reactive core (computations, scheduler) | `src/FluentGpu.Engine/Foundation/Signals/ReactiveCore.cs` |
| `Signal<T>` / `FloatSignal` | `src/FluentGpu.Engine/Foundation/Signals/Signal.cs` |
| `Effect` / `ManagedEffect` | `src/FluentGpu.Engine/Foundation/Signals/Effect.cs` |
| `Memo<T>` | `src/FluentGpu.Engine/Foundation/Signals/Memo.cs` |
| The unified bindable channel `Prop<T>` | `src/FluentGpu.Engine/Foundation/Signals/Prop.cs` |
| Hook cells + stable call-order | `src/FluentGpu.Engine/Hooks/RenderContext.cs` |
| `Component` (run-once inferred) | `src/FluentGpu.Engine/Hooks/Component.cs` |
| Reconcile, render-effects, `For`/`Show`, context, bind wiring | `src/FluentGpu.Engine/Reconciler/Reconciler.cs` |
| The frame loop that calls `Flush` (phase 3) | `src/FluentGpu.Engine/Hosting/AppHost.cs` |
| Golden checks | `src/FluentGpu.VerticalSlice/Program.cs` |

> Namespace vs assembly: the reactive types are authored from the namespace `FluentGpu.Signals`, but their files live
> in the **`FluentGpu.Engine`** assembly under `Foundation/Signals/`. Per the module DAG in `reconciler-hooks.md §1`,
> `Dsl` and `Hooks` reference `Foundation` (for signals) but have **zero** reference to `Scene`; only the
> `Reconciler` bridges signals to the SoA `SceneStore`.

## The reactive core (subscribe-on-read, notify-on-write, value-equality gating)

Everything starts in `ReactiveCore.cs`. The two halves are **`Computation`** (anything that subscribes — effects,
memos, the component render-effect) and **`ReactiveRuntime`** (the per-`AppHost` scheduler). Tracking state — "which
computation is currently running" — is a single `[ThreadStatic]` slot, because the runtime is UI-thread-confined:

```csharp
internal static class Tracking
{
    [ThreadStatic] internal static Computation? Current;
}
```

A read subscribes by linking the live `Tracking.Current` to the source (and the source back to it, so the link can be
torn on the next run). A write marks every subscriber stale and asks the scheduler to run it. The three load-bearing
contracts:

1. **Subscribe-on-read.** `Signal<T>.Value`'s getter calls `Subscribe()` — if a computation is running, it adds itself
   to the signal's `_subs` and records the signal as one of its sources. `Peek()` skips that. A binding thunk that reads
   `.Peek()` instead of `.Value` is the canonical "my binding never updates" bug, by construction.
2. **Notify-on-write, value-gated.** The setter compares with the signal's `IEqualityComparer<T>` and **returns early
   if equal** — so an equal write costs nothing and never schedules. Only on a real change does it walk `_subs` calling
   `MarkStale()`.
3. **Deferred propagation.** `MarkStale()` does not *run* anything; for an effect it enqueues into the runtime's pending
   list, for a memo it cascades staleness to *its* subscribers (memos are lazy). The host drains the queue once per
   frame via `ReactiveRuntime.Flush()`.

`Computation` (in `ReactiveCore.cs`) is the base class that makes re-tracking correct. Each run, `RunComputation`
**unlinks old sources, disposes owned children + cleanups, sets `Tracking.Current = this`, runs the body, then
restores** — so a computation that reads a different set of signals this run subscribes to exactly that new set:

```csharp
internal void RunComputation(Action body)
{
    if (Disposed) return;
    DisposeChildrenAndCleanups();   // onCleanup callbacks + nested owned computations
    UnlinkSources();                // drop last run's subscriptions before re-tracking
    var prevC = Tracking.Current;
    Tracking.Current = this;
    try { body(); }
    finally { Tracking.Current = prevC; State = Clean; }
}
```

Ownership is **explicit**, not ambient (note the comment in `ReactiveCore.cs`): hook-created computations (a
`UseComputed` memo, a binding effect) persist across a component's re-renders and are disposed by the reconciler on
unmount — they are *not* auto-disposed by the enclosing render-effect's next run. You pass an `owner` only when you
want auto-cascade disposal. `ReactiveScope` is the stable, never-re-running owner used as a component's lifetime scope
and as the app root; disposing it cascades to everything it owns.

## Signal, Effect, Memo (the auto-tracked primitives)

These are three thin specializations of the core, each in its own file.

**`Signal<T>` / `FloatSignal`** (`Signal.cs`) are the observable cells. `Signal<T>` is the general one (generic
comparer, used by `UseState`/`UseSignal`/`UseReducer`); `FloatSignal` is the hot-path scalar — no comparer indirection,
exact-equality gate, for slider scrub / scroll offset / progress. `FloatSignal`'s notify loop is hand-inlined for that
reason. Both expose `IReadSignal<T>`:

```csharp
public interface IReadSignal<out T>
{
    T Value { get; }   // read AND subscribe the current computation
    T Peek();          // read WITHOUT subscribing
}
```

Two details a contributor must respect when touching `Signal.cs`:

- The notify loop iterates `_subs` **downward over the live list** (`for (int i = _subs.Count - 1; i >= 0; i--)`).
  This is safe *because* `MarkStale` only schedules effects / cascades memos — it never mutates `_subs` during the
  walk — and it avoids allocating a snapshot on the hot write path. If you ever make `MarkStale` able to subscribe or
  unsubscribe during a notify, this invariant breaks.
- `HasSubscribers` exists so the host can skip publishing an ambient value (e.g. `Viewport.Size`) that nothing reads.

**`Effect`** (`Effect.cs`) is a reactive side-effect: it runs its body once now, then re-runs whenever a signal it read
changes. A **property binding is an `Effect`** the reconciler creates at mount. On becoming stale it schedules itself;
on running it re-tracks via `RunComputation`. `ManagedEffect` is the subclassable variant for callers that own the run
body — the component render-effect (which re-runs `Render()` + reconciles the subtree) is a `ManagedEffect` subclass in
the reconciler.

```csharp
public sealed class Effect : Computation
{
    private protected override void OnStale() => Runtime.Schedule(this);
    internal override void RunStale() => RunComputation(_body);
}
```

**`Memo<T>`** (`Memo.cs`) is the derived value — Solid's `createMemo` / Vue's `computed`. It is **both** a computation
(it subscribes upstream) **and** a source (downstream subscribes to it). It is **pull-based**: becoming stale only
cascades to its subscribers; the recompute happens lazily on the next read, and it **only notifies downstream when the
cached value actually changes** (a second value-equality gate). That double-gate is what makes a memo a re-render
firewall: an upstream churn that recomputes to the same value stops there.

```csharp
public T Value
{
    get { if (State == Stale) Recompute(); SubscribeReader(); return _value; }
}
internal override void RunStale() { /* memos are pull-based: no scheduled run */ }
```

`SubscribeReader()` guards `c == this` so a memo reading itself cannot self-subscribe.

## `Prop<T>` — one bindable channel, three forms, wired once at mount

`Prop<T>` (`Prop.cs`) is the single bindable-channel type. The hard-won design rule — see the
[`SPEC-INDEX.md` Prop<T> row](../../../design/SPEC-INDEX.md) — is **one `Prop<T>` property per bindable channel**
(`BoxEl.Transform/Opacity/Fill/Width/Height`, `TextEl.Text/Color`, `ImageEl.Source/Placeholder`), never a parallel
static + `*Bind` pair. It is a `readonly struct` holding an inline `T` and a single `object? _ref`:

```csharp
private readonly T _value;        // static value, stored inline (never boxed)
private readonly object? _ref;    // null = static | Func<T> = thunk | IReadSignal<T> = signal-direct

public bool IsBound => _ref is not null;
```

The three forms, all via implicit conversion:

```csharp
new BoxEl { Opacity = 0.5f };                         // 1) static value — re-asserted each reconcile IFF !IsBound
new BoxEl { Opacity = Prop.Of(() => f(sig.Value)) };  // 2) derived Func<T> thunk — compositor bind
new BoxEl { Opacity = sig };                          // 3) signal-direct (Signal<T>/FloatSignal/Memo<T>) — no closure
```

Three things to internalize before editing `Prop.cs`:

- **Kind discrimination is a runtime type test on `_ref`, with no tag byte.** The static reconcile path reads only
  `IsBound` (one null test). Bind wiring (mount-time only) does the `_ref as Func<T>` / `_ref as IReadSignal<T>` test
  once per channel. `Value` is meaningful **only when `!IsBound`** — a bound `Prop`'s `_value` is the inert conversion
  seed (`default!`). Use `ValueOr(fallback)` for edge readers (e.g. exit-animation seeds) that need a scalar regardless.
- **Why `Prop.Of` exists.** C# cannot chain a lambda conversion into a user-defined conversion, so `Opacity = () => x`
  does **not** compile bare. `Prop.Of(() => x)` (or assigning a typed `Func<T>` local) makes it explicit. A concrete
  signal needs no wrapper.
- **Conversions from the `IReadSignal<T>` *interface* are illegal (CS0552)** — a user conversion on `Prop<T>` may only
  name types spelled in terms of `T`. That is why `FloatSignal`'s `implicit operator Prop<float>` is declared on
  `FloatSignal` (in `Signal.cs`), not on `Prop<T>`, and why `Prop<T>.FromSignal(IReadSignal<T>)` is a static factory.
  Through an interface-typed variable, use the thunk form: `chan = Prop.Of(() => s.Value)`.

The reconciler's contract: a **bound** channel is wired into an `Effect` **once at mount** and that effect is immortal
until unmount — *a fresh thunk supplied on a later re-render is ignored* (the signals-first rule: change the signal's
value, not the bind). A **static** channel is re-asserted on every reconcile **iff `!IsBound`**. This single
`!IsBound` chokepoint is what fixed the historical bound-value clobbers (Opacity reappearing at `1f`, Fill/TextColor
overwritten on re-render) by construction. Never use `default(Prop<T>)` to mean "unset" — it is the static `default(T)`.

## The hook cells and stable call-order (RenderContext)

`RenderContext.cs` is per-component hook storage. The whole hooks contract is the React one — **stable call order** —
implemented as an ordered `List<HookCell>` plus a cursor reset each render:

```csharp
private readonly List<HookCell> _cells = new();
private int _cursor;
private bool _mounted;

internal void BeginRender() => _cursor = 0;
internal void EndRender()   => _mounted = true;
```

The pattern in every hook is identical: on the first render (`!_mounted`) append a freshly-allocated cell; on every
later render read `_cells[_cursor]` and cast; then `_cursor++`. That is why **hooks must run in stable order — no hooks
inside `if`/loops**: a conditional hook shifts the cursor and every later cell is read at the wrong type. There is no
name-keying; position is identity.

```csharp
public Signal<T> UseSignal<T>(T initial)
{
    SignalCell<T> cell;
    if (!_mounted) { cell = new SignalCell<T>(new Signal<T>(initial)); _cells.Add(cell); }
    else cell = (SignalCell<T>)_cells[_cursor];
    _cursor++;
    return cell.Signal;
}
```

`UseState` is sugar over a signal: it returns `(sig.Value, v => sig.Value = v)`. **Reading `sig.Value` inside the
render-effect is what subscribes the component** — so a `setState` writes the signal, which schedules *only* this
component's render-effect for the next flush (granular, batched, value-equality-gated). `UseSignal`/`UseFloatSignal`
return the cell itself and do **not** read `.Value`, so a component that only *binds* a `UseSignal` never subscribes its
own render-effect — that is the "update text without a re-render" path. `UseComputed` wraps a `Memo<T>` in a disposable
cell (`MemoHookCell : IDisposableCell`) so unmount disposes it.

Cell types worth knowing when you add a hook: `SignalCell<T>`, `FloatSignalCell`, `MemoHookCell<T>` (disposable),
`EffectCell` (holds `Deps` + `Cleanup`), `MemoCell<T>` (the dep-array `UseMemo`), `RefHolderCell`, `AnimValueCell`.
`UseEffect`/`UseLayoutEffect` take a `DepKey deps = default` (compared via `DepDeps.Equals`) rather than signal
tracking — they re-run when a dep changes and stage into `PendingEffects` (phase 12, after present) or
`PendingLayoutEffects` (phase 6.5, after layout, `Bounds` valid); a `UseEffect(Func<Action?>)` with no deps is
auto-tracked (re-runs when a signal it read changes). `RunAllCleanups()` runs every effect cleanup and disposes every
`IDisposableCell` on unmount.

> The AOT-clean **`DepKey`** dep contract described in
> [`reconciler-hooks.md §3.2–3.4`](../../../design/subsystems/reconciler-hooks.md) is now the as-built shape (G1a):
> `RenderContext` stores deps as `DepKey` and compares via `DepDeps.Equals`, killing the old `params object[]`
> allocation and its boxing. It runs only when a component actually re-renders — never on the per-frame paint path. See
> [DepKey / GcDepTable](#depkey--gcdeptable-the-pure-scalar-16-byte-dep-contract) below.

## The one Component model (run-once inferred; the 3-signal memo skip)

`Component.cs` defines **one** base class. `Component` overrides `Render()`, and every `Render()` runs **tracked** — it
subscribes to the signals it reads and re-runs on its own state/context changes (granular). A `Render()` that reads
**no** signals is inferred to run once and never re-renders: **run-once is a consequence, not a mode.** G4b removed the
separate `ReactiveComponent` base, its `Setup()`, and the `RunsOnce` virtual — there is one tracked `Component` and
run-once is inferred, so there is no `RunsOnce` gate and no untracked `Setup()` to reason about.

The author-visible consequence is the #1 signals-native mistake: in a run-once `Render()`, `sig.Value` is a one-time
read, so a changing value must go through a bound prop (`Text = sig`, or `Text = Prop.Of(() => …)`) or `For`/`Show` —
never `Ui.Text(sig.Value)`.

**The 3-signal memo skip** is the reconciler-side decision (per component) of whether to re-run `Render()`. It is owned
by [`reconciler-hooks.md §6.3`](../../../design/subsystems/reconciler-hooks.md) and pinned in the
[`SPEC-INDEX.md` memo-skip row](../../../design/SPEC-INDEX.md). The gate is exactly three signals:

```
skip = !(selfTriggered || propsChanged || HasConsumedContextChanged)
```

- `selfTriggered` — this component (or a descendant routed here) called `setState`.
- `propsChanged` — record value-equality (`Component.ShouldUpdate`) / memo deps / func shallow-equal.
- `HasConsumedContextChanged` — a context this component **consumes** changed value (the per-consumer check).

The keystone correctness rule for anyone touching this: **`SubtreeDirty` is the traversal scope only, never a
skip-decision input.** It gates which subtrees the reconciler descends into; within a descended subtree every component
still runs the full 3-signal skip. Substituting `SubtreeDirty` for the per-consumer context check would wrongly skip a
context consumer that is not on any `setState` path and **drop the context update** — a regression that was caught and
reverted in hardening. Do not re-litigate it.

## The phase-3 reactive flush and auto-batching

`ReactiveRuntime` (in `ReactiveCore.cs`) is the scheduler — one per `AppHost`, single-threaded. It owns a pending list,
a batch depth, and a `FrameRequested` callback the host wires to wake its loop. A signal write that marks an effect
stale calls `Schedule`, which **deduplicates** via the computation's `Queued` flag and requests a frame:

```csharp
internal void Schedule(Computation c)
{
    if (c.Queued || c.Disposed) return;
    c.Queued = true;
    _pending.Add(c);
    if (_batchDepth == 0 && !_flushing) FrameRequested();
}
```

The host calls `Flush()` once per frame at **phase 3** (`AppHost.RunFrame`). It is the auto-batching point: many writes
between frames coalesce into one drain. `Flush` **swaps** the pending and draining lists so new work scheduled *during*
the drain lands in the now-empty `_pending` and is handled by the next loop iteration; it clears `Queued` per item and
re-runs only those still `Stale`:

```csharp
while (_pending.Count > 0)
{
    (_draining, _pending) = (_pending, _draining);   // swap; new work lands in the now-empty _pending
    var batch = _draining;
    for (int i = 0; i < batch.Count; i++)
    {
        var c = batch[i];
        c.Queued = false;
        if (!c.Disposed && c.State == Computation.Stale) c.RunStale();
    }
    batch.Clear();
    if (++guard > 1_000) { /* bail: likely a self-retriggering effect; drop + Diag.Event */ break; }
}
```

The `guard > 1_000` bail is the cycle-breaker for a self-retriggering effect (one that writes a signal it reads); it
logs a `Diag.Event("signals", …)` and drops the rest of the frame rather than hang. `Batch(action)` raises
`_batchDepth` so an explicit burst (a pointer-drag handler firing many writes) requests at most one frame at the end.

> The lists are pre-sized (`new(64)`) and reused (`Clear()`, never realloc'd on the steady path), so a normal flush is
> allocation-free. The richer **lane bitmask + `UpdateQueue`** model (`Lane` enum, MPSC ring, lane-selected drain,
> functional-updater fold) in [`reconciler-hooks.md §7.1–7.2`](../../../design/subsystems/reconciler-hooks.md) and the
> [`SPEC-INDEX.md` lane row](../../../design/SPEC-INDEX.md) is the **design target for concurrency** and is *not yet
> wired* — the as-built flush is the swap-and-drain above. Keep that distinction when editing.

## DepKey / GcDepTable (the pure-scalar 16-byte dep contract)

This is a **design contract** that the dep-array hooks (`UseEffect`/`UseLayoutEffect`/`UseMemo`/`UseCallback`) are
specified to lower onto; it is owned by [`reconciler-hooks.md §3.2–3.4`](../../../design/subsystems/reconciler-hooks.md)
and pinned in the [`SPEC-INDEX.md` DepKey row](../../../design/SPEC-INDEX.md). It exists to kill the per-render
`params object[]` allocation and the boxing of value-type deps.

`DepKey` is a **pure-scalar, blittable, 16-byte** struct — `long Bits` + `DepKind Kind` — with **no GC reference
anywhere**. Comparison is `Kind` + the 8 `Bits` (no `EqualityComparer<T>`, no boxing). The critical landmine, called
out in the canon:

> A `[StructLayout(Explicit)]` `[FieldOffset(0)]` union that overlays an object reference onto a scalar is **illegal
> CLR layout** (`TypeLoadException` / GC corruption). `DepKey` therefore stores **no** ref; `F64` deps go through
> `BitConverter.DoubleToInt64Bits` into `Bits`, never an overlapped `double` field.

Reference and delegate deps (which can't live in a blittable struct) are parked in a parallel managed **`GcDepTable`**
— an `object?[]` reset per render — and the `DepKey` stores the slot index with `Kind = Ref`. Equality of a `Ref` dep
is `ReferenceEquals` of last render's object at that slot vs this render's (React parity: callbacks/objects compare by
reference). The table double-buffers (`_prev`/`_cur`) and swaps-and-clears per render, so live objects stay rooted only
while the component is mounted and the reset is O(slots) — never on the per-frame paint path.

As-built (G1a), `RenderContext` uses this `DepKey`/`GcDepTable` path — deps store as `DepKey` and compare via
`DepDeps.Equals`, never the old `object[]`. The canonical signatures are the `ReadOnlySpan<DepKey>` overloads listed in
`reconciler-hooks.md §3.4`.

## Allocation rules (set/notify must stay alloc-free)

The per-frame paint phases (6–13) must do **zero managed allocation** — the harness asserts
`FrameStats.HotPhaseAllocBytes == 0` on steady frames. Bindings and effects are created **once at mount**; their thunks
then run on each change without allocating. Concretely, when you touch `ReactiveCore.cs` / `Signal.cs` /
`Reconciler.cs`:

- **The set→notify path allocates nothing.** `_subs` is a pre-sized `List<Computation>`; the notify loop walks the live
  list (no snapshot copy); the value-equality early-return drops equal writes before any work. Don't introduce a LINQ
  call, a closure, or a defensive `ToArray()` in that loop.
- **Don't allocate inside a bind thunk or a hot effect body** — no `new`, no LINQ, no boxing, no closures-per-call.
  Capture the signal/objects the thunk needs **once** (the closure is created at mount, not per update). The gallery's
  bound-row template (`Virtual.ListBound`, `GalleryPages.cs`) is the model: `Fill = Prop.Of(() => …)` /
  `Text = Prop.Of(() => …)` thunks built once per visible slot, re-run by writing an index signal — no element rebuild.
- **`Flush`'s lists are reused, not reallocated.** Keep the swap-and-`Clear()` discipline; a fresh `List<>` per flush
  would allocate every frame that has reactive work.
- Hook **cells** are an edge allocation (one per cell, at mount) — that is permitted (`foundations.md`: "GC refs only
  at the edge"). Adding a new hook means adding a `HookCell` subtype, not allocating per render.

## Verifying

Verify with the **headless harness**, never by eye — it is the source of truth for "does reactivity still work"
(~60 cross-seam golden checks, no GPU or window):

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

Four check groups in `src/FluentGpu.VerticalSlice/Program.cs` pin the signals contracts — run them after any change to
the core, hooks, `Prop<T>`, or the bind-wiring path:

- **`GranularityChecks`** (check 59) — mounts nested components, clicks one child, and asserts the click re-renders
  **only** the owning component: `Gran.Counts[0] == a0 + 1` while the sibling and parent counters are unchanged, and
  `FrameStats.ComponentsRendered == 1`. This is the "granular re-render, not the app" guarantee.
- **`SliderSignalChecks`** (check 60) — the slider-tank fix. Writes the bound `FloatSignal` (what a drag does) and
  asserts the thumb's `LocalTransform.Dx` moved, the owning component did **not** re-render
  (`SliderSignalProbe.Renders` unchanged), **and the frame was compositor-only** (`!f.Rendered` — no reconcile, no
  layout). This is the binding tier's reason to exist.
- **`PropNetClobberChecks`** (the `prop-net.*` checks) — the bound-channel ownership net. For every channel
  (`Fill`/`Opacity`/`Width`/`Height`/`Transform`/`Text`/`Color`/`Placeholder`), it fires the bound signal once, then
  forces an **owner re-render**, and asserts the **bound value survives** the re-render (the static re-assert does not
  clobber it). It also proves the re-render really rewrote static columns on a control node (`BorderWidth`,
  underline) — so the test can't pass vacuously.
- **`PropUnionChecks`** (the `prop.signal-direct.*` and `bind.mount-only.stale` checks) — the two bind kinds.
  Signal-direct: a concrete `Signal<T>`/`FloatSignal` assigned straight to a channel drives it with no user closure, and
  paint-channel writes stay compositor-only (`!st.Rendered`). Mount-only: a **fresh thunk on re-render is ignored** —
  after `rr.Value = 1` re-renders with a new `Prop.Of(() => 0.1f + 0.2f*r)`, the painted opacity is still `0.1f`
  (`0.3` would mean illegal re-wiring happened).

When you add a check, assert on `FrameStats` for the interaction you changed: `Rendered` (did reconcile/layout run),
`ComponentsRendered` (how many `Render()` bodies ran), and `HotPhaseAllocBytes` (**must be 0** on steady frames). GPU
pixels are not asserted here — that is a separate manual "needs-pixels" pass on the real D3D12 path.

## Canon links

- **[`reconciler-hooks.md`](../../../design/subsystems/reconciler-hooks.md)** — the owning subsystem doc. `§0bis` is
  the as-built signals-first model; `§3` the hook cells + `DepKey`/`GcDepTable`; `§6.3` the 3-signal memo skip;
  `§7.1–7.2` the (not-yet-wired) lane bitmask + phase-3 update queue.
- **[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md)** — the precedence authority. The **Prop<T>**, **DepKey**,
  **memo-skip**, and **lane bitmask + phase-3 update queue** rows are the canonical values for everything on this page.
- **[`subsystems/README.md`](../../../design/subsystems/README.md)** — the contract-ownership map (every hook, column,
  and seam → its one owning doc).

Sibling pages: the guide's [reactivity page](../../guide/reactivity.md) (the model), and the app-author
[signals, components, and bindings](../app-authors/signals-components-and-bindings.md) page (how to use it).
