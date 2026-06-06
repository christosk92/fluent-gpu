# FluentGpu Subsystem — Input, Focus, IME, Hit-Testing & Accessibility (UIA)

*Owner doc for the **Input** subsystem (portable, COM-free) plus its OS leaves (`Pal.Windows` IME/clipboard/drag, `Accessibility.Uia`). Promotes architecture-spec §5.8 into a full design and folds the §5.8 corrections + the COM-generation ruling (dotnet10 §4 / hardened-v1 §4.2). Cross-cutting contracts (threading, memory, SceneStore columns, DrawList encoding, PAL/RHI seams, hooks/reconcile, color) are OWNED elsewhere and only **referenced** here — see foundations.md, architecture-spec.md §4, hardened-v1-plan.md §2/§4, dotnet10-csharp14-zero-alloc.md §4, and subsystems/{reconciler-hooks,gpu-renderer,text}.md.*

---

## 0. Posture & one-line thesis

Input is a **pure consumer of the committed previous-frame Scene**: it reads `SceneStore` topology + `Bounds` (LOCAL) + `LocalTransform` + `NodeFlags` + `InteractionInfo`/`FocusNav`/`A11yInfo` columns, and it writes **nothing to the Scene** — its only mutations are (a) appending to `RerenderToken`/the setState coalescer (via user delegates), (b) `FocusEngine` state (its own slabs), and (c) one transient overlay layer for the focus rect / KeyTips / drop feedback. It runs entirely on the **UI thread, phases 1–2**, against the **last-published-consistent** Scene (never the in-flight reconcile, never the render-thread snapshot). Accessibility (UIA) is a *projection* of the same retained tree — no peer-control tree, no shadow DOM — reached through generated CCWs, gated to do **zero work** when no AT is attached.

The single hardest correctness facts this doc commits to:
1. **`Action<TRefStruct>` is illegal C#.** All hot event args are `ref struct`; all handlers are **named `delegate` types taking the args by `ref`**, stored in typed `delegate[]` columns.
2. **One shared forward/inverse transform-accumulation helper** is the sole authority for composing ancestor transform + clip. Hit-test, UIA `get_BoundingRectangle`, and IME caret all call it. There is exactly one matrix-walk in the subsystem.
3. **UIA is `UseComThreading`-strict.** Every provider call (read AND write) marshals to the UI thread via the HWND pump — there is no "read on the calling thread" fast path. SoA columns are never touched off-thread.
4. **`GetRuntimeId` is derived from stable *logical* identity**, never slot+gen (slot+gen is ABA-prone across reconcile and would break AT element-tracking).

---

## 1. Assemblies, deps, threads

| Concern | Assembly | Deps | Thread | COM? |
|---|---|---|---|---|
| Hit-test, dispatch, gestures, focus, commands, accelerators, event args, input hooks | **`FluentGpu.Input`** (portable) | `Scene`, `Layout`(iface), `Pal`(iface), `Animation`(iface), `Foundation` | UI thread, phases 1–2 + 7 (inertia) | **none** |
| Input hooks surface (`UseFocus`/`UseElementRef`/`UseCommand`/`UseAnnounce`/`UseGesture`/`UseAccelerator`) | **`FluentGpu.Hooks`** (the hook *shape*) impl wires through `Input` seams | `Dsl`, `Foundation` | UI thread, phase 4 (declared) | none |
| IME (Imm32 v1; TSF v2) | **`Pal.Windows`** behind `Pal`.`IImeSession` | `Pal`, `Foundation` | UI thread (window pump) | flat C / `[LibraryImport]` (Imm32 is not COM); TSF v2 = `[GeneratedComInterface]` |
| Clipboard + drag/drop | **`Pal.Windows`** behind `Pal`.`IClipboard`/`IDragDropBackend` | `Pal`, `Foundation` | UI thread (OLE modal loop) | OLE via `[GeneratedComInterface]`/`[GeneratedComClass]` |
| UIA provider tree | **`Accessibility.Uia`** behind `IA11yBackend` | `Scene`(read-only ref), `Pal`(iface), `Foundation` | UI thread (all calls marshaled) | UIA via `[GeneratedComInterface]`/`[GeneratedComClass]` |
| macOS a11y / IME | **`Accessibility.NSAccessibility`**, **`Pal.Cocoa`** | same seams | main thread | Objective-C runtime, no COM |

All OS leaves are referenced **only by `Hosting`** (foundations §7 cycle invariant 2). `FluentGpu.Input` itself carries `[assembly: DisableRuntimeMarshalling]` (it is 100% blittable, no source-gen COM). The COM leaves (`Accessibility.Uia`, the OLE/TSF parts of `Pal.Windows`) live in / mirror the **`PlatformIntegration`** policy (dotnet10 §3): **no** `DisableRuntimeMarshalling` (source-gen COM uses `in`/`out`), and each generated COM interface hierarchy is kept inside one assembly (cross-assembly `[GeneratedComInterface]` inheritance has a vtable-offset rebuild-coupling pitfall).

**Why this is `safe-by-construction` for confinement:** Input never owns a `ComPtr`; the render thread owns every ComPtr (hardened-v1 §2.1). The only COM Input *causes* (UIA/TSF/OLE callbacks) is marshaled back to the UI thread before it touches any Scene column, so the single-writer rule on `SceneStore` is preserved.

---

## 2. Where each piece lands in the 13-phase loop (and on which thread)

Reference: architecture-spec §4.8 + hardened-v1 §2.2. Input touches **phase 1 (pump), phase 2 (dispatch), phase 4 (hook declaration), phase 6.5 (focus-into-view layout effect)**, and **phase 7 (inertia integrator tick)**. It is *upstream* of PUBLISH(13a) and the render thread.

```
 1 pump            [UI]  Pal.Windows WndProc → InputEvent/WindowEvent POD → InputEventRing (move-coalesced)
 2 input-dispatch  [UI]  InputDispatcher.Drain(ReadOnlySpan<InputEvent>) against the COMMITTED prev-frame Scene:
                          a. hit-test (reverse-z, inverse transform, shape-accurate self-test)
                          b. HandlerMask pre-filter
                          c. capture FSM (pointer/manipulation), gesture recognition
                          d. accelerator match → access keys → routed tunnel/bubble → built-ins (Tab/arrow/Esc)
                          e. focus moves; UIA mutations POSTED here (marshaled, applied on UI thread)
                          handler setState → coalescer (Interlocked CAS), queued for THIS frame's render (guard FALSE)
 3 hook-flush      [UI]  (Reconciler owns) — setState applied
 4 render          [UI]  Component.Render() declares UseFocus/UseCommand/UseAnnounce/UseGesture/UseAccelerator
 5 reconcile       [UI]  (Reconciler owns) — writes A11yInfo/FocusNav/InteractionInfo columns; live-region capture
 6 layout          [UI]  (Layout owns) — writes Bounds[] (LOCAL)
 6.5 layout-effects[UI]  UseFocus(autoFocus)/scroll-into-view read VALID Bounds[]; focus rect overlay marked dirty
 7 animation       [UI]  inertia integrator ticks fling (Input-owned), writes scroll viewport LocalTransform
PUBLISH (13a)      [UI]  SceneFrame snapshot sealed — Input is done for the frame
 8-11              [RENDER] record/batch/submit/present — Input contributes ONLY the DrawFocusRectCmd opcode it
                          authored in the overlay layer (recorded like any other node by the render thread)
12 passive-effects [UI]  UseAnnounce fires UiaRaiseNotificationEvent for any seq ≤ last-presented
```

