#requires -Version 5.1
<#
.SYNOPSIS
  Build, loosely register, identity-launch and verify the FluentGpu Capability Gallery as a PACKAGED (MSIX /
  package-identity) app — the working prototype.

.DESCRIPTION
  Uses the Developer-Mode "register the unpackaged layout" inner-loop workflow: a self-contained win-x64 publish,
  then `Add-AppxPackage -Register <pubDir>\AppxManifest.xml`. This grants FULL package IDENTITY with NO .msix
  container, NO makeappx, NO code signing, NO cert trust, and NO admin elevation (it is per-user). Requires
  Developer Mode (HKLM AppModelUnlock AllowDevelopmentWithoutDevLicense=1), which is already on.

  The packaged app is launched UNDER IDENTITY through its uap5:AppExecutionAlias (fluentgpu-gallery.exe) — the OS
  alias shim cold-launches the registered package WITH package identity and forwards the command line, so
  `fluentgpu-gallery.exe --packaging-probe` runs the gallery's report-only probe (PackagingProbe.cs), which writes
  PackageIdentity.* to a fixed non-virtualized file BEFORE any window/GPU spins up. The script reads that file from
  OUTSIDE the process and asserts IsPackaged=True with a real PackageFullName.

  Idempotent: any prior FluentGpu.Gallery registration and stale probe file are removed up front; re-register uses
  -ForceUpdateFromAnyVersion. Teardown is left to the caller (a one-liner is printed at the end).

  References:
    - Add-AppxPackage -Register (loose layout): https://learn.microsoft.com/en-us/powershell/module/appx/add-appxpackage
    - Register the unpackaged layout (Developer Mode inner loop):
      https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-debug
    - AppExecutionAlias keeps stdin/stdout in the terminal for console-style launch:
      C:\WAVEE\WindowsAppSDK\dev\Templates\Dotnet\README.md:100 (WinAppRunUseExecutionAlias)

.PARAMETER SkipPublish
  Reuse an existing publish in .pkg-publish (skip `dotnet publish`) — for fast re-register iterations.

.PARAMETER KeepRegistered
  Do not remind about teardown; leave the package registered (default behaviour is also to leave it registered).
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$KeepRegistered
)

$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Proj      = Join-Path $RepoRoot 'src\FluentGpu.WindowsApp\FluentGpu.WindowsApp.csproj'
$PubDir    = Join-Path $RepoRoot '.pkg-publish'
$PkgName   = 'FluentGpu.Gallery'
$Alias     = 'fluentgpu-gallery.exe'
$ProbeFile = Join-Path $RepoRoot '.tmp-shots\pkg-probe.txt'   # the primary non-virtualized readback PackagingProbe.cs writes
$ExpectedFullNameRx = '^FluentGpu\.Gallery_1\.0\.0\.0_x64__[0-9a-z]{13}$'

function Write-Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# 0) Idempotent: drop any prior registration + stale probe file -------------------------------------------------
Write-Step '0) idempotent cleanup (prior registration + stale probe file)'
Get-AppxPackage -Name $PkgName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
Remove-Item $ProbeFile -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ProbeFile) | Out-Null

# 1) Self-contained win-x64 publish (NOT AOT for the prototype — AOT is a documented follow-up) -----------------
#    Resolves .NET 10 via src/global.json (10.0.300, rollForward=latestFeature) because we publish the project
#    UNDER src/ — the repo-root SDK is 11-preview and must not be used.
if ($SkipPublish -and (Test-Path (Join-Path $PubDir 'FluentGpu.WindowsApp.exe'))) {
    Write-Step '1) publish (SKIPPED — reusing existing .pkg-publish)'
} else {
    Write-Step '1) dotnet publish (self-contained win-x64, Release)'
    if (Test-Path $PubDir) { Remove-Item $PubDir -Recurse -Force }
    & dotnet publish $Proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $PubDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
}
$Exe = Join-Path $PubDir 'FluentGpu.WindowsApp.exe'
if (-not (Test-Path $Exe)) { throw "published exe not found: $Exe (manifest Executable must match this name exactly)" }

