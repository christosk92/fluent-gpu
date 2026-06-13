# Gallery IA restructure + Image page rework — design

**Date:** 2026-06-13
**Scope:** The in-app capability gallery's navigation (`src/FluentGpu.WindowsApp`). **Not** the DocFX site (`docs/site`).
**Status:** Approved (brainstorm). Next: implementation plan.

## Problem

The gallery's `Fundamentals` and `Design` nav groups have drifted into catch-alls. Three concrete issues:

1. **`Images` is in the wrong group and the wrong shape.** It lives under *Design* and is a hand-rolled
   "async media pipeline" showcase, not the standard `GalleryPage.Shell` + `ControlExample` + code-panel
   shape every other control page uses. It is fundamentally a demo of the `Image` element and belongs under
   the *Media* controls group.
2. **`Fundamentals` mixes the engine model with app-level features.** Reactivity/layout/rendering (the engine
   model) sit next to cross-cutting app features (Localization, Validation, Motion recipes, Async/skeletons)
   that aren't "how the engine works."
3. **Two latent bugs.** (a) The `FundamentalsPage` overview tile-grid is out of sync with the nav — its
   `Items` array omits `motion-recipes` and `async-skeletons`, so the overview silently drops two children.
   (b) "Validation" appears twice in the nav with the same label (`validation-guide` reference manual under
   Fundamentals, `validation` live sample under Samples).

## Decision

Restructure into purpose-named top-level groups, move `Images` into Media as a control-style `Image` page,
and disambiguate the duplicate label. Locked decisions from brainstorming:

