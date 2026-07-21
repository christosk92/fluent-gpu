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
PackageFullName=FluentGpu.Gallery_1.0.0.0_<arch>__<publisherhash>
PackageFamilyName=FluentGpu.Gallery_<publisherhash>
ApplicationUserModelId=FluentGpu.Gallery_<publisherhash>!App
```

## Run it

```powershell
packaging\pack-and-register.ps1                 # publish (host arch) -> register -> launch under identity -> assert
packaging\pack-and-register.ps1 -SkipPublish    # fast re-register (skip the publish)
packaging\pack-and-register.ps1 -Rid win-x64    # force x64 (emulated on an ARM64 host)
# teardown (reversible):
Get-AppxPackage -Name 'FluentGpu.Gallery' | Remove-AppxPackage
```

The script publishes self-contained for the **host architecture** (`-Rid`; defaults to
`win-arm64` on an ARM64 machine — a *native* build — else `win-x64`) to
`.pkg-publish-<arch>\`, patches the manifest `ProcessorArchitecture` to match, lays
`AppxManifest.xml` + `Assets\` beside the exe, `Add-AppxPackage -Register`s the loose
layout, launches the identity alias with `--packaging-probe`, and asserts `IsPackaged=True`.

**Verified on both arches** (this machine is ARM64):

| Build | PackageFullName | Identity | Full gallery (D3D12) renders packaged |
|---|---|---|---|
| `win-arm64` (native) | `FluentGpu.Gallery_1.0.0.0_arm64__b7ry0z9c4vbc4` | `IsPackaged=True` | ✅ |
| `win-x64` (emulated) | `FluentGpu.Gallery_1.0.0.0_x64__b7ry0z9c4vbc4` | `IsPackaged=True` | ✅ |

(`fluentgpu-gallery.exe --screenshot <png>` renders a real frame **under identity** — the
full GUI app works packaged, not just the console probe.)

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
- **NativeAOT:** this prototype uses regular self-contained CoreCLR publish (`-p:PublishAot`
  is wired behind the script's `-Aot` switch). AOT changes only the `dotnet publish` step —
  the manifest / register / alias mechanics are identical and arch-agnostic. **It is
  currently blocked on this machine by a *toolchain* bug, not by FluentGpu code:** the .NET 10
  ILCompiler (`microsoft.dotnet.ilcompiler/10.0.5`) emits a malformed native-link command for
  the installed **VS 2026-preview (v18) / MSVC 14.51-preview** layout, so `link.exe` exits 123
  (a `vswhere`-output fragment is spliced into the linker path). This reproduces on **both**
  `win-x64` (x64 cross-link) and `win-arm64` (native link), confirming it is the VS/MSVC
  preview, not the target arch. It should work unchanged once a stable MSVC toolset is present.
- **SDK determinism:** publish from a CWD under `src/` (or add a repo-root `global.json`)
  so the pinned `10.0.300` SDK is used rather than the machine default.
