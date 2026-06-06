# FluentGpu on .NET 10 / C# 14 — Zero-Alloc & AOT Patterns

*Lead-architect build doc. Apply while building. Every recommendation is tied to a named FluentGpu hot path. SHIPPED unless tagged (preview). Verified against .NET 10 GA (LTS, 2025-11-11) / C# 14.*

---

## 0. The one rule everything serves

**0 managed allocations in phases 6–13** (layout → record → batch → submit → present → effects → arena-swap). Everything below is judged against that. Edge allocations (phase 4 Element records, user lambdas, DSL `Element[]` chunks, mount-time `Component`/`RenderContext`) are tolerated but pushed toward stack-alloc where the JIT can deliver it. The load-bearing zero-alloc mechanism is, and stays, **explicit arenas/slabs/pools** — language/runtime features are icing, never the contract.

---

## 1. Language / runtime baseline

Set these once. They are the substrate for every pattern in this doc.

**Per-project (Directory.Build.props):**
```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <LangVersion>14.0</LangVersion>              <!-- pin; don't ride 'latest' -->
  <Nullable>enable</Nullable>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <ImplicitUsings>enable</ImplicitUsings>

  <!-- AOT + trim -->
  <PublishAot>true</PublishAot>
  <IsAotCompatible>true</IsAotCompatible>        <!-- libraries: enables AOT/trim analyzers -->
  <InvariantGlobalization>true</InvariantGlobalization>  <!-- paint path is culture-free -->
  <TrimMode>full</TrimMode>
  <StackTraceSupport>false</StackTraceSupport>   <!-- optional size/startup trim -->

  <!-- GC: Workstation + Concurrent (see §G) -->
  <ServerGarbageCollection>false</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  <!-- DATAS left ON (default). Do NOT disable. -->
</PropertyGroup>
```

**Assembly split (load-bearing — see §F):**
- **`FluentGpu.Renderer` / `FluentGpu.Pal`** — carries `[assembly: DisableRuntimeMarshalling]`. Hand-vtable `calli` only. All interop blittable. This attribute makes the compiler *prove* every P/Invoke, delegate, and unmanaged-fn-ptr boundary is blittable (CA1421 otherwise) — the cheapest zero-alloc guardrail you can buy.
- **`FluentGpu.PlatformIntegration`** — UIA/TSF/OLE/DWrite-setup COM via `[GeneratedComInterface]`/`[GeneratedComClass]`. **No** `DisableRuntimeMarshalling` here (source-gen COM uses `in`/`out` modifiers that the attribute bans). Keep each generated COM interface hierarchy inside this one assembly (cross-assembly `[GeneratedComInterface]` inheritance has a rebuild-coupling vtable-offset pitfall).

