# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

`fluent-gpu` is a from-scratch, near-zero-allocation, NativeAOT, GPU-rendered UI engine for .NET 10 — the Reactor/React programming model (immutable `Element` records + `Component` + hooks + a keyed reconciler) over a signals-first reactive core and a custom Direct3D 12 + DirectWrite + DirectComposition renderer, behind a swappable PAL/RHI/Text seam (macOS-ready).

**The engine exists: it's in `src/` (8 projects — see below) and it builds, runs, and passes its gates.** The `design/` corpus is no longer the code — it is the **canon authority** for cross-cutting contracts (the precedence rules below still bind the implementation). Usage/how-to lives in `docs/` (start at `docs/guide/README.md`); the architecture spec lives in `design/`. Read `README.md`, then for design work `design/README.md` + `design/SPEC-INDEX.md`, before changing anything.

**The `src/` layout (4 libraries + 2 Roslyn analyzers + 2 exes, in `src/FluentGpu.slnx`):**
- `FluentGpu.Engine` — the portable engine core (`RootNamespace=FluentGpu`, no NuGet). One folder per subsystem, namespaces unchanged: `Foundation/` (incl. `Foundation/Signals/`), `Seams/{Rhi,Pal,Text}/`, `Scene/`, `Render/`, `Layout/`, `Dsl/`, `Hooks/`, `Reconciler/`, `Animation/`, `Input/`, `Media/`, `Hosting/`, `Headless/{Rhi,Pal,Text}/`.
- `FluentGpu.Controls` — the portable control kit (refs `Engine` only; **TerraFX-free** — the zero-alloc harness and the macOS port consume it unchanged).
- `FluentGpu.Windows` — the swappable Windows backend (refs `Engine` + the one `TerraFX.Interop.Windows` PackageReference). Folders: `Interop/`, `Pal/`, `D3D12/`, `DirectWrite/`, `Wic/`, `Uia/`.
- `FluentGpu.WindowsApi` — OS-services scaffold (`Notifications/`, `Credentials/`, `Packaging/`, `Activation/`), refs `Engine`. MSIX packaging is app-side (`.wapproj`), not here.
- Analyzers: `FluentGpu.SourceGen`, `FluentGpu.Interop.SourceGen` (netstandard2.0). Exes: `FluentGpu.VerticalSlice` (the `PublishAot` validation harness; refs `Engine`+`Controls`; **its transitive closure must stay TerraFX-free** — the portability guard) and `FluentGpu.WindowsApp` (the WinExe gallery / composition root; refs `Engine`+`Controls`+`Windows`).

## Commands

```powershell
dotnet build src/FluentGpu.slnx                              # the canonical build (all 8 projects); must be clean
dotnet run --project src/FluentGpu.VerticalSlice            # the validation suite; expect "ALL CHECKS PASSED" (incl. zero-alloc gates)
dotnet run --project src/FluentGpu.WindowsApp              # the gallery (composition root)
dotnet run --project src/FluentGpu.WindowsApp -- --screenshot <path>   # render a deterministic scene to a PNG (visual-diff loop)
powershell -File design\check-canon.ps1                   # design-time drift gate — run AFTER editing any design/ doc (exit 0 = clean)
```

The VerticalSlice harness (`src/FluentGpu.VerticalSlice/Program.cs`) runs ~60 cross-seam golden checks headlessly (no GPU/window) and enforces the alloc tripwire (`GC.GetAllocatedBytesForCurrentThread()` delta == 0 per hot phase) on the headless `Rhi.Headless`/`Pal.Headless` seams; GPU pixels are the separate `--screenshot` check.

The canon gate fails if a known-stale/superseded token reappears anywhere in the **live** `design/` tree (`design/archive/` is excluded). To intentionally mention a superseded form in live prose, put `<!-- canon-allow: reason -->` on that line. The canonical values it protects are in `design/SPEC-INDEX.md`; superseding a value means adding a rule to `check-canon.ps1` **and** moving the old doc to `design/archive/`.

The build/GC baseline lives in `src/Directory.Build.props` (per `design/dotnet10-csharp14-zero-alloc.md` §1: target `net10.0`, `LangVersion 14`, `PublishAot`, `TrimMode full`, Workstation+Concurrent GC, `GCSettings.SustainedLowLatency`). The full gate regime — alloc tripwire, golden-image diff, headless seams, COM-leak gate, the seam race gate — is specified in `design/subsystems/validation.md`.

## Canon & ownership discipline (the most important working rule)

The corpus is large and heavily cross-referenced; consistency is enforced by a strict single-owner model. Honor it — the #1 review finding is the same artifact (an opcode shape, a column, a seam) defined two ways in two docs.

