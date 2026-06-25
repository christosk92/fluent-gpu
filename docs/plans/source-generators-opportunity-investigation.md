# Source generators for fluent-gpu: where they make the engine faster and leaner

## Executive summary

The hot path is already won. Frame phases 6–13 run at **zero managed allocation** (the VerticalSlice `HotPhaseAllocBytes` gates enforce it), so no generator can "restore" a broken per-frame property there. The honest opportunity surface is **three narrower regions**: (a) the **reconcile edge** — the bounded-Gen0 region the engine explicitly admits, hit per interaction / per data-arrival / per route-nav / per virtual-row recycle; (b) **startup / first-frame**; and (c) **steady-state CPU/cache** on the layout + record-walk paths (cycles, not bytes). Most of the textbook generator wins the brief hypothesized (constant-fold layout, frozen token lookups, SoA column accessors, ElementTypeId dispatch) **do not apply** because the corresponding code is already optimal — token reads are static-field loads, SoA accessors are `ref`-returning array indexes, type dispatch is an `int` compare, and the DrawList encode is a `MemoryMarshal.Write` blit.

The result of verifying all 18 candidates against the live code is sobering but useful: **the single "highest-value" candidate (GEN-01, the DiffProps differ) survives only as build-later**, because its two headline claims (a memory win and a recorder-elision multiplier) are false against the code, leaving a modest off-hot-path CPU saving. The genuinely defensible work clusters into a small set:

