# FluentGpu Subsystem — Input, Focus, IME, Hit-Testing & Accessibility (UIA)

*Owner doc for the **Input** subsystem (portable, COM-free) plus its OS leaves (`FluentGpu.Windows` Pal/ IME/clipboard/drag, `FluentGpu.Windows` Uia/). Promotes architecture-spec §5.8 into a full design and folds the §5.8 corrections + the COM-generation ruling (dotnet10 §4 / hardened-v1 §4.2). Cross-cutting contracts (threading, memory, SceneStore columns, DrawList encoding, PAL/RHI seams, hooks/reconcile, color) are OWNED elsewhere and only **referenced** here — see foundations.md, architecture-spec.md §4, hardened-v1-plan.md §2/§4, dotnet10-csharp14-zero-alloc.md §4, and subsystems/{reconciler-hooks,gpu-renderer,text}.md.*

---

## 0. Posture & one-line thesis

Input is a **pure consumer of the committed previous-frame Scene**: it reads `SceneStore` topology + `Bounds` (LOCAL) + `LocalTransform` + `NodeFlags` + `InteractionInfo`/`FocusNav`/`A11yInfo` columns, and it writes **nothing to the Scene** — its only mutations are (a) appending to `RerenderToken`/the setState coalescer (via user delegates), (b) `FocusEngine` state (its own slabs), and (c) one transient overlay layer for the focus rect / KeyTips / drop feedback. It runs entirely on the **UI thread, phases 1–2**, against the **last-published-consistent** Scene (never the in-flight reconcile, never the render-thread snapshot). Accessibility (UIA) is a *projection* of the same retained tree — no peer-control tree, no shadow DOM — reached through generated CCWs, gated to do **zero work** when no AT is attached.

The single hardest correctness facts this doc commits to:
1. **`Action<TRefStruct>` is illegal C#.** All hot event args are `ref struct`; all handlers are **named `delegate` types taking the args by `ref`**, stored in typed `delegate[]` columns.
2. **One shared forward/inverse transform-accumulation helper** is the sole authority for composing ancestor transform + clip. Hit-test, UIA `get_BoundingRectangle`, and IME caret all call it. There is exactly one matrix-walk in the subsystem.
3. **UIA is `UseComThreading`-strict.** Every provider call (read AND write) marshals to the UI thread via the HWND pump — there is no "read on the calling thread" fast path. SoA columns are never touched off-thread.
4. **`GetRuntimeId` is derived from stable *logical* identity**, never slot+gen (slot+gen is ABA-prone across reconcile and would break AT element-tracking).
5. **Pointer capture is *tentative until arena resolution*.** A **gesture arena** sits above `PointerFsm`: from `PointerDown` to a winning recognizer, capture is provisional, `e.Handled` is one *input into* arena resolution rather than the resolution itself, and resolution may defer across pointer-move frames (eager-win / pointer-up sweep / hold-release). This is a documented semantics shift, folded from the gap analysis (L2 §5.4), not a bolt-on.
6. **UIA text is a real document surface.** `ITextProvider`/`ITextRangeProvider` (GetSelection / RangeFromPoint / GetEnclosingElement / text attributes / move-by-unit) are projected over the **same** `TextLayoutSlot` read-side (`text.md` §8) that backs on-screen selection — one read-side, two consumers (L1/L3). There is no second text model for AT.
7. **The arena, overlays, cursor, and edge-autoscroll all share the one hit route.** The reverse-z route computed once per pointer event (§5.1) is the spine for: tunnel/bubble, arena membership, cursor resolution (topmost-wins along the route), light-dismiss hit-classification, and edge-autoscroll viewport detection. There is exactly one hit-test per pointer event per frame.

---

## 1. Assemblies, deps, threads

| Concern | Assembly | Deps | Thread | COM? |
|---|---|---|---|---|
| Hit-test, dispatch, **gesture arena**, gestures, focus, commands, accelerators, **overlay/portal manager**, **cursor resolver**, **edge-autoscroll driver**, event args, input hooks | **`FluentGpu.Input`** (portable) | `Scene`, `Layout`(iface), `Pal`(iface), `Animation`(iface), `Foundation` | UI thread, phases 1–2 + 7 (inertia, edge-autoscroll) | **none** |
| Input hooks surface (`UseFocus`/`UseElementRef`/`UseCommand`/`UseAnnounce`/`UseGesture`/`UseAccelerator`) | **`FluentGpu.Hooks`** (the hook *shape*) impl wires through `Input` seams | `Dsl`, `Foundation` | UI thread, phase 4 (declared) | none |
| IME (Imm32 v1; TSF v2) | **`FluentGpu.Windows` (Pal/ folder)** behind `Pal`.`IImeSession` | `Pal`, `Foundation` | UI thread (window pump) | flat C / `[LibraryImport]` (Imm32 is not COM); TSF v2 = `[GeneratedComInterface]` |
| Clipboard + drag/drop | **`FluentGpu.Windows` (Pal/ folder)** behind `Pal`.`IClipboard`/`IDragDropBackend` | `Pal`, `Foundation` | UI thread (OLE modal loop) | OLE via `[GeneratedComInterface]`/`[GeneratedComClass]` |
| UIA provider tree | **`FluentGpu.Windows` (Uia/ folder)** behind `IA11yBackend` | `Scene`(read-only ref), `Pal`(iface), `Foundation` | UI thread (all calls marshaled) | UIA via `[GeneratedComInterface]`/`[GeneratedComClass]` |
| macOS a11y / IME | **`Accessibility.NSAccessibility`**, **`Pal.Cocoa`** | same seams | main thread | Objective-C runtime, no COM |

All OS leaves are referenced **only by `Hosting`** (foundations §7 cycle invariant 2). `FluentGpu.Input` itself carries `[assembly: DisableRuntimeMarshalling]` (it is 100% blittable, no source-gen COM). The COM leaves (`FluentGpu.Windows` Uia/, the OLE/TSF parts of `FluentGpu.Windows` Pal/) live in / mirror the **`PlatformIntegration`** policy (dotnet10 §3): **no** `DisableRuntimeMarshalling` (source-gen COM uses `in`/`out`), and each generated COM interface hierarchy is kept inside one assembly (cross-assembly `[GeneratedComInterface]` inheritance has a vtable-offset rebuild-coupling pitfall).

**Why this is `safe-by-construction` for confinement:** Input never owns a `ComPtr`; the render thread owns every ComPtr (hardened-v1 §2.1). The only COM Input *causes* (UIA/TSF/OLE callbacks) is marshaled back to the UI thread before it touches any Scene column, so the single-writer rule on `SceneStore` is preserved.

---

## 2. Where each piece lands in the 13-phase loop (and on which thread)

Reference: architecture-spec §4.8 + hardened-v1 §2.2. Input touches **phase 1 (pump), phase 2 (dispatch), phase 4 (hook declaration), phase 6.5 (focus-into-view layout effect)**, and **phase 7 (inertia integrator tick)**. It is *upstream* of PUBLISH(13a) and the render thread.

```
 1 pump            [UI]  FluentGpu.Windows Pal/ WndProc → InputEvent/WindowEvent POD → InputEventRing (move-coalesced)
 2 input-dispatch  [UI]  InputDispatcher.Drain(ReadOnlySpan<InputEvent>) against the COMMITTED prev-frame Scene:
                          a. hit-test ONCE (reverse-z, inverse transform, shape-accurate self-test) → the route
                          b. HandlerMask pre-filter
                          c. GESTURE ARENA: open arena on PointerDown, enroll recognizers along the route,
                             tentative capture; PointerFsm members vote accept/reject; eager-win sweep
                          d. cursor resolution along the route → Pal.SetCursor (L10)
                          e. light-dismiss FSM classifies outside-press for open overlays (L4, via the arena)
                          f. edge-autoscroll: if a tracking gesture (selection-drag / drag-reorder) is past a
                             scroll viewport edge, arm/refresh the edge-autoscroll driver (L11)
                          g. accelerator match → access keys → routed tunnel/bubble → built-ins (Tab/arrow/Esc)
                          h. focus moves; UIA mutations POSTED here (marshaled, applied on UI thread)
                          handler setState → coalescer (Interlocked CAS), queued for THIS frame's render (guard FALSE)
 3 hook-flush      [UI]  (Reconciler owns) — setState applied
 4 render          [UI]  Component.Render() declares UseFocus/UseCommand/UseAnnounce/UseGesture/UseAccelerator/
                          UseOverlay/UsePointerCursor
 5 reconcile       [UI]  (Reconciler owns) — writes A11yInfo/FocusNav/InteractionInfo/SelectionState columns;
                          collection-relation (SetSize/PositionInSet/Level) capture from the virtualizer; live-region capture
 6 layout          [UI]  (Layout owns) — writes Bounds[] (LOCAL); overlay anchor placement-with-flip (layout.md §10)
 6.5 layout-effects[UI]  UseFocus(autoFocus)/scroll-into-view read VALID Bounds[]; focus rect + overlay layers marked dirty;
                          arena pointer-up sweep that needs post-layout geometry runs here
 7 animation       [UI]  inertia integrator ticks fling (Input-owned) → SetScrollOffset/ApplyScrollPosition (clamp +
                          virtual re-realize; §7B); EDGE-AUTOSCROLL driver integrates its velocity → ScrollOffset (same path) (L11)
PUBLISH (13a)      [UI]  SceneFrame snapshot sealed — Input is done for the frame
 8-11              [RENDER] record/batch/submit/present — Input contributes ONLY the DrawFocusRingCmd /
                          DrawSelectionRectCmd / DrawAccessKeyBadgeCmd / overlay-layer opcodes it authored
                          (recorded like any other node by the render thread)
12 passive-effects [UI]  UseAnnounce fires UiaRaiseNotificationEvent for any seq ≤ last-presented
```

