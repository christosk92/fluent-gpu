# Window Backdrop & Mica (WinUI parity, no WinRT)

*Subsystem plan. Owner of the window-level system backdrop. Consistent with the FluentGpu no-WinRT / Win32 + D3D12 + DirectComposition architecture and the hardened threading model. References [`pal-rhi.md`](./pal-rhi.md), [`theming.md`](./theming.md), [`backdrop-effects-animation.md`](./backdrop-effects-animation.md), [`scene-memory.md`](./scene-memory.md).*

## 1. Goal / parity target

Match WinUI 3's window backdrop surface — `MicaBackdrop` (Base + BaseAlt/Tabbed) and `DesktopAcrylicBackdrop` — **without** `Microsoft.UI.Composition` / the WinAppSDK `SystemBackdropController` (we have no WinRT). The replacement is the **DWM system-backdrop attribute** path, which the OS exposes to plain Win32 apps on **Windows 11 22H2+ (build 22621)**. The DWM rasterizes Mica/Acrylic behind the window; FluentGpu's only job is to **present transparent pixels** where the backdrop should show through.

> **Scope.** This doc owns ONLY the *host-window* backdrop (the surface behind the whole UI). **In-app** Acrylic (flyouts, the now-playing scrim, command-bar overlays) is a different mechanism — live-sample blur via `PushLayer{Effect=AcrylicBlur}` — and belongs to [`backdrop-effects-animation.md`](./backdrop-effects-animation.md). The editorial album-art backdrop (WaveeMusic now-playing) is also there. This doc does not touch those.

## 2. Core decision — DWM rasterizes; we just go transparent

WinUI's `MicaController` builds the Mica brush itself in Composition (tint + luminosity + noise) and animates active/inactive state. We **don't** — we set a window attribute and the **Desktop Window Manager** does all of it.

```
DWM  (Mica: samples + blurs the desktop WALLPAPER, applies theme tint+noise;
      Acrylic: samples + blurs WHATEVER IS BEHIND THE WINDOW)
   ↑ shows through wherever our presented alpha = 0
FluentGpu DComp visual ← DXGI composition swapchain (PREMULTIPLIED alpha; root cleared transparent)
   ↑ opaque/translucent UI content (cards, lists, text) drawn on top
```

This buys, for free, the behaviors `MicaController` had to hand-emulate: **active/inactive dimming, theme-following, transparency-setting-following, and battery-saver suppression.** The price: **no programmatic tint** (see §8). For FluentGpu this is the cheapest possible backdrop — **zero extra GPU passes, zero render-thread work, zero GPU resource.**

## 3. Window prerequisites (`Pal.Windows`)

1. **Composition swapchain (already in [`pal-rhi.md`](./pal-rhi.md) §5.1):** `IDXGIFactory2.CreateSwapChainForComposition`, `B8G8R8A8_UNORM`, `DXGI_ALPHA_MODE_PREMULTIPLIED`, `FLIP_DISCARD` → `IDCompositionDevice` target/visual/`Commit`. Premultiplied alpha is **mandatory** for show-through.
2. **Extend the frame so glass fills the client area:** `DwmExtendFrameIntoClientArea(hwnd, &MARGINS{-1,-1,-1,-1})` (the "sheet of glass") — required so the backdrop covers the whole client region, not just the non-client frame. Pairs with our custom title bar (`WM_NCCALCSIZE`/`WM_NCHITTEST`, owned by [`pal-rhi.md`](./pal-rhi.md) windowing).
3. **Set the backdrop type** (UI thread, after window creation; re-applied on theme/DPI/comp change):
   ```c
   // DWMWA_SYSTEMBACKDROP_TYPE = 38 ; values from DWM_SYSTEMBACKDROP_TYPE
   int v = DWMSBT_MAINWINDOW;                 // Mica
   DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, &v, sizeof(int));
   ```
   | `BackdropKind` (ours) | `DWM_SYSTEMBACKDROP_TYPE` | DWM what-it-samples | WinUI equivalent |
   |---|---|---|---|
   | `Auto`    | `DWMSBT_AUTO` (0)            | system default | — |
   | `None`    | `DWMSBT_NONE` (1)           | nothing (we paint opaque) | no backdrop |
   | `Mica`    | `DWMSBT_MAINWINDOW` (2)     | wallpaper | `MicaBackdrop{Kind=Base}` |
   | `Acrylic` | `DWMSBT_TRANSIENTWINDOW` (3)| windows behind | `DesktopAcrylicBackdrop` |
   | `MicaAlt` | `DWMSBT_TABBEDWINDOW` (4)   | wallpaper (alt tint) | `MicaBackdrop{Kind=BaseAlt}` |
