# Packaging & release pipeline

Build, sign, and publish **FluentGpu Gallery** as an MSIX. FluentGpu is a **NativeAOT plain‑Win32 full‑trust** app
(no WindowsAppSDK / WinUI / XAML), so packaging goes straight through `MakeAppx` — and the package is a single
**~7 MB native binary** with **no bundled .NET runtime** (compare WaveeMusic's WinUI self‑contained JIT at ~200 MB).

## Files

| File | What |
|---|---|
| `pack-msix.ps1` | End‑to‑end local build: `dotnet publish` (AOT) → stage layout → `makepri` → `makeappx pack` → `signtool sign` → `.msix` |
| `AppxManifest.xml` | MSIX manifest template (packaged Win32 full‑trust; `__PUBLISHER__`/`__VERSION__`/`__ARCH__` substituted) |
| `AppInstaller.template.xml` | `.appinstaller` template for one‑click sideload **auto‑update** |
| `generate-appicon.ps1` | Regenerates the multi‑res app `.ico` + MSIX tile logos from `appicon-source.png` |
| `generate-download-buttons.ps1` | Regenerates the README "Download" button PNGs (x64/arm64 × light/dark) |
| `../.github/workflows/msix.yml` | CI: matrix arm64+x64 → (optional Trusted Signing) → GitHub Release + `.appinstaller` |

## Local build

```powershell
# Host arch, NativeAOT, self-signed dev cert → artifacts\FluentGpu.WindowsApp_0.1.0.0_<arch>.msix
pwsh build/pack-msix.ps1

pwsh build/pack-msix.ps1 -Arch x64 -Version 0.2.0.0   # pick arch / version (4-part)
pwsh build/pack-msix.ps1 -Install                     # build, sign, trust the dev cert, Add-AppxPackage (run elevated)
pwsh build/pack-msix.ps1 -NoAot                        # framework-dependent self-contained (fast iteration; not release)
pwsh build/pack-msix.ps1 -NoSign                       # leave unsigned (CI re-signs with Trusted Signing)
```

NativeAOT is the **csproj default** (`<PublishAot>true</PublishAot>` in `FluentGpu.WindowsApp.csproj`) — it engages only at
`dotnet publish`; `dotnet build`/`dotnet run` stay fast JIT. AOT's native link needs MSVC `link.exe`; the script adds the
VS Installer dir to `PATH` so ILC can find it via `vswhere`. Requires the **Windows SDK** (`makeappx`/`makepri`/`signtool`).

### Installing a self‑signed build

`signtool verify /pa` reports the chain as untrusted until the dev cert is installed — expected. `-Install` imports the
cert into `LocalMachine\TrustedPeople` (needs an elevated shell) and runs `Add-AppxPackage`. To trust it manually, import
the cert (subject `CN=MarTeco Dev, …`) into **Local Machine → Trusted People**, then double‑click the `.msix`.

## CI / publish (`.github/workflows/msix.yml`)

Trigger: push a tag `v*`, or run the workflow manually. Flow:

1. **version** — derive the 4‑part MSIX version `X.Y.Z.<run-number>` (monotonic, so `.appinstaller` always updates).
2. **build** (matrix) — `x64` on `windows-latest`, `arm64` on `windows-11-arm`; each runs `pack-msix.ps1 -NoSign` (AOT
   can't cross‑compile, hence per‑arch runners). Uploads the unsigned `.msix`.
3. **sign** *(optional)* — runs only if the repo variable `TRUSTED_SIGNING_ACCOUNT` is set; signs both packages with
   **Azure Trusted Signing**.
4. **release** — generates a per‑arch `.appinstaller` and publishes a GitHub Release with the `.msix` + `.appinstaller`.

### Signing config (optional, for trusted public releases)

Set these repo **secrets** — `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` — and **variables** —
`TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_PROFILE`, and `RELEASE_PUBLISHER` (the cert
subject, which **must match** the package manifest `Publisher`). Without them, CI ships **unsigned** packages (install via
the local dev‑cert flow above). The `.appinstaller` `Publisher` must match the signed package exactly.

## Regenerating art

```powershell
# Replace build/appicon-source.png (square PNG), then:
powershell -ExecutionPolicy Bypass -File build/generate-appicon.ps1          # .ico (embedded as <ApplicationIcon> + the
                                                                             #  Win32 window icon) + MSIX tile logos
powershell -ExecutionPolicy Bypass -File build/generate-download-buttons.ps1 # README download buttons
```