# 2) Lay out the loose package: manifest at the publish ROOT + logo assets under Assets\ -----------------------
Write-Step '2) lay out AppxManifest.xml + logo assets into the publish dir'
Copy-Item (Join-Path $PSScriptRoot 'AppxManifest.xml') (Join-Path $PubDir 'AppxManifest.xml') -Force
$AssetsOut = Join-Path $PubDir 'Assets'
New-Item -ItemType Directory -Force -Path $AssetsOut | Out-Null
# Prefer the checked-in packaging\Assets PNGs; (re)generate any that are missing so the script is self-sufficient.
Add-Type -AssemblyName System.Drawing
function Ensure-Png([int]$w,[int]$h,[string]$name) {
    $src = Join-Path $PSScriptRoot "Assets\$name"
    $dst = Join-Path $AssetsOut $name
    if (Test-Path $src) { Copy-Item $src $dst -Force; return }
    $bmp = New-Object System.Drawing.Bitmap($w,$h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255,16,110,190))
    $g.Dispose(); $bmp.Save($dst,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
Ensure-Png  44  44 'Square44x44Logo.png'
Ensure-Png 150 150 'Square150x150Logo.png'
Ensure-Png  50  50 'StoreLogo.png'

# 3) Loose-register: full package IDENTITY, no signing / makeappx / admin (Developer Mode) ----------------------
Write-Step '3) Add-AppxPackage -Register (loose layout)'
Add-AppxPackage -Register (Join-Path $PubDir 'AppxManifest.xml') -ForceUpdateFromAnyVersion
$pkg = Get-AppxPackage -Name $PkgName
if (-not $pkg) { throw 'registration reported success but Get-AppxPackage found no FluentGpu.Gallery' }
$pkg | Format-List Name, PackageFullName, PackageFamilyName, InstallLocation

# 4) Launch UNDER IDENTITY via the AppExecutionAlias (retry until it resolves on PATH) -------------------------
Write-Step '4) launch fluentgpu-gallery.exe --packaging-probe (identity-bearing alias)'
Remove-Item $ProbeFile -ErrorAction SilentlyContinue   # clear again right before launch — no stale false-PASS
$launched = $false
foreach ($attempt in 1..8) {
    try {
        Start-Process -FilePath $Alias -ArgumentList '--packaging-probe' -ErrorAction Stop | Out-Null
        $launched = $true
        Write-Host "  launched via alias on attempt $attempt"
        break
    } catch {
        Write-Host "  attempt ${attempt}: alias not on PATH yet — retrying..." -ForegroundColor DarkYellow
        Start-Sleep -Milliseconds 750
    }
}
if (-not $launched) { throw "alias '$Alias' not resolvable on PATH after register (alias-shim propagation lag exceeded retries)" }

# 5) Read the authoritative probe file from OUTSIDE the process + assert identity -------------------------------
Write-Step '5) read back the probe file + assert IsPackaged=True'
$deadline = (Get-Date).AddSeconds(30)
while (-not (Test-Path $ProbeFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path $ProbeFile)) {
    throw "probe file never written at $ProbeFile — did the alias launch under identity?"
}
$body = Get-Content $ProbeFile -Raw
Write-Host "----- pkg-probe.txt -----`n$body`n-------------------------"

$map = @{}
foreach ($l in ($body -split "`r?`n")) { if ($l -match '^([^=]+)=(.*)$') { $map[$matches[1]] = $matches[2] } }

if ($map['IsPackaged'] -ne 'True') {
    Write-Error "FAIL: IsPackaged=$($map['IsPackaged']) — package identity did NOT attach. Diagnose: manifest Identity vs running exe, alias resolving to the loose exe, or register failed."
    Get-AppxPackage *FluentGpu* | Format-List Name, PackageFullName, InstallLocation
    exit 1
}
if ($map['PackageFullName'] -notmatch $ExpectedFullNameRx) {
    Write-Error "FAIL: unexpected PackageFullName '$($map['PackageFullName'])' (expected to match $ExpectedFullNameRx)"
    exit 1
}

Write-Host "`nPASS: packaged identity attached." -ForegroundColor Green
Write-Host "  PackageFullName = $($map['PackageFullName'])"
Write-Host "  PackageFamilyName = $($map['PackageFamilyName'])"
Write-Host "  AUMID = $($map['ApplicationUserModelId'])"
Write-Host "  Version = $($map['Version'])"
Write-Host "  InstalledLocation = $($map['InstalledLocation'])"
Get-AppxPackage *FluentGpu* | Format-List Name, PackageFullName, InstallLocation

if (-not $KeepRegistered) {
    Write-Host "`n(To undo: Get-AppxPackage -Name $PkgName | Remove-AppxPackage)" -ForegroundColor DarkGray
}
exit 0