**Phase-2-against-the-committed-previous-frame contract (hardened-v1 §2.2):** dispatch hit-tests the **UI-owned `Bounds` double-buffer** — the topology+Bounds as of the last completed reconcile/layout — *never* the in-flight `SceneFrame` snapshot the render thread is consuming, and never a half-built reconcile. This is why Input is phases 1–2: the Scene it reads is stable and single-writer-consistent. A `setState` raised by a handler is queued (the `_isRendering` guard is FALSE in dispatch, so it marks dirty and lands in *this* frame's render phase 4).

---

## 3. Phase 1 — pump → `InputEventRing` (Pal.Windows owns the encode; Input owns the schema)

The window writes POD into a host-owned slab-backed ring (architecture-spec §4.7 amendment: **C# events are replaced by a POD ring**; no per-event delegate/closure alloc). `IPlatformWindow.PumpInto(ref InputEventRing ring)` drains `WM_*`/`NSEvent` into it once per frame.

```csharp
namespace FluentGpu.Input;   // schema is portable; Pal.Windows fills it

public enum InputKind : byte {
    PointerDown, PointerUp, PointerMove, PointerWheel, PointerCaptureLost, PointerLeave, PointerHover,
    KeyDown, KeyUp, Char,                 // Char = committed text (WM_CHAR after TranslateMessage)
    ImeStartComposition, ImeUpdateComposition, ImeEndComposition,
    FocusActivate, FocusDeactivate,       // window-level (WM_ACTIVATE)
    DragEnter, DragOver, DragLeave, Drop, // OLE drop-target, on UI thread modal loop
}

[StructLayout(LayoutKind.Sequential)]    // 40B, blittable, zero managed refs
public readonly struct InputEvent {
    public readonly InputKind  Kind;
    public readonly PointerKind Device;   // Mouse | Touch | Pen
    public readonly ModifierKeys Mods;    // Ctrl/Shift/Alt/Win + LeftBtn/MiddleBtn/RightBtn snapshot
    public readonly ushort      _pad;
    public readonly uint        PointerId;// stable per active contact; mouse = 0
    public readonly Vec2        PosDip;   // CLIENT-space DIP, DIP-converted ONCE at the pump boundary
    public readonly float       WheelDelta;
    public readonly uint        KeyOrChar;// VK_* for Key*, UTF-32 codepoint for Char
    public readonly long        TimestampUs;
    public readonly float       Pressure; // pen/touch; mouse = 1
}
```

**Move-coalescing (Win32 map fix, folded):** the >1 kHz `WM_POINTERUPDATE`/`WM_INPUT`/`WM_MOUSEMOVE` flood is collapsed to the **latest** `PointerMove` per `PointerId` at ring-write time (the ring keeps a per-pointer "last-move index" and overwrites in place), so dispatch sees at most one move per pointer per frame. Wheel deltas **accumulate** (sum, not last). Down/Up/Char are never coalesced (ordering-significant).

**Win32 map fixes (folded):**
- Leave tracking via `WM_POINTERLEAVE` + `TrackMouseEvent(TME_LEAVE|TME_HOVER)` — **not** `RegisterTouchHitTestingWindow`.
- Committed text is `WM_CHAR` after `TranslateMessage` (`WM_UNICHAR` optional for >BMP keyboards).
- `WM_POINTERDOWN/UP/UPDATE` → `GetPointerInfo` → DIP-convert once with the window's current effective DPI → ring.

**DPI (foundations / architecture-spec §7):** DIP↔px conversion happens **once** at the pump boundary using the window's post-`WM_DPICHANGED` `Scale`. Everything above the seam is in DIP. The shared transform helper (§5) never re-applies DPI — it composes node-local DIP transforms only.

---

## 4. Phase 2 — `InputDispatcher.Drain` (the orchestrator)

```csharp
public sealed class InputDispatcher          // UI thread only; asserts via ThreadGuard.AssertWriter in DEBUG
{
    readonly SceneView _scene;               // read-only view over committed prev-frame columns
    readonly HitTester _hit;
    readonly FocusEngine _focus;
    readonly GestureRecognizer _gestures;
    readonly AcceleratorRegistry _accels;
    readonly CommandRouter _commands;
    PointerCaptureState _capture;            // node that owns each active pointer, if any
    HoverState _hover;                       // current hover chain (for enter/leave diff)

    public void Drain(ReadOnlySpan<InputEvent> events)
    {
        for (int i = 0; i < events.Length; i++)
        {
            ref readonly var e = ref events[i];
            switch (e.Kind)
            {
                case InputKind.PointerMove:
                case InputKind.PointerDown:
                case InputKind.PointerUp:
                case InputKind.PointerWheel:   DispatchPointer(in e); break;
                case InputKind.KeyDown:         DispatchKeyDown(in e); break;
                case InputKind.KeyUp:           DispatchKeyUp(in e);   break;
                case InputKind.Char:            DispatchChar(in e);    break;
                case InputKind.DragEnter:
                case InputKind.DragOver:
                case InputKind.DragLeave:
                case InputKind.Drop:            DispatchDrag(in e);    break;
                case InputKind.FocusActivate:
                case InputKind.FocusDeactivate: _focus.OnWindowActivation(e.Kind); break;
                // Ime* events flow through IImeSession, not here (§9)
            }
        }
        _gestures.OnFrameEnd(_scene.Now);     // fire long-press timers, double-tap windows
    }
}
```

Zero managed allocation in `Drain` itself (phases 1–2 alloc budget = "freshly-captured user closures at the edge only"; architecture-spec §8). Every internal collection is a slab/arena/`stackalloc`.

---

## 5. Hit-testing (Input owns; the ONE shared transform helper)

### 5.1 The algorithm (reverse-z descent, inverse transform, shape-accurate self-test)

```
HitTest(ptDip, out HitResult result, Span<NodeHandle> routeBuf) -> int routeLen
  push root; descend; at each node n with point ptParent (DIP, in n's PARENT space):
    if !Flags[n].Visible            -> skip subtree
    if Flags[n].PointerTransparent  -> skip n's own self-test but still descend children
    ptLocal = InverseAccumulate(n, ptParent)          // see §5.3 — the shared helper
    if ClipsToBounds and !ClipContains(n, ptLocal)    -> skip subtree (clip culls children too)
    // reverse-z: LAST child paints on top, so test children back-to-front first (topmost wins)
    for c = LastChild[n]; c != -1; c = PrevSibling[c]:
        if recurse(c, ptLocal) hit -> return hit (deepest topmost node wins)
    // no child hit: self-test THIS node
    if Flags[n].HitTestVisible and SelfTest(n, ptLocal):
        result = Hit(n); capture route into routeBuf; return hit
  return miss
```

- **`ReverseChildren` via `LastChild → PrevSibling`** — O(children), zero alloc, uses the doubly-linked Topology column (foundations §4.4 has `LastChild`/`PrevSibling`).
- **Route captured into a `stackalloc Span<NodeHandle>`** during the descent: the descent is recursive but bounded by tree depth; the route (root→hit) is written into a caller-supplied `stackalloc NodeHandle[MaxDepth]` (typical UI depth ≤ 64; overflow falls back to an arena slice, never the heap). The route is the spine for tunnel/bubble (§6).

### 5.2 Shape-accurate self-test (shares the *fill rule*, not just vertices)

`InteractionInfo.HitShape` selects the test; all tests are against **node-LOCAL** coordinates (P8 fix — `Bounds` is local):

```csharp
public enum HitShape : byte { Rect, RoundRect, Ellipse, Capsule, Path }

static bool SelfTest(in NodeView n, Vec2 pLocal) => n.HitShape switch {
    HitShape.Rect      => RectContains(n.HitBounds, pLocal),
    HitShape.RoundRect => SdRoundRect(pLocal, n.HitBounds, n.HitCorners) <= 0f,   // SAME SDF as paint (§gpu-renderer §4)
    HitShape.Ellipse   => SdEllipse(pLocal, n.HitBounds) <= 0f,
    HitShape.Capsule   => SdCapsule(pLocal, n.HitBounds) <= 0f,
    HitShape.Path      => PathContains(n.HitGeometry, pLocal, n.FillRule),        // fill RULE, not bbox
};
```

- **Path hit-test uses the paint's fill rule (nonzero winding default)** — folds the input-a11y fix in gpu-renderer §5 ("Hit-test shares the fill RULE, not just the vertices"). `PathContains` runs the crossing-number / winding-number test that matches the trapezoidal fill the renderer produced. `evenodd` paths test parity; `nonzero` test signed-winding != 0.
- **bbox + coarse-grid acceleration for hot complex paths:** `InteractionInfo.HitGeometry` references a `SlabAllocator<HitGeom>` entry carrying an AABB (early-reject) and, for large paths, a coarse tile grid of edge-spans so the per-fragment crossing test is O(edges-in-tile), not O(total edges). The grid is built lazily on first hit-test of a path node and invalidated when its `BAKED-GEOMETRY hash` changes (same hash the CleanSpanWitness uses — architecture-spec §5.4).
- The SDF functions are **single-sourced** with the renderer's `shapes_common.hlsli` math (gpu-renderer §line 583) re-expressed in C# (`sdRoundRect` etc.), so hit and paint never disagree about an edge.

### 5.3 The ONE shared forward/inverse transform-accumulation helper

This is the load-bearing P8 contract: hit-test, UIA `get_BoundingRectangle`, and IME caret **all** call the same helper. There is exactly one place that composes ancestor `LocalTransform` + clip.

```csharp
namespace FluentGpu.Input;   // portable; depends only on Scene columns + Foundation math

public static class TransformChain
{
    /// Map a point from node n's PARENT space into n's LOCAL space (one step).
    /// pLocal = Inverse(LocalTransform[n]) * pParent. Bounds is LOCAL so no offset bake here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 InverseStep(in Affine2D local, Vec2 pParent)
        => Affine2D.InverseTransformPoint(in local, pParent);

    /// Compose the FORWARD local->root (== client DIP) matrix by walking Parent[] to the root.
    /// Used by UIA bounding-rect and IME caret to project a LOCAL rect to client DIP, then to screen.
    public static Affine2D AccumulateToRoot(SceneView s, NodeHandle n)
    {
        Affine2D m = Affine2D.Identity;
        for (int i = n.Index; i != -1; i = s.Parent(i))
            m = Affine2D.Multiply(s.LocalTransform(i), m);   // child-local first, then ancestor
        return m;
    }

    /// Project a LOCAL rect to client-DIP, honoring the full ancestor transform chain.
    public static RectDip LocalRectToClient(SceneView s, NodeHandle n, RectDip localRect)
        => Affine2D.TransformRect(AccumulateToRoot(s, n), localRect);
}
```

- Hit-test descends *down*, applying `InverseStep` per level (cheaper than building the full inverse once — descent already visits every ancestor).
- UIA `get_BoundingRectangle` and IME caret go *up* via `AccumulateToRoot` once for the single node of interest, then the host adds the client→screen offset (`ClientToScreen`, in `Pal.Windows`, the one place pixels meet the OS).
- **`get_BoundingRectangle` uses this chain, not just a client→screen offset** (folds the §5.8 fix: a transformed/scrolled element's a11y rect must reflect ancestor transforms).

### 5.4 Hover / capture / enter-leave

- **Pointer capture:** a node that handled `PointerPressed` may call `e.Capture()`; subsequent moves/up for that `PointerId` route only to the capturing node + its ancestors (the route is rebuilt from the captured node up). `WM_POINTERCAPTURECHANGED`/`PointerCaptureLost` clears it.
- **Enter/leave diff:** keep the previous hover route; the new hover route from this frame's hit-test is diffed against it (common-ancestor split): nodes leaving get `PointerExited` (bubble), nodes entering get `PointerEntered` (tunnel). Done over the **same `stackalloc` route span** — no per-event allocation.
- **Hover cache invalidation (folds the stuck-hover fix):** the hover route is invalidated on **any** topology / z-order / overlay-push change — not just layout commits. Concretely: the dispatcher subscribes to a `_sceneEpoch` bumped by the reconciler on topology/reorder and by the overlay manager on focus-rect/KeyTips/drop-feedback push; on epoch change the cached hover route is dropped and recomputed on the next move (or synthesized immediately if the pointer is stationary, so hover follows content that moved under a still cursor).

---

## 6. Dispatch — ref-struct event args + named by-ref delegates + HandlerMask

### 6.1 Event args are `ref struct` (zero box)

```csharp
public ref struct PointerEventArgs {
    public readonly NodeHandle Source;       // the node currently being offered the event
    public readonly NodeHandle Original;     // the hit node (route leaf)
    public readonly Vec2 PositionDip;        // in Source's LOCAL space (re-projected per route hop)
    public readonly PointerKind Device;
    public readonly ModifierKeys Mods;
    public readonly uint PointerId;
    public readonly float WheelDelta;
    public bool Handled;                     // settable — stops further routing if true
    public void Capture();                   // request pointer capture for Source
    public void ReleaseCapture();
}

public ref struct KeyEventArgs {
    public readonly NodeHandle Source, Original;
    public readonly uint Vk;                 // VK_* / portable key code
    public readonly ModifierKeys Mods;
    public readonly bool IsRepeat;
    public bool Handled;
}
```

### 6.2 Handlers are NAMED delegates taking args BY REF (`Action<TRefStruct>` is illegal)

```csharp
// CANNOT be Action<PointerEventArgs> (ref struct can't be a generic type arg).
public delegate void PointerHandler(ref PointerEventArgs e);
public delegate void KeyHandler(ref KeyEventArgs e);
public delegate void CharHandler(ref CharEventArgs e);
```

Handlers are stored in **typed parallel columns** keyed by node slot, not boxed into the node:

```csharp
internal sealed class HandlerTable {     // GC edge — handlers are user closures (foundations: GC at edge OK)
    PointerHandler?[] _pressed, _released, _moved, _wheel, _entered, _exited, _tapped;
    KeyHandler?[]     _keyDown, _keyUp;
    // index-parallel to SceneStore slots; resized in lockstep with the spine.
}
```

The handler delegate is the **only** GC allocation on the input path, and it is amortized: a closure is allocated when the *Element* declaring it is reconciled (phase 5, edge), not per event. Dispatch invokes by ref — no boxing of args, no closure per invocation.

### 6.3 `HandlerMask` pre-filter (one `Flags[]` load + one mask test)

`NodeFlags` already carries `WantsPointer (1<<12)`, `WantsKey (1<<13)`, `WantsWheel (1<<14)` (architecture-spec §4.4). The reconciler sets these when a handler column for the node is non-null. Dispatch pre-filters: before resolving a `PointerHandler?` for a route node, it tests the single `Flags[n]` load against the relevant mask bit. A non-interactive subtree (a big static list of labels) costs one flag load per node and zero delegate-column probes. `InteractionInfo.HandlerMask` (ushort) is the finer per-category mask (Pressed vs Released vs Tapped …) consulted only after the coarse `NodeFlags` gate passes.

### 6.4 Tunnel / bubble over the captured route

```
route = [Root, A, B, …, HitNode]   (captured in §5.1 stackalloc span, root-first)

tunnel:  for i = 0 .. routeLen-1     (Root → HitNode)   offer Preview* handlers; stop if Handled
bubble:  for i = routeLen-1 .. 0     (HitNode → Root)   offer routed handlers;  stop if Handled
```

Tunnel = forward span, bubble = reversed span, enter/leave = common-ancestor diff over the same span (§5.4). `PositionDip` is re-projected into each route node's local space as routing descends/ascends (reuse the per-level inverse computed during hit-test by caching it alongside the route — a parallel `stackalloc Affine2D[]` of accumulated inverses, still zero heap).

### 6.5 `OnClick` = one declaration, three modalities

`OnClick` resolves to `Tapped` ∪ `Space`/`Enter` (when the node is focused) ∪ **UIA `Invoke`**. A single user declaration is reachable from pointer (gesture → Tapped), keyboard (focused + Space/Enter built-in), and assistive tech (UIA Invoke pattern marshaled to the UI thread re-enters the same delegate). The end-to-end "user clicks a Button" flow (architecture-spec §6 step 2d, and the AT note at line 1060) is the canonical example.

### 6.6 DSL framing (corrected — shape-compatible PORT, not "verbatim")

Reactor's `ElementModifiers.OnPointerPressed` is `Action<object, WinRT.PointerRoutedEventArgs>` — it leaks WinRT and violates the no-WinRT constraint, so **handler signatures are redefined portably** (the by-ref delegates above). The `OnX` / `.X()` **naming** is preserved (`.OnPointerPressed(handler)`, `.OnTapped(handler)`, `.OnKeyDown(handler)`). The DSL modifier accumulates the delegate into the per-render arena `ModifierOp` stream (architecture-spec §8 generator 2); the scene-writer (generator 4) installs it into the right `HandlerTable` column and sets the `Wants*` flag.

---

## 7. Gestures (per-pointer FSM + from-scratch inertia integrator)

`GestureRecognizer` runs a per-pointer FSM over the coalesced pointer stream. Gesture structs are **re-typed off `Vec2`** (Reactor uses `Windows.Foundation.Point` — gone).

```csharp
internal struct PointerFsm {            // one per active PointerId, in a SlabAllocator<PointerFsm>
    public GesturePhase Phase;          // Idle, Pressed, Tapping, Dragging, Manipulating
    public Vec2 Start, Last;
    public long DownTimeUs, LastMoveUs;
    public int  TapCount;               // double/triple-tap accumulation
    public VelocitySampler Velocity;    // ring of recent (dt, dPos) for fling
}
```

Recognized gestures → routed as their own bubble events: `Tapped`, `DoubleTapped`, `RightTapped`, `Holding` (long-press timer fired in `OnFrameEnd`), `ManipulationStarted/Delta/Completed` (translate/scale/rotate deltas).

**Inertia integrator (real work — WinUI's `ManipulationInertiaStarting` is gone):** on manipulation release, fling velocity is computed from the `VelocitySampler` window (last ~50 ms of samples, outlier-trimmed) and handed to a **friction-decay extrapolation** that runs in the **animation phase (7)**, not in dispatch. It writes the scroll viewport's `LocalTransform` directly (the `-ScrollOffset` translation is the viewport-child `LocalTransform`; architecture-spec §line 758) and marks `TransformDirty` only — **never** `LayoutDirty`. Decay terminates when |v| < threshold or the offset hits a clamp/bounce boundary. This is composition-style independent animation: no relayout, no re-record; the batcher re-applies the cached transform (architecture-spec §7 animation).

---

## 8. Focus (`FocusEngine`)

```csharp
public sealed class FocusEngine {              // portable; owns its own slabs; reads FocusNav column
    NodeHandle _focused;
    ArenaScratch _tabBuckets;                  // re-authored per nav, never retained
    readonly Stack<NodeHandle> _scopeStack;    // modal/trap scopes; restore prior focus on pop
    public NodeHandle Focused => _focused;
    public bool TryMoveFocus(FocusNavDir dir, FocusReason reason);   // Next/Prev/Up/Down/Left/Right/First/Last
    public void SetFocus(NodeHandle n, FocusReason reason);
    public void PushScope(NodeHandle scopeRoot); public void PopScope();
}
```

### 8.1 Tab order — pinned (folds the open '?')

Reads `FocusNav.{TabIndex, IsTabStop}` (architecture-spec §4.4 column) + `NodeFlags.{Focusable, IsTabStop, FocusScope}`:

- **Bucket A** = nodes with **positive** `TabIndex`, ordered by `TabIndex` ascending, ties broken by document (pre-order) order.
- **Bucket B** = nodes with `TabIndex == 0` / default, in pre-order.
- **Order: B follows A** (all positive indices first, then default doc-order).
- **Negative `TabIndex`** = focusable **programmatically only**, **skipped by Tab**.
- Buckets are computed into `_tabBuckets` arena scratch on each Tab press scoped to the active focus scope, then discarded — zero retained allocation.

### 8.2 XYFocus (gamepad / arrow directional nav)

Projection-based candidate scoring over `Bounds` (projected to client DIP via the shared chain §5.3): from the focused node's rect, candidates in the requested direction are scored by primary-axis distance + secondary-axis overlap penalty (the standard XY-focus heuristic). Gamepad mapping: DPad → arrows, A → `Invoke`, B → `Escape`. Only `Focusable` + visible + non-`A11yOffscreen` nodes are candidates.

### 8.3 Focus scopes / trapping

`_scopeStack` implements modal trapping: opening a dialog pushes its root as a scope; Tab/XYFocus candidate enumeration is clamped to the top scope; closing pops and **restores the previously-focused node** (captured at push). `NodeFlags.FocusScope (1<<18)` marks scope roots.

### 8.4 Focus visual — `DrawFocusRectCmd` anchored to the clip chain

The focus visual is a **synthesized opcode**, not a real Element. `FocusEngine` owns a single transient **overlay layer**; on every focus move it:
1. clears the prior `DrawFocusRectCmd`,
2. emits a new `DrawFocusRectCmd` whose geometry is the focused node's `Bounds` (LOCAL) **anchored to the focused node's clip chain** — i.e. recorded *as if* it were a child of the focused node, so it clips and scrolls with that node (a focus ring on a list item disappears correctly when the item scrolls out of the viewport's clip),
3. marks the overlay layer's root `PaintDirty` so the incremental DrawList re-records it next frame (the render thread picks it up at phase 8; clean siblings memcpy unchanged).

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct DrawFocusRectCmd {          // OWNED by this subsystem (architecture-spec §4.5 opcode list)
    public RectDip   Rect;                // node-LOCAL; positioned by the enclosing Push/Transform/Clip run
    public CornerRadius4 Radii;           // match the focused node's corners
    public BrushHandle Brush;             // HC-aware: pulls ISystemColors focus color in High Contrast
    public float     Thickness;           // logical px (Fluent 2px default)
    public float     DashPeriod;          // 0 = solid; >0 = dashed (Fluent dotted ring)
}
```

It is recorded with `Push/PopClip` + `Push/PopTransform` matching the node's clip chain (that is what "anchored to the clip chain" means concretely). Color comes from PAL `ISystemColors` so it tracks accent / High Contrast (architecture-spec §7 theming).

### 8.5 Hooks — `UseFocus` / `UseElementRef`

Names/return-shapes preserved (Reactor), reimplemented over `FocusEngine` (architecture-spec §5.8):

```csharp
public readonly struct ElementRef {       // wraps a NodeHandle; stale gen → null
    internal readonly NodeHandle Node;
    public bool IsAlive(SceneView s) => s.IsLive(Node);     // generational check
}
public ref struct FocusApi {              // returned by UseFocus
    public bool IsFocused;
    public void Focus();                  // requests focus in the NEXT dispatch window
    public void Blur();
}
```

`UseElementRef()` returns an `ElementRef` whose `NodeHandle` the reconciler wires when the element mounts (the ref's `Node` is back-patched in phase 5); a stale generation (node freed/recycled) yields null on read — never a use-after-free. `UseFocus()` declares focusability + optional `autoFocus`; the actual `SetFocus` for `autoFocus` runs in **phase 6.5 (layout-effect)** so it reads valid `Bounds` (e.g. scroll-into-view computes correctly). These hooks live in `FluentGpu.Hooks` and drive `FocusEngine` through an `IFocusSink` seam so `Hooks` stays GPU/Scene-agnostic (foundations §7 invariant 3).

---

## 9. Commands & accelerators

```csharp
public sealed record Command(StringId Id, string Label) {   // pure data — reused UNCHANGED from Reactor
    public bool CanExecute(object? param) => …;             // user-provided predicate (edge)
    public void Execute(object? param) => …;
}

internal struct AcceleratorEntry {        // SlabAllocator<AcceleratorEntry>
    public uint Vk; public ModifierKeys Mods; public NodeHandle Scope; public int CommandSlot;
}
```

**KeyDown ordering (WinUI order, folded):**
```
1. IME-if-composing   (a composing IME swallows the key — IImeSession.IsComposing gate)
2. AcceleratorRegistry match   (global/scoped accelerators run BEFORE routed dispatch — WinUI order)
3. Access keys (Alt)  (enter KeyTips overlay mode; Alt+char invokes the access-key target)
4. Routed tunnel/bubble OnKeyDown  (user handlers over the focused node's route)
5. Built-ins          (Tab/Shift-Tab → FocusEngine; arrows → XYFocus or caret; Space/Enter → Invoke focused; Esc → close scope)
```

`AcceleratorRegistry` (`SlabAllocator<AcceleratorEntry>`) is matched against the `(Vk, Mods)` with scope filtering (an accelerator scoped to a panel fires only when focus is within it). **Access keys (Alt)** push a transient **KeyTips overlay** (badges drawn via `DrawAccessKeyBadgeCmd`, the sibling opcode to `DrawFocusRectCmd`; architecture-spec §4.5). Commands are matched by `CommandSlot` → invoke `Command.Execute` (the GC edge), guarded by `CanExecute`. `UseCommand` (hook) registers/unregisters an entry on mount/unmount with a try/finally cleanup (Reactor philosophy: do not hide user bugs).

---

## 10. IME — v1 = Imm32 (TSF = v2)

**v1 = `WM_IME_*` / Imm32** (folds the re-scope): covers CJK, far simpler than TSF; lives in `Pal.Windows` behind the portable `IImeSession` seam.

```csharp
namespace FluentGpu.Pal;
public interface IImeSession {
    void Enable(bool on);
    void SetCompositionRect(in RectDip caretRectClient);   // candidate-window placement
    bool IsComposing { get; }                              // gates the KeyDown order (§9 step 1)
    // composition events arrive as InputKind.ImeStart/Update/EndComposition in the ring (§3)
}
```

- Windows impl: `WM_IME_STARTCOMPOSITION`/`WM_IME_COMPOSITION` → `ImmGetCompositionStringW` (GCS_COMPSTR for the in-progress underline string, GCS_RESULTSTR for the committed run); `ImmSetCompositionWindow`/`ImmSetCandidateWindow` for placement. Committed text arrives via `WM_CHAR` (§3).
- **Caret rect** for `SetCompositionRect` is computed via the **shared transform helper** (§5.3): the editable node's caret rect is LOCAL (from the text seam's `HitTestTextPosition→caretRect` once editing exists — architecture-spec §line 688, a v1-display-only/v2-editing boundary), projected to client DIP via `AccumulateToRoot`, then client→screen in `Pal.Windows`.
- **Composition underline** is rendered as a transient overlay run (an extra `DrawGlyphRun` + an underline `FillRect`) pulling HC colors.

**TSF = v2 (a major workstream, not a footnote):** `ITextStoreACP2`/TSF requires document lock arbitration, advise sinks, ACP range mapping, and composition mutation that respects the TSF **write-lock state** — explicitly deferred. When it lands it is **`[GeneratedComInterface]` cold COM** (human-timescale), not hand-vtable. macOS uses `NSTextInputClient` behind the same `IImeSession` seam.

---

## 11. Accessibility — UIA over the retained tree (no peer control)

UIA is a **projection** of the same `SceneStore` (P7: single source of truth — UIA `Navigate` is a topology walk, not a peer tree). It lives in `Accessibility.Uia` behind the portable `IA11yBackend` seam.

### 11.1 COM generation ruling (FOLDED — this overrides the spec's "hand-vtable both directions")

> **The architecture-spec §5.8/§8 "hand-built vtable / `ComPtr<T>` for both directions, no ComWrappers" wording is superseded for the UIA/TSF/OLE surface by the dotnet10 §4 + hardened-v1 §4.2 ruling.** UIA is **cold/warm** COM (human-timescale, never per-frame), so its providers are authored with **`[GeneratedComInterface]`** (consume `IUIAutomation*` services) and **`[GeneratedComClass]`** (the provider CCWs the OS calls into). Hand-vtable `calli` is kept ONLY for the per-frame hot path (D3D12/DComp/Present in the render thread) and in-loop DWrite CCWs — none of which Input/A11y touches.

Rationale (dotnet10 §4): on .NET 10 dispatch is a cached native fn-ptr vtable and the RCW/CCW is allocated once per object and cached; the cost source-gen COM adds (a wrapper object + a dictionary lookup) is irrelevant at human timescale, and it deletes a large error-prone hand-written unsafe surface with correct HRESULT→exception, `[PreserveSig]`, and `[UnmanagedCallersOnly]` vtables. **No `System.Runtime.InteropServices.ComWrappers` on the hot path** (there is no hot path here). The provider classes carry `[GeneratedComClass]`; the consumed UIA core interfaces carry `[GeneratedComInterface]`; the whole hierarchy stays in `Accessibility.Uia` (no cross-assembly `[GeneratedComInterface]` inheritance — vtable-offset rebuild pitfall).

```csharp
[GeneratedComInterface, Guid("…IRawElementProviderSimple…")]
internal partial interface IRawElementProviderSimple { … }
[GeneratedComInterface, Guid("…IRawElementProviderFragment…")]
internal partial interface IRawElementProviderFragment { … }

[GeneratedComClass]
internal sealed partial class NodeProvider :
    IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot,
    IInvokeProvider, IValueProvider, IToggleProvider /* … pattern interfaces by PatternFlags */
{
    readonly A11yBackend _backend;
    NodeHandle _node;            // internal binding only
    uint _nodeGen;               // ABA guard — see §11.5
}
```

### 11.2 The `UiaClientsAreListening` gate (zero work when no AT attached)

**`UiaClientsAreListening()` gates ALL a11y work.** Cached, refreshed on `WM_GETOBJECT` + a low-frequency timer — **not** a per-frame P/Invoke. When no AT is attached: **zero `NodeProvider` objects are materialized**, the reconciler skips all live-region capture, and `get_*` paths are never entered. This is what makes the `A11yInfo` column "cold" (architecture-spec §4.4) — it is populated by the reconciler regardless (cheap StringId writes) but never *read* until an AT shows up.

### 11.3 `Navigate` is a topology walk (the single-tree payoff)

`IRawElementProviderFragment.Navigate(direction)` walks `SceneStore` Topology:
- **Parent/FirstChild/LastChild/NextSibling/PreviousSibling** map onto the Topology columns,
- **skipping `A11yRaw` nodes** (decorative — `NodeFlags 1<<25`) and **collapsing non-`A11yPresent` nodes** (`1<<24`) so the a11y tree is the meaningful logical tree, not the raw visual tree.
Providers are **materialized lazily** per node first reached by a navigate/hit-test, cached in a `Dictionary<NodeHandle, NodeProvider>` (the one allowed long-lived per-object CCW cache; dotnet10 §4 — wrap once, never per frame).

### 11.4 Patterns, name, bounding rect, live regions

- **`PatternFlags` → vtable inclusion:** `A11yInfo.Patterns` (ushort bitfield) selects which pattern interfaces the `NodeProvider` exposes: Invoke / Value / Toggle / Selection / RangeValue / ExpandCollapse / Scroll / Text / Grid. `Invoke` re-enters the same `OnClick` delegate (§6.5).
- **`get_BoundingRectangle`** uses the shared ancestor-transform-chain helper (§5.3 `LocalRectToClient`) + client→screen — **not** a bare client offset.
- **Auto-name derivation:** the `AccessibilityScanner` heuristics (LabeledBy → content text → AutomationId fallback) are extracted into a **shared helper** consumed by BOTH the DEBUG `AccessibilityScanner` lint and the runtime `get_Name`.
- **Live regions:** the **reconciler** captures the pre-write `StringId` for `A11yLiveRegion` nodes (`1<<26`) and compares post-write; on change it raises `RaiseLiveRegionChanged`. `UseAnnounce` raises the **UIA Notification event** (`UiaRaiseNotificationEvent` / `IRawElementProviderSimple3`) added to `IA11yBackend`; it fires in **phase 12** for any seq ≤ last-presented (so screen-reader announcements track what was actually shown).

### 11.5 UIA threading — strict `UseComThreading` (folds the unsafe-read MAJOR)

**`ProviderOptions.UseComThreading`** is mandatory: ALL provider calls (read AND write) marshal to the UI thread via the HWND pump (the HWND must be STA/pumping). There is **no "read on the calling thread"** path — that was the SoA-corruption MAJOR. An OS RPC thread calling `get_Name`/`Invoke`/`Navigate` is bounced through the window message pump onto the UI thread before it dereferences any `SceneStore` column, preserving single-writer confinement (hardened-v1 §2.1). UIA mutations (events) are *posted* from phase 2/5 and delivered on the UI thread.

### 11.6 `GetRuntimeId` — stable logical identity (folds the AT-tracking fix)

`GetRuntimeId` is derived from **stable logical identity** — a keyed-path hash / persistent per-logical-node id — **not** slot+gen. Slot+gen is ABA-prone (a recycled slot would alias a different element after reconcile, breaking the AT's element-tracking across updates). Slot+gen is used **only** as the internal provider→node binding, guarded by `_nodeGen` (a stale gen invalidates the cached `NodeProvider` so a recycled slot never serves the wrong node).

### 11.7 macOS NSAccessibility boundary

`IA11yBackend` is implemented on macOS by **`Accessibility.NSAccessibility`**: `NSAccessibility`-conforming objects reading the **same** `A11yInfo` / topology columns (architecture-spec §9 table). Roles map (Button → `NSAccessibilityButtonRole`, etc.); `accessibilityFrame` uses the same shared transform chain (§5.3) projected to screen via Cocoa. Nothing above the `IA11yBackend` seam changes — the portable `A11yInfo` column + topology walk + auto-name helper are reused; only the OS-object conformance differs. The "what must not leak above the seam" list (architecture-spec §9) forbids any `IRawElementProvider*` / `NSAccessibility*` type above `Hosting`.

---

## 12. Clipboard & drag/drop (UI thread; OLE via generated COM)

```csharp
public interface IClipboard {
    bool TryGetText(out string text);            // edge GC alloc allowed (returns a string)
    void SetText(ReadOnlySpan<char> text);
    bool TryGetData(StringId format, out ReadOnlySpan<byte> blob);
    void SetData(StringId format, ReadOnlySpan<byte> blob);
}
public interface IDragDropBackend {
    DragEffect DoDragDrop(IDataPayload payload, DragEffect allowed);   // BLOCKS on the UI thread modal loop
    void RegisterDropTarget(NativeHandle window);
}
```

- **Clipboard:** OLE `CF_UNICODETEXT` + custom formats. `TryGetText(out string)` is an explicit **edge GC allocation** (not a hot path).
- **`DoDragDrop` runs on the UI thread** (folds the worker-thread bug): it pumps its own modal message loop, so it MUST be on the pumping thread. `IDropTarget` callbacks (`DragEnter`/`DragOver`/`DragLeave`/`Drop`) are invoked by the OS **on the UI thread during that loop**, and are routed into the single-threaded input path as `InputKind.Drag*` events (§3/§4) — **not** free-threaded `event Action`.
- Drag source is `IDropSource`/`IDataObject`. Per the COM ruling (§11.1) these OLE interfaces are **cold/warm → `[GeneratedComInterface]`/`[GeneratedComClass]`** (human-timescale; not the per-frame hot path).
- **Drop feedback** = a transient DrawList overlay (insertion line / hover highlight), same overlay mechanism as the focus rect (§8.4).
- macOS: `NSPasteboard` / `NSDraggingSource`/`NSDraggingDestination` behind the same seams.

---

## 13. Hooks this subsystem OWNS (declared phase 4, in `FluentGpu.Hooks`)

| Hook | Returns | Wires to | Effect timing |
|---|---|---|---|
| `UseFocus(bool autoFocus=false)` | `FocusApi` (IsFocused, Focus(), Blur()) | `FocusEngine` via `IFocusSink` | `autoFocus` SetFocus at **6.5** (valid Bounds) |
| `UseElementRef()` | `ElementRef` (NodeHandle wrapper, stale-gen→null) | reconciler back-patch in phase 5 | n/a |
| `UseCommand(Command, ReadOnlySpan<DepKey> deps)` | nothing (registers) | `AcceleratorRegistry`/`CommandRouter` | register on mount / cleanup on unmount |
| `UseAccelerator(vk, mods, Command)` | nothing | `AcceleratorRegistry` slab | register/unregister |
| `UseGesture(GestureKind, handler)` | nothing | `GestureRecognizer` config | n/a |
| `UseAnnounce()` | `Action<StringId, AnnounceLive>` | `IA11yBackend.RaiseNotification` | fires at **12**, seq ≤ last-presented |

All deps are `ReadOnlySpan<DepKey>` (foundations §6.4 / reconciler-hooks §3.2) — **never** `params object[]`. Reference deps (e.g. a `Command` instance) go to the parallel managed `GcDepTable` compared by `ReferenceEquals` (the `[FieldOffset]` GC/scalar union is illegal CLR layout — reconciler-hooks §3.2). The 3-signal memo skip (`SelfTriggered || propsChanged || HasConsumedContextChanged`) governs whether these hooks re-run; `SubtreeDirty` is traversal scope only, never a skip input.

---

## 14. Zero-alloc & thread-confinement story

- **Phases 1–2 alloc budget = freshly-captured user closures at the edge only** (architecture-spec §8). `Drain`, hit-test, gesture FSM, focus nav, accelerator match, route capture are all slab/arena/`stackalloc`. The route is `stackalloc Span<NodeHandle>` + a parallel `stackalloc Affine2D[]` of inverses; tab buckets are arena scratch reset per nav; the FSM/accelerator/handler tables are slab-backed.
- **No GC ref into a pool on the hot path** (foundations §1.1): handler delegates are the GC edge, stored in `HandlerTable` columns parallel to the spine, resolved by slot, validated by generation. `ElementRef`/`AcceleratorEntry`/provider bindings carry `NodeHandle` + a gen check, never a raw object ref into the slab.
- **Input owns ZERO ComPtr.** UIA/TSF/OLE COM is owned by `Accessibility.Uia`/`Pal.Windows` and is cold/warm — confined to those leaves and marshaled to the UI thread before touching Scene. The render thread owns every hot-path ComPtr; Input never crosses that seam.
- **Single-writer on SceneStore is preserved:** Input writes nothing to Scene. UIA reads are marshaled to the UI thread (the SceneStore's sole consumer-thread for these columns), so there is no off-thread SoA read. `ThreadGuard.AssertWriter` in `InputDispatcher`/`FocusEngine` deterministically throws on a wrong-thread entry in asserts builds (erased from shipping AOT — production safety == CI coverage; hardened-v1 §8).
- **DrawList contribution is POD:** the only thing Input emits downstream is `DrawFocusRectCmd` / `DrawAccessKeyBadgeCmd` into its overlay layer, marked `PaintDirty`; the render thread records it at phase 8 exactly like any node. No cross-thread mutable state.

---

## 15. Failure / edge cases

- **Stuck hover** (content moves under a stationary cursor): hover route invalidated on any topology/z/overlay-push epoch change (§5.4), not just layout commits — re-resolved immediately for a stationary pointer.
- **Pointer capture across unmount:** if the captured node is freed (gen bump), capture is dropped on the next event (generational check), and a synthetic `PointerCaptureLost` is routed to the (now-dead) node's last-live ancestor.
- **Focus on a freed node:** `_focused`'s gen is validated each frame; a stale focus falls back to the nearest live ancestor focus scope, else clears focus and hides the focus rect.
- **`ElementRef` after recycle:** stale gen → `IsAlive` false → null read; never a UAF.
- **IME composing + accelerator collision:** the IME-if-composing gate (§9 step 1) wins — a composing IME swallows the key so Ctrl+letter accelerators don't fire mid-composition.
- **AT attaches mid-session:** `WM_GETOBJECT` flips `UiaClientsAreListening` → providers materialize lazily on first navigate; no retroactive tree build.
- **Half-built reconcile / in-flight snapshot:** dispatch reads the committed prev-frame Bounds double-buffer only (§2) — never observes a torn topology.
- **Wheel flood / >1 kHz pointer flood:** ring move-coalescing (§3) bounds dispatch to one move + summed wheel per pointer per frame.
- **UIA RPC re-entrancy:** all provider calls marshaled to the UI thread (§11.5); an `Invoke` arriving mid-frame is queued behind the pump and applied at the next phase-2 boundary, never racing the SoA.
- **Overflow route depth** (> stackalloc cap, pathological deep tree): falls back to an arena slice for the route, still zero heap.

---

## 16. Cross-platform (macOS) boundary

Nothing above `Hosting` recompiles (architecture-spec §9). The Input subsystem is already portable (Vec2/PointPx, `InputEvent` POD, `SceneStore` reads, the shared transform helper). The OS leaves swap:

| Seam | Windows leaf | macOS leaf | What stays portable |
|---|---|---|---|
| pump → ring | `Pal.Windows` WM_* → InputEvent | `Pal.Cocoa` NSEvent → InputEvent | `InputEventRing`, `InputEvent` schema, coalescing |
| `IImeSession` | Imm32 (v1) / TSF (v2) | `NSTextInputClient` | caret rect via shared transform; composition events in the ring |
| `IA11yBackend` | `Accessibility.Uia` (UIA, generated COM) | `Accessibility.NSAccessibility` | `A11yInfo` column, topology walk, auto-name helper, stable RuntimeId concept |
| `IClipboard`/`IDragDropBackend` | OLE (generated COM), UI-thread `DoDragDrop` | `NSPasteboard`/`NSDragging*` | format StringIds, UI-thread routing, drop-feedback overlay |
| `ISystemColors` (focus/HC color) | Windows HC tokens | macOS appearance colors | `DrawFocusRectCmd.Brush` source |

Forbidden above the seam (architecture-spec §9): any `HWND`/`NSWindow`/`HRESULT`/`NSError`/`ComPtr`/`id<…>`/`IRawElementProvider*`/`NSAccessibility*`/`WM_*`/`NSEvent`/`Windows.Foundation.Point`/WinRT type. The portable layer sees only `Size2/Scale/PointPx/Vec2`, opaque `NativeHandle`, POD `InputEvent`/`WindowEvent`, generational handles.

---

## 17. What this subsystem OWNS (authority list)

- **Types:** `InputEvent`, `InputEventRing` schema, `InputKind`, `InputDispatcher`, `HitTester`, `HitShape`, `HitGeom`, `TransformChain` (the ONE shared transform helper), `PointerEventArgs`/`KeyEventArgs`/`CharEventArgs` (ref structs), `PointerHandler`/`KeyHandler`/`CharHandler` (by-ref delegates), `HandlerTable`, `GestureRecognizer`/`PointerFsm`/`VelocitySampler` (+ inertia integrator), `FocusEngine`/`ElementRef`/`FocusApi`/`FocusNavDir`, `AcceleratorRegistry`/`AcceleratorEntry`/`CommandRouter`, `NodeProvider` (UIA CCW), `A11yBackend`.
- **DrawList opcodes:** `DrawFocusRectCmd`, `DrawAccessKeyBadgeCmd`.
- **NodeFlags bits (read; set by reconciler):** `HitTestVisible`, `PointerTransparent`, `WantsPointer/Key/Wheel`, `Focusable`, `IsTabStop`, `FocusScope`, `IsFocused`, `A11yPresent`, `A11yRaw`, `A11yLiveRegion`, `A11yOffscreen`.
- **SceneStore columns (read):** `InteractionInfo` (HitCorners/HandlerMask/CursorId/HitShape/HitGeometry), `FocusNav` (TabIndex/IsTabStop/XY), `A11yInfo` (Name/AutomationId/HelpText/ControlType/Patterns/…).
- **PAL seams:** `IImeSession`, `IClipboard`, `IDragDropBackend`, `IA11yBackend`, `IFocusSink` (the Hooks→FocusEngine bridge), and consumes `ISystemColors`.
- **Hooks:** `UseFocus`, `UseElementRef`, `UseCommand`, `UseAccelerator`, `UseGesture`, `UseAnnounce`.

---

## Changed vs the original synthesis

Amendments folded into this doc (vs the foundations §6 single-thread synthesis and the literal architecture-spec §5.8/§8 wording):

1. **COM-generation ruling overrides "hand-vtable both directions."** architecture-spec §5.8/§8 said *all* COM (both directions) is hand-vtable / no ComWrappers. This doc applies the dotnet10 §4 + hardened-v1 §4.2 ruling: UIA/TSF/OLE are **cold/warm → `[GeneratedComInterface]`/`[GeneratedComClass]`**; hand-vtable `calli` is reserved for the per-frame hot path (which this subsystem never touches). No `ComWrappers` on the hot path stated explicitly.
2. **`Action<TRefStruct>` is illegal — named by-ref delegates instead** (`delegate void PointerHandler(ref PointerEventArgs e)`), stored in typed `delegate[]` columns. (Folds the §5.8 ref-struct/delegate BLOCKER.)
3. **One shared forward/inverse transform-accumulation helper** (`TransformChain`) serves hit-test, UIA `get_BoundingRectangle`, and IME caret. (P8.)
4. **Hit-test uses the paint's fill RULE** (nonzero winding default), not just the vertices, and the SDF self-tests are single-sourced with the renderer's SDF math. (Folds the gpu-renderer §5 input-a11y fix.)
5. **Bounds is node-LOCAL**; hit-test applies `Inverse(LocalTransform)` per descent level and self-tests against LOCAL bounds. (P8 coordinate-convention fix.)
6. **C# events replaced by a POD `InputEventRing`** with per-pointer move-coalescing and accumulated wheel; zero delegate/closure alloc at the pump. (architecture-spec §4.7 MAJOR amendment.)
7. **Win32 map fixes:** `WM_POINTERLEAVE` + `TrackMouseEvent(TME_LEAVE|TME_HOVER)` for leave; `WM_CHAR` after `TranslateMessage` for committed text.
8. **Hover cache invalidated on ANY topology/z/overlay-push change**, not just layout commits. (Folds the stuck-hover fix.)
9. **Tab order pinned:** positive-TabIndex bucket (asc, doc-order tiebreak) → default/0 bucket (pre-order); negative = programmatic-only, Tab-skipped. (Folds the open '?'.)
10. **`DrawFocusRectCmd` anchored to the focused node's clip chain** (clips/scrolls correctly), overlay layer marked dirty on focus move.
11. **From-scratch inertia integrator** in the animation phase (7) writing the viewport `LocalTransform`, `TransformDirty` only — never `LayoutDirty`. (WinUI's `ManipulationInertiaStarting` is gone.)
12. **DSL framing corrected:** shape-compatible PORT (`OnX`/`.X()` naming kept), not "verbatim" — Reactor's WinRT-leaking signatures redefined portably.
13. **UIA strict `UseComThreading`** (read AND write marshaled to the UI thread; no calling-thread read). (Folds the unsafe-read SoA-corruption MAJOR.)
14. **`GetRuntimeId` from stable logical identity**, not slot+gen (slot+gen is internal binding only, ABA-guarded by `_nodeGen`). (Folds the AT-tracking fix.)
15. **`UiaClientsAreListening` gates ALL a11y work** (cached, refreshed on `WM_GETOBJECT` + low-freq timer — not per-frame P/Invoke); zero providers when no AT attached.
16. **`Navigate` is a topology walk** (skip `A11yRaw`, collapse non-`A11yPresent`) — the single-tree payoff; no peer-control tree.
17. **Live regions + UIA Notification event** added to `IA11yBackend` for `UseAnnounce`; auto-name heuristics extracted into a shared helper for both the DEBUG lint and runtime `get_Name`.
18. **IME re-scoped:** v1 = Imm32 (`WM_IME_*`); TSF (`ITextStoreACP2`, lock arbitration, sinks, ACP ranges) = a v2 workstream, and v2 TSF is generated cold COM, not hand-vtable.
19. **`DoDragDrop` runs on the UI thread** (pumps its own modal loop); `IDropTarget` callbacks route into the single-threaded input path, not free-threaded events. (Folds the worker-thread drag bug.)
20. **Phase-2-against-committed-previous-frame** stated as a hardened-v1 §2.2 confinement contract: dispatch hit-tests the UI-owned Bounds double-buffer, never the in-flight render snapshot or a half-built reconcile; handler `setState` queued via the `_isRendering` guard.
21. **Hook deps are `ReadOnlySpan<DepKey>`** with reference deps in the parallel `GcDepTable` (illegal `[FieldOffset]` union avoided); 3-signal memo skip; `SubtreeDirty` = traversal scope only.
