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
| **Jump list** | Right-click the taskbar button: tasks + a "Jump back in" category | `Shell/JumpList` | [where-to-change-what](../../.claude/skills/wavee-native/where-to-change-what.md) |
| **SMTC** | The system media flyout, lock screen, and hardware media keys | `Media/` | [bridges](../../.claude/skills/wavee/bridges.md) |
| **Toasts** | Action Center notifications, live-updating progress, and OS-scheduled delivery with the app closed | `Notifications/` — `ToastNotifier`, `ToastBuilder`, `ToastImageCache` | [pitfalls](../../.claude/skills/wavee-native/pitfalls.md), [bridges](../../.claude/skills/wavee/bridges.md) |
| **Power** | Playback keeps the machine awake; sleep/resume is handled rather than survived | `Power/PowerSession` | [power-network](../../.claude/skills/wavee-native/power-network.md) |
| **Network cost** | Metered connections quietly cap quality and defer prefetch | `Network/` — `NetworkStatus`, `NetworkCost` | [power-network](../../.claude/skills/wavee-native/power-network.md) |
| **Mouse & keyboard navigation** | Mouse side buttons and Back/Forward keys navigate | engine PAL seam `IPlatformApp.AppNavigationCommand` | [pal-rhi](../design/subsystems/pal-rhi.md), [shortcuts](shortcuts.md) |
| **System theme** | Light/dark and accent follow the OS, reduced motion is honoured | engine (`FluentApp.SystemColorsChanged`, `Motion.ReducedMotion`) | [rendering-and-performance](rendering-and-performance.md) |

## Which layer owns what

- **`FluentGpu.WindowsApi`** — OS *services*: shell, notifications, power, network, activation, storage, credentials. AOT-clean hand-rolled Win32/WinRT interop, no WindowsAppSDK and no CsWinRT. It knows nothing about your app.
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

## Packaged vs unpackaged

Most of this works either way, but not all of it:

- **Unpackaged** registers its own AUMID, protocol associations and toast-activator `LocalServer32` in **HKCU**.
- **Packaged (MSIX)** takes those from the manifest instead, and the library's registry writes deliberately no-op. The toast activator CLSID must be the *same value* in code and in the manifest — see the CLSID note in [bridges](../../.claude/skills/wavee/bridges.md).
- **Packaged only:** `windows.backgroundTasks` / `BackgroundTaskBuilder` (a `TimeTrigger` task that runs with the app closed). Scheduled toasts (`ToastNotifier.Schedule`) need **no** package identity, so prefer them whenever the work is "notify at a known time" rather than "go and look while closed".

## Verifying

`dotnet build src/FluentGpu.slnx` (Debug **and** Release) plus `dotnet run --project src/FluentGpu.VerticalSlice`. None of these surfaces are covered by the headless gates — they are OS side effects — so the acceptance for each is a **user-run pass**: click the link, press the thumb button, sleep and resume the machine, tether to a phone. When a PAL seam is added or changed, reconcile [`docs/design/subsystems/pal-rhi.md`](../design/subsystems/pal-rhi.md) and run `powershell -File docs/design/check-canon.ps1`.
