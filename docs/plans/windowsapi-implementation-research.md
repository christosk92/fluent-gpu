# FluentGpu.WindowsApi — Implementation Research

> Status: design research, read-only against `C:/WAVEE/WindowsAppSDK` (WASDK product source) and the live `C:/WAVEE/fluent-gpu` tree. No engine code for this pillar exists yet — `src/FluentGpu.WindowsApi/` is a scaffold (`WindowsApiInfo.IsScaffold = true`) with four empty folders. This document is the source-of-truth a future implementation is built from.
>
> Every mechanic claim about the Windows App SDK is cited to a WASDK source path (`path:line`). Post-cutoff facts (CsWinRT / WASDK AOT status, unpackaged toast-image and shortcut behavior) are cited to primary Microsoft docs. The three input research maps were adversarially re-verified against source; where a map was wrong, the **corrected** fact is carried here and the correction is noted inline.
>
> Honesty discipline (per repo `CLAUDE.md`): these are cold app-service paths, not per-frame — allocation is fine; what matters is **AOT/trim compatibility, packaged-vs-unpackaged behavior, dependency posture, and servicing**. Where something genuinely cannot be settled without a live test on the target OS build, it is flagged in **§5 Open Questions**, not glossed.

---

## 1. Executive summary + dependency-strategy decision

`FluentGpu.WindowsApi` is the OS-services pillar — a WinAppSDK-*shaped* surface (toast notifications, credential vault, package identity, activation) for FluentGpu apps, with the **WAVEE** Spotify-class client as the driving workload (media toasts, secure token storage, `wavee://` protocol activation, single-instance redirect, optional MSIX distribution).

### The decision

**Hand-roll the OS interop in the house `[LibraryImport]` / `[GeneratedComInterface]` style. Do NOT take the `Microsoft.WindowsAppSDK` NuGet. Do NOT adopt CsWinRT. Add `TerraFX.Interop.Windows` directly to `FluentGpu.WindowsApi.csproj` as a self-owning peer of `FluentGpu.Windows` — never project-reference `FluentGpu.Windows`.**

Rationale, in priority order:

1. **The headline feature is not free under the NuGet anyway.** Local media toasts are WAVEE's driving need. `AppNotifications` (and `PushNotifications`) retain a hard dependency on the WASDK **Singleton** package — an *additional* MSIX — even in self-contained deployment: *"push notifications APIs … and app notifications APIs … have a dependency on the Singleton package … Deploy the required MSIX packages as part of your app installation"* (Microsoft Learn, `deploy-self-contained-apps`, dated 2026-05-28). So Option A pays the full framework cost **and** still requires extra deployment for the one thing FluentGpu most wants. (Honesty correction from verification: the doc hedges *"there are some cases where the APIs will work even without the Singleton … calling `IsSupported` … is often a good idea"* — so it is "generally needs the Singleton," not "always" — but for guaranteed availability you ship the extra MSIX.)

2. **The interop surface to hand-bind is small and bounded.** Three of the four pillars are **pure Win32, zero WinRT, zero COM** (Credentials, Packaging, Activation). The *entire* WinRT ABI that must be hand-bound is the toast-submit path: ~6 interfaces / ~8 methods, a one-time cost. Total realistic budget is **~600–900 LoC of cold interop**.

3. **It is the only option congruent with the repo's COM doctrine.** `design/dotnet10-csharp14-zero-alloc.md §4`: cold COM (all of WindowsApi) = source-generated `[GeneratedComInterface]`/`[GeneratedComClass]`; **no `ComWrappers`/`StrategyBasedComWrappers` on any path**. CsWinRT's runtime is a `ComWrappers`-based activation layer — philosophically the opposite of the house rule even when it AOT-compiles. The WASDK NuGet contradicts the repo's founding bet (it built its own engine to escape heavyweight frameworks).

4. **NativeAOT + `TrimMode=full` is the shipping mode, and only the hand-rolled path keeps it first-party.** Options A and C couple FluentGpu to **CsWinRT's** AOT story, which is **still preview (3.0) as of June 2026** (`3.0.0-preview.260319.2`, build-stamped 2026-03-19; 2.2.0 from 2024-11-12 is the last stable — [CsWinRT releases](https://github.com/microsoft/CsWinRT/releases)), with live .NET 10 AOT regressions ([dotnet/runtime#121590](https://github.com/dotnet/runtime/issues/121590), [dotnet/sdk#53387](https://github.com/dotnet/sdk/issues/53387)). The hand-rolled path couples only to the stable OS ABI.

5. **Placement keeps the renderer out of the way.** TerraFX is a **binding-only** package (P/Invoke declarations + blittable structs; no native DLLs, no runtime). Adding it to `FluentGpu.WindowsApi.csproj` makes WindowsApi a self-owning peer of `FluentGpu.Windows`. An app wanting only credentials gets `Engine + TerraFX`, never the D3D12 backend. Routing through `FluentGpu.Windows` (the PAL/RHI/GPU surface) would transitively pull the entire renderer into a process that wants `CredReadW` — rejected.

### Important correction to the AOT framing (folded in from verification)

Earlier maps marked CsWinRT and the WASDK NuGet as flatly "Red / AOT-disqualified." **That is no longer true and this report does not repeat it.** CsWinRT generated projections have been AOT-compatible since **2.1.1** (source-generator/build-task, not reflection — [CsWinRT aot-trimming docs](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md)), and WASDK **1.6+** supports `PublishAot` including self-contained ([WASDK 1.6 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6)). They are rejected here on **dependency-weight + functional-unsuitability + the repo's COM doctrine**, *not* on an AOT impossibility. This matters for honesty and for the carve-outs (§4): CsWinRT remains a legitimate, AOT-capable *fallback for the notification projection only* if the hand-bound WinRT vtables overrun.

### The two narrow carve-outs to the "no NuGet" rule

- **(a) WinRT-toast vtable overrun → CsWinRT for the notification *consumption* path only** (never the full WASDK). Note CsWinRT 3.0 currently ships *projection/consumption only — authoring not yet*, so even in this fallback the `INotificationActivationCallback` activator still goes through the hand-rolled COM path; CsWinRT would cover only `XmlDocument`/`ToastNotifier` consumption.
- **(b) Cloud push** ("Now Playing" toasts pushed from a backend) is the *only* case to re-evaluate WASDK (Singleton + Azure WNS), as a packaged-only, opt-in, `IsSupported()`-gated feature orthogonal to everything below. Unpackaged third-party apps cannot be WNS push targets without the broker.

### Where the interop lives, concretely

| Pillar | Interop kind | Recommended dependency |
|---|---|---|
| Credentials | flat Win32 (`advapi32`) | hand `[LibraryImport]`, **no NuGet** (`advapi32` is already in the AOT native-link inputs) |
| Packaging | flat Win32 (`kernel32`) | hand `[LibraryImport]`, **no NuGet** |
| Activation | flat Win32 (registry, kernel32, user32 named-objects, `WM_COPYDATA`) | TerraFX (or hand `[LibraryImport]`) |
| Notifications | cold COM (`[GeneratedComInterface]`) + hand WinRT ABI | **TerraFX** (exposes `RoGetActivationFactory`, `CoRegisterClassObject`, `INotificationActivationCallback`, HSTRING) |

Add `<PackageReference Include="TerraFX.Interop.Windows" Version="10.0.26100.6" />` (the exact version `FluentGpu.Windows.csproj` already pins) to `FluentGpu.WindowsApi.csproj` for the COM-bearing pieces; the flat Win32 pillars ride the same reference (or stay hand-declared — both are AOT-clean). A future `FluentGpu.Interop` *decls leaf* (distinct from the existing `FluentGpu.Interop.SourceGen` Roslyn generator, which is a compile-time codegen scaffold referenced by nothing yet) is the right home only if `Windows` and `WindowsApi` start sharing many declarations — premature now.

---

## 2. Per-pillar designs

### 2.1 Notifications — local toasts + activation

#### Mechanics (what the OS owns, what WASDK adds)

The toast stack has three OS-owned primitives. WASDK does **not** add a notification renderer — it is a registration/activation broker over these:

