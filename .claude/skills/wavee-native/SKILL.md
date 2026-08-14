---
name: wavee-native
description: Use when changing FluentGpu.WindowsApi — the AOT-clean Windows OS-services library (Shell jump lists + taskbar, Activation, Power, Network including cost/metered, Notifications including live Update and scheduled toasts, Storage, Dialogs, Media/SMTC, Credentials, Packaging). Read before adding a pillar API, a WinRT/COM call-out, or touching packaged-vs-unpackaged registration. Not the D3D12 backend (FluentGpu.Windows) and not the Wavee app.
---

# FluentGpu.WindowsApi — native OS services

Scope: `src/FluentGpu.WindowsApi/**` only. Engine work: the repo-root [fluentgpu](../fluentgpu/SKILL.md)
skill. Wavee app wiring: the [wavee](../wavee/SKILL.md) skill. **Do not** edit `FluentGpu.Windows` or
`FluentGpu.Engine` from this skill, and **do not** read `src/apps/.native/**` / PlayPlay paths.

`FluentGpu.WindowsApi` is the WinAppSDK-shaped OS-services surface (toasts, jump lists, network, power,
credentials, …) with **zero CsWinRT, zero `Microsoft.WindowsAppSDK` NuGet, zero reflection on the call-OUT
path**. The lone package ref is `TerraFX.Interop.Windows`. Hand-roll what TerraFX does not project (the
Network List Manager / cost manager is the exemplar). MSIX packaging itself stays app-side (`.wapproj`);
this library only *queries* identity via `Packaging/PackageIdentity`.

## Pillar map

| Pillar | What it is | Entry types |
|---|---|---|
| **Shell** | Taskbar button progress/overlay + Jump List user tasks and custom categories | `TaskbarManager`, `JumpList` / `JumpTask` / `JumpListItem`. Thumb-bar / overlay details: **[taskbar.md](taskbar.md)** (sibling; owned with `TaskbarManager.cs`). |
| **Activation** | Unpackaged protocol / file / startup registration + command-line classification + single-instance redirect | `ProtocolRegistrar`, `ActivationArgs`, `SingleInstanceGate` |
| **Power** | Keep-awake (per-thread `SetThreadExecutionState`) + suspend/resume | `PowerSession.KeepAwake`, `PowerSession.Subscribe`, `PowerStatus` |
| **Network** | Online / connectivity level + **cost/metered** (`INetworkCostManager`) | `NetworkStatus.IsOnline` / `ReadAsync` / `ReadCostAsync` → `NetworkCost` (`IsMetered` = Fixed or Variable) |
| **Notifications** | Toast XML builder + Show + **live `Update`** (NotificationData) + **`Schedule`/`Unschedule`/`CountScheduled`** + activator | `ToastNotifier`, `ToastBuilder`, `ToastUpdateResult` |
| **Storage** | Unpackaged-friendly typed settings (HKCU) + signal-backed write-through | `AppDataStore`, `SettingsStore` |
| **Dialogs** | Vista file/folder pickers (`IFileOpenDialog` / `IFileSaveDialog`) | `FilePicker` |
| **Media** | System Media Transport Controls (now-playing flyout / hardware keys) | `SystemMediaControls` |
| **Credentials** | Win32 Credential Manager (not PasswordVault) | `CredentialStore` |
| **Packaging** | Runtime "am I packaged?" + AUMID / family / version | `PackageIdentity` |

## Packaged vs unpackaged

Branch on `PackageIdentity.IsPackaged`. Sparse / external-location identity still reports packaged.

| Concern | Unpackaged | Packaged (MSIX) |
|---|---|---|
| Toast AUMID + activator | `AumidRegistration` writes HKCU AppUserModelId + `LocalServer32` (`----AppNotificationActivated:`) | Manifest owns AUMID + `ToastActivatorCLSID` / `com:ExeServer`. Runtime registry writes are skipped (the platform throws). Still `CoRegisterClassObject` the activator. |
| Protocol / file / startup | `ProtocolRegistrar` writes HKCU `\Software\Classes` | Do **not** call — `E_ILLEGAL_METHOD_CALL`. Declare in the manifest. |
| Jump List / taskbar | Works; pass the **same** AUMID you registered for toasts | Works; AUMID comes from the manifest |
| Storage / credentials / power / network | Identity-free; same code both ways (HKCU/LocalAppData is virtualized when packaged) | Same |
| Toast images | `http(s)://` sources are dropped; localize via `ToastImageCache` to `ms-appdata:///local/…` | Packaged `ms-appx:///…` is legal |

## Threading rules (break these and it looks like "the API does nothing")

- **UI-thread HWND calls.** `TaskbarManager.*`, `FilePicker.*`, `SystemMediaControls.GetForWindow` take the real FluentGpu window HWND and must run on the thread that owns it. Do not invent an HWND on the Engine seam.
- **STA for Jump Lists (and the file picker).** `JumpList.SetTasks` / `SetCategory` / `Clear` and `FilePicker` require an STA. The gallery UI thread is STA via `[STAThread]` on `Program.Main`. Jump List COM is apartment-threaded; a Jump List is per-AUMID, not per-window.
- **OS-callback-thread events.** Toast `Activated`, NLM `Subscribe`, Power suspend/resume, SMTC `ButtonPressed` fire on an arbitrary COM/RPC/power worker. Hop through the host dispatcher (`ToastNotifier.ActivationDispatcher`, SMTC `ButtonDispatcher`) before touching UI. Do not block the callback.
- **Per-thread KeepAwake.** `SetThreadExecutionState` is per calling thread. Create and dispose `PowerSession.KeepAwake` on the **same** stable thread (the UI thread). Disposing on another thread clears *that* thread's flag and leaks the original request.
- **NLM reads off the UI thread.** `NetworkStatus.ReadAsync` / `ReadCostAsync` run on a dedicated long-lived MTA reader. Do not call `IsOnline` / `GetConnectivity` inline from a frame.

## COM doctrine (the one that keeps AOT green)

- **Call-OUT** (we call the OS): TerraFX vtable struct, or a hand-declared one (`INetworkListManager`, `INetworkCostManager`, `IStringMap`, `IScheduledToastView`). No `[ComImport]`, no `ComWrappers` on this path.
- **Call-IN** (the OS calls us): `[GeneratedComInterface]` / `[GeneratedComClass]` (`ToastActivatorCallback`, `INetworkListManagerEvents`, SMTC button handler).
- Activation factories are process-stable — cache them (`RoGetActivationFactory` once, `RoActivateInstance` per XmlDocument / NotificationData). Do not `RoUninitialize` after `Show` (Action Center pulls XML asynchronously).

## Verify

```powershell
dotnet build src/FluentGpu.slnx
dotnet build src/FluentGpu.slnx -c Release
```

Both configurations — diagnostics const-gates fold differently. Do not launch the app. File map: [where-to-change-what.md](where-to-change-what.md). Traps: [pitfalls.md](pitfalls.md). Wavee keep-awake / metered-quality wiring: [power-network.md](power-network.md).
