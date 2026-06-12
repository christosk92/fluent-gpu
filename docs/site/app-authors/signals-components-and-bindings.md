# Signals, components, and bindings

This is the page to read first. Everything else in FluentGpu is a consequence of the model here. If you internalize it, most "why didn't my UI update?" questions answer themselves.

The authoring surface is React/Reactor — immutable `Element` records, `Component`, and hooks you already know. The *core* underneath is a Solid-style signal graph. So the API feels familiar, but the cost model is different (and better): there is no full-tree re-render and no global dirty flag.

```csharp
using static FluentGpu.Dsl.Ui;   // VStack, HStack, Text, Heading, Button, Image, Grid, ScrollView…
using FluentGpu.Hooks;            // Component, ReactiveComponent, Embed, Ctx, Flow
using FluentGpu.Signals;          // Signal<T>, FloatSignal, Memo<T>, Prop<T>, Prop.Of, Reactive
using FluentGpu.Controls;         // Button, Slider, IconButton, NavigationView, Virtual…
```

## The model in one paragraph (`.Value` subscribes, `.Peek()` doesn't)

State lives in **signals**. **Reading** a signal's `.Value` inside a *reactive computation* subscribes that computation to the signal. **Writing** the signal re-runs (on the next frame's flush) exactly the computations that read it — nothing else. A component's render is one kind of computation; a property *binding* is a finer-grained one; a `Flow.For`/`Flow.Show` boundary is a third. There is no full-tree re-render and no global dirty flag.

```csharp
var n = new Signal<int>(0);
int x = n.Value;          // READ + SUBSCRIBE the current computation
int y = n.Peek();         // read WITHOUT subscribing
n.Value = 5;              // notifies subscribers iff 5 != current (value-equality gated)
n.Update(v => v + 1);     // functional read-modify-write off the latest committed value
```

Two consequences fall out of that single sentence, and they are the source of almost every reactivity bug:

