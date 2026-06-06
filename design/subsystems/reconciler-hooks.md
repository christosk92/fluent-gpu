# FluentGpu — Subsystem Design: Reconciler + Hooks Runtime (retargeted to the SoA SceneStore, hardened-v1)

**Author scope:** ONE subsystem — the Reactor-style reconciler + hooks runtime, retargeted from patching
WinUI `FrameworkElement`s to patching **our** SoA `SceneStore` (the single retained RenderNode tree) through
an `ISceneBackend` seam, and re-fitted to the hardened-v1 threading + memo + effect-timing contracts.

This doc is the **authority** for: the `Reconciler`, `ChildReconciler` (keyed-LIS), `ComponentTable`,
`RenderContext` + the hook cell zoo, `DepKey`/`DepDeps`/`GcDepTable`, the `EffectScheduler` + effect timing,
the `ISceneBackend` operation set, `RerenderToken`/`IReRenderSink`/the coalescer, `ContextScope` consume, error
boundaries, hot-reload, and the virtualization hook trio. It **references — does not duplicate** — the shared
contracts owned elsewhere:

- **Threading / publish / quarantine / build order** → `hardened-v1-plan.md` §2, §4.1, §6 (canonical).
- **Scene / DrawList / clean-span / dirty axes** → `architecture-spec.md` §4.4/§4.5, §5.4.
- **RHI / PAL seams** → `architecture-spec.md` §4.7/§5.1.
- **Handles / allocators / StringId / ChunkedArena** → `foundations.md`.
- **.NET 10 / C#14 / COM ruling / zero-alloc patterns** → `dotnet10-csharp14-zero-alloc.md`.
- **WaveeMusic fold-ins (virtualization, image/theme hooks)** → `app-requirements-waveemusic.md` §3.2/§5.

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
  │  StringId, ChunkedArena, DepKey, DepDeps, NodeFlags
Scene (refs Foundation)            ── SceneStore SoA + ISceneBackend (the seam) + SnapshotColumns/SceneFrame
Dsl   (refs Foundation)            ── Element records, ElementModifiers, factories  (GPU-agnostic)
Hooks (refs Foundation)            ── RenderContext, hook cells, Ref<T>, Context<T>, ContextScope, GcDepTable
Reconciler (refs Scene,Dsl,Hooks)  ── Reconciler, ChildReconciler, ComponentTable, EffectScheduler, virtualizer
Hosting (refs everything + leaves) ── FrameLoop driver, RequestRender coalescer, IReRenderSink impl
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

### 4.3 The effect queue — global, ordered, drained at the two phase boundaries

```csharp
internal sealed class EffectScheduler
{
    private readonly List<EffectRef> _layout  = new();   // drained at phase 6.5 (UI)
    private readonly List<EffectRef> _passive = new();   // drained at phase 12 (UI)

    public void Enqueue(RenderContext ctx, int cell, EffectTiming t)
        => (t == EffectTiming.Layout ? _layout : _passive).Add(new EffectRef(ctx, cell));

    public void FlushLayout()  => Drain(_layout);
    public void FlushPassive() => Drain(_passive);

    private static void Drain(List<EffectRef> q)
    {
        for (int i = 0; i < q.Count; i++) q[i].Ctx.RunPendingCleanup(q[i].Cell);  // PHASE A: cleanups
        for (int i = 0; i < q.Count; i++) q[i].Ctx.RunPendingEffect (q[i].Cell);  // PHASE B: new effects
        q.Clear();   // reuse list next frame — zero realloc
    }
}
internal readonly record struct EffectRef(RenderContext Ctx, int Cell);
```

`RenderContext.UseEffect`/`UseLayoutEffect` no longer *run* anything; on dep change they set `cell.Pending = true`,
stage `PendingCleanup = cell.Cleanup`, and `Scheduler.Enqueue(ctx, index, cell.Timing)`. This is the minimal change
from Reactor's `UseEffect` (same `Pending`/`PendingCleanup` fields) — we route the *run* to the frame scheduler. Each
`RunPendingCleanup`/`RunPendingEffect` is wrapped per-cell in try/catch-log so one cell's throw cannot abort the
drain (matches `architecture-spec.md` §7 "per-cell try/catch isolates effect-body exceptions").

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
runs the **13-phase loop**. `RenderPriorityPolicy.PickPriority(lastRenderMs, budget=16ms)` is reused as-is; "render"
now means phases 4–5 duration, fed from the phase stopwatch. The **render thread** keeps presenting the last good
`SceneFrame` while the UI thread is busy (hardened §4.3), so a slow reconcile cannot tear.

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
        │  value changed?
        ├─ no ──► return (no frame)
        └─ yes
              hook.Value = next
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
 3 hook-flush (UI)      apply queued reducer batches (in-place in v1; reserved hook point — §7 note)
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

