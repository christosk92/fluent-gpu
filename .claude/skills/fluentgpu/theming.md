# Theming & live theme switching (FluentGpu)

How `Tok` tokens reach pixels, how a **live, animated theme switch** works end-to-end, and the gotchas that make
"theme switching doesn't fully work" — with the fix for each. Read this before touching theming or debugging a
surface that "won't change theme."

## The model

- **Tokens are static getters over a swappable table.** `Tok.FillCardDefault => T.FillCardDefault`; `Tok.T` points at
  the `Dark` or `Light` `TokenSet`. `Tok.Use(kind)` swaps the pointer (O(1)). `Tok.SetAccent`/`SetWindowBackground` are
  overrides on top. Reading a token is a plain field read — it does **not** subscribe anything.
- **Colors are captured where they're read.** `new BoxEl { Fill = Tok.FillCardDefault }` bakes a `ColorF` into an
  immutable `Element`; the reconciler writes it into the scene paint column **once**. A later `Tok.Use` can't repaint an
  existing node by itself — *something has to re-run the code that read the token.*

### Live re-theme (the mechanism — engine-driven, no remount)

A theme mutation bumps **`Tok.Epoch`** (a plain int — NOT a signal; `Tok` is a process-global static). The host
(`AppHost.Paint`, around the phase-3 flush) detects `Tok.Epoch != _lastThemeEpoch` (or a `RequestThemeTransition`
call), then for exactly that flush:

1. `OnApplyThemeMaterial?.Invoke(dark)` → the Windows backend re-applies the DWM window material (immersive-dark
   caption + Mica). **Instant** — the OS can't cross-fade its backdrop.
2. `Reconciler.SetThemeTransition(250f)` arms a cross-fade window, then `Reconciler.RethemeAll()`:
   - **Re-renders every mounted component in place** (`_comps` + root) — diff, **not** remount, so state and node
     identity survive. `ReactiveComponent`s are `InvalidateTree()`'d first so their cached `Setup()` re-runs (positional
     hook cells reused → state preserved).
   - **Re-fires every binding + control-flow boundary** in `_nodeBindings` — `Flow.For`/`Flow.Show`/skeleton boundary
     effects (rebuild their rows/branches reading the new tokens) and **bound color channels**
     (`Fill = Prop.Of(() => Tok.X)`). These are *not* component renders, so without this they'd keep the old theme.
3. The resulting color diffs cross-fade via the existing **`BrushAnim`** path (`SceneRecorder.ResolveSurface`/
   `ResolveTextColor`, linear-light `LerpLinear`). The transition window forces a 250ms (WinUI ControlNormal) fade for
   every diff during the switch — re-rendered nodes cross-fade; **bound channels snap** (they bypass `BrushAnim` by
   design).

**What updates, and how:**