**Interceptors opt-in (only if a source generator emits them — see Hooks/DSL):**
```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);FluentGpu.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

**Runtime-set once at engine startup (not per-frame):**
```csharp
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; // suppress FOREGROUND gen2 for app life
```

**NativeAOT caveat to internalize:** Dynamic PGO and JIT escape analysis do **not** run under NativeAOT (no tiered recompilation at runtime). AOT does *ahead-of-time* escape analysis only. So treat "the JIT will stack-allocate my closure/array" as a JIT-debug-iteration nicety and a "write inlineable code" guideline — **never** architect the zero-alloc guarantee on it under AOT. Arenas/slabs/pools remain the contract.

---

## 2. Feature → subsystem matrix

| Feature (status) | Where in FluentGpu | Concrete change |
|---|---|---|
| `params ReadOnlySpan<T>` (C# 13) | Batched 2D renderer, record phase 5 | Every internal `params T[]` helper (push N opcodes/clips/glyphs) → `params ReadOnlySpan<T>`; compiler stack-allocs backing. Kills per-call `T[]`. |
| First-class span conversions (C# 14) | `SubmitDrawList`, all span APIs | Drop `.AsSpan()` ceremony; one span signature serves arena buffers + `T[]` scratch. No duplicate array overloads. |
| `[OverloadResolutionPriority(1)]` (C# 13) | DrawList submit migration | If a legacy array overload must coexist, tag the span overload so recompiled callers bind to zero-alloc path. |
| `allows ref struct` + ref-struct interfaces (C# 13) | DrawList leaf walk / submit | `Walk<TSink>(...) where TSink : IDrawSink, allows ref struct`; `TSink` is a `ref struct` over arena cursor → devirtualized, no box. |
| User-defined compound `+=` / instance `++` (C# 14) | Ported Yoga `LayoutNode` math, SceneStore SoA columns | Accumulators (transform/clip/dirty-rect unions, `Ring8`/`Edges4`) mutate in place instead of returning fresh instances. Highest-leverage C# 14 alloc win for aggregate types. |
| Extension members / static extension operators (C# 14) | `ShapedRunBuilder`, `LayoutNode` view, `[FastPath]` DSL builder | Hang `IsEmpty`/`Width`/operators on `ref struct` SoA views — no adapter-class alloc. |
| `field` keyword (C# 14) | Reactor edge: Element/Component/RenderContext | Validating/normalizing setters (clamp, intern `StringId`, null-guard) stay auto-backed. Maintainability, not hot path. |
| `ref readonly`/`in` params + lambda modifiers (C# 14) | `DepKey`/`ModifierOps`/`Handle`/`StringId` boundaries; hooks lowering | Pass 16-byte keys `in` to avoid defensive copies; source-gen `UseEffect` capture uses `in`/`scoped` on type-less lambda params. |
| `[InlineArray(N)]` (C# 12) | `Ring8`, `Edges4`, `DepDeps`, `LayoutNode` scratch | Lock these to `[InlineArray]` → stack/inline, `Span<T>`-convertible, 0 heap, no `fixed`. |
| `Unsafe.SkipInit` (mature) | InlineArray locals fully overwritten | Drop stack zero-init — *only* when every element is provably written before read. |
| `"…"u8` literals (C# 11) | COM IIDs, DXGI/D3D12 enable-strings, DWrite tags | `static ReadOnlySpan<byte> IID_xxx => "…"u8;` — data-section, 0 alloc. (Runtime constant: can't be `const`/default-param.) |
| `SearchValues<char>`/`<string>` (.NET 8/9) | Text seam itemization / line-break (phase ~4–5) | `static readonly SearchValues<char>` for break/space/control sets; drive `ShapedRunBuilder` boundaries via `IndexOfAny`. SIMD, 0 alloc. |
| `FrozenDictionary`/`FrozenSet` + alternate lookup (.NET 8/9) | Theme tokens, accelerator tables, StringId↔name | Build once at load; `GetAlternateLookup<ReadOnlySpan<char>>()` to probe with a sliced span, no `ToString()`. NOT for per-frame maps. |
| `CollectionsMarshal.GetValueRefOrAddDefault` (.NET 6/9) | Keyed reconciler "key→node slot" map | Single probe + `ref TValue?` mutation, no copy-back; alternate-key overload (`allows ref struct`) for span keys. |
| `CollectionsMarshal.AsSpan` (.NET 5) | SceneStore staging `List<T>` | Enumerator-free, in-place ref mutation — single-thread phases only. |
| `GC.AllocateUninitializedArray(…, pinned:true)` (.NET 5) | Arena, DrawList byte arenas, `ulong[]` sortkey arena | Skip memset (~50% faster ≥4KB); pinned removes GC fix-up before native submit. NOT for small edge buffers. |
| `MemoryMarshal.Read/Cast` + `Unsafe.BitCast` (.NET 8) | `SubmitDrawList` POD opcode read; color/sortkey packing | `MemoryMarshal.Read<TOp>(cmds.Slice(c))`; `BitCast` for same-size reinterpret (size-checked). `Unsafe.As` only for `void**`/object refs. |
| `Vector256/128<T>` guarded (.NET 8) | Per-node dirty-flag column scan | `IsHardwareAccelerated`-guarded `LoadUnsafe` + `ExtractMostSignificantBits`; `Vector128` fallback for Arm64/macOS. |
| `TensorPrimitives` (.NET 10 in-box) | Bulk affine transforms / bounds reductions in layout | `Add/Multiply/Max/Dot` over arena spans — only for genuinely batched columns (dozens+); use `Vector128` for 2–4 rects. |
| `[LibraryImport]` (.NET 7) | `d3d12`/`dxgi`/`dcomp`/`dwrite`/`dxcompiler` flat exports | Replace all `[DllImport]`; blittable `nint`/`Guid*`/`void**` → no-marshal inlinable stub. AOT-clean. |
| hand-vtable `calli` + `[UnmanagedCallersOnly]` (.NET 5) | D3D12/DXGI/DComp per-frame consume; in-loop CCWs | Keep. `[GeneratedComInterface]` compiles to the *same* `calli`; on hot path it only adds a wrapper object + cache lookup. |
| `[GeneratedComInterface]`/`[GeneratedComClass]` (.NET 8/9) | UIA, TSF, OLE, DWrite **setup**; cold callee CCWs | Adopt for cold/warm surfaces. Replaces hundreds of lines of hand-vtable + manual `ThrowExceptionForHR`. |
| `System.Threading.Lock` (.NET 9) | `HandleTable`, arena-swap guard, render-thread handoff | `private readonly Lock _gate = new();` — `lock` lowers to `EnterScope()` ref-struct, alloc-free per-acquire. |
| `Channel<T>` bounded `DropOldest` (mature) | setState mailbox; designed render-thread `SceneFrame` handoff; assets; devtools | Cross-thread, message-granular, off-hot-path only. `ValueTask` alloc-free on sync completion. |
| `IBufferWriter<byte>` *contract* (mature) | DrawList writer / `[FastPath]` builder | Implement over the arena cursor (`GetSpan`/`Advance`) — 0 runtime cost, idiomatic seam. NOT `ArrayBufferWriter`. |
| `Microsoft.Extensions.ObjectPool` (.NET 10) | `Component`/`RenderContext` mount churn | `DefaultObjectPool<RenderContext>` + reset policy, cap 32. Managed-class edge only; never phases 6–13. |
| JIT escape analysis / PGO (.NET 10, **JIT only**) | Phase-4 Element records, closures, child chunks | Write small, non-virtual, inlineable call sites so the JIT *may* stack-alloc. Debug-build win; **not** an AOT guarantee. |

---

## 3. Per-area sections

### A. Memory / Allocators

**Arena & DrawList backing → `GC.AllocateUninitializedArray<byte>(cap, pinned: true)`.** `ArenaAllocator`, both DrawList byte arenas, and the `ulong[]` sortkey arena are bump-written-then-read at multi-KB sizes — exactly where skipping memset pays (~50% ≥4096B; net-negative <2048B, so never use it for tiny edge buffers). `pinned: true` also removes GC fix-up before native submit. Correctness invariant: the bump allocator must only hand out Advance-covered written regions (it already does), so uninitialized tail bytes are never read.

```csharp
_bytes = GC.AllocateUninitializedArray<byte>(capacity, pinned: true);
```

**InlineArray for all small fixed buffers.** `Ring8`, `Edges4`, `DepDeps`, `LayoutNode` scratch → `[InlineArray(N)]` structs, `Span<T>`-convertible, 0 heap, no `unsafe fixed`. Pair with `Unsafe.SkipInit` on a local you fully overwrite — but never combine `SkipInit` with a "read whole span" on a partially-filled inline array (garbage tail).

```csharp
[InlineArray(8)] public struct Ring8 { private float _e0; }
```

**Value reinterprets → `Unsafe.BitCast` (size-checked); refs → `Unsafe.As`.** In a hand-`calli` codebase a wrong reinterpret is undebuggable corruption — default to `BitCast` (`Handle`↔`ulong`, color packing, sortkey composition) and restrict `Unsafe.As` to the `void** lpVtbl` / object-ref cases `BitCast` can't express.

**Pools:** keep your own `SlabAllocator`/`ArenaAllocator`/`HandleTable` as primitives. Use `Microsoft.Extensions.ObjectPool` (`DefaultObjectPool<T>`, cap aligning with your "ObjectPool cap 32") *only* for the managed-class edge (`Component`/`RenderContext`). For transient per-frame buffers that ever escape the arena model, `ArrayPool<T>.Shared` (no wrapper alloc) — **never** `MemoryPool<T>` (one `IMemoryOwner` alloc per rent).

### B. Scene / DrawList

**Writer = `IBufferWriter<byte>` contract over the arena cursor — not `ArrayBufferWriter`.** Make the `[FastPath]` builder / opcode emitter implement `System.Buffers.IBufferWriter<byte>`: `GetSpan(int)` returns a slice of the current bump region, `Advance(int)` moves the cursor. Zero runtime cost (your arena, no pool), and every `System.Buffers`/`BinaryPrimitives`/UTF-8 helper now writes straight into your arena. **Reject `ArrayBufferWriter<byte>`** on the hot path: it grows by renting a *larger* array and copying — a hidden alloc+copy that breaks the rule. (OK in cold tooling, e.g. serialize a DrawList for a golden-image test.)

**Consumer = `ReadOnlySpan<byte>`, never `ReadOnlySequence<byte>`.** Your DrawList is one contiguous double-buffered arena = single segment. `ReadOnlySequence`/`SequenceReader` only earn their keep on multi-segment pooled buffers — adopting them adds segment-boundary branches and `SequencePosition` arithmetic for zero gain. Read POD opcodes via `MemoryMarshal.Read<TOp>(cmds.Slice(cursor))` / `MemoryMarshal.Cast`.

**Leaf walk = constrained generic over a ref-struct sink (the "devirtualized concrete types" goal, realized):**
```csharp
public interface IDrawSink { void Op(in QuadOp q); /* … */ }