1. **`Windows.UI.Notifications.ToastNotificationManager`** (WinRT, in-box since Win8) — submits toast XML.
2. **An AUMID** (App User Model ID) the toast is attributed to. Packaged: `{PackageFamilyName}!{AppId}` from the manifest, auto-registered. Unpackaged: registered in `HK...\Software\Classes\AppUserModelId\…`.
3. **A COM activator** — a class object implementing `INotificationActivationCallback` (IID `53e31837-6600-4a81-9395-75cffe746f94`, declared in the **Windows SDK** header `NotificationActivationCallback.h` — *not* WASDK; confirmed absent from `dev/`), registered under the AUMID's `CustomActivator` CLSID so the Shell can call back or relaunch the process on click.

**Packaged `Register()`** branches on `IsPackagedProcess()` (`dev/AppNotifications/AppNotificationManager.cpp:133-141`). It reads the CLSID the package installer wrote via `GetComRegistrationFromRegistry(L"----AppNotificationActivated:")` (`:218`), iterating `HKLM\SOFTWARE\Classes\PackagedCom\Package\{PackageFullName}\Class` and matching the COM server whose `Arguments == "----AppNotificationActivated:"`. The manifest is the registration mechanism; `Register()` only does the runtime `CoRegisterClassObject`.

**Unpackaged `Register()`** (`dev/AppNotifications/AppNotificationManager.cpp:228-244`) is the gold — five steps FluentGpu replicates with plain Win32:

