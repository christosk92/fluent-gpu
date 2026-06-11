# Pitfalls — symptom → cause → fix

[← Guide index](./README.md)

Read this before debugging. Each row is a real failure mode of the signals-first model. 🤖 Agents: scan the
**Symptom** column to match what you're seeing.

## Reactivity

| Symptom | Cause | Fix |
|---|---|---|
| A `ReactiveComponent` shows a stale value forever | `Setup()` runs **once**; `Ui.Text(sig.Value)` read the value one time | Use a binding: `new TextEl("") { TextBind = () => sig.Value }`. In `Setup()`, *everything dynamic* must be a bind / `For` / `Show`. |
| A binding never updates | The thunk used `.Peek()` (no subscribe) — or read a plain field, not a signal | Read `.Value` inside the thunk: `TransformBind = () => Affine2D.Translation(sig.Value, 0)`. `.Value` subscribes the binding effect; `.Peek()` does not. |
| Setting a signal does nothing visible | Whatever should react never *read* that signal (no subscription exists) | Make the renderer/binding read `signal.Value` (in `Render()` to re-render, or in a bind thunk to update a node). |
| A child component ignores new data from its parent | Constructor args are **frozen at mount** — the factory isn't re-invoked on parent re-render | Pass a **`Signal<T>`** or use **context**; the child reads `sig.Value`/`UseContext` and updates. Control factories should use a `Props` record + `Ctx.Provide` for runtime-changeable props. See [reactivity.md](./reactivity.md#props-dont-flow-through-constructors). |
| Infinite re-render / `Flush exceeded 1000 iterations` in logs | A `setState`/signal write happens **during render** (a render re-schedules itself) | Move the write into an event handler or `UseEffect`. Never write a signal a component reads from inside its own `Render()`. |
| Hooks throw / state lands in the wrong cell | Hooks called conditionally or in a loop — slot order shifted | Call hooks unconditionally, top-level, same order every render (the React rules-of-hooks; cells are slot-indexed). |
| `UseContext` returns the default, not the provided value | No `Ctx.Provide` ancestor for that channel above this component | Wrap the subtree in `Ctx.Provide(channel, value, child)`; the consumer must be a descendant. |
| `For`/`Show` doesn't update | The `When`/`Count`/`ItemAt` thunk read `.Peek()` or a non-signal | Read `.Value` inside the thunk so the boundary effect subscribes. |

## Performance

| Symptom | Cause | Fix |
|---|---|---|
| Dragging a slider tanks FPS | A `setState` per pointer-move re-renders the owning component every frame | Use `Slider.Bind(FloatSignal)` (compositor bypass), or hand-bind the value to `TransformBind`. Confirm `FrameStats.Rendered == false` on drag. |
| A small change relayouts the whole page | No layout boundary above the change → the up-walk reaches the root → full layout | Give the enclosing container explicit `Width`+`Height`+`ClipToBounds=true` so it's a boundary. See [rendering-and-performance.md](./rendering-and-performance.md#scoped-relayout). |
| `HotPhaseAllocBytes > 0` (zero-alloc check fails) | Allocation inside a bind thunk or hot effect body (`new`, LINQ, boxing, per-call closure) | Capture everything once at mount; the thunk must only read + write existing state. No allocation in phases 6–13. |
| Whole app re-renders on one interaction | State lives too high (at the root), so the root's render-effect runs | Move state down into the component that owns it; or bind the hot value instead of `setState`. |
| List with thousands of items is slow / leaks nodes | Not virtualized, or no stable `keyOf` | Use `Virtual.List`/`Repeater.ItemsRepeater` with `keyOf: i => stableId`. Only the window is realized; rows recycle. |
| Scroll re-realizes/relayouts every frame | (rare) something marks the viewport dirty each frame | In-window scroll is transform-only by design; check you aren't re-rendering the list component each frame (move its state out / bind it). |

## Layout & visuals

