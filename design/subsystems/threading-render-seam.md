# fluent-gpu — Subsystem Design: Threading & the Render-Thread Seam

Assemblies: **`FluentGpu.Hosting`** (owns the threads, the publisher, the quarantine ledger, the
device-lost rendezvous, the worker pool) · **`FluentGpu.Render`** (the render-thread frame body:
record → batch → submit-call; owns the DrawList arenas) · **`FluentGpu.Windows` (D3D12/ folder)** (leaf; owns every
`ComPtr`, the GPU fence, the deferred-delete ring, the texture-staging ring, the DComp present-tree).

This is the deep, buildable design behind **`hardened-v1-plan.md` §2 (threading model) and §4.1 (the
render-thread seam)**. It goes deeper than the plan: the actual types, the exact memory-ordering, the
reclaim algorithm, the failure rendezvous, and the test ledger. It does **not** redesign the
cross-subsystem contracts — it consumes them. Where a shared type is owned elsewhere it is referenced,
not duplicated:

- SceneStore SoA columns, `NodeFlags`, the dirty worklist, `Mutate()` epoch chokepoint, `CleanSpanWitness`
  — **owned by Scene/Render** (`architecture-spec.md` §4.4/§4.5, §5.4; `gpu-renderer.md` §13).
- DrawList opcode set, `QuadInstance`, batcher, SortKey — **owned by Render** (`gpu-renderer.md` §3–§13).
- RHI surface (`IGpuDevice`/`ISwapchain`/`ICommandEncoder`/`SubmitDrawList`) — **owned by Rhi**
  (`architecture-spec.md` §4.7; `gpu-renderer.md` §2).
- Reconciler/Hooks effect timing, memo skip, `DepKey` — **owned by Reconciler** (`architecture-spec.md` §5.6).
- COM ruling, `[LibraryImport]`, hand-vtable `calli`, `[GeneratedComInterface]` split — **owned by the COM doc**
  (`dotnet10-csharp14-zero-alloc.md` §4; `hardened-v1-plan.md` §4.2).

This subsystem **owns** exactly: the 3-thread topology + single-writer table, `SceneFramePublisher`,
`SnapshotColumns`, `QuarantinePolicy`/`QuarantineLedger`, `ThreadGuard`, the render-private DrawList
arena ring, the render-frame ordering invariant, the **retire-fence handshake** that unifies CPU-slot
and GPU-resource lifetime, the device-lost cross-thread rendezvous, backpressure policy, the
`ReconcileSlicer`, the worker-pool harness, and the build-order gate.

Decisions are stated as MADE. Open items are `OQ-n`.

---

## 0. The one sentence, and the one honest caveat

**Safety rests on confinement + immutability — single-writer per piece of mutable state, immutable POD
snapshots across threads — not on a human auditing memory barriers forever.** Every hazard that reduces
to "one thread owns this refcount/slot/column" is SAFE-by-construction. There is exactly **one**
lock-free fence surface left (the publish/consume/retire handshake), and it is **MANAGED-sampled**:
there is no managed ThreadSanitizer, so its acquire/release correctness is reviewed + schedule-fuzzed +
golden-checked, never *proven*. We shrink the lock-free surface to that one named handshake and treat
any new lock-free shortcut as a standing review red-flag.

And the blunt operational truth carried from the plan: **production safety == CI coverage.** Every
`ThreadGuard.AssertWriter` / `QuarantineLedger` deterministic-throw is `[Conditional("FGGUARD")]`-erased
from the shipping NativeAOT binary, so in the customer's hands these hazards are caught by the corpus the
team maintained, not by the running app.

> **Implementation note (2026-07-01) — landing as Cut A.** Per `docs/plans/render-thread-seam-landing-plan.md` §2 the
> seam is being landed as **Cut A (submit-only)**: the per-frame carrier is a `RenderFrame` naming a finished DrawList in
> a `DrawListArenaRing`, and **record stays on the UI thread** (already sub-ms + zero-alloc; the measured stall is in
> submit/present). The **Cut B** `SceneFrame`/`SnapshotColumns` design in §2.1/§3 — record-on-render — remains the
> eventual target *only if* record ever shows on the UI-thread budget; the sections below describe it and are reconciled
> to Cut A section-by-section as the seam lands. **Step 1 (foundation) is LANDED single-thread + gate-green**
> (`SceneFramePublisher` + `DrawListArenaRing` + `QuarantineLedger` + `QuarantinePolicy`, wired as a UI-thread
> pass-through in `AppHost.Paint`; `Quarantine` logically 0). The `ThreadGuard`, quarantine-derivation (§5.1), publisher
> ordering (§2.2), 3-slot rule (§2.3) and arena-ring rationale (§6) apply to both cuts unchanged.

---

## 1. The 3-thread topology + single-writer confinement table

Three thread classes. Each piece of mutable state has **exactly one writer thread**. This is the entire
safety argument; the rest of the doc is mechanism.

```
┌─ UI THREAD ("the Reactor thread"; the OS message thread; STA for UIA/OLE) ───────────────────┐
│ SOLE WRITER OF: SceneStore SoA columns · LayoutCache · ComponentTable · HookCells ·          │
│   prev-Element retention · Brush/Clip/GlyphRun/Path INTERN side · skyline atlas PACKING math ·│
│   InputEventRing READ cursor · QuarantineLedger producer side · publish slot picker           │
│ RUNS phases 1–7, 6.5, PUBLISH(13a), 12 (passive effects, frame N+1)                           │
│ TOUCHES ZERO COM. No ComPtr, no command list, no GPU fence, ever. (FGCOM-pinned.)             │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
        │  PUBLISH(13a): copy SnapshotColumns into a free SceneFrame slot → seal →
        │   Volatile.Write(_publishedIdx) (release) ; signal Channel<seq> cap-1 DropOldest (wakeup only)
        ▼
┌─ RENDER THREAD (one, dedicated, name "fgpu-render", thread-affinitized, outlives any frame) ──┐
│ SOLE WRITER & SOLE COM OWNER: every ComPtr · RhiHandleTable · PSO cache · instance UploadRing ·│
│   texture-staging ring · glyph GPU atlas pages · path tess slab · GPU fence · deferred-delete  │
│   ring · DComp device/visual-tree · render-private DrawList arenas (ring of >=3) ·             │
│   QuarantineLedger consumer side (_lastConsumedSeq writer) · DeviceLost recovery executor      │
│ RUNS phases 8–11 on the consumed snapshot: DRAIN(workers, atlas evict, retire) → record →      │
│   batch → submit → present (+ Volatile.Write present-ack)                                      │
│ DETECTS device-lost → Volatile.Write(reasonWord) → cross-thread rendezvous (§9)                │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
        │  pure jobs by handle (decode/palette/page-fetch/tess-cold/glyph-RASTER-when-v2)
        ▼
┌─ WORKER POOL (owned threads, NOT ThreadPool; N = clamp(ProcessorCount-2, 2..6)) ──────────────┐
│ Pure functions over immutable inputs → POD results by handle. Touch NO SceneStore, NO RhiTable,│
│   NO GPU fence, NO ComPtr (decode via IImageCodec returns a managed/native pixel buffer the     │
│   render thread uploads). Each job AllocScope-wrapped. Results land in a lock-free MPSC ring,   │
│   drained by the render thread at phase-8 DRAIN. Carries a TargetGen for stale-drop.            │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 1.1 The confinement table (binding; the `ThreadGuard` enforces it)

| Mutable state | Sole writer | Readers | Cross-thread mechanism |
|---|---|---|---|
| SceneStore columns (Topology/Bounds/NodePaint/Flags/…) | UI | UI; render reads **only via SnapshotColumns copy** | value-copy at PUBLISH |
| `WorldTransform[]` (composed phase 7) | UI | render (copied) | value-copy at PUBLISH |
| LayoutCache, ComponentTable, HookCells | UI | UI only | none |
| Brush/Clip/GlyphRun/Path **intern + content tables** | UI (append+mutate side) | render (read by handle) | **append-mostly, content-immutable; epoch-stamped** (§7) |
| InputEventRing | FluentGpu.Windows/Pal/ (write) / UI (read) | — | host-owned POD ring, drained once/frame |
| DrawList arenas (ring >=3) | render | render | render-private; UI never swaps/resets one |
| every `ComPtr`, RhiHandleTable, PSO, UploadRing, staging ring, atlas pages, GPU fence | render | render | none — COM never crosses the seam |
| `_publishedIdx`, sealed SceneFrame slot bytes | UI (write) → render (read) | — | Volatile release/acquire (§2) |
| `_consumeIdx`, `_lastConsumedSeq`, present-ack seq, device-lost reason | render (write) → UI (read) | — | Volatile both-directions (§2) |
| QuarantineLedger entries | UI (append on free) | UI (reclaim) | gated by `_lastConsumedSeq` (Volatile.Read) |
| deferred-delete ring (GPU resources) | render | render | keyed by GPU fence value |
| worker job results MPSC ring | worker (write) → render (read) | — | lock-free MPSC, `TargetGen`-guarded |

**The single most important decision: the render thread owns every `ComPtr`.** There is exactly one
thread that can `AddRef`/`Release`/`Dispose` any `ComPtr`, so the "COM-under-AOT cross-thread refcount
race" is not audited — it is impossible. Cost: the UI thread cannot touch a GPU resource. That is the
point. `SceneFrame` transfers ownership of *handles* (POD), never a `ComPtr` reference; a dedicated
`FGCOM` analyzer rule pins each COM object to one thread (`hardened-v1-plan.md` §4.2).

### 1.2 `ThreadGuard` — deterministic single-writer enforcement

```csharp
namespace FluentGpu.Hosting.Threading;

public static class ThreadGuard
{
    // set ONCE at thread spawn; never reassigned.
    [ThreadStatic] private static ThreadRole t_role;
    private static int s_uiThreadId = -1, s_renderThreadId = -1;

    public enum ThreadRole : byte { Unbound = 0, Ui = 1, Render = 2, Worker = 3 }

    internal static void BindCurrent(ThreadRole role) { t_role = role; /* record id */ }

    [Conditional("FGGUARD")]                 // erased from shipping AOT; live in CI/asserts builds
    public static void AssertUi()     { if (t_role != ThreadRole.Ui)     ThrowWrongThread(ThreadRole.Ui); }
    [Conditional("FGGUARD")]
    public static void AssertRender() { if (t_role != ThreadRole.Render) ThrowWrongThread(ThreadRole.Render); }
    [Conditional("FGGUARD")]
    public static void AssertWorkerOrRender() { if (t_role is not (ThreadRole.Worker or ThreadRole.Render)) ThrowWrongThread(ThreadRole.Worker); }

