# FluentGpu — Subsystem Design: Reconciler + Hooks Runtime (retargeted to the SoA SceneStore, hardened-v1)

**Author scope:** ONE subsystem — the Reactor-style reconciler + hooks runtime, retargeted from patching
WinUI `FrameworkElement`s to patching **our** SoA `SceneStore` (the single retained RenderNode tree) through
an `ISceneBackend` seam, and re-fitted to the hardened-v1 threading + memo + effect-timing contracts.

This doc is the **authority** for: the `Reconciler`, `ChildReconciler` (keyed-LIS), `ComponentTable`,
`RenderContext` + the hook cell zoo, `DepKey`/`DepDeps`/`GcDepTable`, the `EffectScheduler` + effect timing
(now **bottom-up**, P9), the `ISceneBackend` operation set, `RerenderToken`/`IReRenderSink`/the coalescer,
`ContextScope` consume, error boundaries, hot-reload, the virtualization hook trio, **the priority-lane model +
the phase-3 update queue + `UseTransition`/`UseDeferredValue`/`startTransition` (P1/P2a), the `Suspense` boundary
element + reveal state machine (P2b), `UseDerived`/`UseContextSelector` (P6), `UseOptimistic`/`UseActionState`
(P7), and the type-flip-remount handle-release contract (P8)**. It **references — does not duplicate** — the shared
contracts owned elsewhere:

- **Threading / publish / quarantine / build order** → `hardened-v1-plan.md` §2, §4.1, §6 (canonical).
- **Scene / DrawList / clean-span / dirty axes** → `architecture-spec.md` §4.4/§4.5, §5.4.
- **RHI / PAL seams** → `architecture-spec.md` §4.7/§5.1.
- **Handles / allocators / StringId / ChunkedArena** → `foundations.md`.
- **.NET 10 / C#14 / COM ruling / zero-alloc patterns** → `dotnet10-csharp14-zero-alloc.md`.
- **WaveeMusic fold-ins (virtualization, image/theme hooks)** → `app-requirements-waveemusic.md` §3.2/§5.
- **NEW column STORAGE** for the lane/update-queue payload table, `SuspenseSlot`, and the `SuspenseAnchor`
  `NodeFlags` bit → `scene-memory.md` owns the storage (this doc owns the semantics; P1/P2a/P2b).
- **Source generators** for lane/transition/Suspense/`UseDerived`/`UseOptimistic` capture (the `await`-crossing
  lane carrier; the dep-span lowering) → `dsl-aot.md` (this doc owns the runtime contracts they target).
- **Phase-2 input dispatch** pushing `Lane.SyncInput`, and **cursor/arena** consumers → `input-a11y.md`.
- **Keep-stale cross-fade / `DetachedAnim` exit** visual transition for Suspense → `backdrop-effects-animation.md`.
- **New gates** (`TypeFlip_Remount_ReleasesAllRetainedHandles`, lane-batching, Suspense reveal goldens) →
  `validation.md`.

---

## 0bis. AS-BUILT (2026-06): the runtime is **signals-first** (Solid-style), implemented

> The shipped `FluentGpu.Reconciler`/`FluentGpu.Hooks` runtime was built on a **fine-grained reactive (signals)
> core** rather than the React-style re-render-from-root + lane model the rest of this doc describes. The control
> flow below (keyed diff, hook cells, effect timing, context propagation) is preserved; what changed is the
> **update mechanism**: there is no full-tree re-render and no global dirty flag. This section is the authority for
> the as-built model; the lane/`UpdateQueue` sections (P1/P2a) remain the design target for concurrency and are not
> yet wired.

