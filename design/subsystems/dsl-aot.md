# FluentGpu — Subsystem Design: Fluent C# DSL + NativeAOT Toolchain + Source Generators

*Authoritative. Supersedes `dsl-aot-toolchain.md` (which now carries a one-line pointer to this file).*
*Owns the authoring surface, the `[module]` AOT/GC/trim baseline, and the SEVEN source generators.
Cross-cutting contracts (threading, COM ruling, memory/handles, scene/drawlist, RHI/PAL, hooks/reconcile)
are owned by the docs named inline — this doc references, never redefines, them.*

**Assemblies owned here:** `FluentGpu.Dsl` (Element records, factories, modifier arena, `[FastPath]` builder),
`FluentGpu.Hooks` (authoring surface of the hook deps — the cells/scheduler are reconciler-owned),
`FluentGpu.SourceGen` (the six portable codegen + analyzer generators), and `FluentGpu.Interop.SourceGen`
(the COM-binding generator overview; deep detail deferred to `com-interop.md`). Also owns the root
`Directory.Build.props` baseline and the footprint ratchet.

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
| D7 | **net10.0 / LangVersion 14 / PublishAot / TrimMode full / InvariantGlobalization; Workstation+Concurrent GC, DATAS on; `GCSettings.SustainedLowLatency` once at startup; module-wide `[SkipLocalsInit]`.** | `dotnet10-csharp14-zero-alloc.md` §1/§G. |
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

## 2. THE SEVEN SOURCE GENERATORS

Six portable generators + an analyzer ship in ONE `netstandard2.0` analyzer assembly `FluentGpu.SourceGen.dll`
(build-time only, **0 runtime footprint**); the seventh, the COM-binding generator, ships in the leaf-only
`FluentGpu.Interop.SourceGen.dll`. All are incremental (`IIncrementalGenerator`) with cacheable *equatable*
models (vendored `EquatableArray<T>`/`HierarchyInfo`/`DiagnosticInfo` from ComputeSharp's self-contained
helpers; no `ISymbol` leaks into the pipeline).

```
FluentGpu.SourceGen.dll  (netstandard2.0, analyzer — build-time only)
├── 1. ElementTypeIdGenerator   [Element]/[Prop]/[Factory] → ElementTypeId const, typed setters, UI factories
├── 2. ModifierGenerator        [Modifier] manifest        → fluent ext methods over ModifierArena + Pack/Unpack
├── 3. DiffPropsGenerator       (from [Element])           → bitmask DiffProps + Equals/GetHashCode (no reflection)
├── 4. HookDepsGenerator        Use* call sites            → ReadOnlySpan<DepKey> capture (≤4-arity unmanaged)
├── 5. ThemeBlobGenerator       [ThemeTokens]              → baked Light/Dark/HC BrushSpec blobs + resolver switch
└── 7. WgpuAnalyzer + CodeFixer WGPU#### diagnostics       → compile-time DSL validation (§2.7)

(in Reconciler/leaf, NOT Dsl — D4a)
└── 6. SceneWriterGenerator     (from [Element]/[Modifier]) → ApplyToScene(ref SceneWriter): writes SceneStore columns

FluentGpu.Interop.SourceGen.dll  (netstandard2.0, leaf-only)
└── COM-binding generator       harvested *.comabi.json    → hand-vtable IComObject structs + cold [GeneratedComInterface]
                                                              (OVERVIEW here; deep detail → com-interop.md)
```

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

`FluentGpu.SourceGen.CodeFixers` auto-converts `params object[]` deps → interceptable form and adds
`sealed partial record` for WGPU0001.

### 2.8 COM-binding generator (overview — deep detail in `com-interop.md`)

The seventh generator (in leaf-only `FluentGpu.Interop.SourceGen`) implements the **corrected COM ruling**
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
  <InvariantGlobalization>true</InvariantGlobalization>  <!-- ICU dropped; DWrite does locale shaping -->
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

