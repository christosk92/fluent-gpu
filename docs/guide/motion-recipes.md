# Motion recipes — the Expressive Motion Kit

A library of named, production-tuned transitions adopted from [transitions.dev](https://transitions.dev), expressed on
FluentGpu's own animation engine. They ride the existing spring/eased tracks, the geometry channels, the **per-node
self-blur** channel, and an **expressive curve + token vocabulary** — so a short travel reads as a full, polished
motion.

> **This is an opt-in app-author palette.** Framework controls keep their WinUI/Fluent-2 curves for 1:1 parity (see
> `control-fidelity.md`). Reach for these recipes for *app surfaces* and *emphasis moments*, not to restyle the control
> kit. The expressive curves (`SmoothOut`, `Overshoot`, …) are deliberately *not* the WinUI curves.

Every recipe honours reduced motion (`Motion.ReducedMotion` — it no-ops, leaving the element at its resting/end state)
and allocates nothing per frame. Live demos: the **Motion recipes** gallery page (`src/FluentGpu.WindowsApp/MotionRecipesPage.cs`).

## How to call them

Recipes are **imperative seeders** — extension methods on `AnimEngine` that seed tracks on a *captured* node. Capture
a node with `OnRealized`, then call the recipe from an event handler or a `UseLayoutEffect`:

```csharp
using FluentGpu.Hooks;   // brings in the MotionRecipes extension methods

var dot = UseRef<NodeHandle>(default);
// …in the tree:  new BoxEl { OnRealized = h => dot.Value = h, … }

// on an event, or in a UseLayoutEffect keyed by a replay/trigger signal:
Context.Anim.PopIn(dot.Value, dirY: 1f, distance: 8f, blur: 2f);
```

Multi-node recipes take a span (`anim.PopInStaggered(digits.Value, staggerMs: 70f)`). A couple of common mount cases
also have own-node `Component` hooks (`this.UseSoftReveal()`), the counterpart to `MotionHooks.UseEntrance`. And three
are `LayoutTransition` presets for `BoxEl.Animate` (`MotionRecipes.PageSlide`, `.CardResize`, `.PanelReveal`).

## The recipe catalog

| Recipe | Call | What it does |
| --- | --- | --- |
| **Number pop-in** | `anim.PopIn(n)` / `anim.PopInStaggered(span)` | each char re-enters from a direction with a blurred slide (Pop curve); decimals stagger |
| **Error shake** | `anim.Shake(n)` | percussive ±X shake with overshoot — a per-segment-eased keyframe path (A,A,B,B legs) |
| **Skeleton reveal** | `anim.SkeletonPulse(bar)` + swap → `this.UseSoftReveal()` | placeholder breathes, then content cross-fades + cross-blurs in (layout-free) |
| **Success check** | `anim.SuccessCheck(n)` + `StrokeTrimEnd` 0→1 | fade + un-rotate (80°) + Y-bob + un-blur, while the checkmark path draws itself on |
| **Icon swap** | `anim.IconSwapIn(icon)` | the new glyph grows from 0.25 scale with a blurred fade-in |
| **Notification badge** | `anim.BadgePop(dot)` | slides onto the trigger and pops with a low-damping overshoot spring + un-blur |
| **Soft / texts reveal** | `anim.SoftReveal(n)` / `anim.SoftRevealStaggered(span)` | a blurred rise (TranslateY + Opacity + Blur), stagger 40ms for stacked lines |
| **Avatar group hover** | `anim.NeighborLift(span, active, hovered)` | lifts the item + neighbours with exponential falloff; bouncy spring-back on release |
| **Menu / modal / tooltip open** | `anim.ScaleOpen(n, fromScale)` | scale `fromScale`→1 + fade, growing from the node's `TransformOrigin` (set it to the trigger edge) |
| **Page side-by-side** | `Animate = MotionRecipes.PageSlide` | slide between two pages (list ↔ detail) with a cross-fade |
| **Card resize** | `Animate = MotionRecipes.CardResize` | tween a container's size through real layout (neighbours reflow), SmoothOut 300ms |
| **Panel reveal** | `Animate = MotionRecipes.PanelReveal` | slide a panel in (open 400ms / close 350ms asymmetry) |

## Decision rules — situation → recipe

Match the *visible element* first, then the verb (from transitions.dev's decision table):

- **A number updates** → number pop-in (`PopInStaggered`).
- **Form validation error / "this is wrong"** (invalid field, wrong PIN, duplicate name) → error shake (`Shake`).
- **Placeholder that loads then swaps to real content** (list row, card, profile header) → skeleton reveal.
- **Confirmation / success / "done" moment** (checkmark, payment processed, upload complete) → success check.
- **Two icons in the same slot** → icon swap (`IconSwapIn`).
- **A small dot floating on a trigger** → notification badge (`BadgePop`).
- **Stacked headline + supporting lines entering with rhythm** (hero copy, empty state, onboarding) → texts reveal.
- **Hovering an item in a horizontal stack** (avatars, chips, tag pills, reactions) → avatar group hover (`NeighborLift`).
- **A surface that grows from a trigger** → `ScaleOpen` (set `TransformOrigin` to the anchor edge).
- **Two screens, list ↔ detail or step 1 ↔ step 2** → page side-by-side (`PageSlide`).
- **An element changes width/height** → card resize (`CardResize`).
- **A panel slides into a region** → panel reveal (`PanelReveal`).

If two could fit, prefer the lower-overhead one.

## The blur channel

The recipes lean on a new **per-node self-blur**: `BoxEl.Blur` (a static σ in px) and `AnimChannel.BlurSigma` (animate it via
`UseTransition`/`UseKeyframes` or the recipes). When σ > 0 the recorder wraps the node's subtree in a `PushLayer{Blur}`
— the subtree renders to a pooled offscreen RT, gets a separable Gaussian, and composites once at the group alpha (the
same offscreen-layer machinery as `OpacityGroup`, plus the blur). It blurs the element's **own** pixels (CSS
`filter: blur()`), not the backdrop behind it. Composited only — never relayout.

## The expressive vocabulary

- **Curves** (`Foundation.Easing`): `SmoothOut` (the transitions.dev signature `cubic-bezier(0.22,1,0.36,1)`),
  `Overshoot`, `OvershootStrong`, `Pop`. The overshoot curves exceed 1.0 mid-flight (the spring-past-target look).
- **Tokens** (`Dsl.Expressive`): semantic durations (`Stagger` 40 … `VerySlow` 500ms), distances (4/6/8/12/30px),
  pre-open scales (0.96–0.99), and blur radii (2/3/8px) — distinct from the WinUI-flavoured `Motion`/`MotionSprings`.

## See also

- `src/FluentGpu.Engine/Hooks/MotionRecipes.cs` — the recipe source.
- `docs/guide/control-fidelity.md` — building WinUI-faithful controls (and why the kit is a *separate* palette).
- `design/subsystems/backdrop-effects-animation.md` — the animation/effects design canon.