4. **Theme the caption** so the title bar matches: `DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE /*20*/, &isDark, sizeof(BOOL))`. Mica extends through the caption automatically.

All four calls are flat C exports → bound via **`[LibraryImport]`** (per the COM/interop ruling), invoked on the **UI thread**. None touch a `ComPtr`, the RHI, or the render thread — so this subsystem needs **no render-thread coordination** and slots cleanly into the hardened threading model.

## 4. The PAL seam (declared in [`pal-rhi.md`](./pal-rhi.md); impl `Pal.Windows`, `Pal.Cocoa`)

A window-attribute op, not a GPU op:

```csharp
public enum BackdropKind : byte { Auto, None, Mica, MicaAlt, Acrylic }

[Flags] public enum BackdropCaps : byte {
    None = 0,
    SystemBackdropType = 1,   // Win11 22H2+ (build 22621): DWMWA_SYSTEMBACKDROP_TYPE
    LegacyMica         = 2,   // Win11 21H2 (build 22000): undocumented DWMWA_MICA_EFFECT=1029
    DarkCaption        = 4,
}

public interface IBackdropSource {
    BackdropCaps Caps { get; }                                       // probed once at window create
    bool TrySetWindowBackdrop(NativeHandle window, BackdropKind kind, bool isDark);
    bool IsEffectiveNow(BackdropKind kind);                          // false ⇒ caller records the opaque fallback fill
}
```

`Pal.Cocoa` implements the same seam with an `NSVisualEffectView` behind the content view (`blendingMode = .behindWindow`) — macOS vibrancy behind one identical API.

## 5. Public API (the FluentGpu dev surface)

Window-level config + one reactive hook (mirrors WinUI setting `this.SystemBackdrop`):

```csharp
ReactorApp.Run<MyApp>(new WindowSpec {
    Backdrop = BackdropKind.MicaAlt,        // tabbed-shell apps (WaveeMusic) use Alt
    ExtendContentIntoTitleBar = true,
});

// reactive, from a component — re-applies on theme/setting change, never relayouts:
UseWindowBackdrop(BackdropKind.Mica);
```

Plus a surface modifier so panels opt **out** of show-through (Mica appears behind shell chrome, not behind an opaque album-art card):

```csharp
VStack(...).BackdropPassthrough()           // this subtree clears transparent → Mica shows
Card(...).Opaque(Tok.LayerFill)             // default: opaque fill, Mica does NOT show here
```

**Default policy (matches WinUI):** the window **root is passthrough**; controls paint their own fills. Mica therefore shows in gaps, margins, the sidebar, and behind translucent chrome — exactly like a WinUI Mica app.

## 6. Frame-loop integration

- **Phase 8 (record, render thread):** a node carrying `NodeFlags.BackdropPassthrough` emits **no fill** for its region → those back-buffer pixels remain the transparent root clear. Everything else records normally on top.
- **Phase 11 (present, render thread):** unchanged. DComp `Commit` presents the premultiplied frame; DWM composites Mica/Acrylic beneath. **No new GPU work, no new resource.**
- **Mutations are `PaintDirty`, never `LayoutDirty`:** switching `BackdropKind` or toggling passthrough is a `DwmSetWindowAttribute` call + a root re-clear. It never relayouts. (Consistent with [`scene-memory.md`](./scene-memory.md) dirty axes.)

The transparent root clear is the **single render-side change** the whole feature needs; it lives in the root node's fill logic in [`gpu-renderer.md`](./gpu-renderer.md)/[`scene-memory.md`](./scene-memory.md) and is gated by `NodeFlags.BackdropPassthrough`.

## 7. Reactivity & graceful fallback (the policy)

Re-evaluate on `WM_SETTINGCHANGE("ImmersiveColorSet")`, `WM_THEMECHANGED`, `WM_DWMCOMPOSITIONCHANGED`, and the [`theming.md`](./theming.md) `ISystemColors` `Epoch` bump. `IsEffectiveNow()` decides whether the root **clears transparent** or **records an opaque themed fallback fill**:

| Condition (probe via `IBackdropSource` / `ISystemColors`) | Effective | Fallback |
|---|---|---|
| Win11 22H2+ & transparency ON & not HC | requested Mica/Acrylic | — |
| Win11 21H2 (build 22000) | `LegacyMica` if requested Mica, else `None` | opaque themed fill |
| **Transparency effects OFF** (`…\Themes\Personalize\EnableTransparency = 0`) | `None` | opaque `Tok.WindowFill` |
| **High contrast active** | `None` | opaque HC system color |
| **Win10 / pre-22000** (no backdrop attr) | unavailable | opaque themed fill |
| **Remote / RDP session** (`GetSystemMetrics(SM_REMOTESESSION)`) | `None` | opaque fill |
| Battery saver | leave to DWM (system suppresses) | — |

