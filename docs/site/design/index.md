# Design canon (the architecture source of truth)

This page is a **bridge**, not a spec. The architecture canon — every binding contract, DrawList opcode shape, seam
definition, scene column, hook, and threading rule — lives in the `design/` tree at the repository root, **not** on this
site. This page tells you what is in there, the order to read it in, who owns what, and the one rule for changing a
canonical value. It deliberately restates none of those contracts: it links to them.

The split is the whole point. These DocFX pages are the **working developer view** — how to use the engine
([app authors](../app-authors/index.md)) and where to change its internals
([engine contributors](../engine-contributors/index.md)). The `design/` corpus is the **authority**. When a usage page
on this site and a design doc disagree, **the design doc wins**, and the usage page is the bug. When two design docs
disagree, [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) wins. That ladder is the subject of this page.

> [!NOTE]
> The links below point into the repository tree. The `design/` corpus is read **in the repo** — it is not built into
> this site (the site builds only `docs/site/**` plus the generated API reference; see [`docfx.json`](../docfx.json)).
> Follow these links on GitHub or in your local checkout.

> [!IMPORTANT]
> If you are *building an app*, you almost never need this page — start at
> [Building apps with FluentGpu](../app-authors/index.md). This page is for **engine contributors** changing internals,
> and for anyone who needs to know which doc is authoritative for a given contract before they touch it.

## What lives in `design/` vs here (canonical contracts vs usage docs)

Two trees, two jobs, one direction of authority.

| | `design/` (repo root) | `docs/site/**` (this site) + `docs/guide/**` |
|---|---|---|
| **What it is** | The architecture **canon** — binding contracts, struct/opcode shapes, seam definitions, scene columns, the threading model, the 18 subsystem designs-of-record. | The **working view** — task-oriented, source-grounded usage and contributor docs. |
| **Authoritative for** | Cross-cutting contracts and every shared artifact's *single owner*. | Nothing canonical. It *cites* the canon (and the source) rather than re-litigating it. |
| **Form** | Markdown specs + the [`check-canon.ps1`](../../../design/check-canon.ps1) drift gate. Canon-gated. | DocFX site pages + the developer guide. Verified by the headless harness, not the canon gate. |
| **When they disagree** | **Wins.** A usage doc that contradicts it is wrong. | Loses to `design/`; loses to the source. The as-built reactive model in particular is owned by [`reconciler-hooks.md` §0bis](../../../design/subsystems/reconciler-hooks.md). |
| **Edit discipline** | Single-owner: define an artifact in its owner; everywhere else *reference* it. Adding a contract means registering it in `SPEC-INDEX.md` §2 **and** the ownership map first. | Same single-owner spirit: link into the canon, do not duplicate a struct shape or an enum. |

The rule that keeps this honest is **single-owner**: every shared artifact (an opcode shape, a scene column, a seam, a
hook, a source generator, an assembly) is defined in exactly **one** design doc and referenced everywhere else. The #1
review finding on this codebase, every time, is the same artifact defined two ways in two docs — which is exactly the
drift the canon gate and reviews catch. Honor it on both sides of the split.

## Read order: README → SPEC-INDEX → subsystems/README

You do **not** read the whole corpus before a change. The entry path is three docs, then the one subsystem
design-of-record for the area you are touching:

1. **[`design/README.md`](../../../design/README.md)** — the digestible full overview. The stack, the principles, the
   reuse strategy, the end-to-end Button-click data flow, and the minimum vertical slice. Read this first; it is the map.
2. **[`design/SPEC-INDEX.md`](../../../design/SPEC-INDEX.md)** — the **precedence authority**. For every cross-cutting
   contract it names the *one* owning doc and states the current canonical value inline, so you never discover a
   supersession by reading three docs. Consult it before any cross-cutting edit.
3. **[`design/subsystems/README.md`](../../../design/subsystems/README.md)** — the **contract-ownership map**: every
   DrawList opcode, RHI method, PAL seam, hook, source generator, scene column, and assembly maps to its one
   authoritative doc. It also carries the implementer's 15-step reading order (§3) if you want the full tour.

Then go deep in the **owning subsystem doc** for what you are changing — and only that one. The engine-contributor pages
on this site link out to the owning subsystem doc from each topic, so you can navigate from "the file I'm editing" to
"the contract that governs it" without reading everything.

## The precedence ladder (SPEC-INDEX is the authority when two docs disagree)