    [DoesNotReturn] private static void ThrowWrongThread(ThreadRole expected)
        => throw new ThreadConfinementViolation(expected, t_role); // deterministic, never swallowed
}
```

`AssertWriter` style: every mutating SceneStore/RhiTable accessor opens with `ThreadGuard.AssertUi()` /
`AssertRender()`. Because it is **deterministic throw** (not a best-effort log) and runs under the
`FGGUARD` define in every CI build, a confinement violation **cannot reach a green PR**. It is the
SAFE-by-construction backstop for "someone wired a call onto the wrong thread."

`FGGUARD` is defined in: all unit/integration test configs, the `seam.race` soak build, the
fault-injection build. It is **undefined** in `Release`/`Ship` (AOT) — so the guard, and its
`ThreadStatic`/branch, vanish entirely. This is the "production safety == CI coverage" line.

---

## 2. `SceneFramePublisher` — triple-buffer, both-directions-volatile, the exact ordering

The publisher is the seam point. It carries the immutable per-frame snapshot from UI→render with one
release-store / acquire-load happens-before, and protects slot reuse with a symmetric consume index.

### 2.1 The POD snapshot (`SceneFrame`) and what lives in it

`SceneFrame` is a POD value (no GC ref except the *retained-table roots*, which are content-immutable and
addressed by handle). It is **Cut B**: record runs on the render thread, so the frame carries the inputs
record needs, not a finished DrawList.

```csharp
namespace FluentGpu.Hosting.Threading;

// One per triple-buffer slot. ~cache-line header + pointers into render-readable buffers.
public struct SceneFrame
{
    public ulong PublishSeq;                 // monotonic; the happens-before token
    public int   NodeCount;                  // live node count this frame
    public SnapshotColumns Columns;          // VALUE-COPIED hot columns (see §3)
    public DamageView Damage;                // device-space damage rects (<=16 or full-frame flag)
    public CapturedEpochs Epochs;            // per-realization-table epoch arrays captured at publish (§7)
    // Stable roots into append-mostly, content-immutable retained tables (read by handle on render thread):
    public RetainedTableRoots Tables;        // Brush/Clip/GlyphRun/Path/Image table base + count snapshot
    public FrameTimeInfo Time;               // delta, vsync target — for any render-thread-side timing
    public PresentIntent Present;            // VideoSurface placements, DComp-commit-needed flags (§10)
}
```

`SnapshotColumns`, `DamageView`, `CapturedEpochs`, `RetainedTableRoots` are all blittable POD; the
buffers they point at are render-readable (see §3 for the copy and the budget). **No SceneStore column
array is aliased into the SceneFrame** — they are value-copied, because the UI thread mutates them next
frame.

### 2.2 The publisher type and the exact memory-ordering

```csharp
public sealed class SceneFramePublisher
{
    private readonly SceneFrame[] _slots = new SceneFrame[3];   // triple-buffered
    private readonly SnapshotArena[] _slotArenas;               // 3 render-readable column arenas (one per slot)

    // both-directions-volatile (the critique fix): UI reads _consumeIdx before picking a free slot.
    private int  _publishedIdx = -1;        // written by UI (release), read by render (acquire)
    private int  _consumeIdx   = -1;        // written by render (release), read by UI (acquire)
    private ulong _publishSeq;              // UI-private monotonic counter
    private ulong _lastConsumedSeq;         // written by render (release), read by UI (acquire) — also feeds quarantine

    private readonly Channel<ulong> _wake;  // cap 1, DropOldest — WAKEUP/COALESCE ONLY, never the data path

    // ── UI THREAD ───────────────────────────────────────────────────────────────────────────
    public void Publish(in SnapshotRequest req)
    {
        ThreadGuard.AssertUi();
        int consumed = Volatile.Read(ref _consumeIdx);      // ACQUIRE — what render is/was reading
        int free = PickFreeSlot(_publishedIdx, consumed);   // never the published-not-yet-consumed slot, never the consuming slot
        ref SceneFrame f = ref _slots[free];

        ulong seq = ++_publishSeq;
        SnapshotColumns.CopyInto(_slotArenas[free], req, out f.Columns); // §3: the actual copy + budget
        f.PublishSeq = seq; f.NodeCount = req.NodeCount; f.Damage = req.Damage;
        f.Epochs = CapturedEpochs.Capture(req);             // §7: snapshot realization epochs
        f.Tables = RetainedTableRoots.Snapshot(req);
        f.Present = req.Present;

        Volatile.Write(ref _publishedIdx, free);            // RELEASE — publishes ALL stores above as visible-before
        _wake.Writer.TryWrite(seq);                         // coalescing wakeup; failure (full) is fine — render re-reads idx
    }

    // ── RENDER THREAD ───────────────────────────────────────────────────────────────────────
    public bool TryAcquire(out SceneFrame frame)
    {
        ThreadGuard.AssertRender();
        int idx = Volatile.Read(ref _publishedIdx);         // ACQUIRE — pairs with Publish release
        if (idx < 0) { frame = default; return false; }
        frame = _slots[idx];                                // POD copy of header; column buffers are slot-arena-backed
        Volatile.Write(ref _consumeIdx, idx);               // RELEASE — UI now knows this slot is in use
        Volatile.Write(ref _lastConsumedSeq, frame.PublishSeq); // RELEASE — drives consume-gated quarantine (§5)
        return true;
    }
}
```

**The happens-before, stated exactly.** All the field stores in `Publish` (the column memcpy, the epoch
capture, the table roots) are ordinary stores; the single `Volatile.Write(ref _publishedIdx, free)` is a
**release** that makes every prior store visible to any thread that performs the paired **acquire**
`Volatile.Read(ref _publishedIdx)` in `TryAcquire` and observes that value. .NET's `Volatile.Write`/`Read`
are release/acquire (ECMA-335 + the .NET memory model clarifications); on x64 and Arm64 this is the
correct and sufficient fence — no `Interlocked`, no full barrier needed on the hot publish path. The
**reverse** direction (`_consumeIdx`, `_lastConsumedSeq`, present-ack, device-lost reason) is *also*
volatile, so the UI thread's slot picker and quarantine-tick never race the render thread's reads/writes.

**Why not `Channel<SceneFrame>` as the data path.** A bounded `Channel` with `DropOldest` *coalesces*
correctly for wakeup but cannot be the data carrier: (a) it would box/queue the value with `ValueTask`
machinery on a path we want lock-free, and (b) DropOldest silently discards frames whose slots we must
still account for in quarantine. So the **channel is cap-1 DropOldest wakeup-only**; the triple buffer is
the data; **slot recycling is the retire-fence handshake (§5/§8), not the channel's drop callback.**

### 2.3 `PickFreeSlot` — why 3 slots, never 2

With 3 slots, at any instant the render thread may be reading slot `c` (`_consumeIdx`) and the UI thread
may have a freshly-published-but-not-yet-consumed slot `p` (`_publishedIdx`). The UI thread must write
into a slot that is **neither** — the third slot is always available. Two slots cannot guarantee this:
the UI thread could overwrite the slot the render thread is mid-read of (the both-directions-volatile read
of `_consumeIdx` makes the hazard *detectable*, but only 3 slots make it *avoidable* without blocking).

```csharp
static int PickFreeSlot(int published, int consuming)
{
    for (int i = 0; i < 3; i++) if (i != published && i != consuming) return i;
    return -1; // unreachable with 3 slots and at most 2 occupied; FGGUARD asserts
}
```

---

## 3. `SnapshotColumns` — what is copied, and the WorldTransform budget

The render thread must not read live SceneStore columns (the UI mutates them next frame). At PUBLISH the
UI copies the **paint-relevant subset** of each live node's columns into the slot's render-readable arena.

### 3.1 The copied set (~120 B/node, the record-needs subset)

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct SnapshotColumns
{
    // base pointers into the slot arena; index = compacted live-node index (NOT the sparse slab index)
    public ArenaSpan<NodeHandle>   Handles;       // 8B  — identity + IsLive validation against captured gen
    public ArenaSpan<Affine2D>     WorldTransform;// 24B — composed top-down in phase 7 (the big one; see §3.2)
    public ArenaSpan<RectF>        BoundsLocal;   // 16B — node-local arrange result
    public ArenaSpan<NodePaintLite>NodePaint;     // ~48B — Fill/Stroke/Corners/Opacity/Clip/VisualKind/Layer/StrokeWidth
    public ArenaSpan<NodeFlags>    Flags;          // 4B  — render reads PaintDirty/TransformDirty/SubtreePaint/NeedsLayer/Hidden
    public ArenaSpan<int>          PayloadRef;     // 4B  — GlyphRun/Path/Image index for the per-kind opcode
    public ArenaSpan<TopologyLite> Topology;       // 16B — FirstChild/NextSibling/ChildCount (compacted) for the record walk order
}
```

**Clean-node fast copy.** A node whose `Flags` carry no Paint/Transform/Layout dirt and whose realization
epochs are unchanged is `memcpy`'d wholesale from the *previous* slot's `SnapshotColumns` (the back
snapshot), governed by the same `IsLive ∧ epoch-unchanged` rule the DrawList clean-span reuse uses
(`gpu-renderer.md` §13, `architecture-spec.md` §4.5). So the per-frame copy cost is
**O(changed nodes)** for everything *except* WorldTransform — see below.

What is **not** copied: LayoutInput (layout is done), LayoutAux, A11yInfo, FocusNav, InteractionInfo
(input/UIA read live UI-thread state, never the snapshot). Those stay UI-thread-confined.

### 3.2 The WorldTransform copy budget — the honest correction

**The WorldTransform copy is NOT damaged-only.** An ancestor transform change — a scroll, a camera/pan,
a parent scale animation — dirties `WorldTransform` for the *entire descendant set* while each
descendant's DrawList span stays clean (the `TransformDirty` fast path: the batcher re-applies the new
world transform to the cached instanced quads at submit, no re-record). So during any transform
animation/scroll the WorldTransform column must be fully re-copied for the affected subtree:

> **Budget: up-to-full-tree WorldTransform copy = `NodeCount × 24 B` per frame *during a transform
> animation or scroll***. At 5 000 nodes that is 120 KB/frame — sub-millisecond `memcpy`, but it is
> per-frame while the scroll is live, **not** "0.1 ms damaged-only." Steady idle (no transform dirt) is
> O(changed) again. This is the documented, accepted cost of Cut B + composition-style scrolling.