- **(i) AUMID derivation** — `RetrieveUnpackagedNotificationAppId()` (`dev/AppNotifications/AppNotificationUtility.cpp:43-96`): honor `GetCurrentProcessExplicitAppUserModelID()` if set; else open/create `HKCU\Software\Classes\AppUserModelId\{ConvertPathToKey(exePath)}` (every `\` → `.`, `AppNotificationUtility.h:22-32`) and read/create a `NotificationGUID` REG_SZ via `CoCreateGuid` on first run — **that GUID becomes the stable AUMID** across runs.
- **(ii) COM activator CLSID** — `GetActivatorGuid()` reads `…\AppUserModelId\{AppGUID}\CustomActivator` and integrity-checks its `CLSID\{guid}\LocalServer32` (`AppNotificationUtility.cpp:197-226`); `GetOrCreateComActivatorGuid()` (`:246-270`) mints a fresh GUID on `ERROR_FILE_NOT_FOUND`. *(Verification correction: the read + corruption check live in `GetActivatorGuid`, not `GetOrCreateComActivatorGuid` — functionally as described.)*
- **(iii) `LocalServer32`** — `RegisterComServer()` (`AppNotificationUtility.cpp:114-136`) writes `HKCU\Software\Classes\CLSID\{activatorGuid}\LocalServer32 = "C:\path\app.exe" ----AppNotificationActivated:`.
- **(iv) Asset registration** — `RegisterAssets()` (`AppNotificationUtility.cpp:298-328`) writes `DisplayName`, `IconUri` (local file only), and `CustomActivator` under `…\AppUserModelId\{AppGUID}`.
- **(v) COM server activation** — `CoRegisterClassObject(clsid, factory, CLSCTX_LOCAL_SERVER, flag | REGCLS_AGILE, &cookie)` (`AppNotificationManager.cpp:190-208`). **The flag is `REGCLS_MULTIPLEUSE` only if `NotificationInvoked` handlers were registered before `Register()`; otherwise `REGCLS_SINGLEUSE`** (`:197`). `REGCLS_AGILE` is always present (an STA thread registers into the neutral apartment). The factory is an `IClassFactory` whose `CreateInstance` returns the singleton (`AppNotificationManager.h:72-93`).

**No Start-Menu shortcut is required** for unpackaged toasts. *(Verification correction — this resolves a tension the input maps disagreed on.)* For the classic `ToastNotificationManager` path on a modern unpackaged app, the AUMID info **resides in the `AppUserModelId` registry hive rather than requiring a shortcut** (Microsoft Learn, [Send a local toast from other unpackaged apps](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/send-local-toast-other-apps)): register `HKCU\Software\Classes\AppUserModelId\<AUMID>` with `DisplayName`, `IconUri`, `IconBackgroundColor`, `CustomActivator`. The legacy `IShellLink`+`IPropertyStore` shortcut carrying `System.AppUserModel.ID` is the *older* mechanism the registry path supersedes. **`StartMenuShortcut.cs` is NOT needed** — this drops the largest sub-task the earlier estimates carried.

**Submission and the UDK boundary.** WASDK's `Show()` (`AppNotificationManager.cpp:424-454`) calls the FrameworkUDK `ToastNotifications_PostToast`, **not** the public `IToastNotifier::Show`. *You cannot crib WASDK's exact submission call* — the UDK header (`frameworkudk/toastnotifications.h`) is an OS-redist FluentGpu won't link. FluentGpu must **build against the in-box public WinRT** `ToastNotificationManager`/`IToastNotifier` surface instead (the older, stable API):

```
RoActivateInstance("Windows.Data.Xml.Dom.XmlDocument") → IXmlDocumentIO::LoadXml(hstring payload)
RoGetActivationFactory("Windows.UI.Notifications.ToastNotification") → IToastNotificationFactory::CreateToastNotification(xmlDoc) → IToastNotification
RoGetActivationFactory("Windows.UI.Notifications.ToastNotificationManager") → IToastNotificationManagerStatics::CreateToastNotifierWithId(aumid) → IToastNotifier
IToastNotifier::Show(toast)
```

IIDs (verified real OS surface in the in-box `windows.ui.notifications.idl`): `IToastNotifier` `75927b93-03f3-41ec-91d3-6e5bac1b38e7`, `IToastNotificationManagerStatics` `50ac103f-d235-4598-bbef-98fe4d1a3ad4`, `IToastNotificationFactory` `04124b20-82c6-4229-b109-fd9ed4662b53`, `IXmlDocumentIO` `6cd0e74e-ee65-4489-9ebf-ca43e87ba637`.

**Activation dispatch.** `INotificationActivationCallback::Activate(aumid, invokedArgs, NOTIFICATION_USER_INPUT_DATA*, count)` (`AppNotificationManager.cpp:346-422`): builds args from the `launch=`/button `arguments=` string + the user-input array; on first activation with `----AppNotificationActivated:` on the command line (cold-launched by the Shell), it stores args and signals the main thread; with foreground handlers it fires inline.

**Skip the cloud/push infrastructure for local toasts.** *(Verification correction — restated precisely.)* `RegisterUnpackagedApp` calls `PushNotifications_RegisterFullTrustApplication(m_appId, GUID_NULL)` **unconditionally** at `AppNotificationManager.cpp:230` (only the LRP sink `RegisterAppNotificationSinkWithLongRunningPlatform` is `!IsSelfContained()`-gated, `:237`). FluentGpu omits **both** because (1) `RegisterFullTrustApplication` is an unlinkable OS FrameworkUDK API, and (2) the legacy `DesktopNotificationManagerCompat` local-toast path proves a non-push toast needs only AUMID + CLSID + `LocalServer32` registry + `CoRegisterClassObject`. Do **not** state "it's gated, so skip it." Whether omitting it degrades any local edge case is the one thing to confirm end-to-end (§5).

**`IsSupported == !IsElevated`** (`AppNotificationManager.cpp:107`) — toasts genuinely do not work in an elevated process.

#### Toast XML template (cribbed from `AppNotificationBuilder`)

`BuildNotification()` (`dev/AppNotifications/AppNotificationBuilder/AppNotificationBuilder.cpp:379-391`) assembles:

```xml
<toast{timestamp}{ scenario='…'}{ launch='…'}{ useButtonStyle='true'}>
  <visual><binding template='ToastGeneric'>
    {text…}{image…}{progress…}
  </binding></visual>
  {audio}{<actions>…</actions>}
</toast>
```

Hard limits to copy: payload ≤ 5120 bytes (`AppNotificationBuilderUtility.h:11`), ≤ 3 `<text>`, ≤ 5 buttons, ≤ 5 inputs. Image elements (verified exact, `AppNotificationBuilder.cpp`):
- `<image placement='appLogoOverride' src='…' hint-crop='circle'/>` — circular album thumbnail (`:144`).
- `<image placement='hero' src='…'/>` — full-width banner (`:166`).
- `<image src='…'/>` — inline (`:106`).

**Encoding rules (replicate or toasts break / inject):** XML body escapes `& " < > '` (`AppNotificationBuilderUtility.h:24-34`). `launch`/`arguments` key-values percent-encode `%`→`%25`, `;`→`%3B`, `=`→`%3D` *then* XML-escape (`:99-167`), because `;`/`=` delimit `key=value;key2=value2`. The activation parser reverses this.

#### The unpackaged web-image trap (riskiest finding — load-bearing for WAVEE)

**Unpackaged apps cannot use `http(s)://` toast images.** Microsoft Learn (`send-local-toast-other-apps`), verbatim and corroborated twice: *"Unpackaged apps don't support HTTP images; you must download the image to your local app data, and reference it locally. HTTP images are supported only in packaged apps that have the internet capability in their manifest."* The `<image src>` template is a bare URI passthrough — WASDK does **not** rescue this.

Consequence for WAVEE: Spotify album-art URLs (`https://i.scdn.co/…`) **cannot** be dropped straight into an unpackaged toast. `ToastSender` must, for unpackaged apps: download the JPEG to `%LOCALAPPDATA%\WAVEE\toastimg\{hash}.jpg`, reference it as `ms-appdata:///local/toastimg/{hash}.jpg`, and cache it (album art repeats). A **packaged** WAVEE build with the `internet` capability can use the `https://` URL directly — the single cleanest argument for WAVEE choosing MSIX, while the local-download path keeps unpackaged fully functional. (`ms-appx:///` is also unavailable unpackaged; `file:///` works but `ms-appdata` survives Action Center persistence after exit.)

#### Recommended FluentGpu design

Cold COM, hand-rolled. **Use hand-vtable `calli` for the WinRT call-*out* interfaces** (the repo reserves exactly this in `FluentGpu.Windows/Interop/Placeholder.cs`: "consume = `lpVtbl[n]` calli"), and reserve `[GeneratedComInterface]`/`[GeneratedComClass]` for the one interface FluentGpu must *implement* — `INotificationActivationCallback`. *(Verification refinement: this split is more house-consistent and sidesteps the unproven "`[GeneratedComInterface]` over an `IInspectable`-prefixed vtable" pattern — see §5.)*

```csharp
namespace FluentGpu.WindowsApi.Notifications;

// Pure managed string assembly — the only fully portable, no-interop file.
public sealed class ToastBuilder
{
    public ToastBuilder AddText(string s);                              // ≤3, XML-escaped
    public ToastBuilder SetAppLogoOverride(string src, bool circle = false);
    public ToastBuilder SetHeroImage(string src);
    public ToastBuilder AddButton(string content, string argKey, string argValue); // ≤5
    public ToastBuilder AddArgument(string key, string value);          // percent + xml encoded
    public ToastBuilder SetScenario(ToastScenario s);                  // Default/Reminder/Alarm/IncomingCall/Urgent
    public ToastBuilder SetAudioEvent(ToastSound s);                   // or MuteAudio()
    public string BuildXml();                                         // throws if >5120 bytes
}

public sealed class ToastManager                 // singleton; the cold-COM owner
{
    public static ToastManager Default { get; }
    public static bool IsSupported { get; }       // == !IsElevated  (AppNotificationManager.cpp:107)

    // Register handlers BEFORE Register() for in-proc routing (→ REGCLS_MULTIPLEUSE).
    public event Action<ToastActivatedArgs>? Activated;

    public void Register(string? displayName = null, string? localIconPath = null);
    public void Unregister();                     // revoke CoRegisterClassObject cookie
    public void UnregisterAll();                  // + delete registry keys/icon (unpackaged)

    public void Show(string toastXml, string? tag = null, string? group = null);
}

public readonly struct ToastActivatedArgs        // from invokedArgs + NOTIFICATION_USER_INPUT_DATA
{
    public string Argument { get; }                            // the launch= / arguments= string
    public IReadOnlyDictionary<string,string> Arguments { get; } // parsed key=value;…
    public IReadOnlyDictionary<string,string> UserInput { get; } // TextBox/ComboBox by id
}
```

Files: `ToastBuilder.cs` (string only), `ToastXmlSubmit.cs` (the one WinRT-ABI surface), `ToastActivator.cs` (`[GeneratedComInterface] INotificationActivationCallback` + hand `IClassFactory` + `CoRegisterClassObject`), `AumidRegistration.cs` (packaged `GetCurrentApplicationUserModelId`; unpackaged `NotificationGUID` derivation + asset/`LocalServer32` writes), `ToastImageCache.cs` (unpackaged web-image → `ms-appdata` localizer).

**AOT note (fold-in):** declare `CoRegisterClassObject` `[PreserveSig]` with `in Guid rclsid` / `nint pUnk` / `out uint lpdwRegister`, and pass the factory as a raw `ComWrappers`-produced `nint` — [CsWin32 #1670](https://github.com/microsoft/CsWin32/issues/1670) shows it silently no-ops in NativeAOT + ExeServer otherwise. **Validate TerraFX's prebuilt signature against this** before assuming it marshals the factory pointer correctly.

#### Host wiring

- **Set the AUMID before the window is created** — inject in `FluentApp.Run` at `new Win32App()` (`src/FluentGpu.WindowsApp/FluentApp.cs:34`), before the pump. *(Confirmed gap: `Win32Platform.cs:233` window class is `"FluentGpuWindow"`; no `SetCurrentProcessExplicitAppUserModelID` is called anywhere — grep-empty.)*
- **Marshal `Activate` onto the UI thread.** `INotificationActivationCallback::Activate` fires on an arbitrary COM thread (`REGCLS_AGILE`). Do **not** raise `Activated` synchronously. Stash args in a thread-safe slot and `PostMessage(hwnd, WM_APP+n, …)` to the `"FluentGpuWindow"` HWND; the existing `Handle32` WndProc (`Win32Platform.cs:573`) handles it, and `AppHost.WakeFrame()` (`AppHost.cs:465`) runs a frame. **This producer genuinely needs the `PostMessage` hop** because it is cross-thread — unlike the activation-redirect producer (§2.2), which is already on the UI thread. (`WakeFrame` is two unsynchronized bool writes, `AppHost.cs:465-468` — not thread-safe; the message hop is what makes it safe.)
- **Seam:** add `event Action<ToastActivatedArgs>? ToastActivated` to `IPlatformApp` (`Seams/Pal/Pal.cs:173`), modeled on the existing outbound `OpenUri(string)` (`:185`) already wired at `AppHost.cs:400`.

#### Packaged / unpackaged matrix

| Capability | Packaged (MSIX) | Unpackaged (Win32 exe) |
|---|---|---|
| Show local toast | ✅ AUMID from manifest | ✅ AUMID from `NotificationGUID` registry derivation |
| Activation callback (in-proc) | ✅ CLSID from manifest/PackagedCom | ✅ CLSID from `CustomActivator`+`LocalServer32` |
| Cold-launch on click | ✅ | ✅ via `LocalServer32` |
| `Register(displayName, icon)` custom assets | ❌ throws — use manifest | ✅ (icon must be local file) |
| **`http(s)://` web image** | ✅ **iff `internet` capability** | ❌ **must download → `ms-appdata:///local/`** |
| `ms-appdata:///local/` / `file:///` image | ✅ | ✅ |
| Start-Menu shortcut needed | ✅ no | ✅ no (registry-based AUMID) |
| Cloud/WNS push toast | ✅ (Azure WNS + channel) | ⚠ needs WASDK Singleton broker — **out of scope** |
| Works when elevated | ❌ `IsSupported==false` | ❌ `IsSupported==false` |

#### Effort & risks

Effort: **M**. `ToastBuilder` (S), AUMID/registry (S), `[GeneratedComInterface]` activator + `IClassFactory` + `CoRegisterClassObject` (M), `ms-appdata` image cache (S). The **one genuinely fiddly slice** is the hand-bound WinRT XmlDocument/ToastNotifier call-out (~150–250 LoC; `IInspectable`-prefixed vtables + HSTRING create/delete) — the schedule risk and the trigger for carve-out (a). Risks: the unpackaged web-image trap (mitigated by the localizer); the AOT `CoRegisterClassObject` marshaling pitfall; cold-launch of an AOT exe by the Shell (§5).

---

### 2.2 Activation — protocol/file/startup + single-instancing

#### Mechanics

**Unpackaged registration is per-user registry writes only.** `ActivationRegistrationManager` is **unpackaged-only**: every public method opens with `THROW_HR_IF(E_ILLEGAL_METHOD_CALL, IsPackagedProcess())` (`dev/AppLifecycle/ActivationRegistrationManager.cpp:45,76,89`). All writes go to **`HKEY_CURRENT_USER`** (`GetRegistrationRoot()` hardcodes it, `Association.cpp:8-11`) — **no HKLM/all-users in v1** (spec: *"For v1, the platform will support only per-user registration"*). Every register/unregister ends with `SHChangeNotify(SHCNE_ASSOCCHANGED, …)` (`Association.cpp:436-439`).

WASDK's actual registry shape routes through a **ProgId indirection** (`Software\Classes\<progId>\shell\open\command` + `OpenWithProgids` + a `RegisteredApplications`/`Capabilities` block), where `appId = "App." + hex(std::hash(lowercase(exePath)))` — a `std::hash`, **implementation-defined and non-portable** (`Association.cpp:32-33`). *(Verification correction: a FluentGpu reimpl must NOT try to byte-match WASDK's `appId`; we own both writer and reader.)* The launched command line is `exePath "----ms-protocol:<uri>"` — the Shell substitutes the full URI for `%1` (`GenerateCommandLine`, `ActivationRegistrationManager.cpp:26-27,173`; envelope constants `c_argumentPrefix="----"`, `c_msProtocolArgumentString="ms-protocol"` at `ActivationRegistrationManager.h:10-12`). Startup = the plain `HKCU\…\CurrentVersion\Run\<taskId>` key (`ActivationRegistrationManager.h:15`) — not Task Scheduler, no user-toggle contract.

**Activation discovery.** `Microsoft.Windows.AppLifecycle.AppInstance.GetActivatedEventArgs()` (`dev/AppLifecycle/AppInstance.cpp:471-556`): if packaged, it calls the **platform** `Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()` **first** and casts `ActivationKind → ExtendedActivationKind` (`:485-491`); command-line parsing is the fallback. Kinds: values 0–1026 mirror `Windows.ApplicationModel.Activation.ActivationKind` verbatim (`File=3`, `Protocol=4`, `StartupTask=39`); the only WASDK additions are `Push=5000` and `AppNotification=5001` (by enum succession). There is **no `ExtensionBase=4096`** (input-map correction).

**Single-instancing IPC is pure Win32 named kernel objects — no COM server, no pipe, no socket.** *(Verification correction — the input map said `WM_COPYDATA`; WASDK uses none.)* Objects namespaced by `ComputeAppId()` (exe-path hash) so all instances share a namespace:

| Object | Role | Cite |
|---|---|---|
| Named mutex `<module>_<key>_Mutex` | key-ownership election (create-vs-open race, 0 ms wait) | `AppInstance.cpp:586,597,601` |
| Named file-mapping `<module>_Module` | `SharedProcessList` = `DWORD[512]` of live PIDs | `SharedProcessList.h:9-10` |
| Named file-mapping `<module>_<pid>_RedirectionQueue` | 4096-slot GUID queue | `RedirectionRequestQueue.h:10-19` |
| Named file-mapping `<proc>_RedirectionRequest_<guid>` | the **COM-marshaled** `AppActivationArguments` | `RedirectionRequest.cpp:19-57` |
| Named events (doorbell + per-request ack) | wake target / unblock sender | `AppInstance.cpp:208-213,259` |

All mappings are pagefile-backed (`CreateFileMapping(INVALID_HANDLE_VALUE, …)`, `SharedMemory.h:98`). Args cross processes via `CoMarshalInterface(…, MSHCTX_LOCAL, …)` (`RedirectionRequest.cpp:36,55-57`), unmarshaled on the receiver (`AppInstance.cpp:97-98`). `FindOrRegisterForKey` = mutex election (`:338-351`); `RedirectActivationToAsync` (`:268`) writes the request, calls `AllowSetForegroundWindow(targetPid)` (`:256`), sets the doorbell, and blocks on the ack. **The receiver's `Activated` callback fires on a threadpool thread** (`m_activationWatcher`, `AppInstance.h:78`, `:134,205`) — WASDK's hardest integration constraint.

#### Recommended FluentGpu design — named mutex + `WM_COPYDATA` (simpler than WASDK)

WAVEE needs exactly: "second `wavee://…` launch ⇒ focus the running instance and hand it the URI." WASDK's 512-PID table / GUID queue / COM-marshaled arbitrary arg graphs exist for `GetInstances()`/arbitrary-key multi-instance — none of which a single-window client uses. **Present `WM_COPYDATA` as the *simpler hand-rolled choice*, not as "what WASDK does."** FluentGpu can be far simpler precisely because it only forwards a command-line *string*, not a marshaled `IActivatedEventArgs`.

Decisive reasons to diverge:
- `WM_COPYDATA` is the only `SendMessage` payload that **marshals a buffer across processes** (the OS copies `lpData`) — AOT-trivial (one `[LibraryImport]`, blittable `COPYDATASTRUCT`).
- It **arrives on the receiver's UI thread inside `WndProc`** — exactly where FluentGpu wants it. **No threadpool→UI-thread hop** (WASDK's hardest part). `ActivationRedirected.Invoke` may touch the non-thread-safe `WakeFrame` directly.
- It **wakes the idle frame loop for free.** `WaitForWork` is `MsgWaitForMultipleObjectsEx(0, null, timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE)` (`Win32Platform.cs:546-550`). `QS_ALLINPUT ⊇ QS_SENDMESSAGE` (verified: [GetQueueStatus](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getqueuestatus) defines `QS_SENDMESSAGE=0x0040` as "a message sent by another thread or application"), so a sent `WM_COPYDATA` satisfies the wake mask and is dispatched by the kernel inside `PeekMessageW` (`:536-540`). A named-event doorbell (WASDK's mechanism) would **not** wake this loop without adding a wait-handle slot to `WaitForWork`. (`FluentApp.Run`'s idle branch parks in `WaitForWork(host.RecommendedWaitMs())` = `-1` when idle, `FluentApp.cs:84` — exactly the parked case this wakes.)

