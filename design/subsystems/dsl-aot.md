# FluentGpu — Subsystem Design: Fluent C# DSL + NativeAOT Toolchain + Source Generators

*Authoritative. Supersedes [`../archive/dsl-aot-toolchain.md`](../archive/dsl-aot-toolchain.md) (archived under `design/archive/`, historical only).*
*Owns the authoring surface, the `[module]` AOT/GC/trim baseline, the SOURCE GENERATORS (the eight portable +
the WGPU analyzer in `FluentGpu.SourceGen`, the leaf COM-binding generator, and the optional
`LocalizationGenerator`), the **concurrent-era hook lowering generators**
(lane/transition capture, the Suspense element codegen, `UseDerived`/`UseContextSelector` projection capture,
`UseOptimistic` capture), and the **L9 localization build config** (the `InvariantGlobalization`/ICU trade +
the edge-side format→`StringId` path). Cross-cutting contracts (threading, COM ruling, memory/handles,
scene/drawlist, RHI/PAL, hooks/reconcile) are owned by the docs named inline — this doc references, never
redefines, them. In particular it **lowers** the new hooks whose semantics are owned by `reconciler-hooks.md`
(`UseTransition`/`startTransition`, `Suspense`/`Boundary`, `UseDerived`/`UseContextSelector`, `UseOptimistic`)
— this doc owns only their **AOT-clean, reflection-free codegen**, never their runtime semantics.*

**Assemblies owned here:** `FluentGpu.Dsl` (Element records, factories, modifier arena, `[FastPath]` builder,
the `Suspense`/`Boundary` element records), `FluentGpu.Hooks` (authoring surface of the hook deps + the
lane/transition/derived/optimistic capture surface — the cells/scheduler/lane-queue are reconciler-owned),
`FluentGpu.SourceGen` (the portable codegen + analyzer generators), and `FluentGpu.Interop.SourceGen`
(the COM-binding generator overview; deep detail deferred to `com-interop.md`). Also owns the root
`Directory.Build.props` baseline, the footprint ratchet, and the `FluentGpu.Localization` edge-formatter
assembly + its build config (`[L9]`).

**Reference (do not duplicate):**
- Threading / 13-phase / thread-confinement: `hardened-v1-plan.md` §2 (canonical).
- COM ruling (LibraryImport + hand-vtable hot path + `[GeneratedComInterface]` cold + harvested `*.comabi.json`):
  `dotnet10-csharp14-zero-alloc.md` §4 + `hardened-v1-plan.md` §4.2 → deep detail in `com-interop.md`.
- Memory/handles/allocators (`Handle`, `SlabAllocator`, `ObjectPool` cap-32, `ChunkedArena`, `StringId`):
  `foundations.md` §1.
- Scene/DrawList opcodes + clean-span rule + `Mutate()` epoch chokepoint: `architecture-spec.md` §4.4/§4.5,
  §5.4 (and the WaveeMusic opcode amendments in `app-requirements-waveemusic.md` §5).
- Hooks/reconcile (DepKey 16-byte struct, parallel `GcDepTable`, 3-signal memo skip, effect timing,
  phase 6.5 ratified): `subsystems/reconciler-hooks.md` + `architecture-spec.md` §5.6.
- **Lanes / transition / update-queue / `Boundary`-`Suspense` element / `UseDerived`-`UseContextSelector` /
  `UseOptimistic` semantics** (the hooks this doc *lowers*): `subsystems/reconciler-hooks.md` (canonical owner
  of the runtime; this doc owns only the codegen).
- **New scene columns** the lowering populates (`LaneMask`, lane/update-queue storage, `BoundaryState`):
  `subsystems/scene-memory.md` owns the column storage; `reconciler-hooks.md` owns the semantics.
- **Edge text formatting → `StringId`** (the no-string-on-paint-path contract the localizer feeds):
  `subsystems/text.md` §4.4/§6 (`StringId` everywhere; source UTF-16 owned at the edge).
- **Locale PAL seam** (`IPlatformLocale`, the `Epoch`/snapshot shape for current-culture): `subsystems/pal-rhi.md`
  §8.x owns the seam (modeled on `ISystemColors`); this doc references it.
- **New modules in the assembly graph** (`FluentGpu.Controls`, `FluentGpu.Devtools`): `controls.md` /
  `devtools.md` own their content; this doc owns only their **build/trim/footprint placement** (§3.4/§3.6).
- CI gates / spikes / footprint enforcement: `FluentGpu.Validation` → `validation.md`.

---

## 0. CONTRACT SUMMARY (load-bearing decisions this subsystem makes)

