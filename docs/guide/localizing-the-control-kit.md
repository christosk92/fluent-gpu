# Localizing the control kit

FluentGpu.Controls is a **localizable SDK**: every user-facing string it ships (dialog buttons, the media-player
chrome, date/time-picker placeholders, the InfoBar/Toast close tooltip, the NavigationView settings label, the
AutoSuggest empty state) routes through the engine's localization pillar (`FluentGpu.Localization`). This is opt-in
for apps — **an app that never touches localization gets the kit's neutral English unchanged, byte-for-byte** — but
the strings are now translatable and re-resolve live on a culture change.

## How it works (the mechanism)

1. **Neutral strings are a JSON source of truth.** `src/FluentGpu.Controls/assets/loc/en-US.json` holds the kit's
   English strings under control-semantic namespaces (`dialog.*`, `media.*`, `datePicker.*`, …). It is a **build
   input only** (`AdditionalFiles`, not `Content`) — the strings ship *inside* the DLL, not as a loose file.

2. **The source generator (`LocalizationKeysGenerator`) turns that JSON into two things:**
   - **Compile-safe keys** — `FluentGpu.Controls.Strings.Dialog.Ok == "dialog.ok"`, with a typed format method for
     parameterized keys (`Strings.Media.CaptionsIndexed(n)`). Call sites use the const, never a raw string, so a typo
     is a compile error.
   - **A neutral-fallback registration** — because `FluentGpu.Controls.csproj` sets
     `<FluentGpuLocRegisterNeutral>true</FluentGpuLocRegisterNeutral>`, the generator also emits a
     `[ModuleInitializer]` that hands the flattened `dotted-key → neutral-string` table to
     `Localization.RegisterNeutral(...)` at assembly load. That table is the engine's **terminal fallback floor**:
     consulted last in every resolution, so a key always resolves to its neutral string even with no culture loaded.

3. **Controls resolve through the pillar:**
   - Text drawn into a `TextEl` binds via `Loc.Bind(Strings.…)` → a `Prop<string>` thunk that re-resolves on a culture
     change with **no re-render** (compositor-only).
   - Text read inside a component's `Render` (picker faces, dialog button label, menu labels) uses `Loc.Get(Strings.…)`
     / `Loc.Format(...)`; reading it subscribes the render-effect to the culture epoch, so the control **re-renders**
     on a language switch. The whole `MediaStrings` facade works this way.

## Translating the kit in an app

Ship one JSON file per culture next to your app and load it. Keys in a culture table **override the kit's neutral
floor per-key**; anything you don't translate falls back to neutral automatically.

```jsonc
// assets/loc/fr-FR.json
{
  "$culture": "fr-FR",
  "dialog":  { "ok": "OK" },
  "media":   { "off": "Désactivé", "captions": "Sous-titres", "fullscreen": "Plein écran" },
  "datePicker": { "day": "jour", "month": "mois", "year": "année" }
}
```

```csharp
Localization.DefaultCulture = "en-US";
Localization.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "loc"));
Localization.SetCulture("fr-FR");   // bumps the culture epoch → bound text re-resolves, no re-render
```

The full key list is discoverable from `FluentGpu.Controls.Strings.*` in your IDE (or read
`src/FluentGpu.Controls/assets/loc/en-US.json`).

## What is NOT keyed (CultureInfo-derived and universal notation)

- **Month names, day names, AM/PM** in the date/time pickers and CalendarView come from
  `CultureInfo.CurrentCulture.DateTimeFormat` — the kit uses .NET's own globalization data, not shipped strings.
  (Under `InvariantGlobalization=true` these are frozen English; shipping translated month arrays would be a separate
  opt-in and is intentionally out of scope.) Only the picker's **placeholder words** ("day"/"month"/"year"/
  "hour"/"minute"), which .NET does *not* provide, are keyed.
- **Universal notation** stays invariant in C#: aspect ratios (`16:9`), the rate symbol (`×`), the resolution suffix
  (`p`), and the `F11` accelerator key name.

## Pseudo-locale QA

Selecting the pseudo culture accents and pads every resolved string, making any un-externalized literal obvious:

```csharp
Localization.SetCulture(PseudoLocalizer.PseudoCulture);   // "qps-ploc": OK → ⟦ÖĶ···⟧
```

Because the kit's neutral strings resolve through the pillar, they pseudo-localize automatically — no separate pseudo
table exists or needs to be maintained. A control that still renders plain English under pseudo has a hardcoded
literal.

## FGRP008 — the no-hardcoded-string analyzer

The shared analyzer assembly ships `FGRP008` (Warning), which **arms only in the `FluentGpu.Controls` compilation**.
It flags a bare string literal flowing into a user-facing sink — a `TextEl` constructor argument, a `Text` assignment,
or an `AutomationName` assignment. A literal is allowed when it is empty/whitespace, contains no ASCII letter (a glyph,
format specifier, ratio, or separator), or the line carries a `// loc-allow` marker (the deliberate-literal escape
hatch — font names, key names, or the skeleton shimmer templates whose text only sizes a bar).

To add a new user-facing string to a control: add the key to `en-US.json`, then use `Loc.Bind(Strings.…)` (for
`TextEl.Text`) or `Loc.Get`/`Loc.Format` (for a label read in `Render`) — never a literal.
