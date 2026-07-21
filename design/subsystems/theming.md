# fluent-gpu — Subsystem Design: Theming, System + Accent Reactivity, Album-art Dynamic Color

**Author scope:** ONE subsystem — the **`FluentGpu.Theme`** assembly and the three brush tiers it owns:
**T0** static baked design tokens, **T1** live OS system/accent colors via the `ISystemColors` PAL seam +
an `Epoch` reactive context, and **T2** album-art-derived dynamic palettes. All three converge on **one
`BrushHandle`** into the existing `BrushTable` + `RGBA16F` gradient atlas, so the hot paint path (phases
8–10) and the DrawList opcode set are **unchanged — this subsystem adds NO new DrawList opcode and NO new
RHI method**. It owns palette extraction (worker), brush derivation (UI thread), two caches, the theming
hooks, OS-reactivity wiring, derived-brush eviction safety, and the recolor cross-fade.

**Authoritative inputs honored verbatim (referenced, not duplicated):**
- `app-requirements-waveemusic.md` §3.3 — the requirement, the three-tier model, the 80B tiered `Palette`,
  the `Context<uint>`-over-Epoch ruling, the back-reference MarkPaintDirty eviction walk, `TargetGen`
  cancellation, the opacity-only recolor cross-fade, and the "rows bind NO derived brushes" invariant.
- `foundations.md` §1–7 — `Handle{u32 index,u32 gen}`, `BrushHandle`/`HandleKind.Brush`, `SlabAllocator<T>`,
  `ObjectPool<class>` (cap 32, edge), `HandleTable`, `StringId`, `ChunkedArena`, the GC-refs-only-at-edges rule.
- `architecture-spec.md` §4.4/§4.5/§5.4 — the SoA `BrushTable : SlabAllocator<BrushData>` (content-hash
  dedup), gradient stops in a side slab by `GradientRef`, the gradient atlas, the clean-span rule
  (`valid IFF every handle IsLive AND realization ContentEpoch unchanged AND baked-geometry hash unchanged`),
  the single `Mutate()` epoch chokepoint + DEBUG `CleanSpanWitness`. §4.7 PAL seams.
- `hardened-v1-plan.md` §2 (threading), §4.2 (COM), §4.4 (alloc/incremental, the 3-signal memo skip),
  §4.6 (epoch / clean-span discipline — the eviction-safety pattern this subsystem reuses verbatim),
  §6 (single-thread-first build order).
- `dotnet10-csharp14-zero-alloc.md` — `FrozenDictionary` for build-once tables, `[InlineArray]`, `Channel<T>`
  bounded `DropOldest` struct-payload off-thread edge, `Vector128`/`TensorPrimitives` for batched float math,
  the COM ruling (`[GeneratedComInterface]` for cold OS reads — `ISystemColors` impl is cold/warm, never hot).
- `subsystems/reconciler-hooks.md` — `DepKey`/`DepDeps`, the **GC-ref deps go to a parallel `GcDepTable`
  compared by `ReferenceEquals`** ruling (the `[FieldOffset]` union in that doc's §3.2 sketch is **illegal CLR
  layout and is superseded by the AUTH header** — see §3.4 below), the `HasConsumedContextChanged` 3-signal
  memo skip, `Context<T>` + `DepKey ToDepKey(T)` projection, effect timing (UseLayoutEffect 6.5, UseEffect 12).

---

## 0. The one-sentence thesis

> **Three brush tiers — static (T0), live-system (T1), album-art-dynamic (T2) — all funnel through one
> `BrushDeriver` step on the UI thread that interns a single `BrushHandle` into the existing `BrushTable`
> (+ one gradient-atlas row for gradient recipes); palette extraction is a pure off-thread worker job over
> the CPU staging block the image decoder already holds; reactivity is a boxless `Context<uint>` Epoch bump
> (system) or a `TargetGen`-gated `Post` (album), recolor is an opacity-only cross-fade over two pre-derived
> brushes, and derived-brush eviction is frame-start + a back-reference `MarkPaintDirty` walk — because
> `IsLive` gates validity, it never reschedules paint.**

Nothing in the hot path knows a brush came from a palette. A `BrushHandle` from a tinted album cover is
byte-identical in the DrawList to a `BrushHandle` from a static token. That single decision is what keeps
theming at **0 per-frame allocation and 0 hot-path cost** while still recoloring the entire now-playing
chrome on every track change.

---

## 1. Module placement & dependency seam (honors the acyclic assembly DAG)

```
Foundation  ── Handle/BrushHandle, SlabAllocator, ColorF, StringId, DepKey, ChunkedArena, FrozenDictionary tables
   ▲
   │ (deps: Foundation ONLY — preserves acyclicity; portable; no Scene, no Rhi, no Pal impl)
   │
FluentGpu.Theme  ── PaletteExtractor (worker fn), BrushDeriver (UI), PaletteCache, BrushCache,
                    Palette/BrushRecipe/DerivedKey POD, the Recipes table, the back-ref multimap
```

**Assembly: `FluentGpu.Theme`** (NEW, portable, `IsAotCompatible`). Per `app-requirements §5`, it depends on
**`Foundation` only**, and is referenced **only by `Hosting`**. It does NOT reference `Scene`, `Render`,
`Rhi`, or any `Pal` impl — it produces `Palette` (POD) and `BrushData` (POD), and consumes `ColorF`. The
**`BrushTable` itself lives in `Scene`** (it is a SceneStore-adjacent slab, `architecture-spec §4.4`); the
`BrushDeriver` is handed an `IBrushSink` interface (defined in `Theme`, implemented by `Hosting`/`Scene`)
so it can intern without taking a `Scene` dependency. This keeps the macOS swap a leaf change.

**Seam interfaces this subsystem defines** (all POD/handle/`ColorF`, zero COM, zero GC-ref-into-pool):

```csharp
namespace FluentGpu.Theme;

// Implemented by Scene/Hosting; the ONLY surface the deriver uses to mint a brush.
public interface IBrushSink
{
    // Content-hash dedup happens inside BrushTable; returns a stable handle for identical BrushData.
    BrushHandle InternSolid(in ColorF c);
    // Bakes a gradient recipe into one RGBA16F atlas row; returns a gradient-backed BrushHandle.
    BrushHandle InternGradient(ReadOnlySpan<GradientStop> stops, GradientShape shape, in GradientGeometry g);
    // Frame-start eviction sweep entry (BrushTable owns LRU; Theme owns the derived-brush back-ref walk).
    void MarkPaintDirty(NodeHandle node);   // routed to ISceneBackend.MarkDirty(node, PaintDirty)
}

// Implemented by FluentGpu.Windows Pal/ (cold/warm COM + registry reads). See §4.
public interface ISystemColors
{
    SystemColorSnapshot Current { get; }    // by-value fat snapshot; read on demand, never per-frame
    uint Epoch { get; }                      // monotonic; bumped on WM_SETTINGCHANGE("ImmersiveColorSet")/HC change
}
```

`IBrushSink` is the bridge that lets `Theme` stay GPU/Scene-agnostic. The `Scene` impl writes `BrushTable`
columns and the gradient atlas; `Theme` never sees a slab.

---

## 2. The three brush tiers (all → one BrushHandle)

| Tier | Source | Reactivity trigger | Where derived | Cache | Status |
|---|---|---|---|---|---|
| **T0 static tokens** | `Tok.*` baked Light/Dark/HC blobs | full theme swap (`UseTheme`) | build-time + `BrushDeriver` at theme load | `FrozenDictionary<TokenId,BrushHandle>` per theme | exists (Dsl+SourceGen) |
| **T1 live system** | `ISystemColors` PAL (accent + HC palette) | OS `WM_SETTINGCHANGE` → `Epoch++` | `BrushDeriver` on Epoch change | `BrushCache` keyed `(accentColor, hc, theme)` | ADD |
| **T2 album-art dynamic** | album cover → `PaletteExtractor` (worker) → `Palette` | per track change (`TargetGen` gate) | `BrushDeriver` on `(recipe, palette, theme, epoch)` | `PaletteCache` (uri→Palette) + `BrushCache` (DerivedKey→BrushHandle) | ADD |

