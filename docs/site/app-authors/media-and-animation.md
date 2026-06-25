# Media and animation

> **✅ Animation engine — signals-first rework landed + verified.** The signals-first model is live: set a value/signal + a `Transition` and it interpolates from current; `UseSpringValue`/`UseAnimatedValue` for springs; `WhileHover`/`WhilePressed` for gestures; `Enter`/`Exit`/`Stagger` + a `Presence` boundary for entrance/exit; `Layout`/`MorphId` for shared-element. It all runs over one POD `AnimValue` slab + an analytical closed-form spring. The hooks/fields on this page still work (they seed the same slab; `AnimEngine` is its scheduler). Design, now implemented: [`../../plans/animation-engine-rework-design.md`](../../plans/animation-engine-rework-design.md).

Two things on this page have the same secret: **they reach pixels without re-rendering your component.** An
async image swaps its texture in place when the decode lands; an animation hook writes a transform every frame on
the compositor. Neither re-runs `Render()`, neither relayouts. That is the whole reason media and motion live
together here — they are the two big "below the signal" mechanisms, the ones that move pixels while your component
tree sits perfectly still.

If you have not read it yet, start with the signals mental model in
[reactivity](../../guide/reactivity.md) and the element/hook reference in
[components, elements & layout](../../guide/components-elements-layout.md). This page builds on both.

The runnable source for everything below is the gallery's **Animation** and **Images** pages
(`src/FluentGpu.WindowsApp/AnimationPage.cs`, `src/FluentGpu.WindowsApp/GalleryPages.cs`) — open them alongside
this doc.

---

## Async images

`Ui.Image(...)` returns an `ImageEl`: an async, cached, residency-pinned bitmap. You give it a source (a URL or a
local path) and a display size; it shows a flat **placeholder tint** until the decode lands, then cross-fades the
real pixels in. You never touch a decoder, a cache, or a GPU texture.

```csharp
using static FluentGpu.Dsl.Ui;

// Image(source, width, height, corners = 0, placeholder = null, blurHash = null, transition = null)
Image("https://i.scdn.co/image/ab67616d…", 150f, 150f, 8f, "#273E6C")
```

The full factory signature (`src/FluentGpu.Engine/Dsl/Factories.cs`):

```csharp
public static ImageEl Image(string source, float width, float height, float corners = 0f,
                            ColorF? placeholder = null, string? blurHash = null, ImageTransition? transition = null)
```

The placeholder argument also accepts a hex string (`"#273E6C"`) so a palette table reads cleanly. Corner radius
is just a number: **`0` is square, half the tile size is a circle** — the same decode feeds any shape.

```csharp
// One decode, three shapes (the gallery's "Corner-radius variants" card):
Image(cover, 80f, 80f, 0f,  "#273E6C");   // square
Image(cover, 80f, 80f, 12f, "#4F776C");   // rounded
Image(cover, 80f, 80f, 40f, "#5C496D");   // circle (radius = half of 80)
```

### Size variants and why you request a size

Decode is **constrained to the display size** you ask for — a 3000 px cover never materializes full-res in
memory; the worker decodes straight to the requested bucket. So request the size you will actually draw (×2 for
crispness at high DPI is a fine policy), and the cache keys on that logical extent, giving each size its own
residency slot:

```csharp
// Gallery "Size variants": one source URL, three requested sizes → three cache slots.
Image(cover48,  48f,  48f,  6f, tint);
Image(cover80,  80f,  80f,  6f, tint);
Image(cover120, 120f, 120f, 6f, tint);
```

### The pipeline behind the tile (WIC → disk cache → GPU)

When you mount an `Image`, the engine fetches the bytes over HTTP(S) or from disk, decodes them off the UI thread
on a worker pool (WIC on Windows, constrained to the bucket size), caches the result, and uploads it to a GPU
texture — then flips the tile from placeholder to image with a cross-fade. The decode worker pool, the bounded
request channel with priority-drop backpressure, the LRU residency budget, and the pin-while-visible rule are all
described in the engine design — see [the media pipeline subsystem](../../../design/subsystems/media-pipeline.md).
As an app author you only need three facts:

