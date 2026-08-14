# FluentGpu.WindowsApi — where to change what

Paths are relative to the repo root. All of this lives under `src/FluentGpu.WindowsApi/`. Do not edit
`FluentGpu.Windows` / `FluentGpu.Engine` / `src/apps/Wavee/**` from this skill.

---

## Shell — Jump List

| Want | Edit |
|---|---|
| User-task entries (Tasks section) | `Shell/JumpList.cs` — `JumpTask` + `SetTasks(aumid, params JumpTask[])` |
| One custom category + optional tasks in **one** Begin/Commit | `JumpList.SetCategory(categoryTitle, JumpListItem[], JumpTask[]? tasks = null, string? aumid = null)` |
| Shared shell-link construction (path/args/icon/`PKEY_Title`) | private `CreateLink` in `JumpList.cs` — reused by tasks and category items |
| Removed-items filter (user pinned-off a destination) | `CollectRemovedArguments` — compares `IShellLinkW.GetArguments` to `Arguments` |
| Wipe the custom list | `JumpList.Clear(aumid)` |
| Taskbar progress / overlay / thumb bar | `Shell/TaskbarManager.cs` + `Shell/TaskbarProgressState.cs` — see [taskbar.md](taskbar.md) |

STA thread. Pass the same AUMID the process advertised (`SetCurrentProcessExplicitAppUserModelID` / toast `Register`).

---

## Network — connectivity + cost

| Want | Edit |
|---|---|
| Online / coarse level | `Network/NetworkStatus.cs` — `IsOnline`, `GetConnectivity`, `ReadAsync` → `NetworkSnapshot` |
| Connectivity enum | `Network/NetworkConnectivityLevel.cs` |
| **Cost / metered** | `Network/NetworkCost.cs` (`NetworkCostKind`, `NetworkCost.IsMetered`) + `NetworkStatus.ReadCostAsync` |
| Hand-declared NLM / cost COM | `Network/NetworkListManagerInterop.cs` — `INetworkListManager`, `INetworkCostManager` (IID `DCB00008-…`), `NLM_CONNECTION_COST` bits |
| Change subscription | `Network/NetworkListManagerEventSink.cs` + `NetworkStatus.Subscribe` |

Cost is QI'd off the **same** `CLSID_NetworkListManager` coclass. `GetCost(null dest)` = current connection. Fail-soft to `NetworkCost.Unknown` (unmetered-conservative). Reads go through the dedicated MTA reader.

---

## Notifications — toasts

| Want | Edit |
|---|---|
| Fluent XML (no XML at the call site) | `Notifications/ToastBuilder.cs` / `Toast.Create()` — `Progress(dataBound: true)` emits `{progressValue}` placeholders |
| Show / register / activator | `Notifications/ToastNotifier.cs` — `Register` / `Show` / `Activated` + `ActivationDispatcher` |
| **Live update** | `ToastNotifier.Update(IReadOnlyDictionary<string,string> values, string tag, string? group = null)` → `ToastUpdateResult`. Sequence auto-increments per tag/group. |
| **Scheduled toasts** | `Schedule(ToastBuilder, DateTimeOffset, tag, group)` / `Unschedule(tag, group)` / `CountScheduled()`. OS-delivered — process need not be running. |
| Progress payload type | `Notifications/ToastProgress.cs` + `ToastUpdateResult` |
| HSTRING RAII + hand-rolled `IMap` / scheduled `IVectorView` | `Notifications/ToastInterop.cs` (`HStringHandle`, `IStringMap`, `IScheduledToastView`) |
| Unpackaged AUMID + `LocalServer32` | `Notifications/AumidRegistration.cs` |
| COM activator (call-IN) | `Notifications/ToastActivator.cs` |
| Image localize (unpackaged http) | `Notifications/ToastImageCache.cs` |
| Scenario / sound / delivery setting | `Notifications/ToastEnums.cs` |

Interop choice for Update: `RoActivateInstance("Windows.UI.Notifications.NotificationData")` → `INotificationData.get_Values` → hand-rolled `IStringMap.Insert` (call-OUT). Not the factory's `CreateNotificationDataWithValuesAndSequenceNumber(IIterable<IKeyValuePair>)` — that would be a call-IN collection we would have to implement. Delivery: `IToastNotifier2.UpdateWithTag` / `UpdateWithTagAndGroup`. Schedule: TerraFX `IScheduledToastNotificationFactory.CreateScheduledToastNotification(xml, WinRTDateTime)` + `IToastNotifier.AddToSchedule`.

---

## Other pillars (existing)

| Pillar | Files |
|---|---|
| Activation | `Activation/ProtocolRegistrar.cs`, `ActivationArgs.cs`, `SingleInstanceGate.cs` |
| Power | `Power/PowerSession.cs` (`KeepAwake`, `Subscribe`, `ReadPower`) |
| Storage | `Storage/AppDataStore.cs`, `Storage/SettingsStore.cs` |
| Dialogs | `Dialogs/FilePicker.cs` — UI/STA, owner HWND |
| Media / SMTC | `Media/SystemMediaControls.cs`, `MediaButtonHandler.cs`, `MediaPositionChangeHandler.cs`, `MediaEnums.cs` |
| Credentials | `Credentials/CredentialStore.cs`, `StoredCredential.cs`, `CredentialScope.cs` |
| Packaging | `Packaging/PackageIdentity.cs` |
| Marker | `WindowsApiInfo.cs` |

---

## Adding a WinRT call-OUT (the toast/SMTC pattern)

1. Runtime class name as a `const string`.
2. `RoGetActivationFactory` (statics/factory, cache it) **or** `RoActivateInstance` (activatable instance — XmlDocument, NotificationData).
3. `__uuidof<T>()` QI into the TerraFX vtable struct. If TerraFX lacks the interface or the generic (`IMap<K,V>`, `IVectorView<T>`) is awkward, hand-declare a slot-indexed struct in the pillar's `*Interop.cs` (copy `INetworkCostManager` / `IStringMap`).
4. `HStringHandle` for every `HSTRING` you create; `WindowsDeleteString` every `[out] HSTRING` the ABI hands you.
5. Release in reverse acquisition order. Never `RoUninitialize` after a `Show`.