- **Reactive core** (`FluentGpu.Signals`, shipped in FluentGpu.Engine's `Foundation/` folder): `Signal<T>`/`FloatSignal`
  (observable cells, auto-tracked on read), `Memo<T>` (lazy derived), `Effect`/`Computation` (re-runs on dep change),
  `ReactiveRuntime` (per-`AppHost` scheduler: `Schedule`/`Flush`/`Batch`, `FrameRequested` wakes the loop). Files:
  `src/FluentGpu.Engine/Foundation/Signals/{ReactiveCore,Signal,Effect,Memo}.cs`. AOT-clean (delegates, no reflection);
  the set→notify path is allocation-free (subscriptions are wired once at mount).
- **A component is a reactive computation.** `UseState`/`UseReducer` return a `Signal<T>` value; reading it in
  `Render()` subscribes the component's **render-effect**. A setState writes the signal → schedules ONLY that
  component's render-effect → on the next `ReactiveRuntime.Flush` (phase 3) it re-renders and reconciles **just its
  own subtree** (granular; no app-wide re-render, no global dirty bool). `ReactiveComponent.Setup()` is the
  run-once (signals-native) variant whose body is untracked, so it never re-renders — reactivity comes purely from
  bindings/`For`/`Show` inside.
- **Fine-grained bindings — one `Prop<T>` per bindable channel.** Each channel is ONE property (BoxEl
  `Transform : Prop<Affine2D>` / `Opacity` / `Fill` / `Width` / `Height`; TextEl `Text` / `Color`; ImageEl
  `Source` / `Placeholder`) accepting a static `T`, a `Func<T>` thunk, or a concrete signal (signal-direct — the
  engine effect reads `sig.Value`, the caller allocates no closure; inline lambdas wrap in `Prop.Of(...)`). The
  reconciler wires a BOUND channel into an effect **once at mount** (a fresh thunk on a re-render is ignored —
  the signals-first contract: change the signal's value, not the bind; check `bind.mount-only.stale`); the STATIC
  value is re-asserted on every reconcile **iff `!IsBound`** — the single chokepoint rule that fixed the historical
  Opacity `!= 1f` reappear bug and the Fill/TextColor bound-value clobbers by construction. A bound fire writes ONE
  scene column + marks the matching dirty axis (Transform/Paint → compositor-only; Width/Height/Text → scoped
  relayout). This is the **compositor bypass**: a high-frequency scalar (slider scrub via
  `Slider.Bind(FloatSignal)`, scroll offset) updates the exact node with **zero render/reconcile/layout** — the
  "slider tank" fix (vertical-slice check #60). The superseded dual surface spelled each bind as a parallel
  `*Bind` prop (TransformBind/OpacityBind/…) beside its static twin <!-- canon-allow: names the superseded *Bind form on purpose -->.
- **Reactive control-flow** reuses the keyed `ChildReconciler` as the *structural* engine: `ShowEl` (conditional)
  and `ForEl` (keyed list) are boundary effects that mount/remove/diff their subtree on signal change with no
  parent re-render (`src/FluentGpu.Engine/Hooks/ControlFlow.cs`, check #61).
- **Native skeleton-loading** (the SK kit, owned here) is a fourth boundary on the same machinery: `SkelRegionEl`
  (ElementTypeId 13, `src/FluentGpu.Engine/Hooks/SkeletonRegion.cs`) is a `Show`-style boundary effect that reads a
  `Loadable<T>`'s State signal (`Foundation/Signals/Loadable.cs` — Pending|Ready|Failed + Value, the per-field async
  spine) and mounts one of three branches: a shimmer DERIVED from the author's ONE real subtree by `SkeletonDeriver`
  (`Hooks/SkeletonDeriver.cs` — a pure recursive Element→shimmer-bar walk, reading declared statics via `Prop.ValueOr`),
  the real content, or the `onFailed` UI. The Pending→Ready edge reconciles shimmer→real (the proven `Flow.Show` swap —
  NO new scene column), orphan-exits the shimmer with an opacity+blur fade (`EnterExit.Blur`, owned by
  `backdrop-effects-animation.md`) while the real rows blur-reveal in (the `SoftReveal` recipes), and cancels the
  looping `SkeletonPulse` on the exit so HasTracks drops (the idle wake-stop is not defeated). `.Pending(field)` lowers
  to a leaf-grain region for incremental per-field arrival. Per-node `SkeletonMode.Off`/`SkeletonOverride` (on the base
  `Element`, owned by `dsl-aot.md`) tune derivation (checks SK.a–g). `UseAsyncResource` (RenderContext) is the fetch
  lifecycle (UsePost marshal + CTS-cancel-on-unmount), modelled on `UseImage`.
- **Context = signals.** A `ContextProviderEl` stores a `Signal<object?>` per provider node; `UseContext` resolves
  the nearest provider by walking ancestors (`SceneStore.Parent`) and subscribes — so a value change re-renders
  exactly the consumers, and a scoped re-render needs no context-stack reconstruction. Ambient contexts
  (`Viewport.Size`, `FrameDiagnostics.Current`) are host-published signals.
- **Scoped relayout** (`src/FluentGpu.Engine/Layout/LayoutInvalidator.cs`): a `LayoutDirty` worklist on `SceneStore`; each
  dirty node walks UP to the nearest **layout boundary** (fixed-size, non-flexing, clip-to-bounds, or a scroll
  viewport, or root) and only that subtree is re-solved (`FlexLayout.RunSubtree`) — the firewall from `layout.md
  §4`. Full layout only on first frame / resize / DPI / root-structural change. (A text measure-cache is a tracked
  perf follow-up; correctness is identical without it.)
- **Frame loop** (`AppHost`): pump → input → `ReactiveRuntime.Flush` (re-render dirty components + run bindings) →
  scoped relayout → record → present. A frame with only Transform/Paint binding writes does no render/reconcile/
  layout (`FrameStats.Rendered == false`).
- **Component activation lifecycle (notify-only).** `Flow.KeepAlive` (the fourth control-flow boundary,
  `Hooks/ControlFlow.cs`) parks a backgrounded page subtree mounted-but-detached; `Reconciler.SetSubtreeParked` is the
  single park/un-park chokepoint (reached on the deactivate/reactivate edges). A parked component's render-effect is
  **suspended** (`RunComponent` defers, replays once on un-park), so a backgrounded tab stops rebuilding on the signals
  it still subscribes to. On top of that, two hooks expose activation to developers: `UseIsActive() → IReadSignal<bool>`
  (reactive truth) and `UseActivation(onActivated, onDeactivated)` (transition callbacks — edge-only, never at mount or
  unmount). **Inactive = parked by KeepAlive OR window minimized / app-suspended.** The per-component half is a
  lazily-created `Signal<bool>` on `CompEntry` flipped in `SetSubtreeParked`; the app-wide half is the host-owned
  **`Activation.IsActive`** ambient (a `Signal<bool>` written on `AppHost`'s minimize/restore edge — one-shot flush on
  the minimize edge fires `onDeactivated` before the gate's early-return — AND-folded with the optional power-suspend
  gate `AppHost.SetWindowActive(bool)`). `UseActivation` is a **standalone effect** over the value-gated memo, NOT the
  render-effect (which is suspended while parked), so the notification keeps firing while parked; callbacks live in a
  stable cell and run under `Reactive.Untrack`. **Engine auto-quiesce (same chokepoint):** a `NodeFlags.Parked` marker +
  per-node `AnimEngine.SetNodeParked`/`ScrollAnimator.SetNodeParked` make the tickers skip parked tracks and exclude
  them from `HasActive` (O(1) counter), so a parked tab's looping animation / mid-fling scroll cannot defeat the idle
  wake-stop (`backdrop-effects-animation.md` owns the ticker behavior; the window-visibility source is `pal-rhi.md`'s
  `IPlatformWindow.State`). Notify-only by design (the engine can't know which work is the developer's); focus/blur and
  same-screen occlusion are not inputs. As-built: `RenderContext.cs`, `Reconciler.cs`, `Hosting/AppHost.cs`,
  `Animation/{AnimEngine,ScrollAnimator}.cs`, `Foundation/NodeFlags.cs`.

---

## 0. The one-sentence thesis

> Keep React's programming model and Reactor's diffing **control flow** verbatim, but **(a)** retarget the patch
> target `UIElement`/`FrameworkElement` → `NodeHandle` into the SoA `SceneStore` via `ISceneBackend`; **(b)** replace
> the `Dictionary<UIElement,ComponentNode>` instance-identity bookkeeping with a `ComponentSlot` SoA column indexed
> by handle; **(c)** replace the AOT-hostile `params object[]` dependency arrays with source-gen'd
> `ReadOnlySpan<DepKey>` over a 16-byte **pure-scalar** `DepKey` plus a side `GcDepTable` for reference/delegate
> deps; **(d)** run on the **UI thread** as phases 3–6.5 + 12 of the hardened 13-phase loop, and **PUBLISH** an
> immutable `SceneFrame` snapshot at the 13a seam point — effects flush *after present*, reconcile is
> **atomic-on-complete** and never publishes a half-built tree.

The hooks state machine, the effect 2-phase commit, the keyed-LIS child diff, the context shadow-stack, the
**3-signal memo skip**, the rerender re-entrancy cap, the error-fallback recovery, and the hot-reload migration are
**structurally preserved**. What changes is the *backend the diff writes into*, the *allocation discipline* of the
hook table, the *thread* it runs on, and the *publish boundary* it terminates at.

---

## 1. Module placement & the dependency seam (acyclic, portable)

```
Foundation
  ├─ Handle{u32 index,u32 gen}, NodeHandle, SlabAllocator<T>, ObjectPool<class>(cap 32), HandleTable,
  │  StringId, ChunkedArena, DepKey, DepDeps, NodeFlags, Lane (bitmask), LaneContext
Scene (refs Foundation)            ── SceneStore SoA + ISceneBackend (the seam) + SnapshotColumns/SceneFrame
                                      + SuspenseSlot column + UpdatePayloadTable slab (storage; this doc owns meaning)
Dsl   (refs Foundation)            ── Element records (incl. SuspenseElement), ElementModifiers, factories
Hooks (refs Foundation)            ── RenderContext, hook cells (incl. DerivedCell, optimistic cell),
                                      Ref<T>, Context<T>, ContextScope, GcDepTable,
                                      UseTransition/UseDeferredValue/UseDerived/UseContextSelector/UseOptimistic
Reconciler (refs Scene,Dsl,Hooks)  ── Reconciler, ChildReconciler, ComponentTable, EffectScheduler (bottom-up),
                                      virtualizer, UpdateQueue (phase-3 drain), SuspenseContext + reveal FSM
Hosting (refs everything + leaves) ── FrameLoop driver, RequestRender coalescer, IReRenderSink impl,
                                      RenderPriorityPolicy (recast as the lane EXECUTOR)
```

**Critical invariant (foundations DAG):** `Reconciler` is the **only** bridge to `SceneStore`, and it talks to it
**exclusively through `ISceneBackend`**. `Dsl` and `Hooks` have **zero** reference to `Scene`. This is what makes
the macOS/Metal swap a leaf change: the reconciler's algorithm is platform-independent; only the `ISceneBackend`
*implementation* (in `Scene`) knows SoA column layout, and even that knows nothing about D3D12/Metal (that lives
below the RHI seam, two layers down). `FluentGpu.Media`/`FluentGpu.Theme` provide hook *compositions*
(`UseImage`/`UseVirtual`/`UseDerivedBrush`) built on the primitives this subsystem owns; they reference
`Foundation` only and are themselves referenced only by `Hosting`.

```
Reactor today:  Reconciler ──Mount/Update──► WinUI FrameworkElement tree ──► XAML Composition (C++)
fluent-gpu:     Reconciler ──ISceneBackend──► SceneStore (SoA, NodeHandle) ──► Layout ──► PUBLISH(SceneFrame) ──► [render thread] record/batch/submit/present
                            (this subsystem)   (Scene subsystem)                          (hardened §2.3 seam)
```

---

## 2. `ISceneBackend` — the exact operation set (handle-in / handle-out, POD-only)

The seam is **handle-in / handle-out, POD-only, zero COM, zero GC-ref-into-pool**. Every operation the reconciler
needs to mount/update/move/remove a node maps to one method. Reactor called WinUI APIs directly
(`panel.Children.Insert`, `border.Child = x`, `fe.SetValue(...)`); we make that surface **explicit and narrow** so
it is swappable and so the reconciler stays pure. **No `ComPtr`, no `UIElement`, no `System.Object` ever crosses
this seam** — that is what keeps the reconciler render-thread-agnostic and lets the render thread own every ComPtr
(hardened §2.1).

```csharp
namespace FluentGpu.Scene;

/// The ONLY surface the Reconciler uses to mutate the retained tree.
/// All node references are NodeHandle (generational {u32 index,u32 gen}). Span params; no params object[].
public interface ISceneBackend
{
    // ── node lifecycle ───────────────────────────────────────────────
    NodeHandle CreateNode(ushort elementTypeId, VisualKind kind);
    // Recycle: bump generation (defeats ABA), return columns to free list, release PayloadRef/Brush/Clip rows.
    void DestroyNode(NodeHandle node);

    // ── topology (writes Parent/FirstChild/Next/PrevSibling/ChildCount columns) ──
    void InsertChild(NodeHandle parent, NodeHandle child, int index);
    void MoveChild  (NodeHandle parent, NodeHandle child, int newIndex);
    void RemoveChild(NodeHandle parent, NodeHandle child);          // detach only; caller Destroys
    int  ChildCount (NodeHandle parent);

    // The keyed diff borrows the whole child row as ONE span, not N ChildAt() walks (see §5.4).
    NodeChildCollection Children(NodeHandle parent);                // ref struct cursor over the sibling list

    // ── paint/layout property writes (one call per dirty column group; mask says which fields are live) ──
    void WriteVisual (NodeHandle node, in VisualProps props,  VisualMask mask);  // transform/opacity/fill/stroke/corner/clip
    void WriteLayout (NodeHandle node, in LayoutInput  input,  LayoutMask mask);  // size/margin/align/flex inputs
    void WriteText   (NodeHandle node, in TextProps    props,  TextMask  mask);   // StringId run + font/size/weight realization key
    void WritePayload(NodeHandle node, PayloadRef payload);                       // image (ImageHandle), path verts, custom
    void WriteAnim   (NodeHandle node, in AnimWrite    anim);                     // EffectAux / AnimTrack seeds (WaveeMusic §3.4)

    // ── dirty marking (writes NodeFlags[]; propagates SubtreeDirty upward — TRAVERSAL SCOPE only) ──
    void MarkDirty(NodeHandle node, NodeDirty flags);   // PaintDirty | LayoutDirty | TextDirty | TransformDirty

    // ── identity columns the diff itself needs ──
    void SetKey(NodeHandle node, StringId key);
    StringId GetKey(NodeHandle node);
    void SetComponentSlot(NodeHandle node, int slot);   // index into ComponentTable
    int  GetComponentSlot(NodeHandle node);             // -1 = not a component host
}
```

**Why exactly these:** the reconciler must (1) create/destroy nodes, (2) reorder children (prefix/suffix/LIS),
(3) write changed property groups, (4) mark dirty so layout+paint are scoped, (5) read/write the two identity
columns the diff needs (`Key`, `ComponentSlot`). That is the *complete* set. Everything richer (brush interning,
clip stacking, glyph/image realization, atlas UV resolve) is the **renderer's** job during the **record** phase on
the **render thread**, reading the same SoA columns from the published snapshot — not the reconciler's.

**Mask-based writes = the zero-alloc property diff.** The per-element source-gen'd `Diff` computes a
`VisualMask`/`LayoutMask`/`TextMask` from old-vs-new field comparison and emits exactly the changed columns. **No
COM readback**, no boxing — this is the only update path (it supersedes Reactor's `EnableBitmaskDiff` experiment,
which switched between readback and old/new comparison). `VisualProps`/`LayoutInput`/`TextProps` are `readonly
struct`s passed `in`; they mirror the SoA column tuples so `WriteVisual` is a few `Unsafe`-blitted field stores into
the columns at `node.Index` — see `dotnet10` §3 (`in`/`ref readonly` to kill defensive copies).

`NodeChildCollection` is a **ref struct** (it borrows the parent's sibling cursor + the arena snapshot span; never
escapes the diff). It exposes `Count`, `Get(i)`, `Insert(i,child)`, `Move(from,to)`, `Replace(i,child)`, **and
`RemoveAt(i)`** — `RemoveAt` is the explicit detach the keyed-middle uses when a key disappears (the reconciler then
runs cleanups + `DestroyNode`). See §5.4 for why `Children()` returns a borrowed span instead of N `ChildAt(i)`
walks (resolves the old open-question #3 — the sibling list is O(index) per probe).

---

## 3. Per-component hook storage — ordered, zero-alloc, no boxing

### 3.1 Keep the cell table; kill only the per-render array

Reactor's `RenderContext` holds `List<HookState>` of **class** cells (`ValueHookState<T>`, `EffectHookState`,
`MemoHookState<T>`, …). Each cell is a heap object allocated **once at mount** (rare, an *edge* GC-ref) — acceptable
per `foundations.md` ("GC refs only at the edge: Element/Component/closures/hook cells/ComPtr roots"). The real sin
is the **per-render `params object[] dependencies`** (`dependencies.ToArray()` in `UseEffect`/`UseMemo`), which
allocates every frame and boxes value-type deps.

**Decision:** keep the `List<HookCell>` table as-is (edge structure, one per mounted component, behind
`ComponentSlot`). **Eliminate only the per-render array** → `ReadOnlySpan<DepKey>`.

### 3.2 `DepKey` — the pure-scalar 16-byte dependency element (in `Foundation`)

> **CORRECTNESS FIX (hardened).** The original synthesis defined `DepKey` as a `[StructLayout(Explicit)]` with a
> GC-ref overlapped onto a scalar at `[FieldOffset(0)]`. **That is ILLEGAL CLR layout** — you cannot overlay an
> object reference and a non-reference field; the runtime rejects (or GC-corrupts) such a union. `DepKey` is now a
> **pure-scalar blittable struct** with NO GC ref anywhere, and reference/delegate deps live in a parallel managed
> `GcDepTable` compared by `ReferenceEquals`.

```csharp
namespace FluentGpu.Foundation;

public enum DepKind : byte { Null, I64, F64, Bool, Str, Handle, Ref } // Ref = "see GcDepTable[GcSlot]"

/// 16 bytes, blittable, unmanaged. NO object reference. Sequential layout (NOT Explicit/overlapped).
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct DepKey : IEquatable<DepKey>
{
    public readonly long    Bits;    // I64 / Bool(0|1) / Handle.PackedU64 / StringId.Value / Ref→GcSlot index
    public readonly DepKind Kind;    // 1 byte; 7 bytes padding to 16
    // (F64 is stored via BitConverter.DoubleToInt64Bits(value) in Bits — NO overlapped double field)

    public static DepKey FromInt(long v)      => new(v, DepKind.I64);
    public static DepKey FromDouble(double v)  => new(BitConverter.DoubleToInt64Bits(v), DepKind.F64);
    public static DepKey FromBool(bool v)      => new(v ? 1 : 0, DepKind.Bool);
    public static DepKey FromStr(StringId s)   => new(s.Value, DepKind.Str);
    public static DepKey FromHandle(Handle h)  => new(unchecked((long)h.Packed), DepKind.Handle);
    public static DepKey FromRef(int gcSlot)   => new(gcSlot, DepKind.Ref);  // value lives in GcDepTable

    public bool Equals(DepKey o) => Kind == o.Kind && Bits == o.Bits;   // Ref-eq is resolved by the caller via GcDepTable
}
```

Comparison is `Kind` + 8-byte `Bits` — **no `EqualityComparer<T>.Default`, no boxing, no `Equals(object)`** — strictly
cheaper than Reactor's `DepsEqual`.

> *Float note:* `F64` deps compared bitwise ⇒ `NaN != NaN` re-runs (matches "deps changed ⇒ re-run" intent;
> documented).

> **IMPLEMENTATION STATUS (v1 — shipped, `Hooks/DepKey.cs`).** The realized engine ships a deliberately NARROWER
> `DepKey` (in `FluentGpu.Hooks`, not `Foundation`): a Kind-less, pure-scalar 16-byte key (two `long`s) that packs up
> to four 4-byte scalars *by value* — `From(int)/From(int,int)/From(int,int,int,int)/From(long)/From(float)/From(bool)/
> From(NodeHandle)/From(float,int)` — compared by content. It is wired into the `UseEffect/UseLayoutEffect/UseMemo`
> overloads + the retained-motion hooks (`UseSpring/UseTransition/UseKeyframes/UseDrivenAnimation`), giving those their
> no-per-render-`object[]`, no-box fast path. NOT YET built: the `DepKind` tag, `FromStr`/`FromRef`, the `GcDepTable`
> (§3.3), the `[InlineArray(4)]` `DepDeps` overflow (§3.4), and the source-generated span lowering (§3.4) — so v1 keys
> *scalar* deps only; reference / string / delegate deps are not yet expressible as a `DepKey`. Growing to the
> tagged-span model above (the general `params object[]` killer) is a deliberate `DepKey` migration — the `DepKind`
> tag does not coexist with the current 4-scalar packing — and is tracked as **GEN-02** in
> `docs/plans/source-generators-opportunity-investigation.md` (verdict: build-later, unblock this first). The
> pure-scalar / no-GC-ref-inside-`DepKey` canon (SPEC-INDEX §2) holds in BOTH shapes; the narrow v1 is a subset that
> does not yet handle ref deps, not a violation.

### 3.3 `GcDepTable` — the side-buffer for reference & delegate deps

`UseEffect(..., someObject)` / `UseCallback(cb, dep)` cannot put a GC ref in a `DepKey`. The dep-span lowering
**parks the object in a per-context `GcDepTable`** and stores its slot index in `DepKey.Bits` with `Kind=Ref`.
Equality of a `Ref` dep = `ReferenceEquals` of the previous-render object at that slot vs the new one — identical to
React (reference identity for object/callback deps). Delegate deps are `Ref` (React parity: callbacks compare by
reference — resolves the old open-question #5).

```csharp
namespace FluentGpu.Hooks;

/// One per RenderContext. Two generations of object slots so a render can compare prev-vs-next by reference.
/// Reset per render via index reset (no realloc); arrays grow geometrically (edge, mount-time-ish).
internal sealed class GcDepTable
{
    private object?[] _prev = new object?[0];   // last render's ref deps, indexed by GcSlot
    private object?[] _cur  = new object?[8];    // this render's ref deps
    private int _curCount;

    public int Park(object? o) { EnsureCap(_curCount + 1); _cur[_curCount] = o; return _curCount++; }
    public bool RefEqualPrev(int slot, int prevSlot)            // compare this render's [slot] to last render's [prevSlot]
        => ReferenceEquals(_cur[slot], prevSlot >= 0 && prevSlot < _prev.Length ? _prev[prevSlot] : null);
    public void EndRender() { (_prev, _cur) = (_cur, _prev); _curCount = 0; Array.Clear(_cur); } // swap, clear next-cur
}
```

The hook cell stores, alongside its `DepDeps`, the **previous-render GcSlot for each `Ref` dep** so next render's
`RefEqualPrev(newSlot, prevSlot)` is exact. The swap-and-clear keeps the live objects rooted only as long as the
component is mounted (no leak), and resets in O(slots) — **never on the per-frame paint path** (it runs only when a
component actually re-renders). This side-buffer is the single legal home for the GC refs the illegal `[FieldOffset]`
union tried to smuggle into `DepKey`.

### 3.4 Source-generated dep spans (the AOT win, no `params object[]`)

> **STATUS:** this lowering is **GEN-02** (pending — gated on the §3.2 `DepKey` migration). The shipped v1 keys
> scalar deps by value through the narrow `Hooks/DepKey.cs` (see the §3.2 status note); the `stackalloc`-span
> generator + `GcDepTable` parking below is the not-yet-built extension. See
> `docs/plans/source-generators-opportunity-investigation.md`.

A Roslyn source generator (`FluentGpu.SourceGen`) rewrites hook call sites so the dependency list is built into a
**stackalloc'd span**, never a heap array:

```csharp
// Author writes (identical public API to Reactor):
ctx.UseEffect(() => Subscribe(id), count, isOpen, label, model);

// Generator lowers to (conceptually):
Span<DepKey> __deps = stackalloc DepKey[4];
__deps[0] = DepKey.FromInt(count);
__deps[1] = DepKey.FromBool(isOpen);
__deps[2] = DepKey.FromStr(__intern(label));
__deps[3] = DepKey.FromRef(ctx.GcDeps.Park(model));     // reference dep → side table
ctx.UseEffect(() => Subscribe(id), __deps);             // ReadOnlySpan<DepKey> overload
```

The canonical engine signatures (`dotnet10` §3 confirms the lambda `in`/`scoped` modifier capture):

```csharp
public void UseEffect      (Action effect,                 ReadOnlySpan<DepKey> deps);
public void UseEffect      (Func<Action> effectWithCleanup,ReadOnlySpan<DepKey> deps);
public void UseLayoutEffect(Action effect,                 ReadOnlySpan<DepKey> deps);  // phase 6.5
public void UseLayoutEffect(Func<Action> effectWithCleanup,ReadOnlySpan<DepKey> deps);
public T    UseMemo<T>     (Func<T> factory,               ReadOnlySpan<DepKey> deps);
public TCb  UseCallback<TCb>(TCb cb,                       ReadOnlySpan<DepKey> deps) where TCb : Delegate;

// Concurrent-era hooks folded into core from the gap analysis (full design in the cited sections):
public (bool IsPending, Action<Action> Start) UseTransition();                                   // §7.1.1 (P1)
public T    UseDeferredValue<T>(T value)             where T : IEquatable<T>;                     // §7.1.1 (P1)
public TOut UseDerived<TOut>(Func<TOut> project, ReadOnlySpan<DepKey> deps)
                                                     where TOut : IEquatable<TOut>;               // §8.5 (P6)
public TSel UseContextSelector<T,TSel>(Context<T> ctx, Func<T,TSel> select)
                                                     where TSel : IEquatable<TSel>;               // §8.5 (P6)
public (T Value, Action<T> ApplyOptimistic) UseOptimistic<T>(T baseValue)
                                                     where T : IEquatable<T>;                     // §9.6 (P7)
public (TState State, Func<TArg,Task> Dispatch, bool IsPending)
       UseActionState<TState,TArg>(Func<TState,TArg,Task<TState>> action, TState initial);        // §9.6 (P7)
// Static (not on RenderContext): startTransition runs cb now, tagging its setStates Lane.Transition (§7.1.1).
public static void StartTransition(Action cb);                                                    // (P1)
```

The public `params object[]` overload is retained as a thin `[Obsolete("allocates; prefer span overload", false)]`
shim for source-compat / dynamically-built deps; the generator never emits it.

**Storing prev deps across renders** — inline, no array:

```csharp
[System.Runtime.CompilerServices.InlineArray(4)]
internal struct DepInline4 { private DepKey _e0; }   // 64 bytes inline; dotnet10 §2 InlineArray rule

internal struct DepDeps
{
    public byte       Count;
    public bool       HasDeps;          // false == "no deps passed" (always-run); mirrors Reactor null check
    public DepInline4 Inline;           // used when Count <= 4 (≥95% of hooks → zero heap)
    public DepKey[]?  Overflow;         // Count > 4 (rare; rented from ArrayPool, NOT cap-32 ObjectPool)

    public bool Equals(ReadOnlySpan<DepKey> next, GcDepTable gc, ReadOnlySpan<int> prevRefSlots);
    public void CopyFrom(ReadOnlySpan<DepKey> src, ReadOnlySpan<int> refSlots);
}
```

`DepsEqual` = length check + per-element `DepKey.Equals`, with `Ref` elements resolved through
`gc.RefEqualPrev(...)`. No allocation, no boxing.

### 3.5 The hook table lives behind `ComponentSlot`, not behind a node GC-ref

Reactor keyed everything off `Dictionary<UIElement, ComponentNode>` (GC-ref → GC-ref). `foundations.md` forbids
GC-refs-into-pools on hot paths and gives us a `ComponentSlot` SoA column, so:

```csharp
namespace FluentGpu.Reconciler;

// Replaces Reactor's Dictionary<UIElement, ComponentNode>. The int slot is stored in the SceneStore
// 'ComponentSlot' column at the host node. Hot reads are a column read; cold edge refs live in side arrays.
internal sealed class ComponentTable
{
    private readonly SlabAllocator<ComponentSlotData> _slab;    // gen-versioned, intrusive free list (unmanaged)
    private Component?[]    _components;      // class-component instance (null for func components)  — edge GC
    private RenderContext[] _contexts;        // hook table owner — ALWAYS present (func + class)     — edge GC
    private Element?[]      _renderedElement;  // last Render() output (for child diff)                — edge GC
    private Element?[]      _element;          // the Component/Func/Memo element that minted this     — edge GC
}

internal struct ComponentSlotData    // the SoA / unmanaged part (NO GC refs)
{
    public NodeHandle HostNode;      // the ComponentAnchor node this component renders into
    public DepDeps    MemoDeps;      // MemoElement deps (boxless)
    public ComponentKind Kind;       // Class | Func | Memo
    public int        SelfTriggered; // 0/1; Volatile.Read/Write helpers (off-thread setState marks it)
    public uint       Generation;    // matches HostNode.gen for stale-slot detection
}
```

`RenderContext` stays a GC class (it owns `List<HookCell>`, delegates, `Ref<T>`, the `GcDepTable` — all inherently
GC). One allocation per mounted component — an *edge* object, exactly the category `foundations.md` permits. The hot
per-frame path touches SoA columns + inline `DepDeps`; it never allocates. **Node → component is O(1):**
`backend.GetComponentSlot(hostNode)`; `-1` = plain visual.

`ObjectPool<RenderContext>` (cap 32) recycles contexts on unmount per `foundations.md` (edge pool only); the
intrusive slab free list recycles `ComponentSlotData`.

---

## 4. Effect scheduling vs the 13-phase frame lifecycle (RATIFIED timing)

Hardened phase order with the thread map (`hardened-v1-plan.md` §2.2 is canonical; reproduced for the phases this
subsystem touches):

```
1 pump (UI) → 2 input-dispatch (UI) → 3 hook-flush (UI) → 4 render (UI) → 5 reconcile (UI) →
6 layout (UI) → 6.5 layout-effects (UI) → 7 animation (UI) → PUBLISH 13a SceneFrame (UI) →
8 record (RENDER) → 9 batch (RENDER) → 10 submit (RENDER) → 11 present (RENDER) → 12 passive-effects (UI, next turn)
```

Reactor flushed effects inline at the end of each component's `ReconcileComponent` (before layout/paint) — wrong
timing, and impossible across a publish seam. We **must not** flush inside reconcile; effects are scheduled into a
frame-global queue during reconcile and drained at the ratified phase boundaries.

### 4.1 Two effect timings — RATIFIED (resolves the old open-question #1)

| Timing | Public hook | Runs at phase / thread | Use for | Can read |
|---|---|---|---|---|
| **Layout effect** | `UseLayoutEffect` | **6.5 — UI thread**, after layout, before animation/publish | measure-dependent reads, focus, scroll-into-view, `ScrollToIndex`, `UseVisibleRange` | `Bounds[]` (arrange output) valid |
| **Passive effect** | `UseEffect` | **12 — UI thread**, after present (for any seq ≤ last-presented) | subscriptions, timers, data fetch, page-fetch dispatch | everything; visible frame already on screen |

Both go through the **same 2-phase commit** (cleanups first, then new effects). Default `UseEffect = Passive`
(matches Reactor + React). Phase 6.5 is **RATIFIED** by `architecture-spec.md` §4.8 and `hardened-v1-plan.md` §2.2 —
it is no longer an amendment request.

### 4.2 setState in an effect ⇒ frame N+1, NO synchronous re-loop (RATIFIED)

> **POLICY (hardened, supersedes the original synthesis §4.3 "bounded re-loop of phases 4–6 once").** v1 has **NO
> synchronous bounded re-loop**. A `setState` from *either* a layout effect (6.5) *or* a passive effect (12) simply
> **marks the owning component dirty and requests frame N+1** through the coalescer. There is no inner re-run of
> phases 4–6 within the same frame. This is the `architecture-spec.md` §4.8 / `app-requirements` §5 ruling and it
> wins over the earlier "React does a sync layout-effect pass" framing. (Rationale: a synchronous re-loop re-opens
> the publish seam mid-frame, fights the atomic-on-complete reconcile, and risks layout-thrash; deferring to N+1 is
> deterministic and seam-clean. The ≤1-frame latency is acceptable for measure-driven adjustments and matches the
> rest of the loop's N+1 discipline.)

### 4.3 The effect queue — global, ordered BOTTOM-UP, drained at the two phase boundaries

> **RATIFIED (folds P9 — intra-phase effect ordering is child-before-parent).** React commits effects
> **bottom-up** (a node's effects run only after all its descendants' effects have run) so a parent effect
> never reads a child handle/ref that a child layout-effect has not yet attached, and a parent cleanup never
> tears down a resource a child cleanup still depends on. We adopt the same guarantee **explicitly**: each
> timing queue is drained in **depth order descending** (deepest-first = bottom-up). This is folded into core
> rather than "documented as descent-order-safe", because once GPU/native handles (atlas pins, `DrivenClock`
> seeds, `NodeHandle` refs back-patched by `UseElementRef`) live inside effect bodies, descent (parents-first)
> order is *not* safe — a parent layout-effect that calls `ScrollToIndex` on a child must run after the child's
> own layout-effect has registered its extent.

```csharp
internal sealed class EffectScheduler
{
    private readonly List<EffectRef> _layout  = new();   // drained at phase 6.5 (UI), BOTTOM-UP
    private readonly List<EffectRef> _passive = new();   // drained at phase 12  (UI), BOTTOM-UP

    // Depth is the host node's tree depth, captured at enqueue (reconcile walks top-down, so depth is known
    // for free — it is the current descent depth). Stored in the EffectRef; no extra tree walk.
    public void Enqueue(RenderContext ctx, int cell, EffectTiming t, int depth, uint slotGen)
        => (t == EffectTiming.Layout ? _layout : _passive).Add(new EffectRef(ctx, cell, depth, slotGen));

    public void FlushLayout()  => Drain(_layout);
    public void FlushPassive() => Drain(_passive);

    private static void Drain(List<EffectRef> q)
    {
        // Bottom-up: a stable descending-depth sort. Insertion order is preserved within a depth band so
        // sibling effects keep document order (React parity). The sort is over a small per-frame list of
        // *changed-dep* effects only (not all mounted effects) — O(k log k), k = effects firing this frame.
        SortByDepthDescStable(q);
        for (int i = 0; i < q.Count; i++)                       // PHASE A: cleanups (deepest child first)
            if (q[i].IsLive) q[i].Ctx.RunPendingCleanup(q[i].Cell);
        for (int i = 0; i < q.Count; i++)                       // PHASE B: new effects (deepest child first)
            if (q[i].IsLive) q[i].Ctx.RunPendingEffect (q[i].Cell);
        q.Clear();   // reuse list next frame — zero realloc
    }
}
internal readonly record struct EffectRef(RenderContext Ctx, int Cell, int Depth, uint SlotGen)
{
    // Gen-stamp gate (P5 prereq, carried here): an EffectRef enqueued in a partial/earlier reconcile pass is
    // skipped if its owning ComponentSlot generation advanced (component remounted/recycled) before drain.
    public bool IsLive => Ctx.Slot.Generation == SlotGen;
}
```

`RenderContext.UseEffect`/`UseLayoutEffect` no longer *run* anything; on dep change they set `cell.Pending = true`,
stage `PendingCleanup = cell.Cleanup`, and `Scheduler.Enqueue(ctx, index, cell.Timing, currentDescentDepth, slot.Generation)`.
This is the minimal change from Reactor's `UseEffect` (same `Pending`/`PendingCleanup` fields) — we route the *run*
to the frame scheduler and stamp it with depth + slot generation. Each `RunPendingCleanup`/`RunPendingEffect` is
wrapped per-cell in try/catch-log so one cell's throw cannot abort the drain (matches `architecture-spec.md` §7
"per-cell try/catch isolates effect-body exceptions").

**Why the sort is cheap, and why it is correct under carry-forward.** Only effects whose deps *changed this
frame* are enqueued (Reactor's `Pending` flag), so the drain list is the firing set, not the mounted set; the
descending-depth sort is O(k log k) over that small set. The **`SlotGen` gate** (folded from P5's gen-stamp
contract) makes bottom-up drain safe across a carry-forward `ReconcileSlicer` boundary: if a component that
enqueued an effect in frame N's partial pass re-renders (or unmounts) in N+1's completion, its slot generation
no longer matches and the stale `EffectRef` is skipped — the effect that *actually* survived re-enqueues with the
current generation. Drain only ever runs after a **complete** reconcile (atomic-on-complete, §5); a partial pass
never drains. This is the concrete contract `core-fundamentals-gap-analysis.md` P5 asked `threading-render-seam`
to spell out, stated from the reconciler side.

### 4.4 Mount / unmount ordering of effects

- **Mount:** new effect cells are `Pending=true`, `PendingCleanup=null` → only the effect body runs at the timing's
  phase.
- **Unmount:** `RunCleanups()` (verbatim from Reactor — drains both `PendingCleanup` and `Cleanup`, null-guarded so
  it can't double-run) is invoked **synchronously during reconcile remove** (phase 5), NOT deferred to phase 12.
  Rationale: a removed node's subscriptions must be torn down before its handle is recycled, or a late callback fires
  into a destroyed slot. Persisted-state save-on-unmount (`PersistedHookStateBase.SaveToCache`) also happens here.
  This synchronous-cleanup contract is load-bearing for WaveeMusic's `UseImplicitTransition`/`UseConnectedAnimation`
  (`app-requirements` §3.4): the animation must move its paint columns + image pin to a `DetachedAnim` slab *because*
  cleanups already ran and the topology slot is about to be recycled.

---

## 5. The reconcile algorithm against `NodeHandle` (mount / update / move / remove)

Reconcile runs on the **UI thread** as phase 5 and is **time-sliceable but atomic-on-complete by default**
(`hardened-v1-plan.md` §4.1): a `ReconcileSlicer` may yield against a deadline, but the partially-built tree is
**never published** — PUBLISH (13a) only fires after a complete reconcile+layout. Off-thread reconcile is opt-in /
spike-gated / MANAGED, out of v1 scope here.

Reactor's reconcile is preserved as the **two sub-phases** the architecture-spec frame walk names (§5 step 5):
a **structural sub-phase** (growth allowed: keyed-LIS diff vs prev-frame `Element`, may `CreateNode`) then an
**edit sub-phase** (growth locked: per-element mask `Diff` → `backend.WriteX` + `MarkDirty`).

### 5.1 Top-level entry (mirrors Reactor `Reconcile` orchestration)

```csharp
public NodeHandle Reconcile(Element? oldEl, Element newEl, NodeHandle existing, RerenderToken rr)
{
    // 1. null/empty → unmount existing (synchronous cleanups), return Nil
    // 2. type changed OR key changed → Mount(newEl); Unmount(existing)
    // 3. composition wrappers (Component/Func/Memo/Group/ErrorBoundary/Modified) → dispatch
    // 4. plain visual: if CanSkipUpdate(old,new) → refresh callback trampoline only, return existing
    //    else Update(old,new,existing) via source-gen'd per-element Diff (mask → backend.WriteX)
}
```

`CanSkipUpdate` / `ShallowEquals` / `HasCallbacks` (from `Element.cs`) are **reused verbatim** — pure functions over
immutable records once the per-type arms are regenerated by the source generator for our element set. The skip
fast-path is the single biggest perf lever and survives unchanged.

### 5.2 `Mount` — create node, write all columns, recurse

```
Mount(el, parentNode, index, rr):
    node = backend.CreateNode(el.ElementTypeId, VisualKindOf(el))
    if el.Key != null: backend.SetKey(node, intern(el.Key))
    source-gen MountWriter(el) → backend.WriteVisual/Layout/Text/Payload (ALL masks set)
    backend.InsertChild(parentNode, node, index)
    backend.MarkDirty(node, LayoutDirty | PaintDirty)        // new node always dirties layout+paint
    for each child: Mount(child, node, i, rr)
    run el.Modifiers OnMount side-effects (edge)
    return node
```

For a **component** element, `Mount` also allocates a `ComponentTable` slot, constructs the `RenderContext`, calls
`Render()` once, and reconciles the *rendered* element into the host node. The host node is a
`VisualKind.ComponentAnchor` node — a zero-cost passthrough carrying the `ComponentSlot`, giving the component stable
handle identity across re-renders even when its rendered root changes type (this preserves Reactor's
"Border-as-identity-anchor" trick without a real container).

### 5.3 `Update` — bitmask diff, no readback (the edit sub-phase)

```
Update(oldEl, newEl, node, rr):
    mask = SourceGenDiff(oldEl, newEl)        // per-element generated: field compares, each OR-ing a bit
    if mask.Visual != 0: backend.WriteVisual(node, BuildVisual(newEl), mask.Visual)
    if mask.Layout != 0: backend.WriteLayout(node, BuildLayout(newEl), mask.Layout)
    if mask.Text   != 0: backend.WriteText (node, BuildText(newEl),   mask.Text)
    if mask.Payload:     backend.WritePayload(node, BuildPayload(newEl))
    if mask.AnyPaint:    backend.MarkDirty(node, PaintDirty)
    if mask.AnyLayout:   backend.MarkDirty(node, LayoutDirty)    // propagates SubtreeDirty upward (scope only)
    refresh callback trampoline if newEl.HasCallbacks
    recurse children via ChildReconciler (§5.4)
```

The generator emits, per element type, `Diff(in TElement a, in TElement b) → DiffMask` — a flat, branch-predictable
sequence of field compares, **no COM readback**. This is what Reactor's `EnableBitmaskDiff` experiment reached
toward, now the only path.

### 5.4 Keyed child diff — `ChildReconciler` reused, retargeted to `NodeChildCollection`

`ChildReconciler.cs` is **reused verbatim in algorithm**: 4-phase (common prefix → common suffix → pure
insert/remove middle → keyed-LIS minimal moves), `ComputeLIS` patience-sort O(n log n), keyed/unkeyed split. The only
change is the collection it drives — a `NodeChildCollection` over a parent `NodeHandle` + `ISceneBackend`:

```csharp
// Reactor: PanelChildCollection wraps WinUI Panel.Children (COM IVector).
// fluent-gpu: NodeChildCollection is a ref struct over a borrowed sibling-span snapshot + the backend.
internal ref struct NodeChildCollection
{
    private readonly ISceneBackend _b;
    private readonly NodeHandle    _parent;
    private readonly Span<NodeHandle> _snapshot;       // borrowed from arena: the parent's children, O(n) walk ONCE
    public int Count => _snapshot.Length;              // O(1) on the snapshot
    public NodeHandle Get(int i) => _snapshot[i];      // O(1) — NOT a sibling-list re-walk
    public void Insert(int i, NodeHandle child) => _b.InsertChild(_parent, child, i);
    public void Move(int from, int to)          => _b.MoveChild(_parent, _snapshot[from], to);
    public void RemoveAt(int i)                 => _b.RemoveChild(_parent, _snapshot[i]);   // detach; caller Destroys
    public void Replace(int i, NodeHandle child){ _b.RemoveChild(_parent, _snapshot[i]); _b.InsertChild(_parent, child, i); }
}
```

> **RESOLVES old open-question #3 (`ChildAt` O(index²)).** The SoA topology is a `Next`-sibling linked list, so N
> `ChildAt(parent,i)` probes are O(n²). The diff now borrows the whole child row **once** as a `Span<NodeHandle>`
> from the per-frame `ChunkedArena` scratch (the `Children()` backend op walks the sibling cursor a single time).
> All `Get(i)` are O(1) over that span; mutations (`Insert`/`Move`/`RemoveAt`) write the real topology columns. The
> SoA store stays pure (no per-parent `ChildHandle[]` materialization required). This is the "(b)" option the
> original doc defaulted to, now the committed design.

Everything else in `ChildReconciler` — `Filter`, `HasAnyKeys`, `KeyMatch`, `GetKey` (positional fallback), the LIS
backtrack — is copied **byte-for-byte**.

**Key extraction change:** Reactor read keys via an attached DP (`Reconciler.GetElementTag`, a WinUI duplicate-RCW
workaround). We read `backend.GetKey(handle)` — the `Key` SoA column — which is cheaper *and* removes the entire
`ReactorState` attached-DP machinery (a `NodeHandle` is its own identity; no RCW duplication exists in our model).

**Allocations in the keyed middle, re-authored on arena scratch:** Reactor's `ReconcileKeyedMiddle` allocates
`Dictionary<string,int>`, `int[] newToOld`, `bool[] matched`, `HashSet<int> inLIS`, `keyToIndex` per reorder. These
are **per-reorder, not per-frame**, and bounded by list size. We **re-author the keyed-LIS scratch on the per-frame
`ChunkedArena`**: `newToOld`/`matched`/`inLIS` are `Span<int>`/`Span<bool>` carved from the arena; the key→index map
is a **pooled `Dictionary<int,int>` reset** (not realloc'd). Keys are `StringId` ints (not `string`), so map probes
are int-equality, no string hashing on the diff path; the positional fallback key is a packed int, not
`string.Format`. The arena resets O(1) at phase 13; chunk growth is gated by the **native high-water counter**, not
the GC tripwire (`foundations.md` ChunkedArena contract).

### 5.5 Move / remove semantics on the store

- **Move:** `backend.MoveChild` rewrites the sibling links (`Next`/`PrevSibling`) + parent `FirstChild`/`ChildCount`
  — pure index pointer surgery in the topology columns; the node's handle, all other columns, and its whole subtree
  are untouched. A reorder is O(1) pointer writes, no realization/re-parenting cost (the win over WinUI).
- **Remove:** `backend.RemoveChild` detaches; the reconciler then runs the node's component cleanups
  (`RunCleanups`, depth-first if a host) **synchronously**, then `backend.DestroyNode` which bumps generation and
  frees columns + releases `BrushHandle`/`ClipHandle`/`ImageHandle`/`PayloadRef` back to their tables. The gen bump
  means any stale `NodeHandle` still held (e.g. a captured effect closure) **fails the gen check** on next use —
  the ABA-safe dangling-handle defense from `foundations.md`. **Quarantine note (hardened §2.3):** a node slot freed
  during production of frame `p` is *not* reusable until `_lastConsumedSeq > p`; the reconciler defers slot reuse to
  the consume-gated quarantine ledger, not to the same frame. (`QUARANTINE = RenderInFlightDepth + 1`
  (belt-and-suspenders, compile-asserted); =0 in single-thread build order step 1, flipped only behind the
  green race gate.)

### 5.6 Type-flip remount RELEASES retained handles — a named, tested contract (folds P8)

> **CONTRACT (folds P8 — general `A→B` type-flip is the same release path as virtualization recycle).** A
> draw-everything engine has no native control to GC its native resources; when a node is unmounted (type flip
> `A→B` at the same position, key change, or conditional removal), the reconciler must **explicitly release every
> retained GPU/OS handle the node pinned**, not just detach topology. The virtualization recycle path (§11) is
> already specified to do this; P8 promotes the **general** case to the same named, tested contract.

`DestroyNode` (the structural-sub-phase removal terminator) is the single chokepoint that releases, in this exact
order, **synchronously during reconcile phase 5, after `RunCleanups()` has run depth-first**:

1. **Image / palette pins** — any `ImageHandle` in the node's `PayloadRef`/`NodePaint`, plus the in-flight
   image-request epoch (a `UseRef` cell, §11) is invalidated so a late decode `Post` drops on the floor.
2. **Realization handles** — `GlyphRunHandle`/`PathHandle`/`BrushHandle`/`ClipHandle` rows are released back to
   their interning tables (`scene-memory.md` §2.5 owns the slabs; refcount/epoch decisions are theirs).
3. **Present-tree / DComp slab** — if the node owned a `NeedsLayer` routing slot or a DComp visual slab
   (`EffectAuxRef`/`BackdropRef`), its release is **deferred-freed behind the same consume-gated quarantine** as
   the slot (`scene-memory.md` §5.3 `Detached`+deferred-free; `gpu-renderer.md`/`backdrop-effects-animation.md`
   own the actual GPU-side free ring) — the render thread may still be reading it from an in-flight `SceneFrame`.
4. **`DrivenClock` / `AnimTrack` seeds** — any animation track the node drove is stopped and its `EffectAux` row
   released; **exception:** if the node is in an exit transition (`UseImplicitTransition`/`UseConnectedAnimation`),
   §4.4 has already moved the paint columns + image pin to a `DetachedAnim` slab *before* `DestroyNode`, so the
   release here is a no-op for those columns and the `DetachedAnim` slab owns the eventual release at exit-anim end.
5. **Slot generation bump** — `gen++` so any stale `NodeHandle` (captured effect closure, in-flight snapshot,
   `UseElementRef` back-patch target) fails `IsLive`; the slab index is appended to the `QuarantineLedger` and
   only `ReclaimSlot`'d when `_lastConsumedSeq > freedSeq`.

**Test (folds P8's "named, tested"):** `TypeFlip_Remount_ReleasesAllRetainedHandles` (validation.md gate) mounts a
node holding an `ImageHandle` + `BrushHandle` + a `DrivenClock`, flips its type `A→B`, and asserts via the
`ImageRefTable`/`BrushTable` refcount probes + the `DrivenClock` registry that **zero** retained handle survives
into the B subtree, and that the freed slab index is not reclaimed until the consume gate clears. This is the same
assertion shape as the recycle gate (`Recycle_ClearsDerivedColumns`), now covering the general type-flip — so a
control kit that type-flips a row (e.g. `Skeleton`→`TrackRow`) cannot leak a GPU pin.

---

## 6. Render coalescing, priority, and the 3-SIGNAL memo skip

### 6.1 `setState` → re-render request (verbatim control flow, PAL transport)

Reactor's `CreateComponentRerender` (a) bounds re-entrancy via `t_rerenderDepth` (cap 50), (b) marshals
off-loop-thread setters onto the loop thread, (c) sets `slot.SelfTriggered = true`, (d) invokes the parent
`requestRerender`. **All four preserved.** The transport changes from WinUI `DispatcherQueue` to the PAL
`IPlatformAppLoop.Post<TState>` (struct-state marshal, no closure box — `app-requirements` confirms the pattern):

```csharp
// Hooks layer is GPU/OS-agnostic; it holds a RerenderToken, not a DispatcherQueue.
public readonly struct RerenderToken
{
    private readonly IReRenderSink _sink; private readonly int _slot;   // _slot = ComponentTable index
    public void Request() => _sink.RequestRerender(_slot);             // marks SelfTriggered + coalesces
}
```

The off-thread marshal uses `IPlatformAppLoop.IsOnLoopThread` + `Post` (PAL equivalent of
`DispatcherQueue.HasThreadAccess`/`TryEnqueue`). The `t_rerenderDepth` `[ThreadStatic]` cap is copied verbatim
(pure C#).

### 6.2 Frame coalescing — the CAS gate, reused from `ReactorHost.RequestRender`

The coalescer is reused verbatim (already platform-agnostic):
- `_isRendering` guard → during render, setState just sets `_needsRerender = true` (React's
  setState-during-render-queues-N+1).
- `Interlocked.CompareExchange(ref _renderPending, 1, 0)` → at most one frame enqueued between frames; N concurrent
  `setState`s coalesce into one frame.
- After a frame, if `_needsRerender`, re-enqueue at low priority so input/present interleave.

The only change: `IPlatformAppLoop.RequestFrame(priority)` replaces `_dispatcherQueue.TryEnqueue(...)`; the driver
runs the **13-phase loop**. The **render thread** keeps presenting the last good `SceneFrame` while the UI thread is
busy (hardened §4.3), so a slow reconcile cannot tear.

> **RECAST (folds P1 — `RenderPriorityPolicy` becomes the LANE EXECUTOR, not the priority source).** Reactor's
> scalar `RenderPriorityPolicy.PickPriority(lastRenderMs, budget=16ms)` conflated *ordering* (which work is more
> urgent) with *batching* (which updates fold into one frame) — exactly the `expirationTime`-era design React
> abandoned for lanes. It is **no longer the priority source**; updates now carry a **lane** stamped at
> `setState` time (§7.1). `RenderPriorityPolicy` is recast as the **executor**: given the set of pending lanes in
> the update queue and its 16ms budget, it decides **which lanes to flush this frame** (urgent always; transition
> lanes only if budget remains, else re-enqueued at low frame priority). The 16ms budget and the phase-4–5
> stopwatch feed are kept; only the *meaning* changes — it answers "which lanes this frame", not "what whole-frame
> priority". See §7.1 for the lane bitmask and §7.2 for the queue it executes.

### 6.3 The 3-SIGNAL memo skip — KEPT; SubtreeDirty is TRAVERSAL SCOPE ONLY

> **CORRECTNESS REGRESSION CAUGHT IN HARDENING (the keystone fix here).** The original synthesis §6.4 proposed
> *replacing* the per-consumer `contextChanged` check with the `SubtreeDirty` flag — i.e. "the reconciler walks only
> `SubtreeDirty` nodes, so the dirty-ancestor-path scan is free." **That substitution is wrong and was reverted**
> (`hardened-v1-plan.md` §4.4, spec-amendment §7 resolving reconciler open-question #4). A context consumer that is
> NOT on any setState path would not be marked `SubtreeDirty`, so a `SubtreeDirty`-only skip would **wrongly skip it
> and DROP the context update**. `SubtreeDirty` is the **traversal scope** (which subtrees the reconciler descends
> into), **NEVER a skip-decision input**.

The skip decision is the **3-signal gate**, evaluated per component, exactly as v1's correct reconciler:

```
selfTriggered = Volatile.Read(slot.SelfTriggered) != 0;  Volatile.Write(slot.SelfTriggered, 0);
propsChanged  = (class)  ShouldUpdate(old,new) / ShouldUpdate(oldProps,newProps)
                (memo)   !DepsEqual(slot.MemoDeps, newMemo.Deps)            // boxless DepDeps + GcDepTable
                (func)   !ShallowEquals(prevElement, newElement)
contextChanged = HasConsumedContextChanged(slot)                            // per-consumer; see §8

skip = !(selfTriggered || propsChanged || HasConsumedContextChanged)        // the THREE signals
if skip: slot.Element = newEl; return;   // refresh element ref only, no Render()
```

- `selfTriggered` — this component (or a descendant routed here) called `setState`.
- `propsChanged` — `Component.ShouldUpdate` (record value-equality default, unchanged public API) / memo deps /
  func shallow-equal.
- `HasConsumedContextChanged` — a context this component **consumes** changed value (the per-consumer check that
  `SubtreeDirty` cannot substitute for).

**`SubtreeDirty`'s only role:** it gates *traversal* — the reconciler descends into a subtree iff its root is
`SubtreeDirty` (set by upward propagation from any `MarkDirty`/setState). Within a descended subtree, every component
still runs the full 3-signal skip. So `SubtreeDirty` makes the walk O(dirty-scoped) (the win over Reactor's
`HashSet<UIElement>` ancestor scan) **without ever being a correctness input to the skip**. This requires the Scene
contract that `MarkDirty`/setState propagates `SubtreeDirty` to the **component-host ancestor path** (confirmed:
`architecture-spec.md` §5 step 2d — "SubtreeDirty propagates up NodeFlags to the component-host ancestor path").

---

## 7. setState → repaint sequence (exact, on the hardened loop)

```
[user clicks; phase 2 input-dispatch invokes the OnClick closure — UI thread]
        │
        ▼
setCount(n+1)                       (RenderContext.UseState Setter — value-eq guard, verbatim)
        │  value changed (vs the LAST QUEUED, not in-place hook.Value — see §7.2)?
        ├─ no ──► return (no frame)
        └─ yes
              UpdateQueue.Enqueue(slot, updater:=next, lane:=CurrentCauseLane())   // §7.1/§7.2: NOT in-place
              RerenderToken.Request()
                    ├─ off loop thread? ─► IPlatformAppLoop.Post<TState>(...)  (struct-state marshal, no box)
                    └─ on loop thread:
                          Volatile.Write(slot.SelfTriggered, 1)
                          IReRenderSink.RequestRerender(slot):
                                if _isRendering: _needsRerender=true; return            ← coalesce w/ current
                                if CAS(_renderPending,1,0)!=0: _needsRerender=true; return ← coalesce
                                appLoop.RequestFrame(PickPriority(lastRenderMs))
        ▼
════════════ NEXT FRAME (13-phase loop) ════════════
 1 pump (UI)            OS messages; read device-lost + present-ack words (Volatile)
 2 input-dispatch (UI)  hit-test last-published-consistent Bounds; new input; SubtreeDirty propagates to host path
 3 hook-flush (UI)      DRAIN the update queue ONCE (automatic batching): pop all (slot,updater,lane) records
                        whose lane is selected by RenderPriorityPolicy this frame; fold per-slot updaters in
                        enqueue order into committed hook state; mark each touched slot SelfTriggered (§7.2)
 4 render (UI)          descend SubtreeDirty subtrees ONLY; per component run 3-signal skip;
                        if !skip → ctx.BeginRender(token, scope); newChild = Render() → Element tree
 5 reconcile (UI)       atomic-on-complete. structural sub-phase: keyed-LIS vs prev Element on arena scratch →
                        backend.Insert/Move/RemoveAt/CreateNode/DestroyNode (slot reuse consume-gated)
                        edit sub-phase: source-gen Diff → backend.WriteX + MarkDirty
                        effect cells w/ changed deps → EffectScheduler.Enqueue(Layout | Passive)
                        unmounts → RunCleanups() synchronously
 6 layout (UI)          dirty-scoped measure/arrange → Bounds[]
 6.5 layout-effects(UI) EffectScheduler.FlushLayout()  (cleanups then effects; setState here ⇒ mark dirty + N+1)
 7 animation (UI)       advance AnimTracks → LocalTransform/EffectAux; compose WorldTransform[]
 PUBLISH 13a (UI)       copy SnapshotColumns into a free triple-buffer SceneFrame slot, seal,
                        Volatile release-store _publishedIdx; tick consume-gated quarantine; reset GcDepTable
 8 record (RENDER)      walk PaintDirty; clean spans memcpy from render-private prior arena
 9 batch (RENDER)       radix sort sortkeys; coalesce instanced quads; resolve glyph/image atlas UVs
10 submit (RENDER)      ICommandEncoder → IGpuDevice; Signal(fence)
11 present (RENDER)     latency-wait → Present → DComp Commit → Volatile.Write present-ack
12 passive-effects(UI)  EffectScheduler.FlushPassive() (cleanups then effects; setState ⇒ mark dirty + N+1)
   arena swap           per-frame ChunkedArena Reset (O(1))
```

**Phase 3 is the real update queue (folds P1 + P2a — see §7.1/§7.2).** Multiple `setState`/`dispatch` between
frames each **enqueue** a `(slot, updater, lane)` record (NOT an in-place cell mutate) and set `SelfTriggered`; the
coalescer ensures one frame; the queue is drained **once** at phase 3 with **automatic batching** across the whole
handler *and* across `await` continuations. `UseReducer(threadSafe:true)` still serializes concurrent off-thread
writers via lock, but the serialized write is now an *enqueue*, not a cell mutate (verbatim lock, new payload).

---

### 7.1 Priority lanes — a small bitmask, lane from update CAUSE (folds P1)

> **FOLD-INTO-CORE (P1).** Replace the scalar whole-frame `RenderPriorityPolicy` priority *source* with a small
> **lane bitmask** that decouples **ordering** (which work is more urgent) from **batching** (which updates fold
> into one frame). This is the React-lanes idea, sized to FluentGpu: 8 lanes is plenty; we do **not** need React's
> 31-lane entanglement machinery in v1. Stamping lanes now is the cheap part; retrofitting a lane field onto an
> in-use `setState`/hook API is the expensive part (the gap analysis's "cheap-now, costly-later").

```csharp
namespace FluentGpu.Hooks;

[Flags] public enum Lane : byte
{
    None        = 0,
    SyncInput   = 1 << 0,   // input-handler-originated (click/key/pointer in phase 2) — URGENT, never deferred
    Default     = 1 << 1,   // ordinary setState not in a transition and not input-originated — urgent-ish
    Transition  = 1 << 2,   // startTransition / UseTransition body — NON-urgent, may be deferred a frame
    DataArrival = 1 << 3,   // await-continuation / worker Post-back / UseResource resolve — treated as Transition-class
    Deferred    = 1 << 4,   // UseDeferredValue's trailing recompute — lowest, coalesces hardest
    Retry       = 1 << 5,   // Suspense retry after a resource resolves (§7.4) — Transition-class
    // 1<<6, 1<<7 reserved (idle/offscreen prefetch render — virtualization overscan warm)
}

public static class Lanes
{
    public const Lane Urgent     = Lane.SyncInput | Lane.Default;
    public const Lane NonUrgent  = Lane.Transition | Lane.DataArrival | Lane.Deferred | Lane.Retry;
    public static bool IsUrgent(Lane l) => (l & Urgent) != 0;
}
```

**Lane from cause — set at `setState` time by a `[ThreadStatic]` ambient, never inferred.** The lane a `setState`
gets is decided by an **ambient cause** the runtime pushes around the code that triggers the update:

```csharp
internal static class LaneContext
{
    [ThreadStatic] private static Lane _cause;       // UI thread only; default Lane.Default
    public static Lane Current => _cause == Lane.None ? Lane.Default : _cause;

    public readonly ref struct Scope    // ref struct = stack-only, restores on dispose; zero alloc
    {
        private readonly Lane _prev;
        public Scope(Lane l) { _prev = _cause; _cause = l; }
        public void Dispose() => _cause = _prev;
    }
}
```

- **Phase 2 input dispatch** wraps every handler invocation in `using var _ = new LaneContext.Scope(Lane.SyncInput)`
  → every `setState` a click/key handler fires is `SyncInput` (urgent). (Owned by `input-a11y.md`'s dispatch loop;
  it references this seam.)
- **`startTransition(cb)`** pushes `Lane.Transition` for the synchronous portion of `cb`; the **continuation
  capture** (so the lane survives `await`) is **source-generated** — see §7.3 and `dsl-aot.md`.
- **Worker `Post`-back / `UseResource` resolve** marshal onto the UI thread carrying `Lane.DataArrival` in the
  struct-state payload of `IPlatformAppLoop.Post<TState>` (no closure box) — so a data arrival is non-urgent by
  default and cannot preempt a click.
- Anything else = `Lane.Default`.

**`RenderPriorityPolicy` = the lane EXECUTOR (recast, §6.2).** At phase 3 it inspects the union of pending lanes in
the queue and its 16ms budget and returns the **lane mask to flush this frame**: always all `Urgent` lanes; then
`NonUrgent` lanes only if `lastRenderMs` left headroom. Unflushed non-urgent lanes stay in the queue and the
coalescer re-enqueues frame N+1 at low priority — so **a 50k-row regroup wrapped in `startTransition` is demoted to
a non-urgent lane and time-shared across frames instead of being "eaten as one N+1 frame"** (the exact P1 symptom).
The lane mask also rides the `RequestFrame(priority)` call so the OS loop schedules urgent frames ahead of
transition frames.

### 7.1.1 `UseTransition` / `startTransition` / `UseDeferredValue` — the public hooks

```csharp
// startTransition: run cb now; every setState inside cb (sync portion) is tagged Lane.Transition.
public static void StartTransition(Action cb);

// UseTransition: returns (isPending, startTransition). isPending is true while a transition-lane render this
// component started has not yet committed. Backed by a UseState<bool> the runtime flips on enqueue/commit.
public (bool IsPending, Action<Action> Start) UseTransition();

// UseDeferredValue<T>: returns a value that lags `value` by at most one transition-lane render. The trailing
// recompute is enqueued on Lane.Deferred; until it lands, the previous value is returned (keep-stale).
public T UseDeferredValue<T>(T value) where T : IEquatable<T>;
```

`UseTransition`'s `IsPending` is a `UseState<bool>` cell: `Start` sets it true, enqueues the wrapped updaters on
`Lane.Transition`, and the runtime clears it when the transition-lane render for this slot commits (a phase-3
post-drain callback keyed on the slot's pending-transition counter). `UseDeferredValue<T>` stores the last
committed value in a `UseRef` cell; when `value` changes it enqueues a `Lane.Deferred` self-update to advance the
ref, and returns the **ref** (stale) until that low-lane render lands — built entirely on existing `UseState`/
`UseRef`/the queue, no new cell type. All three are **boxless for value-type `T`** via the same `DepKey` projection
used for deps (§3.2).

### 7.2 The phase-3 UPDATE QUEUE — per-update enqueue + AUTOMATIC BATCHING (folds P2a)

> **FOLD-INTO-CORE (P2a).** Phase 3 is no longer "a reserved no-op; v1 applies in place at call time." It is a
> real **update queue**: each `setState`/`dispatch` enqueues a `(slot, updater, lane)` record; the queue is drained
> **once per coalesced frame**, folding every update from a handler **and across `await`** into one consistent
> render. The *shape* (per-update enqueue, not in-place mutate) is fixed now because it cannot change once app code
> depends on the timing. **Column storage for the queue is owned by `scene-memory.md` (the lane/queue storage row);
> this doc owns the queue semantics, the updater shape, and the drain algorithm** (the ownership split the
> directive mandates).

```csharp
namespace FluentGpu.Reconciler;

// One updater record. POD where possible; the functional updater is the one edge GC ref (a delegate),
// parked exactly like a GcDepTable Ref so the record itself stays blittable in the ring.
internal readonly struct UpdateRecord
{
    public readonly int   Slot;        // ComponentTable index whose hook cell this targets
    public readonly int   CellIndex;   // which hook cell in that slot (UseState/UseReducer)
    public readonly Lane  Lane;        // cause lane (§7.1)
    public readonly int   PayloadSlot; // index into the per-frame UpdatePayloadTable (value box OR functional updater)
    public readonly uint  SlotGen;     // slot generation at enqueue — stale records dropped at drain (gen gate)
}
```

The **queue is an MPSC ring** (`UpdateQueue`): UI-thread `setState`s push directly; off-thread `setState`s push
after the `IPlatformAppLoop.Post` marshal (so the ring is single-producer-per-thread, drained single-consumer on
the UI thread at phase 3 — same discipline as the worker-result ring, `threading-render-seam.md` §13). The payload
(either the new value, or a functional updater `prev => next`) lives in a per-frame `UpdatePayloadTable` (a slab
reset O(1) at phase 13); value payloads for `unmanaged T` are blitted, functional-updater delegates are the edge GC
ref (one per pending update, not per frame).

**Drain (phase 3, once, with automatic batching):**

```
HookFlush(laneMaskToFlush):                                  // laneMaskToFlush from RenderPriorityPolicy (§7.1)
  while UpdateQueue.TryPop(out var u):
      if (u.Lane & laneMaskToFlush) == 0: requeue(u); continue        // not this frame's lanes → keep for N+1
      if slotOf(u).Generation != u.SlotGen: drop(u); continue          // component remounted → stale, drop
      cell = slot[u.Slot].Cells[u.CellIndex]
      cell.Pending = ApplyUpdater(cell.Pending ?? cell.Committed, payload(u))   // FOLD in enqueue order (batch)
      Volatile.Write(slotOf(u).SelfTriggered, 1)
  for each touched slot: cell.Committed = cell.Pending; cell.Pending = null     // commit the folded result
```

- **Automatic batching across a handler:** N `setState`s in one click handler enqueue N records on `SyncInput`;
  the coalescer enqueues exactly one frame; phase 3 folds all N into each cell's committed value **before** phase 4
  renders — so the component renders **once** with the final state, not N times. This is the React-18 semantics the
  old in-place model lacked (it mutated cells live and relied only on frame-coalescing, which batches *frames*, not
  *updates within a handler*).
- **Automatic batching across `await`:** the source-gen'd transition/async-action capture (§7.3, `dsl-aot.md`)
  re-establishes the lane on the continuation and routes the post-`await` `setState`s into the **same** queue; they
  drain on the next coalesced frame as one batch. (React-18 parity: pre-18 React did NOT batch across `await`;
  we do.)
- **Functional updaters compose correctly:** because updaters fold in enqueue order over the running `Pending`
  value, `setCount(c => c+1)` twice yields `+2` (not last-write-wins) — the exact reason per-update enqueue beats
  in-place mutate, and the reason the value-eq guard in the setter compares against the **last queued** value, not
  the committed cell (§7 diagram note).
- **Lane selection interaction:** if `laneMaskToFlush` excludes `Transition`, transition records are requeued and
  the urgent records render this frame with stale transition state — that *is* the keep-stale-during-transition
  behavior Suspense (§7.4) and `UseDeferredValue` rely on.

**Thread-confinement & zero-alloc:** the queue ring and `UpdatePayloadTable` are **UI-thread-owned** (off-thread
producers marshal first); steady-state the only allocation is the functional-updater delegate (edge, one per
pending functional update, none for value updates). The ring buffer and payload slab are allocated once and reset
O(1) at phase 13 — **zero per-frame managed alloc on the value-update path** (phases 6–13 stay 0-alloc;
foundations contract honored).

### 7.3 Lane survival across `await` — source-generated continuation capture (folds P1/P2a, ref dsl-aot)

A naive `startTransition(async () => { await fetch(); setX(...); })` would lose the `Transition` lane after the
`await` (the `[ThreadStatic] LaneContext` is unwound when the synchronous portion returns). The fix is a
**source generator** (`dsl-aot.md` owns it) that rewrites `startTransition`/async-action bodies so the captured
lane is threaded into the continuation: the generator emits an `ExecutionContext`-free lane carrier (a struct
stored in the queued async-action state) and re-pushes `new LaneContext.Scope(capturedLane)` at the top of each
continuation segment. The reconciler-side contract is only: **the queue accepts a `(slot,updater,lane)` from any
continuation, and the drain treats it identically to a synchronously-enqueued record.** `dsl-aot.md` owns the
lowering; this doc owns the queue contract it targets.

### 7.4 Lanes feed Suspense keep-stale and UseOptimistic — forward refs

The `Transition`/`Retry` lanes are the machinery §9.5 (Suspense) uses for **keep-stale-during-transition** (a
pending resource reached during a transition-lane render keeps the previous content instead of flashing the
fallback) and §6.5-data (`UseOptimistic`, §8.5) uses for **optimistic-then-rollback** (the optimistic value is a
transition-lane self-update that is discarded when the real async-action result commits). Both are *built on* this
queue + lane model, not parallel mechanisms.

---

## 8. Context propagation (verbatim shadow-stack, dirty-scoped consume, boxless change-check)

`ContextScope` (`List<(ContextBase,object?)>` push/pop stack with shadow-walk-backward + `_version`) is **reused
verbatim** — pure C#. The reconciler pushes an element's `ContextValues` on entering its subtree and pops on leaving,
exactly as Reactor does (`PushContextDisposable`/`Pop`).

`UseContext<T>` is unchanged public API: consumes a hook slot (`ContextCell`), reads `scope.Read(context)`, stores
`LastValue`. `HasConsumedContextChanged(slot)` walks the context cells and compares `scope.Read(ctx)` to
`cell.LastValue` — verbatim. A provider value change at an ancestor changes what descendants `Read`, so their
`contextChanged` is true and they re-render even when props/deps are stable — **this is exactly the per-consumer
signal §6.3 forbids `SubtreeDirty` from replacing.**

**Boxless change-check:** Reactor stored `object? LastValue` (boxes value-type contexts) and compared with
`Equals(object,object)`. We keep the API but give `Context<T>` a `DepKey ToDepKey(T)` projection so the change-check
compares `DepKey`s for value-type contexts (no box). Reference-type contexts fall back to `ReferenceEquals` on the
stored object (via the same `GcDepTable` discipline). Net: context-change detection is boxless for the common
`Context<int>`/`Context<bool>`/`Context<enum>`/`Context<uint>` cases — including the WaveeMusic
**`Context<uint>` over the `ISystemColors.Epoch`** (a 112B `SystemTint` would box and is deliberately NOT the context
payload; `app-requirements` §3.3).

### 8.5 `UseDerived` / `UseContextSelector` — the general derived-state node (folds P6)

> **FOLD-INTO-CORE (P6).** The corpus already ships the load-bearing *instance* of "subscribe to a projection,
> re-read on demand": the boxless `Context<uint>`-over-`ISystemColors.Epoch` pattern (§8 above), where consumers
> subscribe to a coarse `uint` version and re-read the fat value lazily — this is `derivedStateOf`'s
> notify-only-when-the-*projection*-changes behavior, and it is ahead of stock React. P6 **generalizes** it into a
> first-class engine hook so app code stops hand-rolling the `Epoch`-as-`uint` trick per call site.

```csharp
// UseDerived: memoize a projection of inputs; the consumer re-renders only when the PROJECTED value changes
// (compared via DepKey — boxless for value-type TOut), even if the underlying inputs changed more often.
public TOut UseDerived<TOut>(Func<TOut> project, ReadOnlySpan<DepKey> deps) where TOut : IEquatable<TOut>;

// UseContextSelector: consume a context but subscribe ONLY to a projection of it. The component's
// HasConsumedContextChanged signal fires ONLY when select(ctxValue) changes, not when any field changes.
public TSel UseContextSelector<T, TSel>(Context<T> ctx, Func<T, TSel> select) where TSel : IEquatable<TSel>;
```

**`UseDerived` mechanics.** A new `DerivedCell` hook cell stores `{ DepDeps lastDeps; DepKey lastProjected; }`.
On render: if `deps` are unchanged → return the cached projected value (no `project()` call). If deps changed →
run `project()`, compute `DepKey.From*(result)`; **if the new `DepKey` equals `lastProjected`, return the cached
result and do NOT propagate change downstream** (this is the `derivedStateOf` edge-collapse — the input churned but
the output is stable, so dependents skip). For reference-type `TOut`, fall back to `EqualityComparer<TOut>.Default`
via the `GcDepTable` `ReferenceEquals` discipline (no box for the value-type fast path, which is the high-frequency
case: `ScrollOffset→bool isScrolled`, `DrivenClock→int frameBucket`, `Epoch→Brush`).

**`UseContextSelector` mechanics.** It generalizes `HasConsumedContextChanged` (§8) from "did the whole context
value change" to "did `select(ctxValue)` change". The `ContextCell` is extended to optionally carry a
`DepKey selectorLastValue`: when present, the per-consumer change-check (§6.3) compares
`select(scope.Read(ctx)).ToDepKey()` to `selectorLastValue` instead of comparing the whole context value. So a
component that `UseContextSelector(themeCtx, t => t.AccentHue)` re-renders only when the **hue** changes, not when
any other theme field does — boxless, and it composes with the existing 3-signal skip (`contextChanged` becomes
*selected-contextChanged*). This is the engine-level home for the `Epoch`-as-`uint` trick the gap analysis flags as
"absent generalization."

**Zero-alloc / AOT:** `project`/`select` are delegates (edge, captured once at the hook call site — the
source-gen'd dep-span lowering already parks any captured ref deps in `GcDepTable`); the per-frame path is a
`DepKey` compare (8-byte int) + a conditional re-run, no box, no reflection. Per-`TOut`/`TSel` instantiation is
finite and compile-known (NativeAOT-trivial, §14). Storage for the optional `selectorLastValue` on `ContextCell`
is a hook-cell field (this doc's `RenderContext` owns it); no new SceneStore column is required.

---

## 9. Error boundaries (verbatim recovery, retargeted fallback subtree)

`ErrorBoundaryElement(Child, Fallback)` and `ErrorBoundaryNode` are **reused**. The reconcile-time try/catch around
`Render()` (+ the `_errorBoundaryDepth` guard against double-catch) is copied from `ReconcileComponent`:

```
try {
    ctx.BeginRender(token, scope);
    newChild = component.Render();
} catch (HookOrderException) when (hotReloadPass) { ctx.ResetForHotReload(); retry once; }   // §10
  catch (Exception ex) when (_errorBoundaryDepth == 0 && ex is not OOM and not StackOverflow) {
    nearestErrorBoundary.CaughtException = ex;
    newChild = boundary.Fallback(ex);     // render fallback subtree instead
  }
```

The fallback `Element` reconciles into the **same host node** as the failed child — boundary node identity stable,
recovery reuses the slot. `ErrorFallback.BuildElement(ex)` default is reused.

**Threading:** boundaries catch only **render-phase** exceptions (phase 4), exactly like React/Reactor. Exceptions in
**effects** (6.5/12) or **event handlers** (2) are NOT caught by boundaries — they surface to the app's
unhandled-exception sink (matching Reactor's "don't hide user bugs" philosophy and `architecture-spec.md` §7).

---

## 9.5 Suspense boundary — a first-class reconciler element (folds P2b)

> **FOLD-INTO-CORE (P2b — the biggest programming-model gap).** Replace the imperative per-call-site
> `t.IsLoaded ? Row : Skeleton` pattern with a **declarative `Suspense` boundary element** the reconciler
> understands: a subtree that swaps **atomically** between fallback and content, supports **nested progressive
> reveal**, and during a transition (§7.1) **keeps already-revealed content visible** instead of flashing the
> fallback. This is a reconciler+layout concept (which subtree swaps as a unit, where the fallback mounts, how
> reveal interacts with lanes), **not** a bolt-on hook. The keep-stale *capability* already exists per call site
> (`ComponentAnchor` stable identity, synchronous-cleanup→`DetachedAnim` keep-alive, `CrossFadeImage`); P2b adds
> the missing **boundary abstraction** over it. (`threading-render-seam.md` §12 notes that off-thread reconcile
> "is eaten as one N+1 frame with a skeleton" today — Suspense is the framework-level home for that skeleton.)

### 9.5.1 The element + the new sibling-node shape

```csharp
namespace FluentGpu.Dsl;

// Suspense is a composition wrapper element (like Component/Group/ErrorBoundary), dispatched in Reconcile §5.1 step 3.
public sealed record SuspenseElement(
    Element            Content,
    Func<Element>      Fallback,                 // built lazily only when actually pending
    SuspenseReveal     Reveal = SuspenseReveal.Atomic) : Element;   // Atomic | Progressive

public enum SuspenseReveal : byte { Atomic, Progressive }
```

A `SuspenseElement` mounts a **`VisualKind.SuspenseAnchor`** host node — a `Passthrough` node (like
`ComponentAnchor`, zero layout/paint cost) that owns a `SuspenseSlot` in a slab-backed column. The anchor keeps
**both** subtrees as physical children but only one is `Visible`/`HitTestVisible` at a time: the **content
subtree** and (when pending) the **fallback subtree**. Keeping content mounted-but-hidden during a transition is
what makes keep-stale free (no relayout on reveal). **Column storage for the `SuspenseSlot` and the
`SuspenseAnchor` flag is owned by `scene-memory.md`** (a new slab column + a `NodeFlags` bit); this doc owns the
**state machine + reconcile semantics**.

```csharp
internal struct SuspenseSlot          // slab-backed (scene-memory owns the storage; this doc owns the fields' meaning)
{
    public NodeHandle ContentHost;     // the content subtree's anchor
    public NodeHandle FallbackHost;    // mounted only while pending (else Nil)
    public SuspenseState State;        // Showing | Pending | KeepStale
    public int          PendingCount;  // # of descendant UseResource cells currently pending (nested reveal)
    public uint         RevealLane;    // the lane that drove the pending swap (Transition ⇒ keep-stale)
    public NodeHandle   StaleDetached; // DetachedAnim slab handle holding the kept-stale prior content (Nil if none)
}
internal enum SuspenseState : byte { Showing, Pending, KeepStale }
```

### 9.5.2 How "pending" is detected — the `UseResource` throw-to-boundary contract

A descendant signals "not ready" the React-`use(promise)` way, adapted to our single-pass, exception-free hot
path: **`UseResource`/`UseInfiniteResource` (the existing data hooks) set a thread-local `PendingSignal` instead
of throwing.** During phase-4 render of a subtree, if any `UseResource` in it is in `Pending` state, it calls
`SuspenseContext.MarkPending(resourceHandle)`; the **nearest enclosing `SuspenseElement`** (tracked on the
context shadow-stack, §8, exactly like the nearest error boundary) records it in its `SuspenseSlot.PendingCount`.
No exception, no stack unwind — this is *cheaper* than React's throw-and-catch and fits our pure single-pass
render. (We keep the `use(promise)` *programming model* — "read the resource; if not ready, the boundary shows
fallback" — without the throw mechanism.)

> **Why not throw:** our render phase is pure and restartable but **not** exception-driven; a throw per pending
> read would defeat the 0-alloc phase-4 path and the atomic-on-complete reconcile. A thread-local pending tally
> resolved at the boundary is equivalent and allocation-free.

### 9.5.3 The reveal state machine (atomic, nested, transition-aware)

Evaluated at the `SuspenseAnchor` during reconcile (phase 5), after its content subtree's phase-4 render has
tallied `PendingCount`:

```
Showing  --(PendingCount>0, RevealLane is URGENT)-->  Pending     : hide content, mount+show Fallback() atomically
Showing  --(PendingCount>0, RevealLane is Transition)--> KeepStale : KEEP content Visible; DO NOT mount fallback
Pending  --(PendingCount==0)-->                       Showing      : destroy fallback, show content atomically
KeepStale--(PendingCount==0)-->                       Showing      : (content was never hidden) clear KeepStale
```

- **Atomic reveal:** the content subtree is reconciled fully (all its `WriteX` land) **before** the anchor flips
  `Visible` from fallback→content in a single `MarkDirty` — the user never sees a half-populated content tree.
  Because the swap is one `NodeFlags.Visible` toggle on two already-built subtrees, it costs no relayout of the
  rest of the tree (the `SuspenseAnchor` is a layout boundary; `layout.md` §10 owns that isolation).
- **Nested progressive reveal:** each nested `SuspenseElement` has its own `SuspenseSlot`/`PendingCount`; an inner
  boundary reveals its content as soon as *its* resources resolve, independent of an outer boundary still pending.
  `SuspenseReveal.Progressive` on the outer boundary additionally reveals each *direct child boundary* as it
  individually resolves (vs `Atomic`, which waits for the whole content subtree). This is React's nested-Suspense
  behavior, expressed over our per-anchor tally.
- **Transition-aware keep-stale (the lane tie-in, §7.4):** if the update that caused the pending state arrived on a
  `Transition`/`DataArrival` lane (`RevealLane` non-urgent), the boundary enters **`KeepStale`** — it leaves the
  *previously revealed* content `Visible` and never mounts the fallback, so navigating to a new view that is still
  loading does not flash a skeleton. The kept content is the **already-committed** content subtree (no detach
  needed while content stays mounted); if a structural swap is unavoidable (the content subtree's *type* changed),
  the prior content is moved to a **`DetachedAnim` slab** (`StaleDetached`, the §4.4 synchronous-cleanup→detach
  path) so it survives the swap without relayout and cross-fades out via the existing `CrossFadeImage`/implicit-exit
  machinery (`backdrop-effects-animation.md` owns the visual transition).

### 9.5.4 Placement, threading, zero-alloc

- **Phase placement:** pending detection in **phase 4** (render), state-machine + atomic swap in **phase 5**
  (reconcile), resource-resolve `Post`-back schedules **frame N+1** on the `Retry` lane (§7.1) — no synchronous
  re-loop (§4.2). Keep-stale cross-fade advances in **phase 7** (animation), transform-only.
- **Thread:** entirely UI thread (it is reconcile + a `NodeFlags` toggle); the render thread only ever sees the
  published, consistent `SceneFrame` (fallback OR content, never both visible) — atomic-on-complete guarantees no
  half-swapped frame is published.
- **Zero-alloc:** the `Fallback()` builder runs **only** on the `Showing→Pending` edge (not per frame);
  `SuspenseSlot` is slab-backed (no per-frame alloc); the pending tally is a thread-local int. Steady-state
  (content showing, nothing pending) the boundary is a `Passthrough` node skipped by layout/paint — **zero cost**.
- **Edge cases:** (a) a resource that *errors* (not pends) routes to the nearest **ErrorBoundary** (§9), not the
  Suspense boundary — the two boundaries compose, error wins; (b) a boundary unmounted while `Pending` runs its
  fallback's cleanups synchronously and cancels the pending `UseResource` CTS (§11); (c) `PendingCount` underflow
  (a resource resolving twice) is clamped at 0 with a diagnostic.

---

## 9.6 `UseOptimistic` + async-action — optimistic state with rollback over transition lanes (folds P7)

> **FOLD-INTO-CORE (P7).** Add first-class optimistic UX (like / add-to-playlist / reorder) — an optimistic value
> shown immediately, then **reconciled or rolled back** when the real async action settles. This **degenerates to
> manual state without lanes**, so it is built *on* the §7.1 transition lanes (the gap analysis sequences P7 after
> P1 for exactly this reason).

```csharp
// UseOptimistic: returns the current value (optimistic overlay if a transition is in flight, else the base),
// and an apply fn that sets an optimistic value for the duration of the enclosing async action.
public (T Value, Action<T> ApplyOptimistic) UseOptimistic<T>(T baseValue) where T : IEquatable<T>;

// UseActionState: wraps an async action; returns (state, dispatch, isPending). dispatch runs the action on a
// transition lane; state advances to the resolved result; on throw, the optimistic overlay is discarded (rollback).
public (TState State, Func<TArg, Task> Dispatch, bool IsPending)
    UseActionState<TState, TArg>(Func<TState, TArg, Task<TState>> action, TState initial);
```

**Mechanics, entirely over §7's queue + lanes (no new primitive):**

- `UseOptimistic<T>` stores `{ T baseValue; T? overlay; bool hasOverlay; }` in a hook cell. `ApplyOptimistic(v)`
  enqueues a **`Lane.Transition`** self-update setting `overlay=v, hasOverlay=true` and returns immediately — so
  the UI shows `v` on the very next coalesced frame while the network call is still in flight. `Value` returns
  `hasOverlay ? overlay : baseValue`.
- The enclosing **async action** (typically `UseActionState`'s `Dispatch`, or any `startTransition(async …)`)
  carries the `Transition` lane across `await` via the source-gen continuation capture (§7.3). When the action
  **resolves**, it commits the real result to `baseValue` on a `DataArrival`/`Retry` lane and **clears the
  overlay** (`hasOverlay=false`) in the same batch — the optimistic value is seamlessly replaced by the truth (no
  flash if they're equal; a single reconcile if not).
- On action **throw** (rollback): the catch path clears `hasOverlay=false` without touching `baseValue`, enqueued
  on a `DataArrival` lane — the UI snaps back to the pre-optimistic value. Because both the apply and the
  rollback/commit go through the **same update queue**, they batch correctly with any other state the handler
  touched (automatic batching, §7.2), and the `IsPending` flag is the `UseTransition` pending bit (§7.1.1) keyed
  on the action's transition.
- **Multiple in-flight optimistic updates** (e.g. rapid like/unlike) compose because they are **functional
  updaters** folded in enqueue order (§7.2): the overlay reflects the last-applied optimistic value, and each
  action's resolve clears only its own contribution via a per-action token compared at commit (stale resolves are
  dropped — same `TargetGen` discipline as worker results, `threading-render-seam.md` §13).

**Zero-alloc / thread:** UI-thread only; the cell is one edge object (mount-time); per-apply cost is one queue
record (value payload blitted for `unmanaged T`) + the edge async-action delegate. Boxless `T` compares via the
`DepKey` projection (§3.2). No new SceneStore column — the optimistic state lives in the hook cell this doc owns.

---

## 10. Hot-reload-friendly state transfer (gated, AOT-dead in production)

Reactor's hot-reload story is fully reusable and **statically dead under NativeAOT**: every entry point is gated on
`MetadataUpdater.IsSupported` / `HotReloadService.IsHotReloadLive` and annotated `[UnconditionalSuppressMessage]`, so
the trimmer drops it in `PublishAot` builds (preserves the AOT goal; `dotnet10` §1). Three mechanisms, all kept:

1. **Hook-order recovery** (`ResetForHotReload`): a reorder/retyped-hook edit throws `HookOrderException`; inside a
   hot-reload pass we `RunCleanups()` + clear the cell table + re-render once. Verbatim — pure C#.
2. **Hook-state migration** (`MigrateHooksForHotReload`): at pass start, value cells whose stored type was edited get
   a fresh instance with surviving fields copied by name (`ReactorHotReloadCopier`), value-swapped into the cell.
   `_hookIndex` untouched ⇒ hook identity preserved. Reflection isolated to this method, trimmed in production.
3. **Subtree/component-type migration**: an edit minting a new `Component` Type token transfers the live
   `RenderContext` (hooks + cleanups + `GcDepTable`) onto the freshly-constructed instance (`Component.Context`
   internal setter exists for this). Reused.

**Store-specific gating:** during migration we **must NOT recycle node handles** (state transfer must preserve the
`ComponentSlot`→`NodeHandle` binding). The pass sets `ForceFullRenderPending` which (a) **bypasses the 3-signal memo
skip** so edited bodies run, (b) keeps `HostNode` handles stable, (c) re-runs the per-element `Diff` so visual edits
land even when props are unchanged. `ForEachLiveContext` (walks all `ComponentTable` slots) replaces Reactor's
dictionary walk — same shape, column-backed. Hot-reload runs **on the UI thread at frame start** and, like any other
mutation, terminates at the normal PUBLISH(13a) — it never publishes a half-migrated tree (atomic-on-complete).

---

## 11. Virtualization — `UseVirtual` / `UseInfiniteCollection` realize the window as keyed children

(WaveeMusic's #1 load-bearing gap; full design in `app-requirements` §3.2 — this section owns the **hook + reconcile
interaction**.)

**Virtualization is a layout participant + a hook trio, NOT a control and NOT a new phase.** A virtual node has
`LayoutKind.VirtualList/VirtualGrid` + a datasource handle; each frame it computes `[first,last)` from
`ScrollOffset` + viewport + overscan, calls the dev's `RenderItem(i)` *only* for the window, and hands those to the
**existing keyed-LIS reconciler** (§5.4) with stable `ItemKey`s. **Recycling IS `DestroyNode`/`CreateNode` over the
slab free list** — no second `RecyclePool` layer (avoids WinUI's `ElementPool` COM-detach pain).

```csharp
VirtualHandle  UseVirtual<T>(int itemCount, RenderItem<T> renderItem, GetItem<T> getItem,
                             KeyOf<T> keyOf, in VirtualSpec spec, ReadOnlySpan<DepKey> deps);
InfiniteCollection<T> UseInfiniteCollection<T>(int totalCount, FetchPage<T> fetchPage, ...); // composes UseInfiniteResource
void UseVisibleRange(VirtualHandle v, Action<Range> onChange, ReadOnlySpan<DepKey> deps);    // prefetch/image-warm bridge (phase 6.5)
```

`RenderItem`/`GetItem`/`KeyOf` are static-friendly fn-pointer-shaped delegates (no per-row closure). `ItemKey` is a
16B POD interning into the existing `StringId` key space, so the LIS diff is **unchanged**.

**The window buffer = `ArrayPool<Element>.Shared`, NOT the cap-32 `ObjectPool`.** This is the critical
`foundations.md` / `app-requirements` §5 ruling: the cap-32 `ObjectPool<class>` is an *edge* pool and would
**overflow precisely during list realization** (a window of 40+ rows). The window buffer is sized to
window+overscan, rented from `ArrayPool<Element>`, and reused.

**Honest alloc claim.** Within-window scroll that does not cross an item boundary is a **transform-only frame**
(phase 7, ~0 alloc). Crossing a boundary re-renders only the **delta** rows; the delta's phase-4 render **allocates
bounded Gen0** (`Element` records + the pooled `Element[]` window chunk). So: **phases 6–13 paint machinery is
0-alloc; phase 4 realize-delta is bounded Gen0.** Mitigation: only ENTER rows re-render; survivors keep their
`Element` via component memoization (the 3-signal skip).

**Reconcile interaction (recycle invariants, pinned):**
1. Rows **never bind palette-derived brushes** (`WantPalette=false`; only O(1)-per-page hero/now-playing nodes call
   `UseDerivedBrush`).
2. On recycle to a new key, the reconciler **re-runs the component** (image/palette deps change → re-request) OR
   explicitly clears any derived-brush/image column on the recycled slot — no stale handle survives. The image
   request-epoch (carried in a `UseRef` hook cell, `app-requirements` §3.1) survives recycle and drops late
   callbacks; this does **not** rely on `NodeHandle` generation (which bumps on *free*, not recycle).
3. A per-range `CancellationTokenSource` in the slab-backed `VirtualState` column is cancelled when a range exits
   overscan+prefetch margin (cancels page fetch + image decode).

**13-phase placement.** P2 scroll updates `ScrollOffset`, sets `NodeFlags.VirtualRangeDirty` only if the offset
crossed a boundary. P4 if `VirtualRangeDirty`/datasource-version changed, `RenderItem` the new window. P5 keyed-LIS
diff → enter `CreateNode`, exit `DestroyNode`. P6.5 `UseVisibleRange`/`ScrollToIndex` (layout effects). P7
`-ScrollOffset` → viewport `LocalTransform` (transform-only). Off-thread page-result `Post` marks dirty + schedules
**frame N+1** (no synchronous re-loop — §4.2). Sticky group-header pin = a transform write in phase 7, excluded from
clean-span memcpy via `NodeFlags.StickyPinned`.

---

## 12. Threading model — which phases this subsystem owns, on which thread (hardened §2)

| Concern | Thread |
|---|---|
| Phases 3,4,5,6,6.5,7 + PUBLISH(13a) + 12 | **UI thread** (the sole writer of SceneStore/ComponentTable/HookCells) |
| `Render()`, reconcile, hook reads, `ContextScope`, `SceneStore` writes via backend, `GcDepTable` | **UI thread only** |
| Effect *bodies* (6.5 & 12) | UI thread (effects may spawn `Task.Run`/`Channel` for I/O themselves) |
| `setState`/`dispatch`/`reducer` from a worker | Allowed; **auto-marshaled** to UI thread, then **enqueued** on the UpdateQueue carrying `Lane.DataArrival` (unless `threadSafe:true` → locked enqueue + marshaled rerender request) |
| `RerenderToken.Request()` off-thread | CAS gate is `Interlocked`; the enqueue marshals — safe |
| `UpdateQueue` ring + `UpdatePayloadTable` (P2a), `LaneContext` `[ThreadStatic]` (P1), Suspense `PendingSignal` (P2b) | **UI thread only** (off-thread producers marshal first; drained single-consumer at phase 3) |
| `SlabAllocator`/`ChunkedArena`/`ComponentTable`/`HandleTable`/`SuspenseSlot`/`UpdatePayloadTable` | **UI thread only** (no locks; the marshal invariant guarantees single-writer) |
| Record / batch / submit / present (phases 8–11) | **RENDER thread** — consumes the published immutable `SceneFrame`; **owns every ComPtr** |
| Hot-reload migration pass | UI thread (frame start) |

This subsystem **never touches a `ComPtr`, a command list, or a GPU fence** — the render thread owns those by
confinement (hardened §2.1). The reconciler only ever mutates `SceneStore` (UI thread) and hands the renderer a
**value-copied `SnapshotColumns`** at PUBLISH. The seam is clean **by construction**: this subsystem is
"render-thread-ready" with no change between build-order step 1 (single-thread, UI produces+consumes, quarantine=0)
and step 4+ (render thread spawned, quarantine flipped behind the green race gate). The only contract it must honor
across the flip: **slot reuse is consume-gated** (a freed node slot is reusable only when `_lastConsumedSeq >
freedSeq`) — §5.5.

---

## 13. Zero-alloc strategy — summary ledger

| Path | Reactor allocation | fluent-gpu |
|---|---|---|
| Hook deps per render | `params object[]` + `.ToArray()` | **`ReadOnlySpan<DepKey>` (stackalloc) + inline `DepDeps`** — zero heap ≤4 deps |
| Dep compare | `Equals(object,object)` (boxes value types) | `DepKey.Equals` (8-byte int cmp) + `GcDepTable` `ReferenceEquals` — no box |
| Reference/delegate deps | boxed into `object[]` | **`GcDepTable` side-buffer**, slot index in `DepKey.Bits` — no box, legal CLR layout |
| node→component lookup | `Dictionary<UIElement,ComponentNode>` | `ComponentSlot` SoA column read — no hash, no alloc |
| Key extraction | attached DP `GetValue` (COM) | `Key` SoA column read |
| Property update | COM readback to guard writes | source-gen mask `Diff`, no readback |
| Keyed-middle scratch | per-reorder `Dictionary`/`int[]`/`HashSet`/`bool[]` | **`ChunkedArena` spans** + pooled `Dictionary<int,int>` (reset); `StringId` int keys |
| Child enumeration | COM `IVector` per index | borrowed `Span<NodeHandle>` snapshot, O(1) `Get(i)` |
| Effect flush list | per-component inline | one pooled `List<EffectRef>` per timing, `Clear()` reused; bottom-up sort O(k log k) over firing set only |
| Virtual window buffer | n/a (WinUI controls) | **`ArrayPool<Element>`** (NOT cap-32 ObjectPool) |
| Context change-check | boxes value contexts | `DepKey` projection — boxless for value types |
| Effect cell / `RenderContext` | class per slot/component (at mount) | **kept** — edge alloc, mount-only, acceptable |
| setState (value update) | in-place cell mutate | **`UpdateQueue` MPSC ring + `UpdatePayloadTable` slab** (reset O(1) @ phase 13); value payload blitted for `unmanaged T` — **0 heap** (P2a) |
| setState (functional updater) | in-place cell mutate | one edge delegate per pending functional update (none for value updates) (P2a) |
| Lane stamp | n/a (scalar policy) | `[ThreadStatic] Lane` ambient + stack-only `LaneContext.Scope` ref struct — **0 alloc** (P1) |
| `UseDerived`/`UseContextSelector` | n/a | `DepKey` projected compare (8-byte int) per frame; `project`/`select` delegate is edge (mount-time) (P6) |
| Suspense boundary (steady) | per-call-site `? :` re-render | `Passthrough` `SuspenseAnchor` skipped by layout/paint; `Fallback()` runs only on Showing→Pending edge — **0 cost** (P2b) |
| `UseOptimistic` apply | manual UseState | one queue record (value blitted for `unmanaged T`) + edge async-action delegate (P7) |

Per-frame steady state (no structural change, props stable): **zero managed allocation** in this subsystem. A reorder
or mount allocates only edge objects (new `RenderContext`/cells for new components) + arena scratch (reset O(1) at
phase 13). A virtual realize-delta allocates bounded Gen0 (`Element` records + pooled window chunk) — the honest,
CI-gated edge (`hardened-v1-plan.md` §4.5: O(Δ) Gen0, not zero).

---

## 14. NativeAOT implications

- **No reflection on hot paths.** Per-element `Diff`/`MountWriter` and dep-span lowering are **source-generated**
  (`FluentGpu.SourceGen`), not reflected. Reactor's `IPropsReceiver`/`IPropsComparable` dispatch (already
  reflection-free) is kept.
- **No `params object[]`** — the AOT-hostile generic-array path is gone; replaced by `ReadOnlySpan<DepKey>`.
- **`DepKey` is `LayoutKind.Sequential` blittable** — not `Explicit`/overlapped (the illegal GC-ref union is fixed);
  `ComponentSlotData` is `unmanaged`; `DepDeps` uses `[InlineArray(4)]`. All AOT-trivial.
- **Reflection isolated & trimmed:** only `MigrateHooksForHotReload`/`SnapshotHooks`/`ReactorHotReloadCopier`, gated
  behind `MetadataUpdater.IsSupported` + `[UnconditionalSuppressMessage]` — statically dead in production.
- **No dynamic codegen.** All dispatch is static (source-gen'd `switch(ElementTypeId)` ushort, or virtual on `Element`
  arms). `UseState<T>`/`UseMemo<T>`/`Ref<T>`/`UseDerived<TOut>`/`UseContextSelector<T,TSel>`/`UseOptimistic<T>`/
  `UseActionState<TState,TArg>` instantiate per-T (finite, compile-known); `DepKey` and `Lane` are single
  non-generic types, avoiding a dep-path/lane generic explosion.
- **Lane / transition / Suspense / optimistic capture is source-generated** (`dsl-aot.md` owns the generator): the
  `await`-crossing lane carrier (§7.3) and the async-action continuation capture are static rewrites — **no
  `ExecutionContext` reflection, no dynamic delegate creation** on the hot path. The `LaneContext` ambient is a
  `[ThreadStatic] Lane` (a byte) + a stack-only ref struct scope — AOT-trivial, zero-alloc.
- **`UpdateQueue` is a struct-record MPSC ring**; the only managed ref it carries is the functional-updater
  delegate (an edge object), parked exactly like a `GcDepTable` ref so the ring stays blittable. `SuspenseSlot` is
  `unmanaged` (slab-backed). No reflection, no boxing on the queue/lane/Suspense paths.
- **COM:** this subsystem references no COM at all (`dotnet10` §4 COM ruling does not apply here — the closest COM is
  `[GeneratedComInterface]` UIA in `PlatformIntegration`, which marshals *into* the OnClick path at phase 2, not into
  the reconciler).

---

## 15. Cross-platform-seam boundary — what's Windows-specific vs portable

**Everything in this subsystem is portable (zero Windows, zero D3D, zero COM, zero `ComPtr<T>`).**

| Layer | Windows-specific? | Notes |
|---|---|---|
| `Reconciler`, `ChildReconciler`, `ComponentTable`, `EffectScheduler` (bottom-up), virtualizer | **No** | Pure C# over `ISceneBackend` + `Element` records |
| `RenderContext`, hook cells, `DepKey`, `DepDeps`, `GcDepTable`, `ContextScope` | **No** | Pure C# |
| `Lane`/`LaneContext`, `UpdateQueue`/`UpdatePayloadTable`, `SuspenseContext`+reveal FSM, `UseTransition`/`UseDerived`/`UseOptimistic` (P1/P2a/P2b/P6/P7) | **No** | Pure C#; `Lane.SyncInput` is pushed by the PAL/input dispatch (behind a seam), lane survival across `await` is source-gen'd (`dsl-aot`), neither Windows-specific |
| `RenderPriorityPolicy` (lane executor) | **No** | Pure C#; 16ms budget + stopwatch feed |
| `ISceneBackend` interface + `NodeChildCollection` | **No** | POD/handle/span/ref-struct only |
| `ISceneBackend` impl (SoA column writes, snapshot copy) | **No** | In `Scene`; knows SoA layout, not GPU/OS |
| `RerenderToken`/`IReRenderSink`/`IPlatformAppLoop.Post` | **No** (PAL interface) | Windows impl uses the OS message loop; macOS uses CFRunLoop — leaf, behind PAL |
| Off-thread marshal mechanism | **No** | `IPlatformAppLoop.IsOnLoopThread`/`Post<TState>` — PAL, not `DispatcherQueue` |
| `ComPtr<T>`, DXGI, D3D12, DComp, DWrite, HWND, `WM_*` | **N/A here** | Live only in `FluentGpu.Windows` D3D12/ / Pal/ / DirectWrite/ leaves, ≥2 layers below; never referenced |

The macOS/Metal port reimplements only the PAL app-loop (`IPlatformAppLoop`) and the RHI/Text leaves; the reconciler
+ hooks subsystem ships **unchanged**.

---

## 16. Reactor file → fluent-gpu disposition (reuse vs replace)

| Reactor file | Disposition | What changes |
|---|---|---|
| `Core/Element.cs` | **Reuse (regen arms)** | Keep record hierarchy + `ShallowEquals`/`CanSkipUpdate`/`HasCallbacks`/`ModifiersEqual`. WinUI `Brush`/`Thickness`/`FontFamily` → `Foundation` POD (`ColorBrushRef`, `Edges4`, `FontRef`). Per-type arms source-gen'd. → `Dsl`. |
| `Core/Component.cs` | **Reuse near-verbatim** | `Component`/`Component<TProps>`, `ShouldUpdate`, `Context` setter. WinUI-typed hooks (`UseWindowSize`/`UseColorScheme`) re-pointed at PAL/`UseContext`. |
| `Core/RenderContext.cs` | **Reuse, modify deps + effects; ADD concurrent-era hooks** | Hook table, `BeginRender`, `FlushEffects`→split Layout/Passive + route to `EffectScheduler` (now bottom-up), `RunCleanups`, hot-reload methods, cell types. **Change:** `params object[]`→`ReadOnlySpan<DepKey>`; cell `Dependencies object[]`→inline `DepDeps` + `GcDepTable` ref slots; `setState` setter enqueues onto `UpdateQueue` (NOT in-place). **Add:** `UseTransition`/`UseDeferredValue`/`UseDerived`/`UseContextSelector`/`UseOptimistic`/`UseActionState` + `DerivedCell`/optimistic cells (§7.1.1/§8.5/§9.6). → `Hooks`. |
| `Core/Reconciler.cs` | **Replace orchestration, reuse logic** | Keep: 3-signal memo/self-trigger logic, `CreateComponentRerender`+`t_rerenderDepth`, error-boundary try/catch, `HasConsumedContextChanged`, `ForEachLiveContext`, node shapes. **Replace:** WinUI mount/update bodies; `Dictionary<UIElement,…>`→`ComponentTable`+columns; `ReactorState` attached-DP (deleted); descriptor/handler registry (→source-gen'd writers); Border-anchor→`ComponentAnchor` node. Add atomic-on-complete `ReconcileSlicer`. |
| `Core/Reconciler.Mount.cs` | **Replace** | per-control `MountXxx` → source-gen'd `MountWriter` emitting `backend.WriteX`. |
| `Core/Reconciler.Update.cs` | **Replace** | per-control `UpdateXxx` (COM readback) → source-gen'd mask `Diff` + `backend.WriteX`. |
| `Core/ChildReconciler.cs` | **Reuse verbatim (algorithm)** | 4-phase keyed LIS + `ComputeLIS` copied; collection → `NodeChildCollection` (ref struct over borrowed `Span<NodeHandle>` + `RemoveAt`); keys via `Key` column; scratch re-authored on `ChunkedArena`; `string`→`StringId`. |
| `Core/Context.cs`, `Core/ContextScope.cs` | **Reuse verbatim** | Pure C#. Add `DepKey` projection for boxless change-check. |
| `Core/ElementPool.cs` | **Replace** | WinUI control pool → not needed; recycle = slab free-list + gen bump. `ObjectPool<RenderContext>`(cap 32) kept (edge). |
| `Core/HookOrderException.cs`, `Core/ErrorFallback.cs` | **Reuse verbatim** | Pure C#. |
| `Core/Internal/KeyedListDiff.cs`, `ReactorListState.cs` | **Reuse algorithm** | Retargeted to handles; drives `UseVirtual`/`UseInfiniteCollection` window-as-keyed-children. |
| `Hooks/Pending.cs`, `UseResource.cs`, `UseMutation.cs`, `UseInfiniteResource.cs`, `UseMemoCells.cs` | **Reuse + extend** | Built atop `UseRef`/`UseEffect`/`UseReducer`; dep arrays → spans; `DepsEqual` → `DepKey`. **Extend:** `UseResource` adds throw-free `MarkPending` to the nearest `SuspenseElement` (§9.5.2); `UseMutation` becomes the base for `UseOptimistic`/`UseActionState` (§9.6). |
| `Hooks/UseElementRef.cs`, `UseFocus*.cs` | **Replace transport** | Resolve to `NodeHandle` + `Input` subsystem, not `UIElement`/WinUI focus. API shape kept. |
| `Hosting/ReactorHost.cs` `RequestRender`/`RenderLoop` | **Reuse coalescer, replace driver** | CAS gate + `_isRendering`/`_needsRerender` + low-priority re-enqueue copied; `Render()` body → 13-phase `FrameLoop` + PUBLISH; `DispatcherQueue`→`IPlatformAppLoop`. |
| `Hosting/RenderPriorityPolicy.cs` | **Reuse, RECAST as lane executor** | Budget (16ms) + phase-4–5 stopwatch feed kept; meaning changed from whole-frame priority *source* to per-frame **lane selection** (which `Lane`s to flush; anti-starvation watermark) (§6.2/§7.1). |
| `Hosting/HotReloadService.cs`, `ReactorHotReloadCopier.cs` | **Reuse** | Gated AOT-dead; reflection isolated. |

---

## 17. Failure / edge cases

1. **`setState` during render (phase 4):** `_isRendering` guard → sets `_needsRerender`, queues N+1. A component
   calling its own setter synchronously inside `Render()` hits `t_rerenderDepth` cap (50) →
   `InvalidOperationException("Render loop detected")` (verbatim). Bounded, fail-fast.
2. **`setState` in a passive effect (phase 12):** normal — mark dirty + N+1.
3. **`setState` in a layout effect (phase 6.5):** **mark dirty + N+1** (NO synchronous re-loop — §4.2). The earlier
   "bounded re-loop of phases 4–6" is removed.
4. **Stale `NodeHandle` use** (closure outlives node destroy): gen check fails → backend op throws
   `StaleHandleException` (debug) / returns `Nil` (release). ABA-safe via gen++ on alloc and free.
5. **Slot reuse before consume:** a node freed at publish `p` is *not* reusable until `_lastConsumedSeq > p`
   (consume-gated quarantine). Premature reuse is impossible by the ledger; in single-thread step 1, quarantine=0 and
   the case degenerates.
6. **Hook-count change without hot reload** (conditional hook): `HookOrderException` at the cell-type mismatch —
   caught only inside a hot-reload pass; otherwise propagates to error boundary / fallback.
7. **Duplicate keys in a keyed list:** `Dictionary<int,int>` keeps last-write; LIS still produces a valid (if
   suboptimal) move set; no crash; diagnostic logged.
8. **Component type flip at same position (`A`→`B`):** `KeyMatch`/`CanUpdate` false → unmount A (cleanups run, node
   destroyed/quarantined, **all retained GPU/OS handles released per the §5.6 contract**), mount B (new slot/handle).
   State intentionally lost (React semantics); zero handle leak (the tested P8 contract).
9. **Off-thread setter after host shutdown:** `IPlatformAppLoop.Post` returns false (loop closing) → loud "update
   dropped" diagnostic (verbatim `MarshalIfOffUIThread` policy) so a leaking background producer is visible.
10. **Effect cleanup/body throws:** isolated per-cell (try/catch-log per `RunPendingCleanup`/`RunPendingEffect`);
    does not abort the drain of other cells.
11. **Context provider removed mid-tree:** descendants' `scope.Read` falls back to `DefaultValue`;
    `HasConsumedContextChanged` detects the change → they re-render (the per-consumer signal; not `SubtreeDirty`).
12. **Memo element with null deps** ("render once"): `DepDeps.HasDeps=false` on both → never re-renders from parent.
13. **Layout effect reads `Bounds` before layout ran** (first mount): valid because layout effects flush at 6.5,
    strictly after phase-6 layout for the dirty subtree.
14. **Reference dep object resurrected to a new instance with equal contents:** `GcDepTable` compares by
    `ReferenceEquals` → treated as changed → effect re-runs (React parity; documented).
15. **Virtual window overflow vs cap-32 pool:** impossible — the window buffer is `ArrayPool<Element>`, never the
    cap-32 `ObjectPool` (the explicit `app-requirements` §5 / `foundations.md` guard).
16. **Two `setState`s with the same value in one handler (P2a):** both enqueue; the value-eq guard compares against
    the **last queued** value, so the second is dropped if equal — but functional updaters (`c => c+1`) are never
    dropped (they compose). Net: idempotent value sets coalesce; counters increment correctly.
17. **`startTransition` body throws synchronously (P1):** the `LaneContext.Scope` is `using`/ref-struct, so the
    `[ThreadStatic]` lane is restored on unwind; already-enqueued transition records remain valid and drain
    normally. A throw in the *async* portion routes to `UseActionState` rollback (§9.6) or the unhandled sink.
18. **Transition lane starves under a sustained urgent flood (P1):** `RenderPriorityPolicy` guarantees forward
    progress by promoting a transition lane to flush after a bounded number of frames it was skipped (anti-
    starvation watermark) — a transition cannot be deferred indefinitely. Bounded, deterministic.
19. **Suspense resource resolves after its boundary unmounted (P2b):** the resolve `Post` carries the boundary's
    slot generation; drain drops it (gen gate, §7.2) — no fallback-flash into a dead boundary, no crash.
20. **Suspense content type changes while `KeepStale` (P2b):** prior content moved to a `DetachedAnim` slab
    (`StaleDetached`) before the structural swap, cross-fades out; the new content reconciles fully then reveals
    atomically — no relayout flash (the `SuspenseAnchor` is a layout boundary).
21. **`UseOptimistic` action resolves out of order (P7):** each action commit carries a per-action token; a stale
    resolve (an earlier action finishing after a later one) is dropped by token compare — last-dispatched wins,
    matching the optimistic intent (same `TargetGen` discipline as worker results).
22. **`UseDerived` projection that allocates or has side effects (P6):** documented as forbidden — `project` must be
    pure (it runs only on dep change, but ordering vs effects is undefined); a DEBUG `AllocScope` guard around
    `project()` flags per-call Gen0 in tests (validation.md gate), matching the rules-of-hooks lint posture.

---

## Implemented from the gap analysis

Every gap below was previously "reserved / deferred / out-of-scope for v1" framing; it is now a fully-specified,
buildable **core** design in this doc. (Cross-cutting storage/seams owned by other docs are listed under XREF.)

| Gap | What it is | Folded into core here | New artifacts this doc defines |
|---|---|---|---|
| **P1** | Priority lanes + `UseTransition`/`UseDeferredValue`/`startTransition`; lane from update cause | §6.2 (recast `RenderPriorityPolicy` → lane **executor**), §7.1, §7.1.1, §7.3 | `Lane` bitmask (8 lanes), `Lanes` helper, `LaneContext` + `LaneContext.Scope` ref struct, `UseTransition`/`UseDeferredValue`/`StartTransition` |
| **P2a** | Real phase-3 update queue (per-update enqueue, NOT in-place) + automatic batching across handler AND across `await` | §7 diagram, §7.2, §7.3, §3.4 setter note | `UpdateRecord`, `UpdateQueue` (MPSC ring), `UpdatePayloadTable` slab, phase-3 `HookFlush` drain with lane selection + functional-updater fold |
| **P2b** | Suspense boundary: atomic reveal, nested progressive reveal, transition-aware keep-stale over `UseResource` pending + `DetachedAnim` keep-alive | §9.5 (.1–.4) | `SuspenseElement`, `SuspenseReveal`, `VisualKind.SuspenseAnchor`, `SuspenseSlot`/`SuspenseState`, throw-free `UseResource.MarkPending`/`SuspenseContext`, reveal state machine |
| **P6** | General derived-state node generalizing `Context<uint>`-over-`Epoch` | §8.5 | `UseDerived`, `UseContextSelector`, `DerivedCell`, `ContextCell.selectorLastValue` |
| **P7** | `UseOptimistic` + async-action (optimistic + rollback over transition lanes) | §9.6 | `UseOptimistic`, `UseActionState`, per-action commit token |
| **P8** | Named, tested "type-flip remount RELEASES retained handles" contract (atlas pins/present-tree/DComp/`DrivenClock`) wired to the recycle sync-cleanup point | §5.5 (extended), §5.6, edge case 8 | `DestroyNode` 5-step release contract; `TypeFlip_Remount_ReleasesAllRetainedHandles` test (validation) |
| **P9** | Bottom-up intra-phase effect drain (child-before-parent) + rationale | §4.3 (ratified) | `EffectRef{Depth,SlotGen}` + `IsLive` gen gate, depth-descending stable drain (also lands the P5 carry-forward gen-stamp contract from the reconciler side) |

These remove the prior "phase 3 is a reserved no-op", "v1 applies in place at call time", "Suspense is a v2 /
N+1-frame skeleton", and "general type-flip release is not a named contract" framings and replace them with the
designs above.

---

## Changed vs the original synthesis

The amendments folded into this actualization (each closing a hardening finding or ratifying a contract):

1. **DepKey is now a pure-scalar 16-byte blittable struct + a `GcDepTable` side-buffer.** The original
   `[StructLayout(Explicit)]` overlapping a GC ref onto a scalar at `[FieldOffset(0)]` was **illegal CLR layout**;
   it is replaced by `LayoutKind.Sequential` (no object ref) with reference/delegate deps parked in a parallel
   managed `GcDepTable` compared by `ReferenceEquals` (§3.2/§3.3). Delegate deps = `Ref` (resolves old OQ#5).
2. **The 3-SIGNAL memo skip is kept; `SubtreeDirty` is TRAVERSAL SCOPE ONLY.** Reverts the original §6.4 proposal to
   replace the per-consumer `contextChanged` check with a `SubtreeDirty` gate — a correctness regression (would drop
   context updates to consumers off the setState path). `SubtreeDirty` now never feeds the skip decision (§6.3;
   resolves old OQ#4).
3. **Effect timing RATIFIED:** `UseLayoutEffect` at phase **6.5**, `UseEffect` at phase **12**; **`setState` in any
   effect ⇒ mark dirty + frame N+1 with NO synchronous bounded re-loop** in v1. Removes the original §4.3 "bounded
   re-loop of phases 4–6 once" (resolves old OQ#1; §4.1/§4.2, edge case 3).
4. **`ISceneBackend` is handle-in/handle-out POD with `NodeChildCollection.RemoveAt`** and a borrowed-`Span<NodeHandle>`
   child snapshot (the `Children()` op) instead of O(n²) `ChildAt(i)` probes (resolves old OQ#3; §2/§5.4). Added
   `WriteAnim`.
5. **Keyed-LIS scratch re-authored on `ChunkedArena`** (supersedes the single-buffer per-frame arena), with chunk
   growth gated by the native high-water counter, `StringId` int keys, pooled `Dictionary<int,int>` reset (§5.4).
6. **Virtualization owned here:** `UseVirtual`/`UseInfiniteCollection`/`UseVisibleRange` realize the window as
   **keyed children** through the existing keyed-LIS reconciler; the **window buffer is `ArrayPool<Element>`, never
   the cap-32 `ObjectPool`**; recycle = slab `DestroyNode`/`CreateNode`; honest "phase-4 realize-delta is bounded
   Gen0, paint machinery 0-alloc" claim (§11; edge case 15).
7. **Time-sliced reconcile = atomic-on-complete by default**, terminating at **PUBLISH(13a)** of an immutable
   `SceneFrame`; reconcile runs on the **UI thread**; slot reuse is **consume-gated quarantine** across the
   render-thread seam (§5/§5.5/§12).
8. **Hot-reload gated AOT-dead** and store-aware (`ForceFullRenderPending` preserves `ComponentSlot`→`NodeHandle`,
   bypasses the 3-signal skip, terminates atomic-on-complete) (§10).
9. **Reconcile expressed as the architecture-spec two sub-phases** (structural growth-allowed → edit growth-locked),
   and the boxless **`Context<uint>` over `ISystemColors.Epoch`** change-check (§5/§8) aligned with the WaveeMusic
   fold-ins.
10. **13-phase loop + explicit thread map**, render thread owns every ComPtr; this subsystem references no COM,
    `ComPtr`, command list, or fence (§7/§12/§14/§15).
11. **Phase 3 is a real update queue (P2a):** per-update `(slot,updater,lane)` enqueue replaces in-place cell
    mutation; drained once per coalesced frame with automatic batching across the handler and across `await`;
    functional updaters fold in enqueue order (§7.2). Removes "v1 applies in place at call time".
12. **Priority lanes (P1):** an 8-lane `Lane` bitmask + `LaneContext` ambient (lane from cause) replaces the scalar
    priority *source*; `RenderPriorityPolicy` recast as the lane *executor*; `UseTransition`/`UseDeferredValue`/
    `startTransition` added; lane survives `await` via source-gen continuation capture (§6.2/§7.1/§7.3).
13. **Suspense boundary (P2b):** a `SuspenseElement` + `SuspenseAnchor` node with an atomic/nested/keep-stale
    reveal state machine over throw-free `UseResource` pending + `DetachedAnim` keep-alive (§9.5). Removes the
    "v2 / N+1 skeleton" framing.
14. **`UseDerived`/`UseContextSelector` (P6):** general derived-state + selector-subscription node generalizing the
    `Context<uint>`-over-`Epoch` projection; boxless `DepKey`-projected change-check (§8.5).
15. **`UseOptimistic`/`UseActionState` (P7):** optimistic-then-rollback built on transition lanes + the update
    queue, with per-action commit tokens for out-of-order resolves (§9.6).
16. **Type-flip remount releases retained handles (P8):** `DestroyNode` 5-step release contract (image/palette pins,
    realization handles, present-tree/DComp, `DrivenClock`/`AnimTrack`, gen bump) + a named test, generalizing the
    recycle path (§5.5/§5.6).
17. **Bottom-up effect drain (P9):** layout/passive queues drained depth-descending (child-before-parent) with an
    `EffectRef` gen-stamp gate that also lands the P5 carry-forward consistency contract from the reconciler side
    (§4.3).
```