- **GEN-02 — HookDeps lowering** (`params object[]` → stack/inline `DepKey` span): removes the dossier's *self-identified worst authoring-path GC offender* — 1 array + K boxes per dep-bearing hook per re-render. The right idea, but **blocked**: it needs `GcDepTable` + string-interning + the `[InlineArray]` >4-arity overflow (none in `src/` yet) and a canon reconciliation of the shipped 2-long `DepKey` against its owning doc. Build after those land.
- **GEN-17 — Cold side-table slab generator** (replace ~30 `Dictionary<int,…>` with dense slab + ref column): the one candidate that touches the *genuinely per-frame* hot path (layout `ContainsKey` probes + the per-node record-walk `TryGet` probes + a 31-deep `FreeSubtree` cascade). A cycles/cache win, not a memory one, and a data-structure migration — but it **implements** a design `scene-memory.md §2.6` already ratified and never shipped.
- **GEN-04 — Static-subtree hoisting** (cache provably-constant VDOM as singletons): the one place that turns a whole subtree's reconcile into a true `ReferenceEquals` no-op at `Reconciler.cs:360`. Not formally verified in this pass (no verdict was returned) — labeled accordingly.
- **GEN-08 — ThemeBlobGenerator** and the **COM-binding generator** (the critic's biggest miss): both real, both **footprint/enablement**, neither a frame speedup. Defer to when their downstream consumers (BrushDeriver / the TerraFX-free macOS seam) are actually being built.
- **GEN-01 — DiffPropsGenerator**, folded with **GEN-03**'s id-guard and **GEN-10**'s equality-trim, as one unit: build-later, behind a per-type parity gate, only if a 10k-row recycle profile shows `WriteColumns` dominating frame time.

The thesis in one line: **generators help at the reconcile edge, at startup, and on steady-state CPU — never on phases 6–13, which are already 0-alloc — and the survey's most-hyped candidate is its most over-claimed.**

---

## Existing generator baseline

`FluentGpu.SourceGen` (netstandard2.0, the single in-repo Roslyn assembly) ships **two** incremental generators, each dormant unless its trigger is present, and reserves the rest as scaffolds. Recommendations slot into these conventions.

**Shipping:**

- **`Localization/LocalizationKeysGenerator.cs`** — harvests `*.resx` via `AdditionalFiles`, emits a compile-safe loc-keys family. Hand-rolls a small equatable model (`LocalizationKeysGenerator.cs:268-276`, `BaseFile`).
- **`Validation/ValidatorGenerator.cs`** — `ForAttributeWithMetadataName` on `[Validatable]`, emits a per-type `Validate` partial static method. Hand-rolls its own equatable model (`ValidatorGenerator.cs:191-194`, `Model`/`MemberRules`). **This is the exact `ForAttributeWithMetadataName → emit partial static` shape every survivor below reuses.**

**Reserved-but-unbuilt** (`SourceGen/Placeholder.cs:9-16`, the `SourceGenMarker` scaffold, verbatim):

- *Engine codegen (per `dsl-aot.md`):* `ElementTypeId`, modifier extensions, **bitmask `DiffProps`**, **the scene-writer** (homed in the reconciler/leaf), **`HookDeps` (≤4-arity capture + lanes/transition lowering)**, **Theme blobs**, and the `WGPU####`/`FG####` analyzers. Portable / Win32-free.
- *COM-binding generator (per `com-interop.md`):* emits hand-vtable `IComObject` consume bindings + callee CCW vtables from a harvested **`*.comabi.json`** (no human-typed slot indices), plus the `FGCOM####` rules. Win32-free at the source level.

Two facts about the *as-built* engine that the scaffolds pre-suppose but the code has not yet realized:

- The reconciler already integer-dispatches on `ElementTypeId` (an abstract `ushort` const per record, e.g. `BoxEl => 1`), **but the id is hand-written** (`Element.cs:25`), not generated. All 14 ids are currently distinct and dense (verified across `Element.cs`, `ComponentEl.cs:13`, `ControlFlow.cs`, `SkeletonRegion.cs:55`, `VirtualListEl.cs:23`).
- `DepKey` ships as **two bare `long`s** with `From(int/float/bool/NodeHandle/…)` packers and `Equals` = two `long ==` (`DepKey.cs:20-51`). Its own header (`DepKey.cs:8-17`) flags that **`GcDepTable` is a comment, not code**, and that the `[InlineArray]` >4-scalar overflow is "a follow-up." This gap is the gate on GEN-02.

The discipline to honor: **every cross-cutting contract has one owning doc** (`design/subsystems/README.md`). The generators below either implement an already-owned contract (GEN-01/02/03/08/17) or are rejected partly *because* they would mint a parallel owner (GEN-16, GEN-18).

---

## Recommendations

Leverage = impact × confidence / effort. Impact is graded against the verified reality (reconcile-edge / startup / steady-CPU), **not** against phases 6–13.

### Build now

*(none unconditionally)* — No surviving candidate clears the bar of "real, verified, hot-path-or-edge win at proportionate effort **today**." The closest, GEN-02, is blocked on missing infrastructure; GEN-17 is a correctness-sensitive data-structure migration; GEN-04 is unverified. This is the honest outcome of a codebase that already won its hot path. The ordered plan in §5 says what to build *first* among the build-later set.

### Build later

| ID | Generator | Domain | Impact | Effort | Verdict |
|----|-----------|--------|--------|--------|---------|
| GEN-02 | HookDeps lowering (`params object[]` → `DepKey` span) | hooks | Medium (edge GC) | L | build-later — **unblock first** |
| GEN-17 | Cold side-table slab (30 dicts → slab + ref column) | scene-memory | Medium (per-frame CPU/cache) | L | build-later |
| GEN-04 | Hoist static Element subtrees to singletons | dsl | Moderate (edge GC) — **unverified** | M | build-later — verify first |
| GEN-08 | ThemeBlobGenerator (TokenSet → data-section blob) | theming | Low (startup + enablement) | M→L | build-later — when BrushDeriver lands |
| GEN-COM | COM-binding generator from `comabi.json` (**critic-added**) | windows/interop | Low for speed; real for **footprint + portability** | L | build-later — when macOS seam / trim-footprint matters |
| GEN-01 | DiffPropsGenerator (+ GEN-03 id-guard, GEN-10 equality-trim) | reconciler/dsl | Low (edge CPU only) | L | build-later — behind parity gate, only if profiled |

### Rejected

| ID | Generator | One-line reason |
|----|-----------|-----------------|
| GEN-03 (dispatch half) | `delegate*[]` reconcile dispatch table | Cascade is off-hot-path; type dispatch already an `int` compare; a function-pointer table is likely **slower** than the canon-mandated dense `switch` on NativeAOT. (Id-assignment half folds into GEN-01.) |
| GEN-05 | BakedGradientBrushGenerator | Real but tiny (~2 arrays + 2 structs beside a larger unconditional `Style` alloc it leaves untouched); a ~15-line hand-written epoch cache in `Tok` does the same with zero generator surface. |
| GEN-06 | Source-gen handler thunks | Premise refuted: the scaling list path (`RowBind`) runs each template once per slot already; secondary "compounds with GEN-01" synergy is imaginary (`SetClickHandler` does no ref-diff); per-component delegate cache is a retention + mis-key correctness liability. |
| GEN-07 | BakedMotionTokenGenerator | Cost provably absent from the per-frame path (the slab carries baked spring constants; `ControlFaster` is an `Eased` token that never calls `FromResponse`); a bounds-checked array load may **regress** the constant-foldable getter. Zero memory win. |
| GEN-10 (standalone) | Trim dead synthesized record equality | Footprint-only; full-trim AOT likely already DCEs the unreachable `Equals`; contradicts D1's value-equality rationale + double-owns Element-equality with GEN-01. Fold the line-item into GEN-01. |
| GEN-11 | Specialized measure/arrange for fixed templates | `[FixedLayout]` cannot fire on the very card grids it targets (they contain `Embed.Comp`/`RichText`/`Shimmer` component children whose shape isn't static); few-ns payoff for a forever per-template byte-identical CI parity tax. |
| GEN-12 | Packed GridSpec track tables | Headline array alloc already engineered away app-side (hoisted `static readonly` / per-tier memo); only the track *partition* folds (~5–9 float adds on an already-`stackalloc`'d buffer); auto-fill grids short-circuit before it. |
| GEN-13 | BakedAcrylicGenerator | Premise refuted: the two reconcile-path targets do **no** per-read `FromRgba` (field-derefs over already-baked `static readonly`); the only divides run once at static init. |
| GEN-14 | `with`-copy modifier fusion | The "modifier storm" is absent — grep finds **zero** ≥2-method modifier chains in real `.cs`; object-initializers/single `with` are already 1-alloc; the only material form (ModifierArena) is an absent ground-up `Element` rewrite, not a generator. |
| GEN-15 | Compile-time signal dep-graph specialization | Edge-only over 1–4-element lists; static read-set inference is unsound except for trivial components (where the cost is already nil); a ~10-line `_subs` set/mark-bit captures the win with no codegen. |
| GEN-16 | Unified generator attribute/manifest toolkit | Re-proposes infra `dsl-aot.md §2` already owns; its new markers double-own artifacts assigned to scene-memory/gpu-renderer/theming/animation. The real action (lazily extract `EquatableArray<T>`) is an ordinary refactor, not a contract. |
| GEN-18 | Span-format codegen for DSL/text | Cost is ~3–4 tiny strings/sec at the 1 Hz clock edge, dwarfed by the re-render it sits in; the engine already owns this exact pattern as `DynamicTextKind` + `RefreshDynText` (a second mechanism = canon conflict). |

---

## Build-now details

There are no unconditional build-now generators (see the Build-now note above). The closest-to-shovel-ready work — the items that earn a build slot **once their preconditions are met** — is detailed here as if "build-now, conditionally," in priority order. Each entry gives the mechanism, the verified evidence, the honest perf **and** memory wins, the `FluentGpu.SourceGen` fit, and the owning `design/` doc(s) to reconcile.

### GEN-02 — HookDeps lowering (highest-leverage *real* win, once unblocked)

**Mechanism.** *Trigger:* presence of a call to a known hook method symbol (containing type `FluentGpu.Hooks.RenderContext` + method name + the `params object[]` parameter) — no attribute needed (or opt-in `[LowerDeps]` for scoping). *Input model:* the syntactic argument list + each argument's resolved static type. *Emitted code shape* (exactly `reconciler-hooks.md §3.4`): scalars (`int/float/bool/long/StringId/NodeHandle`) lower to `DepKey.From(...)` packed into a `Span<DepKey> __d = stackalloc DepKey[k]; … ctx.UseEffect(body, __d);`; reference deps (`object`/delegate) route to a side `GcDepTable` slot compared by `ReferenceEquals` (the canon `DepKey`-can't-hold-a-GC-ref rule). Plain AOT C#; same `ForAttributeWithMetadataName`-class incremental shape as `ValidatorGenerator`.

**Evidence (the cost it removes).** Boxing overloads exist at `RenderContext.cs:301,304` (UseEffect/UseLayoutEffect), `:366` (UseMemo), `:540,543,546,549` (UseSpring/UseTransition/UseKeyframes/UseDrivenAnimation), `:642` (UseAsyncResource). The megamorphic compare is `DepsEqual(object[],object[])` over `object.Equals` at `RenderContext.cs:758-764`. Confirmed real scalar-dep call sites that box today: `PagedShelf.cs:234`, `NavigationView.cs:377`, `WaveeShell.cs:150,155`, `Expander.cs:108`, `TabView.cs:312`. The dossier ranks this **the worst authoring-path GC offender** (`animation-engine-research-dossier.md:235,:11`). DepKey overloads already ship for the common hooks (`RenderContext.cs:308,385`) and authors hand-opt-in today (`Surfaces.cs:119`; `DetailShell.cs:129` even hand-hashes a ref dep to an int to dodge boxing) — the generator makes the win automatic and total.

**Perf win.** Modest — struct `==` (two `long ==`, `DepKey.cs:46`) replaces a megamorphic `object.Equals` loop; most hooks fire only on real dep change. The headline is allocation, not throughput.

**Memory win.** Direct and real at the **re-render edge** (never phases 6–13 — hooks don't run there): **1 array + K boxes eliminated per dep-bearing hook per re-render** across ~70+ Controls/app call sites. On a re-rendering page (search-as-you-type, resizing shelf, hover/drag churn) this is the dominant bounded-Gen0 source after string interning.

**FluentGpu.SourceGen fit.** Reuses the shipping `ForAttributeWithMetadataName → emit lowering` pattern; symbol-match on the known hook overloads. This is precisely the `HookDeps (≤4-arity capture)` scaffold at `Placeholder.cs:10`.

**Canon doc(s) to reconcile (mandatory, before the generator can target a stable surface).** `reconciler-hooks.md` is the single owner (`design/subsystems/README.md:127`). Its §3.2/§3.3 specify `DepKey` as `{DepKind byte tag, Bits}` + `FromStr`/`FromRef` + a `GcDepTable` side-buffer + string interning (`:260-277,:287-333,:332`). The **shipped** `DepKey` is two bare longs with **no** tag/Str/Ref and **no** `GcDepTable` (`DepKey.cs:20-51`; confirmed). This single-owner drift must be reconciled (doc → two-long shape, or code → tagged shape) **first**; then run `check-canon.ps1`. Generating the *computation* of a `DepKey` is explicitly blessed (`reconciler-hooks.md:1076` names "source-gen'd dep-span lowering") — the conflict is the drift gate, not a redefinition.

**Why it is build-later, not build-now.** The emitted shape depends on `GcDepTable` + string interning + the `[InlineArray]` >4-arity overflow, **none of which exist in `src/`** (`DepKey.cs:14` flags the overflow as a follow-up), plus span overloads for the four retained-anim hooks. Build those (and reconcile the doc) first; then the generator collapses to a clean **M** with medium, real GC-edge payoff.

### GEN-17 — Cold side-table slab generator (the only per-frame-CPU win)

**Mechanism.** *Trigger:* an `AdditionalFiles` manifest or `[ColdSlab]` markers listing each per-node feature + its payload type. *Input model:* the `(feature → payload struct)` set, **segregating `unmanaged` from reference/array payloads** (only the former fit `SlabAllocator<T> where T:unmanaged`). *Emitted code shape:* per feature, a dense slab + a per-node int ref column + typed `Get/Set/Clear/Free` accessors that preserve get-or-add ref semantics, plus a fused `FreeSubtree` fast-path that releases a freed node's slab entries via the index columns. Plain AOT C# over the existing `SlabAllocator<T>` (`Foundation/Allocators.cs:12`).

**Evidence (the cost it removes).** The ~30 sparse tables are `Dictionary<int,…>` verbatim (`SceneStore.cs:94-142`: `_scroll,_extents,_grids,_interact,_shadows,_arcs,_polylines,_gradients,_borderBrushes,_hover/_pressedGradients,…,_spanText,_dragSources,_dropTargets,_gestureSubs`; plus `_hitPassThrough:774`, `_scrollObs:801`). `FreeSubtree` does **31 sequential `.Remove(idx)`** per freed node (`SceneStore.cs:287-313`). The probes are **per-frame, not merely reconcile-edge** (this is the critic's correction to the layout map's framing, verified): `FlexLayout.cs:177-178,332-333` do two `ContainsKey` (HasScroll/HasGrid) on **every measured and arranged node every layout pass**; `SceneRecorder.cs` probes 5–10+ `TryGet` per node per record pass (`:241,320,327,365,394-406,594,653-669,1141-1181`).

**Perf win.** Real steady-state: hash + bucket-chase → index deref on the layout and record-walk paths; `FreeSubtree` from 31 hash deletes → a handful of index clears, hot on nav churn / virtual recycle. The dense column accessors themselves (`SceneStore.cs:431-435`) are **already optimal `ref`-returning indexes** and correctly **not** a target.

**Memory win — partial, and partly self-defeating (stated honestly).** Reduces `Dictionary` bucket/entry overhead and rehash churn. **But** a "dense slab + per-node int ref" adds a ~4-byte ref column to the spine for **every** node including leaves — ~30 features ≈ **+120 B/node** — which can *eat* the dictionary-overhead saving unless features share a packed ref block (extra design). And several payloads are **not** `unmanaged` (`_spanText:TextSpan[]` `:129`; `_textEditSelRects:(RectF[],int)` `:124-125`; `_extents:ExtentTable` `:96`), so the generator covers maybe ~20 of ~30 tables and the `FreeSubtree` win shrinks proportionally. **No allocation is removed on the hot path — it is already 0-alloc** (the probes are `TryGetValue`/`ContainsKey` on value-typed dicts); the win is cycles/cache, not GC.

**FluentGpu.SourceGen fit.** Manifest-driven (the `comabi.json` precedent) or `[ColdSlab]`-marked; emits accessors + a fused teardown. Reuses the equatable-model + emit pattern; the `SlabAllocator<T>` primitive already exists.

**Canon doc(s) to reconcile.** `scene-memory.md` is the owner (`design/subsystems/README.md`). Its **§2.6 (388-398) already ratifies this exact design** ("referenced cold slabs: the hot row carries a 4-byte ref … the small fraction of nodes that use them index a side `SlabAllocator`"); §2.7 names `SlabAllocator<SelectionState>`, an `A11yRel` cold slab, an `EffectAux` cold slab + `EffectAuxRef:int`. The generator **implements** the owned contract — it does not redefine a bound one. No `DepKey`/`Handle`/`ComPtr`/`ChunkedArena`/`AnimValue` contract is touched. Coordinate the new ref-column/dispatch with the §2.6 owner; register nothing new; run `check-canon.ps1`.

**Why build-later, not build-now.** It is a **correctness-sensitive data-structure migration**, not a side-effect-free precompute: it must reproduce `CollectionsMarshal.GetValueRefOrAddDefault` get-or-add semantics (`SceneStore.cs:762,808,831`), `NodeFlags` side-effects on first insert (`ScrollRef` sets `NodeFlags.Scrollable`, `:763`), and exact `FreeSubtree` teardown ordering incl. `OnFreeIndex` + overlay/ghost cleanup (`:287-316`). Land it after the cheaper/pure-precompute items, behind a green VerticalSlice + `--screenshot` diff.

### GEN-04 — Hoist provably-static Element subtrees to cached singletons *(unverified — verify before building)*

**Mechanism.** *Trigger:* a `WGPU0010`-style purity verdict (`dsl-aot.md:479`) — an element-producing expression with **no free variables that aren't compile-time constants** (literal sizes/colors/**non-theme** tokens, no captured locals, no signals, no handlers). *Input model:* the syntax subtree + a constant-foldability verdict. *Emitted code shape:* a `static readonly Element __hoisted_N = <expr>;` in a generated partial + a call-site rewrite (interceptor/codefix) to read the singleton. Plain AOT C#.

**Evidence (the cost it removes).** Per-render allocation of static chrome: `ArtistShimmer`/`HomeShimmer` fixed bar/cover trees rebuilt every mount (`ArtistPage.cs:90-144`, `HomePage.cs:126-136`), `Divider`/`SectionHeader` spacers, and the `new BoxEl()` empty placeholders used as conditional `: new BoxEl()` (`MediaCard.cs:116,150,294`). The unique leverage: the reconciler **already** shortcuts on `ReferenceEquals(newEl, oldEl)` (`Reconciler.cs:360`, verified) but never gets the chance because every render allocates fresh — a hoisted subtree's whole `Update` collapses to that line-360 no-op, skipping diff, column writes, **and child recursion** for the entire subtree.

**Perf win.** Moderate-to-large for static-heavy trees: a hoisted subtree's reconcile becomes O(1). Biggest on skeleton/empty/divider chrome and icon glyphs at route nav and skeleton→real swaps.

**Memory win.** Eliminates the Gen0 records + `Children[]` arrays for every hoisted subtree on every render after the first. For shimmer trees (dozens of bars) rebuilt on each load, a direct per-nav Gen0 cut.

**FluentGpu.SourceGen fit.** Pairs with the `WGPU0010` purity analyzer; emits a `static readonly` partial + an interceptor/codefix.

**Canon doc(s) to reconcile.** `dsl-aot.md` owns the DSL purity diagnostics (`WGPU0010`, `:479`). The hazard to bound in the design: **token-derived colors (`Tok.*`) resolve at element construction** (`Element.cs:438-441` caveat) and go **stale on a live theme switch** — a hoisted-with-tokens singleton would freeze the old theme. The generator must restrict hoisting to truly theme-independent literals, **or** invalidate hoisted singletons on `Tok.Epoch`; conditional-render identity (`?:` returning the singleton vs null) must stay sound.

**Why this carries an asterisk.** This candidate's verdict came back **null** (it was not run through the critic's confirm/refute pass). Everything above is from the candidate's own evidence, which is consistent with verified facts (`Reconciler.cs:360` confirmed; the `Tok.*` construction-time caveat is real). **Treat the impact as plausible-but-unverified** and confirm the rebuild-every-mount claim (e.g. instrument `ArtistShimmer` mount allocations) before committing build effort.

### GEN-08 — ThemeBlobGenerator (startup + enablement, **not** a frame speedup)

**Mechanism.** The designed-but-unbuilt generator (`dsl-aot.md §2.5`; `Placeholder.cs:10`). *Trigger:* `[ThemeTokens]` on `partial class Tok` + per-token value annotations the generator reads to compute the byte blob at compile time (the `/255` fold done at compile time). *Emitted code shape:* `enum TokenId : ushort {…, Count}` + per-theme `static ReadOnlySpan<byte> DarkBlob => […]` (`RuntimeHelpers.CreateSpan` data-section literal, **zero static-ctor**) + `static ColorF Resolve(TokenId,ThemeKind) => MemoryMarshal.Cast<byte,ColorF>(Blob(t))[(int)id]`. AOT/full-trim-safe (the canonical `CreateSpan` + `MemoryMarshal.Cast` over a blittable `ColorF` pattern).

**Evidence (the cost it removes).** At first `Tok.T` touch, two heap `TokenSet` records allocate (`static readonly TokenSet Dark = BuildDark(); Light = BuildLight();`, `Tokens.cs:161-162`); `TokenSet` is a `sealed record` with ~78 `required` fields ≈ ~2.6 KB total. `ColorF.FromRgba` is a runtime divide, not const (`Geometry.cs:62-63`); BuildDark+BuildLight run **~149 `FromRgba` calls ≈ ~596 divides** (`Tokens.cs:331-515`). All of this is **startup-only** — every `Tok.*` read is already a single static-field load through the active-set pointer (`Tokens.cs:190-329`), and the render thread reads the already-resolved `ColorF` from the scene paint column, never `Tok` (`theming.md:9-14,31-33`).

**Perf win.** Startup/first-frame only: elides the static-ctor cost. **Negligible steady-state win** — `MemoryMarshal.Cast<byte,ColorF>(blob)[id]` (a bounds-checked span index) is comparable to (arguably marginally worse than) today's single field load. **Do not sell as a frame speedup.**

**Memory win.** Removes ~2.6 KB of Gen0 (two `TokenSet` records) from the managed heap; tokens become non-GC data-section bytes. Modest, one-time.

**FluentGpu.SourceGen fit.** It **is** the registered owner of its design slot (`README.md:153` ownership map; `dsl-aot.md §2.5`).

**Canon doc(s) to reconcile.** `dsl-aot.md` owns `ThemeBlobGenerator` and the T0 tier. Mandatory reconciliations before building: (1) the §2.5 **type drift** — the doc says `BrushSpec` (`:436-440`; `WGPU0002`/§2.7 `:471` also still lists `BrushSpec`) but the **live type is `ColorF`** (`Geometry.cs:59`); (2) the blob layout must handle **heterogeneous** token types (`AcrylicFlyout` is an `AcrylicSpec`, `Tokens.cs:102`, not a `ColorF`) — the flat `Cast<byte,ColorF>[id]` model is too simple; (3) `Tok` must **remain a hand-written shim** over the generated tables, because accent folding and overrides are **not** flat reads (`_accent ?? T.AccentDefault`, `a with { A = 0.90f }`, `Tokens.cs:260-284,297,329`). Update `SPEC-INDEX §2` + the ownership map if the public surface changes; run `check-canon.ps1`.

**Why build-later.** Low impact (one-time ~2.6 KB + ~596 divides, outside phases 6–13 and outside the reconcile edge) for M→L effort. Its real justification is **enablement**: it provides the `Resolve(TokenId,ThemeKind)` indexer the still-unbuilt `BrushDeriver` (`theming.md §6`) needs to converge T0/T1/T2 onto one `BrushHandle`, and unblocks the `WGPU0008` "no token missing in a theme" diagnostic (`dsl-aot.md:448,477`). Build it **when the BrushDeriver / live-system-color (T1) / album-art-derived (T2) tiers are actually implemented**, bundling the `BrushSpec → ColorF` reconciliation + the override-shim design into that unit of work.

### GEN-COM — COM-binding generator from `comabi.json` (critic-added; footprint + portability, **not** speed)

**Mechanism.** *Trigger:* `[ComAbi("d3d12.comabi.json")]` / an `AdditionalFiles` harvest of a checked-in `*.comabi.json`. *Emitted code shape:* `unsafe static` `calli` thunks through `T** lpVtbl` + IID `u8` data-section spans (hand-vtable `IComObject` consume bindings + callee CCW vtables), with **no human-typed slot indices**, plus the `FGCOM####` rules. This is the design-of-record in `com-interop.md` and the `Placeholder.cs:13-16` scaffold; the brief itself named `comabi.json` as a trigger.

**Evidence + honest perf framing.** This is **not** a per-frame-speed win. The hot path today uses TerraFX bindings (`D3D12Device.cs:6-7,216+`, e.g. `_device->CreateCommittedResource`) which **already do zero-alloc `calli`**; `com-interop.md:388` states today's goal is already "zero managed allocation, zero RCW-cache lookup." The real angles are: **AOT/startup footprint** (dropping the large `TerraFX.Interop.Windows` surface from trimmed output on the render-thread hot path), the **portability seam** (TerraFX-free; the macOS port), and **correctness** (generated slot indices + `FGCOM` gates vs hand-typed vtable offsets).

**Memory/footprint win.** Trims the TerraFX surface from the AOT binary; honors the canon rule "generated-from-`comabi.json` hand-vtable on the hot path + `[GeneratedComInterface]` only for cold COM, **no hot-path `ComWrappers`**." Does **not** reduce per-frame managed bytes (already zero).

**FluentGpu.SourceGen fit.** Win32-free at the source level (no Win32/TerraFX `PackageReference`), so it can live in the portable analyzer assembly. The `AdditionalFiles` manifest convention is exactly the `comabi.json` precedent the scaffold reserves.

**Canon doc(s) to reconcile.** `com-interop.md` is the owner; the `*.comabi.json` manifest convention is its artifact. The bound contract ("generated hand-vtable on the hot path; the render thread owns every `ComPtr`; no hot-path `ComWrappers`") is **implemented**, not redefined. Coordinate with `threading-render-seam.md` (the render thread owns every `ComPtr`); register nothing new; run `check-canon.ps1`.

**Why build-later.** Rates **footprint/enablement, not hot-path speed** — the same reason GEN-08 defers. Worth doing **when the macOS seam or a trimmed-binary-size target makes dropping TerraFX load-bearing**, not for frame time.

### GEN-01 — DiffPropsGenerator (+ GEN-03 id-guard + GEN-10 equality-trim, as one unit)

**Mechanism.** *Trigger:* an `[Element]` marker on each record (or discovery of all `sealed record X : Element`). *Input model:* an equatable POCO `{TypeName, Props[]{Name, TypeFullName, DiffStrategy, ColumnGroup}}` from the record's init properties; `ColumnGroup` from `[Column(...)]` or convention (Fill→Paint, Width→Layout, OnClick→Interaction). *Emitted code shape:* per type, a `ColumnMask Diff(T a, T b)` (flat `a.X!=b.X` compares; `ReferenceEquals`+present-bit for delegates/specs; **excludes `Children`**) + a `WriteColumns_T(SceneStore, NodeHandle, T, ColumnMask)` executing only masked stores; `Update` calls `var d = Diff(new,old); if(d==0) return; WriteColumns_T(...)` and marks `PaintDirty` only when `(d & AnyPaint)!=0`. **Fold in GEN-03's id half** (generator-owned `ElementTypeId` + a `WGPU0003` duplicate-id error) and **GEN-10's equality-trim** (a trivial reference-based `Equals`/`GetHashCode` so Roslyn skips the synthesized per-field versions) — both are line-items inside this one generator, not separate units (per the critic's merge and `dsl-aot.md §2.2`, which makes this generator the owner of Element diff + `Equals`/`GetHashCode`).

**Evidence (the cost it targets).** `WriteColumns` runs the full per-type copy on every surviving-node `Update`; the only fast-out is `ReferenceEquals(newEl,oldEl)` (`Reconciler.cs:360`, ~always false because Render allocates fresh); no field diff/mask exists (grep `DiffProps`/`ColumnMask` → only the `Placeholder.cs:10` scaffold). Recycle (`Reconciler.cs:1344`) and keyed (`:1513`) Update both feed it; unconditional `PaintDirty` at `:2361`. `MediaCard.cs:49-79` re-specifies constant style literals every Render.

**Perf win — modest, off-hot-path.** Skipping ~95% of column writes on a 1–3-field change saves writes to **cache-resident SoA slots** (`SceneStore.cs:431-434` returns `ref` into dense arrays; `Columns.cs:38,80` are structs) — nanoseconds-to-low-µs per re-rendered node, at the **reconcile edge the gates explicitly exclude** from the 0-alloc measurement (`Program.cs:5666-5668,7677-7678`). Nothing for steady scroll (no reconcile), phases 6–13, or startup.

**Memory win — essentially nil (verified against code, refuting the candidate's headline).** Column stores, **handler stores** (`SetClickHandler` = `_click[LiveIndex(h)]=handler`, a dense-array write, `SceneStore.cs:460` — **confirmed**, so "cuts Dictionary touches" is wrong for handlers), and **warm-dictionary touches** on the sparse specs all allocate **0 bytes**. The genuinely allocating edge work — `Intern` (`:2261,2266`), `Images.Request` (`:2209`), `SpanRunTable.Create` (`:2323`), fresh-record allocation in Render — is **already field-gated** (`:2211,2262,2293,2267`) or lives **upstream** of `WriteColumns`, so a diff mask can't reduce it.

**The "recorder stops re-emitting clean subtrees" multiplier is false (verified).** `SceneRecorder.Record` re-walks the full visible tree whenever it runs; it is **not** gated by per-node `PaintDirty`. Frame elision is the `DrawListHash`/`skipSubmit` gate (`AppHost.cs:1233-1234`, confirmed) — so over-marking `PaintDirty` at `Reconciler.cs:2361` costs nothing downstream.

**FluentGpu.SourceGen fit.** Same `ForAttributeWithMetadataName → emit partial static` shape as `ValidatorGenerator`; emit into the **Reconciler assembly** to preserve `Dsl→Scene` acyclicity. Implements the `bitmask DiffProps` + `scene-writer` scaffolds (`Placeholder.cs:9-10`).

**Canon doc(s) to reconcile.** It **implements** an already-owned contract — `dsl-aot.md §2.2 (339-367)` (the `[Element]`/`[Prop]` triggers, the per-prop diff-strategy table, `WGPU0002`/`WGPU0003` at `:471-472`), mirrored in `reconciler-hooks.md §5.3 (588-605)` and `layout.md §4.1`. Owned by `dsl-aot.md` per `design/subsystems/README.md:35,153`. No new cross-cutting artifact; no bound contract touched. Coordinate `WriteLayout`/`MarkDirty` dirt semantics with the `reconciler-hooks`/`layout` owners; register nothing new; run `check-canon.ps1`.

**Why build-later, behind a parity gate, only if profiled.** Effort is **L**: the shipped `WriteColumns` is **not** the clean column-copy §5.3 imagined — it has interleaved side-effects that read **old** column state before overwrite and gate on cross-field conditions: BrushAnim cross-fade seeding reads `paint.Fill`/`paint.BorderColor` before write (`Reconciler.cs:1776-1794`), the static→identity transform hand-off keyed on `old` (`:1845-1848`), bound-channel guards (`!b.Fill.IsBound :1799`, Opacity `:1852`, Width `:1879`), the **deliberately unconditional Opacity re-assert** whose comment documents the 0→1 bug a value-gate caused (`:1850-1852`), Anim seeding (`:1895-1925`), image pin/unpin (`:2211-2216`), span-run AddRef/Release (`:2319-2325`). A naive per-field skip reintroduces documented bugs (ProgressRing IsActive 0→1, bound clobber, ranged-slider 14 px offset). A correct generator must model each prop's **full** side-effect set, then land **type-by-type behind a VerticalSlice parity gate** (diff masked-writer output against the hand path), starting with the pure column-copy props (Fill/Border/Corners/LayoutInput/Opacity) while leaving every animation/flag/handler/refcount block unconditional. **Do not build it on the strength of its memory/recorder claims** (both refuted); revisit only if a 10k-row recycle/storm profile shows `WriteColumns` CPU dominating frame time — the candidate offers no such measurement. The **id-guard** sub-part (WGPU0003) is worth folding in regardless (zero runtime cost, real maintainability) since ids are hand-written today (`Element.cs:25`).

---

## Suggested sequencing

A short ordered plan. The dependency spine is: **infrastructure → the two real edge/CPU wins → the deferred enablement work.**

1. **Reconcile `DepKey` canon first (no code yet).** Decide whether `reconciler-hooks.md §3.2/§3.3` moves to the shipped two-long `DepKey` shape or the code grows the `{DepKind,Bits}`+`FromStr`/`FromRef` shape. This gate blocks GEN-02 and is pure design/`check-canon.ps1` work. *Why first: it is free, unblocks the highest real-payoff generator, and prevents a generator targeting a moving surface.*

2. **Build the `DepKey` infrastructure** — `GcDepTable` + string interning + the `[InlineArray]` >4-arity overflow + the four missing anim-hook `ReadOnlySpan<DepKey>` overloads (`reconciler-hooks.md §3.3`). *Why second: GEN-02's emitted shape literally cannot compile without it; it is also reusable by any future capture-keyed work.*

3. **Ship GEN-02 (HookDeps lowering).** With (1)+(2) done it collapses to a clean M and delivers the real reconcile-edge GC relief (1 array + K boxes per dep-bearing hook per re-render). *This is the first generator that should actually exist.*

4. **In parallel, verify GEN-04's premise**, then build it if confirmed. Independent of the `DepKey` track; depends only on the `WGPU0010` purity analyzer. *Why parallelizable: different owner (`dsl-aot.md` purity), different surface (interceptor/codefix).* Bound the `Tok.*`-staleness hazard (theme-independent literals only, or invalidate on `Tok.Epoch`).

5. **GEN-17 (cold side-table slab)** after the pure-precompute items, behind a green VerticalSlice + `--screenshot` diff. *Why later: it is a correctness-sensitive data-structure migration touching `scene-memory` get-or-add / `FreeSubtree` ordering — highest blast radius of the survivors, and it removes cycles not bytes, so there is no GC emergency forcing it earlier.*

6. **Defer GEN-08 and GEN-COM to their consumers.** Build **GEN-08** as one unit with the `BrushDeriver`/T1/T2 work when those land (it provides the `Resolve(TokenId,ThemeKind)` indexer they need + the `BrushSpec→ColorF` reconciliation). Build **GEN-COM** when the macOS seam or a trimmed-binary-size target makes dropping TerraFX load-bearing. *Why last: both are footprint/enablement, not speed; building them before their consumers is premature.*

7. **GEN-01 only on evidence.** Fold its **id-guard (WGPU0003)** line-item into whichever Element-marker generator lands first (cheap, zero-risk). Hold the **DiffProps differ** itself until a 10k-row recycle profile shows `WriteColumns` CPU dominating; then build it type-by-type behind the parity gate. *Why gated on a profile: its two headline benefits are refuted, leaving an off-hot-path CPU saving with no measured demand.*

**Merges to track as single units** (per the critic): GEN-01 + GEN-03(id) + GEN-10 = **one** generator; GEN-05 + GEN-13 = **one** rejected idea; GEN-07 + GEN-08 + GEN-16 collapse into **one deferred theming-codegen workstream** anchored on `dsl-aot.md §2.5` + the theming token-table when the BrushDeriver lands.

---

## Gaps & unverified

Labeled honestly as **NOT yet verified** in this pass.

**Missing generator families the survey never considered (critic):**

- **COM-binding generator from `comabi.json` (the biggest omission).** None of the 16 original candidates considered it, despite being a full design-of-record (`com-interop.md`), a reserved scaffold (`Placeholder.cs:13-16`), and explicitly named in the brief as a trigger mechanism. Folded in above as **GEN-COM** (footprint/enablement, build-later). Its perf framing is honest: TerraFX already gives zero-alloc `calli`, so it is **not** a hot-path speed win.
- **Bound-binding / RowBind slab writer for VirtualList templates (unverified).** The maps repeatedly cite the bound-slot path (`Reconciler.cs:1206/1227`, "template runs ONCE per slot … never rebuilt") to **narrow** GEN-06 — but nobody asked whether a `[BoundTemplate]` generator over a `(item)=>Element` factory could emit the per-slot `Prop<T>`-to-column bind slab so **more** templates get the zero-rebuild recycle path instead of the allocating `RenderItem` path (`Reconciler.cs:1166`). **This is the one place a generator plausibly touches the genuinely-hot 10k-row recycle path, and it was never proposed.** Not verified — sketch only; worth a dedicated investigation.
- **Per-frame brush/gradient state-interpolation specialization in `SceneRecorder.Walk` (unverified).** The theming maps stopped at the reconcile-edge gradient *allocation* (GEN-05) and never looked at the **per-frame** gradient work the recorder does at phase 8: interpolating resting→hover→pressed stops by eased progress + probing 4+ sparse dicts per stateful node (`SceneStore.cs:108-111`; recorder reads `SceneRecorder.cs:403-406`). A `[StatefulGradient]` / 2-stop-elevation generator could fold the stop-lerp + pack the stateful slab. Not verified; overlaps GEN-17 (the dict-probe half) — assess together.

**Shaky claims on surviving candidates:**

- **GEN-02 impact may be optimistic.** "Medium" rests on favorable call-site framing. DepKey overloads already ship for the common hooks and authors hand-opt-in **today** (`Surfaces.cs:119`); the win is per-re-render bounded Gen0, never phases 6–13, and most hooks fire only on real dep change. Once the already-hand-optimized hot hooks are excluded, **the realized impact could be low rather than medium**. Not re-measured.
- **GEN-17 "medium/per-frame" rests on an unestablished profile.** The per-frame `TryGet` probes are confirmed to be on the hot path, but **no profile in the survey establishes they are a measured bottleneck**. The memory case is partly self-defeating (+~120 B/node ref columns) and covers only ~20 of ~30 tables. It removes cycles/cache pressure, not allocation.
- **GEN-01's residual CPU rationale is itself unverified.** With the memory and recorder claims refuted, the surviving "good CPU hygiene" justification has **no measurement** behind it — writes to cache-resident SoA slots on the off-hot-path reconcile edge. Even build-later is generous absent a recycle profile.
- **GEN-04 has no verdict at all.** Its confirm/refute pass was not run; impact is plausible-but-unverified (see its build-later detail).

**Correctly-absent (verified no generator lever — rightly not candidates):** Input/gesture dispatch (dense slot arrays + parent-walks, dict-free; `InputDispatcher.cs:3679-3771` HitTest is recursive column reads); Media decode (runtime concurrent worker pipeline behind the `IImageCodec` seam); Text shaping (already shape-cached + `StringTable`-interned); UIA/a11y (**unbuilt** — `Uia/Placeholder.cs` only); the `SceneRecorder` `switch(p.VisualKind)` (already a dense JIT switch); the SoA dense column accessors (`SceneStore.cs:431-435`, already optimal `ref` returns).

---

## Honest caveats

State plainly where a generator's benefit is marginal and **why** — the engine already wins on phases 6–13, and several "wins" evaporate on contact with the code.

1. **Nothing here touches phases 6–13.** Those are already 0-alloc and gated (`Program.cs` `HotPhaseAllocBytes`). Every surviving win is at the **reconcile edge** (bounded Gen0 the engine *explicitly accepts* — `CLAUDE.md` "bounded Gen0 at the reconcile edge"), at **startup/first-frame**, or in **steady-state CPU/cache** (cycles, not bytes). No generator can claim a per-frame-hot allocation win, because there is no per-frame-hot allocation to remove.

2. **The "highest-value" candidate is the most over-claimed.** GEN-01's pitch ("largest lever across the whole survey") does not survive the code: its **memory win is essentially nil** (dense-array column/handler stores and warm-dict touches all allocate 0 bytes; the allocating edge work is already field-gated or upstream of `WriteColumns`) and its **recorder-elision multiplier is false** (`SceneRecorder.Record` re-walks regardless of `PaintDirty`; frame elision is the `DrawListHash` gate, `AppHost.cs:1233-1234`). What remains is a modest off-hot-path CPU saving with an L-effort, bug-prone implementation and **no measured demand**.

3. **Token reads are already free — most theming generators have no target.** Solid-color tokens are static-field loads through one active-set pointer (`Tokens.cs:190-329`); **no dictionary, no string key, no per-read alloc, no runtime parse**. A theme swap is one pointer write + an `Epoch++` (`Tokens.cs:178-182`). So GEN-07 (work win, possibly a *regression* via a bounds-checked array load), GEN-13 (premise refuted — no per-read divides), and GEN-08's *hot-read* angle (`Cast`-index ≈ today's field load) are all marginal-to-zero. GEN-08 survives **only** as startup + enablement; GEN-05's real target is **three** allocating getters that a ~15-line hand-edit fixes without a generator.

4. **The "modifier storm" and several allocation hotspots are not present in this codebase.** Grep finds **zero** ≥2-method modifier chains in real `.cs` (GEN-14); the per-construction `TrackSize[]` GEN-12 targets is already hoisted/memoized app-side; GEN-06's scaling list path already runs each template once per slot; GEN-15's signal scan is over 1–4-element lists. Several candidates optimize costs that the app authors already engineered away.

5. **Generated-vs-hand-written is a real tradeoff for tiny targets.** Where a "generator" optimizes 3 properties in one team-owned file (GEN-05), or replaces an already-baked `static readonly` with another `static readonly` (GEN-13), or mirrors an already-tight blit-switch (GEN-09 — *negligible runtime, by admission*), the **maintenance + canon-registration cost of a Roslyn generator exceeds the win**. The honest verdict for those is a small in-place refactor, not codegen.

6. **A data-structure migration is not a precompute.** GEN-17 is the most valuable *idea* but it is a semantics-preserving migration (get-or-add, free-list, `FreeSubtree` ordering, `NodeFlags` first-insert side-effects) — categorically riskier than the pure-precompute generators, and its memory win is partly eaten by per-node ref columns. It earns its slot on **CPU/cache**, behind a green gate, after the cheaper wins.

7. **AOT/trim safety is necessary but not sufficient.** Every proposal emits plain ahead-of-time C# (incremental generators / interceptors over equatable models; data-section spans; `calli` thunks) with no reflection, no runtime IL/codegen, no dynamic loading — consistent with the two shipping generators. **But AOT-clean does not rescue a candidate with no runtime win** (GEN-16, GEN-09) or a canon conflict (GEN-16's parallel markers, GEN-18's duplicate of `DynamicTextKind`/`RefreshDynText` at `AppHost.cs:1657-1669`). Trim-safety was the floor, not the bar.

---

## Implementation status (landed)

Built on `feat/touchpad-scroll` per an explicit "full toolkit now, gated" directive (the user opted to build the complete set despite the verdicts above). The governing rule: **nothing is allowed to regress the 528 VerticalSlice gates** — so every generator that touches verified-green code is built **dormant** (triggers on a marker/manifest applied nowhere) or behind a **default-off opt-in marker**, and the one net-positive runtime change (GEN-05) is a hand-edit verified against the gates. Final state: full solution **0 errors**, VerticalSlice **ALL CHECKS PASSED (528)** with phases 6–13 zero-alloc, `check-canon.ps1` **exit 0**.

> **Update — all generators ENABLED (follow-up directive: "oneshot enable all… no customers, fine to just do it").** `DiffPropsGenerator` (GEN-01/10) is now **opt-out** — it fires on every `Element` record automatically (exclude one with `[FluentGpu.CodeGen.NoCodegen]`); `TokenSet` carries `[ThemeTokens]` (GEN-08/13); and `src/FluentGpu.Engine/_GeneratorEnablement.cs` applies the remaining marker triggers (GEN-02/03/04/06/07/09/11/12/14/15/17) once each so they all emit. `EmitCompilerGeneratedFiles=true` (in `src/Directory.Build.props`) writes the output to disk: **33 `.g.cs` files** under `FluentGpu.Engine/obj/<cfg>/<tfm>/generated/FluentGpu.SourceGen/…`. The emitted code is **additive** (new types in their own namespaces, not wired into the live reconciler/scene/hook paths), so the engine's runtime behavior is unchanged: full solution **0 errors**, VerticalSlice still **528 / zero-alloc green**. Wiring the generated `Diff`/`GcDepTable`/`ColdSlab`/etc. into the live paths remains the separate, risky migration the verdicts describe. Delete `_GeneratorEnablement.cs` (and the `[ThemeTokens]`/opt-out markers) to turn the inert ones back off.

| Item | Form landed | Active? | File |
|------|-------------|---------|------|
| Foundation | analyzer wired into `FluentGpu.Engine` + `[Element]`/`[Prop]`/`[Modifier]`/`[ThemeTokens]`/`[Factory]`/`[FastPath]` markers (internal, post-init) | yes | `SourceGen/Engine/ElementGenerator.cs` |
| **WGPU0003** | duplicate-`ElementTypeId` hard error — **verified firing** on a probe; silent on the 14 distinct ids | **yes (live)** | `ElementGenerator.cs` |
| **GEN-05** | 3 elevation-border gradients memoized per `Tok.Epoch` (the report's recommended hand-edit) | **yes (live)** | `Engine/Dsl/Tokens.cs` |
| GEN-02 step 1 | DepKey canon reconciled vs the shipped narrow v1 (fork decided; no code rewrite) | n/a (doc) | `design/subsystems/reconciler-hooks.md` |
| GEN-01 + GEN-10 | `DiffProps` bitmask differ + `RefEquals` per `[Element]` type — verified on a probe | dormant (no record annotated) | `Engine/DiffPropsGenerator.cs` |
| GEN-08 | `ThemeBlobGenerator`: `TokenId` enum + `Resolve(id,theme)` from `[ThemeTokens]` — verified on a probe | dormant | `Engine/ThemeBlobGenerator.cs` |
| GEN-COM | `ComInteropGenerator`: hand-vtable bindings from `*.comabi.json` AdditionalFiles | dormant (no manifest) | `Interop/ComInteropGenerator.cs` |
| GEN-03/06/07/09/11/12/13/14/15/18 | the rejected set, each a real generator on a marker; verdict + evidence inline | dormant | `Engine/RejectedSetGenerators.cs` |
| GEN-16 | subsumed by the single `FluentGpu.CodeGen` marker namespace + shared `Gen` toolkit | n/a | `RejectedSetGenerators.cs` |
| GEN-02 / GEN-17 / GEN-04 | gated migrations — emit `GcDepTable`/`DepInline4`/`DepDeps`, `ColdSlab<T>`, `StaticSubtreeCache` only under an `[Enable…]` opt-in marker (applied nowhere); both emission + dormancy **verified on a probe** | default-off | `Engine/GatedMigrationGenerators.cs` |
| WGPU/FGCOM family | `WGPU0003` implemented; the rest (`WGPU0001/0005/0006/0009/0010/0012…`, `FGCOM0001-0008`) are **designed-but-deferred** — the dataflow-heavy rules (rules-of-hooks, ref-struct capture, ComPtr lifetime) need interprocedural analysis and would false-positive on existing code; activating them is follow-up. | partial | — |

**Net effect on the shipping engine:** one verified perf hand-edit (GEN-05) + one live build-time safety guard (WGPU0003). Everything else is present-but-inert infrastructure that can be activated (annotate a record / check in a manifest / flip an `[Enable…]` marker) when its consumer exists or its risky live migration is undertaken — exactly the deferral the verdicts above recommend, now with the machinery in place.