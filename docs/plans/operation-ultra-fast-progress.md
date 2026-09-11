# Operation ultra-fast GPU engine — progress

Tracks phases P0–P4 and P8 of `docs/plans/operation-ultra-fast-gpu-engine` (source plan: the user's
`comapre-all-our-changes-reflective-scone.md`, section "Operation ultra-fast GPU engine — zero-allocation,
sub-budget scrolling", 2026-09-08 evening). Kept factual and short so the next agent can pick up without re-deriving
context.

## Status: P0 DONE, P1 DONE, P2 DONE, P3 DONE, P4 DONE (including, as of the 2026-09-09 session, the two leftovers
the P4 session deferred: the `TextMeasureCache` 2-entry ring and the DEBUG `FG_LAYOUT_VERIFY` oracle + unmarked-write
tripwire), **P8 DONE** (incremental scene capture for coast frames). Not started: P5, P5b, P6, P7 — all app-side
except P8's neighbours, and P5 is the next unit of work. See "2026-09-09 session" at the END of this file for what
landed last.

## Regression fixed FIRST this session (pre-P2): `gate.shelf.binding.measurement`

Confirmed NOT pre-existing, as the task brief suspected: `FlexLayout.Collapsed(NodeHandle)` read
`(_scene.Flags(h) & NodeFlags.Visible) == 0` directly — the SAME bit `PagedShelf`'s measurement-probe layer
toggles PURELY for record/paint culling (`PagedShelf.cs`'s "RECORD-cull the permanently-mounted probe layer"
comment, whose contract is "layout still runs, it ignores the flag" — true before P1). P1 made `Collapsed()`
also remove the node from LAYOUT FLOW (0×0, skipped in `FirstVisibleChild`/`NextVisibleSibling`) whenever
`NodeFlags.Visible` clears, for ANY reason — not only the P1 presence channel's own `SceneStore.SetCollapsed`.
So toggling `NodeFlags.Visible` for paint-only culling (PagedShelf's probe) now ALSO collapsed it out of layout,
and the probe never measured a real card height again → `gate.shelf.binding.measurement` regressed to
`heights=68->68->68` (the probe layer stuck at its very first, degenerate 0-height sample).

**Root cause fix (engine, not the gate):** `FlexLayout.Collapsed` (`src/FluentGpu.Engine/Layout/FlexLayout.cs`)
now reads the DEDICATED `SceneStore.AuxFlags.Collapsed` bit via `_scene.IsCollapsed(h)` instead of
`NodeFlags.Visible` directly. `SceneStore.SetCollapsed` still MIRRORS the aux bit onto
`NodeFlags.Visible`/`HitTestVisible` (so the recorder/hit-test/`LayoutSig` readers still see a presence flip for
free — no behavior change there), but the LAYOUT collapse decision now keys off the bit only the P1 presence
channel writes. A caller (like `PagedShelf`) that toggles `NodeFlags.Visible` directly for paint-only culling no
longer affects layout — restoring the pre-P1 contract. One-line fix + a doc comment; `gate.shelf.binding.measurement`
green again, all other presence gates (`gate.presence.*`) unaffected (verified — they all still pass). Verified
Debug AND Release, VerticalSlice both green (1360/1360) BEFORE starting P2.

This session finished P0's two leftovers (the `gate.diag.counters-always-on` VerticalSlice gate + the stale
`FrameStats` doc comment + the `docs/guide/rendering-and-performance.md` FrameStats field list) and landed the whole
of P1 (`Element.Visible : Prop<bool>` — the presence channel), gates included. Everything below is real code,
built and gated (see "Verification").

## P0 — Honest counters + partial-class split (DONE this session)

Prior session landed the counters themselves (see git history / the diff — `IFontSystem.ShapeCount`, the
always-on `FlexLayout` diag counters, the new `FrameStats` fields, `TreeReconciler.NodeBindingFireCount`/
`WriteCount`, and the four empty partial-class shells). This session closed the two open items:

- **`gate.diag.counters-always-on`** (`src/FluentGpu.VerticalSlice/Suites/DiagnosticsSuite.cs`,
  `CountersAlwaysOnChecks`, called from `Run`): mounts a text + two boxes with NO `FG_LAYOUT_DIAG` set, asserts the
  first frame's `MeasureCount > 0` and `TextShapes > 0` (real shaping happened, counted with no env flag), then an
  identical second frame asserts `TextShapes == 0` (a measure-cache hit, nothing re-shaped). Both checks pass.
- **Stale doc comment**: `AppHost.cs`'s `FrameStats.MeasureCount`/`ArrangeCount`/`TextShapeMisses` doc comment said
  "valid only when FG_LAYOUT_DIAG=1, else 0" — no longer true since the prior session's always-on change; corrected
  to say FG_LAYOUT_DIAG now gates only `FlexLayout.Run`'s `Console.Error.WriteLine` printout.
- **`docs/guide/rendering-and-performance.md`** "Measuring" section: added bullets for `MeasureCount`/
  `ArrangeCount`/`TextShapeMisses` (always-on), `TextShapes`, `CapturedNodes`, `BindingFires`/`BindingWrites`,
  `RebindFlushAllocBytes` — this is the practical "FrameStats table" app authors actually read (not
  `devtools.md`, which is a conceptual flamegraph design doc with no literal field list to update).

## P1 — `Element.Visible : Prop<bool>` presence channel (DONE this session)

Read `docs/design/subsystems/layout.md` §4.7 (new, written this session) for the full narrative — this section is
a pointer + the file list + gate names, not a restatement.

### Landed

- **`Element.Visible : Prop<bool>` = true** (`src/FluentGpu.Engine/Dsl/Element.cs`) — on the BASE type, so it
  applies to every element kind. Bindable like `Fill`/`Opacity`.
- **`SceneStore.Aux.cs`** (`src/FluentGpu.Engine/Scene/SceneStore.Aux.cs`, filled in — was an empty P0 shell): new
  `_aux` byte column (ctor-allocated, `ResizeColumns`-tracked, zeroed in `CreateNode`), `AuxFlags` enum
  (`Collapsed` live; `SubtreeLayoutDirty`/`ArrangedValid` reserved for P4, unused). `IsCollapsed`/`SetCollapsed`/
  `SetCollapsedIfChanged`. `SetCollapsed` mirrors `Collapsed` onto the EXISTING `NodeFlags.Visible`/
  `NodeFlags.HitTestVisible` bits (no new flags) so the recorder's paint-reachability early-return
  (`SceneRecorder.Walk`), the hit-test walk (`InputDispatcher`), and `FlexLayout.LayoutSig`'s scoped-relayout
  signature (already hashes `Flags(node)`) all see the flip for free. On collapse it ALSO zeroes the node's own
  `Bounds` immediately — necessary because a Flex/Wrap/ZStack parent's child walk (below) will never call
  `Measure`/`Arrange` on a collapsed child again, so a runtime true→false flip would otherwise leave the node's
  Bounds stale at whatever size it last held (a fresh mount doesn't have this problem — `CreateNode` already zeros
  Bounds). Marks `LayoutDirty|PaintDirty` on the node and `LayoutDirty` on the parent.
- **Two writers**, both in the new `src/FluentGpu.Engine/Reconciler/Reconciler.Presence.cs` (filled in — was an
  empty P0 shell):
  - `ApplyPresenceStatic` — called from `WriteColumns`' generic every-element-type section (alongside
    MorphId/RelativeTo/ScrollBinds/Stagger) when `!el.Visible.IsBound`. Equality-gated via
    `SetCollapsedIfChanged`.
  - `BindPresence` — called from `BindNode` UNCONDITIONALLY (first line, before the `BoxEl`-only channels) since
    `Visible` lives on the base type. One mount-time `Effect`, equality-gated on the RESOLVED collapse state,
    counted by `NodeBindingFireCount`/`WriteCount`. DEBUG-asserts via `BindContract.MorphVisibleBind` (new method,
    `src/FluentGpu.Engine/Reconciler/BindContract.cs`) that a `MorphId` node never binds `Visible`. The
    false→true edge seeds the node's declared `Enter` via `SynthesizeDeclarative` + `AnimEngine.SeedEnter` (same
    machinery the ordinary mount-Enter path uses); true→false just snaps.
  - `SetSubtreeHidden` (same file) — walks the node's live subtree; for every mounted `CompEntry`, sets `Hidden`
    and refreshes `ActiveSig` to `!Parked && !Hidden` (same formula `SetSubtreeParked` now writes on a Park edge —
    that call site was updated too, `Reconciler.cs` ~line 1685). `Hidden` touches ONLY `ActiveSig` — never
    `NodeFlags.Parked`/`DeferredRender`/the render `Effect` — so a collapsed component keeps rendering; only
    `UseInterval` (already gated on `UseIsActive()`) auto-pauses. `MountComponent` seeds `Hidden` by walking the
    node's ancestor chain at mount (`Reconciler.cs` ~line 993) since, unlike `NodeFlags.Parked`, collapse state is
    deliberately NOT propagated down to fresh children (the recorder/hit-test early-returns already make that
    unnecessary for paint/hit-test, so only the component-activation side needs the explicit walk).
- **Layout — `src/FluentGpu.Engine/Layout/FlexLayout.cs`**:
  - `Collapsed(NodeHandle)` reads the mirrored `NodeFlags.Visible` bit.
  - `FirstVisibleChild`/`NextVisibleSibling` — the ONE substitution point. Every Flex row/column loop (both in
    `Measure`'s general path and all four in `Arrange`), `MeasureWrap`, `ArrangeWrap` (both passes + the initial
    `lineStart`), `MeasureZStack`, `ArrangeZStack` now walk children through these instead of raw
    `FirstChild`/`NextSibling` — true CSS `display:none` removal (no box, no margin, no gap slot), by construction
    (no per-loop continue/idx bookkeeping needed — every downstream index, e.g. `Arrange`'s `finalMain[idx]`, stays
    in lockstep because both the counting and placement passes walk the identical filtered sequence).
  - `Measure`/`Arrange` both short-circuit to 0×0 at their very first line when the node itself is collapsed —
    before the scroll/grid/zstack dispatch, before the measure memo. This ONE check is what makes a collapsed grid
    cell size 0×0 with zero grid-specific code (`ArrangeGrid`'s row-assignment walk deliberately keeps RAW
    `NextSibling`, so a collapsed cell still occupies its row-major track —
    `gate.presence.grid-cell-keeps-track`), and what covers "a collapsed virtual slot root measures 0 at its rect"
    (a realized virtual-list row, or a collapsed `ScrollEl`/`VirtualListEl` root) with zero virtualization-specific
    code either.
- **Skeleton deriver** (`src/FluentGpu.Engine/Hooks/SkeletonDeriver.cs`): a statically collapsed real node
  (`Visible` unbound and `false`) derives to `new BoxEl { Visible = false, IsEnabled = false, HitTestVisible =
  false }` — nothing to shimmer. A bound `Visible` is left alone at derive time (the mounted shimmer's own
  `BindPresence` effect governs it later).
- **`BindContract`** (`src/FluentGpu.Engine/Reconciler/BindContract.cs`): new `MorphVisibleBind(string
  elementType)` — same report/throw discipline as the existing `Flip`.
- **DiffProps / static-hoist generators — confirmed, no code changes needed**:
  - `FluentGpu.SourceGen.Engine.DiffPropsGenerator` walks the WHOLE `Element` inheritance chain (explicitly, to
    catch base-type fields like `Transition`/`Enter`/`ScrollBinds`) and treats any `FluentGpu.Signals.Prop<T>`
    property as a bindable channel automatically — `Visible` is picked up with zero generator changes, verified by
    inspection of `Extract`/`IsPropChannel` (`src/FluentGpu.SourceGen/Engine/DiffPropsGenerator.cs`).
  - The "static-hoist generator" (`StaticHoistGenerator`, `src/FluentGpu.SourceGen/Engine/
    GatedMigrationGenerators.cs`) is DORMANT — gated behind `[EnableStaticHoistAttribute]`, which nothing in the
    tree uses, and its own doc comment says "the real mechanism — a C# interceptor... — is risky... Dormant; the
    cache shape lands here, the interceptor + premise verification are the activation steps." There is no
    per-field boundedness check to update because there is no interceptor yet. Nothing to change; noted here so
    the next agent doesn't re-derive this.