| # | Decision | Justification / authority |
|---|----------|---------------------------|
| D1 | **`Element` stays an immutable `record` (GC ref, at the edge).** Not a struct. | `foundations.md` §1 RULE: GC refs only at edges. Records give value-equality (free diff), `with` ergonomics, nullable-init props, `?:`-to-`null` conditional render. Edge cost paid once per render, never per frame. |
| D2 | **Modifiers accumulate as 16-byte `ModifierOp` PODs bump-written into a per-render `ChunkedArena` segment; the Element carries an 8-byte `ModifierRef {Start,Count,Flags}`.** | Kills Reactor's `el with { Modifiers = Merge(...) }` O(nodes×modifiers) record-copy storm → O(modified-nodes). `ChunkedArena` (not the old single-buffer arena) per the `foundations.md` amendment. |
| D3 | **Concrete element type preserved through chaining via `where T : Element` *shared-generic* extension methods that funnel to ONE non-generic `ModifierArena.Append` core.** | NativeAOT shares one canonical instantiation across all reference `T` → no generic explosion. `.Set()`/`with`/typed chaining preserved. |
| D4 | **All per-element setters, factories, no-reflection bitmask diff, theme blobs, modifier methods, and hook-dep capture are SOURCE-GENERATED** from `[Element]`/`[Prop]`/`[Modifier]`/`[ThemeTokens]`. The **scene-writer is generated in the Reconciler/leaf, NOT in `Dsl`** (D4a). | `foundations.md` §1.4 (no reflection/Reflection.Emit). D4a preserves the acyclicity invariant: `Dsl`/`Hooks` know nothing of `Scene`/`Render`. |
| D5 | **Hook deps = `ReadOnlySpan<DepKey>`; `DepKey` is a 16-byte PURE-SCALAR blittable struct; reference deps go to a parallel managed `GcDepTable` compared by `ReferenceEquals`.** The `[FieldOffset]` GC-ref/scalar union is illegal CLR layout and is removed (this corrects the old draft). | `foundations.md` §6.4 + `reconciler-hooks.md` §3.2 (canonical owner of `DepKey`). |
| D6 | **COM interop ruling: `[LibraryImport]` for flat C exports; hand-vtable `calli` on the GENERATED hot-path consume + in-loop CCWs; `[GeneratedComInterface]`/`[GeneratedComClass]` for ALL cold/warm COM. Bindings GENERATED from a harvested, runtime-self-checked `*.comabi.json` — no human types a vtable slot.** ComPtr render-thread-confined, Move-only across the seam. NO `System.Runtime.InteropServices.ComWrappers` on the hot path. | `dotnet10-csharp14-zero-alloc.md` §4 + `hardened-v1-plan.md` §4.2. Deep detail → `com-interop.md`. |
| D7 | **net10.0 / LangVersion 14 / PublishAot / TrimMode full; Workstation+Concurrent GC, DATAS on; `GCSettings.SustainedLowLatency` once at startup; module-wide `[SkipLocalsInit]`.** | `dotnet10-csharp14-zero-alloc.md` §1/§G. |
| D7b | **`InvariantGlobalization` is OPT-OUT-able per app via a `<FluentGpuLocalization>` MSBuild knob (`Invariant` \| `Embedded` \| `Icu`).** Default `Embedded` (a minimal, AOT-static, allocation-bounded number/date/currency/plural formatter — *not* `InvariantGlobalization=true`'s culture-blind formatting, and *not* full ICU). `Icu` opts back into the framework ICU path (footprint cost noted, ~28 MB ICU data or a sliced `icudt`). `Invariant` keeps today's drop-ICU behavior for apps that never format locale values. **All three keep the no-string-on-paint-path rule: formatting happens at the Element edge → interned `StringId`.** | §6.x localization (this doc); footprint cost from `dotnet10-csharp14-zero-alloc.md` §1. |
| D7c | **Eight portable generators (+ the leaf COM-binding generator), not six.** Adds the three concurrent-era **lowering** generators — `LaneCaptureGenerator` (lane/transition + `UseDeferredValue`), `BoundaryGenerator` (`Suspense`/`Boundary` element codegen), `DerivedCaptureGenerator` (`UseDerived`/`UseContextSelector`/`UseOptimistic` projection+deps capture) — all sharing `HookDepsGenerator`'s equatable model + interceptor lowering. All AOT-clean, no reflection. | §2.9–§2.11 (this doc); `reconciler-hooks.md` (runtime). |
| D8 | **Footprint ratchet: DSL+Hooks+generated ≤ ~220 KB IL; whole "rounded-rect + text" self-contained AOT exe ≤ 5.5 MB.** Enforced by a non-suppressible `.mstat`/`sizoscope` CI gate (per-element marginal ≤ 1.5 KB). | `dotnet10-csharp14-zero-alloc.md` §1 footprint; gate lives in `validation.md`. |

---

## 1. AUTHORING SURFACE — the Fluent C# DSL (`FluentGpu.Dsl`, portable, no GPU/OS types)

### 1.1 Public shape (identical to Reactor at the call site)

```csharp
// global using FluentGpu.Dsl;  (shipped in the project template .props — see §1.6)

Element View(int count) =>
    VStack(
        Text("Hello").Bold().FontSize(28),
        Button("Click me", () => setCount(count + 1)).Padding(12).Corner(8),
        count > 5 ? Text("Wow!").Foreground(Tok.AccentText) : null,   // ?:-to-null survives (D1)
        Image(src).Width(64).Height(64)
    ).Spacing(8).Padding(16);
```

Ergonomically unchanged. The differences are all under the hood (D2/D3) and that the terminal `Element`
patches `SceneStore` through the Reconciler's `ISceneBackend` (not WinUI).

### 1.2 Element model — record at the edge, handle-light payload

`Element` is the abstract base record. Concrete elements are `sealed partial record`s carrying **only
value-typed / `StringId` / handle / small value-struct props** — never heavy COM/WinUI types (Reactor's
`Brush?`/`FontFamily?` become `BrushSpec` value structs + `StringId`/`BrushHandle`).

```csharp
namespace FluentGpu.Dsl;

public abstract record Element
{
    public StringId    Key        { get; init; }   // interned; 0 == none
    public ModifierRef Modifiers  { get; init; }   // 8 bytes into the per-render ChunkedArena (NOT a record)
    public ExtrasRef   Extensions { get; init; }   // 8 bytes; lazy SlabAllocator<ElementExtras> bucket, rare data
    public abstract ushort ElementTypeId { get; }  // source-gen'd const → SceneStore.ElementTypeId (ushort)
}

// 8-byte handle into the per-render modifier arena. Flags low nibble = arenaEpoch (stale-detect, §6.1).
public readonly record struct ModifierRef(uint Start, ushort Count, ModifierFlags Flags)
{
    public static readonly ModifierRef None = default;   // Start==0 && Count==0
    public bool IsEmpty => Count == 0;
}
public readonly record struct ExtrasRef(uint Index);     // 0 == none
```

`ElementTypeId` is a generated `const ushort` per type, so the reconciler's type check is an int compare,
never `GetType()`. A concrete element (user writes the `partial`; generator owns the boilerplate):

```csharp
[Element(TypeId = ElementIds.Text, VisualKind = VisualKind.Text)]
public sealed partial record TextElement(StringId Content) : Element
{
    [Prop] public Half       FontSize   { get; init; }   // 16-bit; size ramp tokens cover most cases
    [Prop] public FontWeight Weight     { get; init; }
    [Prop] public BrushSpec  Foreground { get; init; }   // value struct, not a Brush ref
    [Prop] public TextWrap   Wrap       { get; init; }
    [Prop] public StringId   FontFamily { get; init; }
    // ElementGenerator emits: ElementTypeId override, strongly-typed setters, factory bodies.
    // DiffEqualityGenerator emits: DiffProps bitmask + Equals/GetHashCode over [Prop]+Key+Modifiers+Extensions.
    // SceneWriterGenerator (in Reconciler/leaf, D4a) emits ApplyToScene(ref SceneWriter).
}
```

### 1.3 Factory methods (`static partial class UI`)

Bodies generated from `[Element]`; hand-written ergonomic presets stay hand-written.

```csharp
public static partial class UI
{
    // generated per [Element]:
    public static TextElement Text(string content)   => new(StringTable.Intern(content));
    public static TextElement Text(StringId content)  => new(content);
    // hand-written presets:
    public static TextElement Heading(string c) => Text(c).FontSize(28).Bold();
    public static TextElement Caption(string c) => Text(c).FontSize(12);

    // C# 13 params ReadOnlySpan<T> → stack-allocated span, no object[] boxing, no heap array for small arity:
    public static VStackElement VStack(params ReadOnlySpan<Element> children) => new(ChildList.From(children));
    public static HStackElement HStack(params ReadOnlySpan<Element> children) => new(ChildList.From(children));
    public static ButtonElement Button(string label, Action onClick) => new(StringTable.Intern(label), onClick);
    public static ImageElement  Image(ImageSource src)               => new(src);
}
```

`ChildList.From(span)` copies the children into a per-render `ChunkedArena` segment and returns a
`ChildList {start:u32,count:u32}` handle (the Element holds 8 bytes, not an array ref). `null` children
(the `count > 5 ? … : null` idiom) are **stripped during the copy** — preserving Reactor's conditional
render with zero downstream branching.

> **Honest alloc note (from `app-requirements-waveemusic.md` §3.2):** for **virtualization window
> realization**, the per-window child storage is a managed `Element[]` rented from `ArrayPool<Element>.Shared`
> (sized window+overscan, reused), **never** the cap-32 `ObjectPool` (which overflows exactly during list
> realization). `ChildList.From` over a virtualized window copies the pooled array into the arena. Phase-4
> realize-delta allocates bounded Gen0 (`Element` records of ENTER rows + one pooled `Element[]` chunk);
> phases 6–13 paint machinery stay 0-alloc.

### 1.4 THE CORE PROBLEM — modifier accumulation without per-call re-allocation

**Reactor today (the storm we fix):** `Text("x").Bold().Margin(8).FontSize(28)` = 3 × (`Element` record
copy + `ElementModifiers` `with` + bucket `with`). A 1000-node tree, avg 3 modifiers ≈ **6000 record
allocations per render**.

**Our design — `ChunkedArena`-backed `ModifierOp` stream.** Each modifier is a 16-byte POD; modifiers are
appended to a per-render `ChunkedArena` segment; the Element carries a `ModifierRef`. The chain copies the
record **at most ONCE** (the seal), then bump-writes the arena.

```csharp
public enum ModifierKind : ushort
{ Margin, Padding, Width, Height, MinW, MinH, MaxW, MaxH, HAlign, VAlign, Opacity,
  Corner, Foreground, Background, BorderBrush, BorderThickness, Bold, FontSize, Stretch, /*…*/ }

[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct ModifierOp
{
    public readonly ModifierKind Kind;   // 2
    private  readonly ushort _flags;     // 2 (token-ref vs literal)
    public readonly ulong Value;         // 8 (bit-union: double-as-bits, packed 4×Half Thickness, BrushSpec id)
}                                        // 4 pad → 16B, blittable, [SkipLocalsInit]-friendly
```

The append funnels through ONE non-generic core (D3 — this is the anti-generic-explosion move):

```csharp
public static class ModifierArena   // per-render, in FluentGpu.Dsl; backed by Foundation.ChunkedArena
{
    [ThreadStatic] static ChunkedArena _arena;   // UI thread is sole writer (foundations §6); TS = belt-and-braces

    public static ModifierRef Append(ModifierRef cur, in ModifierOp op)
    {
        ref var a = ref _arena;
        if (cur.IsEmpty)
        {
            uint start = a.Position;
            a.Write(op);                                    // bump-write (O(1); add-chunk is O(1), no copy)
            return new ModifierRef(start, 1, ArenaFlags(op));
        }
        if (a.IsTail(cur))                                  // fast path: this ref is the arena tail → extend in place
        {
            a.Write(op);
            return cur with { Count = (ushort)(cur.Count + 1), Flags = cur.Flags | ArenaFlags(op) };
        }
        uint ns = a.Position;                               // slow path: branched/aliased element → copy then append
        a.WriteCopy(cur.Start, cur.Count);
        a.Write(op);
        return new ModifierRef(ns, (ushort)(cur.Count + 1), cur.Flags | ArenaFlags(op));
    }
}
```

Generated extension methods (one per modifier; all delegate to `Append`):

```csharp
// GENERATED: FluentGpu.Dsl.Generated.ModifierExtensions  (T : Element → shared generic, ref-type, one codegen body)
public static T Margin<T>(this T el, double uniform) where T : Element
    => (T)el.WithModifiers(ModifierArena.Append(el.Modifiers, new ModifierOp(ModifierKind.Margin, PackUniform(uniform))));

public static T FontSize<T>(this T el, double size) where T : Element
    => (T)el.WithModifiers(ModifierArena.Append(el.Modifiers, new ModifierOp(ModifierKind.FontSize, BitConverter.DoubleToUInt64Bits(size))));
```

`WithModifiers` is a single generated non-generic virtual on `Element` returning `Element`; the `(T)` is a
no-op reference cast (identity preserved). The default path accepts **1 record copy per *modified* element**
(via `with`), down from Reactor's N-per-element. That is the decisive win: **O(modified-nodes), not
O(nodes × modifiers).**

**Allocation accounting (1000 nodes, avg 3 mods):**

| | Reactor | FluentGpu |
|---|---|---|
| Element record copies | 3000 | 0–1000 (one per modified element) |
| Modifier bucket records | 3000+ | 0 (arena bump) |
| Heap bytes (Gen0) | hundreds of KB | 16 B × 3000 in the arena = 48 KB, **reused every frame** |

The modifier arena is a `ChunkedArena` (reserve-then-commit, native-backed via `IVirtualMemory`, O(1)
add-chunk, no LOH cliff) and is `Reset()` at frame end. A native high-water counter — not the GC tripwire —
gates chunk growth (`hardened-v1-plan.md` §4.4).

### 1.5 The `[FastPath]` ref-struct builder — re-homed on retain (eliminating the last copy)

For the hottest authoring (list-item templates rendered per-frame), `[Element(FastPath=true)]` emits a
`readonly ref struct` builder that **defers record materialization until the reconciler consumes it**, so
even the single `with` disappears. The builder is **re-homed on retain**: it carries the arena cursor on the
stack and seals exactly one record at the container boundary (the implicit conversion).

```csharp
public readonly ref struct TextBuilder
{
    private readonly StringId    _content;
    private readonly ModifierRef _mods;        // mutated via ModifierArena.Append; struct stays on stack
    public TextBuilder FontSize(double s) => new(_content, ModifierArena.Append(_mods, …));
    public TextBuilder Bold()             => new(_content, ModifierArena.Append(_mods, …));
    public static implicit operator TextElement(in TextBuilder b)
        => new(b._content) { Modifiers = b._mods };   // the one and only materialization
}
```

This is a **hybrid, not a replacement**: `record` is the default (D1). The ref-struct builder is opt-in
because (a) a `ref struct` cannot appear in a `?:` returning `Element` (breaks conditional render), and
(b) it cannot be `null`. The default record+arena path already meets the per-frame budget; `[FastPath]` is
the escape valve. Builder members hang via C# 14 **extension blocks** on the ref-struct view — sugar, no
adapter-class alloc. Implements `System.Buffers.IBufferWriter<byte>` semantics over the arena cursor where
it feeds a `[FastPath]` opcode emitter (the `IBufferWriter` *contract*, zero runtime cost — never
`ArrayBufferWriter`).

### 1.6 Concrete-type preservation & extension visibility

`where T : Element` extensions return `T`, so the chain stays `TextElement` end-to-end. Element-intrinsic
props (e.g. `TextElement.Wrap`) get generated typed setters using a single `with` (`this with { Wrap = v }`)
— correct and rare on hot paths. The Reactor `.Set(Action<TWinUI>)` hatch is gone (no WinUI); its
replacement is `.Set(Action<SceneWriter>)`, a deferred arena-recorded mutation applied during the generated
scene-write (D4a, lands in the Reconciler/leaf). Extensions live in the `FluentGpu.Dsl` namespace; a single
`global using FluentGpu.Dsl;` shipped in the project template gives IntelliSense without `using static`.

---

## 2. THE SOURCE GENERATORS (eight portable + the leaf COM generator)

Eight portable generators + an analyzer ship in ONE `netstandard2.0` analyzer assembly `FluentGpu.SourceGen.dll`
(build-time only, **0 runtime footprint**); the COM-binding generator ships in the leaf-only
`FluentGpu.Interop.SourceGen.dll`. All are incremental (`IIncrementalGenerator`) with cacheable *equatable*
models (vendored `EquatableArray<T>`/`HierarchyInfo`/`DiagnosticInfo` from ComputeSharp's self-contained
helpers; no `ISymbol` leaks into the pipeline). The three concurrent-era generators (#8–#10) are **lowering**
generators — they fold the new hooks (whose runtime is owned by `reconciler-hooks.md`) into stackalloc'd,
interceptor-based, reflection-free call sites, reusing #4's `DepKey`-capture machinery verbatim.

```
FluentGpu.SourceGen.dll  (netstandard2.0, analyzer — build-time only)
├── 1.  ElementTypeIdGenerator  [Element]/[Prop]/[Factory] → ElementTypeId const, typed setters, UI factories
├── 2.  ModifierGenerator       [Modifier] manifest        → fluent ext methods over ModifierArena + Pack/Unpack
├── 3.  DiffPropsGenerator      (from [Element])           → bitmask DiffProps + Equals/GetHashCode (no reflection)
├── 4.  HookDepsGenerator       Use* call sites            → ReadOnlySpan<DepKey> capture (≤4-arity unmanaged)
├── 5.  ThemeBlobGenerator      [ThemeTokens]              → baked Light/Dark/HC BrushSpec blobs + resolver switch
├── 8.  LaneCaptureGenerator    UseTransition/startTransition/UseDeferredValue → lane-tagged update enqueue (§2.9)
├── 9.  BoundaryGenerator       Suspense/Boundary element  → BoundaryElement record + boundary-aware MountWriter (§2.10)
├── 10. DerivedCaptureGenerator UseDerived/UseContextSelector/UseOptimistic → projection + DepKey capture (§2.11)
└── 7.  WgpuAnalyzer + CodeFixer WGPU#### diagnostics       → compile-time DSL validation (§2.7)

(in Reconciler/leaf, NOT Dsl — D4a)
└── 6.  SceneWriterGenerator    (from [Element]/[Modifier]) → ApplyToScene(ref SceneWriter): writes SceneStore columns
                                                              (incl. the new LaneMask/BoundaryState columns)

FluentGpu.Interop.SourceGen.dll  (netstandard2.0, leaf-only)
└── COM-binding generator       harvested *.comabi.json    → hand-vtable IComObject structs + cold [GeneratedComInterface]
                                                              (OVERVIEW here; deep detail → com-interop.md)
```

> **Numbering note.** #1–#7 keep their historical IDs (the COM generator stays #7 for cross-doc stability);
> the three concurrent-era lowering generators take #8–#10. The `LocalizationGenerator` (§6.x) is **not**
> counted in this assembly — it is a tiny, optional generator shipped *with* `FluentGpu.Localization` and only
> wired in when `<FluentGpuLocalization>` ≠ `Invariant`.

### 2.1 `ElementTypeIdGenerator` — per-element setters / factories

Cacheable model (equatable POCOs only):

```csharp
sealed record ElementModel(
    HierarchyInfo Hierarchy, string TypeName, ushort TypeId, VisualKind VisualKind,
    EquatableArray<PropModel> Props,         // {Name, FullTypeName, IsNullable, DefaultExpr, DiffStrategy}
    EquatableArray<FactoryModel> Factories);
```

Emits per element: (1) `public override ushort ElementTypeId => <const>;`, (2) strongly-typed fluent
setters for element-intrinsic `[Prop]`s (single `with`), (3) `UI` factory bodies + ergonomic `[Factory]`
overloads. Duplicate `TypeId` across `[Element]` types is a WGPU0003 error.

### 2.2 `DiffPropsGenerator` — no-reflection bitmask diff / equality

Records auto-synthesize `Equals`, but we want a **bitmask diff** so the reconciler patches only the changed
SceneStore columns (exactly the hand-written `TextBlockElement.DiffProps` found in Reactor).

```csharp
[Flags] internal enum TextPropChanged : uint
{ None=0, Content=1, FontSize=2, Weight=4, Foreground=8, Wrap=16, Modifiers=1u<<30, Extensions=1u<<31 }

internal static TextPropChanged DiffProps(TextElement a, TextElement b)
{
    TextPropChanged d = 0;
    if (a.Content    != b.Content)             d |= TextPropChanged.Content;
    if (a.FontSize   != b.FontSize)            d |= TextPropChanged.FontSize;
    // … per [Prop] …
    if (!a.Modifiers.Equals(b.Modifiers))      d |= TextPropChanged.Modifiers;     // 8-byte ModifierRef compare
    if (a.Extensions.Index != b.Extensions.Index) d |= TextPropChanged.Extensions;
    return d;
}
```

`Modifiers.Equals` is a cheap 8-byte compare, but two refs can point at *equal* op sequences in different
arena slots; the reconciler resolves ties with `ModifierArena.SequenceEqual(a,b)` (a
`ReadOnlySpan<ModifierOp>.SequenceEqual`, SIMD-friendly, zero-alloc) only when the ref compare differs.

Diff strategy per prop (chosen from the symbol type, validated by the analyzer): value/enum/`StringId`/handle
→ `!=`; `BrushSpec`/`Thickness`-likes → `!=` (`IEquatable`); delegate (`Action`, `Action<T>`) → reference
compare + a present-bit (can't deep-compare); `Element` child / `ChildList` → excluded (reconciler subtree
diff owns it).

### 2.3 `ModifierGenerator` — fluent modifier methods

From `[Modifier(Kind = ModifierKind.Margin, Packing = Packing.Thickness)]` on a `partial class
ModifierManifest`, emits the shared-generic extension methods (§1.4) + the `Pack*`/`Unpack*` helpers. The
**resolver** — turning a `ModifierOp` into a SoA write — is a single dense jump-table `switch` on
`ModifierKind`, emitted *once* (not per element). To preserve acyclicity that `ApplyModifier(ref SceneWriter,
in ModifierOp)` switch is emitted alongside the scene-writer in the Reconciler (D4a), not in `Dsl`. C# 14
**user-defined compound assignment** is used for the SoA accumulators (`Edges4 +=`, dirty-rect union),
audited so the result is discarded and the target is a real column ref (the silent-realloc trap in
`dotnet10-csharp14-zero-alloc.md` §6).

### 2.4 `HookDepsGenerator` — boxing-free dependency capture (≤4-arity unmanaged)

**Verified problem:** Reactor `UseEffect(Action, params object[] deps)` + `deps.ToArray()` + boxing `Equals`
→ AOT-hostile, allocates. The contract (`foundations.md` §6.4, owned by `reconciler-hooks.md` §3.2):
`DepKey` is a **16-byte PURE-SCALAR blittable struct** — NO `[FieldOffset]` GC-ref/scalar union (that is
illegal CLR layout; this corrects the earlier draft). Reference deps live in a parallel managed `GcDepTable`
compared by `ReferenceEquals`.

```csharp
// owned by reconciler-hooks / Foundation — reproduced for context only:
public readonly struct DepKey : IEquatable<DepKey>
{
    public readonly long    Bits;   // I64/Bool/Handle/StringId, or F64-as-bits, or a GcDepTable token
    public readonly DepKind Kind;
    public bool Equals(DepKey o) => Kind == o.Kind && Bits == o.Bits;   // 8-byte int compare, no box
}
```

This generator emits the **capture** at each `Use*` call site. Primary path = C# interceptors (allow-listed
via `<InterceptorsNamespaces>FluentGpu.Generated</…>`), rewriting `UseEffect(fn, count, isOpen)` into:

```csharp
[InterceptsLocation(/* GetInterceptableLocation token */)]
internal static void UseEffect__i0(this RenderContext ctx, Action fn, int a0, bool a1)
{
    Span<DepKey> deps = stackalloc DepKey[2];        // STACK, ≤4-arity → zero heap
    deps[0] = DepKey.FromInt32(a0);
    deps[1] = DepKey.FromBool(a1);
    ctx.UseEffectCore(fn, deps);                     // ReadOnlySpan<DepKey> core (reconciler-owned)
}
```

The deps backing store is the reconciler's `DepDeps` (`[InlineArray(4)]` over `DepKey` + a rare pooled
`DepKey[]` overflow); compare is `prev.Equals(next)` over spans. Reference deps (`UseEffect(fn, someList)`)
capture a `GcDepTable` token in `Bits`, the object held alive in the per-context `GcDepTable`, compared by
`ReferenceEquals`. Source-gen lowering uses **C# 14 type-less lambda modifiers** so the capture takes
`in`/`scoped` `DepKey` without spelling types. Same treatment for `UseMemo`/`UseCallback`/`UseReducer`;
`UseState`/`UseRef` have no deps; `UseContext` keys off generated `ContextId` consts.