- **`design/SPEC-INDEX.md` is the precedence authority.** When two docs disagree on a cross-cutting contract, §2 gives the canonical value and the ONE owning doc. Consult it before any cross-cutting edit.
- **Every shared artifact has exactly one owner.** `design/subsystems/README.md` holds the contract-ownership map: every DrawList opcode, RHI method, PAL seam, hook, source generator, scene column, and assembly → its one authoritative doc. Define an artifact in its owner; everywhere else **reference it** (never restate a struct shape or enum — that drift is what the canon gate and reviews catch).
- **Adding a cross-cutting contract** ⇒ register it in `SPEC-INDEX.md` §2 + the subsystems ownership map, then run `check-canon.ps1`.
- **Binding contracts not to silently re-litigate** (all in `SPEC-INDEX.md` / `foundations.md`): `Handle = {u32 index, u32 gen}`; the render-thread-seam threading model (UI thread reconciles+layouts → `PUBLISH(13a)` → render thread records/submits/presents; **the render thread owns every `ComPtr`**; consume-gated quarantine = `RenderInFlightDepth + 1`); COM = generated-from-`*.comabi.json` hand-vtable on the hot path + `[GeneratedComInterface]` for cold COM, **no hot-path `ComWrappers`**; clean-span reuse valid IFF handle `IsLive` ∧ realization content-epoch unchanged ∧ baked-geometry hash unchanged (single `Mutate()` chokepoint); color = `BGRA8_UNORM` buffer / `_UNORM_SRGB` RTV / linear blend / premultiplied / text gamma exception; `DepKey` = 16B scalar + `GcDepTable` side-buffer for reference deps; `ChunkedArena` (native-backed, no LOH cliff); **0 managed allocations in frame phases 6–13**.

## Honesty discipline (do not soften or inflate)

The corpus deliberately does not overclaim, and edits must preserve that. These are **truths, not gaps to "fix"**: "**near-zero-allocation**" (not "zero" — zero per-frame managed alloc only on phases 6–13; bounded Gen0 at the render/reconcile edge); slowness is "**decoupled, not invincible**" (a sustained GPU stall still bounds back to the UI thread); "**production safety == CI coverage**" (debug guards are `[Conditional]`-erased from the shipping AOT binary); and the build *order* ships single-thread-correct first, then flips parallelism behind a green race gate. `design/winui-painpoints-assessment.md` is the honest scorecard — don't dress it up. As of the latest pass, **all `design/core-fundamentals-gap-analysis.md` items (P1–P9, L1–L14) are folded into the CORE design with no v2 deferral**; the only residuals are the physical limits above.

## Architecture at a glance

The big picture lives across several docs (it can't be inferred from one):

- `design/architecture-spec.md` — the end-to-end engine: the **13-phase frame loop**, the SoA `SceneStore` (the retained RenderNode tree the reconciler patches), the DrawList POD command stream, the assembly graph (the spec's original 18-assembly decomposition is now physically consolidated into the 8 `src/` projects above — same code, fewer csproj boundaries), the click→repaint data-flow walkthrough, and the minimum vertical slice (the acceptance test).
- `design/foundations.md` — the shared vocabulary (handles, the four allocators, scene columns, the PAL/RHI/Text seams, frame lifecycle).
- `design/subsystems/` — the standalone subsystem designs; start at `subsystems/README.md` (index + ownership map). Core engine: `pal-rhi`, `scene-memory`, `layout`, `gpu-renderer`, `text`, `reconciler-hooks`, `dsl-aot`, `input-a11y`. Features: `media-pipeline`, `theming`, `virtualization`, `backdrop-effects-animation`, `window-backdrop-mica`, `controls`, `devtools`. Hardening: `threading-render-seam` (the canonical threading model), `com-interop`, `validation`.
- `design/hardened-v1-plan.md` — how the parallel render-thread seam + generated/confined COM + the validation spine fold into v1 safe-by-construction.
- `design/app-requirements-waveemusic.md` — the driving app (a Spotify desktop client, `C:\WAVEE\WaveeMusic`) the engine must run: the concrete workload (10k+ virtualized lists, album-art residency, video, synced lyrics, Mica, dynamic accent color) that every subsystem is sized against.
- `design/budgets.md` (resource/eviction budgets) and `design/macos-debt-ledger.md` (the Metal/CoreText/Cocoa cross-platform plan).

**The core bet** (`README.md`): keep Reactor's host-agnostic programming model; replace WinUI 3's heavy C++ XAML/Composition core with our own GPU renderer + retained SoA tree behind a swappable seam. Studied/reused reference repos sit alongside this one under `C:\WAVEE\`: **`microsoft-ui-reactor`** (the Element/Component/hooks model + the pure-C# Yoga port we keep), **`ComputeSharp`** (DX12/DXGI COM bindings, `ComPtr<T>`, the C#→HLSL transpiler, NativeAOT patterns — reused/vendored), and **`microsoft-ui-xaml`** (studied for the rendering/text/layout architecture and what to avoid).

## Working in the code

The engine is built and the minimum vertical slice (`architecture-spec.md §11`: window → GPU clear → rounded rect → one text run → flex layout → reconciler + `UseState` → clickable button) is the **standing acceptance test**, not a to-do — it lives as `src/FluentGpu.VerticalSlice` and enforces 0 per-frame managed alloc on phases 6–13, headlessly, every run. For *how to build UI* and *where to change what* (the file map), use the `fluentgpu` skill / `docs/guide/`. When you change a subsystem, the loop is: edit in `src/`, then `dotnet build src/FluentGpu.slnx` (clean) + `dotnet run --project src/FluentGpu.VerticalSlice` ("ALL CHECKS PASSED", zero-alloc gates green) as evidence before claiming done; `--screenshot` for visual fidelity. Touching a cross-cutting contract still means reconciling the owning `design/` doc and running `check-canon.ps1` — design stays the canon authority even though the implementation now exists.

Two invariants the structure encodes, easy to break: keep `FluentGpu.Controls` (and the whole `FluentGpu.VerticalSlice` transitive closure) **TerraFX-free** — TerraFX enters only through `FluentGpu.Windows`; and keep code in its subsystem folder so the namespace stays its historical `FluentGpu.*` (the folders carry the old project names verbatim).