### 3.4 Trimming / feature switches
- No `rd.xml` for DSL/Hooks/Scene (no reflection → trimmer resolves the full call graph).
- ONE small `ILLink.Descriptors.xml` in the COM-CCW leaf roots the source-gen'd ComWrappers vtable methods
  (reached only from native — trimmer can't see the edge).
- Feature switch `FluentGpu.EnableDevtools` (default false, trimmed) gates the a11y/inspector overlay → 0
  bytes in release.

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
- `InvariantGlobalization` — paint path is culture-free; DWrite does the locale shaping.

### 3.6 Footprint ratchet + measurement (D8)

| Component | IL/binary target |
|-----------|------------------|
| `FluentGpu.Dsl` (records, factories, arena, modifier resolver) | ≤ 90 KB |
| `FluentGpu.Hooks` (authoring surface + DepKey capture) | ≤ 45 KB |
| Generated per-app code (setters/diff/factories for ~60 element types) | ≤ 85 KB |
| **DSL + Hooks + generated** | **≤ ~220 KB** |
| `FluentGpu.Win32.D3D12` (extracted, graphics-trimmed) | ≤ 250 KB |
| `FluentGpu.Win32.DWrite` + `DComp` (authored) | ≤ 120 KB |
| **Whole "rounded-rect + text" self-contained AOT exe** | **≤ 5.5 MB** |

Measurement pipeline (the CI gate lives in `FluentGpu.Validation`/`validation.md`; this doc sets the
budgets): `dotnet publish -r win-x64 -c Release` → `IlcGenerateMstatFile=true` + `IlcGenerateMapFile=true` →
`sizoscope`/a small `.mstat` `size-report` tool attributes bytes per assembly/type/method; **non-suppressible
ratchet fails CI if any row regresses > 5%**; per-element marginal cost target ≤ 1.5 KB generated IL
(regression test keeps the "60 element types" line honest).

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
- **Phase 4 RENDER (UI thread):** `Component.Render` runs the DSL — factories, modifier chain, child-list
  copy. Edge Gen0 alloc only (Element records, user closures, virtualization `Element[]` from `ArrayPool`).
  Memo-skip (the 3-signal gate `SelfTriggered || propsChanged || HasConsumedContextChanged`) skips most
  subtrees — `SubtreeDirty` is traversal scope only, never a skip-decision input.
- **Phase 5 RECONCILE (UI thread):** generated `DiffProps` + `SceneWriterGenerator.ApplyToScene` patch the
  SoA store; keyed-LIS re-authored on arena scratch.
- **Effect timing the deps generator feeds:** `UseLayoutEffect` flush at **phase 6.5** (RATIFIED), `UseEffect`
  at **phase 12**; `setState` in an effect ⇒ mark dirty + frame N+1 (NO synchronous bounded re-loop in v1).
- **PUBLISH (13a) and phases 8–11 (RENDER thread):** the DSL/arena are out of scope; the render thread reads
  the immutable POD `SceneFrame` snapshot. The modifier arena is `Reset()` on the UI thread at frame end and
  is NEVER shared with the render thread (it is fully consumed during phase 5).

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
  pooled overflow for the rare >4 case.
- **Generated code is `[module: SkipLocalsInit]`-friendly** (`ModifierOp`/`DepKey` scratch fully overwritten
  before read; the WGPU-internal analyzer verifies).

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

---

## 7. CROSS-PLATFORM (macOS) BOUNDARY

```
                       PORTABLE (no OS/GPU types)                       │  WINDOWS-SPECIFIC (leaf only)
 ───────────────────────────────────────────────────────────────────── │ ──────────────────────────────
 FluentGpu.Dsl        Element records, ModifierArena, ModifierOp,        │
 FluentGpu.Hooks      DepKey capture, factories, BrushSpec, StringId,    │
                      ChildList, ThemeBlob tables                        │
 FluentGpu.SourceGen  Element/Modifier/DiffProps/HookDeps/Theme + WGPU   │  COM-binding generator emits
                      analyzer (pure Roslyn, runs on any OS)             │   Windows COM structs (data only)
 ───────────────────────────────────────────────────────────────────── │ ──────────────────────────────
 SceneWriter gen      portable (writes portable SoA POD)                 │
 COM lifetime         —                                                  │  ComPtr<T>, vtbl structs, IIDs,
 graphics interop     —                                                  │  D3D12/DXGI/DWrite/DComp bindings
```

- The **entire authoring DSL + hooks + the six portable generators are OS- and GPU-agnostic** — they emit
  `Element` records and (via the reconciler-homed scene-writer) write portable POD into `SceneStore`. A
  Metal/CoreText backend needs **zero DSL changes**.
- The ONLY Windows-specific generator output is the COM-binding generator's vtbl structs; on macOS the
  analogous (smaller — Objective-C `msgSend`, not COM) interop is a sibling generator. Authoring code never
  sees either.
- `BrushSpec`/`ImageSource`/`StringId`/`FontWeight`/`Palette` are portable value structs; DWrite-specific
  font realization lives behind `IFontSystem`, not the DSL.

---

## 8. WHAT THIS SUBSYSTEM OWNS (authority surface)

- **Types:** `Element` (abstract record), `ModifierRef`, `ExtrasRef`, `ModifierOp`, `ModifierKind`,
  `ModifierFlags`, `ModifierArena`, `ChildList`, `UI` (factory class), `TokenId`/`ThemeTables` shape,
  the `[FastPath]` builder pattern, `BrushSpec` (DSL-side value struct shape — size confirmed by Render).
- **Attributes:** `[Element]`, `[Prop]`, `[Factory]`, `[Modifier]`, `[ThemeTokens]`, `[FastPath]`.
- **Generators:** `ElementTypeIdGenerator`, `ModifierGenerator`, `DiffPropsGenerator`, `HookDepsGenerator`,
  `ThemeBlobGenerator`, `WgpuAnalyzer`+CodeFixer (all in `FluentGpu.SourceGen`); the **overview** of the
  COM-binding generator (`FluentGpu.Interop.SourceGen`). `SceneWriterGenerator` is authored here but
  **homed in the Reconciler/leaf** (D4a).
- **Hooks authoring surface:** the `DepKey`-span capture form of `UseEffect`/`UseMemo`/`UseCallback`/
  `UseReducer`/`UseState`/`UseRef`/`UseContext` (cells/scheduler are reconciler-owned).
- **Baseline:** the root `Directory.Build.props` (net10/LangVersion14/AOT/trim/GC), `[module:
  SkipLocalsInit]`, `GCSettings.SustainedLowLatency`, `DisableRuntimeMarshalling` placement, the
  `<InterceptorsNamespaces>` switch, the `WGPU####` analyzer ID range, and the footprint ratchet budgets.

---

## Changed vs the original synthesis (`dsl-aot-toolchain.md`)

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
