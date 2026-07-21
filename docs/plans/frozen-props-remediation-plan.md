# Frozen-props remediation plan — make stale-component bugs impossible, not just fixed

**Status: LARGELY LANDED** (2026-07-08). Done: **Phase B** (ReuseGuard tripwire + `Component.DebugCheckReuse` +
reconciler hook + 4 `gate.reuse.*` gates — 588 VerticalSlice checks green); **Phase A** (`DiagnosticsPanel` re-key,
`NowPlayingView` queue-rail re-key); **Phase C core** (`Expander`/`SettingsExpander` + `ToolTip` SlotsChannel,
`ComboBox`/`NumberBox` EnabledChannel, `ItemsView` guard override, guard overrides on `DropDownButton`/`SplitButton`/
`AppBarToggleButton`/`CommandBarFlyout`/`Popup`); **Phase D** (`design/subsystems/component-props-contract.md` +
subsystems/README + CLAUDE.md pointer, check-canon green); **Phase E** (analyzer). Follow-up: full provider conversion
of the element/list-slot fields on `CommandBar`/`MenuBar`/`DropZone` and the PARTIAL pickers/dialogs long tail — the
guard + analyzer + doc cover them meanwhile. App-side edits await the running-app rebuild.

## 1. The problem (one mechanism, many bugs)

The engine's component model is deliberate and correct: components are **autonomous**. A reused
`ComponentEl` never re-runs its factory (`Reconciler.cs:373-387` early-returns at `:382-384`), so a parent
re-render does NOT re-render a child component. Live data must reach a child via exactly one of:

1. a **`Signal`/`Func`** the child reads in `Render()` (subscribes it),
2. **`Ctx.Provide` + `UseContext`** (context is signal-backed per provider node, `Reconciler.cs:444-451`),
3. a **remount** forced by a changed `Key` (the `DetailTracks.cs:354-362` re-key idiom).

The defect is ergonomic: `Embed.Comp(() => new T { Field = value })` *reads* like a React prop, compiles,
works at first mount — then silently freezes. Three user-visible bugs shipped from this exact shape
(equalizer curve never following the preset; the Diagnostics log list stuck at its mount-time rows; the
Diagnostics toolbar tooltips never flipping text). The fix is three-layered: **fix the instances, standardize
the safe patterns in Controls, and add a debug tripwire + CI gate so the mistake fails loudly forever.**

## 2. Sweep results (2026-07-08)

### 2.1 Live app bugs (Wavee) — fix now

| # | Impact | Where | Frozen value | Symptom | Fix idiom |
|---|--------|-------|-------------|---------|-----------|
| A1 | HIGH | `NowPlayingView.cs:216-229` + `QueueRailSlot` ctor `:247-248` | `items.Count`, the `items` list in the slot factory and the `itemInvoked`/`itemText`/`isItemEnabled` closures | "Up next" rail shows stale rows/count after queue edits or track auto-advance; clicking a row plays the mount-time track at that index | Re-key: wrap in a keyed `BoxEl` folding `items.Count` + `current.Uri` + a queue-URI hash; add stable `scrollKey` |
| A2 | MED-HIGH | `SettingsPage.Playback.cs:152-154` | `ComboBox.Create(... isEnabled: eqOn ...)` — `IsEnabled` frozen inside the ComboBox factory (`ComboBox.cs:140-144`) | EQ on/off toggle doesn't enable/disable the preset dropdown | Root-cause fix in ComboBox (Phase C); interim call-site re-key `Key = "eqpreset:" + eqOn` |
| A3 | MED-HIGH | `SettingsPage.Playback.cs:211-214` | `NumberBox.Create(... isEnabled: crossOn ...)` (`NumberBox.cs:141-149`) | Crossfade toggle updates the *sibling Slider* (context idiom, live) but not the NumberBox — a visibly inconsistent row | Same as A2 |
| A4 | MED | `DiagnosticsPanel.cs` `DiagToolbarToggle` → `ToolTip.Wrap(btn, on ? tipOn : tipOff)` | `ToolTip.Text` + `Target` frozen (`ToolTip.cs:66-67,110-111`) | Toggle tooltips never flip their on/off wording; ANY dynamic tooltip text in the app is frozen at mount | Root-cause fix in ToolTip (Phase C) |