The corpus is large (~26 docs, ~16k lines) and several docs each historically called themselves "authoritative" or
"design-of-record." [`SPEC-INDEX.md` §1](../../../design/SPEC-INDEX.md) resolves that with a strict ladder. Each
contract in its §2 table has **exactly one owner** — that owner wins regardless of generic order. For anything *not* in
the table, conflicts resolve in this order (higher wins):

1. **`SPEC-INDEX.md`** — the canonical value.
2. **`hardened-v1-plan.md`** — threading, safety posture, build order. Its §7 amendment checklist is canonical *even
   where* `architecture-spec.md` / `foundations.md` still print the lean single-thread form (those carry a
   `⊳ Canonical threading model` banner pointing here).
3. **`dotnet10-csharp14-zero-alloc.md` §4** — the COM ruling.
4. **The subsystem design-of-record docs** — deep design *within* the rulings above.
5. **`architecture-spec.md`** — the end-to-end integrating narrative; canonical owner of the handle byte layout, the
   color/DPI contract, and the frame-phase *shape*.
6. **`foundations.md`** — the shared vocabulary. Where it conflicts with anything above, the above wins.
7. **`README.md`** — the digest. Never authoritative over a deeper doc.

The practical consequence: a doc can be locally "out of date" on purpose. The single-thread 13-phase loop printed in
`architecture-spec.md` §4.8 and `foundations.md` §6 is **build-order step 1**, not the shipping topology — the canonical
model is the render-thread seam in `hardened-v1-plan.md` §2. Those older sections carry the `⊳` banner instead of being
rewritten, and `SPEC-INDEX.md` is what tells you which form is live. Trust the ladder, not the nearest paragraph.

## The contract-ownership map (every opcode/seam/hook/column has one owner)

[`subsystems/README.md` §2](../../../design/subsystems/README.md) is the **ownership map** — the operational half of the
single-owner rule. It assigns every shared artifact to its one authority doc, in seven tables:

- **§2.1 DrawList opcodes** — the *encoding framework* (the `DrawCmd` header, the byte arenas, the parallel `ulong[]`
  sortkey array, the opcode enum, the clean-span+epoch rule) is owned by
  [`scene-memory.md`](../../../design/subsystems/scene-memory.md); each opcode's *payload struct shape* is owned by
  [`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md). A few opcodes split emit/consume across two docs
  (e.g. `DrawGlyphRunCmd` is *emitted* by [`text.md`](../../../design/subsystems/text.md), *consumed* by
  `gpu-renderer.md`).
- **§2.2 RHI methods** and **§2.3 PAL seams** — the seam shapes and the leaf internals (owned by
  [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md)), with their consumers cross-listed.
- **§2.4 Hooks** — every hook (the signals core `UseSignal`/`UseComputed`, the React-shaped `UseState`/`UseEffect`/…,
  and the feature hooks) to its semantics owner.
- **§2.5 Source generators & analyzers** — `ElementTypeId`, modifiers, `DiffProps`, `HookDeps`, theme blobs, the COM
  generators, the `WGPU####` analyzers.
- **§2.6 New assemblies** and **§2.7 other cross-cutting artifacts** — including the SoA column layout, the color
  contract, the lane/update-queue, the Suspense boundary, the external-store contract, and the footprint ratchet.

Why it matters for an editor: **change the artifact in its owner, reference it everywhere else.** If you need to change
a DrawList opcode's payload, you change it in `gpu-renderer.md` and the enum registration in `scene-memory.md` — not in
the three docs that *use* it. Restating a struct shape or an enum anywhere else is the drift the gate is built to catch.

The code-side mirror of this map (subsystem → the one `src/` **file** that owns it, with the `src/` types) lives on this
site at the [Contributor Map](../engine-contributors/contributor-map.md). Use both: the ownership map routes a
*contract* to its owning doc; the contributor map routes a *code change* to its owning file.

## The big-picture docs (the parts you can't infer from one subsystem)

Some of the architecture only exists across several docs — it can't be read out of any single subsystem. Start here for
the whole-engine view:

- **[`architecture-spec.md`](../../../design/architecture-spec.md)** — the exhaustive, authoritative spec: the 13-phase
  frame loop, the SoA `SceneStore` (the retained RenderNode tree the reconciler patches), the DrawList POD command
  stream, the assembly graph, the click→repaint walkthrough, the risk register, and the minimum vertical slice (which
  doubles as the architecture's acceptance test). Canonical owner of the handle byte layout, the color/DPI contract, and
  the frame-phase shape.
- **[`foundations.md`](../../../design/foundations.md)** — the shared vocabulary everything else assumes: the
  generational `Handle` (`{u32 Index, u32 Gen}`), the four allocators (`SlabAllocator`/`ArenaAllocator`/`ObjectPool`/
  `HandleTable`), `StringId` interning, the `SceneStore` SoA columns, the PAL/RHI/Text seams, the frame lifecycle, and
  the acyclic-assembly invariants. Nothing below it makes sense without it.
- **[`hardened-v1-plan.md`](../../../design/hardened-v1-plan.md)** — how the parallel render-thread seam, generated-and-
  confined COM, vetted tessellation, the no-cliff arenas, and the validation spine fold into v1 *safe-by-construction*.
  It owns the **canonical threading model** (§2) and carries the honest safety-by-construction ledger (§3) and the build
  order that ships single-thread-correct first and flips parallelism on only behind a green race gate (§6).
- **[`budgets.md`](../../../design/budgets.md)** — the consolidated runtime resource budgets: every native/GPU/bandwidth
  budget, eviction policy, and failure behavior in one table.
- **[`macos-debt-ledger.md`](../../../design/macos-debt-ledger.md)** — the consolidated cross-platform debt: every
  Windows-specific decision, its macOS (Metal/CoreText/Cocoa) plan, and a Designed/Deferred/Unaddressed status.

Two more carry the project's *honesty discipline* and are worth knowing exist:
[`winui-painpoints-assessment.md`](../../../design/winui-painpoints-assessment.md) is the honest scorecard for "does
this actually fix WinUI's GC pressure / slow UI thread / slowness?", and
[`core-fundamentals-gap-analysis.md`](../../../design/core-fundamentals-gap-analysis.md) tracks what the core was missing
versus React/Flutter/Compose/SwiftUI/Solid — now all folded into CORE with no v2 deferral. The driving workload the whole
engine is sized against is in [`app-requirements-waveemusic.md`](../../../design/app-requirements-waveemusic.md).

## The subsystem design-of-record docs

The per-subsystem designs live under [`design/subsystems/`](../../../design/subsystems/README.md) — each one full,
current, cross-referenced, and the **sole authority** for its area (the `§2` ownership map decides exactly which area).
The authoritative listing, one line per doc, is the table in
[`subsystems/README.md` §1](../../../design/subsystems/README.md) — read that for the canonical set and each doc's
one-line charter (the corpus's own digest gives a slightly different round number in a couple of places, so trust the
table, not a count). Grouped by role:

**Core engine** — the pipeline the harness exercises end to end:

- [`pal-rhi.md`](../../../design/subsystems/pal-rhi.md) — the PAL/RHI seams, the `Rhi.D3D12` and `Pal.Windows` leaves,
  the DXGI flip-model + multi-visual DComp present tree, and the staging/bucket-pool mechanism.
- [`scene-memory.md`](../../../design/subsystems/scene-memory.md) — the SoA `SceneStore`, the DrawList encoding
  framework, the opcode enum, the three dirty axes + arena worklist, and the clean-span+epoch rule.
- [`layout.md`](../../../design/subsystems/layout.md) — the incremental Yoga/Flex + Grid engines, the measure/arrange
  passes, the scroll-ownership split, and the virtualization layout participant.
- [`gpu-renderer.md`](../../../design/subsystems/gpu-renderer.md) — the batched 2D renderer: every opcode's payload
  shape, the SortKey layout + batch-break rules, the HLSL shader set, the color contract, the clip/AA ladder.
- [`text.md`](../../../design/subsystems/text.md) — itemize/shape/font/raster/atlas/layout, `GlyphKey`/`PackedGlyph`,
  `DrawGlyphRunCmd` emission, the R8+BGRA atlas + eviction, and the DWrite leaf.
- [`reconciler-hooks.md`](../../../design/subsystems/reconciler-hooks.md) — the reconciler + keyed-LIS child diff, the
  hook cells, `DepKey`/`GcDepTable`, effect timing, the 3-signal memo skip, **the `Prop<T>` reactive prop surface**, and
  the as-built signals-first model (§0bis). This is the as-built reactivity owner the site's usage docs defer to.
- [`dsl-aot.md`](../../../design/subsystems/dsl-aot.md) — the `Element` record + modifier surface, the source generators,
  the `WgpuAnalyzer`, and the root AOT/build baseline + footprint ratchet.
- [`input-a11y.md`](../../../design/subsystems/input-a11y.md) — input/focus/IME/hit-testing/UIA, the gesture arena, the
  overlay/portal manager, the cursor seam, and the `DrawFocusRingCmd` emission.

**Feature layers** (WaveeMusic-driven): [`media-pipeline.md`](../../../design/subsystems/media-pipeline.md),
[`theming.md`](../../../design/subsystems/theming.md), [`virtualization.md`](../../../design/subsystems/virtualization.md),
[`backdrop-effects-animation.md`](../../../design/subsystems/backdrop-effects-animation.md), and the host-window backdrop
in [`window-backdrop-mica.md`](../../../design/subsystems/window-backdrop-mica.md).

**Hardening**: [`threading-render-seam.md`](../../../design/subsystems/threading-render-seam.md) (the canonical 3-thread
topology), [`com-interop.md`](../../../design/subsystems/com-interop.md) (generated/confined/gated COM), and
[`validation.md`](../../../design/subsystems/validation.md) (the spikes + per-PR gates + the public `FluentGpu.Testing`
harness).

**Control kit + devtools**: [`controls.md`](../../../design/subsystems/controls.md) (the accessible-by-default control
kit — pure composition, adds no opcode/column/seam/hook) and
[`devtools.md`](../../../design/subsystems/devtools.md) (the dev-only, release-trimmed inspector).

## The binding contracts not to re-litigate

A handful of decisions are **load-bearing and settled** — they are in [`SPEC-INDEX.md` §2](../../../design/SPEC-INDEX.md)
with their one owner, and the [`check-canon.ps1`](../../../design/check-canon.ps1) gate fails the build if a superseded
form of one reappears in the live tree. Do not silently change these in a subsystem doc; change them the canonical way
(next section). The current canonical values (read the SPEC-INDEX row for the exact wording and the owner):

- **Handle layout** — `Handle = {u32 Index, u32 Gen}`, 8 bytes; generation is **32-bit**, bumped on alloc *and* free
  (ABA defense). Kind is **not** packed into the bits — it lives on the zero-cost typed wrapper as a
  `[Conditional("DEBUG")]` assert. (The old `{index32, gen24, kind8}` form is superseded.) <!-- canon-allow: documents the supersession itself --> *Owner:*
  `architecture-spec.md` §4.1.
- **Tiered COM dispatch** — hand-vtable `calli` on the per-frame hot path + any CCW invoked inside the frame loop;
  `[GeneratedComInterface]`/`[GeneratedComClass]` (source-gen, AOT-recommended) for *all* cold/warm COM (UIA, TSF, OLE,
  DWrite setup). `ComWrappers` is rejected on the **hot path only**. (The blanket "no ComWrappers anywhere / hand-vtable
  both directions" rule is superseded.) *Owner:* `dotnet10-csharp14-zero-alloc.md` §4 + `com-interop.md`.
- **Threading / render-thread seam** — a **PUBLISH(13a)** phase splits the loop; record/batch/submit/present run on a
  dedicated render thread reading an immutable `SceneFrame`; **the render thread owns every `ComPtr`** and the UI thread
  owns no GPU object. The single-thread 13-phase loop is build-order step 1, not the shipping topology. *Owner:*
  `hardened-v1-plan.md` §2 + `threading-render-seam.md` §14.
- **Quarantine constant** — `QUARANTINE = RenderInFlightDepth` (compile-asserted, **consume-gated**: reclaim a freed
  slot only when `_lastConsumedSeq > freedSeq`), with `+1` slack. `0` in single-thread step 1. (The literal `2` is
  superseded.) *Owner:* `threading-render-seam.md`.
- **`DepKey`** — a **pure-scalar blittable 16-byte struct**; GC-ref deps go through a side `GcDepTable` compared by
  `ReferenceEquals`. (A `[FieldOffset]` union overlapping a GC ref with a scalar is **illegal CLR layout** —
  `TypeLoadException`.) *Owner:* `reconciler-hooks.md`.
- **Color / coordinate / DPI** — brush color = straight-alpha sRGB `float4` → renderer converts to linear-premultiplied
  at shader input; swapchain `BGRA8_UNORM` buffer + `BGRA8_UNORM_SRGB` RTV (blend/resolve linear, hardware sRGB on
  write), output premultiplied; **text gamma is a deliberate exception**; DPI applied once at layout→world; `Bounds` is
  node-LOCAL. *Owner:* `foundations.md` P8 + `architecture-spec.md` §1.3 bet 4 (color owner: `gpu-renderer.md`).
- **Zero-alloc phases** — **0 managed allocations in the per-frame paint phases (6–13)**; clean-span reuse is valid iff
  every referenced handle `IsLive` ∧ realization content-epoch unchanged ∧ baked-geometry hash unchanged, through the
  single `Mutate()` chokepoint. *Owners:* `dsl-aot.md` (budgets) + `scene-memory.md`/`gpu-renderer.md`/`reconciler-hooks.md`
  (clean-span rule).
- **`Prop<T>` reactive prop surface** — exactly **one** `Prop<T>` per bindable channel (e.g. `BoxEl.Transform/Opacity/
  Fill`, `TextEl.Text/Color`, `ImageEl.Source/Placeholder`), accepting a static `T`, a `Func<T>` thunk (`Prop.Of(...)`
  for inline lambdas), or a concrete signal; bind wiring is **mount-only**. (The dual static + `*Bind` surface is
  superseded — a `*Bind` spelling reappearing live is a gate violation.) *Owner:* `reconciler-hooks.md` §0bis +
  `dsl-aot.md`.

The matching **honesty discipline** is part of the canon and edits must preserve it — these are *truths, not gaps to
"fix"*: it is **near-zero-allocation** (not "zero" — zero per-frame managed allocation only on phases 6–13, bounded Gen0
at the reconcile/render edge); slowness is **decoupled, not invincible** (a sustained GPU stall still bounds back to the
UI thread); and **production safety == CI coverage** (every debug guard is `[Conditional]`-erased from the shipping
NativeAOT binary, so a hazard not under a green gate is unguarded at runtime). Do not dress up
[`winui-painpoints-assessment.md`](../../../design/winui-painpoints-assessment.md).

## How to change a canonical value (SPEC-INDEX first, then the owner, then the gate)

Changing a settled contract is a deliberate, ordered operation — not an in-place edit of whichever doc you happened to
have open. The sequence (from [`SPEC-INDEX.md` §4](../../../design/SPEC-INDEX.md) and the project working rules):

1. **Edit [`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) §2 first** — update the canonical value in the owning row. This
   is the value of record; nothing else is authoritative until this changes.
2. **Then edit the owning doc** named in that row (and only that doc's definition of the artifact — leave the references
   elsewhere as references).
3. **If you are superseding a value**, also add a rule to [`check-canon.ps1`](../../../design/check-canon.ps1) that
   fails on the *old* token, and **move the superseded doc to [`design/archive/`](../../../design/archive/)** (the gate
   excludes `archive/`). To intentionally mention a superseded form in live prose (e.g. to explain the correction), put
   `<!-- canon-allow: <reason> -->` on that line.
4. **Adding a *new* cross-cutting contract** (not changing an existing one) means registering a §2 row with its single
   owner **and** adding it to the [ownership map](../../../design/subsystems/README.md) *before* two docs can disagree
   about it.
5. **Run the drift gate** and confirm it exits 0:

   ```powershell
   powershell -File design/check-canon.ps1                  # exit 0 = clean   (or: pwsh design/check-canon.ps1)
   ```

   It fails the build if a known-stale/superseded token reappears anywhere in the **live** design tree
   (`design/archive/` is excluded). It is design-time only — it does not touch `src/`.

Two scopes, two gates, kept distinct. The canon gate above runs only for **`design/`** edits. A **source** change is
verified by the headless harness, not the canon gate:

```bash
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # expect: ALL CHECKS PASSED
```

Most contributions touch one or the other; a change that edits both runs both. The docs-side half of the single-owner
discipline (how *these* pages cite the canon instead of duplicating it) is described in
[Contributing to the Docs](../docs-contributing.md).

---

**Next:** [`design/README.md`](../../../design/README.md) to start the corpus ·
[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) for the precedence ladder + canonical values ·
[`subsystems/README.md`](../../../design/subsystems/README.md) for the contract-ownership map and the implementer
reading order · [Contributor Map](../engine-contributors/contributor-map.md) for the code-side
subsystem → owning-`src/`-file routing · [Engine Contributors](../engine-contributors/index.md) for the working
internals walkthrough.
