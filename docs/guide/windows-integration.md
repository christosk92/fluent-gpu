# Windows integration

How a FluentGpu app becomes a Windows citizen rather than a window that happens to run on Windows. This page is the hub: each surface links to the reference that owns it.

Everything here is **additive and fail-soft by construction** — an app that ignores all of it still runs, and a machine where a surface is unavailable (no shell, elevated process, unpackaged where a manifest is required) degrades to "that surface is absent", never to a crash.

## The one rule

**There is exactly one activation intake.** Every OS surface that can launch or wake the app — a protocol link, a toast click or button, a jump-list task, a taskbar thumb button, a second launch redirected to the running instance — routes through the *same* parsed-verb channel. Two intakes means a feature that works from the toast but not the jump list, and a bug you can only reproduce from one of them.

In Wavee that channel is `DeepLinkChannel` and the verbs are `wavee://…`. See [`.claude/skills/wavee/deep-linking.md`](../../.claude/skills/wavee/deep-linking.md) for the verb map and boot order.

## The surfaces

| Surface | What the user sees | Library | Reference |
|---|---|---|---|
| **Protocol + single instance** | Links open the app already running, not a second copy | `FluentGpu.WindowsApi/Activation/` — `SingleInstanceGate`, `ProtocolRegistrar`, `ActivationArgs` | [deep-linking](../../.claude/skills/wavee/deep-linking.md), [pitfalls](../../.claude/skills/wavee-native/pitfalls.md) |
| **Taskbar** | Progress on the taskbar button, a play/pause overlay badge, prev / play-pause / next thumbnail buttons | `Shell/TaskbarManager` | [taskbar](../../.claude/skills/wavee-native/taskbar.md), [bridges](../../.claude/skills/wavee/bridges.md) |
| **Notification area (tray)** | An icon by the clock — from an `.ico`, raw pixels, or a drawn glyph with a live badge — with a native menu | `Shell/NotifyIcon`, `Shell/TrayIconArt` | [Tray icons](#tray-icons-notifyicon) below |
| **Jump list** | Right-click the taskbar button: tasks + a "Jump back in" category | `Shell/JumpList` | [where-to-change-what](../../.claude/skills/wavee-native/where-to-change-what.md) |
| **SMTC** | The system media flyout, lock screen, and hardware media keys | `Media/` | [bridges](../../.claude/skills/wavee/bridges.md) |
| **Toasts** | Action Center notifications, live-updating progress, and OS-scheduled delivery with the app closed | `Notifications/` — `ToastNotifier`, `ToastBuilder`, `ToastImageCache` | [pitfalls](../../.claude/skills/wavee-native/pitfalls.md), [bridges](../../.claude/skills/wavee/bridges.md) |
| **Power** | Playback keeps the machine awake; sleep/resume is handled rather than survived | `Power/PowerSession` | [power-network](../../.claude/skills/wavee-native/power-network.md) |
| **Network cost** | Metered connections quietly cap quality and defer prefetch | `Network/` — `NetworkStatus`, `NetworkCost` | [power-network](../../.claude/skills/wavee-native/power-network.md) |
| **Compute devices (NPU)** | Which NPU this PC has, for on-device AI features | `Devices/ComputeAdapters` — `ComputeAdapterInfo`, `ComputeAdapterVendors` | XML docs on `ComputeAdapters` (DXCore; fail-soft, any thread, ~1-5 ms — not per frame) |
| **Mouse & keyboard navigation** | Mouse side buttons and Back/Forward keys navigate | engine PAL seam `IPlatformApp.AppNavigationCommand` | [pal-rhi](../design/subsystems/pal-rhi.md), [shortcuts](shortcuts.md) |
| **System theme** | Light/dark and accent follow the OS, reduced motion is honoured | engine (`FluentApp.SystemColorsChanged`, `Motion.ReducedMotion`) | [rendering-and-performance](rendering-and-performance.md) |

## Which layer owns what

- **`FluentGpu.WindowsApi`** — OS *services*: shell, notifications, power, network, activation, storage, credentials, compute devices. AOT-clean hand-rolled Win32/WinRT interop, no WindowsAppSDK and no CsWinRT. It knows nothing about your app.
- **The engine PAL (`IPlatformApp`)** — OS *input and window* events that must reach the frame loop: activation redirects, thumb-button clicks, taskbar-button-created, navigation commands, colour-settings changes. Payloads are plain scalars so the engine stays TerraFX-free, and every one is delivered on the **UI thread** with the same stash-and-drain discipline (the backend stashes, wakes a frame, and re-raises at the top of `Paint`), so handlers may mutate signals directly.
- **Your app** — the bridges that mirror app state onto those surfaces, and the single verb router that consumes activations.

A surface that needs both (the taskbar thumb buttons are *produced* by `WindowsApi` but *clicked* through the PAL) is the normal shape, not a design smell.

## Threading, in one table

| Where it runs | What that means for you |
|---|---|
| PAL events (`FluentApp.*`) | **UI thread**, top of a frame. Navigate, write signals, touch host state freely. |
| Shell / taskbar / jump-list calls | Call on the **UI thread** with the real HWND (`FluentApp.WindowHandle`). Jump-list commits need an STA. |
| `PowerSession.KeepAwake` | **Per-thread** API — acquire *and* dispose on the same (UI) thread. |
| `PowerSession.Suspending` / `Resumed` | Raised on an OS power-broadcast worker — **hop to the UI thread** before touching playback. |
| Toast activation | Arrives on a WinRT callback thread — set `ActivationDispatcher` to your UI post so handlers land on the UI thread. |
| Network cost reads | A dedicated MTA reader thread inside `NetworkStatus`; results are posted back. |

## Tray icons (NotifyIcon)

`Shell/NotifyIcon` is a notification-area icon on its own hidden callback window: create it on the UI thread, and its
`Activated` events arrive there. It survives explorer restarts (re-added on `TaskbarCreated`) and reports taskbar DPI and
light/dark changes as `NotifyIconEvent.ShellChanged`. The image comes from one of three sources; the last one set wins.

```csharp
var tray = new NotifyIcon(Guid.Empty, "My app");   // Guid.Empty = window + id identity (unsigned dev builds)

// 1. An .ico file, loaded at the frame the taskbar DPI selects and reloaded when that size changes.
tray.SetIcon(icoPath);

// 2. Raw pixels: tightly packed, top-down, PREMULTIPLIED BGRA, width × height × 4 bytes (1–256 px a side).
tray.SetIcon(pixels, size, size);   // draw at NotifyIcon.SmallIconSizeForDpi(tray.TaskbarDpi) for a crisp frame

// 3. A source: re-run on every DPI / light-dark change (and after an explorer restart that changed either).
tray.SetIconSource(r => TrayIconArt.Glyph(r, "", badge: unread.ToString()));
tray.Show();
```

- **`TrayIconArt.Glyph(request, glyph, ink, badge, badgeFill, fontPath)`** draws a font glyph on the CPU with DirectWrite
  at `request.SizePx` (clamped to 16–48). The glyph comes from `fontPath` if you pass one, then Segoe Fluent Icons, then
  Segoe MDL2 Assets on Windows 10. The first face with every character wins. The ink defaults to white on a dark taskbar
  and `#1B1B1B` on a light one. The glyph is centred on its ink at whole-pixel offsets, so the font's hinting is kept.
- **The badge** is a rounded pill in the bottom-right corner, 9/16 of the icon tall, holding 1–3 characters (longer text
  is cut) in condensed semibold digits (Bahnschrift, falling back to Segoe UI). It is cut out of the glyph with a 1 px gap
  (2 px from 32 px up). The default fill is Fluent "critical" red. The text is white or near-black, whichever contrasts.
- **A changing badge** means calling `SetIconSource` again with the new value, or `RefreshIconSource()` when the callback
  reads live state. Each call builds one new `HICON` (a 32bpp DIB plus a 1bpp mask passed to `CreateIconIndirect`). The
  previous handle is destroyed only after the shell has the new one. This is cheap, but it is a shell round-trip, so do not
  call it per frame.
- **Premultiplied means an alpha-0 pixel is colour 0** and no channel exceeds its alpha. DEBUG builds assert this. A span
  whose length is not `width × height × 4` throws `ArgumentException`. A side outside 1–256 throws
  `ArgumentOutOfRangeException`. A callback that throws or returns a malformed bitmap is logged, and the previous image stays.
- On a disposed icon every setter is a no-op (`false`).

The gallery's Windows APIs page ("Notification area icon" card, **Glyph + badge**) is the manual check: the badge counts
up on each press, and switching the taskbar between light and dark redraws the ink.

## Storage: settings and app-data folders

`FluentGpu.WindowsApi/Storage/` gives an unpackaged app typed per-user settings without package identity. It replaces `ApplicationData.Current.LocalSettings`.

```csharp
var store = AppDataStore.ForUnpackaged("Contoso", "MyApp");
store.SetInt("volume", 70);
int volume = store.GetInt("volume", fallback: 50);
string cache = store.CacheFolder;            // %LOCALAPPDATA%\Contoso\MyApp\Cache
Console.WriteLine(store.RegistryPath);       // HKCU\Software\Contoso\MyApp\Settings

// Reactive variant: one write-through Signal<T> per key, bound straight to a control.
var settings = SettingsStore.ForUnpackaged("Contoso", "MyApp", ctx.Runtime);
Signal<bool> muted = settings.Bool("muted");
```

| What | Where |
|---|---|
| Setting values | Registry values in the **`\Settings` subkey**: `HKCU\Software\<publisher>\<product>\Settings` (`RegistryPath`). `Clear()` deletes only that subkey, not the product key above it. |
| `LocalFolder` | `%LOCALAPPDATA%\<publisher>\<product>`, for durable data |
| `CacheFolder` | `%LOCALAPPDATA%\<publisher>\<product>\Cache`, for data the app can rebuild |
| `TempFolder` | `%TEMP%\<publisher>\<product>`, for scratch files |

- **Each name is one segment.** `ForUnpackaged` throws `ArgumentException` (with `ParamName` set to `publisher` or `product`) when a name is empty, contains `\` or `/`, has leading or trailing whitespace, or is `.`/`..`. Before this check, such names silently nested or escaped the key and the folders. Inner spaces are fine (`"Contoso Ltd"`).
- **Types are kept.** string → `REG_SZ`, bool/int → `REG_DWORD`, long/double → `REG_QWORD`, `byte[]` → `REG_BINARY`. A typed `Get*` returns its fallback if the stored value has a different type, so you never get a reinterpreted value.
- **There is no change watcher.** Writes are atomic per value and the last writer wins. Another process changing a value does not notify this one.
- **Use one `SettingsStore` per process.** Each instance keeps its persist effects alive for its whole lifetime, so don't create one per component.
- **Packaged apps** run the same code. Windows transparently virtualizes HKCU and LocalAppData into the package.

## Packaged vs unpackaged

Most of this works either way, but not all of it:

- **Unpackaged** registers its own AUMID, protocol associations and toast-activator `LocalServer32` in **HKCU**.
- **Packaged (MSIX)** takes those from the manifest instead, and the library's registry writes deliberately no-op. The toast activator CLSID must be the *same value* in code and in the manifest — see the CLSID note in [bridges](../../.claude/skills/wavee/bridges.md).
- **Packaged only:** `windows.backgroundTasks` / `BackgroundTaskBuilder` (a `TimeTrigger` task that runs with the app closed). Scheduled toasts (`ToastNotifier.Schedule`) need **no** package identity, so prefer them whenever the work is "notify at a known time" rather than "go and look while closed".

## Verifying

`dotnet build src/FluentGpu.slnx` (Debug **and** Release) plus `dotnet run --project src/FluentGpu.VerticalSlice`. None of these surfaces are covered by the headless gates — they are OS side effects — so the acceptance for each is a **user-run pass**: click the link, press the thumb button, sleep and resume the machine, tether to a phone. When a PAL seam is added or changed, reconcile [`docs/design/subsystems/pal-rhi.md`](../design/subsystems/pal-rhi.md) and run `powershell -File docs/design/check-canon.ps1`.