Benign-but-noted: `SearchPage.cs:317` (count frozen but each query remounts via the `Skel.Region`
Pending→Ready edge — stales only if results ever update in place), `QualityCombo` (`isEnabled` never
changes), `ArtistPopular.cs:142` (set stable per page). Reference-correct examples to imitate:
`DetailTracks.cs:354-362`, `LibraryPage.cs:180,552`, `ArtistPage.AlbumExpand.cs:485`, `PlayerBar.cs:410-411`.

### 2.2 Controls library census (`src/FluentGpu.Controls`)

Three safe patterns exist in-repo:

- **Pattern A — pure element builder**: static method returns the element tree; re-invoked every parent
  render, nothing frozen. Exemplar `Button.cs:137-185`. (Button, HyperlinkButton, IconButton, AppBar*,
  CheckBox row, InfoBar, InfoBadge, determinate Progress*, Slider builders, most layout/content helpers.)
- **Pattern B — provider channel**: `Ctx.Provide(Props.Channel, new Props(...), Embed.Comp(() => new Core()))`;
  core reads `UseContext` → all props LIVE. Exemplar `SelectorBar.cs:36-48` (the canonical comment),
  also ToggleSwitch `:124-129`, SettingsCard `:110-111`, SettingsExpander `:77-78`, Slider.Ranged `:321-330`,
  RadioButtons, Pivot, FlipView, PipsPager, BreadcrumbBar, SwipeControl, indeterminate Progress*,
  Expander SlotsChannel (`Expander.cs:79-84`, the 2026-07-08 fix).
- **Pattern C — signal/Func params + static-only config**: the mutable value is a `Signal<T>`/`Func<>` read
  at render/realize. Exemplars `LazyGrid.cs:110-114`, `EditableText.cs:92-95` (WidthSignal), Marquee,
  Responsive, ColorPicker, CalendarView, TabView/TreeView (documented mount-seed collections).

**HAZARD (9)** — dynamic caller data through frozen fields, no reactive escape:
`DropDownButton` (:55 — Label/Items/Glyph/IsEnabled), `SplitButton` (:60 — + PrimaryContent Element slot),
`AppBarToggleButton` (:28 — Glyph/Label/IsEnabled/IsCompact), `ToolTip` (:110 — Target + Text),
`CommandBar` (:85 — command lists), `CommandBarFlyout` (:113), `MenuBar` (:40 — menu tree), `Popup` (:24),
`DropZone` (:42/:46 — Content slot; same class Expander just fixed).
The recurring anti-pattern: a stateful trigger/overlay component taking label / item-list / wrapped
Element / IsEnabled as plain fields.

**PARTIAL (18)** — value channel reactive, but some dynamic fields frozen:
`ComboBox` (Items/IsEnabled/Header/Description/ErrorText/ItemDescriptions/ItemEnabled),
`NumberBox`, `PasswordBox`, `TextBox` (inner EditableText fields), `EditableText` (Placeholder/IsEnabled/…),
`AutoSuggestBox` (plain list overload/Placeholder), `CalendarDatePicker`, `DatePicker`, `TimePicker`,
`RatingControl`, `ToggleSplitButton`, `ContentDialog` (title/message/button texts), `TeachingTip`,
`GenericFlyout` (trigger label), `ItemsView` (**ItemCount/Items** — the DiagnosticsPanel bug class),
`PagedShelf` (count/title/header), `ScrollBar` float-overload, `AnnotatedScrollBar`.

**SAFE**: ~40 control files (Patterns A/B/C + documented mount-seed collections) + ~12 non-visual helpers.
**Unverified**: `TabStrip.cs` (no public Create found; check its field surface in Phase C).

## 3. The plan

