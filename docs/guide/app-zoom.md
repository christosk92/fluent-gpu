# App zoom (browser-style Ctrl+= / Ctrl+- / Ctrl+0)

FluentGpu windows support browser-style **application zoom**: a per-window factor that scales the whole UI the way
Chrome/Edge do. It is not a pixel stretch — the zoom folds into the window's one effective scale
(`IPlatformWindow.Scale` = OS DPI scale × zoom), so a zoom step shrinks or grows the **DIP viewport** and the app
**re-lays-out** in it: text re-rasterizes crisply, responsive layouts (NavigationView display modes, container
queries) adapt, and a monitor DPI hop keeps the zoom. Layout, rendering, input and popups all consume the one
folded scale, so app code never multiplies by zoom anywhere.

Zoom is **discrete** — steps on `FluentGpu.Foundation.ZoomLadder` (Chromium's 50%…250% ramp:
`0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5`; hard bounds 0.25–5, default 1). The raster caches
key on quantized device scale, so a continuous zoom slider would churn the glyph atlas and baked path geometry on
every pixel of a drag; a fixed ladder keeps each step one clean re-raster. Use `ZoomLadder.In/Out` to step,
`Snap` to re-enter the ladder from a persisted value, `Clamp` to sanitize arbitrary input, `Percent` for display.

Nothing is wired by default — an app opts in with three small pieces: **seed** a persisted level, **register the
chords**, and (optionally) the **Ctrl+wheel** hook. The engine-side contract is gated headlessly by VerticalSlice
check `54d`.

## Seed a persisted level (`AppOptions.Zoom`)

Set `AppOptions.Zoom` from your settings so the **first frame** lays out at the user's level (no visible re-zoom
after startup), and subscribe `FluentApp.ZoomChanged` to write changes back:

```csharp
float savedZoom = LoadZoomSetting();                        // your settings store (e.g. SettingsStore / AppDataStore)
FluentApp.ZoomChanged += z => SaveZoomSetting(z);           // fires per committed step — debounce the disk write if
                                                            // your store is not already write-through
FluentApp.Run(() => new App(), new AppOptions
{
    Title = "My App",
    Zoom  = ZoomLadder.Snap(savedZoom),                     // Snap = persisted-value hygiene (garbage → a real rung)
});
```

`AppOptions.Zoom` is clamped to the `ZoomLadder` range before it reaches the window, so a corrupt setting can never
produce a 0× or 40× first frame (`ZoomLadder.Clamp` turns NaN/∞/≤0 into 1).

## Step it live (`FluentApp.SetZoom` + the ladder)

```csharp
static void ZoomIn()    => FluentApp.SetZoom(ZoomLadder.In(FluentApp.Zoom));
static void ZoomOut()   => FluentApp.SetZoom(ZoomLadder.Out(FluentApp.Zoom));
static void ZoomReset() => FluentApp.SetZoom(ZoomLadder.Default);
```

`SetZoom` clamps, drops no-change writes, pushes the factor into the live window (which re-derives the effective
scale and re-lays-out — the same route as a per-monitor DPI change), then raises `ZoomChanged`. UI thread only.
`In`/`Out` saturate at the ladder ends, so mashing Ctrl+= past 250% is a clean no-op.

## The chords (recommended set)

Register accelerators with the invisible zero-size `BoxEl` idiom (the same pattern as the gallery's Ctrl+K
palette node). Accelerator matching is **exact on modifiers**, so Ctrl+Shift+= (the layouts where `+` is a shifted
key) needs its own registration next to Ctrl+=:

| Shortcut | Registration | Action |
|---|---|---|
| **Ctrl+=** | `Keys.OemPlus`, `Ctrl` | Zoom in |
| **Ctrl+Shift+=** (Ctrl+`+`) | `Keys.OemPlus`, `Ctrl\|Shift` | Zoom in |
| **Ctrl+NumPad +** | `Keys.Add`, `Ctrl` | Zoom in |
| **Ctrl+-** | `Keys.OemMinus`, `Ctrl` | Zoom out |
| **Ctrl+Shift+-** | `Keys.OemMinus`, `Ctrl\|Shift` | Zoom out |
| **Ctrl+NumPad -** | `Keys.Subtract`, `Ctrl` | Zoom out |
| **Ctrl+0** | `Keys.D0`, `Ctrl` | Reset to 100% |
| **Ctrl+NumPad 0** | `Keys.NumPad0`, `Ctrl` | Reset to 100% |
| **Ctrl+mouse wheel** | `InputHooks.ZoomWheel` (below) | Zoom in / out |

