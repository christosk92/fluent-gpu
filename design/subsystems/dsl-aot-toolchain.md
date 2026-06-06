> **SUPERSEDED — see [`dsl-aot.md`](./dsl-aot.md), the current authoritative doc.** This file is retained
> for history only; it predates the render-thread-seam threading model, the corrected COM ruling (harvested
> `*.comabi.json` + hand-vtable hot path + `[GeneratedComInterface]` cold), the pure-scalar `DepKey` +
> `GcDepTable` fix, the `ChunkedArena`, and the seventh (scene-writer, reconciler-homed) generator.

# fluent-gpu — Subsystem Design: Fluent C# DSL + NativeAOT Toolchain + Source Generators

Authoritative against FOUNDATIONS (Foundation/Scene/Render/Rhi/Pal/Text/Reconciler/Hosting contracts).
Assemblies owned here: **`FluentGpu.Dsl`**, **`FluentGpu.Hooks`** (authoring surface, GPU-agnostic),
**`FluentGpu.SourceGen`** (build-time analyzer/generator), and the COM-interop generator that seeds
`FluentGpu.Rhi.D3D12` / `Text.DirectWrite` / `Pal.Windows` leaf impls.

Grounded in verified source:
- Reactor `Modify<T>` (ElementExtensions.cs:2459) = `el with { Modifiers = el.Modifiers.Merge(mods) }`
  → **two record allocations per `.Margin()` call** (Element copy + bucket `with`). This is the central
  zero-alloc problem we fix.
- Reactor hook deps = `UseEffect(Action, params object[] dependencies)` + `dependencies.ToArray()` +
  `DepsEqual` boxing `Equals` (RenderContext.cs:306, Component.cs:306) → **AOT-hostile, allocates**. Replaced.
- `TextBlockElement.DiffProps` (Element.cs) is a **hand-written bitmask diff** — exactly what our generator emits.
- ComputeSharp COM interop = **struct-over-vtbl + `delegate* unmanaged[MemberFunction]` + `static abstract
  Guid* IComObject.IID`** (IDXGISwapChain.cs, IComObject.cs), NOT ComWrappers. ComWrappers used in only 2
  RCW/CCW callback spots (ICanvasImageInterop). This is our template.
- ComputeSharp incremental-gen plumbing = `EquatableArray<T>`, `HierarchyInfo`, `DiagnosticInfo`,
  netstandard2.0 generators, 70+ `CMPS####` diagnostics. We mirror this packaging.

---

## 0. CONTRACT SUMMARY (load-bearing decisions for this subsystem)