**Reducer batching note (phase 3):** multiple `setState`/`dispatch` between frames each mutate their cell in place
and set `SelfTriggered`; the coalescer ensures one frame. `UseReducer(threadSafe:true)` serializes concurrent writers
via lock (verbatim). Phase 3 is reserved as a no-op hook point for a future batched-update queue (React-18 automatic
batching); v1 applies in place at call time = current Reactor semantics.

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
| `setState`/`dispatch`/`reducer` from a worker | Allowed; **auto-marshaled** to UI thread via `IPlatformAppLoop.Post<TState>` (unless `threadSafe:true` → in-place locked write + marshaled rerender request) |
| `RerenderToken.Request()` off-thread | CAS gate is `Interlocked`; the enqueue marshals — safe |
| `SlabAllocator`/`ChunkedArena`/`ComponentTable`/`HandleTable` | **UI thread only** (no locks; the marshal invariant guarantees single-writer) |
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
| Effect flush list | per-component inline | one pooled `List<EffectRef>` per timing, `Clear()` reused |
| Virtual window buffer | n/a (WinUI controls) | **`ArrayPool<Element>`** (NOT cap-32 ObjectPool) |
| Context change-check | boxes value contexts | `DepKey` projection — boxless for value types |
| Effect cell / `RenderContext` | class per slot/component (at mount) | **kept** — edge alloc, mount-only, acceptable |

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
  arms). `UseState<T>`/`UseMemo<T>`/`Ref<T>` instantiate per-T (finite, compile-known); `DepKey` is a single
  non-generic type, avoiding a dep-path generic explosion.
- **COM:** this subsystem references no COM at all (`dotnet10` §4 COM ruling does not apply here — the closest COM is
  `[GeneratedComInterface]` UIA in `PlatformIntegration`, which marshals *into* the OnClick path at phase 2, not into
  the reconciler).

---

## 15. Cross-platform-seam boundary — what's Windows-specific vs portable

**Everything in this subsystem is portable (zero Windows, zero D3D, zero COM, zero `ComPtr<T>`).**

| Layer | Windows-specific? | Notes |
|---|---|---|
| `Reconciler`, `ChildReconciler`, `ComponentTable`, `EffectScheduler`, virtualizer | **No** | Pure C# over `ISceneBackend` + `Element` records |
| `RenderContext`, hook cells, `DepKey`, `DepDeps`, `GcDepTable`, `ContextScope` | **No** | Pure C# |
| `ISceneBackend` interface + `NodeChildCollection` | **No** | POD/handle/span/ref-struct only |
| `ISceneBackend` impl (SoA column writes, snapshot copy) | **No** | In `Scene`; knows SoA layout, not GPU/OS |
| `RerenderToken`/`IReRenderSink`/`IPlatformAppLoop.Post` | **No** (PAL interface) | Windows impl uses the OS message loop; macOS uses CFRunLoop — leaf, behind PAL |
| Off-thread marshal mechanism | **No** | `IPlatformAppLoop.IsOnLoopThread`/`Post<TState>` — PAL, not `DispatcherQueue` |
| `ComPtr<T>`, DXGI, D3D12, DComp, DWrite, HWND, `WM_*` | **N/A here** | Live only in `Rhi.D3D12`/`Pal.Windows`/`Text.DirectWrite` leaves, ≥2 layers below; never referenced |

The macOS/Metal port reimplements only the PAL app-loop (`IPlatformAppLoop`) and the RHI/Text leaves; the reconciler
+ hooks subsystem ships **unchanged**.

---

## 16. Reactor file → fluent-gpu disposition (reuse vs replace)

