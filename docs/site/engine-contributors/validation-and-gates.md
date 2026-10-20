# Validation and gates

You are changing the engine. This page is the map of *how a change is kept correct* — the clean Debug build,
the canon drift gate, and the trust-ring analyzers. It also states, plainly, what is **not** covered.

> **No tests, ever.** Every test project and the VerticalSlice harness were deleted on 2026-10-20 and the build and
> pre-commit hook reject new ones. The test-based gates that `validation.md` describes (alloc tripwire, golden-image,
> COM-refcount, `seam.race`, the `FluentGpu.Testing` harness, …) are design history, not something to build or run.

This is the working view for contributors. The **design-of-record** — the full machinery, the two clocks, the
trust ledger, every gate's deterministic oracle — is owned by
[`design/subsystems/validation.md`](../../../design/subsystems/validation.md). This page links into it rather
than restating it; where the two differ, the source and the design corpus win and this page is the bug.

> **The one invariant that governs everything below.** Every `ThreadGuard`, alloc-tripwire, `ComTracker`,
> `CleanSpanWitness`, and `IsSpikeCaller` assert is `[Conditional]`-erased from the shipping NativeAOT binary.
> So **in the customer's hands there are no guards.** A hazard not
> covered by the build, an analyzer or a retired spike is, in production, unguarded. This is a truth, not a gap to "fix" (see the
> [engine index honesty discipline](./index.md#canon--single-owner-discipline-design-is-authority-these-docs-are-the-working-view)),
> and it is why the remaining checks have to be non-suppressible and honest about their own holes. The
> canonical statement lives in [`SPEC-INDEX.md` §2 "Validation / safety floor"](../../../design/SPEC-INDEX.md).

## production safety == CI coverage (`[Conditional]`-erased guards in the shipping AOT binary)

Two facts, held together, are the whole posture:

1. **Debug builds carry guards.** Under `Debug`/`FGVALIDATE`, the asserts fire: the alloc-tripwire arms,
   `ThreadGuard.AssertWriter` throws on a cross-thread write to a confined structure, `ComTracker` counts
   refcounts, `CleanSpanWitness` validates clean-span reuse, `IsSpikeCaller` walks the stack. This is the
   *only* configuration in which any safety guard executes.
2. **The shipping binary carries none.** The guards are `[Conditional("DEBUG")]` / `[Conditional("FGVALIDATE")]`.
   The shipping `Release` AOT build defines neither symbol, so ILC drops every call site, the guard methods
   become unreferenced, and the trimmer deletes them. Working-set cost in ship is **~0**.

The consequence for you as a contributor: **a guard is not protection for users — it is a localizer for Debug builds.**
When you add a hot-path invariant, you do not "add a runtime check and ship it." You add a `[Conditional]`
guard that fires in Debug *and* you make sure the build, an analyzer or a retired spike covers the hazard, because the
guard is gone the moment the binary ships. If the only thing standing between a hazard and a user is a debug
assert, the hazard is unguarded in production. The design-of-record restates this wherever a reader might
forget it; so does this page.

## The canon drift gate (`check-canon.ps1`; `canon-allow`; the single-owner model)

The design corpus is large and heavily cross-referenced; consistency is enforced by a **strict single-owner
model** and a drift gate. The gate is design-time only (it scans `design/`, not `docs/`), and you run it after
editing any `design/` doc:

```powershell
powershell -File design/check-canon.ps1                  # exit 0 = clean
```

`check-canon.ps1` (`design/check-canon.ps1`) is a deny-list of known-stale/superseded tokens; it fails (exit 1)
if any of them reappears anywhere in the **live** design tree (`design/archive/` is excluded — historical docs
may carry superseded forms). The rules it currently protects, each tied to a canonical value in
[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md):

- **`handle-layout`** — the `{index32, gen24, kind8}` form is superseded; the handle is `{u32 index, u32 gen}`. <!-- canon-allow: documents the gate rule itself -->
- **`com-blanket`** — the blanket "all COM via hand-vtable / no `ComWrappers` anywhere" rule is superseded by <!-- canon-allow: documents the gate rule itself -->
  the tiered COM ruling.
- **`depkey-union`** — a `[FieldOffset]` GC-ref/scalar union is illegal CLR layout (`TypeLoadException`);
  `DepKey` is pure-scalar + a side `GcDepTable`.
- **`bind-props`** — the dual static + `*Bind` element surface (`TransformBind`, `OpacityBind`, …) is <!-- canon-allow: documents the gate rule itself -->
  superseded by one `Prop<T>` per bindable channel.

To intentionally mention a superseded form in live prose (e.g. to explain a correction), put the marker
`canon-allow` on that line — the convention is an HTML comment `<!-- canon-allow: reason -->`. The gate skips
any line containing it.

The single-owner discipline the gate backs:

- **[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md) is the precedence authority.** When two docs disagree on a
  cross-cutting contract, §2 gives the canonical value and the *one* owning doc.
- **Every shared artifact has exactly one owner.**
  [`subsystems/README.md`](../../../design/subsystems/README.md) is the contract-ownership map — every DrawList
  opcode, RHI method, PAL seam, hook, source generator, scene column, and assembly maps to its one
  authoritative doc. Define it in its owner; everywhere else *reference* it.
- **Superseding a value** means: edit `SPEC-INDEX.md` first, then the owning doc, add a `check-canon.ps1` rule
  for the old token, move the superseded doc to `design/archive/`, and re-run the gate. Adding a new
  cross-cutting contract means registering it in `SPEC-INDEX.md` §2 + the ownership map *before* two docs can
  disagree about it.

If the canon gate is red after a `design/` edit, a stale token
reappeared — fix the token or add `canon-allow`, and re-run. See the
[pitfalls workflow row](../../guide/pitfalls.md#workflow--agents-especially) for the symptom→fix.

## The trust ring and `[Capability]` analyzers

The hardest design problem the program solves is "claim ≠ property": a doc can *assert* a mechanism works, but
that assertion isn't enforced. The trust ring makes the assertion a **compile error** instead of a
code-review hope. The full mechanism is [`validation.md` §4](../../../design/subsystems/validation.md); the
contributor-facing shape:

- A **`[Capability]`** attribute tags a mechanism with the trust ring it has *earned* —
  `Experimental < Spiked < Proven`. A capability is the named property a spike retires (`Com.GeneratedAbi`,
  `Text.Shaping`, `Render.AnalyticAA`, `Seam.Quarantine`, …).
- **Roslyn analyzer rules FG0001–FG0003** (shipped in `FluentGpu.SourceGen`, referenced by everything, so the
  fence is enforced at *every* call site including app code):
  - **FG0001 (Error)** — shipping code physically cannot compile against an `Experimental` capability.
  - **FG0002 (Error)** — a member's declared ring may not exceed the ring the **trust ledger**
    (`trust-ledger.json`) justifies; the ledger is the source of truth, the attribute may not over-claim.
  - **FG0003 (Warning→Error-in-CI)** — using a `Spiked` capability outside a `[SpikeGated]` region without an
    explicit opt-in is flagged.
- The analyzer sees **direct** call sites only. A forwarding shim (shipping method → innocuous wrapper →
  Experimental capability) defeats static analysis — so that gap is closed at **runtime** by the
  `[Conditional("FGVALIDATE")]` `IsSpikeCaller` / `AssertSpikeGated(cap)` assert, which walks the CI-only call
  stack and fails if an unproven capability is reached from a non-spike caller. Three legs: analyzer (direct
  edges) + runtime stack-walk (transitive edges, CI-only) + the trust ledger (the data both consult).

The two clocks couple here: a **spike** (capability gate, run against the actual `PublishAot`'d binary, not the
JIT host) is the *only* event that may promote a capability ring upward and write an evidence row to the
ledger; a spike going **Red** in nightly *demotes* the row, which makes every shipping caller fail FG0001/2 on
the next build — a regression in a *proven* property propagates back into a compile break, automatically. The
canonical floor statement is [`SPEC-INDEX.md` §2 "Validation / safety floor"](../../../design/SPEC-INDEX.md).

## What is honestly NOT covered (physical limits: GPU stall bounds back to the UI thread)

The program is exhaustive about what it can mechanize, and explicit about what it cannot. These are the named
residuals from [`validation.md` §8](../../../design/subsystems/validation.md) — truths, not TODOs:

- **A sustained GPU stall still bounds back to the UI thread.** Decoupling the render thread makes slowness
  *decoupled, not invincible*: the seam absorbs a transient hitch, but a sustained GPU stall eventually applies
  backpressure to the UI thread. No gate makes this go away; it is a physical limit, stated as one.
- **The one lock-free retire-fence is sampled, not proven.** There is no managed TSan. The `seam.race` soak
  shrinks the lock-free surface to exactly one documented acquire/release fence and fuzzes it (sweeping channel
  capacity + reader-stall), but a race *inside* that fence the schedule-fuzz never hits is, in production,
  unguarded. The honest grade is **MANAGED-sampled** — the soak reduces risk, it does not eliminate it.
- **Epoch double-fault** (two errors that cancel in *both* the baked-geometry hash and the content-byte hash)
  is caught only probabilistically.
- **Uncovered AA / DPI / rotation / locale inputs are ungated.** The `render.aa` golden emits a coverage
  manifest so the gap is *visible*, not hidden; a new primitive lands with its golden row or it cannot promote
  the capability. Likewise the real-UIA-client Narrator conformance half of L1/L3/L6 is Windows-gated and
  carries its own coverage manifest.
- **A weak spike behind a `Proven` ring** is a human judgment the ledger records but cannot validate.
- **Production has no guards.** Every assert is erased. This is *the* bottom line, restated wherever a reader
  might forget it: a hazard not covered by the build, an analyzer or a retired spike is unguarded in the customer's
  hands.

There is one suppression floor worth knowing: if the FGCOM analyzers and FG0001–3 are suppressed under schedule
pressure, the build is what still fires. The build *order* is part of the
posture too: it ships single-thread-correct first, then flips parallelism
([`SPEC-INDEX.md` §2 "Quarantine constant"](../../../design/SPEC-INDEX.md)).

## Canon link: validation subsystem (design-of-record)

This page is the working view. The authority is:

- **[`design/subsystems/validation.md`](../../../design/subsystems/validation.md)** — the validation &
  maturity program design-of-record: the two clocks (spikes + gates), the trust ledger, every gate's
  deterministic oracle, the per-folded-gap §12 gate set, and the honest non-coverage ledger.
- **[`SPEC-INDEX.md`](../../../design/SPEC-INDEX.md)** — the precedence authority; §2 owns the canonical
  "Validation / safety floor", the threading model, the quarantine constant, and the handle/COM/`DepKey`/`Prop<T>`
  contracts the canon gate protects.
- **[`subsystems/README.md`](../../../design/subsystems/README.md)** — the contract-ownership map (which doc
  owns which opcode/column/seam/hook the §12 gates reference).

**Within this section:** [Engine Contributors overview](./index.md) ·
[Contributor Map: Where to Change What](./contributor-map.md) ·
[Frame Pipeline and Headless Seams](./frame-pipeline-and-verification-harness.md) · [Signals and Reactivity Internals](./signals-and-reactivity-internals.md) ·
[pitfalls](../../guide/pitfalls.md) (symptom → cause → fix, including the canon-gate row).