| # | Decision | Justification |
|---|----------|---------------|
| D1 | **Element stays an immutable `record` (GC ref, at the edge).** It is NOT moved to a struct. | FOUNDATION §1 RULE: GC refs live only at edges (Element records, Component, delegates). Records give value-equality (free diff), `with` ergonomics, nullable-init props. Edge cost is paid once per render, not per frame. |
| D2 | **Modifiers are accumulated in a per-render `ArenaAllocator`-backed `ModifierAccumulator` (struct cursor into an arena), NOT by `with`-copying the record.** The Element holds an 8-byte `ModifierRef {start:u32,count:u16,flags:u16}` into the arena, set ONCE when the chain terminates. | Kills the O(N) record-copy storm in Reactor's `Modify`. One Element alloc per node, zero per modifier. Arena resets each frame (O(1)). |
| D3 | **Concrete element type preserved through chaining via generic `where T : IElement` extension methods that return `T` — but the chain mutates an arena cursor, not the record.** A `readonly record struct`-free fluent: `T` flows through, `T` is unchanged identity until `.Build()`/implicit-conversion seals it. | FOUNDATION: keeps `.Set()` strongly typed after any modifier (Reactor's headline ergonomic). |
| D4 | **All per-element setters, the modifier methods, the no-reflection prop-diff, the theme-token table, and the hook-dep capture are SOURCE-GENERATED** from `[Element]`/`[Modifier]`/`[ThemeTokens]` attributes. | FOUNDATION §4: no reflection/dynamic codegen; trimmable; small IL. |
| D5 | **COM interop = our own generator in ComputeSharp's struct-over-vtbl style, seeded by extracting `ComputeSharp.Win32.D3D12` / `.D2D1` verbatim; we author DWrite + DComp bindings with the same generator.** NOT ComWrappers except the ≤3 CCW callback seams (DComp/DWrite render callbacks). | FOUNDATION §4 + REUSE brief. ComputeSharp is DX12-only and graphics-pipeline-absent; we take its COM-binding *style* and the D3D12/DXGI structs, add graphics PSO/swapchain/DComp/DWrite ourselves. |
| D6 | **Hook deps = `ReadOnlySpan<DepKey>` where `DepKey` is a 16-byte tagged-union struct; source-gen emits the capture from the call site.** No `params object[]`, no boxing. | FOUNDATION §6. |
| D7 | **Footprint budget: DSL+Hooks+generated code ≤ ~220 KB IL contribution to a self-contained NativeAOT exe; whole-engine "hello rounded-rect + text" target ≤ 5.5 MB on-disk.** Measured via `sizoscope`/`ILSize` + map files. | FOUNDATION §4 "small IL/binary footprint is a primary goal". |

---

## 1. AUTHORING SURFACE — the Fluent C# DSL

### 1.1 Public shape (unchanged from Reactor at the call site)

```csharp
using static FluentGpu.Dsl.UI;   // factories
// (extension methods auto-visible via [assembly: ...] using-static-free design — see §1.6)

Element View(int count) =>
    VStack(
        Text("Hello").Bold().FontSize(28),
        Button("Click me", () => setCount(count + 1)).Padding(12).Corner(8),
        count > 5 ? Text("Wow!").Foreground(Tok.AccentText) : null,
        Image(src).Width(64).Height(64)
    ).Spacing(8).Padding(16);
```

Identical to Reactor ergonomically. The difference is entirely under the hood (D2/D3) and that the
terminal `Element` patches `SceneStore` via `ISceneBackend`, not WinUI.

### 1.2 Element model — record at the edge, handle-light payload

`Element` is the abstract base record (kept from Reactor). Concrete elements are `sealed record`s with
**only value-typed / `StringId` / handle / small-record props** — NO heavy WinUI types (the Reactor
`Brush?`, `FontFamily?` become `BrushSpec` value structs + `StringId`).

```csharp
namespace FluentGpu.Dsl;

public abstract record Element
{
    public StringId Key { get; init; }            // interned; 0 == none (was string? Key)
    public ModifierRef Modifiers { get; init; }   // 8 bytes into per-render arena (NOT a record!)
    public ExtrasRef Extensions { get; init; }    // 8 bytes; lazy bucket for rare cross-cutting data
    public abstract ushort ElementTypeId { get; } // source-gen'd stable id → SceneStore.ElementTypeId
}

// 8-byte handle into the per-render modifier arena (FOUNDATION §1 generational-handle rule).
public readonly record struct ModifierRef(uint Start, ushort Count, ModifierFlags Flags)
{
    public static readonly ModifierRef None = default;     // Start==0 && Count==0
    public bool IsEmpty => Count == 0;
}
public readonly record struct ExtrasRef(uint Index);       // 0 == none; indexes a SlabAllocator<ElementExtras>
```

`ElementTypeId` is a `ushort` (FOUNDATION §2 `ElementTypeId:ushort`) emitted by the generator as a
`const` per element type, so the reconciler's type check is an int compare, never `GetType()`.

A concrete element (the generator owns the boilerplate; user writes the `partial`):

```csharp
[Element(TypeId = ElementIds.Text, VisualKind = VisualKind.GlyphRun)]
public sealed partial record TextElement(StringId Content) : Element
{
    [Prop] public Half FontSize { get; init; }            // 16-bit; ramp tokens cover most sizes
    [Prop] public FontWeight Weight { get; init; }
    [Prop] public BrushSpec Foreground { get; init; }     // value struct, not a Brush ref
    [Prop] public TextWrap Wrap { get; init; }
    [Prop] public StringId FontFamily { get; init; }
    // generator emits: ElementTypeId override, DiffProps bitmask, Equals/GetHashCode override
    //                  using ONLY [Prop]+Key+Modifiers+Extensions, ApplyToScene(ref SceneWriter)
}
```

### 1.3 Factory methods (`FluentGpu.Dsl.UI`)

A hand-written `static partial class UI` with thin factories; the **bodies are generated** from
`[Element]` (one `Create*` per element + ergonomic overloads marked `[Factory]`).

```csharp
public static partial class UI
{
    // generated:
    public static TextElement Text(string content)      => new(StringTable.Intern(content));
    public static TextElement Text(StringId content)    => new(content);
    // hand-written ergonomic shims (not generatable) — heading/caption presets:
    public static TextElement Heading(string c)  => Text(c).FontSize(28).Bold().Heading(1);
    public static TextElement Caption(string c)  => Text(c).FontSize(12);

    // layout containers take ReadOnlySpan<Element> (NO params object[]; FOUNDATION §1) :
    public static VStackElement VStack(params ReadOnlySpan<Element> children) => new(ChildList.From(children));
    public static HStackElement HStack(params ReadOnlySpan<Element> children) => new(ChildList.From(children));
    public static GridElement   Grid (params ReadOnlySpan<Element> children) => new(ChildList.From(children));
    public static ButtonElement Button(string label, Action onClick)         => new(StringTable.Intern(label), onClick);
    public static ImageElement  Image(ImageSource src)                       => new(src);
}
```

C# 13 `params ReadOnlySpan<T>` (collection-expression params) means `VStack(a, b, c)` lowers to a
**stack-allocated span** — no `object[]` boxing, no array on the heap for the common small-arity case.
`ChildList.From(span)` copies into a per-render arena (`ArenaAllocator`) returning a `ChildList`
(`{start:u32,count:u32}` handle), so the Element holds 8 bytes, not an array ref.

`null` children (the `count > 5 ? … : null` idiom) are stripped by `ChildList.From` during the copy —
preserves Reactor's conditional-render idiom with zero downstream branching.

### 1.4 THE CORE PROBLEM: modifier accumulation without per-call re-allocation

**Reactor today (verified, ElementExtensions.cs:2459-2460):**
```csharp
private static T Modify<T>(T el, ElementModifiers mods) =>
    el with { Modifiers = el.Modifiers is not null ? el.Modifiers.Merge(mods) : mods };
```
`Text("x").Bold().Margin(8).FontSize(28)` = 3 × (`Element` record copy + `ElementModifiers` `with` +
bucket `with`). For a 1000-node tree with avg 3 modifiers ≈ **6000 record allocations per render**. Even
gen0, that violates the "near-zero per-frame allocation" constraint and tanks NativeAOT throughput.

**Our design — arena-backed `ModifierAccumulator`:**

Each modifier is a fixed 16-byte POD `ModifierOp {Kind:u16, _pad:u16, ulong Value}` (value is a union:
double-as-bits, packed Thickness, BrushSpec id, etc.). Modifiers are appended to a **per-render
`ArenaAllocator` segment**; the Element carries a `ModifierRef` (start+count). The chain does NOT copy
the record per call — it copies the record at most ONCE (the first modifier that needs to seal the ref),
then mutates the arena in place.

```csharp
public enum ModifierKind : ushort
{ Margin, Padding, Width, Height, MinW, MinH, MaxW, MaxH, HAlign, VAlign,
  Opacity, Corner, Foreground, Background, BorderBrush, BorderThickness, Bold, FontSize, /*…*/ }

[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct ModifierOp
{
    public readonly ModifierKind Kind;     // 2
    private  readonly ushort _flags;       // 2 (e.g. "is-token-ref" vs literal)
    public readonly ulong Value;           // 8  (bit-union; Thickness packs 4×Half; double via BitConverter)
}                                          // 4 pad → 16 bytes, blittable, [SkipLocalsInit]-friendly
```

Append path (the generated extension methods call this — single shared non-generic core, no generic
code bloat per `T`):

```csharp
public static class ModifierArena   // per-render, in FluentGpu.Dsl, fed by Foundation.ArenaAllocator
{
    [ThreadStatic] static ArenaAllocator _arena;   // single UI thread (FOUNDATION §6); TS for safety

    // Returns updated ModifierRef. If el already has a sealed ref that is the *tail* of the arena,
    // we extend in place (count++). Otherwise we copy the existing ops then append (rare: branch reuse).
    public static ModifierRef Append(ModifierRef cur, in ModifierOp op)
    {
        ref var a = ref _arena;
        if (cur.IsEmpty)
        {
            uint start = a.Position;
            a.Write(op);                          // bump pointer
            return new ModifierRef(start, 1, FlagsFor(op));
        }
        // Fast path: this ref is the current arena tail → contiguous extend, O(1), no copy.
        if (cur.Start + cur.Count == /*last write index*/ a.LastIndex && a.IsTail(cur))
        {
            a.Write(op);
            return cur with { Count = (ushort)(cur.Count + 1), Flags = cur.Flags | FlagsFor(op) };
        }
        // Slow path (the element was branched/aliased): copy then append.
        uint ns = a.Position;
        a.WriteCopy(cur.Start, cur.Count);
        a.Write(op);
        return new ModifierRef(ns, (ushort)(cur.Count + 1), cur.Flags | FlagsFor(op));
    }
}
```

Generated extension method (one per modifier; all delegate to the single non-generic `Append`):

```csharp
// GENERATED in FluentGpu.Dsl.Generated.ModifierExtensions
public static T Margin<T>(this T el, double uniform) where T : Element =>
    (T)el.WithModifiers(ModifierArena.Append(el.Modifiers,
        new ModifierOp(ModifierKind.Margin, PackUniform(uniform))));

public static T FontSize<T>(this T el, double size) where T : Element =>
    (T)el.WithModifiers(ModifierArena.Append(el.Modifiers,
        new ModifierOp(ModifierKind.FontSize, BitConverter.DoubleToUInt64Bits(size))));
```

`WithModifiers` is a single generated non-generic virtual on `Element` returning `Element` (the `(T)`
cast is a no-op reference cast — verified safe because identity is preserved). Critically, **`el with {
Modifiers = ref }` is still ONE record copy** — but ONLY because record `with` is unavoidable for
immutability of the returned reference. To eliminate even that, see §1.5 (the builder-hybrid escape
hatch). For the default path we accept **1 record copy per element-that-is-modified** (not per
modifier), down from Reactor's N. That is the decisive win: O(nodes) not O(nodes × modifiers).

**Allocation accounting (1000 nodes, avg 3 mods):**
| | Reactor | fluent-gpu |
|---|---|---|
| Element record copies | 3000 (one per `.Modify`) | 0–1000 (one per modified element, via `with`) |
| Modifier bucket records | 3000+ | 0 (arena bump) |
| Heap bytes (gen0) | ~hundreds of KB | ~16 B × 3000 in arena = 48 KB **reused every frame** |

The arena is double-buffered with the DrawList arenas (FOUNDATION §3) and `Reset()` at frame end.

### 1.5 Eliminating the last record copy — the struct/builder hybrid (opt-in, `[FastPath]`)

For the hottest authoring (list item templates rendered per-frame), the generator can emit a
**builder struct** path so even the single `with` copy disappears. The factory returns a
`readonly ref struct` builder that defers record materialization until the reconciler consumes it:

```csharp
public readonly ref struct TextBuilder
{
    private readonly StringId _content;
    private readonly ModifierRef _mods;     // mutated by arena, struct stays on stack
    public TextBuilder FontSize(double s) => new(_content, ModifierArena.Append(_mods, …));
    public TextBuilder Bold()             => new(_content, ModifierArena.Append(_mods, …));
    // implicit materialization only when assigned to Element / passed to a container:
    public static implicit operator TextElement(in TextBuilder b) => new(b._content){ Modifiers = b._mods };
}
```

This is **decisively a hybrid**: `record` is the default (D1) for ergonomics, equality, and the edge;
the `ref struct` builder is an opt-in `[Element(FastPath=true)]` toggle for proven hot templates. The
`ref struct` cannot escape to the heap (compiler-enforced), guaranteeing zero alloc until the implicit
conversion seals exactly one record at the container boundary. We DO NOT make this the default because
(a) `ref struct` can't be stored in `?:` ternaries returning `Element`, breaking the conditional-render
idiom, and (b) it can't be `null` — both are core Reactor ergonomics. The default record path with the
arena (§1.4) already meets the per-frame budget; `[FastPath]` is the escape valve.

> **Foundation amendment request (minor):** FOUNDATION §2 lists `Element` properties as the reconciler
> input but doesn't pin Element to record-vs-struct. We pin it to **record (edge) + arena modifiers +
> opt-in ref-struct builder**. If the architect intended Element itself to be a struct, that conflicts
> with `null` children and ternaries; we recommend keeping record-at-edge as specified here.

### 1.6 Concrete-type preservation & `.Set()` escape hatch

`where T : Element` generic extensions return `T`, so the chain stays `TextElement` end-to-end and
`.Set(t => …)` (Reactor's typed native-prop hatch) still binds. But `.Set(Action<TWinUI>)` is gone
(no WinUI). Its replacement is `.Set<T>(this T el, Action<SceneWriter> apply)` — a deferred,
arena-recorded mutation applied during `ApplyToScene` (§3 generated). For typed element-prop tweaks the
idiomatic path is `with`: `Text("x") with { Wrap = TextWrap.Wrap }` (record init), or generated
strongly-typed setters `.Wrap(TextWrap.Wrap)`.

Extension visibility: extensions live in `FluentGpu.Dsl` namespace and are auto-imported by a single
`global using FluentGpu.Dsl;` we ship in a `.props`/template, so users get IntelliSense without
`using static`.

---

## 2. SOURCE GENERATORS — the toolchain

Five generators + one analyzer, all `netstandard2.0` (Roslyn host requirement, mirrors ComputeSharp),
all **incremental** (`IIncrementalGenerator`) with cacheable equatable models (we vendor ComputeSharp's
`EquatableArray<T>`, `HierarchyInfo`, `DiagnosticInfo` from `ComputeSharp.SourceGeneration/Helpers` —
they are self-contained and MIT-licensed). Packaged as ONE analyzer assembly `FluentGpu.SourceGen.dll`
(reduces NuGet/SDK surface; multiple `IIncrementalGenerator`s in one DLL).

```
FluentGpu.SourceGen.dll  (netstandard2.0, analyzer, ships build-time only — 0 runtime footprint)
├── ElementGenerator           [Element]/[Prop]/[Factory]  → setters, factories, ElementTypeId
├── ModifierGenerator          [Modifier]                  → fluent ext methods over ModifierArena
├── DiffEqualityGenerator      (from [Element])            → DiffProps bitmask + Equals/GetHashCode
├── ThemeTokenGenerator        [ThemeTokens] enum/partial  → flat token tables + resolver switch
├── HookDepsGenerator          interceptors on Use* calls  → ReadOnlySpan<DepKey> capture
├── ComInteropGenerator        [GenerateComInterface]      → struct-over-vtbl bindings (ComputeSharp style)
└── DslAnalyzer                 WGPU#### diagnostics        → compile-time validation (§2.7)
```

### 2.1 `ElementGenerator` — per-element setters / factories

Input model (cacheable; no `ISymbol` leaks into the pipeline — only equatable POCOs, per ComputeSharp's
incremental discipline to avoid re-running on every keystroke):

```csharp
sealed record ElementModel(
    HierarchyInfo Hierarchy,          // namespace + nesting (ComputeSharp type)
    string TypeName, ushort TypeId, VisualKind VisualKind,
    EquatableArray<PropModel> Props,  // {Name, FullTypeName, IsNullable, DefaultExpr, DiffStrategy}
    EquatableArray<FactoryModel> Factories);
```

Emits, per element:
1. `public override ushort ElementTypeId => <const>;`
2. Strongly-typed fluent setters for each `[Prop]` that DON'T go through the modifier arena (element-
   intrinsic props like `TextElement.Wrap`): `public TextElement Wrap(TextWrap v) => this with { Wrap = v };`
   (these are element-specific so the single `with` is correct and rare on hot paths).
3. Factory bodies in `UI` partial.
4. `internal void ApplyToScene(ref SceneWriter w)` — writes the element's intrinsic props +
   resolved modifiers into `SceneStore` columns via `ISceneBackend` (the reconciler bridge). This is
   where `VisualKind`, `Fill/Stroke:BrushHandle`, `CornerRadius4` (FOUNDATION §2) get set, no reflection.

### 2.2 `DiffEqualityGenerator` — no-reflection equality / diff

Records auto-synthesize `Equals`, but the synthesized version (a) walks every field incl. the rarely-set
ones, and (b) we WANT a **bitmask diff** (`TextPropChanged`) for the reconciler to patch only changed
SceneStore columns — exactly the hand-written `TextElement.DiffProps` we found. The generator emits it:

```csharp
[Flags] internal enum TextPropChanged : uint
{ None=0, Content=1, FontSize=2, Weight=4, Foreground=8, Wrap=16, Modifiers=1u<<30, Extensions=1u<<31 }

internal static TextPropChanged DiffProps(TextElement a, TextElement b)
{
    TextPropChanged d = 0;
    if (a.Content != b.Content)       d |= TextPropChanged.Content;
    if (a.FontSize != b.FontSize)     d |= TextPropChanged.FontSize;
    // … per [Prop] …
    if (!a.Modifiers.Equals(b.Modifiers)) d |= TextPropChanged.Modifiers;   // 8-byte struct compare
    if (a.Extensions.Index != b.Extensions.Index) d |= TextPropChanged.Extensions;
    return d;
}
```

`Modifiers.Equals` is a `ModifierRef` (8 bytes) compare → BUT two refs can point at *equal* op
sequences in different arena slots. The reconciler compares ops via `ModifierArena.SequenceEqual(a,b)`
(a `ReadOnlySpan<ModifierOp>.SequenceEqual`, SIMD-friendly, zero-alloc) only when the cheap ref compare
differs. We also override `GetHashCode`/`Equals` on the record to use this same field set (excluding
delegate props which are reference-compared, matching Reactor's "can't compare delegates" note).

For each element type the generator also emits a **`DepKey ToDepKey()`** if the element is used as a
hook dependency (rare) — usually deps are scalars (§2.5).

Diff strategy per prop (chosen from the symbol type, validated by analyzer):
- value type / enum / `StringId` / handle → `!=`
- `BrushSpec`, `Thickness`-likes (value structs) → `!=` (they're `record struct` or `IEquatable`)
- delegate (`Action`, `Action<T>`) → reference compare + a "delegate present" flag bit (can't deep-compare)
- `Element` child / `ChildList` → handled by reconciler subtree diff, excluded from prop diff

### 2.3 `ModifierGenerator` — fluent modifier methods

From `[Modifier(Kind = ModifierKind.Margin, Packing = Packing.Thickness)]` on a declarative manifest
(`partial class ModifierManifest`), emits the generic extension methods (§1.4) + the `Pack*`/`Unpack*`
helpers + the `ApplyModifier(ref SceneWriter, in ModifierOp)` resolver `switch` (a single dense
jump-table `switch` on `ModifierKind` — no virtual dispatch, no dictionary). Keeping the resolver in ONE
generated switch (not per-element) is the key anti-generic-explosion move (§4.3).

### 2.4 `ThemeTokenGenerator` — theme-token tables

Replaces Reactor's `ThemeRef`/`Theme.cs` WinUI-resource lookup with a **flat, AOT-static token table**.
Input: `[ThemeTokens] partial class Tok` listing semantic tokens; output: a `const`-indexed enum +
per-theme `ReadOnlySpan<BrushSpec>` backing arrays (data baked into the DLL as `static readonly` blobs
via `RuntimeHelpers.CreateSpan` / `ReadOnlySpan<byte>` literal — zero static-ctor cost, no reflection).

```csharp
// user:
[ThemeTokens]
public static partial class Tok
{ public static partial BrushSpec AccentText { get; } public static partial BrushSpec CardBackground { get; } }

// generated:
public enum TokenId : ushort { AccentText, CardBackground, /*…*/ Count }
internal static class ThemeTables
{
    // baked blobs: indexed [theme][TokenId] ; ReadOnlySpan<byte> => no allocation, mapped from PE
    public static ReadOnlySpan<byte> LightBlob => [ /* BrushSpec bytes */ ];
    public static ReadOnlySpan<byte> DarkBlob  => [ … ];
    public static ReadOnlySpan<byte> HighContrastBlob => [ … ];
    public static BrushSpec Resolve(TokenId id, ThemeKind t) =>
        MemoryMarshal.Cast<byte,BrushSpec>(Blob(t))[(int)id];
}
public static partial class Tok
{ public static partial BrushSpec AccentText => ThemeTables.Resolve(TokenId.AccentText, ThemeContext.Current); }
```

Theme switch = swap `ThemeContext.Current` + invalidate (mark `SubtreeDirty` on roots that bind tokens).
Tokens flow into `BrushSpec` (value), so the paint path never holds a `string` (FOUNDATION §1) or a COM
brush ref. Cross-platform: tables are pure data; CoreText/Metal reuse them unchanged.

### 2.5 `HookDepsGenerator` — boxing-free dependency capture (replaces `params object[]`)

**Verified problem:** Reactor `UseEffect(Action, params object[] deps)` + `deps.ToArray()` + boxing
`Equals` (Component.cs:306, RenderContext DepsEqual). FOUNDATION §6: deps = `ReadOnlySpan<DepKey>`.

`DepKey` is a 16-byte tagged union — captures the *value identity* of common dep types without boxing:

```csharp
[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct DepKey : IEquatable<DepKey>
{
    [FieldOffset(0)]  public readonly DepTag Tag;     // I32,I64,F64,Bool,StringId,Handle,Ref,Enum
    [FieldOffset(8)]  public readonly ulong  Bits;    // scalar payload (bit-reinterpreted)
    [FieldOffset(8)]  public readonly object? Ref;    // only for Tag==Ref (GC ref dep, e.g. a list)
    public bool Equals(DepKey o) => Tag==o.Tag && (Tag==DepTag.Ref ? ReferenceEquals(Ref,o.Ref) : Bits==o.Bits);
}
```

The public hook API takes spans; users still write `UseEffect(fn, count, name)` ergonomically because
the generator uses **C# interceptors** to rewrite each `Use*` call site into a `DepKey`-span form:

```csharp
// user writes:
UseEffect(() => Subscribe(id), count, isOpen);
// generator emits an [InterceptsLocation] method that the compiler redirects the call to:
[InterceptsLocation(@"View.cs", line:42, col:9)]
internal static void UseEffect__i0(this RenderContext ctx, Action fn, int a0, bool a1)
{
    Span<DepKey> deps = stackalloc DepKey[2];          // STACK, zero heap
    deps[0] = DepKey.FromInt32(a0);
    deps[1] = DepKey.FromBool(a1);
    ctx.UseEffectCore(fn, deps);                        // ReadOnlySpan<DepKey> core
}
```

`UseEffectCore(Action, ReadOnlySpan<DepKey>)` stores deps in a per-hook `SlabAllocator<DepKey>` segment
(FOUNDATION §1) for next-render comparison; comparison is `prev.SequenceEqual(next)` over spans, no box,
no `ToArray`. If interceptors are deemed too magical (they require `<Features>InterceptorsNamespaces…`),
fallback overloads `UseEffect<T0,T1>(Action, T0, T1) where T0/T1 : unmanaged-or-IEquatable` give the
same boxing-free capture via generic specialization — but interceptors are preferred (zero generic
instantiation explosion, §4.3). Both are AOT-safe; neither uses reflection.

Same treatment for `UseMemo`, `UseCallback`, `UseReducer`. `UseState`/`UseRef` have no deps. `UseContext`
keys off generated `ContextId` consts (no `typeof`).

### 2.6 `ComInteropGenerator` — COM bindings in ComputeSharp's style (NOT ComWrappers)

**Decision (D5):** reuse ComputeSharp's COM-binding *style* + extract its D3D12/DXGI/D2D1 structs; author
DWrite + DComp ourselves with our own generator. Rationale grounded in source:
- ComputeSharp's bindings are **struct-over-vtbl** (`IDXGISwapChain : IComObject` with `void** lpVtbl`
  and `delegate* unmanaged[MemberFunction]<…>` calls) — fully blittable, **zero ComWrappers, zero
  reflection, AOT-perfect, trim-stable** (verified IDXGISwapChain.cs). This is strictly smaller/faster
  than `[GeneratedComInterface]`/ComWrappers (which carry vtable-marshalling machinery and RCW caches).
- ComputeSharp only uses real `ComWrappers` in 2 callback (CCW) spots (ICanvasImageInterop). We have
  ≤3 analogous CCW seams (a DComp/DWrite app-provided callback); those use `System.Runtime.InteropServices.
  Marshalling.ComWrappers` source-gen ONLY there.

Our generator input: `[GenerateComInterface(IID="…")] partial struct IDWriteFactory7` with method
stubs annotated `[VtblIndex(n)]`. Output mirrors ComputeSharp verbatim:

```csharp
internal unsafe partial struct IDWriteFactory7 : IComObject
{
    static Guid* IComObject.IID { get { ReadOnlySpan<byte> d = [ /*16 bytes*/ ]; return (Guid*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(d)); } }
    public void** lpVtbl;
    [VtblIndex(24)] public HRESULT CreateTextFormat(ushort* name, /*…*/, IDWriteTextFormat** fmt)
        => ((delegate* unmanaged[MemberFunction]<IDWriteFactory7*, ushort*, /*…*/, IDWriteTextFormat**, int>)(lpVtbl[24]))(
              (IDWriteFactory7*)Unsafe.AsPointer(ref this), name, /*…*/, fmt);
}
```

Lifetime: we vendor ComputeSharp's `ComPtr<T>` (`unsafe struct over T:unmanaged`, FOUNDATION §1 — "the
only COM lifetime primitive we vendor") verbatim. `ComPtr<T>` + the vtbl structs live ONLY in leaf impls
(`Rhi.D3D12`, `Text.DirectWrite`, `Pal.Windows`) — FOUNDATION §4/§7 invariant. The generated bindings
DLLs are the seed for `Rhi.D3D12` (extract `ComputeSharp.Win32.D3D12` → `FluentGpu.Win32.D3D12`) and the
template for `FluentGpu.Win32.DWrite` / `FluentGpu.Win32.DComp` we add.

**What ComputeSharp does NOT give us (we author):** DWrite interfaces (`IDWriteFactory7`,
`IDWriteTextAnalyzer`, `IDWriteGlyphRunAnalysis`, `IDWriteFontFace`), DComp (`IDCompositionDevice`/
`Target`/`Visual`), and the **graphics pipeline** structs not present in its compute-only D3D12 subset
(`D3D12_GRAPHICS_PIPELINE_STATE_DESC`, `D3D12_INPUT_ELEMENT_DESC`, RTV/DSV descriptor heaps, the swapchain
graphics-present path). The generator produces all of these from the SDK header metadata the same way
ClangSharp/CsWin32 did for ComputeSharp's existing set — but we run it once and check in the `.g.cs`
(build-time-stable, like ComputeSharp ships its `.g.cs`).

### 2.7 `DslAnalyzer` — compile-time validation (`WGPU####`)

Mirrors ComputeSharp's 70+ `CMPS####` diagnostic discipline (AnalyzerReleases.Shipped.md), scoped to DSL
correctness. Concrete rules (Error unless noted):

| ID | Rule |
|----|------|
| WGPU0001 | `[Element]` type must be `sealed partial record` deriving `Element`. |
| WGPU0002 | `[Prop]` type must be diffable (value/`StringId`/handle/`BrushSpec`/delegate); no `string`/heavy WinUI types (cross-platform + paint-path-string ban, FOUNDATION §1/§5). |
| WGPU0003 | Duplicate `ElementTypeId` across `[Element]` types. |
| WGPU0004 | Modifier chain after a container's children spread is fine; but `.Set(Action<TWinUI>)` is removed → suggest `.Set(SceneWriter)` / `with`. |
| WGPU0005 | Hook called outside `Component.Render` / conditionally (rules-of-hooks; Reactor `HookOrderException` becomes a *compile-time* error). |
| WGPU0006 | Hook dep is a freshly-allocated lambda/array each render (would always re-fire) → Warning. |
| WGPU0007 (Warn) | Element built but never returned/added to a container (dead authoring). |
| WGPU0008 | `[ThemeTokens]` partial prop has no matching token in all themes (missing Dark/HighContrast). |
| WGPU0009 | `params object[]` detected in a Use* overload authored by user → forbid (steer to span). |
| WGPU0010 (Info) | Suggest `[FastPath]` on an element type used inside a per-item list template. |

Plus a **CodeFixer** assembly `FluentGpu.SourceGen.CodeFixers` (mirrors `ComputeSharp.CodeFixers`) that
auto-converts `params object[]` deps → interceptable form, and adds `sealed partial record` for WGPU0001.

---

## 3. DATA FLOW — authoring → scene (sequence)

```
USER CODE (Component.Render)                 FluentGpu.Dsl                         Reconciler/Scene
────────────────────────────                 ───────────                          ────────────────
Text("Hi")                ──factory──▶  new TextElement(Intern("Hi"))   (1 record, edge alloc)
   .Bold()                ──ext T──▶    ModifierArena.Append(None, Bold)  (arena bump, 16B)
   .Margin(8)             ──ext T──▶    Append(ref, Margin)               (arena bump, contiguous)
   .FontSize(28)          ──ext T──▶    Append(ref, FontSize)             (arena bump, contiguous)
        │                                  el with { Modifiers = ref }    (≤1 record copy total)
        ▼
   returned Element tree  ─────────────────────────────────────────────▶ Reconciler.Diff(prev, cur)
                                                                            │ DiffProps bitmask (gen'd)
                                                                            │ ModifierArena.SequenceEqual
                                                                            ▼
                                                                   el.ApplyToScene(ref SceneWriter)
                                                                            │ (gen'd) writes SceneStore
                                                                            ▼  columns via ISceneBackend
                                                                   SceneStore SoA  (Bounds, BrushHandle,
                                                                   CornerRadius4, VisualKind, NodeFlags)
                                                                            ▼
                                                                   [Layout → Animation → DrawList → GPU]
```

Per-frame heap allocations on this path: **child-template lambdas/closures the user wrote (edge,
unavoidable) + ≤1 Element record per *changed* node**. Everything else (modifiers, child lists, deps,
scene writes) is arena/slab/span. Clean (unchanged) subtrees skip `ApplyToScene` entirely (FOUNDATION §3
incremental: `memcpy` clean command spans) — so a static node costs 0 even in the DSL layer.

---

## 4. NATIVEAOT STORY

### 4.1 Hard guarantees (FOUNDATION §4)
- **No reflection, no `typeof`-keyed dictionaries on any path.** `ElementTypeId`/`ContextId`/`TokenId`
  are generated `const`s; COM IIDs are `ReadOnlySpan<byte>` literals; theme tables are baked blobs.
- **No `System.Reflection.Emit` / `Expression.Compile`.** All dispatch is generated `switch` or
  `delegate* unmanaged` (COM).
- **ComWrappers only at ≤3 CCW callback seams**, source-generated (`[GeneratedComClass]`), trim-rooted
  explicitly. The 99% RCW path is struct-over-vtbl (no ComWrappers, no RCW cache).

### 4.2 Project settings (root `Directory.Build.props` for runtime assemblies)
```xml
<PublishAot>true</PublishAot>
<IsAotCompatible>true</IsAotCompatible>                <!-- turns on trim+AOT analyzers everywhere -->
<InvariantGlobalization>true</InvariantGlobalization>  <!-- ICU dropped; DWrite does locale shaping -->
<UseSystemResourceKeys>true</UseSystemResourceKeys>    <!-- drops resource strings -->
<EventSourceSupport>false</EventSourceSupport>
<StackTraceSupport>false</StackTraceSupport>           <!-- release; flip on for debug builds -->
<MetadataUpdaterSupport>false</MetadataUpdaterSupport>
<DebuggerSupport>false</DebuggerSupport>               <!-- release -->
<AutoreleasePoolSupport>false</AutoreleasePoolSupport>
<HttpActivityPropagationSupport>false</HttpActivityPropagationSupport>
<NullabilityInfoContextSupport>false</NullabilityInfoContextSupport>
<BuiltInComInteropSupport>false</BuiltInComInteropSupport> <!-- we use struct-vtbl + src-gen ComWrappers -->
<IlcOptimizationPreference>Size</IlcOptimizationPreference>
<IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>
<TrimMode>full</TrimMode>
<SkipLocalsInit>true</SkipLocalsInit>   <!-- module-wide; hot stackalloc paths skip zero-init -->
```
`[module: SkipLocalsInit]` in every runtime assembly (the `stackalloc DepKey[]` and `ModifierOp`
scratch paths benefit; safe because we never read uninitialized — analyzer WGPU-internal verifies).

### 4.3 Avoiding generic code explosion (the AOT footprint trap)
NativeAOT instantiates each generic over each value type → uncontrolled generics blow up binary size.
Mitigations, by design:
1. **Modifier extensions are generic in `T : Element` but `T` is a REFERENCE type** → NativeAOT shares
   ONE canonical instantiation for all reference `T` (shared generics). The `(T)` cast is the only
   per-`T` cost — negligible. So `Margin<TextElement>` and `Margin<ButtonElement>` share code. ✔
2. **The work is in the non-generic `ModifierArena.Append(ModifierRef, in ModifierOp)`** — exactly ONE
   compiled body, called by all extensions. (This is why §1.4 funnels through a non-generic core.)
3. **Hook deps via interceptors (not generic overloads)** → no `UseEffect<int,bool>` style value-type
   instantiation matrix. The interceptor bodies are plain methods. (§2.5)
4. **`DepKey`/`ModifierOp` are non-generic structs**; `ReadOnlySpan<DepKey>.SequenceEqual` is one shared
   body.
5. **No `Func<T>`-keyed generic caches.** `UseMemo<T>` is generic but `T` is usually a reference type or
   a small set of value types — bounded; we add WGPU0011 (Info) if a hot `UseMemo<LargeStruct>` appears.
6. **Container children are `ChildList` (non-generic handle)** not `List<Element>`/`ImmutableArray<T>`.

### 4.4 Trimming descriptors / feature switches
- No `rd.xml` needed for the DSL/Hooks/Scene (no reflection → trimmer fully resolves the call graph).
- ONE small `ILLink.Descriptors.xml` in the COM-CCW leaf to **root the source-gen'd ComWrappers
  vtable methods** (they're reached only from native, the trimmer can't see the edge):
  ```xml
  <linker><assembly fullname="FluentGpu.Rhi.D3D12">
    <type fullname="FluentGpu.Rhi.D3D12.Interop.*Callbacks" preserve="all"/>
  </assembly></linker>
  ```
- Feature switch `FluentGpu.EnableDevtools` (default false, trimmed out) gates the accessibility/inspector
  overlay so it adds 0 bytes in release (mirrors Reactor's `UseDevtools`).
- ComputeSharp's own generators (if we take `ComputeSharp.D3D12MemoryAllocator` / `.D2D1`) run at build
  time only — **0 runtime footprint**; we DO inherit `D3D12MA`'s native allocator IL (~tens of KB) and
  its `ComPtr`/binding IL, accounted in §4.5.

### 4.5 Footprint budget + measurement (D7)

| Component | IL/binary target |
|-----------|------------------|
| `FluentGpu.Dsl` (records, factories, arena, modifier resolver) | ≤ 90 KB |
| `FluentGpu.Hooks` (RenderContext hooks + DepKey) | ≤ 45 KB |
| Generated code (per-app: setters/diff/factories for ~60 element types) | ≤ 85 KB |
| `FluentGpu.Win32.D3D12` (extracted from ComputeSharp, graphics-trimmed) | ≤ 250 KB |
| `FluentGpu.Win32.DWrite` + `DComp` (authored) | ≤ 120 KB |
| D3D12MA (if adopted) | ≤ 90 KB |
| **Whole "rounded-rect + text" self-contained AOT exe (incl. CoreCLR AOT runtime)** | **≤ 5.5 MB** |

Reference points: ComputeSharp ships in Paint.NET/Store as AOT; a minimal AOT console is ~1.2–2 MB;
WinUI 3 unpackaged apps are 30–100 MB+. Our 5.5 MB target is aggressive but defensible because we have
no XAML parser, no WinRT projections, no .NET-WinRT interop layer.

**Measurement pipeline (CI gate):**
1. `dotnet publish -r win-x64 -c Release /p:PublishAot=true` → produces the native exe + `*.map`.
2. `IlcGenerateMapFile=true` + **`sizoscope`** (the AOT size explorer) to attribute bytes per
   assembly/type/method; fail CI if any budget row regresses >5%.
3. `IlcGenerateMstatFile=true` → `.mstat` parsed by a small `size-report` tool (we write) emitting a
   markdown table per PR comment.
4. Track over time; a per-element-type marginal cost target of **≤1.5 KB generated IL** keeps the
   "60 element types" line honest (regression test asserts it).

---

## 5. CROSS-PLATFORM SEAM BOUNDARY (what's Windows-specific vs portable)

```
                              PORTABLE (no OS/GPU types)                    │  WINDOWS-SPECIFIC (leaf only)
  ───────────────────────────────────────────────────────────────────────┼──────────────────────────────
  FluentGpu.Dsl        Element records, ModifierArena, ModifierOp,          │
  FluentGpu.Hooks      DepKey, factories, BrushSpec, StringId, ChildList    │
  FluentGpu.SourceGen  Element/Modifier/Diff/Theme/HookDeps generators      │  ComInteropGenerator emits
                      (analyzers run on any OS; pure Roslyn)               │   Windows COM structs (data)
  ───────────────────────────────────────────────────────────────────────┼──────────────────────────────
  Theme tables        baked BrushSpec blobs (pure data)                    │  (consumed identically by Metal)
  ───────────────────────────────────────────────────────────────────────┼──────────────────────────────
  COM lifetime        —                                                    │  ComPtr<T>, vtbl structs, IIDs,
  graphics interop    —                                                    │  D3D12/DXGI/DWrite/DComp bindings
```

- The **entire authoring DSL + hooks + generators are OS- and GPU-agnostic** — they emit `Element`
  records and write portable POD into `SceneStore` via `ISceneBackend` (FOUNDATION §7: Dsl/Hooks are
  GPU-agnostic; Reconciler is the only bridge). A Metal/CoreText backend needs ZERO DSL changes.
- The ONLY Windows-specific generator output is `ComInteropGenerator`'s vtbl structs; on macOS the
  analogous (much smaller — Objective-C msgSend, not COM) interop is hand-written or a sibling generator.
  Authoring code never sees either.
- `BrushSpec`/`ImageSource`/`StringId`/`FontWeight` are portable value structs; DWrite-specific font
  realization lives behind `IFontSystem` (FOUNDATION §5), not in the DSL.

---

## 6. FAILURE / EDGE CASES

1. **Modifier chain crosses an arena Reset (stored Element kept across frames).** If a user stashes a
   built `Element` in a field and re-adds it next frame, its `ModifierRef` points at a *recycled* arena
   slot. **Mitigation:** `ModifierRef.Flags` carries a 4-bit `arenaEpoch`; `ModifierArena` checks epoch
   on read and, on mismatch, treats the ref as stale → the reconciler re-materializes from the element's
   intrinsic props only, and WGPU-runtime asserts (debug) / silently drops modifiers (release) + logs
   once. Better: WGPU0007-adjacent analyzer flags Elements escaping `Render` scope.
2. **Branch/alias of a modified element** (`var b = a.Bold(); var c = a.Italic();`). `a`'s ref is no
   longer the arena tail when `.Italic()` runs → slow-path copy (§1.4). Correct, just one extra copy;
   rare in practice. Analyzer WGPU0010 can note it.
3. **`?:` returning `null` child.** Handled: `ChildList.From(span)` strips nulls. `[FastPath]` ref-struct
   builders CANNOT participate in `?:`-to-`Element` → analyzer WGPU-FastPath error steers user to record.
4. **`ModifierOp.Value` precision.** `double`→`ulong` bit-exact (no loss). `Thickness` packed as 4×`Half`
   loses sub-pixel precision beyond Half range; analyzer WGPU warns if a literal exceeds Half-exact range;
   `[Modifier(Packing=FullThickness)]` opts into a 2-slot (32-byte) op for the rare high-precision case.
5. **Interceptors disabled / unsupported tooling.** Generator detects `Features` switch absence and
   falls back to the generic-overload dep capture (§2.5) — same semantics, slightly more IL.
6. **Two themes with mismatched token sets.** WGPU0008 (Error) at compile time — can't ship a token
   missing in HighContrast.
7. **Delegate dep in a hook** (`UseEffect(fn, onClick)`). `DepKey.Tag==Ref`, reference-compared. If user
   passes an inline lambda it changes every render → WGPU0006 warns (matches React's exhaustive-deps).
8. **Element record `with` on a `[FastPath]` type.** The ref-struct builder has no `with`; analyzer
   routes `with` to the materialized record (implicit conversion first). Documented limitation.
9. **COM IID byte-order.** Generator emits little-endian field-split bytes exactly as ComputeSharp does
   (verified IDXGISwapChain.cs layout) — a golden test compares generated IIDs against `Guid.ToByteArray`.

---

## 7. OPEN QUESTIONS (for architect / cross-agent)

1. **Interceptors vs generic overloads for hook deps** — interceptors are cleaner for footprint but are
   a preview-adjacent C# feature requiring an opt-in `<InterceptorsNamespaces>` and are sensitive to
   file path/line (fragile under source refactors; the generator must re-emit on edit — incremental but
   noisy). Acceptable? Or prefer the generic-overload fallback as primary? (Recommendation: interceptors
   primary, overloads fallback, behind a `FluentGpu.UseInterceptors` build switch.)
2. **Does `ISceneBackend` (Reconciler) want the bitmask `DiffProps` result, or should it re-diff from
   SoA columns?** I assume the DSL-generated bitmask is handed to the reconciler (cheapest), but that
   couples the generated enum to the Reconciler agent's API. Need the `ISceneBackend.Apply(elementTypeId,
   ref readonly Element, propChangedMask)` signature pinned. (Cross-agent: Reconciler.)
3. **`BrushSpec` layout** — owned by Render/Scene agent. DSL assumes it's a ≤16-byte value struct
   (solid/gradient-handle union). Confirm size so `ModifierOp.Value` packing (8 bytes) can hold a solid
   color but must reference a `BrushHandle` for gradients. (Cross-agent: Render.)
4. **Should `[FastPath]` ref-struct builders be generated for ALL elements** (and the record path become
   `implicit`-derived), unifying the two? Risk: ref-struct ternary/null limits. Deferred; default record.
5. **Extracting ComputeSharp.Win32.D3D12 vs depending on the NuGet** — extraction lets us trim the
   compute-only bits and add graphics structs in one tree; dependency keeps us in sync with upstream
   fixes. Recommendation: **fork-extract** (rename `FluentGpu.Win32.*`) because we must add graphics PSO/
   swapchain/DComp/DWrite the upstream will never carry, and footprint trimming needs source control.
   (Cross-agent: RHI.)
6. **Theme-token blob format** — should HighContrast pull from OS system colors at runtime (needs a tiny
   PAL `ISystemColors`) rather than baked blobs? Baked is smaller/portable; OS-HC is more correct on
   Windows. Recommend baked default + optional PAL override. (Cross-agent: PAL.)

---

## 8. SUMMARY OF DECISIVE CHOICES

- **Element = immutable record at the edge (D1).** Not a struct. Equality/`with`/null/ternary preserved.
- **Modifiers = 16-byte `ModifierOp`s bump-allocated in a per-render arena; Element holds an 8-byte
  `ModifierRef` (D2).** Kills Reactor's O(nodes×modifiers) record-copy storm → O(modified nodes).
- **Concrete type preserved via shared-generic `T : Element` extensions funneling to one non-generic
  `Append` core (D3, §4.3).** No generic explosion; `.Set()`/`with` still strongly typed.
- **Six source generators in one netstandard2.0 analyzer DLL (D4)**: Element setters/factories, Modifier
  methods, no-reflection bitmask Diff/Equality, baked theme-token tables, boxing-free hook-dep capture
  via interceptors, and a struct-over-vtbl COM-interop generator — plus a `WGPU####` analyzer + codefixer.
- **COM interop copies ComputeSharp's struct-over-vtbl + `delegate* unmanaged[MemberFunction]` style and
  extracts its D3D12/DXGI/D2D1 structs; we author DWrite/DComp/graphics-pipeline bindings ourselves;
  ComWrappers only at ≤3 CCW seams (D5).**
- **Hook deps = `ReadOnlySpan<DepKey>` (16-byte tagged-union), zero boxing, replacing `params object[]`
  + `.ToArray()` (D6).**
- **NativeAOT: no reflection anywhere on the authoring path; size-tuned `Directory.Build.props`; module
  `[SkipLocalsInit]`; shared-generics discipline; ≤5.5 MB exe budget enforced by sizoscope/mstat CI
  gate (D7).**
- **Entire DSL/Hooks/generators are OS/GPU-agnostic; only `ComInteropGenerator` output and the leaf
  `ComPtr<T>`/vtbl structs are Windows-specific — a Metal/CoreText backend needs zero authoring changes.**
