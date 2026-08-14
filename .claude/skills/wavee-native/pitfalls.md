# FluentGpu.WindowsApi — pitfalls

Every entry below is a contract the OS actually enforces, not a style preference. Several were hit while landing
Jump List categories, network cost, and toast Update/Schedule.

---

## Jump List — never re-add a user-removed destination

`ICustomDestinationList.BeginList` returns an `IObjectArray` of destinations the user has removed (the little
"unpin" / "remove from this list" affordance). **Re-adding any of those in the same Begin/Commit transaction
makes `AppendCategory` / `CommitList` fail.** `JumpList` filters category items **and** user tasks against that
list by comparing `IShellLinkW.GetArguments` to `JumpListItem.Arguments` / `JumpTask.Arguments`.

A Jump List cannot be patched: every publish is a full rebuild. `SetCategory` that omits `tasks` clears the
Tasks section. Pass both in one call.

STA-only. AUMID must match the process AUMID / toast registration or the tasks attach to the wrong (or no)
taskbar group.

---

## Thumb bar is add-once per HWND

`ITaskbarList3.ThumbBarAddButtons` succeeds **once** for a given HWND's lifetime. A second call fails (the
buttons are already there). Updates go through `ThumbBarUpdateButtons`; new buttons after first-add are not
a thing. See [taskbar.md](taskbar.md) / `TaskbarManager` — do not "fix" a missing button by calling Add again.

---

## AUMID / CLSID must be one value, three places

The App User Model ID and the toast-activator CLSID are identity, not configuration:

1. `SetCurrentProcessExplicitAppUserModelID` / unpackaged `AumidRegistration` / packaged manifest `Application@Id`
2. `JumpList.SetAppID` / `SetTasks`/`SetCategory` `aumid:` argument
3. `ToastNotifier.Register(activatorClsid)` — **and** (packaged) the manifest `ToastActivatorCLSID` +
   `com:ExeServer` class id, **and** (unpackaged) `HKCU\…\CLSID\{clsid}\LocalServer32`

Mismatch ⇒ toasts show under a ghost app, Jump List tasks vanish, clicks do not activate this process.
Register `Activated` handlers **before** `Register` so the class object is `REGCLS_MULTIPLEUSE` (in-proc
repeated clicks) rather than `SINGLEUSE`.

Unpackaged `LocalServer32` command line carries `----AppNotificationActivated:` — the same sentinel
`ActivationArgs` classifies. Unregister on uninstall or the HKCU keys leak.

---

## NotificationData sequence numbers are monotonic and silent

`INotificationData.SequenceNumber` is how the OS drops out-of-order live updates. An update whose sequence is
**≤ the last applied one is ignored** (you may still get `Succeeded`; the banner does not move).
`ToastNotifier.Update` auto-increments per `(tag, group)` under its lock starting at 1 — do not also stamp a
lower number from the caller, there is no setter.

A `Show` of the same tag replaces the toast and resets the *OS* sequence to 0; our next `Update` still has a
higher local counter, so it applies. `NotificationNotFound` means the toast expired / was dismissed — not a
COM failure.

Data-bound progress only updates placeholders that exist in the XML (`Progress(dataBound: true)` emits
`{progressValue}` / `{progressStatus}` / …). Updating a toast that baked literal values (`dataBound: false`)
is a no-op.

---

## Scheduled toasts outlive the process — reconcile on launch

`IToastNotifier.AddToSchedule` is **OS-delivered**. The process does not need to be running at
`deliveryTime`; the Shell holds the XML and posts it. Consequences:

- On launch, `CountScheduled()` is not zero just because this process did not schedule anything this run —
  yesterday's queue is still there. Walk it (`Unschedule` by tag/group, or compare `CountScheduled` against
  what this session believes is pending) and drop stale entries (album that already released, reminder whose
  content is obsolete).
- Scheduling the same tag twice **adds a second pending toast**; it does not replace. `Unschedule(tag, group)`
  first if the identity is meant to be unique.
- A past `deliveryTime` makes `AddToSchedule` fail — `Schedule` returns `false` (same HRESULT-as-bool shape as
  `Show`), it does not throw.
- `Show`/`Schedule`/`Update` all require `Register` first (`InvalidOperationException`). `Unschedule` /
  `RemoveByTag` / `CountScheduled` no-op when unregistered, matching `RemoveByTag`.