The convergence point is `BrushDeriver.Derive(in BrushRecipe, in Palette, ThemeKind, uint epoch) → BrushHandle`.
A `BrushRecipe` is hand-authored POD (a *function* from palette+theme to a brush); T0 tokens are recipes whose
palette input is ignored; T1 is a recipe that reads `Palette.Accent` from the system snapshot folded into a
synthetic `Palette`; T2 is a recipe over a real extracted `Palette`. **One code path, three input sources.**

### 2.1 Why no new opcode

The DrawList already has `FillRoundRect(Stroke)`, `FillRect`, `DrawShadow`, `DrawGlyphRun`, and `DrawImageCmd`,
all of which reference a `BrushHandle`. A gradient brush is a `BrushData{ kind=Gradient, GradientRef }` whose
stops resolve to a baked `RGBA16F` atlas row at intern time (`architecture-spec §4.4`). A palette-derived hero
wash is *exactly* a gradient brush; the only difference is that its stops were computed by `BrushDeriver`
instead of authored in the DSL. The batcher and record walk are untouched. This is the load-bearing,
verified-correct KEEP from `app-requirements §3.3`.

### 2.2 T0 semantic-token catalog additions (SHIPPED — leaf values, NOT a SPEC-INDEX §2 contract)

The `Tok.*` T0 token table (Light/Dark, sourced as a faithful WinUI `*_themeresources` port) gained a small set
of **leaf token values** to back the SDK controls layer (`FluentGpu.Controls`, controls.md §1.1). These are
**not** cross-cutting contracts — they are ordinary design-token leaf values, registered here because this
subsystem owns the T0 token tier; the `Tok.*` table itself is built in the DSL (`exists (Dsl+SourceGen)`, §2).

| Token (`Tok.*`) | WinUI key | Light | Dark | Used by |
|---|---|---|---|---|
| `FillControlStrong` | `ControlStrongFillColorDefault` | `#72000000` | `#8BFFFFFF` | Slider rail, ScrollBar thumb |
| `FillControlStrongDisabled` | `ControlStrongFillColorDisabled` | `#51000000` | `#3FFFFFFF` | disabled rail/thumb |
| `FillControlSolid` | `ControlSolidFillColorDefault` | `#FFFFFF` | `#454545` | Slider thumb ring |
| `StrokeControlOnAccentSecondary` | `ControlStrokeColorOnAccentSecondary` | `#66000000` | `#23000000` | on-accent control stroke (lower stop) |
| `IconBase` | Files ThemedIcon Base | `#DB161616` | `#DBF0F0F0` | ThemedIcon Base layer (neutral icon body) |
| `IconAlt` | Files ThemedIcon Alt | `#66F0F0F0` | `#66161616` | ThemedIcon Alt layer (translucent secondary body) |

**ThemedIcon layer role → token table (controls.md ThemedIcon; SHIPPED).** A layered vector icon's per-layer
`IconRole` resolves to a T0 token in a bound `Tint` thunk (so a theme/accent swap live-recolors with NO mask
re-raster — the mask is colorless, the tint rides `DrawIconMask`): `Base → Tok.IconBase` (`TextOnAccentPrimary`
when on-accent), `Alt → Tok.IconAlt`, `Accent → Tok.AccentDefault` (or the `SystemFillCritical/Caution/Success`
severity fill under an `IconColorType` status recolor), `AccentContrast → TextOnAccentPrimary`. A disabled layer
resolves to `Tok.TextDisabled` regardless of role. These are leaf values, not a SPEC-INDEX §2 contract; the
ThemedIcon mask *primitive* (the opcode/table split) is the §2 row owned by gpu-renderer/scene-memory.

**Corrections folded in:**
- `StrokeControlOnAccentDefault` corrected to `#14FFFFFF` (**both themes** — was a mixed value).
- `ScrollThumb` aligned to `#8BFFFFFF` (== `FillControlStrong` Dark); the ScrollBar control points at the
  canonical `FillControlStrong` and `ScrollThumb` folds into it.

**Two theme-aware gradient brush helpers (assembled from the `StrokeControl*` tokens):** `Tok.ControlElevationBorder`
and `Tok.AccentControlElevationBorder` — 2-stop vertical gradients (= WinUI `ControlElevationBorderBrush` /
`AccentControlElevationBorderBrush`: secondary stroke @0.33 → default stroke @1.0; the accent variant flips the
stops). They produce a `GradientSpec` consumed by the `BoxEl.BorderBrush` field that the new
`DrawGradientStroke` opcode renders (scene-memory.md §4.1 / gpu-renderer.md §3.1a) — the gradient *fill* path is
unchanged; only the gradient-as-stroke raster is new (owned by gpu-renderer.md).

**Accent ramp — theme-aware accent fills/text (SHIPPED; `Dsl/Tokens.cs` `AccentRamp` + `Tok` getters).** A live
accent override is a seven-shade `AccentRamp` (`Base` + `Light1..3` + `Dark1..3`), not one flat color. The host
reads the exact OS ramp via `IUISettings3.GetColorValue` (`FluentApp.SystemAccentRamp()` → `Tok.SetAccent(in ramp)`);
a custom/album accent supplies only a `Base`, so `AccentRamp.Derive(base)` synthesizes the ramp with WinUI's
"alpha-blend over black/white" approximation (`Dark{1,2,3}` = base × `{0.75, 0.55, 0.315}`; `Light{1,2,3}` = base
blended toward white by `{0.26, 0.48, 0.68}` — the DARK ramp is exact for `#0078D4`, the LIGHT ramp approximate).
The `Tok` accent getters then resolve **theme-aware** (mirrors WinUI `Common_themeresources_any.xaml`):

| Getter | WinUI key | Light theme | Dark theme |
|---|---|---|---|
| `AccentDefault` (+ `Secondary`/`Tertiary`/`Subtle` at α 0.90/0.80/0.16) | `AccentFillColorDefault`… | `Dark1` (opaque) | `Light2` (opaque) |
| `AccentTextPrimary` / `Secondary` / `Tertiary` | `AccentTextFillColor*` | `Dark2` / `Dark3` / `Dark1` | `Light3` / `Light3` / `Light2` |
| `AccentSelectedTextBackground` | `AccentFillColorSelectedTextBackground` | `Base` | `Base` |
| `AccentDisabled` | `AccentFillColorDisabled` | `#37000000` (fixed) | `#28FFFFFF` (fixed) |

The bug this fixes: the old override stored one flat color and every accent FILL getter returned it raw in both
themes, so light theme showed the washed-out dark-theme shade (strip icons, progress bar, sliders). Gate:
`gate.theme.accent-ramp`.

### 2.2bis T0 multi-palette axis (SHIPPED — `ThemePalette` + `PaletteSeed`, Mica-first both themes)

Beyond the Light/Dark kind switch, T0 tokens support **N base-color presets** (shadcn-style semantic slots +
multiple value sets). Owner: `src/FluentGpu.Engine/Dsl/{Tokens,PaletteBuilder}.cs`.