- Cut line: **restructure into new groups** (keep `Fundamentals` for the engine model).
- New groups: **two** — `Patterns` (UX/motion recipes) and `App services` (engine features WinUI lacks).
- `Images` rework: **control-style `Image` page** (`ControlExample` cards, each with a C# code panel).
- **Move `Windows APIs`** from Samples into the new `App services` group.

## Target navigation (`Gallery.cs`)

```
Home
Fundamentals   (engine model)
   State & components · Flexbox · CSS Grid · ItemsRepeater
   List virtualization · Animation · Compositor · Scrolling
Patterns        ← NEW
   Motion recipes · Async & skeletons
App services    ← NEW
   Localization · Validation · Windows APIs
Design
   Typography · Iconography
── Controls ──
   All · Basic input · Status & info · Layout · Scrolling · Navigation · Dialogs & flyouts · Text
   Media (Image ← moved · PersonPicture · MediaPlayerElement) · Collections · Menus & toolbars · Date & time
── Samples ──
   Wavee skeleton · Sign-up form  (was "Validation" — relabeled)
```

**Group membership rationale**
- **Fundamentals = how the engine works.** Reactivity (`state`), layout (`flex`, `grid`, `repeater`),
  virtualization, the motion *engine* (`animation`), the compositor, and layout-free scrolling.
  `ItemsRepeater` stays — it is the engine's data-driven layout primitive paired with virtualization, not a
  styled WinUI control.
- **Patterns = recipes built on the engine.** `motion-recipes` (the opt-in Expressive Motion palette on top
  of `animation`) and `async-skeletons` (the shimmer-while-loading pattern).
- **App services = engine features WinUI lacks.** `localization` (the i18n engine), `validation` (the Forms
  validation engine), and `windowsapi` (the OS-services pillars). All three are app-facing subsystems, not
  the rendering/reactivity core.
- **Design = style topics.** `typography` + `icons` only. `images` leaves.
- **Media (Controls) gains `Image`** — the core media element alongside PersonPicture/MediaPlayerElement.

## Components to change

### 1. `Gallery.cs`
- **`Items` nav tree:**
  - Trim `Fundamentals` children to the 8 engine topics (remove `motion-recipes`, `async-skeletons`,
    `localization`, `validation-guide`).
  - Add group `new("patterns", Icons.Movie, "Patterns")` with children `motion-recipes`, `async-skeletons`.
  - Add group `new("app-services", Icons.Globe, "App services")` with children `localization`,
    `validation-guide` (label "Validation"), `windowsapi`.
  - `Design` children → `typography`, `icons` (remove `images`).
  - `Media` children → add `new("Image", Icons.Picture, "Image")`.
  - Remove `windowsapi` from the Samples leaves; relabel the Samples `validation` leaf to "Sign-up form".
- **`ControlCatalog`:** `Media` keys → `["Image", "PersonPicture", "MediaPlayerElement"]`.
- **`Page()` switch:** add `"patterns" => new PatternsPage()`, `"app-services" => new AppServicesPage()`;
  rename `"images" => new ImagesPage()` to `"Image" => new ImagePage()`.

### 2. `ControlGalleryPages.cs` — overview pages
- `FundamentalsPage.Items` → the 8 engine topics (drop `localization`, `validation-guide`).
- `DesignPage.Items` → `typography`, `icons` (drop `images`); keep the existing description or trim the
  "async imagery" clause.
- **New `PatternsPage`** (clone of `FundamentalsPage` shape): title "Patterns", tiles `motion-recipes`,
  `async-skeletons`.
- **New `AppServicesPage`**: title "App services", tiles `localization`, `validation-guide`, `windowsapi`.

### 3. Image page rework — `ImagesPage` → `ImagePage` (`GalleryPages.cs`)
Rewrite `Render()` to `GalleryPage.Shell("Image", <description>, …)` with `ControlExample.Build` cards, each
carrying a `code:` panel. Uses the real `Ui.Image` overloads (in `src/FluentGpu.Engine/Dsl/Factories.cs`):
- **A simple image** — `Image(url, w, h, corner)`.
- **Object-fit** — `ImageFit.Cover` vs `ImageFit.Contain` with `aspect`.
- **Corner radius** — square / rounded / circle (`corner = size/2`).
- **Sizing & `decodePx`** — one source at 48/80/120 px; cache keys on logical extent.
- **Placeholder tint** — the pre-decode fill.
- **Async album grid** — the responsive 8-cover `AutoGrid` (kept from the current page), now with code and a
  short note on the fetch → off-thread WIC decode → disk cache → GPU residency pipeline.

The hand-rolled `Section`/`AlbumCard`/`LabeledTile` helpers and bespoke `ColorF` constants are replaced by
the shared `ControlExample`/`GalleryPage` scaffolding (Mica-correct fills, source panels, related links).

### 4. `PageInfo.cs`
- Rename the `"images"` `EngineMeta` entry key to `"Image"`; update subtitle/description to the `Image`
  element framing; set `ControlSource = "src/FluentGpu.Engine/Dsl/Factories.cs"` and
  `SamplePage = "src/FluentGpu.WindowsApp/GalleryPages.cs"`; keep the media-pipeline + components/layout doc
  links.

### 5. `Home.cs`
- Featured `Demos` entry `("images", …, "Images", "Async album art + placeholders")` →
  `("Image", Icons.Picture, "Image", "Async art · object-fit · corners")`.

## Out of scope / unchanged
- `docs/site` DocFX content (the user's example is the in-app gallery; the site stays as-is).
- `ShotScene.cs` `"validation"` screenshot key (the sample's *key* is unchanged; only its nav *label*
  changes), `ButtonsPage`/`InputsPage` legacy Home tiles, and any non-gallery `"images"` string (the D3D12 /
  AppHost diag segment named "images" is unrelated).

## Data flow / behavior

Navigation is unchanged in mechanism — `NavigationView` renders `Items`; selecting a key routes through
`Page(key)`. Parent group keys (`patterns`, `app-services`) are selectable and route to their overview
tile-grids exactly as `fundamentals`/`design` do today. The title-bar search corpus
(`BuildSearchIndex`) is derived from `Items`, so it updates automatically; relabeling the Samples
`validation` leaf to "Sign-up form" removes the duplicate "Validation" search hit (the App services
"Validation" → `validation-guide` remains).

## Testing / verification
- `dotnet build src/FluentGpu.slnx` — clean (no warnings).
- `dotnet run --project src/FluentGpu.VerticalSlice` — "ALL CHECKS PASSED" (headless engine gates; the
  gallery is not exercised here but the engine must stay green).
- `dotnet run --project src/FluentGpu.WindowsApp` — launch, walk the nav: Fundamentals/Patterns/App
  services/Design overview grids populate, Media shows the Image tile, Samples shows "Sign-up form", search
  finds each entry once.
- `--screenshot` spot-checks of the reworked **Image** page and the two new overview pages.

## Risks
- **Key rename `images` → `Image`** must be applied in every referencing site (Gallery nav + `Page()` +
  `ControlCatalog`, `PageInfo`, `Home`, `DesignPage`). Grep confirms these are the only references in
  `src/FluentGpu.WindowsApp`. Miss one ⇒ a dead tile/route. The plan enumerates all sites.
- Low overall risk: this is gallery composition only — no engine, layout, or render-path code changes, so the
  zero-alloc and golden gates are unaffected.
