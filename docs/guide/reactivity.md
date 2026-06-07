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
    TransformBind = () => Affine2D.Translation(x.Value, 0f),  // reads x.Value => subscribes this binding
};
// elsewhere: x.Value = pointerX;  // → this binding re-runs → node transform updates → recomposite. NO re-render.
```

| Bind prop (on `BoxEl`) | Writes | Cost |
|---|---|---|
| `TransformBind : Func<Affine2D>` | `LocalTransform` | compositor-only |
| `OpacityBind : Func<float>` | `Opacity` | compositor-only |
| `FillBind : Func<ColorF>` | `Fill` | compositor-only (re-record this node) |
| `WidthBind`/`HeightBind : Func<float>` | layout size | **scoped relayout** |
| (`TextEl`) `TextBind : Func<string>` | text content | **scoped relayout** (metrics may change) |
| (`TextEl`) `ColorBind : Func<ColorF>` | text color | compositor-only |

> **Rule:** a bind thunk must read `.Value` (subscribes), not `.Peek()`. And prefer a transform/opacity/fill bind over
> a width/height/text bind when you can express the change as a transform — it skips layout entirely.

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
var items = UseSignal(new List<Track>());
var open  = UseSignal(false);

VStack(gap: 4,
    Flow.For(() => items.Value.Count,
             i => Row(items.Value[i]),
             keyOf: i => items.Value[i].Id),               // add/move/remove rows, state preserved by key
    Flow.Show(() => open.Value, detailsPanel, fallback));  // mount/unmount the branch
```

## The hooks (state side)

| Hook | Returns | Subscribes on read? | Use for |
|---|---|---|---|
| `UseState<T>(initial)` | `(T value, Action<T> set)` | reading `value` does | ordinary component state |
| `UseSignal<T>(initial)` | `Signal<T>` | only when you read `.Value` | a cell you own; bind it, or read `.Value` to subscribe |
| `UseFloatSignal(initial)` | `FloatSignal` | only when you read `.Value` | hot scalars (slider/scroll/progress) bound to channels |
| `UseComputed<T>(fn)` | `Memo<T>` | reading `.Value` does | derived value (lazy, cached, recomputed when inputs change) |
| `UseReducer<S,A>(reducer, init)` | `(S state, Action<A> dispatch)` | reading `state` does | folded state; `dispatch` applies immediately |
| `UseRef<T>(initial)` | `Ref<T>` | never | mutable box that survives re-renders without triggering them |
| `UseMemo<T>(factory, deps)` | `T` | n/a (dep-array memo) | expensive value recomputed only when `deps` change |

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
        return new TextEl("") { TextBind = () => t.Value };   // ✅ updates when t changes
        // return Ui.Text(t.Value);                            // ❌ reads once, never updates (Setup runs once!)
    }
}
```

> 🤖 **AGENT:** the `❌` line above is the #1 signals-native mistake. In `Setup()`, `signal.Value` is a one-time read.
> Anything that should change over time must go through a **bind** (`TextBind`/`TransformBind`/…) or `For`/`Show`.

## Effects

| Hook | Runs | For |
|---|---|---|
| `UseEffect(fn, deps)` | after present (phase 12), when `deps` change | subscriptions, IO, side effects |
| `UseLayoutEffect(fn, deps)` | after layout, before paint (phase 6.5), `Bounds` valid | measuring, seeding animations on the node |
| `Reactive.OnCleanup(fn)` | when the enclosing computation re-runs / disposes | tear-down inside an effect/binding |
| `Reactive.Untrack(fn)` | runs `fn` without subscribing the current computation | read a signal without creating a dependency |

`UseEffect`/`UseLayoutEffect` use a **dependency array** (like React) — they re-run when a dep changes, not via signal
tracking. The reactive **`Effect`** type (in `FluentGpu.Signals`) is the auto-tracked primitive that powers bindings.

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