| Symptom | Cause | Fix |
|---|---|---|
| Content vanishes / is clipped to a tiny box | A `ClipToBounds` ancestor has zero/!wrong size, or a presented-size animation clipped it on frame 1 | Check the node's `Width`/`Height`; dump with `FG_DUMP=1`. Boundaries need real sizes. |
| A static `OffsetX`/`ScaleX`/`Opacity` you set "snaps back" each frame | An animation **or a `TransformBind`/`OpacityBind`** owns that channel; the reconciler won't also write the static value (else it'd fight the animator) | Drive the value through the bind/animation, not both. Pick one owner per channel. |
| Element not clickable | `HitTestVisible = false`, zero size, or no handler | Give it size and an `OnClick`/`OnPointerDown`; `HitTestVisible` defaults true. |
| Text doesn't wrap | `Wrap = NoWrap` (default) or no width constraint to wrap against | `text.Wrapped()` + a bounded width (explicit `Width` or a stretching parent). |
| Colors look wrong across themes | Hard-coded `ColorF` instead of tokens | Read `Tok.*` (e.g. `Tok.TextPrimary`, `Tok.FillCardDefault`); they follow `Tok.Use(theme)`. |

## Lifecycle & effects

| Symptom | Cause | Fix |
|---|---|---|
| `UseEffect` cleanup never runs | `UseEffect(Action, deps)` takes an `Action`, not `Func<Action>` — it has no return-cleanup channel | For tear-down inside a reactive effect/binding use `Reactive.OnCleanup(...)`; for component-scoped resources hold them in `UseRef` and release in your own teardown path. (Returning a cleanup from `UseEffect` is a known gap.) |
| Animation hook seems to do nothing | The hook seeds on `HostNode`, set after the first layout; or `deps` never changed | Animation hooks (`UseSpring` etc.) run in phase 6.5 once mounted; pass `deps` that change to re-target. |
| A removed node lingers briefly | It has an exit animation (`BoxEl.Animate` with an `Exit`) — it's an orphan animating out | Expected; it's reclaimed on settle. Not a leak. |
| State lost when a component's element *type* changes at a position | Type-flip at a position remounts (state-loss is intentional) | Keep the element type stable at a position, or use a `Key`/`For` to preserve identity. |

## Workflow (🤖 agents especially)

| Symptom | Cause | Fix |
|---|---|---|
| "It builds, ship it" but a seam regressed | Didn't run the cross-seam harness | `dotnet run --project src/FluentGpu.VerticalSlice` → require `ALL CHECKS PASSED` before claiming done. |
| Canon gate fails after editing `design/` | A stale/superseded token reappeared in the live design tree | Fix the token, or add `<!-- canon-allow: reason -->`; re-run `powershell -File design/check-canon.ps1`. Usage docs go in `docs/`, not `design/` (the gate scans `design/` only). |
| Added an `Element` type but it doesn't render | Not wired into the reconciler | Give it a free `ElementTypeId`, then handle it in `Reconciler.Mount`/`Update` (and `ChildrenOf` if it has children). |
| AOT publish fails at the native link step | The shell isn't a VS Developer environment (`link.exe`/`vswhere` not on PATH) | The managed/IL-AOT analysis still validated; run the publish from a VS Developer prompt for the final native link. Don't treat the link error as a code defect. |
| Claimed a fix works without evidence | No verification run | Show the harness output / `FrameStats` (`Rendered`, `ComponentsRendered`, `HotPhaseAllocBytes`). Evidence before assertions. |

## Quick self-check before committing an engine change

1. `dotnet build src/FluentGpu.VerticalSlice` — clean.
2. `dotnet run --project src/FluentGpu.VerticalSlice` — `ALL CHECKS PASSED`.
3. If you touched reactivity/layout/render: confirm `FrameStats.Rendered`/`ComponentsRendered`/`HotPhaseAllocBytes`
   are what you expect on the relevant interaction (add a `Check`).
4. If you edited `design/`: `powershell -File design/check-canon.ps1` exits 0.