### Phase A — fix the live app bugs (small, immediate)

1. **NowPlayingView queue rail** (A1): keyed wrapper
   `Key = "queue:" + items.Count + ":" + currentUri + ":" + QueueSig(items)` (cheap URI hash), plus
   `scrollKey: "nowplaying:queue"`. Follow `DetailTracks.cs:362` exactly.
2. **Interim re-keys** for A2/A3 only if Phase C's ComboBox/NumberBox conversion doesn't land in the same
   change; otherwise skip (root cause preferred over band-aid).
3. Evidence: build + on-device check (queue edit updates rail; EQ/crossfade toggles flip enabled states;
   toggle tooltips flip text).

### Phase B — the enforcement layer (the piece that stops future agents/people)

**B1. `ReuseGuard` debug tripwire** — new `src/FluentGpu.Engine/Hooks/ReuseGuard.cs`, mirroring
`RenderBudget` exactly (`RenderBudget.cs:29-39`): `public const bool CompiledIn = (DEBUG || FLUENTGPU_DIAG)`,
runtime `Enabled` flag (default ON when compiled in; `FG_REUSE_GUARD=0` opts out). Dead-code-eliminated
from the shipping AOT binary — honors "production safety == CI coverage".

**B2. `Component.DebugCheckReuse` virtual** — on `Component` (`Component.cs`):

```csharp
/// Debug-only (ReuseGuard): called when the reconciler REUSES this instance and discards `next`
/// (the would-be replacement built by the new factory). Override in controls whose fields carry
/// caller data: compare the dynamic fields and throw FrozenPropException when they differ —
/// naming the field and the three legal delivery idioms in the message.
public virtual void DebugCheckReuse(Component next) { }
```

Hook it at the single reuse point, inside the early-return branch (`Reconciler.cs:382-384`):

```csharp
if (oldEl is ComponentEl oce && oce.ComponentType == nce.ComponentType && _comps.ContainsKey(node)
    && oce.DeriveRenderedOutput == nce.DeriveRenderedOutput)
{
    if (ReuseGuard.CompiledIn && ReuseGuard.Enabled)
        _comps[node].Comp.DebugCheckReuse(nce.Factory());   // debug-only alloc; erased in release
    return;
}
```

Factories are pure object construction (`() => new T { … }`), so running one in debug is side-effect-free;
the allocation is debug-only and outside the phase-6–13 alloc-gated window (reconcile is an earlier phase).

**B3. Seed overrides** on the known-value-carrying cores as they're converted in Phase C — and
immediately on `ItemsView` (compare `ItemCount`/`Items`ref) and `Expander` (compare `Content`ref when no
`SlotsChannel` provider is present), since those already shipped bugs. The exception message is the
teaching moment:

> `Expander.Content` changed on a reused component — fields freeze at first mount. Deliver live content via
> `Expander.SlotsChannel` (Ctx.Provide), a Signal, or remount with a Key.
> See design/subsystems/component-props-contract.md.

**B4. VerticalSlice gates** (via `Check(...)`, `Program.cs:2702`):
- `gate.reuse.frozen-prop-tripwire`: mount a guarded control, re-render the parent with a changed by-value
  field, assert `FrozenPropException` fires (and does NOT fire when the same data flows via provider/signal).
- `gate.reuse.guard-erased`: assert `ReuseGuard.CompiledIn` is false in release builds (compile-time const
  check mirroring the existing Diag/RenderBudget discipline).

### Phase C — converge Controls on the safe patterns (the pit of success)

**C1. A standard `Props` helper** to make Pattern B one-liner cheap — the reason hazards exist is that
Pattern B costs ~10 lines of boilerplate per control. Add to `FluentGpu.Hooks` (or Dsl):

```csharp
// Live<TProps> — the standardized Pattern-B door:
public static Element Live<TProps>(TProps props, Context<TProps?> channel, Func<Component> core)
    => Ctx.Provide(channel, props, Embed.Comp(core));
// + protected TProps? UseProps<TProps>(Context<TProps?> channel) => UseContext(channel);
```