- **Timer parking — landed for `UseInterval`, explicitly NOT for `UseTimeout`/`UseKeyframes`** (see "Open items").

### Gates (VerticalSlice, `LayoutShellSuite`'s presence region — `src/FluentGpu.VerticalSlice/Suites/
LayoutShellSuite.cs`, `PresenceChecks`, called from `Run`)

All six green:
- `gate.presence.static-collapse-removes-from-flow`
- `gate.presence.bound-flip-scoped`
- `gate.presence.true-edge-seeds-enter`
- `gate.presence.hidden-parks-timers`
- `gate.presence.grid-cell-keeps-track`
- `gate.presence.bindcontract-flip` (DEBUG-only; reports `"diag-off"` and passes vacuously in a Release slice,
  matching the suite's existing pattern for DEBUG-gated assertions)

Two implementation notes future-you will want if touching these:
- **`gate.presence.hidden-parks-timers`** does NOT assert on `UseInterval` tick COUNTS across many pumped frames —
  an earlier version tried that and found the headless `FixedFrameTimeSource`'s dt is throttle/resync-clamped
  (`AppHost.cs` ~3349-3357: `_frameTime.Resync()` drops the "stale throttle gap" when stepping up from an
  idle/throttled cadence) whenever nothing else is animating, so a UseInterval firing RATE is not a reliable
  deterministic signal in this harness — pumping 200 extra frames after a reveal did not move the tick count at
  all. The gate instead uses `UseActivation(onActivated, onDeactivated)` (edge-triggered, on the SAME
  `UseIsActive()` signal `UseInterval` gates on) for the actual pass/fail, and keeps `Ticks` only as informational
  detail in the failure message.
- **`SceneStore.SetCollapsed`'s immediate Bounds-zero** (see "Landed" above) exists BECAUSE
  `gate.presence.bound-flip-scoped` caught it: without it, a runtime true→false flip left the collapsed node's
  `Bounds.W/H` stale (the Flex parent's `FirstVisibleChild`/`NextVisibleSibling` walk never visits it again to
  correct them) even though `IsCollapsed` correctly flipped and the FLOW correctly closed the gap around it.

### Open items for the P2 agent (or a later pass)

- **`UseTimeout`/`UseKeyframes` do not consult `UseIsActive()`/`Hidden` at all** — verified BOTH have zero
  active-gating today, even for the pre-existing `Parked` case (not a P1 regression — a pre-existing gap this
  session found while scoping "hosts check it"). Wiring presence-aware (and Park-aware) pausing into a one-shot
  timer (`TimeoutCell`, `src/FluentGpu.Engine/Hooks/RenderContext.Timers.cs`) needs a pause/resume design beyond
  a boolean gate — the timer is scheduled by absolute due-time (`HostTimerQueue`), so "pausing" a one-shot
  requires tracking remaining time, not just skip-the-callback. `UseKeyframes` seeds an `AnimEngine` track directly
  (`Component.cs:919`) — parking that while hidden would mean either gating the seed call itself (loses the
  seeded-once contract) or teaching `AnimEngine.SetNodeParked` about a second, presence-driven parked reason (it
  already exists for `NodeFlags.Parked` via `OnNodeParkedChanged`, but wiring presence into it needs a decision
  on whether a collapsed node's mid-flight keyframe track should resume from where it left off or restart — not
  decided here). Left unstarted; `entry.Hidden` and `RenderContext.UseIsActive()` are already available for
  either.
- P2 (`Foundation/SpanText.cs`, `SpanTextEl`, `OnSpanClick`, `Reconciler.Spans.cs`, `gate.spans.*`) — DONE, see
  the "P2" section below.

## Verification run in this session

- `dotnet build src/FluentGpu.slnx` (Debug): **0 errors** (pre-existing warnings only, none in touched files).
- `dotnet build src/FluentGpu.slnx -c Release`: **0 errors**.
- `dotnet run --project src/FluentGpu.VerticalSlice` (Debug): **1 CHECK FAILED** —
  `gate.shelf.binding.measurement` (`src/FluentGpu.VerticalSlice/Suites/ShelfBindingChecks.cs`). Confirmed
  PRE-EXISTING and unrelated: `git diff src/FluentGpu.Controls/PagedShelf.cs` shows a large uncommitted diff (a
  `PagedShelf.Create<T>(IReadOnlyList<T> items, ...)` retained-shelf-authoring rework) that predates this session
  and has nothing to do with Visible/presence; `ShelfBindingChecks.cs` itself is untracked (`git status`) and was
  not touched this session. Fails identically alone (`--suite scroll`) and in Release — a real pre-existing
  failure in that other feature's work, not a load flake, not a P1 regression.
- `dotnet run --project src/FluentGpu.VerticalSlice -c Release`: same single pre-existing failure, nothing else.
- `dotnet test src/FluentGpu.Engine.Tests/FluentGpu.Engine.Tests.csproj -c Release`: **233 tests, 231 passed, 2
  failed under load** (`UiPostDrainTests.RestoreFrame_HasNoBacklogToSettle`,
  `HeadlessPlayerTests.Facade_RoutedBackendDrivesForwardedSignals`) — both **pass individually**
  (`--filter FullyQualifiedName=...`, 103ms and 22ms) — pre-existing load-flakes per CLAUDE.md's rule, unrelated
  to Reconciler/FlexLayout/SceneStore (UI-post-drain timing and a media-facade test).
- `dotnet build C:\wavee\WaveeMusic\src\apps\Wavee\Wavee.csproj -c Release`: **Build succeeded, 0 errors** — the
  app still compiles against the engine changes (additive-only surface: `Element.Visible`, `SceneStore.IsCollapsed`/
  `SetCollapsed`/`SetCollapsedIfChanged`, `BindContract.MorphVisibleBind`, `CompEntry.Hidden` is private).
- `powershell -File docs/design/check-canon.ps1`: **Canon OK** (33 docs scanned, no stale tokens) after the
  `layout.md` §4.7 / `reconciler-hooks.md` / `component-props-contract.md` §2 / `subsystems/README.md` ownership
  map edits.

## Files touched this session

- `src/FluentGpu.Engine/Scene/SceneStore.cs` (`_aux` wired into ctor/`ResizeColumns`/`CreateNode`)
- `src/FluentGpu.Engine/Scene/SceneStore.Aux.cs` (filled in — was an empty P0 shell)
- `src/FluentGpu.Engine/Dsl/Element.cs` (`Visible` prop)
- `src/FluentGpu.Engine/Reconciler/Reconciler.cs` (`BindPresence`/`ApplyPresenceStatic` call sites in
  `BindNode`/`WriteColumns`; `CompEntry.Hidden` field; `SetSubtreeParked`'s ActiveSig formula; `MountComponent`'s
  ancestor-walk Hidden seed)
- `src/FluentGpu.Engine/Reconciler/Reconciler.Presence.cs` (filled in — was an empty P0 shell)
- `src/FluentGpu.Engine/Reconciler/BindContract.cs` (`MorphVisibleBind`)
- `src/FluentGpu.Engine/Layout/FlexLayout.cs` (`Collapsed`/`FirstVisibleChild`/`NextVisibleSibling`; top-level
  `Measure`/`Arrange` 0×0 short-circuit; every Flex/Wrap/ZStack child-loop header)
- `src/FluentGpu.Engine/Hooks/SkeletonDeriver.cs` (statically-collapsed → nothing)
- `src/FluentGpu.Engine/Hosting/AppHost.cs` (stale `FrameStats` doc comment fix)
- `src/FluentGpu.VerticalSlice/Suites/DiagnosticsSuite.cs` (`gate.diag.counters-always-on`)
- `src/FluentGpu.VerticalSlice/Suites/LayoutShellSuite.cs` (`PresenceChecks` + `PresenceIntervalProbe`)
- `docs/design/subsystems/layout.md` (new §4.7)
- `docs/design/subsystems/reconciler-hooks.md` (bound-channel list)
- `docs/design/subsystems/component-props-contract.md` (§2 presence paragraph)
- `docs/design/subsystems/README.md` (ownership map row)
- `docs/guide/rendering-and-performance.md` (FrameStats field list)
- `.claude/skills/fluentgpu/SKILL.md` (rule 5 extended + new rule 12)

No files deleted or renamed. Nothing in `.native`, `Wavee.PlayPlay`, or `private-runtimes` was read or touched.
No commits were made (per instructions).

## P2 — Bound spans with index-resolved clicks (DONE this session)

Read `docs/design/subsystems/text.md` §8.4 (new, this session) for the full narrative and
`docs/design/subsystems/input-a11y.md` §6.5.2 (new) for the dispatcher's three-reader resolution order — this
section is a pointer + file list + gate names, not a restatement.

### Landed

- **`Foundation/SpanText.cs`**: `TextSpan.IsLink` (bool, default false) + `TextSpan.IsHyperlink` (computed:
  `OnClick is not null || IsLink`) — every "is this span a link" site (shaping's `LinkBit`, the dispatcher's three
  readers, `SameSpanShaping`) now goes through `IsHyperlink` instead of a raw `OnClick is not null` check.
  `readonly struct TextSpans(TextSpan[] Array, int Count)` — implicit from `TextSpan[]`, `AsSpan()`, `Empty`.
  `sealed class SpanBuffer` — `Clear()`/`Add()`/`Current`, grows the backing array at the high-water mark only
  (doubling, floor 4), never shrinks.
- **`Dsl/Element.cs`**: `SpanTextEl`'s primary record parameter is now `Prop<TextSpans> Spans` (was
  `TextSpan[] Spans`); a `SpanTextEl(TextSpan[] spans)` ctor overload keeps every existing call site (4 in the
  engine repo — `RichTextBlock.cs`, `Probes.cs`, `TextSuite.cs`, plus the `SpanTextEl` type itself; 11 in
  Wavee — `TrackRow.cs`, `RichText.cs`, `ArtistPopular.cs`, etc.) compiling UNCHANGED, verified by a clean engine
  solution build AND a clean `Wavee.csproj -c Release` build. New `Action<int>? OnSpanClick { get; init; }`
  property (mount-static).
- **`Scene/SceneStore.cs`**: `_spanText` changed from `Dictionary<int, TextSpan[]>` (retained the caller's array
  by reference) to `Dictionary<int, (TextSpan[]? Arr, int Count)>` (grow-only-capacity, COPIES via
  `SetSpanText(NodeHandle, ReadOnlySpan<TextSpan>)`, same discipline as the pre-existing `_textEditSelRects`
  pooled-slot pattern). `TryGetSpanText` now returns `ReadOnlySpan<TextSpan>` (the live `[0,Count)` prefix, not
  the possibly-oversized backing array). New sparse `_spanClickHandlers` table +
  `SetSpanClickHandler`/`TryGetSpanClickHandler` for `OnSpanClick` (written unconditionally every `WriteColumns`
  pass from the SpanTextEl case, mirroring how an ordinary `OnClick` handler is treated — no bind-effect
  machinery).
- **`Reconciler/Reconciler.cs`**: extracted `WriteSpanText(NodeHandle node, ReadOnlySpan<TextSpan> bodySpans,
  TextSpan[]? suffixSpans, bool inRenderScope)` from the old inline `SpanTextEl` `WriteColumns` case — mints/reuses
  the `SpanRunTable` overlay (shaping-gated via `SameSpanShaping`, now `ReadOnlySpan<TextSpan>`-based), builds the
  concat string through a NEW reused `char[] _spanConcatScratch` field (grow-only) + `StringTable.Intern
  (ReadOnlySpan<char>)` instead of the old `string.Create` (which always allocated even when the concatenated text
  already existed in the table), copies into the scene via `SceneStore.SetSpanText`, and sets
  `InteractionInfo.SpanLinksBit`/`WantsPointer` from `TextSpan.IsHyperlink`. Returns the resolved `SpanRunId` for
  the caller to fold into its own `TextStyle` rebuild — `WriteSpanText` never touches `LayoutInput` itself, since
  every OTHER TextStyle axis (size/weight/wrap/…) is `SpanTextEl`'s static properties, unaffected by a `Spans`
  rebind. `inRenderScope` (true from the static WriteColumns path, false from the bound effect) selects between
  `MarkLayoutShape` (safe only inside an active render/reconcile pass) and a bare `NodeFlags.LayoutDirty` mark
  (the bound-effect-safe form — see `MarkLayoutShape`'s own doc comment, "Deliberately NOT used by the bound …
  effects", the same rule the pre-existing bound `TextEl.Text` effect already follows).
- **`Reconciler/Reconciler.Spans.cs`** (filled in — was an empty P0 shell): `BindSpanText(NodeHandle, SpanTextEl)`
  — one mount-time `Effect` (mirrors the bound `TextEl.Text` effect exactly), equality/shaping-gated through the
  SAME `WriteSpanText`, counted by `NodeBindingFireCount`/`WriteCount`. Wired from `BindNode`'s new
  `else if (el is SpanTextEl st) BindSpanText(node, st);` branch (SpanTextEl previously had no `BindNode` case at
  all — the bound branch is strictly additive).
- **`Foundation/StringTable.cs`**: new `Intern(ReadOnlySpan<char> s)` overload — probes the SAME `_map` by
  content via a cached `Dictionary<string,int>.AlternateLookup<ReadOnlySpan<char>>` (.NET 9+ built-in alternate
  lookup on the ordinal string comparer; zero allocation for the probe itself) and falls through to the ordinary
  `Intern(string)` (which allocates `new string(span)`) ONLY when the content is genuinely new to the table.
- **`Input/InputDispatcher.cs`**: all three hyperlink readers (mouse release ~line 1069, touch tap ~line 2530,
  `HitLinkSpan`'s own eligibility check ~line 3419) now resolve `spans[i].OnClick` first, else
  `SceneStore.TryGetSpanClickHandler(node, out var onSpanClick)` → `onSpanClick(i)`; `HitLinkSpan` itself now
  gates a hit rect on `spans[si].IsHyperlink` (was `spans[si].OnClick is not null`) so an `IsLink`-only span (no
  per-span closure) is still hit-testable.

### Gates (`FluentGpu.VerticalSlice/Suites/TextSuite.cs`, `BoundSpansChecks`, called from `Run` after the
existing `WaveCSpanTextChecks`)

All three green, plus the pre-existing `WC-SPAN.*`/`WC-TXT.*` (rtb-01/rtb-02) checks unaffected:
- `gate.spans.bound-rebind-zero-alloc` — a 1000-row `ItemsView.CreateBound` list (`BoundSpanRowsProbe`), each row
  a `SpanTextEl` whose `Spans` is `Prop.Of(() => a per-slot SpanBuffer refilled from the row's live index)`, 3
  spans/2 links ("Artist", "Album") per row. A 20,000 DIP scroll jump (recycles every realized slot) then 5
  settle frames: the STEADY frame's `HotPhaseAllocBytes == 0`; the RECYCLE frame's `RebindFlushAllocBytes` stays
  ≤ 256 B × realized-row-count (measured ~248 B/row — the unavoidable cost of a genuinely-unique-per-row
  `SpanStyle[]` mint + `SpanRun` object + `StringTable.Intern`'s `new string` for content that differs every
  row; the per-row NUMBER LABEL itself is pre-interned into a `string[1000]` built once at test setup — an ad hoc
  stand-in for the not-yet-landed P3 `FormatCache`, since paying that allocation on every rebind would blow the
  budget and isn't what this gate is meant to catch); `probe.Builds` is unchanged after the jump (recycle = signal
  rebind, never a template rebuild); a click on the SECOND link's seam-published rect (resolved via
  `SpanRunTable.Shared.Resolve(runId)!.Rects`, not a guessed pixel offset — the row's own width is the Stack
  cross-axis's stretched width, not its content width) of WHATEVER row is actually inside the viewport's clip
  band (not merely realized — overscan realizes rows outside the clip too, where a click would miss) reaches
  `OnSpanClick(2)` with that row's CURRENT bound index (read back from its own live text, not from position
  arithmetic — recycled slots keep their pooled DOM position; only their content/index moves).