**Use `SendMessageTimeoutW`, not bare `SendMessageW`** *(verification fix)* — so a mid-`Paint` receiver cannot hang the exiting second instance:

```csharp
// Second-instance detection, any thread, before the window exists:
mutex = CreateMutexW(null, true, "Local\\Wavee.SingleInstance");
if (GetLastError() == ERROR_ALREADY_EXISTS) {
    var target = FindWindowW("FluentGpuWindow", null);                  // Win32Platform.cs:233
    if (target != 0) {
        AllowSetForegroundWindow(ASFW_ANY);                            // mirror AppInstance.cpp:256
        var cds = new COPYDATASTRUCT { dwData = WAVEE_ACTIVATE,
                                       cbData = (uint)(uri.Length*2), lpData = pUri };
        SendMessageTimeoutW(target, WM_COPYDATA, 0, ref cds,
                            SMTO_ABORTIFHUNG | SMTO_NORMAL, 5000, out _);
    }
    return;                                                            // do NOT create a window
}
```

Receiver — a new `WM_COPYDATA` case in `Win32Window.Handle32` (none exists today — grep-empty):

```csharp
case WM_COPYDATA:
    var cds = *(COPYDATASTRUCT*)lParam;
    if ((nuint)cds.dwData == WAVEE_ACTIVATE) {
        string uri = new string((char*)cds.lpData, 0, (int)cds.cbData / 2);
        SetForegroundWindow(hWnd);                                     // required; OS blocks steals otherwise
        ActivationRedirect?.Invoke(uri);                               // UI thread → safe to WakeFrame
        result = (LRESULT)1; return true;
    }
    return false;
```

The activation payload is a string — it **cannot ride the POD `InputEvent` ring** (`Pal.cs:56`; `EnqueueExternal` is POD-only, `Win32Platform.cs:364`). It needs the side channel above.

```csharp
namespace FluentGpu.WindowsApi.Activation;

public enum ActivationKind : byte { Launch = 0, File = 3, Protocol = 4, Startup = 39 }

public readonly record struct ActivationArgs(ActivationKind Kind, string Argument)
{
    public bool TryGetUri(out Uri uri) => Uri.TryCreate(Argument, UriKind.Absolute, out uri!);
    public static ActivationArgs FromCommandLine(ReadOnlySpan<string> argv, string scheme); // Classify
}

public static class ProtocolRegistrar     // pure HKCU writes + SHChangeNotify; no-op/throw if packaged
{
    public static void Register(string scheme, string exePath, string displayName, string? iconPath = null);
    public static void Unregister(string scheme, string exePath);     // RegDeleteTree HKCU\Software\Classes\<scheme>
}

public sealed class SingleInstanceGate : IDisposable
{
    // false + forwards payload to the running instance when one exists (caller then exits)
    public bool TryAcquire(string instanceId, string windowClass, string activationPayload);
}
```