```csharp
static Element ZoomChord(int key, KeyModifiers mods, Action invoke) => new BoxEl
{
    Width = 0f, Height = 0f, HitTestVisible = false,   // invisible; a global accelerator fires its OnClick from anywhere
    Accelerator = new KeyAccelerator(key, mods),
    OnClick = invoke,
};

// Mount ONCE near the app root (e.g. a ZStack lane over the shell):
static Element ZoomChords() => ZStack(
    ZoomChord(Keys.OemPlus,  KeyModifiers.Ctrl,                       ZoomIn),
    ZoomChord(Keys.OemPlus,  KeyModifiers.Ctrl | KeyModifiers.Shift,  ZoomIn),
    ZoomChord(Keys.Add,      KeyModifiers.Ctrl,                       ZoomIn),
    ZoomChord(Keys.OemMinus, KeyModifiers.Ctrl,                       ZoomOut),
    ZoomChord(Keys.OemMinus, KeyModifiers.Ctrl | KeyModifiers.Shift,  ZoomOut),
    ZoomChord(Keys.Subtract, KeyModifiers.Ctrl,                       ZoomOut),
    ZoomChord(Keys.D0,       KeyModifiers.Ctrl,                       ZoomReset),
    ZoomChord(Keys.NumPad0,  KeyModifiers.Ctrl,                       ZoomReset)
);
```

Wavee claims exactly this set — see [shortcuts.md](./shortcuts.md).

## Ctrl+wheel (`InputHooks.ZoomWheel`)

The dispatcher exposes one hook for the browser Ctrl+wheel gesture. It is invoked for a Ctrl+wheel **after**
element-level `OnPointerWheel` handlers declined it (a control's own Ctrl+wheel still wins under the pointer) and
**before** the viewport scrolls; returning `true` consumes the notch so Ctrl+wheel never scrolls. Registered from
the tree via the `InputHooks` ambient:

```csharp
sealed class ZoomHotkeys : Component
{
    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        UseEffect(() =>
        {
            hooks.ZoomWheel = notch =>          // signed device notches: >0 = wheel rotated away = zoom in
            {
                FluentApp.SetZoom(notch > 0 ? ZoomLadder.In(FluentApp.Zoom)
                                            : ZoomLadder.Out(FluentApp.Zoom));
                return true;                    // consume — the viewport never scrolls this notch
            };
            return () => hooks.ZoomWheel = null;   // unregistered = Ctrl+wheel scrolls exactly as before
        });
        return ZoomChords();                    // pair it with the chord set above
    }
}
```

**Mouse wheels only:** the Windows backend consumes Ctrl + hi-resolution/touchpad wheel input as pinch synthesis
before events reach the dispatcher, so this hook only ever sees detented physical mouse wheels. A touchpad
Ctrl+two-finger-scroll or pinch does not drive app zoom in v1.

## Show the level (`Viewport.Zoom`)

The host publishes the current factor as an ambient for **display only** — a settings row, a "110%" flyout badge:

```csharp
var zoom = UseContext(Viewport.Zoom);           // re-renders this component on each step
Text($"{ZoomLadder.Percent(zoom)}%");
```

Never use it for coordinate or size math: `Viewport.Scale` (and every DIP↔px conversion in the engine) already
**contains** the zoom. If you multiply by `Viewport.Zoom` yourself you are applying it twice.

## Limitations (v1)

- **Primary window only.** `FluentApp.SetZoom`/`Zoom`/`ZoomChanged` drive the window `FluentApp.Run` created;
  detached/secondary windows keep their own DPI-only scale (a documented non-goal for v1).
- **No touchpad pinch.** See the Ctrl+wheel caveat above — only detented mouse wheels and the key chords step zoom.
- **The minimum window size stays physical.** `AppOptions.MinWidth/MinHeight` (min-track) are enforced at raw DPI,
  so at 250% the DIP viewport can drop below the layout minimum your app was designed for. Give big containers
  honest scroll/compact fallbacks, and sanity-check the top ladder steps the way you check a 640px window.
- **Persistence is yours.** The engine never writes settings; wire `ZoomChanged` → your store as shown above.

## Under the hood (pointers, for the curious)

The seam contract — effective `Scale` = OS DPI × zoom, `WindowDesc.Zoom`, `IPlatformWindow.SetZoom`, what stays
raw-DPI in the Win32 backend — is canon in `docs/design/subsystems/pal-rhi.md` §1.2; the Ctrl+wheel dispatch
ordering is `docs/design/subsystems/input-a11y.md` §7B; why the ladder is discrete (scale-quantized glyph/path
cache keys) is `docs/design/subsystems/gpu-renderer.md`. Zoom is invisible to layout and to your components — it
arrives everywhere already folded into the one `float scale`.