public static void Walk<TSink>(ReadOnlySpan<byte> cmds, ref TSink sink)
    where TSink : IDrawSink, allows ref struct          // C# 13
{
    int c = 0;
    while (c < cmds.Length)
    {
        var op = MemoryMarshal.Read<QuadOp>(cmds.Slice(c));
        sink.Op(in op);                                 // devirtualized, no box
        c += Unsafe.SizeOf<QuadOp>();
    }
}
```
**Trap:** never reach `TSink`'s interface members through the interface *type* (hard error / would box). Keep the walk generic top-to-bottom; the moment you store the sink as `IDrawSink` you leave the zero-alloc regime.

**Dirty-flag column scan → guarded `Vector256`:** `LoadUnsafe` 32 flags, compare, `ExtractMostSignificantBits` → `uint` bitmask, iterate set bits; `Vector128` fallback for `V128 && !V256` (Arm64/macOS). Gate on `Vector256.IsHardwareAccelerated`, never `Avx2.IsSupported` alone.

### C. Hooks / DSL

**Deps stay `ReadOnlySpan<DepKey>` over an `[InlineArray]` `DepDeps`** (the `<=4-arity unmanaged` capture). Pass `DepKey`/`ModifierOps` (16 B each) and `Handle`/`StringId` as `in`/`ref readonly` across perf boundaries to kill defensive copies.

**Source-gen `UseEffect` lowering uses C# 14 type-less lambda modifiers** so the generated capture can take `in`/`ref`/`scoped` params without spelling types:
```csharp
UseEffect(static (in DepKey k) => { /* … */ }, deps);
```

**DSL `ModifierOps`** stay 16-byte ops in a bump arena (Element holds the 8-byte `ModifierRef`); the `[FastPath]` builder carries the arena cursor as a `ref struct`. Hang ergonomic members (`.Padding`, operators) on the builder/SoA views via **C# 14 extension blocks**, not adapter classes — sugar, zero indirection, no wrapper alloc. (Note: extension operators are compile-time static dispatch; they do not give runtime polymorphism over node variants — that's still the SoA-tag + concrete switch on the opcode stream.)

**`field` keyword** on Element/Component/RenderContext setters that clamp/intern/null-guard — keeps them auto-backed. Edge-layer quality win; one-time grep for any member literally named `field` (use `@field`).

**Interceptors (gated):** only as a *source-generator emission* via Roslyn `GetInterceptableLocation` + `[InterceptsLocation]`, allow-listed through `<InterceptorsNamespaces>`. Use to erase a specific managed virtual/reflection-y startup call site. **It cannot touch your hand `calli` interop** and does not change the ComWrappers ruling. Treat as build infrastructure, never hand-written.

### D. Layout

Port Reactor's Yoga onto the `ref struct LayoutNode` view over SoA columns + arena scratch + `[InlineArray]` `Ring8`/`Edges4`.

**Accumulator math → C# 14 user-defined compound assignment / instance `++`** (the single highest-leverage C# 14 feature for "0-alloc on aggregate types"):
```csharp
public struct RectAccum
{
    public float MinX, MinY, MaxX, MaxY;
    public void operator +=(in Rect r) { /* mutate this in place */ }  // C# 14, void, instance
}
```
**Trap (silent re-alloc):** if the `+=`/`++` *result is used* (`var b = ++a;`) or the target isn't a plain variable (a property without setter, an indexer), the compiler **falls back to the static value-returning operator** — reintroducing exactly the instance you wanted gone, with no warning. On phases 6–13, audit every `+=`/`++` to discard its result and target a real variable (a SoA column ref, not a property getter).

**Bulk transforms/bounds → `TensorPrimitives`** *only* for genuinely batched columns (dozens+ floats over arena spans). For 2–4-element rects, `Vector128` directly — `TensorPrimitives` setup isn't worth it at tiny shapes.

`CollectionsMarshal.AsSpan` over any `List<T>` staging buffer (enumerator-free, in-place mutation) — valid under the single-UI-thread phase discipline; document "no structural mutation while the span/ref is live" because it becomes a footgun the instant the render-thread seam runs concurrently.

### E. Text

**Itemization / line-break / whitespace → `static readonly SearchValues<char>`.** Build once at module init (mandatory-break `\n \r     `, space/tab, ASCII control). Drive `ShapedRunBuilder` boundaries via `runSpan.IndexOfAny(LineBreakChars)` / `IndexOfAnyExcept(...)` — SIMD, 0 alloc. For multi-substring tokens (emoji ZWJ, known escapes) use `SearchValues.Create(ReadOnlySpan<string>, StringComparison.Ordinal)` + `IndexOfAny`. **Traps:** `SearchValues<string>` is `Ordinal`/`OrdinalIgnoreCase` only and `Create` has real build cost — build static, never per-frame/per-run. (Ordinal matches the culture-free paint path anyway.)

**`ShapedRunBuilder` is a `ref struct` filling caller-owned spans.** Hang `IsEmpty`/`Width` via extension members. Keep the in-loop DWrite analysis source/sink **hand-written** (see §F trap).

**Theme/StringId tables → `FrozenDictionary`/`FrozenSet`** built at theme load. Probe with a sliced parse buffer via `GetAlternateLookup<ReadOnlySpan<char>>()` (`IAlternateEqualityComparer<ReadOnlySpan<char>,string>`) to avoid the `ToString()` alloc. **Frozen is build-once** — wrong for `HandleTable`/`SlabAllocator`/the per-frame keyed map (all mutable).

### F. COM / Interop

**Flat C exports → `[LibraryImport]` (never `[DllImport]`).** `D3D12CreateDevice`, `CreateDXGIFactory2`, `DCompositionCreateDevice`, `DWriteCreateFactory`, runtime `dxcompiler`. Blittable `nint`/`Guid*`/`void**` → no-marshal inlinable stub, AOT-clean. Pass IIDs as `in iid` over the pinned `"…"u8` span.

**Per-frame consume path → keep hand-vtable `calli`.** `ID3D12GraphicsCommandList`/`CommandQueue`/swapchain `Present`, DComp. `void** lpVtbl[n]` + `delegate* unmanaged[MemberFunction]`. (ComputeSharp's vendored `ComPtr<T>` is the precedent.)

**In-loop callee CCW (DWrite text-analysis source/sink, *if* invoked in live per-frame shaping) → keep hand-written** via `[UnmanagedCallersOnly]` vtable + `GCHandle` + `Interlocked` refcount.

**Cold/warm COM → `[GeneratedComInterface]` (consume) / `[GeneratedComClass]` (callee CCW).** UIA provider tree, TSF, OLE drag/drop, DWrite *setup* (factory/format/font-collection). Human-timescale, not per-frame. The generator gives correct HRESULT→exception, `[PreserveSig]`, `[UnmanagedCallersOnly]` vtable + GCHandle identity + cached one-CCW-per-object — AOT-safe, deletes a large hand-maintained unsafe surface.

### G. GC / Threading

**Workstation + Concurrent, DATAS on. Never Server GC.** Single-UI-thread desktop; the dominant risk is a *foreground gen2 pause* on the UI thread, which background GC already collects concurrently. Server GC inflates working set and spawns per-core threads competing with your UI/future-render thread, and won't shrink an idle heap — wrong for bursty UI. DATAS-on-Workstation is effectively 1 heap and keeps the heap proportional to your small SceneStore/Slab footprint (the whole point of replacing heavy WinUI 3).

**`SustainedLowLatency` set once at startup** (not per-frame): suppresses foreground gen2 for app life while gen0/gen1 + background gen2 keep running cheaply — exactly right for phase-4 gen0 churn (Element records, lambdas, child chunks). Its "larger, less-compacted heap" downside is bounded because phases 6–13 are 0-alloc and your large text/GPU buffers are native (HandleTable), not pinned on the managed heap.

**Locks → `System.Threading.Lock`.** `HandleTable`, arena-swap guard, future render-thread handoff: `private readonly Lock _gate = new();`. `lock(_gate)` lowers to a ref-struct `EnterScope()` — alloc-free per acquire, cheaper uncontended than `Monitor`.

**setState mailbox + render-thread handoff → bounded `Channel<T>`, `DropOldest`.** Drain at frame start (before phase 1). Make `StateMsg`/the handoff a **struct** (blittable, `DepKey`-style) so the buffer holds values, not boxed refs. `SingleReader=true`, `AllowSynchronousContinuations=false` (so a slow consumer can't run continuations inline on a producer thread / under a held lock). The handoff carries **one `SceneFrame` per frame** — its `ValueTask` cost is amortized across the whole frame and does not violate the phase-6–13 rule. `DropOldest` gives frame-drop backpressure for free (newest frame wins); use the dropped-item callback to recycle the `SceneFrame` into the slot-reuse quarantine. **Unbounded = latency trap** (queues stale frames).

**Optional micro-tune:** if GC native bookkeeping shows up against your modest heap, `System.GC.RegionSize=1MB` (MS's small-heap recommendation) via runtimeconfig. Measure first.

---

## 4. Decisive ruling — ComWrappers / `[GeneratedComInterface]` vs hand-vtable

**Verdict: hand-vtable on the per-frame hot path and any CCW invoked inside the frame loop; `[GeneratedComInterface]`/`[GeneratedComClass]` everywhere cold/warm. Amend the spec's *reasoning*; keep its *conclusion* for the hot path only.**

| COM surface | Mechanism | Why |
|---|---|---|
| D3D12 cmd list / queue / swapchain Present, DComp (phases 6–13) | **Hand-vtable `calli`** | `[GeneratedComInterface]` compiles down to the *same* cached-fn-ptr `calli`. On the hot path it adds only a wrapper object + a `ConcurrentDictionary` RCW-cache lookup, and costs you control of the exact call site inside the devirtualized POD walk. No upside. |
| DWrite text-analysis source/sink **if** in live per-frame shaping | **Hand-vtable CCW** (`[UnmanagedCallersOnly]`+GCHandle+Interlocked) | The call *out* from DWrite hits the generated thunk + GCHandle resolution on the hot path. Keep this specific sink hand-written. |
| UIA, TSF, OLE, DWrite **setup**; all cold callee CCWs | **`[GeneratedComInterface]` / `[GeneratedComClass]`** | Human-timescale. Trades a handful of long-lived wrapper objects for a large reduction in error-prone hand-written unsafe. Microsoft-recommended AOT/trim-clean COM path. |
| Flat C exports (device/factory creation) | **`[LibraryImport]`** | Not COM dispatch; blittable no-marshal stub, AOT-clean, inlinable. |

**Why the spec's stated reason is outdated but the conclusion survives:** "per-object RCW per call" does **not** happen on .NET 10 — dispatch is a cached native fn-ptr vtable, and the RCW (`ComObject`) is allocated *once per distinct COM pointer* and cached (one RCW per object process-wide; one CCW per managed object on a non-collected heap). So on the hot path you are not avoiding a per-call alloc; you are avoiding (1) a one-time RCW alloc if you ever wrap a *transient per-frame* pointer, (2) the RCW-cache dictionary lookup/locking on `GetOrCreateObjectForComInstance` (which also has a documented race — never call it per frame; wrap once at mount, hold the typed reference), and (3) loss of control over the exact `calli`. **Restate the rejection on cache-lookup + call-site-control grounds, not "per-call alloc."** Interceptors do not change this (they devirtualize *managed* calls, not your `calli`). `ComWrappers`/`StrategyBasedComWrappers` stays rejected for the hot path; adopted (via the source generators) for everything cold.

---

## 5. `System.IO.Pipelines` — not the tool here, use X instead

Pipelines is purpose-built for **async byte-stream I/O with backpressure across a thread boundary** (partial-message reassembly off a socket, pooled multi-segment buffers, `PauseWriterThreshold` flow control). FluentGpu's DrawList is the opposite: **synchronous, single-segment, in-process, fully owned by a bump arena.** Using `Pipe` on the frame loop pays async state machines + `MemoryPool<byte>` GC-backed segment pooling (violates phases 6–13) + multi-segment `ReadOnlySequence` that *re-fragments* a deliberately contiguous stream — to model data that's already a flat span you own. Hard no.

| You might reach for | Don't (on the hot path) | Use instead |
|---|---|---|
| `Pipe`/`PipeReader`/`PipeWriter` for DrawList | Async, GC-pooled segments, multi-segment, backpressure you don't need | **`ArenaAllocator` + `IBufferWriter<byte>` contract over its cursor**, consumed as `ReadOnlySpan<byte>` |
| `ArrayBufferWriter<byte>` as DrawList store | GC array; growth = new array + copy | Your **double-buffered arena** (bump + O(1) Reset) |
| `ReadOnlySequence<byte>` / `SequenceReader<byte>` to read commands | Segment-boundary branches, `SequencePosition` arithmetic, `ref struct` reader — for discontiguity you don't have | **`ReadOnlySpan<byte>` + `MemoryMarshal.Read<T>`** (single-segment fast path *is* a span) |
| `Channel<DrawCommand>`/`Channel<byte>` for opcodes | Per-item sync overhead × thousands of commands | One **`Channel<SceneFrame>` per *frame*** (handoff granularity is the frame, not the command) |

**Where `Channel<T>` / `Pipe` *are* correct (all off the hot path):** the designed render-thread `SceneFrame` handoff (bounded `Channel`, `DropOldest`, cap 1–2); async GPU asset/texture/font upload request queues (`Channel<UploadRequest>`); devtools/trace transport (`Channel<TraceEvent>`, `DropOldest`, low-priority drain). And `PipeReader.Create(stream)` + `SequenceReader<byte>` is legitimately right at the **disk/network ingestion edge of the asset subsystem** (real streaming I/O) — never anywhere near SceneStore/DrawList.

---

## 6. Traps / don't bother

- **In-place `++`/`+=` silently falls back to allocating** when the result is used or the target isn't a plain variable. Audit every occurrence on phases 6–13 (discard result, target a real SoA column ref). No compiler warning.
- **First-class span conversions are a .NET 10 *breaking* overload-binding change.** Span overloads now bind in more places, including inside Expression-tree lambdas (throws under `Compile(preferInterpretation:true)`). Paint path is fine, but any diagnostic/test layer building `Expression<>` must cast to `IEnumerable<T>` or call the static form. Adding a span overload next to an array one can silently re-bind callers — use `[OverloadResolutionPriority]` deliberately.
- **`params ReadOnlySpan<T>` empty-call ambiguity:** two `params` span overloads differing only by element type are ambiguous on `M()`/`M([])`. Don't pair near-identical span overloads on hot APIs.
- **Never call a `ref struct`'s interface members through the interface type** (boxing → hard error). Keep the whole leaf walk generic top-to-bottom.
- **`ArrayBufferWriter` / `MemoryPool` / `Pipe` on the frame loop** — all allocate or copy (GC array growth / `IMemoryOwner` per rent / pooled segments). Arena + `ArrayPool` (when you must) only.
- **`GC.AllocateUninitializedArray` for small buffers is net-negative** (<2048B). Multi-KB arenas only. Never read an un-overwritten region.
- **`Unsafe.As` size-mismatch = silent corruption.** Default to `Unsafe.BitCast` for value reinterprets; `Unsafe.As` only for ref/object casts.
- **`Vector512`/AVX-512/AVX10.2 are not universal** (and absent on Arm64/macOS). Gate on `Vector256/512.IsHardwareAccelerated` (not `Avx512.IsSupported`), always supply a `Vector128` fallback. 256-bit is the safer default for short scans.
- **`FrozenDictionary` / `SearchValues.Create` are build-once with real build cost** — load-time static tables only; never per-frame/per-run.
- **`CollectionsMarshal.AsSpan` / `GetValueRefOrAddDefault` refs invalidate on any add/remove.** Safe single-threaded; document "no structural mutation while ref/span live" for the render-thread future.
- **`Channel` `AllowSynchronousContinuations=true` runs the consumer inline on the producer thread** (and under any held lock). Keep it `false` — setState must apply on the UI thread.
- **Don't architect zero-alloc on JIT escape analysis** — JIT-only (not AOT), fires only after inlining, reverts to heap silently if a call site is too large/virtual.
- **`StrategyBasedComWrappers.GetOrCreateObjectForComInstance` has a documented race + concurrent-dict cache** — never per frame; wrap once at mount.
- **`DisableRuntimeMarshalling` does not disable built-in COM and bans `in`/`ref`/`out` on interop boundaries** — keep source-gen COM (which uses those modifiers) in a separate assembly without the attribute.
- **Cross-assembly `[GeneratedComInterface]` inheritance** has a vtable-offset rebuild-coupling pitfall — keep each COM hierarchy in one assembly.
- **`field` keyword** shadows any member literally named `field` — grep before broad adoption.
- **Extension operators are compile-time static dispatch** — no runtime polymorphism over node variants; keep the SoA-tag + concrete switch.

---

## 7. Spec amendments checklist (apply to the docs)

- [ ] **Baseline:** add the `net10.0` / `LangVersion 14.0` / `PublishAot` / `InvariantGlobalization` / `TrimMode full` block to `Directory.Build.props`.
- [ ] **GC:** set `<ServerGarbageCollection>false</>` + `<ConcurrentGarbageCollection>true</>`; document "DATAS on, never Server GC."
- [ ] **Startup:** `GCSettings.LatencyMode = SustainedLowLatency` once at engine init; add optional `FrameLoop.BeginCriticalSpan(budget)` wrapping `TryStartNoGCRegion` *with bool check + fallback* (NOT per-frame).
- [ ] **Assembly split:** `[assembly: DisableRuntimeMarshalling]` on `FluentGpu.Renderer`/`Pal`; create `FluentGpu.PlatformIntegration` (no attribute) for source-gen COM; keep each COM hierarchy single-assembly.
- [ ] **COM ruling:** rewrite "reject ComWrappers" → "hand-vtable on per-frame hot path + in-loop CCWs; `[GeneratedComInterface]`/`[GeneratedComClass]` everywhere cold." Re-justify the hot-path rejection on **cache-lookup + call-site-control**, not "per-call RCW alloc."
- [ ] **Interop:** mandate `[LibraryImport]` for every flat C export; ban `[DllImport]`. Confirm IIDs are `"…"u8` via `static ReadOnlySpan<byte> => …` properties.
- [ ] **DrawList:** specify writer = `IBufferWriter<byte>` contract over arena cursor; consumer = `ReadOnlySpan<byte>` + `MemoryMarshal.Read`. Explicitly state "no `Pipe`/`ArrayBufferWriter`/`ReadOnlySequence`/`SequenceReader` on the paint path."
- [ ] **Leaf walk:** `Walk<TSink>(…) where TSink : IDrawSink, allows ref struct`; document "never reach members via the interface type."
- [ ] **Layout/SoA:** adopt C# 14 user-defined `+=`/instance `++` for accumulators; add the "result-unused / variable-target only" audit rule to the zero-alloc checklist.
- [ ] **Allocators:** arenas/DrawList/sortkey via `GC.AllocateUninitializedArray(pinned:true)`; lock `Ring8`/`Edges4`/`DepDeps` to `[InlineArray(N)]`; reinterprets via `Unsafe.BitCast`.
- [ ] **Text:** `static readonly SearchValues<char>` for break/space/control sets driving `ShapedRunBuilder`; theme/StringId tables → `FrozenDictionary` + `GetAlternateLookup<ReadOnlySpan<char>>()`.
- [ ] **Reconciler:** keyed map via `CollectionsMarshal.GetValueRefOrAddDefault` (+ alternate-key overload for span keys); document the "no structural mutation while ref live" invariant.
- [ ] **Threading:** `System.Threading.Lock` for `HandleTable`/arena-swap/handoff guards.
- [ ] **Channels:** bounded `Channel<StateMsg>` (struct, `SingleReader`, `DropOldest`/`Wait`, sync-continuations off) for setState; bounded `Channel<SceneFrame>` (`DropOldest`, cap 1–2) for the designed render-thread handoff; `Channel<T>` for assets + devtools; `PipeReader.Create(stream)` only at the asset ingestion edge.
- [ ] **Hooks/DSL:** `UseEffect` lowering uses C# 14 type-less lambda `in`/`scoped` modifiers; `field` keyword on validating edge setters; extension blocks for `ref struct` view members.
- [ ] **SIMD:** dirty-flag scan via guarded `Vector256` + `Vector128` fallback; `TensorPrimitives` only for batched layout columns.
- [ ] **Interceptors:** if adopted, emit from a source generator only, allow-list `FluentGpu.Generated` via `<InterceptorsNamespaces>`.
- [ ] **AOT note:** add the caveat that escape analysis / Dynamic PGO do not apply under NativeAOT — arenas/slabs/pools are the contract, not the JIT.
- [ ] **Edge pooling:** `Microsoft.Extensions.ObjectPool` (`DefaultObjectPool<RenderContext>`, cap 32, reset policy) for mount churn — managed edge only.

---

## Sources

- [What's new in C# 14 — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14)
- [Introducing C# 14 — .NET Blog](https://devblogs.microsoft.com/dotnet/introducing-csharp-14/)
- [Performance Improvements in .NET 10 — .NET Blog](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
- [Preparing for the .NET 10 GC (DATAS) — .NET Blog](https://devblogs.microsoft.com/dotnet/preparing-for-dotnet-10-gc/)
- [ComWrappers source generation — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation)
- [Native interop best practices — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices)
- [Native AOT deployment — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