- `gate.spans.shaping-gate-keeps-run` — a single bound `SpanTextEl` (`OneSpanProbe`) re-fired via an unrelated
  `Signal<int> epoch` bump (content unchanged) ⇒ `SpanRunId` unchanged, that frame's `FrameStats.TextShapes == 0`.
- `gate.spans.scene-owns-copy` — mutating the caller's `SpanBuffer` directly AFTER a fire (no bind re-fire in
  between) leaves `SceneStore.TryGetSpanText`'s returned copy (and the drawn/interned text) unaffected; a
  SUBSEQUENT genuine content change (a real refill + rebind) mints a new, different `SpanRunId`.

### Deviations from the plan's literal phrasing

- The plan's phrase "`WriteSpanText(node, ReadOnlySpan<TextSpan>, suffix, isMount)`" became `(node,
  ReadOnlySpan<TextSpan> bodySpans, TextSpan[]? suffixSpans, bool inRenderScope)` — `isMount` was the wrong axis:
  BOTH WriteColumns' mount AND update calls run inside an active render/reconcile pass (safe for
  `MarkLayoutShape`), while EVERY bound-effect fire — including its initial mount-time `runNow: true` fire, which
  happens moments after `WriteColumns` inside the same `Mount()` call — must stay `MarkLayoutShape`-free per that
  method's own documented rule. `inRenderScope` names the actual distinction; see the code comment on
  `WriteSpanText` and the `text.md` §8.4 writeup.
- `TextSpan` gained the `IsHyperlink` computed property alongside the new `IsLink` field — not explicitly
  requested by the plan, but load-bearing: it is the ONE predicate `WriteSpanText`'s `LinkBit`, all three
  `InputDispatcher` readers, and `SameSpanShaping`'s decoration-equality check now share, so `OnClick`-only links
  (rtb-01, unchanged) and `IsLink`-only links (P2, new) are indistinguishable to every downstream reader.

### Verification run in this session (after the regression fix above, before AND after the doc-only edits)

- `dotnet build src/FluentGpu.slnx` Debug: 0 errors. `-c Release`: 0 errors.
- `dotnet run --project src/FluentGpu.VerticalSlice` Debug: **ALL CHECKS PASSED — 1363 checks** (was 1360 before
  P2's 3 new gates). `-c Release`: same, 1363/1363.
- `dotnet test src/FluentGpu.Engine.Tests/FluentGpu.Engine.Tests.csproj -c Release`: **233/233 passed** (this run
  did not hit the two documented load-flakes from the P1 session; they remain a known pre-existing, order/load-
  dependent flake per CLAUDE.md's rule, unrelated to this session's files).
- `dotnet build C:\wavee\WaveeMusic\src\apps\Wavee\Wavee.csproj -c Release`: **Build succeeded, 0 errors** — all
  11 Wavee `SpanTextEl` call sites compile unchanged against the `Prop<TextSpans>` + ctor-overload surface.
- `powershell -File docs/design/check-canon.ps1`: **Canon OK** (33 docs scanned, no stale tokens) after
  `text.md` §8.4 and `input-a11y.md` §6.5.2.

### Files touched this session (P2, after the regression fix)

- `src/FluentGpu.Engine/Layout/FlexLayout.cs` (the regression fix: `Collapsed` reads `IsCollapsed`, not
  `NodeFlags.Visible`)
- `src/FluentGpu.Engine/Foundation/SpanText.cs` (`TextSpan.IsLink`/`IsHyperlink`, `TextSpans`, `SpanBuffer`)
- `src/FluentGpu.Engine/Foundation/StringTable.cs` (`Intern(ReadOnlySpan<char>)` + the alternate-lookup field)
- `src/FluentGpu.Engine/Dsl/Element.cs` (`SpanTextEl.Spans : Prop<TextSpans>`, the array ctor overload,
  `OnSpanClick`)
- `src/FluentGpu.Engine/Scene/SceneStore.cs` (`_spanText` → pooled-copy slot; `SetSpanClickHandler`/
  `TryGetSpanClickHandler` + `_spanClickHandlers`; node-free teardown)
- `src/FluentGpu.Engine/Reconciler/Reconciler.cs` (`WriteSpanText` extraction + `_spanConcatScratch`;
  `SameSpanShaping` → span-based; the `SpanTextEl` `WriteColumns` case slimmed to call `WriteSpanText` only when
  unbound; `BindNode`'s new `SpanTextEl` branch)
- `src/FluentGpu.Engine/Reconciler/Reconciler.Spans.cs` (filled in — was an empty P0 shell; `BindSpanText`)
- `src/FluentGpu.Engine/Input/InputDispatcher.cs` (three hyperlink readers resolve `OnSpanClick` as the fallback;
  `HitLinkSpan` gates on `IsHyperlink`)
- `src/FluentGpu.VerticalSlice/Suites/TextSuite.cs` (`BoundSpansChecks` + `BoundSpanRowsProbe`/`OneSpanProbe`)
- `docs/design/subsystems/text.md` (new §8.4)
- `docs/design/subsystems/input-a11y.md` (new §6.5.2)

No files deleted or renamed. Nothing in `.native`, `Wavee.PlayPlay`, or `private-runtimes` was read or touched.
No commits were made (per instructions).

## P3 — Typed bound-item authoring API + engine-owned gating, caches, recycle rules, bound controls (DONE this session)

Read `docs/design/subsystems/virtualization.md` §3.4 "Typed bound authoring" (new) and §5.5 "Recycle snaps
transitions" (new) for the full narrative and exact code references — this section is a pointer + file list + gate
names + deviations, not a restatement.

### Landed

1. **`BoundItemsSource<T>.BindItem` gated overload** (`FluentGpu.Controls/BoundItemsSource.cs`):
   `IReadSignal<T> BindItem(IReadSignal<int> slotIndex, ReactiveRuntime runtime, int itemStartIndex = 0,
   IEqualityComparer<T>? comparer = null)` returns a `Memo<T>` (equality-gated by construction — an equal
   recompute stays clean and never propagates to subscribers, per `Memo<T>`'s own push/pull cut-off doc). The
   OLD ungated `BindItem(IReadSignal<int>, int)` is kept verbatim for non-hosted callers with no `ReactiveRuntime`
   at hand. `ItemsView.CreateBound<T>` (`ItemsView.cs`) always uses the gated overload now, sourcing the runtime
   from a NEW `RowScope.Runtime` (`SelectorVisualsBound.cs`) — an `init`-only property appended to the existing
   positional `RowScope` record struct (so every existing `new RowScope(...)` call site compiles unchanged), set
   by `ItemsView.Render()`'s bound realize path (`Runtime = Context.Runtime`) since `Component.Context.Runtime`
   is the only place a `ReactiveRuntime` is reachable from inside `rowBind`'s closure (that delegate executes
   later, inside the reconciler's `RealizeBoundWindow`, which has no hook context of its own). `ListOptions<T>`
   gained `IEqualityComparer<T>? ItemComparer` (`ListOptions.cs`), threaded through to `BindItem`.
2. **`FluentGpu.Controls/BoundItemScope.cs`** (new, `BoundItemScopeExtensions`): `Text(sel)`, `Text<TKey>(keySel,
   FormatCache<TKey>, formatter)` (see deviation #1 below), `Color(sel)`, `Opacity(sel)`, `Show(sel)` (→
   `Visible`), `Image(sel)` (null/empty → `""`), `Spans(Action<T,SpanBuffer>)` (one `SpanBuffer` per call site
   per slot), `Number(sel)` (via `FormatCache.Int`), `Duration(selMs)` (via `FormatCache.DurationMmSs`),
   `Value<TVal>(sel)`, `Signal<TVal>(sel, comparer?)` (a gated `Memo<TVal>` for a sub-component, using
   `scope.Row.Runtime`), `Invoke(Action<T>)`/`Invoke(Action<T,PointerEventArgs>)` (resolve `Item.Peek()` — never
   `.Value` — at invocation), `InvokeSpan(Action<T,int>)`, `ShowWhen(pred, build)` (= `Flow.Show`). Every helper
   allocates exactly one closure at template-build time.
3. **`FluentGpu.Engine/Foundation/FormatCache.cs`** (new): `FormatCache<TKey>` — bounded `Dictionary<TKey,string>`
   (`Capacity` = 4096, a miss past the cap clears the whole table), `Get(key, formatter)`. `FormatCache.Int` — a
   dense, grow-only, NEVER-cleared `string[]` (floor 64, doubling) for small non-negative ints. `FormatCache.MmSs`/
   `HhMmSs` — the two shared `FormatCache<int>` instances behind `FormatCache.DurationMmSs`/`DurationHhMmSs`.
   `FormatCache.Create<TKey>(comparer?)` is the factory `Text<TKey>` callers use to hoist their own instance.
   `TextSuite.cs`'s P2 gate `gate.spans.bound-rebind-zero-alloc` was updated to route its per-row label through a
   (pre-warmed) `FormatCache<int>` instead of the ad hoc pre-built `string[1000]` it used before P3 landed — same
   budget, same result, now the REAL primitive.
4. **Equality gating on the six/seven still-ungated static bound channels in `BindNode`** (`Reconciler.cs`):
   `HoverFill`, `PressedFill`, `BorderColor`, `Corners`, `RadialGradientCenter` (compare via
   `SceneStore.TryGetRadialGradientCenter`, not just presence), `TextEl.Color`, `ImageEl.Placeholder` — each now
   compares the resolved value against the live `NodePaint` field (or, for `RadialGradientCenter`, the sparse
   table) BEFORE writing and marking `PaintDirty`, mirroring the shape `Fill`/`Opacity`/`Transform` already used.
   (`IconLayerEl.Tint` was already equality-gated coming into this session — verified, not re-touched.) So
   `NodeBindingWriteCount` now counts REAL writes for all of them, not "fired == wrote" unconditionally.
5. **Recycle snaps transitions**: `TreeReconciler.SuppressBoundTransitions` (`Reconciler.cs`) — a re-entrancy-safe
   depth counter with an `IDisposable` RAII scope (`PushSuppressBoundTransitions()`), pushed by the host around
   (a) `AppHost.FlushRebindsToQuiescence`'s `_runtime.Flush()` call (`AppHost.cs`) — the call that actually RUNS a
   recycled slot's rebound channel effects — and (b) `TreeReconciler.RealizeWindow`'s single bound-realize
   dispatch site (the one call that fans out to `RealizeBoundWindow`/`RealizeBoundWindowExtended`/
   `RealizeBoundWindowWithPersistentPrefix` — covers both `RebindBoundSlot` recycles AND a genuinely new slot's
   cold `Mount` inside the same realize pass). `Reconciler.Presence.cs`'s `BindPresence` now guards its
   false→true Enter seed with `&& SuppressBoundTransitions == 0 &&` — the ONLY bound-channel transition seed that
   exists in the tree today (no bound `Fill`/`BorderColor`/etc. seeds a `BrushFade`/spring yet — that is a P5-era
   addition per the plan's row-template work; the counter and the rule are landed and ready for it).
6. **Shape-stable bound controls**: `PersonPicture.Bound(Prop<string> displayName, Prop<string> imageUrl,
   float size = 96f, ColorF? fill = null)` (`PersonPicture.cs`) — always an `ImageEl` (bound `Source`, empty ⇒
   paints nothing) OVER an initials `TextEl` whose `Visible` is bound to "image empty"; no group/badge support
   (not needed anywhere yet — use `Create` for that). `ToolTip.Wrap(Element target, Prop<string?> text, float
   grow = 0f, float showDelayMs = float.NaN)` (`ToolTip.cs`) — a NEW `ToolTipBoundSlots` record + a third
   `Render()` branch; null/empty resolves to no hover/focus/press/safe-zone wiring THAT RENDER (see deviation #2
   below for the hook-order-safety mechanics), a later non-empty render wires up with no remount. The component
   form (`Wrap(Element, string, ...)`, `WrapStable`) is untouched and still the right choice for a non-list caller.

### Gates (new `Suites/BoundTemplateSuite.cs`, registered as `bound` in `SuiteRegistry.cs`)

All seven green (`dotnet run --project src/FluentGpu.VerticalSlice --suite bound` → `ALL CHECKS PASSED (suite=bound,
10 checks)` — 3 of the 10 are setup preconditions inside `gate.bound.changed-item-fires-only-its-slot`/
`gate.bound.handlers-resolve-at-invocation`, not separate named gates):
- `gate.bound.typed-template-zero-alloc` — a 1000-row `CreateBound<BoundItem>` template (~15 distinct bound
  channels/row across every `BoundItemScopeExtensions` helper — see deviation #3 below on the "≈30" figure); a
  5-row shift then 3 settle frames ⇒ `HotPhaseAllocBytes == 0` on the settled frame, `RebindFlushAllocBytes ≤
  2048` on the shift frame, `probe.Builds` unchanged (recycle never rebuilds the template).
- `gate.bound.equal-republish-fires-nothing` — a republished snapshot (new `List<T>` instance, identical
  per-index values) ⇒ that frame's `FrameStats.BindingFires == 0`. (Deviation #4: reads the FRAME's OWN
  `BindingFires`/`BindingWrites`, not a before/after delta — see below.)
- `gate.bound.changed-item-fires-only-its-slot` — mutating ONE realized index's fields ⇒ that frame's
  `BindingWrites > 0` but stays far below "every realized row wrote" (`< realizedRows * 4`).
- `gate.bound.transition-snaps-on-recycle` — a live (non-recycle) `Visible` false→true edge on an
  already-realized row's node seeds a real `AnimEngine` track (checked via
  `AnimEngine.TryGetTrackValue(node, AnimChannel.Opacity, out _)` on THAT SPECIFIC node — not the global
  `HasActive`, which a scroll jump can also move via unrelated tracks like a scrollbar fade, see deviation #5);
  the SAME persistent slot node, reached through a bound-window recycle instead (a far-away range mutated to the
  same target value, then scrolled into view so every visible slot rebinds onto it), seeds NOTHING on that node.
- `gate.bound.format-cache-bounded` — a `FormatCache<int>` driven past `Capacity` (4096) never exceeds it
  (`Count <= Capacity`, i.e. it cleared rather than growing unbounded); `FormatCache.Int` returns the SAME string
  instance for a repeated key (never clears).
- `gate.bound.handlers-resolve-at-invocation` — clicking a realized slot resolves `item.Invoke`'s current item;
  the SAME slot, recycled to a different logical index between mount and click (scroll far, click the same
  screen position again), resolves the NEW current item — never the one captured when the template built the
  closure.
- `gate.bound.personpicture-tooltip-shape-stable` — `PersonPicture.Bound` + `ToolTip.Wrap`'s bound overload
  flipping image URL / tooltip text (empty ↔ non-empty, several times) never changes `SceneStore.LiveCount`.

### Deviations from the plan's literal phrasing

1. **`BoundItemScope<T>.Text<TKey>(keySel, FormatCache<TKey>)`** (plan's literal 2-arg form) became
   `Text<TKey>(keySel, FormatCache<TKey>, Func<TKey,string> formatter)` (3-arg) — `FormatCache<TKey>.Get` is
   stateless w.r.t. the formatter (it takes one per call, like `Dictionary.GetOrAdd`), so the cache instance is
   reusable across different formatting needs and the formatter is supplied at the call site, matching
   `FormatCache.DurationMmSs`'s own internal `MmSs.Get(seconds, static s => ...)` shape. A "cache owns its own
   formatter" design (matching the plan literally) was considered and rejected — it would mean a SEPARATE
   `FormatCache<TKey>` instance per distinct formatting need even when the key domain is shared, which is a worse
   fit for `virtualization.md`'s own "hoist and reuse ONE instance per call site" guidance than a cache that takes
   its formatter per `Get`.
2. **`ToolTip.Wrap`'s bound overload keeps a component per target**, not the plan's aspirational "no component
   per target — the tooltip service reads the bound text at hover time." Implementing a real hover-time lookup
   service (the overlay/hover machinery querying a Prop directly instead of a mounted `ToolTip` component
   instance) is a materially bigger change to `ToolTip.cs`'s ~600-line hover/focus/press/safe-zone state machine
   than fits this phase's budget alongside the other six P3 items; what IS implemented — null/empty resolving to
   zero wiring cost, reactively, on the SAME reused component — captures the practical win (a per-row tooltip
   that is usually empty costs almost nothing) without the redesign. Documented as an open item, not silently
   dropped. The null/empty short-circuit is applied AFTER every `Use*` hook call in `Render()`, not before — a
   conditional early return before a hook call would violate the "hooks run in stable order" invariant (skill
   rule 7) whenever the bound text's emptiness varies between renders of the SAME mounted component; only the
   FINAL wrap's event-handler wiring (`OnHoverMove`/`OnPointerExit`/`OnPointerPressed`/`OnFocusChanged`/the
   `clock` child) is conditioned on the resolved `hasTooltip` flag.
3. **The zero-alloc gate's row template has ~15 distinct bound channels, not "≈30."** The plan's own phrasing
   frames 30 as an order-of-magnitude target ("≈30 channels per row"), not a literal count to hit; the probe
   covers every helper in `BoundItemScopeExtensions` at least once (`Text`, `Number`, `Color` ×2, `Opacity`,
   `Show` ×2, `Image`, `Spans`, `Duration`, `Text<TKey>`, `Invoke` (click), `Invoke` (pointer), `InvokeSpan`,
   `ShowWhen`+`Signal`), which is what actually exercises the zero-alloc claim; padding it to a literal 30 with
   redundant repeats of the same helper would not change what the gate proves.
4. **`gate.bound.equal-republish-fires-nothing`/`changed-item-fires-only-its-slot` read `FrameStats.BindingFires`/
   `.BindingWrites` from the frame's own `RunFrame()` return, not a before/after `NodeBindingFireCount` delta.**
   `TreeReconciler.NodeBindingFireCount`/`WriteCount` are PER-FRAME counters (reset every `BeginRenderCensus`,
   i.e. every `Paint`/`RunFrame` — see the P0-era doc comment on those fields) — a naive
   `before = reconciler.NodeBindingFireCount; …; after = reconciler.NodeBindingFireCount;` across a
   `host.RunFrame()` call compares "whatever was left over from a PRIOR frame" against "this frame's total," which
   is not a meaningful delta at all (an early version of this gate hit exactly this bug: `fireCount 28->0`, i.e.
   `before` was actually a stale value from a prior frame that had already been reset before the assignment
   executed). `FrameStats.BindingFires`/`BindingWrites` are the documented, correct, per-frame accessors.
5. **`gate.bound.transition-snaps-on-recycle` checks a specific node's `AnimEngine` track, not `AnimEngine.HasActive`.**
   An early version used the global `HasActive` flag and got a false failure: scrolling the list itself can
   perturb OTHER animation tracks (e.g. a scrollbar visibility fade) that have nothing to do with the Enter-seed
   rule under test, so `HasActive` stayed `true` across the "recycle" case even though the SuppressBoundTransitions
   rule was working correctly. `AnimEngine.TryGetTrackValue(node, AnimChannel.Opacity, out _)` on the exact
   persistent slot node isolates the claim to the thing actually being tested. This also required mutating only a
   FAR-AWAY, never-yet-realized index range to the target value before scrolling there (mutating already-visible
   rows' values directly would flip them through the ordinary, UNSUPPRESSED `FlushHosted` reactive path — a real
   live edge, correctly seeding — contaminating the "recycle" reading with a live-path false positive).

### `UseTimeout`/`UseKeyframes` activity gating (plan item 7 — left open, per the plan's own fallback)

Not attempted this session. Per the P1 session's own scoping (see that section above): `UseTimeout` is scheduled
by absolute due-time in `HostTimerQueue`, so "pausing" a one-shot needs a remaining-time-tracking redesign, not a
boolean gate; `UseKeyframes` seeds an `AnimEngine` track directly at `Component.cs:919` with no existing
presence-aware parking hook (`AnimEngine` already has a `NodeFlags.Parked`-driven parking path via
`OnNodeParkedChanged` — wiring presence into it needs a decision on whether a collapsed node's mid-flight
keyframe track resumes from where it left off or restarts, which is a real design choice, not a mechanical
change). Both remain accurately described as "not small" — `entry.Hidden` and `RenderContext.UseIsActive()` are
already available for whichever design a future session picks.

### Verification run in this session

- `dotnet build src/FluentGpu.slnx` (Debug): **0 errors**. `-c Release`: **0 errors**.
- `dotnet run --project src/FluentGpu.VerticalSlice` (Debug): **ALL CHECKS PASSED — 1373 checks** (was 1363 before
  P3's 10 new `bound`-suite gates). One transient unrelated failure was seen mid-session
  (`gate.path.fill.winding [ring-evenodd]`, a random-seeded geometry gate with no relationship to any file this
  session touched) and confirmed a pre-existing flake by re-running alone (`--suite path`, 7/7 green) and the full
  suite again (1363/1363 green before the `bound` suite existed, then 1373/1373 after) — not caused by this
  session's changes.
- `dotnet run --project src/FluentGpu.VerticalSlice -c Release`: **1373/1373**, same as Debug.
- `dotnet run --project src/FluentGpu.VerticalSlice --suite bound` (both configs): **ALL CHECKS PASSED (suite=bound,
  10 checks)**.
- `dotnet test src/FluentGpu.Engine.Tests/FluentGpu.Engine.Tests.csproj -c Release`: **233/233 passed** (no
  load-flakes hit this run).
- `dotnet build C:\wavee\WaveeMusic\src\apps\Wavee\Wavee.csproj -c Release`: **Build succeeded, 0 errors** — the
  app still compiles against the engine's additive-only new surface (`BoundItemsSource<T>.BindItem` gated
  overload, `BoundItemScopeExtensions`, `FormatCache`/`FormatCache<TKey>`, `RowScope.Runtime`,
  `ListOptions<T>.ItemComparer`, `TreeReconciler.SuppressBoundTransitions`, `PersonPicture.Bound`,
  `ToolTip.Wrap(Element, Prop<string?>, ...)`).
- `powershell -File docs/design/check-canon.ps1`: **Canon OK** (33 docs scanned, no stale tokens) after
  `virtualization.md` §3.4/§5.5/§9 and `controls.md`'s ToolTip/PersonPicture bound-form additions.

### Files touched this session (P3)

- `src/FluentGpu.Controls/BoundItemsSource.cs` (gated `BindItem` overload)
- `src/FluentGpu.Controls/BoundItemScope.cs` (new — `BoundItemScopeExtensions`)
- `src/FluentGpu.Controls/SelectorVisualsBound.cs` (`RowScope.Runtime`)
- `src/FluentGpu.Controls/ItemsView.cs` (`CreateBound<T>` uses the gated overload; the bound `rowBind` closure
  stamps `RowScope.Runtime`)
- `src/FluentGpu.Controls/ListOptions.cs` (`ListOptions<T>.ItemComparer`)
- `src/FluentGpu.Controls/PersonPicture.cs` (`PersonPicture.Bound`)
- `src/FluentGpu.Controls/ToolTip.cs` (`ToolTipBoundSlots` + the bound `Wrap` overload + `Render()`'s
  `hasTooltip`-gated final wrap)
- `src/FluentGpu.Engine/Foundation/FormatCache.cs` (new)
- `src/FluentGpu.Engine/Reconciler/Reconciler.cs` (equality gating on HoverFill/PressedFill/BorderColor/Corners/
  RadialGradientCenter/TextEl.Color/ImageEl.Placeholder; `SuppressBoundTransitions` + its RAII scope; the
  `RealizeWindow` bound-dispatch wrap; `using System;` added)
- `src/FluentGpu.Engine/Reconciler/Reconciler.Presence.cs` (`BindPresence`'s Enter seed checks
  `SuppressBoundTransitions == 0`)
- `src/FluentGpu.Engine/Hosting/AppHost.cs` (`FlushRebindsToQuiescence` wraps `_runtime.Flush()` in
  `PushSuppressBoundTransitions()`)
- `src/FluentGpu.VerticalSlice/Suites/BoundTemplateSuite.cs` (new — the `bound` suite, 7 gates)
- `src/FluentGpu.VerticalSlice/Harness/SuiteRegistry.cs` (registers `bound`)
- `src/FluentGpu.VerticalSlice/Suites/TextSuite.cs` (`gate.spans.bound-rebind-zero-alloc`'s label array →
  `FormatCache<int>`)
- `docs/design/subsystems/virtualization.md` (new §3.4 "Typed bound authoring", new §5.5 "Recycle snaps
  transitions", §9 zero-alloc-story addendum)
- `docs/design/subsystems/controls.md` (ToolTip §6.4 bound-form bullet; new "PersonPicture.Bound" subsection)
- `.claude/skills/fluentgpu/SKILL.md` (rule 13; the typed-bound-row snippet in the cheat sheet)

No files deleted or renamed. Nothing in `.native`, `Wavee.PlayPlay`, or `private-runtimes` was read or touched.
No commits were made (per instructions).

## P4 — Incremental layout by default (DONE this session)

Read `docs/design/subsystems/layout.md` §4.8 (new, this session) for the full narrative and exact mechanics — this
section is a pointer + file list + gate names + deviations + the regression story, not a restatement.

### Landed

1. **Subtree-dirty propagation** (`src/FluentGpu.Engine/Scene/SceneStore.Aux.cs`, `SceneStore.cs`):
   `AuxFlags.SubtreeLayoutDirty` (reserved since P1) is now live. `SceneStore.Mark(h, NodeFlags.LayoutDirty)`'s 0→1
   edge calls the new `MarkSubtreeLayoutDirtyChain(idx)` — walks from `h`'s PARENT upward setting the bit, stopping
   at the first already-set ancestor. `ClearLayoutDirty()` (called once per frame after layout runs) walks the
   mirror `ClearSubtreeLayoutDirtyChain` for every worklist entry. `SceneStore.IsLayoutClean(h)` is the combined
   read (`!LayoutDirty(h) && !SubtreeLayoutDirty(h)`). Structural edits needed NO special-case code — they already
   route through `Mark(…, LayoutDirty)`.
2. **Arranged-rect validity**: `FlexLayout._arranged` (new `RectF[]`, grown alongside `_memo` in `BeginMeasurePass`)
   — the LAST rect an `Arrange` call actually placed each node at, written ONLY by `SetArrangedBounds` (unlike
   `scene.Bounds(node)`, which `Measure` also scribbles hypothetical W/H into). `AuxFlags.ArrangedValid` (reserved
   since P1) + `SceneStore.SetArrangedValid`/`IsArrangedValid` track whether the slot holds a real value (cleared on
   `CreateNode`, the whole `_aux` byte zeroed there — no P4-specific clear needed).
3. **The Arrange early-out** (`FlexLayout.cs`, top of `Arrange`, after the P1 collapsed short-circuit): clean
   subtree ∧ `ArrangedValid` ∧ same incoming rect ∧ `!HasScrollInSubtree` ⇒ return after re-asserting
   `scene.Bounds(node)` and delivering any pending `OnBoundsChanged` (`DeliverPendingBoundsChangedIfAny`, new). Does
   NOT increment `_dArrange`/`_dMeasure` (a skip is not a measure/arrange).
4. **`AuxFlags.HasScrollDescendant` — a correctness fix, not an optimization** (`SceneStore.Aux.cs`,
   `SceneStore.cs`'s `ScrollRef`): a PERMANENT, monotonic bit set the first time a viewport is created under a
   node, walking ancestors (never cleared — a later-removed viewport just costs a few foregone early-outs, never a
   correctness gap). `SceneStore.HasScrollInSubtree(h) = HasScroll(h) || the bit`. **Why this exists**: a scroll
   viewport's `ArrangeViewport` has continuous per-frame obligations (`SetFrame`, `NeedsRealize`,
   `VirtualRangeDirty`) that are NOT `LayoutDirty`-gated (scrolling is deliberately layout-free). Without this
   exclusion, a clean-and-unchanged ANCESTOR of a viewport (e.g. a full-bleed crossfade wrapper) takes the early-out
   and never recurses down to the viewport at all — silently starving it forever. Found via a REAL regression this
   session (see "The regression" below), not by inspection — a reminder that this class of bug is exactly why
   `docs/plans/…progress.md`'s task brief says "a stale-layout bug is worse than a slow frame."
5. **Measure cross-pass ring** (`FlexLayout.cs`, `MeasureMemo` struct extended with `Ring0*`/`Ring1*`: `NodeGen`,
   `AvailW`, `W`, `H`, `Sig`): `TryRingHit`/`StoreRing`, checked between the within-pass-memo miss and the
   viewport/grid/zstack dispatch (same exclusion scope as the pre-existing within-pass memo and
   `TryResolveSizeStable`). A hit requires `IsLayoutClean(node)` AND a matching `(NodeGen, AvailW)` slot AND a
   matching `LayoutSig` hash (defense in depth beyond the dirty-marking discipline) — returns the stored size
   WITHOUT DESCENDING. `StoreRing` always refreshes both slots at the end of a real measure; validity is gated
   entirely at read time via the clean check.
6. **Virtual rows — verified to need NO bespoke code**: `ArrangeVirtualLayout`/`ArrangeVirtualVariable`/
   `ArrangeVirtualMeasured`/`RefreshNaturalMeasuredStack` all call the GENERIC `Measure`/`Arrange` entry points per
   realized row (confirmed by reading every one), so the ring + early-out apply automatically. Realize marks
   (`RealizeBoundWindow` and neighbors) were verified ALREADY conditional on structural/order/window change from
   prior sessions — nothing to change.

### The regression: a real correctness bug the early-out introduced, found and fixed before landing

`gate.semantic-zoom.reduced-motion` (pre-existing, `NavSuite.cs`) started failing once the Arrange early-out was
live — NOT a flake (deterministic, reproduced on every run). Root-caused by bisecting the early-out and the ring
independently (temporarily `false &&`-gating each), then instrumenting `Arrange`'s entry with a DEBUG-only trace:
a `SemanticZoom` control keeps BOTH its zoomed-in and zoomed-out `Flow.KeepAlive` branches mounted permanently,
crossfading them via an Opacity/Scale `LayoutTransition` (never touches `LayoutInput`) inside a full-bleed wrapper
`BoxEl` that occupies the identical rect regardless of which branch is "active." Once that wrapper settled (clean,
unchanged rect), the early-out correctly-by-its-own-logic skipped it — but its child, an `ItemsView` scroll
viewport several levels down, never got another `Arrange` call again, so it stopped posting `SetFrame` and stopped
checking `NeedsRealize` — the overview list silently froze (recorded glyph count went from a healthy crossfade
count to a permanent 0 the frame the wrapper became clean, confirmed via a temporary debug trace of
`HeadlessGpuDevice.LastGlyphs` at each step of the failing test). Fixed by item 4 above
(`AuxFlags.HasScrollDescendant`/`HasScrollInSubtree`). Verified: full VerticalSlice suite green (Debug + Release,
1379/1379 both), the specific gate passes deterministically across repeated runs.

### Deviations from the plan's literal wording

1. **"the measured seam feeds `SetMeasured` from `_arranged[rc].H`"** is satisfied INDIRECTLY: `Measure(rc)` itself
   returns the ring-cached value without descending when clean, so `layout.SetMeasured(index, measured.Height,
   cross)` call sites in `ArrangeVirtualVariable`/`ArrangeVirtualMeasured`/`RefreshNaturalMeasuredStack` needed no
   change — reading through `Measure`'s cache is equivalent and keeps one fewer code path than adding a second
   `_arranged`-reading branch at each call site.
2. **`TextMeasureCache`'s 2-entry ring (`Scene/Columns.cs`) — NOT landed this session.** Scoped, investigated
   (`TextMeasureCache` is a single-slot struct in a `ColdSlab<TextMeasureCache>`; the RECORDER reads it via
   `SceneRecordingSnapshot.MeasureCacheRef` — a SEPARATE captured-copy array, `_measurement`, not the live
   `SceneStore` one), but not implemented: turning it into a ring means the recorder's read site
   (`SceneRecorder.cs:2029`, `ref TextMeasureCache mc = ref scene.MeasureCacheRef(node); float effSize = mc.Valid &&
   mc.FitSize > 0f ? mc.FitSize : …`) needs to resolve WHICH of the two slots matches the arranged width, not just
   read "whichever was written last" (today's implicit, currently-correct-by-accident behavior for a single slot).
   That touches `SceneStore.cs`, `SceneRecordingSnapshot.cs`, `FlexLayout.cs`'s text-measure site, AND
   `SceneRecorder.cs` — four files with real text-rendering risk (auto-fit `FitSize`, underline/strikethrough
   metrics) — assessed as not fitting this session's remaining budget after the regression investigation above.
   Open for a follow-up; `gate.layout.text-cache-ring` (the plan's named gate) was NOT added.
3. **DEBUG `FG_LAYOUT_VERIFY` runtime parity oracle + the DEBUG dirty-mark tripwire — NOT landed this session.**
   Implementing a true "from-scratch re-solve into scratch bounds mid-frame, compare, without corrupting the live
   `scene.Bounds` column" oracle is a materially separate piece of engineering (Measure/Arrange write directly into
   the shared `SceneStore.Bounds` column; a scratch pass needs either a second column or a save/restore dance
   around every write site) that risks destabilizing the exact code this session just changed. Instead,
   `gate.layout.parity-from-scratch` (landed, see below) proves the SAME claim — the incremental path never
   diverges from a full solve — as a VerticalSlice harness gate: 20 random scoped width edits on a small tree,
   each re-solved through the incremental (ring + early-out) path, compared node-by-node against an INDEPENDENT
   from-scratch build of the identical final state. This is arguably a BETTER-isolated test (no risk of the oracle
   itself perturbing the thing it's checking) though it doesn't cover the "any live scene, any frame" generality
   `FG_LAYOUT_VERIFY` would. The dirty-mark tripwire (a DEBUG assert that every `LayoutInput` writer calls `Mark`)
   was not attempted — would need to enumerate every write site across the reconciler and bound-effect paths;
   scoped as a follow-up, not attempted.
4. **`gate.layout.virtual-clean-rows-skipped`'s literal "one-row scroll"** became a deterministic 48-sibling-row
   test (one row's bound title changes, not routed through `ItemsView`/`ScrollKernel`) — see the gate list below
   for why: the ItemsView/ScrollKernel recycle path was found to have its own realize-timing latency (`NeedsRealize`
   is detected INSIDE `ArrangeViewport` and only consumed by `ReRealizeVirtuals` the FOLLOWING frame) that made a
   literal one-row-scroll assertion either vacuous (0 work, because the scroll never even reached a layout pass in
   the headless harness within 40 settle frames — investigated but not root-caused; a pure scroll with nothing else
   dirty may simply never trigger `layoutNeeded` in this specific harness setup) or dependent on exact overscan/
   stagger timing unrelated to what P4 owns. The deterministic stand-in exercises the IDENTICAL Measure/Arrange
   fan-out per row (~12 nodes) and proves the same claim (one row's content change costs O(1 row), not O(48 rows))
   without depending on that separate subsystem's timing. This is a genuine, not-yet-closed gap in test coverage
   for the literal virtualization-recycle path — see "Open items" below.

### Gates (`src/FluentGpu.VerticalSlice/Suites/LayoutIncrementalSuite.cs`, new, registered as `layout-inc`)

All 6 checks green (`dotnet run --project src/FluentGpu.VerticalSlice --suite layout-inc` →
`ALL CHECKS PASSED (suite=layout-inc, 6 checks)`):
- `gate.layout.arrange-early-out` — a bound-width leaf 6 `BoxEl` levels inside a fixed-size `ClipToBounds` boundary;
  dirtying ONLY the leaf ⇒ `ArrangeCount<=10`, `MeasureCount<=10` (measured: `arrangeCount=8 measureCount=9` — the
  boundary + 6 wrappers + leaf + the host's own root, not the whole app tree).
- `gate.layout.subtree-bits-clear` — after the same dirty-leaf scenario fully settles (3 frames), no node ANYWHERE
  in the tree still has `AuxFlags.SubtreeLayoutDirty` set, and the root reads `IsLayoutClean`.
- `gate.layout.resize-relayouts-all-changed` — a window resize (300→500px) genuinely re-arranges every one of 6
  stretched rows to the new width — the early-out never mistakes "settled once before" for "still correct after a
  real geometry change."
- `gate.layout.virtual-clean-rows-skipped` — 48 sibling rows (~12 nodes/row); one row's bound `TextEl.Text` changes
  ⇒ `MeasureCount<=20`, `ArrangeCount<=20`, `TextShapes<=2` (measured: `measure=6 arrange=4 textShapes=1`, vs. what
  a non-incremental pass would cost for 48×12≈576 nodes), and every OTHER row's `Bounds` is byte-identical (not
  merely recomputed-equal — the early-out proves it was never touched).
- `gate.layout.parity-from-scratch` — 20 random scoped width edits on a 12-leaf flat tree, each re-solved through
  the incremental path (ring + early-out both live), land on the EXACT same `Bounds` (X/Y/W/H, 0.01px tolerance) an
  independent from-scratch `FlexLayout` solve of the identical final widths produces.
- Precondition check (all 48 rows mounted) — counted as one of the 6.

Every pre-existing gate stays green: VerticalSlice Debug 1379/1379 (was 1373 before P4's 6 new gates), Release
1379/1379, both "ALL CHECKS PASSED". (One `HooksSuite.ResourceChecks` segfault was observed on a single full-suite
run under load and did NOT reproduce on three subsequent full runs nor when the `hooks` suite ran alone twice —
treated as a pre-existing load-flake per CLAUDE.md's documented rule, not a P4 regression; it touches resource-hook
async plumbing, nothing this session's files.)

### Verification run in this session

- `dotnet build src/FluentGpu.slnx` (Debug): **0 errors** (only pre-existing warnings, none in touched files).
- `dotnet build src/FluentGpu.slnx -c Release`: **0 errors**.
- `dotnet run --project src/FluentGpu.VerticalSlice` (Debug), 3 consecutive full runs: **1379/1379 ALL CHECKS
  PASSED** on 2 of 3 (the 3rd hit the `HooksSuite.ResourceChecks` segfault noted above, not reproduced since).
- `dotnet run --project src/FluentGpu.VerticalSlice -c Release`: **1379/1379 ALL CHECKS PASSED**.
- `dotnet run --project src/FluentGpu.VerticalSlice --suite layout-inc` (both configs): **ALL CHECKS PASSED
  (suite=layout-inc, 6 checks)**.
- `dotnet test src/FluentGpu.Engine.Tests/FluentGpu.Engine.Tests.csproj -c Release`: **232/233 passed** — the ONE
  failure, `HeadlessPlayerTests.Facade_RoutedBackendDrivesForwardedSignals` (a media-facade timeout), is the SAME
  documented pre-existing load-flake from the P1/P2 sessions (confirmed: **passes individually**,
  `--filter FullyQualifiedName=…`, 22ms) — unrelated to any file this session touched.
- `dotnet build C:\wavee\WaveeMusic\src\apps\Wavee\Wavee.csproj -c Release`: **Build succeeded, 0 errors** — the
  app compiles unchanged against the engine's additive-only new surface (`SceneStore.IsSubtreeLayoutDirty`/
  `IsLayoutClean`/`IsArrangedValid`/`SetArrangedValid`/`HasScrollInSubtree`; `FlexLayout`'s internals are private).
- `powershell -File docs/design/check-canon.ps1`: **Canon OK** (33 docs scanned, no stale tokens) after
  `layout.md` §4.6 (clarifying pointer) and the new §4.8.

### Files touched this session (P4)

- `src/FluentGpu.Engine/Scene/SceneStore.cs` (`Mark`'s `MarkSubtreeLayoutDirtyChain` call on the LayoutDirty 0→1
  edge; `ClearLayoutDirty`'s `ClearSubtreeLayoutDirtyChain` call per worklist entry; `ScrollRef`'s
  `MarkScrollDescendantChain` call on first viewport creation)
- `src/FluentGpu.Engine/Scene/SceneStore.Aux.cs` (`AuxFlags.HasScrollDescendant` new bit;
  `IsSubtreeLayoutDirty`/`IsLayoutClean`/`IsArrangedValid`/`SetArrangedValid`/`HasScrollInSubtree`;
  `MarkSubtreeLayoutDirtyChain`/`ClearSubtreeLayoutDirtyChain`/`MarkScrollDescendantChain`)
- `src/FluentGpu.Engine/Layout/FlexLayout.cs` (`MeasureMemo`'s `Ring0*`/`Ring1*` fields; `TryRingHit`/`StoreRing`;
  the ring-hit check in `Measure`; `_arranged` array + its growth in `BeginMeasurePass`; `SetArrangedBounds` writes
  `_arranged`+`ArrangedValid`; `DeliverPendingBoundsChangedIfAny`; the early-out block at the top of `Arrange`)
- `src/FluentGpu.VerticalSlice/Suites/LayoutIncrementalSuite.cs` (new — the `layout-inc` suite, 6 checks)
- `src/FluentGpu.VerticalSlice/Harness/SuiteRegistry.cs` (registers `layout-inc`)
- `docs/design/subsystems/layout.md` (new §4.8; a clarifying pointer in §4.6 distinguishing the reconciler's
  `SubtreeDirty` from P4's `AuxFlags.SubtreeLayoutDirty`)

No files deleted or renamed. Nothing in `.native`, `Wavee.PlayPlay`, or `private-runtimes` was read or touched. No
commits were made (per instructions). `FlexLayout.Incremental.cs` (the P0 empty shell) was left untouched — every
P4 change landed in `FlexLayout.cs`/`SceneStore.cs`/`SceneStore.Aux.cs` directly rather than that shell, since the
mechanisms are small, tightly coupled additions to existing methods (`Mark`, `Measure`, `Arrange`,
`SetArrangedBounds`) rather than free-standing new methods that would naturally live in a partial-class file.

### Open items for the next agent

- **`TextMeasureCache`'s 2-entry ring** (plan item 7, `gate.layout.text-cache-ring` not added) — see deviation #2
  above for the exact scope (`Scene/Columns.cs`'s `TextMeasureCache` struct → a 2-slot ring;
  `SceneStore.MeasureCacheRef`/`SceneRecordingSnapshot.MeasureCacheRef`; the read site in `FlexLayout.cs`'s text
  measure block, ~line 373; the recorder's read site `SceneRecorder.cs:2029` needs a maxW-aware resolve, not a
  bare `ref` read).
- **`FG_LAYOUT_VERIFY` DEBUG parity oracle + the DEBUG `Mark`-skip tripwire** (plan item 8) — see deviation #3.
  `gate.layout.parity-from-scratch` covers the same CLAIM via a different, arguably safer mechanism; a live runtime
  oracle would still be valuable for catching a bug in a real app scene the harness gate's synthetic tree doesn't
  reach, if a future session has budget for the scratch-bounds redesign this needs.
- **A literal virtualization-recycle gate through `ItemsView`/`ScrollKernel`** (deviation #4): the current
  `gate.layout.virtual-clean-rows-skipped` proves the Measure/Arrange fan-out claim on a deterministic stand-in,
  not through the real recycle path. Worth revisiting once the `ItemsView`/`ScrollKernel` realize-timing question
  (why a pure scroll + up to 40 settle frames showed ZERO `MeasureCount`/`ArrangeCount` in the headless harness —
  `NeedsRealize` is detected inside `ArrangeViewport`, consumed by `ReRealizeVirtuals` only the following frame, but
  even that lag didn't explain a full 40-frame zero) is understood — likely needs a `FG_RENDER_CENSUS`-style trace
  of `layoutNeeded`/`reconciled`/`AnyLayoutDirty` across those frames to root-cause, out of this session's scope.
- `UseTimeout`/`UseKeyframes` activity gating (from P1/P3, still open, unrelated to P4).
- Wiring a bound `Fill`/`BorderColor`/etc. write to seed a `BrushFade` on the P1 true-edge pattern (from P3, still
  open, needed only if P5's row template design surfaces a concrete need for it).

## Recommendation for the next agent

P5 (the app-side track row, `docs/plans/…` §"P5 — App: the virtualized track row on the new API") is next — it
needs P1–P4 all done, which they now are. Read `docs/design/subsystems/virtualization.md` §3.4/§5.5 (P3) and
§4.8 of `layout.md` (P4, this session) before starting; the `TrackRowTemplate`/`BoundRowContent` rewrite described
in the plan is the next concrete unit of work. The three "Open items" immediately above are independent, small-
scope follow-ups that can land alongside P5 if there's spare budget, but none of them block it.

---

# 2026-09-09 session — the two P4 leftovers + P8 (incremental scene capture)

## Status: both P4 leftovers landed; P8 landed. Engine Debug + Release clean; VerticalSlice **1388/1388 ALL CHECKS
PASSED** in both configurations; `Wavee.csproj -c Release` builds unchanged; `check-canon.ps1` clean.

## A1 — `TextMeasureCache` → a 2-entry ring (P4 item 7)

`Scene/Columns.cs`: the single-slot `TextMeasureCache` became `TextMeasureEntry E0, E1` plus a `_next` (write
alternation) and `_last` (most recently stored OR HIT) byte.

- `TryGet(text, style, maxW, out entry)` probes BOTH entries; a hit also becomes `_last`, so the ring's fallback
  always points at the width the layout pass most recently resolved at (the arrange width).
- `Store(entry)` alternates slots, so a pass's two widths both survive into the next pass.
- `FlexLayout.Measure`'s text block (`FlexLayout.cs` ~:463) probes via `TryGet` and fills via `Store`.
- **The recorder** (`SceneRecorder.cs` ~:2032) resolves WHICH entry to read by the width it is drawing at:
  `scene.MeasureCacheRef(node).ResolveForWidth(b.W)` — exact `MaxW` match first, else `_last`, which is exactly what
  the pre-ring single slot's implicit "whichever was written last" read always resolved to. So auto-fit (`FitSize`)
  and the underline/strikethrough face metrics keep their previous behaviour when nothing matches. The value is
  copied out by VALUE (a `TextMeasureEntry`), not held as a `ref`.
- The snapshot copies the whole struct (both entries) unchanged — `source.TryGetMeasureCache` was already a
  whole-struct copy.

**Gate `gate.layout.text-cache-ring`** (`LayoutIncrementalSuite.cs`): a grow column holding a wrapping text run
inside a fixed-width row, so the run is measured at the row's grow ESTIMATE during Measure and at its distributed
share during Arrange. Measured: `firstPassMisses=2` (proof the two widths are real and a single slot genuinely
thrashed) then, on a second pass with the text leaf explicitly re-dirtied so the P4 Measure ring cannot
short-circuit it, `secondPassMisses=0 secondPassShapes=0`.

## A2 — `FG_LAYOUT_VERIFY` parity oracle + the unmarked-`LayoutInput` tripwire (P4 item 8)

New file `src/FluentGpu.Engine/Layout/FlexLayout.Verify.cs` (both compiled out of Release entirely).

- **The oracle** (`FG_LAYOUT_VERIFY=1`, DEBUG only): after a real solve, re-solve the same root from scratch with
  every incremental short-circuit off (Measure ring, Arrange early-out, `TryResolveSizeStable`), compare every node's
  `Bounds`, report to stderr, then RESTORE the original rects. It runs under a `Verifying` latch that suppresses every
  side effect a second solve would repeat: `OnBoundsChanged` delivery, the `_arranged`/`ArrangedValid` columns, the
  overflow report, viewport `SetFrame`/`AnchorShift` posts, `ScrollBindEval` baking, and the realize/paint marks; the
  diag counters are snapshotted and restored. **It changes nothing** — a run with the variable set produces
  byte-identical frames to one without it. Wired into `Run(root)`, `Run(root, window)` and `RunSubtree`.
- **The tripwire** (always counted in DEBUG; per-node stderr only under `FG_LAYOUT_VERIFY=1`): every node the Arrange
  early-out SKIPS is re-hashed, whole subtree, against the signature its last real arrange recorded (`LayoutSig` plus
  the text inputs `LayoutSig` deliberately omits). A mismatch = a `LayoutInput` writer that skipped `Mark(LayoutDirty)`.
  Surfaced as `FlexLayout.DiagUnmarkedLayoutWrites`. Each divergence is reported once (the recorded signature is
  updated) so a permanent mismatch cannot spam every frame.
- `Verifying` is a DEBUG field / a Release `static bool => false` PROPERTY, deliberately not a `const`: a const makes
  each guard body unreachable (CS0162 → a Release-only build break under `TreatWarningsAsErrors`). The JIT folds the
  property to the same nothing.

**Gates**: `gate.layout.parity-oracle` (forces one check through the public `VerifyLayoutParityNow(root, window)` hook
after 12 random scoped edits: 0 diverging rects AND the scene restored byte-identically; Release reports `-1` =
compiled out, asserted as such rather than silently passing) and `gate.layout.dirty-mark-tripwire` (an UNMARKED
`LayoutInput` write under a clean early-out is caught — `caughtUnmarked=1` — while the same write done correctly with
`Mark(LayoutDirty)` reports nothing: `falsePositives=0`).

**Deviation, stated honestly**: the text measure cache is left LIVE across the oracle's re-solve. It is a pure
function of (text, style, maxWidth) so it cannot manufacture a layout divergence, and re-shaping every run twice per
frame would make the oracle unusable on a real page. The oracle targets the incremental machinery P4 introduced.

## A3 — the literal virtualization-recycle gate (the third P4 open item)

`gate.layout.virtual-recycle-rows-skipped` was NOT added as the plan worded it, because measuring it answered the
previous session's open question and the answer changes what the gate should assert.

**Root cause of "a pure scroll + 40 settle frames showed ZERO MeasureCount"**: that is CORRECT behaviour, not a
stalled harness. Driven through a real `ItemsView` + `ScrollKernel` (`ItemsViewController.ScrollBy` + two
`RunFrame`s — the `ScrollSuite.controller-roundtrip` pattern), a 400px scroll of a 240px viewport over 40px rows
moves `ScrollState.OffsetY` to exactly 400 and costs `measure=0 arrange=0`, with all 30 realized rows' `Bounds`
byte-identical: the realize window never left the overscan band, and scroll is a transform, not a relayout. Only a
scroll PAST the band (another 2000px) re-realizes — `rows 30->14, reboundRows=14, measure=411`.

So the landed gate is **`gate.layout.virtual-scroll-is-layout-free`**, two legs in one check: (A) a real scroll
inside the band moves the offset for ZERO Measure/Arrange with no row's `Bounds` moving, and (B) a scroll past the
band DOES re-realize and re-lay rows — which is what makes (A)'s zero a property of the engine rather than a harness
that never scrolled. The plan's literal "one-row scroll ⇒ MeasureCount ≤ 12+8" is unassertable: a one-row scroll
does no layout at all. The deterministic 48-sibling-row stand-in (`gate.layout.virtual-clean-rows-skipped`) stays as
the "one row's content changes ⇒ O(1 row)" half of the claim.

## P8 — incremental scene capture for coast frames

Read `docs/design/subsystems/threading-render-seam.md` §3.4 (new) for the full narrative. Summary:

1. **The capture ledger** (`src/FluentGpu.Engine/Scene/SceneStore.Capture.cs`, new). `_recordDirtyStamp` answers a
   RENDERER question and is incomplete for a COPY question (layout writes `Bounds` across a subtree off one dirty
   ancestor; `ClearTransformDirty`/`ClearRecordDirty` mutate flag/dirty-bit columns with no mark; hover/press/focus
   flip `NodeFlags` unmarked). So a second, copy-shaped ledger: `NoteCaptureChanged(idx)` (O(1), no ancestor walk —
   a copy is per-node), `_createdStamp` per slot, `NoteBulkMutation()` as the escape hatch, `ClearCaptureLedger(seq)`
   + `CaptureLedgerFloor` for retention. All four arrays are sized WITH the columns in `ResizeColumns`/the ctor —
   a lazy doubling mid-frame is a managed allocation in a phase required to make none (`gate.path.hero.alloc-zero`
   caught exactly that, 504 B, before this was fixed).
2. **Ledger call sites**: `MarkRecordDirty`'s chain (the record-dirty BYTES are captured columns), `Unmark`,
   `ClearRecordDirty` (both overloads), `ClearTransformDirty`, `ClearLayoutDirty`, `CreateNode`, `AppendChild`'s old
   last child and `DetachFromParent`'s previous sibling (their captured `NextSibling` changes and they are not on the
   marked chain), and the write-intent ref accessors `ScrollRef`/`ScrollRefByIndex`/`InteractRef`/`TextEditRef`/
   `MeasureCacheRef` (stamped on ACCESS — those tables cover a handful of nodes, so over-marking is free and
   provably complete).
3. **`NodeFlags` is now read by VALUE.** `public ref NodeFlags Flags(h)` → `public NodeFlags Flags(h)` plus
   `SetFlagBits`/`ClearFlagBits`/`SetFlagsRaw`, so the COMPILER — not a reviewer — enforces that no flag write
   escapes the ledger. 22 write sites in the engine (`ConnectedAnimation`, `DragController`, `InputDispatcher`,
   `Reconciler`) and 26 in the VerticalSlice suites were converted; every read site compiled unchanged.
4. **The bulk escape hatch**: `FlexLayout.BeginMeasurePass` (any layout pass) and `Reconciler.WriteColumns` (any
   reconciler commit) call `NoteBulkMutation()`. Both write half the columns across arbitrary subtrees and
   enumerating the rest precisely would be an audit whose single miss is a stale-pixel bug. Coast frames do neither.
5. **`SceneRecordingSnapshot.CaptureIncremental(source, extraRoots, lastCapturedSeq)`** (`SceneRecordingSnapshot.cs`).
   `CanCaptureIncremental` refuses on: a different store, a baseline that is not the one held, a publication counter
   that has not advanced, a shrunk node high-water, a risen ledger floor, or any bulk mutation since the baseline —
   returning false rather than guessing. `SnapshotColumn<T>` gained `Set`/`Remove` (+ a slot free list) so per-node
   sparse rows can be edited instead of rebuilt; `ClearSparseRows(index)` is the incremental equivalent of a full
   capture's table reset and runs for both re-copied nodes and newly dead slots.
6. **Publisher + host**: `SceneFramePublisher` tracks `_sceneCaptureSeq[3]` (each slot's baseline) and exposes
   `OldestSlotCaptureSeq`; `SceneRenderFrame.Capture` tries incremental and falls back. `AppHost` now calls
   `NotePublished` BEFORE `ClearTransformDirty`/`ClearRecordDirty` (so their ledger entries are stamped against the
   NEXT publication) and trims to `min(LastConsumedSeq, OldestSlotCaptureSeq)` for the record-dirty and
   pending-removal ledgers, `OldestSlotCaptureSeq` for the capture ledger.
   `FrameStats.CapturedNodes` now reports nodes COPIED (`CopiedNodeCount`), not the reachable set.
7. **Parity is the definition of correct.** `SceneRecordingSnapshot.EqualsForParity` (new
   `SceneRecordingSnapshot.Parity.cs`) compares frame scalars, the removal ledger, the orphan/overlay/spotlight
   lists, every dense column of every LIVE slot, every per-node sparse table, scroll chrome, the scroll-bind
   topology, and the referenced-image set. In DEBUG/`FLUENTGPU_DIAG` every incremental capture is re-derived in full
   into a scratch snapshot and compared; a divergence is reported to stderr and the publication is REDONE as a full
   capture, so the frame stays correct while the audit stays loud (`IncrementalParityFailures`). Release compiles it
   out.

### Deviations from the plan's literal wording (P8)

1. **The reachability walk is retained on the incremental path** (the plan implies copying straight from the ledger).
   It is 3 array reads per node and it is what makes dead-slot clearing, un-parking a page, and `CapturedNodeCount`
   EXACT rather than approximately right — which the parity gate demands. What incremental removes is the expensive
   half (~17 dense column copies + ~25 sparse probes per node). Measured effect is unchanged: 3 nodes copied on a
   coast frame vs 204 on a full one.
2. **`_captured` is still a rebuilt list, not a "persistent bitset"** — `_capturedEpoch` IS the persistent bitset
   (it already was); the list is the previous/current diff the dead-slot pass needs, and rebuilding it is ~5k int
   writes.
3. **`ReferencedImageIds` and string retention are derived from the CAPTURED SET, not refcounted per touched node.**
   Because the walk is retained, the captured set is exact and re-deriving both costs 2–3 array reads per node with
   no second source of truth to keep in sync. The gate's actual requirement — a stable retained set and ZERO
   Pin/Unpin churn — is met and now measured directly (`SceneRecordingSnapshot.StringRetentionOps`, new).
4. **`OldestSlotCaptureSeq` excludes slots that have never captured.** Found by `IncrementalCaptureTests`:
   `ClaimWriteSlot` takes the first non-published slot, so in the steady state only TWO of the three rotate (the
   third is claimed only during a consumer handover). Counting the never-written slot as "describes publication 0"
   — the literal `min` over three — would have pinned every ledger open forever, so record-dirty bits would never
   clear and span reuse would never fire again. This is the one place the plan's wording would have caused a real
   regression.

### Gates (P8) — `src/FluentGpu.VerticalSlice/Suites/SceneSnapshotChecks.cs`, all green

- `gate.capture.incremental-coast` — a scroll-only frame (offset through the token-gated `ApplyMotion`, the content
  transform, the TransformDirty/PaintDirty marks — exactly what `SceneScrollSink.Apply` does) publishes incrementally:
  **`copiedOnCoast=3` vs `copiedOnFull=204`**, with column parity against a from-scratch capture and zero DEBUG
  self-heals.
- `gate.capture.gap-union` — a slot that skipped two publications refreshes incrementally against its OWN older
  baseline and reaches full parity (`copied=7`).
- `gate.capture.add-remove-parity` — 6 passes of adds/removes including a RECYCLED slot index, every pass
  incremental, every pass at parity.
- `gate.capture.string-refcount-no-churn` — 4 incremental coast captures, `retained 2->2`, `pinUnpinOps=0`.
- `gate.capture.capacity-trim-falls-back` — `TrimExcessCapacity` (3840 slots, capacity 4096→256) makes
  `CanCaptureIncremental` report false, the full fallback is exact.
- `src/FluentGpu.Engine.Tests/IncrementalCaptureTests.cs` (new, 3 tests) — the publisher side:
  `OldestSlotCaptureSeqIsTheOldestLiveSnapshotNotAnUnusedSlot`, `ACoastPublicationCopiesOnlyTheChangedChain`
  (<= 8 copied through the REAL `PublishScene` path), `ABulkMutationForcesAFullCaptureOnEverySlot`.

Every pre-existing snapshot gate stays green: parked, parked-cost, roots, image-referenced, string-retain-steady,
scrollbind-gated, isolation, free, generation, alloc, span-retain/release, plus `PublicationGapChecks` and
`RenderLifecycleTests.RecordDirtyBitsSurviveASkippedPublication`.

## Verification run in this session

- `dotnet build src/FluentGpu.slnx` (Debug) and `-c Release`: **0 errors** both.
- `dotnet run --project src/FluentGpu.VerticalSlice` and `-c Release`: **1388/1388 ALL CHECKS PASSED** both
  (1379 before this session, + 5 `gate.capture.*`, + `text-cache-ring`, `parity-oracle`, `dirty-mark-tripwire`,
  `virtual-scroll-is-layout-free`).
- `dotnet test src/FluentGpu.Engine.Tests -c Release`: the only failure ever observed is the documented pre-existing
  `HeadlessPlayerTests` media-facade load-flake (passes in isolation).
- `dotnet build C:\wavee\WaveeMusic\src\apps\Wavee\Wavee.csproj -c Release`: **Build succeeded** — the app compiles
  unchanged. (The `Flags(h)` by-value change is source-compatible with every read site; the app writes no flags.)
- `powershell -File docs/design/check-canon.ps1`: **Canon OK** (33 docs) after the `threading-render-seam.md` §0/§3.4
  and `layout.md` §4.8.1/§4.9 edits.
- **Flakes observed** (pre-existing, documented in the P4 section above): a `System.NullReferenceException` crash in
  one full-suite Debug run (HooksSuite resource-hook async plumbing) that did not reproduce, and one run where
  `gate.shelf.binding.metadata`/`.actions` failed and then passed on every subsequent run. Neither touches a file
  this session changed.

## Files touched this session

Engine:
- `src/FluentGpu.Engine/Scene/Columns.cs` (`TextMeasureEntry` + the 2-entry `TextMeasureCache` ring)
- `src/FluentGpu.Engine/Layout/FlexLayout.cs` (ring probe/store; the `Verifying` guards; `VerifyParity` calls;
  `NoteVerifySig`; `NoteBulkMutation` in `BeginMeasurePass`)
- `src/FluentGpu.Engine/Layout/FlexLayout.Verify.cs` (**new** — the oracle + the tripwire)
- `src/FluentGpu.Engine/Render/SceneRecorder.cs` (`ResolveForWidth` at the text record site)
- `src/FluentGpu.Engine/Scene/SceneStore.cs` (capture-ledger call sites; `Flags` by value +
  `SetFlagBits`/`ClearFlagBits`/`SetFlagsRaw`; ledger arrays in the ctor + `ResizeColumns`)
- `src/FluentGpu.Engine/Scene/SceneStore.Capture.cs` (**new** — the capture ledger)
- `src/FluentGpu.Engine/Scene/SceneRecordingSnapshot.cs` (`CaptureCore`/`CaptureIncremental`/`CanCaptureIncremental`,
  the conditional copy, `ClearSparseRows`, the scrollable pass, the image/span-run post-pass, `SnapshotColumn.Set`/
  `Remove`, `StringRetentionOps`)
- `src/FluentGpu.Engine/Scene/SceneRecordingSnapshot.Parity.cs` (**new** — `EqualsForParity` + the DEBUG self-check)
- `src/FluentGpu.Engine/Hosting/Threading/SceneRenderFrame.cs` (incremental-then-fallback capture)
- `src/FluentGpu.Engine/Hosting/Threading/SceneFramePublisher.cs` (`_sceneCaptureSeq`, `OldestSlotCaptureSeq`,
  `LastCaptureWasIncremental`, `LastCapturedNodeCount` = copied)
- `src/FluentGpu.Engine/Hosting/AppHost.cs` (`NotePublished` first; the two-floor ledger trim + `ClearCaptureLedger`)
- `src/FluentGpu.Engine/Animation/ConnectedAnimation.cs`, `Input/DragController.cs`, `Input/InputDispatcher.cs`,
  `Reconciler/Reconciler.cs` (flag writes → `SetFlagBits`/`ClearFlagBits`; `WriteColumns` → `NoteBulkMutation`)

Tests/gates:
- `src/FluentGpu.VerticalSlice/Suites/LayoutIncrementalSuite.cs` (text-cache-ring, parity-oracle,
  dirty-mark-tripwire, virtual-scroll-is-layout-free)
- `src/FluentGpu.VerticalSlice/Suites/SceneSnapshotChecks.cs` (the five `gate.capture.*` checks + helpers)
- `src/FluentGpu.VerticalSlice/Suites/{AnimSuite,ControlsSuite,OverlaySuite,ScrollSuite}.cs` (flag writes)
- `src/FluentGpu.Engine.Tests/IncrementalCaptureTests.cs` (**new**)

Docs:
- `docs/design/subsystems/threading-render-seam.md` (§0 amendment: differential publication IS implemented; new §3.4)
- `docs/design/subsystems/layout.md` (§4.8 deviation rewrite, new §4.8.1 the text ring, new §4.9 the oracle/tripwire)
- this file

No files deleted or renamed. Nothing in `.native`, `Wavee.PlayPlay` or `private-runtimes` was read or touched. No
commits were made. `C:\wavee\WaveeMusic` was only built, never edited.

## Open items for the next agent

- **P5 / P5b / P6 / P7 are all still open** — the app-side work (`C:\wavee\WaveeMusic`). P5 (the virtualized track
  row on the new bound API) is the next unit and needs nothing further from the engine.
- **The capture ledger's completeness is guarded, not proven.** The DEBUG self-check audits every incremental
  capture in every DEBUG run, and `NoteBulkMutation` covers layout + reconcile wholesale, but a NEW store write that
  mutates a captured column outside those two and forgets `NoteCaptureChanged` would only be caught at runtime in a
  DEBUG build (where it self-heals into a full capture and logs). If a future session wants a compile-time
  guarantee for the remaining columns, the `Flags` treatment (by-value read + explicit write methods) is the pattern
  — `Paint`, `Layout`, `Bounds` and `Interaction` each have hundreds of read sites and would need the same
  mechanical split.
- **`ClaimWriteSlot` only rotates two of the three publisher slots** in the steady state (see deviation 4). That is
  pre-existing and harmless now that `OldestSlotCaptureSeq` accounts for it, but it means the third slot's arena is
  dead weight except during a consumer handover — worth a look if slot memory ever matters.
- **Adoption damage is still conservative** (`threading-render-seam.md` §0): a skipped scene publication still
  repaints the target in full. P8 made the CAPTURE differential, not the damage.
- **The live probe has not been run this session.** The plan's acceptance numbers (`CapturedNodes <= 5 x
  nodes-per-row + 16`, coast capture <= 0.2 ms) are app-side and need P5; the engine-side gates measure the same
  claim on synthetic scenes (3 copied vs 204).
- `UseTimeout`/`UseKeyframes` activity gating (from P1/P3, still open).
- Wiring a bound `Fill`/`BorderColor` write to seed a `BrushFade` on the P1 true-edge pattern (from P3, still open).

---

# 2026-09-09 (late) — early-out restores measured descendants

The P4 Arrange early-out re-asserted only the skipped node's own rect. Measure writes hypothetical W/H into live
`Bounds`, and a clean ZStack is a ring miss, so a dirty ancestor still descends it: a `Grow=1` `Width=NaN` rail
measures to W=0 and was never re-arranged — the Wavee `SeekBar` / `MediaSeekBar` rails went invisible. Fix:
`_measuredPass` + `_pass` stamp every Measure Bounds scribble; the skip calls `RestoreArrangedDescendants` so every
descendant this pass touched is copied back from `_arranged`. Gate:
`gate.layout.early-out-restores-measured-descendants` (`layout-inc`). `FlexLayout.Verify.cs` is unchanged — the
oracle still from-scratch re-solves and restores `Bounds`.