**Fallback** (interceptors unsupported): generic overloads `UseEffect<T0,T1>(Action, T0, T1) where T0/T1 :
unmanaged-or-IEquatable` give the same boxing-free capture. Interceptors are preferred (no generic
instantiation matrix → smaller IL), behind a `FluentGpu.UseInterceptors` build switch. The WaveeMusic hooks
(`UseImage`, `UseVirtual`, `UseDerivedBrush`, `UseSyncedLyrics`, `UseVideoSurface`, …) all author their deps
through this generator (`DepKey`-span deps), in `FluentGpu.Media`/`FluentGpu.Theme`.

### 2.5 `ThemeBlobGenerator` — baked theme-token tables

Replaces Reactor's WinUI-resource lookup with a flat AOT-static token table (this is the **T0 static tier**
of the three-tier theming model owned by `FluentGpu.Theme`; see `app-requirements-waveemusic.md` §3.3).
Input `[ThemeTokens] partial class Tok`; output a `const`-indexed enum + per-theme `ReadOnlySpan<byte>`
blob literals (data-section, `RuntimeHelpers.CreateSpan`-backed, zero static-ctor cost, no reflection):

```csharp
public enum TokenId : ushort { AccentText, CardBackground, /*…*/ Count }
internal static class ThemeTables
{
    public static ReadOnlySpan<byte> LightBlob        => [ /* BrushSpec bytes */ ];
    public static ReadOnlySpan<byte> DarkBlob         => [ … ];
    public static ReadOnlySpan<byte> HighContrastBlob => [ … ];
    public static BrushSpec Resolve(TokenId id, ThemeKind t)
        => MemoryMarshal.Cast<byte, BrushSpec>(Blob(t))[(int)id];
}
```