- **It is off-thread and cached.** Scrolling a wall of art does not block the UI thread.
- **Off-screen art evicts; returning re-requests.** Memory stays bounded; a scroll-back re-fades.
- **Bind the row height to your fixed tile size, never to the image's natural size.** Feeding a late-arriving
  `NaturalW/NaturalH` back into the layout of a virtualized row creates a decode→measure→relayout loop. The decode
  bucket is fixed; keep layout fixed too.

### The reveal transition

The placeholder→image cross-fade is the engine's shared motion vocabulary — a duration plus an
[`Easing`](#springs-and-tweens-the-named-curves) curve — so an image reveal animates exactly like every other
control. Override it per image, or set the app-wide default once at startup
(`src/FluentGpu.Engine/Foundation/ImageTransition.cs`):

```csharp
// App-wide default is a ~220ms FluentDecelerate fade. Disable per-image, or pick a custom fade:
Image(cover, 96f, 96f, 8f, transition: ImageTransition.None);                 // snap in, no fade
Image(cover, 96f, 96f, 8f, transition: ImageTransition.Fade(120f));           // faster fade
ImageTransition.Default = ImageTransition.Fade(300f, Easing.FluentDecelerate); // once, at startup
```

### Bound sources for recycled rows (the `ImageEl` record)

In a virtualized list the row is built **once per visible slot** and rebound as you scroll. For that you set the
unified-`Prop` channels on the record directly instead of calling the `Image(...)` factory, so the recycled slot
swaps its art by re-running a thunk — no element rebuild (`src/FluentGpu.Engine/Dsl/Element.cs`,
`src/FluentGpu.WindowsApp/GalleryPages.cs`):

```csharp
// A bound virtual row: Source/Placeholder are thunks that read the row's index signal.
new ImageEl
{
    Width = 32f, Height = 32f, Corners = CornerRadius4.All(8f),
    Source = Prop.Of(() => Cover(idx.Value)),         // re-requests art when idx changes
    Placeholder = Prop.Of(() => TileTint(idx.Value)), // tint follows the rebind
}
```

`ImageEl` exposes `Source`, `Placeholder` (both `Prop<T>` — a value, a `Func<T>`, or a signal), `Width`, `Height`,
`Corners`, `BlurHash`, and `Transition`. See [virtualized lists & ItemsView](./virtualized-lists-and-itemsview.md)
for the recycler fast path this feeds.

## Image hooks

`Ui.Image(...)` paints. The **`UseImage` hook** lets a component *observe* an image's load state — to show a
spinner, swap in a broken-art fallback, or read the failure reason — and **`PrefetchImage`** warms a decode before
the art scrolls into view. They are protected methods on `Component`
(`src/FluentGpu.Engine/Hooks/Component.cs`, `src/FluentGpu.Engine/Hooks/RenderContext.cs`):

```csharp
protected ImageBinding UseImage(string src, int decodePx,
                                ImagePriority priority = ImagePriority.Visible, string? blurHash = null);
protected void PrefetchImage(string src, int decodePx);
```

`UseImage` subscribes the component to image-status changes, so a `Pending → Ready` transition re-renders **just
this component** (granular re-render, not the whole tree). It returns an `ImageBinding`
(`src/FluentGpu.Engine/Scene/ImageCache.cs`):

```csharp
public readonly record struct ImageBinding(ImageHandle Handle, ImageState State, ImageFailureKind Failure, int Attempts)
{
    public bool IsReady   => State == ImageState.Ready;
    public bool IsLoading => State is ImageState.None or ImageState.Pending;
    public bool IsFailed  => State == ImageState.Failed;
}
```

A spinner-until-ready pattern. Note that `UseImage` tracks status but does **not** paint — you still draw with
`Ui.Image(...)` (or an `ImageEl`):

```csharp
sealed class Cover : Component
{
    public required string Url;
    public override Element Render()
    {
        var img = UseImage(Url, 160);          // observe load state (re-renders this component on change)
        if (img.IsFailed)  return BrokenArtTile();
        if (img.IsLoading) return Spinner();
        return Image(Url, 160f, 160f, 8f);     // resident → paint the real tile
    }
}
```

`ImagePriority` is the decode urgency the worker pool drains in order — under backpressure the lowest off-screen
lane is dropped first, never `Visible` (`src/FluentGpu.Engine/Scene/ImageCache.cs`):

```csharp
public enum ImagePriority : byte { Visible = 0, Overscan = 1, Prefetch = 2 }
```

Use `PrefetchImage` (which requests at `Prefetch` priority) to fill the cache for art the user is about to reach —
the next page of a grid, the next track's cover:

```csharp
PrefetchImage(nextTrackCover, 320);   // warm it now; it's resident before the now-playing view mounts
```

---

## Animation hooks are composited — no re-render, no relayout

This is the load-bearing sentence for motion in FluentGpu. An animation hook **seeds a track on the component's
own node** and the engine advances it every frame on the compositor, re-recording only `LocalTransform` /
`Opacity` (and the geometry channels below). It **never re-renders your component and never relayouts.** A
60 fps animation costs zero `Render()` calls.

That is the opposite of a `setState`-per-tick loop, which re-runs `Render()` + reconcile + a scoped relayout on
every frame and tanks your frame rate. The [pitfalls table](../../guide/pitfalls.md) calls this out directly:
"Dragging a slider tanks FPS → a `setState` per pointer-move re-renders the owning component every frame." The
fix is always the same — drive the value through a bind or an animation hook, not through component state. There is
a [whole section below](#prefer-a-transform-bind-or-a-hook-over-setstate-per-tick) on choosing between them.

The animatable channels are the `AnimChannel` enum (`src/FluentGpu.Engine/Animation/AnimEngine.cs`):

```csharp
public enum AnimChannel : byte
{
    TranslateX, TranslateY, ScaleX, ScaleY, Rotation, Opacity,     // compositor transform/opacity
    SizeW, SizeH,                                                  // presented size (reveal)
    StrokeTrimStart, StrokeTrimEnd,                               // stroke draw-on
    ClipL, ClipT, ClipR, ClipB,                                  // clip-rect reveal (edge sweep)
    LayoutW, LayoutH,                                            // real layout size (the reflow exception)
}
```

The first six write the node's transform/opacity (TransformDirty / PaintDirty — composited). The clip and
stroke-trim channels reveal pixels without moving layout. Only `LayoutW`/`LayoutH` (and `SizeMode.Reflow` below)
deliberately touch real layout — see [geometry reveals](#geometry-reveals) and
[declarative layout transitions](#declarative-layout-transitions).

### Springs and tweens (the named curves)

The four scalar hooks all live on the component and take `deps` that re-seed the track when they change
(`src/FluentGpu.Engine/Hooks/RenderContext.cs`):

```csharp
UseSpring(AnimChannel ch, float to, SpringParams spring, params object[] deps);
UseTransition(AnimChannel ch, float from, float to, float ms, Easing e = Easing.EaseInOut, params object[] deps);
UseKeyframes(AnimChannel ch, Keyframe[] keys, float ms, bool loop = false, params object[] deps);
UseDrivenAnimation(AnimChannel ch, Keyframe[] keys, Func<float> source, float min, float max, params object[] deps);
```

A **tween** is a fixed-duration eased interpolation; a **spring** is a velocity-carrying physical settle that
retargets without snapping (interrupt it mid-flight and it keeps its momentum). From the gallery's hooks demo
(`AnimHooksDemo` in `AnimationPage.cs`):

```csharp
// Mount fade-in (tween): opacity 0→1 over 500ms, the "mount" dep seeds it once.
UseTransition(AnimChannel.Opacity, 0f, 1f, 500f, Easing.EaseOut, "mount");

// Looping pulse (keyframes): scale 1 → 1.25 → 1, repeating.
UseKeyframes(AnimChannel.ScaleX, [new(0f, 1f), new(0.5f, 1.25f), new(1f, 1f)], 1400f, loop: true);
UseKeyframes(AnimChannel.ScaleY, [new(0f, 1f), new(0.5f, 1.25f), new(1f, 1f)], 1400f, loop: true);

// Spring toggle: scale to 1.2 when `on` flips; the spring carries velocity across interrupts.
UseSpring(AnimChannel.ScaleX, on ? 1.2f : 1f, SpringParams.FromResponse(0.3f, 0.5f), on);
```

`SpringParams.FromResponse(responseSec, dampingRatio)` is the friendly iOS/Compose form — `response` ≈ the settle
time in seconds, `dampingRatio` 1 = critical (no overshoot), `< 1` = bouncy (`src/FluentGpu.Engine/Animation/AnimEngine.cs`).

A `Keyframe` is `(offset 0..1, value, optional easing)`; the named curves are the `Easing` enum
(`src/FluentGpu.Engine/Foundation/Easing.cs`):

```csharp
public enum Easing : byte
{
    Linear, EaseIn, EaseOut, EaseInOut, Sine, Quad, Cubic, Expo, Back, Elastic, Bounce,
    FluentStandard,   // move / reposition  — cubic-bezier(0.8, 0.0, 0.2, 1.0)
    FluentDecelerate, // entrance / show    — cubic-bezier(0.1, 0.9, 0.2, 1.0)
    FluentAccelerate, // exit / hide        — cubic-bezier(0.9, 0.1, 1.0, 0.2)
    FluentPopOpen,    // flyout / menu open — cubic-bezier(0, 0, 0, 1)
}
```

The four `Fluent*` curves are the real WinUI motion vocabulary; prefer them for Fluent-feeling motion. Any cubic
bezier is also expressible with `EasingSpec.CubicBezier(x1, y1, x2, y2)` — that is how a WinUI storyboard KeySpline
ports over verbatim (a `Keyframe` ctor accepts an `EasingSpec`):

```csharp
new Keyframe(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))
```

`UseDrivenAnimation` is the odd one out: instead of wall-clock time, the track's progress is driven by **any value
source** — a scroll offset, a playback position, a slider — so a header can fade by scroll position rather than by
a timer. (The gallery shows the same idea through the lower-level `AnimEngine.Drive`; see
[authored timelines](#authored-timelines-with-animengine).)

### Motion sugar (UseEntrance, UseHoverScale; the UseAnimatedValue caveat)

Two reusable Fluent patterns are extension methods on `Component`
(`src/FluentGpu.Engine/Hooks/MotionHooks.cs`) — call them inside the component whose rendered root you want to animate:

```csharp
this.UseEntrance(float offsetPx = 24, object? key = null);   // Fluent show: TranslateY 24→0 + Opacity 0→1, FluentDecelerate
this.UseHoverScale(bool hovered, float to = 1.02f);          // a card/button lift (spring) on pointer-over
```

`UseEntrance` reproduces the WinUI implicit show transition; `UseHoverScale` springs a subtle scale on hover. Both
honor reduced motion (`Motion.ReducedMotion`, set by the host from the OS animation setting) — `UseEntrance`
no-ops the entrance when the user has reduced motion enabled.

```csharp
sealed class FadeInCard : Component
{
    public override Element Render()
    {
        this.UseEntrance();   // the card slides up + fades in on mount
        return Card(Text("Hello"));
    }
}
```

**`UseAnimatedValue` for a lerped scalar.** There is also a scalar hook that returns an eased value you can lerp
anything with (`src/FluentGpu.Engine/Hooks/RenderContext.cs`):

```csharp
public float UseAnimatedValue(float target, float durationMs = 180f, Easing easing = Easing.EaseInOut);
```

The gallery uses it to drive a **color** lerp (something the transform channels can't express):

```csharp
float t = UseAnimatedValue(on ? 1f : 0f, 300f);
var fill = ColorF.Lerp(ColorF.FromRgba(80, 80, 88), Tok.AccentDefault, t);   // animate a fill off the slab value
```

Under the landed slab engine `UseAnimatedValue` is just another `AnimValue` channel — it advances on the compositor
frame like `UseSpring`/`UseTransition`/`UseKeyframes`, so it is fine for continuous motion (a color pulse, a spinner
tint) as well as a one-shot transition tied to a state flip. `UseSpringValue` is its spring-backed sibling. The
reference docs cover the same hooks in the [hooks section](../../guide/components-elements-layout.md#animation-composited--no-re-render-rides-the-compositor-frame).

---

## Authored timelines with AnimEngine

The hooks above seed tracks on *your component's* node. When you need to animate a node you captured by handle —
or play several channels of one element, or compose layers — drop to the engine directly via `Context.Anim`
(the `AnimEngine`, `src/FluentGpu.Engine/Animation/AnimEngine.cs`). The pattern: capture a `NodeHandle` with
`OnRealized`, then seed in a `UseLayoutEffect` (which runs after the first layout, when the node is live):

```csharp
var dot = UseRef<NodeHandle>(default);
// …in the element tree:  new BoxEl { OnRealized = h => dot.Value = h }
UseLayoutEffect(() =>
{
    var anim = Context.Anim;                                       // the engine
    if (anim is null || dot.Value.IsNull) return;
    anim.Animate(dot.Value, AnimChannel.TranslateX, 0f, 186f, 1100f, Easing.Bounce);   // eased two-point tween
}, replay);                                                        // deps re-seed on change
```

The engine's authoring methods (all take a `NodeHandle`):

```csharp
void Animate(NodeHandle node, AnimChannel ch, float from, float to, float durationMs,
             Easing easing = Easing.EaseInOut, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f);
void Keyframes(NodeHandle node, AnimChannel ch, Keyframe[] keys, float durationMs,
               bool loop = false, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f);
void Spring(NodeHandle node, AnimChannel ch, float to, in SpringParams spring,
            float? initial = null, CompositeOp composite = CompositeOp.Replace, float delayMs = 0f);
void Drive(NodeHandle node, AnimChannel ch, Keyframe[] keys, int drivenRef, float domainMin, float domainMax,
           CompositeOp composite = CompositeOp.Replace);
```

**Channels compose.** Transform channels on one node fold into a single `LocalTransform`
(translate ∘ rotate ∘ scale), so you can play `Rotation`, `TranslateX`, and `ScaleX` on the same tile at once
(`AnimChannelsPlayground` in `AnimationPage.cs`):

```csharp
anim.Keyframes(tile, AnimChannel.Rotation,
    [new(0f, 0f), new(0.5f, 360f, Easing.FluentStandard), new(1f, 0f, Easing.FluentPopOpen)], 900f);
```

**Stagger is an engine primitive**, not a timer — `delayMs` offsets the seed. The gallery replays one path across
three dots at 0/110/220 ms, and loops a fourth:

```csharp
Keyframe[] path = [new(0f, 0f), new(0.45f, 150f, Easing.Back),
                   new(0.7f, 70f, Easing.EaseOut), new(1f, 150f, Easing.Bounce)];
anim.Keyframes(dotA, AnimChannel.TranslateX, path, 1400f);
anim.Keyframes(dotB, AnimChannel.TranslateX, path, 1400f, delayMs: 110f);
anim.Keyframes(dotC, AnimChannel.TranslateX, path, 1400f, delayMs: 220f);
anim.Keyframes(pulse, AnimChannel.ScaleX, [new(0f, 1f), new(0.5f, 1.3f), new(1f, 1f)], 1200f, loop: true);
```

**A driven timeline** scrubs by any value source. Register a `Func<float>` clock and several channels ride it
(`AnimDrivenScrub`):

```csharp
int src = anim.Clocks.Register(() => t.Value);                // any Func<float> (here a slider signal)
anim.Drive(dot, AnimChannel.TranslateX, [new(0f, 0f), new(1f, 220f, Easing.Linear)], src, 0f, 1f);
anim.Drive(dot, AnimChannel.Rotation,   [new(0f, 0f), new(1f, 360f, Easing.Linear)], src, 0f, 1f);
```

**Additive composition** layers tracks on the *same* channel, exactly like CSS `animation-composition: add` — a
slow `Replace` drift plus a fast additive wobble (`AnimAdditive`):

```csharp
anim.Keyframes(dot, AnimChannel.TranslateX,
    [new(0f, 0f), new(0.5f, 170f, Easing.EaseInOut), new(1f, 0f, Easing.EaseInOut)], 4000f, loop: true);
anim.Keyframes(dot, AnimChannel.TranslateX,
    [new(0f, -6f), new(0.5f, 6f, Easing.Sine), new(1f, -6f, Easing.Sine)], 260f, loop: true,
    composite: CompositeOp.Add);                              // layers, does not replace
```

`CompositeOp` is `{ Replace, Add, Accumulate }`. At most one `Replace` track is live per (node, channel) —
seeding another `Replace` on the same channel retargets the existing one rather than stacking two integrators.

---

## Geometry reveals

Three channels reveal pixels **without moving layout** — the surface stays exactly where it landed; only the drawn
extent changes. These are the compositor-only ways to "draw something on."

**Clip reveal** (`ClipL/T/R/B`) sweeps one edge of a rounded surface while its corners stay round — the WinUI
composition-clip look. A settled clip clears its override automatically (`AnimClipReveal` in `AnimationPage.cs`):

```csharp
anim.Animate(card, AnimChannel.ClipR, 0f, width, 450f, Easing.FluentPopOpen);    // reveal left → right
anim.Animate(card, AnimChannel.ClipT, height, 0f, 450f, Easing.FluentPopOpen);   // reveal bottom → top
```

**Stroke-trim draw-on** (`StrokeTrimStart`/`StrokeTrimEnd`) reveals an analytic polyline along its own length —
this is exactly how the CheckBox checkmark draws itself. Seed it with the hook form on a `PolylineStrokeEl`
(`AnimDrawOnStroke`):

```csharp
Context.UseKeyframes(AnimChannel.StrokeTrimEnd,
    [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))],
    800f, false, seed);
return new PolylineStrokeEl { /* P0..P3, PointCount = 4, Thickness = 3f, … */ };
```

**Presented-size reveal** (`SizeMode.Reveal`) lands layout at the final size *immediately* — siblings snap into
place — and eases only the drawn extent. Crisp and compositor-only. This is a declarative
[`LayoutTransition`](#declarative-layout-transitions), set via `BoxEl.Animate` (`AnimPresentedReveal`):

```csharp
new BoxEl
{
    Width = 220f, Height = open ? 120f : 48f,
    Animate = LayoutTransition.BoundsT(SizeMode.Reveal),   // layout already landed; only pixels ease
}
```

Compare that with smooth reflow (`SizeMode.Reflow`) just below, where the layout *itself* animates and neighbours
glide.

---

## Declarative layout transitions

You can hand the whole problem to the engine: set `Animate = LayoutTransition` on a `BoxEl`, change the
declaration (a width, a justify, a child count), and the engine **diffs the old laid-out rect against the new one
and animates the difference** — no imperative seeding, no per-control knowledge. This is the `LayoutTransition`
surface (`src/FluentGpu.Engine/Foundation/LayoutTransition.cs`):

```csharp
public readonly record struct LayoutTransition(
    TransitionChannels Channels,
    TransitionDynamics Dynamics = default,         // spring (default) or Tween(ms, easing)
    SizeMode Size = SizeMode.Auto,
    EnterExit Enter = default, EnterExit Exit = default,
    ushort CustomCurveId = 0,
    TransitionDynamics? ExitDynamics = null,        // asymmetric open/close timings
    float DelayMs = 0f,                             // stagger
    SizeAnchor Anchor = SizeAnchor.Leading);
```

`TransitionChannels` is `{ Position, Size, Opacity, Bounds }`; `TransitionDynamics` is either a spring
(`.Spring(response, dampingRatio)`, the default) or a tween (`.Tween(durationMs, easing)`). There are ready-made
shortcuts: `LayoutTransition.Slide` (position only), `.Fade` (opacity only), `.BoundsT(size)` (position + size),
and `.AutoAll`.

### Position FLIP — slide between layout slots

The classic FLIP: the element re-lays-out instantly into its new slot, then the engine plays the residual back so
it *appears* to slide. Projection is parent-relative — only the node's own slot change animates, an ancestor
reflow never re-triggers it (`AnimFlipSlide` in `AnimationPage.cs`):

```csharp
new BoxEl { Animate = LayoutTransition.Slide }                         // spring dynamics (default)
new BoxEl { Animate = new LayoutTransition(TransitionChannels.Position,
                          TransitionDynamics.Tween(420f, Easing.FluentStandard)) }   // eased instead
```

### Size modes — Reflow, ScaleCorrect, Relayout

`SizeMode` chooses how a size change animates (`src/FluentGpu.Engine/Foundation/LayoutTransition.cs`):

```csharp
public enum SizeMode : byte
{
    Reveal,        // lay out at final size immediately; ease a clip window. Crisp, compositor-only (default).
    ScaleCorrect,  // GPU scale toward 1 (Framer-Motion projection). Chrome only — distorts text/borders mid-flight.
    Relayout,      // re-solve the subtree each tick so text re-wraps LIVE. Correct, costs scoped layout.
    Reflow,        // the size change runs through REAL layout each tick; neighbours/siblings reflow smoothly.
    Auto,
}
```

- **`Reveal`** — the [presented-size reveal](#geometry-reveals) above. Layout snaps; pixels ease.
- **`ScaleCorrect`** — a size change becomes a GPU scale that springs toward 1. Cheap, but it distorts text and
  borders mid-flight, so chrome only.
- **`Relayout`** — re-solves the subtree at the interpolated size every tick, so text re-wraps live as the width
  animates. Correct, costs a scoped layout per frame.
- **`Reflow`** — the deliberate "smoother than WinUI" mode: the interpolated size participates in **real parent
  layout** each tick (boundary-scoped), so everything below it reflows smoothly and rigidly instead of snapping.
  This is what the gallery's collapsible section cards (and the `Expander` control) run on:

```csharp
static readonly LayoutTransition Reflow = new(TransitionChannels.Size,
    TransitionDynamics.Tween(333f, Easing.FluentPopOpen),                                  // ExpandDown timing
    Size: SizeMode.Reflow,
    ExitDynamics: TransitionDynamics.Tween(167f, EasingSpec.CubicBezier(1f, 1f, 0f, 1f)),  // CollapseUp (faster)
    Anchor: SizeAnchor.Trailing);                                                          // slide out from under the header

new BoxEl { ClipToBounds = true, Height = open ? float.NaN : 0f, Animate = Reflow, Children = [panel] }
```

`ExitDynamics` gives an asymmetric open/close timing (WinUI's Expander is 333 ms open / 167 ms collapse);
`SizeAnchor.Trailing` keeps the content's bottom edge on the reveal edge so it slides out from under the header.

### Enter / Exit terminals with stagger

`EnterExit` is the presented-space terminal an inserted node animates *from* (enter) or a removed node animates
*to* (exit) — an offset, a scale, an opacity, plus `Active = true` to engage it. An inserted node plays in from its
enter terminal; a removed node becomes a brief **exit orphan** that stays alive until its tracks settle (this is
expected — the [pitfalls table](../../guide/pitfalls.md) notes "a removed node lingers briefly … it's reclaimed on
settle, not a leak"). `DelayMs` staggers a batch (`AnimEnterExit` in `AnimationPage.cs`):

```csharp
new BoxEl { Animate = new LayoutTransition(
    TransitionChannels.Position | TransitionChannels.Opacity,
    TransitionDynamics.Tween(220f, Easing.FluentDecelerate),
    Enter: new EnterExit(Dy: 18f, Opacity: 0f, Active: true),       // enter from 18px down, transparent
    Exit:  new EnterExit(Sx: 0.8f, Sy: 0.8f, Opacity: 0f, Active: true),
    DelayMs: index * 40f) }                                          // per-item stagger
```

---

## Prefer a transform bind or a hook over setState-per-tick

The three update paths from the [reactivity model](../../guide/reactivity.md), cheapest first:

1. **Bind a prop to a `Func`/signal** (`Transform`, `Opacity`, `Fill`, `Text`) — compositor-only: no render, no
   reconcile, no layout. The cheapest possible update.
2. **Granular re-render** (`UseState`/`UseSignal` read in `Render()`) — the owning component's subtree re-renders +
   a scoped relayout.
3. **Reactive control flow** (`Flow.For`/`Flow.Show`) — a structural reconcile of one boundary.

For **per-frame motion**, paths 1 and the animation hooks both stay on the compositor; a `setState` per tick is
path 2 every frame and is the classic frame-rate killer. The rules of thumb:

- **A high-frequency scalar from a drag/scroll/timer** → bind it. A slider scrubber is the canonical case: use
  `Slider.Bind(FloatSignal)` (compositor bypass), not `Slider.Create` (re-renders on each move). The
  [pitfalls table](../../guide/pitfalls.md) lists this as the slider-tank fix — and the proof is
  `FrameStats.Rendered == false` while you drag.
- **A self-running transition or spring on this component** → `UseSpring` / `UseTransition` / `UseKeyframes`. They
  seed on the host node and advance without a re-render.
- **A node you captured by handle, or layered/staggered timelines** → `Context.Anim` directly
  ([above](#authored-timelines-with-animengine)).
- **A declarative layout change** (open/close, reorder, resize) → `Animate = LayoutTransition`
  ([above](#declarative-layout-transitions)).
- **`setState`-per-tick** → only when the thing you animate genuinely needs a re-render (e.g. a color via
  `UseAnimatedValue`, or text content) **and** it is low-frequency. Never for continuous motion.

One channel, one owner: if an animation or a bound `Transform`/`Opacity` owns a channel, the reconciler won't also
write a static value to it (it would fight the animator). The [pitfalls table](../../guide/pitfalls.md) calls a
"snaps back each frame" symptom exactly this — pick one owner per channel.

---

## Engine internals

You do not need any of this to build apps — but when you want to know how the pixels actually move:

- **[Media pipeline subsystem](../../../design/subsystems/media-pipeline.md)** — the WIC decode workers, the
  bounded request channel with priority-drop backpressure, the LRU residency manager (pin-before-admit,
  frame-start eviction), the GPU texture pool and `CopyBufferToTexture` upload, the small-image atlas, and the
  placeholder→cross-fade record path.
- **[Backdrop, effects & animation subsystem](../../../design/subsystems/backdrop-effects-animation.md)** — the
  phase-7 animation engine: the unified POD `AnimValue` slab (value+velocity+target+generator, interpolate-from-current
  + auto-retarget on signal change), the analytical closed-form spring (sampled at absolute `t`, dt-deterministic), the
  driven clock, the `DetachedAnimSlab` that keeps an exit animation alive past unmount, connected/shared-element
  transitions, and the three kinds of backdrop.
- **[SPEC-INDEX](../../../design/SPEC-INDEX.md)** is the precedence authority for every cross-cutting contract;
  **[subsystems/README](../../../design/subsystems/README.md)** is the ownership map.

Related app-author pages: [signals, components & bindings](./signals-components-and-bindings.md),
[layout & composition](./layout-and-composition.md),
[controls cookbook](./controls-cookbook.md),
[virtualized lists & ItemsView](./virtualized-lists-and-itemsview.md), and
[rendering & performance](../../guide/rendering-and-performance.md).
