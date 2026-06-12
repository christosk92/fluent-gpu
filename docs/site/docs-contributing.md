# Contributing to the docs

This page is the rulebook for editing **these** docs — the DocFX conceptual site under `docs/site/`. It is the docs-side
half of the same single-owner discipline the engine enforces in code: usage prose lives here, architecture canon lives
in `design/`, and nothing on this site restates a canonical contract. Read it before you add or edit a page; the rules
are few but load-bearing, and each one exists because the alternative produced drift.

The short version:

- **Write usage docs here; never move canon out of `design/`.**
- **Link to a canonical value — never copy it.**
- **A control page cites both the control source and the gallery snippet that runs it.**
- **Every code sample mirrors the real surface in `src/FluentGpu.*` — open the file and copy the names.**
- **Two checks before you commit: the harness if you touched samples, `check-canon.ps1` if you touched `design/`.**

The rest of this page is the *why* behind each.

---

## The source-of-truth rule: usage docs are DocFX pages, architecture canon stays in `design/`

There are two doc trees in this repository and they have different jobs. Keep them separate.

- **`docs/site/`** (this site) is the **working developer view**: how to build apps, how to change the engine, where each
  thing lives, how to verify. It is task-oriented and source-grounded. It is *built* into a static site by DocFX.
- **`design/`** (the design corpus, at the repository root) is the **architecture source of truth**: every binding
  contract, opcode shape, seam definition, scene column, threading rule, and budget. It is canon-gated and is **not**
  built into this site — it is read in the repo or on GitHub.

The precedence is explicit and one-directional: **when a usage page and a design doc disagree, the design doc wins.**
[`design/SPEC-INDEX.md`](../../design/SPEC-INDEX.md) is the precedence authority (it names the one owning doc and the
current canonical value for every cross-cutting contract), and [`design/subsystems/README.md`](../../design/subsystems/README.md)
is the contract-ownership map. Both are mirrored from the engine's `CLAUDE.md` working rules — the docs do not get a
softer standard.

The practical rule that falls out: **never move canonical material out of `design/` into a usage page, and never let a
usage page become the only place a contract is written down.** If you find yourself explaining *what the contract is*
(the exact handle byte layout, the DrawList opcode enum, the 13-phase frame order), you are writing in the wrong tree —
that belongs in its owning design doc, and the usage page should link to it. A usage page explains *how to use* the
contract; the design doc *defines* it. The [Design canon page](./design/index.md) on this site says the same thing from
the reader's side, and the [site home page](./index.md) frames the whole "two trees, design wins" relationship.

---

## Never duplicate a canonical value — link to the SPEC-INDEX row or its owning subsystem doc

This is the rule that prevents the single most common defect in this repository. From `CLAUDE.md`:

> The #1 review finding is the same artifact (an opcode shape, a column, a seam) defined two ways in two docs.

A restated contract is a *future* contradiction: the moment the owning doc changes its value, every copy is silently
wrong, and the canon gate cannot see a copy that lives in prose phrased differently. So **do not restate** a struct
shape, an opcode payload, an enum, a scene-column layout, a handle format, a threading rule, or a quarantine constant in
a usage page. Instead, link to it:

- to the **SPEC-INDEX row** for the canonical value and its owner — [`design/SPEC-INDEX.md`](../../design/SPEC-INDEX.md) §2, or
- to the **owning subsystem doc** for the deep design — e.g. [`design/subsystems/reconciler-hooks.md`](../../design/subsystems/reconciler-hooks.md)
  for the reactive model and the `Prop<T>` surface, [`design/subsystems/gpu-renderer.md`](../../design/subsystems/gpu-renderer.md)
  for opcode payload shapes, [`design/subsystems/scene-memory.md`](../../design/subsystems/scene-memory.md) for the SoA
  columns.