Theme switch = swap `ThemeContext.Current` + dirty roots that bind tokens. Tokens flow into `BrushSpec`
(value), so the paint path never holds a `string` or a COM brush ref. Cross-platform: tables are pure data;
CoreText/Metal reuse them unchanged. T1 (live `ISystemColors`) and T2 (album-art derived brushes) are
`FluentGpu.Theme` runtime concerns, not this generator — but the **dynamic** brushes converge on the same
`BrushHandle`/`BrushTable` so no new theming opcode exists. WGPU0008 errors if a `[ThemeTokens]` prop is
missing in any theme (can't ship a token absent in HighContrast).

### 2.6 `SceneWriterGenerator` — generated scene-write, homed in Reconciler/leaf (D4a)

The generated `internal void ApplyToScene(ref SceneWriter w)` writes the element's intrinsic props + resolved
modifiers into the SoA `SceneStore` columns (`VisualKind`, `Fill/Stroke:BrushHandle`, `Corners:CornerRadius4`,
`Bounds`-affecting layout input, `NodeFlags`) through the reconciler's `SceneWriter` — no reflection. **It is
emitted in `FluentGpu.Reconciler` (or the leaf backend), NOT in `FluentGpu.Dsl`,** because `Dsl`/`Hooks`
must know nothing of `Scene`/`Render` (`foundations.md` §7 cycle invariant #3). The generator reads the same
`[Element]`/`[Modifier]` metadata as #1/#3 but emits into the reconciler assembly. Clean (unchanged)
subtrees skip `ApplyToScene` entirely — a static node costs 0 in the DSL layer (the §5.4 clean-span memcpy).
`ApplyToScene` routes every device-space `Bounds`/rect write through the `Mutate()` epoch chokepoint so the
`CleanSpanWitness` baked-geometry validator (`hardened-v1-plan.md` §4.4) stays honest.

### 2.7 `WgpuAnalyzer` + CodeFixer — compile-time DSL validation

Mirrors ComputeSharp's diagnostic discipline, scoped to DSL correctness (the COM-side `FGCOM####` rules are
owned by `com-interop.md`; this is the DSL `WGPU####` set). Error unless noted:

| ID | Rule |
|----|------|
| WGPU0001 | `[Element]` type must be `sealed partial record` deriving `Element`. |
| WGPU0002 | `[Prop]` type must be diffable (value/`StringId`/handle/`BrushSpec`/delegate); no `string`/heavy COM types (cross-platform + paint-path-string ban, `foundations.md` §1). |
| WGPU0003 | Duplicate `ElementTypeId` across `[Element]` types. |
| WGPU0004 | `.Set(Action<TWinUI>)` removed → suggest `.Set(Action<SceneWriter>)` / `with`. |
| WGPU0005 | Hook called outside `Component.Render` / conditionally (rules-of-hooks; Reactor's runtime `HookOrderException` promoted to a *compile-time* error). |
| WGPU0006 (Warn) | Hook dep is a freshly-allocated lambda/array each render (would always re-fire — React exhaustive-deps analog). |
| WGPU0007 (Warn) | Element built but never returned/added (dead authoring); also flags Elements escaping `Render` scope (the §6.1 stale-arena hazard). |
| WGPU0008 | `[ThemeTokens]` prop missing in some theme (Dark/HighContrast). |
| WGPU0009 | `params object[]` in a user-authored `Use*` overload → forbid (steer to span). |
| WGPU0010 (Info) | Suggest `[FastPath]` on an element type used inside a per-item list template; also notes branch/alias slow-path (§6.1 case 2). |
| WGPU0011 (Info) | Hot `UseMemo<LargeValueStruct>` → generic instantiation footprint warning (§3.2). |
| WGPU0012 | `startTransition` / `UseTransition` body captures a `ref struct` or `Span<T>` (a transition body is deferred across frames — it cannot hold a stack-bound capture). Lowering would be unsound (§2.9). |
| WGPU0013 (Warn) | `Suspense`/`Boundary` with a fallback that itself reads a pending `UseResource` of the SAME boundary (would self-suspend → never reveal). Steer to a nested boundary (§2.10). |
| WGPU0014 | `UseDerived`/`UseContextSelector` projection lambda is impure (captures mutable non-dep state, or allocates) — the projection runs every notify and must be a pure, alloc-free function of its deps (§2.11). |
| WGPU0015 (Warn) | `UseOptimistic` reducer returns a freshly-allocated reference each call when a value-struct optimistic state would do (footprint/GC on the rollback path) (§2.11). |
| WGPU0016 (Info) | Locale-formatted value passed to `Text(...)` without going through `Loc.*` (raw `value.ToString()`/`$"{x:C}"` on a non-edge path) → suggests the edge formatter so the paint path stays string-free (§6.x). |

`FluentGpu.SourceGen.CodeFixers` auto-converts `params object[]` deps → interceptable form, adds
`sealed partial record` for WGPU0001, and offers the `Loc.Number(...)` rewrite for WGPU0016.

### 2.9 `LaneCaptureGenerator` — lane / transition / deferred-value capture (P1, P2a)

**What the runtime gives us (referenced, not redefined):** `reconciler-hooks.md` replaces the reserved phase-3
no-op with a real update queue — each `setState` enqueues an `(slot, updater, LaneMask)` record; the lane is
derived from **update cause** (input-handler = urgent; data-arrival / `await`-continuation / `startTransition`-wrapped
= transition). Automatic batching falls out (drain once per coalesced frame). The `LaneMask` column + the
per-update queue **storage** are owned by `scene-memory.md`; the lane *semantics* by `reconciler-hooks.md`. This
generator owns **only the AOT-clean capture of the update cause at the call site** — the one thing that must be
known statically, because the lane cannot be discovered by reflection at runtime.

The hard problem: `startTransition(() => { … setState …; })` must tag *every* `setState` executed inside the
callback with the **transition lane**, without a thread-static "current lane" that breaks under `await` (a
continuation resumes on the loop thread but the ambient frame is gone). React solves this with a render-internal
lane variable; under NativeAOT we cannot rely on async-local capture being cheap, and we want zero per-call
boxing. **Decision:** the generator lowers `startTransition`/`UseTransition` bodies so the lane is threaded as an
explicit `scoped in LaneScope` argument captured by the rewritten setters — no `AsyncLocal`, no boxing.

```csharp
// Author writes (React-identical):
var (isPending, startTransition) = ctx.UseTransition();
startTransition(() => { setQuery(next); setResults(Regroup(next)); });

// LaneCaptureGenerator lowers (conceptually) via an interceptor on the startTransition delegate-invoke:
[InterceptsLocation(/* token */)]
internal static void StartTransition__i7(this RenderContext ctx, Action body)
{
    using var __lane = ctx.PushLane(LaneMask.Transition);   // ref struct; scoped, stack-only; no AsyncLocal
    body();                                                  // setters inside resolve PushLane's top via a
}                                                            //   [ThreadStatic] LaneScope* (UI-thread-confined)
```

- `LaneScope` is a `ref struct` carrying the `LaneMask`; `PushLane` writes a **`[ThreadStatic]` cursor** (the
  UI thread is the sole writer — `reconciler-hooks.md` §12) and `Dispose` pops it. The `setState` trampoline
  (owned by `reconciler-hooks.md`) reads the cursor to stamp the enqueued update's lane; **outside any transition
  the cursor is `Urgent`** (the default lane), so untagged `setState` is urgent exactly as today.
- **`await` correctness (P2a).** A `startTransition(async () => { await fetch; setX(...); })` cannot rely on the
  stack-bound `LaneScope` surviving the `await`. The generator detects an `async` transition body and lowers it to
  capture the `LaneMask` *as a value* into the state machine, re-establishing the cursor on each continuation via a
  generated `ctx.ResumeLane(LaneMask.Transition)` injected before each `await`-resumption point (the lowering uses
  C# 14 type-less lambda modifiers to take the mask `scoped in`). This is what makes "batch across the `await`
  boundary" AOT-clean without async-local machinery. The enqueue + drain that realizes the batch is reconciler-owned.
- **`UseDeferredValue(value)`** lowers to a `UseDerived` projection (§2.11) that re-publishes the value on the
  transition lane — no separate runtime primitive; it shares `DerivedCaptureGenerator`'s capture.
- **Zero-alloc.** `LaneScope` is a `ref struct` (no heap); the `LaneMask` is a `[Flags] enum : uint` (8 lanes in
  v1, room for 32). The capture span for any deps inside the transition reuses #4's `stackalloc DepKey[]` path.
- **Fallback** (interceptors disabled): `startTransition`/`UseTransition` resolve to non-intercepted methods that
  push the lane via an explicit `using (ctx.Transition())` block the CodeFixer can insert; same `[ThreadStatic]`
  cursor, slightly more visible IL. **WGPU0012** errors if a transition body captures a `ref struct`/`Span<T>`
  (it is deferred across frames — a stack capture would be a use-after-return).

**13-phase placement / thread.** Capture is **phase 2/4 on the UI thread** (inside the event handler or render
body that calls `startTransition`); the lane it stamps is consumed by the **phase-3 update-queue drain** (next
frame), reconciler-owned. The generator emits nothing the render thread sees.

### 2.10 `BoundaryGenerator` — `Suspense`/`Boundary` element codegen (P2b)

`reconciler-hooks.md` (new §) owns the **runtime**: a `Boundary`/`Suspense` element the reconciler understands —
when a descendant `UseResource` is pending it mounts the boundary's fallback as a unit; on ready it atomically
swaps to content over the existing `DetachedAnim` keep-alive slab; supports nested progressive reveal and a
transition-aware keep-stale rule. This generator owns the **element shape + the boundary-aware writer**, so the
boundary is a first-class `[Element]` (not a hand-rolled component) and the reconciler can recognize it by
`ElementTypeId` (an int compare, never `GetType()`).

```csharp
// Authoring surface (in FluentGpu.Dsl):
[Element(TypeId = ElementIds.Boundary, VisualKind = VisualKind.BoundaryAnchor)]
public sealed partial record BoundaryElement(Element Content) : Element
{
    [Prop] public Func<Element>?            Fallback   { get; init; }   // delegate dep (GcDepTable, §2.4)
    [Prop] public BoundaryRevealPolicy      Reveal     { get; init; }   // Atomic | Progressive
    [Prop] public bool                      KeepStale  { get; init; }   // keep already-revealed content during a transition lane
    [Prop] public StringId                  BoundaryKey{ get; init; }   // stable identity for nested-reveal ordering
    // BoundaryGenerator emits: ElementTypeId, the typed setters, and a BoundaryMountWriter that the reconciler
    //   calls to (a) reserve the BoundaryState scene column, (b) wire Content vs Fallback subtree slots.
}

// Factory (generated; ergonomic alias hand-written):
public static partial class UI
{
    public static BoundaryElement Suspense(Element content, Func<Element> fallback) =>
        new(content) { Fallback = fallback, Reveal = BoundaryRevealPolicy.Atomic, KeepStale = true };
}
```

- **`BoundaryState` scene column** (suspended-count, revealed-epoch, keep-stale slab ref) is **owned by
  `scene-memory.md`** (column storage) with semantics owned by `reconciler-hooks.md`; this generator emits the
  `ApplyToScene` writes into it via the reconciler-homed `SceneWriterGenerator` (#6, D4a) — the boundary writer is
  the one element whose `ApplyToScene` also touches topology hints (which child slot is fallback vs content).
- **`Func<Element>? Fallback` is a delegate prop** → captured as a `Ref` dep through the `GcDepTable` discipline
  (§2.4); an inline-lambda fallback changing every render is a `ReferenceEquals` miss — **WGPU0006** warns, exactly
  as for any delegate dep, so authors hoist a stable fallback.
- **`Content` is an `Element` child** → excluded from `DiffProps` (the reconciler's subtree diff owns it, §2.2).
  This is the one `[Prop]` of type `Element` the analyzer permits (WGPU0002 carve-out for `BoundaryElement`).
- **WGPU0013** warns when a fallback reads a pending `UseResource` of the *same* boundary (self-suspend → never
  reveals); the CodeFixer suggests a nested `Suspense`.
- **AOT-clean:** no reflection — the boundary is dispatched by generated `ElementTypeId`; the reveal/keep-stale
  policy is a value enum read in the reconciler's `switch`. **Zero per-frame alloc:** the element record is the
  single edge alloc (D1); keep-stale reuses the `DetachedAnim` slab (no new allocation on swap).

**13-phase placement / thread.** The element is authored **phase 4 (UI)**; the reconciler recognizes it and does
the fallback↔content swap in **phase 5 (UI)**; keep-stale visuals ride the **phase-7 animation** + `DetachedAnim`
path already on the UI thread; nothing crosses to the render thread except the published POD columns.

### 2.11 `DerivedCaptureGenerator` — `UseDerived` / `UseContextSelector` / `UseOptimistic` capture (P6, P7)

One generator, three hooks, because all three are **"capture a projection + its deps, recompute only when the
result changes"** — the generalization of the `Context<uint>`-over-`Epoch` trick (`reconciler-hooks.md` §8). The
runtime (the derived-node cell, the notify-only-on-change gate, the optimistic rollback over transition lanes) is
owned by `reconciler-hooks.md`; this generator owns only the **boxless deps + projection lowering**.

```csharp
// Author writes:
var name = ctx.UseDerived(() => store.User.DisplayName, store.User.Id);          // P6 derived/selector
var hot  = ctx.UseContextSelector(ThemeCtx, t => t.AccentEpoch);                  // P6 context selector
var likes = ctx.UseOptimistic(serverLikes, (cur, delta) => cur + delta);         // P7 optimistic state

// DerivedCaptureGenerator lowers UseDerived (interceptor) to:
[InterceptsLocation(/* token */)]
internal static T UseDerived__i9<T>(this RenderContext ctx, Func<T> project, int dep0)
{
    Span<DepKey> __deps = stackalloc DepKey[1];           // STACK; ≤4-arity → zero heap (#4 machinery)
    __deps[0] = DepKey.FromInt(dep0);
    return ctx.UseDerivedCore(project, __deps);           // reconciler-owned: re-projects iff deps changed,
}                                                         //   notifies consumers iff PROJECTED result changed
```

- **`UseContextSelector`** is `UseDerived` whose dep is the consumed context's `DepKey` projection
  (`Context<T>.ToDepKey`, already owned by `reconciler-hooks.md` §8). The generator wires the context-id `const`
  (from `ElementTypeIdGenerator`'s `ContextId` table) as the dep, so the selector subscribes to the **projection**,
  not the fat value — the `Epoch`-as-`uint` pattern, generalized, with **no string and no box**. The projected
  result is compared with the same `DepKey`/`GcDepTable` discipline as any other change-check.
- **`UseOptimistic(base, reducer)`** captures `base` as a dep (so a server-truth change re-seeds), and lowers the
  `addOptimistic(delta)` call to enqueue an optimistic update on the **transition lane** (§2.9) — the generator
  emits the lane stamp so the optimistic state degenerates to plain state when lanes are off, exactly as the gap
  analysis requires ("degrades to manual state without lanes"). Rollback is reconciler-owned (it drops the
  optimistic overlay when the action's transition settles).
- **Purity is enforced (WGPU0014):** the projection lambda must be a pure, alloc-free function of its deps
  (it runs on every notify). The analyzer flags captures of mutable non-dep locals or allocations in the body.
  **WGPU0015** nudges `UseOptimistic` toward a value-struct optimistic state to keep the rollback path GC-free.
- **AOT / footprint:** `UseDerived<T>`/`UseOptimistic<T>` instantiate per-`T` (finite, compile-known); the
  capture path is non-generic (`DepKey`/projection-delegate). **WGPU0011** already flags a hot
  `UseDerived<LargeValueStruct>`. No reflection; the projection is a plain delegate (a `Ref` dep when it closes
  over state, parked in `GcDepTable`).

**13-phase placement / thread.** Capture + projection run **phase 4 (UI)** during `Render`; the notify-on-change
gate is evaluated as part of the 3-signal memo skip (`HasConsumedContextChanged` for the selector case); optimistic
enqueues feed the **phase-3 update queue** like any lane-tagged update. All UI-thread; render thread unaffected.

### 2.8 COM-binding generator (overview — deep detail in `com-interop.md`)

The COM-binding generator (ID #7, in leaf-only `FluentGpu.Interop.SourceGen`, kept at #7 for cross-doc
stability — see the §2 numbering note) implements the **corrected COM ruling**
(D6). Its input is a machine-harvested, runtime-self-checked **`*.comabi.json`** (winmd × ClangSharp →
checked-in JSON; `hardened-v1-plan.md` §4.2) — **no human types a vtable slot or convention.** It emits:

- **Hand-vtable `IComObject` structs** (`void** lpVtbl` + `delegate* unmanaged[MemberFunction]<…>`, `static
  abstract Guid* IID` via a `"…"u8`/`ReadOnlySpan<byte>` literal) for the **per-frame hot-path consume**
  (D3D12 command list/queue/swapchain `Present`, DComp) and **in-loop CCWs** (DWrite shaping source/sink, if
  in live per-frame shaping — `[UnmanagedCallersOnly]` + GCHandle + Interlocked refcount).
- `[GeneratedComInterface]`/`[GeneratedComClass]` for **all cold/warm** COM (UIA/TSF/OLE/DWrite-setup),
  homed in a single-assembly hierarchy without `[assembly: DisableRuntimeMarshalling]`.
- `[LibraryImport]` (never `[DllImport]`) for **flat C exports** (`D3D12CreateDevice`, `CreateDXGIFactory2`,
  `DCompositionCreateDevice`, `DWriteCreateFactory`, runtime `dxcompiler`).

`ComPtr<T>` (vendored from ComputeSharp) is the only COM lifetime primitive, **render-thread-confined and
Move-only across the seam** (`hardened-v1-plan.md` §2.1). NO `System.Runtime.InteropServices.ComWrappers` on
the hot path. The generator carries a **runtime self-check** (call a known method, assert observed behavior
vs the loaded system DLL) and a CI-RELEASE live-object QI-roundtrip gate. **All algorithm, slot-harvest,
`AbiVerify` fail-safe, `ComTracker` generation-keying, and DWrite-factory-shared detail are owned by
`com-interop.md`** — this doc only fixes the generator's *place in the toolchain* and that it is leaf-only
and AOT-clean.

---

## 3. NATIVEAOT TOOLCHAIN STORY

### 3.1 Hard guarantees (`foundations.md` §1.4, `dotnet10-csharp14-zero-alloc.md`)
- **No reflection, no `typeof`-keyed dictionaries, no `Reflection.Emit`/`Expression.Compile` on any path.**
  `ElementTypeId`/`ContextId`/`TokenId` are generated `const`s; COM IIDs are `"…"u8`/`ReadOnlySpan<byte>`
  literals; theme tables are baked blobs; all dispatch is generated `switch` or `delegate* unmanaged`.
- **ComWrappers only at the cold/warm `[GeneratedComClass]` CCW seams**, never on the hot path.
- **NativeAOT caveat internalized:** Dynamic PGO and JIT escape analysis do NOT run under NativeAOT. The
  zero-alloc guarantee rests on arenas/slabs/pools, never on "the JIT will stack-allocate my closure."

### 3.2 Avoiding generic code explosion (the AOT footprint trap)
1. Modifier extensions are generic in `T : Element` but `T` is a **reference type** → NativeAOT shares ONE
   canonical instantiation across all `T` (shared generics). The `(T)` cast is the only per-`T` cost. ✔
2. The work is in the **non-generic** `ModifierArena.Append(ModifierRef, in ModifierOp)` — exactly one body.
3. **Hook deps via interceptors** (not generic overloads) → no `UseEffect<int,bool>` value-type matrix.
4. `DepKey`/`ModifierOp` are non-generic; `ReadOnlySpan<DepKey>.SequenceEqual` is one shared body.
5. Container children are non-generic `ChildList`, never `List<Element>`/`ImmutableArray<T>`.
6. WGPU0011 flags a hot `UseMemo<LargeValueStruct>` (the one bounded generic risk).

### 3.3 Project settings (root `Directory.Build.props` — owned here)
```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <LangVersion>14.0</LangVersion>                <!-- pin; never 'latest' -->
  <Nullable>enable</Nullable>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <ImplicitUsings>enable</ImplicitUsings>

  <PublishAot>true</PublishAot>
  <IsAotCompatible>true</IsAotCompatible>          <!-- turns on AOT+trim analyzers everywhere -->

  <!-- L9 localization knob (§3.7). Default 'Embedded' = minimal AOT-static formatter, ICU still dropped.
       'Icu' opts ICU back in (footprint cost noted); 'Invariant' = today's culture-blind behavior. -->
  <FluentGpuLocalization Condition="'$(FluentGpuLocalization)'==''">Embedded</FluentGpuLocalization>
  <InvariantGlobalization Condition="'$(FluentGpuLocalization)'!='Icu'">true</InvariantGlobalization>
  <!-- when 'Icu': InvariantGlobalization is NOT forced true → framework ICU ships; see §3.7 footprint -->

  <TrimMode>full</TrimMode>
  <UseSystemResourceKeys>true</UseSystemResourceKeys>
  <EventSourceSupport>false</EventSourceSupport>
  <StackTraceSupport>false</StackTraceSupport>     <!-- release; flip on for debug -->
  <DebuggerSupport>false</DebuggerSupport>         <!-- release -->
  <MetadataUpdaterSupport>false</MetadataUpdaterSupport>
  <HttpActivityPropagationSupport>false</HttpActivityPropagationSupport>
  <NullabilityInfoContextSupport>false</NullabilityInfoContextSupport>
  <BuiltInComInteropSupport>false</BuiltInComInteropSupport>

  <!-- GC: Workstation + Concurrent, DATAS ON (default — do NOT disable) -->
  <ServerGarbageCollection>false</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>

  <IlcOptimizationPreference>Size</IlcOptimizationPreference>
  <IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>
  <SkipLocalsInit>true</SkipLocalsInit>            <!-- module-wide; hot stackalloc paths skip zero-init -->
</PropertyGroup>
```
`[assembly: DisableRuntimeMarshalling]` on `FluentGpu.Renderer`/`Pal` (hand-vtable `calli`, all blittable);
the source-gen COM hierarchy lives in a separate assembly **without** that attribute (it uses `in`/`out`).
`[module: SkipLocalsInit]` in every runtime assembly. Runtime, once at engine init:
```csharp
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;   // suppress FOREGROUND gen2 for app life
```

### 3.4 Trimming / feature switches + the assembly graph (controls / devtools / localization)
- No `rd.xml` for DSL/Hooks/Scene (no reflection → trimmer resolves the full call graph). The three concurrent-era
  generators (#8–#10) emit only static `switch`/interceptor code → **no new reflection, no new `rd.xml`**.
- ONE small `ILLink.Descriptors.xml` in the COM-CCW leaf roots the source-gen'd ComWrappers vtable methods
  (reached only from native — trimmer can't see the edge).
- Feature switch `FluentGpu.EnableDevtools` (default false, trimmed) gates the **`FluentGpu.Devtools`** live
  inspector module (tree/props/hook-cell/re-render-highlight/layout-overlay) → **0 bytes in release**. The module
  *content* is owned by `devtools.md`; this doc owns only that it is a `<FeatureSwitchDefinition>`-gated assembly
  trimmed out of release, built on the retained tree + the `EnableDevtools` switch (its build placement here keeps
  the footprint ratchet honest about it being 0 bytes by default).
- **`FluentGpu.Controls`** (the accessible-by-default control kit — Checkbox/Radio/Switch/Slider/ComboBox/
  TextField/Tabs/Menu/Dialog/Scrollbar; content owned by `controls.md`) is a **normal portable assembly** in the
  graph (refs `Dsl`/`Hooks`/`Foundation` only, like any app), trim-rooted only by what the app uses. It is **not**
  feature-switched (controls are pay-for-what-you-reference via the trimmer). Its own per-control footprint rides
  the §3.6 per-element ≤1.5 KB marginal target. Sequenced *after* the L1/L2/L4/L6 seams stabilize (gap analysis).
- **`FluentGpu.Localization`** (§3.7) ships only when `<FluentGpuLocalization>` ≠ `Invariant`; in `Invariant`
  mode the trimmer drops it entirely (no `Loc.*` references → 0 bytes). Its baked culture blobs are data-section
  (`RuntimeHelpers.CreateSpan`), not code, so they ratchet against the declared `<FluentGpuCultures>` set.

### 3.5 C# 14 / .NET 10 features used (and where)
- `field` keyword — validating/normalizing edge setters on `Element`/`Component`/`RenderContext` (clamp,
  intern `StringId`, null-guard) stay auto-backed. Edge maintainability.
- **Extension members / blocks** — hang `IsEmpty`/`Width`/operators on `ref struct` views and the
  `[FastPath]` builder; no adapter-class alloc (extension operators are compile-time static dispatch only).
- **User-defined compound assignment** (`+=`, instance `++`) — SoA accumulators in the modifier resolver and
  layout math (with the result-unused/variable-target audit).
- **`allows ref struct`** generic over a ref-struct sink — the DrawList leaf walk
  (`Walk<TSink>(…) where TSink : IDrawSink, allows ref struct`) is owned by `gpu-renderer.md`; the DSL relies
  on the same constraint for the `[FastPath]` emitter sink.
- `params ReadOnlySpan<T>` (factories, §1.3); first-class span conversions; `[InlineArray(N)]` for `DepDeps`;
  `SearchValues<char>` for text classification (Text seam); `FrozenDictionary` for build-once tables.
- **Paint path is culture-free regardless of the L9 mode** — all locale *value* formatting happens at the
  Element edge → interned `StringId` (§3.7); DWrite does locale glyph/digit shaping below the seam.

### 3.6 Footprint ratchet + measurement (D8)

| Component | IL/binary target |
|-----------|------------------|
| `FluentGpu.Dsl` (records, factories, arena, modifier resolver, `BoundaryElement`) | ≤ 95 KB |
| `FluentGpu.Hooks` (authoring surface + DepKey capture + lane/transition/derived/optimistic capture) | ≤ 55 KB |
| Generated per-app code (setters/diff/factories for ~60 element types + boundary/lane/derived lowering) | ≤ 90 KB |
| **DSL + Hooks + generated** | **≤ ~240 KB** *(was ≤220 KB; +20 KB ceiling for the concurrent-era lowering)* |
| `FluentGpu.Localization` (`Embedded` mode, per declared culture blob) | ~40–180 KB / culture (separate tier) |
| `FluentGpu.Controls` (per control referenced) | ≤ 1.5 KB marginal/control (rides the per-element ratchet) |
| `FluentGpu.Devtools` (`EnableDevtools` off) | **0 KB** (feature-switched out of release) |
| `FluentGpu.Win32.D3D12` (extracted, graphics-trimmed) | ≤ 250 KB |
| `FluentGpu.Win32.DWrite` + `DComp` (authored) | ≤ 120 KB |
| **Whole "rounded-rect + text" self-contained AOT exe** (`Invariant`/no-controls baseline) | **≤ 5.5 MB** |

Measurement pipeline (the CI gate lives in `FluentGpu.Validation`/`validation.md`; this doc sets the
budgets): `dotnet publish -r win-x64 -c Release` → `IlcGenerateMstatFile=true` + `IlcGenerateMapFile=true` →
`sizoscope`/a small `.mstat` `size-report` tool attributes bytes per assembly/type/method; **non-suppressible
ratchet fails CI if any row regresses > 5%**; per-element marginal cost target ≤ 1.5 KB generated IL
(regression test keeps the "60 element types" line honest). **The ratchet is keyed by the app's declared
`<FluentGpuLocalization>` mode + `<FluentGpuCultures>` set** — `Icu` (with framework ICU) is a separate budget
tier, so opting into ICU does not silently bust the 5.5 MB rounded-rect line (it is measured against its own
declared ceiling). The lowering generators (#8–#10) must hold the **+20 KB** DSL+Hooks+generated ceiling bump;
a regression beyond it fails CI.

### 3.7 L9 — localization build config + the edge formatter (`FluentGpu.Localization`)

**The problem the gap analysis names.** `Directory.Build.props` set `InvariantGlobalization=true`, which **drops
ICU** to shrink the AOT binary and remove a culture-data dependency. DWrite still does locale glyph/digit shaping
(Arabic-Indic digits, BiDi, line-break) below the seam — but `InvariantGlobalization` also makes **value
formatting culture-blind**: `12345.67.ToString("C", culture)` ignores the culture, `DateTime`/number grouping and
decimal separators are invariant, and there is no plural/select. A TIER-1 framework cannot ship "€1.234,56" as
"1234.56" in `de-DE`. So we design the trade explicitly rather than living with the silent default.

**Decision — a three-way MSBuild knob, default to a minimal embedded formatter, never put a `string` on the paint
path.** `<FluentGpuLocalization>` ∈ `{ Invariant, Embedded, Icu }` (D7b; wired in §3.3):

| Mode | What ships | Formatting capability | Footprint delta | When |
|---|---|---|---|---|
| `Invariant` | nothing (today's behavior) | culture-blind invariant only | 0 | app never formats locale values (e.g. a dev tool) |
| **`Embedded`** *(default)* | `FluentGpu.Localization` + a baked locale-data blob for the **shipped culture set** | number / currency / percent / date-time (CLDR patterns) + **CLDR plural/select rules**; no collation, no full ICU corpus | **~40–180 KB** per culture-bundle (a sliced CLDR table, baked like the theme blob) | the default for shipping apps |
| `Icu` | framework ICU (`InvariantGlobalization` NOT forced) | full ICU — collation, full date/number, MessageFormat, all cultures | **~28 MB `icudt`** (or a sliced `icudt` ≈ 2–8 MB via `IcuTrimming`) | apps that need collation / arbitrary runtime cultures / full MessageFormat |

**Why `Embedded` is the default (not `Icu`, not `Invariant`).** The 5.5 MB self-contained-exe budget (D8) cannot
absorb a 28 MB ICU payload by default, and `Invariant` is wrong for any localized product. The embedded formatter
is the FluentGpu-shaped answer: a **baked, AOT-static, allocation-bounded** number/date/currency/percent + plural
formatter whose data is generated exactly like `ThemeBlobGenerator`'s blobs (§2.5) — `RuntimeHelpers.CreateSpan`
data-section literals, **zero static-ctor cost, no reflection**. Only the cultures the app declares
(`<FluentGpuCultures>en-US;de-DE;ar-SA;ja-JP</FluentGpuCultures>`) are baked, so the footprint scales with the
shipped culture set, not all 800+ CLDR locales.

**The edge formatter API (in `FluentGpu.Localization`, portable, no GPU/OS types):**

```csharp
public static class Loc   // edge-only; called inside Component.Render (phase 4, UI thread). Returns StringId.
{
    public static StringId Number  (double v, in NumberFormat fmt = default);
    public static StringId Currency(decimal v, CurrencyCode code);
    public static StringId Percent (double v, int fractionDigits = 0);
    public static StringId Date    (DateTime v, DateStyle style, TimeStyle time = TimeStyle.None);
    public static StringId Plural  (int count, MessageId message);   // CLDR one/few/many/other → interned template
    // current culture flows from IPlatformLocale (PAL seam, below); explicit-culture overloads also exist.
}
```

- **No-string-on-paint-path (the load-bearing rule).** `Loc.*` **format → intern → return `StringId`** at the
  edge. The formatter writes into a `[ThreadStatic]` UI-thread scratch `Span<char>` (`[SkipLocalsInit]`, no heap),
  then interns via `StringTable.Intern(ReadOnlySpan<char>)` (`foundations.md` §… `StringInterner`, the
  `GetAlternateLookup<ReadOnlySpan<char>>` probe so no `ToString()` allocates a transient `string`). The resulting
  `StringId` flows into `TextElement(StringId)` and downstream into `text.md`'s `TextLayoutRequest`/`StringId`
  pipeline **exactly as any other text** — the paint path (phases 6–13) never sees a `string`, a `CultureInfo`,
  or a formatting call. This is the `text.md` §6 "strings = `StringId` everywhere; source UTF-16 owned at the
  edge; no `string` on the paint path" contract, upheld.
- **Re-format only on input change.** `Loc.*` is naturally memoizable: wrap in `UseMemo`/`UseDerived` keyed on
  `(value, cultureEpoch)` so a steady value doesn't re-intern. The **`cultureEpoch`** is the change-trigger — see
  the PAL seam.
- **`LocalizationGenerator`** (the tiny generator shipped with `FluentGpu.Localization`, only when mode ≠
  `Invariant`): bakes the declared cultures' CLDR slices into `ReadOnlySpan<byte>` blobs + a `CultureId` enum +
  a resolver `switch` (same shape as `ThemeTables.Resolve`), and lowers `[Message]`-annotated string templates
  into `MessageId` consts + plural-rule tables. It is **not** counted in `FluentGpu.SourceGen` (§2 numbering note).

**The locale PAL seam (referenced — owned by `pal-rhi.md`).** Current culture is a platform/OS fact that can
change at runtime (Windows `WM_SETTINGCHANGE`/`GetUserDefaultLocaleName`; macOS `NSLocale`). It is modeled
**exactly like `ISystemColors`** (`pal-rhi.md` §8.1): a stable instance + a coarse `uint Epoch` so consumers
subscribe to a `Context<uint>` (boxless `DepKey`-projectable) and re-read the fat snapshot on demand — they do
**not** put a fat `CultureInfo` in a context (it would box).

```csharp
// OWNED BY pal-rhi.md (this doc only references the shape it consumes):
public interface IPlatformLocale
{
    uint     Epoch        { get; }   // bumped on OS locale change (WM_SETTINGCHANGE / NSLocale change)
    StringId CurrentBcp47 { get; }   // e.g. "de-DE"; interned; feeds text.md TextLayoutRequest.locale too
}
```

`FluentGpu.Localization` reads `IPlatformLocale.CurrentBcp47` to pick the baked culture and exposes
`Context<uint>` over `Epoch` so a locale switch dirties the roots that format values (same mechanism as a theme
switch, §2.5). **macOS boundary:** the formatter + baked blobs are pure data (portable, no OS types); only
`IPlatformLocale`'s impl is platform-specific (a `pal-rhi.md` leaf), so the macOS port needs **zero localization
changes** — `Pal.Cocoa` fills `IPlatformLocale` from `NSLocale`, the blobs and `Loc.*` are unchanged.

**RTL icon/image mirroring (the other half of L9, paired with L5).** The `AutoMirror` flag on `Image`/`Path`
(mirror the glyph/path when `FlowDirection` resolves RTL) is an **authoring-surface flag this doc owns** as a
`[Prop]` on `ImageElement`/`PathElement`, but its **resolution** (consult resolved `FlowDirection` at
`WriteLayout`) is owned by `layout.md` §4.1 (the RTL `WriteLayout` boundary). This doc only adds the prop +
the WGPU0016-adjacent steer; layout.md decides the physical transform. Folded here because it rides the same
L9 localization story and the same edge.

**Failure / edge cases (localization):**
- *Culture not in the baked set (`Embedded`).* `Loc.*` falls back to the nearest baked parent culture, else
  invariant; a DEBUG assert + RELEASE once-log names the missing culture so the `<FluentGpuCultures>` list can be
  fixed. Never throws on the render path.
- *`Icu` mode footprint.* The 28 MB `icudt` blows the D8 budget — the footprint ratchet (§3.6) treats `Icu` as a
  **separate budget tier** (the gate compares against the app's declared mode, not the 5.5 MB rounded-rect line).
- *Format on a non-edge / paint path.* WGPU0016 flags raw `value.ToString(culture)` / `$"{x:C}"` reaching
  `Text(...)` without `Loc.*`; the CodeFixer rewrites it. This keeps the no-string-on-paint-path invariant honest.
- *Plural rule mismatch.* CLDR plural categories (one/few/many/other) vary per language; a `[Message]` template
  missing a category required by a baked culture is a **build error** (analog of WGPU0008 for themes).

---

## 4. DATA FLOW — authoring → scene, and WHERE/WHICH THREAD (13-phase)

The DSL itself is touched only on the **UI thread** in phases 4–5 (`hardened-v1-plan.md` §2.2). It produces
the immutable `Element` edge that the reconciler consumes; the render thread never sees `Element`, the DSL,
or the modifier arena.

```
USER CODE (Component.Render, phase 4 — UI thread)      FluentGpu.Dsl                Reconciler (phase 5 — UI thread)
──────────────────────────────────────────────        ───────────                  ─────────────────────────────────
Text("Hi")                 ──factory──▶  new TextElement(Intern("Hi"))   (1 record, edge alloc)
   .Bold()                 ──ext T──▶    ModifierArena.Append(None,Bold)   (arena bump, 16B)
   .Margin(8).FontSize(28) ──ext T──▶    Append(ref, …) ×2                 (arena bump, contiguous)
        │                                  el with { Modifiers = ref }     (≤1 record copy total)
        ▼
   returned Element tree   ──────────────────────────────────────────────▶ Reconciler.Diff(prev, cur)
                                                                              │ DiffProps bitmask (gen #3)
                                                                              │ ModifierArena.SequenceEqual on tie
                                                                              ▼
                                                                     el.ApplyToScene(ref SceneWriter)  (gen #6, here)
                                                                              │ writes SoA columns via ISceneBackend
                                                                              ▼  through Mutate() epoch chokepoint
                                                                     SceneStore SoA → [layout → 6.5 → anim → PUBLISH]
                                                                              ▼  PUBLISH(13a): UI seals SceneFrame
                                                                     RENDER THREAD: record → batch → submit → present
```

**Phase / thread placement the DSL+toolchain own or touch:**
- **Phase 2/4 LANE CAPTURE (UI thread):** `LaneCaptureGenerator` (#8) lowers `startTransition`/`UseTransition`
  bodies — pushes a `[ThreadStatic]` `LaneScope` (ref struct, no heap), re-established before each `await`
  continuation for the async case. The lane it stamps is consumed by the reconciler's **phase-3 update-queue
  drain** (next frame). `Loc.*` edge formatting (§3.7) also runs here → interned `StringId`.
- **Phase 4 RENDER (UI thread):** `Component.Render` runs the DSL — factories, modifier chain, child-list
  copy, `Suspense`/`Boundary` element construction (`BoundaryGenerator` #9), `UseDerived`/`UseContextSelector`/
  `UseOptimistic` capture (`DerivedCaptureGenerator` #10). Edge Gen0 alloc only (Element records, user closures,
  virtualization `Element[]` from `ArrayPool`). Memo-skip (the 3-signal gate
  `SelfTriggered || propsChanged || HasConsumedContextChanged`) skips most subtrees — `SubtreeDirty` is traversal
  scope only, never a skip-decision input. The `UseDerived` notify-on-change gate folds into this skip.
- **Phase 5 RECONCILE (UI thread):** generated `DiffProps` + `SceneWriterGenerator.ApplyToScene` patch the
  SoA store (incl. the new `LaneMask`/`BoundaryState` columns, storage owned by `scene-memory.md`); keyed-LIS
  re-authored on arena scratch; the reconciler does the `Boundary` fallback↔content swap here (keep-stale rides
  the `DetachedAnim` slab into phase 7).
- **Effect timing the deps generator feeds:** `UseLayoutEffect` flush at **phase 6.5** (RATIFIED), `UseEffect`
  at **phase 12**; `setState` in an effect ⇒ mark dirty + frame N+1 (NO synchronous bounded re-loop in v1).
- **PUBLISH (13a) and phases 8–11 (RENDER thread):** the DSL/arena are out of scope; the render thread reads
  the immutable POD `SceneFrame` snapshot. The modifier arena + the `[ThreadStatic]` `LaneScope` cursor are
  `Reset()`/empty on the UI thread at frame end and are NEVER shared with the render thread.

Per-frame heap on this path: user closures (irreducible, the programming model) + ≤1 Element record per
*changed* node + (virtualization) one pooled `Element[]` window chunk. Everything else (modifiers, child
lists, deps, scene writes) is arena/slab/span.

---

## 5. ZERO-ALLOC & THREAD-CONFINEMENT STORY

- **Modifier arena is `[ThreadStatic]` and UI-thread-confined.** It is a `ChunkedArena` (native-backed,
  reserve-then-commit, O(1) add-chunk, no LOH cliff); `Reset()` per frame; native high-water counter gates
  growth. The render thread never touches it.
- **`Element` is edge-only and short-lived.** It crosses no thread boundary; the reconciler consumes it in
  phase 5 and the immutable `SceneFrame` (POD, value-copied columns) is the only thing published.
- **No ComPtr, no COM, no GPU handle in `Dsl`/`Hooks`.** Per `foundations.md` §7 invariant #3, these
  assemblies are GPU/OS-agnostic; the reconciler is the only bridge.
- **`stackalloc DepKey[]` capture is ≤4-arity** → zero heap for 95%+ of hooks (`[InlineArray(4)] DepDeps`),
  pooled overflow for the rare >4 case. The lane/derived/optimistic lowering (#8–#10) **reuses this exact
  `stackalloc` path** — no new per-call allocation.
- **`LaneScope` is a `ref struct` + a `[ThreadStatic]` cursor, UI-thread-confined.** No `AsyncLocal`, no boxing;
  the async-transition case re-establishes the cursor by passing the `LaneMask` `scoped in` to the continuation
  (a value copy of an 8-lane `enum : uint`, zero heap).
- **`Suspense`/`Boundary` keep-stale reuses the `DetachedAnim` slab** (no new allocation on swap); the boundary
  is the single edge `Element` record alloc, like any element.
- **`Loc.*` edge formatting is alloc-bounded:** formats into a `[ThreadStatic]` `[SkipLocalsInit]` `Span<char>`
  scratch, interns via `StringTable.Intern(ReadOnlySpan<char>)` (alternate-lookup probe, no transient `string`),
  returns a `StringId`. The paint path stays string-free; re-format only on `(value, cultureEpoch)` change.
- **Generated code is `[module: SkipLocalsInit]`-friendly** (`ModifierOp`/`DepKey`/`LaneMask`/format-scratch
  fully overwritten before read; the WGPU-internal analyzer verifies).

---

## 6. FAILURE / EDGE CASES

1. **Modifier chain crosses an arena `Reset()` (Element stashed across frames).** A re-added Element's
   `ModifierRef` points at a recycled arena slot. **Mitigation:** `ModifierRef.Flags` carries a 4-bit
   `arenaEpoch`; `ModifierArena` checks it on read — on mismatch the ref is stale → the reconciler
   re-materializes from intrinsic props only, asserts in DEBUG, drops modifiers + logs once in RELEASE.
   WGPU0007 flags Elements escaping `Render` scope statically.
2. **Branch/alias of a modified element** (`var b = a.Bold(); var c = a.Italic();`). `a`'s ref is no longer
   the arena tail → slow-path copy (§1.4) — correct, one extra copy, rare; WGPU0010 notes it.
3. **`?:` returning `null` child.** Handled: `ChildList.From` strips nulls. `[FastPath]` ref-struct builders
   cannot appear in a `?:`-to-`Element` → WGPU error steers to the record path.
4. **`ModifierOp.Value` precision.** `double`→`ulong` is bit-exact. `Thickness` packed as 4×`Half` loses
   sub-pixel precision beyond Half range; WGPU warns past Half-exact range; `[Modifier(Packing=FullThickness)]`
   opts into a 2-slot (32-byte) op for the rare high-precision case.
5. **Interceptors disabled / unsupported tooling.** Generator detects the absent `<InterceptorsNamespaces>`
   switch and falls back to generic-overload dep capture (§2.4) — same semantics, slightly more IL.
6. **Two themes with mismatched token sets.** WGPU0008 (Error) at compile time.
7. **Delegate dep** (`UseEffect(fn, onClick)`). Captured as a `GcDepTable` token, `ReferenceEquals`-compared.
   An inline lambda changes every render → WGPU0006 warns.
8. **Element `with` on a `[FastPath]` type.** The ref-struct builder has no `with`; the analyzer routes `with`
   to the materialized record (implicit conversion first). Documented limitation.
9. **COM IID byte-order.** The COM generator emits little-endian field-split bytes; a golden test compares
   against `Guid` round-trip (owned by `com-interop.md`).
10. **Incremental-generator path/line churn under interceptors.** Editing a file above a `Use*` call shifts
    its `GetInterceptableLocation` token; the generator re-emits incrementally (cacheable model → only the
    affected file re-runs). Noted as a known cost; the generic-overload fallback is path-insensitive.
11. **Transition body holds a stack capture.** `startTransition(() => UseSomeSpan(span))` would be a
    use-after-return (the body is deferred across frames). **WGPU0012** errors; the lane lowering refuses it.
12. **`await` inside a transition.** The stack-bound `LaneScope` cannot survive the `await`; `LaneCaptureGenerator`
    detects `async` bodies and re-establishes the lane via a generated `ResumeLane(mask)` before each resumption
    (§2.9) — batches the post-`await` setStates onto the transition lane (P2a), AOT-clean, no `AsyncLocal`.
13. **Self-suspending boundary.** A `Suspense` fallback that reads the *same* boundary's pending `UseResource`
    never reveals → **WGPU0013** warns; CodeFixer suggests a nested boundary (§2.10).
14. **Impure `UseDerived` projection.** A projection that allocates or captures mutable non-dep state would mis-fire
    the notify-on-change gate → **WGPU0014** error (§2.11).
15. **`UseOptimistic` without lanes.** With `<lanes off>` the optimistic enqueue degenerates to plain state
    (gap-analysis-required graceful degradation, §2.11); rollback simply never has a transition to settle.
16. **Localization edge cases** — culture not in the baked `Embedded` set (nearest-parent fallback + log), `Icu`
    footprint tier, format escaping onto the paint path (WGPU0016), missing CLDR plural category (build error):
    all enumerated in §3.7.

---

## 7. CROSS-PLATFORM (macOS) BOUNDARY

```
                       PORTABLE (no OS/GPU types)                       │  WINDOWS-SPECIFIC (leaf only)
 ───────────────────────────────────────────────────────────────────── │ ──────────────────────────────
 FluentGpu.Dsl        Element records, ModifierArena, ModifierOp,        │
 FluentGpu.Hooks      DepKey capture, factories, BrushSpec, StringId,    │
                      ChildList, ThemeBlob tables, BoundaryElement,      │
                      LaneScope/LaneMask, Loc.* edge formatter API       │
 FluentGpu.SourceGen  Element/Modifier/DiffProps/HookDeps/Theme/Lane/    │  COM-binding generator emits
                      Boundary/Derived + WGPU analyzer (pure Roslyn,     │   Windows COM structs (data only)
                      runs on any OS)                                    │
 FluentGpu.Localization  baked CLDR blobs + Loc.* (pure data, any OS)    │  IPlatformLocale impl (NSLocale vs
 FluentGpu.Controls / .Devtools  portable (refs Dsl/Hooks/Foundation)    │   WM_SETTINGCHANGE) is a Pal leaf
 ───────────────────────────────────────────────────────────────────── │ ──────────────────────────────
 SceneWriter gen      portable (writes portable SoA POD)                 │
 COM lifetime         —                                                  │  ComPtr<T>, vtbl structs, IIDs,
 graphics interop     —                                                  │  D3D12/DXGI/DWrite/DComp bindings
```

- The **entire authoring DSL + hooks + ALL eight portable generators are OS- and GPU-agnostic** — they emit
  `Element` records (incl. `BoundaryElement`), lane/derived/optimistic lowering, and (via the reconciler-homed
  scene-writer) write portable POD into `SceneStore`. A Metal/CoreText backend needs **zero DSL changes**.
- The ONLY Windows-specific generator output is the COM-binding generator's vtbl structs; on macOS the
  analogous (smaller — Objective-C `msgSend`, not COM) interop is a sibling generator. Authoring code never
  sees either.
- `BrushSpec`/`ImageSource`/`StringId`/`FontWeight`/`Palette` are portable value structs; DWrite-specific
  font realization lives behind `IFontSystem`, not the DSL.
- **`FluentGpu.Localization` is pure data + portable code** — the baked CLDR blobs and `Loc.*` are OS-agnostic;
  only the **current-culture fact** (`IPlatformLocale`, owned by `pal-rhi.md`) is platform-specific, filled from
  `NSLocale` on macOS and `WM_SETTINGCHANGE`/`GetUserDefaultLocaleName` on Windows. The macOS port needs **zero
  localization changes** (same as the theme blobs). RTL icon `AutoMirror` resolution is `layout.md`'s, also portable.

---

## 8. WHAT THIS SUBSYSTEM OWNS (authority surface)

- **Types:** `Element` (abstract record), `ModifierRef`, `ExtrasRef`, `ModifierOp`, `ModifierKind`,
  `ModifierFlags`, `ModifierArena`, `ChildList`, `UI` (factory class), `TokenId`/`ThemeTables` shape,
  the `[FastPath]` builder pattern, `BrushSpec` (DSL-side value struct shape — size confirmed by Render),
  **`BoundaryElement`** (+ `BoundaryRevealPolicy`), **`LaneScope`** (ref struct) + the **`LaneMask`** enum
  *authoring shape* (the column storage is `scene-memory.md`'s; the lane *semantics* are `reconciler-hooks.md`'s),
  and the **`Loc`** edge-formatter API + `NumberFormat`/`CurrencyCode`/`DateStyle`/`MessageId`/`CultureId` shapes.
- **Attributes:** `[Element]`, `[Prop]`, `[Factory]`, `[Modifier]`, `[ThemeTokens]`, `[FastPath]`, **`[Message]`**
  (CLDR plural/select template, consumed by `LocalizationGenerator`).
- **Generators:** `ElementTypeIdGenerator`, `ModifierGenerator`, `DiffPropsGenerator`, `HookDepsGenerator`,
  `ThemeBlobGenerator`, **`LaneCaptureGenerator`**, **`BoundaryGenerator`**, **`DerivedCaptureGenerator`**,
  `WgpuAnalyzer`+CodeFixer (all in `FluentGpu.SourceGen`); the **overview** of the COM-binding generator
  (`FluentGpu.Interop.SourceGen`); the **`LocalizationGenerator`** (shipped with `FluentGpu.Localization`,
  not in `FluentGpu.SourceGen`). `SceneWriterGenerator` is authored here but **homed in the Reconciler/leaf** (D4a).
- **Hooks authoring surface (codegen only — runtime owned by `reconciler-hooks.md`):** the `DepKey`-span capture
  form of `UseEffect`/`UseMemo`/`UseCallback`/`UseReducer`/`UseState`/`UseRef`/`UseContext`, **plus the lowering
  of `UseTransition`/`startTransition`/`UseDeferredValue`, `Suspense`/`Boundary`, `UseDerived`/`UseContextSelector`,
  and `UseOptimistic`** (cells/scheduler/lane-queue are reconciler-owned).
- **Baseline:** the root `Directory.Build.props` (net10/LangVersion14/AOT/trim/GC), `[module:
  SkipLocalsInit]`, `GCSettings.SustainedLowLatency`, `DisableRuntimeMarshalling` placement, the
  `<InterceptorsNamespaces>` switch, the **`<FluentGpuLocalization>` + `<FluentGpuCultures>` knobs (L9)**, the
  build/trim placement of `FluentGpu.Controls`/`FluentGpu.Devtools`/`FluentGpu.Localization`, the `WGPU####`
  analyzer ID range, and the footprint ratchet budgets.

---

## Implemented from the gap analysis

The following `core-fundamentals-gap-analysis.md` rows are now folded into core **here** (codegen + build config),
with the runtime semantics referenced to their owning docs. There is no "v2" / "defer" framing for any of these.

| Gap | What this doc now implements | Where | Runtime owner (referenced) |
|---|---|---|---|
| **P1** — priority lanes + transition demotion (`UseTransition`/`startTransition`/`UseDeferredValue`) | `LaneCaptureGenerator` (#8): AOT-clean lane capture at the call site — `[ThreadStatic]` `LaneScope` ref struct, async-`await`-safe `ResumeLane` re-establish (batches post-`await`), `UseDeferredValue` lowered onto `UseDerived`. WGPU0012. | §2.9, §2.11 | `reconciler-hooks.md` (lane bitmask, update queue, executor) |
| **P2a** — automatic batching across handlers / `await` | The async-transition lowering threads the lane as a value across continuations so the reconciler's phase-3 drain folds them into one render. | §2.9 | `reconciler-hooks.md` (phase-3 update-queue drain) |
| **P2b** — Suspense boundary (atomic reveal, nested progressive, keep-stale) | `BoundaryGenerator` (#9): `BoundaryElement` `[Element]` record + boundary-aware `MountWriter`; `Func<Element>` fallback as a `GcDepTable` delegate dep; `Element` `[Prop]` carve-out; `UI.Suspense(...)` factory. WGPU0013. | §2.10 | `reconciler-hooks.md` (fallback↔content swap, `DetachedAnim` keep-alive) |
| **P6** — context selector / derived-state node | `DerivedCaptureGenerator` (#10): `UseDerived`/`UseContextSelector` projection + `DepKey` deps capture (the `Epoch`-as-`uint` trick generalized, boxless). WGPU0014. | §2.11 | `reconciler-hooks.md` §8 (derived node, notify-on-change) |
| **P7** — `UseOptimistic` + async-action | `DerivedCaptureGenerator` lowers `UseOptimistic` capture, stamping the optimistic enqueue with the transition lane (degenerates to plain state when lanes are off). WGPU0015. | §2.11 | `reconciler-hooks.md` (rollback over transition lanes) |
| **L9** — i18n formatting + RTL icon mirroring (the `InvariantGlobalization`/ICU trade) | The `<FluentGpuLocalization>` three-way knob (`Invariant`/`Embedded`/`Icu`) + footprint trade table; the `FluentGpu.Localization` edge formatter (`Loc.*` → interned `StringId`, no-string-on-paint-path); `LocalizationGenerator` baking declared cultures' CLDR slices (like the theme blobs); the `[Message]` plural/select attribute; the `IPlatformLocale` `Epoch`/snapshot consumption; the `AutoMirror` authoring `[Prop]`. WGPU0016. | §3.3, §3.7 | `text.md` §6 (`StringId` edge), `pal-rhi.md` (`IPlatformLocale` seam), `layout.md` §4.1 (`AutoMirror` resolution) |
| **L8 / L12 (build placement only)** — control kit + live devtools | The assembly-graph + trim/footprint placement of `FluentGpu.Controls` (pay-per-reference) and `FluentGpu.Devtools` (`EnableDevtools`-gated, 0 bytes release). Content owned by `controls.md`/`devtools.md`. | §3.4, §3.6 | `controls.md`, `devtools.md` |

**Net new toolchain surface:** generators #8–#10 (lane/transition, boundary, derived/optimistic) + the
`LocalizationGenerator`; the `<FluentGpuLocalization>`/`<FluentGpuCultures>` MSBuild knobs; WGPU0012–WGPU0016;
the `BoundaryElement`/`LaneScope`/`Loc`/`[Message]`/`AutoMirror` authoring shapes; the +20 KB DSL+Hooks+generated
ceiling bump and the per-mode/per-culture footprint tiers.

---

## Changed vs the original synthesis (`../archive/dsl-aot-toolchain.md`)

1. **DepKey corrected to a pure-scalar 16-byte struct + parallel `GcDepTable`.** The old §2.5 used
   `[StructLayout(Explicit)]` with `[FieldOffset(8)] object? Ref` overlapping `[FieldOffset(8)] ulong Bits`
   — an **illegal CLR layout** (GC-ref/scalar union). Folded the AUTH ruling: `DepKey` is pure scalar;
   reference deps go to a managed `GcDepTable` compared by `ReferenceEquals`. (`reconciler-hooks.md` §3.2 is
   the canonical owner.)
2. **Six generators → SEVEN, with the scene-writer split out and re-homed.** The old doc folded scene-writing
   into the Element generator inside `Dsl`. Split it into a dedicated `SceneWriterGenerator` **homed in the
   Reconciler/leaf, not `Dsl`** (D4a), to honor `foundations.md` §7 invariant #3 (Dsl knows nothing of Scene).
   Counted the COM-binding generator as the seventh and moved it to leaf-only `FluentGpu.Interop.SourceGen`.
3. **COM ruling rewritten to the corrected verdict.** Old D5 said "reject ComWrappers, copy ComputeSharp's
   struct-over-vtbl, author with `[VtblIndex]` stubs." New D6: `[LibraryImport]` (flat C) + hand-vtable
   `calli` (generated hot-path consume + in-loop CCWs) + `[GeneratedComInterface]`/`[GeneratedComClass]`
   (ALL cold/warm), bindings **generated from a harvested, runtime-self-checked `*.comabi.json`** (no
   human-typed slot), ComPtr render-thread-confined + Move-only. Deep detail deferred to `com-interop.md`.
4. **Single-buffer arena → `ChunkedArena`.** Modifier accumulation now uses the native-backed,
   reserve-then-commit `ChunkedArena` (O(1) add-chunk, no LOH/Gen2 copy cliff), gated by a native high-water
   counter — superseding the old `ArenaAllocator` per the `foundations.md` amendment.
5. **`[FastPath]` builder explicitly re-homed on retain** and tied to the `IBufferWriter<byte>` *contract* +
   C# 14 extension blocks (was a looser "opt-in toggle").
6. **Threading/thread-confinement made explicit per the hardened model.** Added the UI-thread-only
   placement of the DSL (phases 4–5), the `[ThreadStatic]` arena, the PUBLISH(13a) seam, and the fact that
   the render thread never sees `Element`/DSL/arena — none of which the original (pre-render-thread-seam)
   draft addressed.
7. **Memo-skip + effect-timing contracts folded.** 3-signal skip (`SelfTriggered || propsChanged ||
   HasConsumedContextChanged`), `SubtreeDirty` = traversal scope only, `UseLayoutEffect` phase 6.5 RATIFIED,
   `UseEffect` phase 12, setState-in-effect ⇒ N+1 (no synchronous re-loop).
8. **C# 14/.NET 10 features and GC config made first-class** (D7): `field`, extension members,
   user-defined compound assignment, `allows ref struct`, `InvariantGlobalization`, Workstation+Concurrent GC
   + DATAS + `SustainedLowLatency` — the old draft mentioned only AOT props and `SkipLocalsInit`.
9. **WaveeMusic hooks acknowledged as authoring clients** of the HookDeps generator (`UseImage`, `UseVirtual`,
   `UseDerivedBrush`, `UseSyncedLyrics`, `UseVideoSurface`, etc., in `FluentGpu.Media`/`FluentGpu.Theme`), and
   the `ArrayPool<Element>` (not cap-32 `ObjectPool`) window-buffer rule folded into §1.3.
10. **Footprint ratchet promoted to a non-suppressible CI gate** with an explicit ≤220 KB DSL+Hooks+generated
    sub-budget and the per-element ≤1.5 KB marginal target (gate implementation deferred to `validation.md`).
11. **`WGPU####` vs `FGCOM####` scoping clarified** — this doc owns the DSL `WGPU` analyzer set; the COM
    `FGCOM` rules are owned by `com-interop.md`. Added WGPU0011 (UseMemo<LargeStruct> footprint).
12. **Concurrent-era lowering generators folded into core (P1/P2a/P2b/P6/P7).** Seven generators → **eight
    portable** (+ the leaf COM generator): added `LaneCaptureGenerator` (#8, lane/transition/`UseDeferredValue`),
    `BoundaryGenerator` (#9, `Suspense`/`Boundary` element), `DerivedCaptureGenerator` (#10,
    `UseDerived`/`UseContextSelector`/`UseOptimistic`). All reuse #4's interceptor + `stackalloc DepKey[]`
    machinery — no reflection, no new generic matrix. Added WGPU0012–WGPU0015. The lane semantics, update queue,
    boundary swap, derived node, and optimistic rollback are **referenced** to `reconciler-hooks.md`; this doc owns
    only the AOT-clean codegen. New scene columns `LaneMask`/`BoundaryState` referenced to `scene-memory.md`.
13. **L9 localization designed into core (was a silent `InvariantGlobalization=true`).** Replaced the unconditional
    `InvariantGlobalization=true` with the `<FluentGpuLocalization>` three-way knob (default `Embedded` = a minimal
    AOT-static CLDR formatter; `Icu` opt-in with footprint noted; `Invariant` for the rare no-format app). Added the
    `FluentGpu.Localization` edge formatter (`Loc.*` → interned `StringId`, **no-string-on-paint-path** upheld),
    the `LocalizationGenerator` (bakes declared cultures like the theme blobs), the `[Message]` plural attribute,
    the `IPlatformLocale` PAL-seam consumption (modeled on `ISystemColors`, owned by `pal-rhi.md`), and the
    `AutoMirror` RTL icon `[Prop]` (resolution owned by `layout.md`). Added WGPU0016 + the per-mode/per-culture
    footprint tier (so `Icu` does not silently bust the 5.5 MB line).
14. **Assembly-graph placement of the new modules.** `FluentGpu.Controls` (pay-per-reference, sequenced after the
    L1/L2/L4/L6 seams) and `FluentGpu.Devtools` (`EnableDevtools`-gated, 0 bytes release) are placed in the build/
    trim/footprint story here; their *content* is owned by `controls.md`/`devtools.md`.