(Exact shape decided at implementation — the point is one obvious, documented door.)

**C2. Convert the 9 HAZARD controls** to Pattern B (props record + channel + core reads context), in
app-usage order: **ToolTip first** (ubiquitous; fixes A4 app-wide), then DropDownButton, SplitButton,
CommandBar/CommandBarFlyout, MenuBar, AppBarToggleButton, DropZone (SlotsChannel like Expander), Popup.
Mechanics identical to the Expander fix that already shipped: keep fields as static fallback, context wins.

**C3. Convert the high-traffic PARTIAL controls' frozen fields**: ComboBox + NumberBox (fixes A2/A3 —
route Items/IsEnabled/Header/Description/ErrorText/ItemDescriptions/ItemEnabled through a props channel;
Signals stay as-is), then TextBox/EditableText/PasswordBox/AutoSuggestBox (IsEnabled/Placeholder),
pickers + RatingControl + ToggleSplitButton + ContentDialog/TeachingTip/GenericFlyout as follow-ups.
**ItemsView**: add an `IReadSignal<int>`/`Func<int>` count overload for live lists (read in `Render` —
the LazyGrid.cs:110 idiom); keep the plain `int` for genuinely static sets; document that a changing SET
without a signal count requires the re-key idiom. Verify `TabStrip`.
Each converted control gets its `DebugCheckReuse` override for any field intentionally left frozen.

**C4. Evidence per batch**: `dotnet build src/FluentGpu.slnx` clean + VerticalSlice ALL CHECKS PASSED +
`--screenshot` visual diff on the gallery pages that host the converted controls.

### Phase D — canon registration (what future agents actually read)

1. New **`design/subsystems/component-props-contract.md`** — owns the contract: the autonomy rule, the three
   delivery idioms with exemplars (SelectorBar / Button-builder / LazyGrid+DetailTracks re-key), the
   `ReuseGuard` tripwire, and the checklist for authoring a new control ("caller data ⇒ provider or signal;
   plain fields only for mount-seed config, each listed in `DebugCheckReuse`").
2. Register in `design/SPEC-INDEX.md` §2 + the `design/subsystems/README.md` ownership map; run
   `docs/design/check-canon.ps1`.
3. One-line pointer in `CLAUDE.md` "Working in the code": *"Component props freeze at mount — see
   component-props-contract.md before passing data into `Embed.Comp`."*
4. Update the `fluentgpu` skill / `docs/guide/` how-to page with the same rule stated app-side.

### Phase E — optional hardening (after A–D prove out)

Roslyn analyzer in `FluentGpu.SourceGen` (a real assembly already ships in the NuGet analyzers slot):
flag `Embed.Comp(() => new T { X = <render-scope local that isn't a Signal/Func/const> })` where `T`
doesn't take a provider. High tuning cost; ship only if the tripwire's catch rate shows residual demand.

## 4. Order & effort

| Phase | Size | Depends on |
|---|---|---|
| A (app fixes) | S | — |
| B (tripwire + gates) | M | — (parallel with A) |
| C1+C2 ToolTip/ComboBox/NumberBox | M | B (guard proves conversions) |
| C2/C3 remainder | L (mechanical, batchable) | C1 |
| D (canon) | S | B landed (doc references the guard) |
| E (analyzer) | L | optional |

## 5. Risks / notes

- Running `nce.Factory()` in debug assumes pure factories — true today; the contract doc makes it explicit.
- Context providers add one scene node per control instance; SelectorBar/ToggleSwitch already pay this at
  scale, so it's within budget — but the `ItemsView` row path must keep using PartDelta/bound rows, not
  per-row providers.
- Conversions keep `Create(...)` signatures source-compatible (params unchanged; delivery mechanism swaps
  under the hood) — app code recompiles untouched except where it *wants* the new live behavior.
- The equalizer-curve right-edge **cutoff** (settings page) is a separate layout-overflow issue, tracked
  outside this plan.