What a usage page *may* state is the **honest, stable framing** the corpus already commits to — the phrases that are
truths, not values that drift: "near-zero-allocation" (zero managed alloc only in per-frame paint phases 6–13; bounded
Gen0 at the reconcile edge), "decoupled, not invincible" (a sustained GPU stall still bounds back to the UI thread),
"production safety == CI coverage". Match that voice exactly; do not inflate it and do not soften it. The home page's
[Honest status](./index.md#honest-status) section is the reference for the tone.

A worked example of *linking instead of restating* is already in the site: the engine-contributor page
[Adding or modifying a control](./engine-contributors/adding-or-modifying-a-control.md) states the control constraint in
one sentence ("a control introduces no new column, opcode, PAL seam, RHI method, or hook") and then **links** to
[`controls.md` §0/§13](../../design/subsystems/controls.md) for the authority, rather than recopying the rule's
justification. Do that.

---

## Control behavior docs cite BOTH `src/FluentGpu.Controls/*` and the gallery page

A page that documents a control's behavior is only trustworthy if its sample is **real and proven**. That means citing
two sources, not one:

1. **The control source** — `src/FluentGpu.Controls/<Control>.cs`. This is the behavior of record: the factory
   signatures, the `Style` record, the `Part*` consts, the interaction wiring. Controls are pure composition over the
   engine seams — they mint no new opcode, column, PAL seam, RHI method, or hook (see
   [Adding or modifying a control](./engine-contributors/adding-or-modifying-a-control.md)) — so the `.cs` file is the
   complete, honest surface. Copy names and signatures from it; do not paraphrase them.
2. **The gallery page that runs it** — under `src/FluentGpu.WindowsApp/`. The gallery is the **verified, runnable** form
   of the docs sample. Each demo is built with `ControlExample.Build(title, example, …, output:, code:)`
   (`src/FluentGpu.WindowsApp/ControlExample.cs`), so the snippet you quote is the same code that compiles and renders in
   the live control gallery. The control pages live in `ControlGalleryPages.cs`, `GalleryPages.cs`, `TextPages.cs`,
   `DateTimePages.cs`, and `CollectionsMenusPages.cs`.

The [Controls cookbook](./app-authors/controls-cookbook.md) is the model to follow — every recipe is "mined from a
runnable gallery page," and the page closes with a table mapping each control to its exact gallery page
(e.g. `ControlGalleryPages.cs → SliderControlPage`). When you add or change a control doc, add or update that mapping so
a reader can open the running example. If a sample is *not* in the gallery yet, it is not a verified sample — add the
gallery page (or pick a different, real example) rather than inventing one in prose.

---

## Anti-invention rule: every code sample must mirror the real surface in `src/FluentGpu.*`

This is the hardest rule and the most important: **never invent an API, control, prop, hook, or WinUI style in a
sample.** Inventing a plausible-looking method that does not exist is worse than omitting it — a reader who copies it
hits a compile error and stops trusting the docs. The corpus has been burned by this before; the standing instruction is
blunt: *open the file and copy the real names and signatures. If unsure, read more source; do not guess.*

Concretely, before you write a sample:

- **Open the file.** Mirror exact method names, parameter names, and overload shapes. The signatures are stable enough to
  quote: for example `FluentApp.Run(Func<Component> root, string title = "FluentGpu", int width = 800, int height = 600,
  bool mica = true, …)` is the real entry point (`src/FluentGpu.WindowsApp/FluentApp.cs`), and
  `ControlExample.Build(string title, Element example, string? description = null, …, string? code = null, …)` is the
  real gallery helper (`src/FluentGpu.WindowsApp/ControlExample.cs`).
- **Use the real authoring imports.** The cheat-sheet imports are `using static FluentGpu.Dsl.Ui;`, `using
  FluentGpu.Hooks;`, `using FluentGpu.Signals;`, `using FluentGpu.Controls;` — these are the ones every guide and gallery
  page uses. (Note one true subtlety the docs already state honestly: `Signal<T>` and `Prop<T>` are authored from the
  `FluentGpu.Signals` namespace even though their files live under the `FluentGpu.Engine` assembly's `Foundation/Signals/`
  folder.)
- **Respect the signals mental model in the sample itself.** A bound thunk reads `.Value` (it subscribes), not `.Peek()`;
  `ReactiveComponent.Setup()` shows a changing value through a bound prop (`Text = sig`, or `Text = Prop.Of(() => …)`),
  never `Ui.Text(sig.Value)`; parent→child data flows through signals or context, never constructor args. A sample that
  violates these isn't just stylistically off — it's wrong, and the [Pitfalls](../guide/pitfalls.md) page exists because
  these are the mistakes that actually ship.

If you are tempted to write `// (hypothetical API)` — stop. Either the surface exists (find it and cite it) or the page
shouldn't claim it.

---

## Where pages get their sources

Every page on this site is grounded in some combination of four places. Know which one owns what so you cite the right
authority and don't reinvent prose that already exists:

| Source | What it is | What you cite it for |
| --- | --- | --- |
| **The developer guides** — [`docs/guide/`](../guide/README.md) | The task-oriented, source-grounded guide that serves humans and agents (sections marked **🤖 AGENT** are machine-optimized). | The signals mental model ([reactivity](../guide/reactivity.md)), the element/layout/controls reference ([components-elements-layout](../guide/components-elements-layout.md)), the symptom→cause→fix table ([pitfalls](../guide/pitfalls.md)), WinUI fidelity rules ([control-fidelity](../guide/control-fidelity.md)). |
| **The gallery cookbook** — `src/FluentGpu.WindowsApp/` | The runnable control gallery; every snippet compiles and renders. | The verified, runnable form of any control sample. The [Controls cookbook](./app-authors/controls-cookbook.md) is its docs face. |
| **The control / DSL source** — `src/FluentGpu.*` | The engine itself — the real API surface. | Exact names and signatures for every sample; the file & ownership map in the [guide hub](../guide/README.md#-agent--file--ownership-map-where-to-change-what) tells you which file owns what. |
| **The design corpus** — [`design/`](../../design/README.md) | The architecture source of truth, canon-gated. | Any cross-cutting contract — link the [SPEC-INDEX row](../../design/SPEC-INDEX.md) or the owning [subsystem doc](../../design/subsystems/README.md); never restate it. |

The **API reference** under `api/` is a fifth source, but it is *generated*, not written — see the next section.

By audience: **app-author pages** ([App Authors](./app-authors/index.md)) lean on the guides and the gallery — they show
*how to build*, with runnable examples and the gotchas from [pitfalls](../guide/pitfalls.md). **Engine-contributor
pages** ([Engine Contributors](./engine-contributors/index.md)) lean on the engine source and the design corpus — they
explain *how internals work*, cite the exact file from the where-to-change map, and end with *how to verify* against the
harness. Keep each page on its side of that line.

---

## Building the site (docfx metadata + build) and regenerating the API YAML

The site is DocFX. The config is [`docs/site/docfx.json`](./docfx.json). Two commands, in order:

```text
docfx metadata docs/site/docfx.json   # regenerate the API YAML under docs/site/api/
docfx build    docs/site/docfx.json   # build the conceptual + API site into docs/site/_site/
```

`docfx metadata` reads the engine projects listed in `docfx.json`'s `metadata.src` block and emits the `*.yml` files
under `api/` that the API section is built from. That YAML is **derived from XML doc comments**: the solution baseline
[`src/Directory.Build.props`](../../src/Directory.Build.props) sets `GenerateDocumentationFile=true` (with `CS1591`
suppressed, so members without doc comments don't warn), which means a normal `dotnet build` already produces the doc XML
that `docfx metadata` consumes. So: build the solution, then run `docfx metadata`, then `docfx build`.

Two consequences worth knowing, both stated on the [API reference page](./api/index.md):

- **The `api/` pages are generated — do not hand-edit them.** If an API description is wrong, fix the XML doc comment on
  the type or member in `src/FluentGpu.*` and regenerate. Editing the YAML is editing build output.
- **The harness and gallery apps are excluded from API metadata** (`FluentGpu.VerticalSlice` and `FluentGpu.WindowsApp`
  are applications, not library surface), as are the source generators and the headless test seams. Their *source* is
  still cited heavily from conceptual pages — that's where verified samples come from — but they produce no API YAML.

When you add a new conceptual page, add it to the relevant `toc.yml` (the section TOCs are
[`app-authors/toc.yml`](./app-authors/toc.yml) and [`engine-contributors/toc.yml`](./engine-contributors/toc.yml); the
top nav is [`toc.yml`](./toc.yml)) so it appears in navigation.

---

## The two checks before committing docs

Two gates, each conditional on what you touched. Run the one(s) that apply.

### 1. If you touched or added code samples — prove the surface is real and green

The point of the anti-invention rule is that samples reflect a *working* surface. The fastest way to prove the API you
documented actually exists and behaves is the headless verification harness — ~60 cross-seam golden checks, no GPU and no
window:

```text
dotnet build src/FluentGpu.VerticalSlice                 # clean build first
dotnet run   --project src/FluentGpu.VerticalSlice       # must print: ALL CHECKS PASSED
```

A clean build is itself the cheapest anti-invention check: if a name in your sample doesn't exist, the surface it lives
on won't compile. If your page documents reactive/layout/render behavior, the harness is also where you'd confirm the
claim — checks assert on `FrameStats` (`Rendered`, `ComponentsRendered`, and `HotPhaseAllocBytes`, which must be `0` on
steady frames). GPU pixels are *not* asserted by the harness; that is a separate manual "needs-pixels" pass on the real
D3D12 path, and a doc should describe it as such rather than claiming pixel-level verification it didn't do.

### 2. If you touched anything under `design/` — run the canon drift gate

Editing a usage page on this site does **not** require the canon gate (the gate scans the `design/` tree, which this site
is not part of). But if your change also touched the design corpus — say you corrected a canonical value and updated its
SPEC-INDEX row — run the drift gate, which fails if a known-stale or superseded token reappears in the live `design/`
tree:

```powershell
powershell -File design/check-canon.ps1   # exit 0 = clean
```

The order for a canon change is fixed: edit [`design/SPEC-INDEX.md`](../../design/SPEC-INDEX.md) first, then the owning
doc, then run the gate. To intentionally mention a superseded form in live prose (to explain a correction), put
`<!-- canon-allow: reason -->` on that line. This is the design-side discipline; the [Design canon page](./design/index.md)
covers it from the reader's perspective.

---

**See also:** the [site home page](./index.md) for the two-trees relationship and the honest-status voice; the
[App Authors](./app-authors/index.md) and [Engine Contributors](./engine-contributors/index.md) hubs for the two reader
paths; the [API reference](./api/index.md) for how the generated section works; and the developer
[guide hub](../guide/README.md) for the file & ownership map every sample is grounded in.