- **If nothing visible updated, nothing *read* the signal you wrote.** The renderer/binding that should have reacted never subscribed. (`.Peek()` reads without subscribing — that is exactly what you want for an event-handler read like `n.Value = n.Peek() + 1`, and exactly what you do *not* want inside a bind thunk.)
- **Writes are deferred and value-gated.** A write schedules subscribers for the next flush and is dropped entirely if the new value equals the old (per the signal's comparer). So you never need to debounce equal writes.

> Honesty note: "deferred" means *coalesced to the next frame*, not asynchronous in a way you can race. The whole flush runs on the single UI thread (phase 3 of the frame). See [../../../design/subsystems/reconciler-hooks.md](../../../design/subsystems/reconciler-hooks.md) for the as-built reactive runtime.

## The three update mechanisms (cheapest first)

A change reaches pixels through one of three paths. Reach for the cheapest one that expresses your change — the difference is dramatic on hot paths (a slider drag, a scroll, a progress tick).

| Mechanism | What re-runs | Cost |
|---|---|---|
| **1. Binding** — a `Func`/signal on a node channel (`Transform`/`Opacity`/`Fill`) | one effect → one scene-node property | compositor-only: **no render, no reconcile, no layout** |
| **2. Granular re-render** — `UseState` / `UseSignal` read in `Render()` | the owning component's subtree | render + reconcile + **scoped** relayout of that subtree |
| **3. Reactive control-flow** — `Flow.For` / `Flow.Show` | one boundary effect → keyed diff | structural reconcile of that boundary only |

The gallery's **State & components** page (`src/FluentGpu.WindowsApp/GalleryPages.cs`) demonstrates all three side by side, each with a live render counter that *proves* what actually re-ran. The snippets below are lifted from it.

### 1. Binding — compositor-only (`Transform` / `Opacity` / `Fill`)

A binding is a thunk (or a signal) you assign to a node *channel*. The reconciler turns it into an effect **once at mount**; when a signal the thunk reads changes, that one effect writes one scene-node property and the frame recomposites. No component re-renders, no layout runs. This is the path for high-frequency scalars.

```csharp
var x = UseFloatSignal(0.3f);   // hot scalar → bind it, don't setState per move

Slider.Bind(x);                  // a drag writes x.Value directly — no setState per pointer-move

new BoxEl
{
    Width = 32, Height = 32,
    Transform = Prop.Of(() => Affine2D.Translation(x.Value * 184f, 0f)),  // reads x.Value ⇒ subscribes; compositor-only
    Fill      = Prop.Of(() => ColorF.Lerp(grey, Tok.AccentDefault, x.Value)),  // compositor-only
};
// elsewhere: x.Value = pointerX;  → these two thunks re-run → node transform/fill update → recomposite. NO re-render.
```

Not every channel is free, though — what the bind *writes* decides the cost:

| Bound channel | Writes | Cost |
|---|---|---|
| `Transform : Prop<Affine2D>` | local transform | compositor-only |
| `Opacity : Prop<float>` | opacity | compositor-only |
| `Fill : Prop<ColorF>` (`TextEl.Color`) | paint | compositor-only (re-record this node) |
| `Width` / `Height : Prop<float>` | layout size | **scoped relayout** |
| `TextEl.Text : Prop<string>` | text content | **scoped relayout** (metrics may change) |

> **Rule:** a bind thunk must read `.Value` (so the effect subscribes), never `.Peek()`. And prefer a `Transform`/`Opacity`/`Fill` bind over a `Width`/`Height`/`Text` bind whenever you can express the change as a transform — it skips layout entirely. (Symptom→fix table: [../../guide/pitfalls.md](../../guide/pitfalls.md).)

### 2. Granular re-render — `UseState` / `UseSignal`

`UseState` and `UseSignal` reads **inside `Render()`** subscribe the component's render-effect. A write re-renders only that component's subtree (plus a scoped relayout). This is the familiar React style — now granular by construction, because a component's render is just another computation in the signal graph.

```csharp
sealed class Counter : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);   // reading `count` subscribes THIS component's render-effect
        return VStack(gap: 8,
            Text($"Count: {count}"),
            Button.Accent("Increment", () => setCount(count + 1)));  // re-renders ONLY Counter's subtree
    }
}
```

`UseState` is just sugar over a `Signal<T>`: the value read subscribes the render-effect, and the setter writes the signal. The difference from `UseSignal` is *what subscribes* — `UseState` reads in `Render()` (so a write re-renders), whereas a `UseSignal` you *bind* to a channel never re-renders the component at all. The hot-scalar upgrade is literally `UseState` → `UseSignal`/`UseFloatSignal` plus flipping the assignment from a value to the signal — the property name doesn't change.

### 3. Reactive control-flow — `Flow.For` / `Flow.Show`

`Flow.For` (keyed list) and `Flow.Show` (conditional) are *boundary effects*. Their thunks read signals; when those inputs change, the boundary diffs/swaps **its own** children through the keyed reconciler — the enclosing component does **not** re-render.

```csharp
var open  = UseSignal(false);
var items = UseSignal(new List<string> { "Alpha", "Beta", "Gamma" });

VStack(gap: 10,
    Button.Standard("Toggle details", () => open.Value = !open.Peek()),
    Flow.Show(() => open.Value, detailsPanel, fallback),         // mount/unmount the branch

    Flow.For(() => items.Value.Count,
             i => Row(items.Value[i]),
             keyOf: i => items.Value[i]));                        // add/move/remove rows; state preserved by key
```

Mutate a collection signal by writing a **new instance** (writes are value-equality gated, so an in-place mutation followed by re-assigning the same reference is dropped):

```csharp
var next = new List<string>(items.Peek());
next.Reverse();
items.Value = next;   // a NEW list instance → the boundary effect re-runs → rows reorder by key
```

`Flow.For`/`Flow.Show` are the structural counterpart to a binding: a bind updates one node's *property*; a boundary updates the *set of children* at one position. Both leave the surrounding component untouched. Their thunks subscribe via `.Value` exactly like a bind — read `.Peek()` and the boundary will never update.

## The unified `Prop<T>` bindable channel (value / `Prop.Of` thunk / concrete signal)

Every bindable channel on an element is **one** `Prop<T>` property. It is not three overloaded properties; it is a single field that accepts three forms via implicit conversions:

```csharp
new BoxEl { Opacity = 0.5f };                            // 1) static value  → re-asserted on re-render (granular tier)
new BoxEl { Opacity = Prop.Of(() => op.Value * op.Value) };  // 2) derived Func thunk → compositor-only
new BoxEl { Opacity = op };                              // 3) concrete signal (FloatSignal/Signal<T>/Memo<T>) → compositor-only, no closure
```

Because every form flows through the *same* property type, a helper that takes `Prop<float>` accepts all three uniformly:

```csharp
static Element Chip(string label, Prop<float> opacity) =>
    new BoxEl { Width = 56, Height = 36, Fill = Tok.AccentDefault, Opacity = opacity };

Chip("value",  staticOp);                       // float        → Prop<float>
Chip("Func",   Prop.Of(() => op.Value * op.Value));  // Func<float>  → Prop<float>
Chip("signal", op);                             // FloatSignal  → Prop<float>
```

Things that trip people up, all enforced by the type (`src/FluentGpu.Engine/Foundation/Signals/Prop.cs`):

- **Inline lambdas need `Prop.Of`.** `Opacity = () => x` does *not* compile bare — C# cannot chain a lambda conversion into a user-defined conversion. Write `Opacity = Prop.Of(() => x)`, or assign a typed `Func<float>` local. A pure pass-through should assign the signal itself (`Opacity = op`) — no closure at all.
- **A bound channel is wired once at mount and ignores its static sibling.** Pushing a *fresh* thunk on a later re-render is ignored — change the *signal's value*, not the bind. (A bound `with`-clone stays bound.)
- **Never use `default(Prop<T>)` to mean "unset".** It is the static `default(T)` (e.g. `Opacity = 0`). Element initializers like `= 1f`/`= NaN` run the `T` conversion and survive `with` clones.
- **Through an `IReadSignal<T>`-typed variable, use the thunk form** (`chan = Prop.Of(() => s.Value)`) — implicit conversions from the *interface* are illegal in C# (CS0552). The direct `= signal` form works for the concrete `Signal<T>`/`FloatSignal`/`Memo<T>` types.

A bound channel and an animation/transition both want to own the same node property; pick one owner per channel (the reconciler will not also write a static value to a channel an animation drives — see the "snaps back each frame" row in [../../guide/pitfalls.md](../../guide/pitfalls.md)).

## State hooks

All hooks must be called **unconditionally and in the same order every render** — cells are slot-indexed (the React rules-of-hooks). No hooks inside `if`/loops. The surface lives on `Component` (`src/FluentGpu.Engine/Hooks/Component.cs`); the implementation is `src/FluentGpu.Engine/Hooks/RenderContext.cs`.

| Hook | Returns | Subscribes on read? | Use for |
|---|---|---|---|
| `UseState<T>(initial)` | `(T value, Action<T> set)` | reading `value` does | ordinary component state |
| `UseSignal<T>(initial)` | `Signal<T>` | only when you read `.Value` | a cell you own — bind it, or read `.Value` to subscribe |
| `UseFloatSignal(initial)` | `FloatSignal` | only when you read `.Value` | hot scalars (slider/scroll/progress) bound to a channel |
| `UseComputed<T>(fn)` | `Memo<T>` | reading `.Value` does | derived value (lazy, cached, recomputed when its inputs change) |
| `UseReducer<S,A>(reducer, init)` | `(S state, Action<A> dispatch)` | reading `state` does | folded state; `dispatch` applies immediately |
| `UseRef<T>(initial)` | `Ref<T>` | never | a mutable box that survives re-renders without triggering them |
| `UseMemo<T>(factory, deps)` | `T` | n/a (dep-array memo) | an expensive value recomputed only when `deps` change |

`UseComputed` is the Solid `createMemo`: it recomputes lazily from the signals its `fn` reads, caches the result, and only notifies *its* subscribers when the cached value actually changes — so a chain of memos doesn't fan out spurious work:

```csharp
var a = UseSignal(2);
var b = UseSignal(3);
var product = UseComputed(() => a.Value * b.Value);   // Memo<int>: cached, lazy, recomputes when a or b changes

new TextEl("") { Text = Prop.Of(() => $"{a.Value} × {b.Value} = {product.Value}") };
```

`UseReducer` folds state through a reducer; `dispatch` applies against the latest committed state, and reading the folded `state` subscribes the component (so a dispatch re-renders it):

```csharp
var (s, dispatch) = UseReducer<int, int>((state, action) => state + action, 0);
HStack(12,
    Button.Accent("dispatch +5", () => dispatch(5)),
    Button.Standard("dispatch −3", () => dispatch(-3)));
```

Two important distinctions:

- **`UseMemo` vs `UseComputed`.** `UseMemo` is a *dependency-array* memo (React style): it recomputes only when an entry in `deps` changes, and is **not** reactive to signals it reads internally. `UseComputed` *is* reactive — it tracks the signals its function reads. Use `UseMemo` to cache an expensive value keyed on explicit deps; use `UseComputed` for derived reactive state.
- **`UseRef`** is a stable mutable box (`Ref<T>` with a public `.Value`) that *never* subscribes anyone — perfect for a mutable counter, a captured handle, or "did I already do X". Writing `ref.Value` does not re-render.

## `Component` vs `ReactiveComponent` (`Render` re-runs vs `Setup` runs once)

| | `Component` (classic) | `ReactiveComponent` (signals-native) |
|---|---|---|
| Authoring method | `Element Render()` | `Element Setup()` |
| Runs | re-runs on its own state/context change | **runs once** at mount |
| Reactivity | re-render the subtree (granular) | **bindings / `Flow.For` / `Flow.Show` only** |
| Use when | normal UI; familiar React style | the hottest paths; you want zero re-renders |

`ReactiveComponent.Setup()` runs **exactly once**. Reading `signal.Value` there is a one-time read — to show a value that changes over time you **must** route it through a bound prop or a `Flow` boundary:

```csharp
sealed class Clock : ReactiveComponent
{
    public override Element Setup()
    {
        var t = UseSignal("00:00");
        return new TextEl("") { Text = t };       // ✅ signal-direct: the bind updates when t changes
        // return Ui.Text(t.Value);                // ❌ reads ONCE, never updates (Setup runs once!)
    }
}
```

The `❌` line is the single most common signals-native mistake. In `Setup()`, *everything dynamic* must be a bind / `For` / `Show`. (`Component.RunsOnce` is the gate — `false` for classic, sealed `true` for `ReactiveComponent`; the reconciler runs a run-once body untracked so its render-effect never re-subscribes.) Stay with plain `Component` until a profiler tells you a subtree re-renders too often.

## Effects (`UseEffect`, `UseLayoutEffect`, `Reactive.OnCleanup`, `Reactive.Untrack`)

| API | Runs | For |
|---|---|---|
| `UseEffect(fn, deps)` | after present (phase 12), when `deps` change | subscriptions, IO, side effects |
| `UseLayoutEffect(fn, deps)` | after layout, before paint (phase 6.5), with `Bounds` valid | measuring a node, seeding animations on it |
| `Reactive.OnCleanup(fn)` | when the enclosing computation re-runs / disposes | tear-down inside an effect/binding |
| `Reactive.Untrack(fn)` | runs `fn` without subscribing the current computation | read a signal without creating a dependency |

`UseEffect`/`UseLayoutEffect` use a **dependency array** (like React) — they re-run when a dep changes, *not* via signal tracking. They take an `Action`, not a `Func<Action>`: there is no return-cleanup channel. For tear-down inside a reactive computation use `Reactive.OnCleanup`; for component-scoped resources hold them in a `UseRef` and release them in your own path. (See the lifecycle rows in [../../guide/pitfalls.md](../../guide/pitfalls.md).)

`Reactive.Untrack` runs a function without subscribing the current computation to any signal it reads — use it when, inside a binding or render, you need a signal's *current* value as an input but do **not** want changes to it to retrigger you:

```csharp
// Re-run only when `mode` changes; read `count` for its current value without subscribing to count.
new TextEl("") { Text = Prop.Of(() => $"{mode.Value}: {Reactive.Untrack(() => count.Peek())}") };
```

> Allocation rule: effects (like bindings) are wired **once** and their bodies run on each change. The per-frame paint phases (6–13) must allocate **zero** managed bytes — don't `new`/LINQ/box inside a bind thunk or a hot effect body. The harness asserts `HotPhaseAllocBytes == 0` on steady frames; details in [../../guide/rendering-and-performance.md](../../guide/rendering-and-performance.md).

## Context (`Ctx.Provide` + `UseContext`; ambient `Viewport.Size` and `FrameDiagnostics.Current`)

Context lets a parent push data down without threading it through every layer. A `Context<T>` is a typed channel with a default; a provider stores a signal per node; `UseContext` resolves the **nearest** provider by walking up the scene tree and **subscribes** — so a value change re-renders exactly the consumers, and a consumer that re-renders for its own reasons still reads the right value.

```csharp
public static readonly Context<int> ThemeLevel = new(1);   // a channel + default

Ctx.Provide(ThemeLevel, level, Embed.Comp(() => new Consumer()));   // provide a value to a subtree

sealed class Consumer : Component
{
    public override Element Render()
    {
        var x = UseContext(ThemeLevel);   // reads the nearest provided value + subscribes; re-renders on change
        return Ui.Text($"level {x}");
    }
}
```

If `UseContext` returns the *default* unexpectedly, there is no `Ctx.Provide` ancestor for that channel above the consumer. Context is the idiomatic way to pass runtime-changeable data to a deeply nested component (the alternative — see the next section — is to hand it a `Signal<T>`).

Two ambient contexts the host publishes for you (only while something subscribes, so they're free when idle):

- **`Viewport.Size`** (`Context<Size2>`) — the client size in DIP, pushed each frame, for responsive layout (e.g. choosing a `NavigationView` display mode). Read with `UseContext(Viewport.Size)`.
- **`FrameDiagnostics.Current`** (`Context<FrameStats>`) — the previous frame's stats: `Rendered`, `ComponentsRendered`, `DrawCommandCount`, `HotPhaseAllocBytes`, `Fps`, `FrameMs`, etc. Read with `UseContext(FrameDiagnostics.Current)` to build an on-screen perf overlay — the same numbers you'd assert in a test to prove a binding stayed compositor-only (`Rendered == false` on a drag).

## Composing components (`Embed.Comp`)

A component becomes an element in another component's output through `Embed.Comp`:

```csharp
VStack(gap: 8,
    Embed.Comp(() => new Sidebar()),
    Embed.Comp(() => new MainView()));
```

`Embed.Comp<T>(Func<T>)` instantiates the child **once**, reuses it across the parent's re-renders (keyed by component type at that position), and reconciles its rendered output as that node's content. This is what lets the framework compose many small stateful components instead of one monolithic root — and because a parent re-render does *not* re-render its embedded children, composition stays cheap (no prop-diffing cascade).

> State-identity caveat: if the *element type* at a given position changes, that position remounts and its state is discarded (intentional). Keep the element type stable at a position, or use a key / `Flow.For` to preserve identity.

## Props don't flow through constructors — pass a `Signal` or context

This is the one place the model differs from React, and it trips everyone up at least once. A child component instance is created **once** by the `Embed.Comp` factory and **reused** across the parent's re-renders — the reconciler never re-invokes the factory. So any value you capture in the constructor is **frozen at mount**:

```csharp
// ❌ WRONG: `title` is captured once. If the parent re-renders with a new title, Header never sees it.
Embed.Comp(() => new Header(title));

// ✅ RIGHT: pass a SIGNAL (a stable reference). The child reads sig.Value and re-renders when it changes.
Embed.Comp(() => new Header(titleSignal));

sealed class Header : Component
{
    private readonly Signal<string> _title;
    public Header(Signal<string> title) => _title = title;
    public override Element Render() => Ui.Text(_title.Value);   // subscribes; updates when _title changes
}
```

Equivalently, push the data down via **context** (`Ctx.Provide` + `UseContext`). The takeaway: **parent→child data flows through signals or context, never through constructor arguments.** A parent re-rendering does not re-render its child components — each component re-renders only for its own state/context. (This is *also* why granular re-render is cheap: there is no prop-diffing cascade to walk.)

## Allocation discipline (bindings/effects wired once)

The per-frame paint phases (6–13) must do **zero** managed allocation — the headless harness asserts it. Bindings and effects are created **once at mount**; their thunks then run on each change without allocating. Concretely:

- Don't allocate inside a bind thunk or a hot effect body — no `new`, no LINQ, no boxing, no per-call closures.
- Capture the signals/objects a thunk needs **once** (the closure is built at mount, not per update).
- A signal `set` on the hot path is allocation-free (subscribers are a pre-sized list); a slider drag bound to a `FloatSignal` produces compositor-only frames with `Rendered == false` and `HotPhaseAllocBytes == 0`.

This is what makes the binding tier (mechanism 1) genuinely free per frame — and why "drag a slider" should never be a `setState`-per-pointer-move. (FluentGpu is **near-zero-allocation**, not "zero": the guarantee is *per-frame paint* phases 6–13; the reconcile/edge has bounded Gen0.)

## Verify, don't eyeball

After a reactivity change, run the headless cross-seam harness — it exercises every seam and prints PASS/FAIL (no GPU/window):

```text
dotnet build src/FluentGpu.VerticalSlice    # clean
dotnet run   --project src/FluentGpu.VerticalSlice    # must print "ALL CHECKS PASSED"
```

If you touched a binding/render path, assert the `FrameStats` you expect (`Rendered`, `ComponentsRendered`, `HotPhaseAllocBytes`) — that is how you *prove* a binding stayed compositor-only rather than quietly re-rendering. Evidence before assertions.

## See also

- **Guide — [reactivity.md](../../guide/reactivity.md)** — the canonical, longer treatment of this model (the source this page is grounded in).
- **Guide — [pitfalls.md](../../guide/pitfalls.md)** — symptom → cause → fix for the failure modes above; scan it before debugging.
- **Guide — [components-elements-layout.md](../../guide/components-elements-layout.md)** — the element zoo, flexbox & grid, modifiers, controls, theming.
- **Guide — [rendering-and-performance.md](../../guide/rendering-and-performance.md)** — what happens after you return an `Element`: the frame pipeline, scoped relayout + the boundary firewall, the compositor bypass, zero-alloc.
- **Design corpus** (architecture source-of-truth, canon-gated): [../../../design/subsystems/reconciler-hooks.md](../../../design/subsystems/reconciler-hooks.md) for the as-built reactive runtime, and [../../../design/SPEC-INDEX.md](../../../design/SPEC-INDEX.md) for precedence/ownership.