| Artifact | Role |
|---|---|
| `PaletteSeed` | Hue/chroma + light luminance anchors (frame / rail / page / card) + per-theme chrome saturations (`LightChromeSat` / `DarkChromeSat`) |
| `PaletteBuilder` | Build-time generator → paired `TokenSet` + `ShellPalette` per seed |
| `ThemePalette` | `{ Id, Light, Dark, LightShell, DarkShell }` — one preset |
| `Tok.Use(ThemePalette, ThemeKind)` | Pointer swap + `Epoch++` (palette-only changes bump Epoch) |
| `MicaRef` (`Foundation`) | Reference DWM Mica tones (`Light/Dark` × `Default/Bright/Dim`, ±0x14 assumed swing) — the flatten targets for anchor solving + gates |
| `ColorContrast.Flatten` | Straight-alpha composite of a translucent surface over an opaque backdrop (what actually renders) |
| `ColorRamp.Tinted` | No-softening HSL tint — chrome tints that survive at extreme lightness (`Neutral`'s extreme-L chroma softening crushes them) |
| Presets | `warm` (default), `slate` (230°), `neutral`, `accent` (OS-accent-tinted; `SetAccent` rebuilds it AND re-points the active palette) |

**Both theme shells are translucent-over-Mica** (Mica-first). Dark keeps the WinUI stack (seed-tinted
`LayerOnMicaBaseAlt`-class bars @0x73 + white-alpha page/rows). Light mirrors it: the seed's luminance anchors
are solved as **flattened targets** — `tintL = (anchorL − micaL·(1−a)) / a` against `MicaRef.LightDefault` — so
the frame < rail < page ladder holds on the reference backdrop and compresses (never inverts) under wallpaper
swings; bars @0x73, frame/page @0x8C; rows are neutral overlays (white-alpha zebra, ink-alpha hover/press) so
row states are preset-independent. `TokenSet` values stay opaque (`WindowBackground` remains the inactive-window
fallback the host flattens to when DWM stops the backdrop).

Wavee app shell surfaces (`WaveeColors`) read `Tok.Palette.LightShell` / `DarkShell`; persistence via
`WaveeSettings.PaletteId`. Regression: VerticalSlice `palette.*` checks assert on **flattened** colors — warm
calibration anchors, AA text tiers vs the brightest composited hosting surface, the ≥5% flattened luminance
ladder, and pairwise preset distinctness (flattened-toolbar max-channel delta floors, relaxed for accent pairs).
`HoverFill`/`PressedFill` are bindable `Prop<ColorF>` channels (like `Fill`), so recycled list rows re-fire on
`Epoch` (`prop-net.hoverfill` gate).

### 2.2ter On-media ink/scrim, OnAccent (memoized), spacing/radii scales, generated accessors (SHIPPED — G3, 2026-07)

The flagship overhaul (program phase G3) added the non-color-token layer and the two token source generators. All
leaf values / generated forwarders (NOT SPEC-INDEX §2 contracts); owner `src/FluentGpu.Engine/Dsl/{Tokens,Spacing,Radii}.cs`.

- **On-media ink + scrim (theme-INVARIANT statics).** `Tok.OnMediaPrimary/Secondary/Tertiary` (white @ α 1.0/0.80/0.60),
  `Tok.MediaScrim` (`#8C000000`), `Tok.MediaStage`/`MediaLetterbox`, and two `GradientSpec` scrims `Tok.ScrimBottom`/
  `Tok.ScrimTop` (`Tokens.cs`, the "On-media ink + scrim" block). These are plain `static readonly` fields — they do
  **not** follow Light/Dark (ink over imagery is always white-on-scrim), so no epoch memoization is needed. They are
  the extracted-once ramp behind `MediaCard` and the rebuilt `MediaPlayerElement` (media-pipeline §8 / WS-MediaUI fix #6).
- **`Tok.OnAccent` — the WCAG contrast picker, computed at accent-SET time.** `OnAccent` is a `ColorF` accessor
  memoized against `Epoch` (`_onAccentEpoch`/`_onAccentInk`): it recomputes only when the epoch moved (SetAccent/`Use`
  bump it), calling `ColorContrast.PickContrast(AccentDefault)`, so **paint phases read a baked field, never run
  ratio math** (research adjustment #7; Material-3 "contrast is a property of the token pair"). Contrast primitives
  `ColorContrast.{RelativeLuminance, Ratio, PickContrast}` live in `Foundation/ColorContrast.cs` (pure, alloc-free);
  the near-black ink is `#161616`. Adopted from Wavee's `WaveePalette.OnAccent` (now deletable). Gates:
  `gate.tok.onaccent-contrast`, `gate.tok.onmedia-static-identity`.
- **Spacing / Radii scales.** `Spacing` (`Dsl/Spacing.cs`) = a 4px-grid scale `XXS(2)/XS(4)/S(8)/M(12)/L(16)/XL(20)/
  XXL(24)/XXXL(32)` + `Gutter(24)` with the existing semantic names re-pointed onto it. `Radii` (`Dsl/Radii.cs`) adds
  `None(0)/Card(8)/Full(999)` (Full clamped to half-box at record time). These make Wavee's `WaveeSpace`/`WaveeRadius`
  const-for-const deletable (G6).
- **`TokAccessorGenerator` (add-a-token ⇒ generated getter).** `src/FluentGpu.SourceGen/Engine/TokAccessorGenerator.cs`
  (`IIncrementalGenerator`, off `CompilationProvider`) reflects the `FluentGpu.Dsl.TokenSet` record's public settable
  properties and emits `public static X Foo => TheActiveSet.Foo;` for each into `partial class Tok`
  (`Tok.Accessors.g.cs`, ~69 forwards as-built) — **but only for names `Tok` does not already declare by hand**, so a
  getter-with-logic (e.g. the theme-aware accent getters, `OnAccent`) always wins. It fires only in the Engine
  compilation that owns both types. **`TokenSet` stays hand-written C#** (`public sealed record TokenSet` with
  `required ColorF … { get; init; }`) — palettes are computed/derived, so JSON would freeze the formulas (per the
  "improve, don't port / JSON-over-XML but not for computed data" rule). Kills the silent drift where a new `TokenSet`
  field lacked its `Tok` accessor.
- **`GlyphTableGenerator` (Icons codepoints from JSON).** `src/FluentGpu.SourceGen/Engine/GlyphTableGenerator.cs`
  reads a `glyphs.json` AdditionalFile (`src/FluentGpu.Controls/glyphs.json`, ~105 entries, marked
  `FluentGpuGlyphs="true"` in the csproj) and emits `public const string <Name> = "\uXXXX";` into `partial class
  FluentGpu.Controls.Icons` (`Icons.Glyphs.g.cs`). Dormant unless a `glyphs.json` is present (per the JSON-source-of-truth
  preference; distinct from the layered-vector `ThemedIconData.g.cs`).

---

## 3. Core POD types (Foundation/Theme)

### 3.1 `ColorF` and `ThemeKind`

```csharp
namespace FluentGpu.Foundation;   // ColorF lives in Foundation (shared with renderer)

// Linear-light RGBA, 16 bytes. The renderer blends/resolves in linear (COLOR contract);
// ColorF is ALWAYS linear. Conversions to/from sRGB happen at extraction/derivation edges only.
public readonly struct ColorF
{
    public readonly float R, G, B, A;          // linear, premultiply at emit time per COLOR contract
    public float Luma => 0.2126f*R + 0.7152f*G + 0.0722f*B;   // Rec.709 relative luminance, LINEAR
}
```

```csharp
namespace FluentGpu.Theme;
public enum ThemeKind : byte { Light = 0, Dark = 1, HighContrast = 2 }
```

### 3.2 The 80B tiered `Palette` (folded amendment)

Per `app-requirements §3.3`: **not** a 2-color `{Primary,Accent}` — the real `PaletteGradientCompositor` /
`RightPanelThemeResolver` need a per-theme tier (`BackgroundTinted`) and a *Light-tuned and Dark-tuned* pair,
to avoid the "collapses to muddy near-black at partial alpha on dark covers" bug. Five `ColorF` = 80 bytes:

```csharp
namespace FluentGpu.Theme;

// 80 bytes, fully blittable, no GC ref. Both theme tiers extracted on the worker in ONE pass.
public readonly struct Palette
{
    public readonly ColorF BackgroundDark;        // dominant, darkened for dark-theme backdrop base
    public readonly ColorF BackgroundTintedDark;  // dominant tinted toward theme dark-surface (avoids muddy black)
    public readonly ColorF BackgroundLight;       // dominant, lightened for light-theme backdrop base
    public readonly ColorF BackgroundTintedLight; // dominant tinted toward theme light-surface
    public readonly ColorF Accent;                // most-saturated swatch (pills, focus, progress)

    public static readonly Palette Neutral = /* theme-neutral grey ramp, used on miss/HC/Failed */;

    // Theme-resolving accessors the recipes call (no branch in the hot path; recipes are static):
    public ColorF Background(ThemeKind t)       => t == ThemeKind.Light ? BackgroundLight       : BackgroundDark;
    public ColorF BackgroundTinted(ThemeKind t) => t == ThemeKind.Light ? BackgroundTintedLight : BackgroundTintedDark;
}
```

### 3.3 `BrushRecipe` and `DerivedKey`

```csharp
namespace FluentGpu.Theme;

public enum RecipeKind : byte { Solid, LinearGradient, RadialGradient, PillForeground, AccentSolid, TokenSolid }

// Hand-authored static-readonly POD. A pure function (RecipeKind + params) from (Palette,theme,epoch) → brush.
// 16B id + inline params; AOT-clean; NO delegates (delegates would defeat content-hash dedup & AOT).
public readonly struct BrushRecipe
{
    public readonly ushort RecipeId;      // interned identity (the recipe's StringId-equivalent)
    public readonly RecipeKind Kind;
    public readonly byte StopCount;       // for gradients: 2..4
    public readonly RecipeParams Params;  // [InlineArray]-backed packed params: stop offsets, exposure, tint mix
    // Evaluation is a `switch (Kind)` in BrushDeriver — see §6. No virtual dispatch.
}

// The cache key. 16 bytes, blittable, the BrushCache lookup key. NO GC ref (palette folded by content hash).
public readonly struct DerivedKey : IEquatable<DerivedKey>
{
    public readonly ushort RecipeId;
    public readonly ThemeKind Theme;
    public readonly byte HcFlag;          // 0 normal, 1 high-contrast
    public readonly uint  PaletteHash;    // FNV-1a over the 80B Palette (content hash, dedup-stable)
    public readonly uint  Epoch;          // system-color epoch (folds accent into the key)
    public bool Equals(DerivedKey o) => RecipeId==o.RecipeId && Theme==o.Theme && HcFlag==o.HcFlag
                                      && PaletteHash==o.PaletteHash && Epoch==o.Epoch;
    public override int GetHashCode() => /* combine */ ;
}
```

The `Recipes` table is a `static readonly BrushRecipe[]` (e.g. `Recipes.HeaderWash`, `Recipes.PillFill`,
`Recipes.PillForeground`), built once. WaveeMusic devs reference these by static field; they never author a
recipe at runtime.

### 3.4 `SystemColorSnapshot` and the boxless Epoch context (folded amendment)

`app-requirements §3.3` (and `dotnet10 §C` / `reconciler-hooks §8`): the reactive context is
**`Context<uint>` over the `Epoch`**, NOT `Context<SystemTint>`. Rationale, made explicit:

- The system snapshot is a fat struct (accent + 6-color HC palette + flags ≈ 112B). A 112B struct **cannot
  project into a 16B `DepKey`** — `Context<T>` change-detection projects the context value through
  `DepKey ToDepKey(T)` (`reconciler-hooks §8`), and a 112B value would have to box. **Illegal/expensive.**
- The fix: the context carries only `uint Epoch`. `DepKey.FromUInt(epoch)` is boxless and 16B-clean. Any
  consumer reads the fat snapshot **by value, on demand**, from the stable `ISystemColors` instance; the
  *re-render trigger* is the Epoch context value changing.

```csharp
namespace FluentGpu.Theme;

// ~112B; read by-value on demand, never stored in a context cell, never per-frame.
public readonly struct SystemColorSnapshot
{
    public readonly ColorF Accent, AccentLight1, AccentLight2, AccentDark1;
    public readonly bool   IsHighContrast;
    public readonly HcPalette Hc;        // 6 system HC colors (Window, WindowText, Hotlight, ...)
}
public readonly struct HcPalette { public readonly ColorF Window, WindowText, Hotlight, GrayText, Highlight, HighlightText; }
```

> **DepKey CLR-layout correction (binding):** the AUTH header states the `[FieldOffset]` GC-ref/scalar union
> in `reconciler-hooks §3.2` is **illegal CLR layout**. This subsystem never relies on it: every theming
> `DepKey` is a *pure scalar* (`uint Epoch`, `PaletteHash`, `ThemeKind`, `BrushHandle`), so it is a clean
> 16-byte blittable struct. `Palette` deps are passed as a **content-hashed scalar** (`PaletteHash` into the
> `DepKey`), not as a reference — so theming has **no reference deps at all** and never touches the parallel
> managed `GcDepTable`. This is stricter than the general hook contract and is deliberate (see §5).

---

## 4. T1 — system + accent reactivity (the `ISystemColors` PAL seam)

### 4.1 The PAL seam (`FluentGpu.Windows` Pal/ impl; ADD per `architecture-spec §4.7`)

`ISystemColors` (declared in `Theme`, impl in `FluentGpu.Windows` Pal/) reads the OS color state. The impl is **cold/warm
COM + flat registry reads** — per the COM ruling (`dotnet10 §4`), this uses `[GeneratedComInterface]`/
`[LibraryImport]`, **never the hot path**:

- Accent + light/dark variants: `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent` +
  `DwmGetColorizationColor` (read once at startup, re-read on `WM_DWMCOLORIZATIONCOLORCHANGED`).
- App theme (Light/Dark): `HKCU\...\Themes\Personalize\AppsUseLightTheme`.
- High contrast: `SystemParametersInfo(SPI_GETHIGHCONTRAST)` + `GetSysColor` for the 6 HC entries.
- On `WM_SETTINGCHANGE` with `lParam == "ImmersiveColorSet"` (or an HC toggle), re-read all and **`Epoch++`**.

```csharp
// FluentGpu.Windows Pal/ — cold; runs on the UI thread inside phase 1/2 (pump→dispatch), NEVER per-frame steady state.
sealed class WindowsSystemColors : ISystemColors
{
    SystemColorSnapshot _snapshot;       // volatile-published; written only on OS change
    uint _epoch;
    public SystemColorSnapshot Current => _snapshot;
    public uint Epoch => Volatile.Read(ref _epoch);

    internal void OnSettingChange()      // called from WndProc → InputEventRing → phase 2
    {
        var next = ReadAll();            // registry + SPI reads (cold)
        if (!next.Equals(_snapshot)) { _snapshot = next; Volatile.Write(ref _epoch, _epoch + 1); }
    }
}
```

### 4.2 OS reactivity flow (recolor never forces relayout)

This reuses the existing `WM_SETTINGCHANGE → ThemeChanged WindowEvent → dirty-everything` path; **no new phase.**

```
WM_SETTINGCHANGE("ImmersiveColorSet")  [OS, WndProc on UI thread]
   → InputEventRing entry (POD: SystemColorChanged)            [Pal seam → engine]
   → phase 1 pump drains it
   → phase 2 dispatch: ISystemColors.OnSettingChange() → Epoch++
   → the EpochContext provider value changes (uint)
   → phase 4 render: every UseSystemColors / UseHighContrast / UseDerivedBrush(epoch-dep) consumer
        sees HasConsumedContextChanged(slot)==true → re-renders (3-signal memo skip, §5)
   → phase 5 reconcile: derived brushes re-derived → new BrushHandle (or cache hit)
                        → backend.MarkDirty(node, PaintDirty)   ← PAINT ONLY, never LayoutDirty
   → phase 6 layout: SKIPPED for these nodes (no LayoutDirty bit set)
   → phase 8 record: re-record the PaintDirty chrome subtree only; the 10k grid memcpy's clean
```

**Recolor is PaintDirty, never LayoutDirty.** A color change cannot change a node's measured size; binding
`MarkDirty(LayoutDirty)` here would wrongly re-measure the whole tree. The deriver and the eviction walk emit
**only `PaintDirty`** (enforced by an analyzer rule + a DEBUG assert in `IBrushSink.MarkPaintDirty`).

### 4.3 High Contrast bypasses the palette (folded)

When `ThemeKind == HighContrast`, `BrushDeriver` **short-circuits**: T2 palette inputs are ignored, and every
recipe resolves to the system HC palette (e.g. `Recipes.HeaderWash → sys.Hc.Window`,
`Recipes.PillForeground → sys.Hc.HighlightText`, accent → `sys.Hc.Hotlight`). This is a single
`if (hc) return DeriveHc(recipe, snapshot)` branch at the top of `Derive`. Album-art color is an aesthetic
layer; HC is an accessibility floor and wins unconditionally.

---

## 5. The theming hooks (this subsystem OWNS these)

All hooks are thin `UseMemo`/`UseContext`/`UseRef`/`UseEffect` compositions (per `reconciler-hooks`), with
`ReadOnlySpan<DepKey>` deps. They live in `Theme` (composed via the `Hooks` `RenderContext` extension surface).

```csharp
namespace FluentGpu.Theme;   // extension members on RenderContext (C#14 extension blocks, no adapter alloc)

public static class ThemeHooks
{
    // T0: the active theme kind from the ThemeContext provider. Deps: (themeContext).
    public static ThemeKind UseTheme(this RenderContext ctx);

    // T1: the live OS snapshot, re-read by value when Epoch changes. Deps: (EpochContext as uint).
    //     Returns the fat snapshot by value; the re-render trigger is the Epoch context change.
    public static SystemColorSnapshot UseSystemColors(this RenderContext ctx);

    // T1 convenience: bool gate; deps (Epoch). True ⇒ author should drop palette tinting.
    public static bool UseHighContrast(this RenderContext ctx);

    // T1/T2 convergence: derive ONE BrushHandle. Deps: (recipe.RecipeId, paletteHash, theme, epoch).
    //     palette is passed BY VALUE (80B in); its content hash folds into the DerivedKey (NO ref dep).
    public static BrushHandle UseDerivedBrush(this RenderContext ctx, in BrushRecipe recipe, in Palette palette);

    // T2: album cover → Palette?. A thin wrapper over UseImage(wantPalette:true) from FluentGpu.Media.
    //     Returns null while Decoding/Placeholder/Failed; non-null when the worker has published.
    public static Palette? UseDynamicColor(this RenderContext ctx, StringId albumArtUri);
}
```

### 5.1 Hook semantics

- **`UseTheme`** reads the `ThemeContext` (a `Context<ThemeKind>`, boxless via `DepKey.FromByte`). A theme
  swap changes the provider → consumers re-render → derived brushes re-derive against the new `ThemeKind`.
- **`UseSystemColors`** consumes the **`EpochContext` (`Context<uint>`)** for the change-trigger, then returns
  `ISystemColors.Current` by value. The dep is the `uint` epoch (16B-clean). Reading the fat struct is not a
  dep — the epoch is the only change signal (`app-requirements §3.3`).
- **`UseHighContrast`** = `UseSystemColors().IsHighContrast`, dep = epoch.
- **`UseDerivedBrush`** is the workhorse. Deps = `(RecipeId, PaletteHash, ThemeKind, Epoch)` — all scalar.
  On any dep change it builds a `DerivedKey`, probes `BrushCache`, and on miss calls `BrushDeriver.Derive`.
  The returned `BrushHandle` is stored in the hook cell. **This hook is page-level only** (see invariant §9).
- **`UseDynamicColor`** delegates to `FluentGpu.Media.UseImage(uri, wantPalette:true)`, which carries a
  `TargetGen` and publishes the worker-extracted `Palette` back via `Post` (§7). Returns `Palette?`.

### 5.2 The 3-signal memo skip is honored (NOT SubtreeDirty)

Per `hardened-v1-plan §4.4` and the AUTH header: a theming consumer re-renders iff
`SelfTriggered || propsChanged || HasConsumedContextChanged(slot)`. The Epoch context change drives
`HasConsumedContextChanged` to true for every `UseSystemColors`/`UseHighContrast`/epoch-dep consumer — so they
re-render even when their own props are stable. `SubtreeDirty` is **traversal scope only**, never a skip input.
A now-playing component that consumes the album `Palette` re-renders because its `UseDynamicColor` dep
(`PaletteHash`) changed when the worker published — a normal prop/dep change, not a context change.

---

## 6. `BrushDeriver` — the one convergence point (UI thread)

`BrushDeriver` runs on the **UI thread** (it interns into `BrushTable`, which is UI-thread-owned per the
threading model — the **render thread owns ComPtr, the UI thread owns the Brush/Clip/GlyphRun intern side**,
`hardened-v1-plan §2.1`). It is a pure `switch` over `RecipeKind`, zero alloc, deterministic.

```csharp
namespace FluentGpu.Theme;

public sealed class BrushDeriver
{
    readonly IBrushSink _sink;
    readonly BrushCache _cache;
    readonly ISystemColors _sys;

    public BrushHandle Derive(in BrushRecipe r, in Palette pal, ThemeKind theme, uint epoch)
    {
        var key = new DerivedKey(r.RecipeId, theme, _sys.Current.IsHighContrast ? (byte)1 : (byte)0,
                                 Palette.Hash(pal), epoch);
        if (_cache.TryGet(key, out var cached)) return cached;       // hit: instant, no alloc, no intern

        BrushHandle h;
        if (_sys.Current.IsHighContrast)                              // §4.3 HC bypass
            h = DeriveHc(in r, _sys.Current);
        else h = r.Kind switch
        {
            RecipeKind.TokenSolid   => _sink.InternSolid(Tok.Resolve(r.RecipeId, theme)),     // T0
            RecipeKind.AccentSolid  => _sink.InternSolid(_sys.Current.Accent),                // T1
            RecipeKind.Solid        => _sink.InternSolid(pal.Background(theme)),               // T2 solid
            RecipeKind.PillForeground => _sink.InternSolid(                                    // luma decision
                                          pal.Accent.Luma > 0.63f ? ColorF.Black : ColorF.White),
            RecipeKind.LinearGradient or RecipeKind.RadialGradient => DeriveGradient(in r, in pal, theme),
            _ => _sink.InternSolid(ColorF.Black)
        };

        _cache.Put(key, h);                                          // also registers back-ref (§8) at bind time
        return h;
    }

    BrushHandle DeriveGradient(in BrushRecipe r, in Palette pal, ThemeKind theme)
    {
        Span<GradientStop> stops = stackalloc GradientStop[4];        // <=4; stack, zero heap
        int n = BuildStops(in r, in pal, theme, stops);               // recipe → ColorF stops + offsets
        return _sink.InternGradient(stops[..n], r.Kind == RecipeKind.RadialGradient
                                                 ? GradientShape.Radial : GradientShape.Linear, default);
        // InternGradient bakes ONE RGBA16F atlas row + content-hash dedups; identical washes share a handle.
    }
}
```

Note the `pill-foreground` luma threshold (`> 0.63`) operates on **linear** luma per the COLOR contract; the
`0.63` constant is tuned for the linear space (folded from `app-requirements §3.3`).

### 6.1 Worst-case recolor cost

A now-playing track change re-derives ~7 brushes (header wash, 3 tint-stack layers, accent pill, pill
foreground, progress accent). Cache-miss: 7 `InternSolid`/`InternGradient` calls + 1–2 atlas-row bakes —
sub-millisecond on the UI thread, and only the chrome-root subtree is `PaintDirty` (the 10k grid stays clean,
memcpy'd). Revisiting a previously-seen track is a **`BrushCache` hit → instant, zero intern, zero bake.**

---

## 7. T2 — album-art palette extraction (worker) + caches

### 7.1 `PaletteExtractor` (pure worker job, off the frame loop)

Extraction is a **pure function** over the CPU staging block the image decoder already produced — it runs on
the **WORKER pool** (`hardened-v1-plan §2.1`: workers are pure functions over immutable inputs → POD by
handle, touch NO SceneStore/RhiTable/GPU fence). **No GPU `ReadbackImage`** (that would be a UI-thread device
stall and is unnecessary — the decoder holds the pixels), per `app-requirements §3.1/§3.3`.

The accumulator is a **5-channel × 4096-bucket histogram** (`5 longs × 4096 ≈ 160KB`). `stackalloc` is wrong
at that size; use a **worker-thread-pooled `long[]`** (one per worker, reused, cleared per job) — never the
frame arena, never the cap-32 `ObjectPool`.

```csharp
namespace FluentGpu.Theme;

public static class PaletteExtractor
{
    // Pure. Input: a 16x16 (or bucketed) downsample of the decoded cover (the decoder emits this alongside
    // the bucket decode). Output: 80B Palette by value. Runs on a worker; pooled accumulator passed in.
    public static Palette Extract(ReadOnlySpan<byte> bgraDownsample, int w, int h, long[] pooledAccumulator)
    {
        Array.Clear(pooledAccumulator);                 // reused buffer; no alloc
        // 1. quantize to 4096 buckets (4 bits/channel), weight by saturation*coverage (median-cut-ish)
        // 2. dominant swatch → BackgroundDark/Light via theme-tuned darken/lighten + tint-mix toward surface
        //    (the TintColorHelper logic: tint toward theme surface so dark covers don't collapse to muddy black)
        // 3. most-saturated swatch above a coverage floor → Accent
        // 4. both theme tiers computed in ONE pass (Dark + Light variants)
        // Vector128/TensorPrimitives over the bucket reduction where batched.
        return /* Palette */;
    }
}
```

### 7.2 The off-thread edge (struct-state `Post`, `TargetGen` gate)

The worker result reaches the UI thread via the existing bounded `Channel<T>` + `IPlatformAppLoop.Post`
struct-state marshal — **no closure box** (`dotnet10 §G`). The message is a blittable struct:

```csharp
public readonly struct PaletteResult       // blittable; channel payload, no boxing
{
    public readonly StringId Uri;
    public readonly Palette  Palette;
    public readonly uint     TargetGen;     // the now-playing track sequence at request time
}
```

**`TargetGen` cancellation (folded — fixes nav-flicker):** every palette request carries a `uint TargetGen`
= the now-playing track sequence (or artist-nav sequence) at request time. It is guarded **twice**:

1. **At worker dequeue:** if `TargetGen != currentGen`, drop the job before extracting (cheap).
2. **At `PaletteCache.Publish`:** if `result.TargetGen != currentGen`, drop the result — a late palette for a
   track the user already navigated past never lands.

This directly fixes the `fix-artist-nav-flicker` class of bug (`app-requirements §3.3`). Backdrop bakes
(owned by the Backdrop subsystem, not here) additionally debounce 5 skips → 1 bake; the palette path itself is
cheap enough that it does not debounce, only `TargetGen`-gates.

A worker result `Post` marks the consuming component dirty and schedules **frame N+1** (no synchronous
phase-3 apply in v1 — `architecture-spec §5.4`). So there is a **+1-frame minimum** from palette-arrival to
recolored pixels; the placeholder-tint → cross-fade hides this.

### 7.3 `PaletteCache` and `BrushCache`

```csharp
namespace FluentGpu.Theme;

// uri → Palette. Bounded LRU; survives nav so revisited tracks recolor instantly.
public sealed class PaletteCache
{
    // FrozenDictionary is build-once → WRONG here (mutable). Use a SlabAllocator<PaletteEntry> + open-addressed
    // index keyed by StringId, LRU by LastUsedFrame. Cap ~64 palettes (5KB) — tiny.
    public bool TryGet(StringId uri, out Palette p);
    public void Publish(in PaletteResult r, uint currentGen);   // TargetGen-gated; bumps LastUsedFrame
}

// DerivedKey → BrushHandle. The dedup layer in front of BrushTable's own content-hash dedup.
public sealed class BrushCache
{
    // SlabAllocator<DerivedEntry> + open-addressed index keyed by DerivedKey. LRU. Cap ~256 derived brushes.
    public bool TryGet(in DerivedKey k, out BrushHandle h);
    public void Put(in DerivedKey k, BrushHandle h);            // registers the derived-brush back-ref (§8)
    public void EvictSweep(uint frame);                         // frame-start; respects live-this-frame pins (§8)
}
```

Both caches are mutable and per-frame-probed, so they are slab+open-addressed, **not** `FrozenDictionary`
(`dotnet10 §E`: Frozen is build-once, wrong for mutable hot maps). `FrozenDictionary` is used **only** for the
build-once **T0 token → BrushHandle** tables and the **RecipeId → BrushRecipe** table.

---

## 8. Derived-brush eviction safety (the load-bearing correction)

`app-requirements §3.3` corrects a wrong claim in the source design: **`IsLive` does NOT auto-repair clean
spans.** `IsLive` is a validity *gate* checked by the clean-span validator; it never *schedules* a re-record.
If a derived `BrushHandle` is evicted and freed, the nodes that referenced it must be explicitly re-painted, or
the clean-span memcpy will reuse a span pointing at a dead/recycled brush slot.

This subsystem adopts the **glyph-atlas discipline verbatim** (`hardened-v1-plan §4.6`):

1. **Eviction runs at frame START (phase 1), never mid-frame.** Anything referenced by a live command this
   frame is **pinned and ineligible**. `BrushCache.EvictSweep(frame)` computes liveness from the previous
   frame's live set (a derived brush is live if any node still binds it) and only frees LRU brushes with no
   live binding.

2. **A back-reference multimap drives re-paint on free / theme-change.** `Theme` keeps a small
   `SlabAllocator`-backed **`derived-brush → node` multimap** (only hero/right-panel/page-header nodes register
   — tiny, O(pages), per the row invariant §9). On `BrushDeriver` binding a derived handle to a node, the node
   is registered. On free or `InvalidateThemeDependent()` (theme swap / epoch bump that orphans a cached
   derived brush), the subsystem **walks the back-ref entries and `MarkPaintDirty(node)` each**, so phase 8
   actually re-records them with the freshly-derived handle.

```csharp
namespace FluentGpu.Theme;

sealed class DerivedBrushBackRefs
{
    // SlabAllocator<BackRef>; BackRef { BrushHandle Brush; NodeHandle Node; }. Tiny (O(visible chrome)).
    public void Register(BrushHandle derived, NodeHandle node);
    public void Unregister(NodeHandle node);                    // on node unmount/recycle
    public void OnEvictOrRederive(BrushHandle old, IBrushSink sink)  // walk + MarkPaintDirty
    {
        // for each BackRef where Brush == old: sink.MarkPaintDirty(node);  ← schedules re-record
    }
}
```

3. **The clean-span validator amendment** (`architecture-spec §4.5`, folded): clean span valid IFF every
   handle `IsLive` AND, for `GlyphRunRef`/`ImageRef` handles, the realization `ContentEpoch` is unchanged AND
   the baked-geometry hash is unchanged. **Derived brushes degenerate to `IsLive`-only** (`hardened-v1-plan
   §4.6`: "content-hash dedup makes brush/clip epochs degenerate to IsLive"): a derived brush has no
   in-place-mutated realization (its color is baked into the BrushData / atlas row, identified by content
   hash). So the validator's brush check is just `IsLive`; the *liveness* is what the eviction discipline +
   back-ref walk protect. A re-derived brush that produces *different* content hashes a *new* handle (the old
   one is freed and the back-ref walk re-paints) — there is never an in-place brush mutation that a stale span
   could observe. This is why brush is SAFE-by-construction where glyph/image atlas are only MANAGED.

**Edge case — double-fault:** if a brush is freed AND a node's back-ref was not registered (a bug), the
clean-span `IsLive` gate catches it (the span fails validation → forced re-record), so the failure mode is
"redraw, not corrupt." The DEBUG `CleanSpanWitness` asserts the back-ref coverage in CI.

---

## 9. The list-recycle invariant: rows bind NO derived brushes (page-level only)

This is a **binding cross-subsystem invariant** shared with Virtualization (`app-requirements §3.2/§3.3`):

> **List rows MUST NOT bind T2 (album-art) derived brushes.** Row `Fill`/`Foreground` are **T0 static token
> handles only** (`Tok.TextPrimary`, `Tok.SurfaceCard`, ...). Only **page-level** surfaces — hero card,
> now-playing backdrop, right panel, the palette-tinted *page header* — bind `UseDerivedBrush` /
> `UseDynamicColor`, and there are O(1) of those per screen.

Why this is load-bearing:
- A virtualized row is recycled (`FreeNode`/`CreateNode` over the slab free-list) to a new track on scroll. If
  a row bound a per-track derived brush, every recycle would mint/evict derived brushes at scroll velocity —
  defeating the `BrushCache`, thrashing the back-ref multimap, and risking stale-brush spans on recycled slots.
- Keeping rows on static tokens means a row's `BrushHandle` columns are theme-stable; recycle only re-binds
  text content + an `ImageHandle` (owned by the Media subsystem), never a derived brush. The derived-brush
  back-ref multimap stays O(visible chrome), not O(rows).
- A row art thumbnail is a `DrawImageCmd` with an `ImageHandle` — that is the Media subsystem's residency
  story, **not** a Theme brush. Theme never touches row art.

On recycle to a new key, the reconciler re-runs the row component (deps changed → new static-token bindings,
new image request) OR explicitly clears any derived-brush column on the recycled slot. Because rows never bind
derived brushes in the first place, there is nothing for Theme to clear — the invariant makes recycle trivially
safe for this subsystem.

---

## 10. Recolor = opacity-only cross-fade over two pre-derived brushes (folded)

`app-requirements §3.3` corrects the source claim that recolor re-bakes the gradient atlas every tick. It does
**not**. Recolor is an **opacity-only cross-fade over two pre-derived endpoint brushes**, exactly like
`CrossFadeImage`:

1. On track change, derive the **start** brush (current) and the **end** brush (new palette) **once each** —
   two `BrushHandle`s, two cache entries.
2. Author the recolor as two stacked fills on the same node: the old brush at `Opacity 1→0`, the new brush at
   `Opacity 0→1`, animated over ~220ms by a **phase-7 AnimTrack** writing the `Opacity` column (TransformDirty/
   PaintDirty, **never LayoutDirty**, never a re-derive).
3. At cross-fade completion, the old brush's back-ref is unregistered and it becomes LRU-eligible.

So during the 220ms cross-fade, theming **re-derives nothing and re-bakes no atlas row** — it ticks an opacity
on two already-interned brushes. Per-frame theming cost during a recolor is **one opacity write per layer**
(owned by the Animation subsystem's AnimTrack; Theme contributes only the two endpoint handles). This is the
corrected, verified claim from `app-requirements §6`.

---

## 11. Where each piece lands in the 13-phase loop (and on which thread)

Per `hardened-v1-plan §2.2` (the final phase→thread map). UI thread owns all of Theme's work; the render thread
sees only the resulting `BrushHandle`s in the published `SceneFrame`.

| Phase | Thread | Theme work |
|---|---|---|
| **1 pump** | UI | drain `SystemColorChanged` from InputEventRing; drain `PaletteResult` `Post` mailbox; **`BrushCache.EvictSweep(frame)`** (frame-start derived-brush eviction, §8) |
| **2 input-dispatch** | UI | `ISystemColors.OnSettingChange()` → `Epoch++` if changed → EpochContext value changes |
| **3 hook-flush** | UI | (no Theme-specific work) |
| **4 render** | UI | `UseTheme`/`UseSystemColors`/`UseHighContrast`/`UseDerivedBrush`/`UseDynamicColor` evaluate; 3-signal memo skip gates re-render; `BrushDeriver.Derive` (cache-hit instant, miss interns) |
| **5 reconcile** | UI | derived `BrushHandle`s written to node `Fill`/`Stroke` columns via `ISceneBackend.WriteVisual`; back-refs registered; `MarkDirty(node, PaintDirty)` for recolored nodes; the eviction/theme-swap back-ref `MarkPaintDirty` walk (§8) |
| **6 layout** | UI | **SKIPPED for recolor** — no LayoutDirty set by Theme |
| **6.5 layout-effects** | UI | (none) |
| **7 animation** | UI | recolor cross-fade: AnimTrack ticks `Opacity` on the two stacked fills (§10) — PaintDirty, never re-derive |
| **PUBLISH (13a)** | UI | `BrushHandle`s + `Opacity` snapshot into `SceneFrame`; brushes are append-mostly content-immutable retained-table refs (no per-frame copy) |
| **8 record** | RENDER | sees `BrushHandle`s like any other — **no Theme-aware code on the render thread** |
| **9–11 batch/submit/present** | RENDER | gradient brushes resolve their baked atlas row at batch time (existing path) |
| **12 passive-effects** | UI | (palette fetch *requests* are issued by Media's `UseImage` effect, not Theme) |

**Worker pool** (any time, off-loop): `PaletteExtractor.Extract` runs as a pure job; result lands in the
`Channel<PaletteResult>` → `Post` → drained at phase 1 of frame N+1.

---

## 12. Zero-alloc & thread-confinement story

**0 managed allocations in phases 6–13** (the one rule, `dotnet10 §0`):
- `BrushDeriver.Derive` allocates nothing: `DerivedKey` is a stack value, gradient stops are `stackalloc`,
  cache probe is a slab read, `InternSolid`/`InternGradient` write into the retained `BrushTable` slab + atlas
  (no managed heap). A cache hit is a single open-addressed lookup.
- `PaletteResult` is a blittable struct flowing through a bounded `Channel<T>` `DropOldest` — value, not boxed.
- The extraction accumulator is a **worker-pooled `long[]`** (reused), never per-job allocated, never the frame
  arena, never the cap-32 `ObjectPool` (which would overflow precisely under burst).
- `Palette` (80B) and `SystemColorSnapshot` (112B) are passed `in`/by-value; no boxing into context cells
  (the Epoch `uint` is the only context payload — §3.4).
- Edge allocations (allowed): the `PaletteCache`/`BrushCache`/back-ref slabs grow geometrically at startup;
  hook cells are mount-time class allocations (per `reconciler-hooks §3` — acceptable edge).

**Thread confinement** (`hardened-v1-plan §2.1`):
- **UI thread is sole writer** of `BrushTable` intern side, `BrushCache`, `PaletteCache`, the back-ref
  multimap, and `ISystemColors` epoch state. `BrushDeriver` runs UI-thread-only (asserted by `ThreadGuard`).
- **Worker pool** runs `PaletteExtractor` as a pure function — it reads only the immutable decoded downsample
  it was handed and writes only its pooled `long[]` + the returned POD `Palette`. It touches NO SceneStore, NO
  BrushTable, NO GPU resource. Results cross back **by value** through the channel.
- **Render thread** never executes Theme code; it consumes `BrushHandle`s in the snapshot. **Theme touches
  ZERO COM** — `ISystemColors` registry/SPI reads are flat `[LibraryImport]`/`[GeneratedComInterface]` cold
  calls on the UI thread during phase 1/2, never a per-frame or render-thread path.
- **Build-order safety** (`hardened-v1-plan §6`): Theme is single-thread-correct first (UI produces palette
  requests, worker extracts, UI consumes; quarantine=0). Nothing in Theme requires the render-thread seam; it
  flips to parallel for free when the seam lands because the snapshot already carries finished `BrushHandle`s.

---

## 13. Failure / edge cases

1. **Palette extraction fails / cover never decodes** (`ImageState.Failed`): `UseDynamicColor` returns `null`;
   the author falls back to `Palette.Neutral` (the standard `pal ?? Palette.Neutral` pattern). Chrome shows a
   theme-neutral grey wash — never a crash, never a stale prior-track palette (the prior palette's brushes are
   evicted via LRU once unbound).
2. **Late palette after fast nav** (the nav-flicker bug): `TargetGen` guard at dequeue AND at `Publish` drops
   it (§7.2). The user sees the new track's palette (or Neutral until it arrives), never a flash of the
   skipped-past track's color.
3. **High contrast toggled mid-session:** `Epoch++` → every epoch-dep consumer re-renders → `BrushDeriver`
   takes the HC bypass (§4.3) → palette ignored, system HC colors used. Recolor cross-fade still works
   (opacity-only over the two HC-resolved brushes).
4. **Theme swap (Light↔Dark) while a palette is cached:** `ThemeKind` is in the `DerivedKey`, so the cached
   dark-tier brush and light-tier brush are distinct entries — the swap is a `BrushCache` hit on the other
   tier (both tiers were extracted in one pass). `InvalidateThemeDependent()` walks back-refs → `MarkPaintDirty`
   the chrome → re-record with the other-tier handles. No re-extraction.
5. **Derived brush evicted while still referenced** (over-aggressive LRU): impossible by construction —
   `EvictSweep` pins anything with a live binding this frame (§8.1). If a binding is missed (bug), the
   clean-span `IsLive` gate forces re-record (redraw-not-corrupt) and CI's `CleanSpanWitness` flags it.
6. **A row author mistakenly binds `UseDerivedBrush`** (violating §9): an analyzer rule (`FluentGpu.Validation`)
   flags `UseDerivedBrush`/`UseDynamicColor` inside a component reached through a `VirtualList` item factory.
   Runtime DEBUG assert in `DerivedBrushBackRefs.Register` if the back-ref count exceeds a chrome budget.
7. **Rapid track-change storm** (user mashing next): each change `TargetGen++`; only the latest survives both
   gates; `PaletteCache` LRU absorbs the misses; `BrushCache` hits on any revisited track. No unbounded growth.
8. **System color read fails** (registry locked / SPI error): `ISystemColors` keeps the last good snapshot and
   does not bump Epoch (no spurious recolor); logs a cold diagnostic.

---

## 14. Cross-platform (macOS) boundary

Everything in `FluentGpu.Theme` is **portable** (zero Windows, zero COM, zero D3D): `PaletteExtractor`,
`BrushDeriver`, `PaletteCache`, `BrushCache`, the back-ref multimap, all POD types, and all hooks are pure C#
over `Foundation` + `IBrushSink`.

The **only** Windows-specific piece is the `ISystemColors` *implementation* (`FluentGpu.Windows` Pal/):

| Concern | Windows (`FluentGpu.Windows` Pal/) | macOS later (`Pal.Mac`) |
|---|---|---|
| accent color | `DwmGetColorizationColor` + registry `Accent` | `NSColor.controlAccentColor` |
| app theme Light/Dark | `AppsUseLightTheme` registry | `NSApplication.effectiveAppearance` |
| high contrast | `SPI_GETHIGHCONTRAST` + `GetSysColor` | `NSWorkspace.accessibilityDisplayShouldIncreaseContrast` |
| change notification | `WM_SETTINGCHANGE("ImmersiveColorSet")` → `Epoch++` | `NSApp.effectiveAppearance` KVO / `NSWorkspace` notification → `Epoch++` |

The macOS port reimplements only that one PAL leaf behind the same `ISystemColors` interface (cold COM/ObjC
calls, never the hot path). The `Epoch`-context reactivity, the tiered `Palette`, the deriver, the caches, and
all hooks ship **unchanged**. This is the cleanest realization of the PAL seam contract.

---

## 15. What this subsystem OWNS vs hands off

**OWNS (authority):** `Palette` (80B), `BrushRecipe`, `DerivedKey`, `SystemColorSnapshot`, `HcPalette`,
`PaletteExtractor`, `BrushDeriver`, `PaletteCache`, `BrushCache`, `DerivedBrushBackRefs`, the `Recipes` table,
the `IBrushSink` and `ISystemColors` seam contracts, and the hooks `UseTheme`/`UseSystemColors`/
`UseHighContrast`/`UseDerivedBrush`/`UseDynamicColor`. Owns the derived-brush eviction discipline (frame-start
sweep + back-ref `MarkPaintDirty` walk) and the recolor opacity-cross-fade contract.

**HANDS OFF / DEPENDS ON:**
- **Media** (`FluentGpu.Media`): `UseDynamicColor` is a wrapper over `UseImage(wantPalette:true)`; the worker
  decode + CPU staging block + `TargetGen` plumbing + the `Post` edge are Media's. Theme consumes the published
  `Palette`. Backdrop bakes (editorial 6-layer) are the Backdrop/Effects subsystem; Theme only supplies the
  `Palette` they consume.
- **Scene** (`FluentGpu.Scene`): owns `BrushTable`, the gradient atlas, content-hash dedup, the clean-span
  validator. Theme reaches it only through `IBrushSink`. The `Fill`/`Stroke` columns are Scene's.
- **Reconciler/Hooks**: the 3-signal memo skip, `Context<uint>` Epoch provider, `DepKey`/`DepDeps`,
  effect/render phases.
- **Animation**: the phase-7 AnimTrack that ticks the recolor cross-fade `Opacity` (Theme supplies the two
  endpoint `BrushHandle`s; Animation owns the tween).
- **`FluentGpu.Windows` Pal/**: the `ISystemColors` impl + the `WM_SETTINGCHANGE` wiring into the InputEventRing.

---

## Changed vs the original synthesis

Amendments folded from `app-requirements-waveemusic.md §3.3` (and the AUTH contracts), relative to the
original "static 3-blob theme" synthesis:

1. **Three tiers converge on ONE `BrushHandle` — no new DrawList opcode, no new RHI method.** Palette-derived
   gradients are ordinary gradient brushes baked into the existing atlas. (KEEP, made explicit.)
2. **80B tiered `Palette`, not a 2-color `{Primary,Accent}`.** Five `ColorF` with Light/Dark tinted tiers,
   both extracted in one worker pass — fixes the "muddy near-black on dark covers at partial alpha" bug.
3. **`Context<uint>` over `Epoch`, NOT `Context<SystemTint>`.** A 112B fat struct cannot project into a 16B
   `DepKey` (would box); the epoch is the boxless change-trigger, the fat snapshot is read by value on demand.
4. **DepKey is pure-scalar here** — `Palette` deps fold into a content-`PaletteHash` scalar, so theming has
   **no reference deps** and never touches the parallel managed `GcDepTable`. The `[FieldOffset]` GC/scalar
   union in `reconciler-hooks §3.2` is illegal CLR layout and is explicitly NOT used.
5. **Extraction accumulator is a worker-pooled `long[]` (~160KB), not `stackalloc`**, and runs on a worker off
   the CPU staging block — **no GPU `ReadbackImage`** (no UI-thread device stall).
6. **`IsLive` does NOT auto-repair clean spans.** Added the glyph-atlas discipline verbatim: frame-start
   derived-brush eviction + a back-reference `MarkPaintDirty` walk. Brush epochs degenerate to `IsLive`-only
   (content-hash dedup), making brush SAFE-by-construction.
7. **Recolor = opacity-only cross-fade over two pre-derived brushes** — derives nothing and re-bakes no atlas
   row during the 220ms tween (corrects the source claim of per-tick atlas re-bake).
8. **`TargetGen` cancellation** (now-playing / nav sequence), guarded at worker-dequeue AND at cache-publish —
   fixes the artist-nav-flicker class of bug.
9. **High Contrast bypasses the palette** — a single top-of-`Derive` branch resolving every recipe to the
   system HC palette; accessibility floor wins over album aesthetics.
10. **List-recycle invariant pinned:** rows bind NO derived brushes (T0 static tokens only); only page-level
    chrome (hero/now-playing/right-panel/page-header, O(1) per screen) binds `UseDerivedBrush`/`UseDynamicColor`.
11. **Recolor is PaintDirty, never LayoutDirty** — color cannot change measured size; the OS-reactivity path
    reuses the existing `WM_SETTINGCHANGE → dirty-everything` flow with no new phase.
