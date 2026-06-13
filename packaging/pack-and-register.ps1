#requires -Version 5.1
<#
.SYNOPSIS
  Build, loosely register, identity-launch and verify the FluentGpu gallery as a PACKAGED (MSIX / package-identity)
  app -- the working prototype.

.DESCRIPTION
  Uses the Developer-Mode "register the unpackaged layout" inner loop: a self-contained publish for the host arch
  (-Rid; win-arm64 native on an ARM64 machine, win-x64 emulated), then Add-AppxPackage -Register of the publish
  directory's AppxManifest.xml. This grants FULL package identity with NO .msix container, NO makeappx, NO code
  signing, NO cert trust, and NO admin (per-user). Requires Developer Mode (AppModelUnlock
  AllowDevelopmentWithoutDevLicense=1).

  The packaged app is launched UNDER IDENTITY through its uap5:AppExecutionAlias (fluentgpu-gallery.exe), whose OS
  shim activates the registered package WITH identity and forwards the command line -- so 'fluentgpu-gallery.exe
  --packaging-probe' runs PackagingProbe.cs, which writes PackageIdentity.* to a fixed non-virtualized file before any
  window spins up. This script reads that file from OUTSIDE the process and asserts IsPackaged=True.

  Idempotent: any prior registration + stale probe file are removed up front.

.PARAMETER Rid
  Target runtime: win-x64 | win-arm64. Default = host arch (win-arm64 on ARM64, else win-x64). The manifest
  ProcessorArchitecture is patched to match.

.PARAMETER Aot
  Add -p:PublishAot=true. NOTE: currently toolchain-blocked on this machine (ILCompiler 10.0.5 x VS2026-preview /
  MSVC-14.51-preview link-path bug -- link.exe exits 123). The MSIX manifest/register/alias mechanics are
  AOT-agnostic, so this works unchanged once a stable MSVC toolset is present.

.PARAMETER SkipPublish
  Reuse an existing publish (skip dotnet publish) for fast re-register iterations.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')]
    [string]$Rid = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }),
    [switch]$Aot,
    [switch]$SkipPublish,
    [switch]$KeepRegistered
)

$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Proj      = Join-Path $RepoRoot 'src\FluentGpu.WindowsApp\FluentGpu.WindowsApp.csproj'
$Arch      = $Rid -replace '^win-',''                       # x64 | arm64 (manifest ProcessorArchitecture)
$PubDir    = Join-Path $RepoRoot ('.pkg-publish-' + $Arch)   # per-arch publish dir = loose-register install location
$PkgName   = 'FluentGpu.Gallery'
$Alias     = 'fluentgpu-gallery.exe'
$ProbeFile = Join-Path $RepoRoot '.tmp-shots\pkg-probe.txt'
$ExpectedRx = '^FluentGpu\.Gallery_1\.0\.0\.0_' + $Arch + '__[0-9a-z]{13}$'

function Write-Step([string]$m) { Write-Host ('=== ' + $m + ' ===') -ForegroundColor Cyan }

# 0) idempotent cleanup ----------------------------------------------------------------------------------------------
Write-Step '0) cleanup (prior registration + stale probe file)'
Get-AppxPackage -Name $PkgName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
Remove-Item $ProbeFile -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ProbeFile) | Out-Null

# 1) self-contained publish for the host arch (NOT AOT unless -Aot; AOT currently toolchain-blocked) ----------------
$ExePath = Join-Path $PubDir 'FluentGpu.WindowsApp.exe'
if ($SkipPublish -and (Test-Path $ExePath)) {
    Write-Step ('1) publish SKIPPED (reusing ' + $PubDir + ')')
} else {
    $aotSuffix = ''
    if ($Aot) { $aotSuffix = ' -p:PublishAot=true' }
    Write-Step ('1) dotnet publish -r ' + $Rid + ' --self-contained' + $aotSuffix)
    if (Test-Path $PubDir) { Remove-Item $PubDir -Recurse -Force }
    if ($Aot) {
        Write-Host '   NOTE: -Aot is toolchain-blocked on this machine (link.exe exit 123); see -Aot help.' -ForegroundColor DarkYellow
        & dotnet publish $Proj -c Release -r $Rid --self-contained true -p:PublishSingleFile=false -p:PublishAot=true -o $PubDir
    } else {
        & dotnet publish $Proj -c Release -r $Rid --self-contained true -p:PublishSingleFile=false -o $PubDir
    }
    if ($LASTEXITCODE -ne 0) { throw ('dotnet publish failed (exit ' + $LASTEXITCODE + ')') }
}
if (-not (Test-Path $ExePath)) { throw ('published exe not found: ' + $ExePath) }