**Phase-2-against-the-committed-previous-frame contract (hardened-v1 §2.2):** dispatch hit-tests the **UI-owned `Bounds` double-buffer** — the topology+Bounds as of the last completed reconcile/layout — *never* the in-flight `SceneFrame` snapshot the render thread is consuming, and never a half-built reconcile. This is why Input is phases 1–2: the Scene it reads is stable and single-writer-consistent. A `setState` raised by a handler is queued (the `_isRendering` guard is FALSE in dispatch, so it marks dirty and lands in *this* frame's render phase 4).

---

## 3. Phase 1 — pump → `InputEventRing` (FluentGpu.Windows Pal/ owns the encode; Input owns the schema)

The window writes POD into a host-owned slab-backed ring (architecture-spec §4.7 amendment: **C# events are replaced by a POD ring**; no per-event delegate/closure alloc). `IPlatformWindow.PumpInto(ref InputEventRing ring)` drains `WM_*`/`NSEvent` into it once per frame.

```csharp
namespace FluentGpu.Pal;     // schema lives at the Pal seam (the window writes it, Input reads it); portable

public enum InputKind : byte {
    PointerMove, PointerDown, PointerUp, Wheel, PointerCancel,   // PointerCancel = capture-lost / touch-cancel
    Key, KeyUp, Char,                     // Char = committed text (WM_CHAR after TranslateMessage)
    WindowBlur, WindowFocus, WindowStateChanged,
    // ImeStart/Update/EndComposition + Drag* enrol here as the IME/OLE seams land (§9, §12)
}

// As-built: a blittable `readonly record struct`, no [StructLayout] (sequential is the default; the layout is
// NOT load-bearing — it is never reinterpret-cast over the seam, never memcpy'd as a fixed-size blob; the ring
// (below) stores it by value in a managed `InputEvent[]` slab). Trailing optional ctor params keep mouse call
// sites (PointerId = 0, Pressure = 1) source-compatible.
public readonly record struct InputEvent(
    InputKind Kind, Point2 PositionPx, int Button, int KeyCode, float ScrollDelta = 0f,
    KeyModifiers Mods = KeyModifiers.None, PointerKind Pointer = PointerKind.Mouse,
    bool IsRepeat = false, uint TimestampMs = 0, uint PointerId = 0, float Pressure = 1f);
//      PositionPx  CLIENT-space DIP, DIP-converted ONCE at the pump boundary (the field name is `PositionPx`;
//                  the value is DIP — naming debt, not a coordinate bug). Wheel: ScrollDelta (DIP, signed).
//      KeyCode     VK_* for Key/KeyUp, UTF-32 codepoint for Char (one field, kind-discriminated).
//      TimestampMs platform message time in MILLISECONDS — drives velocity sampling (§7B) and multi-click.
//      PointerId   stable per active contact (mouse = 0; touch/pen carry the OS pointer id); the ring coalesces
//                  moves per id and the dispatcher captures per contact (§4 capture table).
//      Pressure    normalized contact pressure (mouse = 1; touch/pen report 0..1).
```

> **Ratified as-built (the four superseded forms are waived as not load-bearing).** The shipped event is the
> `record struct` above, owned at the `FluentGpu.Pal` seam (not a portable `FluentGpu.Input` struct). The new
> `PointerId`/`Pressure` fields are the only multi-contact additions §3 mandates; everything else the earlier
> spec drew is intentionally **not** reconciled, because none of it is load-bearing: the explicit
> `[StructLayout(LayoutKind.Sequential)]`/`40B` size <!-- canon-allow: names the waived layout form -->
> (the event is never reinterpret-cast or memcpy'd as a fixed blob — it lives by value in the ring's managed
> slab, so its byte layout buys nothing); `TimestampUs` <!-- canon-allow: names the waived field --> (the
> platform message clock is milliseconds — `TimestampMs` — and ms resolution is sufficient for the §7B EMA and
> multi-click windows); the `KeyOrChar` <!-- canon-allow: names the waived field --> rename (as-built is
> `KeyCode`, same kind-discriminated dual use); and the `Vec2`→`PosDip` <!-- canon-allow: names the waived
> field --> rename (as-built is `Point2 PositionPx`, DIP-valued — a naming debt, not a coordinate-space bug).
> The §6 ref-struct event-args migration is likewise a non-goal (the cold-edge handler classes are fine).

**The ring is a fixed-capacity, drained-to-empty slab — NOT a circular buffer.** `InputEventRing` is a `new InputEvent[Capacity]` slab (cap 512) that the host **Clear()s, the window fills, the dispatcher drains whole** every frame (`AppHost.RunFrame`). Because it empties each frame, a single contiguous `ReadOnlySpan<InputEvent>` over `[0, count)` is always the complete frame's input — which is exactly what `Drain` (§4) consumes; a ring/circular buffer would split that span across the wrap and break the single-span contract. On overflow of a non-coalescible event the **oldest pending move** is dropped (else the incoming event) — bounded, zero-growth, no `Array.Resize` (the earlier growable-ring form is retired).

**Move-coalescing (Win32 map fix, folded):** the >1 kHz `WM_POINTERUPDATE`/`WM_INPUT`/`WM_MOUSEMOVE` flood is collapsed to the **latest** `PointerMove` per `PointerId` at ring-write time (the slab keeps a fixed per-id "last-move index" table — reset each drain, so allocation-free at steady state — and overwrites that id's pending move in place), so dispatch sees at most one move per pointer per frame. Consecutive `Wheel` deltas at the same position **accumulate** (sum, not last). Down/Up/Key/Char/Cancel are never coalesced (ordering-significant).

**Win32 map fixes (folded) — pump primitives ratified:**
- **`EnableMouseInPointer(TRUE)` at window create** is the ratified pump mode: it routes mouse, touch, and pen uniformly through the `WM_POINTER*` family, so one decode path tags `PointerId` + `PointerKind` + `Pressure` and the legacy `WM_MOUSE*`/`SetCapture` path is retired atomically (running both double-counts). NC caption input then arrives as `WM_NCPOINTER*` (the custom-frame handlers extend to it; `WM_NCHITTEST` is unaffected).
- **`GetPointerFrameInfoHistory`** is the ratified OS-coalesced drain: each `WM_POINTERUPDATE` carries a frame of back-buffered samples which the pump reads in one call (the OS-side analogue of the slab's per-id coalescing), then **DIP-converts once** with the window's current effective DPI → ring. `GetPointerInfo`/`GetPointerType` classify the contact; `WM_POINTERCAPTURECHANGED` → a per-`PointerId` `PointerCancel` (§4).
- Leave tracking via `WM_POINTERLEAVE` + `TrackMouseEvent(TME_LEAVE|TME_HOVER)` — **not** `RegisterTouchHitTestingWindow`.
- Committed text is `WM_CHAR` after `TranslateMessage` (`WM_UNICHAR` optional for >BMP keyboards).

**DPI (foundations / architecture-spec §7):** DIP↔px conversion happens **once** at the pump boundary using the window's post-`WM_DPICHANGED` `Scale`. Everything above the seam is in DIP. The shared transform helper (§5) never re-applies DPI — it composes node-local DIP transforms only.

---

## 4. Phase 2 — `InputDispatcher.Drain` (the orchestrator)

```csharp
public sealed class InputDispatcher          // UI thread only; asserts via ThreadGuard.AssertWriter in DEBUG
{
    readonly SceneView _scene;               // read-only view over committed prev-frame columns
    readonly HitTester _hit;
    readonly FocusEngine _focus;
    readonly GestureArena _arena;            // L2 — coordinator ABOVE the per-pointer FSMs
    readonly GestureRecognizer _gestures;    // per-pointer FSMs (arena members)
    readonly AcceleratorRegistry _accels;
    readonly CommandRouter _commands;
    readonly OverlayManager _overlays;       // L4 — light-dismiss FSM + z-stack + focus contain/restore
    readonly CursorResolver _cursor;         // L10 — topmost-wins cursor along the route → Pal.SetCursor
    readonly EdgeAutoScrollDriver _autoscroll;// L11 — shared selection-drag / drag-reorder edge driver
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
                case InputKind.PointerWheel:   DispatchPointer(in e); break;   // hit-test ONCE → route reused below
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
        _arena.OnFrameEnd(_scene.Now);        // eager-win timers, pointer-up sweep, hold/release double-tap windows
        _gestures.OnFrameEnd(_scene.Now);     // FSM long-press timers (votes feed the arena)
        _overlays.OnFrameEnd(_scene.Now);     // tooltip hover-delay timers
        // edge-autoscroll integration is phase 7 (animation), not here — it is composition-style (§12D)
    }
}
```

`DispatchPointer` computes the route once (§5.1) and threads `in route` to: the arena (§7A), cursor resolution (§12C), the overlay light-dismiss classifier (§12B), and (if a tracking gesture is active) the edge-autoscroll arming check (§12D). No subsystem re-hit-tests.

> **`Dispatch(ReadOnlySpan<InputEvent>)` realizes this `Drain` (the names + return type differ only).** The
> as-built entry point is `InputDispatcher.Dispatch(ReadOnlySpan<InputEvent>) → int` (the returned count is the
> number of events acted on, a diagnostic); it is the same single-span, drain-the-whole-frame consumer
> described here under the spec name `Drain`. The host pump is `ring.Clear() → window.PumpInto(ring) →
> dispatcher.Dispatch(ring.Drain())` once per frame.

> **Per-`PointerId` capture table (cap 10) — as-built.** The scalar capture/hover/drag fields above are realized
> as a fixed per-contact **capture slab** (`PointerSlot[]`, cap **10** concurrent contacts; mouse always id 0).
> `SlotIn(id)` loads that contact's `Down`/`DragTarget`/`ScrollDragNode`/`Pressed`/`ContextDown`/`MiddleDown` +
> pan state into the working scalars, `SlotOut()` stores them back and **recycles** a fully-idle slot so a
> finished contact frees its seat. The **11th** concurrent contact gets no slot — its events run harmlessly and
> are discarded (a hard, deterministic, zero-growth policy: no eviction of a live contact, no heap growth). This
> is the single-pointer dispatcher generalized per id, not a new arena.

> **Overlay light-dismiss lives in `Controls/OverlayHost`, not an in-dispatcher `OverlayManager` — as-built.** The
> `OverlayManager _overlays` field and the `_overlays.OnFrameEnd(...)` call above are **spec shape, not as-built**:
> the dispatcher holds no overlay registry. An `OverlayHost` (the wrap-the-app-root component) renders the open
> popups into a top-level z-stack with a full-bleed **scrim** node beneath them, and encodes the dismiss policy on
> that scrim through ordinary `BoxEl` handlers reached via the dispatcher's hit-test → click/press delegates: a
> `LightDismiss` scrim's `OnClick` closes the top overlay, a `Modal` scrim's `OnPointerDown` is a no-op that simply
> eats the press. Because the scrim is the topmost hit-test target while a popup is open, an **outside-press lands on
> the scrim** (never the content beneath) and the dismiss is **consumed** by it — no click-through (WinUI
> `CPopupRoot::OnPointerPressed` sets `Handled = didCloseAPopup`, popup.cpp:5206). This is **device-agnostic**: a
> touch tap routes through the SAME click delegate the mouse does (the single-recognizer tap path, §7B), so touch
> light-dismiss is correct with no touch-specific branch; a per-`PointerId` `PointerCancel` over the scrim (capture
> loss) is not a tap and dismisses nothing, and window-deactivation closes every `LightDismiss` overlay (the
> `WindowBlur` host hook) while `Modal` traps. Escape still routes through the dispatcher's global key-preview hook
> (§9 step 5) into the host's close-top. The §12B `OverlayManager`/`OverlayEntry`/arena-routed-dismiss design below
> is the future single-owner z-stack + arena-competing-dismiss target (Phase 3, once the arena lands); until then
> the OverlayHost scrim is the single owner of outside-press classification, and there is **no** dispatcher-side
> overlay state to keep in sync with it.

Zero managed allocation in `Dispatch`/`Drain` itself (phases 1–2 alloc budget = "freshly-captured user closures at the edge only"; architecture-spec §8). Every internal collection is a slab/arena/`stackalloc`.

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
- UIA `get_BoundingRectangle` and IME caret go *up* via `AccumulateToRoot` once for the single node of interest, then the host adds the client→screen offset (`ClientToScreen`, in `FluentGpu.Windows` Pal/, the one place pixels meet the OS).
- **`get_BoundingRectangle` uses this chain, not just a client→screen offset** (folds the §5.8 fix: a transformed/scrolled element's a11y rect must reflect ancestor transforms).

### 5.4 Hover / capture / enter-leave

- **Pointer capture:** a node that handled `PointerPressed` may call `e.Capture()`; subsequent moves/up for that `PointerId` route only to the capturing node + its ancestors (the route is rebuilt from the captured node up). `WM_POINTERCAPTURECHANGED`/`PointerCaptureLost` clears it.
- **Enter/leave diff:** keep the previous hover route; the new hover route from this frame's hit-test is diffed against it (common-ancestor split): nodes leaving get `PointerExited` (bubble), nodes entering get `PointerEntered` (tunnel). Done over the **same `stackalloc` route span** — no per-event allocation.
- **Hover cache invalidation (folds the stuck-hover fix):** the hover route is invalidated on **any** topology / z-order / overlay-push change — not just layout commits. Concretely: the dispatcher subscribes to a `_sceneEpoch` bumped by the reconciler on topology/reorder and by the overlay manager on focus-rect/KeyTips/drop-feedback push; on epoch change the cached hover route is dropped and recomputed on the next move (or synthesized immediately if the pointer is stationary, so hover follows content that moved under a still cursor).
- **`UseHover`/`UsePressed` homing (ratified).** These are **thin compositions homed in `FluentGpu.Hooks`** over this input pipeline — `UseHover` derives from the hover/enter-leave route above; `UsePressed` derives from the pressed/released state on the hit route — **not** engine primitives, not new columns, not a new PAL seam (they add nothing here beyond reading the existing input-derived state). `controls.md` consumes them.

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

## 7. Gestures (gesture arena coordinator + per-pointer FSM + from-scratch inertia integrator)

> **L2 folded into core.** The original design had a per-pointer `PointerFsm` (tap/drag/manip) with tunnel/bubble + `e.Handled` + capture. That is sufficient for a single recognizer, but **competing recognizers on different nodes of the same route cannot race with deterministic loser-rejection** — drag-in-scroll, swipe-a-row-in-a-scroller, and selection-drag-vs-scroll all pick the wrong winner. §7A adds the **gesture arena** (the Flutter `GestureArenaManager` model: *first to accept, or last to not reject, wins*) as a coordinator above the FSMs. This is **not** purely additive: it **shifts capture/`Handled` semantics to tentative-until-resolution** (§7A.5). `PointerFsm` is kept verbatim as the per-recognizer implementation; the arena owns *when* a recognizer commits.

### 7A. The gesture arena (L2 — `GestureArena`, the coordinator above `PointerFsm`)

#### 7A.1 What an arena is and when one opens

One **arena per active `PointerId`**, opened on `PointerDown`, closed on resolution or `PointerUp`-sweep. Each arena holds an ordered list of **members** — recognizers that want this pointer. Members are enrolled along the **already-computed reverse-z route** (§5.1): every route node whose `InteractionInfo.HandlerMask` advertises a gesture (`WantsPointer` + a `GestureKind` bit) contributes a member, *innermost-first* (deepest hit node's recognizers get the earliest claim — matching Flutter's child-before-parent disambiguation and WinUI's inner-control-wins intuition).

```csharp
internal enum ArenaVote : byte { Pending, Accept, Reject, EagerAccept }   // EagerAccept = "I win NOW, sweep the rest"

internal struct ArenaMember {            // SlabAllocator<ArenaMember>; arena holds a (offset,len) span of these
    public NodeHandle Node;              // the route node that owns this recognizer
    public GestureKind Kind;             // Tap | DoubleTap | RightTap | Hold | Drag | Pan | Pinch | SelectionDrag | DragReorder
    public ArenaVote   Vote;             // updated as PointerFsm advances
    public int         FsmSlot;          // index into the SlabAllocator<PointerFsm> driving this member
    public byte        Priority;         // tie-break when two EagerAccept in one frame (innermost wins; doc-order next)
}

internal struct GestureArenaState {      // SlabAllocator<GestureArenaState>, keyed by PointerId
    public uint  PointerId;
    public int   MemberOffset, MemberLen;// span into the ArenaMember slab
    public int   WinnerSlot;             // -1 until resolved
    public bool  Closed;                 // no new members after the first PointerMove past slop
    public bool  Held;                   // a member requested hold (double-tap second-down wait)
    public long  OpenedUs;
}
```

#### 7A.2 The resolution rule (first-accept / last-standing) + eager-win + pointer-up sweep

The arena resolves a winner by exactly the Flutter ruleset, reimplemented over the FSM votes:

1. **Eager-win:** the moment any member votes `EagerAccept` (e.g. a `Drag` recognizer crossed its movement slop, or a `Pinch` saw a second contact), it **immediately wins**; the arena **sweeps** all other members with a synthetic `GestureRejected` (their FSMs reset to `Idle`). This is the common case and resolves mid-stream without waiting for `PointerUp`.
2. **First-accept (no eager):** if no eager-win, the first member to vote `Accept` wins **only once the arena is closed** (no recognizer ahead of it is still `Pending`); ties broken by `Priority` (innermost, then doc-order).
3. **Last-standing:** if all-but-one member has voted `Reject`, the survivor wins by default even if still `Pending` (e.g. a lone `Tap` that never moved).
4. **Pointer-up sweep:** on `PointerUp`, if unresolved, the arena force-sweeps: the highest-priority member still `Accept`/`Pending` wins; everyone else is rejected. This is where a clean tap (no drag slop crossed, single contact) resolves to the `Tap` recognizer.
5. **Hold / release (double-tap):** a `DoubleTap` member sets `Held=true` after the first up, keeping the arena open across the inter-tap timeout; if a second down+up lands inside the window it wins, else the arena releases the hold and the single-`Tap` member wins retroactively (its `Tapped` fires deferred). The hold window timer fires in `OnFrameEnd` (§4).

```csharp
internal void ResolveStep(ref GestureArenaState a, ReadOnlySpan<ArenaMember> members)
{
    int eager = FindFirst(members, ArenaVote.EagerAccept);
    if (eager >= 0) { Sweep(ref a, members, winner: eager); return; }
    int pending = CountVote(members, ArenaVote.Pending);
    int accepts = FirstVote(members, ArenaVote.Accept);
    if (a.Closed && accepts >= 0 && NoPendingAhead(members, accepts)) { Sweep(ref a, members, winner: accepts); return; }
    int alive = members.Length - CountVote(members, ArenaVote.Reject);
    if (alive == 1 && !a.Held) Sweep(ref a, members, winner: LastAlive(members));
    // else: stay open; wait for next PointerMove vote, the up-sweep, or the hold timer
}
```

#### 7A.3 `GestureArenaTeam` — recognizers that share a win

A **team** lets sibling recognizers on the *same logical control* present a single arena entry that doesn't reject internally until the team as a whole loses (Flutter's `GestureArenaTeam`; the canonical use is **selection** — the tap, double-tap-to-word, triple-tap-to-line, and drag-to-extend recognizers of one editable/selectable region must not fight each other). A team has a **captain** (the member that votes on the team's behalf); when the team wins, the captain decides which internal recognizer actually fires based on tap count + movement.

```csharp
internal struct ArenaTeam {              // SlabAllocator<ArenaTeam>
    public int   CaptainSlot;            // the ArenaMember that votes for the team
    public int   MemberOffset, MemberLen;// internal team members (tap/dbltap/tripletap/drag-extend)
}
```

Selection-drag (L1, §12A) is wired as a team: a `SelectionDrag` member + a `Tap`/`DoubleTap`/`TripleTap` member share a captain so a caret-place tap and a select-drag don't reject each other before the slop decides which it is.

#### 7A.4 Where the arena ticks in the 13-phase loop

- **Open + enroll + first votes:** phase 2, inside `DispatchPointer`, over the `in route`.
- **Per-move re-vote + eager/last-standing resolution:** phase 2, each coalesced `PointerMove`.
- **Timer-driven resolution** (hold release, long-press promotion to `EagerAccept`): `OnFrameEnd` (still phase 2) and, when the decision needs post-layout geometry (e.g. a drag that must compare against this frame's `Bounds`), the **6.5 layout-effect** tail.
- The arena allocates **zero per-frame heap**: members/teams/states are slab-backed, the per-arena member list is a `(offset,len)` span, votes are in-place mutations.

#### 7A.5 The capture / `Handled` semantics shift (the documented contract change)

This is the honest scoping note the gap analysis demanded:

- **Capture is tentative until resolution.** `e.Capture()` during `PointerPressed` no longer *immediately* hard-locks the pointer to one node; it enrolls that node's recognizer in the arena and grants **provisional** capture. Hard capture (route locked to the winner + ancestors) is granted **only when the arena resolves a winner**. Until then, pointer-move continues to be offered to all arena members for voting.
- **`e.Handled` becomes an arena input, not the verdict.** A bubble handler setting `Handled=true` casts an `Accept` vote for its node's recognizer (and stops *routed* propagation as before), but it does **not** by itself end the arena if a higher-priority eager-win recognizer is still in play. The common single-recognizer case is unchanged in observable behavior (one recognizer, immediate accept-on-handled, immediate capture); the change only manifests when ≥2 recognizers compete.
- **Resolution can defer across frames.** Because eager-win/last-standing may not fire until a later `PointerMove` or the up-sweep, a gesture's `*Started` event can be emitted a frame or two after `PointerDown`. Recognizers therefore buffer their start point so the eventual `ManipulationStarted`/`SelectionDragStarted` reports the original down position, not the resolution-frame position.
- **Loser cleanup is deterministic:** a swept member receives a synthetic `GestureRejected` that resets its FSM to `Idle` and releases any provisional capture; no recognizer is left half-armed.

`PointerCaptureLost` (OS `WM_POINTERCAPTURECHANGED`) force-closes the arena: the current provisional winner (if any) wins by default, all others are rejected.

> **Shipped Phase-3 (as-built) — the arena landed as written, on the touch path, behind the determinism gate.** The
> §7A/§7B types above (`GestureArena`/`ArenaMember`/`ArenaVote`/`ArenaTeam`/`GestureArenaState`/`PointerFsm`/
> `VelocitySampler`) ship **unamended** (`FluentGpu.Input/GestureArena.cs`, `GestureRecognizer.cs`; one arena per active
> `PointerId` over the cap-10 contact model, innermost-first enrollment on the §5.1 route, the exact §7A.2 `ResolveStep`).
> The dispatcher wires them on the **touch** path: `EnrollTouchArena` mirrors the scalar facts (the `_dragTarget`/OnDrag
> node → `Drag`, the `CanDrag` chain → `DragReorder`, the `Scrollable` ancestor → `Pan`, the clickable → `Tap`/`DoubleTap`,
> a context/hold chain → `Hold`); `StepTouchArena` casts the axis-locked move votes; the up-sweep resolves a clean tap.
> The proven scalar machinery still **executes** the winner, so the single-recognizer common case is observably identical
> (§7A.5). Two narrow as-built points: **(1)** the §7A.5 single-recognizer fast-path is realized concretely — an eager
> OnDrag capture (Slider scrub / EditableText drag; `_dragTarget` set, **not** a `DragYieldsToPan` swipe) is resolved
> **synchronously at the enrollment edge** (its `Drag` member is `EagerAccept`-promoted and `ResolveStep` runs immediately,
> sweeping any incidental co-enrolled `Tap`/team), so capture is **hard, never tentative**, for that common case — the
> editor/slider scrub fires the same frame as the press, exactly as the mouse path did. The tentative-until-resolution
> shift therefore manifests **only** with ≥2 genuinely-competing recognizers (`Drag`-vs-`Pan` swipe-in-scroller,
> `DragReorder`-vs-`Pan`), where it is the *intended* deferral. **(2)** `DragController.YieldsToPan` is **subsumed** on the
> touch path — the arena's axis-locked `DragReorder`-vs-`Pan` vote is the single arbiter (the two-arbitration-models risk is
> gone). The whole coordinator is pinned by the **`validation.md` §12.6** gesture-arena determinism gate: an opt-in
> `GestureArenaRecorder` (Input assembly, attached via `GestureArena.Recorder` — the `Diag`/`WakeDiagnostics`
> zero-cost-when-off discipline, a `_recorder?.X()` null-guard at each arbitration point) records the ordered ledger
> (open / enroll / vote-transition / resolution-winner / sweep-order); the gate asserts a scripted multi-gesture sequence
> replays **bit-identically** across runs and that the same fling target resolves to an identical **resolution** trace
> across `dt ∈ {8.33, 16.67, 33.3} ms` (arbitration is on the event clock; the integrator is downstream of it). The
> recorder is a **test/debug seam** — the host never attaches one (zero production cost).

---

### 7B. Per-pointer FSM + from-scratch inertia integrator

`GestureRecognizer` runs a per-pointer FSM over the coalesced pointer stream; each FSM is an **arena member** (§7A) and produces `ArenaVote`s, not direct events. Gesture structs are **re-typed off `Vec2`** (Reactor uses `Windows.Foundation.Point` — gone).

```csharp
internal struct PointerFsm {            // one per (PointerId, recognizer), in a SlabAllocator<PointerFsm>
    public GesturePhase Phase;          // Idle, Pressed, Tapping, Dragging, Manipulating
    public GestureKind  Kind;           // which recognizer this FSM implements (arena member identity)
    public Vec2 Start, Last;            // Start buffered so deferred resolution reports the DOWN position (§7A.5)
    public long DownTimeUs, LastMoveUs;
    public int  TapCount;               // double/triple-tap accumulation
    public VelocitySampler Velocity;    // ring of recent (dt, dPos) for fling
    public ArenaVote Vote;              // the FSM's current vote into its arena (§7A)
}
```

Recognized gestures emit their bubble events **only after the arena declares this FSM the winner** (§7A.2): `Tapped`, `DoubleTapped`, `RightTapped`, `Holding` (long-press timer fired in `OnFrameEnd`, which also promotes the FSM's vote to `EagerAccept`), `ManipulationStarted/Delta/Completed` (translate/scale/rotate deltas), and the selection gestures `SelectionDragStarted/Delta/Completed` (§12A). A loser FSM that is swept emits nothing and resets to `Idle`. Slop-crossing for `Drag`/`Pan`/`Pinch`/`SelectionDrag` is what produces the `EagerAccept` vote.

**Inertia integrator (real work — WinUI's `ManipulationInertiaStarting` is gone):** on manipulation release, fling velocity is computed from the `VelocitySampler` window (last ~50 ms of samples, outlier-trimmed) and handed to a **friction-decay extrapolation** that runs in the **animation phase (7)**, not in dispatch. Each integrator tick **routes through the Input-owned `SetScrollOffset`/`ApplyScrollPosition` chokepoint** — it does **not** write the viewport `LocalTransform` directly. `SetScrollOffset` clamps to `[0, Content−Viewport]`, then `ApplyScrollPosition` writes the `-offset` viewport-child `LocalTransform` + `TransformDirty|PaintDirty` **and re-realizes the virtual window** (`VirtualWindowing.NeedsRealize → VirtualRangeDirty`); it **never** marks `LayoutDirty`. The fling thus inherits the *same* clamp + virtual re-realize as wheel/keyboard/`ScrollToIndex` — there is one scroll-offset writer (§12D.2). Setting `Target == Offset` per tick idles the wheel follower; same-axis wheel input cancels the fling. Decay terminates when |v| < threshold or the offset hits a clamp boundary. This is still composition-style independent animation downstream of the chokepoint: no relayout, no re-record; the batcher re-applies the cached transform (architecture-spec §7 animation).

> **Why from-scratch, not an OS manipulation engine (recorded rationale).** WinUI rides DirectManipulation /
> `InteractionTracker` / `InteractionContext` for pan-inertia; FluentGpu **rejects all of them** for the
> integrator, on two hard grounds, neither stylistic:
> 1. **They own their own clock and write offset out-of-band**, bypassing the `SetScrollOffset`/`ApplyScrollPosition`
>    chokepoint — so the **virtual re-realize is lost** (a DManip-driven fling over a 10k-item `Virtual.ListBound`
>    would scroll the transform but never fire `VirtualRangeDirty`, leaving the realized window stale). Routing
>    every tick through the one chokepoint is the *reason* virtualization stays correct under inertia.
> 2. **They have no headless presence.** The engine's gates — `HotPhaseAllocBytes == 0` over a fling, and the
>    §12.6 / `validation.md` integrator-determinism sweep (same target + timestep ⇒ bit-identical trace at
>    dt ∈ {8.33, 16.67, 33.3} ms) — require a deterministic, pure-managed integrator that runs in `FluentGpu.Engine` (Headless/Pal/ folder).
>    An OS engine cannot pass either gate. (Confined-to-`FluentGpu.Windows` Pal/ Win32 calls like `EnableMouseInPointer`
>    are fine; an OS *animation/physics* engine in the hot integrator path is the rejected dependency.)

> **Shipped Phase-1 subset (the arena lands later, §7A unchanged).** Phase 1 ships the **synchronous
> single-recognizer** path only: one pan/tap recognizer in `Dispatch` (touch-down on a `Scrollable` anchors;
> crossing the `SM_CXDRAG` slop claims the pan — kills the click candidate, routes `Pressed → PointerCancel` to
> the down chain per the WinUI contract, never a Released/click — and drives `SetScrollOffset(start − delta)`;
> below slop, down→up is the existing click/tap), plus a **Fling mode on `ScrollAnimator`** (the existing
> phase-7 `Tick` armed via the `OnScrollArmed`/`OnScrollHover` delegate seam gains a velocity-seeded
> friction-decay mode alongside its target-chase ease — the `velocity`/`Mode{TargetChase,Fling}` co-located on
> `ScrollState`). The full **`GestureArena` + per-recognizer `PointerFsm`** above (§7A, and the `PointerFsm`
> struct here) ships **unamended in Phase 3** behind the `validation.md §12.6` arena-determinism gate; the
> single recognizer is the documented narrowing of that coordinator to one member. The §7A.5
> tentative-until-resolution capture semantics shift is **Phase-3-only** — Phase 1's capture is immediate
> (single recognizer), observably identical to the mouse path.

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

**Pointer focus is cleared by an inert-background press (deliberate divergence from WinUI).** A `PointerDown` whose hit node has **no focusable self-or-ancestor** (`NearestFocusable == null`) **and** advertises **no press handlers** (`InteractionInfo.HandlerMask & AnyInteractiveMask == 0`) clears focus via `SetFocus(NodeHandle.Null)` — dropping `_focused`, clearing `Focused|FocusVisual` (the focus ring repaints away), and firing the focused node's bubbling `OnFocusChanged(false)` (edit commit + validate-on-blur + caret hide + SIP dismiss). An **interactive-but-non-focusable** hit is *not* background and KEEPS focus: the light-dismiss/modal scrim (carries `OnClick`/`OnPointerDown` while a popup is open) so a press behind a flyout never clears the field beneath; a scrollbar press (handled before the focus block); an `OnDrag`/`OnPointer`, `CanDrag`, selectable-text, hyperlink, gesture, or wheel part. On **touch/pen** there is one extra exclusion: a press that armed a content-pan candidate over a `Scrollable` viewport is a scroll-gesture start, not a background tap, so it keeps focus (the mouse path has no content-pan and treats an empty-content click as a genuine click-away). Applied at both the mouse and touch press sites. WinUI leaves focus put on a background click; FluentGpu intentionally diverges for intuitive click-away-to-blur.

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

### 8.4 Focus visual — `DrawFocusRingCmd` anchored to the clip chain

The focus visual is a **synthesized opcode**, not a real Element. The single production focus-visual opcode is **`DrawFocusRingCmd`** — a rounded, clip-chain-anchored focus **ring** that supports the Fluent dashed reveal style. Its **struct shape + raster are owned by `gpu-renderer.md` §3.6/§4.4** (the opcode-shape authority; enum registered in `scene-memory.md` §4.1 as `DrawFocusRing`); this subsystem only **emits** it. The old rectangular `DrawFocusRect(Cmd)` is a superseded debug placeholder, never the production opcode. `FocusEngine` owns a single transient **overlay layer**; on every focus move it:
1. clears the prior `DrawFocusRingCmd`,
2. emits a new `DrawFocusRingCmd` whose geometry is the focused node's `Bounds` (LOCAL) **anchored to the focused node's clip chain** — i.e. recorded *as if* it were a child of the focused node, so it clips and scrolls with that node (a focus ring on a list item disappears correctly when the item scrolls out of the viewport's clip),
3. marks the overlay layer's root `PaintDirty` so the incremental DrawList re-records it next frame (the render thread picks it up at phase 8; clean siblings memcpy unchanged).

The `DrawFocusRingCmd` shape (owned by `gpu-renderer.md` §3.6 — `OuterRect`/`InnerRect`/`Radii`/`Brush`/`Thickness`/`DashPeriod`/`Clip`) is filled here: the focused node's corners drive `Radii`, the Fluent 2px default drives `Thickness`, `DashPeriod>0` selects the dotted reveal ring, and `Clip` is the focused node's clip chain. It is recorded with `Push/PopClip` + `Push/PopTransform` matching the node's clip chain (that is what "anchored to the clip chain" means concretely). The `Brush` comes from PAL `ISystemColors` so it tracks accent / High Contrast (architecture-spec §7 theming).

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

`AcceleratorRegistry` (`SlabAllocator<AcceleratorEntry>`) is matched against the `(Vk, Mods)` with scope filtering (an accelerator scoped to a panel fires only when focus is within it). **Access keys (Alt)** push a transient **KeyTips overlay** (badges drawn via `DrawAccessKeyBadgeCmd`, the sibling opcode to `DrawFocusRingCmd`; architecture-spec §4.5). Commands are matched by `CommandSlot` → invoke `Command.Execute` (the GC edge), guarded by `CanExecute`. `UseCommand` (hook) registers/unregisters an entry on mount/unmount with a try/finally cleanup (Reactor philosophy: do not hide user bugs).

---

## 10. IME — v1 = Imm32 (TSF = v2)

**v1 = `WM_IME_*` / Imm32** (folds the re-scope): covers CJK, far simpler than TSF; lives in `FluentGpu.Windows` (Pal/ folder) behind the portable `IImeSession` seam.

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
- **Caret rect** for `SetCompositionRect` is computed via the **shared transform helper** (§5.3): the editable node's caret rect is LOCAL (from the text seam's `HitTestTextPosition→caretRect` once editing exists — architecture-spec §line 688, a v1-display-only/v2-editing boundary), projected to client DIP via `AccumulateToRoot`, then client→screen in `FluentGpu.Windows` Pal/.
- **Composition underline** is rendered as a transient overlay run (an extra `DrawGlyphRun` + an underline `FillRect`) pulling HC colors.

**TSF = v2 (a major workstream, not a footnote):** `ITextStoreACP2`/TSF requires document lock arbitration, advise sinks, ACP range mapping, and composition mutation that respects the TSF **write-lock state** — explicitly deferred. When it lands it is **`[GeneratedComInterface]` cold COM** (human-timescale), not hand-vtable. macOS uses `NSTextInputClient` behind the same `IImeSession` seam.
> **Scope of the "TSF v2" deferral (cross-ref `text.md` §16).** What is v2 is the **OS TSF COM-wrapper timeline** only — the `ITextStoreACP2` CCW that forwards OS lock/edit callbacks. The **L1/L3 buffer + selection seam is CORE, not deferred**: the editable transactional `ITextDocument`/commit-lock buffer is owned and fully designed by `text.md` §16 ("FULLY core, not v2"), and the read-side selection/`ITextRangeProvider` geometry (§11.6/§12A) lands in v1. "Editing/TSF v2" never means the buffer+selection contract is postponed.

### 10.1 SIP (software input panel / touch keyboard) trigger seam

The **same per-window text-input seam** (this doc's `IImeSession`; the as-built `IPlatformTextInput.TextInput`) also carries the **SIP** — the OS on-screen keyboard that touch needs and the mouse/IME path never used. Three members fold onto it (defaulted no-ops so a SIP-less backend ignores them):

```csharp
// added to the per-window text-input seam (alongside Enable / SetCompositionRect / IsComposing):
bool TryShowTouchKeyboard();                 // request the panel; false = no touch keyboard available (best-effort, never throws)
bool TryHideTouchKeyboard();                 // dismiss it
event Action<RectDip>? OccludedRectChanged;  // the panel's covered region in CLIENT DIP (empty rect = hidden) — drives the reflow
```

- **Windows impl (`FluentGpu.Windows` Pal/, the IME-seam neighbour):** WinRT `Windows.UI.ViewManagement.InputPane`, obtained for the engine HWND through the classic-COM bridge **`IInputPaneInterop::GetForWindow`** (the desktop analogue of `InputPane.GetForCurrentView` — there is no CoreWindow here), then **`InputPane2.TryShow`/`TryHide`** to raise/dismiss it and **`InputPane.OccludedRect` + the `Showing`/`Hiding` events** to feed `OccludedRectChanged`. Activation is `RoGetActivationFactory` over the runtime-class HSTRING; absence (a desktop with no touch keyboard, a denied request) is handled gracefully — `TryShow/Hide` return false, never throw. Per the **com-interop.md cold-path ruling** this is a **hand-vtable `calli` consume** (no `ComWrappers`); the SIP is a cold path (focus transitions, user-rate), so the typing-edge alloc rule does not bind it. All WinRT stays confined to `FluentGpu.Windows`; the portable engine only sees the seam, so the headless slice's TerraFX-free closure is preserved (the headless impl records the show/hide requests and re-fires `OccludedRectChanged` on a test cue).
- **Trigger policy (engine-side, WinUI-faithful — `InputPaneHandler.cpp`):** the SIP is **shown on (focus gained on an editable text control ∧ the focus-causing input was a TOUCH contact)** and **hidden on focus loss** to a non-editable target. The dispatcher exposes the focus-causing pointer's device class (`LastPointerKind`, tracked on every PointerDown/Move/Up); the host bridges it + the show/hide calls onto the interaction-hooks surface (`§13`), and `EditableText` calls them from its `GotFocus`/`LostFocus` edge (the same edge that arms the caret blinker + IME) — a mouse/Tab focus never raises the panel (WinUI keys it off the focus pointer's type).
- **Reflow (`OccludedRectChanged` → bring-into-view):** when the panel reports its occluded rect, the host scrolls the **focused editor's caret above the pane** — the WinUI `CInputPaneHandler::Showing` → `EnsureFocusedElementInView` bring-into-view. The dispatcher walks from the focused node to its nearest **vertical** scrollable ancestor and, if the field's bottom edge sits below the pane top, lifts that viewport's offset just enough to clear it (+ a small margin), written through the **shared scroll-write clamp chokepoint** (`§12D` `WriteScrollOffset` → `SetScrollOffset`), so it inherits the offset clamp + virtual re-realize and can never push past the content (a zero/empty rect — the `Hiding` notification — is a no-op, exactly the WinUI `Y == 0 ∧ Height == 0` short-circuit). The clamp contract is untouched; the panel reflow is the same hard-clamped offset write every other driver uses.

---

## 11. Accessibility — UIA over the retained tree (no peer control)

UIA is a **projection** of the same `SceneStore` (P7: single source of truth — UIA `Navigate` is a topology walk, not a peer tree). It lives in `FluentGpu.Windows` (Uia/ folder) behind the portable `IA11yBackend` seam.

### 11.1 COM generation ruling (FOLDED — this overrides the spec's "hand-vtable both directions")

> **The architecture-spec §5.8/§8 "hand-built vtable / `ComPtr<T>` for both directions, no ComWrappers" wording is superseded for the UIA/TSF/OLE surface by the dotnet10 §4 + hardened-v1 §4.2 ruling.** UIA is **cold/warm** COM (human-timescale, never per-frame), so its providers are authored with **`[GeneratedComInterface]`** (consume `IUIAutomation*` services) and **`[GeneratedComClass]`** (the provider CCWs the OS calls into). Hand-vtable `calli` is kept ONLY for the per-frame hot path (D3D12/DComp/Present in the render thread) and in-loop DWrite CCWs — none of which Input/A11y touches.

Rationale (dotnet10 §4): on .NET 10 dispatch is a cached native fn-ptr vtable and the RCW/CCW is allocated once per object and cached; the cost source-gen COM adds (a wrapper object + a dictionary lookup) is irrelevant at human timescale, and it deletes a large error-prone hand-written unsafe surface with correct HRESULT→exception, `[PreserveSig]`, and `[UnmanagedCallersOnly]` vtables. **No `System.Runtime.InteropServices.ComWrappers` on the hot path** (there is no hot path here). The provider classes carry `[GeneratedComClass]`; the consumed UIA core interfaces carry `[GeneratedComInterface]`; the whole hierarchy stays in `FluentGpu.Windows` Uia/ (no cross-assembly `[GeneratedComInterface]` inheritance — vtable-offset rebuild pitfall).

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
    uint _nodeGen;               // ABA guard — see §11.9
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

- **`PatternFlags` → vtable inclusion:** `A11yInfo.Patterns` (ushort bitfield) selects which pattern interfaces the `NodeProvider` exposes: Invoke / Value / Toggle / Selection / SelectionItem / RangeValue / ExpandCollapse / Scroll / **Text** / Grid / GridItem / Table. `Invoke` re-enters the same `OnClick` delegate (§6.5). The **Text** pattern is now a full document surface, not a bit — see §11.6.
- **`get_BoundingRectangle`** uses the shared ancestor-transform-chain helper (§5.3 `LocalRectToClient`) + client→screen — **not** a bare client offset.
- **Auto-name derivation:** the `AccessibilityScanner` heuristics (LabeledBy → content text → AutomationId fallback) are extracted into a **shared helper** consumed by BOTH the DEBUG `AccessibilityScanner` lint and the runtime `get_Name`. The same helper resolves `get_FullDescription` (§11.5) so name and full-description never disagree.
- **Live regions:** the **reconciler** captures the pre-write `StringId` for `A11yLiveRegion` nodes (`1<<26`) and compares post-write; on change it raises `RaiseLiveRegionChanged`. `UseAnnounce` raises the **UIA Notification event** (`UiaRaiseNotificationEvent` / `IRawElementProviderSimple3`) added to `IA11yBackend`; it fires in **phase 12** for any seq ≤ last-presented (so screen-reader announcements track what was actually shown).
- **`AutomationRole` (SHIPPED — control-type announcement for a `BoxEl`):** because a FluentGpu control is a `BoxEl`, not a nominal type, it announces its kind via a new `AutomationRole` enum (defined in **Foundation**) surfaced through `BoxEl.Role` → the **`InteractionInfo.Role`** column (a `byte`; storage shape owned by `scene-memory.md`, semantics here). Control factories set it: Button/IconButton → `Button`, ToggleButton → `ToggleButton`, Slider → `Slider`, ScrollBar → `ScrollBar`, NavigationView items → `NavigationItem`. It is the bridge the future UIA `NodeProvider` (mapping `AutomationRole` → UIA `ControlType`), the devtools inspector, and UI tests read to recover a control's type from the flat SoA tree. (Phase 0; the lookless control kit's richer `A11yInfo.ControlType`/`Patterns` story, §11.4 above, supersedes it once that kit lands.)

### 11.5 Collection relations + heading/landmark nav (L6 folded into core)

> **L6 folded.** The topology-walk projection, listening-gate, stable `GetRuntimeId`, live regions, `UseComThreading`, and the pattern set already existed. What was missing — and is now core — is (a) **collection position** (`PositionInSet`/`SizeOfSet`/`Level`), (b) **`DescribedBy`/`FullDescription`/`FlowsTo`**, (c) **heading/landmark navigation wiring**, and (d) a **virtualized-provider realization contract** so `Navigate` is not confined to the ~50 realized rows (§11.7).

**New `A11yInfo` columns (storage owned by `scene-memory.md`; SEMANTICS owned here — reference each other):**

| Field | Type | Source | UIA property |
|---|---|---|---|
| `PositionInSet` | `int` (1-based; 0 = unset) | virtualizer's realized **logical index + 1** | `UIA_PositionInSetPropertyId` |
| `SizeOfSet` | `int` (0 = unset) | virtualizer's **total item count** (the extent table's element count, `layout.md` §8) | `UIA_SizeOfSetPropertyId` |
| `Level` | `byte` (0 = unset) | tree/heading depth from the reconciler | `UIA_LevelPropertyId` |
| `DescribedBy` | `NodeHandle` (or `Handle.Null`) | `UseDescribedBy(ElementRef)` declaration | `UIA_DescribedByPropertyId` (provider array of one) |
| `FullDescription` | `StringId` | declaration or auto-derived | `UIA_FullDescriptionPropertyId` |
| `FlowsTo` | `NodeHandle` | `UseFlowsTo(ElementRef)` | `UIA_FlowsToPropertyId` |
| `HeadingLevel` | `byte` (1–6; 0 = not a heading) | `AutomationHeadingLevel` style | `UIA_HeadingLevelPropertyId` |
| `LandmarkType` | `byte` (enum: Main/Navigation/Search/Form/Custom; 0 = none) | landmark style | `UIA_LandmarkTypePropertyId` |

- **Wiring `PositionInSet`/`SizeOfSet` from the virtualizer (the "track 12 of 50,000" requirement):** the virtualizer (`layout.md` §8 owns the extent table; the hook half is `reconciler-hooks`) knows each realized row's **logical index** and the **total count**. At reconcile (phase 5), when it realizes the window of keyed children, it writes `PositionInSet = logicalIndex + 1` and `SizeOfSet = totalCount` into the row node's `A11yInfo`. This is a pure scalar write, gated by `UiaClientsAreListening` like all a11y capture (§11.2) — zero cost when no AT is attached. The numbers are the **logical** collection position, not the realized-window position, so Narrator announces "12 of 50000" even though only ~50 rows exist in the tree.
- **`DescribedBy`/`FullDescription`:** `get_DescribedBy` returns a one-element provider array resolving the stored `NodeHandle` to its `NodeProvider` (materialized lazily, §11.3); `get_FullDescription` returns the shared-helper string. Both fall through to `null` when unset.
- **Heading & landmark navigation:** Narrator's "next heading" (H) / "next landmark" (D) commands arrive as `IRawElementProviderFragment.Navigate`-adjacent **UIA find/condition** queries (`IRawElementProviderFragmentRoot` + a `PropertyCondition` on `HeadingLevel`/`LandmarkType`). The fragment-root provider answers them by a **filtered topology walk** (the same walk as §11.3, additionally filtered to nodes with `HeadingLevel != 0` / `LandmarkType != 0`), in pre-order from the current element — no separate index needed because the topology *is* the document order.

### 11.6 `ITextProvider` / `ITextRangeProvider` over the text read-side (L3 folded into core)

> **L3 folded.** Only the Text `PatternFlags` *bit* existed; there was zero `ITextRangeProvider` design. The screen reader's **only** way to read document text (Narrator read-by-line / read-by-word, caret tracking, selection announce, attribute reporting) is this surface. It is backed by the **same `TextLayoutSlot` read-side** (`text.md` §8 — `HitTestTextPosition`, `GetSelectionRects`, the cluster map) that backs on-screen selection (L1, §12A): **one read-side, two consumers**, no second text model.

A text-bearing `NodeProvider` (a node whose `A11yInfo.Patterns` has the Text bit) additionally implements `ITextProvider`/`ITextProvider2`; ranges are `[GeneratedComClass]` `TextRangeProvider` objects:

```csharp
[GeneratedComInterface, Guid("…ITextProvider…")]
internal partial interface ITextProvider {
    ITextRangeProvider get_DocumentRange();
    SupportedTextSelection get_SupportedTextSelection();        // Single (display-only v1; Multiple later)
    void GetSelection(out ITextRangeProvider[] ranges);          // current SelectionState → one range
    void GetVisibleRanges(out ITextRangeProvider[] ranges);      // clip-visible spans (uses Bounds + clip chain)
    ITextRangeProvider RangeFromChild(IRawElementProviderSimple child);  // inline-object child → its char span
    ITextRangeProvider RangeFromPoint(UiaPoint screenPt);        // screen→client→LOCAL (§5.3 inverse) → HitTestPoint
}

[GeneratedComClass]
internal sealed partial class TextRangeProvider : ITextRangeProvider {
    readonly A11yBackend _backend;
    NodeHandle _node; uint _nodeGen;          // ABA-guarded binding (§11.9)
    int _startCp, _endCp;                     // codepoint offsets into the node's text (UAX-aware, see below)

    ITextRangeProvider Clone();
    bool   Compare(ITextRangeProvider other);
    int    CompareEndpoints(TextPatternRangeEndpoint a, ITextRangeProvider o, TextPatternRangeEndpoint b);
    void   ExpandToEnclosingUnit(TextUnit unit);                 // Char|Format|Word|Line|Paragraph|Page|Document
    ITextRangeProvider FindAttribute(int attrId, object val, bool backward);
    ITextRangeProvider FindText(string text, bool backward, bool ignoreCase);
    object GetAttributeValue(int attributeId);                   // FontName/Size/Weight/Italic/Color/Culture/…
    void   GetBoundingRectangles(out double[] rects);            // GetSelectionRects → LOCAL → client→screen (§5.3)
    IRawElementProviderSimple GetEnclosingElement();             // the owning NodeProvider
    string GetText(int maxLength);                               // slice of the node's text store (edge alloc OK)
    int    Move(TextUnit unit, int count);                       // move both endpoints by N units
    int    MoveEndpointByUnit(TextPatternRangeEndpoint ep, TextUnit unit, int count);
    void   MoveEndpointByRange(TextPatternRangeEndpoint ep, ITextRangeProvider o, TextPatternRangeEndpoint oep);
    void   Select();                                             // sets SelectionState (§12A) on the node
    void   AddToSelection(); void RemoveFromSelection();         // v2 (Multiple selection)
    void   ScrollIntoView(bool alignToTop);                      // drives ScrollToIndex / scroll-into-view (6.5)
    IRawElementProviderSimple[] GetChildren();                   // inline-object children in range (seam reserved)
}
```

**How each operation maps to the read-side (no re-shape, no new geometry code):**

- **`RangeFromPoint`** — screen→client (host `ScreenToClient`) → LOCAL via the shared inverse chain (§5.3) → `text.md` `HitTestPoint(x,y)` → a zero-length range at that `textPos`.
- **`GetBoundingRectangles`** — `text.md` `GetSelectionRects(start,len, dst)` returns LOCAL visual-fragment rects (BiDi ⇒ multiple); each projected to client-DIP via `AccumulateToRoot` (§5.3) then client→screen. **Same call** on-screen selection uses (§12A) — the rect math lives in text.md exactly once.
- **`ExpandToEnclosingUnit` / `Move` / `MoveEndpointByUnit`** — Word/Line boundaries come from the `TextLayoutSlot`'s line table + the **UAX #29 grapheme/word cluster map** (`text.md` §8 cluster map; the §13 grapheme-navigation follow-up is what `TextUnit.Character` snaps to so a range never splits a grapheme). `Line` uses the slot's line `BaselineY` table; `Paragraph`/`Page`/`Document` collapse to the whole slot in v1 (single text node = one paragraph).
- **`GetAttributeValue`** — reads the run's format from the `TextLayoutSlot` runs (font/size/weight/italic/culture/foreground brush color). Mixed-attribute ranges return the UIA "mixed" sentinel.
- **`GetSelection`** — projects the node's `SelectionState` column (§12A, anchor/extent/affinity) into a single `TextRangeProvider`. **`Select()`/`ScrollIntoView()`** write back: `Select()` sets `SelectionState` (the same column the on-screen highlight reads) so AT-driven selection and pointer-driven selection are the *same* state; `ScrollIntoView` drives the layout scroll-into-view path (`layout.md` 6.5).

This is the L1↔L3 unification the gap analysis required: caret/selection geometry, attributes, and unit navigation all read one retained slot; on-screen selection and the AT range provider are two readers of one `SelectionState` + one `TextLayoutSlot`.

### 11.7 Virtualized-provider realization contract (L6(d) folded into core)

> The deeper L6 hole: `Navigate` only walks the ~50 **realized** rows, so an AT (or a `FindAll`) hits a wall at the realized window edge — "next sibling" past the window returns nothing, breaking read-all / find. Core now defines a **realization-on-navigate** contract.

- A virtualized container's `NodeProvider` is a **`IItemContainerProvider`**: `FindItemByProperty(start, propertyId, value)` and the realization-aware `Navigate`.
- When `Navigate(NextSibling)` (or `IItemContainerProvider.FindItemByProperty`) reaches the realized-window boundary, the provider does **not** return `null`. Instead it **requests realization** of the adjacent logical index from the virtualizer: it posts a `ScrollToIndex(logicalIndex)` (the same path `layout.md` §8 / `reconciler-hooks` `UseVisibleRange` expose) onto the UI thread, **waits for the next completed reconcile+layout** (the provider call is already marshaled to the UI thread under `UseComThreading`, §11.5, so it can pump one frame), then returns the now-materialized neighbor's `NodeProvider`.
- **Determinism + bound:** realization-on-navigate is **gated to AT-driven navigation only** (it never fires for on-screen rendering) and is **rate-limited** — a `FindAll` that would realize 50,000 rows is answered by realizing in **batches** with the provider yielding partial results, so an AT read-all scrolls the list rather than instantiating the whole collection. The virtualizer's recycle path (epoch/CTS/derived-column clear, `reconciler-hooks` virtualization) reclaims rows scrolled back out, so the realized-window invariant (~50 live) is preserved even under AT walk.
- **Scroll-to-realize uses the L11 path, not a new one:** the realization scroll writes `ScrollOffset` through the same contract the edge-autoscroll driver and `ScrollToIndex` use (§12D), so there is one scroll-offset writer.
- `get_PositionInSet`/`get_SizeOfSet` (§11.5) report the **logical** position even for a freshly-realized row, so the AT's "12 of 50000" is correct the instant the row materializes.

### 11.8 UIA threading — strict `UseComThreading` (folds the unsafe-read MAJOR)

**`ProviderOptions.UseComThreading`** is mandatory: ALL provider calls (read AND write) marshal to the UI thread via the HWND pump (the HWND must be STA/pumping). There is **no "read on the calling thread"** path — that was the SoA-corruption MAJOR. An OS RPC thread calling `get_Name`/`Invoke`/`Navigate` is bounced through the window message pump onto the UI thread before it dereferences any `SceneStore` column, preserving single-writer confinement (hardened-v1 §2.1). UIA mutations (events) are *posted* from phase 2/5 and delivered on the UI thread.

### 11.9 `GetRuntimeId` — stable logical identity (folds the AT-tracking fix)

`GetRuntimeId` is derived from **stable logical identity** — a keyed-path hash / persistent per-logical-node id — **not** slot+gen. Slot+gen is ABA-prone (a recycled slot would alias a different element after reconcile, breaking the AT's element-tracking across updates). Slot+gen is used **only** as the internal provider→node binding, guarded by `_nodeGen` (a stale gen invalidates the cached `NodeProvider` so a recycled slot never serves the wrong node).

### 11.10 macOS NSAccessibility boundary

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
- **As-built — inbound OS file/folder drop.** The receiving (`RegisterDropTarget`) leg ships as four nullable `InputHooks.ExternalDrag{Enter,Over,Leave}`/`ExternalDrop` delegates (the inbound twin of `OpenUri`; `Hooks/Context.cs`), host-wired in the AppHost ctor onto the `InputHooks.Current.Default` channel to `InputDispatcher.ExternalDrag*`. The Windows backend's `Win32DropTarget` (`[GeneratedComClass]` `IDropTarget` + `RegisterDragDrop`/`RevokeDragDrop`, `FluentGpu.Windows/Interop/`) invokes them on the UI thread during the OLE loop; they open an external `DragSession` on the same `DragDropContext` (`ExternalBegin`, Source = scene root so `PruneDead` keeps it live) so a `BoxEl.DropTarget` accepting `DropKinds.Files` receives Enter/Over/Leave/Drop identically to an in-app drag, payload `FileDropData`. This delegate-seam shape (not the `IDragDropBackend.RegisterDropTarget` interface above) is the as-built; the interface form remains the canonical contract for the macOS port. Gate: `e5dragdrop.ext`.

---

## 12A. Selection-drag wired through the arena (L1 read-side consumption + `SelectionState`)

> **L1's input half is folded here** (the `SelectionState` column STORAGE is owned by `scene-memory.md`; the on-screen highlight render + the read-side geometry + the editable buffer seam are owned by `text.md` §8; the `DrawSelectionRectCmd` STRUCT SHAPE + raster are owned by `gpu-renderer.md`). Input owns only: routing the **selection-drag gesture through the arena** and writing the `SelectionState` column the highlight + `ITextRangeProvider` (§11.6) both read.

`SelectionState` (column shape registered in `scene-memory.md`; semantics here + `text.md`):

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct SelectionState {            // per text/selectable node; cold until a selection exists
    public int   AnchorCp;                // where the drag started (codepoint offset)
    public int   ExtentCp;                // current moving endpoint (caret)
    public byte  Affinity;                // upstream/downstream for BiDi + line-wrap boundaries
    public byte  Granularity;             // Caret | Word | Line | Paragraph (set by tap-count)
    public ushort _pad;
}
```

- **The gesture:** a `SelectionDrag` recognizer + a `Tap`/`DoubleTap`/`TripleTap` recognizer form a **`GestureArenaTeam`** (§7A.3) on the selectable region. The team captain decides on resolution: a tap places a caret (zero-length selection at the hit `textPos`), double-tap selects the word (granularity `Word`), triple-tap the line, and a slop-crossing drag extends the selection from the anchor. The drag *delta* maps each pointer move to a `textPos` via `text.md` `HitTestPoint` and updates `ExtentCp` + `Affinity`.
- **What Input writes:** only `SelectionState` (the single-writer column for selection). It writes **nothing** to the text layout slot and does not raster. The reconciler/owning text node reads `SelectionState` and the renderer emits `DrawSelectionRectCmd` over `text.md` `GetSelectionRects` (same read-side as §11.6 `GetBoundingRectangles`).
- **Past-the-edge:** when the selection-drag pointer leaves the scroll viewport, the **edge-autoscroll driver (§12D)** arms — the selection keeps extending into newly-scrolled-in text without further pointer movement.
- **AT parity:** `ITextRangeProvider.Select()` (§11.6) writes the **same** `SelectionState`, and on-screen selection raises `RaiseAutomationEvent(Text_TextSelectionChanged)` so Narrator announces selection changes whether they originated from pointer, keyboard (Shift+arrows, a built-in §9 step 5), or AT.
- **Editable buffer seam (CORE — owned by `text.md` §16, not deferred):** the transactional commit-lock `ITextDocument` contract is `ITextStoreACP2`-shaped and lives in `text.md` §16 (designed FULLY for v1 — "not v2"); only the **OS TSF COM wrapper** that drives it is the v2 timeline (§10). Display-only v1 fills `SelectionState` but does not mutate text. Controls and the UIA Value/Text patterns wire against that stable seam now so editing is not a retrofit.

---

## 12B. Overlay / portal manager (L4 folded into core)

> **L4 folded.** The *substrate* existed — `layout.md` §10 overlay-root + containing-block + layout-boundary isolation (z by paint order), `FocusEngine._scopeStack` modal trap with focus-restore-on-pop (§8.3), and the transient-overlay-layer mechanism (§8.4). What was missing is the **composing manager**: a light-dismiss FSM, a stacked z-layer manager, tooltip hover-delay, menu arrow-key sub-scopes, and the UIA `Window`/`Menu`/`ToolTip` + `IsModal`/`IsTopmost` projection. **Anchor-relative placement-with-flip/nudge geometry is owned by `layout.md` §10 and only referenced here** (Input asks layout for a placement, layout computes the flipped/nudged rect).

### 12B.1 `OverlayManager` — the z-stack + portal

```csharp
public enum OverlayKind : byte { Popup, Flyout, Menu, ContextMenu, ToolTip, Dialog, Drawer }
public enum DismissPolicy : byte { LightDismiss, ExplicitOnly, Modal }

internal struct OverlayEntry {            // SlabAllocator<OverlayEntry>; the z-stack is an ordered span of these
    public NodeHandle Root;               // the portal'd subtree root (rendered at the overlay-root, layout.md §10)
    public NodeHandle Anchor;             // anchoring element (Handle.Null for screen-relative)
    public OverlayKind Kind;
    public DismissPolicy Dismiss;
    public byte  ZLayer;                  // stacking order; child overlays sit above their opener
    public bool  IsModal;                 // traps focus + input; dims/blocks below (UIA IsModal)
    public bool  IsTopmost;               // UIA IsTopmost
    public NodeHandle Owner;              // opener overlay (for nested menus / submenu chains)
    public NodeHandle FocusToRestore;     // captured at open; restored on close (focus contain/restore, §8.3)
    public PlacementRequest Placement;    // anchor + preferred side + flip/nudge prefs — RESOLVED BY layout.md §10
}
```

- **Portal:** the overlay's element subtree is reconciled in place (so hooks/context inherit from the logical parent) but its **layout containing block is the overlay-root** (`layout.md` §10 — absolute positioning whose containing block is the layout root). The manager registers the root with the layout overlay-root and assigns a `ZLayer`. This is exactly React-Aria's "render outside the tree" without breaking the logical hook tree.
- **Z-layer manager:** overlays render strictly above page content by paint order (`layout.md` §10), and **child overlays render above their owner** (a submenu above its menu). The z-stack is the ordered `OverlayEntry` span; closing pops it (and its descendants, §12B.3).

### 12B.2 Light-dismiss FSM (via the gesture arena)

Light-dismiss is a small FSM driven by the **arena** (§7A) and the focus/key path, not an ad-hoc outside-click handler:

```
state Open:
  on PointerDown whose hit route (§5.1) is OUTSIDE this overlay's subtree AND outside its anchor:
        → the arena enrolls a synthetic "dismiss" member at the overlay-root level; if no inside
          recognizer eager-wins, the dismiss member wins the up-sweep → Close (and the down is
          optionally swallowed or re-dispatched per platform convention)
  on Esc (built-in, §9 step 5)                         → Close (top overlay only)
  on focus leaving the overlay subtree (§8.3 scope)    → Close (LightDismiss only; Modal traps instead)
  on Anchor unmounted / scrolled fully out of clip     → Close
state Modal:
  outside PointerDown is BLOCKED (consumed, no dismiss); only ExplicitClose/Esc(if allowed) closes
```

- **Why through the arena:** an outside-press that lands on *another* interactive control must let the control's recognizer compete; routing dismiss as an arena member (lowest priority, overlay-root level) means an inside eager-win suppresses the dismiss, while a genuine outside press resolves to dismiss on the up-sweep. This is the gap analysis's "light-dismiss FSM (via the arena)" requirement made concrete.
- **ToolTip hover-delay:** a `ToolTip` overlay is armed by a hover timer (`OnFrameEnd`, §4) — open after the hover dwell threshold on the anchor, close on pointer-leave or any other input. No press needed.
- **Menu arrow-key sub-scope:** opening a `Menu`/`ContextMenu` pushes a **focus scope** (§8.3) so arrow keys navigate items (XYFocus clamped to the menu, §8.2), Enter invokes, Right opens a submenu (a child overlay), Left/Esc closes the leaf. The submenu chain is the `Owner` links in §12B.1.

### 12B.3 Focus containment + restore; stacked dismissal

- **Contain:** opening a `Modal` overlay pushes its root as a focus scope (§8.3); Tab/XYFocus candidates are clamped to it. A `LightDismiss` popup optionally contains focus (menus do; tooltips don't).
- **Restore:** `FocusToRestore` is captured at open and restored on close (reuses the `_scopeStack` restore, §8.3).
- **Stacked dismissal:** closing an overlay closes all overlays whose `Owner` chain roots at it (a submenu tree collapses with its parent menu). Esc closes only the topmost; an outside-press dismisses the whole light-dismissible stack down to (but not including) any modal below it.

### 12B.4 UIA projection (control type + modal/topmost)

The overlay root's `NodeProvider` reports the UIA control type from `OverlayKind`: `Popup`/`Flyout`→`Window` or `Pane`, `Menu`/`ContextMenu`→`Menu`, `ToolTip`→`ToolTip`, `Dialog`→`Window` (with `IsModal=true`), and `get_IsModal`/`get_IsTopmost` from the entry flags. Opening raises `StructureChanged` + (for modal) `Window_WindowOpened`; closing raises `Window_WindowClosed`. This is the §11 topology-walk projection — the overlay subtree is real Scene topology, so AT sees it without a peer tree.

### 12B.5 Hook + thread placement

`UseOverlay(OverlayKind, DismissPolicy, anchor: ElementRef, placement: PlacementRequest)` (declared phase 4, §13) returns an `OverlayApi { Open(), Close(), bool IsOpen }`. Open/close mutate the `OverlayEntry` z-stack in **phase 2** (input-driven) or via a setState→coalesce edge; placement is **resolved at phase 6** by `layout.md` §10 (flip/nudge against this frame's `Bounds`); the light-dismiss FSM ticks in **phase 2**; tooltip hover timers in `OnFrameEnd`. Zero per-frame heap — entries are slab-backed.

---

## 12C. Cursor resolution + `Pal.SetCursor` seam (L10 folded into core)

> **L10 folded.** `InteractionInfo.CursorId` existed as a column with **no resolution logic, no Pal seam, no hover arbitration**. The OS will not pick a cursor for our pixels — the engine owns I-beam (over selectable text the moment L1/§12A lands), resize, hand, busy, etc. The resolver **shares the L2 hit route** (no extra hit-test).

### 12C.1 Resolution — topmost-wins along the route

After the route is computed (§5.1) for a `PointerMove` (and the arena hasn't taken hard capture of a non-default cursor gesture), the resolver walks the route **leaf→root** and takes the **first non-`Inherit` `CursorId`** — the topmost node that asserts a cursor wins, falling through to `Arrow` if none does:

```csharp
public enum CursorId : ushort {
    Inherit = 0,  // "ask my parent" — the resolution default on a node
    Arrow, IBeam, Hand, SizeWE, SizeNS, SizeNWSE, SizeNESW, SizeAll, Cross, Wait, AppStarting, No, None,
    // app-custom cursors index a Pal-registered table above this range
}

internal CursorId ResolveCursor(ReadOnlySpan<NodeHandle> route /* leaf→root */, in GestureArenaState arena)
{
    if (arena.WinnerSlot >= 0 && OverrideCursorForGesture(arena, out var gc)) return gc; // e.g. SizeAll during a pan
    for (int i = 0; i < route.Length; i++) {           // leaf-first = topmost-wins
        var c = _scene.CursorId(route[i]);
        if (c != CursorId.Inherit) return c;
    }
    return CursorId.Arrow;
}
```

- **Gesture override:** an in-progress arena winner can override the static column cursor (a resize-drag shows `SizeWE` regardless of what's under the pointer; a panning manipulation shows `SizeAll`). This is why the resolver consults the arena state.
- **I-beam for text:** a node with selectable text (§12A) carries `CursorId.IBeam` in its `InteractionInfo.CursorId`, so the I-beam appears the instant the pointer is over selectable text — the "I-beam expected the moment L1 lands" requirement.
- **Hover-route arbitration:** because the resolver uses the **same** hover/hit route the dispatcher already maintains (§5.4), cursor and hover never disagree, and the resolver re-runs whenever the hover route is invalidated (topology/z/overlay-push epoch change, §5.4) — so the cursor updates when content moves under a stationary pointer.

### 12C.2 The `Pal.SetCursor` seam (owned by `pal-rhi.md`; referenced here)

The resolved `CursorId` is applied through a new PAL seam **owned by `pal-rhi.md`** (added to `IPlatformWindow`); Input only *consumes* it:

```csharp
// OWNED BY pal-rhi.md (IPlatformWindow); listed here as the consumed seam.
public interface IPlatformWindow {            // … existing members …
    void SetCursor(CursorId id);              // Win32: SetCursor(LoadCursor(IDC_*)); honors WM_SETCURSOR
    ushort RegisterCustomCursor(ReadOnlySpan<byte> argb, Size2 size, Vec2 hotspot);  // app cursor → CursorId range
}
```

- **Win32:** the resolver caches the last-set `CursorId` and only calls `SetCursor` on change; `WM_SETCURSOR` is handled to re-assert our cursor (otherwise the OS resets it to the class cursor on every move). DIP/px is irrelevant — cursors are system-sized.
- **Phase:** resolution + `SetCursor` happen in **phase 2** (after the route is known); it is idempotent and cheap (one comparison + a conditional P/Invoke).
- **macOS:** the same seam maps to `NSCursor.set()` / `addCursorRect`; `CursorId` is portable, the leaf swaps (`Pal.Cocoa`). Nothing above the seam sees `HCURSOR`/`NSCursor`.

---

## 12D. Shared edge-autoscroll driver (L11 folded into core)

> **L11 folded.** The inertia integrator (§7B) handles fling; **edge-autoscroll is a distinct driver with no home** until now. Both **selection-drag (§12A / L1)** and **drag-reorder (drag/drop §12 / drag past the viewport)** need a driver that scrolls the viewport while the pointer is held past an edge. Core defines **one shared driver** writing `ScrollOffset` — it is **not** rediscovered per feature.

### 12D.1 The driver

```csharp
internal struct EdgeAutoScroll {           // one active instance per tracking gesture; SlabAllocator
    public NodeHandle Viewport;            // the scroll viewport whose edge was crossed
    public Vec2  Velocity;                 // dip/sec, sign+magnitude from how far past the edge the pointer is
    public Edge  Edges;                    // which edges are engaged (flags: Left/Top/Right/Bottom)
    public long  LastTickUs;
    public byte  Reason;                   // SelectionDrag | DragReorder | AtNavRealize (§11.7)
}
```

- **Arm (phase 2):** during a tracking gesture (an arena winner of kind `SelectionDrag` or `DragReorder`, or a §11.9 realization request), `DispatchPointer` checks the pointer position against each ancestor scroll viewport's content box (found along the route, §5.1). If the pointer is within an **edge band** (a few DIP inside an edge) or outside it, the driver arms with a `Velocity` proportional to the overshoot (capped). Re-armed/refreshed each move; disarmed when the pointer returns inside or the gesture ends.
- **Integrate (phase 7):** the driver ticks in the **animation phase** alongside the inertia integrator and writes the viewport's `-ScrollOffset` `LocalTransform` — `TransformDirty` only, **never** `LayoutDirty` (identical contract to the inertia integrator, §7B; the scroll-as-transform contract is `layout.md` §6). It clamps to `ContentSize` and stops at the boundary.
- **Drives the dependent gesture forward:** after the autoscroll advances `ScrollOffset`, the tracking gesture re-evaluates against the now-scrolled content on the **next** frame's `PointerMove` (or a synthesized stationary re-eval, §5.4) so a selection keeps extending / a drag-reorder insertion line keeps tracking into the freshly-revealed rows.

### 12D.2 One scroll-offset writer; phase + thread

- **Single writer:** the edge-autoscroll driver, the inertia integrator (§7B), `ScrollToIndex`, and the §11.7 realization-on-navigate **all** write `ScrollOffset` through the same Input-owned path (`architecture-spec.md` §5.5: "Input owns `ScrollOffset`"). There is exactly one mutator of `ScrollOffset`, on the UI thread, in phase 2 (arm) / phase 7 (integrate). No contention with the render thread (transform-only, picked up by the batcher's cached-transform re-apply).
- **Zero alloc:** the active-driver set is slab-backed; arming mutates in place.
- **Termination/edge cases:** gesture end, viewport disappearing (unmount → gen check), `ContentSize` clamp, or pointer returning inside the content box all disarm. Reduced-motion does **not** disable autoscroll (it is functional, not decorative) but caps the velocity to a steady non-accelerating rate.

---

## 13. Hooks this subsystem OWNS (declared phase 4, in `FluentGpu.Hooks`)

| Hook | Returns | Wires to | Effect timing |
|---|---|---|---|
| `UseFocus(bool autoFocus=false)` | `FocusApi` (IsFocused, Focus(), Blur()) | `FocusEngine` via `IFocusSink` | `autoFocus` SetFocus at **6.5** (valid Bounds) |
| `UseElementRef()` | `ElementRef` (NodeHandle wrapper, stale-gen→null) | reconciler back-patch in phase 5 | n/a |
| `UseCommand(Command, ReadOnlySpan<DepKey> deps)` | nothing (registers) | `AcceleratorRegistry`/`CommandRouter` | register on mount / cleanup on unmount |
| `UseAccelerator(vk, mods, Command)` | nothing | `AcceleratorRegistry` slab | register/unregister |
| `UseGesture(GestureKind, handler)` | nothing | `GestureRecognizer` config (enrolls an arena member, §7A) | n/a |
| `UseAnnounce()` | `Action<StringId, AnnounceLive>` | `IA11yBackend.RaiseNotification` | fires at **12**, seq ≤ last-presented |
| `UseOverlay(OverlayKind, DismissPolicy, ElementRef anchor, PlacementRequest)` | `OverlayApi` (Open(), Close(), IsOpen) | `OverlayManager` z-stack (§12B); placement resolved by `layout.md` §10 | open/close phase **2**; placement phase **6** |
| `UsePointerCursor(CursorId)` | nothing (sets `InteractionInfo.CursorId`) | `CursorResolver` (§12C) → `Pal.SetCursor` | resolved phase **2** |
| `UseSelectable()` | `SelectionApi` (reads `SelectionState`, SelectAll(), Collapse()) | selection-drag team (§12A) + `SelectionState` column | n/a (drag in phase 2) |
| `UseDescribedBy(ElementRef)` / `UseFlowsTo(ElementRef)` | nothing | `A11yInfo.DescribedBy/FlowsTo` (§11.5) | reconcile phase **5** |

All deps are `ReadOnlySpan<DepKey>` (foundations §6.4 / reconciler-hooks §3.2) — **never** `params object[]`. Reference deps (e.g. a `Command` instance) go to the parallel managed `GcDepTable` compared by `ReferenceEquals` (the `[FieldOffset]` GC/scalar union is illegal CLR layout — reconciler-hooks §3.2). The 3-signal memo skip (`SelfTriggered || propsChanged || HasConsumedContextChanged`) governs whether these hooks re-run; `SubtreeDirty` is traversal scope only, never a skip input.

---

## 14. Zero-alloc & thread-confinement story

- **Phases 1–2 alloc budget = freshly-captured user closures at the edge only** (architecture-spec §8). `Drain`, hit-test, gesture FSM, **gesture arena**, focus nav, accelerator match, route capture, **cursor resolution**, **light-dismiss FSM**, **edge-autoscroll arming** are all slab/arena/`stackalloc`. The route is `stackalloc Span<NodeHandle>` + a parallel `stackalloc Affine2D[]` of inverses; tab buckets are arena scratch reset per nav; the FSM/accelerator/handler/**arena-member/arena-state/team/overlay-entry/edge-autoscroll** tables are slab-backed.
- **One hit-test per pointer event.** The route computed once in §5.1 is reused by the arena, cursor resolver, light-dismiss classifier, and edge-autoscroll arming — none re-hit-tests (a correctness *and* a budget property).
- **No GC ref into a pool on the hot path** (foundations §1.1): handler delegates are the GC edge, stored in `HandlerTable` columns parallel to the spine, resolved by slot, validated by generation. `ElementRef`/`AcceleratorEntry`/`OverlayEntry`/`ArenaMember`/provider bindings carry `NodeHandle` + a gen check, never a raw object ref into the slab.
- **Input owns ZERO ComPtr.** UIA/TSF/OLE COM (including the new `ITextProvider`/`ITextRangeProvider` CCWs, §11.6) is owned by `FluentGpu.Windows` Uia/ and Pal/ and is cold/warm — confined to those leaves and marshaled to the UI thread before touching Scene. The render thread owns every hot-path ComPtr; Input never crosses that seam.
- **Single-writer on SceneStore is preserved:** Input writes only its own columns — `SelectionState` (the sole selection writer, §12A), `ScrollOffset` (the sole scroll-offset writer across inertia/edge-autoscroll/ScrollToIndex/AT-realize, §12D.2), and overlay z-stack state (its own slabs). UIA reads (and `ITextRangeProvider` walks) are marshaled to the UI thread (the SceneStore's sole consumer-thread for these columns), so there is no off-thread SoA read. `ThreadGuard.AssertWriter` in `InputDispatcher`/`FocusEngine`/`OverlayManager`/`EdgeAutoScrollDriver` deterministically throws on a wrong-thread entry in asserts builds (erased from shipping AOT — production safety == CI coverage; hardened-v1 §8).
- **DrawList contribution is POD:** the only things Input emits downstream are `DrawFocusRingCmd` / `DrawSelectionRectCmd` / `DrawAccessKeyBadgeCmd` + the overlay-layer opcodes, marked `PaintDirty`; the render thread records them at phase 8 exactly like any node. No cross-thread mutable state.

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
- **UIA RPC re-entrancy:** all provider calls marshaled to the UI thread (§11.8); an `Invoke` arriving mid-frame is queued behind the pump and applied at the next phase-2 boundary, never racing the SoA.
- **Overflow route depth** (> stackalloc cap, pathological deep tree): falls back to an arena slice for the route, still zero heap.
- **Arena never resolves** (all recognizers stay `Pending` and no `PointerUp` arrives, e.g. capture lost): `PointerCaptureLost` force-closes the arena (§7A.5) — provisional winner wins, others rejected; no recognizer is left armed.
- **Competing recognizers tie on the same frame:** two simultaneous `EagerAccept` are broken by `Priority` (innermost, then doc-order, §7A.1) — fully deterministic, matches the golden-replay gate (§validation cross-ref).
- **Selection-drag past viewport with no scrollable ancestor:** edge-autoscroll finds no viewport on the route → no-op; the selection clamps at the last reachable `textPos`.
- **Overlay anchor unmounts while open:** light-dismiss `Anchor unmounted` transition closes the overlay (§12B.2); `FocusToRestore` may itself be dead (gen check) → falls back to the nearest live focus scope (§8.3 / focus-on-freed-node rule).
- **Modal overlay swallows outside press:** the down is consumed (no dismiss, no route to content below); only `ExplicitClose`/Esc(if allowed) closes — verified by a UI-test that asserts a click behind a modal does not reach the page.
- **AT realize-on-navigate storms (FindAll over 50k):** §11.7 batches realization + recycles; the realized window stays ~50 live; a malicious/huge `FindAll` scrolls rather than OOMs.
- **Cursor flicker:** the resolver caches the last-set `CursorId` and only P/Invokes `Pal.SetCursor` on change, re-asserting on `WM_SETCURSOR` (§12C.2) so the OS class cursor never wins a frame.
- **AT `Select()` vs pointer selection race:** both write the one `SelectionState` column on the UI thread (single writer, §12A); the later write wins; `Text_TextSelectionChanged` fires once per coalesced change.

---

## 16. Cross-platform (macOS) boundary

Nothing above `Hosting` recompiles (architecture-spec §9). The Input subsystem is already portable (Vec2/PointPx, `InputEvent` POD, `SceneStore` reads, the shared transform helper). The OS leaves swap:

| Seam | Windows leaf | macOS leaf | What stays portable |
|---|---|---|---|
| pump → ring | `FluentGpu.Windows` Pal/ WM_* → InputEvent | `Pal.Cocoa` NSEvent → InputEvent | `InputEventRing`, `InputEvent` schema, coalescing |
| `IImeSession` | Imm32 (v1) / TSF (v2) | `NSTextInputClient` | caret rect via shared transform; composition events in the ring |
| `IA11yBackend` | `FluentGpu.Windows` Uia/ (UIA, generated COM) | `Accessibility.NSAccessibility` | `A11yInfo` column, topology walk, auto-name helper, stable RuntimeId concept |
| `IClipboard`/`IDragDropBackend` | OLE (generated COM), UI-thread `DoDragDrop` | `NSPasteboard`/`NSDragging*` | format StringIds, UI-thread routing, drop-feedback overlay |
| `ISystemColors` (focus/HC color) | Windows HC tokens | macOS appearance colors | `DrawFocusRingCmd.Brush` source |
| `IPlatformWindow.SetCursor` (L10; owned by `pal-rhi.md`) | `SetCursor`/`LoadCursor`, `WM_SETCURSOR` | `NSCursor.set()` / `addCursorRect` | `CursorId` enum, resolver, hover-route arbitration (§12C) |
| UIA `ITextProvider`/`ITextRangeProvider` (L3) | `FluentGpu.Windows` Uia/ (generated COM) | `NSAccessibility` text marker / `AXTextMarkerRange` | the `TextLayoutSlot` read-side + `SelectionState`; range = codepoint pair (§11.6) |
| overlay manager (L4) | portable `OverlayManager`; UIA `Window`/`Menu`/`ToolTip` via `FluentGpu.Windows` Uia/ | same manager; `NSAccessibility` window/menu roles | z-stack, light-dismiss FSM, focus contain/restore, portal (§12B) |

Forbidden above the seam (architecture-spec §9): any `HWND`/`NSWindow`/`HRESULT`/`NSError`/`ComPtr`/`id<…>`/`IRawElementProvider*`/`ITextRangeProvider`/`NSAccessibility*`/`HCURSOR`/`NSCursor`/`WM_*`/`NSEvent`/`Windows.Foundation.Point`/WinRT type. The portable layer sees only `Size2/Scale/PointPx/Vec2`, opaque `NativeHandle`, POD `InputEvent`/`WindowEvent`, `CursorId`, generational handles.

---

## 17. What this subsystem OWNS (authority list)

- **Types:** `InputEvent`, `InputEventRing` schema, `InputKind`, `InputDispatcher`, `HitTester`, `HitShape`, `HitGeom`, `TransformChain` (the ONE shared transform helper), `PointerEventArgs`/`KeyEventArgs`/`CharEventArgs` (ref structs), `PointerHandler`/`KeyHandler`/`CharHandler` (by-ref delegates), `HandlerTable`, **`GestureArena`/`ArenaMember`/`ArenaVote`/`ArenaTeam`/`GestureArenaState`** (L2), `GestureRecognizer`/`PointerFsm`/`VelocitySampler` (+ inertia integrator), `FocusEngine`/`ElementRef`/`FocusApi`/`FocusNavDir`, `AcceleratorRegistry`/`AcceleratorEntry`/`CommandRouter`, **`OverlayManager`/`OverlayEntry`/`OverlayKind`/`DismissPolicy`/`OverlayApi`** (L4), **`CursorResolver`/`CursorId`** (L10), **`EdgeAutoScrollDriver`/`EdgeAutoScroll`** (L11), **`SelectionApi`** (L1 input half), `NodeProvider` (UIA CCW), **`TextRangeProvider` (`ITextProvider`/`ITextRangeProvider` CCW, L3)**, **`IItemContainerProvider` realization (L6)**, `A11yBackend`.
- **DrawList opcodes:** `DrawAccessKeyBadgeCmd` (struct shape here; encoding-framework registration in `scene-memory.md`). **`DrawFocusRingCmd`** (the production focus-visual opcode) and **`DrawSelectionRectCmd`** are owned by **`gpu-renderer.md`** (struct shape + raster) and only **emitted** here — Input authors the focus-ring geometry (anchored to the focused node's clip chain) and writes the `SelectionState` column that drives selection (L1). The rectangular `DrawFocusRect` is a superseded debug placeholder, not the production opcode.
- **NodeFlags bits (read; set by reconciler):** `HitTestVisible`, `PointerTransparent`, `WantsPointer/Key/Wheel`, `Focusable`, `IsTabStop`, `FocusScope`, `IsFocused`, `A11yPresent`, `A11yRaw`, `A11yLiveRegion`, `A11yOffscreen`.
- **SceneStore columns:** **reads** `InteractionInfo` (HitCorners/HandlerMask/CursorId/HitShape/HitGeometry/**GestureKind bits**), `FocusNav` (TabIndex/IsTabStop/XY), `A11yInfo` (Name/AutomationId/HelpText/ControlType/Patterns/**SetSize/PositionInSet/Level/DescribedBy/FullDescription/FlowsTo/HeadingLevel/LandmarkType**); **writes** `SelectionState` (sole writer, L1/§12A) and `ScrollOffset` (sole writer, §12D.2). New-column STORAGE is owned by `scene-memory.md`; SEMANTICS are owned here (§11.7/§12A).
- **PAL seams:** `IImeSession`, `IClipboard`, `IDragDropBackend`, `IA11yBackend` (now incl. `RaiseAutomationEvent`/`ITextProvider` plumbing), `IFocusSink` (the Hooks→FocusEngine bridge); **consumes** `ISystemColors` and the new **`IPlatformWindow.SetCursor`/`RegisterCustomCursor`** seam (owned by `pal-rhi.md`, L10).
- **Hooks:** `UseFocus`, `UseElementRef`, `UseCommand`, `UseAccelerator`, `UseGesture`, `UseAnnounce`, **`UseOverlay`** (L4), **`UsePointerCursor`** (L10), **`UseSelectable`** (L1), **`UseDescribedBy`/`UseFlowsTo`** (L6).

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
10. **`DrawFocusRingCmd` (owned by `gpu-renderer.md`; emitted here) anchored to the focused node's clip chain** (clips/scrolls correctly), overlay layer marked dirty on focus move.
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

---

## Implemented from the gap analysis

The following `L*` items from `core-fundamentals-gap-analysis.md` are now **fully designed into core** in this doc (no v1 deferral, no "out-of-scope"). Each row names the gap and the section(s) where it is specified.

| Gap (id) | Folded as core | Where |
|---|---|---|
| **L2 — Gesture arena / recognizer disambiguation** | `GestureArena` coordinator above `PointerFsm`: tentative capture, accept/reject/eager-win votes, pointer-up sweep, hold/release double-tap, `GestureArenaTeam`; the documented **capture/`Handled` semantics shift to tentative-until-resolution** | **§7 / §7A** (FSM = §7B); `Drain` wiring §4; posture thesis #5 |
| **L3 — UIA `ITextProvider`/`ITextRangeProvider`** | full document surface (GetSelection / RangeFromPoint / GetEnclosingElement / GetBoundingRectangles / move-by-unit / text attributes / Select / ScrollIntoView) over the **same `TextLayoutSlot` read-side** that backs on-screen selection | **§11.6**; read-side referenced to `text.md` §8 |
| **L4 — Overlay / portal manager** | `OverlayManager` z-stack + portal; **light-dismiss FSM via the arena**; focus contain/restore; tooltip hover-delay; menu arrow-key sub-scopes; UIA `Window`/`Menu`/`ToolTip` + `IsModal`/`IsTopmost`. **Placement-with-flip geometry referenced to `layout.md` §10** | **§12B**; `UseOverlay` §13 |
| **L6 — UIA collection relations + virtualized realization** | `SetSize`/`PositionInSet`/`Level` wired from the **virtualizer's index+count**; `DescribedBy`/`FullDescription`/`FlowsTo`; **virtualized-provider realization contract** (`Navigate`/`IItemContainerProvider` can cause scroll-to-realize); **heading/landmark nav** | **§11.5 / §11.7**; columns referenced to `scene-memory.md` |
| **L10 — Cursor resolution + `Pal.SetCursor`** | topmost-wins resolution along the **shared L2 hit route** + gesture override + hover-route arbitration; **`Pal.SetCursor` seam referenced to `pal-rhi.md`** | **§12C**; `UsePointerCursor` §13 |
| **L11 — Shared edge-autoscroll driver** | one `EdgeAutoScrollDriver` for selection-drag + drag-reorder (+ AT realize-on-navigate); arm in phase 2, **integrate in phase 7 writing `ScrollOffset` (TransformDirty only)**; **single scroll-offset writer** | **§12D**; phase map §2 |
| **L1 — Selection-drag (input half)** | selection-drag wired **through the arena as a `GestureArenaTeam`**; Input writes the `SelectionState` column read by both the on-screen highlight and `ITextRangeProvider`; AT/keyboard/pointer all converge on one `SelectionState` | **§12A** (read-side + highlight + buffer seam referenced to `text.md`; `DrawSelectionRectCmd` to `gpu-renderer.md`) |

**Ownership boundaries honored (authored here, referenced elsewhere):** `SelectionState`/collection-relation **column storage** → `scene-memory.md`; `DrawSelectionRectCmd` **struct shape + raster** → `gpu-renderer.md`; **placement-with-flip/nudge geometry** → `layout.md` §10; **`Pal.SetCursor` seam** → `pal-rhi.md`; **text read-side + editable buffer seam** → `text.md` §8/§13. This doc owns the gesture arena, overlay/portal manager, cursor resolution, edge-autoscroll driver, the UIA `ITextRangeProvider`/realization providers, and the semantics of the new `A11yInfo`/`SelectionState` columns.