`OQ-1`: a *subtree-scoped* WorldTransform copy (copy only the dirtied subtree's contiguous compacted
range, leaving the rest memcpy-clean from the back snapshot) reduces this to O(scrolled subtree). It
requires the compaction order to keep a subtree contiguous, which the record-walk order already wants —
fold it in if the 5k-node full copy ever shows on the publish budget. Default v1: full-column copy on any
transform dirt (simpler, correct, measured sub-ms).

### 3.3 Zero-alloc

The three slot arenas are `SnapshotArena` instances backed by
`GC.AllocateUninitializedArray<byte>(cap, pinned: true)` (per `dotnet10` §A), grown only on a new
high-water node count (geometric ×2, never shrunk, old buffer deferred-freed behind the consume index).
The per-frame copy is `Buffer.MemoryCopy` / `Span.CopyTo` over those arenas. **Zero managed allocation at
PUBLISH** in steady state.

---

## 4. The render-frame ordering invariant (phase 8 internal order)

The render thread, on each acquired `SceneFrame`, executes a **fixed internal order**. Getting this order
wrong is the difference between "redraw" and "use-after-free / stale-pixel." The invariant:

```
RENDER FRAME (one acquired SceneFrame):
  R0. DRAIN
       a. drain worker MPSC result ring → upload decoded textures via texture-staging ring,
          intern palette results, mark GlyphRun realizations ready. (TargetGen-guarded: stale dropped.)
       b. atlas eviction: compute the live-reference set FROM THE SNAPSHOT'S COMMAND STREAM INTENT
          (the GlyphRun/Image handles the snapshot will reference), evict only never-referenced slots,
          BUMP the realization content-epoch for any slot that moved/repacked.
       c. RETIRE: DrainRetired() — pop the deferred-delete ring entries whose keyed GPU fence has
          completed AND whose seq < _lastConsumedSeq, and ComPtr.Dispose them (§8). Pop CPU-slot
          retire-requests likewise. (Both fences cleared, ordered.)
  R1. RENDER-LOCAL EPOCH VALIDATION
       For each clean DrawList span we intend to reuse: compare the LIVE realization epoch (just
       possibly-bumped in R0b) against the per-span epoch RECORDED INTO THIS RENDER THREAD'S OWN
       BACK ARENA. Mismatch ⇒ the span is stale ⇒ re-record it. This comparison is RENDER-THREAD-LOCAL:
       both the live epoch and the recorded epoch are read by the render thread; ZERO cross-thread
       epoch staleness. (Contrast: a UI-captured epoch could be stale by the time render evicts.)
  R2. RECORD  (phase 8) — walk the snapshot topology; clean spans memcpy from the render-private BACK
       arena into the FRONT arena; dirty subtrees re-recorded; SortKeys into the parallel ulong[].
  R3. BATCH   (phase 9) — radix sort, overlap-aware coalesce, resolve glyph UVs now (atlas pinned).
  R4. SUBMIT  (phase 10) — SubmitDrawList → ExecuteCommandLists → Signal(GPU fence, thisSubmitValue).
  R5. PRESENT (phase 11) — latency-wait → Present → DComp Commit (incl. video hole-punch §10) →
       Volatile.Write(present-ack seq).
```

**Why DRAIN → evict → epoch-validate → record, in that order.** Eviction (R0b) may repack the atlas and
bump epochs; validation (R1) must therefore run **after** eviction so it sees the post-eviction epoch;
record (R2) must run **after** validation so a span invalidated by an evict is re-recorded, not reused.
The eviction liveness set is computed from the snapshot's command-stream *intent* (which handles this
frame references), so a glyph/image referenced by a clean span this frame is never evicted out from under
it. This is the keystone ordering fix from `hardened-v1-plan.md` §4.1.

---

## 5. CONSUME-GATED quarantine — `QuarantinePolicy` / `QuarantineLedger`

The original synthesis reclaimed SceneStore slots by *publish* count while the proof requires *consume*
count — a real use-after-free under DropOldest coalescing (UI publishes p+1, p+2 while render is still
reading the snapshot taken at p, whose columns alias slots the UI is about to reuse). Fixed:

### 5.1 The constant is derived, not magic

```csharp
public static class QuarantinePolicy
{
    // QUARANTINE = the maximum number of frames a snapshot can be IN FLIGHT before the producer
    // is guaranteed the consumer has moved past it. With one render thread reading one published
    // slot at a time, that is the render-in-flight depth.
    public const int RenderInFlightDepth = 1;          // one render thread, one consumed frame at a time
    public const int Quarantine = RenderInFlightDepth + 1; // +1 belt-and-suspenders slack

    // compile-time assertion that nobody hardcoded a "2"
    static QuarantinePolicy()
        => Debug.Assert(Quarantine == RenderInFlightDepth + 1, "quarantine must be derived");
}
```

`Quarantine` is **compile-asserted as a derived constant**, never a literal `2`. In the single-thread
build order (phase 1, §11) it is logically **0** because the UI thread both produces and consumes — no
slot is in flight across threads.

### 5.2 The reclaim rule (the keystone)

> A SceneStore slot freed during production of frame `p` is **reclaimable only when
> `_lastConsumedSeq > p`** — i.e. the render thread has acquired a strictly-later snapshot and therefore
> can no longer be reading the columns that aliased that slot. The render thread `Volatile.Write`s
> `_lastConsumedSeq` on each `TryAcquire` (release); the UI thread `Volatile.Read`s it (acquire) at the
> quarantine-tick.

```csharp
public sealed class QuarantineLedger
{
    private readonly ArenaRing<FreedSlot> _pending;     // (slabIndex, gen, freedAtSeq)

    // UI thread, during reconcile FreeNode:
    public void OnFreed(int slabIndex, uint gen, ulong currentPublishSeq)
    {
        ThreadGuard.AssertUi();
        _pending.Push(new FreedSlot(slabIndex, gen, currentPublishSeq));
    }

    // UI thread, once per frame at PUBLISH-tick (after Publish):
    public void Tick(SceneStore store, SceneFramePublisher pub)
    {
        ThreadGuard.AssertUi();
        ulong consumed = pub.LastConsumedSeq;            // Volatile.Read acquire
        while (_pending.TryPeek(out var s) && consumed > s.FreedAtSeq + (ulong)(QuarantinePolicy.Quarantine - 1))
        {
            _pending.Pop();
            store.ReclaimSlot(s.SlabIndex);              // NOW safe: the freeing snapshot is no longer in flight
        }
    }
}
```

The generation bump on `FreeNode` (Foundation §1) means any stale `NodeHandle` captured in an in-flight
snapshot fails `IsLive(slotGen)` validation even before reclaim — so the render thread reading a
quarantined-but-not-yet-reclaimed slot sees a *handle that no longer validates*, never live garbage. The
quarantine then guarantees the *bytes* are not overwritten until the snapshot referencing them is gone.
Belt (gen-fail) and suspenders (quarantine) together.

### 5.3 `ThreadGuard` + ledger = deterministic throw on violation

The `seam.race` test (`hardened-v1-plan.md` §4.5) pauses the render thread for 3+ publishes and asserts
**no slot is reclaimed** until `_lastConsumedSeq` advances. A reclaim before that point trips a
`QuarantineLedger` deterministic-throw in the `FGGUARD` build. This is the SAFE-by-construction guard for
the slot-reuse hazard.

---

## 6. The render-private DrawList arena ring (>= 3 deep)

The original synthesis left the 2-deep DrawList arenas swapped/reset by the **UI thread** while the
**render thread** memcpy'd clean spans from the back arena — a torn-DrawList race the single-thread v1
never had (the UI could reset/overwrite the back arena mid-memcpy). Fixed:

> **The render thread owns N >= 3 DrawList arenas. Record reads its OWN previous arena (the back). The UI
> thread NEVER swaps or resets a DrawList arena.** The front/back/in-flight roles rotate render-locally.

```csharp
// render-thread-PRIVATE
public sealed class DrawListArenaRing
{
    private readonly DrawListArena[] _arenas;   // N >= 3, each: byte[] cmds + ulong[] sortkeys, pinned
    private int _front, _back, _inFlight;       // indices; rotated by the render thread only

    public DrawListArena Front => _arenas[_front];   // record target this frame
    public DrawListArena Back  => _arenas[_back];    // clean-span source (previous frame's record)

    public void Rotate()                        // render thread, after present-submit
    {
        ThreadGuard.AssertRender();
        // the arena that was front becomes back; the arena whose GPU work is still in flight is held;
        // the freed third arena becomes the new front. >=3 guarantees a free arena always exists.
        _inFlight = _front;                     // its instance buffers may still be GPU-referenced
        (_front, _back) = (FreeArenaIndex(), _front);
    }
}
```

Why >= 3: at submit time the *front* arena's recorded commands feed instance buffers the GPU is still
reading (in flight until its fence completes); the *back* arena is the clean-span source for the next
record. A third arena is the one the next record writes into. With only 2 you would have to either block
on the GPU fence before recording (re-importing the stall) or overwrite an in-flight arena (corruption).
The §9 memory accounting in `hardened-v1-plan.md` reflects **3× arenas**.

The instance/vertex `UploadRing` (gpu-renderer §12) is double/triple-buffered on the same principle and
reset behind its own GPU fence — it is render-thread-private, so no cross-thread concern beyond the fence.

---

## 7. Realization epochs across the seam (brush/clip degenerate; glyph/path/image managed)

Clean-span reuse is valid IFF every referenced handle `IsLive` **AND** its realization content-epoch is
unchanged **AND** its baked-geometry hash is unchanged (`architecture-spec.md` §5.4). The threading
concern is *where the epoch is read*:

- **Brush/Clip tables are content-hash deduped** — content cannot change under a stable handle — so their
  "epoch" degenerates to `IsLive`. No cross-thread epoch needed. SAFE-by-construction.
- **GlyphRun / Path-tess / Image-atlas are mutated in place** (atlas repack, tessellation refresh, image
  re-decode). Their epoch is **bumped at the single `Mutate()` chokepoint** and the bump is observed
  **render-thread-locally** (§4 R1): the render thread captured the epoch when it recorded the span into
  its own back arena, and re-reads the live epoch (post-eviction) on the same thread. The UI never
  participates in this comparison — eliminating cross-thread epoch staleness.
- `CapturedEpochs` in the `SceneFrame` carries the **UI-side** view of the epoch arrays at publish, used
  only to decide *clean-node column memcpy* (§3.1). The *DrawList* clean-span decision is render-local
  (§4 R1). These are two distinct uses of the epoch and must not be conflated.

