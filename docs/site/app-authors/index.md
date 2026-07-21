# Building apps with FluentGpu

This is the entry point for **app authors** — you're building a UI *with* FluentGpu, not changing the engine
internals. If you're here to modify the framework itself, start at the developer guide's
[file & ownership map](../../guide/README.md#-agent--file--ownership-map-where-to-change-what) instead.

FluentGpu is a from-scratch, near-zero-allocation, NativeAOT, GPU-rendered UI framework for .NET 10. You write a
**React/Reactor** authoring surface — immutable `Element` records + `Component` + hooks — over a **signals-first**
(Solid-style) reactive core. The payoff of that core is the thing to internalize on day one:

> **A change to state reaches pixels through exactly one mechanism: a *signal*.** Reading a signal *subscribes* the
> current reactive computation; writing a signal *re-runs* only the computations that read it. There is **no full-app
> re-render** and **no global dirty flag** — every update is surgical.

Everything below is a consequence of that one rule. Get it, and most of the gotchas evaporate.

## The path through these docs

You do **not** need to read the whole guide before you ship something. Take this path:

1. **Run your first app — 2 minutes.** The [60-second cheat sheet](#60-second-cheat-sheet) below, then
   [getting-started.md](../../guide/getting-started.md) for `FluentApp.Run` and the (optional) frame loop.
2. **Learn the one model — the highest-leverage read.** [reactivity.md](../../guide/reactivity.md): signals, the three
   update mechanisms, `UseState`/`UseSignal`, the one `Component` model (run-once inferred), bindings,
   `Flow.For`/`Flow.Show`, and context. Treat it as required.
3. **Look up the building blocks as you need them.**
   [components-elements-layout.md](../../guide/components-elements-layout.md) — the hooks reference, the element zoo,
   flexbox & grid, modifiers, controls, navigation, virtualization, and theming.
4. **Make it fast (only when you have a hot path).**
   [rendering-and-performance.md](../../guide/rendering-and-performance.md) — the frame pipeline, scoped relayout + the
   boundary firewall, the compositor bypass, and an optimization decision guide.
5. **Debug by symptom.** [pitfalls.md](../../guide/pitfalls.md) is a **symptom → cause → fix** table. When something
   doesn't move, doesn't update, or tanks FPS, scan the Symptom column *first*.

Building WinUI-faithful custom controls is an advanced topic — see
[control-fidelity.md](../../guide/control-fidelity.md). The architecture source-of-truth (canon-gated) lives in the
[design corpus](../../../design/SPEC-INDEX.md); you rarely need it to build an app.

## 60-second cheat sheet

The whole surface, end to end. Copy it, change the body, run it.

```csharp
using FluentGpu;                         // FluentApp
using FluentGpu.Hooks;                   // Component, Embed, Ctx, Flow
using FluentGpu.Signals;                 // Signal<T>, FloatSignal, Memo<T>
using FluentGpu.Controls;                // Button, Slider, NavigationView, Virtual…
using static FluentGpu.Dsl.Ui;           // VStack, HStack, Text, Heading, Button (DSL builders), Image, Grid, ScrollView…

// One call brings up a DPI-aware window, D3D12, Mica + the system accent, the font system, and the frame loop.
FluentApp.Run(() => new App());

sealed class App : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);          // read `count` => subscribe; setCount => re-render THIS subtree
        return VStack(gap: 12,
            Heading("Hello, FluentGpu"),
            Text($"Clicked {count} times"),
            Button.Accent("Click me", () => setCount(count + 1)));
    }
}
```

A few patterns you'll reach for constantly:

```csharp
// Embed a stateful child component (it owns its own hooks/state).
Embed.Comp(() => new Sidebar());

// Provide a context value to a subtree; descendants read it with UseContext (and re-render when it changes).
Ctx.Provide(ThemeName, "dark", Embed.Comp(() => new Child()));

// Reactive control-flow: a keyed list and a conditional. The thunks read `.Value` so the boundary subscribes.
Flow.For(() => items.Value,
         x => x.Id,
         (x, i) => Row(x));                           // add/move/remove rows — state preserved by key
Flow.Show(() => open.Value, detailsPanel, fallback);  // mount/unmount one branch
```

`FluentApp.Run` (signature from `src/FluentGpu.WindowsApp/FluentApp.cs`) is the only host wiring you need:

```csharp
public static void Run(Func<Component> root, AppOptions? options = null);
```

That is the entire "getting an app on screen" story. For driving the loop yourself, or running headless in
tests/CI, see [getting-started.md](../../guide/getting-started.md#the-frame-loop-you-actually-call).

## The 10 rules that prevent 90% of bugs

These are the load-bearing rules of the signals-first model. Each one is the fix for a real, recurring failure mode
catalogued in [pitfalls.md](../../guide/pitfalls.md).

1. **To make something update, a signal it reads must change.** If you set a `Signal`/`UseState` and nothing moves, the
   thing that should react never *read* that signal. Reading `.Value` subscribes; reading `.Peek()` does **not**.

2. **`Component.Render()` re-runs on its OWN state/context changes only** — never because a parent re-rendered. So
   parent→child data flows through **signals or context**, *never* through constructor arguments. Constructor args are
   captured once at mount and frozen (the reconciler reuses the instance — it never re-invokes the factory). See
   [Props don't flow through constructors](../../guide/reactivity.md#props-dont-flow-through-constructors).

   ```csharp
   // ❌ `title` is frozen at mount; if the parent re-renders with a new title, Header never sees it.
   Embed.Comp(() => new Header(title));
   // ✅ pass a Signal (a stable reference); the child reads sig.Value and updates.
   Embed.Comp(() => new Header(titleSignal));
   ```

3. **A `Component.Render()` that reads no signals runs exactly once (run-once inferred).** Reading `signal.Value` there
   is a one-time read. To show a *changing* value you must use a **bound prop** — `Text = sig` (signal-direct), or
   `Text = Prop.Of(() => …)` for derived text — never `Ui.Text(sig.Value)`. This is the #1 signals-native mistake.

   ```csharp
   sealed class Clock : Component
   {
       public override Element Render()
       {
           var t = UseSignal("00:00");           // reads no signals => this Render runs once
           return new TextEl("") { Text = t };   // ✅ updates when t changes
           // return Ui.Text(t.Value);            // ❌ reads once, never updates (Render ran once!)
       }
   }
   ```

4. **A binding thunk must read `.Value`, not `.Peek()`.** The thunk runs inside the binding effect; `.Value` subscribes
   it so it re-runs on change. `.Peek()` wires a binding that never updates. (Same trap applies to the `Flow.For`
   `Count`/`ItemAt` and `Flow.Show` `When` thunks — read `.Value` inside them.)

5. **Bound `Transform`/`Opacity`/`Fill` are compositor-only — no render, no reconcile, no layout.** Bound
   `Width`/`Height`/`Text` trigger a **scoped** relayout. For a high-frequency scalar (slider scrub, scroll, progress,
   hover glow), prefer a transform/opacity bind over a layout bind — and over a `setState`-per-frame.

6. **Don't write a signal during render.** A `setState` (or signal write) inside `Render()` re-schedules the same
   render → an infinite loop (you'll see `Flush exceeded 1000 iterations`). Put the write in an event handler or a
   `UseEffect`.

7. **Hooks run in stable call order** — no hooks inside `if`/loops, same order every render. Cells are slot-indexed
   (the React rules-of-hooks). Call them unconditionally at the top.

8. **Give big containers an explicit size + `ClipToBounds` so relayout can't escape them.** A fixed-size, non-flexing,
   `ClipToBounds = true` container is a **boundary** that stops a scoped relayout from walking up to the page root. Size
   your cards and panes. See [scoped relayout](../../guide/rendering-and-performance.md#scoped-relayout).

9. **Keep state low.** State that lives at the root makes the root's render-effect run on every interaction. Move state
   *down* into the component that owns it, or bind the hot value instead of `setState`-ing it.

10. **Verify with the harness, not by eye.** Run `dotnet run --project src/FluentGpu.VerticalSlice` — ~60 headless
    cross-seam golden checks that print `[PASS]`/`[FAIL]` and `ALL CHECKS PASSED`, in seconds, with no GPU or window.
    For perf claims, read `FrameStats` (`Rendered`, `ComponentsRendered`, `HotPhaseAllocBytes`) — on a slider drag you
    want `Rendered == false`; on a steady frame you want `HotPhaseAllocBytes == 0`.

### One mental model, three update paths (cheapest first)

Rules 1, 5, and 9 all flow from the same picture. When you choose *how* to make a change reach the screen, reach for
the cheapest mechanism that fits:

| Mechanism | What re-runs | Cost | Use for |
|---|---|---|---|
| **Binding** (`Transform`/`Opacity`/`Fill` = a `Func`/signal) | one effect → one scene-node prop | compositor-only: **no render, reconcile, or layout** | high-frequency scalars: slider scrub, scroll, progress, hover glow |
| **Granular re-render** (`UseState`/`UseSignal`) | the owning component's subtree | render + reconcile that subtree + **scoped** relayout | normal UI state, text that shows a value |
| **Reactive control-flow** (`Flow.For`/`Flow.Show`) | one boundary effect → keyed diff of its children | structural reconcile of that boundary only | dynamic lists / conditionals |

Full reasoning: [reactivity.md → the three update mechanisms](../../guide/reactivity.md#the-three-update-mechanisms-cheapest-first).

### A real page, assembled

This is the shape of an actual gallery page (`src/FluentGpu.WindowsApp/Home.cs`) — a stateful selector, a context read
for navigation, and a list of embedded child cards. Note rules 2 and 9 in action: the selection lives in *this* page
and flows to the stateless `SelectorBar` helper as a value + callback (a stateful sub-component would freeze its
selection at mount).

```csharp
sealed class WelcomePage : Component
{
    public override Element Render()
    {
        var (tab, setTab) = UseState("recent");           // selection owned here (rule 9)
        var navigate = UseContext(NavigationView.Nav);    // read shell navigation from context (rule 2)

        var cards = new Element[Demos.Length];
        for (int i = 0; i < Demos.Length; i++)
        {
            int idx = i; var d = Demos[i];
            cards[i] = Embed.Comp(() => new SampleCard     // embed a stateful child per item…
            {
                Index = idx, Glyph = d.Glyph, Title = d.Title, Subtitle = d.Sub,
                OnOpen = () => navigate(d.Key),
            }) with { Key = d.Key };                       // …keyed so identity (and state) is stable
        }

        return ScrollView(new BoxEl
        {
            Direction = 1,
            Children =
            [
                Embed.Comp(() => new HomeHero()),
                new BoxEl { Margin = new Edges4(36, 8, 36, 12), Children = [SelectorBar(tab, setTab)] },
                AutoGrid(280f, 12f, 88f, cards) with { Padding = new Edges4(36, 0, 36, 36) },
            ],
        });
    }
}
```

## Where to go next

- **First app & hosting →** [getting-started.md](../../guide/getting-started.md)
- **The signals model (read this) →** [reactivity.md](../../guide/reactivity.md)
- **Elements, layout, controls, theming →** [components-elements-layout.md](../../guide/components-elements-layout.md)
- **Performance & the frame pipeline →** [rendering-and-performance.md](../../guide/rendering-and-performance.md)
- **Debugging by symptom →** [pitfalls.md](../../guide/pitfalls.md)
- **Building WinUI-faithful controls →** [control-fidelity.md](../../guide/control-fidelity.md)

Honest framing to keep in mind: FluentGpu is **near-zero-allocation**, not "zero" — it guarantees zero *managed*
allocation in the per-frame paint phases (6–13), with bounded Gen0 at the reconcile edge. And slowness is *decoupled,
not invincible*: the compositor bypass keeps high-frequency updates off the UI thread, but a sustained GPU stall still
bounds back to it. Build accordingly.