**Own command-line convention** (`wavee.exe "wavee://…"`) — no reason to copy WASDK's `----ms-protocol:` envelope when we control both writer and parser. `Classify(argv)`: scan argv for a `Uri.TryCreate(Absolute)` whose scheme is `wavee`, else `Launch`.

#### Host wiring

- `IPlatformApp` gains a default-interface-method event (house pattern — the interface already uses `GetWorkArea => RectF.Infinite`, `CreatePopupWindow => null`):
  ```csharp
  event Action<string>? ActivationRedirected { add { } remove { } } // UI-thread delivery; default never fires
  ```
  Headless keeps the no-op default (test-neutral). **Document the invariant:** delivered on the UI thread; any cross-thread producer (the toast-COM callback) must `PostMessage` to hop first.
- `AppHost` subscribes next to the outbound `OpenUri` seam (`AppHost.cs:400`): `app.ActivationRedirected += uri => { _pendingActivation = uri; WakeFrame(); };` and routes the URI through the existing ambient-signal machinery (`_reconciler.SetAmbient(...)`, `AppHost.cs:439-442`), drained at the top of `Paint()`.
- `FluentApp.Run` runs the gate + registration **before `new Win32App()`** (`FluentApp.cs:34`): register `wavee://` if unpackaged, `TryAcquire` the single-instance gate (forward + exit if a primary exists). `Environment.ProcessPath` is the AOT-clean exe path. *(Note: `Win32App`/`Win32Window` live in namespace `FluentGpu.Pal.Windows`, not `FluentGpu.Windows`.)*

#### Packaged / unpackaged matrix

| | Packaged (MSIX, incl. sparse) | Unpackaged |
|---|---|---|
| Protocol/file/startup registration | **Manifest only** (runtime API throws `E_ILLEGAL_METHOD_CALL`) | **Runtime HKCU writes** |
| Scope | per-user via deployment | **per-user only** (`HKCU`); no HKLM in v1 |
| Platform `GetActivatedEventArgs` | available | **not available** (cmd-line only) |
| `wavee://` deep-link basic launch | ✅ | ✅ (classic `shell\open\command`) |
| Clean uninstall of registrations | OS-managed | **app must `Unregister`** (keys leak otherwise) |
| Single-instance redirect | mutex + `WM_COPYDATA` identical | identical |