| Reactor file | Disposition | What changes |
|---|---|---|
| `Core/Element.cs` | **Reuse (regen arms)** | Keep record hierarchy + `ShallowEquals`/`CanSkipUpdate`/`HasCallbacks`/`ModifiersEqual`. WinUI `Brush`/`Thickness`/`FontFamily` → `Foundation` POD (`ColorBrushRef`, `Edges4`, `FontRef`). Per-type arms source-gen'd. → `Dsl`. |
| `Core/Component.cs` | **Reuse near-verbatim** | `Component`/`Component<TProps>`, `ShouldUpdate`, `Context` setter. WinUI-typed hooks (`UseWindowSize`/`UseColorScheme`) re-pointed at PAL/`UseContext`. |
| `Core/RenderContext.cs` | **Reuse, modify deps + effects** | Hook table, `BeginRender`, `FlushEffects`→split Layout/Passive + route to `EffectScheduler`, `RunCleanups`, hot-reload methods, cell types. **Change:** `params object[]`→`ReadOnlySpan<DepKey>`; cell `Dependencies object[]`→inline `DepDeps` + `GcDepTable` ref slots. → `Hooks`. |
| `Core/Reconciler.cs` | **Replace orchestration, reuse logic** | Keep: 3-signal memo/self-trigger logic, `CreateComponentRerender`+`t_rerenderDepth`, error-boundary try/catch, `HasConsumedContextChanged`, `ForEachLiveContext`, node shapes. **Replace:** WinUI mount/update bodies; `Dictionary<UIElement,…>`→`ComponentTable`+columns; `ReactorState` attached-DP (deleted); descriptor/handler registry (→source-gen'd writers); Border-anchor→`ComponentAnchor` node. Add atomic-on-complete `ReconcileSlicer`. |
| `Core/Reconciler.Mount.cs` | **Replace** | per-control `MountXxx` → source-gen'd `MountWriter` emitting `backend.WriteX`. |
| `Core/Reconciler.Update.cs` | **Replace** | per-control `UpdateXxx` (COM readback) → source-gen'd mask `Diff` + `backend.WriteX`. |
| `Core/ChildReconciler.cs` | **Reuse verbatim (algorithm)** | 4-phase keyed LIS + `ComputeLIS` copied; collection → `NodeChildCollection` (ref struct over borrowed `Span<NodeHandle>` + `RemoveAt`); keys via `Key` column; scratch re-authored on `ChunkedArena`; `string`→`StringId`. |
| `Core/Context.cs`, `Core/ContextScope.cs` | **Reuse verbatim** | Pure C#. Add `DepKey` projection for boxless change-check. |
| `Core/ElementPool.cs` | **Replace** | WinUI control pool → not needed; recycle = slab free-list + gen bump. `ObjectPool<RenderContext>`(cap 32) kept (edge). |
| `Core/HookOrderException.cs`, `Core/ErrorFallback.cs` | **Reuse verbatim** | Pure C#. |
| `Core/Internal/KeyedListDiff.cs`, `ReactorListState.cs` | **Reuse algorithm** | Retargeted to handles; drives `UseVirtual`/`UseInfiniteCollection` window-as-keyed-children. |
| `Hooks/Pending.cs`, `UseResource.cs`, `UseMutation.cs`, `UseInfiniteResource.cs`, `UseMemoCells.cs` | **Reuse** | Built atop `UseRef`/`UseEffect`/`UseReducer`; dep arrays → spans; `DepsEqual` → `DepKey`. |
| `Hooks/UseElementRef.cs`, `UseFocus*.cs` | **Replace transport** | Resolve to `NodeHandle` + `Input` subsystem, not `UIElement`/WinUI focus. API shape kept. |
| `Hosting/ReactorHost.cs` `RequestRender`/`RenderLoop` | **Reuse coalescer, replace driver** | CAS gate + `_isRendering`/`_needsRerender` + low-priority re-enqueue copied; `Render()` body → 13-phase `FrameLoop` + PUBLISH; `DispatcherQueue`→`IPlatformAppLoop`. |
| `Hosting/RenderPriorityPolicy.cs` | **Reuse verbatim** | Budget fed by phase-4–5 stopwatch. |
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
   destroyed/quarantined), mount B (new slot/handle). State intentionally lost (React semantics).
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
```
