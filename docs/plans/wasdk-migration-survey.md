# Windows App SDK → FluentGpu.WindowsApi — migration survey & prioritized plan

**Status:** research / planning. The WindowsApi-pillar candidates (§3.1–§3.8) are planning-only (no code); the localization engine (§3.9) is **shipped** (implemented + gate-verified). **Date:** 2026-06.
**Scope:** what the FluentGpu OS-services library (`src/FluentGpu.WindowsApi/`) should adopt *next*, mined from the Windows App SDK source corpus at `C:/WAVEE/WindowsAppSDK`.
**Posture gate (binding):** NO `Microsoft.WindowsAppSDK` NuGet, NO CsWinRT runtime, NO `ComWrappers`-subclassing, NO reflection. Only the three proven house interop modes — `[LibraryImport]` flat Win32; `[GeneratedComInterface]`/cold COM; and the hand-rolled WinRT ABI (`RoGetActivationFactory`/`RoActivateInstance` + `WindowsCreateString`/HSTRING + TerraFX vtable structs, the pattern in `Notifications/ToastInterop.cs`). NativeAOT + `TrimMode full` must survive. The pillar pins **`TerraFX.Interop.Windows` 10.0.26100.6** (`FluentGpu.WindowsApi.csproj:16`).

> **The structural fact that governs every candidate.** WASDK's own implementation almost never reaches the OS directly — it delegates to an *unlinkable* `frameworkudk\*Pal.h` PAL (e.g. `PowerNotifications.h:9` includes `frameworkudk\PowerNotificationsPal.h`; Badge routes through `ToastNotifications_PostToast`). FluentGpu **cannot crib WASDK's call.** But the **public OS primitive underneath** every UDK is reachable flat-Win32 / in-box-WinRT and is what FluentGpu hand-rolls — exactly as the existing toast pillar targets in-box `ToastNotificationManager` instead of the toast UDK. So for each candidate the question is always: *what is the public OS primitive, and is it hand-rollable AOT-clean without the WASDK product?*

---

## 1. Executive summary

FluentGpu.WindowsApi is now **10 pillars** (9 at survey time + **Storage**, shipped per the status update below), all hand-rolled AOT-clean: Notifications, Credentials, Packaging, Activation, Media (SMTC), Dialogs, Shell, Power, Network, Storage. The survey walked every non-plumbing WASDK component (`dev/` impls + `specs/` IDL) and cross-checked each against the shipping pillar code.

**The honest finding:** there is **no marquee, requirement-blocking gap** that a single WASDK port closes. The genuine gaps are a handful of *additive* extensions to existing pillars (plus one new small pillar and one engine-window-seam enhancement), each backed by a flat-Win32 or in-box-WinRT primitive, each cheap, none requiring the WASDK runtime. Several strong-prior candidates were **downgraded or moved to SKIP** after adversarial verification exposed value claims built on misread WAVEE design lines.

**The one thing that shipped this cycle: localization.** The original survey deferred MRTCore to the SKIP list ("big lift, low value"). On re-examination the right move was not *adopt MRT later* but *build a smaller, better thing now*: FluentGpu now ships a from-scratch, AOT-clean **JSON/ICU localization engine** under `src/FluentGpu.Engine/Localization/` — per-culture JSON (named placeholders, an ICU plural/select subset RESW/MRT lack, pseudo-localization, live no-re-render culture switching) parsed with `Utf8JsonReader` (no reflection, no `ResourceManager`/`.resw`/`resources.pri`). It is **implemented, compiling, and gate-verified** (`--loc-probe` 46/46 [PASS], exit 0) — now including a build-time typed-key **source generator** (the **first analyzer wired into the repo**) that emits compile-safe `Strings.*` key constants + typed format methods from the base-culture JSON, AOT-clean. It is an *engine* feature behind a host seam — **not** a `FluentGpu.WindowsApi` pillar — so the WindowsApi pillar count stays at **9**; its only OS touch is one flat `GetUserDefaultLocaleName` read wired through that seam. Full write-up in **§3.9**; the MRTCore SKIP row is now **SUPERSEDED → §3.9**.

### ✅ Status update (2026-06-13) — the recommended migrations shipped

The recommended next-migrations below are **implemented and verified**; the pillar count is now **10** (Storage added):