The `Mutate()` chokepoint and `CleanSpanWitness` (baked-geometry-hash validator) are owned by Scene/Render
(`architecture-spec.md` §5.4); this subsystem only fixes *which thread reads which epoch when*.

---

## 8. The retire-fence handshake — unifying CPU-slot + GPU-resource lifetime

Two orthogonal lifetime problems meet at split resources (a brush's GPU realization, a glyph atlas page,
a layer RT): the **CPU slot** must survive until no in-flight snapshot references it (UI→render,
quarantine §5), and the **GPU resource** must survive until no in-flight GPU submit references it
(render→GPU, the deferred-delete ring). A split resource must clear **both fences, ordered.**

```csharp
// render-thread-only (lives in FluentGpu.Windows D3D12/ leaf; the ONLY thread that Disposes a ComPtr)
public sealed class RhiHandleTable<TNative>
{
    private readonly DeferredDeleteRing _ring;   // entries keyed by (gpuFenceValue, sceneFrameSeq)

    // Called when the render thread learns (from the snapshot) a handle was freed at publish `p`.
    public void RetireDeferred(Handle h, ulong freedAtSeq)
    {
        ThreadGuard.AssertRender();
        // do NOT push to the GPU delete ring yet. Tag it; it becomes eligible only when BOTH:
        //   (1) _lastConsumedSeq > freedAtSeq        (CPU: no in-flight snapshot references the slot)
        //   (2) gpuFence >= maxInFlightSubmitValue   (GPU: no in-flight command list references the resource)
        _ring.Stage(h, freedAtSeq, requiredGpuFence: _maxSubmittedFenceValue);
    }

    public void DrainRetired(ulong lastConsumedSeq, ulong completedGpuFence)   // §4 R0c
    {
        ThreadGuard.AssertRender();
        while (_ring.TryPeekEligible(lastConsumedSeq, completedGpuFence, out var e))
        {
            _ring.Pop();
            e.Resource.Dispose();        // ComPtr.Release → 0; render-thread-confined refcount, safe
            // slot reclaim (CPU side) is the UI thread's QuarantineLedger.Tick (§5) — ordered after this is observed
        }
    }
}
```

**The ordered handshake, in words:** when the UI frees a split-resource handle at publish `p`, the
retire-request is tagged `seq = p`. The render thread does **not** push it onto the GPU delete ring until
`_lastConsumedSeq > p` (no snapshot still reads it) **and** keys the ring entry on the max in-flight
submit fence `>= p`'s submit (no command list still reads it). Only when *both* the consume index and the
GPU fence have passed does the `ComPtr` get `Dispose`d. This closes the premature-GPU-free-across-the-seam
window the critique found, and it mirrors the proven GPU deferred-delete pattern — which is exactly why
it is the *one* lock-free surface we are willing to keep (it reuses a pattern with a 30-year track record).

DXGI-owned back buffers are special-cased: they release via the swapchain on `ResizeBuffers`, never
through D3D12MA / the delete ring (gpu-renderer §12, architecture-spec §4.7).

---

## 9. Device-lost cross-thread rendezvous

Device-lost (TDR, GPU reset, driver upgrade, `DXGI_ERROR_DEVICE_REMOVED`) is detected on the render
thread but recovered cooperatively, because recovery touches retained CPU state the UI thread owns.

### 9.1 Detection (two sources, render thread + OS wait thread)

1. **Primary: synchronous HRESULT** from `Present()` / `Submit()` on the render thread →
   `Volatile.Write(ref _deviceLostReason, code)`.