This *is* WinUI's `FallbackColor` behavior: when Mica can't render, paint a solid theme/accent-tinted color instead of clearing transparent.

## 8. Honest limitations vs WinUI's `MicaController`

- **No programmatic `TintColor` / `TintOpacity` / `LuminosityOpacity`.** DWM owns the tint; we choose a *type*, not a color. WinUI could fully recolor Mica only because it built the brush in Composition (the WinRT we deliberately don't depend on).
- **Workaround — "tinted Mica" (e.g. WaveeMusic's album-art-colored shell):** record a **translucent themed/accent scrim quad** over the passthrough region. Mica shows through the scrim's alpha → an approximate tint we *do* control, composed via the existing [`theming.md`](./theming.md) `UseDerivedBrush`/gradient path. Stays an ordinary DrawList quad — no extra GPU pass. Recommended pattern for "colored Mica." **As-built (app-side):** the Wavee shell publishes a `Signal<ColorF?>` (`ShellTint`) at its root and paints a low-alpha scrim BEHIND the translucent chrome from it; the shared detail page (`Wavee/Features/Detail`) sets it to the page's art colour while active and clears it on park via the component **activation lifecycle** ([`reconciler-hooks.md`](./reconciler-hooks.md) §0bis `UseActivation`) — so the window material is page-scoped tinted and reverts on nav-away. The colour source is the live-track palette until an additive `IMusicLibrary.GetPaletteAsync` lands (then the page's own art).
- **Mica samples only the wallpaper**, not windows behind it (that's Acrylic). Match WinUI guidance: Mica for the main shell, Acrylic (`DWMSBT_TRANSIENTWINDOW`) for transient/flyout *windows*.
- **Per-monitor / multi-window:** the attribute is per-HWND, so each window sets its own; no shared controller to coordinate (simpler than WinUI).

## 9. macOS mapping

`NSVisualEffectView` behind the content view; `Mica/MicaAlt → .sidebar / .headerView`, `Acrylic → .hudWindow`/`.popover`; `blendingMode = .behindWindow`. Same `IBackdropSource` seam, same passthrough-clear contract (Metal layer `isOpaque = false`).

## 10. Build steps

1. `Pal.Windows`: probe `BackdropCaps` (`RtlGetVersion` build ≥ 22621 → `SystemBackdropType`; == 22000 → `LegacyMica`; `EnableTransparency`, `SM_REMOTESESSION`); implement `TrySetWindowBackdrop` via `[LibraryImport]` `DwmSetWindowAttribute` + `DwmExtendFrameIntoClientArea`; wire the reactive triggers + the `Epoch` listener.
2. `Scene`/`Render`: add `NodeFlags.BackdropPassthrough`; root records transparent clear when set, opaque fallback fill when `!IsEffectiveNow`.
3. `Dsl`/`Hosting`: `WindowSpec.Backdrop`, `UseWindowBackdrop`, `.BackdropPassthrough()`.
4. The "tinted Mica" translucent-scrim recipe documented in [`theming.md`](./theming.md).
5. `Validation`: manual/golden matrix — transparency on/off, HC, build 22000 vs 22621 vs Win10, RDP; assert **no relayout** on kind switch (a `LayoutDirty`-tripwire on backdrop changes).

## Changed vs the original synthesis

- Promotes the one-line `IBackdropSource.SetWindowBackdrop` mention in [`backdrop-effects-animation.md`](./backdrop-effects-animation.md) §"window Mica/Acrylic = PAL" into a full plan, and **pins the concrete DWM mechanism**: `DWMWA_SYSTEMBACKDROP_TYPE` (38) + the `DWM_SYSTEMBACKDROP_TYPE` value table, `DwmExtendFrameIntoClientArea(-1)`, `DWMWA_USE_IMMERSIVE_DARK_MODE` (20), and the pre-22H2 `DWMWA_MICA_EFFECT` (1029) legacy fallback.
- Adds the `BackdropCaps` probe + the full fallback policy table (transparency-off / HC / RDP / Win10) — the `FallbackColor` equivalent.
- Establishes the **`NodeFlags.BackdropPassthrough`** contract and the "root passthrough, controls opaque" default, plus the translucent-scrim **tinted-Mica** workaround for the no-programmatic-tint limitation.
- Confirms the feature is **zero extra GPU passes / zero render-thread work** and `PaintDirty`-only — it needs nothing from the render-thread seam.