- Elevated processes: `ToastNotifier.IsSupported == !IsElevated`. `Show` does not itself early-out on that
  flag; callers check it. Keep new methods on the same convention.

---

## Threading (the failures look like "nothing happened")

- **Jump List / file picker = STA.** `RPC_E_CHANGED_MODE` on `CoInitializeEx(APARTMENTTHREADED)` means the
  thread is already MTA — do not swallow it for the picker (`FilePicker.EnsureSta` throws). Jump List
  currently tolerates it the way `SetTasks` always did; still call it from the UI thread.
- **KeepAwake is per-thread.** Dispose on the same thread that created the handle or you clear the wrong
  flag and leak the original `ES_CONTINUOUS` until that thread exits.
- **NLM on the UI thread blocks.** `IsOnline` / `GetConnectivity` / cost can stall on NCSI. Use `ReadAsync` /
  `ReadCostAsync` (dedicated MTA reader). Cost QI failure ⇒ `NetworkCost.Unknown` / `IsMetered == false` —
  do not treat that as "definitely unmetered" for a billing decision; it is fail-soft so a probe never
  throttles playback.
- **Callbacks are not the UI thread.** Toast `Activated`, NLM `Subscribe`, Power suspend/resume, SMTC
  `ButtonPressed` arrive on an OS worker. Install `ActivationDispatcher` / `ButtonDispatcher` before
  relying on thread affinity.
- **Do not `RoUninitialize` after `Show`.** Action Center reads the XML asynchronously; tearing the
  apartment down drops the toast. `RoInitialize` S_FALSE / `RPC_E_CHANGED_MODE` are benign.

---

## COM / AOT

- No CsWinRT, no `[ComImport]`, no `ComWrappers` subclassing on call-OUT. TerraFX vtable or a hand-declared
  slot struct. Getting a slot index wrong silently calls the neighbouring method (`INetworkListManager`
  starts at slot 7 because of `IDispatch`; `INetworkCostManager` starts at slot 3 because it is `IUnknown`).
- `IMap<HSTRING,HSTRING>` / `IVectorView<T>` generics in TerraFX are the reason live-update was a
  fast-follow. Fill NotificationData via the hand-rolled `IStringMap` (slot 11 = Insert). Do not implement
  `IIterable<IKeyValuePair>` just to call the factory overload.
- `CoRegisterClassObject` needs a raw `IUnknown*` from `StrategyBasedComWrappers.GetOrCreateComInterfaceForObject`,
  not a marshaled object, or NativeAOT ExeServer registration silently no-ops.
- `Show` returning S_OK does not paint a banner. Read `ToastNotifier.Setting`. Toasts in an elevated
  process genuinely do not work.

## Single instance + protocol: the gate must outlive the window, and the payload arrives twice

`SingleInstanceGate.TryAcquire(name, windowClass, payload)` is a **named-mutex + `WM_COPYDATA`** pair, not a lock you can
let go of:

- **Keep the gate alive for the whole process.** Disposing it after the check releases the name, and the next launch
  becomes a second "primary" — two windows, two audio sessions. Dispose in the `finally` around the run, not after the
  `if`.
- **The secondary must exit without initializing anything.** It has already handed its payload over; if it goes on to
  register protocols or touch app data it races the primary over the same files.
- **The forwarded message needs a receiver window that already exists.** `TryAcquire` finds the primary by window
  class, so the class name passed here must match the one the PAL actually creates (`"FluentGpuWindow"`), and the
  primary must have pumped at least one message. A payload sent before the window exists is dropped, not queued.
- **A cold launch delivers the payload through `ActivationArgs`, a warm one through the redirect event.** Both paths
  must post into the SAME intake (`DeepLinkChannel`), or a link works only when the app was already running (or only
  when it was not). Test both.
- `ProtocolRegistrar` writes **HKCU** and no-ops when packaged (the manifest owns the association) — so a packaged
  build silently ignores a scheme the unpackaged build registers. Do not "fix" that by calling it anyway; add the
  manifest entry.
- Registering a scheme you did not invent (`spotify:`) is a **user decision**, not a feature: gate it on an explicit
  opt-in setting, and unregister on the way back down so the toggle is symmetric.