2. **Backstop: `RegisterWaitForSingleObject`** on the device-lost fence (`fence → MaxValue` trick,
   gpu-renderer §17, architecture-spec §5.1). The callback runs on an **OS wait thread** and does
   **only** `Volatile.Write(ref _deviceLostReason, code)` + `RequestFrame()`. It **never** touches a
   `ComPtr` and never calls `GetDeviceRemovedReason` (that is the render thread's job). The `GCHandle` for
   the callback context is **Normal** (tied to Hosting lifetime), freed deterministically on teardown — no
   `Weak`/null-target race.

### 9.2 The rendezvous protocol

```
DETECT (render thread or wait thread): Volatile.Write(_deviceLostReason, code); RequestFrame();

UI THREAD phase 1 (pump): reads Volatile.Read(_deviceLostReason) ONCE. If set:
   - STOP publishing new SceneFrames (set a publisher pause flag the render thread observes).
   - Mark the WHOLE tree dirty (every node PaintDirty) — recovery will re-record everything.
   - Signal the render thread to enter RECOVER mode (a volatile recovery-request word).

RENDER THREAD, on observing RECOVER (before TryAcquire):
   1. WaitIdle / abandon in-flight (the device is gone; fences won't complete — use the recovery path,
      not WaitForSingleObject which would hang). Dispose the dead device + all ComPtr (render-thread-confined).
   2. Recreate device (next-best adapter → WARP fallback), recreate per-window swapchains + DComp visuals.
   3. RE-REALIZE GPU resources from retained CPU state: BrushTable/ClipTable re-upload (linear-premul
      recompute), GlyphAtlas re-rasterize from GlyphRunTable, image textures re-decode-or-re-upload from
      ImageRefTable, PSOs recompile from cached DXIL blobs, gradient atlas re-bake.
   4. Clear the recovery word (Volatile.Write).

UI THREAD: on observing recovery-cleared, resume publishing; the whole-tree-dirty frame re-records fully.
```

**Invariant (the reason this is tractable): the RHI stores only realizations; every GPU object is
reconstructible from CPU-side retained data.** SceneStore SoA is untouched by device-lost. The handle
*values* stay valid (the HandleTable slots persist; only the native `ComPtr` behind each is rebuilt), so
no managed-tree state is lost — recovery is a re-realization, not a reload.

The rendezvous uses a `System.Threading.Lock` (`dotnet10` §G) **only** for the off-hot-path
mode-transition handshake (pause/recover/resume), never on a per-frame path.

---

## 10. Present-tree, video hole-punch, and the present-ack (phase 11, render thread)

The DComp present-tree is a **multi-visual** tree owned by the render thread (architecture-spec §4.7,
WaveeMusic fold-in): the UI swapchain visual is z-above a child **video visual** supplied by the PAL
`IVideoPresenter` seam. `DrawVideoCmd` (sortkey below chrome) paints a **transparent premultiplied-0
hole** into the UI canvas so the video child visual shows through.

- The UI thread, at PUBLISH, records `PresentIntent` into the `SceneFrame`: video surface placements
  (`Place(id, deviceRect, opacity, z)`), and a `dcompCommitNeeded` flag (set only when a visual prop
  changed — the hole moved, a surface was created/destroyed, z reordered).
- The render thread, at R5, executes `IVideoPresenter.Place(...)` and the UI-swapchain `Present`, then
  issues **one DComp `Commit()`** that atomically lands both the new UI frame and the video placement —
  so the hole and the surface never tear apart across a frame.
- **FluentGpu never touches video pixels** (externally decoded — `MediaPlayerElement`/PlayReady). The
  heaviest continuous workload is off our threads by construction. This is why the video present-tree is a
  v1 deliverable that is *single-thread-friendly* (WaveeMusic §"v1-safe").

After present, the render thread `Volatile.Write`s the **present-ack seq** (the `PublishSeq` of the frame
just presented). The UI thread reads it at phase 1 / phase 12 to know which passive effects (frame N+1)
are now safe to run (`seq <= last-presented`, <= 1 frame latency).

The `IVideoPresenter`, `ISystemColors`, `IBackdropSource`, `IVirtualMemory`, `IImageCodec` PAL seams are
**owned by the PAL/Media docs**; this subsystem only pins *which thread calls them*: `IVideoPresenter`,
`IBackdropSource` (GPU bake), `IImageCodec` upload → **render thread**; `IImageCodec` *decode* → **worker
pool**; `ISystemColors`/`IVirtualMemory` → UI thread (reserve/commit at the SceneStore growth edge).

---

## 11. Backpressure policy

Two regimes, both off the lock-free hot path:

- **Normal — DropOldest coalesce.** The wakeup `Channel<seq>` is cap-1 DropOldest: if the render thread is
  behind, intermediate publish wakeups coalesce; the render thread always acquires the **latest** published
  slot (`_publishedIdx` is last-writer-wins), so it never renders a stale frame when a newer one exists.
  Frame-drop backpressure for free; newest frame wins. The triple buffer + quarantine make dropping safe
  (the dropped frame's slots are quarantined, not reused, until consumed).
- **Degenerate — bounded-T block then drop-frame.** If the render thread is *sustainedly* stalled (a real
  GPU present-wait after the latency-waitable saturates), the UI thread, when it cannot pick a free slot
  (all 3 occupied because none consumed), blocks for **at most T = one vsync** on a
  `System.Threading.Lock` + condition (off-hot-path), then **drops this frame's publish** and proceeds to
  phase 12. This is the irreducible coupling: you cannot render-ahead infinitely. Bounded, named, and the
  same ultimate limit WinUI's compositor has.

The `seam.race` soak **sweeps channel-capacity and reader-stall as fuzz parameters** — it must validate
the *relationship* (consume-gated reclaim holds under arbitrary stall), not hard-code quarantine=2
(`hardened-v1-plan.md` §4.5).

---

## 12. Interruptible reconcile on the UI thread — slice, carry-forward, AND discard-and-restart

Reconcile is **pure** (no `ISceneBackend`-observable mutation is visible across the seam until PUBLISH) — that purity
is FluentGpu's load-bearing asset (gap analysis §3, §5.4) and it is what makes *both* of the interrupt modes below
safe-by-construction rather than audited. This section pins the seam-side invariants for the two modes and the
**carry-forward effect/enqueue gen-stamp contract** that makes a partial pass consistent.

The hook-side machinery (lanes, `UseTransition`/`startTransition`, the phase-3 update queue, `RenderContext`
generation) is **owned by `reconciler-hooks.md`** (§6–§8); this subsystem references it and pins *the seam invariants
the work loop must hold* and *when effects may drain*.

### 12.1 The base mode — time-sliced, carry-forward, atomic-on-complete (default)

- **Time-sliced reconcile on the UI thread.** `ReconcileSlicer` is deadline-driven: it reconciles within a budget
  (`RenderPriorityPolicy` 16 ms), and if it overruns it **never publishes a half-built tree** — it carries the
  partially-reconciled scratch forward and completes next frame, publishing only when the diff is whole. Input dispatch
  (phase 2) hit-tests the **last-published-consistent** topology (a UI-owned `Bounds` double-buffer), never the
  in-flight reconcile.

```csharp
public ref struct ReconcileSlicer
{
    long _deadlineTicks;
    public bool ShouldYield() => Stopwatch.GetTimestamp() >= _deadlineTicks;
    // atomic-on-complete: caller only flips the published topology pointer when Done==true
}
```

### 12.2 P4 — INTERRUPTIBLE DISCARD-AND-RESTART reconcile (folded into core, not deferred)

The gap analysis (P4) left discard-restart deferred and only specified the *precondition*. Per the no-defer directive
it is now a **fully-specified core mode**. It is the React `workLoopConcurrent` shape — *safe here for exactly the same
reason*: reconcile is pure, and **nothing it produces is observable before PUBLISH**, so an in-progress reconcile can
be thrown away with zero rollback.

**The work loop.** Reconcile descends the dirty host path as a unit of work per node. Before each unit, the loop reads
the highest pending lane (owned by `reconciler-hooks.md`'s phase-3 update queue, exposed read-only here as
`ILaneScheduler.HighestPending`). If a **strictly-higher-priority lane** arrived than the lane this pass is rendering
(`_renderingLane`), the loop **abandons** the in-progress scratch and restarts from the dirty root for the new lane.

```csharp
// UI thread. The slicer + lane check are the only two interrupt sources.
public ref struct ReconcileWorkLoop
{
    long             _deadlineTicks;
    LaneSet          _renderingLane;       // the lane(s) this pass committed to render (from reconciler-hooks)
    ILaneScheduler   _lanes;               // read-only seam onto the phase-3 update queue
    ref ReconcileScratch _scratch;         // arena-backed, UI-private, NOT ISceneBackend

    public ReconcileOutcome Step()
    {
        ThreadGuard.AssertUi();
        while (_scratch.HasWork)
        {
            // INTERRUPT 1 — a higher-priority lane preempts: DISCARD and restart from root.
            if (_lanes.HasHigherPendingThan(_renderingLane))
                return ReconcileOutcome.DiscardRestart;   // caller resets scratch, re-picks lane, re-enters

            // INTERRUPT 2 — deadline: yield, CARRY the partial scratch forward (§12.1).
            if (Stopwatch.GetTimestamp() >= _deadlineTicks)
                return ReconcileOutcome.YieldCarryForward;

            _scratch.AdvanceOneUnit();      // pure: writes ReconcileScratch, never ISceneBackend
        }
        return ReconcileOutcome.Complete;   // ONLY now is PUBLISH eligible
    }
}

public enum ReconcileOutcome : byte { DiscardRestart, YieldCarryForward, Complete }
```

**Why DiscardRestart is free and safe (the invariant, stated as a guard):**

> **THE NO-MUTATION-BEFORE-PUBLISH INVARIANT.** Between the start of phase 5 (reconcile) and PUBLISH(13a), the
> reconcile work loop writes **only** `ReconcileScratch` (a UI-private, arena-backed structure: the keyed-LIS work
> buffers, the pending `CreateNode`/`Move`/`Remove` op list, the per-element `Diff` masks) — it does **NOT** call any
> `ISceneBackend.WriteX`/`CreateNode`/`DestroyNode`/`MarkDirty`. The backend ops are *staged* in scratch and *applied*
> in one burst only on `ReconcileOutcome.Complete`, immediately before layout+publish. Therefore a `DiscardRestart`
> simply resets the arena (`O(1)`); there is nothing committed to roll back, and **no half-mutated SceneStore can
> ever be snapshotted.**

This is the harder, hardened form of the existing "atomic-on-complete" rule (§12.1). The base mode already *delays*
the backend burst to completion; the discard mode *requires* it and additionally requires the scratch to be
restart-clean. Concretely: `CreateNode` slot picks are *reserved* (gen-bumped) but the columns are written into scratch
shadow rows, not the live SoA, until `Complete`; on `DiscardRestart` the reserved slots are returned to the free list
(no quarantine needed — they were never snapshotted).

**`ThreadGuard` enforcement of the invariant.** A new guard surface, `ThreadGuard.AssertNoBackendMutationInReconcile`,
arms a `[ThreadStatic] bool t_inReconcile` for the duration of the work loop; every `ISceneBackend` mutator opens with
`AssertNotMidReconcile()` so a stray live-SoA write *during* reconcile is a deterministic throw under `FGGUARD`, not a
silent tear that only discard-restart would expose:

```csharp
public static class ThreadGuard   // (extends §1.2)
{
    [ThreadStatic] private static bool t_inReconcile;

    [Conditional("FGGUARD")] public static void EnterReconcile() { AssertUi(); t_inReconcile = true; }
    [Conditional("FGGUARD")] public static void ExitReconcile()  { t_inReconcile = false; }

    // Called at the top of every live-SoA ISceneBackend mutator (NOT the scratch writers).
    [Conditional("FGGUARD")]
    public static void AssertNotMidReconcile()
    {
        if (t_inReconcile) throw new ReconcilePurityViolation();  // live SoA touched before the Complete burst
    }
}
```

So discard-restart safety is **two guards deep**: (1) the SoA mutators are gated by `AssertNotMidReconcile` (you
*cannot* mutate observable state mid-pass), and (2) the `ConcurrentRecord_MatchesSingleThreadedGolden` golden
(§18) is extended to a **discard-restart equality test**: a fuzz schedule that injects a higher-lane preemption at
every unit boundary must produce a final published `SceneFrame` **byte-identical** to the un-interrupted single-pass
result. Purity is thereby *checked*, since there is no managed TSan (§0).

**Edge cases.**
- *Starvation:* a continuously-arriving higher lane could discard forever. The lane scheduler (reconciler-hooks §7)
  caps consecutive discards; on the cap the loop **promotes to a blocking single-pass** for the current highest lane
  (ignores further preemption for that one pass) so forward progress is guaranteed. The seam only needs to honor the
  `ReconcileOutcome` it is handed — the promotion decision lives lane-side.
- *Interaction with carry-forward (§12.1):* the two interrupts are mutually exclusive per `Step()` — discard wins over
  yield (a higher lane invalidates the carried partial regardless), so a carried-forward partial is dropped the instant
  a higher lane appears. There is no "carry a partial of lane A while rendering lane B" state to reason about.
- *Effects enqueued in the discarded pass:* gated and dropped — see §12.4 (P5).

### 12.3 The seam-side external-store consistency contract (P3) — see §12bis

External-store tearing is a *seam* problem (a store mutates between snapshot and PUBLISH while reconcile is sliced), so
its full design is §12bis below. It shares this section's no-mutation-before-PUBLISH invariant: the pre-PUBLISH tear
re-check is the seam's last line before sealing the frame.

### 12.4 P5 — CARRY-FORWARD vs DISCARD effect/enqueue gen-stamp contract (folded into core)

Effects are enqueued during reconcile (phase 5) and drained at phase 6.5 (layout) / phase 12 (passive) — the
`EffectScheduler` and `EffectRef` are **owned by `reconciler-hooks.md` §4.3**. The seam concern the gap analysis (P5)
flagged as *unspecified* is now pinned: **what happens to effects enqueued in a pass that was carried-forward or
discarded before it completed.** The rule is a generation gate keyed on the **`RenderContext.Generation`** stamp
(reconciler-hooks §3, `ComponentSlotData.Generation`).

> **THE DRAIN-ONLY-AFTER-COMPLETE RULE.** `EffectScheduler.FlushLayout`/`FlushPassive` are invoked **only after a
> `ReconcileOutcome.Complete`** — never after `YieldCarryForward`, never after `DiscardRestart`. PUBLISH(13a) is the
> gate: phases 6/6.5/7/PUBLISH/12 run **iff** phase 5 returned `Complete` this frame. A frame that only sliced or
> discarded re-requests frame N+1 and runs **none** of layout/effects/publish.

That alone handles the common case. The gen-stamp handles the **straddle case** the gap analysis named precisely: an
effect enqueued during frame N's *partial* (carried-forward) pass whose owning component **re-renders in frame N+1's
completion** — the effect cell may have new deps, or the component may have unmounted. Without a gate, the phase-6.5/12
drain would run a stale effect body closed over a superseded `RenderContext` state.

**Mechanism.** Each `EffectRef` carries the `RenderContext.Generation` captured at enqueue. The component's generation
is bumped (reconciler-hooks §3) every time `BeginRender` runs for that slot. At drain, an `EffectRef` whose captured
generation **≠** the cell owner's *current* generation is **skipped and its `PendingCleanup` is still run** (the
cleanup must fire even for a superseded effect, matching React's cleanup-before-create discipline):

```csharp
// EffectScheduler.Drain — the gen-stamped form (reconciler-hooks §4.3 owns the type; this is the seam-pinned rule)
internal readonly record struct EffectRef(RenderContext Ctx, int Cell, uint EnqueuedGen);

private static void Drain(List<EffectRef> q)
{
    // PHASE A — cleanups ALWAYS run (even superseded), cleanup-before-create.
    for (int i = 0; i < q.Count; i++) q[i].Ctx.RunPendingCleanup(q[i].Cell);
    // PHASE B — new effect bodies run ONLY for the current generation; straddle/discarded ones are skipped.
    for (int i = 0; i < q.Count; i++)
        if (q[i].Ctx.Generation == q[i].EnqueuedGen)        // gen-stamp gate
            q[i].Ctx.RunPendingEffect(q[i].Cell);
    q.Clear();
}
```

**Carry-forward semantics, pinned:**
- A partial pass that yields (`YieldCarryForward`) **does not drain** — its enqueued `EffectRef`s sit in the scheduler
  lists across the frame boundary. When N+1 completes the *same* lane, the component either (a) did not re-render → the
  cell's generation is unchanged → the effect runs normally, or (b) re-rendered → generation bumped → the straddled
  `EffectRef` is gen-skipped at drain and the *fresh* `EffectRef` (enqueued in N+1 with the new gen) runs instead. No
  double-run, no stale-state run.
- A **discarded** pass's enqueued `EffectRef`s are likewise carried in the lists but are guaranteed gen-mismatched
  after the restart re-renders their owners (or the owner unmounted → its slot generation advanced on free → mismatch);
  the cleanup still fires. **The scheduler lists are never speculatively cleared on discard** — clearing is the drain's
  job and the drain only runs after Complete, so the gen-gate is the single source of truth. This avoids a second
  lock-free surface (no cross-pass list surgery).
- **Unmount during a discarded/partial pass:** unmount cleanups run **synchronously during reconcile remove**
  (reconciler-hooks §4.4) — but in the discard mode those removes are *staged in scratch* (§12.2), so the synchronous
  cleanup is itself staged and only executes in the `Complete` burst. A discarded pass therefore runs **zero** unmount
  cleanups for nodes it never committed — correct, because those nodes were never observable. This is the one place the
  "cleanups run synchronously in phase 5" rule (reconciler-hooks §4.4) must be read as "synchronously **in the Complete
  burst**," and this subsystem pins that reading.

**Honest scope.** The gen-stamp + drain-only-after-Complete is a **single-thread (UI) contract** — no cross-thread
fence is added; it is ordinary UI-thread sequencing. It does not widen the one lock-free surface (§0).

### 12.5 Opt-in / spike-gated / MANAGED: off-thread reconcile

Moving `Component.Render()` + diff to a worker is React-Fiber-class work. The discard-restart loop (§12.2) is its
on-the-UI-thread precursor and shares its invariant. Off-thread reconcile remains **opt-in / spike-gated / MANAGED**
(it adds a third lock-free surface and a deep-immutability proof obligation): when it lands it requires the snapshot to
**copy** Element inputs into worker-owned arena slices (not `ReadOnlyMemory` views over aliasable arrays — the
deep-immutability analyzer cannot prove non-aliasing otherwise), and it inherits §12.2's no-mutation-before-PUBLISH
invariant unchanged (the worker writes only its private `ReconcileScratch` copy).

---

## 12bis. External-store consistency across the seam (P3) — `IExternalStore` + frame-start snapshot + pre-PUBLISH tear re-check

**P3 folded into core (was: "specify-now / dormant in v1").** `UseObservable`/`UseResource` read **live external
sources** (an `IObservable<T>`, a model object, a cache). In the single-pass v1 there is no tearing window; the instant
reconcile slices (§12.1) or discard-restarts (§12.2), a store that mutates *between* the read at frame start and PUBLISH
can **tear** — two consumers in the same frame observe two different store states, and the published `SceneFrame`
encodes an inconsistent tree. This is React's `useSyncExternalStore` problem, and the fix is the same shape, pinned at
*our* seam: a **frame-start immutable snapshot** + a **pre-PUBLISH tear re-check** that demotes to a blocking single
pass on mismatch.

The corpus already ships the proven instance of this shape: **`ISystemColors`** is volatile-published, written only on
OS change, read **by-value-on-demand** through a stable instance carrying an **`Epoch`** change-trigger — i.e. a
`(getSnapshot, version)` pair, with `Context<uint>`-over-`Epoch` as the boxless change-check (reconciler-hooks §8,
SPEC-INDEX color row references the `ISystemColors` seam). §12bis **generalizes that exact `Epoch`+by-value-snapshot
shape** to arbitrary stores.

### 12bis.1 The contract (`IExternalStore<TSnapshot>`)

The hook surface (`UseObservable`/`UseResource` signatures, the cell layout) is **owned by `reconciler-hooks.md`
(data-hooks)**; this subsystem owns the **seam contract**: the snapshot must be immutable, frame-start-stable, and
re-checkable just before PUBLISH.

```csharp
// Owned here as the SEAM contract; the hook that consumes it is reconciler-hooks data-hooks.
public interface IExternalStore<TSnapshot> where TSnapshot : struct   // value snapshot: blittable, no tear by aliasing
{
    /// Coarse monotonic version, like ISystemColors.Epoch. Cheap to read; bumped on ANY store mutation.
    /// MUST be readable from the UI thread without blocking the store's writer.
    uint Version { get; }

    /// Returns an IMMUTABLE by-value snapshot of the store as of the call. Pure; no side effects.
    /// Generalizes ISystemColors' "read the 112B SystemTint by value on demand."
    TSnapshot GetSnapshot();

    /// Register a change callback that marks the consuming component dirty + requests frame N+1.
    /// The callback marshals onto the UI thread via IPlatformAppLoop.Post (reconciler-hooks setState path).
    IDisposable Subscribe(StoreChangedCallback onChanged);
}

public readonly record struct StoreChangedCallback(IReRenderSink Sink, NodeHandle Owner);
```

**Why a `struct TSnapshot` by-value.** A reference snapshot can be mutated under the reader (the `ISystemColors`
lesson: the 112-byte `SystemTint` is read by value precisely so a consumer cannot alias the live mutable record). The
contract therefore *requires* the snapshot to be a value type the consumer copies; for stores whose state is genuinely
a large object graph, the store must expose an immutable projection (a frozen DTO / a `ChunkedArena`-backed POD view)
as `TSnapshot` — the read-side immutability is the store author's obligation, asserted by the deep-immutability
analyzer where the snapshot crosses into hook state.

### 12bis.2 Frame-start snapshot + version capture (UI thread, phase 4)

When a component reads `UseObservable(store)`/`UseResource(store)` during phase 4 (render), the hook:
1. reads `store.Version` (cheap, coarse) → `cell.CapturedVersion`,
2. calls `store.GetSnapshot()` → `cell.Snapshot` (the value the component renders against this frame),
3. caches `(CapturedVersion, Snapshot)` so **all reads of this store within this frame return the same snapshot**
   (frame-start stability — the per-frame referential cache `useSyncExternalStore` relies on). A second consumer of the
   same store this frame gets the *cached* snapshot, never a fresh `GetSnapshot()`, so two consumers can never tear
   *within* a single render pass.

These captures are recorded into a UI-thread-private **`StoreReadLedger`** for the frame:

```csharp
// UI-thread-private; reset each frame (arena-backed, zero-alloc steady state).
internal sealed class StoreReadLedger
{
    private readonly ArenaList<StoreRead> _reads;   // (storeId, capturedVersion, IExternalStoreVersioned)
    public void Record(int storeId, uint version, IExternalStoreVersioned store) { ThreadGuard.AssertUi(); /* dedup by storeId */ }
    public bool AnyVersionMoved()                   // pre-PUBLISH re-check (§12bis.3)
    {
        ThreadGuard.AssertUi();
        foreach (ref readonly var r in _reads)
            if (r.Store.Version != r.CapturedVersion) return true;   // a store mutated mid-frame
        return false;
    }
    public void Reset() => _reads.Clear();          // O(1) at frame end
}
```

`IExternalStoreVersioned` is the non-generic base exposing only `uint Version` so the ledger can re-check heterogeneous
stores without boxing the snapshot type.

### 12bis.3 The pre-PUBLISH tear re-check (the seam's last line)

Immediately **before** `SceneFramePublisher.Publish` (the very end of phase 7, before 13a), the UI thread runs the tear
re-check. This is the `useSyncExternalStore` "re-read in commit, bail if changed" mechanism, placed at *our* commit
point (PUBLISH):

```csharp
// UI thread, end of phase 7, immediately before PUBLISH(13a).
if (reconcileWasSliced && _storeLedger.AnyVersionMoved())
{
    // A store mutated between frame-start snapshot and now, AND this frame was not a single atomic pass.
    // DEMOTE to a blocking single-pass frame: re-render this frame synchronously to completion with the
    // fresh snapshots, ignoring the slice deadline, so the published tree is internally consistent.
    RerunFrameBlockingSinglePass();   // re-enter phase 4 with deadline=∞ and lane preemption disabled
    // then fall through to PUBLISH with a tear-free tree.
}
```

**Why gated on `reconcileWasSliced`.** If the frame was a single uninterrupted pass (the common case, and *always* the
case in single-thread build step 1), the snapshot was captured and the tree built with no yield in between, so a
version move that happened entirely after the pass simply schedules frame N+1 via the `Subscribe` callback — there is
**no tear to fix** and the re-check is skipped. The expensive blocking re-render is paid **only** when (a) slicing or
discard-restart actually split the pass across a store mutation **and** (b) a version moved. This keeps the contract
**dormant-cost in v1** (gap analysis: "dormant in v1's single-pass model") while making it *correct* the moment slicing
flips on — exactly the latent-tear the gap analysis warned retrofitting would miss.

**Thread story.** Everything in §12bis is **UI-thread-confined**: snapshot capture, the ledger, the re-check, the
blocking re-render all run on the UI thread in phases 4–7. `store.Version` is a single volatile word the store's writer
(possibly a worker or OS thread, e.g. `ISystemColors` on the OS theme-change thread) publishes with a release store and
the UI reads with an acquire load — this is the **same both-directions-volatile discipline as the publisher** (§2) and
adds **no new lock-free surface**: it reuses the existing "one volatile version word per store" pattern that
`ISystemColors.Epoch` already validates. The snapshot bytes themselves never cross a thread mutably (value-copy).

**Zero-alloc.** `StoreReadLedger` is arena-backed and reset O(1) per frame; `GetSnapshot()` returns a value type into a
hook cell (no box); the re-check is a pointer walk over the ledger. Zero managed allocation on the steady-state path.

**macOS boundary.** Pure C# over POD + one volatile word per store — recompiles unchanged. `ISystemColors` itself is a
PAL seam (owned by theming/PAL) whose macOS backing differs, but the `IExternalStore`/ledger/re-check shape is
platform-agnostic.

---

## 13. The worker pool

```csharp
public sealed class WorkerPool
{
    // owned threads, NOT ThreadPool — predictable priority, no starvation under GC, AllocScope-wrapped.
    private readonly Thread[] _threads;        // N = clamp(ProcessorCount-2, 2..6)
    private readonly MpScRing<WorkerJob> _in;  // UI posts jobs (struct, no closure)
    private readonly MpScRing<WorkerResult> _out; // render thread drains at phase-8 DRAIN
}
```

- Jobs are **pure functions over immutable inputs → POD results by handle**: image decode (`IImageCodec`),
  palette extraction, page-fetch, cold path tessellation, and (v2) glyph rasterization. They touch **no**
  SceneStore, **no** RhiTable, **no** GPU fence, **no** `ComPtr`. Decode produces a pixel buffer the
  **render thread** uploads via `CopyBufferToTexture` + the texture-staging ring (architecture-spec §4.7).
- Each job carries a **`uint TargetGen`** (now-playing/visible-window sequence). The render thread drops a
  result whose `TargetGen` is stale at dequeue — fixing the artist-nav/scroll-recycle flicker class
  (WaveeMusic §"Cancellation").
- Results land in a lock-free **MPSC** ring (multi-producer workers, single-consumer render thread). The
  ring is the second-and-last lock-free surface; it is a textbook MPSC and is fuzz-tested alongside the
  publisher.
- Off-thread `setState` (a worker `Post`ing back) marshals onto the UI thread via
  `IPlatformAppLoop.Post<TState>(state, staticCallback)` (struct-state, no closure box) and schedules
  **frame N+1** — there is no synchronous same-frame apply (architecture-spec §5.6).

---

## 14. Where each phase lands — the final thread map

| Phase | Thread | This subsystem's role |
|---|---|---|
| 1 pump | UI | read `_deviceLostReason` + present-ack seq (single Volatile words) |
| 2 input-dispatch | UI | hit-test last-published-consistent topology; never the in-flight snapshot |
| 3 hook-flush | UI | — |
| 4 render | UI | gated by memo skip (owned by Reconciler); `UseObservable`/`UseResource` capture frame-start store snapshot+version into `StoreReadLedger` (§12bis) |
| 5 reconcile | UI | `ReconcileWorkLoop` — slice/carry-forward **and** discard-and-restart (§12.2); `EnterReconcile`/`ExitReconcile` guard arms; backend ops **staged in scratch**, applied only on `Complete`; `QuarantineLedger.OnFreed` on every committed `FreeNode`. **Phases 6→PUBLISH→12 run iff this returned `Complete`** (§12.4) |
| 6 layout | UI | runs only after reconcile `Complete` |
| 6.5 layout-effects | UI | `EffectScheduler.FlushLayout` — gen-stamp gated (§12.4); drains only after a complete reconcile |
| 7 animation | UI | compose `WorldTransform[]` (the §3.2 copy budget originates here); **end of 7: pre-PUBLISH tear re-check** `_storeLedger.AnyVersionMoved()` → blocking single-pass on mismatch (§12bis.3) |
| **PUBLISH (13a)** | **UI** | `SceneFramePublisher.Publish` + `SnapshotColumns.CopyInto` + `QuarantineLedger.Tick` + `StoreReadLedger.Reset` |
| 8 record | **RENDER** | DRAIN → evict → render-local epoch validation → record (the §4 invariant) |
| 9 batch | **RENDER** | radix sort, overlap-aware coalesce, glyph UV resolve |
| 10 submit | **RENDER** | `SubmitDrawList` → `Signal(fence)` |
| 11 present | **RENDER** | latency-wait → Present → `IVideoPresenter.Place` → DComp Commit → present-ack |
| 12 passive-effects | UI | frame N+1, for `seq <= present-ack`; `EffectScheduler.FlushPassive` — gen-stamp gated (§12.4) |
| (13 arena-swap) | **RENDER** | `DrawListArenaRing.Rotate` + `UploadRing` reset behind fence; **not** a UI-thread step in the seam build |
| (background) | WORKER | decode/palette/fetch jobs; results drained at phase-8 DRAIN |

Note the divergence from the single-thread spec's "phase 13 arena swap on the UI thread": with the seam,
the DrawList arena rotation is **render-local** (§6) and happens after submit on the render thread; the UI
thread's per-frame arenas (DSL/reconcile scratch) reset independently on the UI thread.

---

## 15. Zero-alloc + thread-confinement story (summary)

- **Publish path:** `SnapshotColumns.CopyInto` is `Span.CopyTo`/`Buffer.MemoryCopy` over pinned,
  uninitialized, geometric-grow arenas — **0 managed allocations** in steady state. The triple `SceneFrame`
  slots and 3 snapshot arenas are allocated once; growth is rare and deferred-freed behind the consume
  index.
- **Render path:** record/batch/submit walk POD over render-private arenas via the
  `Walk<TSink> where TSink : IDrawSink, allows ref struct` devirtualized walk (`dotnet10` §B) — **0
  allocations**. The retire/quarantine rings are arena-backed.
- **Worker path:** each job is `AllocScope`-wrapped; results are POD in the MPSC ring. Decode buffers are
  `ArrayPool<byte>` rented and returned after upload (edge, not phases 8–13).
- **Locks:** `System.Threading.Lock` only off the hot path — device-lost rendezvous, degenerate
  backpressure block. The hot publish/consume/retire path is lock-free Volatile + the GPU fence.
- **COM confinement:** every `ComPtr` is render-thread-confined; the `SceneFrame` carries only POD handles;
  no `ComWrappers` on any hot path (`dotnet10` §4). The `FGCOM` analyzer pins each COM object to one thread.

---

## 16. Failure / edge cases

- **Render thread paused 3+ publishes** (debugger, GPU stall): wakeups coalesce; UI quarantine grows but
  is bounded by the publisher's 3 slots — at the 4th un-consumed publish the UI hits the degenerate
  bounded-T block (§11) and drops the frame. No reclaim occurs (`_lastConsumedSeq` static) → no UAF.
- **Worker result arrives after its node was unmounted / recycled:** `TargetGen` mismatch at dequeue →
  dropped. The decoded buffer is returned to `ArrayPool`. No stale upload.
- **Atlas eviction wants to evict a slot a clean span references:** impossible — the eviction liveness set
  is computed from the snapshot's command-stream intent (§4 R0b); referenced slots are pinned.
- **Forgotten epoch bump (split resource mutated without `Mutate()`):** caught by the independent
  content-byte oracle (hash of realization backing bytes) in CI fault-injection, and by the render-local
  epoch comparison degrading to "redraw-not-corrupt" (worst case a stale-pixel frame, CI-caught, never a
  crash) — `hardened-v1-plan.md` §3 ledger.
- **Bounds-move-without-PaintDirty:** the `CleanSpanWitness` records a baked-geometry hash and the
  validator recomputes dest rects from current `Bounds[]/WorldTransform[]` — a span whose device-space
  geometry moved is re-recorded even if its handle/epoch are unchanged (owned by Scene §5.4; this
  subsystem ensures the validation runs render-local at §4 R1).
- **Device-lost during recovery (double fault):** the recovery executor retries with the next adapter →
  WARP; if WARP also fails, clean fatal exception (not corruption). SceneStore intact.
- **Publisher slot picker cannot find a free slot:** unreachable with 3 slots and <=2 occupied; an
  `FGGUARD` assert fires if it ever does (a logic bug, not a race).
- **`Channel<seq>` write fails (full):** benign — DropOldest means the latest seq is what matters and the
  render thread re-reads `_publishedIdx` on its next loop regardless of the channel.

---

## 17. Cross-platform (macOS) boundary

The seam is **almost entirely portable.** `SceneFramePublisher`, `SnapshotColumns`, `QuarantineLedger`,
`ThreadGuard`, `DrawListArenaRing`, `ReconcileSlicer`, `WorkerPool`, the present-ack and device-lost
*protocol* are pure C# over POD and Volatile — they live in `FluentGpu.Hosting`/`FluentGpu.Render` and
recompile unchanged on macOS.

What changes below the seam (leaf-only):
- **GPU fence / deferred-delete ring / retire-fence handshake:** D3D12 `ID3D12Fence` →
  Metal `MTLSharedEvent` / `MTLCommandBuffer` completion handler. The *handshake shape* (stage on consume
  index + GPU completion, ordered Dispose) is identical; only the fence primitive differs.
- **Present-tree + hole-punch:** DComp multi-visual → CoreAnimation `CALayer` tree with a
  `CAMetalLayer` sibling for video. `IVideoPresenter` is the seam; the render-thread placement call is the
  same.
- **Device-lost:** DXGI `DEVICE_REMOVED` + `RegisterWaitForSingleObject` → Metal device-removal
  notification (`MTLDeviceNotificationHandler`). The render-thread-confined recovery + re-realize-from-CPU
  invariant is identical.
- **Render thread affinity:** `Thread` + name + (Windows) `SetThreadDescription` / quality-of-service →
  macOS QoS class `USER_INTERACTIVE`. Same `Thread` API.
- **Worker pool:** fully portable (owned `Thread`s).

The macOS port inherits the entire confinement table and the one lock-free handshake unchanged. There is
**no managed TSan on either platform** — the MANAGED-sampled caveat (§0) is cross-platform.

---

## 18. Test ledger (what makes the MANAGED surface trustworthy)

| Test | What it proves | Build |
|---|---|---|
| `Render_NeverObserves_RecycledSlot` | pause render 3+ publishes; assert no slot reclaimed until `_lastConsumedSeq` advances; assert in-flight snapshot handles fail `IsLive` after free | FGGUARD |
| `ConcurrentRecord_MatchesSingleThreadedGolden` | 10^6-frame schedule-fuzz; render-thread output **byte-identical** to the single-thread golden | FGGUARD |
| `seam.race` soak (SPIKE) | **sweeps** channel-cap + reader-stall as fuzz params; ThreadGuard + QuarantineLedger deterministic-throw stay silent; nightly streak required before flipping quarantine 0→derived | FGGUARD, nightly |
| `Publisher_HappensBefore` | inject a delay between the column store and the `_publishedIdx` release; assert the acquirer never reads a torn column (stress + relacy-style schedule enumeration where feasible) | FGGUARD |
| `RetireFence_BothFences` | free a split resource at seq p with GPU work in flight; assert `Dispose` happens only after `_lastConsumedSeq > p` AND the keyed fence completes | FGGUARD |
| `DeviceLost_Rendezvous` | inject `DEVICE_REMOVED` on the render thread mid-frame; assert UI pauses publish, render re-realizes from CPU, no `ComPtr` touched off the render thread, tree intact | FGGUARD |
| `DiscardRestart_MatchesSingleThreadedGolden` (P4) | inject a higher-lane preemption at **every unit boundary** of phase 5; assert (a) `AssertNotMidReconcile` never throws (no live-SoA write mid-pass), (b) reserved-but-discarded slots are returned without quarantine, (c) the final published `SceneFrame` is **byte-identical** to the un-interrupted single-pass golden | FGGUARD |
| `CarryForward_EffectGenStamp` (P5) | enqueue an effect in a carried-forward partial pass, force the owner to re-render in N+1's completion; assert the straddled `EffectRef` is gen-skipped at drain, its `PendingCleanup` still ran, and the fresh `EffectRef` ran exactly once (no double-run, no stale-state run); assert FlushLayout/FlushPassive ran **only** after a `Complete` reconcile | FGGUARD |
| `ExternalStore_NoTear` (P3) | two consumers of one `IExternalStore`; mutate the store's `Version` between a sliced pass's two unit boundaries; assert both consumers rendered the **same** frame-start snapshot, the pre-PUBLISH re-check tripped `AnyVersionMoved`, the frame demoted to a blocking single pass, and the published tree is internally consistent. Single-pass (unsliced) variant asserts the re-check is **skipped** (dormant cost) | FGGUARD |
| render-thread present-stall bench | sustained GPU stall; assert UI input latency stays bounded and quarantine is non-growing (drops frames, doesn't leak) | perf gate |

The honest line repeated for the record: these are **samples + a golden + schedule-fuzz**, not a proof.
The one lock-free fence's acquire/release correctness rests on review + the above corpus. We bought safety
back by shrinking the lock-free surface to exactly the publisher handshake (+ the MPSC worker ring), both
reusing patterns with long track records.

---

## 19. Build order (safety is never speculative)

This subsystem ships **single-thread-correct first** and flips parallelism only behind a green race gate
(`hardened-v1-plan.md` §6):

1. **Single-thread, behind the snapshot SHAPE — *with the no-mutation-before-PUBLISH invariant + the P3/P5
   contracts landed from day one*.** Build `SceneFramePublisher` / `SnapshotColumns` / `QuarantineLedger` and run
   them **single-threaded**: the UI thread both `Publish`es and `TryAcquire`s; `Quarantine = 0` logically. Land
   **(P5)** the drain-only-after-`Complete` rule + the `EffectRef` gen-stamp (§12.4), **(P3)** the `StoreReadLedger`
   capture + pre-PUBLISH tear re-check (§12bis — dormant-cost, but the *hook shape* and the ledger are present so
   nothing retrofits), and the `EnterReconcile`/`AssertNotMidReconcile` guard arming (§12.2) — even though slicing is
   off, the SoA mutators are gated now so the invariant is true before any interrupt exists. All CI gates green here,
   including `ExternalStore_NoTear` (single-pass variant) and `CarryForward_EffectGenStamp`.
2. **(COM hardening + validation spine land here — owned by other docs; this subsystem depends on them
   being green before any thread spawns.)**
3. **Turn on time-slicing + carry-forward, then DISCARD-AND-RESTART (P4), still single render-consumer.** Enable
   `ReconcileWorkLoop` slicing (§12.1) and the discard-restart interrupt (§12.2) keyed off the reconciler-hooks lane
   scheduler. This is where `DiscardRestart_MatchesSingleThreadedGolden` and the sliced variant of
   `ExternalStore_NoTear` must go green — both run before the render thread is even spawned, because P4/P3 are
   **UI-thread contracts**. (Discard-restart is correct here precisely because the backend burst is staged in scratch
   and nothing is observable across the seam yet.)
4. **Spawn the render thread; keep quarantine=0 via single-consumer.** Move record/batch/submit/present to
   the render thread reading the published snapshot; migrate `ComPtr` ownership to it; install the >=3
   render-private arenas and the retire-fence handshake for slot recycling. Run **with the render thread
   but no slot reuse in flight yet** (force-sync drain).
5. **Flip quarantine 0 → derived depth, under the race gate.** Only after `seam.race` (swept params) +
   `ConcurrentRecord_MatchesSingleThreadedGolden` are green for the required nightly streak. Add the
   present-stall bench.
6. **Off-thread tessellation + glyph raster** (quarantine >= 2 proven): copy-isolated worker arenas + the
   deferred-free slab ring + per-worker AllocScope.
7. **Off-thread reconcile (opt-in), Metal fence/present-tree.** Spike-gated, MANAGED, not default — inherits the
   §12.2 no-mutation-before-PUBLISH invariant verbatim (worker writes only its private `ReconcileScratch`).

> **The correct single-thread v1 is genuinely safer than the seam until snapshot lifetime, bounded
> backpressure, consume-gated quarantine, and the retire-fence are all landed and `seam.race` (swept) is
> green.** That is why we ship single-thread first.

---

## 20. Changed vs the original synthesis (amendments folded)

- **Quarantine is CONSUME-gated, not publish-gated.** The original reclaimed slots by publish count while
  the proof requires consume count — a real UAF under DropOldest coalescing. Now: reclaim only when
  `_lastConsumedSeq > freedSeq`; `Quarantine = RenderInFlightDepth + 1`, **compile-asserted**, never a
  magic `2`.
- **DrawList arenas are render-thread-PRIVATE and >= 3-deep.** The original let the UI thread swap/reset
  2-deep arenas while the render thread memcpy'd clean spans from the back arena — a torn-DrawList race.
  Now the render thread owns N >= 3 arenas and rotates them locally; the UI thread never touches one.
- **Both-directions-volatile publish.** The original published with a one-directional release. Now the UI
  thread `Volatile.Read`s `_consumeIdx` (acquire) before picking a free slot, so it can never overwrite the
  slot the render thread is reading.
- **`Channel<SceneFrame>` demoted to a cap-1 DropOldest WAKEUP signal**; the triple buffer is the data path
  and **slot recycling is the retire-fence handshake**, not the channel's drop callback.
- **Render-frame ordering invariant pinned: DRAIN → atlas-evict (bump epochs) → render-LOCAL epoch
  validation → record.** Eviction liveness is computed from the snapshot's command-stream intent; epoch
  comparison is render-thread-local (the recorded epoch lives in the render's own back arena), eliminating
  cross-thread epoch staleness.
- **Split-resource retire deferred behind BOTH fences, ordered** (consume index AND GPU fence) — closes the
  premature-GPU-free-across-the-seam window.
- **WorldTransform copy budget corrected to up-to-full-tree (`NodeCount × 24 B`) during scroll/transform
  animation**, not "0.1 ms damaged-only." `OQ-1` offers a subtree-scoped reduction.
- **Device-lost is a cross-thread rendezvous**: the OS wait-thread callback only `Volatile.Write`s a reason
  word + `RequestFrame()`; primary detection is the synchronous HRESULT; recovery is render-thread-confined
  and re-realizes from retained CPU state. Normal (not Weak) `GCHandle`.
- **Backpressure named**: DropOldest coalesce (normal) / bounded-T-block then drop-frame (degenerate,
  T = one vsync). `seam.race` **sweeps** channel-cap + reader-stall rather than hard-coding quarantine=2.
- **Off-thread tessellation/glyph-raster DESCOPED from the single-thread phase and sequenced behind the
  seam** (quarantine >= 2) — the original wrongly claimed they were safe at quarantine=0.
- **Time-sliced reconcile defaults to atomic-on-complete on the UI thread**; off-thread reconcile is
  opt-in/spike-gated/MANAGED.
- **INTERRUPTIBLE DISCARD-AND-RESTART reconcile (P4) is now CORE, not deferred.** The §12.1 base mode is joined by a
  fully-specified `ReconcileWorkLoop` that abandons an in-progress pass when a higher lane arrives — safe because the
  backend ops are staged in `ReconcileScratch` and nothing is observable before PUBLISH. Pinned with the
  no-mutation-before-PUBLISH invariant, the `AssertNotMidReconcile` guard, and a byte-identical discard-restart golden.
- **External-store consistency (P3) is now CORE**: `IExternalStore<TSnapshot>` (`Version` + by-value `GetSnapshot` +
  `Subscribe`), generalized from the proven `ISystemColors` Epoch shape; frame-start snapshot capture into a UI-private
  `StoreReadLedger`; a pre-PUBLISH tear re-check that demotes to a blocking single pass on a version-move-under-slicing.
  Dormant-cost in single-pass v1, correct the instant slicing flips on. No new lock-free surface (one volatile version
  word per store, the `ISystemColors` discipline).
- **Carry-forward effect/enqueue gen-stamp contract (P5) is now CORE**: drain-only-after-`Complete`; `EffectRef`
  gen-stamped with `RenderContext.Generation`; straddled/discarded effects gen-skipped at drain while cleanups still
  fire. A pure UI-thread contract — no fence added.
- **Multi-visual DComp present-tree + video hole-punch + present-ack** folded in from the WaveeMusic
  requirements; `IVideoPresenter`/`IBackdropSource`/`IImageCodec` thread-assignment pinned (decode→worker,
  upload/place→render).
- **Honesty preserved**: the one lock-free fence is MANAGED-sampled (no managed TSan); all guards are
  `[Conditional("FGGUARD")]`-erased from shipping AOT, so production safety == CI coverage. P3/P4/P5 add **zero** new
  lock-free surfaces — they are UI-thread sequencing + the existing one-volatile-word-per-store pattern.

---

## 21. Implemented from the gap analysis

This doc folds the following `core-fundamentals-gap-analysis.md` items **into core** (no v2 deferral):

| Gap | Was (gap analysis) | Now (this doc) | Where |
|---|---|---|---|
| **P3 — External-store consistency** (`useSyncExternalStore`-analog on `UseObservable`/`UseResource`) | "Specify-now; dormant in v1; latent the moment slicing flips on; painful to retrofit" | Full core design: `IExternalStore<TSnapshot>` `(Version, GetSnapshot, Subscribe)` generalized from the `ISystemColors` Epoch + by-value-snapshot shape; frame-start `StoreReadLedger` capture; **pre-PUBLISH tear re-check** demoting to a blocking single pass on version-move-under-slicing | §12bis (+ §12.3, phase map §14, build step 1/3, test `ExternalStore_NoTear`) |
| **P4 — Interruptible discard-and-restart reconcile** | "Defer feature; enforce precondition now" | Fully designed `ReconcileWorkLoop` work loop that abandons an in-progress pass on a higher-lane arrival; the **no-`ISceneBackend`-mutation-before-PUBLISH invariant** + `ThreadGuard.AssertNotMidReconcile` + a byte-identical discard-restart golden | §12.2 (+ phase map §14, build step 3, test `DiscardRestart_MatchesSingleThreadedGolden`) |
| **P5 — Carry-forward effect/enqueue gen-stamp consistency** | "Fold-soon (spec); the interaction is unspecified" | Pinned contract: **drain-only-after-`Complete`**; `EffectRef` gen-stamped with `RenderContext.Generation`; straddled/discarded effects gen-skipped while cleanups still fire; unmount cleanups staged in scratch (read "synchronously in the Complete burst") | §12.4 (+ phase map §14, build step 1, test `CarryForward_EffectGenStamp`) |

**Boundaries respected (referenced, not redesigned).** Lanes / `UseTransition` / `startTransition` / the phase-3
update queue / `RenderContext` generation / the `UseObservable`/`UseResource` *hook signatures* / `EffectScheduler` +
`EffectRef` *type* are **owned by `reconciler-hooks.md`** — this doc consumes `ILaneScheduler` read-only and pins only
the *seam invariants* (work-loop purity, drain timing, snapshot/re-check placement). `ISystemColors` and any
`IExternalStore` backing are PAL/theming seams. The `ReconcileScratch`/keyed-LIS structure and `ISceneBackend` mutator
set are owned by Scene/Reconciler; this doc pins only that they are staged, not live, until `Complete`.