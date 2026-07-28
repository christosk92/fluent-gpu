# How Agents Should Build Apps in FluentGpu/Wavee — The Playbook

Audience: AI coding agents (and their humans) working in `C:\wavee\fluent-gpu`. The engine is signals-first, NativeAOT, GPU-rendered; the app is Wavee (`src/apps/Wavee`). Every rule below is grounded in a file you can open.

---

## 1. The meta-workflow

**Explore → plan → implement → verify. Never edit-first.** Editing before reading produces code that solves the wrong problem ([Claude Code best practices](https://code.claude.com/docs/en/best-practices)). In this repo the order is fixed:

1. **Load the rulebook before the file.** Read `CLAUDE.md`, then invoke the `fluentgpu` skill (`.claude/skills/fluentgpu/SKILL.md` — "rules that prevent ~90% of bugs" plus a where-to-change-what file map). For app work, the `wavee` skill; for theming, `.claude/skills/fluentgpu/theming.md` is mandatory reading.
2. **Explore read-only against exemplars, not abstractions.** Exemplar-file pointers beat prose descriptions. The canonical exemplars: `src/apps/Wavee/Features/Detail/ArtistPopular.cs` (responsive + SWR + props-freeze done right), `src/apps/Wavee/Components/TrackRow.cs` (flex laws, row states), `src/FluentGpu.Controls/Responsive.cs` (self-measuring composition).
3. **Plan multi-file work; skip planning for one-sentence diffs.** Symptom→cause→fix lookups live in `docs/guide/pitfalls.md` before you invent a diagnosis.
4. **Verify with the gates, show evidence.** `dotnet build src/FluentGpu.slnx` clean + `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED" (~521 gates incl. zero-alloc tripwires); `--screenshot <path>` for pixels; `docs\design\check-canon.ps1` after any `docs/design/` edit. "Looks done" is never the stop signal — paste the gate output.

**How not to hardcode around existing systems.** Agents optimize for what's in the context window, so they regenerate what already exists — a second validator, an ad-hoc breakpoint, a hand-rolled lerp. The antidote is mechanical: before writing any primitive (animation, spacing, color, virtualization, responsive measure, skeleton), grep for the house one first. This repo enforces it deterministically: the single-owner canon model (`docs/design/SPEC-INDEX.md` §2 + `docs/design/subsystems/README.md` ownership map — every artifact defined in exactly ONE doc), the canon drift gate (`check-canon.ps1` fails when a superseded form reappears), and `ReuseGuard` (`FG_REUSE_GUARD=1`) which trips props-freeze mistakes at runtime. Two standing user rulings bind all agents: **no FG_\* env kill switches for new behavior** (the new path is the unconditional default), and **the user runs the app themselves** — verify headlessly, don't launch the gallery unprompted. Never touch the fenced paths (`src/apps/.native/**`, `Wavee.PlayPlay/**`, `private-runtimes/**` — see CLAUDE.md).

---

## 2. Architecture rules

**Signals are the only reactivity mechanism.** Reading `.Value` subscribes the current computation; writing re-runs only readers; `.Peek()` reads without subscribing. Three update paths, cheapest first: compositor-only bind → granular component re-render → `Flow.For`/`Flow.Show` keyed boundary (`docs/guide/README.md`, `docs/guide/reactivity.md`).

- **Derive, don't duplicate** — a second signal synced by an effect causes glitches ([SolidJS](https://docs.solidjs.com/concepts/derived-values/derived-signals)). House idiom: `UseMemo` / `Prop.Of(() => …)`; an effect whose body only writes another signal is a bug. `docs/guide/reactivity.md`.
- **Effects at the edge only; auto-tracked.** `UseEffect` tracks the signals it reads — no deps list; pass a `DepKey` only to over-scope. Never state→state sync inside one. `.claude/skills/fluentgpu/SKILL.md`.
- **Read reactively or freeze forever.** `Ui.Text(sig.Value)` in a run-once render is the #1 mistake — it renders once and never updates. Write `Text = sig` or `Prop.Of(() => …)`. SKILL.md.
- **Props freeze at mount** — the repo's sharpest trap. `Embed.Comp(() => new T { Field = v })` runs the factory once; parent re-renders discard the new factory. The four sanctioned channels for changing data: re-pushed props + `UseProps<T>()`/`[Props]` partial; a bound `Prop<T>`/signal (swap the *value*, never the signal — bind wiring is mount-only); `Ctx.Provide`+`UseContext` for ambient broadcast only; a `Key` change when *identity* changes. `docs/design/subsystems/component-props-contract.md`. Exemplar of frozen-args-made-legal: `ArtistPopular.cs:86-99` folds the density tier into the `Key` so a width crossing remounts rather than leaving stale props.
- **Stable keys on every dynamic list, never index-when-reorderable** — index keys re-associate old state with the wrong item. `Flow.For` keys are mandatory; entity URIs are the key (`"chart:" + t.Uri + …`, `ArtistPopular.cs`). Corollary: a changed key is the sanctioned remount/reset; re-keying for a mere value change drops focus/popup state (contract §4).
- **State placement: colocate, lift only to the lowest common parent.** Exemplar: selection lives in an external `SelectionModel` *outside* the tier-keyed list precisely so it survives tier remounts (`DetailTracks.cs:95`).
- **Async data = SWR, never block first paint on the network.** `UseResource(ct => fetch, deps…)` returns `Resource<T>{ Loadable, IsFetching, IsStale, Refresh, Mutate }`; revalidation lands *into the same component* — `ArtistPopular.cs:59` grows the pager in place instead of remounting the band. Local cache renders first; the network heals it.
- **Every async view renders all four states** — loading (skeleton), error (retry), empty (designed screen), content. Skeletons: `Element.SkeletonProxy` + `DeriveRenderedOutput` derives placeholders from the rendered output (`src/FluentGpu.Engine/Hooks/SkeletonDeriver.cs:148-160`; guide `docs/guide/skeleton-loading.md`).
- **No nullable-optional deps with silent defaults** in app wiring — `.claude/skills/wavee/wiring-discipline.md`.

---

## 3. Beauty & craft rules

- **Never free-hand a spacing or color value** — constrained scales, not talent, make UI look professional (Refactoring UI). House idiom: `Spacing.*` (4px grid `XXS(2)…XXXL(32)` + semantic re-points like `PageWide`, `Card`, `Inner`) in `src/FluentGpu.Engine/Dsl/Spacing.cs`; radii in the `Radii.cs` sibling.
- **Two-layer tokens: components reference roles, never raw hex.** Frozen literals break live theme switching (a documented theming gotcha). House idiom: `Tok.*` semantic tokens (`src/FluentGpu.Engine/Dsl/Tokens.cs`) — the fill hierarchy *encodes interaction state* (`FillControlDefault`/`Secondary`=hover/`Tertiary`=pressed), `AccentRamp.Derive` synthesizes the 7-shade accent ramp; theme switch is one pointer swap. App palette: `WaveeTokens.cs` (`RowZebra/RowHover/…`). Read `theming.md` first.
- **Motion uses tokens, never ad-hoc durations.** Duration bands (feedback ~83-150ms, transitions 150-250ms, containers 250-400ms; exits faster than enters — Material/Fluent consensus) are already encoded: `MotionTok` (`src/FluentGpu.Engine/Animation/MotionTok.cs:98-135`) — `ControlFaster/Fast/Normal`, `Standard/EmphasizedEnter/Exit`, springs via `FromResponse` (`StandardSpring` 0.35/0.85, `ConnectedFly` critically damped). App-expressive palette: `MotionRecipes.*` (`docs/guide/motion-recipes.md`) — explicitly not for restyling kit controls.
- **Reduced motion is a value the system reads, never an if-branch.** Every `MotionTok` carries a `ReducedMotionPolicy`; `if (reducedMotion) return;` in a hook is banned (CLAUDE.md canon).
- **Declarative orchestration only.** Enter/exit/stagger/layout-FLIP are `Element.{Transition,While*,Enter,Exit,Stagger,Layout}` on the `AnimValue` slab — hand-rolled timers, lerps, and per-control state machines are deleted architecture. `docs/plans/animation-engine-rework-design.md`.
- **Interaction states are declared, not hand-built.** Rest/hover/pressed come from declared `BoxEl` fields serviced by engine anim channels (`HoverFade`/`PressFade`) — `TrackRow.cs:244-260` zebra/hover/press; app chrome via `el.Interactive(Interaction.ListRow/Card/Subtle/AccentGhost)`. Never build a VisualStateManager.
- **Responsive pressure = ordered demotion through tiers with hysteresis, not ad-hoc hiding.** The Spotify list-row drop order (drop tertiary metadata → stack artist under title → ellipsize, never wrap) is encoded as tier maps: `DetailLayoutBreakpoints.cs` (7 tiers; widen immediately, narrow only past a 24-DIP dip; viewport-derived seeds so frame one never composes the wrong tier), consumed via tier-keyed remounts (`DetailTracks.cs:20-22`) so every tier mounts one clean column set.
- **The flex laws** (48 occurrences across 19 Wavee files): elastic lane = `Grow=1f, Basis=0f, MinWidth=0f`; ellipsizing text = `MinWidth=0f + MaxLines=1 + Trim=CharacterEllipsis`; fixed cells = `Shrink=0f`. Exemplar: `TrackRow.cs:195,248,446,496`.

---

## 4. Performance rules

- **Zero managed alloc in frame phases 6–13 is a gated invariant, not an aspiration.** A GC sweep eats several ms of a 16.6ms budget. The gate: `CoreSuite.cs:77` asserts `HotPhaseAllocBytes == 0`; every change re-runs it via VerticalSlice. Corollaries: no `new`/LINQ/boxing/closures in bind thunks or hot effect bodies; wire bindings/effects once at mount.
- **High-frequency scalars bind, never setState-per-event.** A pointer-move driving `setState` re-renders per event; a bound `FloatSignal` (`Slider.Create(FloatSignal)`) stays compositor-side. Prefer a `Transform` bind (compositor-only) over `Width`/`Text` binds (scoped relayout). SKILL.md rules 9-10.
- **Virtualize every long list.** `Virtual.List(count, itemExtent, renderItem, keyOf)` / `Virtual.Grid` recycle over the slab free-list with 0-alloc in-window scroll (`src/FluentGpu.Controls/Virtual.cs:12-30`). `Flow.For` over 10k tracks is a guaranteed hitch. Realize budgets and skewed overscan are already engine policy (scroll-perf campaign) — don't reinvent them per-surface.
- **Respect the budgets ledger.** Image residency Soft 192/Hard 384 MB LRU, decode channel cap ~256 with priority drop, `ObjectPool` cap 32, ≤16 damage rects else full redraw — one roll-up table in `docs/design/budgets.md` §1. Decode off-thread at display size; never on the UI thread.
- **Scoped-relayout firewall:** big containers get explicit `Width+Height+ClipToBounds=true` so a child mutation can't trigger a page-wide relayout (SKILL.md rule 8).
- **Quantize measures.** `UseMeasuredWidth(quantum)` rounds before an exact-compare write so sub-quantum jitter never re-renders; written during layout, consumed next frame (never re-entrant), with a DEBUG tripwire for measure→resize loops (`RenderContext.Measure.cs:115-146`). Always seed a pre-measure fallback (`measuredW.Value > 0.5f ? … : 600f`, `ArtistPopular.cs:63`).
- **Measure percentiles and hitches, not average FPS** — one 50ms frame in a sea of 8ms frames barely moves the average but is a visible hitch. The house toolchain: `ops/diag` capture + present-stamp/publishSeq frame identity + ScrollTrace latency rows; screenshot diffs via `--screenshot`. Profile release/AOT builds only (`cpu-hotspot-trace-campaign` memory: WPR+EtlTop for AOT).
- **The threading model is settled — don't re-litigate.** UI thread reconciles+layouts → `PUBLISH(13a)` → render thread records/submits/presents; the render thread owns every `ComPtr`. Canon: `docs/design/subsystems/threading-render-seam.md`.

---

## 5. Top-10 mistakes agents make here

1. **Passing changing data as ctor args to `Embed.Comp`** expecting updates → props freeze at mount. Fix: signal / `UseProps` / context / `Key` remount (`component-props-contract.md`); `FG_REUSE_GUARD=1` catches it.
2. **`Ui.Text(sig.Value)` in a run-once render** → renders once, never updates. Fix: `Text = sig` or `Prop.Of(() => …)` (SKILL.md).
3. **`if (width > 900)` hardcoded in render** → churn and flip-flop. Fix: `Responsive.Of` / `UseMeasuredWidth(quantum)` + hysteresis tiers + tier-folded `Key` (`DetailLayoutBreakpoints.cs`, `ArtistPopular.cs:86-99`).
4. **Missing `MinWidth=0f` on flexible text / `Shrink=0f` on fixed cells** → row blows out instead of ellipsizing (`TrackRow.cs:446,496`).
5. **Allocating in hot paths** (LINQ, closures, `new` in bind thunks/effects) → trips the phase 6–13 gate (`CoreSuite.cs:77`).
6. **Hand-rolled animation** (timers, lerps, reduced-motion if-branches) → use `Transition`/`While*` + `MotionTok.*`; reduced-motion is a value.
7. **Hardcoded hex/pixel literals** → breaks theme switching. Fix: `Tok.*` / `WaveeTokens` / `Spacing.*`.
8. **Non-virtualized or keyless lists** → `Virtual.List/Grid`; `Flow.For` keys mandatory.
9. **Claiming done without evidence** → run build + VerticalSlice + (for design docs) `check-canon.ps1`; paste output. Never restate an owned contract in a second doc.
10. **Touching fenced paths or adding FG_\* kill switches** → `.native`/`Wavee.PlayPlay`/`private-runtimes` are out of scope; new behavior ships as the unconditional default.

---

## 6. Draft AGENTS.md section (paste-ready)

```markdown
## Non-negotiable laws for agents in fluent-gpu

### Scope & process
- NEVER read/edit: src/apps/.native/**, src/apps/Wavee.PlayPlay/**, private-runtimes/**, ops/tools/playplay_* (separate private repo).
- Before writing UI code, read .claude/skills/fluentgpu/SKILL.md (and theming.md for any theme work); docs/guide/pitfalls.md maps symptom→fix.
- Evidence before "done": `dotnet build src/FluentGpu.slnx` clean AND
  `dotnet run --project src/FluentGpu.VerticalSlice` prints "ALL CHECKS PASSED".
  After editing docs/design/: `powershell -File docs\design\check-canon.ps1` exits 0.
- Every cross-cutting contract has ONE owning doc (docs/design/SPEC-INDEX.md §2). Reference it; never restate a struct/enum elsewhere.
- No FG_* env kill switches for new behavior — the new path is the unconditional default.
- Don't launch the gallery to verify; the user runs the app. Use headless gates + `-- --screenshot <path>`.

### Reactivity (signals-first)
- Reading `sig.Value` subscribes; `Peek()` doesn't. Derive with UseMemo/Prop.Of — never a second signal synced by an effect.
- `Ui.Text(sig.Value)` in a run-once render NEVER updates. Use `Text = sig` or `Prop.Of(() => …)`.
- Props FREEZE at mount: `Embed.Comp(() => new T { Field = v })` runs once. Changing data reaches a child ONLY via
  UseProps/[Props], a bound signal (swap value, never the signal), Ctx.Provide+UseContext, or a Key remount.
  Exemplar: ArtistPopular.cs (density tier folded into Key).
- Flow.For requires stable entity keys. Re-key only on identity change (re-keying on value change drops focus state).
- Async reads via UseResource (SWR). Every async view renders loading/error/empty/content; skeletons via SkeletonProxy.

### Craft
- No free-hand values: Spacing.* (4px grid), Tok.*/WaveeTokens (semantic roles, never hex), MotionTok.*/MotionRecipes (never ad-hoc ms).
- Reduced motion is a VALUE the motion system reads — never an if-branch.
- Interaction states are declared (Fill/HoverFill/PressedFill, el.Interactive(...)) — no per-control state machines.
- Responsive = tier maps with hysteresis (DetailLayoutBreakpoints.cs) + tier-keyed remounts; never `if (width > N)` inline.
- Flex laws: elastic lane Grow=1,Basis=0,MinWidth=0; ellipsizing text MinWidth=0+MaxLines=1+Trim; fixed cells Shrink=0.

### Performance
- ZERO managed alloc in frame phases 6–13 (gated). No new/LINQ/closures in bind thunks, effects, or per-frame code.
- High-frequency scalars bind (FloatSignal), never setState-per-move. Prefer Transform binds over Width/Text binds.
- Long lists use Virtual.List/Grid (never Flow.For over thousands). Budgets live in docs/design/budgets.md — obey them.
- Big containers: explicit Width+Height+ClipToBounds=true (relayout firewall). Quantize measured widths.
```