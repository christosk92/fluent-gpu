# Packaging a FluentGpu app as MSIX / with package identity

This folder is the **app-side** MSIX packaging story for a FluentGpu app (per
`docs/plans/windowsapi-implementation-research.md` §3 — packaging is an app concern,
not a library one). It packages the gallery (`FluentGpu.WindowsApp`) as a full-trust
Win32 desktop app so it runs with real **package identity** — which is what lights up
the OS-services that demand it (`FluentGpu.WindowsApi.Packaging.PackageIdentity`,
notification COM activation by manifest, etc.).

No `Microsoft.WindowsAppSDK` NuGet, no CsWinRT — a hand-rolled manifest, consistent
with the engine's posture.

## Working prototype (verified)

A loose-registered build reports, read back from outside the process:

```
IsPackaged=True
PackageFullName=FluentGpu.Gallery_1.0.0.0_x64__<publisherhash>
PackageFamilyName=FluentGpu.Gallery_<publisherhash>
ApplicationUserModelId=FluentGpu.Gallery_<publisherhash>!App
```

## Run it

```powershell
packaging\pack-and-register.ps1            # publish self-contained -> register -> launch under identity -> assert
packaging\pack-and-register.ps1 -SkipPublish   # fast re-register (skip the publish)
# teardown (reversible):
Get-AppxPackage -Name 'FluentGpu.Gallery' | Remove-AppxPackage
```

The script publishes self-contained win-x64 to `.pkg-publish\`, lays `AppxManifest.xml`
+ `Assets\` beside the exe, `Add-AppxPackage -Register`s the loose layout, launches the
identity alias with `--packaging-probe`, and asserts `IsPackaged=True`.

## How identity attaches (no signing, no makeappx, no admin)

- **Developer Mode** must be ON (`HKLM\…\AppModelUnlock\AllowDevelopmentWithoutDevLicense=1`).
  That lets Windows register an **unpackaged layout** (a directory + `AppxManifest.xml`)
  as a real package — no `.msix` container, no code signature, no admin.
- `Add-AppxPackage -Register AppxManifest.xml` creates the identity record
  (`PackageFullName = Name_Version_Arch__publisherhash`; the hash is derived from the
  manifest `Publisher` string).
- **Identity only attaches to processes started via the package activation path.**
  Launching `.pkg-publish\FluentGpu.WindowsApp.exe` directly runs it *without* identity
  (`GetCurrentPackageFullName` → `APPMODEL_ERROR_NO_PACKAGE`). The manifest's
  `uap5:AppExecutionAlias` (`fluentgpu-gallery.exe`, deliberately a *different* name than
  the real exe so PATH hits the identity shim) activates the app **with** identity and
  forwards args — which is how `--packaging-probe` runs packaged from the command line.

## `--packaging-probe`

A console-style gallery mode (`src/FluentGpu.WindowsApp/PackagingProbe.cs`, routed in
`Program.cs` before `FluentApp.Run`) that writes the `PackageIdentity` fields to
`.tmp-shots\pkg-probe.txt` and exits — the readback the verification asserts on.

## Follow-ups (not needed for the prototype)

- **Distribution / SHIP path:** `makeappx pack` → `signtool sign` with a cert whose
  subject = the manifest `Publisher` (`CN=FluentGpu Dev`) → `Add-AppxPackage` the signed
  `.msix`. Trusting a self-signed cert needs **admin** (machine Trusted People/Root) — the
  loose-register path above avoids it for the inner loop.
- **NativeAOT:** this prototype uses regular self-contained CoreCLR publish. AOT packaging
  changes only the `dotnet publish` step (add `-p:PublishAot=true`); the manifest /
  register / alias mechanics are identical.
- **SDK determinism:** publish from a CWD under `src/` (or add a repo-root `global.json`)
  so the pinned `10.0.300` SDK is used rather than the machine default.