# 2) lay out the loose package: manifest (arch-patched) at the publish ROOT + logo assets under Assets\ -------------
Write-Step '2) lay out AppxManifest.xml + logo assets'
$archAttr = 'ProcessorArchitecture="' + $Arch + '"'
$srcManifest = Join-Path $PSScriptRoot 'AppxManifest.xml'
$dstManifest = Join-Path $PubDir 'AppxManifest.xml'
$mtext = (Get-Content $srcManifest -Raw) -replace 'ProcessorArchitecture="[^"]*"', $archAttr
Set-Content -Path $dstManifest -Value $mtext -Encoding UTF8
$assetsOut = Join-Path $PubDir 'Assets'
New-Item -ItemType Directory -Force -Path $assetsOut | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Assets\*.png') $assetsOut -Force

# 3) loose-register: full identity, no signing/makeappx/admin (Developer Mode) -------------------------------------
Write-Step '3) Add-AppxPackage -Register (loose layout)'
Add-AppxPackage -Register $dstManifest -ForceUpdateFromAnyVersion
$pkg = Get-AppxPackage -Name $PkgName
if (-not $pkg) { throw 'register succeeded but Get-AppxPackage found no FluentGpu.Gallery' }
$pkg | Format-List Name, PackageFullName, PackageFamilyName, InstallLocation

# 4) launch UNDER IDENTITY via the AppExecutionAlias (retry until it resolves on PATH) -----------------------------
Write-Step '4) launch fluentgpu-gallery.exe --packaging-probe (identity alias)'
Remove-Item $ProbeFile -ErrorAction SilentlyContinue
$launched = $false
foreach ($attempt in 1..8) {
    try {
        Start-Process -FilePath $Alias -ArgumentList '--packaging-probe' -ErrorAction Stop | Out-Null
        $launched = $true
        Write-Host ('   launched via alias on attempt ' + $attempt)
        break
    } catch {
        Write-Host ('   attempt ' + $attempt + ': alias not on PATH yet, retrying...') -ForegroundColor DarkYellow
        Start-Sleep -Milliseconds 750
    }
}
if (-not $launched) { throw ("alias '" + $Alias + "' did not resolve on PATH after register") }

# 5) read the probe file from OUTSIDE the process + assert identity ------------------------------------------------
Write-Step '5) read back the probe file + assert IsPackaged=True'
$deadline = (Get-Date).AddSeconds(30)
while (-not (Test-Path $ProbeFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path $ProbeFile)) { throw ('probe file never written: ' + $ProbeFile + ' (did the alias launch under identity?)') }
$body = Get-Content $ProbeFile -Raw
Write-Host '----- pkg-probe.txt -----'
Write-Host $body
Write-Host '-------------------------'

$map = @{}
foreach ($line in ($body -split "`r?`n")) { if ($line -match '^([^=]+)=(.*)$') { $map[$matches[1]] = $matches[2] } }

$isPkg = $map['IsPackaged']
$pfn   = $map['PackageFullName']
if ($isPkg -ne 'True') {
    Write-Host ('FAIL: IsPackaged=' + $isPkg + ' (identity did not attach)') -ForegroundColor Red
    Get-AppxPackage *FluentGpu* | Format-List Name, PackageFullName, InstallLocation
    exit 1
}
if ($pfn -notmatch $ExpectedRx) {
    Write-Host ('FAIL: unexpected PackageFullName ' + $pfn) -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'PASS: packaged identity attached.' -ForegroundColor Green
Write-Host ('  PackageFullName   = ' + $pfn)
Write-Host ('  PackageFamilyName = ' + $map['PackageFamilyName'])
Write-Host ('  AUMID             = ' + $map['ApplicationUserModelId'])
Write-Host ('  Version           = ' + $map['Version'])
Write-Host ('  InstalledLocation = ' + $map['InstalledLocation'])
if (-not $KeepRegistered) {
    Write-Host ''
    Write-Host ('(to undo: Get-AppxPackage -Name ' + $PkgName + ' | Remove-AppxPackage)') -ForegroundColor DarkGray
}
exit 0