**Do not assume a packaged protocol launch delivers the URI on the command line.** *(Verification correction — the input map's convenience shortcut is withdrawn for packaged.)* WASDK calls the platform `GetActivatedEventArgs` first when packaged (`AppInstance.cpp:485-491`), and Microsoft Learn ([Rich activation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-rich-activation)) states packaged apps retrieve a protocol URI via `AppInstance.GetActivatedEventArgs`, with plain command line documented only for `Launch`. The packaged build must either call the platform WinRT API (cold ABI: `RoGetActivationFactory` + the call-out vtable for `IProtocolActivatedEventArgs.Uri`) **or** WAVEE ships unpackaged-only and treats MSIX as a later, separate work item that brings the WinRT-args requirement with it. The hand-rolled table's "packaged-args" row is **Yellow** — that Yellow is now load-bearing.

#### Effort & risks

Effort: registration ~80 LoC (`RegCreateKeyExW`/`RegSetValueExW`/`RegDeleteTree`/`SHChangeNotify`); single-instance ~20–30 LoC (mutex + `WM_COPYDATA` + `COPYDATASTRUCT`); two small additive host-seam changes. The under-counted item is the **packaged** WinRT-args ABI if WAVEE ever ships MSIX. Risks: §5 items 4–6.

---

### 2.3 Packaging — runtime identity queries (the library) + app-side MSIX (the appendix)

#### Mechanics

**WASDK adds no independent identity surface** — its `AppModel::Identity::IsPackagedProcess()` is a thin wrapper over `GetCurrentPackageFullName`. The canonical probe (`dev/Common/AppModel.Identity.IsPackagedProcess.h:11-27`): call with a `nullptr` buffer and branch on the return code. **Tolerate two sentinels** *(verification precision)*: `ERROR_INSUFFICIENT_BUFFER` (122) ⇒ packaged (a name exists, buffer too small); `APPMODEL_ERROR_NO_PACKAGE` (15700 / 0x3D54) ⇒ unpackaged. WASDK keys "packaged" off `ERROR_INSUFFICIENT_BUFFER` (`:16`).

**Sparse / external-location identity** is the highest-value packaging option for FluentGpu: it lets the engine ship an unpackaged native exe (its whole reason to exist) **and** light up identity-gated OS features (notably WNS push) with no installer rewrite and no binaries inside the package (Microsoft Learn, [Grant package identity by packaging with external location](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)). A sparse-registered process returns `ERROR_INSUFFICIENT_BUFFER` — i.e., it reports **packaged** — so the `PackageIdentity` query type works unchanged.

#### Recommended FluentGpu design — `PackageIdentity` (the greenest folder)

All pure `kernel32`, all blittable → AOT-trivial flat `[LibraryImport]` (the `Win32Theme.cs:20-33` pattern: `[LibraryImport("kernel32.dll", …)] private static partial …` on a `static partial class`).

```csharp
namespace FluentGpu.WindowsApi.Packaging;

public static class PackageIdentity
{
    const int APPMODEL_ERROR_NO_PACKAGE = 15700; // unpackaged
    const int ERROR_INSUFFICIENT_BUFFER = 122;   // packaged (probe)

    public static bool IsPackaged { get; }                 // GetCurrentPackageFullName nullptr-probe
    public static string? PackageFullName { get; }         // GetCurrentPackageFullName
    public static string? PackageFamilyName { get; }       // GetCurrentPackageFamilyName
    public static string? ApplicationUserModelId { get; }  // GetCurrentApplicationUserModelId
    public static string? InstallLocation { get; }         // GetCurrentPackagePath
    public static Version? PackageVersion { get; }         // GetCurrentPackageInfo → PACKAGE_VERSION
}
```

Maps 1:1 to WASDK's auditable helpers: `GetCurrentPackage{FullName,FamilyName}`, `GetCurrentApplicationUserModelId` (`dev/Common/AppModel.Identity.h:18-42`), `PACKAGE_VERSION`/`PackageVersion` (`:165-241`), `GetCurrentPackageInfo` nullptr-probe (`AppModel.PackageGraph.h:21-30`). `ApplicationUserModelId` is the cleanest "am I identity-bearing, and what's my notification AUMID?" check the Notifications pillar needs to branch packaged-vs-unpackaged.

**What must NOT be in the library:** no MSIX authoring (`makeappx`/`signtool`), no `.wapproj`/manifest, no `PackageManager.AddPackage…` (installer responsibility), no bootstrapper. (`FluentGpu.WindowsApi.csproj` comment: *"MSIX packaging is deliberately NOT here … not by a class library."*)

**Interop posture: hand `[LibraryImport]`, no NuGet.** Packaging needs ~5 kernel32 functions; pulling TerraFX *just* for these is unjustified. If the sibling Notifications/Activation folders force TerraFX into WindowsApi, Packaging shares it — but alone it does not justify the dependency.

#### Packaged / unpackaged matrix

| | Unpackaged | Packaged (MSIX) | Sparse identity |
|---|---|---|---|
| `IsPackaged` reports | false | true | **true** (`ERROR_INSUFFICIENT_BUFFER`) |
| Identity-gated features (WNS push, file/protocol *modern* assoc) | ❌ | ✅ | ✅ |
| Code in the engine to enable | — | — | none (1 XML manifest + ~6 lines app-manifest + 2 installer cmds) |
| Signing required | n/a | ✅ | ✅ (the identity package itself) |
| Min OS | any | per manifest | **10.0.19041.0** (`AllowExternalContent`) |

#### Effort & risks

Effort: **S** (~60 LoC, the safest folder — flat kernel32, no COM/WinRT/reflection; `0%` trim risk). Risks: the sparse-identity **attach footgun** (§5) — identity attaches only if the embedded `<msix>` manifest values match the identity package AND `ExternalLocation` exactly equals the run directory, else it silently fails (no error; `Package.Current` null) or fails `0x80073D54`; the Explorer/shell-launch path is a documented problem surface. Because WAVEE's WNS path depends on identity attaching, this MUST be smoke-tested (§5).

---

### 2.4 Credentials — secure token storage

#### Mechanics (a/b/c)

- **(a) WASDK provides nothing.** A full grep of `dev/` for `PasswordVault|CredWrite|CredReadW|CredentialManager|PasswordCredential` returns **one** file — `dev/OAuth/OAuth.idl` — and that is OAuth-protocol vocabulary, *not* a store. There is no `dev/Credentials`.
- **(b) The OS provides directly:** the Win32 Credential Manager (`wincred.h`, `advapi32.dll`) and its WinRT face `PasswordVault` — *both backed by the same per-user Credential Locker*; plus DPAPI and Windows Hello (`KeyCredentialManager`).
- **(c) FluentGpu builds:** a thin `CredentialStore` over the four CredMan entry points.

**PasswordVault is the same store, with less and worse, for a full-trust desktop app.** The PasswordVault class page (updated 2025-11-21) states verbatim: *"Apps not running in an AppContainer (for example, regular Desktop apps) can access all the user's lockers."* So PasswordVault and CredMan read/write the **same** locker. Microsoft's own maintainer ([WASDK Discussion #1840](https://github.com/microsoft/WindowsAppSDK/discussions/1840)): *"PasswordVault doesn't work as expected for full-trust desktop apps."* The one thing PasswordVault could add over CredMan (per-app isolation) is exactly what it does NOT deliver for full-trust. *(Verification correction: the "use CredRead/CredWrite or DPAPI instead" guidance is the widely-used community pattern (Meziantou, AdysTech), NOT a prescription in thread #1840 — #1840 lists four migration options. Don't attribute the fix to that thread.)*

**Windows Hello / `KeyCredentialManager`** is identity-sensitive and unreliable unpackaged (reports range from `APPMODEL_ERROR_NO_PACKAGE` to working in some console hosts). *(Verification correction: the flat "fails unpackaged with 'no package identity'" was overstated and its cited winrt-api page does not substantiate it.)* Treat it as **packaged-only, out of scope for v1**; gate behind `PackageIdentity.IsPackaged` if WAVEE ever wants "unlock playback with Windows Hello."

#### Recommended FluentGpu design — raw CredMan, full stop

It is the only option simultaneously (1) AOT/trim-clean with no `ComWrappers`/CsWinRT, (2) **identity-free — works packaged AND unpackaged identically** (the one property PasswordVault/Hello lack), (3) zero heavyweight dependency, (4) already fully exposed by TerraFX, (5) the path Microsoft itself recommends for full-trust desktop apps.

Canonical signatures (`wincred.h`, SDK 10.0.26100.0): `CredWriteW` :864, `CredReadW` :887, `CredEnumerateW` :920, `CredDeleteW` :1007, `CredFree` :1399. **Partition guard is `WINAPI_PARTITION_DESKTOP | WINAPI_PARTITION_SYSTEM` (`:1239-1403`)** *(verification correction — NOT the desktop-only `:1407` block, which wraps the separate `CredUI*` prompt family)* — i.e., available to full-trust desktop **and** services, fine for FluentGpu. Size limits: `CRED_MAX_CREDENTIAL_BLOB_SIZE = 5*512 = 2560` bytes (`:455`) — comfortably fits Spotify OAuth tokens; `CRED_TYPE_GENERIC = 1` (`:442`); for generic creds the blob is "defined by the application," **no auth-package read restriction**. Persist values (`:461-463`): `SESSION=1`, `LOCAL_MACHINE=2`, `ENTERPRISE=3`.

```csharp
namespace FluentGpu.WindowsApi.Credentials;

// Thin wrapper over the Win32 Credential Manager (per-user encrypted locker, DPAPI at rest).
// Key = (target, CRED_TYPE_GENERIC). Username is metadata. Cold path; allocation is fine.
public static class CredentialStore
{
    // Company-prefixed target convention; multi-account by encoding the account:
    //   "WAVEE/Spotify/<spotifyUserId>"
    public static void Store(string target, string userName, ReadOnlySpan<byte> secret,
                             CredentialScope scope = CredentialScope.LocalMachine);  // CredWriteW
    public static bool TryRetrieve(string target, out string userName, out byte[] secret); // CredReadW (+CredFree!)
    public static bool Delete(string target);                                          // CredDeleteW
    public static IReadOnlyList<string> Enumerate(string? filter = "WAVEE/*");          // CredEnumerateW (+CredFree!)
}

public enum CredentialScope { Session = 1, LocalMachine = 2, Roaming = 3 } // → CRED_PERSIST_*
```

Correctness notes: **always `CredFree` the out-pointer** from `CredReadW`/`CredEnumerateW` in a `try/finally` (the classic leak here); store the token UTF-8-encoded (CredMan does not interpret generic blobs); `Type` must match on read/delete — `(TargetName, Type)` is the composite key. SecureString is intentionally avoided (soft-deprecated, weak on Windows, awkward under AOT) — hand the caller `byte[]`/`ReadOnlySpan<byte>` to zero after use.

**Roaming is best-effort.** `CRED_PERSIST_LOCAL_MACHINE` (recommended default for WAVEE) survives reboot, this user's other sessions **on this computer only**. `CRED_PERSIST_ENTERPRISE` roams to other computers **only if the account has a roam-able profile** — *"if the user has no roaming profile, the credential will only persist locally"* (CREDENTIALW Learn page). MSA does not sync generic creds. Don't build features assuming cross-device token availability.

**Multi-account for WAVEE:** encode the account in the target (`"WAVEE/Spotify/<userId>"`); `Enumerate("WAVEE/Spotify/*")` lists signed-in accounts; a separate `"WAVEE/ActiveAccount"` holds the active pointer.

**No host wiring** — credentials are a synchronous service call from the app's auth code, off the hot path (the read happens around `FluentApp.Run` startup; the write after the OAuth exchange). The OAuth loop itself is `ShellExecute(authorizeUrl)` → `wavee://` re-activation (§2.2) → `HttpClient` token POST → `CredentialStore.Store(refreshToken)`. PKCE is ~15 lines (`SHA256` over a random verifier); WASDK's `OAuth2Manager` adds nothing FluentGpu needs.

#### Packaged / unpackaged matrix

| Mechanism | Unpackaged | Packaged | Per-app isolated? | AOT |
|---|---|---|---|---|
| **CredMan** | ✅ | ✅ (same shared locker) | ❌ shared user locker | Green — flat P/Invoke |
| PasswordVault | ⚠ shared locker; "doesn't work as expected" | ⚠ same | ❌ (the whole problem) | Yellow/Red — WinRT ABI |
| DPAPI / DPAPI-NG | ✅ | ✅ | ✅ (your own keyed blob) | Green |
| Windows Hello | ❌/⚠ unreliable | ✅ | ✅ Hello-gated | moot (packaged-only) |

#### Effort & risks

Effort: **S** (~80–150 LoC, 5 functions + 1 struct — smaller than the existing `Win32Theme` P/Invoke block, against the same DLL; **`advapi32.lib` is already in the AOT native-link inputs**, so this adds zero new native-link surface). `CredFree`-discipline is the only non-trivial correctness point. Risks: the shared-locker exposure (any same-user process can read the generic credential — same as PasswordVault for full-trust; don't market it as sandboxed); roaming is best-effort; the 2560-byte ceiling (guard the write; DPAPI-to-file fallback for anything larger — DPAPI is the *only* secondary path worth keeping, and only for that overflow case or a future non-Windows seam).

---

## 3. Appendix — app-side MSIX recipe (for WAVEE app authors)

MSIX packaging is app-side, not class-library code. NativeAOT and MSIX are **orthogonal and sequential**: AOT runs at `dotnet publish`; MSIX wraps the output folder. The ordering is **publish-then-package**.

```bash
dotnet publish src/FluentGpu.WindowsApp -c Release -r win-x64
   # → publish/  : a single self-contained native exe (coreclr statically linked) + Assets/
   #   the ONLY sidecars are OS DLLs it P/Invokes (never copied) + a separate .pdb (exclude; ship to symbol server)
makeappx.exe pack /o /h SHA256 /d <publish-dir> /p WaveeMusic.msix
signtool.exe sign /fd SHA256 /f cert.pfx /p <pwd> WaveeMusic.msix
```

*(Verification correction: NativeAOT statically links the runtime into the `.exe` — there are NO companion .NET runtime DLLs to package, contrary to the "small set of native runtime DLLs" framing.)* This mirrors WASDK's own `MakeMsix.targets:50-52` (`makeappx pack /o /h SHA256 /d … /p …`) + `:78-84` (`signtool sign /fd SHA256 …`). Prefer this **explicit `makeappx`** path over a `.wapproj` (VS-centric, fights self-contained native publish) or single-project-MSIX (`EnableMsixTooling`) — though the latter is acceptable if the author already lives in VS tooling.

App `.csproj` publish posture (opt-in AOT — note `src/Directory.Build.props` does NOT carry these; they engage per-app at publish):

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <PublishAot>true</PublishAot>
  <TrimMode>full</TrimMode>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

`Package.appxmanifest` — folds in the cross-pillar extensions:

```xml
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="com.wavee.WaveeMusic" Publisher="CN=WaveeDev" Version="1.0.0.0" ProcessorArchitecture="x64" />
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
    <!-- NO PackageDependency on Microsoft.WindowsAppRuntime.* — hand-rolled interop -->
  </Dependencies>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <Capability Name="internetClient" />   <!-- enables https:// toast album-art when packaged -->
  </Capabilities>
  <Applications>
    <Application Id="App" Executable="WaveeMusic.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="WAVEE" .../>
      <Extensions>
        <!-- §2.2 protocol activation -->
        <uap:Extension Category="windows.protocol"><uap:Protocol Name="wavee" /></uap:Extension>
        <!-- §2.1 toast activator CLSID + COM server (SAME CLSID in both, and in CoRegisterClassObject) -->
        <desktop:Extension Category="windows.toastNotificationActivation">
          <desktop:ToastNotificationActivation ToastActivatorCLSID="{WAVEE-CLSID}" />
        </desktop:Extension>
        <com:Extension Category="windows.comServer">
          <com:ComServer>
            <com:ExeServer Executable="WaveeMusic.exe" Arguments="----WaveeToastActivated:">
              <com:Class Id="{WAVEE-CLSID}" />
            </com:ExeServer>
          </com:ComServer>
        </com:Extension>
        <!-- optional startup -->
        <uap:Extension Category="windows.startupTask">
          <uap:StartupTask TaskId="WaveeStartup" Enabled="false" DisplayName="WAVEE" />
        </uap:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>
```

Load-bearing details: `EntryPoint="Windows.FullTrustApplication"` is the desktop-bridge entry for a Win32/AOT exe (not WinUI). The toast `ToastActivatorCLSID`, the `com:ExeServer` CLSID, and the value passed to `CoRegisterClassObject` **must be identical** — define it once as a `static readonly Guid` and paste the same value into the manifest. Packaged protocol/startup registration comes from the **manifest**, not runtime registry writes (which throw for packaged). Use a FluentGpu-specific arg sentinel (`----WaveeToastActivated:`) to avoid collision with WASDK if both coexist during testing.

**Sparse identity (default-recommended distribution for WAVEE)** — an unpackaged AOT exe + identity-only package (Microsoft Learn, [grant-identity-to-nonpackaged-apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)):

1. Identity `AppxManifest.xml` with `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>`, `<Identity ProcessorArchitecture="neutral">`, capabilities `runFullTrust`+`unvirtualizedResources`, `<Application uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App">`, `<uap:VisualElements AppListEntry="none">`.
2. `MakeAppx.exe pack /o /d <dir> /nv /p Identity.msix` (**`/nv` required** — files live externally) → `SignTool sign /fd SHA256 /a /f cert.pfx /p <pwd>`.
3. Embed in the classic Win32 `application.manifest` of the exe: `<msix xmlns="urn:schemas-microsoft-com:msix.v1" publisher="CN=WaveeDev" packageName="WaveeMusic" applicationId="App" />` — values must match the identity package (mismatch ⇒ `0x80073D54`).
4. Installer registers: `Add-AppxPackage -Path Identity.msix -ExternalLocation <installDir>` (per-user) — and `ExternalLocation` **must exactly equal the real run directory** or identity silently fails to attach.

**Signing is the recurring tax.** Self-signed PFX requires importing the public `.cer` into `Cert:\CurrentUser\TrustedPeople` (else `0x800B0109`). Production: a CA-trusted cert or **Azure Trusted Signing** (now "Azure Artifact Signing"; no hardware token). **The Store re-signs for free** (*"Code signing is handled free by the Store"*) — but Store submission of an MSI/EXE wrapper does not push updates.

---

## 4. Implementation order (what to build first for WAVEE)

1. **`Packaging/PackageIdentity`** (effort S, risk near-zero). It is the integration seam the other three pillars branch on (`IsPackaged`, `ApplicationUserModelId`). Build it first; retire `WindowsApiInfo.IsScaffold` consumption by having Notifications/Activation branch on `PackageIdentity.IsPackaged`. No host wiring.

2. **`Credentials/CredentialStore`** (effort S, risk low). Pure `advapi32`, already in the link inputs, no host wiring, unblocks the WAVEE OAuth token-persistence loop end-to-end (combined with §2.2's protocol re-activation). High value, lowest cost.

3. **`Activation/`** (effort M, host wiring small). Protocol registration + single-instance gate + the `IPlatformApp.ActivationRedirected` seam + the `WM_COPYDATA` case in `Handle32`. This delivers `wavee://` OAuth callbacks and "second launch focuses the running window," and it establishes the activation-seam shape the toast path then reuses. Build the **unpackaged path only** first (it is fully command-line/registry-driven and needs no WinRT).

4. **`Notifications/`** (effort M + one L sub-task). Last, because it carries the only genuinely fiddly interop (the hand-bound WinRT XmlDocument/ToastNotifier call-out) and the cross-thread `Activate` marshaling. **Spike the single `IToastNotifier.Show` round-trip first** (§5 item 1) before building the rest — that spike decides whether carve-out (a) (CsWinRT-for-notifications-only) is triggered. Build the `ToastBuilder` + AUMID/registry pieces in parallel (they are portable/Win32 and risk-free); gate the web-image localizer on `!PackageIdentity.IsPackaged`.

Cloud push (WNS) is explicitly **not** in this order — it is the only feature that would reopen the WASDK decision, is packaged-only, and is orthogonal to all of the above.

---

## 5. Open questions (genuinely unresolved — verify before committing the relevant pillar)

These are the items that could not be settled by static source reading and need a live test on the target OS build, or a re-check at decision time:

1. **(Notifications, load-bearing) Can a self-contained NativeAOT exe expose a WinRT `IInspectable`-derived interface well enough to call `IToastNotifier.Show()` without CsWinRT's runtime?** The hand-vtable `calli` call-out path (the repo's reserved `Placeholder.cs` mechanism) is the plan, and `INotificationActivationCallback` (the one interface we *implement*) goes through `[GeneratedComInterface]`. But the WinRT call-out pattern is demonstrated nowhere in the repo. **Spike one `Show` round-trip first.** If painful → carve-out (a): CsWinRT for the notification *consumption* path only (still hand-roll the activator, since CsWinRT 3.0 lacks authoring). This is the schedule risk of the whole library.

2. **(Notifications) Cold-launch of the AOT exe by the Shell.** The in-process (already-running) activation path is well-trodden; the cold-launch leg — Shell starts a fresh AOT exe via `LocalServer32` with `----…Activated:` and the process must wire the class object fast enough to receive `Activate` — must be validated end-to-end on the real AOT binary, including that `ComWrappers` source-gen exposes the vtable to an out-of-proc caller. Pitfall to pre-empt: `CoRegisterClassObject` silently no-ops in NativeAOT+ExeServer unless hand-declared `[PreserveSig]` with `nint pUnk` ([CsWin32 #1670](https://github.com/microsoft/CsWin32/issues/1670)) — validate TerraFX's signature.

3. **(Notifications) Does omitting `PushNotifications_RegisterFullTrustApplication` degrade any local-toast edge case?** WASDK calls it unconditionally; we omit it (unlinkable UDK + legacy-path precedent). Confirm a `Show()` + click round-trips with *only* the registry + `CoRegisterClassObject` path and no full-trust/LRP registration.

4. **(Activation, load-bearing for a packaged build) Does a packaged `Windows.FullTrustApplication` protocol launch deliver the URI on the command line?** Evidence says **no** (WASDK prefers the platform `GetActivatedEventArgs` when packaged; Learn documents plain command line only for `Launch`). If WAVEE ships MSIX, the packaged build must call the platform WinRT `GetActivatedEventArgs` via the cold ABI — confirm against the running OS build, or ship unpackaged-only.

5. **(Activation) Does `MsgWaitForMultipleObjectsEx`-parked `WaitForWork(-1)` promptly wake and dispatch a cross-process `WM_COPYDATA`?** The API contract guarantees it (`QS_ALLINPUT ⊇ QS_SENDMESSAGE`); wall-clock promptness and the no-deadlock-behind-a-long-`Paint` behavior of a synchronous send should be confirmed. Mitigation already specified: sender uses `SendMessageTimeoutW(SMTO_ABORTIFHUNG, …)`.

6. **(Packaging, load-bearing for WNS) Does sparse external-location identity attach on a bare `CreateProcess`/Explorer launch?** Identity attaches only if the embedded `<msix>` manifest matches the identity package AND `ExternalLocation` exactly equals the run directory; otherwise it silently fails (`Package.Current` null) or `0x80073D54`. The Explorer/shell-launch path is a documented problem surface. Because WAVEE's WNS "Now Playing" path depends on this, smoke-test with `aka.ms/sparsepkgsample` on the target build before relying on it. Also untested-as-a-unit: self-contained AOT + sparse external-location together (each is documented separately; no mechanistic conflict, but verify once).

7. **(Credentials) `CRED_TYPE_GENERIC` ↔ `PasswordVault` interchange + ENTERPRISE roaming on a real profile.** Neither affects the recommendation (CredMan). Confirm byte-for-byte round-trip only if some component is forced onto PasswordVault; confirm ENTERPRISE actually roams only if WAVEE ever advertises cross-device sign-in via that flag (it likely will not roam on a non-domain consumer profile).

8. **(Strategy) Re-confirm CsWinRT 3.0 GA status at implementation time.** A 3.0 GA between now and coding would soften the CsWinRT-fallback objection. As of June 2026: 3.0 is preview (`3.0.0-preview.260319.2`); 2.2.0 is the last stable.

---

### Key file paths (for the implementer)

- `src/FluentGpu.WindowsApi/FluentGpu.WindowsApi.csproj` — `IsAotCompatible=true`, Engine-only, zero NuGet; add `TerraFX.Interop.Windows 10.0.26100.6` here for the COM-bearing pieces; **never** reference `FluentGpu.Windows`.
- `src/FluentGpu.WindowsApi/WindowsApiInfo.cs` — scaffold marker (`IsScaffold=true`); its doc-comment names `Credentials/` as "PasswordVault / Windows Hello" — **aspirational; the actual recommendation is CredMan, not PasswordVault**. Retire as real types ship.
- `src/FluentGpu.WindowsApi/{Notifications,Activation,Packaging,Credentials}/` — target folders (only `.gitkeep` today).
- `src/FluentGpu.Windows/Pal/Win32Theme.cs:20-33` — the house P/Invoke template to copy (`[LibraryImport("advapi32.dll", EntryPoint=…, StringMarshalling=StringMarshalling.Utf16)] private static partial` on a `static partial class`).
- `src/FluentGpu.Windows/Interop/Placeholder.cs` — reserved hand-vtable COM home (`FluentGpu.Win32.Interop`); the WinRT call-out vtables and the `[GeneratedComInterface]` activator can mirror this pattern but live in WindowsApi.
- `src/FluentGpu.Windows/FluentGpu.Windows.csproj` — the sibling's posture: `TerraFX.Interop.Windows 10.0.26100.6`, the only NuGet-bearing FluentGpu lib.
- `src/FluentGpu.Engine/Seams/Pal/Pal.cs:173` — `IPlatformApp`; `OpenUri(string)` at `:185` (the seam to mirror); add `event Action<string>? ActivationRedirected` and `event Action<ToastActivatedArgs>? ToastActivated` as default-interface-method events.
- `src/FluentGpu.Engine/Hosting/AppHost.cs:400` — `_inputHooks.OpenUri = app.OpenUri` (subscribe activation here); `:439-442` `SetAmbient` (route the URI); `:465-468` `WakeFrame` (non-thread-safe — UI-thread producers only).
- `src/FluentGpu.Windows/Pal/Win32Platform.cs:233` — window class `"FluentGpuWindow"` (single-instance `FindWindowW` target); `Handle32` WndProc switch at `:573` (add `WM_COPYDATA` case — none today); `WaitForWork` at `:546-550`; `EnqueueExternal` POD-only seam at `:364`. No `SetCurrentProcessExplicitAppUserModelID` anywhere (gap).
- `src/FluentGpu.WindowsApp/FluentApp.cs:34` — `new Win32App()` (namespace `FluentGpu.Pal.Windows`): earliest injection point for AUMID + protocol registration + the single-instance gate, before the pump (`:84` idle `WaitForWork(host.RecommendedWaitMs())`).
- `src/Directory.Build.props` — baseline `net10.0`/`LangVersion 14`; **no `PublishAot`/`TrimMode`/`RID`** (those engage per-app at publish; the app template in §3 adds them).

### WASDK source citations (`C:/WAVEE/WindowsAppSDK`)

- Notifications: `dev/AppNotifications/AppNotificationManager.cpp:107,133-141,190-208,218,228-244,346-422,424-454`; `.h:6,72-93`; `dev/AppNotifications/AppNotificationUtility.cpp:43-96,114-136,197-226,246-270,298-328`; `.h:15-18,22-32`; `dev/AppNotifications/AppNotificationBuilder/AppNotificationBuilder.cpp:106,144,166,285-310,379-391`; `AppNotificationBuilderUtility.h:11,24-34,99-167`; `specs/AppNotifications/AppNotifications-spec.md:96-101,144-149,226`.
- Activation: `dev/AppLifecycle/ActivationRegistrationManager.cpp:45,76,89,173`; `.h:10-12,15`; `dev/AppLifecycle/Association.cpp:8-11,32-33,301-307,252-260,436-439`; `dev/AppLifecycle/AppInstance.cpp:186,205,256,259,268,338-351,471-556,586,597,601`; `.h:78`; `SharedProcessList.h:9-10`; `RedirectionRequestQueue.h:10-19`; `RedirectionRequest.cpp:19-57,97-98`; `SharedMemory.h:98`; `dev/AppLifecycle/AppLifecycle.idl` (kinds); `specs/AppLifecycle/Activation/AppLifecycle Activation.md` (per-user-only).
- Packaging: `dev/Common/AppModel.Identity.IsPackagedProcess.h:11-27`; `dev/Common/AppModel.Identity.h:18-42,165-241`; `dev/Common/AppModel.PackageGraph.h:21-30`; `dev/PackageManager/API/M.W.M.D.AddPackageOptions.cpp:55-86`; `MakeMsix.targets:50-84`.
- Credentials: grep of `dev/` (zero credential surface); `dev/OAuth/OAuth.idl:28-67,125-138` (OAuth state machine, not storage).

### Primary doc sources

- [Windows notifications overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/) · [Send a local toast from other unpackaged apps](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/send-local-toast-other-apps) (unpackaged http-image restriction; registry-based AUMID, no shortcut) · [image (toast schema)](https://learn.microsoft.com/en-us/uwp/schemas/tiles/toastschema/element-image)
- [Rich activation with the app lifecycle API](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-rich-activation) · [AppLifecycle Activation spec](https://github.com/microsoft/WindowsAppSDK/blob/main/specs/AppLifecycle/Activation/AppLifecycle%20Activation.md) · [MsgWaitForMultipleObjectsEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-msgwaitformultipleobjectsex) · [GetQueueStatus](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getqueuestatus)
- [Grant package identity by packaging with external location](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) · [Choose a packaging model](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/packaging/) · [Deploy self-contained apps](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) (Singleton-package dependency; cannot single-file) · [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [CREDENTIALW (wincred.h)](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw) · [PasswordVault class](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.passwordvault) · [WASDK Discussion #1840 (PasswordVault for full-trust)](https://github.com/microsoft/WindowsAppSDK/discussions/1840)
- [ComWrappers source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation) + [dotnet/runtime #110264](https://github.com/dotnet/runtime/discussions/110264) (`[GeneratedComInterface]`/`IClassFactory` is the AOT COM-server path; `ComImport` does not work in AOT) · [CsWin32 #1670](https://github.com/microsoft/CsWin32/issues/1670) (`CoRegisterClassObject` AOT pitfall)
- [CsWinRT releases](https://github.com/microsoft/CsWinRT/releases) + [aot-trimming](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md) + [dotnet/runtime #114179](https://github.com/dotnet/runtime/issues/114179) (CsWinRT 2.1.1 AOT-compatible; 3.0 preview, .NET 10 AOT-first) · [WASDK 1.6 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6) (PublishAot support)