1. **PowerManager snapshot (#1)** — DONE. `Power/PowerSession.cs` adds `PowerStatus`/`PowerSource` + `ReadPower()` decoding `GetSystemPowerStatus` (AC/DC, battery %, charging, energy-saver, est. discharge). Snapshot half only; the GUID change-event half remains the deferred follow-up. Gallery: **Power** card "Read power status".
2. **ApplicationData → `Storage/` pillar (#2)** — DONE. New `Storage/AppDataStore.cs` (typed Get/Set over `HKCU\SOFTWARE\<pub>\<prod>`: String→REG_SZ, Bool/Int→REG_DWORD, Long/Double→REG_QWORD, Bytes→REG_BINARY; Keys/Contains/Remove/Clear; Local/Cache/Temp folders) + `Storage/SettingsStore.cs` (reactive write-through `Signal<T>`). Zero new interop. Gallery: new **App data** card.
3. **SMTC Timeline write (#3)** — DONE. `Media/SystemMediaControls.cs` adds `UpdateTimeline`/`UpdatePosition`/`PlaybackRate` + a QI-cached `ISystemMediaTransportControls2` (`EnsureV2`), passing `System.TimeSpan` straight through (TerraFX projects `Windows.Foundation.TimeSpan` onto the BCL type). Gallery: **Media** card "Push timeline" / "+30s". Seek read-back (#5) still a fast-follow.
4. **Toast lifecycle (#4)** — partial + **redesigned**. The `<progress>` bar (data-bound), tag/group on `Show`, and `RemoveByTag`/`RemoveGroup`/`ClearHistory` shipped; the in-place `IToastNotifier2.UpdateWithTagAndGroup`/`INotificationData` write remains a documented fast-follow (TerraFX generic `IMap<HSTRING,HSTRING>` binding quirk). Separately, `ToastBuilder` was **redesigned into a concise fluent API** (`Toast.Create().Title().Body().Button(cfg).Tag().ShowVia(notifier)` — no XML required; `BuildXml()` kept as the escape hatch) with a configurable `ToastButton` (activation type, icon, Success/Critical style, Dismiss, context-menu), `Selection`/`Header` inputs, and `ToastNotifier.Show(ToastBuilder)`.

**Plus cross-cutting engine features (not WindowsApi pillars):** **OS file/folder drop with live hover + drag/drop styling**. The Windows backend's `Win32DropTarget` is a HAND-ROLLED-vtable OLE `IDropTarget` CCW (the `DWriteItemizer.cs` pattern — by-value `POINTL` honored on x64+ARM64; a source-gen `[GeneratedComInterface]` attempt crashed on that ABI, so it was redone hand-vtable) registered via `RegisterDragDrop`. Hover is data-free (`QueryGetData(CF_HDROP)` only) → `InputHooks.ExternalDrag{Enter,Over,Leave}` → `DragDropContext`, so a `BoxEl.DropTarget` accepting `DropKinds.Files` lights up DURING hover and the returned `DropEffect` drives the OS "+Copy" cursor; the file list is read once at `Drop` (`ExternalDropFiles`). On top: `DragSource.Style` (`DragVisualStyle` ghost opacity/shadow/scale), `UseDragState()` + `FluentGpu.Controls.DragPreviewLayer` (cursor-following custom preview), `FluentGpu.Controls.DropZone` (self-restyling dashed-accent-ring + glow drop affordance, no double text), and a `BoxEl.BorderDashOn/Off` dashed-stroke channel. Gates: `e5dragdrop.ext` + `e5dragdrop.style` (headless), smoke `6c.1` (CCW POINTL-ABI round-trip). Gallery: **File & folder drop — styled DropZone** card.

### Original recommended next 2–3 migrations (in order) — now done, see above

1. **PowerManager snapshot state** — `GetSystemPowerStatus` poll (AC/DC, battery %, energy-saver flag, discharge seconds) added to the existing **Power** pillar. Smallest genuine gap, zero engine coupling for the snapshot half. **Effort S** (the snapshot); the GUID change-event half is a deferred follow-up (M, needs an HWND).
2. **ApplicationData unpackaged settings + paths** — a new small **`Storage/`** pillar: a flat `HKCU\SOFTWARE\<publisher>\<product>` registry key/value container + `%LOCALAPPDATA%`/`%TEMP%` folder paths. **Zero new interop** (reuses the registry ABI already in `ProtocolRegistrar.cs`/`AumidRegistration.cs`). Every desktop app needs durable settings; FluentGpu has none. **Effort S.**
3. **SMTC Timeline (write path)** — `ISystemMediaTransportControls2.UpdateTimelineProperties` so the Windows 11 media flyout / lock screen shows a working **scrub bar**. Extends the **Media** pillar; WAVEE already has the 1 Hz clock to drive it. **Effort S** for the write path (the OS-driven seek read-back is a fast follow, M, due to a derived parameterized IID).

Honorable mentions that are still *migrate-now* but lower-value: **Toast Update/progress/RemoveByTag** (cheapest, but value narrowed to download-progress + stale-toast cleanup once SMTC is acknowledged as the canonical now-playing surface). The **AppWindow CompactOverlay/FullScreen presenter** is **migrate-later** — genuinely hand-rollable and a nice media affordance, but its "marquee mini-player" justification is contradicted by the WAVEE design (PiP is an in-window DComp visual, not an OS window).

---

## 2. Ranked table

| # | Capability | Target pillar / seam | OS primitive | Hand-rollable? | Effort | WAVEE value | Recommendation |
|---|------------|----------------------|--------------|:--------------:|:------:|-------------|----------------|
| 1 | PowerManager snapshot state (AC/DC, battery %, energy-saver, discharge time) | **Power** (extend) | `GetSystemPowerStatus` (+ `RegisterPowerSettingNotification` for events) — flat Win32 | ✅ | **S** (poll) / M (events) | MEDIUM | **migrate-now** |
| 2 | ApplicationData unpackaged settings + state paths | **`Storage/`** (new) | `RegOpenCurrentUser` + `HKCU\SOFTWARE\<pub>\<prod>` Reg\*; `SHGetKnownFolderPath`/`Environment.GetFolderPath` — flat Win32 | ✅ | **S** | MEDIUM-HIGH | **migrate-now** |
| 3 | SMTC Timeline / seek-bar (write path) | **Media** (extend) | `ISystemMediaTransportControls2.UpdateTimelineProperties` + `RoActivateInstance(...TimelineProperties)` — in-box WinRT | ✅ | **S** | MEDIUM-HIGH | **migrate-now** |
| 4 | Toast Update / `<progress>` / RemoveByTag lifecycle | **Notifications** (extend) | `IToastNotifier2.UpdateWithTagAndGroup` + `INotificationData` + `IToastNotificationHistory.Remove*` — in-box WinRT | ✅ | **S** | MEDIUM | **migrate-now** |
| 5 | SMTC `PlaybackPositionChangeRequested` (OS-driven seek read-back) | **Media** (extend) | `ISystemMediaTransportControls2.add_PlaybackPositionChangeRequested` (parameterized `ITypedEventHandler`) | ✅ | M | MEDIUM | **migrate-now** (fast-follow of #3) |
| 6 | AppWindow CompactOverlay / AlwaysOnTop / FullScreen presenter | **engine window seam** (`IPlatformWindow`) | `SetWindowPos(HWND_TOPMOST)` / `WS_EX_TOPMOST`; `SetWindowLongPtrW(GWL_STYLE)` swap; `MonitorFromWindow`+`GetMonitorInfo` — flat user32 | ✅ | S-M | MEDIUM | **migrate-later** |
| 7 | ThemeSettings high-contrast probe + live theme-change | **theming subsystem** (not WindowsApi) | `SystemParametersInfo(SPI_GETHIGHCONTRAST)` + `GetSysColor`; `WM_THEMECHANGED` — flat Win32 | ✅ | S | MEDIUM | **migrate-later** |
| 8 | Packaged `GetActivatedEventArgs` delivery | **Activation** (extend) | `RoGetActivationFactory` + `IProtocolActivatedEventArgs`/`IFileActivatedEventArgs` (in-box WinRT), gated on `IsPackaged` | ✅ | M | LOW-MED | **migrate-later** (with MSIX) |
| 9 | Localization (i18n) — strings, named placeholders, ICU plural/select, pseudo-loc, live switching | **engine** (`FluentGpu.Engine/Localization/`) | hand-rolled JSON + `Utf8JsonReader`; OS-locale via `GetUserDefaultLocaleName` (flat Win32, host-seam) | ✅ | M | MEDIUM-HIGH | **SHIPPED** (§3.9 — supersedes MRTCore) |
| — | Storage.Pickers, BadgeNotifications, OAuth2Manager, AppInstance-rich, Restart, EnvironmentManager, ~~MRTCore~~ (→ §3.9), CameraCaptureUI, Licensing, AccessControl, DWriteCore, PushNotifications, BackgroundTask, WASDK runtime plumbing | — | — | — | — | — | **SKIP** (§4) |

*Effort key: S ≈ ≤ ~80 LoC, one primitive; M ≈ ~120–250 LoC, callback/IID/QI wiring; L ≈ build-pipeline lift.*

---

## 3. Migrate-now / migrate-later candidates

### 3.1 — PowerManager snapshot state (extend `Power/`) — migrate-now, effort S

**WASDK surface.** `dev/PowerNotifications/PowerNotifications.idl:82-123` — static `PowerManager` exposes `EnergySaverStatus`, `BatteryStatus`, `PowerSupplyStatus`, `RemainingChargePercent`, `RemainingDischargeTime`, `PowerSourceKind` (AC/DC), `DisplayStatus`, `EffectivePowerMode`, `UserPresenceStatus`, each with a `*Changed` event.

**OS primitive (public, hand-rollable).** WASDK reads these through the **unlinkable** `frameworkudk\PowerNotificationsPal.h` (`PowerNotifications.cpp:39,65,146,159,171,184,279`) — uncribbable. The public substitutes:
- **Snapshot:** `GetSystemPowerStatus(SYSTEM_POWER_STATUS*)` → `ACLineStatus` (AC vs DC), `BatteryFlag` (charging/low), `BatteryLifePercent` (charge %), `BatteryLifeTime` (discharge seconds), `SystemStatusFlag` (energy-saver, Win10 1709+). One trivial poll, no callback. `GetSystemPowerStatus`, `SYSTEM_POWER_STATUS`, `RegisterPowerSettingNotification`, `UnregisterPowerSettingNotification`, `POWERBROADCAST_SETTING` are all projected by the pinned TerraFX.
- **Change events (deferred half):** `RegisterPowerSettingNotification(HANDLE hRecipient, GUID*, flags)` keyed on `GUID_ACDC_POWER_SOURCE` / `GUID_BATTERY_PERCENTAGE_REMAINING` / `GUID_POWER_SAVING_STATUS` / `GUID_CONSOLE_DISPLAY_STATE`. **Correction (load-bearing):** unlike the existing suspend/resume path, `RegisterPowerSettingNotification` does **not** support `DEVICE_NOTIFY_CALLBACK` — `hRecipient` must be an HWND (`DEVICE_NOTIFY_WINDOW_HANDLE`), and changes arrive as `WM_POWERBROADCAST`/`PBT_POWERSETTINGCHANGE` carrying a `POWERBROADCAST_SETTING` into a WndProc. So this half does **not** reuse the `PowerSession.cs` `[UnmanagedCallersOnly]` thunk; it needs a `WM_POWERBROADCAST` case in the engine WndProc + a registration on a stable HWND (an open layering question — see effort). The GUID constants are not in TerraFX; restate them locally from `winnt.h`, exactly as `PowerSession.cs:58-69` already restates its `#define`s and the `DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS` struct (`PowerSession.cs:77-84`).

**Coverage.** `Power/PowerSession.cs` covers only `KeepAwake` (`SetThreadExecutionState`) and `Suspending`/`Resumed` (`RegisterSuspendResumeNotification`); it explicitly declines the WinRT battery surface as "activation-heavy" (`PowerSession.cs:44-52`). A full-tree grep for `GetSystemPowerStatus|SYSTEM_POWER_STATUS|BatteryLifePercent|ACLineStatus|EnergySaver|RegisterPowerSetting` is **empty** — genuine, total gap. The migration *adds* state; it does not duplicate suspend/resume.

**C# API sketch (house style — flat Win32, extends the existing static pillar):**

```csharp
namespace FluentGpu.WindowsApi.Power;

/// <summary>An instantaneous power-state snapshot — flat <c>GetSystemPowerStatus</c>, no callback, identity-free.</summary>
public readonly record struct PowerSnapshot(
    PowerSource Source,             // AC | DC | Unknown        (ACLineStatus)
    int BatteryPercent,             // 0..100, or -1 if unknown (BatteryLifePercent)
    bool IsCharging,                // BatteryFlag & 0x08
    bool EnergySaverOn,             // SystemStatusFlag & 0x01  (1709+)
    TimeSpan? RemainingDischarge);  // BatteryLifeTime, null if AC / unknown

public enum PowerSource : byte { Unknown = 0, Offline = 1 /*DC*/, Online = 2 /*AC*/ }

public static partial class PowerSession   // same file/namespace as KeepAwake + Suspend/Resume
{
    /// <summary>Read the current power state. Pure poll — call on any thread, allocates one struct.</summary>
    public static PowerSnapshot ReadPower();   // wraps GetSystemPowerStatus(out SYSTEM_POWER_STATUS)

    // ── DEFERRED (effort M): change events need an HWND + a WM_POWERBROADCAST WndProc case. ──
    // public static IDisposable SubscribePowerChanged(nint hwnd, Action<PowerSnapshot> onChange);
}
```

**Effort.** Snapshot poll = **S** (~40–60 LoC, one struct + one P/Invoke, zero engine coupling). Change-event subscription = **M** and must be honest about the HWND-ownership question (register on the gallery HWND via a new WndProc case, *or* stand up a pillar-owned message-only HWND). **Ship the poll first.**

**Value — MEDIUM (corrected down from HIGH; honest caveats).** Concrete-but-*inferred*: `design/app-requirements-waveemusic.md` contains **no** written battery/energy-saver requirement (grep of `design/` for `batter|energy-saver|power-source` finds only unrelated prose, plus `window-backdrop-mica.md:23,112` noting DWM already does battery-saver Mica suppression *for free* — i.e. the engine deliberately delegates that one power-awareness case to the OS). The "drop streaming bitrate / pause video on energy-saver or DC" use cases are a defensible *architect inference* from the continuous-video + always-on/casting-audio workload, not a requirement line, and **no engine hook waits to consume the signal** — the WAVEE *app code* would poll and decide. Legitimate for a laptop-class media client; it is the cheapest genuine gap; but value is "app convenience," not "engine-blocking."

---

### 3.2 — ApplicationData unpackaged settings + state paths (new `Storage/` pillar) — migrate-now, effort S

**WASDK surface.** `specs/applicationdata/ApplicationData.md:277-438` — `ApplicationData` with `LocalSettings` (typed key/value container), `LocalPath`/`LocalCachePath`/`TemporaryPath`/`RoamingPath`, and `GetForUnpackaged(publisher, product)`.

**OS primitive (public, flat — NO COM/WinRT for the unpackaged leg).** WASDK's *own* unpackaged impl reaches the OS directly:
- **Paths:** `SHGetKnownFolderPath(FOLDERID_LocalAppData | FOLDERID_ProgramData | FOLDERID_LocalAppDataLow)` + publisher/product subdir (`dev/ApplicationData/UnpackagedApplicationData.cpp:64-74`); `GetTempPath` (`:103-106`).
- **Settings:** `RegOpenCurrentUser(KEY_READ|KEY_WRITE)` → `SOFTWARE\<publisher>\<product>` (`:170-172`); the container is `RegSetValueExW`/`RegQueryValueExW`/`RegDeleteValueW`/`RegDeleteTreeW`/`RegEnumValue` (`dev/ApplicationData/UnpackagedApplicationDataContainer.cpp:64,74,…`).

Every one of those is **already imported and AOT-proven** in the tree: registry write in `Activation/ProtocolRegistrar.cs:47-64`, the two-pass size-probe registry read in `Notifications/AumidRegistration.cs:309-318,362-371`, and known-folder paths via BCL `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` already used at `Notifications/ToastImageCache.cs:56`. ⇒ **unpackaged leg = zero new interop.**

**Two corrections folded in (impl is ground truth over the spec markdown):**
1. The LocalSettings registry path is **`HKCU\SOFTWARE\<publisher>\<product>`** (the impl, `UnpackagedApplicationData.cpp:171`), **not** the `HKCU\SOFTWARE\Classes\Local Settings\Software\…` form that appears only in the stale spec table (`ApplicationData.md:179`) and is the legacy UWP state-manager location. Use the impl path.
2. For unpackaged, only the **static factories** (`GetForUnpackaged`/`GetDefault`/`GetForUser`) and the cache faces throw `hresult_not_implemented` (`UnpackagedApplicationData.cpp:25-53`); the **instance** members `LocalPath`/`TemporaryPath`/`LocalSettings` are fully implemented (`:55-81,165-175`). The conclusion stands — flat-Win32 is the universal answer — but the "WASDK gives unpackaged apps nothing but a folder path" characterization was overstated.

**Coverage.** No `Storage/` or `Settings/` pillar exists (the 9 pillars are Activation, Credentials, Dialogs, Media, Network, Notifications, Packaging, Power, Shell). No app-level settings persistence anywhere: the only on-disk writers are content caches (`DiskImageCache`, `ToastImageCache.cs`), not a key/value store. `Packaging/PackageIdentity.cs` gives `IsPackaged`/`InstalledLocation` but **no writable per-app settings store**. Genuine, complete gap.

**C# API sketch (house style — flat registry + known-folder; composes with `Credentials`):**

```csharp
namespace FluentGpu.WindowsApi.Storage;

/// <summary>Per-app durable settings + state folders. Unpackaged: flat <c>HKCU\SOFTWARE\&lt;pub&gt;\&lt;prod&gt;</c>
/// (REG_SZ/REG_DWORD/REG_QWORD/REG_MULTI_SZ) + <c>%LOCALAPPDATA%\&lt;pub&gt;\&lt;prod&gt;</c>. No COM, no WinRT,
/// identity-free; works packaged and unpackaged identically. The non-secret peer of <see cref="Credentials.CredentialStore"/>.</summary>
public sealed class AppDataStore
{
    public static AppDataStore ForUnpackaged(string publisher, string product);

    public string LocalFolder { get; }       // %LOCALAPPDATA%\<pub>\<prod>   (created on first use)
    public string CacheFolder { get; }        // ...\Cache
    public string TempFolder  { get; }        // %TEMP%\<pub>\<prod>

    public string? GetString(string key);
    public void    SetString(string key, string value);
    public uint?   GetUInt32(string key);
    public void    SetUInt32(string key, uint value);
    public ulong?  GetUInt64(string key);
    public void    SetUInt64(string key, ulong value);
    public bool    Remove(string key);
    public void    Clear();                    // RegDeleteTree of the product subkey
}
```

> WASDK's `+0x20000` custom PropertyType-tagging scheme (`UnpackagedApplicationDataContainer.cpp:19-59`) is **optional** — only needed for full WinRT `PropertyType` fidelity, not for a music app's scalar settings. Map String→REG_SZ, UInt32→REG_DWORD, UInt64→REG_QWORD, string[]→REG_MULTI_SZ and skip it.

**Effort.** **S** (~80–120 LoC, all reusing proven interop). The packaged WinRT `Windows.Storage.ApplicationData.Current` leg (cold COM via the `ToastInterop` factory pattern, gated on `PackageIdentity.IsPackaged`) is **M and deferred** — ship unpackaged-only first, matching the house "build unpackaged first" posture.

**Value — MEDIUM-HIGH.** Universal: window placement, last-played, volume, accent, equalizer, mini-player position, offline-cache index. Composes with `Credentials` (which holds the secret) to hold the non-secret state. Honest caveat: not *unique* — an app could roll its own JSON under `%LOCALAPPDATA%` (the engine already writes there). Real and broadly needed, but "every desktop app" general rather than tied to a named WAVEE requirement line — which is why it sits second-tier, not first.

---

### 3.3 — SMTC Timeline / seek-bar, write path (extend `Media/`) — migrate-now, effort S

**Not a WASDK component.** SMTC is in-box WinRT; a grep of `C:/WAVEE/WindowsAppSDK/{specs,dev}` for `UpdateTimelineProperties|TimelineProperties|PlaybackPositionChange` is **empty**. This is **self-completion of an existing pillar**, not a port.

**OS primitive (in-box WinRT; verified present in the pinned TerraFX 10.0.26100.6).**
- `ISystemMediaTransportControls2.UpdateTimelineProperties(ISystemMediaTransportControlsTimelineProperties*)`.
- `ISystemMediaTransportControlsTimelineProperties` with `put_StartTime/EndTime/Position/MinSeekTime/MaxSeekTime` (all `TimeSpan`). No statics/factory interface → realize via `RoActivateInstance("Windows.Media.SystemMediaTransportControlsTimelineProperties")` + QI.

**Correction (material) — the owning interface.** `UpdateTimelineProperties` and all `*ChangeRequested` events live on **`ISystemMediaTransportControls2`**, the **v2** interface. The pillar currently caches only `ISystemMediaTransportControls*` (`Media/SystemMediaControls.cs:94`), so the work requires a one-time `QueryInterface` from `_smtc` to `ISystemMediaTransportControls2` (cache it AddRef-owned, release in `Dispose`). Small, but real — the surveys' "on the SAME cached instance" wording is wrong.

**Coverage.** `Media/SystemMediaControls.cs` has only `SetPlaybackStatus` (:154), `SetEnabledButtons` (:170), `UpdateDisplay` (:194), `ClearDisplay` (:239), `ButtonPressed` (:264). Grep of `Media/` for `Timeline|Position|PlaybackRate|MinSeek|MaxSeek` = empty; no `ISystemMediaTransportControls2` reference anywhere. Genuine gap. Without `UpdateTimelineProperties` the OS media flyout shows **no scrub bar** — a visible parity gap.

**C# API sketch (house style — extends the existing pillar; v2 QI + `RoActivateInstance`):**

```csharp
public sealed partial class SystemMediaControls
{
    /// <summary>Publish the seekable timeline so the Win11 media flyout / lock screen show a scrub bar.
    /// QIs <c>ISystemMediaTransportControls2</c> (cached) and pushes a realized
    /// <c>Windows.Media.SystemMediaTransportControlsTimelineProperties</c>. Min/MaxSeek default to Start/End.</summary>
    public void SetTimeline(TimeSpan position, TimeSpan duration,
                            TimeSpan? minSeek = null, TimeSpan? maxSeek = null);

    public void ClearTimeline();   // push a zeroed TimelineProperties

    // ── fast-follow (§3.5): OS-driven seek read-back. ──
    // public event Action<TimeSpan>? PlaybackPositionChangeRequested;
}
```

**Effort.** **S** for the write path (one QI + `RoActivateInstance` + five `put_*` + the v2 call — no event handler, no IID derivation). WAVEE already has the data source: a 1 Hz interpolated progress clock on the PlayerBar (`design/app-requirements-waveemusic.md:34`, grounded in `playback.md` per `:359`).

**Value — MEDIUM-HIGH.** The scrubber on the Windows 11 media flyout / lock screen is table-stakes media-app integration and a direct Spotify-parity gap; it lights up purely from the write path.

---

### 3.4 — Toast Update / `<progress>` / RemoveByTag lifecycle (extend `Notifications/`) — migrate-now, effort S

**WASDK surface.** `specs/AppNotifications/AppNotifications-spec.md:292-340` — `AppNotificationProgressData` (Title/Value/ValueStringOverride/Status + monotonic Sequence) bound to a `<progress>` element for an updatable toast; `:275-286` `GetAllAsync` + `RemoveByTagAndGroupAsync`. (The spec's own worked example is a music download — `:309-336`, "Weekly playlist … 15/26 songs … Downloading…", Tag=`weekly-playlist`, Group=`downloads`.)

**OS primitive (in-box WinRT; verified present in pinned TerraFX 10.0.26100.6).** Do **not** mirror the WASDK `AppNotificationManager` runtimeclass (it routes through the unreachable `ToastNotifications_PostToast` UDK). Target the in-box surface FluentGpu already uses for `Show`:
- `IToastNotifier2.UpdateWithTagAndGroup(NotificationData, tag, group)` — the in-place update.
- `INotificationData` / `INotificationDataFactory.CreateNotificationDataWithValues`, with `put_SequenceNumber` (the monotonic counter that makes out-of-order updates safe).
- `IToastNotificationManagerStatics2.get_History` → `IToastNotificationHistory.RemoveGroup` / `RemoveGroupedTagWithId`.
- a `<progress value='{x}' status='…'/>` XML binding with `data-*` placeholders in `ToastBuilder`.

(`IToastNotificationManagerStatics3` is **absent** from the pinned TerraFX and correctly irrelevant — the in-box `IToastNotifier2`/history path is what's used.) Reachable through the exact machinery already shipping: `RoGetActivationFactory` + `__uuidof<T>` + `HStringHandle` (`Notifications/ToastInterop.cs`) + TerraFX vtable structs.

**Coverage.** `ToastNotifier.Show` takes `tag`/`group` but **discards them** today: `Notifications/ToastNotifier.cs:198-199` (`_ = tag; _ = group;` with the comment *"OPEN-QUESTION: tag/group set-once needs IToastNotification2"*). `ToastBuilder` emits no `<progress>` (`BuildXml`). Grep of `Notifications/*.cs` for `Progress|Update|RemoveByTag|NotificationData|History` = only those two doc lines. So Update/Progress/Remove are **missing**; the hard parts (activator + factory + HSTRING RAII) already ship — lowest-friction win.

**C# API sketch (house style):**

```csharp
public sealed partial class ToastBuilder
{
    /// <summary>Add an updatable <c>&lt;progress&gt;</c> bound to <c>data-*</c> placeholders that
    /// <see cref="ToastNotifier.Update"/> later fills.</summary>
    public ToastBuilder AddProgress(string titleKey = "title", string valueKey = "progressValue",
                                    string statusKey = "progressStatus");
}

public sealed partial class ToastNotifier
{
    /// <summary>Replace the data of a live toast in place (IToastNotifier2.UpdateWithTagAndGroup).
    /// <paramref name="sequence"/> must increase across updates so stale updates are dropped.</summary>
    public ToastUpdateResult Update(string tag, string? group, uint sequence,
                                    IReadOnlyDictionary<string, string> values);

    /// <summary>Remove a posted toast by tag (+group) from the Action Center history.</summary>
    public void Remove(string tag, string? group = null);
}
```

**Effort.** **S** (~120 LoC). **Value — MEDIUM (corrected down from HIGH).** Two value links are weaker than the surveys claimed: (a) "in-place now-playing toast update" largely **duplicates SMTC**, which already owns the canonical now-playing surface (`Media/SystemMediaControls.cs:194` `UpdateDisplay`) — a per-track toast would be Action-Center spam; (b) the download-progress flow is real-for-music-apps but **not a named WAVEE feature** (the WAVEE design names in-app chrome/lists/lyrics/video, not toast-progress). The durable value that survives: **RemoveByTag/Group history cleanup** (clear stale toasts on logout/track-change) and **determinate background-job progress** (library import/sync) if/when those ship.

---

### 3.5 — SMTC `PlaybackPositionChangeRequested` (OS-driven seek read-back) — migrate-now (fast-follow of §3.3), effort M

**OS primitive.** `ISystemMediaTransportControls2.add_PlaybackPositionChangeRequested` (+ `put_PlaybackRate(double)`); the handler is `ITypedEventHandler<ISystemMediaTransportControls, IPlaybackPositionChangeRequestedEventArgs>`, and `IPlaybackPositionChangeRequestedEventArgs.get_RequestedPlaybackPosition(TimeSpan*)` is a trivial read. All present in the pinned TerraFX.

**Why this is split out from §3.3 (the effort driver).** Structurally identical to the working `add_ButtonPressed` (`Media/SystemMediaControls.cs:339`), **but** the handler's parameterized `ITypedEventHandler<,>` IID is a *different* type and must be **newly derived** (SHA-1 / RFC-4122-v5), not copied from a header. `Media/MediaButtonHandler.cs:34-39` flags the existing derived IID as "**the single most failure-prone value in the Media pillar**." So this needs its own derived IID + a second `[GeneratedComInterface]`/`[GeneratedComClass]` handler pair — genuine, error-prone work, hence **M** not S. Land it as a fast follow once the write path (§3.3) ships and the scrubber is visible.

**Value — MEDIUM.** Lets the user seek WAVEE *from* Windows (the flyout/lock-screen scrubber becomes interactive, not just informational).

---

### 3.6 — AppWindow CompactOverlay / AlwaysOnTop / FullScreen presenter (engine window seam) — migrate-later, effort S-M

**Not a WASDK port.** `C:/WAVEE/WindowsAppSDK/dev` has **no** AppWindow/presenter source (only `specs/Windowing/DisplayArea.md`, a DisplayId helper). The presenters are WinUI in-box `Microsoft.UI.Windowing` over plain HWND ops — so this is correctly an **engine-window-seam enhancement over flat user32**, landing on `IPlatformWindow` (`src/FluentGpu.Engine/Seams/Pal/Pal.cs:263`, alongside `Minimize`/`ToggleMaximize`/`CloseWindow` at `:323-325`) + `Win32Platform.cs`, **not** a WindowsApi pillar.

**OS primitive (flat user32).** AlwaysOnTop = `SetWindowPos(hwnd, HWND_TOPMOST, SWP_NOMOVE|NOSIZE)` + `WS_EX_TOPMOST`; CompactOverlay = `SetWindowLongPtrW(GWL_STYLE/EX_STYLE)` thin-frame + constrained `SetWindowPos`; FullScreen = save `WINDOWPLACEMENT`, strip `WS_OVERLAPPEDWINDOW`, `MonitorFromWindow`+`GetMonitorInfo`, `SetWindowPos` to `rcMonitor`, restore on exit.

**Gap is real (corrected line anchors).** `IPlatformWindow` exposes no presenter/topmost/fullscreen method; grep of `src/FluentGpu.Windows` for `TOPMOST|FullScreen|CompactOverlay|Presenter|WINDOWPLACEMENT|AlwaysOnTop` = **zero**, and every existing `SetWindowPos` passes `SWP_NOZORDER` (sizing/DPI only — deliberately never z-order). The primitives are *adjacent*, not present: there is no `MonitorFromWindow` (the engine has `MonitorFromPoint`), no `WINDOWPLACEMENT` import, and `SetWindowLongPtrW` is used only for `GWLP_USERDATA` in `WM_NCCREATE`, not a style swap. **Template:** `Win32PopupWindow.cs` already does `WS_POPUP` + `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` local consts + `SetWindowPos`/`ShowWindow` z-order — a near-direct template, which is why effort is **S-M**, not M.

**DWM caveat (corrected — narrower than surveys claimed).** `DWMWA_USE_IMMERSIVE_DARK_MODE` and `DWMWA_SYSTEMBACKDROP_TYPE` set by `Win32Theme.ApplyWindowMaterial` (`Win32Theme.cs:75-99`, applied once at startup) are *sticky* attributes that **survive** a `SetWindowLongPtr` style swap — they do **not** need re-applying. What genuinely needs care across a fullscreen round-trip is the custom-frame `WM_NCCALCSIZE` geometry + glass-margins path — re-assert that, not the Mica/dark attrs.

**C# API sketch (engine seam — default no-ops so headless/standard backends ignore it):**

```csharp
public interface IPlatformWindow : IDisposable
{
    // … existing Minimize/ToggleMaximize/CloseWindow …
    void SetAlwaysOnTop(bool on) { }                 // SetWindowPos(HWND_TOPMOST|HWND_NOTOPMOST)
    void EnterCompactOverlay(Size2 sizePx) { }       // thin-frame WS_POPUP + topmost + clamp size
    void ExitCompactOverlay() { }                    // restore saved style + placement
    void SetFullScreen(bool on) { }                  // save/strip WS_OVERLAPPEDWINDOW; cover monitor; restore
}
```

**Effort S-M. Value — MEDIUM (corrected down from VERY HIGH).** The "marquee MiniVideoPlayer PiP / mini-player depends on this presenter" justification is **contradicted by the WAVEE design**: `app-requirements-waveemusic.md:33` defines theatre/sidebar/**PiP** as video modes *"composited under our chrome"* via the `IVideoPresenter` + multi-visual DComp tree; `:188-189` "PiP drag moves the **DComp child visual** off-loop via `Place`" — an **in-window** draggable visual, **not** a separate always-on-top OS window; `:169` confirms theatre/PiP/sidebar are in-window composition layouts arbitrated by priority. Tear-out/separate-window is noted only as a *future E10* use of the popup seam (`Pal.cs:235`). So there is **no current WAVEE requirement** for an always-on-top mini-player or OS-borderless-fullscreen window. What remains is a legitimate-but-*generic* media affordance (pin-above-other-apps; a fullscreen theatre *enhancement*) — real, but speculative and un-required versus the requirement-traced items above. **Migrate-later.**

---

### 3.7 — ThemeSettings high-contrast probe + live theme-change (theming subsystem) — migrate-later, effort S

**WASDK surface.** `specs/themes/ThemeSettings.md:49-146` — `ThemeSettings.HighContrast` (bool) + `HighContrastScheme` + `Changed`, `CreateForWindowId`.

**OS primitive (flat Win32, per the spec itself).** `ThemeSettings.md:21`: "implemented by calling `SystemParametersInfo` with the `SPI_GETHIGHCONTRAST` flag"; the `Changed` event = `WM_THEMECHANGED` (`:35`) on a window. Plus `GetSysColor` for the HC palette entries.

**Coverage.** `Win32Theme.cs` covers accent (registry, `:39/:58`) + dark/Mica (`ApplyWindowMaterial`), but the **high-contrast runtime probe is not in shipping code** — only reserved in design (`design/subsystems/theming.md:276,346` plans `SystemParametersInfo(SPI_GETHIGHCONTRAST)` + the `UseHighContrast` hook). No live `WM_THEMECHANGED`/`WM_SETTINGCHANGE` subscription exists. **Partial → finish per the existing theming design.**

**Why migrate-later, and why not a WindowsApi pillar.** This is a renderer/theme-seam concern (the engine's T1 `ISystemColors` PAL seam, `design/app-requirements-waveemusic.md:151-155`), **not** an OS-services pillar — it belongs in `FluentGpu.Windows`/theming, wired into the existing `Win32Platform` WndProc, not in `FluentGpu.WindowsApi`. Effort **S** (~50 LoC). Value MEDIUM (a11y-mandated; the blur-heavy editorial backdrop must flatten under HC) but it is *finishing owned design*, not a new capability.

---

### 3.8 — Packaged `GetActivatedEventArgs` delivery (extend `Activation/`) — migrate-later (with MSIX), effort M

**OS primitive.** In-box WinRT via cold ABI: `RoGetActivationFactory` + `IProtocolActivatedEventArgs.Uri` / `IFileActivatedEventArgs.Files` (the platform `Windows.ApplicationModel.AppInstance.GetActivatedEventArgs`), gated on `PackageIdentity.IsPackaged`.

**Coverage.** Unpackaged classification already works (`Activation/ActivationArgs.cs` Launch/File/Protocol/Startup/Toast from the command line). The residual is the load-bearing **OPEN-QUESTION #4 already flagged in-code** (`ActivationArgs.cs:64-71`): a *packaged* protocol/file launch is not guaranteed on the command line (`windows.FullTrustApplication` does not pass the URI), so a future MSIX build must call the platform `GetActivatedEventArgs`. **Only matters if WAVEE ships MSIX** — defer with packaging. Effort **M** (the WinRT args ABI).

---

### 3.9 — Localization (i18n): a hand-rolled JSON engine instead of MRTCore — **SHIPPED** (engine feature, not a WindowsApi pillar)

**Decision.** Localization was the one item the original survey deferred outright (MRTCore, §4: "big lift, low value"). On review the right answer was not *adopt MRT later* but *build a better, smaller thing now*. WASDK's `MRTCore`/`resources.pri`/`ResourceManager` is reflection- and satellite-assembly-adjacent, needs a build-time `.pri` authoring pipeline FluentGpu does not have, stores strings in an opaque binary (not diff-reviewable), and — like RESW — has **no plural/select grammar**. FluentGpu instead ships a from-scratch i18n engine under **`src/FluentGpu.Engine/Localization/`** (platform-agnostic, macOS-port-friendly — it is an *engine* feature, behind a host seam, NOT a `FluentGpu.WindowsApi` pillar), now paired with a build-time typed-key **source generator** (`src/FluentGpu.Localization.SourceGen/`, see below). It is implemented, compiles, and is gate-verified (`--loc-probe`, **46/46 [PASS]** including 13 generated-key assertions, exit 0) and render-verified in a gallery page (English / Polish four-form plurals + gender select / pseudo-loc).

**Why it beats RESW/MRT for this product.**
- **JSON resources, not `.resw`/`.pri`.** Per-culture files (`en-US.json`, `fr-FR.json`, `fr.json`, `de-DE.json`, `pl-PL.json`); nested objects model namespaces and **flatten to dotted keys** (`{"app":{"title":…}}` → `app.title`). Hand-editable, diff-friendly, one logical string per line. An optional reserved `"$culture"` key self-describes the file; any `$`-prefixed key (e.g. `$comment`) is metadata excluded from the table, so translator notes live in-file (JSON has no comments).
- **AOT/trim-safe loader.** Parsed with `System.Text.Json` `Utf8JsonReader` as a forward token walk — **no reflection, no `JsonSerializer`, no source-gen serializer, no `JsonDocument` DOM** (`JsonResourceReader.cs`). Fully `PublishAot` + `TrimMode full` clean. (Contrast `ResourceManager`, which is forbidden here precisely because it resolves satellite resources via reflection.)
- **Named placeholders `{name}`, not positional `{0}`.** Translators see meaning and may reorder freely; a missing argument renders **visibly** (`{name}` kept) and never throws.
- **An ICU MessageFormat subset — the marquee feature RESW/MRT lack.** `{count, plural, =0 {…} one {# …} few {# …} many {# …} other {# …}}` and `{gender, select, male {…} female {…} other {…}}`, with `#` → the count and nested `{name}` inside branch bodies, via a ~250-LoC recursive-descent parser (`MessageFormatter.cs`). Plural **categories are per-language**: under `InvariantGlobalization=true` the BCL exposes no CLDR data, so a hand-rolled CLDR-lite table (`PluralRules.cs`) is **mandatory** — implemented for en/fr/de and **Polish (one/few/many)** as proof the rule set is genuinely multi-form and extensible (every other language falls back to `other`, the honestly-scoped default).
- **Pseudo-localization** (`PseudoLocalizer.cs`): a dev QA transform (the `qps-ploc` convention) that accents every letter and expands ~+40% with `⟦…⟧` brackets, surfacing un-externalized literals and layout overflow at a glance. Placeholders are preserved.
- **Reactive, no re-render.** A culture-epoch `Signal<int>` (`Localization.CultureEpoch`) is read inside `Localization.Get`/`Format`; `SetCulture` bumps it. A text node binds `Text = Prop.Of(() => Loc.Get(key))` (the engine's existing `Prop<T>` bind path), so a language switch re-resolves **only the bound text nodes**, not the component tree — the binding-not-re-render win. Hooks `L(key)`/`Lf(key, args)` (return `Prop<string>`) and `UseLocale()` (re-render-on-change) are added to `Hooks/Component.cs` + `Hooks/RenderContext.cs`. Dev hot-reload (`LocalizationWatcher.cs`) watches the JSON dir and reloads on save through the host UI-thread poster (opt-in).
- **Per-key fallback chain:** exact culture → parent (`fr-FR`→`fr`) → `DefaultCulture` → the key itself (a missing key renders as `[app.title]`).

**The one Win32 piece — behind a seam.** Under `InvariantGlobalization` the engine cannot read `CultureInfo.CurrentUICulture`, and the portable engine carries no OS code, so OS UI-culture detection is a host-injected `Localization.OsCultureProvider` (`Func<string?>`). The Windows app wires it to `GetUserDefaultLocaleName` (kernel32, `[LibraryImport]` in `WindowsApiInterop.cs`); a macOS port wires `CFLocale` to the identical seam. This is the *only* localization-specific OS dependency — exactly the `PrimaryLanguageOverride` read MRT would have needed, minus the whole `.pri`/`ResourceManager` apparatus.

**Posture.** Zero new interop beyond the one flat-Win32 locale read; no `Microsoft.WindowsAppSDK`, no CsWinRT, no `Mrm*`; NativeAOT + `TrimMode full` survive. **Effort actually spent: M** (runtime engine + parser + gallery page + probe).

**Typed-key source generator — ✅ SHIPPED (this cycle).** The proposed Roslyn generator is now built, compiling, and gate-verified — and it is the **first analyzer/source-generator actually wired into the repo** (an `OutputItemType="Analyzer"` reference had zero prior instances; the `src/FluentGpu.SourceGen` scaffold is still markers-only and unreferenced). An `IIncrementalGenerator` reads the base-culture JSON (`en-US.json`) at build and emits a nested `static partial class Strings` (namespace `FluentGpu.WindowsApp`) mirroring the JSON namespaces, with `const string` leaves whose value **is** the dotted key (`Strings.App.Title == "app.title"`, `Strings.Player.Queue == "player.queue"`) — so call sites use `L(Strings.Player.Queue)` (typo-proof, refactor-safe, IntelliSense) instead of raw strings.
- **Wiring path (lowest-risk).** A **new isolated project** `src/FluentGpu.Localization.SourceGen/` (netstandard2.0), referenced **only** by the gallery via `<ProjectReference … OutputItemType="Analyzer" ReferenceOutputAssembly="false" />` so the analyzer assembly is **never linked into the net10.0 app** (confirmed absent from `deps.json`). Chosen over extending `FluentGpu.SourceGen` because that scaffold's charter is the engine's DSL/Element/Theme generators (a parallel session's natural owner) and a loc generator does not belong there. Registered in `src/FluentGpu.slnx` under `/Tooling/`. The base JSON is fed as `<AdditionalFiles Include="assets\loc\en-US.json" FluentGpuLocBase="true" />` with a `CompilerVisibleItemMetadata` surface, plus a path-convention fallback (`assets/loc/en-us.json`) for robustness.
- **Hand-rolled JSON, no `System.Text.Json`.** A netstandard2.0 analyzer cannot reliably bundle `System.Text.Json`, so the generator ships a **dependency-free** ~built-in scanner (`TinyJsonReader.cs`: objects + string leaves + nesting; skips `$`-meta keys like `$culture`/`$comment`). Malformed/absent JSON degrades to an empty `Strings` class plus a diagnostic (`FLLOC001`/`FLLOC002`), never a build break.
- **Typed format methods (advanced — built).** Each parameterized key emits a `…Key` const **plus** a typed method routing to `Loc.Format` — e.g. `Strings.Player.SongsBy(object count, object artist)` → `Loc.Format("player.songsBy", ("count", count), ("artist", artist))`, `Strings.Profile.Invited(object gender, object name)`. A wrong/missing placeholder is now a **compile error**. The ICU placeholder collector (`IcuPlaceholders.cs`) is branch-aware — it collects `{name}`/`{artist}` but excludes plural/select branch-body literals like `{No songs by {artist}}`. (Method/const same-name collision resolved via the `…Key` suffix on parameterized leaves; plain leaves keep the bare-name `const`.)
- **AOT-clean by construction.** The generated code is `const string` + plain static methods only — zero reflection, zero `ResourceManager`, no runtime cost; trim/AOT-clean exactly like the runtime engine.
- **Diagnostics: PARTIAL (as designed).** `FLLOC001` (malformed base JSON → empty class, no break) and `FLLOC002` (no base resource) ship. **Deferred** (designed, not built): cross-culture diagnostics — a key present in `en-US` but missing from `fr-FR`/`de-DE`/`pl-PL`, and unused-key warnings — which would require wiring all 5 culture JSONs as `AdditionalFiles` + a second parse pass; orthogonal to the core deliverable. Minor refinement noted: plural operands are typed `object` (1:1 with `Loc.Format`'s `(string, object)[]`) rather than `int`.
- **Gate-verified.** Isolated gallery build is clean (`dotnet build … -o .tmp-shots/gen-build --no-incremental`, 0 errors; the ~2.7k warnings are pre-existing CA1416 platform-guard, unrelated). `LocalizationPage.cs` call sites migrated to `Strings.*` (e.g. `L(Strings.App.Title)`, `Lf(Strings.App.GreetingKey, …)`, `Prop.Of(() => Strings.Files.Count(Count()))`); `--loc-probe` now **46/46 [PASS]** (exit 0), of which **13** are generated-key assertions (const-equals-key, key-resolves-to-en-US-value, typed-method output for `Greeting`/`Files.Count(0/1/5)`/`SongsBy`/`Profile.Invited`/`Player.Added`); the Localization page still renders via `--shot gallery:localization`.

---

## 4. SKIP list (with per-item reason)

| Item | Reason |
|------|--------|
| **Storage.Pickers** | **covered.** `Dialogs/FilePicker.cs:9-16` already wraps the same `IFileOpenDialog`/`IFileSaveDialog`/`IShellItem` COM. WASDK adds only `SetClientGuid` (SettingsIdentifier) persistence + elevated-process support — thin optional enhancement, not a WAVEE need. |
| **BadgeNotifications** | **covered / identity-gated.** `Shell/TaskbarManager.SetOverlayIcon` (`:124`, `ITaskbarList3`, **no identity**) already paints a taskbar status glyph (the now-playing/unread badge WAVEE wants). The WASDK `BadgeNotificationManager` **requires package identity** (`specs/BadgeNotifications/BadgeNotifications-spec.md:44`) and routes through the internal frameworkudk PALs. The only genuine delta is a numeric **Start-tile/Action-Center** badge (identity-gated; WAVEE has no unread-count concept). Skip unless a numeric Start-tile surface is later wanted (then `BadgeUpdateManager` WinRT, packaged-only). |
| **OAuth2Manager** | **covered by composition.** The redirect hook is `scheme://callback` protocol activation, already delivered by `Activation/ProtocolRegistrar` + `ActivationArgs.TryGetUri` (the WAVEE OAuth callback is named at `ActivationArgs.cs:22`). The PKCE/token state machine is ~15 lines of app code over `HttpClient` + `Credentials`. Build it in app code on the existing seam — no OS primitive to wrap, no pillar warranted. |
| **AppInstance rich redirection** (AppLifecycle/Instancing) | **covered (sufficient) / high-effort-low-value.** `SingleInstanceGate` already does named-mutex election + `WM_COPYDATA` redirect (UI-thread-correct). WASDK's `GetInstances`/`RegisterKey` uses a private 512-PID table + MMF + GUID queue — not one OS primitive; high effort, low marginal value over the existing gate. |
| **Restart** (AppLifecycle) | **niche.** OS primitive is flat `RegisterApplicationRestart`/`RegisterApplicationRecoveryCallback` (hand-rollable) but a media app rarely self-restarts. Optional tiny add to Power/Activation, not a priority. |
| **EnvironmentManager** | **niche / no WAVEE need.** Hand-rollable (`SetEnvironmentVariable` + `RegSetValueEx` + `SendMessageTimeout(WM_SETTINGCHANGE)`); its own spec admits the only net-new-vs-BCL is uninstall-revert tracking. A music player does not edit user PATH. |
| **MRTCore / resources.pri + PrimaryLanguageOverride** | **SUPERSEDED — replaced by a hand-rolled JSON i18n engine (§3.9, shipped).** MRTCore is reachable only via OS WinRT `ResourceManager` (NOT by reusing WASDK's own `Mrm*` DLL — a posture violation) and carries a build-time `.pri` authoring pipeline FluentGpu lacks; its disk format is opaque/non-diffable. Rather than adopt it, FluentGpu now ships its own **JSON-resource, signal-backed localization engine** (`src/FluentGpu.Engine/Localization/`) that is *strictly better* for this product: hand-editable per-culture JSON, named placeholders, an ICU plural/select subset (which RESW/MRT-string-tables lack), pseudo-localization, and live no-re-render switching — all AOT-clean with zero new interop. The OS-locale read MRT needs (`PrimaryLanguageOverride`/`GetUserDefaultLocaleName`) is the only Win32 piece, wired through a host seam. See §3.9. |
| **CameraCaptureUI** | **off-domain.** In-box `Windows.Media.Capture.CameraCaptureUI` + `IInitializeWithWindow` is reachable as cold COM, but a music app has no camera-capture need. |
| **Licensing** (MsixLicensing / Store license) | **only-if-Store-distributed.** `dev/Licensing/MsixLicensing.cpp:9` is a `//TODO` stub over a WASDK-runtime header; the real check is WinRT `StoreContext` (cold COM). Relevant only if WAVEE ships through the Store. |
| **AccessControl** (SecurityDescriptorHelpers) | **niche.** `GetSecurityDescriptorForAppContainerNames` + SDDL helpers only matter for AppContainer IPC ACLs; full-trust FluentGpu does not need them. |
| **DWriteCore** | **covered / needs-WASDK-package.** The redistributable DirectWrite (newest API on old Windows) needs the WASDK package; FluentGpu already uses in-box DirectWrite in its own text stack. No gap. |
| **PushNotifications** | **posture-violating (WASDK-runtime-only).** Needs the WindowsAppRuntime background infrastructure + WNS socket + AAD/portal registration (`specs/PushNotifications/PushNotifications-spec.md:24-39`); unpackaged 3rd-party apps cannot self-register a channel. OS primitive not hand-rollable standalone, and WAVEE specifies no cloud-push service. |
| **BackgroundTask** (BackgroundTaskBuilder) | **posture-violating + mis-shaped.** Needs the UniversalBGTask host + a `windows.backgroundTasks` manifest extension (`specs/BackgroundTask/BackgroundTaskBuilder.md:18,33`); WASDK-runtime + packaged-only. WAVEE needs continuous *foreground* audio (already served by `Power/KeepAwake`), not OS trigger-driven background work. |
| **WASDK runtime plumbing** (Bootstrap, DynamicDependency\*, Detours, UndockedRegFreeWinRT, Deployment/DeploymentAgent, RestartAgent, Projections, Decimal, RuntimeCompatibilityOptions, VersionInfo, PackageManager-dynamic) | **N/A — exists only to host WASDK itself.** FluentGpu does not use WASDK, so these are out of scope; the working `Add-AppxPackage -Register` MSIX prototype already covers packaging without the NuGet. |

---

## 5. Implementation order

0. **Localization engine + typed-key generator (§3.9) — ✅ DONE this cycle.** The runtime engine shipped under `src/FluentGpu.Engine/Localization/` (the `L`/`Lf`/`UseLocale` hooks + the gallery page), and the typed-key Roslyn **source generator** is now shipped too — `src/FluentGpu.Localization.SourceGen/` (netstandard2.0 `IIncrementalGenerator`, hand-rolled JSON, AOT-clean `const`+typed-method emission), wired into the gallery as the **first analyzer in the repo** (`OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"`). Gate-verified end to end: isolated gallery build clean, `--loc-probe` **46/46 [PASS]** (13 generated-key assertions), Localization page renders. Only the cross-culture / unused-key diagnostics remain deferred (designed, not built — needs all 5 JSONs wired + a second parse pass). The numbered items below are the still-unbuilt WindowsApi-pillar candidates.
1. **PowerSession.ReadPower()** — the `GetSystemPowerStatus` snapshot (§3.1). Smallest, zero engine coupling, extends an existing pillar. Ship this first; defer the GUID change-event subscription until the HWND-ownership question is decided.
2. **`Storage/AppDataStore`** (unpackaged) — new pillar (§3.2), zero new interop, reuses the registry ABI from `ProtocolRegistrar.cs`/`AumidRegistration.cs`. Use the impl path `HKCU\SOFTWARE\<pub>\<prod>`, scalar REG types, skip the `+0x20000` tagging.
3. **`SystemMediaControls.SetTimeline()`** — the SMTC v2 write path (§3.3): QI to `ISystemMediaTransportControls2`, `RoActivateInstance` the TimelineProperties, five `put_*`. Drives the OS scrubber from the existing 1 Hz clock.
4. **Toast Update / `<progress>` / Remove** (§3.4) — extend `ToastBuilder` + `ToastNotifier`; the activator/factory/HSTRING RAII already ship. Prioritize **RemoveByTag** + determinate progress; do *not* build a per-track now-playing toast (SMTC owns that).
5. **`SystemMediaControls.PlaybackPositionChangeRequested`** (§3.5) — fast-follow of step 3; budget for the **newly derived parameterized IID** (the highest-risk value in the Media pillar) + a second generated-COM handler pair.

**Deferred (revisit on trigger):**
6. **AppWindow presenter** on `IPlatformWindow` (§3.6) — when a product decision actually wants an always-on-top pin / fullscreen theatre *window* (not the in-window DComp PiP the design already specifies). Template off `Win32PopupWindow.cs`; re-assert the `WM_NCCALCSIZE` frame geometry on fullscreen toggle (Mica/dark DWM attrs persist).
7. **ThemeSettings HC probe** (§3.7) — finish per `theming.md`, in the engine theme seam, not WindowsApi.
8. **Packaged `GetActivatedEventArgs`** (§3.8) — only when an MSIX build ships (closes OPEN-QUESTION #4).

**Posture verdict.** Every migrate-now/-later item is flat Win32 or the proven hand-rolled WinRT ABI / cold COM; NativeAOT + `TrimMode full` survive; no `Microsoft.WindowsAppSDK` NuGet, no CsWinRT, no `ComWrappers`-subclassing. The only items whose richer form needs package identity (Start-tile badge, packaged activation args) are correctly deferred; the WASDK-runtime-only items (Push, BackgroundTask, real Licensing) are correctly skipped.
