# Wavee three-mode sidebar prototype

A local, browser-based interaction prototype derived from the complete Wavee sidebar specification.

## Run it

```powershell
npm install
npm run dev
```

Open `http://127.0.0.1:4173`.

## Main flows

- The first-run chooser previews and applies Classic, Library, or Wavee Curated.
- The sidebar overflow menu switches designs, collapses the active mode, resets its width, and opens the customizer.
- Settings → General provides the same three-card design picker and theme controls.
- Pins are shared across all three designs. Pin from a sidebar row or a main-content card; reorder pins by dragging them in Classic or Curated.
- Library V3 includes library-only search, type and playlist-owner filters, five sorts, four views, folders, and local custom-order drag reordering.
- Wavee Curated renders a persistent section document. Its full-page editor supports five templates, adding/removing/duplicating/reordering sections, live property editing, item picking, a rail preview, and 50-step in-memory undo/redo.
- Each design remembers its own width and collapsed state in browser local storage.
- At widths below 780 px, the sidebar becomes an overlay drawer.

Use Settings → General → Reset prototype to clear local state and replay onboarding.

## Verification

```powershell
npm run build
npm test
```

The Playwright suite covers onboarding and live mode switching, Library V3 controls and shared pins, Settings and collapse behavior, and the Curated live editor.