| Color lives in… | Updates on theme switch? | Animates? |
|---|---|---|
| A plain `Component`'s render (`Fill = Tok.X`) | ✅ component re-renders (RethemeAll) | ✅ cross-fade |
| A `Flow.For` / `Flow.Show` row factory | ✅ boundary effect re-fires (RethemeAll → `_nodeBindings`) | ✅ cross-fade |
| A bound channel `Fill = Prop.Of(() => Tok.X)` | ✅ bind re-fires (RethemeAll → `_nodeBindings`) | ⚠️ snaps |
| A **frozen literal** passed as a constructor arg (`OverlayHost.Child`, a control's `ColorF` prop) | ❌ **stays stale** | — |
| A `ReactiveComponent`'s `Setup()` direct `Tok.X` read | ✅ `InvalidateTree` re-runs Setup | ✅ cross-fade |
| DWM Mica / caption | ✅ re-applied (instant) | ❌ OS-owned |

## App API

```csharp
// switch (in an event handler), then animate it:
Theme.Dark = !Theme.Dark;                              // swaps Tok.T, bumps Tok.Epoch
UseContext(ThemeControl.Request)?.Invoke(250f);        // host: in-place re-render + 250ms cross-fade (0 = snap)
```

- **Persistence**: store a `System | Light | Dark` mode (Wavee: `WaveeSettings.ThemeMode`). **Seed the theme in the
  composition root BEFORE the first frame** (`Program.Main`, before `FluentApp.Run`) so there's no startup flash —
  `Theme.Dark = mode switch { 1 => false, 2 => true, _ => !FluentApp.SystemUsesLightTheme() };`.
- **Follow OS live**: subscribe to `FluentApp.SystemColorsChanged` (relayed from `WM_SETTINGCHANGE("ImmersiveColorSet")`
  on the UI thread); while mode == System, re-read `FluentApp.SystemUsesLightTheme()`/`SystemAccent()`, apply via
  `Theme.Dark`/`Tok.SetAccent`, then `RequestThemeTransition(250f)`.

## Gotchas (each = a real "doesn't change theme" bug)

1. **Frozen literals behind a constructor-arg boundary.** Anything built as a literal `Element` and passed as a child
   *constructor arg* freezes at mount — the canonical case is `OverlayHost { Child = column }` (Wavee's whole shell
   frame). A parent re-render rebuilds the column but the autonomous child **drops** it (engine rule: parent→child via
   signals/context, never constructor args). **Fix:** make the theme-dependent fills **bound** —
   `Fill = Prop.Of(() => WaveeColors.FileArea)` — so they live in `_nodeBindings` and `RethemeAll` re-fires them. (Or
   wrap the surface in its own tiny `Component` so it's autonomous and re-renders.)
2. **Control color props typed `ColorF` freeze.** A control field like `TabStrip.SelectedFill` is a constructor arg —
   frozen at mount; the control re-rendering re-reads the *frozen field*, not the token. **Fix:** type the prop
   `Prop<ColorF>` (implicit from `ColorF`, so defaults still work) and pass `Prop.Of(() => Tok.X)` for theme-dependent
   values.
3. **App-local color helpers that hardcode one theme.** Wavee's `WaveeColors.LayerOnMicaBaseAlt` was hardcoded to the
   *dark* value → near-black sidebar in light theme. **Fix:** make the helper theme-aware (`Tok.Theme == Light ? … : …`,
   or map to a theme-aware `Tok.*` token) **and** bind the surfaces that use it (see #1).
4. **`Flow.For` / `Flow.Show` rows are not component re-renders.** They're boundary effects in `_nodeBindings`;
   `RethemeAll` re-fires them. If you add a **new** reactive-boundary kind, register its effect via `AddBinding` or it
   won't re-theme.
5. **Bound colors snap, they don't cross-fade.** Bound `Fill`/`Color` channels are excluded from `BrushAnim`
   (owned by their effect). Acceptable today; if you need a bound surface to cross-fade, extend the bound-color update
   path (don't reach for a remount).
6. **Mica / DWM backdrop follows the OS, not the app theme.** Flipping `DWMWA_USE_IMMERSIVE_DARK_MODE` (via
   `OnApplyThemeMaterial`) re-themes the **caption**; the Mica **tint** is system-driven (OS theme + wallpaper), so a
   light app on a dark OS still gets a dark-ish backdrop. **Mitigation:** use a theme-aware *translucent light* layer
   (e.g. light `LayerOnMicaBaseAlt` = `#B3FFFFFF`, 70% white) over the Mica to mask it. For a guaranteed-light look
   regardless of OS, drop Mica passthrough in light theme and paint an opaque `Tok` background instead (trade-off: no
   Mica translucency).
7. **Render memos that cache an element tree must include `Tok.Epoch` in their key.** A `Component` that memoizes its
   built tree (e.g. `TitleBar`'s `_cachedTree`/`_cacheKey` gate, to avoid rebuilding on every resize tick) re-runs its
   render-effect on `RethemeAll` but returns the **cached old-theme tree** if the key didn't change — the symptom was
   the caption (min/max/close) buttons not re-theming. **Fix:** fold `Tok.Epoch` into the memo key so a switch busts it.
8. **Never reintroduce the re-key remount.** The old workaround re-keyed the root `OverlayHost`
   (`Key = "shell#" + version`) to force a full remount. It "worked" but lost state (scroll/selection/scrub), thrashed
   layout, and didn't animate. The live re-theme above replaces it — keep it gone.

## Debugging "X won't change theme"

1. Is X built inside a plain `Component`'s render? → it should already work (RethemeAll). If not, X is probably frozen
   or bound — continue.
2. Is X a **literal** passed as a constructor arg (into `OverlayHost.Child`, a control prop)? → **frozen**; bind it
   (#1/#2).
3. Is X inside a `Flow.For`/`Flow.Show`/virtual row? → covered by `RethemeAll` `_nodeBindings` re-fire; if still stale,
   the boundary effect isn't registered (#4).
4. Is X a `ReactiveComponent` reading `Tok` in `Setup()`? → covered by `InvalidateTree`; ensure `Setup`'s hook order is
   theme-invariant.
5. Is X an app-local color constant? → make it theme-aware (#3).
6. Is X the window backdrop/caption? → DWM/OS, not us (#6).

## Where the code lives

| Piece | File |
|---|---|
| Tokens, `Use`, `Epoch`, accent | `src/FluentGpu.Engine/Dsl/Tokens.cs`; facade `Theme.cs` |
| `RethemeAll` / `SetThemeTransition` / `BrushAnim` seeding | `src/FluentGpu.Engine/Reconciler/Reconciler.cs` |
| `ReactiveComponent.InvalidateTree` | `src/FluentGpu.Engine/Hooks/Component.cs` |
| Host detection + transition window + `RequestThemeTransition` + `OnApplyThemeMaterial` + `SystemColorsChanged` | `src/FluentGpu.Engine/Hosting/AppHost.cs` |
| Ambient `ThemeControl.Request` | `src/FluentGpu.Engine/Hooks/ThemeControl.cs` |
| `BrushAnim` cross-fade | `src/FluentGpu.Engine/Scene/{Columns,SceneStore}.cs`, `Render/SceneRecorder.cs` |
| Material flip + OS reader + `WM_SETTINGCHANGE` | `src/FluentGpu.Windows/Pal/{Win32Theme,Win32Platform}.cs`, `Hosting/FluentApp.cs` |
| Wavee theme wiring (toggle / persistence / OS-follow / bound surfaces) | `app/Wavee/{Program.cs, WaveeApp.cs, Features/Shell/WaveeShell.cs, Platform/AppSettings.cs, Design/WaveeTokens.cs}` |
